using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace Sonora {
 // Where sealed packets go. Returns false when a packet could not be delivered right now.
 public interface IPacketSink : IDisposable {
  bool Send(byte[] packet);
 }

 // Wi-Fi: one datagram per packet.
 public sealed class UdpSink : IPacketSink {
  readonly UdpClient udp;
  readonly IPEndPoint target;

  public UdpSink(IPEndPoint target) { this.target = target; udp = new UdpClient(target.AddressFamily); }

  public bool Send(byte[] packet) {
   try { udp.Send(packet, packet.Length, target); return true; }
   catch (SocketException) { return false; }
  }

  public void Dispose() { udp.Close(); }
 }

 // USB: `adb reverse` only tunnels TCP, so packets travel as [u16 big-endian length][packet]
 // over one connection the phone opens to this listener. Only the newest client is kept.
 public sealed class TcpServerSink : IPacketSink {
  readonly TcpListener listener;
  readonly Thread acceptor;
  readonly object gate = new object();
  TcpClient client;
  NetworkStream stream;
  volatile bool running = true;

  public event Action<bool> ConnectionChanged;
  public bool Connected { get { return stream != null; } }
  public int Port { get { return ((IPEndPoint)listener.LocalEndpoint).Port; } }

  public TcpServerSink(int port) {
   // Loopback only: with `adb reverse` the phone arrives through 127.0.0.1, and nothing on the LAN can connect.
   listener = new TcpListener(IPAddress.Loopback, port);
   listener.Start();
   acceptor = new Thread(Accept) { IsBackground = true, Name = "Sonora USB listener" };
   acceptor.Start();
  }

  void Accept() {
   while (running) {
    TcpClient next;
    try { next = listener.AcceptTcpClient(); }
    catch (SocketException) { return; }
    catch (ObjectDisposedException) { return; }
    next.NoDelay = true;
    // A stalled reader must not block audio capture; drop it instead.
    next.SendTimeout = 250;
    lock (gate) {
     Close();
     client = next;
     stream = next.GetStream();
    }
    Raise(true);
   }
  }

  public bool Send(byte[] packet) {
   NetworkStream s = stream;
   if (s == null) return false;
   var frame = new byte[packet.Length + 2];
   frame[0] = (byte)(packet.Length >> 8);
   frame[1] = (byte)packet.Length;
   Buffer.BlockCopy(packet, 0, frame, 2, packet.Length);
   try { s.Write(frame, 0, frame.Length); return true; }
   catch (IOException) { Drop(s); return false; }
   catch (ObjectDisposedException) { Drop(s); return false; }
  }

  void Drop(NetworkStream failed) {
   lock (gate) { if (stream != failed) return; Close(); }
   Raise(false);
  }

  void Close() {
   if (stream != null) { try { stream.Close(); } catch (IOException) { } }
   if (client != null) client.Close();
   stream = null; client = null;
  }

  void Raise(bool connected) { var handler = ConnectionChanged; if (handler != null) handler(connected); }

  public void Dispose() {
   running = false;
   listener.Stop();
   lock (gate) Close();
  }
 }
}
