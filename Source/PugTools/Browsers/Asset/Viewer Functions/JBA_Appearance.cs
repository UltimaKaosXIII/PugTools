using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using GomLib;
using GomLib.Models;
using TorArchive;

namespace FileFormats {
  internal sealed class JBAAppearancePart {
    internal String Model;
    internal String[] Materials;
    internal Boolean MaterialsBySubmesh;
    internal Dictionary<Int32, String> MaterialMap;
    internal Int32 SkinMaterialIndex = -1;
    internal String SkinMaterial;

    // Hand-authored appearance/MAG material lists are indexed by submesh.
    internal JBAAppearancePart(String model, params String[] materials)
      : this(model, true, materials) { }

    // Auto-resolved replacements for a GR2's embedded material slots keep the
    // original matId mapping instead of being interpreted as submesh indexes.
    internal JBAAppearancePart(
      String model,
      Boolean materialsBySubmesh,
      params String[] materials
    ) {
      Model = model;
      Materials = materials ?? Array.Empty<String>();
      MaterialsBySubmesh = materialsBySubmesh;
      MaterialMap = null;
    }

    // Exact appModelMaterialList mapping from NPP/AMI. Positive keys address
    // submesh indexes; -1 is the authored fallback for all remaining pieces.
    internal JBAAppearancePart(
      String model,
      IDictionary<Int32, String> materialMap,
      Int32 skinMaterialIndex = -1,
      String skinMaterial = null
    ) {
      Model = model;
      Materials = Array.Empty<String>();
      MaterialsBySubmesh = true;
      MaterialMap = materialMap == null
        ? null
        : new Dictionary<Int32, String>(materialMap);
      SkinMaterialIndex = skinMaterialIndex;
      SkinMaterial = skinMaterial;
    }
  }

  /// <summary>
  /// A small index built from PugTools' already loaded named-file tree. It lets
  /// JBA preview resolve models/materials without adding every animation body
  /// type to a switch statement.
  /// </summary>
  internal sealed class JBAAppearanceIndex {
    internal readonly List<String> CreatureModels = new List<String>();
    internal readonly List<String> MagFiles = new List<String>();
    internal readonly List<String> Skeletons = new List<String>();
    internal readonly List<String> MaterialNames = new List<String>();
    internal readonly List<String> AnimationAmxFiles = new List<String>();
    internal readonly List<String> AnimationMphFiles = new List<String>();
    internal readonly HashSet<String> MaterialNameSet =
      new HashSet<String>(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<String, Tuple<List<JBAAppearancePart>, String>> _cache =
      new Dictionary<String, Tuple<List<JBAAppearancePart>, String>>(
        StringComparer.OrdinalIgnoreCase
      );

    internal Boolean TryGetCached(
      String key,
      out List<JBAAppearancePart> parts,
      out String diagnostic
    ) {
      parts = null;
      diagnostic = null;
      if (!_cache.TryGetValue(key ?? String.Empty, out var cached))
        return false;

      parts = cached.Item1
        .Select(p => p.MaterialMap != null
          ? new JBAAppearancePart(p.Model, p.MaterialMap, p.SkinMaterialIndex, p.SkinMaterial)
          : new JBAAppearancePart(
              p.Model,
              p.MaterialsBySubmesh,
              p.Materials.ToArray()
            ))
        .ToList();
      diagnostic = cached.Item2;
      return true;
    }

    internal void Cache(
      String key,
      IEnumerable<JBAAppearancePart> parts,
      String diagnostic
    ) {
      List<JBAAppearancePart> copy = (parts ?? Enumerable.Empty<JBAAppearancePart>())
        .Select(p => p.MaterialMap != null
          ? new JBAAppearancePart(p.Model, p.MaterialMap, p.SkinMaterialIndex, p.SkinMaterial)
          : new JBAAppearancePart(
              p.Model,
              p.MaterialsBySubmesh,
              p.Materials.ToArray()
            ))
        .ToList();
      _cache[key ?? String.Empty] = Tuple.Create(copy, diagnostic ?? String.Empty);
    }
  }

  internal static class JBAAppearance {
    internal static String BodyTypeFromAnimationDirectory(String directory) {
      if (String.IsNullOrWhiteSpace(directory))
        return String.Empty;

      String path = directory.Replace('\\', '/').TrimEnd('/');
      Int32 slash = path.LastIndexOf('/');
      return (slash >= 0 ? path.Substring(slash + 1) : path).ToLowerInvariant();
    }

    /// <summary>
    /// Builds a reusable search index from AssetBrowser's /root/named tree
    /// keys (or from plain /resources/... paths). No TOR scan is performed;
    /// the browser has already enumerated these names during startup.
    /// </summary>
    internal static JBAAppearanceIndex BuildIndex(IEnumerable<String> paths) {
      var index = new JBAAppearanceIndex();
      var seenModels = new HashSet<String>(StringComparer.OrdinalIgnoreCase);
      var seenMags = new HashSet<String>(StringComparer.OrdinalIgnoreCase);
      var seenSkeletons = new HashSet<String>(StringComparer.OrdinalIgnoreCase);
      var seenAmx = new HashSet<String>(StringComparer.OrdinalIgnoreCase);
      var seenMph = new HashSet<String>(StringComparer.OrdinalIgnoreCase);

      foreach (String raw in paths ?? Enumerable.Empty<String>()) {
        if (String.IsNullOrWhiteSpace(raw)) continue;

        String path = NormalizeResourcePath(raw);
        if (!path.StartsWith("/resources/", StringComparison.OrdinalIgnoreCase))
          continue;

        if (path.EndsWith(".gr2", StringComparison.OrdinalIgnoreCase)) {
          if (path.StartsWith(
                "/resources/art/dynamic/creature/model/",
                StringComparison.OrdinalIgnoreCase
              )) {
            if (seenModels.Add(path)) index.CreatureModels.Add(path);
          }

          if (path.StartsWith(
                "/resources/art/dynamic/spec/",
                StringComparison.OrdinalIgnoreCase
              ) && Path.GetFileNameWithoutExtension(path)
                   .EndsWith("_skeleton", StringComparison.OrdinalIgnoreCase)) {
            if (seenSkeletons.Add(path)) index.Skeletons.Add(path);
          }
        }
        else if (path.EndsWith(".mag", StringComparison.OrdinalIgnoreCase)
                 && path.StartsWith(
                   "/resources/art/dynamic/spec/",
                   StringComparison.OrdinalIgnoreCase
                 )) {
          if (seenMags.Add(path)) index.MagFiles.Add(path);
        }
        else if (path.EndsWith(".mat", StringComparison.OrdinalIgnoreCase)
                 && path.StartsWith(
                   "/resources/art/shaders/materials/",
                   StringComparison.OrdinalIgnoreCase
                 )) {
          String material = Path.GetFileNameWithoutExtension(path);
          if (index.MaterialNameSet.Add(material))
            index.MaterialNames.Add(material);
        }
        else if (path.EndsWith(".mph.amx", StringComparison.OrdinalIgnoreCase)
                 && path.StartsWith(
                   "/resources/anim/",
                   StringComparison.OrdinalIgnoreCase
                 )) {
          if (seenAmx.Add(path))
            index.AnimationAmxFiles.Add(path);
        }
        else if (path.EndsWith(".mph", StringComparison.OrdinalIgnoreCase)
                 && path.StartsWith(
                   "/resources/anim/",
                   StringComparison.OrdinalIgnoreCase
                 )) {
          if (seenMph.Add(path))
            index.AnimationMphFiles.Add(path);
        }
      }

      return index;
    }

