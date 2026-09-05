using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using GomLib;
using FileFormats;
using SlimDX;

namespace PugTools {
  public partial class WorldBrowser {
    private const String SpaceEncounterAction = "Space Combat Encounter";
    private static readonly String[] SpaceEncounterHooks = { "On Arrive", "On Enter", "On Owner Arrive" };
    private String spaceEncounterTimelineSignature = String.Empty;
    private readonly Dictionary<String, WorldSpaceCombatEncounterSpec[]> spaceEncounterSpecCache = new Dictionary<String, WorldSpaceCombatEncounterSpec[]>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<String, GR2> spaceEncounterModelCache = new Dictionary<String, GR2>(StringComparer.OrdinalIgnoreCase);

    private void ResetWorldSpaceCombatEncounterCache() {
      spaceEncounterTimelineSignature = String.Empty;
      spaceEncounterSpecCache.Clear();
      spaceEncounterModelCache.Clear();
    }

    // Resolve the Space Combat fleet while the area is still on the UI/loading thread. The renderer intentionally
    // never opens TOR files or mutates the shared material dictionary after its D3D thread has started. All HYDRA
    // trigger FQNs are authored directly on .trg placements or on the player rail's WaypointTemplate rows, so the
    // complete set can be collected before View_AREA.LoadModel().
    private void PreloadWorldSpaceCombatEncounterAssets() {
      if (area == null || currentDom == null || currentAssets == null || models == null) return;
      List<AreaPath> rails = SpaceFlypathCandidates();
      if (rails.Count == 0) return;

      var triggerFqns = new HashSet<String>(StringComparer.OrdinalIgnoreCase);
      foreach (FileFormats.Room room in area.RoomList) {
        if (room?.InstancesById == null) continue;
        foreach (FileFormats.AssetInstance instance in room.InstancesById.Values) {
          if (instance == null || !area.AssetIdMap.TryGetValue(instance.assetID, out AreaAsset asset) || asset == null) continue;
          if (!String.Equals((asset.Extension ?? String.Empty).Trim().TrimStart('.'), "trg", StringComparison.OrdinalIgnoreCase)) continue;
          String candidate = !String.IsNullOrWhiteSpace(instance.TriggerParam) ? instance.TriggerParam : instance.TriggerTag;
          if (!String.IsNullOrWhiteSpace(candidate) &&
              (String.Equals(instance.TriggerClassType, "HYDRA", StringComparison.OrdinalIgnoreCase) || candidate.Trim().StartsWith("hyd.", StringComparison.OrdinalIgnoreCase)))
            triggerFqns.Add(candidate.Trim());
        }
      }
      foreach (AreaPath rail in rails) foreach (String fqn in ResolveSpaceFlypathWaypointTemplates(rail).Values)
        if (!String.IsNullOrWhiteSpace(fqn)) triggerFqns.Add(fqn.Trim());
      if (triggerFqns.Count == 0) return;

      var specs = new List<WorldSpaceCombatEncounterSpec>();
      foreach (AreaPath rail in rails) specs.AddRange(ResolveSpaceCombatEncounterSpecs(rail, triggerFqns, true));
      RegisterSpaceCombatEncounterModels(specs);
      System.Diagnostics.Debug.WriteLine("Space Combat preloaded: " + specs.Count.ToString(CultureInfo.InvariantCulture) +
        " encounter rows, " + specs.Where(x => x?.Model != null).Select(x => x.Model).Distinct().Count().ToString(CultureInfo.InvariantCulture) + " ship models.");
    }

    private void RegisterSpaceCombatEncounterModels(IEnumerable<WorldSpaceCombatEncounterSpec> specs) {
      if (specs == null || models == null) return;
      var already = new HashSet<GR2>(models.Values.Where(x => x != null));
      UInt64 synthetic = UInt64.MaxValue;
      foreach (GR2 model in specs.Where(x => x?.Model != null).Select(x => x.Model).Distinct()) {
        if (!already.Add(model)) continue;
        while (models.ContainsKey(synthetic) && synthetic > 0) synthetic--;
        if (synthetic == 0 && models.ContainsKey(0)) break;
        models[synthetic] = model;
        if (synthetic > 0) synthetic--;
      }
    }

