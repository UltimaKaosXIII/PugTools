using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using DrawingColor = System.Drawing.Color;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using DevIL;
using GomLib;
using GomLib.Models;

namespace PugTools {
  public partial class WorldBrowser {
    private sealed class WorldCodexPreviewData {
      public ulong Id;
      public string Fqn;
      public string Name;
      public string Description;
      public string Category;
      public string Image;
      public int Level;
      public string Faction;
      public bool Hidden;
      public string LoadWarning;
    }

    private Form worldCodexPreviewForm;
    private Label worldCodexPreviewHeader;
    private PictureBox worldCodexPreviewImage;
    private RichTextBox worldCodexPreviewText;
    private Button worldSelectionCodexButton;
    private readonly Dictionary<string, ulong> worldLoreCodexFqnCache = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
    // Exact STB retriever identity is language-independent. Index it alongside visible titles so a German PLC can
    // resolve its codex even when the translated placeable title and translated codex title differ in punctuation or
    // wording. This is also much cheaper/more deterministic than fuzzy text matching.
    private readonly Dictionary<string, ulong> worldLoreCodexRetrieverCache = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
    private DataObjectModel worldLoreCodexCacheDom;

    private bool CanOpenWorldCodex(WorldInteractionInfo interaction) {
      return interaction != null && interaction.Kind == WorldInteractionKind.Codex && interaction.CodexId != 0;
    }

    private bool CanOpenSelectedWorldCodex() {
      return CanOpenWorldCodex(panelRender?.SelectedWorldInteraction);
    }

    private void OpenSelectedWorldCodexPreview() {
      OpenWorldCodexPreview(panelRender?.SelectedWorldInteraction);
    }

    // Some old tutorial/lore PLCs are authored as glowing knowledge objects without a usable plcCodexSpec on the
    // modern Placeable model. Keep the normal interaction classifier authoritative, but when the user actually clicks
    // an otherwise-unclassified glowing PLC, resolve the codex lazily from its raw node or (as a last resort) the
    // unique cdx.* FQN leaf. This avoids scanning/loading every codex while an area is being built.
    private bool TryOpenWorldLoreCodexForInteractionTarget() {
      WorldSpnPlacement spn = panelRender?.InteractionTargetWorldSpnPlacement;
      if (spn == null || currentDom == null) return false;

      ulong codexId = 0;
      object rawNameRetriever = null;
      try {
        GomObject plcNode = !String.IsNullOrWhiteSpace(spn.SourceFqn) ? currentDom.GetObject(spn.SourceFqn) : null;
        codexId = WorldInteractionUnsigned(WorldInteractionDataValue(plcNode?.Data, "plcCodexSpec", "4611686062140131223"));
        object rawLocMap = WorldInteractionDataValue(plcNode?.Data, "locTextRetrieverMap", "4611686102842470023");
        rawNameRetriever = WorldCodexDictionaryValue(rawLocMap, 15685385242400905286UL);
      } catch { }
      bool loreShape = WorldLooksLikeLoreObject(spn.Name, spn.SourceFqn);
      if (codexId == 0 && !loreShape) return false;
      if (codexId == 0 && !String.IsNullOrWhiteSpace(spn.SourceFqn)) {
        try { codexId = currentDom.PlaceableLoader.Load(spn.SourceFqn)?.CodexId ?? 0; } catch { }
      }

      // English already resolves this generation of tutorial objects by title. When the UI is de-de the visible
      // PLC title is translated independently from the codex title and can no longer be text-equal. Reuse every
      // localized form carried by the PLC's own name retriever (English first) and feed that through the same proven
      // codex-title index. This keeps the click language-independent without hard-coding German words.
      if (codexId == 0) codexId = ResolveWorldLoreCodexByLocalizedPlaceableName(rawNameRetriever, spn.SourceFqn);
      if (codexId == 0) codexId = ResolveWorldLoreCodexBySourceFqn(spn.SourceFqn);
      if (codexId == 0) codexId = ResolveWorldLoreCodexByName(spn.Name, spn.SourceFqn);
      if (codexId == 0) {
        SetStatusLabel("Knowledge/lore object recognized, but no unique codex entry could be resolved.");
        return false;
      }

      var interaction = new WorldInteractionInfo { Kind = WorldInteractionKind.Codex, CodexId = codexId };
      spn.Interaction = interaction; // Cache the successful lazy resolution for subsequent clicks/inspection.
      OpenWorldCodexPreview(interaction);
      return true;
    }

