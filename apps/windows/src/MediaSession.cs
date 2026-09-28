using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Foundation;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace Sonora {
 // One snapshot of what is playing, as Windows reports it.
 public sealed class NowPlaying {
  public string Title, Artist, App;
  public bool Playing, CanToggle, CanPrevious, CanNext, CanSeek;
  public TimeSpan Position, Duration;
  public DateTime PositionAt;
  public BitmapSource Art;
  // The picture as the app sent it, for a sharper copy than the notch's 160 px one.
  public byte[] ArtBytes;

  // Apps report position occasionally; between reports it advances with the clock while playing.
  public TimeSpan PositionNow {
   get {
    if (!Playing || Duration <= TimeSpan.Zero) return Position;
    TimeSpan position = Position + (DateTime.UtcNow - PositionAt);
    return position > Duration ? Duration : position;
   }
  }
 }

 // What is playing on this PC, from Windows' system media sessions (the same source as the
 // media card in the volume flyout): title, artist, app, artwork and position, plus the
 // playing app's own previous, play/pause, next and seek.
 public sealed class MediaSession {
  readonly Dispatcher dispatcher;
  readonly TypedEventHandler<GlobalSystemMediaTransportControlsSession, MediaPropertiesChangedEventArgs> onProperties;
  readonly TypedEventHandler<GlobalSystemMediaTransportControlsSession, PlaybackInfoChangedEventArgs> onPlayback;
  readonly TypedEventHandler<GlobalSystemMediaTransportControlsSession, TimelinePropertiesChangedEventArgs> onTimeline;
  GlobalSystemMediaTransportControlsSessionManager manager;
  GlobalSystemMediaTransportControlsSession session;
  string artKey;
  BitmapSource art;
  byte[] artBytes;
  ulong artHash;
  bool pinned, reading, again, reloadArt = true;
  DispatcherTimer poll;
  int polls;

  public event EventHandler Changed;
  public NowPlaying Current { get; private set; }
  // False on Windows 10 before 1809, which has no system media session API.
  public bool Available { get; private set; }

  public MediaSession() {
   dispatcher = Dispatcher.CurrentDispatcher;
   // Assume the API exists until Windows says otherwise, so startup doesn't flash a warning.
   Available = true;
   // Only a metadata update can carry new artwork.
   onProperties = delegate { Post(delegate { reloadArt = true; Refresh(); }); };
   onPlayback = delegate { Post(Refresh); };
   onTimeline = delegate { Post(Refresh); };
  }

  public async void Start() {
   // Called before the WPF message loop runs, when the thread has no context yet; without one,
   // every continuation below would resume on a pool thread and touch the UI from there.
   if (SynchronizationContext.Current == null) SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
   try {
    manager = await Await(GlobalSystemMediaTransportControlsSessionManager.RequestAsync());
    Available = true;
    manager.CurrentSessionChanged += delegate { Post(Attach); };
    manager.SessionsChanged += delegate { Post(Attach); };
    // Windows' session events go missing now and then (and some apps send few): look again every
    // 5 s, and re-read the artwork every 15 s, by its bytes, so an unchanged picture costs nothing.
    poll = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromSeconds(5) };
    poll.Tick += delegate { if (++polls % 3 == 0) reloadArt = true; Attach(); };
    poll.Start();
    Attach();
   } catch (Exception) {
    Available = false;
    Publish(null);
   }
  }

  void Post(Action action) { dispatcher.BeginInvoke(action); }

  void Attach() {
   if (manager == null) return;
   GlobalSystemMediaTransportControlsSession next;
   try { next = manager.GetCurrentSession(); } catch (Exception) { next = null; }
   if (!ReferenceEquals(next, session)) {
    if (session != null) {
     try {
      session.MediaPropertiesChanged -= onProperties;
      session.PlaybackInfoChanged -= onPlayback;
      session.TimelinePropertiesChanged -= onTimeline;
     } catch (Exception) { }
    }
    session = next;
    artKey = null;
    reloadArt = true;
    if (session != null) {
     session.MediaPropertiesChanged += onProperties;
     session.PlaybackInfoChanged += onPlayback;
     session.TimelinePropertiesChanged += onTimeline;
    }
   }
   Refresh();
  }

  // One read at a time, never cancelled: anything that changes meanwhile asks for one more read,
  // so the latest state always lands, however fast an app fires events.
  async void Refresh() {
   if (!dispatcher.CheckAccess()) { Post(Refresh); return; }
   if (pinned) return;
   if (reading) { again = true; return; }
   reading = true;
   try {
    do { again = false; await ReadOnce(); } while (again);
   } finally {
    reading = false;
   }
  }

  async Task ReadOnce() {
   var source = session;
   if (source == null) { Publish(null); return; }
   bool wantArt = reloadArt;
   reloadArt = false;
   try {
    var properties = await Within(Await(source.TryGetMediaPropertiesAsync()), 3000);
    if (!ReferenceEquals(source, session)) { again = true; return; }
    var info = source.GetPlaybackInfo();
    var timeline = source.GetTimelineProperties();
    var controls = info.Controls;
    var now = new NowPlaying {
     Title = properties.Title ?? "",
     Artist = string.IsNullOrEmpty(properties.Artist) ? (properties.AlbumArtist ?? "") : properties.Artist,
     App = AppName(source.SourceAppUserModelId),
     Playing = info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
     CanToggle = controls.IsPlayPauseToggleEnabled || controls.IsPlayEnabled || controls.IsPauseEnabled,
     CanPrevious = controls.IsPreviousEnabled,
     CanNext = controls.IsNextEnabled,
     CanSeek = controls.IsPlaybackPositionEnabled,
     Duration = timeline.EndTime - timeline.StartTime,
     Position = timeline.Position - timeline.StartTime
    };
    now.PositionAt = timeline.LastUpdatedTime.Year > 2000 ? timeline.LastUpdatedTime.UtcDateTime : DateTime.UtcNow;
    // A new track never shows the last one's picture.
    string key = now.Title + "\n" + now.Artist + "\n" + now.App;
    if (key != artKey) { artKey = key; art = null; artBytes = null; artHash = 0; wantArt = true; }
    // Browsers announce a track with a placeholder (their own icon) and send the real cover a
    // moment later in another update, so every metadata update re-reads the picture; its bytes
    // decide whether anything changed.
    if (wantArt) {
     var bytes = await LoadArt(properties.Thumbnail);
     if (!ReferenceEquals(source, session)) { again = true; return; }
     ulong hash = bytes == null ? 0 : Fnv(bytes);
     if (bytes != null && hash != artHash) {
      var image = Decode(bytes);
      if (image != null) { art = image; artBytes = bytes; artHash = hash; }
     }
    }
    now.Art = art;
    now.ArtBytes = artBytes;
    // A session with nothing loaded (a closed tab, a stopped player) isn't worth a card.
    Publish(now.Title.Length == 0 && !now.Playing ? null : now);
   } catch (Exception) {
    // A slow or failing app: keep what's shown, and read again on the next event or check.
    if (wantArt) reloadArt = true;
    if (session == null) Publish(null);
   }
  }

  // The thumbnail's bytes (at most 8 MB), or null. Never throws, never waits more than a few seconds.
  static async Task<byte[]> LoadArt(IRandomAccessStreamReference thumbnail) {
   if (thumbnail == null) return null;
   try {
    using (var stream = await Within(Await(thumbnail.OpenReadAsync()), 3000)) {
     if (stream.Size == 0 || stream.Size > 8 * 1024 * 1024) return null;
     var reader = new DataReader(stream.GetInputStreamAt(0));
     uint loaded = await Within(Await(reader.LoadAsync((uint)stream.Size)), 3000);
     var bytes = new byte[loaded];
     reader.ReadBytes(bytes);
     return bytes;
    }
   } catch (Exception) {
    return null;
   }
  }

  static BitmapSource Decode(byte[] bytes) {
   try {
    var image = new BitmapImage();
    image.BeginInit();
    image.CacheOption = BitmapCacheOption.OnLoad;
    image.DecodePixelWidth = 160;
    image.StreamSource = new MemoryStream(bytes);
    image.EndInit();
    image.Freeze();
    return image;
   } catch (Exception) {
    return null;
   }
  }

  static ulong Fnv(byte[] bytes) {
   ulong hash = 14695981039346656037UL;
   foreach (byte b in bytes) { hash ^= b; hash *= 1099511628211UL; }
   return hash == 0 ? 1 : hash;
  }

  static async Task<T> Within<T>(Task<T> task, int milliseconds) {
   if (await Task.WhenAny(task, Task.Delay(milliseconds)) != task) throw new TimeoutException();
   return await task;
  }

  // Diagnostics: every session Windows knows, not just the current one ("title · app · state"),
  // for `--media-test`. Browsers keep one per tab that plays media.
  internal async Task<string[]> DescribeSessions() {
   if (manager == null) return new string[0];
   var lines = new System.Collections.Generic.List<string>();
   foreach (var s in manager.GetSessions()) {
    string title = "?";
    try { var p = await Within(Await(s.TryGetMediaPropertiesAsync()), 3000); title = p.Title; } catch (Exception) { }
    lines.Add(title + " · " + AppName(s.SourceAppUserModelId) + " · " + s.GetPlaybackInfo().PlaybackStatus);
   }
   return lines.ToArray();
  }

  // "Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic" → "Media Player", "chrome.exe" → "Chrome".
  static string AppName(string id) {
   if (string.IsNullOrEmpty(id)) return "";
   string name = id;
   int bang = name.IndexOf('!');
   if (bang >= 0) name = name.Substring(bang + 1);
   if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 4);
   int dot = name.LastIndexOf('.');
   if (dot >= 0 && dot < name.Length - 1) name = name.Substring(dot + 1);
   switch (name.ToLowerInvariant()) {
    case "chrome": return "Chrome";
    case "msedge": return "Edge";
    case "firefox": return "Firefox";
    case "brave": return "Brave";
    case "opera": return "Opera";
    case "spotify": return "Spotify";
    case "zunemusic": return "Media Player";
    case "zunevideo": return "Movies & TV";
    case "vlc": return "VLC";
   }
   return name.Length == 0 ? "" : char.ToUpperInvariant(name[0]) + name.Substring(1);
  }

  // ---------- controls: they act on the playing app itself ----------
  public void TogglePlayPause() {
   var source = session;
   if (source == null) return;
   // Flip the button at once; the app's own update confirms it a moment later.
   // Freeze or restart the clock where it is now, so pausing doesn't snap back to the last report.
   if (Current != null) { Current.Position = Current.PositionNow; Current.PositionAt = DateTime.UtcNow; Current.Playing = !Current.Playing; Raise(); }
   Fire(source.TryTogglePlayPauseAsync());
  }
  // Explicit play and pause, for the phone's headphone buttons: a press can't flip the wrong way
  // when the phone's picture of the state is a moment old.
  public void Play() { SetPlaying(true); }
  public void Pause() { SetPlaying(false); }

  void SetPlaying(bool play) {
   var source = session;
   if (source == null) return;
   if (Current != null && Current.Playing != play) { Current.Position = Current.PositionNow; Current.PositionAt = DateTime.UtcNow; Current.Playing = play; Raise(); }
   Fire(play ? source.TryPlayAsync() : source.TryPauseAsync());
  }

  public void Next() { var source = session; if (source != null) Fire(source.TrySkipNextAsync()); }
  public void Previous() { var source = session; if (source != null) Fire(source.TrySkipPreviousAsync()); }
  public void Seek(TimeSpan position) {
   var source = session;
   if (source == null || Current == null || !Current.CanSeek) return;
   if (position < TimeSpan.Zero) position = TimeSpan.Zero;
   if (Current.Duration > TimeSpan.Zero && position > Current.Duration) position = Current.Duration;
   Current.Position = position; Current.PositionAt = DateTime.UtcNow; Raise();
   Fire(source.TryChangePlaybackPositionAsync(position.Ticks));
  }

  static async void Fire(IAsyncOperation<bool> operation) {
   try { await Await(operation); } catch (Exception) { }
  }

  // Bridges a WinRT async operation to a Task. The in-box compiler can't match .NET's own
  // GetAwaiter for WinRT types against the per-namespace Windows metadata, so this does it directly.
  static Task<T> Await<T>(IAsyncOperation<T> operation) {
   var source = new TaskCompletionSource<T>();
   operation.Completed = delegate(IAsyncOperation<T> done, AsyncStatus status) {
    if (status == AsyncStatus.Completed) source.TrySetResult(done.GetResults());
    else if (status == AsyncStatus.Canceled) source.TrySetCanceled();
    else source.TrySetException(done.ErrorCode ?? new InvalidOperationException("Windows media request failed"));
   };
   return source.Task;
  }

  // Verification only: show a fixed track without touching Windows.
  internal void ShowForVerification(NowPlaying now) {
   pinned = true;
   Publish(now);
  }

  void Publish(NowPlaying now) {
   if (!dispatcher.CheckAccess()) { dispatcher.BeginInvoke(new Action(delegate { Publish(now); })); return; }
   Current = now;
   Raise();
  }
  void Raise() { var handler = Changed; if (handler != null) handler(this, EventArgs.Empty); }
 }
}