    /// <summary>
    /// Resolve a display appearance automatically. The primary source is the
    /// game's own npp.npc.default.&lt;bt&gt; + AMI data. Placeables resolve from MAG.
    /// Name + actual bone compatibility and the old tiny table are fallbacks.
    /// </summary>
    internal static List<JBAAppearancePart> Resolve(
      Assets assets,
      DataObjectModel dom,
      GR2 skeleton,
      String bodyType,
      String animationDirectory,
      JBAAppearanceIndex index,
      out String diagnostic
    ) {
      bodyType = (bodyType ?? String.Empty).ToLowerInvariant();
      animationDirectory = NormalizeResourcePath(animationDirectory ?? String.Empty)
        .TrimEnd('/');

      String cacheKey = animationDirectory + "|" + bodyType;
      if (index != null
          && index.TryGetCached(cacheKey, out var cachedParts, out diagnostic))
        return cachedParts;

      Boolean likelyPlaceable = animationDirectory.IndexOf(
        "/anim/placeable/",
        StringComparison.OrdinalIgnoreCase
      ) >= 0;

      // A few legacy/non-humanoid animation body types are authored as a
      // compact JBA preview appearance rather than a useful npp.npc.default.*
      // dress. Ithorian is one of those: Jedipedia intentionally uses the
      // creature body plus its dedicated eye material. Keep this as a narrow
      // exception while all ordinary body types continue through automatic
      // NPP/MAG/index discovery.
      if (!likelyPlaceable && bodyType == "ithorian") {
        List<JBAAppearancePart> ithorian = GetDefault(bodyType);
        if (ithorian.Count > 0) {
          diagnostic = "Body override: ithorian";
          index?.Cache(cacheKey, ithorian, diagnostic);
          return ithorian;
        }
      }

      // This is the same source the game / Jedipedia uses for a body's default
      // dress: npp.npc.default.<bt>. It resolves through AMI, so model,
      // material slots and attachments come from the game's own data instead
      // of a hand-maintained table or filename guess.
      if (!likelyPlaceable && dom != null && assets != null) {
        if (TryResolveFromNpp(
              assets,
              dom,
              bodyType,
              index,
              out List<JBAAppearancePart> nppParts,
              out String nppInfo
            )) {
          diagnostic = nppInfo;
          index?.Cache(cacheKey, nppParts, diagnostic);
          return nppParts;
        }
      }

      // Placeables don't normally have an NPP. Their MAG contains the authored
      // animation-network folder plus mesh/material and is the authoritative
      // source for the preview appearance.
      if (assets != null && index != null) {
        if (TryResolveFromMag(
              assets,
              bodyType,
              animationDirectory,
              index,
              out List<JBAAppearancePart> magParts,
              out String magInfo
            )) {
          diagnostic = magInfo;
          index.Cache(cacheKey, magParts, diagnostic);
          return magParts;
        }
      }

      // Some odd animation folders have no npp.npc.default.* entry. In that
      // case use an actual skeleton compatibility check before resorting to a
      // tiny exception table. This still lets newly discovered body types work
      // without adding source code entries one by one.
      if (assets != null && index != null) {
        if (TryResolveCreatureModel(
              assets,
              skeleton,
              bodyType,
              index,
              out JBAAppearancePart autoPart,
              out String autoInfo,
              out Boolean autoConfident
            )) {
          List<JBAAppearancePart> knownOverride = GetDefault(bodyType);
          if (!autoConfident && knownOverride.Count > 0) {
            diagnostic = "override (auto ambiguous: " + autoInfo + ")";
            index.Cache(cacheKey, knownOverride, diagnostic);
            return knownOverride;
          }

          List<JBAAppearancePart> result = One(autoPart);
          diagnostic = autoInfo;
          index.Cache(cacheKey, result, diagnostic);
          return result;
        }
      }

      List<JBAAppearancePart> fallback = GetDefault(bodyType);
      diagnostic = fallback.Count > 0
        ? "override fallback"
        : "skeleton only (no appearance match)";
      index?.Cache(cacheKey, fallback, diagnostic);
      return fallback;
    }

    /// <summary>
    /// Resolve the game's default NPC appearance for a body type. AppSlot's
    /// Model/Material properties already resolve through AMI, so this also
    /// follows BioWare's indirection instead of guessing resource filenames.
    /// </summary>
    private static Boolean TryResolveFromNpp(
      Assets assets,
      DataObjectModel dom,
      String animationBodyType,
      JBAAppearanceIndex index,
      out List<JBAAppearancePart> parts,
      out String diagnostic
    ) {
      parts = new List<JBAAppearancePart>();
      diagnostic = null;
      if (assets == null || dom?.AppearanceLoader == null)
        return false;

      String nppBodyType = NppBodyType(animationBodyType);
      if (String.IsNullOrWhiteSpace(nppBodyType))
        return false;

      String fqn = "npp.npc.default." + nppBodyType;
      NpcAppearance npc;
      try {
        npc = dom.AppearanceLoader.Load(fqn) as NpcAppearance;
      }
      catch (Exception ex) {
        System.Diagnostics.Debug.WriteLine(
          "JBA default NPP load failed for " + fqn + ": " + ex
        );
        return false;
      }

      if (npc?.AppearanceSlotMap == null || npc.AppearanceSlotMap.Count == 0)
        return false;

      var seenModels = new HashSet<String>(StringComparer.OrdinalIgnoreCase);
      Int32 missingModels = 0;

      foreach (KeyValuePair<String, List<AppSlot>> slotEntry in npc.AppearanceSlotMap) {
        AppSlot slot = slotEntry.Value?.FirstOrDefault();
        if (slot == null) continue;

        String slotBodyType = String.IsNullOrWhiteSpace(slot.BodyType)
          ? (String.IsNullOrWhiteSpace(npc.BodyType) ? nppBodyType : npc.BodyType)
          : slot.BodyType;

        Dictionary<Int32, String> materialMap;
        Int32 skinMaterialIndex = -1;
        String skinMaterial = null;
        try {
          materialMap = ResolveNppMaterialMap(
            assets,
            index,
            npc,
            slotEntry.Key,
            slot,
            slotBodyType,
            out skinMaterialIndex,
            out skinMaterial
          );
        }
        catch (Exception ex) {
          // An AMI/material failure must not discard an otherwise valid model.
          System.Diagnostics.Debug.WriteLine(
            "JBA NPP material resolve failed for " + fqn + "/" + slotEntry.Key
            + ": " + ex
          );
          materialMap = new Dictionary<Int32, String>();
        }

        String model = String.Empty;
        try {
          model = ToResourceModelPath(
            ExpandAppearanceToken(slot.Model, slotBodyType)
          );
        }
        catch (Exception ex) {
          System.Diagnostics.Debug.WriteLine(
            "JBA NPP model resolve failed for " + fqn + "/" + slotEntry.Key
            + ": " + ex
          );
        }

        AddNppPart(
          assets,
          index,
          parts,
          seenModels,
          model,
          materialMap,
          skinMaterialIndex,
          skinMaterial,
          ref missingModels
        );

        List<String> attachments = null;
        try {
          attachments = slot.AttachedModels;
        }
        catch (Exception ex) {
          System.Diagnostics.Debug.WriteLine(
            "JBA NPP attachment resolve failed for " + fqn + "/" + slotEntry.Key
            + ": " + ex
          );
        }

        foreach (String attachment in attachments ?? Enumerable.Empty<String>()) {
          String attachPath = ToResourceModelPath(
            ExpandAppearanceToken(attachment, slotBodyType)
          );
          AddNppPart(
            assets,
            index,
            parts,
            seenModels,
            attachPath,
            materialMap,
            skinMaterialIndex,
            skinMaterial,
            ref missingModels
          );
        }
      }

      if (parts.Count == 0)
        return false;

      diagnostic = "auto NPP: " + fqn
        + (missingModels > 0 ? " | " + missingModels + " missing model(s)" : String.Empty);
      return true;
    }

    private static void AddNppPart(
      Assets assets,
      JBAAppearanceIndex index,
      List<JBAAppearancePart> parts,
      HashSet<String> seenModels,
      String modelPath,
      Dictionary<Int32, String> materialMap,
      Int32 skinMaterialIndex,
      String skinMaterial,
      ref Int32 missingModels
    ) {
      if (String.IsNullOrWhiteSpace(modelPath)
          || !modelPath.EndsWith(".gr2", StringComparison.OrdinalIgnoreCase)
          || !seenModels.Add(modelPath))
        return;

      if (assets.FindFile(modelPath) == null) {
        missingModels++;
        return;
      }

      // Some current/legacy NPP entries resolve the mesh but do not expose a
      // usable appModelMaterialList through GomLib. The dynamic category's
      // index.xml is the authoritative pre-2.1 form of the same information.
      // Use it before ever falling back to the exporter materials baked into
      // the GR2 (usually "default" / "defaultMirror").
      Dictionary<Int32, String> effectiveMap = materialMap == null
        ? new Dictionary<Int32, String>()
        : new Dictionary<Int32, String>(materialMap);
      Int32 effectiveSkinIndex = skinMaterialIndex;

      if (effectiveMap.Count == 0
          && TryResolveMaterialsFromDynamicIndex(
            assets,
            modelPath,
            out Dictionary<Int32, String> indexedMap,
            out Int32 indexedSkinIndex,
            out _
          )) {
        effectiveMap = indexedMap;
        if (effectiveSkinIndex < 0) effectiveSkinIndex = indexedSkinIndex;
      }

      parts.Add(new JBAAppearancePart(
        modelPath,
        effectiveMap,
        effectiveSkinIndex,
        skinMaterial
      ));
    }

