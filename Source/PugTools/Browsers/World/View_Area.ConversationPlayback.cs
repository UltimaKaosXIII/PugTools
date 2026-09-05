using System;
using System.Collections.Generic;
using SlimDX;

namespace PugTools {
  internal sealed partial class View_AREA {
    private volatile string worldConversationSubtitleSpeaker;
    private volatile string worldConversationSubtitleText;

    private bool worldConversationCameraActive;
    private bool worldConversationCameraSaved;
    private Vector3 worldConversationCameraSavedPosition;
    private Vector3 worldConversationCameraSavedLook;
    private Vector3 worldConversationCameraSavedUp;
    private float worldConversationCameraSavedFov;
    private Vector3 worldConversationCameraFromPosition;
    private Vector3 worldConversationCameraFromLook;
    private Vector3 worldConversationCameraFromUp;
    private float worldConversationCameraFromFov;
    private Vector3 worldConversationCameraTargetPosition;
    private Vector3 worldConversationCameraTargetLook;
    private Vector3 worldConversationCameraTargetUp;
    private float worldConversationCameraTargetFov;
    private float worldConversationCameraMoveStarted;
    private float worldConversationCameraMovePositionSeconds;
    private float worldConversationCameraMoveRotationSeconds;
    private float worldConversationCameraMoveFovSeconds;
    private string worldConversationCameraMovePositionType;
    private string worldConversationCameraMoveRotationType;
    private string worldConversationCameraMoveFovType;

    internal void SetWorldConversationSubtitle(string speaker, string text) {
      worldConversationSubtitleSpeaker = speaker ?? String.Empty;
      worldConversationSubtitleText = text ?? String.Empty;
    }

    internal void ClearWorldConversationSubtitle() {
      worldConversationSubtitleSpeaker = null;
      worldConversationSubtitleText = null;
    }

    internal void SetWorldConversationAnimation(WorldNpcPlacement placement, WorldNpcAnimationClip clip, bool loop = false,
        float startAt = 0f, float speed = 1f) {
      if (placement == null) return;
      placement.ConversationAnimation = clip;
      placement.ConversationAnimationStart = elapsed;
      placement.ConversationAnimationOffset = Single.IsNaN(startAt) || Single.IsInfinity(startAt) ? 0f : Math.Max(0f, startAt);
      placement.ConversationAnimationSpeed = Single.IsNaN(speed) || Single.IsInfinity(speed) || speed <= 0f ? 1f : speed;
      placement.ConversationAnimationLoop = loop;
    }

    internal void ClearWorldConversationAnimation(WorldNpcPlacement placement) {
      if (placement == null) return;
      placement.ConversationAnimation = null;
      placement.ConversationAnimationStart = 0f;
      placement.ConversationAnimationOffset = 0f;
      placement.ConversationAnimationSpeed = 1f;
      placement.ConversationAnimationLoop = false;
    }

    internal void SetWorldConversationFaceFx(WorldNpcPlacement placement, WorldNpcFaceFxClip face) {
      if (placement == null) return;
      placement.ConversationFaceFx = face;
      placement.ConversationFaceFxStart = elapsed;
    }

    internal void ClearWorldConversationFaceFx(WorldNpcPlacement placement) {
      if (placement == null) return;
      placement.ConversationFaceFx = null;
      placement.ConversationFaceFxStart = 0f;
    }

