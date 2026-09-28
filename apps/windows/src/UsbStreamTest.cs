using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace Sonora {
 // `Sonora.exe --usb-stream <dir> [seconds]`: pairs with the USB phone and streams real desktop
 // audio to it for a while, writing what happened to usb-stream.txt. The phone must confirm the
 // number before it connects.
 static class UsbStreamTest {
  public static int Run(string directory, int seconds) {
   Directory.CreateDirectory(directory);
   var report = new StringBuilder();
   string problem;
   var link = UsbLink.Find(out problem);
   if (link == null) { report.AppendLine("No stream: " + problem); Write(directory, report); return 2; }
   report.AppendLine("Phone: " + link.Model + " (" + link.Serial + ")");
   if (!link.IsAppInstalled()) { report.AppendLine("No stream: the Sonora app isn't installed on the phone."); Write(directory, report); return 2; }

   var clock = Stopwatch.StartNew();
   double connectedAt = -1;
   int connections = 0;
   using (var sink = new TcpServerSink(UsbLink.Port)) {
    sink.ConnectionChanged += delegate(bool on) { if (on) { connections++; if (connectedAt < 0) connectedAt = clock.Elapsed.TotalSeconds; } };
    if (!link.Reverse(UsbLink.Port)) { report.AppendLine("No stream: adb reverse failed."); Write(directory, report); return 2; }
    var offer = new PairingOffer("127.0.0.1", UsbLink.Port, Environment.MachineName);
    report.AppendLine("Pairing number: " + offer.Number);
    if (!link.DeliverOffer(offer, null)) { report.AppendLine("No stream: couldn't open Sonora on the phone."); Write(directory, report); return 2; }
    File.WriteAllText(Path.Combine(directory, "number.txt"), offer.Number, Encoding.UTF8);

    using (var sender = new AudioSender(offer.Secret, sink)) {
     sender.Start();
     report.AppendLine("Capture: " + sender.Format);
     for (int i = 0; i < seconds; i++) Thread.Sleep(1000);
     report.AppendLine(connectedAt >= 0
      ? "Phone connected " + connectedAt.ToString("0.0") + " s after the offer (" + connections + " connection" + (connections == 1 ? "" : "s") + ")"
      : "Phone never connected (was the number confirmed?)");
     report.AppendLine("Packets delivered: " + sender.PacketsSent + " (" + (sender.PacketsSent * 5 / 1000.0).ToString("0.0") + " s of audio)");
    }
    link.RemoveReverse(UsbLink.Port);
   }
   Write(directory, report);
   return 0;
  }

  static void Write(string directory, StringBuilder report) { File.WriteAllText(Path.Combine(directory, "usb-stream.txt"), report.ToString(), Encoding.UTF8); }
 }
}