    private static Dictionary<Int32, String> ResolveNppMaterialMap(
      Assets assets,
      JBAAppearanceIndex index,
      NpcAppearance npc,
      String slotKey,
      AppSlot slot,
      String bodyType,
      out Int32 skinMaterialIndex,
      out String skinMaterial
    ) {
      var result = new Dictionary<Int32, String>();
      skinMaterialIndex = -1;
      skinMaterial = null;

      AMIEntry ami = slot.AMI;
      if (ami != null) {
        if (ami.SkinMaterialIndex >= Int32.MinValue
            && ami.SkinMaterialIndex <= Int32.MaxValue)
          skinMaterialIndex = (Int32)ami.SkinMaterialIndex;

        if (ami.MaterialList != null
            && ami.MaterialList.TryGetValue(
              slot.MaterialIndex,
              out Dictionary<Int64, String> rawMap
            )
            && rawMap != null) {
          foreach (KeyValuePair<Int64, String> entry in rawMap) {
            if (entry.Key < Int32.MinValue || entry.Key > Int32.MaxValue)
              continue;

            String material = NormalizeMaterialName(
              ExpandAppearanceToken(entry.Value, bodyType)
            );
            if (!String.IsNullOrWhiteSpace(material))
              result[(Int32)entry.Key] = material;
          }
        }
      }

      // AppSlot.Material0/MaterialMirror use AMI.GetMaterial(), i.e. exactly
      // the same compatibility path as PugTools' established NPC renderer.
      // Keep them as a safety net for old/incomplete AMI material dictionaries.
      String material0 = NormalizeMaterialName(
        ExpandAppearanceToken(slot.Material0, bodyType)
      );
      String mirror = NormalizeMaterialName(
        ExpandAppearanceToken(slot.MaterialMirror, bodyType)
      );

      if (result.Count == 0) {
        if (!String.IsNullOrWhiteSpace(material0)) result[0] = material0;
        if (!String.IsNullOrWhiteSpace(mirror)) result[-1] = mirror;
      }
      else {
        if (!result.ContainsKey(0) && !String.IsNullOrWhiteSpace(material0))
          result[0] = material0;
        if (!result.ContainsKey(-1)
            && !result.ContainsKey(1)
            && !String.IsNullOrWhiteSpace(mirror))
          result[-1] = mirror;
      }

      // Jedipedia does not infer bare skin from a material name. The head AMI
      // explicitly owns the skin material for every other slot, and the target
      // model's appModelSkinMaterialIndex says which submesh must use it. This
      // fixes horns/hands/chests whose GR2-baked material is only a placeholder.
      try {
        if (npc.AppearanceSlotMap.TryGetValue(
              "appSlotHead",
              out List<AppSlot> headSlots
            )) {
          AppSlot head = headSlots?.FirstOrDefault();
          if (head?.AMI?.ChildSkinMaterials != null
              && head.AMI.ChildSkinMaterials.TryGetValue(slotKey, out String skin)) {
            skinMaterial = NormalizeMaterialName(
              ExpandAppearanceToken(skin, bodyType)
            );
          }
        }
      }
      catch (Exception ex) {
        System.Diagnostics.Debug.WriteLine(
          "JBA NPP child skin resolve failed for " + slotKey + ": " + ex
        );
      }

      // Strip malformed/path-qualified entries before GR2_Material.ParseMAT.
      // That parser expects only the MAT basename and constructs
      // /resources/art/shaders/materials/<name>.mat itself.
      foreach (Int32 key in result.Keys.ToList()) {
        String material = NormalizeMaterialName(result[key]);
        if (String.IsNullOrWhiteSpace(material)) {
          result.Remove(key);
          continue;
        }

        // Prefer a known-good convenience fallback when a stale AMI entry no
        // longer exists in the currently loaded assets.
        if (!MaterialExists(assets, index, material)) {
          String fallback = (key == -1 || key == 1) ? mirror : material0;
          fallback = NormalizeMaterialName(fallback);
          if (!String.IsNullOrWhiteSpace(fallback)
              && MaterialExists(assets, index, fallback))
            material = fallback;
        }
        result[key] = material;
      }

      skinMaterial = NormalizeMaterialName(skinMaterial);
      return result;
    }

    private static String NormalizeMaterialName(String value) {
      if (String.IsNullOrWhiteSpace(value)) return String.Empty;

      String material = value.Trim().Replace('\\', '/');
      Int32 variant = material.IndexOf('#');
      if (variant >= 0) material = material.Substring(0, variant);

      // AMI/index.xml sources may contain either a basename or a material
      // resource path. GR2_Material.ParseMAT accepts only the basename.
      Int32 slash = material.LastIndexOf('/');
      if (slash >= 0 && slash + 1 < material.Length)
        material = material.Substring(slash + 1);
      if (material.EndsWith(".mat", StringComparison.OrdinalIgnoreCase))
        material = material.Substring(0, material.Length - 4);

      return material.Trim().ToLowerInvariant();
    }

    private static String NppBodyType(String animationBodyType) {
      String bt = (animationBodyType ?? String.Empty).Trim().ToLowerInvariant();
      if (IsHumanoidBodyType(bt) && bt.EndsWith("new", StringComparison.Ordinal))
        bt = bt.Substring(0, bt.Length - 3);
      return bt;
    }

    private static String ExpandAppearanceToken(String value, String bodyType) {
      if (String.IsNullOrWhiteSpace(value)) return String.Empty;
      String result = value.Trim()
        .Replace("[bt]", bodyType ?? String.Empty)
        .Replace("[BT]", bodyType ?? String.Empty);

      String gender = String.Empty;
      if ((bodyType ?? String.Empty).StartsWith("bf", StringComparison.OrdinalIgnoreCase))
        gender = "f";
      else if ((bodyType ?? String.Empty).StartsWith("bm", StringComparison.OrdinalIgnoreCase))
        gender = "m";

      if (!String.IsNullOrEmpty(gender))
        result = result.Replace("[gen]", gender).Replace("[GEN]", gender);
      return result;
    }

    private static String ToResourceModelPath(String model) {
      if (String.IsNullOrWhiteSpace(model)) return String.Empty;
      String path = NormalizeResourcePath(model);
      if (path.StartsWith("/resources/", StringComparison.OrdinalIgnoreCase))
        return path;
      if (path.StartsWith("/art/", StringComparison.OrdinalIgnoreCase))
        return "/resources" + path;
      return path;
    }

    /// <summary>
    /// Direct bodyType_skeleton first, then close named skeletons, then the
    /// handful of semantic aliases where the game's animation folder and rig
    /// are intentionally named differently.
    /// </summary>
    internal static String ResolveSkeletonPath(
      Assets assets,
      String bodyType,
      JBAAppearanceIndex index,
      out String diagnostic
    ) {
      bodyType = (bodyType ?? String.Empty).ToLowerInvariant();
      String direct = "/resources/art/dynamic/spec/" + bodyType + "_skeleton.gr2";
      if (assets?.FindFile(direct) != null) {
        diagnostic = "direct " + Path.GetFileName(direct);
        return direct;
      }

      // Truly semantic aliases, kept as exceptions instead of growing the
      // appearance table. All ordinary same-name/prefix rigs are automatic.
      String alias = bodyType switch {
        "battle" => "battledroid",
        "battle_warbot_rakata_boss" => "battlewarbotrakataboss",
        "slug" => "klorslug",
        "slugboss" => "klorslugboss",
        "veractyl" => "lizard",
        "colicoidboss" => "colicoid",
        _ => String.Empty
      };

      if (!String.IsNullOrEmpty(alias)) {
        String aliasPath =
          "/resources/art/dynamic/spec/" + alias + "_skeleton.gr2";
        if (assets?.FindFile(aliasPath) != null) {
          diagnostic = "alias " + alias;
          return aliasPath;
        }
      }

      if (index != null) {
        String best = null;
        Int32 bestScore = 0;
        foreach (String candidate in index.Skeletons) {
          String stem = Path.GetFileNameWithoutExtension(candidate);
          if (stem.EndsWith("_skeleton", StringComparison.OrdinalIgnoreCase))
            stem = stem.Substring(0, stem.Length - "_skeleton".Length);

          Int32 score = ScoreName(bodyType, stem);
          if (score > bestScore) {
            bestScore = score;
            best = candidate;
          }
        }

        // Avoid attaching a completely unrelated skeleton just because it was
        // the least-bad filename in the index.
        if (best != null && bestScore >= 900) {
          diagnostic = "auto " + Path.GetFileName(best);
          return best;
        }
      }

      diagnostic = "not found";
      return null;
    }

