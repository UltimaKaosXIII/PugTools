using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;

namespace PugTools {
  /// <summary>
  /// Structured reader for both historical XML .clo cloth assets and the
  /// binary OLCB versions 1-3 used by later SWTOR clients.
  /// </summary>
  internal static class ViewCLO {
    private const UInt32 OlcbMagic = 0x42434C4F; // "OLCB"

    internal sealed class CloInfo {
      internal Boolean IsBinary;
      internal UInt32 Version;
      internal String FormatLabel;
      internal Single GravityX;
      internal Single GravityY;
      internal Single GravityZ;
      internal Single BoundingSphereRadiusSq;
      internal readonly List<CloBone> Bones = new List<CloBone>();
      internal readonly List<CloParticle> Particles = new List<CloParticle>();
      internal readonly List<CloEdge> Edges = new List<CloEdge>();
      internal readonly List<CloCollider> Colliders = new List<CloCollider>();
      internal readonly List<CloTriangle> Triangles = new List<CloTriangle>();
    }

    internal sealed class CloBone {
      internal String Name = String.Empty;
      internal String Parent = String.Empty;
      internal Single[] BoneToParentRot = new Single[4];
      internal Single[] BoneToParentTrans = new Single[3];
      internal Single[] RootToBoneRot = new Single[4];
      internal Single[] RootToBoneTrans = new Single[3];
      internal Single[] RestEdgeDirection = new Single[4];
      internal Int32 StartParticle = -1;
      internal Int32 EndParticle = -1;
    }

    internal sealed class CloParticle {
      internal String Name = String.Empty;
      internal String DrivenBone = String.Empty;
      internal Single Damping;
      internal Single MovementForceFactor;
      internal Single InvertedSimWeight;
      internal UInt32 ColliderBitflag;
      internal Single? Radius;
    }

    internal sealed class CloEdge {
      internal String Name;
      internal UInt32 Node1;
      internal UInt32 Node2;
      internal Single RestLength;
      internal Single MaxLength;
      internal Single? MinLength;
      internal Single SpringStrength;
      internal UInt32 Movement;
    }

    internal sealed class CloCollider {
      internal String Parent = String.Empty;
      internal UInt32 Class;
      internal Single[] Rotation = new Single[4];
      internal Single[] Position = new Single[3];
      internal Single Radius;
      internal Single Height;
      internal Single Friction;
    }

    internal sealed class CloTriangle {
      internal UInt32 Index1;
      internal UInt32 Index2;
      internal UInt32 Index3;
      internal UInt32 ColliderBitflag;
    }

    internal static CloInfo Parse(Stream input) {
      if (input == null) throw new ArgumentNullException(nameof(input));
      using MemoryStream copy = new MemoryStream();
      input.Position = 0;
      input.CopyTo(copy);
      Byte[] bytes = copy.ToArray();
      if (bytes.Length < 4) throw new InvalidDataException("CLO file is too small.");
      if (BitConverter.ToUInt32(bytes, 0) == OlcbMagic) return ParseBinary(bytes, true);
      return ParseText(bytes);
    }

