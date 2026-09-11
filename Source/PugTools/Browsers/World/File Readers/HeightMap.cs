using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace FileFormats {
  public sealed class TerrainLayerMask {
    public string MaterialName { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public byte[] Weights { get; set; }
  }

  public sealed class DynamicDetailPaint {
    public int ChannelId { get; set; }
    public byte[] Density { get; set; }
    public int PaintedVertices { get; set; }
  }

  /// <summary>
  /// One native terrain render vertex from the 0xF1234567 trailing block. SWTOR stores
  /// grid coordinates plus packed normal/tangent vectors; the first float is still of
  /// uncertain semantic meaning in the client format, so it is intentionally exposed as U.
  /// </summary>
  public struct HeightMapRenderVertex {
    public float U;
    public ushort X;
    public ushort Z;
    public sbyte NormalX;
    public sbyte NormalY;
    public sbyte NormalZ;
    public sbyte NormalW;
    public sbyte TangentX;
    public sbyte TangentY;
    public sbyte TangentZ;
    public sbyte TangentW;
  }

  public sealed class HeightMapRenderLodRange {
    public int SourceLevel { get; internal set; }
    public int StartIndex { get; internal set; }
    public int Count { get; internal set; }
    public int TriangleCount => Count / 3;
  }

  /// <summary>
  /// Jedipedia-compatible decoding of the native heightmap render-mesh trailer. The prefix
  /// remains opaque, but its length, LOD/batch boundary table, vertex records and indices are
  /// validated and retained for inspection and optional native terrain topology.
  /// </summary>
  public sealed class HeightMapRenderMesh {
    public const uint Magic = 0xF1234567u;
    public const uint FooterMagic = 0xABCDEFABu;

    public long MarkerOffset { get; internal set; }
    public int MinXFixed { get; internal set; }
    public int MinZFixed { get; internal set; }
    public int GridWidth { get; internal set; }
    public int GridDepth { get; internal set; }
    public int PrefixLength { get; internal set; }
    public uint LevelCount { get; internal set; }
    public uint[] BatchBoundaries { get; internal set; } = Array.Empty<uint>();
    public HeightMapRenderVertex[] Vertices { get; internal set; } = Array.Empty<HeightMapRenderVertex>();
    public ushort[] Indices { get; internal set; } = Array.Empty<ushort>();
    public long RecordOffset { get; internal set; }
    public long IndexOffset { get; internal set; }
    public int VertexCount { get; internal set; }
    public int IndexCount { get; internal set; }

    public int TriangleCount => IndexCount / 3;
    public float MinX => MinXFixed / 64f;
    public float MinZ => MinZFixed / 64f;

    /// <summary>
    /// The native table has 4*K batches and therefore 4*K-1 explicit cumulative boundaries.
    /// Jedipedia has established that they belong to the index-buffer batching, but corpus readers
    /// do not require whether the stored unit is indices or bytes. Accept only an interpretation
    /// where every boundary lands on a triangle edge and all K four-batch LOD ranges partition the
    /// index buffer exactly. This intentionally fails closed for unknown layouts.
    /// </summary>
    public bool TryGetLodRanges(out HeightMapRenderLodRange[] ranges, out string boundaryUnit) {
      ranges = Array.Empty<HeightMapRenderLodRange>();
      boundaryUnit = null;
      if (LevelCount < 1 || LevelCount > (uint)Int32.MaxValue || IndexCount < 3 || IndexCount % 3 != 0) return false;
      ulong expected64 = 4UL * LevelCount - 1UL;
      if (expected64 > Int32.MaxValue || BatchBoundaries == null || BatchBoundaries.Length != (int)expected64) return false;
      if (TryGetLodRanges(1, out ranges)) { boundaryUnit = "indices"; return true; }
      if (TryGetLodRanges(2, out ranges)) { boundaryUnit = "bytes"; return true; }
      ranges = Array.Empty<HeightMapRenderLodRange>();
      return false;
    }

    private bool TryGetLodRanges(int divisor, out HeightMapRenderLodRange[] ranges) {
      ranges = Array.Empty<HeightMapRenderLodRange>();
      int[] boundaries = new int[BatchBoundaries.Length];
      int previous = -1;
      for (int i = 0; i < BatchBoundaries.Length; i++) {
        uint raw = BatchBoundaries[i];
        if (raw % (uint)divisor != 0) return false;
        ulong converted64 = raw / (uint)divisor;
        if (converted64 >= (ulong)IndexCount || converted64 > Int32.MaxValue) return false;
        int converted = (int)converted64;
        if (converted <= previous || converted % 3 != 0) return false;
        boundaries[i] = converted;
        previous = converted;
      }

      int levels = (int)LevelCount;
      var result = new HeightMapRenderLodRange[levels];
      int covered = 0;
      for (int level = 0; level < levels; level++) {
        int start = level == 0 ? 0 : boundaries[level * 4 - 1];
        int end = level == levels - 1 ? IndexCount : boundaries[level * 4 + 3];
        int count = end - start;
        if (start != covered || count <= 0 || count % 3 != 0) return false;
        result[level] = new HeightMapRenderLodRange { SourceLevel = level, StartIndex = start, Count = count };
        covered = end;
      }
      if (covered != IndexCount) return false;
      ranges = result;
      return true;
    }

    public void ReleaseGeometryData() {
      Vertices = Array.Empty<HeightMapRenderVertex>();
      Indices = Array.Empty<ushort>();
    }
  }

  public class HeightMap {
    public bool hasHoles;
    public float[,] elevation;
    public byte headerBitFlag;
    public byte[] holeMap;
    public byte[,] textures;
    public uint width;
    public uint depth;
    public float MinElevation { get; private set; } = float.MaxValue;
    public float MaxElevation { get; private set; } = float.MinValue;
    public List<TerrainLayerMask> TerrainLayers { get; } = new List<TerrainLayerMask>();
    public List<DynamicDetailPaint> DynamicDetails { get; } = new List<DynamicDetailPaint>();
    public byte[] TerrainColorMapRgba { get; private set; }
    public bool HasInstancePosition { get; private set; }
    public float InstancePositionX { get; private set; }
    public float InstancePositionY { get; private set; }
    public float InstancePositionZ { get; private set; }
    public HeightMapRenderMesh RenderMesh { get; private set; }
    public string RenderMeshParseError { get; private set; }
    public long UnexplainedTrailingBytes { get; private set; }

    private readonly Area area;

    public HeightMap(BinaryReader br, Area area = null, bool readRenderMesh = false) {
      this.area = area;
      headerBitFlag = br.ReadByte();
      width = br.ReadUInt32();
      depth = br.ReadUInt32();
      if (width == 0 || depth == 0 || width > 512 || depth > 512)
        throw new InvalidDataException("Invalid heightmap dimensions: " + width + "x" + depth);

      elevation = new float[depth, width];
      bool sparse = (headerBitFlag & 8) != 0;
      if (sparse) {
        // SWTOR stores a redundant copy of the placement position in sparse heightmaps. Jedipedia
        // surfaces it in the parsed view because it is useful when checking room-placement data.
        InstancePositionX = br.ReadSingle();
        InstancePositionY = br.ReadSingle();
        InstancePositionZ = br.ReadSingle();
        HasInstancePosition = true;
      }

      hasHoles = br.ReadByte() == 1;
      if (hasHoles) holeMap = br.ReadBytes((int)((width * depth + 7) / 8));

      for (int x = 0; x < width; x++) {
        SetElevation(x, 0, br.ReadSingle());
        SetElevation(x, (int)depth - 1, br.ReadSingle());
      }
      for (int z = 1; z < depth - 1; z++) {
        SetElevation(0, z, br.ReadSingle());
        SetElevation((int)width - 1, z, br.ReadSingle());
      }
      for (int x = 1; x < width - 1; x++) {
        for (int z = 1; z < depth - 1; z++) {
          SetElevation(x, z, br.ReadUInt16() / 512f);
        }
      }

      if (MinElevation == float.MaxValue) MinElevation = 0;
      if (MaxElevation == float.MinValue) MaxElevation = 1;

      try {
        if (sparse) ReadSparseTerrainWeights(br);
        else ReadDenseTerrainWeights(br);
      } catch (EndOfStreamException) {
        EnsureFallbackTerrain();
      } catch (IOException) {
        EnsureFallbackTerrain();
      }

      long decodedEnd = br.BaseStream.CanSeek ? br.BaseStream.Position : 0;
      // Dense version-2 heightmaps can append a native render mesh after the authored terrain data.
      // Parsing is intentionally non-fatal: the elevation/splat data remains useful even when a
      // malformed or partially-patched trailer cannot be validated.
      if (readRenderMesh && headerBitFlag == 2) TryReadRenderMesh(br, decodedEnd);
      if (readRenderMesh && br.BaseStream.CanSeek)
        UnexplainedTrailingBytes = RenderMesh != null ? 0 : Math.Max(0, br.BaseStream.Length - decodedEnd);
    }

    private void SetElevation(int x, int z, float value) {
      elevation[z, x] = value;
      MinElevation = Math.Min(MinElevation, value);
      MaxElevation = Math.Max(MaxElevation, value);
    }

    private string TerrainName(sbyte textureIndex) {
      return area?.ResolveTerrainTextureName(textureIndex);
    }

    private void ReadDenseTerrainWeights(BinaryReader br) {
      int numTextures = br.ReadByte();
      var names = new List<string>(numTextures);
      for (int i = 0; i < numTextures; i++) names.Add(TerrainName(br.ReadSByte()));

      int mapWidth = checked((int)width * 2 - 1);
      int mapDepth = checked((int)depth * 2 - 1);
      var masks = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
      foreach (string name in names)
        if (!string.IsNullOrWhiteSpace(name) && !masks.ContainsKey(name)) masks[name] = new byte[mapWidth * mapDepth];

      for (int z = 0; z < mapDepth; z++) {
        for (int x = 0; x < mapWidth; x++) {
          int off = z * mapWidth + x;
          for (int i = 0; i < numTextures; i++) {
            byte weight = br.ReadByte();
            string name = names[i];
            if (string.IsNullOrWhiteSpace(name)) continue;
            int sum = masks[name][off] + weight;
            masks[name][off] = (byte)Math.Min(255, sum);
          }
        }
      }
      string baseName = names.Find(n => !string.IsNullOrWhiteSpace(n)) ?? "terrain_checkered";
      EnsureBaseCoverage(masks, baseName, mapWidth, mapDepth);
      AddMasks(masks, mapWidth, mapDepth);

      TerrainColorMapRgba = SolidColorMap();
      if (br.BaseStream.Position < br.BaseStream.Length) {
        byte colorFlag = br.ReadByte();
        long expected = (long)width * depth * 3;
        if (colorFlag != 0 && br.BaseStream.Length - br.BaseStream.Position >= expected) ReadColorMap(br);
      }
      long dynamicEnd = FindTrailingMarker(br.BaseStream, br.BaseStream.Position);
      ReadDynamicDetails(br, dynamicEnd);
    }

    private void ReadSparseTerrainWeights(BinaryReader br) {
      if (br.BaseStream.Position >= br.BaseStream.Length) { EnsureFallbackTerrain(); return; }
      int declared = br.ReadByte();
      _ = declared;
      int mapWidth = (int)width;
      int mapDepth = (int)depth;
      var masks = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
      string first = null;
      bool truncated=false;
      for (int x = 0; x < width && !truncated; x++) {
        for (int z = 0; z < depth; z++) {
          if (br.BaseStream.Position >= br.BaseStream.Length) { truncated=true; break; }
          int count = br.ReadByte();
          for (int i = 0; i < count; i++) {
            if (br.BaseStream.Length-br.BaseStream.Position < 2) { truncated=true; break; }
            string name = TerrainName(br.ReadSByte());
            byte weight = br.ReadByte();
            if (string.IsNullOrWhiteSpace(name)) continue;
            first ??= name;
            if (!masks.TryGetValue(name, out byte[] mask)) masks[name] = mask = new byte[mapWidth * mapDepth];
            int off = z * mapWidth + x;
            mask[off] = (byte)Math.Min(255, mask[off] + weight);
          }
          if(truncated)break;
        }
      }
      first ??= "terrain_checkered";
      EnsureBaseCoverage(masks, first, mapWidth, mapDepth);
      AddMasks(masks, mapWidth, mapDepth);
      if(truncated){TerrainColorMapRgba=SolidColorMap();return;}

      // 1.0.x sparse heightmaps can contain a second dense splat block here. Jedipedia skips it
      // before reading DYD paint; otherwise the splat bytes are easily mistaken for channel records.
      long pos = SkipLegacyDenseWeights(br, br.BaseStream.Position);
      br.BaseStream.Position = pos;

      TerrainColorMapRgba = SolidColorMap();
      // Match Jedipedia's sparse-heightmap tail handling exactly. Some versions append four bytes
      // after the RGB macro map and gate that layout with the byte immediately before the map.
      long colorBytes = (long)width * depth * 3;
      long sparseColorMapOffset = br.BaseStream.Length - colorBytes - 4;
      long packedTailOffset = br.BaseStream.Length - colorBytes;
      long colorMapOffset = packedTailOffset;
      if (sparseColorMapOffset > pos && sparseColorMapOffset > 0) {
        long restore = br.BaseStream.Position;
        br.BaseStream.Position = sparseColorMapOffset - 1;
        if (br.ReadByte() != 0) colorMapOffset = sparseColorMapOffset;
        br.BaseStream.Position = restore;
      }
      if (colorMapOffset >= pos) ReadDynamicDetails(br, colorMapOffset);
      if (colorMapOffset >= pos && colorMapOffset >= 0 && br.BaseStream.Length - colorMapOffset >= colorBytes) {
        br.BaseStream.Position = colorMapOffset;
        ReadColorMap(br);
      }
    }

    private long SkipLegacyDenseWeights(BinaryReader br, long pos) {
      long restore = br.BaseStream.Position;
      try {
        if (pos + 4 > br.BaseStream.Length) return pos;
        br.BaseStream.Position = pos;
        uint layerCount = br.ReadUInt32();
        if (layerCount < 1 || layerCount > 64) return pos;
        long expectedLength = (long)(2 * depth - 1) * (2 * width);
        long walk = pos + 4;
        for (uint layer=0; layer<layerCount; layer++) {
          if (walk + 5 > br.BaseStream.Length) return pos;
          br.BaseStream.Position = walk + 1;
          uint length = br.ReadUInt32();
          if (length != expectedLength) return pos;
          walk += 5 + length;
          if (walk > br.BaseStream.Length) return pos;
        }
        return walk;
      } finally { br.BaseStream.Position = restore; }
    }

    private static long FindTrailingMarker(Stream stream, long start) {
      long restore = stream.Position;
      try {
        stream.Position = Math.Max(0,start);
        int b0=-1,b1=-1,b2=-1,b3;
        while ((b3=stream.ReadByte()) >= 0) {
          b0=b1; b1=b2; b2=b3;
          if (b0==0x67 && b1==0x45 && b2==0x23) {
            int b4=stream.ReadByte();
            if (b4==0xF1) return stream.Position-4;
            if (b4<0) break;
            stream.Position -= 1;
          }
        }
        return stream.Length;
      } finally { stream.Position=restore; }
    }

    private void ReadDynamicDetails(BinaryReader br, long end) {
      long start=br.BaseStream.Position;
      end=Math.Min(end,br.BaseStream.Length);
      int vertexCount=checked((int)(width*depth));
      if(start>=end || end-start<1)return;
      byte count=br.ReadByte();
      long required=start+1L+count*(1L+vertexCount);
      if(required>end){br.BaseStream.Position=start;return;}
      ulong low=0, high=0;
      long scan=start+1;
      for(int i=0;i<count;i++){
        br.BaseStream.Position=scan;int id=br.ReadByte();
        if(id>63){br.BaseStream.Position=start;return;}
        ulong bit=1UL<<(id&31);
        if(id<32){if((low&bit)!=0){br.BaseStream.Position=start;return;}low|=bit;}
        else {if((high&bit)!=0){br.BaseStream.Position=start;return;}high|=bit;}
        scan += 1L+vertexCount;
      }
      br.BaseStream.Position=start+1;
      for(int i=0;i<count;i++){
        int id=br.ReadByte();byte[] density=new byte[vertexCount];int painted=0;
        // Serialized x-major/z-minor; normalize to the z-major grid used by elevation[,] and rendering.
        for(int x=0;x<width;x++)for(int z=0;z<depth;z++){
          byte v=br.ReadByte();density[z*(int)width+x]=v;if(v!=0)painted++;
        }
        if(painted>0)DynamicDetails.Add(new DynamicDetailPaint{ChannelId=id,Density=density,PaintedVertices=painted});
      }
    }

    private void TryReadRenderMesh(BinaryReader br, long scanStart) {
      if (!br.BaseStream.CanSeek || br.BaseStream.Length - scanStart < 24) return;
      long restore = br.BaseStream.Position;
      try {
        long marker = FindTrailingMarker(br.BaseStream, scanStart);
        if (marker >= br.BaseStream.Length) return;
        RenderMesh = ReadRenderMesh(br, marker);
        if (RenderMesh == null) RenderMeshParseError = "0xF1234567 marker found, but the render-mesh layout did not validate.";
      } catch (Exception ex) when (ex is EndOfStreamException || ex is IOException || ex is OverflowException || ex is InvalidDataException) {
        RenderMesh = null;
        RenderMeshParseError = ex.Message;
      } finally {
        br.BaseStream.Position = restore;
      }
    }

    private static HeightMapRenderMesh ReadRenderMesh(BinaryReader br, long markerPos) {
      Stream stream = br.BaseStream;
      long footerPos = stream.Length - 4;
      if (markerPos < 0 || markerPos + 20 > footerPos) return null;

      stream.Position = markerPos;
      if (br.ReadUInt32() != HeightMapRenderMesh.Magic) return null;
      int minX = br.ReadInt32();
      int minZ = br.ReadInt32();
      int meshWidth = br.ReadInt32();
      int meshDepth = br.ReadInt32();
      long bodyStart = stream.Position;

      stream.Position = footerPos;
      if (br.ReadUInt32() != HeightMapRenderMesh.FooterMagic) return null;
      long bodyLength = footerPos - bodyStart;
      if (bodyLength < 24) return null;

      RenderMeshAnchor anchor = default;
      bool found = false;
      for (long candidate = bodyStart; candidate + 24 <= footerPos; candidate++) {
        if (TryRenderMeshAnchor(br, candidate, bodyStart, footerPos, bodyLength, out anchor)) {
          found = true;
          break;
        }
      }
      if (!found) return null;

      if (anchor.VertexCount > (UInt32)Int32.MaxValue || anchor.IndexCount > (UInt32)Int32.MaxValue)
        throw new InvalidDataException("Heightmap render mesh is too large for the in-memory reader.");

      stream.Position = anchor.TableStart;
      uint[] boundaries = new uint[anchor.TableEntries];
      for (int i = 0; i < boundaries.Length; i++) {
        uint first = br.ReadUInt32();
        uint duplicate = br.ReadUInt32();
        if (first != duplicate) throw new InvalidDataException("Heightmap render-mesh batch boundary copy mismatch.");
        boundaries[i] = first;
      }

      stream.Position = anchor.RecordStart;
      var vertices = new HeightMapRenderVertex[(int)anchor.VertexCount];
      for (int i = 0; i < vertices.Length; i++) {
        vertices[i] = new HeightMapRenderVertex {
          U = br.ReadSingle(),
          X = br.ReadUInt16(),
          Z = br.ReadUInt16(),
          NormalX = br.ReadSByte(),
          NormalY = br.ReadSByte(),
          NormalZ = br.ReadSByte(),
          NormalW = br.ReadSByte(),
          TangentX = br.ReadSByte(),
          TangentY = br.ReadSByte(),
          TangentZ = br.ReadSByte(),
          TangentW = br.ReadSByte()
        };
      }

      stream.Position = anchor.IndexStart;
      ushort[] indices = new ushort[(int)anchor.IndexCount];
      for (int i = 0; i < indices.Length; i++) {
        ushort index = br.ReadUInt16();
        if (index >= vertices.Length)
          throw new InvalidDataException($"Heightmap render-mesh index {index} exceeds vertex count {vertices.Length}.");
        indices[i] = index;
      }

      return new HeightMapRenderMesh {
        MarkerOffset = markerPos,
        MinXFixed = minX,
        MinZFixed = minZ,
        GridWidth = meshWidth,
        GridDepth = meshDepth,
        PrefixLength = checked((int)(anchor.AnchorOffset - bodyStart)),
        LevelCount = anchor.LevelCount,
        BatchBoundaries = boundaries,
        Vertices = vertices,
        Indices = indices,
        VertexCount = vertices.Length,
        IndexCount = indices.Length,
        RecordOffset = anchor.RecordStart,
        IndexOffset = anchor.IndexStart
      };
    }

    private struct RenderMeshAnchor {
      public long AnchorOffset;
      public uint LevelCount;
      public int TableEntries;
      public long TableStart;
      public uint VertexCount;
      public uint IndexCount;
      public long RecordStart;
      public long IndexStart;
    }

    private static bool TryRenderMeshAnchor(BinaryReader br, long candidate, long bodyStart,
        long footerPos, long bodyLength, out RenderMeshAnchor anchor) {
      anchor = default;
      Stream stream = br.BaseStream;
      if (candidate < bodyStart || candidate + 8 > footerPos) return false;
      stream.Position = candidate;
      uint levelCount = br.ReadUInt32();
      if (levelCount < 1 || levelCount > 20000 || br.ReadUInt32() != 0) return false;

      ulong tableEntryCount64 = 4UL * levelCount - 1;
      if (tableEntryCount64 > (UInt64)Int32.MaxValue) return false;
      int tableEntries = (int)tableEntryCount64;
      long tableStart = candidate + 8;
      long tableBytes;
      try { tableBytes = checked((long)tableEntries * 8); }
      catch (OverflowException) { return false; }
      long trailerPos;
      try { trailerPos = checked(tableStart + tableBytes); }
      catch (OverflowException) { return false; }
      if (trailerPos + 20 > footerPos) return false;

      uint previous = 0;
      bool havePrevious = false;
      stream.Position = tableStart;
      for (int i = 0; i < tableEntries; i++) {
        uint first = br.ReadUInt32();
        uint duplicate = br.ReadUInt32();
        if (first != duplicate || (havePrevious && first <= previous) || first >= (ulong)bodyLength) return false;
        previous = first;
        havePrevious = true;
      }

      uint indexCount = br.ReadUInt32();
      uint big1 = br.ReadUInt32();
      uint vertexCount = br.ReadUInt32();
      if (br.ReadUInt32() != indexCount) return false;
      uint big2 = br.ReadUInt32();
      ulong expectedDataBytes = (ulong)vertexCount * 16UL + (ulong)indexCount * 2UL;
      if (expectedDataBytes > UInt32.MaxValue || big2 != (uint)expectedDataBytes) return false;
      if ((ulong)big1 != expectedDataBytes + 12UL) return false;

      long recordStart = trailerPos + 20;
      long indexStart;
      long end;
      try {
        indexStart = checked(recordStart + (long)vertexCount * 16L);
        end = checked(indexStart + (long)indexCount * 2L);
      } catch (OverflowException) { return false; }
      if (end != footerPos) return false;

      anchor = new RenderMeshAnchor {
        AnchorOffset = candidate,
        LevelCount = levelCount,
        TableEntries = tableEntries,
        TableStart = tableStart,
        VertexCount = vertexCount,
        IndexCount = indexCount,
        RecordStart = recordStart,
        IndexStart = indexStart
      };
      return true;
    }

    private void AddMasks(Dictionary<string, byte[]> masks, int mapWidth, int mapDepth) {
      foreach (var pair in masks) {
        bool any = false;
        foreach (byte b in pair.Value) { if (b != 0) { any = true; break; } }
        if (!any) continue;
        TerrainLayers.Add(new TerrainLayerMask {
          MaterialName = pair.Key,
          Width = mapWidth,
          Height = mapDepth,
          Weights = pair.Value
        });
      }
      EnsureFallbackTerrain();
    }

    private static void EnsureBaseCoverage(Dictionary<string, byte[]> masks, string baseName, int w, int h) {
      if (!masks.TryGetValue(baseName, out byte[] baseMask)) masks[baseName] = baseMask = new byte[w * h];
      var all = new List<byte[]>(masks.Values);
      for (int i = 0; i < baseMask.Length; i++) {
        int sum = 0;
        foreach (byte[] mask in all) sum += mask[i];
        if (sum < 255) baseMask[i] = (byte)Math.Min(255, baseMask[i] + (255 - sum));
      }
    }

    private byte[] SolidColorMap() {
      byte[] data = new byte[checked((int)(width * depth * 4))];
      for (int i = 0; i < data.Length; i += 4) data[i] = data[i + 1] = data[i + 2] = data[i + 3] = 255;
      return data;
    }

    private void ReadColorMap(BinaryReader br) {
      TerrainColorMapRgba ??= SolidColorMap();
      for (int x = 0; x < width; x++) {
        for (int z = 0; z < depth; z++) {
          int off = (z * (int)width + x) * 4;
          TerrainColorMapRgba[off] = br.ReadByte();
          TerrainColorMapRgba[off + 1] = br.ReadByte();
          TerrainColorMapRgba[off + 2] = br.ReadByte();
          TerrainColorMapRgba[off + 3] = 255;
        }
      }
    }

    private void EnsureFallbackTerrain() {
      if (TerrainLayers.Count == 0) {
        int w = (int)width, h = (int)depth;
        byte[] mask = new byte[w * h];
        Array.Fill(mask, (byte)255);
        TerrainLayers.Add(new TerrainLayerMask { MaterialName = "terrain_checkered", Width = w, Height = h, Weights = mask });
      }
      TerrainColorMapRgba ??= SolidColorMap();
    }

    public bool CheckNoHole(int x, int z) {
      if (!hasHoles || holeMap == null) return true;
      // SWTOR's bit is per cell and MSB-first (Jedipedia load/heightmap.js).
      // Native DAT storage is x-major/z-minor (index = x * depth + z).
      int index = x * (int)depth + z;
      int b = index >> 3;
      int bit = 7 - (index & 7);
      return b >= 0 && b < holeMap.Length && (holeMap[b] & (1 << bit)) != 0;
    }
  }
}
