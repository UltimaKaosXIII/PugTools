namespace PugTools {
  public enum WorldRenderMode {
    InGame,
    Lit,
    Unlit,
    Heightmap,
    Wireframe,
    Map
  }

  public enum WorldRenderBackend {
    Hardware,
    Warp
  }

  public enum WorldAntiAliasing {
    Off,
    FXAA,
    TAA,
    TAAFXAA
  }

  public enum WorldTextureQuality {
    Low,
    Medium,
    High
  }

  public enum WorldShadowQuality {
    Off,
    On,
    High
  }

  public sealed class WorldRenderSettings {
    public WorldRenderMode Mode { get; set; } = WorldRenderMode.InGame;
    public WorldRenderBackend Backend { get; set; } = WorldRenderBackend.Hardware;
    public WorldAntiAliasing AntiAliasing { get; set; } = WorldAntiAliasing.TAA;
    // Jedipedia maps Low/Medium/High to skipping 2/1/0 authored DDS mip levels. The world renderer applies the
    // choice to GR2 materials as well as environment/water/utility textures, so this is a real VRAM-quality setting.
    public WorldTextureQuality TextureQuality { get; set; } = WorldTextureQuality.High;
    public bool ShowTerrain { get; set; } = true;
    public bool ShowModels { get; set; } = true;
    // SWTOR SpeedTree placements (.spt). PugTools v12 renders the shipped same-stem Granny fallback where present;
    // raw SpeedTree decoding/wind remains a separate future path.
    public bool ShowSpeedTrees { get; set; } = true;
    public bool ShowDecorationHooks { get; set; } = true;
    public bool ShowDynamicDetails { get; set; } = true;
    public bool EnableLighting { get; set; } = true;
    public bool EnableFog { get; set; } = true;
    private WorldShadowQuality shadowQuality = WorldShadowQuality.On;
    // Keep the historical boolean facade for older call sites while exposing Jedipedia's Off / On / High tiers.
    public WorldShadowQuality ShadowQuality { get => shadowQuality; set => shadowQuality = value; }
    public bool EnableShadows {
      get => shadowQuality != WorldShadowQuality.Off;
      set {
        if (!value) shadowQuality = WorldShadowQuality.Off;
        else if (shadowQuality == WorldShadowQuality.Off) shadowQuality = WorldShadowQuality.On;
      }
    }
    public bool ShowRoads { get; set; } = false;
    public bool ShowMapArt { get; set; } = true;
    // Map-note classes are opt-in. Large SWTOR areas can carry hundreds/thousands of authored notes, so keeping
    // them disabled by default avoids both visual clutter and a surprisingly expensive per-frame map overlay pass.
    // ShowMapNotes is the internal master gate maintained by the toolbar setters below.
    public bool ShowMapNotes { get; set; } = false;
    public bool ShowMapIconBindpoints { get; set; } = false;
    public bool ShowMapIconMapLinks { get; set; } = false;
    public bool ShowMapIconQuests { get; set; } = false;
    public bool ShowMapIconTaxi { get; set; } = false;
    public bool ShowMapIconWonkavator { get; set; } = false;
    public bool ShowMapIconOther { get; set; } = false;
    public bool AnyMapIconEnabled => ShowMapIconBindpoints || ShowMapIconMapLinks || ShowMapIconQuests ||
      ShowMapIconTaxi || ShowMapIconWonkavator || ShowMapIconOther;
    public bool EnablePostProcessing { get; set; } = true;
    public bool EnableLocalLights { get; set; } = true;
    public bool EnableRoomVisibility { get; set; } = false;
    // Keep the normal 3D viewer on a small room working set like the game/Jedipedia. This is deliberately
    // separate from the authored dPVS-style Room culling toggle: outdoor planets often mark their cells
    // OutdoorsVisible, where the old Room culling intentionally failed open and therefore did no work at all.
    // The full map/minimap bypasses this so map functionality and whole-area rendering remain unchanged.
    public bool EnableLocalRoomStreaming { get; set; } = true;
    // Jedipedia/dPVS uses authored LOD -3 and OCCLUDER_ONLY geometry for visibility. The offline D3D11 renderer
    // cannot run Jedipedia's native solver, but a conservative depth-only prepass provides the same occlusion depth
    // to normal rendering/nameplates and cuts expensive overdraw in dense interiors.
    public bool EnableOccluderPrepass { get; set; } = false;
    // Jedipedia/dPVS also rejects fully hidden receivers before their expensive material/light passes. PugTools v13
    // approximates this conservatively from the previous depth image: only small/medium static model spheres that
    // remain hidden for two consecutive GPU depth probes are skipped, and camera/room changes fail open immediately.
    public bool EnableObjectOcclusionCulling { get; set; } = true;
    public bool ShowSky { get; set; } = true;
    public bool ShowWater { get; set; } = true;

    // Jedipedia-style authoring/utility overlay. The toolbar exposes these as a dropdown of
    // independent checkboxes instead of forcing the editor helpers into the ordinary Models pass.
    public bool ShowUtilitySpawners { get; set; } = false;
    public bool ShowUtilityCoverPoints { get; set; } = false;
    public bool ShowUtilityLights { get; set; } = false;
    public bool ShowUtilitySeedPoints { get; set; } = false;
    // Utility helpers are opt-in by default for a cleaner/faster world view; phase gateways remain the one default-on helper.
    public bool ShowUtilityPaths { get; set; } = false;
    public bool ShowUtilityMapRoadPaths { get; set; } = false;
    public bool ShowUtilityConnections { get; set; } = false;
    public bool ShowUtilityVolumes { get; set; } = false;
    public bool ShowPhaseGateways { get; set; } = true;
    public bool ShowUtilityOther { get; set; } = false;
    // Hidden/editor-only collision, occlusion and helper geometry is opt-in.
    // The Utilities submenu is the single visibility gate; no additional World display tier is required.
    public bool ShowHiddenGeometry { get; set; } = false;
    public bool ShowVolumeList { get; set; } = false;
    // The object label is the default selection feedback. The old wireframe bounds box can become enormous for
    // shell/portal assets, so keep it opt-in.
    public bool ShowSelectionBounds { get; set; } = false;

    // Taxi rides show their resolved vehicle by default, but users can hide it from the Taxi routes submenu.
    public bool ShowTaxiVehicle { get; set; } = true;
    // Taxi droids/terminal NPCs are an interaction surface, not ordinary population. Keep them visible even when
    // the general NPC layer is disabled; the Taxi routes submenu exposes this independently and defaults it on.
    public bool ShowTaxiTerminals { get; set; } = true;

    // Spawned/client-only NPC preview. Models are kept separate from ordinary area geometry so they
    // can be switched off without changing the authored world. Names/items are independent overlays.
    public bool ShowNpcs { get; set; } = false;
    public bool ShowNpcNames { get; set; } = true;
    public bool ShowNpcItems { get; set; } = true;
    public bool AnimateNpcs { get; set; } = true;
    public bool ShowSpnObjects { get; set; } = true;
    public bool AnimateSpnObjects { get; set; } = true;
    public bool ShowPlaceableGlow { get; set; } = true;

    // Ground-following first-person movement. This deliberately reuses the floor indices that room
    // culling already builds, so stairs/interiors work without a second collision representation.
    public bool WalkingMode { get; set; } = false;

    // Jedipedia defaults its world viewer to 3x the authored SWTOR view/fog distance.
    // Keep the same default, but expose it in the toolbar so dense/indoor areas can still use 1x.
    public float ViewDistanceScale { get; set; } = 3f;

    // Jedipedia exposes FOV as a 50°..75° viewer preference and defaults to 50°.
    // Match that range so the desktop World Browser frames scenes like the web viewer.
    public float FieldOfViewDegrees { get; set; } = 50f;

    // Jedipedia's orthographic plan/cutaway view. The renderer couples this to a zoom-scaled
    // projection box and camera height rather than treating it as a static projection toggle.
    public bool OrthographicProjection { get; set; } = false;

    public WorldRenderSettings Clone() {
      return (WorldRenderSettings)MemberwiseClone();
    }
  }
}