    internal void SetWorldConversationActorMark(WorldNpcPlacement placement, Vector3 position, float yaw) {
      if (placement == null || !IsFinite(position) || Single.IsNaN(yaw) || Single.IsInfinity(yaw)) return;
      Matrix current = placement.ConversationWorld ?? NpcPlacementAuthoredWorld(placement);
      float sx = (float)Math.Sqrt(current.M11 * current.M11 + current.M12 * current.M12 + current.M13 * current.M13);
      float sy = (float)Math.Sqrt(current.M21 * current.M21 + current.M22 * current.M22 + current.M23 * current.M23);
      float sz = (float)Math.Sqrt(current.M31 * current.M31 + current.M32 * current.M32 + current.M33 * current.M33);
      if (!(sx > .000001f)) sx = 1f; if (!(sy > .000001f)) sy = 1f; if (!(sz > .000001f)) sz = 1f;

      // SWTOR staging actor marks face local +X. Reproduce Jedipedia's viewerCnvMoveInstance INSTANCE matrix exactly.
      // Important: this is still the placement matrix, not PugTools' final GR2 draw matrix. DrawJedipediaNpcs applies
      // the same fixed PI character/spawner correction to conversation placements as it does to ordinary AREA NPCs.
      // Pre-cancelling that correction here turns every staged actor 180 degrees away from the authored mark.
      float facingX = (float)Math.Cos(yaw), facingZ = -(float)Math.Sin(yaw);
      Matrix desired = Matrix.Identity;
      desired.M11 = -facingZ * sx; desired.M12 = 0f; desired.M13 = facingX * sx;
      desired.M21 = 0f; desired.M22 = sy; desired.M23 = 0f;
      desired.M31 = -facingX * sz; desired.M32 = 0f; desired.M33 = -facingZ * sz;
      desired.M41 = position.X; desired.M42 = position.Y; desired.M43 = position.Z;
      placement.ConversationWorld = desired;
      if (placement.ConversationVirtual && placement.ConversationUnplaced) {
        placement.ConversationUnplaced = false;
        placement.ConversationHidden = false;
      }
    }

    internal void SetWorldConversationActorHidden(WorldNpcPlacement placement, bool hidden) {
      if (placement == null) return;
      placement.ConversationHidden = hidden;
    }

    internal void ClearWorldConversationActorState(WorldNpcPlacement placement) {
      if (placement == null) return;
      placement.ConversationWorld = null;
      placement.ConversationHidden = false;
      placement.ConversationUnplaced = false;
    }

    internal void SetWorldConversationCamera(Vector3 position, Vector3 look, Vector3 up, float? fovRadians,
        float moveSeconds, float rotateSeconds, float fovSeconds, string moveType = null, string rotateType = null, string fovType = null) {
      if (camera == null || !IsFinite(position) || !IsFinite(look) || look.LengthSquared() < .000001f) return;
      look.Normalize();
      if (!IsFinite(up) || up.LengthSquared() < .000001f || Math.Abs(Vector3.Dot(up, look)) > .995f) up = Vector3.UnitY;
      else up.Normalize();
      if (!worldConversationCameraSaved) {
        worldConversationCameraSaved = true;
        worldConversationCameraSavedPosition = camera.Position;
        worldConversationCameraSavedLook = camera.Look;
        worldConversationCameraSavedUp = camera.Up;
        worldConversationCameraSavedFov = camera.FovY;
      }
      worldConversationCameraActive = true;
      worldConversationCameraFromPosition = camera.Position;
      worldConversationCameraFromLook = camera.Look;
      worldConversationCameraFromUp = camera.Up;
      worldConversationCameraFromFov = camera.FovY;
      worldConversationCameraTargetPosition = position;
      worldConversationCameraTargetLook = look;
      worldConversationCameraTargetUp = up;
      worldConversationCameraTargetFov = fovRadians.HasValue && fovRadians.Value > .01f ? fovRadians.Value : camera.FovY;
      worldConversationCameraMoveStarted = elapsed;
      worldConversationCameraMovePositionSeconds = WorldConversationCameraSeconds(moveSeconds);
      worldConversationCameraMoveRotationSeconds = WorldConversationCameraSeconds(rotateSeconds);
      worldConversationCameraMoveFovSeconds = WorldConversationCameraSeconds(fovSeconds);
      worldConversationCameraMovePositionType = moveType;
      worldConversationCameraMoveRotationType = rotateType;
      worldConversationCameraMoveFovType = fovType;
      if (Math.Max(worldConversationCameraMovePositionSeconds, Math.Max(worldConversationCameraMoveRotationSeconds, worldConversationCameraMoveFovSeconds)) <= .001f)
        ApplyWorldConversationCameraPose(position, look, up, worldConversationCameraTargetFov);
      InvalidateTemporalHistory();
    }

