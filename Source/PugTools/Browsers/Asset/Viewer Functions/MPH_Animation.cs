using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;

namespace FileFormats {
  public sealed class JBARig {
    public List<JBARigBone> Bones { get; } = new List<JBARigBone>();
    public Int32[] AnimToRig { get; internal set; } = Array.Empty<Int32>();
    public String Source { get; internal set; } = String.Empty;
  }

  public sealed class JBARigBone {
    public String Name { get; internal set; } = String.Empty;
    public Int32 Parent { get; internal set; } = -1;
    public Vector3 BindTranslation { get; internal set; }
    public Quaternion BindRotation { get; internal set; } = Quaternion.Identity;
  }

  internal sealed class MPHAnimationEntry {
    public UInt32 AnimIndex;
    public UInt32 BoneMappingIndex;
  }

  internal sealed class MPHAnimationSet {
    public UInt32 RigSectionIndex;
    public List<MPHAnimationEntry> Entries = new List<MPHAnimationEntry>();
  }

  internal sealed class MPHAnimationList {
    public List<MPHAnimationSet> Sets = new List<MPHAnimationSet>();
    public List<String> Names = new List<String>();
  }

  internal sealed class MPHRigSection {
    public UInt32 Index;
    public List<JBARigBone> Bones = new List<JBARigBone>();
  }

  internal sealed class MPHMapSection {
    public UInt32 Index;
    public Int32[] AnimToRig = Array.Empty<Int32>();
  }

  public static class MPHAnimationReader {
    private const UInt32 MAWB = 0x4257414D;

    // World-placeable MAG specs name a Morpheme network, not the idle JBA directly. Jedipedia walks the
    // network recipe; for the lightweight world preview we pick the best authored animation name from the
    // network's AnimationList and then use the normal clip->rig mapping below. This still keeps the animation
    // on the correct network/skeleton instead of leaving every animated SPN object rigid.
    public static String FindBestIdleClipName(BinaryReader br) {
      List<String> names = FindIdleClipNames(br);
      return names.Count > 0 ? names[0] : null;
    }

    public static List<String> FindIdleClipNames(BinaryReader br) {
      if (br == null) throw new ArgumentNullException(nameof(br));
      Int64 original = br.BaseStream.Position;
      try {
        Int64 pos = original;
        Boolean is64 = false;
        if (ReadUInt32At(br, pos) == MAWB) {
          UInt32 version = ReadUInt32At(br, pos + 4);
          if (version != 2) return new List<String>();
          is64 = true; pos += 8;
        }
        var candidates = new List<(String Name, Int32 Score, Int32 Order)>();
        var seen = new HashSet<String>(StringComparer.OrdinalIgnoreCase);
        Int32 order = 0;
        while (pos + 16 <= br.BaseStream.Length) {
          UInt32 type = ReadUInt32At(br, pos);
          UInt32 length = ReadUInt32At(br, pos + 8);
          Int64 sectionStart = pos + 16;
          Int64 sectionEnd = sectionStart + length;
          if (length == 0 || sectionEnd > br.BaseStream.Length) break;
          if (type == 1) {
            try {
              MPHAnimationList list = ReadAnimationList(br, sectionStart, is64);
              foreach (String name in list.Names) {
                if (String.IsNullOrWhiteSpace(name)) continue;
                String stem = Path.GetFileNameWithoutExtension(name).ToLowerInvariant();
                if (!seen.Add(stem)) continue;
                candidates.Add((stem, ScoreIdleClip(stem), order++));
              }
            } catch { }
          }
          pos = is64 ? (((sectionEnd - 8 + 15) & ~15L) + 8) : ((sectionEnd + 15) & ~15L);
        }
        return candidates.OrderByDescending(x => x.Score).ThenBy(x => x.Order).Select(x => x.Name).ToList();
      } finally {
        if (br.BaseStream.CanSeek) br.BaseStream.Position = original;
      }
    }

