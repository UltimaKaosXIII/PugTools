using System.Collections.Generic;
using FileFormats;

namespace PugTools {
  internal sealed class WorldNpcPlacement {
    public FileFormats.Room Room { get; set; }
    public FileFormats.AssetInstance Instance { get; set; }
    public string SourceFqn { get; set; }
    public string Name { get; set; }
    public string Title { get; set; }
    public string[] Items { get; set; } = System.Array.Empty<string>();
    // SWTOR faction-package reactions are used for the same green/yellow/red nameplate palette as Jedipedia.
    public string RepublicReaction { get; set; }
    public string ImperialReaction { get; set; }
    public bool HasFactionPackage { get; set; }
    public string IdleAnimationName { get; set; }
    public float AnimationPhase { get; set; }
    public string BodyType { get; set; }
    public WorldNpcAnimationClip Animation { get; set; }
    // Jedipedia nameplates only server-spawned npc.* entities. Client-only .cos scenery is rendered, but unlabeled.
    public bool ShowNameplate { get; set; }
    public float Scale { get; set; } = 1f;
    public readonly List<GR2> Models = new List<GR2>();
  }

  internal sealed class WorldNpcAnimationClip {
    public string DisplayName { get; set; }
    public string Action { get; set; }
    public string Network { get; set; }
    public FileFormats.JBAAnimation Animation { get; set; }
    public FileFormats.JBARig Rig { get; set; }
    // Character/animated placeable parts are often skinned to the external body-type skeleton,
    // not a skeleton embedded in every GR2 part. Jedipedia resolves that authored spec skeleton too.
    public IList<FileFormats.GR2_Bone_Skeleton> Skeleton { get; set; }
  }

  internal sealed class WorldSpnPlacement {
    public FileFormats.Room Room { get; set; }
    public FileFormats.AssetInstance Instance { get; set; }
    public string SourceFqn { get; set; }
    public string Name { get; set; }
    public float Scale { get; set; } = 1f;
    public float AnimationPhase { get; set; }
    public WorldNpcAnimationClip Animation { get; set; }
    public readonly List<GR2> Models = new List<GR2>();
  }
}
