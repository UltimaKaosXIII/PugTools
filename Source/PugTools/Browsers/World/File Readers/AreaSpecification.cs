using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using SlimDX;
using TorArchive;

namespace FileFormats {
  public sealed class AreaTerrainTexture {
    public uint Id { get; set; }
    public uint Layer { get; set; }
    public string Name { get; set; }
  }


  public sealed class AreaDynamicDetailTexture {
    public uint Id { get; set; }
    public string Name { get; set; }
    public bool IsMesh => !string.IsNullOrWhiteSpace(Name) && Name.EndsWith(".gr2", StringComparison.OrdinalIgnoreCase);
    public string MeshPath { get {
      if (!IsMesh) return null;
      string n=(Name??string.Empty).Trim().Replace('\\','/').TrimStart('/').ToLowerInvariant();
      return n.StartsWith("resources/", StringComparison.OrdinalIgnoreCase) ? "/"+n : "/resources/"+n;
    } }
    public string MaterialName { get {
      if (IsMesh || string.IsNullOrWhiteSpace(Name)) return null;
      string n=Name.Trim().Replace('\\','/').ToLowerInvariant().TrimStart('/');
      const string prefix="resources/art/shaders/materials/";
      if (n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) n=n.Substring(prefix.Length);
      if (n.EndsWith(".mat", StringComparison.OrdinalIgnoreCase)) n=n.Substring(0,n.Length-4);
      return n;
    } }
  }

  public sealed class AreaDynamicDetailChannelParam {
    public uint Type { get; set; }
    public uint Id { get; set; }
    public uint[] Values { get; set; }
    public int IntValue { get; set; }
    public string StringValue { get; set; }
    public float[] Floats { get; set; }
  }

  public sealed class AreaEnvironmentMaterial {
    public uint Id { get; set; }
    public string Name { get; set; }
    public bool EnableCreviceMap { get; set; } = true;
    public string BlendDiffuse { get; set; }
    public string BlendNormal { get; set; }
    public Vector4 EnvBlendParams1 { get; set; } = new Vector4(0.5f, 1f, 0.7f, -0.3f);
    public Vector4 EnvBlendParams2 { get; set; } = new Vector4(6f, 4f, 0.2f, 1f);
  }

  public sealed class AreaPathPoint {
    public ulong PathId { get; set; }
    public ulong PointId { get; set; }
    public Vector3 Position { get; set; }
    public Vector3 Rotation { get; set; }
    public string Data { get; set; }
    public float Tension { get; set; } = 0.75f;
    public float Speed { get; set; } = 1f;
    public float HoldTime { get; set; }
  }

  public sealed class AreaPath {
    public ulong Id { get; set; }
    public string Name { get; set; }
    public string Fqn { get; set; }
    public Vector4 Color { get; set; } = new Vector4(1, 1, 1, 1);
    public bool Circular { get; set; }
    public bool Smooth { get; set; }
    public List<AreaPathPoint> Points { get; } = new List<AreaPathPoint>();
    public bool IsMapRoad => !string.IsNullOrEmpty(Name) &&
      (Name.StartsWith("map_road_", StringComparison.OrdinalIgnoreCase) || Name.Equals("flypath01", StringComparison.OrdinalIgnoreCase));
  }

  public sealed class AreaMapNote {
    public string Id { get; set; }
    public string Fqn { get; set; }
    public string Label { get; set; }
    // Resolved from the mpn.* template in the GOM after area.dat/mapnotes.not has been parsed.
    // Keeping this on the lightweight area record lets both the D3D world map and the WinForms
    // minimap use the same original SWTOR symbol without touching the DOM during rendering.
    public string Icon { get; set; }
    public string Condition { get; set; }
    public Dictionary<string, string> LocalizedName { get; set; }
    public long WonkaPackageId { get; set; }
    public ulong WonkaDestinationId { get; set; }
    public long AssetId { get; set; }
    public ulong MapLinkAreaId { get; set; }
    public long MapLinkMapNameSId { get; set; }
    public long MapLinkSubmapNameSId { get; set; }
    public Vector3 Position { get; set; }
    public Vector3 Rotation { get; set; }
    public List<string> Tags { get; } = new List<string>();
    public List<string> ParentTags { get; } = new List<string>();
  }

  public sealed class AreaMapPage {
    public long Guid { get; set; }
    public long SId { get; set; }
    public long ParentId { get; set; }
    public string MapName { get; set; }
    public string ImagePath { get; set; }
    public bool HasImage { get; set; }
    public Vector3 Min { get; set; }
    public Vector3 Max { get; set; }
    public Vector2 MiniMapMin { get; set; }
    public Vector2 MiniMapMax { get; set; }
    public float Volume { get { Vector3 d=Max-Min; return Math.Abs(d.X*d.Y*d.Z); } }
  }

