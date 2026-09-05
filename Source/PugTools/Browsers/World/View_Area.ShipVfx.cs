using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using FileFormats;
using SlimDX;

namespace PugTools {
  internal sealed partial class View_AREA {
    // Port of Jedipedia viewer/shipVfx.js. Ship-window assemblies are ordinary DYN placeables whose rows happen to
    // reference the vfx_planet_transition_* family; SWTOR's hydra scripts hold their states, so a viewer must do the
    // same or the generic two-second DYN preview clock makes the cockpit flicker between destinations.
    private const string ShipVfxDefaultPlace = "starfield";
    private const string ShipVfxTunnelSpec = "/resources/art/fx/fxspec/space_combat/sc_hyperspace_enter01.fxspec";
    private const string ShipVfxTunnelTag = "fx_hyperspace";
    private static readonly Regex ShipVfxSpecRegex = new Regex(@"(?:^|/)vfx_planet_transition_(arrival|idle|move|utility)_(.+)\.fxspec$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ShipVfxDepartureRegex = new Regex(@"(?:^|/)vfx_planet_transition_departure\.fxspec$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    internal sealed class WorldShipVfxDestination {
      public string Place { get; set; }
      public string Label { get; set; }
    }

    internal sealed class WorldShipVfxStatus {
      public string Place { get; set; }
      public string Label { get; set; }
      public string OffLabel { get; set; }
      public bool Jumping { get; set; }
      public readonly List<WorldShipVfxDestination> Places = new List<WorldShipVfxDestination>();
    }

    private sealed class ShipVfxPlaceStates {
      public string Arrival, Idle, Move, Utility;
      public string Get(string kind) {
        switch ((kind ?? String.Empty).ToLowerInvariant()) {
          case "arrival": return Arrival;
          case "idle": return Idle;
          case "move": return Move;
          case "utility": return Utility;
          default: return null;
        }
      }
      public bool SetFirst(string kind, string state) {
        if (String.IsNullOrWhiteSpace(state)) return false;
        switch ((kind ?? String.Empty).ToLowerInvariant()) {
          case "arrival": if (Arrival != null) return false; Arrival = state; return true;
          case "idle": if (Idle != null) return false; Idle = state; return true;
          case "move": if (Move != null) return false; Move = state; return true;
          case "utility": if (Utility != null) return false; Utility = state; return true;
          default: return false;
        }
      }
    }

    private sealed class ShipVfxPlacement {
      public WorldSpnPlacement Placement;
      public string Off;
      public string On;
      public readonly Dictionary<string, ShipVfxPlaceStates> ByPlace = new Dictionary<string, ShipVfxPlaceStates>(StringComparer.OrdinalIgnoreCase);
      public readonly Dictionary<string, int> KindCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) {
        { "arrival", 0 }, { "idle", 0 }, { "move", 0 }, { "utility", 0 }
      };
    }

    private sealed class ShipVfxTransition {
      public string From, To;
      public float At;
    }

    private readonly object shipVfxSync = new object();
    private readonly Dictionary<WorldSpnPlacement, ShipVfxPlacement> shipVfxPlacements = new Dictionary<WorldSpnPlacement, ShipVfxPlacement>();
    private readonly Dictionary<string, string> shipVfxPlaces = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private ShipVfxPlacement shipVfxTunnelPlacement;
    private ShipVfxPlacement shipVfxGlobe, shipVfxBackdrop;
    private bool shipVfxRolesDirty = true;
    private string shipVfxPlace = String.Empty;
    private string shipVfxOffLabel = String.Empty;
    private bool shipVfxChosen;
    private ShipVfxTransition shipVfxTransition;
    private AssetInstance shipVfxTunnelHook;
    private Room shipVfxTunnelHookRoom;
    private bool shipVfxTunnelHookResolved;
    private object shipVfxTunnelOwner = new object();

