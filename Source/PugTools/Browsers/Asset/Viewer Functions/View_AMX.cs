using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace PugTools {
  /// <summary>
  /// Structured reader for SWTOR's *.mph.amx AnimShare sidecars. This mirrors
  /// the layout used by Jedipedia's reader while keeping the data presentation
  /// native to PugTools' TreeListView.
  /// </summary>
  internal static class ViewAMX {
    private const UInt32 Magic = 0x20584D41; // "AMX "

    internal sealed class AmxFileInfo {
      internal Byte Version;
      internal readonly List<AmxAnimationEntry> Animations = new List<AmxAnimationEntry>();
      internal readonly List<List<AmxBone>> BoneLists = new List<List<AmxBone>>();
      internal Int64 FileLength;
    }

    internal sealed class AmxAnimationEntry {
      internal Byte Flags;
      internal String Animation = String.Empty;
      internal String BodyType = String.Empty;
      internal Int32 BoneList = -1;
    }

    internal sealed class AmxBone {
      internal String Name = String.Empty;
      internal Single Qx;
      internal Single Qy;
      internal Single Qz;
      internal Single Qw;
      internal Single X;
      internal Single Y;
      internal Single Z;
    }

    internal static AmxFileInfo Parse(BinaryReader br) {
      if (br == null) throw new ArgumentNullException(nameof(br));
      Stream stream = br.BaseStream;
      if (stream == null || !stream.CanSeek) throw new InvalidDataException("AMX stream is not seekable.");
      stream.Position = 0;
      if (stream.Length < 10) throw new InvalidDataException("AMX file is too small.");

      UInt32 magic = br.ReadUInt32();
      if (magic != Magic) throw new InvalidDataException("Expected AMX magic, got 0x" + magic.ToString("X8") + ".");

      Byte version = br.ReadByte();
      if (version != 1) throw new InvalidDataException("Unsupported AMX version " + version + ".");

      AmxFileInfo info = new AmxFileInfo {
        Version = version,
        FileLength = stream.Length
      };

      while (true) {
        EnsureAvailable(stream, 1);
        Byte flags = br.ReadByte();
        if (flags == 0) break;
        if (flags != 2 && flags != 3)
          throw new InvalidDataException("Unexpected AMX record flags " + flags + " at 0x" + (stream.Position - 1).ToString("X") + ".");

        AmxAnimationEntry entry = new AmxAnimationEntry { Flags = flags };
        if ((flags & 2) != 0) {
          entry.Animation = ReadByteString(br);
          entry.BodyType = ReadByteString(br);
        }
        if ((flags & 1) != 0) {
          EnsureAvailable(stream, 4);
          UInt32 value = br.ReadUInt32();
          if (value > Int32.MaxValue) throw new InvalidDataException("AMX bone-list index is too large: " + value + ".");
          entry.BoneList = (Int32)value;
        }
        info.Animations.Add(entry);
      }

      EnsureAvailable(stream, 4);
      UInt32 numBoneLists = br.ReadUInt32();
      if (numBoneLists > 100000) throw new InvalidDataException("AMX has an unreasonable bone-list count: " + numBoneLists + ".");

      for (UInt32 listIndex = 0; listIndex < numBoneLists; listIndex++) {
        EnsureAvailable(stream, 4);
        UInt32 numBones = br.ReadUInt32();
        if (numBones > 16384) throw new InvalidDataException("AMX bone list #" + listIndex + " has an unreasonable bone count: " + numBones + ".");

        List<AmxBone> list = new List<AmxBone>(checked((Int32)numBones));
        for (UInt32 boneIndex = 0; boneIndex < numBones; boneIndex++) {
          String name = ReadByteString(br);
          EnsureAvailable(stream, 7 * 4L);
          list.Add(new AmxBone {
            Name = name,
            Qx = br.ReadSingle(),
            Qy = br.ReadSingle(),
            Qz = br.ReadSingle(),
            Qw = br.ReadSingle(),
            X = br.ReadSingle(),
            Y = br.ReadSingle(),
            Z = br.ReadSingle()
          });
        }
        info.BoneLists.Add(list);
      }

      if (stream.Position != stream.Length)
        throw new InvalidDataException("AMX parser stopped at 0x" + stream.Position.ToString("X") + " of 0x" + stream.Length.ToString("X") + ".");
      return info;
    }

    internal static ArrayList BuildTree(AmxFileInfo info, String sourcePath) {
      ArrayList roots = new ArrayList();
      if (info == null) return roots;

      NodeListItem header = new NodeListItem("AMX / AnimShare metadata", "version " + info.Version + ", " + info.FileLength.ToString("N0", CultureInfo.InvariantCulture) + " bytes");
      header.children.Add(new NodeListItem("Animations", info.Animations.Count));
      header.children.Add(new NodeListItem("Bone lists", info.BoneLists.Count));
      roots.Add(header);

      NodeListItem animations = new NodeListItem("Animation mappings", info.Animations.Count + " records");
      for (Int32 i = 0; i < info.Animations.Count; i++) {
        AmxAnimationEntry entry = info.Animations[i];
        String title = "#" + i + " " + (String.IsNullOrWhiteSpace(entry.Animation) ? "(unnamed)" : entry.Animation);
        NodeListItem row = new NodeListItem(title, entry.BodyType);
        row.children.Add(new NodeListItem("Flags", "0x" + entry.Flags.ToString("X2")));
        row.children.Add(new NodeListItem("Animation", entry.Animation));
        row.children.Add(new NodeListItem("Body type / folder", entry.BodyType));
        row.children.Add(new NodeListItem("Bone list", entry.BoneList >= 0 ? entry.BoneList.ToString(CultureInfo.InvariantCulture) : "none"));
        String candidate = BuildJbaPath(entry, sourcePath);
        if (!String.IsNullOrEmpty(candidate)) row.children.Add(new NodeListItem("JBA candidate", candidate));
        animations.children.Add(row);
      }
      roots.Add(animations);

      NodeListItem boneLists = new NodeListItem("Bone lists", info.BoneLists.Count + " lists");
      for (Int32 listIndex = 0; listIndex < info.BoneLists.Count; listIndex++) {
        List<AmxBone> list = info.BoneLists[listIndex];
        NodeListItem listNode = new NodeListItem("Bone list #" + listIndex, list.Count + " bones");
        for (Int32 boneIndex = 0; boneIndex < list.Count; boneIndex++) {
          AmxBone bone = list[boneIndex];
          NodeListItem boneNode = new NodeListItem("#" + boneIndex + " " + bone.Name, FormatVector(bone.X, bone.Y, bone.Z));
          boneNode.children.Add(new NodeListItem("Name", bone.Name));
          boneNode.children.Add(new NodeListItem("Rotation quaternion", FormatQuat(bone.Qx, bone.Qy, bone.Qz, bone.Qw)));
          boneNode.children.Add(new NodeListItem("Translation", FormatVector(bone.X, bone.Y, bone.Z)));
          listNode.children.Add(boneNode);
        }
        boneLists.children.Add(listNode);
      }
      roots.Add(boneLists);
      return roots;
    }

    private static String BuildJbaPath(AmxAnimationEntry entry, String sourcePath) {
      if (entry == null || String.IsNullOrWhiteSpace(entry.Animation)) return null;
      String body = (entry.BodyType ?? String.Empty).Replace('\\', '/').Trim('/');
      if (!String.IsNullOrEmpty(body)) return "/resources/anim/" + body + "/" + entry.Animation + ".jba";
      if (!String.IsNullOrWhiteSpace(sourcePath)) {
        String normalized = sourcePath.Replace('\\', '/');
        Int32 slash = normalized.LastIndexOf('/');
        if (slash >= 0) return normalized.Substring(0, slash + 1) + entry.Animation + ".jba";
      }
      return entry.Animation + ".jba";
    }

    private static String FormatVector(Single x, Single y, Single z) {
      return String.Format(CultureInfo.InvariantCulture, "({0:0.#######}, {1:0.#######}, {2:0.#######})", x, y, z);
    }

    private static String FormatQuat(Single x, Single y, Single z, Single w) {
      return String.Format(CultureInfo.InvariantCulture, "({0:0.#######}, {1:0.#######}, {2:0.#######}, {3:0.#######})", x, y, z, w);
    }

    private static String ReadByteString(BinaryReader br) {
      EnsureAvailable(br.BaseStream, 1);
      Int32 length = br.ReadByte();
      EnsureAvailable(br.BaseStream, length);
      if (length == 0) return String.Empty;
      Byte[] bytes = br.ReadBytes(length);
      return Encoding.ASCII.GetString(bytes);
    }

    private static void EnsureAvailable(Stream stream, Int64 count) {
      if (count < 0 || stream.Position < 0 || stream.Position > stream.Length - count)
        throw new EndOfStreamException("AMX record points outside the stream.");
    }
  }
}
