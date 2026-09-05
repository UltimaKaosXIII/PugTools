using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using GomLib;
using GomLib.Models;

namespace PugTools {
  public partial class WorldBrowser {
    private sealed class WorldConversationConditionData {
      public readonly List<ulong> Condition = new List<ulong>();
      public readonly List<ulong> PreviewCondition = new List<ulong>();
    }

    private DataObjectModel worldConversationConditionDom;
    private readonly Dictionary<ulong, Dictionary<long, WorldConversationConditionData>> worldConversationConditionCache =
      new Dictionary<ulong, Dictionary<long, WorldConversationConditionData>>();

    // SWTOR stores a dialog-node condition as a postfix program. The actual game can evaluate that program because it
    // has a live character/quest-state table; an offline viewer cannot truthfully choose the winning branch. Mirror
    // Jedipedia here: decode the predicates, show them on every alternate start/choice and let the user select the
    // branch instead of silently playing Root 1 (which was wrong for many quest-progress variants).
    private void CaptureWorldConversationConditions(Conversation conversation) {
      if (conversation == null || currentDom == null || conversation.Id == 0) return;
      if (!ReferenceEquals(worldConversationConditionDom, currentDom)) {
        worldConversationConditionDom = currentDom;
        worldConversationConditionCache.Clear();
      }
      if (worldConversationConditionCache.ContainsKey(conversation.Id)) return;

      var byNode = new Dictionary<long, WorldConversationConditionData>();
      try {
        GomObject raw = WorldResolveGomObject(conversation.Id);
        if (raw == null && !String.IsNullOrWhiteSpace(conversation.Fqn)) raw = WorldResolveGomObject(conversation.Fqn);
        object rawDialogs = WorldInteractionDataValue(raw?.Data, "cnvTreeDialogNodes_Prototype", "4611686050212071021");
        foreach (KeyValuePair<object, object> entry in WorldConversationMapEntries(rawDialogs)) {
          GomObjectData data = WorldConversationFirstObjectData(entry.Value, false);
          if (WorldUsesLegacyContent && (data == null || WorldInteractionDataValue(data, "cnvNodeNumber", "4611686019044571365") == null))
            data = WorldConversationFindObjectDataWithField(entry.Value, "cnvNodeNumber", "4611686019044571365");
          if (data == null) continue;
          long nodeId = WonkInt64(WorldInteractionDataValue(data, "cnvNodeNumber", "4611686019044571365"));
          if (nodeId == 0) nodeId = WonkInt64(entry.Key);
          if (nodeId == 0) continue;
          var item = new WorldConversationConditionData();
          AppendWorldConversationConditionTokens(item.Condition,
            WorldConversationConditionValue(data, "cnvConditionCompiled", "4611686244921010004", "4611686050718671495"));
          AppendWorldConversationConditionTokens(item.PreviewCondition,
            WorldConversationConditionValue(data, "cnvConditionPreviewCompiled", "4611686244921010003", "4611686050718671499"));
          if (item.Condition.Count > 0 || item.PreviewCondition.Count > 0) byNode[nodeId] = item;
        }
      } catch (Exception ex) {
        System.Diagnostics.Debug.WriteLine("Conversation condition index failed for " + (conversation.Fqn ?? conversation.Id.ToString(CultureInfo.InvariantCulture)) + ": " + ex.Message);
      }
      worldConversationConditionCache[conversation.Id] = byNode;
    }

    private static object WorldConversationConditionValue(GomObjectData data, string name, string modernId, string legacyId) {
      if (data == null) return null;
      if (!String.IsNullOrWhiteSpace(name) && data.Dictionary.TryGetValue(name, out object value)) return value;
      if (!String.IsNullOrWhiteSpace(modernId) && data.Dictionary.TryGetValue(modernId, out value)) return value;
      if (!String.IsNullOrWhiteSpace(legacyId) && data.Dictionary.TryGetValue(legacyId, out value)) return value;
      return null;
    }

    private static void AppendWorldConversationConditionTokens(List<ulong> target, object raw) {
      if (target == null || raw == null) return;
      foreach (object entry in WorldInteractionListEntries(raw)) {
        ulong token = WorldInteractionUnsigned(entry);
        // Zero is an authored FALSE literal, so it must not be discarded merely because the generic id helper returns 0.
        if (token == 0 && entry != null) {
          try { token = unchecked((ulong)Convert.ToInt64(entry, CultureInfo.InvariantCulture)); } catch { }
        }
        target.Add(token);
      }
    }

