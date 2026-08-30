using System;
using System.Linq;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using FileFormats;
using SlimDX;
using SlimDXNet;
using System.Windows.Forms;

namespace PugTools {
  internal sealed partial class View_AREA {
    private readonly object spaceFlypathSync = new object();
    private AreaPath spaceFlypathPendingPath;
    private String spaceFlypathPendingLabel;
    private Boolean spaceFlypathPendingStart;
    private Boolean spaceFlypathPendingStop;
    private Single? spaceFlypathPendingSeek;
    private Boolean spaceFlypathPendingTogglePause;
    private Dictionary<Int32, String> spaceFlypathPendingWaypointTemplates;
    private WorldSpaceCombatEncounterSpec[] spaceFlypathPendingEncounterSpecs = Array.Empty<WorldSpaceCombatEncounterSpec>();
    private Boolean spaceFlypathPendingEncounterSpecsChanged;

    private AreaPath spaceFlypathPath;
    private SpnMotionRoute spaceFlypathRoute;
    private volatile Boolean spaceFlypathActive;
    private volatile String spaceFlypathLabel = String.Empty;
    private Single spaceFlypathElapsed;
    private Single spaceFlypathSpeedMultiplier = 1f;
    private Boolean spaceFlypathPaused;
    private Boolean spaceFlypathSpaceKeyWasDown;
    private Boolean spaceFlypathEscapeKeyWasDown;
    private Boolean spaceFlypathCameraInitialized;
    private Vector3 spaceFlypathDirection = Vector3.UnitZ;
    private Dictionary<Int32, String> spaceFlypathWaypointTemplates = new Dictionary<Int32, String>();
    private WorldSpaceCombatEncounterSpec[] spaceFlypathEncounterSpecs = Array.Empty<WorldSpaceCombatEncounterSpec>();
    private SpaceFlypathTickInfo[] spaceFlypathAuthoredTicks = Array.Empty<SpaceFlypathTickInfo>();
    private volatile SpaceFlypathTickInfo[] spaceFlypathTicks = Array.Empty<SpaceFlypathTickInfo>();
    private volatile SpaceCombatEncounterTimelineInfo[] spaceCombatEncounterTimeline = Array.Empty<SpaceCombatEncounterTimelineInfo>();
    private SpaceCombatShipRuntime[] spaceCombatShips = Array.Empty<SpaceCombatShipRuntime>();

    private sealed class SpaceCombatShipRuntime {
      public WorldSpaceCombatEncounterSpec Spec;
      public GR2 Model;
      public SpnMotionRoute Route;
      public Single StartTime;
      public Single EndTime;
      public Single SpeedScale = 1f;
      public Single ModelScale = 1f;
      public Vector3 LocalOffset;
      public Int32 SlotIndex;
    }

    public sealed class SpaceFlypathTickInfo {
      public String Kind { get; internal set; }
      public String Fqn { get; internal set; }
      public String Label { get; internal set; }
      public String Detail { get; internal set; }
      public Single Progress { get; internal set; }
      public Single TimeSeconds { get; internal set; }
      public UInt64 InstanceId { get; internal set; }
      public String RoomName { get; internal set; }
    }

    public sealed class SpaceCombatEncounterTimelineInfo {
      public String EncounterName { get; internal set; }
      public String TriggerFqn { get; internal set; }
      public String PathName { get; internal set; }
      public String VehicleSpec { get; internal set; }
      public String ModelPath { get; internal set; }
      public String EngineFx { get; internal set; }
      public String AnchorOffset { get; internal set; }
      public String FormationKind { get; internal set; }
      public Single ModelScale { get; internal set; } = 1f;
      public Single SpeedScale { get; internal set; } = 1f;
      public Single SpawnDelay { get; internal set; }
      public String Detail { get; internal set; }
      public Single StartProgress { get; internal set; }
      public Single EndProgress { get; internal set; } = -1f;
      public Single StartTime { get; internal set; }
      public Single EndTime { get; internal set; } = -1f;
      public Int32 ShipCount { get; internal set; }
      public Int32 BoltedCount { get; internal set; }
      public Boolean Boss { get; internal set; }
    }

    public Boolean IsSpaceFlypathActive => spaceFlypathActive || spaceFlypathPendingStart;
    public String CurrentSpaceFlypathLabel => spaceFlypathLabel ?? String.Empty;
    public String CurrentSpaceFlypathFqn => spaceFlypathPath?.Fqn ?? String.Empty;
    public String CurrentSpaceFlypathName => spaceFlypathPath?.Name ?? String.Empty;
    public Boolean SpaceFlypathPaused => spaceFlypathPaused;
    public Single SpaceFlypathSpeedMultiplier => spaceFlypathSpeedMultiplier;
    public SpaceFlypathTickInfo[] SpaceFlypathTicks => spaceFlypathTicks?.ToArray() ?? Array.Empty<SpaceFlypathTickInfo>();
    public SpaceCombatEncounterTimelineInfo[] SpaceCombatEncounterTimeline => spaceCombatEncounterTimeline?.ToArray() ?? Array.Empty<SpaceCombatEncounterTimelineInfo>();
    public Single SpaceFlypathProgress {
      get {
        Single duration = spaceFlypathRoute?.LegDuration ?? 0f;
        return duration > 0f ? Math.Max(0f, Math.Min(1f, spaceFlypathElapsed / duration)) : 0f;
      }
    }

    public void StartSpaceFlypath(AreaPath path, String label = null, Dictionary<Int32, String> waypointTemplates = null) {
      if (path?.Points == null || path.Points.Count < 2) return;
      lock (spaceFlypathSync) {
        spaceFlypathPendingPath = path;
        spaceFlypathPendingLabel = label ?? path.Fqn ?? path.Name ?? "Space Combat rail";
        spaceFlypathPendingWaypointTemplates = waypointTemplates != null
          ? new Dictionary<Int32, String>(waypointTemplates) : new Dictionary<Int32, String>();
        spaceFlypathPendingStop = false;
        spaceFlypathPendingStart = true;
        spaceFlypathPendingSeek = null;
        spaceFlypathLabel = spaceFlypathPendingLabel;
      }
    }

