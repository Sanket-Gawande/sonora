using System;
using System.IO;
using System.Xml.Serialization;
using Microsoft.Win32;

namespace Sonora {
 public class Preferences {
  public string Display;
  public bool HideOverFullscreen = true;
  public bool MatchWallpaper = true;
  public bool? ReduceMotion;
  public double Volume = 72;

  static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sonora");
  static readonly string FilePath = Path.Combine(Folder, "preferences.xml");
  [XmlIgnore] public bool ReadOnly;

  public static Preferences Load(bool readOnly) {
   var prefs = new Preferences();
   if (!readOnly && File.Exists(FilePath)) {
    try { using (var file = File.OpenRead(FilePath)) prefs = (Preferences)new XmlSerializer(typeof(Preferences)).Deserialize(file); }
    catch (Exception) { prefs = new Preferences(); }
   }
   if (double.IsNaN(prefs.Volume)) prefs.Volume = 72;
   prefs.Volume = Math.Max(0, Math.Min(100, prefs.Volume));
   prefs.ReadOnly = readOnly;
   return prefs;
  }

  public void Save() {
   if (ReadOnly) return;
   try {
    Directory.CreateDirectory(Folder);
    using (var file = File.Create(FilePath)) new XmlSerializer(typeof(Preferences)).Serialize(file, this);
   } catch (IOException) { } catch (UnauthorizedAccessException) { }
  }

  public static string LogPath { get { return Path.Combine(Folder, "error.log"); } }
 }

 // "Start with Windows" lives in the per-user Run key so it survives reinstalls and needs no elevation.
 public static class Startup {
  const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
  const string ValueName = "Sonora";

  public static bool IsEnabled {
   get {
    using (var key = Registry.CurrentUser.OpenSubKey(RunKey)) return key != null && key.GetValue(ValueName) != null;
   }
  }

  public static void SetEnabled(bool enabled) {
   using (var key = Registry.CurrentUser.CreateSubKey(RunKey)) {
    if (enabled) key.SetValue(ValueName, Command);
    else key.DeleteValue(ValueName, false);
   }
  }

  // Keeps an existing entry pointing at this copy, so a moved folder or a newer release still starts.
  public static void Refresh() {
   using (var key = Registry.CurrentUser.OpenSubKey(RunKey, true)) {
    if (key == null) return;
    var current = key.GetValue(ValueName) as string;
    if (current != null && current != Command) key.SetValue(ValueName, Command);
   }
  }

  static string Command {
   get { return "\"" + System.Reflection.Assembly.GetExecutingAssembly().Location + "\" --background"; }
  }
 }
}
