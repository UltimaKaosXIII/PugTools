using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace PugTools {
  /// <summary>
  /// Read-only Morpheme .mph network inspector. The loader intentionally shares
  /// the same 32/64-bit layouts used by the JBA/Morpheme runtime support, but it
  /// exposes the network, animation lists, rigs, mappings and event tracks in a
  /// structured Asset Browser tree instead of falling back to hex.
  /// </summary>
  internal static class ViewMPH {
    private const UInt32 MawbMagic = 0x4257414D; // "MAWB"

    internal sealed class MphFileInfo {
      internal Boolean Is64Bit;
      internal UInt32 Version;
      internal Int64 FileLength;
      internal readonly List<MphSection> Sections = new List<MphSection>();
    }

    internal sealed class MphSection {
      internal UInt32 Type;
      internal UInt32 Index;
      internal UInt32 Length;
      internal Int64 HeaderStart;
      internal Int64 Start;
      internal String ParseError;
      internal MphNetwork Network;
      internal MphAnimationList AnimationList;
      internal MphRig Rig;
      internal MphRigMap RigMap;
      internal MphEventTrack EventTrack;
    }

    internal sealed class MphNetwork {
      internal UInt32 AnimationLibrarySectionIndex;
      internal readonly List<MphNode> Nodes = new List<MphNode>();
      internal readonly List<String> ParameterStrings = new List<String>();
    }

    internal sealed class MphNode {
      internal UInt32 Type;
      internal UInt32 Index;
      internal String Name;
      internal Int64 PayloadStart;
      internal Int64 PayloadEnd;
      internal readonly Dictionary<String, String> Fields = new Dictionary<String, String>();
      internal readonly List<UInt32> Flow = new List<UInt32>();
      internal readonly List<UInt32> Control = new List<UInt32>();
    }

    internal sealed class MphAnimationList {
      internal readonly List<MphAnimationSet> Sets = new List<MphAnimationSet>();
      internal List<String> JbaNames = new List<String>();
      internal List<String> Tags = new List<String>();
    }

    internal sealed class MphAnimationSet {
      internal UInt32 JointSectionIndex;
      internal UInt32 UnknownStart;
      internal UInt32 UnknownFinal;
      internal readonly List<MphAnimationEntry> Entries = new List<MphAnimationEntry>();
    }

    internal sealed class MphAnimationEntry {
      internal UInt32 AnimationIndex;
      internal UInt32 BoneMappingIndex;
      internal Int32 Flag;
      internal readonly List<Int32> DiscreteEvents = new List<Int32>();
    }

    internal sealed class MphRig {
      internal UInt32 TrajectoryBoneIndex;
      internal UInt32 CharacterRootBoneIndex;
      internal readonly List<MphBone> Bones = new List<MphBone>();
    }

    internal sealed class MphBone {
      internal String Name;
      internal Int32 Parent;
      internal Single Qx;
      internal Single Qy;
      internal Single Qz;
      internal Single Qw;
      internal Single X;
      internal Single Y;
      internal Single Z;
    }

    internal sealed class MphRigMap {
      internal readonly List<MphRigMapEntry> Entries = new List<MphRigMapEntry>();
    }

    internal sealed class MphRigMapEntry {
      internal UInt16 RigChannel;
      internal UInt16 AnimationChannel;
    }

    internal sealed class MphEventTrack {
      internal UInt32 TrackType;
      internal UInt32 TrackNameRaw;
      internal UInt32 UserData;
      internal UInt32 RuntimeTrackId;
      internal readonly List<MphEvent> Events = new List<MphEvent>();
    }

    internal sealed class MphEvent {
      internal Single Start;
      internal Single Duration;
      internal UInt32 UserType;
    }

    internal static MphFileInfo Parse(BinaryReader br) {
      if (br == null) throw new ArgumentNullException(nameof(br));
      Stream stream = br.BaseStream;
      if (stream == null || !stream.CanSeek) throw new InvalidDataException("MPH stream is not seekable.");
      stream.Position = 0;
      if (stream.Length < 16) throw new InvalidDataException("MPH file is too small.");

      MphFileInfo info = new MphFileInfo { FileLength = stream.Length };
      Int64 pos = 0;
      if (ReadUInt32At(br, 0) == MawbMagic) {
        if (stream.Length < 24) throw new InvalidDataException("64-bit MPH file is too small.");
        info.Version = ReadUInt32At(br, 4);
        if (info.Version != 2) throw new InvalidDataException("Unsupported MAWB/MPH version " + info.Version + ".");
        info.Is64Bit = true;
        pos = 8;
      }

      while (pos + 16 <= stream.Length) {
        UInt32 type = ReadUInt32At(br, pos);
        UInt32 index = ReadUInt32At(br, pos + 4);
        UInt32 length = ReadUInt32At(br, pos + 8);
        UInt32 zero = ReadUInt32At(br, pos + 12);
        if (length == 0) break;

        Int64 start = pos + 16;
        Int64 end = start + length;
        if (end < start || end > stream.Length)
          throw new InvalidDataException("MPH section " + type + "-" + index + " extends outside the file.");

        MphSection section = new MphSection {
          Type = type,
          Index = index,
          Length = length,
          HeaderStart = pos,
          Start = start
        };
        if (zero != 0) section.ParseError = "Non-zero section header word: 0x" + zero.ToString("X8");

        try {
          ParseSection(br, info, section);
        }
        catch (Exception ex) {
          section.ParseError = String.IsNullOrEmpty(section.ParseError)
            ? ex.Message
            : section.ParseError + "; " + ex.Message;
        }
        info.Sections.Add(section);

        pos = info.Is64Bit
          ? Align64Section(end)
          : Align16(end);
        if (pos <= section.HeaderStart) break;
      }

      if (info.Sections.Count == 0)
        throw new InvalidDataException("No MPH sections were found.");

      // Network sections are commonly serialized before the AnimationList they reference. The first pass therefore
      // knows the graph but cannot resolve type-104 animation nodes to JBA names. Re-read only the network sections
      // after the complete section table exists; this is cheap and gives conversation code the same path -> clip
      // index Jedipedia builds from mphParseNetworkClips().
      foreach (MphSection section in info.Sections.Where(x => x.Type == 0)) {
        try { section.Network = ParseNetwork(br, info, section); }
        catch (Exception ex) { section.ParseError = String.IsNullOrEmpty(section.ParseError) ? ex.Message : section.ParseError + "; " + ex.Message; }
      }
      return info;
    }

    private static void ParseSection(BinaryReader br, MphFileInfo file, MphSection section) {
      switch (section.Type) {
        case 0:
          section.Network = ParseNetwork(br, file, section);
          break;
        case 1:
          section.AnimationList = ParseAnimationList(br, file.Is64Bit, section);
          break;
        case 2:
          section.Rig = ParseRig(br, file.Is64Bit, section);
          break;
        case 3:
          section.RigMap = ParseRigMap(br, file.Is64Bit, section);
          break;
        case 4:
          section.EventTrack = ParseEventTrack(br, file.Is64Bit, section);
          break;
      }
    }

    private static MphAnimationList ParseAnimationList(BinaryReader br, Boolean is64, MphSection section) {
      Int64 start = section.Start;
      Int64 end = section.Start + section.Length;
      UInt32 numSets = ReadUInt32AtChecked(br, start, end);
      UInt32 subsectionListOffset = ReadUInt32AtChecked(br, start + (is64 ? 8 : 4), end);
      UInt32 jbaOffset = ReadUInt32AtChecked(br, start + (is64 ? 16 : 8), end);
      UInt32 tagOffset = ReadUInt32AtChecked(br, start + (is64 ? 24 : 12), end);
      if (numSets > 256) throw new InvalidDataException("AnimationList has an unreasonable set count: " + numSets + ".");

      MphAnimationList list = new MphAnimationList();
      list.JbaNames = jbaOffset == 0 ? new List<String>() : ReadStringTable(br, start + jbaOffset, end, is64);
      list.Tags = tagOffset == 0 ? new List<String>() : ReadStringTable(br, start + tagOffset, end, is64);

      for (UInt32 setIndex = 0; setIndex < numSets; setIndex++) {
        Int64 offsetPos = start + subsectionListOffset + setIndex * (is64 ? 8L : 4L);
        UInt32 rawOffset = ReadUInt32AtChecked(br, offsetPos, end);
        if (rawOffset == 0) continue;
        Int64 subStart = start + rawOffset;
        EnsureRange(subStart, is64 ? 40 : 20, end, "AnimationList set header");

        MphAnimationSet set = new MphAnimationSet {
          UnknownStart = ReadUInt32AtChecked(br, subStart, end),
          JointSectionIndex = ReadUInt32AtChecked(br, subStart + (is64 ? 8 : 4), end)
        };
        UInt32 numEntries = ReadUInt32AtChecked(br, subStart + (is64 ? 16 : 8), end);
        if (numEntries > 100000) throw new InvalidDataException("AnimationList set has an unreasonable entry count: " + numEntries + ".");

        Int64 entryStart = subStart + (is64 ? 40 : 20);
        Int32 stride = is64 ? 0x58 : 0x34;
        for (UInt32 i = 0; i < numEntries; i++) {
          Int64 ep = entryStart + i * (Int64)stride;
          EnsureRange(ep, stride, end, "AnimationList entry");
          UInt32 numEvents = ReadUInt32AtChecked(br, ep + (is64 ? 16 : 8), end);
          UInt32 eventsOffset = ReadUInt32AtChecked(br, ep + (is64 ? 24 : 12), end);
          Int32 flag = ReadInt32AtChecked(br, ep + (is64 ? 40 : 20), end);
          UInt32 animIndex = ReadUInt32AtChecked(br, ep + (is64 ? 80 : 44), end);
          UInt32 mapIndex = ReadUInt32AtChecked(br, ep + (is64 ? 84 : 48), end);

          MphAnimationEntry entry = new MphAnimationEntry {
            AnimationIndex = animIndex,
            BoneMappingIndex = mapIndex,
            Flag = flag
          };
          if (numEvents <= 4096 && eventsOffset != 0) {
            Int64 eventPos = ep + eventsOffset;
            for (UInt32 e = 0; e < numEvents; e++)
              entry.DiscreteEvents.Add(ReadInt32AtChecked(br, eventPos + e * 4L, end));
          }
          set.Entries.Add(entry);
        }

        // The compiler places the final words immediately after the fixed entry
        // array; the fourth word is useful when comparing old/new networks.
        Int64 finalPos = entryStart + numEntries * (Int64)stride;
        if (finalPos + 16 <= end) set.UnknownFinal = ReadUInt32At(br, finalPos + 12);
        list.Sets.Add(set);
      }
      return list;
    }

    private static MphRig ParseRig(BinaryReader br, Boolean is64, MphSection section) {
      Int64 start = section.Start;
      Int64 end = section.Start + section.Length;
      UInt32 parentOffset = ReadUInt32AtChecked(br, start + 16, end);
      UInt32 trajectory = ReadUInt32AtChecked(br, start + (is64 ? 24 : 20), end);
      UInt32 root = ReadUInt32AtChecked(br, start + (is64 ? 28 : 24), end);
      UInt32 namesOffset = ReadUInt32AtChecked(br, start + (is64 ? 32 : 28), end);
      UInt32 rotationsOffset = ReadUInt32AtChecked(br, start + (is64 ? 40 : 32), end);
      UInt32 translationsOffset = ReadUInt32AtChecked(br, start + (is64 ? 48 : 36), end);

      Int64 parents = start + parentOffset;
      UInt32 numBones = ReadUInt32AtChecked(br, parents, end);
      if (numBones > 16384) throw new InvalidDataException("Rig has an unreasonable bone count: " + numBones + ".");
      Int64 parentData = parents + (is64 ? 16 : 8);
      List<String> names = ReadStringTable(br, start + namesOffset, end, is64);

      MphRig rig = new MphRig {
        TrajectoryBoneIndex = trajectory,
        CharacterRootBoneIndex = root
      };
      for (Int32 i = 0; i < numBones; i++) {
        Int64 rp = start + rotationsOffset + i * 16L;
        Int64 tp = start + translationsOffset + i * 16L;
        EnsureRange(rp, 16, end, "Rig rotation");
        EnsureRange(tp, 16, end, "Rig translation");
        rig.Bones.Add(new MphBone {
          Name = i < names.Count && !String.IsNullOrWhiteSpace(names[i]) ? names[i] : "bone_" + i,
          Parent = ReadInt32AtChecked(br, parentData + i * 4L, end),
          Qx = ReadSingleAtChecked(br, rp, end),
          Qy = ReadSingleAtChecked(br, rp + 4, end),
          Qz = ReadSingleAtChecked(br, rp + 8, end),
          Qw = ReadSingleAtChecked(br, rp + 12, end),
          X = ReadSingleAtChecked(br, tp, end),
          Y = ReadSingleAtChecked(br, tp + 4, end),
          Z = ReadSingleAtChecked(br, tp + 8, end)
        });
      }
      return rig;
    }

    private static MphRigMap ParseRigMap(BinaryReader br, Boolean is64, MphSection section) {
      Int64 start = section.Start;
      Int64 end = section.Start + section.Length;
      UInt32 count = ReadUInt32AtChecked(br, start, end);
      if (count > 16384) throw new InvalidDataException("RigToAnimMap has an unreasonable entry count: " + count + ".");
      Int64 pos = start + (is64 ? 16 : 8);
      EnsureRange(pos, count * 4L, end, "RigToAnimMap entries");
      MphRigMap map = new MphRigMap();
      for (UInt32 i = 0; i < count; i++) {
        map.Entries.Add(new MphRigMapEntry {
          RigChannel = ReadUInt16At(br, pos),
          AnimationChannel = ReadUInt16At(br, pos + 2)
        });
        pos += 4;
      }
      return map;
    }

    private static MphEventTrack ParseEventTrack(BinaryReader br, Boolean is64, MphSection section) {
      Int64 start = section.Start;
      Int64 end = section.Start + section.Length;
      UInt32 count = ReadUInt32AtChecked(br, start, end);
      if (count > 100000) throw new InvalidDataException("Discrete event track has an unreasonable event count: " + count + ".");
      MphEventTrack track = new MphEventTrack {
        TrackType = ReadUInt32AtChecked(br, start + 4, end),
        TrackNameRaw = ReadUInt32AtChecked(br, start + 8, end),
        UserData = ReadUInt32AtChecked(br, start + 12, end),
        RuntimeTrackId = ReadUInt32AtChecked(br, start + 16, end)
      };
      Int64 pos = start + 20 + (is64 ? 12 : 0);
      for (UInt32 i = 0; i < count; i++) {
        EnsureRange(pos, 12, end, "Discrete event");
        track.Events.Add(new MphEvent {
          Start = ReadSingleAt(br, pos),
          Duration = ReadSingleAt(br, pos + 4),
          UserType = ReadUInt32At(br, pos + 8)
        });
        pos += 12;
      }
      return track;
    }

    private static MphNetwork ParseNetwork(BinaryReader br, MphFileInfo file, MphSection section) {
      Int64 start = section.Start;
      Int64 end = section.Start + section.Length;
      Boolean is64 = file.Is64Bit;
      UInt32 animSection = ReadUInt32AtChecked(br, start, end);
      UInt32 numNodes = ReadUInt32AtChecked(br, start + (is64 ? 8 : 4), end);
      UInt32 nodeOffset = ReadUInt32AtChecked(br, start + (is64 ? 16 : 8), end);
      Int64 nodeNamesField = start + (is64 ? 88 : 52);
      UInt32 nodeNamesOffset = ReadUInt32AtChecked(br, nodeNamesField, end);
      UInt32 paramStringsOffset = ReadUInt32AtChecked(br, nodeNamesField + (is64 ? 8 : 4), end);
      if (numNodes > 100000) throw new InvalidDataException("Network has an unreasonable node count: " + numNodes + ".");

      MphNetwork network = new MphNetwork { AnimationLibrarySectionIndex = animSection };
      List<String> nodeNames = ReadStringTable(br, start + nodeNamesOffset, end, is64);
      List<String> paramStrings = ReadStringTable(br, start + paramStringsOffset, end, is64);
      network.ParameterStrings.AddRange(paramStrings.Where(x => x != null));

      List<UInt32> offsets = new List<UInt32>(checked((Int32)numNodes));
      for (UInt32 i = 0; i < numNodes; i++) {
        Int64 p = start + nodeOffset + i * (is64 ? 8L : 4L);
        offsets.Add(ReadUInt32AtChecked(br, p, end));
      }

      MphAnimationList animationList = file.Sections
        .Where(s => s.Type == 1 && s.Index == animSection)
        .Select(s => s.AnimationList)
        .FirstOrDefault(x => x != null);

      for (UInt32 i = 0; i < numNodes; i++) {
        Int64 np = start + offsets[(Int32)i];
        EnsureRange(np, 8, end, "Network node header");
        UInt32 type = ReadUInt32At(br, np);
        UInt32 index = ReadUInt32At(br, np + 4);
        Int64 payloadStart = np + 8;
        Int64 payloadEnd = i + 1 < numNodes ? start + offsets[(Int32)i + 1] : start + nodeNamesOffset;
        if (payloadEnd < payloadStart || payloadEnd > end) payloadEnd = end;

        MphNode node = new MphNode {
          Type = type,
          Index = index,
          Name = index < nodeNames.Count ? nodeNames[(Int32)index] : null,
          PayloadStart = payloadStart,
          PayloadEnd = payloadEnd
        };
        DecodeNode(br, node, numNodes, is64, animationList);
        network.Nodes.Add(node);
      }
      return network;
    }

    private static void DecodeNode(BinaryReader br, MphNode node, UInt32 numNodes, Boolean is64, MphAnimationList animationList) {
      Int64 start = node.PayloadStart;
      Int64 end = node.PayloadEnd;
      Int32 words = checked((Int32)Math.Min(Int32.MaxValue, Math.Max(0, (end - start) / 4)));
      Func<Int32, UInt32> U = k => k >= 0 && k < words ? ReadUInt32At(br, start + k * 4L) : 0;
      Func<Int32, Single> F = k => k >= 0 && k < words ? ReadSingleAt(br, start + k * 4L) : 0F;
      Action<UInt32> flow = x => AddNodeRef(node.Flow, x, numNodes);
      Action<UInt32> ctrl = x => AddNodeRef(node.Control, x, numNodes, node.Flow);

      switch (node.Type) {
        case 20:
          node.Fields["Default value"] = F(0).ToString("0.#######", CultureInfo.InvariantCulture);
          break;
        case 104: {
          UInt32 animIndex = U(0);
          node.Fields["Animation entry"] = animIndex.ToString(CultureInfo.InvariantCulture);
          node.Fields["Has event track"] = ((end - start) >= (is64 ? 88 : 72)).ToString();
          String name = ResolveAnimationName(animationList, animIndex, out UInt32 mapping);
          if (!String.IsNullOrWhiteSpace(name)) node.Fields["JBA"] = name + ".jba";
          if (mapping != UInt32.MaxValue) node.Fields["RigToAnimMap"] = mapping.ToString(CultureInfo.InvariantCulture);
          break;
        }
        case 402:
        case 401:
          node.Fields["From"] = FormatNodeRef(U(0));
          node.Fields["To"] = FormatNodeRef(U(1));
          if (node.Type == 402) node.Fields["Duration"] = F(2).ToString("0.###", CultureInfo.InvariantCulture) + " s";
          flow(U(0)); flow(U(1));
          break;
        case 107:
        case 101:
          flow(U(0)); flow(U(1));
          ctrl(U(2) & 0xFFFF);
          node.Fields["Weight control"] = FormatNodeRef(U(2) & 0xFFFF);
          break;
        case 109:
          flow(U(0)); ctrl(U(1) & 0xFFFF);
          break;
        case 111:
          ctrl(U(0) & 0xFFFF); ctrl(U(1) & 0xFFFF);
          node.Fields["Operation"] = ArithmeticOperationName(U(2));
          break;
        case 112:
          ctrl(U(0) & 0xFFFF);
          node.Fields["Operation"] = U(1).ToString(CultureInfo.InvariantCulture);
          node.Fields["Constants"] = F(2).ToString("0.####", CultureInfo.InvariantCulture) + ", " + F(3).ToString("0.####", CultureInfo.InvariantCulture);
          break;
        case 108: {
          ctrl(U(1) & 0xFFFF);
          UInt32 count = U(3);
          node.Fields["Sources"] = count.ToString(CultureInfo.InvariantCulture);
          for (UInt32 i = 0; i < count && 4U + 2U * i < (UInt32)words; i++) flow(U(4 + checked((Int32)(2U * i))));
          break;
        }
        case 102: {
          ctrl(U(4) & 0xFFFF);
          UInt32 count = U(6);
          node.Fields["Sources"] = count.ToString(CultureInfo.InvariantCulture);
          for (UInt32 i = 0; i < count && 7U + 2U * i < (UInt32)words; i++) flow(U(7 + checked((Int32)(2U * i))));
          break;
        }
        case 198:
        case 199: {
          ctrl(U(9) & 0xFFFF);
          UInt32 count = U(10);
          node.Fields["Sources"] = count.ToString(CultureInfo.InvariantCulture);
          Int32 baseWord = node.Type == 199 ? 12 : 11;
          for (UInt32 i = 0; i < count && (Int64)baseWord + 2L * i < words; i++) flow(U(baseWord + checked((Int32)(2U * i))));
          break;
        }
        case 10:
          node.Fields["Default state"] = U(0).ToString(CultureInfo.InvariantCulture);
          node.Fields["State count"] = U(1).ToString(CultureInfo.InvariantCulture);
          DecodeStateMachineRefs(br, node, numNodes, is64);
          break;
      }
    }

    private static void DecodeStateMachineRefs(BinaryReader br, MphNode node, UInt32 numNodes, Boolean is64) {
      Int64 start = node.PayloadStart;
      Int64 end = node.PayloadEnd;
      Int64 len = end - start;
      if (len < (is64 ? 32 : 20)) return;
      UInt32 numStates = ReadUInt32At(br, start + 4);
      UInt32 stateDefsPtr = ReadUInt32At(br, start + 8);
      Int64 stateBase = stateDefsPtr >= 8 ? start + stateDefsPtr - 8 : -1;
      Int32 stride = is64 ? 32 : 20;
      if (stateBase < start || stateBase >= end) return;
      UInt32 cap = Math.Min(numStates, 8192);
      for (UInt32 i = 0; i < cap; i++) {
        Int64 p = stateBase + i * (Int64)stride;
        if (p < start || p + 4 > end) break;
        AddNodeRef(node.Flow, ReadUInt32At(br, p), numNodes);
      }
    }

    private static String ResolveAnimationName(MphAnimationList list, UInt32 entryIndex, out UInt32 mapping) {
      mapping = UInt32.MaxValue;
      if (list == null || list.Sets.Count == 0) return null;
      MphAnimationSet first = list.Sets[0];
      if (entryIndex < first.Entries.Count) {
        MphAnimationEntry entry = first.Entries[(Int32)entryIndex];
        mapping = entry.BoneMappingIndex;
        if (entry.AnimationIndex < list.JbaNames.Count) return list.JbaNames[(Int32)entry.AnimationIndex];
      }
      if (entryIndex < list.JbaNames.Count) return list.JbaNames[(Int32)entryIndex];
      return null;
    }

    internal static ArrayList BuildTree(MphFileInfo info, String sourcePath) {
      ArrayList roots = new ArrayList();
      if (info == null) return roots;
      NodeListItem header = new NodeListItem("MPH / Morpheme animation network", (info.Is64Bit ? "64-bit MAWB v" + info.Version : "32-bit legacy") + ", " + info.Sections.Count + " sections");
      header.children.Add(new NodeListItem("File size", info.FileLength.ToString("N0", CultureInfo.InvariantCulture) + " bytes"));
      header.children.Add(new NodeListItem("Dialect", info.Is64Bit ? "64-bit (MAWB header)" : "32-bit (legacy / RED-era compatible)"));
      roots.Add(header);

      foreach (MphSection section in info.Sections) {
        NodeListItem sectionNode = new NodeListItem("Section #" + section.Index + " — " + SectionTypeName(section.Type), "type " + section.Type + ", 0x" + section.Start.ToString("X") + ", " + section.Length.ToString("N0", CultureInfo.InvariantCulture) + " bytes");
        if (!String.IsNullOrEmpty(section.ParseError)) sectionNode.children.Add(new NodeListItem("Parse warning", section.ParseError));
        if (section.Network != null) BuildNetworkTree(sectionNode, section.Network);
        else if (section.AnimationList != null) BuildAnimationTree(sectionNode, section.AnimationList, sourcePath);
        else if (section.Rig != null) BuildRigTree(sectionNode, section.Rig);
        else if (section.RigMap != null) BuildRigMapTree(sectionNode, section.RigMap);
        else if (section.EventTrack != null) BuildEventTree(sectionNode, section.EventTrack);
        else sectionNode.children.Add(new NodeListItem("Data", "Unknown/opaque section; raw bytes remain available via extraction."));
        roots.Add(sectionNode);
      }
      return roots;
    }

    private static void BuildNetworkTree(NodeListItem root, MphNetwork network) {
      root.children.Add(new NodeListItem("AnimationList section", "#" + network.AnimationLibrarySectionIndex));
      root.children.Add(new NodeListItem("Node count", network.Nodes.Count));
      Int32 edgeCount = network.Nodes.Sum(n => n.Flow.Count + n.Control.Count);
      root.children.Add(new NodeListItem("Decoded connections", edgeCount));

      NodeListItem nodes = new NodeListItem("Network nodes", network.Nodes.Count + " nodes");
      foreach (MphNode node in network.Nodes) {
        String nodeName = String.IsNullOrWhiteSpace(node.Name) ? "" : " — " + node.Name;
        NodeListItem n = new NodeListItem("#" + node.Index + " " + NodeTypeName(node.Type) + nodeName, "type " + node.Type);
        n.children.Add(new NodeListItem("Payload", "0x" + node.PayloadStart.ToString("X") + "–0x" + node.PayloadEnd.ToString("X")));
        foreach (KeyValuePair<String, String> field in node.Fields) n.children.Add(new NodeListItem(field.Key, field.Value));
        if (node.Flow.Count > 0) n.children.Add(new NodeListItem("Flow sources", String.Join(", ", node.Flow.Select(x => "#" + x))));
        if (node.Control.Count > 0) n.children.Add(new NodeListItem("Control sources", String.Join(", ", node.Control.Select(x => "#" + x))));
        nodes.children.Add(n);
      }
      root.children.Add(nodes);

      if (network.ParameterStrings.Count > 0) {
        NodeListItem parameters = new NodeListItem("Parameter strings", network.ParameterStrings.Count + " strings");
        for (Int32 i = 0; i < network.ParameterStrings.Count; i++) parameters.children.Add(new NodeListItem("#" + i, network.ParameterStrings[i]));
        root.children.Add(parameters);
      }
    }

    private static void BuildAnimationTree(NodeListItem root, MphAnimationList list, String sourcePath) {
      root.children.Add(new NodeListItem("Animation sets", list.Sets.Count));
      NodeListItem sets = new NodeListItem("Animation sets", list.Sets.Count + " sets");
      for (Int32 s = 0; s < list.Sets.Count; s++) {
        MphAnimationSet set = list.Sets[s];
        NodeListItem setNode = new NodeListItem("Set #" + s, set.Entries.Count + " entries, rig section #" + set.JointSectionIndex);
        setNode.children.Add(new NodeListItem("Joint / rig section", "#" + set.JointSectionIndex));
        setNode.children.Add(new NodeListItem("Compiler word (start)", "0x" + set.UnknownStart.ToString("X8")));
        setNode.children.Add(new NodeListItem("Compiler word (final)", "0x" + set.UnknownFinal.ToString("X8")));
        for (Int32 i = 0; i < set.Entries.Count; i++) {
          MphAnimationEntry entry = set.Entries[i];
          String name = entry.AnimationIndex < list.JbaNames.Count ? list.JbaNames[(Int32)entry.AnimationIndex] : null;
          String label = "#" + i + " " + (String.IsNullOrWhiteSpace(name) ? "animation-id " + entry.AnimationIndex : name);
          NodeListItem e = new NodeListItem(label, "map #" + entry.BoneMappingIndex);
          e.children.Add(new NodeListItem("Animation string id", entry.AnimationIndex));
          e.children.Add(new NodeListItem("RigToAnimMap section", "#" + entry.BoneMappingIndex));
          e.children.Add(new NodeListItem("Flag", entry.Flag));
          if (entry.DiscreteEvents.Count > 0) e.children.Add(new NodeListItem("Discrete events", String.Join(", ", entry.DiscreteEvents)));
          if (!String.IsNullOrWhiteSpace(name)) e.children.Add(new NodeListItem("JBA candidate", BuildSiblingPath(sourcePath, name + ".jba")));
          setNode.children.Add(e);
        }
        sets.children.Add(setNode);
      }
      root.children.Add(sets);

      List<(Int32 id, String value)> namedJbas = SparseStrings(list.JbaNames);
      if (namedJbas.Count > 0) {
        NodeListItem names = new NodeListItem("JBA string table", namedJbas.Count + " named slots");
        foreach ((Int32 id, String value) item in namedJbas) names.children.Add(new NodeListItem("#" + item.id, item.value));
        root.children.Add(names);
      }
      List<(Int32 id, String value)> tags = SparseStrings(list.Tags);
      if (tags.Count > 0) {
        NodeListItem tagNode = new NodeListItem("Tags / parameters", tags.Count + " named slots");
        foreach ((Int32 id, String value) item in tags) tagNode.children.Add(new NodeListItem("#" + item.id, item.value));
        root.children.Add(tagNode);
      }
    }

    private static void BuildRigTree(NodeListItem root, MphRig rig) {
      root.children.Add(new NodeListItem("Trajectory bone", "#" + rig.TrajectoryBoneIndex + ResolveBoneName(rig, rig.TrajectoryBoneIndex)));
      root.children.Add(new NodeListItem("Character root bone", "#" + rig.CharacterRootBoneIndex + ResolveBoneName(rig, rig.CharacterRootBoneIndex)));
      NodeListItem bones = new NodeListItem("Bones", rig.Bones.Count + " bones");
      for (Int32 i = 0; i < rig.Bones.Count; i++) {
        MphBone bone = rig.Bones[i];
        String parent = bone.Parent < 0 ? "none" : "#" + bone.Parent + ResolveBoneName(rig, (UInt32)bone.Parent);
        NodeListItem b = new NodeListItem("#" + i + " " + bone.Name, "parent " + parent);
        b.children.Add(new NodeListItem("Parent", parent));
        b.children.Add(new NodeListItem("Rotation quaternion", FormatQuat(bone.Qx, bone.Qy, bone.Qz, bone.Qw)));
        b.children.Add(new NodeListItem("Translation", FormatVector(bone.X, bone.Y, bone.Z)));
        bones.children.Add(b);
      }
      root.children.Add(bones);
    }

    private static void BuildRigMapTree(NodeListItem root, MphRigMap map) {
      NodeListItem entries = new NodeListItem("Channel mappings", map.Entries.Count + " entries");
      foreach (MphRigMapEntry entry in map.Entries.OrderBy(x => x.AnimationChannel))
        entries.children.Add(new NodeListItem("Animation channel #" + entry.AnimationChannel, "rig bone #" + entry.RigChannel));
      root.children.Add(entries);
    }

    private static void BuildEventTree(NodeListItem root, MphEventTrack track) {
      root.children.Add(new NodeListItem("Track type", track.TrackType));
      root.children.Add(new NodeListItem("Track name/raw pointer", "0x" + track.TrackNameRaw.ToString("X8")));
      root.children.Add(new NodeListItem("User data", track.UserData));
      root.children.Add(new NodeListItem("Runtime track id", track.RuntimeTrackId));
      NodeListItem events = new NodeListItem("Events", track.Events.Count + " events");
      for (Int32 i = 0; i < track.Events.Count; i++) {
        MphEvent e = track.Events[i];
        events.children.Add(new NodeListItem("#" + i + " user type " + e.UserType,
          e.Start.ToString("0.#####", CultureInfo.InvariantCulture) + " + " + e.Duration.ToString("0.#####", CultureInfo.InvariantCulture)));
      }
      root.children.Add(events);
    }

    private static List<String> ReadStringTable(BinaryReader br, Int64 start, Int64 sectionEnd, Boolean is64) {
      EnsureRange(start, is64 ? 32 : 20, sectionEnd, "MPH string table header");
      UInt32 numStrings = ReadUInt32At(br, start);
      UInt32 stringDataLength = ReadUInt32At(br, start + 4);
      if (numStrings > 100000) throw new InvalidDataException("String table has an unreasonable string count: " + numStrings + ".");
      UInt32 indicesOffset = ReadUInt32At(br, start + 8);
      UInt32 offsetsOffset = ReadUInt32At(br, start + (is64 ? 16 : 12));
      UInt32 stringsOffset = ReadUInt32At(br, start + (is64 ? 24 : 16));
      Int64 indices = start + indicesOffset;
      Int64 offsets = start + offsetsOffset;
      Int64 strings = start + stringsOffset;
      EnsureRange(indices, numStrings * 4L, sectionEnd, "MPH string-table indices");
      EnsureRange(offsets, numStrings * 4L, sectionEnd, "MPH string-table offsets");
      EnsureRange(strings, stringDataLength, sectionEnd, "MPH string data");

      const Int32 maxSparseIndex = 1000000;
      List<String> result = new List<String>();
      for (Int32 i = 0; i < numStrings; i++) {
        Int32 id = ReadInt32At(br, indices + i * 4L);
        UInt32 off = ReadUInt32At(br, offsets + i * 4L);
        if (id < 0 || id > maxSparseIndex || off >= stringDataLength) continue;
        while (result.Count <= id) result.Add(null);
        String value = ReadCStringBounded(br, strings + off, strings + stringDataLength);
        if (String.IsNullOrEmpty(result[id])) result[id] = value;
        else result[id] += " + " + value;
      }
      return result;
    }

    private static List<(Int32 id, String value)> SparseStrings(List<String> input) {
      List<(Int32, String)> result = new List<(Int32, String)>();
      if (input == null) return result;
      for (Int32 i = 0; i < input.Count; i++) if (!String.IsNullOrWhiteSpace(input[i])) result.Add((i, input[i]));
      return result;
    }

    private static String BuildSiblingPath(String sourcePath, String fileName) {
      if (String.IsNullOrWhiteSpace(sourcePath)) return fileName;
      String p = sourcePath.Replace('\\', '/');
      Int32 slash = p.LastIndexOf('/');
      return slash >= 0 ? p.Substring(0, slash + 1) + fileName : fileName;
    }

    private static String ResolveBoneName(MphRig rig, UInt32 index) {
      return index < rig.Bones.Count && !String.IsNullOrWhiteSpace(rig.Bones[(Int32)index].Name) ? " " + rig.Bones[(Int32)index].Name : String.Empty;
    }

    internal static String SectionTypeName(UInt32 type) {
      switch (type) {
        case 0: return "Network";
        case 1: return "Animations";
        case 2: return "Rig";
        case 3: return "RigToAnimMap";
        case 4: return "DiscreteEvents";
        default: return "Unknown";
      }
    }

    private static String NodeTypeName(UInt32 type) {
      switch (type) {
        case 10: return "StateMachine";
        case 20: return "ControlParameter";
        case 101: return "Blend2MatchEvents";
        case 102: return "BlendNMatchEvents";
        case 104: return "AnimSource";
        case 107: return "Blend2";
        case 108: return "BlendN";
        case 109: return "SingleFrame";
        case 111: return "Operator2";
        case 112: return "OperatorConst";
        case 198: return "MultiplexNoEvents";
        case 199: return "Multiplex";
        case 401: return "TransitMatchEvents";
        case 402: return "Transit";
        default: return "Type" + type;
      }
    }

    private static String ArithmeticOperationName(UInt32 op) {
      switch (op) {
        case 0: return "multiply";
        case 1: return "add";
        case 2: return "divide";
        case 3: return "subtract";
        case 4: return "min";
        case 5: return "max";
        case 6: return "multiply-add";
        default: return op.ToString(CultureInfo.InvariantCulture);
      }
    }

    private static String FormatNodeRef(UInt32 index) { return "#" + index.ToString(CultureInfo.InvariantCulture); }
    private static String FormatVector(Single x, Single y, Single z) { return String.Format(CultureInfo.InvariantCulture, "({0:0.#######}, {1:0.#######}, {2:0.#######})", x, y, z); }
    private static String FormatQuat(Single x, Single y, Single z, Single w) { return String.Format(CultureInfo.InvariantCulture, "({0:0.#######}, {1:0.#######}, {2:0.#######}, {3:0.#######})", x, y, z, w); }

    private static void AddNodeRef(List<UInt32> list, UInt32 value, UInt32 count, List<UInt32> exclude = null) {
      if (value >= count || list.Contains(value) || (exclude != null && exclude.Contains(value))) return;
      list.Add(value);
    }

    private static Int64 Align16(Int64 value) { return (value + 15L) & ~15L; }
    private static Int64 Align64Section(Int64 value) { return ((value - 8L + 15L) & ~15L) + 8L; }

    private static void EnsureRange(Int64 start, Int64 count, Int64 end, String label) {
      if (start < 0 || count < 0 || start > end - count) throw new EndOfStreamException(label + " points outside its MPH section.");
    }

    private static UInt16 ReadUInt16At(BinaryReader br, Int64 pos) {
      Int64 old = br.BaseStream.Position; br.BaseStream.Position = pos; UInt16 value = br.ReadUInt16(); br.BaseStream.Position = old; return value;
    }
    private static UInt32 ReadUInt32At(BinaryReader br, Int64 pos) {
      Int64 old = br.BaseStream.Position; br.BaseStream.Position = pos; UInt32 value = br.ReadUInt32(); br.BaseStream.Position = old; return value;
    }
    private static Int32 ReadInt32At(BinaryReader br, Int64 pos) {
      Int64 old = br.BaseStream.Position; br.BaseStream.Position = pos; Int32 value = br.ReadInt32(); br.BaseStream.Position = old; return value;
    }
    private static Single ReadSingleAt(BinaryReader br, Int64 pos) {
      Int64 old = br.BaseStream.Position; br.BaseStream.Position = pos; Single value = br.ReadSingle(); br.BaseStream.Position = old; return value;
    }
    private static UInt32 ReadUInt32AtChecked(BinaryReader br, Int64 pos, Int64 end) { EnsureRange(pos, 4, end, "u32"); return ReadUInt32At(br, pos); }
    private static Int32 ReadInt32AtChecked(BinaryReader br, Int64 pos, Int64 end) { EnsureRange(pos, 4, end, "i32"); return ReadInt32At(br, pos); }
    private static Single ReadSingleAtChecked(BinaryReader br, Int64 pos, Int64 end) { EnsureRange(pos, 4, end, "f32"); return ReadSingleAt(br, pos); }

    private static String ReadCStringBounded(BinaryReader br, Int64 pos, Int64 end) {
      if (pos < 0 || pos >= end) return String.Empty;
      Int64 old = br.BaseStream.Position;
      br.BaseStream.Position = pos;
      List<Byte> bytes = new List<Byte>();
      while (br.BaseStream.Position < end) {
        Byte b = br.ReadByte();
        if (b == 0) break;
        bytes.Add(b);
      }
      br.BaseStream.Position = old;
      return Encoding.UTF8.GetString(bytes.ToArray());
    }
  }
}