    private static CloInfo ParseBinary(Byte[] bytes, Boolean allowRepair) {
      using MemoryStream ms = new MemoryStream(bytes, false);
      using BinaryReader br = new BinaryReader(ms, Encoding.UTF8, true);
      if (br.ReadUInt32() != OlcbMagic) throw new InvalidDataException("Expected OLCB binary CLO magic.");
      UInt32 version = br.ReadUInt32();
      if (version < 1 || version > 3) throw new InvalidDataException("Unsupported binary CLO version " + version + ".");
      UInt32 payloadOffset = br.ReadUInt32();
      UInt32 payloadLength = br.ReadUInt32();
      if (payloadOffset != 0x10) throw new InvalidDataException("Unexpected CLO payload offset 0x" + payloadOffset.ToString("X") + ".");

      Int64 expectedLength = payloadOffset + (Int64)payloadLength;
      if (bytes.LongLength != expectedLength) {
        Int64 surplus = bytes.LongLength - expectedLength;
        if (allowRepair && surplus > 0) {
          Byte[] repaired = RepairCrLfExpansion(bytes);
          if (repaired.LongLength == expectedLength) return ParseBinary(repaired, false);
        }
        throw new InvalidDataException("CLO payload length says " + expectedLength + " bytes, file has " + bytes.LongLength + ".");
      }

      CloInfo info = new CloInfo {
        IsBinary = true,
        Version = version,
        FormatLabel = "Binary OLCB v" + version,
        GravityX = ReadSingleAt(br, 0x10),
        GravityY = ReadSingleAt(br, 0x14),
        GravityZ = ReadSingleAt(br, 0x18),
        BoundingSphereRadiusSq = ReadSingleAt(br, 0x24)
      };

      CloOffsets offsets = ReadOffsets(br, version, bytes.LongLength);
      List<String> strings = new List<String>();
      for (UInt32 i = 0; i < offsets.StringsCount; i++) {
        Int64 pos = 0x10L + offsets.StringsOffset + i * 0x20L;
        EnsureRange(pos, 0x20, bytes.LongLength, "CLO string");
        strings.Add(ReadFixedString(br, pos, 0x20));
      }

      for (UInt32 i = 0; i < offsets.BonesCount; i++) {
        Int64 pos = 0x10L + offsets.BonesOffset + i * 0x60L;
        EnsureRange(pos, 0x60, bytes.LongLength, "CLO bone");
        Int32 nameIndex = ReadInt32At(br, pos + 0x58);
        Int32 parentIndex = ReadInt32At(br, pos + 0x5C);
        CloBone bone = new CloBone {
          Name = Lookup(strings, nameIndex),
          Parent = Lookup(strings, parentIndex),
          BoneToParentRot = ReadFloatArray(br, pos, 4),
          BoneToParentTrans = ReadFloatArray(br, pos + 0x10, 3),
          RootToBoneRot = ReadFloatArray(br, pos + 0x20, 4),
          RootToBoneTrans = ReadFloatArray(br, pos + 0x30, 3),
          RestEdgeDirection = ReadFloatArray(br, pos + 0x40, 4),
          StartParticle = ReadInt32At(br, pos + 0x50),
          EndParticle = ReadInt32At(br, pos + 0x54)
        };
        info.Bones.Add(bone);
      }

      for (UInt32 i = 0; i < offsets.ParticlesCount; i++) {
        Int64 pos = 0x10L + offsets.ParticlesOffset + i * 0x24L;
        EnsureRange(pos, 0x24, bytes.LongLength, "CLO particle");
        Int32 drivenBoneIndex = ReadInt32At(br, pos + 8);
        String driven = Lookup(strings, drivenBoneIndex);
        info.Particles.Add(new CloParticle {
          Name = i < info.Bones.Count ? info.Bones[(Int32)i].Name : driven,
          DrivenBone = driven,
          Damping = ReadSingleAt(br, pos),
          MovementForceFactor = ReadSingleAt(br, pos + 4),
          InvertedSimWeight = ReadSingleAt(br, pos + 0x0C),
          ColliderBitflag = ReadUInt32At(br, pos + 0x14)
        });
      }

      Int32 particleDataStride = version == 3 ? 0x28 : 0x1C;
      for (UInt32 i = 0; i < offsets.ParticleDataCount; i++) {
        Int64 pos = 0x10L + offsets.ParticleDataOffset + i * (Int64)particleDataStride;
        EnsureRange(pos, particleDataStride, bytes.LongLength, "CLO particle data");
        UInt32 particleIndex = ReadUInt32At(br, pos);
        if (particleIndex < info.Particles.Count) info.Particles[(Int32)particleIndex].Radius = ReadSingleAt(br, pos + 8);
      }

      Int32 edgeStride = version > 1 ? 0x1C : 0x18;
      for (UInt32 i = 0; i < offsets.EdgesCount; i++) {
        Int64 pos = 0x10L + offsets.EdgesOffset + i * (Int64)edgeStride;
        EnsureRange(pos, edgeStride, bytes.LongLength, "CLO edge");
        CloEdge edge = new CloEdge {
          Node1 = ReadUInt32At(br, pos),
          Node2 = ReadUInt32At(br, pos + 4),
          RestLength = ReadSingleAt(br, pos + 8),
          MaxLength = ReadSingleAt(br, pos + 0x0C),
          SpringStrength = ReadSingleAt(br, pos + 0x10),
          Movement = ReadUInt32At(br, pos + 0x14)
        };
        if (version > 1) edge.MinLength = ReadSingleAt(br, pos + 0x18);
        info.Edges.Add(edge);
      }

      for (UInt32 i = 0; i < offsets.CollidersCount; i++) {
        Int64 pos = 0x10L + offsets.CollidersOffset + i * 0x40L;
        EnsureRange(pos, 0x40, bytes.LongLength, "CLO collider");
        UInt32 parentIndex = ReadUInt32At(br, pos + 0x20);
        info.Colliders.Add(new CloCollider {
          Rotation = ReadFloatArray(br, pos, 4),
          Position = ReadFloatArray(br, pos + 0x10, 3),
          Parent = Lookup(strings, parentIndex),
          Class = ReadUInt32At(br, pos + 0x28),
          Radius = ReadSingleAt(br, pos + 0x2C),
          Height = ReadSingleAt(br, pos + 0x30),
          Friction = ReadSingleAt(br, pos + 0x34)
        });
      }

      for (UInt32 i = 0; i < offsets.TrianglesCount; i++) {
        Int64 pos = 0x10L + offsets.TrianglesOffset + i * 0x10L;
        EnsureRange(pos, 0x10, bytes.LongLength, "CLO triangle");
        info.Triangles.Add(new CloTriangle {
          Index1 = ReadUInt32At(br, pos),
          Index2 = ReadUInt32At(br, pos + 4),
          Index3 = ReadUInt32At(br, pos + 8),
          ColliderBitflag = ReadUInt32At(br, pos + 0x0C)
        });
      }
      return info;
    }

