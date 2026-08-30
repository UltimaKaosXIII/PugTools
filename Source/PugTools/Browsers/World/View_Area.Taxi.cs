using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using FileFormats;
using SlimDX;
using SlimDXNet;
using SlimDXNet.Vertex;
using System.Windows.Forms;

namespace PugTools {
  internal sealed partial class View_AREA {
    private const float TaxiDefaultEyeHeight = .18f;
    private readonly object taxiRideSync = new object();
    private List<WorldTaxiRouteInfo> taxiPendingJourney;
    private string taxiPendingLabel;
    private bool taxiPendingStart;
    private bool taxiPendingStop;

    private SpnMotionRoute taxiRideRoute;
    private readonly Queue<WorldTaxiRouteInfo> taxiRideRemainingLegs = new Queue<WorldTaxiRouteInfo>();
    private int taxiRideLegIndex;
    private int taxiRideLegCount;
    private bool taxiRideThroughJourney;
    private string taxiRideCurrentLegLabel = String.Empty;
    private float taxiRideElapsed;
    private float taxiRideSpeedMultiplier = 1f;
    private bool taxiRidePaused;
    private bool taxiSpaceKeyWasDown;
    private bool taxiEscapeKeyWasDown;
    private volatile bool taxiRideActive;
    private volatile string taxiRideLabel = String.Empty;

    private GR2 taxiRideVehicleModel;
    private float taxiRideVehicleScale = 1f;
    private string taxiRideVehicleModelPath;
    private Matrix taxiRideVehicleWorld = Matrix.Identity;
    private bool taxiRideVehicleVisible;
    private bool taxiRideCameraInitialized;
    private Vector3 taxiRideDirection = Vector3.UnitZ;

    // In-game-style taxi route picker layered on top of the existing interactive M map. The WinForms click only
    // publishes immutable route metadata here; GPU line buffers are rebuilt lazily on the render thread.
    private readonly object taxiRouteMapSync = new object();
    private readonly List<WorldTaxiRouteInfo> taxiRouteMapRoutes = new List<WorldTaxiRouteInfo>();
    private readonly List<LineGpu> taxiRouteMapGpu = new List<LineGpu>();
    private volatile bool taxiRouteMapActive;
    private volatile bool taxiRouteMapGpuDirty;
    private string taxiRouteMapSourceLabel = String.Empty;

    public bool IsTaxiRideActive => taxiRideActive || taxiPendingStart;
    public bool IsTaxiRouteMapActive => taxiRouteMapActive;
    // Service actions use only the transient plain-click target. Ctrl+click inspection deliberately lives in the
    // selectedWorld* fields and must never be re-used as an interaction target on a later click somewhere else.
    public string SelectedWorldSpawnFqn => interactionWorldSpnPlacement?.SourceFqn ?? interactionWorldNpcPlacement?.SourceFqn ?? String.Empty;
    public string SelectedWorldSpawnName => interactionWorldSpnPlacement?.Name ?? interactionWorldNpcPlacement?.Name ?? String.Empty;
    public bool TryGetSelectedWorldSpawnPosition(out float x, out float y, out float z) {
      x = y = z = 0f;
      try {
        Matrix world;
        WorldSpnPlacement spn = interactionWorldSpnPlacement;
        WorldNpcPlacement npc = interactionWorldNpcPlacement;
        if (spn != null) world = SpnPlacementWorld(spn, SettingsSnapshot().AnimateSpnObjects);
        else if (npc != null) world = NpcNameplateWorld(npc);
        else return false;
        x = world.M41; y = world.M42; z = world.M43;
        return Single.IsFinite(x) && Single.IsFinite(y) && Single.IsFinite(z);
      } catch { return false; }
    }
    public string CurrentTaxiRideLabel => taxiRideLabel ?? String.Empty;

    public void StartTaxiRide(WorldTaxiRouteInfo route) {
      if (route == null) return;
      List<WorldTaxiRouteInfo> legs = TaxiRideLegs(route)
        .Where(x => x?.Path?.Points != null && x.Path.Points.Count >= 2)
        .ToList();
      if (legs.Count == 0) return;
      lock (taxiRideSync) {
        taxiPendingJourney = legs;
        taxiPendingLabel = route.Label ?? route.PathFqn ?? legs[0].Path.Name ?? "Taxi route";
        taxiPendingStop = false;
        taxiPendingStart = true;
        taxiRideLabel = taxiPendingLabel;
      }
    }

    public void StopTaxiRide() {
      lock (taxiRideSync) {
        taxiPendingStart = false;
        taxiPendingJourney = null;
        taxiPendingLabel = null;
        taxiPendingStop = true;
      }
    }

