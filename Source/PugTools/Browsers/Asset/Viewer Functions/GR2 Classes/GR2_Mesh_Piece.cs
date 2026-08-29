using System;
using System.IO;

namespace FileFormats {
  public class GR2_Mesh_Piece {
    public Int32 matId = -1;
    public UInt32 numPieceFaces;
    public UInt32 startIndex;

    public GR2_Mesh_Piece() { }

    public GR2_Mesh_Piece(BinaryReader br/*, GR2_Mesh parent*/) {
      startIndex = br.ReadUInt32();
      numPieceFaces = br.ReadUInt32();
      matId = br.ReadInt32();
      br.ReadUInt32(); // index
      br.ReadBytes(32); // pieceBoundingBox
    }
  }
}