    private void BuildShipVfxRegistry() {
      lock (shipVfxSync) {
        shipVfxPlacements.Clear(); shipVfxPlaces.Clear(); shipVfxTunnelPlacement = null;
        shipVfxGlobe = shipVfxBackdrop = null; shipVfxRolesDirty = true;
        shipVfxPlace = String.Empty; shipVfxOffLabel = String.Empty; shipVfxChosen = false; shipVfxTransition = null;
        shipVfxTunnelHook = null; shipVfxTunnelHookRoom = null; shipVfxTunnelHookResolved = false; shipVfxTunnelOwner = new object();
        if (spnPlacements == null) return;
        foreach (WorldSpnPlacement placement in spnPlacements) {
          if (placement?.DynEffects != null) foreach (WorldSpnFxPart effect in placement.DynEffects) if (effect != null) effect.ShipVfx = false;
          RegisterShipVfxPlacementLocked(placement);
        }
      }
    }

    private void RegisterShipVfxPlacementLocked(WorldSpnPlacement placement) {
      if (placement?.DynEffects == null || placement.DynStates == null || placement.DynStates.Count < 2) return;
      string off = placement.DynStates[0]?.Name;
      if (String.IsNullOrWhiteSpace(off)) return;
      ShipVfxPlacement shipPlacement = null;
      foreach (WorldSpnFxPart effect in placement.DynEffects) {
        if (effect == null || effect.StateVisibility == null || effect.StateVisibility.Count == 0 || String.IsNullOrWhiteSpace(effect.Path)) continue;
        string path = effect.Path.Replace('\\', '/');
        bool departure = ShipVfxDepartureRegex.IsMatch(path);
        Match match = departure ? null : ShipVfxSpecRegex.Match(path);
        if (!departure && (match == null || !match.Success)) continue;
        if (shipPlacement == null) {
          shipPlacement = new ShipVfxPlacement { Placement = placement, Off = off };
          shipVfxPlacements[placement] = shipPlacement;
          if (String.IsNullOrWhiteSpace(shipVfxOffLabel)) shipVfxOffLabel = off;
        }
        effect.ShipVfx = true;
        string shown = FirstShownShipVfxState(placement, effect);
        if (departure) {
          if (!String.IsNullOrWhiteSpace(shown)) {
            shipPlacement.On = shown;
            shipVfxTunnelPlacement = shipPlacement;
          }
          continue;
        }
        string kind = match.Groups[1].Value.ToLowerInvariant();
        string place = match.Groups[2].Value;
        if (String.IsNullOrWhiteSpace(place)) continue;
        if (!shipPlacement.ByPlace.TryGetValue(place, out ShipVfxPlaceStates states)) shipPlacement.ByPlace[place] = states = new ShipVfxPlaceStates();
        if (states.SetFirst(kind, shown)) shipPlacement.KindCounts[kind] = shipPlacement.KindCounts.TryGetValue(kind, out int count) ? count + 1 : 1;
        if (!shipVfxPlaces.ContainsKey(place)) shipVfxPlaces[place] = ShipVfxLabel(place);
        shipVfxRolesDirty = true;
      }
    }

    private static string FirstShownShipVfxState(WorldSpnPlacement placement, WorldSpnFxPart effect) {
      if (placement?.DynStates == null || effect?.StateVisibility == null) return null;
      foreach (WorldSpnDynState state in placement.DynStates) {
        string name = state?.Name;
        if (!String.IsNullOrWhiteSpace(name) && effect.StateVisibility.TryGetValue(name, out bool visible) && visible) return name;
      }
      return null;
    }

    private static string ShipVfxLabel(string place) => (place ?? String.Empty).Replace('_', ' ');

    private string ShipVfxCurrentPlaceLocked() {
      if (shipVfxChosen) return shipVfxPlace ?? String.Empty;
      if (shipVfxPlaces.ContainsKey(ShipVfxDefaultPlace)) return ShipVfxDefaultPlace;
      return shipVfxPlaces.OrderBy(x => x.Value, StringComparer.OrdinalIgnoreCase).Select(x => x.Key).FirstOrDefault() ?? String.Empty;
    }

