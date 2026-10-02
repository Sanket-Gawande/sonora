using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Xml.Serialization;
using Microsoft.Win32;

namespace Sonora {
 public class Preferences {
  public string Display;
  public bool HideOverFullscreen = true;
  public bool MatchWallpaper = true;
  public bool? ReduceMotion;
  public double Volume = 72;
  // Phones on the Wi-Fi may find and connect to this PC (pairing still needs Allow). Off: USB only.
  public bool AllowWifi = true;
  // This PC's ID for phones on Wi‑Fi (so a paired phone knows it whatever its address), made once.
  public string PcId;
  // Phones paired over Wi‑Fi. Their keys are encrypted for this Windows user (DPAPI).
  public List<WifiPhone> WifiPhones = new List<WifiPhone>();

  public class WifiPhone {
   public string Id, Model, Key;
  }

  static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sonora");
  static readonly string FilePath = Path.Combine(Folder, "preferences.xml");
  [XmlIgnore] public bool ReadOnly;

  public static Preferences Load(bool readOnly) {
   var prefs = new Preferences();
   if (!readOnly && File.Exists(FilePath)) {
    try { using (var file = File.OpenRead(FilePath)) prefs = (Preferences)new XmlSerializer(typeof(Preferences)).Deserialize(file); }
    catch (Exception) { prefs = new Preferences(); }
   }
   prefs.ReadOnly = readOnly;
   if (double.IsNaN(prefs.Volume)) prefs.Volume = 72;
   if (prefs.WifiPhones == null) prefs.WifiPhones = new List<WifiPhone>();
   if (prefs.PcId == null || prefs.PcId.Length != 16) {
    var id = new byte[8];
    using (var rng = new RNGCryptoServiceProvider()) rng.GetBytes(id);
    prefs.PcId = BitConverter.ToString(id).Replace("-", "").ToLowerInvariant();
    prefs.Save();
   }
   prefs.Volume = Math.Max(0, Math.Min(100, prefs.Volume));
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

  // The long-term key of a phone paired over Wi‑Fi, or null.
  // Called on the phone watcher's threads as well as the UI's.
  public byte[] WifiKey(string phoneId) {
   lock (WifiPhones) foreach (var phone in WifiPhones) {
    if (phone.Id != phoneId || phone.Key == null) continue;
    try { return ProtectedData.Unprotect(Convert.FromBase64String(phone.Key), null, DataProtectionScope.CurrentUser); }
    catch (CryptographicException) { return null; } catch (FormatException) { return null; }
   }
   return null;
  }

  public void RememberWifi(string phoneId, string model, byte[] key) {
   lock (WifiPhones) {
    WifiPhones.RemoveAll(delegate(WifiPhone p) { return p.Id == phoneId; });
    WifiPhones.Add(new WifiPhone { Id = phoneId, Model = model, Key = Convert.ToBase64String(ProtectedData.Protect(key, null, DataProtectionScope.CurrentUser)) });
   }
   Save();
  }

  public void ForgetWifi() { lock (WifiPhones) WifiPhones.Clear(); Save(); }
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
