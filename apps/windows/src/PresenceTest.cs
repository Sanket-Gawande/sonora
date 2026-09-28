using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Sonora {
 // `Sonora.exe --presence-test <dir>`: plays a phone against the real presence channel over a
 // loopback socket (no adb, no phone) and checks every rule in docs/protocol.md, "Finding the PC
 // over USB": nothing about media reaches, or is obeyed from, an app without the stream's key.
 // Writes presence.txt, and presence-vector.txt for the Android tests.
 static class PresenceTest {
  const string Serial = "test-phone";

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

   var watcher = new PhoneWatcher(null, "Test PC");
   var commands = new List<string>();
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

    // Shared vector: the Android tests compute the same proofs.
    var vector = new StringBuilder();
    vector.AppendLine("secret=" + Convert.ToBase64String(streamKey));
    vector.AppendLine("challenge=" + challenge);
    vector.AppendLine("auth=" + PhoneWatcher.ProofFor(streamKey, "sonora-v1-auth", challenge));
    vector.AppendLine("end=" + PhoneWatcher.ProofFor(streamKey, "sonora-v1-end", challenge));
    vector.AppendLine("media=" + mediaLine);
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
  static string Join(List<string> list) { lock (list) return string.Join("|", list.ToArray()); }
 }
}
