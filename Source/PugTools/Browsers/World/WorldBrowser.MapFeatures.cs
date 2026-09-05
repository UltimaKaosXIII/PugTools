using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;
using System.Windows.Forms;
using FileFormats;
using GomLib;
using GomLib.Models;
using SlimDX;

namespace PugTools {
  public partial class WorldBrowser {
    private static string NormalizeWorldMapnoteFqn(string value) {
      string fqn = (value ?? String.Empty).Trim().Replace('\\', '.').Replace('/', '.').Trim('.');
      if (fqn.StartsWith("server.mpn.", StringComparison.OrdinalIgnoreCase)) fqn = fqn.Substring("server.mpn.".Length);
      if (fqn.StartsWith("mpn.", StringComparison.OrdinalIgnoreCase)) fqn = fqn.Substring(4);
      if (fqn.EndsWith(".mpn", StringComparison.OrdinalIgnoreCase)) fqn = fqn.Substring(0, fqn.Length - 4);
      return fqn.Trim('.');
    }

    private object worldMapQuestReverseCacheDom;
    private bool worldMapQuestReverseCacheBuilt;
    private readonly Dictionary<string, List<ulong>> worldMapQuestReverseCache =
      new Dictionary<string, List<ulong>>(StringComparer.OrdinalIgnoreCase);

    // Shared hit-test ordering for the full map and minimap. Quest markers deliberately outrank service/map-link
    // pins at the same screen coordinate so a click can never fall through into the generic teleport surface.
    internal static int WorldMapNoteHitPriority(AreaMapNote note) {
      if (note == null) return Int32.MinValue;
      if (IsWorldQuestMapNote(note)) return 40;
      string icon = (note.Icon ?? String.Empty).ToLowerInvariant();
      if (note.MapLinkAreaId != 0 || note.MapLinkMapNameSId != 0 || note.MapLinkSubmapNameSId != 0 ||
          icon.Contains("maplink") || icon.Contains("exit")) return 30;
      if (String.Equals(note.ServiceKind, "Taxi", StringComparison.OrdinalIgnoreCase) || icon.Contains("taxi")) return 25;
      if (note.IsSyntheticService) return 20;
      return 10;
    }

    internal static bool IsWorldQuestMapNote(AreaMapNote note) {
      if (note == null) return false;
      string icon = (note.Icon ?? String.Empty).ToLowerInvariant();
      if (icon.Contains("quest") || icon.Contains("mission")) return true;
      return String.Equals(note.Condition, "Quest", StringComparison.OrdinalIgnoreCase);
    }

    private void EnrichWorldMapNoteQuestInfo(AreaMapNote note, MapNote resolved) {
      if (note == null || resolved == null || currentDom == null) return;
      var questIds = new SortedSet<ulong>();
      try {
        if (resolved.References != null && resolved.References.TryGetValue("mpnQuest", out SortedSet<ulong> linked))
          foreach (ulong id in linked) if (id != 0) questIds.Add(id);
      } catch { }
      foreach (ulong questId in questIds) EnrichWorldMapNoteQuestById(note, questId);
    }

    /// <summary>
    /// Crosslinks are not present in every client/cache combination. When a quest icon is actually hovered/clicked,
    /// build a reverse qstTaskMapNoteList index once and use the authored quest data itself as the authoritative link.
    /// Keeping this lazy avoids walking every quest during ordinary AREA loading.
    /// </summary>
    private void EnsureWorldMapNoteQuestInfo(AreaMapNote note) {
      if (note == null || currentDom == null || !IsWorldQuestMapNote(note)) return;
      if (note.QuestIds.Count == 0 && !String.IsNullOrWhiteSpace(note.Fqn)) {
        try {
          MapNote resolved = null;
          string fqn = note.Fqn.Trim().Trim('.');
          foreach (string candidate in new[] { fqn, fqn.StartsWith("mpn.", StringComparison.OrdinalIgnoreCase) ? fqn : "mpn." + fqn }
              .Distinct(StringComparer.OrdinalIgnoreCase)) {
            try { resolved = currentDom.MapNoteLoader.Load(candidate); } catch { resolved = null; }
            if (resolved != null) break;
          }
          if (resolved != null) EnrichWorldMapNoteQuestInfo(note, resolved);
        } catch { }
      }
      if (note.QuestIds.Count == 0) {
        EnsureWorldMapQuestReverseIndex();
        string target = NormalizeWorldMapnoteFqn(note.Fqn);
        if (!String.IsNullOrWhiteSpace(target) && worldMapQuestReverseCache.TryGetValue(target, out List<ulong> ids))
          foreach (ulong id in ids) EnrichWorldMapNoteQuestById(note, id);
      }
    }