    private Dictionary<Int32, String> ResolveSpaceFlypathWaypointTemplates(AreaPath path) {
      var result = new Dictionary<Int32, String>();
      if (path == null || String.IsNullOrWhiteSpace(path.Fqn) || currentDom == null) return result;
      GomObject node = null;
      try { node = WorldResolveGomObject(path.Fqn); } catch { }
      if (node?.Data == null) return result;
      Object raw = SpaceDataValue(node.Data, "pthPointList", "4611686030387598203");
      foreach (Object entryRaw in SpaceSequence(raw)) {
        GomObjectData entry = SpaceObjectData(entryRaw);
        if (entry == null) continue;
        Object idRaw = SpaceDataValue(entry, "pthPointId", "4611686030387598855");
        if (!TrySpaceInt32(idRaw, out Int32 templateId) || templateId < 0) continue;
        String fqn = SpaceText(SpaceDataValue(entry, "pthPointHydraFqn", "4611686030672275675"));
        if (String.IsNullOrWhiteSpace(fqn)) {
          Object reference = SpaceDataValue(entry, "pthPointHydra", "4611686062713031201");
          fqn = ResolveSpaceReferenceName(reference);
        }
        if (!String.IsNullOrWhiteSpace(fqn)) result[templateId] = fqn.Trim();
      }
      return result;
    }

    private void UpdateSpaceCombatEncounterTimeline() {
      if (panelRender == null || !panelRender.IsSpaceFlypathActive) {
        spaceEncounterTimelineSignature = String.Empty;
        return;
      }
      View_AREA.SpaceFlypathTickInfo[] authored = panelRender.SpaceFlypathTicks
        .Where(x => x != null && (String.Equals(x.Kind, "HYDRA", StringComparison.OrdinalIgnoreCase) || String.Equals(x.Kind, "WAYPOINT", StringComparison.OrdinalIgnoreCase)))
        .ToArray();
      if (authored.Length == 0) return;
      String route = panelRender.CurrentSpaceFlypathLabel ?? String.Empty;
      String signature = (panelRender.CurrentSpaceFlypathFqn ?? String.Empty) + "|" +
        (panelRender.CurrentSpaceFlypathName ?? String.Empty) + "|" + route + "|" +
        String.Join("|", authored.Select(x => (x.Kind ?? String.Empty) + ":" + (x.Fqn ?? String.Empty) + ":" + x.Progress.ToString("0.000000", CultureInfo.InvariantCulture)));
      if (String.Equals(spaceEncounterTimelineSignature, signature, StringComparison.Ordinal)) return;

      String activeFqn = panelRender.CurrentSpaceFlypathFqn;
      String activeName = panelRender.CurrentSpaceFlypathName;
      AreaPath selectedPath = SpaceFlypathCandidates().FirstOrDefault(x =>
        !String.IsNullOrWhiteSpace(activeFqn) && String.Equals(x.Fqn, activeFqn, StringComparison.OrdinalIgnoreCase) &&
        (String.IsNullOrWhiteSpace(activeName) || String.Equals(x.Name, activeName, StringComparison.OrdinalIgnoreCase)))
        ?? SpaceFlypathCandidates().FirstOrDefault(x => String.Equals(SpaceFlypathLabel(x), route, StringComparison.Ordinal));
      if (selectedPath == null) return;
      try {
        HashSet<String> fqns = new HashSet<String>(authored.Select(x => (x.Fqn ?? String.Empty).Trim()).Where(x => x.Length > 0), StringComparer.OrdinalIgnoreCase);
        WorldSpaceCombatEncounterSpec[] specs = ResolveSpaceCombatEncounterSpecs(selectedPath, fqns, false).ToArray();
        panelRender.SetSpaceCombatEncounterSpecs(specs);
        spaceEncounterTimelineSignature = signature;
        if (specs.Length > 0)
          SetStatusLabel("Space Combat encounter timeline: " + specs.Length.ToString(CultureInfo.InvariantCulture) + " authored encounter entries resolved.");
      } catch (Exception ex) {
        spaceEncounterTimelineSignature = String.Empty;
        System.Diagnostics.Debug.WriteLine("Space Combat encounter timeline resolution failed: " + ex.Message);
      }
    }

