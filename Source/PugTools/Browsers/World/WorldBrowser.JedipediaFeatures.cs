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
      var spawnerCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
      var spnModelCache = new Dictionary<string, List<GR2>>(StringComparer.OrdinalIgnoreCase);
      var spnAnimationCache = new Dictionary<string, WorldNpcAnimationClip>(StringComparer.OrdinalIgnoreCase);
      var animationCache = new Dictionary<string, WorldNpcAnimationClip>(StringComparer.OrdinalIgnoreCase);
      var firstSpawnPointByParent = BuildFirstSpawnPointIndex();
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
            if (!spawnerCache.TryGetValue(spnFqn, out string entityFqn)) {
              entityFqn = ResolveSpawnerEntityFqn(spnFqn);
              spawnerCache[spnFqn] = entityFqn ?? String.Empty;
            }
            if (String.IsNullOrWhiteSpace(entityFqn)) continue;
            Room renderRoom = room;
            AssetInstance renderInstance = instance;
            if (firstSpawnPointByParent.TryGetValue(instance.ID, out var spawnPoint)) { renderRoom = spawnPoint.Room; renderInstance = spawnPoint.Instance; }

            if (entityFqn.StartsWith("npc.", StringComparison.OrdinalIgnoreCase)) {
              if (!npcCache.TryGetValue(entityFqn, out Npc npc)) {
                npc = currentDom.NpcLoader.Load(entityFqn);
                npcCache[entityFqn] = npc;
              }
              string idleAnimation = SpawnerIdleAnimationName(instance);
              WorldNpcPlacement spnPlacement = BuildNpcPlacement(renderRoom, renderInstance, entityFqn, npc, appearanceCache, speciesScales, idleAnimation, animationCache);
              if (spnPlacement != null) worldNpcPlacements.Add(spnPlacement);
            } else if (entityFqn.StartsWith("plc.", StringComparison.OrdinalIgnoreCase)) {
              WorldSpnPlacement spnObject = BuildSpnPlaceablePlacement(renderRoom, renderInstance, entityFqn, spnModelCache, spnAnimationCache);
              if (spnObject != null) worldSpnPlacements.Add(spnObject);
            }
          } catch (Exception ex) {
            failures++;
            System.Diagnostics.Debug.WriteLine("World NPC placement failed for " + asset.Path + "." + asset.Extension + ": " + ex.Message);
          }
        }
      }
      System.Diagnostics.Debug.WriteLine("World spawn preview: " + worldNpcPlacements.Count + " NPCs, " + worldSpnPlacements.Count + " placeables, " + failures + " failures");
    }

    private Dictionary<ulong, (Room Room, AssetInstance Instance)> BuildFirstSpawnPointIndex() {
      var result = new Dictionary<ulong, (Room Room, AssetInstance Instance)>();
      if (area == null) return result;
      foreach (Room room in area.RoomList) {
        foreach (AssetInstance instance in room.InstancesById.Values) {
          if (instance == null || instance.parentInstance == 0 || !area.AssetIdMap.TryGetValue(instance.assetID, out AreaAsset asset) || asset == null) continue;
          if (!String.Equals((asset.Extension ?? String.Empty).Trim(), "spn_pt", StringComparison.OrdinalIgnoreCase)) continue;
          if (!result.ContainsKey(instance.parentInstance)) result[instance.parentInstance] = (room, instance);
        }
      }
      return result;
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

    private string ResolveSpawnerEntityFqn(string spnFqn) {
      GomObject spawner = currentDom.GetObject(spnFqn);
      if (spawner == null) return null;
      var list = spawner.Data.ValueOrDefault<List<object>>("spnEntityList", null);
      if (list == null) return null;
      foreach (object entry in list) {
        if (entry is not GomObjectData row) continue;
        object entityReference = row.ValueOrDefault<object>("spnEntityFqn", null) ?? row.ValueOrDefault<object>("spnEntityId", null);
        if (entityReference == null) continue;
        if (entityReference is string text && !String.IsNullOrWhiteSpace(text)) return text.Trim();
        try {
          if (!TryUnsignedGomId(entityReference, out ulong id)) continue;
          GomObject entity = currentDom.GetObject(id);
          if (entity != null) return entity.Name;
        } catch { }
      }
      return null;
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
        Title = title, Scale = scale, Items = ResolveVisualItemNames(visual), ShowNameplate = true,
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

    private WorldSpnPlacement BuildSpnPlaceablePlacement(Room room, AssetInstance instance, string sourceFqn, Dictionary<string, List<GR2>> modelCache, Dictionary<string, WorldNpcAnimationClip> animationCache) {
      Placeable placeable = null;
      GomObject node = null;
      try { placeable = currentDom.PlaceableLoader.Load(sourceFqn); } catch { }
      try { node = currentDom.GetObject(sourceFqn); } catch { }
      List<GR2> loaded = GetSpnPlaceableModels(sourceFqn, placeable, node, modelCache, animationCache, out WorldNpcAnimationClip animation);
      if (loaded == null || loaded.Count == 0) return null;
      string name = placeable?.Name;
      if (placeable?.LocalizedName != null && placeable.LocalizedName.TryGetValue(GomLib.StringTable.SelectedLocalization, out string localized) && IsRealLocalizedName(localized, sourceFqn)) name = localized;
      if (!IsRealLocalizedName(name, sourceFqn)) name = PrettySpawnName(sourceFqn);
      var placement = new WorldSpnPlacement {
        Room = room, Instance = instance, SourceFqn = sourceFqn, Name = name, Scale = 1f, Animation = animation,
        AnimationPhase = StableAnimationPhase(instance?.ID ?? 0, sourceFqn)
      };
      foreach (GR2 model in loaded) placement.Models.Add(model);
      return placement;
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
      var visuals = dyn.Data.ValueOrDefault<List<object>>("dynVisualList", null);
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
        if (model != null) result.Add(model);
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
