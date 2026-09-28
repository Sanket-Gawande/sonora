using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Sonora {
 // `Sonora.exe --transport-test <dir>`: checks the packet layer and pushes one second of real
 // captured audio through it over 127.0.0.1 only. Also writes vector.txt for the Android tests.
 static class TransportTest {
  const int FramesPerPacket = 240, Channels = 2;

  public static int Run(string directory) {
   Directory.CreateDirectory(directory);
   var report = new StringBuilder();
   WriteVector(directory);
   report.AppendLine("PASS test vector written (vector.txt)");
   CheckResampler(report);

   var secret = new byte[32];
   using (var rng = new System.Security.Cryptography.RNGCryptoServiceProvider()) rng.GetBytes(secret);
   // The notch's level meter runs alongside capture in the app; keep them together here too.
   using (new AudioMeter())
   using (var sender = new PacketCodec(secret))
   using (var receiver = new PacketCodec(secret)) {
    CheckTamperAndReplay(sender, receiver, report);
    receiver.ResetReplayWindow();
    LiveLoop(sender, receiver, report);
    receiver.ResetReplayWindow();
    SenderLoop(secret, receiver, report);
   }
   report.AppendLine("Only 127.0.0.1 was used; nothing left this PC.");
   File.WriteAllText(Path.Combine(directory, "transport.txt"), report.ToString(), Encoding.UTF8);
   return 0;
  }

  static void WriteVector(string directory) {
   var secret = new byte[32];
   for (int i = 0; i < 32; i++) secret[i] = (byte)i;
   var pcm = new byte[FramesPerPacket * Channels * 2];
   for (int i = 0; i < pcm.Length; i++) pcm[i] = (byte)(i * 7);
   byte[] packet;
   using (var codec = new PacketCodec(secret)) packet = codec.Seal(0x01020304, 7, 1680, false, pcm, 0, pcm.Length);
   var offer = new PairingOffer(secret, "192.168.1.20", 47210, "Studio PC");
   File.WriteAllText(Path.Combine(directory, "vector.txt"),
    "secret=" + Hex(secret) + "\nstreamId=16909060\nsequence=7\ntimestamp=1680\nplaintext=" + Hex(pcm) + "\npacket=" + Hex(packet) +
    "\nnumber=" + offer.Number + "\nlink=" + offer.Link + "\n");
  }

  // 1 s of 1 kHz at 44.1 kHz mono, fed in 10 ms blocks, must become ~48000 smooth stereo frames.
  static void CheckResampler(StringBuilder report) {
   var resampler = new Resampler(44100, 1);
   var block = new float[441];
   long total = 0, n = 0;
   double worstStep = 0, previous = double.NaN;
   for (int b = 0; b < 100; b++) {
    for (int i = 0; i < block.Length; i++, n++) block[i] = (float)(0.5 * Math.Sin(2 * Math.PI * 1000 * n / 44100.0));
    int frames;
    float[] output = resampler.Process(block, block.Length, out frames);
    for (int i = 0; i < frames; i++) {
     Expect(output[i * 2] == output[i * 2 + 1], "mono is duplicated to both channels");
     if (!double.IsNaN(previous)) worstStep = Math.Max(worstStep, Math.Abs(output[i * 2] - previous));
     previous = output[i * 2];
    }
    total += frames;
   }
   // A 0.5-amplitude 1 kHz sine moves at most 0.5 * 2π * 1000 / 48000 ≈ 0.065 per 48 kHz frame.
   Expect(Math.Abs(total - 48000) <= 2, "44.1 kHz → 48 kHz frame count (" + total + ")");
   Expect(worstStep < 0.07, "no discontinuities across blocks (largest step " + worstStep.ToString("0.000") + ")");
   report.AppendLine("PASS resampler: 44.1 kHz mono → " + total + " frames of 48 kHz stereo, largest sample step " + worstStep.ToString("0.000"));
  }

  static void CheckTamperAndReplay(PacketCodec sender, PacketCodec receiver, StringBuilder report) {
   var pcm = new byte[960];
   uint id, seq; ulong ts; bool silent;
   var good = sender.Seal(9, 100, 0, false, pcm, 0, pcm.Length);
   var copy = (byte[])good.Clone();
   Expect(receiver.Open(copy, copy.Length, out id, out seq, out ts, out silent) == PacketCodec.Result.Ok, "valid packet opens");
   copy = (byte[])good.Clone();
   Expect(receiver.Open(copy, copy.Length, out id, out seq, out ts, out silent) == PacketCodec.Result.Replay, "replayed packet is rejected");
   var tampered = sender.Seal(9, 101, 0, false, pcm, 0, pcm.Length);
   tampered[PacketCodec.HeaderSize + 10] ^= 1;
   Expect(receiver.Open(tampered, tampered.Length, out id, out seq, out ts, out silent) == PacketCodec.Result.BadTag, "tampered audio is rejected");
   var headerTampered = sender.Seal(9, 102, 0, false, pcm, 0, pcm.Length);
   headerTampered[11] ^= 1;
   Expect(receiver.Open(headerTampered, headerTampered.Length, out id, out seq, out ts, out silent) == PacketCodec.Result.BadTag, "tampered header is rejected");
   using (var stranger = new PacketCodec(new byte[32])) {
    var foreign = stranger.Seal(9, 103, 0, false, pcm, 0, pcm.Length);
    Expect(receiver.Open(foreign, foreign.Length, out id, out seq, out ts, out silent) == PacketCodec.Result.BadTag, "packet from an unpaired key is rejected");
   }
   var old = sender.Seal(9, 20, 0, false, pcm, 0, pcm.Length);
   Expect(receiver.Open(old, old.Length, out id, out seq, out ts, out silent) == PacketCodec.Result.Replay, "packet older than the window is rejected");
   report.AppendLine("PASS rejects: replay, tampered audio, tampered header, wrong key, too old");
  }

  static void LiveLoop(PacketCodec sender, PacketCodec receiver, StringBuilder report) {
   // Capture one second of real desktop audio (with a quiet tone so there is signal).
   var pcm = new List<byte>();
   var capture = new LoopbackCapture();
   capture.Frames += delegate(float[] samples, int frames, bool fromDevice) {
    lock (pcm) for (int i = 0; i < frames * Channels; i++) {
     int v = (int)Math.Round(Math.Max(-1f, Math.Min(1f, samples[i])) * 32767);
     pcm.Add((byte)v); pcm.Add((byte)(v >> 8));
    }
   };
   capture.Start();
   if (capture.SampleRate != 48000 || capture.Channels != 2)
    report.AppendLine("NOTE device mix is " + capture.FormatDescription + "; this test sends it unconverted");
   using (var player = new System.Media.SoundPlayer(new MemoryStream(Tone())) ) { player.Play(); Thread.Sleep(1000); }
   capture.Dispose();
   byte[] audio; lock (pcm) audio = pcm.ToArray();

   int packetBytes = FramesPerPacket * Channels * 2;
   int packets = audio.Length / packetBytes;
   var received = new byte[packets * packetBytes];
   var transit = new List<double>();
   var clock = Stopwatch.StartNew();
   var sendTimes = new double[packets];
   int ok = 0;
   using (var rx = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
   using (var tx = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0))) {
    var target = (IPEndPoint)rx.Client.LocalEndPoint;
    rx.Client.ReceiveTimeout = 1000;
    var listener = new Thread(delegate() {
     var from = new IPEndPoint(IPAddress.Any, 0);
     try {
      while (ok < packets) {
       byte[] data = rx.Receive(ref from);
       double at = clock.Elapsed.TotalMilliseconds;
       uint id, seq; ulong ts; bool silent;
       if (receiver.Open(data, data.Length, out id, out seq, out ts, out silent) != PacketCodec.Result.Ok) continue;
       Buffer.BlockCopy(data, PacketCodec.HeaderSize, received, (int)seq * packetBytes, packetBytes);
       lock (transit) transit.Add(at - sendTimes[seq]);
       ok++;
      }
     } catch (SocketException) { }
    });
    listener.Start();
    for (int i = 0; i < packets; i++) {
     var packet = sender.Seal(0xABCD, (uint)i, (ulong)(i * FramesPerPacket), false, audio, i * packetBytes, packetBytes);
     sendTimes[i] = clock.Elapsed.TotalMilliseconds;
     tx.Send(packet, packet.Length, target);
     if (i % 8 == 7) Thread.Sleep(1);
    }
    listener.Join(3000);
   }
   bool identical = true;
   for (int i = 0; i < packets * packetBytes && identical; i++) identical = received[i] == audio[i];
   Expect(ok == packets && identical, "all packets arrive and decrypt to the captured audio (" + ok + "/" + packets + ")");
   transit.Sort();
   report.AppendLine("PASS live: " + packets + " packets (" + (packets * 5) + " ms of captured audio) sealed, sent over 127.0.0.1, opened, byte-identical");
   report.AppendLine("Localhost transit incl. crypto: median " + transit[transit.Count / 2].ToString("0.000") + " ms, p99 " + transit[(int)(transit.Count * 0.99)].ToString("0.000") + " ms (not network latency)");
  }

  // The real sender, end to end: capture → seal → UDP to 127.0.0.1 → open, for one second.
  static void SenderLoop(byte[] secret, PacketCodec receiver, StringBuilder report) {
   using (var rx = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0))) {
    rx.Client.ReceiveTimeout = 300;
    var target = (IPEndPoint)rx.Client.LocalEndPoint;
    int ok = 0, rejected = 0, gaps = 0;
    long expected = -1;
    bool heard = false;
    var listener = new Thread(delegate() {
     var from = new IPEndPoint(IPAddress.Any, 0);
     try {
      while (true) {
       byte[] data = rx.Receive(ref from);
       uint id, seq; ulong ts; bool silent;
       if (receiver.Open(data, data.Length, out id, out seq, out ts, out silent) != PacketCodec.Result.Ok) { rejected++; continue; }
       if (expected >= 0 && seq != expected) gaps++;
       expected = seq + 1L;
       if (!silent) heard = true;
       ok++;
      }
     } catch (SocketException) { }
    });
    listener.Start();
    using (var sender = new AudioSender(secret, target)) {
     sender.Start();
     using (var player = new System.Media.SoundPlayer(new MemoryStream(Tone()))) { player.Play(); Thread.Sleep(1000); }
     Thread.Sleep(100);
    }
    listener.Join(1000);
    Expect(ok >= 150 && rejected == 0 && gaps == 0 && heard, "sender delivers a contiguous, audible stream (" + ok + " ok, " + rejected + " rejected, " + gaps + " gaps)");
    report.AppendLine("PASS sender: " + ok + " packets in ~1.1 s from live capture, contiguous, none rejected, tone present");
   }
  }

  static byte[] Tone() {
   int rate = 48000, samples = rate / 2;
   var data = new MemoryStream();
   var w = new BinaryWriter(data);
   w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + samples * 2); w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
   w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
   w.Write(Encoding.ASCII.GetBytes("data")); w.Write(samples * 2);
   for (int i = 0; i < samples; i++) w.Write((short)(Math.Sin(2 * Math.PI * 440 * i / rate) * 0.1 * Math.Min(1.0, Math.Min(i, samples - 1 - i) / 480.0) * short.MaxValue));
   w.Flush();
   return data.ToArray();
  }

  static void Expect(bool condition, string what) { if (!condition) throw new Exception("FAILED: " + what); }
  static string Hex(byte[] b) { var s = new StringBuilder(b.Length * 2); foreach (var x in b) s.Append(x.ToString("x2")); return s.ToString(); }
 }
}
