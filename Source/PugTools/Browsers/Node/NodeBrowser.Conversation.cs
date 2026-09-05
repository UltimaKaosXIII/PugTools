using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows.Forms;

using GomLib;
using GomLib.Models;

namespace PugTools {
  internal partial class NodeBrowser {
    private Button _nodeConversationButton;
    private Form _nodeConversationForm;
    private Label _nodeConversationHeader;
    private TextBox _nodeConversationSearch;
    private Button _nodeConversationFindNext;
    private TreeView _nodeConversationTree;
    private RichTextBox _nodeConversationDetails;
    private Conversation _nodeConversation;
    private Int32 _nodeConversationSearchIndex = -1;
    private readonly Dictionary<UInt64, String> _nodeConversationSpeakerNames = new Dictionary<UInt64, String>();

    private sealed class NodeConversationTreeTag {
      public Int64 RawNodeId;
      public Int64 NodeId;
      public DialogNode Node;
      public Boolean IsLink;
      public Boolean IsCycle;
      public Boolean IsRejoin;
      public String Note;
    }

    private void InitializeNodeConversationUi() {
      if (_nodePreviewInfoPanel == null || _nodeConversationButton != null) return;
      _nodeConversationButton = new Button {
        AutoSize = false,
        Size = new Size(190, 28),
        Location = new Point(8, 79),
        Text = "Open conversation tree…",
        Visible = false
      };
      _nodeConversationButton.Click += delegate { OpenSelectedNodeConversation(); };
      _nodePreviewInfoPanel.Controls.Add(_nodeConversationButton);
    }

    private void UpdateNodeConversationButton(GomObject gom) {
      if (_nodeConversationButton == null) return;
      Boolean visible = gom != null && gom.Name != null && gom.Name.StartsWith("cnv.", StringComparison.OrdinalIgnoreCase);
      _nodeConversationButton.Visible = visible;
      _nodeConversationButton.Enabled = visible;
    }