    private static Int32 ScoreIdleClip(String name) {
      if (String.IsNullOrWhiteSpace(name)) return Int32.MinValue;
      String n = name.ToLowerInvariant();
      Int32 score = 0;
      if (n.Contains("idle")) score += 1000;
      if (n.Contains("stand")) score += 350;
      if (n.Contains("closed") || n.Contains("close")) score += 250;
      if (n.Contains("loop")) score += 200;
      if (n.Contains("default")) score += 150;
      if (n.Contains("open")) score -= 100;
      if (n.Contains("combat")) score -= 50;
      // Stable preference for the first sensible AnimationList entry when the network uses opaque names.
      return score;
    }

    public static JBARig FindRigForClip(
      BinaryReader br,
      String clipName
    ) {
      if (br == null) throw new ArgumentNullException(nameof(br));
      if (String.IsNullOrWhiteSpace(clipName)) return null;

      Int64 pos = br.BaseStream.Position;
      Boolean is64 = false;

      if (ReadUInt32At(br, pos) == MAWB) {
        UInt32 version = ReadUInt32At(br, pos + 4);
        if (version != 2) return null;
        is64 = true;
        pos += 8;
      }

      Dictionary<UInt32, MPHRigSection> rigs =
        new Dictionary<UInt32, MPHRigSection>();
      MPHRigSection firstRig = null;
      Dictionary<UInt32, MPHMapSection> maps =
        new Dictionary<UInt32, MPHMapSection>();
      List<MPHAnimationList> animationLists =
        new List<MPHAnimationList>();

      while (pos + 16 <= br.BaseStream.Length) {
        UInt32 type = ReadUInt32At(br, pos);
        UInt32 index = ReadUInt32At(br, pos + 4);
        UInt32 length = ReadUInt32At(br, pos + 8);
        Int64 sectionStart = pos + 16;
        Int64 sectionEnd = sectionStart + length;

        if (length == 0 || sectionEnd > br.BaseStream.Length)
          break;

        try {
          if (type == 1)
            animationLists.Add(ReadAnimationList(br, sectionStart, is64));
          else if (type == 2) {
            MPHRigSection parsedRig = ReadRig(
              br,
              sectionStart,
              index,
              is64
            );
            rigs[index] = parsedRig;

            // Jedipedia's standalone JBA binder deliberately uses the first
            // real type-2 rig section in file order. AnimationList's
            // jointSectionIndex is metadata for the network subsection and is
            // not the selector used by jbaApplyMphMapping().
            if (firstRig == null
                && parsedRig != null
                && parsedRig.Bones != null
                && parsedRig.Bones.Count > 0) {
              firstRig = parsedRig;
            }
          }
          else if (type == 3)
            maps[index] = ReadMap(br, sectionStart, index, is64);
        }
        catch {
          // A large anim_library can contain sections not needed by this clip.
          // Keep scanning instead of making JBA preview fail completely.
        }

        // Match Jedipedia mphSections() exactly. The section payload length
        // ends BEFORE the alignment padding. Morpheme aligns every following
        // section header to 16 bytes; in the 64-bit dialect the section stream
        // itself is offset by eight bytes, so alignment is relative to that
        // base. Omitting this step makes small networks such as
        // placeable_openclose.mph lose their type-2/type-3 rig sections.
        pos = is64
          ? (((sectionEnd - 8 + 15) & ~15L) + 8)
          : ((sectionEnd + 15) & ~15L);
      }

      String wanted = Path.GetFileNameWithoutExtension(clipName);

      foreach (MPHAnimationList list in animationLists) {
        foreach (MPHAnimationSet set in list.Sets) {
          for (Int32 i = 0; i < set.Entries.Count; i++) {
            MPHAnimationEntry entry = set.Entries[i];
            if (entry.AnimIndex >= list.Names.Count) continue;

            String name = list.Names[(Int32)entry.AnimIndex];
            // AnimationList entries are not consistently bare stems: older/current clients may store foo.jba or a
            // resource-relative path. The JBA resolver hands us a stem, so normalize both sides before selecting the
            // RigToAnim map. An exact raw comparison otherwise finds the clip file but binds no usable rig.
            String listed = Path.GetFileNameWithoutExtension((name ?? String.Empty).Replace('\\', '/'));
            if (!String.Equals(
                  listed,
                  wanted,
                  StringComparison.OrdinalIgnoreCase))
              continue;

            // The AnimationList set identifies the rig this RigToAnimMap was authored against. Jedipedia's
            // current NPC path uses that set-0 rig and only falls back to the first type-2 rig when the indexed
            // section is absent. Using the first rig unconditionally can bind a valid clip to a different LOD rig,
            // producing zero useful GR2 bone matches and making a conversation actor look completely static.
            MPHRigSection rig = rigs.TryGetValue(set.RigSectionIndex, out MPHRigSection indexedRig) ? indexedRig : firstRig;
            if (rig == null || rig.Bones == null || rig.Bones.Count == 0) continue;

            if (!maps.TryGetValue(entry.BoneMappingIndex, out MPHMapSection map))
              continue;

            return new JBARig {
              Source = wanted,
              AnimToRig = map.AnimToRig
            }.WithBones(rig.Bones);
          }
        }
      }

      return null;
    }