    internal void ClearWorldConversationCamera(bool restore) {
      if (camera == null) return;
      bool hadCamera = worldConversationCameraActive || worldConversationCameraSaved;
      worldConversationCameraActive = false;
      worldConversationCameraMovePositionSeconds = 0f;
      worldConversationCameraMoveRotationSeconds = 0f;
      worldConversationCameraMoveFovSeconds = 0f;
      if (restore && worldConversationCameraSaved) {
        Vector3 look = worldConversationCameraSavedLook;
        Vector3 up = worldConversationCameraSavedUp;
        if (!IsFinite(look) || look.LengthSquared() < .000001f) look = Vector3.UnitZ;
        if (!IsFinite(up) || up.LengthSquared() < .000001f) up = Vector3.UnitY;
        camera.LookAt(worldConversationCameraSavedPosition, worldConversationCameraSavedPosition + look, up);
        float fov = worldConversationCameraSavedFov > .01f ? worldConversationCameraSavedFov : camera.FovY;
        camera.SetLens(fov, Math.Max(.01f, AspectRatio), .01f, Math.Max(20f, camera.FarZ));
        camera.UpdateViewMatrix();
      }
      worldConversationCameraSaved = false;
      if (hadCamera) InvalidateTemporalHistory();
    }

    private void UpdateWorldConversationCamera() {
      if (!worldConversationCameraActive || camera == null) return;
      float total = Math.Max(worldConversationCameraMovePositionSeconds, Math.Max(worldConversationCameraMoveRotationSeconds, worldConversationCameraMoveFovSeconds));
      if (total <= .001f) {
        ApplyWorldConversationCameraPose(worldConversationCameraTargetPosition, worldConversationCameraTargetLook,
          worldConversationCameraTargetUp, worldConversationCameraTargetFov);
        return;
      }
      float since = Math.Max(0f, elapsed - worldConversationCameraMoveStarted);
      float positionT = WorldConversationCameraEase(worldConversationCameraMovePositionType,
        worldConversationCameraMovePositionSeconds > 0f ? since / worldConversationCameraMovePositionSeconds : 1f);
      float rotationT = WorldConversationCameraEase(worldConversationCameraMoveRotationType,
        worldConversationCameraMoveRotationSeconds > 0f ? since / worldConversationCameraMoveRotationSeconds : 1f);
      float fovT = WorldConversationCameraEase(worldConversationCameraMoveFovType,
        worldConversationCameraMoveFovSeconds > 0f ? since / worldConversationCameraMoveFovSeconds : 1f);

      Vector3 position = Vector3.Lerp(worldConversationCameraFromPosition, worldConversationCameraTargetPosition, positionT);
      Vector3 look = WorldConversationCameraDirectionBetween(worldConversationCameraFromLook, worldConversationCameraTargetLook, rotationT);
      Vector3 up = Vector3.Lerp(worldConversationCameraFromUp, worldConversationCameraTargetUp, rotationT);
      float fov = worldConversationCameraFromFov + (worldConversationCameraTargetFov - worldConversationCameraFromFov) * fovT;
      if (up.LengthSquared() < .000001f || Math.Abs(Vector3.Dot(up, look)) > .995f) up = Vector3.UnitY; else up.Normalize();
      ApplyWorldConversationCameraPose(position, look, up, fov);
      if (positionT >= 1f && rotationT >= 1f && fovT >= 1f) {
        worldConversationCameraMovePositionSeconds = 0f;
        worldConversationCameraMoveRotationSeconds = 0f;
        worldConversationCameraMoveFovSeconds = 0f;
      }
    }

    private static float WorldConversationCameraSeconds(float seconds) {
      return Single.IsNaN(seconds) || Single.IsInfinity(seconds) || seconds <= 0f ? 0f : seconds;
    }