    private static CloInfo ParseText(Byte[] bytes) {
      String text = Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF', '\0', ' ', '\t', '\r', '\n');
      XmlDocument doc = new XmlDocument();
      doc.LoadXml(text);
      XmlElement root = doc.DocumentElement;
      if (root == null || !String.Equals(root.Name, "ClothData", StringComparison.OrdinalIgnoreCase))
        throw new InvalidDataException("Expected <ClothData> root in text CLO.");

      CloInfo info = new CloInfo { IsBinary = false, Version = 1, FormatLabel = "Legacy XML/text CLO" };
      Single[] gravity = ParseVector(ChildText(root.SelectSingleNode("Settings"), "Gravity"), 3);
      info.GravityX = gravity[0]; info.GravityY = gravity[1]; info.GravityZ = gravity[2];

      Dictionary<String, Int32> boneIndex = new Dictionary<String, Int32>(StringComparer.OrdinalIgnoreCase);
      List<String> startNames = new List<String>();
      List<String> endNames = new List<String>();
      XmlNodeList bones = root.SelectNodes("Bones/Bone");
      if (bones != null) {
        foreach (XmlNode node in bones) {
          XmlElement el = node as XmlElement;
          if (el == null) continue;
          CloBone bone = new CloBone {
            Name = el.GetAttribute("name") ?? String.Empty,
            Parent = ChildText(el, "parent") ?? String.Empty,
            BoneToParentTrans = ParseVector(ChildText(el, "m_boneToParentTrans"), 3),
            BoneToParentRot = ParseVector(ChildText(el, "m_boneToParentRot"), 4),
            RootToBoneTrans = ParseVector(ChildText(el, "m_rootToBoneTrans"), 3),
            RootToBoneRot = ParseVector(ChildText(el, "m_rootToBoneRot"), 4)
          };
          boneIndex[bone.Name] = info.Bones.Count;
          info.Bones.Add(bone);
          startNames.Add(ChildText(el, "startParticle"));
          endNames.Add(ChildText(el, "endParticle"));
        }
      }
      for (Int32 i = 0; i < info.Bones.Count; i++) {
        info.Bones[i].StartParticle = ResolveNameIndex(boneIndex, startNames[i]);
        info.Bones[i].EndParticle = ResolveNameIndex(boneIndex, endNames[i]);
      }

      Dictionary<String, CloParticle> particles = new Dictionary<String, CloParticle>(StringComparer.OrdinalIgnoreCase);
      XmlNodeList particleNodes = root.SelectNodes("Particles/Particle");
      if (particleNodes != null) {
        foreach (XmlNode node in particleNodes) {
          XmlElement el = node as XmlElement;
          if (el == null) continue;
          Single simWeight = ParseSingle(ChildText(el, "simWeight"));
          CloParticle p = new CloParticle {
            Name = el.GetAttribute("name") ?? String.Empty,
            DrivenBone = ChildText(el, "drivenBone") ?? String.Empty,
            Damping = ParseSingle(ChildText(el, "damping")),
            MovementForceFactor = ParseSingle(ChildText(el, "movementForceFactor")),
            InvertedSimWeight = 1F - simWeight,
            ColliderBitflag = 0
          };
          Single radius = ParseSingle(ChildText(el, "radius"));
          if (radius != 0F) p.Radius = radius;
          particles[p.Name] = p;
        }
      }
      foreach (CloBone bone in info.Bones) {
        if (particles.TryGetValue(bone.Name, out CloParticle p)) info.Particles.Add(p);
        else info.Particles.Add(new CloParticle { Name = bone.Name, DrivenBone = bone.Name });
      }

      Dictionary<String, CloEdge> edgesByName = new Dictionary<String, CloEdge>(StringComparer.OrdinalIgnoreCase);
      XmlNodeList edgeNodes = root.SelectNodes("Edges/Edge");
      if (edgeNodes != null) {
        foreach (XmlNode node in edgeNodes) {
          XmlElement el = node as XmlElement;
          if (el == null) continue;
          Int32 n1 = ResolveNameIndex(boneIndex, ChildText(el, "node1"));
          Int32 n2 = ResolveNameIndex(boneIndex, ChildText(el, "node2"));
          CloEdge edge = new CloEdge {
            Name = el.GetAttribute("name"),
            Node1 = n1 >= 0 ? (UInt32)n1 : UInt32.MaxValue,
            Node2 = n2 >= 0 ? (UInt32)n2 : UInt32.MaxValue,
            RestLength = ParseSingle(ChildText(el, "restLength")) / 1000F,
            MaxLength = ParseSingle(ChildText(el, "maxLength")) / 1000F,
            SpringStrength = ParseSingle(ChildText(el, "springStrength"))
          };
          if (n1 >= 0 && n2 >= 0 && n1 < info.Particles.Count && n2 < info.Particles.Count)
            edge.Movement = DeriveMovement(info.Particles[n1].InvertedSimWeight, info.Particles[n2].InvertedSimWeight);
          info.Edges.Add(edge);
          if (!String.IsNullOrWhiteSpace(edge.Name)) edgesByName[edge.Name] = edge;
        }
      }

      Dictionary<String, UInt32> colliderClasses = new Dictionary<String, UInt32>(StringComparer.OrdinalIgnoreCase) {
        { "newtonsLab_plane", 0 }, { "newtonsLab_sphere", 1 }, { "newtonsLab_capsule", 2 }, { "newtonsLab_dualcapsule", 3 }
      };
      XmlNodeList colliderNodes = root.SelectNodes("Colliders/Collider");
      if (colliderNodes != null) {
        Int32 colliderIndex = 0;
        foreach (XmlNode node in colliderNodes) {
          XmlElement el = node as XmlElement;
          if (el == null) continue;
          String className = ChildText(el, "class") ?? String.Empty;
          colliderClasses.TryGetValue(className, out UInt32 cls);
          CloCollider collider = new CloCollider {
            Rotation = ParseVector(ChildText(el, "rotation"), 4),
            Position = ParseVector(ChildText(el, "position"), 3),
            Parent = ChildText(el, "parent") ?? String.Empty,
            Class = cls,
            Radius = ParseSingle(ChildText(el, "radius")) / 1000F,
            Height = ParseSingle(ChildText(el, "height")) / 1000F,
            Friction = ParseSingle(ChildText(el, "friction"))
          };
          info.Colliders.Add(collider);

          if (colliderIndex < 32) {
            XmlNodeList collisions = el.SelectNodes("collision");
            if (collisions != null) foreach (XmlNode collision in collisions) {
              String edgeName = collision.InnerText.Trim();
              if (!edgesByName.TryGetValue(edgeName, out CloEdge edge)) continue;
              UInt32 mask = 1U << colliderIndex;
              if (edge.Node1 < info.Particles.Count) info.Particles[(Int32)edge.Node1].ColliderBitflag |= mask;
              if (edge.Node2 < info.Particles.Count) info.Particles[(Int32)edge.Node2].ColliderBitflag |= mask;
            }
          }
          colliderIndex++;
        }
      }

      XmlNodeList triangles = root.SelectNodes("Triangles/Triangle");
      if (triangles != null) foreach (XmlNode node in triangles) {
        Int32 i1 = ResolveNameIndex(boneIndex, ChildText(node, "node1"));
        Int32 i2 = ResolveNameIndex(boneIndex, ChildText(node, "node2"));
        Int32 i3 = ResolveNameIndex(boneIndex, ChildText(node, "node3"));
        info.Triangles.Add(new CloTriangle {
          Index1 = i1 >= 0 ? (UInt32)i1 : UInt32.MaxValue,
          Index2 = i2 >= 0 ? (UInt32)i2 : UInt32.MaxValue,
          Index3 = i3 >= 0 ? (UInt32)i3 : UInt32.MaxValue,
          ColliderBitflag = 0
        });
      }
      return info;
    }

