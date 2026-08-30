using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using FileFormats;

namespace PugTools {
  public partial class WorldBrowser {
    private static readonly Regex SpaceFlypathNamePattern = new Regex(@"^flypath(?:01|50|imp)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SpaceFlypathFqnPattern = new Regex(@"^pth\.space_combat\.", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private ToolStripMenuItem btnWorldSpaceFlypaths;

    private void InitializeSpaceFlypathMenu() {
      btnWorldSpaceFlypaths = new ToolStripMenuItem("Space Combat rails") {
        ToolTipText = "Preview the authored on-rails Space Combat player flypath (FlyPath01/FlyPath50/FlyPathimp)"
      };
      btnWorldSpaceFlypaths.DropDownOpening += (_, __) => RebuildSpaceFlypathMenu();
      btnWorldNavigationMenu?.DropDownItems.Add(btnWorldSpaceFlypaths);
    }

    private List<AreaPath> SpaceFlypathCandidates() {
      if (area?.Paths == null) return new List<AreaPath>();
      // Jedipedia intentionally requires BOTH pieces of authored evidence. There are unrelated
      // flypath-looking paths elsewhere in the game, and pth.space_combat.* contains branch/set-piece
      // splines that are not the player rail.
      return area.Paths
        .Where(path => path?.Points != null && path.Points.Count >= 2 &&
          SpaceFlypathNamePattern.IsMatch(path.Name ?? String.Empty) &&
          SpaceFlypathFqnPattern.IsMatch(path.Fqn ?? String.Empty))
        .OrderBy(path => SpaceFlypathIsImperial(path) ? 1 : 0)
        .ThenBy(path => path.Name, StringComparer.OrdinalIgnoreCase)
        .ThenBy(path => path.Fqn, StringComparer.OrdinalIgnoreCase)
        .ToList();
    }

    private static Boolean SpaceFlypathIsImperial(AreaPath path) {
      return (path?.Name ?? String.Empty).EndsWith("imp", StringComparison.OrdinalIgnoreCase) ||
        (path?.Fqn ?? String.Empty).EndsWith("imp", StringComparison.OrdinalIgnoreCase);
    }

    private static String SpaceFlypathLabel(AreaPath path) {
      String faction = SpaceFlypathIsImperial(path) ? "Empire" : "Republic";
      String fqn = path?.Fqn ?? String.Empty;
      String mission = fqn;
      const String prefix = "pth.space_combat.";
      if (mission.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) mission = mission.Substring(prefix.Length);
      mission = mission.Replace('_', ' ').Trim();
      if (String.IsNullOrWhiteSpace(mission)) mission = path?.Name ?? "mission rail";
      return faction + " — " + mission;
    }

    private void RebuildSpaceFlypathMenu() {
      if (btnWorldSpaceFlypaths == null) return;
      foreach (ToolStripItem old in btnWorldSpaceFlypaths.DropDownItems.Cast<ToolStripItem>().ToArray()) old.Dispose();
      btnWorldSpaceFlypaths.DropDownItems.Clear();

      Boolean active = panelRender?.IsSpaceFlypathActive == true;
      if (active) {
        String label = panelRender.CurrentSpaceFlypathLabel;
        var pause = new ToolStripMenuItem(panelRender.SpaceFlypathPaused ? "Resume" : "Pause") {
          ToolTipText = "Pause/resume the rail clock (Space)"
        };
        pause.Click += (_, __) => { panelRender?.ToggleSpaceFlypathPause(); ActivateWorldRenderInput(); };
        btnWorldSpaceFlypaths.DropDownItems.Add(pause);

        var seek = new ToolStripMenuItem("Seek");
        foreach (Int32 percent in new[] { 0, 10, 25, 50, 75, 90, 100 }) {
          Int32 captured = percent;
          var point = new ToolStripMenuItem(captured + "%");
          point.Click += (_, __) => { panelRender?.SeekSpaceFlypath(captured / 100f); ActivateWorldRenderInput(); };
          seek.DropDownItems.Add(point);
        }
        btnWorldSpaceFlypaths.DropDownItems.Add(seek);

        var stop = new ToolStripMenuItem(String.IsNullOrWhiteSpace(label) ? "Stop rail preview" : "Stop: " + label) {
          ToolTipText = "Leave the rail preview and keep the camera at its current position (Esc)"
        };
        stop.Click += (_, __) => { panelRender?.StopSpaceFlypath(); ActivateWorldRenderInput(); };
        btnWorldSpaceFlypaths.DropDownItems.Add(stop);

        String state = (panelRender.SpaceFlypathPaused ? "Paused" : "Playing") + "  •  " +
          (panelRender.SpaceFlypathProgress * 100f).ToString("0") + "%  •  " +
          panelRender.SpaceFlypathSpeedMultiplier.ToString("0.##") + "x";
        btnWorldSpaceFlypaths.DropDownItems.Add(new ToolStripMenuItem(state) { Enabled = false });

        View_AREA.SpaceFlypathTickInfo[] ticks = panelRender.SpaceFlypathTicks;
        if (ticks.Length > 0) {
          var events = new ToolStripMenuItem("Rail timeline events (" + ticks.Length + ")") {
            ToolTipText = "Waypoint/HYDRA triggers plus resolved Space Combat encounter spawn/despawn marks"
          };
          foreach (View_AREA.SpaceFlypathTickInfo tick in ticks) {
            View_AREA.SpaceFlypathTickInfo captured = tick;
            String kind = String.IsNullOrWhiteSpace(captured.Kind) ? "EVENT" : captured.Kind.ToUpperInvariant();
            String name = !String.IsNullOrWhiteSpace(captured.Label) ? captured.Label : (!String.IsNullOrWhiteSpace(captured.Fqn) ? captured.Fqn : "timeline event");
            var eventItem = new ToolStripMenuItem((captured.Progress * 100f).ToString("0.0") + "%  [" + kind + "] " + name) {
              ToolTipText = String.IsNullOrWhiteSpace(captured.Detail) ? "Seek to timeline event" : captured.Detail
            };
            eventItem.Click += (_, __) => { panelRender?.SeekSpaceFlypath(captured.Progress); ActivateWorldRenderInput(); };
            events.DropDownItems.Add(eventItem);
          }
          btnWorldSpaceFlypaths.DropDownItems.Add(events);
        }

        View_AREA.SpaceCombatEncounterTimelineInfo[] encounters = panelRender.SpaceCombatEncounterTimeline;
        if (encounters.Length > 0) {
          Int32 ships = encounters.Sum(x => Math.Max(0, x.ShipCount));
          Int32 bolted = encounters.Sum(x => Math.Max(0, x.BoltedCount));
          var encounterMenu = new ToolStripMenuItem("Space Combat encounters (" + encounters.Length + ")") {
            ToolTipText = "Encounter groups resolved from the HYDRA actions fired on this rail"
          };
          foreach (View_AREA.SpaceCombatEncounterTimelineInfo encounter in encounters) {
            View_AREA.SpaceCombatEncounterTimelineInfo captured = encounter;
            String range = (captured.StartProgress * 100f).ToString("0.0") + "%";
            if (captured.EndProgress >= captured.StartProgress) range += "–" + (captured.EndProgress * 100f).ToString("0.0") + "%";
            String counts = captured.ShipCount > 0 ? "  ×" + captured.ShipCount : (captured.BoltedCount > 0 ? "  bolted ×" + captured.BoltedCount : String.Empty);
            var item = new ToolStripMenuItem(range + "  " + (captured.Boss ? "[BOSS] " : String.Empty) + captured.EncounterName + counts) {
              ToolTipText = captured.Detail
            };
            var spawn = new ToolStripMenuItem("Seek spawn (" + (captured.StartProgress * 100f).ToString("0.0") + "%)");
            spawn.Click += (_, __) => { panelRender?.SeekSpaceFlypath(captured.StartProgress); ActivateWorldRenderInput(); };
            item.DropDownItems.Add(spawn);
            if (captured.EndProgress >= captured.StartProgress) {
              var despawn = new ToolStripMenuItem("Seek despawn (" + (captured.EndProgress * 100f).ToString("0.0") + "%)");
              despawn.Click += (_, __) => { panelRender?.SeekSpaceFlypath(captured.EndProgress); ActivateWorldRenderInput(); };
              item.DropDownItems.Add(despawn);
            }
            encounterMenu.DropDownItems.Add(item);
          }
          encounterMenu.DropDownItems.Add(new ToolStripSeparator());
          encounterMenu.DropDownItems.Add(new ToolStripMenuItem(ships + " free-flying ships" + (bolted > 0 ? "  •  " + bolted + " bolted components" : String.Empty)) { Enabled = false });
          btnWorldSpaceFlypaths.DropDownItems.Add(encounterMenu);
        }
        btnWorldSpaceFlypaths.DropDownItems.Add(new ToolStripSeparator());
      }

      List<AreaPath> tracks = SpaceFlypathCandidates();
      if (tracks.Count == 0) {
        btnWorldSpaceFlypaths.DropDownItems.Add(new ToolStripMenuItem(area == null ? "(load an area first)" : "(no player Space Combat rail in this area)") { Enabled = false });
        return;
      }

      var showShips = new ToolStripMenuItem("Show encounter ships") {
        CheckOnClick = true, Checked = worldSettings.ShowSpaceCombatShips,
        ToolTipText = "Render the free-flying SCE ships on their authored splines during rail preview"
      };
      showShips.CheckedChanged += (_, __) => {
        if (updatingWorldToolbar) return;
        worldSettings.ShowSpaceCombatShips = showShips.Checked;
        ApplyWorldSettings();
        SetStatusLabel(showShips.Checked ? "Space Combat encounter ships enabled." : "Space Combat encounter ships hidden.");
      };
      btnWorldSpaceFlypaths.DropDownItems.Add(showShips);
      btnWorldSpaceFlypaths.DropDownItems.Add(new ToolStripSeparator());

      foreach (AreaPath path in tracks) {
        AreaPath captured = path;
        String label = SpaceFlypathLabel(captured);
        var item = new ToolStripMenuItem(label) {
          Checked = active && String.Equals(panelRender?.CurrentSpaceFlypathLabel, label, StringComparison.Ordinal),
          ToolTipText = (captured.Fqn ?? captured.Name ?? "Space Combat path") + "  •  " + captured.Points.Count + " authored points"
        };
        item.Click += (_, __) => StartSpaceFlypath(captured, label);
        btnWorldSpaceFlypaths.DropDownItems.Add(item);
      }

      btnWorldSpaceFlypaths.DropDownItems.Add(new ToolStripSeparator());
      btnWorldSpaceFlypaths.DropDownItems.Add(new ToolStripMenuItem("Controls: free look • Space pause • wheel speed • Esc stop") { Enabled = false });
    }

    private void StartSpaceFlypath(AreaPath path, String label) {
      if (path?.Points == null || path.Points.Count < 2 || panelRender == null) return;
      // Like taxi, the rail owns camera translation and is always a perspective preview.
      if (btnWorldWalkingMode?.Checked == true) btnWorldWalkingMode.Checked = false;
      else worldSettings.WalkingMode = false;
      if (btnWorldOrthographic?.Checked == true) btnWorldOrthographic.Checked = false;
      else worldSettings.OrthographicProjection = false;
      ApplyWorldSettings();
      panelRender.StopTaxiRide();
      Dictionary<Int32, String> waypointTemplates = ResolveSpaceFlypathWaypointTemplates(path);
      spaceEncounterTimelineSignature = String.Empty;
      panelRender.StartSpaceFlypath(path, label, waypointTemplates);
      ActivateWorldRenderInput();
      SetStatusLabel("Space Combat rail: " + label + "  •  free look, Space = pause, wheel = speed, Esc = stop");
    }
  }
}
