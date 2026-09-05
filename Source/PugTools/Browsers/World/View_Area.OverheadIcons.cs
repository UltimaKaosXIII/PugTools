using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using FileFormats;
using SlimDX;
using SlimDX.Direct3D11;
using SlimDX.DXGI;
using SlimDXNet.Vertex;
using Buffer = SlimDX.Direct3D11.Buffer;

namespace PugTools {
  internal sealed partial class View_AREA {
    // SWTOR's service/quest markers are ordinary .fxspec effects attached to the entity's NamePlate frame. The world
    // renderer still does not need a general-purpose particle simulator for these six tiny effects: resolve their PRT
    // emitters to the original DDS sprite sheets in the client archives, then draw the authored frame as the same
    // camera-facing marker the effect would produce. This keeps the artwork identical to the client/Jedipedia while
    // retaining PugTools' existing nameplate distance, room/dPVS and opaque-depth visibility rules.
    private sealed class WorldOverheadSprite {
      public string TexturePath;
      public int Columns = 1;
      public int Rows = 1;
      public int Frame;
      public float PixelWidth = 28f;
      public float PixelHeight = 28f;
    }

    private sealed class WorldInteractionIconEntry {
      public object Owner;
      public WorldInteractionInfo Interaction;
      public Vector3 Anchor;
      // Full world-space attachment frame for the authored FXSPEC. NPC effects use the literal NamePlate bone;
      // legacy/static sprite fallbacks intentionally continue to use Anchor instead.
      public Matrix? FxFrame;
      public Vector3 Screen;
      public Vector4 VisibilitySample;
      public float ScreenYOffset;
      public float Distance;
    }

