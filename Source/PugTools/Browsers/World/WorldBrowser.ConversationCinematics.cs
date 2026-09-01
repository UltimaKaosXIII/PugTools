using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using FileFormats;
using GomLib;
using GomLib.Models;
using SlimDX;
using Room = FileFormats.Room;
using AssetInstance = FileFormats.AssetInstance;

namespace PugTools {
  public partial class WorldBrowser {
    private const ulong WorldConversationRoleSpeaker = 16513498648939006850UL;
    private const ulong WorldConversationRoleListener = 15937209066439308881UL;
    private const ulong WorldConversationRoleSpeakerCc = 10143671182081221108UL;
    private const ulong WorldConversationRoleListenerCc = 3798166423556355225UL;
    private const ulong WorldConversationRolePlayer1 = 13999789947792456739UL;
    private const ulong WorldConversationRolePlayer2 = 13999791047304084950UL;
    private const ulong WorldConversationRolePlayer3 = 13999792146815713161UL;
    private const ulong WorldConversationRolePlayer4 = 13999793246327341372UL;
    private const ulong WorldConversationRoleVoteWinner = 16658114557975260616UL;
    private const ulong WorldConversationRoleVoteLoser1 = 3024371963656054499UL;
    private const ulong WorldConversationRoleVoteLoser2 = 3024373063167682710UL;
    private const ulong WorldConversationRoleVoteLoser3 = 3024374162679310921UL;

    private sealed class WorldConversationStageMarkRef {
      public string Stage;
      public string Mark;
    }

    private sealed class WorldConversationStagingInfo {
      public ulong Group;
      public WorldConversationStageMarkRef Player;
      public readonly Dictionary<ulong, WorldConversationStageMarkRef> Speakers = new Dictionary<ulong, WorldConversationStageMarkRef>();
    }

    private sealed class WorldConversationStageContext {
      public string Fqn;
      public GomObjectData Data;
      public Matrix World;
      public float Yaw;
    }

    private sealed class WorldConversationCameraPose {
      public Vector3 Position;
      public Vector3 Look;
      public Vector3 Up = Vector3.UnitY;
      public float? Fov;
    }

    private object worldConversationCinematicCacheDom;
    private readonly Dictionary<string, GomObjectData> worldConversationStageCache = new Dictionary<string, GomObjectData>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, GomObjectData> worldConversationAutoCameraCache = new Dictionary<string, GomObjectData>(StringComparer.OrdinalIgnoreCase);

    // File-backed conversation diagnostics. Visual Studio's Debug output can become very expensive while the world
    // renderer is running, so the Yalt/Morpheme diagnostics deliberately bypass Debug.WriteLine.
    private static readonly object worldConversationDiagnosticFileLock = new object();
    private static string worldConversationDiagnosticFilePath;
    private static bool worldConversationDiagnosticFileInitialized;

    private static void WorldConversationDiagnosticFileWrite(string line) {
      if (String.IsNullOrWhiteSpace(line)) return;
      lock (worldConversationDiagnosticFileLock) {
        try {
          if (String.IsNullOrWhiteSpace(worldConversationDiagnosticFilePath)) {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!String.IsNullOrWhiteSpace(local)) {
              string dir = Path.Combine(local, "PugTools");
              Directory.CreateDirectory(dir);
              worldConversationDiagnosticFilePath = Path.Combine(dir, "PugTools-Yalt-Debug.log");
            } else {
              worldConversationDiagnosticFilePath = Path.Combine(Path.GetTempPath(), "PugTools-Yalt-Debug.log");
            }
          }
          if (!worldConversationDiagnosticFileInitialized) {
            File.WriteAllText(worldConversationDiagnosticFilePath,
              "PugTools conversation diagnostics " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + Environment.NewLine);
            worldConversationDiagnosticFileInitialized = true;
          }
          File.AppendAllText(worldConversationDiagnosticFilePath, line + Environment.NewLine);
        } catch {
          try {
            string fallback = Path.Combine(Path.GetTempPath(), "PugTools-Yalt-Debug.log");
            if (!String.Equals(worldConversationDiagnosticFilePath, fallback, StringComparison.OrdinalIgnoreCase)) {
              worldConversationDiagnosticFilePath = fallback;
              if (!worldConversationDiagnosticFileInitialized) {
                File.WriteAllText(fallback,
                  "PugTools conversation diagnostics " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + Environment.NewLine);
                worldConversationDiagnosticFileInitialized = true;
              }
              File.AppendAllText(fallback, line + Environment.NewLine);
            }
          } catch { }
        }
      }
    }

    private void ApplyWorldConversationStaging(Conversation conversation, DialogNode node) {
      if (conversation == null || node == null || panelRender == null) return;
      ClearWorldConversationStaging();
      EnsureWorldConversationAnimationCache();

      // Jedipedia puts the active actor into the conversation stance as soon as the preview takes ownership of it,
      // not only when the node happens to carry a cnvStagePresetMarksNPCs row. Many ordinary dialog nodes have no
      // staging preset at all; the old gate therefore left their speaker in the room-authored static/idle pose and
      // made an otherwise valid cinematic look completely unanimated. Explicit actions below still win afterwards.
      WorldNpcPlacement player = EnsureWorldConversationPlayerPlacement();
      ApplyWorldConversationDefaultStance(player);
      WorldNpcPlacement activeSpeaker = FindWorldConversationSpeakerPlacement(conversation, node);
      ApplyWorldConversationDefaultStance(activeSpeaker);

      WorldConversationStagingInfo staging = ReadWorldConversationStaging(conversation, node.NodeId);
      if (staging == null) return;
      if (player != null && staging.Player != null && !String.IsNullOrWhiteSpace(staging.Player.Stage) && !String.IsNullOrWhiteSpace(staging.Player.Mark) &&
          TryResolveWorldConversationActorMark(staging.Player.Stage, staging.Player.Mark, out Vector3 playerPosition, out float playerYaw)) {
        panelRender.SetWorldConversationActorMark(player, playerPosition, playerYaw);
        worldConversationStagedNpcs.Add(player);
      }
      foreach (KeyValuePair<ulong, WorldConversationStageMarkRef> pair in staging.Speakers) {
        WorldConversationStageMarkRef markRef = pair.Value;
        WorldNpcPlacement placement = FindWorldConversationSpeakerPlacement(pair.Key);
        if (placement == null) continue;

        ApplyWorldConversationDefaultStance(placement);
        if (markRef == null || String.IsNullOrWhiteSpace(markRef.Stage) || String.IsNullOrWhiteSpace(markRef.Mark)) continue;
        if (!TryResolveWorldConversationActorMark(markRef.Stage, markRef.Mark, out Vector3 position, out float yaw)) continue;
        panelRender.SetWorldConversationActorMark(placement, position, yaw);
        worldConversationStagedNpcs.Add(placement);
      }
    }

