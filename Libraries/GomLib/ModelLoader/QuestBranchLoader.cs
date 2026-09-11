using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using GomLib.Models;

namespace GomLib.ModelLoader {
  public class QuestBranchLoader {
    public static string ClassName {
      get { return "qstBranchDefinition"; }
    }

    readonly DataObjectModel _dom;

    public QuestBranchLoader(DataObjectModel dom) {
      _dom = dom;
    }

    public QuestBranch Load(GomObjectData obj, Quest qst) {
      QuestBranch branch = new QuestBranch
            {
        Quest = qst,
        Id = obj.ValueOrDefault<long>("qstBranchId", 0)
      };

      var qstSteps = obj.ValueOrDefault<List<object>>("qstSteps", null);
      branch.Steps = new List<QuestStep>();
      if (qstSteps != null) {
        foreach (var step in qstSteps) {
          if (step is not GomObjectData stepData) continue;
          try {
            QuestStep parsed = _dom.QuestStepLoader.Load(stepData, branch);
            if (parsed != null) branch.Steps.Add(parsed);
          } catch (Exception ex) {
            System.Diagnostics.Debug.WriteLine($"Quest step failed for {qst?.Fqn}: {ex.Message}");
          }
        }
      }

      return branch;
    }
  }
}
