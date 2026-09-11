using System;
using System.IO;
using System.Linq;
using System.Text;

using FileFormats;

namespace PugTools {
  /// <summary>
  /// Jedipedia-compatible readers for binary payloads embedded in room DAT properties.
  /// These are deliberately kept in the Asset Browser reader as well as the World renderer:
  /// datamining often needs the structure without opening the full 3D world.
  /// </summary>
  internal static partial class View_DAT {
    private const UInt32 HeightMapVertexDataId = 0xA3AB26AEu;
    private const UInt32 WaterDepthTextureId = 0x06C671D8u;
    private const UInt32 WaterVertexDataId = 0x577AA423u;
    private const UInt32 RegionVolumeDataId = 0xFDA2965Du;
    private const Int32 EmbeddedPreviewLimit = 48;

    private static Boolean TryReadEmbeddedRoomProperty(BinaryReader br, Byte type, UInt32 id,
        String assetName, String propertyName, out NodeListItem node) {
      node = null;
      if ((type != 8 && type != 9) || !IsEmbeddedRoomProperty(id, assetName)) return false;

      EnsureRemaining(br, 4, "embedded room property length");
      UInt32 storedLength = br.ReadUInt32();
      EnsureRemaining(br, storedLength, "embedded room property payload");
      if (storedLength > Int32.MaxValue) throw new InvalidDataException("Embedded room property is too large.");
      Byte[] storedBytes = br.ReadBytes((Int32)storedLength);
      Byte[] payload = type == 8 ? AssetInstance.DecodeRoomPayloadBytes(storedBytes) : storedBytes;

      node = CreateEmbeddedRoomPropertyNode(id, propertyName, type, storedLength, payload,
        type == 8 ? "char[] / encoded payload" : "binary payload");
      return true;
    }

    private static Boolean TryCreateEmbeddedTextRoomProperty(String propertyName, String value,
        String assetName, out NodeListItem node) {
      node = null;
      if (!TryEmbeddedPropertyId(propertyName, out UInt32 id) || !IsEmbeddedRoomProperty(id, assetName))
        return false;

      String text = (value ?? String.Empty).Trim();
      if (text.Length >= 2 && text[0] == '"' && text[text.Length - 1] == '"')
        text = text.Substring(1, text.Length - 2);
      Byte[] encoded = Encoding.UTF8.GetBytes(text);
      Byte[] payload = AssetInstance.DecodeRoomPayloadBytes(encoded);
      node = CreateEmbeddedRoomPropertyNode(id, propertyName, 8, (UInt32)encoded.Length, payload,
        "text / encoded payload");
      return true;
    }

    private static NodeListItem CreateEmbeddedRoomPropertyNode(UInt32 id, String propertyName, Byte type,
        UInt32 storedLength, Byte[] payload, String storageKind) {
      String name = String.IsNullOrWhiteSpace(propertyName) ? $"0x{id:X8}" : propertyName;
      NodeListItem property = Branch($"{name} (0x{id:X8})", $"{storedLength:N0} stored bytes  [{RoomTypeName(type)}]");
      payload ??= Array.Empty<Byte>();

      String codec = DetectEmbeddedCompression(payload);
      Byte[] decoded = AssetInstance.DecompressRoomPayload(payload);
      if (decoded == null) {
        property.children.Add(Leaf("Storage", $"{storageKind}; {payload.Length:N0} decoded bytes; compression={codec}"));
        property.children.Add(Leaf("Embedded parse error", "Payload decompression failed."));
        return property;
      }

      property.children.Add(Leaf("Storage",
        $"{storageKind}; {payload.Length:N0} payload bytes; compression={codec}; {decoded.Length:N0} bytes after decompression"));
      try {
        property.children.Add(BuildEmbeddedPayloadNode(id, decoded));
      }
      catch (Exception ex) when (ex is InvalidDataException || ex is EndOfStreamException || ex is OverflowException || ex is IOException) {
        property.children.Add(Leaf("Embedded parse error", ex.Message));
      }
      return property;
    }

    private static NodeListItem BuildEmbeddedPayloadNode(UInt32 id, Byte[] bytes) {
      return id switch {
        WaterDepthTextureId => ReadDepthTexture(bytes),
        WaterVertexDataId => ReadWaterVertexData(bytes),
        RegionVolumeDataId => ReadRegionVolumeData(bytes),
        HeightMapVertexDataId => ReadHeightMapVertexData(bytes),
        _ => throw new InvalidDataException($"No embedded reader for property 0x{id:X8}.")
      };
    }