    private IEnumerable<WorldSpaceCombatEncounterSpec> ResolveSpaceCombatEncounterSpecs(AreaPath playerPath, IEnumerable<String> triggerFqns, Boolean allowModelLoad) {
      if (currentDom == null || playerPath == null || triggerFqns == null) yield break;
      Boolean imperial = SpaceFlypathIsImperial(playerPath);
      String faction = imperial ? "empire" : "republic";

      System.Collections.IDictionary encounters = SpacePrototypeTable("scEncountersProtoData", "scEncountersData", "4611686067808231195", "encounter");
      System.Collections.IDictionary shipTypes = SpacePrototypeTable("scShipTypesProtoData", "scShipTypes", "4611686067858431191", "shiptype");
      System.Collections.IDictionary formationsOffsets = SpacePrototypeTable("scFormationsProtoData", "scFormationOffsets", "4611686067808231191", "formation");
      System.Collections.IDictionary formationsBones = SpacePrototypeTable("scFormationsProtoData", "scFormationBones", "4611686067808231192", "formation");
      System.Collections.IDictionary ships = SpacePrototypeTable("scShipProtoData", "scShipData", "4611686067808231202", "ship");
      System.Collections.IDictionary vehicles = SpacePrototypeTable("spnVehicleProtoData", "spnVehicleData", "4611686067808231196", "vehicle");
      System.Collections.IDictionary appearances = SpacePrototypeTable("vehAppearanceProtoData", "vehAppearanceData", "4611686062111631212", "appearance");
      if (encounters == null) yield break;

      foreach (String triggerFqn in triggerFqns.Distinct(StringComparer.OrdinalIgnoreCase)) {
        String normalizedTrigger = (triggerFqn ?? String.Empty).Trim();
        if (normalizedTrigger.Length == 0) continue;
        String cacheKey = faction + "|" + normalizedTrigger;
        if (spaceEncounterSpecCache.TryGetValue(cacheKey, out WorldSpaceCombatEncounterSpec[] cached)) {
          foreach (WorldSpaceCombatEncounterSpec spec in cached) yield return spec;
          continue;
        }

        var resolved = new List<WorldSpaceCombatEncounterSpec>();
        GomObject hyd = null;
        try { hyd = WorldResolveGomObject(normalizedTrigger); } catch { }
        if (hyd?.Data != null) foreach (List<String> tokens in SpaceHydraEncounterLists(hyd.Data, faction)) {
          foreach (String token in tokens) {
            if (String.Equals(token, "sce.", StringComparison.OrdinalIgnoreCase)) continue;
            UInt64 encounterId = SpaceEncounterNodeId(token);
            Object rowRaw = FindTaxiTableValue(encounters, encounterId);
            GomObjectData row = SpaceObjectData(rowRaw);
            if (row == null) continue;

            Object shipTypeRaw = SpaceDataValue(row, "scShipType", "4611686067808231193");
            Object formationRaw = SpaceDataValue(row, "scShipFormation", "4611686067808231194");
            String pathName = SpaceText(SpaceDataValue(row, "scPathName", "4611686198471740000"));
            Single encounterSpeed = SpaceFloat(SpaceDataValue(row, "scSpeedScale", "4611686051888871069"), 1f);
            Single spawnDelay = SpaceFloat(SpaceDataValue(row, "scSpawnDelay", "4611686051888871068"), 0f);
            if (Single.IsNaN(spawnDelay) || Single.IsInfinity(spawnDelay)) spawnDelay = 0f;
            Boolean boss = SpaceBool(SpaceDataValue(row, "scIsBoss", "4611686051888871073"));
            Boolean hasAnchor = TrySpaceVector3(SpaceDataValue(row, "scPathAnchorOffset", "4611686044019870043"), out Vector3 anchor);
            String anchorOffset = hasAnchor
              ? String.Join(", ", new[] { anchor.X, anchor.Y, anchor.Z }.Select(x => x.ToString("0.###", CultureInfo.InvariantCulture)))
              : null;

            Object vehicleRef = shipTypeRaw;
            Object shipTypeRow = FindTaxiTableValue(shipTypes, shipTypeRaw);
            List<Object> shipTypeValues = SpaceSequence(shipTypeRow).ToList();
            if (shipTypeValues.Count > 0) {
              Int32 column = imperial ? 1 : 0; // Normal difficulty, same default as Jedipedia's rail preview.
              vehicleRef = column < shipTypeValues.Count ? shipTypeValues[column] : shipTypeValues[0];
            }
            String vehicleSpec = ResolveSpaceReferenceName(vehicleRef) ?? SpaceText(vehicleRef);
            Object shipRowRaw = FindTaxiTableValue(ships, vehicleRef);
            GomObjectData shipRow = SpaceObjectData(shipRowRaw);
            Single shipSpeed = SpaceFloat(SpaceDataValue(shipRow, "scSpeedScale", "4611686051888871069"), 1f);
            String engineFx = SpaceText(SpaceDataValue(shipRow, "scEngineFxName", "4611686051888871026"));

            String modelPath = null;
            GR2 model = null;
            Single modelScale = 1f;
            Object vehicleRowRaw = FindTaxiTableValue(vehicles, vehicleRef);
            GomObjectData vehicleRow = SpaceObjectData(vehicleRowRaw);
            Object appearanceRef = SpaceDataValue(vehicleRow, "vehAppearancePackage", "4611686051888871104");
            GomObjectData appearanceRow = SpaceObjectData(FindTaxiTableValue(appearances, appearanceRef));
            if (appearanceRow != null) {
              modelPath = SpaceText(SpaceDataValue(appearanceRow, "vehAppModel", "4611686061988131201"));
              modelScale = SpaceFloat(SpaceDataValue(appearanceRow, "vehAppScale", "4611686062111631191"), 1f);
              if (!(modelScale > 0f) || Single.IsNaN(modelScale) || Single.IsInfinity(modelScale)) modelScale = 1f;
            }
            if (!String.IsNullOrWhiteSpace(modelPath)) {
              String modelKey = modelPath.Trim().Replace('\\', '/').ToLowerInvariant();
              if (!spaceEncounterModelCache.TryGetValue(modelKey, out model) && allowModelLoad) {
                model = ResolveTaxiModelReference(modelPath, out String resolvedModelPath);
                if (model != null) {
                  spaceEncounterModelCache[modelKey] = model;
                  if (!String.IsNullOrWhiteSpace(resolvedModelPath)) modelPath = resolvedModelPath;
                }
              }
            }

            Int32 shipCount = 1;
            Int32 boltedCount = 0;
            String formationKind = "single";
            var formationOffsets = new List<Vector3>();
            Object offsets = FindTaxiTableValue(formationsOffsets, formationRaw);
            foreach (Object offsetRaw in SpaceSequence(offsets))
              if (TrySpaceVector3(offsetRaw, out Vector3 formationOffset)) formationOffsets.Add(formationOffset);
            if (formationOffsets.Count > 0) { shipCount = formationOffsets.Count; formationKind = "offsets"; }
            else {
              Object bones = FindTaxiTableValue(formationsBones, formationRaw);
              Int32 boneCount = SpaceCollectionCount(bones);
              if (boneCount > 0) { boltedCount = boneCount; formationKind = "bones"; }
            }
            if (String.IsNullOrWhiteSpace(pathName)) {
              // These are turrets/engines/etc. bolted to the last path-bearing leader in the flat token list.
              boltedCount = Math.Max(boltedCount, Math.Max(1, shipCount));
              shipCount = 0;
              formationOffsets.Clear();
            }

            Single totalSpeed = encounterSpeed * shipSpeed;
            if (!(totalSpeed > 0f) || Single.IsNaN(totalSpeed) || Single.IsInfinity(totalSpeed)) totalSpeed = 1f;
            var spec = new WorldSpaceCombatEncounterSpec {
              TriggerFqn = normalizedTrigger,
              EncounterName = token,
              PathName = pathName,
              VehicleSpec = vehicleSpec,
              ModelPath = modelPath,
              Model = model,
              EngineFx = engineFx,
              AnchorOffset = anchorOffset,
              AnchorOffsetVector = anchor,
              HasAnchorOffset = hasAnchor,
              FormationKind = formationKind,
              ModelScale = modelScale,
              SpawnDelay = spawnDelay,
              SpeedScale = totalSpeed,
              ShipCount = shipCount,
              BoltedCount = boltedCount,
              Boss = boss
            };
            spec.FormationOffsets.AddRange(formationOffsets);
            resolved.Add(spec);
          }
        }
        WorldSpaceCombatEncounterSpec[] resolvedArray = resolved.ToArray();
        spaceEncounterSpecCache[cacheKey] = resolvedArray;
        foreach (WorldSpaceCombatEncounterSpec spec in resolvedArray) yield return spec;
      }
    }

