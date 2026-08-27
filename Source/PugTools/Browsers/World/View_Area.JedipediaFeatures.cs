using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using FileFormats;
using SlimDX;
using SlimDX.Direct3D11;
using SlimDX.DXGI;
using SlimDXNet;
using SlimDXNet.Vertex;
using SpriteTextRenderer;
using Buffer = SlimDX.Direct3D11.Buffer;

namespace PugTools {
  internal sealed partial class View_AREA {
    private sealed class UtilityRenderEntry {
      public Room Room;
      public AssetInstance Instance;
      public GR2 Model;
      public byte Category;
      public Vector4 Color;
    }

    private sealed class PhaseVolumeEntry {
      public Room Room;
      public AssetInstance Instance;
      public Matrix Inverse;
      public float HalfWidth;
      public float HalfHeight;
      public float HalfDepth;
      public float Volume;
      public bool Ellipsoid;
      public string Name;
    }

    private sealed class NpcNameplateEntry {
      public WorldNpcPlacement Placement;
      public Vector3 Screen;
      public Vector4 VisibilitySample;
      public float Distance;
    }

    private sealed class ObjectOcclusionCandidate {
      public RenderEntry Entry;
      public object DynamicOwner;
      public object DynamicStateToken;
      public Vector3 DynamicCenter;
      public float DynamicRadius;
      public Vector4 Sample;
      public float DistanceSquared;
    }

    private struct DynamicOcclusionPose {
      public Vector3 Center;
      public float Radius;
      public object StateToken;
    }

    private sealed class SpnMotionPoint {
      public Vector3 Position;
      public float Speed = 1f;
      public float HoldTime;
    }

    private sealed class SpnMotionEvent {
      public int From;
      public int To;
      public float Start;
      public float Duration;
    }

    private sealed class SpnMotionRoute {
      public readonly List<SpnMotionPoint> Points = new List<SpnMotionPoint>();
      public readonly List<SpnMotionEvent> Events = new List<SpnMotionEvent>();
      public float LegDuration;
      public bool Alternating;
    }

    private sealed class WalkingPlatformSupport {
      public WorldSpnPlacement Spn;
      public AssetInstance Instance;
      public Room Room;
      public Matrix World;
      // For a dyn assembly the walkable mesh is a child transform of the moving placeable root.
      public Matrix LocalMatrix = Matrix.Identity;
    }

    private static readonly string[] NpcNameplateBoneNames = { "attach_nameplate", "attach_nameplate_fallback", "nameplate" };
    private readonly Dictionary<WorldSpnPlacement, SpnMotionRoute> spnMotionRoutes = new Dictionary<WorldSpnPlacement, SpnMotionRoute>();
    private readonly Dictionary<WorldNpcPlacement, SpnMotionRoute> npcMotionRoutes = new Dictionary<WorldNpcPlacement, SpnMotionRoute>();
    private const float SpnSpawnPointDwellSeconds = 2f;
    private const float SpnSpawnPointTravelSeconds = .25f;
    private const float SpnVariantSeconds = 2f;

    private const int NpcNameplateMaxCount = 192;
    private const int NpcNameplateVisibilityInterval = 4;
    private const float NpcNameplateDepthBias = .00001f;
    private const float NpcNameplateTestLift = .01f;

    // Jedipedia's dPVS performs per-receiver visibility before expensive shading. We do not have its native solver,
    // but the already sampleable D3D11 depth image can conservatively answer the same question one frame later.
    // Restrict this to small/medium static model receivers and require two hidden results before suppressing a draw.
    private const int ObjectOcclusionMaxCount = 512;
    private const int DynamicOcclusionReservedCount = 160;
    private const int ObjectOcclusionInterval = 5;
    private const int ObjectOcclusionHiddenConfirmations = 2;
    private const float DynamicOcclusionMoveTolerance = .08f;
    private const float DynamicOcclusionRadiusTolerance = .15f;
    private const float ObjectOcclusionDepthBias = .00003f;
    private const float ObjectOcclusionMinDistance = 8f;
    private const float ObjectOcclusionMaxProjectedRadius = 96f;
    private const float ObjectOcclusionCameraMoveTolerance = .25f;
    private const float ObjectOcclusionCameraLookDotTolerance = .9990f;

    private const byte UtilitySpawner = 1;
    private const byte UtilityCover = 2;
    private const byte UtilityLight = 3;
    private const byte UtilitySeed = 4;
    private const byte UtilityOther = 5;

    // Keep the full NPC feature set, but use a game-like population draw budget instead of keeping characters
    // almost 200 metres away resident and drawable. 15 world units is roughly 150 game metres and removes a large
    // amount of invisible/one-pixel population work in dense hubs while preserving normal exploration visibility.
    // The frustum leeway keeps just-offscreen characters alive for shadows without posing the whole zone.
    private const float NpcMaxRenderDistance = 15f;
    // CPU skinning is by far the most expensive part of population rendering. Keep all NPCs visible to the same
    // distance, but freeze only distant characters in bind pose. This matches the game's practical animation LOD
    // and avoids burning a core on characters that occupy only a handful of pixels.
    private const float NpcAnimationRenderDistance = 6.5f;
    private const float PopulationFrustumLeeway = 2f;

    private sealed class NpcSkinState : IDisposable {
      public GR2 Model;
      public WorldNpcAnimationClip Clip;
      public IList<GR2_Bone_Skeleton> Skeleton;
      public JBATransform[] PoseSamples;
      public int[] SkeletonToChannel;
      public bool[] UseBindTranslation;
      public Matrix[] InverseBind;
      public Matrix[] CurrentWorld;
      public bool[] CurrentValid;
      public System.Numerics.Quaternion[] BindRotationMorpheme;
      public System.Numerics.Vector3[] BindTranslationMorpheme;
      public System.Numerics.Quaternion[] WorldRotationMorpheme;
      public System.Numerics.Vector3[] WorldTranslationMorpheme;
      public readonly Dictionary<string, Matrix> SkinMatrices = new Dictionary<string, Matrix>(StringComparer.OrdinalIgnoreCase);
      public readonly Dictionary<GR2_Mesh, Matrix[]> Palettes = new Dictionary<GR2_Mesh, Matrix[]>();
      public readonly Dictionary<GR2_Mesh, Buffer> GpuBuffers = new Dictionary<GR2_Mesh, Buffer>();
      public readonly Dictionary<GR2_Mesh, PosNormalTexTan[]> SkinnedVertices = new Dictionary<GR2_Mesh, PosNormalTexTan[]>();
      public readonly Dictionary<GR2_Mesh, Buffer> DynamicBuffers = new Dictionary<GR2_Mesh, Buffer>();
      public int BoundBoneCount;
      public long LastUseFrame;

      public void Dispose() {
        foreach (Buffer buffer in GpuBuffers.Values) buffer?.Dispose();
        foreach (Buffer buffer in DynamicBuffers.Values) buffer?.Dispose();
        GpuBuffers.Clear();
        DynamicBuffers.Clear();
        Palettes.Clear();
        SkinnedVertices.Clear();
        SkinMatrices.Clear();
      }
    }

    private readonly Dictionary<GR2, Dictionary<WorldNpcAnimationClip, NpcSkinState>> npcSkinStates = new Dictionary<GR2, Dictionary<WorldNpcAnimationClip, NpcSkinState>>();
    private Dictionary<string, GR2> utilityMarkerModels = new Dictionary<string, GR2>(StringComparer.OrdinalIgnoreCase);
    private List<WorldNpcPlacement> npcPlacements = new List<WorldNpcPlacement>();
    // Population-heavy worlds can contain thousands of NPC templates. Walking the whole list several times per
    // frame (draw, depth-visibility, nameplates) was still CPU-bound even after render-distance culling. Keep a tiny
    // fixed spatial index of the authored/stable NPC spawn poses so those passes only enumerate nearby cells.
    private const float NpcSpatialCellSize = 10f;
    private readonly Dictionary<(int X, int Z), List<WorldNpcPlacement>> npcSpatialGrid = new Dictionary<(int X, int Z), List<WorldNpcPlacement>>();
    private readonly List<WorldNpcPlacement> npcSpatialFallback = new List<WorldNpcPlacement>();
    private List<WorldSpnPlacement> spnPlacements = new List<WorldSpnPlacement>();
    private const float SpnSpatialCellSize = 12f;
    private readonly Dictionary<(int X, int Z), List<WorldSpnPlacement>> spnSpatialGrid = new Dictionary<(int X, int Z), List<WorldSpnPlacement>>();
    private readonly List<WorldSpnPlacement> spnSpatialFallback = new List<WorldSpnPlacement>();
    // Population GR2 buffers can be a sizeable startup allocation in NPC-heavy hubs. NPC preview now defaults off,
    // so defer those GPU buffers until the user actually enables NPC models; the first enabled frame prepares them
    // on the render thread and subsequent frames use the normal cached buffers. SPN objects keep their own state.
    private bool npcGeometryPrepared;
    private bool spnGeometryPrepared;
    private List<WorldSpnPlacement> walkingSpnPlatforms = new List<WorldSpnPlacement>();
    // DYN light rows are placement-local and can move/state-switch, so they cannot live in BuildLocalLightIndex's
    // static grid. They are rebuilt from the active SPN matrices before the visible world pass each frame.
    private readonly List<LocalLightEntry> dynamicSpnLocalLights = new List<LocalLightEntry>();
    private int npcFactionHint = Int32.MinValue; // -1 Empire, +1 Republic, 0 unknown/mixed
    private readonly List<UtilityRenderEntry> utilityRenderEntries = new List<UtilityRenderEntry>();
    private readonly List<LineGpu> utilityPathGpu = new List<LineGpu>();
    private readonly List<LineGpu> utilityMapPathGpu = new List<LineGpu>();
    private readonly List<LineGpu> utilityConnectionGpu = new List<LineGpu>();
    private readonly List<LineGpu> utilityVolumeGpu = new List<LineGpu>();
    private readonly List<LineGpu> pinnedVolumeGpu = new List<LineGpu>();
    private readonly Dictionary<string, LineGpu> pinnedVolumeGpuByKey = new Dictionary<string, LineGpu>(StringComparer.OrdinalIgnoreCase);
    // Keep a CPU copy of the line segments so the WinForms minimap can redraw pins live without regenerating the
    // expensive map snapshot every time a volume is pinned/unpinned.
    private readonly Dictionary<string, Vector3[]> pinnedVolumePointsByKey = new Dictionary<string, Vector3[]>(StringComparer.OrdinalIgnoreCase);
    private readonly List<LineGpu> phaseGatewayGpu = new List<LineGpu>();
    private readonly List<LineGpu> phaseGatewayPlaneGpu = new List<LineGpu>();
    private readonly List<PhaseVolumeEntry> phaseRegionTriggers = new List<PhaseVolumeEntry>();
    private readonly List<LineGpu> utilitySpawnerFallbackGpu = new List<LineGpu>();
    private readonly List<LineGpu> utilityCoverFallbackGpu = new List<LineGpu>();
    private readonly List<LineGpu> utilityLightFallbackGpu = new List<LineGpu>();
    private readonly List<LineGpu> utilitySeedFallbackGpu = new List<LineGpu>();
    private readonly List<LineGpu> utilityOtherGpu = new List<LineGpu>();

    private SpriteRenderer npcTextSprite;
    private FontCache npcTextFont;
    private bool npcTextFontsRegistered;
    private Texture2D npcNameplateVisibilityTexture;
    private RenderTargetView npcNameplateVisibilityTarget;
    private Texture2D npcNameplateVisibilityStaging;
    private readonly Vector4[] npcNameplateVisibilitySamples = new Vector4[NpcNameplateMaxCount];
    private readonly byte[] npcNameplateVisibilityReadback = new byte[NpcNameplateMaxCount * 4];
    private readonly HashSet<WorldNpcPlacement> npcNameplateVisible = new HashSet<WorldNpcPlacement>();
    private int npcNameplateVisibilityFrame;
    private int npcNameplateVisibilityAskedAt = Int32.MinValue / 2;
    private bool npcNameplateVisibilityFailed;
    private bool npcNameplateHasVisibility;
    // ClientWidth/ClientHeight are written by the WinForms UI thread as soon as the splitter moves, while the
    // current D3D frame may still be using the previous swap-chain viewport. Nameplates must use the render-thread
    // viewport snapshot for both projection and depth sampling or they visibly slide while the sidebar is resized.
    private int npcNameplateViewportWidth = -1;
    private int npcNameplateViewportHeight = -1;

    private Texture2D objectOcclusionVisibilityTexture;
    private RenderTargetView objectOcclusionVisibilityTarget;
    private Texture2D objectOcclusionVisibilityStaging;
    private readonly Vector4[] objectOcclusionVisibilitySamples = new Vector4[ObjectOcclusionMaxCount];
    private readonly byte[] objectOcclusionVisibilityReadback = new byte[ObjectOcclusionMaxCount * 4];
    private readonly HashSet<RenderEntry> objectOcclusionHidden = new HashSet<RenderEntry>();
    private readonly Dictionary<RenderEntry, byte> objectOcclusionHiddenCounts = new Dictionary<RenderEntry, byte>();
    // Dynamic receivers share the same GPU depth query, but their hidden result is valid only while their current
    // world-space sphere still matches the pose that was queried. This gives idle/offscreen population the dPVS win
    // without letting a moving lift/NPC carry a stale hidden flag into a doorway.
    private readonly HashSet<object> dynamicOcclusionHidden = new HashSet<object>();
    private readonly Dictionary<object, byte> dynamicOcclusionHiddenCounts = new Dictionary<object, byte>();
    private readonly Dictionary<object, DynamicOcclusionPose> dynamicOcclusionPoses = new Dictionary<object, DynamicOcclusionPose>();
    private int objectOcclusionFrame;
    private int objectOcclusionAskedAt = Int32.MinValue / 2;
    private bool objectOcclusionFailed;
    private bool objectOcclusionHasVisibility;
    private bool objectOcclusionUseThisFrame;
    private Vector3 objectOcclusionCameraPosition;
    private Vector3 objectOcclusionCameraLook = Vector3.UnitZ;
    private int objectOcclusionViewportWidth = -1;
    private int objectOcclusionViewportHeight = -1;
    private string objectOcclusionVisibilityScope = String.Empty;
    // Read depth-query results one or more frames later. The old path copied the 512-pixel strip to a staging
    // texture and immediately mapped it on the CPU, which serialized the render thread behind the GPU every few
    // frames. That is especially painful on large outdoor worlds: Task Manager shows a mostly idle GPU while the
    // CPU waits for D3D11. Keep exactly the same conservative occlusion decisions, but consume the staging copy
    // with DO_NOT_WAIT and keep rendering until the GPU has finished it.
    private readonly List<ObjectOcclusionCandidate> objectOcclusionPendingCandidates = new List<ObjectOcclusionCandidate>(ObjectOcclusionMaxCount);
    private bool objectOcclusionReadbackPending;
    private Vector3 objectOcclusionPendingCameraPosition;
    private Vector3 objectOcclusionPendingCameraLook = Vector3.UnitZ;
    private int objectOcclusionPendingViewportWidth = -1;
    private int objectOcclusionPendingViewportHeight = -1;
    private string objectOcclusionPendingVisibilityScope = String.Empty;
    private int objectOcclusionPendingFrame = Int32.MinValue / 2;
    private readonly System.Diagnostics.Stopwatch worldRenderStatsClock = System.Diagnostics.Stopwatch.StartNew();
    private int worldRenderStatsFrames;
    private volatile string worldRenderStatsSnapshot = "FPS: --   Occ S/D: 0/0";

    public string CurrentRenderStats => worldRenderStatsSnapshot ?? String.Empty;

    private void UpdateWorldRenderStatsSnapshot() {
      worldRenderStatsFrames++;
      long elapsedMs = worldRenderStatsClock.ElapsedMilliseconds;
      if (elapsedMs < 500) return;
      double fps = worldRenderStatsFrames * 1000.0 / Math.Max(1L, elapsedMs);
      int staticHidden = objectOcclusionUseThisFrame ? objectOcclusionHidden.Count : 0;
      int dynamicHidden = objectOcclusionUseThisFrame ? dynamicOcclusionHidden.Count : 0;
      worldRenderStatsSnapshot = String.Format(System.Globalization.CultureInfo.InvariantCulture,
        "FPS: {0:0.0}   Occ S/D: {1}/{2}", fps, staticHidden, dynamicHidden);
      worldRenderStatsFrames = 0;
      worldRenderStatsClock.Restart();
    }

    private float walkingVerticalVelocity;
    private bool walkingWasEnabled;
    private bool walkingGrounded;
    private bool walkingSpaceWasDown;
    // Moving SPN placeables and /engine/follower.fol descendants are real dynamic walkable geometry. Keep the
    // current support and its previous transform so lifts, trams and other moving parents can carry both the camera
    // position and its orientation instead of sliding/turning out from underneath the viewer.
    private WalkingPlatformSupport walkingPlatform;
    private bool walkingPlatformWorldValid;
    private volatile string implicitPhaseName = String.Empty;
    private volatile string currentPhaseName = String.Empty;
    private const float WalkingEyeHeight = .18f;
    private const float WalkingStepHeight = .075f;
    private const float WalkingGravity = .98f;
    private const float WalkingJumpVelocity = .42f;
    private const float WalkingPlatformFloorEpsilon = .002f;

    private void SetJedipediaFeatureData(Dictionary<string, GR2> utilityModels, List<WorldNpcPlacement> npcData, List<WorldSpnPlacement> spnData) {
      utilityMarkerModels = utilityModels ?? new Dictionary<string, GR2>(StringComparer.OrdinalIgnoreCase);
      npcPlacements = npcData ?? new List<WorldNpcPlacement>();
      spnPlacements = spnData ?? new List<WorldSpnPlacement>();
      BuildNpcSpatialIndex();
      BuildSpnSpatialIndex();
      npcGeometryPrepared = false;
      spnGeometryPrepared = false;
      walkingSpnPlatforms = spnPlacements.Where(p => p != null && p.Models != null && p.Models.Count > 0 &&
        (p.Route != null || (p.SpawnPoints != null && p.SpawnPoints.Count > 0))).ToList();
      npcFactionHint = Int32.MinValue;
      npcNameplateVisible.Clear();
      npcNameplateHasVisibility = false;
      npcNameplateVisibilityFrame = 0;
      npcNameplateVisibilityAskedAt = Int32.MinValue / 2;
      npcNameplateViewportWidth = -1;
      npcNameplateViewportHeight = -1;
      InvalidateObjectOcclusionVisibility();
      objectOcclusionFrame = 0;
      objectOcclusionAskedAt = Int32.MinValue / 2;
      objectOcclusionFailed = false;
      objectOcclusionViewportWidth = -1;
      objectOcclusionViewportHeight = -1;
      objectOcclusionVisibilityScope = String.Empty;
      walkingPlatform = null;
      walkingPlatformWorldValid = false;
      spnMotionRoutes.Clear();
      npcMotionRoutes.Clear();
      currentPhaseName = implicitPhaseName ?? String.Empty;
    }

    private static int NpcSpatialCell(float coordinate) => (int)Math.Floor(coordinate / NpcSpatialCellSize);

    private void BuildNpcSpatialIndex() {
      npcSpatialGrid.Clear();
      npcSpatialFallback.Clear();
      if (npcPlacements == null) return;
      foreach (WorldNpcPlacement placement in npcPlacements) {
        if (placement?.Instance == null || placement.Room == null) { if (placement != null) npcSpatialFallback.Add(placement); continue; }
        try {
          Matrix world = NpcPlacementBaseWorld(placement);
          Vector3 position = new Vector3(world.M41, world.M42, world.M43);
          if (!IsFinite(position)) { npcSpatialFallback.Add(placement); continue; }
          var key = (NpcSpatialCell(position.X), NpcSpatialCell(position.Z));
          if (!npcSpatialGrid.TryGetValue(key, out List<WorldNpcPlacement> bucket))
            npcSpatialGrid[key] = bucket = new List<WorldNpcPlacement>();
          bucket.Add(placement);
        } catch { npcSpatialFallback.Add(placement); }
      }
    }

    private IEnumerable<WorldNpcPlacement> NearbyNpcPlacements(float range) {
      if (npcPlacements == null || npcPlacements.Count == 0) yield break;
      if (camera == null || npcSpatialGrid.Count == 0) { foreach (WorldNpcPlacement placement in npcPlacements) yield return placement; yield break; }
      // One-cell padding covers character bounds and authored spawn offsets. The exact distance/frustum tests below
      // remain authoritative, so this broad phase can only add work, never hide a visible NPC.
      float query = Math.Max(0f, range) + NpcSpatialCellSize;
      int minX = NpcSpatialCell(camera.Position.X - query), maxX = NpcSpatialCell(camera.Position.X + query);
      int minZ = NpcSpatialCell(camera.Position.Z - query), maxZ = NpcSpatialCell(camera.Position.Z + query);
      for (int z = minZ; z <= maxZ; z++) for (int x = minX; x <= maxX; x++)
        if (npcSpatialGrid.TryGetValue((x, z), out List<WorldNpcPlacement> bucket))
          foreach (WorldNpcPlacement placement in bucket) yield return placement;
      foreach (WorldNpcPlacement placement in npcSpatialFallback) yield return placement;
    }

    private static int SpnSpatialCell(float coordinate) => (int)Math.Floor(coordinate / SpnSpatialCellSize);

    private void BuildSpnSpatialIndex() {
      spnSpatialGrid.Clear();spnSpatialFallback.Clear();
      if(spnPlacements==null)return;
      foreach(WorldSpnPlacement placement in spnPlacements) {
        if(placement?.Instance==null||placement.Room==null) { if(placement!=null)spnSpatialFallback.Add(placement);continue; }
        // Moving lifts/platforms must stay in the fallback set because their current pose can leave the authored cell.
        if(placement.Route!=null||(placement.SpawnPoints!=null&&placement.SpawnPoints.Count>0)) { spnSpatialFallback.Add(placement);continue; }
        try {
          Matrix world=InstanceWorld(placement.Instance,placement.Room);Vector3 position=new Vector3(world.M41,world.M42,world.M43);
          if(!IsFinite(position)){spnSpatialFallback.Add(placement);continue;}
          var key=(SpnSpatialCell(position.X),SpnSpatialCell(position.Z));
          if(!spnSpatialGrid.TryGetValue(key,out List<WorldSpnPlacement> bucket))spnSpatialGrid[key]=bucket=new List<WorldSpnPlacement>();
          bucket.Add(placement);
        } catch { spnSpatialFallback.Add(placement); }
      }
    }