    // SWTOR's camera move curves form a closed four-value set. Linear is the serializer's default and therefore the
    // correct answer for absent/unknown values as well.
    private static float WorldConversationCameraEase(string kind, float t) {
      float x = Math.Max(0f, Math.Min(1f, t));
      string name = (kind ?? String.Empty).Trim().ToLowerInvariant();
      if (name == "easeout") return 1f - (1f - x) * (1f - x);
      if (name == "easein") return x * x;
      if (name == "smooth") return x * x * (3f - 2f * x);
      return x;
    }

    private static Vector3 WorldConversationCameraDirectionBetween(Vector3 from, Vector3 to, float t) {
      if (!IsFinite(from) || from.LengthSquared() < .000001f) from = Vector3.UnitZ; else from.Normalize();
      if (!IsFinite(to) || to.LengthSquared() < .000001f) to = from; else to.Normalize();
      float fromPitch = (float)Math.Asin(Math.Max(-1f, Math.Min(1f, from.Y)));
      float toPitch = (float)Math.Asin(Math.Max(-1f, Math.Min(1f, to.Y)));
      float fromYaw = (float)Math.Atan2(from.X, from.Z);
      float toYaw = (float)Math.Atan2(to.X, to.Z);
      float delta = (toYaw - fromYaw) % ((float)Math.PI * 2f);
      if (delta > Math.PI) delta -= (float)Math.PI * 2f;
      if (delta < -Math.PI) delta += (float)Math.PI * 2f;
      float pitch = fromPitch + (toPitch - fromPitch) * t;
      float yaw = fromYaw + delta * t;
      float cp = (float)Math.Cos(pitch);
      Vector3 result = new Vector3((float)Math.Sin(yaw) * cp, (float)Math.Sin(pitch), (float)Math.Cos(yaw) * cp);
      if (result.LengthSquared() > .000001f) result.Normalize();
      return result;
    }

    private void ApplyWorldConversationCameraPose(Vector3 position, Vector3 look, Vector3 up, float fov) {
      camera.LookAt(position, position + look, up);
      if (fov > .01f) camera.SetLens(fov, Math.Max(.01f, AspectRatio), .01f, Math.Max(20f, camera.FarZ));
    }

    private static List<string> WrapWorldConversationSubtitle(string text, int maxChars) {
      var result = new List<string>();
      if (String.IsNullOrWhiteSpace(text)) return result;
      string[] paragraphs = text.Replace("\r", String.Empty).Split('\n');
      foreach (string paragraph in paragraphs) {
        string[] words = paragraph.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        string line = String.Empty;
        foreach (string word in words) {
          if (line.Length == 0) line = word;
          else if (line.Length + 1 + word.Length <= maxChars) line += " " + word;
          else { result.Add(line); line = word; }
        }
        if (line.Length > 0) result.Add(line);
      }
      return result;
    }

    private void DrawWorldConversationSubtitleHud(WorldRenderSettings settings) {
      if (settings == null || settings.Mode == WorldRenderMode.Map || settings.Mode == WorldRenderMode.Heightmap ||
          !npcTextFontsRegistered || npcTextFont == null || npcTextSprite == null) return;
      string text = worldConversationSubtitleText;
      if (String.IsNullOrWhiteSpace(text)) return;

      int width = Math.Max(1, (int)Viewport.Width);
      int height = Math.Max(1, (int)Viewport.Height);
      float y = Math.Max(40f, height - 150f);
      string speaker = worldConversationSubtitleSpeaker;
      if (!String.IsNullOrWhiteSpace(speaker)) {
        DrawNpcText(speaker, new Vector2(width * .5f, y), new Color4(1f, .95f, .73f, .23f), "world-npc-name");
        y += 22f;
      }
      foreach (string line in WrapWorldConversationSubtitle(text, Math.Max(42, Math.Min(100, width / 9)))) {
        DrawNpcText(line, new Vector2(width * .5f, y), new Color4(1f, 1f, 1f, 1f), "world-selection");
        y += 19f;
        if (y > height - 12f) break;
      }
    }
  }
}