  public sealed class AreaEnvironmentScheme {
    public string Name { get; set; }
    public Dictionary<string, string> Properties { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public string SkySceneRoom { get; set; }
    public string EnvironmentMap { get; set; }
    public string IlluminationMap { get; set; }
    public string ColorLookupTable { get; set; }
    public int EnvironmentMaterialIndex { get; set; } = -1;
    public float ClipDistance { get; set; } = 100f;
    public float ClipExtended { get; set; } = 100f;
    public Vector4 PointLightDirection { get; set; } = new Vector4(0, 1, 0, 0);
    public Vector4 DiffuseLightColor { get; set; } = new Vector4(1, 1, 1, 1);
    public Vector4 SideLightColor { get; set; } = new Vector4(.5f, .5f, .5f, 1);
    public Vector4 BackLightColor { get; set; } = new Vector4(0, 0, 0, 1);
    public Vector4 AmbientLightColor { get; set; } = new Vector4(0, 0, 0, 1);
    public Vector4 StaticDiffuseColor { get; set; } = new Vector4(1, 1, 1, 1);
    public Vector4 AdvancedLightingParams { get; set; } = new Vector4(1, 1, 1, 1);
    public Vector4 FogColor0 { get; set; } = new Vector4(.55f, .62f, .66f, 1);
    public Vector4 FogColor1 { get; set; } = new Vector4(.55f, .62f, .66f, 1);
    public Vector4 FogColorSky { get; set; } = new Vector4(.55f, .62f, .66f, 1);
    public Vector4 FogAttenuation { get; set; } = new Vector4(0, 0, 10, 30);
    public Vector2 FogColorAttenuation { get; set; } = new Vector2(5, 15);
    public bool IsTwoColorFog { get; set; }
    public bool CastDirectionalShadows { get; set; }
    public bool ScrollingLightEnabled { get; set; }
    public string ScrollingTexture { get; set; }
    public string ScrollingMask { get; set; }
    public Vector4 ScrollingVisualParams { get; set; } = new Vector4(1, 1, 1, 0);
    public Vector4 ScrollingSpeeds { get; set; }

    public void FinalizeValues() {
      SkySceneRoom = Value("skyscene_room", SkySceneRoom)?.ToLowerInvariant();
      EnvironmentMap = Value("environment_map", EnvironmentMap);
      IlluminationMap = Value("illumination_map", IlluminationMap);
      ColorLookupTable = Value("color_lookup_table", ColorLookupTable);
      EnvironmentMaterialIndex = (int)Number("envmaterial_index", EnvironmentMaterialIndex);
      ClipDistance = Number("static_clip_distance", 100);
      ClipExtended = Number("static_clip_extended", 100);

      float azimuth = Number("light_azimuth", 0) * (float)Math.PI / 180f;
      float polar = Number("light_polar", 180) * (float)Math.PI / 180f;
      float sinPolar = (float)Math.Sin(polar);
      Vector3 light = new Vector3(-sinPolar * (float)Math.Cos(azimuth), -(float)Math.Cos(polar), -sinPolar * (float)Math.Sin(azimuth));
      if (light.LengthSquared() < 0.000001f) light = new Vector3(0, 1, 0);
      light.Normalize();
      PointLightDirection = new Vector4(light, 0);

      float intensity = Number("static_light_intensity", 1);
      Vector4 front = Color("static_light_front", new Vector4(1, 1, 1, 1));
      Vector4 side = Color("static_light_side", new Vector4(.5f, .5f, .5f, 1));
      Vector4 back = Color("static_light_back", new Vector4(0, 0, 0, 1));
      Vector4 ambient = Color("static_light_ambient", new Vector4(0, 0, 0, 1));
      Vector4 diffuse = Color("static_light_diffuse", new Vector4(1, 1, 1, 1));
      DiffuseLightColor = front; // Jedipedia deliberately does not apply static_light_intensity to the ramp light.
      SideLightColor = ScaleRgb(side, intensity);
      BackLightColor = ScaleRgb(back, intensity);
      AmbientLightColor = ScaleRgb(ambient, intensity);
      StaticDiffuseColor = ScaleRgb(diffuse, intensity);
      float rim = Number("rimlight_intensity", 1);
      AdvancedLightingParams = new Vector4(rim, rim, Number("skyspec_intensity", 1), 1);

      List<Vector4> fog = ColorList(Value("static_fog_colors", null));
      FogColor0 = fog.Count > 0 ? fog[0] : new Vector4(.55f, .62f, .66f, 1);
      FogColor1 = fog.Count > 1 ? fog[1] : FogColor0;
      FogColorSky = Color("static_fog_color_sky", FogColor0);
      IsTwoColorFog = fog.Count > 1;
      float fogMin = Number("static_fog_min", 0), fogMax = Number("static_fog_max", 0);
      float fogStart = Number("static_fog_start", 10), fogRamp = Math.Max(.0001f, Number("static_fog_ramp", 30));
      float colorStart = Number("static_fog_colors_start", 5), colorEnd = Number("static_fog_colors_end", 20);
      FogAttenuation = new Vector4(fogMin, Math.Max(0, fogMax - fogMin), fogStart, fogRamp);
      FogColorAttenuation = new Vector2(colorStart, Math.Max(.0001f, colorEnd - colorStart));
      CastDirectionalShadows = Bool("cast_directional_shadows", false);

      ScrollingLightEnabled = Bool("enable_scrolling_light_textures", false);
      ScrollingTexture = Value("texture_scrolling_map", null);
      ScrollingMask = Value("mask_scrolling_map", null);
      ScrollingVisualParams = new Vector4(Math.Max(.0001f, Number("texture_tile_rate", 1)), Math.Max(.0001f, Number("mask_tile_rate", 1)), 1, 0);
      ScrollingSpeeds = new Vector4(Number("texture_u_scroll_speed", 0), Number("texture_v_scroll_speed", 0), Number("mask_u_scroll_speed", 0), Number("mask_v_scroll_speed", 0));
    }

    private string Value(string key, string fallback) => Properties.TryGetValue(key, out string v) ? v : fallback;
    private float Number(string key, float fallback) => float.TryParse(Value(key, null), NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : fallback;
    private bool Bool(string key, bool fallback) => bool.TryParse(Value(key, null), out bool v) ? v : fallback;
    private Vector4 Color(string key, Vector4 fallback) => ParseColor(Value(key, null), fallback);
    private static Vector4 ScaleRgb(Vector4 v, float f) => new Vector4(v.X * f, v.Y * f, v.Z * f, v.W);
    private static Vector4 ParseColor(string value, Vector4 fallback) {
      if (string.IsNullOrWhiteSpace(value)) return fallback;
      string[] p = value.TrimStart('#').Split(',');
      if (p.Length < 3) return fallback;
      if (!float.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x) ||
          !float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y) ||
          !float.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float z)) return fallback;
      float w = p.Length > 3 && float.TryParse(p[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float a) ? a : fallback.W;
      return new Vector4(x, y, z, w);
    }
    private static List<Vector4> ColorList(string value) {
      var result = new List<Vector4>();
      if (string.IsNullOrEmpty(value)) return result;
      foreach (string part in value.Split('|')) result.Add(ParseColor(part, new Vector4(float.NaN, float.NaN, float.NaN, float.NaN)));
      result.RemoveAll(v => float.IsNaN(v.X));
      return result;
    }
  }

  public class Area {
    private static readonly Regex PathParser = new Regex(@"\\(?:([A-Za-z]):)?(.+)\.([^\s]+)", RegexOptions.Compiled);
    private readonly Assets assets;
    private TorArchive.File File { get; set; }
    public ulong Id;
    public string Path;
    public Dictionary<ulong, AreaAsset> AssetIdMap { get; set; } = new Dictionary<ulong, AreaAsset>();
    public Dictionary<string, List<AreaAsset>> AssetsByExtension { get; set; } = new Dictionary<string, List<AreaAsset>>(StringComparer.OrdinalIgnoreCase);
    public List<Room> RoomList { get; set; } = new List<Room>();
    public Dictionary<uint, string> TerrainTextureNames { get; } = new Dictionary<uint, string>();
    public List<AreaTerrainTexture> TerrainTextures { get; } = new List<AreaTerrainTexture>();
    public Dictionary<uint, AreaDynamicDetailTexture> DynamicDetailTextures { get; } = new Dictionary<uint, AreaDynamicDetailTexture>();
    public Dictionary<uint, AreaDynamicDetailChannelParam> DynamicDetailChannelParams { get; } = new Dictionary<uint, AreaDynamicDetailChannelParam>();
    public Dictionary<string, AreaEnvironmentScheme> EnvironmentSchemes { get; } = new Dictionary<string, AreaEnvironmentScheme>(StringComparer.OrdinalIgnoreCase);
    public List<AreaEnvironmentMaterial> EnvironmentMaterials { get; } = new List<AreaEnvironmentMaterial>();
    public List<AreaPath> Paths { get; } = new List<AreaPath>();
    public List<AreaMapNote> MapNotes { get; } = new List<AreaMapNote>();
    public List<AreaMapPage> MapPages { get; } = new List<AreaMapPage>();
    public AreaPathPoint ArrivalPoint { get; private set; }
    public int SkyRotation { get; private set; }
    public ulong DefaultSkySceneAssetId { get; private set; }
    public string InternalName { get; private set; }
    public string DebugHeaderInfo { get; private set; }

    private uint roomsOffset, assetsOffset, pathsOffset, schemesOffset, terrainTexOffset, dynDetailTexOffset, dydChnlParamsOffset, settingsOffset, guidOffset;

    public Area(HashFileInfo info, Assets assets, ulong areaId) {
      this.assets = assets;
      File = info.File ?? throw new ArgumentNullException(nameof(info), "File cannot be null");
      Id = areaId;
      string filePath = File.FilePath;
      if (!string.IsNullOrEmpty(filePath)) {
        int slash = filePath.LastIndexOf('/');
        Path = slash >= 0 ? filePath.Substring(0, slash) : filePath;
      } else Path = info.Directory;
    }