    private string WorldConversationConditionSummary(Conversation conversation, long nodeId) {
      if (conversation == null || nodeId == 0) return null;
      CaptureWorldConversationConditions(conversation);
      if (!worldConversationConditionCache.TryGetValue(conversation.Id, out Dictionary<long, WorldConversationConditionData> map) ||
          !map.TryGetValue(nodeId, out WorldConversationConditionData condition) || condition.Condition.Count == 0) return null;
      if (condition.Condition.SequenceEqual(condition.PreviewCondition)) return null;
      return RenderWorldConversationCondition(condition.Condition);
    }

    private string RenderWorldConversationCondition(IReadOnlyList<ulong> tokens) {
      if (tokens == null || tokens.Count == 0) return null;
      var stack = new Stack<string>();
      try {
        for (int i = 0; i < tokens.Count; i++) {
          ulong token = tokens[i];
          if (token == 16) {
            if (++i >= tokens.Count) return WorldConversationConditionFallback(tokens);
            stack.Push(unchecked((long)tokens[i]).ToString(CultureInfo.InvariantCulture));
            continue;
          }
          if (token == 0) { stack.Push("FALSE"); continue; }
          if (token == 1) { stack.Push("TRUE"); continue; }
          if (token == 10) {
            if (stack.Count < 1) return WorldConversationConditionFallback(tokens);
            stack.Push("NOT (" + stack.Pop() + ")");
            continue;
          }
          string op = token == 3 ? "!=" : token == 4 ? ">" : token == 5 ? ">=" : token == 6 ? "<" : token == 7 ? "<=" :
            token == 8 ? "AND" : token == 9 ? "OR" : token == 11 || token == 12 ? "==" : null;
          if (op != null) {
            if (stack.Count < 2) return WorldConversationConditionFallback(tokens);
            string right = stack.Pop(), left = stack.Pop();
            stack.Push("(" + left + " " + op + " " + right + ")");
            continue;
          }
          if (token <= 16) return WorldConversationConditionFallback(tokens);
          stack.Push(WorldConversationConditionVariable(token));
        }
        if (stack.Count != 1) return WorldConversationConditionFallback(tokens);
        string result = stack.Pop();
        if (result.Length > 1 && result[0] == '(' && result[result.Length - 1] == ')') result = result.Substring(1, result.Length - 2);
        return result;
      } catch { return WorldConversationConditionFallback(tokens); }
    }

    private string WorldConversationConditionVariable(ulong token) {
      ulong nodeId = 0xE000000000000000UL | (token >> 16);
      int local = (int)(token & 0xFFFFUL);
      string quest = null;
      try { quest = WorldResolveGomObject(nodeId)?.Name; } catch { }
      if (String.IsNullOrWhiteSpace(quest) && WorldUsesLegacyContent) quest = WorldLegacyPrototypeName(nodeId, "qst");
      if (String.IsNullOrWhiteSpace(quest)) quest = "quest@" + nodeId.ToString(CultureInfo.InvariantCulture);
      string variable = local == 1 ? "is_on_quest" : local == 2 ? "has_completed_quest" : local == 3 ? "has_failed_quest" :
        local == 4 ? "is_eligible_for_quest" : local == 5 ? "experienced_alternate_ending" : local == 88 ? "QUEST_NAME" : "#" + local.ToString(CultureInfo.InvariantCulture);
      return quest + "." + variable;
    }

    private string WorldConversationConditionFallback(IReadOnlyList<ulong> tokens) {
      var values = new List<string>();
      for (int i = 0; i < tokens.Count; i++) {
        ulong token = tokens[i];
        if (token == 16 && i + 1 < tokens.Count) values.Add(unchecked((long)tokens[++i]).ToString(CultureInfo.InvariantCulture));
        else if (token == 0) values.Add("FALSE");
        else if (token == 1) values.Add("TRUE");
        else if (token == 3) values.Add("!="); else if (token == 4) values.Add(">"); else if (token == 5) values.Add(">=");
        else if (token == 6) values.Add("<"); else if (token == 7) values.Add("<="); else if (token == 8) values.Add("AND");
        else if (token == 9) values.Add("OR"); else if (token == 10) values.Add("NOT"); else if (token == 11 || token == 12) values.Add("==");
        else values.Add(token > 16 ? WorldConversationConditionVariable(token) : token.ToString(CultureInfo.InvariantCulture));
      }
      return String.Join(" ", values);
    }
  }
}
