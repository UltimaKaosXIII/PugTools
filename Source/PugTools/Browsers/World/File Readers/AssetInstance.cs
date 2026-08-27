using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ICSharpCode.SharpZipLib.GZip;
using ICSharpCode.SharpZipLib.Zip.Compression;
using ICSharpCode.SharpZipLib.Zip.Compression.Streams;
using SlimDX;
using SlimDX.Direct3D11;
using SlimDXNet.Vertex;
using Buffer = SlimDX.Direct3D11.Buffer;

namespace FileFormats {
  public enum AssetInstanceViewability {
    WorldAndMap = 1,
    WorldOnly = 2,
    MapOnly = 3,
    OccluderOnly = 4
  }

  // Jedipedia's rgnVolumeData: an arbitrary local-space triangle footprint whose vertices each carry
  // an independent extrusion height. SWTOR uses these prisms for map/audio/respawn/PvP/quest regions;
  // treating every .rgn as the default 64x64 box makes room selection and utility overlays wildly inaccurate.
  public sealed class RegionVolumeData {
    public Vector3[] Positions { get; internal set; } = Array.Empty<Vector3>();
    public float[] Heights { get; internal set; } = Array.Empty<float>();
    public ushort[] Indices { get; internal set; } = Array.Empty<ushort>();
    public Vector3 Min { get; internal set; }
    public Vector3 Max { get; internal set; }
  }

  public class AssetInstance1 {
    public ulong Id; public ulong AssetId; public ulong ParentInstanceId; public Room Room;
    public Vector3 Position; public Vector3 Scale = new Vector3(1,1,1); public Vector3 Rotation; public Matrix Transform; public bool Hidden;
    public void CalculateTransform() { Transform = Matrix.Scaling(Scale) * Matrix.RotationZ((float)(Rotation.Z * Math.PI / 180.0)) * Matrix.RotationX((float)(Rotation.X * Math.PI / 180.0)) * Matrix.RotationY((float)(Rotation.Y * Math.PI / 180.0)) * Matrix.Translation(Position); }
  }

  public class AssetInstance {
    public ulong ID;
    public ulong assetID;
    public ulong parentInstance;
    public bool hasHeightMap;
    public bool hasWater;
    public bool hidden;
    public int EnvironmentMaterialIndex { get; private set; }
    public HeightMap HeightMap { get; private set; }
    private byte[] vertexData;
    private byte[] waterDepthData;
    private byte[] regionVolumeData;
    public byte[] WaterDepthData => waterDepthData;
    public RegionVolumeData RegionVolume { get; private set; }
    public string RegionClassType { get; private set; } = "GENERIC";
    public string RegionCharacteristics { get; private set; }
    public string RegionRespawnMedCenter { get; private set; }
    // /engine/portal.p target room. Jedipedia feeds this into its dPVS portal graph; keeping the parsed
    // target here lets the offline renderer build a conservative portal-aware visibility graph as well.
    public string PortalTarget { get; private set; }
    public int numFaces;
    public float depth = 64, height = 64, width = 64;
    public bool HasWidthProperty { get; private set; }
    public bool HasHeightProperty { get; private set; }
    public bool HasDepthProperty { get; private set; }
    public float LodFactor { get; private set; } = 1f;
    public AssetInstanceViewability Viewability { get; private set; } = AssetInstanceViewability.WorldAndMap;
    // Runtime path-follower metadata. SWTOR uses /engine/follower.fol as an invisible moving parent; children inherit
    // the follower's path pose through ParentInstance. Jedipedia hides those descendants until the path is resolved,
    // otherwise authored traffic flashes at the follower's origin (typically 0,0,0) during/after load.
    public string PathFollowerPath { get; private set; }
    public float PathFollowerSpeed { get; private set; }
    public float PathFollowerTurnRate { get; private set; }
    // Phase/trigger metadata used by the World Browser. TriggerClassType is stored as a 1-based enum in room DATs;
    // TriggerParam names INSTANCE_REGION phases (for example stronghold_dromund_kaas).
    public string TriggerClassType { get; private set; }
    public string TriggerParam { get; private set; }
    public string TriggerTag { get; private set; }
    public bool TriggerEllipsoid { get; private set; }
    public bool PathFollowerPending { get; set; }
    public bool PathFollowerAnimated { get; set; }
    public bool PathFollowerBoundaryExcluded { get; set; }
    // Stronghold decoration-hook layout (aptPlacementLayoutLayoutId). Jedipedia expands this ID through its
    // generated hooks registry into the colored floor/wall/ceiling placement-field GR2s.
    public uint DecorationHookLayoutId { get; private set; }
    public bool HasDecorationHook => DecorationHookLayoutId != 0;
    public Area area;
    public Buffer VBO, IBO;
    public BufferDescription VBD, IBD;
    public DataStream VDS, IDS;
    public Vector3 position;
    public Vector3 rotation;
    public Vector3 scale = new Vector3(1, 1, 1);
    public Matrix transformMatrix;
    public Dictionary<uint, object> ParsedProperties { get; } = new Dictionary<uint, object>();

