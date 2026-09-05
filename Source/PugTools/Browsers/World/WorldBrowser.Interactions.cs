using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using GomLib;
using GomLib.Models;

namespace PugTools {
  public partial class WorldBrowser {
    // Additive compatibility switch for pre-64-bit world content. Retail/64-bit never enters these fallbacks.
    // IsLegacyGom covers DBLB v1 beta; IsPre64BitClient also covers the 32-bit DBLB v2 releases.
    private bool WorldUsesLegacyContent => currentDom?.IsLegacyGom == true || currentDom?.IsPre64BitClient == true;

    // Interaction classification mirrors the prototype-field precedence used by the client/Jedipedia. In particular,
    // mission boards are tested before plcConvo: every board carries a conversation, but that tree is a notice list
    // and is not a conversation the player actually starts.
    private WorldInteractionInfo ResolveWorldNpcInteraction(string sourceFqn, Npc npc, Dictionary<string, WorldInteractionInfo> cache) {
      if (String.IsNullOrWhiteSpace(sourceFqn)) return null;
      string key = "npc|" + sourceFqn;
      if (cache != null && cache.TryGetValue(key, out WorldInteractionInfo cached)) return cached;

      GomObject node = null;
      try { node = WorldResolveGomObject(sourceFqn); } catch { }
      WorldInteractionInfo result = BuildWorldNpcInteraction(sourceFqn, npc, node);
      if (cache != null) cache[key] = result;
      return result;
    }

    private WorldInteractionInfo ResolveWorldPlaceableInteraction(string sourceFqn, Placeable placeable, GomObject node,
        bool isQuickTravel, Dictionary<string, WorldInteractionInfo> cache) {
      if (String.IsNullOrWhiteSpace(sourceFqn)) return null;
      string key = "plc|" + sourceFqn;
      if (cache != null && cache.TryGetValue(key, out WorldInteractionInfo cached)) return cached;
      WorldInteractionInfo result = BuildWorldPlaceableInteraction(sourceFqn, placeable, node, isQuickTravel);
      if (cache != null) cache[key] = result;
      return result;
    }