    private static Boolean IsEmbeddedRoomProperty(UInt32 id, String assetName) {
      // DepthTexture/wtrVertexData/rgnVolumeData are uniquely named SWTOR payload properties.
      // VertexData is a generic authored name, so only interpret it as a heightmap when the
      // placing asset is actually /engine/heightmap.hms (Jedipedia's prototype guard).
      if (id == WaterDepthTextureId || id == WaterVertexDataId || id == RegionVolumeDataId) return true;
      if (id != HeightMapVertexDataId) return false;
      String normalized = (assetName ?? String.Empty).Replace('\\', '/').ToLowerInvariant();
      return normalized.EndsWith(".hms", StringComparison.Ordinal)
        || normalized.Contains("/heightmap.hms", StringComparison.Ordinal);
    }

    private static Boolean TryEmbeddedPropertyId(String propertyName, out UInt32 id) {
      switch ((propertyName ?? String.Empty).Trim().ToLowerInvariant()) {
        case "depthtexture": id = WaterDepthTextureId; return true;
        case "wtrvertexdata": id = WaterVertexDataId; return true;
        case "rgnvolumedata": id = RegionVolumeDataId; return true;
        case "vertexdata": id = HeightMapVertexDataId; return true;
        default: id = 0; return false;
      }
    }

    private static String DetectEmbeddedCompression(Byte[] bytes) {
      if (bytes == null || bytes.Length == 0) return "none";
      if (bytes.Length >= 4 && bytes[0] == 0x28 && bytes[1] == 0xB5 && bytes[2] == 0x2F && bytes[3] == 0xFD)
        return "zstd";
      if (bytes.Length >= 2 && bytes[0] == 0x1F && bytes[1] == 0x8B) return "gzip";
      if (bytes.Length >= 2 && (bytes[0] & 0x0F) == 8 && (bytes[0] >> 4) <= 7
          && (((bytes[0] << 8) + bytes[1]) % 31) == 0) return "zlib";
      return "none";
    }

