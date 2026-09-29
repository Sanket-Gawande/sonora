using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace Sonora {
 // A "Sonora" entry in the Start menu, so after a restart Sonora is one search away without being
 // a startup app. It points at the copy that ran last, so moving the folder or unzipping a newer
 // release updates it on the next launch. Per user: no installer, no elevation.
 public static class Launcher {
  static readonly Type ShellLinkType = Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046"));
  const string Description = "Play your PC's audio on your Android phone";

  [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  interface IShellLinkW {
   void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int length, IntPtr data, int flags);
   void GetIDList(out IntPtr list);
   void SetIDList(IntPtr list);
   void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int length);
   void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
   void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory, int length);
   void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
   void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int length);
   void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
   void GetHotkey(out short hotkey);
   void SetHotkey(short hotkey);
   void GetShowCmd(out int show);
   void SetShowCmd(int show);
   void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int length, out int index);
   void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
   void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, int reserved);
   void Resolve(IntPtr window, int flags);
   void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
  }

  public static string ShortcutPath {
   get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Sonora.lnk"); }
  }

  // Creates the entry, or points it here if it leads somewhere else. Returns true when it wrote one.
  public static bool Install() {
   return Install(ShortcutPath, System.Reflection.Assembly.GetExecutingAssembly().Location);
  }

  internal static bool Install(string path, string exe) {
   if (string.Equals(TargetOf(path), exe, StringComparison.OrdinalIgnoreCase)) return false;
   var link = (IShellLinkW)Activator.CreateInstance(ShellLinkType);
   try {
    link.SetPath(exe);
    link.SetWorkingDirectory(Path.GetDirectoryName(exe));
    link.SetDescription(Description);
    link.SetIconLocation(exe, 0);
    Directory.CreateDirectory(Path.GetDirectoryName(path));
    ((IPersistFile)link).Save(path, true);
   } finally { Marshal.ReleaseComObject(link); }
   return true;
  }

  internal static string TargetOf(string path) {
   if (!File.Exists(path)) return null;
   var link = (IShellLinkW)Activator.CreateInstance(ShellLinkType);
   try {
    ((IPersistFile)link).Load(path, 0);
    var target = new StringBuilder(1024);
    link.GetPath(target, target.Capacity, IntPtr.Zero, 0x4); // SLGP_RAWPATH: as stored, not resolved
    return target.ToString();
   } catch (COMException) {
    return null;
   } finally { Marshal.ReleaseComObject(link); }
  }
 }
}
