using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using SlimDX;

namespace FileFormats {
  /// <summary>
  /// Reader for the original RAD Granny v7 GR2 container used by early SWTOR beta
  /// builds. The parser follows Jedipedia's gr2_granny-read.js: sections are
  /// decompressed, pointer fixups are applied, the Granny type graph is walked and
  /// the resulting mesh/skeleton data is converted to the normal PugTools GR2 model.
  /// </summary>
  internal static class GrannyGR2Reader {
    internal const UInt32 Magic1 = 0xC06CDE29;
    private const UInt32 Magic2 = 0x2B53A4BA;
    private const UInt32 Magic3 = 0xA5B7F525;
    private const UInt32 Magic4 = 0xEEE266F6;

    private sealed class Section {
      public UInt32 Compression;
      public UInt32 DataOffset;
      public UInt32 DataSize;
      public UInt32 ExpandedDataSize;
      public UInt32 InternalAlignment;
      public UInt32 First16Bit;
      public UInt32 First8Bit;
      public UInt32 PointerFixupOffset;
      public UInt32 PointerFixupCount;
    }

    private sealed class MemberType {
      public UInt32 Type;
      public String Name;
      public UInt32 ArraySize;
      public List<MemberType> Children;
    }

    private sealed class Parser {
      private Byte[] m_data;
      private readonly Section[] m_sections;
      private readonly Dictionary<Int32, List<MemberType>> m_typeCache = new Dictionary<Int32, List<MemberType>>();
      private readonly UInt32 m_rootTypeSection;
      private readonly UInt32 m_rootTypeOffset;
      private readonly UInt32 m_rootSection;
      private readonly UInt32 m_rootOffset;

      public Parser(Byte[] source) {
        m_data = (Byte[])source.Clone();
        if (m_data.Length < 0x1C8) throw new InvalidDataException("Granny GR2 stream is too short.");
        if (U32(0) != Magic1 || U32(4) != Magic2 || U32(8) != Magic3 || U32(12) != Magic4)
          throw new InvalidDataException("Invalid Granny v7 GR2 magic.");
        if (U32(0x14) != 0) throw new InvalidDataException("Only little-endian 32-bit Granny GR2 is supported.");
        if (U32(0x20) != 7) throw new InvalidDataException("Unsupported Granny GR2 version " + U32(0x20) + ".");

        UInt32 sectionArrayOffset = U32(0x2C);
        UInt32 sectionCount = U32(0x30);
        if (sectionCount == 0 || sectionCount > 64)
          throw new InvalidDataException("Invalid Granny section count.");
        m_rootTypeSection = U32(0x34);
        m_rootTypeOffset = U32(0x38);
        m_rootSection = U32(0x3C);
        m_rootOffset = U32(0x40);
        if (m_rootTypeSection >= sectionCount || m_rootSection >= sectionCount)
          throw new InvalidDataException("Invalid Granny root reference.");

        m_sections = new Section[checked((Int32)sectionCount)];
        Int64 p = 0x20L + sectionArrayOffset;
        for (Int32 i = 0; i < m_sections.Length; i++, p += 0x2C) {
          Ensure(p, 0x2C);
          Section s = new Section {
            Compression = U32(p),
            DataOffset = U32(p + 4),
            DataSize = U32(p + 8),
            ExpandedDataSize = U32(p + 12),
            InternalAlignment = U32(p + 16),
            First16Bit = U32(p + 20),
            First8Bit = U32(p + 24),
            PointerFixupOffset = U32(p + 28),
            PointerFixupCount = U32(p + 32)
          };
          if (s.Compression != 0 && s.Compression != 2)
            throw new InvalidDataException("Unsupported Granny section compression " + s.Compression + ".");
          m_sections[i] = s;
        }

        ExpandCompressedSections();
        ApplyPointerFixups();
      }

      public Dictionary<String, Object> ReadRoot() {
        Section typeSection = m_sections[m_rootTypeSection];
        Section rootSection = m_sections[m_rootSection];
        if (typeSection.DataSize == 0 || rootSection.DataSize == 0)
          return new Dictionary<String, Object>(StringComparer.Ordinal);

        Int32 typePos = checked((Int32)(typeSection.DataOffset + m_rootTypeOffset));
        List<MemberType> types = ReadTypes(typePos);
        Int32 rootPos = checked((Int32)(rootSection.DataOffset + m_rootOffset));
        var root = new Dictionary<String, Object>(StringComparer.Ordinal);
        ReadValues(root, types, rootPos);
        return root;
      }