    internal static ArrayList BuildTree(CloInfo info) {
      ArrayList roots = new ArrayList();
      NodeListItem header = new NodeListItem("CLO / cloth simulation", info.FormatLabel);
      header.children.Add(new NodeListItem("Gravity", FormatVector(new[] { info.GravityX, info.GravityY, info.GravityZ })));
      if (info.IsBinary) {
        header.children.Add(new NodeListItem("Bounding sphere radius²", info.BoundingSphereRadiusSq.ToString("0.########", CultureInfo.InvariantCulture)));
        if (info.BoundingSphereRadiusSq >= 0F)
          header.children.Add(new NodeListItem("Bounding sphere radius", Math.Sqrt(info.BoundingSphereRadiusSq).ToString("0.########", CultureInfo.InvariantCulture)));
      }
      header.children.Add(new NodeListItem("Bones / particles", info.Bones.Count + " / " + info.Particles.Count));
      header.children.Add(new NodeListItem("Edges / triangles", info.Edges.Count + " / " + info.Triangles.Count));
      header.children.Add(new NodeListItem("Colliders", info.Colliders.Count));
      roots.Add(header);

      NodeListItem bones = new NodeListItem("Bones", info.Bones.Count + " bones");
      for (Int32 i = 0; i < info.Bones.Count; i++) {
        CloBone bone = info.Bones[i];
        NodeListItem b = new NodeListItem("#" + i + " " + bone.Name, String.IsNullOrWhiteSpace(bone.Parent) ? "root" : "parent " + bone.Parent);
        b.children.Add(new NodeListItem("Parent", String.IsNullOrWhiteSpace(bone.Parent) ? "none" : bone.Parent));
        b.children.Add(new NodeListItem("Start / end particle", bone.StartParticle + " / " + bone.EndParticle));
        b.children.Add(new NodeListItem("Bone→parent rotation", FormatVector(bone.BoneToParentRot)));
        b.children.Add(new NodeListItem("Bone→parent translation", FormatVector(bone.BoneToParentTrans)));
        b.children.Add(new NodeListItem("Root→bone rotation", FormatVector(bone.RootToBoneRot)));
        b.children.Add(new NodeListItem("Root→bone translation", FormatVector(bone.RootToBoneTrans)));
        if (bone.RestEdgeDirection.Any(v => v != 0F)) b.children.Add(new NodeListItem("Rest edge direction", FormatVector(bone.RestEdgeDirection)));
        bones.children.Add(b);
      }
      roots.Add(bones);

      NodeListItem particles = new NodeListItem("Particles", info.Particles.Count + " particles");
      for (Int32 i = 0; i < info.Particles.Count; i++) {
        CloParticle p = info.Particles[i];
        NodeListItem n = new NodeListItem("#" + i + " " + p.Name, "weight " + (1F - p.InvertedSimWeight).ToString("0.####", CultureInfo.InvariantCulture));
        n.children.Add(new NodeListItem("Driven bone", p.DrivenBone));
        n.children.Add(new NodeListItem("Damping", p.Damping.ToString("0.#######", CultureInfo.InvariantCulture)));
        n.children.Add(new NodeListItem("Movement force factor", p.MovementForceFactor.ToString("0.#######", CultureInfo.InvariantCulture)));
        n.children.Add(new NodeListItem("Sim weight", (1F - p.InvertedSimWeight).ToString("0.#######", CultureInfo.InvariantCulture)));
        n.children.Add(new NodeListItem("Collider bitflag", "0x" + p.ColliderBitflag.ToString("X8")));
        if (p.Radius.HasValue) n.children.Add(new NodeListItem("Radius", p.Radius.Value.ToString("0.########", CultureInfo.InvariantCulture)));
        particles.children.Add(n);
      }
      roots.Add(particles);

      NodeListItem edges = new NodeListItem("Edges", info.Edges.Count + " constraints");
      for (Int32 i = 0; i < info.Edges.Count; i++) {
        CloEdge e = info.Edges[i];
        String name = String.IsNullOrWhiteSpace(e.Name) ? "#" + i : "#" + i + " " + e.Name;
        NodeListItem n = new NodeListItem(name, "#" + e.Node1 + " ↔ #" + e.Node2);
        n.children.Add(new NodeListItem("Rest length", e.RestLength.ToString("0.########", CultureInfo.InvariantCulture)));
        n.children.Add(new NodeListItem("Max length", e.MaxLength.ToString("0.########", CultureInfo.InvariantCulture)));
        if (e.MinLength.HasValue) n.children.Add(new NodeListItem("Min length", e.MinLength.Value.ToString("0.########", CultureInfo.InvariantCulture)));
        n.children.Add(new NodeListItem("Spring strength", e.SpringStrength.ToString("0.####", CultureInfo.InvariantCulture)));
        n.children.Add(new NodeListItem("Movement", MovementName(e.Movement)));
        edges.children.Add(n);
      }
      roots.Add(edges);

      NodeListItem colliders = new NodeListItem("Colliders", info.Colliders.Count + " colliders");
      for (Int32 i = 0; i < info.Colliders.Count; i++) {
        CloCollider c = info.Colliders[i];
        NodeListItem n = new NodeListItem("#" + i + " " + ColliderClassName(c.Class), c.Parent);
        n.children.Add(new NodeListItem("Parent", c.Parent));
        n.children.Add(new NodeListItem("Position", FormatVector(c.Position)));
        n.children.Add(new NodeListItem("Rotation", FormatVector(c.Rotation)));
        n.children.Add(new NodeListItem("Radius / height", c.Radius.ToString("0.########", CultureInfo.InvariantCulture) + " / " + c.Height.ToString("0.########", CultureInfo.InvariantCulture)));
        n.children.Add(new NodeListItem("Friction", c.Friction.ToString("0.####", CultureInfo.InvariantCulture)));
        colliders.children.Add(n);
      }
      roots.Add(colliders);

      if (info.Triangles.Count > 0) {
        NodeListItem triangles = new NodeListItem("Triangles", info.Triangles.Count + " triangles");
        for (Int32 i = 0; i < info.Triangles.Count; i++) {
          CloTriangle t = info.Triangles[i];
          triangles.children.Add(new NodeListItem("#" + i, t.Index1 + ", " + t.Index2 + ", " + t.Index3 + " | colliders 0x" + t.ColliderBitflag.ToString("X8")));
        }
        roots.Add(triangles);
      }
      return roots;
    }

