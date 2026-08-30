using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PugTools {
  internal static class ViewFaceFX {
    internal sealed class HeaderInfo {
      internal UInt32 SdkVersion;
      internal UInt32 FileFormatVersion;
      internal String LicenseeName = String.Empty;
      internal String LicenseeProjectName = String.Empty;
      internal UInt32 LicenseeVersion;
      internal UInt16 ArchiveDirectoryVersion;
      internal readonly Dictionary<String, UInt16> ClassVersions = new Dictionary<String, UInt16>(StringComparer.Ordinal);
      internal readonly List<String> Strings = new List<String>();
    }

    internal static HeaderInfo ReadHeader(BinaryReader br) {
      if (br == null) throw new ArgumentNullException(nameof(br));
      Stream stream = br.BaseStream;
      if (!stream.CanSeek) throw new InvalidDataException("FaceFX stream must be seekable.");
      stream.Position = 0;
      if (stream.Length < 12) throw new InvalidDataException("FaceFX file is too small.");

      UInt32 magic = br.ReadUInt32();
      if (magic != 0x45434146u) throw new InvalidDataException("Expected FACE magic, got 0x" + magic.ToString("X8") + ".");
      UInt32 sdk = br.ReadUInt32();
      if (sdk != 1720) System.Diagnostics.Debug.WriteLine("Unexpected FaceFX SDK version " + sdk + " (SWTOR normally uses 1720).");
      UInt32 fileVersion = sdk >= 1720 ? br.ReadUInt32() : 0;
      if (fileVersion != 0) System.Diagnostics.Debug.WriteLine("Unexpected FaceFX file-format version " + fileVersion + ".");

      HeaderInfo info = new HeaderInfo {
        SdkVersion = sdk,
        FileFormatVersion = fileVersion,
        LicenseeName = ReadString32(br),
        LicenseeProjectName = ReadString32(br),
        LicenseeVersion = br.ReadUInt32()
      };
      // Jedipedia treats these values as assertions, not fatal parser errors. A few beta/legacy archives carry
      // harmless variations here; keep parsing the structurally identical archive instead of falling back to HEX.
      if (!String.Equals(info.LicenseeName, "Bioware Edmonton", StringComparison.Ordinal))
        System.Diagnostics.Debug.WriteLine("Unexpected FaceFX licensee: " + info.LicenseeName);
      if (!String.Equals(info.LicenseeProjectName, "Bioware Edmonton Project", StringComparison.Ordinal))
        System.Diagnostics.Debug.WriteLine("Unexpected FaceFX project: " + info.LicenseeProjectName);
      if (info.LicenseeVersion != 1000)
        System.Diagnostics.Debug.WriteLine("Unexpected FaceFX licensee version: " + info.LicenseeVersion);

      if (sdk > 1600) {
        info.ArchiveDirectoryVersion = br.ReadUInt16();
        if (info.ArchiveDirectoryVersion >= 1) {
          UInt32 binsInUse = br.ReadUInt32();
          if (binsInUse > 100000) throw new InvalidDataException("FaceFX class-version hash has an unreasonable bin count.");
          for (UInt32 i = 0; i < binsInUse; i++) {
            br.ReadUInt32(); // serialized bin index
            UInt32 binLength = br.ReadUInt32();
            if (binLength > 100000) throw new InvalidDataException("FaceFX class-version bin is unreasonably large.");
            for (UInt32 j = 0; j < binLength; j++) {
              if (sdk < 1700) br.ReadUInt16();
              String name = ReadString32(br);
              UInt16 version = br.ReadUInt16();
              info.ClassVersions[name] = version;
            }
          }
        }

        UInt32 numNames = br.ReadUInt32();
        if (numNames > 1000000) throw new InvalidDataException("FaceFX string table is unreasonably large.");
        for (UInt32 i = 0; i < numNames; i++) {
          if (sdk < 1700) { br.ReadUInt16(); br.ReadUInt16(); }
          info.Strings.Add(ReadString32(br));
        }
      }

      ExpectZero(br.ReadUInt32(), "FaceFX archive trailing word 1");
      ExpectZero(br.ReadUInt32(), "FaceFX archive trailing word 2");
      return info;
    }

    internal static String StringAt(HeaderInfo header, UInt32 index, String context) {
      if (header == null || index >= header.Strings.Count)
        throw new InvalidDataException(context + " string index " + index + " is outside the FaceFX string table (" + (header?.Strings.Count ?? 0) + ").");
      return header.Strings[(Int32)index];
    }

    internal static String ReadString32(BinaryReader br) {
      UInt32 length = br.ReadUInt32();
      if (length > Int32.MaxValue || br.BaseStream.Position + length > br.BaseStream.Length)
        throw new InvalidDataException("FaceFX string points outside the stream.");
      Byte[] bytes = br.ReadBytes((Int32)length);
      // FaceFX serializes byte strings with a declared byte count. Shipped SWTOR files may include a terminating
      // NUL inside that count. Jedipedia's readString() stops at the first NUL while still advancing by the full
      // declared length; doing the same is important because retaining \0 makes perfectly valid licensee/name rows
      // look different and used to make the Asset Browser drop straight to the HEX viewer. The strings are effectively
      // 8-bit in these archives (class/name identifiers are ASCII), so decode byte-for-byte rather than treating a
      // high byte as the start of malformed UTF-8.
      Int32 count = Array.IndexOf(bytes, (Byte)0);
      if (count < 0) count = bytes.Length;
      var chars = new Char[count];
      for (Int32 i = 0; i < count; i++) chars[i] = (Char)bytes[i];
      return new String(chars);
    }

    internal static void ExpectZero(UInt32 value, String label) {
      // The browser reader mirrors Jedipedia's non-fatal assert semantics. These marker words are diagnostics, not
      // enough reason to throw away the rest of a parse and present only HEX.
      if (value != 0) System.Diagnostics.Debug.WriteLine(label + " expected 0, got " + value + ".");
    }
  }
}
