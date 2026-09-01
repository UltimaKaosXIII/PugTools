using System;
using System.Numerics;

namespace PugTools {
  internal sealed partial class View_AREA {
    private const float NpcFaceFxEpsilon = 1.1920929e-7f;

    private static float NpcFaceFxCurve(ViewFXE.Curve curve, float time) {
      if (curve?.Keys == null || curve.Keys.Count == 0) return 0f;
      if (time <= curve.Keys[0].Time) return curve.Keys[0].Value;
      ViewFXE.Key last = curve.Keys[curve.Keys.Count - 1];
      if (time >= last.Time) return last.Value;
      int low = 0, high = curve.Keys.Count - 1;
      while (high - low > 1) {
        int mid = (low + high) >> 1;
        if (curve.Keys[mid].Time <= time) low = mid; else high = mid;
      }
      ViewFXE.Key a = curve.Keys[low], b = curve.Keys[high];
      float span = b.Time - a.Time;
      if (!(span > 0f)) return b.Value;
      float t = (time - a.Time) / span;
      float t2 = t * t, t3 = t2 * t;
      return (2f*t3 - 3f*t2 + 1f) * a.Value + (t3 - 2f*t2 + t) * span * a.SlopeOut +
        (-2f*t3 + 3f*t2) * b.Value + (t3 - t2) * span * b.SlopeIn;
    }

    // FaceFX's compiled graph is serialized in evaluation order. A single pass is therefore the complete runtime:
    // curve drive, incoming link function, Sum/Multiply combination and node clamp, matching Jedipedia/client logic.
    private static float[] NpcEvaluateFaceFx(NpcSkinState state, WorldNpcFaceFxClip face, float time) {
      ViewFXA.FxaInfo actor = face?.Actor;
      ViewFXE.Animation animation = face?.Animation;
      if (state == null || actor?.FaceGraph == null || actor.FaceGraph.Count == 0 || animation == null) return null;
      int count = actor.FaceGraph.Count;
      if (!ReferenceEquals(state.FaceActor, actor) || state.FaceValues == null || state.FaceValues.Length != count) {
        state.FaceActor = actor;
        state.FaceValues = new float[count];
        state.FaceDriven = new bool[count];
      } else {
        Array.Clear(state.FaceValues, 0, count);
        Array.Clear(state.FaceDriven, 0, count);
      }

      foreach (ViewFXE.Curve curve in animation.Curves) {
        if (curve == null || String.IsNullOrWhiteSpace(curve.Name) || !face.NodeByName.TryGetValue(curve.Name, out int index) || index < 0 || index >= count) continue;
        state.FaceValues[index] = NpcFaceFxCurve(curve, time);
        state.FaceDriven[index] = true;
      }

      for (int i = 0; i < count; i++) {
        ViewFXA.FaceNode node = actor.FaceGraph[i];
        bool sum = node.InputOperation == 0; // FxInputOp: 0 Sum, 1 Multiply. SWTOR actors use these two.
        float value = state.FaceDriven[i] ? state.FaceValues[i] : (sum ? 0f : 1f);
        foreach (ViewFXA.FaceLink link in node.InputLinks) {
          if (link == null || link.NodeIndex >= state.FaceValues.Length) continue;
          float input = state.FaceValues[link.NodeIndex];
          float mapped = input;
          // FxLinkFnType: 1 Linear, 2 Quadratic, 5 Negate. Other functions are not used by shipped SWTOR actors;
          // passthrough mirrors Jedipedia's safe default rather than silently zeroing an unknown graph edge.
          if (link.FunctionType == 1) {
            float m = link.Parameters.Count > 0 ? link.Parameters[0] : 0f;
            float b = link.Parameters.Count > 1 ? link.Parameters[1] : 0f;
            mapped = m * input + b;
          } else if (link.FunctionType == 2) {
            float a = link.Parameters.Count > 0 ? link.Parameters[0] : 0f;
            mapped = a * input * input;
          } else if (link.FunctionType == 5) mapped = -input;
          value = sum ? value + mapped : value * mapped;
        }
        state.FaceValues[i] = Math.Min(node.Max, Math.Max(node.Min, value));
      }
      return state.FaceValues;
    }

    private static bool NpcTryFaceFxDelta(WorldNpcFaceFxClip face, float[] graph, string rigBoneName,
        out Quaternion rotation, out Vector3 translation) {
      rotation = Quaternion.Identity;
      translation = Vector3.Zero;
      if (face?.Actor == null || graph == null || String.IsNullOrWhiteSpace(rigBoneName)) return false;
      string canonical = NpcFaceFxCanonicalBoneName(rigBoneName);
      if (!face.BoneByName.TryGetValue(canonical, out ViewFXA.Bone bone) || bone?.PoseNodes == null || bone.PoseNodes.Count == 0) return false;

      Quaternion reference = NpcFaceFxQuaternion(bone.Rotation);
      Quaternion inverse = NpcFaceFxQuaternion(bone.InverseRotation);
      if (!NpcJbaFinite(reference) || !NpcJbaFinite(inverse)) return false;
      Quaternion accumulated = reference;
      float dx = 0f, dy = 0f, dz = 0f;
      bool moved = false;
      foreach (ViewFXA.PoseNode pose in bone.PoseNodes) {
        if (pose == null || pose.NodeIndex >= graph.Length) continue;
        float weight = graph[pose.NodeIndex];
        if (!(weight >= NpcFaceFxEpsilon || weight <= -NpcFaceFxEpsilon)) continue;
        moved = true;
        if (pose.Position != null && pose.Position.Length >= 3) {
          dx += weight * pose.Position[0]; dy += weight * pose.Position[1]; dz += weight * pose.Position[2];
        }
        Quaternion target = NpcFaceFxQuaternion(pose.Rotation);
        if (!NpcJbaFinite(target)) continue;
        Quaternion mixed = Quaternion.Slerp(reference, target, weight);
        // Each link is measured from the actor reference, never from the result of the previous link. Reusing the
        // running pose here is the classic FaceFX bug that compounds lips/lids into frozen or over-rotated faces.
        accumulated = NpcJbaQuatMultiply(accumulated, inverse);
        accumulated = NpcJbaQuatMultiply(accumulated, mixed);
      }
      if (!moved || !NpcJbaFinite(accumulated) || accumulated.LengthSquared() <= .0000001f) return false;
      accumulated = Quaternion.Normalize(accumulated);
      Quaternion delta = NpcJbaQuatMultiply(accumulated, inverse);
      if (!NpcJbaFinite(delta)) return false;

      // FaceFX -> Morpheme frame. Both systems use centimetres, so this is only an axis relabelling (no scale).
      rotation = new Quaternion(-delta.X, -delta.Z, -delta.Y, delta.W);
      translation = new Vector3(dx, dz, dy);
      return NpcJbaFinite(rotation) && NpcJbaFinite(translation);
    }

    private static Quaternion NpcFaceFxQuaternion(float[] values) {
      if (values == null || values.Length < 4) return Quaternion.Identity;
      Quaternion result = new Quaternion(values[0], values[1], values[2], values[3]);
      if (!NpcJbaFinite(result) || result.LengthSquared() <= .0000001f) return Quaternion.Identity;
      return Quaternion.Normalize(result);
    }

    private static string NpcFaceFxCanonicalBoneName(string name) {
      string normalized = (name ?? String.Empty).Trim().ToLowerInvariant();
      int colon = normalized.LastIndexOf(':');
      if (colon >= 0 && colon + 1 < normalized.Length) normalized = normalized.Substring(colon + 1);
      return normalized;
    }
  }
}
