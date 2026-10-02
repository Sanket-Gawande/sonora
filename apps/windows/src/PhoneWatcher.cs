using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Sonora {
 // Phones plugged in with USB debugging on, found as they arrive: adb's own track-devices feed,
 // one idle connection to the adb server, no polling. Each phone gets `adb reverse tcp:47211` to a
 // loopback listener of its own, so the Sonora app on it can see this PC, ask it to connect, and
 // end the stream; the listener a request arrives on says which phone sent it. Phones on this
 // Wi-Fi reach the same channel on port 47211 of every interface, after finding the PC with a UDP
 // query on 47212, and pair or join with keys of their own. docs/protocol.md, "Finding the PC over
 // USB" and "Wi-Fi".
 public sealed class PhoneWatcher : IDisposable {
  public const int Port = 47211, DiscoveryPort = 47212;
  // The app asks by broadcast and on this multicast group too: some routers drop broadcasts between
  // Wi-Fi devices but pass multicast to devices that joined the group.
  public static readonly IPAddress DiscoveryGroup = IPAddress.Parse("239.255.47.212");
  const int AdbServerPort = 5037, MaxLanConnections = 16, MaxPerAddress = 4;

  public sealed class Phone {
   public string Serial { get; internal set; }
   public string Model { get; internal set; }
   public bool Emulator { get { return Serial.StartsWith("emulator-"); } }
   // adb over Wi-Fi: "192.168.1.5:40397", or the name wireless debugging advertises.
   public bool Wireless { get { return Serial.Contains(":") || Serial.Contains("._adb-tls-connect."); } }
  }

  // What the session is doing, as the phones are told it.
  public sealed class Status {
   public string State = "offline";   // offline | pairing | streaming | waiting
   public string Serial;              // the phone the session is with
   public string Problem;             // why the last attempt for ProblemSerial failed
   public string ProblemSerial;
   public byte[] Secret;              // the stream's key: only checks proofs, never sent
   public bool PcMuted;               // the PC's speakers are muted (told to the stream's phone)
  }

  // What's playing on this PC, ready to send: one MEDIA line, and the artwork it names.
  sealed class MediaState {
   public string Line = NoMedia;
   public string ArtId, ArtLine;
  }

  public const string NoMedia = "MEDIA none=1";
  static readonly string[] Actions = { "toggle", "play", "pause", "next", "previous", "seek", "mute", "unmute" };

  static readonly Regex Token = new Regex("^[0-9a-f]{32}$"), Proof = new Regex("^[0-9a-f]{64}$"), RequestId = new Regex("^[0-9a-f]{8}$"), PhoneId = new Regex("^[0-9a-f]{16}$");

  string adb;
  readonly string computer, pcId;
  readonly object gate = new object();
  readonly Dictionary<string, Presence> presences = new Dictionary<string, Presence>();
  // When each phone's app, holding the stream's key, was last heard from (a Wi-Fi stream's pulse).
  readonly Dictionary<string, DateTime> heard = new Dictionary<string, DateTime>();
  Presence lan;
  UdpClient discovery;
  bool watchingAddresses;
  // After a pairing is declined (or left unanswered), that address can't ask again for a while, so
  // nobody on the network can keep the notch popping up.
  public TimeSpan PairCooldown = TimeSpan.FromSeconds(30);
  readonly Dictionary<IPAddress, DateTime> cooling = new Dictionary<IPAddress, DateTime>();
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
  // A phone on Wi-Fi asks to pair: serial, model, the number to compare, the agreed key, and the
  // answer to call once (true to allow). Then a paired phone joins: serial, model, where to send
  // the stream, and its key.
  public event Action<string, string, string, byte[], Action<bool>> PairRequested;
  public event Action<string, string, IPEndPoint, byte[]> JoinRequested;
  // The stream's phone reports its media volume, 0..100 (0: nothing will come out of it).
  public event Action<string, int> PhoneVolume;
  // A paired phone's long-term key by its ID, or null.
  public Func<string, byte[]> WifiKey;
  // The stream's latest clock sample, for PONG; null while nothing streams. Called on control threads.
  public Func<AudioSender.ClockSample> Clock;
  // The playing tab's link, for LINK; null when there's none. Slow (it asks the browser), so it's
  // called on a thread of its own.
  public Func<string> Link;

  // This PC's clock for PONG: the performance counter in microseconds, the same clock Windows
  // stamps captured audio with.
  public static long NowMicros() {
   long ticks = System.Diagnostics.Stopwatch.GetTimestamp(), f = System.Diagnostics.Stopwatch.Frequency;
   return ticks / f * 1000000 + ticks % f * 1000000 / f;
  }

  public PhoneWatcher(string adb, string computer, string pcId) { this.adb = adb; this.computer = computer; this.pcId = pcId; }

  // Phones on USB; without adb there are none to watch.
  public void Start() {
   if (adb == null) return;
   thread = new Thread(Watch) { IsBackground = true, Name = "Sonora phone watcher" };
   thread.Start();
  }

  // Phones on this Wi-Fi. The first time, Windows asks whether Sonora may accept connections.
  // False when the ports are taken (another copy of Sonora, or another app).
  public bool StartLan(int port, int discoveryPort) {
   TcpListener listener = null;
   try {
    listener = new TcpListener(IPAddress.Any, port);
    Native.Own(listener.Server);
    listener.Start();
    var udp = new UdpClient(new IPEndPoint(IPAddress.Any, discoveryPort));
    Native.Own(udp.Client);
    // A reply to a phone that has gone would otherwise make the next receive fail.
    try { udp.Client.IOControl(-1744830452 /* SIO_UDP_CONNRESET */, new byte[] { 0 }, null); } catch (SocketException) { }
    discovery = udp;
    JoinGroup();
    if (!watchingAddresses) { watchingAddresses = true; NetworkChange.NetworkAddressChanged += delegate { JoinGroup(); }; }
   } catch (SocketException) {
    if (listener != null) listener.Stop();
    return false;
   }
   lan = new Presence(this, null, listener);
   lan.Start();
   new Thread(Answer) { IsBackground = true, Name = "Sonora discovery" }.Start();
   return true;
  }

  // Phones on the Wi-Fi can't find or reach this PC any more; open connections close.
  public void StopLan() {
   var l = lan; var d = discovery;
   lan = null; discovery = null;
   if (l != null) l.Dispose();
   if (d != null) d.Close();
  }

  bool CoolingDown(IPAddress address) { DateTime until; lock (cooling) return cooling.TryGetValue(address, out until) && DateTime.UtcNow < until; }
  void Cool(IPAddress address) { lock (cooling) cooling[address] = DateTime.UtcNow + PairCooldown; }

  public int LanPort { get { var l = lan; return l == null ? 0 : l.Port; } }
  public int AnswerPort { get { var d = discovery; return d == null ? 0 : ((IPEndPoint)d.Client.LocalEndPoint).Port; } }

  // "SONORA? v=1" from the app, broadcast on the Wi-Fi: this PC's name, ID and port, straight back.
  // On every IPv4 interface, and again whenever the addresses change (a new Wi-Fi, a cable).
  void JoinGroup() {
   var udp = discovery;
   if (udp == null) return;
   foreach (var nic in NetworkInterface.GetAllNetworkInterfaces()) {
    if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
    foreach (var address in nic.GetIPProperties().UnicastAddresses) {
     if (address.Address.AddressFamily != AddressFamily.InterNetwork) continue;
     try { udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(DiscoveryGroup, address.Address)); }
     catch (SocketException) { } // already a member there
     catch (ObjectDisposedException) { return; }
    }
   }
  }

  void Answer() {
   var udp = discovery;
   while (running) {
    var from = new IPEndPoint(IPAddress.Any, 0);
    byte[] data;
    try { data = udp.Receive(ref from); }
    catch (SocketException) { if (!running) return; Thread.Sleep(100); continue; }
    catch (ObjectDisposedException) { return; }
    if (data.Length > 256) continue;
    string query = Encoding.UTF8.GetString(data);
    if (!query.StartsWith("SONORA? ") || Control.Value(query, "v") != "1") continue;
    var reply = Encoding.UTF8.GetBytes("SONORA! v=1&name=" + Uri.EscapeDataString(computer) + "&id=" + pcId + "&port=" + LanPort);
    try { udp.Send(reply, reply.Length, from); } catch (SocketException) { } catch (ObjectDisposedException) { return; }
   }
  }

  public static string WifiSerial(string phoneId) { return "wifi-" + phoneId; }
  public static string WifiId(string serial) { return serial != null && serial.StartsWith("wifi-") ? serial.Substring(5) : null; }

  void Heard(string serial) { lock (heard) heard[serial] = DateTime.UtcNow; }

  // How long since the app on that phone, holding the stream's key, was last heard from.
  public TimeSpan SinceHeard(string serial) {
   DateTime at;
   lock (heard) return serial != null && heard.TryGetValue(serial, out at) ? DateTime.UtcNow - at : TimeSpan.MaxValue;
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

  // adb turned up after Sonora started (platform-tools unzipped beside it): start watching USB.
  public void UseAdb(string found) {
   if (adb != null || found == null) return;
   adb = found;
   Start();
  }

  // Tells every phone the session's state; each hears only what differs from the last line it got.
  public void Publish(Status next) {
   status = next;
   foreach (var presence in All()) presence.Push();
  }

  List<Presence> All() {
   List<Presence> all;
   lock (gate) all = new List<Presence>(presences.Values);
   var l = lan;
   if (l != null) all.Add(l);
   return all;
  }

  // Now playing, for the phone holding the stream. `artLine` is sent once per `artId`.
  public void PublishMedia(string line, string artId, string artLine) {
   media = new MediaState { Line = line ?? NoMedia, ArtId = artLine == null ? null : artId, ArtLine = artLine };
   foreach (var presence in All()) presence.Push();
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
     using (var socket = Native.Own(new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))) {
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
   Native.Own(listener.Server);
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
   Native.Own(listener.Server);
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
   if (lan != null) lan.Dispose();
   if (discovery != null) discovery.Close();
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
   if (hello) {
    text.Append("v=1&name=").Append(Uri.EscapeDataString(computer)).Append("&challenge=").Append(challenge).Append('&');
    if (pcId != null) text.Append("id=").Append(pcId).Append('&');
   }
   text.Append("state=").Append(mine ? s.State : "offline").Append("&you=").Append(mine ? '1' : '0');
   if (s.Problem != null && s.ProblemSerial == phone.Serial) text.Append("&problem=").Append(Uri.EscapeDataString(s.Problem));
   if (!mine && s.State != "offline") text.Append("&busy=1");
   if (mine && s.PcMuted) text.Append("&pcmuted=1");
   return text.ToString();
  }

  // One plugged-in phone: its listener and the Sonora app connections that arrive on it.
  sealed class Presence : IDisposable {
   readonly PhoneWatcher owner;
   readonly TcpListener listener;
   readonly List<Control> controls = new List<Control>();
   public readonly Phone Phone;

   // `phone` is null for the Wi-Fi listener, whose connections each say who they are.
   public Presence(PhoneWatcher owner, Phone phone, TcpListener listener) { this.owner = owner; Phone = phone; this.listener = listener; }

   public bool Lan { get { return Phone == null; } }
   public int Port { get { return ((IPEndPoint)listener.LocalEndpoint).Port; } }

   public void Start() { new Thread(Accept) { IsBackground = true, Name = "Sonora presence " + (Lan ? "Wi-Fi" : Phone.Serial) }.Start(); }

   void Accept() {
    while (true) {
     TcpClient client;
     try { client = listener.AcceptTcpClient(); Native.Own(client.Client); }
     catch (SocketException) { return; }
     catch (ObjectDisposedException) { return; }
     Control control;
     lock (controls) {
      if (Lan && (controls.Count >= MaxLanConnections || controls.FindAll(c => c.Remote.Equals(((IPEndPoint)client.Client.RemoteEndPoint).Address)).Count >= MaxPerAddress)) { client.Close(); continue; }
      control = new Control(owner, this, client);
      controls.Add(control);
     }
     new Thread(control.Run) { IsBackground = true, Name = "Sonora control " + (Lan ? "Wi-Fi" : Phone.Serial) }.Start();
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
   readonly bool lan;
   readonly IPAddress remote;
   public IPAddress Remote { get { return remote; } }
   // Over USB the listener says which phone this is; over Wi-Fi its HELLO does.
   Phone phone;
   string phoneId;
   string last, lastMedia = NoMedia, sentArt, authProof;
   long lastPing;
   byte[] verifiedSecret;
   bool greeted, linking, pairing;
   int pairings;

   public Control(PhoneWatcher owner, Presence presence, TcpClient client) {
    this.owner = owner; this.presence = presence; this.client = client;
    phone = presence.Phone;
    lan = presence.Lan;
    remote = ((IPEndPoint)client.Client.RemoteEndPoint).Address;
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
     if (lan) {
      // A phone on Wi-Fi names itself: its app's own random ID, and its model for the notch.
      phoneId = Value(hello, "id");
      if (phoneId == null || !PhoneId.IsMatch(phoneId)) return;
      string model = Value(hello, "model");
      if (string.IsNullOrEmpty(model)) model = "Phone";
      phone = new Phone { Serial = WifiSerial(phoneId), Model = model.Length > 60 ? model.Substring(0, 60) : model };
     }
     client.ReceiveTimeout = 0;
     lock (writing) { greeted = true; Write("SONORA " + owner.Fields(phone, true, challenge)); last = owner.Fields(phone, false, null); }
     string line;
     while ((line = ReadLine(input)) != null) {
      if (line.StartsWith("CONNECT ")) {
       // Over USB only: the PC answers by opening the app over adb, which Wi-Fi can't reach.
       string token = Value(line, "request");
       var handler = owner.ConnectRequested;
       if (!lan && token != null && Token.IsMatch(token) && handler != null) handler(phone.Serial, token);
      } else if (line.StartsWith("PAIR ")) {
       Pair(Value(line, "key"));
      } else if (line.StartsWith("JOIN ")) {
       Join(Value(line, "port"), Value(line, "nonce"), Value(line, "proof"));
      } else if (line.StartsWith("END ")) {
       string proof = Value(line, "proof");
       var handler = owner.EndRequested;
       if (proof != null && handler != null) handler(phone.Serial, challenge, proof);
      } else if (line.StartsWith("AUTH ")) {
       string proof = Value(line, "proof");
       if (proof != null && Proof.IsMatch(proof)) { lock (writing) { authProof = proof; verifiedSecret = null; } Push(); }
      } else if (line.StartsWith("CONTROL ")) {
       Command(Value(line, "action"), Value(line, "position"));
      } else if (line.StartsWith("PING ")) {
       Pong(Value(line, "t"));
      } else if (line.StartsWith("VOLUME ")) {
       Volume(Value(line, "level"));
      } else if (line.StartsWith("LINK ")) {
       Link(Value(line, "request"));
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
      string fields = owner.Fields(phone, false, null);
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
    if (authProof == null || s.Secret == null || s.Serial != phone.Serial) return false;
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
     owner.Heard(phone.Serial);
     var source = owner.Clock;
     var sample = source == null ? null : source();
     try { Write("PONG t=" + sent + "&time=" + now + (sample == null ? "" : "&frame=" + sample.Frame + "&at=" + sample.Micros)); }
     catch (IOException) { Close(); }
     catch (ObjectDisposedException) { }
    }
   }

   // The link to what's playing, so the phone can open it itself: one request at a time, answered
   // with its request ID, and only to the app holding the stream.
   void Link(string request) {
    if (request == null || !RequestId.IsMatch(request)) return;
    lock (writing) {
     if (!greeted || linking || !Authorized()) return;
     linking = true;
    }
    new Thread(delegate() {
     string url = null;
     try { var source = owner.Link; if (source != null) url = source(); } catch (Exception) { }
     lock (writing) {
      linking = false;
      if (!Authorized()) return;
      try { Write("LINK request=" + request + (url == null ? "&none=1" : "&url=" + Uri.EscapeDataString(url))); }
      catch (IOException) { Close(); }
      catch (ObjectDisposedException) { }
     }
    }) { IsBackground = true, Name = "Sonora link " + phone.Serial }.Start();
   }

   // A phone on Wi-Fi asks to pair. This PC's half of the key agreement goes back at once, so the
   // phone can show the number; the answer follows when the user allows or declines the phone on
   // the PC. A few tries per connection at most.
   void Pair(string key) {
    if (!lan || !greeted || pairing || pairings >= 3 || owner.CoolingDown(remote)) return;
    byte[] phonePublic = WifiTrust.FromBase64Url(key), pcPublic, longTerm;
    try { longTerm = WifiTrust.Agree(phonePublic, out pcPublic); }
    catch (CryptographicException) { return; }
    lock (writing) {
     pairing = true;
     pairings++;
     try { Write("PAIRING key=" + WifiTrust.Base64Url(pcPublic)); }
     catch (IOException) { Close(); return; }
     catch (ObjectDisposedException) { return; }
    }
    bool answered = false;
    Action<bool> answer = delegate(bool allowed) {
     lock (writing) {
      if (answered) return;
      answered = true;
      pairing = false;
      if (!allowed) owner.Cool(remote);
      try { Write("PAIRED ok=" + (allowed ? "1" : "0")); }
      catch (IOException) { Close(); }
      catch (ObjectDisposedException) { }
     }
    };
    var handler = owner.PairRequested;
    if (handler == null) answer(false);
    else handler(phone.Serial, phone.Model, WifiTrust.Code(longTerm), longTerm, answer);
   }

   // A paired phone on Wi-Fi starts a stream: it proves it holds the pairing's key for this
   // connection's challenge, and both sides derive the stream's key; neither key crosses the network.
   void Join(string port, string nonce, string proof) {
    int udp;
    if (!lan || !greeted || !int.TryParse(port, out udp) || udp < 1024 || udp > 65535) return;
    if (nonce == null || !Token.IsMatch(nonce) || proof == null || !Proof.IsMatch(proof)) return;
    var keys = owner.WifiKey;
    byte[] longTerm = keys == null ? null : keys(phoneId);
    bool ok = longTerm != null && WifiTrust.CheckJoin(longTerm, challenge, nonce, udp, proof);
    lock (writing) {
     try { Write(ok ? "JOINED ok=1&proof=" + WifiTrust.JoinedProof(longTerm, challenge, nonce) : "JOINED ok=0"); }
     catch (IOException) { Close(); return; }
     catch (ObjectDisposedException) { return; }
    }
    if (!ok) return;
    owner.Heard(phone.Serial);
    var handler = owner.JoinRequested;
    if (handler != null) handler(phone.Serial, phone.Model, new IPEndPoint(remote, udp), WifiTrust.StreamKey(longTerm, challenge, nonce));
   }

   void Volume(string level) {
    int value;
    if (level == null || !int.TryParse(level, out value) || value < 0 || value > 100) return;
    bool authorized;
    lock (writing) authorized = Authorized();
    var handler = owner.PhoneVolume;
    if (authorized && handler != null) handler(phone.Serial, value);
   }

   void Command(string action, string position) {
    if (action == null || Array.IndexOf(Actions, action) < 0) return;
    long ms = -1;
    if (action == "seek" && (position == null || !long.TryParse(position, out ms) || ms < 0)) return;
    bool authorized;
    lock (writing) authorized = Authorized();
    var handler = owner.ControlRequested;
    if (authorized && handler != null) handler(phone.Serial, action, ms);
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

   internal static string Value(string line, string key) {
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