    private void EnsureWorldConversationAnimationCache() {
      if (ReferenceEquals(worldConversationAnimationCacheAssets, currentAssets)) return;
      worldConversationAnimationCache.Clear();
      worldConversationAnimationCacheAssets = currentAssets;
    }

    private void ApplyWorldConversationDefaultStance(WorldNpcPlacement placement) {
      if (placement == null || panelRender == null || worldConversationAnimatedNpcs.Contains(placement)) return;
      WorldNpcAnimationClip stance = ResolveNpcConversationPostureClip("Normal", placement.BodyType, worldConversationAnimationCache);
      if (stance == null) return;
      panelRender.SetWorldConversationAnimation(placement, stance, true);
      worldConversationAnimatedNpcs.Add(placement);
    }

    private void ApplyWorldConversationCinematicAction(Conversation conversation, DialogNode node, WorldConversationCinematicAction action) {
      if (conversation == null || node == null || action == null || action.Ignored || panelRender == null) return;
      string kind = (action.Type ?? String.Empty).Trim();
      if (kind.Length == 0) return;

      if (String.Equals(kind, "Set Camera", StringComparison.OrdinalIgnoreCase) ||
          String.Equals(kind, "Move Camera To Mark", StringComparison.OrdinalIgnoreCase)) {
        if (!TryResolveWorldConversationCameraPose(conversation, node, action.Value, out WorldConversationCameraPose pose)) return;
        Dictionary<string, string> parameters = WorldConversationActionParameters(action.Params);
        ApplyWorldConversationCameraOverrides(pose, parameters);
        bool moving = String.Equals(kind, "Move Camera To Mark", StringComparison.OrdinalIgnoreCase);
        float moveSeconds = moving ? WorldConversationParameterSeconds(parameters, "cameramoveduration") : 0f;
        float rotateSeconds = moving ? WorldConversationParameterSeconds(parameters, "camerarotateduration") : 0f;
        float fovSeconds = moving ? WorldConversationParameterSeconds(parameters, "camerafovduration") : 0f;
        string moveType = moving ? WorldConversationParameterText(parameters, "movetype") : null;
        string rotateType = moving ? WorldConversationParameterText(parameters, "rotatetype") : null;
        string fovType = moving ? WorldConversationParameterText(parameters, "fovtype") : null;
        panelRender.SetWorldConversationCamera(pose.Position, pose.Look, pose.Up, pose.Fov,
          moveSeconds, rotateSeconds, fovSeconds, moveType, rotateType, fovType);
        return;
      }

      WorldNpcPlacement placement = FindWorldConversationActionPlacement(conversation, node, action);
      if (String.Equals(kind, "Set Mark", StringComparison.OrdinalIgnoreCase)) {
        if (placement == null) return;
        string text = (action.Value ?? String.Empty).Trim();
        int split = text.LastIndexOf(':');
        if (split <= 0 || split + 1 >= text.Length) return;
        if (!TryResolveWorldConversationActorMark(text.Substring(0, split), text.Substring(split + 1), out Vector3 position, out float yaw)) return;
        panelRender.SetWorldConversationActorMark(placement, position, yaw);
        worldConversationStagedNpcs.Add(placement);
        return;
      }

      if (String.Equals(kind, "Hide Actor", StringComparison.OrdinalIgnoreCase)) {
        if (placement == null) return;
        bool hidden = !String.Equals((action.Value ?? String.Empty).Trim(), "false", StringComparison.OrdinalIgnoreCase);
        panelRender.SetWorldConversationActorHidden(placement, hidden);
        worldConversationStagedNpcs.Add(placement);
        return;
      }

      bool posture = String.Equals(kind, "Set Posture", StringComparison.OrdinalIgnoreCase) || String.Equals(kind, "Snap To Posture", StringComparison.OrdinalIgnoreCase);
      bool animation = posture || String.Equals(kind, "Play Animation", StringComparison.OrdinalIgnoreCase) || String.Equals(kind, "Queue Animation", StringComparison.OrdinalIgnoreCase);
      if (!animation || placement == null || String.IsNullOrWhiteSpace(action.Value)) return;
      EnsureWorldConversationAnimationCache();
      WorldNpcAnimationClip clip = posture
        ? ResolveNpcConversationPostureClip(action.Value.Trim(), placement.BodyType, worldConversationAnimationCache)
        : ResolveNpcConversationAnimationClip(action.Value.Trim(), placement.BodyType, worldConversationAnimationCache);
      if (clip == null) {
        WorldConversationDiagnosticFileWrite("CNV body clip unresolved: " + kind + " '" + action.Value + "' body=" +
          (placement.BodyType ?? "<none>") + " actor=" + (placement.SourceFqn ?? "<virtual>"));
        return;
      }
      WorldConversationDiagnosticFileWrite("CNV body clip: " + kind + " '" + action.Value + "' -> " + clip.Action +
        " body=" + (placement.BodyType ?? "<none>"));
      DebugWorldConversationClipDiagnostics(placement, clip);
      Dictionary<string, string> animationParameters = WorldConversationActionParameters(action.Params);
      float startAt = WorldConversationParameterFloat(animationParameters, "start", out float authoredStart) ? Math.Max(0f, authoredStart) : 0f;
      float speed = WorldConversationParameterFloat(animationParameters, "speed", out float authoredSpeed) && authoredSpeed > 0f ? authoredSpeed : 1f;
      panelRender.SetWorldConversationAnimation(placement, clip, posture, startAt, speed);
      worldConversationAnimatedNpcs.Add(placement);
    }