    private System.Collections.IDictionary SpacePrototypeTable(String preferredName, String fieldName, String numericField, String hint) {
      GomObject node = null;
      try { node = WorldResolveGomObject(preferredName); } catch { }
      Object table = SpaceDataValue(node?.Data, fieldName, numericField);
      System.Collections.IDictionary dictionary = SpaceMapTable(table);
      if (dictionary != null) return dictionary;
      try {
        node = FindTaxiPrototypeWithDataField(fieldName, numericField, hint, preferredName);
        return SpaceMapTable(SpaceDataValue(node?.Data, fieldName, numericField));
      } catch { return null; }
    }

    private IEnumerable<List<String>> SpaceHydraEncounterLists(GomObjectData hydra, String faction) {
      Object mapRaw = SpaceDataValue(hydra, "hydScriptMap", "4611686026539819747");
      System.Collections.IDictionary map = SpaceMapTable(mapRaw);
      if (map == null) yield break;
      foreach (KeyValuePair<Object, Object> hookEntry in SpaceDictionaryEntries(map)) {
        String hook = SpaceText(hookEntry.Key);
        if (!SpaceEncounterHooks.Any(x => String.Equals(x, hook, StringComparison.OrdinalIgnoreCase))) continue;
        GomObjectData script = SpaceObjectData(hookEntry.Value);
        foreach (Object blockRaw in SpaceSequence(SpaceDataValue(script, "hydScriptBlocks", "4611686026567367447"))) {
          GomObjectData block = SpaceObjectData(blockRaw);
          if (block == null || SpaceHydraWrongFaction(block, faction)) continue;
          foreach (Object actionBlockRaw in SpaceSequence(SpaceDataValue(block, "hydActionBlocks", "4611686026567369455"))) {
            GomObjectData actionBlock = SpaceObjectData(actionBlockRaw);
            foreach (Object actionRaw in SpaceSequence(SpaceDataValue(actionBlock, "hydActions", "4611686026567774554"))) {
              GomObjectData action = SpaceObjectData(actionRaw);
              if (action == null || !String.Equals(SpaceText(SpaceDataValue(action, "hydAction", "4611686026567774112")), SpaceEncounterAction, StringComparison.Ordinal)) continue;
              String value = SpaceText(SpaceDataValue(action, "hydValue", "4611686026567774127"));
              List<String> tokens = Regex.Split(value ?? String.Empty, @"\s+").Where(x => x.StartsWith("sce.", StringComparison.OrdinalIgnoreCase)).ToList();
              if (tokens.Count > 0) yield return tokens;
            }
          }
        }
      }
    }

