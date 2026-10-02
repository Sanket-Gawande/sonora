using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Sonora {
 // `Sonora.exe --media-test <dir> [--link] [--show]`: reads what Windows says is playing for four seconds and
 // writes it to media.txt (and the artwork to art.png). Touches nothing; the playing app keeps playing.
 // `--media-test <dir> --track-change`: the regression check for "the second song keeps the first
 // one's details". Skips to the next track, then checks that the reader that watched the change
 // shows the same title and artwork as a fresh one; then goes back a track and restores pause.
 static class MediaTest {
  // `--link`: also the playing tab's link, as the phone would get it; `--show`: bring the playing
  // app (or tab) forward, as a click on the notch's track does.
  public static int Run(string directory, bool trackChange, bool link, bool show) {
   Directory.CreateDirectory(directory);
   var report = new StringBuilder();
   var media = new MediaSession();
   int updates = 0;
   media.Changed += delegate { updates++; };
   media.Start();
   Wait(4);

   report.AppendLine("Media session API available: " + (media.Available ? "yes" : "no"));
   // Every session Windows knows (browsers keep one per tab), not only the current one.
   var task = media.DescribeSessions();
   var until = DateTime.UtcNow.AddSeconds(5);
   while (!task.IsCompleted && DateTime.UtcNow < until) Pump(100);
   if (task.IsCompleted && !task.IsFaulted) foreach (var line in task.Result) report.AppendLine("Session: " + line);
   report.AppendLine("Updates received: " + updates);
   var now = media.Current;
   Describe(report, now);
   if (now != null && now.Art != null) Save(now.Art, Path.Combine(directory, "art.png"));
   if (now != null) {
    // The cover the streaming phone would be sent.
    var cover = Session.PhoneCover(now.ArtBytes, now.Art);
    if (cover == null) report.AppendLine("Phone cover: none");
    else {
     var frame = BitmapFrame.Create(new MemoryStream(cover), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
     report.AppendLine("Phone cover: " + frame.PixelWidth + "×" + frame.PixelHeight + " JPEG, " + (cover.Length / 1024) + " KB (cover.jpg)");
     File.WriteAllBytes(Path.Combine(directory, "cover.jpg"), cover);
    }
   }
   int result = 0;
   if (link && now != null) {
    var clock = System.Diagnostics.Stopwatch.StartNew();
    string url = PlayingApp.Link(now);
    report.AppendLine("Link: " + (url ?? "none") + " (" + clock.ElapsedMilliseconds + " ms, app ID " + now.AppId + ")");
   }
   if (show && now != null) {
    var clock = System.Diagnostics.Stopwatch.StartNew();
    report.AppendLine("Show: " + (PlayingApp.Show(now) ? "brought forward" : "not found") + " (" + clock.ElapsedMilliseconds + " ms)");
   }

   if (trackChange) {
    if (now == null || !now.CanNext) {
     report.AppendLine("Track change: skipped, nothing playing that can skip.");
    } else {
     bool wasPlaying = now.Playing;
     string first = now.Title;
     media.Next();
     Wait(8);
     var watched = media.Current;
     var fresh = new MediaSession();
     fresh.Start();
     Wait(4);
     var truth = fresh.Current;
     report.AppendLine();
     report.AppendLine("After Next, the reader that watched the change:");
     Describe(report, watched);
     report.AppendLine("A fresh reader:");
     Describe(report, truth);
     bool moved = watched != null && watched.Title != first;
     bool same = watched != null && truth != null && watched.Title == truth.Title && watched.Artist == truth.Artist && Hash(watched.Art) == Hash(truth.Art);
     report.AppendLine((moved ? "PASS" : "FAIL") + " the title moved on to the next track");
     report.AppendLine((same ? "PASS" : "FAIL") + " title, artist and artwork match a fresh read");
     if (watched != null && watched.Art != null) Save(watched.Art, Path.Combine(directory, "art-after-next.png"));
     if (!moved || !same) result = 1;

     // Put things back as they were.
     media.Previous();
     Wait(3);
     var back = media.Current;
     if (back != null && back.Playing != wasPlaying && back.CanToggle) media.TogglePlayPause();
     Wait(1);
     report.AppendLine("Restored: " + (media.Current == null ? "nothing" : media.Current.Title + (media.Current.Playing ? " (playing)" : " (paused)")));
    }
   }
   File.WriteAllText(Path.Combine(directory, "media.txt"), report.ToString(), Encoding.UTF8);
   return result;
  }

  static void Wait(int seconds) { Pump(seconds * 1000); }

  static void Pump(int milliseconds) {
   var frame = new DispatcherFrame();
   var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
   timer.Tick += delegate { timer.Stop(); frame.Continue = false; };
   timer.Start();
   Dispatcher.PushFrame(frame);
  }

  static void Describe(StringBuilder report, NowPlaying now) {
   if (now == null) { report.AppendLine("Nothing playing (no current media session with a title)"); return; }
   report.AppendLine("App: " + now.App);
   report.AppendLine("Title: " + now.Title);
   report.AppendLine("Artist: " + now.Artist);
   report.AppendLine("State: " + (now.Playing ? "playing" : "paused"));
   report.AppendLine("Position: " + Ui.Clock(now.PositionNow) + " of " + (now.Duration > TimeSpan.Zero ? Ui.Clock(now.Duration) : "unknown length"));
   report.AppendLine("Controls: play/pause " + now.CanToggle + ", previous " + now.CanPrevious + ", next " + now.CanNext + ", seek " + now.CanSeek);
   report.AppendLine("Artwork: " + (now.Art == null ? "none" : now.Art.PixelWidth + "×" + now.Art.PixelHeight + ", " + Hash(now.Art)));
  }

  // The picture's pixels, so two decodes of the same artwork compare equal.
  static string Hash(BitmapSource image) {
   if (image == null) return "none";
   var converted = new FormatConvertedBitmap(image, System.Windows.Media.PixelFormats.Bgra32, null, 0);
   int stride = converted.PixelWidth * 4;
   var pixels = new byte[stride * converted.PixelHeight];
   converted.CopyPixels(pixels, stride, 0);
   using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(pixels), 0, 6).Replace("-", "").ToLowerInvariant();
  }

  static void Save(BitmapSource image, string path) {
   var encoder = new PngBitmapEncoder();
   encoder.Frames.Add(BitmapFrame.Create(image));
   using (var file = File.Create(path)) encoder.Save(file);
  }
 }
}
