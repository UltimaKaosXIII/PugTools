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

  public sealed class WorldRenderSettings {
    public WorldRenderMode Mode { get; set; } = WorldRenderMode.InGame;
    public WorldRenderBackend Backend { get; set; } = WorldRenderBackend.Hardware;
    public WorldAntiAliasing AntiAliasing { get; set; } = WorldAntiAliasing.TAAFXAA;
    public bool ShowTerrain { get; set; } = true;
    public bool ShowModels { get; set; } = true;
    public bool ShowDecorationHooks { get; set; } = true;
    public bool ShowDynamicDetails { get; set; } = true;
    public bool EnableLighting { get; set; } = true;
    public bool EnableFog { get; set; } = true;
    public bool EnableShadows { get; set; } = true;
    public bool ShowRoads { get; set; } = true;
    public bool ShowMapArt { get; set; } = true;
    public bool ShowMapNotes { get; set; } = true;
    public bool EnablePostProcessing { get; set; } = true;
    public bool EnableLocalLights { get; set; } = true;
    public bool EnableRoomVisibility { get; set; } = false;
    public bool ShowSky { get; set; } = true;
    public bool ShowWater { get; set; } = true;

    // Jedipedia-style authoring/utility overlay. The toolbar exposes these as a dropdown of
    // independent checkboxes instead of forcing the editor helpers into the ordinary Models pass.
    public bool ShowUtilitySpawners { get; set; } = false;
    public bool ShowUtilityCoverPoints { get; set; } = false;
    public bool ShowUtilityLights { get; set; } = false;
    public bool ShowUtilitySeedPoints { get; set; } = false;
    public bool ShowUtilityPaths { get; set; } = false;
    public bool ShowUtilityMapRoadPaths { get; set; } = false;
    public bool ShowUtilityConnections { get; set; } = false;
    public bool ShowUtilityVolumes { get; set; } = false;
    public bool ShowPhaseGateways { get; set; } = true;
    public bool ShowUtilityOther { get; set; } = false;

    // Spawned/client-only NPC preview. Models are kept separate from ordinary area geometry so they
    // can be switched off without changing the authored world. Names/items are independent overlays.
    public bool ShowNpcs { get; set; } = false;
    public bool ShowNpcNames { get; set; } = true;
    public bool ShowNpcItems { get; set; } = true;
    public bool AnimateNpcs { get; set; } = true;
    public bool ShowSpnObjects { get; set; } = false;
    public bool AnimateSpnObjects { get; set; } = true;

    // Ground-following first-person movement. This deliberately reuses the floor indices that room
    // culling already builds, so stairs/interiors work without a second collision representation.
    public bool WalkingMode { get; set; } = false;

    // Jedipedia defaults its world viewer to 3x the authored SWTOR view/fog distance.
    // Keep the same default, but expose it in the toolbar so dense/indoor areas can still use 1x.
    public float ViewDistanceScale { get; set; } = 3f;

    public WorldRenderSettings Clone() {
      return (WorldRenderSettings)MemberwiseClone();
    }
  }
}
