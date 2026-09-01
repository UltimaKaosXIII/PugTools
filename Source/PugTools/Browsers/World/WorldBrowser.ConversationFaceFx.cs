using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GomLib.Models;

namespace PugTools {
  public partial class WorldBrowser {
    private readonly Dictionary<string, ViewFXE.FxeInfo> worldConversationFaceFxSetCache =
      new Dictionary<string, ViewFXE.FxeInfo>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ViewFXA.FxaInfo> worldConversationFaceFxActorCache =
      new Dictionary<string, ViewFXA.FxaInfo>(StringComparer.OrdinalIgnoreCase);
    private object worldConversationFaceFxCacheAssets;

    // FaceFX animations are named after the VO take they were authored against. This is the same join Jedipedia
    // uses: prefer the exact WEM basename and only fall back to the decoded node/speaker tuple for legacy naming.
    private WorldNpcFaceFxClip LoadWorldConversationFaceFx(Conversation conversation, DialogNode node, ViewWEM wem,
        WorldNpcPlacement speaker) {
      if (conversation == null || node == null || wem == null || speaker == null || currentAssets == null ||
          String.IsNullOrWhiteSpace(speaker.BodyType)) return null;
      if (!ReferenceEquals(worldConversationFaceFxCacheAssets, currentAssets)) {
        worldConversationFaceFxSetCache.Clear();
        worldConversationFaceFxActorCache.Clear();
        worldConversationFaceFxCacheAssets = currentAssets;
      }

      string fqn = !String.IsNullOrWhiteSpace(node.CnvAlienVOFQN) ? node.CnvAlienVOFQN : conversation.Fqn;
      long wantedNode = !String.IsNullOrWhiteSpace(node.CnvAlienVOFQN) && node.CnvAlienVONode != 0 ? node.CnvAlienVONode : node.NodeId;
      ViewFXE.FxeInfo set = null;
      foreach (string path in WorldConversationFaceFxCandidates(fqn, !String.IsNullOrWhiteSpace(node.CnvAlienVOFQN))) {
        set = LoadWorldConversationFaceFxSet(path);
        if (set != null && set.Animations.Count > 0) break;
      }
      if (set == null) return null;

      ViewFXE.Animation animation = ChooseWorldConversationFaceFxAnimation(set.Animations, wem.WemName, wantedNode);
      if (animation == null) return null;
      ViewFXA.FxaInfo actor = LoadWorldConversationFaceFxActor(speaker.BodyType);
      if (actor == null || actor.FaceGraph.Count == 0 || actor.Bones.Count == 0) return null;

      var clip = new WorldNpcFaceFxClip { Actor = actor, Animation = animation };
      for (int i = 0; i < actor.FaceGraph.Count; i++) {
        string name = actor.FaceGraph[i]?.Name;
        if (!String.IsNullOrWhiteSpace(name)) clip.NodeByName[name] = i;
      }
      foreach (ViewFXA.Bone bone in actor.Bones) {
        if (bone == null || bone.ReferenceWeight == 0f || bone.PoseNodes == null || bone.PoseNodes.Count == 0 || String.IsNullOrWhiteSpace(bone.Name)) continue;
        clip.BoneByName[NpcFaceFxCanonicalBoneName(bone.Name)] = bone;
      }
      return clip.BoneByName.Count == 0 ? null : clip;
    }

    private IEnumerable<string> WorldConversationFaceFxCandidates(string fqn, bool alien) {
      string slash = (fqn ?? String.Empty).Trim().ToLowerInvariant().Replace('.', '/');
      if (String.IsNullOrWhiteSpace(slash)) yield break;
      if (alien || fqn.StartsWith("cnv.alien_vo", StringComparison.OrdinalIgnoreCase)) {
        yield return "/resources/alien/fxe/" + slash + ".fxe";
        yield break;
      }
      string locale = WorldConversationAudioLocale();
      yield return "/resources/" + locale + "/fxe/" + slash + ".fxe";
      if (!String.Equals(locale, "en-us", StringComparison.OrdinalIgnoreCase))
        yield return "/resources/en-us/fxe/" + slash + ".fxe";
    }

