using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FileFormats;
using GomLib;
using GomLib.Models;
using SlimDX;
using TorArchive;
using File = TorArchive.File;
using Room = FileFormats.Room;
using AssetInstance = FileFormats.AssetInstance;

namespace PugTools {
  public partial class WorldBrowser {
    private volatile string worldImplicitPhaseName = String.Empty;

    // Jedipedia uses a bundled beta/live marker catalogue. PugTools can resolve the live equivalents directly
    // from the selected SWTOR archives; missing beta-only markers fall back to a simple line gizmo in View_AREA.
    private static readonly Dictionary<string, string> WorldUtilityMarkerPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
      { "cvr", "/resources/engine/spawner/coverpoint_crouching_exclusion.gr2" },
      { "cos", "/resources/engine/spawner/coverpoint_auto_left.gr2" },
      { "enc", "/resources/engine/spawner/spawner_encounter.gr2" },
      { "lit", "/resources/engine/staging/engine_staging_pointlight.gr2" },
      { "stg", "/resources/engine/staging/engine_staging_camera.gr2" },
      { "mpn", "/resources/engine/map/engine_map_mapnode.gr2" },
      { "spn_mov", "/resources/engine/spawner/spawner_movepoint_1.gr2" },
      { "spn_c", "/resources/engine/spawner/spawner_creature.gr2" },
      { "spn_enc", "/resources/engine/spawner/spawner_creature.gr2" },
      { "spn_p", "/resources/engine/spawner/spawner_placeable.gr2" },
      { "spn_crf", "/resources/engine/spawner/spawner_crefac_quest.gr2" },
      { "spn_lst", "/resources/engine/spawner/spawner_group.gr2" },
      { "spn_grp", "/resources/engine/spawner/spawner_group.gr2" },
      { "spn_rly", "/resources/engine/spawner/spawner_rallypoint.gr2" },
      { "spn_seed", "/resources/engine/spawners/spawner_seed_point.gr2" },
      // These marker shapes were beta-only in Jedipedia's catalogue. Use a stable live stand-in when available;
      // the overlay tint still distinguishes the placement kind and the fallback cross covers missing assets.
      { "spn_pt", "/resources/engine/spawner/spawner_movepoint_1.gr2" },
      { "spn_med", "/resources/engine/spawner/spawner_group.gr2" },
      { "spn_rez", "/resources/engine/spawner/spawner_rallypoint.gr2" },
      { "spn_veh", "/resources/engine/spawner/spawner_placeable.gr2" },
    };

    private string ResolveWorldImplicitPhaseName(ulong areaId) {
      if (currentDom == null || areaId == 0) return String.Empty;
      try {
        GomObject prototype = currentDom.GetObject("phsInstanceDataPrototype");
        if (prototype == null) return String.Empty;

        object mapObject = null;
        if (!prototype.Data.Dictionary.TryGetValue("phsDataImplicitAreaMap", out mapObject))
          prototype.Data.Dictionary.TryGetValue("4611686102770270021", out mapObject);
        if (!(mapObject is System.Collections.IDictionary phaseMap)) return String.Empty;

        object phaseReference = null;
        foreach (System.Collections.DictionaryEntry entry in phaseMap) {
          if (!TryPhaseId(entry.Key, out ulong mappedArea) || mappedArea != areaId) continue;
          phaseReference = entry.Value;
          break;
        }
        if (phaseReference == null) return String.Empty;

        if (phaseReference is GomObject phaseObject) return PhaseDisplayName(phaseObject.Name);
        if (phaseReference is string phaseText) {
          if (phaseText.StartsWith("phs.", StringComparison.OrdinalIgnoreCase)) return PhaseDisplayName(phaseText);
          if (!UInt64.TryParse(phaseText, out ulong phaseTextId)) return String.Empty;
          phaseReference = phaseTextId;
        }
        if (!TryPhaseId(phaseReference, out ulong phaseId) || phaseId == 0) return String.Empty;
        GomObject phaseNode = currentDom.GetObject(phaseId);
        return PhaseDisplayName(phaseNode?.Name);
      } catch (Exception ex) {
        System.Diagnostics.Debug.WriteLine("Implicit phase lookup failed for area " + areaId + ": " + ex.Message);
        return String.Empty;
      }
    }

    private static bool TryPhaseId(object candidate, out ulong id) {
      id = 0;
      if (candidate == null) return false;
      if (candidate is ulong u) { id = u; return true; }
      if (candidate is long l && l >= 0) { id = (ulong)l; return true; }
      if (candidate is uint ui) { id = ui; return true; }
      if (candidate is int i && i >= 0) { id = (ulong)i; return true; }
      return UInt64.TryParse(candidate.ToString(), out id);
    }

    private static string PhaseDisplayName(string fqn) {
      string name = (fqn ?? String.Empty).Trim();
      return name.StartsWith("phs.", StringComparison.OrdinalIgnoreCase) ? name.Substring(4) : name;
    }

    private void LoadWorldUtilityModels() {
      worldUtilityModels.Clear();
      if (currentAssets == null) return;
      foreach (var pair in WorldUtilityMarkerPaths) {
        try {
          using File file = currentAssets.FindFile(pair.Value);
          if (file == null) continue;
          using Stream stream = file.OpenCopyInMemory();
          using var br = new BinaryReader(stream);
          // Do not use the area's shared material dictionary: editor marker materials are intentionally
          // EditorOnly in the normal world pass and View_AREA draws these with an explicit override.
          worldUtilityModels[pair.Key] = new GR2(br, pair.Value.Split('/').Last());
        } catch (Exception ex) {
          System.Diagnostics.Debug.WriteLine("Utility marker load failed (" + pair.Key + "): " + ex.Message);
        }
      }
    }

    private void LoadWorldNpcPlacements() {
      worldNpcPlacements.Clear();
      worldSpnPlacements.Clear();
      if (area == null || currentDom == null || currentAssets == null) return;

      var appearanceCache = new Dictionary<string, List<GR2>>(StringComparer.OrdinalIgnoreCase);
      var npcCache = new Dictionary<string, Npc>(StringComparer.OrdinalIgnoreCase);
      var spawnerCache = new Dictionary<string, SpawnerPreviewInfo>(StringComparer.OrdinalIgnoreCase);
      var spnModelCache = new Dictionary<string, List<GR2>>(StringComparer.OrdinalIgnoreCase);
      var spnAnimationCache = new Dictionary<string, WorldNpcAnimationClip>(StringComparer.OrdinalIgnoreCase);
      var spnDynCache = new Dictionary<string, SpnDynPreviewTemplate>(StringComparer.OrdinalIgnoreCase);
      var animationCache = new Dictionary<string, WorldNpcAnimationClip>(StringComparer.OrdinalIgnoreCase);
      var spawnPointsByParent = BuildSpawnPointIndex();
      var speciesScales = LoadNpcSpeciesScales();
      int failures = 0;

      foreach (Room room in area.RoomList) {
        foreach (AssetInstance instance in room.InstancesById.Values) {
          if (instance == null || instance.hidden || !area.AssetIdMap.TryGetValue(instance.assetID, out AreaAsset asset) || asset == null) continue;
          string ext = (asset.Extension ?? String.Empty).Trim().ToLowerInvariant();
          try {
            if (ext == "cos") {
              WorldNpcPlacement placement = ResolveClientOnlyNpc(room, instance, asset, appearanceCache, animationCache);
              if (placement != null) worldNpcPlacements.Add(placement);
              continue;
            }
            if (!ext.StartsWith("spn_", StringComparison.OrdinalIgnoreCase)) continue;
            string spnFqn = ResolveSpawnerFqn(asset, instance);
            if (String.IsNullOrWhiteSpace(spnFqn)) continue;
            if (!spawnerCache.TryGetValue(spnFqn, out SpawnerPreviewInfo spawnerInfo)) {
              spawnerInfo = ResolveSpawnerPreviewInfo(spnFqn);
              spawnerCache[spnFqn] = spawnerInfo ?? new SpawnerPreviewInfo();
            }
            List<string> entityFqns = spawnerInfo?.EntityFqns;
            if (entityFqns == null || entityFqns.Count == 0) continue;
            string idleAnimation = SpawnerIdleAnimationName(instance);
            // Build first, then compress the variant slots. Jedipedia skips an alternative that fails to resolve;
            // keeping its original index would otherwise create a blank two-second slot in our preview.
            var builtVariants = new List<object>();
            foreach (string entityFqn in entityFqns) {
              if (String.IsNullOrWhiteSpace(entityFqn)) continue;
              try {
                if (entityFqn.StartsWith("npc.", StringComparison.OrdinalIgnoreCase)) {
                  if (!npcCache.TryGetValue(entityFqn, out Npc npc)) {
                    npc = currentDom.NpcLoader.Load(entityFqn);
                    npcCache[entityFqn] = npc;
                  }
                  WorldNpcPlacement spnPlacement = BuildNpcPlacement(room, instance, entityFqn, npc, appearanceCache, speciesScales, idleAnimation, animationCache);
                  if (spnPlacement != null) {
                    // Jedipedia only resolves pth.* ride routes for placeables. Applying the spawner's path to an NPC
                    // turns platform/elevator metadata into character locomotion and can make a creature race back and
                    // forth at absurd speed. NPC dispensers still use authored spn_pt spawn points, but never pth.*.
                    if (spawnPointsByParent.TryGetValue(instance.ID, out var npcSpawnPoints))
                      foreach (var point in npcSpawnPoints) spnPlacement.SpawnPoints.Add(BuildSpawnPointPose(point.Room, point.Instance));
                    builtVariants.Add(spnPlacement);
                  }
                } else if (entityFqn.StartsWith("plc.", StringComparison.OrdinalIgnoreCase)) {
                  WorldSpnPlacement spnObject = BuildSpnPlaceablePlacement(room, instance, entityFqn, spnModelCache, spnAnimationCache, spnDynCache);
                  if (spnObject != null) {
                    spnObject.PathFqn = spawnerInfo?.PathFqn;
                    spnObject.TraversalStyle = spawnerInfo?.TraversalStyle ?? 1;
                    spnObject.Route = ResolveSpnAreaPath(spnObject.PathFqn, room, instance);
                    if (spnObject.Route == null && spawnPointsByParent.TryGetValue(instance.ID, out var objectSpawnPoints))
                      foreach (var point in objectSpawnPoints) spnObject.SpawnPoints.Add(BuildSpawnPointPose(point.Room, point.Instance));
                    builtVariants.Add(spnObject);
                  }
                }
              } catch (Exception variantError) {
                // An invalid alternative does not invalidate the dispenser. Jedipedia skips that slot and keeps the
                // alternatives that did resolve, so do the same here before compressing VariantIndex/VariantCount.
                System.Diagnostics.Debug.WriteLine("Spawner alternative failed (" + entityFqn + "): " + variantError.Message);
              }
            }

            int variantCount = builtVariants.Count;
            for (int variantIndex = 0; variantIndex < builtVariants.Count; variantIndex++) {
              if (builtVariants[variantIndex] is WorldNpcPlacement npcVariant) {
                npcVariant.VariantIndex = variantIndex;
                npcVariant.VariantCount = Math.Max(1, variantCount);
                worldNpcPlacements.Add(npcVariant);
              } else if (builtVariants[variantIndex] is WorldSpnPlacement spnVariant) {
                spnVariant.VariantIndex = variantIndex;
                spnVariant.VariantCount = Math.Max(1, variantCount);
                worldSpnPlacements.Add(spnVariant);
              }
            }
          } catch (Exception ex) {
            failures++;
            System.Diagnostics.Debug.WriteLine("World NPC placement failed for " + asset.Path + "." + asset.Extension + ": " + ex.Message);
          }
        }
      }
      System.Diagnostics.Debug.WriteLine("World spawn preview: " + worldNpcPlacements.Count + " NPCs, " + worldSpnPlacements.Count + " placeables, " + failures + " failures");
    }