    private static NodeListItem ReadDepthTexture(Byte[] bytes) {
      using MemoryStream ms = new MemoryStream(bytes, false);
      using BinaryReader br = new BinaryReader(ms);
      EnsureEmbeddedRemaining(br, 9, "DepthTexture header");
      Byte version = br.ReadByte();
      if (version != 1) throw new InvalidDataException("Unsupported DepthTexture version " + version + ".");
      UInt32 width = br.ReadUInt32();
      UInt32 height = br.ReadUInt32();
      UInt64 pixels = (UInt64)width * height;
      if (pixels > Int32.MaxValue || pixels != (UInt64)(ms.Length - ms.Position))
        throw new InvalidDataException($"DepthTexture expects {pixels:N0} values ({width}x{height}), but {ms.Length - ms.Position:N0} bytes remain.");

      Byte[] depth = br.ReadBytes((Int32)pixels);
      Int32 min = 255, max = 0, nonZero = 0;
      UInt64 sum = 0;
      Int32[] histogram = new Int32[16];
      foreach (Byte value in depth) {
        min = Math.Min(min, value); max = Math.Max(max, value); sum += value;
        if (value != 0) nonZero++;
        histogram[value >> 4]++;
      }
      if (depth.Length == 0) min = 0;

      NodeListItem root = Branch("Embedded DepthTexture", $"{width} x {height}");
      root.children.Add(Leaf("Version", version.ToString(Invariant)));
      root.children.Add(Leaf("Dimensions", $"{width:N0} x {height:N0}"));
      root.children.Add(Leaf("Pixels", pixels.ToString("N0", Invariant)));
      root.children.Add(Leaf("Depth range", $"{min} .. {max}"));
      root.children.Add(Leaf("Mean depth", depth.Length == 0 ? "0" : ((Double)sum / depth.Length).ToString("0.###", Invariant)));
      root.children.Add(Leaf("Non-zero pixels", $"{nonZero:N0} ({(depth.Length == 0 ? 0 : (Double)nonZero * 100.0 / depth.Length).ToString("0.##", Invariant)}%)"));
      NodeListItem buckets = Branch("Depth histogram", "16 buckets");
      for (Int32 i = 0; i < histogram.Length; i++)
        buckets.children.Add(Leaf($"{i * 16:D3}-{i * 16 + 15:D3}", histogram[i].ToString("N0", Invariant)));
      root.children.Add(buckets);
      return root;
    }

    private static NodeListItem ReadWaterVertexData(Byte[] bytes) {
      using MemoryStream ms = new MemoryStream(bytes, false);
      using BinaryReader br = new BinaryReader(ms);
      EnsureEmbeddedRemaining(br, 3, "water vertex header");
      Byte version = br.ReadByte();
      UInt16 vertexCount = br.ReadUInt16();
      EnsureEmbeddedRemaining(br, checked((UInt64)vertexCount * 12 + 2), "water vertices");

      (Single X, Single Y, Single Z)[] vertices = new (Single, Single, Single)[vertexCount];
      Single minX = Single.PositiveInfinity, minY = Single.PositiveInfinity, minZ = Single.PositiveInfinity;
      Single maxX = Single.NegativeInfinity, maxY = Single.NegativeInfinity, maxZ = Single.NegativeInfinity;
      for (Int32 i = 0; i < vertexCount; i++) {
        Single x = br.ReadSingle(), y = br.ReadSingle(), z = br.ReadSingle();
        if (!Single.IsFinite(x) || !Single.IsFinite(y) || !Single.IsFinite(z))
          throw new InvalidDataException("Water vertex contains a non-finite coordinate.");
        vertices[i] = (x, y, z);
        minX = Math.Min(minX, x); minY = Math.Min(minY, y); minZ = Math.Min(minZ, z);
        maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y); maxZ = Math.Max(maxZ, z);
      }
      UInt16 indexCount = br.ReadUInt16();
      if (indexCount % 3 != 0) throw new InvalidDataException($"Water index count {indexCount} is not a multiple of 3.");
      EnsureEmbeddedRemaining(br, (UInt64)indexCount * 2, "water indices");
      UInt16[] indices = new UInt16[indexCount];
      for (Int32 i = 0; i < indexCount; i++) {
        UInt16 index = br.ReadUInt16();
        if (index >= vertexCount) throw new InvalidDataException($"Water index {index} exceeds vertex count {vertexCount}.");
        indices[i] = index;
      }
      if (ms.Position != ms.Length) throw new InvalidDataException($"Water payload has {ms.Length - ms.Position:N0} trailing bytes.");

      NodeListItem root = Branch("Embedded water mesh", $"{vertexCount:N0} vertices / {indexCount / 3:N0} triangles");
      root.children.Add(Leaf("Version", version.ToString(Invariant)));
      root.children.Add(Leaf("Vertices", vertexCount.ToString("N0", Invariant)));
      root.children.Add(Leaf("Triangles", (indexCount / 3).ToString("N0", Invariant)));
      root.children.Add(Leaf("Bounds min", vertexCount == 0 ? "n/a" : Vec3(minX, minY, minZ)));
      root.children.Add(Leaf("Bounds max", vertexCount == 0 ? "n/a" : Vec3(maxX, maxY, maxZ)));

      NodeListItem vertexPreview = Branch("Vertex preview", PreviewCount(vertexCount));
      for (Int32 i = 0; i < Math.Min((Int32)vertexCount, EmbeddedPreviewLimit); i++)
        vertexPreview.children.Add(Leaf(i.ToString(Invariant), Vec3(vertices[i].X, vertices[i].Y, vertices[i].Z)));
      root.children.Add(vertexPreview);
      root.children.Add(BuildTrianglePreview(indices));
      return root;
    }

    private static NodeListItem ReadRegionVolumeData(Byte[] bytes) {
      using MemoryStream ms = new MemoryStream(bytes, false);
      using BinaryReader br = new BinaryReader(ms);
      EnsureEmbeddedRemaining(br, 3, "region volume header");
      Byte version = br.ReadByte();
      UInt16 vertexCount = br.ReadUInt16();
      EnsureEmbeddedRemaining(br, checked((UInt64)vertexCount * 16 + 2), "region volume vertices");

      (Single X, Single Y, Single Z, Single Height)[] edges = new (Single, Single, Single, Single)[vertexCount];
      Single minX = Single.PositiveInfinity, minY = Single.PositiveInfinity, minZ = Single.PositiveInfinity;
      Single maxX = Single.NegativeInfinity, maxY = Single.NegativeInfinity, maxZ = Single.NegativeInfinity;
      for (Int32 i = 0; i < vertexCount; i++) {
        Single x = br.ReadSingle(), y = br.ReadSingle(), z = br.ReadSingle(), height = br.ReadSingle();
        if (!Single.IsFinite(x) || !Single.IsFinite(y) || !Single.IsFinite(z) || !Single.IsFinite(height))
          throw new InvalidDataException("Region-volume vertex contains a non-finite value.");
        edges[i] = (x, y, z, height);
        minX = Math.Min(minX, x); minY = Math.Min(minY, Math.Min(y, y + height)); minZ = Math.Min(minZ, z);
        maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, Math.Max(y, y + height)); maxZ = Math.Max(maxZ, z);
      }
      UInt16 indexCount = br.ReadUInt16();
      if (indexCount % 3 != 0) throw new InvalidDataException($"Region-volume index count {indexCount} is not a multiple of 3.");
      EnsureEmbeddedRemaining(br, (UInt64)indexCount * 2, "region volume indices");
      UInt16[] indices = new UInt16[indexCount];
      for (Int32 i = 0; i < indexCount; i++) {
        UInt16 index = br.ReadUInt16();
        if (index >= vertexCount) throw new InvalidDataException($"Region-volume index {index} exceeds vertex count {vertexCount}.");
        indices[i] = index;
      }
      if (ms.Position != ms.Length) throw new InvalidDataException($"Region-volume payload has {ms.Length - ms.Position:N0} trailing bytes.");

      NodeListItem root = Branch("Embedded region volume", $"{vertexCount:N0} edges / {indexCount / 3:N0} triangles");
      root.children.Add(Leaf("Version", version.ToString(Invariant)));
      root.children.Add(Leaf("Edges", vertexCount.ToString("N0", Invariant)));
      root.children.Add(Leaf("Triangles", (indexCount / 3).ToString("N0", Invariant)));
      root.children.Add(Leaf("Extruded bounds min", vertexCount == 0 ? "n/a" : Vec3(minX, minY, minZ)));
      root.children.Add(Leaf("Extruded bounds max", vertexCount == 0 ? "n/a" : Vec3(maxX, maxY, maxZ)));

      NodeListItem edgePreview = Branch("Edge preview", PreviewCount(vertexCount));
      for (Int32 i = 0; i < Math.Min((Int32)vertexCount, EmbeddedPreviewLimit); i++) {
        var edge = edges[i];
        edgePreview.children.Add(Leaf(i.ToString(Invariant),
          $"position={Vec3(edge.X, edge.Y, edge.Z)}, height={Float(edge.Height)}"));
      }
      root.children.Add(edgePreview);
      root.children.Add(BuildTrianglePreview(indices));
      return root;
    }