    private IEnumerable<WorldSpnPlacement> NearbySpnPlacements(float range) {
      if(spnPlacements==null||spnPlacements.Count==0)yield break;
      if(camera==null||spnSpatialGrid.Count==0||!Single.IsFinite(range)||range>10000f) { foreach(WorldSpnPlacement placement in spnPlacements)yield return placement;yield break; }
      float query=Math.Max(0f,range)+SpnSpatialCellSize;
      int minX=SpnSpatialCell(camera.Position.X-query),maxX=SpnSpatialCell(camera.Position.X+query);
      int minZ=SpnSpatialCell(camera.Position.Z-query),maxZ=SpnSpatialCell(camera.Position.Z+query);
      long cellCount=(long)(maxX-minX+1)*(maxZ-minZ+1);
      if(cellCount>Math.Max(128L,(long)spnSpatialGrid.Count*3L)){
        foreach(List<WorldSpnPlacement> bucket in spnSpatialGrid.Values)foreach(WorldSpnPlacement placement in bucket)yield return placement;
      } else {
        for(int z=minZ;z<=maxZ;z++)for(int x=minX;x<=maxX;x++)
          if(spnSpatialGrid.TryGetValue((x,z),out List<WorldSpnPlacement> bucket))foreach(WorldSpnPlacement placement in bucket)yield return placement;
      }
      foreach(WorldSpnPlacement placement in spnSpatialFallback)yield return placement;
    }

    private IEnumerable<WorldSpnPlacement> MapVisibleSpnPlacements() {
      if(spnPlacements==null||spnPlacements.Count==0)yield break;
      if(spnSpatialGrid.Count==0){foreach(WorldSpnPlacement placement in spnPlacements)yield return placement;yield break;}
      float halfW=Math.Max(.001f,mapVisibleWidth*.5f),halfH=Math.Max(.001f,mapVisibleHeight*.5f);
      float pad=SpnSpatialCellSize*2f;
      int minX=SpnSpatialCell(mapCenter.X-halfW-pad),maxX=SpnSpatialCell(mapCenter.X+halfW+pad);
      int minZ=SpnSpatialCell(mapCenter.Y-halfH-pad),maxZ=SpnSpatialCell(mapCenter.Y+halfH+pad);
      long cellCount=(long)(maxX-minX+1)*(maxZ-minZ+1);
      if(cellCount>Math.Max(128L,(long)spnSpatialGrid.Count*3L)){
        foreach(List<WorldSpnPlacement> bucket in spnSpatialGrid.Values)foreach(WorldSpnPlacement placement in bucket)yield return placement;
      } else {
        for(int z=minZ;z<=maxZ;z++)for(int x=minX;x<=maxX;x++)
          if(spnSpatialGrid.TryGetValue((x,z),out List<WorldSpnPlacement> bucket))foreach(WorldSpnPlacement placement in bucket)yield return placement;
      }
      // Routes/moving lifts are deliberately kept out of the fixed grid because their current pose may leave the
      // authored cell. They are few enough to keep as exact fallback candidates.
      foreach(WorldSpnPlacement placement in spnSpatialFallback)yield return placement;
    }

    public string CurrentPhaseName => currentPhaseName ?? String.Empty;

    public void SetImplicitPhaseName(string phaseName) {
      implicitPhaseName = PhaseDisplayName(phaseName);
      currentPhaseName = implicitPhaseName;
    }

    private static string PhaseDisplayName(string phaseName) {
      string text = (phaseName ?? String.Empty).Trim();
      return text.StartsWith("phs.", StringComparison.OrdinalIgnoreCase) ? text.Substring(4) : text;
    }

    private void InitializeFeatureTextRenderer() {
      if (npcTextFontsRegistered || Device == null) return;
      try {
        npcTextSprite = new SpriteRenderer(Device);
        npcTextFont = new FontCache(npcTextSprite);
        npcTextFont.RegisterFont("world-npc-name", 14f, "Arial", SlimDX.DirectWrite.FontWeight.Bold);
        npcTextFont.RegisterFont("world-npc-title", 11f, "Arial", SlimDX.DirectWrite.FontWeight.Bold);
        npcTextFont.RegisterFont("world-npc-item", 11f, "Arial");
        npcTextFont.RegisterFont("world-selection", 12f, "Arial", SlimDX.DirectWrite.FontWeight.Bold);
        npcTextFont.RegisterFont("world-taxi", 12f, "Consolas", SlimDX.DirectWrite.FontWeight.Bold);
        npcTextFontsRegistered = true;
      } catch (Exception ex) {
        System.Diagnostics.Debug.WriteLine("NPC nameplate renderer unavailable: " + ex.Message);
        npcTextFontsRegistered = false;
        npcTextFont?.Dispose(); npcTextFont = null;
        npcTextSprite?.Dispose(); npcTextSprite = null;
      }
    }

    private void BuildJedipediaFeatureGeometry(HashSet<GR2> built) {
      // Geometry is streamed per visible model by EnsureModelGeometryPrepared(). Keep these flags only as a cheap
      // compatibility gate for the existing feature draw paths; no whole-population GPU upload happens here anymore.
      npcGeometryPrepared = true;
      spnGeometryPrepared = true;
    }

    private void BuildNpcGeometry(HashSet<GR2> built) {
      foreach (WorldNpcPlacement placement in npcPlacements)
        if (placement?.Models != null) foreach (GR2 model in placement.Models) if (model != null) BuildModelGeometry(model, built, false);
    }

    private void BuildSpnGeometry(HashSet<GR2> built) {
      foreach (WorldSpnPlacement placement in spnPlacements)
        if (placement?.Models != null) foreach (GR2 model in placement.Models) if (model != null) BuildModelGeometry(model, built, false);
    }

    private void EnsureNpcGeometryPrepared() {
      if (npcGeometryPrepared) return;
      npcGeometryPrepared = true;
    }

    private void EnsureSpnGeometryPrepared() {
      if (spnGeometryPrepared) return;
      spnGeometryPrepared = true;
    }

    private void BuildJedipediaOverlayResources() {
      DisposeLines(utilityPathGpu); DisposeLines(utilityMapPathGpu); DisposeLines(utilityConnectionGpu); DisposeLines(utilityVolumeGpu); DisposeLines(phaseGatewayGpu); DisposeLines(phaseGatewayPlaneGpu);
      DisposeLines(utilitySpawnerFallbackGpu); DisposeLines(utilityCoverFallbackGpu); DisposeLines(utilityLightFallbackGpu); DisposeLines(utilitySeedFallbackGpu); DisposeLines(utilityOtherGpu);
      utilityRenderEntries.Clear();
      phaseRegionTriggers.Clear();
      if (area == null) return;

      var spawnPointLinks = new List<Vector3>();
      var movePointLinks = new List<Vector3>();
      var rallyPointLinks = new List<Vector3>();
      var encounterLinks = new List<Vector3>();
      var volumePoints = new List<Vector3>();
      var phaseGatewayPoints = new List<Vector3>();
      var phaseGatewayTriangles = new List<Vector3>();
      var spawnerFallbackPoints = new List<Vector3>();
      var coverFallbackPoints = new List<Vector3>();
      var lightFallbackPoints = new List<Vector3>();
      var seedFallbackPoints = new List<Vector3>();
      var otherPoints = new List<Vector3>();
      var instanceById = new Dictionary<ulong, AssetInstance>();
      var roomByInstanceId = new Dictionary<ulong, Room>();
      foreach (Room indexedRoom in rooms) {
        if (indexedRoom?.InstancesById == null) continue;
        foreach (AssetInstance indexedInstance in indexedRoom.InstancesById.Values) {
          if (indexedInstance == null) continue;
          instanceById[indexedInstance.ID] = indexedInstance;
          roomByInstanceId[indexedInstance.ID] = indexedRoom;
        }
      }
      foreach (Room room in rooms) {
        foreach (AssetInstance inst in room.InstancesById.Values) {
          if (inst == null || !area.AssetIdMap.TryGetValue(inst.assetID, out AreaAsset asset) || asset == null) continue;
          string ext = (asset.Extension ?? String.Empty).Trim().ToLowerInvariant();
          string normalized = NormalizeAssetPath(asset.Path);
          string markerKey = UtilityMarkerKey(ext, normalized);
          byte category = UtilityCategory(ext, normalized);
          if (category != 0) {
            GR2 marker = null;
            if (!String.IsNullOrWhiteSpace(markerKey)) utilityMarkerModels.TryGetValue(markerKey, out marker);
            // Keep an entry even when the client is missing Jedipedia's pretty beta-era marker. The renderer falls
            // back to a coloured cross below, while selection can still target the authored placement itself.
            utilityRenderEntries.Add(new UtilityRenderEntry { Room = room, Instance = inst, Model = marker, Category = category, Color = UtilityColor(category) });
            if (marker == null) {
              Matrix w = InstanceWorld(inst, room);
              List<Vector3> fallback = category == UtilityCover ? coverFallbackPoints
                : category == UtilityLight ? lightFallbackPoints
                : category == UtilitySeed ? seedFallbackPoints
                : category == UtilityOther ? otherPoints : spawnerFallbackPoints;
              AddCross(fallback, new Vector3(w.M41, w.M42, w.M43), .08f);
            }
          }

          if (inst.parentInstance != 0 && instanceById.TryGetValue(inst.parentInstance, out AssetInstance parent)) {
            Room parentRoom = roomByInstanceId.TryGetValue(parent.ID, out Room owner) ? owner : room;
            string parentExt = area.AssetIdMap.TryGetValue(parent.assetID, out AreaAsset parentAsset) && parentAsset != null
              ? (parentAsset.Extension ?? String.Empty).Trim().ToLowerInvariant() : String.Empty;
            byte linkKind = UtilityLinkKind(ext, parentExt);
            if (linkKind != 0) {
              Matrix a = InstanceWorld(inst, room), b = InstanceWorld(parent, parentRoom);
              Vector3 av = new Vector3(a.M41, a.M42, a.M43), bv = new Vector3(b.M41, b.M42, b.M43);
              // Jedipedia suppresses very long background links (>20 internal units / 200 game metres).
              if ((av - bv).Length() <= 20f) {
                List<Vector3> target = linkKind == 1 ? spawnPointLinks : linkKind == 2 ? movePointLinks : linkKind == 3 ? rallyPointLinks : encounterLinks;
                target.Add(av); target.Add(bv);
              }
            }
          }

          if (ext == "rgn") {
            if (inst.RegionVolume != null) AddRegionVolumeEdges(volumePoints, inst, room);
            else if (inst.HasWidthProperty && inst.HasDepthProperty) AddVolumeEdges(volumePoints, inst, room);
          } else if (ext == "trg" || ext == "env") AddVolumeEdges(volumePoints, inst, room);
          if (ext == "trg") {
            if (String.Equals(inst.TriggerClassType, "INSTANCE_REGION", StringComparison.OrdinalIgnoreCase) && !String.IsNullOrWhiteSpace(inst.TriggerParam))
              RegisterPhaseRegion(inst, room);
            else if (String.Equals(inst.TriggerClassType, "INSTANCE_GATEWAY", StringComparison.OrdinalIgnoreCase))
              AddPhaseGatewayGeometry(phaseGatewayPoints, phaseGatewayTriangles, inst, room);
          }
        }
      }
      if (spawnPointLinks.Count >= 2) utilityConnectionGpu.Add(BuildLine(spawnPointLinks, new Vector4(0f, .68f, .19f, .85f)));
      if (movePointLinks.Count >= 2) utilityConnectionGpu.Add(BuildLine(movePointLinks, new Vector4(.70f, .58f, .06f, .85f)));
      if (rallyPointLinks.Count >= 2) utilityConnectionGpu.Add(BuildLine(rallyPointLinks, new Vector4(1f, .38f, .34f, .85f)));
      if (encounterLinks.Count >= 2) utilityConnectionGpu.Add(BuildLine(encounterLinks, new Vector4(.33f, .60f, 1f, .85f)));
      if (volumePoints.Count >= 2) utilityVolumeGpu.Add(BuildLine(volumePoints, new Vector4(.95f, .45f, .15f, 1f)));
      if (phaseGatewayTriangles.Count >= 3) phaseGatewayPlaneGpu.Add(BuildLine(phaseGatewayTriangles, new Vector4(.55f, 1f, .55f, .30f)));
      if (phaseGatewayPoints.Count >= 2) phaseGatewayGpu.Add(BuildLine(phaseGatewayPoints, new Vector4(.55f, 1f, .55f, .82f)));
      if (spawnerFallbackPoints.Count >= 2) utilitySpawnerFallbackGpu.Add(BuildLine(spawnerFallbackPoints, UtilityColor(UtilitySpawner)));
      if (coverFallbackPoints.Count >= 2) utilityCoverFallbackGpu.Add(BuildLine(coverFallbackPoints, UtilityColor(UtilityCover)));
      if (lightFallbackPoints.Count >= 2) utilityLightFallbackGpu.Add(BuildLine(lightFallbackPoints, UtilityColor(UtilityLight)));
      if (seedFallbackPoints.Count >= 2) utilitySeedFallbackGpu.Add(BuildLine(seedFallbackPoints, UtilityColor(UtilitySeed)));
      if (otherPoints.Count >= 2) utilityOtherGpu.Add(BuildLine(otherPoints, UtilityColor(UtilityOther)));

      foreach (AreaPath path in area.Paths) {
        if (path?.Points == null || path.Points.Count < 2) continue;
        var segments = new List<Vector3>();
        for (int i = 1; i < path.Points.Count; i++) { segments.Add(path.Points[i - 1].Position); segments.Add(path.Points[i].Position); }
        if (path.Circular && path.Points.Count > 2) { segments.Add(path.Points[path.Points.Count - 1].Position); segments.Add(path.Points[0].Position); }
        Vector4 color = path.Color; if (color.W <= 0f) color.W = 1f;
        (path.IsMapRoad ? utilityMapPathGpu : utilityPathGpu).Add(BuildLine(segments, color));
      }
    }

    // Mirrors Jedipedia utilityLinks.js: only authored spawn/move/rally links and encounter-spawner links
    // belong to the utilities overlay. Drawing every ParentInstance relation makes dense rooms unreadable.
    private static byte UtilityLinkKind(string childExt, string parentExt) {
      if (String.Equals(childExt, "spn_pt", StringComparison.OrdinalIgnoreCase)) return 1;
      if (String.Equals(childExt, "spn_mov", StringComparison.OrdinalIgnoreCase)) return 2;
      if (String.Equals(childExt, "spn_rly", StringComparison.OrdinalIgnoreCase)) return 3;
      if (String.Equals(parentExt, "enc", StringComparison.OrdinalIgnoreCase)) return 4;
      return 0;
    }

    private static string UtilityMarkerKey(string ext, string path) {
      if (ext == "spn_c" && path.IndexOf("spn/test/path/seed_point", StringComparison.OrdinalIgnoreCase) >= 0) return "spn_seed";
      if (ext == "fol") return "spn_mov";
      if (ext == "spn_c" && path.StartsWith("enc", StringComparison.OrdinalIgnoreCase)) return "spn_enc";
      return ext;
    }

    private static byte UtilityCategory(string ext, string path) {
      if (ext == "cvr") return UtilityCover;
      if (ext == "lit") return UtilityLight;
      if (ext == "spn_c" && path.IndexOf("spn/test/path/seed_point", StringComparison.OrdinalIgnoreCase) >= 0) return UtilitySeed;
      if (ext == "enc" || ext == "cos" || ext == "fol" || ext.StartsWith("spn_", StringComparison.OrdinalIgnoreCase)) return UtilitySpawner;
      switch (ext) {
        case "stg": case "mpn": case "prt": case "fxp": case "zzp": case "fla": case "ext": case "wws": case "amk":
        case "bil": case "mir": case "sfq": case "box": case "cdr": case "phj": case "bon": case "rbd": case "cam": case "spn_activator": return UtilityOther;
        default: return 0;
      }
    }

    private static Vector4 UtilityColor(byte category) {
      switch (category) {
        case UtilityCover: return new Vector4(.45f, .9f, 1f, 1f);
        case UtilityLight: return new Vector4(1f, .92f, .3f, 1f);
        case UtilitySeed: return new Vector4(.35f, 1f, .45f, 1f);
        case UtilityOther: return new Vector4(.55f, .9f, .48f, 1f);
        default: return new Vector4(.96f, .48f, .28f, 1f);
      }
    }

    private static void AddCross(List<Vector3> points, Vector3 c, float r) {
      points.Add(c - new Vector3(r, 0, 0)); points.Add(c + new Vector3(r, 0, 0));
      points.Add(c - new Vector3(0, r, 0)); points.Add(c + new Vector3(0, r, 0));
      points.Add(c - new Vector3(0, 0, r)); points.Add(c + new Vector3(0, 0, r));
    }

    // region.rgn does not use the simple Width/Height/Depth cuboid used by trigger.trg. Jedipedia decodes
    // rgnVolumeData into a triangulated footprint with a separate extrusion height per vertex. Draw only the
    // boundary shell here (not every internal triangulation edge) so the utility overlay remains readable.
    private void AddRegionVolumeEdges(List<Vector3> points, AssetInstance inst, Room room) {
      RegionVolumeData region = inst?.RegionVolume;
      if (region?.Positions == null || region.Heights == null || region.Indices == null || region.Indices.Length < 3) return;
      Matrix world = InstanceWorld(inst, room);
      var edgeCounts = new Dictionary<(int A,int B),int>();
      void CountEdge(int a,int b) { if (a > b) { int t = a; a = b; b = t; } var key = (a,b); edgeCounts.TryGetValue(key,out int count); edgeCounts[key] = count + 1; }
      for (int i = 0; i + 2 < region.Indices.Length; i += 3) {
        int a=region.Indices[i],b=region.Indices[i+1],c=region.Indices[i+2];
        if(a>=region.Positions.Length||b>=region.Positions.Length||c>=region.Positions.Length)continue;
        CountEdge(a,b);CountEdge(b,c);CountEdge(c,a);
      }
      var boundaryVertices = new HashSet<int>();
      foreach (var pair in edgeCounts) {
        if (pair.Value != 1) continue;
        int a=pair.Key.A,b=pair.Key.B;if(a>=region.Heights.Length||b>=region.Heights.Length)continue;
        Vector3 pa=region.Positions[a],pb=region.Positions[b];Vector3 ta=pa+new Vector3(0,region.Heights[a],0),tb=pb+new Vector3(0,region.Heights[b],0);
        points.Add(Vector3.TransformCoordinate(pa,world));points.Add(Vector3.TransformCoordinate(pb,world));
        points.Add(Vector3.TransformCoordinate(ta,world));points.Add(Vector3.TransformCoordinate(tb,world));
        boundaryVertices.Add(a);boundaryVertices.Add(b);
      }
      foreach (int index in boundaryVertices) {
        if(index>=region.Positions.Length||index>=region.Heights.Length)continue;Vector3 p=region.Positions[index],top=p+new Vector3(0,region.Heights[index],0);
        points.Add(Vector3.TransformCoordinate(p,world));points.Add(Vector3.TransformCoordinate(top,world));
      }
    }

    private void AddVolumeEdges(List<Vector3> points, AssetInstance inst, Room room) {
      Matrix world = InstanceWorld(inst, room);
      float hx = Math.Max(.01f, inst.width * .5f), hy = Math.Max(.01f, inst.height * .5f), hz = Math.Max(.01f, inst.depth * .5f);
      Vector3[] c = new Vector3[8];
      int n = 0;
      for (int y = 0; y < 2; y++) for (int z = 0; z < 2; z++) for (int x = 0; x < 2; x++)
        c[n++] = Vector3.TransformCoordinate(new Vector3(x == 0 ? -hx : hx, y == 0 ? -hy : hy, z == 0 ? -hz : hz), world);
      int[,] edges = { {0,1},{2,3},{4,5},{6,7},{0,2},{1,3},{4,6},{5,7},{0,4},{1,5},{2,6},{3,7} };
      for (int i = 0; i < 12; i++) { points.Add(c[edges[i,0]]); points.Add(c[edges[i,1]]); }
    }

    private void RegisterPhaseRegion(AssetInstance inst, Room room) {
      Matrix world = InstanceWorld(inst, room);
      Matrix inverse;
      try { inverse = Matrix.Invert(world); } catch { return; }
      float halfWidth = Math.Max(.0001f, inst.width * .5f);
      float halfHeight = Math.Max(.0001f, inst.height * .5f);
      float halfDepth = Math.Max(.0001f, inst.depth * .5f);
      float determinant = world.M11 * (world.M22 * world.M33 - world.M23 * world.M32)
        - world.M12 * (world.M21 * world.M33 - world.M23 * world.M31)
        + world.M13 * (world.M21 * world.M32 - world.M22 * world.M31);
      float volume = Math.Max(.000001f, inst.width * inst.height * inst.depth * Math.Abs(determinant));
      phaseRegionTriggers.Add(new PhaseVolumeEntry {
        Room = room, Instance = inst, Inverse = inverse, HalfWidth = halfWidth, HalfHeight = halfHeight, HalfDepth = halfDepth,
        Volume = volume, Ellipsoid = inst.TriggerEllipsoid, Name = PhaseDisplayName(inst.TriggerParam)
      });
    }