    /// <summary>
    /// Called from UpdateScene after map handling. Taxi mode owns translation along the authored spline, but no longer
    /// owns camera orientation after the initial facing. Mouse look and the normal A/D/J/L + I/K look keys therefore
    /// remain usable throughout the ride. FpsCamera is RH and the viewer's visible forward vector is -Look, so the
    /// initial LookAt target deliberately points opposite the path tangent; the previous version used +tangent and
    /// consequently made every taxi ride look backwards.
    /// </summary>
    private bool UpdateTaxiRide(float dt, bool escapeKey) {
      ConsumePendingTaxiRide();
      if (!taxiRideActive || taxiRideRoute == null) {
        taxiEscapeKeyWasDown = escapeKey;
        return false;
      }

      if (escapeKey && !taxiEscapeKeyWasDown) {
        EndTaxiRide("Taxi ride stopped.");
        taxiEscapeKeyWasDown = true;
        return true;
      }
      taxiEscapeKeyWasDown = escapeKey;

      bool space = Util.IsKeyDown(Keys.Space);
      if (space && !taxiSpaceKeyWasDown) {
        taxiRidePaused = !taxiRidePaused;
        if (Window is WorldBrowser browser)
          browser.SetStatusLabel((taxiRidePaused ? "Taxi paused: " : "Taxi resumed: ") + taxiRideLabel +
            "  •  free look, wheel = " + taxiRideSpeedMultiplier.ToString("0.##") + "x, Esc = stop");
      }
      taxiSpaceKeyWasDown = space;

      if (!taxiRidePaused) taxiRideElapsed += Math.Max(0f, dt) * taxiRideSpeedMultiplier;
      float duration = taxiRideRoute.LegDuration;
      if (!(duration > 0f) || Single.IsNaN(duration) || Single.IsInfinity(duration)) {
        EndTaxiRide("Taxi route has no usable travel duration.");
        return true;
      }

      bool atEnd = taxiRideElapsed >= duration;
      float time = Math.Max(0f, Math.Min(duration, taxiRideElapsed));
      Vector3 position = SpnRouteSample(taxiRideRoute, time);
      Vector3 direction = TaxiRouteDirection(taxiRideRoute, time, duration);
      Vector3 previousRouteDirection = taxiRideDirection;

      // Keep the vehicle itself on the authored spline. If taxVehicleSpec could be resolved, move the camera to a
      // conservative third-person anchor behind it; otherwise retain the original eye-on-spline behaviour. Preserve
      // the user's yaw/pitch OFFSET from the previous route tangent, then apply that offset to the new tangent. This
      // gives the useful combination of automatic forward-follow through bends and unrestricted mouse/key free-look.
      WorldRenderSettings rideSettings = SettingsSnapshot();
      bool showVehicle = rideSettings?.ShowTaxiVehicle != false;
      taxiRideVehicleWorld = TaxiVehicleWorld(position, direction, taxiRideVehicleScale);
      taxiRideVehicleVisible = showVehicle && taxiRideVehicleModel != null;
      Vector3 cameraAnchor = TaxiCameraAnchor(position, direction, showVehicle);
      Vector3 visibleLook = direction;
      if (taxiRideCameraInitialized) {
        Vector3 currentVisible = -camera.Look;
        if (!IsFinite(currentVisible) || currentVisible.LengthSquared() < .000001f) currentVisible = previousRouteDirection;
        else currentVisible.Normalize();
        float yawOffset = WrapTaxiAngle(TaxiYaw(currentVisible) - TaxiYaw(previousRouteDirection));
        float pitchOffset = TaxiPitch(currentVisible) - TaxiPitch(previousRouteDirection);
        float yaw = TaxiYaw(direction) + yawOffset;
        float pitch = Math.Max(-1.52f, Math.Min(1.52f, TaxiPitch(direction) + pitchOffset));
        visibleLook = TaxiDirectionFromYawPitch(yaw, pitch);
      }
      Vector3 up = Math.Abs(Vector3.Dot(visibleLook, Vector3.UnitY)) > .985f ? Vector3.UnitZ : Vector3.UnitY;
      camera.LookAt(cameraAnchor, cameraAnchor - visibleLook, up);
      taxiRideCameraInitialized = true;
      taxiRideDirection = direction;

      // Keyboard free-look mirrors normal perspective navigation. Translation keys intentionally remain owned by the
      // spline so the camera cannot drift away from its taxi while the ride is active.
      float lookStep = 1.8f * Math.Max(0f, dt);
      if (Util.IsKeyDown(Keys.A) || Util.IsKeyDown(Keys.J)) camera.Yaw(lookStep);
      if (Util.IsKeyDown(Keys.D) || Util.IsKeyDown(Keys.L)) camera.Yaw(-lookStep);
      if (Util.IsKeyDown(Keys.I)) PitchPerspectiveCamera(lookStep);
      if (Util.IsKeyDown(Keys.K)) PitchPerspectiveCamera(-lookStep);

      currentCameraRoom = FindCameraRoom(camera.Position);
      if (atEnd) {
        if (AdvanceTaxiRideLeg()) return true;
        EndTaxiRide("Taxi route complete: " + taxiRideLabel);
      }
      return true;
    }

    private static IEnumerable<WorldTaxiRouteInfo> TaxiRideLegs(WorldTaxiRouteInfo route) {
      if (route == null) return Enumerable.Empty<WorldTaxiRouteInfo>();
      return route.Legs != null && route.Legs.Count > 0 ? route.Legs : new[] { route };
    }

