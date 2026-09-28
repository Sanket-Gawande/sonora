using System;
using System.Net;
using System.Security.Cryptography;

namespace Sonora {
 // Captures desktop audio and sends it as sealed 5 ms packets (docs/protocol.md) to one phone.
 // Packets are encrypted and authenticated with the paired secret; nothing is sent in the clear.
 public sealed class AudioSender : IDisposable {
  public const int SampleRate = 48000, Channels = 2, FramesPerPacket = 240;
  const int PacketBytes = FramesPerPacket * Channels * 2;

  readonly PacketCodec codec;
  readonly IPacketSink sink;
  readonly LoopbackCapture capture = new LoopbackCapture();
  readonly byte[] pending = new byte[PacketBytes];
  readonly uint streamId;
  Resampler resampler;
  int filled;
  uint sequence;
  ulong timestamp;
  volatile bool paused;

  public long PacketsSent { get; private set; }

  // Where the stream sits on this PC's clock: the frame (on the packets' timeline) a device buffer
  // started at, and when Windows mixed it (QPC, µs). The phone measures its latency against it.
  public sealed class ClockSample { public long Frame, Micros; }
  volatile ClockSample clock;
  public ClockSample Clock { get { return clock; } }
  public string Format { get { return capture.FormatDescription; } }
  public event Action<Exception> Failed;

  public AudioSender(byte[] secret, IPEndPoint target) : this(secret, new UdpSink(target)) { }

  public AudioSender(byte[] secret, IPacketSink sink) {
   codec = new PacketCodec(secret);
   this.sink = sink;
   var id = new byte[4];
   using (var rng = new RNGCryptoServiceProvider()) rng.GetBytes(id);
   streamId = BitConverter.ToUInt32(id, 0);
   capture.Frames += OnFrames;
   capture.Failed += delegate(Exception error) { var handler = Failed; if (handler != null) handler(error); };
  }

  // Paused keeps the connection alive with silent packets; Gain scales the stream (0 = mute).
  public bool Paused { get { return paused; } set { paused = value; } }
  volatile float gain = 1f;
  public float Gain { get { return gain; } set { gain = Math.Max(0f, Math.Min(1f, value)); } }

  public void Start() {
   capture.Start();
   // The receiver always gets 48 kHz stereo, whatever the Windows mix format is.
   resampler = new Resampler(capture.SampleRate, capture.Channels);
  }

  void OnFrames(float[] input, int inputFrames, bool fromDevice) {
   if (resampler == null) return;
   long mixed = capture.BufferMicros;
   // A few a second is plenty; the resampler's own delay is under one frame.
   var last = clock;
   if (fromDevice && mixed > 0 && (last == null || mixed - last.Micros >= 250000 || mixed < last.Micros))
    clock = new ClockSample { Frame = (long)timestamp + filled / (Channels * 2), Micros = mixed };
   int frames;
   float[] samples = resampler.Process(input, inputFrames, out frames);
   int count = frames * Channels;
   float scale = paused ? 0f : gain;
   for (int i = 0; i < count; i++) {
    int v = (int)Math.Round(Math.Max(-1f, Math.Min(1f, samples[i] * scale)) * 32767);
    pending[filled++] = (byte)v;
    pending[filled++] = (byte)(v >> 8);
    if (filled == PacketBytes) Flush();
   }
  }

  void Flush() {
   filled = 0;
   ulong at = timestamp;
   timestamp += FramesPerPacket;
   bool silence = true;
   for (int i = 0; i < PacketBytes && silence; i++) silence = pending[i] == 0;
   byte[] packet = codec.Seal(streamId, sequence++, at, silence, pending, 0, PacketBytes);
   if (sink.Send(packet)) PacketsSent++;
  }

  public void Dispose() { capture.Dispose(); sink.Dispose(); codec.Dispose(); }
 }
}