    internal static List<JBAAppearancePart> GetDefault(String bodyType) {
      bodyType = (bodyType ?? String.Empty).ToLowerInvariant();

      if (IsHumanoidBodyType(bodyType)) {
        String bt = bodyType.Substring(0, bodyType.Length - 3);
        String gender = bt.StartsWith("bf", StringComparison.OrdinalIgnoreCase) ? "f" : "m";

        var parts = new List<JBAAppearancePart> {
          new JBAAppearancePart($"/resources/art/dynamic/boot/model/boot_tall_{bt}_archetype.gr2",
            "boot_tall_heavy_bh_a02c01_u"),
          new JBAAppearancePart($"/resources/art/dynamic/bracer/model/bracer_short_{bt}_archetype.gr2",
            "bracer_short_heavy_bh_a02c01_u_v01"),
          new JBAAppearancePart($"/resources/art/dynamic/chest/model/chest_tight_{bt}_archetype.gr2",
            $"chest_tight_light_ge_a33hacker01_{gender}"),
          new JBAAppearancePart($"/resources/art/dynamic/hand/model/hand_gauntlet_{bt}_archetype.gr2",
            "hand_gauntlet_heavy_bh_a02c01_u"),
          new JBAAppearancePart($"/resources/art/dynamic/leg/model/leg_pant_{bt}_archetype.gr2",
            $"leg_pant_light_ge_a33hacker01_{gender}_v02"),
          new JBAAppearancePart($"/resources/art/dynamic/waist/model/waist_belt_{bt}_archetype.gr2",
            "waist_belt_heavy_bh_a02c01_u")
        };

        if (gender == "f") {
          parts.Add(new JBAAppearancePart(
            $"/resources/art/dynamic/head/model/head_human_{bt}_caucasian_a01.gr2",
            "head_human_caucasian_a01c01_f", "eye_human_non_a01_c01"));
          parts.Add(new JBAAppearancePart(
            $"/resources/art/dynamic/hair/model/hair_human_{bt}_non_a01.gr2",
            "hair_human_non_a01_v01_f"));
        } else {
          parts.Add(new JBAAppearancePart(
            $"/resources/art/dynamic/head/model/head_human_{bt}_caucasian_a01.gr2",
            $"head_human_{bt}_caucasian_a01c01", "eye_human_non_a01_c01"));

          if (bt != "bms") {
            String hair = bt == "bmn" ? "a05" : "a04";
            parts.Add(new JBAAppearancePart(
              $"/resources/art/dynamic/hair/model/hair_human_{bt}_non_{hair}.gr2",
              $"hair_human_non_{hair}_v01_m"));
          }
        }

        return parts;
      }

      // Compatibility overrides. These are intentionally not the primary
      // lookup anymore; Resolve() reaches them only when automatic discovery
      // cannot select a trustworthy result.
      return bodyType switch {
        "acklay" => One("/resources/art/dynamic/creature/model/acklay_acklay_a01.gr2", "acklay_acklay_a01_v01"),
        "acklaydroid" => One("/resources/art/dynamic/creature/model/acklay_huttaminingdroid_a01.gr2", "acklay_huttaminingdroid_a01_v01"),
        "bantha" => One("/resources/art/dynamic/creature/model/bantha_bantha_a01.gr2", "bantha_bantha_a01_v01"),
        "cat" => One("/resources/art/dynamic/creature/model/cat_nexu_a01.gr2", "cat_nexu_a01_v01"),
        "dewback" => One("/resources/art/dynamic/creature/model/dewback_dewback_a01.gr2"),
        "dog" => One("/resources/art/dynamic/creature/model/dog_akk_a01.gr2", "dog_akk_a01_v01"),
        "gree" => One("/resources/art/dynamic/creature/model/gree_gree_a01.gr2", "gree_gree_a01_v01", "gree_gree_eye_a01_v01"),
        "manta" => One("/resources/art/dynamic/creature/model/manta_thranta_a01.gr2", "manta_thranta_a01_v01"),
        "rancor" => One("/resources/art/dynamic/creature/model/rancor_rancor_a01.gr2", "rancor_rancor_a01_v01"),
        "tauntaun" => One("/resources/art/dynamic/creature/model/tauntaun_tauntaun_a01.gr2", "tauntaun_tauntaun_a01_v01"),
        "veractyl" => One("/resources/art/dynamic/creature/model/lizard_veractyl_a01.gr2"),
        "wampa" => One("/resources/art/dynamic/creature/model/wampa_wampa_a01.gr2", "wampa_wampa_a01_v01"),
        "assassin" => One("/resources/art/dynamic/creature/model/assassin_rogue_a01.gr2", "assassin_rogue_a01_v01"),
        "astromech" => One("/resources/art/dynamic/creature/model/astromech_generic_a01.gr2", "astromech_generic_a01_v01"),
        "atst" => One("/resources/art/dynamic/creature/model/atst_walker_mount01.gr2", "atst_walker_mount01_v01"),
        "battle" => One("/resources/art/dynamic/creature/model/battledroid_combat_a01.gr2", "battledroid_combat_a01_v01"),
        "protocol" => One("/resources/art/dynamic/creature/model/protocol_courier_a01.gr2", "protocol_courier_a01_v01"),
        "walker" => One("/resources/art/dynamic/creature/model/walker_atst_a01.gr2", "walker_atst_a01_v01"),
        "chevin" => One("/resources/art/dynamic/creature/model/chevin_chevin_a01.gr2", "chevin_chevin_a01_v01"),
        "hutt" => One("/resources/art/dynamic/creature/model/hutt_hutt_a01.gr2", "hutt_hutt_a01_v01", "eye_hutt_hutt_a01_c01"),
        "ithorian" => One("/resources/art/dynamic/creature/model/ithorian_ithorian_a01.gr2", "ithorian_ithorian_a01_v01", "eye_ithorian_non_a01_v01"),
        _ => new List<JBAAppearancePart>()
      };
    }

