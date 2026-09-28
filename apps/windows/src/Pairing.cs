using System;
using System.Security.Cryptography;
using System.Text;

namespace Sonora {
 // A pairing offer: the secret the PC generates, the six-digit number both screens show, and the
 // link the QR code carries (docs/protocol.md). The number is derived from the secret, so matching
 // numbers mean the phone really holds the same key.
 public sealed class PairingOffer {
  public byte[] Secret { get; private set; }
  public string Host { get; private set; }
  public int Port { get; private set; }
  public string Name { get; private set; }

  public PairingOffer(string host, int port, string name) : this(NewSecret(), host, port, name) { }

  public PairingOffer(byte[] secret, string host, int port, string name) {
   if (secret == null || secret.Length != 32) throw new ArgumentException("The session secret must be 32 bytes.");
   Secret = secret; Host = host; Port = port; Name = name;
  }

  static byte[] NewSecret() {
   var secret = new byte[32];
   using (var rng = new RNGCryptoServiceProvider()) rng.GetBytes(secret);
   return secret;
  }

  // "482 913": first 4 bytes of HMAC-SHA256(secret, "sonora-v1-sas") as a big-endian number, mod 10^6.
  public string Number {
   get {
    byte[] mac;
    using (var hmac = new HMACSHA256(Secret)) mac = hmac.ComputeHash(Encoding.ASCII.GetBytes("sonora-v1-sas"));
    uint value = (uint)(mac[0] << 24 | mac[1] << 16 | mac[2] << 8 | mac[3]) % 1000000;
    string digits = value.ToString("000000");
    return digits.Substring(0, 3) + " " + digits.Substring(3);
   }
  }

  public string Link {
   get {
    return "sonora://pair?v=1&h=" + Uri.EscapeDataString(Host) + "&p=" + Port + "&n=" + Uri.EscapeDataString(Name) + "&k=" + Base64Url(Secret);
   }
  }

  static string Base64Url(byte[] data) { return Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_'); }
 }
}
