using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Path = System.Windows.Shapes.Path;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace Sonora {
 public enum NotchView { Idle, Compact, Home, Pairing, Settings }

 // A transparent, click-through host pinned to the top edge of one display. The window is kept
 // just big enough for the notch (plus slack for the spring and the hover lean), and rendered in
 // software, so each redraw of this layered window is cheap and never waits on the GPU.
 public sealed partial class NotchWindow : Window {
  const double TestWidth = 720, TestHeight = 300;
  const int HotkeyId = 0x534E;

  readonly Session session;
  readonly MediaSession media;
  readonly Preferences prefs;
  readonly AudioMeter meter;
  readonly bool testing;
  readonly DispatcherTimer collapseTimer, meterTimer, fullscreenTimer, wallpaperTimer, progressTimer;
  readonly OutsideClicks outsideClicks;
  int outsideCollapseAt;
  Native.WinEventProc winEventProc;
  IntPtr hwnd, foregroundHook, previousForeground;
  string wallpaperFingerprint;

  Grid root;
  Border notch;
  Path earLeft, earRight, outline;
  Canvas stage;
  TranslateTransform slide;
  FrameworkElement current;
  string currentSignature;
  NotchView view = NotchView.Idle;
  bool expanded, settingsOpen, keyboardMode, hovering, hiddenByUser, hiddenForFullscreen;
  // Opened by the hotkey, tray or a second launch while a fullscreen app was in front.
  bool summonedOverFullscreen;
  double maxNotchWidth = 640, hostWidth = HostWidthFor(640), hostHeight = 40;
  // True while the notch animates between sizes; the hover lean waits so it can't cut in.
  bool sizing;
  int sizeGeneration;
  Native.RECT placed;
  bool hasPlaced;

  public bool HotkeyAvailable { get; private set; }
  public NotchView View { get { return view; } }
  public bool IsExpanded { get { return expanded; } }

  public NotchWindow(Session session, MediaSession media, Preferences prefs, AudioMeter meter, bool testing) {
   this.session = session; this.media = media; this.prefs = prefs; this.meter = meter; this.testing = testing;
   Title = "Sonora";
   WindowStyle = WindowStyle.None;
   AllowsTransparency = true;
   Background = Brushes.Transparent;
   ResizeMode = ResizeMode.NoResize;
   ShowInTaskbar = false;
   ShowActivated = false;
   Topmost = true;
   Width = testing ? TestWidth : hostWidth;
   Height = testing ? TestHeight : hostHeight;
   FontFamily = Ui.Font; FontSize = 13; Foreground = Ui.Text;
   UseLayoutRounding = true;
   // Ideal keeps Mona Sans's real outlines; Display mode snaps glyphs to the pixel grid, which
   // looks blocky on a transparent window (no ClearType) at fractional scaling.
   TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
   TextOptions.SetTextRenderingMode(this, TextRenderingMode.Grayscale);
   Ui.ReduceMotion = ReduceMotion;

   BuildShell();

   outsideClicks = new OutsideClicks(delegate(int x, int y) { Dispatcher.BeginInvoke(new Action(delegate { OnPressAnywhere(x, y); })); });
   collapseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2500) };
   collapseTimer.Tick += delegate { collapseTimer.Stop(); if (!hovering && !keyboardMode && view != NotchView.Pairing && Mouse.Captured == null) Collapse(); };
   // 20 fps is plenty for a level meter and halves the redraws of the old 30 fps.
   meterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
   meterTimer.Tick += delegate { MeterTick(); };
   progressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
   progressTimer.Tick += delegate { UpdateProgress(); };
   fullscreenTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
   fullscreenTimer.Tick += delegate { CheckFullscreen(); };
   // Slideshows change the wallpaper without telling anyone, so look every 20 s (a registry read and a file time).
   wallpaperTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
   wallpaperTimer.Tick += delegate { CheckWallpaper(); };

   session.Changed += delegate { Render(); };
   media.Changed += delegate { Render(); };
   KeyDown += OnKeyDown;
   Deactivated += delegate { if (keyboardMode) Collapse(); };
   SourceInitialized += OnSourceInitialized;
   IsVisibleChanged += delegate { UpdateTimers(); };
   Closed += delegate { Shutdown(); };
   ApplyTheme();
  }

  public bool ReduceMotion {
   get { return prefs.ReduceMotion.HasValue ? prefs.ReduceMotion.Value : !SystemParameters.ClientAreaAnimation; }
  }

  // ---------- theme ----------
  // Settings › Tint notch to wallpaper. The image is decoded off the UI thread.
  public void ApplyTheme() {
   if (testing) { RefreshTheme(); return; }
   wallpaperFingerprint = WallpaperTheme.Fingerprint();
   if (!prefs.MatchWallpaper) { Ui.ApplySurface(null); RefreshTheme(); return; }
   ThreadPool.QueueUserWorkItem(delegate {
    var tone = WallpaperTheme.FromCurrentWallpaper();
    Dispatcher.BeginInvoke(new Action(delegate {
     if (!prefs.MatchWallpaper) return;
     Ui.ApplySurface(tone);
     RefreshTheme();
    }));
   });
  }

  public void RefreshTheme() {
   notch.Background = Ui.Ink;
   earLeft.Fill = Ui.Ink;
   earRight.Fill = Ui.Ink;
   outline.Stroke = Ui.Edge;
   currentSignature = null;
   Render();
  }

  void CheckWallpaper() { if (WallpaperTheme.Fingerprint() != wallpaperFingerprint) ApplyTheme(); }

  // ---------- shape ----------
  void BuildShell() {
   slide = new TranslateTransform();
   root = new Grid { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, RenderTransform = slide };
   root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
   root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
   root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });

   // Concave "ears" blend the flat top into the screen edge.
   earLeft = new Path { Data = Geometry.Parse("M0,0 L12,0 L12,12 A12,12 0 0 0 0,0 Z"), Fill = Ui.Ink, VerticalAlignment = VerticalAlignment.Top };
   earRight = new Path { Data = Geometry.Parse("M12,0 L0,0 L0,12 A12,12 0 0 1 12,0 Z"), Fill = Ui.Ink, VerticalAlignment = VerticalAlignment.Top };
   Grid.SetColumn(earRight, 2);

   notch = new Border { Background = Ui.Ink, VerticalAlignment = VerticalAlignment.Top, Width = 132, Height = 28 };
   Grid.SetColumn(notch, 1);
   stage = new Canvas { ClipToBounds = false };
   notch.Child = stage;
   notch.SizeChanged += delegate { UpdateShape(); };
   notch.MouseEnter += delegate { hovering = true; collapseTimer.Stop(); Peek(true); };
   notch.MouseLeave += delegate { hovering = false; Peek(false); if (expanded && !keyboardMode) collapseTimer.Start(); };

   // One hairline traced down the ears, the sides and along the bottom; the top is the screen edge.
   outline = new Path { Stroke = Ui.Edge, StrokeThickness = 1, IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left };
   Grid.SetColumnSpan(outline, 3);

   root.Children.Add(earLeft);
   root.Children.Add(notch);
   root.Children.Add(earRight);
   root.Children.Add(outline);
   Content = root;
  }

  // Corner radius follows the height so the shape morphs smoothly: 13 idle, 16 compact, 30 open.
  static double RadiusFor(double h) {
   if (h <= 28) return 13;
   if (h <= 36) return 13 + (h - 28) / 8 * 3;
   return Math.Min(30, 16 + (h - 36) / 124 * 14);
  }

  void UpdateShape() {
   double w = notch.ActualWidth, h = notch.ActualHeight, r = Math.Min(RadiusFor(h), Math.Min(w, h) / 2);
   notch.CornerRadius = new CornerRadius(0, 0, r, r);
   var clip = new StreamGeometry();
   using (var ctx = clip.Open()) {
    ctx.BeginFigure(new Point(0, 0), true, true);
    ctx.LineTo(new Point(w, 0), false, false);
    ctx.LineTo(new Point(w, h - r), false, false);
    ctx.ArcTo(new Point(w - r, h), new Size(r, r), 0, false, SweepDirection.Clockwise, false, false);
    ctx.LineTo(new Point(r, h), false, false);
    ctx.ArcTo(new Point(0, h - r), new Size(r, r), 0, false, SweepDirection.Clockwise, false, false);
   }
   clip.Freeze();
   notch.Clip = clip;

   var edge = new StreamGeometry();
   using (var ctx = edge.Open()) {
    ctx.BeginFigure(new Point(0, 0), false, false);
    ctx.ArcTo(new Point(12, 12), new Size(12, 12), 0, false, SweepDirection.Clockwise, true, true);
    ctx.LineTo(new Point(12, h - r), true, true);
    ctx.ArcTo(new Point(12 + r, h), new Size(r, r), 0, false, SweepDirection.Counterclockwise, true, true);
    ctx.LineTo(new Point(12 + w - r, h), true, true);
    ctx.ArcTo(new Point(12 + w, h - r), new Size(r, r), 0, false, SweepDirection.Counterclockwise, true, true);
    ctx.LineTo(new Point(12 + w, 12), true, true);
    ctx.ArcTo(new Point(24 + w, 0), new Size(12, 12), 0, false, SweepDirection.Clockwise, true, true);
   }
   edge.Freeze();
   outline.Data = edge;
   foreach (UIElement child in stage.Children) {
    var element = child as FrameworkElement;
    if (element != null) Canvas.SetLeft(element, Math.Round((w - element.Width) / 2));
   }
  }

  // ---------- state → view ----------
  bool HasMedia { get { return media.Current != null; } }
  bool HasProgress { get { return media.Current != null && media.Current.Duration > TimeSpan.Zero; } }
  // Something is playing and nothing can hear it: the PC's speakers are muted, and no phone is
  // streaming, or the phone is muted here or turned all the way down. Easy to walk away from.
  bool NobodyHears {
   get {
    var now = media.Current;
    return now != null && now.Playing && session.PcMuted && (!session.IsActive || session.Muted || session.PhoneLevel == 0);
   }
  }
  bool CompactShowsMedia { get { return HasMedia && session.State != LinkState.Pairing && session.State != LinkState.Reconnecting; } }

  NotchView TargetView() {
   if (!expanded) {
    bool playing = media.Current != null && media.Current.Playing;
    return session.State != LinkState.Offline || playing ? NotchView.Compact : NotchView.Idle;
   }
   if (session.Approval != null) return NotchView.Pairing;
   if (settingsOpen) return NotchView.Settings;
   if (session.State == LinkState.Pairing) return NotchView.Pairing;
   return NotchView.Home;
  }

  Size SizeFor(NotchView v) {
   switch (v) {
    case NotchView.Idle: return new Size(132, 28);
    case NotchView.Compact: return new Size(NobodyHears ? 420 : CompactShowsMedia ? 340 : 300, 36);
    case NotchView.Pairing: return new Size(580, 200);
    case NotchView.Settings: return new Size(640, 308);
    default: return new Size(620, (HasProgress ? 264 : 238) + (NobodyHears ? 26 : 0));
   }
  }

  // Views update in place where possible so keyboard focus survives state changes.
  string Signature(NotchView v) {
   switch (v) {
    case NotchView.Compact: return "compact:" + session.State + CompactShowsMedia + NobodyHears;
    case NotchView.Home: return "home:" + session.State + session.Busy + session.Problem + session.PluggedPhone + session.PluggedWireless + HasMedia + HasProgress + session.PcMuted + session.MutedBy + session.WifiReady + session.OverWifi + NobodyHears;
    case NotchView.Pairing: return "pairing:" + session.PairingCode + (session.Approval == null ? "" : ":wifi:" + session.Approval.Code);
    case NotchView.Settings: return "settings:" + (testing ? 0 : session.WifiPhoneCount) + session.WifiProblem + session.AllowWifi;
    default: return v.ToString();
   }
  }

  public void Render() {
   NotchView target = TargetView();
   string signature = Signature(target);
   if (signature != currentSignature) {
    Size size = Fit(SizeFor(target));
    bool growing = size.Width > notch.ActualWidth + 0.5 || size.Height > notch.ActualHeight + 0.5;
    bool viewChanged = target != view;
    view = target;
    currentSignature = signature;
    var next = BuildView(target);
    next.Width = size.Width; next.Height = size.Height;
    SwapContent(next, viewChanged);
    AnimateSize(size, growing);
   }
   UpdateView();
   UpdateTimers();
  }

  Size Fit(Size size) { return new Size(Math.Min(size.Width, maxNotchWidth), size.Height); }

  void SwapContent(FrameworkElement next, bool animate) {
   var previous = current;
   current = next;
   Canvas.SetTop(next, 0);
   Canvas.SetLeft(next, Math.Round((notch.ActualWidth - next.Width) / 2));
   if (!animate || testing || previous == null) {
    stage.Children.Clear();
    stage.Children.Add(next);
    return;
   }
   // The old view fades out under the new one instead of being cut, so the notch never flashes
   // empty mid-resize. Anything still fading from an earlier swap goes now.
   for (int i = stage.Children.Count - 1; i >= 0; i--) if (stage.Children[i] != previous) stage.Children.RemoveAt(i);
   previous.IsHitTestVisible = false;
   var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(ReduceMotion ? 100 : 130));
   fade.Completed += delegate { stage.Children.Remove(previous); };
   previous.BeginAnimation(OpacityProperty, fade);
   stage.Children.Add(next);
   if (ReduceMotion) {
    next.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)));
    return;
   }
   var rise = new TranslateTransform(0, -6);
   next.RenderTransform = rise;
   var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
   next.Opacity = 0;
   next.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(240)) { BeginTime = TimeSpan.FromMilliseconds(110), EasingFunction = ease });
   rise.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-6, 0, TimeSpan.FromMilliseconds(240)) { BeginTime = TimeSpan.FromMilliseconds(110), EasingFunction = ease });
  }

  void AnimateSize(Size size, bool growing) {
   int ticket = ++sizeGeneration;
   if (testing || ReduceMotion || !IsVisible) {
    sizing = false;
    notch.BeginAnimation(WidthProperty, null); notch.BeginAnimation(HeightProperty, null);
    notch.Width = size.Width; notch.Height = size.Height;
    FitHost(size, false);
    return;
   }
   // Grow the window before the notch (with room for the spring's overshoot); shrink it after.
   FitHost(new Size(Math.Max(size.Width, notch.ActualWidth), Math.Max(size.Height, notch.ActualHeight)), growing);
   IEasingFunction ease = growing ? (IEasingFunction)new BackEase { Amplitude = 0.22, EasingMode = EasingMode.EaseOut } : new CubicEase { EasingMode = EasingMode.EaseInOut };
   var duration = TimeSpan.FromMilliseconds(growing ? 440 : 300);
   var width = new DoubleAnimation(size.Width, duration) { EasingFunction = ease };
   sizing = true;
   width.Completed += delegate {
    if (ticket != sizeGeneration) return;
    sizing = false;
    FitHost(size, false);
    if (hovering) Peek(true);
   };
   notch.BeginAnimation(WidthProperty, width);
   notch.BeginAnimation(HeightProperty, new DoubleAnimation(size.Height, duration) { EasingFunction = ease });
  }

  // The window's width is fixed at the widest the notch can get, so it never moves sideways:
  // a layered window that moves and resizes shows its old picture shifted for a frame before
  // the content catches up, which read as flicker. Only the height follows the notch (anchored
  // at the top, so nothing shifts): grown before it grows, with room for the spring's
  // overshoot, and shrunk after it shrinks.
  static double HostWidthFor(double maxNotch) { return Math.Ceiling(maxNotch * 1.1) + 24 + 20; }

  void FitHost(Size notchSize, bool overshoot) {
   if (testing) return;
   double h = Math.Ceiling(notchSize.Height * (overshoot ? 1.1 : 1.0)) + 8;
   if (Math.Abs(h - hostHeight) < 1) return;
   hostHeight = h;
   Reposition();
  }

  // Idle and compact states lean out slightly on hover without taking focus.
  void Peek(bool on) {
   if (expanded || sizing || testing || ReduceMotion) return;
   Size size = Fit(SizeFor(view));
   if (on) size = new Size(size.Width + 14, size.Height + 4);
   var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
   var duration = TimeSpan.FromMilliseconds(180);
   notch.BeginAnimation(WidthProperty, new DoubleAnimation(size.Width, duration) { EasingFunction = ease });
   notch.BeginAnimation(HeightProperty, new DoubleAnimation(size.Height, duration) { EasingFunction = ease });
  }

  // ---------- expand / collapse ----------
  public void Expand(bool viaKeyboard) {
   collapseTimer.Stop();
   // Asked for explicitly: show it even over a fullscreen app; it slips away again on close.
   if (hiddenForFullscreen) { hiddenForFullscreen = false; summonedOverFullscreen = true; }
   if (!IsVisible) Reveal();
   expanded = true;
   if (!testing) outsideClicks.Start();
   if (viaKeyboard) EnterKeyboardMode();
   Render();
   if (viaKeyboard) FocusFirst();
  }

  public void Collapse() {
   collapseTimer.Stop();
   outsideClicks.Stop();
   expanded = false;
   settingsOpen = false;
   Render();
   LeaveKeyboardMode();
   if (summonedOverFullscreen) { summonedOverFullscreen = false; CheckFullscreen(); }
  }

  public void OpenSettings(bool viaKeyboard) {
   settingsOpen = true;
   Expand(viaKeyboard);
  }

  void CloseSettings() {
   settingsOpen = false;
   Render();
   if (keyboardMode) FocusFirst();
  }

  void EnterKeyboardMode() {
   if (keyboardMode || hwnd == IntPtr.Zero) return;
   previousForeground = Native.GetForegroundWindow();
   keyboardMode = true;
   Native.SetNoActivate(hwnd, false);
   Native.SetForegroundWindow(hwnd);
   Activate();
  }

  void LeaveKeyboardMode() {
   if (!keyboardMode) return;
   keyboardMode = false;
   if (hwnd != IntPtr.Zero) Native.SetNoActivate(hwnd, true);
   // Hand focus back to whatever the user was doing before the shortcut.
   if (previousForeground != IntPtr.Zero && Native.IsWindow(previousForeground)) Native.SetForegroundWindow(previousForeground);
   previousForeground = IntPtr.Zero;
  }

  // A press anywhere but the notch itself (its body or ears) closes it, like a menu.
  void OnPressAnywhere(int x, int y) {
   // A phone asking to pair waits for an answer (or its minute), not for a stray click.
   if (!expanded || !IsVisible || session.Approval != null) return;
   Point p;
   try { p = notch.PointFromScreen(new Point(x, y)); }
   catch (InvalidOperationException) { return; }
   const double ear = 12;
   if (p.X >= -ear && p.X <= notch.ActualWidth + ear && p.Y >= 0 && p.Y <= notch.ActualHeight) return;
   outsideCollapseAt = Environment.TickCount;
   Collapse();
  }

  void FocusFirst() {
   Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(delegate {
    if (current != null) current.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
   }));
  }

  void OnKeyDown(object sender, KeyEventArgs e) {
   switch (e.Key) {
    case Key.Escape: if (settingsOpen) CloseSettings(); else Collapse(); e.Handled = true; break;
    case Key.MediaPlayPause: media.TogglePlayPause(); e.Handled = true; break;
   }
  }

  // ---------- show / hide ----------
  public void ToggleFromTray() {
   // The press on the tray icon already closed the notch; the click that follows shouldn't reopen it.
   if (!expanded && Environment.TickCount - outsideCollapseAt < 500) return;
   if (!IsVisible || hiddenByUser) { hiddenByUser = false; Reveal(); Expand(false); return; }
   if (expanded) Collapse(); else Expand(false);
  }

  public void ShowNotch() { hiddenByUser = false; Reveal(); }

  public void HideNotch() {
   hiddenByUser = true;
   Collapse();
   SlideAway();
  }

  void Reveal() {
   if (hiddenForFullscreen) return;
   if (!IsVisible) Show();
   Reposition();
   if (testing || ReduceMotion) { slide.Y = 0; return; }
   slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-(notch.ActualHeight + 16), 0, TimeSpan.FromMilliseconds(240)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
  }

  void SlideAway() {
   if (!IsVisible) return;
   if (testing || ReduceMotion) { Hide(); return; }
   var away = new DoubleAnimation(-(notch.ActualHeight + 16), TimeSpan.FromMilliseconds(180)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
   away.Completed += delegate { if (hiddenByUser || hiddenForFullscreen) Hide(); };
   slide.BeginAnimation(TranslateTransform.YProperty, away);
  }

  void CheckFullscreen() {
   if (hwnd == IntPtr.Zero) return;
   bool cover = prefs.HideOverFullscreen && Native.ForegroundIsFullscreenOn(hwnd);
   if (cover && !hiddenForFullscreen) {
    // Summoned over it: wait until the user closes it.
    if (summonedOverFullscreen && expanded) return;
    Collapse();
    hiddenForFullscreen = true;
    SlideAway();
   } else if (!cover && hiddenForFullscreen) {
    hiddenForFullscreen = false;
    if (!hiddenByUser) Reveal();
   }
  }

  // ---------- window plumbing ----------
  void OnSourceInitialized(object sender, EventArgs e) {
   hwnd = new WindowInteropHelper(this).Handle;
   Native.SetNoActivate(hwnd, true);
   var source = HwndSource.FromHwnd(hwnd);
   source.AddHook(WndProc);
   // A layered window drawn by the GPU has to be read back every frame; for a window this small,
   // software rendering is cheaper and doesn't contend with DWM during desktop animations.
   source.CompositionTarget.RenderMode = RenderMode.SoftwareOnly;
   Reposition();
   if (testing) return;
   HotkeyAvailable = Native.RegisterHotKey(hwnd, HotkeyId, Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT, (uint)KeyInterop.VirtualKeyFromKey(Key.S));
   // Only foreground changes are hooked. Location changes fire for every window on the system
   // (thousands during a virtual-desktop switch); the 1.5 s check covers in-place fullscreen.
   winEventProc = OnWinEvent;
   foregroundHook = Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, winEventProc, 0, 0, Native.WINEVENT_OUTOFCONTEXT);
   fullscreenTimer.Start();
   wallpaperTimer.Start();
  }

  void OnWinEvent(IntPtr hook, uint evt, IntPtr target, int idObject, int idChild, uint thread, uint time) { CheckFullscreen(); }

  IntPtr WndProc(IntPtr h, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) {
   switch (msg) {
    case Native.WM_HOTKEY:
     if (wParam.ToInt32() == HotkeyId) {
      hiddenByUser = false;
      if (expanded && keyboardMode) Collapse(); else Expand(true);
      handled = true;
     }
     break;
    case Native.WM_SETTINGCHANGE:
     bool wallpaper = wParam.ToInt32() == Native.SPI_SETDESKWALLPAPER;
     Dispatcher.BeginInvoke(new Action(delegate { Reposition(); if (wallpaper) CheckWallpaper(); }));
     break;
    case Native.WM_DISPLAYCHANGE:
    case Native.WM_DPICHANGED:
     hasPlaced = false;
     Dispatcher.BeginInvoke(new Action(Reposition));
     break;
   }
   return IntPtr.Zero;
  }

  public Forms.Screen TargetScreen() {
   foreach (var screen in Forms.Screen.AllScreens) if (screen.DeviceName == prefs.Display) return screen;
   return Forms.Screen.PrimaryScreen;
  }

  public string DisplayLabel(Forms.Screen screen) {
   int index = Array.IndexOf(Forms.Screen.AllScreens, screen) + 1;
   return "Display " + Math.Max(1, index) + (screen.Primary ? " (main)" : "");
  }

  public void CycleDisplay() {
   var screens = Forms.Screen.AllScreens;
   int index = Array.IndexOf(screens, TargetScreen());
   prefs.Display = screens[(index + 1) % screens.Length].DeviceName;
   prefs.Save();
   hasPlaced = false;
   Reposition();
   UpdateView();
  }

  // Positions in physical pixels so per-monitor DPI and mixed-scale setups stay exact. The window
  // is only moved when its rectangle actually changes.
  public void Reposition() {
   if (hwnd == IntPtr.Zero) return;
   var area = TargetScreen().WorkingArea;
   double scale = Native.DpiAt(area.Left + area.Width / 2, area.Top + 1) / 96.0;
   double limit = Math.Min(640, Math.Max(300, area.Width / scale - 48));
   hostWidth = HostWidthFor(limit);
   double width = testing ? TestWidth : hostWidth, height = testing ? TestHeight : hostHeight;
   int w = (int)Math.Round(width * scale), h = (int)Math.Round(height * scale);
   int x = area.Left + (area.Width - w) / 2, y = area.Top;
   if (!hasPlaced || placed.Left != x || placed.Top != y || placed.Right != x + w || placed.Bottom != y + h) {
    Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, x, y, w, h, Native.SWP_NOACTIVATE);
    placed = new Native.RECT { Left = x, Top = y, Right = x + w, Bottom = y + h };
    hasPlaced = true;
   }
   if (Math.Abs(limit - maxNotchWidth) > 0.5) { maxNotchWidth = limit; currentSignature = null; Render(); }
  }

  // ---------- live updates ----------
  void UpdateTimers() {
   bool visible = IsVisible && !testing;
   bool wave = visible && view == NotchView.Compact && (session.IsActive || (media.Current != null && media.Current.Playing));
   if (wave) meterTimer.Start(); else meterTimer.Stop();
   bool progress = visible && view == NotchView.Home && HasProgress && media.Current.Playing;
   if (progress) progressTimer.Start(); else progressTimer.Stop();
  }

  void MeterTick() {
   meter.RefreshIfStale();
   double level = Math.Min(1, Math.Sqrt(meter.Peak()) * 1.1);
   if (compactBars != null) compactBars.Push(level, session.IsActive && session.Muted);
  }

  public void PushTestLevel(double level) {
   for (int i = 0; i < 8; i++) if (compactBars != null) compactBars.Push(level, false);
  }

  void Shutdown() {
   outsideClicks.Stop();
   meterTimer.Stop(); fullscreenTimer.Stop(); collapseTimer.Stop(); wallpaperTimer.Stop(); progressTimer.Stop();
   if (hwnd != IntPtr.Zero && HotkeyAvailable) Native.UnregisterHotKey(hwnd, HotkeyId);
   if (foregroundHook != IntPtr.Zero) Native.UnhookWinEvent(foregroundHook);
  }
 }
}