    private static void DebugWorldConversationClipDiagnostics(WorldNpcPlacement placement, WorldNpcAnimationClip clip) {
      try {
        JBAAnimation animation = clip?.Animation;
        if (animation == null || animation.BoneCount <= 0) return;
        int count = Math.Max(0, animation.BoneCount);
        var samples = new JBATransform[count];
        float sampleTime = animation.Length > .02f ? animation.Length * .5f : 0f;
        animation.SampleInto(sampleTime, samples);
        var rows = new List<Tuple<float, string>>();
        for (int channel = 0; channel < count; channel++) {
          string boneName = null;
          int rigIndex = -1;
          if (animation.BoneNames != null && channel < animation.BoneNames.Count &&
              !String.IsNullOrWhiteSpace(animation.BoneNames[channel]) &&
              !animation.BoneNames[channel].StartsWith("bone_", StringComparison.OrdinalIgnoreCase)) {
            boneName = animation.BoneNames[channel];
          } else if (clip.Rig?.AnimToRig != null && channel < clip.Rig.AnimToRig.Length) {
            rigIndex = clip.Rig.AnimToRig[channel];
            if (rigIndex >= 0 && rigIndex < clip.Rig.Bones.Count) boneName = clip.Rig.Bones[rigIndex].Name;
          }
          if (String.IsNullOrWhiteSpace(boneName)) boneName = "channel#" + channel;
          var t = samples[channel].Translation;
          float length = t.Length();
          string bind = String.Empty;
          if (rigIndex >= 0 && clip.Rig?.Bones != null && rigIndex < clip.Rig.Bones.Count) {
            var b = clip.Rig.Bones[rigIndex].BindTranslation;
            bind = String.Format(CultureInfo.InvariantCulture, " bind=({0:0.###},{1:0.###},{2:0.###})", b.X, b.Y, b.Z);
          }
          string row = String.Format(CultureInfo.InvariantCulture,
            "CNV DIAG ch={0} bone={1} t=({2:0.###},{3:0.###},{4:0.###}) |t|={5:0.###} hasT={6} bindT={7}{8}",
            channel, boneName, t.X, t.Y, t.Z, length, samples[channel].HasTranslation,
            animation.UsesRigBindTranslation(channel), bind);
          rows.Add(Tuple.Create(length, row));
        }
        var block = new List<string> {
          String.Format(CultureInfo.InvariantCulture,
            "CNV DIAG clip={0} network={1} body={2} actor={3} len={4:0.###} fps={5:0.###} channels={6} rig={7} skeleton={8}",
            clip.Action ?? "<none>", clip.Network ?? "<none>", placement?.BodyType ?? "<none>",
            placement?.SourceFqn ?? "<virtual>", animation.Length, animation.FPS, animation.BoneCount,
            clip.Rig?.Source ?? (clip.Rig == null ? "<named-jba>" : "<rig>"), clip.Skeleton?.Count ?? 0)
        };
        foreach (Tuple<float, string> row in rows.OrderByDescending(x => x.Item1).Take(12)) block.Add(row.Item2);
        block.Add(String.Empty.PadRight(72, '-'));
        WorldConversationDiagnosticFileWrite(String.Join(Environment.NewLine, block));
      } catch (Exception ex) {
        WorldConversationDiagnosticFileWrite("CNV DIAG failed: " + ex.Message);
      }
    }

    private WorldNpcPlacement FindWorldConversationActionPlacement(Conversation conversation, DialogNode node, WorldConversationCinematicAction action) {
      if (action == null) return null;
      if (!String.IsNullOrWhiteSpace(action.Actor)) {
        string symbolic = action.Actor.Trim().ToLowerInvariant().Replace("_", " ").Replace("-", " ");
        if (symbolic == "speaker" || symbolic == "speaker cc") return FindWorldConversationSpeakerPlacement(conversation, node);
        if (symbolic == "listener" || symbolic == "listener cc")
          return node != null && node.IsPlayerNode ? FindWorldConversationNpcSpeakerPlacement(conversation, node) : EnsureWorldConversationPlayerPlacement();
        if (WorldConversationPlayerActorName(action.Actor)) return EnsureWorldConversationPlayerPlacement();
        WorldNpcPlacement participant = FindWorldConversationActorPlacement(action.Actor);
        if (participant != null) return participant;
      }

      if (action.ActorId == WorldConversationRoleSpeaker || action.ActorId == WorldConversationRoleSpeakerCc)
        return FindWorldConversationSpeakerPlacement(conversation, node);
      if (action.ActorId == WorldConversationRoleListener || action.ActorId == WorldConversationRoleListenerCc)
        return node != null && node.IsPlayerNode ? FindWorldConversationNpcSpeakerPlacement(conversation, node) : EnsureWorldConversationPlayerPlacement();
      if (WorldConversationPlayerRole(action.ActorId)) return EnsureWorldConversationPlayerPlacement();

      // A fair number of old cinematics omit hydObject on speaker-local animation/posture actions. The game resolves
      // those against the active speaker, so do the same instead of silently dropping the beat.
      return action.ActorId == 0 ? FindWorldConversationSpeakerPlacement(conversation, node) : null;
    }

    private static bool WorldConversationPlayerRole(ulong role) =>
      role == WorldConversationRolePlayer1 || role == WorldConversationRolePlayer2 || role == WorldConversationRolePlayer3 || role == WorldConversationRolePlayer4 ||
      role == WorldConversationRoleVoteWinner || role == WorldConversationRoleVoteLoser1 || role == WorldConversationRoleVoteLoser2 || role == WorldConversationRoleVoteLoser3;