    private sealed class CloOffsets {
      internal UInt32 StringsCount, StringsOffset, BonesCount, BonesOffset, ParticlesCount, ParticlesOffset;
      internal UInt32 ParticleDataCount, ParticleDataOffset, EdgesCount, EdgesOffset, CollidersCount, CollidersOffset;
      internal UInt32 TrianglesCount, TrianglesOffset;
    }

    private static CloOffsets ReadOffsets(BinaryReader br, UInt32 version, Int64 length) {
      CloOffsets o = new CloOffsets();
      if (version < 3) {
        EnsureRange(0x38, 0x48, length, "CLO v1/v2 offset table");
        o.StringsCount = ReadUInt32At(br, 0x38); o.StringsOffset = ReadUInt32At(br, 0x3C);
        o.BonesCount = ReadUInt32At(br, 0x40); o.BonesOffset = ReadUInt32At(br, 0x44);
        o.ParticlesCount = ReadUInt32At(br, 0x48); o.ParticlesOffset = ReadUInt32At(br, 0x4C);
        o.ParticleDataCount = ReadUInt32At(br, 0x54); o.ParticleDataOffset = ReadUInt32At(br, 0x58);
        o.EdgesCount = ReadUInt32At(br, 0x5C); o.EdgesOffset = ReadUInt32At(br, 0x60);
        o.CollidersCount = ReadUInt32At(br, 0x64); o.CollidersOffset = ReadUInt32At(br, 0x68);
        o.TrianglesCount = ReadUInt32At(br, 0x74); o.TrianglesOffset = ReadUInt32At(br, 0x78);
      } else {
        EnsureRange(0x38, 0x70, length, "CLO v3 offset table");
        o.StringsCount = ReadUInt32At(br, 0x38); o.BonesCount = ReadUInt32At(br, 0x3C);
        o.ParticlesCount = ReadUInt32At(br, 0x40); o.ParticleDataCount = ReadUInt32At(br, 0x44);
        o.EdgesCount = ReadUInt32At(br, 0x48); o.CollidersCount = ReadUInt32At(br, 0x4C);
        o.TrianglesCount = ReadUInt32At(br, 0x54);
        o.StringsOffset = ReadUInt32At(br, 0x58); o.BonesOffset = ReadUInt32At(br, 0x60);
        o.ParticlesOffset = ReadUInt32At(br, 0x68); o.ParticleDataOffset = ReadUInt32At(br, 0x78);
        o.EdgesOffset = ReadUInt32At(br, 0x80); o.CollidersOffset = ReadUInt32At(br, 0x88);
        o.TrianglesOffset = ReadUInt32At(br, 0x98);
      }
      ValidateCount(o.StringsCount, "strings"); ValidateCount(o.BonesCount, "bones"); ValidateCount(o.ParticlesCount, "particles");
      ValidateCount(o.ParticleDataCount, "particle data"); ValidateCount(o.EdgesCount, "edges"); ValidateCount(o.CollidersCount, "colliders"); ValidateCount(o.TrianglesCount, "triangles");
      return o;
    }

