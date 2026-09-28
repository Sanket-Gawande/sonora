using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Sonora {
 static class Native {
  public const int GWL_EXSTYLE = -20;
  public const int WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
  public const int SPI_SETDESKWALLPAPER = 0x0014;
  public const int WM_HOTKEY = 0x0312, WM_DISPLAYCHANGE = 0x007E, WM_SETTINGCHANGE = 0x001A, WM_DPICHANGED = 0x02E0;
  public const uint SWP_NOACTIVATE = 0x10, SWP_NOZORDER = 0x4;
  public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
  public const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_NOREPEAT = 0x4000;
  public const uint EVENT_SYSTEM_FOREGROUND = 3, EVENT_OBJECT_LOCATIONCHANGE = 0x800B, WINEVENT_OUTOFCONTEXT = 0;
  public const int OBJID_WINDOW = 0;
  public const int WH_MOUSE_LL = 14;
  public const int WM_LBUTTONDOWN = 0x0201, WM_RBUTTONDOWN = 0x0204, WM_MBUTTONDOWN = 0x0207, WM_XBUTTONDOWN = 0x020B;
  public const uint WM_QUIT = 0x0012;

  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
  [StructLayout(LayoutKind.Sequential)] public struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public int dwFlags; }

  public delegate void WinEventProc(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
  public delegate IntPtr LowLevelMouseProc(int code, IntPtr wParam, IntPtr lParam);

  [StructLayout(LayoutKind.Sequential)] public struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData, flags, time; public IntPtr dwExtraInfo; }
  [StructLayout(LayoutKind.Sequential)] public struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public POINT pt; }

  [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr SetWindowsHookEx(int id, LowLevelMouseProc proc, IntPtr module, uint threadId);
  [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr hook);
  [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
  [DllImport("user32.dll")] public static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
  [DllImport("user32.dll")] public static extern bool PeekMessage(out MSG msg, IntPtr hwnd, uint min, uint max, uint remove);
  [DllImport("user32.dll")] public static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);
  [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string name);
  [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();

  [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hwnd, int index);
  [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr hwnd, int index, int value);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
  [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
  [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hwnd, int id);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
  [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hwnd);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
  [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr hwnd);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hwnd, StringBuilder name, int count);
  [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
  [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(POINT point, uint flags);
  [DllImport("user32.dll")] public static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
  [DllImport("user32.dll")] public static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint process, uint thread, uint flags);
  [DllImport("user32.dll")] public static extern bool UnhookWinEvent(IntPtr hook);
  [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
  [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

  public static void SetNoActivate(IntPtr hwnd, bool noActivate) {
   int style = GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_TOOLWINDOW;
   style = noActivate ? style | WS_EX_NOACTIVATE : style & ~WS_EX_NOACTIVATE;
   SetWindowLong(hwnd, GWL_EXSTYLE, style);
  }

  public static uint DpiAt(int x, int y) {
   var point = new POINT { X = x, Y = y };
   IntPtr monitor = MonitorFromPoint(point, 2);
   uint dpiX, dpiY;
   try { if (GetDpiForMonitor(monitor, 0, out dpiX, out dpiY) == 0) return dpiX; }
   catch (DllNotFoundException) { } catch (EntryPointNotFoundException) { }
   return 96;
  }

  static bool IsCloaked(IntPtr hwnd) {
   int cloaked;
   try { return DwmGetWindowAttribute(hwnd, 14, out cloaked, 4) == 0 && cloaked != 0; }
   catch (DllNotFoundException) { return false; }
  }

  // True when the foreground app covers the whole monitor this window is on.
  public static bool ForegroundIsFullscreenOn(IntPtr ownWindow) {
   IntPtr foreground = GetForegroundWindow();
   if (foreground == IntPtr.Zero || foreground == ownWindow || !IsWindowVisible(foreground) || IsCloaked(foreground)) return false;
   // A maximized window can cover the whole monitor when the taskbar auto-hides; that isn't fullscreen.
   if (IsZoomed(foreground)) return false;
   var name = new StringBuilder(64);
   GetClassName(foreground, name, name.Capacity);
   string cls = name.ToString();
   if (cls == "Progman" || cls == "WorkerW" || cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd") return false;
   IntPtr monitor = MonitorFromWindow(foreground, 2);
   if (monitor != MonitorFromWindow(ownWindow, 2)) return false;
   var info = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
   RECT rect;
   if (!GetMonitorInfo(monitor, ref info) || !GetWindowRect(foreground, out rect)) return false;
   return rect.Left <= info.rcMonitor.Left && rect.Top <= info.rcMonitor.Top && rect.Right >= info.rcMonitor.Right && rect.Bottom >= info.rcMonitor.Bottom;
  }
 }
}
