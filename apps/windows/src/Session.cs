using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Sonora {
 public enum LinkState { Offline, Pairing, Streaming, Reconnecting }

 // The stream to one phone. Over USB: find it with adb, hand over the pairing link, then stream
 // real desktop audio once the phone confirms the number. Over Wi-Fi: a phone the user allowed
 // once (it and the notch showed the same number) joins by itself, and the audio goes to it as
 // UDP. The stream carries whatever the PC plays; pausing is done on the media itself, not on the
 // stream. The phone can also start and end it: PhoneWatcher shows this PC to the Sonora app on
 // every plugged-in phone and every phone on the Wi-Fi.
 public sealed class Session {
  // A phone on Wi-Fi waiting to be allowed: the notch shows its model and the number to compare.
  public sealed class WifiRequest {
   public string Model { get; internal set; }
   public string Code { get; internal set; }
   internal Action<bool> Answer;
  }

  readonly Dispatcher dispatcher;
  readonly Preferences prefs;
  readonly DispatcherTimer approvalTimer, pulse, speakerWatch;
  // This PC's speakers (Windows' own volume and mute), followed once a second.
  readonly SystemVolume speakers = new SystemVolume();
  double pcLevel = -1;
  bool pcMuted;
  // The phone the stream is with: an adb serial, or "wifi-" and its app's ID.
  string streamSerial;
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
  // The playing tab's link for the phone (PlayingApp.Link); called off the UI thread.
  public Func<string> PlayingLink;
  public LinkState State { get; private set; }
  public bool OverWifi { get; private set; }
  public double PcLevel { get { return pcLevel; } }
  public bool PcMuted { get { return pcMuted; } }
  // The phone that muted this PC's speakers; they come back on when its stream ends.
  public string MutedBy { get; private set; }
  public bool AllowWifi { get { return prefs.AllowWifi; } }
  // The streaming phone's media volume as it reports it, 0..100; -1 until it does.
  public int PhoneLevel { get; private set; }
  public bool WifiReady { get { return phones != null && phones.LanPort > 0; } }
  public WifiRequest Approval { get; private set; }
  // Raised when a phone asks to pair, so the notch can open.
  public event EventHandler ApprovalRequested;
  // Why phones on the Wi-Fi can't reach this PC, or null.
  public string WifiProblem { get; private set; }
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
  // That phone is on adb over Wi-Fi (wireless debugging), not a cable.
  public bool PluggedWireless { get; private set; }
  public bool IsActive { get { return State == LinkState.Streaming; } }

  // The name phones show for this PC: the host name as the user typed it, not the upper-case NetBIOS one.
  public static string ComputerName {
   get {
    try { string name = Dns.GetHostName(); if (!string.IsNullOrEmpty(name)) return name; }
    catch (System.Net.Sockets.SocketException) { }
    return Environment.MachineName;
   }
  }

  public Session(Preferences prefs) {
   dispatcher = Dispatcher.CurrentDispatcher;
   this.prefs = prefs;
   Volume = prefs.Volume;
   PhoneLevel = -1;
   approvalTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
   approvalTimer.Tick += delegate { approvalTimer.Stop(); AnswerApproval(false); };
   // A Wi-Fi stream has no connection to lose: the phone's app pings every 2 s while it plays, and
   // 20 s of silence means it has gone (out of range, or the app closed).
   speakerWatch = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
   speakerWatch.Tick += delegate { ReadSpeakers(); };
   ReadSpeakers();
   speakerWatch.Start();
   pulse = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
   pulse.Tick += delegate {
    if (!OverWifi || phones == null || phones.SinceHeard(streamSerial) < TimeSpan.FromSeconds(20)) return;
    string device = DeviceName;
    Stop();
    Problem = device + " stopped answering on Wi-Fi.";
    Set(LinkState.Offline);
   };
  }

  public int WifiPhoneCount { get { return prefs.WifiPhones.Count; } }

  // The speakers as Windows has them now (the flyout, a keyboard key, or the phone may change them).
  void ReadSpeakers() {
   double level = speakers.Level;
   bool muted = speakers.Muted;
   if (!muted) MutedBy = null;
   if (level == pcLevel && muted == pcMuted) return;
   pcLevel = level; pcMuted = muted;
   Raise();
  }

  public void SetPcLevel(double level) { speakers.Level = level; ReadSpeakers(); }

  // `by`: the phone that asked, so its stream ending unmutes them again.
  public void SetPcMuted(bool muted, string by) {
   speakers.Muted = muted;
   MutedBy = muted ? by : null;
   ReadSpeakers();
   Raise();
  }

  // Off: phones on the Wi-Fi can't see this PC (a stream over Wi-Fi ends). On: they can again.
  public void SetAllowWifi(bool on) {
   prefs.AllowWifi = on;
   prefs.Save();
   if (phones != null) {
    if (on) StartLan(); else { phones.StopLan(); WifiProblem = null; if (OverWifi) Disconnect(); }
   }
   Raise();
  }

  void StartLan() {
   WifiProblem = phones.StartLan(PhoneWatcher.Port, PhoneWatcher.DiscoveryPort) ? null
    : "Ports " + PhoneWatcher.Port + " and " + PhoneWatcher.DiscoveryPort + " are in use, so phones on Wi-Fi can't find this PC.";
  }

  // Phones on Wi-Fi then have to be allowed again; a stream playing now carries on.
  public void ForgetWifiPhones() { prefs.ForgetWifi(); Raise(); }

  // Starts watching for phones: plugged in (when adb is here; the notch says what's missing when
  // you ask for USB without it) and on this Wi-Fi.
  public void Watch() {
   if (phones != null) {
    // platform-tools may have been added since Sonora started.
    if (phones.Adb == null) phones.UseAdb(UsbLink.FindAdb());
    return;
   }
   phones = new PhoneWatcher(UsbLink.FindAdb(), ComputerName, prefs.PcId);
   phones.PhonesChanged += delegate { dispatcher.BeginInvoke(new Action(OnPhonesChanged)); };
   phones.ConnectRequested += delegate(string serial, string request) { dispatcher.BeginInvoke(new Action(delegate { ConnectFromPhone(serial, request); })); };
   phones.EndRequested += delegate(string serial, string challenge, string proof) { dispatcher.BeginInvoke(new Action(delegate { EndFromPhone(serial, challenge, proof); })); };
   phones.ControlRequested += delegate(string serial, string action, long ms) {
    dispatcher.BeginInvoke(new Action(delegate {
     // The watcher checked the key; the stream must still be with that phone.
     if (streamSerial == null || streamSerial != serial) return;
     if (action == "mute" || action == "unmute") { SetPcMuted(action == "mute", DeviceName ?? "your phone"); return; }
     var handler = MediaControl;
     if (handler != null) handler(action, TimeSpan.FromMilliseconds(Math.Max(0, ms)));
    }));
   };
   phones.Clock = delegate { var live = liveSender; return live == null ? null : live.Clock; };
   phones.Link = delegate { var source = PlayingLink; return source == null ? null : source(); };
   phones.PairRequested += delegate(string serial, string model, string code, byte[] key, Action<bool> answer) {
    dispatcher.BeginInvoke(new Action(delegate { AskToPair(serial, model, code, key, answer); }));
   };
   phones.JoinRequested += delegate(string serial, string model, IPEndPoint target, byte[] key) {
    dispatcher.BeginInvoke(new Action(delegate { ConnectWifi(serial, model, target, key); }));
   };
   phones.WifiKey = delegate(string id) { return prefs.WifiKey(id); };
   phones.PhoneVolume += delegate(string serial, int level) {
    dispatcher.BeginInvoke(new Action(delegate { if (serial == streamSerial && level != PhoneLevel) { PhoneLevel = level; Raise(); } }));
   };
   phones.Start();
   if (prefs.AllowWifi) StartLan();
  }

  public void StopWatching() { if (phones != null) phones.Dispose(); }

  public void ConnectUsb() { ConnectUsb(null, null); }

  // `serial` and `request` are set when the Sonora app on that phone asked to connect.
  void ConnectUsb(string serial, string request) {
   if (Busy || State != LinkState.Offline) return;
   Watch();
   if (phones.Adb == null) { Problem = UsbLink.NoAdb; problemSerial = serial; Raise(); return; }
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
   if (streamSerial == null || streamSerial != serial || !PhoneWatcher.Verify(secret, challenge, proof)) return;
   Disconnect();
  }

  // A phone on Wi-Fi asks to pair: the notch opens with its number, and the user allows it or not.
  // One at a time; no answer within a minute counts as no.
  void AskToPair(string serial, string model, string code, byte[] key, Action<bool> answer) {
   if (Approval != null) { answer(false); return; }
   var request = new WifiRequest { Model = model, Code = code };
   request.Answer = delegate(bool allowed) {
    if (Approval != request) return;
    Approval = null;
    approvalTimer.Stop();
    if (allowed) prefs.RememberWifi(PhoneWatcher.WifiId(serial), model, key);
    answer(allowed);
    Raise();
   };
   Approval = request;
   approvalTimer.Stop();
   approvalTimer.Start();
   var handler = ApprovalRequested;
   if (handler != null) handler(this, EventArgs.Empty);
   Raise();
  }

  public void AnswerApproval(bool allowed) { var request = Approval; if (request != null) request.Answer(allowed); }

  // A paired phone on Wi-Fi joined: the stream moves to it, even from another phone.
  void ConnectWifi(string serial, string model, IPEndPoint target, byte[] key) {
   if (State != LinkState.Offline) Stop();
   Busy = false; Problem = null; problemSerial = null;
   streamSerial = serial; OverWifi = true; secret = key;
   DeviceName = model; PairingCode = null;
   try { sender = new AudioSender(key, target); }
   catch (SocketException) { Stop(); Problem = "Couldn't send audio over Wi-Fi."; Set(LinkState.Offline); return; }
   ApplyGain();
   liveSender = sender;
   try { sender.Start(); }
   catch (Exception error) { Stop(); Problem = "Couldn't capture desktop audio: " + error.Message; Set(LinkState.Offline); return; }
   pulse.Start();
   Set(LinkState.Streaming);
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
   PluggedWireless = ready != null && ready.Wireless;
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
   usb = link; sink = newSink; secret = offer.Secret; streamSerial = link.Serial; OverWifi = false;
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
   streamSerial = null; OverWifi = false; PhoneLevel = -1;
   pulse.Stop();
   // Muted from the phone: the speakers come back with the end of its stream.
   if (MutedBy != null) { speakers.Muted = false; MutedBy = null; ReadSpeakers(); }
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

  // Verification only: a phone on Wi-Fi asking to pair, and a stream over Wi-Fi, without a phone.
  internal void ShowApprovalForVerification(string model, string code) {
   Approval = new WifiRequest { Model = model, Code = code, Answer = delegate { Approval = null; Raise(); } };
   Raise();
  }
  internal void ShowWifiForVerification(bool wifi) { OverWifi = wifi; Raise(); }
  // The speakers shown as muted without touching the real ones (the 1 s check would put them back).
  internal void ShowPcMutedForVerification(bool muted) { speakerWatch.Stop(); pcMuted = muted; Raise(); if (!muted) speakerWatch.Start(); }

  void Set(LinkState state) { State = state; Raise(); }
  void Raise() {
   if (phones != null) phones.Publish(new PhoneWatcher.Status {
    State = State == LinkState.Pairing ? "pairing" : State == LinkState.Streaming ? "streaming" : State == LinkState.Reconnecting ? "waiting" : "offline",
    Serial = streamSerial,
    Problem = Problem,
    ProblemSerial = problemSerial,
    Secret = secret,
    PcMuted = pcMuted,
   });
   var handler = Changed; if (handler != null) handler(this, EventArgs.Empty);
  }
 }
}