    private static void ValidateCount(UInt32 count, String label) {
      if (count > 1000000) throw new InvalidDataException("CLO has an unreasonable " + label + " count: " + count + ".");
    }

    private static Byte[] RepairCrLfExpansion(Byte[] source) {
      using MemoryStream output = new MemoryStream(source.Length);
      for (Int32 i = 0; i < source.Length; i++) {
        if (i + 1 < source.Length && source[i] == 0x0D && source[i + 1] == 0x0A) {
          output.WriteByte(0x0A); i++;
        } else output.WriteByte(source[i]);
      }
      return output.ToArray();
    }

    private static UInt32 DeriveMovement(Single w1, Single w2) {
      if (w1 < 1F && w2 < 1F) return 2;
      if (w1 >= 1F && w2 < 1F) return 0;
      if (w1 < 1F && w2 >= 1F) return 4;
      return 5;
    }

    private static String MovementName(UInt32 movement) {
      switch (movement) {
        case 0: return "0 — node 2 moves";
        case 2: return "2 — both nodes move";
        case 4: return "4 — node 1 moves";
        case 5: return "5 — fixed";
        default: return movement.ToString(CultureInfo.InvariantCulture);
      }
    }

    private static String ColliderClassName(UInt32 cls) {
      switch (cls) {
        case 0: return "Plane";
        case 1: return "Sphere";
        case 2: return "Capsule";
        case 3: return "Dual capsule";
        default: return "Class " + cls;
      }
    }

