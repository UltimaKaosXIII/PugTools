using System;
using System.Collections.Generic;
using System.IO;
using SlimDX;

namespace FileFormats {
  public class GR2 {
    public List<GR2> attachedModels = new List<GR2>();
    public Matrix attachMatrix;
    public List<GR2_Attachment> attachments = new List<GR2_Attachment>();
    private Boolean disposed = false;
    public Boolean enabled = true;
    public String filename;
    public String lodSchemaName = "granny_legacy_default";
    public Single[] lodThresholds = Array.Empty<Single>();
    public GR2_Bounding_Box globalBox;
    public List<GR2_Material> materials = new List<GR2_Material>();
    public List<GR2_Mesh> meshes = new List<GR2_Mesh>();
    public UInt16 numAttach;
    public UInt16 numBones;
    public UInt16 numMaterials;
    public UInt16 numMeshes;
    public Matrix parentPosMatrix;
    public Matrix parentRotMatrix;
    public Matrix positionMatrix;
    public Matrix rotationMatrix;
    public Matrix scaleMatrix;
    public List<GR2_Bone_Skeleton> skeleton_bones = new List<GR2_Bone_Skeleton>();
    public Matrix transformMatrix;

    public GR2() { }

    public GR2(BinaryReader br,
               String filename,
               Dictionary<String, GR2_Material> globalMaterials = null) {

      this.filename = filename ?? String.Empty;

      if (br == null)
        throw new ArgumentNullException(nameof(br));

      if (br.BaseStream.Length - br.BaseStream.Position < 0x20)
        throw new InvalidDataException("GR2 stream is too short.");

      UInt32 header = br.ReadUInt32();
      if (header != 0x42574147)
        throw new InvalidDataException(
          $"Invalid GR2/BWAG header 0x{header:X8} in '{this.filename}'."
        );

      UInt32 version = br.ReadUInt32();
      if (version != 4 && version != 5)
        throw new InvalidDataException($"Unsupported SWTOR GR2 version {version}.");

      Boolean is64Bit = version >= 5;

      // The LOD-schema string pointer and the bounding-box layout differ between BWAG v4 and v5.
      // v5 stores min at 0x20 and max at 0x30, while v4 stores min/max at 0x30/0x40.  The old
      // PugTools parser always started at 0x30, which made every v5 model sphere/radius garbage and in turn
      // defeated distance/frustum culling.  These offsets match Jedipedia's gr2 loader.
      Int64 schemaPointerPos = version == 5 ? 0x40 : 0x28;
      if (schemaPointerPos + 4 <= br.BaseStream.Length) {
        br.BaseStream.Seek(schemaPointerPos, SeekOrigin.Begin);
        UInt32 schemaOffset = br.ReadUInt32();
        if (schemaOffset > 0 && schemaOffset < br.BaseStream.Length) {
          String schema = FileHelpers.ReadString(br, schemaOffset);
          if (!String.IsNullOrWhiteSpace(schema)) lodSchemaName = schema;
        }
      }

      br.BaseStream.Seek(0x10, SeekOrigin.Begin);

      br.ReadUInt32(); // num50Offsets
      UInt32 type = br.ReadUInt32();
      numMeshes = br.ReadUInt16();
      numMaterials = br.ReadUInt16();
      numBones = br.ReadUInt16();
      numAttach = br.ReadUInt16();

      br.BaseStream.Seek(version == 5 ? 0x20 : 0x30, SeekOrigin.Begin);
      globalBox = new GR2_Bounding_Box(br);

      br.BaseStream.Seek(0x50, SeekOrigin.Begin);

      if (is64Bit) {
        br.ReadUInt64(); // cached offsets
        UInt64 offsetMeshHeader = br.ReadUInt64();
        UInt64 offsetMaterialName = br.ReadUInt64();
        UInt64 offsetBoneStruct = br.ReadUInt64();
        UInt64 offsetAttach = br.ReadUInt64();

        ReadSkeleton(br, type, numBones, offsetBoneStruct, true);
        ReadMeshes(br, numMeshes, offsetMeshHeader, true);
        ReadMaterials(br, numMaterials, offsetMaterialName, globalMaterials, true);
        ReadAttachments(br, numAttach, offsetAttach, true);
      } else {
        br.ReadUInt32(); // cached offsets
        UInt64 offsetMeshHeader = br.ReadUInt32();
        UInt64 offsetMaterialName = br.ReadUInt32();
        UInt64 offsetBoneStruct = br.ReadUInt32();
        UInt64 offsetAttach = br.ReadUInt32();

        ReadSkeleton(br, type, numBones, offsetBoneStruct, false);
        ReadMeshes(br, numMeshes, offsetMeshHeader, false);
        ReadMaterials(br, numMaterials, offsetMaterialName, globalMaterials, false);
        ReadAttachments(br, numAttach, offsetAttach, false);
      }
    }

    private void ReadSkeleton(BinaryReader br, UInt32 type, UInt16 count, UInt64 offset, Boolean is64Bit) {
      if (type != 2 || count == 0 || offset == 0) return;

      EnsureRange(br, offset, (UInt64)count * (UInt64)(is64Bit ? 144 : 136), "skeleton");
      br.BaseStream.Seek((Int64)offset, SeekOrigin.Begin);

      for (Int32 i = 0; i < count; i++)
        skeleton_bones.Add(new GR2_Bone_Skeleton(br, i, is64Bit));
    }