    // SWTOR local-light placement properties (same hashes used by Jedipedia's room reader).
    public bool IsLocalLight { get; private set; }
    public string LocalLightType { get; private set; } = "OMNI";
    public float LocalLightSourceOffset { get; private set; }
    public float LocalLightRange { get; private set; } = 1f;
    public float LocalLightIntensity { get; private set; } = 1f;
    public Vector4 LocalLightColor { get; private set; } = new Vector4(1,1,1,1);
    public string LocalLightIlluminationMap { get; private set; }
    public string LocalLightRampMap { get; private set; }
    public string LocalLightFalloff { get; private set; }
    public bool LocalLightDoHeightmaps { get; private set; } = true;
    public bool LocalLightDoGranny { get; private set; } = true;
    public bool LocalLightDoSpeedTree { get; private set; } = true;
    public bool LocalLightDoWater { get; private set; } = true;
    public bool LocalLightDoCharacters { get; private set; } = true;
    public bool LocalLightRestrictToRoom { get; private set; }

    // SWTOR .wtr properties. Defaults mirror Jedipedia's World Viewer so partially-authored water still renders.
    public Vector4 WaterDeepColor { get; private set; } = new Vector4(.13f,.46f,.63f,1f);
    public Vector4 WaterShallowColor { get; private set; } = new Vector4(.36f,.34f,.24f,1f);
    public Vector4 WaterGlossColor { get; private set; } = new Vector4(1f,1f,1f,1f);
    public string WaterNormalMap1 { get; private set; }
    public string WaterNormalMap2 { get; private set; }
    public string WaterSurfaceMap { get; private set; }
    public int WaterTextureIndex { get; private set; } = -1;
    public float WaterNormalMap1Speed { get; private set; }
    public float WaterNormalMap2Speed { get; private set; }
    public float WaterNormalMap1Dir { get; private set; }
    public float WaterNormalMap2Dir { get; private set; }
    public float WaterNormalMap1Rotation { get; private set; }
    public float WaterNormalMap2Rotation { get; private set; }
    public float WaterNormalMap1CoordScale { get; private set; } = .05f;
    public float WaterNormalMap2CoordScale { get; private set; } = .04f;
    public float WaterSurfaceMapCoordScale { get; private set; } = .03f;
    public float WaterNormalMapScale { get; private set; } = 1f;
    public float WaterDepthModulator { get; private set; } = 1f;
    public float WaterSurfaceMapShininess { get; private set; } = 1f;
    public float WaterReflectionModulator { get; private set; }
    public float WaterDistanceOpacityScale { get; private set; } = 1f;
    public float WaterAngleOpacityScale { get; private set; } = .4f;
    // SWTOR stores this normalized; the shader remaps it to [1,64].
    public float WaterSpecularPower { get; private set; } = .5f;
    public float WaterKneePosition1 { get; private set; } = .25f;
    public float WaterKneePosition2 { get; private set; } = .5f;
    public float WaterKneePosition3 { get; private set; } = .75f;
    public float WaterKneeValue1 { get; private set; }
    public float WaterKneeValue2 { get; private set; } = .35f;
    public float WaterKneeValue3 { get; private set; } = .75f;
    public float WaterKneeValueAt1 { get; private set; } = 1f;
    public float WaterSurfaceKneePosition1 { get; private set; } = .25f;
    public float WaterSurfaceKneePosition2 { get; private set; } = .5f;
    public float WaterSurfaceKneePosition3 { get; private set; } = .75f;
    public float WaterSurfaceKneeValueAt0 { get; private set; } = .01f;
    public float WaterSurfaceKneeValue1 { get; private set; } = .25f;
    public float WaterSurfaceKneeValue2 { get; private set; } = .35f;
    public float WaterSurfaceKneeValue3 { get; private set; } = .5f;
    public float WaterSurfaceKneeValueAt1 { get; private set; } = 1f;

    public AssetInstance(ulong ID, ulong assetID, Area area) { this.ID = ID; this.assetID = assetID; this.area = area; }

    public void AddProperty(ref BinaryReader br, uint name, uint type) {
      if (name == 0x3C472B4A && type == 0) { hidden = br.ReadBoolean(); return; }
      if (name == 0x1B205D23 && type == 4) { depth = br.ReadSingle(); HasDepthProperty = true; return; }
      if (name == 0x16B4F247 && type == 4) { height = br.ReadSingle(); HasHeightProperty = true; return; }
      if (name == 0xD9DDF326 && type == 4) { width = br.ReadSingle(); HasWidthProperty = true; return; }
      if (name == 0x40865E7F && type == 5) { parentInstance = br.ReadUInt64(); return; }
      if (name == 0x4F77E269 && type == 6) { position = ReadVec3(br); return; }
      if (name == 0x8C64AF1E && type == 6) { rotation = ReadVec3(br); return; }
      if (name == 0xB181622A && type == 6) { scale = ReadVec3(br); return; }
      // Same placement LODFactor consumed by Jedipedia's instanceLodFactor(). Zero intentionally requests the
      // coarsest visual LOD; malformed/negative values are clamped by the renderer.
      if (name == 0x1D30CC76 && type == 4) { LodFactor = br.ReadSingle(); return; }

      // /engine/follower.fol movement properties. Path and Speed are stored as strings in shipped room DATs;
      // TurnRate is a float. Keep the parsed values in ParsedProperties as well because the inspector/data viewer
      // should still expose the raw room metadata.
      if (name == 0x8B335065 && (type == 8 || type == 9)) { // Path
        object pathValue = ReadGenericProperty(ref br, type);
        PathFollowerPath = pathValue is byte[] bytes ? DecodeText(bytes) : pathValue?.ToString();
        ParsedProperties[name] = pathValue;
        return;
      }
      if (name == 0x104B3827 && (type == 8 || type == 9)) { // Speed
        object speedValue = ReadGenericProperty(ref br, type);
        string text = speedValue is byte[] bytes ? DecodeText(bytes) : speedValue?.ToString();
        if (!float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float speed)) speed = 0f;
        PathFollowerSpeed = Math.Abs(speed);
        ParsedProperties[name] = speedValue;
        return;
      }
      if (name == 0xBD389DDD && type == 4) { // TurnRate
        PathFollowerTurnRate = Math.Abs(br.ReadSingle());
        ParsedProperties[name] = PathFollowerTurnRate;
        return;
      }

