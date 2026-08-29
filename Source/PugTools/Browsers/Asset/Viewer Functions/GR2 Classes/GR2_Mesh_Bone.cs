using System;
using System.IO;

namespace FileFormats {
  public class GR2_Mesh_Bone {
    public String boneName = "";
    public Single maxX = 0;
    public Single maxY = 0;
    public Single maxZ = 0;
    public Single minX = 0;
    public Single minY = 0;
    public Single minZ = 0;
    public UInt64 offsetName = 0;

    public GR2_Mesh_Bone() { }

    public GR2_Mesh_Bone(BinaryReader br, Boolean is64Bit) {
      offsetName = is64Bit ? br.ReadUInt64() : br.ReadUInt32();
      minX = br.ReadSingle();
      minY = br.ReadSingle();
      minZ = br.ReadSingle();
      maxX = br.ReadSingle();
      maxY = br.ReadSingle();
      maxZ = br.ReadSingle();
      boneName = FileHelpers.ReadString(br, offsetName);
    }

    public override string ToString() {
      return boneName;
    }
  }
}
