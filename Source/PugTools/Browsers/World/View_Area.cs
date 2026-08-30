using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using FileFormats;
using SlimDX;
using SlimDX.Direct3D11;
using SlimDX.DXGI;
using SlimDXNet;
using SlimDXNet.Camera;
using SlimDXNet.Vertex;
using Buffer = SlimDX.Direct3D11.Buffer;

namespace PugTools {
  internal sealed partial class View_AREA : D3DPanelApp {
    private struct TerrainLodRange { public int StartIndex, Count; public TerrainLodRange(int start,int count){StartIndex=start;Count=count;} }
    private sealed class TerrainGpu : IDisposable {
      public ShaderResourceView ColorMap;
      public Buffer LodIndexBuffer;
      public TerrainLodRange[] LodRanges;
      public bool OwnsLodIndexBuffer = true;
      public bool TexturesPrepared;
      public long LastUseFrame;
      public readonly Dictionary<string, ShaderResourceView> Masks = new Dictionary<string, ShaderResourceView>(StringComparer.OrdinalIgnoreCase);
      public void ReleaseTextures(){foreach(var v in Masks.Values)v?.Dispose();Masks.Clear();ColorMap?.Dispose();ColorMap=null;TexturesPrepared=false;}
      public void Dispose() { ReleaseTextures(); if(OwnsLodIndexBuffer)LodIndexBuffer?.Dispose(); LodIndexBuffer=null; LodRanges=null; }
    }
    private sealed class SharedTerrainIndexGpu : IDisposable {
      public Buffer Buffer; public TerrainLodRange[] Ranges;
      public void Dispose(){Buffer?.Dispose();Buffer=null;Ranges=null;}
    }
    private sealed class LineGpu : IDisposable {
      public Buffer Buffer; public int Count; public Vector4 Color; public Vector3 Center; public float Radius;
      public void Dispose(){Buffer?.Dispose();Buffer=null;}
    }
    private sealed class WaterGpu : IDisposable {
      public ShaderResourceView Normal1, Normal2, DepthMap, SurfaceMap;
      public bool OwnsDepthMap;
      public bool TexturesPrepared;
      public long LastUseFrame;
      public void ReleaseTextures(){if(OwnsDepthMap)DepthMap?.Dispose();Normal1=null;Normal2=null;DepthMap=null;SurfaceMap=null;OwnsDepthMap=false;TexturesPrepared=false;}
      public void Dispose(){ReleaseTextures();}
    }
    private sealed class MapArtGpu : IDisposable {
      public Buffer Buffer; public int Count; public ShaderResourceView Texture; public string Name;
      public void Dispose(){Buffer?.Dispose();Buffer=null;Texture=null;}
    }
    private sealed class MapNoteIconGpu : IDisposable {
      public Buffer Buffer; public int Capacity; public ShaderResourceView Texture; public string Key;
      public void Dispose(){Buffer?.Dispose();Buffer=null;Texture?.Dispose();Texture=null;Capacity=0;}
    }
    private sealed class DynamicDetailGpu : IDisposable {
      public Buffer Buffer; public int Count; public int AtlasMode; public float Wind; public Vector2 TextureSize; public GR2_Material Material; public int ChannelId;
      public void Dispose(){Buffer?.Dispose();Buffer=null;Count=0;}
    }
    private sealed class DynamicDetailMeshBatch : IDisposable {
      public int ChannelId; public GR2 Model; public AreaDynamicDetailChannelParam Params; public List<DynamicDetailPlacement> Placements;
      public Buffer InstanceBuffer; public int InstanceCount;
      public void Dispose(){InstanceBuffer?.Dispose();InstanceBuffer=null;InstanceCount=0;}
    }
    private sealed class LocalLightEntry {
      public Room Room;
      public AssetInstance Instance;
      public Vector3 Position;
      public float RangeSquared;
      public bool Directional;
      public bool RestrictToRoom;
      public bool DoHeightmaps;
      public bool DoGranny;
      public bool DoSpeedTree = true;
      public bool DoCharacters;
      public bool DoWater;
      public Vector4 PosRange;
      public Vector4 ColorIntensity;
      public Vector4 DirType;
      public Matrix ProjectorInv;
      public Vector4 ProjectorParams;
      // Keep light texture paths as metadata and stream the SRVs only when this light is actually selected for a
      // visible receiver. Eagerly uploading every static .lit projector was a large startup/VRAM cost on planets.
      public string IlluminationPath;
      public string FalloffPath;
      public string RampPath;
    }

    private sealed class HeightMapFloorEntry {
      public Room Room;
      public HeightMap HeightMap;
      public Matrix World;
      public Matrix Inverse;
      public float XMin;
      public float ZMin;
      public float WorldMinX;
      public float WorldMaxX;
      public float WorldMinZ;
      public float WorldMaxZ;
    }

    private sealed class RoomPlacementEntry {
      public Room Room;
      public AssetInstance Instance;
      public string AssetPath;
      public Matrix Inverse;
      public float Width;
      public float Depth;
      public float Height;
      public bool HasHeight;
      public int Rank;
      public RegionVolumeData RegionVolume;
    }

    // Jedipedia keeps trigger/region membership separate from camera-room selection. In particular, many authored
    // .rgn volumes live in _everywhere_; excluding that room is correct for room culling but wrong for the volume
    // inspector. Keep a dedicated all-room spatial index so Current volumes follows the authored data exactly.
    private sealed class VolumeMembershipEntry {
      public Room Room;
      public AssetInstance Instance;
      public string AssetPath;
      public Matrix Inverse;
      public RegionVolumeData RegionVolume;
      public bool IsRegion;
      public float HalfWidth, HalfHeight, HalfDepth;
      public bool Ellipsoid;
      public string ClassType;
      public string Detail;
      public float SortVolume;
    }

    public sealed class WorldVolumeInfo {
      public ulong InstanceId { get; internal set; }
      public string RoomName { get; internal set; }
      public string Description { get; internal set; }
      public string ClassType { get; internal set; }
      public string Detail { get; internal set; }
      public string AssetPath { get; internal set; }
      public bool IsRegion { get; internal set; }
    }

    private sealed class FloorMeshData {
      public GR2_Mesh Mesh;
      public float CellSize;
      public Dictionary<(int X,int Z),List<int>> Cells;
      public List<int> Overflow;
      public int TriangleCount;
    }

    private sealed class ModelFloorData {
      public readonly List<FloorMeshData> Meshes = new List<FloorMeshData>();
    }

    private sealed class ModelFloorPlacementEntry {
      public Room Room;
      public ModelFloorData Model;
      public Matrix World;
      public Matrix Inverse;
      public float WorldMinX;
      public float WorldMaxX;
      public float WorldMinZ;
      public float WorldMaxZ;
      public bool LocalXZIndependentOfY;
    }

    private sealed class RenderEntry {
      public Room Room;
      public AssetInstance Instance;
      public GR2 Model;
      public Matrix World;
      public Vector3 Center;
      public float Radius;
      public byte Kind;
    }

    private sealed class PathFollowerPoint {
      public Vector3 Position;
      public Vector3 Rotation;
      public Vector3 Direction;
    }

    private sealed class PathFollowerSegment {
      public PathFollowerPoint From;
      public PathFollowerPoint To;
      public Vector3 Direction;
      public float Start;
      public float Length;
    }

    private sealed class PathFollowerRoute {
      public readonly List<PathFollowerPoint> Points = new List<PathFollowerPoint>();
      public readonly List<PathFollowerSegment> Segments = new List<PathFollowerSegment>();
      public float Length;
    }

    private sealed class PathFollowerRuntime {
      public Room Room;
      public AssetInstance Root;
      public PathFollowerRoute Route;
      public float Speed;
      public float TurnRate;
      public readonly List<AssetInstance> Descendants = new List<AssetInstance>();
    }

    private sealed class ModelInstanceBatch {
      public Room Room;
      public GR2 Model;
      public int LodLevel;
      public readonly List<Matrix> Worlds = new List<Matrix>();
      public int StartInstance;
      public Vector3 LightSample;
      public float LightRadius;
      public bool SpeedTree;
    }

    private sealed class DecorationHookRenderEntry {
      public Room Room;
      public AssetInstance Instance;
      public GR2 Model;
      public Matrix World;
      public Vector3 Center;
      public float Radius;
      public Vector4 FallbackTint;
    }

    private sealed class MapBoundsBox {
      public float MinX,MaxX,MinZ,MaxZ,X,Z;
      public bool Large;
    }

    private sealed class MapBoundsOverride {
      public float? MinX,MaxX,MinZ,MaxZ;
      public float LargeObjectSize=5f;
      public bool HideLargeObjects;
      public readonly HashSet<string> HideAssets=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      public MapBoundsOverride(float? minX=null,float? maxX=null,float? minZ=null,float? maxZ=null,float largeObjectSize=5f,bool hideLargeObjects=false,params string[] hideAssets){
        MinX=minX;MaxX=maxX;MinZ=minZ;MaxZ=maxZ;LargeObjectSize=largeObjectSize;HideLargeObjects=hideLargeObjects;
        if(hideAssets!=null)foreach(string name in hideAssets)if(!String.IsNullOrWhiteSpace(name))HideAssets.Add(name);
      }
      public bool HasMeasuredBounds=>MinX.HasValue||MaxX.HasValue||MinZ.HasValue||MaxZ.HasValue;
    }

    private sealed class LocalLightSelection {
      public static readonly LocalLightSelection Empty = new LocalLightSelection(Array.Empty<LocalLightEntry>());
      public readonly LocalLightEntry[] Entries;
      public int Count => Entries?.Length ?? 0;
      public LocalLightSelection(LocalLightEntry[] entries) { Entries = entries ?? Array.Empty<LocalLightEntry>(); }
      public LocalLightEntry Get(int index) => index >= 0 && index < Count ? Entries[index] : null;
      public static LocalLightSelection From(LocalLightEntry[] entries, int count) {
        if(entries==null||count<=0)return Empty;
        var copy=new LocalLightEntry[count];Array.Copy(entries,copy,count);return new LocalLightSelection(copy);
      }
    }

    private sealed class PortalVisibilityEntry {
      public Room Source;
      public Room Target;
      public AssetInstance Instance;
      public Vector3 Center;
      public float Radius;
    }

    private string fqn;
    private Area area;
    private Dictionary<ulong, GR2> models = new Dictionary<ulong, GR2>();
    private Dictionary<string, GR2_Material> materials = new Dictionary<string, GR2_Material>();
    private List<Room> rooms = new List<Room>();
    // v6 resource streaming: only decoded models are added to models/render entries. The catalog itself is cheap
    // (asset id + archive path) and lets the initial frame avoid decoding an entire planet.
    private WorldModelStreamer modelStreamer;
    private WorldMaterialMetadataStreamer materialMetadataStreamer;
    private readonly Dictionary<string,List<WorldModelStreamRequest>> streamRequestsByRoom = new Dictionary<string,List<WorldModelStreamRequest>>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<ulong,List<(Room Room,AssetInstance Instance)>> streamPlacementsByAsset = new Dictionary<ulong,List<(Room Room,AssetInstance Instance)>>();
    // Direct room->asset placement slices avoid repeatedly filtering Corellia's ~345k global placements whenever a
    // shared GR2 is decoded, a room becomes current, or the startup readiness gate runs.
    private readonly Dictionary<string,Dictionary<ulong,List<(Room Room,AssetInstance Instance)>>> streamPlacementsByRoomAsset = new Dictionary<string,Dictionary<ulong,List<(Room Room,AssetInstance Instance)>>>(StringComparer.OrdinalIgnoreCase);
    private sealed class StreamRoomAssetHint { public Vector3 Min=new Vector3(float.MaxValue,float.MaxValue,float.MaxValue),Max=new Vector3(float.MinValue,float.MinValue,float.MinValue); public bool Any; }
    private readonly Dictionary<string,Dictionary<ulong,StreamRoomAssetHint>> streamRoomAssetHints = new Dictionary<string,Dictionary<ulong,StreamRoomAssetHint>>(StringComparer.OrdinalIgnoreCase);
    // Room bounds are derived from GR2 geometry, so they cannot be the only source of the first streaming
    // decision.  This inexpensive XZ grid is built from the already-authored instance transforms and gives the
    // spawn path a non-circular, physical working set before any room shell has been decoded.
    private readonly Dictionary<(int X,int Z),HashSet<ulong>> streamBootstrapAssetsByCell = new Dictionary<(int X,int Z),HashSet<ulong>>();
    // The asset-only bootstrap cannot tell the integration stage which room owns a nearby placement. Keep the same
    // cheap XZ broad phase for room owners as well so startup can gate/integrate the actual local cells rather than
    // merely decoding a GR2 whose placement remains invisible because its room never entered the working set.
    private readonly Dictionary<(int X,int Z),HashSet<string>> streamBootstrapRoomsByCell = new Dictionary<(int X,int Z),HashSet<string>>();
    private readonly Dictionary<ulong,WorldModelStreamRequest> streamRequestsByAsset = new Dictionary<ulong,WorldModelStreamRequest>();
    private const float StreamBootstrapCellSize = 48f;
    // Assets referenced by the current portal/visibility working set.  They are a residency pin, not a second
    // cache: a large planet may evict a cold room, but must never evict the floor/shell currently under camera.
    private readonly HashSet<ulong> activeStreamAssetIds = new HashSet<ulong>();
    // Queue persistence is intentionally separate from residency pins. A large neighbour can have more assets than
    // the bounded GPU/CPU hot slice; once one of those assets is admitted to the bounded decode queue it must remain
    // queued for as long as its owner room is still demanded, otherwise the rolling neighbour slice cancels its own
    // tail every frame and the room never converges.
    private readonly HashSet<ulong> queuedStreamDemandAssetIds = new HashSet<ulong>();
    // Current/startup-room assets are the latency-sensitive subset of the active working set. Background workers
    // still decode neighbours, but render-thread placement/GPU commits always drain this set first so a doorway or
    // spawn shell cannot sit behind already-decoded speculative rooms.
    private readonly HashSet<ulong> urgentStreamAssetIds = new HashSet<ulong>();
    // Texture/MAT demand is intentionally narrower than geometry demand. The current room and active sky receive
    // strong demand, while visible neighbours get a bounded progressive slice so a doorway is textured before the
    // camera crosses it without letting planet-wide DDS work compete with structural GR2 streaming.
    private readonly HashSet<ulong> materialStreamAssetIds = new HashSet<ulong>();
    // Keep room demand separately from asset demand. A shared prop GR2 can have thousands of placements across a
    // planet; integrating every placement when that GR2 decodes turns a local stream into a whole-world rebuild.
    // Only placements belonging to rooms in this working set are integrated on the render thread.
    private readonly HashSet<string> activeStreamRoomNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> previousStreamRoomNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    // Collision-floor construction is substantially heavier than adding render entries. Only the room actually under
    // the camera (plus a phase-trigger owner at the same point) gets new floor meshes; neighbours stay geometry-only.
    private readonly HashSet<string> floorStreamRoomNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> previousFloorStreamRoomNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    // Immutable readiness target for the loading overlay. The active portal set is intentionally dynamic and can
    // grow while decoding creates additional room bounds; it is therefore unsuitable as a completion condition.
    private readonly HashSet<ulong> initialStreamAssetIds = new HashSet<ulong>();
    private readonly HashSet<string> initialStreamRoomNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    // Direct neighbours are prefetched behind the loading overlay, but are not part of its completion barrier. This
    // keeps v6 local: the spawn room is guaranteed complete without accidentally turning a broad Corellia visibility
    // ring into Jedipedia's planet-wide first-frame gate.
    private readonly HashSet<string> initialStreamPrefetchRoomNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<ulong,float> initialStreamAssetDistances = new Dictionary<ulong,float>();
    private readonly HashSet<AssetInstance> streamIndexedInstances = new HashSet<AssetInstance>();
    private readonly HashSet<AssetInstance> streamFloorIndexedInstances = new HashSet<AssetInstance>();
    private readonly HashSet<GR2> streamedWorldModels = new HashSet<GR2>();
    private readonly Dictionary<GR2,ulong> streamedModelAssetIds = new Dictionary<GR2,ulong>();
    private readonly Queue<GR2> pendingModelGpuUploads = new Queue<GR2>();
    private readonly HashSet<GR2> queuedModelGpuUploads = new HashSet<GR2>();
    private sealed class PendingStreamedModelIntegration { public ulong AssetId; public string RoomName; public GR2 Model; public List<(Room Room,AssetInstance Instance)> Placements; public int NextPlacement; }
    private readonly Queue<PendingStreamedModelIntegration> pendingStreamedModelIntegrations = new Queue<PendingStreamedModelIntegration>();
    private readonly HashSet<(ulong AssetId,string RoomName)> queuedStreamedModelIntegrations = new HashSet<(ulong AssetId,string RoomName)>();
    // Floor/collision indexing is useful for the authoritative room locator, but it is not required to draw a model.
    // Keep it off the render-entry commit path: on Corellia the old one-new-floor-model-per-frame guard accidentally
    // reduced visible room assembly to roughly one unique GR2 per frame.
    private sealed class PendingStreamedFloorIntegration { public ulong AssetId; public string RoomName; public GR2 Model; public List<(Room Room,AssetInstance Instance)> Placements; public int NextPlacement; }
    private readonly Queue<PendingStreamedFloorIntegration> pendingStreamedFloorIntegrations = new Queue<PendingStreamedFloorIntegration>();
    private readonly HashSet<(ulong AssetId,string RoomName)> queuedStreamedFloorIntegrations = new HashSet<(ulong AssetId,string RoomName)>();
    private sealed class PendingStreamedModelMaterials { public GR2 Model; public Queue<GR2_Material> Materials; }
    // While a streamed material is in the bounded prewarm queue, draw it with its already parsed MAT metadata and a
    // textureless fallback instead of synchronously reading DDS files from ResolvePieceMaterial(). Geometry can thus
    // appear immediately and texture detail can catch up without stalling the frame that first sees a room.
    private readonly HashSet<GR2_Material> pendingStreamedMaterialPrepares = new HashSet<GR2_Material>();
    private readonly HashSet<GR2_Material> streamedWorldMaterials = new HashSet<GR2_Material>();
    private readonly HashSet<GR2_Material> streamedMaterialResourcesPrepared = new HashSet<GR2_Material>();
    // Streaming needs a room-space broad phase before the corresponding GR2 shell has been decoded. Keep these
    // conservative bounds separate from Room.Visibility*: they are demand-planning hints only and must never make
    // a coarse placement-origin box participate in final visibility/culling.
    private sealed class RoomStreamingBounds { public Vector3 Min,Max,Center; public float Radius; public bool Coarse; }
    private readonly Dictionary<string,RoomStreamingBounds> roomStreamingBounds = new Dictionary<string,RoomStreamingBounds>(StringComparer.OrdinalIgnoreCase);
    // Zero-authored rooms need Jedipedia-style model-derived bounds, but only after every streamed GR2 referenced by
    // that room has reached the view. A partial heightmap/water-only box is worse than no box because it can make the
    // wrong overlapping Corellia room authoritative before its hangar shell exists.
    private readonly HashSet<string> streamRoomsNeedingExactBounds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<PendingStreamedModelMaterials> pendingStreamedModelMaterials = new Queue<PendingStreamedModelMaterials>();
    private readonly HashSet<GR2> queuedStreamedModelMaterials = new HashSet<GR2>();
    private readonly HashSet<GR2> streamedModelMaterialsReady = new HashSet<GR2>();
    private int streamCatalogAssetCount;
    private bool initialStreamLoading;
    // Keep the locally completed spawn shell visible for a short hand-off window while streamed floor/dPVS data
    // replaces the provisional outdoor room selected before the hangar existed. This is visibility-only; the normal
    // residency/eviction policy remains active and the grace expires automatically.
    private long initialStreamVisibilityKeepUntilFrame;
    // Decoding is asynchronous, but indexing instances and creating D3D buffers are render-thread work. Keep
    // those commits deliberately small: a whole decoded room arriving in one frame used to cause multi-frame hitches.
    private const int MaxDecodedModelsPerFrame = 12;
    private const int MaxModelGpuUploadsPerFrame = 4;
    private const int MaxMaterialPreparesPerFrame = 4;
    private const int MaxStreamPlacementIntegrationsPerFrame = 512;
    private const int MaxStreamFloorPlacementsPerFrame = 64;
    private Vector3 streamPrefetchAnchor = new Vector3(float.NaN,float.NaN,float.NaN);
    private Vector3 streamPrefetchLook = new Vector3(float.NaN,float.NaN,float.NaN);
    private string streamPrefetchRoom = String.Empty;
    // Prediction is recomputed only after meaningful movement/rotation, but its demand must survive every frame in
    // between. Otherwise activeStreamRoomNames is cleared, CancelQueuedExcept() cancels the predicted queue tail,
    // and standing still literally stops the room in front of the camera from loading.
    private readonly Dictionary<string,int> cameraAheadStreamRooms = new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);
    // The primary forward-projected room is more than speculative background work: at a doorway it is the room the
    // camera is about to enter. Keep a tiny separate urgent set so its nearest shell assets can be promoted without
    // turning the entire visible-neighbour ring into high-priority work.
    private readonly HashSet<string> urgentCameraAheadStreamRooms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    // Keep both sides of a doorway resident for a short hand-off window. Corellia has several stacked/overlapping
    // streaming cells; the authoritative camera room can flip as a newly streamed floor becomes available. Without a
    // grace window the previous room immediately fell out of visibility/residency and could disappear while the next
    // room was still assembling, producing the large empty/green gaps seen when crossing the spaceport threshold.
    private readonly Dictionary<string,long> streamTransitionRoomGrace = new Dictionary<string,long>(StringComparer.OrdinalIgnoreCase);
    private string lastStreamAnchorRoom = String.Empty;
    private const long StreamTransitionGraceFrames = 180;
    // PugTools cannot run Jedipedia's native dPVS solver. During a room-boundary hand-off keep a
    // bounded slice of the old room's authored VisibleRooms alive as a conservative dPVS fallback.
    // These are precisely the city-shell/backdrop cells that otherwise vanish for a frame range when
    // the semantic camera room flips before the new room's full portal/floor state is established.
    private const long StreamTransitionVisibleGraceFrames = 120;
    private const int StreamTransitionVisibleGraceLimit = 24;
    // Room instances are static. Resolve parent chains once at load instead of once per render pass;
    // the old path recomputed the same absolute transform for terrain/models/water and four shadow
    // cascades every frame.
    private readonly Dictionary<AssetInstance,Matrix> instanceWorldTransforms = new Dictionary<AssetInstance,Matrix>();
    private readonly List<PathFollowerRuntime> pathFollowers = new List<PathFollowerRuntime>();
    private readonly Dictionary<AssetInstance,TerrainGpu> terrainGpu = new Dictionary<AssetInstance,TerrainGpu>();
    private readonly Dictionary<(int Width,int Depth),SharedTerrainIndexGpu> terrainIndexCache = new Dictionary<(int Width,int Depth),SharedTerrainIndexGpu>();
    private readonly Dictionary<AssetInstance,WaterGpu> waterGpu = new Dictionary<AssetInstance,WaterGpu>();
    private readonly Dictionary<string,GR2_Material> terrainMaterials = new Dictionary<string,GR2_Material>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string,ShaderResourceView> textureCache = new Dictionary<string,ShaderResourceView>(StringComparer.OrdinalIgnoreCase);
    // Shared environment/light/water textures used to stay resident for the lifetime of an area. On the large
    // planets this can easily consume several gigabytes of VRAM even though only a small camera neighbourhood is
    // visible. Track last use and evict cold, transient entries like Jedipedia's LRU tile cache. Textures referenced
    // by persistent GPU records (map art and static lights) are pinned until those records are rebuilt; water
    // refreshes its shared references on each visible draw and can therefore remain transient.
    private readonly Dictionary<string,long> textureLastUseFrame = new Dictionary<string,long>(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> pinnedTexturePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    // Transient light maps currently bound to the effect must survive the shared-texture LRU until another light
    // selection replaces them. Unlike pinned map art, this set is rebuilt on every actual light bind.
    private readonly HashSet<string> boundLocalLightTexturePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private int appliedTextureMipSkip = -1;
    // Jedipedia keeps a persistent clicked selection and outlines its current receiver bounds. PugTools already had
    // an exact one-shot triangle inspector; keep the same hit test but retain the winning render entry so the object
    // remains visually identified while the user moves the camera.
    private sealed class WorldPickCandidate {
      public string Kind;
      public string Key;
      public float Distance;
      public Vector3 HitPoint;
      public Vector3 Center;
      public float Radius;
      public RenderEntry RenderEntry;
      public GR2 Model;
      public GR2_Mesh Mesh;
      public GR2_Mesh_Piece Piece;
      public WorldNpcPlacement Npc;
      public WorldSpnPlacement Spn;
      public UtilityRenderEntry Utility;
      public AreaPath Path;
    }

    private Buffer selectedWorldBoxBuffer;
    private RenderEntry selectedWorldRenderEntry;
    private WorldNpcPlacement selectedWorldNpcPlacement;
    private WorldSpnPlacement selectedWorldSpnPlacement;
    // Plain left-click or a short right-click service interaction is deliberately separate from the persistent
    // Ctrl+left-click inspector. Service clicks must not create selection labels/bounds or replace an inspection.
    private WorldNpcPlacement interactionWorldNpcPlacement;
    private WorldSpnPlacement interactionWorldSpnPlacement;
    private UtilityRenderEntry selectedWorldUtilityEntry;
    private AreaPath selectedWorldPath;
    private Vector3 selectedWorldPathHit;
    private string selectedWorldModelDetails = String.Empty;
    private string selectedWorldModelSummary = String.Empty;
    private int selectedWorldCycleIndex = -1;
    private int selectedWorldCycleCount;
    private int lastWorldPickX = Int32.MinValue;
    private int lastWorldPickY = Int32.MinValue;
    private int lastWorldPickIndex = -1;
    private string lastWorldPickSignature = String.Empty;
    private string selectedWorldPickKey = String.Empty;
    private readonly List<LineGpu> roadGpu = new List<LineGpu>();
    private readonly List<LineGpu> mapNoteFallbackGpu = new List<LineGpu>();
    private readonly List<MapArtGpu> mapArtGpu = new List<MapArtGpu>();
    private bool mapArtPrepared;
    private readonly Dictionary<string,MapNoteIconGpu> mapNoteIconGpu = new Dictionary<string,MapNoteIconGpu>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<GR2_Material,long> materialLastUseFrame = new Dictionary<GR2_Material,long>();
    private readonly HashSet<GR2> modelGeometryPrepared = new HashSet<GR2>();
    private readonly Dictionary<GR2,long> modelGeometryLastUseFrame = new Dictionary<GR2,long>();
    private long worldRenderFrame;
    private readonly Dictionary<AssetInstance,List<DynamicDetailGpu>> dynamicDetailGpu = new Dictionary<AssetInstance,List<DynamicDetailGpu>>();
    private readonly Dictionary<string,GR2_Material> dynamicDetailMaterials = new Dictionary<string,GR2_Material>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<uint,GR2> dynamicDetailMeshModels = new Dictionary<uint,GR2>();
    private readonly Dictionary<AssetInstance,List<DynamicDetailMeshBatch>> dynamicDetailMeshBatches = new Dictionary<AssetInstance,List<DynamicDetailMeshBatch>>();
    // Stronghold decoration placement fields (Jedipedia viewer/hooks.js). These GR2s are not part of the
    // area's AssetIdMap, so keep them separate from ordinary world models and expand only the layout IDs that
    // actually occur in the loaded Stronghold.
    private readonly Dictionary<string,GR2> strongholdHookModels = new Dictionary<string,GR2>(StringComparer.OrdinalIgnoreCase);
    private readonly List<DecorationHookRenderEntry> decorationHookRenderEntries = new List<DecorationHookRenderEntry>();
    // Heightmap floor locator for room selection. Jedipedia uses a spatial floor index rather than scanning
    // every terrain tile on every camera update; keep the same idea here so room-culling stays cheap.
    private const float FloorCellSize = 16f;
    private readonly Dictionary<(int X,int Z),List<HeightMapFloorEntry>> heightMapFloorGrid = new Dictionary<(int X,int Z),List<HeightMapFloorEntry>>();
    private readonly List<HeightMapFloorEntry> heightMapFloors = new List<HeightMapFloorEntry>();
    // Jedipedia also uses authored RoomBound/Region/Trigger placements when no close floor wins. These
    // transformed volumes catch buildings whose room-level visibility AABB is too coarse or whose floor is GR2
    // geometry rather than a heightmap.
    private const float RoomPlacementCellSize = 32f;
    private const int MaxRoomPlacementCells = 4096;
    private readonly Dictionary<(int X,int Z),List<RoomPlacementEntry>> roomPlacementGrid = new Dictionary<(int X,int Z),List<RoomPlacementEntry>>();
    private readonly List<RoomPlacementEntry> roomPlacementGlobal = new List<RoomPlacementEntry>();
    private readonly Dictionary<(int X,int Z),List<VolumeMembershipEntry>> volumeMembershipGrid = new Dictionary<(int X,int Z),List<VolumeMembershipEntry>>();
    private readonly List<VolumeMembershipEntry> volumeMembershipGlobal = new List<VolumeMembershipEntry>();
    private readonly Dictionary<string,VolumeMembershipEntry> volumeMembershipByKey = new Dictionary<string,VolumeMembershipEntry>(StringComparer.OrdinalIgnoreCase);
    // Jedipedia's camera-room lookup also ray-tests walkable Granny/collision geometry. Keep one local-space
    // triangle index per unique GR2 mesh and a cheap world-XZ placement index, instead of baking millions of
    // transformed floor triangles for every repeated building/prop placement.
    private const float ModelFloorPlacementCellSize = 16f;
    private const int MaxModelFloorPlacementCells = 4096;
    private const int FloorMeshIndexThreshold = 2048;
    private const int FloorMeshTargetTrianglesPerCell = 64;
    private readonly Dictionary<GR2,ModelFloorData> modelFloorData = new Dictionary<GR2,ModelFloorData>();
    private readonly Dictionary<(int X,int Z),List<ModelFloorPlacementEntry>> modelFloorPlacementGrid = new Dictionary<(int X,int Z),List<ModelFloorPlacementEntry>>();
    private readonly List<ModelFloorPlacementEntry> modelFloorPlacementGlobal = new List<ModelFloorPlacementEntry>();

    // View-distance spatial index. A hard far plane alone still left the CPU walking every placement on huge
    // planets four or five times per frame. Index small/normal instances by their centre and keep only unusually
    // huge instances in a global fallback list; exact sphere tests are still applied before every draw.
    private const float RenderCellSize = 256f;
    private const float RenderIndexedRadiusLimit = 256f;
    private const byte RenderKindTerrain = 1;
    private const byte RenderKindModel = 2;
    private const byte RenderKindWater = 3;
    private readonly Dictionary<(int X,int Z),List<RenderEntry>> renderGrid = new Dictionary<(int X,int Z),List<RenderEntry>>();
    private readonly List<RenderEntry> renderGlobal = new List<RenderEntry>();
    // When local room streaming is active, walking the camera's entire XZ grid still touches thousands of entries
    // belonging to non-active Corellia cells. Keep a second lightweight room->entry index so current+adjacent room
    // rendering can skip those entries before any distance/frustum/LOD/material work.
    private readonly Dictionary<string,List<RenderEntry>> renderEntriesByRoom = new Dictionary<string,List<RenderEntry>>(StringComparer.OrdinalIgnoreCase);
    // Authored dPVS occlusion geometry: ordinary model meshes with Granny LOD -3 plus placements explicitly marked
    // OCCLUDER_ONLY. These never enter the visible model pass, but v12 can submit them depth-only before world art.
    private readonly List<RenderEntry> occluderRenderEntries = new List<RenderEntry>();
    // Path-follower descendants are dynamic and therefore live in renderGlobal, but Walking Mode queries the floor
    // several times per frame. Keep just the model subset separately so riding a tram does not scan every global
    // terrain/water/large-radius entry on each step/gravity test.
    private readonly List<RenderEntry> walkingPathFollowerRenderEntries = new List<RenderEntry>();
    private Buffer regularModelInstanceBuffer;
    private int regularModelInstanceCapacity;
    private float[] regularModelInstanceScratch = Array.Empty<float>();
    private readonly List<Matrix> regularModelFrameInstances = new List<Matrix>(2048);
    private readonly Dictionary<GR2,bool> regularModelInstancingSafe = new Dictionary<GR2,bool>();
    // Elevator/bookmark teleports can jump into a completely cold part of a huge planet. While the destination
    // dialog is still open, opportunistically prepare a small nearest-first working set on the render thread.
    // This hides most first-frame GR2/DDS upload stalls without keeping the whole planet resident.
    private readonly object teleportWarmupLock = new object();
    private Vector3 teleportWarmupRequestedPosition;
    private bool teleportWarmupRequestPending;
    private bool teleportWarmupCancelPending;
    private readonly Queue<GR2> teleportWarmupModels = new Queue<GR2>();
    private readonly HashSet<GR2> teleportWarmupQueued = new HashSet<GR2>();
    // Continuous camera-neighbourhood warmup. The renderer is CPU/upload bound when several cold models/materials
    // first enter view at once; D3D11 then waits idle even on a fast GPU. Prepare a tiny nearest/ahead-of-camera
    // working set over several frames instead of allowing a single movement frame to synchronously upload it all.
    private readonly Queue<GR2> cameraWarmupModels = new Queue<GR2>();
    private readonly HashSet<GR2> cameraWarmupQueued = new HashSet<GR2>();
    private Vector3 cameraWarmupAnchor = new Vector3(float.NaN,float.NaN,float.NaN);
    private string cameraWarmupRoomName = String.Empty;
    // Jedipedia honours the signed LOD class embedded in every BWAG mesh and the thresholds from
    // /resources/art/LODSchemas3.lod.  Rendering every non-negative mesh at once is both visually wrong and a
    // major draw-call/triangle multiplier on open-world assets, so cache the same model-level information here.
    private const float GrannyLegacyLodThreshold = 2f;
    private const float GrannyLodProjectedSizeScale = 100f;
    private readonly Dictionary<string,float[]> lodSchemas = new Dictionary<string,float[]>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<GR2,int[]> visualLodLevels = new Dictionary<GR2,int[]>();
    // Local-light selection used to rescan every instance in every visible room for every object draw.
    // That made the render thread CPU-bound while the GPU waited. Build a static XZ spatial index once per area,
    // preload the projector textures once, and reuse scratch arrays/bindings during the frame.
    private const float LocalLightCellSize = 32f;
    // The base material pass keeps four lights for compatibility with the existing shader resources. Jedipedia
    // then re-draws opaque/test receivers additively for every additional local light. Cap the offline renderer's
    // receiver list to a generous 16 after projector/category/room culling so pathological authoring cannot explode
    // draw calls while ordinary interiors are no longer limited to the nearest four lights.
    private const int LocalLightBaseSlots = 4;
    private const int MaxReceiverLocalLights = 16;
    // Jedipedia's 32-unit light grid is a broad-phase index only. It never substitutes the centre of that
    // enormous cell for the receiver's real position. Keep a much finer quantisation solely for sharing the
    // selected four-light set between genuinely nearby PugTools draws.
    private const float LocalLightSelectionCellSize = 1f;
    private const float LocalLightGridRangeLimit = 256f;
    private readonly Dictionary<(int X,int Z),List<LocalLightEntry>> localLightGrid = new Dictionary<(int X,int Z),List<LocalLightEntry>>();
    private readonly List<LocalLightEntry> localLightGlobal = new List<LocalLightEntry>();
    // Radius is part of the cache key because Jedipedia tests light volumes against receiver bounds, not merely
    // the placement origin. This is important for room shells and other large GR2s whose origin can be outside
    // every local light that visibly touches the mesh.
    private readonly Dictionary<(int X,int Y,int Z,int Radius,string Room,byte Kind),LocalLightSelection> localLightSelectionCache = new Dictionary<(int X,int Y,int Z,int Radius,string Room,byte Kind),LocalLightSelection>();
    private readonly HashSet<LocalLightEntry> localLightCandidateScratch = new HashSet<LocalLightEntry>();
    private readonly LocalLightEntry[] localLightBest = new LocalLightEntry[MaxReceiverLocalLights];
    private readonly float[] localLightBestDistance = new float[MaxReceiverLocalLights];
    private readonly LocalLightEntry[] lastLocalLightSelection = new LocalLightEntry[LocalLightBaseSlots];
    private readonly Vector4[] localLightPosRangeScratch = new Vector4[LocalLightBaseSlots];
    private readonly Vector4[] localLightColorScratch = new Vector4[LocalLightBaseSlots];
    private readonly Vector4[] localLightDirScratch = new Vector4[LocalLightBaseSlots];
    private readonly Matrix[] localLightProjectorScratch = new Matrix[LocalLightBaseSlots];
    private readonly Vector4[] localLightProjectorParamsScratch = new Vector4[LocalLightBaseSlots];
    private readonly ShaderResourceView[] localLightIlluminationScratch = new ShaderResourceView[LocalLightBaseSlots];
    private readonly ShaderResourceView[] localLightFalloffScratch = new ShaderResourceView[LocalLightBaseSlots];
    private readonly ShaderResourceView[] localLightRampScratch = new ShaderResourceView[LocalLightBaseSlots];
    private int lastLocalLightCount = -1;
    private string localLightVisibilityScope;
    private LocalLightSelection currentLocalLightSelection = LocalLightSelection.Empty;
    // Conservative portal graph built from /engine/portal.p placements. This is not the proprietary/native dPVS
    // occluder solver, but mirrors Jedipedia's first visibility layer: room transitions are driven by authored
    // PortalTarget links and only portals/rooms intersecting the camera frustum are expanded.
    private readonly Dictionary<string,List<PortalVisibilityEntry>> roomPortals = new Dictionary<string,List<PortalVisibilityEntry>>(StringComparer.OrdinalIgnoreCase);
    // Direct room neighbourhood used by local room streaming. Portal and conservative physical links live here.
    // Authored VisibleRooms is kept separately and ranked at demand time: before streamed GR2 bounds exist, permanently
    // truncating that list from coarse placement-origin boxes can discard the real doorway/hangar neighbour. Jedipedia
    // builds its dPVS only after all GR2 bounds are known, so it never has that bootstrap ordering problem.
    private readonly Dictionary<string,HashSet<string>> roomStreamingNeighbors = new Dictionary<string,HashSet<string>>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string,List<Room>> roomStreamingAuthoredVisible = new Dictionary<string,List<Room>>(StringComparer.OrdinalIgnoreCase);
    private Room currentCameraRoom;
    private Room displayCameraRoom;
    private readonly HashSet<string> skyRoomNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private readonly object settingsLock = new object();
    private WorldRenderSettings settings = new WorldRenderSettings();
    private WorldEffect fx;
    private InputLayout inputLayout;
    private InputLayout skinnedLayout;
    private InputLayout instancedLayout;
    private InputLayout dynamicDetailLayout;
    private readonly FpsCamera camera = new FpsCamera();
    private Point lastMousePos;
    private float cameraSpeed=1f;
    private const float MinCameraSpeed=.05f;
    private const float MaxCameraSpeed=1000f;

    // Jedipedia orthographic plan/cutaway camera. The projection box, camera height and movement scale all follow
    // the same zoom value, so zooming into a room also lowers the near-plane cut beneath roofs/upper storeys.
    private bool orthographicActive;
    private bool appliedOrthographicProjection;
    private float appliedOrthographicHalfHeight=-1f;
    private float orthographicZoom=1f;
    private float orthographicZoomTarget=1f;
    private float orthographicFloorY=float.NaN;
    private float orthographicPitch=(float)Math.PI/2f;
    private Vector3 orthographicHeading=Vector3.UnitZ;
    private float perspectivePitchBeforeOrthographic;
    private bool havePerspectivePitchBeforeOrthographic;
    private bool orthographicPanning;
    private Point orthographicPanStart;
    private Vector3 orthographicPanCameraStart;
    private const float OrthographicReferenceDistance=5f;
    private const float OrthographicZoomMin=.05f;
    private const float OrthographicZoomMax=20f;
    private const float OrthographicZoomSensitivity=.0018f;
    private const float OrthographicZoomSmoothingSeconds=.12f;
    private const float OrthographicFloorClearance=.05f;
    private const float OrthographicHeightPerBox=1.5f;
    private const float OrthographicHeightClipShare=.5f;
    private const float OrthographicMinPitch=.35f;
    private const float OrthographicMaxPitch=(float)Math.PI/2f;
    private const float OrthographicMoveSpeedScale=4.5f;

    // Jedipedia-style navigation map. M temporarily switches the viewport to an authored-world
    // top-down render without changing the toolbar render mode. A click teleports to the highest
    // heightmap surface under the cursor; drag pans and the wheel zooms.
    private bool mapOpen;
    private volatile bool miniMapCaptureRequested;
    private bool mapKeyWasDown;
    private bool escapeKeyWasDown;
    private bool mapPointerDown;
    private bool mapPointerDragged;
    private Point mapPointerStart;
    private Vector2 mapPanStartCenter;
    private Vector2 mapCenter;
    private float mapZoom=1f;
    private float mapBaseHeight=20f;
    private float mapExtentMinX=-10f,mapExtentMaxX=10f,mapExtentMinZ=-10f,mapExtentMaxZ=10f;
    // Jedipedia's map trims isolated placements and giant scenery shells when they would otherwise make the
    // playable part of an area a tiny speck. Keep both extents so the user can switch back to the literal full area.
    private float mapFullExtentMinX=-10f,mapFullExtentMaxX=10f,mapFullExtentMinZ=-10f,mapFullExtentMaxZ=10f;
    private float mapClusterExtentMinX=-10f,mapClusterExtentMaxX=10f,mapClusterExtentMinZ=-10f,mapClusterExtentMaxZ=10f;
    private volatile bool mapShowEntireArea;
    private bool mapHasClusterExtent;
    private float mapVisibleWidth=20f;
    private float mapVisibleHeight=20f;
    private Vector3 mapCameraPosition;
    // Fixed-pixel player/camera arrow drawn over the interactive M map. This uses Jedipedia's embedded copy of
    // SWTOR's original 22x27 player marker (pivot 11,6), so the world map and minimap use the same authored art.
    private const float MapMinZoom=.025f;
    private const float MapMaxZoom=1f;
    private const float MapTeleportClearance=.5f;
    // Ported from Jedipedia map.js. A crop must keep at least 95% of ordinary placements, cross a real empty
    // gap, and reduce the map footprint substantially before it is offered as the default view.
    private const float MapClusterCoverage=.95f;
    private const float MapClusterMinGap=.10f;
    private const float MapClusterMaxFootprint=.60f;
    private const int MapClusterMaxPasses=4;
    private const int MapClusterMinSmallCount=20;
    private const float MapClusterMinSmallShare=.50f;
    private const float MapClusterLargeObjectSize=5f;

    // Jedipedia map.js MAP_BOUNDS_OVERRIDES. These are deliberately keyed by numeric area id: the same internal
    // names can have variants, while the measured coordinates belong to one concrete area resource.
    private static readonly Dictionary<string,MapBoundsOverride> MapBoundsOverrides=new Dictionary<string,MapBoundsOverride>(StringComparer.Ordinal){
      ["4611686356715258910"]=new MapBoundsOverride(minX:-147f,maxX:-97f,minZ:47f,maxZ:77f),
      ["4611686358155127000"]=new MapBoundsOverride(maxX:-31f,maxZ:-231.5f),
      ["4611686301284054000"]=new MapBoundsOverride(minX:-9f,maxX:8f,minZ:-4.5f,maxZ:4.5f,largeObjectSize:10f,hideLargeObjects:true,hideAssets:new[]{"all_item_neon_trim_hue-able_8m.gr2"}),
      ["4611686300770584000"]=new MapBoundsOverride(minX:-5f,maxX:3f,minZ:-7f,maxZ:8.5f,hideAssets:new[]{
        "all_arch_neu_city_building_09.gr2","all_arch_neu_city_building_16.gr2","all_arch_neu_city_building_18.gr2",
        "all_arch_neu_city_set_roof_2x2.gr2","all_arch_neu_city_set_roof_flat.gr2","str_arch_neu_city_vent_lod.gr2",
        "all_item_tech_panel_01.gr2","all_item_tech_panel_02.gr2","all_item_tech_panel_03.gr2","all_item_tech_panel_04.gr2",
        "str_arch_coruscant_exterior_01.gr2","str_arch_coruscant_exterior_02.gr2","str_arch_coruscant_exterior_03.gr2",
        "str_arch_nar_vista_wall_mix_double.gr2","str_arch_neu_city_antenna_lod.gr2"}),
      ["4611686301174784000"]=new MapBoundsOverride(minX:-5.5f,maxX:5.5f,minZ:-19.5f,maxZ:8f,hideAssets:new[]{
        "str_arch_nar_exterior_01.gr2","str_arch_nar_exterior_02.gr2","str_arch_nar_fan_housing.gr2","all_arch_neu_city_set_roof_flat.gr2"}),
      ["4611686351279967011"]=new MapBoundsOverride(largeObjectSize:20f,hideAssets:new[]{"str_arch_rep_guildship_exterior.gr2"}),
      ["4611686351279967010"]=new MapBoundsOverride(largeObjectSize:20f,hideAssets:new[]{"all_arch_imp_space_room_bridge_exterior.gr2","veh_imp_capital_destroyer_bridge_view.gr2"}),
      ["4611686044198570319"]=new MapBoundsOverride(maxX:17f,minZ:-12f,maxZ:12f,hideAssets:new[]{"ald_arch_battleground_turret_tower_center.gr2"}),
      ["4611686255030930011"]=new MapBoundsOverride(minX:-15f,maxX:15f,minZ:-14f,maxZ:10f),
      ["4611686343808730000"]=new MapBoundsOverride(minX:-22f,maxX:25f,minZ:-12f,maxZ:12f),
      ["4611686349514817006"]=new MapBoundsOverride(minX:-7f,maxX:7f,minZ:-8f,maxZ:8f),
      ["4611686354954347004"]=new MapBoundsOverride(minX:-15f,maxX:13f,minZ:-12f,maxZ:10f),
      ["4611686307922114000"]=new MapBoundsOverride(minX:-18f,maxX:17f,minZ:-11f,maxZ:11f),
      ["4611686309279494000"]=new MapBoundsOverride(minZ:-2f,maxZ:15f,hideAssets:new[]{
        "all_arch_neu_city_set_roof_rusted.gr2","bot_arch_metal_accessories_grain_elevator.gr2","czk_arch_city_walkway_fill_64m.gr2",
        "van_arch_ext_facade_02_96.gr2","van_arch_huttball_vista_buildings_yellow_main_lower_walls.gr2","van_arch_huttball_vista_refinery.gr2",
        "van_arch_huttball_vista_smokestacks.gr2","van_arch_vandin_rocks_purple_side.gr2","van_arch_vandin_rocks_yellow_side.gr2",
        "van_item_huttball_vista_yellow_right_building_01.gr2"}),
      ["4611686051463571770"]=new MapBoundsOverride(hideAssets:new[]{
        "veh_imp_capital_destroyer.gr2","veh_imp_capital_destroyer_lod02.gr2","veh_imp_capital_destroyer_lod03.gr2","veh_rep_capital_ship.gr2"}),
      ["4611686351475197004"]=new MapBoundsOverride(minX:-24f,maxX:11f,minZ:-20f,maxZ:12.5f),
      ["4611686019802843831"]=new MapBoundsOverride(minX:-110f,maxX:100f,minZ:-106f,maxZ:75f),
      ["4611686357389147000"]=new MapBoundsOverride(maxZ:210f)
    };

    private bool makeScreenshot;
    public bool _disposed;
    public List<string> ignoreList = new List<string>{"collision","dbo","fadeportal","occluder"};
    private readonly ShadowMap[] shadowMaps = new ShadowMap[4];
    private readonly Matrix[] shadowMatrices = new Matrix[4];
    private readonly float[] shadowDistances = {1.5f,4.5f,12.5f,25f};
    private WorldShadowQuality appliedShadowQuality = (WorldShadowQuality)(-1);
    private int appliedShadowResolution;
    private static readonly float[] terrainLodDistances = {50f,120f,250f};
    // Keep SWTOR/Jedipedia's authored static_clip_distance for fog/environment behaviour, but use a stable
    // user-facing hard render budget for the World Browser. Some shipped room schemes author very short clip
    // distances; using those values as the D3D far plane made 10x look like only a few nearby tiles. The spatial
    // grid, LOD selection and instancing still ensure geometry outside this budget is never submitted.
    private const float DefaultStaticClipDistance = 100f;
    private const float DefaultViewDistanceScale = 3f;
    private const float CameraFarPerViewScale = 500f;
    private const float SkyFarDistance = 100000f;
    private const float RangeCullPadding = 2f;
    private const float FrustumCullPadding = 1f; // retained for shadow/debug helper bounds
    private float activeClipDistance = 300f;
    private float appliedCameraFar = -1f;
    private Vector3 boundsMin = new Vector3(-10,-10,-10), boundsMax = new Vector3(10,10,10);
    private Matrix mapView = Matrix.Identity, mapProj = Matrix.Identity;
    private float elapsed;
    private Texture2D sceneTexture;
    private RenderTargetView sceneRenderTarget;
    private ShaderResourceView sceneShaderResource;
    private ShaderResourceView sceneDepthShaderResource;
    private readonly Texture2D[] taaHistoryTextures = new Texture2D[2];
    private readonly RenderTargetView[] taaHistoryRenderTargets = new RenderTargetView[2];
    private readonly ShaderResourceView[] taaHistoryShaderResources = new ShaderResourceView[2];
    private Texture2D aaTempTexture;
    private RenderTargetView aaTempRenderTarget;
    private ShaderResourceView aaTempShaderResource;
    private int taaHistoryIndex;
    private bool taaHistoryValid;
    private bool hasPreviousViewProj;
    private bool taaReprojectionValid;
    private Matrix previousViewProj = Matrix.Identity;
    private Matrix taaReprojection = Matrix.Identity;
    private Matrix frameProjection = Matrix.Identity;
    private Vector2 taaJitterPixels = Vector2.Zero;
    private int taaFrame;
    private volatile bool temporalHistoryResetRequested;
    private ShaderResourceView defaultWaterNormal;
    private ShaderResourceView defaultWaterDepth;
    private ulong placeableCameraAssetId;

    public View_AREA(IntPtr hInstance, Form form, string panelName="", WorldRenderBackend backend=WorldRenderBackend.Hardware) : base(hInstance,panelName) {
      Window=form; RenderPanelName=panelName; Enable4XMsaa=false; DriverType=backend==WorldRenderBackend.Warp?SlimDX.Direct3D11.DriverType.Warp:SlimDX.Direct3D11.DriverType.Hardware;
      ClientHeight=form.Controls.Find(panelName,true).First().Height; ClientWidth=form.Controls.Find(panelName,true).First().Width;
      settings.Backend=backend; lastMousePos=new Point();
    }

    public void SetSettings(WorldRenderSettings settingsValue) { lock(settingsLock) settings=settingsValue?.Clone()??new WorldRenderSettings(); temporalHistoryResetRequested=true; }
    private WorldRenderSettings SettingsSnapshot(){lock(settingsLock)return settings.Clone();}

    public override bool Init() {
      if(!base.Init())return false;
      RenderStates.InitAll(Device);
      fx=new WorldEffect(Device,"Shaders\\World.fx");
      var signature=fx.Lit.GetPassByIndex(0).Description.Signature;
      inputLayout=new InputLayout(Device,signature,InputLayoutDescriptions.PosNormalTexTan);
      var skinnedSignature=fx.SkinnedLit.GetPassByIndex(0).Description.Signature;
      skinnedLayout=new InputLayout(Device,skinnedSignature,InputLayoutDescriptions.PosNormalTexTanSkinned);
      var instancedSignature=fx.InstancedLit.GetPassByIndex(0).Description.Signature;
      instancedLayout=new InputLayout(Device,instancedSignature,InputLayoutDescriptions.InstancedPosNormalTexTan);
      var dydSignature=fx.DynamicDetail.GetPassByIndex(0).Description.Signature;
      dynamicDetailLayout=new InputLayout(Device,dydSignature,InputLayoutDescriptions.DynamicDetail);
      CreateSceneTarget();
      InitializeFeatureTextRenderer();
      return true;
    }

    public void LoadModel(Dictionary<ulong,GR2> models, Dictionary<string,GR2_Material> materials, List<Room> rooms, string fqn, Area area=null, Dictionary<string,GR2> utilityModels=null, List<WorldNpcPlacement> npcData=null, List<WorldSpnPlacement> spnData=null, WorldModelStreamer streamer=null, IEnumerable<WorldModelStreamRequest> streamRequests=null) {
      this.fqn=fqn; this.area=area; this.models=models??new Dictionary<ulong,GR2>(); this.materials=materials??new Dictionary<string,GR2_Material>(); this.rooms=rooms??new List<Room>();
      modelStreamer=streamer;materialMetadataStreamer?.Dispose();materialMetadataStreamer=modelStreamer==null?null:new WorldMaterialMetadataStreamer(4); BuildModelStreamingCatalog(streamRequests); initialStreamLoading=modelStreamer!=null&&streamCatalogAssetCount>0;initialStreamVisibilityKeepUntilFrame=0;
      WorldRenderSettings initialSettings=SettingsSnapshot();
      appliedTextureMipSkip=TextureMipSkip(initialSettings.TextureQuality);
      SetJedipediaFeatureData(utilityModels,npcData,spnData);
      elapsed=0f;
      placeableCameraAssetId=0; currentCameraRoom=null; displayCameraRoom=null; skyRoomNames.Clear(); regularModelInstancingSafe.Clear(); mapOpen=false; mapPointerDown=false;
      orthographicActive=false;appliedOrthographicProjection=false;appliedOrthographicHalfHeight=-1f;orthographicZoom=orthographicZoomTarget=1f;orthographicFloorY=float.NaN;orthographicPanning=false;havePerspectivePitchBeforeOrthographic=false;
      if(Window is WorldBrowser worldBrowser)worldBrowser.SetFullMapActive(false);
      if(area!=null){
        AreaAsset camAsset=area.AssetIdMap.Values.FirstOrDefault(a=>string.Equals(a.Extension,"cam",StringComparison.OrdinalIgnoreCase)&&NormalizeAssetPath(a.Path)=="engine/placeablecamera");if(camAsset!=null)placeableCameraAssetId=camAsset.Id;
      }
      // Jedipedia's window.skyscenes is authoritative: every room actually resolved from an environment scheme's
      // skyscene_room plus the area's numeric default skydome. Do not infer a backdrop from a room name, a camera,
      // or a Skydome material: ordinary Dantooine rooms contain those too and would disappear from the world/map.
      BuildAuthoritativeSkyRoomSet();
      BuildInstanceWorldTransformCache();
      BuildSpatialStreamingBootstrapIndex();
      BuildPathFollowers();
      UpdatePathFollowers(elapsed);
      PrimeRoomVisibilityBounds();
      BuildRoomStreamingBounds();
      BuildPortalVisibilityGraph();
      BuildRoomStreamingGraph();
      RequestSkyModels();
      BuildDynamicDetailMeshModels();
      LoadStrongholdHookModels();
      LoadAndApplyLodSchemas();
      // Material textures are intentionally resident-on-demand. Parsing every MAT here eagerly uploaded every texture
      // used anywhere in a planet before the first frame, which was the main source of long loads and runaway VRAM.
      BuildModelGeometry(); BuildJedipediaOverlayResources(); BuildDecorationHookRenderEntries(); BuildEmbeddedGeometry(); BuildTerrainResources(); BuildWaterResources(); BuildHeightMapFloorIndex(); BuildRoomPlacementIndex(); BuildVolumeMembershipIndex(); BuildModelFloorIndex(); BuildRenderSpatialIndex(); BuildLocalLightIndex(); BuildRoads(); BuildMapNotes(); CalculateBounds(); InvalidateTemporalHistory();
      camera.Reset();
      activeClipDistance=GetActiveClipDistance(area?.GetEnvironmentScheme("area"),initialSettings);
      appliedCameraFar=GetCameraFarDistance(activeClipDistance,initialSettings);
      camera.SetLens(GetFieldOfViewRadians(initialSettings),AspectRatio,.01f,appliedCameraFar);
      if(area?.ArrivalPoint!=null){Vector3 p=area.ArrivalPoint.Position+new Vector3(0,2,0);camera.LookAt(p,p+new Vector3(0,0,1),new Vector3(0,1,0));}
      else {Vector3 center=(boundsMin+boundsMax)*.5f; float r=Math.Max(10,(boundsMax-boundsMin).Length()*.35f); camera.LookAt(center+new Vector3(0,r*.35f,r),center,new Vector3(0,1,0));}
      if(initialSettings.OrthographicProjection)SetOrthographicMode(true);
      ApplyCameraLens(initialSettings,appliedCameraFar,true);
      ResetMapCamera();
      // Establish a semantic arrival-room demand before the broad spatial fallback. A zero-authored-bounds hangar
      // can still be identified from conservative placement bounds, so its shell/floor gets admission to the bounded
      // decoder queue instead of losing all 96 slots to arbitrary nearby placement origins.
      activeStreamAssetIds.Clear();urgentStreamAssetIds.Clear();materialStreamAssetIds.Clear();activeStreamRoomNames.Clear();previousStreamRoomNames.Clear();floorStreamRoomNames.Clear();previousFloorStreamRoomNames.Clear();
      Room arrivalStreamRoom=ResolveStreamingRoom(camera.Position,FindCameraRoom(camera.Position));
      BuildInitialStreamWorkingSet(camera.Position,arrivalStreamRoom);
      RequestInitialStreamWorkingSet();
      RequestCameraRoomModels(arrivalStreamRoom,true);
      if(initialStreamAssetIds.Count==0)RequestSpatialBootstrapModels(camera.Position,180f,48,true);
      if(initialStreamAssetIds.Count==0)initialStreamLoading=false;
      QueueNewlyDemandedRoomIntegrations();
    }

    public void FitArea(){ResetMapCamera();}
    public void RequestScreenshot(){makeScreenshot=true;}
    public string CurrentRoomName {
      get {
        Room room=displayCameraRoom??currentCameraRoom;
        return room==null||IsEverywhereRoom(room)?"unknown":room.RoomName;
      }
    }
    public Vector3 CurrentCameraPosition => camera?.Position ?? new Vector3();
    public float CurrentCameraSpeed => cameraSpeed;
    public void SetCameraSpeed(float speed) {
      cameraSpeed=(float)Math.Max(MinCameraSpeed,Math.Min(MaxCameraSpeed,speed));
      if(Window is WorldBrowser browser)browser.SetStatusLabel("Camera speed: "+cameraSpeed.ToString("0.##")+" u/s (mouse wheel fine-tunes)");
    }
    public bool IsFullMapOpen => mapOpen;

    // Jedipedia's public position readout is SWTOR display space: world X is display X, world Z is display Y,
    // world Y is display Z, all multiplied by ten. Camera height includes the 1.8 m eye offset, which the readout
    // removes so copying the coordinates describes the floor/player position rather than the camera lens.
    public Vector3 CurrentDisplayPosition {
      get {
        Vector3 p = CurrentCameraPosition;
        return new Vector3(p.X * 10f, p.Z * 10f, p.Y * 10f - WalkingEyeHeight * 10f);
      }
    }

    public void TeleportToDisplayCoordinates(float displayX,float displayY,float? displayZ){
      if(camera==null)return;
      Vector3 pos=camera.Position;
      pos.X=displayX/10f;
      pos.Z=displayY/10f;
      if(displayZ.HasValue)pos.Y=(displayZ.Value+WalkingEyeHeight*10f)/10f;
      camera.Position=pos;
      currentCameraRoom=null;displayCameraRoom=null;walkingVerticalVelocity=0f;walkingGrounded=false;ClearWalkingPlatform();
      CloseTaxiRouteMapState();CloseQuickTravelMapState();
      mapOpen=false;mapPointerDown=false;mapPointerDragged=false;InvalidateTemporalHistory();InvalidateObjectOcclusionVisibility();
      if(Window is WorldBrowser browser)browser.SetFullMapActive(false);
    }

    public AreaMapNote CaptureCameraMapNote(string id, string label) {
      if (camera == null) return null;
      Vector3 look = camera.Look;
      if (!IsFinite(look) || look.LengthSquared() < .000001f) look = Vector3.UnitZ; else look.Normalize();
      float pitch = (float)Math.Asin(Math.Max(-1f, Math.Min(1f, look.Y)));
      float yaw = (float)Math.Atan2(look.X, look.Z);
      float toDeg = 180f / (float)Math.PI;
      return new AreaMapNote {
        Id = id ?? String.Empty,
        Fqn = "Bookmark",
        Label = String.IsNullOrWhiteSpace(label) ? "Bookmark" : label.Trim(),
        // Mapnotes use a ground position; TeleportToMapNote restores the viewer eye height.
        Position = camera.Position - new Vector3(0f, WalkingEyeHeight, 0f),
        Rotation = new Vector3(-pitch * toDeg, -yaw * toDeg, 0f)
      };
    }

    public void TeleportToMapNote(AreaMapNote note) {
      if(note==null||camera==null)return;
      Vector3 pos=note.Position+new Vector3(0,WalkingEyeHeight,0);
      float toRad=(float)Math.PI/180f;
      float pitch=-note.Rotation.X*toRad;
      float yaw=-note.Rotation.Y*toRad;
      // Jedipedia stores mapnote rotations in degrees and flips pitch/yaw when applying them to the viewer camera.
      // FpsCamera has no SetAngles helper, so build the same forward vector and install it through LookAt().
      float cp=(float)Math.Cos(pitch),sp=(float)Math.Sin(pitch),cy=(float)Math.Cos(yaw),sy=(float)Math.Sin(yaw);
      Vector3 look=new Vector3(sy*cp,sp,cy*cp);
      if(!IsFinite(look)||look.LengthSquared()<.000001f)look=Vector3.UnitZ;else look.Normalize();
      camera.LookAt(pos,pos+look,Vector3.UnitY);
      camera.UpdateViewMatrix();
      currentCameraRoom=null;displayCameraRoom=null;walkingVerticalVelocity=0f;walkingGrounded=false;ClearWalkingPlatform();
      CloseTaxiRouteMapState();CloseQuickTravelMapState();
      mapOpen=false;mapPointerDown=false;mapPointerDragged=false;InvalidateTemporalHistory();InvalidateObjectOcclusionVisibility();
      if(Window is WorldBrowser browser)browser.SetFullMapActive(false);
    }

    public void RequestTeleportWarmup(AreaMapNote note) {
      if (note == null) return;
      lock (teleportWarmupLock) {
        teleportWarmupRequestedPosition = note.Position + new Vector3(0f, WalkingEyeHeight, 0f);
        teleportWarmupRequestPending = true;
      }
    }

    public void CancelTeleportWarmup() {
      lock (teleportWarmupLock) {
        teleportWarmupRequestPending = false;
        teleportWarmupCancelPending = true;
      }
    }

    /// <summary>
    /// One-shot diagnostic picker for the World Browser. This deliberately intersects the same visible
    /// GR2 mesh pieces that DrawModel() uses instead of guessing from an instance bounding sphere. It is
    /// intended for authoring/debugging mismatches against Jedipedia (for example a stray Stronghold prop).
    /// </summary>
    public string InspectModelAtScreen(int screenX,int screenY){
      if(area==null||camera==null||ClientWidth<=1||ClientHeight<=1)return "No world is loaded.";
      WorldRenderSettings s=SettingsSnapshot();
      if(s.Mode==WorldRenderMode.Map)return "Model inspection is only available in the 3D render modes.";
      try{
        // D3D NDC: X/Y are [-1,+1], depth is [0,1]. Invert the exact camera ViewProj used by the world pass.
        float nx=2f*screenX/(float)Math.Max(1,ClientWidth)-1f;
        float ny=1f-2f*screenY/(float)Math.Max(1,ClientHeight);
        Matrix inverseViewProj=Matrix.Invert(camera.ViewProj);
        Vector3 near=Vector3.TransformCoordinate(new Vector3(nx,ny,0f),inverseViewProj);
        Vector3 far=Vector3.TransformCoordinate(new Vector3(nx,ny,1f),inverseViewProj);
        Vector3 direction=far-near;
        if(direction.LengthSquared()<.0000001f)return "Could not build a pick ray for this screen position.";
        direction.Normalize();

        HashSet<string> visible=BuildVisibleRoomSet(currentCameraRoom,s);
        RenderEntry bestEntry=null;GR2 bestModel=null;GR2_Mesh bestMesh=null;GR2_Mesh_Piece bestPiece=null;
        float bestDistance=float.MaxValue;Vector3 bestPoint=Vector3.Zero;
        // The spatial index already excludes sky rooms. Inspection is a click-time diagnostic, so disable the
        // camera-frustum sphere prefilter and use exact triangle tests; this also catches models with poor bounds.
        foreach(RenderEntry entry in NearbyRenderEntries(RenderKindModel,camera.FarZ,false,visible)){
          Room room=entry.Room;AssetInstance inst=entry.Instance;GR2 model=entry.Model;
          if(room==null||inst==null||model==null||!model.enabled||skyRoomNames.Contains(room.RoomName)||!InstanceRoomVisible(inst,room,visible)||!InstanceVisibleInWorld(inst,s))continue;
          if(ShouldCullModelByLod(model,entry.World,false,inst.LodFactor))continue;
          if(TryRayHitModel(model,entry.World,inst.LodFactor,near,direction,s,ref bestDistance,ref bestPoint,ref bestModel,ref bestMesh,ref bestPiece))bestEntry=entry;
        }
        if(bestEntry==null||bestModel==null)return "No rendered model was hit. Click directly on an opaque/visible part of the object.";

        AssetInstance hit=bestEntry.Instance;AreaAsset asset=null;area.AssetIdMap.TryGetValue(hit.assetID,out asset);
        Matrix world=bestEntry.World;
        var sb=new System.Text.StringBuilder();
        sb.AppendLine("World model inspector");
        sb.AppendLine("Room: "+(bestEntry.Room?.RoomName??"(unknown)"));
        string inspectedAssetPath=asset==null?(bestModel.filename??"(unknown)"):asset.Path+(string.IsNullOrWhiteSpace(asset.Extension)?string.Empty:"."+asset.Extension.TrimStart('.'));
        sb.AppendLine("Asset path: "+inspectedAssetPath);
        sb.AppendLine("Asset ID: "+hit.assetID);
        sb.AppendLine("Instance ID: "+hit.ID);
        sb.AppendLine("Parent instance: "+hit.parentInstance);
        sb.AppendLine("Path follower animated: "+hit.PathFollowerAnimated);
        sb.AppendLine("Path follower pending: "+hit.PathFollowerPending);
        AppendInspectorParentChain(sb,bestEntry.Room,hit);
        sb.AppendLine("Viewability: "+hit.Viewability);
        sb.AppendLine("Hidden: "+hit.hidden);
        sb.AppendLine("LOD factor: "+hit.LodFactor.ToString("0.###",System.Globalization.CultureInfo.InvariantCulture));
        sb.AppendLine("Mesh: "+(bestMesh?.meshName??"(unknown)")+"  [LOD "+(bestMesh==null?"?":bestMesh.lod.ToString())+"]");
        GR2_Material material=ResolvePieceMaterial(bestModel,bestPiece);
        sb.AppendLine("Material: "+(material?.materialName??"(none)"));
        if(material!=null){
          sb.AppendLine("Material derived: "+(material.derived??"(none)"));
          sb.AppendLine("Material visibility: "+(material.visibility??"(default)"));
          sb.AppendLine("Material poly type: "+(material.polyType??"(default)"));
          sb.AppendLine("Alpha mode: "+(material.alphaMode??"None"));
        }
        sb.AppendLine(string.Format(System.Globalization.CultureInfo.InvariantCulture,"Local position: {0:0.###}, {1:0.###}, {2:0.###}",hit.position.X,hit.position.Y,hit.position.Z));
        sb.AppendLine(string.Format(System.Globalization.CultureInfo.InvariantCulture,"Local rotation: {0:0.###}, {1:0.###}, {2:0.###}",hit.rotation.X,hit.rotation.Y,hit.rotation.Z));
        sb.AppendLine(string.Format(System.Globalization.CultureInfo.InvariantCulture,"Local scale: {0:0.###}, {1:0.###}, {2:0.###}",hit.scale.X,hit.scale.Y,hit.scale.Z));
        sb.AppendLine(string.Format(System.Globalization.CultureInfo.InvariantCulture,"World position: {0:0.###}, {1:0.###}, {2:0.###}",world.M41,world.M42,world.M43));
        sb.AppendLine(string.Format(System.Globalization.CultureInfo.InvariantCulture,"Hit point: {0:0.###}, {1:0.###}, {2:0.###}  (distance {3:0.###})",bestPoint.X,bestPoint.Y,bestPoint.Z,bestDistance));
        return sb.ToString().TrimEnd();
      }catch(Exception ex){
        return "Model inspection failed: "+ex.Message;
      }
    }

    public string SelectedWorldModelSummary => selectedWorldModelSummary ?? String.Empty;
    public string SelectedWorldModelDetails => selectedWorldModelDetails ?? String.Empty;
    internal WorldInteractionInfo SelectedWorldInteraction => selectedWorldSpnPlacement?.Interaction ?? selectedWorldNpcPlacement?.Interaction;
    internal WorldNpcPlacement SelectedWorldNpcPlacement => selectedWorldNpcPlacement;
    internal WorldInteractionInfo InteractionTargetWorldInteraction => interactionWorldSpnPlacement?.Interaction ?? interactionWorldNpcPlacement?.Interaction;
    internal WorldSpnPlacement InteractionTargetWorldSpnPlacement => interactionWorldSpnPlacement;
    internal WorldNpcPlacement InteractionTargetWorldNpcPlacement => interactionWorldNpcPlacement;

    internal void RefreshSelectedWorldInteractionDetails(){
      if(selectedWorldNpcPlacement!=null){
        WorldNpcPlacement npc=selectedWorldNpcPlacement;Matrix world=NpcNameplateWorld(npc);Vector3 center=new Vector3(world.M41,world.M42,world.M43);float radius=.08f;
        TryWorldModelsSphere(npc.Models,world,out center,out radius);
        selectedWorldModelDetails=WorldPickDetails(new WorldPickCandidate{Kind="npc",Npc=npc,Center=center,Radius=Math.Max(.08f,radius)});return;
      }
      if(selectedWorldSpnPlacement!=null){
        WorldSpnPlacement spn=selectedWorldSpnPlacement;WorldRenderSettings settings=SettingsSnapshot();Matrix world=SpnPlacementWorld(spn,settings.AnimateSpnObjects);
        WorldSpnDynState dyn=ActiveSpnDynState(spn,settings.AnimateSpnObjects);Vector3 center=new Vector3(world.M41,world.M42,world.M43);float radius=.08f;
        TrySpnReceiverSphere(spn,world,dyn,out center,out radius);
        selectedWorldModelDetails=WorldPickDetails(new WorldPickCandidate{Kind="spn",Spn=spn,Center=center,Radius=Math.Max(.08f,radius)});
      }
    }

    public void ClearWorldModelSelection(){
      selectedWorldRenderEntry=null;selectedWorldNpcPlacement=null;selectedWorldSpnPlacement=null;selectedWorldUtilityEntry=null;selectedWorldPath=null;
      selectedWorldPathHit=Vector3.Zero;selectedWorldModelSummary=String.Empty;selectedWorldModelDetails=String.Empty;selectedWorldPickKey=String.Empty;
      selectedWorldCycleIndex=-1;selectedWorldCycleCount=0;
      lastWorldPickX=lastWorldPickY=Int32.MinValue;lastWorldPickIndex=-1;lastWorldPickSignature=String.Empty;
      ClearWorldInteractionTarget();
    }

    public void ClearWorldInteractionTarget(){
      interactionWorldNpcPlacement=null;interactionWorldSpnPlacement=null;
    }

    // Backward-compatible one-shot entry point for callers that want a full world pick without cycling.
    // The World Browser UI itself now marks objects exclusively through Ctrl+left-click.
    public string SelectWorldModelAtScreen(int screenX,int screenY){return SelectWorldObjectAtScreen(screenX,screenY,false,false);}

    /// <summary>
    /// Lightweight interaction pick used by a plain left/right click. It deliberately ignores ordinary geometry,
    /// editor helpers and non-interactive spawned props. The old implementation first picked every SPN sphere and only
    /// then asked whether the winner was actionable; a door, frame or decorative dyn object overlapping a tiny button
    /// could therefore swallow the click. Every classified service may participate here, while Ctrl+left-click remains
    /// exclusively the read-only object inspector.
    /// </summary>
    public string SelectWorldSpawnAtScreen(int screenX,int screenY){
      ClearWorldInteractionTarget();
      if(area==null||camera==null||ClientWidth<=1||ClientHeight<=1)return "No world is loaded.";
      WorldRenderSettings s=SettingsSnapshot();
      if(s.Mode==WorldRenderMode.Map)return "World interaction is only available in the 3D render modes.";
      try{
        if(!TryBuildWorldPickRay(screenX,screenY,out Vector3 rayOrigin,out Vector3 rayDirection))return "Could not build a pick ray for this screen position.";
        HashSet<string> visible=BuildVisibleRoomSet(currentCameraRoom,s);
        List<WorldPickCandidate> hits=BuildWorldPickCandidates(rayOrigin,rayDirection,s,visible,true,true)
          .Where(h=>h?.Npc?.Interaction!=null||h?.Spn?.Interaction!=null||h?.Spn?.BlueGlow==true||WorldSpnLooksLikeLoreCandidate(h?.Spn)||
            h?.Npc?.IsTaxiTerminal==true||(h?.Spn?.WonkaPackageId??0)!=0||h?.Spn?.IsQuickTravel==true)
          .OrderBy(h=>h.Distance).ToList();
        if(hits.Count==0)return "No interactive NPC or placeable was hit.";
        WorldPickCandidate hit=hits[0];
        interactionWorldNpcPlacement=hit.Npc;interactionWorldSpnPlacement=hit.Spn;
        return WorldPickDetails(hit);
      }catch(Exception ex){ClearWorldInteractionTarget();return "World interaction failed: "+ex.Message;}
    }

    /// <summary>
    /// World selection used by the explicit Ctrl+left-click gesture. The caller may restrict the hit list to
    /// population/editor markers and paths, or include ordinary world geometry. Repeated picks can cycle through
    /// overlapping candidates. All expensive ray work happens only here, never in the render loop.
    /// </summary>
    public string SelectWorldObjectAtScreen(int screenX,int screenY,bool markersOnly,bool cycle){
      ClearWorldInteractionTarget();
      if(area==null||camera==null||ClientWidth<=1||ClientHeight<=1){ClearWorldModelSelection();return "No world is loaded.";}
      WorldRenderSettings s=SettingsSnapshot();
      if(s.Mode==WorldRenderMode.Map){ClearWorldModelSelection();return "World selection is only available in the 3D render modes.";}
      try{
        if(!TryBuildWorldPickRay(screenX,screenY,out Vector3 rayOrigin,out Vector3 rayDirection)){ClearWorldModelSelection();return "Could not build a pick ray for this screen position.";}
        HashSet<string> visible=BuildVisibleRoomSet(currentCameraRoom,s);
        List<WorldPickCandidate> hits=BuildWorldPickCandidates(rayOrigin,rayDirection,s,visible,markersOnly);
        if(hits.Count==0){ClearWorldModelSelection();return markersOnly?"No visible spawner, utility or path was hit.":"No rendered world object was hit.";}

        hits.Sort((a,b)=>a.Distance.CompareTo(b.Distance)!=0?a.Distance.CompareTo(b.Distance):String.Compare(a.Key,b.Key,StringComparison.Ordinal));
        if(hits.Count>96)hits.RemoveRange(96,hits.Count-96);
        string signature=String.Join("|",hits.Select(h=>h.Key));
        int index=0;
        bool samePickRay=cycle&&Math.Abs(screenX-lastWorldPickX)<=3&&Math.Abs(screenY-lastWorldPickY)<=3&&String.Equals(signature,lastWorldPickSignature,StringComparison.Ordinal);
        if(samePickRay) index=(lastWorldPickIndex+1)%hits.Count;
        else if(cycle&&hits.Count>1&&String.Equals(hits[0].Key,selectedWorldPickKey,StringComparison.Ordinal)) index=1;
        lastWorldPickX=screenX;lastWorldPickY=screenY;lastWorldPickIndex=index;lastWorldPickSignature=signature;
        ApplyWorldPickCandidate(hits[index]);
        selectedWorldCycleIndex=index;selectedWorldCycleCount=hits.Count;
        if(cycle&&hits.Count>1)selectedWorldModelDetails+="\r\n\r\nSelection cycle: "+(index+1)+" of "+hits.Count+" (Ctrl+click again to advance).";
        return selectedWorldModelDetails;
      }catch(Exception ex){ClearWorldModelSelection();return "World selection failed: "+ex.Message;}
    }

    // Codex/tutorial placeables are structurally language-independent.  Do not make their click target depend on
    // a localized display name or on the interaction resolver having succeeded for the current locale: several
    // de-de placeables only expose the codex link lazily through their plc/tutorial FQN.
    private static bool WorldSpnLooksLikeLoreCandidate(WorldSpnPlacement spn){
      if(spn==null)return false;
      string probe=((spn.SourceFqn??String.Empty)+" "+(spn.Name??String.Empty)).ToLowerInvariant();
      return probe.Contains("lore")||probe.Contains("codex")||probe.Contains("knowledge")||probe.Contains("tutorial");
    }

    private bool TryBuildWorldPickRay(int screenX,int screenY,out Vector3 origin,out Vector3 direction){
      origin=Vector3.Zero;direction=Vector3.Zero;
      float nx=2f*screenX/(float)Math.Max(1,ClientWidth)-1f;
      float ny=1f-2f*screenY/(float)Math.Max(1,ClientHeight);
      Matrix inverseViewProj=Matrix.Invert(camera.ViewProj);
      Vector3 near=Vector3.TransformCoordinate(new Vector3(nx,ny,0f),inverseViewProj);
      Vector3 far=Vector3.TransformCoordinate(new Vector3(nx,ny,1f),inverseViewProj);
      direction=far-near;if(direction.LengthSquared()<.0000001f)return false;direction.Normalize();
      // Orthographic rays start at their near-plane pixel; perspective rays all start at the eye. The previous
      // inspector used the near point in both modes, which was harmless for meshes but breaks cutaway picking.
      origin=orthographicActive?near:camera.Position;return true;
    }

    private List<WorldPickCandidate> BuildWorldPickCandidates(Vector3 rayOrigin,Vector3 rayDirection,WorldRenderSettings s,HashSet<string> visible,bool markersOnly,bool interactionPick=false){
      var hits=new List<WorldPickCandidate>();
      if(!markersOnly){
        foreach(RenderEntry entry in NearbyRenderEntries(RenderKindModel,camera.FarZ,false,visible)){
          Room room=entry.Room;AssetInstance inst=entry.Instance;GR2 model=entry.Model;
          if(room==null||inst==null||model==null||!model.enabled||skyRoomNames.Contains(room.RoomName)||!InstanceRoomVisible(inst,room,visible)||!InstanceVisibleInWorld(inst,s))continue;
          if(ShouldCullModelByLod(model,entry.World,false,inst.LodFactor))continue;
          float hitDistance=float.MaxValue;Vector3 hitPoint=Vector3.Zero;GR2 hitModel=null;GR2_Mesh hitMesh=null;GR2_Mesh_Piece hitPiece=null;
          if(!TryRayHitModel(model,entry.World,inst.LodFactor,rayOrigin,rayDirection,s,ref hitDistance,ref hitPoint,ref hitModel,ref hitMesh,ref hitPiece))continue;
          hits.Add(new WorldPickCandidate{Kind="model",Key="model:"+(room.RoomName??String.Empty)+":"+inst.ID,Distance=hitDistance,HitPoint=hitPoint,Center=entry.Center,Radius=Math.Max(.05f,entry.Radius),RenderEntry=entry,Model=hitModel,Mesh=hitMesh,Piece=hitPiece});
        }
      }

      if((s.ShowNpcs||s.ShowTaxiTerminals)&&npcPlacements!=null){
          foreach(WorldNpcPlacement npc in npcPlacements){
            if(!NpcLayerVisible(npc,s))continue;
            if(npc?.Instance==null||npc.Room==null||npc.Models==null||!SpnVariantActive(npc.VariantIndex,npc.VariantCount,npc.SpawnPoints))continue;
            bool moving=npc.SpawnPoints!=null&&npc.SpawnPoints.Count>0;
            if(!moving&&!InstanceRoomVisible(npc.Instance,npc.Room,visible))continue;
            if(!InstanceVisibleInWorld(npc.Instance,s))continue;
            Matrix world=NpcNameplateWorld(npc);
            if(!TryWorldModelsSphere(npc.Models,world,out Vector3 center,out float radius))continue;
            float npcPickRadius=interactionPick&&npc.Interaction!=null?WorldInteractionPickRadius(center,radius,s):Math.Max(.08f,radius);
            if(!TryRaySphere(rayOrigin,rayDirection,center,npcPickRadius,out float distance))continue;
            hits.Add(new WorldPickCandidate{Kind="npc",Key="npc:"+(npc.Room.RoomName??String.Empty)+":"+npc.Instance.ID+":"+(npc.SourceFqn??String.Empty),Distance=distance,HitPoint=rayOrigin+rayDirection*distance,Center=center,Radius=Math.Max(.08f,radius),Npc=npc});
          }
        }
        if(s.ShowSpnObjects&&spnPlacements!=null){
          foreach(WorldSpnPlacement spn in spnPlacements){
            if(spn?.Instance==null||spn.Room==null||!SpnVariantActive(spn.VariantIndex,spn.VariantCount,spn.SpawnPoints))continue;
            bool moving=spn.Route!=null||(spn.SpawnPoints!=null&&spn.SpawnPoints.Count>0);
            if(!moving&&!InstanceRoomVisible(spn.Instance,spn.Room,visible))continue;
            if(!InstanceVisibleInWorld(spn.Instance,s))continue;
            Matrix world=SpnPlacementWorld(spn,s.AnimateSpnObjects);WorldSpnDynState dyn=ActiveSpnDynState(spn,s.AnimateSpnObjects);if(dyn!=null&&dyn.Hidden)continue;
            bool interactiveWonk=interactionPick&&(spn.Interaction?.Kind==WorldInteractionKind.Wonkavator||spn.WonkaPackageId!=0);
            bool interactiveTaxi=interactionPick&&spn.Interaction?.Kind==WorldInteractionKind.Taxi;
            bool interactiveQuickTravel=interactionPick&&(spn.Interaction?.Kind==WorldInteractionKind.QuickTravel||spn.IsQuickTravel);
            bool interactiveLoreCandidate=interactionPick&&spn.Interaction==null&&(spn.BlueGlow||WorldSpnLooksLikeLoreCandidate(spn));
            bool interactiveService=interactionPick&&(spn.Interaction!=null||interactiveWonk||interactiveTaxi||interactiveQuickTravel||interactiveLoreCandidate);
            Vector3 center;float radius;
            bool haveSphere=(interactiveWonk||interactiveTaxi)?TrySpnInteractionSphere(spn,world,dyn,out center,out radius):TrySpnReceiverSphere(spn,world,dyn,out center,out radius);
            // Some historical bindpoint placeables are effect-only and have no GR2 receiver at all. The client still
            // gives their placement a clickable quick-travel interaction, so provide a small authored-position sphere.
            if(!haveSphere&&interactiveQuickTravel){center=new Vector3(world.M41,world.M42,world.M43);radius=.20f;haveSphere=true;}
            if(!haveSphere)continue;
            float pickRadius=interactiveService?WorldInteractionPickRadius(center,radius,s):Math.Max(.08f,radius);
            if(!TryRaySphere(rayOrigin,rayDirection,center,pickRadius,out float distance))continue;
            hits.Add(new WorldPickCandidate{Kind="spn",Key="spn:"+(spn.Room.RoomName??String.Empty)+":"+spn.Instance.ID+":"+(spn.SourceFqn??String.Empty),Distance=distance,HitPoint=rayOrigin+rayDirection*distance,Center=center,Radius=Math.Max(.08f,radius),Spn=spn});
          }
        }

      foreach(UtilityRenderEntry utility in utilityRenderEntries){
          if(utility?.Instance==null||utility.Room==null||!UtilityCategoryEnabled(utility.Category,s))continue;
          if(!InstanceRoomVisible(utility.Instance,utility.Room,visible)||!InstanceCanEnterRenderIndex(utility.Instance))continue;
          Matrix world=InstanceWorld(utility.Instance,utility.Room);Vector3 center;float radius;
          if(utility.Model!=null){if(!TryModelSphere(utility.Model,world,out center,out radius))continue;}
          else {center=new Vector3(world.M41,world.M42,world.M43);radius=.08f;}
          radius=Math.Max(.06f,radius);if(!TryRaySphere(rayOrigin,rayDirection,center,radius,out float distance))continue;
          hits.Add(new WorldPickCandidate{Kind="utility",Key="utility:"+(utility.Room.RoomName??String.Empty)+":"+utility.Instance.ID,Distance=distance,HitPoint=rayOrigin+rayDirection*distance,Center=center,Radius=radius,Utility=utility});
        }
      if(s.ShowUtilityPaths&&area?.Paths!=null){
        foreach(AreaPath path in area.Paths){
          if(path?.Points==null||path.Points.Count<2||path.IsMapRoad&&!s.ShowUtilityMapRoadPaths)continue;
          if(TryRayHitAreaPath(rayOrigin,rayDirection,path,s,out float distance,out Vector3 hitPoint))
            hits.Add(new WorldPickCandidate{Kind="path",Key="path:"+path.Id+":"+(path.Fqn??path.Name??String.Empty),Distance=distance,HitPoint=hitPoint,Center=hitPoint,Radius=.12f,Path=path});
        }
      }
      return hits;
    }

    private bool TryRayHitAreaPath(Vector3 rayOrigin,Vector3 rayDirection,AreaPath path,WorldRenderSettings s,out float bestDistance,out Vector3 bestPoint){
      bestDistance=float.MaxValue;bestPoint=Vector3.Zero;if(path?.Points==null||path.Points.Count<2)return false;bool found=false;
      int segments=path.Points.Count-1+(path.Circular?1:0);
      for(int i=0;i<segments;i++){
        Vector3 a=path.Points[i%path.Points.Count].Position,b=path.Points[(i+1)%path.Points.Count].Position;
        if(!TryClosestRaySegment(rayOrigin,rayDirection,a,b,out float rayT,out Vector3 onSegment,out float separation))continue;
        float tolerance=WorldPickLineTolerance(rayT,s);if(separation>tolerance||rayT>=bestDistance)continue;
        bestDistance=rayT;bestPoint=onSegment;found=true;
      }
      return found;
    }

    private float WorldPickLineTolerance(float distance,WorldRenderSettings s){
      const float pixels=7f;float height=Math.Max(1f,ClientHeight);
      if(orthographicActive)return Math.Max(.01f,2f*GetOrthographicHalfHeight(s)/height*pixels);
      float worldPerPixel=2f*Math.Max(.01f,distance)*(float)Math.Tan(GetFieldOfViewRadians(s)*.5f)/height;
      return Math.Max(.01f,worldPerPixel*pixels);
    }

    private static bool TryClosestRaySegment(Vector3 origin,Vector3 direction,Vector3 a,Vector3 b,out float rayT,out Vector3 segmentPoint,out float separation){
      rayT=0f;segmentPoint=a;separation=float.MaxValue;Vector3 seg=b-a;float c=Vector3.Dot(seg,seg);if(c<.0000001f)return false;
      Vector3 w=origin-a;float bd=Vector3.Dot(direction,seg),d=Vector3.Dot(direction,w),e=Vector3.Dot(seg,w);float denom=c-bd*bd;
      float u=Math.Abs(denom)>.0000001f?(e-bd*d)/denom:e/c;u=Math.Max(0f,Math.Min(1f,u));segmentPoint=a+seg*u;
      rayT=Math.Max(0f,Vector3.Dot(segmentPoint-origin,direction));Vector3 rayPoint=origin+direction*rayT;separation=(rayPoint-segmentPoint).Length();return true;
    }

    private static bool TryRaySphere(Vector3 origin,Vector3 direction,Vector3 center,float radius,out float distance){
      distance=0f;Vector3 toCenter=center-origin;float along=Vector3.Dot(toCenter,direction);float r=Math.Max(.0001f,radius);float closestSq=toCenter.LengthSquared()-along*along;float rSq=r*r;if(closestSq>rSq)return false;
      float half=(float)Math.Sqrt(Math.Max(0f,rSq-closestSq));float entry=along-half,exit=along+half;if(exit<0f)return false;distance=Math.Max(0f,entry);return true;
    }

    private void ApplyWorldPickCandidate(WorldPickCandidate pick){
      selectedWorldRenderEntry=null;selectedWorldNpcPlacement=null;selectedWorldSpnPlacement=null;selectedWorldUtilityEntry=null;selectedWorldPath=null;selectedWorldPathHit=Vector3.Zero;
      if(pick==null){selectedWorldModelSummary=selectedWorldModelDetails=selectedWorldPickKey=String.Empty;selectedWorldCycleIndex=-1;selectedWorldCycleCount=0;return;}
      if(pick.RenderEntry!=null)selectedWorldRenderEntry=pick.RenderEntry;
      else if(pick.Npc!=null)selectedWorldNpcPlacement=pick.Npc;
      else if(pick.Spn!=null)selectedWorldSpnPlacement=pick.Spn;
      else if(pick.Utility!=null)selectedWorldUtilityEntry=pick.Utility;
      else if(pick.Path!=null){selectedWorldPath=pick.Path;selectedWorldPathHit=pick.HitPoint;}
      selectedWorldPickKey=pick.Key??String.Empty;selectedWorldModelSummary=WorldPickSummary(pick);selectedWorldModelDetails=WorldPickDetails(pick);
    }

    private string WorldPickSummary(WorldPickCandidate pick){
      if(pick.Npc!=null)return "NPC: "+(pick.Npc.Name??pick.Npc.SourceFqn??"(unknown)")+"  ["+(pick.Npc.Room?.RoomName??"unknown")+"]";
      if(pick.Spn!=null)return "SPN: "+(pick.Spn.Name??pick.Spn.SourceFqn??"(unknown)")+"  ["+(pick.Spn.Room?.RoomName??"unknown")+"]";
      if(pick.Utility!=null){AreaAsset asset=null;if(area!=null)area.AssetIdMap.TryGetValue(pick.Utility.Instance.assetID,out asset);return "Utility: "+WorldAssetDisplayPath(asset,pick.Utility.Model)+"  ["+(pick.Utility.Room?.RoomName??"unknown")+"]";}
      if(pick.Path!=null)return "Path: "+(pick.Path.Name??pick.Path.Fqn??pick.Path.Id.ToString());
      if(pick.RenderEntry!=null){AreaAsset asset=null;if(area!=null)area.AssetIdMap.TryGetValue(pick.RenderEntry.Instance.assetID,out asset);return WorldAssetDisplayPath(asset,pick.Model??pick.RenderEntry.Model)+"  ["+(pick.RenderEntry.Room?.RoomName??"unknown")+"]";}
      return pick.Kind??"Selection";
    }

    private string WorldPickDetails(WorldPickCandidate pick){
      var sb=new System.Text.StringBuilder();sb.AppendLine("World viewer selection");sb.AppendLine("Type: "+(pick.Kind??"unknown"));
      if(pick.Path!=null){
        sb.AppendLine("Path: "+(pick.Path.Name??"(unnamed)"));sb.AppendLine("Node: "+(pick.Path.Fqn??"(none)"));sb.AppendLine("Path id: "+pick.Path.Id);
        sb.AppendLine("Points: "+(pick.Path.Points?.Count??0)+"  Circular: "+pick.Path.Circular+"  Smooth: "+pick.Path.Smooth);
        sb.AppendLine(WorldPickPositionLine("Hit point",pick.HitPoint));return sb.ToString().TrimEnd();
      }
      if(pick.Npc!=null){
        WorldNpcPlacement n=pick.Npc;sb.AppendLine("NPC: "+(n.Name??"(unknown)"));if(!String.IsNullOrWhiteSpace(n.Title))sb.AppendLine("Title: "+n.Title);
        sb.AppendLine("Node: "+(n.SourceFqn??"(none)"));sb.AppendLine("Room: "+(n.Room?.RoomName??"unknown"));sb.AppendLine("Instance id: "+(n.Instance?.ID??0));
        if(n.Items!=null&&n.Items.Length>0)sb.AppendLine("Items: "+String.Join(", ",n.Items));if(!String.IsNullOrWhiteSpace(n.BodyType))sb.AppendLine("Body type: "+n.BodyType);
        if(!String.IsNullOrWhiteSpace(n.IdleAnimationName))sb.AppendLine("Idle animation: "+n.IdleAnimationName);if(!String.IsNullOrWhiteSpace(n.PathFqn))sb.AppendLine("Path: "+n.PathFqn);
        if(!String.IsNullOrWhiteSpace(n.RepublicReaction)||!String.IsNullOrWhiteSpace(n.ImperialReaction))sb.AppendLine("Faction reaction: Republic="+(n.RepublicReaction??"?")+", Empire="+(n.ImperialReaction??"?"));
        AppendWorldInteractionDetails(sb,n.Interaction);
        sb.AppendLine(WorldPickPositionLine("World position",pick.Center));return sb.ToString().TrimEnd();
      }
      if(pick.Spn!=null){
        WorldSpnPlacement spn=pick.Spn;sb.AppendLine("Placeable: "+(spn.Name??spn.SourceFqn??"(unknown)"));sb.AppendLine("Node: "+(spn.SourceFqn??"(none)"));
        sb.AppendLine("Room: "+(spn.Room?.RoomName??"unknown"));sb.AppendLine("Instance id: "+(spn.Instance?.ID??0));if(!String.IsNullOrWhiteSpace(spn.PathFqn))sb.AppendLine("Path: "+spn.PathFqn);
        WorldSpnDynState dyn=ActiveSpnDynState(spn,SettingsSnapshot().AnimateSpnObjects);if(dyn!=null)sb.AppendLine("State: "+(dyn.Name??"(unnamed)")+(dyn.Hidden?" (hidden)":""));
        AppendWorldInteractionDetails(sb,spn.Interaction);
        if(spn.Interaction==null&&spn.WonkaPackageId!=0)sb.AppendLine("Wonkavator package: "+spn.WonkaPackageId);
        sb.AppendLine("Interactable glow: "+spn.BlueGlow);sb.AppendLine(WorldPickPositionLine("World position",pick.Center));return sb.ToString().TrimEnd();
      }
      Room room=pick.Utility?.Room??pick.RenderEntry?.Room;AssetInstance inst=pick.Utility?.Instance??pick.RenderEntry?.Instance;AreaAsset asset=null;if(inst!=null&&area!=null)area.AssetIdMap.TryGetValue(inst.assetID,out asset);
      sb.AppendLine("Asset: "+WorldAssetDisplayPath(asset,pick.Model??pick.Utility?.Model??pick.RenderEntry?.Model));sb.AppendLine("Room: "+(room?.RoomName??"unknown"));sb.AppendLine("Instance id: "+(inst?.ID??0));
      if(inst!=null)sb.AppendLine("Asset id: "+inst.assetID);
      if(pick.Utility!=null)sb.AppendLine("Utility category: "+UtilityCategoryName(pick.Utility.Category));
      if(inst!=null){
        sb.AppendLine(WorldPickPositionLine("Local position",inst.position));sb.AppendLine(WorldPickPositionLine("Local rotation",inst.rotation));sb.AppendLine(WorldPickPositionLine("Local scale",inst.scale));
        sb.AppendLine(WorldPickPositionLine("World position",pick.Center));sb.AppendLine("Viewability: "+inst.Viewability);sb.AppendLine("Hidden: "+inst.hidden);sb.AppendLine("LOD factor: "+inst.LodFactor.ToString("0.###",System.Globalization.CultureInfo.InvariantCulture));
        if(inst.parentInstance!=0)AppendInspectorParentChain(sb,room,inst);
      }
      if(pick.Mesh!=null)sb.AppendLine("Mesh: "+(pick.Mesh.meshName??"(unknown)")+"  [LOD "+pick.Mesh.lod+"]");
      if(pick.Piece!=null){GR2_Material material=ResolvePieceMaterial(pick.Model,pick.Piece);sb.AppendLine("Material: "+(material?.materialName??"(none)"));if(material!=null){sb.AppendLine("Material derived: "+(material.derived??"(none)"));sb.AppendLine("Material visibility: "+(material.visibility??"(default)"));sb.AppendLine("Material poly type: "+(material.polyType??"(default)"));sb.AppendLine("Alpha mode: "+(material.alphaMode??"None"));}}
      sb.AppendLine(WorldPickPositionLine("Hit point",pick.HitPoint));sb.AppendLine("Hit distance: "+pick.Distance.ToString("0.###",System.Globalization.CultureInfo.InvariantCulture));return sb.ToString().TrimEnd();
    }

    private static void AppendWorldInteractionDetails(System.Text.StringBuilder sb,WorldInteractionInfo interaction){
      if(sb==null||interaction==null||interaction.Kind==WorldInteractionKind.None)return;
      sb.AppendLine("Interaction: "+WorldInteractionKindName(interaction.Kind)+(interaction.LegacyHeuristic?" (legacy fallback)":""));
      if(interaction.WonkaPackageId!=0)sb.AppendLine("Wonkavator package: "+interaction.WonkaPackageId);
      if(!String.IsNullOrWhiteSpace(interaction.TaxiTerminalSpec))sb.AppendLine("Taxi terminal: "+interaction.TaxiTerminalSpec);
      if(interaction.UtilityType!=0)sb.AppendLine("Utility type: "+interaction.UtilityType);
      if(!String.IsNullOrWhiteSpace(interaction.Profession))sb.AppendLine("Required profession: "+interaction.Profession);
      if(interaction.VendorPackages!=null&&interaction.VendorPackages.Length>0){
        sb.AppendLine("Vendor packages ("+interaction.VendorPackages.Length+"): "+String.Join(", ",interaction.VendorPackages));
      }
      if(!String.IsNullOrWhiteSpace(interaction.MissionBoardPackage))sb.AppendLine("Mission-board package: "+interaction.MissionBoardPackage);
      if(interaction.ConversationId!=0)sb.AppendLine("Conversation id: "+interaction.ConversationId);
      if(!String.IsNullOrWhiteSpace(interaction.Conversation))sb.AppendLine("Conversation: "+interaction.Conversation);
      if(interaction.CodexId!=0)sb.AppendLine("Codex id: "+interaction.CodexId);
      if(interaction.Kind==WorldInteractionKind.MissionBoard){
        if(!String.IsNullOrWhiteSpace(interaction.MissionBoardError))sb.AppendLine("Mission-board read error: "+interaction.MissionBoardError);
        sb.AppendLine("Mission-board notices: "+interaction.MissionBoardNotices.Count);
        for(int i=0;i<interaction.MissionBoardNotices.Count;i++){
          WorldMissionBoardNotice notice=interaction.MissionBoardNotices[i];if(notice==null)continue;
          string text=(notice.Text??String.Empty).Replace("\r"," ").Replace("\n"," ").Trim();
          sb.Append("  "+(i+1)+". node "+notice.NodeId);if(!String.IsNullOrWhiteSpace(text))sb.Append(" — "+text);sb.AppendLine();
          foreach(WorldMissionBoardQuest quest in notice.Quests){
            if(quest==null)continue;string label=!String.IsNullOrWhiteSpace(quest.Name)?quest.Name:!String.IsNullOrWhiteSpace(quest.Fqn)?quest.Fqn:quest.Id.ToString();
            sb.Append("     Quest: "+label+" ["+quest.Id+"]");if(!String.IsNullOrWhiteSpace(quest.Fqn)&&!String.Equals(label,quest.Fqn,StringComparison.Ordinal))sb.Append("  "+quest.Fqn);sb.AppendLine();
          }
        }
      }
    }

    private static string WorldInteractionKindName(WorldInteractionKind kind){
      switch(kind){
        case WorldInteractionKind.Wonkavator:return "Wonkavator";case WorldInteractionKind.Taxi:return "Taxi terminal";
        case WorldInteractionKind.QuickTravel:return "Quick travel";case WorldInteractionKind.Bank:return "Bank / cargo hold";
        case WorldInteractionKind.GuildBank:return "Guild bank";case WorldInteractionKind.Mailbox:return "Mailbox";
        case WorldInteractionKind.Vendor:return "Vendor";case WorldInteractionKind.ProfessionTrainer:return "Crafting trainer";
        case WorldInteractionKind.ClassTrainer:return "Class trainer";case WorldInteractionKind.Harvest:return "Harvesting node";
        case WorldInteractionKind.MissionBoard:return "Mission board";case WorldInteractionKind.Conversation:return "Conversation";
        case WorldInteractionKind.Codex:return "Codex / knowledge object";
        case WorldInteractionKind.AuctionHouse:return "Auction house";case WorldInteractionKind.EnhancementStation:return "Enhancement station";
        default:return kind.ToString();
      }
    }

    private static string UtilityCategoryName(byte category){if(category==UtilitySpawner)return "Spawner / encounter";if(category==UtilityCover)return "Cover point";if(category==UtilityLight)return "Light";if(category==UtilitySeed)return "Kynapse seed point";return "Other helper";}
    private static string WorldPickPositionLine(string label,Vector3 p){return String.Format(System.Globalization.CultureInfo.InvariantCulture,"{0}: {1:0.###}, {2:0.###}, {3:0.###}",label,p.X,p.Y,p.Z);}
    private static string WorldAssetDisplayPath(AreaAsset asset,GR2 model){if(asset==null)return model?.filename??"(unknown)";return asset.Path+(String.IsNullOrWhiteSpace(asset.Extension)?String.Empty:"."+asset.Extension.TrimStart('.'));}

    private void EnsureSelectedWorldBoxBuffer(){
      if(selectedWorldBoxBuffer!=null||Device==null)return;
      Vector3[] p={
        new Vector3(-1,-1,-1),new Vector3(1,-1,-1), new Vector3(1,-1,-1),new Vector3(1,-1,1),
        new Vector3(1,-1,1),new Vector3(-1,-1,1), new Vector3(-1,-1,1),new Vector3(-1,-1,-1),
        new Vector3(-1,1,-1),new Vector3(1,1,-1), new Vector3(1,1,-1),new Vector3(1,1,1),
        new Vector3(1,1,1),new Vector3(-1,1,1), new Vector3(-1,1,1),new Vector3(-1,1,-1),
        new Vector3(-1,-1,-1),new Vector3(-1,1,-1), new Vector3(1,-1,-1),new Vector3(1,1,-1),
        new Vector3(1,-1,1),new Vector3(1,1,1), new Vector3(-1,-1,1),new Vector3(-1,1,1)
      };
      PosNormalTexTan[] verts=p.Select(v=>new PosNormalTexTan(v,new Vector3(0,1,0),Vector2.Zero,new Vector3(1,0,0))).ToArray();
      var bd=new BufferDescription(PosNormalTexTan.Stride*verts.Length,ResourceUsage.Immutable,BindFlags.VertexBuffer,CpuAccessFlags.None,ResourceOptionFlags.None,0);
      using var ds=new DataStream(verts,false,false);selectedWorldBoxBuffer=new Buffer(Device,ds,bd){DebugName="World selection bounds"};
    }

    private bool TrySelectedWorldBounds(WorldRenderSettings s,out Vector3 center,out float radius){
      center=Vector3.Zero;radius=0f;
      if(selectedWorldRenderEntry!=null){RenderEntry entry=selectedWorldRenderEntry;if(entry.Instance==null||entry.Model==null||!entry.Model.enabled||!InstanceVisibleInWorld(entry.Instance,s))return false;center=entry.Center;radius=Math.Max(.05f,entry.Radius);return true;}
      if(selectedWorldNpcPlacement!=null){WorldNpcPlacement npc=selectedWorldNpcPlacement;if(!NpcLayerVisible(npc,s)||!SpnVariantActive(npc.VariantIndex,npc.VariantCount,npc.SpawnPoints))return false;Matrix world=NpcNameplateWorld(npc);return TryWorldModelsSphere(npc.Models,world,out center,out radius);}
      if(selectedWorldSpnPlacement!=null){WorldSpnPlacement spn=selectedWorldSpnPlacement;if(!s.ShowSpnObjects||!SpnVariantActive(spn.VariantIndex,spn.VariantCount,spn.SpawnPoints))return false;Matrix world=SpnPlacementWorld(spn,s.AnimateSpnObjects);WorldSpnDynState dyn=ActiveSpnDynState(spn,s.AnimateSpnObjects);if(dyn!=null&&dyn.Hidden)return false;return TrySpnReceiverSphere(spn,world,dyn,out center,out radius);}
      if(selectedWorldUtilityEntry!=null){UtilityRenderEntry utility=selectedWorldUtilityEntry;if(!UtilityCategoryEnabled(utility.Category,s))return false;Matrix world=InstanceWorld(utility.Instance,utility.Room);if(utility.Model!=null)return TryModelSphere(utility.Model,world,out center,out radius);center=new Vector3(world.M41,world.M42,world.M43);radius=.08f;return true;}
      if(selectedWorldPath!=null){bool pathVisible=s.ShowUtilityPaths&&(!selectedWorldPath.IsMapRoad||s.ShowUtilityMapRoadPaths);if(!pathVisible)return false;center=selectedWorldPathHit;radius=.12f;return true;}
      return false;
    }

    private void DrawWorldSelectionOutline(Matrix vp,WorldRenderSettings s){
      if(s==null||!s.ShowSelectionBounds||s.Mode==WorldRenderMode.Map||s.Mode==WorldRenderMode.Heightmap||!TrySelectedWorldBounds(s,out Vector3 center,out float radius))return;
      EnsureSelectedWorldBoxBuffer();if(selectedWorldBoxBuffer==null)return;radius=Math.Max(.05f,radius);
      Matrix boxWorld=Matrix.Scaling(radius,radius,radius)*Matrix.Translation(center);
      fx.SetWorld(boxWorld);fx.SetViewProj(vp);fx.SetMaterial(null);fx.SetOverlay(new Vector4(1f,.82f,.18f,.95f));
      ImmediateContext.InputAssembler.InputLayout=inputLayout;ImmediateContext.InputAssembler.PrimitiveTopology=PrimitiveTopology.LineList;
      ImmediateContext.InputAssembler.SetVertexBuffers(0,new VertexBufferBinding(selectedWorldBoxBuffer,PosNormalTexTan.Stride,0));
      fx.Overlay.GetPassByIndex(0).Apply(ImmediateContext);ImmediateContext.Draw(24,0);ImmediateContext.InputAssembler.PrimitiveTopology=PrimitiveTopology.TriangleList;
    }

    private void AppendInspectorParentChain(System.Text.StringBuilder sb,Room room,AssetInstance instance){
      if(sb==null||room==null||instance==null)return;
      ulong parentId=instance.parentInstance;var seen=new HashSet<ulong>();int depth=0;
      while(parentId!=0&&depth<8&&seen.Add(parentId)&&room.InstancesById.TryGetValue(parentId,out AssetInstance parent)){
        AreaAsset parentAsset=null;area?.AssetIdMap.TryGetValue(parent.assetID,out parentAsset);
        string parentPath=parentAsset==null?"(unknown)":parentAsset.Path+(string.IsNullOrWhiteSpace(parentAsset.Extension)?string.Empty:"."+parentAsset.Extension.TrimStart('.'));
        string extra=IsPathFollowerAsset(parent)?string.Format(System.Globalization.CultureInfo.InvariantCulture,"  [Path={0}, Speed={1:0.######}, TurnRate={2:0.######}]",parent.PathFollowerPath??"",parent.PathFollowerSpeed,parent.PathFollowerTurnRate):string.Empty;
        sb.AppendLine("Parent["+depth+"]: "+parent.ID+"  "+parentPath+extra);
        parentId=parent.parentInstance;depth++;
      }
    }

    private bool TryRayHitModel(GR2 model,Matrix world,float lodFactor,Vector3 rayOrigin,Vector3 rayDirection,WorldRenderSettings s,ref float bestDistance,ref Vector3 bestPoint,ref GR2 bestModel,ref GR2_Mesh bestMesh,ref GR2_Mesh_Piece bestPiece){
      if(model==null)return false;bool hitAny=false;
      Matrix inverse;try{inverse=Matrix.Invert(world);}catch{return false;}
      Vector3 localOrigin=Vector3.TransformCoordinate(rayOrigin,inverse);
      Vector3 localDirection=Vector3.TransformNormal(rayDirection,inverse);
      if(localDirection.LengthSquared()<.0000001f)return false;localDirection.Normalize();
      int selectedLod=SelectModelLodLevel(model,world,false,lodFactor);
      foreach(GR2_Mesh mesh in model.meshes){
        if(mesh==null||!MeshVisibleForLod(model,mesh,selectedLod)||mesh.meshVerts==null||mesh.meshVertIndex==null||mesh.meshPieces==null)continue;
        int vertexCount=mesh.meshVerts.Count,indexCount=mesh.meshVertIndex.Count;if(vertexCount<3||indexCount<3)continue;
        foreach(GR2_Mesh_Piece piece in mesh.meshPieces){
          if(piece==null||IsMaterialHiddenFromWorld(ResolvePieceMaterial(model,piece),s))continue;
          long rawStart=(long)piece.startIndex*3L,rawEnd=rawStart+(long)piece.numPieceFaces*3L;
          int start=(int)Math.Max(0,Math.Min(indexCount,rawStart));int end=(int)Math.Max(start,Math.Min(indexCount,rawEnd));
          for(int i=start;i+2<end;i+=3){
            int ai=mesh.meshVertIndex[i].index,bi=mesh.meshVertIndex[i+1].index,ci=mesh.meshVertIndex[i+2].index;
            if(ai<0||bi<0||ci<0||ai>=vertexCount||bi>=vertexCount||ci>=vertexCount)continue;
            GR2_Mesh_Vertex av=mesh.meshVerts[ai],bv=mesh.meshVerts[bi],cv=mesh.meshVerts[ci];if(av==null||bv==null||cv==null)continue;
            Vector3 a=new Vector3(av.X,av.Y,av.Z),b=new Vector3(bv.X,bv.Y,bv.Z),c=new Vector3(cv.X,cv.Y,cv.Z);
            if(!TryRayTriangle(localOrigin,localDirection,a,b,c,out float localT))continue;
            Vector3 localHit=localOrigin+localDirection*localT;Vector3 worldHit=Vector3.TransformCoordinate(localHit,world);
            float distance=(worldHit-rayOrigin).Length();
            if(distance<0f||distance>=bestDistance)continue;
            // Ignore numerical intersections that ended up behind the camera after a non-uniform transform.
            if(Vector3.Dot(worldHit-rayOrigin,rayDirection)<-.0001f)continue;
            bestDistance=distance;bestPoint=worldHit;bestModel=model;bestMesh=mesh;bestPiece=piece;hitAny=true;
          }
        }
      }
      foreach(GR2 attached in model.attachedModels)if(TryRayHitModel(attached,world,lodFactor,rayOrigin,rayDirection,s,ref bestDistance,ref bestPoint,ref bestModel,ref bestMesh,ref bestPiece))hitAny=true;
      return hitAny;
    }

    private static bool TryRayTriangle(Vector3 origin,Vector3 direction,Vector3 a,Vector3 b,Vector3 c,out float distance){
      distance=0f;const float epsilon=.0000001f;Vector3 edge1=b-a,edge2=c-a;Vector3 p=Vector3.Cross(direction,edge2);float det=Vector3.Dot(edge1,p);
      // Do not back-face cull: two-sided and reversed-winding SWTOR materials are common in world geometry.
      if(Math.Abs(det)<epsilon)return false;float invDet=1f/det;Vector3 t=origin-a;float u=Vector3.Dot(t,p)*invDet;if(u<0f||u>1f)return false;
      Vector3 q=Vector3.Cross(t,edge1);float v=Vector3.Dot(direction,q)*invDet;if(v<0f||u+v>1f)return false;float d=Vector3.Dot(edge2,q)*invDet;if(d<epsilon)return false;distance=d;return true;
    }

    public void RequestMiniMapSnapshot(){
      miniMapCaptureRequested=true;
    }

    public void RequestMapTeleport(float worldX,float worldZ){
      float y;
      if(!TrySampleMapHeight(worldX,worldZ,out y))y=Math.Max(boundsMin.Y+2f,Math.Min(boundsMax.Y+2f,camera.Position.Y));
      camera.Position=new Vector3(worldX,y+MapTeleportClearance,worldZ);
      currentCameraRoom=FindCameraRoom(camera.Position);
      InvalidateTemporalHistory();
      if(Window is WorldBrowser browser)browser.SetStatusLabel(string.Format(System.Globalization.CultureInfo.InvariantCulture,"Teleported to {0:0}, {1:0}, {2:0}",worldX*10f,worldZ*10f,y*10f));
    }

    public void GetMapPose(out float x,out float z,out float lookX,out float lookZ){
      Vector3 pos=camera.Position,look=camera.Look;
      x=pos.X;z=pos.Z;lookX=look.X;lookZ=look.Z;
    }

    public void Clear(){ClearSpaceFlypathState();EndTaxiRide(null);CloseTaxiRouteMapState();CloseQuickTravelMapState();ReleaseWorldGpu(); SetImplicitPhaseName(String.Empty); pathFollowers.Clear();instanceWorldTransforms.Clear(); models.Clear();materials.Clear();rooms.Clear();area=null;}
    private void ReleaseWorldGpu(){
      // Capture every material while population/SPN/model references are still alive.  Appearance-specific NPC
      // materials are not guaranteed to live in the area's top-level MAT dictionary; releasing only that dictionary
      // leaked their SRVs every time a world was cleared and made long sessions steadily consume VRAM.
      List<GR2_Material> worldOwnedMaterials=WorldMaterialResidencySet().ToList();
      fx?.ClearWater();
      ReleaseTaxiRouteMapGpu();
      foreach(GR2_Material material in worldOwnedMaterials)ReleaseOwnedMaterial(material);
      ReleaseJedipediaFeatureGpu();
      foreach(var g in terrainGpu.Values)g.Dispose();terrainGpu.Clear();foreach(var g in terrainIndexCache.Values)g.Dispose();terrainIndexCache.Clear(); foreach(var g in waterGpu.Values)g.Dispose();waterGpu.Clear(); foreach(var g in roadGpu)g.Dispose();roadGpu.Clear();foreach(var g in mapNoteFallbackGpu)g.Dispose();mapNoteFallbackGpu.Clear();foreach(var g in mapArtGpu)g.Dispose();mapArtGpu.Clear();mapArtPrepared=false;foreach(var g in mapNoteIconGpu.Values)g.Dispose();mapNoteIconGpu.Clear();materialLastUseFrame.Clear();modelGeometryPrepared.Clear();modelGeometryLastUseFrame.Clear();worldRenderFrame=0;Release(ref selectedWorldBoxBuffer);ClearWorldModelSelection();
      foreach(var list in dynamicDetailGpu.Values)foreach(var g in list)g.Dispose();dynamicDetailGpu.Clear();
      foreach(var list in dynamicDetailMeshBatches.Values)foreach(var g in list)g.Dispose();dynamicDetailMeshBatches.Clear();
      instanceWorldTransforms.Clear();heightMapFloorGrid.Clear();heightMapFloors.Clear();roomPlacementGrid.Clear();roomPlacementGlobal.Clear();volumeMembershipGrid.Clear();volumeMembershipGlobal.Clear();modelFloorData.Clear();modelFloorPlacementGrid.Clear();modelFloorPlacementGlobal.Clear();renderGrid.Clear();renderGlobal.Clear();renderEntriesByRoom.Clear();occluderRenderEntries.Clear();walkingPathFollowerRenderEntries.Clear();decorationHookRenderEntries.Clear();teleportWarmupModels.Clear();teleportWarmupQueued.Clear();cameraWarmupModels.Clear();cameraWarmupQueued.Clear();cameraWarmupAnchor=new Vector3(float.NaN,float.NaN,float.NaN);cameraWarmupRoomName=String.Empty;lock(teleportWarmupLock){teleportWarmupRequestPending=false;teleportWarmupCancelPending=false;}Release(ref regularModelInstanceBuffer);regularModelInstanceCapacity=0;regularModelInstanceScratch=Array.Empty<float>();regularModelInstancingSafe.Clear();visualLodLevels.Clear();lodSchemas.Clear();localLightGrid.Clear();localLightGlobal.Clear();localLightSelectionCache.Clear();localLightVisibilityScope=null;currentLocalLightSelection=LocalLightSelection.Empty;boundLocalLightTexturePaths.Clear();roomPortals.Clear();roomStreamingNeighbors.Clear();roomStreamingAuthoredVisible.Clear();roomStreamingBounds.Clear();streamTransitionRoomGrace.Clear();lastStreamAnchorRoom=String.Empty;Array.Clear(lastLocalLightSelection,0,lastLocalLightSelection.Length);lastLocalLightCount=-1;currentCameraRoom=null;displayCameraRoom=null;skyRoomNames.Clear();
      foreach(var m in dynamicDetailMaterials.Values)ReleaseOwnedMaterial(m);dynamicDetailMaterials.Clear();
      var releasedDydModels=new HashSet<GR2>();foreach(var model in dynamicDetailMeshModels.Values)if(model!=null&&releasedDydModels.Add(model))ReleaseModelBuffers(model);dynamicDetailMeshModels.Clear();
      foreach(var model in strongholdHookModels.Values)if(model!=null&&releasedDydModels.Add(model))ReleaseModelBuffers(model);strongholdHookModels.Clear();
      Release(ref defaultWaterNormal);Release(ref defaultWaterDepth);
      foreach(var v in textureCache.Values)v?.Dispose();textureCache.Clear();textureLastUseFrame.Clear();pinnedTexturePaths.Clear();appliedTextureMipSkip=-1;
      foreach(var m in terrainMaterials.Values){Release(ref m.diffuseSRV);Release(ref m.diffuse2SRV);Release(ref m.rotationSRV);Release(ref m.glossSRV);Release(ref m.waterSurfaceSRV);}terrainMaterials.Clear();
      foreach(var room in rooms)foreach(var inst in room.InstancesById.Values){Release(ref inst.VBO);Release(ref inst.IBO);}
      foreach(var model in models.Values)ReleaseModelBuffers(model);
      modelStreamer?.Dispose();modelStreamer=null;materialMetadataStreamer?.Dispose();materialMetadataStreamer=null;streamRequestsByRoom.Clear();streamPlacementsByAsset.Clear();streamPlacementsByRoomAsset.Clear();streamRoomAssetHints.Clear();streamBootstrapAssetsByCell.Clear();streamBootstrapRoomsByCell.Clear();streamRequestsByAsset.Clear();activeStreamAssetIds.Clear();queuedStreamDemandAssetIds.Clear();urgentStreamAssetIds.Clear();materialStreamAssetIds.Clear();activeStreamRoomNames.Clear();previousStreamRoomNames.Clear();floorStreamRoomNames.Clear();previousFloorStreamRoomNames.Clear();initialStreamAssetIds.Clear();initialStreamRoomNames.Clear();initialStreamPrefetchRoomNames.Clear();initialStreamAssetDistances.Clear();streamIndexedInstances.Clear();streamFloorIndexedInstances.Clear();streamedWorldModels.Clear();streamedModelAssetIds.Clear();pendingModelGpuUploads.Clear();queuedModelGpuUploads.Clear();pendingStreamedModelIntegrations.Clear();queuedStreamedModelIntegrations.Clear();pendingStreamedFloorIntegrations.Clear();queuedStreamedFloorIntegrations.Clear();pendingStreamedModelMaterials.Clear();queuedStreamedModelMaterials.Clear();pendingStreamedMaterialPrepares.Clear();streamedWorldMaterials.Clear();streamedMaterialResourcesPrepared.Clear();streamedModelMaterialsReady.Clear();cameraAheadStreamRooms.Clear();urgentCameraAheadStreamRooms.Clear();streamTransitionRoomGrace.Clear();lastStreamAnchorRoom=String.Empty;streamPrefetchAnchor=new Vector3(float.NaN,float.NaN,float.NaN);streamPrefetchLook=new Vector3(float.NaN,float.NaN,float.NaN);streamPrefetchRoom=String.Empty;
    }
    private static void ReleaseModelBuffers(GR2 model){foreach(var mesh in model.meshes){Release(ref mesh.vertBuffer);Release(ref mesh.idxBuffer);}foreach(var a in model.attachedModels)ReleaseModelBuffers(a);}
    private static void Release<T>(ref T v) where T:class,IDisposable{v?.Dispose();v=null;}

    protected override void Dispose(bool disposing){
      if(!_disposed){if(disposing){ReleaseWorldGpu();DisposeFeatureTextRenderer();ReleasePostTargets();Release(ref sceneDepthShaderResource);for(int i=0;i<shadowMaps.Length;i++)shadowMaps[i]?.Dispose();dynamicDetailLayout?.Dispose();instancedLayout?.Dispose();skinnedLayout?.Dispose();inputLayout?.Dispose();fx?.Dispose();RenderStates.DestroyAll();}_disposed=true;}base.Dispose(disposing);
    }

    public override void OnResize(){
      base.OnResize();
      // SpriteTextRenderer caches the D3D viewport/screen size internally. The WinForms sidebar resizes only the
      // swap-chain panel, so without refreshing this cache DrawString continues converting pixel coordinates with
      // the previous render-panel dimensions. That makes otherwise correct 3D-projected nameplates slide/scale as
      // the splitter moves. Refresh it on the render thread immediately after base.OnResize() installed the viewport.
      try { npcTextSprite?.RefreshViewport(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine("NPC text viewport refresh failed: " + ex.Message); }
      InvalidateObjectOcclusionVisibility();
      objectOcclusionViewportWidth=-1;objectOcclusionViewportHeight=-1;
      CreateSampleableDepthTarget();
      CreateSceneTarget();
      WorldRenderSettings resizeSettings=SettingsSnapshot();
      float cameraFar=GetCameraFarDistance(activeClipDistance,resizeSettings);
      ApplyCameraLens(resizeSettings,cameraFar,true);
      appliedCameraFar=cameraFar;
      UpdateMapCamera();
    }

    private void CreateSampleableDepthTarget(){
      Release(ref sceneDepthShaderResource); Release(ref DepthStencilView); Release(ref DepthStencilBuffer);
      if(Device==null || ClientWidth<=0 || ClientHeight<=0)return;
      var desc=new Texture2DDescription{Width=ClientWidth,Height=ClientHeight,MipLevels=1,ArraySize=1,Format=Format.R24G8_Typeless,SampleDescription=new SampleDescription(1,0),Usage=ResourceUsage.Default,BindFlags=BindFlags.DepthStencil|BindFlags.ShaderResource,CpuAccessFlags=CpuAccessFlags.None,OptionFlags=ResourceOptionFlags.None};
      DepthStencilBuffer=new Texture2D(Device,desc){DebugName="World sampleable depth"};
      var dsvDesc=new DepthStencilViewDescription{Flags=DepthStencilViewFlags.None,Format=Format.D24_UNorm_S8_UInt,Dimension=DepthStencilViewDimension.Texture2D,MipSlice=0};
      DepthStencilView=new DepthStencilView(Device,DepthStencilBuffer,dsvDesc);
      var srvDesc=new ShaderResourceViewDescription{Format=Format.R24_UNorm_X8_Typeless,Dimension=ShaderResourceViewDimension.Texture2D,MipLevels=1,MostDetailedMip=0};
      sceneDepthShaderResource=new ShaderResourceView(Device,DepthStencilBuffer,srvDesc);
      ImmediateContext.OutputMerger.SetTargets(DepthStencilView,RenderTargetView);
    }

    private void CreateSceneTarget(){
      ReleasePostTargets();
      if(Device==null || ClientWidth<=0 || ClientHeight<=0) return;
      // Jedipedia keeps the composite/history in floating point; using RGBA16F avoids quantising the 0.9 TAA
      // accumulation step and also preserves lighting headroom until the final LUT/copy into the swap chain.
      var desc=new Texture2DDescription{Width=ClientWidth,Height=ClientHeight,MipLevels=1,ArraySize=1,Format=Format.R16G16B16A16_Float,SampleDescription=new SampleDescription(1,0),Usage=ResourceUsage.Default,BindFlags=BindFlags.RenderTarget|BindFlags.ShaderResource,CpuAccessFlags=CpuAccessFlags.None,OptionFlags=ResourceOptionFlags.None};
      sceneTexture=new Texture2D(Device,desc){DebugName="World scene composite"}; sceneRenderTarget=new RenderTargetView(Device,sceneTexture); sceneShaderResource=new ShaderResourceView(Device,sceneTexture);
      InvalidateTemporalHistory();
    }

    private void ReleasePostTargets(){
      fx?.ClearAntiAliasing(); fx?.ClearPost();
      Release(ref aaTempShaderResource);Release(ref aaTempRenderTarget);Release(ref aaTempTexture);
      for(int i=0;i<2;i++){Release(ref taaHistoryShaderResources[i]);Release(ref taaHistoryRenderTargets[i]);Release(ref taaHistoryTextures[i]);}
      Release(ref sceneShaderResource);Release(ref sceneRenderTarget);Release(ref sceneTexture);
      InvalidateTemporalHistory();
    }

    private void EnsureAntiAliasingTargets(bool taa,bool fxaa){
      if(Device==null||ClientWidth<=0||ClientHeight<=0)return;
      if(!taa&&taaHistoryTextures[0]!=null){for(int i=0;i<2;i++){Release(ref taaHistoryShaderResources[i]);Release(ref taaHistoryRenderTargets[i]);Release(ref taaHistoryTextures[i]);}InvalidateTemporalHistory();}
      if(!fxaa&&aaTempTexture!=null){Release(ref aaTempShaderResource);Release(ref aaTempRenderTarget);Release(ref aaTempTexture);}
      var desc=new Texture2DDescription{Width=ClientWidth,Height=ClientHeight,MipLevels=1,ArraySize=1,Format=Format.R16G16B16A16_Float,SampleDescription=new SampleDescription(1,0),Usage=ResourceUsage.Default,BindFlags=BindFlags.RenderTarget|BindFlags.ShaderResource,CpuAccessFlags=CpuAccessFlags.None,OptionFlags=ResourceOptionFlags.None};
      if(taa&&taaHistoryTextures[0]==null){for(int i=0;i<2;i++){taaHistoryTextures[i]=new Texture2D(Device,desc){DebugName="World TAA history "+i};taaHistoryRenderTargets[i]=new RenderTargetView(Device,taaHistoryTextures[i]);taaHistoryShaderResources[i]=new ShaderResourceView(Device,taaHistoryTextures[i]);}InvalidateTemporalHistory();}
      if(fxaa&&aaTempTexture==null){aaTempTexture=new Texture2D(Device,desc){DebugName="World FXAA target"};aaTempRenderTarget=new RenderTargetView(Device,aaTempTexture);aaTempShaderResource=new ShaderResourceView(Device,aaTempTexture);}
    }

    private void InvalidateTemporalHistory(){taaHistoryValid=false;hasPreviousViewProj=false;taaReprojectionValid=false;taaHistoryIndex=0;taaFrame=0;taaJitterPixels=Vector2.Zero;taaReprojection=Matrix.Identity;}

    public override void UpdateScene(float dt){
      base.UpdateScene(dt);elapsed+=dt;
      WorldRenderSettings navigationSettings=SettingsSnapshot();
      // Projection transitions and the eased ortho zoom keep running even while a toolbar/search box owns input.
      UpdateOrthographicState(dt,navigationSettings);
      bool inputActive=Window is WorldBrowser browser&&(mapOpen?browser.WorldWindowInputEnabled:browser.WorldKeyboardInputEnabled);
      if(!inputActive){
        // Preserve the physical edge state while another UI surface (notably Search) owns input,
        // otherwise releasing focus with M still held could immediately open the map.
        mapKeyWasDown=Util.IsKeyDown(Keys.M);
        escapeKeyWasDown=Util.IsKeyDown(Keys.Escape);
        return;
      }

      bool mapKey=Util.IsKeyDown(Keys.M);
      if(mapKey&&!mapKeyWasDown){
        bool mapShift=Util.IsKeyDown(Keys.LShiftKey)||Util.IsKeyDown(Keys.RShiftKey);
        if(mapShift&&Window is WorldBrowser mapBrowser)mapBrowser.ToggleMiniMapFromRenderer();
        else SetMapOpen(!mapOpen);
      }
      mapKeyWasDown=mapKey;
      bool escapeKey=Util.IsKeyDown(Keys.Escape);
      if(mapOpen&&escapeKey&&!escapeKeyWasDown)SetMapOpen(false);
      escapeKeyWasDown=escapeKey;
      if(Util.IsKeyDown(Keys.PrintScreen))makeScreenshot=true;
      if(mapOpen)return;
      if(UpdateSpaceFlypath(dt,escapeKey))return;
      if(UpdateTaxiRide(dt,escapeKey))return;

      bool shift=Util.IsKeyDown(Keys.LShiftKey)||Util.IsKeyDown(Keys.RShiftKey);
      bool alt=Util.IsKeyDown(Keys.LMenu)||Util.IsKeyDown(Keys.RMenu);
      float multiplier=shift?(alt?100f:10f):1f;

      if(orthographicActive){
        if(Util.IsKeyDown(Keys.R)){
          Vector3 c=(boundsMin+boundsMax)*.5f;camera.Position=new Vector3(c.X,camera.Position.Y,c.Z);
          orthographicHeading=Vector3.UnitZ;orthographicPitch=OrthographicMaxPitch;orthographicZoom=orthographicZoomTarget=1f;orthographicFloorY=float.NaN;ApplyOrthographicOrientation();
        }
        float lookStep=1.8f*dt;bool orientationChanged=false;
        float yaw=0f;if(Util.IsKeyDown(Keys.A)||Util.IsKeyDown(Keys.J))yaw-=lookStep;if(Util.IsKeyDown(Keys.D)||Util.IsKeyDown(Keys.L))yaw+=lookStep;
        if(Math.Abs(yaw)>.000001f){orthographicHeading=Vector3.TransformNormal(orthographicHeading,Matrix.RotationY(yaw));orthographicHeading=HorizontalUnit(orthographicHeading,Vector3.UnitZ);orientationChanged=true;}
        float pitch=orthographicPitch;if(Util.IsKeyDown(Keys.I))pitch-=lookStep;if(Util.IsKeyDown(Keys.K))pitch+=lookStep;
        pitch=Math.Max(OrthographicMinPitch,Math.Min(OrthographicMaxPitch,pitch));if(Math.Abs(pitch-orthographicPitch)>.000001f){orthographicPitch=pitch;orientationChanged=true;}
        if(orientationChanged)ApplyOrthographicOrientation();

        Vector3 right=HorizontalUnit(camera.Right,Vector3.UnitX);
        Vector3 velocity=Vector3.Zero;
        if(Util.IsKeyDown(Keys.W))velocity+=orthographicHeading;if(Util.IsKeyDown(Keys.S))velocity-=orthographicHeading;
        if(Util.IsKeyDown(Keys.Q))velocity-=right;if(Util.IsKeyDown(Keys.E))velocity+=right;
        if(velocity.LengthSquared()>.000001f){velocity.Normalize();float speed=cameraSpeed*multiplier*GetOrthographicMoveScale(navigationSettings);camera.Position+=velocity*(speed*dt);}
        return;
      }

      if(Util.IsKeyDown(Keys.R)){Vector3 c=(boundsMin+boundsMax)*.5f;camera.LookAt(c+new Vector3(0,5,20),c,new Vector3(0,1,0));}
      if(UpdateWalkingMode(dt))return;
      float freeSpeed=cameraSpeed*multiplier;

      // Match Jedipedia's perspective keyboard layout. A/D and J/L turn, I/K pitch, Q/E strafe, while W/S
      // move along the actual view direction and Space lifts in world Y. Build one normalized velocity so diagonal
      // movement is not faster than a single-axis move. PugTools' RH view uses -Look as the visible forward vector.
      float perspectiveLookStep=1.8f*dt;
      if(Util.IsKeyDown(Keys.A)||Util.IsKeyDown(Keys.J))camera.Yaw(perspectiveLookStep);
      if(Util.IsKeyDown(Keys.D)||Util.IsKeyDown(Keys.L))camera.Yaw(-perspectiveLookStep);
      if(Util.IsKeyDown(Keys.I))PitchPerspectiveCamera(perspectiveLookStep);
      if(Util.IsKeyDown(Keys.K))PitchPerspectiveCamera(-perspectiveLookStep);

      Vector3 freeMove=Vector3.Zero;
      Vector3 visibleForward=-camera.Look;
      Vector3 visibleRight=camera.Right;
      if(Util.IsKeyDown(Keys.W))freeMove+=visibleForward;
      if(Util.IsKeyDown(Keys.S))freeMove-=visibleForward;
      if(Util.IsKeyDown(Keys.E))freeMove+=visibleRight;
      if(Util.IsKeyDown(Keys.Q))freeMove-=visibleRight;
      if(Util.IsKeyDown(Keys.Space))freeMove+=Vector3.UnitY;
      if(freeMove.LengthSquared()>.000001f){freeMove.Normalize();camera.Position+=freeMove*(freeSpeed*dt);}
    }

    private void PitchPerspectiveCamera(float delta){
      // Jedipedia clamps perspective pitch just short of the poles (about 87 degrees). Derive the visible pitch
      // from -Look so keyboard look cannot flip the FPS basis even if a key is held for a long time.
      const float maxPitch=1.52f;
      float y=Math.Max(-1f,Math.Min(1f,-camera.Look.Y));
      float current=(float)Math.Asin(y);
      float target=Math.Max(-maxPitch,Math.Min(maxPitch,current+delta));
      float applied=target-current;
      if(Math.Abs(applied)>.000001f)camera.Pitch(applied);
    }

    public override void DrawScene(){
      base.DrawScene(); if(fx==null)return; if(temporalHistoryResetRequested){InvalidateTemporalHistory();temporalHistoryResetRequested=false;} WorldRenderSettings s=SettingsSnapshot();
      EnsureTextureQualityResources(s);
      EnsureShadowQualityResources(s);
      ProcessTeleportWarmup();
      bool captureMiniMap=miniMapCaptureRequested&&!mapOpen;
      bool requestedMapNotes=s.ShowMapNotes;
      // The full map/minimap is intentionally static. Freezing follower/SPN animation while a map frame is being
      // rendered removes a large amount of per-frame transform/CPU-skin work without changing the normal 3D view;
      // elapsed time is absolute, so followers immediately resume at the correct pose after the map closes.
      if(!mapOpen&&!captureMiniMap)UpdatePathFollowers(elapsed);
      if(captureMiniMap){miniMapCaptureRequested=false;ResetMapCamera();}
      if(mapOpen||captureMiniMap){
        s=s.Clone();
        s.Mode=WorldRenderMode.Map;
        s.EnableFog=false;
        s.EnableShadows=false;
        s.EnablePostProcessing=false;
        s.EnableLocalLights=false;
        s.ShowSky=false;
        s.ShowDynamicDetails=false;
        // Keep the user's population visibility setting intact. Only animation is frozen below; this preserves the
        // feature set while avoiding the costly per-frame CPU skinning/route work on full world/minimap renders.
        // Generated maps suppress editor helpers regardless of the 3D-view submenu toggles. Without the old
        // top-level Show tier these have to be disabled explicitly on the capture clone.
        s.ShowUtilitySpawners=false;
        s.ShowUtilityCoverPoints=false;
        s.ShowUtilityLights=false;
        s.ShowUtilitySeedPoints=false;
        s.ShowUtilityPaths=false;
        s.ShowUtilityMapRoadPaths=false;
        s.ShowUtilityConnections=false;
        s.ShowUtilityVolumes=false;
        s.ShowUtilityOther=false;
        s.ShowHiddenGeometry=false;
        s.ShowTerrain=true;
        s.ShowModels=true;
        s.ShowWater=true;
        s.AnimateNpcs=false;
        s.AnimateSpnObjects=false;
        // The interactive M map now keeps the user's map-note layer and renders original game symbols. A generated
        // minimap snapshot stays symbol-free because the WinForms overlay paints the same icons live on top of it.
        s.ShowMapNotes=mapOpen&&requestedMapNotes;
        // Jedipedia's M map is a render of the actual terrain/world layout. Authored 2D map art remains
        // available through the explicit toolbar Map mode, but does not cover the interactive M map.
        s.ShowMapArt=false;
      }
      Room cameraRoom=FindCameraRoom(camera.Position);
      // A streamed interior can be known from conservative placement bounds before its collision floor is indexed.
      // Use that semantic room for visibility/environment while the authoritative floor locator catches up; otherwise
      // Corellia starts in the outdoor cell underneath the hangar and immediately culls the shell we just streamed.
      Room renderCameraRoom=cameraRoom;
      if(modelStreamer!=null&&s.Mode!=WorldRenderMode.Map){Room streamed=ResolveStreamingRoom(camera.Position,cameraRoom);if(streamed!=null&&!IsEverywhereRoom(streamed))renderCameraRoom=streamed;}
      UpdateStreamTransitionGrace(renderCameraRoom);
      UpdateCurrentPhase(camera.Position);
      // Jedipedia does not let the synthetic `_everywhere_` cell choose a room environment/skyscene. When the
      // camera has no concrete visibility room it explicitly falls back to envSchemes.area. Using `_everywhere_`'s
      // scheme here was enough to replace Dantooine's authored sky with the plain fog/clear colour.
      AreaEnvironmentScheme env=renderCameraRoom!=null&&!IsEverywhereRoom(renderCameraRoom)
        ? renderCameraRoom.EnvironmentScheme??area?.GetEnvironmentScheme("area")??new AreaEnvironmentScheme()
        : area?.GetEnvironmentScheme("area")??new AreaEnvironmentScheme();
      UpdateActiveClipDistance(env,s);
      UpdateWorldConversationCamera();
      camera.UpdateViewMatrix();
      bool inGame=s.Mode==WorldRenderMode.InGame;
      bool aaAllowed=s.Mode!=WorldRenderMode.Map;
      bool useTaa=aaAllowed&&(s.AntiAliasing==WorldAntiAliasing.TAA||s.AntiAliasing==WorldAntiAliasing.TAAFXAA);
      bool useFxaa=aaAllowed&&(s.AntiAliasing==WorldAntiAliasing.FXAA||s.AntiAliasing==WorldAntiAliasing.TAAFXAA);
      bool useColorPost=inGame&&s.EnablePostProcessing;
      EnsureAntiAliasingTargets(useTaa,useFxaa);

      Matrix viewProj;
      Vector3 cam;
      if(s.Mode==WorldRenderMode.Map){
        viewProj=mapView*mapProj;frameProjection=mapProj;
        cam=mapCameraPosition;
      } else {
        cam=camera.Position;
        Matrix currentUnjittered=camera.ViewProj;
        if(useTaa)BeginTemporalFrame(currentUnjittered,out viewProj);
        else {viewProj=currentUnjittered;frameProjection=camera.Proj;taaJitterPixels=Vector2.Zero;taaReprojectionValid=false;}
      }

      bool shadows=s.EnableShadows&&s.Mode!=WorldRenderMode.Map&&s.Mode!=WorldRenderMode.Heightmap&&env.CastDirectionalShadows&&shadowMaps.All(x=>x!=null);
      HashSet<string> visible=BuildVisibleRoomSet(renderCameraRoom,s);
      // Decode/MAT metadata happen on background TOR workers. The render thread commits local placements and a
      // small time-bounded D3D upload slice; collision-floor indexing is lower priority and cannot hold geometry back.
      RequestVisibleRoomModels(visible,renderCameraRoom,env,s);
      PumpDecodedWorldModels();
      ProcessStreamedModelIntegrations();
      ProcessModelGpuUploads();
      // Spatial correctness outranks texture refinement: finish the current/startup room locator before spending
      // this frame's remaining archive/D3D budget on DDS resources.
      ProcessStreamedFloorIntegrations();
      ProcessStreamedModelMaterials();
      if((worldRenderFrame%6)==0)ReportWorldStreamingProgress();
      ProcessCameraWarmup(renderCameraRoom,visible,s);
      PrepareObjectOcclusionFrame(s,visible);
      // Local lights and receiver meshes are static. Keep the cell selections across frames and invalidate only
      // when the active room/visibility mode changes; this removes the remaining per-frame CPU selection cost.
      string lightScope=(renderCameraRoom?.RoomName??String.Empty)+(s.ShowSky?"|sky|":"|nosky|")+VisibleRoomScope(visible);
      if(!String.Equals(lightScope,localLightVisibilityScope,StringComparison.Ordinal)){localLightSelectionCache.Clear();localLightVisibilityScope=lightScope;}
      // Dynamic-detail shadow cards need the same camera/time values as their visible pass.
      fx.SetCamera(cam);fx.SetScrolling(env,elapsed,LoadTexture(env.ScrollingTexture),LoadTexture(env.ScrollingMask));
      if(shadows)RenderShadowCascades(env,visible,s);

      bool useOffscreen=sceneRenderTarget!=null&&(useColorPost||useTaa||useFxaa);
      RenderTargetView target=useOffscreen?sceneRenderTarget:RenderTargetView;
      ImmediateContext.OutputMerger.SetTargets(DepthStencilView,target);ImmediateContext.Rasterizer.SetViewports(Viewport);
      var clear=new Color4(env.FogColorSky.W,env.FogColorSky.X,env.FogColorSky.Y,env.FogColorSky.Z);
      ImmediateContext.ClearRenderTargetView(target,clear);ImmediateContext.ClearDepthStencilView(DepthStencilView,DepthStencilClearFlags.Depth|DepthStencilClearFlags.Stencil,1,0);
      ImmediateContext.InputAssembler.InputLayout=inputLayout;ImmediateContext.InputAssembler.PrimitiveTopology=PrimitiveTopology.TriangleList;
      fx.SetViewProj(viewProj);fx.SetCamera(cam);fx.SetEnvironment(env,s.EnableLighting,s.EnableFog,shadows,s.ViewDistanceScale);fx.SetHeightRange(boundsMin.Y,boundsMax.Y);fx.SetPlaceableBlueGlow(false);
      ShaderResourceView illum=LoadTexture(env.IlluminationMap);fx.SetIllumination(illum);fx.SetScrolling(env,elapsed,LoadTexture(env.ScrollingTexture),LoadTexture(env.ScrollingMask));
      var maps=shadowMaps.Select(x=>x?.DepthMapSRV).ToArray();fx.SetShadows(shadowMatrices,maps,shadowDistances,shadows);fx.ClearLocalLights();currentLocalLightSelection=LocalLightSelection.Empty;lastLocalLightCount=0;Array.Clear(lastLocalLightSelection,0,lastLocalLightSelection.Length);
      UpdateJedipediaDynamicLights(s);
      // Jedipedia renders the skyscene as a backdrop and clears depth before the actual world. Drawing it
      // mixed into the model pass lets sky geometry fight with terrain/models and is responsible for many
      // floating/half-screen artefacts when a skyscene happens to intersect the world depth buffer.
      if(s.ShowSky&&s.Mode!=WorldRenderMode.Map&&s.Mode!=WorldRenderMode.Heightmap){
        DrawSky(env,s);
        ImmediateContext.ClearDepthStencilView(DepthStencilView,DepthStencilClearFlags.Depth|DepthStencilClearFlags.Stencil,1,0);
      }
      // Jedipedia's native dPVS consumes authored occlusion geometry before deciding which receivers are visible.
      // We cannot run its WASM/native solver here, but the same LOD -3/OCCLUDER_ONLY meshes make an effective
      // conservative D3D11 depth prepass and also give nameplate occlusion the authored walls instead of text-only heuristics.
      if(s.ShowModels&&s.EnableOccluderPrepass&&s.Mode!=WorldRenderMode.Map&&s.Mode!=WorldRenderMode.Heightmap&&s.Mode!=WorldRenderMode.Wireframe)
        DrawOccluderPrepass(viewProj,visible,s);
      if(s.ShowTerrain)DrawTerrain(viewProj,visible,s,env,shadows);if(s.ShowDynamicDetails&&s.Mode!=WorldRenderMode.Map&&s.Mode!=WorldRenderMode.Heightmap)DrawDynamicDetails(viewProj,visible,s,env,shadows);if(s.ShowModels)DrawModels(viewProj,visible,s,env,shadows);if((s.ShowNpcs||s.ShowTaxiTerminals)&&s.Mode!=WorldRenderMode.Heightmap&&s.Mode!=WorldRenderMode.Map)DrawJedipediaNpcs(viewProj,visible,s,env,shadows);if(s.ShowSpnObjects&&s.Mode!=WorldRenderMode.Heightmap)DrawJedipediaSpnObjects(viewProj,visible,s,env,shadows);DrawTaxiVehicle(viewProj,visible,s,env,shadows);DrawSpaceCombatShips(viewProj,visible,s,env,shadows);if(s.ShowDecorationHooks&&s.Mode!=WorldRenderMode.Map&&s.Mode!=WorldRenderMode.Heightmap)DrawDecorationHooks(viewProj,visible,s,env,shadows);if(s.ShowWater)DrawWater(viewProj,visible,s);
      if(s.Mode==WorldRenderMode.Map&&s.ShowMapArt)DrawMapArt(viewProj);
      if(s.ShowRoads)DrawLines(roadGpu,viewProj,s.Mode==WorldRenderMode.Map?float.MaxValue:camera.FarZ);
      if(s.ShowMapNotes&&s.Mode==WorldRenderMode.Map){
        if(s.ShowMapIconOther)DrawLines(mapNoteFallbackGpu,viewProj,float.MaxValue);
        DrawMapNoteIcons(viewProj,s);
      }
      if(s.Mode!=WorldRenderMode.Heightmap)DrawJedipediaUtilities(viewProj,visible,s);
      // The generated minimap already paints its marker in WinForms. Only the interactive M map needs the GPU
      // marker, otherwise the minimap capture would bake a second arrow into its bitmap.
      if(mapOpen&&s.Mode==WorldRenderMode.Map){DrawTaxiRouteMapOverlay(viewProj,s);DrawQuickTravelMapOverlay(viewProj,s);DrawMapPlayerMarker(viewProj);}
      DrawWorldSelectionOutline(viewProj,s);

      if(useOffscreen)ResolvePostProcessing(s,env,useTaa,useFxaa);
      if(s.Mode!=WorldRenderMode.Heightmap){
        // UI/nameplate projection must stay on the stable camera matrix while the depth query samples the matrix
        // that actually rendered this frame (jittered when TAA is enabled).
        Matrix labelViewProj=s.Mode==WorldRenderMode.Map?viewProj:camera.ViewProj;
        DrawNpcNameplates(labelViewProj,viewProj,visible,s);
        DrawWorldInteractionIcons(labelViewProj,viewProj,visible,s);
        DrawWorldSelectionLabel(labelViewProj,s);
        DrawWorldConversationSubtitleHud(s);
        DrawSpaceFlypathHud(s);
        DrawTaxiRideHud(s);
      }
      UpdateObjectOcclusionVisibility(viewProj,visible,s);
      UpdateWorldRenderStatsSnapshot();
      worldRenderFrame++;
      // Spread cache maintenance across frames.  Running all six world-wide scans on the same frame produced a
      // visible hitch on NPC-heavy planets even though each cache keeps the same ~180-frame eviction cadence.
      if((worldRenderFrame%30)==0){
        switch((int)((worldRenderFrame/30)%6)){
          case 0:TrimMaterialTextureResidency();break;
          case 1:TrimWaterTextureResidency(s);break;
          case 2:TrimSharedTextureResidency();break;
          case 3:TrimTerrainTextureResidency(s);break;
          case 4:TrimModelGeometryResidency();break;
          default:TrimNpcSkinStateResidency();TrimStreamedModelCpuResidency();break;
        }
      }
      if(captureMiniMap){
        Bitmap snapshot=CaptureBackBufferBitmap();
        if(snapshot!=null&&Window is WorldBrowser browser){
          snapshot=CropMiniMapToArea(snapshot,out float minX,out float maxX,out float minZ,out float maxZ);
          browser.PostMiniMapSnapshot(snapshot,minX,maxX,minZ,maxZ);
        } else snapshot?.Dispose();
        // Do not present the temporary top-down render. The next render iteration immediately draws the
        // normal camera again, so opening/refreshing the minimap never flashes a full-screen map.
        return;
      }
      if(makeScreenshot){MakeScreenshot(ImageFileFormat.Jpg);makeScreenshot=false;}
      SwapChain.Present(1,PresentFlags.None);
    }

    private Bitmap CaptureBackBufferBitmap(){
      try{
        var d=new Texture2DDescription{Width=ClientWidth,Height=ClientHeight,MipLevels=1,ArraySize=1,Format=Format.R8G8B8A8_UNorm,SampleDescription=new SampleDescription(1,0),Usage=ResourceUsage.Default,BindFlags=BindFlags.None,CpuAccessFlags=CpuAccessFlags.None,OptionFlags=ResourceOptionFlags.None};
        using var output=new Texture2D(Device,d);
        using var back=SlimDX.Direct3D11.Resource.FromSwapChain<Texture2D>(SwapChain,0);
        ImmediateContext.CopyResource(back,output);
        using var ms=new MemoryStream();
        Texture2D.ToStream(ImmediateContext,output,ImageFileFormat.Png,ms);
        ms.Position=0;
        using var temporary=new Bitmap(ms);
        return new Bitmap(temporary);
      }catch(Exception ex){
        System.Diagnostics.Debug.WriteLine("Could not capture World Browser minimap: "+ex.Message);
        return null;
      }
    }

    private void BeginTemporalFrame(Matrix currentUnjittered,out Matrix renderViewProj){
      taaFrame=(taaFrame+1)%8;
      taaJitterPixels=new Vector2(Halton(taaFrame+1,2)-.5f,Halton(taaFrame+1,3)-.5f);
      Matrix jitter=Matrix.Identity;
      jitter.M41=taaJitterPixels.X*2f/Math.Max(1,ClientWidth);
      // Texture-space Y grows downward while clip-space Y grows upward.
      jitter.M42=-taaJitterPixels.Y*2f/Math.Max(1,ClientHeight);
      frameProjection=camera.Proj*jitter;
      renderViewProj=camera.View*frameProjection;

      taaReprojectionValid=false;
      if(hasPreviousViewProj){
        try{
          Matrix currentInverse;Matrix.Invert(ref currentUnjittered,out currentInverse);
          taaReprojection=currentInverse*previousViewProj;
          taaReprojectionValid=MatrixFinite(taaReprojection);
        }catch{taaReprojection=Matrix.Identity;taaReprojectionValid=false;}
      }
      previousViewProj=currentUnjittered;hasPreviousViewProj=true;
    }

    private void ResolvePostProcessing(WorldRenderSettings s,AreaEnvironmentScheme env,bool useTaa,bool useFxaa){
      ShaderResourceView finalSource=sceneShaderResource;
      var texel=new Vector2(1f/Math.Max(1,ClientWidth),1f/Math.Max(1,ClientHeight));
      ImmediateContext.InputAssembler.InputLayout=null;ImmediateContext.InputAssembler.PrimitiveTopology=PrimitiveTopology.TriangleList;

      if(useTaa&&taaHistoryRenderTargets[0]!=null&&sceneDepthShaderResource!=null){
        int target=taaHistoryIndex^1;
        ImmediateContext.OutputMerger.SetTargets((DepthStencilView)null,taaHistoryRenderTargets[target]);ImmediateContext.Rasterizer.SetViewports(Viewport);
        ShaderResourceView history=taaHistoryValid?taaHistoryShaderResources[taaHistoryIndex]:null;
        fx.SetTemporalAA(sceneShaderResource,history,sceneDepthShaderResource,taaReprojection,texel,taaJitterPixels,.9f,taaHistoryValid&&taaReprojectionValid);
        fx.TemporalAA.GetPassByIndex(0).Apply(ImmediateContext);ImmediateContext.Draw(3,0);fx.ClearAntiAliasing();
        taaHistoryIndex=target;taaHistoryValid=true;finalSource=taaHistoryShaderResources[target];
      }

      if(useFxaa&&aaTempRenderTarget!=null){
        ImmediateContext.OutputMerger.SetTargets((DepthStencilView)null,aaTempRenderTarget);ImmediateContext.Rasterizer.SetViewports(Viewport);
        fx.SetFxaa(finalSource,texel);fx.Fxaa.GetPassByIndex(0).Apply(ImmediateContext);ImmediateContext.Draw(3,0);fx.ClearAntiAliasing();
        finalSource=aaTempShaderResource;
      }

      ImmediateContext.OutputMerger.SetTargets((DepthStencilView)null,RenderTargetView);ImmediateContext.Rasterizer.SetViewports(Viewport);
      fx.SetPost(finalSource,(s.Mode==WorldRenderMode.InGame&&s.EnablePostProcessing)?LoadTexture(env.ColorLookupTable):null);fx.PostProcess.GetPassByIndex(0).Apply(ImmediateContext);ImmediateContext.Draw(3,0);fx.ClearPost();
      ImmediateContext.InputAssembler.InputLayout=inputLayout;
    }

    private static float Halton(int index,int basis){float result=0f,fraction=1f;int i=index;while(i>0){fraction/=basis;result+=fraction*(i%basis);i/=basis;}return result;}
    private static bool MatrixFinite(Matrix m){for(int r=0;r<4;r++)for(int c=0;c<4;c++){float v=m[r,c];if(float.IsNaN(v)||float.IsInfinity(v))return false;}return true;}

    private void BuildInstanceWorldTransformCache(){
      instanceWorldTransforms.Clear();
      foreach(Room room in rooms)foreach(AssetInstance inst in room.InstancesById.Values)instanceWorldTransforms[inst]=inst.GetAbsoluteTransform(room);
    }

    private static int StreamBootstrapCell(float coordinate){return (int)Math.Floor(coordinate/StreamBootstrapCellSize);}
    private void BuildSpatialStreamingBootstrapIndex(){
      streamBootstrapAssetsByCell.Clear();streamBootstrapRoomsByCell.Clear();streamRoomAssetHints.Clear();
      foreach(var assetPlacements in streamPlacementsByAsset){
        foreach(var placement in assetPlacements.Value){
          if(placement.Room==null||placement.Instance==null)continue;
          Matrix world=InstanceWorld(placement.Instance,placement.Room);
          Vector3 position=new Vector3(world.M41,world.M42,world.M43);
          if(!IsFinite(position))continue;
          var cell=(StreamBootstrapCell(position.X),StreamBootstrapCell(position.Z));
          if(!streamBootstrapAssetsByCell.TryGetValue(cell,out HashSet<ulong> assets))streamBootstrapAssetsByCell[cell]=assets=new HashSet<ulong>();
          assets.Add(assetPlacements.Key);
          if(!String.IsNullOrWhiteSpace(placement.Room.RoomName)){
            if(!streamBootstrapRoomsByCell.TryGetValue(cell,out HashSet<string> ownerRooms))streamBootstrapRoomsByCell[cell]=ownerRooms=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            ownerRooms.Add(placement.Room.RoomName);
          }
          if(!streamRoomAssetHints.TryGetValue(placement.Room.RoomName,out Dictionary<ulong,StreamRoomAssetHint> roomHints))streamRoomAssetHints[placement.Room.RoomName]=roomHints=new Dictionary<ulong,StreamRoomAssetHint>();
          if(!roomHints.TryGetValue(assetPlacements.Key,out StreamRoomAssetHint hint))roomHints[assetPlacements.Key]=hint=new StreamRoomAssetHint();
          hint.Min=new Vector3(Math.Min(hint.Min.X,position.X),Math.Min(hint.Min.Y,position.Y),Math.Min(hint.Min.Z,position.Z));
          hint.Max=new Vector3(Math.Max(hint.Max.X,position.X),Math.Max(hint.Max.Y,position.Y),Math.Max(hint.Max.Z,position.Z));hint.Any=true;
        }
      }
    }

    private Matrix InstanceWorld(AssetInstance inst,Room room){
      if(inst!=null&&instanceWorldTransforms.TryGetValue(inst,out Matrix world))return world;
      return inst?.GetAbsoluteTransform(room)??Matrix.Identity;
    }

    private bool IsPathFollowerAsset(AssetInstance inst){
      if(inst==null||area==null||!area.AssetIdMap.TryGetValue(inst.assetID,out AreaAsset asset))return false;
      return string.Equals((asset.Extension??string.Empty).Trim().TrimStart('.'),"fol",StringComparison.OrdinalIgnoreCase)&&
        string.Equals(NormalizeAssetPath(asset.Path),"engine/follower",StringComparison.OrdinalIgnoreCase);
    }

    private bool IsSpeedTreeInstance(AssetInstance inst){
      if(inst==null||area==null||!area.AssetIdMap.TryGetValue(inst.assetID,out AreaAsset asset)||asset==null)return false;
      return string.Equals((asset.Extension??string.Empty).Trim().TrimStart('.'),"spt",StringComparison.OrdinalIgnoreCase);
    }

    private void BuildPathFollowers(){
      pathFollowers.Clear();
      if(area==null||rooms==null)return;
      foreach(Room room in rooms){
        if(room==null)continue;
        foreach(AssetInstance root in room.InstancesById.Values){
          if(!IsPathFollowerAsset(root))continue;
          root.PathFollowerBoundaryExcluded=true;
          List<AssetInstance> descendants=PathFollowerDescendants(room,root);
          foreach(AssetInstance child in descendants){
            child.PathFollowerPending=true;
            child.PathFollowerAnimated=true;
            child.PathFollowerBoundaryExcluded=true;
          }

          string pathName=(root.PathFollowerPath??string.Empty).Trim();
          if(string.IsNullOrEmpty(pathName))continue;
          AreaPath path=area.Paths.FirstOrDefault(p=>p!=null&&
            (string.Equals((p.Name??string.Empty).Trim(),pathName,StringComparison.OrdinalIgnoreCase)||
             string.Equals((p.Fqn??string.Empty).Trim(),pathName,StringComparison.OrdinalIgnoreCase)));
          if(path==null)continue;
          PathFollowerRoute route=BuildPathFollowerRoute(path);
          if(route==null||route.Points.Count==0)continue;

          foreach(AssetInstance child in descendants)child.PathFollowerPending=false;
          var runtime=new PathFollowerRuntime{Room=room,Root=root,Route=route,Speed=Math.Abs(root.PathFollowerSpeed),TurnRate=Math.Abs(root.PathFollowerTurnRate)};
          runtime.Descendants.AddRange(descendants);
          pathFollowers.Add(runtime);
        }
      }
    }

    private static List<AssetInstance> PathFollowerDescendants(Room room,AssetInstance root){
      var result=new List<AssetInstance>();
      if(room==null||root==null)return result;
      var queue=new Queue<AssetInstance>();
      if(room.InstancesByParentId.TryGetValue(root.ID,out List<AssetInstance> first))foreach(AssetInstance child in first)queue.Enqueue(child);
      var seen=new HashSet<ulong>{root.ID};
      while(queue.Count>0){
        AssetInstance child=queue.Dequeue();if(child==null||!seen.Add(child.ID))continue;
        result.Add(child);
        if(room.InstancesByParentId.TryGetValue(child.ID,out List<AssetInstance> nested))foreach(AssetInstance next in nested)queue.Enqueue(next);
      }
      return result;
    }

    private void UpdatePathFollowers(float elapsedSeconds){
      if(pathFollowers.Count==0)return;
      foreach(PathFollowerRuntime follower in pathFollowers){
        if(follower?.Root==null||follower.Room==null||follower.Route==null)continue;
        if(!TryPathFollowerPose(follower.Route,elapsedSeconds,follower.Speed,out Vector3 position,out Vector3 rotation,out Vector3 direction))continue;
        Matrix rootWorld=PathFollowerMatrix(position,rotation,direction);
        instanceWorldTransforms[follower.Root]=rootWorld;
        UpdatePathFollowerChildren(follower.Room,follower.Root,rootWorld);
      }
      RefreshAnimatedRenderEntries();
    }

    private void UpdatePathFollowerChildren(Room room,AssetInstance parent,Matrix parentWorld){
      if(room==null||parent==null||!room.InstancesByParentId.TryGetValue(parent.ID,out List<AssetInstance> children))return;
      foreach(AssetInstance child in children){
        if(child==null)continue;
        Matrix world=child.transformMatrix*parentWorld;
        instanceWorldTransforms[child]=world;
        UpdatePathFollowerChildren(room,child,world);
      }
    }

    private void RefreshAnimatedRenderEntries(){
      foreach(RenderEntry entry in renderGlobal){
        AssetInstance inst=entry?.Instance;if(inst==null||!inst.PathFollowerAnimated)continue;
        Matrix world=InstanceWorld(inst,entry.Room);entry.World=world;
        if(entry.Kind==RenderKindModel&&entry.Model!=null){
          if(TryModelSphere(entry.Model,world,out Vector3 center,out float radius)){entry.Center=center;entry.Radius=radius;}
          else {entry.Center=new Vector3(world.M41,world.M42,world.M43);entry.Radius=0f;}
        }else if(entry.Kind==RenderKindWater){
          entry.Center=Vector3.TransformCoordinate(Vector3.Zero,world);
          entry.Radius=.5f*(float)Math.Sqrt(Math.Max(0f,inst.width*inst.width+inst.height*inst.height+inst.depth*inst.depth))*MatrixMaxScale(world);
        }
      }
    }

    private static PathFollowerRoute BuildPathFollowerRoute(AreaPath path){
      var route=new PathFollowerRoute();
      List<PathFollowerPoint> points=FlattenPathFollowerPoints(path);
      route.Points.AddRange(points);
      int segmentCount=Math.Max(0,points.Count-1)+(path!=null&&path.Circular&&points.Count>1?1:0);
      float length=0f;
      for(int i=0;i<segmentCount;i++){
        PathFollowerPoint from=points[i],to=points[(i+1)%points.Count];Vector3 delta=to.Position-from.Position;float segmentLength=delta.Length();
        if(segmentLength<=.000001f)continue;Vector3 direction=delta/segmentLength;
        route.Segments.Add(new PathFollowerSegment{From=from,To=to,Direction=direction,Start=length,Length=segmentLength});length+=segmentLength;
      }
      route.Length=length;return route;
    }

    private static List<PathFollowerPoint> FlattenPathFollowerPoints(AreaPath path){
      var result=new List<PathFollowerPoint>();if(path==null||path.Points==null||path.Points.Count==0)return result;
      List<AreaPathPoint> points=path.Points;
      if(!path.Smooth||points.Count<3){
        foreach(AreaPathPoint point in points)result.Add(new PathFollowerPoint{Position=point.Position,Rotation=point.Rotation});
        if(path.Circular&&points.Count>1)result.Add(new PathFollowerPoint{Position=points[0].Position,Rotation=points[0].Rotation});
        return result;
      }

      Vector3[] tangents=new Vector3[points.Count];for(int i=0;i<points.Count;i++)tangents[i]=PathCurveTangent(points,i,path.Circular);
      result.Add(new PathFollowerPoint{Position=points[0].Position,Rotation=points[0].Rotation,Direction=NormalizeOrFallback(tangents[0],new Vector3(0,0,1))});
      int segmentCount=points.Count-1+(path.Circular?1:0);
      for(int i=0;i<segmentCount;i++){
        int next=(i+1)%points.Count;Vector3 a=points[i].Position,b=points[next].Position;int steps=PathCurveSteps(a,b,tangents[i],tangents[next]);
        for(int step=1;step<steps;step++){
          float t=step/(float)steps;Vector3 dir=PathHermiteDirection(a,b,tangents[i],tangents[next],t);
          result.Add(new PathFollowerPoint{Position=PathHermite(a,b,tangents[i],tangents[next],t),Rotation=Vector3.Lerp(points[i].Rotation,points[next].Rotation,t),Direction=NormalizeOrFallback(dir,b-a)});
        }
        result.Add(new PathFollowerPoint{Position=points[next].Position,Rotation=points[next].Rotation,Direction=NormalizeOrFallback(tangents[next],b-a)});
      }
      return result;
    }

    private static Vector3 PathCurveTangent(List<AreaPathPoint> points,int index,bool circular){
      int previous=circular?(index-1+points.Count)%points.Count:Math.Max(index-1,0);int next=circular?(index+1)%points.Count:Math.Min(index+1,points.Count-1);
      return (points[next].Position-points[previous].Position)*points[index].Tension;
    }

    private static Vector3 PathHermite(Vector3 a,Vector3 b,Vector3 ta,Vector3 tb,float t){
      float t2=t*t,t3=t2*t,h00=2*t3-3*t2+1,h10=t3-2*t2+t,h01=-2*t3+3*t2,h11=t3-t2;
      return a*h00+ta*h10+b*h01+tb*h11;
    }

    private static Vector3 PathHermiteDirection(Vector3 a,Vector3 b,Vector3 ta,Vector3 tb,float t){
      float t2=t*t,h00=6*t2-6*t,h10=3*t2-4*t+1,h01=-h00,h11=3*t2-2*t;
      return a*h00+ta*h10+b*h01+tb*h11;
    }

    private static float PathCurveAngle(Vector3 a,Vector3 b){
      float la=a.Length(),lb=b.Length();if(la<=.000001f||lb<=.000001f)return 0f;float c=Vector3.Dot(a,b)/(la*lb);c=Math.Max(-1f,Math.Min(1f,c));return (float)Math.Acos(c);
    }

    private static float PathCurveWorstTurn(Vector3 a,Vector3 b,Vector3 ta,Vector3 tb,int steps){
      Vector3 previous=a,incoming=ta;float worst=0f;
      for(int step=1;step<=steps;step++){
        Vector3 current=PathHermite(a,b,ta,tb,step/(float)steps),edge=current-previous;worst=Math.Max(worst,PathCurveAngle(incoming,edge));incoming=edge;previous=current;
      }
      return Math.Max(worst,PathCurveAngle(incoming,tb));
    }

    private static int PathCurveSteps(Vector3 a,Vector3 b,Vector3 ta,Vector3 tb){
      const float toleranceFloor=.03f,relativeTolerance=.02f,maxTolerance=.1f,maxTurn=(float)(12.0*Math.PI/180.0);const int maxSteps=24;
      Vector3 chord=b-a;float chordLengthSq=chord.LengthSquared(),deviation=0f;float[] probes={.25f,.5f,.75f};
      foreach(float t in probes){
        Vector3 sample=PathHermite(a,b,ta,tb,t),along=sample-a;float projection=chordLengthSq>0f?Vector3.Dot(along,chord)/chordLengthSq:0f;deviation=Math.Max(deviation,(along-chord*projection).Length());
      }
      float turn=PathCurveAngle(ta,chord)+PathCurveAngle(chord,tb),chordLength=(float)Math.Sqrt(chordLengthSq),tolerance=Math.Min(Math.Max(toleranceFloor,chordLength*relativeTolerance),maxTolerance);
      int sagSteps=deviation<=tolerance?1:(int)Math.Ceiling(2*Math.Sqrt(deviation/tolerance));int steps=Math.Min(maxSteps,Math.Max(1,Math.Max(sagSteps,(int)Math.Ceiling(turn/maxTurn))));
      while(steps<maxSteps&&PathCurveWorstTurn(a,b,ta,tb,steps)>maxTurn)steps=Math.Min(maxSteps,steps*2);return steps;
    }

    private static Vector3 NormalizeOrFallback(Vector3 inputVector,Vector3 fallback){
      if(inputVector.LengthSquared()>.0000001f){inputVector.Normalize();return inputVector;}if(fallback.LengthSquared()>.0000001f){fallback.Normalize();return fallback;}return new Vector3(0,0,1);
    }

    private static bool TryPathFollowerPose(PathFollowerRoute route,float elapsedSeconds,float speed,out Vector3 position,out Vector3 rotation,out Vector3 direction){
      position=Vector3.Zero;rotation=Vector3.Zero;direction=new Vector3(0,0,1);if(route==null||route.Points.Count==0)return false;
      if(route.Length<=.000001f||speed<=0f){PathFollowerPoint first=route.Points[0];position=first.Position;rotation=first.Rotation;direction=NormalizeOrFallback(first.Direction,new Vector3(0,0,1));return true;}
      float distance=(elapsedSeconds*speed*10f)%route.Length;if(distance<0f)distance+=route.Length;PathFollowerSegment segment=route.Segments[route.Segments.Count-1];
      foreach(PathFollowerSegment candidate in route.Segments)if(distance<candidate.Start+candidate.Length){segment=candidate;break;}
      float t=(distance-segment.Start)/segment.Length;position=Vector3.Lerp(segment.From.Position,segment.To.Position,t);rotation=Vector3.Lerp(segment.From.Rotation,segment.To.Rotation,t);
      direction=segment.Direction;
      if(segment.From.Direction.LengthSquared()>.0000001f&&segment.To.Direction.LengthSquared()>.0000001f)direction=NormalizeOrFallback(Vector3.Lerp(segment.From.Direction,segment.To.Direction,t),segment.Direction);
      return true;
    }

    private static Matrix PathFollowerMatrix(Vector3 position,Vector3 rotation,Vector3 forward){
      forward=NormalizeOrFallback(forward,new Vector3(0,0,1));
      float sx=(float)Math.Sin(rotation.X),cx=(float)Math.Cos(rotation.X),sy=(float)Math.Sin(rotation.Y),cy=(float)Math.Cos(rotation.Y),sz=(float)Math.Sin(rotation.Z),cz=(float)Math.Cos(rotation.Z);
      Vector3 up=new Vector3(-cy*sz+sy*sx*cz,cx*cz,sy*sz+cy*sx*cz);up-=forward*Vector3.Dot(up,forward);
      if(up.LengthSquared()<.0000001f)up=Math.Abs(forward.Y)>.999f?new Vector3(1,0,0):new Vector3(0,1,0);up.Normalize();
      Vector3 right=Vector3.Cross(up,forward);if(right.LengthSquared()<.0000001f)right=new Vector3(1,0,0);else right.Normalize();up=Vector3.Cross(forward,right);up.Normalize();
      Matrix result=Matrix.Identity;
      // gl-matrix stores right/up/forward as columns; the DirectX row-vector equivalent is its transpose.
      result.M11=right.X;result.M12=right.Y;result.M13=right.Z;
      result.M21=up.X;result.M22=up.Y;result.M23=up.Z;
      result.M31=forward.X;result.M32=forward.Y;result.M33=forward.Z;
      result.M41=position.X;result.M42=position.Y;result.M43=position.Z;
      return result;
    }

    private static bool InstanceVisibleInWorld(AssetInstance inst,WorldRenderSettings s=null){
      if(inst==null||inst.PathFollowerPending)return false;
      // Hidden/MAP_ONLY/OCCLUDER_ONLY placements are an explicit opt-in through the Utilities submenu. There is no
      // second top-level display tier anymore: the submenu checkbox is the single source of truth.
      bool showHidden=s?.ShowHiddenGeometry==true;
      if(inst.hidden&&!showHidden)return false;
      // OCCLUDER_ONLY is solver/helper geometry, never a visible world surface. Rendering it in the colour pass can
      // turn authored doorway blockers into opaque black walls. Keep it out even when hidden helpers are enabled;
      // the Utilities overlay/wireframe diagnostics remain the place to inspect those shapes.
      if(inst.Viewability==AssetInstanceViewability.OccluderOnly&&!(showHidden&&s?.Mode==WorldRenderMode.Wireframe))return false;
      if(!showHidden&&inst.Viewability==AssetInstanceViewability.MapOnly)return false;
      return true;
    }

    private static bool InstanceCanEnterRenderIndex(AssetInstance inst)=>inst!=null&&!inst.PathFollowerPending;

    private static bool InstanceVisibleOnMap(AssetInstance inst){
      if(inst==null||inst.hidden||inst.PathFollowerPending)return false;
      return inst.Viewability!=AssetInstanceViewability.WorldOnly&&inst.Viewability!=AssetInstanceViewability.OccluderOnly;
    }

    private void PrimeRoomVisibilityBounds(){
      // Jedipedia runs dpvsPrimeRenderableRoomBounds only after its first-pass GR2 queue is complete. In v6 a
      // streamed room has none/only some of those models here; committing a partial heightmap/water box as final
      // visibility bounds can select the wrong overlapping room and exclude the actual hangar. Streaming uses its
      // separate conservative placement bounds until TryFinalizeStreamedRoomVisibilityBounds() has all room GR2s.
      if(modelStreamer!=null)return;
      // Jedipedia's dpvsPrimeRenderableRoomBounds() repairs rooms whose authored visibility box is the common
      // all-zero placeholder. Without this fallback a perfectly valid interior can never win FindBoundsRoom(), so
      // the camera appears to remain in the exterior room (or `_everywhere_`) whenever its collision floor has a gap.
      foreach(Room room in rooms){
        if(room==null||IsEverywhereRoom(room)||skyRoomNames.Contains(room.RoomName)||HasUsableRoomBounds(room))continue;
        Vector3 min=new Vector3(float.MaxValue,float.MaxValue,float.MaxValue),max=new Vector3(float.MinValue,float.MinValue,float.MinValue);bool any=false;
        foreach(AssetInstance inst in room.InstancesById.Values){
          // Jedipedia dPVS uses every static renderable placement to prime a room boundary, including authored
          // hidden/MAP_ONLY/OCCLUDER_ONLY instances. Those flags control drawing, not spatial membership.
          if(inst==null||inst.PathFollowerBoundaryExcluded)continue;Vector3 localMin,localMax;bool have=false;
          if(inst.hasHeightMap&&inst.HeightMap!=null){
            HeightMap hm=inst.HeightMap;int w=checked((int)hm.width),d=checked((int)hm.depth);
            float xmin=-.2f*(float)Math.Ceiling(.5f*(w-1)),zmin=-.2f*(float)Math.Ceiling(.5f*(d-1));
            localMin=new Vector3(xmin,hm.MinElevation,zmin);localMax=new Vector3(xmin+.2f*(w-1),hm.MaxElevation,zmin+.2f*(d-1));have=true;
          }else if(models.TryGetValue(inst.assetID,out GR2 model)&&model?.globalBox!=null){
            GR2_Bounding_Box box=model.globalBox;localMin=new Vector3(box.minX,box.minY,box.minZ);localMax=new Vector3(box.maxX,box.maxY,box.maxZ);
            have=IsFinite(localMin)&&IsFinite(localMax)&&localMin.X<=localMax.X&&localMin.Y<=localMax.Y&&localMin.Z<=localMax.Z;
          }else if(inst.hasWater){
            float hw=Math.Max(.01f,Math.Abs(inst.width)*.5f),hd=Math.Max(.01f,Math.Abs(inst.depth)*.5f),hh=inst.HasHeightProperty?Math.Max(.01f,Math.Abs(inst.height)*.5f):.05f;
            localMin=new Vector3(-hw,-hh,-hd);localMax=new Vector3(hw,hh,hd);have=true;
          }else continue;
          if(!have)continue;Matrix world=InstanceWorld(inst,room);
          for(int z=0;z<2;z++)for(int y=0;y<2;y++)for(int x=0;x<2;x++){
            Vector3 p=Vector3.TransformCoordinate(new Vector3(x==0?localMin.X:localMax.X,y==0?localMin.Y:localMax.Y,z==0?localMin.Z:localMax.Z),world);
            if(!IsFinite(p))continue;Expand(ref min,ref max,p);any=true;
          }
        }
        if(any&&min.X<=max.X&&min.Y<=max.Y&&min.Z<=max.Z)room.SetComputedVisibilityBounds(min,max);
      }
    }

    private void BuildRoomStreamingBounds(){
      roomStreamingBounds.Clear();streamRoomsNeedingExactBounds.Clear();
      if(rooms==null)return;
      foreach(Room room in rooms){
        if(room==null||IsEverywhereRoom(room)||skyRoomNames.Contains(room.RoomName)||String.IsNullOrWhiteSpace(room.RoomName))continue;
        if(HasUsableRoomBounds(room)){
          roomStreamingBounds[room.RoomName]=new RoomStreamingBounds{Min=room.VisibilityMin,Max=room.VisibilityMax,Center=room.VisibilityCenter,Radius=Math.Max(.01f,room.VisibilityRadius),Coarse=false};
          continue;
        }
        streamRoomsNeedingExactBounds.Add(room.RoomName);
        Vector3 min=new Vector3(float.MaxValue,float.MaxValue,float.MaxValue),max=new Vector3(float.MinValue,float.MinValue,float.MinValue);bool any=false;int count=0;
        // Use the streaming catalog's placement origins, including authored hidden placements. Visibility is a draw
        // decision; excluding those origins here can erase the only useful bootstrap hint for phased/interior shells.
        if(streamRoomAssetHints.TryGetValue(room.RoomName,out Dictionary<ulong,StreamRoomAssetHint> hints))foreach(StreamRoomAssetHint hint in hints.Values){
          if(hint==null||!hint.Any||!IsFinite(hint.Min)||!IsFinite(hint.Max))continue;Expand(ref min,ref max,hint.Min);Expand(ref min,ref max,hint.Max);any=true;count++;
        }
        // Terrain/water-only rooms have no normal GR2 request. Their placement origins still make a useful fallback.
        if(!any&&room.InstancesById!=null)foreach(AssetInstance inst in room.InstancesById.Values){
          if(inst==null||inst.PathFollowerBoundaryExcluded||(!inst.hasHeightMap&&!inst.hasWater))continue;Matrix world=InstanceWorld(inst,room);Vector3 p=new Vector3(world.M41,world.M42,world.M43);if(!IsFinite(p))continue;Expand(ref min,ref max,p);any=true;count++;
        }
        if(!any)continue;
        // A single room-shell GR2 is frequently authored with its origin near an edge rather than at the centre.
        // Use a deliberately conservative streaming-only pad; multiple placements already describe room extent and
        // therefore need less extra reach. This never changes render culling or the authoritative camera-room result.
        float horizontalPad=count<=2?112f:Math.Max(48f,Math.Min(96f,Math.Max(max.X-min.X,max.Z-min.Z)*.20f+32f));
        float verticalPad=count<=2?48f:Math.Max(24f,Math.Min(64f,(max.Y-min.Y)*.20f+16f));
        min-=new Vector3(horizontalPad,verticalPad,horizontalPad);max+=new Vector3(horizontalPad,verticalPad,horizontalPad);
        Vector3 center=(min+max)*.5f;float radius=(max-min).Length()*.5f;
        roomStreamingBounds[room.RoomName]=new RoomStreamingBounds{Min=min,Max=max,Center=center,Radius=Math.Max(.01f,radius),Coarse=true};
      }
    }

    private bool TryComputeStreamedRoomVisibilityBounds(Room room,out Vector3 min,out Vector3 max){
      min=new Vector3(float.MaxValue,float.MaxValue,float.MaxValue);max=new Vector3(float.MinValue,float.MinValue,float.MinValue);bool any=false;
      if(room?.InstancesById==null)return false;
      foreach(AssetInstance inst in room.InstancesById.Values){
        // Match dpvsPrimeRenderableRoomBounds(): hidden/viewability does not remove an authored placement from the
        // room's spatial shell. Otherwise phased Corellia interiors can collapse to an incomplete box.
        if(inst==null||inst.PathFollowerBoundaryExcluded)continue;Vector3 localMin,localMax;bool have=false;
        if(inst.hasHeightMap&&inst.HeightMap!=null){
          HeightMap hm=inst.HeightMap;int w=checked((int)hm.width),d=checked((int)hm.depth);float xmin=-.2f*(float)Math.Ceiling(.5f*(w-1)),zmin=-.2f*(float)Math.Ceiling(.5f*(d-1));
          localMin=new Vector3(xmin,hm.MinElevation,zmin);localMax=new Vector3(xmin+.2f*(w-1),hm.MaxElevation,zmin+.2f*(d-1));have=true;
        }else if(models.TryGetValue(inst.assetID,out GR2 model)&&model?.globalBox!=null){
          GR2_Bounding_Box box=model.globalBox;localMin=new Vector3(box.minX,box.minY,box.minZ);localMax=new Vector3(box.maxX,box.maxY,box.maxZ);have=IsFinite(localMin)&&IsFinite(localMax)&&localMin.X<=localMax.X&&localMin.Y<=localMax.Y&&localMin.Z<=localMax.Z;
        }else if(inst.hasWater){
          float hw=Math.Max(.01f,Math.Abs(inst.width)*.5f),hd=Math.Max(.01f,Math.Abs(inst.depth)*.5f),hh=inst.HasHeightProperty?Math.Max(.01f,Math.Abs(inst.height)*.5f):.05f;
          localMin=new Vector3(-hw,-hh,-hd);localMax=new Vector3(hw,hh,hd);have=true;
        }else continue;
        if(!have)continue;Matrix world=InstanceWorld(inst,room);
        for(int z=0;z<2;z++)for(int y=0;y<2;y++)for(int x=0;x<2;x++){
          Vector3 p=Vector3.TransformCoordinate(new Vector3(x==0?localMin.X:localMax.X,y==0?localMin.Y:localMax.Y,z==0?localMin.Z:localMax.Z),world);
          if(!IsFinite(p))continue;Expand(ref min,ref max,p);any=true;
        }
      }
      return any&&min.X<=max.X&&min.Y<=max.Y&&min.Z<=max.Z;
    }

    private void TryFinalizeStreamedRoomVisibilityBounds(string roomName){
      if(modelStreamer==null||String.IsNullOrWhiteSpace(roomName)||!streamRoomsNeedingExactBounds.Contains(roomName))return;
      if(streamRequestsByRoom.TryGetValue(roomName,out List<WorldModelStreamRequest> requests)){
        foreach(WorldModelStreamRequest request in requests)if(request!=null&&!models.ContainsKey(request.AssetId)&&!modelStreamer.IsFailed(request.AssetId))return;
      }
      Room room=rooms.FirstOrDefault(r=>r!=null&&String.Equals(r.RoomName,roomName,StringComparison.OrdinalIgnoreCase));if(room==null){streamRoomsNeedingExactBounds.Remove(roomName);return;}
      if(!TryComputeStreamedRoomVisibilityBounds(room,out Vector3 min,out Vector3 max))return;
      room.SetComputedVisibilityBounds(min,max);roomStreamingBounds[room.RoomName]=new RoomStreamingBounds{Min=min,Max=max,Center=(min+max)*.5f,Radius=Math.Max(.01f,(max-min).Length()*.5f),Coarse=false};
      streamRoomsNeedingExactBounds.Remove(roomName);
      RefreshPhysicalStreamingNeighbors(room);
    }

    private void RefreshPhysicalStreamingNeighbors(Room room){
      // BuildRoomStreamingGraph() initially runs before v6 has GR2-derived bounds. When a Corellia outdoor cell later
      // gets its exact shell, add newly discovered touching cells instead of keeping the origin-box adjacency forever.
      // Only additions are needed: stale conservative neighbours cost a little prefetch but cannot create a hole.
      if(room==null||!room.OutdoorsVisible||IsEverywhereRoom(room)||skyRoomNames.Contains(room.RoomName)||!TryGetRoomStreamingBounds(room,out RoomStreamingBounds rb))return;
      const float maxGap=32f,maxVerticalGap=32f;float maxGapSq=maxGap*maxGap;
      var nearby=new List<(Room Room,float Gap)>();
      foreach(Room candidate in rooms){
        if(candidate==null||ReferenceEquals(room,candidate)||!candidate.OutdoorsVisible||IsEverywhereRoom(candidate)||skyRoomNames.Contains(candidate.RoomName)||!TryGetRoomStreamingBounds(candidate,out RoomStreamingBounds cb))continue;
        float dy=AxisGap(rb.Min.Y,rb.Max.Y,cb.Min.Y,cb.Max.Y);if(dy>maxVerticalGap)continue;
        float dx=AxisGap(rb.Min.X,rb.Max.X,cb.Min.X,cb.Max.X),dz=AxisGap(rb.Min.Z,rb.Max.Z,cb.Min.Z,cb.Max.Z),gap=dx*dx+dz*dz;if(gap<=maxGapSq)nearby.Add((candidate,gap));
      }
      foreach(var item in nearby.OrderBy(x=>x.Gap).Take(8)){
        if(!roomStreamingNeighbors.TryGetValue(room.RoomName,out HashSet<string> a))roomStreamingNeighbors[room.RoomName]=a=new HashSet<string>(StringComparer.OrdinalIgnoreCase);a.Add(item.Room.RoomName);
        if(!roomStreamingNeighbors.TryGetValue(item.Room.RoomName,out HashSet<string> b))roomStreamingNeighbors[item.Room.RoomName]=b=new HashSet<string>(StringComparer.OrdinalIgnoreCase);b.Add(room.RoomName);
      }
    }

    private void FinalizeReadyStreamRoomVisibilityBounds(){
      if(modelStreamer==null||streamRoomsNeedingExactBounds.Count==0)return;
      var candidates=new HashSet<string>(activeStreamRoomNames,StringComparer.OrdinalIgnoreCase);candidates.UnionWith(initialStreamRoomNames);candidates.UnionWith(floorStreamRoomNames);
      foreach(string roomName in candidates.ToArray())TryFinalizeStreamedRoomVisibilityBounds(roomName);
    }

    private bool TryGetRoomStreamingBounds(Room room,out RoomStreamingBounds bounds){
      bounds=null;if(room==null||String.IsNullOrWhiteSpace(room.RoomName))return false;
      return roomStreamingBounds.TryGetValue(room.RoomName,out bounds)&&bounds!=null&&IsFinite(bounds.Min)&&IsFinite(bounds.Max)&&bounds.Max.X>=bounds.Min.X&&bounds.Max.Y>=bounds.Min.Y&&bounds.Max.Z>=bounds.Min.Z;
    }

    private static bool ContainsStreamingBounds(RoomStreamingBounds bounds,Vector3 p,float margin){
      return bounds!=null&&p.X>=bounds.Min.X-margin&&p.X<=bounds.Max.X+margin&&p.Y>=bounds.Min.Y-margin&&p.Y<=bounds.Max.Y+margin&&p.Z>=bounds.Min.Z-margin&&p.Z<=bounds.Max.Z+margin;
    }

    private static float StreamingBoundsGapSquared(RoomStreamingBounds a,RoomStreamingBounds b){
      if(a==null||b==null)return float.MaxValue;
      float dx=AxisGap(a.Min.X,a.Max.X,b.Min.X,b.Max.X),dy=AxisGap(a.Min.Y,a.Max.Y,b.Min.Y,b.Max.Y),dz=AxisGap(a.Min.Z,a.Max.Z,b.Min.Z,b.Max.Z);
      return dx*dx+dy*dy+dz*dz;
    }

    private float StreamingBoundsDistanceSquared(RoomStreamingBounds bounds,Vector3 p){
      if(bounds==null)return float.MaxValue;
      float dx=p.X<bounds.Min.X?bounds.Min.X-p.X:(p.X>bounds.Max.X?p.X-bounds.Max.X:0f);
      float dy=p.Y<bounds.Min.Y?bounds.Min.Y-p.Y:(p.Y>bounds.Max.Y?p.Y-bounds.Max.Y:0f);
      float dz=p.Z<bounds.Min.Z?bounds.Min.Z-p.Z:(p.Z>bounds.Max.Z?p.Z-bounds.Max.Z:0f);
      return dx*dx+dy*dy+dz*dz;
    }

    private Room FindStreamingRoom(Vector3 p,float maxFallbackDistance=220f){
      Room best=null;float bestScore=float.MaxValue;
      foreach(Room room in rooms){
        if(room==null||IsEverywhereRoom(room)||skyRoomNames.Contains(room.RoomName)||!TryGetRoomStreamingBounds(room,out RoomStreamingBounds bounds))continue;
        if(!ContainsStreamingBounds(bounds,p,.5f))continue;
        // Prefer a tighter/interior box when conservative boxes overlap.
        float score=bounds.Radius+(room.OutdoorsVisible?100000f:0f);if(best==null||score<bestScore){best=room;bestScore=score;}
      }
      if(best!=null)return best;
      float maxSq=maxFallbackDistance*maxFallbackDistance;bestScore=maxSq;
      foreach(Room room in rooms){
        if(room==null||IsEverywhereRoom(room)||skyRoomNames.Contains(room.RoomName)||!TryGetRoomStreamingBounds(room,out RoomStreamingBounds bounds))continue;
        float distance=StreamingBoundsDistanceSquared(bounds,p);if(distance<bestScore){best=room;bestScore=distance;}
      }
      return best;
    }

    private Room ResolveStreamingRoom(Vector3 p,Room authoritative){
      Room coarse=FindStreamingRoom(p);
      if(authoritative!=null&&!IsEverywhereRoom(authoritative)&&!skyRoomNames.Contains(authoritative.RoomName)){
        // Before streamed collision floors exist an outdoor heightmap can legitimately win the authoritative lookup
        // even while the spawn sits inside an interior hangar above it. For demand planning only, prefer an overlapping
        // interior coarse room; FindCameraRoom itself remains untouched and takes over as soon as its floor arrives.
        if(coarse!=null&&!ReferenceEquals(coarse,authoritative)&&!coarse.OutdoorsVisible&&authoritative.OutdoorsVisible)return coarse;
        return authoritative;
      }
      return coarse??authoritative;
    }

    private void BuildHeightMapFloorIndex(){
      heightMapFloorGrid.Clear();heightMapFloors.Clear();
      foreach(Room room in rooms){
        if(room==null||IsEverywhereRoom(room)||skyRoomNames.Contains(room.RoomName))continue;
        foreach(AssetInstance inst in room.InstancesById.Values){
          if(inst==null||inst.PathFollowerBoundaryExcluded||!inst.hasHeightMap)continue;HeightMap hm=inst.HeightMap;if(hm==null||hm.width<2||hm.depth<2)continue;
          Matrix world=InstanceWorld(inst,room);Matrix inv;
          try{inv=Matrix.Invert(world);}catch{continue;}
          int w=(int)hm.width,d=(int)hm.depth;
          float xmin=-.2f*(float)Math.Ceiling(.5f*(w-1));float zmin=-.2f*(float)Math.Ceiling(.5f*(d-1));
          float xmax=xmin+.2f*(w-1),zmax=zmin+.2f*(d-1);
          float minX=float.MaxValue,maxX=float.MinValue,minZ=float.MaxValue,maxZ=float.MinValue;
          float low=hm.MinElevation,high=hm.MaxElevation;
          for(int yi=0;yi<2;yi++)for(int zi=0;zi<2;zi++)for(int xi=0;xi<2;xi++){
            Vector3 c=Vector3.TransformCoordinate(new Vector3(xi==0?xmin:xmax,yi==0?low:high,zi==0?zmin:zmax),world);
            minX=Math.Min(minX,c.X);maxX=Math.Max(maxX,c.X);minZ=Math.Min(minZ,c.Z);maxZ=Math.Max(maxZ,c.Z);
          }
          var entry=new HeightMapFloorEntry{Room=room,HeightMap=hm,World=world,Inverse=inv,XMin=xmin,ZMin=zmin,WorldMinX=minX,WorldMaxX=maxX,WorldMinZ=minZ,WorldMaxZ=maxZ};
          heightMapFloors.Add(entry);
          int cminX=FloorCell(minX),cmaxX=FloorCell(maxX),cminZ=FloorCell(minZ),cmaxZ=FloorCell(maxZ);
          for(int z=cminZ;z<=cmaxZ;z++)for(int x=cminX;x<=cmaxX;x++){
            var key=(x,z);if(!heightMapFloorGrid.TryGetValue(key,out List<HeightMapFloorEntry> bucket))heightMapFloorGrid[key]=bucket=new List<HeightMapFloorEntry>();bucket.Add(entry);
          }
        }
      }
    }
    private static int FloorCell(float coordinate)=>(int)Math.Floor(coordinate/FloorCellSize);

    private void BuildRoomPlacementIndex(){
      roomPlacementGrid.Clear();roomPlacementGlobal.Clear();if(area==null)return;
      foreach(Room room in rooms){
        if(room==null||IsEverywhereRoom(room)||skyRoomNames.Contains(room.RoomName))continue;
        foreach(AssetInstance inst in room.InstancesById.Values){
          // dPVS uses authored RoomBound/Region/Trigger/Heightmap placements for spatial membership even when the
          // placement is hidden or MAP_ONLY/OCCLUDER_ONLY. Those flags are render policy, not room topology.
          if(inst==null||inst.PathFollowerBoundaryExcluded||!area.AssetIdMap.TryGetValue(inst.assetID,out AreaAsset asset))continue;
          string ext=(asset.Extension??String.Empty).Trim().TrimStart('.').ToLowerInvariant();int rank;
          // Jedipedia keeps Heightmap placements in addition to the exact floor lattice. A heightmap placement is
          // an infinite vertical X/Z prism when Height was not authored, which is why flying high above a planet
          // still has a real camera room instead of `_everywhere_`.
          if(inst.hasHeightMap)rank=0;else if(ext=="rbd")rank=1;else if(ext=="rgn")rank=2;else if(ext=="trg")rank=3;else continue;
          RegionVolumeData region=ext=="rgn"?inst.RegionVolume:null;
          float width=Math.Abs(inst.width),depth=Math.Abs(inst.depth),height=Math.Abs(inst.height);bool hasHeight=inst.HasHeightProperty&&height>.0001f;
          // A real .rgn is not a Width/Depth box at all. Its rgnVolumeData footprint can be concave and each
          // vertex has its own ceiling height; keep the simple dimensions only as a fallback for old/partial DATs.
          if(region==null&&(!(width>.0001f)||!(depth>.0001f)))continue;
          Matrix world=InstanceWorld(inst,room),inverse;try{inverse=Matrix.Invert(world);}catch{continue;}
          string placementAssetPath=asset.Path+(String.IsNullOrWhiteSpace(asset.Extension)?String.Empty:"."+asset.Extension.TrimStart('.'));
          var entry=new RoomPlacementEntry{Room=room,Instance=inst,AssetPath=placementAssetPath,Inverse=inverse,Width=width,Depth=depth,Height=height,HasHeight=hasHeight,Rank=rank,RegionVolume=region};
          float halfW=width*.5f,halfD=depth*.5f,halfH=hasHeight?height*.5f:0f;
          // With no authored height the legacy box is an infinite local-Y prism. If local Y rotates into world X/Z,
          // there is no finite 2D index box; keep this rare placement in the global fallback exactly rather than
          // risking a false negative. Real region meshes always have finite local bounds.
          if(region==null&&!hasHeight&&(Math.Abs(world.M21)>.000001f||Math.Abs(world.M23)>.000001f)){roomPlacementGlobal.Add(entry);continue;}
          Vector3 min=new Vector3(float.MaxValue,float.MaxValue,float.MaxValue),max=new Vector3(float.MinValue,float.MinValue,float.MinValue);
          Vector3 localMin=region!=null?region.Min:new Vector3(-halfW,-halfH,-halfD);
          Vector3 localMax=region!=null?region.Max:new Vector3(halfW,halfH,halfD);
          for(int z=0;z<2;z++)for(int y=0;y<2;y++)for(int x=0;x<2;x++){
            Vector3 c=Vector3.TransformCoordinate(new Vector3(x==0?localMin.X:localMax.X,y==0?localMin.Y:localMax.Y,z==0?localMin.Z:localMax.Z),world);Expand(ref min,ref max,c);
          }
          int minX=RoomPlacementCell(min.X),maxX=RoomPlacementCell(max.X),minZ=RoomPlacementCell(min.Z),maxZ=RoomPlacementCell(max.Z);
          long cells=(long)(maxX-minX+1)*(maxZ-minZ+1);
          if(cells<=0||cells>MaxRoomPlacementCells){roomPlacementGlobal.Add(entry);continue;}
          for(int z=minZ;z<=maxZ;z++)for(int x=minX;x<=maxX;x++){
            var key=(x,z);if(!roomPlacementGrid.TryGetValue(key,out List<RoomPlacementEntry> bucket))roomPlacementGrid[key]=bucket=new List<RoomPlacementEntry>();bucket.Add(entry);
          }
        }
      }
    }
    private void BuildVolumeMembershipIndex(){
      volumeMembershipGrid.Clear();volumeMembershipGlobal.Clear();volumeMembershipByKey.Clear();if(area==null)return;
      foreach(Room room in rooms){
        if(room?.InstancesById==null)continue;
        foreach(AssetInstance inst in room.InstancesById.Values){
          if(inst==null||!area.AssetIdMap.TryGetValue(inst.assetID,out AreaAsset asset)||asset==null)continue;
          string ext=(asset.Extension??String.Empty).Trim().TrimStart('.').ToLowerInvariant();
          bool isRegion=ext=="rgn",isTrigger=ext=="trg";if(!isRegion&&!isTrigger)continue;
          RegionVolumeData region=isRegion?inst.RegionVolume:null;
          if(isRegion&&region==null)continue; // Jedipedia never invents a Width/Depth box for a region.rgn.
          float width=Math.Abs(inst.width),height=Math.Abs(inst.height),depth=Math.Abs(inst.depth);
          if(isTrigger){
            // AssetInstance's 64-unit defaults are parser fallbacks, not authored trigger extents. A trigger with no
            // dimension property is therefore not a real membership volume and must not cover half the map.
            if(!inst.HasWidthProperty&&!inst.HasHeightProperty&&!inst.HasDepthProperty)continue;
            if(!(width>.0001f)||!(height>.0001f)||!(depth>.0001f))continue;
          }
          Matrix world=InstanceWorld(inst,room),inverse;try{inverse=Matrix.Invert(world);}catch{continue;}
          Vector3 localMin,localMax;
          if(isRegion){localMin=region.Min;localMax=region.Max;}
          else{localMin=new Vector3(-width*.5f,-height*.5f,-depth*.5f);localMax=new Vector3(width*.5f,height*.5f,depth*.5f);}
          string cls=isRegion?(String.IsNullOrWhiteSpace(inst.RegionClassType)?"GENERIC":inst.RegionClassType)
            :(String.IsNullOrWhiteSpace(inst.TriggerClassType)?"GENERIC":inst.TriggerClassType);
          string detail=isRegion
            ? (String.Equals(cls,"RESPAWN",StringComparison.OrdinalIgnoreCase)?inst.RegionRespawnMedCenter:RegionCharacteristicsSummary(inst.RegionCharacteristics))
            : (!String.IsNullOrWhiteSpace(inst.TriggerParam)?inst.TriggerParam:inst.TriggerTag);
          float determinant=world.M11*(world.M22*world.M33-world.M23*world.M32)-world.M12*(world.M21*world.M33-world.M23*world.M31)+world.M13*(world.M21*world.M32-world.M22*world.M31);
          Vector3 extent=localMax-localMin;float sortVolume=Math.Max(.000001f,Math.Abs(extent.X*extent.Y*extent.Z*determinant));
          string placementAssetPath=asset.Path+(String.IsNullOrWhiteSpace(asset.Extension)?String.Empty:"."+asset.Extension.TrimStart('.'));
          var entry=new VolumeMembershipEntry{Room=room,Instance=inst,AssetPath=placementAssetPath,Inverse=inverse,RegionVolume=region,IsRegion=isRegion,
            HalfWidth=width*.5f,HalfHeight=height*.5f,HalfDepth=depth*.5f,Ellipsoid=inst.TriggerEllipsoid,ClassType=cls,Detail=detail,SortVolume=sortVolume};
          volumeMembershipByKey[WorldVolumeKey(room?.RoomName,inst.ID)]=entry;
          Vector3 min=new Vector3(float.MaxValue,float.MaxValue,float.MaxValue),max=new Vector3(float.MinValue,float.MinValue,float.MinValue);
          for(int z=0;z<2;z++)for(int y=0;y<2;y++)for(int x=0;x<2;x++){
            Vector3 c=Vector3.TransformCoordinate(new Vector3(x==0?localMin.X:localMax.X,y==0?localMin.Y:localMax.Y,z==0?localMin.Z:localMax.Z),world);Expand(ref min,ref max,c);
          }
          int minX=RoomPlacementCell(min.X),maxX=RoomPlacementCell(max.X),minZ=RoomPlacementCell(min.Z),maxZ=RoomPlacementCell(max.Z);
          long cells=(long)(maxX-minX+1)*(maxZ-minZ+1);
          if(cells<=0||cells>MaxRoomPlacementCells){volumeMembershipGlobal.Add(entry);continue;}
          for(int z=minZ;z<=maxZ;z++)for(int x=minX;x<=maxX;x++){
            var key=(x,z);if(!volumeMembershipGrid.TryGetValue(key,out List<VolumeMembershipEntry> bucket))volumeMembershipGrid[key]=bucket=new List<VolumeMembershipEntry>();bucket.Add(entry);
          }
        }
      }
    }

    private static string RegionCharacteristicsSummary(string value){
      string text=(value??String.Empty).Trim();if(text.Length==0||text=="!!")return String.Empty;
      return String.Join(", ",text.Split(new[]{'!'},StringSplitOptions.RemoveEmptyEntries).Select(part=>part.Trim()).Where(part=>part.Length>0));
    }

    private static bool VolumeContainsPosition(VolumeMembershipEntry entry,Vector3 worldPosition){
      if(entry==null)return false;Vector3 local;try{local=Vector3.TransformCoordinate(worldPosition,entry.Inverse);}catch{return false;}
      if(entry.IsRegion)return RegionContainsLocalPosition(entry.RegionVolume,local);
      float x=Math.Abs(local.X)/Math.Max(.0001f,entry.HalfWidth),y=Math.Abs(local.Y)/Math.Max(.0001f,entry.HalfHeight),z=Math.Abs(local.Z)/Math.Max(.0001f,entry.HalfDepth);
      return entry.Ellipsoid?x*x+y*y+z*z<=1.0001f:x<=1.0001f&&y<=1.0001f&&z<=1.0001f;
    }

    private static int VolumeClassOrder(string value){
      string cls=(value??String.Empty).ToUpperInvariant();
      switch(cls){
        case "RESPAWN": return 0; case "MAP": return 1; case "AUDIO": case "AUDIO_REGION": return 2; case "GENERIC": return 3;
        case "INSTANCE_REGION": return 4; case "DEATH": case "DEATH_VOLUME": return 5; case "EXHAUSTION": case "EXHAUSTION_VOLUME": return 6;
        case "PLANETARY_WORLD_QUEST": case "PLANETARY_WORLD_QUEST_VOLUME": return 7; case "META_WORLD_QUEST": case "META_WORLD_QUEST_VOLUME": return 8;
        case "SHARED_WORLD_QUEST": case "SHARED_WORLD_QUEST_VOLUME": return 9; case "WORLD_QUEST": case "WORLD_QUEST_VOLUME": return 10; default:return 100;
      }
    }

    private static string WorldVolumeKey(string roomName,ulong instanceId)=>(roomName??String.Empty)+"#"+instanceId.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static WorldVolumeInfo ToWorldVolumeInfo(VolumeMembershipEntry entry){
      if(entry?.Instance==null)return null;
      string kind=entry.IsRegion?"REGION":"TRIGGER";string cls=String.IsNullOrWhiteSpace(entry.ClassType)?"GENERIC":entry.ClassType;
      string text=kind+" "+cls;if(!String.IsNullOrWhiteSpace(entry.Detail))text+=" — "+entry.Detail;
      text+=" — "+(entry.AssetPath??("instance "+entry.Instance.ID));if(!String.IsNullOrWhiteSpace(entry.Room?.RoomName))text+=" ["+entry.Room.RoomName+"]";
      return new WorldVolumeInfo{InstanceId=entry.Instance.ID,RoomName=entry.Room?.RoomName??String.Empty,Description=text,ClassType=cls,Detail=entry.Detail??String.Empty,AssetPath=entry.AssetPath??String.Empty,IsRegion=entry.IsRegion};
    }

    public List<WorldVolumeInfo> PinnedWorldVolumeInfos {
      get {
        var result=new List<WorldVolumeInfo>();
        lock(pinnedVolumeGpu){
          foreach(string key in pinnedVolumeGpuByKey.Keys){
            if(!volumeMembershipByKey.TryGetValue(key,out VolumeMembershipEntry entry))continue;
            WorldVolumeInfo info=ToWorldVolumeInfo(entry);if(info!=null)result.Add(info);
          }
        }
        return result;
      }
    }

    public List<WorldVolumeInfo> CurrentVolumeInfos(Vector3 worldPosition,int maxCount=32){
      var matches=new List<VolumeMembershipEntry>();var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      void Consider(IEnumerable<VolumeMembershipEntry> entries){
        if(entries==null)return;foreach(VolumeMembershipEntry entry in entries){
          if(entry?.Instance==null)continue;string key=(entry.Room?.RoomName??String.Empty)+"#"+entry.Instance.ID.ToString(System.Globalization.CultureInfo.InvariantCulture);if(!seen.Add(key))continue;
          if(VolumeContainsPosition(entry,worldPosition))matches.Add(entry);
        }
      }
      volumeMembershipGrid.TryGetValue((RoomPlacementCell(worldPosition.X),RoomPlacementCell(worldPosition.Z)),out List<VolumeMembershipEntry> bucket);Consider(bucket);Consider(volumeMembershipGlobal);
      matches.Sort((a,b)=>{int c=VolumeClassOrder(a.ClassType).CompareTo(VolumeClassOrder(b.ClassType));if(c!=0)return c;c=String.Compare(a.ClassType,b.ClassType,StringComparison.OrdinalIgnoreCase);if(c!=0)return c;c=a.SortVolume.CompareTo(b.SortVolume);if(c!=0)return c;return String.Compare(a.AssetPath,b.AssetPath,StringComparison.OrdinalIgnoreCase);});
      var result=new List<WorldVolumeInfo>();int limit=Math.Max(1,maxCount);
      foreach(VolumeMembershipEntry entry in matches){
        WorldVolumeInfo info=ToWorldVolumeInfo(entry);if(info!=null)result.Add(info);
        if(result.Count>=limit)break;
      }
      return result;
    }

    private static int RoomPlacementCell(float coordinate)=>(int)Math.Floor(coordinate/RoomPlacementCellSize);

    private static bool IsEverywhereRoom(Room room)=>room!=null&&string.Equals(room.RoomName,"_everywhere_",StringComparison.OrdinalIgnoreCase);

    private void BuildModelFloorIndex(){
      modelFloorData.Clear();modelFloorPlacementGrid.Clear();modelFloorPlacementGlobal.Clear();
      foreach(Room room in rooms){
        if(room==null||IsEverywhereRoom(room)||skyRoomNames.Contains(room.RoomName))continue;
        foreach(AssetInstance inst in room.InstancesById.Values){
          // Floor/camera-room membership is independent from draw visibility in Jedipedia. Hidden and map/occluder
          // placements can still carry the collision shell that says which room the camera occupies.
          if(inst==null||inst.PathFollowerBoundaryExcluded||inst.hasHeightMap||inst.hasWater||IsSpeedTreeInstance(inst)||!models.TryGetValue(inst.assetID,out GR2 model)||model==null)continue;
          ModelFloorData floor=GetOrBuildModelFloorData(model,inst);if(floor==null||floor.Meshes.Count==0)continue;
          Matrix world=InstanceWorld(inst,room),inverse;try{inverse=Matrix.Invert(world);}catch{continue;}
          if(!TryModelWorldXZBounds(model,world,out float minX,out float maxX,out float minZ,out float maxZ))continue;
          var entry=new ModelFloorPlacementEntry{Room=room,Model=floor,World=world,Inverse=inverse,WorldMinX=minX,WorldMaxX=maxX,WorldMinZ=minZ,WorldMaxZ=maxZ,LocalXZIndependentOfY=Math.Abs(inverse.M21)<.000001f&&Math.Abs(inverse.M23)<.000001f};
          int cminX=ModelFloorPlacementCell(minX),cmaxX=ModelFloorPlacementCell(maxX),cminZ=ModelFloorPlacementCell(minZ),cmaxZ=ModelFloorPlacementCell(maxZ);
          long cells=(long)(cmaxX-cminX+1)*(cmaxZ-cminZ+1);
          if(cells<=0||cells>MaxModelFloorPlacementCells){modelFloorPlacementGlobal.Add(entry);continue;}
          for(int z=cminZ;z<=cmaxZ;z++)for(int x=cminX;x<=cmaxX;x++){var key=(x,z);if(!modelFloorPlacementGrid.TryGetValue(key,out List<ModelFloorPlacementEntry> bucket))modelFloorPlacementGrid[key]=bucket=new List<ModelFloorPlacementEntry>();bucket.Add(entry);}
        }
      }
    }

    private static int ModelFloorPlacementCell(float coordinate)=>(int)Math.Floor(coordinate/ModelFloorPlacementCellSize);

    private ModelFloorData GetOrBuildModelFloorData(GR2 model,AssetInstance inst){
      if(modelFloorData.TryGetValue(model,out ModelFloorData cached))return cached;
      var result=new ModelFloorData();modelFloorData[model]=result;
      string assetPath=String.Empty;if(area!=null&&inst!=null&&area.AssetIdMap.TryGetValue(inst.assetID,out AreaAsset asset))assetPath=NormalizeAssetPath(asset.Path);
      List<GR2_Mesh> candidates=model.meshes.Where(m=>m!=null&&m.meshVerts!=null&&m.meshVertIndex!=null&&m.meshVerts.Count>=3&&m.meshVertIndex.Count>=3).ToList();
      List<GR2_Mesh> collision=candidates.Where(m=>IsFloorCollisionMesh(m,assetPath)).ToList();
      IEnumerable<GR2_Mesh> chosen=collision.Count>0?collision:candidates.Where(m=>!IsFloorNonVisualMesh(m,assetPath));
      foreach(GR2_Mesh mesh in chosen){FloorMeshData data=BuildFloorMeshData(mesh);if(data!=null&&data.TriangleCount>0)result.Meshes.Add(data);}
      return result;
    }

    private static bool IsFloorCollisionMesh(GR2_Mesh mesh,string assetPath){
      if(mesh==null)return false;string name=(mesh.meshName??String.Empty).ToLowerInvariant();string path=NormalizeAssetPath(assetPath);
      // Exact Jedipedia hint-geometry classification. In particular, do not hide arbitrary meshes merely because
      // their name contains "collision"/"occluder"; SWTOR ships visible meshes with misleading names.
      return mesh.lod==-1||name=="collision"||path.Contains("designblockout/cover_objects/")||path.Contains("superexclusion")||name.Contains("superexclusion");
    }

    private static bool IsFloorNonVisualMesh(GR2_Mesh mesh,string assetPath){
      if(mesh==null)return true;string name=(mesh.meshName??String.Empty).ToLowerInvariant();string path=NormalizeAssetPath(assetPath);
      if(IsFloorCollisionMesh(mesh,path))return true;
      return mesh.lod==-3||mesh.lod==-2||name=="portal"||path.Contains("fadeportal");
    }

    private FloorMeshData BuildFloorMeshData(GR2_Mesh mesh){
      int indexCount=mesh?.meshVertIndex?.Count??0,vertexCount=mesh?.meshVerts?.Count??0;if(indexCount<3||vertexCount<3)return null;
      int triangleCount=indexCount/3;var result=new FloorMeshData{Mesh=mesh,TriangleCount=triangleCount};
      if(triangleCount<FloorMeshIndexThreshold)return result;
      float minX=float.MaxValue,maxX=float.MinValue,minZ=float.MaxValue,maxZ=float.MinValue;
      foreach(GR2_Mesh_Vertex v in mesh.meshVerts){if(v==null)continue;minX=Math.Min(minX,v.X);maxX=Math.Max(maxX,v.X);minZ=Math.Min(minZ,v.Z);maxZ=Math.Max(maxZ,v.Z);}
      float extent=Math.Max(maxX-minX,maxZ-minZ);if(float.IsNaN(extent)||float.IsInfinity(extent)||extent<=0f)return result;
      int perAxis=Math.Max(1,(int)Math.Ceiling(Math.Sqrt(triangleCount/(double)FloorMeshTargetTrianglesPerCell)));float cell=Math.Max(.01f,extent/perAxis);
      result.CellSize=cell;result.Cells=new Dictionary<(int X,int Z),List<int>>();result.Overflow=new List<int>();
      for(int offset=0;offset+2<indexCount;offset+=3){
        int ai=mesh.meshVertIndex[offset].index,bi=mesh.meshVertIndex[offset+1].index,ci=mesh.meshVertIndex[offset+2].index;if(ai>=vertexCount||bi>=vertexCount||ci>=vertexCount)continue;
        GR2_Mesh_Vertex a=mesh.meshVerts[ai],b=mesh.meshVerts[bi],c=mesh.meshVerts[ci];
        float tminX=Math.Min(a.X,Math.Min(b.X,c.X)),tmaxX=Math.Max(a.X,Math.Max(b.X,c.X)),tminZ=Math.Min(a.Z,Math.Min(b.Z,c.Z)),tmaxZ=Math.Max(a.Z,Math.Max(b.Z,c.Z));
        int cminX=(int)Math.Floor(tminX/cell),cmaxX=(int)Math.Floor(tmaxX/cell),cminZ=(int)Math.Floor(tminZ/cell),cmaxZ=(int)Math.Floor(tmaxZ/cell);long cells=(long)(cmaxX-cminX+1)*(cmaxZ-cminZ+1);
        if(cells<=0||cells>256){result.Overflow.Add(offset);continue;}
        for(int z=cminZ;z<=cmaxZ;z++)for(int x=cminX;x<=cmaxX;x++){var key=(x,z);if(!result.Cells.TryGetValue(key,out List<int> bucket))result.Cells[key]=bucket=new List<int>();bucket.Add(offset);}
      }
      return result;
    }

    private static bool TryModelWorldXZBounds(GR2 model,Matrix world,out float minX,out float maxX,out float minZ,out float maxZ){
      minX=minZ=float.MaxValue;maxX=maxZ=float.MinValue;GR2_Bounding_Box box=model?.globalBox;if(box==null)return false;
      Vector3 min=new Vector3(box.minX,box.minY,box.minZ),max=new Vector3(box.maxX,box.maxY,box.maxZ);if(!IsFinite(min)||!IsFinite(max))return false;
      for(int z=0;z<2;z++)for(int y=0;y<2;y++)for(int x=0;x<2;x++){Vector3 p=Vector3.TransformCoordinate(new Vector3(x==0?min.X:max.X,y==0?min.Y:max.Y,z==0?min.Z:max.Z),world);minX=Math.Min(minX,p.X);maxX=Math.Max(maxX,p.X);minZ=Math.Min(minZ,p.Z);maxZ=Math.Max(maxZ,p.Z);}
      return minX<=maxX&&minZ<=maxZ;
    }

    private Room FindModelFloorRoom(Vector3 p,float maxDistance){
      Room best=null;float bestDistance=maxDistance+.0001f;
      void Consider(IEnumerable<ModelFloorPlacementEntry> placements){
        if(placements==null)return;
        foreach(ModelFloorPlacementEntry placement in placements){
          if(placement==null||placement.Room==null||IsEverywhereRoom(placement.Room)||p.X<placement.WorldMinX-.0001f||p.X>placement.WorldMaxX+.0001f||p.Z<placement.WorldMinZ-.0001f||p.Z>placement.WorldMaxZ+.0001f)continue;
          Vector3 local=Vector3.TransformCoordinate(p,placement.Inverse);
          foreach(FloorMeshData meshData in placement.Model.Meshes){
            IEnumerable<int> offsets=FloorMeshCandidateOffsets(meshData,local,placement.LocalXZIndependentOfY);
            foreach(int offset in offsets){
              if(!TryFloorTriangleHit(meshData.Mesh,offset,placement.World,p,out float y))continue;float distance=p.Y-y;if(distance<-.0001f||distance>bestDistance)continue;
              Room room=placement.Room;if(best==null||distance<bestDistance-.0001f||(Math.Abs(distance-bestDistance)<=.0001f&&RoomFloorTieRadius(room)<RoomFloorTieRadius(best))){best=room;bestDistance=Math.Max(0f,distance);}
            }
          }
        }
      }
      modelFloorPlacementGrid.TryGetValue((ModelFloorPlacementCell(p.X),ModelFloorPlacementCell(p.Z)),out List<ModelFloorPlacementEntry> bucket);Consider(bucket);Consider(modelFloorPlacementGlobal);return best;
    }

    private static IEnumerable<int> FloorMeshCandidateOffsets(FloorMeshData data,Vector3 local,bool canUseIndex){
      if(data==null||data.Mesh==null)yield break;
      if(!canUseIndex||data.Cells==null||data.CellSize<=0f){for(int offset=0;offset+2<data.Mesh.meshVertIndex.Count;offset+=3)yield return offset;yield break;}
      int cx=(int)Math.Floor(local.X/data.CellSize),cz=(int)Math.Floor(local.Z/data.CellSize);var seen=new HashSet<int>();
      for(int z=cz-1;z<=cz+1;z++)for(int x=cx-1;x<=cx+1;x++)if(data.Cells.TryGetValue((x,z),out List<int> bucket))foreach(int offset in bucket)if(seen.Add(offset))yield return offset;
      if(data.Overflow!=null)foreach(int offset in data.Overflow)if(seen.Add(offset))yield return offset;
    }

    private static bool TryFloorTriangleHit(GR2_Mesh mesh,int offset,Matrix world,Vector3 point,out float worldY){
      worldY=0f;if(mesh==null||offset<0||offset+2>=mesh.meshVertIndex.Count)return false;int count=mesh.meshVerts.Count;int ai=mesh.meshVertIndex[offset].index,bi=mesh.meshVertIndex[offset+1].index,ci=mesh.meshVertIndex[offset+2].index;if(ai>=count||bi>=count||ci>=count)return false;
      GR2_Mesh_Vertex av=mesh.meshVerts[ai],bv=mesh.meshVerts[bi],cv=mesh.meshVerts[ci];Vector3 a=Vector3.TransformCoordinate(new Vector3(av.X,av.Y,av.Z),world),b=Vector3.TransformCoordinate(new Vector3(bv.X,bv.Y,bv.Z),world),c=Vector3.TransformCoordinate(new Vector3(cv.X,cv.Y,cv.Z),world);
      Vector3 ab=b-a,ac=c-a,n=Vector3.Cross(ab,ac);float len2=n.LengthSquared();if(len2<=.00000001f||n.Y*n.Y<len2*.2025f)return false;
      return TryTriangleYAtXZ(a,b,c,point.X,point.Z,out worldY);
    }

    // Orthographic cutaway height follows Jedipedia's nearest authored floor under the camera. Prefer the
    // highest valid HMS/model triangle at this X/Z, but never jump to geometry above the current eye.
    private bool TrySampleViewerFloor(float worldX,float worldZ,float eyeY,out float worldY){
      worldY=0f;float bestWorldY=float.MinValue;bool found=false;Vector3 p=new Vector3(worldX,eyeY,worldZ);
      void ConsiderY(float y){
        if(float.IsNaN(y)||float.IsInfinity(y)||y>eyeY+.05f)return;
        if(!found||y>bestWorldY){bestWorldY=y;found=true;}
      }
      if(heightMapFloorGrid.TryGetValue((FloorCell(worldX),FloorCell(worldZ)),out List<HeightMapFloorEntry> heightEntries))
        foreach(HeightMapFloorEntry entry in heightEntries)if(TryHeightMapFloorY(entry,p,out float y))ConsiderY(y);
      void ConsiderModels(IEnumerable<ModelFloorPlacementEntry> placements){
        if(placements==null)return;
        foreach(ModelFloorPlacementEntry placement in placements){
          if(placement==null||placement.Model==null||worldX<placement.WorldMinX-.0001f||worldX>placement.WorldMaxX+.0001f||worldZ<placement.WorldMinZ-.0001f||worldZ>placement.WorldMaxZ+.0001f)continue;
          Vector3 local;try{local=Vector3.TransformCoordinate(p,placement.Inverse);}catch{continue;}
          foreach(FloorMeshData meshData in placement.Model.Meshes)
            foreach(int offset in FloorMeshCandidateOffsets(meshData,local,placement.LocalXZIndependentOfY))
              if(TryFloorTriangleHit(meshData.Mesh,offset,placement.World,p,out float y))ConsiderY(y);
        }
      }
      modelFloorPlacementGrid.TryGetValue((ModelFloorPlacementCell(worldX),ModelFloorPlacementCell(worldZ)),out List<ModelFloorPlacementEntry> modelEntries);
      ConsiderModels(modelEntries);ConsiderModels(modelFloorPlacementGlobal);
      if(found)worldY=bestWorldY;return found;
    }

    private static bool TryTriangleYAtXZ(Vector3 a,Vector3 b,Vector3 c,float x,float z,out float y){
      y=0f;if(x<Math.Min(a.X,Math.Min(b.X,c.X))-.0001f||x>Math.Max(a.X,Math.Max(b.X,c.X))+.0001f||z<Math.Min(a.Z,Math.Min(b.Z,c.Z))-.0001f||z>Math.Max(a.Z,Math.Max(b.Z,c.Z))+.0001f)return false;
      float denominator=(b.Z-c.Z)*(a.X-c.X)+(c.X-b.X)*(a.Z-c.Z);if(Math.Abs(denominator)<.0001f)return false;float wa=((b.Z-c.Z)*(x-c.X)+(c.X-b.X)*(z-c.Z))/denominator;float wb=((c.Z-a.Z)*(x-c.X)+(a.X-c.X)*(z-c.Z))/denominator;float wc=1f-wa-wb;if(wa<-.0001f||wb<-.0001f||wc<-.0001f)return false;y=wa*a.Y+wb*b.Y+wc*c.Y;return true;
    }

    // Same triangular-prism containment as Jedipedia's phase.js. The floor follows the authored triangle plane
    // and the ceiling adds the barycentrically interpolated per-vertex extrusion, so sloped regions remain wedges
    // instead of turning into oversized axis-aligned slabs.
    private static bool RegionContainsLocalPosition(RegionVolumeData region,Vector3 local){
      if(region?.Positions==null||region.Heights==null||region.Indices==null||region.Indices.Length<3)return false;
      Vector3 min=region.Min,max=region.Max;if(local.X<min.X-.0001f||local.X>max.X+.0001f||local.Y<min.Y-.0001f||local.Y>max.Y+.0001f||local.Z<min.Z-.0001f||local.Z>max.Z+.0001f)return false;
      Vector3[] positions=region.Positions;float[] heights=region.Heights;ushort[] indices=region.Indices;
      for(int i=0;i+2<indices.Length;i+=3){
        int ai=indices[i],bi=indices[i+1],ci=indices[i+2];if(ai>=positions.Length||bi>=positions.Length||ci>=positions.Length||ai>=heights.Length||bi>=heights.Length||ci>=heights.Length)continue;
        Vector3 a=positions[ai],b=positions[bi],c=positions[ci];float acx=c.X-a.X,acz=c.Z-a.Z,abx=b.X-a.X,abz=b.Z-a.Z;float denominator=abx*acz-acx*abz;if(Math.Abs(denominator)<.000000001f)continue;
        float px=local.X-a.X,pz=local.Z-a.Z;float beta=(px*acz-acx*pz)/denominator,gamma=(abx*pz-px*abz)/denominator;if(beta<-.0001f||gamma<-.0001f||beta+gamma>1.0001f)continue;
        float alpha=1f-beta-gamma;float floor=alpha*a.Y+beta*b.Y+gamma*c.Y;float ceiling=floor+alpha*heights[ai]+beta*heights[bi]+gamma*heights[ci];float low=Math.Min(floor,ceiling),high=Math.Max(floor,ceiling);
        if(local.Y>=low-.0001f&&local.Y<=high+.0001f)return true;
      }
      return false;
    }

    public string CurrentRegionSummary {
      get {
        List<string> regions=CurrentRegionDescriptions(camera?.Position??Vector3.Zero,4);
        return regions.Count==0?"none":String.Join(" | ",regions);
      }
    }

    public List<string> CurrentRegionDescriptions(Vector3 worldPosition,int maxCount=16){
      return CurrentVolumeInfos(worldPosition,maxCount).Select(info=>info.Description).ToList();
    }

    private Room FindPlacementRoom(Vector3 p){
      RoomPlacementEntry best=null;float bestDistance=float.MaxValue;
      void Consider(IEnumerable<RoomPlacementEntry> entries){
        if(entries==null)return;
        foreach(RoomPlacementEntry entry in entries){
          Vector3 local=Vector3.TransformCoordinate(p,entry.Inverse);float distance;
          if(entry.RegionVolume!=null){
            if(!RegionContainsLocalPosition(entry.RegionVolume,local))continue;
            Vector3 rmin=entry.RegionVolume.Min,rmax=entry.RegionVolume.Max;
            float cx=(rmin.X+rmax.X)*.5f,cz=(rmin.Z+rmax.Z)*.5f;
            float hx=Math.Max((rmax.X-rmin.X)*.5f,1f),hz=Math.Max((rmax.Z-rmin.Z)*.5f,1f);
            float dx=(local.X-cx)/hx,dz=(local.Z-cz)/hz;distance=(float)Math.Sqrt(dx*dx+dz*dz);
          }else{
            float halfW=entry.Width*.5f,halfD=entry.Depth*.5f;
            if(Math.Abs(local.X)>halfW+.0001f||Math.Abs(local.Z)>halfD+.0001f)continue;
            if(entry.HasHeight&&Math.Abs(local.Y)>entry.Height*.5f+8f)continue;
            distance=(float)Math.Sqrt(local.X*local.X/Math.Max(halfW*halfW,1f)+local.Z*local.Z/Math.Max(halfD*halfD,1f));
          }
          if(best==null||entry.Rank<best.Rank||(entry.Rank==best.Rank&&(distance<bestDistance-.0001f||(Math.Abs(distance-bestDistance)<.0001f&&String.Compare(entry.Room.RoomName,best.Room.RoomName,StringComparison.OrdinalIgnoreCase)<0)))){
            best=entry;bestDistance=distance;
          }
        }
      }
      roomPlacementGrid.TryGetValue((RoomPlacementCell(p.X),RoomPlacementCell(p.Z)),out List<RoomPlacementEntry> bucket);Consider(bucket);Consider(roomPlacementGlobal);
      return best?.Room;
    }

    private void BuildRenderSpatialIndex(){
      renderGrid.Clear();renderGlobal.Clear();renderEntriesByRoom.Clear();occluderRenderEntries.Clear();walkingPathFollowerRenderEntries.Clear();
      foreach(Room room in rooms){
        if(room==null||skyRoomNames.Contains(room.RoomName))continue;
        foreach(AssetInstance inst in room.InstancesById.Values){
          Matrix world=InstanceWorld(inst,room);
          // Only prepass LOD -3 occlusion meshes that belong to otherwise visible world placements. Direct
          // OCCLUDER_ONLY / MAP_ONLY placements often are doorway/portal blocker planes; writing those invisible
          // shapes into the final scene depth produces the solid black "wall" masks seen at some entrances.
          if(inst!=null&&!inst.hidden&&!inst.PathFollowerPending&&!inst.PathFollowerAnimated&&
             inst.Viewability!=AssetInstanceViewability.OccluderOnly&&inst.Viewability!=AssetInstanceViewability.MapOnly&&
             models.TryGetValue(inst.assetID,out GR2 occModel)&&occModel!=null&&occModel.enabled&&ModelHasOcclusionGeometry(occModel)){
            Vector3 oc;float oradius;if(!TryModelSphere(occModel,world,out oc,out oradius)){oc=new Vector3(world.M41,world.M42,world.M43);oradius=0f;}
            if(IsFinite(oc))occluderRenderEntries.Add(new RenderEntry{Room=room,Instance=inst,Model=occModel,World=world,Center=oc,Radius=oradius,Kind=RenderKindModel});
          }
          if(!InstanceCanEnterRenderIndex(inst))continue;RenderEntry entry=null;
          if(inst.hasHeightMap&&TryTerrainSphere(inst,room,out Vector3 tc,out float tr))entry=new RenderEntry{Room=room,Instance=inst,World=world,Center=tc,Radius=tr,Kind=RenderKindTerrain};
          else if(inst.hasWater){
            Vector3 wc=Vector3.TransformCoordinate(Vector3.Zero,world);float wr=.5f*(float)Math.Sqrt(Math.Max(0f,inst.width*inst.width+inst.height*inst.height+inst.depth*inst.depth))*MatrixMaxScale(world);
            entry=new RenderEntry{Room=room,Instance=inst,World=world,Center=wc,Radius=wr,Kind=RenderKindWater};
          } else if(models.TryGetValue(inst.assetID,out GR2 model)&&model!=null&&model.enabled){
            Vector3 mc;float mr;if(!TryModelSphere(model,world,out mc,out mr)){mc=new Vector3(world.M41,world.M42,world.M43);mr=0f;}
            entry=new RenderEntry{Room=room,Instance=inst,Model=model,World=world,Center=mc,Radius=mr,Kind=RenderKindModel};
          }
          if(entry==null||!IsFinite(entry.Center))continue;
          if(!renderEntriesByRoom.TryGetValue(room.RoomName,out List<RenderEntry> roomEntries))renderEntriesByRoom[room.RoomName]=roomEntries=new List<RenderEntry>();
          roomEntries.Add(entry);
          // Moving path-follower descendants cannot live in a fixed grid cell. Jedipedia keeps this traffic dynamic;
          // put the handful of animated entries in the global list and refresh their world sphere every frame.
          if(inst.PathFollowerAnimated){renderGlobal.Add(entry);if(entry.Kind==RenderKindModel)walkingPathFollowerRenderEntries.Add(entry);continue;}
          if(entry.Radius>RenderIndexedRadiusLimit){renderGlobal.Add(entry);continue;}
          var key=(RenderCell(entry.Center.X),RenderCell(entry.Center.Z));if(!renderGrid.TryGetValue(key,out List<RenderEntry> bucket))renderGrid[key]=bucket=new List<RenderEntry>();bucket.Add(entry);
        }
      }
    }

    private void BuildModelStreamingCatalog(IEnumerable<WorldModelStreamRequest> requests) {
      streamRequestsByRoom.Clear();streamPlacementsByAsset.Clear();streamPlacementsByRoomAsset.Clear();streamRoomAssetHints.Clear();streamBootstrapAssetsByCell.Clear();streamBootstrapRoomsByCell.Clear();streamRoomsNeedingExactBounds.Clear();streamRequestsByAsset.Clear();activeStreamAssetIds.Clear();queuedStreamDemandAssetIds.Clear();urgentStreamAssetIds.Clear();materialStreamAssetIds.Clear();activeStreamRoomNames.Clear();previousStreamRoomNames.Clear();floorStreamRoomNames.Clear();previousFloorStreamRoomNames.Clear();initialStreamAssetIds.Clear();initialStreamRoomNames.Clear();initialStreamPrefetchRoomNames.Clear();initialStreamAssetDistances.Clear();streamCatalogAssetCount=0;streamIndexedInstances.Clear();streamFloorIndexedInstances.Clear();streamedWorldModels.Clear();streamedModelAssetIds.Clear();pendingModelGpuUploads.Clear();queuedModelGpuUploads.Clear();pendingStreamedModelIntegrations.Clear();queuedStreamedModelIntegrations.Clear();pendingStreamedFloorIntegrations.Clear();queuedStreamedFloorIntegrations.Clear();pendingStreamedModelMaterials.Clear();queuedStreamedModelMaterials.Clear();pendingStreamedMaterialPrepares.Clear();streamedWorldMaterials.Clear();streamedMaterialResourcesPrepared.Clear();streamedModelMaterialsReady.Clear();cameraAheadStreamRooms.Clear();urgentCameraAheadStreamRooms.Clear();streamTransitionRoomGrace.Clear();lastStreamAnchorRoom=String.Empty;
      if(modelStreamer==null||requests==null)return;
      var byAsset=requests.Where(x=>x!=null).GroupBy(x=>x.AssetId).ToDictionary(x=>x.Key,x=>x.First());
      foreach(var pair in byAsset)streamRequestsByAsset[pair.Key]=pair.Value;
      streamCatalogAssetCount=byAsset.Count;
      foreach(Room room in rooms) {
        if(room?.InstancesById==null)continue;
        foreach(AssetInstance instance in room.InstancesById.Values) {
          if(instance==null||!byAsset.TryGetValue(instance.assetID,out WorldModelStreamRequest request))continue;
          if(!streamRequestsByRoom.TryGetValue(room.RoomName,out List<WorldModelStreamRequest> roomRequests))streamRequestsByRoom[room.RoomName]=roomRequests=new List<WorldModelStreamRequest>();
          if(!roomRequests.Any(x=>x.AssetId==request.AssetId))roomRequests.Add(request);
          if(!streamPlacementsByAsset.TryGetValue(request.AssetId,out List<(Room Room,AssetInstance Instance)> placements))streamPlacementsByAsset[request.AssetId]=placements=new List<(Room Room,AssetInstance Instance)>();
          placements.Add((room,instance));
          if(!streamPlacementsByRoomAsset.TryGetValue(room.RoomName,out Dictionary<ulong,List<(Room Room,AssetInstance Instance)>> byRoomAsset))streamPlacementsByRoomAsset[room.RoomName]=byRoomAsset=new Dictionary<ulong,List<(Room Room,AssetInstance Instance)>>();
          if(!byRoomAsset.TryGetValue(request.AssetId,out List<(Room Room,AssetInstance Instance)> roomPlacements))byRoomAsset[request.AssetId]=roomPlacements=new List<(Room Room,AssetInstance Instance)>();
          roomPlacements.Add((room,instance));
        }
      }
    }

    private void BuildInitialStreamWorkingSet(Vector3 position,Room arrivalRoom){
      initialStreamAssetIds.Clear();initialStreamRoomNames.Clear();initialStreamPrefetchRoomNames.Clear();initialStreamAssetDistances.Clear();
      const int maxGateRooms=12,maxLocalCandidateRooms=7,maxPrefetchRooms=16;
      var byName=rooms.Where(r=>r!=null&&!String.IsNullOrWhiteSpace(r.RoomName))
        .GroupBy(r=>r.RoomName,StringComparer.OrdinalIgnoreCase).ToDictionary(g=>g.Key,g=>g.First(),StringComparer.OrdinalIgnoreCase);
      bool AddGateRoom(Room room){
        if(room==null||IsEverywhereRoom(room)||skyRoomNames.Contains(room.RoomName)||String.IsNullOrWhiteSpace(room.RoomName)||initialStreamRoomNames.Contains(room.RoomName)||initialStreamRoomNames.Count>=maxGateRooms)return false;
        initialStreamRoomNames.Add(room.RoomName);
        if(streamRequestsByRoom.TryGetValue(room.RoomName,out List<WorldModelStreamRequest> requests))foreach(WorldModelStreamRequest request in requests){
          if(request==null)continue;
          // The first-frame barrier is a *visible shell* barrier, not a dPVS/editor-data barrier. Jedipedia loads the
          // whole planet before presenting, but our local equivalent must not hold a Corellia spawn on authored-hidden
          // or MAP/OCCLUDER-only placements. Those remain normal room demand and can settle behind the first frame.
          bool visiblePlacement=!streamPlacementsByRoomAsset.TryGetValue(room.RoomName,out Dictionary<ulong,List<(Room Room,AssetInstance Instance)>> byAsset)||
            !byAsset.TryGetValue(request.AssetId,out List<(Room Room,AssetInstance Instance)> placements)||placements.Any(x=>InstanceVisibleInWorld(x.Instance));
          if(visiblePlacement)initialStreamAssetIds.Add(request.AssetId);
        }
        return true;
      }

      AddGateRoom(arrivalRoom);
      // The phase/region owner is a semantic seed, but the phase label itself is not a room name. On Corellia a class
      // hangar often overlaps a generic spaceport cell, so both the floor candidate and the trigger owner participate.
      AddGateRoom(FindPhaseTriggerRoom(position));

      // Exact authored/completed room bounds are reliable at startup. Coarse placement-origin boxes are only hints;
      // allowing every coarse overlap to consume all gate slots was capable of selecting several outdoor/base cells
      // while excluding the still-undecoded hangar. Admit exact overlaps freely, then at most two coarse overlaps.
      var overlapping=rooms.Where(r=>r!=null&&!IsEverywhereRoom(r)&&!skyRoomNames.Contains(r.RoomName)&&TryGetRoomStreamingBounds(r,out _))
        .Select(r=>{TryGetRoomStreamingBounds(r,out RoomStreamingBounds b);return (Room:r,Bounds:b,Distance:StreamingBoundsDistanceSquared(b,position));})
        .Where(x=>ContainsStreamingBounds(x.Bounds,position,18f))
        .OrderBy(x=>x.Bounds.Coarse?1:0).ThenBy(x=>x.Bounds.Radius).ThenBy(x=>x.Distance).ToList();
      foreach(var item in overlapping.Where(x=>!x.Bounds.Coarse)){if(initialStreamRoomNames.Count>=maxGateRooms)break;AddGateRoom(item.Room);}
      int coarseOverlapAdds=0;
      foreach(var item in overlapping.Where(x=>x.Bounds.Coarse)){if(initialStreamRoomNames.Count>=maxGateRooms||coarseOverlapAdds>=2)break;if(AddGateRoom(item.Room))coarseOverlapAdds++;}

      // This is the important v6/Jedipedia bridge: before GR2-derived floors exist, resolve nearby *room owners* from
      // authored placement transforms. The previous safety ring added only asset IDs to the barrier; those GR2s could
      // finish decoding and uploading while their owner room never entered render/floor integration, allowing the
      // loading overlay to disappear over an empty hangar. Gate a tiny number of physically local/semantic owner cells.
      Dictionary<string,float> physicalRooms=SpatialBootstrapRoomDistances(position,260f);
      var semanticRooms=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      foreach(string seed in initialStreamRoomNames.ToArray())foreach(string name in StreamingNeighborRoomNames(seed,96))semanticRooms.Add(name);
      var localCandidates=byName.Values.Where(r=>r!=null&&!initialStreamRoomNames.Contains(r.RoomName)&&!IsEverywhereRoom(r)&&!skyRoomNames.Contains(r.RoomName))
        .Select(r=>{
          bool have=TryGetRoomStreamingBounds(r,out RoomStreamingBounds b);bool contains=have&&ContainsStreamingBounds(b,position,18f);
          float boundsDistance=have?StreamingBoundsDistanceSquared(b,position):float.MaxValue;
          bool physical=physicalRooms.TryGetValue(r.RoomName,out float physicalDistance);
          bool semantic=semanticRooms.Contains(r.RoomName);
          float distance=Math.Min(boundsDistance,physical?physicalDistance:float.MaxValue);
          return (Room:r,Have:have,Contains:contains,Coarse:!have||b.Coarse,Physical:physical,Semantic:semantic,Distance:distance);
        })
        .Where(x=>x.Contains||x.Semantic&&x.Distance<=240f*240f||x.Physical&&x.Distance<=220f*220f)
        .OrderBy(x=>x.Contains?0:1).ThenBy(x=>x.Room.OutdoorsVisible?1:0).ThenBy(x=>x.Physical?0:1).ThenBy(x=>x.Semantic?0:1).ThenBy(x=>x.Coarse?1:0).ThenBy(x=>x.Distance)
        .ThenBy(x=>x.Room.RoomName,StringComparer.OrdinalIgnoreCase);
      int localAdds=0;
      foreach(var item in localCandidates){if(initialStreamRoomNames.Count>=maxGateRooms||localAdds>=maxLocalCandidateRooms)break;if(AddGateRoom(item.Room))localAdds++;}
      if(initialStreamRoomNames.Count==0){Room fallback=FindStreamingRoom(position,128f);AddGateRoom(fallback);}

      // Prefetch is room-based too. It can be broader than the first-frame barrier because it is normal-priority and
      // does not decide when the loading overlay disappears. Include authored/portal neighbours plus physically local
      // owners so a doorway already has render integration queued even when its pre-decode room bounds are inaccurate.
      var neighbourCandidates=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      foreach(string seed in initialStreamRoomNames)foreach(string name in StreamingNeighborRoomNames(seed,32))neighbourCandidates.Add(name);
      foreach(string name in physicalRooms.OrderBy(x=>x.Value).Select(x=>x.Key).Take(36))neighbourCandidates.Add(name);
      foreach(string name in neighbourCandidates.Where(name=>byName.ContainsKey(name)&&!initialStreamRoomNames.Contains(name))
        .OrderBy(name=>{
          Room r=byName[name];float boundsDistance=TryGetRoomStreamingBounds(r,out RoomStreamingBounds b)?StreamingBoundsDistanceSquared(b,position):float.MaxValue;
          return Math.Min(boundsDistance,physicalRooms.TryGetValue(name,out float physicalDistance)?physicalDistance:float.MaxValue);
        })){
        if(initialStreamPrefetchRoomNames.Count>=maxPrefetchRooms)break;initialStreamPrefetchRoomNames.Add(name);
      }
      if(initialStreamPrefetchRoomNames.Count<maxPrefetchRooms){
        foreach(Room room in rooms.Where(r=>r!=null&&!IsEverywhereRoom(r)&&!skyRoomNames.Contains(r.RoomName)&&!initialStreamRoomNames.Contains(r.RoomName)&&!initialStreamPrefetchRoomNames.Contains(r.RoomName)&&TryGetRoomStreamingBounds(r,out _))
          .OrderBy(r=>{TryGetRoomStreamingBounds(r,out RoomStreamingBounds b);return StreamingBoundsDistanceSquared(b,position);})){
          if(initialStreamPrefetchRoomNames.Count>=maxPrefetchRooms)break;
          TryGetRoomStreamingBounds(room,out RoomStreamingBounds bounds);if(StreamingBoundsDistanceSquared(bounds,position)>240f*240f)break;initialStreamPrefetchRoomNames.Add(room.RoomName);
        }
      }

      // Only assets belonging to actual gate rooms decide first-frame readiness. This keeps the invariant useful:
      // every barrier GR2 has a room whose render/floor placements are also required below. Arbitrary nearby assets
      // remain speculative prefetch and can no longer make IsInitialStreamAssetDisplayReady() return true too early.
      foreach(ulong assetId in initialStreamAssetIds){
        float roomDistance=InitialStreamAssetDistanceUncached(assetId,position);
        initialStreamAssetDistances[assetId]=roomDistance;
      }
    }

    private static float PointAabbDistanceSquared(Vector3 p,Vector3 min,Vector3 max){
      float dx=p.X<min.X?min.X-p.X:(p.X>max.X?p.X-max.X:0f),dy=p.Y<min.Y?min.Y-p.Y:(p.Y>max.Y?p.Y-max.Y:0f),dz=p.Z<min.Z?min.Z-p.Z:(p.Z>max.Z?p.Z-max.Z:0f);return dx*dx+dy*dy+dz*dz;
    }

    private float InitialStreamAssetDistanceUncached(ulong assetId,Vector3 position){
      float best=float.MaxValue;
      foreach(string roomName in initialStreamRoomNames){
        if(!streamRoomAssetHints.TryGetValue(roomName,out Dictionary<ulong,StreamRoomAssetHint> roomHints)||!roomHints.TryGetValue(assetId,out StreamRoomAssetHint hint)||hint==null||!hint.Any)continue;
        best=Math.Min(best,PointAabbDistanceSquared(position,hint.Min,hint.Max));
      }
      return best;
    }

    private float InitialStreamAssetDistance(ulong assetId)=>initialStreamAssetDistances.TryGetValue(assetId,out float distance)?distance:InitialStreamAssetDistanceUncached(assetId,camera.Position);

    private void RequestInitialStreamWorkingSet(){
      if(modelStreamer==null||initialStreamAssetIds.Count==0)return;
      activeStreamRoomNames.UnionWith(initialStreamRoomNames);activeStreamAssetIds.UnionWith(initialStreamAssetIds);urgentStreamAssetIds.UnionWith(initialStreamAssetIds);
      // Build collision/floor data for the same small spawn shell while the overlay is still up. This lets
      // FindCameraRoom become authoritative before normal runtime demand starts instead of continuing from an
      // outdoor/spaceport fallback underneath a not-yet-decoded hangar.
      // Room-floor classification is deferred detail, not visible-shell work. Keep only the nearest few gate rooms in
      // that CPU-heavy queue while the overlay is up; once the scene starts, normal anchor/phase demand continues it.
      // Prefer the interior cells that actually contain the spawn. Distance alone is often tied at zero for several
      // overlapping Corellia cells, and HashSet order could otherwise spend every expensive floor slot on the
      // generic outdoor/base rooms underneath the hangar.
      foreach(string roomName in initialStreamRoomNames.Select(name=>{
        Room r=rooms.FirstOrDefault(x=>x!=null&&String.Equals(x.RoomName,name,StringComparison.OrdinalIgnoreCase));
        RoomStreamingBounds b=null;bool have=r!=null&&TryGetRoomStreamingBounds(r,out b);
        bool contains=have&&ContainsStreamingBounds(b,camera.Position,12f);
        float distance=have?StreamingBoundsDistanceSquared(b,camera.Position):float.MaxValue;
        return (Name:name,Room:r,Contains:contains,Distance:distance);
      }).OrderBy(x=>x.Contains?0:1).ThenBy(x=>x.Room!=null&&x.Room.OutdoorsVisible?1:0).ThenBy(x=>x.Distance).ThenBy(x=>x.Name,StringComparer.OrdinalIgnoreCase).Take(4).Select(x=>x.Name))floorStreamRoomNames.Add(roomName);
      // Schedule by distance across the whole startup shell instead of filling all 96 slots from the first room in
      // dictionary order. This is the C# equivalent of Jedipedia's global priority queue/backpressure behaviour.
      foreach(ulong assetId in initialStreamAssetIds.OrderBy(InitialStreamAssetDistance))
        if(streamRequestsByAsset.TryGetValue(assetId,out WorldModelStreamRequest request))modelStreamer.Request(request,true);
      // Adjacent rooms are useful immediately after the gate drops, but are not themselves a reason to hold the
      // first frame. Keep a bounded normal-priority ring resident/queued behind the urgent spawn shell.
      foreach(string roomName in initialStreamPrefetchRoomNames)RequestRoomModels(roomName,false,48,true);
      // Full DDS resources are not part of structural first-frame readiness. Warm only the nearest slice so texture
      // uploads cannot starve GR2/GPU work for the rest of the hangar shell.
      // Do not let MAT/DDS archive traffic compete with the missing structural shell. Jedipedia's loader has separate
      // bands/backpressure; in this local streamer the equivalent is to start texture refinement only once most of the
      // startup GR2 set has already been installed. The first frame is allowed to show metadata-only fallback materials.
      int installedGeometry=initialStreamAssetIds.Count(id=>models.ContainsKey(id));
      // Keep GR2/TOR bandwidth exclusive until every visible first-frame asset is installed. The previous 75%
      // threshold started two MAT workers while the last quarter of a hangar shell was still waiting on the same
      // archives, extending exactly the missing-geometry phase the startup gate is meant to minimize.
      int materialWarmThreshold=Math.Max(1,initialStreamAssetIds.Count);
      if(installedGeometry>=materialWarmThreshold)foreach(ulong assetId in initialStreamAssetIds.OrderBy(InitialStreamAssetDistance).Take(24)){
        materialStreamAssetIds.Add(assetId);
        if(models.TryGetValue(assetId,out GR2 model)&&model!=null)QueueStreamedModelMaterials(model);
      }
    }

    private void MarkRoomMaterialDemand(string roomName,int limit=-1){
      if(String.IsNullOrWhiteSpace(roomName)||!streamRequestsByRoom.TryGetValue(roomName,out List<WorldModelStreamRequest> requests))return;
      IEnumerable<WorldModelStreamRequest> ordered=requests.OrderBy(x=>StreamRequestDistance(roomName,x.AssetId));
      if(limit>=0)ordered=ordered.Take(limit);
      foreach(WorldModelStreamRequest request in ordered){
        if(request==null)continue;materialStreamAssetIds.Add(request.AssetId);
        if(models.TryGetValue(request.AssetId,out GR2 model)&&model!=null)QueueStreamedModelMaterials(model);
      }
    }

    private float StreamRoomDistanceSquared(string roomName){
      if(String.IsNullOrWhiteSpace(roomName))return float.MaxValue;
      Room room=rooms.FirstOrDefault(r=>r!=null&&String.Equals(r.RoomName,roomName,StringComparison.OrdinalIgnoreCase));
      if(room!=null&&TryGetRoomStreamingBounds(room,out RoomStreamingBounds bounds))return StreamingBoundsDistanceSquared(bounds,camera.Position);
      if(streamRequestsByRoom.TryGetValue(roomName,out List<WorldModelStreamRequest> requests)&&requests!=null&&requests.Count>0)
        return requests.Min(x=>x==null?float.MaxValue:StreamRequestDistance(roomName,x.AssetId));
      return float.MaxValue;
    }

    private void RequestVisibleRoomMaterials(HashSet<string> visible,Room anchor,string activeSky){
      if(initialStreamLoading)return;
      MarkRoomMaterialDemand(anchor?.RoomName);
      if(visible==null||visible.Count==0)return;

      // Direct doorway neighbours need more than a tiny texture slice: otherwise their geometry is present but large
      // sections remain white/flat until the room becomes the anchor. Prewarm the two nearest direct cells strongly,
      // then use a wider bounded budget for the rest of the visible set.
      var prewarmed=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      foreach(string roomName in DirectStreamingNeighborRoomNames(anchor?.RoomName??String.Empty).OrderBy(StreamRoomDistanceSquared).Take(2)){
        if(String.IsNullOrWhiteSpace(roomName)||String.Equals(roomName,activeSky,StringComparison.OrdinalIgnoreCase))continue;
        MarkRoomMaterialDemand(roomName,160);prewarmed.Add(roomName);
      }
      foreach(string roomName in ActiveStreamTransitionRoomNames()){MarkRoomMaterialDemand(roomName,128);prewarmed.Add(roomName);}
      int budget=320;
      foreach(string roomName in visible
        .Where(name=>!String.IsNullOrWhiteSpace(name)
          &&!String.Equals(name,anchor?.RoomName,StringComparison.OrdinalIgnoreCase)
          &&!String.Equals(name,activeSky,StringComparison.OrdinalIgnoreCase)
          &&!String.Equals(name,"_everywhere_",StringComparison.OrdinalIgnoreCase)
          &&!prewarmed.Contains(name))
        .OrderBy(StreamRoomDistanceSquared)
        .ThenBy(name=>name,StringComparer.OrdinalIgnoreCase)){
        if(budget<=0)break;
        int slice=Math.Min(64,budget);
        MarkRoomMaterialDemand(roomName,slice);
        budget-=slice;
      }
    }

    private void RequestActiveSkyModels(AreaEnvironmentScheme env,WorldRenderSettings s){
      if(modelStreamer==null||s==null||!s.ShowSky||s.Mode==WorldRenderMode.Map||s.Mode==WorldRenderMode.Heightmap)return;
      string activeSky=ResolveSkyRoomName(env);
      if(String.IsNullOrWhiteSpace(activeSky)||String.Equals(activeSky,"_everywhere_",StringComparison.OrdinalIgnoreCase))return;

      // Sky rendering is independent of the camera-room resolver. Previously the sky only received a tiny one-shot
      // request during LoadModel(), so while FindCameraRoom was unresolved (or for sky assets beyond the hot slice)
      // decoded GR2s were never eligible for GPU upload and their MAT/DDS resources were never demanded. Keep the
      // active skyscene resident explicitly and let its textures refine behind structural room work.
      PinRoomStreamAssets(activeSky,-1);
      RequestRoomModels(activeSky,false,96,true);
      if(!initialStreamLoading)MarkRoomMaterialDemand(activeSky,96);
    }

    private void QueueNewlyDemandedRoomIntegrations(){
      foreach(string roomName in activeStreamRoomNames)if(!previousStreamRoomNames.Contains(roomName))QueueRoomStreamIntegrations(roomName);
      previousStreamRoomNames.Clear();previousStreamRoomNames.UnionWith(activeStreamRoomNames);
      foreach(string roomName in floorStreamRoomNames)if(!previousFloorStreamRoomNames.Contains(roomName))QueueRoomStreamFloorIntegrations(roomName);
      previousFloorStreamRoomNames.Clear();previousFloorStreamRoomNames.UnionWith(floorStreamRoomNames);
    }

    private void QueueRoomStreamFloorIntegrations(string roomName){
      if(String.IsNullOrWhiteSpace(roomName)||!streamRequestsByRoom.TryGetValue(roomName,out List<WorldModelStreamRequest> requests))return;
      if(!streamPlacementsByRoomAsset.TryGetValue(roomName,out Dictionary<ulong,List<(Room Room,AssetInstance Instance)>> roomPlacements))return;
      foreach(WorldModelStreamRequest request in requests){
        if(request==null||!models.TryGetValue(request.AssetId,out GR2 model)||model==null||!roomPlacements.TryGetValue(request.AssetId,out List<(Room Room,AssetInstance Instance)> local))continue;
        QueueStreamedFloorIntegration(request.AssetId,model,roomName,local);
      }
    }

    private void QueueRoomStreamIntegrations(string roomName){
      if(String.IsNullOrWhiteSpace(roomName)||!streamRequestsByRoom.TryGetValue(roomName,out List<WorldModelStreamRequest> requests))return;
      foreach(WorldModelStreamRequest request in requests)if(request!=null&&models.TryGetValue(request.AssetId,out GR2 model)&&model!=null)QueueStreamedModelIntegration(request.AssetId,model,roomName);
    }

    private void RequestSkyModels() {
      if(modelStreamer==null)return;
      // Opportunistic startup prefetch only. Runtime residency is owned by RequestActiveSkyModels(), which also
      // works before/without a stable camera-room result and requests the skyscene's material resources.
      foreach(string roomName in skyRoomNames) RequestRoomModels(roomName,false,4);
    }
    private Dictionary<string,float> SpatialBootstrapRoomDistances(Vector3 position,float radius){
      var distances=new Dictionary<string,float>(StringComparer.OrdinalIgnoreCase);if(streamBootstrapRoomsByCell.Count==0||radius<=0f)return distances;
      float radiusSquared=radius*radius;int minX=StreamBootstrapCell(position.X-radius),maxX=StreamBootstrapCell(position.X+radius),minZ=StreamBootstrapCell(position.Z-radius),maxZ=StreamBootstrapCell(position.Z+radius);
      for(int x=minX;x<=maxX;x++)for(int z=minZ;z<=maxZ;z++){
        if(!streamBootstrapRoomsByCell.TryGetValue((x,z),out HashSet<string> cellRooms))continue;
        float dx=(x+.5f)*StreamBootstrapCellSize-position.X,dz=(z+.5f)*StreamBootstrapCellSize-position.Z,distance=dx*dx+dz*dz;
        if(distance>radiusSquared)continue;
        foreach(string roomName in cellRooms)if(!String.IsNullOrWhiteSpace(roomName)&&(!distances.TryGetValue(roomName,out float oldDistance)||distance<oldDistance))distances[roomName]=distance;
      }
      return distances;
    }
    private void RequestSpatialBootstrapRooms(Vector3 position,Room anchor,float radius,int roomLimit){
      if(modelStreamer==null||streamBootstrapRoomsByCell.Count==0||roomLimit<=0||radius<=0f)return;
      Dictionary<string,float> distances=SpatialBootstrapRoomDistances(position,radius);if(distances.Count==0)return;
      var byName=rooms.Where(r=>r!=null&&!String.IsNullOrWhiteSpace(r.RoomName))
        .GroupBy(r=>r.RoomName,StringComparer.OrdinalIgnoreCase).ToDictionary(g=>g.Key,g=>g.First(),StringComparer.OrdinalIgnoreCase);
      bool weakAnchor=anchor==null||IsEverywhereRoom(anchor);
      if(!weakAnchor){if(!TryGetRoomStreamingBounds(anchor,out RoomStreamingBounds anchorBounds)||anchorBounds.Coarse)weakAnchor=true;}
      var ranked=distances.Where(x=>byName.ContainsKey(x.Key))
        .Select(x=>{
          Room room=byName[x.Key];bool have=TryGetRoomStreamingBounds(room,out RoomStreamingBounds b);
          bool contains=have&&ContainsStreamingBounds(b,position,12f);
          return (Room:room,Distance:x.Value,Contains:contains,Coarse:!have||b.Coarse);
        })
        .Where(x=>x.Room!=null&&!IsEverywhereRoom(x.Room)&&!skyRoomNames.Contains(x.Room.RoomName))
        // A nearby interior is especially valuable when the authoritative floor lookup still reports the outdoor cell
        // underneath it. Exact/containing rooms outrank origin-only hints; everything else remains normal prefetch.
        .OrderBy(x=>x.Contains?0:1).ThenBy(x=>weakAnchor&&!x.Room.OutdoorsVisible?0:1).ThenBy(x=>x.Coarse?1:0).ThenBy(x=>x.Distance)
        .ThenBy(x=>x.Room.RoomName,StringComparer.OrdinalIgnoreCase).Take(roomLimit).ToList();
      int urgentFallbacks=0;
      foreach(var item in ranked){
        bool urgent=weakAnchor&&item.Contains&&!item.Room.OutdoorsVisible&&urgentFallbacks<2;
        if(urgent)urgentFallbacks++;
        PinRoomStreamAssets(item.Room.RoomName,urgent?-1:160,urgent);
        RequestRoomModels(item.Room.RoomName,urgent,urgent?128:64,true);
      }
    }
    private Dictionary<ulong,float> SpatialBootstrapAssetDistances(Vector3 position,float radius){
      var distances=new Dictionary<ulong,float>();if(streamBootstrapAssetsByCell.Count==0||radius<=0f)return distances;
      float radiusSquared=radius*radius;int minX=StreamBootstrapCell(position.X-radius),maxX=StreamBootstrapCell(position.X+radius),minZ=StreamBootstrapCell(position.Z-radius),maxZ=StreamBootstrapCell(position.Z+radius);
      for(int x=minX;x<=maxX;x++)for(int z=minZ;z<=maxZ;z++){
        if(!streamBootstrapAssetsByCell.TryGetValue((x,z),out HashSet<ulong> cellAssets))continue;
        float dx=(x+.5f)*StreamBootstrapCellSize-position.X,dz=(z+.5f)*StreamBootstrapCellSize-position.Z,distance=dx*dx+dz*dz;
        if(distance>radiusSquared)continue;
        foreach(ulong assetId in cellAssets)if(!distances.TryGetValue(assetId,out float oldDistance)||distance<oldDistance)distances[assetId]=distance;
      }
      return distances;
    }

    private void RequestSpatialBootstrapModels(Vector3 position,float radius,int requestedLimit,bool highPriority){
      if(modelStreamer==null||streamBootstrapAssetsByCell.Count==0||requestedLimit<=0)return;
      // Do not rescan every placement here: Corellia has hundreds of thousands. A cell-centre distance is enough
      // to rank a 48-unit streaming cell and keeps this request path bounded to the nearby grid buckets.
      Dictionary<ulong,float> distances=SpatialBootstrapAssetDistances(position,radius);
      var ordered=distances.OrderBy(x=>x.Value).Select(x=>x.Key);
      IEnumerable<ulong> candidates;
      if(highPriority){
        int promotionLimit=Math.Min(16,Math.Max(1,requestedLimit/4));
        List<ulong> promotions=ordered.Where(id=>modelStreamer.IsNormalQueued(id)).Take(promotionLimit).ToList();
        candidates=promotions.Concat(ordered.Where(id=>!modelStreamer.IsKnown(id)).Take(Math.Max(0,requestedLimit-promotions.Count)));
      }else candidates=ordered.Where(id=>!modelStreamer.IsKnown(id)).Take(requestedLimit);
      foreach(ulong assetId in candidates)if(streamRequestsByAsset.TryGetValue(assetId,out WorldModelStreamRequest request)){if(highPriority){activeStreamAssetIds.Add(assetId);urgentStreamAssetIds.Add(assetId);}modelStreamer.Request(request,highPriority);}
    }
    private void RequestCameraRoomModels(Room room,bool highPriority) {
      if(room==null)return;RequestRoomModels(room.RoomName,highPriority);
      // Direct neighbours are speculative but persistent demand. The old path queued them as low priority and then
      // CancelQueuedExcept() removed them in the very same frame because only high-priority IDs entered activeStreamAssetIds.
      foreach(string neighbour in StreamingNeighborRoomNames(room.RoomName,32))RequestRoomModels(neighbour,false,96,true);
    }
    private bool IsStreamAssetActivelyDemanded(ulong assetId) {
      if(activeStreamAssetIds.Contains(assetId)||(initialStreamLoading&&initialStreamAssetIds.Contains(assetId)))return true;
      if(!streamPlacementsByAsset.TryGetValue(assetId,out List<(Room Room,AssetInstance Instance)> placements)||placements==null)return false;
      // activeStreamRoomNames is the semantic residency set (camera, visible neighbours, transition rooms and
      // camera-ahead rooms).  A bounded hot asset slice controls queue priority only; it must not make another
      // decoded asset from the same still-active room ineligible for GPU residency.
      foreach(var placement in placements)
        if(placement.Room!=null&&activeStreamRoomNames.Contains(placement.Room.RoomName))return true;
      return false;
    }

    private void PinRoomStreamAssets(string roomName,int limit,bool urgent=false) {
      if(String.IsNullOrWhiteSpace(roomName)||!streamRequestsByRoom.TryGetValue(roomName,out List<WorldModelStreamRequest> requests))return;
      IEnumerable<WorldModelStreamRequest> ordered=requests.OrderBy(x=>StreamRequestDistance(roomName,x.AssetId));
      if(limit>=0)ordered=ordered.Take(limit);
      foreach(WorldModelStreamRequest request in ordered){
        if(request==null)continue;
        activeStreamAssetIds.Add(request.AssetId);if(urgent)urgentStreamAssetIds.Add(request.AssetId);
        // A model may already be decoded because it belonged to a neighbour earlier, while its D3D upload was
        // intentionally dropped after that room fell out of the hot set. Re-pinning the room must also re-arm the
        // GPU upload; otherwise the GR2 stays "known" forever and the corresponding wall/floor never appears.
        if(models.TryGetValue(request.AssetId,out GR2 decodedModel)&&decodedModel!=null&&!modelGeometryPrepared.Contains(decodedModel))
          QueueModelGpuUpload(decodedModel);
      }
    }

    private void PinVisibleRoomStreamAssets(string roomName,int hotLimit,int recoveryLimit=32){
      PinRoomStreamAssets(roomName,hotLimit);
      if(String.IsNullOrWhiteSpace(roomName)||recoveryLimit<=0||!streamRequestsByRoom.TryGetValue(roomName,out List<WorldModelStreamRequest> requests))return;

      // A normal-priority neighbour can contain more assets than its bounded hot slice. Once asset 65+ had decoded,
      // it became "known", stopped appearing in RequestRoomModels() candidates, and was therefore no longer in
      // activeStreamAssetIds when its GPU upload was processed. The upload was dropped every frame until that room
      // became the anchor. Keep a small rolling recovery set for decoded-but-not-yet-uploaded visible assets so a
      // stationary doorway view converges instead of requiring the user to enter/leave the room to kick it again.
      int recovered=0;
      foreach(WorldModelStreamRequest request in requests.OrderBy(x=>StreamRequestDistance(roomName,x.AssetId))){
        if(request==null||!models.TryGetValue(request.AssetId,out GR2 model)||model==null||modelGeometryPrepared.Contains(model))continue;
        activeStreamAssetIds.Add(request.AssetId);
        QueueModelGpuUpload(model);
        if(++recovered>=recoveryLimit)break;
      }
    }
    private void RequestRoomModels(string roomName,bool highPriority,int requestedLimit=-1,bool keepQueued=false) {
      if(modelStreamer==null||String.IsNullOrWhiteSpace(roomName)||!streamRequestsByRoom.TryGetValue(roomName,out List<WorldModelStreamRequest> requests))return;
      if(highPriority||keepQueued)activeStreamRoomNames.Add(roomName);
      // A connected room needs its shell, floor and doorway pieces together.  The earlier 12/3
      // slice could consume the complete in-flight budget with props and leave a hangar without
      // a floor. This stays memory-safe: the streamer has a bounded work set and View_AREA
      // evicts cold CPU/GPU residency independently.
      int limit=requestedLimit>=0?requestedLimit:(highPriority?64:12);
      IEnumerable<WorldModelStreamRequest> ordered=requests.OrderBy(x=>StreamRequestDistance(roomName,x.AssetId));
      // Keep a small promotion slice, then fill the remaining budget exclusively with unknown assets.  The old
      // `Take(limit)` over all high-priority entries kept returning the same already-decoded nearest 64 forever;
      // the rest of a hangar therefore never entered the decoder at all.
      IEnumerable<WorldModelStreamRequest> candidates;
      if(highPriority){
        int promotionLimit=Math.Min(12,Math.Max(1,limit/4));
        // Promote requests that are actually waiting in the normal band. Using IsKnown() here selected already
        // decoded/worker-owned nearest assets over and over, so the remainder of a neighbour that became current
        // could stay low-priority for seconds.
        List<WorldModelStreamRequest> promotions=ordered.Where(x=>modelStreamer.IsNormalQueued(x.AssetId)).Take(promotionLimit).ToList();
        candidates=promotions.Concat(ordered.Where(x=>!modelStreamer.IsKnown(x.AssetId)).Take(Math.Max(0,limit-promotions.Count)));
      }else candidates=ordered.Where(x=>!modelStreamer.IsKnown(x.AssetId)).Take(limit);
      foreach(WorldModelStreamRequest request in candidates){if(highPriority||keepQueued)activeStreamAssetIds.Add(request.AssetId);if(highPriority)urgentStreamAssetIds.Add(request.AssetId);modelStreamer.Request(request,highPriority);}
    }
    private float StreamRequestDistance(string roomName,ulong assetId) {
      if(String.IsNullOrWhiteSpace(roomName)||!streamRoomAssetHints.TryGetValue(roomName,out Dictionary<ulong,StreamRoomAssetHint> roomHints)||!roomHints.TryGetValue(assetId,out StreamRoomAssetHint hint)||hint==null||!hint.Any)return float.MaxValue;
      return PointAabbDistanceSquared(camera.Position,hint.Min,hint.Max);
    }
    private void RequestVisibleRoomModels(HashSet<string> visible,Room cameraRoom,AreaEnvironmentScheme env,WorldRenderSettings s) {
      if(modelStreamer==null||s?.Mode==WorldRenderMode.Map)return;
      // GR2 floors/bounds arrive lazily. Use a separate conservative placement-bounds anchor until the authoritative
      // room locator has enough geometry, rather than waiting for camera motion to discover the spawn/next room.
      Room anchor=cameraRoom!=null&&!IsEverywhereRoom(cameraRoom)?cameraRoom:displayCameraRoom;
      anchor=ResolveStreamingRoom(camera.Position,anchor);
      activeStreamAssetIds.Clear();urgentStreamAssetIds.Clear();materialStreamAssetIds.Clear();activeStreamRoomNames.Clear();floorStreamRoomNames.Clear();
      if(anchor!=null&&!IsEverywhereRoom(anchor)&&!skyRoomNames.Contains(anchor.RoomName))floorStreamRoomNames.Add(anchor.RoomName);
      Room phaseRoom=FindPhaseTriggerRoom(camera.Position);
      bool havePhaseRoom=phaseRoom!=null&&!IsEverywhereRoom(phaseRoom)&&!skyRoomNames.Contains(phaseRoom.RoomName);
      if(havePhaseRoom)floorStreamRoomNames.Add(phaseRoom.RoomName);
      // The authoritative floor room and the tightest streamed-bounds room are deliberately separate signals.
      // A broad Corellia spaceport floor can keep winning FindCameraRoom() while the camera has already entered a
      // nested hangar/corridor whose collision has not been streamed yet. If only the authoritative room is demanded,
      // that inner room falls out of residency after the transition grace expires and can never contribute the floor
      // that would make it authoritative: a streaming chicken-and-egg loop. Jedipedia builds dPVS/floor membership
      // from all room detail up front; while PugTools streams it, keep the best spatial candidate alive in parallel.
      Room spatialRoom=FindStreamingRoom(camera.Position,160f);
      bool haveSpatialRoom=spatialRoom!=null&&!IsEverywhereRoom(spatialRoom)&&!skyRoomNames.Contains(spatialRoom.RoomName)
        &&!String.Equals(spatialRoom.RoomName,anchor?.RoomName,StringComparison.OrdinalIgnoreCase);
      if(haveSpatialRoom)floorStreamRoomNames.Add(spatialRoom.RoomName);
      // A phase gateway can be the only semantic evidence that the camera is inside a class/story hangar while the
      // generic spaceport floor underneath still wins FindCameraRoom(). The previous transition grace kept the hangar
      // alive for ~6 seconds, then it disappeared exactly as reported. Treat the phase owner as persistent demand for
      // as long as the camera remains inside the authored trigger, not as a temporary hand-off room.
      if(havePhaseRoom){PinRoomStreamAssets(phaseRoom.RoomName,-1,true);RequestRoomModels(phaseRoom.RoomName,true,256,true);}
      if(haveSpatialRoom){PinRoomStreamAssets(spatialRoom.RoomName,-1,true);RequestRoomModels(spatialRoom.RoomName,true,256,true);}
      // The startup shell is an immutable demand set. Keep requesting it even if partially decoded floors cause the
      // authoritative camera-room resolver to oscillate between an overlapping base room and the phase interior.
      if(initialStreamLoading)RequestInitialStreamWorkingSet();
      else if(UseInitialStreamVisibilityGrace())foreach(string roomName in initialStreamRoomNames)RequestRoomModels(roomName,false,64,true);
      PinRoomStreamAssets(anchor?.RoomName,-1,true);
      var directNeighbourSet=new HashSet<string>(DirectStreamingNeighborRoomNames(anchor?.RoomName??String.Empty),StringComparer.OrdinalIgnoreCase);
      if(havePhaseRoom)foreach(string neighbour in DirectStreamingNeighborRoomNames(phaseRoom.RoomName))directNeighbourSet.Add(neighbour);
      if(haveSpatialRoom)foreach(string neighbour in DirectStreamingNeighborRoomNames(spatialRoom.RoomName))directNeighbourSet.Add(neighbour);
      List<string> directNeighbours=directNeighbourSet
        .Where(name=>!String.Equals(name,"_everywhere_",StringComparison.OrdinalIgnoreCase)&&!skyRoomNames.Contains(name)
          &&!String.Equals(name,anchor?.RoomName,StringComparison.OrdinalIgnoreCase)
          &&(!havePhaseRoom||!String.Equals(name,phaseRoom.RoomName,StringComparison.OrdinalIgnoreCase))
          &&(!haveSpatialRoom||!String.Equals(name,spatialRoom.RoomName,StringComparison.OrdinalIgnoreCase)))
        .OrderBy(StreamRoomDistanceSquared).ThenBy(name=>name,StringComparer.OrdinalIgnoreCase).Take(6).ToList();
      // Build collision for the nearest doorway cells before the camera crosses into them. Previously only the
      // authoritative current room had floor indexing, so Walking Mode could enter a fully visible neighbour one
      // frame before its collision triangles existed and start falling through the spaceport floor.
      foreach(string floorNeighbour in directNeighbours.Take(2))floorStreamRoomNames.Add(floorNeighbour);
      for(int i=0;i<directNeighbours.Count;i++)PinRoomStreamAssets(directNeighbours[i],i<2?-1:192);
      foreach(string roomName in ActiveStreamTransitionRoomNames())PinRoomStreamAssets(roomName,-1);
      if(visible!=null)foreach(string roomName in visible)if(!String.Equals(roomName,anchor?.RoomName,StringComparison.OrdinalIgnoreCase)&&!directNeighbours.Contains(roomName,StringComparer.OrdinalIgnoreCase))PinVisibleRoomStreamAssets(roomName,96,48);

      // Semantic room demand must get queue admission before the physical bootstrap. During the first-frame geometry
      // barrier MAT/DDS work stays deferred; after that visible neighbours receive a bounded refinement budget too.
      RequestCameraRoomModels(anchor,true);
      for(int i=0;i<directNeighbours.Count;i++)RequestRoomModels(directNeighbours[i],false,i<2?192:128,true);
      foreach(string roomName in ActiveStreamTransitionRoomNames())RequestRoomModels(roomName,false,128,true);
      if(visible!=null)foreach(string roomName in visible) {
        bool isAnchor=String.Equals(roomName,anchor?.RoomName,StringComparison.OrdinalIgnoreCase)
          ||(haveSpatialRoom&&String.Equals(roomName,spatialRoom.RoomName,StringComparison.OrdinalIgnoreCase))
          ||(havePhaseRoom&&String.Equals(roomName,phaseRoom.RoomName,StringComparison.OrdinalIgnoreCase));
        bool sharedBackground=String.Equals(roomName,"_everywhere_",StringComparison.OrdinalIgnoreCase);
        bool direct=directNeighbours.Contains(roomName,StringComparer.OrdinalIgnoreCase);
        // Current room owns the high band. Direct/visible neighbours stay persistent in the normal band so they
        // prefetch continuously without taking decoder slots away from missing current-room shell pieces.
        RequestRoomModels(roomName,isAnchor,sharedBackground?12:(isAnchor?192:(direct?128:96)),!isAnchor&&!sharedBackground);
      }
      string activeSky=ResolveSkyRoomName(env);
      RequestVisibleRoomMaterials(visible,anchor,activeSky);
      RequestCameraAheadModels(anchor);
      RequestActiveSkyModels(env,s);
      // The old bootstrap requested nearby asset IDs without demanding their owner rooms. Those GR2s could decode and
      // upload successfully yet never receive render placements, while the same rolling asset slice also displaced
      // semantic neighbour work from the high queue. Resolve physical proximity to room owners instead.
      bool needWideBootstrap=anchor==null||IsEverywhereRoom(anchor);
      if(!needWideBootstrap){if(!TryGetRoomStreamingBounds(anchor,out RoomStreamingBounds anchorStreamingBounds)||anchorStreamingBounds.Coarse)needWideBootstrap=true;}
      RequestSpatialBootstrapRooms(camera.Position,anchor,needWideBootstrap?380f:300f,needWideBootstrap?16:12);
      QueueNewlyDemandedRoomIntegrations();

      if(initialStreamLoading)activeStreamAssetIds.UnionWith(initialStreamAssetIds);
      // Cancellation is queue backpressure, not residency eviction. Preserve every already-admitted request whose room
      // is still part of current/neighbour/prediction demand, even when that asset sits outside the bounded hot pin slice.
      queuedStreamDemandAssetIds.Clear();queuedStreamDemandAssetIds.UnionWith(activeStreamAssetIds);
      foreach(string roomName in activeStreamRoomNames)if(streamRequestsByRoom.TryGetValue(roomName,out List<WorldModelStreamRequest> demandedRequests))
        foreach(WorldModelStreamRequest request in demandedRequests)if(request!=null)queuedStreamDemandAssetIds.Add(request.AssetId);
      if(initialStreamLoading)queuedStreamDemandAssetIds.UnionWith(initialStreamAssetIds);
      modelStreamer.CancelQueuedExcept(queuedStreamDemandAssetIds);
    }
    private void RequestCameraAheadModels(Room cameraRoom) {
      // Keep a persistent multi-distance forward cone. A single 120-unit sample often jumped over Corellia's narrow
      // transition cell or selected the outdoor room beneath an elevated hangar, so the real next room did not become
      // urgent until after the camera crossed the threshold.
      Vector3 look=-camera.Look;look.Y=0;if(look.LengthSquared()<.0001f)return;look.Normalize();
      bool moved=(camera.Position-streamPrefetchAnchor).LengthSquared()>36f;
      bool turned=streamPrefetchLook.LengthSquared()<.0001f||Vector3.Dot(look,streamPrefetchLook)<.96f;
      bool roomChanged=!String.Equals(streamPrefetchRoom,cameraRoom?.RoomName,StringComparison.OrdinalIgnoreCase);
      if(moved||turned||roomChanged||cameraAheadStreamRooms.Count==0){
        streamPrefetchAnchor=camera.Position;streamPrefetchLook=look;streamPrefetchRoom=cameraRoom?.RoomName??String.Empty;
        cameraAheadStreamRooms.Clear();urgentCameraAheadStreamRooms.Clear();
        void AddAhead(string roomName,int limit,bool urgent=false){
          if(String.IsNullOrWhiteSpace(roomName)||String.Equals(roomName,"_everywhere_",StringComparison.OrdinalIgnoreCase)||skyRoomNames.Contains(roomName))return;
          if(cameraAheadStreamRooms.TryGetValue(roomName,out int oldLimit))cameraAheadStreamRooms[roomName]=Math.Max(oldLimit,limit);else cameraAheadStreamRooms[roomName]=limit;
          if(urgent)urgentCameraAheadStreamRooms.Add(roomName);
        }
        Room savedCurrent=currentCameraRoom,savedDisplay=displayCameraRoom;
        foreach(float distance in new[]{60f,120f,220f}){
          Vector3 predicted=camera.Position+look*distance;Room predictedRoom=FindCameraRoom(predicted);currentCameraRoom=savedCurrent;displayCameraRoom=savedDisplay;predictedRoom=ResolveStreamingRoom(predicted,predictedRoom);
          if(predictedRoom==null)continue;
          bool nextRoom=!String.Equals(predictedRoom.RoomName,cameraRoom?.RoomName,StringComparison.OrdinalIgnoreCase);
          AddAhead(predictedRoom.RoomName,distance<=120f?256:160,nextRoom&&distance<=120f);
          foreach(string neighbour in DirectStreamingNeighborRoomNames(predictedRoom.RoomName).OrderBy(StreamRoomDistanceSquared).Take(4))AddAhead(neighbour,distance<=120f?96:64);
        }
        currentCameraRoom=savedCurrent;displayCameraRoom=savedDisplay;
        var candidates=rooms.Where(r=>r!=null&&!IsEverywhereRoom(r)&&!skyRoomNames.Contains(r.RoomName)&&TryGetRoomStreamingBounds(r,out _))
          .Select(r=>{TryGetRoomStreamingBounds(r,out RoomStreamingBounds b);Vector3 delta=b.Center-camera.Position;float length=delta.Length();float distance=Math.Max(0f,length-Math.Max(0f,b.Radius));float forward=length<.0001f?1f:Vector3.Dot(delta/Math.Max(.0001f,length),look);return (Room:r,Distance:distance,Forward:forward);})
          .Where(x=>x.Distance<360f&&x.Forward>.05f).OrderByDescending(x=>x.Forward).ThenBy(x=>x.Distance).Take(10);
        foreach(var candidate in candidates)AddAhead(candidate.Room.RoomName,64);
      }
      foreach(KeyValuePair<string,int> pair in cameraAheadStreamRooms){
        bool urgent=urgentCameraAheadStreamRooms.Contains(pair.Key);
        if(urgent)floorStreamRoomNames.Add(pair.Key);
        if(urgent)PinRoomStreamAssets(pair.Key,-1,true);else PinRoomStreamAssets(pair.Key,Math.Min(192,pair.Value));
        RequestRoomModels(pair.Key,urgent,urgent?Math.Max(256,pair.Value):pair.Value,true);
      }
    }

    private void PumpDecodedWorldModels() {
      if(modelStreamer==null)return;
      // While the loading overlay is up there is no benefit in stretching a completed spawn shell over dozens of
      // presentation frames. Drain a larger local burst; normal runtime goes back to a tight frame-time budget.
      bool decodeBacklog=!initialStreamLoading&&modelStreamer.OutstandingCount>64;
      int maxModels=initialStreamLoading?80:(decodeBacklog?64:32);
      double maxMilliseconds=initialStreamLoading?16.0:(decodeBacklog?11.0:6.5);
      var budget=System.Diagnostics.Stopwatch.StartNew();int count=0;
      while(count<maxModels&&(count==0||budget.Elapsed.TotalMilliseconds<maxMilliseconds)&&modelStreamer.TryDequeue(out WorldModelStreamResult result)) {
        count++;
        if(result==null)continue;
        try{
          if(result.Model==null) { if(result.Error!=null)System.Diagnostics.Debug.WriteLine("World stream decode failed: "+result.Error.Message); continue; }
          if(models.ContainsKey(result.Request.AssetId))continue;
          models[result.Request.AssetId]=result.Model;streamedWorldModels.Add(result.Model);streamedModelAssetIds[result.Model]=result.Request.AssetId;
          RegisterStreamedModelMaterials(result.Model);
          // A decoded shared model is integrated only into rooms that currently demand it. Corellia has ~345k model
          // placements but only ~1.6k unique GR2s; integrating every planet-wide placement here was the reason rooms
          // visibly assembled for tens of seconds even though their GR2 had already finished decoding.
          QueueStreamedModelIntegrationsForDemand(result.Request.AssetId,result.Model);
          if(IsStreamAssetActivelyDemanded(result.Request.AssetId))QueueModelGpuUpload(result.Model);
          // InitialStreamAssetIds is a geometry barrier, not a blanket full-DDS barrier. Only the explicitly selected
          // texture slice/current room enters the material queue; otherwise startup I/O competes with the missing shell.
          if(materialStreamAssetIds.Contains(result.Request.AssetId))QueueStreamedModelMaterials(result.Model);
          if(streamedWorldModels.Count>384)TrimStreamedModelCpuResidency();
        } finally {
          // State 3 means only "worker finished". Publish state 5 after the result has actually been installed so the
          // first-frame gate can never mistake a completed-queue entry for a display-ready model.
          modelStreamer.MarkResultConsumed(result.Request.AssetId);
        }
      }
      FinalizeReadyStreamRoomVisibilityBounds();
    }

    private void RegisterStreamedModelMaterials(GR2 model){
      var seen=new HashSet<GR2>();
      void Add(GR2 current){
        if(current==null||!seen.Add(current))return;
        if(current.materials!=null)for(int i=0;i<current.materials.Count;i++){
          GR2_Material material=current.materials[i];if(material==null||String.IsNullOrWhiteSpace(material.materialName))continue;
          if(materials.TryGetValue(material.materialName,out GR2_Material shared)&&shared!=null){
            if(!shared.parsed&&material.parsed)materials[material.materialName]=material;
            else current.materials[i]=material=shared;
          }else materials[material.materialName]=material;
          streamedWorldMaterials.Add(material);
        }
        if(current.attachedModels!=null)foreach(GR2 attached in current.attachedModels)Add(attached);
      }
      Add(model);
    }

    private void QueueStreamedModelIntegrationsForDemand(ulong assetId,GR2 model){
      if(model==null)return;
      var demand=new HashSet<string>(activeStreamRoomNames,StringComparer.OrdinalIgnoreCase);if(initialStreamLoading)demand.UnionWith(initialStreamRoomNames);
      foreach(string roomName in demand)if(streamPlacementsByRoomAsset.TryGetValue(roomName,out Dictionary<ulong,List<(Room Room,AssetInstance Instance)>> byAsset)&&byAsset.ContainsKey(assetId))QueueStreamedModelIntegration(assetId,model,roomName);
    }

    private void QueueStreamedModelIntegration(ulong assetId,GR2 model,string roomName){
      if(model==null||String.IsNullOrWhiteSpace(roomName)||!streamPlacementsByRoomAsset.TryGetValue(roomName,out Dictionary<ulong,List<(Room Room,AssetInstance Instance)>> byAsset)||!byAsset.TryGetValue(assetId,out List<(Room Room,AssetInstance Instance)> roomPlacements)||roomPlacements.Count==0)return;
      var key=(assetId,roomName);
      // Visible world placements go first. Hidden/editor-only copies still enter the index for the optional utility
      // view, but must never consume the bounded commit slice ahead of the shell the player is waiting to see.
      List<(Room Room,AssetInstance Instance)> renderPlacements=roomPlacements.Where(x=>!streamIndexedInstances.Contains(x.Instance))
        .OrderBy(x=>InstanceVisibleInWorld(x.Instance)?0:1).ToList();
      if(renderPlacements.Count>0&&queuedStreamedModelIntegrations.Add(key))pendingStreamedModelIntegrations.Enqueue(new PendingStreamedModelIntegration{AssetId=assetId,RoomName=roomName,Model=model,Placements=renderPlacements});
      if(floorStreamRoomNames.Contains(roomName))QueueStreamedFloorIntegration(assetId,model,roomName,roomPlacements);
    }

    private void QueueStreamedFloorIntegration(ulong assetId,GR2 model,string roomName,List<(Room Room,AssetInstance Instance)> placements){
      if(model==null||placements==null||placements.Count==0||String.IsNullOrWhiteSpace(roomName))return;
      List<(Room Room,AssetInstance Instance)> local=placements.Where(x=>x.Instance!=null&&!streamFloorIndexedInstances.Contains(x.Instance)).ToList();if(local.Count==0)return;
      var key=(assetId,roomName);if(!queuedStreamedFloorIntegrations.Add(key))return;
      pendingStreamedFloorIntegrations.Enqueue(new PendingStreamedFloorIntegration{AssetId=assetId,RoomName=roomName,Model=model,Placements=local});
    }

    private bool IsUrgentStreamRoom(string roomName){
      return !String.IsNullOrWhiteSpace(roomName)&&(floorStreamRoomNames.Contains(roomName)||(initialStreamLoading&&initialStreamRoomNames.Contains(roomName)));
    }

    private PendingStreamedModelIntegration DequeueNextStreamedModelIntegration(){
      int scan=pendingStreamedModelIntegrations.Count;
      for(int i=0;i<scan;i++){
        PendingStreamedModelIntegration pending=pendingStreamedModelIntegrations.Dequeue();
        if(pending==null)continue;
        if(IsUrgentStreamRoom(pending.RoomName))return pending;
        pendingStreamedModelIntegrations.Enqueue(pending);
      }
      return pendingStreamedModelIntegrations.Count>0?pendingStreamedModelIntegrations.Dequeue():null;
    }

    private void ProcessStreamedModelIntegrations(){
      bool integrationBacklog=!initialStreamLoading&&pendingStreamedModelIntegrations.Count>24;
      int maxPlacements=initialStreamLoading?3072:(integrationBacklog?2048:768);double maxMilliseconds=initialStreamLoading?12.0:(integrationBacklog?8.0:5.0);
      var budget=System.Diagnostics.Stopwatch.StartNew();int count=0;
      while(count<maxPlacements&&pendingStreamedModelIntegrations.Count>0&&(count==0||budget.Elapsed.TotalMilliseconds<maxMilliseconds)){
        PendingStreamedModelIntegration pending=DequeueNextStreamedModelIntegration();var key=(pending?.AssetId??0UL,pending?.RoomName??String.Empty);
        if(pending?.Model==null||!streamedWorldModels.Contains(pending.Model)||pending.Placements==null){queuedStreamedModelIntegrations.Remove(key);continue;}
        // Do not spend the bounded commit budget finishing a room that has already fallen out of the camera/prefetch
        // working set. Its already-added entries are harmless; the remaining slice is rebuilt when that room returns.
        bool demanded=activeStreamRoomNames.Contains(pending.RoomName)||(initialStreamLoading&&initialStreamRoomNames.Contains(pending.RoomName));
        if(!demanded){queuedStreamedModelIntegrations.Remove(key);continue;}
        if(pending.NextPlacement>=pending.Placements.Count){queuedStreamedModelIntegrations.Remove(key);continue;}
        AddStreamedModelRenderPlacement(pending.Model,pending.Placements[pending.NextPlacement++]);count++;
        if(pending.NextPlacement<pending.Placements.Count)pendingStreamedModelIntegrations.Enqueue(pending);else queuedStreamedModelIntegrations.Remove(key);
      }
    }

    private bool PriorityFloorStreamRoom(string roomName){
      if(String.IsNullOrWhiteSpace(roomName))return false;
      // floorStreamRoomNames is rebuilt every frame from the camera, phase, spatial and immediate-doorway demand.
      // Prioritizing that exact set prevents a newly entered nested cell from waiting behind cold background floors.
      if(floorStreamRoomNames.Contains(roomName))return true;
      if(String.Equals(roomName,currentCameraRoom?.RoomName,StringComparison.OrdinalIgnoreCase)||String.Equals(roomName,displayCameraRoom?.RoomName,StringComparison.OrdinalIgnoreCase))return true;
      return urgentCameraAheadStreamRooms.Contains(roomName);
    }

    private PendingStreamedFloorIntegration DequeueNextStreamedFloorIntegration(){
      int scan=pendingStreamedFloorIntegrations.Count;
      for(int i=0;i<scan;i++){
        PendingStreamedFloorIntegration pending=pendingStreamedFloorIntegrations.Dequeue();
        if(pending==null)continue;
        if(PriorityFloorStreamRoom(pending.RoomName))return pending;
        pendingStreamedFloorIntegrations.Enqueue(pending);
      }
      return pendingStreamedFloorIntegrations.Count>0?pendingStreamedFloorIntegrations.Dequeue():null;
    }

    private void ProcessStreamedFloorIntegrations(){
      bool floorBacklog=!initialStreamLoading&&pendingStreamedFloorIntegrations.Count>12;
      int maxPlacements=initialStreamLoading?640:(floorBacklog?320:128);int maxNewFloorModels=initialStreamLoading?20:(floorBacklog?12:6);double maxMilliseconds=initialStreamLoading?15.0:(floorBacklog?9.0:5.5);
      var budget=System.Diagnostics.Stopwatch.StartNew();int count=0,newFloorModels=0;
      while(count<maxPlacements&&pendingStreamedFloorIntegrations.Count>0&&(count==0||budget.Elapsed.TotalMilliseconds<maxMilliseconds)){
        PendingStreamedFloorIntegration pending=DequeueNextStreamedFloorIntegration();var key=(pending?.AssetId??0UL,pending?.RoomName??String.Empty);
        if(pending?.Model==null||!streamedWorldModels.Contains(pending.Model)||pending.Placements==null){queuedStreamedFloorIntegrations.Remove(key);continue;}
        bool demanded=floorStreamRoomNames.Contains(pending.RoomName);
        if(!demanded){queuedStreamedFloorIntegrations.Remove(key);continue;}
        if(pending.NextPlacement>=pending.Placements.Count){queuedStreamedFloorIntegrations.Remove(key);continue;}
        bool needsFloorBuild=!modelFloorData.ContainsKey(pending.Model);
        if(needsFloorBuild&&newFloorModels>=maxNewFloorModels){pendingStreamedFloorIntegrations.Enqueue(pending);break;}
        AddStreamedModelFloorPlacement(pending.Model,pending.Placements[pending.NextPlacement++]);count++;
        if(needsFloorBuild)newFloorModels++;
        if(pending.NextPlacement<pending.Placements.Count)pendingStreamedFloorIntegrations.Enqueue(pending);else queuedStreamedFloorIntegrations.Remove(key);
      }
    }

    private void AddStreamedModelRenderPlacement(GR2 model,(Room Room,AssetInstance Instance) placement){
      Room room=placement.Room;AssetInstance inst=placement.Instance;
      if(room==null||inst==null)return;
      if(streamIndexedInstances.Add(inst)&&InstanceCanEnterRenderIndex(inst)){
        Matrix world=InstanceWorld(inst,room);Vector3 center;float radius;
        if(!TryModelSphere(model,world,out center,out radius)){center=new Vector3(world.M41,world.M42,world.M43);radius=0f;}
        if(IsFinite(center)){
          var entry=new RenderEntry{Room=room,Instance=inst,Model=model,World=world,Center=center,Radius=radius,Kind=RenderKindModel};
          if(!renderEntriesByRoom.TryGetValue(room.RoomName,out List<RenderEntry> roomEntries))renderEntriesByRoom[room.RoomName]=roomEntries=new List<RenderEntry>();roomEntries.Add(entry);
          if(inst.PathFollowerAnimated){renderGlobal.Add(entry);walkingPathFollowerRenderEntries.Add(entry);}
          else if(entry.Radius>RenderIndexedRadiusLimit)renderGlobal.Add(entry);
          else {var key=(RenderCell(entry.Center.X),RenderCell(entry.Center.Z));if(!renderGrid.TryGetValue(key,out List<RenderEntry> bucket))renderGrid[key]=bucket=new List<RenderEntry>();bucket.Add(entry);}
        }
      }
    }

    private void AddStreamedModelFloorPlacement(GR2 model,(Room Room,AssetInstance Instance) placement){
      Room room=placement.Room;AssetInstance inst=placement.Instance;
      if(room==null||inst==null||inst.PathFollowerBoundaryExcluded||inst.hasHeightMap||inst.hasWater||IsSpeedTreeInstance(inst)||!streamFloorIndexedInstances.Add(inst))return;
      ModelFloorData floor=GetOrBuildModelFloorData(model,inst);if(floor==null||floor.Meshes.Count==0)return;
      Matrix floorWorld=InstanceWorld(inst,room),inverse;try{inverse=Matrix.Invert(floorWorld);}catch{return;}
      if(!TryModelWorldXZBounds(model,floorWorld,out float minX,out float maxX,out float minZ,out float maxZ))return;
      var floorEntry=new ModelFloorPlacementEntry{Room=room,Model=floor,World=floorWorld,Inverse=inverse,WorldMinX=minX,WorldMaxX=maxX,WorldMinZ=minZ,WorldMaxZ=maxZ,LocalXZIndependentOfY=Math.Abs(inverse.M21)<.000001f&&Math.Abs(inverse.M23)<.000001f};
      int cminX=ModelFloorPlacementCell(minX),cmaxX=ModelFloorPlacementCell(maxX),cminZ=ModelFloorPlacementCell(minZ),cmaxZ=ModelFloorPlacementCell(maxZ);long cells=(long)(cmaxX-cminX+1)*(cmaxZ-cminZ+1);
      if(cells<=0||cells>MaxModelFloorPlacementCells){modelFloorPlacementGlobal.Add(floorEntry);return;}
      for(int z=cminZ;z<=cmaxZ;z++)for(int x=cminX;x<=cmaxX;x++){var key=(x,z);if(!modelFloorPlacementGrid.TryGetValue(key,out List<ModelFloorPlacementEntry> bucket))modelFloorPlacementGrid[key]=bucket=new List<ModelFloorPlacementEntry>();bucket.Add(floorEntry);}
    }

    private void QueueModelGpuUpload(GR2 model) { if(model!=null&&queuedModelGpuUploads.Add(model))pendingModelGpuUploads.Enqueue(model); }
    private void QueueStreamedModelMaterials(GR2 model) {
      if(model==null||!queuedStreamedModelMaterials.Add(model))return;
      var queuedMaterials=new Queue<GR2_Material>();var seenModels=new HashSet<GR2>();var seenMaterials=new HashSet<GR2_Material>();
      void Add(GR2 current){
        if(current==null||!seenModels.Add(current))return;
        if(current.materials!=null)foreach(GR2_Material material in current.materials)if(material!=null&&seenMaterials.Add(material)&&!streamedMaterialResourcesPrepared.Contains(material)&&pendingStreamedMaterialPrepares.Add(material)){queuedMaterials.Enqueue(material);if(!material.parsed)materialMetadataStreamer?.Request(material);}
        if(current.attachedModels!=null)foreach(GR2 attached in current.attachedModels)Add(attached);
      }
      Add(model);
      if(queuedMaterials.Count==0){queuedStreamedModelMaterials.Remove(model);streamedModelMaterialsReady.Add(model);return;}
      streamedModelMaterialsReady.Remove(model);
      pendingStreamedModelMaterials.Enqueue(new PendingStreamedModelMaterials{Model=model,Materials=queuedMaterials});
    }
    private void ProcessStreamedModelMaterials() {
      // MAT XML/archive parsing is a separate background queue now. This pass performs only D3D texture creation
      // for metadata that is already stable, so a single cold MAT cannot block the render thread at a doorway.
      bool materialBacklog=pendingStreamedModelMaterials.Count>8;
      int maxMaterialPrepares=materialBacklog?24:8;
      double maxMaterialMilliseconds=materialBacklog?10.0:5.0;
      var budget=System.Diagnostics.Stopwatch.StartNew();int count=0,inspected=0,maxInspect=Math.Min(80,Math.Max(maxMaterialPrepares*4,pendingStreamedModelMaterials.Count));
      while(count<maxMaterialPrepares&&pendingStreamedModelMaterials.Count>0&&inspected<maxInspect&&(count==0||budget.Elapsed.TotalMilliseconds<maxMaterialMilliseconds)){
        PendingStreamedModelMaterials pending=pendingStreamedModelMaterials.Dequeue();inspected++;
        if(pending?.Model==null||!streamedWorldModels.Contains(pending.Model)){if(pending?.Materials!=null)foreach(GR2_Material stale in pending.Materials)pendingStreamedMaterialPrepares.Remove(stale);continue;}
        if(streamedModelAssetIds.TryGetValue(pending.Model,out ulong pendingAssetId)
          &&!materialStreamAssetIds.Contains(pendingAssetId)
          &&!(initialStreamLoading&&initialStreamAssetIds.Contains(pendingAssetId))){
          if(pending.Materials!=null)foreach(GR2_Material stale in pending.Materials)pendingStreamedMaterialPrepares.Remove(stale);
          queuedStreamedModelMaterials.Remove(pending.Model);
          continue;
        }
        if(pending.Materials.Count>0){
          GR2_Material material=pending.Materials.Peek();
          if(material==null){pending.Materials.Dequeue();}
          else if(!material.parsed){
            materialMetadataStreamer?.Request(material);
            if(materialMetadataStreamer!=null&&!materialMetadataStreamer.IsFailed(material)){pendingStreamedModelMaterials.Enqueue(pending);continue;}
            pending.Materials.Dequeue();pendingStreamedMaterialPrepares.Remove(material);
          }else{
            pending.Materials.Dequeue();
            try{material.EnsureTextureResources(Device,Math.Max(0,appliedTextureMipSkip));streamedMaterialResourcesPrepared.Add(material);}catch(Exception ex){System.Diagnostics.Debug.WriteLine("World material '"+material.materialName+"' failed: "+ex.Message);}finally{pendingStreamedMaterialPrepares.Remove(material);}
            materialLastUseFrame[material]=worldRenderFrame;count++;if(materialLastUseFrame.Count>160)TrimMaterialTextureResidency();if(textureCache.Count>128)TrimSharedTextureResidency();
          }
        }
        if(pending.Materials.Count>0)pendingStreamedModelMaterials.Enqueue(pending);else{queuedStreamedModelMaterials.Remove(pending.Model);streamedModelMaterialsReady.Add(pending.Model);}
      }
    }
    private bool IsUrgentStreamModel(GR2 model){
      return model!=null&&streamedModelAssetIds.TryGetValue(model,out ulong assetId)&&urgentStreamAssetIds.Contains(assetId);
    }

    private GR2 DequeueNextModelGpuUpload(){
      int scan=pendingModelGpuUploads.Count;
      for(int i=0;i<scan;i++){
        GR2 model=pendingModelGpuUploads.Dequeue();
        if(model==null||!queuedModelGpuUploads.Contains(model))continue;
        if(IsUrgentStreamModel(model))return model;
        pendingModelGpuUploads.Enqueue(model);
      }
      return pendingModelGpuUploads.Count>0?pendingModelGpuUploads.Dequeue():null;
    }

    private void ProcessModelGpuUploads() {
      bool uploadBacklog=!initialStreamLoading&&pendingModelGpuUploads.Count>16;
      int maxUploads=initialStreamLoading?24:(uploadBacklog?24:12);
      double maxMilliseconds=initialStreamLoading?16.0:(uploadBacklog?10.0:6.5);
      var budget=System.Diagnostics.Stopwatch.StartNew();int count=0;
      while(count<maxUploads&&pendingModelGpuUploads.Count>0&&(count==0||budget.Elapsed.TotalMilliseconds<maxMilliseconds)) {
        GR2 model=DequeueNextModelGpuUpload();if(model==null)continue;queuedModelGpuUploads.Remove(model);if(modelGeometryPrepared.Contains(model))continue;
        // A decoded model can become cold while waiting behind the D3D budget. Do not upload obsolete room geometry
        // just because it was once queued; a later room visit will enqueue it again. Moving followers stay global.
        if(streamedModelAssetIds.TryGetValue(model,out ulong assetId)&&!IsStreamAssetActivelyDemanded(assetId)&&!walkingPathFollowerRenderEntries.Any(x=>ReferenceEquals(x?.Model,model)))continue;
        var built=new HashSet<GR2>();BuildModelGeometry(model,built,false);foreach(GR2 prepared in built)modelGeometryPrepared.Add(prepared);MarkModelGeometryUsed(model);count++;if(modelGeometryPrepared.Count>192)TrimModelGeometryResidency();
      }
    }
    private void ReportWorldStreamingProgress() {
      if(!(Window is WorldBrowser browser)||modelStreamer==null||streamCatalogAssetCount<=0)return;
      int activeTotal=activeStreamAssetIds.Count;
      int activeDecoded=activeStreamAssetIds.Count(id=>models.ContainsKey(id));
      int initialTotal=initialStreamAssetIds.Count;
      int initialSettled=initialStreamAssetIds.Count(IsInitialStreamAssetDisplayReady);
      int decoded=streamedWorldModels.Count;
      browser.UpdateWorldStreamingLoading(decoded,streamCatalogAssetCount,activeDecoded,activeTotal,modelStreamer.OutstandingCount,pendingModelGpuUploads.Count,pendingStreamedModelMaterials.Count,initialStreamLoading);
      // Jedipedia does not expose the scene while normal GR2/MAT/tiny-DDS work is still assembling. Our hybrid gate is
      // local rather than planet-wide, but likewise waits until the arrival working set is decoded and display-ready.
      if(initialStreamLoading&&initialTotal>0&&initialSettled>=initialTotal){
        initialStreamLoading=false;
        initialStreamVisibilityKeepUntilFrame=worldRenderFrame+180;
        // Re-resolve from the now-complete local floor/bounds data on the next frame. Do not carry the outdoor/base
        // room that was observed while the hangar shell was only partially present across the gate transition.
        currentCameraRoom=null;displayCameraRoom=null;streamPrefetchRoom=String.Empty;cameraAheadStreamRooms.Clear();urgentCameraAheadStreamRooms.Clear();
        streamPrefetchAnchor=new Vector3(float.NaN,float.NaN,float.NaN);streamPrefetchLook=new Vector3(float.NaN,float.NaN,float.NaN);
      }
    }
    private bool IsInitialStreamAssetDisplayReady(ulong assetId){
      if(modelStreamer==null)return false;
      // A failed archive/model is a terminal condition, but a successful worker result is not ready until
      // PumpDecodedWorldModels has consumed it (state 5) and installed its GR2 into this view.
      if(modelStreamer.IsFailed(assetId))return true;
      if(!modelStreamer.IsSettled(assetId))return false;
      if(!models.TryGetValue(assetId,out GR2 model)||model==null)return false;
      if(streamedWorldModels.Contains(model)&&(!modelGeometryPrepared.Contains(model)||!ModelGeometryBuffersReady(model)))return false;
      foreach(string roomName in initialStreamRoomNames){
        if(!streamPlacementsByRoomAsset.TryGetValue(roomName,out Dictionary<ulong,List<(Room Room,AssetInstance Instance)>> byAsset)||!byAsset.TryGetValue(assetId,out List<(Room Room,AssetInstance Instance)> placements))continue;
        foreach(var placement in placements){
          AssetInstance inst=placement.Instance;if(inst==null)continue;
          if(InstanceVisibleInWorld(inst)&&!streamIndexedInstances.Contains(inst))return false;
          // Floor/dPVS-style detail is intentionally not a first-frame barrier. Jedipedia presents the initial scene
          // before its deferred room-detail visibility build; requiring every decorative GR2 to be classified for floor
          // collision here made large hangars spend seconds behind a CPU triangle pass after their visible shell was ready.
        }
      }
      return true;
    }
    private static int RenderCell(float coordinate)=>(int)Math.Floor(coordinate/RenderCellSize);

    private void RebuildTeleportWarmupQueue(Vector3 target) {
      teleportWarmupModels.Clear();
      teleportWarmupQueued.Clear();
      const float range = 72f;
      float query = range + RenderIndexedRadiusLimit + RangeCullPadding;
      int minX = RenderCell(target.X - query), maxX = RenderCell(target.X + query);
      int minZ = RenderCell(target.Z - query), maxZ = RenderCell(target.Z + query);
      var candidates = new List<(GR2 Model, float DistanceSquared)>();

      void Consider(RenderEntry entry) {
        if (entry == null || entry.Kind != RenderKindModel || entry.Model == null || !entry.Model.enabled) return;
        Vector3 delta = entry.Center - target;
        float limit = range + Math.Max(0f, entry.Radius);
        float distanceSquared = delta.LengthSquared();
        if (distanceSquared > limit * limit || !teleportWarmupQueued.Add(entry.Model)) return;
        candidates.Add((entry.Model, distanceSquared));
      }

      long cellCount = (long)(maxX - minX + 1) * (maxZ - minZ + 1);
      if (cellCount > Math.Max(256L, (long)renderGrid.Count * 3L)) {
        foreach (List<RenderEntry> bucket in renderGrid.Values) foreach (RenderEntry entry in bucket) Consider(entry);
      } else {
        for (int z = minZ; z <= maxZ; z++) for (int x = minX; x <= maxX; x++)
          if (renderGrid.TryGetValue((x, z), out List<RenderEntry> bucket)) foreach (RenderEntry entry in bucket) Consider(entry);
      }
      foreach (RenderEntry entry in renderGlobal) Consider(entry);

      teleportWarmupQueued.Clear();
      foreach (var candidate in candidates.OrderBy(x => x.DistanceSquared).Take(64)) {
        if (teleportWarmupQueued.Add(candidate.Model)) teleportWarmupModels.Enqueue(candidate.Model);
      }
    }

    private void WarmTeleportModel(GR2 model, HashSet<GR2> seen) {
      if (model == null || !seen.Add(model)) return;
      EnsureModelGeometryPrepared(model);
      foreach (GR2_Mesh mesh in model.meshes) {
        if (mesh == null || IsNonVisualMesh(model, mesh)) continue;
        foreach (GR2_Mesh_Piece piece in mesh.meshPieces) ResolvePieceMaterial(model, piece);
      }
      foreach (GR2 attached in model.attachedModels) WarmTeleportModel(attached, seen);
    }

    private void ProcessTeleportWarmup() {
      bool rebuild = false, cancel = false;
      Vector3 requested = Vector3.Zero;
      lock (teleportWarmupLock) {
        if (teleportWarmupCancelPending) {
          teleportWarmupCancelPending = false;
          cancel = true;
        }
        if (teleportWarmupRequestPending) {
          teleportWarmupRequestPending = false;
          requested = teleportWarmupRequestedPosition;
          rebuild = true;
        }
      }
      if (cancel) {
        teleportWarmupModels.Clear();
        teleportWarmupQueued.Clear();
      }
      if (rebuild) RebuildTeleportWarmupQueue(requested);
      if (teleportWarmupModels.Count == 0) return;

      // A small time/count budget keeps the ordinary world responsive. The work happens while the modal elevator
      // dialog is visible, so by the time the user presses Teleport most nearby geometry/textures are already hot.
      var budget = System.Diagnostics.Stopwatch.StartNew();
      int prepared = 0;
      var seen = new HashSet<GR2>();
      while (teleportWarmupModels.Count > 0 && prepared < 2 && budget.ElapsedMilliseconds < 5) {
        GR2 model = teleportWarmupModels.Dequeue();
        WarmTeleportModel(model, seen);
        prepared++;
      }
      if (teleportWarmupModels.Count == 0) teleportWarmupQueued.Clear();
    }

    private bool ModelNeedsCameraWarmup(GR2 model,HashSet<GR2> seen){
      if(model==null||!seen.Add(model))return false;
      if(!ModelGeometryBuffersReady(model))return true;
      if(model.materials!=null)foreach(GR2_Material material in model.materials){
        if(material==null)continue;
        if(!material.parsed)return true;
        if(!String.IsNullOrWhiteSpace(material.diffuseDDS)&&material.diffuseSRV==null)return true;
      }
      if(model.attachedModels!=null)foreach(GR2 attached in model.attachedModels)if(ModelNeedsCameraWarmup(attached,seen))return true;
      return false;
    }

    private void RebuildCameraWarmupQueue(Room cameraRoom,HashSet<string> visible,WorldRenderSettings s){
      cameraWarmupModels.Clear();cameraWarmupQueued.Clear();
      if(s==null||s.Mode==WorldRenderMode.Map||!s.ShowModels)return;
      const float range=110f;
      Vector3 forward=-camera.Look;if(!IsFinite(forward)||forward.LengthSquared()<.000001f)forward=Vector3.UnitZ;else forward.Normalize();
      var needsSeen=new HashSet<GR2>();
      var candidates=new List<(GR2 Model,float Score)>();
      foreach(RenderEntry entry in NearbyRenderEntries(RenderKindModel,range,false,visible)){
        if(entry?.Model==null||entry.Room==null||!entry.Model.enabled||!InstanceRoomVisible(entry.Instance,entry.Room,visible)||!InstanceVisibleInWorld(entry.Instance,s))continue;
        if(!ModelNeedsCameraWarmup(entry.Model,needsSeen))continue;
        Vector3 delta=entry.Center-camera.Position;float dist2=delta.LengthSquared();
        float ahead=0f;if(dist2>.0001f){Vector3 dir=delta;dir.Normalize();ahead=Math.Max(0f,Vector3.Dot(dir,forward));}
        // Prefer nearby assets and the camera's forward half-space so walking/driving toward a cold block prepares
        // it before it actually enters the frustum. The factor affects order only, never visibility.
        float score=dist2*(1f-.45f*ahead);
        if(cameraWarmupQueued.Add(entry.Model))candidates.Add((entry.Model,score));
      }
      cameraWarmupQueued.Clear();
      foreach(var candidate in candidates.OrderBy(x=>x.Score).Take(48))if(cameraWarmupQueued.Add(candidate.Model))cameraWarmupModels.Enqueue(candidate.Model);
      cameraWarmupAnchor=camera.Position;cameraWarmupRoomName=cameraRoom?.RoomName??String.Empty;
    }

    private void ProcessCameraWarmup(Room cameraRoom,HashSet<string> visible,WorldRenderSettings s){
      if(s==null||s.Mode==WorldRenderMode.Map||!s.ShowModels){cameraWarmupModels.Clear();cameraWarmupQueued.Clear();return;}
      string roomName=cameraRoom?.RoomName??String.Empty;
      bool invalidAnchor=!IsFinite(cameraWarmupAnchor);
      bool moved=invalidAnchor||(camera.Position-cameraWarmupAnchor).LengthSquared()>=64f; // rebuild every ~8 world units
      if(moved||!String.Equals(roomName,cameraWarmupRoomName,StringComparison.OrdinalIgnoreCase))RebuildCameraWarmupQueue(cameraRoom,visible,s);
      if(cameraWarmupModels.Count==0)return;
      // Prepare at most one cold model per frame. A model may involve several DDS uploads; spreading them avoids
      // the much larger "new block entered view" burst where dozens were previously prepared synchronously in Draw.
      GR2 model=cameraWarmupModels.Dequeue();var seen=new HashSet<GR2>();WarmTeleportModel(model,seen);
      if(cameraWarmupModels.Count==0)cameraWarmupQueued.Clear();
    }

    private IEnumerable<RenderEntry> NearbyRenderEntries(byte kind,float range,bool cameraFrustum=true,HashSet<string> visible=null){
      // A small active room set is much cheaper to walk than Corellia's entire spatial grid. This bypasses
      // non-active cells before distance/frustum/LOD/material work and includes global/path-follower entries because
      // every RenderEntry is also registered in renderEntriesByRoom.
      if(visible!=null&&visible.Count>0&&visible.Count<=64){
        foreach(string roomName in visible){
          if(!renderEntriesByRoom.TryGetValue(roomName,out List<RenderEntry> roomEntries))continue;
          foreach(RenderEntry entry in roomEntries){
            if(entry==null||entry.Kind!=kind)continue;
            if(SphereWithinDistance(entry.Center,entry.Radius,range)&&(!cameraFrustum||SphereVisibleInCameraFrustum(entry.Center,entry.Radius)))yield return entry;
          }
        }
        yield break;
      }

      float query=Math.Max(0f,range)+RenderIndexedRadiusLimit+RangeCullPadding;int minX=RenderCell(camera.Position.X-query),maxX=RenderCell(camera.Position.X+query),minZ=RenderCell(camera.Position.Z-query),maxZ=RenderCell(camera.Position.Z+query);
      long cellCount=(long)(maxX-minX+1)*(maxZ-minZ+1);
      // Very large authored far distances can span tens of thousands of empty hash-grid cells. Once probing the
      // rectangle is more expensive than walking the populated buckets, scan the populated buckets instead; the
      // exact sphere/frustum tests below remain authoritative, so this changes cost only, never visibility.
      if(cellCount>Math.Max(256L,(long)renderGrid.Count*3L)){
        foreach(List<RenderEntry> bucket in renderGrid.Values)foreach(RenderEntry entry in bucket)if(entry.Kind==kind&&SphereWithinDistance(entry.Center,entry.Radius,range)&&(!cameraFrustum||SphereVisibleInCameraFrustum(entry.Center,entry.Radius)))yield return entry;
      } else {
        for(int z=minZ;z<=maxZ;z++)for(int x=minX;x<=maxX;x++)if(renderGrid.TryGetValue((x,z),out List<RenderEntry> bucket))foreach(RenderEntry entry in bucket)if(entry.Kind==kind&&SphereWithinDistance(entry.Center,entry.Radius,range)&&(!cameraFrustum||SphereVisibleInCameraFrustum(entry.Center,entry.Radius)))yield return entry;
      }
      foreach(RenderEntry entry in renderGlobal)if(entry.Kind==kind&&SphereWithinDistance(entry.Center,entry.Radius,range)&&(!cameraFrustum||SphereVisibleInCameraFrustum(entry.Center,entry.Radius)))yield return entry;
    }

    private IEnumerable<RenderEntry> MapVisibleRenderEntries(byte kind){
      // The full-map renderer used to walk every room and every placement on every frame, then reject almost all
      // of them with MapSphereVisible(). On large planets that CPU traversal dominated even when the GPU had very
      // little to draw. Query the same fixed spatial index by the current orthographic viewport first; the final
      // sphere check is unchanged, so this is a pure broad-phase optimization with identical visible results.
      float halfW=Math.Max(.001f,mapVisibleWidth*.5f),halfH=Math.Max(.001f,mapVisibleHeight*.5f);
      float pad=RenderIndexedRadiusLimit+RangeCullPadding;
      int minX=RenderCell(mapCenter.X-halfW-pad),maxX=RenderCell(mapCenter.X+halfW+pad);
      int minZ=RenderCell(mapCenter.Y-halfH-pad),maxZ=RenderCell(mapCenter.Y+halfH+pad);
      long cellCount=(long)(maxX-minX+1)*(maxZ-minZ+1);
      if(cellCount>Math.Max(256L,(long)renderGrid.Count*3L)){
        foreach(List<RenderEntry> bucket in renderGrid.Values)
          foreach(RenderEntry entry in bucket)
            if(entry.Kind==kind&&MapSphereVisible(entry.Center,entry.Radius))yield return entry;
      } else {
        for(int z=minZ;z<=maxZ;z++)for(int x=minX;x<=maxX;x++)
          if(renderGrid.TryGetValue((x,z),out List<RenderEntry> bucket))
            foreach(RenderEntry entry in bucket)
              if(entry.Kind==kind&&MapSphereVisible(entry.Center,entry.Radius))yield return entry;
      }
      // Very large placements and path followers live outside the fixed grid. There are normally only a handful;
      // keep testing them individually so map behaviour remains exact.
      foreach(RenderEntry entry in renderGlobal)if(entry.Kind==kind&&MapSphereVisible(entry.Center,entry.Radius))yield return entry;
    }
    private bool SphereWithinDistance(Vector3 center,float radius,float range){
      if(!IsFinite(center))return false;float limit=Math.Max(0f,range)+Math.Max(0f,radius)+RangeCullPadding;return (center-camera.Position).LengthSquared()<=limit*limit;
    }
    private bool SphereVisibleInCameraFrustum(Vector3 center,float radius){
      // Do not rebuild Jedipedia's camera-basis cone from Camera.Look/Right/Up here. SlimDXNet's legacy FPS
      // camera and its RH projection use conventions that are not guaranteed to match that basis test 1:1; v8
      // therefore rejected almost every normal-sized renderable in the actual visible half-space and only very
      // large terrain spheres survived close to the camera. Use the frustum extracted from the exact ViewProj that
      // D3D renders with. A padded sphere represented by its containing AABB is conservative (never tighter than
      // the sphere) and still runs only after the spatial-grid/range query has reduced the candidate set.
      if(!IsFinite(center))return false;
      float r=Math.Max(0f,radius)+FrustumCullPadding;
      Vector3 pad=new Vector3(r,r,r);
      return camera.Visible(new BoundingBox(center-pad,center+pad));
    }

    private Room FindCameraRoom(Vector3 p){
      // Match Jedipedia's camera-cell selection order. A floor hit within eight units is authoritative;
      // authored RoomBound/Region/Trigger volumes are the next fallback; then the previous valid room is
      // retained across small floor gaps; only after that do coarse room visibility bounds participate.
      // `_everywhere_` is never treated as a real floor/placement room.
      Room selected=FindBestFloorRoom(p,8f);
      if(selected==null){Room placementRoom=FindPlacementRoom(p);if(placementRoom!=null&&!IsEverywhereRoom(placementRoom))selected=placementRoom;}
      // Jedipedia only starts camera-room tracking after the complete initial GR2/dPVS input set exists. During our
      // local first-frame gate, retaining a provisional outdoor floor hit can lock Corellia to the cell underneath
      // the still-building hangar and prevent the freshly streamed interior from ever becoming authoritative.
      if(selected==null&&!initialStreamLoading&&currentCameraRoom!=null&&!IsEverywhereRoom(currentCameraRoom)&&!skyRoomNames.Contains(currentCameraRoom.RoomName)&&rooms.Contains(currentCameraRoom))selected=currentCameraRoom;
      if(selected==null)selected=FindBoundsRoom(p);
      currentCameraRoom=selected;

      // Jedipedia's lastRoomName normally prevents its UI from falling back to `_everywhere_` after a real camera
      // cell has been established. PugTools can start/teleport above a floor before it has such a previous cell.
      // Keep render visibility faithful to the 8-unit rule above, but make the status display useful: never replace a
      // known specific room with `_everywhere_`; if there has never been one, use the nearest actual floor below the
      // camera as a display-only fallback. This is especially useful when flying high over an exterior room.
      if(selected!=null&&!IsEverywhereRoom(selected))displayCameraRoom=selected;
      else if(displayCameraRoom==null||IsEverywhereRoom(displayCameraRoom)){
        Room below=FindBestFloorRoom(p,float.MaxValue);if(below!=null&&!IsEverywhereRoom(below))displayCameraRoom=below;else displayCameraRoom=selected;
      }
      return selected;
    }

    private Room FindBestFloorRoom(Vector3 p,float maxDistance){
      Room hm=FindHeightMapFloorRoom(p,maxDistance);Room gr2=FindModelFloorRoom(p,maxDistance);
      if(hm==null)return gr2;if(gr2==null)return hm;
      // Re-evaluate exact distances only for the two winners so the same nearest-floor rule as Jedipedia decides.
      float hd=FindHeightMapFloorDistance(p,hm,maxDistance),gd=FindModelFloorDistance(p,gr2,maxDistance);
      if(gd<hd-.0001f)return gr2;if(hd<gd-.0001f)return hm;return RoomFloorTieRadius(gr2)<RoomFloorTieRadius(hm)?gr2:hm;
    }

    private float FindHeightMapFloorDistance(Vector3 p,Room target,float maxDistance){
      if(target==null||!heightMapFloorGrid.TryGetValue((FloorCell(p.X),FloorCell(p.Z)),out List<HeightMapFloorEntry> entries))return float.MaxValue;float best=float.MaxValue;
      foreach(HeightMapFloorEntry entry in entries){if(entry.Room!=target)continue;if(TryHeightMapFloorY(entry,p,out float y)){float d=p.Y-y;if(d>=-.0001f&&d<=maxDistance+.0001f&&d<best)best=Math.Max(0f,d);}}return best;
    }

    private float FindModelFloorDistance(Vector3 p,Room target,float maxDistance){
      if(target==null)return float.MaxValue;float best=float.MaxValue;
      void Consider(IEnumerable<ModelFloorPlacementEntry> placements){if(placements==null)return;foreach(ModelFloorPlacementEntry placement in placements){if(placement.Room!=target||p.X<placement.WorldMinX-.0001f||p.X>placement.WorldMaxX+.0001f||p.Z<placement.WorldMinZ-.0001f||p.Z>placement.WorldMaxZ+.0001f)continue;Vector3 local=Vector3.TransformCoordinate(p,placement.Inverse);foreach(FloorMeshData md in placement.Model.Meshes)foreach(int offset in FloorMeshCandidateOffsets(md,local,placement.LocalXZIndependentOfY))if(TryFloorTriangleHit(md.Mesh,offset,placement.World,p,out float y)){float d=p.Y-y;if(d>=-.0001f&&d<=maxDistance+.0001f&&d<best)best=Math.Max(0f,d);}}}
      modelFloorPlacementGrid.TryGetValue((ModelFloorPlacementCell(p.X),ModelFloorPlacementCell(p.Z)),out List<ModelFloorPlacementEntry> bucket);Consider(bucket);Consider(modelFloorPlacementGlobal);return best;
    }

    private Room FindBoundsRoom(Vector3 p){
      var matches=new List<Room>();
      foreach(Room r in rooms){if(r==null||IsEverywhereRoom(r)||skyRoomNames.Contains(r.RoomName)||!HasUsableRoomBounds(r)||!ContainsRoom(r,p,.5f))continue;matches.Add(r);}
      if(matches.Count==0)return rooms.FirstOrDefault(IsEverywhereRoom);
      List<Room> interiors=matches.Where(r=>!r.OutdoorsVisible).ToList();
      if(interiors.Count>0)return interiors.OrderBy(RoomSpecificity).ThenBy(r=>r.RoomName,StringComparer.OrdinalIgnoreCase).First();
      // Jedipedia chooses the broadest authored outdoor cell when all matching rooms are outdoors-visible.
      return matches.OrderByDescending(RoomSpecificity).ThenBy(r=>r.RoomName,StringComparer.OrdinalIgnoreCase).First();
    }

    private static bool HasUsableRoomBounds(Room r){
      return r!=null&&IsFinite(r.VisibilityMin)&&IsFinite(r.VisibilityMax)&&r.VisibilityMax.X>=r.VisibilityMin.X&&r.VisibilityMax.Y>=r.VisibilityMin.Y&&r.VisibilityMax.Z>=r.VisibilityMin.Z&&r.VisibilityRadius>.0001f;
    }
    private static float RoomSpecificity(Room r){return r!=null&&r.VisibilityRadius>.0001f?r.VisibilityRadius:float.MaxValue;}
    private static float RoomFloorTieRadius(Room r){return r!=null&&r.VisibilityRadius>0f?r.VisibilityRadius:0f;}
    private Room FindHorizontalRoomColumn(Vector3 p){
      Room best=null;
      foreach(Room r in rooms){
        if(r==null||skyRoomNames.Contains(r.RoomName)||!HasUsableRoomBounds(r)||!ContainsRoomXZ(r,p,.5f))continue;
        if(best==null||RoomSpecificity(r)<RoomSpecificity(best))best=r;
      }
      return best;
    }
    private Room FindHeightMapFloorRoom(Vector3 p,float maxDistance){
      if(!heightMapFloorGrid.TryGetValue((FloorCell(p.X),FloorCell(p.Z)),out List<HeightMapFloorEntry> entries))return null;
      Room best=null;float bestDistance=maxDistance+.0001f;
      for(int i=0;i<entries.Count;i++){
        HeightMapFloorEntry entry=entries[i];if(entry==null||entry.Room==null||IsEverywhereRoom(entry.Room)||!TryHeightMapFloorY(entry,p,out float floorY))continue;
        float distance=p.Y-floorY;if(distance<-.0001f||distance>bestDistance)continue;Room room=entry.Room;
        if(best==null||distance<bestDistance-.0001f||(Math.Abs(distance-bestDistance)<=.0001f&&RoomFloorTieRadius(room)<RoomFloorTieRadius(best))){best=room;bestDistance=Math.Max(0,distance);}
      }
      return best;
    }

    private static bool TryHeightMapFloorY(HeightMapFloorEntry entry,Vector3 p,out float worldY){
      worldY=0f;HeightMap hm=entry?.HeightMap;if(hm==null||hm.elevation==null)return false;
      // Jedipedia locates the heightmap column from world X/Z only. Camera Y must not move the query sideways when
      // a placement has a non-trivial parent transform; the vertical floor comparison happens after interpolation.
      Matrix inv=entry.Inverse;float lw=p.X*inv.M14+p.Z*inv.M34+inv.M44;if(Math.Abs(lw)<.000001f)lw=1f;
      float localX=(p.X*inv.M11+p.Z*inv.M31+inv.M41)/lw,localZ=(p.X*inv.M13+p.Z*inv.M33+inv.M43)/lw;
      int w=checked((int)hm.width),d=checked((int)hm.depth);if(w<2||d<2)return false;
      float gx=(localX-entry.XMin)/.2f,gz=(localZ-entry.ZMin)/.2f;int x=(int)Math.Floor(gx),z=(int)Math.Floor(gz);
      if(x<0||z<0||x>=w-1||z>=d-1)return false;if(hm.hasHoles&&!hm.CheckNoHole(x,z))return false;
      float tx=gx-x,tz=gz-z;float h00=hm.elevation[z,x],h10=hm.elevation[z,x+1],h01=hm.elevation[z+1,x],h11=hm.elevation[z+1,x+1],y;
      // Match the rendered lattice diagonal (x,z+1) -> (x+1,z), exactly as Jedipedia's floor locator does.
      if(tx+tz<=1f)y=h00+(h10-h00)*tx+(h01-h00)*tz;else y=h11+(h01-h11)*(1f-tx)+(h10-h11)*(1f-tz);
      Vector3 floorWorld=Vector3.TransformCoordinate(new Vector3(localX,y,localZ),entry.World);worldY=floorWorld.Y;return !float.IsNaN(worldY)&&!float.IsInfinity(worldY);
    }

    private static bool ContainsRoom(Room r,Vector3 p,float margin){return p.X>=r.VisibilityMin.X-margin&&p.X<=r.VisibilityMax.X+margin&&p.Y>=r.VisibilityMin.Y-margin&&p.Y<=r.VisibilityMax.Y+margin&&p.Z>=r.VisibilityMin.Z-margin&&p.Z<=r.VisibilityMax.Z+margin;}
    private static bool ContainsRoomXZ(Room r,Vector3 p,float margin){return r!=null&&p.X>=r.VisibilityMin.X-margin&&p.X<=r.VisibilityMax.X+margin&&p.Z>=r.VisibilityMin.Z-margin&&p.Z<=r.VisibilityMax.Z+margin;}

    private static string ParsePortalTargetRoom(string raw){
      if(String.IsNullOrWhiteSpace(raw))return null;string text=raw.Trim();int split=text.IndexOfAny(new[]{' ','\t','\r','\n'});
      if(split>0){string first=text.Substring(0,split);if(first.All(Char.IsDigit))text=text.Substring(split+1).Trim();}
      string normalized=NormalizeRoom(text);return String.IsNullOrWhiteSpace(normalized)?null:normalized;
    }

    private void BuildPortalVisibilityGraph(){
      roomPortals.Clear();if(area==null||rooms==null||rooms.Count==0)return;
      var byName=rooms.Where(r=>r!=null).GroupBy(r=>NormalizeRoom(r.RoomName),StringComparer.OrdinalIgnoreCase).ToDictionary(g=>g.Key,g=>g.First(),StringComparer.OrdinalIgnoreCase);
      foreach(Room source in rooms){
        if(source==null||IsEverywhereRoom(source))continue;
        foreach(AssetInstance inst in source.InstancesById.Values){
          if(inst==null||inst.PathFollowerBoundaryExcluded||String.IsNullOrWhiteSpace(inst.PortalTarget))continue;
          if(!area.AssetIdMap.TryGetValue(inst.assetID,out AreaAsset asset))continue;
          string ext=(asset.Extension??String.Empty).Trim().TrimStart('.');string path=NormalizeAssetPath(asset.Path);
          if(!String.Equals(ext,"p",StringComparison.OrdinalIgnoreCase)||!String.Equals(path,"engine/portal",StringComparison.OrdinalIgnoreCase))continue;
          string targetName=ParsePortalTargetRoom(inst.PortalTarget);if(String.IsNullOrWhiteSpace(targetName))continue;
          if(!byName.TryGetValue(targetName,out Room target)){
            string leaf=targetName.IndexOf('/')>=0?targetName.Substring(targetName.LastIndexOf('/')+1):targetName;
            target=rooms.FirstOrDefault(r=>r!=null&&String.Equals(NormalizeRoom(r.RoomName),leaf,StringComparison.OrdinalIgnoreCase));
          }
          if(target==null||ReferenceEquals(target,source))continue;
          Matrix world=InstanceWorld(inst,source);Vector3 center=new Vector3(world.M41,world.M42,world.M43);
          float sx=(float)Math.Sqrt(world.M11*world.M11+world.M12*world.M12+world.M13*world.M13);
          float sy=(float)Math.Sqrt(world.M21*world.M21+world.M22*world.M22+world.M23*world.M23);
          float sz=(float)Math.Sqrt(world.M31*world.M31+world.M32*world.M32+world.M33*world.M33);
          float radius=.5f*(float)Math.Sqrt(sx*sx+sy*sy+sz*sz)+.15f;
          string key=source.RoomName;if(!roomPortals.TryGetValue(key,out List<PortalVisibilityEntry> links))roomPortals[key]=links=new List<PortalVisibilityEntry>();
          links.Add(new PortalVisibilityEntry{Source=source,Target=target,Instance=inst,Center=center,Radius=Math.Max(.15f,radius)});
        }
      }
    }

    private void BuildRoomStreamingGraph(){
      roomStreamingNeighbors.Clear();roomStreamingAuthoredVisible.Clear();if(rooms==null||rooms.Count==0)return;
      var byName=rooms.Where(r=>r!=null&&!String.IsNullOrWhiteSpace(r.RoomName))
        .GroupBy(r=>NormalizeRoom(r.RoomName),StringComparer.OrdinalIgnoreCase)
        .ToDictionary(g=>g.Key,g=>g.First(),StringComparer.OrdinalIgnoreCase);
      void Add(Room a,Room b){
        if(a==null||b==null||ReferenceEquals(a,b)||String.IsNullOrWhiteSpace(a.RoomName)||String.IsNullOrWhiteSpace(b.RoomName))return;
        if(!roomStreamingNeighbors.TryGetValue(a.RoomName,out HashSet<string> set))roomStreamingNeighbors[a.RoomName]=set=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        set.Add(b.RoomName);
      }

      // Portal links are true direct adjacency. Make them bidirectional for streaming: a portal can be authored on
      // only one side while the player can still cross it in either direction.
      foreach(List<PortalVisibilityEntry> links in roomPortals.Values)foreach(PortalVisibilityEntry portal in links){
        if(portal?.Source==null||portal.Target==null)continue;Add(portal.Source,portal.Target);Add(portal.Target,portal.Source);
      }

      // DAT VisibleRooms is a visibility list rather than strict adjacency and can be huge outdoors. Do not freeze a
      // nearest-N subset here: at this point many v6 rooms still have only placement-origin bounds, so the real next
      // room can rank far away and be discarded permanently. Keep the authored candidates cheaply and choose a bounded
      // nearest slice later, when streamed exact bounds and the live camera position are available.
      foreach(Room room in rooms){
        if(room==null||IsEverywhereRoom(room)||skyRoomNames.Contains(room.RoomName)||room.VisibleRooms==null||room.VisibleRooms.Count==0)continue;
        var authored=new List<Room>();var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(string raw in room.VisibleRooms){
          string key=NormalizeRoom(raw);if(String.IsNullOrWhiteSpace(key)||!byName.TryGetValue(key,out Room candidate)||candidate==null||ReferenceEquals(candidate,room)||!seen.Add(candidate.RoomName))continue;
          authored.Add(candidate);
        }
        if(authored.Count>0)roomStreamingAuthoredVisible[room.RoomName]=authored;
      }

      // Outdoor areas often have no explicit portal graph at all. Add a small number of physically touching/near
      // cells from room bounds. O(n²) here is a one-time load step (Corellia is still only hundreds of rooms) and
      // replaces far more expensive per-frame whole-room scans.
      var usable=rooms.Where(r=>r!=null&&!IsEverywhereRoom(r)&&!skyRoomNames.Contains(r.RoomName)&&TryGetRoomStreamingBounds(r,out _)).ToList();
      const float maxGap=32f,maxVerticalGap=32f;float maxGapSq=maxGap*maxGap;
      foreach(Room room in usable){
        if(!room.OutdoorsVisible||!TryGetRoomStreamingBounds(room,out RoomStreamingBounds rb))continue;
        var nearby=new List<(Room Room,float Gap)>();
        foreach(Room candidate in usable){
          if(ReferenceEquals(room,candidate)||!candidate.OutdoorsVisible||!TryGetRoomStreamingBounds(candidate,out RoomStreamingBounds cb))continue;
          float dy=AxisGap(rb.Min.Y,rb.Max.Y,cb.Min.Y,cb.Max.Y);
          if(dy>maxVerticalGap)continue;
          float dx=AxisGap(rb.Min.X,rb.Max.X,cb.Min.X,cb.Max.X);
          float dz=AxisGap(rb.Min.Z,rb.Max.Z,cb.Min.Z,cb.Max.Z);
          float gap=dx*dx+dz*dz;if(gap<=maxGapSq)nearby.Add((candidate,gap));
        }
        foreach(var item in nearby.OrderBy(x=>x.Gap).Take(8)){Add(room,item.Room);Add(item.Room,room);}
      }
    }

    private IEnumerable<string> DirectStreamingNeighborRoomNames(string roomName){
      if(String.IsNullOrWhiteSpace(roomName)||!roomStreamingNeighbors.TryGetValue(roomName,out HashSet<string> direct)||direct==null)yield break;
      foreach(string name in direct)if(!String.IsNullOrWhiteSpace(name))yield return name;
    }

    private void UpdateStreamTransitionGrace(Room anchor){
      if(modelStreamer==null){streamTransitionRoomGrace.Clear();lastStreamAnchorRoom=String.Empty;return;}
      string next=anchor!=null&&!IsEverywhereRoom(anchor)&&!skyRoomNames.Contains(anchor.RoomName)?anchor.RoomName:String.Empty;
      if(!String.Equals(next,lastStreamAnchorRoom,StringComparison.OrdinalIgnoreCase)){
        if(!String.IsNullOrWhiteSpace(lastStreamAnchorRoom)){
          streamTransitionRoomGrace[lastStreamAnchorRoom]=worldRenderFrame+StreamTransitionGraceFrames;
          // Keep only the nearest old doorway neighbours as a shorter safety net. This covers room-locator oscillation
          // without retaining an entire outdoor visibility ring after every transition.
          foreach(string neighbour in DirectStreamingNeighborRoomNames(lastStreamAnchorRoom).OrderBy(StreamRoomDistanceSquared).Take(2))
            streamTransitionRoomGrace[neighbour]=Math.Max(streamTransitionRoomGrace.TryGetValue(neighbour,out long old)?old:0,worldRenderFrame+90);

          // Jedipedia's dPVS keeps geometry that is still visible through the crossed portal even after the
          // camera-room identity changes. We do not have its native solver, so retain a small distance-ranked
          // slice of the OLD room's authored VisibleRooms. ActiveStreamTransitionRoomNames() feeds both the
          // draw set and model/material/floor residency, preventing those backdrop/sibling cells from being
          // culled or evicted exactly while the player walks through the doorway.
          if(roomStreamingAuthoredVisible.TryGetValue(lastStreamAnchorRoom,out List<Room> oldAuthored)&&oldAuthored!=null){
            foreach(Room visibleRoom in oldAuthored.Where(r=>r!=null&&!IsEverywhereRoom(r)&&!skyRoomNames.Contains(r.RoomName))
              .OrderBy(r=>StreamRoomDistanceSquared(r.RoomName)).Take(StreamTransitionVisibleGraceLimit)){
              string visibleName=visibleRoom.RoomName;if(String.IsNullOrWhiteSpace(visibleName))continue;
              long expiry=worldRenderFrame+StreamTransitionVisibleGraceFrames;
              streamTransitionRoomGrace[visibleName]=Math.Max(streamTransitionRoomGrace.TryGetValue(visibleName,out long oldExpiry)?oldExpiry:0,expiry);
            }
          }
        }
        lastStreamAnchorRoom=next;
      }
      foreach(string roomName in streamTransitionRoomGrace.Where(x=>x.Value<worldRenderFrame).Select(x=>x.Key).ToList())streamTransitionRoomGrace.Remove(roomName);
    }

    private IEnumerable<string> ActiveStreamTransitionRoomNames(){
      foreach(var pair in streamTransitionRoomGrace)if(pair.Value>=worldRenderFrame)yield return pair.Key;
    }

    private IEnumerable<string> StreamingNeighborRoomNames(string roomName,int authoredLimit=16){
      if(String.IsNullOrWhiteSpace(roomName))yield break;
      var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      if(roomStreamingNeighbors.TryGetValue(roomName,out HashSet<string> direct)){
        foreach(string name in direct)if(!String.IsNullOrWhiteSpace(name)&&seen.Add(name))yield return name;
      }
      if(authoredLimit<=0||!roomStreamingAuthoredVisible.TryGetValue(roomName,out List<Room> authored)||authored==null||authored.Count==0)yield break;
      Vector3 position=camera.Position;
      var ranked=authored.Where(r=>r!=null&&!IsEverywhereRoom(r)&&!skyRoomNames.Contains(r.RoomName)&&!seen.Contains(r.RoomName))
        .Select(r=>{
          bool have=TryGetRoomStreamingBounds(r,out RoomStreamingBounds b);
          bool contains=have&&ContainsStreamingBounds(b,position,12f);
          float distance=have?StreamingBoundsDistanceSquared(b,position):float.MaxValue;
          return (Room:r,Contains:contains,Coarse:!have||b.Coarse,Distance:distance);
        })
        .OrderBy(x=>x.Contains?0:1).ThenBy(x=>x.Coarse?1:0).ThenBy(x=>x.Distance).ThenBy(x=>x.Room.RoomName,StringComparer.OrdinalIgnoreCase)
        .Take(authoredLimit);
      foreach(var item in ranked)if(seen.Add(item.Room.RoomName))yield return item.Room.RoomName;
    }

    private static float AxisGap(float amin,float amax,float bmin,float bmax){
      if(amax<bmin)return bmin-amax;if(bmax<amin)return amin-bmax;return 0f;
    }
    private static float RoomBoundsGapSquared(Room a,Room b){
      if(!HasUsableRoomBounds(a)||!HasUsableRoomBounds(b))return float.MaxValue;
      float dx=AxisGap(a.VisibilityMin.X,a.VisibilityMax.X,b.VisibilityMin.X,b.VisibilityMax.X);
      float dy=AxisGap(a.VisibilityMin.Y,a.VisibilityMax.Y,b.VisibilityMin.Y,b.VisibilityMax.Y);
      float dz=AxisGap(a.VisibilityMin.Z,a.VisibilityMax.Z,b.VisibilityMin.Z,b.VisibilityMax.Z);
      return dx*dx+dy*dy+dz*dz;
    }

    private bool UseInitialStreamVisibilityGrace()=>modelStreamer!=null&&(initialStreamLoading||(initialStreamVisibilityKeepUntilFrame>0&&worldRenderFrame<=initialStreamVisibilityKeepUntilFrame));

    private HashSet<string> BuildLocalRoomStreamingSet(Room current,WorldRenderSettings s){
      if(s==null||!s.EnableLocalRoomStreaming||s.Mode==WorldRenderMode.Map||current==null||IsEverywhereRoom(current))return null;
      var set=new HashSet<string>(StringComparer.OrdinalIgnoreCase){current.RoomName,"_everywhere_"};
      // The renderer's portal/VisibleRooms traversal can legitimately return more than sixteen Corellia cells. The
      // previous local-stream intersection hard-capped the current room to 16 authored neighbours and then discarded
      // every otherwise-visible room outside that set. Those rooms only reappeared after crossing into another cell,
      // which looked exactly like late streaming. Keep a wider first hop and a bounded authored second hop (the same
      // backdrop pattern Jedipedia follows for visibility-only rooms) while the spatial/frustum passes still decide
      // what is actually drawn.
      List<string> firstHop=StreamingNeighborRoomNames(current.RoomName,48).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
      foreach(string roomName in firstHop)set.Add(roomName);
      int secondHopBudget=32;
      foreach(string roomName in firstHop.OrderBy(StreamRoomDistanceSquared)){
        if(secondHopBudget<=0)break;
        if(!roomStreamingAuthoredVisible.TryGetValue(roomName,out List<Room> authored)||authored==null)continue;
        foreach(Room linked in authored.Where(r=>r!=null&&!IsEverywhereRoom(r)&&!skyRoomNames.Contains(r.RoomName))
          .OrderBy(r=>StreamRoomDistanceSquared(r.RoomName)).Take(8)){
          if(set.Add(linked.RoomName)&&--secondHopBudget<=0)break;
        }
      }
      foreach(string roomName in ActiveStreamTransitionRoomNames())set.Add(roomName);
      if(UseInitialStreamVisibilityGrace())foreach(string roomName in initialStreamRoomNames)set.Add(roomName);
      // Boundary continuity comes from the bidirectional portal links plus physically near outdoor cells built once
      // in BuildRoomStreamingGraph(); do not scan every room again on every frame.
      string sky=ResolveSkyRoomName(current.EnvironmentScheme);if(s.ShowSky&&!String.IsNullOrWhiteSpace(sky))set.Add(sky);
      return set;
    }

    private bool RoomBoundsVisibleInCameraFrustum(Room room){
      if(room==null)return true;Vector3 min=room.VisibilityMin,max=room.VisibilityMax;
      if(!HasUsableRoomBounds(room)&&TryGetRoomStreamingBounds(room,out RoomStreamingBounds streaming)){min=streaming.Min;max=streaming.Max;}
      if(!IsFinite(min)||!IsFinite(max)||max.X<min.X||max.Y<min.Y||max.Z<min.Z)return true;
      Vector3 pad=new Vector3(FrustumCullPadding,FrustumCullPadding,FrustumCullPadding);return camera.Visible(new BoundingBox(min-pad,max+pad));
    }

    private bool PortalVisibleInCameraFrustum(PortalVisibilityEntry portal){
      if(portal==null)return false;float radius=Math.Max(.15f,portal.Radius);
      // Very near portals must remain open even when their centre is technically behind the near plane; this avoids
      // a room popping out exactly while the camera crosses the doorway. Otherwise use the same exact camera-frustum
      // sphere test as the render spatial index.
      float nearReach=radius+1.25f;if((portal.Center-camera.Position).LengthSquared()<=nearReach*nearReach)return true;
      return SphereVisibleInCameraFrustum(portal.Center,radius)&&SphereWithinViewDistance(portal.Center,radius);
    }

    private static string VisibleRoomScope(HashSet<string> visible){
      if(visible==null)return "*";if(visible.Count==0)return String.Empty;return String.Join(";",visible.OrderBy(x=>x,StringComparer.OrdinalIgnoreCase));
    }

    private HashSet<string> BuildVisibleRoomSet(Room current, WorldRenderSettings s){
      if(s==null||s.Mode==WorldRenderMode.Map||current==null||IsEverywhereRoom(current))return null;
      HashSet<string> streaming=BuildLocalRoomStreamingSet(current,s);
      if(!s.EnableRoomVisibility)return streaming;

      // Jedipedia's fallback dPVS policy fails open from every room the camera can currently belong to, then expands
      // each seed's authored VisibleRooms. PugTools streams room details lazily, so the floor-derived camera room can
      // lag behind a tighter nested hangar/corridor. Seed visibility with BOTH the authoritative room and that spatial
      // candidate (plus a phase-owner when present) instead of letting a temporary transition grace hide the mismatch.
      var set=new HashSet<string>(StringComparer.OrdinalIgnoreCase){"_everywhere_"};
      var byName=rooms.Where(r=>r!=null).GroupBy(r=>NormalizeRoom(r.RoomName),StringComparer.OrdinalIgnoreCase)
        .ToDictionary(g=>g.Key,g=>g.First(),StringComparer.OrdinalIgnoreCase);
      var seeds=new List<Room>();
      var seedNames=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      void AddSeed(Room seed){
        if(seed==null||IsEverywhereRoom(seed)||skyRoomNames.Contains(seed.RoomName)||!seedNames.Add(seed.RoomName))return;
        seeds.Add(seed);set.Add(seed.RoomName);
        foreach(string raw in seed.VisibleRooms){
          string roomName=NormalizeRoom(raw);if(String.IsNullOrWhiteSpace(roomName))continue;
          if(byName.TryGetValue(roomName,out Room candidate)&&candidate!=null)set.Add(candidate.RoomName);
        }
        // Keep the exact bidirectional portal/physical neighbours at a crossed doorway. This is deliberately NOT the
        // wider prefetch ring: residency and draw visibility remain separate.
        foreach(string neighbour in DirectStreamingNeighborRoomNames(seed.RoomName))
          if(byName.TryGetValue(NormalizeRoom(neighbour),out Room direct)&&direct!=null)set.Add(direct.RoomName);
      }

      AddSeed(current);
      Room spatialRoom=FindStreamingRoom(camera.Position,160f);
      if(spatialRoom!=null&&!String.Equals(spatialRoom.RoomName,current.RoomName,StringComparison.OrdinalIgnoreCase))AddSeed(spatialRoom);
      AddSeed(FindPhaseTriggerRoom(camera.Position));

      // Conservative streamed bounds can overlap while exact floor/room data catches up. Keep only a few tight cells
      // that really contain the camera; this prevents the room from vanishing while avoiding the old mistake of drawing
      // the complete streaming/prefetch ring.
      foreach(Room overlap in rooms.Where(r=>r!=null&&!IsEverywhereRoom(r)&&!skyRoomNames.Contains(r.RoomName))
        .Select(r=>{bool have=TryGetRoomStreamingBounds(r,out RoomStreamingBounds b);return (Room:r,Bounds:b,Have:have);})
        .Where(x=>x.Have&&ContainsStreamingBounds(x.Bounds,camera.Position,2f))
        .OrderBy(x=>x.Bounds.Coarse?1:0).ThenBy(x=>x.Bounds.Radius).Take(8).Select(x=>x.Room))AddSeed(overlap);

      // Portal traversal starts from every active seed just like Jedipedia expands all active dPVS camera rooms. Portal
      // geometry may ADD rooms, but it never subtracts the authored fallback rooms collected above.
      var queue=new Queue<(Room Room,int Depth)>();
      var queued=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      foreach(Room seed in seeds)if(queued.Add(seed.RoomName))queue.Enqueue((seed,0));
      const int maxPortalDepth=16;
      while(queue.Count>0){
        var node=queue.Dequeue();if(node.Room==null||node.Depth>=maxPortalDepth)continue;
        if(!roomPortals.TryGetValue(node.Room.RoomName,out List<PortalVisibilityEntry> links)||links==null)continue;
        foreach(PortalVisibilityEntry portal in links){
          if(portal?.Target==null||!PortalVisibleInCameraFrustum(portal))continue;
          set.Add(portal.Target.RoomName);
          if(queued.Add(portal.Target.RoomName))queue.Enqueue((portal.Target,node.Depth+1));
        }
      }

      string sky=ResolveSkyRoomName(current.EnvironmentScheme);if(s.ShowSky&&!String.IsNullOrEmpty(sky))set.Add(sky);
      foreach(string roomName in ActiveStreamTransitionRoomNames())set.Add(roomName);
      if(UseInitialStreamVisibilityGrace())foreach(string roomName in initialStreamRoomNames)set.Add(roomName);
      return set;
    }

    private static string NormalizeRoom(string s)=>(s??"").Replace('\\','/').TrimStart('/').Replace(".dat","",StringComparison.OrdinalIgnoreCase).ToLowerInvariant();

    private GR2_Material EnsureMaterialParsed(GR2_Material material) {
      if(material==null)return null;
      materialLastUseFrame[material]=worldRenderFrame;
      int mip=Math.Max(0,appliedTextureMipSkip);
      try{
        // Streamed world materials are allowed to render from MAT metadata before their DDS resources are ready.
        // Never turn a doorway/first-visible draw into synchronous archive I/O; the bounded prewarm queue owns those
        // uploads. Non-streamed population/utility materials keep the legacy immediate behaviour.
        if(streamedWorldMaterials.Contains(material)&&!streamedMaterialResourcesPrepared.Contains(material)){
          if(!material.parsed){materialMetadataStreamer?.Request(material);return null;}
          return material;
        }
        // ModelBrowserViewMaterial and a few metadata paths intentionally call ParseMAT(null,...). Such a material is
        // "parsed" but has no D3D resources. Rehydrate its already-resolved paths instead of rendering the NPC/model
        // flat grey. This is also safe after the world texture LRU has evicted a cold material.
        if(!material.parsed)material.ParseMAT(Device,null,mip);
        material.EnsureTextureResources(Device,mip);
      } catch(Exception ex){System.Diagnostics.Debug.WriteLine("World material '"+material.materialName+"' failed: "+ex.Message);}
      return material;
    }

    private void TrimMaterialTextureResidency(){
      // Jedipedia streams/cache-evicts cold detail instead of keeping the entire planet resident. Do the equivalent
      // for MAT-owned SRVs. Keep a generous hot set and never evict character-only complexion/facepaint overrides,
      // which are authored outside the MAT and cannot be reconstructed by ParseMAT alone.
      const int highWater=160,target=120;const long idleFrames=180;
      var parsed=WorldMaterialResidencySet().Where(m=>m!=null&&m.parsed).ToList();
      if(parsed.Count<=highWater)return;
      long cutoff=worldRenderFrame-idleFrames;
      // Current-room/direct-neighbour materials are streaming residency, not merely render-frame residency. They can
      // be fully prewarmed while still off-screen, so a pure last-drawn LRU would evict them before the doorway.
      var protectedMaterials=new HashSet<GR2_Material>();var protectedModels=new HashSet<GR2>();var protectedStack=new Stack<GR2>();
      foreach(ulong assetId in activeStreamAssetIds)if(models.TryGetValue(assetId,out GR2 activeModel)&&activeModel!=null)protectedStack.Push(activeModel);
      while(protectedStack.Count>0){GR2 model=protectedStack.Pop();if(model==null||!protectedModels.Add(model))continue;if(model.materials!=null)foreach(GR2_Material m in model.materials)if(m!=null)protectedMaterials.Add(m);if(model.attachedModels!=null)foreach(GR2 attached in model.attachedModels)if(attached!=null)protectedStack.Push(attached);}
      foreach(GR2_Material material in parsed
        .Where(m=>String.IsNullOrWhiteSpace(m.complexionDDS)&&String.IsNullOrWhiteSpace(m.facepaintDDS))
        .OrderBy(m=>materialLastUseFrame.TryGetValue(m,out long frame)?frame:long.MinValue).ToList()){
        if(parsed.Count<=target)break;
        if(protectedMaterials.Contains(material))continue;
        long last=materialLastUseFrame.TryGetValue(material,out long frame)?frame:long.MinValue;
        if(last>cutoff)continue;
        ReleaseOwnedMaterial(material);material.parsed=false;streamedMaterialResourcesPrepared.Remove(material);materialLastUseFrame.Remove(material);parsed.Remove(material);
      }
    }

    private GR2_Material ResolvePieceMaterial(GR2 model, GR2_Mesh_Piece piece) {
      if(model==null||piece==null)return null;
      GR2_Material local=null;
      if(piece.matId>=0&&piece.matId<model.materials.Count)local=model.materials[piece.matId];
      else if(model.materials.Count>0)local=model.materials[0];
      if(local==null)return null;
      // Appearance/NPC variants can own a material object that is not registered in the area's global MAT map.
      // Falling back to null here was the reason animated characters became flat grey after lazy material streaming.
      GR2_Material resolved=local;
      if(!String.IsNullOrWhiteSpace(local.materialName)&&materials.TryGetValue(local.materialName,out GR2_Material shared)&&shared!=null)resolved=shared;
      GR2_Material parsed=EnsureMaterialParsed(resolved);
      // Keep the authored material name available while streamed MAT metadata is still pending so the world pass can
      // suppress known collision/occluder helper hulls instead of drawing them as a textureless black fallback.
      return parsed??(LooksLikeHiddenUtilityMaterial(resolved)?resolved:null);
    }

    private void BuildAuthoritativeSkyRoomSet(){
      skyRoomNames.Clear();if(area==null)return;
      foreach(AreaEnvironmentScheme scheme in area.EnvironmentSchemes.Values){
        string reference=NormalizeRoom(scheme?.SkySceneRoom);if(TryResolveSkyRoom(reference,out string resolved))skyRoomNames.Add(resolved);
      }
      Room defaultSky=FindDefaultSkyRoom();if(defaultSky!=null)skyRoomNames.Add(defaultSky.RoomName);
      System.Diagnostics.Debug.WriteLine("World Browser skyscenes: "+(skyRoomNames.Count==0?"(none)":string.Join(", ",skyRoomNames.OrderBy(x=>x,StringComparer.OrdinalIgnoreCase))));
    }

    private Room FindDefaultSkyRoom(){
      if(area==null||area.DefaultSkySceneAssetId==0)return null;
      return rooms.FirstOrDefault(r=>r!=null&&r.RoomId==area.DefaultSkySceneAssetId);
    }

    private string ResolveSkyRoomName(AreaEnvironmentScheme env){
      // Same selection order as Jedipedia currentSkysceneIndex(): active room scheme first; when there is no usable
      // active reference, the area's default skydome. If camera-room lookup has no room yet, caller passes the area
      // scheme and therefore gets its skyscene_room before the same numeric default.
      string named=NormalizeRoom(env?.SkySceneRoom);if(TryResolveSkyRoom(named,out string resolved))return resolved;
      Room defaultSky=FindDefaultSkyRoom();if(defaultSky!=null)return defaultSky.RoomName;
      return null;
    }

    private bool TryResolveSkyRoom(string name,out string resolved){
      resolved=null;if(string.IsNullOrWhiteSpace(name)||name=="_everywhere_")return false;string normalized=NormalizeRoom(name);Room room=null;
      // Jedipedia has already canonicalised resource references by the time currentSkysceneIndex() runs. SWTOR
      // clients encountered offline can still leave either a full resource path or a numeric room-id reference.
      // Resolve all three forms without falling back to dangerous room-content heuristics.
      if(TryParseRoomIdReference(normalized,out ulong roomId))room=rooms.FirstOrDefault(r=>r!=null&&r.RoomId==roomId);
      if(room==null)room=rooms.FirstOrDefault(r=>r!=null&&string.Equals(NormalizeRoom(r.RoomName),normalized,StringComparison.OrdinalIgnoreCase));
      if(room==null&&normalized.IndexOf('/')>=0){string leaf=normalized.Substring(normalized.LastIndexOf('/')+1);room=rooms.FirstOrDefault(r=>r!=null&&string.Equals(NormalizeRoom(r.RoomName),leaf,StringComparison.OrdinalIgnoreCase));}
      if(room==null)return false;resolved=room.RoomName;return true;
    }

    private static bool TryParseRoomIdReference(string referenceText,out ulong id){
      id=0;if(string.IsNullOrWhiteSpace(referenceText))return false;string text=referenceText.Trim();int split=text.IndexOf('|');
      if(split>0&&uint.TryParse(text.Substring(0,split),out uint low)&&uint.TryParse(text.Substring(split+1),out uint high)){id=((ulong)high<<32)|low;return id!=0;}
      return ulong.TryParse(text,out id)&&id!=0;
    }

    private bool RoomVisible(Room r,HashSet<string> visible)=>visible==null||visible.Contains(r.RoomName);
    // A follower-owned traffic model can leave the static room that authored it. Jedipedia deliberately bypasses
    // that room's dPVS gate for the moving subtree; otherwise a ship disappears as soon as its owner room is culled.
    private bool InstanceRoomVisible(AssetInstance inst,Room room,HashSet<string> visible)=>inst!=null&&inst.PathFollowerAnimated||RoomVisible(room,visible);

    private float GetOrthographicHalfHeight(WorldRenderSettings s){
      float zoom=Math.Max(OrthographicZoomMin,Math.Min(OrthographicZoomMax,orthographicZoom));
      return Math.Max(.001f,(float)Math.Tan(GetFieldOfViewRadians(s)*.5f)*OrthographicReferenceDistance*zoom);
    }

    private static float GetOrthographicReferenceHalfHeight(){
      return (float)Math.Tan(SlimDXNet.MathF.ToRadians(50f)*.5f)*OrthographicReferenceDistance;
    }

    private float GetOrthographicMoveScale(WorldRenderSettings s){
      return OrthographicMoveSpeedScale*GetOrthographicHalfHeight(s)/Math.Max(.001f,GetOrthographicReferenceHalfHeight());
    }

    private static Vector3 HorizontalUnit(Vector3 v,Vector3 fallback){
      Vector3 h=new Vector3(v.X,0f,v.Z);
      if(h.LengthSquared()<.000001f)h=new Vector3(fallback.X,0f,fallback.Z);
      if(h.LengthSquared()<.000001f)h=Vector3.UnitZ;
      h.Normalize();return h;
    }

    private void ApplyOrthographicOrientation(){
      orthographicHeading=HorizontalUnit(orthographicHeading,Vector3.UnitZ);
      orthographicPitch=Math.Max(OrthographicMinPitch,Math.Min(OrthographicMaxPitch,orthographicPitch));
      float cp=(float)Math.Cos(orthographicPitch),sp=(float)Math.Sin(orthographicPitch);
      Vector3 look=orthographicHeading*cp-Vector3.UnitY*sp;
      Vector3 up=orthographicHeading*sp+Vector3.UnitY*cp;
      camera.LookAt(camera.Position,camera.Position+look,up);
    }

    private void SetOrthographicMode(bool enabled){
      if(orthographicActive==enabled)return;
      if(enabled){
        Vector3 perspectiveLook=camera.Look;if(!IsFinite(perspectiveLook)||perspectiveLook.LengthSquared()<.000001f)perspectiveLook=Vector3.UnitZ;else perspectiveLook.Normalize();
        perspectivePitchBeforeOrthographic=(float)Math.Asin(Math.Max(-1f,Math.Min(1f,-perspectiveLook.Y)));havePerspectivePitchBeforeOrthographic=true;
        orthographicHeading=HorizontalUnit(perspectiveLook,camera.Up);orthographicPitch=OrthographicMaxPitch;orthographicZoom=orthographicZoomTarget=1f;
        orthographicActive=true;
        ApplyOrthographicOrientation();
        // Jedipedia samples the floor on the wheel notch, not on projection entry. Until then the camera tracks
        // only the projection-box delta, so toggling ortho never unexpectedly drops the viewpoint through a roof.
        orthographicFloorY=float.NaN;
      }else{
        orthographicActive=false;orthographicPanning=false;orthographicZoom=orthographicZoomTarget=1f;orthographicFloorY=float.NaN;
        if(havePerspectivePitchBeforeOrthographic){
          // Jedipedia restores only the old perspective pitch. Yaw changes made while in plan view deliberately
          // survive the projection switch, so rotating the map also turns the perspective camera you return to.
          float cp=(float)Math.Cos(perspectivePitchBeforeOrthographic),sp=(float)Math.Sin(perspectivePitchBeforeOrthographic);
          Vector3 look=orthographicHeading*cp-Vector3.UnitY*sp;Vector3 up=orthographicHeading*sp+Vector3.UnitY*cp;
          camera.LookAt(camera.Position,camera.Position+look,up);
        }
        havePerspectivePitchBeforeOrthographic=false;
      }
      appliedOrthographicHalfHeight=-1f;
      InvalidateTemporalHistory();InvalidateObjectOcclusionVisibility();
    }

    private void UpdateOrthographicState(float dt,WorldRenderSettings s){
      bool desired=s?.OrthographicProjection==true;
      if(desired!=orthographicActive)SetOrthographicMode(desired);
      if(!orthographicActive||!(dt>0f))return;
      float oldHalf=GetOrthographicHalfHeight(s);
      float current=Math.Max(OrthographicZoomMin,Math.Min(OrthographicZoomMax,orthographicZoom));
      float target=Math.Max(OrthographicZoomMin,Math.Min(OrthographicZoomMax,orthographicZoomTarget));
      float ease=1f-(float)Math.Exp(-dt/OrthographicZoomSmoothingSeconds);
      if(Math.Abs(target/current-1f)>.0005f){
        orthographicZoom=current*(float)Math.Pow(target/current,ease);
        if(Math.Abs(target/orthographicZoom-1f)<.0005f)orthographicZoom=target;
      }else orthographicZoom=target;
      float newHalf=GetOrthographicHalfHeight(s);
      if(float.IsNaN(orthographicFloorY)||float.IsInfinity(orthographicFloorY)){
        if(Math.Abs(newHalf-oldHalf)>.000001f)camera.Position+=Vector3.UnitY*(newHalf-oldHalf);
      }else{
        float ceiling=Math.Max(OrthographicFloorClearance,activeClipDistance*OrthographicHeightClipShare);
        float above=Math.Max(OrthographicFloorClearance,Math.Min(ceiling,OrthographicFloorClearance+newHalf*OrthographicHeightPerBox));
        float targetY=orthographicFloorY+above;float gap=targetY-camera.Position.Y;
        if(Math.Abs(gap)<.001f)camera.Position=new Vector3(camera.Position.X,targetY,camera.Position.Z);
        else camera.Position+=Vector3.UnitY*(gap*ease);
      }
    }

    private void ApplyCameraLens(WorldRenderSettings s,float cameraFar,bool force=false){
      float fov=GetFieldOfViewRadians(s);float aspect=Math.Max(.01f,AspectRatio);bool ortho=orthographicActive&&s?.OrthographicProjection==true;
      if(worldConversationCameraActive){
        float conversationFov=camera.FovY>.01f?camera.FovY:fov;
        camera.SetLens(conversationFov,aspect,.01f,cameraFar);appliedOrthographicProjection=false;appliedOrthographicHalfHeight=-1f;appliedCameraFar=cameraFar;return;
      }
      if(ortho){
        float halfHeight=GetOrthographicHalfHeight(s);
        if(!force&&appliedOrthographicProjection&&Math.Abs(appliedCameraFar-cameraFar)<.01f&&Math.Abs(camera.Aspect-aspect)<.0001f&&Math.Abs(camera.FovY-fov)<.0001f&&Math.Abs(appliedOrthographicHalfHeight-halfHeight)<.0001f)return;
        camera.SetOrthographicLens(fov,aspect,halfHeight*2f*aspect,halfHeight*2f,.01f,cameraFar);
        appliedOrthographicProjection=true;appliedOrthographicHalfHeight=halfHeight;
      }else{
        if(!force&&!appliedOrthographicProjection&&Math.Abs(appliedCameraFar-cameraFar)<.01f&&Math.Abs(camera.Aspect-aspect)<.0001f&&Math.Abs(camera.FovY-fov)<.0001f)return;
        camera.SetLens(fov,aspect,.01f,cameraFar);appliedOrthographicProjection=false;appliedOrthographicHalfHeight=-1f;
      }
      appliedCameraFar=cameraFar;InvalidateTemporalHistory();
    }

    private static float GetFieldOfViewRadians(WorldRenderSettings s){
      float degrees=Math.Max(50f,Math.Min(75f,s?.FieldOfViewDegrees??50f));
      return SlimDXNet.MathF.ToRadians(degrees);
    }

    private static float GetActiveClipDistance(AreaEnvironmentScheme env,WorldRenderSettings s){
      float scale=Math.Max(1f,Math.Min(10f,s?.ViewDistanceScale??DefaultViewDistanceScale));
      float authored=env?.ClipDistance??0f;
      float clip=authored>0f?authored*scale:DefaultStaticClipDistance*(scale/DefaultViewDistanceScale);
      return Math.Max(20f,clip);
    }

    private float GetCameraFarDistance(float logicalClip,WorldRenderSettings s){
      float scale=Math.Max(1f,Math.Min(10f,s?.ViewDistanceScale??DefaultViewDistanceScale));
      // The dropdown is the explicit World Browser render budget: 1x/2x/3x/5x/10x =
      // 500/1000/1500/2500/5000 world units. The authored clip remains available through logicalClip for
      // environment/fog fidelity, but it must not unexpectedly collapse the user's selected world view.
      return Math.Max(20f,CameraFarPerViewScale*scale);
    }

    private void UpdateActiveClipDistance(AreaEnvironmentScheme env,WorldRenderSettings s){
      activeClipDistance=GetActiveClipDistance(env,s);
      float cameraFar=GetCameraFarDistance(activeClipDistance,s);
      ApplyCameraLens(s,cameraFar);
    }

    private bool RoomWithinCameraRange(Room room){
      if(room==null||!IsFinite(room.VisibilityMin)||!IsFinite(room.VisibilityMax))return true;
      // Zero radius means "no authored bounds". Keep it conservative instead of treating the room as
      // a point at the origin. Unlike frustum culling, this range test cannot make rooms pop in/out when
      // the free camera merely rotates. The D3D clip planes still reject off-screen geometry on the GPU.
      if(!(room.VisibilityRadius>.0001f)||room.VisibilityMax.X<room.VisibilityMin.X||room.VisibilityMax.Y<room.VisibilityMin.Y||room.VisibilityMax.Z<room.VisibilityMin.Z)return true;
      Vector3 center=(room.VisibilityMin+room.VisibilityMax)*.5f;
      Vector3 half=(room.VisibilityMax-room.VisibilityMin)*.5f;
      float radius=Math.Max(room.VisibilityRadius,half.Length());
      float distance=(center-camera.Position).Length()-radius;
      return distance<=camera.FarZ+RangeCullPadding;
    }

    private bool SphereWithinViewDistance(Vector3 center,float radius){
      if(!IsFinite(center))return false;
      float r=Math.Max(0f,radius);float limit=Math.Max(20f,camera.FarZ)+r+RangeCullPadding;
      return (center-camera.Position).LengthSquared()<=limit*limit;
    }
    private bool TerrainWithinViewDistance(AssetInstance inst,Room room){
      if(TryTerrainSphere(inst,room,out Vector3 center,out float radius))return SphereWithinViewDistance(center,radius);
      Matrix world=InstanceWorld(inst,room);return SphereWithinViewDistance(new Vector3(world.M41,world.M42,world.M43),0f);
    }
    private bool ModelWithinViewDistance(GR2 model,Matrix world){
      if(TryModelSphere(model,world,out Vector3 center,out float radius))return SphereWithinViewDistance(center,radius);
      return SphereWithinViewDistance(new Vector3(world.M41,world.M42,world.M43),0f);
    }
    private bool WaterWithinViewDistance(AssetInstance inst,Matrix world){
      Vector3 center=Vector3.TransformCoordinate(Vector3.Zero,world);
      float localRadius=.5f*(float)Math.Sqrt(Math.Max(0f,inst.width*inst.width+inst.height*inst.height+inst.depth*inst.depth));
      return SphereWithinViewDistance(center,localRadius*MatrixMaxScale(world));
    }

    private bool LocalBoundsVisible(Vector3 localMin,Vector3 localMax,Matrix world){
      Vector3 min=new Vector3(float.MaxValue,float.MaxValue,float.MaxValue),max=new Vector3(float.MinValue,float.MinValue,float.MinValue);
      for(int z=0;z<2;z++)for(int y=0;y<2;y++)for(int x=0;x<2;x++){
        Vector3 p=Vector3.TransformCoordinate(new Vector3(x==0?localMin.X:localMax.X,y==0?localMin.Y:localMax.Y,z==0?localMin.Z:localMax.Z),world);Expand(ref min,ref max,p);
      }
      Vector3 pad=new Vector3(FrustumCullPadding,FrustumCullPadding,FrustumCullPadding);return camera.Visible(new BoundingBox(min-pad,max+pad));
    }

    private bool TerrainInCameraFrustum(AssetInstance inst,Room room){
      HeightMap hm=inst?.HeightMap;if(hm==null)return true;int w=checked((int)hm.width),d=checked((int)hm.depth);
      float xmin=-.2f*(float)Math.Ceiling(.5f*(w-1)),zmin=-.2f*(float)Math.Ceiling(.5f*(d-1));
      Vector3 min=new Vector3(xmin,hm.MinElevation,zmin),max=new Vector3(xmin+.2f*(w-1),hm.MaxElevation,zmin+.2f*(d-1));
      return LocalBoundsVisible(min,max,InstanceWorld(inst,room));
    }

    private bool ModelInCameraFrustum(GR2 model,Matrix world){
      GR2_Bounding_Box box=model?.globalBox;if(box==null)return true;
      Vector3 min=new Vector3(box.minX,box.minY,box.minZ),max=new Vector3(box.maxX,box.maxY,box.maxZ);
      if(!IsFinite(min)||!IsFinite(max))return true;return LocalBoundsVisible(min,max,world);
    }

    private static float MatrixMaxScale(Matrix world){
      float sx=(float)Math.Sqrt(world.M11*world.M11+world.M12*world.M12+world.M13*world.M13);
      float sy=(float)Math.Sqrt(world.M21*world.M21+world.M22*world.M22+world.M23*world.M23);
      float sz=(float)Math.Sqrt(world.M31*world.M31+world.M32*world.M32+world.M33*world.M33);return Math.Max(.0001f,Math.Max(sx,Math.Max(sy,sz)));
    }

    private bool TryTerrainSphere(AssetInstance inst,Room room,out Vector3 center,out float radius){
      center=Vector3.Zero;radius=0;HeightMap hm=inst?.HeightMap;if(hm==null)return false;int w=checked((int)hm.width),d=checked((int)hm.depth);
      float xmin=-.2f*(float)Math.Ceiling(.5f*(w-1)),zmin=-.2f*(float)Math.Ceiling(.5f*(d-1));
      Vector3 localMin=new Vector3(xmin,hm.MinElevation,zmin),localMax=new Vector3(xmin+.2f*(w-1),hm.MaxElevation,zmin+.2f*(d-1));Matrix world=InstanceWorld(inst,room);
      Vector3 lc=(localMin+localMax)*.5f;center=Vector3.TransformCoordinate(lc,world);radius=(localMax-localMin).Length()*.5f*MatrixMaxScale(world);return true;
    }

    private bool TryModelSphere(GR2 model,Matrix world,out Vector3 center,out float radius){
      center=Vector3.Zero;radius=0;GR2_Bounding_Box box=model?.globalBox;if(box==null)return false;Vector3 min=new Vector3(box.minX,box.minY,box.minZ),max=new Vector3(box.maxX,box.maxY,box.maxZ);if(!IsFinite(min)||!IsFinite(max))return false;
      center=Vector3.TransformCoordinate((min+max)*.5f,world);radius=(max-min).Length()*.5f*MatrixMaxScale(world);return true;
    }

    private bool MapSphereVisible(Vector3 center,float radius){
      if(!IsFinite(center))return true;
      float r=Math.Max(0f,radius),halfW=Math.Max(.001f,mapVisibleWidth*.5f),halfH=Math.Max(.001f,mapVisibleHeight*.5f);
      return center.X+r>=mapCenter.X-halfW&&center.X-r<=mapCenter.X+halfW&&center.Z+r>=mapCenter.Y-halfH&&center.Z-r<=mapCenter.Y+halfH;
    }

    private bool ShadowRangeVisible(Vector3 center,float radius,float far){float d=far+radius+2f;return (center-camera.Position).LengthSquared()<=d*d;}

    private void DrawTerrain(Matrix vp,HashSet<string> visible,WorldRenderSettings s,AreaEnvironmentScheme cameraEnv,bool sceneShadows){
      if(s.Mode==WorldRenderMode.Map){
        Room mapActiveRoom=null;
        foreach(RenderEntry entry in MapVisibleRenderEntries(RenderKindTerrain)){
          Room room=entry.Room;AssetInstance inst=entry.Instance;
          if(room==null||inst==null||skyRoomNames.Contains(room.RoomName)||!RoomVisible(room,visible)||!InstanceVisibleOnMap(inst)||!inst.hasHeightMap||inst.VBO==null)continue;
          if(!ReferenceEquals(mapActiveRoom,room)){ApplyRoomEnvironment(room,cameraEnv,s,sceneShadows);mapActiveRoom=room;}
          DrawTerrainInstance(inst,room,vp,visible,s);
        }
        return;
      }
      // Normal perspective rendering never walks every terrain placement on the planet. The spatial index is
      // queried by the exact View budget, then each candidate still gets a sphere/range test in NearbyRenderEntries.
      Room activeRoom=null;
      foreach(RenderEntry entry in NearbyRenderEntries(RenderKindTerrain,camera.FarZ,true,visible)){
        Room room=entry.Room;AssetInstance inst=entry.Instance;
        if(room==null||!InstanceVisibleInWorld(inst,s)||skyRoomNames.Contains(room.RoomName)||!RoomVisible(room,visible)||inst.VBO==null)continue;
        if(!ReferenceEquals(activeRoom,room)){ApplyRoomEnvironment(room,cameraEnv,s,sceneShadows);activeRoom=room;}
        DrawTerrainInstance(inst,room,vp,visible,s);
      }
    }

    private void DrawTerrainInstance(AssetInstance inst,Room room,Matrix vp,HashSet<string> visible,WorldRenderSettings s){
      Matrix world=InstanceWorld(inst,room);fx.SetWorld(world);fx.SetViewProj(vp);
      Vector3 terrainLightPoint=new Vector3(world.M41,world.M42,world.M43);float terrainLightRadius=0f;
      if(TryTerrainSphere(inst,room,out Vector3 terrainCenter,out float terrainRadius)){terrainLightPoint=terrainCenter;terrainLightRadius=terrainRadius;}
      SetNearestLocalLights(terrainLightPoint,room,s,visible,true,false,false,terrainLightRadius);
      ImmediateContext.InputAssembler.SetVertexBuffers(0,new VertexBufferBinding(inst.VBO,PosNormalTexTan.Stride,0));
      terrainGpu.TryGetValue(inst,out TerrainGpu gpu);
      TerrainLodRange lod=SelectTerrainLod(inst,room,gpu,s);
      Buffer ibo=gpu?.LodIndexBuffer??inst.IBO;if(ibo==null)return;
      ImmediateContext.InputAssembler.SetIndexBuffer(ibo,Format.R16_UInt,0);
      int drawCount=lod.Count>0?lod.Count:inst.numFaces;int drawStart=lod.Count>0?lod.StartIndex:0;

      if(s.Mode==WorldRenderMode.Heightmap){
        fx.SetMaterial(null);fx.Height.GetPassByIndex(0).Apply(ImmediateContext);ImmediateContext.DrawIndexed(drawCount,drawStart,0);return;
      }
      if(s.Mode==WorldRenderMode.Wireframe){
        fx.SetMaterial(null);fx.Wire.GetPassByIndex(0).Apply(ImmediateContext);ImmediateContext.DrawIndexed(drawCount,drawStart,0);return;
      }

      EnsureTerrainTextures(inst,gpu);

      // Jedipedia's terrain path first lays down black + depth, then sums every splat layer with ONE/ONE.
      // That guarantees complete coverage even when the first material's mask is zero at a texel.
      fx.SetMaterial(null);fx.TerrainCoverage.GetPassByIndex(0).Apply(ImmediateContext);ImmediateContext.DrawIndexed(drawCount,drawStart,0);
      var layers=inst.HeightMap?.TerrainLayers;
      if(layers==null||layers.Count==0){
        GR2_Material fallback=GetTerrainMaterial("terrain_checkered");
        fx.SetTerrain(fallback,null,gpu?.ColorMap,1f,new Vector2(2f/Math.Max(1u,inst.HeightMap.width),2f/Math.Max(1u,inst.HeightMap.depth)));
        PickTerrainTech(s,true,false).GetPassByIndex(0).Apply(ImmediateContext);ImmediateContext.DrawIndexed(drawCount,drawStart,0);
        DrawTerrainLocalLightOverflow(drawCount,drawStart);return;
      }
      foreach(TerrainLayerMask layer in layers){
        GR2_Material mat=GetTerrainMaterial(layer.MaterialName);ShaderResourceView mask=null;if(gpu!=null)gpu.Masks.TryGetValue(layer.MaterialName,out mask);
        fx.SetTerrain(mat,mask,gpu?.ColorMap,1f,new Vector2(2f/Math.Max(1u,inst.HeightMap.width),2f/Math.Max(1u,inst.HeightMap.depth)));
        PickTerrainTech(s,true,false).GetPassByIndex(0).Apply(ImmediateContext);ImmediateContext.DrawIndexed(drawCount,drawStart,0);
        DrawTerrainLocalLightOverflow(drawCount,drawStart);
      }
    }

    private void DrawTerrainLocalLightOverflow(int drawCount,int drawStart){
      LocalLightSelection selection=currentLocalLightSelection;if(selection==null||selection.Count<=LocalLightBaseSlots)return;
      for(int offset=LocalLightBaseSlots;offset<selection.Count;offset+=LocalLightBaseSlots){
        if(BindLocalLightChunk(selection,offset)<=0)break;fx.TerrainLocalLightAdd.GetPassByIndex(0).Apply(ImmediateContext);ImmediateContext.DrawIndexed(drawCount,drawStart,0);
      }
      InvalidateTrackedLocalLightBinding();BindLocalLightSelection(selection);
    }

    private TerrainLodRange SelectTerrainLod(AssetInstance inst,Room room,TerrainGpu gpu,WorldRenderSettings s){
      if(gpu?.LodRanges==null||gpu.LodRanges.Length==0)return new TerrainLodRange(0,inst.numFaces);
      if(s.Mode==WorldRenderMode.Map)return gpu.LodRanges[0]; // Jedipedia favors map correctness over one-shot speed.
      HeightMap hm=inst.HeightMap;if(hm==null)return gpu.LodRanges[0];
      Matrix world=InstanceWorld(inst,room);Vector3 localCenter=new Vector3(0,(hm.MinElevation+hm.MaxElevation)*.5f,0);Vector3 center=Vector3.TransformCoordinate(localCenter,world);
      float rx=Math.Max(0,(hm.width-1)*.1f),rz=Math.Max(0,(hm.depth-1)*.1f),ry=Math.Max(0,(hm.MaxElevation-hm.MinElevation)*.5f);
      float sx=(float)Math.Sqrt(world.M11*world.M11+world.M12*world.M12+world.M13*world.M13),sy=(float)Math.Sqrt(world.M21*world.M21+world.M22*world.M22+world.M23*world.M23),sz=(float)Math.Sqrt(world.M31*world.M31+world.M32*world.M32+world.M33*world.M33);
      float maxScale=Math.Max(.0001f,Math.Max(sx,Math.Max(sy,sz)));float radius=(float)Math.Sqrt(rx*rx+ry*ry+rz*rz)*maxScale;
      // In orthographic mode screen size no longer depends on physical camera distance. Jedipedia feeds its LOD
      // system an equivalent perspective distance derived from the zoom box so zooming still selects sensible LODs.
      float distance=orthographicActive?Math.Max(0f,OrthographicReferenceDistance*orthographicZoom-radius):Math.Max(0,(camera.Position-center).Length()-radius);
      int level=0;while(level<terrainLodDistances.Length&&distance>=terrainLodDistances[level])level++;
      return gpu.LodRanges[Math.Min(level,gpu.LodRanges.Length-1)];
    }

    private EffectTechnique PickTerrainTech(WorldRenderSettings s,bool additive,bool dummy){if(s.Mode==WorldRenderMode.Wireframe)return fx.Wire;bool lit=s.Mode!=WorldRenderMode.Unlit&&s.EnableLighting;return lit?(additive?fx.TerrainAddLit:fx.TerrainLit):(additive?fx.TerrainAddUnlit:fx.TerrainUnlit);}

    private const float DynamicDetailRenderDistance = 7.5f;

    private void DrawDynamicDetails(Matrix vp,HashSet<string> visible,WorldRenderSettings s,AreaEnvironmentScheme cameraEnv,bool sceneShadows){
      if(area==null||dynamicDetailLayout==null)return;
      ImmediateContext.InputAssembler.InputLayout=dynamicDetailLayout;ImmediateContext.InputAssembler.PrimitiveTopology=PrimitiveTopology.TriangleList;
      try{
        Room activeRoom=null;
        foreach(RenderEntry entry in NearbyRenderEntries(RenderKindTerrain,DynamicDetailRenderDistance,true,visible)){
          Room room=entry.Room;AssetInstance inst=entry.Instance;
          if(room==null||!InstanceVisibleInWorld(inst,s)||skyRoomNames.Contains(room.RoomName)||!RoomVisible(room,visible)||inst.HeightMap?.DynamicDetails==null||inst.HeightMap.DynamicDetails.Count==0)continue;
          float distance=DynamicDetailTerrainDistance(inst,room);if(distance>=DynamicDetailRenderDistance)continue;
          if(!ReferenceEquals(activeRoom,room)){ApplyRoomEnvironment(room,cameraEnv,s,sceneShadows);activeRoom=room;}
          List<DynamicDetailGpu> batches=EnsureDynamicDetails(inst);List<DynamicDetailMeshBatch> meshBatches=EnsureDynamicDetailMeshes(inst);if((batches==null||batches.Count==0)&&meshBatches.Count==0)continue;
          Matrix world=entry.World;fx.SetWorld(world);fx.SetViewProj(vp);SetNearestLocalLights(new Vector3(world.M41,world.M42,world.M43),room,s,visible,true);
          foreach(DynamicDetailGpu batch in batches){
            if(batch.Buffer==null||batch.Count<=0||batch.Material==null)continue;
            // DYD batches retain a material reference for the life of the terrain tile. The texture LRU may evict
            // that MAT while the batch remains alive, so re-touch/reparse it before drawing. Otherwise vegetation
            // becomes the opaque green fallback quads seen after the streaming changes.
            GR2_Material dynamicMaterial=EnsureMaterialParsed(batch.Material);
            // Billboard DYD without its diffuse/alpha texture is not a useful fallback: it becomes a solid green
            // rectangle. Fail open by omitting only that broken grass card until its MAT can be streamed again.
            if(dynamicMaterial==null||dynamicMaterial.diffuseSRV==null)continue;
            if(dynamicMaterial.alphaTestValue<=0)dynamicMaterial.alphaTestValue=.5f;
            else if(dynamicMaterial.alphaTestValue>1)dynamicMaterial.alphaTestValue=Math.Min(1f,dynamicMaterial.alphaTestValue/255f);
            fx.SetMaterial(dynamicMaterial);fx.SetDynamicDetail(camera.Right,batch.AtlasMode,batch.Wind,dynamicMaterial.vegetationParams2.Z,batch.TextureSize);
            ImmediateContext.InputAssembler.SetVertexBuffers(0,new VertexBufferBinding(batch.Buffer,48,0));
            fx.DynamicDetail.GetPassByIndex(0).Apply(ImmediateContext);ImmediateContext.Draw(batch.Count,0);
          }
          if(meshBatches.Count>0){
            ImmediateContext.InputAssembler.InputLayout=instancedLayout;
            foreach(DynamicDetailMeshBatch meshBatch in meshBatches)if(meshBatch.Model!=null&&meshBatch.Model.enabled&&meshBatch.InstanceBuffer!=null&&meshBatch.InstanceCount>0)DrawInstancedModel(meshBatch.Model,meshBatch.InstanceBuffer,meshBatch.InstanceCount,world,vp,s);
            ImmediateContext.InputAssembler.InputLayout=dynamicDetailLayout;
          }
        }
      } finally { ImmediateContext.InputAssembler.InputLayout=inputLayout;ImmediateContext.InputAssembler.PrimitiveTopology=PrimitiveTopology.TriangleList; }
    }

    // Keep billboard vegetation to a game-like near-field draw distance; geometry/terrain itself remains unchanged.
    private float DynamicDetailTerrainDistance(AssetInstance inst,Room room){
      HeightMap hm=inst?.HeightMap;if(hm==null)return float.MaxValue;Matrix world=InstanceWorld(inst,room);
      Vector3 center=Vector3.TransformCoordinate(new Vector3(0,(hm.MinElevation+hm.MaxElevation)*.5f,0),world);
      float rx=Math.Max(0,(hm.width-1)*.1f),rz=Math.Max(0,(hm.depth-1)*.1f);
      float sx=(float)Math.Sqrt(world.M11*world.M11+world.M12*world.M12+world.M13*world.M13),sz=(float)Math.Sqrt(world.M31*world.M31+world.M32*world.M32+world.M33*world.M33);
      rx*=Math.Max(.0001f,sx);rz*=Math.Max(.0001f,sz);
      float dx=Math.Max(0,Math.Abs(camera.Position.X-center.X)-rx),dz=Math.Max(0,Math.Abs(camera.Position.Z-center.Z)-rz);return (float)Math.Sqrt(dx*dx+dz*dz);
    }

    private List<DynamicDetailGpu> EnsureDynamicDetails(AssetInstance inst){
      if(dynamicDetailGpu.TryGetValue(inst,out List<DynamicDetailGpu> cached))return cached;
      var result=new List<DynamicDetailGpu>();dynamicDetailGpu[inst]=result;HeightMap hm=inst.HeightMap;if(hm==null||area==null)return result;
      foreach(DynamicDetailPaint paint in hm.DynamicDetails){
        if(paint==null||paint.ChannelId<0||paint.ChannelId>=32)continue; // mesh DYD is handled separately below in a later pass.
        if(!area.DynamicDetailTextures.TryGetValue((uint)paint.ChannelId,out AreaDynamicDetailTexture texture)||texture==null||texture.IsMesh)continue;
        if(!area.DynamicDetailChannelParams.TryGetValue((uint)paint.ChannelId,out AreaDynamicDetailChannelParam param)||param?.Type!=0||param.Values==null)continue;
        GR2_Material material=GetDynamicDetailMaterial(texture.MaterialName);if(material==null)continue;
        List<DynamicDetailPlacement> placements=DynamicDetailScatter.Build(hm,paint,param,false,100,20000);if(placements.Count==0)continue;
        float[] vertices=BuildDynamicDetailVertices(placements);if(vertices.Length==0)continue;
        var bd=new BufferDescription(sizeof(float)*vertices.Length,ResourceUsage.Immutable,BindFlags.VertexBuffer,CpuAccessFlags.None,ResourceOptionFlags.None,0);using(var ds=new DataStream(vertices,false,false))result.Add(new DynamicDetailGpu{Buffer=new Buffer(Device,ds,bd),Count=placements.Count*6,AtlasMode=(int)(param.Values.Length>0?param.Values[0]:0),Wind=param.Values.Length>13?param.Values[13]/255f:0f,TextureSize=GetDdsTextureSize(material.diffuseDDS),Material=material,ChannelId=paint.ChannelId});
      }
      return result;
    }

    private List<DynamicDetailMeshBatch> EnsureDynamicDetailMeshes(AssetInstance inst){
      if(dynamicDetailMeshBatches.TryGetValue(inst,out List<DynamicDetailMeshBatch> cached))return cached;
      var result=new List<DynamicDetailMeshBatch>();dynamicDetailMeshBatches[inst]=result;HeightMap hm=inst?.HeightMap;if(hm==null||area==null)return result;
      foreach(DynamicDetailPaint paint in hm.DynamicDetails){
        if(paint==null||paint.ChannelId<32)continue;
        if(!dynamicDetailMeshModels.TryGetValue((uint)paint.ChannelId,out GR2 model)||model==null)continue;
        if(!area.DynamicDetailChannelParams.TryGetValue((uint)paint.ChannelId,out AreaDynamicDetailChannelParam param)||param?.Type!=0||param.Values==null)continue;
        List<DynamicDetailPlacement> placements=DynamicDetailScatter.Build(hm,paint,param,true,100,5000);if(placements.Count==0)continue;
        Buffer instanceBuffer=BuildDynamicDetailInstanceBuffer(placements,param.Values);if(instanceBuffer==null)continue;
        result.Add(new DynamicDetailMeshBatch{ChannelId=paint.ChannelId,Model=model,Params=param,Placements=placements,InstanceBuffer=instanceBuffer,InstanceCount=placements.Count});
      }
      return result;
    }

    private Buffer BuildDynamicDetailInstanceBuffer(List<DynamicDetailPlacement> placements,uint[] values){
      if(placements==null||placements.Count==0)return null;
      var data=new float[checked(placements.Count*16)];int o=0;
      foreach(DynamicDetailPlacement p in placements){
        Matrix m=BuildDynamicDetailMeshLocal(p,values);
        data[o++]=m.M11;data[o++]=m.M12;data[o++]=m.M13;data[o++]=m.M14;
        data[o++]=m.M21;data[o++]=m.M22;data[o++]=m.M23;data[o++]=m.M24;
        data[o++]=m.M31;data[o++]=m.M32;data[o++]=m.M33;data[o++]=m.M34;
        data[o++]=m.M41;data[o++]=m.M42;data[o++]=m.M43;data[o++]=m.M44;
      }
      var bd=new BufferDescription(sizeof(float)*data.Length,ResourceUsage.Immutable,BindFlags.VertexBuffer,CpuAccessFlags.None,ResourceOptionFlags.None,0);using var ds=new DataStream(data,false,false);return new Buffer(Device,ds,bd);
    }

    private static Matrix BuildDynamicDetailMeshLocal(DynamicDetailPlacement p,uint[] values){
      Matrix local;float scale=Math.Max(.0001f,p.Scale);int mode14=values!=null&&values.Length>14?(int)values[14]:0;int align=values!=null&&values.Length>0?(int)values[0]:0;
      if(mode14==1){
        // Native D3DX yaw/pitch/roll: roll -> pitch -> yaw, then the uniform scale and translation.
        Matrix rotation=Matrix.RotationYawPitchRoll(p.Rotation.Y,p.Rotation.X,p.Rotation.Z);local=Matrix.Scaling(scale,scale,scale)*rotation*Matrix.Translation(p.Position);
      } else {
        Matrix rotation=Matrix.Identity;
        if(align==1)rotation=Matrix.RotationZ(-p.SurfaceNormal.X)*Matrix.RotationX(p.SurfaceNormal.Z);
        if(mode14==3)rotation*=Matrix.RotationY(p.Rotation.Y);
        local=Matrix.Scaling(scale,scale,scale)*rotation*Matrix.Translation(p.Position);
      }
      return local;
    }

    private void BuildDynamicDetailMeshModels(){
      dynamicDetailMeshModels.Clear();if(area==null)return;
      var used=new HashSet<uint>();foreach(Room room in rooms)foreach(AssetInstance inst in room.InstancesById.Values)if(inst.HeightMap?.DynamicDetails!=null)foreach(DynamicDetailPaint paint in inst.HeightMap.DynamicDetails)if(paint.ChannelId>=32)used.Add((uint)paint.ChannelId);
      var byPath=new Dictionary<string,GR2>(StringComparer.OrdinalIgnoreCase);
      foreach(uint channel in used){
        if(!area.DynamicDetailTextures.TryGetValue(channel,out AreaDynamicDetailTexture entry)||entry==null||!entry.IsMesh||string.IsNullOrWhiteSpace(entry.MeshPath))continue;
        if(byPath.TryGetValue(entry.MeshPath,out GR2 shared)){dynamicDetailMeshModels[channel]=shared;continue;}
        try{using TorArchive.File file=area.FindFile(entry.MeshPath);if(file==null)continue;using Stream stream=file.OpenCopyInMemory();using var br=new BinaryReader(stream);var model=new GR2(br,"dyd_"+channel,materials);byPath[entry.MeshPath]=model;dynamicDetailMeshModels[channel]=model;}catch(Exception ex){System.Diagnostics.Debug.WriteLine("Could not load DYD mesh "+entry.MeshPath+": "+ex.Message);}
      }
    }

    private static float[] BuildDynamicDetailVertices(List<DynamicDetailPlacement> placements){
      if(placements==null||placements.Count==0)return Array.Empty<float>();
      // Two triangles, matching Jedipedia's shared +/-0.9 billboard card.
      float[] corners={-.9f,-.9f,.9f,-.9f,-.9f,.9f,-.9f,.9f,.9f,-.9f,.9f,.9f};
      var data=new float[checked(placements.Count*6*12)];int o=0;
      foreach(DynamicDetailPlacement p in placements)for(int v=0;v<6;v++){
        data[o++]=corners[v*2];data[o++]=corners[v*2+1];
        data[o++]=p.Position.X;data[o++]=p.Position.Y;data[o++]=p.Position.Z;data[o++]=p.Scale;
        data[o++]=p.Tint.X;data[o++]=p.Tint.Y;data[o++]=p.Tint.Z;data[o++]=p.AtlasPacked;data[o++]=p.Flip;data[o++]=p.PackedNormal;
      }
      return data;
    }

    private Vector2 GetDdsTextureSize(string path){
      if(string.IsNullOrWhiteSpace(path)||area==null)return new Vector2(1,1);
      try{using TorArchive.File file=area.FindFile(path);if(file==null)return new Vector2(1,1);using Stream stream=file.OpenCopyInMemory();using var br=new BinaryReader(stream);if(stream.Length<20||br.ReadUInt32()!=0x20534444)return new Vector2(1,1);stream.Position=12;uint height=br.ReadUInt32(),width=br.ReadUInt32();return new Vector2(Math.Max(1u,width),Math.Max(1u,height));}catch{return new Vector2(1,1);}
    }

    private GR2_Material GetDynamicDetailMaterial(string name){
      if(string.IsNullOrWhiteSpace(name))return null;
      GR2_Material material=null;
      if(materials.TryGetValue(name,out GR2_Material shared))material=EnsureMaterialParsed(shared);
      else if(dynamicDetailMaterials.TryGetValue(name,out GR2_Material cached))material=EnsureMaterialParsed(cached);
      else {material=new GR2_Material(name);dynamicDetailMaterials[name]=material;material=EnsureMaterialParsed(material);}
      if(material==null)return null;
      if(material.alphaTestValue<=0)material.alphaTestValue=.5f;else if(material.alphaTestValue>1)material.alphaTestValue=Math.Min(1f,material.alphaTestValue/255f);
      return material;
    }
    private static void ReleaseOwnedMaterial(GR2_Material m){if(m==null)return;Release(ref m.ageSRV);Release(ref m.complexionSRV);Release(ref m.diffuseSRV);Release(ref m.diffuse2SRV);Release(ref m.facepaintSRV);Release(ref m.glossSRV);Release(ref m.paletteMaskSRV);Release(ref m.paletteSRV);Release(ref m.rotationSRV);Release(ref m.waterSurfaceSRV);}

    private void DrawOccluderPrepass(Matrix vp,HashSet<string> visible,WorldRenderSettings s){
      if(occluderRenderEntries.Count==0)return;
      ClearLocalLightBinding();fx.SetViewProj(vp);fx.SetPlaceableBlueGlow(false);
      ImmediateContext.InputAssembler.InputLayout=inputLayout;ImmediateContext.InputAssembler.PrimitiveTopology=PrimitiveTopology.TriangleList;
      foreach(RenderEntry entry in occluderRenderEntries){
        if(entry?.Room==null||entry.Instance==null||entry.Model==null||!entry.Model.enabled||!RoomVisible(entry.Room,visible))continue;
        if(IsSpeedTreeInstance(entry.Instance)&&!s.ShowSpeedTrees)continue;
        if(!SphereWithinDistance(entry.Center,entry.Radius,camera.FarZ)||!SphereVisibleInCameraFrustum(entry.Center,entry.Radius))continue;
        Matrix world=entry.World;fx.SetWorld(world);DrawModelOccluderDepth(entry.Model,world,false);
      }
    }

    private void DrawModelOccluderDepth(GR2 model,Matrix world,bool placementOccluderOnly){
      if(model==null||!EnsureModelGeometryPrepared(model))return;int selectedLod=SelectModelLodLevel(model,world,false);
      foreach(GR2_Mesh mesh in model.meshes){
        if(mesh==null||mesh.vertBuffer==null||mesh.idxBuffer==null)continue;
        // Dedicated dPVS occlusion geometry is Granny LOD -3. An OCCLUDER_ONLY placement makes its ordinary
        // selected visual mesh an occluder as well, while collision (-1) and portal (-2) helpers remain excluded.
        bool draw=mesh.lod==-3||(placementOccluderOnly&&mesh.lod>=0&&mesh.LodLevel==selectedLod);if(!draw)continue;
        ImmediateContext.InputAssembler.SetVertexBuffers(0,new VertexBufferBinding(mesh.vertBuffer,PosNormalTexTan.Stride,0));
        ImmediateContext.InputAssembler.SetIndexBuffer(mesh.idxBuffer,Format.R16_UInt,0);
        fx.OccluderDepth.GetPassByIndex(0).Apply(ImmediateContext);
        foreach(GR2_Mesh_Piece piece in mesh.meshPieces)ImmediateContext.DrawIndexed((int)piece.numPieceFaces*3,(int)piece.startIndex*3,0);
      }
      foreach(GR2 attached in model.attachedModels)DrawModelOccluderDepth(attached,world,placementOccluderOnly);
    }

    private void DrawSky(AreaEnvironmentScheme env,WorldRenderSettings s){
      string activeSky=ResolveSkyRoomName(env);
      if(string.IsNullOrEmpty(activeSky)||activeSky=="_everywhere_")return;
      Room room=rooms.FirstOrDefault(r=>r!=null&&r.RoomName==activeSky);
      if(room==null)return;
      ApplyRoomEnvironment(room,env,s,false);ClearLocalLightBinding();
      Matrix skyCameraInv=Matrix.Identity;bool hasSkyCamera=TryGetSkyCameraInverse(room,out skyCameraInv);
      Matrix skyVp=GetSkyViewProjection();
      foreach(AssetInstance inst in room.InstancesById.Values){
        if(!InstanceVisibleInWorld(inst,s)||inst.hasHeightMap||inst.hasWater||!models.TryGetValue(inst.assetID,out GR2 model)||!model.enabled)continue;
        Matrix world=InstanceWorldForSky(inst,room);if(hasSkyCamera)world*=skyCameraInv;
        DrawModel(model,world,skyVp,s,true,null);
      }
    }

    private void DrawModels(Matrix vp,HashSet<string> visible,WorldRenderSettings s,AreaEnvironmentScheme env,bool sceneShadows){
      if(s.Mode==WorldRenderMode.Map){
        Room mapActiveRoom=null;
        foreach(RenderEntry entry in MapVisibleRenderEntries(RenderKindModel)){
          Room room=entry.Room;AssetInstance inst=entry.Instance;GR2 model=entry.Model;
          if(room==null||inst==null||model==null||!model.enabled||skyRoomNames.Contains(room.RoomName)||!RoomVisible(room,visible)||!InstanceVisibleOnMap(inst)||inst.hasHeightMap||inst.hasWater)continue;
          bool speedTree=IsSpeedTreeInstance(inst);if(speedTree&&!s.ShowSpeedTrees)continue;
          Matrix world=entry.World;if(!MapModelVisibleForCurrentMap(inst,model,world))continue;
          if(!ReferenceEquals(mapActiveRoom,room)){ApplyRoomEnvironment(room,env,s,sceneShadows);mapActiveRoom=room;}
          // Local lighting is intentionally disabled in map mode by SetNearestLocalLights, but keep the same call
          // path so future map-light settings do not diverge from the normal model renderer.
          SetNearestLocalLights(entry.Center,room,s,visible,false,false,false,Math.Max(0f,entry.Radius),speedTree);
          AreaEnvironmentMaterial envMat=area?.GetEnvironmentMaterial(room,inst);DrawModel(model,world,vp,s,false,envMat);
        }
        return;
      }

      // Jedipedia batches repeated opaque/test geometry with drawElementsInstanced. SWTOR open worlds contain
      // thousands of repeated rocks, trees and props, so distance culling alone still leaves the D3D11 render
      // thread draw-call bound. PugTools already had an instanced input path for DYD meshes; reuse it for safe
      // ordinary models and split batches by room + local-light cell so shared uniforms stay correct.
      var batches=new Dictionary<(Room Room,GR2 Model,int Lod,int X,int Y,int Z,bool SpeedTree),ModelInstanceBatch>();
      var singles=new List<(RenderEntry Entry,int Lod)>();
      bool splitLights=s.EnableLocalLights&&s.EnableLighting&&s.Mode!=WorldRenderMode.Unlit&&s.Mode!=WorldRenderMode.Wireframe&&s.Mode!=WorldRenderMode.Heightmap;
      foreach(RenderEntry entry in NearbyRenderEntries(RenderKindModel,camera.FarZ,true,visible)){
        Room room=entry.Room;AssetInstance inst=entry.Instance;GR2 model=entry.Model;
        if(room==null||inst==null||model==null||!model.enabled||skyRoomNames.Contains(room.RoomName)||!InstanceRoomVisible(inst,room,visible)||!InstanceVisibleInWorld(inst,s))continue;
        if(!RenderEntryVisibleByOcclusion(entry,s))continue;
        bool speedTree=IsSpeedTreeInstance(inst);if(speedTree&&!s.ShowSpeedTrees)continue;
        // Jedipedia's model LOD is not cosmetic: below the lowest schema threshold the asset stops contributing.
        // This removes the sea of tiny props that remained CPU/GPU work in v7 even after distance culling.
        if(ShouldCullModelByLod(model,entry.World,false,inst.LodFactor))continue;int lod=SelectModelLodLevel(model,entry.World,false,inst.LodFactor);
        if(!ModelCanUseRegularInstancing(model)||!MatrixHasNearlyUniformScale(entry.World)){singles.Add((entry,lod));continue;}
        Vector3 p=entry.Center;float lightRadius=Math.Max(0f,entry.Radius);
        bool splitThisLight=splitLights&&ReceiverCouldHaveIndexedLocalLights(p,lightRadius);
        int lx=splitThisLight?LightSelectionCell(p.X):0,ly=splitThisLight?LightSelectionCell(p.Y):0,lz=splitThisLight?LightSelectionCell(p.Z):0;
        var key=(room,model,lod,lx,ly,lz,speedTree);
        if(!batches.TryGetValue(key,out ModelInstanceBatch batch)){batch=new ModelInstanceBatch{Room=room,Model=model,LodLevel=lod,LightSample=p,LightRadius=lightRadius,SpeedTree=speedTree};batches[key]=batch;}
        else if(lightRadius>batch.LightRadius)batch.LightRadius=lightRadius;
        batch.Worlds.Add(entry.World);
      }

      Room activeRoom=null;
      foreach(var single in singles){
        RenderEntry entry=single.Entry;Room room=entry.Room;AssetInstance inst=entry.Instance;if(!ReferenceEquals(activeRoom,room)){ApplyRoomEnvironment(room,env,s,sceneShadows);activeRoom=room;}
        Matrix world=entry.World;bool speedTree=IsSpeedTreeInstance(inst);SetNearestLocalLights(entry.Center,room,s,visible,false,false,false,Math.Max(0f,entry.Radius),speedTree);
        AreaEnvironmentMaterial envMat=area?.GetEnvironmentMaterial(room,inst);DrawModel(entry.Model,world,vp,s,false,envMat,single.Lod);
      }
      regularModelFrameInstances.Clear();
      foreach(ModelInstanceBatch batch in batches.Values){
        if(batch.Worlds.Count<2)continue;
        batch.StartInstance=regularModelFrameInstances.Count;
        regularModelFrameInstances.AddRange(batch.Worlds);
      }
      Buffer sharedInstanceBuffer=regularModelFrameInstances.Count>0?UploadRegularModelInstances(regularModelFrameInstances):null;
      foreach(ModelInstanceBatch batch in batches.Values){
        if(batch.Worlds.Count<2){
          Matrix world=batch.Worlds[0];if(!ReferenceEquals(activeRoom,batch.Room)){ApplyRoomEnvironment(batch.Room,env,s,sceneShadows);activeRoom=batch.Room;}
          SetNearestLocalLights(batch.LightSample,batch.Room,s,visible,false,false,false,batch.LightRadius,batch.SpeedTree);DrawModel(batch.Model,world,vp,s,false,null,batch.LodLevel);continue;
        }
        if(!ReferenceEquals(activeRoom,batch.Room)){ApplyRoomEnvironment(batch.Room,env,s,sceneShadows);activeRoom=batch.Room;}
        SetNearestLocalLights(batch.LightSample,batch.Room,s,visible,false,false,false,batch.LightRadius,batch.SpeedTree);
        if(sharedInstanceBuffer==null){foreach(Matrix world in batch.Worlds)DrawModel(batch.Model,world,vp,s,false,null,batch.LodLevel);continue;}
        ImmediateContext.InputAssembler.InputLayout=instancedLayout;DrawInstancedModel(batch.Model,sharedInstanceBuffer,batch.Worlds.Count,Matrix.Identity,vp,s,batch.LodLevel,batch.Worlds[0],true,batch.StartInstance);
      }
      ImmediateContext.InputAssembler.InputLayout=inputLayout;
    }

    private void LoadStrongholdHookModels(){
      strongholdHookModels.Clear();
      if(area==null||rooms==null||rooms.Count==0)return;
      var requiredPaths=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      foreach(Room room in rooms)foreach(AssetInstance inst in room.InstancesById.Values){
        if(!InstanceVisibleInWorld(inst)||!inst.HasDecorationHook)continue;
        // Jedipedia's renderRoom2 checks the normal mesh branch before the hook branch. A placement that already
        // owns a Granny model therefore is not also expanded as a decoration field.
        if(models.TryGetValue(inst.assetID,out GR2 ownModel)&&ownModel!=null&&ownModel.meshes!=null&&ownModel.meshes.Count>0)continue;
        if(!StrongholdDecorationHooks.TryGetLayout(inst.DecorationHookLayoutId,out StrongholdDecorationHookPart[] parts)||parts==null)continue;
        foreach(StrongholdDecorationHookPart part in parts)if(part!=null&&!String.IsNullOrWhiteSpace(part.ModelPath))requiredPaths.Add(part.ModelPath);
      }
      foreach(string path in requiredPaths){
        TorArchive.File file=null;
        try{
          file=area.FindFile(path);
          if(file==null){System.Diagnostics.Debug.WriteLine("Stronghold hook GR2 not present in selected archives: "+path);continue;}
          using Stream stream=file.OpenCopyInMemory();using var br=new BinaryReader(stream);
          strongholdHookModels[path]=new GR2(br,path,materials);
        }catch(Exception ex){System.Diagnostics.Debug.WriteLine("Could not load Stronghold decoration hook "+path+": "+ex.Message);}
        finally{file?.Dispose();}
      }
    }

    private static Matrix DecorationHookLocalTransform(AssetInstance inst){
      if(inst==null)return Matrix.Identity;
      Matrix scale=inst.HasDecorationHook?Matrix.Identity:Matrix.Scaling(inst.scale);
      return scale*Matrix.RotationZ((float)(inst.rotation.Z*Math.PI/180.0))*Matrix.RotationX((float)(inst.rotation.X*Math.PI/180.0))*Matrix.RotationY((float)(inst.rotation.Y*Math.PI/180.0))*Matrix.Translation(inst.position);
    }

    private Matrix DecorationHookAnchorWorld(AssetInstance inst,Room room){
      if(inst==null)return Matrix.Identity;
      // Jedipedia forces scaleX/Y/Z to 1 on every aptPlacementLayoutLayoutId carrier before it builds parent
      // matrices. Apply the same rule to the whole hook-bearing parent chain, not only to the leaf placement.
      Matrix output=DecorationHookLocalTransform(inst);ulong parent=inst.parentInstance;var seen=new HashSet<ulong>();
      while(parent!=0&&seen.Add(parent)){
        if(room==null||!room.InstancesById.TryGetValue(parent,out AssetInstance p))break;
        output*=DecorationHookLocalTransform(p);parent=p.parentInstance;
      }
      return output;
    }

    private void BuildDecorationHookRenderEntries(){
      decorationHookRenderEntries.Clear();
      if(strongholdHookModels.Count==0)return;
      foreach(Room room in rooms){
        if(room==null||skyRoomNames.Contains(room.RoomName))continue;
        foreach(AssetInstance inst in room.InstancesById.Values){
          if(!InstanceVisibleInWorld(inst)||!inst.HasDecorationHook)continue;
          if(models.TryGetValue(inst.assetID,out GR2 ownModel)&&ownModel!=null&&ownModel.meshes!=null&&ownModel.meshes.Count>0)continue;
          if(!StrongholdDecorationHooks.TryGetLayout(inst.DecorationHookLayoutId,out StrongholdDecorationHookPart[] parts)||parts==null)continue;
          Matrix anchorWorld=DecorationHookAnchorWorld(inst,room);
          foreach(StrongholdDecorationHookPart part in parts){
            if(part==null||String.IsNullOrWhiteSpace(part.ModelPath)||!strongholdHookModels.TryGetValue(part.ModelPath,out GR2 model)||model==null||!model.enabled)continue;
            Matrix world=Matrix.Translation(part.Offset)*anchorWorld;
            Vector3 center=new Vector3(world.M41,world.M42,world.M43);float radius=.5f;
            if(TryModelSphere(model,world,out Vector3 modelCenter,out float modelRadius)){center=modelCenter;radius=modelRadius;}
            decorationHookRenderEntries.Add(new DecorationHookRenderEntry{Room=room,Instance=inst,Model=model,World=world,Center=center,Radius=Math.Max(.05f,radius),FallbackTint=DecorationHookFallbackTint(part.ModelPath)});
          }
        }
      }
    }

    private static Vector4 DecorationHookFallbackTint(string path){
      string p=(path??String.Empty).ToLowerInvariant();
      // Jedipedia's aptHookData reader explicitly matches its hook labels to the authored hook plates:
      // Small = green, Medium = blue, Large = purple. Use the same representative hues here.
      if(p.Contains("small"))return new Vector4(127f/255f,230f/255f,160f/255f,.62f);
      if(p.Contains("med"))return new Vector4(127f/255f,196f/255f,255f/255f,.52f);
      if(p.Contains("large"))return new Vector4(185f/255f,166f/255f,255f/255f,.46f);
      // Centerpiece/Starship/Massive Structure/etc. are intentionally neutral in Jedipedia rather
      // than being folded into the Large class just because their models are physically large.
      return new Vector4(227f/255f,246f/255f,253f/255f,.52f);
    }

    private static Vector4 DecorationHookMaterialTint(GR2_Material mat,Vector4 fallbackTint){
      if(mat==null||!mat.hasDiffuseFlatColor)return fallbackTint;
      Vector4 authored=mat.diffuseFlatColor;
      // A number of the Stronghold VFX MATs author diffuseFlatColorProp as neutral white/grey.
      // That is not the size-class colour: the real AnimatedUV/VFX shader gets the colour from its
      // other tint/fresnel inputs. PugTools does not emulate that whole shader family in World.fx,
      // so letting a neutral value replace the size tint turns every hook white. Preserve a genuinely
      // chromatic authored value, otherwise keep the known hook-class colour.
      float max=Math.Max(authored.X,Math.Max(authored.Y,authored.Z));
      float min=Math.Min(authored.X,Math.Min(authored.Y,authored.Z));
      if(max-min>.06f)return new Vector4(authored.X,authored.Y,authored.Z,fallbackTint.W);
      return fallbackTint;
    }

    private void DrawDecorationHookModel(GR2 model,Matrix world,Matrix vp,WorldRenderSettings s,Vector4 fallbackTint,int forcedLod=-1){
      if(model==null||!EnsureModelGeometryPrepared(model))return;int selectedLod=forcedLod>=0?forcedLod:SelectModelLodLevel(model,world,false);fx.SetWorld(world);fx.SetViewProj(vp);
      foreach(var mesh in model.meshes){if(mesh.vertBuffer==null||mesh.idxBuffer==null||!MeshVisibleForLod(model,mesh,selectedLod))continue;ImmediateContext.InputAssembler.SetVertexBuffers(0,new VertexBufferBinding(mesh.vertBuffer,PosNormalTexTan.Stride,0));ImmediateContext.InputAssembler.SetIndexBuffer(mesh.idxBuffer,Format.R16_UInt,0);
        foreach(var piece in mesh.meshPieces){GR2_Material mat=ResolvePieceMaterial(model,piece);if(IsMaterialHiddenFromWorld(mat))continue;fx.SetMaterial(mat);
          // The hook VFX use authored diffuseFlatColorProp where available. Older/client-specific hook MATs sometimes
          // omit it and rely on shader-family defaults; color those by SWTOR hook size instead of leaving grey.
          string alpha=mat?.alphaMode??"None";
          bool additive=string.Equals(alpha,"Add",StringComparison.OrdinalIgnoreCase);
          // Jedipedia draws the Stronghold hook GR2s as luminous editor guides. Use the authored MAT tint when
          // present, otherwise use SWTOR's familiar green/blue/purple size colors. The dedicated hook pass keeps
          // the field transparent even when an older/client-specific MAT reports an opaque alpha mode.
          Vector4 tint=DecorationHookMaterialTint(mat,fallbackTint);
          // Do not force additive hook pieces to white. In Jedipedia they run through the material's
          // real VFX shader and keep the hook hue; forcing white here was the direct cause of the
          // white plates/columns in PugTools. The additive pixel shader below keeps a bright core.
          fx.SetMaterialFlatColor(tint);fx.SetHookOpacity(additive?1f:fallbackTint.W);
          (additive?fx.HookAdditive:fx.HookOverlay).GetPassByIndex(0).Apply(ImmediateContext);
          ImmediateContext.DrawIndexed((int)piece.numPieceFaces*3,(int)piece.startIndex*3,0);}}
      foreach(var a in model.attachedModels)DrawDecorationHookModel(a,world,vp,s,fallbackTint);
    }

    private void DrawDecorationHooks(Matrix vp,HashSet<string> visible,WorldRenderSettings s,AreaEnvironmentScheme env,bool sceneShadows){
      if(!s.ShowDecorationHooks||decorationHookRenderEntries.Count==0)return;
      // Jedipedia's hook arm is never entered for a local-light sub-pass. Clear any receiver light state left by
      // the normal model pass before drawing the colored placement fields.
      fx.ClearLocalLights();currentLocalLightSelection=LocalLightSelection.Empty;lastLocalLightCount=0;Array.Clear(lastLocalLightSelection,0,lastLocalLightSelection.Length);
      Room activeRoom=null;
      foreach(DecorationHookRenderEntry entry in decorationHookRenderEntries){
        if(entry==null||entry.Room==null||entry.Model==null||!entry.Model.enabled||!InstanceRoomVisible(entry.Instance,entry.Room,visible))continue;
        if(!SphereWithinViewDistance(entry.Center,entry.Radius))continue;
        if(!ModelInCameraFrustum(entry.Model,entry.World))continue;
        if(ShouldCullModelByLod(entry.Model,entry.World,false,1f))continue;
        if(!ReferenceEquals(activeRoom,entry.Room)){ApplyRoomEnvironment(entry.Room,env,s,sceneShadows);activeRoom=entry.Room;}
        DrawDecorationHookModel(entry.Model,entry.World,vp,s,entry.FallbackTint,SelectModelLodLevel(entry.Model,entry.World,false,1f));
      }
      fx.ClearLocalLights();lastLocalLightCount=0;
    }

    private void LoadAndApplyLodSchemas(){
      lodSchemas.Clear();visualLodLevels.Clear();
      if(area!=null){
        TorArchive.File file=null;
        try{
          file=area.FindFile("/resources/art/LODSchemas3.lod")??area.FindFile("/resources/art/lodschemas3.lod");
          if(file!=null){
            using Stream stream=file.OpenCopyInMemory();using var reader=new StreamReader(stream,true);string text=reader.ReadToEnd();
            foreach(Match schemaMatch in Regex.Matches(text,@"<LODSchema\b([^>]*)>([\s\S]*?)</LODSchema>",RegexOptions.IgnoreCase)){
              string name=XmlAttributeValue(schemaMatch.Groups[1].Value,"name");if(string.IsNullOrWhiteSpace(name))continue;
              var thresholds=new List<float>();
              foreach(Match lodMatch in Regex.Matches(schemaMatch.Groups[2].Value,@"<LOD\b([^>]*)/?>",RegexOptions.IgnoreCase)){
                string raw=XmlAttributeValue(lodMatch.Groups[1].Value,"threshold");
                if(float.TryParse(raw,System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out float threshold)&&!float.IsNaN(threshold)&&!float.IsInfinity(threshold))thresholds.Add(threshold);
              }
              if(thresholds.Count>0)lodSchemas[name]=thresholds.ToArray();
            }
          }
        }catch(Exception ex){System.Diagnostics.Debug.WriteLine("Could not load LODSchemas3.lod: "+ex.Message);}finally{file?.Dispose();}
      }
      var seen=new HashSet<GR2>();foreach(GR2 model in models.Values)ApplyLodSchemaRecursive(model,seen);foreach(GR2 model in dynamicDetailMeshModels.Values)ApplyLodSchemaRecursive(model,seen);foreach(GR2 model in strongholdHookModels.Values)ApplyLodSchemaRecursive(model,seen);
    }

    private static string XmlAttributeValue(string attributes,string name){
      if(string.IsNullOrEmpty(attributes)||string.IsNullOrEmpty(name))return string.Empty;
      Match match=Regex.Match(attributes,@"(?:^|\s)"+Regex.Escape(name)+@"\s*=\s*([""'])(.*?)\1",RegexOptions.IgnoreCase);
      return match.Success?System.Net.WebUtility.HtmlDecode(match.Groups[2].Value):string.Empty;
    }

    private void ApplyLodSchemaRecursive(GR2 model,HashSet<GR2> seen){
      if(model==null||!seen.Add(model))return;float[] thresholds=null;string name=model.lodSchemaName??"granny_legacy_default";
      if(!lodSchemas.TryGetValue(name,out thresholds))lodSchemas.TryGetValue("granny_legacy_default",out thresholds);
      model.lodThresholds=thresholds??Array.Empty<float>();
      foreach(GR2 attached in model.attachedModels)ApplyLodSchemaRecursive(attached,seen);
    }

    private int[] GetVisualLodLevels(GR2 model){
      if(model==null)return Array.Empty<int>();if(visualLodLevels.TryGetValue(model,out int[] cached))return cached;
      int[] levels=model.meshes.Where(mesh=>mesh!=null&&!IsNonVisualMesh(model,mesh)&&mesh.lod>=0).Select(mesh=>(int)mesh.lod).Distinct().OrderBy(level=>level).ToArray();
      visualLodLevels[model]=levels;return levels;
    }

    private static bool ModelHasOcclusionGeometry(GR2 model){
      if(model==null)return false;
      if(model.meshes!=null&&model.meshes.Any(mesh=>mesh!=null&&mesh.lod==-3&&mesh.meshVerts!=null&&mesh.meshVertIndex!=null))return true;
      if(model.attachedModels!=null)foreach(GR2 attached in model.attachedModels)if(ModelHasOcclusionGeometry(attached))return true;
      return false;
    }

    private static bool IsCollisionMesh(GR2 model,GR2_Mesh mesh){
      if(mesh==null)return false;string name=(mesh.meshName??string.Empty).ToLowerInvariant();string path=NormalizeAssetPath(model?.filename);
      return mesh.lod==-1||name=="collision"||path.Contains("designblockout/cover_objects/")||path.Contains("superexclusion")||name.Contains("superexclusion");
    }

    private static bool IsNonVisualMesh(GR2 model,GR2_Mesh mesh){
      if(mesh==null)return true;if(IsCollisionMesh(model,mesh))return true;string name=(mesh.meshName??string.Empty).ToLowerInvariant();string path=NormalizeAssetPath(model?.filename);
      // Live GR2s tag these as negative LODs; Granny/Beta assets often only carry the authored helper name. Do not
      // submit either representation to the normal opaque pass or portal/occluder quads appear as solid black doors.
      bool portalName=name.Contains("portal");
      bool occluderName=name=="occluder"||name.StartsWith("occluder_",StringComparison.Ordinal)||name.Contains("occlusion");
      if(mesh.lod==-3||occluderName)return true;return mesh.lod==-2||portalName||path.Contains("fadeportal")||path.Contains("fade_portal");
    }

    private bool TryModelLodProjection(GR2 model,Matrix world,float lodFactor,out float projectedSize){
      projectedSize=0f;GR2_Bounding_Box box=model?.globalBox;if(box==null)return false;
      Vector3 min=new Vector3(box.minX,box.minY,box.minZ),max=new Vector3(box.maxX,box.maxY,box.maxZ);if(!IsFinite(min)||!IsFinite(max)||min.X>max.X||min.Y>max.Y||min.Z>max.Z)return false;
      Vector3 localCenter=(min+max)*.5f,center=Vector3.TransformCoordinate(localCenter,world);float radius=(max-min).Length()*.5f;if(radius<=.0001f||float.IsNaN(radius)||float.IsInfinity(radius))return false;
      float factor=Math.Max(0f,float.IsNaN(lodFactor)||float.IsInfinity(lodFactor)?1f:lodFactor);
      float distance=orthographicActive?Math.Max(OrthographicReferenceDistance*orthographicZoom,.0001f):Math.Max((camera.Position-center).Length(),.0001f);
      projectedSize=radius*factor*GrannyLodProjectedSizeScale/distance;return true;
    }

    private int SelectModelLodLevel(GR2 model,Matrix world,bool mapOrSky,float lodFactor=1f){
      int[] levels=GetVisualLodLevels(model);if(levels.Length==0)return 0;if(levels.Length==1||mapOrSky)return levels[0];
      if(!TryModelLodProjection(model,world,lodFactor,out float projected))return levels[0];float[] thresholds=model.lodThresholds??Array.Empty<float>();int max=levels.Length-1;
      for(int index=0;index<=max;index++){
        int thresholdIndex=Math.Max(0,thresholds.Length-1-index);float threshold=thresholds.Length>0?thresholds[Math.Min(thresholdIndex,thresholds.Length-1)]:GrannyLegacyLodThreshold;
        if(projected>=threshold)return levels[index];
      }
      return levels[max];
    }

    private bool ShouldCullModelByLod(GR2 model,Matrix world,bool mapOrSky,float lodFactor=1f){
      if(mapOrSky)return false;int[] levels=GetVisualLodLevels(model);if(levels.Length<=1)return false;if(!TryModelLodProjection(model,world,lodFactor,out float projected))return false;
      float[] thresholds=model.lodThresholds??Array.Empty<float>();float cullThreshold=thresholds.Length>0?thresholds[0]:GrannyLegacyLodThreshold;return cullThreshold>0f&&projected<=cullThreshold;
    }

    private static bool MeshVisibleForLod(GR2 model,GR2_Mesh mesh,int selectedLod){
      if(mesh==null||IsNonVisualMesh(model,mesh))return false;return mesh.lod<0?false:mesh.lod==selectedLod;
    }

    private static bool MatrixHasNearlyUniformScale(Matrix m){
      // The instanced shader transforms normals with the instance matrix. Keep non-uniformly scaled placements on
      // the ordinary path so their lighting stays correct without an inverse-transpose matrix per instance.
      float sx=(float)Math.Sqrt(m.M11*m.M11+m.M12*m.M12+m.M13*m.M13);
      float sy=(float)Math.Sqrt(m.M21*m.M21+m.M22*m.M22+m.M23*m.M23);
      float sz=(float)Math.Sqrt(m.M31*m.M31+m.M32*m.M32+m.M33*m.M33);
      float min=Math.Min(sx,Math.Min(sy,sz)),max=Math.Max(sx,Math.Max(sy,sz));
      return min>.00001f&&max/min<=1.03f;
    }

    private bool ModelCanUseRegularInstancing(GR2 model){
      if(model==null)return false;if(regularModelInstancingSafe.TryGetValue(model,out bool cached))return cached;
      bool safe=ModelCanUseRegularInstancing(model,new HashSet<GR2>());regularModelInstancingSafe[model]=safe;return safe;
    }
    private bool ModelCanUseRegularInstancing(GR2 model,HashSet<GR2> seen){
      if(model==null||!seen.Add(model))return true;
      foreach(var mesh in model.meshes){if(IsNonVisualMesh(model,mesh))continue;foreach(var piece in mesh.meshPieces){
        GR2_Material mat=ResolvePieceMaterial(model,piece);
        string alpha=mat?.alphaMode??"None";if(!string.Equals(alpha,"None",StringComparison.OrdinalIgnoreCase)&&!string.Equals(alpha,"Test",StringComparison.OrdinalIgnoreCase))return false;
        string derived=mat?.derived??String.Empty;if(string.Equals(derived,"Skydome",StringComparison.OrdinalIgnoreCase)||string.Equals(derived,"UberEnvBlend",StringComparison.OrdinalIgnoreCase)||string.Equals(derived,"OpacityFade",StringComparison.OrdinalIgnoreCase)||string.Equals(derived,"Distortion",StringComparison.OrdinalIgnoreCase)||string.Equals(derived,"_FinalBlend",StringComparison.OrdinalIgnoreCase)||string.Equals(derived,"Water",StringComparison.OrdinalIgnoreCase))return false;
      }}
      foreach(var attached in model.attachedModels)if(!ModelCanUseRegularInstancing(attached,seen))return false;
      return true;
    }

    private Buffer UploadRegularModelInstances(IList<Matrix> worlds){
      int count=worlds?.Count??0;if(count<=0)return null;
      if(regularModelInstanceBuffer==null||regularModelInstanceCapacity<count){
        Release(ref regularModelInstanceBuffer);int capacity=64;while(capacity<count&&capacity<65536)capacity*=2;if(capacity<count)capacity=count;
        regularModelInstanceCapacity=capacity;regularModelInstanceScratch=new float[checked(capacity*16)];var bd=new BufferDescription(checked(capacity*64),ResourceUsage.Dynamic,BindFlags.VertexBuffer,CpuAccessFlags.Write,ResourceOptionFlags.None,0);regularModelInstanceBuffer=new Buffer(Device,bd);
      }
      if(regularModelInstanceScratch==null||regularModelInstanceScratch.Length<regularModelInstanceCapacity*16)regularModelInstanceScratch=new float[checked(regularModelInstanceCapacity*16)];
      float[] data=regularModelInstanceScratch;int o=0;foreach(Matrix m in worlds){
        data[o++]=m.M11;data[o++]=m.M12;data[o++]=m.M13;data[o++]=m.M14;data[o++]=m.M21;data[o++]=m.M22;data[o++]=m.M23;data[o++]=m.M24;
        data[o++]=m.M31;data[o++]=m.M32;data[o++]=m.M33;data[o++]=m.M34;data[o++]=m.M41;data[o++]=m.M42;data[o++]=m.M43;data[o++]=m.M44;
      }
      try{DataBox mapped=ImmediateContext.MapSubresource(regularModelInstanceBuffer,MapMode.WriteDiscard,SlimDX.Direct3D11.MapFlags.None);mapped.Data.WriteRange(data,0,count*16);ImmediateContext.UnmapSubresource(regularModelInstanceBuffer,0);return regularModelInstanceBuffer;}
      catch(Exception ex){System.Diagnostics.Debug.WriteLine("Regular world instancing upload failed: "+ex.Message);return null;}
    }
    private void DrawModel(GR2 model,Matrix world,Matrix vp,WorldRenderSettings s,bool sky,AreaEnvironmentMaterial envMat,int forcedLod=-1,bool blueGlow=false,bool allowLocalLightOverflow=true){
      if(model==null||!EnsureModelGeometryPrepared(model))return;int selectedLod=forcedLod>=0?forcedLod:SelectModelLodLevel(model,world,sky||s.Mode==WorldRenderMode.Map);fx.SetWorld(world);fx.SetViewProj(vp);fx.SetPlaceableBlueGlow(blueGlow&&!sky&&s.Mode!=WorldRenderMode.Map);
      foreach(var mesh in model.meshes){if(mesh.vertBuffer==null||mesh.idxBuffer==null||!MeshVisibleForLod(model,mesh,selectedLod))continue;ImmediateContext.InputAssembler.SetVertexBuffers(0,new VertexBufferBinding(mesh.vertBuffer,PosNormalTexTan.Stride,0));ImmediateContext.InputAssembler.SetIndexBuffer(mesh.idxBuffer,Format.R16_UInt,0);
        foreach(var piece in mesh.meshPieces){GR2_Material mat=ResolvePieceMaterial(model,piece);if(IsMaterialHiddenFromWorld(mat,s))continue;fx.SetMaterial(mat);
          if(!sky&&mat!=null&&string.Equals(mat.derived,"UberEnvBlend",StringComparison.OrdinalIgnoreCase)&&envMat!=null)fx.SetEnvironmentBlend(envMat,mat,LoadTexture(envMat.BlendDiffuse),LoadTexture(envMat.BlendNormal));
          var tech=PickModelTech(s,mat,sky);tech.GetPassByIndex(0).Apply(ImmediateContext);ImmediateContext.DrawIndexed((int)piece.numPieceFaces*3,(int)piece.startIndex*3,0);}}
      foreach(var a in model.attachedModels)DrawModel(a,world,vp,s,sky,envMat,-1,blueGlow,false);
      if(allowLocalLightOverflow&&!sky&&HasLocalLightOverflow)DrawModelLocalLightOverflow(model,world,vp,s,envMat,forcedLod);
    }

    private static bool MaterialReceivesAdditiveLocalLight(GR2_Material mat){
      string alpha=mat?.alphaMode??"None";return string.Equals(alpha,"None",StringComparison.OrdinalIgnoreCase)||string.Equals(alpha,"Test",StringComparison.OrdinalIgnoreCase);
    }

    private void DrawModelLocalLightOverflow(GR2 model,Matrix world,Matrix vp,WorldRenderSettings s,AreaEnvironmentMaterial envMat,int forcedLod=-1){
      LocalLightSelection selection=currentLocalLightSelection;if(model==null||selection==null||selection.Count<=LocalLightBaseSlots)return;
      fx.SetPlaceableBlueGlow(false);
      for(int offset=LocalLightBaseSlots;offset<selection.Count;offset+=LocalLightBaseSlots){
        if(BindLocalLightChunk(selection,offset)<=0)break;DrawModelLocalLightPassRecursive(model,world,vp,s,envMat,forcedLod);
      }
      InvalidateTrackedLocalLightBinding();BindLocalLightSelection(selection);
    }

    private void DrawModelLocalLightPassRecursive(GR2 model,Matrix world,Matrix vp,WorldRenderSettings s,AreaEnvironmentMaterial envMat,int forcedLod=-1){
      if(model==null||!EnsureModelGeometryPrepared(model))return;int selectedLod=forcedLod>=0?forcedLod:SelectModelLodLevel(model,world,false);fx.SetWorld(world);fx.SetViewProj(vp);
      foreach(var mesh in model.meshes){if(mesh.vertBuffer==null||mesh.idxBuffer==null||!MeshVisibleForLod(model,mesh,selectedLod))continue;ImmediateContext.InputAssembler.SetVertexBuffers(0,new VertexBufferBinding(mesh.vertBuffer,PosNormalTexTan.Stride,0));ImmediateContext.InputAssembler.SetIndexBuffer(mesh.idxBuffer,Format.R16_UInt,0);
        foreach(var piece in mesh.meshPieces){GR2_Material mat=ResolvePieceMaterial(model,piece);if(IsMaterialHiddenFromWorld(mat,s)||!MaterialReceivesAdditiveLocalLight(mat))continue;fx.SetMaterial(mat);
          if(mat!=null&&string.Equals(mat.derived,"UberEnvBlend",StringComparison.OrdinalIgnoreCase)&&envMat!=null)fx.SetEnvironmentBlend(envMat,mat,LoadTexture(envMat.BlendDiffuse),LoadTexture(envMat.BlendNormal));
          fx.LocalLightAdd.GetPassByIndex(0).Apply(ImmediateContext);ImmediateContext.DrawIndexed((int)piece.numPieceFaces*3,(int)piece.startIndex*3,0);}}
      foreach(var a in model.attachedModels)DrawModelLocalLightPassRecursive(a,world,vp,s,envMat,-1);
    }
    private static bool IsMaterialHiddenFromWorld(GR2_Material mat,WorldRenderSettings s=null){
      if(mat==null)return false;
      string visibility=mat.visibility??String.Empty;
      bool showHidden=s?.ShowHiddenGeometry==true;
      // With the top-level Show tier removed, hidden/editor geometry is controlled only by its Utilities submenu
      // checkbox. This avoids the previous double-enable requirement.
      if(visibility.Equals("EditorOnly",StringComparison.OrdinalIgnoreCase))return !showHidden;
      if(visibility.Equals("Hidden",StringComparison.OrdinalIgnoreCase))return !showHidden;
      // Streamed MAT metadata is intentionally read in the background. Before that XML has arrived, helper hulls
      // otherwise render with the textureless fallback for a few frames (or forever if a beta MAT is missing),
      // producing the solid black rectangles seen in some doorways. Jedipedia never submits these collision/occluder
      // utility materials to the normal world pass. Use the same conservative authored-name fallback until metadata
      // can confirm Visibility=Hidden; the explicit hidden-geometry diagnostic can still reveal them.
      if(!showHidden&&String.IsNullOrWhiteSpace(visibility)&&LooksLikeHiddenUtilityMaterial(mat))return true;
      return false;
    }

    private static bool LooksLikeHiddenUtilityMaterial(GR2_Material mat){
      string name=(mat?.sourceMaterialName??mat?.materialName??String.Empty).Replace('\\','/').ToLowerInvariant();
      int slash=name.LastIndexOf('/');if(slash>=0)name=name.Substring(slash+1);
      if(name.EndsWith(".mat",StringComparison.OrdinalIgnoreCase))name=name.Substring(0,name.Length-4);
      return name.Contains("utility_hidden")||name.StartsWith("util_collision",StringComparison.Ordinal)||
        name=="collision"||name.StartsWith("collision_",StringComparison.Ordinal)||
        name=="occluder"||name.StartsWith("occluder_",StringComparison.Ordinal)||
        name=="portal"||name.StartsWith("portal_",StringComparison.Ordinal)||
        name.Contains("fadeportal")||name.Contains("fade_portal");
    }

    private EffectTechnique PickModelTech(WorldRenderSettings s,GR2_Material mat,bool sky){
      if(sky)return fx.Sky;if(s.Mode==WorldRenderMode.Wireframe)return fx.Wire;if(s.Mode==WorldRenderMode.Unlit||!s.EnableLighting)return fx.Unlit;
      string alpha=mat?.alphaMode??"None";if(string.Equals(alpha,"Test",StringComparison.OrdinalIgnoreCase))return fx.AlphaTestLit;
      if(string.Equals(alpha,"Add",StringComparison.OrdinalIgnoreCase))return fx.AddLit;
      if(string.Equals(alpha,"Multiply",StringComparison.OrdinalIgnoreCase))return fx.MultiplyLit;
      return !string.Equals(alpha,"None",StringComparison.OrdinalIgnoreCase)?fx.AlphaLit:fx.Lit;
    }

    private EffectTechnique PickSkinnedModelTech(WorldRenderSettings s,GR2_Material mat){
      if(s.Mode==WorldRenderMode.Wireframe)return fx.SkinnedWire;if(s.Mode==WorldRenderMode.Unlit||!s.EnableLighting)return fx.SkinnedUnlit;
      string alpha=mat?.alphaMode??"None";if(string.Equals(alpha,"Test",StringComparison.OrdinalIgnoreCase))return fx.SkinnedAlphaTestLit;
      if(string.Equals(alpha,"Add",StringComparison.OrdinalIgnoreCase))return fx.SkinnedAddLit;
      if(string.Equals(alpha,"Multiply",StringComparison.OrdinalIgnoreCase))return fx.SkinnedMultiplyLit;
      return !string.Equals(alpha,"None",StringComparison.OrdinalIgnoreCase)?fx.SkinnedAlphaLit:fx.SkinnedLit;
    }

    private EffectTechnique PickInstancedModelTech(WorldRenderSettings s,GR2_Material mat){
      if(s.Mode==WorldRenderMode.Wireframe)return fx.InstancedWire;if(s.Mode==WorldRenderMode.Unlit||!s.EnableLighting)return fx.InstancedUnlit;
      string alpha=mat?.alphaMode??"None";if(string.Equals(alpha,"Test",StringComparison.OrdinalIgnoreCase))return fx.InstancedAlphaTestLit;
      return !string.Equals(alpha,"None",StringComparison.OrdinalIgnoreCase)?fx.InstancedAlphaLit:fx.InstancedLit;
    }

    private void DrawInstancedModel(GR2 model,Buffer instanceBuffer,int instanceCount,Matrix parentWorld,Matrix vp,WorldRenderSettings s,int forcedLod=-1,Matrix? lodReferenceWorld=null,bool allowLocalLightOverflow=true,int startInstance=0){
      if(model==null||instanceBuffer==null||instanceCount<=0||!EnsureModelGeometryPrepared(model))return;Matrix reference=lodReferenceWorld??parentWorld;int selectedLod=forcedLod>=0?forcedLod:SelectModelLodLevel(model,reference,s.Mode==WorldRenderMode.Map);fx.SetWorld(parentWorld);fx.SetViewProj(vp);
      foreach(var mesh in model.meshes){
        if(mesh.vertBuffer==null||mesh.idxBuffer==null||!MeshVisibleForLod(model,mesh,selectedLod))continue;
        ImmediateContext.InputAssembler.SetVertexBuffers(0,new[]{new VertexBufferBinding(mesh.vertBuffer,PosNormalTexTan.Stride,0),new VertexBufferBinding(instanceBuffer,64,0)});ImmediateContext.InputAssembler.SetIndexBuffer(mesh.idxBuffer,Format.R16_UInt,0);
        foreach(var piece in mesh.meshPieces){GR2_Material mat=ResolvePieceMaterial(model,piece);if(IsMaterialHiddenFromWorld(mat,s))continue;fx.SetMaterial(mat);
          PickInstancedModelTech(s,mat).GetPassByIndex(0).Apply(ImmediateContext);ImmediateContext.DrawIndexedInstanced((int)piece.numPieceFaces*3,instanceCount,(int)piece.startIndex*3,0,startInstance);}
      }
      foreach(var a in model.attachedModels)DrawInstancedModel(a,instanceBuffer,instanceCount,parentWorld,vp,s,-1,reference,false,startInstance);
      if(allowLocalLightOverflow&&HasLocalLightOverflow)DrawInstancedModelLocalLightOverflow(model,instanceBuffer,instanceCount,parentWorld,vp,s,forcedLod,reference,startInstance);
    }

    private void DrawInstancedModelLocalLightOverflow(GR2 model,Buffer instanceBuffer,int instanceCount,Matrix parentWorld,Matrix vp,WorldRenderSettings s,int forcedLod,Matrix reference,int startInstance){
      LocalLightSelection selection=currentLocalLightSelection;if(model==null||selection==null||selection.Count<=LocalLightBaseSlots)return;
      for(int offset=LocalLightBaseSlots;offset<selection.Count;offset+=LocalLightBaseSlots){if(BindLocalLightChunk(selection,offset)<=0)break;DrawInstancedModelLocalLightPassRecursive(model,instanceBuffer,instanceCount,parentWorld,vp,s,forcedLod,reference,startInstance);}
      InvalidateTrackedLocalLightBinding();BindLocalLightSelection(selection);
    }

    private void DrawInstancedModelLocalLightPassRecursive(GR2 model,Buffer instanceBuffer,int instanceCount,Matrix parentWorld,Matrix vp,WorldRenderSettings s,int forcedLod,Matrix reference,int startInstance){
      if(model==null||!EnsureModelGeometryPrepared(model))return;int selectedLod=forcedLod>=0?forcedLod:SelectModelLodLevel(model,reference,false);fx.SetWorld(parentWorld);fx.SetViewProj(vp);
      foreach(var mesh in model.meshes){if(mesh.vertBuffer==null||mesh.idxBuffer==null||!MeshVisibleForLod(model,mesh,selectedLod))continue;
        ImmediateContext.InputAssembler.SetVertexBuffers(0,new[]{new VertexBufferBinding(mesh.vertBuffer,PosNormalTexTan.Stride,0),new VertexBufferBinding(instanceBuffer,64,0)});ImmediateContext.InputAssembler.SetIndexBuffer(mesh.idxBuffer,Format.R16_UInt,0);
        foreach(var piece in mesh.meshPieces){GR2_Material mat=ResolvePieceMaterial(model,piece);if(IsMaterialHiddenFromWorld(mat,s)||!MaterialReceivesAdditiveLocalLight(mat))continue;fx.SetMaterial(mat);fx.InstancedLocalLightAdd.GetPassByIndex(0).Apply(ImmediateContext);ImmediateContext.DrawIndexedInstanced((int)piece.numPieceFaces*3,instanceCount,(int)piece.startIndex*3,0,startInstance);}}
      foreach(var a in model.attachedModels)DrawInstancedModelLocalLightPassRecursive(a,instanceBuffer,instanceCount,parentWorld,vp,s,-1,reference,startInstance);
    }

    private void DrawInstancedModelShadow(GR2 model,Buffer instanceBuffer,int instanceCount,Matrix parentWorld,int forcedLod=-1,Matrix? lodReferenceWorld=null,int startInstance=0){
      if(model==null||instanceBuffer==null||instanceCount<=0||!EnsureModelGeometryPrepared(model))return;Matrix reference=lodReferenceWorld??parentWorld;int selectedLod=forcedLod>=0?forcedLod:SelectModelLodLevel(model,reference,false);fx.SetWorld(parentWorld);
      foreach(var mesh in model.meshes){
        if(mesh.vertBuffer==null||mesh.idxBuffer==null||!MeshVisibleForLod(model,mesh,selectedLod))continue;
        ImmediateContext.InputAssembler.SetVertexBuffers(0,new[]{new VertexBufferBinding(mesh.vertBuffer,PosNormalTexTan.Stride,0),new VertexBufferBinding(instanceBuffer,64,0)});ImmediateContext.InputAssembler.SetIndexBuffer(mesh.idxBuffer,Format.R16_UInt,0);
        foreach(var piece in mesh.meshPieces){GR2_Material mat=ResolvePieceMaterial(model,piece);if(IsMaterialHiddenFromWorld(mat))continue;fx.SetMaterial(mat);fx.InstancedShadow.GetPassByIndex(0).Apply(ImmediateContext);ImmediateContext.DrawIndexedInstanced((int)piece.numPieceFaces*3,instanceCount,(int)piece.startIndex*3,0,startInstance);}
      }
      foreach(var a in model.attachedModels)DrawInstancedModelShadow(a,instanceBuffer,instanceCount,parentWorld,-1,reference,startInstance);
    }

    private static string NormalizeAssetPath(string p)=>(p??string.Empty).Trim().Replace('\\','/').TrimStart('/').ToLowerInvariant();
    private Matrix InstanceWorldForSky(AssetInstance inst,Room room){
      // Jedipedia deliberately zeroes rotation on /engine/placeablecamera.cam while room matrices are built.
      // PugTools' normal instance cache contains that authored camera rotation, so merely stripping rotation
      // from the inverse is not enough when sky geometry is parented to the camera: the parent rotation remains
      // baked into the child's world matrix and can rotate the entire backdrop out of view. Rebuild this one
      // parent chain with the same camera exception as Jedipedia.
      if(inst==null)return Matrix.Identity;
      Matrix output=SkyInstanceLocalTransform(inst);ulong parent=inst.parentInstance;var seen=new HashSet<ulong>();
      while(parent!=0&&seen.Add(parent)){
        if(room==null||!room.InstancesById.TryGetValue(parent,out AssetInstance p))break;
        output*=SkyInstanceLocalTransform(p);parent=p.parentInstance;
      }
      return output;
    }
    private Matrix SkyInstanceLocalTransform(AssetInstance inst){
      if(inst!=null&&placeableCameraAssetId!=0&&inst.assetID==placeableCameraAssetId)
        return Matrix.Scaling(inst.scale)*Matrix.Translation(inst.position);
      return inst?.transformMatrix??Matrix.Identity;
    }
    private bool TryGetSkyCameraInverse(Room room,out Matrix inverse){
      inverse=Matrix.Identity;if(room==null||placeableCameraAssetId==0||!room.InstancesByAssetId.TryGetValue(placeableCameraAssetId,out List<AssetInstance> list)||list.Count==0)return false;
      AssetInstance cam=list[0];
      // Jedipedia deliberately ignores rotation authored on /engine/placeablecamera.cam. Keep its scale,
      // translation and parent chain, then transform skyscene contents into that camera's local space.
      Matrix skyCamera=Matrix.Scaling(cam.scale)*Matrix.Translation(cam.position);ulong parent=cam.parentInstance;var seen=new HashSet<ulong>();
      while(parent!=0&&seen.Add(parent)){if(!room.InstancesById.TryGetValue(parent,out AssetInstance p))break;skyCamera*=p.transformMatrix;parent=p.parentInstance;}
      try{inverse=Matrix.Invert(skyCamera);return true;}catch{return false;}
    }
    private Matrix GetSkyViewProjection(){
      Matrix rotationOnly=camera.View;rotationOnly.M41=rotationOnly.M42=rotationOnly.M43=0f;
      // The backdrop has its own depth pass which is cleared before world geometry. Give it a generous independent
      // far plane so a large authored skydome cannot disappear just because the user selected a short View distance.
      // This does not increase world draw distance or hurt the world's depth precision.
      Matrix skyProjection;
      if(orthographicActive&&camera.NearWindowHeight>.0001f)skyProjection=Matrix.OrthoRH(Math.Max(.001f,camera.NearWindowWidth),Math.Max(.001f,camera.NearWindowHeight),.01f,SkyFarDistance);
      else skyProjection=Matrix.PerspectiveFovRH(camera.FovY>0?camera.FovY:.25f*SlimDXNet.MathF.PI,Math.Max(.01f,camera.Aspect),.01f,SkyFarDistance);
      return rotationOnly*skyProjection;
    }

    private void ApplyRoomEnvironment(Room room,AreaEnvironmentScheme cameraEnv,WorldRenderSettings s,bool sceneShadows){
      AreaEnvironmentScheme roomEnv=room?.EnvironmentScheme??area?.GetEnvironmentScheme("area")??new AreaEnvironmentScheme();
      // CSM matrices are generated from the active camera room's sun. Reuse them only for that same
      // scheme; indoor/override rooms can have a different directional light and must not sample a
      // shadow map projected from another sun direction.
      bool roomShadows=sceneShadows&&(ReferenceEquals(roomEnv,cameraEnv)||string.Equals(roomEnv.Name,cameraEnv?.Name,StringComparison.OrdinalIgnoreCase));
      fx.SetEnvironment(roomEnv,s.EnableLighting,s.EnableFog,roomShadows,s.ViewDistanceScale);
      fx.SetIllumination(LoadTexture(roomEnv.IlluminationMap));
      fx.SetScrolling(roomEnv,elapsed,LoadTexture(roomEnv.ScrollingTexture),LoadTexture(roomEnv.ScrollingMask));
    }

    private void DrawWater(Matrix vp,HashSet<string> visible,WorldRenderSettings s){
      if(s.Mode==WorldRenderMode.Heightmap)return;
      if(s.Mode==WorldRenderMode.Map){
        foreach(Room room in rooms){
          if(skyRoomNames.Contains(room.RoomName)||!RoomVisible(room,visible))continue;
          SetWaterRoomEnvironment(room,s);
          foreach(AssetInstance inst in room.InstancesById.Values){
            if(!InstanceVisibleOnMap(inst)||!inst.hasWater||inst.VBO==null)continue;DrawWaterInstance(inst,room,InstanceWorld(inst,room),vp,visible,s);
          }
        }
      } else {
        Room activeRoom=null;
        foreach(RenderEntry entry in NearbyRenderEntries(RenderKindWater,camera.FarZ,true,visible)){
          Room room=entry.Room;AssetInstance inst=entry.Instance;
          if(room==null||!InstanceVisibleInWorld(inst,s)||skyRoomNames.Contains(room.RoomName)||!RoomVisible(room,visible)||inst.VBO==null)continue;
          if(!ReferenceEquals(activeRoom,room)){SetWaterRoomEnvironment(room,s);activeRoom=room;}
          DrawWaterInstance(inst,room,entry.World,vp,visible,s);
        }
      }
      fx.ClearWater();
    }
    private void SetWaterRoomEnvironment(Room room,WorldRenderSettings s){
      AreaEnvironmentScheme waterEnv=room.EnvironmentScheme??area?.GetEnvironmentScheme("area")??new AreaEnvironmentScheme();
      fx.SetEnvironment(waterEnv,s.EnableLighting,s.EnableFog,false,s.ViewDistanceScale);fx.SetIllumination(LoadTexture(waterEnv.IlluminationMap));fx.SetScrolling(waterEnv,elapsed,LoadTexture(waterEnv.ScrollingTexture),LoadTexture(waterEnv.ScrollingMask));
    }
    private void DrawWaterInstance(AssetInstance inst,Room room,Matrix waterWorld,Matrix vp,HashSet<string> visible,WorldRenderSettings s){
      waterGpu.TryGetValue(inst,out WaterGpu gpu);EnsureWaterTextures(inst,gpu);AreaEnvironmentScheme waterEnv=room.EnvironmentScheme??area?.GetEnvironmentScheme("area")??new AreaEnvironmentScheme();ShaderResourceView environmentMap=LoadTexture(waterEnv.EnvironmentMap);
      fx.SetWorld(waterWorld);fx.SetViewProj(vp);fx.SetMaterial(null);SetNearestLocalLights(new Vector3(waterWorld.M41,waterWorld.M42,waterWorld.M43),room,s,visible,false,true);
      fx.SetWater(inst,elapsed,gpu?.Normal1??defaultWaterNormal,gpu?.Normal2??gpu?.Normal1??defaultWaterNormal,gpu?.DepthMap??defaultWaterDepth,gpu?.SurfaceMap,environmentMap);
      ImmediateContext.InputAssembler.SetVertexBuffers(0,new VertexBufferBinding(inst.VBO,PosNormalTexTan.Stride,0));ImmediateContext.InputAssembler.SetIndexBuffer(inst.IBO,Format.R16_UInt,0);
      EffectTechnique tech=s.Mode==WorldRenderMode.Wireframe?fx.Wire:fx.Water;tech.GetPassByIndex(0).Apply(ImmediateContext);ImmediateContext.DrawIndexed(inst.numFaces,0,0);
    }

    private void DrawMapArt(Matrix vp){
      if(!mapArtPrepared)BuildMapArt();
      if(mapArtGpu.Count==0)return;
      ImmediateContext.InputAssembler.PrimitiveTopology=PrimitiveTopology.TriangleList;fx.SetWorld(Matrix.Identity);fx.SetViewProj(vp);
      foreach(var g in mapArtGpu){if(g.Texture==null||g.Buffer==null)continue;fx.SetMapArt(g.Texture,.82f);ImmediateContext.InputAssembler.SetVertexBuffers(0,new VertexBufferBinding(g.Buffer,PosNormalTexTan.Stride,0));fx.MapArt.GetPassByIndex(0).Apply(ImmediateContext);ImmediateContext.Draw(g.Count,0);}
      fx.ClearMapArt();
    }

    private void DrawMapPlayerMarker(Matrix vp) {
      if(!mapOpen||ClientWidth<=0||ClientHeight<=0)return;
      const float pivotX=11f,pivotY=6f,width=22f,height=27f;
      MapNoteIconGpu gpu=EnsureMapNoteIconGpu("player-marker",6);
      if(gpu?.Buffer==null||gpu.Texture==null)return;

      Vector3 position=camera.Position,look=-camera.Look;
      Vector2 forward=new Vector2(look.X,look.Z);
      if(forward.LengthSquared()<.000001f)forward=new Vector2(0,-1);else forward.Normalize();
      Vector2 side=new Vector2(-forward.Y,forward.X);
      float worldPerPixelX=Math.Max(.00001f,mapVisibleWidth/Math.Max(1f,ClientWidth));
      float worldPerPixelZ=Math.Max(.00001f,mapVisibleHeight/Math.Max(1f,ClientHeight));
      float y=mapCameraPosition.Y-1f;
      Vector2 p=new Vector2(position.X,position.Z);
      Vector2 Corner(float imageX,float imageY){
        float dx=(imageX-pivotX)*worldPerPixelX,dy=(imageY-pivotY)*worldPerPixelZ;
        return p+side*dx+forward*dy;
      }
      Vector2 tl=Corner(0,0),tr=Corner(width,0),br=Corner(width,height),bl=Corner(0,height);
      var normal=new Vector3(0,1,0);var tangent=new Vector3(1,0,0);
      var verts=new[]{
        new PosNormalTexTan(new Vector3(tl.X,y,tl.Y),normal,new Vector2(0,0),tangent),
        new PosNormalTexTan(new Vector3(tr.X,y,tr.Y),normal,new Vector2(1,0),tangent),
        new PosNormalTexTan(new Vector3(br.X,y,br.Y),normal,new Vector2(1,1),tangent),
        new PosNormalTexTan(new Vector3(tl.X,y,tl.Y),normal,new Vector2(0,0),tangent),
        new PosNormalTexTan(new Vector3(br.X,y,br.Y),normal,new Vector2(1,1),tangent),
        new PosNormalTexTan(new Vector3(bl.X,y,bl.Y),normal,new Vector2(0,1),tangent)
      };
      try{
        DataBox mapped=ImmediateContext.MapSubresource(gpu.Buffer,MapMode.WriteDiscard,SlimDX.Direct3D11.MapFlags.None);
        mapped.Data.WriteRange(verts);ImmediateContext.UnmapSubresource(gpu.Buffer,0);
        ImmediateContext.InputAssembler.PrimitiveTopology=PrimitiveTopology.TriangleList;
        ImmediateContext.InputAssembler.SetVertexBuffers(0,new VertexBufferBinding(gpu.Buffer,PosNormalTexTan.Stride,0));
        fx.SetWorld(Matrix.Identity);fx.SetViewProj(vp);fx.SetPlaceableBlueGlow(false);fx.SetMapArt(gpu.Texture,1f);
        fx.MapArt.GetPassByIndex(0).Apply(ImmediateContext);ImmediateContext.Draw(6,0);fx.ClearMapArt();
      }catch(Exception ex){System.Diagnostics.Debug.WriteLine("Map player marker draw failed: "+ex.Message);}
    }

    private void DrawLines(List<LineGpu> list,Matrix vp,float range){if(list.Count==0)return;ImmediateContext.InputAssembler.PrimitiveTopology=PrimitiveTopology.LineList;fx.SetWorld(Matrix.Identity);fx.SetViewProj(vp);fx.SetMaterial(null);foreach(var g in list){if(range<float.MaxValue&&!SphereWithinDistance(g.Center,g.Radius,range))continue;fx.SetOverlay(g.Color);ImmediateContext.InputAssembler.SetVertexBuffers(0,new VertexBufferBinding(g.Buffer,PosNormalTexTan.Stride,0));fx.Overlay.GetPassByIndex(0).Apply(ImmediateContext);ImmediateContext.Draw(g.Count,0);}ImmediateContext.InputAssembler.PrimitiveTopology=PrimitiveTopology.TriangleList;}
    private void DrawOverlayTriangles(List<LineGpu> list,Matrix vp,float range){if(list.Count==0)return;ImmediateContext.InputAssembler.PrimitiveTopology=PrimitiveTopology.TriangleList;fx.SetWorld(Matrix.Identity);fx.SetViewProj(vp);fx.SetMaterial(null);foreach(var g in list){if(range<float.MaxValue&&!SphereWithinDistance(g.Center,g.Radius,range))continue;fx.SetOverlay(g.Color);ImmediateContext.InputAssembler.SetVertexBuffers(0,new VertexBufferBinding(g.Buffer,PosNormalTexTan.Stride,0));fx.Overlay.GetPassByIndex(0).Apply(ImmediateContext);ImmediateContext.Draw(g.Count,0);}}

    private void BuildLocalLightIndex(){
      localLightGrid.Clear();localLightGlobal.Clear();localLightSelectionCache.Clear();localLightVisibilityScope=null;
      foreach(Room room in rooms){
        foreach(AssetInstance inst in room.InstancesById.Values){
          bool litAsset=area!=null&&area.AssetIdMap.TryGetValue(inst.assetID,out AreaAsset lightAsset)&&
            string.Equals((lightAsset.Extension??String.Empty).Trim().TrimStart('.'),"lit",StringComparison.OrdinalIgnoreCase);
          // Jedipedia also recognizes the .lit asset itself, even when a particular placement omitted LightType.
          if(!InstanceVisibleInWorld(inst)||(!inst.IsLocalLight&&!litAsset))continue;
          Matrix m=InstanceWorld(inst,room);Vector3 pos=new Vector3(m.M41,m.M42,m.M43);Vector3 d=Vector3.TransformNormal(new Vector3(0,0,1),m);if(d.LengthSquared()<.0001f)d=new Vector3(0,-1,0);else d.Normalize();
          bool directional=string.Equals(inst.LocalLightType,"DIRECTIONAL",StringComparison.OrdinalIgnoreCase);float range=Math.Max(.0001f,inst.LocalLightRange);
          // The authored Range scales the placement's three basis axes. Using Range alone here caused scaled .lit
          // volumes to be rejected long before their projector reached a receiver.
          float sx=(float)Math.Sqrt(m.M11*m.M11+m.M12*m.M12+m.M13*m.M13)*range;
          float sy=(float)Math.Sqrt(m.M21*m.M21+m.M22*m.M22+m.M23*m.M23)*range;
          float sz=(float)Math.Sqrt(m.M31*m.M31+m.M32*m.M32+m.M33*m.M33)*range;
          float broadRange=Math.Max(.0001f,Math.Max(sx,Math.Max(sy,sz)));
          float type=directional?1f:(string.Equals(inst.LocalLightType,"SPOT",StringComparison.OrdinalIgnoreCase)?2f:0f);
          var entry=new LocalLightEntry{
            Room=room,Instance=inst,Position=pos,RangeSquared=broadRange*broadRange,Directional=directional,RestrictToRoom=inst.LocalLightRestrictToRoom,
            DoHeightmaps=inst.LocalLightDoHeightmaps,DoGranny=inst.LocalLightDoGranny,DoSpeedTree=inst.LocalLightDoSpeedTree,DoCharacters=inst.LocalLightDoCharacters,DoWater=inst.LocalLightDoWater,
            PosRange=new Vector4(pos,broadRange),ColorIntensity=new Vector4(inst.LocalLightColor.X,inst.LocalLightColor.Y,inst.LocalLightColor.Z,inst.LocalLightIntensity),DirType=new Vector4(d,type),
            ProjectorInv=BuildLocalLightProjectorInverse(m,range),
            IlluminationPath=NormalizeTexturePath(inst.LocalLightIlluminationMap),FalloffPath=NormalizeTexturePath(inst.LocalLightFalloff),RampPath=NormalizeTexturePath(inst.LocalLightRampMap),
            ProjectorParams=new Vector4(inst.LocalLightSourceOffset,0,0,0)
          };
          if(directional||broadRange>LocalLightGridRangeLimit){localLightGlobal.Add(entry);continue;}
          int minX=LightCell(pos.X-broadRange),maxX=LightCell(pos.X+broadRange),minZ=LightCell(pos.Z-broadRange),maxZ=LightCell(pos.Z+broadRange);
          for(int z=minZ;z<=maxZ;z++)for(int x=minX;x<=maxX;x++){
            var key=(x,z);if(!localLightGrid.TryGetValue(key,out List<LocalLightEntry> bucket))localLightGrid[key]=bucket=new List<LocalLightEntry>();bucket.Add(entry);
          }
        }
      }
      currentLocalLightSelection=LocalLightSelection.Empty;lastLocalLightCount=-1;Array.Clear(lastLocalLightSelection,0,lastLocalLightSelection.Length);
    }
    private static int LightCell(float coordinate)=>(int)Math.Floor(coordinate/LocalLightCellSize);
    private static int LightSelectionCell(float coordinate)=>(int)Math.Floor(coordinate/LocalLightSelectionCellSize);

    private bool ReceiverCouldHaveIndexedLocalLights(Vector3 p,float receiverRadius){
      // The old instancing key split every model into a 1-unit XYZ light cell whenever local lighting was enabled,
      // even in outdoor cells with no local lights at all. That effectively disabled instancing on large planets and
      // left the D3D11 render thread draw-call bound. The 32-unit light grid already covers every light's authored
      // range, so an empty set of overlapped buckets proves the receiver's selection is empty. Such receivers can
      // share one batch without changing a single lighting result. Global/dynamic lights conservatively keep the
      // fine split because they are not represented by those fixed buckets.
      if(localLightGlobal.Count>0||dynamicSpnLocalLights.Count>0)return true;
      float radius=Math.Max(0f,receiverRadius);
      int minX=LightCell(p.X-radius),maxX=LightCell(p.X+radius),minZ=LightCell(p.Z-radius),maxZ=LightCell(p.Z+radius);
      long cells=(long)(maxX-minX+1)*(maxZ-minZ+1);
      if(cells>Math.Max(128L,(long)localLightGrid.Count*3L))return localLightGrid.Count>0;
      for(int z=minZ;z<=maxZ;z++)for(int x=minX;x<=maxX;x++)if(localLightGrid.TryGetValue((x,z),out List<LocalLightEntry> bucket)&&bucket.Count>0)return true;
      return false;
    }

    private void SetNearestLocalLights(Vector3 p,Room receiverRoom,WorldRenderSettings s,HashSet<string> visible,bool heightmap,bool water=false,bool character=false,float receiverRadius=0f,bool speedTree=false){
      if(!s.EnableLocalLights||!s.EnableLighting||s.Mode==WorldRenderMode.Unlit||s.Mode==WorldRenderMode.Wireframe||s.Mode==WorldRenderMode.Heightmap||s.Mode==WorldRenderMode.Map){ClearLocalLightBinding();return;}

      receiverRadius=Math.Max(0f,receiverRadius);
      int cellX=LightSelectionCell(p.X),cellY=LightSelectionCell(p.Y),cellZ=LightSelectionCell(p.Z);
      int radiusKey=(int)Math.Ceiling(Math.Min(receiverRadius,2048f)*2f); // half-unit radius buckets
      byte receiverKind=speedTree?(byte)4:(character?(byte)3:(water?(byte)2:(heightmap?(byte)1:(byte)0)));
      string receiverName=receiverRoom?.RoomName??String.Empty;
      var cacheKey=(cellX,cellY,cellZ,radiusKey,receiverName,receiverKind);

      if(!localLightSelectionCache.TryGetValue(cacheKey,out LocalLightSelection selection)){
        for(int i=0;i<MaxReceiverLocalLights;i++){localLightBest[i]=null;localLightBestDistance[i]=float.MaxValue;}
        int count=0;
        // The 32-unit buckets are only a broad phase. Test the receiver's REAL centre/bounds against every
        // light whose broad sphere overlaps one of the receiver's XZ cells, just as Jedipedia does.
        int minGridX=LightCell(p.X-receiverRadius),maxGridX=LightCell(p.X+receiverRadius);
        int minGridZ=LightCell(p.Z-receiverRadius),maxGridZ=LightCell(p.Z+receiverRadius);
        localLightCandidateScratch.Clear();
        long lightCellCount=(long)(maxGridX-minGridX+1)*(maxGridZ-minGridZ+1);
        if(lightCellCount>Math.Max(128L,(long)localLightGrid.Count*3L)){
          foreach(List<LocalLightEntry> bucket in localLightGrid.Values)for(int i=0;i<bucket.Count;i++)if(localLightCandidateScratch.Add(bucket[i]))
            count=ConsiderLocalLight(bucket[i],p,receiverRadius,receiverRoom,visible,heightmap,water,character,speedTree,count);
        } else {
          for(int gz=minGridZ;gz<=maxGridZ;gz++)for(int gx=minGridX;gx<=maxGridX;gx++){
            if(!localLightGrid.TryGetValue((gx,gz),out List<LocalLightEntry> bucket))continue;
            for(int i=0;i<bucket.Count;i++)if(localLightCandidateScratch.Add(bucket[i]))
              count=ConsiderLocalLight(bucket[i],p,receiverRadius,receiverRoom,visible,heightmap,water,character,speedTree,count);
          }
        }
        for(int i=0;i<localLightGlobal.Count;i++)count=ConsiderLocalLight(localLightGlobal[i],p,receiverRadius,receiverRoom,visible,heightmap,water,character,speedTree,count);
        for(int i=0;i<dynamicSpnLocalLights.Count;i++)count=ConsiderLocalLight(dynamicSpnLocalLights[i],p,receiverRadius,receiverRoom,visible,heightmap,water,character,speedTree,count);
        selection=LocalLightSelection.From(localLightBest,count);
        // Camera tours can otherwise leave one quantized receiver key per visited position in RAM forever. The
        // cache is only an acceleration structure, so a coarse cap has no visual/functional effect.
        if(localLightSelectionCache.Count>=16384)localLightSelectionCache.Clear();
        localLightSelectionCache[cacheKey]=selection;
      }

      currentLocalLightSelection=selection??LocalLightSelection.Empty;
      BindLocalLightSelection(currentLocalLightSelection);
    }

    private void BindLocalLightSelection(LocalLightSelection selection){
      selection??=LocalLightSelection.Empty;int count=Math.Min(LocalLightBaseSlots,selection.Count);
      bool unchanged=count==lastLocalLightCount;
      if(unchanged)for(int i=0;i<count;i++)if(!ReferenceEquals(lastLocalLightSelection[i],selection.Get(i))){unchanged=false;break;}
      if(unchanged)return;
      BindLocalLightChunk(selection,0,true);
    }

    private int BindLocalLightChunk(LocalLightSelection selection,int offset,bool trackBase=false){
      selection??=LocalLightSelection.Empty;int count=Math.Max(0,Math.Min(LocalLightBaseSlots,selection.Count-Math.Max(0,offset)));
      boundLocalLightTexturePaths.Clear();
      for(int n=0;n<LocalLightBaseSlots;n++){
        LocalLightEntry e=n<count?selection.Get(offset+n):null;if(trackBase)lastLocalLightSelection[n]=e;
        if(e!=null){
          ShaderResourceView illumination=LoadTexture(e.IlluminationPath),falloff=LoadTexture(e.FalloffPath),ramp=LoadTexture(e.RampPath);
          if(illumination!=null&&!String.IsNullOrWhiteSpace(e.IlluminationPath))boundLocalLightTexturePaths.Add(e.IlluminationPath);
          if(falloff!=null&&!String.IsNullOrWhiteSpace(e.FalloffPath))boundLocalLightTexturePaths.Add(e.FalloffPath);
          if(ramp!=null&&!String.IsNullOrWhiteSpace(e.RampPath))boundLocalLightTexturePaths.Add(e.RampPath);
          localLightPosRangeScratch[n]=e.PosRange;localLightColorScratch[n]=e.ColorIntensity;localLightDirScratch[n]=e.DirType;localLightProjectorScratch[n]=e.ProjectorInv;
          localLightProjectorParamsScratch[n]=new Vector4(e.ProjectorParams.X,illumination!=null?1:0,falloff!=null?1:0,ramp!=null?1:0);
          localLightIlluminationScratch[n]=illumination;localLightFalloffScratch[n]=falloff;localLightRampScratch[n]=ramp;
        }
        else {localLightPosRangeScratch[n]=new Vector4();localLightColorScratch[n]=new Vector4();localLightDirScratch[n]=new Vector4();localLightProjectorScratch[n]=Matrix.Identity;localLightProjectorParamsScratch[n]=new Vector4();localLightIlluminationScratch[n]=null;localLightFalloffScratch[n]=null;localLightRampScratch[n]=null;}
      }
      fx.SetLocalLights(localLightPosRangeScratch,localLightColorScratch,localLightDirScratch,localLightProjectorScratch,localLightProjectorParamsScratch,localLightIlluminationScratch,localLightFalloffScratch,localLightRampScratch,count);
      if(trackBase)lastLocalLightCount=count;return count;
    }

    private bool HasLocalLightOverflow => currentLocalLightSelection!=null&&currentLocalLightSelection.Count>LocalLightBaseSlots;
    private void InvalidateTrackedLocalLightBinding(){lastLocalLightCount=-1;Array.Clear(lastLocalLightSelection,0,lastLocalLightSelection.Length);}

    private int ConsiderLocalLight(LocalLightEntry e,Vector3 p,float receiverRadius,Room receiverRoom,HashSet<string> visible,bool heightmap,bool water,bool character,bool speedTree,int count){
      if(e==null||!RoomVisible(e.Room,visible)||(speedTree?!e.DoSpeedTree:(character?!e.DoCharacters:(water?!e.DoWater:(heightmap?!e.DoHeightmaps:!e.DoGranny))))||(e.RestrictToRoom&&receiverRoom!=e.Room))return count;
      float dist=-1f;
      if(!e.Directional){
        Vector3 delta=e.Position-p;float centerDistSq=delta.LengthSquared();
        float lightRadius=(float)Math.Sqrt(Math.Max(0f,e.RangeSquared));
        float reach=lightRadius+receiverRadius;
        if(centerDistSq>reach*reach)return count;
        // Jedipedia rejects a receiver against the actual authored projector BEFORE choosing the nearest lights.
        // Without this, four nearby projectors that do not cover the receiver can occupy all four shader slots;
        // the shader then masks them to zero and a fifth light that really illuminates the surface is never bound.
        if(!LocalLightProjectorIntersectsSphere(e,p,receiverRadius))return count;
        // Rank by distance to the receiver surface. A light touching a large room shell should beat an
        // unrelated light that merely happens to be closer to that shell's distant placement origin.
        float centerDist=(float)Math.Sqrt(Math.Max(0f,centerDistSq));
        float surfaceDist=Math.Max(0f,centerDist-receiverRadius);
        dist=surfaceDist*surfaceDist;
      }
      int insert=count;
      if(count<MaxReceiverLocalLights)count++;else {if(dist>=localLightBestDistance[MaxReceiverLocalLights-1])return count;insert=MaxReceiverLocalLights-1;}
      while(insert>0&&dist<localLightBestDistance[insert-1]){if(insert<MaxReceiverLocalLights){localLightBestDistance[insert]=localLightBestDistance[insert-1];localLightBest[insert]=localLightBest[insert-1];}insert--;}
      localLightBestDistance[insert]=dist;localLightBest[insert]=e;return count;
    }

    private static bool LocalLightProjectorIntersectsSphere(LocalLightEntry light,Vector3 center,float radius){
      if(light==null||light.Directional)return true;
      Matrix inv=light.ProjectorInv;
      // BuildLocalLightProjectorInverse is affine. Fail open if a malformed asset ever produces a projective matrix;
      // the pixel shader remains the final authority and a conservative false positive is harmless.
      if(Math.Abs(inv.M14)>.000001f||Math.Abs(inv.M24)>.000001f||Math.Abs(inv.M34)>.000001f||Math.Abs(inv.M44)<.000001f)return true;
      Vector3 local;try{local=Vector3.TransformCoordinate(center,inv);}catch{return true;}
      if(!IsFinite(local))return true;
      // A sphere becomes an ellipsoid under a non-uniform inverse transform. The Frobenius norm is a cheap upper
      // bound of the largest singular value, so this local sphere can only be larger than the true ellipsoid and
      // therefore cannot incorrectly cull an actually lit receiver. Jedipedia also pads by 0.05 world units.
      float scale=(float)Math.Sqrt(inv.M11*inv.M11+inv.M12*inv.M12+inv.M13*inv.M13+inv.M21*inv.M21+inv.M22*inv.M22+inv.M23*inv.M23+inv.M31*inv.M31+inv.M32*inv.M32+inv.M33*inv.M33);
      if(!Single.IsFinite(scale)||scale<=0f)return true;float r=(Math.Max(0f,radius)+.05f)*scale;
      float sourceOffset=Single.IsFinite(light.ProjectorParams.X)?Math.Min(1f,light.ProjectorParams.X):0f;float minZ=sourceOffset-1f;
      return local.X+r>=-1f&&local.X-r<=1f&&local.Y+r>=-1f&&local.Y-r<=1f&&local.Z+r>=minZ&&local.Z-r<=1f;
    }

    private void ClearLocalLightBinding(){
      currentLocalLightSelection=LocalLightSelection.Empty;boundLocalLightTexturePaths.Clear();if(lastLocalLightCount==0)return;fx.ClearLocalLights();lastLocalLightCount=0;Array.Clear(lastLocalLightSelection,0,lastLocalLightSelection.Length);
    }

    private static Matrix BuildLocalLightProjectorInverse(Matrix world,float range){
      // Matches Jedipedia lightProjectiveMatrix(): scale the three basis axes by the authored Range
      // while leaving translation untouched, then invert for world -> projector-local coordinates.
      float r=Math.Max(.0001f,range);Matrix scaled=world;
      scaled.M11*=r;scaled.M12*=r;scaled.M13*=r;
      scaled.M21*=r;scaled.M22*=r;scaled.M23*=r;
      scaled.M31*=r;scaled.M32*=r;scaled.M33*=r;
      try{return Matrix.Invert(scaled);}catch{return Matrix.Identity;}
    }

    private void RenderShadowCascades(AreaEnvironmentScheme env,HashSet<string> visible,WorldRenderSettings s){string skyRoom=ResolveSkyRoomName(env);for(int c=0;c<4;c++){float near=c==0?.01f:shadowDistances[c-1], far=shadowDistances[c];shadowMatrices[c]=BuildCascadeMatrix(env.PointLightDirection,near,far);shadowMaps[c].BindDsvAndSetNullRenderTarget(ImmediateContext);fx.SetViewProj(shadowMatrices[c]);ImmediateContext.InputAssembler.InputLayout=inputLayout;ImmediateContext.InputAssembler.PrimitiveTopology=PrimitiveTopology.TriangleList;DrawShadowGeometry(visible,s,skyRoom,far);}}
    private Matrix BuildCascadeMatrix(Vector4 toLight,float near,float far){Vector3 center=camera.Position+camera.Look*((near+far)*.5f);float radius=Math.Max(2,far*1.15f);Vector3 dir=new Vector3(toLight.X,toLight.Y,toLight.Z);if(dir.LengthSquared()<.001f)dir=new Vector3(0,1,0);dir.Normalize();Vector3 up=Math.Abs(Vector3.Dot(dir,new Vector3(0,1,0)))>.95f?new Vector3(0,0,1):new Vector3(0,1,0);Matrix view=Matrix.LookAtRH(center+dir*radius*2.2f,center,up);
      Vector3 lc=Vector3.TransformCoordinate(center,view);float texel=(radius*2)/1024f;lc.X=(float)Math.Floor(lc.X/texel)*texel;lc.Y=(float)Math.Floor(lc.Y/texel)*texel;Matrix proj=Matrix.OrthoOffCenterRH(lc.X-radius,lc.X+radius,lc.Y-radius,lc.Y+radius,.1f,radius*5);return view*proj;}
    private void DrawShadowGeometry(HashSet<string> visible,WorldRenderSettings s,string skyRoom,float cascadeFar){
      // Shadow cascades are tiny (<=25 units) compared with an open-world planet. Query the same spatial index as
      // the main pass instead of walking every instance four separate times each frame.
      foreach(RenderEntry entry in NearbyRenderEntries(RenderKindTerrain,cascadeFar,false,visible)){
        Room room=entry.Room;AssetInstance inst=entry.Instance;
        if(room==null||!InstanceVisibleInWorld(inst,s)||!RoomVisible(room,visible)||skyRoomNames.Contains(room.RoomName)||(!string.IsNullOrEmpty(skyRoom)&&room.RoomName==skyRoom))continue;
        Matrix world=entry.World;fx.SetWorld(world);
        if(s.ShowTerrain&&inst.VBO!=null){
          ImmediateContext.InputAssembler.InputLayout=inputLayout;ImmediateContext.InputAssembler.SetVertexBuffers(0,new VertexBufferBinding(inst.VBO,PosNormalTexTan.Stride,0));terrainGpu.TryGetValue(inst,out TerrainGpu tg);TerrainLodRange lod=SelectTerrainLod(inst,room,tg,s);Buffer terrainIbo=tg?.LodIndexBuffer??inst.IBO;
          if(terrainIbo!=null){ImmediateContext.InputAssembler.SetIndexBuffer(terrainIbo,Format.R16_UInt,0);fx.Shadow.GetPassByIndex(0).Apply(ImmediateContext);ImmediateContext.DrawIndexed(lod.Count>0?lod.Count:inst.numFaces,lod.Count>0?lod.StartIndex:0,0);}
        }
        if(s.ShowDynamicDetails&&inst.HeightMap?.DynamicDetails!=null&&DynamicDetailTerrainDistance(inst,room)<DynamicDetailRenderDistance){
          List<DynamicDetailGpu> batches=EnsureDynamicDetails(inst);if(batches!=null&&batches.Count>0){ImmediateContext.InputAssembler.InputLayout=dynamicDetailLayout;ImmediateContext.InputAssembler.PrimitiveTopology=PrimitiveTopology.TriangleList;foreach(DynamicDetailGpu batch in batches){if(batch.Buffer==null||batch.Count<=0||batch.Material==null)continue;GR2_Material dynamicMaterial=EnsureMaterialParsed(batch.Material);if(dynamicMaterial==null||dynamicMaterial.diffuseSRV==null)continue;fx.SetMaterial(dynamicMaterial);fx.SetDynamicDetail(camera.Right,batch.AtlasMode,batch.Wind,dynamicMaterial.vegetationParams2.Z,batch.TextureSize);ImmediateContext.InputAssembler.SetVertexBuffers(0,new VertexBufferBinding(batch.Buffer,48,0));fx.DynamicDetailShadow.GetPassByIndex(0).Apply(ImmediateContext);ImmediateContext.Draw(batch.Count,0);}}
          List<DynamicDetailMeshBatch> meshBatches=EnsureDynamicDetailMeshes(inst);if(meshBatches.Count>0){ImmediateContext.InputAssembler.InputLayout=instancedLayout;foreach(DynamicDetailMeshBatch meshBatch in meshBatches)if(meshBatch.Model!=null&&meshBatch.Model.enabled&&meshBatch.InstanceBuffer!=null&&meshBatch.InstanceCount>0)DrawInstancedModelShadow(meshBatch.Model,meshBatch.InstanceBuffer,meshBatch.InstanceCount,world);}
        }
      }
      if(s.ShowModels){
        // The main pass now instances repeated opaque/test GR2s; do the same for each CSM cascade. Without this,
        // a planet with thousands of repeated props still paid four complete model draw-call streams per frame even
        // though the shadow radius is only 1.5..25 units. The spatial query limits candidates first, then batching
        // collapses the remaining repeated placements to one draw per model/submesh.
        var shadowBatches=new Dictionary<(GR2 Model,int Lod),ModelInstanceBatch>();var shadowSingles=new List<(RenderEntry Entry,int Lod)>();
        foreach(RenderEntry entry in NearbyRenderEntries(RenderKindModel,cascadeFar,false,visible)){
          Room room=entry.Room;GR2 model=entry.Model;
          if(room==null||model==null||!model.enabled||!InstanceRoomVisible(entry.Instance,room,visible)||!InstanceVisibleInWorld(entry.Instance,s)||skyRoomNames.Contains(room.RoomName)||(!string.IsNullOrEmpty(skyRoom)&&room.RoomName==skyRoom))continue;
          if(IsSpeedTreeInstance(entry.Instance)&&!s.ShowSpeedTrees)continue;
          float lodFactor=entry.Instance?.LodFactor??1f;if(ShouldCullModelByLod(model,entry.World,false,lodFactor))continue;int lod=SelectModelLodLevel(model,entry.World,false,lodFactor);
          if(ModelCanUseRegularInstancing(model)&&MatrixHasNearlyUniformScale(entry.World)){
            var key=(model,lod);if(!shadowBatches.TryGetValue(key,out ModelInstanceBatch batch))shadowBatches[key]=batch=new ModelInstanceBatch{Model=model,LodLevel=lod};batch.Worlds.Add(entry.World);
          }else shadowSingles.Add((entry,lod));
        }
        ImmediateContext.InputAssembler.InputLayout=inputLayout;
        foreach(var single in shadowSingles){fx.SetWorld(single.Entry.World);DrawModelShadow(single.Entry.Model,single.Lod,single.Entry.World);}
        regularModelFrameInstances.Clear();
        foreach(ModelInstanceBatch batch in shadowBatches.Values){
          if(batch.Worlds.Count<2)continue;
          batch.StartInstance=regularModelFrameInstances.Count;
          regularModelFrameInstances.AddRange(batch.Worlds);
        }
        Buffer sharedShadowInstances=regularModelFrameInstances.Count>0?UploadRegularModelInstances(regularModelFrameInstances):null;
        foreach(ModelInstanceBatch batch in shadowBatches.Values){
          GR2 model=batch.Model;int lod=batch.LodLevel;
          if(batch.Worlds.Count<2){fx.SetWorld(batch.Worlds[0]);ImmediateContext.InputAssembler.InputLayout=inputLayout;DrawModelShadow(model,lod,batch.Worlds[0]);continue;}
          if(sharedShadowInstances==null){foreach(Matrix world in batch.Worlds){fx.SetWorld(world);ImmediateContext.InputAssembler.InputLayout=inputLayout;DrawModelShadow(model,lod,world);}continue;}
          ImmediateContext.InputAssembler.InputLayout=instancedLayout;DrawInstancedModelShadow(model,sharedShadowInstances,batch.Worlds.Count,Matrix.Identity,lod,batch.Worlds[0],batch.StartInstance);
        }
      }
      ImmediateContext.InputAssembler.InputLayout=inputLayout;ImmediateContext.InputAssembler.PrimitiveTopology=PrimitiveTopology.TriangleList;
    }
    private void DrawModelShadow(GR2 model,int forcedLod=-1,Matrix? lodReferenceWorld=null){
      if(model==null||!EnsureModelGeometryPrepared(model))return;Matrix reference=lodReferenceWorld??Matrix.Identity;int selectedLod=forcedLod>=0?forcedLod:SelectModelLodLevel(model,reference,false);
      foreach(var mesh in model.meshes){
        if(mesh.vertBuffer==null||mesh.idxBuffer==null||!MeshVisibleForLod(model,mesh,selectedLod))continue;
        ImmediateContext.InputAssembler.SetVertexBuffers(0,new VertexBufferBinding(mesh.vertBuffer,PosNormalTexTan.Stride,0));ImmediateContext.InputAssembler.SetIndexBuffer(mesh.idxBuffer,Format.R16_UInt,0);
        foreach(var piece in mesh.meshPieces){
          GR2_Material mat=ResolvePieceMaterial(model,piece);
          if(IsMaterialHiddenFromWorld(mat))continue;
          bool alpha=mat!=null&&!string.IsNullOrEmpty(mat.alphaMode)&&mat.alphaMode!="None";if(alpha)fx.SetMaterial(mat);
          (alpha?fx.AlphaShadow:fx.Shadow).GetPassByIndex(0).Apply(ImmediateContext);ImmediateContext.DrawIndexed((int)piece.numPieceFaces*3,(int)piece.startIndex*3,0);
        }
      }
      foreach(var a in model.attachedModels)DrawModelShadow(a,-1,reference);
    }

    private void BuildModelGeometry(){
      // GR2 CPU data remains available for exact picking, animation and later streaming, but immutable D3D buffers
      // are now uploaded only when a model actually enters a visible draw pass. This removes the former whole-world
      // GPU upload pause and prevents off-screen assets from consuming VRAM just because their area was loaded.
      modelGeometryPrepared.Clear();
    }
    private bool ModelGeometryBuffersReady(GR2 model){
      if(model==null)return false;
      foreach(GR2_Mesh mesh in model.meshes)if(mesh!=null&&!IsNonVisualMesh(model,mesh)&&mesh.meshVerts!=null&&mesh.meshVerts.Count>0&&mesh.meshVertIndex!=null&&mesh.meshVertIndex.Count>0&&(mesh.vertBuffer==null||mesh.idxBuffer==null))return false;
      foreach(GR2 attached in model.attachedModels)if(attached!=null&&!ModelGeometryBuffersReady(attached))return false;
      return true;
    }
    private bool EnsureModelGeometryPrepared(GR2 model){
      if(model==null)return false;
      MarkModelGeometryUsed(model);
      // Streamed world assets may only be uploaded by ProcessModelGpuUploads(). Do not let an unexpected draw,
      // shadow pass or warmup turn into an unbounded synchronous upload hitch.
      if(streamedWorldModels.Contains(model)){
        QueueModelGpuUpload(model);
        if(streamedModelAssetIds.TryGetValue(model,out ulong assetId)&&materialStreamAssetIds.Contains(assetId))QueueStreamedModelMaterials(model);
        // Geometry is the room-continuity barrier; textures are deliberately independent. ResolvePieceMaterial() uses
        // metadata-only streamed materials until the bounded DDS queue catches up, so missing textures become a brief
        // flat fallback rather than a missing wall/floor or a synchronous hitch.
        return modelGeometryPrepared.Contains(model)&&ModelGeometryBuffersReady(model);
      }
      if(modelGeometryPrepared.Contains(model)&&ModelGeometryBuffersReady(model))return true;
      var built=new HashSet<GR2>();BuildModelGeometry(model,built,false);foreach(GR2 prepared in built)modelGeometryPrepared.Add(prepared);
      return true;
    }
    private void MarkModelGeometryUsed(GR2 model){
      if(model==null)return;
      modelGeometryLastUseFrame[model]=worldRenderFrame;
      foreach(GR2 attached in model.attachedModels)MarkModelGeometryUsed(attached);
    }
    private static bool ModelHasAnyGpuBuffers(GR2 model){
      if(model==null)return false;
      foreach(GR2_Mesh mesh in model.meshes)if(mesh!=null&&(mesh.vertBuffer!=null||mesh.idxBuffer!=null))return true;
      foreach(GR2 attached in model.attachedModels)if(ModelHasAnyGpuBuffers(attached))return true;
      return false;
    }
    private static bool ModelTreeContains(GR2 root,GR2 model){
      if(root==null||model==null)return false;if(ReferenceEquals(root,model))return true;
      foreach(GR2 attached in root.attachedModels)if(ModelTreeContains(attached,model))return true;
      return false;
    }
    private void ForgetPreparedModelGeometry(GR2 model){
      if(model==null)return;modelGeometryPrepared.Remove(model);modelGeometryLastUseFrame.Remove(model);
      foreach(GR2 attached in model.attachedModels)ForgetPreparedModelGeometry(attached);
    }
    private IEnumerable<GR2> WorldModelResidencyRoots(){
      foreach(GR2 model in models.Values)if(model!=null)yield return model;
      if(utilityMarkerModels!=null)foreach(GR2 model in utilityMarkerModels.Values)if(model!=null)yield return model;
      // Population data normally belongs exclusively to the render view, but keep this maintenance pass resilient to
      // a browser-side refresh as well. List<T>.foreach is versioned and throws immediately when another thread
      // clears/rebuilds the source list; small array snapshots make VRAM trimming observational instead of fatal.
      WorldNpcPlacement[] npcSnapshot=npcPlacements?.ToArray()??Array.Empty<WorldNpcPlacement>();
      foreach(WorldNpcPlacement placement in npcSnapshot){GR2[] placementModels=placement?.Models?.ToArray()??Array.Empty<GR2>();foreach(GR2 model in placementModels)if(model!=null)yield return model;}
      WorldSpnPlacement[] spnSnapshot=spnPlacements?.ToArray()??Array.Empty<WorldSpnPlacement>();
      foreach(WorldSpnPlacement placement in spnSnapshot){GR2[] placementModels=placement?.Models?.ToArray()??Array.Empty<GR2>();foreach(GR2 model in placementModels)if(model!=null)yield return model;}
      foreach(GR2 model in dynamicDetailMeshModels.Values)if(model!=null)yield return model;
      foreach(GR2 model in strongholdHookModels.Values)if(model!=null)yield return model;
      if(taxiRideVehicleModel!=null)yield return taxiRideVehicleModel;
    }
    private IEnumerable<GR2_Material> WorldMaterialResidencySet(){
      var seenMaterials=new HashSet<GR2_Material>();
      foreach(GR2_Material material in materials.Values)if(material!=null&&seenMaterials.Add(material))yield return material;
      foreach(GR2_Material material in terrainMaterials.Values)if(material!=null&&seenMaterials.Add(material))yield return material;
      foreach(GR2_Material material in dynamicDetailMaterials.Values)if(material!=null&&seenMaterials.Add(material))yield return material;

      // Models loaded for population appearances can own per-NPC palette/complexion materials.  Walk attached
      // models as well, but de-duplicate both models and materials because attachments often intentionally share them.
      var seenModels=new HashSet<GR2>();var pending=new Stack<GR2>();
      foreach(GR2 root in WorldModelResidencyRoots())if(root!=null)pending.Push(root);
      while(pending.Count>0){
        GR2 model=pending.Pop();if(model==null||!seenModels.Add(model))continue;
        if(model.materials!=null)foreach(GR2_Material material in model.materials)if(material!=null&&seenMaterials.Add(material))yield return material;
        if(model.attachedModels!=null)foreach(GR2 attached in model.attachedModels)if(attached!=null)pending.Push(attached);
      }
    }
    private void TrimModelGeometryResidency(){
      // Keep visible geometry on the GPU, but do not let a long camera tour make every model visited since area
      // load permanently resident. Include population/SPN/helper models as well as static room assets: NPC-heavy
      // sessions were otherwise still able to accumulate their entire visited population in VRAM. CPU GR2 data is
      // retained so exact picking and a later re-upload remain lossless.
      const int highWater=192,target=144;const long idleFrames=240;
      List<GR2> resident=WorldModelResidencyRoots().Where(ModelHasAnyGpuBuffers).Distinct().ToList();
      if(resident.Count<=highWater)return;
      GR2 selected=selectedWorldRenderEntry?.Model;long cutoff=worldRenderFrame-idleFrames;
      foreach(GR2 model in resident.OrderBy(m=>modelGeometryLastUseFrame.TryGetValue(m,out long frame)?frame:long.MinValue).ToList()){
        if(resident.Count<=target)break;
        if(selected!=null&&ModelTreeContains(model,selected))continue;
        if(streamedModelAssetIds.TryGetValue(model,out ulong streamAssetId)&&IsStreamAssetActivelyDemanded(streamAssetId))continue;
        long last=modelGeometryLastUseFrame.TryGetValue(model,out long frame)?frame:long.MinValue;
        if(last>cutoff)continue;
        ReleaseModelBuffers(model);ForgetPreparedModelGeometry(model);resident.Remove(model);
      }
    }
    private void TrimStreamedModelCpuResidency(){
      // The former v5 LRU released only D3D buffers; the parsed Granny object graph remained in RAM forever.
      // Drop cold streamed roots completely and make their room request eligible again. Render entries are removed
      // alongside the model so a later room visit is a normal background decode/re-upload, never a dangling draw.
      // A Corellia room routinely has more than 64 distinct GR2 roots.  64/40 made a just decoded room evict
      // its own structural pieces before the renderer could upload them.  The active set is always protected;
      // only truly cold rooms are reduced to the 256-root CPU working set.
      const int highWater=384,target=256;const long idleFrames=300;
      if(streamedWorldModels.Count<=highWater)return;
      long cutoff=worldRenderFrame-idleFrames;GR2 selected=selectedWorldRenderEntry?.Model;
      foreach(GR2 model in streamedWorldModels.OrderBy(m=>modelGeometryLastUseFrame.TryGetValue(m,out long frame)?frame:long.MinValue).ToList()){
        if(streamedWorldModels.Count<=target)break;
        if(model==null||ReferenceEquals(model,selected)||!streamedModelAssetIds.TryGetValue(model,out ulong assetId)||IsStreamAssetActivelyDemanded(assetId))continue;
        long last=modelGeometryLastUseFrame.TryGetValue(model,out long frame)?frame:long.MinValue;if(last>cutoff)continue;
        RemoveStreamedModel(assetId,model);
      }
    }
    private void RemoveStreamedModel(ulong assetId,GR2 model){
      ReleaseModelBuffers(model);ForgetPreparedModelGeometry(model);models.Remove(assetId);streamedWorldModels.Remove(model);streamedModelAssetIds.Remove(model);queuedModelGpuUploads.Remove(model);queuedStreamedModelMaterials.Remove(model);streamedModelMaterialsReady.Remove(model);
      foreach(var key in queuedStreamedModelIntegrations.Where(x=>x.AssetId==assetId).ToList())queuedStreamedModelIntegrations.Remove(key);
      foreach(var key in queuedStreamedFloorIntegrations.Where(x=>x.AssetId==assetId).ToList())queuedStreamedFloorIntegrations.Remove(key);
      // Keep canonical MAT metadata in the area dictionary. Streamed models deliberately share those objects; removing
      // an entry just because one GR2 is evicted can invalidate another still-resident model that references it.
      GR2[] retainedUploads=pendingModelGpuUploads.Where(x=>!ReferenceEquals(x,model)).ToArray();pendingModelGpuUploads.Clear();foreach(GR2 queued in retainedUploads)pendingModelGpuUploads.Enqueue(queued);
      PendingStreamedModelIntegration[] retainedIntegrations=pendingStreamedModelIntegrations.Where(x=>x!=null&&!ReferenceEquals(x.Model,model)).ToArray();pendingStreamedModelIntegrations.Clear();foreach(PendingStreamedModelIntegration queued in retainedIntegrations)pendingStreamedModelIntegrations.Enqueue(queued);
      PendingStreamedFloorIntegration[] retainedFloors=pendingStreamedFloorIntegrations.Where(x=>x!=null&&!ReferenceEquals(x.Model,model)).ToArray();pendingStreamedFloorIntegrations.Clear();foreach(PendingStreamedFloorIntegration queued in retainedFloors)pendingStreamedFloorIntegrations.Enqueue(queued);
      PendingStreamedModelMaterials[] retainedMaterials=pendingStreamedModelMaterials.Where(x=>x!=null&&!ReferenceEquals(x.Model,model)).ToArray();pendingStreamedModelMaterials.Clear();foreach(PendingStreamedModelMaterials queued in retainedMaterials)pendingStreamedModelMaterials.Enqueue(queued);
      pendingStreamedMaterialPrepares.Clear();foreach(PendingStreamedModelMaterials pending in pendingStreamedModelMaterials)if(pending?.Materials!=null)foreach(GR2_Material material in pending.Materials)if(material!=null)pendingStreamedMaterialPrepares.Add(material);
      if(modelFloorData.TryGetValue(model,out ModelFloorData floor)){modelFloorData.Remove(model);modelFloorPlacementGlobal.RemoveAll(x=>ReferenceEquals(x.Model,floor));foreach(List<ModelFloorPlacementEntry> bucket in modelFloorPlacementGrid.Values)bucket.RemoveAll(x=>ReferenceEquals(x.Model,floor));modelFloorPlacementGrid.Where(x=>x.Value.Count==0).Select(x=>x.Key).ToList().ForEach(key=>modelFloorPlacementGrid.Remove(key));}
      if(streamPlacementsByAsset.TryGetValue(assetId,out List<(Room Room,AssetInstance Instance)> placements))foreach(var placement in placements){streamIndexedInstances.Remove(placement.Instance);streamFloorIndexedInstances.Remove(placement.Instance);}
      foreach(List<RenderEntry> bucket in renderGrid.Values)bucket.RemoveAll(x=>ReferenceEquals(x.Model,model));renderGrid.Where(x=>x.Value.Count==0).Select(x=>x.Key).ToList().ForEach(key=>renderGrid.Remove(key));
      renderGlobal.RemoveAll(x=>ReferenceEquals(x.Model,model));walkingPathFollowerRenderEntries.RemoveAll(x=>ReferenceEquals(x.Model,model));occluderRenderEntries.RemoveAll(x=>ReferenceEquals(x.Model,model));
      foreach(List<RenderEntry> bucket in renderEntriesByRoom.Values)bucket.RemoveAll(x=>ReferenceEquals(x.Model,model));renderEntriesByRoom.Where(x=>x.Value.Count==0).Select(x=>x.Key).ToList().ForEach(key=>renderEntriesByRoom.Remove(key));
      modelStreamer?.Forget(assetId);
    }
    private void BuildModelGeometry(GR2 model,HashSet<GR2> built,bool attachment){
      if(model==null||!built.Add(model))return;
      foreach(var mesh in model.meshes){
        if(IsNonVisualMesh(model,mesh))continue;
        if(mesh.vertBuffer!=null&&mesh.idxBuffer!=null)continue;
        Release(ref mesh.vertBuffer);Release(ref mesh.idxBuffer);
        var verts=new PosNormalTexTan[mesh.meshVerts.Count];
        for(int i=0;i<verts.Length;i++){
          var v=mesh.meshVerts[i];Vector3 p=new Vector3(v.X,v.Y,v.Z);
          if(attachment)foreach(Matrix m in AttachmentMatrices(model))if(m!=new Matrix())p=Vector3.TransformCoordinate(p,m);
          verts[i]=new PosNormalTexTan(p,new Vector3(v.normX,v.normY,v.normZ),new Vector2(v.texU,v.texV),new Vector3(v.tanX,v.tanY,v.tanZ));
        }
        CreateModelBuffers(mesh,verts);
      }
      foreach(var a in model.attachedModels)BuildModelGeometry(a,built,true);
    }
    private static IEnumerable<Matrix> AttachmentMatrices(GR2 a){yield return a.attachMatrix;yield return a.parentPosMatrix;yield return a.scaleMatrix;yield return a.parentRotMatrix;yield return a.rotationMatrix;yield return a.positionMatrix;}
    private void CreateModelBuffers(GR2_Mesh mesh,PosNormalTexTan[] verts){var vbd=new BufferDescription(PosNormalTexTan.Stride*verts.Length,ResourceUsage.Immutable,BindFlags.VertexBuffer,CpuAccessFlags.None,ResourceOptionFlags.None,0);using(var ds=new DataStream(verts,false,false))mesh.vertBuffer=new Buffer(Device,ds,vbd);ushort[] idx=mesh.meshVertIndex.Select(x=>x.index).ToArray();var ibd=new BufferDescription(sizeof(ushort)*idx.Length,ResourceUsage.Immutable,BindFlags.IndexBuffer,CpuAccessFlags.None,ResourceOptionFlags.None,0);using(var ds=new DataStream(idx,false,false))mesh.idxBuffer=new Buffer(Device,ds,ibd);}
    private void BuildEmbeddedGeometry(){foreach(Room r in rooms)foreach(AssetInstance i in r.InstancesById.Values)if((i.hasHeightMap||i.hasWater)&&i.VDS!=null&&i.IDS!=null){i.VBO=new Buffer(Device,i.VDS,i.VBD);i.IBO=new Buffer(Device,i.IDS,i.IBD);i.VDS.Dispose();i.IDS.Dispose();i.VDS=null;i.IDS=null;}}

    private void BuildTerrainResources(){
      foreach(Room r in rooms)foreach(AssetInstance i in r.InstancesById.Values){
        if(!i.hasHeightMap||i.HeightMap==null)continue;var g=new TerrainGpu();
        BuildTerrainLods(i,g);terrainGpu[i]=g;
      }
    }

    private void EnsureTerrainTextures(AssetInstance inst,TerrainGpu gpu){
      if(inst?.HeightMap==null||gpu==null)return;gpu.LastUseFrame=worldRenderFrame;if(gpu.TexturesPrepared)return;
      HeightMap hm=inst.HeightMap;
      gpu.ColorMap=CreateTexture(hm.TerrainColorMapRgba,(int)hm.width,(int)hm.depth,Format.R8G8B8A8_UNorm,4);
      foreach(TerrainLayerMask layer in hm.TerrainLayers){
        if(layer==null||String.IsNullOrWhiteSpace(layer.MaterialName))continue;
        ShaderResourceView old;if(gpu.Masks.TryGetValue(layer.MaterialName,out old))old?.Dispose();
        gpu.Masks[layer.MaterialName]=CreateTexture(layer.Weights,layer.Width,layer.Height,Format.R8_UNorm,1);
      }
      gpu.TexturesPrepared=true;
    }

    private void TrimTerrainTextureResidency(WorldRenderSettings s){
      const int highWater=96,target=64;
      List<TerrainGpu> resident=terrainGpu.Values.Where(x=>x!=null&&x.TexturesPrepared).Distinct().ToList();
      if(resident.Count<=highWater)return;
      // In perspective mode, map-only tiles can be dropped immediately as long as they were not used this frame.
      // During a whole-area map render every visible tile is needed, so avoid destructive per-frame stream thrash.
      bool map=s?.Mode==WorldRenderMode.Map;
      foreach(TerrainGpu gpu in resident.OrderBy(x=>x.LastUseFrame).ToList()){
        if(resident.Count<=target)break;
        if(gpu.LastUseFrame>=worldRenderFrame-1)continue;
        if(map&&gpu.LastUseFrame>worldRenderFrame-360)continue;
        gpu.ReleaseTextures();resident.Remove(gpu);
      }
    }
    private void BuildTerrainLods(AssetInstance i,TerrainGpu g){
      HeightMap hm=i.HeightMap;if(hm==null||hm.hasHoles)return;int w=checked((int)hm.width),d=checked((int)hm.depth);if(w<3||d<3||w*d>65535)return;
      var key=(w,d);if(terrainIndexCache.TryGetValue(key,out SharedTerrainIndexGpu cached)){g.LodIndexBuffer=cached.Buffer;g.LodRanges=cached.Ranges;g.OwnsLodIndexBuffer=false;return;}
      var all=new List<ushort>(Math.Max(6,(w-1)*(d-1)*8));var ranges=new List<TerrainLodRange>();
      int start=all.Count;AppendTerrainFullGrid(all,w,d);ranges.Add(new TerrainLodRange(start,all.Count-start));
      int cellsX=w-1,cellsZ=d-1;foreach(int stride in new[]{2,4,8}){if(cellsX%stride!=0||cellsZ%stride!=0)break;if(cellsX/stride<2||cellsZ/stride<2)break;start=all.Count;AppendTerrainStride(all,w,d,stride);ranges.Add(new TerrainLodRange(start,all.Count-start));}
      if(ranges.Count<=1)return;ushort[] data=all.ToArray();var bd=new BufferDescription(sizeof(ushort)*data.Length,ResourceUsage.Immutable,BindFlags.IndexBuffer,CpuAccessFlags.None,ResourceOptionFlags.None,0);using var ds=new DataStream(data,false,false);
      var shared=new SharedTerrainIndexGpu{Buffer=new Buffer(Device,ds,bd),Ranges=ranges.ToArray()};terrainIndexCache[key]=shared;g.LodIndexBuffer=shared.Buffer;g.LodRanges=shared.Ranges;g.OwnsLodIndexBuffer=false;
    }
    private static void AppendTerrainFullGrid(List<ushort> output,int width,int depth){for(int z=0;z<depth-1;z++)for(int x=0;x<width-1;x++){output.Add((ushort)(z*width+x));output.Add((ushort)((z+1)*width+x));output.Add((ushort)(z*width+x+1));output.Add((ushort)(z*width+x+1));output.Add((ushort)((z+1)*width+x));output.Add((ushort)((z+1)*width+x+1));}}
    private static void AppendTerrainStride(List<ushort> output,int width,int depth,int stride){
      int cellsX=width-1,cellsZ=depth-1,half=stride/2;var ring=new List<ushort>(stride*4);
      for(int z0=0;z0<cellsZ;z0+=stride)for(int x0=0;x0<cellsX;x0+=stride){int x1=x0+stride,z1=z0+stride;ushort centre=(ushort)((z0+half)*width+(x0+half));ring.Clear();
        int step=x0==0?1:stride;for(int t=0;t<stride;t+=step)ring.Add((ushort)((z0+t)*width+x0));
        step=z1==cellsZ?1:stride;for(int t=0;t<stride;t+=step)ring.Add((ushort)(z1*width+(x0+t)));
        step=x1==cellsX?1:stride;for(int t=0;t<stride;t+=step)ring.Add((ushort)((z1-t)*width+x1));
        step=z0==0?1:stride;for(int t=0;t<stride;t+=step)ring.Add((ushort)(z0*width+(x1-t)));
        for(int n=0;n<ring.Count;n++){output.Add(centre);output.Add(ring[n]);output.Add(ring[(n+1)%ring.Count]);}
      }
    }
    private void BuildWaterResources(){
      defaultWaterNormal=CreateTexture(new byte[]{0,127,0,127},1,1,Format.R8G8B8A8_UNorm,4);
      defaultWaterDepth=CreateTexture(new byte[]{255,255,255,160},1,1,Format.R8G8B8A8_UNorm,4);
      foreach(Room room in rooms)foreach(AssetInstance i in room.InstancesById.Values){
        if(!i.hasWater)continue;
        waterGpu[i]=new WaterGpu();
      }
    }
    private void EnsureWaterTextures(AssetInstance inst,WaterGpu gpu){
      if(inst==null||gpu==null)return;gpu.LastUseFrame=worldRenderFrame;
      // Shared normal/surface maps are touched on every visible draw so the global texture LRU cannot invalidate a
      // WaterGpu reference. The per-instance depth map is decoded only while this water body is actually resident.
      gpu.Normal1=LoadTexture(inst.WaterNormalMap1);
      gpu.Normal2=LoadTexture(inst.WaterNormalMap2)??gpu.Normal1;
      gpu.SurfaceMap=LoadTexture(inst.WaterSurfaceMap);
      if(!gpu.TexturesPrepared){
        gpu.DepthMap=CreateWaterDepthTexture(inst.WaterDepthData);
        if(gpu.DepthMap!=null)gpu.OwnsDepthMap=true;else gpu.DepthMap=defaultWaterDepth;
        gpu.TexturesPrepared=true;
      }
      if(gpu.SurfaceMap==null&&inst.WaterTextureIndex>=0){
        string terrainName=null;
        if(area?.TerrainTextureNames.TryGetValue((uint)inst.WaterTextureIndex,out string byId)==true)terrainName=byId;
        else if(area!=null&&inst.WaterTextureIndex<area.TerrainTextures.Count)terrainName=area.TerrainTextures[inst.WaterTextureIndex].Name;
        if(!string.IsNullOrEmpty(terrainName))gpu.SurfaceMap=GetTerrainMaterial(terrainName).waterSurfaceSRV;
      }
    }
    private void TrimWaterTextureResidency(WorldRenderSettings s){
      const int highWater=32,target=20;const long idleFrames=180;
      List<WaterGpu> resident=waterGpu.Values.Where(g=>g!=null&&g.TexturesPrepared).Distinct().ToList();if(resident.Count<=highWater)return;
      bool map=s?.Mode==WorldRenderMode.Map;long cutoff=worldRenderFrame-idleFrames;
      foreach(WaterGpu gpu in resident.OrderBy(g=>g.LastUseFrame).ToList()){
        if(resident.Count<=target)break;if(gpu.LastUseFrame>=worldRenderFrame-1)continue;if(map&&gpu.LastUseFrame>cutoff)continue;
        gpu.ReleaseTextures();resident.Remove(gpu);
      }
    }
    private ShaderResourceView CreateWaterDepthTexture(byte[] raw){
      if(raw==null||raw.Length==0)return null;
      try{
        byte[] bytes=AssetInstance.DecompressRoomPayload(raw);if(bytes==null||bytes.Length<9||bytes[0]!=1)return null;
        int w=BitConverter.ToInt32(bytes,1),h=BitConverter.ToInt32(bytes,5);long pixels=(long)w*h;
        if(w<=0||h<=0||w>16384||h>16384||pixels<=0||pixels>268435456L||bytes.Length<9+pixels)return null;
        int pixelCount=checked(w*h);byte[] rgba=new byte[checked(pixelCount*4)];for(int n=0;n<pixelCount;n++){int o=n*4;rgba[o]=255;rgba[o+1]=255;rgba[o+2]=255;rgba[o+3]=bytes[9+n];}
        return CreateTexture(rgba,w,h,Format.R8G8B8A8_UNorm,4);
      }catch(Exception ex){System.Diagnostics.Debug.WriteLine("Could not decode WTR DepthTexture: "+ex.Message);return null;}
    }
    private ShaderResourceView CreateTexture(byte[] bytes,int w,int h,Format fmt,int bpp){if(bytes==null||w<=0||h<=0)return null;var desc=new Texture2DDescription{Width=w,Height=h,MipLevels=1,ArraySize=1,Format=fmt,SampleDescription=new SampleDescription(1,0),Usage=ResourceUsage.Immutable,BindFlags=BindFlags.ShaderResource,CpuAccessFlags=CpuAccessFlags.None,OptionFlags=ResourceOptionFlags.None};using var ds=new DataStream(bytes,false,false);using var tex=new Texture2D(Device,desc,new DataRectangle(w*bpp,ds));return new ShaderResourceView(Device,tex);}
    private GR2_Material GetTerrainMaterial(string name){
      name=ResolveTerrainMaterialName(name);
      if(terrainMaterials.TryGetValue(name,out GR2_Material m))return EnsureMaterialParsed(m);
      m=new GR2_Material(name);terrainMaterials[name]=m;return EnsureMaterialParsed(m);
    }
    private string ResolveTerrainMaterialName(string name){
      string exact=NormalizeTerrainMaterialName(name);
      if(TerrainMaterialExists(exact))return exact;
      // Compatibility only: older PugTools paths sometimes handed this routine a diffuse texture
      // name instead of the area.dat material name. Never rewrite a valid exact material.
      string candidate=exact;
      if(candidate.EndsWith(".dds",StringComparison.OrdinalIgnoreCase))candidate=candidate.Substring(0,candidate.Length-4);
      if(candidate.EndsWith("_d",StringComparison.OrdinalIgnoreCase)){string baseName=candidate.Substring(0,candidate.Length-2);if(TerrainMaterialExists(baseName))return baseName;}
      return exact;
    }
    private bool TerrainMaterialExists(string name){
      if(string.IsNullOrWhiteSpace(name)||area==null)return false;
      using var file=area.FindFile("/resources/art/shaders/materials/"+name+".mat");
      return file!=null;
    }
    private static string NormalizeTerrainMaterialName(string name){
      if(string.IsNullOrWhiteSpace(name))return "terrain_checkered";
      string n=name.Trim().Replace('\\','/');int slash=n.LastIndexOf('/');if(slash>=0)n=n.Substring(slash+1);
      foreach(string ext in new[]{".dds",".tga",".png",".jpg",".jpeg",".mat"})if(n.EndsWith(ext,StringComparison.OrdinalIgnoreCase)){n=n.Substring(0,n.Length-ext.Length);break;}
      if(n.EndsWith("_d",StringComparison.OrdinalIgnoreCase))n=n.Substring(0,n.Length-2);
      return string.IsNullOrWhiteSpace(n)?"terrain_checkered":n.ToLowerInvariant();
    }

    private ShaderResourceView LoadTexture(string raw,bool pin=false){
      string path=NormalizeTexturePath(raw);if(path==null)return null;
      if(textureCache.TryGetValue(path,out ShaderResourceView v)){textureLastUseFrame[path]=worldRenderFrame;if(pin)pinnedTexturePaths.Add(path);return v;}
      try{using var file=area?.FindFile(path);if(file==null)return null;using Stream stream=file.OpenCopyInMemory();int mip=ClampDdsFirstMipLevel(stream,Math.Max(0,appliedTextureMipSkip));
        if(mip>0){ImageLoadInformation info=ImageLoadInformation.FromDefaults();info.FirstMipLevel=mip;v=ShaderResourceView.FromStream(Device,stream,(int)stream.Length,info);}
        else v=ShaderResourceView.FromStream(Device,stream,(int)stream.Length);
        textureCache[path]=v;textureLastUseFrame[path]=worldRenderFrame;if(pin)pinnedTexturePaths.Add(path);return v;
      }catch(Exception ex){System.Diagnostics.Debug.WriteLine("World texture failed "+path+": "+ex.Message);return null;}
    }
    private void TrimSharedTextureResidency(){
      // Environment, scrolling and blend maps are fetched from the shared cache and can vary by room. Evict only
      // cold transient entries; persistent records explicitly pin their SRVs so this never leaves a dangling water,
      // static-light or map-art reference behind.
      const int highWater=128,target=96;const long idleFrames=180;
      if(textureCache.Count<=highWater)return;long cutoff=worldRenderFrame-idleFrames;
      foreach(string path in textureCache.Keys
        .Where(p=>!pinnedTexturePaths.Contains(p)&&!boundLocalLightTexturePaths.Contains(p))
        .OrderBy(p=>textureLastUseFrame.TryGetValue(p,out long frame)?frame:long.MinValue).ToList()){
        if(textureCache.Count<=target)break;
        long last=textureLastUseFrame.TryGetValue(path,out long frame)?frame:long.MinValue;
        if(last>cutoff||last>=worldRenderFrame-1)continue;
        if(textureCache.TryGetValue(path,out ShaderResourceView texture))texture?.Dispose();
        textureCache.Remove(path);textureLastUseFrame.Remove(path);
      }
    }
    private static int ClampDdsFirstMipLevel(Stream stream,int requested){
      if(requested<=0||stream==null||!stream.CanSeek)return 0;long old=stream.Position;try{if(stream.Length<32)return 0;byte[] h=new byte[32];stream.Position=0;int read=stream.Read(h,0,h.Length);if(read<h.Length||BitConverter.ToUInt32(h,0)!=0x20534444)return 0;uint count=BitConverter.ToUInt32(h,28);if(count==0)count=1;return Math.Min(requested,Math.Max(0,(int)count-1));}catch{return 0;}finally{try{stream.Position=old;}catch{}}
    }
    private static int TextureMipSkip(WorldTextureQuality quality){return quality==WorldTextureQuality.Low?2:quality==WorldTextureQuality.Medium?1:0;}

    private void EnsureTextureQualityResources(WorldRenderSettings current){
      int desired=TextureMipSkip(current?.TextureQuality??WorldTextureQuality.High);if(desired==appliedTextureMipSkip)return;ReloadWorldTextureResources(desired);
    }

    private void ReloadWorldTextureResources(int desiredMipSkip){
      bool rebuildMapArt=mapArtPrepared;
      foreach(var value in textureCache.Values)value?.Dispose();textureCache.Clear();textureLastUseFrame.Clear();pinnedTexturePaths.Clear();boundLocalLightTexturePaths.Clear();
      appliedTextureMipSkip=Math.Max(0,desiredMipSkip);
      foreach(GR2_Material material in WorldMaterialResidencySet().ToList()){
        // Preserve parsed MAT state and appearance overrides. EnsureTextureResources recreates all owned SRVs at the
        // new mip level from their resolved paths, including NPC complexion/facepaint textures, without reparsing the
        // base MAT and accidentally discarding the appearance-specific values.
        ReleaseOwnedMaterial(material);streamedMaterialResourcesPrepared.Remove(material);materialLastUseFrame.Remove(material);
      }
      foreach(var gpu in waterGpu.Values)gpu.Dispose();waterGpu.Clear();Release(ref defaultWaterNormal);Release(ref defaultWaterDepth);BuildWaterResources();
      foreach(var art in mapArtGpu)art.Dispose();mapArtGpu.Clear();mapArtPrepared=false;if(rebuildMapArt)BuildMapArt();
      BuildLocalLightIndex();localLightSelectionCache.Clear();dynamicSpnLocalLights.Clear();
      InvalidateTemporalHistory();InvalidateObjectOcclusionVisibility();
    }

    private void EnsureShadowQualityResources(WorldRenderSettings current){
      WorldShadowQuality desired=current?.ShadowQuality??WorldShadowQuality.Off;
      if(desired==appliedShadowQuality)return;
      appliedShadowQuality=desired;
      if(desired==WorldShadowQuality.High){shadowDistances[0]=1.5f;shadowDistances[1]=4.5f;shadowDistances[2]=25f;shadowDistances[3]=50f;}
      else {shadowDistances[0]=1.5f;shadowDistances[1]=4.5f;shadowDistances[2]=12.5f;shadowDistances[3]=25f;}

      ReleaseShadowMaps();
      if(desired==WorldShadowQuality.Off){InvalidateTemporalHistory();return;}

      int requestedResolution=desired==WorldShadowQuality.High?4096:2048;
      if(!TryCreateShadowMaps(requestedResolution)&&desired==WorldShadowQuality.High){
        System.Diagnostics.Debug.WriteLine("4096 sun-shadow allocation failed; falling back to 2048 while keeping the High cascade range.");
        TryCreateShadowMaps(2048);
      }
      InvalidateTemporalHistory();
    }

    private bool TryCreateShadowMaps(int resolution){
      ReleaseShadowMaps();
      try{
        for(int i=0;i<shadowMaps.Length;i++)shadowMaps[i]=new ShadowMap(Device,resolution,resolution);
        appliedShadowResolution=resolution;
        return true;
      }catch(Exception ex){
        System.Diagnostics.Debug.WriteLine("Sun-shadow allocation "+resolution+"x"+resolution+" failed: "+ex.Message);
        ReleaseShadowMaps();
        return false;
      }
    }

    private void ReleaseShadowMaps(){
      for(int i=0;i<shadowMaps.Length;i++){shadowMaps[i]?.Dispose();shadowMaps[i]=null;}
      appliedShadowResolution=0;
    }

    private static string NormalizeTexturePath(string raw){if(string.IsNullOrWhiteSpace(raw))return null;string p=raw.Trim().Replace('\\','/');if(p.EndsWith(".tex",StringComparison.OrdinalIgnoreCase))p=p.Substring(0,p.Length-4);if(!p.EndsWith(".dds",StringComparison.OrdinalIgnoreCase))p+=".dds";if(p.StartsWith("resources/",StringComparison.OrdinalIgnoreCase))p="/"+p;else if(!p.StartsWith("/resources/",StringComparison.OrdinalIgnoreCase))p="/resources/"+p.TrimStart('/');return p.ToLowerInvariant();}

    private void BuildRoads(){if(area==null)return;foreach(AreaPath p in area.Paths.Where(x=>x.IsMapRoad&&x.Points.Count>1)){var pts=new List<Vector3>();for(int i=0;i<p.Points.Count-1;i++){pts.Add(p.Points[i].Position);pts.Add(p.Points[i+1].Position);}if(p.Circular){pts.Add(p.Points[^1].Position);pts.Add(p.Points[0].Position);}roadGpu.Add(BuildLine(pts,p.Color));}}
    private void BuildMapNotes(){
      if(area==null)return;
      foreach(AreaMapNote n in area.MapNotes){
        // Known note classes are rendered as one batched GPU sprite group, so do not allocate the old per-note line
        // buffer for them. Large areas can contain thousands of notes and the dead buffers were pure VRAM/load-time cost.
        if(MapNoteIconKey(n?.Icon)!=null)continue;
        float r=.75f;var p=n.Position;
        mapNoteFallbackGpu.Add(BuildLine(new[]{p-new Vector3(r,0,0),p+new Vector3(r,0,0),p-new Vector3(0,0,r),p+new Vector3(0,0,r)},new Vector4(1,.75f,.1f,1)));
      }
      if(area.ArrivalPoint!=null){
        var p=area.ArrivalPoint.Position;float r=1.2f;
        mapNoteFallbackGpu.Add(BuildLine(new[]{p-new Vector3(r,0,0),p+new Vector3(r,0,0),p-new Vector3(0,0,r),p+new Vector3(0,0,r)},new Vector4(.2f,1,.2f,1)));
      }
    }

    private static string MapNoteIconKey(string icon){
      if(String.IsNullOrWhiteSpace(icon))return null;
      string value=icon.Trim().ToLowerInvariant();
      if(value.Contains("bind"))return "bindpoint";
      if(value.Contains("maplink")||value=="defaultmaplink")return "maplink";
      if(value.Contains("quest"))return "quest";
      if(value.Contains("taxi"))return "taxi";
      if(value.Contains("wonka")||value.Contains("elevator")||value.Contains("lift"))return "wonkavator";
      return null;
    }

    private static bool MapNoteCategoryEnabled(AreaMapNote note,WorldRenderSettings s){
      if(note==null||s==null||!s.ShowMapNotes)return false;
      string key=MapNoteIconKey(note.Icon);
      switch(key){
        case "bindpoint": return s.ShowMapIconBindpoints;
        case "maplink": return s.ShowMapIconMapLinks;
        case "quest": return s.ShowMapIconQuests;
        case "taxi": return s.ShowMapIconTaxi;
        case "wonkavator": return s.ShowMapIconWonkavator;
        default: return s.ShowMapIconOther;
      }
    }

    private static Size MapNoteIconPixelSize(string key){
      switch(key){
        case "maplink": return new Size(24,26);
        case "quest": return new Size(23,23);
        case "wonkavator": return new Size(24,34);
        default: return new Size(20,20);
      }
    }

    private static string MapNoteIconFileName(string key){
      if(String.IsNullOrWhiteSpace(key))return null;
      return String.Equals(key,"player-marker",StringComparison.OrdinalIgnoreCase)?"player-marker.png":"mpn-"+key+".png";
    }

    private MapNoteIconGpu EnsureMapNoteIconGpu(string key,int requiredVertices){
      if(String.IsNullOrWhiteSpace(key)||requiredVertices<=0||Device==null)return null;
      if(!mapNoteIconGpu.TryGetValue(key,out MapNoteIconGpu gpu)){
        string file=MapNoteIconFileName(key);
        string path=Path.Combine(AppContext.BaseDirectory,"Resources","WorldMapIcons",file??String.Empty);
        if(!File.Exists(path))return null;
        try{
          using Stream stream=File.OpenRead(path);
          gpu=new MapNoteIconGpu{Key=key,Texture=ShaderResourceView.FromStream(Device,stream,(int)stream.Length)};
          mapNoteIconGpu[key]=gpu;
        }catch(Exception ex){System.Diagnostics.Debug.WriteLine("Map-note icon load failed "+path+": "+ex.Message);return null;}
      }
      if(gpu.Texture==null)return null;
      if(gpu.Buffer==null||gpu.Capacity<requiredVertices){
        gpu.Buffer?.Dispose();gpu.Buffer=null;
        int capacity=96;while(capacity<requiredVertices&&capacity<65536)capacity*=2;if(capacity<requiredVertices)capacity=requiredVertices;
        var bd=new BufferDescription(PosNormalTexTan.Stride*capacity,ResourceUsage.Dynamic,BindFlags.VertexBuffer,CpuAccessFlags.Write,ResourceOptionFlags.None,0);
        gpu.Buffer=new Buffer(Device,bd){DebugName="World map note icons "+key};gpu.Capacity=capacity;
      }
      return gpu;
    }

    private void DrawMapNoteIcons(Matrix vp,WorldRenderSettings s){
      if(area?.MapNotes==null||area.MapNotes.Count==0||ClientWidth<=0||ClientHeight<=0||s==null||!s.ShowMapNotes)return;
      float worldPerPixelX=Math.Max(.00001f,mapVisibleWidth/Math.Max(1f,ClientWidth));
      float worldPerPixelZ=Math.Max(.00001f,mapVisibleHeight/Math.Max(1f,ClientHeight));
      // Cull note sprites before allocating/uploading their dynamic vertex arrays. Large worlds can contain thousands
      // of mapnotes but only a small fraction intersects the current M-map viewport.
      float marginX=40f*worldPerPixelX,marginZ=40f*worldPerPixelZ;
      float minX=mapCenter.X-mapVisibleWidth*.5f-marginX,maxX=mapCenter.X+mapVisibleWidth*.5f+marginX;
      float minZ=mapCenter.Y-mapVisibleHeight*.5f-marginZ,maxZ=mapCenter.Y+mapVisibleHeight*.5f+marginZ;
      var groups=area.MapNotes
        .Where(n=>n!=null&&MapNoteCategoryEnabled(n,s)&&n.Position.X>=minX&&n.Position.X<=maxX&&n.Position.Z>=minZ&&n.Position.Z<=maxZ)
        .Select(n=>(Note:n,Key:MapNoteIconKey(n.Icon)))
        .Where(x=>x.Key!=null)
        .GroupBy(x=>x.Key,StringComparer.OrdinalIgnoreCase);
      float y=boundsMax.Y+3.5f;
      var normal=new Vector3(0,1,0);var tangent=new Vector3(1,0,0);
      ImmediateContext.InputAssembler.PrimitiveTopology=PrimitiveTopology.TriangleList;
      fx.SetWorld(Matrix.Identity);fx.SetViewProj(vp);
      foreach(var group in groups){
        var notes=group.Select(x=>x.Note).ToList();if(notes.Count==0)continue;
        int vertexCount=checked(notes.Count*6);MapNoteIconGpu gpu=EnsureMapNoteIconGpu(group.Key,vertexCount);if(gpu?.Buffer==null||gpu.Texture==null)continue;
        Size px=MapNoteIconPixelSize(group.Key);float hx=px.Width*worldPerPixelX*.5f,hz=px.Height*worldPerPixelZ*.5f;
        var verts=new PosNormalTexTan[vertexCount];int o=0;
        foreach(AreaMapNote note in notes){
          Vector3 p=note.Position;p.Y=y;
          // SWTOR's icon bitmaps are authored opposite the top-down D3D quad winding used by this viewer. Apply the
          // missing half-turn globally, then layer the authored map-link yaw on top. This matches the WinForms minimap.
          float angle=(float)Math.PI;
          if(String.Equals(group.Key,"maplink",StringComparison.OrdinalIgnoreCase))angle-=note.Rotation.Y*(float)Math.PI/180f;
          float c=(float)Math.Cos(angle),sn=(float)Math.Sin(angle);
          Vector2 tl=RotateMapIconOffset(-hx,hz,c,sn),tr=RotateMapIconOffset(hx,hz,c,sn),br=RotateMapIconOffset(hx,-hz,c,sn),bl=RotateMapIconOffset(-hx,-hz,c,sn);
          verts[o++]=new PosNormalTexTan(new Vector3(p.X+tl.X,y,p.Z+tl.Y),normal,new Vector2(0,0),tangent);
          verts[o++]=new PosNormalTexTan(new Vector3(p.X+tr.X,y,p.Z+tr.Y),normal,new Vector2(1,0),tangent);
          verts[o++]=new PosNormalTexTan(new Vector3(p.X+br.X,y,p.Z+br.Y),normal,new Vector2(1,1),tangent);
          verts[o++]=new PosNormalTexTan(new Vector3(p.X+tl.X,y,p.Z+tl.Y),normal,new Vector2(0,0),tangent);
          verts[o++]=new PosNormalTexTan(new Vector3(p.X+br.X,y,p.Z+br.Y),normal,new Vector2(1,1),tangent);
          verts[o++]=new PosNormalTexTan(new Vector3(p.X+bl.X,y,p.Z+bl.Y),normal,new Vector2(0,1),tangent);
        }
        try{
          DataBox mapped=ImmediateContext.MapSubresource(gpu.Buffer,MapMode.WriteDiscard,SlimDX.Direct3D11.MapFlags.None);
          mapped.Data.WriteRange(verts);ImmediateContext.UnmapSubresource(gpu.Buffer,0);
          fx.SetMapArt(gpu.Texture,1f);ImmediateContext.InputAssembler.SetVertexBuffers(0,new VertexBufferBinding(gpu.Buffer,PosNormalTexTan.Stride,0));
          fx.MapArt.GetPassByIndex(0).Apply(ImmediateContext);ImmediateContext.Draw(vertexCount,0);
        }catch(Exception ex){System.Diagnostics.Debug.WriteLine("Map-note icon draw failed: "+ex.Message);}
      }
      fx.ClearMapArt();
    }

    private static Vector2 RotateMapIconOffset(float x,float z,float cosine,float sine){return new Vector2(x*cosine-z*sine,x*sine+z*cosine);}

    private void BuildMapArt(){
      if(mapArtPrepared)return;mapArtPrepared=true;
      if(area?.MapPages==null||area.MapPages.Count==0)return;
      var pages=area.MapPages.Where(p=>p.HasImage&&!string.IsNullOrWhiteSpace(p.ImagePath)).ToList();
      // Most SWTOR areas contain a root map plus zoomed child pages. Showing both at once causes
      // duplicate artwork, so prefer root pages and fall back to all pages where no root is authored.
      var roots=pages.Where(p=>p.ParentId==0).ToList();if(roots.Count>0)pages=roots;
      foreach(AreaMapPage page in pages){
        ShaderResourceView texture=LoadTexture(page.ImagePath,true);if(texture==null)continue;
        float minX=Math.Min(page.Min.X,page.Max.X),maxX=Math.Max(page.Min.X,page.Max.X);
        float minZ=Math.Min(page.Min.Z,page.Max.Z),maxZ=Math.Max(page.Min.Z,page.Max.Z);
        if(maxX-minX<.001f||maxZ-minZ<.001f)continue;
        // The top-down map camera uses -Z as screen-up. The texture UVs are therefore arranged
        // so north/up in the authored _r.dds page remains up on screen.
        float y=boundsMax.Y+2f;
        var n=new Vector3(0,1,0);var t=new Vector3(1,0,0);
        var v=new[]{
          new PosNormalTexTan(new Vector3(minX,y,maxZ),n,new Vector2(0,0),t),
          new PosNormalTexTan(new Vector3(maxX,y,maxZ),n,new Vector2(1,0),t),
          new PosNormalTexTan(new Vector3(maxX,y,minZ),n,new Vector2(1,1),t),
          new PosNormalTexTan(new Vector3(minX,y,maxZ),n,new Vector2(0,0),t),
          new PosNormalTexTan(new Vector3(maxX,y,minZ),n,new Vector2(1,1),t),
          new PosNormalTexTan(new Vector3(minX,y,minZ),n,new Vector2(0,1),t)
        };
        var bd=new BufferDescription(PosNormalTexTan.Stride*v.Length,ResourceUsage.Immutable,BindFlags.VertexBuffer,CpuAccessFlags.None,ResourceOptionFlags.None,0);using var ds=new DataStream(v,false,false);
        mapArtGpu.Add(new MapArtGpu{Buffer=new Buffer(Device,ds,bd),Count=v.Length,Texture=texture,Name=page.MapName});
      }
    }
    private LineGpu BuildLine(IEnumerable<Vector3> points,Vector4 color){var positions=points.ToArray();var verts=positions.Select(p=>new PosNormalTexTan(p,new Vector3(0,1,0),Vector2.Zero,new Vector3(1,0,0))).ToArray();Vector3 min=new Vector3(float.MaxValue,float.MaxValue,float.MaxValue),max=new Vector3(float.MinValue,float.MinValue,float.MinValue);foreach(Vector3 p in positions)Expand(ref min,ref max,p);Vector3 center=positions.Length>0?(min+max)*.5f:Vector3.Zero;float radius=positions.Length>0?(max-min).Length()*.5f:0f;var bd=new BufferDescription(PosNormalTexTan.Stride*verts.Length,ResourceUsage.Immutable,BindFlags.VertexBuffer,CpuAccessFlags.None,ResourceOptionFlags.None,0);using var ds=new DataStream(verts,false,false);return new LineGpu{Buffer=new Buffer(Device,ds,bd),Count=verts.Length,Color=color,Center=center,Radius=radius};}

    private void CalculateBounds(){Vector3 min=new Vector3(float.MaxValue,float.MaxValue,float.MaxValue),max=new Vector3(float.MinValue,float.MinValue,float.MinValue);bool any=false;
      foreach(Room r in rooms){if(r==null||skyRoomNames.Contains(r.RoomName))continue;if(IsFinite(r.VisibilityMin)&&IsFinite(r.VisibilityMax)){Expand(ref min,ref max,r.VisibilityMin);Expand(ref min,ref max,r.VisibilityMax);any=true;}foreach(AssetInstance i in r.InstancesById.Values){if(i.PathFollowerBoundaryExcluded)continue;Matrix m=InstanceWorld(i,r);Vector3 p=new Vector3(m.M41,m.M42,m.M43);Expand(ref min,ref max,p);any=true;if(i.HeightMap!=null){Expand(ref min,ref max,p+new Vector3(-i.HeightMap.width*.1f,i.HeightMap.MinElevation,-i.HeightMap.depth*.1f));Expand(ref min,ref max,p+new Vector3(i.HeightMap.width*.1f,i.HeightMap.MaxElevation,i.HeightMap.depth*.1f));}}}if(area!=null){foreach(var p in area.Paths)foreach(var q in p.Points){Expand(ref min,ref max,q.Position);any=true;}foreach(var page in area.MapPages){Expand(ref min,ref max,page.Min);Expand(ref min,ref max,page.Max);any=true;}}if(any){boundsMin=min;boundsMax=max;}}
    private static bool IsFinite(Vector3 v)=>!float.IsNaN(v.X)&&!float.IsInfinity(v.X)&&!float.IsNaN(v.Y)&&!float.IsInfinity(v.Y)&&!float.IsNaN(v.Z)&&!float.IsInfinity(v.Z);
    private static void Expand(ref Vector3 min,ref Vector3 max,Vector3 p){min.X=Math.Min(min.X,p.X);min.Y=Math.Min(min.Y,p.Y);min.Z=Math.Min(min.Z,p.Z);max.X=Math.Max(max.X,p.X);max.Y=Math.Max(max.Y,p.Y);max.Z=Math.Max(max.Z,p.Z);}
    private MapBoundsOverride CurrentMapBoundsOverride(){
      if(area==null)return null;MapBoundsOverrides.TryGetValue(area.Id.ToString(),out MapBoundsOverride value);return value;
    }

    private string MapAssetFileName(AssetInstance inst,GR2 model){
      string path=null;
      if(area!=null&&inst!=null&&area.AssetIdMap.TryGetValue(inst.assetID,out AreaAsset asset)&&asset!=null){
        path=asset.Path;string ext=(asset.Extension??String.Empty).Trim().TrimStart('.');
        if(!String.IsNullOrWhiteSpace(ext)&&!String.IsNullOrWhiteSpace(path)&&!path.EndsWith("."+ext,StringComparison.OrdinalIgnoreCase))path+="."+ext;
      }
      if(String.IsNullOrWhiteSpace(path))path=model?.filename;
      string normalized=NormalizeAssetPath(path);int slash=normalized.LastIndexOf('/');return slash>=0?normalized.Substring(slash+1):normalized;
    }

    private bool MapOverrideHidesAsset(MapBoundsOverride mapOverride,AssetInstance inst,GR2 model){
      if(mapOverride==null||mapOverride.HideAssets.Count==0)return false;string name=MapAssetFileName(inst,model);
      if(String.IsNullOrWhiteSpace(name))return false;
      foreach(string hidden in mapOverride.HideAssets)if(name.IndexOf(hidden,StringComparison.OrdinalIgnoreCase)>=0)return true;
      return false;
    }

    private static float MapModelMaxDimension(GR2 model,Matrix world){
      GR2_Bounding_Box box=model?.globalBox;if(box==null)return 0f;
      Vector3 min=new Vector3(box.minX,box.minY,box.minZ),max=new Vector3(box.maxX,box.maxY,box.maxZ);if(!IsFinite(min)||!IsFinite(max))return 0f;
      Vector3 size=max-min;return Math.Max(size.X,Math.Max(size.Y,size.Z))*MatrixMaxScale(world);
    }

    private bool MapModelVisibleForCurrentMap(AssetInstance inst,GR2 model,Matrix world){
      if(TryModelSphere(model,world,out Vector3 center,out float radius)&&!MapSphereVisible(center,radius))return false;
      if(mapShowEntireArea||!mapHasClusterExtent)return true;MapBoundsOverride mapOverride=CurrentMapBoundsOverride();if(mapOverride==null)return true;
      if(MapOverrideHidesAsset(mapOverride,inst,model))return false;
      if(mapOverride.HideLargeObjects&&MapModelMaxDimension(model,world)>mapOverride.LargeObjectSize)return false;
      return true;
    }

    private bool ApplyMeasuredMapBoundsOverride(MapBoundsOverride mapOverride,bool hasAutomaticCrop,float fullMinX,float fullMaxX,float fullMinZ,float fullMaxZ,
      ref float minX,ref float maxX,ref float minZ,ref float maxZ){
      if(mapOverride==null||!mapOverride.HasMeasuredBounds)return hasAutomaticCrop;
      float beforeMinX=minX,beforeMaxX=maxX,beforeMinZ=minZ,beforeMaxZ=maxZ;
      float ClampX(float value)=>Math.Max(fullMinX,Math.Min(fullMaxX,value));
      float ClampZ(float value)=>Math.Max(fullMinZ,Math.Min(fullMaxZ,value));
      if(mapOverride.MinX.HasValue)minX=ClampX(mapOverride.MinX.Value);if(mapOverride.MaxX.HasValue)maxX=ClampX(mapOverride.MaxX.Value);
      if(mapOverride.MinZ.HasValue)minZ=ClampZ(mapOverride.MinZ.Value);if(mapOverride.MaxZ.HasValue)maxZ=ClampZ(mapOverride.MaxZ.Value);
      if(maxX<=minX||maxZ<=minZ){minX=beforeMinX;maxX=beforeMaxX;minZ=beforeMinZ;maxZ=beforeMaxZ;return hasAutomaticCrop;}
      bool changed=Math.Abs(minX-beforeMinX)>.0001f||Math.Abs(maxX-beforeMaxX)>.0001f||Math.Abs(minZ-beforeMinZ)>.0001f||Math.Abs(maxZ-beforeMaxZ)>.0001f;
      return hasAutomaticCrop||changed;
    }

    private void ResetMapCamera(){
      float minX=boundsMin.X,maxX=boundsMax.X,minZ=boundsMin.Z,maxZ=boundsMax.Z;
      bool havePrimaryExtent=false;
      // Start with the real heightmap footprint, then union authored map-page/path extents. Some SWTOR areas have
      // roads, water or map pages extending beyond the HMS tiles; v8.2 only used HMS and could therefore create a
      // map that was already clipped at its maximum zoom-out level.
      if(heightMapFloors.Count>0){
        minX=heightMapFloors.Min(x=>x.WorldMinX);maxX=heightMapFloors.Max(x=>x.WorldMaxX);
        minZ=heightMapFloors.Min(x=>x.WorldMinZ);maxZ=heightMapFloors.Max(x=>x.WorldMaxZ);havePrimaryExtent=true;
      }
      void IncludeXZ(float x,float z){
        if(!float.IsFinite(x)||!float.IsFinite(z))return;
        if(!havePrimaryExtent){minX=maxX=x;minZ=maxZ=z;havePrimaryExtent=true;return;}
        minX=Math.Min(minX,x);maxX=Math.Max(maxX,x);minZ=Math.Min(minZ,z);maxZ=Math.Max(maxZ,z);
      }
      if(area!=null){
        foreach(var page in area.MapPages){IncludeXZ(page.Min.X,page.Min.Z);IncludeXZ(page.Max.X,page.Max.Z);}
        foreach(var path in area.Paths)foreach(var point in path.Points)IncludeXZ(point.Position.X,point.Position.Z);
        foreach(var note in area.MapNotes)IncludeXZ(note.Position.X,note.Position.Z);
        if(area.ArrivalPoint!=null)IncludeXZ(area.ArrivalPoint.Position.X,area.ArrivalPoint.Position.Z);
      }
      // Include normal-sized world renderables as well. Several phase/space maps contain architecture or water
      // outside the HMS footprint. Ignore absurd helper/malformed spheres so one broken bound cannot shrink the
      // useful map to a dot. Skyscene rooms never enter the render index, so their giant domes do not affect fit.
      var uniqueRenderEntries=new HashSet<RenderEntry>();
      foreach(RenderEntry entry in renderGlobal)if(entry!=null)uniqueRenderEntries.Add(entry);
      foreach(var bucket in renderGrid.Values)foreach(RenderEntry entry in bucket)if(entry!=null)uniqueRenderEntries.Add(entry);
      foreach(RenderEntry entry in uniqueRenderEntries){
        if(entry==null||!IsFinite(entry.Center)||entry.Radius<0f||entry.Radius>4096f)continue;
        IncludeXZ(entry.Center.X-entry.Radius,entry.Center.Z-entry.Radius);IncludeXZ(entry.Center.X+entry.Radius,entry.Center.Z+entry.Radius);
      }
      if(!havePrimaryExtent){minX=boundsMin.X;maxX=boundsMax.X;minZ=boundsMin.Z;maxZ=boundsMax.Z;}
      if(maxX-minX<1f){minX-=5f;maxX+=5f;}if(maxZ-minZ<1f){minZ-=5f;maxZ+=5f;}

      mapFullExtentMinX=minX;mapFullExtentMaxX=maxX;mapFullExtentMinZ=minZ;mapFullExtentMaxZ=maxZ;
      MapBoundsOverride mapOverride=CurrentMapBoundsOverride();
      bool automaticCrop=TryBuildMapClusterExtent(uniqueRenderEntries,minX,maxX,minZ,maxZ,mapOverride,
        out mapClusterExtentMinX,out mapClusterExtentMaxX,out mapClusterExtentMinZ,out mapClusterExtentMaxZ);
      mapHasClusterExtent=ApplyMeasuredMapBoundsOverride(mapOverride,automaticCrop,minX,maxX,minZ,maxZ,
        ref mapClusterExtentMinX,ref mapClusterExtentMaxX,ref mapClusterExtentMinZ,ref mapClusterExtentMaxZ);
      ApplyMapExtentSelection();
      mapCenter=new Vector2((mapExtentMinX+mapExtentMaxX)*.5f,(mapExtentMinZ+mapExtentMaxZ)*.5f);
      mapZoom=1f;
      UpdateMapCamera();
    }

    private static float MapBoundsFootprint(float minX,float maxX,float minZ,float maxZ){
      return Math.Max(0f,maxX-minX)*Math.Max(0f,maxZ-minZ);
    }

    private static void MapBoundsOfBoxes(IList<MapBoundsBox> boxes,out float minX,out float maxX,out float minZ,out float maxZ){
      minX=minZ=float.MaxValue;maxX=maxZ=float.MinValue;
      foreach(MapBoundsBox box in boxes){
        if(box==null)continue;
        minX=Math.Min(minX,box.MinX);maxX=Math.Max(maxX,box.MaxX);
        minZ=Math.Min(minZ,box.MinZ);maxZ=Math.Max(maxZ,box.MaxZ);
      }
    }

    private static List<MapBoundsBox> MapTrimWidestGap(List<MapBoundsBox> boxes,int minKeep){
      int removable=boxes.Count-minKeep;if(removable<1)return null;
      float bestGap=-1f;List<MapBoundsBox> best=null;
      foreach(bool xAxis in new[]{true,false}){
        List<MapBoundsBox> sorted=boxes.OrderBy(box=>xAxis?box.X:box.Z).ToList();
        int last=sorted.Count-1;
        float span=(xAxis?sorted[last].X:sorted[last].Z)-(xAxis?sorted[0].X:sorted[0].Z);
        float minGap=span*MapClusterMinGap;if(!(minGap>0f))continue;
        for(int count=1;count<=removable;count++){
          float lowGap=(xAxis?sorted[count].X:sorted[count].Z)-(xAxis?sorted[count-1].X:sorted[count-1].Z);
          if(lowGap>=minGap&&lowGap>bestGap){bestGap=lowGap;best=sorted.Skip(count).ToList();}
          float highGap=(xAxis?sorted[last-count+1].X:sorted[last-count+1].Z)-(xAxis?sorted[last-count].X:sorted[last-count].Z);
          if(highGap>=minGap&&highGap>bestGap){bestGap=highGap;best=sorted.Take(last-count+1).ToList();}
        }
      }
      return best;
    }

    private bool TryBuildMapClusterExtent(IEnumerable<RenderEntry> renderEntries,float fullMinX,float fullMaxX,float fullMinZ,float fullMaxZ,MapBoundsOverride mapOverride,
      out float minX,out float maxX,out float minZ,out float maxZ){
      minX=fullMinX;maxX=fullMaxX;minZ=fullMinZ;maxZ=fullMaxZ;
      var boxes=new List<MapBoundsBox>();

      // Heightmaps are the ground, not scenery. Keep their tiles in the clustering input so open-world planets form
      // one continuous distribution and cannot be "helpfully" cropped down to a city or prop cluster.
      foreach(HeightMapFloorEntry entry in heightMapFloors){
        if(entry==null)continue;
        boxes.Add(new MapBoundsBox{MinX=entry.WorldMinX,MaxX=entry.WorldMaxX,MinZ=entry.WorldMinZ,MaxZ=entry.WorldMaxZ,
          X=(entry.WorldMinX+entry.WorldMaxX)*.5f,Z=(entry.WorldMinZ+entry.WorldMaxZ)*.5f,Large=false});
      }
      bool hiddenAssetsSteerBounds=mapOverride==null||!mapOverride.HasMeasuredBounds;
      float largeObjectSize=mapOverride?.LargeObjectSize??MapClusterLargeObjectSize;
      foreach(RenderEntry entry in renderEntries){
        if(entry==null||!IsFinite(entry.Center)||entry.Radius<0f||entry.Radius>4096f)continue;
        if(entry.Kind==RenderKindModel&&entry.Model!=null&&MapOverrideHidesAsset(mapOverride,entry.Instance,entry.Model)&&!hiddenAssetsSteerBounds)continue;
        float minBoxX,maxBoxX,minBoxZ,maxBoxZ;bool large=false;
        if(entry.Kind==RenderKindModel&&entry.Model!=null&&TryModelWorldXZBounds(entry.Model,entry.World,out minBoxX,out maxBoxX,out minBoxZ,out maxBoxZ)){
          large=MapModelMaxDimension(entry.Model,entry.World)>largeObjectSize;
        }else{
          float r=Math.Max(.001f,entry.Radius);minBoxX=entry.Center.X-r;maxBoxX=entry.Center.X+r;minBoxZ=entry.Center.Z-r;maxBoxZ=entry.Center.Z+r;
        }
        boxes.Add(new MapBoundsBox{MinX=minBoxX,MaxX=maxBoxX,MinZ=minBoxZ,MaxZ=maxBoxZ,
          X=(minBoxX+maxBoxX)*.5f,Z=(minBoxZ+maxBoxZ)*.5f,Large=large});
      }
      if(boxes.Count<8)return false;

      List<MapBoundsBox> kept=boxes;
      List<MapBoundsBox> small=boxes.Where(box=>!box.Large).ToList();
      if(small.Count>=MapClusterMinSmallCount&&small.Count>=boxes.Count*MapClusterMinSmallShare)kept=small;
      int minKeep=(int)Math.Ceiling(kept.Count*MapClusterCoverage);
      bool changed=!ReferenceEquals(kept,boxes);
      for(int pass=0;pass<MapClusterMaxPasses;pass++){
        List<MapBoundsBox> trimmed=MapTrimWidestGap(kept,minKeep);
        if(trimmed==null)break;
        kept=trimmed;changed=true;
      }
      if(!changed||kept.Count==0)return false;

      MapBoundsOfBoxes(kept,out float cropMinX,out float cropMaxX,out float cropMinZ,out float cropMaxZ);
      if(!float.IsFinite(cropMinX)||!float.IsFinite(cropMaxX)||!float.IsFinite(cropMinZ)||!float.IsFinite(cropMaxZ)||
         cropMaxX<=cropMinX||cropMaxZ<=cropMinZ)return false;
      float fullFootprint=MapBoundsFootprint(fullMinX,fullMaxX,fullMinZ,fullMaxZ);
      float cropFootprint=MapBoundsFootprint(cropMinX,cropMaxX,cropMinZ,cropMaxZ);
      if(fullFootprint<=0f||cropFootprint>fullFootprint*MapClusterMaxFootprint)return false;

      minX=Math.Max(fullMinX,cropMinX);maxX=Math.Min(fullMaxX,cropMaxX);
      minZ=Math.Max(fullMinZ,cropMinZ);maxZ=Math.Min(fullMaxZ,cropMaxZ);
      return maxX-minX>=1f&&maxZ-minZ>=1f;
    }

    private void ApplyMapExtentSelection(){
      bool full=mapShowEntireArea||!mapHasClusterExtent;
      mapExtentMinX=full?mapFullExtentMinX:mapClusterExtentMinX;
      mapExtentMaxX=full?mapFullExtentMaxX:mapClusterExtentMaxX;
      mapExtentMinZ=full?mapFullExtentMinZ:mapClusterExtentMinZ;
      mapExtentMaxZ=full?mapFullExtentMaxZ:mapClusterExtentMaxZ;
    }

    public bool MapHasSmartCrop=>mapHasClusterExtent;
    public bool MapShowEntireArea=>mapShowEntireArea;

    public void SetMapShowEntireArea(bool showEntireArea){
      mapShowEntireArea=showEntireArea;
      if(mapOpen){
        ApplyMapExtentSelection();
        mapCenter=new Vector2((mapExtentMinX+mapExtentMaxX)*.5f,(mapExtentMinZ+mapExtentMaxZ)*.5f);
        mapZoom=1f;UpdateMapCamera();
        if(Window is WorldBrowser browser)browser.SetStatusLabel(showEntireArea||!mapHasClusterExtent
          ?"Map extent: entire area"
          :"Map extent: main area (isolated outliers cropped)");
      }
      miniMapCaptureRequested=true;
    }

    private void UpdateMapCamera(){
      float aspect=Math.Max(.1f,ClientWidth/(float)Math.Max(1,ClientHeight));
      // Recalculate the fit height every update. This makes 1x an actual "fit whole map" zoom even after the
      // World Browser, splitter or toolbar changes the render-panel aspect ratio while M is open.
      float worldWidth=Math.Max(10f,(mapExtentMaxX-mapExtentMinX)*1.08f);
      float worldHeight=Math.Max(10f,(mapExtentMaxZ-mapExtentMinZ)*1.08f);
      mapBaseHeight=Math.Max(worldHeight,worldWidth/aspect);
      mapVisibleHeight=Math.Max(2f,mapBaseHeight*Math.Max(MapMinZoom,Math.Min(MapMaxZoom,mapZoom)));
      mapVisibleWidth=Math.Max(2f,mapVisibleHeight*aspect);
      float extentW=Math.Max(.0001f,mapExtentMaxX-mapExtentMinX),extentH=Math.Max(.0001f,mapExtentMaxZ-mapExtentMinZ);
      if(mapVisibleWidth>=extentW)mapCenter.X=(mapExtentMinX+mapExtentMaxX)*.5f;
      else mapCenter.X=Math.Max(mapExtentMinX+mapVisibleWidth*.5f,Math.Min(mapExtentMaxX-mapVisibleWidth*.5f,mapCenter.X));
      if(mapVisibleHeight>=extentH)mapCenter.Y=(mapExtentMinZ+mapExtentMaxZ)*.5f;
      else mapCenter.Y=Math.Max(mapExtentMinZ+mapVisibleHeight*.5f,Math.Min(mapExtentMaxZ-mapVisibleHeight*.5f,mapCenter.Y));
      float y=boundsMax.Y+Math.Max(mapVisibleWidth,mapVisibleHeight)+100f;
      mapCameraPosition=new Vector3(mapCenter.X,y,mapCenter.Y);
      float targetY=(boundsMin.Y+boundsMax.Y)*.5f;
      mapView=Matrix.LookAtRH(mapCameraPosition,new Vector3(mapCenter.X,targetY,mapCenter.Y),new Vector3(0,0,-1));
      mapProj=Matrix.OrthoRH(mapVisibleWidth,mapVisibleHeight,.1f,Math.Max(100f,y-boundsMin.Y+200f));
    }

    private void SetMapOpen(bool open){
      if(mapOpen==open){if(!open){CloseTaxiRouteMapState();CloseQuickTravelMapState();}return;}
      mapOpen=open;mapPointerDown=false;mapPointerDragged=false;
      if(open)ResetMapCamera();else {CloseTaxiRouteMapState();CloseQuickTravelMapState();}
      InvalidateTemporalHistory();
      if(Window is WorldBrowser browser){
        browser.SetFullMapActive(open);
        browser.SetStatusLabel(open
          ? (IsTaxiRouteMapActive ? "Taxi map: click a highlighted route/destination, drag = pan, wheel = zoom, M/Esc = close"
            : IsQuickTravelMapActive ? "Quick travel: click a bindpoint destination, drag = pan, wheel = zoom, M/Esc = close"
            : "Map: click = teleport, drag = pan, wheel = zoom, move mouse = coordinates, M/Esc = close")
          : "Map closed. Camera speed: "+cameraSpeed.ToString("0.##")+" u/s");
      }
    }

    private Vector2 MapWorldAtScreen(Point p){
      float width=Math.Max(1,ClientWidth),height=Math.Max(1,ClientHeight);
      float nx=p.X/width*2f-1f;
      float nz=p.Y/height*2f-1f; // screen down is world +Z in the top-down map
      return new Vector2(mapCenter.X+nx*mapVisibleWidth*.5f,mapCenter.Y+nz*mapVisibleHeight*.5f);
    }

    private AreaMapNote HitTestFullMapNote(Point point) {
      if (area?.MapNotes == null || area.MapNotes.Count == 0 || ClientWidth <= 0 || ClientHeight <= 0) return null;
      AreaMapNote best = null; float bestD2 = float.MaxValue;
      float left = mapCenter.X - mapVisibleWidth * .5f, top = mapCenter.Y - mapVisibleHeight * .5f;
      WorldRenderSettings s = SettingsSnapshot();
      foreach (AreaMapNote note in area.MapNotes) {
        if (!MapNoteCategoryEnabled(note, s)) continue;
        string key = MapNoteIconKey(note.Icon); if (key == null) continue;
        Size px = MapNoteIconPixelSize(key);
        float x = (note.Position.X - left) / Math.Max(.0001f, mapVisibleWidth) * ClientWidth;
        float y = (note.Position.Z - top) / Math.Max(.0001f, mapVisibleHeight) * ClientHeight;
        float hx = Math.Max(8f, px.Width * .6f), hy = Math.Max(8f, px.Height * .6f);
        if (Math.Abs(point.X - x) > hx || Math.Abs(point.Y - y) > hy) continue;
        float dx = point.X - x, dy = point.Y - y, d2 = dx * dx + dy * dy;
        if (d2 < bestD2) { bestD2 = d2; best = note; }
      }
      return best;
    }

    private void ZoomMapAt(Point p,int delta){
      if(delta==0)return;
      Vector2 before=MapWorldAtScreen(p);
      float factor=(float)Math.Exp(-delta*0.0015f);
      mapZoom=Math.Max(MapMinZoom,Math.Min(MapMaxZoom,mapZoom*factor));
      UpdateMapCamera();
      Vector2 after=MapWorldAtScreen(p);
      mapCenter+=before-after;
      UpdateMapCamera();
    }

    private bool TrySampleHeightMapEntry(HeightMapFloorEntry entry,float worldX,float worldZ,out float worldY){
      worldY=0;HeightMap hm=entry?.HeightMap;if(hm==null||hm.width<2||hm.depth<2)return false;
      float probeY=boundsMax.Y+20f;Vector3 floorWorld=Vector3.Zero;
      for(int iteration=0;iteration<3;iteration++){
        Vector3 local=Vector3.TransformCoordinate(new Vector3(worldX,probeY,worldZ),entry.Inverse);
        int w=(int)hm.width,d=(int)hm.depth;float gx=(local.X-entry.XMin)/.2f,gz=(local.Z-entry.ZMin)/.2f;
        int x=(int)Math.Floor(gx),z=(int)Math.Floor(gz);
        if(x<0||z<0||x>=w-1||z>=d-1)return false;
        if(hm.hasHoles&&!hm.CheckNoHole(x,z))return false;
        float tx=gx-x,tz=gz-z;float h00=hm.elevation[z,x],h10=hm.elevation[z,x+1],h01=hm.elevation[z+1,x],h11=hm.elevation[z+1,x+1];
        float localY=tx+tz<=1f?h00+(h10-h00)*tx+(h01-h00)*tz:h11+(h01-h11)*(1f-tx)+(h10-h11)*(1f-tz);
        floorWorld=Vector3.TransformCoordinate(new Vector3(local.X,localY,local.Z),entry.World);
        probeY=floorWorld.Y;
      }
      worldY=floorWorld.Y;return !float.IsNaN(worldY)&&!float.IsInfinity(worldY);
    }

    private bool TrySampleMapHeight(float worldX,float worldZ,out float worldY){
      worldY=float.MinValue;
      if(!heightMapFloorGrid.TryGetValue((FloorCell(worldX),FloorCell(worldZ)),out List<HeightMapFloorEntry> entries))return false;
      bool found=false;
      foreach(HeightMapFloorEntry entry in entries){
        if(!TrySampleHeightMapEntry(entry,worldX,worldZ,out float y))continue;
        if(!found||y>worldY){worldY=y;found=true;}
      }
      return found;
    }

    private void TeleportFromMap(Point p){
      Vector2 world=MapWorldAtScreen(p);
      float y;
      if(!TrySampleMapHeight(world.X,world.Y,out y))y=Math.Max(boundsMin.Y+2f,Math.Min(boundsMax.Y+2f,camera.Position.Y));
      camera.Position=new Vector3(world.X,y+MapTeleportClearance,world.Y);
      currentCameraRoom=null;
      if(Window is WorldBrowser browser){
        browser.SetStatusLabel(string.Format(System.Globalization.CultureInfo.InvariantCulture,"Teleported to {0:0}, {1:0}, {2:0}",world.X*10f,world.Y*10f,y*10f));
        browser.SetFullMapActive(false);
      }
      mapOpen=false;mapPointerDown=false;mapPointerDragged=false;InvalidateTemporalHistory();
    }

    public void MakeScreenshot(ImageFileFormat format){try{string filename=Tools.PrepExtractPath(fqn+'-'+DateTime.Now.ToString("yyyyMMddHHmmss")+'.'+format.ToString().ToLower());var d=new Texture2DDescription{Width=ClientWidth,Height=ClientHeight,MipLevels=1,ArraySize=1,Format=Format.R8G8B8A8_UNorm,SampleDescription=new SampleDescription(1,0),Usage=ResourceUsage.Default,BindFlags=BindFlags.None,CpuAccessFlags=CpuAccessFlags.None};using var output=new Texture2D(Device,d);using var back=SlimDX.Direct3D11.Resource.FromSwapChain<Texture2D>(SwapChain,0);ImmediateContext.CopyResource(back,output);Texture2D.ToFile(ImmediateContext,output,format,filename);((WorldBrowser)Window).SetStatusLabel("Screenshot Completed: "+filename);}catch(Exception ex){System.Diagnostics.Debug.WriteLine(ex);}}

    protected override void OnMouseDown(object sender,MouseEventArgs e){
      lastMousePos=e.Location;Control control=Window.Controls.Find(RenderPanelName,true).FirstOrDefault();if(control!=null)control.Capture=true;
      if(mapOpen&&e.Button==MouseButtons.Left){mapPointerDown=true;mapPointerDragged=false;mapPointerStart=e.Location;mapPanStartCenter=mapCenter;return;}
      if(!mapOpen&&orthographicActive&&e.Button==MouseButtons.Right){orthographicPanning=true;orthographicPanStart=e.Location;orthographicPanCameraStart=camera.Position;}
    }
    protected override void OnMouseUp(object sender,MouseEventArgs e){
      Control control=Window.Controls.Find(RenderPanelName,true).FirstOrDefault();if(control!=null)control.Capture=false;
      if(e.Button==MouseButtons.Right)orthographicPanning=false;
      if(mapOpen&&e.Button==MouseButtons.Left&&mapPointerDown){
        bool click=!mapPointerDragged;mapPointerDown=false;
        if(click){
          if(IsTaxiRouteMapActive){
            if(TryPickTaxiRouteOnMap(e.Location,out WorldTaxiRouteInfo pickedRoute) && Window is WorldBrowser taxiBrowser){
              CloseTaxiRouteMapState();mapOpen=false;mapPointerDragged=false;InvalidateTemporalHistory();taxiBrowser.SetFullMapActive(false);taxiBrowser.StartTaxiRouteFromMap(pickedRoute);
            } else if(Window is WorldBrowser taxiMapBrowser) taxiMapBrowser.SetStatusLabel("Taxi map: click a highlighted route or destination marker; drag to pan, wheel to zoom, Esc/M to close.");
          } else if(IsQuickTravelMapActive){
            WorldQuickTravelTerminal terminal=QuickTravelTerminalAtMapPoint(e.Location);
            if(terminal!=null)TeleportQuickTravel(terminal);
            else if(Window is WorldBrowser quickMapBrowser)quickMapBrowser.SetStatusLabel("Quick travel: click a bindpoint destination; drag to pan, wheel to zoom, Esc/M to close.");
          } else TeleportFromMap(e.Location);
        }
      }
    }
    protected override void OnMouseMove(object sender,MouseEventArgs e){
      if(mapOpen){
        if(mapPointerDown&&(e.Button&MouseButtons.Left)!=0){
          int dx=e.X-mapPointerStart.X,dy=e.Y-mapPointerStart.Y;
          if(!mapPointerDragged&&dx*dx+dy*dy>=16)mapPointerDragged=true;
          if(mapPointerDragged){
            if(Window is WorldBrowser dragBrowser)dragBrowser.UpdateWorldMapNoteToolTip(null,Point.Empty);
            float wx=dx/(float)Math.Max(1,ClientWidth)*mapVisibleWidth;
            float wz=dy/(float)Math.Max(1,ClientHeight)*mapVisibleHeight;
            mapCenter=new Vector2(mapPanStartCenter.X-wx,mapPanStartCenter.Y-wz);UpdateMapCamera();
          }
        }
        if(!mapPointerDragged&&Window is WorldBrowser mapBrowser){
          AreaMapNote hoverNote = !IsTaxiRouteMapActive&&!IsQuickTravelMapActive ? HitTestFullMapNote(e.Location) : null;
          mapBrowser.UpdateWorldMapNoteToolTip(hoverNote, e.Location);
          if(IsTaxiRouteMapActive && TryPickTaxiRouteOnMap(e.Location,out WorldTaxiRouteInfo hoverRoute))
            mapBrowser.SetStatusLabel("Taxi destination: "+TaxiRouteMapDisplayName(hoverRoute)+"  •  click to ride");
          else if(IsTaxiRouteMapActive)
            mapBrowser.SetStatusLabel("Taxi map: click a highlighted route/destination; drag = pan, wheel = zoom, Esc/M = close");
          else if(IsQuickTravelMapActive){
            WorldQuickTravelTerminal hoverTerminal=QuickTravelTerminalAtMapPoint(e.Location);
            mapBrowser.SetStatusLabel(hoverTerminal!=null
              ? "Quick travel destination: "+hoverTerminal.Label+"  •  click to travel"
              : "Quick travel: click a bindpoint destination; drag = pan, wheel = zoom, Esc/M = close");
          }
          else {
            Vector2 world=MapWorldAtScreen(e.Location);
            string heightText=TrySampleMapHeight(world.X,world.Y,out float mapY)
              ? ", "+Math.Round(mapY*10f).ToString(System.Globalization.CultureInfo.InvariantCulture)
              : String.Empty;
            // Match Jedipedia's map readout: display X/Y are world X/Z ×10, followed by the sampled surface Z.
            mapBrowser.SetStatusLabel("Map cursor: "+Math.Round(world.X*10f).ToString(System.Globalization.CultureInfo.InvariantCulture)+
              ", "+Math.Round(world.Y*10f).ToString(System.Globalization.CultureInfo.InvariantCulture)+heightText+
              (mapHasClusterExtent&&!mapShowEntireArea?"  •  cropped main area":""));
          }
        }
        lastMousePos=e.Location;return;
      }
      bool selectionGesture=(Control.ModifierKeys&Keys.Control)!=0;
      const float pointerLookSensitivity=.0025f; // Jedipedia mouse/pointer sensitivity (radians per pixel).
      if(orthographicActive){
        if(orthographicPanning&&(e.Button&MouseButtons.Right)!=0){
          int dx=e.X-orthographicPanStart.X,dy=e.Y-orthographicPanStart.Y;WorldRenderSettings s=SettingsSnapshot();
          float worldPerPixel=2f*GetOrthographicHalfHeight(s)/Math.Max(1f,ClientHeight);
          Vector3 right=HorizontalUnit(camera.Right,Vector3.UnitX);
          float across=-dx*worldPerPixel;float along=dy*worldPerPixel/Math.Max((float)Math.Sin(orthographicPitch),.1f);
          Vector3 pan=orthographicPanCameraStart+right*across+orthographicHeading*along;camera.Position=new Vector3(pan.X,camera.Position.Y,pan.Z);lastMousePos=e.Location;return;
        }
        if(!selectionGesture&&(e.Button&MouseButtons.Left)!=0){
          float pitchDelta=pointerLookSensitivity*(e.Y-lastMousePos.Y);
          float yawDelta=-pointerLookSensitivity*(e.X-lastMousePos.X);
          orthographicPitch=Math.Max(OrthographicMinPitch,Math.Min(OrthographicMaxPitch,orthographicPitch+pitchDelta));
          if(Math.Abs(yawDelta)>.000001f)orthographicHeading=HorizontalUnit(Vector3.TransformNormal(orthographicHeading,Matrix.RotationY(yawDelta)),Vector3.UnitZ);
          ApplyOrthographicOrientation();
        }
        lastMousePos=e.Location;return;
      }
      bool rotate=((e.Button&MouseButtons.Right)!=0)||(!selectionGesture&&(e.Button&MouseButtons.Left)!=0);
      if(rotate){float dy=pointerLookSensitivity*(e.Y-lastMousePos.Y);float dx=-pointerLookSensitivity*(e.X-lastMousePos.X);PitchPerspectiveCamera(-dy);camera.Yaw(dx);}
      lastMousePos=e.Location;
    }
    public void HandleMouseWheel(System.Drawing.Point location,int delta){
      if(mapOpen){ZoomMapAt(location,delta);return;}
      if(HandleSpaceFlypathMouseWheel(delta))return;
      if(HandleTaxiMouseWheel(delta))return;
      if(orthographicActive){
        orthographicZoomTarget=Math.Max(OrthographicZoomMin,Math.Min(OrthographicZoomMax,orthographicZoomTarget*(float)Math.Exp(-delta*OrthographicZoomSensitivity)));
        orthographicFloorY=TrySampleViewerFloor(camera.Position.X,camera.Position.Z,camera.Position.Y+.05f,out float floorY)?floorY:float.NaN;
        if(Window is WorldBrowser orthoBrowser)orthoBrowser.SetStatusLabel("Orthographic zoom: "+(100f/orthographicZoomTarget).ToString("0")+"%  •  wheel controls cut height");
        return;
      }
      cameraSpeed=(float)Math.Max(MinCameraSpeed,Math.Min(MaxCameraSpeed,cameraSpeed*Math.Exp(delta*0.001f)));
      if(Window is WorldBrowser browser){bool walking=SettingsSnapshot().WalkingMode;browser.SetStatusLabel((walking?"Walking speed: ":"Camera speed: ")+cameraSpeed.ToString("0.##")+" u/s (Shift "+(walking?"2x":"10x")+")");}
    }

    protected override void OnMouseWheel(object sender,MouseEventArgs e){
      HandleMouseWheel(e.Location,e.Delta);
    }
  }
}
