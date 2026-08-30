using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using FileFormats;
using GomLib;
using SlimDX;

namespace PugTools {
  public partial class WorldBrowser {
    private readonly object worldTaxiRoutesSync = new object();
    private readonly List<WorldTaxiRouteInfo> worldTaxiRoutes = new List<WorldTaxiRouteInfo>();
    private ToolStripMenuItem btnWorldTaxiRoutes;
    private ToolStripMenuItem btnWorldOrthographic;

    private sealed class WorldTaxiVehicleResolution {
      public string Spec;
      public string Appearance;
      public string ModelPath;
      public GR2 Model;
      public float Scale = 1f;
      public bool Fallback;
    }

    private void InitializeTaxiRoutesMenu() {
      btnWorldTaxiRoutes = new ToolStripMenuItem("Taxi routes") {
        ToolTipText = "Ride authored SWTOR taxi paths from tax.* / taxRoutePath data; generated taxi-like area paths are included as a fallback"
      };
      btnWorldTaxiRoutes.DropDownOpening += (_, __) => RebuildTaxiRoutesMenu();
      btnWorldNavigationMenu?.DropDownItems.Add(btnWorldTaxiRoutes);
    }

    private void RebuildTaxiRoutesMenu() {
      if (btnWorldTaxiRoutes == null) return;
      foreach (ToolStripItem old in btnWorldTaxiRoutes.DropDownItems.Cast<ToolStripItem>().ToArray()) old.Dispose();
      btnWorldTaxiRoutes.DropDownItems.Clear();

      bool active = panelRender?.IsTaxiRideActive == true;
      if (active) {
        string label = panelRender.CurrentTaxiRideLabel;
        var stop = new ToolStripMenuItem(String.IsNullOrWhiteSpace(label) ? "Stop current taxi ride" : "Stop: " + label) {
          ToolTipText = "Leave taxi ride mode and keep the camera at its current position (Esc)"
        };
        stop.Click += (_, __) => { panelRender?.StopTaxiRide(); ActivateWorldRenderInput(); };
        btnWorldTaxiRoutes.DropDownItems.Add(stop);
        btnWorldTaxiRoutes.DropDownItems.Add(new ToolStripSeparator());
      }

      var showTerminals = new ToolStripMenuItem("Show taxi droids / terminals") {
        CheckOnClick = true, Checked = worldSettings.ShowTaxiTerminals,
        ToolTipText = "Keep taxi droids visible and clickable independently from the general NPC layer. Enabled by default."
      };
      showTerminals.CheckedChanged += (_, __) => {
        if (updatingWorldToolbar) return;
        worldSettings.ShowTaxiTerminals = showTerminals.Checked;
        ApplyWorldSettings();
        SetStatusLabel(showTerminals.Checked ? "Taxi droids enabled." : "Taxi droids hidden.");
      };
      btnWorldTaxiRoutes.DropDownItems.Add(showTerminals);

      List<WorldTaxiRouteInfo> routes;
      lock (worldTaxiRoutesSync) routes = worldTaxiRoutes.Where(x => x?.Path?.Points != null && x.Path.Points.Count >= 2).ToList();
      if (routes.Count == 0) {
        btnWorldTaxiRoutes.DropDownItems.Add(new ToolStripSeparator());
        btnWorldTaxiRoutes.DropDownItems.Add(new ToolStripMenuItem(area == null ? "(load an area first)" : "(no taxi paths found in this area)") { Enabled = false });
        return;
      }

      var showVehicle = new ToolStripMenuItem("Show vehicle during ride") {
        CheckOnClick = true, Checked = worldSettings.ShowTaxiVehicle,
        ToolTipText = "Render the resolved taxi speeder/vehicle and use the third-person follow anchor. Enabled by default."
      };
      showVehicle.CheckedChanged += (_, __) => {
        if (updatingWorldToolbar) return;
        worldSettings.ShowTaxiVehicle = showVehicle.Checked;
        ApplyWorldSettings();
        SetStatusLabel(showVehicle.Checked ? "Taxi vehicle enabled." : "Taxi vehicle hidden; rides use the camera-on-route view.");
      };
      var help = new ToolStripMenuItem("Ride controls: mouse/look keys = free look • Space pause • wheel speed • Esc stop") { Enabled = false };
      btnWorldTaxiRoutes.DropDownItems.Add(new ToolStripSeparator());
      btnWorldTaxiRoutes.DropDownItems.Add(showVehicle);
      btnWorldTaxiRoutes.DropDownItems.Add(help);
      btnWorldTaxiRoutes.DropDownItems.Add(new ToolStripSeparator());

      foreach (var group in routes
        .GroupBy(x => String.IsNullOrWhiteSpace(x.SourceLabel) ? "Generated / area taxi paths" : x.SourceLabel)
        .OrderBy(x => x.Key, StringComparer.CurrentCultureIgnoreCase)) {
        var sourceMenu = new ToolStripMenuItem(group.Key);
        foreach (WorldTaxiRouteInfo route in group
          .OrderBy(x => x.DestinationLabel ?? x.Label ?? x.PathFqn, StringComparer.CurrentCultureIgnoreCase)) {
          WorldTaxiRouteInfo captured = route;
          string destination = !String.IsNullOrWhiteSpace(captured.DestinationLabel)
            ? captured.DestinationLabel
            : !String.IsNullOrWhiteSpace(captured.Label)
              ? captured.Label
              : TaxiFriendlyName(captured.PathFqn ?? captured.Path?.Name ?? "Taxi path");
          if (captured.Reversed) destination += "  (reverse)";
          var item = new ToolStripMenuItem(destination) {
            ToolTipText = TaxiRouteToolTip(captured)
          };
          item.Click += (_, __) => StartTaxiRoute(captured);
          sourceMenu.DropDownItems.Add(item);
        }
        btnWorldTaxiRoutes.DropDownItems.Add(sourceMenu);
      }
    }

    private void StartTaxiRoute(WorldTaxiRouteInfo route) {
      WorldTaxiRouteInfo firstLeg = TaxiRouteLegs(route).FirstOrDefault(r => r?.Path?.Points != null && r.Path.Points.Count >= 2);
      if (firstLeg == null || panelRender == null) return;
      // Taxi preview is a perspective camera ride. Walking/orthographic movement would fight the route camera.
      if (btnWorldWalkingMode?.Checked == true) btnWorldWalkingMode.Checked = false;
      else worldSettings.WalkingMode = false;
      if (btnWorldOrthographic?.Checked == true) btnWorldOrthographic.Checked = false;
      else worldSettings.OrthographicProjection = false;
      ApplyWorldSettings();
      panelRender.StopSpaceFlypath();
      panelRender.StartTaxiRide(route);
      ActivateWorldRenderInput();
      SetStatusLabel("Taxi ride: " + (route.Label ?? route.PathFqn ?? firstLeg.Path.Name) + "  •  mouse/look keys = free look, Space = pause, wheel = speed, Esc = stop");
    }

    internal void StartTaxiRouteFromMap(WorldTaxiRouteInfo route) {
      StartTaxiRoute(route);
    }