    private void AddPhaseGatewayGeometry(List<Vector3> lines, List<Vector3> triangles, AssetInstance inst, Room room) {
      if (inst == null) return;
      // Jedipedia ignores the parser's 64x64 default trigger extent. An INSTANCE_GATEWAY is only useful when
      // at least its horizontal dimensions were actually authored, and absurd extents are editor defaults.
      float width = Math.Abs(inst.width), depth = Math.Abs(inst.depth);
      if (!(width > 0f) || !(depth > 0f) || width > 40f || depth > 40f) return;
      if (!inst.HasWidthProperty && !inst.HasDepthProperty && Math.Abs(width - 64f) < .001f && Math.Abs(depth - 64f) < .001f) return;

      Matrix world = InstanceWorld(inst, room);
      float height = inst.HasHeightProperty && inst.height > 0f ? Math.Min(Math.Abs(inst.height), 40f) : .2f;
      bool alongX = width >= depth;
      float halfSpan = Math.Max(.01f, (alongX ? width : depth) * .5f);
      float halfHeight = Math.Max(.01f, height * .5f);
      Vector3[] local = alongX
        ? new[] { new Vector3(-halfSpan,-halfHeight,0), new Vector3(halfSpan,-halfHeight,0), new Vector3(halfSpan,halfHeight,0), new Vector3(-halfSpan,halfHeight,0) }
        : new[] { new Vector3(0,-halfHeight,-halfSpan), new Vector3(0,-halfHeight,halfSpan), new Vector3(0,halfHeight,halfSpan), new Vector3(0,halfHeight,-halfSpan) };
      Vector3[] c = local.Select(v => Vector3.TransformCoordinate(v, world)).ToArray();
      for (int i = 0; i < 4; i++) { lines.Add(c[i]); lines.Add(c[(i + 1) & 3]); }
      // Add both windings: gateways must be visible when approached from either side of the phase boundary.
      triangles.Add(c[0]); triangles.Add(c[1]); triangles.Add(c[2]);
      triangles.Add(c[0]); triangles.Add(c[2]); triangles.Add(c[3]);
      triangles.Add(c[0]); triangles.Add(c[2]); triangles.Add(c[1]);
      triangles.Add(c[0]); triangles.Add(c[3]); triangles.Add(c[2]);
    }

    private void UpdateCurrentPhase(Vector3 worldPosition) {
      PhaseVolumeEntry best = null;
      foreach (PhaseVolumeEntry trigger in phaseRegionTriggers) {
        if (trigger == null || String.IsNullOrWhiteSpace(trigger.Name)) continue;
        Vector3 local = Vector3.TransformCoordinate(worldPosition, trigger.Inverse);
        float x = Math.Abs(local.X) / trigger.HalfWidth;
        float y = Math.Abs(local.Y) / trigger.HalfHeight;
        float z = Math.Abs(local.Z) / trigger.HalfDepth;
        bool inside = trigger.Ellipsoid ? x * x + y * y + z * z <= 1f : x <= 1f && y <= 1f && z <= 1f;
        if (!inside) continue;
        if (best == null || trigger.Volume < best.Volume) best = trigger;
      }
      currentPhaseName = best?.Name ?? implicitPhaseName ?? String.Empty;
    }

    private static void DisposeLines(List<LineGpu> lines) { foreach (LineGpu line in lines) line?.Dispose(); lines.Clear(); }

    private void DrawJedipediaUtilities(Matrix vp, HashSet<string> visible, WorldRenderSettings s) {
      if (s == null) return;
      bool anyMarkers = s.ShowUtilitySpawners || s.ShowUtilityCoverPoints || s.ShowUtilityLights || s.ShowUtilitySeedPoints || s.ShowUtilityOther;
      if (anyMarkers) {
        ImmediateContext.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
        foreach (UtilityRenderEntry entry in utilityRenderEntries) {
          if (entry?.Instance == null || entry.Model == null || !InstanceRoomVisible(entry.Instance, entry.Room, visible)) continue;
          if (s.Mode == WorldRenderMode.Map ? !InstanceVisibleOnMap(entry.Instance) : !InstanceCanEnterRenderIndex(entry.Instance)) continue;
          if (!UtilityCategoryEnabled(entry.Category, s)) continue;
          Matrix world = InstanceWorld(entry.Instance, entry.Room);
          DrawUtilityMarker(entry.Model, world, vp, entry.Color);
        }
      }
      float range = s.Mode == WorldRenderMode.Map ? float.MaxValue : camera.FarZ;
      if (s.ShowUtilitySpawners) DrawLines(utilitySpawnerFallbackGpu, vp, range);
      if (s.ShowUtilityCoverPoints) DrawLines(utilityCoverFallbackGpu, vp, range);
      if (s.ShowUtilityLights) DrawLines(utilityLightFallbackGpu, vp, range);
      if (s.ShowUtilitySeedPoints) DrawLines(utilitySeedFallbackGpu, vp, range);
      if (s.ShowUtilityPaths) DrawLines(utilityPathGpu, vp, range);
      if (s.ShowUtilityMapRoadPaths) DrawLines(utilityMapPathGpu, vp, range);
      if (s.ShowUtilityConnections) DrawLines(utilityConnectionGpu, vp, range);
      if (s.ShowUtilityVolumes) DrawLines(utilityVolumeGpu, vp, range);
      // A pinned region/trigger remains visible outside Utilities. Pins are changed from the WinForms thread, so
      // keep list mutation/disposal out of the render thread's enumeration.
      if (s.Mode != WorldRenderMode.Map || mapOpen) lock (pinnedVolumeGpu) DrawLines(pinnedVolumeGpu, vp, range);
      // Phase gateways are controlled directly by their spawned-content toggle, independently of utility helpers.
      if (s.ShowPhaseGateways) { DrawOverlayTriangles(phaseGatewayPlaneGpu, vp, range); DrawLines(phaseGatewayGpu, vp, range); }
      if (s.ShowUtilityOther) DrawLines(utilityOtherGpu, vp, range);
    }

    private static bool UtilityCategoryEnabled(byte category, WorldRenderSettings s) {
      if (category == UtilityCover) return s.ShowUtilityCoverPoints;
      if (category == UtilityLight) return s.ShowUtilityLights;
      if (category == UtilitySeed) return s.ShowUtilitySeedPoints;
      if (category == UtilityOther) return s.ShowUtilityOther;
      return s.ShowUtilitySpawners;
    }

    private void DrawUtilityMarker(GR2 model, Matrix world, Matrix vp, Vector4 color) {
      EnsureModelGeometryPrepared(model);
      fx.SetWorld(world); fx.SetViewProj(vp); fx.SetMaterial(null); fx.SetOverlay(color);
      foreach (GR2_Mesh mesh in model.meshes) {
        if (mesh?.vertBuffer == null || mesh.idxBuffer == null || mesh.meshVertIndex == null || mesh.meshVertIndex.Count == 0) continue;
        ImmediateContext.InputAssembler.SetVertexBuffers(0, new VertexBufferBinding(mesh.vertBuffer, PosNormalTexTan.Stride, 0));
        ImmediateContext.InputAssembler.SetIndexBuffer(mesh.idxBuffer, Format.R16_UInt, 0);
        fx.Overlay.GetPassByIndex(0).Apply(ImmediateContext);
        ImmediateContext.DrawIndexed(mesh.meshVertIndex.Count, 0, 0);
      }
    }

    private static bool IsTaxiNpc(WorldNpcPlacement placement) {
      return placement?.IsTaxiTerminal == true;
    }

    private static bool NpcLayerVisible(WorldNpcPlacement placement, WorldRenderSettings s) {
      return s != null && (s.ShowNpcs || (s.ShowTaxiTerminals && IsTaxiNpc(placement)));
    }

    private bool NpcVisibleForWork(Vector3 position) {
      float reach = NpcMaxRenderDistance;
      if (reach > 0f && (position - camera.Position).LengthSquared() > reach * reach) return false;
      // SphereVisibleInCameraFrustum already has one unit of conservative padding. Add the remainder so the
      // effective population leeway matches Jedipedia's two-unit gate.
      return SphereVisibleInCameraFrustum(position, Math.Max(0f, PopulationFrustumLeeway - FrustumCullPadding));
    }

    private void DrawJedipediaNpcs(Matrix vp, HashSet<string> visible, WorldRenderSettings s, AreaEnvironmentScheme cameraEnv, bool sceneShadows) {
      if ((!s.ShowNpcs && !s.ShowTaxiTerminals) || npcPlacements == null || npcPlacements.Count == 0) return;
      EnsureNpcGeometryPrepared();
      Room activeRoom = null;
      foreach (WorldNpcPlacement placement in NearbyNpcPlacements(NpcMaxRenderDistance)) {
        if (!NpcLayerVisible(placement, s)) continue;
        if (placement?.Instance == null || placement.Room == null || placement.Models == null) continue;
        if (!SpnVariantActive(placement.VariantIndex, placement.VariantCount, placement.SpawnPoints)) continue;
        bool hasSpawnMotion = placement.SpawnPoints != null && placement.SpawnPoints.Count > 0;
        // An authored spn_pt spawn position can lie outside the dPVS/static room of the editor handle. Let the
        // population distance/frustum gate decide visibility for those placements instead.
        if (!hasSpawnMotion && !InstanceRoomVisible(placement.Instance, placement.Room, visible)) continue;
        if (!InstanceVisibleInWorld(placement.Instance, s)) continue;

        Matrix baseWorld = NpcPlacementBaseWorld(placement);
        Vector3 rootPosition = new Vector3(baseWorld.M41, baseWorld.M42, baseWorld.M43);
        // This test intentionally happens before animation sampling/CPU skinning. On population-heavy hubs that is
        // the expensive work Jedipedia avoids for the hundreds of characters that are far away or off screen.
        if (s.Mode != WorldRenderMode.Map && !NpcVisibleForWork(rootPosition)) continue;

        // Query result comes from the previous stable depth frame, so hidden NPCs can be skipped before the
        // comparatively expensive animation sampling/CPU skinning work.
        Matrix npcWorld = Matrix.RotationY((float)Math.PI) * baseWorld;
        bool haveReceiver = TryWorldModelsSphere(placement.Models, npcWorld, out Vector3 receiverCenter, out float receiverRadius);
        if (s.Mode == WorldRenderMode.Map && haveReceiver && !MapSphereVisible(receiverCenter, receiverRadius)) continue;
        if (haveReceiver && !DynamicReceiverVisibleByOcclusion(placement, receiverCenter, receiverRadius)) continue;

        if (!ReferenceEquals(activeRoom, placement.Room)) {
          ApplyRoomEnvironment(placement.Room, cameraEnv, s, sceneShadows);
          activeRoom = placement.Room;
        }
        SetNearestLocalLights(rootPosition, placement.Room, s, visible, false, false, true);

        // SWTOR spawner facing is authored opposite to the GR2 character forward axis. Apply the same
        // half-turn to both bind-pose and animated NPCs; nameplate bones are transformed through this matrix too.
        WorldNpcAnimationClip activeClip = placement.Animation;
        bool animate = s.Mode != WorldRenderMode.Map && s.AnimateNpcs && activeClip?.Animation != null &&
          (rootPosition - camera.Position).LengthSquared() <= NpcAnimationRenderDistance * NpcAnimationRenderDistance;
        float animationTime = 0f;
        if (animate) {
          float length = activeClip.Animation.Length;
          animationTime = length > 0f ? ((float)elapsed + placement.AnimationPhase * length) % length : 0f;
          if (animationTime < 0f) animationTime += length;
          // The animated rig will refresh this from attach_nameplate this frame. Clearing it avoids retaining the
          // previous pose when an animation/model fails and is exactly the kind of stale anchor that makes labels swim.
          placement.NameplateLocal = null;
        } else {
          placement.NameplateLocal = NpcBindNameplateLocal(placement);
        }

        foreach (GR2 model in placement.Models) {
          if (!animate || !TryDrawAnimatedNpcModel(model, npcWorld, vp, s, activeClip, animationTime, placement))
            DrawModel(model, npcWorld, vp, s, false, null);
        }
        if (!placement.NameplateLocal.HasValue) placement.NameplateLocal = NpcBindNameplateLocal(placement);
      }
    }

    private void DrawJedipediaSpnObjects(Matrix vp, HashSet<string> visible, WorldRenderSettings s, AreaEnvironmentScheme cameraEnv, bool sceneShadows) {
      if (!s.ShowSpnObjects || spnPlacements == null || spnPlacements.Count == 0) return;
      EnsureSpnGeometryPrepared();
      // The navigation map is intentionally static: animating population/placeables there adds CPU/GPU work but
      // no useful navigation information. The detailed 3D view keeps the user's animation setting unchanged.
      bool animateMotion = s.Mode != WorldRenderMode.Map && s.AnimateSpnObjects;
      foreach (WorldSpnPlacement placement in (s.Mode==WorldRenderMode.Map?MapVisibleSpnPlacements():NearbySpnPlacements(camera.FarZ))) {
        if (placement?.Instance == null || placement.Room == null || placement.Models == null) continue;
        if (!SpnVariantActive(placement.VariantIndex, placement.VariantCount, placement.SpawnPoints)) continue;
        bool hasMotion = placement.Route != null || (placement.SpawnPoints != null && placement.SpawnPoints.Count > 0);
        // A moving platform can leave the static room/handle that authored it. Do not let that source room's coarse
        // visibility gate hide the platform at the other end of its shaft; this is the same exception PugTools already
        // makes for normal path followers and Jedipedia makes for spawnAnimated instances.
        if (!hasMotion && !InstanceRoomVisible(placement.Instance, placement.Room, visible)) continue;
        if (s.Mode == WorldRenderMode.Map ? !InstanceVisibleOnMap(placement.Instance) : !InstanceVisibleInWorld(placement.Instance,s)) continue;

        // Placeables keep the scale/orientation authored on the spawner. Unlike creatures, their placement scale must
        // not be normalized to the template scale. A pth.* route replaces translation only; .spn_pt children also
        // supply their authored relative rotation, matching Jedipedia's viewerSpnPoseAt.
        Matrix world = SpnPlacementWorld(placement, animateMotion);
        Vector3 position = new Vector3(world.M41, world.M42, world.M43);
        WorldSpnDynState dynState = ActiveSpnDynState(placement, animateMotion);
        if (dynState != null && dynState.Hidden) continue;
        bool haveReceiver = TrySpnReceiverSphere(placement, world, dynState, out Vector3 receiverCenter, out float receiverRadius);
        // Static room visibility is not enough for moving lifts/platforms because their authored handle may remain in
        // a visible room while the object itself is kilometres away. Cull against the actual receiver before doing
        // material lookup, animation or draw work. Map mode keeps its separate orthographic visibility semantics.
        if (s.Mode != WorldRenderMode.Map && haveReceiver &&
            (!SphereWithinDistance(receiverCenter, receiverRadius, camera.FarZ) || !SphereVisibleInCameraFrustum(receiverCenter, receiverRadius))) continue;
        if (s.Mode == WorldRenderMode.Map && haveReceiver && !MapSphereVisible(receiverCenter, receiverRadius)) continue;
        if (haveReceiver && !DynamicReceiverVisibleByOcclusion(placement, receiverCenter, receiverRadius, dynState)) continue;
        ApplyRoomEnvironment(placement.Room, cameraEnv, s, sceneShadows);

        if (dynState != null) {
          foreach (WorldSpnDynPart part in dynState.Parts) {
            if (part?.Model == null) continue;
            Matrix partWorld = part.LocalMatrix * world;
            Vector3 lightPoint = new Vector3(partWorld.M41, partWorld.M42, partWorld.M43);
            float lightRadius = 0f;
            if (TryModelSphere(part.Model, partWorld, out Vector3 partCenter, out float partRadius)) {
              lightPoint = partCenter; lightRadius = Math.Max(0f, partRadius);
            }
            SetNearestLocalLights(lightPoint, placement.Room, s, visible, false, false, false, lightRadius);

            WorldNpcAnimationClip partClip = part.Animation;
            bool animatePart = animateMotion && partClip?.Animation != null;
            float partTime = 0f;
            if (animatePart) {
              float length = partClip.Animation.Length;
              partTime = length > 0f ? (elapsed + placement.AnimationPhase * length) % length : 0f;
              if (partTime < 0f) partTime += length;
            }
            bool blueGlow = s.ShowPlaceableGlow && part.BlueGlow;
            if (!animatePart || !TryDrawAnimatedNpcModel(part.Model, partWorld, vp, s, partClip, partTime, null, blueGlow))
              DrawModel(part.Model, partWorld, vp, s, false, null, -1, blueGlow);
          }
          continue;
        }

        SetNearestLocalLights(position, placement.Room, s, visible, false);
        bool animateModel = animateMotion && placement.Animation?.Animation != null;
        float animationTime = 0f;
        if (animateModel) {
          float length = placement.Animation.Animation.Length;
          animationTime = length > 0f ? (elapsed + placement.AnimationPhase * length) % length : 0f;
          if (animationTime < 0f) animationTime += length;
        }
        bool placementBlueGlow = s.ShowPlaceableGlow && placement.BlueGlow;
        foreach (GR2 model in placement.Models) {
          if (!animateModel || !TryDrawAnimatedNpcModel(model, world, vp, s, placement.Animation, animationTime, null, placementBlueGlow))
            DrawModel(model, world, vp, s, false, null, -1, placementBlueGlow);
        }
      }
    }

    private Matrix NpcNameplateWorld(WorldNpcPlacement placement) {
      Matrix baseWorld = NpcPlacementBaseWorld(placement);
      return Matrix.RotationY((float)Math.PI) * baseWorld;
    }

    private Matrix NpcPlacementBaseWorld(WorldNpcPlacement placement) {
      Matrix baseWorld = InstanceWorld(placement.Instance, placement.Room);
      if (placement?.SpawnPoints != null && placement.SpawnPoints.Count > 0) {
        // pth.* belongs to placeables in Jedipedia/SWTOR's spawner path. NPCs can have spn_pt dispenser positions,
        // but treating a placeable route as creature locomotion is what caused the extreme back-and-forth sprinting.
        // Keep one deterministic authored spawn pose stable for the preview instead.
        baseWorld = NpcSpawnPointWorld(baseWorld, placement);
      }
      return ApplyNpcScale(baseWorld, placement.Scale);
    }

    private Matrix NpcSpawnPointWorld(Matrix baseWorld, WorldNpcPlacement placement) {
      IList<WorldSpawnPointPose> points = placement?.SpawnPoints;
      if (points == null || points.Count == 0) return baseWorld;
      // spn_pt children are candidate dispenser locations. Cycling a single NPC through them made the viewer look as
      // if population was sprinting/teleporting around a room even though no creature locomotion was authored. Pick one
      // stable point per spawner instead; pth.* movement remains reserved for placeables.
      unchecked {
        ulong seed = placement.Instance?.ID ?? 0UL;
        int index = (int)(seed % (ulong)points.Count);
        WorldSpawnPointPose point = points[index];
        return SpawnPointWorld(baseWorld, point, point.Position);
      }
    }

    private static void SetNpcWorldYaw(ref Matrix world, float yaw) {
      float sx = (float)Math.Sqrt(world.M11 * world.M11 + world.M12 * world.M12 + world.M13 * world.M13);
      float sy = (float)Math.Sqrt(world.M21 * world.M21 + world.M22 * world.M22 + world.M23 * world.M23);
      float sz = (float)Math.Sqrt(world.M31 * world.M31 + world.M32 * world.M32 + world.M33 * world.M33);
      if (!(sx > .000001f)) sx = 1f; if (!(sy > .000001f)) sy = 1f; if (!(sz > .000001f)) sz = 1f;
      Matrix rotation = Matrix.RotationY(yaw);
      world.M11 = rotation.M11 * sx; world.M12 = rotation.M12 * sx; world.M13 = rotation.M13 * sx;
      world.M21 = rotation.M21 * sy; world.M22 = rotation.M22 * sy; world.M23 = rotation.M23 * sy;
      world.M31 = rotation.M31 * sz; world.M32 = rotation.M32 * sz; world.M33 = rotation.M33 * sz;
    }

    private Matrix SpawnPointCycleWorld(Matrix baseWorld, IList<WorldSpawnPointPose> points) {
      if (points == null || points.Count == 0) return baseWorld;
      float step = SpnSpawnPointDwellSeconds + SpnSpawnPointTravelSeconds;
      float cycle = step * points.Count;
      float phase = cycle > 0f ? (float)(elapsed % cycle) : 0f;
      if (phase < 0f) phase += cycle;
      int index = Math.Min(points.Count - 1, (int)(phase / step));
      float into = phase - index * step;
      WorldSpawnPointPose point = points[index];
      if (into < SpnSpawnPointDwellSeconds || points.Count == 1)
        return SpawnPointWorld(baseWorld, point, point.Position);

      int nextIndex = (index + 1) % points.Count;
      WorldSpawnPointPose next = points[nextIndex];
      float t = Math.Max(0f, Math.Min(1f, (into - SpnSpawnPointDwellSeconds) / SpnSpawnPointTravelSeconds));
      Vector3 position = Vector3.Lerp(point.Position, next.Position, t);
      // Jedipedia snaps to the destination point's authored rotation for the whole short slide.
      return SpawnPointWorld(baseWorld, next, position);
    }

    private static Matrix SpawnPointWorld(Matrix baseWorld, WorldSpawnPointPose point, Vector3 position) {
      Matrix world = (point?.Rotation ?? Matrix.Identity) * baseWorld;
      world.M41 = position.X; world.M42 = position.Y; world.M43 = position.Z;
      return world;
    }

    private static int NpcNameplateBoneIndex(IList<GR2_Bone_Skeleton> skeleton) {
      if (skeleton == null) return -1;
      foreach (string wanted in NpcNameplateBoneNames)
        for (int i = 0; i < skeleton.Count; i++)
          if (String.Equals(skeleton[i]?.boneName, wanted, StringComparison.OrdinalIgnoreCase)) return i;
      return -1;
    }

    private static Vector3? NpcNameplateLocalFromSkin(NpcSkinState state) {
      int index = NpcNameplateBoneIndex(state?.Skeleton);
      if (index < 0 || state.CurrentValid == null || index >= state.CurrentValid.Length || !state.CurrentValid[index]) return null;
      Matrix bone = state.CurrentWorld[index];
      return IsFinite(new Vector3(bone.M41, bone.M42, bone.M43)) ? new Vector3(bone.M41, bone.M42, bone.M43) : (Vector3?)null;
    }

    private static Vector3? NpcNameplateLocalFromBind(IList<GR2_Bone_Skeleton> skeleton) {
      int index = NpcNameplateBoneIndex(skeleton);
      if (index < 0) return null;
      Matrix bone = skeleton[index].root; // rootToBoneRaw inverted by the GR2 reader = bind-space bone transform.
      Vector3 point = new Vector3(bone.M41, bone.M42, bone.M43);
      return IsFinite(point) ? point : (Vector3?)null;
    }