    private void ReadMeshes(BinaryReader br, UInt16 count, UInt64 offset, Boolean is64Bit) {
      if (count == 0 || offset == 0) return;

      UInt64 headerSize = (UInt64)(is64Bit ? 64 : 40);
      EnsureRange(br, offset, (UInt64)count * headerSize, "mesh headers");

      br.BaseStream.Seek((Int64)offset, SeekOrigin.Begin);

      for (Int32 i = 0; i < count; i++)
        meshes.Add(new GR2_Mesh(br, is64Bit) { parent = this });

      foreach (GR2_Mesh mesh in meshes) {
        if (mesh.numPieces > 0) {
          EnsureRange(br, mesh.offsetMeshPieces, (UInt64)mesh.numPieces * 48, "mesh pieces");
          br.BaseStream.Seek((Int64)mesh.offsetMeshPieces, SeekOrigin.Begin);

          for (Int32 i = 0; i < mesh.numPieces; i++)
            mesh.meshPieces.Add(new GR2_Mesh_Piece(br));
        }

        if (mesh.numVerts > 0) {
          if (mesh.vertexSize == 0)
            throw new InvalidDataException($"GR2 mesh '{mesh.meshName}' has a zero vertex size.");

          EnsureRange(br, mesh.offsetMeshVerts, (UInt64)mesh.numVerts * mesh.vertexSize, "vertex buffer");
          br.BaseStream.Seek((Int64)mesh.offsetMeshVerts, SeekOrigin.Begin);

          for (UInt32 i = 0; i < mesh.numVerts; i++)
            mesh.meshVerts.Add(new GR2_Mesh_Vertex(br, mesh.bitFlag2));
        }

        if (mesh.numVertIndex > 0) {
          EnsureRange(br, mesh.offsetMeshVertIndex, (UInt64)mesh.numVertIndex * 2, "index buffer");
          br.BaseStream.Seek((Int64)mesh.offsetMeshVertIndex, SeekOrigin.Begin);

          for (UInt32 i = 0; i < mesh.numVertIndex; i++)
            mesh.meshVertIndex.Add(new GR2_Mesh_Vertex_Index(br));
        }

        if (mesh.numBones > 0) {
          UInt64 boneSize = (UInt64)(is64Bit ? 32 : 28);
          EnsureRange(br, mesh.offsetMeshBones, (UInt64)mesh.numBones * boneSize, "mesh bone buffer");
          br.BaseStream.Seek((Int64)mesh.offsetMeshBones, SeekOrigin.Begin);

          for (Int32 i = 0; i < mesh.numBones; i++)
            mesh.meshBones.Add(new GR2_Mesh_Bone(br, is64Bit));
        }
      }
    }

    private void ReadMaterials(BinaryReader br,
                               UInt16 count,
                               UInt64 offset,
                               Dictionary<String, GR2_Material> globalMaterials,
                               Boolean is64Bit) {
      if (count == 0 || offset == 0) return;

      EnsureRange(br, offset, (UInt64)count * (UInt64)(is64Bit ? 8 : 4), "material table");
      br.BaseStream.Seek((Int64)offset, SeekOrigin.Begin);

      for (Int32 i = 0; i < count; i++) {
        GR2_Material material = new GR2_Material(br, is64Bit);

        if (globalMaterials != null && !globalMaterials.ContainsKey(material.materialName))
          globalMaterials.Add(material.materialName, material);

        materials.Add(material);
      }
    }

    private void ReadAttachments(BinaryReader br, UInt16 count, UInt64 offset, Boolean is64Bit) {
      if (count == 0 || offset == 0) return;

      UInt64 recordSize = (UInt64)(is64Bit ? 80 : 72);
      EnsureRange(br, offset, (UInt64)count * recordSize, "attachment table");
      br.BaseStream.Seek((Int64)offset, SeekOrigin.Begin);

      for (Int32 i = 0; i < count; i++)
        attachments.Add(new GR2_Attachment(br, is64Bit));
    }

    private static void EnsureRange(BinaryReader br, UInt64 offset, UInt64 size, String section) {
      UInt64 length = (UInt64)br.BaseStream.Length;
      if (offset > length || size > length - offset)
        throw new EndOfStreamException(
          $"GR2 {section} points outside the stream: offset 0x{offset:X}, size {size}, length {length}."
        );
    }

    ~GR2() {
      Dispose(false);
    }

    public void Dispose() {
      Dispose(true);
      GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(Boolean disposing) {
      if (disposed) return;

      if (disposing) {
        foreach (GR2 attached in attachedModels)
          attached?.Dispose();

        attachedModels.Clear();
        skeleton_bones.Clear();
        meshes.Clear();
        materials.Clear();
        attachments.Clear();
      }

      disposed = true;
    }

    public Matrix GetTransform() {
      Matrix output = Matrix.Identity;

      if (attachMatrix != new Matrix()) output *= attachMatrix;

      return output * transformMatrix;
    }
  }
}