    /// <summary>
    /// A Ctrl+click selection may be an actual SWTOR taxi terminal placeable. plcTaxiTerminalSpec points straight
    /// at the same tax.* object used by the route graph, so this is much more reliable than guessing from model/name.
    /// When a client revision stores the direct tax.* object as the selected spawn reference, accept that as well.
    /// </summary>
    private bool TryOpenTaxiMapForSelectedObject() {
      if (panelRender == null || currentDom == null) return false;
      string selectedFqn = panelRender.SelectedWorldSpawnFqn;
      if (String.IsNullOrWhiteSpace(selectedFqn)) return false;
      string selectedName = panelRender.SelectedWorldSpawnName;
      string terminalFqn = null;
      object rawTerminal = null;
      try {
        if (selectedFqn.StartsWith("tax.", StringComparison.OrdinalIgnoreCase)) terminalFqn = selectedFqn;
        else {
          GomObject selected = currentDom.GetObject(selectedFqn);
          rawTerminal = TaxiDataValue(selected?.Data, "plcTaxiTerminalSpec", "4611686035128171095")
            ?? TaxiDataValue(selected?.Data, "taxTerminalSpec", "4611686035046870025");
          terminalFqn = ResolveTaxiReferenceName(rawTerminal);
        }
      } catch (Exception ex) {
        System.Diagnostics.Debug.WriteLine("Taxi terminal selection lookup failed for " + selectedFqn + ": " + ex.Message);
      }
      string hint = ((selectedFqn ?? String.Empty) + " " + (selectedName ?? String.Empty)).ToLowerInvariant();
      bool looksTaxi = hint.Contains("taxi") || hint.Contains("speeder") || hint.Contains("transport") || hint.Contains("shuttle");
      bool hasTerminalReference = !String.IsNullOrWhiteSpace(terminalFqn);
      if (!hasTerminalReference && !looksTaxi) return false;

      List<WorldTaxiRouteInfo> routes = new List<WorldTaxiRouteInfo>();
      if (hasTerminalReference) {
        lock (worldTaxiRoutesSync) routes = worldTaxiRoutes
          .Where(r => r?.FromTaxiGom == true && !String.IsNullOrWhiteSpace(r.SourceFqn) &&
            (String.Equals(r.SourceFqn.Trim(), terminalFqn.Trim(), StringComparison.OrdinalIgnoreCase) ||
             (rawTerminal != null && TaxiLookupKeyEquals(r.SourceFqn, rawTerminal))) &&
            r.Path?.Points != null && r.Path.Points.Count >= 2)
          .ToList();
      }
      if (routes.Count == 0 && hasTerminalReference) {
        // A few data builds expose plcTaxiTerminalSpec as an alias that resolves to the same terminal name only after
        // one more GOM dereference. Compare normalized friendly names as a conservative last resort, but never open
        // every taxi route just because the selected asset contains the word "taxi".
        string friendly = TaxiFriendlyName(terminalFqn);
        lock (worldTaxiRoutesSync) routes = worldTaxiRoutes
          .Where(r => r?.FromTaxiGom == true && r.Path?.Points != null && r.Path.Points.Count >= 2 &&
            (String.Equals(TaxiFriendlyName(r.SourceFqn), friendly, StringComparison.CurrentCultureIgnoreCase) ||
             (!String.IsNullOrWhiteSpace(selectedName) && String.Equals(r.SourceLabel, selectedName, StringComparison.CurrentCultureIgnoreCase))))
          .ToList();
      }
      if (routes.Count == 0 && looksTaxi && panelRender.TryGetSelectedWorldSpawnPosition(out float sx, out float sy, out float sz)) {
        // Last fallback for older GeneratedTaxi/terminal data: the authored taxi path starts at the terminal. Only use
        // this when the selected spawn actually looks taxi-related, and require a close spatial match so an arbitrary
        // placeable near a flight path cannot open the taxi UI.
        List<WorldTaxiRouteInfo> all; lock (worldTaxiRoutesSync) all = worldTaxiRoutes.Where(r => r?.Path?.Points != null && r.Path.Points.Count >= 2).ToList();
        string bestSource = null; float bestDistance2 = 25f; // 5 internal units / 50 game metres maximum.
        foreach (WorldTaxiRouteInfo candidate in all.Where(r => r.FromTaxiGom)) {
          AreaPathPoint start = candidate.Reversed ? candidate.Path.Points[candidate.Path.Points.Count - 1] : candidate.Path.Points[0];
          float dx = start.Position.X - sx, dy = start.Position.Y - sy, dz = start.Position.Z - sz;
          float d2 = dx * dx + dy * dy + dz * dz;
          if (d2 < bestDistance2) { bestDistance2 = d2; bestSource = candidate.SourceFqn; }
        }
        if (!String.IsNullOrWhiteSpace(bestSource)) routes = all.Where(r => r.FromTaxiGom && String.Equals(r.SourceFqn, bestSource, StringComparison.OrdinalIgnoreCase)).ToList();
      }
      if (routes.Count == 0) {
        string missingLabel = !String.IsNullOrWhiteSpace(selectedName) ? selectedName : hasTerminalReference ? TaxiFriendlyName(terminalFqn) : selectedFqn;
        SetStatusLabel("Taxi terminal selected, but no rideable route for " + missingLabel + " exists in this area.");
        return false;
      }
      // The authored tax.* table is an availability graph. Expand reachability here, then prefer the area's complete
      // source-to-destination Generated_Taxi spline instead of physically concatenating graph hops at stations.
      Vector3? selectedTaxiOrigin = null;
      if (panelRender.TryGetSelectedWorldSpawnPosition(out float taxiX, out float taxiY, out float taxiZ))
        selectedTaxiOrigin = new Vector3(taxiX, taxiY, taxiZ);
      List<WorldTaxiRouteInfo> reachable = BuildReachableTaxiRoutes(routes, selectedTaxiOrigin);
      if (reachable.Count > 0) routes = reachable;
      string label = routes.Select(r => r.SourceLabel).FirstOrDefault(x => !String.IsNullOrWhiteSpace(x))
        ?? (!String.IsNullOrWhiteSpace(selectedName) ? selectedName : hasTerminalReference ? TaxiFriendlyName(terminalFqn) : TaxiFriendlyName(selectedFqn));
      panelRender.OpenTaxiRouteMap(routes, label);
      // Opening the taxi UI consumes only the transient plain-click target. Persistent Ctrl+click inspection is a
      // separate gesture and must remain untouched, matching the rest of the World Browser.
      panelRender.ClearWorldInteractionTarget();
      ActivateWorldRenderInput();
      return true;
    }

    private static IEnumerable<WorldTaxiRouteInfo> TaxiRouteLegs(WorldTaxiRouteInfo route) {
      if (route == null) return Enumerable.Empty<WorldTaxiRouteInfo>();
      return route.Legs != null && route.Legs.Count > 0 ? route.Legs : new[] { route };
    }

    private const float TaxiPhysicalEndpointRadius = 5f;

    private sealed class WorldTaxiTerminalAnchor {
      public object RawSpec;
      public string SpecFqn;
      public string Label;
      public Vector3 Position;
    }

    /// <summary>
    /// Expand the selected terminal's authored availability graph, then map every reachable placed destination back
    /// to the shortest physical Generated_Taxi spline that actually leaves the clicked terminal. The tax.* graph is
    /// an availability graph, not necessarily a list of spline legs to concatenate: on planets such as Corellia a
    /// destination may be reachable through an intermediate graph node while area.dat already contains one direct
    /// source-to-destination flight. Concatenating graph links makes the preview fly to the intermediate station,
    /// turn around there, and only then continue even though the game uses the direct spline.
    ///
    /// If endpoint/terminal identity cannot be resolved safely, retain the old graph journey for that destination so
    /// older client revisions keep working rather than silently losing routes.
    /// </summary>
    private List<WorldTaxiRouteInfo> BuildReachableTaxiRoutes(List<WorldTaxiRouteInfo> selectedOutgoing, Vector3? selectedOrigin) {
      if (selectedOutgoing == null || selectedOutgoing.Count == 0) return new List<WorldTaxiRouteInfo>();
      string source = selectedOutgoing.Select(x => x?.SourceFqn).FirstOrDefault(x => !String.IsNullOrWhiteSpace(x));
      if (String.IsNullOrWhiteSpace(source)) return selectedOutgoing;

      List<WorldTaxiRouteInfo> graph;
      lock (worldTaxiRoutesSync) graph = worldTaxiRoutes
        .Where(x => x?.FromTaxiGom == true && !String.IsNullOrWhiteSpace(x.SourceFqn) && !String.IsNullOrWhiteSpace(x.DestinationFqn) &&
          x.Path?.Points != null && x.Path.Points.Count >= 2)
        .ToList();
      if (graph.Count == 0) return selectedOutgoing;

      var adjacency = graph.GroupBy(x => x.SourceFqn, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(x => x.Key, x => x.ToList(), StringComparer.OrdinalIgnoreCase);
      var hops = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { [source] = 0 };
      var tieCost = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase) { [source] = 0L };
      var previous = new Dictionary<string, WorldTaxiRouteInfo>(StringComparer.OrdinalIgnoreCase);
      var queue = new Queue<string>();
      queue.Enqueue(source);
      while (queue.Count > 0) {
        string from = queue.Dequeue();
        if (!adjacency.TryGetValue(from, out List<WorldTaxiRouteInfo> outgoing)) continue;
        foreach (WorldTaxiRouteInfo edge in outgoing) {
          string to = edge.DestinationFqn;
          if (String.IsNullOrWhiteSpace(to) || String.Equals(to, source, StringComparison.OrdinalIgnoreCase)) continue;
          int candidateHops = hops[from] + 1;
          long candidateCost = tieCost[from] + Math.Max(0, edge.Cost);
          bool better = !hops.TryGetValue(to, out int oldHops) || candidateHops < oldHops ||
            (candidateHops == oldHops && candidateCost < tieCost[to]);
          if (!better) continue;
          hops[to] = candidateHops;
          tieCost[to] = candidateCost;
          previous[to] = edge;
          queue.Enqueue(to);
        }
      }

      // Keep the graph journeys as a compatibility fallback and as the source for fare/vehicle metadata. They are no
      // longer assumed to describe the physical flight that should be rendered.
      var graphJourneys = new Dictionary<string, WorldTaxiRouteInfo>(StringComparer.OrdinalIgnoreCase);
      foreach (string destination in hops.Keys.Where(x => !String.Equals(x, source, StringComparison.OrdinalIgnoreCase))) {
        List<WorldTaxiRouteInfo> legs = BuildTaxiGraphLegs(source, destination, previous);
        if (legs.Count == 0) continue;
        graphJourneys[destination] = BuildTaxiGraphJourney(legs);
      }
      if (graphJourneys.Count == 0) return selectedOutgoing;

      Dictionary<string, WorldTaxiRouteInfo> physical = BuildPhysicalTaxiRoutes(source, selectedOrigin, graphJourneys, graph);
      var result = new List<WorldTaxiRouteInfo>();
      foreach (KeyValuePair<string, WorldTaxiRouteInfo> pair in graphJourneys) {
        if (physical.TryGetValue(pair.Key, out WorldTaxiRouteInfo direct)) result.Add(direct);
        else result.Add(pair.Value);
      }
      return result.OrderBy(x => x.HopCount)
        .ThenBy(x => x.DestinationLabel ?? x.Label ?? String.Empty, StringComparer.CurrentCultureIgnoreCase)
        .ToList();
    }

