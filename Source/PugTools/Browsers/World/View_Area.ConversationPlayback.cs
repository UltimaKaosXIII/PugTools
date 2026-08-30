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
    private float worldConversationCameraMoveSeconds;

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

    internal void SetWorldConversationActorMark(WorldNpcPlacement placement, Vector3 position, float yaw) {
      if (placement == null || !IsFinite(position)) return;
      Matrix world = placement.ConversationWorld ?? NpcPlacementAuthoredWorld(placement);
      SetNpcWorldYaw(ref world, yaw);
      world.M41 = position.X; world.M42 = position.Y; world.M43 = position.Z;
      placement.ConversationWorld = world;
    }

    internal void SetWorldConversationActorHidden(WorldNpcPlacement placement, bool hidden) {
      if (placement == null) return;
      placement.ConversationHidden = hidden;
    }

    internal void ClearWorldConversationActorState(WorldNpcPlacement placement) {
      if (placement == null) return;
      placement.ConversationWorld = null;
      placement.ConversationHidden = false;
    }

    internal void SetWorldConversationCamera(Vector3 position, Vector3 look, Vector3 up, float? fovRadians, float moveSeconds) {
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
      worldConversationCameraMoveSeconds = Math.Max(0f, moveSeconds);
      if (worldConversationCameraMoveSeconds <= .001f) ApplyWorldConversationCameraPose(position, look, up, worldConversationCameraTargetFov);
      InvalidateTemporalHistory();
    }

    internal void ClearWorldConversationCamera(bool restore) {
      if (camera == null) return;
      bool hadCamera = worldConversationCameraActive || worldConversationCameraSaved;
      worldConversationCameraActive = false;
      worldConversationCameraMoveSeconds = 0f;
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
      if (worldConversationCameraMoveSeconds <= .001f) {
        ApplyWorldConversationCameraPose(worldConversationCameraTargetPosition, worldConversationCameraTargetLook,
          worldConversationCameraTargetUp, worldConversationCameraTargetFov);
        return;
      }
      float t = Math.Max(0f, Math.Min(1f, (elapsed - worldConversationCameraMoveStarted) / worldConversationCameraMoveSeconds));
      // SWTOR supports several per-component camera curves. Smoothstep is the closest safe common denominator and
      // avoids the robotic constant-speed slide when a move does not expose its curve metadata to this build.
      float eased = t * t * (3f - 2f * t);
      Vector3 position = Vector3.Lerp(worldConversationCameraFromPosition, worldConversationCameraTargetPosition, eased);
      Vector3 look = Vector3.Lerp(worldConversationCameraFromLook, worldConversationCameraTargetLook, eased);
      Vector3 up = Vector3.Lerp(worldConversationCameraFromUp, worldConversationCameraTargetUp, eased);
      float fov = worldConversationCameraFromFov + (worldConversationCameraTargetFov - worldConversationCameraFromFov) * eased;
      if (look.LengthSquared() < .000001f) look = worldConversationCameraTargetLook; else look.Normalize();
      if (up.LengthSquared() < .000001f || Math.Abs(Vector3.Dot(up, look)) > .995f) up = Vector3.UnitY; else up.Normalize();
      ApplyWorldConversationCameraPose(position, look, up, fov);
      if (t >= 1f) worldConversationCameraMoveSeconds = 0f;
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