    private void OpenSelectedNodeConversation() {
      if (treeViewFast1?.SelectedNode?.Tag is not NodeAsset asset || asset.Obj == null) return;
      GomObject gom = asset.Obj;
      if (!gom.Name.StartsWith("cnv.", StringComparison.OrdinalIgnoreCase)) return;

      if (!TryLoadNodeConversation(gom, out Conversation conversation, out String error)) {
        MessageBox.Show(this, error ?? "The conversation could not be loaded.", "Conversation tree",
          MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return;
      }

      EnsureNodeConversationForm();
      _nodeConversation = conversation;
      _nodeConversationSpeakerNames.Clear();
      _nodeConversationSearchIndex = -1;
      if (_nodeConversationSearch != null) _nodeConversationSearch.Text = String.Empty;

      Int32 nodeCount = conversation.NodeLookup?.Count ?? conversation.DialogNodes?.Count ?? 0;
      Int32 rootCount = conversation.RootNodes?.Count ?? 0;
      String audio = String.Empty;
      try {
        String[] installedAudio = conversation.AudioLanguageState
          ?.Where(x => x.Value).Select(x => x.Key).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        if (installedAudio != null && installedAudio.Length > 0) audio = "   •   audio: " + String.Join(", ", installedAudio);
      } catch { }
      _nodeConversationHeader.Text = (conversation.Fqn ?? gom.Name) + "\r\n"
        + nodeCount.ToString("N0") + " dialog nodes   •   " + rootCount.ToString("N0") + " roots"
        + (conversation.IsKOTORStyle ? "   •   KOTOR-style" : String.Empty) + audio
        + "   •   " + LocalizationResolver.GetStatusText();

      RebuildNodeConversationTree(conversation);
      if (!_nodeConversationForm.Visible) _nodeConversationForm.Show(this);
      else _nodeConversationForm.BringToFront();
      _nodeConversationForm.Focus();
    }

    private Boolean TryLoadNodeConversation(GomObject gom, out Conversation conversation, out String error) {
      conversation = null;
      error = null;
      if (gom == null || _currentDom == null) {
        error = "No GOM data is loaded.";
        return false;
      }

      List<String> errors = new List<String>();
      try { conversation = _currentDom.ConversationLoader.Load(gom); }
      catch (Exception ex) { errors.Add("ConversationLoader: " + ex.Message); }
      if (conversation != null && conversation.NodeLookup != null && conversation.NodeLookup.Count > 0) return true;

      // Some beta/legacy clients are missing unrelated helper prototypes used by ConversationLoader
      // (for example companion-reaction tables). The dialog tree itself is still perfectly useful,
      // so keep a small read-only raw fallback mirroring Jedipedia's cnv reader.
      try {
        if (TryBuildNodeConversationCore(gom, out conversation, out String fallbackError)) return true;
        if (!String.IsNullOrWhiteSpace(fallbackError)) errors.Add("Core reader: " + fallbackError);
      } catch (Exception ex) { errors.Add("Core reader: " + ex.Message); }

      error = "Conversation " + gom.Name + " could not be loaded.";
      if (errors.Count > 0) error += "\r\n\r\n" + String.Join("\r\n", errors.Distinct());
      return false;
    }

    private Boolean TryBuildNodeConversationCore(GomObject obj, out Conversation conversation, out String error) {
      conversation = null;
      error = null;
      if (obj?.Data == null) { error = "node has no data"; return false; }
      try {
        Conversation result = new Conversation {
          Id = obj.Id,
          Fqn = obj.Name,
          Dom_ = _currentDom,
          References = obj.References,
          IsKOTORStyle = NodeConversationBool(NodeConversationDataValue(obj.Data, "cnvIsKOTORStyle", null)),
          DefaultSpeakerId = NodeConversationUInt64(NodeConversationDataValue(obj.Data, "cnvDefaultSpeaker", null))
        };
        if (result.DefaultSpeakerId != 0 && !result.SpeakersIds.Contains(result.DefaultSpeakerId)) result.SpeakersIds.Add(result.DefaultSpeakerId);

        Object rawDialogs = NodeConversationDataValue(obj.Data, "cnvTreeDialogNodes_Prototype", "4611686050212071021");
        foreach (KeyValuePair<Object, Object> pair in NodeConversationPairs(rawDialogs)) {
          if (pair.Value is not GomObjectData data) continue;
          Int64 fallbackId = NodeConversationInt64(pair.Key);
          Int64 nodeId = NodeConversationInt64(NodeConversationDataValue(data, "cnvNodeNumber", "4611686019044571365"));
          if (nodeId == 0) nodeId = fallbackId;
          if (nodeId == 0) continue;

          DialogNode node = new DialogNode {
            Conversation = result,
            NodeId = nodeId,
            MinLevel = (Int32)NodeConversationInt64(NodeConversationDataValue(data, "cnvLevelConditionMin", null)),
            MaxLevel = (Int32)NodeConversationInt64(NodeConversationDataValue(data, "cnvLevelConditionMax", null)),
            IsEmpty = NodeConversationBool(NodeConversationDataValue(data, "cnvIsEmpty", null)),
            IsAmbient = NodeConversationBool(NodeConversationDataValue(data, "cnvIsAmbient", null)),
            JoinDisabledForHolocom = NodeConversationBool(NodeConversationDataValue(data, "cnvIsJoinDisabledForHolocom", null)),
            ChoiceDisabledForHolocom = NodeConversationBool(NodeConversationDataValue(data, "cnvIsVoteWinDisabledForHolocom", null)),
            AbortsConversation = NodeConversationBool(NodeConversationDataValue(data, "cnvAbortConversation", null)),
            IsPlayerNode = NodeConversationBool(NodeConversationDataValue(data, "cnvIsPcNode", "4611686019058500344")),
            GenericNodeNumber = NodeConversationInt64(NodeConversationDataValue(data, "cnvGenericNodeNumber", "4611686019251991207")),
            CnvAlienVOFQN = NodeConversationText(NodeConversationDataValue(data, "cnvAlienVOConvoFQN", null)) ?? String.Empty,
            CnvAlienVONode = NodeConversationInt64(NodeConversationDataValue(data, "cnvAlienVONodeNumber", null)),
            ActionHook = NodeConversationText(NodeConversationDataValue(data, "cnvActionHook", null)),
            SpeakerId = NodeConversationUInt64(NodeConversationDataValue(data, "cnvSpeaker", "4611686068585531196")),
            ActionQuest = NodeConversationUInt64(NodeConversationDataValue(data, "cnvActionQuest", null)),
            QuestReward = NodeConversationUInt64(NodeConversationDataValue(data, "cnvRewardQuest", null)),
            ChildIds = NodeConversationListEntries(NodeConversationDataValue(data, "cnvChildNodes", "4611686019044571321"))
              .Select(x => unchecked((Int32)NodeConversationInt64(x))).Where(x => x != 0).ToList(),
            QuestsGranted = NodeConversationEnabledIds(NodeConversationDataValue(data, "cnvNodeQuestGrants", null)),
            QuestsEnded = NodeConversationEnabledIds(NodeConversationDataValue(data, "cnvNodeQuestEnds", null)),
            QuestsProgressed = NodeConversationEnabledIds(NodeConversationDataValue(data, "cnvNodeQuestProgress", null)),
            AffectionRewardEvents = new Dictionary<Int64, KeyValuePair<Int32, String>>()
          };
          if (node.MinLevel == 0 && NodeConversationDataValue(data, "cnvLevelConditionMin", null) == null) node.MinLevel = -1;
          if (node.MaxLevel == 0 && NodeConversationDataValue(data, "cnvLevelConditionMax", null) == null) node.MaxLevel = -1;
          if (node.SpeakerId != 0 && !result.SpeakersIds.Contains(node.SpeakerId)) result.SpeakersIds.Add(node.SpeakerId);

          try {
            Object locMap = NodeConversationDataValue(data, "locTextRetrieverMap", "4611686102842470023");
            Object rawRetriever = NodeConversationMapValueById(locMap, nodeId);
            if (rawRetriever is GomObjectData retriever) {
              node.Text = _currentDom.StringTable.TryGetString(obj.Name, retriever);
              node.LocalizedText = _currentDom.StringTable.TryGetLocalizedStrings(obj.Name, retriever);
              node.LocalizedOptionText = _currentDom.StringTable.TryGetLocalizedOptionStrings(obj.Name, retriever);
              node.Stb = NodeConversationText(NodeConversationDataValue(retriever, "strLocalizedTextRetrieverBucket", null));
            }
          } catch { }

          result.DialogNodes.Add(node);
          result.NodeLookup[node.NodeId] = node;
          result.QuestStarted.AddRange(node.QuestsGranted);
          result.QuestEnded.AddRange(node.QuestsEnded);
          result.QuestProgressed.AddRange(node.QuestsProgressed);
        }
        if (result.NodeLookup.Count == 0) { error = "cnvTreeDialogNodes_Prototype contains no readable dialog nodes"; return false; }

        Object rawRoot = NodeConversationDataValue(obj.Data, "cnvTreeRootNode_Prototype", "4611686247405460000");
        GomObjectData rootData = rawRoot as GomObjectData;
        if (rootData == null) {
          // Older clients stored a map of roots instead of a single root object.
          Object legacyRoots = NodeConversationDataValue(obj.Data, "cnvTreeRootNodes_Prototype", "4611686050212071023");
          rootData = NodeConversationPairs(legacyRoots).Select(x => x.Value).OfType<GomObjectData>().FirstOrDefault();
        }
        if (rootData != null) {
          Int32 index = 0;
          foreach (Object value in NodeConversationListEntries(NodeConversationDataValue(rootData, "cnvChildNodes", "4611686019044571321"))) {
            Int64 id = NodeConversationInt64(value);
            if (id != 0) result.RootNodes[index++] = id;
          }
        }

        Object rawLinks = NodeConversationDataValue(obj.Data, "cnvTreeLinkNodes_Prototype", "4611686050212071022");
        foreach (KeyValuePair<Object, Object> pair in NodeConversationPairs(rawLinks)) {
          Int64 linkId = NodeConversationInt64(pair.Key);
          if (linkId == 0 || pair.Value is not GomObjectData linkData) continue;
          Int64 target = NodeConversationInt64(NodeConversationDataValue(linkData, "cnvLinkTarget", "4611686019044820878"));
          if (target != 0) result.NodeLinkList[linkId] = target;
        }

        conversation = result;
        return true;
      } catch (Exception ex) {
        error = ex.Message;
        return false;
      }
    }

    private void EnsureNodeConversationForm() {
      if (_nodeConversationForm != null && !_nodeConversationForm.IsDisposed) return;

      _nodeConversationForm = new Form {
        Text = "Conversation tree",
        FormBorderStyle = FormBorderStyle.Sizable,
        ShowInTaskbar = false,
        StartPosition = FormStartPosition.CenterParent,
        MinimumSize = new Size(820, 520),
        Size = new Size(1220, 780)
      };
      _nodeConversationHeader = new Label {
        Dock = DockStyle.Top,
        Height = 52,
        Padding = new Padding(8, 6, 8, 4),
        AutoEllipsis = true,
        Font = new Font(Font, FontStyle.Bold)
      };

      Panel searchPanel = new Panel { Dock = DockStyle.Top, Height = 34, Padding = new Padding(6, 3, 6, 3) };
      _nodeConversationFindNext = new Button { Dock = DockStyle.Right, Width = 92, Text = "Find next" };
      _nodeConversationSearch = new TextBox { Dock = DockStyle.Fill };
      _nodeConversationSearch.KeyDown += delegate (Object sender, KeyEventArgs e) {
        if (e.KeyCode != Keys.Enter) return;
        e.SuppressKeyPress = true;
        FindNextNodeConversationTreeMatch();
      };
      _nodeConversationFindNext.Click += delegate { FindNextNodeConversationTreeMatch(); };
      searchPanel.Controls.Add(_nodeConversationSearch);
      searchPanel.Controls.Add(_nodeConversationFindNext);

      SplitContainer split = new SplitContainer {
        Dock = DockStyle.Fill,
        Orientation = Orientation.Vertical,
        SplitterWidth = 5
      };
      _nodeConversationTree = new TreeView {
        Dock = DockStyle.Fill,
        HideSelection = false,
        ShowNodeToolTips = true,
        Font = new Font(FontFamily.GenericMonospace, 9f)
      };
      _nodeConversationDetails = new RichTextBox {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        DetectUrls = false,
        BorderStyle = BorderStyle.FixedSingle,
        Font = new Font(FontFamily.GenericMonospace, 9f),
        ScrollBars = RichTextBoxScrollBars.Vertical
      };
      _nodeConversationTree.AfterSelect += delegate (Object sender, TreeViewEventArgs e) { UpdateNodeConversationDetails(e.Node); };
      split.Panel1.Controls.Add(_nodeConversationTree);
      split.Panel2.Controls.Add(_nodeConversationDetails);

      FlowLayoutPanel buttons = new FlowLayoutPanel {
        Dock = DockStyle.Bottom,
        Height = 42,
        FlowDirection = FlowDirection.RightToLeft,
        Padding = new Padding(6, 6, 6, 4),
        WrapContents = false
      };
      Button close = new Button { Text = "Close", Width = 92, Height = 28 };
      Button copyTree = new Button { Text = "Copy tree", Width = 104, Height = 28 };
      Button copyNode = new Button { Text = "Copy node", Width = 104, Height = 28 };
      Button openSpeaker = new Button { Text = "Open speaker", Width = 116, Height = 28 };
      Button openQuest = new Button { Text = "Open quest", Width = 104, Height = 28 };
      close.Click += delegate { _nodeConversationForm.Hide(); };
      copyTree.Click += delegate { CopyNodeConversationTree(); };
      copyNode.Click += delegate {
        if (String.IsNullOrWhiteSpace(_nodeConversationDetails?.Text)) return;
        try { Clipboard.SetText(_nodeConversationDetails.Text); } catch { }
      };
      openSpeaker.Click += delegate { NavigateSelectedNodeConversationSpeaker(); };
      openQuest.Click += delegate { NavigateSelectedNodeConversationQuest(); };
      buttons.Controls.Add(close);
      buttons.Controls.Add(copyTree);
      buttons.Controls.Add(copyNode);
      buttons.Controls.Add(openQuest);
      buttons.Controls.Add(openSpeaker);

      _nodeConversationForm.Controls.Add(split);
      _nodeConversationForm.Controls.Add(buttons);
      _nodeConversationForm.Controls.Add(searchPanel);
      _nodeConversationForm.Controls.Add(_nodeConversationHeader);
      _nodeConversationForm.Shown += delegate {
        try {
          Int32 width = split.ClientSize.Width;
          if (width > 600) split.SplitterDistance = Math.Max(300, Math.Min(width - 300, width * 55 / 100));
        } catch { }
      };
      _nodeConversationForm.FormClosing += delegate (Object sender, FormClosingEventArgs e) {
        if (_closing) return;
        e.Cancel = true;
        _nodeConversationForm.Hide();
      };
      _nodeConversationForm.FormClosed += delegate {
        _nodeConversationForm = null;
        _nodeConversationHeader = null;
        _nodeConversationSearch = null;
        _nodeConversationFindNext = null;
        _nodeConversationTree = null;
        _nodeConversationDetails = null;
        _nodeConversation = null;
        _nodeConversationSpeakerNames.Clear();
      };
    }

    private void RebuildNodeConversationTree(Conversation conversation) {
      if (_nodeConversationTree == null) return;
      _nodeConversationTree.BeginUpdate();
      try {
        _nodeConversationTree.Nodes.Clear();
        _nodeConversationDetails.Clear();
        if (conversation?.NodeLookup == null || conversation.NodeLookup.Count == 0) {
          _nodeConversationTree.Nodes.Add(new TreeNode("(no dialog nodes)"));
          return;
        }

        List<KeyValuePair<Int32, Int64>> roots = conversation.RootNodes?.OrderBy(x => x.Key).ToList()
          ?? new List<KeyValuePair<Int32, Int64>>();
        if (roots.Count == 0) roots = InferNodeConversationRoots(conversation);
        HashSet<Int64> expanded = new HashSet<Int64>();
        Int32 budget = Math.Max(100, Math.Min(10000, conversation.NodeLookup.Count * 5 + (conversation.NodeLinkList?.Count ?? 0) * 2));
        foreach (KeyValuePair<Int32, Int64> root in roots) {
          TreeNode wrapper = new TreeNode("Root " + (root.Key + 1)) { ForeColor = Color.SteelBlue };
          _nodeConversationTree.Nodes.Add(wrapper);
          AddNodeConversationTreeNode(wrapper.Nodes, conversation, root.Value, new HashSet<Int64>(), expanded, ref budget, 0);
          wrapper.Expand();
        }
        TreeNode first = FirstNodeConversationDialogTreeNode(_nodeConversationTree.Nodes);
        if (first != null) _nodeConversationTree.SelectedNode = first;
      } finally { _nodeConversationTree.EndUpdate(); }
    }

    private List<KeyValuePair<Int32, Int64>> InferNodeConversationRoots(Conversation conversation) {
      HashSet<Int64> incoming = new HashSet<Int64>();
      foreach (DialogNode node in conversation?.DialogNodes ?? new List<DialogNode>()) {
        if (node?.ChildIds == null) continue;
        foreach (Int32 child in node.ChildIds) {
          Int64 target = ResolveNodeConversationLinkTarget(conversation, child, out _);
          if (target != 0) incoming.Add(target);
        }
      }
      List<Int64> roots = conversation?.NodeLookup?.Keys.Where(x => !incoming.Contains(x)).OrderBy(x => x).ToList() ?? new List<Int64>();
      if (roots.Count == 0 && conversation?.NodeLookup?.Count > 0) roots.Add(conversation.NodeLookup.Keys.OrderBy(x => x).First());
      return roots.Select((id, index) => new KeyValuePair<Int32, Int64>(index, id)).ToList();
    }

    private void AddNodeConversationTreeNode(TreeNodeCollection parent, Conversation conversation, Int64 rawId,
        HashSet<Int64> path, HashSet<Int64> expanded, ref Int32 budget, Int32 depth) {
      if (parent == null || conversation == null) return;
      if (budget-- <= 0) { parent.Add(new TreeNode("… tree truncated …") { ForeColor = Color.DarkGray }); return; }
      if (depth > 256) { parent.Add(new TreeNode("… depth limit reached …") { ForeColor = Color.DarkGray }); return; }

      Int64 nodeId = ResolveNodeConversationLinkTarget(conversation, rawId, out List<Int64> links);
      if (nodeId == 0 || conversation.NodeLookup == null || !conversation.NodeLookup.TryGetValue(nodeId, out DialogNode node) || node == null) {
        String missing = links != null && links.Count > 0 ? "Link " + rawId + " → missing node " + nodeId : "Missing node " + rawId;
        parent.Add(new TreeNode(missing) {
          ForeColor = Color.IndianRed,
          Tag = new NodeConversationTreeTag { RawNodeId = rawId, NodeId = nodeId, IsLink = links != null && links.Count > 0, Note = missing }
        });
        return;
      }

      Boolean isCycle = path.Contains(nodeId);
      Boolean isRejoin = !isCycle && expanded.Contains(nodeId);
      String label = NodeConversationNodeLabel(conversation, node);
      if (links != null && links.Count > 0) label = "↪ " + String.Join(" → ", links) + " → " + label;
      if (isCycle) label += "  [cycle]";
      else if (isRejoin) label += "  [rejoin]";

      TreeNode treeNode = new TreeNode(label) {
        Tag = new NodeConversationTreeTag {
          RawNodeId = rawId, NodeId = nodeId, Node = node,
          IsLink = links != null && links.Count > 0, IsCycle = isCycle, IsRejoin = isRejoin
        },
        ForeColor = node.IsPlayerNode ? Color.RoyalBlue : (isCycle || isRejoin ? Color.DarkGray : SystemColors.WindowText),
        ToolTipText = NodeConversationNodeTooltip(conversation, node, links)
      };
      parent.Add(treeNode);
      if (isCycle || isRejoin) return;

      expanded.Add(nodeId);
      HashSet<Int64> nextPath = new HashSet<Int64>(path) { nodeId };
      foreach (Int32 child in node.ChildIds ?? new List<Int32>())
        AddNodeConversationTreeNode(treeNode.Nodes, conversation, child, nextPath, expanded, ref budget, depth + 1);
    }

    private static Int64 ResolveNodeConversationLinkTarget(Conversation conversation, Int64 rawId, out List<Int64> links) {
      links = null;
      if (conversation == null) return rawId;
      Int64 current = rawId;
      HashSet<Int64> seen = new HashSet<Int64>();
      for (Int32 i = 0; i < 64 && conversation.NodeLinkList != null && conversation.NodeLinkList.TryGetValue(current, out Int64 target); i++) {
        links ??= new List<Int64>();
        links.Add(current);
        if (!seen.Add(current) || target == current) return target;
        current = target;
      }
      return current;
    }

    private String NodeConversationNodeLabel(Conversation conversation, DialogNode node) {
      String speaker = NodeConversationSpeakerName(conversation, node);
      String text = OneLineNodeConversationText(NodeConversationChoiceOrLine(node));
      if (text.Length > 120) text = text.Substring(0, 119) + "…";
      String prefix = "#" + node.NodeId + "  " + speaker;
      if (node.IsPlayerNode) prefix += " [choice]";
      else if (node.IsAmbient) prefix += " [ambient]";
      if (node.IsEmpty && String.IsNullOrWhiteSpace(text)) text = "(empty)";
      return prefix + (String.IsNullOrWhiteSpace(text) ? String.Empty : ": " + text);
    }

    private String NodeConversationNodeTooltip(Conversation conversation, DialogNode node, List<Int64> links) {
      if (node == null) return String.Empty;
      StringBuilder sb = new StringBuilder();
      if (links != null && links.Count > 0) sb.Append("Link ").Append(String.Join(" → ", links)).Append(" → ");
      sb.Append('#').Append(node.NodeId).Append(" • ").Append(NodeConversationSpeakerName(conversation, node));
      String text = OneLineNodeConversationText(NodeConversationChoiceOrLine(node));
      if (!String.IsNullOrWhiteSpace(text)) sb.Append(" • ").Append(text);
      return sb.ToString();
    }

    private String NodeConversationSpeakerName(Conversation conversation, DialogNode node) {
      if (node == null) return "(unknown)";
      if (node.IsPlayerNode) return "Player";
      UInt64 id = node.SpeakerId != 0 ? node.SpeakerId : conversation?.DefaultSpeakerId ?? 0;
      if (id == 0) return "(no speaker)";
      if (_nodeConversationSpeakerNames.TryGetValue(id, out String cached)) return cached;

      String label = null;
      try {
        GameObject speaker = _currentDom?.ConversationLoader.LoadSpeaker(id);
        if (speaker is Npc npc) label = !String.IsNullOrWhiteSpace(npc.Name) ? npc.Name : npc.Fqn;
        else if (speaker is Placeable plc) label = !String.IsNullOrWhiteSpace(plc.Name) ? plc.Name : plc.Fqn;
        else if (speaker != null) label = speaker.Fqn;
      } catch { }
      if (String.IsNullOrWhiteSpace(label)) {
        try { label = _currentDom?.GetObject(id)?.Name; } catch { }
      }
      if (String.IsNullOrWhiteSpace(label)) label = "Speaker " + id.ToString(CultureInfo.InvariantCulture);
      _nodeConversationSpeakerNames[id] = label;
      return label;
    }

    private static String NodeConversationChoiceOrLine(DialogNode node) {
      if (node == null) return null;
      String option = NodeConversationLocalizedText(node.LocalizedOptionText, null);
      if (node.IsPlayerNode && !String.IsNullOrWhiteSpace(option)) return option;
      return NodeConversationLocalizedText(node.LocalizedText, node.Text);
    }

    private static String NodeConversationLocalizedText(Dictionary<String, String> localized, String fallback) {
      String text = StringTable.SelectLocalizedText(localized, fallback);
      return String.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    private static String OneLineNodeConversationText(String text) {
      if (String.IsNullOrWhiteSpace(text)) return String.Empty;
      return String.Join(" ", text.Replace('\r', ' ').Replace('\n', ' ')
        .Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries));
    }

    private static TreeNode FirstNodeConversationDialogTreeNode(TreeNodeCollection nodes) {
      if (nodes == null) return null;
      foreach (TreeNode node in nodes) {
        if (node?.Tag is NodeConversationTreeTag tag && tag.Node != null) return node;
        TreeNode child = FirstNodeConversationDialogTreeNode(node?.Nodes);
        if (child != null) return child;
      }
      return null;
    }

    private void UpdateNodeConversationDetails(TreeNode selected) {
      if (_nodeConversationDetails == null) return;
      if (selected?.Tag is not NodeConversationTreeTag tag) {
        _nodeConversationDetails.Text = selected?.Text ?? String.Empty;
        return;
      }
      if (tag.Node == null) {
        _nodeConversationDetails.Text = tag.Note ?? selected.Text;
        return;
      }

      DialogNode node = tag.Node;
      Conversation conversation = _nodeConversation;
      StringBuilder sb = new StringBuilder();
      sb.AppendLine("Dialog node #" + node.NodeId);
      if (tag.RawNodeId != tag.NodeId) sb.AppendLine("Reached through link node: " + tag.RawNodeId + " → " + tag.NodeId);
      if (tag.IsCycle) sb.AppendLine("Tree edge: cycle back to an ancestor");
      else if (tag.IsRejoin) sb.AppendLine("Tree edge: rejoins a node already shown elsewhere");
      sb.AppendLine("Speaker: " + NodeConversationSpeakerName(conversation, node));
      UInt64 speakerId = node.SpeakerId != 0 ? node.SpeakerId : conversation?.DefaultSpeakerId ?? 0;
      if (speakerId != 0) sb.AppendLine("Speaker id: " + speakerId);
      sb.AppendLine("Player choice: " + node.IsPlayerNode);
      sb.AppendLine("Ambient: " + node.IsAmbient);
      if (node.IsEmpty) sb.AppendLine("Empty node: true");
      if (node.AbortsConversation) sb.AppendLine("Aborts conversation: true");
      if (node.JoinDisabledForHolocom) sb.AppendLine("Holocom join disabled: true");
      if (node.ChoiceDisabledForHolocom) sb.AppendLine("Holocom choice disabled: true");
      if (node.MinLevel >= 0 || node.MaxLevel >= 0)
        sb.AppendLine("Level condition: " + (node.MinLevel >= 0 ? node.MinLevel.ToString() : "-") + " .. " + (node.MaxLevel >= 0 ? node.MaxLevel.ToString() : "-"));
      if (!String.IsNullOrWhiteSpace(node.ActionHook)) sb.AppendLine("Action hook: " + node.ActionHook);
      if (node.AlignmentGain.ToString() != "None") sb.AppendLine("Alignment reward: " + node.AlignmentGain);
      if (node.CreditsGained != 0) sb.AppendLine("Credits: " + node.CreditsGained.ToString("N0"));
      if (node.GenericNodeNumber != 0) sb.AppendLine("Generic line node: " + node.GenericNodeNumber);
      if (!String.IsNullOrWhiteSpace(node.CnvAlienVOFQN)) sb.AppendLine("Alien VO: " + node.CnvAlienVOFQN + " #" + node.CnvAlienVONode);
      if (!String.IsNullOrWhiteSpace(node.Stb)) sb.AppendLine("String table: " + node.Stb);

      String option = NodeConversationLocalizedText(node.LocalizedOptionText, null);
      String line = NodeConversationLocalizedText(node.LocalizedText, node.Text);
      if (!String.IsNullOrWhiteSpace(option)) { sb.AppendLine(); sb.AppendLine("Option text:"); sb.AppendLine(option.Trim()); }
      if (!String.IsNullOrWhiteSpace(line)) { sb.AppendLine(); sb.AppendLine("Dialog text:"); sb.AppendLine(line.Trim()); }

      AppendNodeConversationQuestList(sb, "Grants quest", node.QuestsGranted);
      AppendNodeConversationQuestList(sb, "Ends quest", node.QuestsEnded);
      AppendNodeConversationQuestList(sb, "Progresses quest", node.QuestsProgressed);
      if (node.ActionQuest != 0) AppendNodeConversationQuestList(sb, "Action quest", new[] { node.ActionQuest });
      if (node.QuestReward != 0) AppendNodeConversationQuestList(sb, "Quest reward", new[] { node.QuestReward });

      if (node.AffectionRewardEvents != null && node.AffectionRewardEvents.Count > 0) {
        sb.AppendLine(); sb.AppendLine("Companion reactions:");
        foreach (KeyValuePair<Int64, KeyValuePair<Int32, String>> reward in node.AffectionRewardEvents)
          sb.AppendLine("  " + reward.Key + ": " + reward.Value.Key + (String.IsNullOrWhiteSpace(reward.Value.Value) ? String.Empty : " — " + reward.Value.Value));
      }

      if (node.ChildIds != null && node.ChildIds.Count > 0) {
        sb.AppendLine(); sb.AppendLine("Children:");
        foreach (Int32 child in node.ChildIds) {
          Int64 target = ResolveNodeConversationLinkTarget(conversation, child, out List<Int64> links);
          sb.AppendLine(links != null && links.Count > 0 ? "  " + child + " → link → " + target : "  " + child);
        }
      } else { sb.AppendLine(); sb.AppendLine("Children: none (terminal node)"); }
      _nodeConversationDetails.Text = sb.ToString().TrimEnd();
    }

    private void AppendNodeConversationQuestList(StringBuilder sb, String label, IEnumerable<UInt64> ids) {
      if (sb == null || ids == null) return;
      foreach (UInt64 id in ids.Where(x => x != 0).Distinct()) {
        String name = ResolveNodeName(id);
        sb.AppendLine();
        sb.Append(label).Append(": ").Append(name).Append(" [").Append(id).AppendLine("]");
      }
    }

    private void NavigateSelectedNodeConversationSpeaker() {
      if (_nodeConversationTree?.SelectedNode?.Tag is not NodeConversationTreeTag tag || tag.Node == null) return;
      UInt64 id = tag.Node.SpeakerId != 0 ? tag.Node.SpeakerId : _nodeConversation?.DefaultSpeakerId ?? 0;
      if (id == 0) return;
      try { NavigateToNodeName(_currentDom?.GetObject(id)?.Name); } catch { }
    }

    private void NavigateSelectedNodeConversationQuest() {
      if (_nodeConversationTree?.SelectedNode?.Tag is not NodeConversationTreeTag tag || tag.Node == null) return;
      DialogNode node = tag.Node;
      UInt64 id = node.ActionQuest != 0 ? node.ActionQuest
        : node.QuestReward != 0 ? node.QuestReward
        : node.QuestsGranted?.FirstOrDefault() ?? 0;
      if (id == 0) id = node.QuestsProgressed?.FirstOrDefault() ?? 0;
      if (id == 0) id = node.QuestsEnded?.FirstOrDefault() ?? 0;
      if (id == 0) return;
      try { NavigateToNodeName(_currentDom?.GetObject(id)?.Name); } catch { }
    }

    private void FindNextNodeConversationTreeMatch() {
      if (_nodeConversationTree == null || _nodeConversationSearch == null) return;
      String needle = _nodeConversationSearch.Text?.Trim();
      if (String.IsNullOrWhiteSpace(needle)) return;
      List<TreeNode> nodes = new List<TreeNode>();
      CollectNodeConversationTreeNodes(_nodeConversationTree.Nodes, nodes);
      if (nodes.Count == 0) return;
      for (Int32 offset = 1; offset <= nodes.Count; offset++) {
        Int32 index = (_nodeConversationSearchIndex + offset + nodes.Count) % nodes.Count;
        TreeNode node = nodes[index];
        String haystack = node.Text ?? String.Empty;
        if (haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0) continue;
        _nodeConversationSearchIndex = index;
        _nodeConversationTree.SelectedNode = node;
        node.EnsureVisible();
        _nodeConversationTree.Focus();
        return;
      }
      System.Media.SystemSounds.Beep.Play();
    }

    private static void CollectNodeConversationTreeNodes(TreeNodeCollection source, List<TreeNode> target) {
      if (source == null || target == null) return;
      foreach (TreeNode node in source) {
        target.Add(node);
        CollectNodeConversationTreeNodes(node.Nodes, target);
      }
    }

    private void CopyNodeConversationTree() {
      if (_nodeConversationTree == null) return;
      StringBuilder sb = new StringBuilder();
      if (_nodeConversation != null) sb.AppendLine(_nodeConversation.Fqn ?? "Conversation");
      foreach (TreeNode node in _nodeConversationTree.Nodes) AppendNodeConversationTreeText(sb, node, 0);
      String text = sb.ToString().TrimEnd();
      if (String.IsNullOrWhiteSpace(text)) return;
      try { Clipboard.SetText(text); } catch { }
    }

    private static void AppendNodeConversationTreeText(StringBuilder sb, TreeNode node, Int32 depth) {
      if (sb == null || node == null) return;
      sb.Append(' ', Math.Max(0, depth) * 2).AppendLine(node.Text);
      foreach (TreeNode child in node.Nodes) AppendNodeConversationTreeText(sb, child, depth + 1);
    }

    private static Object NodeConversationDataValue(GomObjectData data, String name, String numericName) {
      if (data == null) return null;
      if (!String.IsNullOrWhiteSpace(name) && data.Dictionary.TryGetValue(name, out Object value)) return value;
      return !String.IsNullOrWhiteSpace(numericName) && data.Dictionary.TryGetValue(numericName, out value) ? value : null;
    }

    private static IEnumerable<KeyValuePair<Object, Object>> NodeConversationPairs(Object value) {
      if (value is GomObjectData gom) {
        foreach (KeyValuePair<String, Object> pair in gom.Dictionary) {
          if (String.Equals(pair.Key, "_count", StringComparison.OrdinalIgnoreCase)) continue;
          yield return new KeyValuePair<Object, Object>(pair.Key, pair.Value);
        }
        yield break;
      }
      if (value is IDictionary dictionary) {
        foreach (DictionaryEntry entry in dictionary)
          yield return new KeyValuePair<Object, Object>(entry.Key, entry.Value);
      }
    }

    private static List<Object> NodeConversationListEntries(Object value) {
      List<Object> result = new List<Object>();
      if (value == null || value is String) return result;
      if (value is GomObjectData gom) {
        List<KeyValuePair<Int32, Object>> ordered = new List<KeyValuePair<Int32, Object>>();
        Int32 fallback = 1000000;
        foreach (KeyValuePair<String, Object> pair in gom.Dictionary) {
          if (String.Equals(pair.Key, "_count", StringComparison.OrdinalIgnoreCase)) continue;
          Int32 order = Int32.TryParse(pair.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out Int32 parsed) ? parsed : fallback++;
          ordered.Add(new KeyValuePair<Int32, Object>(order, pair.Value));
        }
        result.AddRange(ordered.OrderBy(x => x.Key).Select(x => x.Value));
        return result;
      }
      if (value is IDictionary dictionary) {
        List<KeyValuePair<Int32, Object>> ordered = new List<KeyValuePair<Int32, Object>>();
        Int32 fallback = 1000000;
        foreach (DictionaryEntry entry in dictionary) {
          String key = entry.Key?.ToString();
          Int32 order = Int32.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out Int32 parsed) ? parsed : fallback++;
          ordered.Add(new KeyValuePair<Int32, Object>(order, entry.Value));
        }
        result.AddRange(ordered.OrderBy(x => x.Key).Select(x => x.Value));
        return result;
      }
      if (value is IEnumerable enumerable) foreach (Object item in enumerable) result.Add(item);
      return result;
    }

