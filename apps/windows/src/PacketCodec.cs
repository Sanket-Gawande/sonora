using System;
using System.Security.Cryptography;
using System.Text;

namespace Sonora {
 // Seals and opens audio packets as specified in docs/protocol.md:
 // AES-256-CTR, then a truncated HMAC-SHA256 tag over header and ciphertext, plus a replay window.
 public sealed class PacketCodec : IDisposable {
  public const int HeaderSize = 20, TagSize = 16;
  const byte Version = 1;

  readonly byte[] macKey;
  readonly ICryptoTransform block;
  readonly Aes aes;
  readonly HMACSHA256 mac;
  uint highest;
  ulong window;
  bool any;

  public PacketCodec(byte[] secret) {
   if (secret == null || secret.Length != 32) throw new ArgumentException("The session secret must be 32 bytes.");
   byte[] encKey;
   using (var kdf = new HMACSHA256(secret)) {
    encKey = kdf.ComputeHash(Encoding.ASCII.GetBytes("sonora-v1-enc"));
    macKey = kdf.ComputeHash(Encoding.ASCII.GetBytes("sonora-v1-mac"));
   }
   aes = new AesCryptoServiceProvider { Key = encKey, Mode = CipherMode.ECB, Padding = PaddingMode.None };
   block = aes.CreateEncryptor();
   mac = new HMACSHA256(macKey);
  }

  public byte[] Seal(uint streamId, uint sequence, ulong timestamp, bool silence, byte[] pcm, int offset, int count) {
   var packet = new byte[HeaderSize + count + TagSize];
   packet[0] = 0x53; packet[1] = 0x4E; packet[2] = Version; packet[3] = (byte)(silence ? 1 : 0);
   WriteUInt32(packet, 4, streamId);
   WriteUInt32(packet, 8, sequence);
   WriteUInt64(packet, 12, timestamp);
   Buffer.BlockCopy(pcm, offset, packet, HeaderSize, count);
   Crypt(packet, HeaderSize, count, streamId, sequence);
   byte[] tag = mac.ComputeHash(packet, 0, HeaderSize + count);
   Buffer.BlockCopy(tag, 0, packet, HeaderSize + count, TagSize);
   return packet;
  }

  public enum Result { Ok, Malformed, BadTag, Replay }

  // On Ok, the packet's payload region is decrypted in place.
  public Result Open(byte[] packet, int length, out uint streamId, out uint sequence, out ulong timestamp, out bool silence) {
   streamId = 0; sequence = 0; timestamp = 0; silence = false;
   if (length < HeaderSize + TagSize || packet[0] != 0x53 || packet[1] != 0x4E || packet[2] != Version) return Result.Malformed;
   int count = length - HeaderSize - TagSize;
   byte[] expected = mac.ComputeHash(packet, 0, HeaderSize + count);
   int diff = 0;
   for (int i = 0; i < TagSize; i++) diff |= expected[i] ^ packet[HeaderSize + count + i];
   if (diff != 0) return Result.BadTag;
   streamId = ReadUInt32(packet, 4);
   sequence = ReadUInt32(packet, 8);
   timestamp = ReadUInt64(packet, 12);
   silence = (packet[3] & 1) != 0;
   if (!Accept(sequence)) return Result.Replay;
   Crypt(packet, HeaderSize, count, streamId, sequence);
   return Result.Ok;
  }

  // 64-packet sliding window: rejects duplicates and anything older than the window.
  bool Accept(uint sequence) {
   if (!any) { any = true; highest = sequence; window = 1; return true; }
   if (sequence > highest) {
    uint shift = sequence - highest;
    window = shift >= 64 ? 1UL : (window << (int)shift) | 1UL;
    highest = sequence;
    return true;
   }
   uint age = highest - sequence;
   if (age >= 64) return false;
   ulong bit = 1UL << (int)age;
   if ((window & bit) != 0) return false;
   window |= bit;
   return true;
  }

  public void ResetReplayWindow() { any = false; window = 0; highest = 0; }

  void Crypt(byte[] data, int offset, int count, uint streamId, uint sequence) {
   var counter = new byte[16];
   var stream = new byte[16];
   WriteUInt32(counter, 0, streamId);
   WriteUInt32(counter, 4, sequence);
   ulong blockIndex = 0;
   for (int done = 0; done < count; done += 16, blockIndex++) {
    WriteUInt64(counter, 8, blockIndex);
    block.TransformBlock(counter, 0, 16, stream, 0);
    int n = Math.Min(16, count - done);
    for (int i = 0; i < n; i++) data[offset + done + i] ^= stream[i];
   }
  }

  static void WriteUInt32(byte[] b, int o, uint v) { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }
  static void WriteUInt64(byte[] b, int o, ulong v) { WriteUInt32(b, o, (uint)(v >> 32)); WriteUInt32(b, o + 4, (uint)v); }
  static uint ReadUInt32(byte[] b, int o) { return (uint)(b[o] << 24 | b[o + 1] << 16 | b[o + 2] << 8 | b[o + 3]); }
  static ulong ReadUInt64(byte[] b, int o) { return (ulong)ReadUInt32(b, o) << 32 | ReadUInt32(b, o + 4); }

  public void Dispose() { block.Dispose(); aes.Dispose(); mac.Dispose(); }
 }
}