    private Dictionary<ulong, List<(Room Room, AssetInstance Instance)>> BuildSpawnPointIndex() {
      var result = new Dictionary<ulong, List<(Room Room, AssetInstance Instance)>>();
      if (area == null) return result;
      foreach (Room room in area.RoomList) {
        foreach (AssetInstance instance in room.InstancesById.Values) {
          if (instance == null || instance.parentInstance == 0 || !area.AssetIdMap.TryGetValue(instance.assetID, out AreaAsset asset) || asset == null) continue;
          if (!String.Equals((asset.Extension ?? String.Empty).Trim(), "spn_pt", StringComparison.OrdinalIgnoreCase)) continue;
          if (!result.TryGetValue(instance.parentInstance, out var list)) result[instance.parentInstance] = list = new List<(Room Room, AssetInstance Instance)>();
          list.Add((room, instance));
        }
      }
      // Room dictionaries are normally insertion ordered, but IDs give the dispenser a stable authored ordering on
      // runtimes where dictionary enumeration differs.
      foreach (var list in result.Values) list.Sort((a, b) => a.Instance.ID.CompareTo(b.Instance.ID));
      return result;
    }


    private static WorldSpawnPointPose BuildSpawnPointPose(Room room, AssetInstance point) {
      if (point == null) return new WorldSpawnPointPose();
      Matrix absolute = point.GetAbsoluteTransform(room);
      float rx = point.rotation.X * (float)Math.PI / 180f;
      float ry = point.rotation.Y * (float)Math.PI / 180f;
      float rz = point.rotation.Z * (float)Math.PI / 180f;
      // Row-vector equivalent of Jedipedia's gl-matrix Ry -> Rx -> Rz composition. Point scale is deliberately
      // excluded: .spn_pt is an editor gizmo normally authored at 0.5 and must never shrink the spawned entity.
      Matrix rotation = Matrix.RotationZ(rz) * Matrix.RotationX(rx) * Matrix.RotationY(ry);
      return new WorldSpawnPointPose {
        Position = new Vector3(absolute.M41, absolute.M42, absolute.M43),
        Rotation = rotation
      };
    }

    private Dictionary<ulong, float> LoadNpcSpeciesScales() {
      var result = new Dictionary<ulong, float>();
      try {
        GomObject table = currentDom?.GetObject("chrSpeciesScalePrototype");
        var lookup = table?.Data.ValueOrDefault<Dictionary<object, object>>("chrSpeciesScaleLookup", null);
        if (lookup == null) return result;
        foreach (var pair in lookup) {
          try {
            ulong key = pair.Key is long signed ? unchecked((ulong)signed) : Convert.ToUInt64(pair.Key);
            float scaleValue = Convert.ToSingle(pair.Value, System.Globalization.CultureInfo.InvariantCulture);
            if (scaleValue > 0f && !Single.IsNaN(scaleValue) && !Single.IsInfinity(scaleValue)) result[key] = scaleValue;
          } catch { }
        }
      } catch (Exception ex) {
        System.Diagnostics.Debug.WriteLine("NPC species scale table unavailable: " + ex.Message);
      }
      return result;
    }

    private string ResolveSpawnerFqn(AreaAsset asset, AssetInstance instance) {
      if (asset == null) return null;
      string path = (asset.Path ?? String.Empty).Trim().Replace('/', '\\');
      if (!String.IsNullOrWhiteSpace(asset.EncounterIndex)) {
        string encounterFqn = path.Replace('\\', '.');
        string tag = asset.EncounterIndex;
        if (instance != null && instance.ParsedProperties.TryGetValue(0x2A887DE0u, out object rawTag) && rawTag != null) {
          string overrideTag = rawTag.ToString().Replace("\0", String.Empty).Trim();
          if (!String.IsNullOrWhiteSpace(overrideTag)) tag = overrideTag;
        }
        Encounter enc = currentDom.EncounterLoader.Load(encounterFqn);
        if (enc?.Spawners == null) return null;
        string key = (tag ?? String.Empty).Trim().ToLowerInvariant();
        return enc.Spawners.TryGetValue(key, out string fqn) ? fqn : null;
      }
      string standalone = path.Replace('\\', '.').Trim('.');
      return standalone.StartsWith("spn.", StringComparison.OrdinalIgnoreCase) ? standalone : null;
    }

    private sealed class SpawnerPreviewInfo {
      public readonly List<string> EntityFqns = new List<string>();
      public string EntityFqn => EntityFqns.Count > 0 ? EntityFqns[0] : null;
      public string PathFqn;
      public int TraversalStyle = 1;
    }

    private SpawnerPreviewInfo ResolveSpawnerPreviewInfo(string spnFqn) {
      var result = new SpawnerPreviewInfo();
      GomObject spawner = currentDom.GetObject(spnFqn);
      if (spawner == null) return result;

      var list = spawner.Data.ValueOrDefault<List<object>>("spnEntityList", null);
      if (list == null) return result;
      bool firstResolvedRow = true;
      var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      foreach (object entry in list) {
        if (entry is not GomObjectData row) continue;
        object entityReference = row.ValueOrDefault<object>("spnEntityFqn", null) ?? row.ValueOrDefault<object>("spnEntityId", null);
        string resolved = ResolveGomReferenceName(entityReference);
        if (String.IsNullOrWhiteSpace(resolved)) continue;
        if (seen.Add(resolved)) result.EntityFqns.Add(resolved);

        // spnEntityList is alternatives rather than a group. Jedipedia uses the FIRST readable row for the route,
        // even while it cycles every distinct entity in the list. Weighted duplicate rows are deduplicated above.
        if (firstResolvedRow) {
          firstResolvedRow = false;
          var paths = row.ValueOrDefault<List<object>>("spnPathList", null);
          if (paths != null) foreach (object pathReference in paths) {
            string path = ResolveGomReferenceName(pathReference);
            if (String.IsNullOrWhiteSpace(path) || path.StartsWith("pth.generic.", StringComparison.OrdinalIgnoreCase)) continue;
            result.PathFqn = path;
            result.TraversalStyle = ResolveSpnTraversalStyle(path);
            break;
          }
        }
      }
      return result;
    }

    private string ResolveGomReferenceName(object rawValue) {
      if (rawValue == null) return null;
      if (rawValue is string text) {
        string trimmed = text.Trim();
        if (trimmed.Length == 0) return null;
        if (!UInt64.TryParse(trimmed, out ulong numericText)) return trimmed;
        try { return currentDom.GetObject(numericText)?.Name ?? trimmed; } catch { return trimmed; }
      }
      try {
        if (!TryUnsignedGomId(rawValue, out ulong id)) return rawValue.ToString()?.Trim();
        return currentDom.GetObject(id)?.Name;
      } catch { return null; }
    }

