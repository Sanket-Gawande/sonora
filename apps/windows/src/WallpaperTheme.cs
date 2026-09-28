using System;
using System.Drawing;
using System.IO;
using Microsoft.Win32;
using Media = System.Windows.Media;

namespace Sonora {
 // Finds the wallpaper's dominant colour for the notch background: the most vivid hue, with the
 // top of the image (where the notch sits) counting double. Grey or near-grey wallpapers return
 // null so the notch stays plain graphite.
 static class WallpaperTheme {
  const string DesktopKey = @"HKEY_CURRENT_USER\Control Panel\Desktop";
  const string ColorsKey = @"HKEY_CURRENT_USER\Control Panel\Colors";
  static readonly string Transcoded = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Themes\TranscodedWallpaper");

  // Changes whenever the wallpaper does, including slideshow steps, so callers can poll cheaply.
  public static string Fingerprint() {
   string path = Registry.GetValue(DesktopKey, "WallPaper", "") as string ?? "";
   string color = Registry.GetValue(ColorsKey, "Background", "") as string ?? "";
   long stamp = 0;
   try { if (File.Exists(Transcoded)) stamp = File.GetLastWriteTimeUtc(Transcoded).Ticks; } catch (IOException) { }
   return path + "|" + color + "|" + stamp;
  }

  public static Media.Color? FromCurrentWallpaper() {
   try {
    string path = Registry.GetValue(DesktopKey, "WallPaper", "") as string;
    if (string.IsNullOrEmpty(path)) return FromSolidColor();
    // Windows keeps the image it is actually showing here, which also covers slideshows.
    string source = File.Exists(Transcoded) ? Transcoded : path;
    if (!File.Exists(source)) return null;
    using (var stream = new MemoryStream(File.ReadAllBytes(source)))
    using (var image = Image.FromStream(stream))
    using (var bitmap = new Bitmap(image)) return Dominant(bitmap);
   } catch (Exception) {
    return null;
   }
  }

  static Media.Color? FromSolidColor() {
   string value = Registry.GetValue(ColorsKey, "Background", null) as string;
   if (value == null) return null;
   var parts = value.Split(' ');
   int r, g, b;
   if (parts.Length != 3 || !int.TryParse(parts[0], out r) || !int.TryParse(parts[1], out g) || !int.TryParse(parts[2], out b)) return null;
   using (var bitmap = new Bitmap(4, 4)) {
    using (var gfx = Graphics.FromImage(bitmap)) gfx.Clear(Color.FromArgb(r, g, b));
    return Dominant(bitmap);
   }
  }

  public static Media.Color? Dominant(Bitmap source) {
   const int size = 48, buckets = 36;
   using (var small = new Bitmap(size, size)) {
    using (var gfx = Graphics.FromImage(small)) {
     gfx.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
     gfx.DrawImage(source, 0, 0, size, size);
    }
    var weight = new double[buckets];
    var red = new double[buckets]; var green = new double[buckets]; var blue = new double[buckets];
    double colourful = 0, total = 0;
    for (int y = 0; y < size; y++) {
     double row = y < size / 4 ? 2 : 1;
     for (int x = 0; x < size; x++) {
      Color c = small.GetPixel(x, y);
      int max = Math.Max(c.R, Math.Max(c.G, c.B)), min = Math.Min(c.R, Math.Min(c.G, c.B));
      double chroma = (max - min) / 255.0;
      double light = (max + min) / 510.0;
      // Vivid mid-tones count most; near-black and near-white pixels barely count.
      double w = row * chroma * Math.Max(0, 1 - Math.Abs(light - 0.5) * 1.4);
      total += row;
      if (w <= 0) continue;
      int bucket = (int)(c.GetHue() / 10) % buckets;
      weight[bucket] += w; red[bucket] += c.R * w; green[bucket] += c.G * w; blue[bucket] += c.B * w;
      colourful += w;
     }
    }
    if (colourful / total < 0.035) return null;
    int best = 0;
    double bestScore = -1;
    for (int i = 0; i < buckets; i++) {
     double score = weight[i] + 0.5 * (weight[(i + buckets - 1) % buckets] + weight[(i + 1) % buckets]);
     if (score > bestScore) { bestScore = score; best = i; }
    }
    return Media.Color.FromRgb((byte)Math.Round(red[best] / weight[best]), (byte)Math.Round(green[best] / weight[best]), (byte)Math.Round(blue[best] / weight[best]));
   }
  }
 }
}