    public TorArchive.File FindFile(string path) => assets?.FindFile(path);

    public AreaEnvironmentScheme GetEnvironmentScheme(string name) {
      if (!string.IsNullOrWhiteSpace(name) && EnvironmentSchemes.TryGetValue(name, out AreaEnvironmentScheme exact)) return exact;
      if (EnvironmentSchemes.TryGetValue("area", out AreaEnvironmentScheme area)) return area;
      return EnvironmentSchemes.Values.FirstOrDefault() ?? new AreaEnvironmentScheme { Name = "area" };
    }

    public AreaEnvironmentMaterial GetEnvironmentMaterial(Room room, AssetInstance instance) {
      int index = instance != null && instance.EnvironmentMaterialIndex > 0 ? instance.EnvironmentMaterialIndex - 1 : (room?.EnvironmentScheme?.EnvironmentMaterialIndex ?? GetEnvironmentScheme("area").EnvironmentMaterialIndex);
      if (index < 0) return null;
      AreaEnvironmentMaterial byId = EnvironmentMaterials.FirstOrDefault(x => x.Id == (uint)index);
      if (byId != null) return byId;
      return index < EnvironmentMaterials.Count ? EnvironmentMaterials[index] : null;
    }

    public string ResolveTerrainTextureName(sbyte textureIndex) {
      // Heightmap records store the area.dat terrain-texture ID in one signed byte.
      // Jedipedia indexes the area terrainTextures table by that ID. Preserve the raw 8-bit
      // value first (IDs 128..255 arrive here as negative sbytes), then tolerate old files
      // that used the ordinal position instead. -1 is the engine's "no texture" sentinel.
      if (textureIndex == -1) return null;
      uint id = unchecked((byte)textureIndex);
      if (TerrainTextureNames.TryGetValue(id, out string direct) && !string.IsNullOrWhiteSpace(direct))
        return NormalizeTerrainName(direct);
      int ordinal = textureIndex;
      if (ordinal >= 0 && ordinal < TerrainTextures.Count && !string.IsNullOrWhiteSpace(TerrainTextures[ordinal].Name))
        return NormalizeTerrainName(TerrainTextures[ordinal].Name);
      // Some early tools treated the signed byte as an ordinal after it had wrapped through 128. This is not
      // Jedipedia's primary lookup, but accepting the raw byte here is harmless and prevents a checker fallback
      // on those legacy tables when the direct ID is absent.
      int rawOrdinal = unchecked((byte)textureIndex);
      if (rawOrdinal < TerrainTextures.Count && !string.IsNullOrWhiteSpace(TerrainTextures[rawOrdinal].Name))
        return NormalizeTerrainName(TerrainTextures[rawOrdinal].Name);
      return null;
    }

    public void Read() {
      using Stream stream = File.OpenCopyInMemory();
      if (stream.Length < 4) throw new InvalidDataException("area.dat is truncated.");
      using BinaryReader br = new BinaryReader(stream, Encoding.UTF8, true);
      uint signature = br.ReadUInt32();
      br.BaseStream.Position = 0;
      if (signature == 0x18) ReadBinary(br);
      else ReadText(br);
    }

    private void ReadBinary(BinaryReader br) {
      _ = br.ReadUInt32();
      br.BaseStream.Position = 0x1C;
      roomsOffset = br.ReadUInt32(); assetsOffset = br.ReadUInt32(); pathsOffset = br.ReadUInt32(); schemesOffset = br.ReadUInt32();
      terrainTexOffset = br.ReadUInt32(); dynDetailTexOffset = br.ReadUInt32(); dydChnlParamsOffset = br.ReadUInt32(); settingsOffset = br.ReadUInt32(); guidOffset = br.ReadUInt32();
      DebugHeaderInfo = string.Format(CultureInfo.InvariantCulture, "binary rooms=0x{0:X} assets=0x{1:X} paths=0x{2:X} schemes=0x{3:X} terrain=0x{4:X} settings=0x{5:X}", roomsOffset, assetsOffset, pathsOffset, schemesOffset, terrainTexOffset, settingsOffset);
      // Room vertex payloads are decoded immediately while ReadRooms() walks the room DATs. Those payloads
      // need BOTH lookup tables below already populated.
      ReadAssets(br);
      ReadTerrainTextures(br);
      ReadRooms(br);
      ReadPaths(br);
      ReadEnvironmentSchemes(br);
      ReadDynamicDetailTextures(br);
      ReadDynamicDetailChannelParams(br);
      ReadSettings(br);
      FinalizeEnvironmentSchemes();
      ReadMapNotes();
    }

    // Text area.dat was used by all client builds before the binary 0x18 format (pre-3.3.2).
    // This mirrors Jedipedia's dat_area_text reader and populates the same Area model as ReadBinary.
    private void ReadText(BinaryReader br) {
      br.BaseStream.Position = 0;
      byte[] bytes = br.ReadBytes(checked((int)br.BaseStream.Length));
      string text = DecodeTextFile(bytes);
      string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
      string section = String.Empty;
      var roomNames = new List<string>();
      DebugHeaderInfo = "legacy text area.dat";

      foreach (string rawLine in lines) {
        string line = (rawLine ?? String.Empty).Trim();
        if (line.Length == 0 || line == "!" || line.StartsWith("! Area Specification", StringComparison.OrdinalIgnoreCase) || line.StartsWith("Version=", StringComparison.OrdinalIgnoreCase)) continue;
        if (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal)) { section = line.ToUpperInvariant(); continue; }

        switch (section) {
          case "[ROOMS]":
            roomNames.Add(line);
            break;
          case "[ASSETS]": {
            int eq=line.IndexOf('='); if(eq<=0) break;
            if (UInt64.TryParse(line.Substring(0,eq).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong id))
              AddAreaAsset(id,line.Substring(eq+1).Trim());
            break;
          }
          case "[PATHS]": ReadTextPath(line); break;
          case "[SCHEMES]": {
            Match m=Regex.Match(line,"^\"([^\"]+)\"\\s+(~0\\|.*)$");
            if(m.Success) EnvironmentSchemes[m.Groups[1].Value.ToLowerInvariant()]=ParseEnvironmentScheme(m.Groups[1].Value.ToLowerInvariant(),m.Groups[2].Value);
            break;
          }
          case "[TERRAINTEXTURES]": ReadTextTerrainTexture(line); break;
          case "[DYDTEXTURES]": {
            int colon=line.IndexOf(':'); if(colon<=0) break;
            if(UInt32.TryParse(line.Substring(0,colon),NumberStyles.Integer,CultureInfo.InvariantCulture,out uint id))
              DynamicDetailTextures[id]=new AreaDynamicDetailTexture{Id=id,Name=line.Substring(colon+1).Trim()};
            break;
          }
          case "[DYDCHANNELPARAMS]": ReadTextDynamicChannel(line); break;
          case "[SETTINGS]": ReadTextSetting(line); break;
        }
      }