    private static NodeListItem ReadHeightMapVertexData(Byte[] bytes) {
      using MemoryStream ms = new MemoryStream(bytes, false);
      using BinaryReader br = new BinaryReader(ms);
      HeightMap map = new HeightMap(br, null, true);
      UInt64 vertices = (UInt64)map.width * map.depth;
      Int64 solidCells = 0, holeCells = 0;
      if (map.width > 1 && map.depth > 1) {
        for (Int32 x = 0; x < map.width - 1; x++) {
          for (Int32 z = 0; z < map.depth - 1; z++) {
            if (map.CheckNoHole(x, z)) solidCells++; else holeCells++;
          }
        }
      }

      NodeListItem root = Branch("Embedded heightmap", $"{map.width:N0} x {map.depth:N0}");
      root.children.Add(Leaf("Version / flags", $"{map.headerBitFlag} (0x{map.headerBitFlag:X2})"));
      root.children.Add(Leaf("Terrain weight layout", (map.headerBitFlag & 8) != 0 ? "sparse" : "dense"));
      root.children.Add(Leaf("Dimensions", $"{map.width:N0} x {map.depth:N0}"));
      if (map.HasInstancePosition)
        root.children.Add(Leaf("Instance position", Vec3(map.InstancePositionX, map.InstancePositionY, map.InstancePositionZ)));
      root.children.Add(Leaf("Vertices", vertices.ToString("N0", Invariant)));
      root.children.Add(Leaf("Elevation range", $"{Float(map.MinElevation)} .. {Float(map.MaxElevation)}"));
      root.children.Add(Leaf("Has holes", map.hasHoles.ToString()));
      root.children.Add(Leaf("Solid terrain cells", solidCells.ToString("N0", Invariant)));
      root.children.Add(Leaf("Hole cells", holeCells.ToString("N0", Invariant)));
      root.children.Add(Leaf("Terrain layers decoded", map.TerrainLayers.Count.ToString("N0", Invariant)));
      root.children.Add(Leaf("Dynamic-detail channels", map.DynamicDetails.Count.ToString("N0", Invariant)));
      root.children.Add(Leaf("Native render mesh", map.RenderMesh == null
        ? (String.IsNullOrWhiteSpace(map.RenderMeshParseError) ? "none" : "marker present; parse failed")
        : $"{map.RenderMesh.VertexCount:N0} vertices / {map.RenderMesh.TriangleCount:N0} triangles / {map.RenderMesh.LevelCount:N0} LOD-batch levels"));
      root.children.Add(Leaf("Unexplained trailing bytes", map.UnexplainedTrailingBytes.ToString("N0", Invariant)));

      if (map.DynamicDetails.Count > 0) {
        NodeListItem dyd = Branch("Dynamic details", map.DynamicDetails.Count.ToString("N0", Invariant));
        foreach (DynamicDetailPaint channel in map.DynamicDetails.OrderBy(x => x.ChannelId)) {
          Byte maxDensity = channel.Density == null || channel.Density.Length == 0 ? (Byte)0 : channel.Density.Max();
          String kind = channel.ChannelId >= 32 ? "mesh" : "billboard";
          dyd.children.Add(Leaf($"Channel {channel.ChannelId}",
            $"{kind}; {channel.PaintedVertices:N0} painted vertices; max density {maxDensity}"));
        }
        root.children.Add(dyd);
      }

      if (map.RenderMesh != null) root.children.Add(BuildHeightMapRenderMeshNode(map.RenderMesh));
      else if (!String.IsNullOrWhiteSpace(map.RenderMeshParseError))
        root.children.Add(Leaf("Render-mesh parse error", map.RenderMeshParseError));
      return root;
    }