      private void ExpandCompressedSections() {
        var expanded = new List<(Section section, Byte[] bytes)>();
        foreach (Section s in m_sections) {
          if (s.Compression != 2) continue;
          Ensure(s.DataOffset, s.DataSize);
          if (s.First16Bit > s.First8Bit || s.First8Bit > s.ExpandedDataSize)
            throw new InvalidDataException("Invalid Granny Oodle stop points.");
          Byte[] compressed = new Byte[checked((Int32)s.DataSize)];
          Buffer.BlockCopy(m_data, checked((Int32)s.DataOffset), compressed, 0, compressed.Length);
          expanded.Add((s, Oodle1.Decompress(compressed, s.First16Bit, s.First8Bit, s.ExpandedDataSize)));
        }
        if (expanded.Count == 0) return;

        Int32 append = m_data.Length;
        foreach (var item in expanded) {
          Int32 alignment = checked((Int32)Math.Max(1, item.section.InternalAlignment));
          append = Align(append, alignment);
          item.section.DataOffset = checked((UInt32)append);
          append = checked(append + item.bytes.Length);
        }

        Array.Resize(ref m_data, append);
        foreach (var item in expanded) {
          Buffer.BlockCopy(item.bytes, 0, m_data, checked((Int32)item.section.DataOffset), item.bytes.Length);
          item.section.DataSize = checked((UInt32)item.bytes.Length);
          item.section.ExpandedDataSize = checked((UInt32)item.bytes.Length);
          item.section.Compression = 0;
        }
      }

      private void ApplyPointerFixups() {
        for (Int32 srcSection = 0; srcSection < m_sections.Length; srcSection++) {
          Section s = m_sections[srcSection];
          Int64 pos = s.PointerFixupOffset;
          for (UInt32 i = 0; i < s.PointerFixupCount; i++, pos += 12) {
            Ensure(pos, 12);
            UInt32 srcOffset = U32(pos);
            UInt32 dstSection = U32(pos + 4);
            UInt32 dstOffset = U32(pos + 8);
            if (dstSection >= m_sections.Length) throw new InvalidDataException("Invalid Granny fixup section.");
            UInt64 src = (UInt64)s.DataOffset + srcOffset;
            UInt64 dst = (UInt64)m_sections[dstSection].DataOffset + dstOffset;
            if (src + 4 > (UInt64)m_data.Length || dst > UInt32.MaxValue)
              throw new InvalidDataException("Granny pointer fixup is out of range.");
            W32((Int32)src, (UInt32)dst);
          }
        }
      }

      private List<MemberType> ReadTypes(Int32 pos) {
        if (pos <= 0 || pos >= m_data.Length) return new List<MemberType>();
        if (m_typeCache.TryGetValue(pos, out List<MemberType> cached)) return cached;

        var result = new List<MemberType>();
        m_typeCache[pos] = result; // break recursive type cycles
        var usedNames = new HashSet<String>(StringComparer.Ordinal);
        Int32 p = pos;
        while (p + 32 <= m_data.Length) {
          UInt32 type = U32(p); p += 4;
          if (type == 0) break;
          UInt32 nameOffset = U32(p); p += 4;
          UInt32 childrenOffset = U32(p); p += 4;
          UInt32 arraySize = U32(p); p += 4;
          p += 16; // four extra fields

          String name = ReadCString(nameOffset);
          if (String.IsNullOrEmpty(name)) name = "[empty]";
          while (!usedNames.Add(name)) name += "_Copy";

          result.Add(new MemberType {
            Type = type,
            Name = name,
            ArraySize = arraySize,
            Children = childrenOffset == 0 ? null : ReadTypes(checked((Int32)childrenOffset))
          });
        }
        return result;
      }

