using System.Collections.Generic;
using FileFormats;

namespace PugTools {
  internal enum WorldInteractionKind {
    None = 0,
    Wonkavator,
    Taxi,
    QuickTravel,
    Bank,
    GuildBank,
    Mailbox,
    Vendor,
    ProfessionTrainer,
    ClassTrainer,
    Harvest,
    MissionBoard,
    Conversation,
    Codex,
    AuctionHouse,
    EnhancementStation
  }

  internal sealed class WorldMissionBoardQuest {
    public ulong Id { get; set; }
    public string Fqn { get; set; }
    public string Name { get; set; }
  }

  internal sealed class WorldMissionBoardNotice {
    public long NodeId { get; set; }
    public string Text { get; set; }
    public readonly List<WorldMissionBoardQuest> Quests = new List<WorldMissionBoardQuest>();
  }

  internal sealed class WorldInteractionInfo {
    public WorldInteractionKind Kind { get; set; }
    // Raw/source identities are retained for inspection and for later richer service UIs. The renderer never has to
    // reopen the GOM node merely to explain what an already-resolved world object does.
    public string TaxiTerminalSpec { get; set; }
    public long WonkaPackageId { get; set; }
    public string MissionBoardPackage { get; set; }
    public string Conversation { get; set; }
    public ulong ConversationId { get; set; }
    public ulong CodexId { get; set; }
    public int UtilityType { get; set; }
    public string Profession { get; set; }
    public string[] VendorPackages { get; set; } = System.Array.Empty<string>();
    public readonly List<WorldMissionBoardNotice> MissionBoardNotices = new List<WorldMissionBoardNotice>();
    public bool MissionBoardLoaded { get; set; }
    public string MissionBoardError { get; set; }
    // True only when an old client does not expose the authoritative terminal field and the legacy name hint had to
    // be kept as a compatibility fallback. Live/current data should normally leave this false.
    public bool LegacyHeuristic { get; set; }
  }
  internal sealed class WorldSpawnPointPose {
    public SlimDX.Vector3 Position { get; set; }
    // Authored spawn-point rotation only. The editor gizmo scale is intentionally omitted; Jedipedia combines this
    // relative rotation with the spawner/entity base transform and uses the point's absolute position separately.
    public SlimDX.Matrix Rotation { get; set; } = SlimDX.Matrix.Identity;
  }


  internal sealed class WorldNpcClothAsset {
    // The GR2 whose skinned mesh consumes the CLO particle bones. Parsed data is shared by every placement wearing
    // the appearance part; simulation state remains placement-local in View_AREA.
    public GR2 Model { get; set; }
    public string SourcePath { get; set; }
    public ViewCLO.CloInfo Cloth { get; set; }
  }

