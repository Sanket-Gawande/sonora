using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Automation;

namespace Sonora {
 // Where the music is actually playing. Windows' media session names the app, never the tab, so a
 // browser's tab is found in the browser's own accessibility tree: the tab whose name holds the
 // track's title, or that the browser marks as playing audio. The notch brings that tab forward;
 // the phone gets its link (docs/protocol.md, LINK). Only ever on request, because asking wakes the
 // browser's accessibility support; always off the UI thread, and given up after a few seconds.
 public static class PlayingApp {
  sealed class Browser {
   public readonly string Key, Process, WindowClass;
   public Browser(string key, string process, string windowClass) { Key = key; Process = process; WindowClass = windowClass; }
  }

  // Chromium's window class is shared with every Electron app, so windows are matched by process too.
  static readonly Browser[] Browsers = {
   new Browser("chrome", "chrome", "Chrome_WidgetWin_1"),
   new Browser("msedge", "msedge", "Chrome_WidgetWin_1"),
   new Browser("brave", "brave", "Chrome_WidgetWin_1"),
   new Browser("opera", "opera", "Chrome_WidgetWin_1"),
   new Browser("vivaldi", "vivaldi", "Chrome_WidgetWin_1"),
   new Browser("firefox", "firefox", "MozillaWindowClass"),
  };
  static readonly Browser Firefox = Browsers[Browsers.Length - 1];
  // Firefox's media sessions carry its install hash as the app ID, not a name.
  static readonly Regex FirefoxId = new Regex("^[0-9A-Fa-f]{16}$");
  // How Chromium names a tab that's making sound (English; other languages rely on the title).
  static readonly string[] AudibleMarks = { "Audio playing", "Playing audio" };
  static readonly Condition TabItems = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem);
  const int Budget = 4000;

  sealed class Tab { public IntPtr Window; public AutomationElement Element, AddressBar; public int Score; public bool Selected; }

  // Brings the playing app forward; for a browser, switches to the playing tab first.
  public static bool Show(NowPlaying now) {
   if (now == null || string.IsNullOrEmpty(now.AppId)) return false;
   return Within<bool>(delegate { return ShowNow(now); }, false);
  }

  // The playing tab's link, when that tab is the one showing in its window (only that tab's address
  // is on screen to read). Null otherwise, and for apps that aren't browsers.
  public static string Link(NowPlaying now) {
   if (now == null || string.IsNullOrEmpty(now.AppId)) return null;
   return Within<string>(delegate { return LinkNow(now); }, null);
  }

  public static bool IsBrowser(string appId) { return BrowserFor(appId) != null; }

