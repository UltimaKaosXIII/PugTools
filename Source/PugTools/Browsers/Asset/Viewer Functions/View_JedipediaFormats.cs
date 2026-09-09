using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace PugTools {
  /// <summary>
  /// Lightweight desktop ports of Jedipedia File Reader formats that PugTools previously
  /// sent directly to the hex view. Very large index files deliberately expose counts and
  /// bounded previews: TreeListView is not virtualized like Jedipedia's browser table and
  /// creating several hundred thousand WinForms rows would make the Asset Browser unusable.
  /// </summary>
  internal static class View_JedipediaFormats {
    private const Int32 LargePreviewLimit = 500;
    private const Int32 GomPreviewPerType = 200;
    private const Int32 MaxFbxNodes = 100000;

    internal static ArrayList Parse(Stream source, String extension, String fileName) {
      if (source == null) throw new ArgumentNullException(nameof(source));
      source.Position = 0;
      using BinaryReader br = new BinaryReader(source, Encoding.UTF8, true);
      String ext = (extension ?? String.Empty).Trim().ToUpperInvariant();
      UInt32 magic = source.Length >= 4 ? br.ReadUInt32() : 0;
      source.Position = 0;

      return ext switch {
        "BIN" => ParseBin(br, fileName),
        "BKT" => ParseBkt(br),
        "FBX" => ParseFbx(br),
        "GOM" => ParseGom(br),
        "NODE" => ParseNode(br),
        "INFO" => magic switch {
          0x4B434250u => ParseBucketInfo(br), // PBCK
          0x464E4950u => ParsePrototypeInfo(br), // PINF
          _ => throw new InvalidDataException("Unsupported .info magic 0x" + magic.ToString("X8", CultureInfo.InvariantCulture))
        },
        "LIST" => magic switch {
          0x46454453u => ParseScriptDefList(br), // SDEF
          0x53444953u => ParseScriptIdsList(br), // SIDS
          _ => throw new InvalidDataException("Unsupported .list magic 0x" + magic.ToString("X8", CultureInfo.InvariantCulture))
        },
        _ => throw new NotSupportedException("No structured parser for ." + ext)
      };
    }

    private static ArrayList Roots(params NodeListItem[] roots) {
      ArrayList result = new ArrayList();
      foreach (NodeListItem root in roots) if (root != null) result.Add(root);
      return result;
    }

    private static NodeListItem Leaf(String name, Object value) => new NodeListItem(name, value ?? String.Empty);

    private static NodeListItem Branch(String name, String value = "") => new NodeListItem(name, value);

    private static void Add(NodeListItem parent, String name, Object value) => parent.children.Add(Leaf(name, value));

    private static String ReadCString(BinaryReader br, Int64 absoluteOffset, Int64 maxExclusive) {
      if (absoluteOffset < 0 || absoluteOffset >= maxExclusive || absoluteOffset >= br.BaseStream.Length) return String.Empty;
      Int64 old = br.BaseStream.Position;
      try {
        br.BaseStream.Position = absoluteOffset;
        List<Byte> bytes = new List<Byte>();
        while (br.BaseStream.Position < maxExclusive && br.BaseStream.Position < br.BaseStream.Length) {
          Byte b = br.ReadByte();
          if (b == 0) break;
          bytes.Add(b);
          if (bytes.Count > 1024 * 1024) throw new InvalidDataException("Unreasonably long string in binary asset.");
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
      } finally { br.BaseStream.Position = old; }
    }

    private static String ReadFixedString(BinaryReader br, Int32 byteCount, Boolean trimNul = true) {
      if (byteCount < 0 || br.BaseStream.Position + byteCount > br.BaseStream.Length) throw new EndOfStreamException();
      String s = Encoding.UTF8.GetString(br.ReadBytes(byteCount));
      return trimNul ? s.TrimEnd('\0') : s;
    }

    private static Int64 ReadHeroVarInt(BinaryReader br) {
      Byte lead = br.ReadByte();
      if (lead < 0xC0) return lead;
      if (lead == 0xD0) return Int64.MinValue;
      if (lead > 0xCF) return lead; // keep the reader aligned even for invalid legacy bytes
      Int32 bytes = (lead & 0x07) + 1;
      UInt64 magnitude = 0;
      for (Int32 i = 0; i < bytes; i++) magnitude = (magnitude << 8) | br.ReadByte();
      if (lead < 0xC8) {
        if (magnitude == 0x8000000000000000UL) return Int64.MinValue;
        return -checked((Int64)magnitude);
      }
      return unchecked((Int64)magnitude);
    }

    private static UInt64 ReadHeroVarUInt(BinaryReader br) {
      Int64 value = ReadHeroVarInt(br);
      return unchecked((UInt64)value);
    }

    // ---------------- BIN ----------------
    private static ArrayList ParseBin(BinaryReader br, String fileName) {
      String name = (fileName ?? String.Empty).Trim().ToLowerInvariant();
      if (name == "localcacheversioninfo.bin") {
        if (br.BaseStream.Length != 20) throw new InvalidDataException("Local cache version file must be 20 bytes.");
        UInt64 dbVersion = br.ReadUInt64();
        UInt32 apesVersion = br.ReadUInt32();
        UInt32 zero1 = br.ReadUInt32();
        UInt32 zero2 = br.ReadUInt32();
        NodeListItem summary = Branch("BIN / Locale cache version");
        String db = dbVersion.ToString(CultureInfo.InvariantCulture);
        if (db.Length == 14) db = db.Substring(0, 4) + "-" + db.Substring(4, 2) + "-" + db.Substring(6, 2) + " " + db.Substring(8, 2) + ":" + db.Substring(10, 2) + ":" + db.Substring(12, 2);
        Add(summary, "dbVersion", db);
        Add(summary, "apesVersion", apesVersion);
        if (zero1 != 0 || zero2 != 0) Add(summary, "Header warning", "Expected two trailing zero fields.");
        return Roots(summary);
      }

      if (name == "groupmanifest.bin") {
        UInt32 magic = br.ReadUInt32();
        if (magic != 1) throw new InvalidDataException("Expected group manifest magic 1.");
        List<String> assets = new List<String>();
        while (br.BaseStream.Position < br.BaseStream.Length) {
          List<Byte> bytes = new List<Byte>();
          while (br.BaseStream.Position < br.BaseStream.Length) {
            Byte b = br.ReadByte();
            if (b == 0) break;
            bytes.Add(b);
          }
          if (bytes.Count > 0) assets.Add(Encoding.UTF8.GetString(bytes.ToArray()));
        }
        NodeListItem summary = Branch("BIN / Archive group manifest");
        Add(summary, "Archives", assets.Count);
        if (assets.Count > 0) Add(summary, "Group", assets[0].Split('_')[0]);
        NodeListItem list = Branch("Archives (" + assets.Count + ")");
        for (Int32 i = 0; i < assets.Count; i++) list.children.Add(Leaf(i.ToString(CultureInfo.InvariantCulture), assets[i]));
        return Roots(summary, list);
      }

      if (name == "metadata.bin") {
        if (br.BaseStream.Length == 0 || br.BaseStream.Length % 32 != 0) throw new InvalidDataException("Archive metadata length must be a non-zero multiple of 32 bytes.");
        Int32 count = checked((Int32)(br.BaseStream.Length / 32));
        NodeListItem summary = Branch("BIN / Archive metadata");
        Add(summary, "Files", count.ToString("N0", CultureInfo.InvariantCulture));
        NodeListItem list = Branch("Hash entries (preview)");
        for (Int32 i = 0; i < count; i++) {
          UInt32 noPrefixSh = br.ReadUInt32();
          UInt32 noPrefixPh = br.ReadUInt32();
          UInt32 c1 = br.ReadUInt32();
          UInt32 c0a = br.ReadUInt32();
          UInt32 withPrefixSh = br.ReadUInt32();
          UInt32 withPrefixPh = br.ReadUInt32();
          UInt32 c0b = br.ReadUInt32();
          UInt32 c0c = br.ReadUInt32();
          if (i < LargePreviewLimit) {
            NodeListItem row = Branch(i.ToString(CultureInfo.InvariantCulture));
            Add(row, "Hash without /resources", noPrefixPh.ToString("X8") + noPrefixSh.ToString("X8"));
            Add(row, "Hash with /resources", withPrefixPh.ToString("X8") + withPrefixSh.ToString("X8"));
            if ((c1 != 0 && c1 != 1) || c0a != 0 || (c0b != 0 && c0b != 1) || c0c != 0) Add(row, "Warning", "Unexpected metadata constants");
            list.children.Add(row);
          }
        }
        if (count > LargePreviewLimit) list.children.Add(Leaf("…", (count - LargePreviewLimit).ToString("N0", CultureInfo.InvariantCulture) + " more metadata rows omitted"));
        return Roots(summary, list);
      }

      throw new InvalidDataException("Unknown .bin variant: " + (fileName ?? "(unnamed)"));
    }

    // ---------------- BKT ----------------
    private sealed class BktEntry {
      public UInt64 Id;
      public UInt64 BaseClass;
      public String Name;
      public UInt16 Bitset;
      public UInt16 Glommed;
      public UInt32 DataLength;
      public Byte StreamStyle;
      public String Compression;
    }

    private static ArrayList ParseBkt(BinaryReader br) {
      if (br.ReadUInt32() != 0x4B554250u) throw new InvalidDataException("Expected PBUK header.");
      UInt16 major = br.ReadUInt16();
      UInt16 minor = br.ReadUInt16();
      if (major != 2 || (minor != 4 && minor != 5)) throw new InvalidDataException($"Unsupported PBUK version {major}.{minor}.");

      List<BktEntry> entries = new List<BktEntry>();
      Int32 sectionCount = 0;
      while (br.BaseStream.Position + 4 <= br.BaseStream.Length - 4) {
        UInt32 sectionLength = br.ReadUInt32();
        if (sectionLength == 0) break;
        Int64 sectionStart = br.BaseStream.Position;
        Int64 sectionEnd = checked(sectionStart + sectionLength);
        if (sectionEnd > br.BaseStream.Length) throw new InvalidDataException("BKT DBLB section exceeds file length.");
        if (br.ReadUInt32() != 0x424C4244u) throw new InvalidDataException("Expected DBLB section in BKT.");
        UInt32 dblbVersion = br.ReadUInt32();
        if (dblbVersion != 1 && dblbVersion != 2) throw new InvalidDataException("Unsupported BKT DBLB version " + dblbVersion + ".");
        sectionCount++;

        while (br.BaseStream.Position + 4 <= sectionEnd) {
          Int64 entryStart = br.BaseStream.Position;
          UInt32 entryLength = br.ReadUInt32();
          if (entryLength == 0) break;
          Int64 entryEnd = entryStart + entryLength;
          if (entryEnd > sectionEnd) throw new InvalidDataException("BKT node entry exceeds DBLB section.");

          UInt16 bitset;
          UInt16 dataOffset;
          UInt64 id;
          if (dblbVersion == 1) {
            bitset = br.ReadUInt16(); dataOffset = br.ReadUInt16(); id = br.ReadUInt64();
          } else {
            br.ReadUInt32(); id = br.ReadUInt64(); bitset = br.ReadUInt16(); dataOffset = br.ReadUInt16();
          }
          UInt16 nameOffset = br.ReadUInt16();
          br.ReadUInt16(); // description offset
          if (dblbVersion == 1) br.ReadUInt32();
          UInt64 baseClass = br.ReadUInt64();
          if (dblbVersion == 2) br.ReadUInt32();
          UInt16 glommed = br.ReadUInt16();
          br.ReadUInt16(); // glommed offset
          br.ReadUInt32(); // uncompressed node content length
          br.ReadUInt16(); // uncompressed data offset
          br.ReadUInt16(); // PBUK minor copy
          Byte streamStyle = br.ReadByte();
          br.ReadByte(); // node type

          String name = ReadCString(br, entryStart + nameOffset, entryEnd);
          UInt32 dataLength = dataOffset <= entryLength ? entryLength - dataOffset : 0;
          String compression = "None";
          if (bitset == 15 && dataOffset + 2 <= entryLength) {
            Int64 old = br.BaseStream.Position;
            br.BaseStream.Position = entryStart + dataOffset;
            UInt16 cm = br.ReadUInt16();
            br.BaseStream.Position = old;
            compression = cm == 0x9C78 ? "DEFLATE" : cm == 0xB528 ? "zstd" : "Unknown (0x" + cm.ToString("X4") + ")";
          }
          entries.Add(new BktEntry { Id = id, BaseClass = baseClass, Name = name, Bitset = bitset, Glommed = glommed, DataLength = dataLength, StreamStyle = streamStyle, Compression = compression });

          Int64 relative = entryStart - sectionStart;
          br.BaseStream.Position = sectionStart + ((relative + entryLength + 7L) & ~7L);
          if (br.BaseStream.Position > sectionEnd) br.BaseStream.Position = sectionEnd;
        }
        br.BaseStream.Position = sectionEnd;
      }

      NodeListItem summary = Branch("BKT / Prototype bucket");
      Add(summary, "Version", major + "." + minor);
      Add(summary, "DBLB sections", sectionCount);
      Add(summary, "Prototype nodes", entries.Count);

      NodeListItem nodes = Branch("Nodes (" + entries.Count.ToString("N0", CultureInfo.InvariantCulture) + ")");
      foreach (BktEntry entry in entries) {
        NodeListItem node = Branch(String.IsNullOrWhiteSpace(entry.Name) ? entry.Id.ToString(CultureInfo.InvariantCulture) : entry.Name);
        Add(node, "ID", entry.Id);
        Add(node, "Base class", entry.BaseClass);
        Add(node, "Reference type", entry.Bitset == 14 ? "ScriptRef" : entry.Bitset == 15 ? "NodeRef" : entry.Bitset.ToString(CultureInfo.InvariantCulture));
        Add(node, "Compressed bytes", entry.DataLength.ToString("N0", CultureInfo.InvariantCulture));
        Add(node, "Compression", entry.Compression);
        Add(node, "Glommed classes", entry.Glommed);
        Add(node, "Stream style", entry.StreamStyle);
        nodes.children.Add(node);
      }
      return Roots(summary, nodes);
    }

    // ---------------- GOM ----------------
    private sealed class GomEntrySummary {
      public String Type;
      public UInt64 Id;
      public String Name;
      public Boolean Compressed;
      public Boolean ServerDom;
      public UInt32 NameHash;
      public UInt32 Length;
    }

    private static ArrayList ParseGom(BinaryReader br) {
      if (br.ReadUInt32() != 0x424C4244u) throw new InvalidDataException("Expected DBLB header in .gom file.");
      UInt32 version = br.ReadUInt32();
      if (version != 1 && version != 2) throw new InvalidDataException("Unsupported GOM DBLB version " + version + ".");

      Dictionary<String, Int32> counts = new Dictionary<String, Int32>(StringComparer.Ordinal);
      Dictionary<String, List<GomEntrySummary>> previews = new Dictionary<String, List<GomEntrySummary>>(StringComparer.Ordinal);
      Int32 total = 0;
      while (br.BaseStream.Position + 4 <= br.BaseStream.Length) {
        Int64 start = br.BaseStream.Position;
        UInt32 length = br.ReadUInt32();
        if (length == 0) break;
        Int64 end = start + length;
        if (end > br.BaseStream.Length || length < 16) throw new InvalidDataException("Invalid GOM DBLB entry length.");

        UInt32 nameHash = 0;
        UInt64 id;
        UInt16 flags;
        UInt16 nameOffset;
        if (version == 1) {
          flags = br.ReadUInt16();
          br.ReadUInt16(); // compressed data offset
          id = br.ReadUInt64();
          nameOffset = br.ReadUInt16();
          br.ReadUInt16(); // description
        } else {
          nameHash = br.ReadUInt32();
          id = br.ReadUInt64();
          flags = br.ReadUInt16();
          br.ReadUInt16(); // compressed data offset
          nameOffset = br.ReadUInt16();
          br.ReadUInt16(); // description
        }
        Int32 typeId = (flags >> 3) & 0xF;
        String type = typeId switch { 1 => "Node", 2 => "Enumeration", 3 => "Field", 4 => "Class", 5 => "Association", 7 => "Script", _ => "Type " + typeId };
        String name = ReadCString(br, start + nameOffset, end);
        Boolean compressed = (flags & 1) != 0;
        Boolean serverDom = (flags & 4) != 0;
        total++;
        counts[type] = counts.TryGetValue(type, out Int32 c) ? c + 1 : 1;
        if (!previews.TryGetValue(type, out List<GomEntrySummary> list)) previews[type] = list = new List<GomEntrySummary>();
        if (list.Count < GomPreviewPerType) list.Add(new GomEntrySummary { Type = type, Id = id, Name = name, Compressed = compressed, ServerDom = serverDom, NameHash = nameHash, Length = length });
        br.BaseStream.Position = end;
        // DBLB entries are normally 8-byte padded and entryLength includes the padding. No extra alignment here.
      }

      NodeListItem summary = Branch("GOM / client DOM");
      Add(summary, "DBLB version", version);
      Add(summary, "Definitions", total.ToString("N0", CultureInfo.InvariantCulture));
      foreach (KeyValuePair<String, Int32> pair in counts.OrderBy(p => p.Key)) Add(summary, pair.Key + "s", pair.Value.ToString("N0", CultureInfo.InvariantCulture));

      NodeListItem definitions = Branch("Definitions (bounded preview)");
      foreach (String type in counts.Keys.OrderBy(x => x)) {
        List<GomEntrySummary> list = previews[type];
        NodeListItem category = Branch(type + " (" + counts[type].ToString("N0", CultureInfo.InvariantCulture) + ")");
        foreach (GomEntrySummary entry in list) {
          String label = !String.IsNullOrWhiteSpace(entry.Name) ? entry.Name : entry.Id.ToString(CultureInfo.InvariantCulture);
          NodeListItem row = Branch(label);
          Add(row, "ID", entry.Id);
          if (entry.NameHash != 0) Add(row, "Name hash", "0x" + entry.NameHash.ToString("X8"));
          Add(row, "Size", entry.Length.ToString("N0", CultureInfo.InvariantCulture) + " bytes");
          Add(row, "Compressed", entry.Compressed);
          Add(row, "Server DOM", entry.ServerDom);
          category.children.Add(row);
        }
        if (counts[type] > list.Count) category.children.Add(Leaf("…", (counts[type] - list.Count).ToString("N0", CultureInfo.InvariantCulture) + " more entries omitted to keep the desktop tree responsive"));
        definitions.children.Add(category);
      }
      return Roots(summary, definitions);
    }

    // ---------------- NODE ----------------
    private static ArrayList ParseNode(BinaryReader br) {
      if (br.ReadUInt32() != 0x544F5250u) throw new InvalidDataException("Expected PROT header in .node file.");
      UInt16 major = br.ReadUInt16();
      UInt16 minor = br.ReadUInt16();
      if (major != 2 || (minor != 4 && minor != 5)) throw new InvalidDataException($"Unsupported NODE version {major}.{minor}.");
      UInt64 id = br.ReadUInt64();
      UInt32 fqnLength = br.ReadUInt32();
      String fqn = ReadFixedString(br, checked((Int32)fqnLength));
      UInt32 descLength = br.ReadUInt32();
      String description = ReadFixedString(br, checked((Int32)descLength));
      UInt32 constant3 = br.ReadUInt32();
      UInt32 constant1 = br.ReadUInt32();
      UInt64 baseClass = br.ReadUInt64();
      UInt32 legacyPrimitiveCount = 0;
      if (minor == 4) legacyPrimitiveCount = br.ReadUInt32();
      UInt32 glommedCount = br.ReadUInt32();
      List<UInt64> glommed = new List<UInt64>();
      for (UInt32 i = 0; i < glommedCount; i++) glommed.Add(br.ReadUInt64());
      Byte nodeKind = br.ReadByte();
      UInt16 repeatedVersion = br.ReadUInt16();
      Byte streamStyle = repeatedVersion >= 3 ? br.ReadByte() : (Byte)0;
      UInt32 contentLength = br.ReadUInt32();
      Int64 contentStart = br.BaseStream.Position;
      Int64 totalRootFields = ReadHeroVarInt(br);
      Int64 storedRootFields = ReadHeroVarInt(br);

      NodeListItem summary = Branch("NODE / External prototype");
      Add(summary, "Version", major + "." + minor);
      Add(summary, "ID", id);
      Add(summary, "FQN", fqn);
      Add(summary, "Description", description);
      Add(summary, "Base class", baseClass);
      Add(summary, "Glommed classes", glommedCount);
      Add(summary, "Root fields", totalRootFields);
      Add(summary, "Stored root fields", storedRootFields);
      Add(summary, "Content length", contentLength.ToString("N0", CultureInfo.InvariantCulture) + " bytes");
      Add(summary, "Stream style", streamStyle);
      Add(summary, "Node kind", nodeKind);
      if (minor == 4) Add(summary, "Legacy primitive count", legacyPrimitiveCount);
      if (constant3 != 3 || constant1 != 1 || repeatedVersion != minor) Add(summary, "Header warning", "Unexpected constant/version copy; inspect Hex view for details.");
      if (contentStart + contentLength != br.BaseStream.Length) Add(summary, "Length warning", "Declared content does not end exactly at EOF.");

      if (glommed.Count > 0) {
        NodeListItem g = Branch("Glommed classes (" + glommed.Count + ")");
        foreach (UInt64 x in glommed) g.children.Add(Leaf(x.ToString(CultureInfo.InvariantCulture), x));
        return Roots(summary, g);
      }
      return Roots(summary);
    }

    // ---------------- INFO ----------------
    private static ArrayList ParseBucketInfo(BinaryReader br) {
      if (br.ReadUInt32() != 0x4B434250u) throw new InvalidDataException("Expected PBCK header.");
      UInt16 major = br.ReadUInt16(); UInt16 minor = br.ReadUInt16();
      UInt64 count64 = ReadHeroVarUInt(br);
      if (count64 > Int32.MaxValue) throw new InvalidDataException("Too many bucket files.");
      Int32 count = (Int32)count64;
      NodeListItem summary = Branch("INFO / Prototype buckets");
      Add(summary, "Version", major + "." + minor);
      Add(summary, "Bucket files", count);
      NodeListItem list = Branch("Buckets (" + count + ")");
      for (Int32 i = 0; i < count; i++) {
        Int32 length = br.ReadByte();
        String name = ReadFixedString(br, length, false);
        if (i < LargePreviewLimit) list.children.Add(Leaf(i.ToString(CultureInfo.InvariantCulture), name));
      }
      if (br.BaseStream.Position < br.BaseStream.Length) {
        Byte marker = br.ReadByte();
        Add(summary, "End marker", "0x" + marker.ToString("X2"));
      }
      if (count > LargePreviewLimit) list.children.Add(Leaf("…", (count - LargePreviewLimit).ToString("N0", CultureInfo.InvariantCulture) + " more bucket names omitted"));
      return Roots(summary, list);
    }

    private static ArrayList ParsePrototypeInfo(BinaryReader br) {
      if (br.ReadUInt32() != 0x464E4950u) throw new InvalidDataException("Expected PINF header.");
      UInt16 major = br.ReadUInt16(); UInt16 minor = br.ReadUInt16();
      UInt64 count64 = ReadHeroVarUInt(br);
      if (count64 > Int32.MaxValue) throw new InvalidDataException("Too many prototypes.");
      Int32 count = (Int32)count64;
      Int32[] types = new Int32[4];
      List<(UInt64 Id, Byte Type)> preview = new List<(UInt64, Byte)>();
      for (Int32 i = 0; i < count; i++) {
        UInt64 id = ReadHeroVarUInt(br);
        Byte type = br.ReadByte();
        if (type < types.Length) types[type]++;
        if (preview.Count < LargePreviewLimit) preview.Add((id, type));
      }
      Byte end = br.BaseStream.Position < br.BaseStream.Length ? br.ReadByte() : (Byte)0;
      NodeListItem summary = Branch("INFO / Prototype index");
      Add(summary, "Version", major + "." + minor);
      Add(summary, "Nodes", count.ToString("N0", CultureInfo.InvariantCulture));
      Add(summary, "External .node", types[1].ToString("N0", CultureInfo.InvariantCulture));
      Add(summary, "Static bucket", types[2].ToString("N0", CultureInfo.InvariantCulture));
      Add(summary, "Dynamic bucket", types[3].ToString("N0", CultureInfo.InvariantCulture));
      Add(summary, "End marker", "0x" + end.ToString("X2"));
      NodeListItem nodes = Branch("Nodes (preview)");
      foreach ((UInt64 Id, Byte Type) row in preview) nodes.children.Add(Leaf(row.Id.ToString(CultureInfo.InvariantCulture), row.Type switch { 1 => "External .node", 2 => "Static bucket", 3 => "Dynamic bucket", _ => "Type " + row.Type }));
      if (count > preview.Count) nodes.children.Add(Leaf("…", (count - preview.Count).ToString("N0", CultureInfo.InvariantCulture) + " more nodes omitted"));
      return Roots(summary, nodes);
    }

    // ---------------- LIST ----------------
    private sealed class ScriptListEntry { public UInt64 Id; public String Name; public UInt64 Hash; }

    private static ArrayList ParseScriptDefList(BinaryReader br) {
      if (br.ReadUInt32() != 0x46454453u) throw new InvalidDataException("Expected SDEF header.");
      UInt16 contentVersion = br.ReadUInt16(); UInt16 transportVersion = br.ReadUInt16();
      if (!((contentVersion == 1 && transportVersion == 4) || (contentVersion == 2 && transportVersion == 5))) throw new InvalidDataException($"Unsupported SDEF version {contentVersion}.{transportVersion}.");
      UInt64 rawCount = ReadHeroVarUInt(br);
      if (rawCount > Int32.MaxValue) throw new InvalidDataException("Too many script entries.");
      Int32 raw = (Int32)rawCount;
      List<ScriptListEntry> scripts = new List<ScriptListEntry>();
      for (Int32 i = 0; i < raw; i++) {
        UInt64 id = ReadHeroVarUInt(br);
        Int32 nameLength = br.ReadByte();
        String name = nameLength > 0 ? ReadFixedString(br, nameLength, false).TrimEnd('\0') : String.Empty;
        UInt64 hash = 0;
        if (contentVersion == 2) {
          hash = ReadHeroVarUInt(br);
          br.ReadByte(); // duplicate marker 0/1
        }
        if ((i & 1) == 0) scripts.Add(new ScriptListEntry { Id = id, Name = name, Hash = hash });
      }
      NodeListItem summary = Branch("LIST / HeroScript definitions");
      Add(summary, "Version", contentVersion + "." + transportVersion);
      Add(summary, "Scripts", scripts.Count.ToString("N0", CultureInfo.InvariantCulture));
      NodeListItem list = Branch("Scripts (preview)");
      foreach (ScriptListEntry script in scripts.Take(LargePreviewLimit)) {
        NodeListItem row = Branch(String.IsNullOrWhiteSpace(script.Name) ? script.Id.ToString(CultureInfo.InvariantCulture) : script.Name);
        Add(row, "ID", script.Id);
        if (script.Hash != 0) Add(row, "Name hash", "0x" + script.Hash.ToString("X8"));
        list.children.Add(row);
      }
      if (scripts.Count > LargePreviewLimit) list.children.Add(Leaf("…", (scripts.Count - LargePreviewLimit).ToString("N0", CultureInfo.InvariantCulture) + " more scripts omitted"));
      return Roots(summary, list);
    }

    private static ArrayList ParseScriptIdsList(BinaryReader br) {
      if (br.ReadUInt32() != 0x53444953u) throw new InvalidDataException("Expected SIDS header.");
      UInt32 version = br.ReadUInt32();
      UInt64 rawCount = ReadHeroVarUInt(br);
      if (rawCount > Int32.MaxValue) throw new InvalidDataException("Too many script IDs.");
      Int32 raw = (Int32)rawCount;
      List<UInt64> scripts = new List<UInt64>();
      UInt64 previous = 0;
      for (Int32 i = 0; i < raw; i++) {
        UInt64 id = ReadHeroVarUInt(br);
        if ((i & 1) == 0) { scripts.Add(id); previous = id; }
        else if (id != previous) throw new InvalidDataException("SIDS duplicate script ID does not match its first copy.");
      }
      Byte marker = br.BaseStream.Position < br.BaseStream.Length ? br.ReadByte() : (Byte)0;
      NodeListItem summary = Branch("LIST / HeroScript IDs");
      Add(summary, "Version", (version & 0xFFFF) + "." + (version >> 16));
      Add(summary, "Scripts", scripts.Count.ToString("N0", CultureInfo.InvariantCulture));
      Add(summary, "End marker", "0x" + marker.ToString("X2"));
      NodeListItem list = Branch("Script IDs (preview)");
      foreach (UInt64 id in scripts.Take(LargePreviewLimit)) list.children.Add(Leaf(id.ToString(CultureInfo.InvariantCulture), "/resources/systemgenerated/compilednative/" + id));
      if (scripts.Count > LargePreviewLimit) list.children.Add(Leaf("…", (scripts.Count - LargePreviewLimit).ToString("N0", CultureInfo.InvariantCulture) + " more scripts omitted"));
      return Roots(summary, list);
    }

    // ---------------- FBX ----------------
    private sealed class FbxNode {
      public String Name;
      public readonly List<String> Properties = new List<String>();
      public readonly List<FbxNode> Children = new List<FbxNode>();
    }

    private static ArrayList ParseFbx(BinaryReader br) {
      Byte[] header = br.ReadBytes(23);
      if (header.Length != 23 || Encoding.ASCII.GetString(header, 0, 20) != "Kaydara FBX Binary  " || header[20] != 0 || header[21] != 0x1A || header[22] != 0) throw new InvalidDataException("Expected binary FBX header.");
      UInt32 version = br.ReadUInt32();
      Boolean wide = version >= 7500;
      Int32 nullRecordLength = wide ? 25 : 13;
      Int32 nodeCount = 0;

      FbxNode ReadNode() {
        UInt64 endOffset = ReadFbxWord(br, wide);
        UInt64 propertyCount = ReadFbxWord(br, wide);
        UInt64 propertyBytes = ReadFbxWord(br, wide);
        Byte nameLength = br.ReadByte();
        if (endOffset == 0) return null;
        if (endOffset > (UInt64)br.BaseStream.Length) throw new InvalidDataException("FBX node end offset exceeds file length.");
        if (++nodeCount > MaxFbxNodes) throw new InvalidDataException("FBX contains too many nodes for the desktop preview.");
        String name = ReadFixedString(br, nameLength, false);
        FbxNode node = new FbxNode { Name = name };
        Int64 propStart = br.BaseStream.Position;
        for (UInt64 i = 0; i < propertyCount; i++) node.Properties.Add(ReadFbxPropertySummary(br));
        if ((UInt64)(br.BaseStream.Position - propStart) != propertyBytes) throw new InvalidDataException("FBX property byte count mismatch in node " + name + ".");
        while (br.BaseStream.Position < (Int64)endOffset - nullRecordLength) {
          FbxNode child = ReadNode();
          if (child == null) break;
          node.Children.Add(child);
        }
        br.BaseStream.Position = (Int64)endOffset;
        return node;
      }

      List<FbxNode> roots = new List<FbxNode>();
      while (br.BaseStream.Position < br.BaseStream.Length - nullRecordLength) {
        FbxNode node = ReadNode();
        if (node == null) break;
        roots.Add(node);
      }

      NodeListItem summary = Branch("FBX / Autodesk binary scene");
      Add(summary, "Version", version);
      Add(summary, "Nodes", nodeCount.ToString("N0", CultureInfo.InvariantCulture));
      NodeListItem tree = Branch("FBX tree (" + roots.Count + " top-level nodes)");
      foreach (FbxNode node in roots) tree.children.Add(BuildFbxTree(node));
      return Roots(summary, tree);
    }

    private static UInt64 ReadFbxWord(BinaryReader br, Boolean wide) => wide ? br.ReadUInt64() : br.ReadUInt32();

    private static String ReadFbxPropertySummary(BinaryReader br) {
      Char type = (Char)br.ReadByte();
      switch (type) {
        case 'Y': return "Y Int16 = " + br.ReadInt16().ToString(CultureInfo.InvariantCulture);
        case 'C': return "C Bool = " + (br.ReadByte() != 0);
        case 'I': return "I Int32 = " + br.ReadInt32().ToString(CultureInfo.InvariantCulture);
        case 'F': return "F Float = " + br.ReadSingle().ToString("R", CultureInfo.InvariantCulture);
        case 'D': return "D Double = " + br.ReadDouble().ToString("R", CultureInfo.InvariantCulture);
        case 'L': return "L Int64 = " + br.ReadInt64().ToString(CultureInfo.InvariantCulture);
        case 'S':
        case 'R': {
          UInt32 length = br.ReadUInt32();
          if (br.BaseStream.Position + length > br.BaseStream.Length) throw new EndOfStreamException();
          if (type == 'S') {
            Byte[] bytes = br.ReadBytes(checked((Int32)length));
            String value = Encoding.UTF8.GetString(bytes).Replace("\0", "\\0");
            if (value.Length > 240) value = value.Substring(0, 240) + "…";
            return "S String = " + value;
          }
          br.BaseStream.Position += length;
          return "R Raw = " + length.ToString("N0", CultureInfo.InvariantCulture) + " bytes";
        }
        case 'f': case 'd': case 'l': case 'i': case 'b': {
          UInt32 count = br.ReadUInt32();
          UInt32 encoding = br.ReadUInt32();
          UInt32 byteLength = br.ReadUInt32();
          if (br.BaseStream.Position + byteLength > br.BaseStream.Length) throw new EndOfStreamException();
          // Jedipedia inflates these arrays to inspect values. The desktop tree only needs a useful structural
          // summary and deliberately does not allocate potentially huge geometry arrays.
          br.BaseStream.Position += byteLength;
          return type + " Array[" + count.ToString("N0", CultureInfo.InvariantCulture) + "] " + (encoding == 1 ? "zlib" : encoding == 0 ? "raw" : "encoding " + encoding) + ", " + byteLength.ToString("N0", CultureInfo.InvariantCulture) + " bytes";
        }
        default: throw new InvalidDataException("Unknown FBX property type '" + type + "'.");
      }
    }

    private static NodeListItem BuildFbxTree(FbxNode node) {
      NodeListItem result = Branch(node.Name);
      if (node.Properties.Count > 0) {
        NodeListItem props = Branch("Properties (" + node.Properties.Count + ")");
        for (Int32 i = 0; i < node.Properties.Count; i++) props.children.Add(Leaf(i.ToString(CultureInfo.InvariantCulture), node.Properties[i]));
        result.children.Add(props);
      }
      foreach (FbxNode child in node.Children) result.children.Add(BuildFbxTree(child));
      return result;
    }
  }
}