    private ShipVfxPlacement ShipVfxMostOfLocked(string kind) {
      ShipVfxPlacement best = null; int bestCount = 0;
      foreach (ShipVfxPlacement placement in shipVfxPlacements.Values) {
        int count = placement.KindCounts.TryGetValue(kind, out int n) ? n : 0;
        if (count > bestCount) { bestCount = count; best = placement; }
      }
      return best;
    }

    private void ResolveShipVfxRolesLocked() {
      if (!shipVfxRolesDirty) return;
      shipVfxGlobe = ShipVfxMostOfLocked("move") ?? ShipVfxMostOfLocked("utility") ?? ShipVfxMostOfLocked("arrival");
      shipVfxBackdrop = ShipVfxMostOfLocked("idle") ?? ShipVfxMostOfLocked("arrival") ?? shipVfxGlobe;
      shipVfxRolesDirty = false;
    }

    private static string ShipVfxKindState(ShipVfxPlacement placement, string place, string kind) {
      if (placement == null || String.IsNullOrWhiteSpace(place) || !placement.ByPlace.TryGetValue(place, out ShipVfxPlaceStates states)) return null;
      return states.Get(kind);
    }

    private void ShipVfxAdvanceLocked() {
      ShipVfxTransition transition = shipVfxTransition;
      if (transition == null) return;
      float rest = shipVfxTunnelPlacement != null ? 7.5f : 12.25f;
      float dt = elapsed - transition.At;
      if (dt < 0f || dt >= rest) shipVfxTransition = null;
    }

    private bool ShipVfxTunnelOpenLocked() {
      ShipVfxAdvanceLocked();
      if (shipVfxTransition == null) return false;
      float dt = elapsed - shipVfxTransition.At;
      if (shipVfxTunnelPlacement != null) return dt >= .25f && dt < 7.5f;
      return dt >= 5f && dt < 9.25f;
    }

    private void ShipVfxPictureLocked(out ShipVfxPlacement placement, out string state) {
      placement = null; state = null; ShipVfxAdvanceLocked(); ResolveShipVfxRolesLocked();
      if (shipVfxTransition == null) {
        string place = ShipVfxCurrentPlaceLocked();
        if (shipVfxBackdrop == null || String.IsNullOrWhiteSpace(place) || !shipVfxBackdrop.ByPlace.TryGetValue(place, out ShipVfxPlaceStates resting)) return;
        state = resting.Idle ?? resting.Arrival ?? resting.Utility ?? resting.Move;
        if (!String.IsNullOrWhiteSpace(state)) placement = shipVfxBackdrop;
        return;
      }
      float dt = elapsed - shipVfxTransition.At;
      float dark = shipVfxTunnelPlacement != null ? 2.15f : 7.25f;
      float arrive = shipVfxTunnelPlacement != null ? 4.5f : 9.25f;
      if (dt >= dark && dt < arrive) return;
      bool turning = dt < dark;
      string placeToken = turning ? shipVfxTransition.From : shipVfxTransition.To;
      string stateName = ShipVfxKindState(shipVfxGlobe, placeToken, turning ? "move" : "utility");
      if (!String.IsNullOrWhiteSpace(stateName)) { placement = shipVfxGlobe; state = stateName; return; }
      if (shipVfxBackdrop == null || String.IsNullOrWhiteSpace(placeToken) || !shipVfxBackdrop.ByPlace.TryGetValue(placeToken, out ShipVfxPlaceStates fallback)) return;
      state = fallback.Idle ?? fallback.Arrival ?? fallback.Utility ?? fallback.Move;
      if (!String.IsNullOrWhiteSpace(state)) placement = shipVfxBackdrop;
    }