    private static Boolean TryResolveFromMag(
      Assets assets,
      String bodyType,
      String animationDirectory,
      JBAAppearanceIndex index,
      out List<JBAAppearancePart> parts,
      out String diagnostic
    ) {
      parts = null;
      diagnostic = null;

      Boolean likelyPlaceable = animationDirectory.IndexOf(
        "/anim/placeable/",
        StringComparison.OrdinalIgnoreCase
      ) >= 0;

      // AnimNetworkFolder is authoritative. Scan the small named MAG set in
      // score order so common direct matches are cheap, but do not throw away
      // unrelated filenames: many valid MAG stems do not resemble the anim
      // folder at all.
      var candidates = index.MagFiles
        .Select(path => new {
          Path = path,
          Score = ScoreName(bodyType, Path.GetFileNameWithoutExtension(path))
        })
        .OrderByDescending(x => x.Score)
        .ToList();

      JBAAppearancePart filenameFallback = null;
      String filenameFallbackInfo = null;

      foreach (var candidate in candidates) {
        TorArchive.File file = assets.FindFile(candidate.Path);
        if (file == null) continue;

        try {
          Dictionary<String, String> values;
          using (Stream stream = file.OpenCopyInMemory())
          using (StreamReader reader = new StreamReader(
            stream,
            Encoding.UTF8,
            true,
            4096,
            false
          )) {
            values = ParseMag(reader.ReadToEnd());
          }

          if (!values.TryGetValue("Mesh", out String mesh) ||
              String.IsNullOrWhiteSpace(mesh))
            continue;

          String modelPath = NormalizeResourcePath(
            mesh.StartsWith("/resources/", StringComparison.OrdinalIgnoreCase)
              ? mesh
              : "/resources/" + mesh.TrimStart('/', '\\')
          );
          if (assets.FindFile(modelPath) == null)
            continue;

          String[] materials = Array.Empty<String>();
          if (values.TryGetValue("Material", out String materialText)) {
            materials = materialText
              .Split((Char[])null, StringSplitOptions.RemoveEmptyEntries)
              .Select(x => Path.GetFileNameWithoutExtension(x.Trim()))
              .Where(x => !String.IsNullOrWhiteSpace(x))
              .ToArray();
          }

          Boolean folderMatch = false;
          if (values.TryGetValue("AnimNetworkFolder", out String animFolder)
              && !String.IsNullOrWhiteSpace(animFolder)) {
            String magFolder = NormalizeResourcePath(
              animFolder.StartsWith("/resources/", StringComparison.OrdinalIgnoreCase)
                ? animFolder
                : "/resources/" + animFolder.TrimStart('/', '\\')
            ).TrimEnd('/');
            folderMatch = String.Equals(
              magFolder,
              animationDirectory,
              StringComparison.OrdinalIgnoreCase
            );
          }

          JBAAppearancePart part = new JBAAppearancePart(
            modelPath,
            true,
            materials
          );

          if (folderMatch) {
            parts = One(part);
            diagnostic = "auto MAG: " + Path.GetFileName(candidate.Path)
              + " (AnimNetworkFolder)";
            return true;
          }

          // Some old specs omit AnimNetworkFolder. Remember, but don't return,
          // the strongest body-name candidate until every exact folder match
          // had a chance to win.
          if (likelyPlaceable && filenameFallback == null && candidate.Score >= 1000) {
            filenameFallback = part;
            filenameFallbackInfo = "auto MAG: " + Path.GetFileName(candidate.Path)
              + " (name fallback)";
          }
        }
        catch (Exception ex) {
          System.Diagnostics.Debug.WriteLine(
            "JBA MAG appearance failed for " + candidate.Path + ": " + ex
          );
        }
      }

      if (filenameFallback != null) {
        parts = One(filenameFallback);
        diagnostic = filenameFallbackInfo;
        return true;
      }

      return false;
    }

    private static Boolean TryResolveCreatureModel(
      Assets assets,
      GR2 skeleton,
      String bodyType,
      JBAAppearanceIndex index,
      out JBAAppearancePart part,
      out String diagnostic,
      out Boolean confident
    ) {
      part = null;
      diagnostic = null;
      confident = false;
      if (String.IsNullOrWhiteSpace(bodyType) || index.CreatureModels.Count == 0)
        return false;

      HashSet<String> skeletonBones = new HashSet<String>(
        (skeleton?.skeleton_bones ?? new List<GR2_Bone_Skeleton>())
          .Select(b => CanonicalBoneName(b.boneName))
          .Where(n => !String.IsNullOrWhiteSpace(n)),
        StringComparer.OrdinalIgnoreCase
      );

      var lexical = index.CreatureModels
        .Select(path => new ModelCandidate {
          Path = path,
          Lexical = ScoreName(bodyType, Path.GetFileNameWithoutExtension(path))
        })
        .Where(x => x.Lexical > 0)
        .OrderByDescending(x => x.Lexical)
        .Take(18)
        .ToList();

      ModelCandidate best = null;
      ModelCandidate second = null;
      GR2 bestModel = null;

      foreach (ModelCandidate candidate in lexical) {
        TorArchive.File file = assets.FindFile(candidate.Path);
        if (file == null) continue;

        GR2 model = null;
        try {
          using (Stream stream = file.OpenCopyInMemory())
          using (BinaryReader br = new BinaryReader(stream))
            model = new GR2(br, Path.GetFileName(candidate.Path));

          if (model.meshes.Count == 0) {
            model.Dispose();
            continue;
          }

          HashSet<String> meshBones = new HashSet<String>(
            model.meshes
              .Where(m => m.meshBones != null)
              .SelectMany(m => m.meshBones)
              .Select(b => CanonicalBoneName(b.boneName))
              .Where(n => !String.IsNullOrWhiteSpace(n)),
            StringComparer.OrdinalIgnoreCase
          );

          Int32 matched = meshBones.Count == 0
            ? 0
            : meshBones.Count(n => skeletonBones.Contains(n));
          Double ratio = meshBones.Count == 0
            ? 0.0
            : matched / (Double)meshBones.Count;

          candidate.BoneRatio = ratio;
          candidate.Final = candidate.Lexical
            + (Int32)Math.Round(ratio * 2200.0)
            + (ratio >= 0.90 ? 700 : 0)
            + (ratio >= 0.98 ? 400 : 0);

          // A low-overlap mesh can still share a convenient filename. Do not
          // let it beat a real rig match unless the file has no bone bindings.
          if (meshBones.Count > 0 && ratio < 0.45) {
            model.Dispose();
            continue;
          }

          if (best == null || candidate.Final > best.Final) {
            second = best;
            bestModel?.Dispose();
            best = candidate;
            bestModel = model;
          } else {
            if (second == null || candidate.Final > second.Final)
              second = candidate;
            model.Dispose();
          }
        }
        catch (Exception ex) {
          model?.Dispose();
          System.Diagnostics.Debug.WriteLine(
            "JBA auto model probe failed for " + candidate.Path + ": " + ex
          );
        }
      }

      if (best == null || bestModel == null)
        return false;

      // Require either a strong name match or a very strong skeleton match.
      // This stops an unrelated creature that happens to share generic helper
      // bones from becoming the preview for an unknown body type.
      if (best.Lexical < 650 && best.BoneRatio < 0.92) {
        bestModel.Dispose();
        return false;
      }

      String materialInfo;

      // Before looking at the GR2's embedded material names, consult the
      // category index.xml. SWTOR GR2s commonly contain exporter placeholders
      // named "default" / "defaultMirror"; those MAT files really exist,
      // but are not the appearance and intentionally render grey/white.
      if (TryResolveMaterialsFromDynamicIndex(
            assets,
            best.Path,
            out Dictionary<Int32, String> indexedMap,
            out Int32 indexedSkinIndex,
            out String indexedInfo
          )) {
        part = new JBAAppearancePart(
          best.Path,
          indexedMap,
          indexedSkinIndex
        );
        materialInfo = indexedInfo;
      }
      else {
        MaterialResolution materialResolution = ResolveMaterialsForModel(
          assets,
          index,
          best.Path,
          bestModel
        );

        part = new JBAAppearancePart(
          best.Path,
          materialResolution.BySubmesh,
          materialResolution.Materials
        );
        materialInfo = materialResolution.Materials.Length == 0
          ? "embedded materials"
          : "auto materials";
      }

      bestModel.Dispose();

      Int32 gap = second == null ? Int32.MaxValue : best.Final - second.Final;
      confident = gap >= 180
        || best.Lexical >= 3600
        || (best.BoneRatio >= 0.98 && gap >= 80);

      diagnostic = String.Format(
        "auto model: {0} | bones {1:0}% | {2}{3}",
        Path.GetFileName(best.Path),
        best.BoneRatio * 100.0,
        materialInfo,
        confident ? String.Empty : " | ambiguous"
      );
      return true;
    }

    private static MaterialResolution ResolveMaterialsForModel(
      Assets assets,
      JBAAppearanceIndex index,
      String modelPath,
      GR2 model
    ) {
      String modelStem = Path.GetFileNameWithoutExtension(modelPath) ?? String.Empty;
      List<String> embedded = (model.materials ?? new List<GR2_Material>())
        .Select(m => m?.materialName ?? String.Empty)
        .ToList();

      if (embedded.Count > 0) {
        Boolean allExact = embedded.All(name =>
          !IsExporterPlaceholderMaterial(name)
          && MaterialExists(assets, index, name)
        );
        if (allExact)
          return new MaterialResolution(Array.Empty<String>(), false);

        // Preserve the GR2's material slot count/order, but replace exporter
        // placeholders with the closest real MAT. LoadComposite will retain
        // each mesh piece's original matId for this mode.
        var replacements = new List<String>();
        String modelFallback = FindBestMaterial(index, modelStem, assets);

        foreach (String embeddedName in embedded) {
          String resolved = !IsExporterPlaceholderMaterial(embeddedName)
                            && MaterialExists(assets, index, embeddedName)
            ? embeddedName
            : null;

          if (String.IsNullOrWhiteSpace(resolved)
              && !IsExporterPlaceholderMaterial(embeddedName))
            resolved = FindBestMaterial(index, embeddedName, assets);

          if (String.IsNullOrWhiteSpace(resolved))
            resolved = modelFallback;
          if (String.IsNullOrWhiteSpace(resolved))
            resolved = modelStem;

          replacements.Add(resolved);
        }

        return new MaterialResolution(replacements.ToArray(), false);
      }

      // Most creature assets follow model_a01 -> model_a01_v01. Try those
      // cheap deterministic names before searching the material index.
      String[] guesses = {
        modelStem + "_v01",
        modelStem + "_c01",
        modelStem
      };
      foreach (String guess in guesses) {
        if (MaterialExists(assets, index, guess))
          return new MaterialResolution(new[] { guess }, true);
      }

      String fuzzy = FindBestMaterial(index, modelStem, assets);
      if (!String.IsNullOrWhiteSpace(fuzzy))
        return new MaterialResolution(new[] { fuzzy }, true);

      return new MaterialResolution(Array.Empty<String>(), false);
    }

