using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

using GomLib;
using GomLib.Models;

namespace PugTools {
  internal partial class NodeBrowser {
    private LinkLabel _nodeReferencesHeader;
    private ListView _nodeReferencesList;
    private Boolean _nodeReferencesExpanded;
    private Task<Dictionary<UInt64, List<NodeReverseReference>>> _nodeReferenceIndexTask;
    private Dictionary<UInt64, List<NodeReverseReference>> _nodeReverseReferenceIndex;
    private readonly Object _nodeReferenceIndexLock = new Object();
    private UInt64 _nodeReferencesCurrentId;

    private sealed class NodeReverseReference {
      public String SourceFqn { get; init; }
      public String SourceClass { get; init; }
      public String FieldPath { get; init; }
    }

    private void InitializeNodeReferenceUi() {
      if (_nodePreviewInfoPanel == null || _nodeReferencesHeader != null) return;

      _nodeReferencesHeader = new LinkLabel {
        AutoSize = false,
        Height = 22,
        Text = "Referenced by: preparing local index…",
        TextAlign = ContentAlignment.MiddleLeft,
        LinkBehavior = LinkBehavior.HoverUnderline,
        Visible = true
      };
      _nodeReferencesHeader.LinkClicked += delegate {
        if (_nodeReverseReferenceIndex == null) return;
        if (!_nodeReverseReferenceIndex.TryGetValue(_nodeReferencesCurrentId, out List<NodeReverseReference> refs)
            || refs == null || refs.Count == 0) return;
        _nodeReferencesExpanded = !_nodeReferencesExpanded;
        _nodeReferencesList.Visible = _nodeReferencesExpanded;
        RenderNodeReferenceHeader(refs);
        ResizeNodePreviewLayout();
      };
      _nodePreviewInfoPanel.Controls.Add(_nodeReferencesHeader);

      _nodeReferencesList = new ListView {
        FullRowSelect = true,
        GridLines = true,
        HideSelection = false,
        MultiSelect = false,
        View = View.Details,
        Visible = false
      };
      _nodeReferencesList.Columns.Add("Node", 310);
      _nodeReferencesList.Columns.Add("Base class", 135);
      _nodeReferencesList.Columns.Add("Field", 420);
      _nodeReferencesList.DoubleClick += delegate {
        if (_nodeReferencesList.SelectedItems.Count == 0) return;
        String fqn = _nodeReferencesList.SelectedItems[0].Tag as String;
        if (!String.IsNullOrWhiteSpace(fqn)) NavigateToNodeName(fqn);
      };
      _nodePreviewInfoPanel.Controls.Add(_nodeReferencesList);
      _nodePreviewInfoPanel.Resize += delegate { LayoutNodePreviewInformation(); };
    }

    private Int32 CalculateNodePreviewInfoDesiredHeight() {
      Boolean hasText = _nodePreviewText != null && _nodePreviewText.Visible
        && !String.IsNullOrWhiteSpace(_nodePreviewText.Text);
      Boolean hasIpp = _nodePreviewIppSetCheck != null && _nodePreviewIppSetCheck.Visible;
      Boolean hasConversation = _nodeConversationButton != null && _nodeConversationButton.Visible;
      Boolean hasIcon = _nodePreviewIcon != null && _nodePreviewIcon.Visible;
      Boolean hasReferences = _nodeReferencesHeader != null && _nodeReferencesHeader.Visible;
      Boolean expandedReferences = hasReferences && _nodeReferencesList != null && _nodeReferencesList.Visible;

      Int32 height = hasIcon ? 84 : 78;
      if (hasIpp) height = Math.Max(height, 106);
      if (hasConversation) height = Math.Max(height, 112);
      if (hasText) height += 82;
      if (hasReferences) height += 26;
      if (expandedReferences) height += 126;
      return Math.Max(78, height);
    }