    private static Object NodeConversationMapValueById(Object map, Int64 id) {
      foreach (KeyValuePair<Object, Object> pair in NodeConversationPairs(map))
        if (NodeConversationInt64(pair.Key) == id) return pair.Value;
      return null;
    }

    private static Boolean NodeConversationBool(Object value) {
      if (value == null) return false;
      if (value is Boolean flag) return flag;
      try { return Convert.ToInt64(value, CultureInfo.InvariantCulture) != 0; }
      catch { return Boolean.TryParse(value.ToString(), out Boolean parsed) && parsed; }
    }

    private static Int64 NodeConversationInt64(Object value) {
      if (value == null) return 0;
      try {
        if (value is Int64 l) return l;
        if (value is UInt64 ul) return unchecked((Int64)ul);
        if (value is Int32 i) return i;
        if (value is UInt32 ui) return ui;
        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
      } catch { return 0; }
    }

    private static UInt64 NodeConversationUInt64(Object value) {
      if (value == null) return 0;
      try {
        if (value is UInt64 ul) return ul;
        if (value is Int64 l) return unchecked((UInt64)l);
        if (value is UInt32 ui) return ui;
        if (value is Int32 i) return unchecked((UInt64)(Int64)i);
        return Convert.ToUInt64(value, CultureInfo.InvariantCulture);
      } catch { return 0; }
    }

    private static String NodeConversationText(Object value) {
      if (value == null) return null;
      String text = value as String ?? value.ToString();
      return String.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    private static List<UInt64> NodeConversationEnabledIds(Object value) {
      List<UInt64> result = new List<UInt64>();
      foreach (KeyValuePair<Object, Object> pair in NodeConversationPairs(value)) {
        if (!NodeConversationBool(pair.Value)) continue;
        UInt64 id = NodeConversationUInt64(pair.Key);
        if (id != 0 && !result.Contains(id)) result.Add(id);
      }
      return result;
    }

    private void DisposeNodeConversationUi() {
      if (_nodeConversationForm != null && !_nodeConversationForm.IsDisposed) {
        try { _nodeConversationForm.Dispose(); } catch { }
      }
      _nodeConversationForm = null;
      _nodeConversation = null;
      _nodeConversationSpeakerNames.Clear();
    }
  }
}