      // Text areas do not carry the binary environment-material table. Schemes and terrain
      // must be ready before rooms because old room payloads can reference them during load.
      ResolveTextArrivalPoint();
      FinalizeEnvironmentSchemes();
      foreach(string rawName in roomNames) {
        string name=(rawName??String.Empty).Trim().Replace('\\','/').Replace(".dat",String.Empty,StringComparison.OrdinalIgnoreCase).ToLowerInvariant();
        if(String.IsNullOrWhiteSpace(name)) continue;
        TorArchive.File roomFile=assets.FindFile(Path+"/"+name+".dat");
        if(roomFile==null){System.Diagnostics.Debug.WriteLine("Cannot find legacy area room file: "+Path+"/"+name+".dat");continue;}
        Room room=new Room(roomFile,name,this); room.Read(); RoomList.Add(room);
      }
      ReadMapNotes();
    }

    private static string ReadFixed(BinaryReader br, uint length, bool unicode = false) {
      byte[] bytes = br.ReadBytes(checked((int)length));
      Encoding enc = unicode ? Encoding.Unicode : Encoding.UTF8;
      return enc.GetString(bytes).TrimEnd('\0');
    }


    private static string DecodeTextFile(byte[] bytes) {
      if (bytes == null || bytes.Length == 0) return String.Empty;
      if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode.GetString(bytes,2,bytes.Length-2).TrimEnd('\0');
      if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(bytes,2,bytes.Length-2).TrimEnd('\0');
      if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return Encoding.UTF8.GetString(bytes,3,bytes.Length-3).TrimEnd('\0');
      return Encoding.UTF8.GetString(bytes).TrimEnd('\0');
    }

    private void AddAreaAsset(ulong id,string raw) {
      raw=(raw??String.Empty).Trim(); Match m=PathParser.Match(raw);
      string path,ext,encounter=null;
      if(m.Success){encounter=m.Groups[1].Value;path=m.Groups[2].Value;ext=m.Groups[3].Value;}
      else{string normalized=raw.TrimStart('\\','/').Replace('\\','/');int dot=normalized.LastIndexOf('.');path=dot>0?normalized.Substring(0,dot):normalized;ext=dot>0?normalized.Substring(dot+1):String.Empty;}
      // Authored text assets can include a leading "resources/" while binary tables normally do not.
      path=(path??String.Empty).Replace('\\','/').TrimStart('/');
      if(path.StartsWith("resources/",StringComparison.OrdinalIgnoreCase))path=path.Substring("resources/".Length);
      var asset=new AreaAsset{Id=id,Path=path,Extension=(ext??String.Empty).TrimStart('.')};
      if(!String.IsNullOrEmpty(encounter))asset.EncounterIndex=encounter.ToLowerInvariant();
      AssetIdMap[id]=asset;
      if(!AssetsByExtension.TryGetValue(asset.Extension,out List<AreaAsset> list))AssetsByExtension[asset.Extension]=list=new List<AreaAsset>();
      list.Add(asset);
    }

    private void ReadTextPath(string line) {
      if(line.StartsWith("addpath",StringComparison.OrdinalIgnoreCase)){
        // Match Jedipedia's pre-3.3.2 parser: colour/FQN may be empty and the trailing
        // Circular/Cardinal booleans are optional in the oldest authored files.
        Match m=Regex.Match(line,"^addpath\\s+\\\"([0-9]+)\\\"\\s+\\\"([^\\\"]*)\\\"\\s+\\\"([^\\\"]*)\\\"\\s+\\\"([^\\\"]*)\\\"(?:\\s+\\\"(true|false)\\\"\\s+\\\"(true|false)\\\")?",RegexOptions.IgnoreCase);
        if(!m.Success)return;
        if(!UInt64.TryParse(m.Groups[1].Value,NumberStyles.Integer,CultureInfo.InvariantCulture,out ulong id))return;
        var path=new AreaPath{Id=id,Name=m.Groups[2].Value,Fqn=m.Groups[4].Value,Circular=String.Equals(m.Groups[5].Value,"true",StringComparison.OrdinalIgnoreCase),Smooth=String.Equals(m.Groups[6].Value,"true",StringComparison.OrdinalIgnoreCase)};
        string color=m.Groups[3].Value.TrimStart('#');string[] cp=color.Split(',');if(cp.Length==4)path.Color=new Vector4(ParseF(cp[0]),ParseF(cp[1]),ParseF(cp[2]),ParseF(cp[3]));
        Paths.Add(path);return;
      }
      if(line.StartsWith("addpoint",StringComparison.OrdinalIgnoreCase)){
        Match m=Regex.Match(line,"^addpoint\\s+\\\"([0-9]+)\\\"\\s+\\\"([0-9]+)\\\"\\s+\\(([^)]*)\\)\\s+\\(([^)]*)\\)(?:\\s+\\\"([^\\\"]*)\\\")?",RegexOptions.IgnoreCase);if(!m.Success)return;
        if(!UInt64.TryParse(m.Groups[1].Value,NumberStyles.Integer,CultureInfo.InvariantCulture,out ulong pathId)||!UInt64.TryParse(m.Groups[2].Value,NumberStyles.Integer,CultureInfo.InvariantCulture,out ulong pointId))return;
        AreaPath path=Paths.FirstOrDefault(x=>x.Id==pathId);if(path==null)return;
        string data=m.Groups[5].Success?m.Groups[5].Value:String.Empty;
        path.Points.Add(new AreaPathPoint{PathId=pathId,PointId=pointId,Position=ParseTextVec3(m.Groups[3].Value),Rotation=ParseTextVec3(m.Groups[4].Value),Data=data,Tension=PathNumber(data,"tension",.75f),Speed=PathNumber(data,"speed",1f),HoldTime=PathNumber(data,"holdtime",0f)});
      }
    }

    private void ReadTextTerrainTexture(string line) {
      int colon=line.IndexOf(':');if(colon<=0||!UInt32.TryParse(line.Substring(0,colon),NumberStyles.Integer,CultureInfo.InvariantCulture,out uint id))return;
      string payload=line.Substring(colon+1).Trim(),name=null;int layer=0;
      Match bwa=Regex.Match(payload,"^bwa:([A-Za-z0-9_]+):(-?[0-9]+)$",RegexOptions.IgnoreCase);
      Match simple=Regex.Match(payload,"^([A-Za-z0-9_]+)(?::(-?[0-9]+))?$");
      Match path=Regex.Match(payload,"^(.+?)(?:::(-?[0-9]+):(-?[0-9]+))?$");
      if(bwa.Success){name=bwa.Groups[1].Value;Int32.TryParse(bwa.Groups[2].Value,out layer);}
      else if(simple.Success){name=simple.Groups[1].Value;if(simple.Groups[2].Success)Int32.TryParse(simple.Groups[2].Value,out layer);}
      else if(path.Success){name=path.Groups[1].Value;if(path.Groups[2].Success)Int32.TryParse(path.Groups[2].Value,out layer);}
      name=NormalizeTerrainName(name);TerrainTextureNames[id]=name;TerrainTextures.Add(new AreaTerrainTexture{Id=id,Layer=unchecked((uint)Math.Max(0,layer)),Name=name});
    }