    // Multi-hop taxi journeys are authored as graph legs, but in-game many of them are a single continuous ride:
    // the vehicle passes intermediate terminals without stopping/restarting. Merge the spline legs into one timeline
    // and remove endpoint holds at the seams. If a client has incompatible legs we fail open to the old leg-by-leg path.
    private static SpnMotionRoute BuildTaxiThroughRoute(IList<WorldTaxiRouteInfo> legs) {
      if (legs == null || legs.Count < 2) return null;
      var points = new List<SpnMotionPoint>();
      for (int legIndex = 0; legIndex < legs.Count; legIndex++) {
        WorldTaxiRouteInfo leg = legs[legIndex];
        List<SpnMotionPoint> legPoints = FlattenSpnMotionPoints(leg?.Path);
        if (leg?.Reversed == true) legPoints.Reverse();
        if (legPoints.Count < 2) return null;

        for (int i = 0; i < legPoints.Count; i++) {
          SpnMotionPoint source = legPoints[i];
          var copy = new SpnMotionPoint { Position = source.Position, Speed = SafeSpnSpeed(source.Speed), HoldTime = Math.Max(0f, source.HoldTime) };
          if (points.Count == 0) { points.Add(copy); continue; }

          bool firstOfLeg = i == 0;
          if (firstOfLeg) {
            // A graph seam can repeat the exact terminal point or have a very short authored hand-off segment. In
            // both cases the intermediate station is a pass-through point, never a new taxi boarding pause.
            points[points.Count - 1].HoldTime = 0f;
            copy.HoldTime = 0f;
            if ((points[points.Count - 1].Position - copy.Position).LengthSquared() <= .0025f) continue;
          }
          points.Add(copy);
        }
      }
      if (points.Count < 2) return null;

      var route = new SpnMotionRoute();
      route.Points.AddRange(points);
      float timeline = 0f;
      Action<int, int, float> add = (from, to, duration) => {
        if (!(duration > 0f) || Single.IsNaN(duration) || Single.IsInfinity(duration)) return;
        route.Events.Add(new SpnMotionEvent { From = from, To = to, Start = timeline, Duration = duration });
        timeline += duration;
      };
      for (int i = 0; i < points.Count; i++) {
        add(i, -1, Math.Max(0f, points[i].HoldTime));
        if (i + 1 >= points.Count) continue;
        float length = (points[i + 1].Position - points[i].Position).Length();
        float speed = (SafeSpnSpeed(points[i].Speed) + SafeSpnSpeed(points[i + 1].Speed)) * .5f;
        add(i, i + 1, speed > 0f ? length / speed : 0f);
      }
      if (!(timeline > 0f)) return null;
      route.LegDuration = timeline;
      return route;
    }

    private bool AdvanceTaxiRideLeg() {
      while (taxiRideRemainingLegs.Count > 0) {
        WorldTaxiRouteInfo next = taxiRideRemainingLegs.Dequeue();
        taxiRideLegIndex++;
        if (!BeginTaxiRideLeg(next, false)) continue;
        if (Window is WorldBrowser browser)
          browser.SetStatusLabel("Taxi ride: " + taxiRideLabel + "  •  leg " + taxiRideLegIndex + "/" + taxiRideLegCount +
            "  •  free look, Space = pause, wheel = speed, Esc = stop");
        return true;
      }
      return false;
    }

    private bool BeginTaxiRideLeg(WorldTaxiRouteInfo leg, bool firstLeg) {
      AreaPath path = leg?.Path;
      if (path?.Points == null || path.Points.Count < 2) return false;
      SpnMotionRoute built = BuildSpnMotionRoute(path, leg.Reversed ? 2 : 1);
      if (built == null || built.LegDuration <= 0f) return false;
      taxiRideRoute = built;
      taxiRideElapsed = 0f;
      taxiRideCurrentLegLabel = leg.Label ?? leg.PathFqn ?? path.Name ?? "Taxi leg";
      taxiRideVehicleModel = leg.VehicleModel;
      taxiRideVehicleScale = leg.VehicleScale > 0f && !Single.IsNaN(leg.VehicleScale) && !Single.IsInfinity(leg.VehicleScale) ? leg.VehicleScale : 1f;
      taxiRideVehicleModelPath = leg.VehicleModelPath;
      taxiRideVehicleWorld = Matrix.Identity;
      taxiRideVehicleVisible = false;
      if (firstLeg) {
        taxiRideCameraInitialized = false;
        taxiRideDirection = Vector3.UnitZ;
      }
      return true;
    }

    private static Vector3 TaxiRouteDirection(SpnMotionRoute route, float time, float duration) {
      float step = Math.Max(.08f, Math.Min(.5f, duration * .0025f));
      Vector3 position = SpnRouteSample(route, time);
      Vector3 direction = Vector3.Zero;
      if (time + step <= duration) direction = SpnRouteSample(route, time + step) - position;
      if (direction.LengthSquared() < .000001f && time > 0f) direction = position - SpnRouteSample(route, Math.Max(0f, time - step));
      if (direction.LengthSquared() < .000001f && route?.Points != null && route.Points.Count > 1)
        direction = route.Points[route.Points.Count - 1].Position - route.Points[route.Points.Count - 2].Position;
      if (direction.LengthSquared() < .000001f || !IsFinite(direction)) direction = Vector3.UnitZ;
      direction.Normalize();
      return direction;
    }

