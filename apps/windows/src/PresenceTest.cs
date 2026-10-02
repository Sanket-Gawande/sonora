using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Sonora {
 // `Sonora.exe --presence-test <dir>`: plays a phone against the real presence channel over a
 // loopback socket (no adb, no phone) and checks every rule in docs/protocol.md, "Finding the PC
 // over USB": nothing about media reaches, or is obeyed from, an app without the stream's key.
 // Writes presence.txt, and presence-vector.txt for the Android tests.
 static class PresenceTest {
  const string Serial = "test-phone";
  const string PcId = "00112233aabbccdd", WifiPhoneId = "0123456789abcdef";

  sealed class PairAsk { public string Serial, Model, Code; public byte[] Key; public Action<bool> Answer; }
  sealed class JoinAsk { public string Serial, Model; public IPEndPoint Target; public byte[] Key; }

  sealed class Phone : IDisposable {
   readonly TcpClient client;
   readonly Stream stream;
   public Phone(int port) {
    client = new TcpClient();
    client.Connect("127.0.0.1", port);
    client.ReceiveTimeout = 1500;
    stream = client.GetStream();
   }
   public void Send(string line) { var b = Encoding.UTF8.GetBytes(line + "\n"); stream.Write(b, 0, b.Length); }
   // The next line, or null if none arrives within the timeout (or the PC closed the connection).
   public string Read(int timeout) {
    client.ReceiveTimeout = timeout;
    var bytes = new List<byte>();
    try {
     while (true) {
      int b = stream.ReadByte();
      if (b < 0) return bytes.Count == 0 ? null : Encoding.UTF8.GetString(bytes.ToArray());
      if (b == '\n') return Encoding.UTF8.GetString(bytes.ToArray());
      bytes.Add((byte)b);
     }
    } catch (IOException) { return null; }
   }
   public bool Closed() {
    try { client.ReceiveTimeout = 1500; return stream.ReadByte() < 0; }
    catch (IOException) { return true; }
   }
   public void Dispose() { client.Close(); }
  }

  public static int Run(string directory) {
   Directory.CreateDirectory(directory);
   var report = new StringBuilder();
   int failures = 0;
   Action<bool, string> check = delegate(bool ok, string what) {
    report.AppendLine((ok ? "PASS " : "FAIL ") + what);
    if (!ok) failures++;
   };

   var watcher = new PhoneWatcher(null, "Test PC", PcId) { PairCooldown = TimeSpan.FromMilliseconds(400) };
   var commands = new List<string>();
   var connects = new List<string>();
   var pairs = new List<PairAsk>();
   var joins = new List<JoinAsk>();
   var trusted = new Dictionary<string, byte[]>();
   var wifiVector = new StringBuilder();
   watcher.ConnectRequested += delegate(string serial, string request) { lock (connects) connects.Add(serial); };
   watcher.PairRequested += delegate(string serial, string model, string code, byte[] key, Action<bool> answer) {
    lock (pairs) pairs.Add(new PairAsk { Serial = serial, Model = model, Code = code, Key = key, Answer = answer });
   };
   watcher.JoinRequested += delegate(string serial, string model, IPEndPoint target, byte[] key) {
    lock (joins) joins.Add(new JoinAsk { Serial = serial, Model = model, Target = target, Key = key });
   };
   watcher.WifiKey = delegate(string id) { lock (trusted) { byte[] key; return trusted.TryGetValue(id, out key) ? key : null; } };
   var ends = new List<string>();
   watcher.ControlRequested += delegate(string serial, string action, long ms) { lock (commands) commands.Add(serial + " " + action + " " + ms); };
   watcher.EndRequested += delegate(string serial, string challenge, string proof) { lock (ends) ends.Add(serial + " " + (PhoneWatcher.Verify(streamKey, challenge, proof) ? "valid" : "invalid")); };
   int port = watcher.AddForTest(Serial, "Test phone");
   try {
    // 1. Greeting: name, a fresh challenge, and the state; nothing about media.
    var phone = new Phone(port);
    phone.Send("HELLO v=1&model=Test%20phone");
    string hello = phone.Read(2000);
    check(hello != null && hello.StartsWith("SONORA v=1&name=Test%20PC&challenge=") && hello.Contains("state=offline&you=0"), "greeting: " + hello);
    string challenge = Field(hello, "challenge");
    check(challenge != null && challenge.Length == 32, "challenge is 32 hex digits");

    // 2. The stream starts with this phone: it hears the state, but no media without a proof.
    streamKey = Key(1);
    watcher.Publish(new PhoneWatcher.Status { State = "streaming", Serial = Serial, Secret = streamKey });
    check(phone.Read(1500) == "STATE state=streaming&you=1", "state follows the session");
    watcher.PublishMedia("MEDIA title=Midnight%20City&artist=M83&app=Spotify&playing=1&toggle=1&previous=1&next=1&seek=1&position=61000&duration=243000&art=abc", "abc", "ART id=abc&jpeg=AAAA");
    check(phone.Read(700) == null, "no media before AUTH");
    phone.Send("CONTROL action=toggle");
    phone.Send("PING t=1");
    Thread.Sleep(300);
    check(Count(commands) == 0, "no control before AUTH");
    check(phone.Read(500) == null, "no PONG before AUTH");
    watcher.Link = delegate { return "https://www.youtube.com/watch?v=abc&t=95s"; };
    phone.Send("LINK request=0000abcd");
    check(phone.Read(500) == null, "no LINK before AUTH");

    // 3. A wrong proof changes nothing.
    phone.Send("AUTH proof=" + PhoneWatcher.ProofFor(Key(9), "sonora-v1-auth", challenge));
    check(phone.Read(700) == null, "wrong key: still no media");
    phone.Send("CONTROL action=next");
    Thread.Sleep(300);
    check(Count(commands) == 0, "wrong key: still no control");

    // 4. The right proof: artwork once, then the media line; controls are obeyed.
    phone.Send("AUTH proof=" + PhoneWatcher.ProofFor(streamKey, "sonora-v1-auth", challenge));
    check(phone.Read(1500) == "ART id=abc&jpeg=AAAA", "artwork after AUTH");
    string mediaLine = phone.Read(1500);
    check(mediaLine != null && mediaLine.StartsWith("MEDIA title=Midnight%20City"), "media after AUTH: " + mediaLine);
    watcher.PublishMedia("MEDIA title=Midnight%20City&artist=M83&app=Spotify&playing=0&toggle=1&previous=1&next=1&seek=1&position=62000&duration=243000&art=abc", "abc", "ART id=abc&jpeg=AAAA");
    string paused = phone.Read(1500);
    check(paused != null && paused.Contains("playing=0"), "media update, artwork not resent: " + paused);
    phone.Send("CONTROL action=toggle");
    phone.Send("CONTROL action=pause");
    phone.Send("CONTROL action=play");
    phone.Send("CONTROL action=seek&position=120000");
    phone.Send("CONTROL action=seek&position=-5");
    phone.Send("CONTROL action=seek");
    phone.Send("CONTROL action=format-c");
    phone.Send("CONTROL action=previous");
    Thread.Sleep(400);
    check(Join(commands) == Serial + " toggle -1|" + Serial + " pause -1|" + Serial + " play -1|" + Serial + " seek 120000|" + Serial + " previous -1", "controls obeyed, bad ones dropped: " + Join(commands));

    // Clock sync for the latency reading: PONG echoes the phone's time with this PC's clock and the
    // stream's clock sample; a second PING within 50 ms is ignored.
    watcher.Clock = delegate { return new AudioSender.ClockSample { Frame = 4800, Micros = 123456789 }; };
    long before = PhoneWatcher.NowMicros();
    phone.Send("PING t=42");
    phone.Send("PING t=43");
    string pong = phone.Read(1500);
    long pcTime;
    check(pong != null && pong.StartsWith("PONG t=42&time=") && pong.EndsWith("&frame=4800&at=123456789")
     && long.TryParse(Field(pong, "time"), out pcTime) && pcTime >= before && pcTime - before < 1000000, "PONG: " + pong);
    check(phone.Read(300) == null, "PINGs are rate-limited");
    Thread.Sleep(60);
    phone.Send("PING t=x");
    check(phone.Read(300) == null, "a PING without a number is ignored");

    // The playing tab's link, answered with the request's ID; "none" when there isn't one.
    phone.Send("LINK request=1234abcd");
    linkLine = phone.Read(4500);
    check(linkLine == "LINK request=1234abcd&url=https%3A%2F%2Fwww.youtube.com%2Fwatch%3Fv%3Dabc%26t%3D95s", "LINK answers with the link");
    watcher.Link = delegate { return null; };
    phone.Send("LINK request=1234abce");
    check(phone.Read(4500) == "LINK request=1234abce&none=1", "LINK says none when there's no link");
    phone.Send("LINK request=nothex!!");
    phone.Send("LINK");
    check(phone.Read(500) == null, "a LINK without a proper request ID is ignored");

    // 5. The stream moves to another phone: this one hears "none" and loses control.
    watcher.Publish(new PhoneWatcher.Status { State = "streaming", Serial = "other-phone", Secret = Key(2) });
    check(phone.Read(1500) == "STATE state=offline&you=0&busy=1", "state after the stream moved");
    check(phone.Read(1500) == PhoneWatcher.NoMedia, "media cleared after the stream moved");
    lock (commands) commands.Clear();
    phone.Send("CONTROL action=toggle");
    phone.Send("PING t=2");
    Thread.Sleep(300);
    check(Count(commands) == 0, "no control after the stream moved");
    check(phone.Read(500) == null, "no PONG after the stream moved");
    watcher.Link = delegate { return "https://example.com/"; };
    phone.Send("LINK request=5678abcd");
    check(phone.Read(500) == null, "no LINK after the stream moved");

    // 6. A new stream with this phone and a new key: the old proof no longer counts.
    streamKey = Key(3);
    watcher.Publish(new PhoneWatcher.Status { State = "streaming", Serial = Serial, Secret = streamKey });
    check(phone.Read(1500) == "STATE state=streaming&you=1", "back with this phone");
    check(phone.Read(700) == null, "old proof doesn't carry over to a new key");
    phone.Send("AUTH proof=" + PhoneWatcher.ProofFor(streamKey, "sonora-v1-auth", challenge));
    string again = phone.Read(1500);
    check(again != null && again.StartsWith("MEDIA title="), "new proof: media again (artwork already sent on this connection): " + again);

    // 7. END carries its own proof.
    phone.Send("END proof=" + PhoneWatcher.ProofFor(Key(9), "sonora-v1-end", challenge));
    phone.Send("END proof=" + PhoneWatcher.ProofFor(streamKey, "sonora-v1-end", challenge));
    Thread.Sleep(400);
    check(Join(ends) == Serial + " invalid|" + Serial + " valid", "END proofs checked: " + Join(ends));

    // 8. Another connection (another app on the phone) sees the state but none of the media.
    using (var other = new Phone(port)) {
     other.Send("HELLO v=1&model=x");
     string otherHello = other.Read(1500);
     check(otherHello != null && otherHello.Contains("state=streaming&you=1") && !otherHello.Contains(Field(hello, "challenge")), "second connection gets its own challenge");
     other.Send("AUTH proof=" + PhoneWatcher.ProofFor(streamKey, "sonora-v1-auth", challenge));
     check(other.Read(700) == null, "a proof made for another connection's challenge doesn't work");
    }

    // 9. Anything over 1 KB isn't Sonora: the PC hangs up.
    phone.Send("CONTROL action=" + new string('x', 2000));
    check(phone.Closed(), "oversized line closes the connection");
    phone.Dispose();

    // 10. A client that never says HELLO is dropped after 5 s.
    using (var silent = new Phone(port)) check(silent.Read(6500) == null && silent.Closed(), "no HELLO: dropped");

    // 11. Phones on Wi-Fi: found with a UDP query, paired once with the number, then joining with a proof.
    check(watcher.StartLan(0, 0), "Wi-Fi listener and discovery start");
    int lanPort = watcher.LanPort, answerPort = watcher.AnswerPort;
    using (var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0))) {
     udp.Client.ReceiveTimeout = 2000;
     var query = Encoding.UTF8.GetBytes("SONORA? v=1");
     udp.Send(query, query.Length, new IPEndPoint(IPAddress.Loopback, answerPort));
     var from = new IPEndPoint(IPAddress.Any, 0);
     string answer = Encoding.UTF8.GetString(udp.Receive(ref from));
     check(answer == "SONORA! v=1&name=Test%20PC&id=" + PcId + "&port=" + lanPort, "discovery answer: " + answer);
     // The same query on the multicast group, for routers that drop broadcasts (sent from a
     // network socket: multicast doesn't leave a socket bound to loopback).
     using (var multicast = new UdpClient(new IPEndPoint(IPAddress.Any, 0))) {
      multicast.Client.ReceiveTimeout = 2000;
      multicast.Send(query, query.Length, new IPEndPoint(PhoneWatcher.DiscoveryGroup, answerPort));
      string grouped;
      try { grouped = Encoding.UTF8.GetString(multicast.Receive(ref from)); } catch (SocketException) { grouped = null; }
      check(grouped == answer, "the multicast query is answered too: " + grouped);
     }
     var junk = Encoding.UTF8.GetBytes("HELLO? v=1");
     udp.Send(junk, junk.Length, new IPEndPoint(IPAddress.Loopback, answerPort));
     bool silentToJunk;
     try { udp.Client.ReceiveTimeout = 600; udp.Receive(ref from); silentToJunk = false; } catch (SocketException) { silentToJunk = true; }
     check(silentToJunk, "anything but the query gets no answer");
    }
    using (var anonymous = new Phone(lanPort)) {
     anonymous.Send("HELLO v=1&model=x");
     check(anonymous.Read(1500) == null && anonymous.Closed(), "a Wi-Fi HELLO without the app's ID is dropped");
    }
    var wifi = new Phone(lanPort);
    wifi.Send("HELLO v=1&model=Pixel%208&id=" + WifiPhoneId);
    string wifiHello = wifi.Read(2000);
    check(wifiHello != null && wifiHello.Contains("&id=" + PcId + "&") && wifiHello.Contains("you=0"), "Wi-Fi greeting carries the PC's ID: " + wifiHello);
    string wifiChallenge = Field(wifiHello, "challenge");
    wifi.Send("CONNECT request=" + new string('a', 32));
    Thread.Sleep(300);
    check(Count(connects) == 0, "USB's CONNECT is ignored over Wi-Fi");
    const string nonce = "00112233445566778899aabbccddeeff";
    wifi.Send("JOIN port=47210&nonce=" + nonce + "&proof=" + new string('0', 64));
    check(wifi.Read(1500) == "JOINED ok=0", "JOIN before pairing is refused");

    // Pairing, declined: both sides show the same number, and an answer counts once.
    using (var phoneKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256)) {
     byte[] phonePublic = PublicOf(phoneKey);
     wifi.Send("PAIR key=" + WifiTrust.Base64Url(phonePublic));
     string pairing = wifi.Read(1500);
     byte[] pcPublic = pairing == null ? null : WifiTrust.FromBase64Url(Field(pairing, "key"));
     check(pcPublic != null && pcPublic.Length == 64, "PAIRING carries the PC's public key: " + pairing);
     byte[] longTerm = WifiTrust.Agree(phoneKey, pcPublic, phonePublic, pcPublic);
     PairAsk ask = WaitFor(pairs, 0);
     check(ask != null && ask.Serial == PhoneWatcher.WifiSerial(WifiPhoneId) && ask.Model == "Pixel 8" && ask.Code == WifiTrust.Code(longTerm), "both sides show the same number");
     ask.Answer(false);
     check(wifi.Read(1500) == "PAIRED ok=0", "declined on the PC");
     ask.Answer(true);
     check(wifi.Read(300) == null, "an answer counts once");
     wifi.Send("PAIR key=" + WifiTrust.Base64Url(phonePublic));
     check(wifi.Read(200) == null, "right after a decline, that address can't ask again");
     Thread.Sleep(300);
    }
    wifi.Send("PAIR key=" + WifiTrust.Base64Url(new byte[64]));
    check(wifi.Read(600) == null, "a key that isn't on the curve is ignored");

    // Pairing, allowed: the PC remembers the key; the phone joins with a proof for this connection.
    using (var phoneKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256)) {
     byte[] phonePublic = PublicOf(phoneKey);
     wifi.Send("PAIR key=" + WifiTrust.Base64Url(phonePublic));
     string pairing = wifi.Read(1500);
     byte[] pcPublic = WifiTrust.FromBase64Url(Field(pairing, "key"));
     byte[] longTerm = WifiTrust.Agree(phoneKey, pcPublic, phonePublic, pcPublic);
     PairAsk ask = WaitFor(pairs, 1);
     check(ask != null && Same(ask.Key, longTerm), "both sides agree the same key");
     lock (trusted) trusted[WifiPhoneId] = ask.Key;
     ask.Answer(true);
     check(wifi.Read(1500) == "PAIRED ok=1", "allowed on the PC");

     wifi.Send("JOIN port=47210&nonce=" + nonce + "&proof=" + WifiTrust.JoinProof(longTerm, wifiChallenge, nonce, 47211));
     check(wifi.Read(1500) == "JOINED ok=0", "a proof for another port is refused");
     string proof = WifiTrust.JoinProof(longTerm, wifiChallenge, nonce, 47210);
     wifi.Send("JOIN port=47210&nonce=" + nonce + "&proof=" + proof);
     string joined = wifi.Read(1500);
     check(joined == "JOINED ok=1&proof=" + WifiTrust.JoinedProof(longTerm, wifiChallenge, nonce), "a paired phone joins, and the PC proves it holds the pairing too: " + joined);
     JoinAsk join = WaitFor(joins, 0);
     byte[] streamKeyWifi = WifiTrust.StreamKey(longTerm, wifiChallenge, nonce);
     check(join != null && join.Serial == PhoneWatcher.WifiSerial(WifiPhoneId) && join.Target.Port == 47210 && IPAddress.IsLoopback(join.Target.Address) && Same(join.Key, streamKeyWifi),
      "the stream goes to the phone's address and port, with the derived key");
     check(watcher.SinceHeard(PhoneWatcher.WifiSerial(WifiPhoneId)) < TimeSpan.FromSeconds(5), "a join counts as hearing from the phone");

     // Streaming to it: the derived key authorises media, as over USB.
     watcher.Publish(new PhoneWatcher.Status { State = "streaming", Serial = PhoneWatcher.WifiSerial(WifiPhoneId), Secret = streamKeyWifi });
     check(wifi.Read(1500) == "STATE state=streaming&you=1", "Wi-Fi phone hears the stream is its");
     wifi.Send("AUTH proof=" + PhoneWatcher.ProofFor(streamKeyWifi, "sonora-v1-auth", wifiChallenge));
     string first = wifi.Read(1500), second = wifi.Read(1500);
     check(first == "ART id=abc&jpeg=AAAA" && second != null && second.StartsWith("MEDIA title="), "media over Wi-Fi after AUTH: " + second);

     var p = phoneKey.ExportParameters(true);
     wifiVector.AppendLine("wifi_phone_private=" + WifiTrust.Base64Url(p.D));
     wifiVector.AppendLine("wifi_phone_public=" + WifiTrust.Base64Url(phonePublic));
     wifiVector.AppendLine("wifi_pc_public=" + WifiTrust.Base64Url(pcPublic));
     wifiVector.AppendLine("wifi_key=" + WifiTrust.Base64Url(longTerm));
     wifiVector.AppendLine("wifi_code=" + WifiTrust.Code(longTerm));
     wifiVector.AppendLine("wifi_challenge=" + wifiChallenge);
     wifiVector.AppendLine("wifi_nonce=" + nonce);
     wifiVector.AppendLine("wifi_port=47210");
     wifiVector.AppendLine("wifi_proof=" + proof);
     wifiVector.AppendLine("wifi_stream=" + WifiTrust.Base64Url(streamKeyWifi));
     wifiVector.AppendLine("wifi_joined=" + WifiTrust.JoinedProof(longTerm, wifiChallenge, nonce));
    }
    wifi.Dispose();

    // Finding the playing tab, and what its address bar may hand the phone.
    check(PlayingApp.Score("He Surrano Chandra Vha | Mahesh Kale | YouTube Music - Audio playing", "He Surrano Chandra Vha | Mahesh Kale | Natyageet") == 3, "tab with the title and the audio mark scores highest");
    check(PlayingApp.Score("Another video - YouTube - Audio playing", "He Surrano Chandra Vha") == 1, "a tab only marked as playing still counts");
    check(PlayingApp.Score("Inbox - Gmail", "Afterglow") == 0, "an unrelated tab doesn't");
    check(PlayingApp.Normalize("youtube.com/watch?v=abc") == "https://youtube.com/watch?v=abc", "a bare address becomes https");
    check(PlayingApp.Normalize("http://example.com/a b") == null, "text with spaces isn't a link");
    check(PlayingApp.Normalize("file:///C:/secret.txt") == null && PlayingApp.Normalize("javascript:alert(1)") == null, "only http and https links");
    check(PlayingApp.Normalize("localhost:3000/x") == null, "local addresses aren't sent");
    check(PlayingApp.WithPosition("https://www.youtube.com/watch?v=abc&t=10s", TimeSpan.FromSeconds(95.6)) == "https://www.youtube.com/watch?v=abc&t=95s", "YouTube links carry the position");
    check(PlayingApp.WithPosition("https://music.youtube.com/watch?v=abc", TimeSpan.FromSeconds(95)) == "https://music.youtube.com/watch?v=abc", "other links are left alone");
    check(PlayingApp.WithPosition("https://youtu.be/abc", TimeSpan.FromSeconds(3)) == "https://youtu.be/abc", "no position in the first seconds");

    // Shared vector: the Android tests compute the same proofs.
    var vector = new StringBuilder();
    vector.AppendLine("secret=" + Convert.ToBase64String(streamKey));
    vector.AppendLine("challenge=" + challenge);
    vector.AppendLine("auth=" + PhoneWatcher.ProofFor(streamKey, "sonora-v1-auth", challenge));
    vector.AppendLine("end=" + PhoneWatcher.ProofFor(streamKey, "sonora-v1-end", challenge));
    vector.AppendLine("media=" + mediaLine);
    vector.AppendLine("link=" + linkLine);
    vector.Append(wifiVector);
    File.WriteAllText(Path.Combine(directory, "presence-vector.txt"), vector.ToString(), new UTF8Encoding(false));
   } catch (Exception error) {
    check(false, "unexpected: " + error.Message);
   } finally {
    watcher.Dispose();
   }
   report.AppendLine(failures == 0 ? "All presence checks passed." : failures + " presence check(s) failed.");
   File.WriteAllText(Path.Combine(directory, "presence.txt"), report.ToString(), Encoding.UTF8);
   return failures == 0 ? 0 : 1;
  }

  static byte[] streamKey;
  static string linkLine;

  static byte[] Key(int seed) { var key = new byte[32]; for (int i = 0; i < 32; i++) key[i] = (byte)(seed * 31 + i * 7); return key; }

  static string Field(string line, string key) {
   if (line == null) return null;
   foreach (var pair in line.Substring(line.IndexOf(' ') + 1).Split('&')) {
    int eq = pair.IndexOf('=');
    if (eq > 0 && pair.Substring(0, eq) == key) return pair.Substring(eq + 1);
   }
   return null;
  }

  static int Count(List<string> list) { lock (list) return list.Count; }

  static T WaitFor<T>(List<T> list, int index) where T : class {
   var until = DateTime.UtcNow.AddSeconds(2);
   while (DateTime.UtcNow < until) {
    lock (list) if (list.Count > index) return list[index];
    Thread.Sleep(20);
   }
   return null;
  }

  static byte[] PublicOf(ECDiffieHellman key) {
   var q = key.ExportParameters(false).Q;
   var both = new byte[64];
   Buffer.BlockCopy(q.X, 0, both, 0, 32);
   Buffer.BlockCopy(q.Y, 0, both, 32, 32);
   return both;
  }

  static bool Same(byte[] a, byte[] b) { return a != null && b != null && Convert.ToBase64String(a) == Convert.ToBase64String(b); }
  static string Join(List<string> list) { lock (list) return string.Join("|", list.ToArray()); }
 }
}