    private void LayoutNodePreviewInformation() {
      if (_nodePreviewInfoPanel == null) return;

      Boolean hasIpp = _nodePreviewIppSetCheck != null && _nodePreviewIppSetCheck.Visible;
      Boolean hasConversation = _nodeConversationButton != null && _nodeConversationButton.Visible;
      Boolean hasText = _nodePreviewText != null && _nodePreviewText.Visible
        && !String.IsNullOrWhiteSpace(_nodePreviewText.Text);
      Boolean hasReferences = _nodeReferencesHeader != null && _nodeReferencesHeader.Visible;
      Boolean expandedReferences = hasReferences && _nodeReferencesList != null && _nodeReferencesList.Visible;

      Int32 width = Math.Max(30, _nodePreviewInfoPanel.ClientSize.Width - 16);
      Int32 top = 82;
      if (hasIpp) {
        _nodePreviewIppSetCheck.Location = new Point(8, 79);
        top = 105;
      }
      if (hasConversation) {
        _nodeConversationButton.Location = new Point(8, 79);
        top = 112;
      }

      Int32 reserveReferences = hasReferences ? 26 : 0;
      if (expandedReferences) reserveReferences += 126;

      if (hasText) {
        Int32 available = _nodePreviewInfoPanel.ClientSize.Height - top - reserveReferences - 6;
        _nodePreviewText.Location = new Point(8, top);
        _nodePreviewText.Size = new Size(width, Math.Max(48, available));
        top += _nodePreviewText.Height + 4;
      }

      if (hasReferences) {
        _nodeReferencesHeader.Location = new Point(8, top);
        _nodeReferencesHeader.Width = width;
        top += 24;
      }

      if (expandedReferences) {
        _nodeReferencesList.Location = new Point(8, top);
        _nodeReferencesList.Size = new Size(width, Math.Max(90, _nodePreviewInfoPanel.ClientSize.Height - top - 6));
      }
    }

    private void UpdateNodeReferencePanel(GomObject gom) {
      if (_nodeReferencesHeader == null || gom == null) return;
      _nodeReferencesCurrentId = gom.Id;
      _nodeReferencesList.Items.Clear();
      _nodeReferencesList.Visible = false;
      _nodeReferencesExpanded = false;

      Dictionary<UInt64, List<NodeReverseReference>> index;
      lock (_nodeReferenceIndexLock) index = _nodeReverseReferenceIndex;
      if (index != null) {
        RenderNodeReferences(gom.Id, index);
        return;
      }

      _nodeReferencesHeader.Text = "Referenced by: building offline index…";
      _nodeReferencesHeader.Links.Clear();
      StartNodeReferenceIndex();
    }

    private void StartNodeReferenceIndex() {
      lock (_nodeReferenceIndexLock) {
        if (_nodeReverseReferenceIndex != null || _nodeReferenceIndexTask != null || _nodeDict == null || _currentDom == null)
          return;

        List<GomObject> nodes = _nodeDict.Values.OfType<GomObject>().ToList();
        DataObjectModel dom = _currentDom;
        _nodeReferenceIndexTask = Task.Run(() => BuildNodeReferenceIndex(nodes, dom));
      }

      _nodeReferenceIndexTask.ContinueWith(task => {
        if (_closing || IsDisposed) return;
        if (task.IsFaulted || task.IsCanceled || task.Result == null) {
          try {
            BeginInvoke(new Action(() => {
              if (_nodeReferencesHeader != null)
                _nodeReferencesHeader.Text = "Referenced by: index unavailable";
            }));
          } catch { }
          return;
        }

        lock (_nodeReferenceIndexLock) _nodeReverseReferenceIndex = task.Result;
        try {
          BeginInvoke(new Action(() => {
            if (_closing || _nodeReferencesHeader == null) return;
            RenderNodeReferences(_nodeReferencesCurrentId, task.Result);
            ResizeNodePreviewLayout();
          }));
        } catch { }
      }, TaskScheduler.Default);
    }