      // Region metadata. Unlike trigger.trg's simple Width/Height/Depth primitive, region.rgn owns an
      // arbitrary triangulated footprint in rgnVolumeData. The blob uses the same misleading string/binary
      // storage as terrain/water payloads, so decode hex-string wrappers before decompression.
      if (name == 0xFDA2965D && (type == 8 || type == 9)) { // rgnVolumeData
        uint len = br.ReadUInt32(); byte[] raw = br.ReadBytes(checked((int)len));
        regionVolumeData = type == 8 ? DecodePayloadBytes(raw) : raw;
        ParsedProperties[name] = regionVolumeData;
        return;
      }
      if (name == 0xAFF3B819 && (type == 1 || type == 3)) { // rgnClassType (1-based)
        int rawRegionClass = br.ReadInt32();
        string[] regionClasses = { "GENERIC", "MAP", "AUDIO", "RESPAWN", "PVP", "DEATH", "EXHAUSTION",
          "WORLD_QUEST", "SHARED_WORLD_QUEST", "META_WORLD_QUEST", "PLANETARY_WORLD_QUEST" };
        RegionClassType = rawRegionClass >= 1 && rawRegionClass <= regionClasses.Length ? regionClasses[rawRegionClass - 1] : "GENERIC";
        ParsedProperties[name] = rawRegionClass;
        return;
      }
      if ((name == 0x27EEE87F || name == 0x1BD46F12) && (type == 8 || type == 9)) { // characteristics / respawn med center
        object regionTextValue = ReadGenericProperty(ref br, type);
        string regionText = regionTextValue is byte[] regionBytes ? DecodeText(regionBytes) : regionTextValue?.ToString();
        if (name == 0x27EEE87F) RegionCharacteristics = regionText; else RegionRespawnMedCenter = regionText;
        ParsedProperties[name] = regionTextValue;
        return;
      }

      // /engine/portal.p target. The shipped value is text in both type-8 and type-9 room properties and may
      // optionally start with a numeric portal id ("123 room/name.dat"). Jedipedia strips that id later when
      // resolving the graph; preserve the raw text here.
      if (name == 0xB04139FD && (type == 8 || type == 9)) { // PortalTarget
        object portalValue = ReadGenericProperty(ref br, type);
        PortalTarget = portalValue is byte[] portalBytes ? DecodeText(portalBytes) : portalValue?.ToString();
        ParsedProperties[name] = portalValue;
        return;
      }

      // Phase trigger properties. Jedipedia reads these from /engine/trigger.trg placements and chooses the
      // smallest INSTANCE_REGION containing the camera as the current phase.
      if (name == 0x80AF3C1A && (type == 1 || type == 3)) { // TriggerClassType
        int rawTriggerClass = br.ReadInt32();
        string[] triggerClasses = {
          "GENERIC", "BARK", "INSTANCE_REGION", "INSTANCE_GATEWAY", "QUEST_REGION", "HYDRA", "BARRIER",
          "AUDIO_REGION", "MAP", "COMPANION_EQUIPMENT", "COMPANION_PRIVATE", "DEATH_VOLUME",
          "EXHAUSTION_VOLUME", "BLOCKING_VOLUME", "FLAGSHIP_TRANSPORT_BLOCKING_VOLUME",
          "CONTROL_POINT_VOLUME", "WORLD_QUEST_VOLUME", "SHARED_WORLD_QUEST_VOLUME",
          "META_WORLD_QUEST_VOLUME", "PLANETARY_WORLD_QUEST_VOLUME"
        };
        TriggerClassType = rawTriggerClass >= 1 && rawTriggerClass <= triggerClasses.Length ? triggerClasses[rawTriggerClass - 1] : "GENERIC";
        ParsedProperties[name] = rawTriggerClass;
        return;
      }
      if (name == 0xD84FB395 && (type == 8 || type == 9)) { // TriggerParam
        object triggerParamValue = ReadGenericProperty(ref br, type);
        TriggerParam = triggerParamValue is byte[] triggerBytes ? DecodeText(triggerBytes) : triggerParamValue?.ToString();
        ParsedProperties[name] = triggerParamValue;
        return;
      }
      if (name == 0x39801EBA && (type == 8 || type == 9)) { // Tag (MAP trigger page / generic trigger label)
        object triggerTagValue = ReadGenericProperty(ref br, type);
        TriggerTag = triggerTagValue is byte[] triggerBytes ? DecodeText(triggerBytes) : triggerTagValue?.ToString();
        ParsedProperties[name] = triggerTagValue;
        return;
      }
      if (name == 0x5BFA9DA3 && type == 0) { // Ellipsoid
        TriggerEllipsoid = br.ReadBoolean();
        ParsedProperties[name] = TriggerEllipsoid;
        return;
      }

