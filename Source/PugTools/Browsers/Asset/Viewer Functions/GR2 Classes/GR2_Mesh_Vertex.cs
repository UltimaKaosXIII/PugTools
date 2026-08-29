using System;
using System.IO;

namespace FileFormats {
  public class GR2_Mesh_Vertex {
    public Single boneIndex1;
    public Single boneIndex2;
    public Single boneIndex3;
    public Single boneIndex4;
    public Single boneWeight1;
    public Single boneWeight2;
    public Single boneWeight3;
    public Single boneWeight4;
    public Single normX;
    public Single normY;
    public Single normZ;
    public Single normW;
    public Single tanX;
    public Single tanY;
    public Single tanZ;
    public Single tanW;
    public SlimDX.Half texHalfU;
    public SlimDX.Half texHalfV;
    public Single texU;
    public Single texV;
    public Single X;
    public Single Y;
    public Single Z;

    public GR2_Mesh_Vertex() { }

    public GR2_Mesh_Vertex(BinaryReader br, UInt32 bitFlag2) {
      if ((bitFlag2 & 0x1) != 0x1) {
        throw new GR2_Vertex_Size_Exception("Invalid Vertex Size");
      } else {
        X = br.ReadSingle();
        Y = br.ReadSingle();
        Z = br.ReadSingle();
      }

      if ((bitFlag2 & 0x100) == 0x100) {
        boneWeight1 = FileHelpers.ByteToFloat(br.ReadByte());
        boneWeight2 = FileHelpers.ByteToFloat(br.ReadByte());
        boneWeight3 = FileHelpers.ByteToFloat(br.ReadByte());
        boneWeight4 = FileHelpers.ByteToFloat(br.ReadByte());
        boneIndex1 = FileHelpers.ByteToFloat(br.ReadByte());
        boneIndex2 = FileHelpers.ByteToFloat(br.ReadByte());
        boneIndex3 = FileHelpers.ByteToFloat(br.ReadByte());
        boneIndex4 = FileHelpers.ByteToFloat(br.ReadByte());
      }

      if ((bitFlag2 & 0x2) == 0x2) {
        normX = br.ReadByte(); // File_Helpers.ByteToNormal(br.ReadByte());
        normY = br.ReadByte(); // File_Helpers.ByteToNormal(br.ReadByte());
        normZ = br.ReadByte(); // File_Helpers.ByteToNormal(br.ReadByte());
        normW = br.ReadByte(); // File_Helpers.ByteToNormal(br.ReadByte());

        tanX = br.ReadByte(); // File_Helpers.ByteToNormal(br.ReadByte());
        tanY = br.ReadByte(); // File_Helpers.ByteToNormal(br.ReadByte());
        tanZ = br.ReadByte(); // File_Helpers.ByteToNormal(br.ReadByte());
        tanW = br.ReadByte(); // File_Helpers.ByteToNormal(br.ReadByte());
      }

      if ((bitFlag2 & 0x10) == 0x10) {
        br.ReadByte();
        br.ReadByte();
        br.ReadByte();
        br.ReadByte();
      }

      if ((bitFlag2 & 0x20) == 0x20) {
        texHalfU.RawValue = br.ReadUInt16();
        texHalfV.RawValue = br.ReadUInt16();
        Single[] texFloats = SlimDX.Half.ConvertToFloat(new SlimDX.Half[] { texHalfU, texHalfV });
        texU = texFloats[0];
        texV = texFloats[1];
      }

      if ((bitFlag2 & 0x40) == 0x40) {
        br.ReadByte();
        br.ReadByte();
        br.ReadByte();
        br.ReadByte();
      }

      if ((bitFlag2 & 0x80) == 0x80) {
        br.ReadByte();
        br.ReadByte();
        br.ReadByte();
        br.ReadByte();
      }
    }
  }

  public class GR2_Vertex_Size_Exception : Exception {
    public GR2_Vertex_Size_Exception(String message) : base(message) { }
  }
}
