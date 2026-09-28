using System;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Sonora {
 public enum LinkState { Offline, Pairing, Streaming, Reconnecting }

 // The connection to one phone over USB: find it with adb, hand over the pairing link, then
 // stream real desktop audio once the phone confirms the number. The stream carries whatever
 // the PC plays; pausing is done on the media itself, not on the stream. The phone can also
 // start and end it: PhoneWatcher shows this PC to the Sonora app on every plugged-in phone.
 public sealed class Session {
  readonly Dispatcher dispatcher;
  readonly object tunnelGate = new object();
  UsbLink usb;
  TcpServerSink sink;
  AudioSender sender;
  // The same sender, for the phone watcher's threads.
  volatile AudioSender liveSender;
  byte[] secret;
  PhoneWatcher phones;
  string problemSerial;
  // The phone whose stream tunnel (47210) should exist; a late removal must not undo a newer one.
  string tunnelSerial;
  bool sessionPhonePresent;
  BitmapSource art;
  string artId, artLine;

  public event EventHandler Changed;
  // The streaming phone's media buttons: "toggle", "play", "pause", "next", "previous", or "seek" with a position.
  public event Action<string, TimeSpan> MediaControl;
  public LinkState State { get; private set; }
  public string DeviceName { get; private set; }
  public bool Muted { get; private set; }
  public double Volume { get; private set; }
  public string PairingCode { get; private set; }
  // True while adb is finding the phone and setting up the tunnel.
  public bool Busy { get; private set; }
  // Why the last attempt failed, in words for the user.
  public string Problem { get; private set; }
  // A plugged-in phone with USB debugging allowed, ready to connect (real phones before emulators).
  public string PluggedPhone { get; private set; }
  public bool IsActive { get { return State == LinkState.Streaming; } }

  // The name phones show for this PC: the host name as the user typed it, not the upper-case NetBIOS one.
  public static string ComputerName {
   get {
    try { string name = Dns.GetHostName(); if (!string.IsNullOrEmpty(name)) return name; }
    catch (System.Net.Sockets.SocketException) { }
    return Environment.MachineName;
   }
  }

  public Session(double volume) {
   dispatcher = Dispatcher.CurrentDispatcher;
   Volume = volume;
  }

  // Starts watching for phones as they're plugged in. Without adb there's nothing to watch, and
  // the notch says what's missing.
  public void Watch() {
   if (phones != null) return;
   string adb = UsbLink.FindAdb();
   if (adb == null) { Problem = UsbLink.NoAdb; Raise(); return; }
   phones = new PhoneWatcher(adb, ComputerName);
   phones.PhonesChanged += delegate { dispatcher.BeginInvoke(new Action(OnPhonesChanged)); };
   phones.ConnectRequested += delegate(string serial, string request) { dispatcher.BeginInvoke(new Action(delegate { ConnectFromPhone(serial, request); })); };
   phones.EndRequested += delegate(string serial, string challenge, string proof) { dispatcher.BeginInvoke(new Action(delegate { EndFromPhone(serial, challenge, proof); })); };
   phones.ControlRequested += delegate(string serial, string action, long ms) {
    dispatcher.BeginInvoke(new Action(delegate {
     // The watcher checked the key; the stream must still be with that phone.
     var handler = MediaControl;
     if (usb != null && usb.Serial == serial && handler != null) handler(action, TimeSpan.FromMilliseconds(Math.Max(0, ms)));
    }));
   };
   phones.Clock = delegate { var live = liveSender; return live == null ? null : live.Clock; };
   phones.Start();
  }

  public void StopWatching() { if (phones != null) phones.Dispose(); }

  public void ConnectUsb() { ConnectUsb(null, null); }

  // `serial` and `request` are set when the Sonora app on that phone asked to connect.
  void ConnectUsb(string serial, string request) {
   if (Busy || State != LinkState.Offline) return;
   // platform-tools may have been added since Sonora started.
   Watch();
   Busy = true; Problem = null; problemSerial = serial; Raise();
   var known = phones != null ? phones.Find(serial) : null;
   ThreadPool.QueueUserWorkItem(delegate {
    string problem = null;
    UsbLink link = null; TcpServerSink newSink = null; PairingOffer offer = null;
    try {
     // The app on a watched phone asked, so it's there and installed: skip straight to the tunnel.
     if (known != null) link = new UsbLink(phones.Adb, known.Serial, known.Model);
     else {
      link = UsbLink.Find(serial, out problem);
      if (link != null && !link.IsAppInstalled()) problem = "Install Sonora on " + link.Model + " first.";
     }
     if (problem == null) {
      newSink = new TcpServerSink(UsbLink.Port);
      bool tunnel;
      lock (tunnelGate) { tunnelSerial = link.Serial; tunnel = link.Reverse(UsbLink.Port); }
      if (!tunnel) problem = "Couldn't open a USB tunnel to the phone.";
     }
     if (problem == null) {
      offer = new PairingOffer("127.0.0.1", UsbLink.Port, ComputerName);
      if (!link.DeliverOffer(offer, request)) problem = "Couldn't open Sonora on the phone.";
     }
    } catch (Exception error) {
     problem = error is System.Net.Sockets.SocketException ? "Port " + UsbLink.Port + " is in use by another app." : error.Message;
    }
    dispatcher.BeginInvoke(new Action(delegate {
     Busy = false;
     if (problem != null) {
      if (newSink != null) newSink.Dispose();
      Problem = problem;
      if (link != null) problemSerial = link.Serial;
      Raise();
      return;
     }
     Start(link, newSink, offer);
    }));
   });
  }

  // The Sonora app on a plugged-in phone asked for the stream. It moves to that phone, even from
  // another one; the app asked, so it skips the number (the link still only travels over adb).
  void ConnectFromPhone(string serial, string request) {
   if (Busy) return;
   if (State != LinkState.Offline) { Stop(); State = LinkState.Offline; }
   ConnectUsb(serial, request);
  }

  // The phone's Disconnect: honoured only with proof it holds this stream's key.
  void EndFromPhone(string serial, string challenge, string proof) {
   if (usb == null || usb.Serial != serial || !PhoneWatcher.Verify(secret, challenge, proof)) return;
   Disconnect();
  }

  // Tells the streaming phone what's playing (docs/protocol.md). The artwork is re-encoded only
  // when the track's picture changes, and sent only when its bytes do.
  public void PublishMedia(NowPlaying now) {
   if (phones == null) return;
   if (now == null) { phones.PublishMedia(PhoneWatcher.NoMedia, null, null); return; }
   if (!ReferenceEquals(now.Art, art)) { art = now.Art; EncodeArt(now.ArtBytes, art); }
   var line = new StringBuilder("MEDIA title=").Append(Uri.EscapeDataString(Clip(now.Title)));
   line.Append("&artist=").Append(Uri.EscapeDataString(Clip(now.Artist)));
   line.Append("&app=").Append(Uri.EscapeDataString(Clip(now.App)));
   line.Append("&playing=").Append(now.Playing ? '1' : '0');
   line.Append("&toggle=").Append(now.CanToggle ? '1' : '0');
   line.Append("&previous=").Append(now.CanPrevious ? '1' : '0');
   line.Append("&next=").Append(now.CanNext ? '1' : '0');
   line.Append("&seek=").Append(now.CanSeek && now.Duration > TimeSpan.Zero ? '1' : '0');
   line.Append("&position=").Append((long)Math.Max(0, now.PositionNow.TotalMilliseconds));
   line.Append("&duration=").Append((long)Math.Max(0, now.Duration.TotalMilliseconds));
   if (artId != null) line.Append("&art=").Append(artId);
   phones.PublishMedia(line.ToString(), artId, artLine);
  }

  static string Clip(string text) { text = text ?? ""; return text.Length > 200 ? text.Substring(0, 200) : text; }

  // A JPEG for the phone's full-width cover: from the app's own picture, at most 480 px on its
  // longer side (the notch's 160 px copy only when that can't be read), named by a hash of its bytes.
  void EncodeArt(byte[] original, BitmapSource small) {
   artId = null; artLine = null;
   byte[] bytes = PhoneCover(original, small);
   if (bytes == null) return;
   try {
    byte[] hash;
    using (var sha = SHA256.Create()) hash = sha.ComputeHash(bytes);
    artId = BitConverter.ToString(hash, 0, 8).Replace("-", "").ToLowerInvariant();
    artLine = "ART id=" + artId + "&jpeg=" + Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
   } catch (Exception) {
    artId = null; artLine = null;
   }
  }

  // The JPEG the phone gets, or null when there's no picture that fits in 160 KB.
  internal static byte[] PhoneCover(byte[] original, BitmapSource small) {
   byte[] bytes = null;
   if (original != null) { try { bytes = Jpeg(Sharp(original)); } catch (Exception) { bytes = null; } }
   if ((bytes == null || bytes.Length > 160 * 1024) && small != null) { try { bytes = Jpeg(small); } catch (Exception) { bytes = null; } }
   return bytes == null || bytes.Length == 0 || bytes.Length > 160 * 1024 ? null : bytes;
  }

  static BitmapSource Sharp(byte[] original) {
   var decoded = BitmapFrame.Create(new MemoryStream(original), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
   double scale = Math.Min(1.0, 480.0 / Math.Max(decoded.PixelWidth, decoded.PixelHeight));
   if (scale >= 1.0) return decoded;
   var smaller = new TransformedBitmap(decoded, new System.Windows.Media.ScaleTransform(scale, scale));
   smaller.Freeze();
   return smaller;
  }

  static byte[] Jpeg(BitmapSource source) {
   // High quality: covers are often small already (browsers send 120 px), and a second lossy pass shows.
   var encoder = new JpegBitmapEncoder { QualityLevel = 92 };
   encoder.Frames.Add(BitmapFrame.Create(source));
   using (var memory = new MemoryStream()) { encoder.Save(memory); return memory.ToArray(); }
  }

  void OnPhonesChanged() {
   PhoneWatcher.Phone ready = null;
   foreach (var phone in phones.Phones) if (ready == null || (ready.Emulator && !phone.Emulator)) ready = phone;
   PluggedPhone = ready == null ? null : ready.Model;
   // adb drops a phone's tunnels when its cable is pulled; put the stream's back when it returns.
   bool present = usb != null && phones.Has(usb.Serial);
   if (present && !sessionPhonePresent && State != LinkState.Offline) {
    var link = usb;
    ThreadPool.QueueUserWorkItem(delegate { lock (tunnelGate) { if (tunnelSerial == link.Serial) link.Reverse(UsbLink.Port); } });
   }
   sessionPhonePresent = present;
   Raise();
  }

  void Start(UsbLink link, TcpServerSink newSink, PairingOffer offer) {
   usb = link; sink = newSink; secret = offer.Secret;
   sessionPhonePresent = phones != null && phones.Has(link.Serial);
   DeviceName = link.Model;
   PairingCode = offer.Number;
   sink.ConnectionChanged += delegate(bool connected) { dispatcher.BeginInvoke(new Action(delegate { OnPhoneConnection(connected); })); };
   sender = new AudioSender(offer.Secret, sink);
   ApplyGain();
   liveSender = sender;
   try { sender.Start(); }
   catch (Exception error) { Stop(); Problem = "Couldn't capture desktop audio: " + error.Message; Set(LinkState.Offline); return; }
   Set(LinkState.Pairing);
  }

  // The phone only connects after the user confirms the number, so connecting means paired.
  void OnPhoneConnection(bool connected) {
   if (State == LinkState.Offline) return;
   if (connected) Set(LinkState.Streaming);
   else if (IsActive) Set(LinkState.Reconnecting);
  }

  void Stop() {
   var oldSender = sender; var oldSink = sink; var oldLink = usb;
   sender = null; sink = null; usb = null; secret = null; liveSender = null;
   if (oldSender != null) oldSender.Dispose(); else if (oldSink != null) oldSink.Dispose();
   lock (tunnelGate) tunnelSerial = null;
   if (oldLink != null) ThreadPool.QueueUserWorkItem(delegate { lock (tunnelGate) { if (tunnelSerial != oldLink.Serial) oldLink.RemoveReverse(UsbLink.Port); } });
  }

  void ApplyGain() { if (sender != null) sender.Gain = Muted ? 0f : (float)(Volume / 100.0); }

  public void CancelPairing() { if (State != LinkState.Pairing) return; Stop(); Set(LinkState.Offline); }
  public void SetMuted(bool value) { Muted = value; ApplyGain(); Raise(); }
  public void SetVolume(double value) {
   Volume = Math.Max(0, Math.Min(100, value));
   Muted = false;
   ApplyGain();
   Raise();
  }
  public void Disconnect() { Stop(); Set(LinkState.Offline); }

  // Verification only: puts the session in a state without a phone, so every view can be rendered.
  internal void ShowForVerification(LinkState state, string device, string code) {
   DeviceName = device; PairingCode = code; Problem = null;
   Set(state);
  }

  void Set(LinkState state) { State = state; Raise(); }
  void Raise() {
   if (phones != null) phones.Publish(new PhoneWatcher.Status {
    State = State == LinkState.Pairing ? "pairing" : State == LinkState.Streaming ? "streaming" : State == LinkState.Reconnecting ? "waiting" : "offline",
    Serial = usb == null ? null : usb.Serial,
    Problem = Problem,
    ProblemSerial = problemSerial,
    Secret = secret,
   });
   var handler = Changed; if (handler != null) handler(this, EventArgs.Empty);
  }
 }
}