    private static List<WorldTaxiRouteInfo> BuildTaxiGraphLegs(string source, string destination,
      Dictionary<string, WorldTaxiRouteInfo> previous) {
      var legs = new List<WorldTaxiRouteInfo>();
      string cursor = destination;
      var guard = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      while (!String.Equals(cursor, source, StringComparison.OrdinalIgnoreCase) && guard.Add(cursor) &&
        previous.TryGetValue(cursor, out WorldTaxiRouteInfo edge)) {
        legs.Add(edge);
        cursor = edge.SourceFqn;
      }
      if (!String.Equals(cursor, source, StringComparison.OrdinalIgnoreCase) || legs.Count == 0) return new List<WorldTaxiRouteInfo>();
      legs.Reverse();
      return legs;
    }

    private static WorldTaxiRouteInfo BuildTaxiGraphJourney(List<WorldTaxiRouteInfo> legs) {
      if (legs == null || legs.Count == 0) return null;
      if (legs.Count == 1) return legs[0];
      WorldTaxiRouteInfo first = legs[0], last = legs[legs.Count - 1];
      bool costKnown = legs.All(x => x.Cost >= 0);
      long totalCost = costKnown ? legs.Sum(x => (long)x.Cost) : -1L;
      return new WorldTaxiRouteInfo {
        SourceFqn = first.SourceFqn,
        SourceLabel = first.SourceLabel,
        DestinationFqn = last.DestinationFqn,
        DestinationLabel = last.DestinationLabel,
        PathFqn = String.Join(" + ", legs.Select(x => x.PathFqn ?? x.Path?.Fqn ?? x.Path?.Name).Where(x => !String.IsNullOrWhiteSpace(x))),
        Path = first.Path,
        Reversed = first.Reversed,
        Cost = totalCost >= 0 && totalCost <= Int32.MaxValue ? (int)totalCost : -1,
        Label = (first.SourceLabel ?? TaxiFriendlyName(first.SourceFqn)) + " → " + (last.DestinationLabel ?? TaxiFriendlyName(last.DestinationFqn)),
        FromTaxiGom = true,
        VehicleSpec = first.VehicleSpec,
        VehicleAppearance = first.VehicleAppearance,
        VehicleModelPath = first.VehicleModelPath,
        VehicleModel = first.VehicleModel,
        VehicleScale = first.VehicleScale,
        VehicleFallback = first.VehicleFallback,
        Legs = legs
      };
    }

    /// <summary>
    /// One Generated_Taxi path is already the complete physical flight between two placed terminals. Use the terminal
    /// placements to identify the path's far endpoint, and choose the shortest complete spline for each destination.
    /// This mirrors the client data more closely than joining availability-graph edges at intermediate stations.
    /// </summary>
    private Dictionary<string, WorldTaxiRouteInfo> BuildPhysicalTaxiRoutes(string source, Vector3? selectedOrigin,
      Dictionary<string, WorldTaxiRouteInfo> graphJourneys, List<WorldTaxiRouteInfo> graph) {
      var result = new Dictionary<string, WorldTaxiRouteInfo>(StringComparer.OrdinalIgnoreCase);
      if (area?.Paths == null || graphJourneys == null || graphJourneys.Count == 0) return result;

      List<WorldTaxiTerminalAnchor> anchors = BuildTaxiTerminalAnchors(graph);
      Vector3? origin = selectedOrigin;
      if (!origin.HasValue) {
        WorldTaxiTerminalAnchor sourceAnchor = anchors
          .Where(x => TaxiTerminalAnchorMatches(x, source))
          .OrderBy(x => TaxiClosestOutgoingDistanceSquared(x.Position, graphJourneys.Values))
          .FirstOrDefault();
        if (sourceAnchor != null) origin = sourceAnchor.Position;
      }
      if (!origin.HasValue) return result;

      float endpointRadius2 = TaxiPhysicalEndpointRadius * TaxiPhysicalEndpointRadius;
      var bestLengths = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
      foreach (AreaPath path in area.Paths) {
        if (!LooksLikeGeneratedTaxiPath(path) || path?.Points == null || path.Points.Count < 2) continue;
        Vector3 start = path.Points[0].Position;
        if ((start - origin.Value).LengthSquared() > endpointRadius2) continue;
        Vector3 end = path.Points[path.Points.Count - 1].Position;
        string destination = TaxiTerminalSpecAt(anchors, end, endpointRadius2);
        if (String.IsNullOrWhiteSpace(destination) || String.Equals(destination, source, StringComparison.OrdinalIgnoreCase) ||
          !graphJourneys.TryGetValue(destination, out WorldTaxiRouteInfo graphJourney)) continue;

        float length = TaxiPathLength(path);
        if (!(length > 0f) || Single.IsNaN(length) || Single.IsInfinity(length)) continue;
        if (bestLengths.TryGetValue(destination, out float oldLength) && oldLength <= length) continue;
        bestLengths[destination] = length;

        WorldTaxiRouteInfo first = TaxiRouteLegs(graphJourney).FirstOrDefault() ?? graphJourney;
        result[destination] = new WorldTaxiRouteInfo {
          SourceFqn = source,
          SourceLabel = graphJourney.SourceLabel,
          DestinationFqn = destination,
          DestinationLabel = graphJourney.DestinationLabel,
          PathFqn = path.Fqn ?? path.Name,
          Path = path,
          Reversed = false,
          Cost = graphJourney.Cost,
          Label = (graphJourney.SourceLabel ?? TaxiFriendlyName(source)) + " → " +
            (graphJourney.DestinationLabel ?? TaxiFriendlyName(destination)),
          FromTaxiGom = true,
          VehicleSpec = first.VehicleSpec,
          VehicleAppearance = first.VehicleAppearance,
          VehicleModelPath = first.VehicleModelPath,
          VehicleModel = first.VehicleModel,
          VehicleScale = first.VehicleScale,
          VehicleFallback = first.VehicleFallback
          // Deliberately no Legs: this path is the complete authored physical flight.
        };
      }
      return result;
    }

