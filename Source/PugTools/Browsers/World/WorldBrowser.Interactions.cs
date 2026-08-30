using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using GomLib;
using GomLib.Models;

namespace PugTools {
  public partial class WorldBrowser {
    // Interaction classification mirrors the prototype-field precedence used by the client/Jedipedia. In particular,
    // mission boards are tested before plcConvo: every board carries a conversation, but that tree is a notice list
    // and is not a conversation the player actually starts.
    private WorldInteractionInfo ResolveWorldNpcInteraction(string sourceFqn, Npc npc, Dictionary<string, WorldInteractionInfo> cache) {
      if (String.IsNullOrWhiteSpace(sourceFqn)) return null;
      string key = "npc|" + sourceFqn;
      if (cache != null && cache.TryGetValue(key, out WorldInteractionInfo cached)) return cached;

      GomObject node = null;
      try { node = currentDom?.GetObject(sourceFqn); } catch { }
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
      object rawTaxi = WorldInteractionDataValue(node?.Data, "taxTerminalSpec", "4611686035046870025");
      if (WorldInteractionIdSet(rawTaxi)) {
        return new WorldInteractionInfo {
          Kind = WorldInteractionKind.Taxi,
          TaxiTerminalSpec = WorldInteractionReferenceText(rawTaxi)
        };
      }

      string[] vendorPackages = WorldInteractionStrings(WorldInteractionDataValue(node?.Data, "npcVendorPackages", "4611686033397770003"));
      if (vendorPackages.Length == 0 && npc?.VendorPackages != null)
        vendorPackages = npc.VendorPackages.Where(x => !String.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
      int interactionOverride = WorldInteractionEnumNumber(WorldInteractionDataValue(node?.Data, "npcInteractionOverride", "4611686306390074000"));
      // The client still draws the vendor marker for dynamic-stock vendors that have no npcVendorPackages row.
      if (vendorPackages.Length > 0 || interactionOverride == 10) {
        return new WorldInteractionInfo { Kind = WorldInteractionKind.Vendor, VendorPackages = vendorPackages };
      }

      // Match Jedipedia/client overhead precedence: profession trainers are more specific than the generic Trainer
      // interaction override, and both beat an otherwise-present conversation marker.
      object rawTrainerProfession = WorldInteractionDataValue(node?.Data, "prfTrainerProfession", "4611686053680031369");
      int trainerProfession = WorldInteractionEnumNumber(rawTrainerProfession);
      if (trainerProfession > 1) {
        return new WorldInteractionInfo { Kind = WorldInteractionKind.ProfessionTrainer, Profession = WorldInteractionText(rawTrainerProfession) };
      }
      if (interactionOverride == 11) return new WorldInteractionInfo { Kind = WorldInteractionKind.ClassTrainer };

      object rawConversationId = WorldInteractionDataValue(node?.Data, "cnvConversationId", "4611690223044920002");
      ulong conversationId = WorldInteractionUnsigned(rawConversationId);
      string conversationName = WorldInteractionText(WorldInteractionDataValue(node?.Data, "cnvConversationName", "4611686019044571529"));
      if (String.IsNullOrWhiteSpace(conversationName)) conversationName = npc?.CnvConversationName;
      if (conversationId != 0 || !String.IsNullOrWhiteSpace(conversationName)) {
        return new WorldInteractionInfo {
          Kind = WorldInteractionKind.Conversation,
          ConversationId = conversationId,
          Conversation = conversationName
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
        var result = new WorldInteractionInfo {
          Kind = WorldInteractionKind.MissionBoard,
          MissionBoardPackage = WorldInteractionReferenceText(rawBoard),
          Conversation = conversation
        };
        return result;
      }

      string placeableConversation = WorldInteractionText(WorldInteractionDataValue(node?.Data, "plcConvo", "4611686022462471005"));
      if (String.IsNullOrWhiteSpace(placeableConversation)) placeableConversation = placeable?.ConversationFqn;
      if (!String.IsNullOrWhiteSpace(placeableConversation))
        return new WorldInteractionInfo { Kind = WorldInteractionKind.Conversation, Conversation = placeableConversation };

      // These are useful service identities already exposed by GomLib. They intentionally come after the authoritative
      // interaction fields above so an auction/enhancement prop can never hide a more specific mission/conversation.
      if (placeable?.IsAuctionHouse == true) return new WorldInteractionInfo { Kind = WorldInteractionKind.AuctionHouse };
      if (placeable?.IsEnhancementStation == true) return new WorldInteractionInfo { Kind = WorldInteractionKind.EnhancementStation };
      return null;
    }

    private void PopulateWorldMissionBoard(WorldInteractionInfo interaction) {
      if (interaction == null || interaction.Kind != WorldInteractionKind.MissionBoard || interaction.MissionBoardLoaded) return;
      interaction.MissionBoardLoaded = true;
      if (String.IsNullOrWhiteSpace(interaction.Conversation) || currentDom == null) {
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
      string locale = GomLib.StringTable.SelectedLocalization ?? "enMale";
      if (localized != null) {
        if (localized.TryGetValue(locale, out string exact) && !String.IsNullOrWhiteSpace(exact)) return exact.Trim();
        string language = locale.Length >= 2 ? locale.Substring(0, 2) : locale;
        string close = localized.FirstOrDefault(x => x.Key.StartsWith(language, StringComparison.OrdinalIgnoreCase) && !String.IsNullOrWhiteSpace(x.Value)).Value;
        if (!String.IsNullOrWhiteSpace(close)) return close.Trim();
        string any = localized.Values.FirstOrDefault(x => !String.IsNullOrWhiteSpace(x));
        if (!String.IsNullOrWhiteSpace(any)) return any.Trim();
      }
      return String.IsNullOrWhiteSpace(fallback) ? null : fallback.Trim();
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