    private static NodeListItem BuildHeightMapRenderMeshNode(HeightMapRenderMesh mesh) {
      NodeListItem root = Branch("Native render mesh (0xF1234567)",
        $"{mesh.VertexCount:N0} vertices / {mesh.TriangleCount:N0} triangles");
      root.children.Add(Leaf("Tile min corner",
        $"x={Float(mesh.MinX)}, z={Float(mesh.MinZ)}  (fixed {mesh.MinXFixed}, {mesh.MinZFixed}; /64)"));
      root.children.Add(Leaf("Grid", $"{mesh.GridWidth:N0} x {mesh.GridDepth:N0}"));
      root.children.Add(Leaf("LOD / batch levels", mesh.LevelCount.ToString("N0", Invariant)));
      root.children.Add(Leaf("Batch boundaries", mesh.BatchBoundaries.Length.ToString("N0", Invariant)));
      root.children.Add(Leaf("Vertices", mesh.VertexCount.ToString("N0", Invariant)));
      root.children.Add(Leaf("Indices", mesh.IndexCount.ToString("N0", Invariant)));
      root.children.Add(Leaf("Triangles", mesh.TriangleCount.ToString("N0", Invariant)));
      root.children.Add(Leaf("Undecoded prefix", mesh.PrefixLength.ToString("N0", Invariant) + " bytes"));
      root.children.Add(Leaf("Offsets",
        $"marker=0x{mesh.MarkerOffset:X}; vertices=0x{mesh.RecordOffset:X}; indices=0x{mesh.IndexOffset:X}"));

      if (mesh.TryGetLodRanges(out HeightMapRenderLodRange[] lodRanges, out String boundaryUnit)) {
        root.children.Add(Leaf("LOD boundary interpretation", $"{boundaryUnit}; triangle-aligned"));
        NodeListItem derived = Branch("Derived native LOD ranges", PreviewCount(lodRanges.Length));
        for (Int32 i = 0; i < Math.Min(lodRanges.Length, EmbeddedPreviewLimit); i++) {
          HeightMapRenderLodRange range = lodRanges[i];
          derived.children.Add(Leaf($"Source level {range.SourceLevel}",
            $"start={range.StartIndex:N0}; indices={range.Count:N0}; triangles={range.TriangleCount:N0}"));
        }
        root.children.Add(derived);
      } else {
        root.children.Add(Leaf("LOD boundary interpretation", "unknown; World Browser will use procedural grid LODs"));
      }

      NodeListItem boundaries = Branch("LOD / batch boundary groups", PreviewCount((Int32)mesh.LevelCount));
      Int32 levelPreview = (Int32)Math.Min((UInt32)EmbeddedPreviewLimit, mesh.LevelCount);
      for (Int32 level = 0; level < levelPreview; level++) {
        Int32 start = checked(level * 4);
        Int32 count = Math.Min(4, mesh.BatchBoundaries.Length - start);
        if (count <= 0) break;
        String values = String.Join(", ", mesh.BatchBoundaries.Skip(start).Take(count).Select(x => x.ToString("N0", Invariant)));
        boundaries.children.Add(Leaf($"Level {level}", values));
      }
      root.children.Add(boundaries);

      UInt16 minGridX = UInt16.MaxValue, minGridZ = UInt16.MaxValue, maxGridX = 0, maxGridZ = 0;
      Int32 outsideGrid = 0;
      foreach (HeightMapRenderVertex vertex in mesh.Vertices) {
        if (vertex.X < minGridX) minGridX = vertex.X; if (vertex.X > maxGridX) maxGridX = vertex.X;
        if (vertex.Z < minGridZ) minGridZ = vertex.Z; if (vertex.Z > maxGridZ) maxGridZ = vertex.Z;
        if (vertex.X >= mesh.GridWidth || vertex.Z >= mesh.GridDepth) outsideGrid++;
      }
      root.children.Add(Leaf("Vertex grid-coordinate range", mesh.VertexCount == 0 ? "n/a"
        : $"x={minGridX}..{maxGridX}; z={minGridZ}..{maxGridZ}; outside declared grid={outsideGrid:N0}"));

      NodeListItem vertices = Branch("Native vertex preview", PreviewCount(mesh.VertexCount));
      for (Int32 i = 0; i < Math.Min(mesh.VertexCount, EmbeddedPreviewLimit); i++) {
        HeightMapRenderVertex v = mesh.Vertices[i];
        vertices.children.Add(Leaf(i.ToString(Invariant),
          $"U={Float(v.U)}; grid=({v.X}, {v.Z}); " +
          $"normal=({Packed(v.NormalX)}, {Packed(v.NormalY)}, {Packed(v.NormalZ)}, {Packed(v.NormalW)}); " +
          $"tangent=({Packed(v.TangentX)}, {Packed(v.TangentY)}, {Packed(v.TangentZ)}, {Packed(v.TangentW)})"));
      }
      root.children.Add(vertices);
      root.children.Add(BuildTrianglePreview(mesh.Indices));
      return root;
    }

