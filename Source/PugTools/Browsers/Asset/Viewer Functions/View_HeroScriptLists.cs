using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace PugTools {
  /// <summary>
  /// Reader for the HeroScript index lists stored as scriptdef.list (SDEF) and,
  /// in beta builds, scripts.list (SIDS). Both formats use HeroEngine's signed-
  /// magnitude varint, and both deliberately repeat every script entry twice.
  /// </summary>
  internal static class ViewHeroScriptLists {
    private const UInt32 SdefMagic = 0x46454453; // SDEF
    private const UInt32 SidsMagic = 0x53444953; // SIDS
    private const Int32 MaxScriptEntries = 4000000;

    internal sealed class HeroScriptListInfo {
      internal String Kind;
      internal UInt16 ContentVersion;
      internal UInt16 TransportVersion;
      internal readonly List<HeroScriptListEntry> Scripts = new List<HeroScriptListEntry>();
      internal Int32 RawEntryCount;
    }

    internal sealed class HeroScriptListEntry {
      internal UInt64 Id;
      internal String Name;
      internal UInt32 NameHash;
      internal Boolean HasNameHash;
    }

    internal static HeroScriptListInfo Parse(BinaryReader br) {
      if (br == null) throw new ArgumentNullException(nameof(br));
      if (!br.BaseStream.CanSeek) throw new InvalidDataException("HeroScript list reader requires a seekable stream.");
      br.BaseStream.Position = 0;
      if (br.BaseStream.Length < 9) throw new InvalidDataException("HeroScript list is shorter than its header.");

      UInt32 magic = br.ReadUInt32();
      br.BaseStream.Position = 0;
      if (magic == SdefMagic) return ParseSdef(br);
      if (magic == SidsMagic) return ParseSids(br);
      throw new InvalidDataException("Unknown HeroScript list magic 0x" + magic.ToString("X8", CultureInfo.InvariantCulture) + ".");
    }

    private static HeroScriptListInfo ParseSdef(BinaryReader br) {
      if (br.ReadUInt32() != SdefMagic) throw new InvalidDataException("Invalid SDEF magic.");
      UInt16 contentVersion = br.ReadUInt16();
      UInt16 transportVersion = br.ReadUInt16();
      if (!((contentVersion == 1 && transportVersion == 4) || (contentVersion == 2 && transportVersion == 5)))
        throw new InvalidDataException(
          "Unsupported SDEF version " + contentVersion.ToString(CultureInfo.InvariantCulture) + "." +
          transportVersion.ToString(CultureInfo.InvariantCulture) + ".");

      HeroScriptListInfo result = new HeroScriptListInfo {
        Kind = "SDEF",
        ContentVersion = contentVersion,
        TransportVersion = transportVersion
      };

      Int32 rawCount = ReadCount(br, "SDEF script");
      result.RawEntryCount = rawCount;
      if ((rawCount & 1) != 0) throw new InvalidDataException("SDEF script entry count is not even; entries are expected in repeated pairs.");

      HeroScriptListEntry previous = null;
      for (Int32 i = 0; i < rawCount; i++) {
        UInt64 id = ReadPositiveVarUInt64(br, "SDEF script ID");
        Int32 nameLength = br.ReadByte();
        EnsureRemaining(br, nameLength);
        String name = nameLength == 0 ? null : Encoding.UTF8.GetString(br.ReadBytes(nameLength));
        if (contentVersion == 1 && nameLength == 0)
          throw new InvalidDataException("SDEF 1.4 entry #" + i.ToString(CultureInfo.InvariantCulture) + " is missing its script name.");

        UInt32 hash = 0;
        Boolean hasHash = false;
        if (contentVersion == 2) {
          UInt64 hash64 = ReadPositiveVarUInt64(br, "SDEF script-name hash");
          if (hash64 > UInt32.MaxValue) throw new InvalidDataException("SDEF script-name hash exceeds 32 bits.");
          UInt32 storedHash = (UInt32)hash64;
          if (storedHash != 0 && !String.IsNullOrEmpty(name))
            throw new InvalidDataException("SDEF 2.5 entry unexpectedly stores both a non-zero name hash and plaintext name.");
          hash = storedHash == 0 && !String.IsNullOrEmpty(name) ? Fnv1a32(name) : storedHash;
          hasHash = true;
          Byte repetition = br.ReadByte();
          if (repetition != (Byte)(i & 1))
            throw new InvalidDataException("SDEF repetition marker does not match entry parity at #" + i.ToString(CultureInfo.InvariantCulture) + ".");
        } else if (!String.IsNullOrEmpty(name)) {
          hash = Fnv1a32(name);
          hasHash = true;
        }

        HeroScriptListEntry current = new HeroScriptListEntry {
          Id = id,
          Name = name,
          NameHash = hash,
          HasNameHash = hasHash
        };

        if ((i & 1) == 0) {
          result.Scripts.Add(current);
          previous = current;
        } else {
          if (previous == null || current.Id != previous.Id || (contentVersion == 2 && current.NameHash != previous.NameHash))
            throw new InvalidDataException("SDEF repeated entry #" + i.ToString(CultureInfo.InvariantCulture) + " does not match the preceding entry.");
        }
      }

      if (br.BaseStream.Position != br.BaseStream.Length)
        throw new InvalidDataException("SDEF list has " + (br.BaseStream.Length - br.BaseStream.Position).ToString(CultureInfo.InvariantCulture) + " unread byte(s).");
      return result;
    }

    private static HeroScriptListInfo ParseSids(BinaryReader br) {
      if (br.ReadUInt32() != SidsMagic) throw new InvalidDataException("Invalid SIDS magic.");
      UInt32 packedVersion = br.ReadUInt32();
      if (packedVersion != 0x00040001)
        throw new InvalidDataException(
          "Unsupported SIDS version " + (packedVersion & 0xFFFF).ToString(CultureInfo.InvariantCulture) + "." +
          (packedVersion >> 16).ToString(CultureInfo.InvariantCulture) + ".");

      HeroScriptListInfo result = new HeroScriptListInfo {
        Kind = "SIDS",
        ContentVersion = 1,
        TransportVersion = 4
      };

      Int32 rawCount = ReadCount(br, "SIDS script");
      result.RawEntryCount = rawCount;
      if ((rawCount & 1) != 0) throw new InvalidDataException("SIDS script entry count is not even; entries are expected in repeated pairs.");

      UInt64 previous = 0;
      for (Int32 i = 0; i < rawCount; i++) {
        UInt64 id = ReadPositiveVarUInt64(br, "SIDS script ID");
        if ((i & 1) == 0) {
          result.Scripts.Add(new HeroScriptListEntry { Id = id });
          previous = id;
        } else if (id != previous) {
          throw new InvalidDataException("SIDS repeated entry #" + i.ToString(CultureInfo.InvariantCulture) + " does not match the preceding entry.");
        }
      }

      EnsureRemaining(br, 1);
      Byte final = br.ReadByte();
      if (final != 0xD3) throw new InvalidDataException("SIDS final marker is 0x" + final.ToString("X2", CultureInfo.InvariantCulture) + " instead of D3.");
      if (br.BaseStream.Position != br.BaseStream.Length)
        throw new InvalidDataException("SIDS list has " + (br.BaseStream.Length - br.BaseStream.Position).ToString(CultureInfo.InvariantCulture) + " unread byte(s).");
      return result;
    }

    internal static ArrayList BuildTree(HeroScriptListInfo list) {
      ArrayList roots = new ArrayList();
      NodeListItem header = new NodeListItem("HeroScript list (" + list.Kind + ")", "");
      header.children.Add(new NodeListItem("Version", list.ContentVersion.ToString(CultureInfo.InvariantCulture) + "." + list.TransportVersion.ToString(CultureInfo.InvariantCulture)));
      header.children.Add(new NodeListItem("Raw entries", list.RawEntryCount));
      header.children.Add(new NodeListItem("Unique scripts", list.Scripts.Count));
      roots.Add(header);

      NodeListItem scripts = new NodeListItem("Scripts", list.Scripts.Count);
      for (Int32 i = 0; i < list.Scripts.Count; i++) {
        HeroScriptListEntry script = list.Scripts[i];
        String title = !String.IsNullOrWhiteSpace(script.Name)
          ? script.Name
          : script.Id.ToString(CultureInfo.InvariantCulture);
        NodeListItem node = new NodeListItem("#" + i.ToString(CultureInfo.InvariantCulture) + "  " + title, "");
        node.children.Add(new NodeListItem("ID", script.Id.ToString(CultureInfo.InvariantCulture) + "  (0x" + script.Id.ToString("X16", CultureInfo.InvariantCulture) + ")"));
        node.children.Add(new NodeListItem("Compiled native", "/resources/systemgenerated/compilednative/" + script.Id.ToString(CultureInfo.InvariantCulture)));
        if (!String.IsNullOrWhiteSpace(script.Name)) node.children.Add(new NodeListItem("Script name", script.Name));
        if (script.HasNameHash) node.children.Add(new NodeListItem("Name hash (FNV-1a 32)", script.NameHash.ToString(CultureInfo.InvariantCulture) + "  (0x" + script.NameHash.ToString("X8", CultureInfo.InvariantCulture) + ")"));
        scripts.children.Add(node);
      }
      roots.Add(scripts);
      return roots;
    }

    private static Int32 ReadCount(BinaryReader br, String what) {
      UInt64 value = ReadPositiveVarUInt64(br, what + " count");
      if (value > MaxScriptEntries) throw new InvalidDataException("Invalid " + what + " count: " + value.ToString(CultureInfo.InvariantCulture) + ".");
      return (Int32)value;
    }

    /// <summary>
    /// Unsigned subset of HeroEngine's varint. Script IDs can exceed Int64.MaxValue, so
    /// preserving the full positive 64-bit magnitude is required for SWTOR's D0... IDs.
    /// </summary>
    private static UInt64 ReadPositiveVarUInt64(BinaryReader br, String what) {
      EnsureRemaining(br, 1);
      Byte first = br.ReadByte();
      if (first < 0xC0) return first;
      if (first < 0xC8 || first > 0xCF)
        throw new InvalidDataException(what + " uses a negative/invalid HeroEngine varint lead byte 0x" + first.ToString("X2", CultureInfo.InvariantCulture) + ".");
      Int32 numBytes = (first & 0x07) + 1;
      EnsureRemaining(br, numBytes);
      UInt64 magnitude = 0;
      for (Int32 i = 0; i < numBytes; i++) magnitude = (magnitude << 8) | br.ReadByte();
      return magnitude;
    }

    private static UInt32 Fnv1a32(String text) {
      UInt32 hash = 2166136261;
      Byte[] data = Encoding.UTF8.GetBytes(text ?? String.Empty);
      foreach (Byte b in data) {
        hash ^= b;
        hash *= 16777619;
      }
      return hash;
    }

    private static void EnsureRemaining(BinaryReader br, Int64 count) {
      if (count < 0 || br.BaseStream.Length - br.BaseStream.Position < count)
        throw new EndOfStreamException("Unexpected end of HeroScript list.");
    }
  }
}