      // Room Viewability is a 1-based enum in SWTOR's room DAT:
      // 1=WORLD_AND_MAP, 2=WORLD_ONLY, 3=MAP_ONLY, 4=OCCLUDER_ONLY.
      // Jedipedia excludes MAP_ONLY and OCCLUDER_ONLY from the ordinary 3D world view.
      if (name == 0x71A31565 && (type == 1 || type == 3)) {
        int raw = br.ReadInt32();
        Viewability = raw >= 1 && raw <= 4 ? (AssetInstanceViewability)raw : AssetInstanceViewability.WorldAndMap;
        ParsedProperties[name] = raw;
        return;
      }

      // VertexData and wtrVertexData are binary blobs even when the DAT declares them as a string (type 8).
      // Old room files store the compressed bytes as ASCII/UTF-16 hex; newer ones can store the bytes directly.
      if ((name == 0xA3AB26AE || name == 0x577AA423) && (type == 8 || type == 9)) {
        uint len = br.ReadUInt32(); byte[] raw = br.ReadBytes(checked((int)len));
        vertexData = type == 8 ? DecodePayloadBytes(raw) : raw;
        ParsedProperties[name] = vertexData;
        return;
      }
      // DepthTexture has the same misleading string type in SWTOR's WTR property table.
      if (name == 0x06C671D8 && (type == 8 || type == 9)) {
        uint len = br.ReadUInt32(); byte[] raw = br.ReadBytes(checked((int)len));
        waterDepthData = type == 8 ? DecodePayloadBytes(raw) : raw;
        ParsedProperties[name] = waterDepthData;
        return;
      }
      if (name == 0xD576D2CF && (type == 1 || type == 3)) { DecorationHookLayoutId = br.ReadUInt32(); ParsedProperties[name] = DecorationHookLayoutId; return; }

      object propertyValue = ReadGenericProperty(ref br, type);
      ParsedProperties[name] = propertyValue;
      if (name == 0x50D6785E) EnvironmentMaterialIndex = ToInt(propertyValue);
      ApplyLocalLightProperty(name, propertyValue);
      ApplyWaterProperty(name, propertyValue);
    }

    private static object ReadGenericProperty(ref BinaryReader br, uint type) {
      switch (type) {
        case 0: return br.ReadBoolean();
        case 1: case 3: return br.ReadInt32();
        case 4: return br.ReadSingle();
        case 5: return br.ReadUInt64();
        case 6: return ReadVec3(br);
        case 7: return new Vector4(br.ReadSingle(), br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
        case 8: case 9: {
          uint len = br.ReadUInt32(); byte[] bytes = br.ReadBytes(checked((int)len));
          if (type == 9) return bytes;
          return DecodeText(bytes);
        }
        default: throw new InvalidDataException("Unknown room property type " + type);
      }
    }

    private static Vector3 ReadVec3(BinaryReader br) => new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
    private static string DecodeText(byte[] bytes) {
      if (bytes == null || bytes.Length == 0) return string.Empty;
      int zeros = 0, pairs = bytes.Length / 2; for (int i=1;i<bytes.Length;i+=2) if (bytes[i]==0) zeros++;
      Encoding enc = pairs > 0 && (float)zeros/pairs > .6f ? Encoding.Unicode : Encoding.UTF8;
      return enc.GetString(bytes).TrimEnd('\0');
    }

    // Mirrors Jedipedia's roomPayloadBytesFromSlice/value behavior closely enough for the two WTR blob properties:
    // recognize UTF-16LE/BE or ASCII hex, otherwise preserve the original binary bytes untouched.
    private static byte[] DecodePayloadBytes(byte[] raw) {
      if (raw == null || raw.Length == 0) return Array.Empty<byte>();
      int start = 0;
      if (raw.Length >= 2 && ((raw[0] == 0xFF && raw[1] == 0xFE) || (raw[0] == 0xFE && raw[1] == 0xFF))) start = 2;
      if (raw.Length - start <= 2 && raw.Skip(start).All(b => b == 0)) return Array.Empty<byte>();

      string candidate = null;
      int len = raw.Length - start;
      if (len >= 4) {
        int leZeros = 0, beZeros = 0, pairs = len / 2;
        for (int i = start; i + 1 < raw.Length; i += 2) { if (raw[i + 1] == 0) leZeros++; if (raw[i] == 0) beZeros++; }
        if (pairs > 0 && leZeros >= pairs * 3 / 4) candidate = Encoding.Unicode.GetString(raw, start, len);
        else if (pairs > 0 && beZeros >= pairs * 3 / 4) candidate = Encoding.BigEndianUnicode.GetString(raw, start, len);
      }
      if (candidate == null) {
        bool plausibleAscii = true;
        for (int i=start;i<raw.Length;i++) {
          byte b=raw[i]; char c=(char)b;
          if (!(Uri.IsHexDigit(c) || char.IsWhiteSpace(c) || c=='x' || c=='X')) { plausibleAscii=false; break; }
        }
        if (plausibleAscii) candidate = Encoding.ASCII.GetString(raw, start, len);
      }
      if (candidate != null && TryDecodeHex(candidate, out byte[] decoded)) return decoded;
      if (start == 0) return raw;
      byte[] copy = new byte[len]; System.Buffer.BlockCopy(raw, start, copy, 0, len); return copy;
    }