    private static bool WorldConversationPlayerActorName(string actor) {
      string text = (actor ?? String.Empty).Trim().ToLowerInvariant().Replace("_", " ").Replace("-", " ");
      return text == "player" || text == "player 1" || text == "player 2" || text == "player 3" || text == "player 4" ||
        text == "vote winner" || text == "vote loser" || text == "vote loser 1" || text == "vote loser 2" || text == "vote loser 3";
    }

    private Matrix WorldConversationPlayerFallbackWorld() {
      Matrix world = Matrix.Identity;
      if (worldConversationPrimaryNpc?.Instance == null || worldConversationPrimaryNpc.Room == null) return world;
      try {
        world = worldConversationPrimaryNpc.Instance.GetAbsoluteTransform(worldConversationPrimaryNpc.Room);
        // Only a fallback until cnvStagePresetMarksPCs places the player. Keep the stand-in near the NPC rather than
        // exactly inside it for conversations without a PC staging preset.
        Vector3 forward = new Vector3(world.M31, 0f, world.M33);
        if (forward.LengthSquared() < .000001f) forward = Vector3.UnitZ; else forward.Normalize();
        world.M41 += forward.X * 1.5f; world.M43 += forward.Z * 1.5f;
        world = Matrix.RotationY((float)Math.PI) * world;
      } catch { world = Matrix.Identity; }
      return world;
    }

    private WorldNpcPlacement EnsureWorldConversationPlayerPlacement() {
      if (worldConversationPlayerNpc != null) {
        if (!worldConversationPlayerNpc.ConversationWorld.HasValue) worldConversationPlayerNpc.ConversationWorld = WorldConversationPlayerFallbackWorld();
        panelRender?.SetWorldConversationVirtualPlayer(worldConversationPlayerNpc);
        return worldConversationPlayerNpc;
      }
      if (currentDom == null || currentAssets == null || panelRender == null) return null;
      try {
        NpcAppearance appearance = null;
        try { appearance = currentDom.AppearanceLoader.Load("npp.npc.staging.pc.bmn") as NpcAppearance; } catch { }
        if (appearance == null) try { appearance = currentDom.AppearanceLoader.Load("npp.npc.default.bmn") as NpcAppearance; } catch { }
        if (appearance == null) return null;
        string bodyType = !String.IsNullOrWhiteSpace(appearance.BodyType) ? appearance.BodyType : "bmn";
        var appearanceCache = new Dictionary<string, List<GR2>>(StringComparer.OrdinalIgnoreCase);
        List<GR2> playerModels = GetNpcAppearanceModels(appearance, appearanceCache, bodyType);
        if (playerModels == null || playerModels.Count == 0) return null;

        Room room = worldConversationPrimaryNpc?.Room ?? worldNpcPlacements?.FirstOrDefault(x => x?.Room != null)?.Room ?? area?.RoomList?.FirstOrDefault(x => x != null);
        if (room == null) return null;
        Matrix world = WorldConversationPlayerFallbackWorld();

        var placement = new WorldNpcPlacement {
          Room = room, Instance = null, SourceFqn = "npp.npc.staging.pc.bmn", Name = "Player", BodyType = bodyType,
          ShowNameplate = false, Scale = 1f, ConversationVirtual = true, ConversationWorld = world,
          Animation = ResolveNpcAnimationClip(null, bodyType, worldConversationAnimationCache)
        };
        foreach (GR2 model in playerModels) placement.Models.Add(model);
        AddNpcClothAssets(placement);
        worldConversationPlayerNpc = placement;
        panelRender.SetWorldConversationVirtualPlayer(placement);
        return placement;
      } catch (Exception ex) {
        System.Diagnostics.Debug.WriteLine("Conversation player stand-in failed: " + ex.Message);
        return null;
      }
    }

    private WorldNpcPlacement FindWorldConversationSpeakerPlacement(ulong speakerId) {
      if (speakerId == 0) return null;
      string fqn = null;
      try { fqn = currentDom?.GetObject(speakerId)?.Name; } catch { }
      if (String.IsNullOrWhiteSpace(fqn)) {
        try { fqn = currentDom?.ConversationLoader.LoadSpeaker(speakerId)?.Fqn; } catch { }
      }
      return FindWorldConversationActorPlacement(fqn);
    }

    private WorldConversationStagingInfo ReadWorldConversationStaging(Conversation conversation, long nodeId) {
      if (!TryGetWorldConversationRawData(conversation, nodeId, out GomObjectData root, out GomObjectData nodeData)) return null;
      ulong group = WorldInteractionUnsigned(WorldInteractionDataValue(nodeData, "cnvAssignmentPresetName", "4611686068758931198"));
      if (group == 0) return null;

      var result = new WorldConversationStagingInfo { Group = group };
      object playerGroups = WorldInteractionDataValue(root, "cnvStagePresetMarksPCs", "4611686068758931191");
      object playerGroup = WorldConversationMapValueUnsigned(playerGroups, group);
      GomObjectData playerOuter = WorldConversationFirstObjectData(playerGroup, true);
      result.Player = ReadWorldConversationStageMarkRef(playerOuter);

      object speakerGroups = WorldInteractionDataValue(root, "cnvStagePresetMarksNPCs", "4611686068758931193");
      object speakerGroup = WorldConversationMapValueUnsigned(speakerGroups, group);
      foreach (KeyValuePair<object, object> pair in WorldConversationMapEntries(speakerGroup)) {
        ulong speaker = WorldInteractionUnsigned(pair.Key);
        GomObjectData row = WorldConversationFirstObjectData(pair.Value, false);
        WorldConversationStageMarkRef mark = ReadWorldConversationStageMarkRef(row);
        if (speaker != 0 && mark != null) result.Speakers[speaker] = mark;
      }
      return result;
    }