    private static String Packed(SByte value) {
      String normalized = (value / 127f).ToString("0.###", Invariant);
      return $"{value} / {normalized}";
    }

    private static NodeListItem BuildTrianglePreview(UInt16[] indices) {
      Int32 triangleCount = (indices?.Length ?? 0) / 3;
      NodeListItem triangles = Branch("Triangle preview", PreviewCount(triangleCount));
      for (Int32 i = 0; i < Math.Min(triangleCount, EmbeddedPreviewLimit); i++) {
        Int32 offset = i * 3;
        triangles.children.Add(Leaf(i.ToString(Invariant),
          $"{indices[offset]}, {indices[offset + 1]}, {indices[offset + 2]}"));
      }
      return triangles;
    }

    private static String PreviewCount(Int32 count) => count <= EmbeddedPreviewLimit
      ? count.ToString("N0", Invariant)
      : $"first {EmbeddedPreviewLimit:N0} of {count:N0}";

    private static String PreviewCount(UInt16 count) => PreviewCount((Int32)count);

    private static void EnsureEmbeddedRemaining(BinaryReader br, UInt64 count, String context) {
      if (count > (UInt64)Math.Max(0, br.BaseStream.Length - br.BaseStream.Position))
        throw new EndOfStreamException($"Unexpected end of embedded DAT while reading {context}.");
    }
  }
}