    private void EnsureWorldMapQuestReverseIndex() {
      if (currentDom == null) return;
      if (!ReferenceEquals(worldMapQuestReverseCacheDom, currentDom)) {
        worldMapQuestReverseCacheDom = currentDom;
        worldMapQuestReverseCacheBuilt = false;
        worldMapQuestReverseCache.Clear();
      }
      if (worldMapQuestReverseCacheBuilt) return;
      worldMapQuestReverseCacheBuilt = true;
      try {
        foreach (GomObject candidate in WorldObjectsStartingWith("qst.")) {
          if (candidate == null) continue;
          GomObject qstObject = candidate;
          try { if (qstObject.Data == null) qstObject = WorldResolveGomObject(candidate.Id); } catch { }
          if (qstObject?.Data == null) continue;
          foreach (GomObjectData branch in WorldInteractionListEntries(WorldInteractionDataValue(qstObject.Data, "qstBranches", "4611686039996770003")).OfType<GomObjectData>()) {
            foreach (GomObjectData step in WorldInteractionListEntries(WorldInteractionDataValue(branch, "qstSteps", "4611686039996770001")).OfType<GomObjectData>()) {
              foreach (GomObjectData task in WorldInteractionListEntries(WorldInteractionDataValue(step, "qstTasks", "4611686039996770000")).OfType<GomObjectData>()) {
                foreach (string rawMpn in WorldInteractionStrings(WorldInteractionDataValue(task, "qstTaskMapNoteList", "4611686038776181190"))) {
                  string key = NormalizeWorldMapnoteFqn(rawMpn);
                  if (String.IsNullOrWhiteSpace(key)) continue;
                  if (!worldMapQuestReverseCache.TryGetValue(key, out List<ulong> list)) {
                    list = new List<ulong>();
                    worldMapQuestReverseCache[key] = list;
                  }
                  if (!list.Contains(qstObject.Id)) list.Add(qstObject.Id);
                }
              }
            }
          }
        }
      } catch (Exception ex) {
        System.Diagnostics.Debug.WriteLine("Quest/mapnote reverse index failed: " + ex.Message);
      }
    }