    private static float TaxiYaw(Vector3 direction) {
      if (!IsFinite(direction)) return 0f;
      return (float)Math.Atan2(direction.X, direction.Z);
    }

    private static float TaxiPitch(Vector3 direction) {
      if (!IsFinite(direction)) return 0f;
      float len = direction.Length();
      if (!(len > .000001f)) return 0f;
      return (float)Math.Asin(Math.Max(-1f, Math.Min(1f, direction.Y / len)));
    }

    private static float WrapTaxiAngle(float angle) {
      const float twoPi = (float)(Math.PI * 2.0);
      while (angle > Math.PI) angle -= twoPi;
      while (angle < -Math.PI) angle += twoPi;
      return angle;
    }

    private static Vector3 TaxiDirectionFromYawPitch(float yaw, float pitch) {
      float cp = (float)Math.Cos(pitch);
      Vector3 direction = new Vector3((float)Math.Sin(yaw) * cp, (float)Math.Sin(pitch), (float)Math.Cos(yaw) * cp);
      if (direction.LengthSquared() < .000001f || !IsFinite(direction)) return Vector3.UnitZ;
      direction.Normalize();
      return direction;
    }

    private Vector3 TaxiCameraAnchor(Vector3 position, Vector3 direction, bool showVehicle) {
      if (!showVehicle || taxiRideVehicleModel == null) return position + new Vector3(0f, TaxiDefaultEyeHeight, 0f);
      float radius = TaxiVehicleRadius(taxiRideVehicleModel, taxiRideVehicleScale);
      float follow = Math.Max(.28f, Math.Min(1.5f, radius * 2.2f));
      float height = Math.Max(.18f, Math.Min(.9f, radius * .65f + .12f));
      return position - direction * follow + Vector3.UnitY * height;
    }

    private static float TaxiVehicleRadius(GR2 model, float scale) {
      if (model?.globalBox == null) return .18f;
      float dx = Math.Abs(model.globalBox.maxX - model.globalBox.minX);
      float dy = Math.Abs(model.globalBox.maxY - model.globalBox.minY);
      float dz = Math.Abs(model.globalBox.maxZ - model.globalBox.minZ);
      float diameter = Math.Max(dx, Math.Max(dy, dz)) * Math.Max(.001f, scale);
      if (!(diameter > 0f) || Single.IsNaN(diameter) || Single.IsInfinity(diameter)) return .18f;
      return Math.Max(.08f, Math.Min(2f, diameter * .5f));
    }

    private static Matrix TaxiVehicleWorld(Vector3 position, Vector3 direction, float scale) {
      float horizontal = (float)Math.Sqrt(direction.X * direction.X + direction.Z * direction.Z);
      float yaw = (float)Math.Atan2(direction.X, direction.Z);
      float pitch = -(float)Math.Atan2(direction.Y, Math.Max(.00001f, horizontal));
      float safeScale = scale > 0f && !Single.IsNaN(scale) && !Single.IsInfinity(scale) ? scale : 1f;
      return Matrix.Scaling(safeScale, safeScale, safeScale) * Matrix.RotationYawPitchRoll(yaw, pitch, 0f) * Matrix.Translation(position);
    }

    /// <summary>Draw the taxVehicleSpec model at the current spline pose. The model is loaded/registered on the UI
    /// loading thread before View_AREA.LoadModel, so all GR2 buffers and materials already exist here.</summary>
    private void DrawTaxiVehicle(Matrix vp, HashSet<string> visible, WorldRenderSettings s, AreaEnvironmentScheme cameraEnv, bool sceneShadows) {
      if (!taxiRideActive || !taxiRideVehicleVisible || taxiRideVehicleModel == null || s == null || !s.ShowTaxiVehicle ||
          s.Mode == WorldRenderMode.Map || s.Mode == WorldRenderMode.Heightmap) return;
      Matrix world = taxiRideVehicleWorld;
      Vector3 position = new Vector3(world.M41, world.M42, world.M43);
      Room room = currentCameraRoom;
      if (room != null) ApplyRoomEnvironment(room, cameraEnv, s, sceneShadows);
      float receiverRadius = 0f;
      Vector3 lightPoint = position;
      if (TryModelSphere(taxiRideVehicleModel, world, out Vector3 center, out float radius)) { lightPoint = center; receiverRadius = radius; }
      SetNearestLocalLights(lightPoint, room, s, visible, false, false, false, receiverRadius);
      DrawModel(taxiRideVehicleModel, world, vp, s, false, null);
    }