    private List<WorldTaxiTerminalAnchor> BuildTaxiTerminalAnchors(List<WorldTaxiRouteInfo> graph) {
      var anchors = new List<WorldTaxiTerminalAnchor>();
      if (currentDom == null) return anchors;

      foreach (WorldNpcPlacement placement in worldNpcPlacements) {
        if (placement?.Instance == null || placement.Room == null || !placement.IsTaxiTerminal) continue;
        Vector3 position = TaxiNpcPlacementPosition(placement);
        AddTaxiTerminalAnchor(anchors, placement.SourceFqn, placement.Name, position);
      }
      foreach (WorldSpnPlacement placement in worldSpnPlacements) {
        if (placement?.Instance == null || placement.Room == null) continue;
        object raw = TaxiTerminalReference(placement.SourceFqn);
        if (raw == null && !WorldNpcLooksLikeTaxiTerminal(placement.SourceFqn, placement.Name, null)) continue;
        Matrix world = placement.Instance.GetAbsoluteTransform(placement.Room);
        AddTaxiTerminalAnchor(anchors, placement.SourceFqn, placement.Name, new Vector3(world.M41, world.M42, world.M43), raw);
      }

      // Normalize every reference to the actual tax.* names used by this area's graph. Some revisions store a stable
      // ID in plcTaxiTerminalSpec/taxTerminalSpec instead of a direct GOM object reference; TaxiLookupKeyEquals knows
      // how to compare that ID with the FNV stable ID of the graph FQN.
      List<string> graphSpecs = graph
        .SelectMany(x => new[] { x?.SourceFqn, x?.DestinationFqn })
        .Where(x => !String.IsNullOrWhiteSpace(x))
        .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
      foreach (WorldTaxiTerminalAnchor anchor in anchors) {
        List<string> matches = graphSpecs.Where(spec => TaxiTerminalAnchorMatches(anchor, spec)).ToList();
        if (matches.Count == 1) anchor.SpecFqn = matches[0];
      }

      // Older builds sometimes expose a taxi-looking NPC without a directly readable terminalSpec. Infer only from a
      // nearby authored graph endpoint, and only when the nearest endpoint is unique enough to avoid cross-faction
      // terminals sharing the same pad.
      foreach (WorldTaxiTerminalAnchor anchor in anchors.Where(x => String.IsNullOrWhiteSpace(x.SpecFqn)).ToList()) {
        string inferred = TaxiInferTerminalSpec(anchor.Position, graph);
        if (!String.IsNullOrWhiteSpace(inferred)) anchor.SpecFqn = inferred;
      }
      return anchors.Where(x => !String.IsNullOrWhiteSpace(x.SpecFqn)).ToList();
    }

    private void AddTaxiTerminalAnchor(List<WorldTaxiTerminalAnchor> anchors, string sourceFqn, string label, Vector3 position, object knownRaw = null) {
      object raw = knownRaw ?? TaxiTerminalReference(sourceFqn);
      string spec = ResolveTaxiReferenceName(raw);
      if (String.IsNullOrWhiteSpace(spec) && !String.IsNullOrWhiteSpace(sourceFqn) && sourceFqn.StartsWith("tax.", StringComparison.OrdinalIgnoreCase))
        spec = sourceFqn;
      anchors.Add(new WorldTaxiTerminalAnchor { RawSpec = raw, SpecFqn = spec, Label = label, Position = position });
    }

    private object TaxiTerminalReference(string sourceFqn) {
      if (String.IsNullOrWhiteSpace(sourceFqn) || currentDom == null) return null;
      if (sourceFqn.StartsWith("tax.", StringComparison.OrdinalIgnoreCase)) return sourceFqn;
      try {
        GomObject selected = currentDom.GetObject(sourceFqn);
        return TaxiDataValue(selected?.Data, "plcTaxiTerminalSpec", "4611686035128171095")
          ?? TaxiDataValue(selected?.Data, "taxTerminalSpec", "4611686035046870025");
      } catch { return null; }
    }

    private static Vector3 TaxiNpcPlacementPosition(WorldNpcPlacement placement) {
      if (placement?.SpawnPoints != null && placement.SpawnPoints.Count > 0) {
        unchecked {
          ulong seed = placement.Instance?.ID ?? 0UL;
          int index = (int)(seed % (ulong)placement.SpawnPoints.Count);
          return placement.SpawnPoints[index].Position;
        }
      }
      Matrix world = placement.Instance.GetAbsoluteTransform(placement.Room);
      return new Vector3(world.M41, world.M42, world.M43);
    }

    private static bool TaxiTerminalAnchorMatches(WorldTaxiTerminalAnchor anchor, string specFqn) {
      if (anchor == null || String.IsNullOrWhiteSpace(specFqn)) return false;
      if (!String.IsNullOrWhiteSpace(anchor.SpecFqn) && String.Equals(anchor.SpecFqn.Trim(), specFqn.Trim(), StringComparison.OrdinalIgnoreCase)) return true;
      return anchor.RawSpec != null && TaxiLookupKeyEquals(anchor.RawSpec, specFqn);
    }

    private static string TaxiTerminalSpecAt(List<WorldTaxiTerminalAnchor> anchors, Vector3 position, float maxDistanceSquared) {
      if (anchors == null || anchors.Count == 0) return null;
      WorldTaxiTerminalAnchor best = null;
      float bestDistance = maxDistanceSquared;
      var specs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      foreach (WorldTaxiTerminalAnchor anchor in anchors) {
        float distance = (anchor.Position - position).LengthSquared();
        if (distance > maxDistanceSquared) continue;
        string spec = anchor.SpecFqn;
        if (String.IsNullOrWhiteSpace(spec)) continue;
        specs.Add(spec);
        if (best == null || distance < bestDistance) { best = anchor; bestDistance = distance; }
      }
      // Different terminal specs inside the same endpoint radius are ambiguous. Failing closed here prevents nearby
      // Imperial/Republic droids from lending each other routes.
      if (best == null || specs.Count != 1) return null;
      return best.SpecFqn;
    }

    private static string TaxiInferTerminalSpec(Vector3 position, List<WorldTaxiRouteInfo> graph) {
      if (graph == null || graph.Count == 0) return null;
      float maxDistance2 = TaxiPhysicalEndpointRadius * TaxiPhysicalEndpointRadius;
      float bestDistance = maxDistance2;
      string bestSpec = null;
      bool ambiguous = false;
      foreach (WorldTaxiRouteInfo edge in graph) {
        if (edge?.Path?.Points == null || edge.Path.Points.Count < 2) continue;
        Vector3 start = edge.Reversed ? edge.Path.Points[edge.Path.Points.Count - 1].Position : edge.Path.Points[0].Position;
        Vector3 end = edge.Reversed ? edge.Path.Points[0].Position : edge.Path.Points[edge.Path.Points.Count - 1].Position;
        TaxiConsiderEndpoint(position, start, edge.SourceFqn, ref bestSpec, ref bestDistance, ref ambiguous);
        TaxiConsiderEndpoint(position, end, edge.DestinationFqn, ref bestSpec, ref bestDistance, ref ambiguous);
      }
      return ambiguous ? null : bestSpec;
    }

    private static void TaxiConsiderEndpoint(Vector3 position, Vector3 endpoint, string spec, ref string bestSpec, ref float bestDistance, ref bool ambiguous) {
      if (String.IsNullOrWhiteSpace(spec)) return;
      float distance = (position - endpoint).LengthSquared();
      if (distance > bestDistance + .0001f) return;
      if (distance + .0001f < bestDistance) {
        bestDistance = distance;
        bestSpec = spec;
        ambiguous = false;
      } else if (!String.IsNullOrWhiteSpace(bestSpec) && !String.Equals(bestSpec, spec, StringComparison.OrdinalIgnoreCase)) {
        ambiguous = true;
      }
    }

    private static float TaxiClosestOutgoingDistanceSquared(Vector3 position, IEnumerable<WorldTaxiRouteInfo> journeys) {
      float best = Single.MaxValue;
      foreach (WorldTaxiRouteInfo journey in journeys ?? Enumerable.Empty<WorldTaxiRouteInfo>()) {
        WorldTaxiRouteInfo first = TaxiRouteLegs(journey).FirstOrDefault();
        if (first?.Path?.Points == null || first.Path.Points.Count == 0) continue;
        Vector3 start = first.Reversed ? first.Path.Points[first.Path.Points.Count - 1].Position : first.Path.Points[0].Position;
        best = Math.Min(best, (start - position).LengthSquared());
      }
      return best;
    }

    private static float TaxiPathLength(AreaPath path) {
      if (path?.Points == null || path.Points.Count < 2) return 0f;
      float length = 0f;
      for (int i = 1; i < path.Points.Count; i++) length += (path.Points[i].Position - path.Points[i - 1].Position).Length();
      return length;
    }