    private static bool WorldLooksLikeLoreObject(string name, string fqn) {
      string text = ((name ?? String.Empty) + " " + (fqn ?? String.Empty)).ToLowerInvariant();
      return text.Contains("lore") || text.Contains("codex") || text.Contains("knowledge") || text.Contains("tutorial");
    }

    private ulong ResolveWorldLoreCodexByRetriever(object retriever) {
      EnsureWorldLoreCodexFqnCache();
      string key = WorldLoreCodexRetrieverKey(retriever);
      return !String.IsNullOrWhiteSpace(key) && worldLoreCodexRetrieverCache.TryGetValue(key, out ulong id) ? id : 0;
    }

    private ulong ResolveWorldLoreCodexByLocalizedPlaceableName(object retriever, string sourceFqn) {
      if (currentDom == null) return 0;
      Dictionary<string, string> localized = WorldLocAll(retriever);
      if (localized == null || localized.Count == 0) return 0;

      // en-us is intentionally first: it is the path known to resolve these objects in the original viewer. The
      // retriever identity is the same regardless of the currently selected locale, so a de-de click can safely use
      // that stable English synonym to find the same cdx node; the preview itself is still rendered in de-de.
      foreach (string key in new[] { "enMale", "enFemale", "deMale", "deFemale", "frMale", "frFemale" }) {
        if (!localized.TryGetValue(key, out string title) || String.IsNullOrWhiteSpace(title)) continue;
        ulong id = ResolveWorldLoreCodexByName(title, null);
        if (id != 0) return id;
      }
      foreach (string title in localized.Values.Where(value => !String.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase)) {
        ulong id = ResolveWorldLoreCodexByName(title, null);
        if (id != 0) return id;
      }
      return 0;
    }

    // Old tutorial/lore placeables often do not carry plcCodexSpec at all. Their PLC FQN still encodes the
    // tutorial topic, however, and that part is deliberately language independent. Example from Hutta:
    //   plc.tutorial.item_modification.imperial.item_mod_lore_object
    // maps to cdx.game_rules.tutorials.item_modifications. Resolve that structural relation before localized-title
    // matching so de-de behaves exactly like en-us instead of depending on the translated visible name.
    private ulong ResolveWorldLoreCodexBySourceFqn(string sourceFqn) {
      if (currentDom == null || String.IsNullOrWhiteSpace(sourceFqn)) return 0;
      string source = sourceFqn.Trim().ToLowerInvariant();
      const string tutorialPrefix = "plc.tutorial.";
      if (!source.StartsWith(tutorialPrefix, StringComparison.OrdinalIgnoreCase)) return 0;

      string remainder = source.Substring(tutorialPrefix.Length);
      string[] parts = remainder.Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries);
      if (parts.Length == 0) return 0;
      // The first segment is the tutorial subject; following segments normally select faction/variant/model.
      string topic = parts[0].Trim('_');
      if (topic.Length == 0) return 0;

      var candidates = new List<string>();
      void AddCandidate(string leaf) {
        if (String.IsNullOrWhiteSpace(leaf)) return;
        string fqn = "cdx.game_rules.tutorials." + leaf.Trim().ToLowerInvariant();
        if (!candidates.Contains(fqn, StringComparer.OrdinalIgnoreCase)) candidates.Add(fqn);
      }
      AddCandidate(topic);
      if (topic.EndsWith("y", StringComparison.OrdinalIgnoreCase) && topic.Length > 1)
        AddCandidate(topic.Substring(0, topic.Length - 1) + "ies");
      else if (topic.EndsWith("s", StringComparison.OrdinalIgnoreCase)) AddCandidate(topic.Substring(0, topic.Length - 1));
      else AddCandidate(topic + "s");