    public void SetSpaceCombatEncounterSpecs(IEnumerable<WorldSpaceCombatEncounterSpec> specs) {
      lock (spaceFlypathSync) {
        spaceFlypathPendingEncounterSpecs = specs?.Where(x => x != null).ToArray() ?? Array.Empty<WorldSpaceCombatEncounterSpec>();
        spaceFlypathPendingEncounterSpecsChanged = true;
      }
    }

    public void StopSpaceFlypath() {
      lock (spaceFlypathSync) {
        spaceFlypathPendingStart = false;
        spaceFlypathPendingPath = null;
        spaceFlypathPendingLabel = null;
        spaceFlypathPendingSeek = null;
        spaceFlypathPendingStop = true;
      }
    }

    private void ClearSpaceFlypathState() {
      lock (spaceFlypathSync) {
        spaceFlypathPendingPath = null;
        spaceFlypathPendingLabel = null;
        spaceFlypathPendingStart = false;
        spaceFlypathPendingStop = false;
        spaceFlypathPendingSeek = null;
        spaceFlypathPendingTogglePause = false;
        spaceFlypathPendingWaypointTemplates = null;
        spaceFlypathPendingEncounterSpecs = Array.Empty<WorldSpaceCombatEncounterSpec>();
        spaceFlypathPendingEncounterSpecsChanged = false;
      }
      EndSpaceFlypath(null);
    }

    public void ToggleSpaceFlypathPause() {
      lock (spaceFlypathSync) spaceFlypathPendingTogglePause = true;
    }

    public void SeekSpaceFlypath(Single ratio) {
      lock (spaceFlypathSync) spaceFlypathPendingSeek = Math.Max(0f, Math.Min(1f, ratio));
    }

    private void ConsumePendingSpaceFlypath() {
      AreaPath startPath = null;
      String startLabel = null;
      Boolean start = false;
      Boolean stop = false;
      Boolean togglePause = false;
      Single? seek = null;
      Dictionary<Int32, String> startWaypointTemplates = null;
      WorldSpaceCombatEncounterSpec[] encounterSpecs = null;
      Boolean encounterSpecsChanged = false;
      lock (spaceFlypathSync) {
        stop = spaceFlypathPendingStop;
        spaceFlypathPendingStop = false;
        if (spaceFlypathPendingStart) {
          start = true;
          startPath = spaceFlypathPendingPath;
          startLabel = spaceFlypathPendingLabel;
          startWaypointTemplates = spaceFlypathPendingWaypointTemplates;
          spaceFlypathPendingStart = false;
          spaceFlypathPendingPath = null;
          spaceFlypathPendingLabel = null;
          spaceFlypathPendingWaypointTemplates = null;
        }
        encounterSpecsChanged = spaceFlypathPendingEncounterSpecsChanged;
        if (encounterSpecsChanged) {
          encounterSpecs = spaceFlypathPendingEncounterSpecs;
          spaceFlypathPendingEncounterSpecsChanged = false;
        }
        togglePause = spaceFlypathPendingTogglePause;
        spaceFlypathPendingTogglePause = false;
        seek = spaceFlypathPendingSeek;
        spaceFlypathPendingSeek = null;
      }

      if (stop) EndSpaceFlypath(null);
      if (start && startPath != null) BeginSpaceFlypath(startPath, startLabel, startWaypointTemplates);
      if (encounterSpecsChanged) {
        spaceFlypathEncounterSpecs = encounterSpecs ?? Array.Empty<WorldSpaceCombatEncounterSpec>();
        if (spaceFlypathActive && spaceFlypathRoute != null) RebuildSpaceCombatEncounterTimeline();
      }
      if (togglePause && spaceFlypathActive) {
        spaceFlypathPaused = !spaceFlypathPaused;
        if (Window is WorldBrowser browser)
          browser.SetStatusLabel((spaceFlypathPaused ? "Space rail paused: " : "Space rail resumed: ") + spaceFlypathLabel + SpaceFlypathControlHint());
      }
      if (seek.HasValue && spaceFlypathActive && spaceFlypathRoute != null) {
        Single duration = spaceFlypathRoute.LegDuration;
        if (duration > 0f) {
          spaceFlypathElapsed = Math.Max(0f, Math.Min(duration, duration * seek.Value));
          spaceFlypathCameraInitialized = false;
          InvalidateTemporalHistory();
        }
      }
    }

    private void BeginSpaceFlypath(AreaPath path, String label, Dictionary<Int32, String> waypointTemplates) {
      SpnMotionRoute route = BuildSpaceFlypathMotionRoute(path);
      if (route == null || !(route.LegDuration > 0f)) {
        if (Window is WorldBrowser badBrowser) badBrowser.SetStatusLabel("Space Combat rail has no usable timeline: " + (path.Fqn ?? path.Name));
        return;
      }

      // The two route controllers both own camera translation. A rail start therefore
      // ends any taxi ride instead of letting the two update loops fight each other.
      EndTaxiRide(null);
      spaceFlypathPath = path;
      spaceFlypathRoute = route;
      spaceFlypathLabel = label ?? path.Fqn ?? path.Name ?? "Space Combat rail";
      spaceFlypathElapsed = 0f;
      spaceFlypathSpeedMultiplier = 1f;
      spaceFlypathPaused = false;
      spaceFlypathCameraInitialized = false;
      spaceFlypathDirection = Vector3.UnitZ;
      spaceFlypathWaypointTemplates = waypointTemplates != null ? new Dictionary<Int32, String>(waypointTemplates) : new Dictionary<Int32, String>();
      spaceFlypathEncounterSpecs = Array.Empty<WorldSpaceCombatEncounterSpec>();
      spaceFlypathActive = true;
      SpaceFlypathTickInfo[] hydra = BuildSpaceFlypathHydraTicks(route);
      SpaceFlypathTickInfo[] waypoint = BuildSpaceFlypathWaypointTicks(route, path, spaceFlypathWaypointTemplates);
      spaceFlypathAuthoredTicks = hydra.Concat(waypoint).OrderBy(x => x.Progress).ThenBy(x => x.Kind, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Fqn, StringComparer.OrdinalIgnoreCase).ToArray();
      spaceFlypathTicks = spaceFlypathAuthoredTicks.ToArray();
      spaceCombatEncounterTimeline = Array.Empty<SpaceCombatEncounterTimelineInfo>();
      InvalidateTemporalHistory();
      if (Window is WorldBrowser browser)
        browser.SetStatusLabel("Space Combat rail: " + spaceFlypathLabel + SpaceFlypathControlHint());
    }