    private static MPHAnimationList ReadAnimationList(
      BinaryReader br,
      Int64 start,
      Boolean is64
    ) {
      MPHAnimationList list = new MPHAnimationList();

      // Morpheme's 64-bit serialization uses pointer-width SPACING, but the
      // relative offsets/counts themselves remain 32-bit values. This is the
      // layout used by Jedipedia's mphParseAnimationList(). Treating these as
      // UInt64 shifts every field after the first one and produces an empty or
      // unrelated RigToAnimMap for current SWTOR clips.
      UInt32 numSets = ReadUInt32At(br, start);
      UInt32 subsectionListOffset = ReadUInt32At(br, start + (is64 ? 8 : 4));
      UInt32 jbaOffset = ReadUInt32At(br, start + (is64 ? 16 : 8));

      if (numSets == 0 || numSets > 64 || subsectionListOffset == 0)
        return list;

      for (UInt32 setIndex = 0; setIndex < numSets; setIndex++) {
        Int64 offsetWord = start + subsectionListOffset
          + setIndex * (is64 ? 8L : 4L);
        UInt32 rawOffset = ReadUInt32At(br, offsetWord);
        if (rawOffset == 0) break;

        Int64 subStart = start + rawOffset;
        UInt32 rigIndex = ReadUInt32At(br, subStart + (is64 ? 8 : 4));
        UInt32 numEntries = ReadUInt32At(br, subStart + (is64 ? 16 : 8));

        if (numEntries == 0 || numEntries > 100000)
          break;

        Int64 entryStart = subStart + (is64 ? 40 : 20);
        Int32 stride = is64 ? 0x58 : 0x34;
        Int32 animField = is64 ? 0x50 : 0x2C;

        MPHAnimationSet set = new MPHAnimationSet {
          RigSectionIndex = rigIndex
        };

        for (UInt32 i = 0; i < numEntries; i++) {
          Int64 entry = entryStart + i * (Int64)stride;
          if (entry < 0 || entry + stride > br.BaseStream.Length)
            break;

          set.Entries.Add(new MPHAnimationEntry {
            AnimIndex = ReadUInt32At(br, entry + animField),
            BoneMappingIndex = ReadUInt32At(br, entry + animField + 4)
          });
        }

        if (set.Entries.Count == 0)
          break;
        list.Sets.Add(set);
      }

      if (jbaOffset != 0)
        list.Names = ReadStringTable(br, start + jbaOffset, is64);
      return list;
    }