    private Boolean SpaceHydraWrongFaction(GomObjectData block, String faction) {
      GomObjectData conditionBlock = SpaceObjectData(SpaceDataValue(block, "hydConditionBlock", "4611686026567369621"));
      foreach (Object conditionRaw in SpaceSequence(SpaceDataValue(conditionBlock, "hydConditions", "4611686029670182931"))) {
        GomObjectData condition = SpaceObjectData(conditionRaw);
        if (condition == null) continue;
        if (!String.Equals(SpaceText(SpaceDataValue(condition, "hydAction", "4611686026567774112")), "Is Faction", StringComparison.OrdinalIgnoreCase)) continue;
        String value = SpaceText(SpaceDataValue(condition, "hydValue", "4611686026567774127"));
        if (!String.Equals(value, faction, StringComparison.OrdinalIgnoreCase)) return true;
      }
      return false;
    }

    private GomObjectData SpaceObjectData(Object raw) {
      if (raw == null) return null;
      if (raw is GomObjectData data) return data;
      if (raw is GomObject node) { try { return node.Data; } catch { return null; } }
      if (raw is IDictionary dictionary) {
        var copy = new GomObjectData();
        foreach (KeyValuePair<Object, Object> entry in SpaceDictionaryEntries(dictionary)) {
          String key = SpaceText(entry.Key);
          if (!String.IsNullOrWhiteSpace(key)) copy.Dictionary[key] = entry.Value;
        }
        return copy;
      }
      try {
        if (TryUnsignedGomId(raw, out UInt64 id) && id != 0) return WorldResolveGomObject(id)?.Data;
        if (raw is String fqn && !String.IsNullOrWhiteSpace(fqn)) return WorldResolveGomObject(fqn.Trim())?.Data;
      } catch { }
      return null;
    }