    // Player rails commonly author speed=0 at the terminal control points. area.dat retains that as the generic
    // 0.01 floor used by moving scenery, but on a player rail that turns the final metres into minutes. Jedipedia's
    // rail clock uses a 1 unit/s floor for the player only; encounter ships keep their own authored 0.01 behaviour.
    private static SpnMotionRoute BuildSpaceFlypathMotionRoute(AreaPath path) {
      List<SpnMotionPoint> points = FlattenSpnMotionPoints(path);
      if (points.Count < 2) return null;
      var route = new SpnMotionRoute();
      route.Points.AddRange(points);
      Single timeline = 0f;
      Action<Int32, Int32, Single> add = (from, to, duration) => {
        if (!(duration > 0f) || Single.IsNaN(duration) || Single.IsInfinity(duration)) return;
        route.Events.Add(new SpnMotionEvent { From = from, To = to, Start = timeline, Duration = duration });
        timeline += duration;
      };
      Func<Int32, Int32, Single> travel = (from, to) => {
        Single length = (points[to].Position - points[from].Position).Length();
        // SWTOR's rail clock uses the speed authored on the point a flattened segment leaves, not an average of
        // both endpoints. The player rail alone applies a 1 u/s floor; encounter splines use their 0.01 floor.
        Single speed = Math.Max(1f, points[from].Speed);
        return speed > 0f ? length / speed : 0f;
      };
      for (Int32 i = 0; i < points.Count; i++) {
        if (i + 1 < points.Count) add(i, i + 1, travel(i, i + 1));
      }
      if (path.Circular) add(points.Count - 1, 0, travel(points.Count - 1, 0));
      if (!(timeline > 0f)) return null;
      route.LegDuration = timeline;
      return route;
    }

    private Boolean UpdateSpaceFlypath(Single dt, Boolean escapeKey) {
      ConsumePendingSpaceFlypath();
      if (!spaceFlypathActive || spaceFlypathRoute == null) {
        spaceFlypathEscapeKeyWasDown = escapeKey;
        return false;
      }

      if (escapeKey && !spaceFlypathEscapeKeyWasDown) {
        EndSpaceFlypath("Space Combat rail stopped.");
        spaceFlypathEscapeKeyWasDown = true;
        return true;
      }
      spaceFlypathEscapeKeyWasDown = escapeKey;

      Boolean space = Util.IsKeyDown(Keys.Space);
      if (space && !spaceFlypathSpaceKeyWasDown) {
        spaceFlypathPaused = !spaceFlypathPaused;
        if (Window is WorldBrowser browser)
          browser.SetStatusLabel((spaceFlypathPaused ? "Space rail paused: " : "Space rail resumed: ") + spaceFlypathLabel + SpaceFlypathControlHint());
      }
      spaceFlypathSpaceKeyWasDown = space;

      Single duration = spaceFlypathRoute.LegDuration;
      if (!(duration > 0f) || Single.IsNaN(duration) || Single.IsInfinity(duration)) {
        EndSpaceFlypath("Space Combat rail has no usable travel duration.");
        return true;
      }

      if (!spaceFlypathPaused)
        spaceFlypathElapsed += Math.Max(0f, dt) * spaceFlypathSpeedMultiplier;
      if (spaceFlypathElapsed >= duration) {
        spaceFlypathElapsed = duration;
        spaceFlypathPaused = true;
      }
      if (spaceFlypathElapsed < 0f) spaceFlypathElapsed = 0f;

      Single time = Math.Max(0f, Math.Min(duration, spaceFlypathElapsed));
      Vector3 position = SpnRouteSample(spaceFlypathRoute, time);
      Vector3 direction = TaxiRouteDirection(spaceFlypathRoute, time, duration);
      Vector3 previousDirection = spaceFlypathDirection;
      Vector3 visibleLook = direction;

      // As with taxi preview, preserve the user's free-look offset while the authored
      // tangent changes underneath it. The rail owns translation; mouse/look keys own
      // the relative camera orientation.
      if (spaceFlypathCameraInitialized) {
        Vector3 currentVisible = -camera.Look;
        if (!IsFinite(currentVisible) || currentVisible.LengthSquared() < .000001f) currentVisible = previousDirection;
        else currentVisible.Normalize();
        Single yawOffset = WrapTaxiAngle(TaxiYaw(currentVisible) - TaxiYaw(previousDirection));
        Single pitchOffset = TaxiPitch(currentVisible) - TaxiPitch(previousDirection);
        Single yaw = TaxiYaw(direction) + yawOffset;
        Single pitch = Math.Max(-1.52f, Math.Min(1.52f, TaxiPitch(direction) + pitchOffset));
        visibleLook = TaxiDirectionFromYawPitch(yaw, pitch);
      }
      Vector3 up = Math.Abs(Vector3.Dot(visibleLook, Vector3.UnitY)) > .985f ? Vector3.UnitZ : Vector3.UnitY;
      camera.LookAt(position, position - visibleLook, up);
      spaceFlypathCameraInitialized = true;
      spaceFlypathDirection = direction;

      Single lookStep = 1.8f * Math.Max(0f, dt);
      if (Util.IsKeyDown(Keys.A) || Util.IsKeyDown(Keys.J)) camera.Yaw(lookStep);
      if (Util.IsKeyDown(Keys.D) || Util.IsKeyDown(Keys.L)) camera.Yaw(-lookStep);
      if (Util.IsKeyDown(Keys.I)) PitchPerspectiveCamera(lookStep);
      if (Util.IsKeyDown(Keys.K)) PitchPerspectiveCamera(-lookStep);

      currentCameraRoom = FindCameraRoom(camera.Position);
      return true;
    }

