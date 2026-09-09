using System;
using System.IO;
using System.Text;

namespace PugTools {
  /// <summary>
  /// Lightweight content sniffer adapted from Jedipedia's file_types_resolver.js.
  /// It is used only after an asset has already been opened for preview, so the large
  /// Asset Browser index still avoids decompressing unnamed files at startup.
  ///
  /// The return value is an Asset Browser dispatch extension (DDS, DAT, AAM, ...).
  /// A declared type is kept unless it is unknown/generic or the payload proves that a
  /// more useful SWTOR subtype is present (for example an am_*.xml with root &lt;aam&gt;).
  /// </summary>
  internal static class JedipediaFileTypeResolver {
    private const Int32 XmlScanLimit = 262144;

    internal static String Resolve(Stream input, String declaredExtension) {
      String declared = Normalize(declaredExtension);
      if (input == null || !input.CanSeek) return declared;

      Int64 oldPosition = input.Position;
      try {
        input.Position = 0;
        Int32 length = (Int32)Math.Min(input.Length, XmlScanLimit);
        if (length <= 0) return declared;
        Byte[] data = new Byte[length];
        Int32 total = 0;
        while (total < data.Length) {
          Int32 read = input.Read(data, total, data.Length - total);
          if (read <= 0) break;
          total += read;
        }
        if (total != data.Length) Array.Resize(ref data, total);
        if (data.Length == 0) return declared;

        String sniffed = ResolveBytes(data, declared);
        return String.IsNullOrWhiteSpace(sniffed) ? declared : sniffed;
      }
      catch (Exception ex) when (ex is IOException || ex is ArgumentException
                                 || ex is ObjectDisposedException || ex is NotSupportedException) {
        return declared;
      }
      finally {
        try { input.Position = oldPosition; } catch { }
      }
    }