      foreach (string fqn in candidates) {
        // GetObjectId is an index-only lookup. It cannot be derailed by a sparse selected localization and is exactly
        // what we need here: the cdx node's stable identity, not its translated model yet.
        try { ulong directId = currentDom.GetObjectId(fqn); if (directId != 0) return directId; } catch { }
      }

      // A few releases renamed the codex leaf without changing the PLC topic. Compare normalized tutorial leaves as
      // a final structural fallback, but require a unique match so a broad token can never open the wrong entry.
      string wanted = WorldLoreCodexCompactKey(topic);
      if (String.IsNullOrWhiteSpace(wanted)) return 0;
      var matches = new List<ulong>();
      try {
        foreach (GomObject node in currentDom.GetObjectsStartingWith("cdx.game_rules.tutorials.")) {
          if (node == null || node.Id == 0 || String.IsNullOrWhiteSpace(node.Name)) continue;
          string leaf = node.Name.Substring(node.Name.LastIndexOf('.') + 1);
          string compact = WorldLoreCodexCompactKey(leaf);
          if (String.Equals(compact, wanted, StringComparison.OrdinalIgnoreCase) ||
              String.Equals(compact, wanted + "s", StringComparison.OrdinalIgnoreCase) ||
              String.Equals(compact + "s", wanted, StringComparison.OrdinalIgnoreCase)) matches.Add(node.Id);
        }
      } catch { }
      List<ulong> uniqueTutorial = matches.Distinct().Take(2).ToList();
      if (uniqueTutorial.Count == 1) return uniqueTutorial[0];

      // RED/beta branches do not all file these nodes below cdx.game_rules.tutorials. The PLC topic is still stable,
      // so score cdx FQNs without loading their localized payload. Requiring a unique best score avoids ever opening a
      // merely similar codex. This is particularly useful for plc.tutorial.item_modification.* in de-de.
      string[] topicTokens = topic.Split(new[] { '_' }, StringSplitOptions.RemoveEmptyEntries)
        .Select(WorldLoreCodexCompactKey).Where(x => !String.IsNullOrWhiteSpace(x)).ToArray();
      int bestScore = 0;
      ulong bestId = 0;
      bool tied = false;
      try {
        foreach (GomObject node in currentDom.GetObjectsStartingWith("cdx.")) {
          if (node == null || node.Id == 0 || String.IsNullOrWhiteSpace(node.Name)) continue;
          string fqn = node.Name.ToLowerInvariant();
          string leaf = fqn.Substring(fqn.LastIndexOf('.') + 1);
          string compactLeaf = WorldLoreCodexCompactKey(leaf);
          string compactFqn = WorldLoreCodexCompactKey(fqn);
          int score = 0;
          if (String.Equals(compactLeaf, wanted, StringComparison.OrdinalIgnoreCase)) score += 120;
          if (String.Equals(compactLeaf, wanted + "s", StringComparison.OrdinalIgnoreCase) ||
              String.Equals(compactLeaf + "s", wanted, StringComparison.OrdinalIgnoreCase)) score += 110;
          if (fqn.Contains("tutorial")) score += 30;
          int tokenHits = 0;
          foreach (string token in topicTokens) if (!String.IsNullOrWhiteSpace(token) && compactFqn.Contains(token)) tokenHits++;
          if (topicTokens.Length > 0 && tokenHits == topicTokens.Length) score += 20 + tokenHits * 8;
          if (score < 40) continue;
          if (score > bestScore) { bestScore = score; bestId = node.Id; tied = false; }
          else if (score == bestScore && bestId != node.Id) tied = true;
        }
      } catch { }
      return bestScore > 0 && !tied ? bestId : 0;
    }