  static bool ShowNow(NowPlaying now) {
   var browser = BrowserFor(now.AppId);
   if (browser != null) {
    var windows = WindowsOf(browser);
    if (windows.Count == 0) return false;
    var tab = FindTab(windows, now.Title);
    if (tab == null) return Forward(windows[0]); // the frontmost window
    object pattern;
    if (!tab.Selected && tab.Element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out pattern)) ((SelectionItemPattern)pattern).Select();
    return Forward(tab.Window);
   }
   // A Store app (Media Player and the like) is brought forward by its app ID.
   if (now.AppId.IndexOf('!') > 0) return Activate(now.AppId);
   IntPtr window = MainWindowOf(ProcessName(now.AppId));
   return window != IntPtr.Zero && Forward(window);
  }

  static string LinkNow(NowPlaying now) {
   var browser = BrowserFor(now.AppId);
   if (browser == null) return null;
   var tab = FindTab(WindowsOf(browser), now.Title);
   if (tab == null || !tab.Selected || tab.AddressBar == null) return null;
   object pattern;
   if (!tab.AddressBar.TryGetCurrentPattern(ValuePattern.Pattern, out pattern)) return null;
   string url = Normalize(((ValuePattern)pattern).Current.Value);
   return url == null ? null : WithPosition(url, now.PositionNow);
  }

  static Browser BrowserFor(string appId) {
   if (string.IsNullOrEmpty(appId)) return null;
   if (FirefoxId.IsMatch(appId)) return Firefox;
   string id = appId.ToLowerInvariant();
   foreach (var browser in Browsers) if (id.Contains(browser.Key)) return browser;
   return null;
  }

  // The browser's top-level windows, frontmost first.
  static List<IntPtr> WindowsOf(Browser browser) {
   var processes = new HashSet<uint>();
   foreach (var process in Process.GetProcessesByName(browser.Process)) { processes.Add((uint)process.Id); process.Dispose(); }
   var found = new List<IntPtr>();
   var name = new StringBuilder(64);
   Native.EnumWindows(delegate(IntPtr hwnd, IntPtr data) {
    uint id;
    if (!Native.IsWindowVisible(hwnd) || Native.GetWindowTextLength(hwnd) == 0) return true;
    Native.GetWindowThreadProcessId(hwnd, out id);
    if (!processes.Contains(id)) return true;
    name.Length = 0;
    Native.GetClassName(hwnd, name, name.Capacity);
    if (name.ToString() == browser.WindowClass) found.Add(hwnd);
    return true;
   }, IntPtr.Zero);
   return found;
  }

  static Tab FindTab(List<IntPtr> windows, string title) {
   Tab best = null;
   foreach (var hwnd in windows) {
    try {
     AutomationElement strip, addressBar;
     BrowserControls(AutomationElement.FromHandle(hwnd), out strip, out addressBar);
     if (strip == null) continue;
     foreach (AutomationElement item in strip.FindAll(TreeScope.Descendants, TabItems)) {
      int score = Score(item.Current.Name, title);
      if (score == 0 || (best != null && score <= best.Score)) continue;
      object pattern;
      bool selected = item.TryGetCurrentPattern(SelectionItemPattern.Pattern, out pattern) && ((SelectionItemPattern)pattern).Current.IsSelected;
      best = new Tab { Window = hwnd, Element = item, AddressBar = addressBar, Score = score, Selected = selected };
     }
    } catch (Exception) {
     // A window closing, or a browser too busy to answer: try the next one.
    }
   }
   return best;
  }

  // The browser's own controls, never the page's: the first tab list and the first text field (the
  // address bar), found without walking into any web page. An app window has no address bar.
  static void BrowserControls(AutomationElement window, out AutomationElement strip, out AutomationElement addressBar) {
   strip = null; addressBar = null;
   var walker = TreeWalker.ControlViewWalker;
   var queue = new Queue<AutomationElement>();
   queue.Enqueue(window);
   int seen = 0;
   while (queue.Count > 0 && seen++ < 2000 && (strip == null || addressBar == null)) {
    var element = queue.Dequeue();
    var type = element.Current.ControlType;
    if (type == ControlType.Document) continue;
    if (type == ControlType.Tab) { if (strip == null) strip = element; continue; }
    if (type == ControlType.Edit) { if (addressBar == null) addressBar = element; continue; }
    for (var child = walker.GetFirstChild(element); child != null; child = walker.GetNextSibling(child)) queue.Enqueue(child);
   }
  }

  // 0: not it. A tab that holds the track's title outranks one merely marked as playing audio.
  internal static int Score(string tab, string title) {
   if (string.IsNullOrEmpty(tab)) return 0;
   int score = 0;
   foreach (var mark in AudibleMarks) if (tab.IndexOf(mark, StringComparison.OrdinalIgnoreCase) >= 0) { score = 1; break; }
   string t = (title ?? "").Trim();
   if (t.Length >= 3 && tab.IndexOf(t.Length > 24 ? t.Substring(0, 24) : t, StringComparison.OrdinalIgnoreCase) >= 0) score += 2;
   return score;
  }

  // What an address bar shows, as a link the phone may open: http or https only. Chromium hides
  // the scheme, so a bare "youtube.com/watch?v=…" is taken as https.
  internal static string Normalize(string text) {
   if (text == null) return null;
   text = text.Trim();
   if (text.Length == 0 || text.Length > 2048 || text.IndexOf(' ') >= 0) return null;
   if (text.IndexOf("://", StringComparison.Ordinal) < 0) text = "https://" + text;
   Uri uri;
   if (!Uri.TryCreate(text, UriKind.Absolute, out uri)) return null;
   if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;
   if (uri.Host.IndexOf('.') < 0) return null; // localhost and the like mean nothing on the phone
   return uri.AbsoluteUri;
  }

  // A YouTube video link carries the position, so the phone carries on where the PC was.
  internal static string WithPosition(string url, TimeSpan position) {
   Uri uri;
   if (!Uri.TryCreate(url, UriKind.Absolute, out uri)) return url;
   string host = uri.Host.ToLowerInvariant();
   bool video = host == "youtu.be"
    || ((host == "youtube.com" || host == "www.youtube.com" || host == "m.youtube.com") && uri.AbsolutePath == "/watch");
   if (!video || position.TotalSeconds < 5) return url;
   var query = new List<string>();
   foreach (var pair in uri.Query.TrimStart('?').Split('&')) if (pair.Length > 0 && !pair.StartsWith("t=")) query.Add(pair);
   query.Add("t=" + (long)position.TotalSeconds + "s");
   return new UriBuilder(uri) { Query = string.Join("&", query.ToArray()) }.Uri.AbsoluteUri;
  }

  static string ProcessName(string appId) {
   string name = appId;
   int slash = name.LastIndexOfAny(new[] { '\\', '/' });
   if (slash >= 0) name = name.Substring(slash + 1);
   if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 4);
   return name;
  }

  static IntPtr MainWindowOf(string process) {
   IntPtr window = IntPtr.Zero;
   foreach (var p in Process.GetProcessesByName(process)) {
    if (window == IntPtr.Zero) window = p.MainWindowHandle;
    p.Dispose();
   }
   return window;
  }

  static bool Forward(IntPtr hwnd) {
   if (Native.IsIconic(hwnd)) Native.ShowWindow(hwnd, 9); // SW_RESTORE
   if (Native.SetForegroundWindow(hwnd)) return true;
   // Windows hands the foreground only to the app the user last touched; borrow the current
   // foreground window's input state for the moment it takes.
   uint process, mine = Native.GetCurrentThreadId();
   uint theirs = Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out process);
   bool attached = theirs != 0 && theirs != mine && Native.AttachThreadInput(mine, theirs, true);
   try {
    Native.BringWindowToTop(hwnd);
    return Native.SetForegroundWindow(hwnd);
   } finally {
    if (attached) Native.AttachThreadInput(mine, theirs, false);
   }
  }

  [ComImport, Guid("2e941141-7f97-4756-ba1d-9decde894a3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  interface IApplicationActivationManager {
   [PreserveSig] int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId, [MarshalAs(UnmanagedType.LPWStr)] string arguments, int options, out uint processId);
  }
  static readonly Type ActivationManager = Type.GetTypeFromCLSID(new Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C"));

  static bool Activate(string appId) {
   var manager = (IApplicationActivationManager)Activator.CreateInstance(ActivationManager);
   try {
    uint process;
    return manager.ActivateApplication(appId, null, 0, out process) >= 0;
   } finally { Marshal.ReleaseComObject(manager); }
  }

  static T Within<T>(Func<T> work, T otherwise) {
   T result = otherwise;
   var thread = new Thread(delegate() {
    try { result = work(); } catch (Exception) { }
   }) { IsBackground = true, Name = "Sonora playing app" };
   thread.SetApartmentState(ApartmentState.MTA);
   thread.Start();
   return thread.Join(Budget) ? result : otherwise;
  }
 }
}
