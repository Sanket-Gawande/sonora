using System;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Drawing = System.Drawing;

namespace Sonora {
 // `Sonora.exe --verify <dir>`: drives every notch state without animation, a phone or a media
 // app, checks the bundled font and the wallpaper surface, and renders each state to PNG.
 static class Verifier {
  public static int Run(NotchWindow notch, Session session, MediaSession media, Preferences prefs, string directory) {
   Directory.CreateDirectory(directory);
   var report = new StringBuilder();
   // A wallpaper stand-in so the snapshots show the notch against a desktop.
   notch.Background = new LinearGradientBrush(new GradientStopCollection {
    new GradientStop((Color)ColorConverter.ConvertFromString("#2B3550"), 0),
    new GradientStop((Color)ColorConverter.ConvertFromString("#56506E"), 0.45),
    new GradientStop((Color)ColorConverter.ConvertFromString("#9A7384"), 1) }, new Point(0, 0), new Point(0.4, 1));
   notch.Show();

   // Each weight the UI uses must come from its own bundled file, not a synthesized or system face.
   var weights = new[] { FontWeights.Normal, FontWeights.Medium, FontWeights.SemiBold };
   var files = new[] { "monasans-regular.otf", "monasans-medium.otf", "monasans-semibold.otf" };
   for (int i = 0; i < weights.Length; i++) {
    GlyphTypeface glyph;
    if (!new Typeface(Ui.Font, FontStyles.Normal, weights[i], FontStretches.Normal).TryGetGlyphTypeface(out glyph) || !glyph.FontUri.ToString().ToLowerInvariant().EndsWith("assets/fonts/" + files[i]))
     throw new Exception("Bundled Mona Sans " + weights[i] + " did not resolve: " + (glyph == null ? "none" : glyph.FontUri.ToString()));
   }
   report.AppendLine("PASS font: Mona Sans Regular, Medium and SemiBold resolve from the bundled files");

   CheckSurfaces(report);

   Expect(notch.View == NotchView.Idle, "starts idle");
   Snap(notch, directory, "01-idle");

   notch.Expand(false);
   Expect(notch.View == NotchView.Home, "expand shows home");
   Snap(notch, directory, "02-home-nothing-playing");

   media.ShowForVerification(new NowPlaying {
    Title = "Midnight City", Artist = "M83", App = "Spotify", Playing = true,
    CanToggle = true, CanPrevious = true, CanNext = true, CanSeek = true,
    Duration = TimeSpan.FromSeconds(243), Position = TimeSpan.FromSeconds(72), PositionAt = DateTime.UtcNow, Art = SampleArt()
   });
   Expect(notch.View == NotchView.Home, "media keeps home open");
   Snap(notch, directory, "03-home-playing");

   notch.Collapse();
   Expect(notch.View == NotchView.Compact, "playing media shows the compact activity");
   notch.PushTestLevel(0.7);
   Snap(notch, directory, "04-compact-playing");

   session.ShowForVerification(LinkState.Pairing, "moto g82 5G", "482 913");
   notch.Expand(false);
   Expect(notch.View == NotchView.Pairing, "pairing view");
   Snap(notch, directory, "05-pairing");

   session.ShowForVerification(LinkState.Streaming, "moto g82 5G", "482 913");
   Expect(notch.View == NotchView.Home, "a connected phone returns to home");
   session.SetVolume(64);
   Snap(notch, directory, "06-home-streaming");
   session.SetMuted(true);
   Expect(session.Muted, "mute");
   session.SetVolume(40);
   Expect(!session.Muted && session.Volume == 40, "volume change unmutes");

   notch.Collapse();
   notch.PushTestLevel(0.7);
   Snap(notch, directory, "07-compact-streaming");

   session.ShowForVerification(LinkState.Reconnecting, "moto g82 5G", "482 913");
   notch.Expand(false);
   Snap(notch, directory, "08-home-waiting");

   session.ShowForVerification(LinkState.Streaming, "moto g82 5G", "482 913");
   notch.OpenSettings(false);
   Expect(notch.View == NotchView.Settings, "settings open inside the notch");
   Snap(notch, directory, "09-settings");

   // The same home with a warm wallpaper: the background follows it, the accent does not.
   Ui.ApplySurface(Color.FromRgb(0xC8, 0x6A, 0x3C));
   notch.RefreshTheme();
   notch.Collapse();
   notch.Expand(false);
   Snap(notch, directory, "10-home-warm-wallpaper");
   Ui.ApplySurface(Color.FromRgb(0x3C, 0x6A, 0xC8));
   notch.RefreshTheme();
   Snap(notch, directory, "11-home-blue-wallpaper");
   Ui.ApplySurface(null);
   notch.RefreshTheme();

   session.Disconnect();
   Expect(session.State == LinkState.Offline, "disconnect");
   notch.HideNotch();
   Expect(!notch.IsVisible, "hide");
   notch.ShowNotch();
   Expect(notch.IsVisible, "show");

   report.AppendLine("PASS states: idle, home (nothing playing, playing, streaming, waiting), compact (playing, streaming), pairing, settings, mute/volume, disconnect, hide/show.");
   report.AppendLine("Snapshots written to " + directory);
   File.WriteAllText(Path.Combine(directory, "verification.txt"), report.ToString(), Encoding.UTF8);
   notch.Close();
   return 0;
  }