  internal sealed class WorldNpcWeaponAttachment {
    public GR2 Model { get; set; }
    // Canonical rig bone name used by SWTOR/Jedipedia for held weapons.
    public string BoneName { get; set; }
    // staCombatMode: 1=None, 2=Melee, 3=Ranged, 4=Unarmed.
    public int WeaponMode { get; set; }
    public ulong ItemId { get; set; }
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
    public WorldInteractionInfo Interaction { get; set; }
    public string[] Items { get; set; } = System.Array.Empty<string>();
    public string RepublicReaction { get; set; }
    public string ImperialReaction { get; set; }
    public bool HasFactionPackage { get; set; }
    public string IdleAnimationName { get; set; }
    public float AnimationPhase { get; set; }
    public string BodyType { get; set; }
    public WorldNpcAnimationClip Animation { get; set; }
    // Temporary authored conversation animation. The world conversation player owns this override and clears it
    // when a line/player session ends; normal idle/locomotion animation remains untouched underneath.
    public WorldNpcAnimationClip ConversationAnimation { get; set; }
    // FaceFX is a placement-local overlay on top of the Morpheme/JBA pose. It is intentionally not stored on the
    // shared animation clip: two actors can play the same body clip while only the speaking placement has a face.
    public WorldNpcFaceFxClip ConversationFaceFx { get; set; }
    public float ConversationFaceFxStart { get; set; }
    public float ConversationAnimationStart { get; set; }
    public float ConversationAnimationOffset { get; set; }
    public float ConversationAnimationSpeed { get; set; } = 1f;
    public bool ConversationAnimationLoop { get; set; }
    // Temporary staging overrides owned by the conversation player. They are restored when playback stops.
    public SlimDX.Matrix? ConversationWorld { get; set; }
    public bool ConversationHidden { get; set; }
    // Conversation previews synthesize the local player even though no AREA AssetInstance exists for them. The
    // renderer treats this placement like an ordinary animated NPC, but skips authored-instance/dPVS gates.
    public bool ConversationVirtual { get; set; }
    // Optional locomotion clips resolved from the body type's Morpheme/anim_library network. A routed NPC should not
    // slide through the world while its skeleton keeps playing the idle pose.
    public WorldNpcAnimationClip WalkAnimation { get; set; }
    public WorldNpcAnimationClip RunAnimation { get; set; }
    public bool ShowNameplate { get; set; }
    public float Scale { get; set; } = 1f;
    public SlimDX.Vector3? NameplateLocal { get; set; }
    // Exact literal NamePlate bone frame used by authored overhead FXSPEC effects. Unlike the text nameplate anchor,
    // this must preserve bone orientation: SWTOR marker specs commonly offset along local -Z, which the NamePlate
    // frame rotates into viewer-up. Keeping it separate prevents attach_nameplate text priorities from moving FX.
    public SlimDX.Matrix? OverheadIconLocalFrame { get; set; }
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
    // Weapon geometry is not part of the skinned appearance mesh. It is a rigid child of LeftWeapon/RightWeapon and
    // is gated by the placement's current combat mode, exactly like Jedipedia's merged npcWeapon meshes.
    public int CombatMode { get; set; } = 1;
    public readonly List<WorldNpcWeaponAttachment> Weapons = new List<WorldNpcWeaponAttachment>();
    // Shared parsed CLO assets. Each wearer gets its own Verlet state in the renderer.
    public readonly List<WorldNpcClothAsset> ClothAssets = new List<WorldNpcClothAsset>();
    public readonly List<GR2> Models = new List<GR2>();
  }

  internal sealed class WorldNpcFaceFxClip {
    public ViewFXA.FxaInfo Actor { get; set; }
    public ViewFXE.Animation Animation { get; set; }
    // Prepared once when the clip is assembled. Face graph evaluation is then only array math in the render loop.
    public readonly Dictionary<string, int> NodeByName = new Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase);
    public readonly Dictionary<string, ViewFXA.Bone> BoneByName = new Dictionary<string, ViewFXA.Bone>(System.StringComparer.OrdinalIgnoreCase);
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
    // Cached from the plc.* prototype. Plain-click interaction can therefore consider only real service objects
    // instead of letting an unrelated spawned prop's broad bounds swallow the click in front of a control panel.
    public long WonkaPackageId { get; set; }
    // The exact SWTOR quick-travel interaction is plcAbilitySpecOnUse == 16140902321107152398. Keep the resolved
    // classification per placement so picking and the destination map use the same decision in every client build.
    public bool IsQuickTravel { get; set; }
    public WorldInteractionInfo Interaction { get; set; }
    public bool BlueGlow { get; set; }
    public readonly List<WorldSpawnPointPose> SpawnPoints = new List<WorldSpawnPointPose>();
    // Models remains the union of all visual models so the renderer can build GPU buffers once. DynStates contains
    // the actual per-state part lists and local transforms used at draw time.
    public readonly List<GR2> Models = new List<GR2>();
    public readonly List<WorldSpnDynState> DynStates = new List<WorldSpnDynState>();
    public readonly List<WorldSpnDynLight> DynLights = new List<WorldSpnDynLight>();
  }
}