    private ViewFXE.FxeInfo LoadWorldConversationFaceFxSet(string path) {
      if (String.IsNullOrWhiteSpace(path) || currentAssets == null) return null;
      if (worldConversationFaceFxSetCache.TryGetValue(path, out ViewFXE.FxeInfo cached)) return cached;
      try {
        using var file = currentAssets.FindFile(path);
        if (file == null) { worldConversationFaceFxSetCache[path] = null; return null; }
        using Stream stream = file.OpenCopyInMemory();
        ViewFXE.FxeInfo parsed = ViewFXE.Parse(stream);
        worldConversationFaceFxSetCache[path] = parsed;
        return parsed;
      } catch (Exception ex) {
        System.Diagnostics.Debug.WriteLine("Conversation FXE load failed " + path + ": " + ex.Message);
        worldConversationFaceFxSetCache[path] = null;
        return null;
      }
    }

    private ViewFXA.FxaInfo LoadWorldConversationFaceFxActor(string bodyType) {
      string bt = (bodyType ?? String.Empty).Trim().ToLowerInvariant();
      if (String.IsNullOrWhiteSpace(bt) || currentAssets == null) return null;
      if (worldConversationFaceFxActorCache.TryGetValue(bt, out ViewFXA.FxaInfo cached)) return cached;
      string path = "/resources/server/facefx/" + bt + "_base.fxa";
      try {
        using var file = currentAssets.FindFile(path);
        if (file == null) { worldConversationFaceFxActorCache[bt] = null; return null; }
        using Stream stream = file.OpenCopyInMemory();
        ViewFXA.FxaInfo parsed = ViewFXA.Parse(stream);
        worldConversationFaceFxActorCache[bt] = parsed;
        return parsed;
      } catch (Exception ex) {
        System.Diagnostics.Debug.WriteLine("Conversation FXA load failed " + path + ": " + ex.Message);
        worldConversationFaceFxActorCache[bt] = null;
        return null;
      }
    }

    private static ViewFXE.Animation ChooseWorldConversationFaceFxAnimation(IList<ViewFXE.Animation> animations, string wemName, long wantedNode) {
      if (animations == null || animations.Count == 0) return null;
      string exact = Path.GetFileNameWithoutExtension(wemName ?? String.Empty).Trim().ToLowerInvariant();
      if (!String.IsNullOrWhiteSpace(exact)) {
        ViewFXE.Animation byName = animations.FirstOrDefault(a => String.Equals(
          Path.GetFileNameWithoutExtension(a?.Name ?? String.Empty).Trim(), exact, StringComparison.OrdinalIgnoreCase));
        if (byName != null) return byName;
      }

      string speakerCode = WorldConversationAudioSpeakerCode(wemName);
      List<ViewFXE.Animation> nodeMatches = animations.Where(a => WorldConversationAudioNodeIndex(a?.Name) == wantedNode).ToList();
      if (!String.IsNullOrWhiteSpace(speakerCode)) {
        ViewFXE.Animation sameSpeaker = nodeMatches.FirstOrDefault(a =>
          String.Equals(WorldConversationAudioSpeakerCode(a?.Name), speakerCode, StringComparison.OrdinalIgnoreCase));
        if (sameSpeaker != null) return sameSpeaker;
      }
      return nodeMatches.FirstOrDefault();
    }

    private static string NpcFaceFxCanonicalBoneName(string name) {
      string normalized = (name ?? String.Empty).Trim().ToLowerInvariant();
      int colon = normalized.LastIndexOf(':');
      if (colon >= 0 && colon + 1 < normalized.Length) normalized = normalized.Substring(colon + 1);
      return normalized;
    }

    private void ClearWorldConversationFaceFx() {
      foreach (WorldNpcPlacement placement in worldConversationFaceFxNpcs.ToArray()) panelRender?.ClearWorldConversationFaceFx(placement);
      worldConversationFaceFxNpcs.Clear();
    }
  }
}