    private bool TryGetShipVfxStateName(WorldSpnPlacement placement, out string stateName) {
      stateName = null; if (placement == null) return false;
      lock (shipVfxSync) {
        if (!shipVfxPlacements.TryGetValue(placement, out ShipVfxPlacement shipPlacement)) return false;
        if (ReferenceEquals(shipPlacement, shipVfxTunnelPlacement)) {
          stateName = ShipVfxTunnelOpenLocked() && !String.IsNullOrWhiteSpace(shipPlacement.On) ? shipPlacement.On : shipPlacement.Off;
          return true;
        }
        ShipVfxPictureLocked(out ShipVfxPlacement picture, out string pictureState);
        stateName = ReferenceEquals(picture, shipPlacement) && !String.IsNullOrWhiteSpace(pictureState) ? pictureState : shipPlacement.Off;
        return true;
      }
    }

    internal WorldShipVfxStatus GetShipVfxStatus(bool includePlaces = false) {
      lock (shipVfxSync) {
        if (shipVfxPlaces.Count == 0) return null;
        ShipVfxAdvanceLocked();
        string place = ShipVfxCurrentPlaceLocked();
        var result = new WorldShipVfxStatus {
          Place = place,
          Label = String.IsNullOrWhiteSpace(place) ? shipVfxOffLabel : (shipVfxPlaces.TryGetValue(place, out string label) ? label : ShipVfxLabel(place)),
          OffLabel = shipVfxOffLabel,
          Jumping = shipVfxTransition != null
        };
        if (includePlaces) foreach (var pair in shipVfxPlaces.OrderBy(x => x.Value, StringComparer.OrdinalIgnoreCase))
          result.Places.Add(new WorldShipVfxDestination { Place = pair.Key, Label = pair.Value });
        return result;
      }
    }

    internal WorldShipVfxStatus SelectShipVfxDestination(string place) {
      lock (shipVfxSync) {
        if (shipVfxPlaces.Count == 0) return null;
        ShipVfxAdvanceLocked();
        if (shipVfxTransition != null) return GetShipVfxStatus(true);
        string next = place ?? String.Empty;
        if (!String.IsNullOrWhiteSpace(next) && !shipVfxPlaces.ContainsKey(next)) return GetShipVfxStatus(true);
        string from = ShipVfxCurrentPlaceLocked();
        shipVfxPlace = next; shipVfxChosen = true;
        if (!String.Equals(next, from, StringComparison.OrdinalIgnoreCase)) {
          shipVfxTunnelOwner = new object();
          shipVfxTransition = new ShipVfxTransition { From = from, To = next, At = elapsed };
        }
        return GetShipVfxStatus(true);
      }
    }

    private bool SpnDynEffectEnabled(WorldSpnPlacement placement, WorldSpnFxPart effect, bool animateStates) {
      if (effect == null) return false;
      if (effect.StateVisibility == null || effect.StateVisibility.Count == 0) return true;
      WorldSpnDynState state = ActiveSpnDynState(placement, animateStates);
      string name = state?.Name;
      if (String.IsNullOrWhiteSpace(name)) return true;
      return effect.StateVisibility.TryGetValue(name, out bool visible) && visible;
    }

    private void DrawSpnFxEffects(WorldSpnPlacement placement, Matrix placementWorld, Matrix viewProj, bool animateStates, bool shipOnly, Vector3? cameraPositionOverride = null) {
      if (placement?.DynEffects == null || placement.DynEffects.Count == 0) return;
      foreach (WorldSpnFxPart effect in placement.DynEffects) {
        if (effect == null || effect.ShipVfx != shipOnly || !SpnDynEffectEnabled(placement, effect, animateStates)) continue;
        Matrix frame = effect.LocalMatrix * placementWorld;
        WorldFxHostContext hostContext = BuildWorldFxSpnHostContext(placement, placementWorld, frame, animateStates);
        TryDrawWorldFxPlayer(effect, effect.Path, frame, viewProj, WorldFxMaxRenderParticlesPerWorldEffect, out _, cameraPositionOverride, settings, shipOnly && cameraPositionOverride.HasValue, hostContext: hostContext);
      }
    }

