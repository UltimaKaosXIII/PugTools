using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using SlimDX;
using File = TorArchive.File;

namespace FileFormats {
  public class Room {
    private File File { get; set; }
    public string RoomName { get; private set; }
    public Area Area { get; private set; }
    public Dictionary<ulong, AssetInstance> InstancesById { get; set; } = new Dictionary<ulong, AssetInstance>();
    public Dictionary<ulong, List<AssetInstance>> InstancesByAssetId { get; set; } = new Dictionary<ulong, List<AssetInstance>>();
    public Dictionary<ulong, List<AssetInstance>> InstancesByParentId { get; set; } = new Dictionary<ulong, List<AssetInstance>>();
    public List<string> VisibleRooms { get; } = new List<string>();
    public string EnvironmentSchemeName { get; private set; } = "area";
    public Vector3 VisibilityMin { get; private set; }
    public Vector3 VisibilityMax { get; private set; }
    public Vector3 VisibilityCenter { get; private set; }
    public float VisibilityRadius { get; private set; }
    public ulong RoomId { get; private set; }
    public bool OutdoorsVisible { get; private set; }
    public bool DisablePlantEmitters { get; private set; }
    public AreaEnvironmentScheme EnvironmentScheme => Area?.GetEnvironmentScheme(EnvironmentSchemeName);

    public Room(File roomDat, string roomName, Area area) {
      Area = area; RoomName = NormalizeRoomName(roomName); File = roomDat ?? throw new ArgumentNullException(nameof(roomDat));
    }

    public bool Contains(Vector3 p) => p.X >= VisibilityMin.X && p.X <= VisibilityMax.X && p.Y >= VisibilityMin.Y && p.Y <= VisibilityMax.Y && p.Z >= VisibilityMin.Z && p.Z <= VisibilityMax.Z;

    // Some shipped room DATs contain an all-zero visibility box. Jedipedia replaces those with the conservative
    // bounds of the room's renderable placements before its dPVS/fallback room lookup starts. Keep the authored
    // box when it is usable; this setter exists only for the same load-time fallback in the offline renderer.
    public void SetComputedVisibilityBounds(Vector3 min, Vector3 max) {
      VisibilityMin = min; VisibilityMax = max; VisibilityCenter = (min + max) * .5f;
      VisibilityRadius = (max - min).Length() * .5f;
    }

    public void Read() {
      using Stream stream = File.OpenCopyInMemory();
      if (stream.Length < 4) throw new InvalidDataException("room.dat is truncated.");
      using BinaryReader br = new BinaryReader(stream, Encoding.UTF8, true);
      uint signature = br.ReadUInt32(); br.BaseStream.Position = 0;
      if (signature == 0x18) ReadBinary(br); else ReadText(br);
      BuildParentIndex();
    }

    private void ReadBinary(BinaryReader br) {
      _ = br.ReadUInt32();
      br.BaseStream.Position = 0x1C; uint instancesOffset = br.ReadUInt32(); uint visibleOffset = br.ReadUInt32(); uint settingsOffset = br.ReadUInt32();
      ReadInstances(br, instancesOffset); ReadVisibleRooms(br, visibleOffset); ReadSettings(br, settingsOffset);
    }