    private static Int32 ResolveNameIndex(Dictionary<String, Int32> map, String value) {
      if (String.IsNullOrWhiteSpace(value)) return -1;
      return map.TryGetValue(value.Trim(), out Int32 index) ? index : -1;
    }

    private static String ChildText(XmlNode parent, String name) {
      return parent?.SelectSingleNode(name)?.InnerText?.Trim();
    }

    private static Single ParseSingle(String value) {
      return Single.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out Single f) ? f : 0F;
    }

    private static Single[] ParseVector(String value, Int32 count) {
      Single[] result = new Single[count];
      if (String.IsNullOrWhiteSpace(value)) return result;
      String[] parts = value.Split(',');
      for (Int32 i = 0; i < count && i < parts.Length; i++) result[i] = ParseSingle(parts[i].Trim());
      return result;
    }

    private static String Lookup(List<String> strings, Int64 index) {
      return index >= 0 && index < strings.Count ? strings[(Int32)index] : String.Empty;
    }

    private static String FormatVector(IEnumerable<Single> values) {
      return "(" + String.Join(", ", values.Select(v => v.ToString("0.#######", CultureInfo.InvariantCulture))) + ")";
    }

    private static Single[] ReadFloatArray(BinaryReader br, Int64 pos, Int32 count) {
      Single[] result = new Single[count];
      for (Int32 i = 0; i < count; i++) result[i] = ReadSingleAt(br, pos + i * 4L);
      return result;
    }

