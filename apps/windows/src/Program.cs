using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

[assembly: AssemblyTitle("Sonora")]
[assembly: AssemblyProduct("Sonora")]
[assembly: AssemblyVersion("0.5.0.0")]
[assembly: AssemblyFileVersion("0.5.0.0")]
[assembly: AssemblyDescription("Sonora Windows notch · preview")]

namespace Sonora {
 static class Program {
  const string MutexName = "Local\\Sonora.Notch", ShowEventName = "Local\\Sonora.Notch.Show";

  [STAThread]
  static int Main(string[] args) {
   if (args.Length > 0 && args[0] == "--capture-test") {
    try { return CaptureTest.Run(args.Length > 1 ? args[1] : Environment.CurrentDirectory, Array.IndexOf(args, "--tone") >= 0, Array.IndexOf(args, "--muted") >= 0); }
    catch (Exception error) { Log(error); return 1; }
   }
   if (args.Length > 0 && args[0] == "--presence-test") {
    try { return PresenceTest.Run(args.Length > 1 ? args[1] : Environment.CurrentDirectory); }
    catch (Exception error) { Log(error); return 1; }
   }
   if (args.Length > 0 && args[0] == "--media-test") {
    try { return MediaTest.Run(args.Length > 1 ? args[1] : Environment.CurrentDirectory, Array.IndexOf(args, "--track-change") >= 0, Array.IndexOf(args, "--link") >= 0, Array.IndexOf(args, "--show") >= 0); }
    catch (Exception error) { Log(error); return 1; }
   }
   if (args.Length > 0 && args[0] == "--usb-stream") {
    int seconds;
    if (args.Length < 3 || !int.TryParse(args[2], out seconds)) seconds = 20;
    try { return UsbStreamTest.Run(args.Length > 1 ? args[1] : Environment.CurrentDirectory, seconds); }
    catch (Exception error) { Log(error); return 1; }
   }
   if (args.Length > 0 && args[0] == "--transport-test") {
    try { return TransportTest.Run(args.Length > 1 ? args[1] : Environment.CurrentDirectory); }
    catch (Exception error) { Log(error); return 1; }
   }
   bool verify = args.Length > 0 && args[0] == "--verify";
   bool background = Array.IndexOf(args, "--background") >= 0;
   try {
    bool created;
    using (var mutex = new Mutex(true, verify ? MutexName + ".Verify" : MutexName, out created)) {
     if (!created) {
      // A second launch brings the running notch forward instead of exiting silently.
      EventWaitHandle show;
      if (EventWaitHandle.TryOpenExisting(ShowEventName, out show)) { show.Set(); show.Dispose(); }
      return 0;
     }
     var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
     app.DispatcherUnhandledException += delegate(object sender, DispatcherUnhandledExceptionEventArgs e) {
      Log(e.Exception);
      e.Handled = true;
     };
     Ui.Initialize(app);
     var prefs = Preferences.Load(verify);
     var session = new Session(prefs);
     var meter = new AudioMeter();
     var media = new MediaSession();
     var notch = new NotchWindow(session, media, prefs, meter, verify);

     if (verify) return Verifier.Run(notch, session, media, prefs, args.Length > 1 ? args[1] : Environment.CurrentDirectory);
     media.Start();
     session.Watch();
     // Sonora in the Start menu, pointing at this copy; and "Start with Windows", if on, too.
     try { Launcher.Install(); Startup.Refresh(); }
     catch (Exception error) { Log(error); }
     // The streaming phone sees what's playing and controls it (docs/protocol.md).
     media.Changed += delegate { session.PublishMedia(media.Current); };
     session.PublishMedia(media.Current);
     session.MediaControl += delegate(string action, TimeSpan position) {
      switch (action) {
       case "toggle": media.TogglePlayPause(); break;
       case "play": media.Play(); break;
       case "pause": media.Pause(); break;
       case "next": media.Next(); break;
       case "previous": media.Previous(); break;
       case "seek": media.Seek(position); break;
      }
     };
     // The phone asks for the playing tab's link to open it there (docs/protocol.md, LINK).
     session.PlayingLink = delegate { return PlayingApp.Link(media.Current); };

     Tray tray = null;
     Action quit = delegate {
      prefs.Save();
      session.Disconnect();
      session.StopWatching();
      if (tray != null) tray.Dispose();
      notch.Close();
      meter.Dispose();
      app.Shutdown();
     };
     tray = new Tray(notch, session, media, quit);
     // A phone on Wi-Fi asking to pair opens the notch with its number.
     session.ApprovalRequested += delegate { notch.ShowNotch(); notch.Expand(false); };
     ListenForSecondLaunch(app, notch);
     notch.Show();
     if (!background) notch.Expand(false);
     return app.Run();
    }
   } catch (Exception error) {
    Log(error);
    if (!verify) MessageBox.Show("Sonora could not start. Details were saved to " + Preferences.LogPath, "Sonora");
    return 1;
   }
  }

  static void ListenForSecondLaunch(Application app, NotchWindow notch) {
   var show = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
   var thread = new Thread(delegate() {
    while (show.WaitOne()) app.Dispatcher.BeginInvoke(new Action(delegate { notch.ShowNotch(); notch.Expand(false); }));
   }) { IsBackground = true, Name = "Sonora second-launch listener" };
   thread.Start();
  }

  static void Log(Exception error) {
   try {
    Directory.CreateDirectory(Path.GetDirectoryName(Preferences.LogPath));
    File.AppendAllText(Preferences.LogPath, DateTime.Now.ToString("s") + "  " + error + Environment.NewLine + Environment.NewLine);
   } catch (IOException) { } catch (UnauthorizedAccessException) { }
  }
 }
}