    // Legacy text room.dat (Version=4), used together with text area.dat. The format stores
    // property names and values as text rather than hashed binary room-property records.
    private void ReadText(BinaryReader br) {
      br.BaseStream.Position = 0;
      byte[] bytes = br.ReadBytes(checked((int)br.BaseStream.Length));
      string text = DecodeTextFile(bytes);
      string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
      string section = String.Empty;
      AssetInstance current = null;

      foreach (string rawLine in lines) {
        string untrimmed = rawLine ?? String.Empty;
        string line = untrimmed.Trim();
        if (line.Length == 0 || line == "!" || line.Equals("! Room Specification",StringComparison.OrdinalIgnoreCase) || line.StartsWith("Version=",StringComparison.OrdinalIgnoreCase)) continue;
        if (line.StartsWith("[",StringComparison.Ordinal) && line.EndsWith("]",StringComparison.Ordinal)) { section=line.ToUpperInvariant(); current=null; continue; }

        switch(section) {
          case "[INSTANCES]":
            if (untrimmed.StartsWith("    .",StringComparison.Ordinal) || line.StartsWith(".",StringComparison.Ordinal)) {
              if (current == null) break;
              string prop=line.TrimStart('.'); int eq=prop.IndexOf('='); if(eq<=0) break;
              current.AddTextProperty(prop.Substring(0,eq).Trim(),prop.Substring(eq+1));
            } else {
              // Pre-3.3.2 rooms do not have one fixed instance-id prefix. Jedipedia accepts both
              // the common "  4..." and older "  1..." records; the authoritative grammar is
              // simply <instanceId>=<assetId> followed by indented property lines.
              int eq=line.IndexOf('='); if(eq<=0) break;
              if(!UInt64.TryParse(line.Substring(0,eq).Trim(),NumberStyles.Integer,CultureInfo.InvariantCulture,out ulong instanceId)) break;
              if(!UInt64.TryParse(line.Substring(eq+1).Trim(),NumberStyles.Integer,CultureInfo.InvariantCulture,out ulong assetId)) break;
              current=new AssetInstance(instanceId,assetId,Area);
              InstancesById[instanceId]=current;
              if(!InstancesByAssetId.TryGetValue(assetId,out List<AssetInstance> list))InstancesByAssetId[assetId]=list=new List<AssetInstance>();
              list.Add(current);
            }
            break;
          case "[VISIBLE]": {
            string visible=NormalizeRoomName(line); if(!String.IsNullOrWhiteSpace(visible))VisibleRooms.Add(visible); break;
          }
          case "[SETTINGS]": ReadTextSetting(line); break;
        }
      }

      // Embedded HMS/WTR/RGN blobs are textual hex in early room files. Decode them only after
      // every property has been collected, then build exactly the same transform as binary rooms.
      foreach(AssetInstance instance in InstancesById.Values) { instance.ReadEmbeddedGeometry(); instance.CalculateTransform(); }
    }

    private void ReadTextSetting(string line) {
      int eq=line.IndexOf('='); if(eq<=0)return;
      string key=line.Substring(0,eq).Trim(),value=line.Substring(eq+1).Trim().Trim('"');
      switch(key.ToLowerInvariant()) {
        case "enviroscheme": EnvironmentSchemeName=String.IsNullOrWhiteSpace(value)?"area":value.ToLowerInvariant(); break;
        case "outdoorsvisible": OutdoorsVisible=ParseBool(value); break;
        case "disableplantemitters": DisablePlantEmitters=ParseBool(value); break;
        case "roomguid": UInt64.TryParse(value,NumberStyles.Integer,CultureInfo.InvariantCulture,out ulong roomId);RoomId=roomId; break;
        case "boundingbox":
          // Most text rooms author (0,0,0)-(0,0,0); if real bounds are present preserve them.
          if(TryParseBoundingBox(value,out Vector3 min,out Vector3 max)){VisibilityMin=min;VisibilityMax=max;VisibilityCenter=(min+max)*.5f;VisibilityRadius=(max-min).Length()*.5f;}
          break;
      }
    }

    private static bool ParseBool(string value) => value != null && (value.Equals("true",StringComparison.OrdinalIgnoreCase)||value.Equals("yes",StringComparison.OrdinalIgnoreCase)||value=="1");

    private static bool TryParseBoundingBox(string value,out Vector3 min,out Vector3 max) {
      min=Vector3.Zero;max=Vector3.Zero;if(String.IsNullOrWhiteSpace(value))return false;
      int close=value.IndexOf(')');if(close<0)return false;
      string a=value.Substring(0,close+1),b=value.Substring(close+1).Trim();
      return TryParseVec3(a,out min)&&TryParseVec3(b,out max);
    }

    private static bool TryParseVec3(string text,out Vector3 value) {
      string[] p=(text??String.Empty).Trim().Trim('(',')').Split(',');value=Vector3.Zero;if(p.Length<3)return false;
      if(!float.TryParse(p[0],NumberStyles.Float,CultureInfo.InvariantCulture,out float x)||!float.TryParse(p[1],NumberStyles.Float,CultureInfo.InvariantCulture,out float y)||!float.TryParse(p[2],NumberStyles.Float,CultureInfo.InvariantCulture,out float z))return false;
      value=new Vector3(x,y,z);return true;
    }

