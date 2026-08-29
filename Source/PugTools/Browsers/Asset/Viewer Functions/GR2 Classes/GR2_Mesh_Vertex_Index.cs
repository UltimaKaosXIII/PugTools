using System;
using System.IO;

namespace FileFormats {
  public class GR2_Mesh_Vertex_Index {
    public UInt16 index;

    public GR2_Mesh_Vertex_Index() { }
    public GR2_Mesh_Vertex_Index(UInt16 value) { index = value; }

    public GR2_Mesh_Vertex_Index(BinaryReader br) {
      index = br.ReadUInt16();
    }
  }
}