    private WorldInteractionInfo BuildWorldNpcInteraction(string sourceFqn, Npc npc, GomObject node) {
      // Conversation is deliberately resolved before the mutually-exclusive primary interaction kind. SWTOR NPCs can
      // be a vendor/trainer/taxi AND own a quest conversation at the same time. The old early returns discarded the
      // conversation metadata, which is why those actors lost their quest marker even though the conversation itself
      // was still present in the prototype.
      bool legacyDom = WorldUsesLegacyContent;
      object rawConversationId = WorldInteractionDataValue(node?.Data, "cnvConversationId", "4611690223044920002");
      if (rawConversationId == null && legacyDom)
        rawConversationId = WorldInteractionDataValue(node?.Data, "cnvConversationIdOverride", "4611690223044920001");
      ulong conversationId = WorldInteractionUnsigned(rawConversationId);
      string conversationName = WorldInteractionText(WorldInteractionDataValue(node?.Data, "cnvConversationName", "4611686019044571529"));
      if (String.IsNullOrWhiteSpace(conversationName) && legacyDom)
        conversationName = WorldInteractionText(WorldInteractionDataValue(node?.Data, "cnvConversationNameOverride", "4611686026242697764"));
      if (String.IsNullOrWhiteSpace(conversationName)) conversationName = npc?.CnvConversationName;
      bool legacyConversationReference = false;
      if (legacyDom && !String.IsNullOrWhiteSpace(conversationName)) {
        string normalized = WorldNormalizePrototypeReference(conversationName);
        if (!String.IsNullOrWhiteSpace(normalized) && normalized.StartsWith("cnv.", StringComparison.OrdinalIgnoreCase)) {
          legacyConversationReference = !String.Equals(normalized, conversationName.Trim(), StringComparison.OrdinalIgnoreCase);
          conversationName = normalized;
          if (conversationId == 0) conversationId = WorldPrototypeFqnToId(normalized);
        }
      }
      if (legacyDom && conversationId == 0 && String.IsNullOrWhiteSpace(conversationName) && node?.Data != null) {
        WorldFindLegacyConversationReference(node.Data, ref conversationId, ref conversationName);
        legacyConversationReference = conversationId != 0 || !String.IsNullOrWhiteSpace(conversationName);
      }
      if (legacyDom && String.IsNullOrWhiteSpace(conversationName) && conversationId != 0) {
        conversationName = WorldLegacyPrototypeName(conversationId, "cnv");
        if (!String.IsNullOrWhiteSpace(conversationName)) legacyConversationReference = true;
      }

      WorldInteractionInfo WithConversation(WorldInteractionInfo interaction) {
        if (interaction == null) return null;
        interaction.ConversationId = conversationId;
        interaction.Conversation = conversationName;
        interaction.LegacyHeuristic |= legacyConversationReference;
        return interaction;
      }

      object rawTaxi = WorldInteractionDataValue(node?.Data, "taxTerminalSpec", "4611686035046870025");
      if (WorldInteractionIdSet(rawTaxi)) {
        return WithConversation(new WorldInteractionInfo {
          Kind = WorldInteractionKind.Taxi,
          TaxiTerminalSpec = WorldInteractionReferenceText(rawTaxi)
        });
      }

      string[] vendorPackages = WorldInteractionStrings(WorldInteractionDataValue(node?.Data, "npcVendorPackages", "4611686033397770003"));
      if (vendorPackages.Length == 0 && npc?.VendorPackages != null)
        vendorPackages = npc.VendorPackages.Where(x => !String.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
      int interactionOverride = WorldInteractionEnumNumber(WorldInteractionDataValue(node?.Data, "npcInteractionOverride", "4611686306390074000"));
      // The client still draws the vendor marker for dynamic-stock vendors that have no npcVendorPackages row.
      if (vendorPackages.Length > 0 || interactionOverride == 10) {
        return WithConversation(new WorldInteractionInfo { Kind = WorldInteractionKind.Vendor, VendorPackages = vendorPackages });
      }

      // Match Jedipedia/client overhead precedence: profession trainers are more specific than the generic Trainer
      // interaction override. Conversation metadata remains attached as a secondary capability instead of being lost.
      object rawTrainerProfession = WorldInteractionDataValue(node?.Data, "prfTrainerProfession", "4611686053680031369");
      int trainerProfession = WorldInteractionEnumNumber(rawTrainerProfession);
      if (trainerProfession > 1) {
        return WithConversation(new WorldInteractionInfo { Kind = WorldInteractionKind.ProfessionTrainer, Profession = WorldInteractionText(rawTrainerProfession) });
      }
      if (interactionOverride == 11) return WithConversation(new WorldInteractionInfo { Kind = WorldInteractionKind.ClassTrainer });

      if (conversationId != 0 || !String.IsNullOrWhiteSpace(conversationName)) {
        return new WorldInteractionInfo {
          Kind = WorldInteractionKind.Conversation,
          ConversationId = conversationId,
          Conversation = conversationName,
          LegacyHeuristic = legacyConversationReference
        };
      }

      // Pre-2.x/partial DOMs occasionally omit the terminal field while retaining the recognizable terminal npc.
      // Keep the existing compatibility hint only as a last resort and mark it so the Ctrl-inspector tells us when
      // a build depended on it. Current data should resolve the taxTerminalSpec branch above.
      if (WorldNpcLooksLikeTaxiTerminal(sourceFqn, npc?.Name, npc?.Title)) {
        return new WorldInteractionInfo { Kind = WorldInteractionKind.Taxi, LegacyHeuristic = true };
      }
      return null;
    }

    private WorldInteractionInfo BuildWorldPlaceableInteraction(string sourceFqn, Placeable placeable, GomObject node, bool isQuickTravel) {
      long wonkaPackage = placeable?.WonkaPackageId ?? 0;
      if (wonkaPackage == 0) wonkaPackage = WonkInt64(WorldInteractionDataValue(node?.Data, "wnkPackageID", "4611686061108531204"));
      if (wonkaPackage != 0) return new WorldInteractionInfo { Kind = WorldInteractionKind.Wonkavator, WonkaPackageId = wonkaPackage };

      object rawTaxi = WorldInteractionDataValue(node?.Data, "plcTaxiTerminalSpec", "4611686035128171095");
      if (WorldInteractionIdSet(rawTaxi)) {
        return new WorldInteractionInfo { Kind = WorldInteractionKind.Taxi, TaxiTerminalSpec = WorldInteractionReferenceText(rawTaxi) };
      }

      if (isQuickTravel) return new WorldInteractionInfo { Kind = WorldInteractionKind.QuickTravel };

      // Knowledge/lore placeables award the cdx entry named by plcCodexSpec. This is a real click interaction in the
      // client (and one of the fields that advertises the blue usable glow), so expose it before the generic service
      // fallbacks. Prefer the already-loaded Placeable value but retain the raw field for old/partial DOMs.
      ulong codexId = placeable?.CodexId ?? 0;
      if (codexId == 0) codexId = WorldInteractionUnsigned(WorldInteractionDataValue(node?.Data, "plcCodexSpec", "4611686062140131223"));
      if (codexId != 0) return new WorldInteractionInfo { Kind = WorldInteractionKind.Codex, CodexId = codexId };

      int utilityType = WorldInteractionEnumNumber(WorldInteractionDataValue(node?.Data, "plcUtilityType", "4611686300986244002"));
      if (utilityType == 4 || (utilityType == 0 && placeable?.IsBank == true))
        return new WorldInteractionInfo { Kind = WorldInteractionKind.Bank, UtilityType = utilityType };
      if (utilityType == 7)
        return new WorldInteractionInfo { Kind = WorldInteractionKind.GuildBank, UtilityType = utilityType };
      if (utilityType == 9 || (utilityType == 0 && placeable?.IsMailbox == true))
        return new WorldInteractionInfo { Kind = WorldInteractionKind.Mailbox, UtilityType = utilityType };

      if (WorldInteractionHasProfession(placeable, WorldInteractionDataValue(node?.Data, "prfProfessionRequired", "4611686043588270023"), out string profession)) {
        return new WorldInteractionInfo { Kind = WorldInteractionKind.Harvest, Profession = profession };
      }

      object rawBoard = WorldInteractionDataValue(node?.Data, "plcMissionBoardPkg", "4611686052498831192");
      if (WorldInteractionValueSet(rawBoard)) {
        string conversation = WorldInteractionText(WorldInteractionDataValue(node?.Data, "plcConvo", "4611686022462471005"));
        if (String.IsNullOrWhiteSpace(conversation)) conversation = placeable?.ConversationFqn;
        ulong conversationId = 0;
        if (WorldUsesLegacyContent && !String.IsNullOrWhiteSpace(conversation)) {
          string normalized = WorldNormalizePrototypeReference(conversation);
          if (!String.IsNullOrWhiteSpace(normalized) && normalized.StartsWith("cnv.", StringComparison.OrdinalIgnoreCase)) {
            conversation = normalized;
            conversationId = WorldPrototypeFqnToId(normalized);
          }
        }
        if (WorldUsesLegacyContent && conversationId == 0 && String.IsNullOrWhiteSpace(conversation) && node?.Data != null)
          WorldFindLegacyConversationReference(node.Data, ref conversationId, ref conversation);
        var result = new WorldInteractionInfo {
          Kind = WorldInteractionKind.MissionBoard,
          MissionBoardPackage = WorldInteractionReferenceText(rawBoard),
          ConversationId = conversationId,
          Conversation = conversation
        };
        return result;
      }

      string placeableConversation = WorldInteractionText(WorldInteractionDataValue(node?.Data, "plcConvo", "4611686022462471005"));
      if (String.IsNullOrWhiteSpace(placeableConversation)) placeableConversation = placeable?.ConversationFqn;
      ulong placeableConversationId = 0;
      bool legacyPlaceableConversation = false;
      if (WorldUsesLegacyContent && !String.IsNullOrWhiteSpace(placeableConversation)) {
        string normalized = WorldNormalizePrototypeReference(placeableConversation);
        if (!String.IsNullOrWhiteSpace(normalized) && normalized.StartsWith("cnv.", StringComparison.OrdinalIgnoreCase)) {
          legacyPlaceableConversation = !String.Equals(normalized, placeableConversation.Trim(), StringComparison.OrdinalIgnoreCase);
          placeableConversation = normalized;
          placeableConversationId = WorldPrototypeFqnToId(normalized);
        }
      } else if (WorldUsesLegacyContent && node?.Data != null) {
        WorldFindLegacyConversationReference(node.Data, ref placeableConversationId, ref placeableConversation);
        legacyPlaceableConversation = placeableConversationId != 0 || !String.IsNullOrWhiteSpace(placeableConversation);
      }
      if (placeableConversationId != 0 || !String.IsNullOrWhiteSpace(placeableConversation))
        return new WorldInteractionInfo { Kind = WorldInteractionKind.Conversation, ConversationId = placeableConversationId, Conversation = placeableConversation, LegacyHeuristic = legacyPlaceableConversation };

      // These are useful service identities already exposed by GomLib. They intentionally come after the authoritative
      // interaction fields above so an auction/enhancement prop can never hide a more specific mission/conversation.
      if (placeable?.IsAuctionHouse == true) return new WorldInteractionInfo { Kind = WorldInteractionKind.AuctionHouse };
      if (placeable?.IsEnhancementStation == true) return new WorldInteractionInfo { Kind = WorldInteractionKind.EnhancementStation };
      return null;
    }

    private void PopulateWorldMissionBoard(WorldInteractionInfo interaction) {
      if (interaction == null || interaction.Kind != WorldInteractionKind.MissionBoard || interaction.MissionBoardLoaded) return;
      interaction.MissionBoardLoaded = true;
      if (currentDom == null || (interaction.ConversationId == 0 && String.IsNullOrWhiteSpace(interaction.Conversation))) {
        interaction.MissionBoardError = "No mission-board conversation is available.";
        return;
      }
      try {
        if (!TryLoadWorldConversation(interaction, out Conversation conversation, out string conversationError) || conversation == null) {
          interaction.MissionBoardError = conversationError ?? "Conversation could not be loaded.";
          return;
        }

        var ordered = new List<DialogNode>();
        var seen = new HashSet<long>();
        if (conversation.RootNodes != null) {
          foreach (var root in conversation.RootNodes.OrderBy(x => x.Key)) {
            if (conversation.NodeLookup != null && conversation.NodeLookup.TryGetValue(root.Value, out DialogNode node) && node != null && seen.Add(node.NodeId))
              ordered.Add(node);
          }
        }
        if (conversation.DialogNodes != null) {
          foreach (DialogNode node in conversation.DialogNodes)
            if (node != null && seen.Add(node.NodeId)) ordered.Add(node);
        }

        foreach (DialogNode node in ordered) {
          var notice = new WorldMissionBoardNotice { NodeId = node.NodeId, Text = WorldLocalizedDialogText(node) };
          var questIds = new List<ulong>();
          if (node.QuestsGranted != null) foreach (ulong id in node.QuestsGranted) if (id != 0 && !questIds.Contains(id)) questIds.Add(id);
          if (node.ActionQuest != 0 && !questIds.Contains(node.ActionQuest)) questIds.Add(node.ActionQuest);
          foreach (ulong questId in questIds) notice.Quests.Add(ResolveWorldMissionBoardQuest(questId));
          interaction.MissionBoardNotices.Add(notice);
        }
      } catch (Exception ex) {
        interaction.MissionBoardError = ex.Message;
        System.Diagnostics.Debug.WriteLine("Mission board inspection failed (" + interaction.Conversation + "): " + ex.Message);
      }
    }

    private WorldMissionBoardQuest ResolveWorldMissionBoardQuest(ulong id) {
      var result = new WorldMissionBoardQuest { Id = id };
      try {
        Quest quest = currentDom?.QuestLoader.Load(id);
        if (quest != null) {
          result.Fqn = quest.Fqn;
          // Quest.Name/LocalizedName are intentionally internal in GomLib. ToXElement(false) is the public
          // lightweight projection and already emits the currently selected localized quest name.
          try { result.Name = quest.ToXElement(false)?.Element("Name")?.Value; } catch { }
          if (String.IsNullOrWhiteSpace(result.Name)) result.Name = result.Fqn;
        }
      } catch (Exception ex) {
        System.Diagnostics.Debug.WriteLine("Mission board quest lookup failed (" + id + "): " + ex.Message);
      }
      return result;
    }

    private static string WorldLocalizedDialogText(DialogNode node) {
      if (node == null) return null;
      return WorldLocalizedText(node.LocalizedText, node.Text);
    }

    private static string WorldLocalizedText(Dictionary<string, string> localized, string fallback) {
      string text = GomLib.StringTable.SelectLocalizedText(localized, fallback);
      return String.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    // Legacy prototype compatibility ---------------------------------------------------------------
    //
    // 2011/32-bit builds frequently store an FQN string where current data stores a prototype id.  RED also
    // ships the server-side source tree, whose path is the prototype name with / -> . and the extension removed.
    // Client.gom does not necessarily carry the resolved name in its name lookup, even when the object itself is
    // present by id.  Jedipedia therefore folds a candidate FQN back to the node id.  Keep the same rule local to
    // the World Browser so every map feature (NPC/plc/spn/dyn/mapnote/conversation) can share it without changing
    // the normal DataObjectModel semantics used by the other browsers.
    private static string WorldNormalizePrototypeReference(string reference) {
      if (String.IsNullOrWhiteSpace(reference)) return null;
      string original = reference.Trim().Trim('"', '\'').Replace('\\', '/');
      string value = original;
      if (value.Length == 0) return null;

      int resource = value.IndexOf("/resources/", StringComparison.OrdinalIgnoreCase);
      if (resource >= 0) value = value.Substring(resource + "/resources/".Length);
      else value = value.TrimStart('/');

      if (value.StartsWith("resources/", StringComparison.OrdinalIgnoreCase)) value = value.Substring("resources/".Length);
      bool serverPath = value.StartsWith("server/", StringComparison.OrdinalIgnoreCase) || value.StartsWith("gamedata/", StringComparison.OrdinalIgnoreCase);
      if (value.StartsWith("server/", StringComparison.OrdinalIgnoreCase)) value = value.Substring("server/".Length);
      else if (value.StartsWith("gamedata/", StringComparison.OrdinalIgnoreCase)) value = value.Substring("gamedata/".Length);

      string lower = value.ToLowerInvariant();
      string[] prototypeExtensions = {
        ".cnv", ".npc", ".plc", ".dyn", ".npp", ".ipp", ".spn", ".spn_c", ".spn_p", ".spn_crf",
        ".spn_enc", ".spn_grp", ".spn_lst", ".spn_mov", ".spn_pt", ".enc", ".qst", ".mpn", ".pth",
        ".abl", ".itm", ".ach", ".cdx", ".hyd", ".phs", ".veh", ".cos", ".eff", ".pkg", ".tal"
      };
      bool prototypeExtension = false;
      foreach (string extension in prototypeExtensions.OrderByDescending(x => x.Length)) {
        if (!lower.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) continue;
        value = value.Substring(0, value.Length - extension.Length);
        prototypeExtension = true;
        break;
      }

      // Do not reinterpret ordinary resources (/resources/art/*.gr2, icons, textures, audio...) as GOM FQNs.
      if (value.IndexOf('/') >= 0 && !serverPath && !prototypeExtension) return original;
      if (value.IndexOf('/') >= 0) value = value.Replace('/', '.');
      value = value.Trim('.');
      return value.Length == 0 ? null : value;
    }

    private static ulong WorldPrototypeFqnToId(string fqn) {
      if (String.IsNullOrWhiteSpace(fqn)) return 0;
      unchecked {
        const ulong prime = 0x100000001B3UL;
        ulong hash = 0xCBF29CE484222325UL;
        string upper = fqn.Trim().ToUpperInvariant();
        foreach (char ch in upper) {
          hash ^= ch;
          hash *= prime;
        }
        ulong top = hash >> 48;
        ulong middle = hash & 0x0000FFFFFFFF0000UL;
        ulong low = hash & 0xFFFFUL;
        return 0xE000000000000000UL | middle | (top ^ low);
      }
    }

    private readonly object worldLegacyPrototypeNameLock = new object();
    private Dictionary<ulong, string> worldLegacyPrototypeNamesById;
    private bool worldLegacyPrototypeNamesAttempted;

    private void ResetWorldLegacyPrototypeNameIndex() {
      lock (worldLegacyPrototypeNameLock) {
        worldLegacyPrototypeNamesAttempted = false;
        worldLegacyPrototypeNamesById = null;
      }
    }

    private void EnsureWorldLegacyPrototypeNameIndex() {
      if (worldLegacyPrototypeNamesAttempted) return;
      lock (worldLegacyPrototypeNameLock) {
        if (worldLegacyPrototypeNamesAttempted) return;
        worldLegacyPrototypeNamesAttempted = true;
        worldLegacyPrototypeNamesById = new Dictionary<ulong, string>();
        // Do not walk the archive filename corpus for a normal retail/64-bit DOM.  The existing name table is
        // authoritative there and remains the fast path.  This index exists only to recover names that old GOMs
        // intentionally omit.
        if (!WorldUsesLegacyContent || currentAssets?.Libraries == null) return;

        foreach (TorArchive.Library library in currentAssets.Libraries) {
          try { if (!library.Loaded) library.Load(); } catch { continue; }
          foreach (TorArchive.Archive archive in library.Archives.Values) {
            if (archive == null) continue;
            int serverNames = 0;
            IEnumerable<nsHashDictionary.HashData> namedFiles = null;
            try { namedFiles = TorArchive.HashDictionaryInstance.Instance.Dictionary.EnumerateArchiveFiles(archive.StrippedFileName); }
            catch { }
            if (namedFiles != null) foreach (nsHashDictionary.HashData named in namedFiles) {
              string path = named?.FileName;
              if (!WorldTryAddLegacyPrototypePath(path, archive)) continue;
              serverNames++;
            }

            // If the archive-specific filename table predates this beta family, recover names through the global hash
            // dictionary, but only in this legacy-only World Browser index.  Keeping the fallback local is important:
            // HashFileInfo remains byte-for-byte on the established Retail/64-bit lookup path.
            if (serverNames == 0 && WorldUsesLegacyContent) {
              foreach (TorArchive.File file in archive.EnumerateFiles()) {
                try {
                  nsHashDictionary.HashData global = TorArchive.HashDictionaryInstance.Instance.Dictionary.SearchHashList(
                    file.FileInfo.PrimaryHash, file.FileInfo.SecondaryHash);
                  if (global == null || String.IsNullOrWhiteSpace(global.FileName)) continue;
                  if (WorldTryAddLegacyPrototypePath(global.FileName, archive)) serverNames++;
                } catch { }
              }
            }
          }
        }
      }
    }

    private bool WorldTryAddLegacyPrototypePath(string rawPath, TorArchive.Archive archive) {
      string path = (rawPath ?? String.Empty).Replace('\\', '/').Trim();
      if (path.IndexOf("/resources/server/", StringComparison.OrdinalIgnoreCase) < 0 &&
          path.IndexOf("/resources/gamedata/", StringComparison.OrdinalIgnoreCase) < 0 &&
          !path.StartsWith("resources/server/", StringComparison.OrdinalIgnoreCase) &&
          !path.StartsWith("resources/gamedata/", StringComparison.OrdinalIgnoreCase)) return false;
      string fqn = WorldNormalizePrototypeReference(path);
      if (String.IsNullOrWhiteSpace(fqn) || fqn.StartsWith("tbl.", StringComparison.OrdinalIgnoreCase)) return false;

      // A dictionary can contain names learned from another release of the same archive family. Only accept names
      // whose hash is actually present in this physical TOR, otherwise old and current prototypes get mixed.
      if (archive != null) {
        TorArchive.File installed = null;
        try { installed = archive.FindFile(TorArchive.FileId.FromFilePath(path)); } catch { }
        if (installed == null && !path.StartsWith("/", StringComparison.Ordinal)) {
          try { installed = archive.FindFile(TorArchive.FileId.FromFilePath("/" + path)); } catch { }
        }
        if (installed == null) return false;
        try { installed.Dispose(); } catch { }
      }

      ulong id = WorldPrototypeFqnToId(fqn);
      if (id != 0 && !worldLegacyPrototypeNamesById.ContainsKey(id)) worldLegacyPrototypeNamesById[id] = fqn;
      return true;
    }


    private string WorldLegacyPrototypeName(ulong id, string namespaceHint = null) {
      if (id == 0) return null;
      EnsureWorldLegacyPrototypeNameIndex();
      if (worldLegacyPrototypeNamesById == null || !worldLegacyPrototypeNamesById.TryGetValue(id, out string fqn)) return null;
      if (!String.IsNullOrWhiteSpace(namespaceHint) && !fqn.StartsWith(namespaceHint + ".", StringComparison.OrdinalIgnoreCase)) return null;
      return fqn;
    }

    private GomObject WorldResolveGomObject(ulong id) {
      if (id == 0 || currentDom == null) return null;
      try { return currentDom.GetObject(id); } catch { return null; }
    }

    private GomObject WorldResolveGomObject(ulong id, string namespaceHint) {
      GomObject obj = WorldResolveGomObject(id);
      if (obj == null || !WorldUsesLegacyContent || !String.IsNullOrWhiteSpace(obj.Name)) return obj;
      string legacyName = WorldLegacyPrototypeName(id, namespaceHint);
      if (!String.IsNullOrWhiteSpace(legacyName)) obj.Name = legacyName;
      return obj;
    }

    private GomObject WorldResolveGomObject(string reference) {
      if (String.IsNullOrWhiteSpace(reference) || currentDom == null) return null;
      string raw = reference.Trim();
      if (UInt64.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong numeric) && numeric != 0)
        return WorldResolveGomObject(numeric);
      if (Int64.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long signed) && signed != 0)
        return WorldResolveGomObject(unchecked((ulong)signed));

      try {
        GomObject exact = currentDom.GetObject(raw);
        if (exact != null) return exact;
      } catch { }

      string normalized = WorldNormalizePrototypeReference(raw);
      if (String.IsNullOrWhiteSpace(normalized)) return null;
      if (!String.Equals(normalized, raw, StringComparison.OrdinalIgnoreCase)) {
        try {
          GomObject named = currentDom.GetObject(normalized);
          if (named != null) return named;
        } catch { }
      }

      // This is the critical RED/32-bit fallback: an unnamed client.gom prototype can still be opened because its
      // id is a deterministic fold of the FQN. It is deliberately gated behind WorldUsesLegacyContent. A normal
      // retail/64-bit DOM therefore stops after the exact/name-table lookups above and keeps the established parser semantics.
      if (!WorldUsesLegacyContent) return null;
      ulong folded = WorldPrototypeFqnToId(normalized);
      GomObject fallback = WorldResolveGomObject(folded);
      if (fallback != null && String.IsNullOrWhiteSpace(fallback.Name)) fallback.Name = normalized;
      return fallback;
    }

