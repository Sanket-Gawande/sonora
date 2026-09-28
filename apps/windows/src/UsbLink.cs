using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace Sonora {
 // A phone on a USB cable with debugging on. `adb reverse` lets the phone reach this PC at
 // 127.0.0.1:<port>, and the pairing link is handed to the Sonora app over the same cable,
 // so the key never crosses the network.
 public sealed class UsbLink {
  public const int Port = 47210;
  public const string NoAdb = "Sonora needs Android's platform-tools: unzip them next to Sonora.exe, then try again.";
  const string Package = "app.sonora.receiver";

  readonly string adb;
  public string Serial { get; private set; }
  public string Model { get; private set; }

  internal UsbLink(string adb, string serial) { this.adb = adb; Serial = serial; }
  internal UsbLink(string adb, string serial, string model) : this(adb, serial) { Model = model; }

  public static UsbLink Find(out string problem) { return Find(null, out problem); }

  // `serial`: the phone that asked to connect; null picks the first ready phone.
  public static UsbLink Find(string serial, out string problem) {
   string adb = FindAdb();
   if (adb == null) { problem = NoAdb; return null; }
   string output = Run(adb, "devices");
   var ready = new List<string>();
   bool unauthorized = false;
   foreach (var line in output.Split('\n')) {
    var parts = line.Trim().Split('\t');
    if (parts.Length != 2 || (serial != null && parts[0] != serial)) continue;
    // Real phones first: an emulator's audio comes back out of this PC's speakers.
    if (parts[1] == "device") { if (parts[0].StartsWith("emulator-")) ready.Add(parts[0]); else ready.Insert(0, parts[0]); }
    else if (parts[1] == "unauthorized") unauthorized = true;
   }
   if (ready.Count == 0) {
    problem = unauthorized ? "Allow USB debugging on the phone, then try again." : "No phone found. Connect it with USB debugging on.";
    return null;
   }
   var link = new UsbLink(adb, ready[0]);
   link.ReadModel();
   problem = null;
   return link;
  }

  internal string ReadModel() {
   Model = Shell("getprop ro.product.model").Trim();
   if (Model.Length == 0 || Model.Contains("error")) Model = Serial;
   return Model;
  }

  // Where adb is looked for: a platform-tools folder next to Sonora.exe (the easy way for a
  // download: unzip Google's platform-tools beside it), then PATH, then the Android SDK.
  internal static string FindAdb() {
   var candidates = new List<string>();
   string here = AppDomain.CurrentDomain.BaseDirectory;
   candidates.Add(Path.Combine(here, "platform-tools", "adb.exe"));
   candidates.Add(Path.Combine(here, "adb.exe"));
   foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
    if (dir.Length > 0) candidates.Add(Path.Combine(dir.Trim('"'), "adb.exe"));
   foreach (var variable in new[] { "ANDROID_HOME", "ANDROID_SDK_ROOT" }) {
    string root = Environment.GetEnvironmentVariable(variable);
    if (!string.IsNullOrEmpty(root)) candidates.Add(Path.Combine(root, "platform-tools", "adb.exe"));
   }
   candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Android", "Sdk", "platform-tools", "adb.exe"));
   foreach (var path in candidates) { try { if (File.Exists(path)) return path; } catch (ArgumentException) { } }
   return null;
  }

  public bool IsAppInstalled() { return Shell("pm path " + Package).Contains("package:"); }

  public bool Reverse(int port) { return Reverse(port, port); }

  // The phone's 127.0.0.1:<phonePort> reaches this PC's 127.0.0.1:<pcPort> while the cable is in.
  public bool Reverse(int phonePort, int pcPort) {
   Run(adb, "-s " + Serial + " reverse tcp:" + phonePort + " tcp:" + pcPort);
   return Run(adb, "-s " + Serial + " reverse --list").Contains("tcp:" + phonePort + " tcp:" + pcPort);
  }

  public void RemoveReverse(int port) { Run(adb, "-s " + Serial + " reverse --remove tcp:" + port); }

  // Opens the Sonora app with the pairing link as an intent extra. Extras, unlike intent data,
  // aren't written to the system log. `request` is the token the app sent when it asked to
  // connect (32 hex digits, checked by PhoneWatcher); the app skips the number for its own request.
  public bool DeliverOffer(PairingOffer offer, string request) {
   string extra = request == null ? "" : " --es sonora_request " + request;
   string result = Shell("am start -n " + Package + "/.MainActivity --es sonora_link '" + offer.Link + "'" + extra);
   return !result.Contains("Error");
  }

  string Shell(string command) { return Run(adb, "-s " + Serial + " shell \"" + command + "\""); }

  // A wedged adb server hangs every client, so nothing here waits more than `timeout`.
  internal static string Run(string exe, string arguments, int timeout = 15000) {
   var info = new ProcessStartInfo(exe, arguments) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
   using (var process = Process.Start(info)) {
    var output = process.StandardOutput.ReadToEndAsync();
    var error = process.StandardError.ReadToEndAsync();
    if (!process.WaitForExit(timeout)) {
     try { process.Kill(); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
     return "";
    }
    // A server started by `adb start-server` can inherit the pipes and keep them open; don't wait on it.
    return Task.WaitAll(new Task[] { output, error }, 2000) ? output.Result + error.Result : "";
   }
  }
 }
}