    private Dictionary<UInt64, List<NodeReverseReference>> BuildNodeReferenceIndex(
      List<GomObject> nodes, DataObjectModel dom) {
      Dictionary<UInt64, List<NodeReverseReference>> result =
        new Dictionary<UInt64, List<NodeReverseReference>>();
      if (nodes == null || dom == null) return result;

      Int32 done = 0;
      Int32 total = Math.Max(1, nodes.Count);
      foreach (GomObject source in nodes) {
        if (_closing) return null;
        try {
          GomObjectData data = source.Data;
          if (data?.Dictionary != null) {
            HashSet<String> seenForSource = new HashSet<String>(StringComparer.Ordinal);
            Int32 budget = 12000;
            foreach (KeyValuePair<String, Object> field in data.Dictionary) {
              if (budget <= 0) break;
              if (String.Equals(field.Key, "Script_Type", StringComparison.OrdinalIgnoreCase)) continue;
              ScanReverseReferences(source, field.Value, field.Key, dom, result, seenForSource, ref budget, 0);
            }
          }
        } catch { }
        finally {
          // Indexing can touch tens of thousands of compressed GOM objects. Keep the reverse index,
          // not every decompressed node, resident. The selected node is left loaded so a concurrent
          // preview never loses the object it is currently rendering.
          if (source.Id != _nodeReferencesCurrentId) {
            try { source.Unload(); } catch { }
          }
        }

        done++;
        if ((done % 1500) == 0) {
          Int32 percent = done * 100 / total;
          try {
            BeginInvoke(new Action(() => {
              if (_nodeReferencesHeader != null && _nodeReverseReferenceIndex == null)
                _nodeReferencesHeader.Text = "Referenced by: indexing local nodes… " + percent + "%";
            }));
          } catch { }
        }
      }

      foreach (List<NodeReverseReference> refs in result.Values) {
        refs.Sort((a, b) => {
          Int32 cmp = StringComparer.OrdinalIgnoreCase.Compare(a.SourceFqn, b.SourceFqn);
          return cmp != 0 ? cmp : StringComparer.OrdinalIgnoreCase.Compare(a.FieldPath, b.FieldPath);
        });
      }
      return result;
    }

    private void ScanReverseReferences(GomObject source, Object value, String path, DataObjectModel dom,
                                       Dictionary<UInt64, List<NodeReverseReference>> index,
                                       HashSet<String> seenForSource, ref Int32 budget, Int32 depth) {
      if (value == null || budget-- <= 0 || depth > 8) return;

      if (TryResolveReferencedNode(value, dom, out GomObject target)) {
        if (target.Id != source.Id) AddReverseReference(source, target, path, index, seenForSource);
        // A direct node object has its own data graph. Treat it as a reference only; do not recurse into the target.
        if (value is GomObject) return;
      }

      if (value is GomObjectData objectData) {
        if (objectData.Dictionary == null) return;
        foreach (KeyValuePair<String, Object> pair in objectData.Dictionary) {
          if (budget <= 0) break;
          if (String.Equals(pair.Key, "Script_Type", StringComparison.OrdinalIgnoreCase)) continue;
          ScanReverseReferences(source, pair.Value, AppendReferencePath(path, pair.Key), dom,
            index, seenForSource, ref budget, depth + 1);
        }
        return;
      }

      if (value is IDictionary dictionary) {
        Int32 count = 0;
        foreach (DictionaryEntry entry in dictionary) {
          if (budget <= 0 || count++ >= 3000) break;
          String keyText = entry.Key?.ToString();
          if (TryResolveReferencedNode(entry.Key, dom, out GomObject keyTarget) && keyTarget.Id != source.Id)
            AddReverseReference(source, keyTarget, AppendReferencePath(path, "[key]"), index, seenForSource);
          ScanReverseReferences(source, entry.Value,
            AppendReferencePath(path, String.IsNullOrWhiteSpace(keyText) ? "[]" : "[" + ShortReferenceKey(keyText) + "]"),
            dom, index, seenForSource, ref budget, depth + 1);
        }
        return;
      }

      if (value is IEnumerable enumerable && value is not String && value is not Byte[]) {
        Int32 i = 0;
        foreach (Object child in enumerable) {
          if (budget <= 0 || i >= 3000) break;
          // Generic KeyValuePair<,> does not implement non-generic IDictionary. Preserve both key references
          // and a meaningful path when GOM map loaders expose it this way.
          Object actualChild = child;
          String childPath = AppendReferencePath(path, "[" + i + "]");
          try {
            Type type = child?.GetType();
            System.Reflection.PropertyInfo keyProperty = type?.GetProperty("Key");
            System.Reflection.PropertyInfo valueProperty = type?.GetProperty("Value");
            if (valueProperty != null) {
              Object key = keyProperty?.GetValue(child);
              String keyText = key?.ToString();
              if (TryResolveReferencedNode(key, dom, out GomObject keyTarget) && keyTarget.Id != source.Id)
                AddReverseReference(source, keyTarget, AppendReferencePath(path, "[key]"), index, seenForSource);
              actualChild = valueProperty.GetValue(child);
              childPath = AppendReferencePath(path,
                String.IsNullOrWhiteSpace(keyText) ? "[" + i + "]" : "[" + ShortReferenceKey(keyText) + "]");
            }
          } catch { }
          ScanReverseReferences(source, actualChild, childPath, dom, index, seenForSource, ref budget, depth + 1);
          i++;
        }
      }
    }

