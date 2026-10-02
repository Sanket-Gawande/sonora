using System;
using System.Runtime.InteropServices;

namespace Sonora {
 // The default output device's own volume and mute, the ones the Windows volume flyout shows: the
 // notch's "This PC" row, and the phone's Mute PC. Sonora's capture taps the mix before them, so the
 // phone keeps playing while the PC's speakers are muted.
 public sealed class SystemVolume {
  static readonly Type EnumeratorType = Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"));

  [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  interface IMMDeviceEnumerator {
   [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
   [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
  }

  [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  interface IMMDevice {
   [PreserveSig] int Activate(ref Guid iid, int context, IntPtr parameters, [MarshalAs(UnmanagedType.IUnknown)] out object value);
   [PreserveSig] int OpenPropertyStore(int access, out IntPtr store);
   [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
  }

  [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  interface IAudioEndpointVolume {
   [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
   [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
   [PreserveSig] int GetChannelCount(out int count);
   [PreserveSig] int SetMasterVolumeLevel(float level, ref Guid context);
   [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid context);
   [PreserveSig] int GetMasterVolumeLevel(out float level);
   [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
   [PreserveSig] int SetChannelVolumeLevel(int channel, float level, ref Guid context);
   [PreserveSig] int SetChannelVolumeLevelScalar(int channel, float level, ref Guid context);
   [PreserveSig] int GetChannelVolumeLevel(int channel, out float level);
   [PreserveSig] int GetChannelVolumeLevelScalar(int channel, out float level);
   [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
   [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
  }

  IAudioEndpointVolume endpoint;
  string deviceId;
  DateTime resolved = DateTime.MinValue;

  // 0..100, or -1 when there's no output device.
  public double Level {
   get {
    var e = Endpoint();
    float level;
    return e != null && e.GetMasterVolumeLevelScalar(out level) == 0 ? Math.Round(level * 100) : -1;
   }
   set {
    var e = Endpoint();
    if (e == null) return;
    Guid context = Guid.Empty;
    e.SetMasterVolumeLevelScalar((float)(Math.Max(0, Math.Min(100, value)) / 100.0), ref context);
   }
  }

  public bool Muted {
   get {
    var e = Endpoint();
    bool muted;
    return e != null && e.GetMute(out muted) == 0 && muted;
   }
   set {
    var e = Endpoint();
    if (e == null) return;
    Guid context = Guid.Empty;
    e.SetMute(value, ref context);
   }
  }

  // The default device can change (headphones, HDMI): looked up again every 2 s.
  IAudioEndpointVolume Endpoint() {
   if (endpoint != null && (DateTime.UtcNow - resolved).TotalSeconds < 2) return endpoint;
   resolved = DateTime.UtcNow;
   try {
    var enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(EnumeratorType);
    IMMDevice device;
    if (enumerator.GetDefaultAudioEndpoint(0, 1, out device) != 0 || device == null) { endpoint = null; deviceId = null; return null; }
    string id;
    device.GetId(out id);
    if (id == deviceId && endpoint != null) return endpoint;
    Guid iid = typeof(IAudioEndpointVolume).GUID;
    object value;
    endpoint = device.Activate(ref iid, 23, IntPtr.Zero, out value) == 0 ? value as IAudioEndpointVolume : null;
    deviceId = id;
   } catch (COMException) {
    endpoint = null;
   } catch (InvalidCastException) {
    endpoint = null;
   }
   return endpoint;
  }
 }
}