    private void ReadTextDynamicChannel(string line) {
      int colon=line.IndexOf(':');if(colon<=0||!UInt32.TryParse(line.Substring(0,colon),NumberStyles.Integer,CultureInfo.InvariantCulture,out uint rawId))return;
      string payload=line.Substring(colon+1);
      if(rawId<2000){string[] parts=payload.Split(':');var values=new uint[parts.Length];for(int i=0;i<parts.Length;i++){if(String.Equals(parts[i],"false",StringComparison.OrdinalIgnoreCase))values[i]=0;else UInt32.TryParse(parts[i],NumberStyles.Integer,CultureInfo.InvariantCulture,out values[i]);}DynamicDetailChannelParams[rawId]=new AreaDynamicDetailChannelParam{Id=rawId,Type=0,Values=values};}
      else if(rawId<=2999){Int32.TryParse(payload,NumberStyles.Integer,CultureInfo.InvariantCulture,out int value);uint id=rawId-2000;DynamicDetailChannelParams[id]=new AreaDynamicDetailChannelParam{Id=id,Type=2,IntValue=value};}
      else if(rawId<=3999){uint id=rawId-3000;DynamicDetailChannelParams[id]=new AreaDynamicDetailChannelParam{Id=id,Type=3,StringValue=payload,Floats=Array.Empty<float>()};}
    }

    private void ReadTextSetting(string line) {
      int eq=line.IndexOf('=');if(eq<=0)return;string key=line.Substring(0,eq).Trim(),value=line.Substring(eq+1).Trim();
      if(key.Equals("AreaGUID",StringComparison.OrdinalIgnoreCase)){if(TryParseTextId(value,out ulong areaId))Id=areaId;}
      else if(key.Equals("AreaDisplayName",StringComparison.OrdinalIgnoreCase))InternalName=value;
      else if(key.Equals("SkyRotation",StringComparison.OrdinalIgnoreCase)){if(Int32.TryParse(value,NumberStyles.Integer,CultureInfo.InvariantCulture,out int rot))SkyRotation=rot;}
      else if(key.Equals("SkyDome",StringComparison.OrdinalIgnoreCase)){if(TryParseTextId(value,out ulong sky))DefaultSkySceneAssetId=sky;}
    }

    private static bool TryParseTextId(string value,out ulong id) {
      id=0; string text=(value??String.Empty).Trim().Trim('"');
      if(UInt64.TryParse(text,NumberStyles.Integer,CultureInfo.InvariantCulture,out id))return true;
      int split=text.IndexOf('|');
      if(split>0&&UInt32.TryParse(text.Substring(0,split),NumberStyles.Integer,CultureInfo.InvariantCulture,out uint low)&&UInt32.TryParse(text.Substring(split+1),NumberStyles.Integer,CultureInfo.InvariantCulture,out uint high)){id=((ulong)high<<32)|low;return true;}
      return false;
    }

    private void ResolveTextArrivalPoint() {
      int best=int.MaxValue;
      string[] names={"arrival","arrival_jedi_knight","arrival_jedi_wizard","arrival_smuggler","arrival_trooper","arrival_sith_warrior","arrival_sith_sorcerer","arrival_spy","arrival_bounty_hunter"};
      foreach(AreaPath p in Paths){
        if(p==null||p.Points.Count==0||String.IsNullOrWhiteSpace(p.Name))continue;
        int rank=Array.FindIndex(names,n=>n.Equals(p.Name,StringComparison.OrdinalIgnoreCase));
        if(rank>=0&&rank<best){best=rank;ArrivalPoint=p.Points[0];}
      }
    }

    private static Vector3 ParseTextVec3(string value) {
      string[] p=(value??String.Empty).Trim().Trim('(',')').Split(',');if(p.Length<3)return Vector3.Zero;return new Vector3(ParseF(p[0]),ParseF(p[1]),ParseF(p[2]));
    }

    private void ReadRooms(BinaryReader br) {
      br.BaseStream.Position = roomsOffset;
      uint count = br.ReadUInt32();
      for (int i = 0; i < count; i++) {
        uint len = br.ReadUInt32(); string name = ReadFixed(br, len).Trim().ToLowerInvariant();
        TorArchive.File roomFile = assets.FindFile(Path + "/" + name + ".dat");
        if (roomFile == null) { System.Diagnostics.Debug.WriteLine("Cannot find area room file: " + Path + "/" + name + ".dat"); continue; }
        Room room = new Room(roomFile, name, this); room.Read(); RoomList.Add(room);
      }
    }

    private void ReadAssets(BinaryReader br) {
      br.BaseStream.Position = assetsOffset;
      uint count = br.ReadUInt32();
      for (int i = 0; i < count; i++) {
        ulong id = br.ReadUInt64(); uint len = br.ReadUInt32(); string raw = ReadFixed(br, len).Trim();
        AddAreaAsset(id, raw);
      }
    }