    private static String AppendReferencePath(String path, String child) {
      if (String.IsNullOrWhiteSpace(path)) return child ?? String.Empty;
      if (String.IsNullOrWhiteSpace(child)) return path;
      return child.StartsWith("[", StringComparison.Ordinal) ? path + child : path + "." + child;
    }

    private static String ShortReferenceKey(String key) {
      if (String.IsNullOrEmpty(key)) return String.Empty;
      return key.Length <= 36 ? key : key.Substring(0, 33) + "…";
    }

    private Boolean TryResolveReferencedNode(Object value, DataObjectModel dom, out GomObject target) {
      target = null;
      if (value == null || dom == null) return false;
      try {
        UInt64 id;
        switch (value) {
          case GomObject direct:
            target = direct;
            return true;
          case UInt64 u64:
            id = u64;
            break;
          case Int64 i64 when i64 > 0:
            id = unchecked((UInt64)i64);
            break;
          case UInt32 u32:
            id = u32;
            break;
          case Int32 i32 when i32 > 0:
            id = (UInt64)i32;
            break;
          case String text:
            String trimmed = text.Trim();
            if (trimmed.Length == 0 || trimmed.Length > 320 || trimmed.StartsWith("/", StringComparison.Ordinal)) return false;
            if (UInt64.TryParse(trimmed, out id)) break;
            // FQNs are compact technical identifiers. Avoid probing the DOM for normal localized prose
            // that merely happens to contain a full stop; that makes a full reverse-index pass much cheaper.
            if (!trimmed.Contains('.') || trimmed.Any(Char.IsWhiteSpace)
                || trimmed.IndexOf('/') >= 0 || trimmed.IndexOf('\\') >= 0) return false;
            target = dom.GetObject(trimmed);
            return target != null;
          default:
            return false;
        }

        if (dom.DomTypeMap.TryGetValue(id, out DomType type) && type is GomObject gom) {
          target = gom;
          return true;
        }
      } catch { }
      return false;
    }

    private static void AddReverseReference(GomObject source, GomObject target, String fieldPath,
                                            Dictionary<UInt64, List<NodeReverseReference>> index,
                                            HashSet<String> seenForSource) {
      if (source == null || target == null || source.Id == target.Id) return;
      String normalizedPath = String.IsNullOrWhiteSpace(fieldPath) ? "(unknown field)" : fieldPath;
      String unique = target.Id + "\u001f" + normalizedPath;
      if (!seenForSource.Add(unique)) return;

      if (!index.TryGetValue(target.Id, out List<NodeReverseReference> refs)) {
        refs = new List<NodeReverseReference>();
        index.Add(target.Id, refs);
      }
      refs.Add(new NodeReverseReference {
        SourceFqn = source.Name,
        SourceClass = source.DomClass?.Name ?? "unknown",
        FieldPath = normalizedPath
      });
    }

    private void RenderNodeReferences(UInt64 targetId, Dictionary<UInt64, List<NodeReverseReference>> index) {
      if (_nodeReferencesHeader == null || _nodeReferencesList == null || index == null) return;
      if (targetId != _nodeReferencesCurrentId) return;

      index.TryGetValue(targetId, out List<NodeReverseReference> refs);
      refs ??= new List<NodeReverseReference>();
      _nodeReferencesList.BeginUpdate();
      try {
        _nodeReferencesList.Items.Clear();
        foreach (NodeReverseReference reference in refs.Take(2000)) {
          ListViewItem item = new ListViewItem(reference.SourceFqn ?? String.Empty) { Tag = reference.SourceFqn };
          item.SubItems.Add(reference.SourceClass ?? String.Empty);
          item.SubItems.Add(reference.FieldPath ?? String.Empty);
          _nodeReferencesList.Items.Add(item);
        }
        if (refs.Count > 2000) {
          ListViewItem more = new ListViewItem("… " + (refs.Count - 2000).ToString("N0") + " more references") { Tag = null };
          more.SubItems.Add(String.Empty);
          more.SubItems.Add("Use raw/local search for extremely large reference sets.");
          _nodeReferencesList.Items.Add(more);
        }
      } finally {
        _nodeReferencesList.EndUpdate();
      }
      RenderNodeReferenceHeader(refs);
      LayoutNodePreviewInformation();
    }