    private static bool TryDecodeHex(string text, out byte[] bytes) {
      bytes = null; if (string.IsNullOrWhiteSpace(text)) { bytes = Array.Empty<byte>(); return true; }
      var sb = new StringBuilder(text.Length);
      foreach (char c in text.Trim('\0')) if (!char.IsWhiteSpace(c)) sb.Append(c);
      string s = sb.ToString(); if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s.Substring(2);
      if (s.Length == 0) { bytes = Array.Empty<byte>(); return true; }
      if ((s.Length & 1) != 0 || s.Any(c => !Uri.IsHexDigit(c))) return false;
      bytes = new byte[s.Length / 2];
      for (int i=0;i<bytes.Length;i++) bytes[i] = Convert.ToByte(s.Substring(i*2,2),16);
      return true;
    }

    private static int ToInt(object o) { try { return Convert.ToInt32(o); } catch { return 0; } }
    private static float ToFloat(object o, float fallback = 0) { try { return Convert.ToSingle(o); } catch { if (float.TryParse(o?.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float f)) return f; return fallback; } }
    private static bool ToBool(object o, bool fallback = false) {
      if (o is bool b) return b;
      string text = LocalLightText(o);
      if (String.Equals(text, "1", StringComparison.OrdinalIgnoreCase)) return true;
      if (String.Equals(text, "0", StringComparison.OrdinalIgnoreCase)) return false;
      return bool.TryParse(text, out b) ? b : fallback;
    }

    // Room property type 9 is a text field in Jedipedia's local-light schema, while the generic reader deliberately
    // preserves type-9 bytes because other room properties use that type for binary payloads. Decode it only while
    // interpreting local-light values so those other payloads remain untouched.
    private static string LocalLightText(object value) {
      if (value is byte[] bytes) return DecodeText(bytes).Trim().Trim('"');
      return value?.ToString()?.Trim().Trim('"') ?? String.Empty;
    }

    private static float LocalLightNumber(object value, float fallback) {
      string text = LocalLightText(value);
      return float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float parsed)
        && Single.IsFinite(parsed) ? parsed : fallback;
    }