    private static Vector3? NpcBindNameplateLocal(WorldNpcPlacement placement) {
      IList<GR2_Bone_Skeleton> skeleton = placement?.Animation?.Skeleton;
      Vector3? result = NpcNameplateLocalFromBind(skeleton);
      if (result.HasValue || placement?.Models == null) return result;
      foreach (GR2 model in placement.Models) {
        result = NpcNameplateLocalFromBind(model?.skeleton_bones);
        if (result.HasValue) return result;
      }
      return null;
    }

    private static float NpcNameplateClearLocalY(WorldNpcPlacement placement, float anchorY) {
      float clear = anchorY;
      if (placement?.Models == null) return clear;
      foreach (GR2 model in placement.Models) {
        GR2_Bounding_Box box = model?.globalBox;
        if (box != null && Single.IsFinite(box.maxY)) clear = Math.Max(clear, box.maxY);
      }
      return clear;
    }

    private bool TryDrawAnimatedNpcModel(GR2 model, Matrix world, Matrix vp, WorldRenderSettings s, WorldNpcAnimationClip clip, float animationTime, WorldNpcPlacement nameplatePlacement = null, bool blueGlow = false) {
      EnsureModelGeometryPrepared(model);
      NpcSkinState state = GetNpcSkinState(model, clip);
      if (state == null || state.BoundBoneCount <= 0) return false;
      if (!UpdateNpcSkinState(state, animationTime)) return false;
      if (nameplatePlacement != null && !nameplatePlacement.NameplateLocal.HasValue) {
        Vector3? local = NpcNameplateLocalFromSkin(state);
        if (local.HasValue) nameplatePlacement.NameplateLocal = local.Value;
      }
      DrawNpcModelWithSkinBuffers(model, state, world, vp, s, blueGlow);
      return true;
    }

    private bool SpnVariantActive(int variantIndex, int variantCount, IList<WorldSpawnPointPose> points) {
      if (variantCount <= 1) return true;
      int index;
      if (points != null && points.Count > 0) {
        float step = SpnSpawnPointDwellSeconds + SpnSpawnPointTravelSeconds;
        double time = elapsed;
        long hop = (long)Math.Floor(time / step);
        double into = time - hop * step;
        // Jedipedia changes alternative half-way through the short move so a standing entity never visibly pops.
        if (into >= SpnSpawnPointDwellSeconds + SpnSpawnPointTravelSeconds * .5f) hop++;
        index = (int)(hop % variantCount);
      } else {
        index = (int)Math.Floor(elapsed / SpnVariantSeconds) % variantCount;
      }
      if (index < 0) index += variantCount;
      return variantIndex == index;
    }

    private WorldSpnDynState ActiveSpnDynState(WorldSpnPlacement placement, bool animateStates = true) {
      if (placement?.DynStates == null || placement.DynStates.Count == 0) return null;
      if (!animateStates || placement.DynStates.Count == 1) return placement.DynStates[0];
      int index = (int)Math.Floor(elapsed / SpnVariantSeconds) % placement.DynStates.Count;
      if (index < 0) index += placement.DynStates.Count;
      return placement.DynStates[index];
    }

    private bool SpnDynLightEnabled(WorldSpnPlacement placement, WorldSpnDynLight definition, bool animateStates) {
      if (definition == null) return false;
      if (definition.StateVisibility == null || definition.StateVisibility.Count == 0) return true;
      WorldSpnDynState state = ActiveSpnDynState(placement, animateStates);
      string name = state?.Name;
      if (String.IsNullOrWhiteSpace(name)) return true;
      return !definition.StateVisibility.TryGetValue(name, out bool visible) || visible;
    }

    // Called before terrain/models so DYN lights illuminate ordinary room geometry too, not only the SPN object that
    // contributed them. The static light grid cannot hold these because their matrices can change every frame.
    private void UpdateJedipediaDynamicLights(WorldRenderSettings s) {
      bool hadDynamicLights = dynamicSpnLocalLights.Count > 0;
      dynamicSpnLocalLights.Clear();
      if (s == null || !s.ShowSpnObjects || !s.EnableLocalLights || spnPlacements == null || spnPlacements.Count == 0) {
        // Do not throw away the static receiver-light cache every frame just because a world contains SPNs. Only an
        // actually active dynamic light can make those cached selections stale. This is a major CPU win in hubs.
        if (hadDynamicLights) {
          localLightSelectionCache.Clear();
          lastLocalLightCount = -1;
          Array.Clear(lastLocalLightSelection, 0, lastLocalLightSelection.Length);
        }
        return;
      }

      foreach (WorldSpnPlacement placement in NearbySpnPlacements(camera.FarZ+25f)) {
        if (placement?.Instance == null || placement.Room == null || placement.DynLights == null || placement.DynLights.Count == 0) continue;
        if (!SpnVariantActive(placement.VariantIndex, placement.VariantCount, placement.SpawnPoints)) continue;
        WorldSpnDynState activeState = ActiveSpnDynState(placement, s.AnimateSpnObjects);
        if (activeState != null && activeState.Hidden) continue;

        Matrix placementWorld;
        try { placementWorld = SpnPlacementWorld(placement, s.AnimateSpnObjects); } catch { continue; }
        foreach (WorldSpnDynLight definition in placement.DynLights) {
          if (!SpnDynLightEnabled(placement, definition, s.AnimateSpnObjects)) continue;
          Matrix m = definition.LocalMatrix * placementWorld;
          Vector3 pos = new Vector3(m.M41, m.M42, m.M43);
          Vector3 direction = Vector3.TransformNormal(new Vector3(0, 0, 1), m);
          if (direction.LengthSquared() < .0001f) direction = new Vector3(0, -1, 0); else direction.Normalize();

          bool directional = String.Equals(definition.LightType, "DIRECTIONAL", StringComparison.OrdinalIgnoreCase);
          float range = Math.Max(.0001f, definition.Range);
          float sx = (float)Math.Sqrt(m.M11 * m.M11 + m.M12 * m.M12 + m.M13 * m.M13) * range;
          float sy = (float)Math.Sqrt(m.M21 * m.M21 + m.M22 * m.M22 + m.M23 * m.M23) * range;
          float sz = (float)Math.Sqrt(m.M31 * m.M31 + m.M32 * m.M32 + m.M33 * m.M33) * range;
          float broadRange = Math.Max(.0001f, Math.Max(sx, Math.Max(sy, sz)));

          float type = directional ? 1f : (String.Equals(definition.LightType, "SPOT", StringComparison.OrdinalIgnoreCase) ? 2f : 0f);
          dynamicSpnLocalLights.Add(new LocalLightEntry {
            Room = placement.Room,
            Instance = placement.Instance,
            Position = pos,
            RangeSquared = broadRange * broadRange,
            Directional = directional,
            RestrictToRoom = definition.RestrictToRoom,
            DoHeightmaps = definition.DoHeightmaps,
            DoGranny = definition.DoGranny,
            DoCharacters = definition.DoCharacters,
            DoWater = definition.DoWater,
            PosRange = new Vector4(pos, broadRange),
            ColorIntensity = new Vector4(definition.Color.X, definition.Color.Y, definition.Color.Z, definition.Intensity),
            DirType = new Vector4(direction, type),
            ProjectorInv = BuildLocalLightProjectorInverse(m, range),
            IlluminationPath = NormalizeTexturePath(definition.IlluminationMap),
            FalloffPath = NormalizeTexturePath(definition.Falloff),
            RampPath = NormalizeTexturePath(definition.RampMap),
            ProjectorParams = new Vector4(definition.SourceOffset, 0, 0, 0)
          });
        }
      }

      // Positions/state membership can change every frame. Reusing a cell selection would keep a lift's old light at
      // the old floor or leave an "Off" lamp contributing until some unrelated cache invalidation occurs. Static-only
      // worlds retain their cache; dynamic worlds still invalidate exactly as before.
      if (hadDynamicLights || dynamicSpnLocalLights.Count > 0) {
        localLightSelectionCache.Clear();
        lastLocalLightCount = -1;
        Array.Clear(lastLocalLightSelection, 0, lastLocalLightSelection.Length);
      }
    }

    private Matrix SpnPlacementWorld(WorldSpnPlacement placement, bool animateMotion) {
      Matrix baseWorld = InstanceWorld(placement.Instance, placement.Room);
      if (!animateMotion) return baseWorld;

      SpnMotionRoute route = GetSpnMotionRoute(placement);
      if (route != null) {
        Vector3 position = SpnRouteSample(route, SpnRouteTime(route, SpnSyncTimeSeconds()));
        baseWorld.M41 = position.X; baseWorld.M42 = position.Y; baseWorld.M43 = position.Z;
        return baseWorld;
      }

      if (placement.SpawnPoints != null && placement.SpawnPoints.Count > 0)
        return SpawnPointCycleWorld(baseWorld, placement.SpawnPoints);
      return baseWorld;
    }

    private SpnMotionRoute GetSpnMotionRoute(WorldSpnPlacement placement) {
      if (placement?.Route == null || placement.Route.Points == null || placement.Route.Points.Count < 2) return null;
      if (spnMotionRoutes.TryGetValue(placement, out SpnMotionRoute cached)) return cached;
      SpnMotionRoute built = BuildSpnMotionRoute(placement.Route, placement.TraversalStyle);
      spnMotionRoutes[placement] = built;
      return built;
    }

    private SpnMotionRoute GetNpcMotionRoute(WorldNpcPlacement placement) {
      if (placement?.Route == null || placement.Route.Points == null || placement.Route.Points.Count < 2) return null;
      if (npcMotionRoutes.TryGetValue(placement, out SpnMotionRoute cached)) return cached;
      SpnMotionRoute built = BuildSpnMotionRoute(placement.Route, placement.TraversalStyle);
      npcMotionRoutes[placement] = built;
      return built;
    }

    private static SpnMotionRoute BuildSpnMotionRoute(AreaPath path, int style) {
      List<SpnMotionPoint> points = FlattenSpnMotionPoints(path);
      if (style == 2) points.Reverse();
      if (points.Count < 2) return null;

      var route = new SpnMotionRoute { Alternating = style == 3 };
      route.Points.AddRange(points);
      bool circuit = !route.Alternating && (style == 4 || path.Circular);
      float timeline = 0f;

      Action<int, int, float> add = (from, to, duration) => {
        if (!(duration > 0f) || Single.IsNaN(duration) || Single.IsInfinity(duration)) return;
        route.Events.Add(new SpnMotionEvent { From = from, To = to, Start = timeline, Duration = duration });
        timeline += duration;
      };
      Func<int, int, float> travel = (from, to) => {
        float length = (points[to].Position - points[from].Position).Length();
        float speed = (SafeSpnSpeed(points[from].Speed) + SafeSpnSpeed(points[to].Speed)) * .5f;
        return speed > 0f ? length / speed : 0f;
      };

      for (int i = 0; i < points.Count; i++) {
        float hold = Math.Max(0f, points[i].HoldTime);
        if (route.Alternating && (i == 0 || i == points.Count - 1)) hold *= .5f;
        add(i, -1, hold);
        if (i + 1 < points.Count) add(i, i + 1, travel(i, i + 1));
      }
      if (circuit) add(points.Count - 1, 0, travel(points.Count - 1, 0));
      if (timeline <= 0f) return null;
      route.LegDuration = timeline;
      return route;
    }

    private static List<SpnMotionPoint> FlattenSpnMotionPoints(AreaPath path) {
      var result = new List<SpnMotionPoint>();
      if (path?.Points == null || path.Points.Count == 0) return result;
      List<AreaPathPoint> points = path.Points;
      Func<AreaPathPoint, SpnMotionPoint> control = point => new SpnMotionPoint {
        Position = point.Position, Speed = SafeSpnSpeed(point.Speed), HoldTime = Math.Max(0f, point.HoldTime)
      };

      if (!path.Smooth || points.Count < 3) {
        foreach (AreaPathPoint point in points) result.Add(control(point));
        if (path.Circular && points.Count > 1) result.Add(control(points[0]));
        return result;
      }

      Vector3[] tangents = new Vector3[points.Count];
      for (int i = 0; i < points.Count; i++) tangents[i] = PathCurveTangent(points, i, path.Circular);
      result.Add(control(points[0]));
      int segmentCount = points.Count - 1 + (path.Circular ? 1 : 0);
      for (int i = 0; i < segmentCount; i++) {
        int next = (i + 1) % points.Count;
        Vector3 a = points[i].Position, b = points[next].Position;
        int steps = PathCurveSteps(a, b, tangents[i], tangents[next]);
        for (int step = 1; step < steps; step++) {
          float t = step / (float)steps;
          float speedA = SafeSpnSpeed(points[i].Speed), speedB = SafeSpnSpeed(points[next].Speed);
          result.Add(new SpnMotionPoint {
            Position = PathHermite(a, b, tangents[i], tangents[next], t),
            Speed = speedA + (speedB - speedA) * t,
            HoldTime = 0f
          });
        }
        result.Add(control(points[next]));
      }
      return result;
    }

    private static float SafeSpnSpeed(float speed) {
      return speed > 0f && !Single.IsNaN(speed) && !Single.IsInfinity(speed) ? speed : 1f;
    }

    private static float SpnSyncTimeSeconds() {
      long milliseconds = DateTime.UtcNow.ToFileTimeUtc() / TimeSpan.TicksPerMillisecond;
      return (milliseconds % 0x1000000L) / 1000f;
    }

    private static float SpnRouteTime(SpnMotionRoute route, float timeSeconds) {
      if (route == null || route.LegDuration <= 0f) return 0f;
      float total = route.Alternating ? route.LegDuration * 2f : route.LegDuration;
      float t = timeSeconds % total;
      if (t < 0f) t += total;
      return route.Alternating && t > route.LegDuration ? total - t : t;
    }

    private static Vector3 SpnRouteSample(SpnMotionRoute route, float time) {
      if (route?.Events == null || route.Events.Count == 0 || route.Points.Count == 0) return Vector3.Zero;
      SpnMotionEvent selected = route.Events[route.Events.Count - 1];
      foreach (SpnMotionEvent candidate in route.Events) {
        if (time < candidate.Start + candidate.Duration) { selected = candidate; break; }
      }
      Vector3 from = route.Points[selected.From].Position;
      if (selected.To < 0) return from;
      Vector3 to = route.Points[selected.To].Position;
      float t = Math.Max(0f, Math.Min(1f, (time - selected.Start) / selected.Duration));
      return Vector3.Lerp(from, to, t);
    }

    private NpcSkinState GetNpcSkinState(GR2 model, WorldNpcAnimationClip clip) {
      IList<GR2_Bone_Skeleton> animationSkeleton = clip?.Skeleton != null && clip.Skeleton.Count > 0 ? clip.Skeleton : model?.skeleton_bones;
      if (model == null || clip?.Animation == null || animationSkeleton == null || animationSkeleton.Count == 0 || Device == null) return null;
      if (!npcSkinStates.TryGetValue(model, out Dictionary<WorldNpcAnimationClip, NpcSkinState> byClip)) {
        byClip = new Dictionary<WorldNpcAnimationClip, NpcSkinState>();
        npcSkinStates[model] = byClip;
      }
      if (byClip.TryGetValue(clip, out NpcSkinState cached)) {
        if (cached != null) cached.LastUseFrame = worldRenderFrame;
        return cached;
      }

      NpcSkinState state = BuildNpcSkinState(model, clip);
      if (state != null) state.LastUseFrame = worldRenderFrame;
      byClip[clip] = state;
      return state;
    }

    private void TrimNpcSkinStateResidency() {
      // Animated NPC states own pose arrays plus a GPU skinning VBO. Keeping one for every character appearance
      // encountered during a long camera tour recreates the RAM/VRAM growth we avoid for static GR2 geometry.
      // Evict only cold states; a visible animated NPC touches its state every frame and is never removed.
      const int highWater = 48, target = 28;
      const long idleFrames = 360;
      var resident = npcSkinStates
        .SelectMany(modelPair => modelPair.Value.Select(clipPair => (Model: modelPair.Key, Clip: clipPair.Key, State: clipPair.Value)))
        .Where(item => item.State != null)
        .OrderBy(item => item.State.LastUseFrame)
        .ToList();
      if (resident.Count <= highWater) return;
      int remaining = resident.Count;
      long cutoff = worldRenderFrame - idleFrames;
      foreach (var item in resident) {
        if (remaining <= target) break;
        if (item.State.LastUseFrame > cutoff || item.State.LastUseFrame >= worldRenderFrame - 1) continue;
        if (!npcSkinStates.TryGetValue(item.Model, out Dictionary<WorldNpcAnimationClip, NpcSkinState> byClip)) continue;
        if (!byClip.TryGetValue(item.Clip, out NpcSkinState current) || !ReferenceEquals(current, item.State)) continue;
        byClip.Remove(item.Clip);
        item.State.Dispose();
        remaining--;
        if (byClip.Count == 0) npcSkinStates.Remove(item.Model);
      }
    }

    private NpcSkinState BuildNpcSkinState(GR2 model, WorldNpcAnimationClip clip) {
      try {
        IList<GR2_Bone_Skeleton> skeleton = clip?.Skeleton != null && clip.Skeleton.Count > 0 ? clip.Skeleton : model.skeleton_bones;
        int count = skeleton.Count;
        var state = new NpcSkinState {
          Model = model,
          Clip = clip,
          Skeleton = skeleton,
          PoseSamples = new JBATransform[Math.Max(0, clip.Animation.BoneCount)],
          InverseBind = new Matrix[count],
          CurrentWorld = new Matrix[count],
          CurrentValid = new bool[count],
          BindRotationMorpheme = new System.Numerics.Quaternion[count],
          BindTranslationMorpheme = new System.Numerics.Vector3[count],
          WorldRotationMorpheme = new System.Numerics.Quaternion[count],
          WorldTranslationMorpheme = new System.Numerics.Vector3[count]
        };

        for (int i = 0; i < count; i++) {
          GR2_Bone_Skeleton bone = skeleton[i];
          state.InverseBind[i] = bone.rootToBoneRaw;
          Matrix bindWorld = bone.root;
          Matrix bindLocal;
          int parent = bone.parentBoneIndex;
          if (parent >= 0 && parent < i) {
            Matrix parentInverseBind = skeleton[parent].rootToBoneRaw;
            Matrix.Multiply(ref bindWorld, ref parentInverseBind, out bindLocal);
          } else bindLocal = bindWorld;

          if (!NpcTryJbaBindLocalMorpheme(bindLocal, out state.BindRotationMorpheme[i], out state.BindTranslationMorpheme[i])) {
            state.BindRotationMorpheme[i] = System.Numerics.Quaternion.Identity;
            state.BindTranslationMorpheme[i] = System.Numerics.Vector3.Zero;
          }
        }

        NpcBuildChannelBinding(clip.Animation, clip.Rig, skeleton, out state.SkeletonToChannel, out state.UseBindTranslation);
        state.BoundBoneCount = state.SkeletonToChannel.Count(x => x >= 0);
        if (state.BoundBoneCount <= 0) return state;

        foreach (GR2_Mesh mesh in model.meshes) {
          if (mesh?.meshVerts == null || mesh.meshVerts.Count == 0 || mesh.meshBones == null || mesh.meshBones.Count == 0) continue;
          state.Palettes[mesh] = new Matrix[mesh.meshBones.Count];
          if (mesh.meshBones.Count <= 256 && skinnedLayout != null) {
            // Same immutable vertex format used by the GR2 viewer. Per-frame work is now only the small bone
            // palette upload; vertex skinning stays on D3D11 instead of rebuilding and mapping an NPC VBO.
            var gpuVertices = new PosNormalTexTanSkinned[mesh.meshVerts.Count];
            for (int i = 0; i < mesh.meshVerts.Count; i++) {
              GR2_Mesh_Vertex vertex = mesh.meshVerts[i];
              gpuVertices[i] = new PosNormalTexTanSkinned(
                new Vector3(vertex.X, vertex.Y, vertex.Z),
                new Vector3(vertex.normX, vertex.normY, vertex.normZ),
                new Vector2(vertex.texU, vertex.texV),
                new Vector3(vertex.tanX, vertex.tanY, vertex.tanZ),
                new Vector4(vertex.boneWeight1, vertex.boneWeight2, vertex.boneWeight3, vertex.boneWeight4),
                NpcBoneIndexByte(vertex.boneIndex1), NpcBoneIndexByte(vertex.boneIndex2),
                NpcBoneIndexByte(vertex.boneIndex3), NpcBoneIndexByte(vertex.boneIndex4));
            }
            var description = new BufferDescription(
              checked(PosNormalTexTanSkinned.Stride * gpuVertices.Length), ResourceUsage.Immutable, BindFlags.VertexBuffer,
              CpuAccessFlags.None, ResourceOptionFlags.None, 0);
            using (var stream = new DataStream(gpuVertices, false, false)) state.GpuBuffers[mesh] = new Buffer(Device, stream, description);
          } else {
            // Extremely unusual >256-bone meshes retain the proven CPU fallback.
            var description = new BufferDescription(
              checked(PosNormalTexTan.Stride * mesh.meshVerts.Count), ResourceUsage.Dynamic, BindFlags.VertexBuffer,
              CpuAccessFlags.Write, ResourceOptionFlags.None, 0);
            state.DynamicBuffers[mesh] = new Buffer(Device, description);
            state.SkinnedVertices[mesh] = new PosNormalTexTan[mesh.meshVerts.Count];
          }
        }
        return state;
      } catch (Exception ex) {
        System.Diagnostics.Debug.WriteLine("NPC JBA state build failed: " + ex.Message);
        return null;
      }
    }

