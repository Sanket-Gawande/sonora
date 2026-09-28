using System;
using System.Runtime.InteropServices;

namespace Sonora {
 // Reads the real peak level of the default Windows output device, so the waveform
 // reflects actual desktop audio instead of a decorative loop.
 public sealed class AudioMeter : IDisposable {
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
   [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore store);
   [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
  }

  [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  interface IAudioMeterInformation {
   [PreserveSig] int GetPeakValue(out float peak);
  }

  [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  interface IPropertyStore {
   [PreserveSig] int GetCount(out int count);
   [PreserveSig] int GetAt(int index, out PropertyKey key);
   [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
  }

  [StructLayout(LayoutKind.Sequential)] struct PropertyKey { public Guid FormatId; public int PropertyId; }
  [StructLayout(LayoutKind.Sequential)] struct PropVariant { public ushort Type; ushort r1, r2, r3; public IntPtr Pointer; IntPtr extra; }
  [DllImport("ole32.dll")] static extern int PropVariantClear(ref PropVariant value);

  IMMDeviceEnumerator enumerator;
  IAudioMeterInformation meter;
  string deviceId;
  DateTime lastRefresh = DateTime.MinValue;

  public string DeviceName { get; private set; }

  public AudioMeter() {
   DeviceName = "No output device";
   try { enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(EnumeratorType); }
   catch (COMException) { enumerator = null; }
   Refresh();
  }

  // The default device can change at any time (headphones, HDMI), so re-resolve it periodically.
  public void RefreshIfStale() {
   if ((DateTime.UtcNow - lastRefresh).TotalSeconds >= 2) Refresh();
  }

  void Refresh() {
   lastRefresh = DateTime.UtcNow;
   if (enumerator == null) return;
   IMMDevice device;
   if (enumerator.GetDefaultAudioEndpoint(0, 1, out device) != 0 || device == null) {
    meter = null; deviceId = null; DeviceName = "No output device";
    return;
   }
   string id;
   device.GetId(out id);
   if (id == deviceId && meter != null) return;
   deviceId = id;
   Guid iid = typeof(IAudioMeterInformation).GUID;
   object value;
   meter = device.Activate(ref iid, 23, IntPtr.Zero, out value) == 0 ? value as IAudioMeterInformation : null;
   DeviceName = ReadFriendlyName(device) ?? "Default output";
  }

  static string ReadFriendlyName(IMMDevice device) {
   IPropertyStore store;
   if (device.OpenPropertyStore(0, out store) != 0 || store == null) return null;
   var key = new PropertyKey { FormatId = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), PropertyId = 14 };
   PropVariant value;
   if (store.GetValue(ref key, out value) != 0) return null;
   string name = value.Type == 31 ? Marshal.PtrToStringUni(value.Pointer) : null;
   PropVariantClear(ref value);
   return name;
  }

  // Peak of the current output mix, 0..1.
  public double Peak() {
   if (meter == null) return 0;
   float peak;
   try { return meter.GetPeakValue(out peak) == 0 ? peak : 0; }
   catch (COMException) { meter = null; return 0; }
  }

  public void Dispose() { meter = null; enumerator = null; }
 }
}