    private readonly Dictionary<string, List<WorldOverheadSprite>> worldOverheadSpriteCache = new Dictionary<string, List<WorldOverheadSprite>>(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<object> worldInteractionIconVisible = new HashSet<object>();
    private readonly Vector4[] worldInteractionIconVisibilitySamples = new Vector4[NpcNameplateMaxCount];
    private int worldInteractionIconVisibilityFrame;
    private int worldInteractionIconVisibilityAskedAt = Int32.MinValue / 2;
    private bool worldInteractionIconHasVisibility;
    private bool worldInteractionIconVisibilityFailed;

    private void DrawWorldInteractionIcons(Matrix labelViewProj, Matrix depthViewProj, HashSet<string> visible, WorldRenderSettings s) {
      if (s == null || !s.ShowInteractionIcons || s.Mode == WorldRenderMode.Map || s.Mode == WorldRenderMode.Heightmap || camera == null) return;
      int width = Math.Max(1, (int)Viewport.Width), height = Math.Max(1, (int)Viewport.Height);
      if (width <= 0 || height <= 0) return;

      // SpriteTextRenderer owns a small amount of D3D state and is flushed immediately before this pass. Rebind the
      // world back-buffer/pipeline explicitly instead of relying on whatever state that helper left behind. Without
      // this, the marker draw can succeed without touching the visible render target (the bundled quest fallback then
      // disappears too, which is why a valid Burnok interaction could still show no icon at all).
      try {
        ImmediateContext.OutputMerger.SetTargets((DepthStencilView)null, RenderTargetView);
        ImmediateContext.Rasterizer.SetViewports(Viewport);
        ImmediateContext.InputAssembler.InputLayout = inputLayout;
        ImmediateContext.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
      } catch { }

      var entries = new List<WorldInteractionIconEntry>(NpcNameplateMaxCount);
      if ((s.ShowNpcs || s.ShowTaxiTerminals) && npcPlacements != null) {
        foreach (WorldNpcPlacement placement in NearbyNpcPlacements(NpcMaxRenderDistance)) {
          if (placement?.Interaction == null || placement.Instance == null || placement.Room == null || !NpcLayerVisible(placement, s)) continue;
          if (!SpnVariantActive(placement.VariantIndex, placement.VariantCount, placement.SpawnPoints)) continue;
          bool moving = placement.SpawnPoints != null && placement.SpawnPoints.Count > 0;
          if (!moving && !InstanceRoomVisible(placement.Instance, placement.Room, visible)) continue;
          if (!InstanceVisibleInWorld(placement.Instance, s)) continue;
          Matrix world;
          try { world = NpcNameplateWorld(placement); } catch { continue; }
          Vector3 basePos = new Vector3(world.M41, world.M42, world.M43);
          if (!NpcVisibleForWork(basePos)) continue;
          if (TryWorldModelsSphere(placement.Models, world, out Vector3 receiverCenter, out float receiverRadius) &&
              !DynamicReceiverVisibleByOcclusion(placement, receiverCenter, receiverRadius)) continue;

          Vector3 anchor;
          Vector3 testPoint;
          if (placement.NameplateLocal.HasValue) {
            Vector3 localAnchor = placement.NameplateLocal.Value;
            anchor = Vector3.TransformCoordinate(localAnchor, world);
            float clearLocalY = NpcNameplateClearLocalY(placement, localAnchor.Y);
            float liftLocal = Math.Max(0f, clearLocalY - localAnchor.Y) + NpcNameplateTestLift;
            testPoint = anchor + Vector3.TransformNormal(new Vector3(0f, liftLocal, 0f), world);
          } else if (TryWorldModelsSphere(placement.Models, world, out Vector3 center, out float radius)) {
            anchor = center + new Vector3(0f, Math.Max(.25f, radius * .75f), 0f);
            testPoint = center + new Vector3(0f, Math.Max(.3f, radius + NpcNameplateTestLift), 0f);
          } else {
            anchor = new Vector3(world.M41, world.M42 + 1.4f, world.M43);
            testPoint = anchor + new Vector3(0f, NpcNameplateTestLift, 0f);
          }
          Matrix? fxFrame = null;
          if (placement.OverheadIconLocalFrame.HasValue) {
            Matrix localFrame = placement.OverheadIconLocalFrame.Value;
            Matrix.Multiply(ref localFrame, ref world, out Matrix attachedFrame);
            if (NpcFinite(attachedFrame)) fxFrame = attachedFrame;
          }
          AddWorldInteractionIconEntry(entries, placement, placement.Interaction, anchor, testPoint, -24f, fxFrame,
            labelViewProj, depthViewProj, width, height);
        }
      }

      if (s.ShowSpnObjects && spnPlacements != null) {
        // Same population radius as nameplates. The old pass walked camera.FarZ, which left icons floating through
        // entire rooms and outdoor cells long after the corresponding SWTOR nameplate would have disappeared.
        foreach (WorldSpnPlacement placement in NearbySpnPlacements(NpcMaxRenderDistance)) {
          if (placement?.Interaction == null || placement.Instance == null || placement.Room == null) continue;
          if (!SpnVariantActive(placement.VariantIndex, placement.VariantCount, placement.SpawnPoints)) continue;
          bool moving = placement.Route != null || (placement.SpawnPoints != null && placement.SpawnPoints.Count > 0);
          if (!moving && !InstanceRoomVisible(placement.Instance, placement.Room, visible)) continue;
          if (!InstanceVisibleInWorld(placement.Instance, s)) continue;
          Matrix world;
          try { world = SpnPlacementWorld(placement, s.AnimateSpnObjects); } catch { continue; }
          Vector3 basePos = new Vector3(world.M41, world.M42, world.M43);
          if (!NpcVisibleForWork(basePos)) continue;
          WorldSpnDynState dyn = ActiveSpnDynState(placement, s.AnimateSpnObjects);
          if (dyn != null && dyn.Hidden) continue;
          Vector3 anchor;
          Vector3 testPoint;
          Matrix? fxFrame = null;
          // Placeables have no literal NamePlate bone. Keep this isolated from the NPC path: synthesize the same
          // top attachment frame Jedipedia uses for bindpoints/mission boards, then let the authored FXSPEC apply
          // its local offsets relative to that frame. No NPC skeleton/nameplate state is touched here.
          if (TryWorldOverheadTopFrame(placement, dyn, world, out Matrix topFrame)) {
            fxFrame = topFrame;
            anchor = new Vector3(topFrame.M41, topFrame.M42, topFrame.M43);
            Vector3 up = Vector3.TransformNormal(new Vector3(0f, 0f, -1f), topFrame);
            if (up.LengthSquared() > .000001f) up.Normalize(); else up = Vector3.UnitY;
            testPoint = anchor + up * NpcNameplateTestLift;
          } else if (TrySpnReceiverSphere(placement, world, dyn, out Vector3 center, out float radius)) {
            float above = Math.Max(.3f, radius * .85f);
            anchor = center + new Vector3(0f, above, 0f);
            testPoint = center + new Vector3(0f, Math.Max(above, radius + NpcNameplateTestLift), 0f);
          } else {
            anchor = new Vector3(world.M41, world.M42 + 1.1f, world.M43);
            testPoint = anchor + new Vector3(0f, NpcNameplateTestLift, 0f);
          }
          AddWorldInteractionIconEntry(entries, placement, placement.Interaction, anchor, testPoint, 0f, fxFrame,
            labelViewProj, depthViewProj, width, height);
        }
      }

      entries.Sort((a, b) => a.Distance.CompareTo(b.Distance));
      int budget = Math.Min(NpcNameplateMaxCount, Math.Max(1, width));
      if (entries.Count > budget) entries.RemoveRange(budget, entries.Count - budget);
      bool useOcclusion = UpdateWorldInteractionIconVisibility(entries, width, height);

      foreach (WorldInteractionIconEntry entry in entries) {
        // NPC markers share the exact visibility answer already computed for the nameplate immediately before this
        // pass. Apart from matching SWTOR's behaviour this avoids a second depth query disagreeing by a pixel and
        // suppressing every service icon while the corresponding name is plainly visible.
        if (entry.Owner is WorldNpcPlacement npcOwner) {
          // Reuse the text-nameplate depth answer only when this NPC actually participated in that query. Some
          // conversation actors deliberately have no authored text nameplate/NameplateLocal, yet still own an
          // overhead quest FXSPEC. They must fall back to this pass' own depth sample rather than being interpreted
          // as occluded merely because npcNameplateVisible cannot contain an NPC that was never queried.
          if (npcNameplateHasVisibility && npcNameplateVisibilityQueried.Contains(npcOwner)) {
            if (!npcNameplateVisible.Contains(npcOwner)) continue;
          } else if (useOcclusion && !worldInteractionIconVisible.Contains(entry.Owner)) continue;
        } else if (useOcclusion && !worldInteractionIconVisible.Contains(entry.Owner)) continue;
        // Quest/conversation capability is independent from the primary service identity. A vendor/trainer/taxi can
        // also own cnvConversationId/cnvConversationName; the old mutually-exclusive Kind silently discarded that
        // quest marker. Draw the small bundled SWTOR quest symbol explicitly and, for mixed service actors, place it
        // one icon-height above the service marker so both remain readable. This also avoids the malformed green-box
        // result produced by trying to approximate icon_overhead_questavailable.fxspec through the generic PRT host.
        bool wantsQuestMarker = entry.Interaction.Kind == WorldInteractionKind.MissionBoard || entry.Interaction.HasConversation;
        if (wantsQuestMarker) {
          float questYOffset = (entry.Interaction.Kind == WorldInteractionKind.Conversation || entry.Interaction.Kind == WorldInteractionKind.MissionBoard)
            ? entry.ScreenYOffset : entry.ScreenYOffset - 30f;
          DrawWorldInteractionTextureAt("quest", entry.Anchor, questYOffset, labelViewProj, width, height);
        }

        // Pure conversation/mission-board entries use the stable screen-space quest marker above. Service actors can
        // still render their own authored marker underneath it.
        if (entry.Interaction.Kind == WorldInteractionKind.Conversation || entry.Interaction.Kind == WorldInteractionKind.MissionBoard) continue;

        bool drawn = false;
        bool runtimeHandled = false;
        if (TryWorldInteractionOriginalFxSpec(entry.Interaction, out string fxSpecPath)) {
          // Primary path: the same authored FXSPEC -> PRT runtime model Jedipedia uses. The old static sprite
          // extraction below is now only a compatibility fallback for malformed/legacy specs the structured host
          // cannot own yet (GRANNY/FXSPEC child particle types, unusual beta marshal layouts, etc.).
          runtimeHandled = TryDrawWorldInteractionFxPlayer(entry, fxSpecPath, labelViewProj, s, out bool runtimeDrawn);
          drawn |= runtimeDrawn;
          if (!runtimeHandled) {
            List<WorldOverheadSprite> sprites = ResolveWorldOverheadSprites(fxSpecPath);
            if (sprites != null) foreach (WorldOverheadSprite sprite in sprites.Take(6))
              drawn |= DrawWorldInteractionSpriteAt(sprite, entry.Anchor, entry.ScreenYOffset, labelViewProj, width, height);
          }
        }
        // Older/beta clients can genuinely lack one of the canonical FX resources. Taxi/bindpoint/mailbox interactions
        // still get a visible bundled SWTOR marker in that exceptional case; no generic text/service glyphs are made.
        if (!drawn && !runtimeHandled && TryWorldInteractionBundledFallbackTexture(entry.Interaction, out string textureKey))
          DrawWorldInteractionTextureAt(textureKey, entry.Anchor, entry.ScreenYOffset, labelViewProj, width, height);
      }
    }

    private static bool TryWorldOverheadExpandBounds(GR2 model, Matrix localMatrix, ref bool have,
        ref Vector3 min, ref Vector3 max) {
      GR2_Bounding_Box box = model?.globalBox;
      if (box == null) return false;
      Vector3 lo = new Vector3(box.minX, box.minY, box.minZ);
      Vector3 hi = new Vector3(box.maxX, box.maxY, box.maxZ);
      if (!IsFinite(lo) || !IsFinite(hi)) return false;
      for (int ix = 0; ix < 2; ix++) for (int iy = 0; iy < 2; iy++) for (int iz = 0; iz < 2; iz++) {
        Vector3 point = new Vector3(ix == 0 ? lo.X : hi.X, iy == 0 ? lo.Y : hi.Y, iz == 0 ? lo.Z : hi.Z);
        point = Vector3.TransformCoordinate(point, localMatrix);
        if (!IsFinite(point)) continue;
        if (!have) { min = max = point; have = true; }
        else {
          min.X = Math.Min(min.X, point.X); min.Y = Math.Min(min.Y, point.Y); min.Z = Math.Min(min.Z, point.Z);
          max.X = Math.Max(max.X, point.X); max.Y = Math.Max(max.Y, point.Y); max.Z = Math.Max(max.Z, point.Z);
        }
      }
      return have;
    }

    private static bool TryWorldOverheadTopFrame(WorldSpnPlacement placement, WorldSpnDynState dyn, Matrix world,
        out Matrix frame) {
      frame = Matrix.Identity;
      if (placement == null) return false;
      bool have = false;
      Vector3 min = Vector3.Zero, max = Vector3.Zero;
      if (dyn != null) {
        if (dyn.Hidden) return false;
        foreach (WorldSpnDynPart part in dyn.Parts)
          if (part?.Model != null) TryWorldOverheadExpandBounds(part.Model, part.LocalMatrix, ref have, ref min, ref max);
      } else if (placement.Models != null) {
        foreach (GR2 model in placement.Models)
          if (model != null) TryWorldOverheadExpandBounds(model, Matrix.Identity, ref have, ref min, ref max);
      }
      if (!have) return false;

      // Row-vector equivalent of Jedipedia's synthetic placeable NamePlate frame:
      // X stays X, local Y becomes +Z and local -Z becomes +Y.
      Matrix local = Matrix.Identity;
      local.M11 = 1f; local.M12 = 0f; local.M13 = 0f;
      local.M21 = 0f; local.M22 = 0f; local.M23 = 1f;
      local.M31 = 0f; local.M32 = -1f; local.M33 = 0f;
      local.M41 = (min.X + max.X) * .5f;
      local.M42 = max.Y;
      local.M43 = (min.Z + max.Z) * .5f;
      Matrix.Multiply(ref local, ref world, out frame);
      return NpcFinite(frame);
    }

    private void AddWorldInteractionIconEntry(List<WorldInteractionIconEntry> entries, object owner,
        WorldInteractionInfo interaction, Vector3 anchor, Vector3 testPoint, float screenYOffset, Matrix? fxFrame,
        Matrix labelViewProj, Matrix depthViewProj, int width, int height) {
      if (entries == null || owner == null || interaction == null || !IsFinite(anchor) || !IsFinite(testPoint)) return;
      Vector3 screen = Vector3.Project(anchor, 0f, 0f, width, height, 0f, 1f, labelViewProj);
      if (!IsFinite(screen) || screen.Z < 0f || screen.Z > 1f || screen.X < 0f || screen.X > width || screen.Y < 0f || screen.Y > height) return;
      Vector3 testScreen = Vector3.Project(testPoint, 0f, 0f, width, height, 0f, 1f, depthViewProj);
      if (!IsFinite(testScreen) || testScreen.Z < 0f || testScreen.Z > 1f) return;
      entries.Add(new WorldInteractionIconEntry {
        Owner = owner,
        Interaction = interaction,
        Anchor = anchor,
        FxFrame = fxFrame,
        Screen = screen,
        ScreenYOffset = screenYOffset,
        VisibilitySample = new Vector4(
          Math.Max(0f, Math.Min(1f, testScreen.X / Math.Max(1f, width))),
          Math.Max(0f, Math.Min(1f, testScreen.Y / Math.Max(1f, height))),
          testScreen.Z, 0f),
        Distance = (anchor - camera.Position).LengthSquared()
      });
    }

    private bool UpdateWorldInteractionIconVisibility(List<WorldInteractionIconEntry> entries, int viewportWidth, int viewportHeight) {
      if (entries == null || entries.Count == 0) { worldInteractionIconVisible.Clear(); worldInteractionIconHasVisibility = true; return true; }
      if (sceneDepthShaderResource == null || fx?.NameplateVisibility == null || worldInteractionIconVisibilityFailed) return false;
      int frame = ++worldInteractionIconVisibilityFrame;
      if (worldInteractionIconHasVisibility && frame - worldInteractionIconVisibilityAskedAt < NpcNameplateVisibilityInterval) return true;
      try {
        EnsureNpcNameplateVisibilityResources();
        if (npcNameplateVisibilityTarget == null || npcNameplateVisibilityStaging == null) return false;
        int count = Math.Min(NpcNameplateMaxCount, entries.Count);
        for (int i = 0; i < count; i++) worldInteractionIconVisibilitySamples[i] = entries[i].VisibilitySample;
        ImmediateContext.OutputMerger.SetTargets((DepthStencilView)null, npcNameplateVisibilityTarget);
        ImmediateContext.Rasterizer.SetViewports(Viewport);
        ImmediateContext.ClearRenderTargetView(npcNameplateVisibilityTarget, new Color4(0f, 0f, 0f, 0f));
        ImmediateContext.InputAssembler.InputLayout = null;
        ImmediateContext.InputAssembler.PrimitiveTopology = PrimitiveTopology.PointList;
        fx.SetNameplateVisibility(sceneDepthShaderResource, worldInteractionIconVisibilitySamples, count,
          viewportWidth, viewportHeight, NpcNameplateDepthBias);
        fx.NameplateVisibility.GetPassByIndex(0).Apply(ImmediateContext);
        ImmediateContext.Draw(count, 0);
        fx.ClearNameplateVisibility();
        fx.NameplateVisibility.GetPassByIndex(0).Apply(ImmediateContext);
        ImmediateContext.CopyResource(npcNameplateVisibilityTexture, npcNameplateVisibilityStaging);
        DataBox mapped = ImmediateContext.MapSubresource(npcNameplateVisibilityStaging, 0, 0, MapMode.Read, SlimDX.Direct3D11.MapFlags.None);
        try { mapped.Data.ReadRange(npcNameplateVisibilityReadback, 0, count * 4); }
        finally { ImmediateContext.UnmapSubresource(npcNameplateVisibilityStaging, 0); }
        worldInteractionIconVisible.Clear();
        for (int i = 0; i < count; i++) if (npcNameplateVisibilityReadback[i * 4] > 127) worldInteractionIconVisible.Add(entries[i].Owner);
        worldInteractionIconHasVisibility = true;
        worldInteractionIconVisibilityAskedAt = frame;
        return true;
      } catch (Exception ex) {
        System.Diagnostics.Debug.WriteLine("World interaction icon depth visibility unavailable: " + ex.Message);
        worldInteractionIconVisibilityFailed = true;
        worldInteractionIconVisible.Clear();
        worldInteractionIconHasVisibility = false;
        return false;
      } finally {
        ImmediateContext.OutputMerger.SetTargets((DepthStencilView)null, RenderTargetView);
        ImmediateContext.Rasterizer.SetViewports(Viewport);
        ImmediateContext.InputAssembler.InputLayout = inputLayout;
        ImmediateContext.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
      }
    }

    private bool DrawWorldInteractionTextureAt(string textureKey, Vector3 anchor, float screenYOffset,
        Matrix viewProj, int width, int height) {
      MapNoteIconGpu gpu = EnsureMapNoteIconGpu(textureKey, 6);
      if (gpu?.Texture == null || gpu.Buffer == null) return false;
      Size px = MapNoteIconPixelSize(textureKey);
      return DrawWorldInteractionQuad(gpu, anchor, screenYOffset, viewProj, height, px.Width * 1.25f, px.Height * 1.25f,
        0f, 0f, 1f, 1f);
    }

    private bool DrawWorldInteractionSpriteAt(WorldOverheadSprite sprite, Vector3 anchor, float screenYOffset,
        Matrix viewProj, int width, int height) {
      if (sprite == null || String.IsNullOrWhiteSpace(sprite.TexturePath)) return false;
      MapNoteIconGpu gpu = EnsureWorldOverheadTextureGpu(sprite.TexturePath, 6);
      if (gpu?.Texture == null || gpu.Buffer == null) return false;
      int columns = Math.Max(1, sprite.Columns), rows = Math.Max(1, sprite.Rows);
      int total = Math.Max(1, columns * rows);
      int frame = Math.Max(0, Math.Min(total - 1, sprite.Frame));
      int column = frame % columns, row = frame / columns;
      float u0 = column / (float)columns, v0 = row / (float)rows;
      float u1 = (column + 1) / (float)columns, v1 = (row + 1) / (float)rows;
      return DrawWorldInteractionQuad(gpu, anchor, screenYOffset, viewProj, height,
        Math.Max(8f, sprite.PixelWidth), Math.Max(8f, sprite.PixelHeight), u0, v0, u1, v1);
    }

    private bool DrawWorldInteractionQuad(MapNoteIconGpu gpu, Vector3 anchor, float screenYOffset, Matrix viewProj,
        int height, float pixelWidth, float pixelHeight, float u0, float v0, float u1, float v1) {
      if (gpu?.Texture == null || gpu.Buffer == null || camera == null || height <= 0) return false;
      // FpsCamera stores Look toward the target, but this viewer uses a right-handed view/projection where the
      // visible forward vector is -Look (same convention as world picking/taxi navigation). Using +Look here made
      // every icon that was actually in front of the camera fail the depth test, so even the bundled fallback icons
      // were never drawn.
      Vector3 visibleForward = -camera.Look;
      if (visibleForward.LengthSquared() < .000001f) return false;
      visibleForward.Normalize();
      float depth = Vector3.Dot(anchor - camera.Position, visibleForward);
      if (!(depth > .001f)) return false;
      float worldPerPixel = (float)(2.0 * depth * Math.Tan(camera.FovY * .5f) / Math.Max(1, height));
      Vector3 center = anchor + camera.Up * (-screenYOffset * worldPerPixel);
      Vector3 right = camera.Right * (pixelWidth * worldPerPixel * .5f);
      Vector3 up = camera.Up * (pixelHeight * worldPerPixel * .5f);
      Vector3 tl = center - right + up, tr = center + right + up, br = center + right - up, bl = center - right - up;
      Vector3 normal = -camera.Look, tangent = camera.Right;
      var vertices = new[] {
        new PosNormalTexTan(tl, normal, new Vector2(u0, v0), tangent),
        new PosNormalTexTan(tr, normal, new Vector2(u1, v0), tangent),
        new PosNormalTexTan(br, normal, new Vector2(u1, v1), tangent),
        new PosNormalTexTan(tl, normal, new Vector2(u0, v0), tangent),
        new PosNormalTexTan(br, normal, new Vector2(u1, v1), tangent),
        new PosNormalTexTan(bl, normal, new Vector2(u0, v1), tangent)
      };
      try {
        DataBox mapped = ImmediateContext.MapSubresource(gpu.Buffer, MapMode.WriteDiscard, SlimDX.Direct3D11.MapFlags.None);
        mapped.Data.WriteRange(vertices); ImmediateContext.UnmapSubresource(gpu.Buffer, 0);
        ImmediateContext.InputAssembler.InputLayout = inputLayout;
        ImmediateContext.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
        ImmediateContext.InputAssembler.SetVertexBuffers(0, new VertexBufferBinding(gpu.Buffer, PosNormalTexTan.Stride, 0));
        fx.SetWorld(Matrix.Identity); fx.SetViewProj(viewProj); fx.SetPlaceableBlueGlow(false); fx.SetMapArt(gpu.Texture, 1f);
        fx.MapArt.GetPassByIndex(0).Apply(ImmediateContext); ImmediateContext.Draw(6, 0); fx.ClearMapArt();
        return true;
      } catch (Exception ex) { System.Diagnostics.Debug.WriteLine("World interaction icon draw failed: " + ex.Message); return false; }
    }

    private MapNoteIconGpu EnsureWorldOverheadTextureGpu(string texturePath, int requiredVertices) {
      if (String.IsNullOrWhiteSpace(texturePath) || requiredVertices <= 0 || Device == null || area == null) return null;
      string actualPath = null;
      TorArchive.File textureFile = null;
      foreach (string candidate in WorldOverheadTextureCandidates(texturePath)) {
        try { textureFile = area.FindFile(candidate); } catch { textureFile = null; }
        if (textureFile != null) { actualPath = candidate; break; }
      }
      if (textureFile == null || String.IsNullOrWhiteSpace(actualPath)) return null;
      string key = "world-overhead-fx|" + actualPath.ToLowerInvariant();
      if (!mapNoteIconGpu.TryGetValue(key, out MapNoteIconGpu gpu)) {
        try {
          using (textureFile) {
            using Stream stream = textureFile.OpenCopyInMemory();
            gpu = new MapNoteIconGpu { Key = key, Texture = ShaderResourceView.FromStream(Device, stream, (int)stream.Length) };
            mapNoteIconGpu[key] = gpu;
          }
        } catch (Exception ex) {
          try { textureFile?.Dispose(); } catch { }
          System.Diagnostics.Debug.WriteLine("SWTOR overhead texture load failed " + actualPath + ": " + ex.Message);
          return null;
        }
      } else {
        try { textureFile.Dispose(); } catch { }
      }
      if (gpu.Texture == null) return null;
      if (gpu.Buffer == null || gpu.Capacity < requiredVertices) {
        gpu.Buffer?.Dispose(); gpu.Buffer = null;
        int capacity = 96; while (capacity < requiredVertices && capacity < 65536) capacity *= 2;
        if (capacity < requiredVertices) capacity = requiredVertices;
        var bd = new BufferDescription(PosNormalTexTan.Stride * capacity, ResourceUsage.Dynamic, BindFlags.VertexBuffer,
          CpuAccessFlags.Write, ResourceOptionFlags.None, 0);
        gpu.Buffer = new Buffer(Device, bd) { DebugName = "SWTOR overhead FX " + actualPath };
        gpu.Capacity = capacity;
      }
      return gpu;
    }

    private List<WorldOverheadSprite> ResolveWorldOverheadSprites(string fxSpecPath) {
      if (String.IsNullOrWhiteSpace(fxSpecPath) || area == null) return null;
      if (worldOverheadSpriteCache.TryGetValue(fxSpecPath, out List<WorldOverheadSprite> cached)) return cached;
      var result = new List<WorldOverheadSprite>();
      var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      ResolveWorldOverheadFxResource(fxSpecPath, result, seen, 0);
      // A glow and its core can legitimately share the same sheet/frame; don't draw an accidental duplicate caused by
      // a nested EmitSpec reference, while preserving separate authored layers that use different textures/frames.
      result = result.GroupBy(x => x.TexturePath + "|" + x.Columns + "|" + x.Rows + "|" + x.Frame,
          StringComparer.OrdinalIgnoreCase).Select(x => x.First()).Take(8).ToList();
      // Do not permanently cache an empty resolve. Area rendering starts while archive/model integration is still
      // warming; a first-frame miss is therefore not proof that the client lacks the FXSPEC/PRT. Keeping the empty
      // result forever made every later frame skip a marker that had become resolvable meanwhile.
      if (result.Count > 0) worldOverheadSpriteCache[fxSpecPath] = result;
      return result;
    }

    private void ResolveWorldOverheadFxResource(string path, List<WorldOverheadSprite> result, HashSet<string> seen, int depth) {
      if (depth > 8 || result == null || seen == null || result.Count >= 8) return;
      path = NormalizeWorldOverheadResourcePath(path, null);
      if (String.IsNullOrWhiteSpace(path) || !seen.Add(path)) return;
      if (path.EndsWith(".prt", StringComparison.OrdinalIgnoreCase)) {
        ResolveWorldOverheadPrt(path, result, seen, depth + 1);
        return;
      }
      if (!path.EndsWith(".fxspec", StringComparison.OrdinalIgnoreCase)) return;
      try {
        using TorArchive.File file = area.FindFile(path);
        if (file == null) return;
        using Stream stream = file.OpenCopyInMemory();
        byte[] bytes = new byte[stream.Length];
        int read = 0; while (read < bytes.Length) { int got = stream.Read(bytes, read, bytes.Length - read); if (got <= 0) break; read += got; }
        bool utf16 = bytes.Length >= 2 && ((bytes[0] == 0xFF && bytes[1] == 0xFE) || bytes[1] == 0);
        string xml = (utf16 ? Encoding.Unicode : Encoding.UTF8).GetString(bytes, 0, read).TrimStart('\uFEFF').TrimEnd('\0');
        var resourcesFound = new List<string>();
        var texturesFound = new List<string>();
        try {
          var doc = new XmlDocument(); doc.LoadXml(xml);
          XmlNodeList resources = doc.SelectNodes("//*[@name='_fxResourceName']");
          if (resources != null) foreach (XmlNode node in resources) if (!String.IsNullOrWhiteSpace(node?.InnerText)) resourcesFound.Add(node.InnerText.Trim());
          // A few overhead specs bind the sprite texture directly rather than through a PRT. Keep these original
          // client textures too; this mirrors Jedipedia's resource scanner instead of making the icon disappear.
          XmlNodeList textures = doc.SelectNodes("//*[@name='_fxTextureName' or @name='TextureName' or @name='_fxTexture']");
          if (textures != null) foreach (XmlNode node in textures) if (!String.IsNullOrWhiteSpace(node?.InnerText)) texturesFound.Add(node.InnerText.Trim());
          // Jedipedia does not depend on one marshal field name here: it scans every leaf value for resource paths
          // before interpreting the FXSPEC structure. SWTOR's overhead specs from different releases use several
          // resource field names, so collect every leaf that contains a PRT/FXSPEC/DDS reference as well.
          CollectWorldOverheadFxReferences(doc, resourcesFound, texturesFound);
        } catch (Exception xmlEx) {
          // Shipped FXSPECs include pretty-printed files and a few XML-ish legacy files with a trailing NUL. Jedipedia
          // deliberately scans resource values even when strict DOM parsing fails, so retain that recovery path here.
          System.Diagnostics.Debug.WriteLine("SWTOR overhead FX DOM parse fallback " + path + ": " + xmlEx.Message);
        }
        foreach (Match match in Regex.Matches(xml, @"(?i)([^<>""']+?\.(?:prt|fxspec))(?=\s*<)")) {
          string value = match.Groups[1].Value.Trim(); if (value.Length > 0) resourcesFound.Add(value);
        }
        foreach (Match match in Regex.Matches(xml, @"(?i)([^<>""']+?\.(?:tiny\.)?dds)(?=\s*<)")) {
          string value = match.Groups[1].Value.Trim(); if (value.Length > 0) texturesFound.Add(value);
        }
        // Legacy/Beta FXSPECs are not always strict XML leaves: resource names can live in attributes or marshal
        // fragments. Jedipedia scans the whole payload for archive-looking paths, so do the same as a recovery path.
        // Extension-specific matching keeps this deliberately narrow and avoids treating arbitrary XML text as files.
        foreach (Match match in Regex.Matches(xml, @"(?i)(?:/?resources/|/?art/|/?engine/|[a-z0-9_.-]+/)?[a-z0-9_./\\-]+\.(?:prt|fxspec)")) {
          string value = match.Value.Trim(); if (value.Length > 0) resourcesFound.Add(value);
        }
        foreach (Match match in Regex.Matches(xml, @"(?i)(?:/?resources/|/?art/|/?engine/|[a-z0-9_.-]+/)?[a-z0-9_./\\-]+\.(?:tiny\.)?dds")) {
          string value = match.Value.Trim(); if (value.Length > 0) texturesFound.Add(value);
        }
        foreach (string textureRaw in texturesFound.Distinct(StringComparer.OrdinalIgnoreCase)) {
          string texturePath = NormalizeWorldOverheadTexturePath(textureRaw);
          if (String.IsNullOrWhiteSpace(texturePath) || !WorldOverheadTextureExists(texturePath)) continue;
          Size size = WorldOverheadTexturePixelSize(texturePath, 1, 1);
          result.Add(new WorldOverheadSprite { TexturePath = texturePath, Columns = 1, Rows = 1, Frame = 0, PixelWidth = size.Width, PixelHeight = size.Height });
          if (result.Count >= 8) break;
        }
        foreach (string resourceRaw in resourcesFound.Distinct(StringComparer.OrdinalIgnoreCase)) {
          string resource = resourceRaw.Trim();
          int prtAt = resource.LastIndexOf(".prt", StringComparison.OrdinalIgnoreCase);
          int fxAt = resource.LastIndexOf(".fxspec", StringComparison.OrdinalIgnoreCase);
          string ext = fxAt >= 0 && fxAt > prtAt ? ".fxspec" : prtAt >= 0 ? ".prt" : null;
          if (ext == null) continue;
          int end = resource.LastIndexOf(ext, StringComparison.OrdinalIgnoreCase) + ext.Length;
          if (end > 0 && end < resource.Length) resource = resource.Substring(0, end);
          // Jedipedia intentionally probes BOTH resource-root and particles-root spellings for .prt references.
          // SWTOR's corpus contains both conventions, including paths with and without a slash. Trying the authored
          // spelling first and then the particle fallback is more reliable than rewriting every relative path into
          // art/fx/particles (which is why valid service icons disappeared in the previous build).
          foreach (string child in WorldOverheadResourceCandidates(resource, ext)) {
            ResolveWorldOverheadFxResource(child, result, seen, depth + 1);
            if (result.Count >= 8) break;
          }
          if (result.Count >= 8) break;
        }
      } catch (Exception ex) { System.Diagnostics.Debug.WriteLine("SWTOR overhead FX parse failed " + path + ": " + ex.Message); }
    }

    private static void CollectWorldOverheadFxReferences(XmlNode node, List<string> resources, List<string> textures) {
      if (node == null) return;
      if (node.ChildNodes == null || node.ChildNodes.Count == 0 || node.ChildNodes.Cast<XmlNode>().All(x => x.NodeType != XmlNodeType.Element)) {
        string text = node.InnerText;
        if (!String.IsNullOrWhiteSpace(text)) {
          string value = text.Trim().Trim('"');
          if (value.IndexOf(".prt", StringComparison.OrdinalIgnoreCase) >= 0 || value.IndexOf(".fxspec", StringComparison.OrdinalIgnoreCase) >= 0)
            resources?.Add(value);
          if (value.IndexOf(".dds", StringComparison.OrdinalIgnoreCase) >= 0) textures?.Add(value);
        }
      }
      foreach (XmlNode child in node.ChildNodes) if (child.NodeType == XmlNodeType.Element) CollectWorldOverheadFxReferences(child, resources, textures);
    }

    private void ResolveWorldOverheadPrt(string path, List<WorldOverheadSprite> result, HashSet<string> seen, int depth) {
      string[] candidates = WorldOverheadPrtCandidates(path).ToArray();
      foreach (string candidate in candidates) {
        try {
          using TorArchive.File file = area.FindFile(candidate);
          if (file == null) continue;
          int before = result.Count;
          var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
          string fullText;
          using (Stream stream = file.OpenCopyInMemory()) using (var reader = new StreamReader(stream, true)) fullText = reader.ReadToEnd();
          using (var reader = new StringReader(fullText ?? String.Empty)) {
            string line;
            while ((line = reader.ReadLine()) != null) {
              line = line.Trim(); if (line.Length == 0) continue;
              int equals = line.IndexOf('='); if (equals <= 0) continue;
              string key = line.Substring(0, equals).Trim().TrimStart('.');
              string value = line.Substring(equals + 1).Trim().Trim('"');
              if (key.Length > 0) parameters[key] = value;
            }
          }
          if (parameters.TryGetValue("TextureName", out string textureName)) {
            string texturePath = NormalizeWorldOverheadTexturePath(textureName);
            if (!String.IsNullOrWhiteSpace(texturePath) && WorldOverheadTextureExists(texturePath)) {
              int columns = WorldOverheadPositiveInt(parameters, "RowSize", 1);
              int rows = WorldOverheadPositiveInt(parameters, "ColumnSize", 1);
              int frame = WorldOverheadNonNegativeInt(parameters, "StartFrame", 0);
              Size size = WorldOverheadTexturePixelSize(texturePath, columns, rows);
              result.Add(new WorldOverheadSprite {
                TexturePath = texturePath, Columns = columns, Rows = rows, Frame = frame,
                PixelWidth = size.Width, PixelHeight = size.Height
              });
            }
          }

          // PRT wrappers are common in the overhead-icon chain. Jedipedia deliberately scans every `.Name=...prt`
          // reference, not only EmitSpec/EmitAtDeathSpec, because a wrapper can carry no TextureName of its own.
          // Scan all parameter values and the raw text so old files whose key parser differs still reach the child.
          var nestedResources = new List<string>();
          foreach (string value in parameters.Values) if (!String.IsNullOrWhiteSpace(value) &&
              (value.IndexOf(".prt", StringComparison.OrdinalIgnoreCase) >= 0 || value.IndexOf(".fxspec", StringComparison.OrdinalIgnoreCase) >= 0))
            nestedResources.Add(value);
          foreach (string nestedRaw in nestedResources.Distinct(StringComparer.OrdinalIgnoreCase)) {
            string nested = nestedRaw.Trim().Trim('"');
            int fxAt = nested.LastIndexOf(".fxspec", StringComparison.OrdinalIgnoreCase);
            int prtAt = nested.LastIndexOf(".prt", StringComparison.OrdinalIgnoreCase);
            string ext = fxAt >= 0 && fxAt > prtAt ? ".fxspec" : prtAt >= 0 ? ".prt" : null;
            if (ext == null) continue;
            int resourceEnd = nested.LastIndexOf(ext, StringComparison.OrdinalIgnoreCase) + ext.Length;
            if (resourceEnd > 0 && resourceEnd < nested.Length) nested = nested.Substring(0, resourceEnd);
            foreach (string child in WorldOverheadResourceCandidates(nested, ext)) {
              ResolveWorldOverheadFxResource(child, result, seen, depth + 1);
              if (result.Count >= 8) break;
            }
            if (result.Count >= 8) break;
          }
          if (result.Count > before || result.Count >= 8) return;
          // If this was a wrapper whose child was missing, keep trying the paired foo.prt/foo_p.prt candidate.
        } catch (Exception ex) { System.Diagnostics.Debug.WriteLine("SWTOR overhead PRT parse failed " + candidate + ": " + ex.Message); }
      }
    }

    private IEnumerable<string> WorldOverheadPrtCandidates(string path) {
      var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      foreach (string basePath in WorldOverheadResourceCandidates(path, ".prt")) {
        if (String.IsNullOrWhiteSpace(basePath)) continue;
        if (seen.Add(basePath)) yield return basePath;
        string alternate = basePath.EndsWith("_p.prt", StringComparison.OrdinalIgnoreCase)
          ? basePath.Substring(0, basePath.Length - 6) + ".prt"
          : basePath.EndsWith(".prt", StringComparison.OrdinalIgnoreCase) ? basePath.Substring(0, basePath.Length - 4) + "_p.prt" : null;
        if (!String.IsNullOrWhiteSpace(alternate) && seen.Add(alternate)) yield return alternate;
      }
    }

    private static IEnumerable<string> WorldOverheadResourceCandidates(string resource, string extension) {
      if (String.IsNullOrWhiteSpace(resource)) yield break;
      string path = resource.Trim().Trim('"').Replace('\\', '/');
      while (path.Contains("//")) path = path.Replace("//", "/");
      path = path.TrimStart('/');
      if (!String.IsNullOrWhiteSpace(extension) && !path.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) {
        int extAt = path.IndexOf(extension, StringComparison.OrdinalIgnoreCase);
        if (extAt >= 0) path = path.Substring(0, extAt + extension.Length); else path += extension;
      }
      if (String.IsNullOrWhiteSpace(path)) yield break;
      string lower = path.ToLowerInvariant();
      if (lower.StartsWith("resources/", StringComparison.OrdinalIgnoreCase)) { yield return "/" + lower; yield break; }
      if (lower.StartsWith("art/", StringComparison.OrdinalIgnoreCase) || lower.StartsWith("engine/", StringComparison.OrdinalIgnoreCase)) {
        yield return "/resources/" + lower; yield break;
      }
      if (lower.EndsWith(".fxspec", StringComparison.OrdinalIgnoreCase)) {
        // EmitFXSpec bare/relative names live below art/fx/fxspec; also retain the authored resource-root spelling
        // because old/beta specs occasionally include a directory that is already resource-relative.
        yield return "/resources/art/fx/fxspec/" + lower;
        yield return "/resources/" + lower;
        yield break;
      }
      if (lower.EndsWith(".prt", StringComparison.OrdinalIgnoreCase)) {
        // Match fxspec-read.js: test the literal /resources path and art/fx/particles. For a bare filename prefer
        // particles first (the runtime convention); for a path containing a slash prefer the literal spelling.
        if (lower.IndexOf('/') < 0) {
          yield return "/resources/art/fx/particles/" + lower;
          yield return "/resources/art/fx/particles/_testtrash/" + lower;
          yield return "/resources/" + lower;
        } else {
          yield return "/resources/" + lower;
          yield return "/resources/art/fx/particles/" + lower;
          string leaf = lower.Substring(lower.LastIndexOf('/') + 1);
          yield return "/resources/art/fx/particles/_testtrash/" + leaf;
        }
        yield break;
      }
      yield return "/resources/" + lower;
    }

    private static string NormalizeWorldOverheadResourcePath(string resource, string extension) {
      if (String.IsNullOrWhiteSpace(resource)) return null;
      string path = resource.Trim().Trim('"').Replace('\\', '/');
      while (path.Contains("//")) path = path.Replace("//", "/");
      path = path.TrimStart('/');
      if (!String.IsNullOrWhiteSpace(extension) && !path.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) {
        int extAt = path.IndexOf(extension, StringComparison.OrdinalIgnoreCase);
        if (extAt >= 0) path = path.Substring(0, extAt + extension.Length);
        else path += extension;
      }
      if (path.StartsWith("resources/", StringComparison.OrdinalIgnoreCase)) return "/" + path.ToLowerInvariant();
      if (path.StartsWith("art/", StringComparison.OrdinalIgnoreCase) || path.StartsWith("engine/", StringComparison.OrdinalIgnoreCase))
        return "/resources/" + path.ToLowerInvariant();
      // Single-path normalization is only used for already-resolved/canonical callers. Relative PRT/FXSPEC references
      // go through WorldOverheadResourceCandidates so both archive conventions are tested instead of guessed.
      return "/resources/" + path.ToLowerInvariant();
    }

    private static string NormalizeWorldOverheadTexturePath(string texture) {
      if (String.IsNullOrWhiteSpace(texture)) return null;
      string path = texture.Trim().Trim('"').Replace('\\', '/');
      if (!path.EndsWith(".dds", StringComparison.OrdinalIgnoreCase)) path += ".dds";
      path = path.TrimStart('/');
      if (path.StartsWith("resources/", StringComparison.OrdinalIgnoreCase)) return "/" + path.ToLowerInvariant();
      if (path.StartsWith("art/", StringComparison.OrdinalIgnoreCase) || path.StartsWith("engine/", StringComparison.OrdinalIgnoreCase)) return "/resources/" + path.ToLowerInvariant();
      return "/resources/" + path.ToLowerInvariant();
    }

    private bool WorldOverheadTextureExists(string texturePath) {
      if (area == null) return false;
      foreach (string candidate in WorldOverheadTextureCandidates(texturePath)) {
        try { using TorArchive.File file = area.FindFile(candidate); if (file != null) return true; } catch { }
      }
      return false;
    }

    private static IEnumerable<string> WorldOverheadTextureCandidates(string texturePath) {
      string path = NormalizeWorldOverheadTexturePath(texturePath);
      if (String.IsNullOrWhiteSpace(path)) yield break;
      var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      if (seen.Add(path)) yield return path;
      // SWTOR archives may carry only the tiny streaming proxy or only the full DDS depending on client generation.
      // Jedipedia's DDS resolver probes both rather than rewriting the authored name unconditionally.
      string alternate = path.EndsWith(".tiny.dds", StringComparison.OrdinalIgnoreCase)
        ? path.Substring(0, path.Length - ".tiny.dds".Length) + ".dds"
        : path.EndsWith(".dds", StringComparison.OrdinalIgnoreCase)
          ? path.Substring(0, path.Length - ".dds".Length) + ".tiny.dds" : null;
      if (!String.IsNullOrWhiteSpace(alternate) && seen.Add(alternate)) yield return alternate;
    }

    private Size WorldOverheadTexturePixelSize(string texturePath, int columns, int rows) {
      const float maxPixels = 28f;
      foreach (string candidate in WorldOverheadTextureCandidates(texturePath)) {
        try {
          using TorArchive.File file = area.FindFile(candidate);
          if (file == null) continue;
          using Stream stream = file.OpenCopyInMemory();
          using var reader = new BinaryReader(stream);
          if (stream.Length < 20 || reader.ReadUInt32() != 0x20534444) continue;
          stream.Position = 12; uint height = reader.ReadUInt32(), width = reader.ReadUInt32();
          float cellWidth = Math.Max(1f, width / (float)Math.Max(1, columns));
          float cellHeight = Math.Max(1f, height / (float)Math.Max(1, rows));
          float scale = maxPixels / Math.Max(cellWidth, cellHeight);
          return new Size(Math.Max(8, (int)Math.Round(cellWidth * scale)), Math.Max(8, (int)Math.Round(cellHeight * scale)));
        } catch { }
      }
      return new Size((int)maxPixels, (int)maxPixels);
    }

    private static int WorldOverheadPositiveInt(Dictionary<string, string> parameters, string key, int fallback) {
      if (parameters != null && parameters.TryGetValue(key, out string raw) &&
          Double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && value > 0)
        return Math.Max(1, (int)Math.Round(value));
      return Math.Max(1, fallback);
    }

    private static int WorldOverheadNonNegativeInt(Dictionary<string, string> parameters, string key, int fallback) {
      if (parameters != null && parameters.TryGetValue(key, out string raw) &&
          Double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && value >= 0)
        return Math.Max(0, (int)Math.Floor(value));
      return Math.Max(0, fallback);
    }

    private static bool TryWorldInteractionOriginalFxSpec(WorldInteractionInfo interaction, out string path) {
      path = null; if (interaction == null) return false;
      switch (interaction.Kind) {
        case WorldInteractionKind.Vendor: path = "/resources/art/fx/fxspec/overhead_icons/vendor_01.fxspec"; return true;
        case WorldInteractionKind.Taxi: path = "/resources/art/fx/fxspec/overhead_icons/icon_taxi_discovered.fxspec"; return true;
        case WorldInteractionKind.Conversation:
        case WorldInteractionKind.MissionBoard: path = "/resources/art/fx/fxspec/worlddesign/quests/icon_overhead_questavailable.fxspec"; return true;
        case WorldInteractionKind.QuickTravel: path = "/resources/art/fx/fxspec/worlddesign/quests/icon_overhead_bindpoint.fxspec"; return true;
        case WorldInteractionKind.ProfessionTrainer: path = "/resources/art/fx/fxspec/overhead_icons/icon_class_trainer_crafting.fxspec"; return true;
        case WorldInteractionKind.ClassTrainer: path = "/resources/art/fx/fxspec/overhead_icons/icon_class_trainer.fxspec"; return true;
        default: return false;
      }
    }

    private static bool TryWorldInteractionBundledFallbackTexture(WorldInteractionInfo interaction, out string textureKey) {
      textureKey = null; if (interaction == null) return false;
      switch (interaction.Kind) {
        case WorldInteractionKind.Conversation:
        case WorldInteractionKind.MissionBoard: textureKey = "quest"; return true;
        case WorldInteractionKind.Taxi: textureKey = "taxi"; return true;
        case WorldInteractionKind.QuickTravel: textureKey = "bindpoint"; return true;
        // Mailboxes expose their service through plcUtilityType=9. Jedipedia renders the authored envelope effect from
        // the mailbox dyn row; PugTools does not yet preserve dyn FXSPEC rows, so use the bundled SWTOR mailbox
        // map-note artwork as the world-space overhead marker. It follows the same visibility/occlusion rules as the
        // other interaction icons above and therefore disappears behind walls together with the placeable.
        case WorldInteractionKind.Mailbox: textureKey = "mailbox"; return true;
        default: return false;
      }
    }
  }
}
