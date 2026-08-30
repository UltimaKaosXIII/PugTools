using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace PugTools {
  /// <summary>
  /// Reader for SWTOR's BNRY/LTLE collision blob. The same layout is used by
  /// standalone *.collision files and by collision data appended to GR2/SPT.
  /// Marker 4 is the beta/32-bit layout; marker 5 is the later 64-bit layout.
  /// </summary>
  internal static class ViewCollision {
    internal sealed class BoundingBox {
      internal readonly Single[] Min = new Single[3];
      internal readonly Single[] Max = new Single[3];
    }

    internal sealed class BihNode {
      internal UInt32 Parent;
      internal UInt32 Axis;
      internal Int32 LeftLeafTriangleCount;
      internal UInt32 Left;
      internal Int32 RightLeafTriangleCount;
      internal UInt32 Right;
      internal Single LeftMaxPlane;
      internal Single RightMinPlane;
    }

    internal sealed class PieceSummary {
      internal UInt32 Count1;
      internal UInt32 TriangleCount;
      internal BoundingBox Bounds;
      internal UInt32 Count2;
      internal UInt32 Flags;
    }

    internal sealed class Triangle {
      internal Byte Fields;
      internal Byte Index1;
      internal Byte Index2;
      internal Byte Index3;
      internal Byte Edge1;
      internal Byte Edge2;
      internal Byte Edge3;
      internal Byte? Extra;
    }

    internal sealed class Piece {
      internal UInt16 TriangleCount;
      internal UInt16 TriangleBytes;
      internal readonly List<Single[]> Vertices = new List<Single[]>();
      internal readonly List<Triangle> Triangles = new List<Triangle>();
    }

    internal sealed class BnryInfo {
      internal UInt32 Length;
      internal UInt32 Marker;
      internal UInt32 UnknownNumber;
      internal UInt32 TriangleCount;
      internal BoundingBox Bounds;
      internal readonly List<BihNode> BihNodes = new List<BihNode>();
      internal readonly List<PieceSummary> PieceSummaries = new List<PieceSummary>();
      internal readonly List<Piece> Pieces = new List<Piece>();
      internal Int64 EndPosition;
      internal Boolean Is64Bit => Marker == 5;
    }

    internal sealed class CollisionInfo {
      internal BnryInfo Bnry;
      internal UInt32 UnknownMaterialCount;
      internal readonly List<String> Materials = new List<String>();
      internal BoundingBox CollisionBounds;
      internal BoundingBox TotalBounds;
      internal UInt32 EgcdVersion;
      internal UInt32 EgcdOffset;
    }

    internal static BnryInfo ParseBnry(BinaryReader br, Int64 startPosition) {
      if (br == null) throw new ArgumentNullException(nameof(br));
      Stream stream = br.BaseStream;
      EnsureAvailable(stream, startPosition, 4);
      stream.Position = startPosition;
      UInt32 length = br.ReadUInt32();
      if (length == 0) return null;
      if (length < 16) throw new InvalidDataException("BNRY section is too small: " + length + ".");
      Int64 end = checked(startPosition + length);
      if (end > stream.Length) throw new InvalidDataException("BNRY section points beyond the end of the file.");

      Expect(br.ReadUInt32(), 0x59524E42u, "BNRY magic"); // BNRY
      UInt32 version = ReadUInt32BigEndian(br);
      Expect(version, 2u, "BNRY version");
      Expect(br.ReadUInt32(), 0x454C544Cu, "LTLE magic"); // LTLE
      Expect(br.ReadUInt32(), 1u, "BNRY constant 1");
      Expect(br.ReadUInt32(), 2u, "BNRY constant 2");

      UInt32 numPieces = br.ReadUInt32();
      if (numPieces >= 4096) throw new InvalidDataException("Collision has an unreasonable piece count: " + numPieces + ".");
      br.ReadUInt32(); // runtime alternate-layout byte size
      UInt32 numBihNodes = br.ReadUInt32();
      UInt32 numTriangles = br.ReadUInt32();
      Expect(br.ReadUInt32(), 1u, "collision header constant");
      BoundingBox bounds = ReadHomogeneousBoundingBox(br);

      br.ReadUInt32();
      br.ReadUInt32();
      UInt32 marker = br.ReadUInt32();
      if (marker != 4 && marker != 5) throw new InvalidDataException("Unsupported BNRY layout marker " + marker + ".");
      Boolean is64 = marker == 5;
      br.ReadUInt32();
      br.ReadUInt32();

      BnryInfo info = new BnryInfo {
        Length = length,
        Marker = marker,
        UnknownNumber = br.ReadUInt32(),
        TriangleCount = numTriangles,
        Bounds = bounds,
        EndPosition = end
      };

      Expect(br.ReadUInt32(), numTriangles, "triangle count repeat 1");
      Expect(br.ReadUInt32(), 1u, "triangle-count constant");
      ReadAndCheckBox(br, bounds, "bounding box repeat 1");

      if (is64) Expect(br.ReadUInt32(), 0u, "64-bit collision leading zero");
      Expect(br.ReadUInt32(), numPieces, "piece count repeat 1");
      Expect(br.ReadUInt32(), numPieces, "piece count repeat 2");
      Expect(br.ReadUInt32(), numTriangles, "triangle count repeat 2");
      Expect(br.ReadUInt32(), numTriangles, "triangle count repeat 3");
      Expect(br.ReadUInt32(), 0u, "collision zero");
      if (!is64) br.ReadUInt32();
      Expect(br.ReadUInt32(), 16u, "collision constant 16");
      Expect(br.ReadUInt32(), 0x00800000u, "collision 0x00800000 constant");
      Expect(br.ReadByte(), (Byte)1, "collision byte constant");
      Expect(br.ReadUInt32(), 1u, "collision constant after byte");
      Expect(br.ReadUInt32(), numBihNodes, "BIH node count repeat");
      Expect(br.ReadUInt32(), numTriangles, "triangle count repeat 4");
      Expect(br.ReadUInt32(), 1u, "collision pre-bounds constant");
      ReadAndCheckBox(br, bounds, "bounding box repeat 2");
      Expect(br.ReadUInt32(), 1u, "collision pre-BIH constant");

      for (UInt32 i = 0; i < numBihNodes; i++) {
        BihNode node = new BihNode {
          Parent = br.ReadUInt32(),
          Axis = br.ReadUInt32()
        };
        if (node.Axis > 2) throw new InvalidDataException("BIH node #" + i + " has invalid axis " + node.Axis + ".");
        Expect(br.ReadUInt32(), 1u, "BIH node constant");
        node.LeftLeafTriangleCount = br.ReadInt32();
        node.Left = br.ReadUInt32();
        node.RightLeafTriangleCount = br.ReadInt32();
        node.Right = br.ReadUInt32();
        node.LeftMaxPlane = br.ReadSingle();
        node.RightMinPlane = br.ReadSingle();
        info.BihNodes.Add(node);
      }

      Expect(br.ReadUInt32(), is64 ? numPieces : 0u, "post-BIH word 0");
      Expect(br.ReadUInt32(), 1u, "post-BIH word 1");

      if (is64) {
        for (UInt32 i = 0; i < numPieces; i++) {
          PieceSummary summary = new PieceSummary {
            Count1 = br.ReadUInt32(),
            TriangleCount = br.ReadUInt32()
          };
          Expect(br.ReadUInt32(), 1u, "piece summary constant 1");
          Expect(br.ReadUInt32(), 1u, "piece summary constant 2");
          BoundingBox bb = new BoundingBox();
          bb.Min[0] = br.ReadSingle(); bb.Min[1] = br.ReadSingle(); bb.Min[2] = br.ReadSingle();
          Expect(br.ReadUInt32(), 1u, "piece summary middle constant");
          bb.Max[0] = br.ReadSingle(); bb.Max[1] = br.ReadSingle(); bb.Max[2] = br.ReadSingle();
          summary.Bounds = bb;
          summary.Count2 = br.ReadUInt32();
          summary.Flags = br.ReadUInt32();
          info.PieceSummaries.Add(summary);
        }
      }

      for (UInt32 i = 0; i < numPieces; i++) br.ReadUInt32(); // runtime-layout piece offsets

      UInt64 triangleTotal = 0;
      for (UInt32 pieceIndex = 0; pieceIndex < numPieces; pieceIndex++) {
        Piece piece = new Piece {
          TriangleCount = br.ReadUInt16(),
          TriangleBytes = br.ReadUInt16()
        };
        UInt16 numVertices = br.ReadUInt16();
        if (numVertices > 256) throw new InvalidDataException("Collision piece #" + pieceIndex + " has more than 256 vertices.");
        Expect(br.ReadUInt16(), numVertices, "vertex count repeat 1");
        br.ReadUInt16();
        Expect(br.ReadByte(), (Byte)0, "piece alignment byte");
        Expect(br.ReadUInt16(), numVertices, "vertex count repeat 2");
        Expect(br.ReadUInt32(), 1u, "piece vertex constant");

        for (Int32 v = 0; v < numVertices; v++)
          piece.Vertices.Add(new[] { br.ReadSingle(), br.ReadSingle(), br.ReadSingle() });

        Expect(br.ReadUInt32(), 1u, "piece triangle constant");
        Int64 triangleStart = stream.Position;
        for (Int32 t = 0; t < piece.TriangleCount; t++) {
          Byte fields = br.ReadByte();
          Triangle tri = new Triangle { Fields = fields };
          if ((fields & 0x01) != 0) {
            tri.Index1 = br.ReadByte(); tri.Index2 = br.ReadByte(); tri.Index3 = br.ReadByte();
            if (tri.Index1 >= numVertices || tri.Index2 >= numVertices || tri.Index3 >= numVertices)
              throw new InvalidDataException("Collision triangle references a vertex outside piece #" + pieceIndex + ".");
          }
          if ((fields & 0x20) != 0) {
            tri.Edge1 = br.ReadByte(); tri.Edge2 = br.ReadByte(); tri.Edge3 = br.ReadByte();
          }
          if ((fields & 0x80) != 0) tri.Extra = br.ReadByte();
          piece.Triangles.Add(tri);

          if ((fields & ~0xA1) != 0) {
            // Unknown optional fields: the on-disk byte count still lets us skip safely.
            stream.Position = triangleStart + piece.TriangleBytes;
            break;
          }
        }
        if (stream.Position != triangleStart + piece.TriangleBytes)
          throw new InvalidDataException("Collision piece #" + pieceIndex + " triangle byte count does not match its header.");
        if (is64 && info.PieceSummaries[(Int32)pieceIndex].TriangleCount != piece.TriangleCount)
          throw new InvalidDataException("Collision piece #" + pieceIndex + " summary triangle count mismatch.");
        triangleTotal += piece.TriangleCount;
        info.Pieces.Add(piece);
      }

      if (triangleTotal != numTriangles)
        throw new InvalidDataException("Collision piece triangle total " + triangleTotal + " does not match header " + numTriangles + ".");
      if (stream.Position != end)
        throw new InvalidDataException("BNRY parser stopped at 0x" + stream.Position.ToString("X") + " instead of 0x" + end.ToString("X") + ".");
      return info;
    }

    internal static CollisionInfo ParseStandalone(Stream input) {
      if (input == null) throw new ArgumentNullException(nameof(input));
      input.Position = 0;
      using BinaryReader br = new BinaryReader(input, Encoding.UTF8, true);
      CollisionInfo info = new CollisionInfo();
      info.Bnry = ParseBnry(br, 0);
      if (info.Bnry != null) {
        info.UnknownMaterialCount = br.ReadUInt32();
        if (info.UnknownMaterialCount < 1 || info.UnknownMaterialCount > 8)
          throw new InvalidDataException("Unexpected collision material pre-count " + info.UnknownMaterialCount + ".");
      }
      UInt32 materialCount = br.ReadUInt32();
      if (materialCount > 4096) throw new InvalidDataException("Unreasonable collision material count " + materialCount + ".");
      for (UInt32 i = 0; i < materialCount; i++) info.Materials.Add(ReadString32(br));
      info.CollisionBounds = ReadPlainBoundingBox(br);
      info.TotalBounds = ReadPlainBoundingBox(br);
      ReadEgcdFooter(br, 0, out UInt32 version, out UInt32 offset);
      info.EgcdVersion = version;
      info.EgcdOffset = offset;
      if (input.Position != input.Length)
        throw new InvalidDataException("Collision parser stopped with " + (input.Length - input.Position) + " bytes remaining.");
      return info;
    }

    internal static void ReadEgcdFooter(BinaryReader br, Int64 expectedOffset, out UInt32 version, out UInt32 offset) {
      UInt32 first = br.ReadUInt32();
      UInt32 magic;
      if (first == 0) magic = br.ReadUInt32();
      else magic = first;
      Expect(magic, 0x44434745u, "EGCD magic");
      version = br.ReadUInt32();
      if (version != 4 && version != 5) throw new InvalidDataException("Unsupported EGCD version " + version + ".");
      offset = br.ReadUInt32();
      if (offset != expectedOffset)
        throw new InvalidDataException("EGCD offset 0x" + offset.ToString("X") + " does not match expected 0x" + expectedOffset.ToString("X") + ".");
    }

    internal static ArrayList BuildTree(CollisionInfo info) {
      ArrayList roots = new ArrayList();
      if (info == null) return roots;
      NodeListItem header = new NodeListItem("Collision", info.Bnry == null ? "no BNRY geometry" : (info.Bnry.Is64Bit ? "64-bit BNRY/LTLE" : "beta/32-bit BNRY/LTLE"));
      header.children.Add(new NodeListItem("EGCD version", info.EgcdVersion));
      header.children.Add(new NodeListItem("EGCD offset", "0x" + info.EgcdOffset.ToString("X")));
      header.children.Add(new NodeListItem("Collision bounds", FormatBox(info.CollisionBounds)));
      header.children.Add(new NodeListItem("Total bounds", FormatBox(info.TotalBounds)));
      roots.Add(header);

      if (info.Materials.Count > 0) {
        NodeListItem materials = new NodeListItem("Materials", info.Materials.Count + " entries");
        for (Int32 i = 0; i < info.Materials.Count; i++) materials.children.Add(new NodeListItem("#" + i, info.Materials[i]));
        roots.Add(materials);
      }
      if (info.Bnry != null) roots.Add(BuildBnryNode(info.Bnry));
      return roots;
    }

    internal static NodeListItem BuildBnryNode(BnryInfo bnry) {
      NodeListItem root = new NodeListItem("BNRY / LTLE geometry", bnry.Is64Bit ? "marker 5 (64-bit)" : "marker 4 (beta/32-bit)");
      root.children.Add(new NodeListItem("Section length", bnry.Length.ToString("N0", CultureInfo.InvariantCulture) + " bytes"));
      root.children.Add(new NodeListItem("Unknown number", bnry.UnknownNumber));
      root.children.Add(new NodeListItem("Triangles", bnry.TriangleCount));
      root.children.Add(new NodeListItem("Bounds", FormatBox(bnry.Bounds)));

      NodeListItem bih = new NodeListItem("Bounding Interval Hierarchy", bnry.BihNodes.Count + " nodes");
      for (Int32 i = 0; i < bnry.BihNodes.Count; i++) {
        BihNode n = bnry.BihNodes[i];
        NodeListItem row = new NodeListItem("#" + i + " axis " + AxisName(n.Axis), String.Format(CultureInfo.InvariantCulture, "planes {0:0.#####} / {1:0.#####}", n.LeftMaxPlane, n.RightMinPlane));
        row.children.Add(new NodeListItem("Parent", n.Parent));
        row.children.Add(new NodeListItem("Left", FormatChild(n.LeftLeafTriangleCount, n.Left)));
        row.children.Add(new NodeListItem("Right", FormatChild(n.RightLeafTriangleCount, n.Right)));
        bih.children.Add(row);
      }
      root.children.Add(bih);

      NodeListItem pieces = new NodeListItem("Convex pieces", bnry.Pieces.Count + " pieces");
      for (Int32 i = 0; i < bnry.Pieces.Count; i++) {
        Piece p = bnry.Pieces[i];
        NodeListItem row = new NodeListItem("Piece #" + i, p.Vertices.Count + " vertices, " + p.TriangleCount + " triangles");
        if (i < bnry.PieceSummaries.Count) {
          PieceSummary s = bnry.PieceSummaries[i];
          row.children.Add(new NodeListItem("Summary bounds", FormatBox(s.Bounds)));
          row.children.Add(new NodeListItem("Summary flags", "0x" + s.Flags.ToString("X")));
          row.children.Add(new NodeListItem("Summary counts", s.Count1 + " / " + s.Count2));
        }
        NodeListItem verts = new NodeListItem("Vertices", p.Vertices.Count + " entries");
        for (Int32 v = 0; v < p.Vertices.Count; v++) verts.children.Add(new NodeListItem("#" + v, FormatVector(p.Vertices[v])));
        row.children.Add(verts);
        NodeListItem tris = new NodeListItem("Triangles", p.Triangles.Count + " entries");
        for (Int32 t = 0; t < p.Triangles.Count; t++) {
          Triangle tri = p.Triangles[t];
          tris.children.Add(new NodeListItem("#" + t,
            tri.Index1 + ", " + tri.Index2 + ", " + tri.Index3 + "  flags 0x" + tri.Fields.ToString("X2")));
        }
        row.children.Add(tris);
        pieces.children.Add(row);
      }
      root.children.Add(pieces);
      return root;
    }

    internal static BoundingBox ReadPlainBoundingBox(BinaryReader br) {
      BoundingBox box = new BoundingBox();
      box.Min[0] = br.ReadSingle(); box.Min[1] = br.ReadSingle(); box.Min[2] = br.ReadSingle();
      box.Max[0] = br.ReadSingle(); box.Max[1] = br.ReadSingle(); box.Max[2] = br.ReadSingle();
      return box;
    }

    internal static String ReadString32(BinaryReader br) {
      UInt32 length = br.ReadUInt32();
      if (length > Int32.MaxValue || br.BaseStream.Position + length > br.BaseStream.Length)
        throw new InvalidDataException("String length points outside collision/SPT stream.");
      return Encoding.UTF8.GetString(br.ReadBytes((Int32)length));
    }

    private static BoundingBox ReadHomogeneousBoundingBox(BinaryReader br) {
      Expect(br.ReadUInt32(), 1u, "bounding-box min marker");
      BoundingBox box = new BoundingBox();
      box.Min[0] = br.ReadSingle(); box.Min[1] = br.ReadSingle(); box.Min[2] = br.ReadSingle();
      Expect(br.ReadUInt32(), 1u, "bounding-box max marker");
      box.Max[0] = br.ReadSingle(); box.Max[1] = br.ReadSingle(); box.Max[2] = br.ReadSingle();
      return box;
    }

    private static void ReadAndCheckBox(BinaryReader br, BoundingBox expected, String label) {
      BoundingBox actual = ReadHomogeneousBoundingBox(br);
      for (Int32 i = 0; i < 3; i++) {
        if (actual.Min[i] != expected.Min[i] || actual.Max[i] != expected.Max[i])
          throw new InvalidDataException(label + " does not match the original collision bounds.");
      }
    }

    private static UInt32 ReadUInt32BigEndian(BinaryReader br) {
      Byte[] b = br.ReadBytes(4);
      if (b.Length != 4) throw new EndOfStreamException();
      return ((UInt32)b[0] << 24) | ((UInt32)b[1] << 16) | ((UInt32)b[2] << 8) | b[3];
    }

    private static void EnsureAvailable(Stream stream, Int64 position, Int64 bytes) {
      if (!stream.CanSeek || position < 0 || bytes < 0 || position > stream.Length - bytes)
        throw new EndOfStreamException("Collision data points outside the stream.");
    }

    private static void Expect(UInt32 actual, UInt32 expected, String label) {
      if (actual != expected) throw new InvalidDataException(label + ": expected 0x" + expected.ToString("X") + ", got 0x" + actual.ToString("X") + ".");
    }
    private static void Expect(UInt16 actual, UInt16 expected, String label) {
      if (actual != expected) throw new InvalidDataException(label + ": expected " + expected + ", got " + actual + ".");
    }
    private static void Expect(Byte actual, Byte expected, String label) {
      if (actual != expected) throw new InvalidDataException(label + ": expected " + expected + ", got " + actual + ".");
    }
    private static String AxisName(UInt32 axis) => axis == 0 ? "X" : axis == 1 ? "Y" : "Z";
    private static String FormatChild(Int32 count, UInt32 value) => count < 0 ? "node #" + value : count + " triangles @ byte " + value;
    private static String FormatVector(Single[] v) => String.Format(CultureInfo.InvariantCulture, "({0:0.#####}, {1:0.#####}, {2:0.#####})", v[0], v[1], v[2]);
    internal static String FormatBox(BoundingBox b) => b == null ? "(none)" : FormatVector(b.Min) + " – " + FormatVector(b.Max);
  }
}