      private Int32 ReadValues(Dictionary<String, Object> root, List<MemberType> types, Int32 pos) {
        Int32 start = pos;
        foreach (MemberType type in types) {
          if (pos < 0 || pos > m_data.Length) break;
          Int32 count = checked((Int32)Math.Max(1U, type.ArraySize));
          switch (type.Type) {
            case 0:
              break;
            case 1: { // inline
              var values = new List<Object>();
              for (Int32 i = 0; i < count; i++) {
                var child = new Dictionary<String, Object>(StringComparer.Ordinal);
                pos += ReadValues(child, type.Children ?? new List<MemberType>(), pos);
                values.Add(child);
              }
              root[type.Name] = count == 1 ? values[0] : values;
            } break;
            case 2:
            case 22: { // reference
              Ensure(pos, 4);
              UInt32 offset = U32(pos); pos += 4;
              if (offset != 0) {
                var child = new Dictionary<String, Object>(StringComparer.Ordinal);
                ReadValues(child, type.Children ?? new List<MemberType>(), checked((Int32)offset));
                root[type.Name] = child;
              }
            } break;
            case 3: { // reference to contiguous array
              Ensure(pos, 8);
              UInt32 length = U32(pos); UInt32 offset = U32(pos + 4); pos += 8;
              if (length > 0 && offset != 0) {
                var list = new List<Object>(checked((Int32)length));
                Int32 p = checked((Int32)offset);
                for (UInt32 i = 0; i < length; i++) {
                  var child = new Dictionary<String, Object>(StringComparer.Ordinal);
                  p += ReadValues(child, type.Children ?? new List<MemberType>(), p);
                  list.Add(child);
                }
                root[type.Name] = list;
              }
            } break;
            case 4: { // array of references
              Ensure(pos, 8);
              UInt32 length = U32(pos); UInt32 offsetArray = U32(pos + 4); pos += 8;
              if (length > 0 && offsetArray != 0) {
                var list = new List<Object>(checked((Int32)length));
                for (UInt32 i = 0; i < length; i++) {
                  UInt32 dataOffset = U32((Int64)offsetArray + i * 4L);
                  var child = new Dictionary<String, Object>(StringComparer.Ordinal);
                  if (dataOffset != 0) ReadValues(child, type.Children ?? new List<MemberType>(), checked((Int32)dataOffset));
                  list.Add(child);
                }
                root[type.Name] = list;
              }
            } break;
            case 5: { // variant reference
              Ensure(pos, 8);
              UInt32 typesOffset = U32(pos); UInt32 dataOffset = U32(pos + 4); pos += 8;
              if (typesOffset != 0 && dataOffset != 0) {
                var child = new Dictionary<String, Object>(StringComparer.Ordinal);
                ReadValues(child, ReadTypes(checked((Int32)typesOffset)), checked((Int32)dataOffset));
                root[type.Name] = child;
              }
            } break;
            case 7: { // reference to variant array
              Ensure(pos, 12);
              UInt32 typesOffset = U32(pos); UInt32 length = U32(pos + 4); UInt32 dataOffset = U32(pos + 8); pos += 12;
              if (typesOffset != 0 && length > 0 && dataOffset != 0) {
                List<MemberType> childTypes = ReadTypes(checked((Int32)typesOffset));
                var list = new List<Object>(checked((Int32)length));
                Int32 p = checked((Int32)dataOffset);
                for (UInt32 i = 0; i < length; i++) {
                  var child = new Dictionary<String, Object>(StringComparer.Ordinal);
                  p += ReadValues(child, childTypes, p);
                  list.Add(child);
                }
                root[type.Name] = list;
              }
            } break;
            case 8:
              root[type.Name] = ReadRepeated(count, () => { UInt32 o = U32(pos); pos += 4; return (Object)ReadCString(o); });
              break;
            case 9:
              root[type.Name] = ReadRepeated(count, () => {
                Ensure(pos, 68);
                UInt32 flags = U32(pos); pos += 4;
                var floats = new Single[16];
                for (Int32 i = 0; i < 16; i++) { floats[i] = F32(pos); pos += 4; }
                return (Object)new Dictionary<String, Object>(StringComparer.Ordinal) {
                  ["flags"] = flags,
                  ["translation"] = floats.Take(3).Cast<Object>().ToList(),
                  ["rotation"] = floats.Skip(3).Take(4).Cast<Object>().ToList(),
                  ["scaleShear"] = floats.Skip(7).Take(9).Cast<Object>().ToList()
                };
              });
              break;
            case 10:
              root[type.Name] = ReadRepeated(count, () => { Single v = F32(pos); pos += 4; return (Object)v; });
              break;
            case 11:
            case 13:
              root[type.Name] = ReadRepeated(count, () => { Ensure(pos, 1); return (Object)(SByte)m_data[pos++]; });
              break;
            case 12:
            case 14:
              root[type.Name] = ReadRepeated(count, () => { Ensure(pos, 1); return (Object)m_data[pos++]; });
              break;
            case 15:
            case 17:
              root[type.Name] = ReadRepeated(count, () => { Int16 v = I16(pos); pos += 2; return (Object)v; });
              break;
            case 16:
            case 18:
              root[type.Name] = ReadRepeated(count, () => { UInt16 v = U16(pos); pos += 2; return (Object)v; });
              break;
            case 19:
              root[type.Name] = ReadRepeated(count, () => { Int32 v = I32(pos); pos += 4; return (Object)v; });
              break;
            case 20:
              root[type.Name] = ReadRepeated(count, () => { UInt32 v = U32(pos); pos += 4; return (Object)v; });
              break;
            case 21:
              root[type.Name] = ReadRepeated(count, () => { UInt16 h = U16(pos); pos += 2; return (Object)HalfToSingle(h); });
              break;
            default:
              throw new InvalidDataException("Unsupported Granny member type " + type.Type + " (" + type.Name + ").");
          }
        }
        return pos - start;
      }

      private static Object ReadRepeated(Int32 count, Func<Object> reader) {
        if (count == 1) return reader();
        var list = new List<Object>(count);
        for (Int32 i = 0; i < count; i++) list.Add(reader());
        return list;
      }

      private String ReadCString(UInt32 offset) {
        if (offset == 0 || (UInt64)offset >= (UInt64)m_data.Length) return String.Empty;
        Int32 p = checked((Int32)offset);
        Int32 end = p;
        while (end < m_data.Length && m_data[end] != 0) end++;
        return Encoding.UTF8.GetString(m_data, p, end - p);
      }

      private UInt32 U32(Int64 p) { Ensure(p, 4); return BitConverter.ToUInt32(m_data, checked((Int32)p)); }
      private Int32 I32(Int64 p) { Ensure(p, 4); return BitConverter.ToInt32(m_data, checked((Int32)p)); }
      private UInt16 U16(Int64 p) { Ensure(p, 2); return BitConverter.ToUInt16(m_data, checked((Int32)p)); }
      private Int16 I16(Int64 p) { Ensure(p, 2); return BitConverter.ToInt16(m_data, checked((Int32)p)); }
      private Single F32(Int64 p) { Ensure(p, 4); return BitConverter.ToSingle(m_data, checked((Int32)p)); }
      private void W32(Int32 p, UInt32 v) { Byte[] b = BitConverter.GetBytes(v); Buffer.BlockCopy(b, 0, m_data, p, 4); }
      private void Ensure(Int64 p, Int64 n) { if (p < 0 || n < 0 || p > m_data.Length - n) throw new EndOfStreamException("Granny GR2 data points outside the stream."); }
    }

