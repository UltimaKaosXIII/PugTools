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
      public readonly Dictionary<string, ShaderResourceView> Masks = new Dictionary<string, ShaderResourceView>(StringComparer.OrdinalIgnoreCase);
      public void Dispose() { foreach (var v in Masks.Values) v?.Dispose(); Masks.Clear(); ColorMap?.Dispose(); ColorMap=null; if(OwnsLodIndexBuffer)LodIndexBuffer?.Dispose(); LodIndexBuffer=null; LodRanges=null; }
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
      public void Dispose(){if(OwnsDepthMap)DepthMap?.Dispose();Normal1=null;Normal2=null;DepthMap=null;SurfaceMap=null;}
    }
    private sealed class MapArtGpu : IDisposable {
      public Buffer Buffer; public int Count; public ShaderResourceView Texture; public string Name;
      public void Dispose(){Buffer?.Dispose();Buffer=null;Texture=null;}
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
      public bool DoWater;
      public Vector4 PosRange;
      public Vector4 ColorIntensity;
      public Vector4 DirType;
      public Matrix ProjectorInv;
      public Vector4 ProjectorParams;
      public ShaderResourceView IlluminationMap;
      public ShaderResourceView FalloffMap;
      public ShaderResourceView RampMap;
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
      public Matrix Inverse;
      public float Width;
      public float Depth;
      public float Height;
      public bool HasHeight;
      public int Rank;
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
      public Vector3 LightSample;
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

    private struct LocalLightSelection {
      public int Count;
      public LocalLightEntry E0;
      public LocalLightEntry E1;
      public LocalLightEntry E2;
      public LocalLightEntry E3;

      public LocalLightEntry Get(int index) {
        return index == 0 ? E0 : index == 1 ? E1 : index == 2 ? E2 : index == 3 ? E3 : null;
      }

      public static LocalLightSelection From(LocalLightEntry[] entries, int count) {
        return new LocalLightSelection {
          Count = count,
          E0 = count > 0 ? entries[0] : null,
          E1 = count > 1 ? entries[1] : null,
          E2 = count > 2 ? entries[2] : null,
          E3 = count > 3 ? entries[3] : null
        };
      }
    }

    private string fqn;
    private Area area;
    private Dictionary<ulong, GR2> models = new Dictionary<ulong, GR2>();
    private Dictionary<string, GR2_Material> materials = new Dictionary<string, GR2_Material>();
    private List<Room> rooms = new List<Room>();
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
    private readonly List<LineGpu> roadGpu = new List<LineGpu>();
    private readonly List<LineGpu> noteGpu = new List<LineGpu>();
    private readonly List<MapArtGpu> mapArtGpu = new List<MapArtGpu>();
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
    private Buffer regularModelInstanceBuffer;
    private int regularModelInstanceCapacity;
    private float[] regularModelInstanceScratch = Array.Empty<float>();
    private readonly Dictionary<GR2,bool> regularModelInstancingSafe = new Dictionary<GR2,bool>();
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
    private const float LocalLightGridRangeLimit = 256f;
    private readonly Dictionary<(int X,int Z),List<LocalLightEntry>> localLightGrid = new Dictionary<(int X,int Z),List<LocalLightEntry>>();
    private readonly List<LocalLightEntry> localLightGlobal = new List<LocalLightEntry>();
    // Cache one four-light choice per spatial cell/receiver kind for the current frame. Large SWTOR rooms
    // can contain thousands of GR2 placements in the same few cells; recomputing the same nearest-light
    // query for every placement was still enough to starve D3D11 even after introducing the light grid.
    private readonly Dictionary<(int X,int Y,int Z,string Room,byte Kind),LocalLightSelection> localLightSelectionCache = new Dictionary<(int X,int Y,int Z,string Room,byte Kind),LocalLightSelection>();
    private readonly LocalLightEntry[] localLightBest = new LocalLightEntry[4];
    private readonly float[] localLightBestDistance = new float[4];
    private readonly LocalLightEntry[] lastLocalLightSelection = new LocalLightEntry[4];
    private readonly Vector4[] localLightPosRangeScratch = new Vector4[4];
    private readonly Vector4[] localLightColorScratch = new Vector4[4];
    private readonly Vector4[] localLightDirScratch = new Vector4[4];
    private readonly Matrix[] localLightProjectorScratch = new Matrix[4];
    private readonly Vector4[] localLightProjectorParamsScratch = new Vector4[4];
    private readonly ShaderResourceView[] localLightIlluminationScratch = new ShaderResourceView[4];
    private readonly ShaderResourceView[] localLightFalloffScratch = new ShaderResourceView[4];
    private readonly ShaderResourceView[] localLightRampScratch = new ShaderResourceView[4];
    private int lastLocalLightCount = -1;
    private string localLightVisibilityScope;
    private Room currentCameraRoom;
    private Room displayCameraRoom;
    private readonly HashSet<string> skyRoomNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private readonly object settingsLock = new object();
    private WorldRenderSettings settings = new WorldRenderSettings();
    private WorldEffect fx;
    private InputLayout inputLayout;
    private InputLayout instancedLayout;
    private InputLayout dynamicDetailLayout;
    private readonly FpsCamera camera = new FpsCamera();
    private Point lastMousePos;
    private float cameraSpeed=1f;
    private const float MinCameraSpeed=.05f;
    private const float MaxCameraSpeed=1000f;
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
    private float mapVisibleWidth=20f;
    private float mapVisibleHeight=20f;
    private Vector3 mapCameraPosition;
    private const float MapMinZoom=.025f;
    private const float MapMaxZoom=1f;
    private const float MapTeleportClearance=.5f;
    private bool makeScreenshot;
    public bool _disposed;
    public List<string> ignoreList = new List<string>{"collision","dbo","fadeportal","occluder"};
    private readonly ShadowMap[] shadowMaps = new ShadowMap[4];
    private readonly Matrix[] shadowMatrices = new Matrix[4];
    private readonly float[] shadowDistances = {1.5f,4.5f,12.5f,25f};
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
      var instancedSignature=fx.InstancedLit.GetPassByIndex(0).Description.Signature;
      instancedLayout=new InputLayout(Device,instancedSignature,InputLayoutDescriptions.InstancedPosNormalTexTan);
      var dydSignature=fx.DynamicDetail.GetPassByIndex(0).Description.Signature;
      dynamicDetailLayout=new InputLayout(Device,dydSignature,InputLayoutDescriptions.DynamicDetail);
      for(int i=0;i<4;i++)shadowMaps[i]=new ShadowMap(Device,1024,1024);
      CreateSceneTarget();
      InitializeFeatureTextRenderer();
      return true;
    }

    public void LoadModel(Dictionary<ulong,GR2> models, Dictionary<string,GR2_Material> materials, List<Room> rooms, string fqn, Area area=null, Dictionary<string,GR2> utilityModels=null, List<WorldNpcPlacement> npcData=null, List<WorldSpnPlacement> spnData=null) {
      this.fqn=fqn; this.area=area; this.models=models??new Dictionary<ulong,GR2>(); this.materials=materials??new Dictionary<string,GR2_Material>(); this.rooms=rooms??new List<Room>();
      SetJedipediaFeatureData(utilityModels,npcData,spnData);
      elapsed=0f;
      placeableCameraAssetId=0; currentCameraRoom=null; displayCameraRoom=null; skyRoomNames.Clear(); regularModelInstancingSafe.Clear(); mapOpen=false; mapPointerDown=false;
      if(Window is WorldBrowser worldBrowser)worldBrowser.SetFullMapActive(false);
      if(area!=null){
        AreaAsset camAsset=area.AssetIdMap.Values.FirstOrDefault(a=>string.Equals(a.Extension,"cam",StringComparison.OrdinalIgnoreCase)&&NormalizeAssetPath(a.Path)=="engine/placeablecamera");if(camAsset!=null)placeableCameraAssetId=camAsset.Id;
      }
      // Jedipedia's window.skyscenes is authoritative: every room actually resolved from an environment scheme's
      // skyscene_room plus the area's numeric default skydome. Do not infer a backdrop from a room name, a camera,
      // or a Skydome material: ordinary Dantooine rooms contain those too and would disappear from the world/map.
      BuildAuthoritativeSkyRoomSet();
      BuildInstanceWorldTransformCache();
      BuildPathFollowers();
      UpdatePathFollowers(elapsed);
      PrimeRoomVisibilityBounds();
      BuildDynamicDetailMeshModels();
      LoadStrongholdHookModels();
      LoadAndApplyLodSchemas();
      foreach(var m in this.materials.Values) if(!m.parsed) m.ParseMAT(Device);
      BuildModelGeometry(); BuildJedipediaOverlayResources(); BuildDecorationHookRenderEntries(); BuildEmbeddedGeometry(); BuildTerrainResources(); BuildWaterResources(); BuildHeightMapFloorIndex(); BuildRoomPlacementIndex(); BuildModelFloorIndex(); BuildRenderSpatialIndex(); BuildLocalLightIndex(); BuildRoads(); BuildMapNotes(); CalculateBounds(); BuildMapArt(); InvalidateTemporalHistory();
      camera.Reset();
      WorldRenderSettings initialSettings=SettingsSnapshot();
      activeClipDistance=GetActiveClipDistance(area?.GetEnvironmentScheme("area"),initialSettings);
      appliedCameraFar=GetCameraFarDistance(activeClipDistance,initialSettings);
      camera.SetLens(.25f*SlimDXNet.MathF.PI,AspectRatio,.01f,appliedCameraFar);
      if(area?.ArrivalPoint!=null){Vector3 p=area.ArrivalPoint.Position+new Vector3(0,2,0);camera.LookAt(p,p+new Vector3(0,0,1),new Vector3(0,1,0));}
      else {Vector3 center=(boundsMin+boundsMax)*.5f; float r=Math.Max(10,(boundsMax-boundsMin).Length()*.35f); camera.LookAt(center+new Vector3(0,r*.35f,r),center,new Vector3(0,1,0));}
      ResetMapCamera();
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
        foreach(RenderEntry entry in NearbyRenderEntries(RenderKindModel,camera.FarZ,false)){
          Room room=entry.Room;AssetInstance inst=entry.Instance;GR2 model=entry.Model;
          if(room==null||inst==null||model==null||!model.enabled||skyRoomNames.Contains(room.RoomName)||!InstanceRoomVisible(inst,room,visible)||!InstanceVisibleInWorld(inst))continue;
          if(ShouldCullModelByLod(model,entry.World,false,inst.LodFactor))continue;
          if(TryRayHitModel(model,entry.World,inst.LodFactor,near,direction,ref bestDistance,ref bestPoint,ref bestModel,ref bestMesh,ref bestPiece))bestEntry=entry;
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

    private bool TryRayHitModel(GR2 model,Matrix world,float lodFactor,Vector3 rayOrigin,Vector3 rayDirection,ref float bestDistance,ref Vector3 bestPoint,ref GR2 bestModel,ref GR2_Mesh bestMesh,ref GR2_Mesh_Piece bestPiece){
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
          if(piece==null||IsMaterialHiddenFromWorld(ResolvePieceMaterial(model,piece)))continue;
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
      foreach(GR2 attached in model.attachedModels)if(TryRayHitModel(attached,world,lodFactor,rayOrigin,rayDirection,ref bestDistance,ref bestPoint,ref bestModel,ref bestMesh,ref bestPiece))hitAny=true;
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
      if(Window is WorldBrowser browser)browser.SetStatusLabel(string.Format(System.Globalization.CultureInfo.InvariantCulture,"Teleported to {0:0.00}, {1:0.00}, {2:0.00}",camera.Position.X,camera.Position.Y,camera.Position.Z));
    }

    public void GetMapPose(out float x,out float z,out float lookX,out float lookZ){
      Vector3 pos=camera.Position,look=camera.Look;
      x=pos.X;z=pos.Z;lookX=look.X;lookZ=look.Z;
    }

    public void Clear(){ReleaseWorldGpu(); SetImplicitPhaseName(String.Empty); pathFollowers.Clear();instanceWorldTransforms.Clear(); models.Clear();materials.Clear();rooms.Clear();area=null;}
    private void ReleaseWorldGpu(){
      fx?.ClearWater();
      ReleaseJedipediaFeatureGpu();
      foreach(var g in terrainGpu.Values)g.Dispose();terrainGpu.Clear();foreach(var g in terrainIndexCache.Values)g.Dispose();terrainIndexCache.Clear(); foreach(var g in waterGpu.Values)g.Dispose();waterGpu.Clear(); foreach(var g in roadGpu)g.Dispose();roadGpu.Clear();foreach(var g in noteGpu)g.Dispose();noteGpu.Clear();foreach(var g in mapArtGpu)g.Dispose();mapArtGpu.Clear();
      foreach(var list in dynamicDetailGpu.Values)foreach(var g in list)g.Dispose();dynamicDetailGpu.Clear();
      foreach(var list in dynamicDetailMeshBatches.Values)foreach(var g in list)g.Dispose();dynamicDetailMeshBatches.Clear();
      instanceWorldTransforms.Clear();heightMapFloorGrid.Clear();heightMapFloors.Clear();roomPlacementGrid.Clear();roomPlacementGlobal.Clear();modelFloorData.Clear();modelFloorPlacementGrid.Clear();modelFloorPlacementGlobal.Clear();renderGrid.Clear();renderGlobal.Clear();decorationHookRenderEntries.Clear();Release(ref regularModelInstanceBuffer);regularModelInstanceCapacity=0;regularModelInstanceScratch=Array.Empty<float>();regularModelInstancingSafe.Clear();visualLodLevels.Clear();lodSchemas.Clear();localLightGrid.Clear();localLightGlobal.Clear();localLightSelectionCache.Clear();localLightVisibilityScope=null;Array.Clear(lastLocalLightSelection,0,lastLocalLightSelection.Length);lastLocalLightCount=-1;currentCameraRoom=null;displayCameraRoom=null;skyRoomNames.Clear();
      foreach(var m in dynamicDetailMaterials.Values)ReleaseOwnedMaterial(m);dynamicDetailMaterials.Clear();
      var releasedDydModels=new HashSet<GR2>();foreach(var model in dynamicDetailMeshModels.Values)if(model!=null&&releasedDydModels.Add(model))ReleaseModelBuffers(model);dynamicDetailMeshModels.Clear();
      foreach(var model in strongholdHookModels.Values)if(model!=null&&releasedDydModels.Add(model))ReleaseModelBuffers(model);strongholdHookModels.Clear();
      Release(ref defaultWaterNormal);Release(ref defaultWaterDepth);
      foreach(var v in textureCache.Values)v?.Dispose();textureCache.Clear();
      foreach(var m in terrainMaterials.Values){Release(ref m.diffuseSRV);Release(ref m.diffuse2SRV);Release(ref m.rotationSRV);Release(ref m.glossSRV);Release(ref m.waterSurfaceSRV);}terrainMaterials.Clear();
      foreach(var room in rooms)foreach(var inst in room.InstancesById.Values){Release(ref inst.VBO);Release(ref inst.IBO);}
      foreach(var model in models.Values)ReleaseModelBuffers(model);
    }
    private static void ReleaseModelBuffers(GR2 model){foreach(var mesh in model.meshes){Release(ref mesh.vertBuffer);Release(ref mesh.idxBuffer);}foreach(var a in model.attachedModels)ReleaseModelBuffers(a);}
    private static void Release<T>(ref T v) where T:class,IDisposable{v?.Dispose();v=null;}

    protected override void Dispose(bool disposing){
      if(!_disposed){if(disposing){ReleaseWorldGpu();DisposeFeatureTextRenderer();ReleasePostTargets();Release(ref sceneDepthShaderResource);for(int i=0;i<shadowMaps.Length;i++)shadowMaps[i]?.Dispose();dynamicDetailLayout?.Dispose();instancedLayout?.Dispose();inputLayout?.Dispose();fx?.Dispose();RenderStates.DestroyAll();}_disposed=true;}base.Dispose(disposing);
    }

    public override void OnResize(){
      base.OnResize();
      CreateSampleableDepthTarget();
      CreateSceneTarget();
      float cameraFar=GetCameraFarDistance(activeClipDistance,SettingsSnapshot());
      camera.SetLens(.25f*SlimDXNet.MathF.PI,AspectRatio,.01f,cameraFar);
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

      if(Util.IsKeyDown(Keys.R)){Vector3 c=(boundsMin+boundsMax)*.5f;camera.LookAt(c+new Vector3(0,5,20),c,new Vector3(0,1,0));}
      if(UpdateWalkingMode(dt))return;
      bool shift=Util.IsKeyDown(Keys.LShiftKey)||Util.IsKeyDown(Keys.RShiftKey);
      bool alt=Util.IsKeyDown(Keys.LMenu)||Util.IsKeyDown(Keys.RMenu);
      float multiplier=shift?(alt?100f:10f):1f;
      float speed=cameraSpeed*multiplier;
      // Keep PugTools' camera-space sign convention, but use the familiar WASD layout requested for the browser.
      if(Util.IsKeyDown(Keys.W))camera.Walk(-speed*dt);
      if(Util.IsKeyDown(Keys.S))camera.Walk(speed*dt);
      if(Util.IsKeyDown(Keys.A))camera.Strafe(-speed*dt);
      if(Util.IsKeyDown(Keys.D))camera.Strafe(speed*dt);
    }

    public override void DrawScene(){
      base.DrawScene(); if(fx==null)return; if(temporalHistoryResetRequested){InvalidateTemporalHistory();temporalHistoryResetRequested=false;} WorldRenderSettings s=SettingsSnapshot();
      // /engine/follower.fol is an invisible moving parent used heavily by vista traffic. Update it before camera-room
      // lookup and visibility collection so children never render at their authored origin (often 0,0,0).
      UpdatePathFollowers(elapsed);
      bool captureMiniMap=miniMapCaptureRequested&&!mapOpen;
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
        s.ShowTerrain=true;
        s.ShowModels=true;
        s.ShowWater=true;
        s.ShowRoads=true;
        s.ShowMapNotes=false;
        // Jedipedia's M map is a render of the actual terrain/world layout. Authored 2D map art remains
        // available through the explicit toolbar Map mode, but does not cover the interactive M map.
        s.ShowMapArt=false;
      }
      Room cameraRoom=FindCameraRoom(camera.Position);
      UpdateCurrentPhase(camera.Position);
      // Jedipedia does not let the synthetic `_everywhere_` cell choose a room environment/skyscene. When the
      // camera has no concrete visibility room it explicitly falls back to envSchemes.area. Using `_everywhere_`'s
      // scheme here was enough to replace Dantooine's authored sky with the plain fog/clear colour.
      AreaEnvironmentScheme env=cameraRoom!=null&&!IsEverywhereRoom(cameraRoom)
        ? cameraRoom.EnvironmentScheme??area?.GetEnvironmentScheme("area")??new AreaEnvironmentScheme()
        : area?.GetEnvironmentScheme("area")??new AreaEnvironmentScheme();
      UpdateActiveClipDistance(env,s);
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

      bool shadows=s.EnableShadows&&s.Mode!=WorldRenderMode.Map&&s.Mode!=WorldRenderMode.Heightmap&&env.CastDirectionalShadows;
      HashSet<string> visible=BuildVisibleRoomSet(cameraRoom,s);
      // Local lights and receiver meshes are static. Keep the cell selections across frames and invalidate only
      // when the active room/visibility mode changes; this removes the remaining per-frame CPU selection cost.
      string lightScope=visible==null?"*":((cameraRoom?.RoomName??String.Empty)+(s.ShowSky?"|sky":"|nosky"));
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
      fx.SetViewProj(viewProj);fx.SetCamera(cam);fx.SetEnvironment(env,s.EnableLighting,s.EnableFog,shadows,s.ViewDistanceScale);fx.SetHeightRange(boundsMin.Y,boundsMax.Y);
      ShaderResourceView illum=LoadTexture(env.IlluminationMap);fx.SetIllumination(illum);fx.SetScrolling(env,elapsed,LoadTexture(env.ScrollingTexture),LoadTexture(env.ScrollingMask));
      var maps=shadowMaps.Select(x=>x.DepthMapSRV).ToArray();fx.SetShadows(shadowMatrices,maps,shadowDistances,shadows);fx.ClearLocalLights();lastLocalLightCount=0;Array.Clear(lastLocalLightSelection,0,lastLocalLightSelection.Length);
      // Jedipedia renders the skyscene as a backdrop and clears depth before the actual world. Drawing it
      // mixed into the model pass lets sky geometry fight with terrain/models and is responsible for many
      // floating/half-screen artefacts when a skyscene happens to intersect the world depth buffer.
      if(s.ShowSky&&s.Mode!=WorldRenderMode.Map&&s.Mode!=WorldRenderMode.Heightmap){
        DrawSky(env,s);
        ImmediateContext.ClearDepthStencilView(DepthStencilView,DepthStencilClearFlags.Depth|DepthStencilClearFlags.Stencil,1,0);
      }
      if(s.ShowTerrain)DrawTerrain(viewProj,visible,s,env,shadows);if(s.ShowDynamicDetails&&s.Mode!=WorldRenderMode.Map&&s.Mode!=WorldRenderMode.Heightmap)DrawDynamicDetails(viewProj,visible,s,env,shadows);if(s.ShowModels)DrawModels(viewProj,visible,s,env,shadows);if(s.ShowNpcs&&s.Mode!=WorldRenderMode.Heightmap)DrawJedipediaNpcs(viewProj,visible,s);if(s.ShowSpnObjects&&s.Mode!=WorldRenderMode.Heightmap)DrawJedipediaSpnObjects(viewProj,visible,s);if(s.ShowDecorationHooks&&s.Mode!=WorldRenderMode.Map&&s.Mode!=WorldRenderMode.Heightmap)DrawDecorationHooks(viewProj,visible,s,env,shadows);if(s.ShowWater)DrawWater(viewProj,visible,s);
      if(s.Mode==WorldRenderMode.Map&&s.ShowMapArt)DrawMapArt(viewProj);
      if(s.ShowRoads)DrawLines(roadGpu,viewProj,s.Mode==WorldRenderMode.Map?float.MaxValue:camera.FarZ);if(s.ShowMapNotes)DrawLines(noteGpu,viewProj,s.Mode==WorldRenderMode.Map?float.MaxValue:camera.FarZ);
      if(s.Mode!=WorldRenderMode.Heightmap)DrawJedipediaUtilities(viewProj,visible,s);

      if(useOffscreen)ResolvePostProcessing(s,env,useTaa,useFxaa);
      if(s.Mode!=WorldRenderMode.Heightmap)DrawNpcNameplates(viewProj,visible,s);
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

    private Matrix InstanceWorld(AssetInstance inst,Room room){
      if(inst!=null&&instanceWorldTransforms.TryGetValue(inst,out Matrix world))return world;
      return inst?.GetAbsoluteTransform(room)??Matrix.Identity;
    }

    private bool IsPathFollowerAsset(AssetInstance inst){
      if(inst==null||area==null||!area.AssetIdMap.TryGetValue(inst.assetID,out AreaAsset asset))return false;
      return string.Equals((asset.Extension??string.Empty).Trim().TrimStart('.'),"fol",StringComparison.OrdinalIgnoreCase)&&
        string.Equals(NormalizeAssetPath(asset.Path),"engine/follower",StringComparison.OrdinalIgnoreCase);
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

    private static bool InstanceVisibleInWorld(AssetInstance inst){
      if(inst==null||inst.hidden||inst.PathFollowerPending)return false;
      return inst.Viewability!=AssetInstanceViewability.MapOnly&&inst.Viewability!=AssetInstanceViewability.OccluderOnly;
    }

    private static bool InstanceVisibleOnMap(AssetInstance inst){
      if(inst==null||inst.hidden||inst.PathFollowerPending)return false;
      return inst.Viewability!=AssetInstanceViewability.WorldOnly&&inst.Viewability!=AssetInstanceViewability.OccluderOnly;
    }

    private void PrimeRoomVisibilityBounds(){
      // Jedipedia's dpvsPrimeRenderableRoomBounds() repairs rooms whose authored visibility box is the common
      // all-zero placeholder. Without this fallback a perfectly valid interior can never win FindBoundsRoom(), so
      // the camera appears to remain in the exterior room (or `_everywhere_`) whenever its collision floor has a gap.
      foreach(Room room in rooms){
        if(room==null||IsEverywhereRoom(room)||skyRoomNames.Contains(room.RoomName)||HasUsableRoomBounds(room))continue;
        Vector3 min=new Vector3(float.MaxValue,float.MaxValue,float.MaxValue),max=new Vector3(float.MinValue,float.MinValue,float.MinValue);bool any=false;
        foreach(AssetInstance inst in room.InstancesById.Values){
          if(!InstanceVisibleInWorld(inst)||inst.PathFollowerBoundaryExcluded)continue;Vector3 localMin,localMax;bool have=false;
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

    private void BuildHeightMapFloorIndex(){
      heightMapFloorGrid.Clear();heightMapFloors.Clear();
      foreach(Room room in rooms){
        if(room==null||IsEverywhereRoom(room)||skyRoomNames.Contains(room.RoomName))continue;
        foreach(AssetInstance inst in room.InstancesById.Values){
          HeightMap hm=inst.HeightMap;if(!InstanceVisibleInWorld(inst)||inst.PathFollowerBoundaryExcluded||!inst.hasHeightMap||hm==null||hm.width<2||hm.depth<2)continue;
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
          if(!InstanceVisibleInWorld(inst)||inst.PathFollowerBoundaryExcluded||!area.AssetIdMap.TryGetValue(inst.assetID,out AreaAsset asset))continue;
          string ext=(asset.Extension??String.Empty).Trim().TrimStart('.').ToLowerInvariant();int rank;
          // Jedipedia keeps Heightmap placements in addition to the exact floor lattice. A heightmap placement is
          // an infinite vertical X/Z prism when Height was not authored, which is why flying high above a planet
          // still has a real camera room instead of `_everywhere_`.
          if(inst.hasHeightMap)rank=0;else if(ext=="rbd")rank=1;else if(ext=="rgn")rank=2;else if(ext=="trg")rank=3;else continue;
          float width=Math.Abs(inst.width),depth=Math.Abs(inst.depth),height=Math.Abs(inst.height);bool hasHeight=inst.HasHeightProperty&&height>.0001f;
          if(!(width>.0001f)||!(depth>.0001f))continue;
          Matrix world=InstanceWorld(inst,room),inverse;try{inverse=Matrix.Invert(world);}catch{continue;}
          var entry=new RoomPlacementEntry{Room=room,Inverse=inverse,Width=width,Depth=depth,Height=height,HasHeight=hasHeight,Rank=rank};
          float halfW=width*.5f,halfD=depth*.5f,halfH=hasHeight?height*.5f:0f;
          // With no authored height the volume is an infinite local-Y prism. If local Y rotates into world X/Z,
          // there is no finite 2D index box; keep this rare placement in the global fallback exactly rather than
          // risking a false negative. Upright heightmaps/volumes can still use the cheap grid.
          if(!hasHeight&&(Math.Abs(world.M21)>.000001f||Math.Abs(world.M23)>.000001f)){roomPlacementGlobal.Add(entry);continue;}
          Vector3 min=new Vector3(float.MaxValue,float.MaxValue,float.MaxValue),max=new Vector3(float.MinValue,float.MinValue,float.MinValue);
          for(int z=0;z<2;z++)for(int y=0;y<2;y++)for(int x=0;x<2;x++){
            Vector3 c=Vector3.TransformCoordinate(new Vector3(x==0?-halfW:halfW,y==0?-halfH:halfH,z==0?-halfD:halfD),world);Expand(ref min,ref max,c);
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
    private static int RoomPlacementCell(float coordinate)=>(int)Math.Floor(coordinate/RoomPlacementCellSize);

    private static bool IsEverywhereRoom(Room room)=>room!=null&&string.Equals(room.RoomName,"_everywhere_",StringComparison.OrdinalIgnoreCase);

    private void BuildModelFloorIndex(){
      modelFloorData.Clear();modelFloorPlacementGrid.Clear();modelFloorPlacementGlobal.Clear();
      foreach(Room room in rooms){
        if(room==null||IsEverywhereRoom(room)||skyRoomNames.Contains(room.RoomName))continue;
        foreach(AssetInstance inst in room.InstancesById.Values){
          if(!InstanceVisibleInWorld(inst)||inst.PathFollowerBoundaryExcluded||inst.hasHeightMap||inst.hasWater||!models.TryGetValue(inst.assetID,out GR2 model)||model==null)continue;
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
      return mesh.lod==-3||mesh.lod==-2||name=="portal"||path.Contains("arch/fadeportal/");
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

    private static bool TryTriangleYAtXZ(Vector3 a,Vector3 b,Vector3 c,float x,float z,out float y){
      y=0f;if(x<Math.Min(a.X,Math.Min(b.X,c.X))-.0001f||x>Math.Max(a.X,Math.Max(b.X,c.X))+.0001f||z<Math.Min(a.Z,Math.Min(b.Z,c.Z))-.0001f||z>Math.Max(a.Z,Math.Max(b.Z,c.Z))+.0001f)return false;
      float denominator=(b.Z-c.Z)*(a.X-c.X)+(c.X-b.X)*(a.Z-c.Z);if(Math.Abs(denominator)<.0001f)return false;float wa=((b.Z-c.Z)*(x-c.X)+(c.X-b.X)*(z-c.Z))/denominator;float wb=((c.Z-a.Z)*(x-c.X)+(a.X-c.X)*(z-c.Z))/denominator;float wc=1f-wa-wb;if(wa<-.0001f||wb<-.0001f||wc<-.0001f)return false;y=wa*a.Y+wb*b.Y+wc*c.Y;return true;
    }

    private Room FindPlacementRoom(Vector3 p){
      RoomPlacementEntry best=null;float bestDistance=float.MaxValue;
      void Consider(IEnumerable<RoomPlacementEntry> entries){
        if(entries==null)return;
        foreach(RoomPlacementEntry entry in entries){
          Vector3 local=Vector3.TransformCoordinate(p,entry.Inverse);float halfW=entry.Width*.5f,halfD=entry.Depth*.5f;
          if(Math.Abs(local.X)>halfW+.0001f||Math.Abs(local.Z)>halfD+.0001f)continue;
          if(entry.HasHeight&&Math.Abs(local.Y)>entry.Height*.5f+8f)continue;
          float distance=(float)Math.Sqrt(local.X*local.X/Math.Max(halfW*halfW,1f)+local.Z*local.Z/Math.Max(halfD*halfD,1f));
          if(best==null||entry.Rank<best.Rank||(entry.Rank==best.Rank&&(distance<bestDistance-.0001f||(Math.Abs(distance-bestDistance)<.0001f&&String.Compare(entry.Room.RoomName,best.Room.RoomName,StringComparison.OrdinalIgnoreCase)<0)))){
            best=entry;bestDistance=distance;
          }
        }
      }
      roomPlacementGrid.TryGetValue((RoomPlacementCell(p.X),RoomPlacementCell(p.Z)),out List<RoomPlacementEntry> bucket);Consider(bucket);Consider(roomPlacementGlobal);
      return best?.Room;
    }

    private void BuildRenderSpatialIndex(){
      renderGrid.Clear();renderGlobal.Clear();
      foreach(Room room in rooms){
        if(room==null||skyRoomNames.Contains(room.RoomName))continue;
        foreach(AssetInstance inst in room.InstancesById.Values){
          if(!InstanceVisibleInWorld(inst))continue;Matrix world=InstanceWorld(inst,room);RenderEntry entry=null;
          if(inst.hasHeightMap&&TryTerrainSphere(inst,room,out Vector3 tc,out float tr))entry=new RenderEntry{Room=room,Instance=inst,World=world,Center=tc,Radius=tr,Kind=RenderKindTerrain};
          else if(inst.hasWater){
            Vector3 wc=Vector3.TransformCoordinate(Vector3.Zero,world);float wr=.5f*(float)Math.Sqrt(Math.Max(0f,inst.width*inst.width+inst.height*inst.height+inst.depth*inst.depth))*MatrixMaxScale(world);
            entry=new RenderEntry{Room=room,Instance=inst,World=world,Center=wc,Radius=wr,Kind=RenderKindWater};
          } else if(models.TryGetValue(inst.assetID,out GR2 model)&&model!=null&&model.enabled){
            Vector3 mc;float mr;if(!TryModelSphere(model,world,out mc,out mr)){mc=new Vector3(world.M41,world.M42,world.M43);mr=0f;}
            entry=new RenderEntry{Room=room,Instance=inst,Model=model,World=world,Center=mc,Radius=mr,Kind=RenderKindModel};
          }
          if(entry==null||!IsFinite(entry.Center))continue;
          // Moving path-follower descendants cannot live in a fixed grid cell. Jedipedia keeps this traffic dynamic;
          // put the handful of animated entries in the global list and refresh their world sphere every frame.
          if(inst.PathFollowerAnimated){renderGlobal.Add(entry);continue;}
          if(entry.Radius>RenderIndexedRadiusLimit){renderGlobal.Add(entry);continue;}
          var key=(RenderCell(entry.Center.X),RenderCell(entry.Center.Z));if(!renderGrid.TryGetValue(key,out List<RenderEntry> bucket))renderGrid[key]=bucket=new List<RenderEntry>();bucket.Add(entry);
        }
      }
    }
    private static int RenderCell(float coordinate)=>(int)Math.Floor(coordinate/RenderCellSize);
    private IEnumerable<RenderEntry> NearbyRenderEntries(byte kind,float range,bool cameraFrustum=true){
      float query=Math.Max(0f,range)+RenderIndexedRadiusLimit+RangeCullPadding;int minX=RenderCell(camera.Position.X-query),maxX=RenderCell(camera.Position.X+query),minZ=RenderCell(camera.Position.Z-query),maxZ=RenderCell(camera.Position.Z+query);
      for(int z=minZ;z<=maxZ;z++)for(int x=minX;x<=maxX;x++)if(renderGrid.TryGetValue((x,z),out List<RenderEntry> bucket))foreach(RenderEntry entry in bucket)if(entry.Kind==kind&&SphereWithinDistance(entry.Center,entry.Radius,range)&&(!cameraFrustum||SphereVisibleInCameraFrustum(entry.Center,entry.Radius)))yield return entry;
      foreach(RenderEntry entry in renderGlobal)if(entry.Kind==kind&&SphereWithinDistance(entry.Center,entry.Radius,range)&&(!cameraFrustum||SphereVisibleInCameraFrustum(entry.Center,entry.Radius)))yield return entry;
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
      if(selected==null&&currentCameraRoom!=null&&!IsEverywhereRoom(currentCameraRoom)&&!skyRoomNames.Contains(currentCameraRoom.RoomName)&&rooms.Contains(currentCameraRoom))selected=currentCameraRoom;
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
    private HashSet<string> BuildVisibleRoomSet(Room current, WorldRenderSettings s){
      if(s.Mode==WorldRenderMode.Map||!s.EnableRoomVisibility||current==null||IsEverywhereRoom(current))return null;
      // PugTools does not have Jedipedia's native dPVS object/portal query yet. The room DAT visible list
      // alone is NOT a replacement outdoors: it changes abruptly when the free camera crosses a cell and
      // was the reason entire terrain/model chunks vanished while flying. Outdoors, rely on conservative
      // room + instance frustum culling instead. The authored list is kept for enclosed interior cells.
      if(current.OutdoorsVisible)return null;
      var set=new HashSet<string>(StringComparer.OrdinalIgnoreCase){current.RoomName,"_everywhere_"};
      foreach(string n in current.VisibleRooms){string room=NormalizeRoom(n);if(!string.IsNullOrEmpty(room))set.Add(room);}
      string sky=ResolveSkyRoomName(current.EnvironmentScheme);if(s.ShowSky&&!string.IsNullOrEmpty(sky))set.Add(sky);return set;
    }
    private static string NormalizeRoom(string s)=>(s??"").Replace('\\','/').TrimStart('/').Replace(".dat","",StringComparison.OrdinalIgnoreCase).ToLowerInvariant();

    private GR2_Material ResolvePieceMaterial(GR2 model, GR2_Mesh_Piece piece) {
      if(model==null||piece==null)return null;
      GR2_Material mat=null;
      if(piece.matId>=0&&model.materials.ElementAtOrDefault(piece.matId)!=null)materials.TryGetValue(model.materials[piece.matId].materialName,out mat);
      else if(model.materials.Count>0)materials.TryGetValue(model.materials[0].materialName,out mat);
      return mat;
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
      if(Math.Abs(appliedCameraFar-cameraFar)<.01f&&Math.Abs(camera.Aspect-AspectRatio)<.0001f)return;
      camera.SetLens(camera.FovY>0?camera.FovY:.25f*SlimDXNet.MathF.PI,AspectRatio,.01f,cameraFar);
      appliedCameraFar=cameraFar;InvalidateTemporalHistory();
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

    private bool ShadowRangeVisible(Vector3 center,float radius,float far){float d=far+radius+2f;return (center-camera.Position).LengthSquared()<=d*d;}

    private void DrawTerrain(Matrix vp,HashSet<string> visible,WorldRenderSettings s,AreaEnvironmentScheme cameraEnv,bool sceneShadows){
      if(s.Mode==WorldRenderMode.Map){
        foreach(Room room in rooms){
          if(skyRoomNames.Contains(room.RoomName)||!RoomVisible(room,visible))continue;
          ApplyRoomEnvironment(room,cameraEnv,s,sceneShadows);
          foreach(AssetInstance inst in room.InstancesById.Values)if(InstanceVisibleOnMap(inst)&&inst.hasHeightMap&&inst.VBO!=null)DrawTerrainInstance(inst,room,vp,visible,s);
        }
        return;
      }
      // Normal perspective rendering never walks every terrain placement on the planet. The spatial index is
      // queried by the exact View budget, then each candidate still gets a sphere/range test in NearbyRenderEntries.
      Room activeRoom=null;
      foreach(RenderEntry entry in NearbyRenderEntries(RenderKindTerrain,camera.FarZ)){
        Room room=entry.Room;AssetInstance inst=entry.Instance;
        if(room==null||!InstanceVisibleInWorld(inst)||skyRoomNames.Contains(room.RoomName)||!RoomVisible(room,visible)||inst.VBO==null)continue;
        if(!ReferenceEquals(activeRoom,room)){ApplyRoomEnvironment(room,cameraEnv,s,sceneShadows);activeRoom=room;}
        DrawTerrainInstance(inst,room,vp,visible,s);
      }
    }

    private void DrawTerrainInstance(AssetInstance inst,Room room,Matrix vp,HashSet<string> visible,WorldRenderSettings s){
      Matrix world=InstanceWorld(inst,room);fx.SetWorld(world);fx.SetViewProj(vp);SetNearestLocalLights(new Vector3(world.M41,world.M42,world.M43),room,s,visible,true);
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

      // Jedipedia's terrain path first lays down black + depth, then sums every splat layer with ONE/ONE.
      // That guarantees complete coverage even when the first material's mask is zero at a texel.
      fx.SetMaterial(null);fx.TerrainCoverage.GetPassByIndex(0).Apply(ImmediateContext);ImmediateContext.DrawIndexed(drawCount,drawStart,0);
      var layers=inst.HeightMap?.TerrainLayers;
      if(layers==null||layers.Count==0){
        GR2_Material fallback=GetTerrainMaterial("terrain_checkered");
        fx.SetTerrain(fallback,null,gpu?.ColorMap,1f,new Vector2(2f/Math.Max(1u,inst.HeightMap.width),2f/Math.Max(1u,inst.HeightMap.depth)));
        PickTerrainTech(s,true,false).GetPassByIndex(0).Apply(ImmediateContext);ImmediateContext.DrawIndexed(drawCount,drawStart,0);return;
      }
      foreach(TerrainLayerMask layer in layers){
        GR2_Material mat=GetTerrainMaterial(layer.MaterialName);ShaderResourceView mask=null;if(gpu!=null)gpu.Masks.TryGetValue(layer.MaterialName,out mask);
        fx.SetTerrain(mat,mask,gpu?.ColorMap,1f,new Vector2(2f/Math.Max(1u,inst.HeightMap.width),2f/Math.Max(1u,inst.HeightMap.depth)));
        PickTerrainTech(s,true,false).GetPassByIndex(0).Apply(ImmediateContext);ImmediateContext.DrawIndexed(drawCount,drawStart,0);
      }
    }

    private TerrainLodRange SelectTerrainLod(AssetInstance inst,Room room,TerrainGpu gpu,WorldRenderSettings s){
      if(gpu?.LodRanges==null||gpu.LodRanges.Length==0)return new TerrainLodRange(0,inst.numFaces);
      if(s.Mode==WorldRenderMode.Map)return gpu.LodRanges[0]; // Jedipedia favors map correctness over one-shot speed.
      HeightMap hm=inst.HeightMap;if(hm==null)return gpu.LodRanges[0];
      Matrix world=InstanceWorld(inst,room);Vector3 localCenter=new Vector3(0,(hm.MinElevation+hm.MaxElevation)*.5f,0);Vector3 center=Vector3.TransformCoordinate(localCenter,world);
      float rx=Math.Max(0,(hm.width-1)*.1f),rz=Math.Max(0,(hm.depth-1)*.1f),ry=Math.Max(0,(hm.MaxElevation-hm.MinElevation)*.5f);
      float sx=(float)Math.Sqrt(world.M11*world.M11+world.M12*world.M12+world.M13*world.M13),sy=(float)Math.Sqrt(world.M21*world.M21+world.M22*world.M22+world.M23*world.M23),sz=(float)Math.Sqrt(world.M31*world.M31+world.M32*world.M32+world.M33*world.M33);
      float maxScale=Math.Max(.0001f,Math.Max(sx,Math.Max(sy,sz)));float radius=(float)Math.Sqrt(rx*rx+ry*ry+rz*rz)*maxScale;
      float distance=Math.Max(0,(camera.Position-center).Length()-radius);int level=0;while(level<terrainLodDistances.Length&&distance>=terrainLodDistances[level])level++;
      return gpu.LodRanges[Math.Min(level,gpu.LodRanges.Length-1)];
    }

    private EffectTechnique PickTerrainTech(WorldRenderSettings s,bool additive,bool dummy){if(s.Mode==WorldRenderMode.Wireframe)return fx.Wire;bool lit=s.Mode!=WorldRenderMode.Unlit&&s.EnableLighting;return lit?(additive?fx.TerrainAddLit:fx.TerrainLit):(additive?fx.TerrainAddUnlit:fx.TerrainUnlit);}

    private void DrawDynamicDetails(Matrix vp,HashSet<string> visible,WorldRenderSettings s,AreaEnvironmentScheme cameraEnv,bool sceneShadows){
      if(area==null||dynamicDetailLayout==null)return;
      ImmediateContext.InputAssembler.InputLayout=dynamicDetailLayout;ImmediateContext.InputAssembler.PrimitiveTopology=PrimitiveTopology.TriangleList;
      try{
        Room activeRoom=null;
        foreach(RenderEntry entry in NearbyRenderEntries(RenderKindTerrain,10f)){
          Room room=entry.Room;AssetInstance inst=entry.Instance;
          if(room==null||!InstanceVisibleInWorld(inst)||skyRoomNames.Contains(room.RoomName)||!RoomVisible(room,visible)||inst.HeightMap?.DynamicDetails==null||inst.HeightMap.DynamicDetails.Count==0)continue;
          float distance=DynamicDetailTerrainDistance(inst,room);if(distance>=10f)continue;
          if(!ReferenceEquals(activeRoom,room)){ApplyRoomEnvironment(room,cameraEnv,s,sceneShadows);activeRoom=room;}
          List<DynamicDetailGpu> batches=EnsureDynamicDetails(inst);List<DynamicDetailMeshBatch> meshBatches=EnsureDynamicDetailMeshes(inst);if((batches==null||batches.Count==0)&&meshBatches.Count==0)continue;
          Matrix world=entry.World;fx.SetWorld(world);fx.SetViewProj(vp);SetNearestLocalLights(new Vector3(world.M41,world.M42,world.M43),room,s,visible,true);
          foreach(DynamicDetailGpu batch in batches){
            if(batch.Buffer==null||batch.Count<=0||batch.Material==null)continue;
            fx.SetMaterial(batch.Material);fx.SetDynamicDetail(camera.Right,batch.AtlasMode,batch.Wind,batch.Material.vegetationParams2.Z,batch.TextureSize);
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

    // Jedipedia admits DYD for a whole terrain tile when the tile's horizontal AABB is within 10 world units.
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
      if(string.IsNullOrWhiteSpace(name))return null;if(materials.TryGetValue(name,out GR2_Material shared))return shared;if(dynamicDetailMaterials.TryGetValue(name,out GR2_Material material))return material;
      material=new GR2_Material(name);try{material.ParseMAT(Device);}catch(Exception ex){System.Diagnostics.Debug.WriteLine("Could not load DYD material "+name+": "+ex.Message);}if(material.alphaTestValue<=0)material.alphaTestValue=.5f;else if(material.alphaTestValue>1)material.alphaTestValue=Math.Min(1f,material.alphaTestValue/255f);dynamicDetailMaterials[name]=material;return material;
    }
    private static void ReleaseOwnedMaterial(GR2_Material m){if(m==null)return;Release(ref m.ageSRV);Release(ref m.complexionSRV);Release(ref m.diffuseSRV);Release(ref m.diffuse2SRV);Release(ref m.facepaintSRV);Release(ref m.glossSRV);Release(ref m.paletteMaskSRV);Release(ref m.paletteSRV);Release(ref m.rotationSRV);Release(ref m.waterSurfaceSRV);}

    private void DrawSky(AreaEnvironmentScheme env,WorldRenderSettings s){
      string activeSky=ResolveSkyRoomName(env);
      if(string.IsNullOrEmpty(activeSky)||activeSky=="_everywhere_")return;
      Room room=rooms.FirstOrDefault(r=>r!=null&&r.RoomName==activeSky);
      if(room==null)return;
      ApplyRoomEnvironment(room,env,s,false);ClearLocalLightBinding();
      Matrix skyCameraInv=Matrix.Identity;bool hasSkyCamera=TryGetSkyCameraInverse(room,out skyCameraInv);
      Matrix skyVp=GetSkyViewProjection();
      foreach(AssetInstance inst in room.InstancesById.Values){
        if(!InstanceVisibleInWorld(inst)||inst.hasHeightMap||inst.hasWater||!models.TryGetValue(inst.assetID,out GR2 model)||!model.enabled)continue;
        Matrix world=InstanceWorldForSky(inst,room);if(hasSkyCamera)world*=skyCameraInv;
        DrawModel(model,world,skyVp,s,true,null);
      }
    }

    private void DrawModels(Matrix vp,HashSet<string> visible,WorldRenderSettings s,AreaEnvironmentScheme env,bool sceneShadows){
      if(s.Mode==WorldRenderMode.Map){
        foreach(Room room in rooms){
          if(skyRoomNames.Contains(room.RoomName)||!RoomVisible(room,visible))continue;
          ApplyRoomEnvironment(room,env,s,sceneShadows);
          foreach(AssetInstance inst in room.InstancesById.Values){
            if(!InstanceVisibleOnMap(inst)||inst.hasHeightMap||inst.hasWater||!models.TryGetValue(inst.assetID,out GR2 model)||!model.enabled)continue;
            Matrix world=InstanceWorld(inst,room);SetNearestLocalLights(new Vector3(world.M41,world.M42,world.M43),room,s,visible,false);
            AreaEnvironmentMaterial envMat=area?.GetEnvironmentMaterial(room,inst);DrawModel(model,world,vp,s,false,envMat);
          }
        }
        return;
      }

      // Jedipedia batches repeated opaque/test geometry with drawElementsInstanced. SWTOR open worlds contain
      // thousands of repeated rocks, trees and props, so distance culling alone still leaves the D3D11 render
      // thread draw-call bound. PugTools already had an instanced input path for DYD meshes; reuse it for safe
      // ordinary models and split batches by room + local-light cell so shared uniforms stay correct.
      var batches=new Dictionary<(Room Room,GR2 Model,int Lod,int X,int Y,int Z),ModelInstanceBatch>();
      var singles=new List<(RenderEntry Entry,int Lod)>();
      bool splitLights=s.EnableLocalLights&&s.EnableLighting&&s.Mode!=WorldRenderMode.Unlit&&s.Mode!=WorldRenderMode.Wireframe&&s.Mode!=WorldRenderMode.Heightmap;
      foreach(RenderEntry entry in NearbyRenderEntries(RenderKindModel,camera.FarZ)){
        Room room=entry.Room;AssetInstance inst=entry.Instance;GR2 model=entry.Model;
        if(room==null||inst==null||model==null||!model.enabled||skyRoomNames.Contains(room.RoomName)||!InstanceRoomVisible(inst,room,visible))continue;
        // Jedipedia's model LOD is not cosmetic: below the lowest schema threshold the asset stops contributing.
        // This removes the sea of tiny props that remained CPU/GPU work in v7 even after distance culling.
        if(ShouldCullModelByLod(model,entry.World,false,inst.LodFactor))continue;int lod=SelectModelLodLevel(model,entry.World,false,inst.LodFactor);
        if(!ModelCanUseRegularInstancing(model)||!MatrixHasNearlyUniformScale(entry.World)){singles.Add((entry,lod));continue;}
        Vector3 p=new Vector3(entry.World.M41,entry.World.M42,entry.World.M43);
        int lx=splitLights?LightCell(p.X):0,ly=splitLights?LightCell(p.Y):0,lz=splitLights?LightCell(p.Z):0;
        var key=(room,model,lod,lx,ly,lz);
        if(!batches.TryGetValue(key,out ModelInstanceBatch batch)){batch=new ModelInstanceBatch{Room=room,Model=model,LodLevel=lod,LightSample=p};batches[key]=batch;}
        batch.Worlds.Add(entry.World);
      }

      Room activeRoom=null;
      foreach(var single in singles){
        RenderEntry entry=single.Entry;Room room=entry.Room;AssetInstance inst=entry.Instance;if(!ReferenceEquals(activeRoom,room)){ApplyRoomEnvironment(room,env,s,sceneShadows);activeRoom=room;}
        Matrix world=entry.World;SetNearestLocalLights(new Vector3(world.M41,world.M42,world.M43),room,s,visible,false);
        AreaEnvironmentMaterial envMat=area?.GetEnvironmentMaterial(room,inst);DrawModel(entry.Model,world,vp,s,false,envMat,single.Lod);
      }
      foreach(ModelInstanceBatch batch in batches.Values){
        if(batch.Worlds.Count<2){
          Matrix world=batch.Worlds[0];if(!ReferenceEquals(activeRoom,batch.Room)){ApplyRoomEnvironment(batch.Room,env,s,sceneShadows);activeRoom=batch.Room;}
          SetNearestLocalLights(new Vector3(world.M41,world.M42,world.M43),batch.Room,s,visible,false);DrawModel(batch.Model,world,vp,s,false,null,batch.LodLevel);continue;
        }
        if(!ReferenceEquals(activeRoom,batch.Room)){ApplyRoomEnvironment(batch.Room,env,s,sceneShadows);activeRoom=batch.Room;}
        SetNearestLocalLights(batch.LightSample,batch.Room,s,visible,false);
        Buffer instanceBuffer=UploadRegularModelInstances(batch.Worlds);if(instanceBuffer==null){foreach(Matrix world in batch.Worlds)DrawModel(batch.Model,world,vp,s,false,null,batch.LodLevel);continue;}
        ImmediateContext.InputAssembler.InputLayout=instancedLayout;DrawInstancedModel(batch.Model,instanceBuffer,batch.Worlds.Count,Matrix.Identity,vp,s,batch.LodLevel,batch.Worlds[0]);
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
      if(model==null)return;int selectedLod=forcedLod>=0?forcedLod:SelectModelLodLevel(model,world,false);fx.SetWorld(world);fx.SetViewProj(vp);
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
      fx.ClearLocalLights();lastLocalLightCount=0;Array.Clear(lastLocalLightSelection,0,lastLocalLightSelection.Length);
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

    private static bool IsCollisionMesh(GR2 model,GR2_Mesh mesh){
      if(mesh==null)return false;string name=(mesh.meshName??string.Empty).ToLowerInvariant();string path=NormalizeAssetPath(model?.filename);
      return mesh.lod==-1||name=="collision"||path.Contains("designblockout/cover_objects/")||path.Contains("superexclusion")||name.Contains("superexclusion");
    }

    private static bool IsNonVisualMesh(GR2 model,GR2_Mesh mesh){
      if(mesh==null)return true;if(IsCollisionMesh(model,mesh))return true;string name=(mesh.meshName??string.Empty).ToLowerInvariant();string path=NormalizeAssetPath(model?.filename);
      if(mesh.lod==-3)return true;return mesh.lod==-2||name=="portal"||path.Contains("arch/fadeportal/");
    }

    private bool TryModelLodProjection(GR2 model,Matrix world,float lodFactor,out float projectedSize){
      projectedSize=0f;GR2_Bounding_Box box=model?.globalBox;if(box==null)return false;
      Vector3 min=new Vector3(box.minX,box.minY,box.minZ),max=new Vector3(box.maxX,box.maxY,box.maxZ);if(!IsFinite(min)||!IsFinite(max)||min.X>max.X||min.Y>max.Y||min.Z>max.Z)return false;
      Vector3 localCenter=(min+max)*.5f,center=Vector3.TransformCoordinate(localCenter,world);float radius=(max-min).Length()*.5f;if(radius<=.0001f||float.IsNaN(radius)||float.IsInfinity(radius))return false;
      float factor=Math.Max(0f,float.IsNaN(lodFactor)||float.IsInfinity(lodFactor)?1f:lodFactor);
      float distance=Math.Max((camera.Position-center).Length(),.0001f);projectedSize=radius*factor*GrannyLodProjectedSizeScale/distance;return true;
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
        GR2_Material mat=ResolvePieceMaterial(model,piece);if(IsMaterialHiddenFromWorld(mat))continue;
        string alpha=mat?.alphaMode??"None";if(!string.Equals(alpha,"None",StringComparison.OrdinalIgnoreCase)&&!string.Equals(alpha,"Test",StringComparison.OrdinalIgnoreCase))return false;
        string derived=mat?.derived??String.Empty;if(string.Equals(derived,"Skydome",StringComparison.OrdinalIgnoreCase)||string.Equals(derived,"UberEnvBlend",StringComparison.OrdinalIgnoreCase)||string.Equals(derived,"OpacityFade",StringComparison.OrdinalIgnoreCase)||string.Equals(derived,"Distortion",StringComparison.OrdinalIgnoreCase)||string.Equals(derived,"_FinalBlend",StringComparison.OrdinalIgnoreCase)||string.Equals(derived,"Water",StringComparison.OrdinalIgnoreCase))return false;
      }}
      foreach(var attached in model.attachedModels)if(!ModelCanUseRegularInstancing(attached,seen))return false;
      return true;
    }

    private Buffer UploadRegularModelInstances(List<Matrix> worlds){
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
      try{DataBox mapped=ImmediateContext.MapSubresource(regularModelInstanceBuffer,MapMode.WriteDiscard,SlimDX.Direct3D11.MapFlags.None);mapped.Data.WriteRange(data);ImmediateContext.UnmapSubresource(regularModelInstanceBuffer,0);return regularModelInstanceBuffer;}
      catch(Exception ex){System.Diagnostics.Debug.WriteLine("Regular world instancing upload failed: "+ex.Message);return null;}
    }
    private void DrawModel(GR2 model,Matrix world,Matrix vp,WorldRenderSettings s,bool sky,AreaEnvironmentMaterial envMat,int forcedLod=-1){
      if(model==null)return;int selectedLod=forcedLod>=0?forcedLod:SelectModelLodLevel(model,world,sky||s.Mode==WorldRenderMode.Map);fx.SetWorld(world);fx.SetViewProj(vp);
      foreach(var mesh in model.meshes){if(mesh.vertBuffer==null||mesh.idxBuffer==null||!MeshVisibleForLod(model,mesh,selectedLod))continue;ImmediateContext.InputAssembler.SetVertexBuffers(0,new VertexBufferBinding(mesh.vertBuffer,PosNormalTexTan.Stride,0));ImmediateContext.InputAssembler.SetIndexBuffer(mesh.idxBuffer,Format.R16_UInt,0);
        foreach(var piece in mesh.meshPieces){GR2_Material mat=null;if(piece.matId>=0&&model.materials.ElementAtOrDefault(piece.matId)!=null)materials.TryGetValue(model.materials[piece.matId].materialName,out mat);else if(model.materials.Count>0)materials.TryGetValue(model.materials[0].materialName,out mat);if(IsMaterialHiddenFromWorld(mat))continue;fx.SetMaterial(mat);
          if(!sky&&mat!=null&&string.Equals(mat.derived,"UberEnvBlend",StringComparison.OrdinalIgnoreCase)&&envMat!=null)fx.SetEnvironmentBlend(envMat,mat,LoadTexture(envMat.BlendDiffuse),LoadTexture(envMat.BlendNormal));
          var tech=PickModelTech(s,mat,sky);tech.GetPassByIndex(0).Apply(ImmediateContext);ImmediateContext.DrawIndexed((int)piece.numPieceFaces*3,(int)piece.startIndex*3,0);}}
      foreach(var a in model.attachedModels)DrawModel(a,world,vp,s,sky,envMat);
    }
    private static bool IsMaterialHiddenFromWorld(GR2_Material mat){
      if(mat==null)return false;
      string visibility=mat.visibility??String.Empty;
      // Jedipedia's ordinary in-game view hides both EditorOnly utility geometry and Hidden collision/occluder
      // materials. These are the source of conspicuous authoring placeholders such as yellow "Converting" blocks.
      return visibility.Equals("EditorOnly",StringComparison.OrdinalIgnoreCase)||visibility.Equals("Hidden",StringComparison.OrdinalIgnoreCase);
    }

    private EffectTechnique PickModelTech(WorldRenderSettings s,GR2_Material mat,bool sky){
      if(sky)return fx.Sky;if(s.Mode==WorldRenderMode.Wireframe)return fx.Wire;if(s.Mode==WorldRenderMode.Unlit||!s.EnableLighting)return fx.Unlit;
      string alpha=mat?.alphaMode??"None";if(string.Equals(alpha,"Test",StringComparison.OrdinalIgnoreCase))return fx.AlphaTestLit;
      if(string.Equals(alpha,"Add",StringComparison.OrdinalIgnoreCase))return fx.AddLit;
      if(string.Equals(alpha,"Multiply",StringComparison.OrdinalIgnoreCase))return fx.MultiplyLit;
      return !string.Equals(alpha,"None",StringComparison.OrdinalIgnoreCase)?fx.AlphaLit:fx.Lit;
    }

    private EffectTechnique PickInstancedModelTech(WorldRenderSettings s,GR2_Material mat){
      if(s.Mode==WorldRenderMode.Wireframe)return fx.InstancedWire;if(s.Mode==WorldRenderMode.Unlit||!s.EnableLighting)return fx.InstancedUnlit;
      string alpha=mat?.alphaMode??"None";if(string.Equals(alpha,"Test",StringComparison.OrdinalIgnoreCase))return fx.InstancedAlphaTestLit;
      return !string.Equals(alpha,"None",StringComparison.OrdinalIgnoreCase)?fx.InstancedAlphaLit:fx.InstancedLit;
    }

    private void DrawInstancedModel(GR2 model,Buffer instanceBuffer,int instanceCount,Matrix parentWorld,Matrix vp,WorldRenderSettings s,int forcedLod=-1,Matrix? lodReferenceWorld=null){
      if(model==null||instanceBuffer==null||instanceCount<=0)return;Matrix reference=lodReferenceWorld??parentWorld;int selectedLod=forcedLod>=0?forcedLod:SelectModelLodLevel(model,reference,s.Mode==WorldRenderMode.Map);fx.SetWorld(parentWorld);fx.SetViewProj(vp);
      foreach(var mesh in model.meshes){
        if(mesh.vertBuffer==null||mesh.idxBuffer==null||!MeshVisibleForLod(model,mesh,selectedLod))continue;
        ImmediateContext.InputAssembler.SetVertexBuffers(0,new[]{new VertexBufferBinding(mesh.vertBuffer,PosNormalTexTan.Stride,0),new VertexBufferBinding(instanceBuffer,64,0)});ImmediateContext.InputAssembler.SetIndexBuffer(mesh.idxBuffer,Format.R16_UInt,0);
        foreach(var piece in mesh.meshPieces){GR2_Material mat=null;if(piece.matId>=0&&model.materials.ElementAtOrDefault(piece.matId)!=null)materials.TryGetValue(model.materials[piece.matId].materialName,out mat);else if(model.materials.Count>0)materials.TryGetValue(model.materials[0].materialName,out mat);if(IsMaterialHiddenFromWorld(mat))continue;fx.SetMaterial(mat);
          PickInstancedModelTech(s,mat).GetPassByIndex(0).Apply(ImmediateContext);ImmediateContext.DrawIndexedInstanced((int)piece.numPieceFaces*3,instanceCount,(int)piece.startIndex*3,0,0);}
      }
      foreach(var a in model.attachedModels)DrawInstancedModel(a,instanceBuffer,instanceCount,parentWorld,vp,s,-1,reference);
    }

    private void DrawInstancedModelShadow(GR2 model,Buffer instanceBuffer,int instanceCount,Matrix parentWorld,int forcedLod=-1,Matrix? lodReferenceWorld=null){
      if(model==null||instanceBuffer==null||instanceCount<=0)return;Matrix reference=lodReferenceWorld??parentWorld;int selectedLod=forcedLod>=0?forcedLod:SelectModelLodLevel(model,reference,false);fx.SetWorld(parentWorld);
      foreach(var mesh in model.meshes){
        if(mesh.vertBuffer==null||mesh.idxBuffer==null||!MeshVisibleForLod(model,mesh,selectedLod))continue;
        ImmediateContext.InputAssembler.SetVertexBuffers(0,new[]{new VertexBufferBinding(mesh.vertBuffer,PosNormalTexTan.Stride,0),new VertexBufferBinding(instanceBuffer,64,0)});ImmediateContext.InputAssembler.SetIndexBuffer(mesh.idxBuffer,Format.R16_UInt,0);
        foreach(var piece in mesh.meshPieces){GR2_Material mat=null;if(piece.matId>=0&&model.materials.ElementAtOrDefault(piece.matId)!=null)materials.TryGetValue(model.materials[piece.matId].materialName,out mat);else if(model.materials.Count>0)materials.TryGetValue(model.materials[0].materialName,out mat);if(IsMaterialHiddenFromWorld(mat))continue;fx.SetMaterial(mat);fx.InstancedShadow.GetPassByIndex(0).Apply(ImmediateContext);ImmediateContext.DrawIndexedInstanced((int)piece.numPieceFaces*3,instanceCount,(int)piece.startIndex*3,0,0);}
      }
      foreach(var a in model.attachedModels)DrawInstancedModelShadow(a,instanceBuffer,instanceCount,parentWorld,-1,reference);
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
      Matrix skyProjection=Matrix.PerspectiveFovRH(camera.FovY>0?camera.FovY:.25f*SlimDXNet.MathF.PI,Math.Max(.01f,camera.Aspect),.01f,SkyFarDistance);
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
        foreach(RenderEntry entry in NearbyRenderEntries(RenderKindWater,camera.FarZ)){
          Room room=entry.Room;AssetInstance inst=entry.Instance;
          if(room==null||!InstanceVisibleInWorld(inst)||skyRoomNames.Contains(room.RoomName)||!RoomVisible(room,visible)||inst.VBO==null)continue;
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
      waterGpu.TryGetValue(inst,out WaterGpu gpu);AreaEnvironmentScheme waterEnv=room.EnvironmentScheme??area?.GetEnvironmentScheme("area")??new AreaEnvironmentScheme();ShaderResourceView environmentMap=LoadTexture(waterEnv.EnvironmentMap);
      fx.SetWorld(waterWorld);fx.SetViewProj(vp);fx.SetMaterial(null);SetNearestLocalLights(new Vector3(waterWorld.M41,waterWorld.M42,waterWorld.M43),room,s,visible,false,true);
      fx.SetWater(inst,elapsed,gpu?.Normal1??defaultWaterNormal,gpu?.Normal2??gpu?.Normal1??defaultWaterNormal,gpu?.DepthMap??defaultWaterDepth,gpu?.SurfaceMap,environmentMap);
      ImmediateContext.InputAssembler.SetVertexBuffers(0,new VertexBufferBinding(inst.VBO,PosNormalTexTan.Stride,0));ImmediateContext.InputAssembler.SetIndexBuffer(inst.IBO,Format.R16_UInt,0);
      EffectTechnique tech=s.Mode==WorldRenderMode.Wireframe?fx.Wire:fx.Water;tech.GetPassByIndex(0).Apply(ImmediateContext);ImmediateContext.DrawIndexed(inst.numFaces,0,0);
    }

    private void DrawMapArt(Matrix vp){
      if(mapArtGpu.Count==0)return;
      ImmediateContext.InputAssembler.PrimitiveTopology=PrimitiveTopology.TriangleList;fx.SetWorld(Matrix.Identity);fx.SetViewProj(vp);
      foreach(var g in mapArtGpu){if(g.Texture==null||g.Buffer==null)continue;fx.SetMapArt(g.Texture,.82f);ImmediateContext.InputAssembler.SetVertexBuffers(0,new VertexBufferBinding(g.Buffer,PosNormalTexTan.Stride,0));fx.MapArt.GetPassByIndex(0).Apply(ImmediateContext);ImmediateContext.Draw(g.Count,0);}
      fx.ClearMapArt();
    }

    private void DrawLines(List<LineGpu> list,Matrix vp,float range){if(list.Count==0)return;ImmediateContext.InputAssembler.PrimitiveTopology=PrimitiveTopology.LineList;fx.SetWorld(Matrix.Identity);fx.SetViewProj(vp);fx.SetMaterial(null);foreach(var g in list){if(range<float.MaxValue&&!SphereWithinDistance(g.Center,g.Radius,range))continue;fx.SetOverlay(g.Color);ImmediateContext.InputAssembler.SetVertexBuffers(0,new VertexBufferBinding(g.Buffer,PosNormalTexTan.Stride,0));fx.Overlay.GetPassByIndex(0).Apply(ImmediateContext);ImmediateContext.Draw(g.Count,0);}ImmediateContext.InputAssembler.PrimitiveTopology=PrimitiveTopology.TriangleList;}
    private void DrawOverlayTriangles(List<LineGpu> list,Matrix vp,float range){if(list.Count==0)return;ImmediateContext.InputAssembler.PrimitiveTopology=PrimitiveTopology.TriangleList;fx.SetWorld(Matrix.Identity);fx.SetViewProj(vp);fx.SetMaterial(null);foreach(var g in list){if(range<float.MaxValue&&!SphereWithinDistance(g.Center,g.Radius,range))continue;fx.SetOverlay(g.Color);ImmediateContext.InputAssembler.SetVertexBuffers(0,new VertexBufferBinding(g.Buffer,PosNormalTexTan.Stride,0));fx.Overlay.GetPassByIndex(0).Apply(ImmediateContext);ImmediateContext.Draw(g.Count,0);}}

    private void BuildLocalLightIndex(){
      localLightGrid.Clear();localLightGlobal.Clear();localLightSelectionCache.Clear();localLightVisibilityScope=null;
      foreach(Room room in rooms){
        foreach(AssetInstance inst in room.InstancesById.Values){
          if(!InstanceVisibleInWorld(inst)||!inst.IsLocalLight)continue;
          Matrix m=InstanceWorld(inst,room);Vector3 pos=new Vector3(m.M41,m.M42,m.M43);Vector3 d=Vector3.TransformNormal(new Vector3(0,0,1),m);if(d.LengthSquared()<.0001f)d=new Vector3(0,-1,0);else d.Normalize();
          bool directional=string.Equals(inst.LocalLightType,"DIRECTIONAL",StringComparison.OrdinalIgnoreCase);float range=Math.Max(.0001f,inst.LocalLightRange);
          ShaderResourceView illuminationMap=LoadTexture(inst.LocalLightIlluminationMap);ShaderResourceView falloffMap=LoadTexture(inst.LocalLightFalloff);ShaderResourceView rampMap=LoadTexture(inst.LocalLightRampMap);
          float type=directional?1f:(string.Equals(inst.LocalLightType,"SPOT",StringComparison.OrdinalIgnoreCase)?2f:0f);
          var entry=new LocalLightEntry{
            Room=room,Instance=inst,Position=pos,RangeSquared=range*range,Directional=directional,RestrictToRoom=inst.LocalLightRestrictToRoom,
            DoHeightmaps=inst.LocalLightDoHeightmaps,DoGranny=inst.LocalLightDoGranny,DoWater=inst.LocalLightDoWater,
            PosRange=new Vector4(pos,range),ColorIntensity=new Vector4(inst.LocalLightColor.X,inst.LocalLightColor.Y,inst.LocalLightColor.Z,inst.LocalLightIntensity),DirType=new Vector4(d,type),
            ProjectorInv=BuildLocalLightProjectorInverse(m,range),IlluminationMap=illuminationMap,FalloffMap=falloffMap,RampMap=rampMap,
            ProjectorParams=new Vector4(inst.LocalLightSourceOffset,illuminationMap!=null?1:0,falloffMap!=null?1:0,rampMap!=null?1:0)
          };
          if(directional||range>LocalLightGridRangeLimit){localLightGlobal.Add(entry);continue;}
          int minX=LightCell(pos.X-range),maxX=LightCell(pos.X+range),minZ=LightCell(pos.Z-range),maxZ=LightCell(pos.Z+range);
          for(int z=minZ;z<=maxZ;z++)for(int x=minX;x<=maxX;x++){
            var key=(x,z);if(!localLightGrid.TryGetValue(key,out List<LocalLightEntry> bucket))localLightGrid[key]=bucket=new List<LocalLightEntry>();bucket.Add(entry);
          }
        }
      }
      lastLocalLightCount=-1;Array.Clear(lastLocalLightSelection,0,lastLocalLightSelection.Length);
    }
    private static int LightCell(float coordinate)=>(int)Math.Floor(coordinate/LocalLightCellSize);

    private void SetNearestLocalLights(Vector3 p,Room receiverRoom,WorldRenderSettings s,HashSet<string> visible,bool heightmap,bool water=false){
      if(!s.EnableLocalLights||!s.EnableLighting||s.Mode==WorldRenderMode.Unlit||s.Mode==WorldRenderMode.Wireframe||s.Mode==WorldRenderMode.Heightmap||s.Mode==WorldRenderMode.Map){ClearLocalLightBinding();return;}

      int cellX=LightCell(p.X),cellY=LightCell(p.Y),cellZ=LightCell(p.Z);
      byte receiverKind=water?(byte)2:(heightmap?(byte)1:(byte)0);
      string receiverName=receiverRoom?.RoomName??String.Empty;
      var cacheKey=(cellX,cellY,cellZ,receiverName,receiverKind);

      if(!localLightSelectionCache.TryGetValue(cacheKey,out LocalLightSelection selection)){
        for(int i=0;i<4;i++){localLightBest[i]=null;localLightBestDistance[i]=float.MaxValue;}
        int count=0;
        // Quantize the receiver position to the centre of a 32-unit cell. This deliberately trades a tiny
        // amount of per-object light ordering precision for a very large reduction in CPU work: every mesh
        // in the same cell/room now shares one four-light list for the frame instead of sorting it again.
        Vector3 samplePoint=new Vector3((cellX+.5f)*LocalLightCellSize,(cellY+.5f)*LocalLightCellSize,(cellZ+.5f)*LocalLightCellSize);
        if(localLightGrid.TryGetValue((cellX,cellZ),out List<LocalLightEntry> bucket)){
          for(int i=0;i<bucket.Count;i++)count=ConsiderLocalLight(bucket[i],samplePoint,receiverRoom,visible,heightmap,water,count);
        }
        for(int i=0;i<localLightGlobal.Count;i++)count=ConsiderLocalLight(localLightGlobal[i],samplePoint,receiverRoom,visible,heightmap,water,count);
        selection=LocalLightSelection.From(localLightBest,count);
        localLightSelectionCache[cacheKey]=selection;
      }

      BindLocalLightSelection(selection);
    }

    private void BindLocalLightSelection(LocalLightSelection selection){
      int count=selection.Count;
      bool unchanged=count==lastLocalLightCount;
      if(unchanged)for(int i=0;i<count;i++)if(!ReferenceEquals(lastLocalLightSelection[i],selection.Get(i))){unchanged=false;break;}
      if(unchanged)return;

      for(int n=0;n<4;n++){
        LocalLightEntry e=n<count?selection.Get(n):null;lastLocalLightSelection[n]=e;
        if(e!=null){localLightPosRangeScratch[n]=e.PosRange;localLightColorScratch[n]=e.ColorIntensity;localLightDirScratch[n]=e.DirType;localLightProjectorScratch[n]=e.ProjectorInv;localLightProjectorParamsScratch[n]=e.ProjectorParams;localLightIlluminationScratch[n]=e.IlluminationMap;localLightFalloffScratch[n]=e.FalloffMap;localLightRampScratch[n]=e.RampMap;}
        else {localLightPosRangeScratch[n]=new Vector4();localLightColorScratch[n]=new Vector4();localLightDirScratch[n]=new Vector4();localLightProjectorScratch[n]=Matrix.Identity;localLightProjectorParamsScratch[n]=new Vector4();localLightIlluminationScratch[n]=null;localLightFalloffScratch[n]=null;localLightRampScratch[n]=null;}
      }
      lastLocalLightCount=count;
      fx.SetLocalLights(localLightPosRangeScratch,localLightColorScratch,localLightDirScratch,localLightProjectorScratch,localLightProjectorParamsScratch,localLightIlluminationScratch,localLightFalloffScratch,localLightRampScratch,count);
    }

    private int ConsiderLocalLight(LocalLightEntry e,Vector3 p,Room receiverRoom,HashSet<string> visible,bool heightmap,bool water,int count){
      if(e==null||!RoomVisible(e.Room,visible)||(water?!e.DoWater:(heightmap?!e.DoHeightmaps:!e.DoGranny))||(e.RestrictToRoom&&receiverRoom!=e.Room))return count;
      float dist=e.Directional?-1f:(e.Position-p).LengthSquared();if(!e.Directional&&dist>e.RangeSquared)return count;
      int insert=count;
      if(count<4)count++;else {if(dist>=localLightBestDistance[3])return count;insert=3;}
      while(insert>0&&dist<localLightBestDistance[insert-1]){if(insert<4){localLightBestDistance[insert]=localLightBestDistance[insert-1];localLightBest[insert]=localLightBest[insert-1];}insert--;}
      localLightBestDistance[insert]=dist;localLightBest[insert]=e;return count;
    }

    private void ClearLocalLightBinding(){
      if(lastLocalLightCount==0)return;fx.ClearLocalLights();lastLocalLightCount=0;Array.Clear(lastLocalLightSelection,0,lastLocalLightSelection.Length);
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
      foreach(RenderEntry entry in NearbyRenderEntries(RenderKindTerrain,cascadeFar,false)){
        Room room=entry.Room;AssetInstance inst=entry.Instance;
        if(room==null||!InstanceVisibleInWorld(inst)||!RoomVisible(room,visible)||skyRoomNames.Contains(room.RoomName)||(!string.IsNullOrEmpty(skyRoom)&&room.RoomName==skyRoom))continue;
        Matrix world=entry.World;fx.SetWorld(world);
        if(s.ShowTerrain&&inst.VBO!=null){
          ImmediateContext.InputAssembler.InputLayout=inputLayout;ImmediateContext.InputAssembler.SetVertexBuffers(0,new VertexBufferBinding(inst.VBO,PosNormalTexTan.Stride,0));terrainGpu.TryGetValue(inst,out TerrainGpu tg);TerrainLodRange lod=SelectTerrainLod(inst,room,tg,s);Buffer terrainIbo=tg?.LodIndexBuffer??inst.IBO;
          if(terrainIbo!=null){ImmediateContext.InputAssembler.SetIndexBuffer(terrainIbo,Format.R16_UInt,0);fx.Shadow.GetPassByIndex(0).Apply(ImmediateContext);ImmediateContext.DrawIndexed(lod.Count>0?lod.Count:inst.numFaces,lod.Count>0?lod.StartIndex:0,0);}
        }
        if(s.ShowDynamicDetails&&inst.HeightMap?.DynamicDetails!=null&&DynamicDetailTerrainDistance(inst,room)<10f){
          List<DynamicDetailGpu> batches=EnsureDynamicDetails(inst);if(batches!=null&&batches.Count>0){ImmediateContext.InputAssembler.InputLayout=dynamicDetailLayout;ImmediateContext.InputAssembler.PrimitiveTopology=PrimitiveTopology.TriangleList;foreach(DynamicDetailGpu batch in batches){if(batch.Buffer==null||batch.Count<=0||batch.Material==null)continue;fx.SetMaterial(batch.Material);fx.SetDynamicDetail(camera.Right,batch.AtlasMode,batch.Wind,batch.Material.vegetationParams2.Z,batch.TextureSize);ImmediateContext.InputAssembler.SetVertexBuffers(0,new VertexBufferBinding(batch.Buffer,48,0));fx.DynamicDetailShadow.GetPassByIndex(0).Apply(ImmediateContext);ImmediateContext.Draw(batch.Count,0);}}
          List<DynamicDetailMeshBatch> meshBatches=EnsureDynamicDetailMeshes(inst);if(meshBatches.Count>0){ImmediateContext.InputAssembler.InputLayout=instancedLayout;foreach(DynamicDetailMeshBatch meshBatch in meshBatches)if(meshBatch.Model!=null&&meshBatch.Model.enabled&&meshBatch.InstanceBuffer!=null&&meshBatch.InstanceCount>0)DrawInstancedModelShadow(meshBatch.Model,meshBatch.InstanceBuffer,meshBatch.InstanceCount,world);}
        }
      }
      if(s.ShowModels){
        // The main pass now instances repeated opaque/test GR2s; do the same for each CSM cascade. Without this,
        // a planet with thousands of repeated props still paid four complete model draw-call streams per frame even
        // though the shadow radius is only 1.5..25 units. The spatial query limits candidates first, then batching
        // collapses the remaining repeated placements to one draw per model/submesh.
        var shadowBatches=new Dictionary<(GR2 Model,int Lod),List<Matrix>>();var shadowSingles=new List<(RenderEntry Entry,int Lod)>();
        foreach(RenderEntry entry in NearbyRenderEntries(RenderKindModel,cascadeFar,false)){
          Room room=entry.Room;GR2 model=entry.Model;
          if(room==null||model==null||!model.enabled||!InstanceRoomVisible(entry.Instance,room,visible)||skyRoomNames.Contains(room.RoomName)||(!string.IsNullOrEmpty(skyRoom)&&room.RoomName==skyRoom))continue;
          float lodFactor=entry.Instance?.LodFactor??1f;if(ShouldCullModelByLod(model,entry.World,false,lodFactor))continue;int lod=SelectModelLodLevel(model,entry.World,false,lodFactor);
          if(ModelCanUseRegularInstancing(model)&&MatrixHasNearlyUniformScale(entry.World)){
            var key=(model,lod);if(!shadowBatches.TryGetValue(key,out List<Matrix> worlds))shadowBatches[key]=worlds=new List<Matrix>();worlds.Add(entry.World);
          }else shadowSingles.Add((entry,lod));
        }
        ImmediateContext.InputAssembler.InputLayout=inputLayout;
        foreach(var single in shadowSingles){fx.SetWorld(single.Entry.World);DrawModelShadow(single.Entry.Model,single.Lod,single.Entry.World);}
        foreach(var pair in shadowBatches){
          GR2 model=pair.Key.Model;int lod=pair.Key.Lod;
          if(pair.Value.Count<2){fx.SetWorld(pair.Value[0]);ImmediateContext.InputAssembler.InputLayout=inputLayout;DrawModelShadow(model,lod,pair.Value[0]);continue;}
          Buffer instanceBuffer=UploadRegularModelInstances(pair.Value);if(instanceBuffer==null){foreach(Matrix world in pair.Value){fx.SetWorld(world);ImmediateContext.InputAssembler.InputLayout=inputLayout;DrawModelShadow(model,lod,world);}continue;}
          ImmediateContext.InputAssembler.InputLayout=instancedLayout;DrawInstancedModelShadow(model,instanceBuffer,pair.Value.Count,Matrix.Identity,lod,pair.Value[0]);
        }
      }
      ImmediateContext.InputAssembler.InputLayout=inputLayout;ImmediateContext.InputAssembler.PrimitiveTopology=PrimitiveTopology.TriangleList;
    }
    private void DrawModelShadow(GR2 model,int forcedLod=-1,Matrix? lodReferenceWorld=null){
      if(model==null)return;Matrix reference=lodReferenceWorld??Matrix.Identity;int selectedLod=forcedLod>=0?forcedLod:SelectModelLodLevel(model,reference,false);
      foreach(var mesh in model.meshes){
        if(mesh.vertBuffer==null||mesh.idxBuffer==null||!MeshVisibleForLod(model,mesh,selectedLod))continue;
        ImmediateContext.InputAssembler.SetVertexBuffers(0,new VertexBufferBinding(mesh.vertBuffer,PosNormalTexTan.Stride,0));ImmediateContext.InputAssembler.SetIndexBuffer(mesh.idxBuffer,Format.R16_UInt,0);
        foreach(var piece in mesh.meshPieces){
          GR2_Material mat=null;if(piece.matId>=0&&model.materials.ElementAtOrDefault(piece.matId)!=null)materials.TryGetValue(model.materials[piece.matId].materialName,out mat);else if(model.materials.Count>0)materials.TryGetValue(model.materials[0].materialName,out mat);
          if(IsMaterialHiddenFromWorld(mat))continue;
          bool alpha=mat!=null&&!string.IsNullOrEmpty(mat.alphaMode)&&mat.alphaMode!="None";if(alpha)fx.SetMaterial(mat);
          (alpha?fx.AlphaShadow:fx.Shadow).GetPassByIndex(0).Apply(ImmediateContext);ImmediateContext.DrawIndexed((int)piece.numPieceFaces*3,(int)piece.startIndex*3,0);
        }
      }
      foreach(var a in model.attachedModels)DrawModelShadow(a,-1,reference);
    }

    private void BuildModelGeometry(){var built=new HashSet<GR2>();foreach(var model in models.Values)BuildModelGeometry(model,built,false);foreach(var model in dynamicDetailMeshModels.Values)BuildModelGeometry(model,built,false);foreach(var model in strongholdHookModels.Values)BuildModelGeometry(model,built,false);BuildJedipediaFeatureGeometry(built);}
    private void BuildModelGeometry(GR2 model,HashSet<GR2> built,bool attachment){
      if(!built.Add(model))return;
      foreach(var mesh in model.meshes){
        if(IsNonVisualMesh(model,mesh))continue;
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
        g.ColorMap=CreateTexture(i.HeightMap.TerrainColorMapRgba,(int)i.HeightMap.width,(int)i.HeightMap.depth,Format.R8G8B8A8_UNorm,4);
        foreach(var l in i.HeightMap.TerrainLayers){g.Masks[l.MaterialName]=CreateTexture(l.Weights,l.Width,l.Height,Format.R8_UNorm,1);GetTerrainMaterial(l.MaterialName);}
        BuildTerrainLods(i,g);terrainGpu[i]=g;
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
        var g=new WaterGpu();
        g.Normal1=LoadTexture(i.WaterNormalMap1);
        g.Normal2=LoadTexture(i.WaterNormalMap2)??g.Normal1;
        g.DepthMap=CreateWaterDepthTexture(i.WaterDepthData);
        if(g.DepthMap!=null)g.OwnsDepthMap=true;else g.DepthMap=defaultWaterDepth;
        g.SurfaceMap=LoadTexture(i.WaterSurfaceMap);
        if(g.SurfaceMap==null&&i.WaterTextureIndex>=0){
          string terrainName=null;
          if(area?.TerrainTextureNames.TryGetValue((uint)i.WaterTextureIndex,out string byId)==true)terrainName=byId;
          else if(area!=null&&i.WaterTextureIndex<area.TerrainTextures.Count)terrainName=area.TerrainTextures[i.WaterTextureIndex].Name;
          if(!string.IsNullOrEmpty(terrainName))g.SurfaceMap=GetTerrainMaterial(terrainName).waterSurfaceSRV;
        }
        waterGpu[i]=g;
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
      if(terrainMaterials.TryGetValue(name,out GR2_Material m))return m;
      m=new GR2_Material(name);
      try{m.ParseMAT(Device);}catch(Exception ex){System.Diagnostics.Debug.WriteLine("World terrain material '"+name+"' failed: "+ex.Message);}
      terrainMaterials[name]=m;return m;
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

    private ShaderResourceView LoadTexture(string raw){string path=NormalizeTexturePath(raw);if(path==null)return null;if(textureCache.TryGetValue(path,out ShaderResourceView v))return v;try{var file=area?.FindFile(path);if(file==null)return null;using Stream s=file.OpenCopyInMemory();v=ShaderResourceView.FromStream(Device,s,(int)s.Length);textureCache[path]=v;return v;}catch{return null;}}
    private static string NormalizeTexturePath(string raw){if(string.IsNullOrWhiteSpace(raw))return null;string p=raw.Trim().Replace('\\','/');if(p.EndsWith(".tex",StringComparison.OrdinalIgnoreCase))p=p.Substring(0,p.Length-4);if(!p.EndsWith(".dds",StringComparison.OrdinalIgnoreCase))p+=".dds";if(p.StartsWith("resources/",StringComparison.OrdinalIgnoreCase))p="/"+p;else if(!p.StartsWith("/resources/",StringComparison.OrdinalIgnoreCase))p="/resources/"+p.TrimStart('/');return p.ToLowerInvariant();}

    private void BuildRoads(){if(area==null)return;foreach(AreaPath p in area.Paths.Where(x=>x.IsMapRoad&&x.Points.Count>1)){var pts=new List<Vector3>();for(int i=0;i<p.Points.Count-1;i++){pts.Add(p.Points[i].Position);pts.Add(p.Points[i+1].Position);}if(p.Circular){pts.Add(p.Points[^1].Position);pts.Add(p.Points[0].Position);}roadGpu.Add(BuildLine(pts,p.Color));}}
    private void BuildMapNotes(){if(area==null)return;foreach(AreaMapNote n in area.MapNotes){float r=.75f;var p=n.Position;noteGpu.Add(BuildLine(new[]{p-new Vector3(r,0,0),p+new Vector3(r,0,0),p-new Vector3(0,0,r),p+new Vector3(0,0,r)},new Vector4(1,.75f,.1f,1)));}if(area.ArrivalPoint!=null){var p=area.ArrivalPoint.Position;float r=1.2f;noteGpu.Add(BuildLine(new[]{p-new Vector3(r,0,0),p+new Vector3(r,0,0),p-new Vector3(0,0,r),p+new Vector3(0,0,r)},new Vector4(.2f,1,.2f,1)));}}
    private void BuildMapArt(){
      if(area?.MapPages==null||area.MapPages.Count==0)return;
      var pages=area.MapPages.Where(p=>p.HasImage&&!string.IsNullOrWhiteSpace(p.ImagePath)).ToList();
      // Most SWTOR areas contain a root map plus zoomed child pages. Showing both at once causes
      // duplicate artwork, so prefer root pages and fall back to all pages where no root is authored.
      var roots=pages.Where(p=>p.ParentId==0).ToList();if(roots.Count>0)pages=roots;
      foreach(AreaMapPage page in pages){
        ShaderResourceView texture=LoadTexture(page.ImagePath);if(texture==null)continue;
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
      foreach(RenderEntry entry in renderGlobal.Concat(renderGrid.Values.SelectMany(x=>x))){
        if(entry==null||!IsFinite(entry.Center)||entry.Radius<0f||entry.Radius>4096f)continue;
        IncludeXZ(entry.Center.X-entry.Radius,entry.Center.Z-entry.Radius);IncludeXZ(entry.Center.X+entry.Radius,entry.Center.Z+entry.Radius);
      }
      if(!havePrimaryExtent){minX=boundsMin.X;maxX=boundsMax.X;minZ=boundsMin.Z;maxZ=boundsMax.Z;}
      if(maxX-minX<1f){minX-=5f;maxX+=5f;}if(maxZ-minZ<1f){minZ-=5f;maxZ+=5f;}
      mapExtentMinX=minX;mapExtentMaxX=maxX;mapExtentMinZ=minZ;mapExtentMaxZ=maxZ;
      mapCenter=new Vector2((minX+maxX)*.5f,(minZ+maxZ)*.5f);
      mapZoom=1f;
      UpdateMapCamera();
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
      if(mapOpen==open)return;
      mapOpen=open;mapPointerDown=false;mapPointerDragged=false;
      if(open)ResetMapCamera();
      InvalidateTemporalHistory();
      if(Window is WorldBrowser browser){
        browser.SetFullMapActive(open);
        browser.SetStatusLabel(open
          ? "Map: click = teleport, drag = pan, mouse wheel = zoom, M/Esc = close"
          : "Map closed. Camera speed: "+cameraSpeed.ToString("0.##")+" u/s");
      }
    }

    private Vector2 MapWorldAtScreen(Point p){
      float width=Math.Max(1,ClientWidth),height=Math.Max(1,ClientHeight);
      float nx=p.X/width*2f-1f;
      float nz=p.Y/height*2f-1f; // screen down is world +Z in the top-down map
      return new Vector2(mapCenter.X+nx*mapVisibleWidth*.5f,mapCenter.Y+nz*mapVisibleHeight*.5f);
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
      if(Window is WorldBrowser browser)browser.SetStatusLabel(string.Format(System.Globalization.CultureInfo.InvariantCulture,"Teleported to {0:0.00}, {1:0.00}, {2:0.00}",camera.Position.X,camera.Position.Y,camera.Position.Z));
      mapOpen=false;mapPointerDown=false;mapPointerDragged=false;InvalidateTemporalHistory();
    }

    public void MakeScreenshot(ImageFileFormat format){try{string filename=Tools.PrepExtractPath(fqn+'-'+DateTime.Now.ToString("yyyyMMddHHmmss")+'.'+format.ToString().ToLower());var d=new Texture2DDescription{Width=ClientWidth,Height=ClientHeight,MipLevels=1,ArraySize=1,Format=Format.R8G8B8A8_UNorm,SampleDescription=new SampleDescription(1,0),Usage=ResourceUsage.Default,BindFlags=BindFlags.None,CpuAccessFlags=CpuAccessFlags.None};using var output=new Texture2D(Device,d);using var back=SlimDX.Direct3D11.Resource.FromSwapChain<Texture2D>(SwapChain,0);ImmediateContext.CopyResource(back,output);Texture2D.ToFile(ImmediateContext,output,format,filename);((WorldBrowser)Window).SetStatusLabel("Screenshot Completed: "+filename);}catch(Exception ex){System.Diagnostics.Debug.WriteLine(ex);}}

    protected override void OnMouseDown(object sender,MouseEventArgs e){
      lastMousePos=e.Location;Control control=Window.Controls.Find(RenderPanelName,true).FirstOrDefault();if(control!=null)control.Capture=true;
      if(mapOpen&&e.Button==MouseButtons.Left){mapPointerDown=true;mapPointerDragged=false;mapPointerStart=e.Location;mapPanStartCenter=mapCenter;}
    }
    protected override void OnMouseUp(object sender,MouseEventArgs e){
      Control control=Window.Controls.Find(RenderPanelName,true).FirstOrDefault();if(control!=null)control.Capture=false;
      if(mapOpen&&e.Button==MouseButtons.Left&&mapPointerDown){bool teleport=!mapPointerDragged;mapPointerDown=false;if(teleport)TeleportFromMap(e.Location);}
    }
    protected override void OnMouseMove(object sender,MouseEventArgs e){
      if(mapOpen){
        if(mapPointerDown&&(e.Button&MouseButtons.Left)!=0){
          int dx=e.X-mapPointerStart.X,dy=e.Y-mapPointerStart.Y;
          if(!mapPointerDragged&&dx*dx+dy*dy>=16)mapPointerDragged=true;
          if(mapPointerDragged){
            float wx=dx/(float)Math.Max(1,ClientWidth)*mapVisibleWidth;
            float wz=dy/(float)Math.Max(1,ClientHeight)*mapVisibleHeight;
            mapCenter=new Vector2(mapPanStartCenter.X-wx,mapPanStartCenter.Y-wz);UpdateMapCamera();
          }
        }
        lastMousePos=e.Location;return;
      }
      if((e.Button&MouseButtons.Left)!=0||(e.Button&MouseButtons.Right)!=0){float dy=SlimDXNet.MathF.ToRadians(.4f*(e.Y-lastMousePos.Y));float dx=-SlimDXNet.MathF.ToRadians(.4f*(e.X-lastMousePos.X));camera.Pitch(-dy);camera.Yaw(dx);}
      lastMousePos=e.Location;
    }
    public void HandleMouseWheel(System.Drawing.Point location,int delta){
      if(mapOpen){ZoomMapAt(location,delta);return;}
      cameraSpeed=(float)Math.Max(MinCameraSpeed,Math.Min(MaxCameraSpeed,cameraSpeed*Math.Exp(delta*0.001f)));
      if(Window is WorldBrowser browser){bool walking=SettingsSnapshot().WalkingMode;browser.SetStatusLabel((walking?"Walking speed: ":"Camera speed: ")+cameraSpeed.ToString("0.##")+" u/s (Shift "+(walking?"2x":"10x")+")");}
    }

    protected override void OnMouseWheel(object sender,MouseEventArgs e){
      HandleMouseWheel(e.Location,e.Delta);
    }
  }
}
