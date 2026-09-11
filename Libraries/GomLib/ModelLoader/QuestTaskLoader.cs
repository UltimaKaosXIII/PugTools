using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using GomLib.Models;

namespace GomLib.ModelLoader {
  public class QuestTaskLoader {
    public static string ClassName {
      get { return "qstTaskDefinition"; }
    }

    readonly DataObjectModel _dom;

    public QuestTaskLoader(DataObjectModel dom) {
      _dom = dom;
    }

    public QuestTask Load(GomObjectData obj, QuestStep step) {
      QuestTask task = new QuestTask
            {
        Step = step,
        Id = (int)obj.ValueOrDefault<long>("qstTaskId", 0),
        Dom_ = _dom
      };

      var qstTaskMapNoteList = obj.ValueOrDefault<List<object>>("qstTaskMapNoteList", null);
      if (qstTaskMapNoteList != null)
        task.MapNoteFqnList = qstTaskMapNoteList
          .Select(x => x?.ToString())
          .Where(x => !String.IsNullOrWhiteSpace(x))
          .ToList();
      var bonusMissions = obj.ValueOrDefault<List<object>>("qstBonusMissions", null);
      task.BonusMissionsIds = QuestLoader.LoadBonusMissions(bonusMissions);

      task.CountMax = (int)obj.ValueOrDefault<long>("qstTaskCountMax", 0);
      task.ShowTracking = obj.ValueOrDefault("qstTaskShowTracking", false);
      task.ShowCount = obj.ValueOrDefault("qstTaskShowTrackingCount", false);
      task.Hook = obj.ValueOrDefault<string>("qstHook", null)
        ?? obj.ValueOrDefault<string>("qstHookName", null);
      task.HookId = obj.ValueOrDefault<ulong>("qstHookId", 0);
      task.HookFlags = obj.ValueOrDefault<long>("qstHookFlags", 0);
      task.HideBannerText = obj.ValueOrDefault("qstTaskHideBannerText", false);
      Object stringIdRaw = obj.Dictionary.TryGetValue("qstTaskStringid", out Object currentStringId)
        ? currentStringId : null;
      long stringId = 0;
      if (stringIdRaw != null) {
        try { stringId = Convert.ToInt64(stringIdRaw); } catch { }
      }
      task.StringId = stringId;

      var txtLookup = step.Branch.Quest.TextLookup;
      GomObjectData retriever = null;
      Object rawRetriever = null;
      if (stringId != 0 && txtLookup != null
          && (txtLookup.TryGetValue(stringId, out rawRetriever)
              || txtLookup.TryGetValue(unchecked((ulong)stringId), out rawRetriever)
              || txtLookup.TryGetValue(stringId.ToString(), out rawRetriever)))
        retriever = rawRetriever as GomObjectData;

      if (stringId != 0) {
        try {
          if (retriever != null) {
            task.Text = _dom.StringTable.TryGetString(step.Branch.Quest.Fqn, retriever);
            task.LocalizedString = _dom.StringTable.TryGetLocalizedStrings(step.Branch.Quest.Fqn, retriever);
          } else {
            // Since 7.x this field is commonly serialized as an integer STB address.
            // Jedipedia resolves it directly through str.qst.
            task.Text = _dom.StringTable.TryGetString("str.qst", stringId);
            task.LocalizedString = _dom.StringTable.TryGetLocalizedStrings("str.qst", stringId);
          }
        } catch {
          // Missing one localized string must not drop the whole task/quest from extraction.
          task.Text = String.Empty;
          task.LocalizedString = new Dictionary<String, String>();
        }
        task.LocalizedString = Normalize.Dictionary(task.LocalizedString, task.Text ?? String.Empty);
      } else {
        task.Text = String.Empty;
        task.LocalizedString = Normalize.Dictionary(null, "task_" + task.Id);
      }

      task.TaskQuests = new List<Quest>();
      task.TaskNpcs = new List<Npc>();
      task.TaskQuestIds = new List<UInt64>();
      task.TaskNpcIds = new List<UInt64>();
      task.TaskPlcIds = new List<UInt64>();

      // Current clients can use both qstTaskObjects and qstTaskInteractionObjects on the same
      // task. The old null-coalescing implementation silently ignored the second dictionary.
      Dictionary<Object, Object>[] taskObjectMaps = {
        obj.ValueOrDefault<Dictionary<Object, Object>>("qstTaskObjects", null),
        obj.ValueOrDefault<Dictionary<Object, Object>>("qstTaskInteractionObjects", null),
        obj.ValueOrDefault<Dictionary<Object, Object>>("qstTaskTriggerIdMap", null)
      };
      foreach (Dictionary<Object, Object> qstTaskObjects in taskObjectMaps.Where(x => x != null)) {
        foreach (KeyValuePair<Object, Object> taskObj in qstTaskObjects) {
          UInt64 taskObjectId;
          try { taskObjectId = Convert.ToUInt64(taskObj.Key); } catch { continue; }
          GomObject taskgom = null;
          try { taskgom = _dom.GetObject(taskObjectId); } catch { }
          if (taskgom == null || String.IsNullOrWhiteSpace(taskgom.Name) || taskgom.Name.Length < 3) continue;
          String fqn = taskgom.Name;
          switch (fqn.Substring(0, 3)) {
            case "qst":
              if (!task.TaskQuestIds.Contains(taskObjectId)) task.TaskQuestIds.Add(taskObjectId);
              break;
            case "npc":
              if (!task.TaskNpcIds.Contains(taskObjectId)) task.TaskNpcIds.Add(taskObjectId);
              break;
            case "plc":
              if (!task.TaskPlcIds.Contains(taskObjectId)) task.TaskPlcIds.Add(taskObjectId);
              break;
            case "enc":
              // defeat encounter
              break;
            case "mpn":
              task.MapNoteFqnList ??= new List<String>();
              if (!task.MapNoteFqnList.Contains(fqn)) task.MapNoteFqnList.Add(fqn);
              break;
            case "cos": // overhear conversation
            case "spn":
            case "itm":
            case "TIM": // timer
            default:
              break;
          }
        }
      }

      var itemsGiven = obj.ValueOrDefault<List<object>>("qstItemsGivenOnCompletion", null);
      task.ItemsGiven = _dom.QuestLoader.LoadGivenOrTakenItems(task.Step.Branch.Quest, itemsGiven);

      var itemsTaken = obj.ValueOrDefault<List<object>>("qstItemsTakenOnCompletion", null);
      task.ItemsTaken = _dom.QuestLoader.LoadGivenOrTakenItems(task.Step.Branch.Quest, itemsTaken);

      return task;
    }
  }
}