    private void EnrichWorldMapNoteQuestById(AreaMapNote note, ulong questId) {
      if (note == null || questId == 0 || currentDom == null) return;
      if (!note.QuestIds.Contains(questId)) note.QuestIds.Add(questId);
      try {
        GomObject qstObject = WorldResolveGomObject(questId);
        if (qstObject?.Data == null) return;
        string questName = ResolveWorldQuestName(qstObject);
        if (!String.IsNullOrWhiteSpace(questName) && !note.QuestNames.Contains(questName, StringComparer.OrdinalIgnoreCase))
          note.QuestNames.Add(questName.Trim());
        string targetMpn = NormalizeWorldMapnoteFqn(note.Fqn);
        Dictionary<object, object> textLookup = WorldInteractionMap(WorldInteractionDataValue(qstObject.Data, "locTextRetrieverMap", "4611686102842470023"));
        foreach (GomObjectData branch in WorldInteractionListEntries(WorldInteractionDataValue(qstObject.Data, "qstBranches", "4611686039996770003")).OfType<GomObjectData>()) {
          foreach (GomObjectData step in WorldInteractionListEntries(WorldInteractionDataValue(branch, "qstSteps", "4611686039996770001")).OfType<GomObjectData>()) {
            foreach (GomObjectData task in WorldInteractionListEntries(WorldInteractionDataValue(step, "qstTasks", "4611686039996770000")).OfType<GomObjectData>()) {
              string[] taskMapnotes = WorldInteractionStrings(WorldInteractionDataValue(task, "qstTaskMapNoteList", "4611686038776181190"));
              if (!taskMapnotes.Any(x => String.Equals(NormalizeWorldMapnoteFqn(x), targetMpn, StringComparison.OrdinalIgnoreCase))) continue;
              string objective = ResolveWorldQuestTaskText(qstObject.Name, task, textLookup);
              if (!String.IsNullOrWhiteSpace(objective) && !note.QuestObjectives.Contains(objective, StringComparer.OrdinalIgnoreCase))
                note.QuestObjectives.Add(objective.Trim());
            }
          }
        }
      } catch (Exception ex) {
        System.Diagnostics.Debug.WriteLine("Map-note quest enrichment failed (" + questId.ToString(CultureInfo.InvariantCulture) + "): " + ex.Message);
      }
    }

    private string ResolveWorldQuestName(GomObject qstObject) {
      if (qstObject?.Data == null || currentDom?.StringTable == null) return qstObject?.Name;
      try {
        object rawMap = WorldInteractionDataValue(qstObject.Data, "locTextRetrieverMap", "4611686102842470023");
        if (rawMap is Dictionary<object, object> textLookup &&
            TryWorldMapnoteInt64(WorldInteractionDataValue(qstObject.Data, "qstQuestDefinitionGUID", "4611686019167675410"), out long guid)) {
          long nameId = unchecked(guid + 0x58L);
          object retriever = WorldQuestTextLookup(textLookup, nameId);
          if (retriever is GomObjectData lookup) {
            string text = currentDom.StringTable.TryGetString(qstObject.Name, lookup);
            if (!String.IsNullOrWhiteSpace(text)) return text.Trim();
          }
        }
      } catch { }
      try {
        Quest quest = currentDom.QuestLoader.Load(qstObject.Id);
        string text = quest?.ToXElement(false)?.Element("Name")?.Value;
        if (!String.IsNullOrWhiteSpace(text)) return text.Trim();
      } catch { }
      return qstObject.Name;
    }

    private static object WorldQuestTextLookup(Dictionary<object, object> map, long key) {
      if (map == null) return null;
      if (map.TryGetValue(key, out object value)) return value;
      ulong unsigned = unchecked((ulong)key);
      if (map.TryGetValue(unsigned, out value)) return value;
      string text = key.ToString(CultureInfo.InvariantCulture);
      if (map.TryGetValue(text, out value)) return value;
      return null;
    }

    private string ResolveWorldQuestString(string questFqn, Dictionary<object, object> textLookup, object rawId) {
      if (textLookup == null || currentDom?.StringTable == null || !TryWorldMapnoteInt64(rawId, out long id)) return null;
      if (WorldQuestTextLookup(textLookup, id) is not GomObjectData lookup) return null;
      try { return currentDom.StringTable.TryGetString(questFqn, lookup); } catch { return null; }
    }

    private string ResolveWorldQuestTaskText(string questFqn, GomObjectData task, Dictionary<object, object> textLookup) {
      if (task == null) return null;
      return ResolveWorldQuestString(questFqn, textLookup, WorldInteractionDataValue(task, "qstTaskStringid", "4611686019157990836"));
    }