    private IEnumerable<Object> SpaceSequence(Object raw) {
      if (raw == null) yield break;
      if (raw is String || raw is Byte[]) yield break;
      if (raw is GomObjectData data) {
        foreach (KeyValuePair<String, Object> pair in data.Dictionary.Where(x => !String.Equals(x.Key, "_count", StringComparison.OrdinalIgnoreCase)).OrderBy(x => SpaceSortKey(x.Key)))
          yield return pair.Value;
        yield break;
      }
      if (raw is IDictionary dictionary) {
        foreach (KeyValuePair<Object, Object> pair in SpaceDictionaryEntries(dictionary)
          .Where(x => !String.Equals(SpaceText(x.Key), "_count", StringComparison.OrdinalIgnoreCase))
          .OrderBy(x => SpaceSortKey(SpaceText(x.Key))))
          yield return pair.Value;
        yield break;
      }
      if (raw is IEnumerable enumerable) foreach (Object item in enumerable) yield return item;
    }

    private static System.Collections.IDictionary SpaceMapTable(Object raw) {
      if (raw == null) return null;
      if (raw is System.Collections.IDictionary dictionary) return dictionary;
      if (raw is GomObjectData data) {
        var result = new Dictionary<Object, Object>();
        foreach (KeyValuePair<String, Object> pair in data.Dictionary) {
          if (String.Equals(pair.Key, "_count", StringComparison.OrdinalIgnoreCase)) continue;
          result[pair.Key] = pair.Value;
        }
        return result;
      }
      return null;
    }

    // Do not use LINQ Cast<DictionaryEntry>() here. Generic Dictionary<TKey,TValue>
    // implements IDictionary, but its IEnumerable enumerator yields KeyValuePair<TKey,TValue>.
    // Asking IDictionary for its IDictionaryEnumerator guarantees DictionaryEntry semantics.
    private static IEnumerable<KeyValuePair<Object, Object>> SpaceDictionaryEntries(IDictionary dictionary) {
      if (dictionary == null) yield break;
      IDictionaryEnumerator enumerator = dictionary.GetEnumerator();
      while (enumerator.MoveNext())
        yield return new KeyValuePair<Object, Object>(enumerator.Key, enumerator.Value);
    }