    private void RenderNodeReferenceHeader(List<NodeReverseReference> refs) {
      refs ??= new List<NodeReverseReference>();
      Int32 nodes = refs.Select(x => x.SourceFqn).Where(x => !String.IsNullOrWhiteSpace(x))
                        .Distinct(StringComparer.OrdinalIgnoreCase).Count();
      String marker = refs.Count > 0 ? (_nodeReferencesExpanded ? " ▼" : " ▶") : String.Empty;
      _nodeReferencesHeader.Text = "Referenced by (" + nodes.ToString("N0") + " nodes, "
        + refs.Count.ToString("N0") + " references)" + marker;
      _nodeReferencesHeader.Links.Clear();
      if (refs.Count > 0) _nodeReferencesHeader.Links.Add(0, _nodeReferencesHeader.Text.Length);
    }

    private Boolean NavigateToNodeName(String nodeString) {
      if (String.IsNullOrWhiteSpace(nodeString) || treeViewFast1 == null) return false;
      TreeNode[] nodes = treeViewFast1.Nodes.Find(nodeString, true);
      if (nodes.Length == 0 && _compareNodes && _assetDict != null) {
        String compareKey = _assetDict.Keys.FirstOrDefault(key =>
          key.EndsWith("/" + nodeString, StringComparison.OrdinalIgnoreCase));
        if (!String.IsNullOrEmpty(compareKey)) nodes = treeViewFast1.Nodes.Find(compareKey, true);
      }
      if (nodes.Length == 0) return false;
      treeViewFast1.SelectedNode = nodes[0];
      treeViewFast1.SelectedNode.EnsureVisible();
      treeViewFast1.Focus();
      return true;
    }

    private void DisposeNodeReferenceUi() {
      lock (_nodeReferenceIndexLock) {
        _nodeReverseReferenceIndex = null;
        _nodeReferenceIndexTask = null;
      }
      _nodeReferencesList?.Items.Clear();
    }

    private String BuildQuestJournalPreview(Quest quest) {
      if (quest == null) return null;
      try {
        System.Reflection.PropertyInfo branchesProperty = quest.GetType().GetProperty("Branches",
          System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public |
          System.Reflection.BindingFlags.NonPublic);
        IEnumerable branchesEnumerable = branchesProperty?.GetValue(quest) as IEnumerable;
        if (branchesEnumerable == null) return null;

        List<String> lines = new List<String>();
        Int32 branchNumber = 0;
        foreach (Object rawBranch in branchesEnumerable) {
          if (rawBranch is not QuestBranch branch) continue;
          branchNumber++;
          lines.Add("Branch " + branchNumber + " (" + branch.Id + ")");
          if (branch.Steps == null) continue;
          Int32 stepNumber = 0;
          foreach (QuestStep step in branch.Steps) {
            if (step == null) continue;
            stepNumber++;
            String journal = SelectLocalizedPreviewText(step.LocalizedJournalText, step.JournalText);
            lines.Add("  Step " + stepNumber + (String.IsNullOrWhiteSpace(journal) ? String.Empty : ": " + journal));
            if (step.Tasks == null) continue;
            Int32 taskNumber = 0;
            foreach (QuestTask task in step.Tasks) {
              if (task == null) continue;
              taskNumber++;
              String taskText = SelectLocalizedPreviewText(task.LocalizedString, task.Text);
              String count = task.ShowCount && task.CountMax > 0 ? " (0/" + task.CountMax + ")" : String.Empty;
              lines.Add("    • " + (String.IsNullOrWhiteSpace(taskText) ? "Task " + taskNumber : taskText) + count);
              String targets = BuildQuestTaskTargets(task);
              if (!String.IsNullOrWhiteSpace(targets)) lines.Add("      " + targets);
            }
            if (step.BonusMissionsIds != null && step.BonusMissionsIds.Count > 0)
              lines.Add("    Bonus missions: " + String.Join(", ", step.BonusMissionsIds.Select(ResolveNodeName)));
          }
          if (lines.Count > 120) {
            lines.Add("… additional quest steps omitted from preview");
            break;
          }
        }
        return lines.Count == 0 ? null : String.Join(Environment.NewLine, lines);
      } catch { return null; }
    }