    internal static void Populate(GR2 target, BinaryReader br, Dictionary<String, GR2_Material> globalMaterials) {
      if (target == null) throw new ArgumentNullException(nameof(target));
      if (br == null) throw new ArgumentNullException(nameof(br));
      Int64 start = br.BaseStream.Position;
      br.BaseStream.Position = 0;
      if (br.BaseStream.Length > Int32.MaxValue) throw new InvalidDataException("Granny GR2 is too large.");
      Byte[] bytes = br.ReadBytes(checked((Int32)br.BaseStream.Length));
      br.BaseStream.Position = start;

      Dictionary<String, Object> root = new Parser(bytes).ReadRoot();
      ConvertRoot(target, root, globalMaterials);
    }

    private static void ConvertRoot(GR2 target, Dictionary<String, Object> root, Dictionary<String, GR2_Material> globalMaterials) {
      target.globalBox = new GR2_Bounding_Box();
      target.lodSchemaName = "granny_legacy_default";
      var materialIndex = new Dictionary<String, Int32>(StringComparer.OrdinalIgnoreCase);

      foreach (Dictionary<String, Object> meshObj in AsObjects(Get(root, "Meshes"))) {
        Dictionary<String, Object> vertexData = AsObject(Get(meshObj, "PrimaryVertexData"));
        Dictionary<String, Object> topology = AsObject(Get(meshObj, "PrimaryTopology"));
        List<Dictionary<String, Object>> vertices = AsObjects(Get(vertexData, "Vertices"));
        if (vertices.Count == 0 || topology == null) continue;

        GR2_Mesh mesh = new GR2_Mesh {
          parent = target,
          meshName = AsString(Get(meshObj, "Name")) ?? "mesh",
          lod = IsCollisionName(AsString(Get(meshObj, "Name"))) ? (Int16)(-1) : (Int16)0,
          numVerts = checked((UInt32)vertices.Count)
        };

        Boolean hasWeights = Has(vertices[0], "BoneWeights") && Has(vertices[0], "BoneIndices");
        Boolean uv0 = Has(vertices[0], "TextureCoordinates0");
        Boolean uv1 = Has(vertices[0], "TextureCoordinates1");
        Boolean uv2 = Has(vertices[0], "TextureCoordinates2");
        mesh.bitFlag2 = 0x1U | 0x2U | (uv0 ? 0x20U : 0U) | (hasWeights ? 0x100U : 0U) | (uv1 ? 0x40U : 0U) | (uv2 ? 0x80U : 0U);
        mesh.vertexSize = 12U + 8U + (uv0 ? 4U : 0U) + (hasWeights ? 8U : 0U) + (uv1 ? 4U : 0U) + (uv2 ? 4U : 0U);

        foreach (Dictionary<String, Object> v in vertices) {
          Single[] p = Floats(Get(v, "Position"), 3);
          Single[] n = Floats(Get(v, "Normal"), 3, new[] { 0F, 0F, 1F });
          Single[] t = Floats(Get(v, "Tangent"), 3, new[] { 1F, 0F, 0F });
          Single[] uv = Floats(Get(v, "TextureCoordinates0"), 2);
          GR2_Mesh_Vertex vertex = new GR2_Mesh_Vertex {
            X = p[0], Y = p[1], Z = p[2],
            normX = NormalByte(n[0]), normY = NormalByte(n[1]), normZ = NormalByte(n[2]), normW = 255,
            tanX = NormalByte(t[0]), tanY = NormalByte(t[1]), tanZ = NormalByte(t[2]), tanW = 255,
            texU = uv[0], texV = uv[1]
          };
          if (hasWeights) {
            Single[] w = Floats(Get(v, "BoneWeights"), 4);
            Single[] bi = Floats(Get(v, "BoneIndices"), 4);
            // Granny stores weights/indices as bytes. PugTools' in-memory BWAG
            // representation normalizes both to [0,1] and multiplies indices by
            // 255 in the skinning path, so retain that convention here.
            vertex.boneWeight1 = NormalizeByteOrUnit(w[0]); vertex.boneWeight2 = NormalizeByteOrUnit(w[1]);
            vertex.boneWeight3 = NormalizeByteOrUnit(w[2]); vertex.boneWeight4 = NormalizeByteOrUnit(w[3]);
            vertex.boneIndex1 = NormalizeIndex(bi[0]); vertex.boneIndex2 = NormalizeIndex(bi[1]);
            vertex.boneIndex3 = NormalizeIndex(bi[2]); vertex.boneIndex4 = NormalizeIndex(bi[3]);
          }
          mesh.meshVerts.Add(vertex);
          ExpandBounds(target.globalBox, p[0], p[1], p[2], mesh.meshVerts.Count == 1 && target.meshes.Count == 0);
        }

        foreach (Object idxObj in AsList(Get(topology, "Indices"))) {
          Dictionary<String, Object> idxDict = AsObject(idxObj);
          Int32 index = idxDict != null ? AsInt(Get(idxDict, "Int32")) : AsInt(idxObj);
          if (index < 0 || index > UInt16.MaxValue) throw new InvalidDataException("Granny mesh uses 32-bit indices that PugTools cannot render.");
          mesh.meshVertIndex.Add(new GR2_Mesh_Vertex_Index((UInt16)index));
        }
        mesh.numVertIndex = checked((UInt32)mesh.meshVertIndex.Count);

        List<Dictionary<String, Object>> bindings = AsObjects(Get(meshObj, "MaterialBindings"));
        Int32[] bindingToGlobal = new Int32[bindings.Count];
        for (Int32 i = 0; i < bindings.Count; i++) {
          Dictionary<String, Object> material = AsObject(Get(bindings[i], "Material"));
          String name = AsString(Get(material, "Name"));
          if (String.IsNullOrWhiteSpace(name)) name = "default";
          if (!materialIndex.TryGetValue(name, out Int32 globalIndex)) {
            globalIndex = target.materials.Count;
            materialIndex[name] = globalIndex;
            GR2_Material mat = new GR2_Material(name);
            target.materials.Add(mat);
            if (globalMaterials != null && !globalMaterials.ContainsKey(name)) globalMaterials.Add(name, mat);
          }
          bindingToGlobal[i] = globalIndex;
        }

        foreach (Dictionary<String, Object> group in AsObjects(Get(topology, "Groups"))) {
          Int32 localMat = AsInt(Get(group, "MaterialIndex"));
          mesh.meshPieces.Add(new GR2_Mesh_Piece {
            matId = localMat >= 0 && localMat < bindingToGlobal.Length ? bindingToGlobal[localMat] : -1,
            startIndex = checked((UInt32)Math.Max(0, AsInt(Get(group, "TriFirst")))),
            numPieceFaces = checked((UInt32)Math.Max(0, AsInt(Get(group, "TriCount"))))
          });
        }
        mesh.numPieces = checked((UInt16)Math.Min(UInt16.MaxValue, mesh.meshPieces.Count));

        foreach (Dictionary<String, Object> binding in AsObjects(Get(meshObj, "BoneBindings"))) {
          Single[] min = Floats(Get(binding, "OBBMin"), 3);
          Single[] max = Floats(Get(binding, "OBBMax"), 3);
          mesh.meshBones.Add(new GR2_Mesh_Bone {
            boneName = AsString(Get(binding, "BoneName")) ?? String.Empty,
            minX = min[0], minY = min[1], minZ = min[2],
            maxX = max[0], maxY = max[1], maxZ = max[2]
          });
        }
        mesh.numBones = checked((UInt16)Math.Min(UInt16.MaxValue, mesh.meshBones.Count));
        target.meshes.Add(mesh);
      }

      Int32 boneBase = 0;
      foreach (Dictionary<String, Object> skeleton in AsObjects(Get(root, "Skeletons"))) {
        List<Dictionary<String, Object>> bones = AsObjects(Get(skeleton, "Bones"));
        for (Int32 i = 0; i < bones.Count; i++) {
          Dictionary<String, Object> bone = bones[i];
          Int32 parent = AsInt(Get(bone, "ParentIndex"), -1);
          Matrix localRaw = TransformToMatrix(AsObject(Get(bone, "Transform")));
          Matrix inverseWorld = MatrixFromArray(Get(bone, "InverseWorldTransform"));
          GR2_Bone_Skeleton result = new GR2_Bone_Skeleton {
            boneIndex = target.skeleton_bones.Count,
            boneName = AsString(Get(bone, "Name")) ?? ("bone_" + target.skeleton_bones.Count),
            parentBoneIndex = parent < 0 ? -1 : parent + boneBase,
            boneToParentRaw = localRaw,
            rootToBoneRaw = inverseWorld,
            parent = localRaw,
            root = inverseWorld
          };
          result.parent.Invert();
          result.root.Invert();
          target.skeleton_bones.Add(result);
        }
        boneBase += bones.Count;
      }

      target.numMeshes = checked((UInt16)Math.Min(UInt16.MaxValue, target.meshes.Count));
      target.numMaterials = checked((UInt16)Math.Min(UInt16.MaxValue, target.materials.Count));
      target.numBones = checked((UInt16)Math.Min(UInt16.MaxValue, target.skeleton_bones.Count));
      target.numAttach = 0;
    }