    private void ReadPaths(BinaryReader br) {
      br.BaseStream.Position = pathsOffset;
      if (pathsOffset >= schemesOffset) return;
      uint count = br.ReadUInt32();
      int arrivalRank = int.MaxValue;
      string[] arrivals = { "arrival", "arrival_jedi_knight", "arrival_jedi_wizard", "arrival_smuggler", "arrival_trooper", "arrival_sith_warrior", "arrival_sith_sorcerer", "arrival_spy", "arrival_bounty_hunter" };
      for (int i = 0; i < count && br.BaseStream.Position < schemesOffset; i++) {
        var p = new AreaPath { Id = br.ReadUInt64() };
        p.Name = ReadFixed(br, br.ReadUInt32()); p.Fqn = ReadFixed(br, br.ReadUInt32());
        p.Color = new Vector4(br.ReadSingle(), br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
        p.Circular = br.ReadBoolean(); p.Smooth = br.ReadBoolean();
        uint points = br.ReadUInt32();
        for (int j = 0; j < points; j++) {
          var pp = new AreaPathPoint { PathId = br.ReadUInt64(), PointId = br.ReadUInt64() };
          pp.Position = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
          pp.Rotation = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
          pp.Data = ReadFixed(br, br.ReadUInt32());
          pp.Tension = PathNumber(pp.Data, "tension", .75f); pp.Speed = Math.Max(.01f, PathNumber(pp.Data, "speed", 1)); pp.HoldTime = Math.Max(0, PathNumber(pp.Data, "holdtime", 0));
          p.Points.Add(pp);
        }
        Paths.Add(p);
        int rank = Array.FindIndex(arrivals, x => x.Equals(p.Name, StringComparison.OrdinalIgnoreCase));
        if (rank >= 0 && p.Points.Count > 0 && rank < arrivalRank) { arrivalRank = rank; ArrivalPoint = p.Points[0]; }
      }
    }

    private static float PathNumber(string data, string key, float fallback) {
      Match m = Regex.Match(data ?? string.Empty, "(?:^|;)\\s*" + Regex.Escape(key) + "\\s*=\\s*(-?[0-9.eE+-]+)", RegexOptions.IgnoreCase);
      return m.Success && float.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : fallback;
    }

    private void ReadEnvironmentSchemes(BinaryReader br) {
      br.BaseStream.Position = schemesOffset;
      uint count = br.ReadUInt32();
      for (int i = 0; i < count; i++) {
        string name = ReadFixed(br, br.ReadUInt32()).ToLowerInvariant(); string data = ReadFixed(br, br.ReadUInt32());
        EnvironmentSchemes[name] = ParseEnvironmentScheme(name, data);
      }
    }

    private static AreaEnvironmentScheme ParseEnvironmentScheme(string name, string data) {
      var scheme = new AreaEnvironmentScheme { Name = name };
      if (string.IsNullOrEmpty(data)) return scheme;
      try {
        int pos = 3; int count = ReadPipeInt(data, ref pos);
        for (int i = 0; i < count && pos < data.Length; i++) {
          int keyLen = ReadPipeInt(data, ref pos); if (pos + keyLen > data.Length) break;
          string key = data.Substring(pos, keyLen).ToLowerInvariant(); pos += keyLen; if (pos < data.Length && data[pos] == '|') pos++;
          int valLen = ReadPipeInt(data, ref pos); if (pos + valLen > data.Length) break;
          string value = data.Substring(pos, valLen); pos += valLen; if (pos < data.Length && data[pos] == '|') pos++;
          scheme.Properties[key] = value;
        }
      } catch { }
      return scheme;
    }

    private static int ReadPipeInt(string data, ref int pos) {
      int v = 0; while (pos < data.Length && data[pos] != '|') { if (char.IsDigit(data[pos])) v = v * 10 + (data[pos] - '0'); pos++; } if (pos < data.Length) pos++; return v;
    }

    private void ReadTerrainTextures(BinaryReader br) {
      br.BaseStream.Position = terrainTexOffset;
      uint count = br.ReadUInt32();
      for (int i = 0; i < count; i++) {
        uint id = br.ReadUInt32(), layer = br.ReadUInt32(); string name = NormalizeTerrainName(ReadFixed(br, br.ReadUInt32()));
        TerrainTextureNames[id] = name; TerrainTextures.Add(new AreaTerrainTexture { Id = id, Layer = layer, Name = name });
      }
    }

    private void ReadDynamicDetailTextures(BinaryReader br) {
      if (dynDetailTexOffset == 0 || dynDetailTexOffset >= br.BaseStream.Length) return;
      br.BaseStream.Position = dynDetailTexOffset;
      uint count = br.ReadUInt32();
      for (uint i=0; i<count && br.BaseStream.Position+8<=br.BaseStream.Length; i++) {
        uint id=br.ReadUInt32(); uint len=br.ReadUInt32();
        if (len > br.BaseStream.Length-br.BaseStream.Position) break;
        string name=ReadFixed(br,len).Trim();
        DynamicDetailTextures[id]=new AreaDynamicDetailTexture{Id=id,Name=name};
      }
    }

    private void ReadDynamicDetailChannelParams(BinaryReader br) {
      if (dydChnlParamsOffset == 0 || dydChnlParamsOffset >= br.BaseStream.Length) return;
      br.BaseStream.Position = dydChnlParamsOffset;
      uint count=br.ReadUInt32();
      for (uint i=0;i<count && br.BaseStream.Position+8<=br.BaseStream.Length;i++) {
        uint type=br.ReadUInt32(), id=br.ReadUInt32();
        var param=new AreaDynamicDetailChannelParam{Type=type,Id=id};
        if (type==0) {
          if (br.BaseStream.Position+60>br.BaseStream.Length) break;
          param.Values=new uint[15]; for(int j=0;j<15;j++)param.Values[j]=br.ReadUInt32();
        } else if (type==2) {
          if (br.BaseStream.Position+4>br.BaseStream.Length) break; param.IntValue=br.ReadInt32();
        } else if (type==3) {
          if (br.BaseStream.Position+4>br.BaseStream.Length) break; uint len=br.ReadUInt32();
          if (len>br.BaseStream.Length-br.BaseStream.Position) break; param.StringValue=ReadFixed(br,len);
          if (br.BaseStream.Position+28>br.BaseStream.Length) break; param.Floats=new float[7]; for(int j=0;j<7;j++)param.Floats[j]=br.ReadSingle();
          if (br.BaseStream.Position+4>br.BaseStream.Length) break; uint materialCount=br.ReadUInt32();
          // Type-3 parameters are not currently rendered, but their nested material records must be skipped exactly.
          for(uint m=0;m<materialCount;m++) {
            if (br.BaseStream.Position+12>br.BaseStream.Length) { br.BaseStream.Position=br.BaseStream.Length; break; }
            br.ReadUInt32(); br.ReadSingle(); uint materialNameLength=br.ReadUInt32();
            if (materialNameLength>br.BaseStream.Length-br.BaseStream.Position) { br.BaseStream.Position=br.BaseStream.Length; break; }
            br.BaseStream.Position += materialNameLength;
            const int tail=1+6*4+1+4+4+1;
            if (br.BaseStream.Position+tail>br.BaseStream.Length) { br.BaseStream.Position=br.BaseStream.Length; break; }
            br.BaseStream.Position += tail;
          }
        } else {
          // Unknown types have no documented size. Stop rather than desynchronising every following record.
          break;
        }
        DynamicDetailChannelParams[id]=param;
      }
    }

    private void ReadSettings(BinaryReader br) {
      br.BaseStream.Position = settingsOffset;
      uint count = br.ReadUInt32();
      for (int i = 0; i < count; i++) {
        uint id = br.ReadUInt32(); string name = ReadFixed(br, br.ReadUInt32()).Trim();
        var entry = new AreaEnvironmentMaterial { Id = id, Name = name };
        ReadEnvironmentMaterialDefinition(entry); EnvironmentMaterials.Add(entry);
      }
      if (br.BaseStream.Position + 8 <= br.BaseStream.Length) br.ReadUInt64(); // area id
      if (br.BaseStream.Position + 4 <= br.BaseStream.Length) { uint nameLen = br.ReadUInt32(); if (br.BaseStream.Position + nameLen <= br.BaseStream.Length) InternalName = ReadFixed(br, nameLen).Trim(); }
      if (br.BaseStream.Position + 12 <= br.BaseStream.Length) { SkyRotation = br.ReadInt32(); DefaultSkySceneAssetId = br.ReadUInt64(); }
    }

    private void ReadEnvironmentMaterialDefinition(AreaEnvironmentMaterial entry) {
      if (string.IsNullOrWhiteSpace(entry.Name)) return;
      TorArchive.File file = assets.FindFile("/resources/art/shaders/environmentmaterials/" + entry.Name.ToLowerInvariant() + ".emt");
      if (file == null) return;
      try {
        using Stream s = file.OpenCopyInMemory(); using var ms = new MemoryStream(); s.CopyTo(ms); byte[] bytes = ms.ToArray();
        string xml = bytes.Length > 1 && bytes[1] == 0 ? Encoding.Unicode.GetString(bytes) : Encoding.UTF8.GetString(bytes);
        XmlDocument doc = new XmlDocument(); doc.LoadXml(xml.Trim('\0', '\uFEFF', ' ', '\r', '\n', '\t'));
        XmlElement root = doc.DocumentElement; if (root == null) return;
        if (bool.TryParse(root.GetAttribute("enableCreviceMap"), out bool crevice)) entry.EnableCreviceMap = crevice;
        entry.BlendDiffuse = root.SelectSingleNode("//BlendDiffuse")?.InnerText;
        entry.BlendNormal = root.SelectSingleNode("//BlendNormal")?.InnerText;
        entry.EnvBlendParams1 = ParseVec4(root.SelectSingleNode("//EnvBlendParams1")?.InnerText, entry.EnvBlendParams1);
        entry.EnvBlendParams2 = ParseVec4(root.SelectSingleNode("//EnvBlendParams2")?.InnerText, entry.EnvBlendParams2);
      } catch { }
    }

    private static Vector4 ParseVec4(string text, Vector4 fallback) {
      if (string.IsNullOrWhiteSpace(text)) return fallback; string[] p = text.Split(','); if (p.Length != 4) return fallback;
      float[] f = new float[4]; for (int i = 0; i < 4; i++) if (!float.TryParse(p[i], NumberStyles.Float, CultureInfo.InvariantCulture, out f[i])) return fallback;
      return new Vector4(f[0], f[1], f[2], f[3]);
    }

    private void FinalizeEnvironmentSchemes() {
      EnvironmentSchemes.TryGetValue("area", out AreaEnvironmentScheme root);
      foreach (var pair in EnvironmentSchemes) {
        if (root != null && !ReferenceEquals(pair.Value, root)) foreach (var kv in root.Properties) if (!pair.Value.Properties.ContainsKey(kv.Key)) pair.Value.Properties[kv.Key] = kv.Value;
        pair.Value.FinalizeValues();
      }
      if (EnvironmentSchemes.Count == 0) { var d = new AreaEnvironmentScheme { Name = "area" }; d.FinalizeValues(); EnvironmentSchemes["area"] = d; }
    }

    private void ReadMapNotes() {
      // Mapnotes have existed in a few slightly different text wrappers over the lifetime of the client. The old
      // GomLib loader successfully parsed the complete XML document, while Jedipedia's current browser strips the
      // fixed archive wrapper and parses the regular <k>/<e><node> stream. Support both forms here instead of
      // assuming one exact wrapper/encoding: otherwise a valid mapnotes.not can silently produce an empty list.
      bool systemGenerated = Id == 36268000006UL || Id == 3758002374UL;
      string normalPath = "/resources/world/areas/" + Id + "/mapnotes.not";
      string generatedPath = "/resources/world/livecontent/systemgenerated/" + Id + "/mapnotes.not";
      var candidates = new List<string>();
      candidates.Add(systemGenerated ? generatedPath : normalPath);
      if (!String.IsNullOrWhiteSpace(Path)) candidates.Add(Path.TrimEnd('/', '\\') + "/mapnotes.not");
      candidates.Add(systemGenerated ? normalPath : generatedPath);

      foreach (string candidate in candidates.Where(x => !String.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase)) {
        TorArchive.File file = assets.FindFile(candidate);
        if (file == null) continue;
        try {
          byte[] bytes;
          using (Stream s = file.OpenCopyInMemory()) {
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            bytes = ms.ToArray();
          }

          string encoded = DecodeMapnoteFile(bytes);
          var parsed = new List<AreaMapNote>();

          // First try the full XML document. This is the format PugTools/GomLib historically consumed and is still
          // present in some client builds. It also avoids depending on a fixed prefix/suffix length.
          AddUniqueMapnotes(parsed, ParseMapnoteXml(encoded));

          // Then mirror Jedipedia's current parser for the fixed wrapper used by live archives. Keep a full-stream
          // regex fallback for malformed/legacy XML where XmlDocument is intentionally stricter than the client.
          if (encoded.Length > 48) {
            string wrapped = encoded.Substring(30, encoded.Length - 48);
            AddUniqueMapnotes(parsed, ParseMapnoteText(DecodeMapnoteText(wrapped)));
          }
          AddUniqueMapnotes(parsed, ParseMapnoteText(DecodeMapnoteText(encoded)));

          if (parsed.Count == 0) {
            System.Diagnostics.Debug.WriteLine("No mapnotes parsed from: " + candidate);
            continue;
          }

          MapNotes.AddRange(parsed);
          System.Diagnostics.Debug.WriteLine("Loaded " + parsed.Count.ToString(CultureInfo.InvariantCulture) + " mapnotes from: " + candidate);
          return;
        } catch (Exception ex) {
          System.Diagnostics.Debug.WriteLine("Could not read mapnotes from " + candidate + ": " + ex.Message);
        }
      }
    }

    private static string DecodeMapnoteFile(byte[] bytes) {
      if (bytes == null || bytes.Length == 0) return String.Empty;

      // StreamReader used by the original GomLib implementation auto-detected UTF BOMs. Preserve that behaviour,
      // and also recognize BOM-less UTF-16 by its alternating zero bytes before falling back to UTF-8.
      if (bytes.Length >= 2) {
        if (bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2).TrimEnd('\0');
        if (bytes[0] == 0xFE && bytes[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2).TrimEnd('\0');
      }
      if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3).TrimEnd('\0');

      int probe = Math.Min(bytes.Length, 256), evenZero = 0, oddZero = 0;
      for (int i = 0; i < probe; i++) if (bytes[i] == 0) { if ((i & 1) == 0) evenZero++; else oddZero++; }
      if (oddZero > probe / 8 && oddZero > evenZero * 2) return Encoding.Unicode.GetString(bytes).TrimEnd('\0');
      if (evenZero > probe / 8 && evenZero > oddZero * 2) return Encoding.BigEndianUnicode.GetString(bytes).TrimEnd('\0');
      return Encoding.UTF8.GetString(bytes).TrimEnd('\0');
    }

    private static void AddUniqueMapnotes(List<AreaMapNote> target, IEnumerable<AreaMapNote> source) {
      if (target == null || source == null) return;
      var seen = new HashSet<string>(target.Select(MapnoteIdentity), StringComparer.OrdinalIgnoreCase);
      foreach (AreaMapNote note in source) {
        if (note == null) continue;
        string key = MapnoteIdentity(note);
        if (seen.Add(key)) target.Add(note);
      }
    }

    private static string MapnoteIdentity(AreaMapNote note) {
      if (note == null) return String.Empty;
      if (!String.IsNullOrWhiteSpace(note.Id)) return "id:" + note.Id.Trim();
      return String.Format(CultureInfo.InvariantCulture, "{0}|{1:0.#####}|{2:0.#####}|{3:0.#####}", note.Fqn ?? String.Empty, note.Position.X, note.Position.Y, note.Position.Z);
    }

    private static List<AreaMapNote> ParseMapnoteXml(string text) {
      var result = new List<AreaMapNote>();
      if (String.IsNullOrWhiteSpace(text)) return result;

      foreach (string candidate in MapnoteXmlCandidates(text)) {
        try {
          var doc = new XmlDocument { PreserveWhitespace = false };
          doc.LoadXml(candidate);
          XmlNodeList valueNodes = doc.SelectNodes("//e[node]");
          if (valueNodes == null) continue;

          foreach (XmlNode valueNode in valueNodes) {
            XmlNode keyNode = valueNode.PreviousSibling;
            while (keyNode != null && keyNode.NodeType != XmlNodeType.Element) keyNode = keyNode.PreviousSibling;
            if (keyNode == null || !String.Equals(keyNode.Name, "k", StringComparison.OrdinalIgnoreCase)) continue;
            XmlNode node = valueNode.SelectSingleNode("./node");
            if (node == null) continue;

            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (XmlNode field in node.SelectNodes("./f")) {
              string name = field.Attributes?["name"]?.Value;
              if (!String.IsNullOrEmpty(name)) fields[name] = field.InnerXml;
            }
            AreaMapNote note = BuildMapnote(keyNode.InnerText, fields);
            if (note != null) result.Add(note);
          }
          if (result.Count > 0) return result;
        } catch (XmlException) { }
      }
      return result;
    }

    private static IEnumerable<string> MapnoteXmlCandidates(string text) {
      string raw = (text ?? String.Empty).Replace("\0", String.Empty).Trim('\uFEFF', ' ', '\r', '\n', '\t');
      if (raw.Length == 0) yield break;
      yield return raw;

      // Some TOR revisions encode the inner XML one extra time. The historical GomLib loader explicitly handled
      // both &lt; and &amp;lt;, so preserve that compatibility before falling back to the loose stream parser.
      string decoded = DecodeMapnoteText(DecodeMapnoteText(raw));
      if (!String.Equals(decoded, raw, StringComparison.Ordinal)) yield return decoded;

      if (raw.Length > 48) {
        string wrapped = DecodeMapnoteText(raw.Substring(30, raw.Length - 48)).Trim('\uFEFF', ' ', '\r', '\n', '\t');
        if (wrapped.Length > 0) {
          yield return wrapped;
          // A stripped stream can contain several sibling <k>/<e> pairs without one root element.
          yield return "<mapnotes>" + wrapped + "</mapnotes>";
        }
      }
    }

    private static List<AreaMapNote> ParseMapnoteText(string text) {
      var result = new List<AreaMapNote>();
      if (String.IsNullOrWhiteSpace(text)) return result;

      Regex entryRx = new Regex(@"<k\b[^>]*>(?<id>[\s\S]*?)</k>\s*<e\b[^>]*>\s*<node\b[^>]*>(?<node>[\s\S]*?)</node>\s*</e>", RegexOptions.IgnoreCase);
      foreach (Match entry in entryRx.Matches(text)) {
        var fields = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match f in Regex.Matches(entry.Groups["node"].Value, @"<f\b(?<attrs>[^>]*)>(?<value>[\s\S]*?)</f>", RegexOptions.IgnoreCase)) {
          Match nm = Regex.Match(f.Groups["attrs"].Value, @"\bname\s*=\s*(?:""(?<dq>[^""]*)""|'(?<sq>[^']*)'|(?<bare>[^\s>]+))", RegexOptions.IgnoreCase);
          string name = nm.Groups["dq"].Success ? nm.Groups["dq"].Value : nm.Groups["sq"].Success ? nm.Groups["sq"].Value : nm.Groups["bare"].Value;
          if (!String.IsNullOrEmpty(name)) fields[name] = f.Groups["value"].Value;
        }
        AreaMapNote note = BuildMapnote(entry.Groups["id"].Value, fields);
        if (note != null) result.Add(note);
      }
      return result;
    }