    private WorldConversationStageMarkRef ReadWorldConversationStageMarkRef(GomObjectData row) {
      if (row == null) return null;
      object rawMark = WorldInteractionDataValue(row, "cnvMark", "4611686068758931197");
      GomObjectData mark = WorldConversationFirstObjectData(rawMark, false) ?? rawMark as GomObjectData;
      if (mark == null) return null;
      string stage = WorldInteractionText(WorldInteractionDataValue(mark, "cnvMarkTemplate", "4611686068758931195"));
      string name = WorldInteractionText(WorldInteractionDataValue(mark, "cnvMarkName", "4611686068758931196"));
      return !String.IsNullOrWhiteSpace(stage) && !String.IsNullOrWhiteSpace(name) ? new WorldConversationStageMarkRef { Stage = stage, Mark = name } : null;
    }

    private bool TryResolveWorldConversationActorMark(string stageFqn, string markName, out Vector3 position, out float yaw) {
      position = Vector3.Zero; yaw = 0f;
      if (!TryGetWorldConversationStageContext(stageFqn, out WorldConversationStageContext stage)) return false;
      GomObjectData mark = WorldConversationNamedObject(WorldInteractionDataValue(stage.Data, "stgTemplateActorMarkList_ForPrototype", "4611686042788570002"), markName);
      if (mark == null) return false;
      Vector3 local = SpnDynVector3(WorldInteractionDataValue(mark, "stgMarkPosition", "4611686024438910010"), Vector3.Zero);
      Vector3 rotation = SpnDynVector3(WorldInteractionDataValue(mark, "stgMarkRotation", "4611686024438910011"), Vector3.Zero);
      position = Vector3.TransformCoordinate(local, stage.World);
      yaw = stage.Yaw + DegreesToRadians(rotation.Y);
      return IsFiniteVector(position);
    }

    private bool TryResolveWorldConversationCameraPose(Conversation conversation, DialogNode node, string value, out WorldConversationCameraPose pose) {
      pose = null;
      string text = (value ?? String.Empty).Trim();
      if (text.Length == 0) return false;
      int split = text.LastIndexOf(':');
      if (split > 0 && text.Substring(0, split).IndexOf('.') >= 0) {
        string stageFqn = text.Substring(0, split);
        string markName = text.Substring(split + 1);
        if (!TryGetWorldConversationStageContext(stageFqn, out WorldConversationStageContext stage)) return false;
        GomObjectData camera = WorldConversationNamedObject(WorldInteractionDataValue(stage.Data, "stgTemplateCameraMarkList_ForPrototype", "4611686042788570001"), markName);
        if (camera != null) { pose = WorldConversationPoseFromCameraMark(stage, camera, 0f); return pose != null; }
        GomObjectData auto = WorldConversationNamedObject(WorldInteractionDataValue(stage.Data, "stgTemplateAutoCameraList_ForPrototype", "4611686042788570003"), markName);
        if (auto != null) { pose = WorldConversationPoseFromAutoCamera(stage, auto); return pose != null; }
        return false;
      }
      return TryResolveWorldConversationAutoShot(conversation, node, text, out pose);
    }