    private static String ResolveBytes(Byte[] data, String declared) {
      // XML is worth refining even when the filename is already known: Animation Actor
      // Model files are shipped as am_*.xml and otherwise look like an ordinary XML asset.
      if (String.Equals(declared, "XML", StringComparison.OrdinalIgnoreCase)) {
        String root = XmlRootTag(data);
        if (String.Equals(root, "aam", StringComparison.OrdinalIgnoreCase)) return "AAM";
        return declared;
      }

      // DAT, CLO, GR2, INFO and LIST are generic filename extensions. Their existing
      // viewers already understand the variants, so keep the dispatch key stable.
      if (!IsUnknown(declared)) return declared;

      if (data.Length >= 4) {
        UInt32 magic = ReadU32(data, 0);
        switch (magic) {
          case 0x4B554250u: return "BKT";      // PBUK
          case 0x44484B42u: return "BNK";      // BKHD
          case 0x424C4244u: return "GOM";      // DBLB
          case 0x464E4950u: return "INFO";     // PINF
          case 0x4B434250u: return "INFO";     // PBCK
          case 0x46454453u: return "LIST";     // SDEF
          case 0x53444953u: return "LIST";     // SIDS
          case 0x20534444u: return "DDS";      // "DDS "
          case 0x54504353u: return "SCPT";     // SCPT
          case 0x45434146u: return "FXE";      // FACE; AssetBrowser has FXE -> FXA fallback
          case 0x42574147u: return "GR2";      // GAWB/BWAG
          case 0x4257414Au: return "JBA";      // JAWB/BWAJ
          case 0x4257414Du: return "MPH";      // MAWB/BWAM
          case 0xC06CDE29u: return "GR2";      // Granny
          case 0x42434C4Fu: return "CLO";      // OLCB/BCLO
          case 0x544F5250u: return "NODE";     // PROT
          case 0x6479614Bu: return "FBX";      // Kayd...
          case 0x20584D41u: return "AMX";      // "AMX "
          case 0x000003E8u: return "SPT";
          case 0x08584643u:
          case 0x09584643u:
          case 0x0A584643u:
          case 0x0B584643u:
          case 0x08584647u:
          case 0x09584647u:
          case 0x0A584647u:
          case 0x0B584647u: return "GFX";
          case 0x08535743u:
          case 0x09535743u:
          case 0x0A535743u:
          case 0x0B535743u:
          case 0x08535746u:
          case 0x09535746u:
          case 0x0A535746u:
          case 0x0B535746u: return "SWF";
          case 0x73657061u: return "TXT";      // APES - version.txt
          case 0x475B0A0Du: return "INI";      // dev_keybindings.ini
          case 0x006E003Cu: return "FXSPEC";   // UTF-16LE <nodeWClasses>
          case 0x646F6E3Cu: return "FXSPEC";   // <nod...
          case 0x3C3E763Cu: return "NOT";      // <v><
          case 0x6D61613Cu: return "AAM";      // <aam
          case 0x73726556u: return "DYC";      // Version=2
          case 0x0A0D5D5Bu: return "DYC";      // []\r\nVersion=1
          case 0x005BFEFFu: return "INI";      // UTF-16LE [LEXICON]
          case 0x6D61672Fu: return "TXT";      // /manifest... beta manifest.txt
          case 0x68432021u: return "DAT";      // ! Character Specification (compact header)
          case 0x3D202F2Fu:
          case 0x636E6923u:
          case 0x6E666923u:
          case 0x66656423u:
          case 0x75727473u:
          case 0x2F0A0D20u:
          case 0x2F2F0A0Du: return "FX";
        }

        // RIFF can be either a bare Wwise WEM (WAVE) or a DirectMusic segment (DMSG).
        if (magic == 0x46464952u) {
          if (data.Length >= 12 && ReadU32(data, 8) == 0x47534D44u) return "SGT";
          return "WEM";
        }

        // Current binary DAT starts with a 0x18-sized ASCII format string. ACB also
        // commonly starts with 0x18 followed by zero and is handled as an audio bank.
        if (magic == 0x00000018u && data.Length >= 8) {
          UInt32 second = ReadU32(data, 4);
          if (second == 0x41455241u || second == 0x4D4F4F52u) return "DAT"; // AREA/ROOM
          if (second == 0) return "ACB";
        }

        // Text specifications begin with ! CRLF ! in the common format.
        if (magic == 0x210A0D21u && data.Length >= 8) {
          UInt32 second = ReadU32(data, 4);
          if (second == 0x72615020u) return "PRT"; // Particle
          if (second == 0x67614D20u) return "MAG"; // Mag
          if (second == 0x61684320u || second == 0x65724120u
              || second == 0x6F6F5220u || second == 0x73614D20u) return "DAT";
        }

        // UTF-16 Appearance / EnvironmentMaterial variants from beta/current assets.
        if (magic == 0x0041003Cu || magic == 0x003CFEFFu) {
          String utf16Head = DecodeHead(data);
          if (utf16Head.IndexOf("<Appearance", StringComparison.OrdinalIgnoreCase) >= 0) return "EPP";
          if (utf16Head.IndexOf("<EnvironmentMaterial", StringComparison.OrdinalIgnoreCase) >= 0) return "EMT";
          if (utf16Head.IndexOf("<nodeWClasses", StringComparison.OrdinalIgnoreCase) >= 0) return "FXSPEC";
          if (utf16Head.TrimStart().StartsWith("<", StringComparison.Ordinal)) return "XML";
        }

        // Beta JBA heap snapshots have no fixed magic but a distinctive 30fps header.
        if (data.Length >= 0x10) {
          Single duration = BitConverter.ToSingle(data, 4);
          Single fps = BitConverter.ToSingle(data, 8);
          UInt32 blocks = ReadU32(data, 0x0C);
          if (Single.IsFinite(duration) && duration >= 0 && duration <= 3600
              && Math.Abs(fps - 30.0F) < 0.001F && blocks >= 1 && blocks <= 3)
            return "JBA";
        }

        // ACB fallback used by Jedipedia: the cooked name marker cnv_ appears at 8.
        if (data.Length >= 12 && ReadU32(data, 4) == 0 && ReadU32(data, 8) == 0x5F766E63u)
          return "ACB";

        if ((magic & 0x00FFFFFFu) == 1u) return "STB";
        if (magic == 2u && data.Length >= 8 && ReadU32(data, 4) == 0u) return "MPH";
      }

      String rootTag = XmlRootTag(data);
      if (!String.IsNullOrEmpty(rootTag)) {
        if (String.Equals(rootTag, "aam", StringComparison.OrdinalIgnoreCase)) return "AAM";
        if (String.Equals(rootTag, "Material", StringComparison.OrdinalIgnoreCase)) return "MAT";
        if (String.Equals(rootTag, "TextureObject", StringComparison.OrdinalIgnoreCase)) return "TEX";
        if (String.Equals(rootTag, "EnvironmentMaterial", StringComparison.OrdinalIgnoreCase)) return "EMT";
        if (String.Equals(rootTag, "nodeWClasses", StringComparison.OrdinalIgnoreCase)) return "FXSPEC";
        if (String.Equals(rootTag, "Appearance", StringComparison.OrdinalIgnoreCase)) return "EPP";
        return "XML";
      }

      return declared;
    }

