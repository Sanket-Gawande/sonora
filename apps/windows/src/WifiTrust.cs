using System;
using System.Security.Cryptography;
using System.Text;

namespace Sonora {
 // Pairing a phone over Wi‑Fi (docs/protocol.md, "Wi‑Fi"). Both sides agree a key with ECDH on
 // P-256 and show a six-digit number derived from it; the user allows the phone on the PC only if
 // the numbers match, which rules out anyone in between. The agreed long-term key then never
 // travels: each stream's key, and the proof that lets a paired phone start one, are derived from
 // it with the connection's own challenge.
 public static class WifiTrust {
  // Public keys on the wire: the curve point's X and Y, 32 bytes each, big-endian.
  public const int PublicKeyLength = 64;

  // This PC's half of the agreement with `phonePublic`: the long-term key, and the public key the
  // phone needs for its half. Throws for anything that isn't a point on the curve.
  public static byte[] Agree(byte[] phonePublic, out byte[] pcPublic) {
   using (var pc = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256)) {
    var q = pc.ExportParameters(false).Q;
    pcPublic = Join(q.X, q.Y);
    return Agree(pc, phonePublic, phonePublic, pcPublic);
   }
  }

  // Either side's half: `ours` with the other side's public key. The long-term key is bound to both
  // public keys, phone's first.
  internal static byte[] Agree(ECDiffieHellman ours, byte[] theirPublic, byte[] phonePublic, byte[] pcPublic) {
   if (theirPublic == null || theirPublic.Length != PublicKeyLength) throw new CryptographicException("Not a P-256 public key.");
   var point = new ECParameters {
    Curve = ECCurve.NamedCurves.nistP256,
    Q = new ECPoint { X = Slice(theirPublic, 0, 32), Y = Slice(theirPublic, 32, 32) },
   };
   byte[] shared;
   try {
    using (var theirs = ECDiffieHellman.Create(point)) shared = ours.DeriveKeyFromHash(theirs.PublicKey, HashAlgorithmName.SHA256);
   } catch (PlatformNotSupportedException error) {
    // .NET reports a point off the curve this way.
    throw new CryptographicException("Not a P-256 public key.", error);
   }
   return Hmac(shared, Join(Encoding.ASCII.GetBytes("sonora-v1-pair"), phonePublic, pcPublic));
  }

  // The number both screens show, "482 913": the same derivation as a USB pairing's.
  public static string Code(byte[] longTermKey) { return new PairingOffer(longTermKey, "", 0, "").Number; }

  // JOIN's proof: HMAC-SHA256(L, "sonora-v1-wifi" ‖ challenge ‖ nonce ‖ port), in lower-case hex.
  public static string JoinProof(byte[] longTermKey, string challenge, string nonce, int port) {
   return Hex(Hmac(longTermKey, Encoding.ASCII.GetBytes("sonora-v1-wifi" + challenge + nonce + port)));
  }

  // JOINED's proof, the PC's side: HMAC-SHA256(L, "sonora-v1-joined" ‖ challenge ‖ nonce), so the
  // phone knows it reached the PC it paired with and not something answering in its name.
  public static string JoinedProof(byte[] longTermKey, string challenge, string nonce) {
   return Hex(Hmac(longTermKey, Encoding.ASCII.GetBytes("sonora-v1-joined" + challenge + nonce)));
  }

  public static bool CheckJoin(byte[] longTermKey, string challenge, string nonce, int port, string proof) {
   if (longTermKey == null || proof == null) return false;
   string expected = JoinProof(longTermKey, challenge, nonce, port);
   if (proof.Length != expected.Length) return false;
   int diff = 0;
   for (int i = 0; i < expected.Length; i++) diff |= expected[i] ^ proof[i];
   return diff == 0;
  }

  // The stream's key K: HMAC-SHA256(L, "sonora-v1-stream" ‖ challenge ‖ nonce). New for every join.
  public static byte[] StreamKey(byte[] longTermKey, string challenge, string nonce) {
   return Hmac(longTermKey, Encoding.ASCII.GetBytes("sonora-v1-stream" + challenge + nonce));
  }

  public static string Base64Url(byte[] bytes) { return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_'); }

  public static byte[] FromBase64Url(string text) {
   if (string.IsNullOrEmpty(text) || text.Length > 200) return null;
   string padded = text.Replace('-', '+').Replace('_', '/');
   padded += new string('=', (4 - padded.Length % 4) % 4);
   try { return Convert.FromBase64String(padded); } catch (FormatException) { return null; }
  }

  internal static string Hex(byte[] bytes) { return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant(); }

  static byte[] Hmac(byte[] key, byte[] data) { using (var hmac = new HMACSHA256(key)) return hmac.ComputeHash(data); }

  static byte[] Slice(byte[] bytes, int offset, int count) { var part = new byte[count]; Buffer.BlockCopy(bytes, offset, part, 0, count); return part; }

  static byte[] Join(params byte[][] parts) {
   int length = 0;
   foreach (var part in parts) length += part.Length;
   var all = new byte[length];
   int at = 0;
   foreach (var part in parts) { Buffer.BlockCopy(part, 0, all, at, part.Length); at += part.Length; }
   return all;
  }
 }
}