    private List<GomObject> WorldObjectsStartingWith(string prefix) {
      var result = new List<GomObject>();
      if (currentDom == null || String.IsNullOrWhiteSpace(prefix)) return result;
      var seen = new HashSet<ulong>();

      // Preserve the current 64-bit parser/list first, byte-for-byte in terms of lookup precedence.
      try {
        foreach (GomObject obj in currentDom.GetObjectsStartingWith(prefix)) {
          if (obj == null || !seen.Add(obj.Id)) continue;
          result.Add(obj);
        }
      } catch { }

      // Old RED/HE32 GOMs can have the object by id but no FQN entry in the name map.  Augment, never replace, the
      // normal result with FQNs reconstructed from installed server/gamedata source paths.
      if (WorldUsesLegacyContent) {
        EnsureWorldLegacyPrototypeNameIndex();
        if (worldLegacyPrototypeNamesById != null) {
          foreach (KeyValuePair<ulong, string> entry in worldLegacyPrototypeNamesById) {
            if (String.IsNullOrWhiteSpace(entry.Value) || !entry.Value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !seen.Add(entry.Key)) continue;
            GomObject obj = WorldResolveGomObject(entry.Key);
            if (obj == null) continue;
            if (String.IsNullOrWhiteSpace(obj.Name)) obj.Name = entry.Value;
            result.Add(obj);
          }
        }
      }
      return result;
    }