    private static string DecodeTextFile(byte[] bytes) {
      if(bytes==null||bytes.Length==0)return String.Empty;
      if(bytes.Length>=2&&bytes[0]==0xFF&&bytes[1]==0xFE)return Encoding.Unicode.GetString(bytes,2,bytes.Length-2).TrimEnd('\0');
      if(bytes.Length>=2&&bytes[0]==0xFE&&bytes[1]==0xFF)return Encoding.BigEndianUnicode.GetString(bytes,2,bytes.Length-2).TrimEnd('\0');
      if(bytes.Length>=3&&bytes[0]==0xEF&&bytes[1]==0xBB&&bytes[2]==0xBF)return Encoding.UTF8.GetString(bytes,3,bytes.Length-3).TrimEnd('\0');
      return Encoding.UTF8.GetString(bytes).TrimEnd('\0');
    }

    private void ReadInstances(BinaryReader br, uint offset) {
      br.BaseStream.Position = offset; uint count = br.ReadUInt32();
      for (int i = 0; i < count; i++) {
        br.BaseStream.Position += 5; ulong instanceId = br.ReadUInt64(); ulong assetId = br.ReadUInt64();
        var instance = new AssetInstance(instanceId, assetId, Area);
        br.ReadByte(); uint numProperties = br.ReadUInt32(); _ = numProperties; uint propertiesLength = br.ReadUInt32(); long end = br.BaseStream.Position + propertiesLength;
        if (br.BaseStream.Position < end) br.ReadByte();
        while (br.BaseStream.Position < end) {
          uint type = br.ReadByte(); uint name = br.ReadUInt32();
          try { instance.AddProperty(ref br, name, type); } catch { br.BaseStream.Position = end; break; }
        }
        br.BaseStream.Position = end;
        instance.ReadEmbeddedGeometry(); instance.CalculateTransform();
        InstancesById[instanceId] = instance;
        if (!InstancesByAssetId.TryGetValue(assetId, out List<AssetInstance> list)) InstancesByAssetId[assetId] = list = new List<AssetInstance>();
        list.Add(instance);
      }
    }

    private void ReadVisibleRooms(BinaryReader br, uint offset) {
      br.BaseStream.Position = offset; uint count = br.ReadUInt32();
      for (int i = 0; i < count; i++) {
        uint len = br.ReadUInt32(); string visible = NormalizeRoomName(ReadWString(br, len)); if (!string.IsNullOrEmpty(visible)) VisibleRooms.Add(visible);
      }
    }

    private void ReadSettings(BinaryReader br, uint offset) {
      br.BaseStream.Position = offset;
      uint envLen = br.ReadUInt32(); EnvironmentSchemeName = ReadWString(br, envLen).Trim().ToLowerInvariant(); if (string.IsNullOrEmpty(EnvironmentSchemeName)) EnvironmentSchemeName = "area";
      uint mapsLen = br.ReadUInt32(); br.BaseStream.Position += mapsLen; uint mapsVisibleLen = br.ReadUInt32(); br.BaseStream.Position += mapsVisibleLen;
      VisibilityMin = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
      VisibilityMax = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
      VisibilityCenter = (VisibilityMin + VisibilityMax) * .5f;
      VisibilityRadius = (VisibilityMax - VisibilityMin).Length() * .5f;
      RoomId = br.ReadUInt64(); OutdoorsVisible = br.ReadBoolean(); DisablePlantEmitters = br.ReadBoolean();
    }

    private void BuildParentIndex() {
      InstancesByParentId.Clear();
      foreach (AssetInstance instance in InstancesById.Values) {
        if (instance.parentInstance == 0) continue;
        if (!InstancesByParentId.TryGetValue(instance.parentInstance, out List<AssetInstance> children)) InstancesByParentId[instance.parentInstance] = children = new List<AssetInstance>();
        children.Add(instance);
      }
    }

    private static string ReadWString(BinaryReader br, uint byteLength) {
      byte[] b = br.ReadBytes(checked((int)byteLength)); return Encoding.Unicode.GetString(b).TrimEnd('\0');
    }
    private static string NormalizeRoomName(string value) => (value ?? string.Empty).Trim().Replace('\\', '/').TrimStart('/').Replace(".dat", "", StringComparison.OrdinalIgnoreCase).ToLowerInvariant();
  }
}