    private static Matrix TransformToMatrix(Dictionary<String, Object> transform) {
      if (transform == null) return Matrix.Identity;
      Single[] tr = Floats(Get(transform, "translation"), 3);
      Single[] qr = Floats(Get(transform, "rotation"), 4, new[] { 0F, 0F, 0F, 1F });
      Single[] ss = Floats(Get(transform, "scaleShear"), 9, new[] { 1F, 0F, 0F, 0F, 1F, 0F, 0F, 0F, 1F });
      Matrix scaleShear = new Matrix {
        M11 = ss[0], M12 = ss[1], M13 = ss[2], M14 = 0,
        M21 = ss[3], M22 = ss[4], M23 = ss[5], M24 = 0,
        M31 = ss[6], M32 = ss[7], M33 = ss[8], M34 = 0,
        M41 = 0, M42 = 0, M43 = 0, M44 = 1
      };
      Quaternion q = new Quaternion(qr[0], qr[1], qr[2], qr[3]);
      if (q.LengthSquared() > 0.000001F) q = Quaternion.Normalize(q); else q = Quaternion.Identity;
      Matrix rt = Matrix.RotationQuaternion(q) * Matrix.Translation(tr[0], tr[1], tr[2]);
      return scaleShear * rt;
    }

    private static Matrix MatrixFromArray(Object value) {
      Single[] m = Floats(value, 16);
      if (m.All(v => Math.Abs(v) < 0.0000001F)) return Matrix.Identity;
      return new Matrix {
        M11=m[0], M12=m[1], M13=m[2], M14=m[3],
        M21=m[4], M22=m[5], M23=m[6], M24=m[7],
        M31=m[8], M32=m[9], M33=m[10], M34=m[11],
        M41=m[12], M42=m[13], M43=m[14], M44=m[15]
      };
    }

