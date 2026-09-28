using System;
using System.Collections.Generic;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace Sonora {
 // Design tokens and small element builders for the notch.
 static class Ui {
  public static Brush Ink, Card, Edge, Text, Text2, Text3, Signal, Live, LiveTint, Warn, WarnTint, Line, Track, SwitchOff;
  public static FontFamily Font;
  public static bool ReduceMotion;
  // The wallpaper colour the surface is toned from, or null for plain graphite.
  public static Color? SurfaceTone;
  static Application application;

  public static void Initialize(Application app) {
   application = app;
   bool contrast = SystemParameters.HighContrast;
   Text = contrast ? SystemColors.WindowTextBrush : B("#F5F5F7");
   Text2 = contrast ? SystemColors.WindowTextBrush : B("#A9A9B0");
   Text3 = contrast ? SystemColors.GrayTextBrush : B("#8B8B93");
   Signal = contrast ? SystemColors.HighlightBrush : B("#A3B8FF");
   Live = contrast ? SystemColors.WindowTextBrush : B("#5CD69A");
   LiveTint = contrast ? Brushes.Transparent : B("#1F5CD69A");
   Warn = contrast ? SystemColors.WindowTextBrush : B("#FFB347");
   WarnTint = contrast ? Brushes.Transparent : B("#1FFFB347");
   Line = contrast ? SystemColors.WindowTextBrush : B("#17FFFFFF");
   Track = contrast ? SystemColors.GrayTextBrush : B("#33FFFFFF");
   SwitchOff = contrast ? SystemColors.GrayTextBrush : B("#3A3A42");
   Font = new FontFamily(new Uri("pack://application:,,,/"), "./Assets/Fonts/#Mona Sans");
   app.Resources["Sonora.Text"] = Text;
   app.Resources["Sonora.Signal"] = Signal;
   app.Resources["Sonora.Track"] = Track;
   app.Resources["Sonora.SwitchOff"] = SwitchOff;
   app.Resources["Sonora.Half"] = new HalfConverter();
   ApplySurface(null);
   using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Sonora.Styles.xaml"))
    app.Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Load(stream));
  }

  // The notch background follows the wallpaper: its dominant hue at a deep, fully opaque tone,
  // with raised surfaces and the border in the same hue. Lightness is fixed so text contrast
  // never depends on the wallpaper. The accent (Signal) does not change.
  public static void ApplySurface(Color? tone) {
   if (SystemParameters.HighContrast) {
    Ink = SystemColors.WindowBrush; Card = SystemColors.ControlBrush; Edge = SystemColors.WindowTextBrush;
   } else if (tone.HasValue) {
    double h, s, l;
    ToHsl(tone.Value, out h, out s, out l);
    Ink = Solid(Hsl(h, Clamp(s * 0.6, 0.16, 0.36), 0.11));
    Card = Solid(Hsl(h, Clamp(s * 0.5, 0.12, 0.28), 0.185));
    Edge = Solid(Hsl(h, Clamp(s * 0.5, 0.14, 0.30), 0.30));
   } else {
    Ink = B("#0B0B0D"); Card = B("#1C1C20"); Edge = B("#303036");
   }
   SurfaceTone = SystemParameters.HighContrast ? null : tone;
   application.Resources["Sonora.Ink"] = Ink;
   application.Resources["Sonora.Card"] = Card;
   application.Resources["Sonora.Edge"] = Edge;
  }

  static double Clamp(double v, double min, double max) { return Math.Max(min, Math.Min(max, v)); }
  static SolidColorBrush Solid(Color c) { var brush = new SolidColorBrush(c); brush.Freeze(); return brush; }

  public static void ToHsl(Color c, out double h, out double s, out double l) {
   double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
   double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
   l = (max + min) / 2; h = 0; s = 0;
   if (d <= 0) return;
   s = d / (1 - Math.Abs(2 * l - 1));
   if (max == r) h = ((g - b) / d + 6) % 6; else if (max == g) h = (b - r) / d + 2; else h = (r - g) / d + 4;
   h *= 60;
  }

  public static Color Hsl(double h, double s, double l) {
   double c = (1 - Math.Abs(2 * l - 1)) * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = l - c / 2, r, g, b;
   if (h < 60) { r = c; g = x; b = 0; } else if (h < 120) { r = x; g = c; b = 0; } else if (h < 180) { r = 0; g = c; b = x; }
   else if (h < 240) { r = 0; g = x; b = c; } else if (h < 300) { r = x; g = 0; b = c; } else { r = c; g = 0; b = x; }
   return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
  }

  public static SolidColorBrush B(string hex) {
   var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(hex);
   brush.Freeze();
   return brush;
  }

  public static Style S(string key) { return (Style)Application.Current.Resources[key]; }

  public static TextBlock T(string text, double size, FontWeight weight, Brush color) {
   return new TextBlock { Text = text, FontSize = size, FontWeight = weight, Foreground = color, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
  }

  public static TextBlock Numbers(TextBlock block) { block.Typography.NumeralAlignment = FontNumeralAlignment.Tabular; return block; }

  // ---------- icons: 24×24 paths ----------
  static readonly Dictionary<string, Geometry> cache = new Dictionary<string, Geometry>();
  static string R(double x, double y, double w, double h, double r) {
   return string.Format(System.Globalization.CultureInfo.InvariantCulture,
    "M{0},{1} H{2} A{4},{4} 0 0 1 {3},{5} V{6} A{4},{4} 0 0 1 {2},{7} H{0} A{4},{4} 0 0 1 {8},{6} V{5} A{4},{4} 0 0 1 {0},{1} Z ",
    x + r, y, x + w - r, x + w, r, y + r, y + h - r, y + h, x);
  }
  static readonly Dictionary<string, string> paths = new Dictionary<string, string> {
   { "phone", R(6.5, 2.5, 11, 19, 2.6) + "M10.5,18.5 H13.5" },
   { "speaker", "M4,9.5 H7.2 L12,5.5 V18.5 L7.2,14.5 H4 Z M15.5,9 A4.2,4.2 0 0 1 15.5,15 M18,6.5 A8,8 0 0 1 18,17.5" },
   { "muted", "M4,9.5 H7.2 L12,5.5 V18.5 L7.2,14.5 H4 Z M16,9.5 L21,14.5 M21,9.5 L16,14.5" },
   { "pause", "M8.5,5.5 V18.5 M15.5,5.5 V18.5" },
   { "play", "M8,5.2 V18.8 L19,12 Z" },
   { "prev", "M6,5.5 V18.5 M19,6 V18 L9.5,12 Z" },
   { "next", "M18,5.5 V18.5 M5,6 V18 L14.5,12 Z" },
   { "music", "M9,17.5 V6.5 L19,4.5 V15.5 M9,17.5 A2.5,2.5 0 1 1 6.5,15 A2.5,2.5 0 0 1 9,17.5 Z M19,15.5 A2.5,2.5 0 1 1 16.5,13 A2.5,2.5 0 0 1 19,15.5 Z" },
   { "unlink", "M9,17 H7 A5,5 0 0 1 7,7 H9 M15,7 H17 A5,5 0 0 1 21,15 M8,12 H11 M3,3 L21,21" },
   { "back", "M15,6 L9,12 L15,18" },
   { "right", "M9,6 L15,12 L9,18" },
   { "close", "M6,6 L18,18 M18,6 L6,18" },
   { "qr", R(3.5, 3.5, 6, 6, 1) + R(14.5, 3.5, 6, 6, 1) + R(3.5, 14.5, 6, 6, 1) + "M14.5,14.5 H16.5 V16.5 M20.5,14.5 V20.5 H14.5 M17.5,20.5 V17.5" },
   { "logo", "M4,16 C7,10 9,10 12,16 S17,22 20,16" },
   { "logo2", "M4,9.5 C7,5.5 9,5.5 12,9.5 S17,13.5 20,9.5" },
  };
  static readonly HashSet<string> filled = new HashSet<string> { "play", "prev", "next" };

  static Geometry Geo(string key) {
   Geometry geometry;
   if (cache.TryGetValue(key, out geometry)) return geometry;
   geometry = key == "gear" ? Gear() : Geometry.Parse(paths[key]);
   geometry.Freeze();
   cache[key] = geometry;
   return geometry;
  }

  static Geometry Gear() {
   // Eight-tooth gear with a centre hole, drawn as one closed outline.
   var figure = new StreamGeometry();
   using (var ctx = figure.Open()) {
    const int teeth = 8;
    for (int i = 0; i < teeth * 4; i++) {
     double angle = (i / (double)(teeth * 4)) * Math.PI * 2 - Math.PI / 2;
     double radius = (i % 4 == 0 || i % 4 == 1) ? 9.6 : 7.4;
     var point = new Point(12 + radius * Math.Cos(angle), 12 + radius * Math.Sin(angle));
     if (i == 0) ctx.BeginFigure(point, false, true); else ctx.LineTo(point, true, true);
    }
    ctx.BeginFigure(new Point(15, 12), false, true);
    ctx.ArcTo(new Point(9, 12), new Size(3, 3), 0, false, SweepDirection.Clockwise, true, true);
    ctx.ArcTo(new Point(15, 12), new Size(3, 3), 0, false, SweepDirection.Clockwise, true, true);
   }
   return figure;
  }

  public static FrameworkElement Icon(string key, double size, double stroke, Brush color) {
   var path = new Path { Data = Geo(key), Stroke = color, StrokeThickness = stroke, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
   if (filled.Contains(key)) path.Fill = color;
   var canvas = new Canvas { Width = 24, Height = 24 };
   canvas.Children.Add(path);
   return new Viewbox { Width = size, Height = size, Child = canvas, IsHitTestVisible = false };
  }

  public static FrameworkElement Logo(double size, Brush color) {
   var canvas = new Canvas { Width = 24, Height = 24 };
   canvas.Children.Add(new Path { Data = Geo("logo"), Stroke = color, StrokeThickness = 2.2, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
   canvas.Children.Add(new Path { Data = Geo("logo2"), Stroke = color, StrokeThickness = 2.2, Opacity = 0.5, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
   return new Viewbox { Width = size, Height = size, Child = canvas, IsHitTestVisible = false };
  }

  // ---------- controls ----------
  public static Button IconButton(string icon, string label, string style, double size, Action click) {
   var button = new Button { Style = S(style), Width = size, Height = size, ToolTip = label };
   SetIcon(button, icon, style == "PrimaryIconButton");
   AutomationProperties.SetName(button, label);
   if (click != null) button.Click += delegate { click(); };
   return button;
  }

  public static void SetIcon(Button button, string icon, bool primary) {
   double size = button.Width >= 44 ? 20 : 18;
   double stroke = icon == "gear" ? 1.6 : (icon == "pause" ? 2.6 : 1.8);
   button.Content = Icon(icon, size, stroke, primary ? Ink : Text);
  }

  public static Button Pill(string text, string icon, bool primary, Action click) {
   var row = new StackPanel { Orientation = Orientation.Horizontal };
   if (icon != null) { var glyph = Icon(icon, 16, 2, primary ? Ink : Text); glyph.Margin = new Thickness(0, 0, 8, 0); row.Children.Add(glyph); }
   row.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
   var button = new Button { Style = S(primary ? "PrimaryPill" : "Pill"), Content = row };
   AutomationProperties.SetName(button, text);
   if (click != null) button.Click += delegate { click(); };
   return button;
  }

  public static FrameworkElement DeviceTile(string icon, bool dashed, double size) {
   if (dashed) {
    var grid = new Grid { Width = size, Height = size };
    grid.Children.Add(new Rectangle { RadiusX = size * 0.3, RadiusY = size * 0.3, Stroke = Edge, StrokeThickness = 1.5, StrokeDashArray = new DoubleCollection { 3, 2.5 } });
    grid.Children.Add(Icon(icon, size * 0.5, 1.6, Text2));
    return grid;
   }
   return new Border { Width = size, Height = size, CornerRadius = new CornerRadius(size * 0.3), Background = Card, Child = Icon(icon, size * 0.5, 1.6, Text) };
  }

  // Album art, or a note glyph on a raised tile when there is none.
  public static FrameworkElement Art(BitmapSource image, double size, double radius) {
   if (image == null) {
    return new Border { Width = size, Height = size, CornerRadius = new CornerRadius(radius), Background = Card, Child = Icon("music", size * 0.42, 1.6, Text3) };
   }
   var brush = new ImageBrush(image) { Stretch = Stretch.UniformToFill };
   brush.Freeze();
   return new Border { Width = size, Height = size, CornerRadius = new CornerRadius(radius), Background = brush };
  }

  // Status is always a word plus a shape, never color alone.
  public static FrameworkElement Status(string kind, string text) {
   var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
   switch (kind) {
    case "live":
     row.Children.Add(new Ellipse { Width = 6, Height = 6, Fill = Live, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
     row.Children.Add(T(text, 12, FontWeights.Medium, Live));
     break;
    case "warn":
     var spinner = Spinner(10, Warn); spinner.Margin = new Thickness(0, 0, 6, 0);
     row.Children.Add(spinner);
     row.Children.Add(T(text, 12, FontWeights.Medium, Warn));
     break;
    default:
     row.Children.Add(T(text, 12, FontWeights.Normal, Text2));
     break;
   }
   return row;
  }

  public static FrameworkElement Spinner(double size, Brush color) {
   double r = size / 2 - 0.75;
   var arc = new Path {
    Width = size, Height = size, Stroke = color, StrokeThickness = 1.5, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
    Data = new PathGeometry(new[] { new PathFigure(new Point(size / 2, 0.75), new[] { new ArcSegment(new Point(0.75, size / 2), new Size(r, r), 0, true, SweepDirection.Clockwise, true) }, false) }),
    RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new RotateTransform(), VerticalAlignment = VerticalAlignment.Center
   };
   if (!ReduceMotion)
    arc.RenderTransform.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1)) { RepeatBehavior = RepeatBehavior.Forever });
   return arc;
  }

  public static FrameworkElement Keycap(string text) {
   var label = T(text, 11, FontWeights.SemiBold, Text);
   return new Border { Height = 22, Padding = new Thickness(7, 0, 7, 0), CornerRadius = new CornerRadius(6), Background = Card, BorderBrush = Edge, BorderThickness = new Thickness(1), Child = label, Margin = new Thickness(4, 0, 0, 0) };
  }

  public static string Clock(TimeSpan t) {
   if (t < TimeSpan.Zero) t = TimeSpan.Zero;
   return t.TotalHours >= 1 ? string.Format("{0}:{1:00}:{2:00}", (int)t.TotalHours, t.Minutes, t.Seconds) : string.Format("{0}:{1:00}", (int)t.TotalMinutes, t.Seconds);
  }
 }

 // Capsule corners: half the element's height.
 sealed class HalfConverter : System.Windows.Data.IValueConverter {
  public object Convert(object value, Type type, object parameter, System.Globalization.CultureInfo culture) { return value is double ? (double)value / 2 : 0.0; }
  public object ConvertBack(object value, Type type, object parameter, System.Globalization.CultureInfo culture) { throw new NotSupportedException(); }
 }

 // Waveform bars driven by the real output level. Newest sample sits in the centre and older
 // samples ripple outwards, so silence reads as a flat line. Bars scale with a render transform,
 // which redraws without a layout pass, and nothing is touched while everything is already flat.
 sealed class Bars : StackPanel {
  static readonly double[] Pattern = { 6, 11, 18, 26, 16, 22, 30, 20, 12, 24, 17, 9, 14, 7 };
  readonly ScaleTransform[] scales;
  readonly double[] shape, current, history;
  readonly double minHeight, maxHeight;
  bool flat = true;

  public Bars(int count, double height, double gap, Brush color) {
   Orientation = Orientation.Horizontal;
   VerticalAlignment = VerticalAlignment.Center;
   Height = height;
   minHeight = 3; maxHeight = height;
   scales = new ScaleTransform[count]; shape = new double[count]; current = new double[count];
   history = new double[count / 2 + 1];
   double peak = 0;
   for (int i = 0; i < count; i++) peak = Math.Max(peak, Pattern[i % Pattern.Length]);
   for (int i = 0; i < count; i++) {
    shape[i] = 0.35 + 0.65 * Pattern[i % Pattern.Length] / peak;
    current[i] = minHeight;
    scales[i] = new ScaleTransform(1, minHeight / maxHeight);
    Children.Add(new Rectangle { Width = 3, Height = maxHeight, RadiusX = 1.5, RadiusY = 1.5, Fill = color, Margin = new Thickness(gap / 2, 0, gap / 2, 0), VerticalAlignment = VerticalAlignment.Center, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = scales[i] });
   }
   IsHitTestVisible = false;
   AutomationProperties.SetName(this, "Desktop output level");
  }

  public void Push(double level, bool dim) {
   double opacity = dim ? 0.45 : 1;
   if (Opacity != opacity) Opacity = opacity;
   if (level < 0.005 && flat) return;
   for (int i = history.Length - 1; i > 0; i--) history[i] = history[i - 1];
   history[0] = level;
   int centre = scales.Length / 2;
   bool allFlat = true;
   for (int i = 0; i < scales.Length; i++) {
    int age = Math.Min(history.Length - 1, Math.Abs(i - centre));
    double target = minHeight + (maxHeight - minHeight) * history[age] * shape[i];
    current[i] += (target - current[i]) * (target > current[i] ? 0.65 : 0.22);
    double scale = Math.Max(minHeight, current[i]) / maxHeight;
    if (Math.Abs(scales[i].ScaleY - scale) > 0.004) scales[i].ScaleY = scale;
    if (current[i] > minHeight + 0.2) allFlat = false;
   }
   flat = allFlat;
  }
 }
}
