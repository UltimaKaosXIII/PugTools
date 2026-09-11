using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using Newtonsoft.Json;

namespace GomLib.Models {
  public class QuestTask : IEquatable<QuestTask> {
    [JsonIgnore]
    public QuestStep Step { get; set; }
    public int Id { get; set; }
    public int DbId { get; set; }
    [JsonIgnore]
    public DataObjectModel Dom_ { get; set; }

    public string Text { get; set; }
    public Dictionary<string, string> LocalizedString { get; set; }
    public string Hook { get; set; }
    public ulong HookId { get; set; }
    public long HookFlags { get; set; }
    public bool HideBannerText { get; set; }
    public long StringId { get; set; }

    public bool ShowTracking { get; set; }
    public bool ShowCount { get; set; }
    public int CountMax { get; set; }

    public List<ulong> TaskQuestIds { get; set; }
    public List<string> TaskQuestB62Ids {
      get {
        if (TaskQuestIds == null) return new List<string>();
        return TaskQuestIds.Select(x => x.ToMaskedBase62()).ToList();
      }
    }
    public List<ulong> TaskNpcIds { get; set; }
    public List<string> TaskNpcB62Ids {
      get {
        if (TaskNpcIds == null) return new List<string>();
        return TaskNpcIds.Select(x => x.ToMaskedBase62()).ToList();
      }
    }
    public List<ulong> TaskPlcIds { get; set; }
    public List<string> TaskPlcB62Ids {
      get {
        if (TaskPlcIds == null) return new List<string>();
        return TaskPlcIds.Select(x => x.ToMaskedBase62()).ToList();
      }
    }
    [JsonIgnore]
    public List<Quest> TaskQuests { get; set; }
    [JsonIgnore]
    public List<Npc> TaskNpcs { get; set; }

    [JsonIgnore]
    public List<string> MapNoteFqnList { get; set; }
    public List<string> MapNoteB62Ids {
      get {
        if (MapNoteFqnList == null) return new List<string>();
        return MapNoteFqnList.Select(x => Dom_.GetObjectId(x).ToMaskedBase62()).ToList();
      }
    }

    [JsonIgnore]
    public List<Quest> BonusMissions {
      get {
        var bMissions = new List<Quest>();
        foreach (var bonMisId in BonusMissionsIds ?? new List<ulong>()) {
          if (bonMisId != 0) {
            var qst = Dom_.QuestLoader.Load(bonMisId);
            if (qst.Fqn != null)
              bMissions.Add(qst);
          }
        }
        return bMissions;
      }
    }
    public List<ulong> BonusMissionsIds { get; set; }
    public List<QuestItem> ItemsGiven { get; set; }
    public List<QuestItem> ItemsTaken { get; set; }

    public override int GetHashCode() {
      int hash = Id.GetHashCode();
      if (Text != null) { hash ^= Text.TrimEnd().GetHashCode(); }
      if (Hook != null) { hash ^= Hook.GetHashCode(); }
      if (MapNoteFqnList != null) foreach (var mpn in MapNoteFqnList) { hash ^= mpn.GetHashCode(); }
      hash ^= ShowTracking.GetHashCode();
      hash ^= ShowCount.GetHashCode();
      hash ^= CountMax.GetHashCode();
      return hash;
    }

    public override bool Equals(object obj) {
      if (obj == null) return false;

      if (ReferenceEquals(this, obj)) return true;

      if (obj is not QuestTask qts) return false;

      return Equals(qts);
    }

    public bool Equals(QuestTask qts) {
      if (qts == null) return false;

      if (ReferenceEquals(this, qts)) return true;

      if (BonusMissionsIds != null) {
        if (qts.BonusMissionsIds == null) {
          return false;
        } else {
          if (!Enumerable.SequenceEqual(BonusMissionsIds, qts.BonusMissionsIds))
            return false;
        }
      }
      if (CountMax != qts.CountMax)
        return false;
      if (DbId != qts.DbId)
        return false;
      if (Hook != null) {
        if (qts.Hook == null)
          return false;
        else {
          if (!Hook.Equals(qts.Hook))
            return false;
        }
      }
      if (Id != qts.Id)
        return false;
      if (ItemsGiven != null) {
        if (qts.ItemsGiven == null) {
          return false;
        } else {
          if (!Enumerable.SequenceEqual(ItemsGiven, qts.ItemsGiven))
            return false;
        }
      }
      if (ItemsTaken != null) {
        if (qts.ItemsTaken == null) {
          return false;
        } else {
          if (!Enumerable.SequenceEqual(ItemsTaken, qts.ItemsTaken))
            return false;
        }
      }

      var ssComp = new DictionaryComparer<string, string>();
      if (!ssComp.Equals(LocalizedString, qts.LocalizedString))
        return false;

      if (ShowCount != qts.ShowCount)
        return false;
      if (ShowTracking != qts.ShowTracking)
        return false;
      if (Text != qts.Text)
        return false;

      if (!Enumerable.SequenceEqual(TaskNpcIds ?? new List<ulong>(), qts.TaskNpcIds ?? new List<ulong>()))
        return false;
      if (!Enumerable.SequenceEqual(TaskQuestIds ?? new List<ulong>(), qts.TaskQuestIds ?? new List<ulong>()))
        return false;
      if (!Enumerable.SequenceEqual(TaskPlcIds ?? new List<ulong>(), qts.TaskPlcIds ?? new List<ulong>()))
        return false;
      if (!Enumerable.SequenceEqual(MapNoteFqnList ?? new List<string>(), qts.MapNoteFqnList ?? new List<string>()))
        return false;
      if (HookId != qts.HookId || HookFlags != qts.HookFlags || HideBannerText != qts.HideBannerText || StringId != qts.StringId)
        return false;

      return true;
    }

