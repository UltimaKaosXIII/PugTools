using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using FileFormats;
using SlimDX;
using SlimDX.Direct3D11;
using SlimDXNet.Vertex;

namespace PugTools {
  internal sealed partial class View_AREA {
    private sealed class WorldQuickTravelTerminal {
      public WorldSpnPlacement Placement;
      public ulong InstanceId;
      public string Label;
      public Vector3 Position;
      public bool IsSource;
    }

    private readonly object quickTravelMapSync = new object();
    private readonly List<WorldQuickTravelTerminal> quickTravelMapTerminals = new List<WorldQuickTravelTerminal>();
    private volatile bool quickTravelMapActive;
    private string quickTravelMapSourceLabel = String.Empty;

    public bool IsQuickTravelMapActive => quickTravelMapActive;

    /// <summary>
    /// Opens the SWTOR bindpoint map for the transient plain-click interaction target. The game uses the exact
    /// plcAbilitySpecOnUse bindpoint ability; WorldSpnPlacement.IsQuickTravel caches that classification when the
    /// spawner is built, so the clickable object and the destination list cannot disagree.
    /// </summary>
    public bool TryOpenQuickTravelMapForSelectedSpawn() {
      WorldSpnPlacement source = interactionWorldSpnPlacement;
      if (source?.IsQuickTravel != true || source.Instance == null) return false;

      List<WorldQuickTravelTerminal> terminals = BuildQuickTravelTerminals(source);
      if (terminals.Count == 0) return false;

      CloseTaxiRouteMapState();
      lock (quickTravelMapSync) {
        quickTravelMapTerminals.Clear();
        quickTravelMapTerminals.AddRange(terminals);
        quickTravelMapSourceLabel = String.IsNullOrWhiteSpace(source.Name) ? PrettyQuickTravelName(source.SourceFqn) : source.Name;
        quickTravelMapActive = true;
      }

      SetMapOpen(true);
      FitQuickTravelMap();
      if (Window is WorldBrowser browser) {
        browser.SetFullMapActive(true);
        browser.SetStatusLabel("Quick travel — " + quickTravelMapSourceLabel + ": click a bindpoint destination; drag = pan, wheel = zoom, Esc/M = close");
      }
      return true;
    }

    private List<WorldQuickTravelTerminal> BuildQuickTravelTerminals(WorldSpnPlacement source) {
      var result = new List<WorldQuickTravelTerminal>();
      WorldSpnPlacement[] placements;
      try { placements = spnPlacements?.ToArray() ?? Array.Empty<WorldSpnPlacement>(); }
      catch { placements = Array.Empty<WorldSpnPlacement>(); }
      WorldRenderSettings settings = SettingsSnapshot();
      ulong sourceId = source?.Instance?.ID ?? 0;

      foreach (WorldSpnPlacement spn in placements) {
        if (spn?.IsQuickTravel != true || spn.Instance == null || spn.Room == null) continue;
        // Destination enumeration intentionally ignores camera distance/dPVS, but it must still respect authored/world
        // activity. A hidden or path-follower-pending placement is not a usable terminal in the SWTOR client either.
        if (spn.Instance.hidden || spn.Instance.PathFollowerPending) continue;
        if (!SpnVariantActive(spn.VariantIndex, spn.VariantCount, spn.SpawnPoints)) continue;
        WorldSpnDynState dyn = ActiveSpnDynState(spn, settings.AnimateSpnObjects);
        if (dyn != null && dyn.Hidden) continue;
        try {
          Matrix world = SpnPlacementWorld(spn, settings.AnimateSpnObjects);
          Vector3 position = new Vector3(world.M41, world.M42, world.M43);
          if (!IsFinite(position)) continue;
          string label = String.IsNullOrWhiteSpace(spn.Name) ? PrettyQuickTravelName(spn.SourceFqn) : spn.Name.Trim();
          result.Add(new WorldQuickTravelTerminal {
            Placement = spn,
            InstanceId = spn.Instance.ID,
            Label = label,
            Position = position,
            IsSource = spn.Instance.ID == sourceId && ReferenceEquals(spn.Room, source.Room)
          });
        } catch { }
      }

      // Effect-only legacy bindpoints can briefly miss the placement snapshot during a population turnover. Keep the
      // terminal that was actually clicked so the interaction never opens an empty map just because that frame changed.
      if (!result.Any(x => x.IsSource)) {
        try {
          Matrix world = SpnPlacementWorld(source, settings.AnimateSpnObjects);
          Vector3 position = new Vector3(world.M41, world.M42, world.M43);
          if (IsFinite(position)) result.Add(new WorldQuickTravelTerminal {
            Placement = source,
            InstanceId = source.Instance.ID,
            Label = String.IsNullOrWhiteSpace(source.Name) ? PrettyQuickTravelName(source.SourceFqn) : source.Name.Trim(),
            Position = position,
            IsSource = true
          });
        } catch { }
      }

      // One active bindpoint should appear once even when a legacy SPN table repeats the same entity reference.
      return result
        .GroupBy(x => x.InstanceId.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" +
          x.Position.X.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "|" +
          x.Position.Y.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "|" +
          x.Position.Z.ToString("R", System.Globalization.CultureInfo.InvariantCulture))
        .Select(g => g.OrderByDescending(x => x.IsSource).First())
        .OrderBy(x => x.Label, StringComparer.CurrentCultureIgnoreCase)
        .ThenBy(x => x.InstanceId)
        .ToList();
    }

