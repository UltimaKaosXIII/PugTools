using System;
using System.Collections.Generic;
using System.IO;
using Buffer = SlimDX.Direct3D11.Buffer;

namespace FileFormats {
  public class GR2_Mesh {
    public GR2 parent;
    public UInt32 bitFlag2;
    // SWTOR BWAG stores a signed Granny LOD class in the low 16 bits immediately after the mesh-name pointer.
    // Jedipedia maps -1/-2/-3 to collision/portal/occlusion and non-negative values to visual LOD levels.
    public Int16 lod;
    public Int32 LodLevel => lod >= 0 ? lod : 0;
    public UInt32 vertexSize;
    public Buffer idxBuffer;
    public List<GR2_Mesh_Bone> meshBones;
    public String meshName;
    public List<GR2_Mesh_Piece> meshPieces;
    public List<GR2_Mesh_Vertex_Index> meshVertIndex;
    public List<GR2_Mesh_Vertex> meshVerts;
    public UInt16 numBones;
    public UInt16 numPieces;
    public UInt32 numVertIndex;
    public UInt32 numVerts;
    public UInt64 offsetMeshBones;
    public UInt64 offsetMeshName;
    public UInt64 offsetMeshPieces;
    public UInt64 offsetMeshVertIndex;
    public UInt64 offsetMeshVerts;
    public Buffer vertBuffer;

    public GR2_Mesh(BinaryReader br, Boolean is64Bit) {
      meshBones = new List<GR2_Mesh_Bone>();
      meshPieces = new List<GR2_Mesh_Piece>();
      meshVertIndex = new List<GR2_Mesh_Vertex_Index>();
      meshVerts = new List<GR2_Mesh_Vertex>();

      offsetMeshName = is64Bit ? br.ReadUInt64() : br.ReadUInt32();
      lod = br.ReadInt16();
      br.ReadUInt16(); // remaining mesh-header flags/padding
      numPieces = br.ReadUInt16();
      numBones = br.ReadUInt16();

      if (is64Bit) {
        // BWAG v5 keeps the same 16-bit flag and vertex-size values as v4, but pads each to 32 bits.
        // Reading the padding as the high half (the old PugTools behavior) can manufacture an absurd stride on
        // assets whose padding is non-zero. This matches Jedipedia's v5 header walk exactly.
        bitFlag2 = br.ReadUInt16();
        br.ReadUInt16();
        vertexSize = br.ReadUInt16();
        br.ReadUInt16();
      } else {
        bitFlag2 = br.ReadUInt16();
        vertexSize = br.ReadUInt16();
      }

      numVerts = br.ReadUInt32();
      numVertIndex = br.ReadUInt32();

      offsetMeshVerts = is64Bit ? br.ReadUInt64() : br.ReadUInt32();
      offsetMeshPieces = is64Bit ? br.ReadUInt64() : br.ReadUInt32();
      offsetMeshVertIndex = is64Bit ? br.ReadUInt64() : br.ReadUInt32();
      offsetMeshBones = is64Bit ? br.ReadUInt64() : br.ReadUInt32();

      meshName = FileHelpers.ReadString(br, offsetMeshName);
    }
  }
}