    private bool UpdateNpcSkinState(NpcSkinState state, float animationTime) {
      if (state?.Clip?.Animation == null || state.PoseSamples == null || state.BoundBoneCount <= 0) return false;
      state.LastUseFrame = worldRenderFrame;
      try {
        state.Clip.Animation.SampleInto(animationTime, state.PoseSamples);
        NpcBuildJbaWorldPose(state, state.PoseSamples);
        state.SkinMatrices.Clear();
        IList<GR2_Bone_Skeleton> skeleton = state.Skeleton;
        for (int i = 0; i < skeleton.Count; i++) {
          if (!state.CurrentValid[i]) continue;
          Matrix inverseBind = state.InverseBind[i];
          Matrix current = state.CurrentWorld[i];
          Matrix.Multiply(ref inverseBind, ref current, out Matrix skin);
          if (!NpcFinite(skin)) continue;
          state.SkinMatrices[NpcCanonicalAnimationBoneName(skeleton[i].boneName)] = skin;
        }
        if (state.SkinMatrices.Count == 0) return false;

        foreach (var pair in state.Palettes) {
          GR2_Mesh mesh = pair.Key;
          Matrix[] palette = pair.Value;
          for (int i = 0; i < palette.Length; i++) {
            string boneName = NpcCanonicalAnimationBoneName(mesh.meshBones[i].boneName);
            palette[i] = state.SkinMatrices.TryGetValue(boneName, out Matrix skin) ? skin : Matrix.Identity;
          }
        }

        // Only the >256-bone fallback still touches vertices on the CPU. Normal SWTOR character meshes are
        // immutable GPU buffers and consume just one small matrix-palette update at draw time.
        foreach (var pair in state.DynamicBuffers) {
          GR2_Mesh mesh = pair.Key;
          Buffer buffer = pair.Value;
          Matrix[] palette = state.Palettes[mesh];
          PosNormalTexTan[] skinned = state.SkinnedVertices[mesh];
          for (int i = 0; i < mesh.meshVerts.Count; i++) {
            GR2_Mesh_Vertex src = mesh.meshVerts[i];
            Vector3 original = new Vector3(src.X, src.Y, src.Z);
            Vector3 position = Vector3.Zero;
            float totalWeight = 0f;
            NpcApplySkinWeight(original, src.boneWeight1, src.boneIndex1, palette, ref position, ref totalWeight);
            NpcApplySkinWeight(original, src.boneWeight2, src.boneIndex2, palette, ref position, ref totalWeight);
            NpcApplySkinWeight(original, src.boneWeight3, src.boneIndex3, palette, ref position, ref totalWeight);
            NpcApplySkinWeight(original, src.boneWeight4, src.boneIndex4, palette, ref position, ref totalWeight);
            if (totalWeight <= .00001f) position = original;
            else if (Math.Abs(totalWeight - 1f) > .0001f) position /= totalWeight;
            skinned[i] = new PosNormalTexTan(position, new Vector3(src.normX, src.normY, src.normZ),
              new Vector2(src.texU, src.texV), new Vector3(src.tanX, src.tanY, src.tanZ));
          }
          DataBox mapped = ImmediateContext.MapSubresource(buffer, MapMode.WriteDiscard, SlimDX.Direct3D11.MapFlags.None);
          mapped.Data.WriteRange(skinned); ImmediateContext.UnmapSubresource(buffer, 0);
        }
        return state.GpuBuffers.Count > 0 || state.DynamicBuffers.Count > 0;
      } catch (Exception ex) {
        System.Diagnostics.Debug.WriteLine("NPC JBA CPU skinning failed: " + ex.Message);
        return false;
      }
    }

    private static byte NpcBoneIndexByte(float normalizedIndex) {
      int value=(int)Math.Round(normalizedIndex*255f);
      if(value<0)value=0;else if(value>255)value=255;
      return (byte)value;
    }

    private static void NpcApplySkinWeight(Vector3 original, float weight, float encodedBoneIndex, Matrix[] palette, ref Vector3 position, ref float totalWeight) {
      if (weight <= .00001f || palette == null) return;
      int boneIndex = (int)Math.Round(encodedBoneIndex * 255f);
      if (boneIndex < 0 || boneIndex >= palette.Length) return;
      position += Vector3.TransformCoordinate(original, palette[boneIndex]) * weight;
      totalWeight += weight;
    }

    private bool BindNpcSkinMesh(NpcSkinState state, GR2_Mesh mesh, out bool gpuSkinned) {
      gpuSkinned=false;
      if(state==null||mesh==null||mesh.idxBuffer==null)return false;
      if(skinnedLayout!=null&&state.GpuBuffers.TryGetValue(mesh,out Buffer gpuBuffer)&&gpuBuffer!=null&&
          state.Palettes.TryGetValue(mesh,out Matrix[] palette)&&palette!=null&&palette.Length>0&&palette.Length<=256) {
        gpuSkinned=true;
        fx.SetSkinPalette(palette);
        ImmediateContext.InputAssembler.InputLayout=skinnedLayout;
        ImmediateContext.InputAssembler.SetVertexBuffers(0,new VertexBufferBinding(gpuBuffer,PosNormalTexTanSkinned.Stride,0));
      } else {
        Buffer vertexBuffer=state.DynamicBuffers.TryGetValue(mesh,out Buffer dynamicBuffer)?dynamicBuffer:mesh.vertBuffer;
        if(vertexBuffer==null)return false;
        ImmediateContext.InputAssembler.InputLayout=inputLayout;
        ImmediateContext.InputAssembler.SetVertexBuffers(0,new VertexBufferBinding(vertexBuffer,PosNormalTexTan.Stride,0));
      }
      ImmediateContext.InputAssembler.SetIndexBuffer(mesh.idxBuffer,Format.R16_UInt,0);
      return true;
    }

    private void DrawNpcModelWithSkinBuffers(GR2 model, NpcSkinState state, Matrix world, Matrix vp, WorldRenderSettings s, bool blueGlow = false) {
      if(model==null)return;
      int selectedLod=SelectModelLodLevel(model,world,s.Mode==WorldRenderMode.Map);
      fx.SetWorld(world);fx.SetViewProj(vp);fx.SetPlaceableBlueGlow(blueGlow&&s.Mode!=WorldRenderMode.Map);
      try {
        foreach(GR2_Mesh mesh in model.meshes) {
          if(!MeshVisibleForLod(model,mesh,selectedLod)||!BindNpcSkinMesh(state,mesh,out bool gpuSkinned))continue;
          foreach(GR2_Mesh_Piece piece in mesh.meshPieces) {
            // Animated characters bypass DrawModel(), so they must participate in the same lazy MAT residency path.
            GR2_Material mat=ResolvePieceMaterial(model,piece);
            if(IsMaterialHiddenFromWorld(mat,s))continue;
            fx.SetMaterial(mat);
            (gpuSkinned?PickSkinnedModelTech(s,mat):PickModelTech(s,mat,false)).GetPassByIndex(0).Apply(ImmediateContext);
            ImmediateContext.DrawIndexed((int)piece.numPieceFaces*3,(int)piece.startIndex*3,0);
          }
        }
        if(HasLocalLightOverflow)DrawNpcModelLocalLightOverflow(model,state,world,vp,s,selectedLod);
      } finally { ImmediateContext.InputAssembler.InputLayout=inputLayout; }

      // Slot-attached GR2s currently have their attachment transform baked into their static vertex buffer by the
      // world geometry builder. Keep drawing those through the proven static path until bone-bound equipment is ported.
      foreach(GR2 attached in model.attachedModels)DrawModel(attached,world,vp,s,false,null,-1,blueGlow);
    }

    private void DrawNpcModelLocalLightOverflow(GR2 model, NpcSkinState state, Matrix world, Matrix vp, WorldRenderSettings s, int selectedLod) {
      LocalLightSelection selection=currentLocalLightSelection;
      if(model==null||state==null||selection==null||selection.Count<=LocalLightBaseSlots)return;
      fx.SetPlaceableBlueGlow(false);
      for(int offset=LocalLightBaseSlots;offset<selection.Count;offset+=LocalLightBaseSlots) {
        if(BindLocalLightChunk(selection,offset)<=0)break;
        fx.SetWorld(world);fx.SetViewProj(vp);
        foreach(GR2_Mesh mesh in model.meshes) {
          if(!MeshVisibleForLod(model,mesh,selectedLod)||!BindNpcSkinMesh(state,mesh,out bool gpuSkinned))continue;
          foreach(GR2_Mesh_Piece piece in mesh.meshPieces) {
            GR2_Material mat=ResolvePieceMaterial(model,piece);
            if(IsMaterialHiddenFromWorld(mat,s)||!MaterialReceivesAdditiveLocalLight(mat))continue;
            fx.SetMaterial(mat);
            (gpuSkinned?fx.SkinnedLocalLightAdd:fx.LocalLightAdd).GetPassByIndex(0).Apply(ImmediateContext);
            ImmediateContext.DrawIndexed((int)piece.numPieceFaces*3,(int)piece.startIndex*3,0);
          }
        }
      }
      ImmediateContext.InputAssembler.InputLayout=inputLayout;
      InvalidateTrackedLocalLightBinding();BindLocalLightSelection(selection);
    }

    private static string NpcCanonicalAnimationBoneName(string name) {
      return String.Equals(name, "Bip01", StringComparison.OrdinalIgnoreCase) ? "GOD" : (name ?? String.Empty);
    }

    private static Matrix NpcHeroToMorphemeBasis() {
      return new Matrix { M11 = 1000f, M23 = 1000f, M32 = -1000f, M44 = 1f };
    }

    private static Matrix NpcMorphemeToHeroBasis() {
      return new Matrix { M11 = .001f, M23 = -.001f, M32 = .001f, M44 = 1f };
    }

    private static bool NpcTryJedipediaBindRotation(Matrix matrix, out SlimDX.Quaternion rotation) {
      rotation = SlimDX.Quaternion.Identity;
      float m00 = matrix.M11, m11 = matrix.M22, m22 = matrix.M33;
      float trace = m00 + m11 + m22;
      float scale;
      try {
        if (trace > 0f) {
          scale = 2f * (float)Math.Sqrt(trace + 1f); if (Math.Abs(scale) <= .0000001f) return false;
          rotation.W = .25f * scale; rotation.X = (matrix.M23 - matrix.M32) / scale; rotation.Y = (matrix.M31 - matrix.M13) / scale; rotation.Z = (matrix.M12 - matrix.M21) / scale;
        } else if (m00 > m11 && m00 > m22) {
          scale = 2f * (float)Math.Sqrt(1f + m00 - m11 - m22); if (Math.Abs(scale) <= .0000001f) return false;
          rotation.W = (matrix.M23 - matrix.M32) / scale; rotation.X = .25f * scale; rotation.Y = (matrix.M12 + matrix.M21) / scale; rotation.Z = (matrix.M31 + matrix.M13) / scale;
        } else if (m11 > m22) {
          scale = 2f * (float)Math.Sqrt(1f + m11 - m00 - m22); if (Math.Abs(scale) <= .0000001f) return false;
          rotation.W = (matrix.M31 - matrix.M13) / scale; rotation.X = (matrix.M12 + matrix.M21) / scale; rotation.Y = .25f * scale; rotation.Z = (matrix.M23 + matrix.M32) / scale;
        } else {
          scale = 2f * (float)Math.Sqrt(1f + m22 - m00 - m11); if (Math.Abs(scale) <= .0000001f) return false;
          rotation.W = (matrix.M12 - matrix.M21) / scale; rotation.X = (matrix.M31 + matrix.M13) / scale; rotation.Y = (matrix.M23 + matrix.M32) / scale; rotation.Z = .25f * scale;
        }
      } catch { return false; }
      return Single.IsFinite(rotation.X) && Single.IsFinite(rotation.Y) && Single.IsFinite(rotation.Z) && Single.IsFinite(rotation.W);
    }

    private static Matrix NpcJedipediaRotationMatrix(SlimDX.Quaternion rotation) {
      float x = rotation.X, y = rotation.Y, z = rotation.Z, w = rotation.W;
      float x2 = x+x, y2 = y+y, z2 = z+z;
      float xx=x*x2, xy=x*y2, xz=x*z2, yy=y*y2, yz=y*z2, zz=z*z2, wx=w*x2, wy=w*y2, wz=w*z2;
      return new Matrix {
        M11=1f-(yy+zz), M12=xy+wz, M13=xz-wy, M14=0f,
        M21=xy-wz, M22=1f-(xx+zz), M23=yz+wx, M24=0f,
        M31=xz+wy, M32=yz-wx, M33=1f-(xx+yy), M34=0f,
        M41=0f, M42=0f, M43=0f, M44=1f
      };
    }

    private static bool NpcTryJbaBindLocalMorpheme(Matrix heroBindLocal, out System.Numerics.Quaternion rotation, out System.Numerics.Vector3 translation) {
      rotation = System.Numerics.Quaternion.Identity;
      translation = System.Numerics.Vector3.Zero;
      Matrix h2m = NpcHeroToMorphemeBasis(), m2h = NpcMorphemeToHeroBasis();
      Matrix.Multiply(ref m2h, ref heroBindLocal, out Matrix temp);
      Matrix.Multiply(ref temp, ref h2m, out Matrix morpheme);
      if (!NpcTryJedipediaBindRotation(morpheme, out SlimDX.Quaternion bindRotation)) return false;
      rotation = new System.Numerics.Quaternion(bindRotation.X, bindRotation.Y, bindRotation.Z, bindRotation.W);
      translation = new System.Numerics.Vector3(morpheme.M41, morpheme.M42, morpheme.M43);
      return NpcJbaFinite(rotation) && NpcJbaFinite(translation);
    }

    private static void NpcBuildChannelBinding(JBAAnimation animation, JBARig rig, IList<GR2_Bone_Skeleton> skeleton, out int[] skeletonToChannel, out bool[] useBindTranslation) {
      int skeletonCount = skeleton?.Count ?? 0;
      skeletonToChannel = Enumerable.Repeat(-1, skeletonCount).ToArray();
      useBindTranslation = new bool[skeletonCount];
      if (animation == null || skeleton == null) return;
      int sampleCount = Math.Max(0, animation.BoneCount);
      var channelByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
      bool authoritative = animation.BoneNames != null && animation.BoneNames.Take(Math.Min(animation.BoneNames.Count, sampleCount)).Any(name => !String.IsNullOrWhiteSpace(name) && !name.StartsWith("bone_", StringComparison.OrdinalIgnoreCase));
      if (!authoritative && rig?.Bones != null && rig.AnimToRig != null) {
        for (int channel = 0; channel < rig.AnimToRig.Length; channel++) {
          int rigIndex = rig.AnimToRig[channel];
          if (rigIndex < 0 || rigIndex >= rig.Bones.Count) continue;
          string name = NpcCanonicalAnimationBoneName(rig.Bones[rigIndex].Name);
          if (!String.IsNullOrWhiteSpace(name)) channelByName[name] = channel;
        }
      }
      if (authoritative) {
        int namedCount = Math.Min(animation.BoneNames.Count, sampleCount);
        for (int channel = 0; channel < namedCount; channel++) {
          string name = animation.BoneNames[channel];
          if (String.IsNullOrWhiteSpace(name) || name.StartsWith("bone_", StringComparison.OrdinalIgnoreCase)) continue;
          channelByName[NpcCanonicalAnimationBoneName(name)] = channel;
        }
      }
      for (int i = 0; i < skeletonCount; i++) {
        string name = NpcCanonicalAnimationBoneName(skeleton[i].boneName);
        if (channelByName.TryGetValue(name, out int channel) && channel >= 0 && channel < sampleCount) {
          skeletonToChannel[i] = channel;
          useBindTranslation[i] = animation.UsesRigBindTranslation(channel);
        }
      }
    }

    private static System.Numerics.Quaternion NpcJbaQuatMultiply(System.Numerics.Quaternion a, System.Numerics.Quaternion b) {
      return new System.Numerics.Quaternion(
        a.X*b.W+a.W*b.X+a.Y*b.Z-a.Z*b.Y,
        a.Y*b.W+a.W*b.Y+a.Z*b.X-a.X*b.Z,
        a.Z*b.W+a.W*b.Z+a.X*b.Y-a.Y*b.X,
        a.W*b.W-a.X*b.X-a.Y*b.Y-a.Z*b.Z
      );
    }

    private static System.Numerics.Vector3 NpcJbaTransformQuat(System.Numerics.Vector3 v, System.Numerics.Quaternion q) {
      float x=v.X,y=v.Y,z=v.Z,qx=q.X,qy=q.Y,qz=q.Z,qw=q.W;
      float ix=qw*x+qy*z-qz*y, iy=qw*y+qz*x-qx*z, iz=qw*z+qx*y-qy*x, iw=-qx*x-qy*y-qz*z;
      return new System.Numerics.Vector3(
        ix*qw+iw*-qx+iy*-qz-iz*-qy,
        iy*qw+iw*-qy+iz*-qx-ix*-qz,
        iz*qw+iw*-qz+ix*-qy-iy*-qx
      );
    }

    private static bool NpcJbaFinite(System.Numerics.Quaternion q) => Single.IsFinite(q.X) && Single.IsFinite(q.Y) && Single.IsFinite(q.Z) && Single.IsFinite(q.W);
    private static bool NpcJbaFinite(System.Numerics.Vector3 v) => Single.IsFinite(v.X) && Single.IsFinite(v.Y) && Single.IsFinite(v.Z);

    private static System.Numerics.Quaternion NpcJbaSampleQuaternion(JBATransform sample) {
      System.Numerics.Quaternion q = sample.Rotation;
      if (!NpcJbaFinite(q) || q.LengthSquared() <= .000001f) return System.Numerics.Quaternion.Identity;
      return System.Numerics.Quaternion.Normalize(q);
    }

    private static Matrix NpcJbaMorphemePoseToHero(System.Numerics.Quaternion rotation, System.Numerics.Vector3 translation) {
      var slimRotation = new SlimDX.Quaternion(rotation.X, rotation.Y, rotation.Z, rotation.W);
      Matrix morpheme = NpcJedipediaRotationMatrix(slimRotation);
      morpheme.M41 = translation.X; morpheme.M42 = translation.Y; morpheme.M43 = translation.Z; morpheme.M44 = 1f;
      Matrix h2m = NpcHeroToMorphemeBasis(), m2h = NpcMorphemeToHeroBasis();
      Matrix.Multiply(ref h2m, ref morpheme, out Matrix temp);
      Matrix.Multiply(ref temp, ref m2h, out Matrix hero);
      return hero;
    }

    private static void NpcBuildJbaWorldPose(NpcSkinState state, IReadOnlyList<JBATransform> poseFrame) {
      IList<GR2_Bone_Skeleton> skeleton = state?.Skeleton;
      if (state == null || poseFrame == null || skeleton == null) return;
      int count = Math.Min(skeleton.Count, state.CurrentWorld.Length);
      for (int i = 0; i < count; i++) {
        GR2_Bone_Skeleton bone = skeleton[i];
        int channel = state.SkeletonToChannel[i];
        bool driven = channel >= 0 && channel < poseFrame.Count;
        System.Numerics.Quaternion rotation;
        System.Numerics.Vector3 translation;
        if (driven) {
          JBATransform sample = poseFrame[channel];
          rotation = NpcJbaSampleQuaternion(sample);
          translation = state.UseBindTranslation[i] ? state.BindTranslationMorpheme[i] : sample.Translation;
        } else {
          rotation = state.BindRotationMorpheme[i];
          translation = state.BindTranslationMorpheme[i];
        }
        if (!NpcJbaFinite(rotation) || !NpcJbaFinite(translation)) {
          state.CurrentValid[i] = false;
          state.WorldRotationMorpheme[i] = System.Numerics.Quaternion.Identity;
          state.WorldTranslationMorpheme[i] = System.Numerics.Vector3.Zero;
          state.CurrentWorld[i] = Matrix.Identity;
          continue;
        }
        int parent = bone.parentBoneIndex;
        bool parentDriven = parent >= 0 && parent < i && state.SkeletonToChannel[parent] >= 0;
        if (parent >= 0 && parent < i && state.CurrentValid[parent] && (!driven || parentDriven)) {
          System.Numerics.Quaternion parentRotation = state.WorldRotationMorpheme[parent];
          state.WorldRotationMorpheme[i] = NpcJbaQuatMultiply(parentRotation, rotation);
          state.WorldTranslationMorpheme[i] = NpcJbaTransformQuat(translation, parentRotation) + state.WorldTranslationMorpheme[parent];
        } else {
          state.WorldRotationMorpheme[i] = rotation;
          state.WorldTranslationMorpheme[i] = translation;
        }
        state.CurrentWorld[i] = NpcJbaMorphemePoseToHero(state.WorldRotationMorpheme[i], state.WorldTranslationMorpheme[i]);
        state.CurrentValid[i] = true;
      }
    }

    private static bool NpcFinite(Matrix m) {
      return Single.IsFinite(m.M11)&&Single.IsFinite(m.M12)&&Single.IsFinite(m.M13)&&Single.IsFinite(m.M14)
        &&Single.IsFinite(m.M21)&&Single.IsFinite(m.M22)&&Single.IsFinite(m.M23)&&Single.IsFinite(m.M24)
        &&Single.IsFinite(m.M31)&&Single.IsFinite(m.M32)&&Single.IsFinite(m.M33)&&Single.IsFinite(m.M34)
        &&Single.IsFinite(m.M41)&&Single.IsFinite(m.M42)&&Single.IsFinite(m.M43)&&Single.IsFinite(m.M44);
    }

    private void InvalidateObjectOcclusionVisibility() {
      objectOcclusionHidden.Clear();
      objectOcclusionHiddenCounts.Clear();
      dynamicOcclusionHidden.Clear();
      dynamicOcclusionHiddenCounts.Clear();
      dynamicOcclusionPoses.Clear();
      objectOcclusionHasVisibility = false;
      objectOcclusionUseThisFrame = false;
      objectOcclusionReadbackPending = false;
      objectOcclusionPendingCandidates.Clear();
    }

    private static string ObjectOcclusionScope(HashSet<string> visible, WorldRenderSettings s) {
      return VisibleRoomScope(visible) + "|hidden:" + ((s?.ShowHiddenGeometry ?? false) ? "1" : "0") +
        "|npc:" + ((s?.ShowNpcs ?? false) ? "1" : "0") + "|taxi:" + ((s?.ShowTaxiTerminals ?? false) ? "1" : "0") +
        "|spn:" + ((s?.ShowSpnObjects ?? false) ? "1" : "0");
    }