    private Boolean HandleSpaceFlypathMouseWheel(Int32 delta) {
      if (!spaceFlypathActive || spaceFlypathRoute == null) return false;
      spaceFlypathSpeedMultiplier = (Single)Math.Max(.1, Math.Min(16.0, spaceFlypathSpeedMultiplier * Math.Exp(delta * .001f)));
      if (Window is WorldBrowser browser)
        browser.SetStatusLabel("Space rail speed: " + spaceFlypathSpeedMultiplier.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "x" + SpaceFlypathControlHint());
      return true;
    }

    private void EndSpaceFlypath(String status) {
      Boolean wasActive = spaceFlypathActive || spaceFlypathRoute != null;
      spaceFlypathActive = false;
      spaceFlypathPath = null;
      spaceFlypathRoute = null;
      spaceFlypathElapsed = 0f;
      spaceFlypathPaused = false;
      spaceFlypathSpaceKeyWasDown = false;
      spaceFlypathEscapeKeyWasDown = false;
      spaceFlypathCameraInitialized = false;
      spaceFlypathDirection = Vector3.UnitZ;
      spaceFlypathSpeedMultiplier = 1f;
      spaceFlypathWaypointTemplates = new Dictionary<Int32, String>();
      spaceFlypathEncounterSpecs = Array.Empty<WorldSpaceCombatEncounterSpec>();
      spaceFlypathAuthoredTicks = Array.Empty<SpaceFlypathTickInfo>();
      spaceFlypathTicks = Array.Empty<SpaceFlypathTickInfo>();
      spaceCombatEncounterTimeline = Array.Empty<SpaceCombatEncounterTimelineInfo>();
      spaceCombatShips = Array.Empty<SpaceCombatShipRuntime>();
      if (wasActive) InvalidateTemporalHistory();
      if (!String.IsNullOrWhiteSpace(status) && Window is WorldBrowser browser) browser.SetStatusLabel(status);
    }


    // Jedipedia marks HYDRA trigger volumes on the Space Combat seekbar. PugTools already has exact trigger
    // transforms in its volume-membership index, so compute the first segment entry once when the rail starts and
    // publish an immutable snapshot to the WinForms menu. This does not add any new world-loading work.
    private SpaceFlypathTickInfo[] BuildSpaceFlypathHydraTicks(SpnMotionRoute route) {
      if (route?.Events == null || route.Events.Count == 0 || !(route.LegDuration > 0f) || volumeMembershipByKey.Count == 0)
        return Array.Empty<SpaceFlypathTickInfo>();
      var ticks = new System.Collections.Generic.List<SpaceFlypathTickInfo>();
      foreach (VolumeMembershipEntry volume in volumeMembershipByKey.Values.ToArray()) {
        if (volume == null || volume.IsRegion || volume.Instance == null ||
            !String.Equals(volume.ClassType, "HYDRA", StringComparison.OrdinalIgnoreCase)) continue;
        Single? firstTime = null;
        foreach (SpnMotionEvent ev in route.Events) {
          if (ev == null || ev.To < 0 || ev.From < 0 || ev.From >= route.Points.Count || ev.To >= route.Points.Count || !(ev.Duration > 0f)) continue;
          Vector3 a, b;
          try {
            a = Vector3.TransformCoordinate(route.Points[ev.From].Position, volume.Inverse);
            b = Vector3.TransformCoordinate(route.Points[ev.To].Position, volume.Inverse);
          } catch { continue; }
          if (!TrySpaceFlypathVolumeEntry(volume, a, b, out Single t)) continue;
          Single at = ev.Start + Math.Max(0f, Math.Min(1f, t)) * ev.Duration;
          if (!firstTime.HasValue || at < firstTime.Value) firstTime = at;
        }
        if (!firstTime.HasValue) continue;
        ticks.Add(new SpaceFlypathTickInfo {
          Kind = "HYDRA",
          Fqn = String.IsNullOrWhiteSpace(volume.Detail) ? (volume.AssetPath ?? String.Empty) : volume.Detail.Trim(),
          Label = String.IsNullOrWhiteSpace(volume.Detail) ? "HYDRA trigger" : volume.Detail.Trim(),
          Detail = "HYDRA volume" + (String.IsNullOrWhiteSpace(volume.Room?.RoomName) ? String.Empty : " in " + volume.Room.RoomName),
          TimeSeconds = firstTime.Value,
          Progress = Math.Max(0f, Math.Min(1f, firstTime.Value / route.LegDuration)),
          InstanceId = volume.Instance.ID,
          RoomName = volume.Room?.RoomName ?? String.Empty
        });
      }
      return ticks.OrderBy(x => x.Progress).ThenBy(x => x.Fqn, StringComparer.OrdinalIgnoreCase).ToArray();
    }