    /// <summary>
    /// Resolve the material authored for a model from the dynamic category's
    /// index.xml. This is the legacy/on-disk equivalent of appModelMaterialList:
    /// the Material filename is the default (key 0), while MaterialOverride
    /// entries address individual submesh indexes. It is especially important
    /// for GR2s whose embedded names are merely default/defaultMirror.
    /// </summary>
    private static Boolean TryResolveMaterialsFromDynamicIndex(
      Assets assets,
      String modelPath,
      out Dictionary<Int32, String> materialMap,
      out Int32 skinMaterialIndex,
      out String diagnostic
    ) {
      materialMap = new Dictionary<Int32, String>();
      skinMaterialIndex = -1;
      diagnostic = null;
      if (assets == null || String.IsNullOrWhiteSpace(modelPath)) return false;

      String normalizedModel = NormalizeDynamicIndexPath(modelPath);
      const String dynamicPrefix = "art/dynamic/";
      if (!normalizedModel.StartsWith(dynamicPrefix, StringComparison.OrdinalIgnoreCase))
        return false;

      String remainder = normalizedModel.Substring(dynamicPrefix.Length);
      Int32 slash = remainder.IndexOf('/');
      if (slash <= 0) return false;
      String category = remainder.Substring(0, slash);
      String indexPath = "/resources/art/dynamic/" + category + "/index.xml";

      using TorArchive.File indexFile = assets.FindFile(indexPath);
      if (indexFile == null) return false;

      try {
        XmlDocument document = new XmlDocument();
        using (Stream stream = indexFile.OpenCopyInMemory())
          document.Load(stream);

        XmlNode matchedAsset = null;
        foreach (XmlNode asset in document.SelectNodes("//Asset")) {
          XmlNode baseFile = asset.SelectSingleNode("BaseFile");
          if (baseFile != null
              && String.Equals(
                NormalizeDynamicIndexPath(baseFile.InnerText),
                normalizedModel,
                StringComparison.OrdinalIgnoreCase
              )) {
            matchedAsset = asset;
            break;
          }

          foreach (XmlNode attachment in asset.SelectNodes(".//Attachment")) {
            String filename = attachment.Attributes?["filename"]?.Value;
            if (String.Equals(
                  NormalizeDynamicIndexPath(filename),
                  normalizedModel,
                  StringComparison.OrdinalIgnoreCase
                )) {
              matchedAsset = asset;
              break;
            }
          }
          if (matchedAsset != null) break;
        }

        if (matchedAsset == null) return false;

        XmlNodeList materialNodes = matchedAsset.SelectNodes("./Materials/Material");
        if (materialNodes == null || materialNodes.Count == 0) return false;

        // A legacy asset can carry several appearance material sets. With no
        // NPP material-id available here, id=0/default is the correct preview
        // choice; otherwise use the first authored set just like the old
        // default appearance path.
        XmlNode selected = null;
        foreach (XmlNode candidate in materialNodes) {
          String id = candidate.Attributes?["id"]?.Value;
          if (id == "0" || String.Equals(id, "default", StringComparison.OrdinalIgnoreCase)) {
            selected = candidate;
            break;
          }
        }
        selected ??= materialNodes[0];

        String defaultFile = selected.Attributes?["filename"]?.Value;
        String defaultMaterial = NormalizeMaterialName(defaultFile);
        if (!String.IsNullOrWhiteSpace(defaultMaterial))
          materialMap[0] = defaultMaterial;

        foreach (XmlNode materialOverride in selected.SelectNodes(".//MaterialOverride")) {
          if (!Int32.TryParse(
                materialOverride.Attributes?["index"]?.Value,
                out Int32 submeshIndex
              )) continue;

          String material = NormalizeMaterialName(
            materialOverride.Attributes?["filename"]?.Value
          );
          if (!String.IsNullOrWhiteSpace(material))
            materialMap[submeshIndex] = material;
        }

        XmlNode skinNode = matchedAsset.SelectSingleNode("SkinMaterialIndex");
        if (skinNode != null
            && Int32.TryParse(skinNode.InnerText, out Int32 parsedSkinIndex))
          skinMaterialIndex = parsedSkinIndex;

        // Do not return a map that points only at exporter placeholders. It is
        // no better than the GR2 and would reproduce the white/grey model.
        foreach (Int32 key in materialMap.Keys.ToList()) {
          String material = materialMap[key];
          if (IsExporterPlaceholderMaterial(material)
              || !MaterialExists(assets, null, material))
            materialMap.Remove(key);
        }

        if (materialMap.Count == 0) return false;
        diagnostic = "index.xml materials";
        return true;
      }
      catch (Exception ex) {
        System.Diagnostics.Debug.WriteLine(
          "JBA dynamic index material resolve failed for " + modelPath + ": " + ex
        );
        materialMap.Clear();
        skinMaterialIndex = -1;
        return false;
      }
    }

    private static String NormalizeDynamicIndexPath(String path) {
      if (String.IsNullOrWhiteSpace(path)) return String.Empty;
      String value = path.Trim().Replace('\\', '/');
      while (value.Contains("//")) value = value.Replace("//", "/");
      if (value.StartsWith("/resources/", StringComparison.OrdinalIgnoreCase))
        value = value.Substring("/resources/".Length);
      else if (value.StartsWith("resources/", StringComparison.OrdinalIgnoreCase))
        value = value.Substring("resources/".Length);
      else if (value.StartsWith("/"))
        value = value.Substring(1);
      return value.ToLowerInvariant();
    }

    private static Boolean IsExporterPlaceholderMaterial(String material) {
      String name = NormalizeMaterialName(material);
      return name == "default"
        || name == "defaultmirror"
        || name == "all_test_grey_128"
        || name == "all_test_gray_128"
        || name == "all_test_white_128";
    }

    private static String FindBestMaterial(
      JBAAppearanceIndex index,
      String target,
      Assets assets
    ) {
      if (index == null || String.IsNullOrWhiteSpace(target))
        return null;

      String cleanTarget = Path.GetFileNameWithoutExtension(target.Trim());
      String best = null;
      Int32 bestScore = 0;

      foreach (String material in index.MaterialNames) {
        Int32 score = ScoreMaterial(cleanTarget, material);
        if (score > bestScore) {
          bestScore = score;
          best = material;
        }
      }

      if (bestScore < 700 || !MaterialExists(assets, index, best))
        return null;
      return best;
    }

    private static Boolean MaterialExists(
      Assets assets,
      JBAAppearanceIndex index,
      String material
    ) {
      if (String.IsNullOrWhiteSpace(material)) return false;
      material = Path.GetFileNameWithoutExtension(material.Trim());

      if (index?.MaterialNameSet.Contains(material) == true)
        return true;

      return assets?.FindFile(
        "/resources/art/shaders/materials/" + material + ".mat"
      ) != null;
    }

    private static Dictionary<String, String> ParseMag(String text) {
      var values = new Dictionary<String, String>(StringComparer.OrdinalIgnoreCase);
      using (StringReader reader = new StringReader(text ?? String.Empty)) {
        String line;
        while ((line = reader.ReadLine()) != null) {
          line = line.Trim();
          if (line.Length == 0 || line == "!" || line.StartsWith("["))
            continue;

          Int32 equals = line.IndexOf('=');
          if (equals <= 0) continue;

          String key = line.Substring(0, equals).Trim();
          String value = line.Substring(equals + 1).Trim();
          if (!values.ContainsKey(key)) values.Add(key, value);
        }
      }
      return values;
    }