    private void PrepareObjectOcclusionFrame(WorldRenderSettings s, HashSet<string> visible) {
      objectOcclusionUseThisFrame = false;
      if (s == null || !s.EnableObjectOcclusionCulling || !s.ShowModels || s.Mode == WorldRenderMode.Map ||
          s.Mode == WorldRenderMode.Heightmap || s.Mode == WorldRenderMode.Wireframe || objectOcclusionFailed ||
          !objectOcclusionHasVisibility || camera == null) return;

      int viewportWidth = Math.Max(1, (int)Viewport.Width);
      int viewportHeight = Math.Max(1, (int)Viewport.Height);
      string scope = ObjectOcclusionScope(visible, s);
      Vector3 look = camera.Look;
      if (look.LengthSquared() < .000001f) look = Vector3.UnitZ; else look.Normalize();
      Vector3 previousLook = objectOcclusionCameraLook;
      if (previousLook.LengthSquared() < .000001f) previousLook = Vector3.UnitZ; else previousLook.Normalize();
      bool moved = (camera.Position - objectOcclusionCameraPosition).LengthSquared() >
        ObjectOcclusionCameraMoveTolerance * ObjectOcclusionCameraMoveTolerance;
      bool turned = Vector3.Dot(look, previousLook) < ObjectOcclusionCameraLookDotTolerance;
      bool resized = viewportWidth != objectOcclusionViewportWidth || viewportHeight != objectOcclusionViewportHeight;
      bool scopeChanged = !String.Equals(scope, objectOcclusionVisibilityScope, StringComparison.Ordinal);
      if (moved || turned || resized || scopeChanged) {
        // dPVS-style visibility must never lag a teleport/doorway/camera snap. Fail open for this frame and rebuild
        // from the new depth image at the end of it instead of showing stale hidden receivers for several frames.
        InvalidateObjectOcclusionVisibility();
        return;
      }
      objectOcclusionUseThisFrame = true;
    }

    private bool RenderEntryVisibleByOcclusion(RenderEntry entry, WorldRenderSettings s) {
      return !objectOcclusionUseThisFrame || entry == null || !objectOcclusionHidden.Contains(entry);
    }

    private bool TryBuildOcclusionSample(Vector3 center, float radius, Matrix depthViewProj, int viewportWidth, int viewportHeight, out Vector4 sample, out float distanceSquared) {
      sample = Vector4.Zero;
      distanceSquared = 0f;
      if (radius <= .01f || !IsFinite(center) || camera == null) return false;
      Vector3 toCenter = center - camera.Position;
      distanceSquared = toCenter.LengthSquared();
      float minDistance = ObjectOcclusionMinDistance + Math.Max(0f, radius);
      if (distanceSquared <= minDistance * minDistance) return false;
      float distance = (float)Math.Sqrt(distanceSquared);
      if (distance <= radius + .01f) return false;

      Vector3 centreScreen = Vector3.Project(center, 0f, 0f, viewportWidth, viewportHeight, 0f, 1f, depthViewProj);
      if (!IsFinite(centreScreen) || centreScreen.Z <= 0f || centreScreen.Z >= 1f) return false;

      Vector3 right = camera.Right;
      if (right.LengthSquared() < .000001f) right = Vector3.UnitX; else right.Normalize();
      Vector3 up = camera.Up;
      if (up.LengthSquared() < .000001f) up = Vector3.UnitY; else up.Normalize();
      Vector3 rightScreen = Vector3.Project(center + right * radius, 0f, 0f, viewportWidth, viewportHeight, 0f, 1f, depthViewProj);
      Vector3 upScreen = Vector3.Project(center + up * radius, 0f, 0f, viewportWidth, viewportHeight, 0f, 1f, depthViewProj);
      if (!IsFinite(rightScreen) || !IsFinite(upScreen)) return false;
      float rx = (float)Math.Sqrt((rightScreen.X - centreScreen.X) * (rightScreen.X - centreScreen.X) + (rightScreen.Y - centreScreen.Y) * (rightScreen.Y - centreScreen.Y));
      float ry = (float)Math.Sqrt((upScreen.X - centreScreen.X) * (upScreen.X - centreScreen.X) + (upScreen.Y - centreScreen.Y) * (upScreen.Y - centreScreen.Y));
      float radiusPixels = Math.Max(1.5f, Math.Max(rx, ry));
      if (!Single.IsFinite(radiusPixels) || radiusPixels > ObjectOcclusionMaxProjectedRadius) return false;
      float edgePadding = radiusPixels + 3f;
      if (centreScreen.X < edgePadding || centreScreen.X > viewportWidth - edgePadding ||
          centreScreen.Y < edgePadding || centreScreen.Y > viewportHeight - edgePadding) return false;

      Vector3 direction = toCenter / distance;
      Vector3 nearestPoint = center - direction * radius;
      Vector3 nearestScreen = Vector3.Project(nearestPoint, 0f, 0f, viewportWidth, viewportHeight, 0f, 1f, depthViewProj);
      if (!IsFinite(nearestScreen) || nearestScreen.Z <= 0f || nearestScreen.Z >= 1f) return false;

      sample = new Vector4(
        Math.Max(0f, Math.Min(1f, centreScreen.X / Math.Max(1f, viewportWidth))),
        Math.Max(0f, Math.Min(1f, centreScreen.Y / Math.Max(1f, viewportHeight))),
        nearestScreen.Z,
        radiusPixels);
      return true;
    }

    private bool TryBuildObjectOcclusionSample(RenderEntry entry, Matrix depthViewProj, int viewportWidth, int viewportHeight, out Vector4 sample, out float distanceSquared) {
      sample = Vector4.Zero;
      distanceSquared = 0f;
      if (entry == null || entry.Instance == null || entry.Model == null || entry.Instance.PathFollowerAnimated) return false;
      return TryBuildOcclusionSample(entry.Center, entry.Radius, depthViewProj, viewportWidth, viewportHeight, out sample, out distanceSquared);
    }

    private bool DynamicReceiverVisibleByOcclusion(object owner, Vector3 center, float radius, object stateToken = null) {
      if (!objectOcclusionUseThisFrame || owner == null || !dynamicOcclusionHidden.Contains(owner)) return true;
      if (!dynamicOcclusionPoses.TryGetValue(owner, out DynamicOcclusionPose pose)) return true;
      if (!ReferenceEquals(pose.StateToken, stateToken)) return true;
      float moveTolerance = Math.Max(DynamicOcclusionMoveTolerance, Math.Max(radius, pose.Radius) * .08f);
      if ((center - pose.Center).LengthSquared() > moveTolerance * moveTolerance) return true;
      float radiusTolerance = Math.Max(.03f, Math.Max(radius, pose.Radius) * DynamicOcclusionRadiusTolerance);
      if (Math.Abs(radius - pose.Radius) > radiusTolerance) return true;
      return false;
    }

    private static bool TryUnionSphere(ref bool have, ref Vector3 center, ref float radius, Vector3 otherCenter, float otherRadius) {
      if (!IsFinite(otherCenter) || !Single.IsFinite(otherRadius) || otherRadius <= .001f) return false;
      if (!have) { center = otherCenter; radius = otherRadius; have = true; return true; }
      Vector3 delta = otherCenter - center;
      float distance = delta.Length();
      if (distance + otherRadius <= radius) return true;
      if (distance + radius <= otherRadius) { center = otherCenter; radius = otherRadius; return true; }
      if (distance <= .000001f) { radius = Math.Max(radius, otherRadius); return true; }
      float newRadius = (radius + distance + otherRadius) * .5f;
      center += delta * ((newRadius - radius) / distance);
      radius = newRadius;
      return true;
    }

    private bool TryWorldModelsSphere(IEnumerable<GR2> modelsToTest, Matrix world, out Vector3 center, out float radius) {
      center = Vector3.Zero; radius = 0f; bool have = false;
      if (modelsToTest == null) return false;
      foreach (GR2 model in modelsToTest) {
        if (model == null || !TryModelSphere(model, world, out Vector3 modelCenter, out float modelRadius)) continue;
        TryUnionSphere(ref have, ref center, ref radius, modelCenter, modelRadius);
      }
      return have;
    }

    private bool TrySpnReceiverSphere(WorldSpnPlacement placement, Matrix world, WorldSpnDynState dynState, out Vector3 center, out float radius) {
      center = Vector3.Zero; radius = 0f; bool have = false;
      if (placement == null) return false;
      if (dynState != null) {
        if (dynState.Hidden) return false;
        foreach (WorldSpnDynPart part in dynState.Parts) {
          if (part?.Model == null) continue;
          Matrix partWorld = part.LocalMatrix * world;
          if (!TryModelSphere(part.Model, partWorld, out Vector3 partCenter, out float partRadius)) continue;
          TryUnionSphere(ref have, ref center, ref radius, partCenter, partRadius);
        }
        return have;
      }
      return TryWorldModelsSphere(placement.Models, world, out center, out radius);
    }

    private bool TryConsumeObjectOcclusionReadback() {
      if (!objectOcclusionReadbackPending) return true;
      if (objectOcclusionVisibilityStaging == null) {
        objectOcclusionReadbackPending = false;
        objectOcclusionPendingCandidates.Clear();
        return true;
      }

      int count = objectOcclusionPendingCandidates.Count;
      if (count <= 0) {
        objectOcclusionReadbackPending = false;
        return true;
      }

      DataBox mapped;
      try {
        // DO_NOT_WAIT is the important part: if the copy issued at the end of a previous frame is still in flight,
        // keep the previous conservative visibility set and try again next frame instead of stalling the render thread.
        mapped = ImmediateContext.MapSubresource(objectOcclusionVisibilityStaging, 0, 0, MapMode.Read, SlimDX.Direct3D11.MapFlags.DoNotWait);
      } catch (SlimDXException ex) {
        if (ex.ResultCode == SlimDX.Direct3D11.ResultCode.WasStillDrawing) return false;
        throw;
      }

      try { mapped.Data.ReadRange(objectOcclusionVisibilityReadback, 0, count * 4); }
      finally { ImmediateContext.UnmapSubresource(objectOcclusionVisibilityStaging, 0); }

      var queriedStatic = new HashSet<RenderEntry>();
      var queriedDynamic = new HashSet<object>();
      for (int i = 0; i < count; i++) {
        ObjectOcclusionCandidate candidate = objectOcclusionPendingCandidates[i];
        bool depthVisible = objectOcclusionVisibilityReadback[i * 4] > 127;
        if (candidate.Entry != null) {
          RenderEntry entry = candidate.Entry;
          queriedStatic.Add(entry);
          if (depthVisible) {
            objectOcclusionHidden.Remove(entry);
            objectOcclusionHiddenCounts.Remove(entry);
          } else {
            objectOcclusionHiddenCounts.TryGetValue(entry, out byte hiddenCount);
            hiddenCount = (byte)Math.Min(255, hiddenCount + 1);
            objectOcclusionHiddenCounts[entry] = hiddenCount;
            if (hiddenCount >= ObjectOcclusionHiddenConfirmations) objectOcclusionHidden.Add(entry);
          }
        } else if (candidate.DynamicOwner != null) {
          object owner = candidate.DynamicOwner;
          queriedDynamic.Add(owner);
          dynamicOcclusionPoses[owner] = new DynamicOcclusionPose {
            Center = candidate.DynamicCenter, Radius = candidate.DynamicRadius, StateToken = candidate.DynamicStateToken
          };
          if (depthVisible) {
            dynamicOcclusionHidden.Remove(owner);
            dynamicOcclusionHiddenCounts.Remove(owner);
          } else {
            dynamicOcclusionHiddenCounts.TryGetValue(owner, out byte hiddenCount);
            hiddenCount = (byte)Math.Min(255, hiddenCount + 1);
            dynamicOcclusionHiddenCounts[owner] = hiddenCount;
            if (hiddenCount >= ObjectOcclusionHiddenConfirmations) dynamicOcclusionHidden.Add(owner);
          }
        }
      }

      // Never keep a hidden decision for a receiver that no longer fits the conservative probe/budget. This is a
      // deliberate fail-open policy and prevents slow camera approaches or moving spawners from retaining stale state.
      foreach (RenderEntry hidden in objectOcclusionHidden.ToArray()) {
        if (queriedStatic.Contains(hidden)) continue;
        objectOcclusionHidden.Remove(hidden);
        objectOcclusionHiddenCounts.Remove(hidden);
      }
      foreach (RenderEntry pending in objectOcclusionHiddenCounts.Keys.ToArray())
        if (!queriedStatic.Contains(pending)) objectOcclusionHiddenCounts.Remove(pending);
      foreach (object hidden in dynamicOcclusionHidden.ToArray()) {
        if (queriedDynamic.Contains(hidden)) continue;
        dynamicOcclusionHidden.Remove(hidden);
        dynamicOcclusionHiddenCounts.Remove(hidden);
        dynamicOcclusionPoses.Remove(hidden);
      }
      foreach (object pending in dynamicOcclusionHiddenCounts.Keys.ToArray()) {
        if (queriedDynamic.Contains(pending)) continue;
        dynamicOcclusionHiddenCounts.Remove(pending);
        dynamicOcclusionPoses.Remove(pending);
      }

      objectOcclusionHasVisibility = true;
      objectOcclusionAskedAt = objectOcclusionPendingFrame;
      objectOcclusionCameraPosition = objectOcclusionPendingCameraPosition;
      objectOcclusionCameraLook = objectOcclusionPendingCameraLook;
      objectOcclusionViewportWidth = objectOcclusionPendingViewportWidth;
      objectOcclusionViewportHeight = objectOcclusionPendingViewportHeight;
      objectOcclusionVisibilityScope = objectOcclusionPendingVisibilityScope;
      objectOcclusionReadbackPending = false;
      objectOcclusionPendingCandidates.Clear();
      return true;
    }

    private void UpdateObjectOcclusionVisibility(Matrix depthViewProj, HashSet<string> visible, WorldRenderSettings s) {
      if (s == null || !s.EnableObjectOcclusionCulling || !s.ShowModels || s.Mode == WorldRenderMode.Map ||
          s.Mode == WorldRenderMode.Heightmap || s.Mode == WorldRenderMode.Wireframe || sceneDepthShaderResource == null ||
          fx?.ObjectVisibility == null || objectOcclusionFailed || camera == null) {
        objectOcclusionUseThisFrame = false;
        return;
      }

      int frame = ++objectOcclusionFrame;
      try {
        // A query from a previous frame owns the single staging texture until its asynchronous copy can be read.
        // If the GPU is still busy, skip issuing another query instead of forcing a CPU/GPU rendezvous.
        if (!TryConsumeObjectOcclusionReadback()) return;
      } catch (Exception ex) {
        System.Diagnostics.Debug.WriteLine("Object depth visibility readback unavailable: " + ex.Message);
        objectOcclusionFailed = true;
        InvalidateObjectOcclusionVisibility();
        return;
      }
      if (objectOcclusionHasVisibility && frame - objectOcclusionAskedAt < ObjectOcclusionInterval) return;

      int viewportWidth = Math.Max(1, (int)Viewport.Width);
      int viewportHeight = Math.Max(1, (int)Viewport.Height);
      var staticCandidates = new List<ObjectOcclusionCandidate>(ObjectOcclusionMaxCount);
      foreach (RenderEntry entry in NearbyRenderEntries(RenderKindModel, camera.FarZ)) {
        Room room = entry.Room;
        AssetInstance inst = entry.Instance;
        GR2 model = entry.Model;
        if (room == null || inst == null || model == null || !model.enabled || skyRoomNames.Contains(room.RoomName) ||
            !InstanceRoomVisible(inst, room, visible) || !InstanceVisibleInWorld(inst,s)) continue;
        if (IsSpeedTreeInstance(inst) && !s.ShowSpeedTrees) continue;
        if (ShouldCullModelByLod(model, entry.World, false, inst.LodFactor)) continue;
        if (!TryBuildObjectOcclusionSample(entry, depthViewProj, viewportWidth, viewportHeight, out Vector4 sample, out float distanceSquared)) continue;
        staticCandidates.Add(new ObjectOcclusionCandidate { Entry = entry, Sample = sample, DistanceSquared = distanceSquared });
      }

      // Dynamic population/placeables use the same depth image as the static dPVS approximation. Unlike static
      // RenderEntry results, however, the next frame accepts a hidden decision only if the queried world-space sphere
      // still matches. A walking NPC, lift or state-changing DYN therefore automatically fails open as soon as it moves.
      var dynamicCandidates = new List<ObjectOcclusionCandidate>(DynamicOcclusionReservedCount * 2);
      if ((s.ShowNpcs || s.ShowTaxiTerminals) && npcPlacements != null) {
        foreach (WorldNpcPlacement placement in NearbyNpcPlacements(NpcMaxRenderDistance)) {
          if (!NpcLayerVisible(placement, s)) continue;
          if (placement?.Instance == null || placement.Room == null || placement.Models == null || placement.Models.Count == 0) continue;
          if (!SpnVariantActive(placement.VariantIndex, placement.VariantCount, placement.SpawnPoints)) continue;
          bool hasSpawnMotion = placement.SpawnPoints != null && placement.SpawnPoints.Count > 0;
          if (!hasSpawnMotion && !InstanceRoomVisible(placement.Instance, placement.Room, visible)) continue;
          if (!InstanceVisibleInWorld(placement.Instance, s)) continue;
          Matrix baseWorld = NpcPlacementBaseWorld(placement);
          Vector3 rootPosition = new Vector3(baseWorld.M41, baseWorld.M42, baseWorld.M43);
          if (!NpcVisibleForWork(rootPosition)) continue;
          Matrix npcWorld = Matrix.RotationY((float)Math.PI) * baseWorld;
          if (!TryWorldModelsSphere(placement.Models, npcWorld, out Vector3 center, out float radius)) continue;
          if (!TryBuildOcclusionSample(center, radius, depthViewProj, viewportWidth, viewportHeight, out Vector4 sample, out float distanceSquared)) continue;
          dynamicCandidates.Add(new ObjectOcclusionCandidate { DynamicOwner = placement, DynamicCenter = center, DynamicRadius = radius, Sample = sample, DistanceSquared = distanceSquared });
        }
      }
      if (s.ShowSpnObjects && spnPlacements != null) {
        foreach (WorldSpnPlacement placement in NearbySpnPlacements(camera.FarZ)) {
          if (placement?.Instance == null || placement.Room == null || placement.Models == null) continue;
          if (!SpnVariantActive(placement.VariantIndex, placement.VariantCount, placement.SpawnPoints)) continue;
          bool hasMotion = placement.Route != null || (placement.SpawnPoints != null && placement.SpawnPoints.Count > 0);
          if (!hasMotion && !InstanceRoomVisible(placement.Instance, placement.Room, visible)) continue;
          if (!InstanceVisibleInWorld(placement.Instance, s)) continue;
          Matrix world;
          try { world = SpnPlacementWorld(placement, s.AnimateSpnObjects); } catch { continue; }
          WorldSpnDynState dynState = ActiveSpnDynState(placement, s.AnimateSpnObjects);
          if (!TrySpnReceiverSphere(placement, world, dynState, out Vector3 center, out float radius)) continue;
          if (!SphereVisibleInCameraFrustum(center, radius) || !SphereWithinDistance(center, radius, camera.FarZ)) continue;
          if (!TryBuildOcclusionSample(center, radius, depthViewProj, viewportWidth, viewportHeight, out Vector4 sample, out float distanceSquared)) continue;
          dynamicCandidates.Add(new ObjectOcclusionCandidate { DynamicOwner = placement, DynamicStateToken = dynState, DynamicCenter = center, DynamicRadius = radius, Sample = sample, DistanceSquared = distanceSquared });
        }
      }

      if (staticCandidates.Count == 0 && dynamicCandidates.Count == 0) {
        InvalidateObjectOcclusionVisibility();
        objectOcclusionCameraPosition = camera.Position;
        objectOcclusionCameraLook = camera.Look;
        objectOcclusionViewportWidth = viewportWidth;
        objectOcclusionViewportHeight = viewportHeight;
        objectOcclusionVisibilityScope = ObjectOcclusionScope(visible, s);
        return;
      }

      // Reserve part of the fixed readback strip for population/SPNs so a distant open-world vista cannot starve the
      // very receivers whose animation/skinning is most expensive. Both pools still prefer farther receivers.
      staticCandidates.Sort((a, b) => b.DistanceSquared.CompareTo(a.DistanceSquared));
      dynamicCandidates.Sort((a, b) => b.DistanceSquared.CompareTo(a.DistanceSquared));
      if (dynamicCandidates.Count > DynamicOcclusionReservedCount)
        dynamicCandidates.RemoveRange(DynamicOcclusionReservedCount, dynamicCandidates.Count - DynamicOcclusionReservedCount);
      int staticBudget = ObjectOcclusionMaxCount - dynamicCandidates.Count;
      if (staticCandidates.Count > staticBudget) staticCandidates.RemoveRange(staticBudget, staticCandidates.Count - staticBudget);
      var candidates = new List<ObjectOcclusionCandidate>(staticCandidates.Count + dynamicCandidates.Count);
      candidates.AddRange(staticCandidates);
      candidates.AddRange(dynamicCandidates);

      try {
        EnsureObjectOcclusionResources();
        if (objectOcclusionVisibilityTarget == null || objectOcclusionVisibilityStaging == null) return;
        int count = candidates.Count;
        for (int i = 0; i < count; i++) objectOcclusionVisibilitySamples[i] = candidates[i].Sample;

        ImmediateContext.OutputMerger.SetTargets((DepthStencilView)null, objectOcclusionVisibilityTarget);
        ImmediateContext.Rasterizer.SetViewports(new Viewport(0, 0, ObjectOcclusionMaxCount, 1));
        ImmediateContext.ClearRenderTargetView(objectOcclusionVisibilityTarget, new Color4(0f, 0f, 0f, 0f));
        ImmediateContext.InputAssembler.InputLayout = null;
        ImmediateContext.InputAssembler.PrimitiveTopology = PrimitiveTopology.PointList;
        fx.SetObjectVisibility(sceneDepthShaderResource, objectOcclusionVisibilitySamples, count, ObjectOcclusionMaxCount, viewportWidth, viewportHeight, ObjectOcclusionDepthBias);
        fx.ObjectVisibility.GetPassByIndex(0).Apply(ImmediateContext);
        ImmediateContext.Draw(count, 0);
        fx.ClearObjectVisibility();
        fx.ObjectVisibility.GetPassByIndex(0).Apply(ImmediateContext);

        ImmediateContext.CopyResource(objectOcclusionVisibilityTexture, objectOcclusionVisibilityStaging);
        objectOcclusionPendingCandidates.Clear();
        objectOcclusionPendingCandidates.AddRange(candidates);
        objectOcclusionPendingCameraPosition = camera.Position;
        objectOcclusionPendingCameraLook = camera.Look;
        objectOcclusionPendingViewportWidth = viewportWidth;
        objectOcclusionPendingViewportHeight = viewportHeight;
        objectOcclusionPendingVisibilityScope = ObjectOcclusionScope(visible, s);
        objectOcclusionPendingFrame = frame;
        objectOcclusionReadbackPending = true;
      } catch (Exception ex) {
        System.Diagnostics.Debug.WriteLine("Object depth visibility unavailable: " + ex.Message);
        objectOcclusionFailed = true;
        InvalidateObjectOcclusionVisibility();
      } finally {
        ImmediateContext.OutputMerger.SetTargets((DepthStencilView)null, RenderTargetView);
        ImmediateContext.Rasterizer.SetViewports(Viewport);
        ImmediateContext.InputAssembler.InputLayout = inputLayout;
        ImmediateContext.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
      }
    }

