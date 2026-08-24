using System;
using System.Collections.Generic;
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
      using Stream stream = File.OpenCopyInMemory(); using BinaryReader br = new BinaryReader(stream);
      if (br.ReadUInt32() != 0x18) throw new InvalidDataException("Only binary room DAT is supported by the World renderer.");
      br.BaseStream.Position = 0x1C; uint instancesOffset = br.ReadUInt32(); uint visibleOffset = br.ReadUInt32(); uint settingsOffset = br.ReadUInt32();
      ReadInstances(br, instancesOffset); ReadVisibleRooms(br, visibleOffset); ReadSettings(br, settingsOffset); BuildParentIndex();
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