    private static MPHRigSection ReadRig(
      BinaryReader br,
      Int64 start,
      UInt32 index,
      Boolean is64
    ) {
      // Same packed/padded layout as Jedipedia mphParseRig(). Relative
      // pointers remain u32 in both dialects.
      UInt32 parentsOffset = ReadUInt32At(br, start + 16);
      UInt32 namesOffset = ReadUInt32At(br, start + (is64 ? 32 : 28));
      UInt32 rotationsOffset = ReadUInt32At(br, start + (is64 ? 40 : 32));
      UInt32 translationsOffset = ReadUInt32At(br, start + (is64 ? 48 : 36));

      Int64 parents = start + parentsOffset;
      UInt32 numBones = ReadUInt32At(br, parents);
      if (numBones == 0 || numBones > 4096)
        return new MPHRigSection { Index = index };

      Int64 parentData = parents + (is64 ? 16 : 8);
      List<String> names = ReadStringTable(br, start + namesOffset, is64);
      MPHRigSection rig = new MPHRigSection { Index = index };

      for (Int32 i = 0; i < numBones; i++) {
        Int64 rp = start + rotationsOffset + i * 16L;
        Int64 tp = start + translationsOffset + i * 16L;

        Quaternion rotation = new Quaternion(
          ReadSingleAt(br, rp),
          ReadSingleAt(br, rp + 4),
          ReadSingleAt(br, rp + 8),
          ReadSingleAt(br, rp + 12)
        );
        if (rotation.LengthSquared() > 0.000001F)
          rotation = Quaternion.Normalize(rotation);
        else
          rotation = Quaternion.Identity;

        rig.Bones.Add(new JBARigBone {
          Name = i < names.Count && !String.IsNullOrEmpty(names[i])
            ? names[i]
            : "bone_" + i,
          Parent = ReadInt32At(br, parentData + i * 4L),
          BindRotation = rotation,
          BindTranslation = new Vector3(
            ReadSingleAt(br, tp),
            ReadSingleAt(br, tp + 4),
            ReadSingleAt(br, tp + 8)
          )
        });
      }

      return rig;
    }

    private static MPHMapSection ReadMap(
      BinaryReader br,
      Int64 start,
      UInt32 index,
      Boolean is64
    ) {
      // Exact layout used by Jedipedia mphParseRigMap():
      //   u32 numEntries
      //   32-bit: 4 bytes header/pad, entries start at +8
      //   64-bit: 12 bytes header/pad, entries start at +16
      // Each entry is { u16 rigIndex, u16 animIndex }. There is no pointer
      // that needs to equal 0x08/0x10; treating the padding word as one made
      // valid small placeable maps look empty.
      UInt32 count = ReadUInt32At(br, start);
      if (count == 0 || count > 4096)
        return new MPHMapSection { Index = index };

      Int64 p = start + (is64 ? 16L : 8L);
      if (p < 0 || p + count * 4L > br.BaseStream.Length)
        return new MPHMapSection { Index = index };

      List<(UInt16 rig, UInt16 anim)> pairs =
        new List<(UInt16, UInt16)>();
      Int32 maxAnim = -1;

      for (UInt32 i = 0; i < count; i++) {
        UInt16 rig = ReadUInt16At(br, p);
        UInt16 anim = ReadUInt16At(br, p + 2);
        p += 4;
        pairs.Add((rig, anim));
        maxAnim = Math.Max(maxAnim, anim);
      }

      Int32[] map = new Int32[Math.Max(0, maxAnim + 1)];
      for (Int32 i = 0; i < map.Length; i++) map[i] = -1;
      foreach (var pair in pairs)
        if (pair.anim < map.Length)
          map[pair.anim] = pair.rig;

      return new MPHMapSection {
        Index = index,
        AnimToRig = map
      };
    }