    private static Int32 ScoreMaterial(String target, String material) {
      String t = NormalizeIdentifier(target);
      String m = NormalizeIdentifier(material);
      if (t.Length == 0 || m.Length == 0) return 0;
      if (m == t) return 5000;
      if (m.StartsWith(t, StringComparison.Ordinal)) return 3900;
      if (m.Contains(t, StringComparison.Ordinal)) return 3000;

      Int32 prefix = CommonPrefix(t, m);
      Int32 score = prefix * 45;
      foreach (String key in NameKeys(target)) {
        String k = NormalizeIdentifier(key);
        if (k.Length >= 3 && m.Contains(k, StringComparison.Ordinal))
          score += 180 + k.Length * 12;
      }
      return score;
    }

    private static Int32 ScoreName(String bodyType, String candidateStem) {
      String target = NormalizeIdentifier(bodyType);
      String stem = NormalizeIdentifier(candidateStem);
      if (target.Length == 0 || stem.Length == 0) return 0;

      if (stem == target) return 5000;

      Int32 score = 0;
      if (stem.StartsWith(target, StringComparison.Ordinal)) score += 2200;
      else if (stem.Contains(target, StringComparison.Ordinal)) score += 1700;

      String rawStem = (candidateStem ?? String.Empty).ToLowerInvariant();
      foreach (String keyRaw in NameKeys(bodyType)) {
        String key = NormalizeIdentifier(keyRaw);
        if (key.Length < 3) continue;
        if (stem == key) score += 1400;
        else if (stem.StartsWith(key, StringComparison.Ordinal)) score += 900;
        else if (stem.Contains(key, StringComparison.Ordinal)) score += 520;

        String rawKey = keyRaw.ToLowerInvariant();
        if (rawKey.Length >= 3 && rawStem.StartsWith(rawKey + "_", StringComparison.Ordinal))
          score += 120;
        if (rawKey.Length >= 3 && rawStem.StartsWith(rawKey + "_" + rawKey + "_", StringComparison.Ordinal))
          score += 360;
      }

      Int32 prefix = CommonPrefix(target, stem);
      score += prefix * 35;

      // Folder and model names often use different suffixes (basiliskbuddy ->
      // dog_basiliskcompanion, petfightership -> veh_mini_rep_fighter). A long
      // shared substring recovers those without a per-body mapping. Ignore
      // very short matches such as cat/dog because they are too generic.
      Int32 substring = LongestCommonSubstring(target, stem);
      if (substring >= 5) score += substring * 95;

      // Prefer the simplest/default-looking skin when several models share a
      // rig. This makes foo_foo_a01 win over raid/boss/prototype variants and
      // keeps ordinary short names ahead of cinematic alternates.
      score += Math.Max(0, 150 - Math.Min(150, rawStem.Length * 5));
      if (rawStem.EndsWith("_a01", StringComparison.Ordinal)) score += 80;
      if (rawStem.Contains("_boss", StringComparison.Ordinal)
          || rawStem.Contains("_raid", StringComparison.Ordinal)
          || rawStem.Contains("_prototype", StringComparison.Ordinal)
          || rawStem.Contains("_cine", StringComparison.Ordinal))
        score -= 120;

      return Math.Max(0, score);
    }

    private static Int32 LongestCommonSubstring(String a, String b) {
      if (String.IsNullOrEmpty(a) || String.IsNullOrEmpty(b)) return 0;
      Int32[] previous = new Int32[b.Length + 1];
      Int32 best = 0;
      for (Int32 i = 1; i <= a.Length; i++) {
        Int32[] current = new Int32[b.Length + 1];
        for (Int32 j = 1; j <= b.Length; j++) {
          if (a[i - 1] == b[j - 1]) {
            current[j] = previous[j - 1] + 1;
            if (current[j] > best) best = current[j];
          }
        }
        previous = current;
      }
      return best;
    }

    private static IEnumerable<String> NameKeys(String bodyType) {
      var keys = new HashSet<String>(StringComparer.OrdinalIgnoreCase);
      String raw = (bodyType ?? String.Empty).ToLowerInvariant();
      if (raw.Length == 0) return keys;

      keys.Add(raw);
      foreach (String token in raw.Split(new[] { '_', '-', '.' }, StringSplitOptions.RemoveEmptyEntries))
        keys.Add(token);

      if (raw.StartsWith("pet", StringComparison.Ordinal) && raw.Length > 3) {
        keys.Add("pet");
        keys.Add(raw.Substring(3));
      }
      if (raw.EndsWith("boss", StringComparison.Ordinal) && raw.Length > 4)
        keys.Add(raw.Substring(0, raw.Length - 4));
      if (raw.EndsWith("cine", StringComparison.Ordinal) && raw.Length > 4)
        keys.Add(raw.Substring(0, raw.Length - 4));
      if (raw.EndsWith("droid", StringComparison.Ordinal) && raw.Length > 5) {
        keys.Add(raw.Substring(0, raw.Length - 5));
        keys.Add("droid");
      }
      if (raw.EndsWith("sit", StringComparison.Ordinal) && raw.Length > 3)
        keys.Add(raw.Substring(0, raw.Length - 3));

      return keys;
    }

    private static Int32 CommonPrefix(String a, String b) {
      Int32 count = Math.Min(a.Length, b.Length);
      Int32 i = 0;
      while (i < count && a[i] == b[i]) i++;
      return i;
    }

    private static String NormalizeIdentifier(String value) {
      if (String.IsNullOrWhiteSpace(value)) return String.Empty;
      var sb = new StringBuilder(value.Length);
      foreach (Char c in value.ToLowerInvariant()) {
        if (Char.IsLetterOrDigit(c)) sb.Append(c);
      }
      return sb.ToString();
    }

    private static String CanonicalBoneName(String name) {
      if (String.IsNullOrWhiteSpace(name)) return String.Empty;
      name = name.Trim();
      Int32 colon = name.LastIndexOf(':');
      if (colon >= 0 && colon + 1 < name.Length)
        name = name.Substring(colon + 1);
      return name;
    }

    private static String NormalizeResourcePath(String raw) {
      String path = (raw ?? String.Empty).Replace('\\', '/');
      const String namedPrefix = "/root/named";
      if (path.StartsWith(namedPrefix, StringComparison.OrdinalIgnoreCase))
        path = path.Substring(namedPrefix.Length);

      while (path.Contains("//")) path = path.Replace("//", "/");
      if (path.Length > 0 && path[0] != '/') path = "/" + path;
      return path;
    }

    private static Boolean IsHumanoidBodyType(String bodyType) =>
      bodyType is "bfanew" or "bfbnew" or "bfnnew" or "bfsnew"
        or "bmanew" or "bmfnew" or "bmnnew" or "bmsnew";

    private static List<JBAAppearancePart> One(String model, params String[] mats) =>
      new List<JBAAppearancePart> { new JBAAppearancePart(model, mats) };

    private static List<JBAAppearancePart> One(JBAAppearancePart part) =>
      new List<JBAAppearancePart> { part };

    private sealed class ModelCandidate {
      internal String Path;
      internal Int32 Lexical;
      internal Int32 Final;
      internal Double BoneRatio;
    }

    private readonly struct MaterialResolution {
      internal readonly String[] Materials;
      internal readonly Boolean BySubmesh;

      internal MaterialResolution(String[] materials, Boolean bySubmesh) {
        Materials = materials ?? Array.Empty<String>();
        BySubmesh = bySubmesh;
      }
    }

