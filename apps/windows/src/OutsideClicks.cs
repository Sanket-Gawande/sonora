using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Sonora {
 // Mouse presses anywhere on screen while the notch is open, so a click outside closes it the way
 // a menu closes. The notch never takes focus, so Windows never tells it it lost focus.
 // The low-level hook is installed only while the notch is open, on a thread of its own: it never
 // waits on the UI thread (a busy UI thread would otherwise stall the mouse system-wide), and it
 // only reports the point, in physical pixels, then passes the press on untouched.
 sealed class OutsideClicks {
  readonly Action<int, int> pressed;
  Thread thread;
  uint threadId;
  Native.LowLevelMouseProc proc;

  public OutsideClicks(Action<int, int> pressed) { this.pressed = pressed; proc = Hook; }

  public void Start() {
   if (thread != null) return;
   var ready = new ManualResetEvent(false);
   thread = new Thread(delegate() {
    Native.MSG msg;
    // Make sure this thread has a message queue before Stop can post to it.
    Native.PeekMessage(out msg, IntPtr.Zero, 0, 0, 0);
    threadId = Native.GetCurrentThreadId();
    IntPtr hook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, proc, Native.GetModuleHandle(null), 0);
    ready.Set();
    while (Native.GetMessage(out msg, IntPtr.Zero, 0, 0) > 0) { }
    if (hook != IntPtr.Zero) Native.UnhookWindowsHookEx(hook);
   }) { IsBackground = true, Name = "Sonora outside clicks" };
   thread.Start();
   ready.WaitOne(1000);
  }

  public void Stop() {
   if (thread == null) return;
   Native.PostThreadMessage(threadId, Native.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
   thread = null;
  }

  IntPtr Hook(int code, IntPtr wParam, IntPtr lParam) {
   if (code >= 0) {
    int message = wParam.ToInt32();
    if (message == Native.WM_LBUTTONDOWN || message == Native.WM_RBUTTONDOWN || message == Native.WM_MBUTTONDOWN || message == Native.WM_XBUTTONDOWN) {
     var info = (Native.MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(Native.MSLLHOOKSTRUCT));
     pressed(info.pt.X, info.pt.Y);
    }
   }
   return Native.CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
  }
 }
}