    public XElement ToXElement(bool verbose) {
      XElement taskNode = new XElement("Task",
                new XAttribute("Id", Id));
      //new XAttribute("DBId", DbId)); //this is always 0
      if (Text != null)
        taskNode.Add(new XElement("String", Text));
      else
        taskNode.Add(new XElement("String"));

      taskNode.Add(new XElement("CountMax", CountMax));

      Quest.QuestItemsGivenOrTakenToXElement(taskNode, ItemsGiven, ItemsTaken);

      taskNode.Add(new XElement("BonusMissions"));
      foreach (UInt64 bonusId in BonusMissionsIds ?? new List<ulong>()) {
        if (bonusId == 0) continue;
        String fqn = Dom_?.GetStoredTypeName(bonusId);
        taskNode.Element("BonusMissions").Add(
          new XElement("Quest", new XAttribute("Id", bonusId),
            String.IsNullOrWhiteSpace(fqn) ? null : new XAttribute("Fqn", fqn)));
      }

      if (verbose) {
        taskNode.Add(new XElement("Hook", Hook),
            new XElement("HookId", HookId),
            new XElement("HookFlags", HookFlags),
            new XElement("StringId", StringId),
            new XElement("HideBannerText", HideBannerText),
            new XElement("ShowCount", ShowCount),
            new XElement("ShowTracking", ShowTracking));
        XElement taskNpcs = new XElement("TaskNpcs");
        foreach (var npc in TaskNpcs ?? new List<Npc>()) {
          if (Step.Branch.Quest.m_loadedNpcs.ContainsKey(npc.Fqn)) {
            taskNpcs.Add(Step.Branch.Quest.m_loadedNpcs[npc.Fqn]);
          } else {
            XElement taskNpc = npc.ToXElement(verbose);
            taskNpcs.Add(taskNpc); //add task npc to task npcs
          }
        }
        foreach (UInt64 npcId in TaskNpcIds ?? new List<ulong>()) {
          String fqn = Dom_?.GetStoredTypeName(npcId);
          if (!String.IsNullOrWhiteSpace(fqn) && !taskNpcs.Elements("NpcRef").Any(x => (String)x.Attribute("Fqn") == fqn))
            taskNpcs.Add(new XElement("NpcRef", new XAttribute("Id", npcId), new XAttribute("Fqn", fqn)));
        }
        foreach (UInt64 plcId in TaskPlcIds ?? new List<ulong>()) {
          String fqn = Dom_?.GetStoredTypeName(plcId);
          taskNpcs.Add(new XElement("PlaceableRef", new XAttribute("Id", plcId),
            String.IsNullOrWhiteSpace(fqn) ? null : new XAttribute("Fqn", fqn)));
        }
        foreach (String mapNote in MapNoteFqnList ?? new List<String>())
          taskNpcs.Add(new XElement("MapNoteRef", new XAttribute("Fqn", mapNote)));
        taskNode.Add(taskNpcs); //add task npcs/placeables/map notes to task
        XElement taskQuests = new XElement("TaskQuests");
        foreach (var quest in TaskQuests ?? new List<Quest>()) {
          XElement taskQuest = quest.ToXElement(verbose);
          taskQuests.Add(taskQuest); //add task quest to task quests
        }
        foreach (UInt64 questId in TaskQuestIds ?? new List<ulong>()) {
          String fqn = Dom_?.GetStoredTypeName(questId);
          if (!String.IsNullOrWhiteSpace(fqn) && !taskQuests.Elements("QuestRef").Any(x => (String)x.Attribute("Fqn") == fqn))
            taskQuests.Add(new XElement("QuestRef", new XAttribute("Id", questId), new XAttribute("Fqn", fqn)));
        }
        taskNode.Add(taskQuests); //add task quests to task
      }
      return taskNode;
    }
  }
}