    private static Boolean IsUnknown(String extension) {
      return String.IsNullOrWhiteSpace(extension)
        || extension == "UNKNOWN" || extension == "UNK" || extension == "RIFF";
    }

    private static String Normalize(String extension) =>
      (extension ?? String.Empty).Trim().TrimStart('.').ToUpperInvariant();

    private static UInt32 ReadU32(Byte[] data, Int32 offset) {
      if (data == null || offset < 0 || offset + 4 > data.Length) return 0;
      return (UInt32)(data[offset]
        | (data[offset + 1] << 8)
        | (data[offset + 2] << 16)
        | (data[offset + 3] << 24));
    }

    private static String DecodeHead(Byte[] data) {
      if (data == null || data.Length == 0) return String.Empty;
      Int32 sample = Math.Min(data.Length, 4096);
      try {
        if (sample >= 2 && data[0] == 0xFF && data[1] == 0xFE)
          return Encoding.Unicode.GetString(data, 2, sample - 2).Replace("\0", String.Empty);
        if (sample >= 2 && data[0] == 0xFE && data[1] == 0xFF)
          return Encoding.BigEndianUnicode.GetString(data, 2, sample - 2).Replace("\0", String.Empty);

        Int32 oddZero = 0, evenZero = 0;
        for (Int32 i = 0; i < Math.Min(sample, 256); i++) {
          if (data[i] != 0) continue;
          if ((i & 1) == 0) evenZero++; else oddZero++;
        }
        if (oddZero >= 2 && oddZero > evenZero * 2)
          return Encoding.Unicode.GetString(data, 0, sample & ~1).Replace("\0", String.Empty);
        if (evenZero >= 2 && evenZero > oddZero * 2)
          return Encoding.BigEndianUnicode.GetString(data, 0, sample & ~1).Replace("\0", String.Empty);
        return Encoding.UTF8.GetString(data, 0, sample).Replace("\0", String.Empty);
      }
      catch { return String.Empty; }
    }

    /// <summary>
    /// Reads the first real XML element name while tolerating BOM, whitespace, XML prolog,
    /// comments and doctype. The direct byte scan mirrors Jedipedia's resolver so a large
    /// documentation comment before &lt;aam&gt; does not hide the subtype.
    /// </summary>
    private static String XmlRootTag(Byte[] data) {
      if (data == null || data.Length == 0) return String.Empty;

      // UTF-16 XML is easier and safer to classify after decoding a bounded prefix.
      String decoded = DecodeHead(data);
      if (decoded.IndexOf('<') < 0) return String.Empty;
      Int32 limit = Math.Min(decoded.Length, XmlScanLimit);
      Int32 i = 0;
      while (i < limit) {
        Char c = decoded[i];
        if (c == '\uFEFF' || c == '\0' || Char.IsWhiteSpace(c)) { i++; continue; }
        if (c != '<') return String.Empty;
        if (i + 1 >= limit) return String.Empty;

        if (decoded[i + 1] == '?') {
          Int32 end = decoded.IndexOf("?>", i + 2, StringComparison.Ordinal);
          if (end < 0 || end >= limit) return String.Empty;
          i = end + 2;
          continue;
        }
        if (decoded[i + 1] == '!') {
          if (i + 3 < limit && decoded.Substring(i, 4) == "<!--") {
            Int32 end = decoded.IndexOf("-->", i + 4, StringComparison.Ordinal);
            if (end < 0 || end >= limit) return String.Empty;
            i = end + 3;
            continue;
          }
          Int32 endDecl = decoded.IndexOf('>', i + 2);
          if (endDecl < 0 || endDecl >= limit) return String.Empty;
          i = endDecl + 1;
          continue;
        }

        Int32 start = i + 1;
        Int32 endName = start;
        while (endName < limit) {
          Char cc = decoded[endName];
          Boolean first = endName == start;
          Boolean alpha = Char.IsLetter(cc) || cc == '_';
          Boolean valid = alpha || (!first && (Char.IsDigit(cc) || cc == '.' || cc == '-' || cc == ':'));
          if (!valid) break;
          endName++;
        }
        return endName > start ? decoded.Substring(start, endName - start) : String.Empty;
      }
      return String.Empty;
    }
  }
}
