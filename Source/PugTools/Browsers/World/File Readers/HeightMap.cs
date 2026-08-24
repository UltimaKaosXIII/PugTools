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

    private readonly Area area;

    public HeightMap(BinaryReader br, Area area = null) {
      this.area = area;
      headerBitFlag = br.ReadByte();
      width = br.ReadUInt32();
      depth = br.ReadUInt32();
      if (width == 0 || depth == 0 || width > 512 || depth > 512)
        throw new InvalidDataException("Invalid heightmap dimensions: " + width + "x" + depth);

      elevation = new float[depth, width];
      bool sparse = (headerBitFlag & 8) != 0;
      if (sparse) {
        // SWTOR stores a redundant copy of the placement position in sparse heightmaps.
        br.ReadSingle(); br.ReadSingle(); br.ReadSingle();
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
