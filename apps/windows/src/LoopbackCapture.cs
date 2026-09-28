using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace Sonora {
 // Captures what the default output device is playing (WASAPI shared-mode loopback) and hands
 // out interleaved float frames. Windows sends no packets while nothing plays, so the reader
 // fills those gaps with silence to keep the stream's clock continuous.
 public sealed class LoopbackCapture : IDisposable {
  // Created by CLSID and cast to the interface (a QueryInterface), so two classes wrapping the
  // same COM object never clash over the wrapper type.
  static readonly Type EnumeratorType = Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"));

  [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  interface IMMDeviceEnumerator {
   [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
   [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
  }

  [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  interface IMMDevice {
   [PreserveSig] int Activate(ref Guid iid, int context, IntPtr parameters, [MarshalAs(UnmanagedType.IUnknown)] out object value);
  }

  [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  interface IAudioClient {
   [PreserveSig] int Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
   [PreserveSig] int GetBufferSize(out uint frames);
   [PreserveSig] int GetStreamLatency(out long latency);
   [PreserveSig] int GetCurrentPadding(out uint frames);
   [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
   [PreserveSig] int GetMixFormat(out IntPtr format);
   [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
   [PreserveSig] int Start();
   [PreserveSig] int Stop();
   [PreserveSig] int Reset();
   [PreserveSig] int SetEventHandle(IntPtr handle);
   [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
  }

  [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  interface IAudioCaptureClient {
   [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
   [PreserveSig] int ReleaseBuffer(uint frames);
   [PreserveSig] int GetNextPacketSize(out uint frames);
  }

  [DllImport("ole32.dll")] static extern void CoTaskMemFree(IntPtr pointer);

  const int Loopback = 0x00020000, SilentFlag = 0x2, TimestampErrorFlag = 0x4;
  const ushort FormatFloat = 3, FormatExtensible = 0xFFFE;
  static readonly Guid FloatSubtype = new Guid("00000003-0000-0010-8000-00aa00389b71");

  IAudioClient client;
  IAudioCaptureClient capture;
  Thread thread;
  volatile bool running;
  bool isFloat;
  int bitsPerSample;
  short[] pcm16 = new short[0];
  int[] pcm32 = new int[0];

  public int SampleRate { get; private set; }
  public int Channels { get; private set; }
  public string FormatDescription { get; private set; }
  public long DeviceBufferFrames { get; private set; }
  public double DevicePeriodMs { get; private set; }

  // Called on the capture thread with interleaved samples in -1..1, the frame count, and
  // whether the frames were real device packets (false for gap-filling silence).
  public event Action<float[], int, bool> Frames;
  // When Windows mixed the first frame of the buffer a Frames handler is being given, on the QPC
  // clock in microseconds (the device's own timestamp); 0 for gap-filling silence or a bad stamp.
  public long BufferMicros { get; private set; }
  public event Action<Exception> Failed;

  public void Start() {
   var enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(EnumeratorType);
   IMMDevice device;
   Check(enumerator.GetDefaultAudioEndpoint(0, 1, out device), "No default output device");
   Guid iid = typeof(IAudioClient).GUID;
   object value;
   Check(device.Activate(ref iid, 23, IntPtr.Zero, out value), "Could not open the output device");
   client = (IAudioClient)value;

   IntPtr format;
   Check(client.GetMixFormat(out format), "Could not read the mix format");
   try {
    ReadFormat(format);
    // 100 ms shared buffer; loopback latency is set by the engine period, not this size.
    Check(client.Initialize(0, Loopback, 1000000, 0, format, IntPtr.Zero), "Could not start loopback capture");
   } finally { CoTaskMemFree(format); }

   uint bufferFrames;
   client.GetBufferSize(out bufferFrames);
   DeviceBufferFrames = bufferFrames;
   long period, minimum;
   if (client.GetDevicePeriod(out period, out minimum) >= 0) DevicePeriodMs = period / 10000.0;
   Guid captureId = typeof(IAudioCaptureClient).GUID;
   object service;
   Check(client.GetService(ref captureId, out service), "Could not get the capture service");
   capture = (IAudioCaptureClient)service;
   Check(client.Start(), "Could not start the audio client");
   running = true;
   thread = new Thread(Run) { IsBackground = true, Name = "Sonora loopback capture", Priority = ThreadPriority.AboveNormal };
   thread.Start();
  }

  void ReadFormat(IntPtr format) {
   ushort tag = (ushort)Marshal.ReadInt16(format, 0);
   Channels = Marshal.ReadInt16(format, 2);
   SampleRate = Marshal.ReadInt32(format, 4);
   bitsPerSample = Marshal.ReadInt16(format, 14);
   isFloat = tag == FormatFloat;
   if (tag == FormatExtensible) {
    var subtype = new byte[16];
    Marshal.Copy(new IntPtr(format.ToInt64() + 24), subtype, 0, 16);
    isFloat = new Guid(subtype) == FloatSubtype;
   }
   if (!isFloat && bitsPerSample != 16 && bitsPerSample != 32) throw new NotSupportedException("Unsupported mix format: " + bitsPerSample + "-bit integer");
   FormatDescription = SampleRate + " Hz · " + Channels + " ch · " + bitsPerSample + "-bit " + (isFloat ? "float" : "PCM");
  }

  void Run() {
   var buffer = new float[0];
   var clock = Stopwatch.StartNew();
   long delivered = 0;
   try {
    while (running) {
     uint next;
     Check(capture.GetNextPacketSize(out next), "Capture failed");
     if (next == 0) {
      // No packets while the device is idle: emit silence so the receiver's clock keeps running.
      // Loopback packets trail real time by about one engine period, so don't pad ahead of them.
      long expected = (long)(clock.Elapsed.TotalSeconds * SampleRate) - SampleRate / 50;
      if (expected - delivered >= SampleRate / 100) {
       int gap = (int)Math.Min(expected - delivered, SampleRate / 10);
       BufferMicros = 0;
       Emit(ref buffer, IntPtr.Zero, gap, true, false);
       delivered += gap;
      }
      Thread.Sleep(3);
      continue;
     }
     IntPtr data; uint frames, flags; ulong position, qpc;
     Check(capture.GetBuffer(out data, out frames, out flags, out position, out qpc), "Capture failed");
     // The position is in 100 ns units of the performance counter.
     BufferMicros = (flags & TimestampErrorFlag) == 0 ? (long)(qpc / 10) : 0;
     Emit(ref buffer, data, (int)frames, (flags & SilentFlag) != 0, true);
     delivered += frames;
     capture.ReleaseBuffer(frames);
    }
   } catch (Exception error) {
    running = false;
    var handler = Failed;
    if (handler != null) handler(error);
   }
  }

  void Emit(ref float[] buffer, IntPtr data, int frames, bool silent, bool fromDevice) {
   int count = frames * Channels;
   if (buffer.Length < count) buffer = new float[count];
   if (silent || data == IntPtr.Zero) Array.Clear(buffer, 0, count);
   else if (isFloat) Marshal.Copy(data, buffer, 0, count);
   else if (bitsPerSample == 16) {
    if (pcm16.Length < count) pcm16 = new short[count];
    Marshal.Copy(data, pcm16, 0, count);
    for (int i = 0; i < count; i++) buffer[i] = pcm16[i] / 32768f;
   } else {
    if (pcm32.Length < count) pcm32 = new int[count];
    Marshal.Copy(data, pcm32, 0, count);
    for (int i = 0; i < count; i++) buffer[i] = pcm32[i] / 2147483648f;
   }
   var handler = Frames;
   if (handler != null) handler(buffer, frames, fromDevice);
  }

  static void Check(int hr, string message) { if (hr < 0) throw new InvalidOperationException(message + " (0x" + hr.ToString("X8") + ")"); }

  public void Stop() {
   running = false;
   if (thread != null) thread.Join(500);
   if (client != null) client.Stop();
  }

  public void Dispose() { Stop(); capture = null; client = null; }
 }
}
