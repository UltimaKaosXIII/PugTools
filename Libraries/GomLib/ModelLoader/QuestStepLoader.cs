using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using GomLib.Models;

namespace GomLib.ModelLoader {
  public class QuestStepLoader {
    public static string ClassName {
      get { return "qstStepDefinition"; }
    }

    readonly DataObjectModel _dom;

    public QuestStepLoader(DataObjectModel dom) {
      _dom = dom;
    }

    public QuestStep Load(GomObjectData obj, QuestBranch branch) {
      QuestStep step = new QuestStep
            {
        Dom_ = _dom,

        Id = (int)obj.ValueOrDefault<long>("qstStepId", 0),
        Branch = branch,
        IsShareable = obj.ValueOrDefault("qstStepIsShareable", false),
        FailTime = obj.ValueOrDefault<long>("qstFailTime", 0),
        HideTimer = obj.ValueOrDefault("qstHideTimer", false)
      };

      var bonusMissions = obj.ValueOrDefault<List<object>>("qstBonusMissions", null);
      step.BonusMissionsIds = QuestLoader.LoadBonusMissions(bonusMissions);

      step.Tasks = new List<QuestTask>();
      var tasks = obj.ValueOrDefault<List<object>>("qstTasks", null);
      if (tasks != null) {
        foreach (var taskDef in tasks) {
          if (taskDef is not GomObjectData taskData) continue;
          try {
            QuestTask tsk = _dom.QuestTaskLoader.Load(taskData, step);
            if (tsk != null) step.Tasks.Add(tsk);
          } catch (Exception ex) {
            System.Diagnostics.Debug.WriteLine($"Quest task failed for {branch?.Quest?.Fqn}: {ex.Message}");
          }
        }
      }

      var stringIds = obj.ValueOrDefault<List<object>>("qstStepJournalEntryStringIdList", null);
      var strings = new List<string>();
      var localizedStrings = new Dictionary<string, List<string>>();
      if (stringIds != null) {
        var txtLookup = branch.Quest.TextLookup;
        foreach (var strId in stringIds) {
          long key;
          try { key = Convert.ToInt64(strId); }
          catch { continue; }

          GomObjectData retriever = null;
          Object rawRetriever = null;
          if (txtLookup != null
              && (txtLookup.TryGetValue(key, out rawRetriever)
                  || txtLookup.TryGetValue(unchecked((ulong)key), out rawRetriever)
                  || txtLookup.TryGetValue(key.ToString(), out rawRetriever)))
            retriever = rawRetriever as GomObjectData;

          Dictionary<string, string> tempStrings = null;
          try {
            if (retriever != null) {
              strings.Add(_dom.StringTable.TryGetString(branch.Quest.Fqn, retriever));
              tempStrings = _dom.StringTable.TryGetLocalizedStrings(branch.Quest.Fqn, retriever);
            } else {
              // Current qstStepJournalEntryStringIdList values are direct str.qst ids.
              // This mirrors Jedipedia's modern quest reader and also keeps old builds
              // working through the retriever path above.
              strings.Add(_dom.StringTable.TryGetString("str.qst", key));
              tempStrings = _dom.StringTable.TryGetLocalizedStrings("str.qst", key);
            }
          } catch {
            // One missing/corrupt localized line should not remove the complete step.
            strings.Add(String.Empty);
          }

          if (tempStrings != null && tempStrings.Count > 0) {
            for (int i = 0; i < tempStrings.Count; i++) {
              if (!localizedStrings.ContainsKey(tempStrings.ElementAt(i).Key)) {
                localizedStrings.Add(tempStrings.ElementAt(i).Key, new List<string>());
              }
              if (tempStrings.ElementAt(i).Value != "") { localizedStrings[tempStrings.ElementAt(i).Key].Add(tempStrings.ElementAt(i).Value); }
            }
          } else {
            if (localizedStrings.Count == 0) {
              localizedStrings.Add("enMale", new List<string>());
              //localizedStrings.Add("enFemale", new List<string>());
              localizedStrings.Add("frMale", new List<string>());
              localizedStrings.Add("frFemale", new List<string>());
              localizedStrings.Add("deMale", new List<string>());
              localizedStrings.Add("deFemale", new List<string>());
            }
            if (!localizedStrings.ContainsKey("enMale")) localizedStrings["enMale"] = new List<string>();
            if (!localizedStrings.ContainsKey("frMale")) localizedStrings["frMale"] = new List<string>();
            if (!localizedStrings.ContainsKey("frFemale")) localizedStrings["frFemale"] = new List<string>();
            if (!localizedStrings.ContainsKey("deMale")) localizedStrings["deMale"] = new List<string>();
            if (!localizedStrings.ContainsKey("deFemale")) localizedStrings["deFemale"] = new List<string>();
            localizedStrings["enMale"].Add(string.Empty);
            //localizedStrings["enFemale"].Add(String.Empty);
            localizedStrings["frMale"].Add(string.Empty);
            localizedStrings["frFemale"].Add(string.Empty);
            localizedStrings["deMale"].Add(string.Empty);
            localizedStrings["deFemale"].Add(string.Empty);
          }
        }
      }

      step.JournalText = string.Join(Environment.NewLine + Environment.NewLine, strings.ToArray());
      step.LocalizedJournalText = localizedStrings.ToDictionary(x => x.Key, x => string.Join(Environment.NewLine + Environment.NewLine, x.Value.ToArray()));

      var itemsGiven = obj.ValueOrDefault<List<object>>("qstItemsGivenOnCompletion", null);
      step.ItemsGiven = _dom.QuestLoader.LoadGivenOrTakenItems(step.Branch.Quest, itemsGiven);

      var itemsTaken = obj.ValueOrDefault<List<object>>("qstItemsTakenOnCompletion", null);
      step.ItemsTaken = _dom.QuestLoader.LoadGivenOrTakenItems(step.Branch.Quest, itemsTaken);

      return step;
    }
  }
}