    public void OpenTaxiRouteMap(IEnumerable<WorldTaxiRouteInfo> routes, string sourceLabel) {
      if (routes == null) return;
      CloseQuickTravelMapState();
      List<WorldTaxiRouteInfo> usable = routes.Where(r => TaxiRideLegs(r).Any(x => x?.Path?.Points != null && x.Path.Points.Count >= 2)).ToList();
      if (usable.Count == 0) return;
      lock (taxiRouteMapSync) {
        taxiRouteMapRoutes.Clear();
        taxiRouteMapRoutes.AddRange(usable);
        taxiRouteMapSourceLabel = sourceLabel ?? usable[0].SourceLabel ?? "Taxi terminal";
        taxiRouteMapGpuDirty = true;
        taxiRouteMapActive = true;
      }
      SetMapOpen(true);
      FitTaxiRouteMap();
      if (Window is WorldBrowser browser) {
        browser.SetFullMapActive(true);
        browser.SetStatusLabel("Taxi map — " + taxiRouteMapSourceLabel + ": click a highlighted route/destination; drag = pan, wheel = zoom, Esc/M = close");
      }
    }

    private void FitTaxiRouteMap() {
      if (!taxiRouteMapActive || !mapOpen) return;
      List<WorldTaxiRouteInfo> routes;
      lock (taxiRouteMapSync) routes = taxiRouteMapRoutes.ToList();
      if (routes.Count == 0) return;
      float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
      bool any = false;
      foreach (WorldTaxiRouteInfo route in routes) foreach (WorldTaxiRouteInfo leg in TaxiRideLegs(route)) {
        if (leg?.Path?.Points == null) continue;
        foreach (AreaPathPoint point in leg.Path.Points) {
          Vector3 p = point.Position; if (!IsFinite(p)) continue;
          minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X); minZ = Math.Min(minZ, p.Z); maxZ = Math.Max(maxZ, p.Z); any = true;
        }
      }
      if (!any) return;
      mapCenter = new Vector2((minX + maxX) * .5f, (minZ + maxZ) * .5f);
      UpdateMapCamera();
      float aspect = Math.Max(.1f, ClientWidth / (float)Math.Max(1, ClientHeight));
      float desired = Math.Max(3f, Math.Max((maxZ - minZ) * 1.35f, (maxX - minX) * 1.35f / aspect));
      mapZoom = Math.Max(MapMinZoom, Math.Min(1f, desired / Math.Max(.001f, mapBaseHeight)));
      UpdateMapCamera();
    }

    private void CloseTaxiRouteMapState() {
      if (!taxiRouteMapActive && !taxiRouteMapGpuDirty) return;
      lock (taxiRouteMapSync) {
        taxiRouteMapActive = false;
        taxiRouteMapSourceLabel = String.Empty;
        taxiRouteMapRoutes.Clear();
        taxiRouteMapGpuDirty = true;
      }
    }

    private void ReleaseTaxiRouteMapGpu() {
      foreach (LineGpu line in taxiRouteMapGpu) line?.Dispose();
      taxiRouteMapGpu.Clear();
      lock (taxiRouteMapSync) {
        taxiRouteMapRoutes.Clear();
        taxiRouteMapSourceLabel = String.Empty;
        taxiRouteMapActive = false;
        taxiRouteMapGpuDirty = false;
      }
    }

    private static string TaxiRouteMapDisplayName(WorldTaxiRouteInfo route) {
      if (route == null) return "Taxi route";
      string name = !String.IsNullOrWhiteSpace(route.DestinationLabel) ? route.DestinationLabel : route.Label;
      if (String.IsNullOrWhiteSpace(name)) name = route.PathFqn ?? route.Path?.Name ?? "Taxi route";
      if (route.Cost >= 0) name += "  (" + route.Cost.ToString(System.Globalization.CultureInfo.InvariantCulture) + " cr)";
      if (route.HopCount > 1) name += "  [" + route.HopCount.ToString(System.Globalization.CultureInfo.InvariantCulture) + " legs]";
      return name;
    }

    private static Vector3 TaxiRouteDestination(WorldTaxiRouteInfo route) {
      WorldTaxiRouteInfo last = TaxiRideLegs(route).LastOrDefault(x => x?.Path?.Points != null && x.Path.Points.Count > 0);
      if (last?.Path?.Points == null || last.Path.Points.Count == 0) return Vector3.Zero;
      return last.Reversed ? last.Path.Points[0].Position : last.Path.Points[last.Path.Points.Count - 1].Position;
    }

    private PointF TaxiMapScreenPoint(Vector3 world) {
      float width = Math.Max(1f, ClientWidth), height = Math.Max(1f, ClientHeight);
      return new PointF((world.X - mapCenter.X) / Math.Max(.0001f, mapVisibleWidth) * width + width * .5f,
        (world.Z - mapCenter.Y) / Math.Max(.0001f, mapVisibleHeight) * height + height * .5f);
    }

    private static float TaxiPointSegmentDistanceSquared(PointF p, PointF a, PointF b) {
      float dx = b.X - a.X, dy = b.Y - a.Y;
      float denom = dx * dx + dy * dy;
      if (denom <= .0001f) { float x = p.X - a.X, y = p.Y - a.Y; return x * x + y * y; }
      float t = Math.Max(0f, Math.Min(1f, ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / denom));
      float qx = a.X + dx * t, qy = a.Y + dy * t, ex = p.X - qx, ey = p.Y - qy;
      return ex * ex + ey * ey;
    }