    private SpaceFlypathTickInfo[] BuildSpaceFlypathWaypointTicks(SpnMotionRoute route, AreaPath path, Dictionary<Int32, String> templates) {
      if (route == null || path?.Points == null || path.Points.Count == 0 || templates == null || templates.Count == 0 || !(route.LegDuration > 0f))
        return Array.Empty<SpaceFlypathTickInfo>();
      var ticks = new List<SpaceFlypathTickInfo>();
      Int32 routeIndex = 0;
      for (Int32 pointIndex = 0; pointIndex < path.Points.Count; pointIndex++) {
        AreaPathPoint point = path.Points[pointIndex];

        // Consume every authored control point in order, even when it has no template. That mirrors Jedipedia's
        // isControl walk and keeps duplicate/coincident authored points from making a later template bind to the
        // wrong occurrence on a rail that doubles back.
        Int32 matched = -1;
        for (Int32 i = routeIndex; i < route.Points.Count; i++) {
          if ((route.Points[i].Position - point.Position).LengthSquared() > .000001f) continue;
          matched = i;
          routeIndex = i + 1;
          break;
        }
        if (matched < 0) continue;
        Int32? templateId = SpaceFlypathWaypointTemplateId(point);
        if (!templateId.HasValue || !templates.TryGetValue(templateId.Value, out String fqn) || String.IsNullOrWhiteSpace(fqn)) continue;
        Single at = SpaceFlypathArrivalTime(route, matched);
        ticks.Add(new SpaceFlypathTickInfo {
          Kind = "WAYPOINT",
          Fqn = fqn.Trim(),
          Label = fqn.Trim(),
          Detail = "WaypointTemplateId=" + templateId.Value.ToString(CultureInfo.InvariantCulture) +
            "  •  point " + point.PointId.ToString(CultureInfo.InvariantCulture),
          TimeSeconds = at,
          Progress = Math.Max(0f, Math.Min(1f, at / route.LegDuration)),
          InstanceId = point.PointId,
          RoomName = String.Empty
        });
      }
      return ticks.OrderBy(x => x.Progress).ThenBy(x => x.Fqn, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static Int32? SpaceFlypathWaypointTemplateId(AreaPathPoint point) {
      Match match = Regex.Match(point?.Data ?? String.Empty, @"(?:^|;)\s*WaypointTemplateId\s*=\s*([0-9]+)\s*(?:;|$)", RegexOptions.IgnoreCase);
      return match.Success && Int32.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out Int32 value) ? value : (Int32?)null;
    }

    private static Single SpaceFlypathArrivalTime(SpnMotionRoute route, Int32 pointIndex) {
      if (route == null || pointIndex <= 0) return 0f;
      Single best = 0f;
      foreach (SpnMotionEvent ev in route.Events) {
        if (ev == null || ev.To != pointIndex) continue;
        Single arrival = ev.Start + ev.Duration;
        if (arrival > best) best = arrival;
      }
      return Math.Max(0f, Math.Min(route.LegDuration, best));
    }

    private void RebuildSpaceCombatEncounterTimeline() {
      if (!spaceFlypathActive || spaceFlypathRoute == null || !(spaceFlypathRoute.LegDuration > 0f) ||
          spaceFlypathEncounterSpecs == null || spaceFlypathEncounterSpecs.Length == 0 || spaceFlypathAuthoredTicks.Length == 0) {
        spaceCombatEncounterTimeline = Array.Empty<SpaceCombatEncounterTimelineInfo>();
        spaceFlypathTicks = spaceFlypathAuthoredTicks?.ToArray() ?? Array.Empty<SpaceFlypathTickInfo>();
        spaceCombatShips = Array.Empty<SpaceCombatShipRuntime>();
        return;
      }

      Single playerDuration = spaceFlypathRoute.LegDuration;
      var timeline = new List<SpaceCombatEncounterTimelineInfo>();
      var encounterTicks = new List<SpaceFlypathTickInfo>();
      var ships = new List<SpaceCombatShipRuntime>();
      foreach (WorldSpaceCombatEncounterSpec spec in spaceFlypathEncounterSpecs) {
        if (spec == null || String.IsNullOrWhiteSpace(spec.TriggerFqn) || String.IsNullOrWhiteSpace(spec.EncounterName)) continue;
        foreach (SpaceFlypathTickInfo trigger in spaceFlypathAuthoredTicks.Where(x => x != null &&
          String.Equals(x.Fqn, spec.TriggerFqn, StringComparison.OrdinalIgnoreCase))) {
          // Spawn delays are authored relative to the trigger and can be negative. Keep the true birth time: a ship
          // spawned before t=0 must already be part-way along its own spline when the player rail begins. Only the
          // seekbar marker is clamped to the visible 0..100% player range.
          Single delay = Single.IsNaN(spec.SpawnDelay) || Single.IsInfinity(spec.SpawnDelay) ? 0f : spec.SpawnDelay;
          Single start = trigger.TimeSeconds + delay;
          if (start > playerDuration + .0001f) continue;
          Single end = -1f;
          AreaPath shipPath = null;
          SpnMotionRoute shipRoute = null;
          Single speedScale = spec.SpeedScale > .0001f && !Single.IsNaN(spec.SpeedScale) && !Single.IsInfinity(spec.SpeedScale) ? spec.SpeedScale : 1f;
          if (!String.IsNullOrWhiteSpace(spec.PathName) && area?.Paths != null) {
            shipPath = area.Paths.FirstOrDefault(x => x?.Points != null && x.Points.Count >= 2 &&
              String.Equals(x.Name, spec.PathName, StringComparison.OrdinalIgnoreCase));
            shipRoute = BuildSpaceCombatShipMotionRoute(shipPath);
            if (shipRoute != null && shipRoute.LegDuration > 0f) end = start + shipRoute.LegDuration / speedScale;
          }

          Single startProgress = Math.Max(0f, Math.Min(1f, start / playerDuration));
          Boolean despawnsOnRail = end >= 0f && end <= playerDuration + .0001f;
          Single endProgress = despawnsOnRail ? Math.Max(0f, Math.Min(1f, end / playerDuration)) : -1f;
          String counts = spec.ShipCount > 0 ? spec.ShipCount.ToString(CultureInfo.InvariantCulture) + " free-flying ship" + (spec.ShipCount == 1 ? String.Empty : "s") : String.Empty;
          if (spec.BoltedCount > 0) counts += (counts.Length > 0 ? " + " : String.Empty) + spec.BoltedCount.ToString(CultureInfo.InvariantCulture) + " bolted component" + (spec.BoltedCount == 1 ? String.Empty : "s");
          String detail = (spec.Boss ? "Boss  •  " : String.Empty) + (String.IsNullOrWhiteSpace(counts) ? "Encounter" : counts) +
            "\nTrigger: " + spec.TriggerFqn +
            "\nSpawn delay: " + spec.SpawnDelay.ToString("0.###", CultureInfo.InvariantCulture) + " s" +
            "\nSpeed scale: " + spec.SpeedScale.ToString("0.###", CultureInfo.InvariantCulture) + "x" +
            (String.IsNullOrWhiteSpace(spec.FormationKind) ? String.Empty : "\nFormation: " + spec.FormationKind) +
            (String.IsNullOrWhiteSpace(spec.AnchorOffset) ? String.Empty : "\nAnchor offset: " + spec.AnchorOffset) +
            (String.IsNullOrWhiteSpace(spec.PathName) ? String.Empty : "\nPath: " + spec.PathName) +
            (String.IsNullOrWhiteSpace(spec.VehicleSpec) ? String.Empty : "\nVehicle: " + spec.VehicleSpec) +
            (String.IsNullOrWhiteSpace(spec.ModelPath) ? String.Empty : "\nModel: " + spec.ModelPath + "  ×" + spec.ModelScale.ToString("0.###", CultureInfo.InvariantCulture)) +
            (String.IsNullOrWhiteSpace(spec.EngineFx) ? String.Empty : "\nEngine FX: " + spec.EngineFx);
          var info = new SpaceCombatEncounterTimelineInfo {
            EncounterName = spec.EncounterName,
            TriggerFqn = spec.TriggerFqn,
            PathName = spec.PathName,
            VehicleSpec = spec.VehicleSpec,
            ModelPath = spec.ModelPath,
            EngineFx = spec.EngineFx,
            AnchorOffset = spec.AnchorOffset,
            FormationKind = spec.FormationKind,
            ModelScale = spec.ModelScale,
            SpeedScale = spec.SpeedScale,
            SpawnDelay = spec.SpawnDelay,
            Detail = detail,
            StartTime = start,
            EndTime = end,
            StartProgress = startProgress,
            EndProgress = endProgress,
            ShipCount = Math.Max(0, spec.ShipCount),
            BoltedCount = Math.Max(0, spec.BoltedCount),
            Boss = spec.Boss
          };
          timeline.Add(info);
          if (spec.Model != null && shipRoute != null && shipRoute.LegDuration > 0f && spec.ShipCount > 0) {
            Vector3[] slots = spec.FormationOffsets != null && spec.FormationOffsets.Count > 0
              ? spec.FormationOffsets.ToArray() : new[] { Vector3.Zero };
            Vector3 anchor = spec.HasAnchorOffset ? spec.AnchorOffsetVector : Vector3.Zero;
            for (Int32 slot = 0; slot < slots.Length; slot++) ships.Add(new SpaceCombatShipRuntime {
              Spec = spec,
              Model = spec.Model,
              Route = shipRoute,
              StartTime = start,
              EndTime = end,
              SpeedScale = speedScale,
              ModelScale = spec.ModelScale > 0f && !Single.IsNaN(spec.ModelScale) && !Single.IsInfinity(spec.ModelScale) ? spec.ModelScale : 1f,
              LocalOffset = slots[slot] + anchor,
              SlotIndex = slot
            });
          }
          encounterTicks.Add(new SpaceFlypathTickInfo {
            Kind = "SCE+",
            Fqn = spec.TriggerFqn,
            Label = spec.EncounterName,
            Detail = "Spawn\n" + detail,
            TimeSeconds = start,
            Progress = startProgress
          });
          if (despawnsOnRail) encounterTicks.Add(new SpaceFlypathTickInfo {
            Kind = "SCE-",
            Fqn = spec.TriggerFqn,
            Label = spec.EncounterName,
            Detail = "Despawn\n" + detail,
            TimeSeconds = end,
            Progress = endProgress
          });
        }
      }
      spaceCombatEncounterTimeline = timeline.OrderBy(x => x.StartTime).ThenBy(x => x.EncounterName, StringComparer.OrdinalIgnoreCase).ToArray();
      spaceCombatShips = ships.OrderBy(x => x.StartTime).ThenBy(x => x.Spec?.EncounterName, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.SlotIndex).ToArray();
      spaceFlypathTicks = spaceFlypathAuthoredTicks.Concat(encounterTicks)
        .OrderBy(x => x.Progress).ThenBy(x => SpaceFlypathTickOrder(x.Kind)).ThenBy(x => x.Label ?? x.Fqn, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static Int32 SpaceFlypathTickOrder(String kind) {
      if (String.Equals(kind, "HYDRA", StringComparison.OrdinalIgnoreCase)) return 0;
      if (String.Equals(kind, "WAYPOINT", StringComparison.OrdinalIgnoreCase)) return 1;
      if (String.Equals(kind, "SCE+", StringComparison.OrdinalIgnoreCase)) return 2;
      if (String.Equals(kind, "SCE-", StringComparison.OrdinalIgnoreCase)) return 3;
      return 4;
    }

    private Int32 SpaceCombatActiveShipCount(Single time) {
      SpaceCombatEncounterTimelineInfo[] timeline = spaceCombatEncounterTimeline;
      if (timeline == null || timeline.Length == 0) return 0;
      Int32 count = 0;
      foreach (SpaceCombatEncounterTimelineInfo encounter in timeline) {
        if (encounter == null || encounter.ShipCount <= 0 || time < encounter.StartTime) continue;
        if (encounter.EndTime >= 0f && time >= encounter.EndTime) continue;
        count += encounter.ShipCount;
      }
      return count;
    }

    private String SpaceFlypathTimelineMarkers(Int32 segments) {
      if (segments <= 0 || spaceFlypathTicks == null || spaceFlypathTicks.Length == 0) return String.Empty;
      Char[] marks = Enumerable.Repeat(' ', segments).ToArray();
      foreach (SpaceFlypathTickInfo tick in spaceFlypathTicks) {
        if (tick == null) continue;
        Int32 index = Math.Max(0, Math.Min(segments - 1, (Int32)Math.Round(Math.Max(0f, Math.Min(1f, tick.Progress)) * (segments - 1))));
        Char value = String.Equals(tick.Kind, "HYDRA", StringComparison.OrdinalIgnoreCase) ? 'H' :
          String.Equals(tick.Kind, "WAYPOINT", StringComparison.OrdinalIgnoreCase) ? 'W' :
          String.Equals(tick.Kind, "SCE+", StringComparison.OrdinalIgnoreCase) ? '+' :
          String.Equals(tick.Kind, "SCE-", StringComparison.OrdinalIgnoreCase) ? '-' : '|';
        marks[index] = marks[index] == ' ' || marks[index] == value ? value : '*';
      }
      return "[" + new String(marks) + "]  H/W trigger  +/- encounter";
    }

    private static Boolean TrySpaceFlypathVolumeEntry(VolumeMembershipEntry volume, Vector3 a, Vector3 b, out Single entry) {
      entry = 0f;
      if (volume == null) return false;
      Single hx = Math.Max(.0001f, volume.HalfWidth), hy = Math.Max(.0001f, volume.HalfHeight), hz = Math.Max(.0001f, volume.HalfDepth);
      if (volume.Ellipsoid) {
        Vector3 p = new Vector3(a.X / hx, a.Y / hy, a.Z / hz);
        Vector3 q = new Vector3(b.X / hx, b.Y / hy, b.Z / hz);
        if (p.LengthSquared() <= 1.0001f) { entry = 0f; return true; }
        Vector3 d = q - p;
        Single aa = Vector3.Dot(d, d);
        if (!(aa > .0000001f)) return false;
        Single bb = 2f * Vector3.Dot(p, d);
        Single cc = Vector3.Dot(p, p) - 1f;
        Single discriminant = bb * bb - 4f * aa * cc;
        if (discriminant < 0f) return false;
        Single root = (Single)Math.Sqrt(Math.Max(0f, discriminant));
        Single t0 = (-bb - root) / (2f * aa), t1 = (-bb + root) / (2f * aa);
        if (t0 >= 0f && t0 <= 1f) { entry = t0; return true; }
        if (t1 >= 0f && t1 <= 1f) { entry = t1; return true; }
        return false;
      }

      Vector3 dbox = b - a;
      Single tMin = 0f, tMax = 1f;
      if (!SpaceFlypathSlab(a.X, dbox.X, -hx, hx, ref tMin, ref tMax) ||
          !SpaceFlypathSlab(a.Y, dbox.Y, -hy, hy, ref tMin, ref tMax) ||
          !SpaceFlypathSlab(a.Z, dbox.Z, -hz, hz, ref tMin, ref tMax)) return false;
      entry = tMin;
      return tMax >= tMin && tMax >= 0f && tMin <= 1f;
    }

    private static Boolean SpaceFlypathSlab(Single origin, Single direction, Single min, Single max, ref Single tMin, ref Single tMax) {
      if (Math.Abs(direction) < .0000001f) return origin >= min && origin <= max;
      Single inv = 1f / direction;
      Single a = (min - origin) * inv, b = (max - origin) * inv;
      if (a > b) { Single swap = a; a = b; b = swap; }
      if (a > tMin) tMin = a;
      if (b < tMax) tMax = b;
      return tMax >= tMin;
    }

    private String SpaceFlypathControlHint() => "  •  free look, Space = pause, wheel = speed, Esc = stop";

    private static SpnMotionRoute BuildSpaceCombatShipMotionRoute(AreaPath path) {
      List<SpnMotionPoint> points = FlattenSpnMotionPoints(path);
      if (points.Count < 2) return null;
      var route = new SpnMotionRoute();
      route.Points.AddRange(points);
      Single timeline = 0f;
      for (Int32 i = 0; i + 1 < points.Count; i++) {
        Single length = (points[i + 1].Position - points[i].Position).Length();
        Single speed = Math.Max(.01f, points[i].Speed);
        Single duration = speed > 0f ? length / speed : 0f;
        if (!(duration > 0f) || Single.IsNaN(duration) || Single.IsInfinity(duration)) continue;
        route.Events.Add(new SpnMotionEvent { From = i, To = i + 1, Start = timeline, Duration = duration });
        timeline += duration;
      }
      if (!(timeline > 0f)) return null;
      route.LegDuration = timeline;
      return route;
    }

    private static Boolean TrySpaceCombatRoutePose(SpnMotionRoute route, Single time, out Vector3 position, out Vector3 rotation, out Vector3 direction) {
      position = Vector3.Zero; rotation = Vector3.Zero; direction = Vector3.UnitZ;
      if (route?.Events == null || route.Events.Count == 0 || route.Points == null || route.Points.Count == 0) return false;
      Single clamped = Math.Max(0f, Math.Min(route.LegDuration, time));
      SpnMotionEvent selected = route.Events[route.Events.Count - 1];
      foreach (SpnMotionEvent candidate in route.Events) {
        if (clamped < candidate.Start + candidate.Duration) { selected = candidate; break; }
      }
      if (selected.From < 0 || selected.From >= route.Points.Count) return false;
      SpnMotionPoint from = route.Points[selected.From];
      position = from.Position; rotation = from.Rotation;
      if (selected.To >= 0 && selected.To < route.Points.Count && selected.Duration > .000001f) {
        SpnMotionPoint to = route.Points[selected.To];
        Single t = Math.Max(0f, Math.Min(1f, (clamped - selected.Start) / selected.Duration));
        position = Vector3.Lerp(from.Position, to.Position, t);
        rotation = Vector3.Lerp(from.Rotation, to.Rotation, t);
        direction = NormalizeOrFallback(to.Position - from.Position, Vector3.UnitZ);
      } else {
        // A hold event still needs the tangent the ship had entering/leaving the point.
        direction = TaxiRouteDirection(route, clamped, route.LegDuration);
      }
      if (!IsFinite(position) || !IsFinite(direction)) return false;
      direction = NormalizeOrFallback(direction, Vector3.UnitZ);
      return true;
    }

    private static Matrix SpaceCombatShipWorld(SpaceCombatShipRuntime ship, Single playerTime) {
      Single tau = Math.Max(0f, playerTime - ship.StartTime);
      Single routeTime = tau * ship.SpeedScale;
      if (!TrySpaceCombatRoutePose(ship.Route, routeTime, out Vector3 position, out Vector3 rotation, out Vector3 direction)) return Matrix.Identity;
      Matrix frame = PathFollowerMatrix(position, rotation, direction);
      // DirectX uses row vectors. Translation * frame therefore applies the authored formation/anchor offset in the
      // ship's local tangent frame; model scale is deliberately before that translation so wing spacing is unscaled.
      return Matrix.Scaling(ship.ModelScale, ship.ModelScale, ship.ModelScale) * Matrix.Translation(ship.LocalOffset) * frame;
    }

    private void DrawSpaceCombatShips(Matrix vp, HashSet<String> visible, WorldRenderSettings s, AreaEnvironmentScheme cameraEnv, Boolean sceneShadows) {
      if (!spaceFlypathActive || spaceCombatShips == null || spaceCombatShips.Length == 0 || s == null || !s.ShowSpaceCombatShips ||
          s.Mode == WorldRenderMode.Map || s.Mode == WorldRenderMode.Heightmap) return;
      Single playerTime = spaceFlypathElapsed;
      FileFormats.Room environmentRoom = currentCameraRoom;
      if (environmentRoom != null) ApplyRoomEnvironment(environmentRoom, cameraEnv, s, sceneShadows);
      foreach (SpaceCombatShipRuntime ship in spaceCombatShips) {
        if (ship?.Model == null || ship.Route == null || playerTime < ship.StartTime) continue;
        Single tau = playerTime - ship.StartTime;
        if (tau < 0f || tau * ship.SpeedScale >= ship.Route.LegDuration) continue;
        Matrix world = SpaceCombatShipWorld(ship, playerTime);
        Vector3 lightPoint = new Vector3(world.M41, world.M42, world.M43);
        Single receiverRadius = 0f;
        if (TryModelSphere(ship.Model, world, out Vector3 center, out Single radius)) {
          lightPoint = center; receiverRadius = radius;
          if (!SphereVisibleInCameraFrustum(center, radius)) continue;
        }
        SetNearestLocalLights(lightPoint, environmentRoom, s, visible, false, false, false, receiverRadius);
        DrawModel(ship.Model, world, vp, s, false, null);
      }
    }

    private void DrawSpaceFlypathHud(WorldRenderSettings s) {
      if (!spaceFlypathActive || spaceFlypathRoute == null || s == null || s.Mode == WorldRenderMode.Map ||
          s.Mode == WorldRenderMode.Heightmap || !npcTextFontsRegistered || npcTextFont == null || npcTextSprite == null)
        return;
      Single duration = spaceFlypathRoute.LegDuration;
      if (!(duration > 0f) || Single.IsNaN(duration) || Single.IsInfinity(duration)) return;

      Int32 viewportWidth = Math.Max(1, (Int32)Viewport.Width);
      Int32 viewportHeight = Math.Max(1, (Int32)Viewport.Height);
      if (viewportWidth != npcNameplateViewportWidth || viewportHeight != npcNameplateViewportHeight) {
        npcNameplateViewportWidth = viewportWidth;
        npcNameplateViewportHeight = viewportHeight;
        try { npcTextSprite.RefreshViewport(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine("Space rail HUD viewport refresh failed: " + ex.Message); }
      }

      Single ratio = Math.Max(0f, Math.Min(1f, spaceFlypathElapsed / duration));
      const Int32 segments = 34;
      Int32 filled = Math.Max(0, Math.Min(segments, (Int32)Math.Round(ratio * segments)));
      String bar = "[" + new String('█', filled) + new String('░', segments - filled) + "]  " +
        (ratio * 100f).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "%";
      String state = spaceFlypathPaused ? (ratio >= .9999f ? "End" : "Paused") : "Playing";
      Int32 activeShips = SpaceCombatActiveShipCount(spaceFlypathElapsed);
      String detail = state + "  •  " + spaceFlypathSpeedMultiplier.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "x  •  " +
        (spaceFlypathPath?.Name ?? spaceFlypathLabel) + (spaceCombatEncounterTimeline.Length > 0 ? "  •  SCE " + activeShips.ToString(CultureInfo.InvariantCulture) + " active ships" : String.Empty);
      String markers = SpaceFlypathTimelineMarkers(segments);

      Single y = Math.Max(4f, viewportHeight - (String.IsNullOrWhiteSpace(markers) ? 84f : 102f)); // sit above the taxi HUD position if both states ever overlap
      Color4 shadow = new Color4(.92f, 0f, 0f, 0f);
      Color4 barColor = new Color4(1f, .58f, .86f, 1f);
      Color4 detailColor = new Color4(1f, .82f, .92f, 1f);
      DrawNpcText(bar, new Vector2(viewportWidth * .5f + 1f, y + 1f), shadow, "world-taxi");
      DrawNpcText(bar, new Vector2(viewportWidth * .5f, y), barColor, "world-taxi");
      if (!String.IsNullOrWhiteSpace(markers)) {
        DrawNpcText(markers, new Vector2(viewportWidth * .5f + 1f, y + 18f), shadow, "world-npc-item");
        DrawNpcText(markers, new Vector2(viewportWidth * .5f, y + 17f), detailColor, "world-npc-item");
      }
      Single detailY = y + (String.IsNullOrWhiteSpace(markers) ? 17f : 34f);
      DrawNpcText(detail, new Vector2(viewportWidth * .5f + 1f, detailY + 1f), shadow, "world-npc-item");
      DrawNpcText(detail, new Vector2(viewportWidth * .5f, detailY), detailColor, "world-npc-item");
      npcTextSprite.Flush();
    }
  }
}