    private static AreaMapNote BuildMapnote(string idRaw, Dictionary<string, string> fields) {
      if (fields == null) return null;
      string fqnFull = MapnoteTextContent(fields.TryGetValue("mpnTemplateFQN", out string fqnRaw) ? fqnRaw : null);
      if (String.IsNullOrEmpty(fqnFull) || !TryParseMapnoteVector(fields.TryGetValue("mpnPosition", out string posRaw) ? posRaw : null, out Vector3 pos)) return null;

      // Jedipedia's shipped FQN is normally "\\server\\mpn\\...mpn". Work from that representation directly,
      // while retaining the old world.* trimming as a fallback for tool-generated fixtures.
      string fqn = fqnFull.Trim();
      if (fqn.StartsWith("\\server\\mpn\\", StringComparison.OrdinalIgnoreCase)) {
        fqn = fqn.Substring("\\server\\mpn\\".Length);
        if (fqn.EndsWith(".mpn", StringComparison.OrdinalIgnoreCase)) fqn = fqn.Substring(0, fqn.Length - 4);
      } else if (fqn.Length >= 12 && fqn.StartsWith("world.", StringComparison.OrdinalIgnoreCase)) {
        fqn = fqn.Substring(8, fqn.Length - 12);
      }
      fqn = fqn.Replace('\\', '.').Trim('.');

      var note = new AreaMapNote {
        Id = MapnoteTextContent(idRaw),
        Fqn = fqn,
        Label = fqn.Split('.').LastOrDefault()?.Replace('_', ' ') ?? fqn,
        Position = pos
      };
      if (TryParseMapnoteVector(fields.TryGetValue("mpnRotation", out string rotRaw) ? rotRaw : null, out Vector3 rot)) note.Rotation = rot;
      if (fields.TryGetValue("mpnMapTags", out string tagsRaw)) {
        foreach (Match tag in Regex.Matches(tagsRaw, @"<k\b[^>]*>(?<tag>[\s\S]*?)</k>", RegexOptions.IgnoreCase)) {
          string value = MapnoteTextContent(tag.Groups["tag"].Value);
          if (!String.IsNullOrEmpty(value)) note.Tags.Add(value);
        }
      }
      if (fields.TryGetValue("ParentMapTag", out string parentRaw)) {
        foreach (string parent in MapnoteTextContent(parentRaw).Split(',')) {
          string value = parent.Trim();
          if (value.Length > 0) note.ParentTags.Add(value);
        }
      }
      return note;
    }