    private bool TryPickTaxiRouteOnMap(Point point, out WorldTaxiRouteInfo route) {
      route = null; if (!taxiRouteMapActive || !mapOpen) return false;
      List<WorldTaxiRouteInfo> routes; lock (taxiRouteMapSync) routes = taxiRouteMapRoutes.ToList();
      if (routes.Count == 0) return false;
      PointF mouse = new PointF(point.X, point.Y);
      float best = 18f * 18f;
      // Destination markers win over lines, mirroring the game's clickable terminal nodes.
      foreach (WorldTaxiRouteInfo candidate in routes) {
        PointF d = TaxiMapScreenPoint(TaxiRouteDestination(candidate));
        float dx = mouse.X - d.X, dy = mouse.Y - d.Y, dist = dx * dx + dy * dy;
        if (dist <= best) { best = dist; route = candidate; }
      }
      if (route != null) return true;
      best = 9f * 9f;
      foreach (WorldTaxiRouteInfo candidate in routes) {
        foreach (WorldTaxiRouteInfo leg in TaxiRideLegs(candidate)) {
          IList<AreaPathPoint> points = leg.Path?.Points; if (points == null || points.Count < 2) continue;
          for (int i = 1; i < points.Count; i++) {
            float dist = TaxiPointSegmentDistanceSquared(mouse, TaxiMapScreenPoint(points[i - 1].Position), TaxiMapScreenPoint(points[i].Position));
            if (dist <= best) { best = dist; route = candidate; }
          }
        }
      }
      return route != null;
    }

    private void RebuildTaxiRouteMapGpuIfNeeded() {
      if (!taxiRouteMapGpuDirty) return;
      foreach (LineGpu line in taxiRouteMapGpu) line?.Dispose();
      taxiRouteMapGpu.Clear();
      List<WorldTaxiRouteInfo> routes;
      lock (taxiRouteMapSync) { routes = taxiRouteMapRoutes.ToList(); taxiRouteMapGpuDirty = false; }
      int colorIndex = 0;
      Vector4[] colors = {
        new Vector4(.18f, .78f, 1f, 1f), new Vector4(.35f, 1f, .58f, 1f), new Vector4(1f, .72f, .18f, 1f),
        new Vector4(.92f, .45f, 1f, 1f), new Vector4(1f, .38f, .38f, 1f)
      };
      foreach (WorldTaxiRouteInfo route in routes) {
        Vector4 color = colors[colorIndex++ % colors.Length];
        foreach (WorldTaxiRouteInfo leg in TaxiRideLegs(route)) {
          IList<AreaPathPoint> points = leg.Path?.Points; if (points == null || points.Count < 2) continue;
          var segments = new List<Vector3>((points.Count - 1) * 2);
          for (int i = 1; i < points.Count; i++) { segments.Add(points[i - 1].Position + Vector3.UnitY * .08f); segments.Add(points[i].Position + Vector3.UnitY * .08f); }
          taxiRouteMapGpu.Add(BuildLine(segments, color));
        }
        Vector3 d = TaxiRouteDestination(route) + Vector3.UnitY * .10f;
        float r = Math.Max(.18f, Math.Min(.55f, Math.Max(mapVisibleWidth / Math.Max(1, ClientWidth), mapVisibleHeight / Math.Max(1, ClientHeight)) * 10f));
        taxiRouteMapGpu.Add(BuildLine(new[] { d - new Vector3(r, 0, 0), d + new Vector3(r, 0, 0), d - new Vector3(0, 0, r), d + new Vector3(0, 0, r) }, color));
      }
    }

    private void DrawTaxiRouteMapLines(Matrix vp) {
      if (taxiRouteMapGpu.Count == 0) return;
      ImmediateContext.InputAssembler.PrimitiveTopology = SlimDX.Direct3D11.PrimitiveTopology.LineList;
      fx.SetWorld(Matrix.Identity); fx.SetViewProj(vp); fx.SetMaterial(null);
      foreach (LineGpu line in taxiRouteMapGpu) {
        if (line?.Buffer == null || line.Count <= 0) continue;
        fx.SetOverlay(line.Color);
        ImmediateContext.InputAssembler.SetVertexBuffers(0, new SlimDX.Direct3D11.VertexBufferBinding(line.Buffer, PosNormalTexTan.Stride, 0));
        // MapMarker uses NoDepthDSS, so routes stay visible over roofs/terrain just like the in-game taxi map.
        fx.MapMarker.GetPassByIndex(0).Apply(ImmediateContext);
        ImmediateContext.Draw(line.Count, 0);
      }
      ImmediateContext.InputAssembler.PrimitiveTopology = SlimDX.Direct3D11.PrimitiveTopology.TriangleList;
    }