    private IEnumerable<string> WorldKnownPrototypeNames() {
      if (currentDom == null) yield break;
      var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      IEnumerable<string> registered = null;
      try { registered = currentDom.GetAllInstanceNames().Keys.ToArray(); } catch { }
      if (registered != null) foreach (string name in registered) {
        if (String.IsNullOrWhiteSpace(name) || !seen.Add(name)) continue;
        yield return name;
      }
      if (!WorldUsesLegacyContent) yield break;
      EnsureWorldLegacyPrototypeNameIndex();
      if (worldLegacyPrototypeNamesById == null) yield break;
      foreach (string name in worldLegacyPrototypeNamesById.Values) {
        if (String.IsNullOrWhiteSpace(name) || !seen.Add(name)) continue;
        yield return name;
      }
    }

    private static bool WorldLooksLikePrototypeFqn(string value, string namespacePrefix) {
      if (String.IsNullOrWhiteSpace(value)) return false;
      string normalized = WorldNormalizePrototypeReference(value);
      return !String.IsNullOrWhiteSpace(normalized) && normalized.StartsWith(namespacePrefix + ".", StringComparison.OrdinalIgnoreCase);
    }

    private void WorldFindLegacyConversationReference(GomObjectData data, ref ulong id, ref string fqn, int depth = 0) {
      if (data == null || depth > 4 || (id != 0 && !String.IsNullOrWhiteSpace(fqn))) return;
      foreach (KeyValuePair<string, object> pair in data.Dictionary) {
        if (String.Equals(pair.Key, "_count", StringComparison.OrdinalIgnoreCase)) continue;
        string key = pair.Key ?? String.Empty;
        object value = pair.Value;
        bool keySuggestsConversation = key.IndexOf("cnv", StringComparison.OrdinalIgnoreCase) >= 0 ||
          key.IndexOf("convo", StringComparison.OrdinalIgnoreCase) >= 0 || key.IndexOf("conversation", StringComparison.OrdinalIgnoreCase) >= 0;

        if (value is string text) {
          string normalized = WorldNormalizePrototypeReference(text);
          if (!String.IsNullOrWhiteSpace(normalized) && normalized.StartsWith("cnv.", StringComparison.OrdinalIgnoreCase)) {
            fqn = normalized;
            if (id == 0) id = WorldPrototypeFqnToId(normalized);
            return;
          }
          if (keySuggestsConversation && id == 0) {
            ulong numericText = WorldInteractionUnsigned(text);
            if (numericText != 0) id = numericText;
          }
        } else if (keySuggestsConversation && id == 0) {
          ulong candidate = WorldInteractionUnsigned(value);
          if (candidate != 0) id = candidate;
        }

        if (value is GomObjectData nested) WorldFindLegacyConversationReference(nested, ref id, ref fqn, depth + 1);
        else if (value is IDictionary dictionary && depth < 4) {
          foreach (DictionaryEntry entry in dictionary) {
            if (entry.Value is GomObjectData nestedEntry) WorldFindLegacyConversationReference(nestedEntry, ref id, ref fqn, depth + 1);
            else if (entry.Value is string nestedText && WorldLooksLikePrototypeFqn(nestedText, "cnv")) {
              fqn = WorldNormalizePrototypeReference(nestedText);
              if (id == 0) id = WorldPrototypeFqnToId(fqn);
            }
            if (id != 0 && !String.IsNullOrWhiteSpace(fqn)) return;
          }
        }
        if (id != 0 && !String.IsNullOrWhiteSpace(fqn)) return;
      }
    }