    private static string DecodeMapnoteText(string value) => (value ?? string.Empty)
      .Replace("&quot;", "\"").Replace("&#39;", "'").Replace("&apos;", "'").Replace("&gt;", ">").Replace("&lt;", "<").Replace("&amp;", "&");
    private static string MapnoteTextContent(string value) => Regex.Replace(DecodeMapnoteText(value), "<[^>]*>", string.Empty).Trim();
    private static bool TryParseMapnoteVector(string value, out Vector3 result) {
      result = Vector3.Zero;
      string text = MapnoteTextContent(value).Trim();
      if (String.IsNullOrEmpty(text)) return false;

      bool TryVector(string candidate, out Vector3 vector) {
        vector = Vector3.Zero;
        string[] parts = candidate.Split(',');
        if (parts.Length != 3) return false;
        if (!float.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float x) ||
            !float.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float y) ||
            !float.TryParse(parts[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float z)) return false;
        vector = new Vector3(x, y, z);
        return true;
      }

      if (TryVector(text, out result)) return true;
      if (text.Length >= 2 && TryVector(text.Substring(1, text.Length - 2), out result)) return true;

      // Be tolerant of uncommon wrappers such as Vector3(1,2,3) without accepting arbitrary numbers elsewhere.
      Match m = Regex.Match(text, @"^[^,(]*\(?\s*([+\-]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][+\-]?\d+)?)\s*,\s*([+\-]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][+\-]?\d+)?)\s*,\s*([+\-]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][+\-]?\d+)?)\s*\)?[^,]*$", RegexOptions.CultureInvariant);
      if (m.Success && float.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float rx) &&
          float.TryParse(m.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float ry) &&
          float.TryParse(m.Groups[3].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float rz)) {
        result = new Vector3(rx, ry, rz);
        return true;
      }
      result = Vector3.Zero;
      return false;
    }

    private static float ParseF(string s) => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : 0;
    private static string NormalizeTerrainName(string name) {
      if (string.IsNullOrWhiteSpace(name)) return null;
      // Match the current Jedipedia area reader. SWTOR terrain tables are inconsistent across
      // areas: some entries are material names, others are authored diffuse texture paths.
      // The renderer resolves them through /art/shaders/materials, so strip the image extension
      // and the conventional diffuse suffix before looking up the MAT.
      string n = name.Trim().Replace('\\', '/').ToLowerInvariant();
      int slash = n.LastIndexOf('/'); if (slash >= 0) n = n.Substring(slash + 1);
      foreach (string ext in new[] { ".dds", ".tga", ".png", ".jpg", ".jpeg", ".mat" })
        if (n.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) { n = n.Substring(0, n.Length - ext.Length); break; }
      if (n.EndsWith("_d", StringComparison.OrdinalIgnoreCase)) n = n.Substring(0, n.Length - 2);
      return string.IsNullOrWhiteSpace(n) ? null : n;
    }
  }
}