    /// <summary>
    /// Loads only the authored skeleton/bounds from the first resolved appearance
    /// model that actually carries a GR2 skeleton. This is intentionally used for
    /// placeables and body types whose *_skeleton.gr2 cannot be resolved: their
    /// own mesh is a safer source than the old bmnnew fallback, and its bone names
    /// are exactly the names the mesh palettes use.
    /// </summary>
    internal static GR2 LoadSkeletonFromAppearance(
      Assets assets,
      IEnumerable<JBAAppearancePart> parts,
      out String modelPath
    ) {
      modelPath = null;
      if (assets == null)
        return null;

      foreach (JBAAppearancePart part in parts ?? Enumerable.Empty<JBAAppearancePart>()) {
        if (String.IsNullOrWhiteSpace(part?.Model))
          continue;

        TorArchive.File file = assets.FindFile(part.Model);
        if (file == null)
          continue;

        try {
          GR2 model;
          using (Stream stream = file.OpenCopyInMemory())
          using (BinaryReader br = new BinaryReader(stream))
            model = new GR2(br, Path.GetFileName(part.Model));

          if (model.skeleton_bones == null || model.skeleton_bones.Count == 0) {
            model.Dispose();
            continue;
          }

          GR2 skeleton = new GR2 {
            filename = "jba_appearance_skeleton_" + Path.GetFileNameWithoutExtension(part.Model),
            transformMatrix = SlimDX.Matrix.Identity,
            scaleMatrix = SlimDX.Matrix.Identity,
            rotationMatrix = SlimDX.Matrix.Identity,
            positionMatrix = SlimDX.Matrix.Identity,
            parentPosMatrix = SlimDX.Matrix.Identity,
            parentRotMatrix = SlimDX.Matrix.Identity,
            numBones = model.numBones,
            globalBox = model.globalBox
          };

          // Bone records are immutable for the preview path; copying the object
          // references is enough and lets the temporary mesh GR2 be disposed.
          skeleton.skeleton_bones.AddRange(model.skeleton_bones);
          modelPath = part.Model;
          model.Dispose();
          return skeleton;
        }
        catch (Exception ex) {
          System.Diagnostics.Debug.WriteLine(
            "JBA appearance-skeleton load failed for " + part.Model + ": " + ex.Message
          );
        }
      }

      return null;
    }

    internal static GR2 LoadComposite(
      Assets assets,
      GR2 skeleton,
      IEnumerable<JBAAppearancePart> parts,
      String name
    ) {
      if (assets == null || skeleton == null)
        return skeleton;

      GR2 composite = new GR2 {
        filename = name ?? "jba_preview",
        transformMatrix = SlimDX.Matrix.Identity,
        scaleMatrix = SlimDX.Matrix.Identity,
        rotationMatrix = SlimDX.Matrix.Identity,
        positionMatrix = SlimDX.Matrix.Identity,
        parentPosMatrix = SlimDX.Matrix.Identity,
        parentRotMatrix = SlimDX.Matrix.Identity
      };

      composite.skeleton_bones.AddRange(skeleton.skeleton_bones);
      composite.numBones = skeleton.numBones;
      composite.globalBox = skeleton.globalBox;

      Boolean gotMesh = false;

      foreach (JBAAppearancePart part in parts ?? Enumerable.Empty<JBAAppearancePart>()) {
        TorArchive.File file = assets.FindFile(part.Model);
        if (file == null) continue;

        GR2 model;
        using (Stream stream = file.OpenCopyInMemory())
        using (BinaryReader br = new BinaryReader(stream))
          model = new GR2(br, Path.GetFileName(part.Model));

        if (model.meshes.Count == 0) {
          model.Dispose();
          continue;
        }

        Boolean useMaterialMap = part.MaterialMap != null
          && part.MaterialMap.Count > 0;
        Boolean useAppearanceMaterials = part.Materials.Length > 0;
        Dictionary<Int32, Int32> materialMapToLocal = null;

        Int32 skinMaterialLocal = -1;

        if (useMaterialMap) {
          model.materials.Clear();
          materialMapToLocal = new Dictionary<Int32, Int32>();
          var localByName = new Dictionary<String, Int32>(
            StringComparer.OrdinalIgnoreCase
          );

          foreach (KeyValuePair<Int32, String> entry in part.MaterialMap) {
            String materialName = NormalizeMaterialName(entry.Value);
            if (String.IsNullOrWhiteSpace(materialName)) continue;
            if (!localByName.TryGetValue(materialName, out Int32 local)) {
              local = model.materials.Count;
              localByName.Add(materialName, local);
              model.materials.Add(new GR2_Material(materialName));
            }
            materialMapToLocal[entry.Key] = local;
          }

          String skinName = NormalizeMaterialName(part.SkinMaterial);
          if (!String.IsNullOrWhiteSpace(skinName)) {
            if (!localByName.TryGetValue(skinName, out skinMaterialLocal)) {
              skinMaterialLocal = model.materials.Count;
              localByName.Add(skinName, skinMaterialLocal);
              model.materials.Add(new GR2_Material(skinName));
            }
          }

          model.numMaterials = (ushort)model.materials.Count;
        }
        Dictionary<Int32, Int32> submeshAppearanceMaterialToLocal = null;
        if (useAppearanceMaterials && part.MaterialsBySubmesh) {
          // Jedipedia's array form is positional, not a "last material wins"
          // fallback: mats[0] overrides submesh 0, mats[1] submesh 1, and any
          // later submesh that is not explicitly named keeps the GR2's own
          // material slot. This matters for multi-part creature meshes such as
          // the Ithorian, where forcing the eye material onto every later
          // submesh makes geometry appear missing/wrong.
          submeshAppearanceMaterialToLocal = new Dictionary<Int32, Int32>();
          for (Int32 materialIndex = 0; materialIndex < part.Materials.Length; materialIndex++) {
            String materialName = NormalizeMaterialName(part.Materials[materialIndex]);
            if (String.IsNullOrWhiteSpace(materialName)) continue;
            Int32 local = model.materials.Count;
            model.materials.Add(new GR2_Material(materialName));
            submeshAppearanceMaterialToLocal[materialIndex] = local;
          }
          model.numMaterials = (ushort)model.materials.Count;
        }
        else if (useAppearanceMaterials) {
          model.materials.Clear();
          foreach (String material in part.Materials) {
            String materialName = NormalizeMaterialName(material);
            if (!String.IsNullOrWhiteSpace(materialName))
              model.materials.Add(new GR2_Material(materialName));
          }
          model.numMaterials = (ushort)model.materials.Count;
        }

        Int32 materialBase = composite.materials.Count;
        composite.materials.AddRange(model.materials);

        foreach (GR2_Mesh mesh in model.meshes) {
          for (Int32 pieceIndex = 0; pieceIndex < mesh.meshPieces.Count; pieceIndex++) {
            GR2_Mesh_Piece piece = mesh.meshPieces[pieceIndex];

            if (useMaterialMap && model.materials.Count > 0) {
              Int32 local;

              // appModelSkinMaterialIndex is authoritative for exposed skin.
              // It must beat the ordinary material map, matching Jedipedia's
              // nppMaterialForSubmesh()/skinMaterial handling.
              if (pieceIndex == part.SkinMaterialIndex
                  && skinMaterialLocal >= 0) {
                local = skinMaterialLocal;
              }
              else if (!materialMapToLocal.TryGetValue(pieceIndex, out local)
                       && !materialMapToLocal.TryGetValue(-1, out local)
                       && !materialMapToLocal.TryGetValue(0, out local)) {
                local = 0;
              }

              piece.matId = materialBase + local;
            }
            else if (useAppearanceMaterials && model.materials.Count > 0) {
              Int32 local;
              if (part.MaterialsBySubmesh) {
                // Positional appearance arrays only override explicitly named
                // submeshes. Unnamed pieces keep their original embedded GR2
                // material, exactly like file_gr2_appearanceMaterial() in
                // Jedipedia.
                if (submeshAppearanceMaterialToLocal != null
                    && submeshAppearanceMaterialToLocal.TryGetValue(pieceIndex, out local)) {
                  piece.matId = materialBase + local;
                }
                else if (piece.matId >= 0 && piece.matId < model.materials.Count) {
                  piece.matId = materialBase + piece.matId;
                }
                else {
                  piece.matId = -1;
                }
                continue;
              } else {
                // Auto-resolved embedded material replacements preserve the
                // GR2's authored matId mapping instead of re-numbering pieces.
                local = piece.matId >= 0
                  ? Math.Min(piece.matId, model.materials.Count - 1)
                  : 0;
              }
              piece.matId = materialBase + local;
            }
            else if (piece.matId >= 0 && model.materials.Count > 0) {
              Int32 local = Math.Min(piece.matId, model.materials.Count - 1);
              piece.matId = materialBase + local;
            }
            else if (piece.matId >= 0) {
              piece.matId = -1;
            }
          }

          composite.meshes.Add(mesh);
        }

        composite.numMeshes += model.numMeshes;
        composite.attachedModels.Add(model);
        gotMesh = true;
      }

      if (!gotMesh) {
        composite.Dispose();
        return skeleton;
      }

      composite.numMaterials = (ushort)composite.materials.Count;
      composite.numMeshes = (ushort)composite.meshes.Count;

      return composite;
    }
  }
}