    private String BuildQuestTaskTargets(QuestTask task) {
      List<String> parts = new List<String>();
      if (task.TaskNpcIds != null && task.TaskNpcIds.Count > 0)
        parts.Add("NPC: " + String.Join(", ", task.TaskNpcIds.Take(6).Select(ResolveNodeName)));
      if (task.TaskPlcIds != null && task.TaskPlcIds.Count > 0)
        parts.Add("Placeable: " + String.Join(", ", task.TaskPlcIds.Take(6).Select(ResolveNodeName)));
      if (task.TaskQuestIds != null && task.TaskQuestIds.Count > 0)
        parts.Add("Quest: " + String.Join(", ", task.TaskQuestIds.Take(6).Select(ResolveNodeName)));
      if (task.MapNoteFqnList != null && task.MapNoteFqnList.Count > 0)
        parts.Add("Map: " + String.Join(", ", task.MapNoteFqnList.Take(6)));
      return String.Join("; ", parts);
    }

    private String BuildQuestRewardsPreview(Quest quest) {
      if (quest == null) return null;
      try {
        System.Reflection.PropertyInfo rewardsProperty = quest.GetType().GetProperty("Rewards",
          System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public |
          System.Reflection.BindingFlags.NonPublic);
        IEnumerable rewards = rewardsProperty?.GetValue(quest) as IEnumerable;
        if (rewards == null) return null;
        List<String> values = new List<String>();
        foreach (Object raw in rewards) {
          if (raw is not QuestReward reward) continue;
          Item item = reward.RewardItem;
          String name = item == null ? ResolveNodeName(reward.RewardItemId)
            : SelectLocalizedPreviewText(item.LocalizedName, item.Name);
          String amount = reward.NumberOfItem > 1 ? reward.NumberOfItem + "× " : String.Empty;
          values.Add(amount + (String.IsNullOrWhiteSpace(name) ? reward.RewardItemId.ToString() : name));
          if (values.Count >= 24) break;
        }
        return values.Count == 0 ? null : String.Join(", ", values);
      } catch { return null; }
    }

    private String BuildItemEnhancementPreview(Item item) {
      if (item?.EnhancementSlots == null || item.EnhancementSlots.Count == 0) return null;
      List<String> parts = new List<String>();
      foreach (ItemEnhancement enhancement in item.EnhancementSlots.Take(12)) {
        if (enhancement == null) continue;
        String name = null;
        try {
          Item modification = enhancement.Modification;
          name = modification == null ? null : SelectLocalizedPreviewText(modification.LocalizedName, modification.Name);
        } catch { }
        if (String.IsNullOrWhiteSpace(name)) name = ResolveNodeName(enhancement.ModificationId);
        parts.Add(enhancement.Slot + ": " + name);
      }
      return parts.Count == 0 ? null : String.Join(", ", parts);
    }

    private String BuildItemSetBonusPreview(Item item) {
      if (item == null || item.SetBonusId == 0) return null;
      try {
        SetBonusEntry set = item.SetBonus;
        if (set == null) return item.SetBonusId.ToString();
        String name = SelectLocalizedPreviewText(set.LocalizedName, set.Name);
        List<String> bonuses = new List<String>();
        if (set.BonusAbilityByNum != null) {
          foreach (KeyValuePair<Int64, Ability> bonus in set.BonusAbilityByNum.OrderBy(x => x.Key)) {
            Ability ability = bonus.Value;
            String abilityName = ability == null ? null : SelectLocalizedPreviewText(ability.LocalizedName, ability.Name);
            String description = ability == null ? null
              : SelectLocalizedPreviewText(ability.ParsedLocalizedDescription ?? ability.LocalizedDescription, ability.Description);
            String text = bonus.Key + " pieces";
            if (!String.IsNullOrWhiteSpace(abilityName)) text += ": " + abilityName;
            if (!String.IsNullOrWhiteSpace(description)) text += " — " + CleanPreviewText(description);
            bonuses.Add(text);
          }
        }
        String prefix = String.IsNullOrWhiteSpace(name) ? item.SetBonusId.ToString() : name;
        return bonuses.Count == 0 ? prefix : prefix + Environment.NewLine + String.Join(Environment.NewLine, bonuses);
      } catch { return item.SetBonusId.ToString(); }
    }