    private static string LocalLightTypeName(object value) {
      string text = LocalLightText(value);
      if (String.Equals(text, "DIRECTIONAL", StringComparison.OrdinalIgnoreCase)) return "DIRECTIONAL";
      if (String.Equals(text, "OMNI", StringComparison.OrdinalIgnoreCase)) return "OMNI";
      if (String.Equals(text, "SPOT", StringComparison.OrdinalIgnoreCase)) return "SPOT";
      if (String.Equals(text, "BOX", StringComparison.OrdinalIgnoreCase)) return "BOX";

      // Jedipedia's room reader treats enum property type 3 as one-based:
      // 1=DIRECTIONAL, 2=OMNI, 3=SPOT.
      if (Int32.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int raw))
        return raw == 1 ? "DIRECTIONAL" : raw == 3 ? "SPOT" : "OMNI";
      return "OMNI";
    }

    private static bool TryLocalLightColor(object value, out Vector4 color) {
      if (value is Vector4 direct) { color = direct; return true; }
      string text = LocalLightText(value);
      if (String.IsNullOrWhiteSpace(text) || text.StartsWith("[", StringComparison.Ordinal)) { color = default; return false; }
      string[] parts = text.TrimStart('#').Split(',');
      if (parts.Length < 3) { color = default; return false; }
      float[] c = new float[4] { 1, 1, 1, 1 };
      for (int i = 0; i < Math.Min(parts.Length, 4); i++) {
        if (!float.TryParse(parts[i].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out c[i])
            || !Single.IsFinite(c[i])) { color = default; return false; }
      }
      color = new Vector4(c[0], c[1], c[2], c[3]);
      return true;
    }

    private void ApplyLocalLightProperty(uint name, object value) {
      switch (name) {
        case 0x36A29A30: IsLocalLight = true; LocalLightType = LocalLightTypeName(value); break;
        case 0x6CB280CE: IsLocalLight = true; LocalLightSourceOffset = LocalLightNumber(value, 0); break;
        case 0x16E929FD: IsLocalLight = true; LocalLightRange = Math.Max(.0001f, LocalLightNumber(value, 1)); break;
        case 0x6C84A1CD: IsLocalLight = true; LocalLightIlluminationMap = LocalLightText(value); break;
        case 0xBC68CFF3: IsLocalLight = true; LocalLightIntensity = LocalLightNumber(value, 1); break;
        case 0x429C878A: IsLocalLight = true; LocalLightRampMap = LocalLightText(value); break;
        case 0x279A7732: IsLocalLight = true; LocalLightRestrictToRoom = ToBool(value); break;
        case 0xA67AE663: IsLocalLight = true; if (TryLocalLightColor(value, out Vector4 c)) LocalLightColor = c; break;
        case 0xC5B29609: IsLocalLight = true; LocalLightDoHeightmaps = ToBool(value, true); break;
        case 0x925224AE: IsLocalLight = true; LocalLightDoGranny = ToBool(value, true); break;
        case 0x3902C81A: IsLocalLight = true; LocalLightDoSpeedTree = ToBool(value, true); break;
        case 0xB37CB64C: IsLocalLight = true; LocalLightDoWater = ToBool(value, true); break;
        case 0x0D59B255: IsLocalLight = true; LocalLightDoCharacters = ToBool(value, true); break;
        case 0x3B92D7B4: IsLocalLight = true; LocalLightFalloff = LocalLightText(value); break;
      }
    }

    private void ApplyWaterProperty(uint name, object value) {
      switch (name) {
        case 0x9EB346D7: WaterSurfaceMapShininess = ToFloat(value, 1); break;
        case 0xA4DEE397: if (value is Vector4 dc) WaterDeepColor = dc; break;
        case 0x67D56C2C: WaterTextureIndex = ToInt(value); break;
        case 0x79252004: WaterKneeValue2 = ToFloat(value, .35f); break;
        case 0x22232EB0: WaterNormalMap2Dir = ToFloat(value); break;
        case 0x309B8EDA: WaterNormalMap1Rotation = ToFloat(value); break;
        case 0x34B33599: WaterSurfaceKneePosition2 = ToFloat(value, .5f); break;
        case 0x97BD57BC: WaterNormalMap1 = value?.ToString(); break;
        case 0xAF219C64: WaterSurfaceMapCoordScale = ToFloat(value, .03f); break;
        case 0x79252005: WaterKneeValue3 = ToFloat(value, .75f); break;
        case 0x34B3359A: WaterSurfaceKneePosition3 = ToFloat(value, .75f); break;
        case 0x012A9C47: WaterKneePosition3 = ToFloat(value, .75f); break;
        case 0x62720B6B: WaterNormalMap1Speed = ToFloat(value); break;
        case 0x789E6CAA: WaterNormalMap2Speed = ToFloat(value); break;
        case 0xEF241B39: if (value is Vector4 gc) WaterGlossColor = gc; break;
        case 0x5430EB0F: WaterSurfaceMap = value?.ToString(); break;
        case 0x97BD57BD: WaterNormalMap2 = value?.ToString(); break;
        case 0x03B54CDB: WaterNormalMap2Rotation = ToFloat(value); break;
        case 0x34B33598: WaterSurfaceKneePosition1 = ToFloat(value, .25f); break;
        case 0x012A9C46: WaterKneePosition2 = ToFloat(value, .5f); break;
        case 0xA0A2E891: WaterNormalMap1CoordScale = ToFloat(value, .05f); break;
        case 0x9458550C: WaterDepthModulator = ToFloat(value, 1); break;
        case 0x073BB612: WaterNormalMap2CoordScale = ToFloat(value, .04f); break;
        case 0xF39C5DF1: WaterNormalMap1Dir = ToFloat(value); break;
        case 0x6F62ABAF: if (value is Vector4 sc) WaterShallowColor = sc; break;
        case 0x012A9C45: WaterKneePosition1 = ToFloat(value, .25f); break;
        case 0x79252003: WaterKneeValue1 = ToFloat(value); break;
        case 0x12265370: WaterKneeValueAt1 = ToFloat(value, 1); break;
        case 0xC7A1E914: WaterDistanceOpacityScale = ToFloat(value, 1); break;
        case 0xE19C6092: WaterAngleOpacityScale = ToFloat(value, .4f); break;
        case 0x6EB96870: WaterSurfaceKneeValue1 = ToFloat(value, .25f); break;
        case 0x6EB96871: WaterSurfaceKneeValue2 = ToFloat(value, .35f); break;
        case 0x6EB96872: WaterSurfaceKneeValue3 = ToFloat(value, .5f); break;
        case 0x27C3355C: WaterSurfaceKneeValueAt0 = ToFloat(value, .01f); break;
        case 0x27C3355D: WaterSurfaceKneeValueAt1 = ToFloat(value, 1); break;
        case 0xA1DF4EB5: WaterNormalMapScale = ToFloat(value, 1); break;
        case 0x059903C4: WaterReflectionModulator = ToFloat(value); break;
        case 0x172A4EC2: WaterSpecularPower = ToFloat(value, .5f); break;
      }
    }

    public void ReadEmbeddedGeometry() {
      string ext = area != null && area.AssetIdMap.TryGetValue(assetID, out AreaAsset asset)
        ? (asset.Extension ?? String.Empty).Trim().TrimStart('.').ToLowerInvariant() : null;
      byte[] payload = ext == "rgn" ? regionVolumeData : vertexData;
      if (payload == null || payload.Length < 2) return;
      byte[] data = Decompress(payload);
      if (data == null || data.Length < 2) return;
      try {
        using var br = new BinaryReader(new MemoryStream(data, false));
        if (ext == "rgn") ReadRegionVolume(br);
        else if (ext == "wtr") ReadWater(br);
        else if (ext == "hms" || ext == null || ext == string.Empty) ReadHeightMap(br);
      } catch (Exception ex) {
        System.Diagnostics.Debug.WriteLine("Could not parse embedded geometry " + ID + ": " + ex.Message);
      }
    }

    private void ReadRegionVolume(BinaryReader br) {
      if (br.BaseStream.Length - br.BaseStream.Position < 3) throw new InvalidDataException("Truncated RGN volume");
      _ = br.ReadByte(); // version; the shipped layouts currently share the same payload structure
      ushort vertexCount = br.ReadUInt16();
      if (vertexCount < 3 || vertexCount > 65530 || br.BaseStream.Length - br.BaseStream.Position < (long)vertexCount * 16 + 2)
        throw new InvalidDataException("Invalid RGN vertex count");
      Vector3[] positions = new Vector3[vertexCount];
      float[] heights = new float[vertexCount];
      Vector3 min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
      Vector3 max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
      for (int i = 0; i < vertexCount; i++) {
        Vector3 position = ReadVec3(br); float extrusion = br.ReadSingle();
        if (!Single.IsFinite(position.X) || !Single.IsFinite(position.Y) || !Single.IsFinite(position.Z) || !Single.IsFinite(extrusion))
          throw new InvalidDataException("Non-finite RGN vertex");
        positions[i] = position; heights[i] = extrusion;
        min.X = Math.Min(min.X, position.X); min.Y = Math.Min(min.Y, Math.Min(position.Y, position.Y + extrusion)); min.Z = Math.Min(min.Z, position.Z);
        max.X = Math.Max(max.X, position.X); max.Y = Math.Max(max.Y, Math.Max(position.Y, position.Y + extrusion)); max.Z = Math.Max(max.Z, position.Z);
      }
      ushort indexCount = br.ReadUInt16();
      if (indexCount < 3 || indexCount % 3 != 0 || br.BaseStream.Length - br.BaseStream.Position < (long)indexCount * 2)
        throw new InvalidDataException("Invalid RGN index count");
      ushort[] indices = new ushort[indexCount];
      for (int i = 0; i < indexCount; i++) { ushort index = br.ReadUInt16(); if (index >= vertexCount) throw new InvalidDataException("RGN index out of range"); indices[i] = index; }
      RegionVolume = new RegionVolumeData { Positions = positions, Heights = heights, Indices = indices, Min = min, Max = max };
    }

    // Kept for older call sites.
    public void ReadHeightMap() => ReadEmbeddedGeometry();

    public static byte[] DecompressRoomPayload(byte[] bytes) => Decompress(bytes);

    private static byte[] Decompress(byte[] bytes) {
      if (bytes == null || bytes.Length == 0) return bytes;
      try {
        bool zstd = bytes.Length >= 4 && bytes[0] == 0x28 && bytes[1] == 0xB5 && bytes[2] == 0x2F && bytes[3] == 0xFD;
        if (zstd) { using var d = new ZstdSharp.Decompressor(); return d.Unwrap(bytes, 64 * 1024 * 1024).ToArray(); }
        bool gzip = bytes.Length >= 2 && bytes[0] == 0x1F && bytes[1] == 0x8B;
        if (gzip) {
          using var input = new GZipInputStream(new MemoryStream(bytes, false));
          using var output = new MemoryStream(); input.CopyTo(output); return output.ToArray();
        }
        bool zlib = bytes.Length >= 2 && (bytes[0] & 0x0F) == 8 && (bytes[0] >> 4) <= 7 && (((bytes[0] << 8) + bytes[1]) % 31) == 0;
        if (zlib) {
          using var input = new InflaterInputStream(new MemoryStream(bytes, false), new Inflater(false));
          using var output = new MemoryStream(); input.CopyTo(output); return output.ToArray();
        }
        return bytes;
      } catch { return null; }
    }

    private void ReadHeightMap(BinaryReader br) {
      HeightMap = new HeightMap(br, area);
      int w = (int)HeightMap.width, d = (int)HeightMap.depth;
      var indices = new List<ushort>(Math.Max(0, (w - 1) * (d - 1) * 6));
      for (int z=0; z<d-1; z++) for (int x=0; x<w-1; x++) {
        if (HeightMap.hasHoles && !HeightMap.CheckNoHole(x,z)) continue;
        indices.Add((ushort)(z*w+x)); indices.Add((ushort)((z+1)*w+x)); indices.Add((ushort)(z*w+x+1));
        indices.Add((ushort)(z*w+x+1)); indices.Add((ushort)((z+1)*w+x)); indices.Add((ushort)((z+1)*w+x+1));
      }
      Vector3[] pos = new Vector3[w*d]; Vector3[] normals = new Vector3[w*d];
      float xmin = -.2f * (float)Math.Ceiling(.5f * (w - 1)); float zmin = -.2f * (float)Math.Ceiling(.5f * (d - 1));
      for (int z=0; z<d; z++) for (int x=0; x<w; x++) pos[z*w+x] = new Vector3(xmin + .2f*x, HeightMap.elevation[z,x], zmin + .2f*z);
      for (int i=0; i+2<indices.Count; i+=3) {
        int a=indices[i], b=indices[i+1], c=indices[i+2]; Vector3 n = Vector3.Cross(pos[b]-pos[a], pos[c]-pos[a]); normals[a]+=n; normals[b]+=n; normals[c]+=n;
      }
      var verts = new PosNormalTexTan[w*d];
      for (int z=0; z<d; z++) for (int x=0; x<w; x++) {
        int i=z*w+x; Vector3 n=normals[i]; if (n.LengthSquared()<.000001f) n=new Vector3(0,1,0); else n.Normalize();
        int xl=Math.Max(0,x-1), xr=Math.Min(w-1,x+1); Vector3 t=pos[z*w+xr]-pos[z*w+xl]; if(t.LengthSquared()<.000001f)t=new Vector3(1,0,0); else t.Normalize();
        float u = w > 1 ? ((pos[i].X-xmin)/(.2f*(w-1))) * w/2f : 0; float v = d > 1 ? ((pos[i].Z-zmin)/(.2f*(d-1))) * d/2f : 0;
        verts[i]=new PosNormalTexTan(pos[i], n, new Vector2(u,v), t);
      }
      CreateGeometryStreams(verts, indices.ToArray()); hasHeightMap = true;
    }

    private void ReadWater(BinaryReader br) {
      byte version = br.ReadByte(); _ = version; ushort vertexCount = br.ReadUInt16(); if (vertexCount < 3 || vertexCount > 65530) throw new InvalidDataException("Invalid WTR vertex count");
      Vector3[] p = new Vector3[vertexCount]; Vector3 min=new Vector3(float.MaxValue,float.MaxValue,float.MaxValue), max=new Vector3(float.MinValue,float.MinValue,float.MinValue);
      for (int i=0;i<vertexCount;i++) { p[i]=ReadVec3(br); min.X=Math.Min(min.X,p[i].X);min.Y=Math.Min(min.Y,p[i].Y);min.Z=Math.Min(min.Z,p[i].Z);max.X=Math.Max(max.X,p[i].X);max.Y=Math.Max(max.Y,p[i].Y);max.Z=Math.Max(max.Z,p[i].Z); }
      ushort declared = br.ReadUInt16(); int available=(int)Math.Min(declared,(br.BaseStream.Length-br.BaseStream.Position)/2); var idx=new List<ushort>(Math.Max(available,(vertexCount-2)*3));
      for(int i=0;i<available;i++){ushort v=br.ReadUInt16();if(v<vertexCount)idx.Add(v);} while(idx.Count%3!=0)idx.RemoveAt(idx.Count-1);
      if(idx.Count<3) for(ushort i=1;i<vertexCount-1;i++){idx.Add(0);idx.Add(i);idx.Add((ushort)(i+1));}
      float sx=Math.Max(.0001f,max.X-min.X), sz=Math.Max(.0001f,max.Z-min.Z); var verts=new PosNormalTexTan[vertexCount];
      for(int i=0;i<vertexCount;i++) verts[i]=new PosNormalTexTan(p[i],new Vector3(0,1,0),new Vector2((p[i].X-min.X)/sx,(p[i].Z-min.Z)/sz),new Vector3(1,0,0));
      CreateGeometryStreams(verts,idx.ToArray()); hasWater=true;
    }

    private void CreateGeometryStreams(PosNormalTexTan[] vertices, ushort[] indices) {
      VBD = new BufferDescription(PosNormalTexTan.Stride * vertices.Length, ResourceUsage.Immutable, BindFlags.VertexBuffer, CpuAccessFlags.None, ResourceOptionFlags.None, 0);
      VDS = new DataStream(vertices, false, false);
      IBD = new BufferDescription(sizeof(ushort) * indices.Length, ResourceUsage.Immutable, BindFlags.IndexBuffer, CpuAccessFlags.None, ResourceOptionFlags.None, 0);
      IDS = new DataStream(indices, false, false); numFaces = indices.Length;
    }

    public void CalculateTransform() {
      transformMatrix = Matrix.Scaling(scale) * Matrix.RotationZ((float)(rotation.Z*Math.PI/180.0)) * Matrix.RotationX((float)(rotation.X*Math.PI/180.0)) * Matrix.RotationY((float)(rotation.Y*Math.PI/180.0)) * Matrix.Translation(position);
    }

    public Matrix GetAbsoluteTransform(Room room) {
      ulong pId=parentInstance; Matrix output=transformMatrix; var seen=new HashSet<ulong>();
      while(pId!=0 && seen.Add(pId)) { if(!room.InstancesById.TryGetValue(pId,out AssetInstance p))break; output*=p.transformMatrix;pId=p.parentInstance; }
      return output;
    }
  }
}