    // Ship window/backdrop records use infinite reach in Jedipedia. They also belong to the same render layer as the
    // placement that owns them: flagship transition assemblies commonly live in the skyscene, whose camera-relative
    // transform and depth pass are different from the ordinary world. Keep those passes separate so a planet cannot
    // be drawn in front of the cockpit after world depth has been cleared.
    private void DrawShipVfxEffects(Matrix viewProj, WorldRenderSettings settings, bool skyOnly = false, Room activeSkyRoom = null, Matrix? skyCameraInverse = null) {
      if (settings == null || !settings.ShowSpnObjects || settings.Mode == WorldRenderMode.Map || settings.Mode == WorldRenderMode.Heightmap) return;
      List<WorldSpnPlacement> placements;
      bool tunnelOpen;
      lock (shipVfxSync) {
        if (shipVfxPlaces.Count == 0) return;
        placements = shipVfxPlacements.Keys.ToList();
        tunnelOpen = ShipVfxTunnelOpenLocked();
      }
      foreach (WorldSpnPlacement placement in placements) {
        if (placement?.Instance == null || placement.Room == null || placement.DynEffects == null) continue;
        bool isSky = skyRoomNames.Contains(placement.Room.RoomName);
        if (isSky != skyOnly) continue;
        if (skyOnly && activeSkyRoom != null && !String.Equals(activeSkyRoom.RoomName, placement.Room.RoomName, StringComparison.Ordinal)) continue;
        Matrix world;
        Vector3? effectCamera = null;
        try {
          if (skyOnly) {
            world = InstanceWorldForSky(placement.Instance, placement.Room);
            if (skyCameraInverse.HasValue) world *= skyCameraInverse.Value;
            effectCamera = Vector3.Zero;
          } else world = SpnPlacementWorld(placement, settings.AnimateSpnObjects);
        } catch { continue; }
        DrawSpnFxEffects(placement, world, viewProj, settings.AnimateSpnObjects, true, effectCamera);
      }
      // The class-ship tunnel hangs from a normal room marker. A flagship owns a DYN tunnel placement and is handled
      // by the matching layer above instead.
      if (!skyOnly && tunnelOpen && shipVfxTunnelPlacement == null && TryResolveShipVfxTunnelHook(out Room hookRoom, out AssetInstance hook)) {
        Matrix frame;
        try { frame = InstanceWorld(hook, hookRoom); } catch { return; }
        object owner; lock (shipVfxSync) owner = shipVfxTunnelOwner;
        TryDrawWorldFxPlayer(owner, ShipVfxTunnelSpec, frame, viewProj, WorldFxMaxRenderParticlesPerWorldEffect, out _, null, settings, false);
      }
    }

    private bool TryResolveShipVfxTunnelHook(out Room room, out AssetInstance instance) {
      lock (shipVfxSync) {
        if (!shipVfxTunnelHookResolved) {
          shipVfxTunnelHookResolved = true;
          foreach (Room candidateRoom in rooms) {
            if (candidateRoom?.InstancesById == null) continue;
            foreach (AssetInstance candidate in candidateRoom.InstancesById.Values) {
              if (candidate == null || !String.Equals(candidate.TriggerTag, ShipVfxTunnelTag, StringComparison.OrdinalIgnoreCase)) continue;
              shipVfxTunnelHook = candidate; shipVfxTunnelHookRoom = candidateRoom; break;
            }
            if (shipVfxTunnelHook != null) break;
          }
        }
        room = shipVfxTunnelHookRoom; instance = shipVfxTunnelHook;
        return room != null && instance != null;
      }
    }
  }
}