    private static object WorldInteractionDataValue(GomObjectData data, string name, string numericName) {
      if (data == null) return null;
      if (!String.IsNullOrWhiteSpace(name) && data.Dictionary.TryGetValue(name, out object value)) return value;
      return !String.IsNullOrWhiteSpace(numericName) && data.Dictionary.TryGetValue(numericName, out value) ? value : null;
    }

    private static List<object> WorldInteractionListEntries(object value) {
      var result = new List<object>();
      if (value == null || value is string) return result;
      // Serialized GOM arrays/maps are GomObjectData rather than IDictionary. Treat their numeric keys exactly like
      // the wire map; otherwise Hydra action blocks, child-node lists and several old prototype lists silently read
      // as empty even though the data is present.
      if (value is GomObjectData gom) {
        var entries = new List<(int Order, object Value)>();
        int fallback = 0;
        foreach (KeyValuePair<string, object> entry in gom.Dictionary) {
          string key = entry.Key;
          if (String.Equals(key, "_count", StringComparison.OrdinalIgnoreCase)) continue;
          int order = Int32.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) ? parsed : Int32.MaxValue - 100000 + fallback++;
          entries.Add((order, entry.Value));
        }
        result.AddRange(entries.OrderBy(x => x.Order).Select(x => x.Value));
        return result;
      }
      if (value is IDictionary dictionary) {
        var entries = new List<(int Order, object Value)>();
        int fallback = 0;
        foreach (DictionaryEntry entry in dictionary) {
          string key = entry.Key?.ToString();
          if (String.Equals(key, "_count", StringComparison.OrdinalIgnoreCase)) continue;
          int order = Int32.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) ? parsed : Int32.MaxValue - 100000 + fallback++;
          entries.Add((order, entry.Value));
        }
        result.AddRange(entries.OrderBy(x => x.Order).Select(x => x.Value));
        return result;
      }
      if (value is IEnumerable enumerable) foreach (object entry in enumerable) result.Add(entry);
      return result;
    }

    private static GomObjectData WorldInteractionObjectData(object value) {
      if (value is GomObjectData data) return data;
      if (value is IDictionary dictionary) {
        var copy = new GomObjectData();
        foreach (DictionaryEntry entry in dictionary) {
          string key = entry.Key?.ToString();
          if (String.IsNullOrWhiteSpace(key) || String.Equals(key, "_count", StringComparison.OrdinalIgnoreCase)) continue;
          copy.Dictionary[key] = entry.Value;
        }
        return copy.Dictionary.Count == 0 ? null : copy;
      }
      return null;
    }

    private static Dictionary<object, object> WorldInteractionMap(object value) {
      if (value == null) return null;
      var result = new Dictionary<object, object>();
      if (value is GomObjectData gom) {
        foreach (KeyValuePair<string, object> entry in gom.Dictionary) {
          if (String.Equals(entry.Key, "_count", StringComparison.OrdinalIgnoreCase)) continue;
          result[entry.Key] = entry.Value;
        }
      } else if (value is IDictionary dictionary) {
        foreach (DictionaryEntry entry in dictionary) {
          if (String.Equals(entry.Key?.ToString(), "_count", StringComparison.OrdinalIgnoreCase)) continue;
          result[entry.Key] = entry.Value;
        }
      }
      return result.Count == 0 ? null : result;
    }

    private static string[] WorldInteractionStrings(object value) {
      return WorldInteractionListEntries(value).Select(WorldInteractionText).Where(x => !String.IsNullOrWhiteSpace(x))
        .Select(x => x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private string WorldInteractionReferenceText(object value) {
      if (value == null) return null;
      if (value is GomObject gom) return gom.Name;
      string resolved = null;
      try { resolved = ResolveGomReferenceName(value); } catch { }
      if (!String.IsNullOrWhiteSpace(resolved)) return resolved.Trim();
      return WorldInteractionText(value);
    }

    private static string WorldInteractionText(object value) {
      if (value == null) return null;
      if (value is string text) return String.IsNullOrWhiteSpace(text) ? null : text.Trim();
      string result = value.ToString()?.Trim();
      return String.IsNullOrWhiteSpace(result) || result == "0" ? null : result;
    }

    private static bool WorldInteractionIdSet(object value) {
      return WorldInteractionUnsigned(value) != 0 || (!String.IsNullOrWhiteSpace(WorldInteractionText(value)) && !String.Equals(WorldInteractionText(value), "0", StringComparison.Ordinal));
    }

    private static bool WorldInteractionValueSet(object value) {
      if (value == null) return false;
      if (value is string text) return !String.IsNullOrWhiteSpace(text) && text.Trim() != "0";
      if (value is ScriptEnum scriptEnum) return scriptEnum.Value != 0;
      try { return Convert.ToDecimal(value, CultureInfo.InvariantCulture) != 0m; } catch { return true; }
    }

    private static ulong WorldInteractionUnsigned(object value) {
      if (value == null) return 0;
      try {
        if (value is GomObjectData gom) {
          // A few BLUE/legacy prototype references arrive wrapped in a one-entry GomObjectData instead of as the
          // modern scalar node id. Unwrap the common value/id/1 shape before giving up on interactions such as codex.
          foreach (string key in new[] { "value", "Value", "id", "Id", "1" })
            if (gom.Dictionary.TryGetValue(key, out object nested)) { ulong unwrapped = WorldInteractionUnsigned(nested); if (unwrapped != 0) return unwrapped; }
          foreach (KeyValuePair<string, object> pair in gom.Dictionary) {
            if (String.Equals(pair.Key, "_count", StringComparison.OrdinalIgnoreCase)) continue;
            ulong unwrapped = WorldInteractionUnsigned(pair.Value); if (unwrapped != 0) return unwrapped;
          }
          return 0;
        }
        if (value is ulong ul) return ul;
        if (value is long l) return unchecked((ulong)l);
        if (value is uint ui) return ui;
        if (value is int i) return unchecked((ulong)(long)i);
        if (value is ScriptEnum se && se.Value > 0) return (ulong)se.Value;
        string text = value.ToString()?.Trim();
        if (UInt64.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong parsed)) return parsed;
        if (Int64.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long signed)) return unchecked((ulong)signed);
      } catch { }
      return 0;
    }

    private static int WorldInteractionEnumNumber(object value) {
      if (value == null) return 0;
      if (value is ScriptEnum scriptEnum) return scriptEnum.Value + 1;
      try { return Convert.ToInt32(value, CultureInfo.InvariantCulture); } catch { return 0; }
    }

    private static bool WorldInteractionHasProfession(Placeable placeable, object raw, out string profession) {
      profession = null;
      if (placeable != null && placeable.RequiredProfession != Profession.None) {
        profession = placeable.RequiredProfession.ToString();
        return true;
      }
      if (raw == null) return false;
      if (raw is ScriptEnum scriptEnum) {
        string name = scriptEnum.ToString();
        if (name.IndexOf("None", StringComparison.OrdinalIgnoreCase) >= 0) return false;
        // GomLib's Enum reader stores ScriptEnum.Value zero-based (wire value - 1). A raw value of zero is therefore
        // SWTOR's authored enum value 1 == None; every larger authored value is a real profession.
        int authored = scriptEnum.Value + 1;
        if (authored <= 1) return false;
        profession = scriptEnum.EnumType != null ? name : authored.ToString(CultureInfo.InvariantCulture);
        return true;
      }
      string text = raw.ToString()?.Trim();
      if (String.IsNullOrWhiteSpace(text) || text == "0" || text == "1" || text.IndexOf("None", StringComparison.OrdinalIgnoreCase) >= 0) return false;
      if (Int32.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number) && number <= 1) return false;
      profession = text;
      return true;
    }
  }
}