    private void EnsureObjectOcclusionResources() {
      if (objectOcclusionVisibilityTexture != null || Device == null) return;
      var desc = new Texture2DDescription {
        Width = ObjectOcclusionMaxCount, Height = 1, MipLevels = 1, ArraySize = 1, Format = Format.R8G8B8A8_UNorm,
        SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default, BindFlags = BindFlags.RenderTarget,
        CpuAccessFlags = CpuAccessFlags.None, OptionFlags = ResourceOptionFlags.None
      };
      objectOcclusionVisibilityTexture = new Texture2D(Device, desc) { DebugName = "World object visibility" };
      objectOcclusionVisibilityTarget = new RenderTargetView(Device, objectOcclusionVisibilityTexture);
      desc.Usage = ResourceUsage.Staging; desc.BindFlags = BindFlags.None; desc.CpuAccessFlags = CpuAccessFlags.Read;
      objectOcclusionVisibilityStaging = new Texture2D(Device, desc) { DebugName = "World object visibility readback" };
    }

    private void DrawNpcNameplates(Matrix labelViewProj, Matrix depthViewProj, HashSet<string> visible, WorldRenderSettings s) {
      if ((!s.ShowNpcs && !s.ShowTaxiTerminals) || (!s.ShowNpcNames && !s.ShowNpcItems) || !npcTextFontsRegistered || npcTextFont == null || npcTextSprite == null) return;

      // IMPORTANT: never use ClientWidth/ClientHeight below. SetSize() is called by the WinForms splitter thread and
      // changes those fields before the render thread reaches OnResize(). During an interactive sidebar drag this can
      // happen halfway through DrawScene(), so the scene is still rendered with the old viewport while labels suddenly
      // project with the new client size. The D3D viewport is owned/updated by the render thread and therefore gives us
      // one coherent size for the entire frame.
      int viewportWidth = Math.Max(1, (int)Viewport.Width);
      int viewportHeight = Math.Max(1, (int)Viewport.Height);
      if (s.Mode == WorldRenderMode.Map || viewportWidth <= 0 || viewportHeight <= 0) return;

      if (viewportWidth != npcNameplateViewportWidth || viewportHeight != npcNameplateViewportHeight) {
        npcNameplateViewportWidth = viewportWidth;
        npcNameplateViewportHeight = viewportHeight;
        // SpriteRenderer has its own cached ScreenSize. Refreshing only the D3D Viewport is not enough after the
        // World Browser splitter changes renderPanel.Width. Keep the sprite transform in the exact same pixel space
        // used by Vector3.Project below. This is deliberately on the render thread, immediately before queuing text.
        try { npcTextSprite.RefreshViewport(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine("NPC text viewport refresh failed: " + ex.Message); }
        npcNameplateVisible.Clear();
        npcNameplateHasVisibility = false;
        npcNameplateVisibilityAskedAt = Int32.MinValue / 2;
      }

      // WHERE and WHETHER intentionally use two matrices when TAA is active. Text is a screen-space overlay and must
      // be projected with the camera's stable, unjittered matrix; the occlusion sample must use the exact jittered
      // matrix that produced SceneDepthBuffer. Jedipedia makes the same separation, which prevents camera movement
      // from turning the labels into a one/sub-pixel wobbling layer over otherwise stable NPCs.
      var entries = new List<NpcNameplateEntry>(NpcNameplateMaxCount);
      foreach (WorldNpcPlacement placement in NearbyNpcPlacements(NpcMaxRenderDistance)) {
        if (!NpcLayerVisible(placement, s)) continue;
        if (placement?.Instance == null || placement.Room == null || !placement.ShowNameplate) continue;
        if (!SpnVariantActive(placement.VariantIndex, placement.VariantCount, placement.SpawnPoints)) continue;
        bool hasSpawnMotion = placement.SpawnPoints != null && placement.SpawnPoints.Count > 0;
        if (!hasSpawnMotion && !InstanceRoomVisible(placement.Instance, placement.Room, visible)) continue;
        if (!InstanceVisibleInWorld(placement.Instance, s)) continue;
        Matrix world = NpcNameplateWorld(placement);
        Vector3 basePos = new Vector3(world.M41, world.M42, world.M43);
        if (!NpcVisibleForWork(basePos)) continue;
        if (TryWorldModelsSphere(placement.Models, world, out Vector3 receiverCenter, out float receiverRadius) &&
            !DynamicReceiverVisibleByOcclusion(placement, receiverCenter, receiverRadius)) continue;

        // Jedipedia projects attach_nameplate (fallback: attach_nameplate_fallback/nameplate) from the current pose
        // and skips rigs that have no authored nameplate point. Keeping the old fixed eye-height fallback is what made
        // labels on unusual creatures slide around independently of the model, so do not invent one here.
        if (!placement.NameplateLocal.HasValue) continue;
        Vector3 localAnchor = placement.NameplateLocal.Value;
        Vector3 anchor = Vector3.TransformCoordinate(localAnchor, world);

        Vector3 screen = Vector3.Project(anchor, 0f, 0f, viewportWidth, viewportHeight, 0f, 1f, labelViewProj);
        if (!IsFinite(screen) || screen.Z < 0f || screen.Z > 1f || screen.X < 0f || screen.X > viewportWidth || screen.Y < 0f || screen.Y > viewportHeight) continue;

        // Raise only the depth-test point clear of the model's own bounding box. The text itself stays on the bone.
        // This mirrors viewerNameplateSamplePoint(): max(model top, anchorY) + TEST_LIFT along the placement's up axis.
        float clearLocalY = NpcNameplateClearLocalY(placement, localAnchor.Y);
        float liftLocal = Math.Max(0f, clearLocalY - localAnchor.Y) + NpcNameplateTestLift;
        Vector3 lift = Vector3.TransformNormal(new Vector3(0f, liftLocal, 0f), world);
        Vector3 testPoint = anchor + lift;
        Vector3 testScreen = Vector3.Project(testPoint, 0f, 0f, viewportWidth, viewportHeight, 0f, 1f, depthViewProj);
        if (!IsFinite(testScreen) || testScreen.Z < 0f || testScreen.Z > 1f) continue;

        entries.Add(new NpcNameplateEntry {
          Placement = placement,
          Screen = screen,
          VisibilitySample = new Vector4(
            Math.Max(0f, Math.Min(1f, testScreen.X / Math.Max(1f, viewportWidth))),
            Math.Max(0f, Math.Min(1f, testScreen.Y / Math.Max(1f, viewportHeight))),
            testScreen.Z, 0f),
          Distance = (anchor - camera.Position).LengthSquared()
        });
      }

      entries.Sort((a, b) => a.Distance.CompareTo(b.Distance));
      int nameplateBudget = Math.Min(NpcNameplateMaxCount, Math.Max(1, viewportWidth));
      if (entries.Count > nameplateBudget) entries.RemoveRange(nameplateBudget, entries.Count - nameplateBudget);

      bool useOcclusion = UpdateNpcNameplateVisibility(entries, viewportWidth, viewportHeight);
      foreach (NpcNameplateEntry entry in entries) {
        WorldNpcPlacement placement = entry.Placement;
        if (useOcclusion && !npcNameplateVisible.Contains(placement)) continue;

        Vector3 screen = entry.Screen;
        float y = screen.Y;
        if (s.ShowNpcNames) {
          string text = placement.Name;
          Color4 nameColor = NpcNameplateColor(placement);
          if (!String.IsNullOrWhiteSpace(text)) {
            DrawNpcText(text, new Vector2(screen.X + 1, y + 1), new Color4(.85f, 0f, 0f, 0f), "world-npc-name");
            DrawNpcText(text, new Vector2(screen.X, y), nameColor, "world-npc-name");
            y += 16f;
          }
          if (!String.IsNullOrWhiteSpace(placement.Title)) {
            string title = "<" + placement.Title.Trim() + ">";
            DrawNpcText(title, new Vector2(screen.X + 1, y + 1), new Color4(.8f, 0f, 0f, 0f), "world-npc-title");
            DrawNpcText(title, new Vector2(screen.X, y), nameColor, "world-npc-title");
            y += 13f;
          }
        }
        if (s.ShowNpcItems && placement.Items != null && placement.Items.Length > 0) {
          string items = "[" + String.Join(", ", placement.Items) + "]";
          DrawNpcText(items, new Vector2(screen.X + 1, y + 1), new Color4(.8f, 0f, 0f, 0f), "world-npc-item");
          DrawNpcText(items, new Vector2(screen.X, y), new Color4(1f, .72f, .9f, 1f), "world-npc-item");
        }
      }
      npcTextSprite.Flush();
    }

    private bool UpdateNpcNameplateVisibility(List<NpcNameplateEntry> entries, int viewportWidth, int viewportHeight) {
      if(entries==null||entries.Count==0){npcNameplateVisible.Clear();npcNameplateHasVisibility=true;return true;}
      if(sceneDepthShaderResource==null||fx?.NameplateVisibility==null||npcNameplateVisibilityFailed)return false;

      int frame=++npcNameplateVisibilityFrame;
      if(npcNameplateHasVisibility && frame-npcNameplateVisibilityAskedAt<NpcNameplateVisibilityInterval)return true;

      try{
        EnsureNpcNameplateVisibilityResources();
        if(npcNameplateVisibilityTarget==null||npcNameplateVisibilityStaging==null)return false;

        int count=Math.Min(NpcNameplateMaxCount,entries.Count);
        for(int i=0;i<count;i++)npcNameplateVisibilitySamples[i]=entries[i].VisibilitySample;

        // SceneDepthBuffer cannot be sampled while its DSV is bound. Nameplates are drawn after the world/post
        // pass, so temporarily bind only the tiny 192x1 visibility target, then restore the final back buffer.
        ImmediateContext.OutputMerger.SetTargets((DepthStencilView)null,npcNameplateVisibilityTarget);
        ImmediateContext.Rasterizer.SetViewports(Viewport);
        ImmediateContext.ClearRenderTargetView(npcNameplateVisibilityTarget,new Color4(0f,0f,0f,0f));
        ImmediateContext.InputAssembler.InputLayout=null;
        ImmediateContext.InputAssembler.PrimitiveTopology=PrimitiveTopology.PointList;
        fx.SetNameplateVisibility(sceneDepthShaderResource,npcNameplateVisibilitySamples,count,viewportWidth,viewportHeight,NpcNameplateDepthBias);
        fx.NameplateVisibility.GetPassByIndex(0).Apply(ImmediateContext);
        ImmediateContext.Draw(count,0);
        fx.ClearNameplateVisibility();
        // Push the null SceneDepthMap immediately so the next frame may bind the same texture as a DSV
        // without relying on D3D11's automatic hazard unbinding.
        fx.NameplateVisibility.GetPassByIndex(0).Apply(ImmediateContext);

        // A one-row RGBA8 copy every third frame is deliberately tiny. Jedipedia uses an asynchronous pack
        // buffer/fence; SlimDX has no equivalent helper in this project, so this keeps the same query interval
        // and reads only 768 bytes rather than ever mapping the scene depth itself.
        ImmediateContext.CopyResource(npcNameplateVisibilityTexture,npcNameplateVisibilityStaging);
        DataBox mapped=ImmediateContext.MapSubresource(npcNameplateVisibilityStaging,0,0,MapMode.Read,SlimDX.Direct3D11.MapFlags.None);
        try{mapped.Data.ReadRange(npcNameplateVisibilityReadback,0,count*4);}
        finally{ImmediateContext.UnmapSubresource(npcNameplateVisibilityStaging,0);}

        npcNameplateVisible.Clear();
        for(int i=0;i<count;i++)if(npcNameplateVisibilityReadback[i*4]>127)npcNameplateVisible.Add(entries[i].Placement);
        npcNameplateHasVisibility=true;
        npcNameplateVisibilityAskedAt=frame;
        return true;
      }catch(Exception ex){
        // Fail open: a driver that dislikes the small readback should never make every label disappear.
        System.Diagnostics.Debug.WriteLine("NPC nameplate depth visibility unavailable: "+ex.Message);
        npcNameplateVisibilityFailed=true;
        npcNameplateVisible.Clear();
        npcNameplateHasVisibility=false;
        return false;
      }finally{
        ImmediateContext.OutputMerger.SetTargets((DepthStencilView)null,RenderTargetView);
        ImmediateContext.Rasterizer.SetViewports(Viewport);
        ImmediateContext.InputAssembler.InputLayout=inputLayout;
        ImmediateContext.InputAssembler.PrimitiveTopology=PrimitiveTopology.TriangleList;
      }
    }

    private void EnsureNpcNameplateVisibilityResources() {
      if(npcNameplateVisibilityTexture!=null||Device==null)return;
      var desc=new Texture2DDescription{
        Width=NpcNameplateMaxCount,Height=1,MipLevels=1,ArraySize=1,Format=Format.R8G8B8A8_UNorm,
        SampleDescription=new SampleDescription(1,0),Usage=ResourceUsage.Default,BindFlags=BindFlags.RenderTarget,
        CpuAccessFlags=CpuAccessFlags.None,OptionFlags=ResourceOptionFlags.None
      };
      npcNameplateVisibilityTexture=new Texture2D(Device,desc){DebugName="NPC nameplate visibility"};
      npcNameplateVisibilityTarget=new RenderTargetView(Device,npcNameplateVisibilityTexture);
      desc.Usage=ResourceUsage.Staging;desc.BindFlags=BindFlags.None;desc.CpuAccessFlags=CpuAccessFlags.Read;
      npcNameplateVisibilityStaging=new Texture2D(Device,desc){DebugName="NPC nameplate visibility readback"};
    }

    private Color4 NpcNameplateColor(WorldNpcPlacement placement) {
      if (placement != null && !placement.HasFactionPackage) return new Color4(1f, 0f, 1f, 0f);
      int attitude = NpcAttitudeKind(PreferredNpcReaction(placement));
      if (attitude < 0) return new Color4(1f, 1f, 0f, 0f);   // hostile: red
      if (attitude > 0) return new Color4(1f, 0f, 1f, 0f);   // friendly: green
      return new Color4(1f, 1f, 1f, 0f);                     // neutral: yellow
    }

    private static int NpcAttitudeKind(string reaction) {
      if (String.IsNullOrWhiteSpace(reaction)) return 0;
      string reactionText = reaction.Trim();
      if (reactionText.Equals("0x02", StringComparison.OrdinalIgnoreCase) || reactionText.IndexOf("hostile", StringComparison.OrdinalIgnoreCase) >= 0) return -1;
      if (reactionText.Equals("0x03", StringComparison.OrdinalIgnoreCase) || reactionText.IndexOf("friendly", StringComparison.OrdinalIgnoreCase) >= 0) return 1;
      if (Int32.TryParse(reactionText, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int numericReaction)) {
        if (numericReaction == 2) return -1;
        if (numericReaction == 3) return 1;
      }
      return 0;
    }

    private string PreferredNpcReaction(WorldNpcPlacement placement) {
      if (npcFactionHint == Int32.MinValue) {
        npcFactionHint = 0;
        bool empire = false, republic = false;
        if (UInt64.TryParse(fqn, out ulong areaId)) {
          WorldAreaCatalogEntry entry = WorldAreaCatalog.Entries.FirstOrDefault(x => x.Id == areaId);
          string hint = ((entry?.Comment ?? String.Empty) + " " + (entry?.InternalName ?? String.Empty)).ToLowerInvariant();
          empire = hint.Contains("(empire)") || hint.Contains("dromund") || hint.Contains("imperial") || hint.Contains("korriban");
          republic = hint.Contains("(republic)") || hint.Contains("coruscant") || hint.Contains("republic") || hint.Contains("tython");
        }
        if (empire && !republic) npcFactionHint = -1;
        else if (republic && !empire) npcFactionHint = 1;
      }
      if (npcFactionHint < 0) return placement?.ImperialReaction;
      if (npcFactionHint > 0) return placement?.RepublicReaction;
      string rep = placement?.RepublicReaction, imp = placement?.ImperialReaction;
      if (NpcAttitudeKind(rep) < 0 || NpcAttitudeKind(imp) < 0) return "Hostile";
      if (NpcAttitudeKind(rep) > 0 || NpcAttitudeKind(imp) > 0) return "Friendly";
      return rep ?? imp;
    }

    private void DrawNpcText(string text, Vector2 pos, Color4 color, string font) {
      if (String.IsNullOrWhiteSpace(text)) return;
      // Center approximately; TextBlockRenderer does not expose a cheap measurement-only call.
      float width = Math.Min(500f, text.Length * (font == "world-npc-name" ? 7.1f : 5.8f));
      npcTextFont.DrawString(font, text, new Vector2(pos.X - width * .5f, pos.Y), color);
    }

    private static string SelectionOverlayEllipsis(string value, int maxChars) {
      string text = value ?? String.Empty;
      if (maxChars < 4 || text.Length <= maxChars) return text;
      return text.Substring(0, maxChars - 1) + "…";
    }

    // Keep the selection readout attached to the object instead of forcing the user to open the Tools submenu.
    // It intentionally appears only after the explicit Ctrl+left-click selection gesture and follows moving NPC/SPN
    // placements every frame. The detailed/copyable diagnostic remains available from Selected object in the menu.
    private void DrawWorldSelectionLabel(Matrix labelViewProj, WorldRenderSettings s) {
      if (s == null || s.Mode == WorldRenderMode.Map || s.Mode == WorldRenderMode.Heightmap ||
          String.IsNullOrWhiteSpace(selectedWorldModelSummary) || !npcTextFontsRegistered || npcTextFont == null || npcTextSprite == null || camera == null)
        return;
      if (!TrySelectedWorldBounds(s, out Vector3 center, out float radius)) return;

      int viewportWidth = Math.Max(1, (int)Viewport.Width);
      int viewportHeight = Math.Max(1, (int)Viewport.Height);
      if (viewportWidth <= 0 || viewportHeight <= 0) return;
      if (viewportWidth != npcNameplateViewportWidth || viewportHeight != npcNameplateViewportHeight) {
        npcNameplateViewportWidth = viewportWidth;
        npcNameplateViewportHeight = viewportHeight;
        try { npcTextSprite.RefreshViewport(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine("Selection text viewport refresh failed: " + ex.Message); }
        npcNameplateVisible.Clear();
        npcNameplateHasVisibility = false;
        npcNameplateVisibilityAskedAt = Int32.MinValue / 2;
      }

      Vector3 screenUp = camera.Up;
      if (!IsFinite(screenUp) || screenUp.LengthSquared() < .000001f) screenUp = Vector3.UnitY; else screenUp.Normalize();
      Vector3 anchor = center + screenUp * Math.Max(.10f, radius * 1.08f);
      Vector3 screen = Vector3.Project(anchor, 0f, 0f, viewportWidth, viewportHeight, 0f, 1f, labelViewProj);
      if (!IsFinite(screen) || screen.Z < 0f || screen.Z > 1f || screen.X < -40f || screen.X > viewportWidth + 40f || screen.Y < -80f || screen.Y > viewportHeight + 20f) return;

      AssetInstance instance = selectedWorldRenderEntry?.Instance ?? selectedWorldNpcPlacement?.Instance ?? selectedWorldSpnPlacement?.Instance ?? selectedWorldUtilityEntry?.Instance;
      Room room = selectedWorldRenderEntry?.Room ?? selectedWorldNpcPlacement?.Room ?? selectedWorldSpnPlacement?.Room ?? selectedWorldUtilityEntry?.Room;
      Vector3 position = center;
      try {
        if (selectedWorldRenderEntry != null) position = new Vector3(selectedWorldRenderEntry.World.M41, selectedWorldRenderEntry.World.M42, selectedWorldRenderEntry.World.M43);
        else if (selectedWorldNpcPlacement != null) { Matrix world = NpcNameplateWorld(selectedWorldNpcPlacement); position = new Vector3(world.M41, world.M42, world.M43); }
        else if (selectedWorldSpnPlacement != null) { Matrix world = SpnPlacementWorld(selectedWorldSpnPlacement, s.AnimateSpnObjects); position = new Vector3(world.M41, world.M42, world.M43); }
        else if (selectedWorldUtilityEntry != null) { Matrix world = InstanceWorld(selectedWorldUtilityEntry.Instance, selectedWorldUtilityEntry.Room); position = new Vector3(world.M41, world.M42, world.M43); }
        else if (selectedWorldPath != null) position = selectedWorldPathHit;
      } catch { position = center; }

      var lines = new List<string>();
      lines.Add(SelectionOverlayEllipsis(selectedWorldModelSummary, 74));
      if (selectedWorldPath != null) lines.Add("Path id: " + selectedWorldPath.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
      else if (instance != null) {
        string meta = "Instance: " + instance.ID.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (room != null && !String.IsNullOrWhiteSpace(room.RoomName)) meta += "  •  " + room.RoomName;
        lines.Add(SelectionOverlayEllipsis(meta, 74));
      }
      lines.Add(String.Format(System.Globalization.CultureInfo.InvariantCulture, "Position: {0:0}, {1:0}, {2:0}", position.X * 10f, position.Z * 10f, position.Y * 10f));
      if (selectedWorldCycleCount > 1 && selectedWorldCycleIndex >= 0)
        lines.Add((selectedWorldCycleIndex + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + " of " + selectedWorldCycleCount.ToString(System.Globalization.CultureInfo.InvariantCulture) + "  •  Ctrl+click again for next");

      float lineHeight = 14f;
      float y = Math.Max(4f, screen.Y - lines.Count * lineHeight - 5f);
      Color4 shadow = new Color4(.90f, 0f, 0f, 0f);
      Color4 title = new Color4(1f, .91f, .74f, .33f);
      Color4 detail = new Color4(1f, .69f, .91f, 1f);
      for (int i = 0; i < lines.Count; i++) {
        string line = lines[i];
        string font = i == 0 ? "world-selection" : "world-npc-item";
        DrawNpcText(line, new Vector2(screen.X + 1f, y + 1f), shadow, font);
        DrawNpcText(line, new Vector2(screen.X, y), i == 0 ? title : detail, font);
        y += lineHeight;
      }
      npcTextSprite.Flush();
    }

    private static Matrix ApplyNpcScale(Matrix world, float scale) {
      float target = scale > 0f ? scale : 1f;
      NormalizeMatrixRow(ref world.M11, ref world.M12, ref world.M13, target);
      NormalizeMatrixRow(ref world.M21, ref world.M22, ref world.M23, target);
      NormalizeMatrixRow(ref world.M31, ref world.M32, ref world.M33, target);
      return world;
    }

    private static void NormalizeMatrixRow(ref float x, ref float y, ref float z, float target) {
      float len = (float)Math.Sqrt(x*x + y*y + z*z); if (len <= .000001f) return;
      float f = target / len; x *= f; y *= f; z *= f;
    }

    private void ClearWalkingPlatform() {
      walkingPlatform = null;
      walkingPlatformWorldValid = false;
    }

    private void SetWalkingPlatform(WalkingPlatformSupport platform) {
      if (platform == null) { ClearWalkingPlatform(); return; }
      walkingPlatform = platform;
      walkingPlatformWorldValid = NpcFinite(platform.World);
      if (!walkingPlatformWorldValid) walkingPlatform = null;
    }

    private bool TryCurrentWalkingPlatformWorld(WorldRenderSettings s, out Matrix world) {
      world = Matrix.Identity;
      if (walkingPlatform == null) return false;
      if (walkingPlatform.Spn != null) {
        world = walkingPlatform.LocalMatrix * SpnPlacementWorld(walkingPlatform.Spn, s.AnimateSpnObjects);
        return NpcFinite(world);
      }
      if (walkingPlatform.Instance != null && walkingPlatform.Room != null && walkingPlatform.Instance.PathFollowerAnimated) {
        world = InstanceWorld(walkingPlatform.Instance, walkingPlatform.Room);
        return NpcFinite(world);
      }
      return false;
    }

    private void CarryWalkingCameraWithPlatform(WorldRenderSettings s) {
      if (!walkingGrounded || walkingPlatform == null || !walkingPlatformWorldValid) return;
      try {
        if (!TryCurrentWalkingPlatformWorld(s, out Matrix current)) { ClearWalkingPlatform(); return; }
        Matrix previousInverse = Matrix.Invert(walkingPlatform.World);
        Matrix delta = previousInverse * current;
        Vector3 localCamera = Vector3.TransformCoordinate(camera.Position, previousInverse);
        Vector3 carried = Vector3.TransformCoordinate(localCamera, current);
        if (!IsFinite(carried)) { ClearWalkingPlatform(); return; }

        // A moving-parent transform can rotate as well as translate. Carry the view basis through the exact same
        // delta so standing on a turning tram/follower feels attached to it rather than letting the vehicle rotate
        // underneath the camera. LookAt re-orthogonalizes any tiny skew introduced by non-uniform model scale.
        Vector3 carriedLook = Vector3.TransformNormal(camera.Look, delta);
        Vector3 carriedUp = Vector3.TransformNormal(camera.Up, delta);
        if (IsFinite(carriedLook) && IsFinite(carriedUp) && carriedLook.LengthSquared() > .000001f && carriedUp.LengthSquared() > .000001f) {
          carriedLook.Normalize(); carriedUp.Normalize();
          camera.LookAt(carried, carried + carriedLook, carriedUp);
        } else camera.Position = carried;
        walkingPlatform.World = current;
      } catch {
        ClearWalkingPlatform();
      }
    }

    private bool UpdateWalkingMode(float dt) {
      WorldRenderSettings s = SettingsSnapshot();
      if (!s.WalkingMode) {
        walkingWasEnabled = false; walkingVerticalVelocity = 0f; walkingGrounded = false; walkingSpaceWasDown = Util.IsKeyDown(Keys.Space);
        ClearWalkingPlatform();
        return false;
      }

      if (!walkingWasEnabled) {
        walkingWasEnabled = true; walkingVerticalVelocity = 0f; walkingGrounded = false;
        if (TryWalkingFloor(camera.Position.X, camera.Position.Z, camera.Position.Y + .05f, 8f,
            out float initialFloor, out WalkingPlatformSupport initialPlatform)) {
          camera.Position = new Vector3(camera.Position.X, initialFloor + WalkingEyeHeight, camera.Position.Z); walkingGrounded = true;
          SetWalkingPlatform(initialPlatform);
        }
      } else {
        // Apply the moving support first, before WASD/gravity. This keeps the camera at the same local point on a
        // lift/tram and also rotates the view with a turning /engine/follower.fol parent.
        CarryWalkingCameraWithPlatform(s);
      }

      bool shift = Util.IsKeyDown(Keys.LShiftKey) || Util.IsKeyDown(Keys.RShiftKey);
      float speed = cameraSpeed * (shift ? 2f : 1f);
      Vector3 forward = new Vector3(-camera.Look.X, 0f, -camera.Look.Z);
      Vector3 right = new Vector3(camera.Right.X, 0f, camera.Right.Z);
      if (forward.LengthSquared() > .000001f) forward.Normalize();
      if (right.LengthSquared() > .000001f) right.Normalize();
      Vector3 move = Vector3.Zero;
      if (Util.IsKeyDown(Keys.W)) move += forward;
      if (Util.IsKeyDown(Keys.S)) move -= forward;
      if (Util.IsKeyDown(Keys.D)) move += right;
      if (Util.IsKeyDown(Keys.A)) move -= right;
      if (move.LengthSquared() > 1f) move.Normalize();

      bool space = Util.IsKeyDown(Keys.Space);
      if (walkingGrounded && space && !walkingSpaceWasDown) {
        walkingVerticalVelocity = WalkingJumpVelocity; walkingGrounded = false; ClearWalkingPlatform();
      }
      walkingSpaceWasDown = space;

      Vector3 pos = camera.Position;
      float oldFloor = pos.Y - WalkingEyeHeight;
      Vector3 candidate = pos + move * (speed * dt);
      if (walkingGrounded && move.LengthSquared() > .000001f) {
        if (TryWalkingFloor(candidate.X, candidate.Z, oldFloor + WalkingStepHeight + .02f, .65f,
            out float nextFloor, out WalkingPlatformSupport nextPlatform) &&
            nextFloor - oldFloor <= WalkingStepHeight + .005f) {
          candidate.Y = nextFloor + WalkingEyeHeight;
          oldFloor = nextFloor;
          SetWalkingPlatform(nextPlatform);
        } else { candidate.X = pos.X; candidate.Z = pos.Z; }
      }

      if (!walkingGrounded) {
        walkingVerticalVelocity -= WalkingGravity * dt;
        candidate.Y += walkingVerticalVelocity * dt;
        if (TryWalkingFloor(candidate.X, candidate.Z, candidate.Y + WalkingEyeHeight, 1.2f,
            out float floor, out WalkingPlatformSupport landedPlatform) &&
            candidate.Y <= floor + WalkingEyeHeight + .015f && walkingVerticalVelocity <= 0f) {
          candidate.Y = floor + WalkingEyeHeight; walkingVerticalVelocity = 0f; walkingGrounded = true;
          SetWalkingPlatform(landedPlatform);
        }
      } else {
        if (TryWalkingFloor(candidate.X, candidate.Z, candidate.Y + .03f, .25f,
            out float floor, out WalkingPlatformSupport standingPlatform)) {
          candidate.Y = floor + WalkingEyeHeight;
          SetWalkingPlatform(standingPlatform);
        } else {
          walkingGrounded = false;
          ClearWalkingPlatform();
        }
      }
      camera.Position = candidate;
      return true;
    }

    private bool TryWalkingFloor(float x, float z, float ceilingY, float maxDrop, out float bestY) {
      WalkingPlatformSupport ignoredPlatform;
      return TryWalkingFloor(x, z, ceilingY, maxDrop, out bestY, out ignoredPlatform);
    }

    private bool TryWalkingFloor(float x, float z, float ceilingY, float maxDrop, out float bestY,
        out WalkingPlatformSupport bestPlatform) {
      // C# does not allow an out/ref/in parameter to be captured by a local function. Accumulate in ordinary locals.
      float localBestY = float.MinValue;
      WalkingPlatformSupport localBestPlatform = null;
      Vector3 query = new Vector3(x, ceilingY, z);
      if (heightMapFloorGrid.TryGetValue((FloorCell(x), FloorCell(z)), out List<HeightMapFloorEntry> hmEntries)) {
        foreach (HeightMapFloorEntry entry in hmEntries) {
          if (entry == null || !TryHeightMapFloorY(entry, query, out float y) || y > ceilingY + .0001f || ceilingY - y > maxDrop) continue;
          if (y > localBestY) { localBestY = y; localBestPlatform = null; }
        }
      }
      void Consider(IEnumerable<ModelFloorPlacementEntry> placements) {
        if (placements == null) return;
        foreach (ModelFloorPlacementEntry placement in placements) {
          if (placement == null || x < placement.WorldMinX-.0001f || x > placement.WorldMaxX+.0001f || z < placement.WorldMinZ-.0001f || z > placement.WorldMaxZ+.0001f) continue;
          Vector3 local = Vector3.TransformCoordinate(query, placement.Inverse);
          foreach (FloorMeshData md in placement.Model.Meshes) foreach (int offset in FloorMeshCandidateOffsets(md, local, placement.LocalXZIndependentOfY)) {
            if (!TryFloorTriangleHit(md.Mesh, offset, placement.World, query, out float y) || y > ceilingY + .0001f || ceilingY - y > maxDrop) continue;
            if (y > localBestY) { localBestY = y; localBestPlatform = null; }
          }
        }
      }
      modelFloorPlacementGrid.TryGetValue((ModelFloorPlacementCell(x), ModelFloorPlacementCell(z)), out List<ModelFloorPlacementEntry> bucket);
      Consider(bucket); Consider(modelFloorPlacementGlobal);

      void ConsiderDynamicModel(GR2 model, AssetInstance instance, Room room, Matrix world, WorldSpnPlacement spn, Matrix? localMatrix = null) {
        if (model == null || instance == null || !NpcFinite(world) ||
            !TryModelWorldXZBounds(model, world, out float minX, out float maxX, out float minZ, out float maxZ) ||
            x < minX-.0001f || x > maxX+.0001f || z < minZ-.0001f || z > maxZ+.0001f) return;
        Matrix inverse;
        try { inverse = Matrix.Invert(world); } catch { return; }
        bool localXZIndependentOfY = Math.Abs(inverse.M21) < .000001f && Math.Abs(inverse.M23) < .000001f;
        Vector3 localQuery = Vector3.TransformCoordinate(query, inverse);
        ModelFloorData floorData = GetOrBuildModelFloorData(model, instance);
        if (floorData == null || floorData.Meshes.Count == 0) return;
        foreach (FloorMeshData md in floorData.Meshes) foreach (int offset in FloorMeshCandidateOffsets(md, localQuery, localXZIndependentOfY)) {
          if (!TryFloorTriangleHit(md.Mesh, offset, world, query, out float y) || y > ceilingY + .0001f || ceilingY - y > maxDrop) continue;
          // Prefer dynamic support over coincident static shaft/station geometry. Otherwise a platform at a stop can
          // lose its rider for one frame and then move away without carrying the camera.
          if (y > localBestY + WalkingPlatformFloorEpsilon ||
              (Math.Abs(y - localBestY) <= WalkingPlatformFloorEpsilon && localBestPlatform == null)) {
            localBestY = y;
            localBestPlatform = new WalkingPlatformSupport { Spn = spn, Instance = instance, Room = room, World = world, LocalMatrix = localMatrix ?? Matrix.Identity };
          }
        }
      }

      WorldRenderSettings settings = SettingsSnapshot();
      // Server-spawned moving placeables are not in the area's static render/floor indices.
      if (settings.ShowSpnObjects && walkingSpnPlatforms != null) {
        foreach (WorldSpnPlacement platform in walkingSpnPlatforms) {
          if (platform?.Models == null || platform.Models.Count == 0 || platform.Instance == null) continue;
          if (!SpnVariantActive(platform.VariantIndex, platform.VariantCount, platform.SpawnPoints)) continue;
          Matrix world;
          try { world = SpnPlacementWorld(platform, settings.AnimateSpnObjects); } catch { continue; }

          WorldSpnDynState dynState = ActiveSpnDynState(platform, settings.AnimateSpnObjects);
          if (dynState != null) {
            if (dynState.Hidden) continue;
            foreach (WorldSpnDynPart part in dynState.Parts) {
              if (part?.Model == null) continue;
              Matrix partWorld = part.LocalMatrix * world;
              ConsiderDynamicModel(part.Model, platform.Instance, platform.Room, partWorld, platform, part.LocalMatrix);
            }
          } else {
            foreach (GR2 model in platform.Models) ConsiderDynamicModel(model, platform.Instance, platform.Room, world, platform);
          }
        }
      }

      // Jedipedia's /engine/follower.fol is a moving parent used for trams, ships and vista traffic. Its descendants
      // are deliberately excluded from the static floor grid because their matrices change each frame; test their
      // current render matrices here so Walking Mode can actually ride a follower-driven platform as well.
      foreach (RenderEntry entry in walkingPathFollowerRenderEntries) {
        if (entry == null || entry.Model == null || entry.Instance == null ||
            !entry.Instance.PathFollowerAnimated || entry.Instance.PathFollowerPending) continue;
        Matrix world = InstanceWorld(entry.Instance, entry.Room);
        ConsiderDynamicModel(entry.Model, entry.Instance, entry.Room, world, null);
      }

      bestY = localBestY;
      bestPlatform = localBestPlatform;
      return localBestY > float.MinValue * .5f;
    }

    private Bitmap CropMiniMapToArea(Bitmap source, out float minX, out float maxX, out float minZ, out float maxZ) {
      minX = mapExtentMinX; maxX = mapExtentMaxX; minZ = mapExtentMinZ; maxZ = mapExtentMaxZ;
      if (source == null || mapVisibleWidth <= 0f || mapVisibleHeight <= 0f || maxX <= minX || maxZ <= minZ) return source;
      float captureMinX = mapCenter.X - mapVisibleWidth * .5f, captureMaxX = mapCenter.X + mapVisibleWidth * .5f;
      float captureMinZ = mapCenter.Y - mapVisibleHeight * .5f, captureMaxZ = mapCenter.Y + mapVisibleHeight * .5f;
      float u0 = (minX - captureMinX) / Math.Max(.0001f, captureMaxX - captureMinX);
      float u1 = (maxX - captureMinX) / Math.Max(.0001f, captureMaxX - captureMinX);
      float v0 = (minZ - captureMinZ) / Math.Max(.0001f, captureMaxZ - captureMinZ);
      float v1 = (maxZ - captureMinZ) / Math.Max(.0001f, captureMaxZ - captureMinZ);
      int left = Math.Max(0, Math.Min(source.Width - 1, (int)Math.Floor(u0 * source.Width)));
      int right = Math.Max(left + 1, Math.Min(source.Width, (int)Math.Ceiling(u1 * source.Width)));
      int top = Math.Max(0, Math.Min(source.Height - 1, (int)Math.Floor(v0 * source.Height)));
      int bottom = Math.Max(top + 1, Math.Min(source.Height, (int)Math.Ceiling(v1 * source.Height)));
      if (left == 0 && top == 0 && right == source.Width && bottom == source.Height) return source;
      try {
        Bitmap cropped = source.Clone(new System.Drawing.Rectangle(left, top, right-left, bottom-top), source.PixelFormat);
        source.Dispose(); return cropped;
      } catch { return source; }
    }

    public bool HasPinnedWorldVolume {
      get { lock (pinnedVolumeGpu) return pinnedVolumeGpuByKey.Count > 0; }
    }

    public bool IsWorldVolumePinned(ulong instanceId,string roomName){
      lock (pinnedVolumeGpu) return pinnedVolumeGpuByKey.ContainsKey(WorldVolumeKey(roomName,instanceId));
    }

    public void ClearPinnedWorldVolume(){
      lock (pinnedVolumeGpu) {
        DisposeLines(pinnedVolumeGpu);
        pinnedVolumeGpuByKey.Clear();
        pinnedVolumePointsByKey.Clear();
      }
    }

    public List<Vector2> PinnedWorldVolumeMapSegments {
      get {
        var result = new List<Vector2>();
        lock (pinnedVolumeGpu) {
          foreach (Vector3[] points in pinnedVolumePointsByKey.Values) {
            if (points == null) continue;
            for (int i = 0; i + 1 < points.Length; i += 2) {
              result.Add(new Vector2(points[i].X, points[i].Z));
              result.Add(new Vector2(points[i + 1].X, points[i + 1].Z));
            }
          }
        }
        return result;
      }
    }

    public bool TogglePinnedWorldVolume(ulong instanceId,string roomName){
      string key=WorldVolumeKey(roomName,instanceId);
      lock (pinnedVolumeGpu) {
        if(pinnedVolumeGpuByKey.TryGetValue(key,out LineGpu existing)){
          pinnedVolumeGpuByKey.Remove(key);
          pinnedVolumePointsByKey.Remove(key);
          pinnedVolumeGpu.Remove(existing);
          existing?.Dispose();
          return false;
        }
        if(area==null||instanceId==0)return false;
        Room room=rooms.FirstOrDefault(r=>r!=null&&String.Equals(r.RoomName??String.Empty,roomName??String.Empty,StringComparison.OrdinalIgnoreCase));
        if(room==null||!room.InstancesById.TryGetValue(instanceId,out AssetInstance inst)||inst==null||!area.AssetIdMap.TryGetValue(inst.assetID,out AreaAsset asset)||asset==null)return false;
        string ext=(asset.Extension??String.Empty).Trim().TrimStart('.').ToLowerInvariant();var points=new List<Vector3>();
        if(ext=="rgn"&&inst.RegionVolume!=null)AddRegionVolumeEdges(points,inst,room);else if(ext=="trg")AddVolumeEdges(points,inst,room);else return false;
        if(points.Count<2)return false;
        LineGpu line=BuildLine(points,new Vector4(.2f,1f,.95f,1f));
        if(line==null)return false;
        pinnedVolumeGpu.Add(line);
        pinnedVolumeGpuByKey[key]=line;
        pinnedVolumePointsByKey[key]=points.ToArray();
        return true;
      }
    }

    private void ReleaseJedipediaFeatureGpu() {
      DisposeLines(utilityPathGpu); DisposeLines(utilityMapPathGpu); DisposeLines(utilityConnectionGpu); DisposeLines(utilityVolumeGpu);
      lock (pinnedVolumeGpu) { DisposeLines(pinnedVolumeGpu); pinnedVolumeGpuByKey.Clear(); pinnedVolumePointsByKey.Clear(); }
      DisposeLines(phaseGatewayGpu); DisposeLines(phaseGatewayPlaneGpu);
      DisposeLines(utilitySpawnerFallbackGpu); DisposeLines(utilityCoverFallbackGpu); DisposeLines(utilityLightFallbackGpu); DisposeLines(utilitySeedFallbackGpu); DisposeLines(utilityOtherGpu);
      utilityRenderEntries.Clear();
      phaseRegionTriggers.Clear();
      currentPhaseName = implicitPhaseName ?? String.Empty;
      foreach (Dictionary<WorldNpcAnimationClip, NpcSkinState> states in npcSkinStates.Values) foreach (NpcSkinState state in states.Values) state?.Dispose();
      npcSkinStates.Clear();
      var released = new HashSet<GR2>();
      foreach (GR2 model in utilityMarkerModels.Values) if (model != null && released.Add(model)) ReleaseModelBuffers(model);
      foreach (WorldNpcPlacement p in npcPlacements) if (p?.Models != null) foreach (GR2 model in p.Models) if (model != null && released.Add(model)) ReleaseModelBuffers(model);
      foreach (WorldSpnPlacement p in spnPlacements) if (p?.Models != null) foreach (GR2 model in p.Models) if (model != null && released.Add(model)) ReleaseModelBuffers(model);
      utilityMarkerModels = new Dictionary<string, GR2>(StringComparer.OrdinalIgnoreCase);
      npcPlacements = new List<WorldNpcPlacement>();
      npcSpatialGrid.Clear();
      npcSpatialFallback.Clear();
      spnSpatialGrid.Clear();
      spnSpatialFallback.Clear();
      spnPlacements = new List<WorldSpnPlacement>();
      npcGeometryPrepared = false;
      spnGeometryPrepared = false;
      walkingSpnPlatforms = new List<WorldSpnPlacement>();
      dynamicSpnLocalLights.Clear();
      spnMotionRoutes.Clear();
      npcMotionRoutes.Clear();
      ClearWalkingPlatform();
    }

    private void DisposeFeatureTextRenderer() {
      npcTextFontsRegistered = false;
      npcTextFont?.Dispose(); npcTextFont = null;
      npcTextSprite?.Dispose(); npcTextSprite = null;
      npcNameplateVisibilityTarget?.Dispose(); npcNameplateVisibilityTarget = null;
      npcNameplateVisibilityTexture?.Dispose(); npcNameplateVisibilityTexture = null;
      npcNameplateVisibilityStaging?.Dispose(); npcNameplateVisibilityStaging = null;
      objectOcclusionVisibilityTarget?.Dispose(); objectOcclusionVisibilityTarget = null;
      objectOcclusionVisibilityTexture?.Dispose(); objectOcclusionVisibilityTexture = null;
      objectOcclusionVisibilityStaging?.Dispose(); objectOcclusionVisibilityStaging = null;
      objectOcclusionHidden.Clear(); objectOcclusionHiddenCounts.Clear(); dynamicOcclusionHidden.Clear(); dynamicOcclusionHiddenCounts.Clear(); dynamicOcclusionPoses.Clear(); objectOcclusionHasVisibility = false;
      npcNameplateVisible.Clear();
      npcNameplateHasVisibility = false;
      npcNameplateViewportWidth = -1;
      npcNameplateViewportHeight = -1;
    }
  }
}