    private ulong ResolveWorldLoreCodexByName(string displayName, string sourceFqn) {
      EnsureWorldLoreCodexFqnCache();
      var keys = new List<string>();
      string displayKey = WorldLoreCodexKey(displayName);
      if (!String.IsNullOrWhiteSpace(displayKey)) keys.Add(displayKey);
      string sourceLeaf = sourceFqn;
      if (!String.IsNullOrWhiteSpace(sourceLeaf)) {
        int dot = sourceLeaf.LastIndexOf('.');
        if (dot >= 0 && dot + 1 < sourceLeaf.Length) sourceLeaf = sourceLeaf.Substring(dot + 1);
        string sourceKey = WorldLoreCodexKey(sourceLeaf.Replace('_', ' '));
        if (!String.IsNullOrWhiteSpace(sourceKey) && !keys.Contains(sourceKey, StringComparer.OrdinalIgnoreCase)) keys.Add(sourceKey);
      }
      foreach (string key in keys) {
        if (worldLoreCodexFqnCache.TryGetValue(key, out ulong id) && id != 0) return id;
        string compact = WorldLoreCodexCompactKey(key);
        if (!String.IsNullOrWhiteSpace(compact) && worldLoreCodexFqnCache.TryGetValue(compact, out id) && id != 0) return id;
      }

      // German tutorial labels often differ from the codex title only by inflection, for example
      // "Gegenstands-Modifikation" vs. "Gegenstandsmodifikationen". English happened to be an exact title match,
      // which hid this bug. Compare the language-independent punctuation-free forms by prefix as a LAST fallback and
      // require one unique codex id; this accepts plural/case suffixes without fuzzy-opening an unrelated entry.
      foreach (string rawKey in keys) {
        string wanted = WorldLoreCodexCompactKey(rawKey);
        if (String.IsNullOrWhiteSpace(wanted) || wanted.Length < 8) continue;
        ulong candidateId = 0;
        bool ambiguous = false;
        foreach (KeyValuePair<string, ulong> entry in worldLoreCodexFqnCache) {
          if (entry.Value == 0) continue;
          string candidate = WorldLoreCodexCompactKey(entry.Key);
          if (String.IsNullOrWhiteSpace(candidate) || candidate.Length < 8) continue;
          int shorter = Math.Min(wanted.Length, candidate.Length);
          if (Math.Abs(wanted.Length - candidate.Length) > 5 || shorter < 8) continue;
          if (!wanted.StartsWith(candidate, StringComparison.OrdinalIgnoreCase) &&
              !candidate.StartsWith(wanted, StringComparison.OrdinalIgnoreCase)) continue;
          if (candidateId == 0) candidateId = entry.Value;
          else if (candidateId != entry.Value) { ambiguous = true; break; }
        }
        if (candidateId != 0 && !ambiguous) return candidateId;
      }
      return 0;
    }

    private void EnsureWorldLoreCodexFqnCache() {
      if (ReferenceEquals(worldLoreCodexCacheDom, currentDom)) return;
      worldLoreCodexCacheDom = currentDom;
      worldLoreCodexFqnCache.Clear();
      worldLoreCodexRetrieverCache.Clear();
      if (currentDom == null) return;
      var duplicateKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      var duplicateRetrievers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      try {
        foreach (GomObject node in currentDom.GetObjectsStartingWith("cdx.")) {
          if (node == null || node.Id == 0 || String.IsNullOrWhiteSpace(node.Name)) continue;

          // The FQN leaf is language independent and remains useful for old/beta objects whose PLC carries no
          // plcCodexSpec. Do not stop there, though: spawned knowledge objects commonly use the LOCALIZED codex
          // title as their visible PLC name. Index every language shipped in the string table so de-de/fr-fr resolve
          // exactly like en-us instead of relying on English words such as "knowledge" or "lore".
          string leaf = node.Name;
          int dot = leaf.LastIndexOf('.');
          if (dot >= 0 && dot + 1 < leaf.Length) leaf = leaf.Substring(dot + 1);
          AddWorldLoreCodexCacheKey(leaf.Replace('_', ' '), node.Id, duplicateKeys);

          try {
            object rawMap = WorldInteractionDataValue(node.Data, "locTextRetrieverMap", "4611686102842470023");
            object nameRetriever = WorldCodexDictionaryValue(rawMap, 9583395879878673097UL);
            if (nameRetriever is GomObjectData nameData) {
              AddWorldLoreCodexRetriever(nameData, node.Id, duplicateRetrievers);
              Dictionary<string, string> localized = WorldLocAll(nameData);
              if (localized != null) foreach (string title in localized.Values) AddWorldLoreCodexCacheKey(title, node.Id, duplicateKeys);
            }
          } catch (Exception ex) {
            System.Diagnostics.Debug.WriteLine("Codex localized title index failed for " + node.Name + ": " + ex.Message);
          }
        }
        foreach (string duplicate in duplicateKeys) worldLoreCodexFqnCache.Remove(duplicate);
        foreach (string duplicate in duplicateRetrievers) worldLoreCodexRetrieverCache.Remove(duplicate);
      } catch (Exception ex) {
        System.Diagnostics.Debug.WriteLine("Codex FQN index could not be built: " + ex.Message);
      }
    }


