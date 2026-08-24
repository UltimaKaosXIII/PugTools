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

    private const byte UtilitySpawner = 1;
    private const byte UtilityCover = 2;
    private const byte UtilityLight = 3;
    private const byte UtilitySeed = 4;
    private const byte UtilityOther = 5;

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
      public readonly Dictionary<GR2_Mesh, PosNormalTexTan[]> SkinnedVertices = new Dictionary<GR2_Mesh, PosNormalTexTan[]>();
      public readonly Dictionary<GR2_Mesh, Buffer> DynamicBuffers = new Dictionary<GR2_Mesh, Buffer>();
      public int BoundBoneCount;

      public void Dispose() {
        foreach (Buffer buffer in DynamicBuffers.Values) buffer?.Dispose();
        DynamicBuffers.Clear();
        Palettes.Clear();
        SkinnedVertices.Clear();
        SkinMatrices.Clear();
      }
    }

    private readonly Dictionary<GR2, Dictionary<WorldNpcAnimationClip, NpcSkinState>> npcSkinStates = new Dictionary<GR2, Dictionary<WorldNpcAnimationClip, NpcSkinState>>();
    private Dictionary<string, GR2> utilityMarkerModels = new Dictionary<string, GR2>(StringComparer.OrdinalIgnoreCase);
    private List<WorldNpcPlacement> npcPlacements = new List<WorldNpcPlacement>();
    private List<WorldSpnPlacement> spnPlacements = new List<WorldSpnPlacement>();
    private int npcFactionHint = Int32.MinValue; // -1 Empire, +1 Republic, 0 unknown/mixed
    private readonly List<UtilityRenderEntry> utilityRenderEntries = new List<UtilityRenderEntry>();
    private readonly List<LineGpu> utilityPathGpu = new List<LineGpu>();
    private readonly List<LineGpu> utilityMapPathGpu = new List<LineGpu>();
    private readonly List<LineGpu> utilityConnectionGpu = new List<LineGpu>();
    private readonly List<LineGpu> utilityVolumeGpu = new List<LineGpu>();
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
    private float walkingVerticalVelocity;
    private bool walkingWasEnabled;
    private bool walkingGrounded;
    private bool walkingSpaceWasDown;
    private volatile string implicitPhaseName = String.Empty;
    private volatile string currentPhaseName = String.Empty;
    private const float WalkingEyeHeight = .18f;
    private const float WalkingStepHeight = .075f;
    private const float WalkingGravity = .98f;
    private const float WalkingJumpVelocity = .42f;

    private void SetJedipediaFeatureData(Dictionary<string, GR2> utilityModels, List<WorldNpcPlacement> npcData, List<WorldSpnPlacement> spnData) {
      utilityMarkerModels = utilityModels ?? new Dictionary<string, GR2>(StringComparer.OrdinalIgnoreCase);
      npcPlacements = npcData ?? new List<WorldNpcPlacement>();
      spnPlacements = spnData ?? new List<WorldSpnPlacement>();
      npcFactionHint = Int32.MinValue;
      currentPhaseName = implicitPhaseName ?? String.Empty;
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
        npcTextFontsRegistered = true;
      } catch (Exception ex) {
        System.Diagnostics.Debug.WriteLine("NPC nameplate renderer unavailable: " + ex.Message);
        npcTextFontsRegistered = false;
        npcTextFont?.Dispose(); npcTextFont = null;
        npcTextSprite?.Dispose(); npcTextSprite = null;
      }
    }

    private void BuildJedipediaFeatureGeometry(HashSet<GR2> built) {
      foreach (GR2 model in utilityMarkerModels.Values) if (model != null) BuildModelGeometry(model, built, false);
      foreach (WorldNpcPlacement placement in npcPlacements)
        if (placement?.Models != null) foreach (GR2 model in placement.Models) if (model != null) BuildModelGeometry(model, built, false);
      foreach (WorldSpnPlacement placement in spnPlacements)
        if (placement?.Models != null) foreach (GR2 model in placement.Models) if (model != null) BuildModelGeometry(model, built, false);
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
            if (!String.IsNullOrWhiteSpace(markerKey) && utilityMarkerModels.TryGetValue(markerKey, out GR2 marker) && marker != null) {
              utilityRenderEntries.Add(new UtilityRenderEntry { Room = room, Instance = inst, Model = marker, Category = category, Color = UtilityColor(category) });
            } else {
              // Some of Jedipedia's baked beta-era markers do not exist in every live client. Never make the
              // placement disappear just because the pretty marker is unavailable: a small colour-coded cross
              // still exposes the authored utility at the correct world transform.
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

          if (ext == "rgn" || ext == "trg" || ext == "env") AddVolumeEdges(volumePoints, inst, room);
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
      bool anyMarkers = s.ShowUtilitySpawners || s.ShowUtilityCoverPoints || s.ShowUtilityLights || s.ShowUtilitySeedPoints || s.ShowUtilityOther;
      if (anyMarkers) {
        ImmediateContext.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
        foreach (UtilityRenderEntry entry in utilityRenderEntries) {
          if (entry?.Instance == null || entry.Model == null || !InstanceRoomVisible(entry.Instance, entry.Room, visible)) continue;
          if (s.Mode == WorldRenderMode.Map ? !InstanceVisibleOnMap(entry.Instance) : !InstanceVisibleInWorld(entry.Instance)) continue;
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
      fx.SetWorld(world); fx.SetViewProj(vp); fx.SetMaterial(null); fx.SetOverlay(color);
      foreach (GR2_Mesh mesh in model.meshes) {
        if (mesh?.vertBuffer == null || mesh.idxBuffer == null || mesh.meshVertIndex == null || mesh.meshVertIndex.Count == 0) continue;
        ImmediateContext.InputAssembler.SetVertexBuffers(0, new VertexBufferBinding(mesh.vertBuffer, PosNormalTexTan.Stride, 0));
        ImmediateContext.InputAssembler.SetIndexBuffer(mesh.idxBuffer, Format.R16_UInt, 0);
        fx.Overlay.GetPassByIndex(0).Apply(ImmediateContext);
        ImmediateContext.DrawIndexed(mesh.meshVertIndex.Count, 0, 0);
      }
    }

    private void DrawJedipediaNpcs(Matrix vp, HashSet<string> visible, WorldRenderSettings s) {
      if (!s.ShowNpcs || npcPlacements == null || npcPlacements.Count == 0) return;
      foreach (WorldNpcPlacement placement in npcPlacements) {
        if (placement?.Instance == null || placement.Room == null || placement.Models == null || !InstanceRoomVisible(placement.Instance, placement.Room, visible)) continue;
        if (s.Mode == WorldRenderMode.Map ? !InstanceVisibleOnMap(placement.Instance) : !InstanceVisibleInWorld(placement.Instance)) continue;

        Matrix baseWorld = ApplyNpcScale(InstanceWorld(placement.Instance, placement.Room), placement.Scale);
        // SWTOR spawner facing is authored opposite to the GR2 character forward axis. Apply the same
        // half-turn to both bind-pose and animated NPCs; previously only the static fallback got it.
        Matrix npcWorld = Matrix.RotationY((float)Math.PI) * baseWorld;
        bool animate = s.AnimateNpcs && placement.Animation?.Animation != null;
        float animationTime = 0f;
        if (animate) {
          float length = placement.Animation.Animation.Length;
          animationTime = length > 0f ? (elapsed + placement.AnimationPhase * length) % length : 0f;
          if (animationTime < 0f) animationTime += length;
        }

        foreach (GR2 model in placement.Models) {
          if (!animate || !TryDrawAnimatedNpcModel(model, npcWorld, vp, s, placement.Animation, animationTime))
            DrawModel(model, npcWorld, vp, s, false, null);
        }
      }
    }

    private void DrawJedipediaSpnObjects(Matrix vp, HashSet<string> visible, WorldRenderSettings s) {
      if (!s.ShowSpnObjects || spnPlacements == null || spnPlacements.Count == 0) return;
      foreach (WorldSpnPlacement placement in spnPlacements) {
        if (placement?.Instance == null || placement.Room == null || placement.Models == null || !InstanceRoomVisible(placement.Instance, placement.Room, visible)) continue;
        if (s.Mode == WorldRenderMode.Map ? !InstanceVisibleOnMap(placement.Instance) : !InstanceVisibleInWorld(placement.Instance)) continue;
        Matrix world = ApplyNpcScale(InstanceWorld(placement.Instance, placement.Room), placement.Scale);
        bool animate = s.AnimateSpnObjects && placement.Animation?.Animation != null;
        float animationTime = 0f;
        if (animate) {
          float length = placement.Animation.Animation.Length;
          animationTime = length > 0f ? (elapsed + placement.AnimationPhase * length) % length : 0f;
          if (animationTime < 0f) animationTime += length;
        }
        foreach (GR2 model in placement.Models) {
          if (!animate || !TryDrawAnimatedNpcModel(model, world, vp, s, placement.Animation, animationTime))
            DrawModel(model, world, vp, s, false, null);
        }
      }
    }

    private Matrix NpcNameplateWorld(WorldNpcPlacement placement) {
      return ApplyNpcScale(InstanceWorld(placement.Instance, placement.Room), placement.Scale);
    }

    private bool TryDrawAnimatedNpcModel(GR2 model, Matrix world, Matrix vp, WorldRenderSettings s, WorldNpcAnimationClip clip, float animationTime) {
      NpcSkinState state = GetNpcSkinState(model, clip);
      if (state == null || state.BoundBoneCount <= 0) return false;
      if (!UpdateNpcSkinState(state, animationTime)) return false;
      DrawNpcModelWithSkinBuffers(model, state, world, vp, s);
      return true;
    }

    private NpcSkinState GetNpcSkinState(GR2 model, WorldNpcAnimationClip clip) {
      IList<GR2_Bone_Skeleton> animationSkeleton = clip?.Skeleton != null && clip.Skeleton.Count > 0 ? clip.Skeleton : model?.skeleton_bones;
      if (model == null || clip?.Animation == null || animationSkeleton == null || animationSkeleton.Count == 0 || Device == null) return null;
      if (!npcSkinStates.TryGetValue(model, out Dictionary<WorldNpcAnimationClip, NpcSkinState> byClip)) {
        byClip = new Dictionary<WorldNpcAnimationClip, NpcSkinState>();
        npcSkinStates[model] = byClip;
      }
      if (byClip.TryGetValue(clip, out NpcSkinState cached)) return cached;

      NpcSkinState state = BuildNpcSkinState(model, clip);
      byClip[clip] = state;
      return state;
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
          var description = new BufferDescription(
            checked(PosNormalTexTan.Stride * mesh.meshVerts.Count),
            ResourceUsage.Dynamic,
            BindFlags.VertexBuffer,
            CpuAccessFlags.Write,
            ResourceOptionFlags.None,
            0
          );
          state.DynamicBuffers[mesh] = new Buffer(Device, description);
          state.SkinnedVertices[mesh] = new PosNormalTexTan[mesh.meshVerts.Count];
          state.Palettes[mesh] = new Matrix[mesh.meshBones.Count];
        }
        return state;
      } catch (Exception ex) {
        System.Diagnostics.Debug.WriteLine("NPC JBA state build failed: " + ex.Message);
        return null;
      }
    }

    private bool UpdateNpcSkinState(NpcSkinState state, float animationTime) {
      if (state?.Clip?.Animation == null || state.PoseSamples == null || state.BoundBoneCount <= 0) return false;
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

        foreach (var pair in state.DynamicBuffers) {
          GR2_Mesh mesh = pair.Key;
          Buffer buffer = pair.Value;
          Matrix[] palette = state.Palettes[mesh];
          for (int i = 0; i < palette.Length; i++) {
            string boneName = NpcCanonicalAnimationBoneName(mesh.meshBones[i].boneName);
            palette[i] = state.SkinMatrices.TryGetValue(boneName, out Matrix skin) ? skin : Matrix.Identity;
          }

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
            skinned[i] = new PosNormalTexTan(
              position,
              new Vector3(src.normX, src.normY, src.normZ),
              new Vector2(src.texU, src.texV),
              new Vector3(src.tanX, src.tanY, src.tanZ)
            );
          }

          DataBox mapped = ImmediateContext.MapSubresource(buffer, MapMode.WriteDiscard, SlimDX.Direct3D11.MapFlags.None);
          mapped.Data.WriteRange(skinned);
          ImmediateContext.UnmapSubresource(buffer, 0);
        }
        return state.DynamicBuffers.Count > 0;
      } catch (Exception ex) {
        System.Diagnostics.Debug.WriteLine("NPC JBA CPU skinning failed: " + ex.Message);
        return false;
      }
    }

    private static void NpcApplySkinWeight(Vector3 original, float weight, float encodedBoneIndex, Matrix[] palette, ref Vector3 position, ref float totalWeight) {
      if (weight <= .00001f || palette == null) return;
      int boneIndex = (int)Math.Round(encodedBoneIndex * 255f);
      if (boneIndex < 0 || boneIndex >= palette.Length) return;
      position += Vector3.TransformCoordinate(original, palette[boneIndex]) * weight;
      totalWeight += weight;
    }

    private void DrawNpcModelWithSkinBuffers(GR2 model, NpcSkinState state, Matrix world, Matrix vp, WorldRenderSettings s) {
      if (model == null) return;
      int selectedLod = SelectModelLodLevel(model, world, s.Mode == WorldRenderMode.Map);
      fx.SetWorld(world); fx.SetViewProj(vp);
      foreach (GR2_Mesh mesh in model.meshes) {
        Buffer vertexBuffer = state.DynamicBuffers.TryGetValue(mesh, out Buffer dynamicBuffer) ? dynamicBuffer : mesh.vertBuffer;
        if (vertexBuffer == null || mesh.idxBuffer == null || !MeshVisibleForLod(model, mesh, selectedLod)) continue;
        ImmediateContext.InputAssembler.SetVertexBuffers(0, new VertexBufferBinding(vertexBuffer, PosNormalTexTan.Stride, 0));
        ImmediateContext.InputAssembler.SetIndexBuffer(mesh.idxBuffer, Format.R16_UInt, 0);
        foreach (GR2_Mesh_Piece piece in mesh.meshPieces) {
          GR2_Material mat = null;
          if (piece.matId >= 0 && model.materials.ElementAtOrDefault(piece.matId) != null) materials.TryGetValue(model.materials[piece.matId].materialName, out mat);
          else if (model.materials.Count > 0) materials.TryGetValue(model.materials[0].materialName, out mat);
          if (IsMaterialHiddenFromWorld(mat)) continue;
          fx.SetMaterial(mat);
          PickModelTech(s, mat, false).GetPassByIndex(0).Apply(ImmediateContext);
          ImmediateContext.DrawIndexed((int)piece.numPieceFaces * 3, (int)piece.startIndex * 3, 0);
        }
      }
      // Slot-attached GR2s currently have their attachment transform baked into their static vertex buffer by the
      // world geometry builder. Keep drawing those through the proven static path until bone-bound equipment is ported.
      foreach (GR2 attached in model.attachedModels) DrawModel(attached, world, vp, s, false, null);
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

    private void DrawNpcNameplates(Matrix vp, HashSet<string> visible, WorldRenderSettings s) {
      if (!s.ShowNpcs || (!s.ShowNpcNames && !s.ShowNpcItems) || !npcTextFontsRegistered || npcTextFont == null || npcTextSprite == null) return;
      int drawn = 0;
      foreach (WorldNpcPlacement placement in npcPlacements) {
        if (drawn >= 200 || placement?.Instance == null || placement.Room == null || !placement.ShowNameplate || !InstanceRoomVisible(placement.Instance, placement.Room, visible)) continue;
        if (s.Mode == WorldRenderMode.Map ? !InstanceVisibleOnMap(placement.Instance) : !InstanceVisibleInWorld(placement.Instance)) continue;
        Matrix world = NpcNameplateWorld(placement);
        Vector3 basePos = new Vector3(world.M41, world.M42, world.M43);
        if (s.Mode != WorldRenderMode.Map && (basePos - camera.Position).Length() > 25f) continue;
        Vector3 anchor = basePos + new Vector3(0, WalkingEyeHeight * Math.Max(1f, placement.Scale) + .08f, 0);
        Vector3 screen = Vector3.Project(anchor, 0f, 0f, ClientWidth, ClientHeight, 0f, 1f, vp);
        if (!IsFinite(screen) || screen.Z < 0f || screen.Z > 1f || screen.X < -300f || screen.X > ClientWidth + 20f || screen.Y < -60f || screen.Y > ClientHeight + 60f) continue;
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
        drawn++;
      }
      npcTextSprite.Flush();
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

    private bool UpdateWalkingMode(float dt) {
      WorldRenderSettings s = SettingsSnapshot();
      if (!s.WalkingMode) {
        walkingWasEnabled = false; walkingVerticalVelocity = 0f; walkingGrounded = false; walkingSpaceWasDown = Util.IsKeyDown(Keys.Space);
        return false;
      }

      if (!walkingWasEnabled) {
        walkingWasEnabled = true; walkingVerticalVelocity = 0f; walkingGrounded = false;
        if (TryWalkingFloor(camera.Position.X, camera.Position.Z, camera.Position.Y + .05f, 8f, out float initialFloor)) {
          camera.Position = new Vector3(camera.Position.X, initialFloor + WalkingEyeHeight, camera.Position.Z); walkingGrounded = true;
        }
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
      if (walkingGrounded && space && !walkingSpaceWasDown) { walkingVerticalVelocity = WalkingJumpVelocity; walkingGrounded = false; }
      walkingSpaceWasDown = space;

      Vector3 pos = camera.Position;
      float oldFloor = pos.Y - WalkingEyeHeight;
      Vector3 candidate = pos + move * (speed * dt);
      if (walkingGrounded && move.LengthSquared() > .000001f) {
        if (TryWalkingFloor(candidate.X, candidate.Z, oldFloor + WalkingStepHeight + .02f, .65f, out float nextFloor) && nextFloor - oldFloor <= WalkingStepHeight + .005f) {
          candidate.Y = nextFloor + WalkingEyeHeight;
          oldFloor = nextFloor;
        } else { candidate.X = pos.X; candidate.Z = pos.Z; }
      }

      if (!walkingGrounded) {
        walkingVerticalVelocity -= WalkingGravity * dt;
        candidate.Y += walkingVerticalVelocity * dt;
        if (TryWalkingFloor(candidate.X, candidate.Z, candidate.Y + WalkingEyeHeight, 1.2f, out float floor) && candidate.Y <= floor + WalkingEyeHeight + .015f && walkingVerticalVelocity <= 0f) {
          candidate.Y = floor + WalkingEyeHeight; walkingVerticalVelocity = 0f; walkingGrounded = true;
        }
      } else {
        if (TryWalkingFloor(candidate.X, candidate.Z, candidate.Y + .03f, .25f, out float floor)) candidate.Y = floor + WalkingEyeHeight;
        else walkingGrounded = false;
      }
      camera.Position = candidate;
      return true;
    }

    private bool TryWalkingFloor(float x, float z, float ceilingY, float maxDrop, out float bestY) {
      // C# does not allow an out/ref/in parameter to be captured by a local function.
      // Accumulate into an ordinary local and copy it to the out parameter at the end.
      float localBestY = float.MinValue;
      Vector3 query = new Vector3(x, ceilingY, z);
      if (heightMapFloorGrid.TryGetValue((FloorCell(x), FloorCell(z)), out List<HeightMapFloorEntry> hmEntries)) {
        foreach (HeightMapFloorEntry entry in hmEntries) {
          if (entry == null || !TryHeightMapFloorY(entry, query, out float y) || y > ceilingY + .0001f || ceilingY - y > maxDrop) continue;
          if (y > localBestY) localBestY = y;
        }
      }
      void Consider(IEnumerable<ModelFloorPlacementEntry> placements) {
        if (placements == null) return;
        foreach (ModelFloorPlacementEntry placement in placements) {
          if (placement == null || x < placement.WorldMinX-.0001f || x > placement.WorldMaxX+.0001f || z < placement.WorldMinZ-.0001f || z > placement.WorldMaxZ+.0001f) continue;
          Vector3 local = Vector3.TransformCoordinate(query, placement.Inverse);
          foreach (FloorMeshData md in placement.Model.Meshes) foreach (int offset in FloorMeshCandidateOffsets(md, local, placement.LocalXZIndependentOfY)) {
            if (!TryFloorTriangleHit(md.Mesh, offset, placement.World, query, out float y) || y > ceilingY + .0001f || ceilingY - y > maxDrop) continue;
            if (y > localBestY) localBestY = y;
          }
        }
      }
      modelFloorPlacementGrid.TryGetValue((ModelFloorPlacementCell(x), ModelFloorPlacementCell(z)), out List<ModelFloorPlacementEntry> bucket);
      Consider(bucket); Consider(modelFloorPlacementGlobal);
      bestY = localBestY;
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

    private void ReleaseJedipediaFeatureGpu() {
      DisposeLines(utilityPathGpu); DisposeLines(utilityMapPathGpu); DisposeLines(utilityConnectionGpu); DisposeLines(utilityVolumeGpu); DisposeLines(phaseGatewayGpu); DisposeLines(phaseGatewayPlaneGpu);
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
      spnPlacements = new List<WorldSpnPlacement>();
    }

    private void DisposeFeatureTextRenderer() {
      npcTextFontsRegistered = false;
      npcTextFont?.Dispose(); npcTextFont = null;
      npcTextSprite?.Dispose(); npcTextSprite = null;
    }
  }
}
