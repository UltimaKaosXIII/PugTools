using System.Collections.Generic;
using FileFormats;

namespace PugTools {
  internal sealed class WorldSpawnPointPose {
    public SlimDX.Vector3 Position { get; set; }
    // Authored spawn-point rotation only. The editor gizmo scale is intentionally omitted; Jedipedia combines this
    // relative rotation with the spawner/entity base transform and uses the point's absolute position separately.
    public SlimDX.Matrix Rotation { get; set; } = SlimDX.Matrix.Identity;
  }

  internal sealed class WorldNpcPlacement {
    public FileFormats.Room Room { get; set; }
    public FileFormats.AssetInstance Instance { get; set; }
    public string SourceFqn { get; set; }
    public string Name { get; set; }
    public string Title { get; set; }
    // Cached once while the GOM placement is built. Taxi terminals remain an independent interaction layer even when
    // the general NPC population is hidden, without doing string classification/allocation every render frame.
    public bool IsTaxiTerminal { get; set; }
    public string[] Items { get; set; } = System.Array.Empty<string>();
    public string RepublicReaction { get; set; }
    public string ImperialReaction { get; set; }
    public bool HasFactionPackage { get; set; }
    public string IdleAnimationName { get; set; }
    public float AnimationPhase { get; set; }
    public string BodyType { get; set; }
    public WorldNpcAnimationClip Animation { get; set; }
    // Optional locomotion clips resolved from the body type's Morpheme/anim_library network. A routed NPC should not
    // slide through the world while its skeleton keeps playing the idle pose.
    public WorldNpcAnimationClip WalkAnimation { get; set; }
    public WorldNpcAnimationClip RunAnimation { get; set; }
    public bool ShowNameplate { get; set; }
    public float Scale { get; set; } = 1f;
    public SlimDX.Vector3? NameplateLocal { get; set; }
    // spnEntityList is a list of alternatives, not a group. All alternatives share one clock and only the selected
    // slot is drawn. Keeping this on the placement lets NPC and PLC alternatives use the same deterministic logic.
    public int VariantIndex { get; set; }
    public int VariantCount { get; set; } = 1;
    // pth.* motion is shared by every alternative of the spawner. Jedipedia applies it to whatever entity is
    // currently dispensed, including NPC alternatives, before the population/frustum gate.
    public string PathFqn { get; set; }
    public FileFormats.AreaPath Route { get; set; }
    public int TraversalStyle { get; set; } = 1;
    public readonly List<WorldSpawnPointPose> SpawnPoints = new List<WorldSpawnPointPose>();
    public readonly List<GR2> Models = new List<GR2>();
  }

  internal sealed class WorldNpcAnimationClip {
    public string DisplayName { get; set; }
    public string Action { get; set; }
    public string Network { get; set; }
    public FileFormats.JBAAnimation Animation { get; set; }
    public FileFormats.JBARig Rig { get; set; }
    public IList<FileFormats.GR2_Bone_Skeleton> Skeleton { get; set; }
  }

  internal sealed class WorldSpnDynPart {
    public GR2 Model { get; set; }
    public SlimDX.Matrix LocalMatrix { get; set; } = SlimDX.Matrix.Identity;
    // A .mag part can have its own animation network. This remains per part instead of per placement because a dyn
    // assembly can mix several independently placed visuals.
    public WorldNpcAnimationClip Animation { get; set; }
    // DYN rows own this decision per visual/state; do not fall back to the parent PLC glow when false.
    public bool BlueGlow { get; set; }
  }

  internal sealed class WorldSpnDynState {
    public string Name { get; set; }
    public bool Hidden { get; set; }
    public readonly List<WorldSpnDynPart> Parts = new List<WorldSpnDynPart>();
  }

  internal sealed class WorldSpnDynLight {
    public SlimDX.Matrix LocalMatrix { get; set; } = SlimDX.Matrix.Identity;
    public string LightType { get; set; } = "OMNI";
    public float SourceOffset { get; set; }
    public float Range { get; set; } = 1f;
    public float Intensity { get; set; } = 1f;
    public SlimDX.Vector4 Color { get; set; } = new SlimDX.Vector4(1f, 1f, 1f, 1f);
    public string IlluminationMap { get; set; }
    public string RampMap { get; set; }
    public string Falloff { get; set; }
    public bool DoHeightmaps { get; set; } = true;
    public bool DoGranny { get; set; } = true;
    public bool DoCharacters { get; set; } = true;
    public bool DoWater { get; set; } = true;
    public bool RestrictToRoom { get; set; }
    // State -> whether the row's dynState Visible bit is set. Empty means the light is always on.
    public readonly Dictionary<string, bool> StateVisibility = new Dictionary<string, bool>(System.StringComparer.OrdinalIgnoreCase);
  }

  internal sealed class WorldSpnPlacement {
    public FileFormats.Room Room { get; set; }
    public FileFormats.AssetInstance Instance { get; set; }
    public string SourceFqn { get; set; }
    public string Name { get; set; }
    public float Scale { get; set; } = 1f;
    public float AnimationPhase { get; set; }
    public WorldNpcAnimationClip Animation { get; set; }
    public string PathFqn { get; set; }
    public FileFormats.AreaPath Route { get; set; }
    public int TraversalStyle { get; set; } = 1;
    public int VariantIndex { get; set; }
    public int VariantCount { get; set; } = 1;
    public string DynStartState { get; set; }
    public bool BlueGlow { get; set; }
    public readonly List<WorldSpawnPointPose> SpawnPoints = new List<WorldSpawnPointPose>();
    // Models remains the union of all visual models so the renderer can build GPU buffers once. DynStates contains
    // the actual per-state part lists and local transforms used at draw time.
    public readonly List<GR2> Models = new List<GR2>();
    public readonly List<WorldSpnDynState> DynStates = new List<WorldSpnDynState>();
    public readonly List<WorldSpnDynLight> DynLights = new List<WorldSpnDynLight>();
  }
}