    private int ResolveSpnTraversalStyle(string pathFqn) {
      if (String.IsNullOrWhiteSpace(pathFqn)) return 1;
      try {
        GomObject pathNode = currentDom.GetObject(pathFqn);
        // GomLib stores script-enum payloads as ScriptEnum and deliberately zero-bases ScriptEnum.Value while
        // decoding them (see DomEnum.ValueString/ScriptEnum). SWTOR's pthTraversalStyle itself is 1-based:
        // 1 Forward, 2 Reverse, 3 Patrol, 4 Circuit, 5 Random. Treating ScriptEnum as an ordinary integer made
        // Patrol unreadable and silently fell back to Forward, which is the characteristic "ride one way, snap
        // back to the start" behaviour seen on elevators.
        object raw = pathNode?.Data.ValueOrDefault<object>("pthTraversalStyle", null)
          ?? pathNode?.Data.ValueOrDefault<object>("4611686030387597023", null);
        if (raw == null) return 1;

        if (raw is ScriptEnum scriptEnum) {
          int scriptStyle = scriptEnum.Value + 1;
          if (scriptStyle >= 1 && scriptStyle <= 5) return scriptStyle;
        }

        try {
          int numeric = Convert.ToInt32(raw, System.Globalization.CultureInfo.InvariantCulture);
          if (numeric >= 1 && numeric <= 5) return numeric;
        } catch { }

        string text = raw.ToString()?.Trim() ?? String.Empty;
        if (text.IndexOf("backward", StringComparison.OrdinalIgnoreCase) >= 0 || text.IndexOf("reverse", StringComparison.OrdinalIgnoreCase) >= 0) return 2;
        if (text.IndexOf("patrol", StringComparison.OrdinalIgnoreCase) >= 0) return 3;
        if (text.IndexOf("circuit", StringComparison.OrdinalIgnoreCase) >= 0) return 4;
        if (text.IndexOf("random", StringComparison.OrdinalIgnoreCase) >= 0) return 5;
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            Int32.TryParse(text.Substring(2), System.Globalization.NumberStyles.HexNumber,
              System.Globalization.CultureInfo.InvariantCulture, out int zeroBasedEnum) && zeroBasedEnum >= 0 && zeroBasedEnum <= 4)
          return zeroBasedEnum + 1;
      } catch { }
      return 1;
    }

    private AreaPath ResolveSpnAreaPath(string pathFqn, Room room, AssetInstance spawner) {
      if (String.IsNullOrWhiteSpace(pathFqn) || area?.Paths == null) return null;
      List<AreaPath> candidates = area.Paths.Where(path => path != null && path.Points != null && path.Points.Count >= 2 &&
        String.Equals(path.Fqn, pathFqn, StringComparison.OrdinalIgnoreCase)).ToList();
      if (candidates.Count == 0) return null;
      if (candidates.Count == 1 || spawner == null || room == null) return candidates[0];

      Matrix spawnerWorld = spawner.GetAbsoluteTransform(room);
      Vector3 origin = new Vector3(spawnerWorld.M41, spawnerWorld.M42, spawnerWorld.M43);
      AreaPath best = candidates[0];
      float bestDistance = (best.Points[0].Position - origin).LengthSquared();
      for (int i = 1; i < candidates.Count; i++) {
        float distance = (candidates[i].Points[0].Position - origin).LengthSquared();
        if (distance < bestDistance) { best = candidates[i]; bestDistance = distance; }
      }
      return best;
    }

    private static bool TryUnsignedGomId(object rawValue, out ulong id) {
      id = 0;
      if (rawValue == null) return false;
      if (rawValue is ulong u) { id = u; return true; }
      if (rawValue is long l) { id = unchecked((ulong)l); return true; }
      if (rawValue is uint ui) { id = ui; return true; }
      if (rawValue is int i) { id = unchecked((ulong)(long)i); return true; }
      string text = rawValue.ToString()?.Trim();
      if (String.IsNullOrEmpty(text)) return false;
      if (UInt64.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out id)) return true;
      if (Int64.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out long signed)) { id = unchecked((ulong)signed); return true; }
      return false;
    }

    private static bool WorldNpcLooksLikeTaxiTerminal(string sourceFqn, string name, string title) {
      string hint = ((sourceFqn ?? String.Empty) + " " + (name ?? String.Empty) + " " + (title ?? String.Empty)).ToLowerInvariant();
      return hint.Contains("taxi") || hint.Contains("taxidroid") || hint.Contains("taxi_droid") || hint.Contains("taxi droid") ||
        hint.Contains("transport_droid") || hint.Contains("transport droid") || hint.Contains("travel_droid") || hint.Contains("travel droid") ||
        hint.Contains("speeder_droid") || hint.Contains("speeder droid");
    }

    private WorldNpcPlacement ResolveClientOnlyNpc(Room room, AssetInstance instance, AreaAsset asset, Dictionary<string, List<GR2>> appearanceCache, Dictionary<string, WorldNpcAnimationClip> animationCache) {
      string fqn = (asset.Path ?? String.Empty).Replace('\\', '.').Replace('/', '.').Trim('.');
      if (!fqn.StartsWith("cos.", StringComparison.OrdinalIgnoreCase)) return null;
      GomObject obj = currentDom.GetObject(fqn);
      if (obj == null) return null;
      GomObjectData visual = obj.Data.ValueOrDefault<GomObjectData>("cosVisualData", null);
      if (visual == null) return null;
      ulong appearanceId = visual.ValueOrDefault<ulong>("npcTemplateVisualDataAppearance", 0);
      if (appearanceId == 0) return null;
      GomObject appearanceObj = currentDom.GetObject(appearanceId);
      if (appearanceObj == null) return null;
      NpcAppearance appearance = currentDom.AppearanceLoader.Load(appearanceObj.Name) as NpcAppearance;
      if (appearance == null) return null;
      float scale = visual.ValueOrDefault<float>("cosTemplateVisualDataScale", 1f);
      if (!(scale > 0)) scale = visual.ValueOrDefault<float>("npcTemplateVisualDataScaleAdjustment", 1f);
      if (!(scale > 0)) scale = 1f;
      string bodyType = appearance.BodyType;
      if (String.IsNullOrWhiteSpace(bodyType) && appearance.AppearanceSlotMap != null) {
        AppSlot bodySlot = appearance.AppearanceSlotMap.Values.Where(x => x != null).SelectMany(x => x).FirstOrDefault(x => x != null && !String.IsNullOrWhiteSpace(x.BodyType));
        bodyType = bodySlot?.BodyType;
      }
      var placement = new WorldNpcPlacement {
        Room = room, Instance = instance, SourceFqn = fqn, Name = fqn, Scale = scale, ShowNameplate = false,
        IsTaxiTerminal = WorldNpcLooksLikeTaxiTerminal(fqn, fqn, null),
        BodyType = bodyType, Animation = ResolveNpcAnimationClip(null, bodyType, animationCache),
        AnimationPhase = StableAnimationPhase(instance?.ID ?? 0, fqn)
      };
      foreach (GR2 model in GetNpcAppearanceModels(appearance, appearanceCache)) placement.Models.Add(model);
      placement.Items = ResolveVisualItemNames(visual);
      return placement.Models.Count > 0 ? placement : null;
    }

    private WorldNpcPlacement BuildNpcPlacement(Room room, AssetInstance instance, string sourceFqn, Npc npc, Dictionary<string, List<GR2>> appearanceCache, Dictionary<ulong, float> speciesScales, string idleAnimationName, Dictionary<string, WorldNpcAnimationClip> animationCache) {
      if (npc?.VisualDataList == null || npc.VisualDataList.Count == 0) return null;
      NpcVisualData visual = npc.VisualDataList.FirstOrDefault(x => x != null && !String.IsNullOrWhiteSpace(x.AppearanceFqn));
      if (visual == null) return null;
      NpcAppearance appearance = visual.Appearance;
      if (appearance == null) return null;
      float scale = visual.ScaleAdjustment > 0 ? visual.ScaleAdjustment : 1f;
      if (visual.SpeciesScale != 0 && speciesScales != null) {
        ulong speciesKey = unchecked((ulong)visual.SpeciesScale);
        if (speciesScales.TryGetValue(speciesKey, out float speciesScale) && speciesScale > 0f) scale *= speciesScale;
      }
      string title = npc.Title;
      if (npc.LocalizedTitle != null && npc.LocalizedTitle.TryGetValue(GomLib.StringTable.SelectedLocalization, out string localizedTitle) && !String.IsNullOrWhiteSpace(localizedTitle)) title = localizedTitle;
      string displayName = LocalizedNpcName(npc, sourceFqn);
      string bodyType = appearance.BodyType;
      if (String.IsNullOrWhiteSpace(bodyType) && appearance.AppearanceSlotMap != null) {
        AppSlot bodySlot = appearance.AppearanceSlotMap.Values.Where(x => x != null).SelectMany(x => x).FirstOrDefault(x => x != null && !String.IsNullOrWhiteSpace(x.BodyType));
        bodyType = bodySlot?.BodyType;
      }
      WorldNpcAnimationClip animation = ResolveNpcAnimationClip(idleAnimationName, bodyType, animationCache);
      var placement = new WorldNpcPlacement {
        Room = room, Instance = instance, SourceFqn = sourceFqn, Name = displayName,
        Title = title, IsTaxiTerminal = WorldNpcLooksLikeTaxiTerminal(sourceFqn, displayName, title),
        Scale = scale, Items = ResolveVisualItemNames(visual), ShowNameplate = true,
        RepublicReaction = npc.DetFaction?.RepublicReaction, ImperialReaction = npc.DetFaction?.ImperialReaction, HasFactionPackage = npc.DetFaction != null,
        IdleAnimationName = idleAnimationName, AnimationPhase = StableAnimationPhase(instance?.ID ?? 0, sourceFqn), BodyType = bodyType, Animation = animation
      };
      foreach (GR2 model in GetNpcAppearanceModels(appearance, appearanceCache)) placement.Models.Add(model);
      return placement.Models.Count > 0 ? placement : null;
    }

    private sealed class NpcAnimationSpec {
      public string Category;
      public string Folder;
      public string SkeletonPath;
    }

    private WorldNpcAnimationClip ResolveNpcAnimationClip(string displayName, string bodyType, Dictionary<string, WorldNpcAnimationClip> cache) {
      if (String.IsNullOrWhiteSpace(bodyType) || currentAssets == null || currentDom == null) return null;
      string cacheName = String.IsNullOrWhiteSpace(displayName) ? "<default>" : displayName.Trim();
      string key = cacheName + "|" + bodyType.Trim().ToLowerInvariant();
      if (cache != null && cache.TryGetValue(key, out WorldNpcAnimationClip cached)) return cached;

      WorldNpcAnimationClip result = null;
      try {
        string action = null;
        string network = null;
        if (!String.IsNullOrWhiteSpace(displayName)) ResolveNpcAnimationInfo(displayName.Trim(), out action, out network);

        List<NpcAnimationSpec> specs = ResolveNpcAnimationSpecs(bodyType);
        if (specs.Count > 0) {
          string cleanNetwork = AnimationStem(network);
          var clipCandidates = new[] { AnimationStem(action), AnimationStem(network), AnimationStem(displayName) }
            .Where(x => !String.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
          foreach (string clipCandidate in clipCandidates) {
            foreach (NpcAnimationSpec spec in specs) {
              result = TryLoadNpcAnimationClip(spec, clipCandidate, cleanNetwork, displayName);
              if (result != null) break;
            }
            if (result != null) break;
          }
        }

        // Jedipedia/the client does not leave a spawner rigid when no authored pose was supplied (or the pose could
        // not be resolved). Probe the body type's rest loop. Humanoid/npc/some droid rigs use ex_stand_idle_1 while
        // creatures/pets/the remaining droids use ex_idle_1; the first shipped spelling wins.
        if (result == null) {
          foreach (NpcAnimationSpec spec in specs) {
            result = TryLoadNpcAnimationClip(spec, "ex_stand_idle_1", "humanoid_loco_idle", displayName ?? "Idle");
            if (result == null) result = TryLoadNpcAnimationClip(spec, "ex_idle_1", "creature_loco_idle", displayName ?? "Idle");
            if (result != null) break;
          }
        }
      } catch (Exception ex) {
        System.Diagnostics.Debug.WriteLine("NPC idle animation resolution failed " + (displayName ?? "<default>") + "/" + bodyType + ": " + ex.Message);
      }
      if (cache != null) cache[key] = result;
      return result;
    }

    private void ResolveNpcLocomotionClips(string bodyType, Dictionary<string, WorldNpcAnimationClip> cache,
        out WorldNpcAnimationClip walk, out WorldNpcAnimationClip run) {
      walk = null; run = null;
      if (String.IsNullOrWhiteSpace(bodyType) || currentAssets == null) return;
      string bt = bodyType.Trim().ToLowerInvariant();
      string walkKey = "<locomotion-walk>|" + bt;
      string runKey = "<locomotion-run>|" + bt;
      bool walkCached = cache != null && cache.TryGetValue(walkKey, out walk);
      bool runCached = cache != null && cache.TryGetValue(runKey, out run);
      if (walkCached && runCached) return;

      try {
        List<NpcAnimationSpec> specs = ResolveNpcAnimationSpecs(bodyType);
        foreach (NpcAnimationSpec spec in specs) {
          if (walk != null && run != null) break;
          string family = String.Equals(spec.Category, "creature", StringComparison.OrdinalIgnoreCase) || String.Equals(spec.Category, "pet", StringComparison.OrdinalIgnoreCase)
            ? "creature" : "humanoid";
          string[] networks = { family + "_loco", "anim_library", family + "_loco_idle", "droid_loco", "npc_loco" };

          // Cheap direct probes cover the common shipped spellings without parsing the multi-megabyte anim_library.
          if (walk == null) {
            foreach (string candidate in new[] { "ex_stand_walk_forward", "ex_stand_walk_fwd", "ex_walk_forward", "ex_walk_fwd", "ex_stand_walk", "ex_walk" }) {
              foreach (string network in networks) {
                walk = TryLoadNpcAnimationClip(spec, candidate, network, "Walk");
                if (walk != null) break;
              }
              if (walk != null) break;
            }
          }
          if (run == null) {
            foreach (string candidate in new[] { "ex_stand_run_forward", "ex_stand_run_fwd", "ex_run_forward", "ex_run_fwd", "ex_stand_run", "ex_run", "ex_sprint" }) {
              foreach (string network in networks) {
                run = TryLoadNpcAnimationClip(spec, candidate, network, "Run");
                if (run != null) break;
              }
              if (run != null) break;
            }
          }

          // Body types use many non-obvious clip names. Let the actual Morpheme animation list tell us what the
          // network ships, then rank forward locomotion leaves by name. This is still cached once per body type.
          if (walk == null || run == null) {
            foreach (string network in networks.Distinct(StringComparer.OrdinalIgnoreCase)) {
              string mphPath = "/resources/anim/" + spec.Category + "/" + spec.Folder + "/" + network + ".mph";
              using File mphFile = currentAssets.FindFile(mphPath);
              if (mphFile == null) continue;
              List<string> names;
              try {
                using Stream mphStream = mphFile.OpenCopyInMemory();
                using var mphReader = new BinaryReader(mphStream);
                names = MPHAnimationReader.FindIdleClipNames(mphReader);
              } catch { continue; }
              if (names == null || names.Count == 0) continue;

              if (walk == null) {
                string bestWalk = names.Select(n => new { Name = n, Score = ScoreNpcLocomotionClip(n, false) })
                  .Where(x => x.Score > 0).OrderByDescending(x => x.Score).Select(x => x.Name).FirstOrDefault();
                if (!String.IsNullOrWhiteSpace(bestWalk)) walk = TryLoadNpcAnimationClip(spec, bestWalk, network, "Walk");
              }
              if (run == null) {
                string bestRun = names.Select(n => new { Name = n, Score = ScoreNpcLocomotionClip(n, true) })
                  .Where(x => x.Score > 0).OrderByDescending(x => x.Score).Select(x => x.Name).FirstOrDefault();
                if (!String.IsNullOrWhiteSpace(bestRun)) run = TryLoadNpcAnimationClip(spec, bestRun, network, "Run");
              }
              if (walk != null && run != null) break;
            }
          }
        }
      } catch (Exception ex) {
        System.Diagnostics.Debug.WriteLine("NPC locomotion resolution failed " + bodyType + ": " + ex.Message);
      }
      if (cache != null) { cache[walkKey] = walk; cache[runKey] = run; }
    }

    private static int ScoreNpcLocomotionClip(string name, bool run) {
      if (String.IsNullOrWhiteSpace(name)) return Int32.MinValue;
      string n = name.ToLowerInvariant();
      bool hasRun = n.Contains("run") || n.Contains("sprint");
      bool hasWalk = n.Contains("walk");
      if (run ? !hasRun : !hasWalk) return Int32.MinValue;
      int score = 1000;
      if (n.Contains("forward") || n.Contains("_fwd") || n.EndsWith("fwd", StringComparison.Ordinal)) score += 500;
      if (n.Contains("stand")) score += 120;
      if (n.Contains("loop")) score += 80;
      if (run && n.Contains("sprint")) score += 100;
      foreach (string bad in new[] { "back", "strafe", "turn", "left", "right", "jump", "fall", "swim", "mount", "combat", "weapon", "idle" })
        if (n.Contains(bad)) score -= 700;
      return score;
    }

    private void ResolveNpcAnimationInfo(string displayName, out string action, out string network) {
      action = null;
      network = null;
      if (String.IsNullOrWhiteSpace(displayName)) return;
      try {
        GomObject hyd = currentDom.GetObject("hydAnimationInfoPrototype");
        if (hyd != null) {
          var byDisplay = hyd.Data.ValueOrDefault<Dictionary<object, object>>("hydAnimationByDisplayName", null);
          var animations = hyd.Data.ValueOrDefault<Dictionary<object, object>>("hydAnimations", null);
          object animationId = null;
          if (byDisplay != null) {
            foreach (var pair in byDisplay) {
              if (String.Equals(pair.Key?.ToString(), displayName, StringComparison.OrdinalIgnoreCase)) { animationId = pair.Value; break; }
            }
          }
          GomObjectData row = FindAnimationRow(animations, animationId);
          if (row != null) {
            action = row.ValueOrDefault<string>("hydAnimationAction", null);
            network = row.ValueOrDefault<string>("hydAnimationLocoNetwork", null);
          }
        }

        // spnNpcIdleAnimationName may also name an npcIdlePackage rather than hydAnimationByDisplayName directly.
        if (String.IsNullOrWhiteSpace(action)) {
          GomObject packages = currentDom.GetObject("npcIdlePackagePrototype");
          var packageMap = packages?.Data.ValueOrDefault<Dictionary<object, object>>("npcIdlePackageMap", null);
          if (packageMap != null) {
            object packageObject = null;
            foreach (var pair in packageMap) {
              if (String.Equals(pair.Key?.ToString(), displayName, StringComparison.OrdinalIgnoreCase)) { packageObject = pair.Value; break; }
            }
            if (packageObject is GomObjectData packageRow) {
              object animationId = packageRow.ValueOrDefault<object>("npcIdlePackageAnim", null);
              GomObject idleHyd = currentDom.GetObject("hydAnimationInfoPrototype");
              var animations = idleHyd?.Data.ValueOrDefault<Dictionary<object, object>>("hydAnimations", null);
              GomObjectData animationRow = FindAnimationRow(animations, animationId);
              if (animationRow != null) {
                action = animationRow.ValueOrDefault<string>("hydAnimationAction", null);
                network = animationRow.ValueOrDefault<string>("hydAnimationLocoNetwork", null);
              } else if (animationId != null) action = animationId.ToString();
            }
          }
        }
      } catch { }
      if (String.IsNullOrWhiteSpace(action)) action = displayName;
    }

    private static GomObjectData FindAnimationRow(Dictionary<object, object> animations, object animationId) {
      if (animations == null || animationId == null) return null;
      if (animations.TryGetValue(animationId, out object exact) && exact is GomObjectData exactRow) return exactRow;
      string wanted = animationId.ToString();
      foreach (var pair in animations) if (String.Equals(pair.Key?.ToString(), wanted, StringComparison.OrdinalIgnoreCase)) return pair.Value as GomObjectData;
      return null;
    }

    private List<NpcAnimationSpec> ResolveNpcAnimationSpecs(string bodyType) {
      var result = new List<NpcAnimationSpec>();
      string bt = (bodyType ?? String.Empty).Trim().ToLowerInvariant();
      if (String.IsNullOrEmpty(bt)) return result;

      // Authoritative character-spec path, matching Jedipedia viewerNpcAnimSpec(): the body-type DAT names
      // both the Morpheme animation directory and the DYC whose Skeleton= entry is the actual rig that the
      // individual appearance GR2 parts are skinned to. Most appearance parts do NOT embed that skeleton.
      try {
        using File specFile = currentAssets.FindFile("/resources/art/dynamic/spec/" + bt + ".dat");
        if (specFile != null) {
          using Stream stream = specFile.OpenCopyInMemory();
          using var reader = new StreamReader(stream);
          Dictionary<string, string> values = ParseNpcSpec(reader.ReadToEnd());
          string skeletonPath = null;
          if (values.TryGetValue("Model", out string modelSpec) && !String.IsNullOrWhiteSpace(modelSpec)) {
            string dycPath = NormalizeNpcSpecPath(modelSpec);
            using File dycFile = currentAssets.FindFile(dycPath);
            if (dycFile != null) {
              using Stream dycStream = dycFile.OpenCopyInMemory();
              using var dycReader = new StreamReader(dycStream);
              Dictionary<string, string> dycValues = ParseNpcSpec(dycReader.ReadToEnd());
              if (dycValues.TryGetValue("Skeleton", out string skeletonSpec) && !String.IsNullOrWhiteSpace(skeletonSpec))
                skeletonPath = NormalizeNpcSpecPath(skeletonSpec);
            }
          }
          if (values.TryGetValue("AnimNetworkFolder", out string folderPath) && !String.IsNullOrWhiteSpace(folderPath)) {
            string[] parts = folderPath.Replace('\\', '/').Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2) AddNpcAnimationSpec(result, parts[parts.Length - 2], parts[parts.Length - 1], skeletonPath);
          }
        }
      } catch (Exception ex) {
        System.Diagnostics.Debug.WriteLine("NPC animation spec failed " + bt + ": " + ex.Message);
      }

      // Pre-spec fallback used by Jedipedia for body types without a shipped DAT. The skeleton follows the
      // guessed folder even when the animation category must still be probed across the game's five roots.
      foreach (string folder in new[] { bt + "new", bt }) {
        string skeletonPath = "/resources/art/dynamic/spec/" + folder + "_skeleton.gr2";
        if (currentAssets.FindFile(skeletonPath) == null) skeletonPath = null;
        foreach (string category in new[] { "humanoid", "creature", "droid", "npc", "pet" }) AddNpcAnimationSpec(result, category, folder, skeletonPath);
      }
      return result;
    }

    private static string NormalizeNpcSpecPath(string relative) {
      string path = (relative ?? String.Empty).Trim().Replace('\\', '/');
      while (path.StartsWith("/", StringComparison.Ordinal)) path = path.Substring(1);
      if (path.StartsWith("resources/", StringComparison.OrdinalIgnoreCase)) return "/" + path.ToLowerInvariant();
      if (path.StartsWith("art/dynamic/spec/", StringComparison.OrdinalIgnoreCase)) return "/resources/" + path.ToLowerInvariant();
      return "/resources/art/dynamic/spec/" + path.ToLowerInvariant();
    }

    private static Dictionary<string, string> ParseNpcSpec(string text) {
      var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
      using var reader = new StringReader(text ?? String.Empty);
      string line;
      while ((line = reader.ReadLine()) != null) {
        line = line.Trim();
        if (line.Length == 0 || line == "!" || line.StartsWith("[", StringComparison.Ordinal)) continue;
        int equals = line.IndexOf('=');
        if (equals <= 0) continue;
        string name = line.Substring(0, equals).Trim();
        string fieldValue = line.Substring(equals + 1).Trim();
        if (!values.ContainsKey(name)) values.Add(name, fieldValue);
      }
      return values;
    }

    private static void AddNpcAnimationSpec(List<NpcAnimationSpec> list, string category, string folder, string skeletonPath) {
      if (list == null || String.IsNullOrWhiteSpace(category) || String.IsNullOrWhiteSpace(folder)) return;
      category = category.Trim().ToLowerInvariant();
      folder = folder.Trim().ToLowerInvariant();
      NpcAnimationSpec existing = list.FirstOrDefault(x => String.Equals(x.Category, category, StringComparison.OrdinalIgnoreCase) && String.Equals(x.Folder, folder, StringComparison.OrdinalIgnoreCase));
      if (existing != null) { if (String.IsNullOrWhiteSpace(existing.SkeletonPath) && !String.IsNullOrWhiteSpace(skeletonPath)) existing.SkeletonPath = skeletonPath; return; }
      list.Add(new NpcAnimationSpec { Category = category, Folder = folder, SkeletonPath = skeletonPath });
    }

    private static string AnimationStem(string name) {
      if (String.IsNullOrWhiteSpace(name)) return null;
      return Path.GetFileNameWithoutExtension(name.Trim().Replace('\\', '/')).ToLowerInvariant();
    }

    private WorldNpcAnimationClip TryLoadNpcAnimationClip(NpcAnimationSpec spec, string clipName, string networkName, string displayName) {
      if (spec == null || String.IsNullOrWhiteSpace(clipName)) return null;
      string basePath = "/resources/anim/" + spec.Category + "/" + spec.Folder + "/";
      using File jbaFile = currentAssets.FindFile(basePath + clipName + ".jba");
      if (jbaFile == null) return null;

      JBAAnimation animation;
      using (Stream stream = jbaFile.OpenCopyInMemory()) using (var br = new BinaryReader(stream)) animation = JBAReader.Read(br);
      if (animation == null || animation.BoneCount <= 0 || animation.Length <= 0f) return null;
      animation.PrepareSamples();

      JBARig rig = null;
      foreach (string mappingName in new[] { AnimationStem(networkName), "anim_library" }.Where(x => !String.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase)) {
        using File mphFile = currentAssets.FindFile(basePath + mappingName + ".mph");
        if (mphFile == null) continue;
        try {
          using Stream mphStream = mphFile.OpenCopyInMemory();
          using var mphReader = new BinaryReader(mphStream);
          rig = MPHAnimationReader.FindRigForClip(mphReader, clipName);
          if (rig != null) break;
        } catch { }
      }
      IList<GR2_Bone_Skeleton> skeleton = LoadNpcAnimationSkeleton(spec.SkeletonPath);
      return new WorldNpcAnimationClip {
        DisplayName = String.IsNullOrWhiteSpace(displayName) ? clipName : displayName.Trim(),
        Action = clipName,
        Network = AnimationStem(networkName),
        Animation = animation,
        Rig = rig,
        Skeleton = skeleton
      };
    }

    private IList<GR2_Bone_Skeleton> LoadNpcAnimationSkeleton(string skeletonPath) {
      if (String.IsNullOrWhiteSpace(skeletonPath) || currentAssets == null) return null;
      try {
        using File skeletonFile = currentAssets.FindFile(skeletonPath);
        if (skeletonFile == null) return null;
        using Stream skeletonStream = skeletonFile.OpenCopyInMemory();
        using var skeletonReader = new BinaryReader(skeletonStream);
        GR2 skeletonModel = new GR2(skeletonReader, skeletonPath);
        if (skeletonModel.skeleton_bones == null || skeletonModel.skeleton_bones.Count == 0) return null;
        // Only the parsed bone data is retained. GPU buffers are never created for this rig-only GR2.
        return skeletonModel.skeleton_bones.ToList();
      } catch (Exception ex) {
        System.Diagnostics.Debug.WriteLine("NPC animation skeleton failed " + skeletonPath + ": " + ex.Message);
        return null;
      }
    }

    private static string SpawnerIdleAnimationName(AssetInstance instance) {
      if (instance?.ParsedProperties == null || !instance.ParsedProperties.TryGetValue(0x8EA2A60Bu, out object raw) || raw == null) return null;
      string text = raw.ToString().Replace("\0", String.Empty).Trim();
      return String.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static float StableAnimationPhase(ulong id, string fqn) {
      unchecked {
        uint hash = (uint)(id ^ (id >> 32));
        if (!String.IsNullOrEmpty(fqn)) foreach (char ch in fqn) hash = hash * 16777619u ^ ch;
        return (hash & 0xFFFFu) / 65535f;
      }
    }

    private string LocalizedNpcName(Npc npc, string fallbackFqn) {
      if (npc == null) return PrettySpawnName(fallbackFqn);
      string selected = GomLib.StringTable.SelectedLocalization;
      if (npc.LocalizedName != null && npc.LocalizedName.TryGetValue(selected, out string localized) && IsRealLocalizedName(localized, fallbackFqn)) return localized.Trim();
      if (IsRealLocalizedName(npc.Name, fallbackFqn)) return npc.Name.Trim();
      try {
        GomLib.StringTable table = currentDom?.StringTable?.Find("str.npc");
        if (table != null && npc.NameId != 0) {
          string fromStrNpc = table.GetText(npc.NameId, fallbackFqn, selected);
          if (IsRealLocalizedName(fromStrNpc, fallbackFqn)) return fromStrNpc.Trim();
        }
      } catch { }
      // Unlike the initial asynchronous Jedipedia placeholder, this loader has already finished its STB lookup.
      // If the localized text genuinely does not exist, leave the plate unnamed rather than leaking npc.* internals.
      return null;
    }

    private static bool IsRealLocalizedName(string text, string fqn) {
      if (String.IsNullOrWhiteSpace(text)) return false;
      string t = text.Trim();
      if (!String.IsNullOrWhiteSpace(fqn) && String.Equals(t, fqn, StringComparison.OrdinalIgnoreCase)) return false;
      return !t.StartsWith("npc.", StringComparison.OrdinalIgnoreCase) && !t.StartsWith("plc.", StringComparison.OrdinalIgnoreCase);
    }

    private static string PrettySpawnName(string fqn) {
      if (String.IsNullOrWhiteSpace(fqn)) return String.Empty;
      string leaf = fqn.Trim().Split('.').LastOrDefault() ?? fqn;
      leaf = leaf.Replace('_', ' ').Trim();
      if (leaf.Length == 0) return fqn;
      return System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(leaf.ToLowerInvariant());
    }

    private sealed class SpnDynPreviewTemplate {
      public string StartState;
      public readonly List<GR2> Models = new List<GR2>();
      public readonly List<WorldSpnDynState> States = new List<WorldSpnDynState>();
      public readonly List<WorldSpnDynLight> Lights = new List<WorldSpnDynLight>();
    }

    private WorldSpnPlacement BuildSpnPlaceablePlacement(Room room, AssetInstance instance, string sourceFqn,
        Dictionary<string, List<GR2>> modelCache, Dictionary<string, WorldNpcAnimationClip> animationCache,
        Dictionary<string, SpnDynPreviewTemplate> dynCache) {
      Placeable placeable = null;
      GomObject node = null;
      try { placeable = currentDom.PlaceableLoader.Load(sourceFqn); } catch { }
      try { node = currentDom.GetObject(sourceFqn); } catch { }

      SpnDynPreviewTemplate dynTemplate = null;
      if (dynCache != null && dynCache.TryGetValue(sourceFqn, out SpnDynPreviewTemplate cachedDyn)) dynTemplate = cachedDyn;
      else {
        dynTemplate = BuildSpnDynPreviewTemplate(placeable, node);
        if (dynCache != null) dynCache[sourceFqn] = dynTemplate;
      }

      WorldNpcAnimationClip animation = null;
      List<GR2> loaded;
      if (dynTemplate != null) loaded = dynTemplate.Models;
      else loaded = GetSpnPlaceableModels(sourceFqn, placeable, node, modelCache, animationCache, out animation);
      // A dyn assembly may intentionally contain only a light row. Jedipedia keeps that placement even without
      // geometry, because the light is a world-space contribution rather than a drawable model.
      if ((loaded == null || loaded.Count == 0) && (dynTemplate == null || dynTemplate.Lights.Count == 0)) return null;

      string name = placeable?.Name;
      if (placeable?.LocalizedName != null && placeable.LocalizedName.TryGetValue(GomLib.StringTable.SelectedLocalization, out string localized) && IsRealLocalizedName(localized, sourceFqn)) name = localized;
      if (!IsRealLocalizedName(name, sourceFqn)) name = PrettySpawnName(sourceFqn);
      var placement = new WorldSpnPlacement {
        Room = room, Instance = instance, SourceFqn = sourceFqn, Name = name, Scale = 1f, Animation = animation,
        AnimationPhase = StableAnimationPhase(instance?.ID ?? 0, sourceFqn),
        DynStartState = dynTemplate?.StartState,
        BlueGlow = SpnPlaceableHasBlueGlow(placeable, node)
      };
      if (loaded != null) foreach (GR2 model in loaded) if (model != null && !placement.Models.Contains(model)) placement.Models.Add(model);
      if (dynTemplate != null) {
        foreach (WorldSpnDynState state in dynTemplate.States) placement.DynStates.Add(state);
        foreach (WorldSpnDynLight light in dynTemplate.Lights) placement.DynLights.Add(light);
      }
      return placement;
    }

    private SpnDynPreviewTemplate BuildSpnDynPreviewTemplate(Placeable placeable, GomObject plcNode) {
      if (plcNode == null) return null;
      string modelReference = plcNode.Data.ValueOrDefault<string>("plcModel", null);
      if (String.IsNullOrWhiteSpace(modelReference)) modelReference = placeable?.Model;
      if (String.IsNullOrWhiteSpace(modelReference) || modelReference.EndsWith(".gr2", StringComparison.OrdinalIgnoreCase) ||
          modelReference.EndsWith(".mag", StringComparison.OrdinalIgnoreCase)) return null;

      string dynFqn = modelReference.Trim().Replace('/', '.').Replace('\\', '.').Trim('.');
      GomObject dynNode = null;
      try { dynNode = currentDom.GetObject(dynFqn); } catch { }
      if (dynNode == null) return null;
      var rows = dynNode.Data.ValueOrDefault<List<object>>("dynObjectDataList", null)
        ?? dynNode.Data.ValueOrDefault<List<object>>("dynVisualList", null);
      if (rows == null || rows.Count == 0) return null;

      string startState = CleanGomString(plcNode.Data.ValueOrDefault<object>("plcdynStartState", null));
      var template = new SpnDynPreviewTemplate { StartState = startState };
      List<string> stateNames = SpnDynStateNames(dynNode, rows, startState);
      if (stateNames.Count == 0) stateNames.Add(null);

      var modelByVisual = new Dictionary<string, GR2>(StringComparer.OrdinalIgnoreCase);
      var animationByVisual = new Dictionary<string, WorldNpcAnimationClip>(StringComparer.OrdinalIgnoreCase);

      foreach (string stateName in stateNames) {
        var state = new WorldSpnDynState { Name = stateName };
        for (int rowIndex = 0; rowIndex < rows.Count; rowIndex++) {
          if (rows[rowIndex] is not GomObjectData row) continue;
          string visual = CleanGomString(row.ValueOrDefault<object>("dynVisualFqn", null));
          if (String.IsNullOrWhiteSpace(visual) || SpnDynVisualHidden(row, visual) || SpnDynRowIsLight(row, visual)) continue;
          bool isMag = visual.EndsWith(".mag", StringComparison.OrdinalIgnoreCase);
          if (!isMag && !visual.EndsWith(".gr2", StringComparison.OrdinalIgnoreCase)) continue;

          Dictionary<string, GomObjectData> rowStates = SpnDynRowStates(dynNode, row, rowIndex);
          if (!SpnDynRowVisible(rowStates, stateName)) continue;
          GomObjectData selectedState = !String.IsNullOrWhiteSpace(stateName) && rowStates != null && rowStates.TryGetValue(stateName, out GomObjectData stateRow)
            ? stateRow : null;
          string action = selectedState == null ? null : CleanGomString(selectedState.ValueOrDefault<object>("dynMagName", null));

          if (isMag && SpnMagActionHidden(visual, action)) {
            // Jedipedia treats a hidden animated part as the object itself being hidden for this dyn state.
            state.Hidden = true;
            state.Parts.Clear();
            break;
          }

          if (!modelByVisual.TryGetValue(visual, out GR2 model)) {
            WorldNpcAnimationClip partAnimation = null;
            model = isMag ? LoadSpnMag(visual, out partAnimation) : LoadSpnModel(visual);
            modelByVisual[visual] = model;
            if (partAnimation != null) animationByVisual[visual] = partAnimation;
            if (model != null && !template.Models.Contains(model)) template.Models.Add(model);
          }
          if (model == null) continue;
          animationByVisual.TryGetValue(visual, out WorldNpcAnimationClip animation);
          state.Parts.Add(new WorldSpnDynPart {
            Model = model,
            LocalMatrix = SpnDynLocalMatrix(row),
            Animation = animation,
            BlueGlow = SpnDynRowBlueGlow(rowStates, stateName)
          });
        }
        template.States.Add(state);
      }

      foreach (WorldSpnDynLight light in BuildSpnDynLights(dynNode, rows, stateNames)) template.Lights.Add(light);
      return template.Models.Count == 0 && template.Lights.Count == 0 ? null : template;
    }

    private static string CleanGomString(object value) {
      if (value == null) return null;
      string text = value.ToString()?.Replace("\0", String.Empty).Trim();
      return String.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static List<string> SpnDynStateNames(GomObject dynNode, List<object> rows, string startState) {
      var result = new List<string>();
      if (!String.IsNullOrWhiteSpace(startState)) result.Add(startState);
      if (String.IsNullOrWhiteSpace(startState) || rows == null) return result;
      for (int i = 0; i < rows.Count; i++) {
        if (rows[i] is not GomObjectData row) continue;
        Dictionary<string, GomObjectData> states = SpnDynRowStates(dynNode, row, i);
        if (states == null) continue;
        foreach (string name in states.Keys)
          if (!String.IsNullOrWhiteSpace(name) && !result.Contains(name, StringComparer.OrdinalIgnoreCase)) result.Add(name);
      }
      return result;
    }

    private static Dictionary<string, GomObjectData> SpnDynRowStates(GomObject dynNode, GomObjectData row, int rowIndex) {
      var result = new Dictionary<string, GomObjectData>(StringComparer.OrdinalIgnoreCase);
      Dictionary<object, object> current = row?.ValueOrDefault<Dictionary<object, object>>("dynStateToDynObjectState", null);
      if (current != null) {
        foreach (var pair in current) if (pair.Value is GomObjectData state) {
          string name = CleanGomString(pair.Key);
          if (!String.IsNullOrWhiteSpace(name)) result[name] = state;
        }
        if (result.Count > 0) return result;
      }

      Dictionary<object, object> legacy = dynNode?.Data.ValueOrDefault<Dictionary<object, object>>("dynStateNameToListOfVisualStates", null);
      if (legacy == null) return result.Count == 0 ? null : result;
      foreach (var pair in legacy) {
        string name = CleanGomString(pair.Key);
        if (String.IsNullOrWhiteSpace(name) || pair.Value is not List<object> stateRows || rowIndex < 0 || rowIndex >= stateRows.Count) continue;
        if (stateRows[rowIndex] is GomObjectData state) result[name] = state;
      }
      return result.Count == 0 ? null : result;
    }

    private static bool SpnPlaceableHasBlueGlow(Placeable placeable, GomObject node) {
      if (placeable != null && (placeable.AbilitySpecOnUseId != 0 || placeable.CodexId != 0 || placeable.LootPackageId != 0)) return true;
      if (node?.Data == null) return false;
      return SpnGlowValueSet(node.Data.ValueOrDefault<object>("plcAbilitySpecOnUse", null)) ||
        SpnGlowValueSet(node.Data.ValueOrDefault<object>("plcAbilitySpecOnLoot", null)) ||
        SpnGlowValueSet(node.Data.ValueOrDefault<object>("plcCodexSpec", null)) ||
        SpnGlowValueSet(node.Data.ValueOrDefault<object>("plcdynLootPackage", null)) ||
        SpnGlowValueSet(node.Data.ValueOrDefault<object>("plcTreasureChestLootLevel", null));
    }

    private static bool SpnGlowValueSet(object value) {
      if (value == null) return false;
      if (value is string text) return !String.IsNullOrWhiteSpace(text) && text.Trim() != "0";
      if (value is ScriptEnum scriptEnum) return scriptEnum.Value >= 0;
      try { return Convert.ToDecimal(value, System.Globalization.CultureInfo.InvariantCulture) != 0m; } catch { return true; }
    }

    private static bool SpnDynRowBlueGlow(Dictionary<string, GomObjectData> states, string stateName) {
      if (String.IsNullOrWhiteSpace(stateName) || states == null || !states.TryGetValue(stateName, out GomObjectData state) || state == null) return false;
      if (!state.Dictionary.TryGetValue("dynState", out object raw) || raw == null) return false;
      return (SpnDynInteger(raw, 0) & 4L) != 0;
    }

    private static bool SpnDynRowVisible(Dictionary<string, GomObjectData> states, string stateName) {
      if (String.IsNullOrWhiteSpace(stateName) || states == null || states.Count == 0) return true;
      bool authored = false;
      bool visible = false;
      foreach (var pair in states) {
        if (pair.Value == null || !pair.Value.Dictionary.TryGetValue("dynState", out object raw) || raw == null) continue;
        authored = true;
        long flags = SpnDynInteger(raw, 0);
        if (String.Equals(pair.Key, stateName, StringComparison.OrdinalIgnoreCase) && (flags & 1L) != 0) visible = true;
      }
      return authored ? visible : true;
    }

    private static long SpnDynInteger(object value, long fallback) {
      if (value == null) return fallback;
      if (value is ScriptEnum scriptEnum) return scriptEnum.Value + 1L;
      try { return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture); } catch { }
      string text = CleanGomString(value);
      if (String.IsNullOrWhiteSpace(text)) return fallback;
      if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
          Int64.TryParse(text.Substring(2), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out long hex)) return hex;
      return Int64.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out long parsed) ? parsed : fallback;
    }

    private static float SpnDynNumber(object value, float fallback) {
      if (value == null) return fallback;
      if (value is ScriptEnum scriptEnum) return scriptEnum.Value + 1f;
      try {
        float number = Convert.ToSingle(value, System.Globalization.CultureInfo.InvariantCulture);
        return Single.IsNaN(number) || Single.IsInfinity(number) ? fallback : number;
      } catch { return fallback; }
    }

    private static Vector3 SpnDynVector3(object value, Vector3 fallback) {
      if (value is List<float> floats && floats.Count >= 3) return new Vector3(floats[0], floats[1], floats[2]);
      if (value is List<object> objects && objects.Count >= 3)
        return new Vector3(SpnDynNumber(objects[0], fallback.X), SpnDynNumber(objects[1], fallback.Y), SpnDynNumber(objects[2], fallback.Z));
      if (value is System.Collections.IList list && list.Count >= 3)
        return new Vector3(SpnDynNumber(list[0], fallback.X), SpnDynNumber(list[1], fallback.Y), SpnDynNumber(list[2], fallback.Z));
      return fallback;
    }

    private static Matrix SpnDynLocalMatrix(GomObjectData row) {
      Vector3 position = SpnDynVector3(row?.ValueOrDefault<object>("dynPosition", null), Vector3.Zero);
      Vector3 rotation = SpnDynVector3(row?.ValueOrDefault<object>("dynRotation", null), Vector3.Zero);
      Vector3 scale = SpnDynVector3(row?.ValueOrDefault<object>("dynScale", null), new Vector3(1f, 1f, 1f));
      float rx = rotation.X * (float)Math.PI / 180f, ry = rotation.Y * (float)Math.PI / 180f, rz = rotation.Z * (float)Math.PI / 180f;
      // Row-vector equivalent of Jedipedia's T · Ry · Rx · Rz · S.
      return Matrix.Scaling(scale) * Matrix.RotationZ(rz) * Matrix.RotationX(rx) * Matrix.RotationY(ry) * Matrix.Translation(position);
    }

    private static bool SpnDynVisualHidden(GomObjectData row, string visual) {
      string path = (visual ?? String.Empty).Replace('\\', '/').ToLowerInvariant();
      string objectName = CleanGomString(row?.ValueOrDefault<object>("dynObjectName", null)) ?? String.Empty;
      if (objectName.StartsWith("dbo_", StringComparison.OrdinalIgnoreCase)) return true;
      string[] pieces = path.Split(new[] { '/', '_', '.' }, StringSplitOptions.RemoveEmptyEntries);
      return pieces.Any(piece => String.Equals(piece, "collision", StringComparison.OrdinalIgnoreCase)) ||
        path.IndexOf("designblockout", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool SpnDynRowIsLight(GomObjectData row, string visual) {
      if (!String.IsNullOrWhiteSpace(visual) && visual.EndsWith(".lit", StringComparison.OrdinalIgnoreCase)) return true;
      object rawType = row?.ValueOrDefault<object>("dynObjectType", null);
      return SpnDynInteger(rawType, -1) == 5;
    }

    private bool SpnMagActionHidden(string magReference, string action) {
      if (String.IsNullOrWhiteSpace(magReference) || String.IsNullOrWhiteSpace(action) || currentAssets == null) return false;
      try {
        string magPath = NormalizeResourceAssetPath(magReference, null);
        using File file = currentAssets.FindFile(magPath);
        if (file == null) return false;
        Dictionary<string, string> values;
        using (Stream stream = file.OpenCopyInMemory()) using (var reader = new StreamReader(stream)) values = ParseNpcSpec(reader.ReadToEnd());
        if (!values.TryGetValue("AnimMetadataFqn", out string metadata) || String.IsNullOrWhiteSpace(metadata)) return false;
        string metadataPath = NormalizeResourceAssetPath(metadata, null);
        using File metadataFile = currentAssets.FindFile(metadataPath);
        if (metadataFile == null) return false;
        string text;
        using (Stream stream = metadataFile.OpenCopyInMemory()) using (var reader = new StreamReader(stream)) text = reader.ReadToEnd();
        string escaped = System.Text.RegularExpressions.Regex.Escape(action.Trim());
        var match = System.Text.RegularExpressions.Regex.Match(text,
          "<action\\s+name=\"" + escaped + "\"[^>]*>([\\s\\S]*?)</action>",
          System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success && System.Text.RegularExpressions.Regex.IsMatch(match.Groups[1].Value,
          "<sa\\s+path=\"mv_hide\"", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
      } catch { return false; }
    }

    private static readonly Dictionary<ulong, string> SpnDynLightPropertyNames = new Dictionary<ulong, string> {
      { 17837464995121910744UL, "Color" },
      { 6349573576868323461UL, "Falloff" },
      { 4748468179436753632UL, "IlluminationMap" },
      { 2600316532131449482UL, "Intensity" },
      { 6521451727119918103UL, "LightType" },
      { 4517970592427740037UL, "RampMap" },
      { 2758068308593703698UL, "Range" },
      { 14067375507992444331UL, "SourceOffset" },
      { 9041169397708211167UL, "RestrictToRoom" },
      { 16232016272567338703UL, "DoGranny" },
      { 8982315057199028444UL, "DoHeightmaps" },
      { 4864343265957239075UL, "DoSpeedTree" },
      { 1118051188720359896UL, "DoCharacters" },
      { 10066959401320433411UL, "DoWater" }
    };

    private List<WorldSpnDynLight> BuildSpnDynLights(GomObject dynNode, List<object> rows, List<string> stateNames) {
      var result = new List<WorldSpnDynLight>();
      if (dynNode == null || rows == null) return result;
      for (int rowIndex = 0; rowIndex < rows.Count; rowIndex++) {
        if (rows[rowIndex] is not GomObjectData row) continue;
        string visual = CleanGomString(row.ValueOrDefault<object>("dynVisualFqn", null));
        if (!SpnDynRowIsLight(row, visual)) continue;

        Dictionary<string, GomObjectData> rowStates = SpnDynRowStates(dynNode, row, rowIndex);
        if (stateNames != null && stateNames.Count == 1 && !SpnDynRowVisible(rowStates, stateNames[0])) continue;

        Dictionary<string, object> properties = SpnDynLightProperties(dynNode, row);
        var light = new WorldSpnDynLight { LocalMatrix = SpnDynLocalMatrix(row) };
        if (properties.TryGetValue("LightType", out object lightType)) light.LightType = SpnDynLightTypeName(lightType);
        if (properties.TryGetValue("SourceOffset", out object sourceOffset)) light.SourceOffset = SpnDynNumber(sourceOffset, 0f);
        if (properties.TryGetValue("Range", out object range)) light.Range = Math.Max(.0001f, SpnDynNumber(range, 1f));
        if (properties.TryGetValue("Intensity", out object intensity)) light.Intensity = SpnDynNumber(intensity, 1f);
        if (properties.TryGetValue("Color", out object color) && TrySpnDynColor(color, out Vector4 parsedColor)) light.Color = parsedColor;
        if (properties.TryGetValue("IlluminationMap", out object illumination)) light.IlluminationMap = CleanGomString(illumination);
        if (properties.TryGetValue("RampMap", out object ramp)) light.RampMap = CleanGomString(ramp);
        if (properties.TryGetValue("Falloff", out object falloff)) light.Falloff = CleanGomString(falloff);
        if (properties.TryGetValue("RestrictToRoom", out object restrict)) light.RestrictToRoom = SpnDynBool(restrict, false);
        if (properties.TryGetValue("DoHeightmaps", out object doHeightmaps)) light.DoHeightmaps = SpnDynBool(doHeightmaps, true);
        if (properties.TryGetValue("DoGranny", out object doGranny)) light.DoGranny = SpnDynBool(doGranny, true);
        if (properties.TryGetValue("DoCharacters", out object doCharacters)) light.DoCharacters = SpnDynBool(doCharacters, true);
        if (properties.TryGetValue("DoWater", out object doWater)) light.DoWater = SpnDynBool(doWater, true);

        if (stateNames != null && stateNames.Count > 1)
          foreach (string stateName in stateNames) if (!String.IsNullOrWhiteSpace(stateName))
            light.StateVisibility[stateName] = SpnDynRowVisible(rowStates, stateName);
        result.Add(light);
      }
      return result;
    }

    private static Dictionary<string, object> SpnDynLightProperties(GomObject dynNode, GomObjectData row) {
      var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
      string objectName = CleanGomString(row?.ValueOrDefault<object>("dynObjectName", null));
      Dictionary<object, object> legacyByName = dynNode?.Data.ValueOrDefault<Dictionary<object, object>>("dynLightNameToProperty", null);
      if (legacyByName != null && !String.IsNullOrWhiteSpace(objectName)) {
        foreach (var pair in legacyByName) {
          if (!String.Equals(CleanGomString(pair.Key), objectName, StringComparison.OrdinalIgnoreCase) || pair.Value is not GomObjectData legacy) continue;
          AddLegacySpnDynLightProperty(result, legacy, "dynLightType", "LightType");
          AddLegacySpnDynLightProperty(result, legacy, "dynLightColor", "Color");
          AddLegacySpnDynLightProperty(result, legacy, "dynLightRampMap", "RampMap");
          AddLegacySpnDynLightProperty(result, legacy, "dynLightIlluminationMap", "IlluminationMap");
          AddLegacySpnDynLightProperty(result, legacy, "dynLightFalloff", "Falloff");
          AddLegacySpnDynLightProperty(result, legacy, "dynLightIntensity", "Intensity");
          AddLegacySpnDynLightProperty(result, legacy, "dynLightRange", "Range");
          break;
        }
      }

      foreach (string field in new[] {
        "dynObjectDataStringProperties", "dynObjectDataFloatProperties", "dynObjectDataBooleanProperties",
        "dynObjectDataIntegerProperties", "dynObjectDataVector3Properties"
      }) {
        Dictionary<object, object> map = row?.ValueOrDefault<Dictionary<object, object>>(field, null);
        if (map == null) continue;
        foreach (var pair in map) {
          if (!TryUnsignedGomId(pair.Key, out ulong hash) || !SpnDynLightPropertyNames.TryGetValue(hash, out string name)) continue;
          result[name] = pair.Value;
        }
      }
      return result;
    }

    private static void AddLegacySpnDynLightProperty(Dictionary<string, object> target, GomObjectData source, string field, string name) {
      if (source != null && source.Dictionary.TryGetValue(field, out object value) && value != null) target[name] = value;
    }

    private static bool SpnDynBool(object value, bool fallback) {
      if (value == null) return fallback;
      if (value is bool boolean) return boolean;
      if (value is ScriptEnum scriptEnum) return scriptEnum.Value + 1 != 0;
      string text = CleanGomString(value);
      if (String.Equals(text, "true", StringComparison.OrdinalIgnoreCase) || text == "1") return true;
      if (String.Equals(text, "false", StringComparison.OrdinalIgnoreCase) || text == "0") return false;
      try { return Convert.ToBoolean(value, System.Globalization.CultureInfo.InvariantCulture); } catch { return fallback; }
    }

    private static string SpnDynLightTypeName(object value) {
      long numeric = SpnDynInteger(value, Int64.MinValue);
      if (numeric == 1) return "DIRECTIONAL";
      if (numeric == 2) return "OMNI";
      if (numeric == 3) return "SPOT";
      string text = CleanGomString(value) ?? "OMNI";
      if (text.IndexOf("direction", StringComparison.OrdinalIgnoreCase) >= 0) return "DIRECTIONAL";
      if (text.IndexOf("spot", StringComparison.OrdinalIgnoreCase) >= 0) return "SPOT";
      return "OMNI";
    }

    private static bool TrySpnDynColor(object value, out Vector4 color) {
      color = new Vector4(1f, 1f, 1f, 1f);
      Vector3 vector = SpnDynVector3(value, new Vector3(Single.NaN, Single.NaN, Single.NaN));
      if (!Single.IsNaN(vector.X) && !Single.IsNaN(vector.Y) && !Single.IsNaN(vector.Z)) {
        color = new Vector4(vector, 1f);
        return true;
      }
      string text = CleanGomString(value);
      if (String.IsNullOrWhiteSpace(text)) return false;
      if (text.StartsWith("#", StringComparison.Ordinal)) text = text.Substring(1);
      string[] parts = text.Split(',');
      if (parts.Length < 3) return false;
      if (!Single.TryParse(parts[0].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float r) ||
          !Single.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float g) ||
          !Single.TryParse(parts[2].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float b)) return false;
      float a = 1f;
      if (parts.Length > 3) Single.TryParse(parts[3].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out a);
      color = new Vector4(r, g, b, a);
      return true;
    }


    private List<GR2> GetSpnPlaceableModels(string sourceFqn, Placeable placeable, GomObject node, Dictionary<string, List<GR2>> cache, Dictionary<string, WorldNpcAnimationClip> animationCache, out WorldNpcAnimationClip animation) {
      animation = null;
      if (cache.TryGetValue(sourceFqn, out List<GR2> cached)) {
        if (animationCache != null) animationCache.TryGetValue(sourceFqn, out animation);
        return cached;
      }
      var result = new List<GR2>();
      cache[sourceFqn] = result;
      var candidates = new List<string>();
      if (!String.IsNullOrWhiteSpace(placeable?.Model)) candidates.Add(placeable.Model);
      if (node != null) {
        string plcModel = node.Data.ValueOrDefault<string>("plcModel", null);
        string modelSpec = node.Data.ValueOrDefault<string>("plcModelAssetSpec", null);
        if (!String.IsNullOrWhiteSpace(plcModel)) candidates.Add(plcModel);
        if (!String.IsNullOrWhiteSpace(modelSpec)) candidates.Add(modelSpec);
      }
      foreach (string candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        CollectSpnModels(candidate, result, new HashSet<string>(StringComparer.OrdinalIgnoreCase), ref animation);
      if (animationCache != null) animationCache[sourceFqn] = animation;
      return result;
    }

    private void CollectSpnModels(string reference, List<GR2> output, HashSet<string> visited, ref WorldNpcAnimationClip animation) {
      if (String.IsNullOrWhiteSpace(reference) || output == null || !visited.Add(reference)) return;
      string referenceValue = reference.Trim();
      if (referenceValue.EndsWith(".mag", StringComparison.OrdinalIgnoreCase)) {
        GR2 magModel = LoadSpnMag(referenceValue, out WorldNpcAnimationClip magAnimation);
        if (magModel != null && !output.Contains(magModel)) output.Add(magModel);
        if (animation == null && magAnimation != null) animation = magAnimation;
        return;
      }
      if (referenceValue.EndsWith(".gr2", StringComparison.OrdinalIgnoreCase) || referenceValue.IndexOf("/art/", StringComparison.OrdinalIgnoreCase) >= 0 || referenceValue.IndexOf("\\art\\", StringComparison.OrdinalIgnoreCase) >= 0) {
        GR2 model = LoadSpnModel(referenceValue);
        if (model != null && !output.Contains(model)) output.Add(model);
        return;
      }
      string fqn = referenceValue.Replace('/', '.').Replace('\\', '.').Trim('.');
      GomObject dyn = null;
      try { dyn = currentDom.GetObject(fqn); } catch { }
      if (dyn == null) return;
      foreach (string field in new[] { "plcModel", "plcModelAssetSpec", "vehAppModel", "dynVisualFqn" }) {
        string direct = dyn.Data.ValueOrDefault<string>(field, null);
        if (!String.IsNullOrWhiteSpace(direct)) CollectSpnModels(direct, output, visited, ref animation);
      }
      var visuals = dyn.Data.ValueOrDefault<List<object>>("dynObjectDataList", null)
        ?? dyn.Data.ValueOrDefault<List<object>>("dynVisualList", null);
      if (visuals != null) foreach (object item in visuals) if (item is GomObjectData row) {
        string visual = row.ValueOrDefault<string>("dynVisualFqn", null) ?? row.ValueOrDefault<string>("dynVisualModel", null);
        if (!String.IsNullOrWhiteSpace(visual)) CollectSpnModels(visual, output, visited, ref animation);
      }
    }

    private GR2 LoadSpnMag(string magReference, out WorldNpcAnimationClip animation) {
      animation = null;
      try {
        string magPath = NormalizeResourceAssetPath(magReference, null);
        using File file = currentAssets.FindFile(magPath);
        if (file == null) return null;
        Dictionary<string, string> values;
        using (Stream stream = file.OpenCopyInMemory()) using (var reader = new StreamReader(stream)) values = ParseNpcSpec(reader.ReadToEnd());
        if (!values.TryGetValue("Mesh", out string mesh) || String.IsNullOrWhiteSpace(mesh)) return null;
        GR2 model = LoadSpnModel(mesh);
        if (model == null) return null;

        // MAG Material= is a positional submesh override. Keep the existing GR2 slots beyond the authored list.
        if (values.TryGetValue("Material", out string materialList) && !String.IsNullOrWhiteSpace(materialList)) {
          string[] overrides = materialList.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
          for (int i = 0; i < overrides.Length; i++) {
            string materialName = NormalizeNpcMaterialName(overrides[i]);
            if (String.IsNullOrWhiteSpace(materialName)) continue;
            if (!materials.TryGetValue(materialName, out GR2_Material material)) { material = new GR2_Material(materialName); materials[materialName] = material; }
            if (i < model.materials.Count) model.materials[i] = material; else model.materials.Add(material);
          }
        }
        animation = ResolveSpnMagAnimation(values);
        return model;
      } catch (Exception ex) {
        System.Diagnostics.Debug.WriteLine("SPN MAG failed " + magReference + ": " + ex.Message);
        return null;
      }
    }

    private WorldNpcAnimationClip ResolveSpnMagAnimation(Dictionary<string, string> values) {
      if (values == null || currentAssets == null) return null;
      if (!values.TryGetValue("AnimNetworkFolder", out string folder) || String.IsNullOrWhiteSpace(folder)) return null;
      string prefix = values.TryGetValue("AnimNetworkPrefix", out string authoredPrefix) && !String.IsNullOrWhiteSpace(authoredPrefix) ? authoredPrefix.Trim() : "mags";
      string relative = folder.Trim().Replace('\\', '/').Trim('/');
      string mphPath = NormalizeResourceAssetPath(relative + "/" + prefix + ".mph", null);
      using File mphFile = currentAssets.FindFile(mphPath);
      if (mphFile == null) return null;

      List<string> candidates;
      using (Stream mphStream = mphFile.OpenCopyInMemory()) using (var mphReader = new BinaryReader(mphStream))
        candidates = MPHAnimationReader.FindIdleClipNames(mphReader);
      if (candidates == null || candidates.Count == 0) return null;

      int slash = mphPath.LastIndexOf('/');
      string directory = slash >= 0 ? mphPath.Substring(0, slash + 1) : "/resources/";
      foreach (string clipName in candidates) {
        using File jbaFile = currentAssets.FindFile(directory + clipName + ".jba");
        if (jbaFile == null) continue;
        JBAAnimation jba;
        try {
          using Stream jbaStream = jbaFile.OpenCopyInMemory();
          using var br = new BinaryReader(jbaStream);
          jba = JBAReader.Read(br);
        } catch { continue; }
        if (jba == null || jba.BoneCount <= 0 || jba.Length <= 0f) continue;
        jba.PrepareSamples();

        JBARig rig = null;
        try {
          using Stream mphStream = mphFile.OpenCopyInMemory();
          using var mphReader = new BinaryReader(mphStream);
          rig = MPHAnimationReader.FindRigForClip(mphReader, clipName);
        } catch { }

        IList<GR2_Bone_Skeleton> skeleton = null;
        if (values.TryGetValue("Model", out string modelSpec) && !String.IsNullOrWhiteSpace(modelSpec)) {
          string stem = Path.GetFileNameWithoutExtension(modelSpec.Trim().Replace('\\', '/')).ToLowerInvariant();
          skeleton = LoadNpcAnimationSkeleton("/resources/art/dynamic/spec/" + stem + "_skeleton.gr2");
        }
        return new WorldNpcAnimationClip { DisplayName = clipName, Action = clipName, Network = prefix, Animation = jba, Rig = rig, Skeleton = skeleton };
      }
      return null;
    }

    private static string NormalizeResourceAssetPath(string assetReference, string extension) {
      string path = (assetReference ?? String.Empty).Trim().Replace('\\', '/');
      while (path.Contains("//")) path = path.Replace("//", "/");
      if (!path.StartsWith("/", StringComparison.Ordinal)) path = "/" + path;
      if (!path.StartsWith("/resources/", StringComparison.OrdinalIgnoreCase)) path = "/resources" + path;
      if (!String.IsNullOrWhiteSpace(extension) && !path.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) path += extension;
      return path.ToLowerInvariant();
    }

    private GR2 LoadSpnModel(string modelReference) {
      try {
        string path = NormalizeResourceAssetPath(modelReference, ".gr2");
        using File file = currentAssets.FindFile(path);
        if (file == null) return null;
        using Stream stream = file.OpenCopyInMemory();
        using var br = new BinaryReader(stream);
        return new GR2(br, path.Split('/').Last(), materials);
      } catch (Exception ex) {
        System.Diagnostics.Debug.WriteLine("SPN placeable model failed " + modelReference + ": " + ex.Message);
        return null;
      }
    }

    private string[] ResolveVisualItemNames(NpcVisualData visual) {
      if (visual == null) return Array.Empty<string>();
      var names = new List<string>();
      foreach (ulong id in new[] { visual.MeleeWepId, visual.MeleeOffWepId, visual.RangedWepId, visual.RangedOffWepId }) AddNpcItemName(id, names);
      return names.Distinct(StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    private string[] ResolveVisualItemNames(GomObjectData visual) {
      if (visual == null) return Array.Empty<string>();
      var names = new List<string>();
      AddNpcItemName(visual.ValueOrDefault<ulong>("npcTemplateVisualDataMeleeWeapon", 0), names);
      AddNpcItemName(visual.ValueOrDefault<ulong>("npcTemplateVisualDataMeleeOffWeapon", 0), names);
      AddNpcItemName(visual.ValueOrDefault<ulong>("npcTemplateVisualDataRangedWeapon", 0), names);
      AddNpcItemName(visual.ValueOrDefault<ulong>("npcTemplateVisualDataRangedOffWeapon", 0), names);
      return names.Distinct(StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    private void AddNpcItemName(ulong id, List<string> names) {
      if (id == 0 || names == null) return;
      try {
        Item item = currentDom.ItemLoader.Load(id);
        if (item != null && !String.IsNullOrWhiteSpace(item.Name)) names.Add(item.Name);
      } catch { }
    }

    private List<GR2> GetNpcAppearanceModels(NpcAppearance appearance, Dictionary<string, List<GR2>> cache) {
      if (appearance == null) return new List<GR2>();
      string cacheKey = !String.IsNullOrWhiteSpace(appearance.Fqn) ? appearance.Fqn : appearance.Id.ToString();
      if (cache.TryGetValue(cacheKey, out List<GR2> cached)) return cached;
      var result = new List<GR2>();
      cache[cacheKey] = result;
      if (appearance.AppearanceSlotMap == null) return result;

      foreach (var pair in appearance.AppearanceSlotMap) {
        if (pair.Value == null || pair.Value.Count == 0) continue;
        // Jedipedia resolves one deterministic variant for a static preview. Do not drop the entire slot merely
        // because an NPP offers multiple weighted alternatives (hair/face parts commonly do).
        AppSlot slot = pair.Value.FirstOrDefault(x => x != null);
        if (slot == null) continue;
        string bodyType = String.IsNullOrWhiteSpace(slot.BodyType) ? appearance.BodyType : slot.BodyType;
        string modelPath = (slot.Model ?? String.Empty).Replace("[bt]", bodyType ?? String.Empty).Replace("[BT]", bodyType ?? String.Empty);
        if (pair.Key.IndexOf("FaceHair", StringComparison.OrdinalIgnoreCase) >= 0 && String.IsNullOrWhiteSpace(modelPath)) modelPath = "/art/defaultassets/blank.gr2";
        if (String.IsNullOrWhiteSpace(modelPath) || !modelPath.EndsWith(".gr2", StringComparison.OrdinalIgnoreCase)) continue;
        GR2 model = LoadNpcPartModel(modelPath, slot, appearance, pair.Key, bodyType);
        if (model != null) {
          result.Add(model);
          // Attached appearance GR2s (hair, facial pieces, armour accessories, etc.) are real skinned NPP parts.
          // Drawing them recursively through DrawModel() leaves them in bind pose while the parent body animates,
          // which is the detached/floating-parts artifact seen on some NPCs. Flatten them into the placement so every
          // attachment goes through the same JBA skinning path as the owning appearance part.
          if (model.attachedModels != null && model.attachedModels.Count > 0) {
            foreach (GR2 attached in model.attachedModels.Where(x => x != null).ToArray()) result.Add(attached);
            model.attachedModels.Clear();
          }
        }
      }
      return result;
    }

    private GR2 LoadNpcPartModel(string modelPath, AppSlot slot, NpcAppearance appearance, string slotName, string bodyType) {
      try {
        using File file = currentAssets.FindFile("/resources" + modelPath.Replace('\\', '/'));
        if (file == null) return null;
        using Stream stream = file.OpenCopyInMemory();
        using var br = new BinaryReader(stream);
        GR2 model = new GR2(br, modelPath.Split('/', '\\').Last());

        string material0 = (slot.Material0 ?? String.Empty).Replace("[bt]", bodyType ?? String.Empty).Replace("[BT]", bodyType ?? String.Empty);
        string materialMirror = (slot.MaterialMirror ?? String.Empty).Replace("[bt]", bodyType ?? String.Empty).Replace("[BT]", bodyType ?? String.Empty);
        if (appearance.AppearanceSlotMap.TryGetValue("appSlotHead", out List<AppSlot> heads) && heads?.Count > 0 && heads[0]?.AMI?.ChildSkinMaterials != null) {
          var skin = heads[0].AMI.ChildSkinMaterials;
          if (material0.IndexOf("_naked_", StringComparison.OrdinalIgnoreCase) >= 0 && skin.TryGetValue(slotName, out string skin0)) material0 = skin0.Replace("[bt]", bodyType ?? String.Empty);
          if (model.numMaterials > 1 && String.IsNullOrWhiteSpace(materialMirror) && skin.TryGetValue(slotName, out string skin1)) materialMirror = skin1.Replace("[bt]", bodyType ?? String.Empty);
        }
        material0 = NormalizeNpcMaterialName(ResolveNpcGenderMaterial(material0, bodyType));
        materialMirror = NormalizeNpcMaterialName(ResolveNpcGenderMaterial(materialMirror, bodyType));
        string palette1 = PalettePath(slot.PrimaryHue);
        string palette2 = PalettePath(slot.SecondaryHue);
        model.materials = new List<GR2_Material>();
        if (!String.IsNullOrWhiteSpace(material0)) model.materials.Add(RegisterNpcMaterial(material0, palette1, palette2, appearance, slotName, 0));
        if (!String.IsNullOrWhiteSpace(materialMirror)) model.materials.Add(RegisterNpcMaterial(materialMirror, palette1, palette2, appearance, slotName, 1));

        if (slot.AttachedModels != null) {
          foreach (string attachment in slot.AttachedModels.Where(x => !String.IsNullOrWhiteSpace(x))) {
            string attachPath = attachment.Replace("[bt]", bodyType ?? String.Empty).Replace("[BT]", bodyType ?? String.Empty);
            using File attachFile = currentAssets.FindFile("/resources" + attachPath.Replace('\\', '/'));
            if (attachFile == null) continue;
            using Stream attachStream = attachFile.OpenCopyInMemory();
            using var abr = new BinaryReader(attachStream);
            GR2 attached = new GR2(abr, attachPath.Split('/', '\\').Last());
            attached.materials = model.materials;
            attached.transformMatrix = Matrix.Scaling(new Vector3(1f, 1f, 1f));
            model.attachedModels.Add(attached);
          }
        }
        model.transformMatrix = Matrix.Scaling(new Vector3(1f, 1f, 1f));
        return model;
      } catch (Exception ex) {
        System.Diagnostics.Debug.WriteLine("NPC appearance part failed " + modelPath + ": " + ex.Message);
        return null;
      }
    }

    private GR2_Material RegisterNpcMaterial(string baseMaterial, string palette1, string palette2, NpcAppearance appearance, string slot, int index) {
      string authored = (baseMaterial ?? String.Empty).Trim();
      string variantKey = authored + "|npc|" + (appearance?.Fqn ?? appearance?.Id.ToString() ?? "unknown") + "|" + slot + "|" + index + "|" + palette1 + "|" + palette2;
      if (materials.TryGetValue(variantKey, out GR2_Material existing)) return existing;
      var material = new GR2_Material(authored) { materialName = variantKey, sourceMaterialName = authored, palette1XML = palette1, palette2XML = palette2 };
      materials[variantKey] = material;
      return material;
    }

    private static string NormalizeNpcMaterialName(string material) {
      if (String.IsNullOrWhiteSpace(material)) return String.Empty;
      string materialName = material.Trim().Replace('\\', '/');
      int variant = materialName.IndexOf('#');
      if (variant >= 0) materialName = materialName.Substring(0, variant);
      int slash = materialName.LastIndexOf('/');
      if (slash >= 0 && slash + 1 < materialName.Length) materialName = materialName.Substring(slash + 1);
      if (materialName.EndsWith(".mat", StringComparison.OrdinalIgnoreCase)) materialName = materialName.Substring(0, materialName.Length - 4);
      return materialName.Trim();
    }

    private static string ResolveNpcGenderMaterial(string material, string bodyType) {
      if (String.IsNullOrWhiteSpace(material)) return String.Empty;
      if ((bodyType ?? String.Empty).IndexOf("bf", StringComparison.OrdinalIgnoreCase) >= 0) return material.Replace("[gen]", "f");
      if ((bodyType ?? String.Empty).IndexOf("bm", StringComparison.OrdinalIgnoreCase) >= 0) return material.Replace("[gen]", "m");
      return material.Replace("[gen]", "u");
    }

    private static string PalettePath(string hue) {
      if (String.IsNullOrWhiteSpace(hue)) return null;
      string path = hue.Split(';').FirstOrDefault();
      return String.IsNullOrWhiteSpace(path) ? null : "/resources" + path.Replace('\\', '/');
    }
  }
}