    private void DrawTaxiRouteMapOverlay(Matrix vp, WorldRenderSettings s) {
      if (!taxiRouteMapActive || !mapOpen || s == null || s.Mode != WorldRenderMode.Map) return;
      RebuildTaxiRouteMapGpuIfNeeded();
      DrawTaxiRouteMapLines(vp);
      if (!npcTextFontsRegistered || npcTextFont == null || npcTextSprite == null) return;
      int viewportWidth = Math.Max(1, (int)Viewport.Width), viewportHeight = Math.Max(1, (int)Viewport.Height);
      if (viewportWidth != npcNameplateViewportWidth || viewportHeight != npcNameplateViewportHeight) {
        npcNameplateViewportWidth = viewportWidth; npcNameplateViewportHeight = viewportHeight;
        try { npcTextSprite.RefreshViewport(); } catch { }
      }
      List<WorldTaxiRouteInfo> routes; lock (taxiRouteMapSync) routes = taxiRouteMapRoutes.ToList();
      foreach (WorldTaxiRouteInfo route in routes) {
        PointF p = TaxiMapScreenPoint(TaxiRouteDestination(route));
        if (p.X < -120 || p.X > viewportWidth + 120 || p.Y < -30 || p.Y > viewportHeight + 30) continue;
        string label = TaxiRouteMapDisplayName(route);
        DrawNpcText(label, new Vector2(p.X + 1f, p.Y - 21f), new Color4(.92f, 0f, 0f, 0f), "world-npc-item");
        DrawNpcText(label, new Vector2(p.X, p.Y - 22f), new Color4(1f, .82f, .94f, 1f), "world-npc-item");
      }
      npcTextSprite.Flush();
    }