    private static string PrettyQuickTravelName(string fqn) {
      if (String.IsNullOrWhiteSpace(fqn)) return "Quick travel point";
      string leaf = fqn.Trim().Split('.').LastOrDefault() ?? fqn;
      leaf = leaf.Replace('_', ' ').Trim();
      return leaf.Length == 0 ? "Quick travel point" : System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(leaf.ToLowerInvariant());
    }

    private void FitQuickTravelMap() {
      if (!quickTravelMapActive || !mapOpen) return;
      List<WorldQuickTravelTerminal> terminals;
      lock (quickTravelMapSync) terminals = quickTravelMapTerminals.ToList();
      if (terminals.Count == 0) return;
      float minX = terminals.Min(x => x.Position.X), maxX = terminals.Max(x => x.Position.X);
      float minZ = terminals.Min(x => x.Position.Z), maxZ = terminals.Max(x => x.Position.Z);
      mapCenter = new Vector2((minX + maxX) * .5f, (minZ + maxZ) * .5f);
      UpdateMapCamera();
      float aspect = Math.Max(.1f, ClientWidth / (float)Math.Max(1, ClientHeight));
      float desired = Math.Max(3f, Math.Max((maxZ - minZ) * 1.35f, (maxX - minX) * 1.35f / aspect));
      mapZoom = Math.Max(MapMinZoom, Math.Min(1f, desired / Math.Max(.001f, mapBaseHeight)));
      UpdateMapCamera();
    }

    private void CloseQuickTravelMapState() {
      lock (quickTravelMapSync) {
        if (!quickTravelMapActive && quickTravelMapTerminals.Count == 0) return;
        quickTravelMapActive = false;
        quickTravelMapSourceLabel = String.Empty;
        quickTravelMapTerminals.Clear();
      }
    }

    private WorldQuickTravelTerminal QuickTravelTerminalAtMapPoint(Point point) {
      if (!quickTravelMapActive || !mapOpen) return null;
      List<WorldQuickTravelTerminal> terminals;
      lock (quickTravelMapSync) terminals = quickTravelMapTerminals.ToList();
      if (terminals.Count == 0) return null;
      PointF mouse = new PointF(point.X, point.Y);
      WorldQuickTravelTerminal bestTerminal = null;
      float best = 18f * 18f;
      foreach (WorldQuickTravelTerminal terminal in terminals) {
        PointF p = TaxiMapScreenPoint(terminal.Position);
        float dx = p.X - mouse.X, dy = p.Y - mouse.Y, d2 = dx * dx + dy * dy;
        if (d2 <= best) { best = d2; bestTerminal = terminal; }
      }
      return bestTerminal;
    }

    private void TeleportQuickTravel(WorldQuickTravelTerminal terminal) {
      if (terminal == null || camera == null) return;
      Vector3 destination = terminal.Position + new Vector3(0f, WalkingEyeHeight, 0f);
      if (!IsFinite(destination)) return;
      CloseQuickTravelMapState();
      mapOpen = false; mapPointerDown = false; mapPointerDragged = false;
      camera.Position = destination;
      currentCameraRoom = FindCameraRoom(camera.Position); displayCameraRoom = currentCameraRoom;
      walkingVerticalVelocity = 0f; walkingGrounded = false; ClearWalkingPlatform();
      InvalidateTemporalHistory(); InvalidateObjectOcclusionVisibility();
      if (Window is WorldBrowser browser) {
        browser.SetFullMapActive(false);
        browser.SetStatusLabel("Quick travel: " + (String.IsNullOrWhiteSpace(terminal.Label) ? "Bindpoint" : terminal.Label));
      }
    }