    private static string TaxiRouteToolTip(WorldTaxiRouteInfo route) {
      if (route == null) return String.Empty;
      var bits = new List<string>();
      if (route.HopCount > 1) bits.Add(route.HopCount.ToString(CultureInfo.InvariantCulture) + " legs");
      if (!String.IsNullOrWhiteSpace(route.PathFqn)) bits.Add(route.PathFqn);
      if (route.Cost >= 0) bits.Add(route.Cost.ToString(CultureInfo.InvariantCulture) + " credits");
      if (route.FromTaxiGom) bits.Add("tax.* GOM route"); else bits.Add("generated/area path fallback");
      if (!String.IsNullOrWhiteSpace(route.VehicleModelPath)) bits.Add("vehicle: " + route.VehicleModelPath + (route.VehicleFallback ? " (fallback)" : String.Empty));
      else if (!String.IsNullOrWhiteSpace(route.VehicleSpec)) bits.Add("vehicle spec: " + route.VehicleSpec);
      return String.Join(" • ", bits);
    }

    /// <summary>
    /// Resolve the current area's taxi graph once while the area is already loading. SWTOR stores terminal links in
    /// tax.* objects (taxRoutes -> taxRoutePath/taxRouteDestination). The path itself is authored in area.dat, so only
    /// GOM routes whose path exists in this area are exposed. A conservative taxi-name fallback also catches generated
    /// taxi paths that are present in area.dat but are not reachable from the current client GOM build.
    /// </summary>
    private void LoadWorldTaxiRoutes() {
      var found = new List<WorldTaxiRouteInfo>();
      if (area?.Paths == null || currentDom == null) {
        lock (worldTaxiRoutesSync) { worldTaxiRoutes.Clear(); }
        return;
      }

      List<AreaPath> areaPaths = area.Paths.Where(x => x?.Points != null && x.Points.Count >= 2).ToList();
      var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      var vehicleCache = new Dictionary<string, WorldTaxiVehicleResolution>(StringComparer.OrdinalIgnoreCase);
      StringTable terminalNames = null;
      try { terminalNames = currentDom.StringTable.Find("str.tax.terminals"); } catch { }

      try {
        foreach (GomObject terminal in currentDom.GetObjectsStartingWith("tax.")) {
          if (terminal == null) continue;
          GomObjectData data;
          try { data = terminal.Data; } catch { continue; }
          if (data == null) continue;

          string sourceFqn = terminal.Name;
          string sourceLabel = TaxiTerminalDisplayName(terminal, terminalNames);
          var routeRows = new List<GomObjectData>();
          if (TaxiDataValue(data, "taxRoutePath", "4611686034424570033") != null) routeRows.Add(data);
          object rawRoutes = TaxiDataValue(data, "taxRoutes", "4611686034424570035");
          if (rawRoutes != null) routeRows.AddRange(EnumerateTaxiRouteRows(rawRoutes));

          foreach (GomObjectData row in routeRows) {
            if (row == null) continue;
            object rawPath = TaxiDataValue(row, "taxRoutePath", "4611686034424570033")
              ?? TaxiDataValue(row, "taxUnknownPath", "4611686142554550000")
              ?? TaxiDataValue(row, "taxPathName", "4611686319507014001");
            string pathFqn = ResolveTaxiReferenceName(rawPath);
            AreaPath matchedPath = FindTaxiAreaPath(areaPaths, pathFqn);
            if (matchedPath == null) continue;

            object rawDestination = TaxiDataValue(row, "taxRouteDestination", "4611686034424570031");
            GomObject destinationObject = ResolveTaxiObject(rawDestination);
            string destinationFqn = destinationObject?.Name ?? ResolveTaxiReferenceName(rawDestination);
            string destinationLabel = destinationObject != null
              ? TaxiTerminalDisplayName(destinationObject, terminalNames)
              : TaxiFriendlyName(destinationFqn);
            bool reversed = TaxiBool(TaxiDataValue(row, "taxRoutePathIsReversed", "4611686034424570032"));
            int cost = TaxiInt(TaxiDataValue(row, "taxRouteCost", "4611686034424570029"), -1);
            object rawVehicleSpec = TaxiDataValue(row, "taxVehicleSpec", "4611686141951440000")
              ?? TaxiDataValue(data, "taxVehicleSpec", "4611686141951440000");
            WorldTaxiVehicleResolution vehicle = ResolveTaxiVehicle(rawVehicleSpec, vehicleCache);
            if (vehicle == null || vehicle.Model == null)
              vehicle = ResolveTaxiFallbackVehicle(vehicle, rawVehicleSpec, sourceFqn, sourceLabel, destinationFqn, destinationLabel, matchedPath, vehicleCache);

            string key = String.Join("|", sourceFqn ?? String.Empty, destinationFqn ?? String.Empty,
              matchedPath.Id.ToString(CultureInfo.InvariantCulture), reversed ? "1" : "0");
            if (!seen.Add(key)) continue;
            string label = !String.IsNullOrWhiteSpace(sourceLabel) && !String.IsNullOrWhiteSpace(destinationLabel)
              ? sourceLabel + " → " + destinationLabel
              : TaxiFriendlyName(matchedPath.Fqn ?? matchedPath.Name);
            found.Add(new WorldTaxiRouteInfo {
              SourceFqn = sourceFqn,
              SourceLabel = sourceLabel,
              DestinationFqn = destinationFqn,
              DestinationLabel = destinationLabel,
              PathFqn = !String.IsNullOrWhiteSpace(pathFqn) ? pathFqn : matchedPath.Fqn,
              Path = matchedPath,
              Reversed = reversed,
              Cost = cost,
              Label = label,
              FromTaxiGom = true,
              VehicleSpec = vehicle?.Spec,
              VehicleAppearance = vehicle?.Appearance,
              VehicleModelPath = vehicle?.ModelPath,
              VehicleModel = vehicle?.Model,
              VehicleScale = vehicle?.Scale ?? 1f,
              VehicleFallback = vehicle?.Fallback == true
            });
          }
        }
      } catch (Exception ex) {
        System.Diagnostics.Debug.WriteLine("Taxi GOM route discovery failed: " + ex.Message);
      }

      // GeneratedTaxi paths have existed in several client revisions without a convenient terminal route reachable
      // from the current tax.* object graph. Only add strongly taxi-named area paths, and only if that exact path was
      // not already represented by a real taxRoutePath above.
      foreach (AreaPath path in areaPaths) {
        if (!LooksLikeGeneratedTaxiPath(path)) continue;
        if (found.Any(x => x.Path == path)) continue;
        string key = "fallback|" + path.Id.ToString(CultureInfo.InvariantCulture);
        if (!seen.Add(key)) continue;
        string friendly = TaxiFriendlyName(path.Name ?? path.Fqn);
        WorldTaxiVehicleResolution fallbackVehicle = ResolveTaxiFallbackVehicle(null, null,
          "Generated / area taxi paths", null, path.Fqn, friendly, path, vehicleCache);
        found.Add(new WorldTaxiRouteInfo {
          SourceLabel = "Generated / area taxi paths",
          DestinationLabel = friendly,
          PathFqn = path.Fqn,
          Path = path,
          Label = friendly,
          FromTaxiGom = false,
          VehicleSpec = fallbackVehicle?.Spec,
          VehicleAppearance = fallbackVehicle?.Appearance,
          VehicleModelPath = fallbackVehicle?.ModelPath,
          VehicleModel = fallbackVehicle?.Model,
          VehicleScale = fallbackVehicle?.Scale ?? 1f,
          VehicleFallback = fallbackVehicle?.Fallback == true
        });
      }

      found = found
        .OrderBy(x => x.SourceLabel ?? String.Empty, StringComparer.CurrentCultureIgnoreCase)
        .ThenBy(x => x.DestinationLabel ?? x.Label ?? String.Empty, StringComparer.CurrentCultureIgnoreCase)
        .ToList();

      // Vehicle GR2s are not ordinary area placements, but registering them in the world model set ensures their GPU
      // buffers/materials are prepared with the rest of the scene. No room instance references these synthetic keys,
      // so they can only appear through the active taxi ride renderer.
      RegisterTaxiVehicleModels(found);
      lock (worldTaxiRoutesSync) {
        worldTaxiRoutes.Clear();
        worldTaxiRoutes.AddRange(found);
      }
      System.Diagnostics.Debug.WriteLine("World taxi routes resolved for area " + area.Id + ": " + found.Count);
    }

