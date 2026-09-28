using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Sonora {
 public sealed partial class NotchWindow {
  Bars compactBars;
  ContentControl compactArt, artHost;
  TextBlock compactTitle, mediaTitle, mediaSub, elapsedText, durationText, volumeLabel, displayLabel, statusText;
  Button playButton, prevButton, nextButton, muteButton;
  Grid progressTrack;
  Border progressFill;
  Slider volumeSlider;
  string lastStatus;
  bool updatingVolume;

  FrameworkElement BuildView(NotchView v) {
   compactBars = null; compactArt = null; artHost = null; compactTitle = null; mediaTitle = null; mediaSub = null;
   elapsedText = null; durationText = null; volumeLabel = null; displayLabel = null;
   playButton = null; prevButton = null; nextButton = null; muteButton = null;
   progressTrack = null; progressFill = null; volumeSlider = null;
   switch (v) {
    case NotchView.Idle: return BuildIdle();
    case NotchView.Compact: return BuildCompact();
    case NotchView.Pairing: return BuildPairing();
    case NotchView.Settings: return BuildSettings();
    default: return BuildHome();
   }
  }

  // ---------- helpers ----------
  static Grid Columns(params GridLength[] widths) {
   var grid = new Grid();
   foreach (var width in widths) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
   return grid;
  }
  static GridLength Auto { get { return GridLength.Auto; } }
  static GridLength Star { get { return new GridLength(1, GridUnitType.Star); } }
  static GridLength Px(double v) { return new GridLength(v); }
  static T At<T>(T element, int column) where T : UIElement { Grid.SetColumn(element, column); return element; }
  static T Row<T>(T element, int row) where T : UIElement { Grid.SetRow(element, row); return element; }

  static StackPanel Stack(Orientation orientation, params UIElement[] children) {
   var panel = new StackPanel { Orientation = orientation, VerticalAlignment = VerticalAlignment.Center };
   foreach (var child in children) panel.Children.Add(child);
   return panel;
  }

  static FrameworkElement Pad(FrameworkElement element, double left, double right) { element.Margin = new Thickness(left, 0, right, 0); return element; }

  static FrameworkElement Wrap(FrameworkElement content) {
   var host = new Grid();
   host.Children.Add(content);
   return host;
  }

  static FrameworkElement Heading(string title, string detail, bool warn) {
   var panel = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 12, 0) };
   panel.Children.Add(Ui.T(title, 13, FontWeights.SemiBold, Ui.Text));
   var d = Ui.T(detail, 12, FontWeights.Normal, warn ? Ui.Warn : Ui.Text2);
   d.Margin = new Thickness(0, 3, 0, 0);
   d.ToolTip = detail;
   panel.Children.Add(d);
   return panel;
  }

  static string Join(string a, string b) {
   if (string.IsNullOrEmpty(a)) return b ?? "";
   if (string.IsNullOrEmpty(b)) return a;
   return a + " · " + b;
  }

  Button SettingsButton() { return Ui.IconButton("gear", "Settings", "GhostIconButton", 36, delegate { OpenSettings(keyboardMode); }); }

  static void SetArt(ContentControl host, BitmapSource art, double size, double radius) {
   object key = (object)art ?? "none";
   if (host == null || ReferenceEquals(host.Tag, key)) return;
   host.Tag = key;
   host.Content = Ui.Art(art, size, radius);
  }

  // ---------- idle: a small lip on the screen edge ----------
  FrameworkElement BuildIdle() {
   var dot = new System.Windows.Shapes.Ellipse { Width = 6, Height = 6, Stroke = Ui.Text3, StrokeThickness = 1.3, Margin = new Thickness(8, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center };
   var row = Stack(Orientation.Horizontal, Ui.Logo(15, Ui.Text3), dot);
   row.HorizontalAlignment = HorizontalAlignment.Center;
   var button = new Button { Style = Ui.S("SurfaceButton"), Content = row, ToolTip = "Sonora (Ctrl+Alt+S)" };
   AutomationProperties.SetName(button, "Open Sonora");
   button.Click += delegate { Expand(false); };
   return button;
  }

  // ---------- compact: what's playing, or the phone link ----------
  FrameworkElement BuildCompact() {
   var grid = Columns(Star, Auto);
   UIElement lead, trailing;
   TextBlock title;
   string name;
   if (session.State == LinkState.Pairing) {
    grid.Margin = new Thickness(16, 0, 16, 0);
    lead = Ui.Icon("qr", 15, 1.8, Ui.Text);
    title = Ui.T("Pairing · " + session.PairingCode, 12, FontWeights.SemiBold, Ui.Text);
    trailing = Ui.Spinner(12, Ui.Text2);
    name = "Pairing with " + session.DeviceName;
   } else if (session.State == LinkState.Reconnecting) {
    grid.Margin = new Thickness(16, 0, 16, 0);
    lead = Ui.Icon("phone", 15, 1.8, Ui.Text);
    title = Ui.T("Waiting for " + session.DeviceName, 12, FontWeights.SemiBold, Ui.Text);
    trailing = Ui.Spinner(12, Ui.Warn);
    name = "Waiting for " + session.DeviceName;
   } else {
    compactBars = new Bars(9, 16, 2, Ui.Signal);
    if (CompactShowsMedia) {
     grid.Margin = new Thickness(7, 0, 16, 0);
     compactArt = new ContentControl { Width = 24, Height = 24, Focusable = false, IsTabStop = false, VerticalAlignment = VerticalAlignment.Center };
     lead = compactArt;
     title = compactTitle = Ui.T("", 12, FontWeights.SemiBold, Ui.Text);
     name = "Now playing";
    } else {
     grid.Margin = new Thickness(16, 0, 16, 0);
     lead = Ui.Icon("phone", 15, 1.8, Ui.Text);
     title = Ui.T(session.DeviceName ?? "Phone", 12, FontWeights.SemiBold, Ui.Text);
     name = "Streaming to " + session.DeviceName;
    }
    if (session.IsActive && CompactShowsMedia) {
     var link = Ui.Icon("phone", 13, 1.8, Ui.Live);
     trailing = Stack(Orientation.Horizontal, link, Pad(compactBars, 8, 0));
     name += ", streaming to " + session.DeviceName;
    } else {
     trailing = compactBars;
    }
   }
   title.MaxWidth = 210;
   grid.Children.Add(Stack(Orientation.Horizontal, lead, Pad(title, 10, 10)));
   grid.Children.Add(At(trailing, 1));
   var button = new Button { Style = Ui.S("SurfaceButton"), Content = grid, HorizontalContentAlignment = HorizontalAlignment.Stretch, ToolTip = "Open Sonora (Ctrl+Alt+S)" };
   AutomationProperties.SetName(button, "Open Sonora. " + name);
   button.Click += delegate { Expand(false); };
   return button;
  }

  // ---------- home: now playing on top, the phone below ----------
  FrameworkElement BuildHome() {
   var grid = new Grid { Margin = new Thickness(22, 18, 22, 16) };
   for (int i = 0; i < 4; i++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
   grid.Children.Add(BuildMediaRow());
   if (HasProgress) grid.Children.Add(Row(BuildProgress(), 1));
   grid.Children.Add(Row(new Border { Height = 1, Background = Ui.Line, Margin = new Thickness(0, HasProgress ? 14 : 18, 0, 14) }, 2));
   grid.Children.Add(Row(BuildPhoneRow(), 3));
   return Wrap(grid);
  }

  FrameworkElement BuildMediaRow() {
   var row = Columns(Px(64), Star, Auto);
   row.Height = 64;
   artHost = new ContentControl { Width = 64, Height = 64, Focusable = false, IsTabStop = false };
   row.Children.Add(artHost);
   var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 12, 0) };
   mediaTitle = Ui.T("", 15, FontWeights.SemiBold, Ui.Text);
   mediaSub = Ui.T("", 12, FontWeights.Normal, Ui.Text2);
   mediaSub.Margin = new Thickness(0, 5, 0, 0);
   text.Children.Add(mediaTitle);
   text.Children.Add(mediaSub);
   row.Children.Add(At(text, 1));
   if (HasMedia) {
    // These act on the playing app itself; the stream to the phone simply carries what it plays.
    prevButton = Ui.IconButton("prev", "Previous track", "GhostIconButton", 40, media.Previous);
    playButton = Ui.IconButton("pause", "Pause", "PrimaryIconButton", 48, media.TogglePlayPause);
    nextButton = Ui.IconButton("next", "Next track", "GhostIconButton", 40, media.Next);
    row.Children.Add(At(Stack(Orientation.Horizontal, prevButton, Pad(playButton, 8, 8), nextButton), 2));
   }
   return row;
  }

  FrameworkElement BuildProgress() {
   var row = Columns(Px(44), Star, Px(44));
   row.Margin = new Thickness(0, 12, 0, 0);
   elapsedText = Ui.Numbers(Ui.T("", 11, FontWeights.Medium, Ui.Text3));
   durationText = Ui.Numbers(Ui.T("", 11, FontWeights.Medium, Ui.Text3));
   durationText.HorizontalAlignment = HorizontalAlignment.Right;
   progressTrack = new Grid { Height = 16, Background = Brushes.Transparent, Margin = new Thickness(8, 0, 8, 0) };
   progressTrack.Children.Add(new Border { Height = 4, CornerRadius = new CornerRadius(2), Background = Ui.Track, VerticalAlignment = VerticalAlignment.Center });
   progressFill = new Border { Height = 4, CornerRadius = new CornerRadius(2), Background = Ui.Signal, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
   progressTrack.Children.Add(progressFill);
   AutomationProperties.SetName(progressTrack, "Playback position");
   var now = media.Current;
   if (now != null && now.CanSeek) {
    progressTrack.Cursor = Cursors.Hand;
    progressTrack.ToolTip = "Click to jump";
    progressTrack.MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e) {
     var playing = media.Current;
     if (playing == null || progressTrack.ActualWidth <= 0) return;
     double fraction = Math.Max(0, Math.Min(1, e.GetPosition(progressTrack).X / progressTrack.ActualWidth));
     media.Seek(TimeSpan.FromTicks((long)(playing.Duration.Ticks * fraction)));
     e.Handled = true;
    };
   }
   progressTrack.SizeChanged += delegate { UpdateProgress(); };
   row.Children.Add(elapsedText);
   row.Children.Add(At(progressTrack, 1));
   row.Children.Add(At(durationText, 2));
   return row;
  }

  FrameworkElement BuildPhoneRow() {
   switch (session.State) {
    case LinkState.Streaming: return BuildStreamingRow();
    case LinkState.Reconnecting: return BuildWaitingRow();
    default: return BuildOfflineRow();
   }
  }

  FrameworkElement BuildOfflineRow() {
   var row = Columns(Auto, Star, Auto, Auto);
   row.Height = 44;
   row.Children.Add(Ui.DeviceTile("phone", true, 40));
   string detail = session.Problem ?? (session.Busy ? "Looking for your phone on USB…"
    : session.PluggedPhone != null ? session.PluggedPhone + " is plugged in. Connect here or from the phone."
    : "Plug in your Android phone with USB debugging on.");
   row.Children.Add(At(Heading("Play on your phone", detail, session.Problem != null), 1));
   var connect = Ui.Pill(session.Busy ? "Connecting…" : "Connect over USB", "phone", true, delegate { session.ConnectUsb(); });
   connect.IsEnabled = !session.Busy;
   row.Children.Add(At(connect, 2));
   row.Children.Add(At(Pad(SettingsButton(), 8, 0), 3));
   return row;
  }

  FrameworkElement BuildStreamingRow() {
   var row = Columns(Auto, Star, Auto, Auto, Auto, Auto);
   row.Height = 44;
   row.Children.Add(Ui.DeviceTile("phone", false, 40));
   var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 12, 0) };
   info.Children.Add(Ui.T(session.DeviceName ?? "Phone", 13, FontWeights.SemiBold, Ui.Text));
   var status = Ui.Status("live", "Streaming over USB");
   ((FrameworkElement)status).Margin = new Thickness(0, 3, 0, 0);
   info.Children.Add(status);
   row.Children.Add(At(info, 1));

   volumeSlider = new Slider { Style = Ui.S("VolumeSlider"), Width = 112, Minimum = 0, Maximum = 100, SmallChange = 1, LargeChange = 5, Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, ToolTip = "Volume on the phone" };
   AutomationProperties.SetName(volumeSlider, "Volume on the phone");
   volumeSlider.ValueChanged += delegate {
    if (updatingVolume) return;
    session.SetVolume(Math.Round(volumeSlider.Value));
    prefs.Volume = session.Volume;
   };
   volumeSlider.LostMouseCapture += delegate { prefs.Save(); };
   volumeSlider.LostKeyboardFocus += delegate { prefs.Save(); };
   volumeLabel = Ui.Numbers(Ui.T("", 12, FontWeights.Medium, Ui.Text2));
   volumeLabel.Width = 38;
   row.Children.Add(At(Stack(Orientation.Horizontal, volumeSlider, volumeLabel), 2));
   muteButton = Ui.IconButton("speaker", "Mute phone", "GhostIconButton", 36, delegate { session.SetMuted(!session.Muted); });
   row.Children.Add(At(muteButton, 3));
   row.Children.Add(At(Pad(Ui.IconButton("unlink", "Disconnect phone", "GhostIconButton", 36, session.Disconnect), 2, 0), 4));
   row.Children.Add(At(Pad(SettingsButton(), 2, 0), 5));
   return row;
  }

  FrameworkElement BuildWaitingRow() {
   var row = Columns(Auto, Star, Auto, Auto);
   row.Height = 44;
   row.Children.Add(Ui.DeviceTile("phone", false, 40));
   var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 12, 0) };
   info.Children.Add(Ui.T(session.DeviceName ?? "Phone", 13, FontWeights.SemiBold, Ui.Text));
   var status = Ui.Status("warn", "Disconnected · check the cable, keep Sonora open");
   ((FrameworkElement)status).Margin = new Thickness(0, 3, 0, 0);
   info.Children.Add(status);
   row.Children.Add(At(info, 1));
   row.Children.Add(At(Ui.Pill("Stop", null, false, session.Disconnect), 2));
   row.Children.Add(At(Pad(SettingsButton(), 8, 0), 3));
   return row;
  }

  // ---------- pairing ----------
  FrameworkElement BuildPairing() {
   var grid = Columns(Px(152), Star);
   grid.Margin = new Thickness(22, 24, 22, 20);
   var cable = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
   cable.Children.Add(Ui.Icon("phone", 44, 1.4, Ui.Text));
   var usbLabel = Ui.T("USB", 12, FontWeights.SemiBold, Ui.Signal);
   usbLabel.HorizontalAlignment = HorizontalAlignment.Center;
   usbLabel.Margin = new Thickness(0, 10, 0, 0);
   cable.Children.Add(usbLabel);
   grid.Children.Add(new Border { Width = 152, Height = 152, CornerRadius = new CornerRadius(18), Background = Ui.Card, VerticalAlignment = VerticalAlignment.Top, Child = cable });

   var right = new StackPanel { Margin = new Thickness(22, 0, 0, 0) };
   var header = Columns(Star, Auto);
   header.Children.Add(Ui.T("Pair over USB", 15, FontWeights.SemiBold, Ui.Text));
   header.Children.Add(At(Ui.IconButton("close", "Cancel pairing", "GhostIconButton", 32, session.CancelPairing), 1));
   right.Children.Add(header);
   right.Children.Add(Step(1, "Sonora opened on " + session.DeviceName));
   right.Children.Add(Step(2, "Check the phone shows this number"));
   right.Children.Add(Step(3, "Tap Numbers match on the phone"));

   var bottom = Columns(Auto, Star, Auto);
   bottom.Margin = new Thickness(0, 8, 0, 0);
   var number = Ui.Numbers(Ui.T(session.PairingCode ?? "", 26, FontWeights.SemiBold, Ui.Text));
   AutomationProperties.SetName(number, "Pairing number " + session.PairingCode);
   bottom.Children.Add(number);
   var waiting = Ui.T("Waiting for the phone", 12, FontWeights.Medium, Ui.Text2);
   bottom.Children.Add(At(Stack(Orientation.Horizontal, Ui.Spinner(12, Ui.Text2), Pad(waiting, 6, 0)), 2));
   right.Children.Add(bottom);
   grid.Children.Add(At(right, 1));
   return Wrap(grid);
  }

  FrameworkElement Step(int n, string text) {
   var badge = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(9), Background = Ui.Card, Child = new TextBlock { Text = n.ToString(), FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Ui.Text, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
   var row = Stack(Orientation.Horizontal, badge, Pad(Ui.T(text, 13, FontWeights.Normal, Ui.Text2), 10, 0));
   row.Margin = new Thickness(0, 6, 0, 0);
   return row;
  }

  // ---------- settings ----------
  FrameworkElement BuildSettings() {
   var outer = new Grid { Margin = new Thickness(22, 16, 22, 14) };
   outer.RowDefinitions.Add(new RowDefinition { Height = Auto });
   outer.RowDefinitions.Add(new RowDefinition { Height = Star });

   var header = Columns(Auto, Star, Auto);
   header.Margin = new Thickness(-6, 0, 0, 6);
   header.Children.Add(Ui.IconButton("back", "Back (Esc)", "GhostIconButton", 32, CloseSettings));
   header.Children.Add(At(Pad(Ui.T("Settings", 15, FontWeights.SemiBold, Ui.Text), 8, 0), 1));
   header.Children.Add(At(Ui.T("Sonora 0.4", 11, FontWeights.Normal, Ui.Text3), 2));
   outer.Children.Add(header);

   var body = Columns(Star, Px(28), Star);
   var left = new StackPanel();
   var keys = Stack(Orientation.Horizontal, Ui.Keycap("Ctrl"), Ui.Keycap("Alt"), Ui.Keycap("S"));
   left.Children.Add(SettingRow("Open Sonora", testing || HotkeyAvailable ? "Works from any app" : "Shortcut is taken by another app", keys));
   displayLabel = Ui.T("", 12, FontWeights.Normal, Ui.Text2);
   var displayButton = new Button { Style = Ui.S("Pill"), Height = 30, Padding = new Thickness(12, 0, 8, 0), Content = Stack(Orientation.Horizontal, displayLabel, Pad(Ui.Icon("right", 14, 1.8, Ui.Text2), 4, 0)) };
   AutomationProperties.SetName(displayButton, "Show on display. Activate to switch.");
   displayButton.Click += delegate { CycleDisplay(); };
   left.Children.Add(SettingRow("Show on", null, displayButton));
   left.Children.Add(SettingRow("Hide over fullscreen apps", "Games, videos, presentations",
    Switch("Hide over fullscreen apps", prefs.HideOverFullscreen, delegate(bool on) { prefs.HideOverFullscreen = on; prefs.Save(); CheckFullscreen(); })));
   body.Children.Add(left);

   var right = new StackPanel();
   right.Children.Add(SettingRow("Start with Windows", null,
    Switch("Start with Windows", !testing && Startup.IsEnabled, delegate(bool on) { if (!testing) Startup.SetEnabled(on); })));
   right.Children.Add(SettingRow("Reduce motion", prefs.ReduceMotion.HasValue ? null : "Follows Windows until changed",
    Switch("Reduce motion", ReduceMotion, delegate(bool on) { prefs.ReduceMotion = on; Ui.ReduceMotion = on; prefs.Save(); })));
   string tint = Ui.SurfaceTone.HasValue ? "Background follows your desktop colour" : (prefs.MatchWallpaper ? "Wallpaper has no strong colour" : "Plain graphite background");
   right.Children.Add(SettingRow("Tint to wallpaper", tint,
    Switch("Tint to wallpaper", prefs.MatchWallpaper, delegate(bool on) { prefs.MatchWallpaper = on; prefs.Save(); ApplyTheme(); })));
   body.Children.Add(At(right, 2));
   outer.Children.Add(Row(body, 1));
   return Wrap(outer);
  }

  FrameworkElement SettingRow(string title, string detail, FrameworkElement control) {
   var grid = Columns(Star, Auto);
   var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
   text.Children.Add(Ui.T(title, 13, FontWeights.Medium, Ui.Text));
   if (detail != null) { var d = Ui.T(detail, 11, FontWeights.Normal, Ui.Text3); d.Margin = new Thickness(0, 2, 0, 0); text.Children.Add(d); }
   grid.Children.Add(text);
   if (control != null) { control.VerticalAlignment = VerticalAlignment.Center; grid.Children.Add(At(control, 1)); }
   return new Border { BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 8, 0, 8), MinHeight = 52, Child = grid };
  }

  static ToggleButton Switch(string name, bool value, Action<bool> changed) {
   var toggle = new ToggleButton { Style = Ui.S("Switch"), IsChecked = value };
   AutomationProperties.SetName(toggle, name);
   toggle.Checked += delegate { changed(true); };
   toggle.Unchecked += delegate { changed(false); };
   return toggle;
  }

  // ---------- in-place updates ----------
  void UpdateView() {
   var now = media.Current;
   if (mediaTitle != null) {
    mediaTitle.Text = now == null ? "Nothing playing" : (now.Title.Length > 0 ? now.Title : "Untitled");
    mediaTitle.ToolTip = mediaTitle.Text;
    mediaSub.Text = now != null ? Join(now.Artist, now.App)
     : (media.Available ? "Play something on this PC and it shows up here." : "Media controls need Windows 10 version 1809 or later.");
    SetArt(artHost, now == null ? null : now.Art, 64, 14);
   }
   if (playButton != null && now != null) {
    string label = now.Playing ? "Pause" : "Play";
    if (!Equals(playButton.Tag, label)) {
     playButton.Tag = label;
     Ui.SetIcon(playButton, now.Playing ? "pause" : "play", true);
     playButton.ToolTip = label;
     AutomationProperties.SetName(playButton, label);
    }
    playButton.IsEnabled = now.CanToggle;
    prevButton.IsEnabled = now.CanPrevious;
    nextButton.IsEnabled = now.CanNext;
   }
   if (compactTitle != null && now != null) {
    compactTitle.Text = now.Title.Length > 0 ? now.Title : now.App;
    SetArt(compactArt, now.Art, 24, 7);
   }
   UpdateProgress();
   if (volumeSlider != null) {
    updatingVolume = true;
    volumeSlider.Value = session.Volume;
    updatingVolume = false;
    volumeLabel.Text = session.Muted ? "Muted" : Math.Round(session.Volume) + "%";
   }
   if (muteButton != null) {
    string label = session.Muted ? "Unmute phone" : "Mute phone";
    if (!Equals(muteButton.Tag, label)) {
     muteButton.Tag = label;
     Ui.SetIcon(muteButton, session.Muted ? "muted" : "speaker", false);
     muteButton.ToolTip = label;
     AutomationProperties.SetName(muteButton, label);
    }
   }
   if (displayLabel != null) displayLabel.Text = DisplayLabel(TargetScreen());
   Announce();
  }

  void UpdateProgress() {
   var now = media.Current;
   if (progressFill == null || now == null || now.Duration <= TimeSpan.Zero) return;
   TimeSpan position = now.PositionNow;
   double fraction = Math.Max(0, Math.Min(1, position.TotalSeconds / now.Duration.TotalSeconds));
   progressFill.Width = Math.Round(progressTrack.ActualWidth * fraction, 1);
   elapsedText.Text = Ui.Clock(position);
   durationText.Text = Ui.Clock(now.Duration);
  }

  // Screen readers hear connection changes without the notch taking focus.
  void Announce() {
   string status;
   switch (session.State) {
    case LinkState.Pairing: status = "Pairing over USB, number " + session.PairingCode; break;
    case LinkState.Streaming: status = "Streaming to " + session.DeviceName; break;
    case LinkState.Reconnecting: status = "Waiting for " + session.DeviceName; break;
    default: status = "Phone not connected"; break;
   }
   if (status == lastStatus) return;
   lastStatus = status;
   if (statusText == null) {
    statusText = new TextBlock { Width = 1, Height = 1, Opacity = 0, IsHitTestVisible = false };
    AutomationProperties.SetLiveSetting(statusText, AutomationLiveSetting.Polite);
    root.Children.Add(statusText);
   }
   statusText.Text = status;
   AutomationProperties.SetName(statusText, status);
   var peer = UIElementAutomationPeer.FromElement(statusText) ?? UIElementAutomationPeer.CreatePeerForElement(statusText);
   if (peer != null) peer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
  }
 }
}