    private static Int64 SpaceSortKey(String key) => Int64.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out Int64 value) ? value : Int64.MaxValue;

    private static Int32 SpaceCollectionCount(Object raw) {
      if (raw == null) return 0;
      if (raw is GomObjectData data) return data.Dictionary.Count(x => !String.Equals(x.Key, "_count", StringComparison.OrdinalIgnoreCase));
      if (raw is IDictionary dictionary) return SpaceDictionaryEntries(dictionary).Count(x => !String.Equals(SpaceText(x.Key), "_count", StringComparison.OrdinalIgnoreCase));
      if (raw is ICollection collection) return collection.Count;
      if (raw is IEnumerable enumerable) { Int32 count = 0; foreach (Object _ in enumerable) count++; return count; }
      return 0;
    }

    private String ResolveSpaceReferenceName(Object raw) {
      if (raw == null) return null;
      if (raw is GomObject node) return node.Name;
      try {
        if (TryUnsignedGomId(raw, out UInt64 id) && id != 0) return WorldResolveGomObject(id)?.Name;
        String text = raw.ToString()?.Trim();
        if (!String.IsNullOrWhiteSpace(text) && !TryTaxiUInt64(text, out _)) {
          try { return WorldResolveGomObject(text)?.Name ?? text; } catch { return text; }
        }
      } catch { }
      return raw.ToString()?.Trim();
    }

    private static Object SpaceDataValue(GomObjectData data, String name, String numericName) {
      if (data == null) return null;
      if (!String.IsNullOrWhiteSpace(name) && data.Dictionary.TryGetValue(name, out Object value)) return value;
      return !String.IsNullOrWhiteSpace(numericName) && data.Dictionary.TryGetValue(numericName, out value) ? value : null;
    }

    private static String SpaceText(Object raw) => raw?.ToString()?.Replace("\0", String.Empty).Trim() ?? String.Empty;
    private Boolean TrySpaceVector3(Object raw, out Vector3 value) {
      value = Vector3.Zero;
      if (raw is Vector3 direct) {
        if (Single.IsNaN(direct.X) || Single.IsInfinity(direct.X) || Single.IsNaN(direct.Y) || Single.IsInfinity(direct.Y) || Single.IsNaN(direct.Z) || Single.IsInfinity(direct.Z)) return false;
        value = direct; return true;
      }
      List<Object> parts = SpaceSequence(raw).Take(3).ToList();
      if (parts.Count < 3) return false;
      value = new Vector3(SpaceFloat(parts[0], 0f), SpaceFloat(parts[1], 0f), SpaceFloat(parts[2], 0f));
      return !(Single.IsNaN(value.X) || Single.IsInfinity(value.X) || Single.IsNaN(value.Y) || Single.IsInfinity(value.Y) || Single.IsNaN(value.Z) || Single.IsInfinity(value.Z));
    }

    private static Single SpaceFloat(Object raw, Single fallback) { try { return raw == null ? fallback : Convert.ToSingle(raw, CultureInfo.InvariantCulture); } catch { return fallback; } }
    private static Boolean SpaceBool(Object raw) { try { return raw != null && Convert.ToBoolean(raw, CultureInfo.InvariantCulture); } catch { return false; } }
    private static Boolean TrySpaceInt32(Object raw, out Int32 value) { try { return Int32.TryParse(raw?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value); } catch { value = 0; return false; } }

    private static UInt64 SpaceEncounterNodeId(String fqn) {
      const UInt64 offset = 14695981039346656037UL;
      const UInt64 prime = 1099511628211UL;
      UInt64 hash = offset;
      foreach (Char c in (fqn ?? String.Empty).ToUpperInvariant()) { hash ^= c; hash = unchecked(hash * prime); }
      return (0xE000UL << 48) | (((hash >> 16) & 0xFFFFFFFFUL) << 16) | ((hash >> 48) ^ (hash & 0xFFFFUL));
    }
  }
}