    private void AddWorldLoreCodexRetriever(object retriever, ulong id, HashSet<string> duplicateRetrievers) {
      string key = WorldLoreCodexRetrieverKey(retriever);
      if (String.IsNullOrWhiteSpace(key) || id == 0 || duplicateRetrievers.Contains(key)) return;
      if (worldLoreCodexRetrieverCache.TryGetValue(key, out ulong previous) && previous != id) {
        duplicateRetrievers.Add(key);
        worldLoreCodexRetrieverCache.Remove(key);
      } else worldLoreCodexRetrieverCache[key] = id;
    }

    private static string WorldLoreCodexRetrieverKey(object retriever) {
      return WorldLocRetrieverKey(retriever);
    }

    private void AddWorldLoreCodexCacheKey(string value, ulong id, HashSet<string> duplicateKeys) {
      string key = WorldLoreCodexKey(value);
      AddWorldLoreCodexCacheAlias(key, id, duplicateKeys);
      // German localization alternates freely between compounds and hyphenated spellings (for example
      // Gegenstandsmodifikation vs. Gegenstands-Modifikation). Both are the same visible title to the player, but the
      // old whitespace key considered them unrelated and only en-us knowledge objects remained resolvable.
      AddWorldLoreCodexCacheAlias(WorldLoreCodexCompactKey(key), id, duplicateKeys);
    }

    private void AddWorldLoreCodexCacheAlias(string key, ulong id, HashSet<string> duplicateKeys) {
      if (String.IsNullOrWhiteSpace(key) || id == 0 || duplicateKeys.Contains(key)) return;
      if (worldLoreCodexFqnCache.TryGetValue(key, out ulong previous) && previous != id) {
        duplicateKeys.Add(key);
        worldLoreCodexFqnCache.Remove(key);
      } else worldLoreCodexFqnCache[key] = id;
    }

    private static string WorldLoreCodexCompactKey(string value) {
      string key = WorldLoreCodexKey(value);
      return String.IsNullOrWhiteSpace(key) ? null : key.Replace(" ", String.Empty);
    }

    private static string WorldLoreCodexKey(string value) {
      if (String.IsNullOrWhiteSpace(value)) return null;
      var normalized = new StringBuilder(value.Length);
      foreach (char c in value.ToLowerInvariant()) normalized.Append(Char.IsLetterOrDigit(c) ? c : ' ');
      string[] words = normalized.ToString().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
      for (int i = 0; i < words.Length; i++) {
        // Enough stemming for shipped labels such as "Item Modification" -> cdx....item_modifications, while
        // retaining short words and avoiding a general natural-language stemmer in the world loader.
        if (words[i].Length > 4 && words[i].EndsWith("s", StringComparison.Ordinal)) words[i] = words[i].Substring(0, words[i].Length - 1);
      }
      return String.Join(" ", words);
    }