    private void DrawTaxiRideHud(WorldRenderSettings s) {
      if (!taxiRideActive || taxiRideRoute == null || s == null || s.Mode == WorldRenderMode.Map ||
          s.Mode == WorldRenderMode.Heightmap || !npcTextFontsRegistered || npcTextFont == null || npcTextSprite == null)
        return;

      float duration = taxiRideRoute.LegDuration;
      if (!(duration > 0f) || Single.IsNaN(duration) || Single.IsInfinity(duration)) return;
      int viewportWidth = Math.Max(1, (int)Viewport.Width);
      int viewportHeight = Math.Max(1, (int)Viewport.Height);
      if (viewportWidth <= 0 || viewportHeight <= 0) return;
      if (viewportWidth != npcNameplateViewportWidth || viewportHeight != npcNameplateViewportHeight) {
        npcNameplateViewportWidth = viewportWidth;
        npcNameplateViewportHeight = viewportHeight;
        try { npcTextSprite.RefreshViewport(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine("Taxi HUD viewport refresh failed: " + ex.Message); }
      }

      float ratio = Math.Max(0f, Math.Min(1f, taxiRideElapsed / duration));
      float remainingRouteSeconds = Math.Max(0f, duration - taxiRideElapsed);
      float remainingRealSeconds = taxiRidePaused ? remainingRouteSeconds : remainingRouteSeconds / Math.Max(.1f, taxiRideSpeedMultiplier);
      const int segments = 34;
      int filled = Math.Max(0, Math.Min(segments, (int)Math.Round(ratio * segments)));
      string bar = "[" + new string('█', filled) + new string('░', segments - filled) + "]  " +
        (ratio * 100f).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "%";
      string state = taxiRidePaused ? "Paused" : FormatTaxiRideTime(remainingRealSeconds) + " remaining";
      string detail = state + "  •  " + taxiRideSpeedMultiplier.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "x";
      if (taxiRideLegCount > 1) detail += taxiRideThroughJourney
        ? "  •  through route (" + taxiRideLegCount + " segments)"
        : "  •  leg " + taxiRideLegIndex + "/" + taxiRideLegCount;
      if (s.ShowTaxiVehicle && taxiRideVehicleModel != null && !String.IsNullOrWhiteSpace(taxiRideVehicleModelPath))
        detail += "  •  " + System.IO.Path.GetFileName(taxiRideVehicleModelPath.Replace('\\', '/'));
      else if (!s.ShowTaxiVehicle && taxiRideVehicleModel != null) detail += "  •  vehicle hidden";

      float y = Math.Max(4f, viewportHeight - 47f);
      Color4 shadow = new Color4(.92f, 0f, 0f, 0f);
      Color4 barColor = new Color4(1f, .94f, .82f, .30f);
      Color4 detailColor = new Color4(1f, .88f, .93f, 1f);
      DrawNpcText(bar, new Vector2(viewportWidth * .5f + 1f, y + 1f), shadow, "world-taxi");
      DrawNpcText(bar, new Vector2(viewportWidth * .5f, y), barColor, "world-taxi");
      DrawNpcText(detail, new Vector2(viewportWidth * .5f + 1f, y + 18f), shadow, "world-npc-item");
      DrawNpcText(detail, new Vector2(viewportWidth * .5f, y + 17f), detailColor, "world-npc-item");
      npcTextSprite.Flush();
    }

    private static string FormatTaxiRideTime(float seconds) {
      if (Single.IsNaN(seconds) || Single.IsInfinity(seconds) || seconds < 0f) seconds = 0f;
      int total = Math.Max(0, (int)Math.Ceiling(seconds));
      int minutes = total / 60;
      int secs = total % 60;
      return minutes > 0
        ? minutes.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + secs.ToString("00", System.Globalization.CultureInfo.InvariantCulture)
        : secs.ToString(System.Globalization.CultureInfo.InvariantCulture) + "s";
    }

    private void ConsumePendingTaxiRide() {
      List<WorldTaxiRouteInfo> journey = null;
      string label = null;
      bool start = false;
      bool stop = false;
      lock (taxiRideSync) {
        stop = taxiPendingStop;
        taxiPendingStop = false;
        if (taxiPendingStart) {
          journey = taxiPendingJourney == null ? null : taxiPendingJourney.ToList();
          label = taxiPendingLabel;
          taxiPendingJourney = null;
          taxiPendingLabel = null;
          taxiPendingStart = false;
          start = true;
        }
      }

      if (stop) EndTaxiRide(null);
      if (!start || journey == null || journey.Count == 0) return;
      taxiRideRemainingLegs.Clear();
      WorldTaxiRouteInfo first = journey[0];
      taxiRideLegIndex = 1;
      taxiRideLegCount = journey.Count;
      taxiRideThroughJourney = false;

      SpnMotionRoute through = journey.Count > 1 ? BuildTaxiThroughRoute(journey) : null;
      if (through != null) {
        taxiRideRoute = through;
        taxiRideElapsed = 0f;
        taxiRideCurrentLegLabel = label ?? first.Label ?? first.PathFqn ?? first.Path?.Name ?? "Taxi route";
        taxiRideVehicleModel = first.VehicleModel;
        taxiRideVehicleScale = first.VehicleScale > 0f && !Single.IsNaN(first.VehicleScale) && !Single.IsInfinity(first.VehicleScale) ? first.VehicleScale : 1f;
        taxiRideVehicleModelPath = first.VehicleModelPath;
        taxiRideVehicleWorld = Matrix.Identity;
        taxiRideVehicleVisible = false;
        taxiRideCameraInitialized = false;
        taxiRideDirection = Vector3.UnitZ;
        taxiRideThroughJourney = true;
      } else {
        for (int i = 1; i < journey.Count; i++) taxiRideRemainingLegs.Enqueue(journey[i]);
        if (!BeginTaxiRideLeg(first, true)) {
          taxiRideRemainingLegs.Clear();
          taxiRideLegIndex = taxiRideLegCount = 0;
          if (Window is WorldBrowser failedBrowser) failedBrowser.SetStatusLabel("Taxi path could not be converted into a rideable route: " + (label ?? first?.PathFqn ?? first?.Path?.Name));
          return;
        }
      }
      taxiRideSpeedMultiplier = 1f;
      taxiRidePaused = false;
      taxiSpaceKeyWasDown = Util.IsKeyDown(Keys.Space);
      taxiEscapeKeyWasDown = Util.IsKeyDown(Keys.Escape);
      taxiRideLabel = label ?? first.PathFqn ?? first.Path.Name ?? "Taxi route";
      taxiRideActive = true;
      InvalidateTemporalHistory();
      if (Window is WorldBrowser browser) {
        string vehicle = taxiRideVehicleModel != null
          ? "  •  vehicle: " + (!String.IsNullOrWhiteSpace(taxiRideVehicleModelPath) ? taxiRideVehicleModelPath : taxiRideVehicleModel.filename)
          : String.Empty;
        string leg = taxiRideLegCount > 1 ? (taxiRideThroughJourney
          ? "  •  through route (" + taxiRideLegCount + " segments)"
          : "  •  leg 1/" + taxiRideLegCount) : String.Empty;
        browser.SetStatusLabel("Taxi ride: " + taxiRideLabel + leg + vehicle + "  •  free look, Space = pause, wheel = speed, Esc = stop");
      }
    }

    private void EndTaxiRide(string status) {
      bool wasActive = taxiRideActive || taxiRideRoute != null;
      taxiRideActive = false;
      taxiRideRoute = null;
      taxiRideRemainingLegs.Clear();
      taxiRideLegIndex = taxiRideLegCount = 0;
      taxiRideThroughJourney = false;
      taxiRideCurrentLegLabel = String.Empty;
      taxiRideElapsed = 0f;
      taxiRidePaused = false;
      taxiSpaceKeyWasDown = false;
      taxiRideVehicleModel = null;
      taxiRideVehicleScale = 1f;
      taxiRideVehicleModelPath = null;
      taxiRideVehicleVisible = false;
      taxiRideVehicleWorld = Matrix.Identity;
      taxiRideCameraInitialized = false;
      taxiRideDirection = Vector3.UnitZ;
      if (wasActive) InvalidateTemporalHistory();
      if (!String.IsNullOrWhiteSpace(status) && Window is WorldBrowser browser) browser.SetStatusLabel(status);
    }

    private bool HandleTaxiMouseWheel(int delta) {
      if (!taxiRideActive || taxiRideRoute == null) return false;
      taxiRideSpeedMultiplier = (float)Math.Max(.1, Math.Min(8.0, taxiRideSpeedMultiplier * Math.Exp(delta * .001f)));
      if (Window is WorldBrowser browser)
        browser.SetStatusLabel("Taxi speed: " + taxiRideSpeedMultiplier.ToString("0.##") + "x  •  free look, Space = pause, Esc = stop");
      return true;
    }
  }
}