    private String BuildItemAbilityPreview(Ability ability) {
      if (ability == null) return null;
      String name = SelectLocalizedPreviewText(ability.LocalizedName, ability.Name);
      String description = SelectLocalizedPreviewText(ability.ParsedLocalizedDescription ?? ability.LocalizedDescription,
        ability.Description);
      if (String.IsNullOrWhiteSpace(name)) return CleanPreviewText(description);
      if (String.IsNullOrWhiteSpace(description)) return name;
      return name + " — " + CleanPreviewText(description);
    }

    private String BuildSchematicMaterialsPreview(Schematic schematic) {
      if (schematic?.Materials == null || schematic.Materials.Count == 0) return null;
      List<String> parts = new List<String>();
      foreach (KeyValuePair<UInt64, Int32> material in schematic.Materials.Take(24)) {
        String name = null;
        try {
          Item item = _currentDom?.ItemLoader.Load(material.Key);
          if (item != null) name = SelectLocalizedPreviewText(item.LocalizedName, item.Name);
        } catch { }
        if (String.IsNullOrWhiteSpace(name)) name = ResolveNodeName(material.Key);
        parts.Add(material.Value + "× " + name);
      }
      return parts.Count == 0 ? null : String.Join(", ", parts);
    }

    private String BuildSchematicResearchPreview(Schematic schematic) {
      if (schematic == null) return null;
      List<String> parts = new List<String>();
      AddSchematicResearchPart(parts, schematic.Research1, schematic.ResearchQuantity1, schematic.ResearchChance1, 1);
      AddSchematicResearchPart(parts, schematic.Research2, schematic.ResearchQuantity2, schematic.ResearchChance2, 2);
      AddSchematicResearchPart(parts, schematic.Research3, schematic.ResearchQuantity3, schematic.ResearchChance3, 3);
      return parts.Count == 0 ? null : String.Join(Environment.NewLine, parts);
    }

    private static void AddSchematicResearchPart(List<String> parts, Item item, Int32 quantity,
                                                 SchematicResearchChance chance, Int32 tier) {
      if (parts == null || item == null) return;
      String name = SelectLocalizedPreviewText(item.LocalizedName, item.Name);
      String text = "Tier " + tier + ": " + (quantity > 1 ? quantity + "× " : String.Empty)
        + (String.IsNullOrWhiteSpace(name) ? item.Fqn : name);
      if (!chance.Equals(default(SchematicResearchChance))) text += " (" + chance + ")";
      parts.Add(text);
    }

    private String BuildAchievementTasksPreview(Achievement achievement) {
      if (achievement?.Tasks == null || achievement.Tasks.Count == 0) return null;
      List<String> lines = new List<String>();
      Int32 number = 0;
      foreach (AchTask task in achievement.Tasks.Take(40)) {
        if (task == null) continue;
        number++;
        String name = SelectLocalizedPreviewText(task.LocalizedNames, task.Name);
        String line = number + ". " + (String.IsNullOrWhiteSpace(name) ? "Task" : name);
        if (task.Count > 1) line += " ×" + task.Count;
        if (task.Events != null && task.Events.Count > 0) {
          List<String> events = new List<String>();
          foreach (AchEvent evt in task.Events.Take(12)) {
            if (evt == null) continue;
            String target = ResolveNodeName(evt.Id);
            if (evt.Value != 0) target += " (" + evt.Value + ")";
            events.Add(target);
          }
          if (events.Count > 0) line += " — " + String.Join(", ", events);
        }
        lines.Add(line);
      }
      return lines.Count == 0 ? null : String.Join(Environment.NewLine, lines);
    }

    private static String BuildAchievementConditionsPreview(Achievement achievement) {
      if (achievement?.Conditions == null || achievement.Conditions.Count == 0) return null;
      List<String> parts = new List<String>();
      foreach (AchCondition condition in achievement.Conditions.Take(40)) {
        if (condition == null) continue;
        parts.Add(condition.Type + " → " + condition.Target + (condition.UnknownBoolean ? " (flagged)" : String.Empty));
      }
      return parts.Count == 0 ? null : String.Join(", ", parts);
    }