    private void OpenWorldCodexPreview(WorldInteractionInfo interaction) {
      if (!CanOpenWorldCodex(interaction)) return;
      WorldCodexPreviewData codex = LoadWorldCodexPreviewData(interaction.CodexId, out string error);
      if (codex == null) {
        MessageBox.Show(this, error ?? "The codex entry could not be loaded.", "Codex entry", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return;
      }
      EnsureWorldCodexPreviewForm();

      string title = !String.IsNullOrWhiteSpace(codex.Name) ? codex.Name : !String.IsNullOrWhiteSpace(codex.Fqn) ? codex.Fqn : "Codex entry";
      worldCodexPreviewForm.Text = "Codex — " + title;
      worldCodexPreviewHeader.Text = title + (String.IsNullOrWhiteSpace(codex.Category) ? String.Empty : "\r\n" + codex.Category);

      var details = new System.Text.StringBuilder();
      if (!String.IsNullOrWhiteSpace(codex.Description)) details.AppendLine(codex.Description.Trim());
      if (details.Length > 0) details.AppendLine().AppendLine("────────────────────────────────────────");
      details.AppendLine("Codex id: " + codex.Id.ToString(CultureInfo.InvariantCulture));
      if (!String.IsNullOrWhiteSpace(codex.Fqn)) details.AppendLine("Node: " + codex.Fqn);
      if (!String.IsNullOrWhiteSpace(codex.Category)) details.AppendLine("Category: " + codex.Category);
      if (codex.Level != 0) details.AppendLine("Level: " + codex.Level.ToString(CultureInfo.InvariantCulture));
      if (!String.IsNullOrWhiteSpace(codex.Faction) && !String.Equals(codex.Faction, "None", StringComparison.OrdinalIgnoreCase)) details.AppendLine("Faction: " + codex.Faction);
      details.AppendLine("Hidden: " + codex.Hidden);
      if (!String.IsNullOrWhiteSpace(codex.Image)) details.AppendLine("Image: /resources/gfx/codex/" + codex.Image + ".dds");
      if (!String.IsNullOrWhiteSpace(codex.LoadWarning)) details.AppendLine().AppendLine("Compatibility note: " + codex.LoadWarning);
      worldCodexPreviewText.Text = details.ToString().TrimEnd();

      Bitmap image = TryLoadWorldCodexImage(codex.Image);
      System.Drawing.Image old = worldCodexPreviewImage.Image;
      worldCodexPreviewImage.Image = image;
      if (old != null && !ReferenceEquals(old, image)) old.Dispose();
      worldCodexPreviewImage.Visible = image != null;

      if (!worldCodexPreviewForm.Visible) worldCodexPreviewForm.Show(this);
      else worldCodexPreviewForm.BringToFront();
      worldCodexPreviewForm.Focus();
    }

    private WorldCodexPreviewData LoadWorldCodexPreviewData(ulong id, out string error) {
      error = null;
      if (id == 0 || currentDom == null) { error = "No codex id or GOM data is available."; return null; }
      Exception loaderError = null;
      WorldCodexPreviewData result = null;
      try {
        Codex cdx = currentDom.CodexLoader.Load(id);
        if (cdx != null) {
          result = new WorldCodexPreviewData {
            Id = cdx.Id != 0 ? cdx.Id : id,
            Fqn = cdx.Fqn,
            Name = cdx.Name,
            Description = cdx.Description,
            Category = cdx.CategoryName,
            Image = cdx.Image,
            Level = cdx.Level,
            Faction = cdx.Faction.ToString(),
            Hidden = cdx.IsHidden
          };
        }
      } catch (Exception ex) { loaderError = ex; }

      // CodexLoader returns the localization selected when the node is loaded. Some DE/FR codices have only one
      // gender variant (or a sparse STB row), which used to leave the World Browser with an empty knowledge popup.
      // Always re-read the raw retrievers and explicitly choose the current locale with a same-language fallback.
      try {
        GomObject node = currentDom.GetObject(id);
        if (node == null) {
          if (result != null) return result;
          error = loaderError == null ? "Codex node " + id + " was not found." : loaderError.Message;
          return null;
        }
        result ??= new WorldCodexPreviewData { Id = id };
        if (result.Id == 0) result.Id = id;
        if (String.IsNullOrWhiteSpace(result.Fqn)) result.Fqn = node.Name;
        if (String.IsNullOrWhiteSpace(result.Image)) result.Image = WorldInteractionText(WorldInteractionDataValue(node.Data, "cdxImage", "4611686054011931292"));
        if (result.Level == 0) result.Level = WorldInteractionInt32(WorldInteractionDataValue(node.Data, "cdxLevel", "4611686053415631338"));
        if (String.IsNullOrWhiteSpace(result.Faction)) {
          object faction = WorldInteractionDataValue(node.Data, "cdxFaction", "4611686054208831201");
          if (faction != null) result.Faction = faction.ToString();
        }
        if (loaderError != null) result.LoadWarning = loaderError.Message;

        object rawMap = WorldInteractionDataValue(node.Data, "locTextRetrieverMap", "4611686102842470023");
        string localizedName = WorldCodexLocalizedText(node.Name, WorldCodexDictionaryValue(rawMap, 9583395879878673097UL));
        string localizedDescription = WorldCodexLocalizedText(node.Name, WorldCodexDictionaryValue(rawMap, 1078249248256508798UL));
        if (!String.IsNullOrWhiteSpace(localizedName)) result.Name = localizedName;
        if (!String.IsNullOrWhiteSpace(localizedDescription)) result.Description = localizedDescription;

        object categoryRetriever = WorldCodexDictionaryValue(rawMap, 18079279596637806545UL);
        if (categoryRetriever is GomObjectData categoryData) {
          string category = WorldCodexLocalizedText(node.Name, categoryData);
          if (!String.IsNullOrWhiteSpace(category)) result.Category = category;
        }
        if (String.IsNullOrWhiteSpace(result.Name)) result.Name = node.Name;
        return result;
      } catch (Exception ex) {
        if (result != null) { result.LoadWarning = loaderError == null ? ex.Message : loaderError.Message + "; fallback: " + ex.Message; return result; }
        error = loaderError == null ? ex.Message : loaderError.Message + "\r\nFallback: " + ex.Message;
        return null;
      }
    }

    private static object WorldCodexDictionaryValue(object dictionary, ulong wanted) {
      return WorldLocMapValue(dictionary, wanted);
    }

    private string WorldCodexLocalizedText(string context, object retriever) {
      return WorldLocText(retriever, context);
    }

    private static string WorldCodexPickLocalization(Dictionary<string, string> localized) {
      return GomLib.StringTable.SelectLocalizedText(localized);
    }

    private static int WorldInteractionInt32(object value) {
      if (value == null) return 0;
      try { return Convert.ToInt32(value, CultureInfo.InvariantCulture); } catch { return 0; }
    }

    private static bool WorldInteractionBoolValue(object value) {
      if (value == null) return false;
      if (value is bool flag) return flag;
      try { return Convert.ToInt64(value, CultureInfo.InvariantCulture) != 0; } catch { return Boolean.TryParse(value.ToString(), out bool parsedFlag) && parsedFlag; }
    }

    private Bitmap TryLoadWorldCodexImage(string imageName) {
      if (String.IsNullOrWhiteSpace(imageName) || currentAssets == null) return null;
      string path = "/resources/gfx/codex/" + imageName.Trim().ToLowerInvariant() + ".dds";
      try {
        var file = currentAssets.FindFile(path);
        if (file == null) return null;
        using Stream source = file.OpenCopyInMemory();
        using var png = new MemoryStream();
        var importer = new ImageImporter();
        DevIL.Image dds = importer.LoadImageFromStream(ImageType.Dds, source);
        var exporter = new ImageExporter();
        exporter.SaveImageToStream(dds, ImageType.Png, png);
        png.Position = 0;
        using var decoded = new Bitmap(png);
        return new Bitmap(decoded);
      } catch (Exception ex) {
        System.Diagnostics.Debug.WriteLine("Codex image load failed (" + path + "): " + ex.Message);
        return null;
      }
    }

    private static Button WorldDarkActionButton(string text, int width) {
      var button = new Button {
        Text = text, Width = width, Height = 28, FlatStyle = FlatStyle.Flat,
        UseVisualStyleBackColor = false, BackColor = DrawingColor.FromArgb(31, 49, 55), ForeColor = DrawingColor.White
      };
      button.FlatAppearance.BorderColor = DrawingColor.FromArgb(103, 154, 171);
      button.FlatAppearance.MouseOverBackColor = DrawingColor.FromArgb(43, 70, 80);
      button.FlatAppearance.MouseDownBackColor = DrawingColor.FromArgb(22, 42, 49);
      return button;
    }

    private void EnsureWorldCodexPreviewForm() {
      if (worldCodexPreviewForm != null && !worldCodexPreviewForm.IsDisposed) return;
      worldCodexPreviewForm = new Form {
        Text = "Codex entry",
        FormBorderStyle = FormBorderStyle.SizableToolWindow,
        ShowInTaskbar = false,
        StartPosition = FormStartPosition.CenterParent,
        MinimumSize = new Size(620, 420),
        Size = new Size(820, 580),
        BackColor = DrawingColor.FromArgb(13, 24, 29)
      };
      worldCodexPreviewHeader = new Label {
        Dock = DockStyle.Top,
        Height = 64,
        Padding = new Padding(12, 8, 10, 5),
        AutoEllipsis = true,
        Font = new Font(Font, FontStyle.Bold),
        ForeColor = DrawingColor.FromArgb(233, 189, 84),
        BackColor = DrawingColor.FromArgb(8, 19, 23)
      };
      var body = new TableLayoutPanel {
        Dock = DockStyle.Fill,
        ColumnCount = 2,
        RowCount = 1,
        BackColor = DrawingColor.FromArgb(13, 24, 29),
        Padding = new Padding(10)
      };
      body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 250));
      body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
      worldCodexPreviewImage = new PictureBox {
        Dock = DockStyle.Top,
        Height = 250,
        SizeMode = PictureBoxSizeMode.Zoom,
        BackColor = DrawingColor.Black,
        Margin = new Padding(0, 0, 10, 0)
      };
      worldCodexPreviewText = new RichTextBox {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        BorderStyle = BorderStyle.None,
        BackColor = DrawingColor.FromArgb(13, 24, 29),
        ForeColor = DrawingColor.Gainsboro,
        Font = new Font(FontFamily.GenericSansSerif, 10f),
        DetectUrls = false,
        ScrollBars = RichTextBoxScrollBars.Vertical,
        Margin = new Padding(0)
      };
      body.Controls.Add(worldCodexPreviewImage, 0, 0);
      body.Controls.Add(worldCodexPreviewText, 1, 0);

