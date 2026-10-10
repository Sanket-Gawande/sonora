using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using Path = System.Windows.Shapes.Path;

namespace Sonora {
 public enum Dock { Top, Left, Right }

 // While the notch is dragged: an outline of where it would dock, on the nearest edge, with a
 // bar along that edge. A small window of its own that never takes focus or the mouse; it's only
 // redrawn when the edge changes, and moved otherwise.
 sealed class DockPreview : Window {
  readonly Path ghost;
  readonly Border bar;
  IntPtr hwnd;
  Dock shown = (Dock)(-1);
  Size shownSize;

  public DockPreview() {
   WindowStyle = WindowStyle.None;
   AllowsTransparency = true;
   Background = Brushes.Transparent;
   ResizeMode = ResizeMode.NoResize;
   ShowInTaskbar = false;
   ShowActivated = false;
   Topmost = true;
   Focusable = false;
   IsHitTestVisible = false;
   Width = 40; Height = 40;
   var grid = new Grid();
   ghost = new Path { Stroke = Ui.Signal, StrokeThickness = 2, StrokeDashArray = new DoubleCollection { 3, 2 }, Fill = new SolidColorBrush(Color.FromArgb(0x24, 0xA3, 0xB8, 0xFF)) };
   bar = new Border { Background = Ui.Signal };
   grid.Children.Add(ghost);
   grid.Children.Add(bar);
   Content = grid;
   SourceInitialized += delegate {
    hwnd = new WindowInteropHelper(this).Handle;
    Native.SetNoActivate(hwnd, true);
    var source = HwndSource.FromHwnd(hwnd);
    source.CompositionTarget.RenderMode = RenderMode.SoftwareOnly;
   };
  }

  // `rect`: where the closed notch would sit, in physical pixels.
  public void ShowAt(Native.RECT rect, Dock dock) {
   if (!IsVisible) Show();
   if (hwnd == IntPtr.Zero) return;
   int w = rect.Right - rect.Left, h = rect.Bottom - rect.Top;
   double scale = Native.DpiAt(rect.Left + w / 2, rect.Top + h / 2) / 96.0;
   var size = new Size(w / scale, h / scale);
   if (dock != shown || Math.Abs(size.Width - shownSize.Width) > 0.5 || Math.Abs(size.Height - shownSize.Height) > 0.5) {
    shown = dock; shownSize = size;
    ghost.Data = Tab(size.Width, size.Height, dock);
    bar.Width = dock == Dock.Top ? size.Width : 4;
    bar.Height = dock == Dock.Top ? 4 : size.Height;
    bar.HorizontalAlignment = dock == Dock.Left ? HorizontalAlignment.Left : dock == Dock.Right ? HorizontalAlignment.Right : HorizontalAlignment.Stretch;
    bar.VerticalAlignment = dock == Dock.Top ? VerticalAlignment.Top : VerticalAlignment.Stretch;
   }
   Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, rect.Left, rect.Top, w, h, Native.SWP_NOACTIVATE);
  }

  // A rectangle with its corners rounded away from the edge it sits on.
  static Geometry Tab(double w, double h, Dock dock) {
   double r = Math.Min(16, Math.Min(w, h) / 2), i = 1;
   var geometry = new StreamGeometry();
   using (var ctx = geometry.Open()) {
    switch (dock) {
     case Dock.Top:
      ctx.BeginFigure(new Point(i, 0), true, true);
      ctx.LineTo(new Point(i, h - r), true, true);
      ctx.ArcTo(new Point(i + r, h - i), new Size(r, r), 0, false, SweepDirection.Counterclockwise, true, true);
      ctx.LineTo(new Point(w - i - r, h - i), true, true);
      ctx.ArcTo(new Point(w - i, h - r), new Size(r, r), 0, false, SweepDirection.Counterclockwise, true, true);
      ctx.LineTo(new Point(w - i, 0), true, true);
      break;
     case Dock.Right:
      ctx.BeginFigure(new Point(w, i), true, true);
      ctx.LineTo(new Point(r, i), true, true);
      ctx.ArcTo(new Point(i, r), new Size(r, r), 0, false, SweepDirection.Counterclockwise, true, true);
      ctx.LineTo(new Point(i, h - r), true, true);
      ctx.ArcTo(new Point(r, h - i), new Size(r, r), 0, false, SweepDirection.Counterclockwise, true, true);
      ctx.LineTo(new Point(w, h - i), true, true);
      break;
     default:
      ctx.BeginFigure(new Point(0, i), true, true);
      ctx.LineTo(new Point(w - r, i), true, true);
      ctx.ArcTo(new Point(w - i, r), new Size(r, r), 0, false, SweepDirection.Clockwise, true, true);
      ctx.LineTo(new Point(w - i, h - r), true, true);
      ctx.ArcTo(new Point(w - r, h - i), new Size(r, r), 0, false, SweepDirection.Clockwise, true, true);
      ctx.LineTo(new Point(0, h - i), true, true);
      break;
    }
   }
   geometry.Freeze();
   return geometry;
  }
 }
}
