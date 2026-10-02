using System;
using System.Diagnostics;
using System.IO;
using System.Media;
using System.Text;
using System.Threading;

namespace Sonora {
 // `Sonora.exe --capture-test <dir> [--tone]`: runs loopback capture for three seconds and
 // reports what was actually measured. With --tone it plays a short, quiet 440 Hz tone on the
 // default output and times how long it takes to appear in the capture.
 static class CaptureTest {
  // `--muted` mutes the speakers (Windows' own mute) for the run and restores them after: the tone
  // must still reach the capture, since the phone's Mute PC relies on it.
  public static int Run(string directory, bool tone, bool muted) {
   Directory.CreateDirectory(directory);
   var report = new StringBuilder();
   var capture = new LoopbackCapture();
   var clock = Stopwatch.StartNew();
   object sync = new object();
   long deviceFrames = 0, fillFrames = 0, packets = 0;
   double lastPacketMs = -1, maxPacketGapMs = 0, peak = 0, toneStartMs = -1, toneHeardMs = -1;
   Exception failure = null;

   capture.Failed += delegate(Exception error) { failure = error; };
   capture.Frames += delegate(float[] samples, int frames, bool fromDevice) {
    lock (sync) {
     double now = clock.Elapsed.TotalMilliseconds;
     if (!fromDevice) { fillFrames += frames; return; }
     packets++;
     deviceFrames += frames;
     if (lastPacketMs >= 0) maxPacketGapMs = Math.Max(maxPacketGapMs, now - lastPacketMs);
     lastPacketMs = now;
     for (int i = 0; i < frames * capture.Channels; i++) {
      double v = Math.Abs(samples[i]);
      if (v > peak) peak = v;
      if (toneStartMs >= 0 && toneHeardMs < 0 && v > 0.02) toneHeardMs = now;
     }
    }
   };

   var volume = new SystemVolume();
   bool wasMuted = volume.Muted;
   if (muted) volume.Muted = true;
   capture.Start();
   lock (sync) clock.Restart();
   report.AppendLine("Device format: " + capture.FormatDescription);
   report.AppendLine("Engine period: " + capture.DevicePeriodMs.ToString("0.0") + " ms · buffer " + capture.DeviceBufferFrames + " frames");

   Thread.Sleep(500);
   SoundPlayer player = null;
   if (tone) {
    player = new SoundPlayer(new MemoryStream(ToneWave(capture.SampleRate > 0 ? capture.SampleRate : 48000, 440, 0.4, 0.1)));
    player.Load();
    lock (sync) toneStartMs = clock.Elapsed.TotalMilliseconds;
    player.Play();
   }
   Thread.Sleep(2500);
   double elapsed = clock.Elapsed.TotalSeconds;
   capture.Dispose();
   if (player != null) player.Dispose();
   if (muted) { report.AppendLine("Speakers muted during the run: " + volume.Muted); volume.Muted = wasMuted; }
   if (failure != null) throw failure;

   lock (sync) {
    long total = deviceFrames + fillFrames;
    report.AppendLine("Captured: " + deviceFrames + " device frames in " + packets + " packets, " + fillFrames + " silence-fill frames");
    report.AppendLine("Stream rate: " + (total / elapsed).ToString("0") + " frames/s over " + elapsed.ToString("0.00") + " s (nominal " + capture.SampleRate + ")");
    report.AppendLine("Largest gap between device packets: " + (packets > 1 ? maxPacketGapMs.ToString("0.0") + " ms" : "n/a (device idle)"));
    report.AppendLine("Peak level: " + (peak > 0 ? (20 * Math.Log10(peak)).ToString("0.0") + " dBFS" : "silence"));
    if (tone)
     report.AppendLine(toneHeardMs >= 0
      ? "Tone reached the capture " + (toneHeardMs - toneStartMs).ToString("0") + " ms after Play() (includes SoundPlayer start-up; an upper bound, not stream latency)"
      : "Tone was not detected in the capture");
   }
   report.AppendLine("Nothing was sent over the network.");
   File.WriteAllText(Path.Combine(directory, "capture.txt"), report.ToString(), Encoding.UTF8);
   return 0;
  }

  // 16-bit mono WAV: a short sine with 10 ms fades so it doesn't click.
  static byte[] ToneWave(int rate, double hz, double seconds, double amplitude) {
   int samples = (int)(rate * seconds);
   var data = new MemoryStream();
   var w = new BinaryWriter(data);
   w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + samples * 2); w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
   w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
   w.Write(Encoding.ASCII.GetBytes("data")); w.Write(samples * 2);
   int fade = rate / 100;
   for (int i = 0; i < samples; i++) {
    double envelope = Math.Min(1.0, Math.Min(i, samples - 1 - i) / (double)fade);
    w.Write((short)(Math.Sin(2 * Math.PI * hz * i / rate) * amplitude * envelope * short.MaxValue));
   }
   w.Flush();
   return data.ToArray();
  }
 }
}
