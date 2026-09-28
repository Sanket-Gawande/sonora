using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Sonora {
 // Phones plugged in with USB debugging on, found as they arrive: adb's own track-devices feed,
 // one idle connection to the adb server, no polling. Each phone gets `adb reverse tcp:47211` to a
 // loopback listener of its own, so the Sonora app on it can see this PC, ask it to connect, and
 // end the stream; the listener a request arrives on says which phone sent it.
 // docs/protocol.md, "Finding the PC over USB".
 public sealed class PhoneWatcher : IDisposable {
  public const int Port = 47211;
  const int AdbServerPort = 5037;

  public sealed class Phone {
   public string Serial { get; internal set; }
   public string Model { get; internal set; }
   public bool Emulator { get { return Serial.StartsWith("emulator-"); } }
  }

  // What the session is doing, as the phones are told it.
  public sealed class Status {
   public string State = "offline";   // offline | pairing | streaming | waiting
   public string Serial;              // the phone the session is with
   public string Problem;             // why the last attempt for ProblemSerial failed
   public string ProblemSerial;
   public byte[] Secret;              // the stream's key: only checks proofs, never sent
  }

  // What's playing on this PC, ready to send: one MEDIA line, and the artwork it names.
  sealed class MediaState {
   public string Line = NoMedia;
   public string ArtId, ArtLine;
  }

  public const string NoMedia = "MEDIA none=1";
  static readonly string[] Actions = { "toggle", "play", "pause", "next", "previous", "seek" };

  static readonly Regex Token = new Regex("^[0-9a-f]{32}$"), Proof = new Regex("^[0-9a-f]{64}$");

  readonly string adb;
  readonly string computer;
  readonly object gate = new object();
  readonly Dictionary<string, Presence> presences = new Dictionary<string, Presence>();
  volatile Status status = new Status();
  volatile MediaState media = new MediaState();
  volatile bool running = true;
  volatile Socket feed;
  Thread thread;

  // All raised on the watcher's own threads.
  public event Action PhonesChanged;
  public event Action<string, string> ConnectRequested;        // serial, request token
  public event Action<string, string, string> EndRequested;    // serial, challenge, proof
  public event Action<string, string, long> ControlRequested;  // serial, action, position in ms (seek only)
  // The stream's latest clock sample, for PONG; null while nothing streams. Called on control threads.
  public Func<AudioSender.ClockSample> Clock;

  // This PC's clock for PONG: the performance counter in microseconds, the same clock Windows
  // stamps captured audio with.
  public static long NowMicros() {
   long ticks = System.Diagnostics.Stopwatch.GetTimestamp(), f = System.Diagnostics.Stopwatch.Frequency;
   return ticks / f * 1000000 + ticks % f * 1000000 / f;
  }

  public PhoneWatcher(string adb, string computer) { this.adb = adb; this.computer = computer; }

  public void Start() {
   thread = new Thread(Watch) { IsBackground = true, Name = "Sonora phone watcher" };
   thread.Start();
  }

  public List<Phone> Phones {
   get { lock (gate) { var list = new List<Phone>(); foreach (var p in presences.Values) list.Add(p.Phone); return list; } }
  }

  public bool Has(string serial) { return Find(serial) != null; }

  public Phone Find(string serial) {
   Presence presence;
   lock (gate) return serial != null && presences.TryGetValue(serial, out presence) ? presence.Phone : null;
  }

  public string Adb { get { return adb; } }

  // Tells every phone the session's state; each hears only what differs from the last line it got.
  public void Publish(Status next) {
   status = next;
   List<Presence> all;
   lock (gate) all = new List<Presence>(presences.Values);
   foreach (var presence in all) presence.Push();
  }

  // Now playing, for the phone holding the stream. `artLine` is sent once per `artId`.
  public void PublishMedia(string line, string artId, string artLine) {
   media = new MediaState { Line = line ?? NoMedia, ArtId = artLine == null ? null : artId, ArtLine = artLine };
   List<Presence> all;
   lock (gate) all = new List<Presence>(presences.Values);
   foreach (var presence in all) presence.Push();
  }

  // HMAC-SHA256(K, "sonora-v1-end" ‖ challenge): only the phone holding this stream's key can end it.
  public static bool Verify(byte[] secret, string challenge, string proof) { return Verify(secret, "sonora-v1-end", challenge, proof); }

  static bool Verify(byte[] secret, string label, string challenge, string proof) {
   if (secret == null || challenge == null || proof == null || !Proof.IsMatch(proof)) return false;
   byte[] mac;
   using (var hmac = new HMACSHA256(secret)) mac = hmac.ComputeHash(Encoding.ASCII.GetBytes(label + challenge));
   int diff = 0;
   for (int i = 0; i < 32; i++) diff |= mac[i] ^ Convert.ToByte(proof.Substring(i * 2, 2), 16);
   return diff == 0;
  }

  void Watch() {
   DateTime started = DateTime.MinValue;
   while (running) {
    try {
     using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)) {
      feed = socket;
      socket.Connect(IPAddress.Loopback, AdbServerPort);
      const string request = "host:track-devices";
      socket.Send(Encoding.ASCII.GetBytes(request.Length.ToString("x4") + request));
      if (ReadAscii(socket, 4) != "OKAY") throw new IOException("adb refused track-devices");
      // Each update is the whole device list: [4 hex digits length]["serial\tstate\n"...].
      while (running) {
       int length = Convert.ToInt32(ReadAscii(socket, 4), 16);
       Update(length == 0 ? "" : ReadAscii(socket, length));
      }
     }
    } catch (SocketException) {
    } catch (IOException) {
    } catch (FormatException) {
    } catch (ObjectDisposedException) { }
    feed = null;
    if (!running) return;
    Update("");
    // Nothing has started the adb server since boot: start it, at most once a minute.
    if (DateTime.UtcNow - started > TimeSpan.FromMinutes(1)) { started = DateTime.UtcNow; UsbLink.Run(adb, "start-server"); }
    else Thread.Sleep(5000);
   }
  }

  static string ReadAscii(Socket socket, int count) {
   var buffer = new byte[count];
   int read = 0;
   while (read < count) {
    int n = socket.Receive(buffer, read, count - read, SocketFlags.None);
    if (n == 0) throw new IOException("adb closed the device feed");
    read += n;
   }
   return Encoding.ASCII.GetString(buffer);
  }

  void Update(string devices) {
   var ready = new HashSet<string>();
   foreach (var line in devices.Split('\n')) {
    var parts = line.Trim().Split('\t');
    if (parts.Length == 2 && parts[1] == "device") ready.Add(parts[0]);
   }
   var gone = new List<Presence>(); var added = new List<string>();
   lock (gate) {
    foreach (var pair in presences) if (!ready.Contains(pair.Key)) gone.Add(pair.Value);
    foreach (var presence in gone) presences.Remove(presence.Phone.Serial);
    foreach (var serial in ready) if (!presences.ContainsKey(serial)) added.Add(serial);
   }
   foreach (var presence in gone) presence.Dispose();
   bool changed = gone.Count > 0;
   foreach (var serial in added) {
    var presence = Open(serial);
    if (presence == null) continue;
    lock (gate) { if (running) presences[serial] = presence; }
    if (running) presence.Start(); else presence.Dispose();
    changed = true;
   }
   if (changed) { var handler = PhonesChanged; if (handler != null) handler(); }
  }

  // Self-test only: a phone that's really a loopback socket, with no adb involved. Returns the port.
  internal int AddForTest(string serial, string model) {
   var listener = new TcpListener(IPAddress.Loopback, 0);
   listener.Start();
   var presence = new Presence(this, new Phone { Serial = serial, Model = model }, listener);
   lock (gate) presences[serial] = presence;
   presence.Start();
   return ((IPEndPoint)listener.LocalEndpoint).Port;
  }

  internal static string ProofFor(byte[] secret, string label, string challenge) {
   using (var hmac = new HMACSHA256(secret))
    return BitConverter.ToString(hmac.ComputeHash(Encoding.ASCII.GetBytes(label + challenge))).Replace("-", "").ToLowerInvariant();
  }

  Presence Open(string serial) {
   var listener = new TcpListener(IPAddress.Loopback, 0);
   listener.Start();
   try {
    var link = new UsbLink(adb, serial);
    if (!link.Reverse(Port, ((IPEndPoint)listener.LocalEndpoint).Port)) { listener.Stop(); return null; }
    return new Presence(this, new Phone { Serial = serial, Model = link.ReadModel() }, listener);
   } catch (Exception) { listener.Stop(); return null; }
  }

  // Takes the phones' tunnels down too, so their apps see "no PC" at once instead of reaching
  // through adb to a closed port. Gives adb a second and a half, then quits regardless.
  public void Dispose() {
   running = false;
   var socket = feed;
   if (socket != null) socket.Close();
   List<Presence> all;
   lock (gate) { all = new List<Presence>(presences.Values); presences.Clear(); }
   var removals = new List<Thread>();
   foreach (var presence in all) {
    presence.Dispose();
    if (adb == null) continue;
    string serial = presence.Phone.Serial;
    var removal = new Thread(delegate() { UsbLink.Run(adb, "-s " + serial + " reverse --remove tcp:" + Port, 1500); }) { IsBackground = true };
    removal.Start();
    removals.Add(removal);
   }
   var deadline = DateTime.UtcNow.AddMilliseconds(1500);
   foreach (var removal in removals) removal.Join(TimeSpan.FromMilliseconds(Math.Max(0, (deadline - DateTime.UtcNow).TotalMilliseconds)));
  }

  string Fields(Phone phone, bool hello, string challenge) {
   var s = status;
   bool mine = s.Serial == phone.Serial;
   var text = new StringBuilder();
   if (hello) text.Append("v=1&name=").Append(Uri.EscapeDataString(computer)).Append("&challenge=").Append(challenge).Append('&');
   text.Append("state=").Append(mine ? s.State : "offline").Append("&you=").Append(mine ? '1' : '0');
   if (s.Problem != null && s.ProblemSerial == phone.Serial) text.Append("&problem=").Append(Uri.EscapeDataString(s.Problem));
   if (!mine && s.State != "offline") text.Append("&busy=1");
   return text.ToString();
  }

  // One plugged-in phone: its listener and the Sonora app connections that arrive on it.
  sealed class Presence : IDisposable {
   readonly PhoneWatcher owner;
   readonly TcpListener listener;
   readonly List<Control> controls = new List<Control>();
   public readonly Phone Phone;

   public Presence(PhoneWatcher owner, Phone phone, TcpListener listener) { this.owner = owner; Phone = phone; this.listener = listener; }

   public void Start() { new Thread(Accept) { IsBackground = true, Name = "Sonora presence " + Phone.Serial }.Start(); }

   void Accept() {
    while (true) {
     TcpClient client;
     try { client = listener.AcceptTcpClient(); }
     catch (SocketException) { return; }
     catch (ObjectDisposedException) { return; }
     var control = new Control(owner, this, client);
     lock (controls) controls.Add(control);
     new Thread(control.Run) { IsBackground = true, Name = "Sonora control " + Phone.Serial }.Start();
    }
   }

   public void Remove(Control control) { lock (controls) controls.Remove(control); }

   public void Push() {
    List<Control> all;
    lock (controls) all = new List<Control>(controls);
    foreach (var control in all) control.Push();
   }

   public void Dispose() {
    listener.Stop();
    List<Control> all;
    lock (controls) { all = new List<Control>(controls); controls.Clear(); }
    foreach (var control in all) control.Close();
   }
  }

  // One connection from the Sonora app: HELLO, then state lines out and CONNECT / END in. Once the
  // app proves it holds the stream's key (AUTH), it also hears what's playing and may control it.
  sealed class Control {
   readonly PhoneWatcher owner;
   readonly Presence presence;
   readonly TcpClient client;
   readonly NetworkStream stream;
   readonly string challenge;
   readonly object writing = new object();
   string last, lastMedia = NoMedia, sentArt, authProof;
   long lastPing;
   byte[] verifiedSecret;
   bool greeted;

   public Control(PhoneWatcher owner, Presence presence, TcpClient client) {
    this.owner = owner; this.presence = presence; this.client = client;
    client.NoDelay = true;
    client.SendTimeout = 1000;
    stream = client.GetStream();
    var bytes = new byte[16];
    using (var rng = new RNGCryptoServiceProvider()) rng.GetBytes(bytes);
    challenge = BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
   }

   public void Run() {
    try {
     client.ReceiveTimeout = 5000;
     var input = new BufferedStream(stream);
     string hello = ReadLine(input);
     if (hello == null || !hello.StartsWith("HELLO ") || Value(hello, "v") != "1") return;
     client.ReceiveTimeout = 0;
     lock (writing) { greeted = true; Write("SONORA " + owner.Fields(presence.Phone, true, challenge)); last = owner.Fields(presence.Phone, false, null); }
     string line;
     while ((line = ReadLine(input)) != null) {
      if (line.StartsWith("CONNECT ")) {
       string token = Value(line, "request");
       var handler = owner.ConnectRequested;
       if (token != null && Token.IsMatch(token) && handler != null) handler(presence.Phone.Serial, token);
      } else if (line.StartsWith("END ")) {
       string proof = Value(line, "proof");
       var handler = owner.EndRequested;
       if (proof != null && handler != null) handler(presence.Phone.Serial, challenge, proof);
      } else if (line.StartsWith("AUTH ")) {
       string proof = Value(line, "proof");
       if (proof != null && Proof.IsMatch(proof)) { lock (writing) { authProof = proof; verifiedSecret = null; } Push(); }
      } else if (line.StartsWith("CONTROL ")) {
       Command(Value(line, "action"), Value(line, "position"));
      } else if (line.StartsWith("PING ")) {
       Pong(Value(line, "t"));
      }
     }
    } catch (IOException) {
    } catch (ObjectDisposedException) {
    } finally {
     presence.Remove(this);
     Close();
    }
   }

   public void Push() {
    lock (writing) {
     if (!greeted) return;
     try {
      string fields = owner.Fields(presence.Phone, false, null);
      if (fields != last) { last = fields; Write("STATE " + fields); }
      // What's playing goes only to the app holding this stream's key; when that lapses (the
      // stream ended or moved), it hears "none".
      var now = owner.media;
      bool authorized = Authorized();
      if (authorized && now.ArtId != null && now.ArtId != sentArt) { sentArt = now.ArtId; Write(now.ArtLine); }
      string line = authorized ? now.Line : NoMedia;
      if (line != lastMedia) { lastMedia = line; Write(line); }
     } catch (IOException) {
      Close();
     } catch (ObjectDisposedException) { }
    }
   }

   // Called with `writing` held. The proof is checked again whenever the stream's key changes.
   bool Authorized() {
    var s = owner.status;
    if (authProof == null || s.Secret == null || s.Serial != presence.Phone.Serial) return false;
    if (s.Secret == verifiedSecret) return true;
    if (!Verify(s.Secret, "sonora-v1-auth", challenge, authProof)) return false;
    verifiedSecret = s.Secret;
    return true;
   }

   // Clock sync for the latency reading: the phone's time echoed with this PC's, stamped as soon as
   // the line is read, plus the stream's latest clock sample. Only for the app holding the stream,
   // at most 20 a second.
   void Pong(string t) {
    long now = NowMicros(), sent;
    if (t == null || !long.TryParse(t, out sent)) return;
    lock (writing) {
     if (!greeted || !Authorized() || now - lastPing < 50000) return;
     lastPing = now;
     var source = owner.Clock;
     var sample = source == null ? null : source();
     try { Write("PONG t=" + sent + "&time=" + now + (sample == null ? "" : "&frame=" + sample.Frame + "&at=" + sample.Micros)); }
     catch (IOException) { Close(); }
     catch (ObjectDisposedException) { }
    }
   }

   void Command(string action, string position) {
    if (action == null || Array.IndexOf(Actions, action) < 0) return;
    long ms = -1;
    if (action == "seek" && (position == null || !long.TryParse(position, out ms) || ms < 0)) return;
    bool authorized;
    lock (writing) authorized = Authorized();
    var handler = owner.ControlRequested;
    if (authorized && handler != null) handler(presence.Phone.Serial, action, ms);
   }

   void Write(string line) {
    var bytes = Encoding.UTF8.GetBytes(line + "\n");
    stream.Write(bytes, 0, bytes.Length);
   }

   public void Close() { client.Close(); }

   // Lines are short; anything over 1 KB isn't from Sonora.
   static string ReadLine(Stream input) {
    var bytes = new List<byte>(96);
    while (true) {
     int b = input.ReadByte();
     if (b < 0) return null;
     if (b == '\n') return Encoding.UTF8.GetString(bytes.ToArray()).TrimEnd('\r');
     if (bytes.Count >= 1024) return null;
     bytes.Add((byte)b);
    }
   }

   static string Value(string line, string key) {
    int space = line.IndexOf(' ');
    foreach (var pair in line.Substring(space + 1).Split('&')) {
     int eq = pair.IndexOf('=');
     if (eq > 0 && pair.Substring(0, eq) == key) return Uri.UnescapeDataString(pair.Substring(eq + 1));
    }
    return null;
   }
  }
 }
}