    private WorldTaxiVehicleResolution ResolveTaxiVehicle(object rawSpec, Dictionary<string, WorldTaxiVehicleResolution> cache) {
      if (rawSpec == null || currentDom == null || currentAssets == null) return null;
      string cacheKey = TaxiLookupKeyText(rawSpec);
      if (cache != null && cache.TryGetValue(cacheKey, out WorldTaxiVehicleResolution cached)) return cached;

      var result = new WorldTaxiVehicleResolution { Spec = TaxiReferenceDisplay(rawSpec) };
      try {
        // taxVehicleSpec is normally a stable-id key into spnVehicleDataPrototype, but some client revisions expose
        // a direct GOM reference instead. Accept both. The old code only tried the class name spnVehicleProtoData;
        // that is not necessarily the name of the live prototype object and was the main reason no taxi model was
        // resolved on current clients.
        GomObjectData vehicleData = TaxiObjectData(rawSpec);
        if (vehicleData == null) {
          GomObject prototype = FindTaxiPrototypeWithDataField("spnVehicleData", "4611686067808231196", "spnvehicle",
            "spnVehicleDataPrototype", "spnVehicleProtoData");
          System.Collections.IDictionary vehicleTable = TaxiDataTable(prototype, "spnVehicleData", "4611686067808231196");
          vehicleData = TaxiObjectData(FindTaxiTableValue(vehicleTable, rawSpec));
        }

        object appearanceRef = TaxiDataValue(vehicleData, "vehAppearancePackage", "4611686051888871104")
          ?? TaxiDataValue(vehicleData, "vehAppearancePrototype", "4611686062111631213");
        object modelSpec = TaxiDataValue(vehicleData, "spnVehicleModelSpec", "4611686348008047002");

        // Direct model/spec references are useful on newer data and also cover wrappers that ultimately expose a
        // vehAppModel. CollectSpnModels already knows how to traverse GR2, MAG and GOM visual references.
        result.Model = ResolveTaxiModelReference(modelSpec, out string modelPath);
        if (result.Model != null) result.ModelPath = modelPath;

        GomObjectData appearanceData = TaxiObjectData(appearanceRef);
        if (appearanceData == null && appearanceRef != null) {
          GomObject appearancePrototype = FindTaxiPrototypeWithDataField("vehAppearanceData", "4611686062111631212", "vehappearance",
            "vehAppearanceDataPrototype", "vehAppearancePackagePrototype", "vehAppearancePrototype", "vehAppearanceProtoData");
          System.Collections.IDictionary appearanceTable = TaxiDataTable(appearancePrototype, "vehAppearanceData", "4611686062111631212");
          appearanceData = TaxiObjectData(FindTaxiTableValue(appearanceTable, appearanceRef));
        }

        // A few client revisions insert one more appearance-table indirection. Resolve it through the discovered
        // live prototype instead of assuming a hard-coded object name.
        if (appearanceData != null) {
          object nestedAppearance = TaxiDataValue(appearanceData, "vehAppearancePrototype", "4611686062111631213");
          if (TaxiDataValue(appearanceData, "vehAppModel", "4611686061988131201") == null && nestedAppearance != null) {
            GomObject appearancePrototype = FindTaxiPrototypeWithDataField("vehAppearanceData", "4611686062111631212", "vehappearance",
              "vehAppearanceDataPrototype", "vehAppearancePackagePrototype", "vehAppearancePrototype", "vehAppearanceProtoData");
            System.Collections.IDictionary appearanceTable = TaxiDataTable(appearancePrototype, "vehAppearanceData", "4611686062111631212");
            GomObjectData nestedData = TaxiObjectData(FindTaxiTableValue(appearanceTable, nestedAppearance));
            if (nestedData != null) { appearanceRef = nestedAppearance; appearanceData = nestedData; }
          }
        }

        result.Appearance = TaxiReferenceDisplay(appearanceRef);
        object appModelRef = TaxiDataValue(appearanceData, "vehAppModel", "4611686061988131201")
          ?? TaxiDataValue(vehicleData, "vehAppModel", "4611686061988131201");
        if (result.Model == null) {
          result.Model = ResolveTaxiModelReference(appModelRef, out modelPath);
          if (result.Model != null) result.ModelPath = modelPath;
        }
        // Some appearance references are actual named wrapper objects rather than table keys. Let the existing
        // visual resolver walk those too if the table lookup did not expose vehAppModel directly.
        if (result.Model == null) {
          result.Model = ResolveTaxiModelReference(appearanceRef, out modelPath);
          if (result.Model != null) result.ModelPath = modelPath;
        }

        result.Scale = TaxiFloat(TaxiDataValue(appearanceData, "vehAppScale", "4611686062111631191"), 1f);
        if (!(result.Scale > 0f) || Single.IsNaN(result.Scale) || Single.IsInfinity(result.Scale)) result.Scale = 1f;
      } catch (Exception ex) {
        System.Diagnostics.Debug.WriteLine("Taxi vehicle resolution failed for " + result.Spec + ": " + ex.Message);
      }

      // Cache failures as well. A terminal normally owns many routes and repeatedly walking the prototype tables for
      // the same unknown vehicle spec would otherwise make area loading noticeably slower.
      if (cache != null) cache[cacheKey] = result;
      return result;
    }

    private GomObject FindTaxiPrototypeWithDataField(string fieldName, string numericFieldName, string nameHint, params string[] preferredNames) {
      if (currentDom == null) return null;
      Func<GomObject, bool> hasField = node => {
        try { return node?.Data != null && TaxiDataValue(node.Data, fieldName, numericFieldName) is System.Collections.IDictionary; }
        catch { return false; }
      };

      if (preferredNames != null) foreach (string name in preferredNames) {
        if (String.IsNullOrWhiteSpace(name)) continue;
        try { GomObject node = currentDom.GetObject(name); if (hasField(node)) return node; } catch { }
      }

      // Prototype object names have moved over the lifetime of the client while the table field itself stayed stable.
      // Search only likely names first, then all *Prototype instances as a conservative last resort.
      try {
        IEnumerable<string> names = currentDom.GetAllInstanceNames().Keys;
        foreach (string name in names.Where(x => !String.IsNullOrWhiteSpace(x) &&
          x.IndexOf(nameHint ?? String.Empty, StringComparison.OrdinalIgnoreCase) >= 0)) {
          try { GomObject node = currentDom.GetObject(name); if (hasField(node)) return node; } catch { }
        }
        foreach (string name in names.Where(x => !String.IsNullOrWhiteSpace(x) &&
          x.IndexOf("prototype", StringComparison.OrdinalIgnoreCase) >= 0)) {
          try { GomObject node = currentDom.GetObject(name); if (hasField(node)) return node; } catch { }
        }
      } catch { }
      return null;
    }

    private static System.Collections.IDictionary TaxiDataTable(GomObject prototype, string fieldName, string numericFieldName) {
      if (prototype?.Data == null) return null;
      return TaxiDataValue(prototype.Data, fieldName, numericFieldName) as System.Collections.IDictionary;
    }

    private GR2 ResolveTaxiModelReference(object raw, out string modelPath) {
      modelPath = null;
      if (raw == null || currentAssets == null) return null;
      if (raw is GomObjectData inline) {
        foreach (string field in new[] { "vehAppModel", "spnVehicleModelSpec", "plcModel", "plcModelAssetSpec", "dynVisualFqn" }) {
          object value = TaxiDataValue(inline, field, field == "vehAppModel" ? "4611686061988131201" : field == "spnVehicleModelSpec" ? "4611686348008047002" : null);
          GR2 nested = ResolveTaxiModelReference(value, out modelPath);
          if (nested != null) return nested;
        }
        return null;
      }

      string reference;
      if (raw is GomObject node) reference = node.Name;
      else reference = raw.ToString()?.Trim();
      if (String.IsNullOrWhiteSpace(reference) || TryTaxiUInt64(reference, out _)) return null;

      try {
        var candidates = new List<GR2>();
        WorldNpcAnimationClip unusedAnimation = null;
        CollectSpnModels(reference, candidates, new HashSet<string>(StringComparer.OrdinalIgnoreCase), ref unusedAnimation);
        GR2 model = candidates.FirstOrDefault(x => x != null);
        if (model == null) return null;
        string direct = TaxiModelPathFromValue(raw, false);
        modelPath = !String.IsNullOrWhiteSpace(direct) ? direct : model.filename;
        return model;
      } catch (Exception ex) {
        System.Diagnostics.Debug.WriteLine("Taxi model reference failed " + reference + ": " + ex.Message);
        return null;
      }
    }