    private string ResolveWorldQuestStepText(string questFqn, GomObjectData step, Dictionary<object, object> textLookup) {
      var parts = new List<string>();
      foreach (object rawId in WorldInteractionListEntries(WorldInteractionDataValue(step, "qstStepJournalEntryStringIdList", "4611686245133230001"))) {
        string text = ResolveWorldQuestString(questFqn, textLookup, rawId);
        if (!String.IsNullOrWhiteSpace(text)) parts.Add(text.Trim());
      }
      return String.Join(Environment.NewLine + Environment.NewLine, parts);
    }

    private static bool TryWorldMapnoteInt64(object value, out long result) {
      result = 0;
      if (value == null) return false;
      try {
        switch (value) {
          case long l: result = l; return true;
          case ulong u: result = unchecked((long)u); return true;
          case int i: result = i; return true;
          case uint ui: result = ui; return true;
        }
        string text = value.ToString()?.Trim();
        if (Int64.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out result)) return true;
        if (UInt64.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong parsed)) { result = unchecked((long)parsed); return true; }
      } catch { }
      return false;
    }

    private static long WorldQuestLong(GomObjectData data, string name, string numericName) {
      return TryWorldMapnoteInt64(WorldInteractionDataValue(data, name, numericName), out long value) ? value : 0L;
    }

    internal bool TryShowWorldMapQuestDetails(AreaMapNote note) {
      if (note == null || !IsWorldQuestMapNote(note)) return false;
      EnsureWorldMapNoteQuestInfo(note);
      if (note.QuestIds == null || note.QuestIds.Count == 0) return false;
      ShowWorldMapQuestDetails(note);
      return true;
    }

    private void ShowWorldMapQuestDetails(AreaMapNote note) {
      bool de = (GomLib.StringTable.SelectedLocalization ?? String.Empty).StartsWith("de", StringComparison.OrdinalIgnoreCase);
      bool fr = (GomLib.StringTable.SelectedLocalization ?? String.Empty).StartsWith("fr", StringComparison.OrdinalIgnoreCase);
      string windowTitle = de ? "Questdetails" : fr ? "Détails de quête" : "Quest details";
      var form = new Form {
        Text = windowTitle,
        StartPosition = FormStartPosition.CenterParent,
        Size = new Size(920, 650),
        MinimumSize = new Size(680, 440),
        BackColor = Color.FromArgb(8, 24, 30),
        ForeColor = Color.Gainsboro,
        ShowIcon = false,
        ShowInTaskbar = false
      };
      var header = new Label {
        Dock = DockStyle.Top,
        Height = 38,
        Padding = new Padding(10, 8, 8, 4),
        ForeColor = Color.FromArgb(255, 190, 58),
        BackColor = form.BackColor,
        Font = new Font(Font, FontStyle.Bold),
        AutoEllipsis = true,
        Text = note.QuestNames.FirstOrDefault() ?? WorldMapNoteDisplayName(note)
      };
      var split = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 330, BackColor = form.BackColor };
      var tree = new TreeView {
        Dock = DockStyle.Fill,
        BackColor = Color.FromArgb(7, 27, 34),
        ForeColor = Color.Gainsboro,
        BorderStyle = BorderStyle.FixedSingle,
        HideSelection = false,
        FullRowSelect = true
      };
      var details = new RichTextBox {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        BorderStyle = BorderStyle.FixedSingle,
        BackColor = Color.FromArgb(7, 27, 34),
        ForeColor = Color.Gainsboro,
        Font = new Font("Segoe UI", 9.5f),
        DetectUrls = false
      };
      split.Panel1.Controls.Add(tree);
      split.Panel2.Controls.Add(details);
      var close = new Button { Dock = DockStyle.Bottom, Height = 34, Text = de ? "Schließen" : fr ? "Fermer" : "Close", FlatStyle = FlatStyle.Flat };
      close.Click += (_, __) => form.Close();
      form.Controls.Add(split);
      form.Controls.Add(header);
      form.Controls.Add(close);

      foreach (ulong questId in note.QuestIds.Distinct()) {
        TreeNode root = BuildWorldQuestDetailTree(questId, note, out string fullText, out string name);
        if (root == null) continue;
        root.Tag = fullText;
        tree.Nodes.Add(root);
        if (tree.Nodes.Count == 1) {
          tree.SelectedNode = root;
          details.Text = fullText;
          if (!String.IsNullOrWhiteSpace(name)) header.Text = name;
        }
      }
      tree.AfterSelect += (_, e) => { if (e.Node?.Tag is string text) details.Text = text; };
      tree.ExpandAll();
      form.ShowDialog(this);
    }

    private TreeNode BuildWorldQuestDetailTree(ulong questId, AreaMapNote clickedNote, out string fullText, out string questName) {
      fullText = String.Empty; questName = null;
      if (currentDom == null || questId == 0) return null;
      GomObject qstObject;
      try { qstObject = WorldResolveGomObject(questId); } catch { qstObject = null; }
      if (qstObject?.Data == null) return null;
      questName = ResolveWorldQuestName(qstObject) ?? qstObject.Name;
      Dictionary<object, object> textLookup = WorldInteractionMap(WorldInteractionDataValue(qstObject.Data, "locTextRetrieverMap", "4611686102842470023"));
      var all = new System.Text.StringBuilder();
      all.AppendLine(questName).AppendLine(qstObject.Name).Append("ID: ").AppendLine(questId.ToString(CultureInfo.InvariantCulture));
      long requiredLevel = WorldQuestLong(qstObject.Data, "qstReqMinLevel", "4611686019157990631");
      long xpLevel = WorldQuestLong(qstObject.Data, "qstXpLevel", "4611686021337056540");
      if (requiredLevel > 0) all.Append("Required level: ").AppendLine(requiredLevel.ToString(CultureInfo.InvariantCulture));
      if (xpLevel > 0) all.Append("Quest level: ").AppendLine(xpLevel.ToString(CultureInfo.InvariantCulture));
      all.AppendLine();

      var root = new TreeNode(questName ?? qstObject.Name);
      int branchNumber = 0;
      foreach (GomObjectData branch in WorldInteractionListEntries(WorldInteractionDataValue(qstObject.Data, "qstBranches", "4611686039996770003")).OfType<GomObjectData>()) {
        branchNumber++;
        long branchId = WorldQuestLong(branch, "qstBranchId", "4611686019241777060");
        string branchLabel = "Branch " + branchNumber.ToString(CultureInfo.InvariantCulture) + (branchId != 0 ? " (#" + branchId.ToString(CultureInfo.InvariantCulture) + ")" : String.Empty);
        var branchNode = new TreeNode(branchLabel);
        var branchText = new System.Text.StringBuilder(branchLabel).AppendLine();
        int stepNumber = 0;
        foreach (GomObjectData step in WorldInteractionListEntries(WorldInteractionDataValue(branch, "qstSteps", "4611686039996770001")).OfType<GomObjectData>()) {
          stepNumber++;
          long stepId = WorldQuestLong(step, "qstStepId", "4611686019241777922");
          string journal = ResolveWorldQuestStepText(qstObject.Name, step, textLookup);
          string stepLabel = "Step " + stepNumber.ToString(CultureInfo.InvariantCulture) + (stepId != 0 ? " (#" + stepId.ToString(CultureInfo.InvariantCulture) + ")" : String.Empty);
          if (!String.IsNullOrWhiteSpace(journal)) stepLabel += " — " + journal.Replace("\r", " ").Replace("\n", " ").Trim();
          var stepNode = new TreeNode(stepLabel);
          var stepText = new System.Text.StringBuilder(stepLabel).AppendLine();
          if (!String.IsNullOrWhiteSpace(journal)) stepText.AppendLine().AppendLine(journal.Trim());
          int taskNumber = 0;
          foreach (GomObjectData task in WorldInteractionListEntries(WorldInteractionDataValue(step, "qstTasks", "4611686039996770000")).OfType<GomObjectData>()) {
            taskNumber++;
            long taskId = WorldQuestLong(task, "qstTaskId", "4611686019711221702");
            string taskText = ResolveWorldQuestTaskText(qstObject.Name, task, textLookup);
            long countMax = WorldQuestLong(task, "qstTaskCountMax", "4611686040042870000");
            string taskLabel = !String.IsNullOrWhiteSpace(taskText) ? taskText.Trim() : "Task " + taskNumber.ToString(CultureInfo.InvariantCulture);
            if (countMax > 1) taskLabel += " (" + countMax.ToString(CultureInfo.InvariantCulture) + ")";
            var taskNode = new TreeNode(taskLabel);
            var taskDetails = new System.Text.StringBuilder();
            taskDetails.Append("Task ").Append(taskNumber.ToString(CultureInfo.InvariantCulture));
            if (taskId != 0) taskDetails.Append(" (#").Append(taskId.ToString(CultureInfo.InvariantCulture)).Append(')');
            taskDetails.AppendLine();
            if (!String.IsNullOrWhiteSpace(taskText)) taskDetails.AppendLine().AppendLine(taskText.Trim());
            string[] mapnotes = WorldInteractionStrings(WorldInteractionDataValue(task, "qstTaskMapNoteList", "4611686038776181190"));
            if (mapnotes.Length > 0) {
              taskDetails.AppendLine("Map notes:");
              foreach (string mpn in mapnotes) {
                bool current = clickedNote != null && String.Equals(NormalizeWorldMapnoteFqn(mpn), NormalizeWorldMapnoteFqn(clickedNote.Fqn), StringComparison.OrdinalIgnoreCase);
                taskDetails.Append(current ? "  → " : "  • ").AppendLine(mpn);
              }
            }
            taskNode.Tag = taskDetails.ToString().TrimEnd();
            stepNode.Nodes.Add(taskNode);
            stepText.Append("  • ").AppendLine(taskLabel);
          }
          stepNode.Tag = stepText.ToString().TrimEnd();
          branchNode.Nodes.Add(stepNode);
          branchText.AppendLine().AppendLine(stepText.ToString().TrimEnd());
          all.AppendLine(stepText.ToString().TrimEnd()).AppendLine();
        }
        branchNode.Tag = branchText.ToString().TrimEnd();
        root.Nodes.Add(branchNode);
      }
      fullText = all.ToString().TrimEnd();
      return root;
    }

    private void BuildWorldServiceMapNotes() {
      if (area?.MapNotes == null) return;
      // A freshly loaded Area owns the authored notes, so synthetic entries only need to be removed when the method
      // is deliberately rerun after a population refresh in the same area.
      area.MapNotes.RemoveAll(x => x?.IsSyntheticService == true);
      var dedupe = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

      foreach (WorldNpcPlacement npc in (worldNpcPlacements != null ? (IEnumerable<WorldNpcPlacement>)worldNpcPlacements : Enumerable.Empty<WorldNpcPlacement>())) {
        if (npc?.Interaction == null || npc.Instance == null || npc.Room == null) continue;
        if (!TryWorldMapPlacementPosition(npc.Room, npc.Instance, npc.SpawnPoints, out Vector3 position)) continue;
        string label = !String.IsNullOrWhiteSpace(npc.Name) ? npc.Name.Trim() : PrettySpawnName(npc.SourceFqn);
        if (npc.Interaction.Kind == WorldInteractionKind.Conversation && TryBuildQuestGiverMapNote(dedupe, npc, label, position)) continue;
        if (!TryWorldServiceMapIcon(npc.Interaction.Kind, out string icon, out string serviceKind)) continue;
        AddWorldServiceMapNote(dedupe, "npc", npc.Instance.ID, npc.SourceFqn, label, icon, serviceKind, position);
      }
      foreach (WorldSpnPlacement spn in (worldSpnPlacements != null ? (IEnumerable<WorldSpnPlacement>)worldSpnPlacements : Enumerable.Empty<WorldSpnPlacement>())) {
        if (spn?.Interaction == null || spn.Instance == null || spn.Room == null) continue;
        if (!TryWorldServiceMapIcon(spn.Interaction.Kind, out string icon, out string serviceKind)) continue;
        if (!TryWorldMapPlacementPosition(spn.Room, spn.Instance, spn.SpawnPoints, out Vector3 position)) continue;
        string label = !String.IsNullOrWhiteSpace(spn.Name) ? spn.Name.Trim() : PrettySpawnName(spn.SourceFqn);
        long wonkaPackageId = spn.Interaction.Kind == WorldInteractionKind.Wonkavator
          ? (spn.Interaction.WonkaPackageId != 0 ? spn.Interaction.WonkaPackageId : spn.WonkaPackageId)
          : 0;
        AddWorldServiceMapNote(dedupe, "spn", spn.Instance.ID, spn.SourceFqn, label, icon, serviceKind, position, wonkaPackageId);
      }
    }


    private bool TryBuildQuestGiverMapNote(HashSet<string> dedupe, WorldNpcPlacement npc, string label, Vector3 position) {
      if (npc?.Interaction == null || currentDom == null) return false;
      GomObject conversation = null;
      try {
        if (npc.Interaction.ConversationId != 0) conversation = WorldResolveGomObject(npc.Interaction.ConversationId);
        if (conversation == null && !String.IsNullOrWhiteSpace(npc.Interaction.Conversation)) conversation = WorldResolveGomObject(npc.Interaction.Conversation);
      } catch { }
      if (conversation?.References == null || !conversation.References.TryGetValue("startsQuest", out SortedSet<ulong> questIds) || questIds.Count == 0) return false;
      bool exploration = false;
      var questNames = new List<string>();
      foreach (ulong questId in questIds) {
        try {
          Quest quest = currentDom.QuestLoader.Load(questId);
          string questName = null;
          try { questName = quest?.ToXElement(false)?.Element("Name")?.Value; } catch { }
          if (!String.IsNullOrWhiteSpace(questName)) questNames.Add(questName.Trim());
          string qfqn = quest?.Fqn ?? WorldResolveGomObject(questId)?.Name ?? String.Empty;
          if (qfqn.IndexOf("exploration", StringComparison.OrdinalIgnoreCase) >= 0 || qfqn.IndexOf(".explore.", StringComparison.OrdinalIgnoreCase) >= 0) exploration = true;
        } catch { }
      }
      string icon = exploration ? "ExplorationQuest" : "Quest";
      string serviceKind = exploration ? "ExplorationQuest" : "QuestGiver";
      string identity = serviceKind + "|" + Math.Round(position.X, 1).ToString(CultureInfo.InvariantCulture) + "|" + Math.Round(position.Z, 1).ToString(CultureInfo.InvariantCulture);
      if (!dedupe.Add(identity)) return true;
      var note = new AreaMapNote {
        Id = "service:npc:" + npc.Instance.ID.ToString(CultureInfo.InvariantCulture) + ":" + serviceKind,
        Fqn = npc.SourceFqn,
        Label = label,
        Icon = icon,
        Position = position,
        IsSyntheticService = true,
        ServiceKind = serviceKind
      };
      foreach (ulong id in questIds) note.QuestIds.Add(id);
      foreach (string name in questNames.Distinct(StringComparer.OrdinalIgnoreCase)) note.QuestNames.Add(name);
      area.MapNotes.Add(note);
      return true;
    }

    private void AddWorldServiceMapNote(HashSet<string> dedupe, string sourceType, ulong instanceId, string fqn,
        string label, string icon, string serviceKind, Vector3 position, long wonkaPackageId = 0) {
      if (area?.MapNotes == null || String.IsNullOrWhiteSpace(icon)) return;
      string identity = serviceKind + "|" + Math.Round(position.X, 1).ToString(CultureInfo.InvariantCulture) + "|" + Math.Round(position.Z, 1).ToString(CultureInfo.InvariantCulture);
      if (!dedupe.Add(identity)) return;
      // Avoid drawing a second taxi/bindpoint/elevator symbol directly on top of an authored mpn.* note.
      if ((serviceKind == "Taxi" || serviceKind == "QuickTravel" || serviceKind == "Wonkavator") &&
          area.MapNotes.Any(n => n != null && !n.IsSyntheticService && WorldMapNoteIconEquivalent(n.Icon, icon) && HorizontalMapDistanceSquared(n.Position, position) < 2.25f)) return;
      area.MapNotes.Add(new AreaMapNote {
        Id = "service:" + sourceType + ":" + instanceId.ToString(CultureInfo.InvariantCulture) + ":" + serviceKind,
        Fqn = fqn,
        Label = label,
        Icon = icon,
        Position = position,
        IsSyntheticService = true,
        ServiceKind = serviceKind,
        WonkaPackageId = wonkaPackageId
      });
    }

    private static float HorizontalMapDistanceSquared(Vector3 a, Vector3 b) {
      float dx = a.X - b.X, dz = a.Z - b.Z; return dx * dx + dz * dz;
    }

    private static bool WorldMapNoteIconEquivalent(string a, string b) {
      string x = (a ?? String.Empty).ToLowerInvariant(), y = (b ?? String.Empty).ToLowerInvariant();
      if ((x.Contains("taxi") && y.Contains("taxi")) || (x.Contains("wonka") && y.Contains("wonka"))) return true;
      return (x.Contains("bind") || x.Contains("quicktravel")) && (y.Contains("bind") || y.Contains("quicktravel"));
    }

    private static bool TryWorldMapPlacementPosition(FileFormats.Room room, FileFormats.AssetInstance instance, List<WorldSpawnPointPose> spawnPoints, out Vector3 position) {
      position = Vector3.Zero;
      if (spawnPoints != null && spawnPoints.Count > 0 && spawnPoints[0] != null) { position = spawnPoints[0].Position; return IsWorldMapFinite(position); }
      try {
        Matrix world = instance.GetAbsoluteTransform(room);
        position = new Vector3(world.M41, world.M42, world.M43);
        return IsWorldMapFinite(position);
      } catch { return false; }
    }

    private static bool IsWorldMapFinite(Vector3 p) => !Single.IsNaN(p.X) && !Single.IsNaN(p.Y) && !Single.IsNaN(p.Z) &&
      !Single.IsInfinity(p.X) && !Single.IsInfinity(p.Y) && !Single.IsInfinity(p.Z);

    private static bool TryWorldServiceMapIcon(WorldInteractionKind kind, out string icon, out string serviceKind) {
      icon = null; serviceKind = kind.ToString();
      switch (kind) {
        case WorldInteractionKind.Vendor: icon = "Vendor"; return true;
        case WorldInteractionKind.Taxi: icon = "Taxi"; return true;
        case WorldInteractionKind.QuickTravel: icon = "BindPoint"; return true;
        case WorldInteractionKind.ProfessionTrainer: icon = "CrewTrainer"; return true;
        case WorldInteractionKind.ClassTrainer: icon = "ClassTrainer"; return true;
        case WorldInteractionKind.Harvest: icon = "ResourceNode"; return true;
        case WorldInteractionKind.Mailbox: icon = "Mailbox"; return true;
        case WorldInteractionKind.EnhancementStation: icon = "EnhancementStation"; return true;
        case WorldInteractionKind.Bank: icon = "Bank"; return true;
        case WorldInteractionKind.GuildBank: icon = "GuildBank"; return true;
        case WorldInteractionKind.AuctionHouse: icon = "AuctionHouse"; return true;
        case WorldInteractionKind.MissionBoard: icon = "MissionBoard"; return true;
        case WorldInteractionKind.Wonkavator: icon = "Wonkavator"; return true;
        default: return false;
      }
    }
  }
}
