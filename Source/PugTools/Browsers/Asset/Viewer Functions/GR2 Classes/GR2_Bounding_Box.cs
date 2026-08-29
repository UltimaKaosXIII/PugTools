using System;
using System.IO;

namespace FileFormats {
  public class GR2_Bounding_Box {
    public Single maxX;
    public Single maxY;
    public Single maxZ;
    public Single maxW;
    public Single minX;
    public Single minY;
    public Single minZ;
    public Single minW;

    public GR2_Bounding_Box() { }

    public GR2_Bounding_Box(BinaryReader br) {
      minX = br.ReadSingle();
      minY = br.ReadSingle();
      minZ = br.ReadSingle();
      minW = br.ReadSingle();

      maxX = br.ReadSingle();
      maxY = br.ReadSingle();
      maxZ = br.ReadSingle();
      maxW = br.ReadSingle();
    }
  }
}