    private WorldTaxiVehicleResolution ResolveTaxiFallbackVehicle(WorldTaxiVehicleResolution existing, object rawSpec,
      string sourceFqn, string sourceLabel, string destinationFqn, string destinationLabel, AreaPath path,
      Dictionary<string, WorldTaxiVehicleResolution> cache) {
      if (existing?.Model != null) return existing;
      string hint = String.Join(" ", new[] {
        existing?.Spec, TaxiReferenceDisplay(rawSpec), sourceFqn, sourceLabel, destinationFqn, destinationLabel,
        path?.Fqn, path?.Name
      }.Where(x => !String.IsNullOrWhiteSpace(x))).ToLowerInvariant();

      string category = "airspeeder";
      if (hint.Contains("thranta")) category = "thranta";
      else if (hint.Contains("escape") || hint.Contains("pod")) category = "escapepod";
      else if (hint.Contains("rocket") || hint.Contains("sled")) category = "rocketsled";
      else if (hint.Contains("bike")) category = "bike";
      else if (hint.Contains("van")) category = "van";
      else if (hint.Contains("ship")) category = "ship";
      else if (hint.Contains("imperial") || hint.Contains("_imp") || hint.Contains(" imp ")) category = "airspeeder_imp";

      string fallbackKey = "fallback|" + category;
      if (cache != null && cache.TryGetValue(fallbackKey, out WorldTaxiVehicleResolution cached) && cached?.Model != null) {
        return new WorldTaxiVehicleResolution {
          Spec = existing?.Spec ?? TaxiReferenceDisplay(rawSpec),
          Appearance = existing?.Appearance,
          ModelPath = cached.ModelPath,
          Model = cached.Model,
          Scale = cached.Scale,
          Fallback = true
        };
      }

      var references = new List<string>();
      // Prefer the concrete MAG mesh where its shipped filename is known. This bypasses malformed/legacy MAG text
      // wrappers entirely and makes the visual fallback useful even on clients whose spec parser cannot decode the
      // corresponding .mag. Keep the MAG itself as a secondary candidate for clients where the concrete mesh path
      // differs but the authored wrapper is still available.
      switch (category) {
        case "thranta":
          references.Add("/art/dynamic/mag/model/mag_taxithranta_a01.gr2");
          references.Add("/art/dynamic/spec/mag_taxithranta_a01.mag");
          break;
        case "escapepod":
          references.Add("/art/dynamic/mag/model/mag_taxiescapepod_v01.gr2");
          references.Add("/art/dynamic/spec/mag_taxiescapepod_v01.mag");
          break;
        case "rocketsled":
          references.Add("/art/dynamic/mag/model/mag_taxirocketsled_v01.gr2");
          references.Add("/art/dynamic/spec/mag_taxirocketsled_v01.mag");
          break;
        case "bike":
          references.Add("/art/dynamic/mag/model/mag_taxispeederbike02_v01.gr2");
          references.Add("/art/dynamic/spec/mag_taxispeederbike02_v01.mag");
          break;
        case "van":
          references.Add("/art/dynamic/mag/model/mag_taxispeedervan_neu.gr2");
          references.Add("/art/dynamic/spec/mag_taxispeedervan_neu.mag");
          break;
        case "ship":
          references.Add("/art/dynamic/mag/model/taxi_veh_neu_ship_small_02.gr2");
          references.Add("/art/dynamic/spec/taxi_veh_neu_ship_small_02.mag");
          break;
        case "airspeeder_imp":
          references.Add("/art/dynamic/spec/mag_taxi_airspeeder_imp.mag");
          break;
      }
      references.Add("/art/dynamic/mag/model/mag_taxi_airspeeder_v01.gr2");
      references.Add("/art/dynamic/spec/mag_taxi_airspeeder_v01.mag");
      references.Add("/art/dynamic/mag/model/mag_taxispeederneu_v01.gr2");
      references.Add("/art/dynamic/spec/mag_taxispeederneu_v01.mag");
      references.Add("/art/dynamic/mag/model/mag_taxispeederbike02_v01.gr2");
      references.Add("/art/dynamic/spec/mag_taxispeederbike02_v01.mag");

      WorldTaxiVehicleResolution result = existing ?? new WorldTaxiVehicleResolution { Spec = TaxiReferenceDisplay(rawSpec) };
      foreach (string reference in references.Distinct(StringComparer.OrdinalIgnoreCase)) {
        GR2 model = ResolveTaxiModelReference(reference, out string modelPath);
        if (model == null) continue;
        result.Model = model;
        result.ModelPath = modelPath ?? model.filename ?? reference;
        result.Scale = 1f;
        result.Fallback = true;
        break;
      }
      if (cache != null && result.Model != null) cache[fallbackKey] = result;
      return result;
    }

    private GomObject GetTaxiPrototype(params string[] names) {
      if (currentDom == null || names == null) return null;
      foreach (string name in names) {
        if (String.IsNullOrWhiteSpace(name)) continue;
        try { GomObject node = currentDom.GetObject(name); if (node != null) return node; } catch { }
      }
      return null;
    }

    private void RegisterTaxiVehicleModels(IEnumerable<WorldTaxiRouteInfo> routes) {
      if (routes == null || models == null) return;
      var registered = new HashSet<GR2>();
      ulong synthetic = UInt64.MaxValue;
      foreach (WorldTaxiRouteInfo route in routes) {
        GR2 model = route?.VehicleModel;
        if (model == null || !registered.Add(model)) continue;
        while (models.ContainsKey(synthetic) && synthetic > 0) synthetic--;
        if (synthetic == 0 && models.ContainsKey(0)) break;
        models[synthetic] = model;
        if (synthetic > 0) synthetic--;
      }
    }

    private GomObjectData TaxiObjectData(object raw) {
      if (raw == null) return null;
      if (raw is GomObjectData data) return data;
      if (raw is GomObject node) { try { return node.Data; } catch { return null; } }
      // Only treat a raw value as a node reference if it really resolves. Stable IDs used as prototype table keys are
      // not GOM object IDs, so failure here simply falls through to table lookup.
      try {
        GomObject resolved = ResolveTaxiObject(raw);
        return resolved?.Data;
      } catch { return null; }
    }

    private static object FindTaxiTableValue(System.Collections.IDictionary table, object lookup) {
      if (table == null || lookup == null) return null;
      try { if (table.Contains(lookup)) return table[lookup]; } catch { }
      foreach (System.Collections.DictionaryEntry pair in table)
        if (TaxiLookupKeyEquals(pair.Key, lookup)) return pair.Value;
      return null;
    }

    private static bool TaxiLookupKeyEquals(object left, object right) {
      if (left == null || right == null) return false;
      string l = left.ToString()?.Trim(), r = right.ToString()?.Trim();
      if (!String.IsNullOrWhiteSpace(l) && !String.IsNullOrWhiteSpace(r) && String.Equals(l, r, StringComparison.OrdinalIgnoreCase)) return true;
      bool ln = TryTaxiUInt64(left, out ulong lu), rn = TryTaxiUInt64(right, out ulong ru);
      if (ln && rn && lu == ru) return true;
      if (!ln && !String.IsNullOrWhiteSpace(l) && rn && TaxiStableId(l) == ru) return true;
      if (!rn && !String.IsNullOrWhiteSpace(r) && ln && TaxiStableId(r) == lu) return true;
      return false;
    }