    private static List<String> ReadStringTable(
      BinaryReader br,
      Int64 start,
      Boolean is64
    ) {
      UInt32 numStrings = ReadUInt32At(br, start);
      UInt32 stringDataLength = ReadUInt32At(br, start + 4);
      if (numStrings > 100000)
        return new List<String>();

      // Offsets are u32 values placed at pointer-width aligned words.
      UInt32 indicesOffset = ReadUInt32At(br, start + 8);
      UInt32 lengthsOffset = ReadUInt32At(br, start + (is64 ? 16 : 12));
      UInt32 stringsOffset = ReadUInt32At(br, start + (is64 ? 24 : 16));

      Int64 indicesPos = start + indicesOffset;
      Int64 offsetsPos = start + lengthsOffset;
      Int64 stringsPos = start + stringsOffset;

      // IMPORTANT: Morpheme string-table ids are SPARSE. numStrings is the
      // number of stored records, not the largest id plus one. AnimationList
      // entries routinely reference ids in the thousands (Jedipedia documents
      // e.g. entry.animIndex 3264) even when far fewer strings are physically
      // present. A dense List(numStrings) therefore silently drops exactly the
      // names needed to resolve small placeable networks such as
      // placeable_openclose.mph.
      //
      // JavaScript arrays grow on assignment; reproduce that behavior here.
      // Keep a generous sanity cap so corrupt assets cannot request an
      // unbounded allocation.
      const Int32 maxSparseStringIndex = 1000000;
      List<String> result = new List<String>();

      for (Int32 i = 0; i < numStrings; i++) {
        Int32 stringIndex = ReadInt32At(br, indicesPos + i * 4L);
        UInt32 off = ReadUInt32At(br, offsetsPos + i * 4L);

        // -1 marks unnamed/removed slots and its offset word can contain
        // arbitrary compiler residue.
        if (stringIndex < 0
            || stringIndex > maxSparseStringIndex
            || off >= stringDataLength) {
          continue;
        }

        while (result.Count <= stringIndex)
          result.Add(null);

        String value = ReadCString(br, stringsPos + off);
        if (String.IsNullOrEmpty(result[stringIndex]))
          result[stringIndex] = value;
        else
          result[stringIndex] += " + " + value;
      }

      return result;
    }

    private static UInt64 ReadWide(
      BinaryReader br,
      ref Int64 pos,
      Boolean is64
    ) {
      UInt64 value = is64
        ? ReadUInt64At(br, pos)
        : ReadUInt32At(br, pos);
      pos += is64 ? 8 : 4;
      return value;
    }

    private static UInt16 ReadUInt16At(BinaryReader br, Int64 pos) {
      Int64 old = br.BaseStream.Position;
      br.BaseStream.Position = pos;
      UInt16 value = br.ReadUInt16();
      br.BaseStream.Position = old;
      return value;
    }

    private static UInt32 ReadUInt32At(BinaryReader br, Int64 pos) {
      Int64 old = br.BaseStream.Position;
      br.BaseStream.Position = pos;
      UInt32 value = br.ReadUInt32();
      br.BaseStream.Position = old;
      return value;
    }

    private static UInt64 ReadUInt64At(BinaryReader br, Int64 pos) {
      Int64 old = br.BaseStream.Position;
      br.BaseStream.Position = pos;
      UInt64 value = br.ReadUInt64();
      br.BaseStream.Position = old;
      return value;
    }

    private static Int32 ReadInt32At(BinaryReader br, Int64 pos) {
      Int64 old = br.BaseStream.Position;
      br.BaseStream.Position = pos;
      Int32 value = br.ReadInt32();
      br.BaseStream.Position = old;
      return value;
    }

    private static Single ReadSingleAt(BinaryReader br, Int64 pos) {
      Int64 old = br.BaseStream.Position;
      br.BaseStream.Position = pos;
      Single value = br.ReadSingle();
      br.BaseStream.Position = old;
      return value;
    }

    private static String ReadCString(BinaryReader br, Int64 pos) {
      Int64 old = br.BaseStream.Position;
      br.BaseStream.Position = pos;

      List<Byte> bytes = new List<Byte>();
      while (br.BaseStream.Position < br.BaseStream.Length) {
        Byte b = br.ReadByte();
        if (b == 0) break;
        bytes.Add(b);
      }

      br.BaseStream.Position = old;
      return System.Text.Encoding.UTF8.GetString(bytes.ToArray());
    }

    private static JBARig WithBones(
      this JBARig rig,
      IEnumerable<JBARigBone> bones
    ) {
      rig.Bones.AddRange(bones);
      return rig;
    }
  }
}