    private static String ReadFixedString(BinaryReader br, Int64 pos, Int32 length) {
      Int64 old = br.BaseStream.Position; br.BaseStream.Position = pos;
      Byte[] data = br.ReadBytes(length); br.BaseStream.Position = old;
      Int32 zero = Array.IndexOf(data, (Byte)0); if (zero >= 0) length = zero;
      return Encoding.UTF8.GetString(data, 0, length);
    }

    private static UInt32 ReadUInt32At(BinaryReader br, Int64 pos) { Int64 old = br.BaseStream.Position; br.BaseStream.Position = pos; UInt32 v = br.ReadUInt32(); br.BaseStream.Position = old; return v; }
    private static Int32 ReadInt32At(BinaryReader br, Int64 pos) { Int64 old = br.BaseStream.Position; br.BaseStream.Position = pos; Int32 v = br.ReadInt32(); br.BaseStream.Position = old; return v; }
    private static Single ReadSingleAt(BinaryReader br, Int64 pos) { Int64 old = br.BaseStream.Position; br.BaseStream.Position = pos; Single v = br.ReadSingle(); br.BaseStream.Position = old; return v; }
    private static void EnsureRange(Int64 pos, Int64 count, Int64 end, String label) { if (pos < 0 || count < 0 || pos > end - count) throw new EndOfStreamException(label + " points outside the CLO file."); }
  }
}
