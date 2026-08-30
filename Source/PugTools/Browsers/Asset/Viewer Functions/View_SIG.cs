using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text;

namespace PugTools {
  /// <summary>
  /// Reader for archive ft.sig files. SWTOR stores an RSA signature whose
  /// public-key operation yields FF..FF + 8-byte file-table hash + FF..FF.
  /// </summary>
  internal static class ViewSIG {
    private static readonly BigInteger Modulus = BigInteger.Parse(
      "2792703406818314332072380993358963849564522542681741906024287447861812735879492019569448311225509964260143299635788869810961853623797456215679381541548809801123135691486965910671008679226001946623228554983239424684166527934787330722035189286443645320539418251527606920338605037150290067865978196984808413703397469331503405088668624959641723223940921389517840147476975701728079622960544601241993282176481655565983922357693083115922324052527848693510882845884879629",
      CultureInfo.InvariantCulture);
    private static readonly BigInteger Exponent = new BigInteger(65537);

    internal sealed class SigInfo {
      internal UInt16 SignatureLength;
      internal String SignatureHex = String.Empty;
      internal String DecryptedHex = String.Empty;
      internal String FileTableHash = String.Empty;
      internal Boolean ValidEnvelope;
    }

    internal static SigInfo Parse(Stream input) {
      if (input == null) throw new ArgumentNullException(nameof(input));
      if (!input.CanSeek) throw new InvalidDataException("SIG stream must be seekable.");
      input.Position = 0;
      using BinaryReader br = new BinaryReader(input, Encoding.UTF8, true);
      if (input.Length < 2) throw new InvalidDataException("SIG file is too short.");
      UInt16 length = ReadUInt16BigEndian(br);
      if (length != 0xBF && length != 0xC0 && length != 0xC1)
        throw new InvalidDataException("Expected a 191, 192 or 193 byte SWTOR signature, got " + length + " bytes.");
      if (input.Length != 2L + length)
        throw new InvalidDataException("SIG length field does not match file size.");
      Byte[] signature = br.ReadBytes(length);
      BigInteger sig = new BigInteger(signature, isUnsigned: true, isBigEndian: true);
      BigInteger decrypted = BigInteger.ModPow(sig, Exponent, Modulus);
      String decryptedHex = decrypted.ToString("x", CultureInfo.InvariantCulture).PadLeft(48, '0');
      Boolean valid = decryptedHex.Length == 48
        && decryptedHex.StartsWith("ffffffffffffffff", StringComparison.OrdinalIgnoreCase)
        && decryptedHex.EndsWith("ffffffffffffffff", StringComparison.OrdinalIgnoreCase);
      return new SigInfo {
        SignatureLength = length,
        SignatureHex = Convert.ToHexString(signature).ToLowerInvariant(),
        DecryptedHex = decryptedHex,
        FileTableHash = valid ? decryptedHex.Substring(16, 16) : String.Empty,
        ValidEnvelope = valid
      };
    }

    internal static ArrayList BuildTree(SigInfo info) {
      ArrayList roots = new ArrayList();
      if (info == null) return roots;
      NodeListItem root = new NodeListItem("TOR file-table signature", info.ValidEnvelope ? "valid RSA envelope" : "unexpected RSA envelope");
      root.children.Add(new NodeListItem("Signature length", info.SignatureLength + " bytes (" + (info.SignatureLength * 8) + " bits)"));
      root.children.Add(new NodeListItem("Signature", GroupHex(info.SignatureHex)));
      root.children.Add(new NodeListItem("RSA public-key result", GroupHex(info.DecryptedHex)));
      root.children.Add(new NodeListItem("Expected file-table hash", String.IsNullOrEmpty(info.FileTableHash) ? "unavailable" : GroupHex(info.FileTableHash)));
      root.children.Add(new NodeListItem("Meaning", "The client compares this 8-byte value with HashLittle2 over the archive file table (excluding ft.sig)."));
      roots.Add(root);
      return roots;
    }

    private static UInt16 ReadUInt16BigEndian(BinaryReader br) {
      Byte a = br.ReadByte();
      Byte b = br.ReadByte();
      return (UInt16)((a << 8) | b);
    }

    private static String GroupHex(String hex) {
      if (String.IsNullOrWhiteSpace(hex)) return String.Empty;
      StringBuilder sb = new StringBuilder(hex.Length + hex.Length / 2);
      for (Int32 i = 0; i < hex.Length; i += 2) {
        if (sb.Length > 0) sb.Append(' ');
        sb.Append(hex, i, Math.Min(2, hex.Length - i));
      }
      return sb.ToString();
    }
  }
}