    private static bool TryTaxiUInt64(object raw, out ulong value) {
      value = 0;
      if (raw == null) return false;
      try {
        switch (raw) {
          case ulong u: value = u; return true;
          case long l: value = unchecked((ulong)l); return true;
          case uint u32: value = u32; return true;
          case int i32: value = unchecked((ulong)(long)i32); return true;
          case ushort u16: value = u16; return true;
          case short i16: value = unchecked((ulong)(long)i16); return true;
          case byte u8: value = u8; return true;
          case sbyte i8: value = unchecked((ulong)(long)i8); return true;
        }
        string text = raw.ToString()?.Trim();
        if (UInt64.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value)) return true;
        if (Int64.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long signed)) { value = unchecked((ulong)signed); return true; }
      } catch { }
      return false;
    }

    // SWTOR stable IDs use upper-case FNV-1a 64-bit over character codes (same algorithm as Jedipedia's fnv1a64).
    private static ulong TaxiStableId(string text) {
      const ulong offset = 14695981039346656037UL;
      const ulong prime = 1099511628211UL;
      ulong hash = offset;
      foreach (char c in (text ?? String.Empty).ToUpperInvariant()) { hash ^= c; hash = unchecked(hash * prime); }
      return hash;
    }

    private static string TaxiLookupKeyText(object raw) {
      if (raw == null) return String.Empty;
      if (TryTaxiUInt64(raw, out ulong numeric)) return numeric.ToString(CultureInfo.InvariantCulture);
      return raw.ToString()?.Trim() ?? String.Empty;
    }

    private string TaxiReferenceDisplay(object raw) {
      if (raw == null) return null;
      if (raw is GomObject node && !String.IsNullOrWhiteSpace(node.Name)) return node.Name;
      GomObject resolved = null;
      try { resolved = ResolveTaxiObject(raw); } catch { }
      if (resolved != null && !String.IsNullOrWhiteSpace(resolved.Name)) return resolved.Name;
      return raw.ToString()?.Trim();
    }

    private static string TaxiModelPathFromValue(object raw, bool requireGr2Extension) {
      if (raw == null) return null;
      string text = raw.ToString()?.Trim();
      if (String.IsNullOrWhiteSpace(text)) return null;
      text = text.Replace('\\', '/');
      if (text.IndexOf(".gr2", StringComparison.OrdinalIgnoreCase) >= 0) return text;
      if (requireGr2Extension) return null;
      // vehAppModel is a GR2 asset field; older prototypes sometimes omit the extension. Reject obvious stable IDs or
      // vehapp.* keys here so malformed data cannot turn into a bogus /resources/<number>.gr2 lookup.
      if (TryTaxiUInt64(text, out _) || text.StartsWith("vehapp.", StringComparison.OrdinalIgnoreCase)) return null;
      return text.IndexOf('/') >= 0 ? text : null;
    }

    private static float TaxiFloat(object raw, float fallback) {
      if (raw == null) return fallback;
      try { return Convert.ToSingle(raw, CultureInfo.InvariantCulture); } catch { return fallback; }
    }

    private IEnumerable<GomObjectData> EnumerateTaxiRouteRows(object raw) {
      if (raw == null) yield break;
      if (raw is GomObjectData inline) { yield return inline; yield break; }
      if (raw is GomObject objectNode) { GomObjectData d = null; try { d = objectNode.Data; } catch { } if (d != null) yield return d; yield break; }
      GomObject referenced = ResolveTaxiObject(raw);
      if (referenced != null) { GomObjectData d = null; try { d = referenced.Data; } catch { } if (d != null) yield return d; yield break; }
      if (raw is string || raw is byte[]) yield break;
      if (raw is IDictionary dictionary) {
        foreach (DictionaryEntry pair in dictionary) {
          foreach (GomObjectData row in EnumerateTaxiRouteRows(pair.Key)) yield return row;
          foreach (GomObjectData row in EnumerateTaxiRouteRows(pair.Value)) yield return row;
        }
        yield break;
      }
      if (raw is IEnumerable enumerable) {
        foreach (object value in enumerable)
          foreach (GomObjectData row in EnumerateTaxiRouteRows(value)) yield return row;
      }
    }

    private GomObject ResolveTaxiObject(object raw) {
      if (raw == null) return null;
      if (raw is GomObject direct) return direct;
      try {
        if (TryUnsignedGomId(raw, out ulong id) && id != 0) return currentDom.GetObject(id);
        if (raw is string text && !String.IsNullOrWhiteSpace(text)) return currentDom.GetObject(text.Trim());
      } catch { }
      return null;
    }

    private string ResolveTaxiReferenceName(object raw) {
      if (raw == null) return null;
      if (raw is GomObject node) return node.Name;
      GomObject resolved = ResolveTaxiObject(raw);
      if (resolved != null) return resolved.Name;
      if (raw is string text) return text.Trim();
      return ResolveGomReferenceName(raw);
    }

    private static object TaxiDataValue(GomObjectData data, string name, string numericName) {
      if (data == null) return null;
      object value;
      if (!String.IsNullOrWhiteSpace(name) && data.Dictionary.TryGetValue(name, out value)) return value;
      return !String.IsNullOrWhiteSpace(numericName) && data.Dictionary.TryGetValue(numericName, out value) ? value : null;
    }

    private string TaxiTerminalDisplayName(GomObject terminal, StringTable table) {
      if (terminal == null) return null;
      try {
        long id = terminal.Data.ValueOrDefault<long>("taxNameId", -1);
        if (id >= 0 && table != null) {
          string localized = table.GetText(0x7D60500000000L + id, String.Empty);
          if (!String.IsNullOrWhiteSpace(localized)) return localized.Trim();
        }
      } catch { }
      return TaxiFriendlyName(terminal.Name);
    }

    private static AreaPath FindTaxiAreaPath(List<AreaPath> paths, string routePath) {
      if (paths == null || paths.Count == 0 || String.IsNullOrWhiteSpace(routePath)) return null;
      string needle = NormalizeTaxiPath(routePath);
      AreaPath exact = paths.FirstOrDefault(path => String.Equals(NormalizeTaxiPath(path.Fqn), needle, StringComparison.OrdinalIgnoreCase));
      if (exact != null) return exact;
      exact = paths.FirstOrDefault(path => String.Equals(NormalizeTaxiPath(path.Name), needle, StringComparison.OrdinalIgnoreCase));
      if (exact != null) return exact;

      string tail = TaxiPathTail(needle);
      if (String.IsNullOrWhiteSpace(tail)) return null;
      List<AreaPath> tailMatches = paths.Where(path =>
        String.Equals(TaxiPathTail(NormalizeTaxiPath(path.Fqn)), tail, StringComparison.OrdinalIgnoreCase) ||
        String.Equals(TaxiPathTail(NormalizeTaxiPath(path.Name)), tail, StringComparison.OrdinalIgnoreCase)).ToList();
      return tailMatches.Count == 1 ? tailMatches[0] : null;
    }

    private static string NormalizeTaxiPath(string value) {
      if (String.IsNullOrWhiteSpace(value)) return String.Empty;
      string text = value.Trim().Replace('\\', '.').Replace('/', '.').Trim('.').ToLowerInvariant();
      // File-looking references occasionally surface from older dumps; area.dat stores the path FQN without a
      // trailing asset extension.
      if (text.EndsWith(".pth", StringComparison.Ordinal)) text = text.Substring(0, text.Length - 4);
      while (text.Contains("..")) text = text.Replace("..", ".");
      return text;
    }

    private static string TaxiPathTail(string value) {
      if (String.IsNullOrWhiteSpace(value)) return String.Empty;
      string normalized = NormalizeTaxiPath(value);
      int pth = normalized.IndexOf("pth.", StringComparison.Ordinal);
      if (pth >= 0) normalized = normalized.Substring(pth + 4);
      int dot = normalized.LastIndexOf('.');
      return dot >= 0 ? normalized.Substring(dot + 1) : normalized;
    }

    private static bool LooksLikeGeneratedTaxiPath(AreaPath path) {
      if (path == null) return false;
      string text = ((path.Name ?? String.Empty) + " " + (path.Fqn ?? String.Empty)).ToLowerInvariant();
      if (text.Contains("generatedtaxi") || text.Contains("generated_taxi") || text.Contains("taxi")) return true;
      // Some generated paths only carry the marker in point metadata.
      return path.Points != null && path.Points.Any(point => (point?.Data ?? String.Empty).IndexOf("taxi", StringComparison.OrdinalIgnoreCase) >= 0);
    }

    private static string TaxiFriendlyName(string value) {
      if (String.IsNullOrWhiteSpace(value)) return null;
      string text = value.Trim().Replace('\\', '.').Replace('/', '.').Trim('.');
      int dot = text.LastIndexOf('.');
      if (dot >= 0 && dot + 1 < text.Length) text = text.Substring(dot + 1);
      text = Regex.Replace(text, "[_-]+", " ").Trim();
      if (text.Length == 0) return value;
      return CultureInfo.CurrentCulture.TextInfo.ToTitleCase(text.ToLower(CultureInfo.CurrentCulture));
    }

    private static bool TaxiBool(object raw) {
      if (raw == null) return false;
      if (raw is bool b) return b;
      try { return Convert.ToInt32(raw, CultureInfo.InvariantCulture) != 0; } catch { }
      string text = raw.ToString()?.Trim();
      return String.Equals(text, "true", StringComparison.OrdinalIgnoreCase) || String.Equals(text, "yes", StringComparison.OrdinalIgnoreCase);
    }

    private static int TaxiInt(object raw, int fallback) {
      if (raw == null) return fallback;
      try { return Convert.ToInt32(raw, CultureInfo.InvariantCulture); } catch { return fallback; }
    }
  }
}