    private void DrawQuickTravelMapOverlay(Matrix vp, WorldRenderSettings s) {
      if (!quickTravelMapActive || !mapOpen || s == null || s.Mode != WorldRenderMode.Map || ClientWidth <= 0 || ClientHeight <= 0) return;
      List<WorldQuickTravelTerminal> terminals;
      lock (quickTravelMapSync) terminals = quickTravelMapTerminals.ToList();
      if (terminals.Count == 0) return;

      float worldPerPixelX = Math.Max(.00001f, mapVisibleWidth / Math.Max(1f, ClientWidth));
      float worldPerPixelZ = Math.Max(.00001f, mapVisibleHeight / Math.Max(1f, ClientHeight));
      const float iconPixels = 32f;
      float hx = iconPixels * worldPerPixelX * .5f, hz = iconPixels * worldPerPixelZ * .5f;
      float y = boundsMax.Y + 4.5f;
      float marginX = iconPixels * worldPerPixelX, marginZ = iconPixels * worldPerPixelZ;
      float minX = mapCenter.X - mapVisibleWidth * .5f - marginX, maxX = mapCenter.X + mapVisibleWidth * .5f + marginX;
      float minZ = mapCenter.Y - mapVisibleHeight * .5f - marginZ, maxZ = mapCenter.Y + mapVisibleHeight * .5f + marginZ;
      terminals = terminals.Where(t => t.Position.X >= minX && t.Position.X <= maxX && t.Position.Z >= minZ && t.Position.Z <= maxZ).ToList();
      if (terminals.Count == 0) return;

      MapNoteIconGpu gpu = EnsureMapNoteIconGpu("bindpoint", checked(terminals.Count * 6));
      if (gpu?.Buffer == null || gpu.Texture == null) return;
      var normal = new Vector3(0f, 1f, 0f); var tangent = new Vector3(1f, 0f, 0f);
      var vertices = new PosNormalTexTan[terminals.Count * 6]; int o = 0;
      // Same half-turn as authored mapnote sprites; the bindpoint art itself remains upright in screen space.
      float c = -1f, sn = 0f;
      Vector2 tl = RotateMapIconOffset(-hx, hz, c, sn), tr = RotateMapIconOffset(hx, hz, c, sn);
      Vector2 br = RotateMapIconOffset(hx, -hz, c, sn), bl = RotateMapIconOffset(-hx, -hz, c, sn);
      foreach (WorldQuickTravelTerminal terminal in terminals) {
        Vector3 p = terminal.Position; p.Y = y;
        vertices[o++] = new PosNormalTexTan(new Vector3(p.X + tl.X, y, p.Z + tl.Y), normal, new Vector2(0, 0), tangent);
        vertices[o++] = new PosNormalTexTan(new Vector3(p.X + tr.X, y, p.Z + tr.Y), normal, new Vector2(1, 0), tangent);
        vertices[o++] = new PosNormalTexTan(new Vector3(p.X + br.X, y, p.Z + br.Y), normal, new Vector2(1, 1), tangent);
        vertices[o++] = new PosNormalTexTan(new Vector3(p.X + tl.X, y, p.Z + tl.Y), normal, new Vector2(0, 0), tangent);
        vertices[o++] = new PosNormalTexTan(new Vector3(p.X + br.X, y, p.Z + br.Y), normal, new Vector2(1, 1), tangent);
        vertices[o++] = new PosNormalTexTan(new Vector3(p.X + bl.X, y, p.Z + bl.Y), normal, new Vector2(0, 1), tangent);
      }

      try {
        DataBox mapped = ImmediateContext.MapSubresource(gpu.Buffer, MapMode.WriteDiscard, SlimDX.Direct3D11.MapFlags.None);
        mapped.Data.WriteRange(vertices); ImmediateContext.UnmapSubresource(gpu.Buffer, 0);
        ImmediateContext.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
        fx.SetWorld(Matrix.Identity); fx.SetViewProj(vp); fx.SetMapArt(gpu.Texture, 1f);
        ImmediateContext.InputAssembler.SetVertexBuffers(0, new VertexBufferBinding(gpu.Buffer, PosNormalTexTan.Stride, 0));
        fx.MapArt.GetPassByIndex(0).Apply(ImmediateContext); ImmediateContext.Draw(vertices.Length, 0); fx.ClearMapArt();
      } catch (Exception ex) { System.Diagnostics.Debug.WriteLine("Quick-travel map draw failed: " + ex.Message); }
    }
  }
}