    private static Boolean IsCollisionName(String name) {
      if (String.IsNullOrEmpty(name)) return false;
      return name.IndexOf("collision", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static void ExpandBounds(GR2_Bounding_Box box, Single x, Single y, Single z, Boolean first) {
      if (first) { box.minX = box.maxX = x; box.minY = box.maxY = y; box.minZ = box.maxZ = z; return; }
      box.minX = Math.Min(box.minX, x); box.minY = Math.Min(box.minY, y); box.minZ = Math.Min(box.minZ, z);
      box.maxX = Math.Max(box.maxX, x); box.maxY = Math.Max(box.maxY, y); box.maxZ = Math.Max(box.maxZ, z);
    }

    private static Single NormalByte(Single v) => Math.Max(0F, Math.Min(255F, (v + 1F) * 127.5F));
    private static Single NormalizeByteOrUnit(Single v) => v > 1.0001F ? Math.Max(0F, Math.Min(255F, v)) / 255F : Math.Max(0F, Math.Min(1F, v));
    private static Single NormalizeIndex(Single v) => Math.Max(0F, Math.Min(255F, v)) / 255F;
    private static Boolean Has(Dictionary<String, Object> d, String k) => d != null && d.ContainsKey(k);
    private static Object Get(Dictionary<String, Object> d, String k) => d != null && d.TryGetValue(k, out Object v) ? v : null;
    private static Dictionary<String, Object> AsObject(Object value) => value as Dictionary<String, Object>;
    private static List<Object> AsList(Object value) => value is List<Object> list ? list : value == null ? new List<Object>() : new List<Object> { value };
    private static List<Dictionary<String, Object>> AsObjects(Object value) => AsList(value).Select(AsObject).Where(x => x != null).ToList();
    private static String AsString(Object value) => value?.ToString();
    private static Int32 AsInt(Object value, Int32 fallback = 0) { try { return value == null ? fallback : Convert.ToInt32(value); } catch { return fallback; } }
    private static Single[] Floats(Object value, Int32 count, Single[] fallback = null) {
      Single[] result = new Single[count];
      if (fallback != null) Array.Copy(fallback, result, Math.Min(count, fallback.Length));
      List<Object> list = AsList(value);
      for (Int32 i = 0; i < Math.Min(count, list.Count); i++) {
        try { result[i] = Convert.ToSingle(list[i]); } catch { }
      }
      return result;
    }

    private static Single HalfToSingle(UInt16 value) {
      UInt32 sign = (UInt32)(value >> 15) & 1;
      UInt32 exponent = (UInt32)(value >> 10) & 0x1F;
      UInt32 mantissa = (UInt32)value & 0x3FF;
      if (exponent == 0) {
        if (mantissa == 0) return sign == 0 ? 0F : -0F;
        Single v = (Single)(mantissa / 1024.0 * Math.Pow(2, -14));
        return sign == 0 ? v : -v;
      }
      if (exponent == 31) return mantissa == 0 ? (sign == 0 ? Single.PositiveInfinity : Single.NegativeInfinity) : Single.NaN;
      Single n = (Single)((1.0 + mantissa / 1024.0) * Math.Pow(2, exponent - 15));
      return sign == 0 ? n : -n;
    }

    private static Int32 Align(Int32 value, Int32 alignment) => checked((value + alignment - 1) / alignment * alignment);

    // C# port of Jedipedia/xoreos Granny Oodle1 decoder.
    private static class Oodle1 {
      private sealed class Decoder {
        private readonly Byte[] b;
        private Int32 idx;
        private UInt32 numer;
        private UInt64 denom;
        private UInt64 nextDenom;
        public Decoder(Byte[] buffer, Int32 offset) { b = buffer; idx = offset; numer = (UInt32)(b[idx] >> 1); denom = 0x80; }
        public Int32 Decode(Int32 max) {
          while (denom <= 0x800000) {
            numer = unchecked((numer << 8) | (UInt32)((b[idx] << 7) & 0x80) | (UInt32)((b[idx + 1] >> 1) & 0x7F));
            idx++;
            denom *= 256;
          }
          nextDenom = denom / (UInt32)max;
          return (Int32)Math.Min((UInt64)(numer / nextDenom), (UInt64)(max - 1));
        }
        public Int32 Commit(Int32 max, Int32 val, Int32 err) {
          numer = unchecked((UInt32)(numer - nextDenom * (UInt32)val));
          denom = val + err < max ? nextDenom * (UInt32)err : denom - nextDenom * (UInt32)val;
          return val;
        }
        public Int32 DecodeAndCommit(Int32 max) => Commit(max, Decode(max), 1);
      }

      private sealed class Window {
        private readonly Int32 countCap;
        private readonly Int32 threshWeightRebuild;
        private readonly Int32 threshIncreaseCap;
        private Int32 threshIncrease = 4;
        private Int32 threshRangeRebuild = 8;
        internal readonly List<Int32> Ranges = new List<Int32> { 0, 0x4000 };
        internal readonly List<Int32> Values = new List<Int32> { 0 };
        private readonly List<Int32> weights = new List<Int32> { 4 };
        private Int32 weightTotal = 4;
        public Window(Int32 maxValue, Int32 count) {
          countCap = count + 1;
          threshWeightRebuild = Math.Max(256, Math.Min(32 * maxValue, 15160));
          threshIncreaseCap = maxValue > 64 ? Math.Min(2 * maxValue, threshWeightRebuild / 2 - 32) : 128;
        }
        private void RebuildRanges() {
          Ranges.Clear();
          Int32 rangeWeight = (8 * 0x4000) / weightTotal;
          Int32 rangeStart = 0;
          for (Int32 i = 0; i < weights.Count; i++) { Ranges.Add(rangeStart); rangeStart += weights[i] * rangeWeight / 8; }
          Ranges.Add(0x4000);
          if (threshIncrease > threshIncreaseCap / 2) threshRangeRebuild = weightTotal + threshIncreaseCap;
          else { threshIncrease *= 2; threshRangeRebuild = weightTotal + threshIncrease; }
        }
        private void RebuildWeights() {
          for (Int32 i = 0; i < weights.Count; i++) weights[i] /= 2;
          weightTotal = weights.Sum();
          for (Int32 i = 1; i < weights.Count; i++) {
            while (i < weights.Count && weights[i] == 0) {
              Int32 last = weights.Count - 1;
              weights[i] = weights[last]; Values[i] = Values[last];
              weights.RemoveAt(last); Values.RemoveAt(last);
            }
          }
          Int32 maxIdx = -1, maxVal = -1;
          for (Int32 i = 1; i < weights.Count; i++) if (weights[i] > maxVal) { maxVal = weights[i]; maxIdx = i; }
          if (maxIdx >= 0) {
            Int32 last = weights.Count - 1;
            Int32 w = weights[maxIdx]; weights[maxIdx] = weights[last]; weights[last] = w;
            Int32 v = Values[maxIdx]; Values[maxIdx] = Values[last]; Values[last] = v;
          }
          if (weights.Count < countCap && weights[0] == 0) { weights[0] = 1; weightTotal++; }
        }
        public (Int32 ptr, Int32 value) TryDecode(Decoder dec) {
          if (weightTotal >= threshRangeRebuild) { if (threshRangeRebuild >= threshWeightRebuild) RebuildWeights(); RebuildRanges(); }
          Int32 value = dec.Decode(0x4000);
          Int32 index = UpperBound(Ranges, value) - 1;
          dec.Commit(0x4000, Ranges[index], Ranges[index + 1] - Ranges[index]);
          weights[index]++; weightTotal++;
          if (index > 0) return (-1, Values[index]);
          if (weights.Count >= Ranges.Count && dec.DecodeAndCommit(2) == 1) {
            Int32 reuseIndex = Ranges.Count + dec.DecodeAndCommit(weights.Count - Ranges.Count + 1) - 1;
            weights[reuseIndex] += 2; weightTotal += 2;
            return (-1, Values[reuseIndex]);
          }
          Values.Add(0); weights.Add(2); weightTotal += 2;
          if (weights.Count == countCap) { weightTotal -= weights[0]; weights[0] = 0; }
          return (Values.Count - 1, 0);
        }
      }

      private sealed class Dict {
        private readonly Byte[] output;
        private Int32 decodedSize;
        private Int32 backrefSize;
        private readonly Int32 decodedValueMax, backrefValueMax, lowbitValueMax, midbitValueMax, highbitValueMax;
        private readonly Window lowbitWindow, highbitWindow;
        private readonly List<Window> midbitWindows = new List<Window>();
        private readonly List<Window> decodedWindows = new List<Window>();
        private readonly List<Window> sizeWindows = new List<Window>();
        public Dict(Byte[] output, Params p) {
          this.output = output;
          decodedValueMax = p.DecodedValueMax; backrefValueMax = p.BackrefValueMax;
          lowbitValueMax = Math.Min(backrefValueMax + 1, 4);
          midbitValueMax = Math.Min(backrefValueMax / 4 + 1, 256);
          highbitValueMax = backrefValueMax / 1024 + 1;
          lowbitWindow = new Window(lowbitValueMax - 1, lowbitValueMax);
          highbitWindow = new Window(highbitValueMax - 1, p.HighbitCount + 1);
          for (Int32 i = 0; i < highbitValueMax; i++) midbitWindows.Add(new Window(midbitValueMax - 1, midbitValueMax));
          for (Int32 i = 0; i < 4; i++) decodedWindows.Add(new Window(decodedValueMax - 1, p.DecodedCount));
          for (Int32 i = 0; i < 4; i++) for (Int32 j = 0; j < 16; j++) sizeWindows.Add(new Window(64, p.SizesCount[3 - i]));
          sizeWindows.Add(new Window(64, p.SizesCount[0]));
        }
        public Int32 DecompressBlock(Decoder dec, Int32 pos) {
          Window sw = sizeWindows[backrefSize];
          var d1 = sw.TryDecode(dec); Int32 value1 = d1.value;
          if (d1.ptr >= 0) { value1 = dec.DecodeAndCommit(65); sw.Values[d1.ptr] = value1; }
          backrefSize = value1;
          if (backrefSize > 0) {
            Int32[] sizes = { 128, 192, 256, 512 };
            Int32 copySize = backrefSize < 61 ? backrefSize + 1 : sizes[backrefSize - 61];
            Int32 backrefRange = Math.Min(backrefValueMax, decodedSize);
            var d3 = lowbitWindow.TryDecode(dec); Int32 low = d3.value;
            if (d3.ptr >= 0) { low = dec.DecodeAndCommit(lowbitValueMax); lowbitWindow.Values[d3.ptr] = low; }
            var d4 = highbitWindow.TryDecode(dec); Int32 high = d4.value;
            if (d4.ptr >= 0) { high = dec.DecodeAndCommit(backrefRange / 1024 + 1); highbitWindow.Values[d4.ptr] = high; }
            Window mw = midbitWindows[high];
            var d5 = mw.TryDecode(dec); Int32 mid = d5.value;
            if (d5.ptr >= 0) { mid = dec.DecodeAndCommit(Math.Min(backrefRange / 4 + 1, 256)); mw.Values[d5.ptr] = mid; }
            Int32 back = high * 1024 + mid * 4 + low + 1;
            if (pos - back < 0) throw new InvalidDataException("Oodle1 back-reference points before output.");
            if (pos + copySize > output.Length) throw new InvalidDataException("Oodle1 output overrun.");
            for (Int32 i = 0; i < copySize; i++) output[pos + i] = output[pos - back + (i % back)];
            decodedSize += copySize;
            return copySize;
          }
          Window dw = decodedWindows[pos & 3];
          var d2 = dw.TryDecode(dec); Int32 literal = d2.value;
          if (d2.ptr >= 0) { literal = dec.DecodeAndCommit(decodedValueMax); dw.Values[d2.ptr] = literal; }
          output[pos] = (Byte)literal; decodedSize++; return 1;
        }
      }

      private sealed class Params { public Int32 DecodedValueMax, BackrefValueMax, DecodedCount, HighbitCount; public Int32[] SizesCount; }
      private static Int32 UpperBound(List<Int32> a, Int32 value) { Int32 lo=0, hi=a.Count; while(lo<hi){Int32 mid=(lo+hi)>>1;if(a[mid]<=value)lo=mid+1;else hi=mid;} return lo; }
      public static Byte[] Decompress(Byte[] compressed, UInt32 step1, UInt32 step2, UInt32 dsize) {
        if (dsize > Int32.MaxValue) throw new InvalidDataException("Oodle1 section is too large.");
        Byte[] output = new Byte[checked((Int32)dsize)];
        if (compressed.Length == 0) return output;
        if (compressed.Length < 36) throw new InvalidDataException("Oodle1 stream is too short.");
        Byte[] stream = new Byte[compressed.Length + 8]; Buffer.BlockCopy(compressed,0,stream,0,compressed.Length);
        var ps = new Params[3];
        for(Int32 i=0;i<3;i++){
          Int32 b=i*12; UInt32 w0=BitConverter.ToUInt32(stream,b), w1=BitConverter.ToUInt32(stream,b+4);
          ps[i]=new Params{DecodedValueMax=(Int32)(w0&0x1FF),BackrefValueMax=(Int32)((w0>>9)&0x7FFFFF),DecodedCount=(Int32)(w1&0x1FF),HighbitCount=(Int32)((w1>>19)&0x1FFF),SizesCount=new[]{(Int32)stream[b+8],(Int32)stream[b+9],(Int32)stream[b+10],(Int32)stream[b+11]}};
        }
        Decoder dec=new Decoder(stream,36); UInt32[] steps={step1,step2,dsize}; Int32 pos=0;
        for(Int32 i=0;i<3;i++){Dict dict=new Dict(output,ps[i]);while(pos<steps[i])pos+=dict.DecompressBlock(dec,pos);if(pos!=(Int32)steps[i])throw new InvalidDataException("Oodle1 pass overran its stop point.");}
        return output;
      }
    }
  }
}