      var buttons = new FlowLayoutPanel {
        Dock = DockStyle.Bottom,
        Height = 44,
        FlowDirection = FlowDirection.RightToLeft,
        Padding = new Padding(7, 7, 7, 4),
        WrapContents = false,
        BackColor = DrawingColor.FromArgb(8, 19, 23)
      };
      var close = WorldDarkActionButton("Close", 92);
      var copy = WorldDarkActionButton("Copy entry", 106);
      close.Click += (_, __) => worldCodexPreviewForm.Hide();
      copy.Click += (_, __) => {
        string text = worldCodexPreviewHeader.Text + "\r\n\r\n" + (worldCodexPreviewText?.Text ?? String.Empty);
        if (String.IsNullOrWhiteSpace(text)) return;
        try { Clipboard.SetText(text); SetStatusLabel("Codex entry copied to clipboard."); } catch (Exception ex) { SetStatusLabel("Could not copy codex entry: " + ex.Message); }
      };
      buttons.Controls.Add(close);
      buttons.Controls.Add(copy);
      worldCodexPreviewForm.Controls.Add(body);
      worldCodexPreviewForm.Controls.Add(buttons);
      worldCodexPreviewForm.Controls.Add(worldCodexPreviewHeader);
      worldCodexPreviewForm.FormClosed += (_, __) => {
        System.Drawing.Image old = worldCodexPreviewImage?.Image;
        if (old != null) old.Dispose();
        worldCodexPreviewForm = null;
        worldCodexPreviewHeader = null;
        worldCodexPreviewImage = null;
        worldCodexPreviewText = null;
      };
    }
  }
}