    private String BuildAchievementRewardsPreview(Achievement achievement) {
      Rewards rewards = achievement?.Rewards;
      if (rewards == null) return null;
      List<String> parts = new List<String>();
      if (rewards.AchievementPoints > 0) parts.Add(rewards.AchievementPoints + " achievement points");
      if (rewards.CartelCoins > 0) parts.Add(rewards.CartelCoins + " Cartel Coins");
      if (rewards.Requisition > 0) parts.Add(rewards.Requisition + " requisition");
      String legacyTitle = SelectLocalizedPreviewText(rewards.LocalizedLegacyTitle, rewards.LegacyTitle);
      if (!String.IsNullOrWhiteSpace(legacyTitle)) parts.Add("Title: " + legacyTitle);
      if (rewards.ItemRewardList != null) {
        foreach (KeyValuePair<UInt64, Int64> reward in rewards.ItemRewardList.Take(20)) {
          String name = null;
          try {
            Item item = _currentDom?.ItemLoader.Load(reward.Key);
            if (item != null) name = SelectLocalizedPreviewText(item.LocalizedName, item.Name);
          } catch { }
          if (String.IsNullOrWhiteSpace(name)) name = ResolveNodeName(reward.Key);
          parts.Add((reward.Value > 1 ? reward.Value + "× " : String.Empty) + name);
        }
      }
      return parts.Count == 0 ? null : String.Join(", ", parts);
    }

    private static String BuildCodexLinksPreview(Codex codex) {
      if (codex?.Planets == null || codex.Planets.Count == 0) return null;
      List<String> parts = new List<String>();
      foreach (Codex linked in codex.Planets.Take(30)) {
        if (linked == null) continue;
        String name = SelectLocalizedPreviewText(linked.LocalizedName, linked.Name);
        parts.Add(String.IsNullOrWhiteSpace(name) ? linked.Fqn : name + " (" + linked.Fqn + ")");
      }
      return parts.Count == 0 ? null : String.Join(", ", parts);
    }

    private String BuildTalentRanksPreview(Talent talent) {
      if (talent == null) return null;
      List<String> lines = new List<String>();
      String baseDescription = SelectLocalizedPreviewText(talent.LocalizedDescription, talent.Description);
      if (!String.IsNullOrWhiteSpace(baseDescription)) {
        try { baseDescription = Talent.ParseDescription(talent, baseDescription); } catch { }
        lines.Add("Rank 1: " + CleanPreviewText(baseDescription));
      }
      if (!String.IsNullOrWhiteSpace(talent.DescriptionRank2)) lines.Add("Rank 2: " + CleanPreviewText(talent.DescriptionRank2));
      if (!String.IsNullOrWhiteSpace(talent.DescriptionRank3)) lines.Add("Rank 3: " + CleanPreviewText(talent.DescriptionRank3));

      if (talent.RankStats != null) {
        Int32 rank = 0;
        foreach (Talent.RankStatData rankStats in talent.RankStats.Take(10)) {
          rank++;
          if (rankStats == null) continue;
          List<String> stats = new List<String>();
          IEnumerable<Talent.StatData> combined = (rankStats.OffensiveStats ?? new List<Talent.StatData>())
            .Concat(rankStats.DefensiveStats ?? new List<Talent.StatData>());
          foreach (Talent.StatData stat in combined.Take(16)) {
            if (stat == null || !stat.Enabled) continue;
            String target = stat.AffectedNodeId != 0 ? " → " + ResolveNodeName(stat.AffectedNodeId) : String.Empty;
            stats.Add(stat.Stat + " " + stat.Modifier + " " + stat.Value.ToString("0.###") + target);
          }
          if (stats.Count > 0) lines.Add("Rank " + rank + " stats: " + String.Join(", ", stats));
        }
      }
      return lines.Count == 0 ? null : String.Join(Environment.NewLine, lines);
    }

    private String ResolveNodeName(UInt64 id) {
      if (id == 0 || _currentDom == null) return id.ToString();
      try { return _currentDom.GetObject(id)?.Name ?? id.ToString(); } catch { return id.ToString(); }
    }

    private static String SelectLocalizedPreviewText(Dictionary<String, String> localized, String fallback) {
      String text = null;
      try { text = StringTable.SelectLocalizedText(localized, fallback); } catch { }
      return String.IsNullOrWhiteSpace(text) ? fallback : text;
    }
  }
}