  // Synthetic wallpapers: a warm one gives a warm background, a grey one plain graphite, and a
  // colour behind the notch outweighs a larger area lower down. Every surface stays opaque and
  // keeps body text at 12:1 and the faintest text at 4.5:1 or better.
  static void CheckSurfaces(StringBuilder report) {
   Color? warm = Dominant(Drawing.Color.FromArgb(214, 110, 52), Drawing.Color.FromArgb(214, 110, 52), 0.5);
   Expect(warm.HasValue && warm.Value.R > warm.Value.B, "warm wallpaper is read as warm");
   Expect(!Dominant(Drawing.Color.FromArgb(90, 90, 92), Drawing.Color.FromArgb(40, 40, 41), 0.5).HasValue, "grey wallpaper keeps plain graphite");
   // Blue fills only the top 40%, but the top quarter counts double, so it outweighs the orange 60%.
   Color? split = Dominant(Drawing.Color.FromArgb(40, 90, 200), Drawing.Color.FromArgb(200, 60, 40), 0.4);
   Expect(split.HasValue && split.Value.B > split.Value.R, "the colour behind the notch outweighs a larger area lower down");
   foreach (var tone in new[] { warm.Value, split.Value }) {
    Ui.ApplySurface(tone);
    Color ink = ((SolidColorBrush)Ui.Ink).Color;
    Expect(ink.A == 255, "surface is opaque");
    Expect(Contrast(((SolidColorBrush)Ui.Text).Color, ink) >= 12, "text readable on " + ink);
    Expect(Contrast(((SolidColorBrush)Ui.Text3).Color, ink) >= 4.5, "faint text readable on " + ink);
   }
   Ui.ApplySurface(warm.Value);
   Color warmInk = ((SolidColorBrush)Ui.Ink).Color;
   Expect(warmInk.R > warmInk.B, "warm wallpaper gives a warm background");
   Ui.ApplySurface(null);
   report.AppendLine("PASS wallpaper surface: warm " + warmInk + ", opaque, text ≥ 12:1, faint text ≥ 4.5:1, grey falls back");
  }

  static Color? Dominant(Drawing.Color top, Drawing.Color bottom, double topShare) {
   using (var bitmap = new Drawing.Bitmap(96, 96)) {
    using (var g = Drawing.Graphics.FromImage(bitmap)) {
     int split = (int)(96 * topShare);
     g.FillRectangle(new Drawing.SolidBrush(top), 0, 0, 96, split);
     g.FillRectangle(new Drawing.SolidBrush(bottom), 0, split, 96, 96 - split);
    }
    return WallpaperTheme.Dominant(bitmap);
   }
  }

  // A plain gradient standing in for album art in the snapshots.
  static BitmapSource SampleArt() {
   var visual = new DrawingVisual();
   using (var dc = visual.RenderOpen())
    dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(0xF2, 0x6B, 0x5B), Color.FromRgb(0x5B, 0x3F, 0xD9), 45), null, new Rect(0, 0, 160, 160));
   var bitmap = new RenderTargetBitmap(160, 160, 96, 96, PixelFormats.Pbgra32);
   bitmap.Render(visual);
   bitmap.Freeze();
   return bitmap;
  }

  static double Contrast(Color a, Color b) {
   double la = Luminance(a), lb = Luminance(b);
   return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
  }
  static double Luminance(Color c) { return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B); }
  static double Channel(byte v) { double s = v / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }

  static void Expect(bool condition, string what) { if (!condition) throw new Exception("FAILED: " + what); }

  static void Snap(Window window, string directory, string name) {
   window.UpdateLayout();
   window.Dispatcher.Invoke(new Action(delegate { }), System.Windows.Threading.DispatcherPriority.Render);
   var bitmap = new RenderTargetBitmap((int)(window.ActualWidth * 2), (int)(window.ActualHeight * 2), 192, 192, PixelFormats.Pbgra32);
   bitmap.Render(window);
   var encoder = new PngBitmapEncoder();
   encoder.Frames.Add(BitmapFrame.Create(bitmap));
   using (var file = File.Create(Path.Combine(directory, name + ".png"))) encoder.Save(file);
  }
 }
}