    private bool TryResolveWorldConversationAutoShot(Conversation conversation, DialogNode node, string value, out WorldConversationCameraPose pose) {
      pose = null;
      WorldConversationStagingInfo staging = ReadWorldConversationStaging(conversation, node?.NodeId ?? 0);
      if (staging == null) return false;
      string shotText = (value ?? String.Empty).Trim();
      bool reaction = shotText.StartsWith("reaction.", StringComparison.OrdinalIgnoreCase);
      string shot = shotText;
      if (shot.StartsWith("auto.", StringComparison.OrdinalIgnoreCase)) shot = shot.Substring(5);
      else if (reaction) shot = shot.Substring(9);
      shot = shot.Trim().ToUpperInvariant();
      if (shot.Length == 0) return false;

      WorldConversationStageMarkRef speaker = null;
      ulong speakerId = node != null && node.SpeakerId != 0 ? node.SpeakerId : conversation?.DefaultSpeakerId ?? 0;
      if (speakerId != 0) staging.Speakers.TryGetValue(speakerId, out speaker);
      if (speaker == null && staging.Speakers.Count == 1) speaker = staging.Speakers.Values.First();
      WorldConversationStageMarkRef player = staging.Player;
      WorldConversationStageMarkRef subject = node != null && node.IsPlayerNode ? player : speaker;
      WorldConversationStageMarkRef other = node != null && node.IsPlayerNode ? speaker : player;
      if (reaction) { WorldConversationStageMarkRef swap = subject; subject = other; other = swap; }
      string stageFqn = subject?.Stage ?? player?.Stage ?? speaker?.Stage ?? staging.Speakers.Values.Select(x => x?.Stage).FirstOrDefault(x => !String.IsNullOrWhiteSpace(x));
      if (String.IsNullOrWhiteSpace(stageFqn) || !TryGetWorldConversationStageContext(stageFqn, out WorldConversationStageContext stage)) return false;

      string subjectMark = subject?.Mark ?? String.Empty;
      string otherMark = other?.Mark ?? String.Empty;
      string suffix = "." + shot.ToLowerInvariant();
      GomObjectData any = null, exact = null;
      object autoTable = WorldInteractionDataValue(stage.Data, "stgTemplateAutoCameraList_ForPrototype", "4611686042788570003");
      foreach (KeyValuePair<object, object> pair in WorldConversationMapEntries(autoTable)) {
        GomObjectData entry = WorldConversationFirstObjectData(pair.Value, false);
        if (entry == null) continue;
        string spec = WorldInteractionText(WorldInteractionDataValue(entry, "stgAutoCameraSpec", "4611686025877970033")) ?? String.Empty;
        if (!spec.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
        any ??= entry;
        string to = WorldInteractionText(WorldInteractionDataValue(entry, "stgAutoCameraToMark", "4611686025877970031")) ?? String.Empty;
        string from = WorldInteractionText(WorldInteractionDataValue(entry, "stgAutoCameraFromMark", "4611686025877970030")) ?? String.Empty;
        if (String.Equals(to, subjectMark, StringComparison.OrdinalIgnoreCase) && String.Equals(from, otherMark, StringComparison.OrdinalIgnoreCase)) { exact = entry; break; }
      }

      GomObjectData selected = exact;
      if (selected == null && !String.IsNullOrWhiteSpace(subjectMark) && !String.IsNullOrWhiteSpace(otherMark)) {
        string generated = WorldConversationAutoCameraTemplate(stage, shot, subjectMark, otherMark);
        if (!String.IsNullOrWhiteSpace(generated)) {
          selected = new GomObjectData();
          selected.Dictionary["4611686025877970030"] = otherMark;
          selected.Dictionary["4611686025877970031"] = subjectMark;
          selected.Dictionary["4611686025877970033"] = generated;
        }
      }
      selected ??= any;
      if (selected == null) return false;
      pose = WorldConversationPoseFromAutoCamera(stage, selected);
      return pose != null;
    }

    private string WorldConversationAutoCameraTemplate(WorldConversationStageContext stage, string shot, string subjectMark, string otherMark) {
      if (String.Equals(shot, "CLOSE_UP", StringComparison.OrdinalIgnoreCase)) return "cam.auto.close_up.close_up";
      if (String.Equals(shot, "MEDIUM", StringComparison.OrdinalIgnoreCase)) return "cam.auto.medium.medium";
      GomObjectData subject = WorldConversationNamedObject(WorldInteractionDataValue(stage.Data, "stgTemplateActorMarkList_ForPrototype", "4611686042788570002"), subjectMark);
      GomObjectData other = WorldConversationNamedObject(WorldInteractionDataValue(stage.Data, "stgTemplateActorMarkList_ForPrototype", "4611686042788570002"), otherMark);
      if (subject == null || other == null) return null;
      Vector3 here = SpnDynVector3(WorldInteractionDataValue(subject, "stgMarkPosition", "4611686024438910010"), Vector3.Zero);
      Vector3 there = SpnDynVector3(WorldInteractionDataValue(other, "stgMarkPosition", "4611686024438910010"), Vector3.Zero);
      float metres = (here - there).Length() * 10f;
      float[] steps = { 1f, 1.5f, 2f, 2.5f, 3f, 3.5f, 4f, 4.5f, 5f, 6f, 7f, 8f, 10f, 12f };
      float step = steps.OrderBy(x => Math.Abs(x - metres)).First();
      int whole = (int)Math.Floor(step);
      int tenth = (int)Math.Round((step - whole) * 10f);
      return "cam.auto." + whole.ToString(CultureInfo.InvariantCulture) + "-" + tenth.ToString(CultureInfo.InvariantCulture) + "." + shot.ToLowerInvariant();
    }

    private WorldConversationCameraPose WorldConversationPoseFromAutoCamera(WorldConversationStageContext stage, GomObjectData entry) {
      string fromName = WorldInteractionText(WorldInteractionDataValue(entry, "stgAutoCameraFromMark", "4611686025877970030"));
      string toName = WorldInteractionText(WorldInteractionDataValue(entry, "stgAutoCameraToMark", "4611686025877970031"));
      string specFqn = WorldInteractionText(WorldInteractionDataValue(entry, "stgAutoCameraSpec", "4611686025877970033"));
      if (String.IsNullOrWhiteSpace(fromName) || String.IsNullOrWhiteSpace(toName) || String.IsNullOrWhiteSpace(specFqn)) return null;
      GomObjectData actorTable = WorldInteractionDataValue(stage.Data, "stgTemplateActorMarkList_ForPrototype", "4611686042788570002") as GomObjectData;
      object rawActorTable = actorTable ?? WorldInteractionDataValue(stage.Data, "stgTemplateActorMarkList_ForPrototype", "4611686042788570002");
      GomObjectData from = WorldConversationNamedObject(rawActorTable, fromName);
      GomObjectData to = WorldConversationNamedObject(rawActorTable, toName);
      GomObjectData spec = LoadWorldConversationAutoCamera(specFqn);
      if (from == null || to == null || spec == null) return null;

      Vector3 here = SpnDynVector3(WorldInteractionDataValue(to, "stgMarkPosition", "4611686024438910010"), Vector3.Zero);
      Vector3 away = SpnDynVector3(WorldInteractionDataValue(from, "stgMarkPosition", "4611686024438910010"), Vector3.Zero);
      float frameYaw = (float)Math.Atan2(-(away.Z - here.Z), away.X - here.X);
      Vector3 specPosition = SpnDynVector3(WorldInteractionDataValue(spec, "stgMarkPosition", "4611686024438910010"), Vector3.Zero);
      float cos = (float)Math.Cos(-frameYaw), sin = (float)Math.Sin(-frameYaw);
      Vector3 offset = new Vector3(specPosition.X * cos + specPosition.Z * sin, specPosition.Y, -specPosition.X * sin + specPosition.Z * cos);

      WorldConversationCameraPose pose = WorldConversationPoseFromCameraMark(stage, spec, -frameYaw);
      if (pose == null) return null;
      pose.Position = Vector3.TransformCoordinate(here + offset, stage.World);
      return pose;
    }

    private WorldConversationCameraPose WorldConversationPoseFromCameraMark(WorldConversationStageContext stage, GomObjectData mark, float extraYaw) {
      if (stage == null || mark == null) return null;
      Vector3 local = SpnDynVector3(WorldInteractionDataValue(mark, "stgMarkPosition", "4611686024438910010"), Vector3.Zero);
      Vector3 rotation = SpnDynVector3(WorldInteractionDataValue(mark, "stgMarkRotation", "4611686024438910011"), Vector3.Zero);
      float pitch = DegreesToRadians(rotation.X);
      float yaw = stage.Yaw + DegreesToRadians(rotation.Y) + extraYaw;
      float cp = (float)Math.Cos(pitch), sp = (float)Math.Sin(pitch);
      // Jedipedia's FlyingCamera returns the actual visible forward vector (yaw 0 = -Z). PugTools' FpsCamera feeds
      // its stored Look vector into a RH projection whose visible direction is -Look -- the same convention already
      // handled by the taxi/space-camera paths. Store the COMPLETE negation of Jedipedia's forward (including pitch),
      // otherwise a correct camera mark looks 180 degrees away horizontally and pitched the wrong way vertically.
      Vector3 look = new Vector3((float)Math.Sin(yaw) * cp, -sp, (float)Math.Cos(yaw) * cp);
      if (!IsFiniteVector(look) || look.LengthSquared() < .000001f) return null;
      look.Normalize();
      float fovDegrees = SpnDynNumber(WorldInteractionDataValue(mark, "stgCameraFov", "4611686025915590054"), Single.NaN);
      return new WorldConversationCameraPose {
        Position = Vector3.TransformCoordinate(local, stage.World),
        Look = look,
        Up = Vector3.UnitY,
        Fov = !Single.IsNaN(fovDegrees) && fovDegrees > 0f ? DegreesToRadians(fovDegrees) : (float?)null
      };
    }

    private void ApplyWorldConversationCameraOverrides(WorldConversationCameraPose pose, Dictionary<string, string> parameters) {
      if (pose == null || parameters == null || parameters.Count == 0) return;
      if (WorldConversationParameterFloat(parameters, "overridepositionx", out float x)) pose.Position.X += x;
      if (WorldConversationParameterFloat(parameters, "overridepositiony", out float y)) pose.Position.Y += y;
      if (WorldConversationParameterFloat(parameters, "overridepositionz", out float z)) pose.Position.Z += z;
      if (WorldConversationParameterFloat(parameters, "overridefov", out float fov) && fov > 0f) pose.Fov = DegreesToRadians(fov);

      bool hasPitch = WorldConversationParameterFloat(parameters, "overriderotationx", out float pitchDeg);
      bool hasYaw = WorldConversationParameterFloat(parameters, "overriderotationy", out float yawDeg);
      if (!hasPitch && !hasYaw) return;
      Vector3 look = pose.Look;
      if (look.LengthSquared() < .000001f) return;
      look.Normalize();
      float pitch = hasPitch ? DegreesToRadians(pitchDeg) : -(float)Math.Asin(Math.Max(-1f, Math.Min(1f, look.Y)));
      float yaw = (float)Math.Atan2(look.X, look.Z);
      if (hasYaw) yaw += DegreesToRadians(yawDeg);
      float cp = (float)Math.Cos(pitch);
      pose.Look = new Vector3((float)Math.Sin(yaw) * cp, -(float)Math.Sin(pitch), (float)Math.Cos(yaw) * cp);
      if (pose.Look.LengthSquared() > .000001f) pose.Look.Normalize();
    }

    private Dictionary<string, string> WorldConversationActionParameters(string raw) {
      var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
      if (String.IsNullOrWhiteSpace(raw)) return result;
      foreach (string pair in raw.Split(';')) {
        int split = pair.IndexOf('=');
        if (split <= 0) continue;
        string key = pair.Substring(0, split).Trim();
        if (key.Length == 0) continue;
        result[key] = pair.Substring(split + 1).Trim();
      }
      return result;
    }

    private static float WorldConversationParameterSeconds(Dictionary<string, string> parameters, string key) {
      return WorldConversationParameterFloat(parameters, key, out float seconds) && seconds > 0f ? seconds : 0f;
    }

    private static string WorldConversationParameterText(Dictionary<string, string> parameters, string key) {
      return parameters != null && parameters.TryGetValue(key, out string text) ? (text ?? String.Empty).Trim() : String.Empty;
    }

    private static bool WorldConversationParameterFloat(Dictionary<string, string> parameters, string key, out float value) {
      value = 0f;
      return parameters != null && parameters.TryGetValue(key, out string text) &&
        Single.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && !Single.IsNaN(value) && !Single.IsInfinity(value);
    }

    private bool TryGetWorldConversationStageContext(string stageFqn, out WorldConversationStageContext context) {
      context = null;
      string clean = NormalizeWorldConversationStageFqn(stageFqn);
      if (clean.Length == 0 || area?.RoomList == null) return false;
      GomObjectData stageData = LoadWorldConversationStage(clean);
      if (stageData == null) return false;

      Matrix bestWorld = Matrix.Identity;
      bool found = false;
      float bestDistance = Single.MaxValue;
      Vector3 reference = Vector3.Zero;
      bool hasReference = false;
      if (worldConversationPrimaryNpc?.Instance != null && worldConversationPrimaryNpc.Room != null) {
        try {
          Matrix primaryWorld = worldConversationPrimaryNpc.Instance.GetAbsoluteTransform(worldConversationPrimaryNpc.Room);
          reference = new Vector3(primaryWorld.M41, primaryWorld.M42, primaryWorld.M43);
          hasReference = true;
        } catch { }
      }

      foreach (Room room in area.RoomList) {
        if (room?.InstancesById == null) continue;
        foreach (AssetInstance instance in room.InstancesById.Values) {
          if (instance == null || !area.AssetIdMap.TryGetValue(instance.assetID, out AreaAsset asset) || asset == null) continue;
          if (!String.Equals(NormalizeWorldConversationStageAsset(asset), clean, StringComparison.OrdinalIgnoreCase)) continue;
          Matrix world;
          try { world = instance.GetAbsoluteTransform(room); } catch { continue; }
          float distance = 0f;
          if (hasReference) {
            Vector3 p = new Vector3(world.M41, world.M42, world.M43);
            distance = (p - reference).LengthSquared();
          }
          if (!found || distance < bestDistance) { found = true; bestDistance = distance; bestWorld = world; }
        }
      }
      if (!found) return false;
      context = new WorldConversationStageContext {
        Fqn = clean,
        Data = stageData,
        World = bestWorld,
        Yaw = (float)Math.Atan2(-bestWorld.M13, bestWorld.M11)
      };
      return true;
    }

    private GomObjectData LoadWorldConversationStage(string fqn) {
      EnsureWorldConversationCinematicCaches();
      if (worldConversationStageCache.TryGetValue(fqn, out GomObjectData cached)) return cached;
      GomObjectData data = null;
      try { data = currentDom?.GetObject(fqn)?.Data; } catch { }
      worldConversationStageCache[fqn] = data;
      return data;
    }

    private GomObjectData LoadWorldConversationAutoCamera(string fqn) {
      EnsureWorldConversationCinematicCaches();
      if (worldConversationAutoCameraCache.TryGetValue(fqn, out GomObjectData cached)) return cached;
      GomObjectData data = null;
      try { data = currentDom?.GetObject(fqn)?.Data; } catch { }
      worldConversationAutoCameraCache[fqn] = data;
      return data;
    }

    private void EnsureWorldConversationCinematicCaches() {
      if (ReferenceEquals(worldConversationCinematicCacheDom, currentDom)) return;
      worldConversationCinematicCacheDom = currentDom;
      worldConversationStageCache.Clear();
      worldConversationAutoCameraCache.Clear();
    }

    private static string NormalizeWorldConversationStageAsset(AreaAsset asset) {
      if (asset == null || !String.Equals(asset.Extension, "stg", StringComparison.OrdinalIgnoreCase)) return String.Empty;
      string path = asset.Path ?? String.Empty;
      if (!path.EndsWith(".stg", StringComparison.OrdinalIgnoreCase)) path += ".stg";
      return NormalizeWorldConversationStageFqn(path);
    }

    private static string NormalizeWorldConversationStageFqn(string value) {
      string text = (value ?? String.Empty).Trim().Replace('\\', '/').Trim('/').ToLowerInvariant();
      if (text.EndsWith(".stg", StringComparison.OrdinalIgnoreCase)) text = text.Substring(0, text.Length - 4);
      while (text.StartsWith("gamedata/", StringComparison.OrdinalIgnoreCase) || text.StartsWith("server/", StringComparison.OrdinalIgnoreCase) || text.StartsWith("resources/", StringComparison.OrdinalIgnoreCase)) {
        int slash = text.IndexOf('/');
        text = slash >= 0 ? text.Substring(slash + 1) : String.Empty;
      }
      text = text.Replace('/', '.').Trim('.');
      return text;
    }

    private bool TryGetWorldConversationRawData(Conversation conversation, long nodeId, out GomObjectData root, out GomObjectData nodeData) {
      root = null; nodeData = null;
      if (conversation == null || currentDom == null || nodeId == 0) return false;
      GomObject raw = null;
      try {
        raw = conversation.Id != 0 ? currentDom.GetObject(conversation.Id) : null;
        if (raw == null && !String.IsNullOrWhiteSpace(conversation.Fqn)) raw = currentDom.GetObject(conversation.Fqn);
      } catch { }
      root = raw?.Data;
      if (root == null) return false;
      object dialogs = WorldInteractionDataValue(root, "cnvTreeDialogNodes_Prototype", "4611686050212071021");
      foreach (KeyValuePair<object, object> pair in WorldConversationMapEntries(dialogs)) {
        GomObjectData candidate = WorldConversationFirstObjectData(pair.Value, false);
        if (candidate == null) continue;
        long id = WonkInt64(WorldInteractionDataValue(candidate, "cnvNodeNumber", "4611686019044571365"));
        if (id == 0) id = WonkInt64(pair.Key);
        if (id == nodeId) { nodeData = candidate; return true; }
      }
      return false;
    }

    private static object WorldConversationMapValueUnsigned(object map, ulong wanted) {
      foreach (KeyValuePair<object, object> pair in WorldConversationMapEntries(map))
        if (WorldInteractionUnsigned(pair.Key) == wanted) return pair.Value;
      return null;
    }

    private static IEnumerable<KeyValuePair<object, object>> WorldConversationMapEntries(object map) {
      if (map is GomObjectData gom) {
        foreach (KeyValuePair<string, object> pair in gom.Dictionary) {
          if (String.Equals(pair.Key, "_count", StringComparison.OrdinalIgnoreCase)) continue;
          yield return new KeyValuePair<object, object>(pair.Key, pair.Value);
        }
        yield break;
      }
      if (map is IDictionary dictionary) {
        IDictionaryEnumerator it = dictionary.GetEnumerator();
        while (it.MoveNext()) {
          if (String.Equals(it.Key?.ToString(), "_count", StringComparison.OrdinalIgnoreCase)) continue;
          yield return new KeyValuePair<object, object>(it.Key, it.Value);
        }
      }
    }

    private static GomObjectData WorldConversationFirstObjectData(object value, bool descendOneLevel) {
      if (value is GomObjectData direct) {
        if (!descendOneLevel) return direct;
        object preferred = direct.Dictionary.TryGetValue("1", out object one) ? one : direct.Dictionary.Values.FirstOrDefault();
        return preferred as GomObjectData ?? direct;
      }
      if (value is IDictionary dictionary) {
        object preferred = null;
        if (dictionary.Contains("1")) preferred = dictionary["1"];
        if (preferred == null) {
          IDictionaryEnumerator it = dictionary.GetEnumerator();
          while (it.MoveNext()) { if (!String.Equals(it.Key?.ToString(), "_count", StringComparison.OrdinalIgnoreCase)) { preferred = it.Value; break; } }
        }
        return preferred as GomObjectData;
      }
      return null;
    }

    private static GomObjectData WorldConversationNamedObject(object table, string name) {
      if (String.IsNullOrWhiteSpace(name)) return null;
      foreach (KeyValuePair<object, object> pair in WorldConversationMapEntries(table)) {
        if (!String.Equals(pair.Key?.ToString(), name, StringComparison.OrdinalIgnoreCase)) continue;
        return WorldConversationFirstObjectData(pair.Value, false);
      }
      return null;
    }

    private static float DegreesToRadians(float degrees) => degrees * (float)Math.PI / 180f;
    private static bool IsFiniteVector(Vector3 value) => !Single.IsNaN(value.X) && !Single.IsInfinity(value.X) && !Single.IsNaN(value.Y) && !Single.IsInfinity(value.Y) && !Single.IsNaN(value.Z) && !Single.IsInfinity(value.Z);
  }
}
