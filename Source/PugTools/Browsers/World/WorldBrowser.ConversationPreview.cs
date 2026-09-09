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
  public partial class WorldBrowser {
    private sealed class WorldConversationTreeTag {
      public long RawNodeId;
      public long NodeId;
      public DialogNode Node;
      public bool IsLink;
      public bool IsCycle;
      public bool IsRejoin;
      public string Note;
    }

    private Form worldConversationPreviewForm;
    private Label worldConversationPreviewHeader;
    private TreeView worldConversationPreviewTree;
    private RichTextBox worldConversationPreviewDetails;
    private Button worldConversationPreviewPlayButton;
    private ToolStripMenuItem btnWorldSelectedConversation;
    private Panel worldSelectionInfoActions;
    private Button worldSelectionConversationButton;
    private Conversation worldConversationPreviewConversation;
    private WorldNpcPlacement worldConversationPreviewPrimaryNpc;
    private WorldSpnPlacement worldConversationPreviewPrimarySpn;
    private readonly Dictionary<ulong, string> worldConversationSpeakerNames = new Dictionary<ulong, string>();

    private bool CanOpenWorldConversation(WorldInteractionInfo interaction) {
      if (interaction == null) return false;
      // Vendors/trainers/taxi NPCs can also own a quest conversation. Primary interaction precedence must not hide
      // the conversation-preview action when the underlying cnv reference is present.
      return interaction.HasConversation;
    }

    private bool CanOpenSelectedWorldConversation() {
      return CanOpenWorldConversation(panelRender?.SelectedWorldInteraction);
    }

    private void UpdateWorldSelectionInfoActions() {
      if (worldSelectionInfoActions == null) return;
      WorldInteractionInfo interaction = panelRender?.SelectedWorldInteraction;
      bool conversationAvailable = CanOpenSelectedWorldConversation();
      bool codexAvailable = CanOpenSelectedWorldCodex();
      worldSelectionInfoActions.Visible = conversationAvailable || codexAvailable;
      if (worldSelectionConversationButton != null) {
        worldSelectionConversationButton.Visible = conversationAvailable;
        if (conversationAvailable) worldSelectionConversationButton.Text = interaction?.Kind == WorldInteractionKind.MissionBoard ? "Open notice tree…" : "Open conversation tree…";
      }
      if (worldSelectionCodexButton != null) worldSelectionCodexButton.Visible = codexAvailable;
    }

    private void OpenSelectedWorldConversationPreview() {
      OpenWorldConversationPreview(panelRender?.SelectedWorldInteraction);
    }

    private void OpenWorldConversationPreview(WorldInteractionInfo interaction) {
      if (!CanOpenWorldConversation(interaction)) return;
      if (!TryLoadWorldConversation(interaction, out Conversation conversation, out string error)) {
        MessageBox.Show(this, error ?? "The conversation could not be loaded.", "Conversation preview", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return;
      }
      WorldNpcPlacement selectedNpc = panelRender?.SelectedWorldNpcPlacement;
      WorldNpcPlacement interactionNpc = panelRender?.InteractionTargetWorldNpcPlacement;
      WorldSpnPlacement selectedSpn = panelRender?.SelectedWorldSpnPlacement;
      WorldSpnPlacement interactionSpn = panelRender?.InteractionTargetWorldSpnPlacement;
      worldConversationPreviewPrimaryNpc = selectedNpc != null && ReferenceEquals(selectedNpc.Interaction, interaction) ? selectedNpc :
        interactionNpc != null && ReferenceEquals(interactionNpc.Interaction, interaction) ? interactionNpc : null;
      // Placeable conversations (notably datacrons) have no NPC anchor. Keep BOTH the Ctrl-selected SPN and the
      // ordinary interaction-pick SPN alive across the tree window so pressing "Play conversation" still knows where
      // the interaction happened. Fixed3 preserved neither in this form path, causing the fallback STG to lose its
      // world frame and making camera marks and actor animation marks silently resolve to nothing.
      worldConversationPreviewPrimarySpn = selectedSpn != null && ReferenceEquals(selectedSpn.Interaction, interaction) ? selectedSpn :
        interactionSpn != null && ReferenceEquals(interactionSpn.Interaction, interaction) ? interactionSpn : null;
      ShowWorldConversationPreview(conversation, interaction.Kind == WorldInteractionKind.MissionBoard);
    }

    private bool TryLoadWorldConversation(WorldInteractionInfo interaction, out Conversation conversation, out string error) {
      conversation = null;
      error = null;
      if (interaction == null || currentDom == null) {
        error = "No GOM data is loaded.";
        return false;
      }

      var errors = new List<string>();
      // NPC conversations carry the authoritative numeric cnvConversationId. Prefer it: standalone conversation
      // prototypes are stored by id and old clients do not always provide a usable name beside the id.
      if (interaction.ConversationId != 0) {
        try { conversation = currentDom.ConversationLoader.Load(interaction.ConversationId); }
        catch (Exception ex) { errors.Add("id " + interaction.ConversationId + ": " + ex.Message); }
      }
      if (conversation == null && !String.IsNullOrWhiteSpace(interaction.Conversation)) {
        string reference = interaction.Conversation.Trim();
        if (UInt64.TryParse(reference, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong numeric) && numeric != 0) {
          try { conversation = currentDom.ConversationLoader.Load(numeric); }
          catch (Exception ex) { errors.Add("id " + numeric + ": " + ex.Message); }
        }
        // Keep the established 64-bit path first: use the authored reference exactly as before. Legacy path/FQN
        // normalization and deterministic-id recovery are appended only for a DOM explicitly identified as legacy.
        if (conversation == null) {
          try { conversation = currentDom.ConversationLoader.Load(reference); }
          catch (Exception ex) { errors.Add(reference + ": " + ex.Message); }
        }
        if (conversation == null && WorldUsesLegacyContent) {
          string normalized = WorldNormalizePrototypeReference(reference) ?? reference;
          if (!String.Equals(normalized, reference, StringComparison.OrdinalIgnoreCase)) {
            try { conversation = currentDom.ConversationLoader.Load(normalized); }
            catch (Exception ex) { errors.Add(normalized + ": " + ex.Message); }
          }
          if (conversation == null) {
            // RED can omit the FQN from the client.gom name table even though the prototype is present by id.
            ulong folded = WorldPrototypeFqnToId(normalized);
            if (folded != 0) {
              try { conversation = currentDom.ConversationLoader.Load(folded); }
              catch (Exception ex) { errors.Add("folded id " + folded + ": " + ex.Message); }
            }
          }
        }
      }
      if (conversation != null) { CaptureWorldConversationConditions(conversation); return true; }

      // ConversationLoader also resolves companion/reaction convenience data which some beta GOMs simply do not
      // contain. A preview only needs the tree itself, so fall back to a deliberately small raw-node reader instead
      // of declaring an otherwise perfectly readable RED/Beta conversation broken because an unrelated helper table
      // was absent. This path is read-only and does not alter the DOM/loaders used by the other browsers.
      GomObject raw = null;
      try {
        if (interaction.ConversationId != 0) raw = WorldResolveGomObject(interaction.ConversationId);
        if (raw == null && !String.IsNullOrWhiteSpace(interaction.Conversation)) raw = WorldResolveGomObject(interaction.Conversation);
        string fallbackError = null;
        if (raw != null && TryBuildWorldConversationCore(raw, out conversation, out fallbackError)) {
          // RED/Beta objects are sometimes present only by deterministic prototype id and therefore have no Name in
          // the client GOM name map.  Preserve the FQN the interaction/source path gave us so STB context, headers and
          // cross-navigation still behave like Jedipedia instead of showing an anonymous conversation.
          if (String.IsNullOrWhiteSpace(conversation.Fqn)) {
            if (!String.IsNullOrWhiteSpace(interaction.Conversation))
              conversation.Fqn = WorldNormalizePrototypeReference(interaction.Conversation) ?? interaction.Conversation.Trim();
            if (String.IsNullOrWhiteSpace(conversation.Fqn) && interaction.ConversationId != 0)
              conversation.Fqn = WorldLegacyPrototypeName(interaction.ConversationId, "cnv");
          }
          CaptureWorldConversationConditions(conversation);
          return true;
        }
        if (!String.IsNullOrWhiteSpace(fallbackError)) errors.Add("core reader: " + fallbackError);
      } catch (Exception ex) {
        errors.Add("core reader: " + ex.Message);
      }

      string referenceText = interaction.ConversationId != 0 ? interaction.ConversationId.ToString(CultureInfo.InvariantCulture) : interaction.Conversation;
      error = "Conversation " + (String.IsNullOrWhiteSpace(referenceText) ? "(unknown)" : referenceText) + " could not be loaded.";
      if (errors.Count > 0) error += "\r\n\r\n" + String.Join("\r\n", errors.Distinct());
      return false;
    }

    private bool TryBuildWorldConversationCore(GomObject obj, out Conversation conversation, out string error) {
      conversation = null;
      error = null;
      if (obj?.Data == null) { error = "node has no data"; return false; }
      try {
        var result = new Conversation {
          Id = obj.Id,
          Fqn = obj.Name,
          Dom_ = currentDom,
          References = obj.References,
          IsKOTORStyle = obj.Data.ValueOrDefault("cnvIsKOTORStyle", false) ||
            (WorldUsesLegacyContent && WorldConversationBool(WorldInteractionDataValue(obj.Data, "cnvIsClassicKotORDialog", "4611686303632834000"))),
          DefaultSpeakerId = WorldInteractionUnsigned(WorldInteractionDataValue(obj.Data, "cnvDefaultSpeaker", "4611686068585531195"))
        };
        if (result.DefaultSpeakerId != 0 && !result.SpeakersIds.Contains(result.DefaultSpeakerId)) result.SpeakersIds.Add(result.DefaultSpeakerId);

        object rawDialogs = WorldInteractionDataValue(obj.Data, "cnvTreeDialogNodes_Prototype", "4611686050212071021");
        var dialogRows = new List<KeyValuePair<object, GomObjectData>>();
        foreach (KeyValuePair<object, object> entry in WorldConversationMapEntries(rawDialogs)) {
          GomObjectData data = WorldConversationFirstObjectData(entry.Value, false);
          // Some RED builds put another wrapper around every ClassView in the lookup list.  Keep the normal direct
          // 64-bit shape first and unwrap only when the selected world really is legacy.
          if (WorldUsesLegacyContent && (data == null || WorldInteractionDataValue(data, "cnvNodeNumber", "4611686019044571365") == null))
            data = WorldConversationFindObjectDataWithField(entry.Value, "cnvNodeNumber", "4611686019044571365");
          if (data != null) dialogRows.Add(new KeyValuePair<object, GomObjectData>(entry.Key, data));
        }
        if (dialogRows.Count == 0 && WorldUsesLegacyContent) {
          foreach (GomObjectData data in WorldConversationFindAllObjectDataWithField(rawDialogs, "cnvNodeNumber", "4611686019044571365"))
            dialogRows.Add(new KeyValuePair<object, GomObjectData>(0L, data));
        }
        foreach (KeyValuePair<object, GomObjectData> entry in dialogRows) {
            GomObjectData data = entry.Value;
            long fallbackId = WonkInt64(entry.Key);
            long nodeId = WonkInt64(WorldInteractionDataValue(data, "cnvNodeNumber", "4611686019044571365"));
            if (nodeId == 0) nodeId = fallbackId;
            if (nodeId == 0) continue;
            var node = new DialogNode {
              Conversation = result,
              NodeId = nodeId,
              MinLevel = (int)WonkInt64(WorldInteractionDataValue(data, "cnvLevelConditionMin", "4611686019674493758")),
              MaxLevel = (int)WonkInt64(WorldInteractionDataValue(data, "cnvLevelConditionMax", "4611686019674493755")),
              IsEmpty = WorldConversationBool(WorldInteractionDataValue(data, "cnvIsEmpty", "4611686019093994683")),
              IsAmbient = WorldConversationBool(WorldInteractionDataValue(data, "cnvIsAmbient", "4611686030912475398")) ||
                (WorldUsesLegacyContent && WorldConversationBool(WorldInteractionDataValue(data, null, "4611686092038169994"))),
              JoinDisabledForHolocom = WorldConversationBool(WorldInteractionDataValue(data, "cnvIsJoinDisabledForHolocom", "4611686074873607191")),
              ChoiceDisabledForHolocom = WorldConversationBool(WorldInteractionDataValue(data, "cnvIsVoteWinDisabledForHolocom", "4611686070094731191")),
              AbortsConversation = WorldConversationBool(WorldInteractionDataValue(data, "cnvAbortConversation", "4611686041734970002")),
              IsPlayerNode = WorldConversationBool(WorldInteractionDataValue(data, "cnvIsPcNode", "4611686019058500344")),
              GenericNodeNumber = WonkInt64(WorldInteractionDataValue(data, "cnvGenericNodeNumber", "4611686019251991207")),
              CnvAlienVOFQN = WorldInteractionText(WorldInteractionDataValue(data, "cnvAlienVOConvoFQN", "4611686037539970007")) ?? String.Empty,
              CnvAlienVONode = WonkInt64(WorldInteractionDataValue(data, "cnvAlienVONodeNumber", "4611686037539970006")),
              ActionHook = WorldInteractionText(WorldInteractionDataValue(data, "cnvActionHook", "4611686019157992533")),
              SpeakerId = WorldInteractionUnsigned(WorldInteractionDataValue(data, "cnvSpeaker", "4611686068585531196")),
              ActionQuest = WorldInteractionUnsigned(WorldInteractionDataValue(data, "cnvActionQuest", "4611686244754080006")),
              QuestReward = WorldInteractionUnsigned(WorldInteractionDataValue(data, "cnvRewardQuest", "4611686226452830003")),
              ChildIds = WorldInteractionListEntries(WorldInteractionDataValue(data, "cnvChildNodes", "4611686019044571321")).Select(x => unchecked((int)WonkInt64(x))).ToList(),
              QuestsGranted = WorldConversationEnabledIds(WorldInteractionDataValue(data, "cnvNodeQuestGrants", "4611686245059810000")),
              QuestsEnded = WorldConversationEnabledIds(WorldInteractionDataValue(data, "cnvNodeQuestEnds", "4611686245059810001")),
              QuestsProgressed = WorldConversationEnabledIds(WorldInteractionDataValue(data, "cnvNodeQuestProgress", "4611686245059810002")),
              AffectionRewardEvents = new Dictionary<long, KeyValuePair<int, string>>(),
              AffectionRewardEventsB62 = new Dictionary<string, KeyValuePair<int, Dictionary<string, string>>>()
            };
            if (node.MinLevel == 0 && WorldInteractionDataValue(data, "cnvLevelConditionMin", "4611686019674493758") == null) node.MinLevel = -1;
            if (node.MaxLevel == 0 && WorldInteractionDataValue(data, "cnvLevelConditionMax", "4611686019674493755") == null) node.MaxLevel = -1;
            if (node.SpeakerId != 0 && !result.SpeakersIds.Contains(node.SpeakerId)) result.SpeakersIds.Add(node.SpeakerId);

            object textMap = WorldInteractionDataValue(data, "locTextRetrieverMap", "4611686102842470023");
            object textValue = WorldConversationMapValueById(textMap, nodeId);
            GomObjectData textData = WorldConversationFirstObjectData(textValue, false);
            // Current raw nodes often key the retriever map by dialog node id. RED beta uses a one-entry loc map whose
            // key is the text slot instead and can wrap the retriever ClassView once more. Preserve current behaviour
            // first; the recursive lookup is legacy-only.
            if (textData == null && WorldUsesLegacyContent) {
              object firstTextValue = WorldConversationMapEntries(textMap).Select(x => x.Value).FirstOrDefault();
              textData = WorldConversationFirstObjectData(firstTextValue, false)
                ?? WorldConversationFindObjectDataWithField(firstTextValue, "strLocalizedTextRetrieverStringID", "4611686093000569992")
                ?? WorldConversationFindObjectDataWithField(textMap, "strLocalizedTextRetrieverStringID", "4611686093000569992")
                ?? WorldConversationFindObjectDataWithField(textMap, "strLocalizedTextRetrieverBucket", "4611686093000569993");
            }
            if (textData != null) {
              if (TryWorldLocEntry(textData, out GomLib.StringTable.LocEntry locEntry)) node.Stb = locEntry.Bucket;
              node.Text = WorldLocText(textData, result.Fqn);
              node.LocalizedText = WorldLocAll(textData);
              node.LocalizedOptionText = WorldLocAll(textData, true);
            }
            result.DialogNodes.Add(node);
            result.NodeLookup[node.NodeId] = node;
        }

        if (result.NodeLookup.Count == 0) {
          string rawType = rawDialogs == null ? "null" : rawDialogs.GetType().FullName;
          string sampleTypes = String.Join(", ", WorldConversationMapEntries(rawDialogs).Take(4)
            .Select(x => x.Value == null ? "null" : x.Value.GetType().FullName).Distinct());
          error = "cnvTreeDialogNodes_Prototype contains no readable dialog nodes (container " + rawType
            + (String.IsNullOrWhiteSpace(sampleTypes) ? String.Empty : "; values " + sampleTypes) + ")";
          return false;
        }

        object rawRoot = WorldInteractionDataValue(obj.Data, "cnvTreeRootNode_Prototype", "4611686247405460000");
        GomObjectData rootData = WorldConversationFirstObjectData(rawRoot, false);
        if (WorldUsesLegacyContent && (rootData == null || WorldInteractionDataValue(rootData, "cnvChildNodes", "4611686019044571321") == null))
          rootData = WorldConversationFindObjectDataWithField(rawRoot, "cnvChildNodes", "4611686019044571321");
        if (rootData == null) {
          // RED/Beta stores a plural root-node container instead of the later single root object.
          object legacyRoots = WorldInteractionDataValue(obj.Data, "cnvTreeRootNodes_Prototype", "4611686050212071023");
          rootData = WorldInteractionListEntries(legacyRoots).Select(x => WorldConversationFirstObjectData(x, false)).FirstOrDefault(x => x != null);
          if (rootData == null) rootData = WorldConversationFirstObjectData(legacyRoots, true);
          if (rootData == null || WorldInteractionDataValue(rootData, "cnvChildNodes", "4611686019044571321") == null)
            rootData = WorldConversationFindObjectDataWithField(legacyRoots, "cnvChildNodes", "4611686019044571321");
        }
        if (rootData != null) {
          int index = 0;
          foreach (object value in WorldInteractionListEntries(WorldInteractionDataValue(rootData, "cnvChildNodes", "4611686019044571321"))) {
            long id = WonkInt64(value);
            if (id != 0) result.RootNodes[index++] = id;
          }
        }

        object rawLinks = WorldInteractionDataValue(obj.Data, "cnvTreeLinkNodes_Prototype", "4611686050212071022");
        foreach (KeyValuePair<object, object> entry in WorldConversationMapEntries(rawLinks)) {
          GomObjectData linkData = WorldConversationFirstObjectData(entry.Value, false);
          if (WorldUsesLegacyContent && (linkData == null || WorldInteractionDataValue(linkData, "cnvLinkTarget", "4611686019044820878") == null))
            linkData = WorldConversationFindObjectDataWithField(entry.Value, "cnvLinkTarget", "4611686019044820878");
          if (linkData == null) continue;
          long linkId = WorldUsesLegacyContent
            ? WonkInt64(WorldInteractionDataValue(linkData, "cnvNodeNumber", "4611686019044571365"))
            : 0;
          if (linkId == 0) linkId = WonkInt64(entry.Key);
          if (linkId == 0) continue;
          long target = WonkInt64(WorldInteractionDataValue(linkData, "cnvLinkTarget", "4611686019044820878"));
          if (target != 0) result.NodeLinkList[linkId] = target;
        }
        conversation = result;
        return true;
      } catch (Exception ex) {
        error = ex.Message;
        return false;
      }
    }

    // WorldConversationMapEntries is shared with the cinematic/playback legacy compatibility layer.

    private static object WorldConversationMapValueById(object map, long id) {
      foreach (KeyValuePair<object, object> entry in WorldConversationMapEntries(map))
        if (WonkInt64(entry.Key) == id) return entry.Value;
      return null;
    }

    private static bool WorldConversationBool(object value) {
      if (value == null) return false;
      if (value is bool flag) return flag;
      try { return Convert.ToInt64(value, CultureInfo.InvariantCulture) != 0; } catch { return Boolean.TryParse(value.ToString(), out bool parsed) && parsed; }
    }

    private static List<ulong> WorldConversationEnabledIds(object value) {
      var result = new List<ulong>();
      foreach (KeyValuePair<object, object> entry in WorldConversationMapEntries(value)) {
        if (!WorldConversationBool(entry.Value)) continue;
        ulong id = WorldInteractionUnsigned(entry.Key);
        if (id != 0 && !result.Contains(id)) result.Add(id);
      }
      return result;
    }

    private void ShowWorldConversationPreview(Conversation conversation, bool noticeBoard) {
      if (conversation == null) return;
      EnsureWorldConversationPreviewForm();
      worldConversationPreviewConversation = conversation;
      worldConversationSpeakerNames.Clear();

      string title = noticeBoard ? "Mission-board notice tree" : "Conversation preview";
      worldConversationPreviewForm.Text = title;
      int nodeCount = conversation.DialogNodes?.Count ?? conversation.NodeLookup?.Count ?? 0;
      int rootCount = conversation.RootNodes?.Count ?? 0;
      worldConversationPreviewHeader.Text = (conversation.Fqn ?? "(unnamed conversation)") + "\r\n" +
        nodeCount + " dialog nodes   •   " + rootCount + " roots" + (conversation.IsKOTORStyle ? "   •   KOTOR-style" : String.Empty);
      if (worldConversationPreviewPlayButton != null) worldConversationPreviewPlayButton.Visible = !noticeBoard;

      RebuildWorldConversationTree(conversation);
      if (!worldConversationPreviewForm.Visible) worldConversationPreviewForm.Show(this);
      else worldConversationPreviewForm.BringToFront();
      worldConversationPreviewForm.Focus();
    }

    private void EnsureWorldConversationPreviewForm() {
      if (worldConversationPreviewForm != null && !worldConversationPreviewForm.IsDisposed) return;

      worldConversationPreviewForm = new Form {
        Text = "Conversation preview",
        FormBorderStyle = FormBorderStyle.SizableToolWindow,
        ShowInTaskbar = false,
        StartPosition = FormStartPosition.CenterParent,
        MinimumSize = new Size(720, 460),
        Size = new Size(980, 680),
        BackColor = Color.FromArgb(13, 24, 29)
      };
      worldConversationPreviewHeader = new Label {
        Dock = DockStyle.Top,
        Height = 54,
        Padding = new Padding(10, 7, 10, 5),
        AutoEllipsis = true,
        Font = new Font(Font, FontStyle.Bold),
        ForeColor = Color.FromArgb(135, 220, 242),
        BackColor = Color.FromArgb(8, 19, 23)
      };

      var split = new SplitContainer {
        Dock = DockStyle.Fill,
        Orientation = Orientation.Vertical,
        BackColor = Color.FromArgb(31, 49, 55)
      };
      worldConversationPreviewTree = new TreeView {
        Dock = DockStyle.Fill,
        HideSelection = false,
        BorderStyle = BorderStyle.None,
        BackColor = Color.FromArgb(13, 24, 29),
        ForeColor = Color.Gainsboro,
        Font = new Font(FontFamily.GenericMonospace, 9f),
        ShowNodeToolTips = true
      };
      worldConversationPreviewDetails = new RichTextBox {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        BorderStyle = BorderStyle.None,
        BackColor = Color.FromArgb(13, 24, 29),
        ForeColor = Color.Gainsboro,
        Font = new Font(FontFamily.GenericMonospace, 9f),
        DetectUrls = false,
        ScrollBars = RichTextBoxScrollBars.Vertical
      };
      worldConversationPreviewTree.AfterSelect += (_, e) => UpdateWorldConversationNodeDetails(e.Node);
      split.Panel1.Controls.Add(worldConversationPreviewTree);
      split.Panel2.Controls.Add(worldConversationPreviewDetails);

      var buttons = new FlowLayoutPanel {
        Dock = DockStyle.Bottom,
        Height = 42,
        FlowDirection = FlowDirection.RightToLeft,
        Padding = new Padding(7, 6, 7, 4),
        WrapContents = false,
        BackColor = Color.FromArgb(8, 19, 23)
      };
      var close = WorldDarkActionButton("Close", 92);
      var copyTree = WorldDarkActionButton("Copy tree", 104);
      var copyNode = WorldDarkActionButton("Copy node", 104);
      worldConversationPreviewPlayButton = WorldDarkActionButton("Play conversation", 142);
      close.Click += (_, __) => worldConversationPreviewForm.Hide();
      copyTree.Click += (_, __) => CopyWorldConversationTree();
      copyNode.Click += (_, __) => {
        string text = worldConversationPreviewDetails?.Text ?? String.Empty;
        if (String.IsNullOrWhiteSpace(text)) return;
        try { Clipboard.SetText(text); SetStatusLabel("Conversation node details copied to clipboard."); } catch (Exception ex) { SetStatusLabel("Could not copy conversation node: " + ex.Message); }
      };
      buttons.Controls.Add(close);
      buttons.Controls.Add(copyTree);
      buttons.Controls.Add(copyNode);
      buttons.Controls.Add(worldConversationPreviewPlayButton);
      worldConversationPreviewPlayButton.Click += (_, __) => {
        if (worldConversationPreviewConversation == null) return;
        long startNode = 0;
        if (worldConversationPreviewTree?.SelectedNode?.Tag is WorldConversationTreeTag tag && tag.Node != null)
          startNode = tag.RawNodeId != 0 ? tag.RawNodeId : tag.NodeId;
        StartWorldConversationPlayback(worldConversationPreviewConversation, worldConversationPreviewPrimaryNpc,
          worldConversationPreviewPrimarySpn, startNode != 0 ? (long?)startNode : null);
      };

      worldConversationPreviewForm.Controls.Add(split);
      worldConversationPreviewForm.Controls.Add(buttons);

      // SplitContainer starts life with a tiny default client width. Setting Panel2MinSize in the object initializer
      // can therefore throw before WinForms has laid the dialog out (especially with non-100% DPI). Apply the
      // authored 300/260 minima only after the real size exists, and clamp the splitter to whatever space is
      // actually available.
      Action layoutSplit = () => {
        if (split.IsDisposed) return;
        int width = Math.Max(0, split.ClientSize.Width);
        if (width <= split.SplitterWidth + 2) return;
        int maxPanel1 = Math.Max(1, width - split.SplitterWidth - 1);
        int p1Min = Math.Min(300, Math.Max(0, maxPanel1 - 1));
        int p2Min = Math.Min(260, Math.Max(0, width - split.SplitterWidth - p1Min - 1));
        try {
          split.Panel1MinSize = p1Min;
          split.Panel2MinSize = p2Min;
          int minDistance = split.Panel1MinSize;
          int maxDistance = Math.Max(minDistance, width - split.SplitterWidth - split.Panel2MinSize);
          split.SplitterDistance = Math.Max(minDistance, Math.Min(510, maxDistance));
        } catch (InvalidOperationException) {
          // A resize/DPI layout can transiently report an impossible width. The next layout pass retries.
        }
      };
      split.SizeChanged += (_, __) => layoutSplit();
      worldConversationPreviewForm.Shown += (_, __) => layoutSplit();
      worldConversationPreviewForm.Controls.Add(worldConversationPreviewHeader);
      worldConversationPreviewForm.FormClosing += (_, e) => {
        if (_closing) return;
        e.Cancel = true;
        worldConversationPreviewForm.Hide();
      };
      worldConversationPreviewForm.FormClosed += (_, __) => {
        worldConversationPreviewForm = null;
        worldConversationPreviewHeader = null;
        worldConversationPreviewTree = null;
        worldConversationPreviewDetails = null;
        worldConversationPreviewPlayButton = null;
        worldConversationPreviewConversation = null;
        worldConversationPreviewPrimaryNpc = null;
        worldConversationPreviewPrimarySpn = null;
        worldConversationSpeakerNames.Clear();
      };
    }

    private void RebuildWorldConversationTree(Conversation conversation) {
      if (worldConversationPreviewTree == null) return;
      worldConversationPreviewTree.BeginUpdate();
      try {
        worldConversationPreviewTree.Nodes.Clear();
        worldConversationPreviewDetails.Clear();
        if (conversation == null || conversation.NodeLookup == null || conversation.NodeLookup.Count == 0) {
          worldConversationPreviewTree.Nodes.Add(new TreeNode("(no dialog nodes)"));
          return;
        }

        List<KeyValuePair<int, long>> roots = conversation.RootNodes?.OrderBy(x => x.Key).ToList() ?? new List<KeyValuePair<int, long>>();
        if (roots.Count == 0) roots = InferWorldConversationRoots(conversation);
        var expanded = new HashSet<long>();
        int budget = Math.Max(100, Math.Min(5000, conversation.NodeLookup.Count * 4 + (conversation.NodeLinkList?.Count ?? 0) * 2));
        foreach (var root in roots) {
          long rootNodeId = ResolveWorldConversationLinkTarget(conversation, root.Value, out _);
          string rootCondition = WorldConversationConditionSummary(conversation, rootNodeId);
          string rootLabel = "Root " + (root.Key + 1);
          var wrapper = new TreeNode(rootLabel) {
            ForeColor = Color.FromArgb(135, 220, 242),
            ToolTipText = String.IsNullOrWhiteSpace(rootCondition) ? String.Empty : "Condition: " + rootCondition
          };
          worldConversationPreviewTree.Nodes.Add(wrapper);
          AddWorldConversationTreeNode(wrapper.Nodes, conversation, root.Value, new HashSet<long>(), expanded, ref budget, 0);
          wrapper.Expand();
        }
        if (worldConversationPreviewTree.Nodes.Count == 0) worldConversationPreviewTree.Nodes.Add(new TreeNode("(no roots could be resolved)"));
        TreeNode first = FirstWorldConversationDialogTreeNode(worldConversationPreviewTree.Nodes);
        if (first != null) worldConversationPreviewTree.SelectedNode = first;
      } finally {
        worldConversationPreviewTree.EndUpdate();
      }
    }

    private List<KeyValuePair<int, long>> InferWorldConversationRoots(Conversation conversation) {
      var incoming = new HashSet<long>();
      if (conversation?.DialogNodes != null) {
        foreach (DialogNode node in conversation.DialogNodes) {
          if (node?.ChildIds == null) continue;
          foreach (int child in node.ChildIds) {
            long target = ResolveWorldConversationLinkTarget(conversation, child, out _);
            if (target != 0) incoming.Add(target);
          }
        }
      }
      var roots = conversation?.NodeLookup?.Keys.Where(x => !incoming.Contains(x)).OrderBy(x => x).ToList() ?? new List<long>();
      if (roots.Count == 0 && conversation?.NodeLookup?.Count > 0) roots.Add(conversation.NodeLookup.Keys.OrderBy(x => x).First());
      return roots.Select((id, index) => new KeyValuePair<int, long>(index, id)).ToList();
    }

    private void AddWorldConversationTreeNode(TreeNodeCollection parent, Conversation conversation, long rawId,
        HashSet<long> path, HashSet<long> expanded, ref int budget, int depth) {
      if (parent == null || conversation == null) return;
      if (budget-- <= 0) {
        parent.Add(new TreeNode("… tree truncated …") { ForeColor = Color.DarkGray });
        return;
      }
      if (depth > 256) {
        parent.Add(new TreeNode("… depth limit reached …") { ForeColor = Color.DarkGray });
        return;
      }

      long nodeId = ResolveWorldConversationLinkTarget(conversation, rawId, out List<long> links);
      if (nodeId == 0 || conversation.NodeLookup == null || !conversation.NodeLookup.TryGetValue(nodeId, out DialogNode node) || node == null) {
        string missing = links != null && links.Count > 0 ? "Link " + rawId + " → missing node " + nodeId : "Missing node " + rawId;
        parent.Add(new TreeNode(missing) { ForeColor = Color.IndianRed, Tag = new WorldConversationTreeTag { RawNodeId = rawId, NodeId = nodeId, IsLink = links != null && links.Count > 0, Note = missing } });
        return;
      }

      bool isCycle = path.Contains(nodeId);
      bool isRejoin = !isCycle && expanded.Contains(nodeId);
      string label = WorldConversationNodeLabel(conversation, node);
      if (links != null && links.Count > 0) label = "↪ " + String.Join(" → ", links.Select(x => x.ToString(CultureInfo.InvariantCulture))) + " → " + label;
      if (isCycle) label += "  [cycle]";
      else if (isRejoin) label += "  [rejoin]";

      var treeNode = new TreeNode(label) {
        Tag = new WorldConversationTreeTag { RawNodeId = rawId, NodeId = nodeId, Node = node, IsLink = links != null && links.Count > 0, IsCycle = isCycle, IsRejoin = isRejoin }
      };
      treeNode.ForeColor = node.IsPlayerNode ? Color.FromArgb(135, 220, 242) : isCycle || isRejoin ? Color.DarkGray : Color.Gainsboro;
      treeNode.ToolTipText = WorldConversationNodeTooltip(conversation, node, links);
      parent.Add(treeNode);
      if (isCycle || isRejoin) return;

      expanded.Add(nodeId);
      var nextPath = new HashSet<long>(path) { nodeId };
      if (node.ChildIds == null || node.ChildIds.Count == 0) return;
      foreach (int childId in node.ChildIds) AddWorldConversationTreeNode(treeNode.Nodes, conversation, childId, nextPath, expanded, ref budget, depth + 1);
    }

    private static long ResolveWorldConversationLinkTarget(Conversation conversation, long rawId, out List<long> links) {
      links = null;
      if (conversation == null) return rawId;
      long current = rawId;
      var seen = new HashSet<long>();
      for (int i = 0; i < 64 && conversation.NodeLinkList != null && conversation.NodeLinkList.TryGetValue(current, out long target); i++) {
        if (links == null) links = new List<long>();
        links.Add(current);
        if (!seen.Add(current) || target == current) return target;
        current = target;
      }
      return current;
    }

    private string WorldConversationNodeLabel(Conversation conversation, DialogNode node) {
      string speaker = WorldConversationSpeakerName(conversation, node);
      string text = WorldConversationChoiceOrLine(node);
      text = OneLineWorldConversationText(text);
      if (text.Length > 110) text = text.Substring(0, 109) + "…";
      string prefix = "#" + node.NodeId + "  " + speaker;
      if (node.IsPlayerNode) prefix += " [choice]";
      else if (node.IsAmbient) prefix += " [ambient]";
      if (node.IsEmpty && String.IsNullOrWhiteSpace(text)) text = "(empty)";
      return prefix + (String.IsNullOrWhiteSpace(text) ? String.Empty : ": " + text);
    }

    private string WorldConversationNodeTooltip(Conversation conversation, DialogNode node, List<long> links) {
      if (node == null) return String.Empty;
      var sb = new StringBuilder();
      if (links != null && links.Count > 0) sb.Append("Link ").Append(String.Join(" → ", links)).Append(" → ");
      sb.Append('#').Append(node.NodeId).Append(" • ").Append(WorldConversationSpeakerName(conversation, node));
      string text = OneLineWorldConversationText(WorldConversationChoiceOrLine(node));
      if (!String.IsNullOrWhiteSpace(text)) sb.Append(" • ").Append(text);
      string condition = WorldConversationConditionSummary(conversation, node.NodeId);
      if (!String.IsNullOrWhiteSpace(condition)) sb.AppendLine().Append("Condition: ").Append(condition);
      return sb.ToString();
    }

    private string WorldConversationSpeakerName(Conversation conversation, DialogNode node) {
      if (node == null) return "(unknown)";
      if (node.IsPlayerNode) return "Player";
      ulong id = node.SpeakerId != 0 ? node.SpeakerId : conversation?.DefaultSpeakerId ?? 0;
      if (id == 0) return "(no speaker)";
      if (worldConversationSpeakerNames.TryGetValue(id, out string cached)) return cached;

      string label = null;
      // Jedipedia resolves speaker labels from the raw retriever pair, not from whichever localized name happened to
      // be materialized by a model loader. This also survives a sparse selected gender row in DE/FR.
      try {
        GomObject rawSpeaker = WorldResolveGomObject(id);
        object locMap = WorldInteractionDataValue(rawSpeaker?.Data, "locTextRetrieverMap", "4611686102842470023");
        label = WorldLocMapText(locMap, WorldLocNameSlot, rawSpeaker?.Name);
      } catch { }
      try {
        if (String.IsNullOrWhiteSpace(label)) {
          GameObject speaker = currentDom?.ConversationLoader.LoadSpeaker(id);
          if (speaker is Npc npc) label = !String.IsNullOrWhiteSpace(npc.Name) ? npc.Name : npc.Fqn;
          else if (speaker is Placeable plc) label = !String.IsNullOrWhiteSpace(plc.Name) ? plc.Name : plc.Fqn;
          else if (speaker != null) label = speaker.Fqn;
        }
      } catch { }
      if (String.IsNullOrWhiteSpace(label)) {
        try { label = WorldResolveGomObject(id)?.Name; } catch { }
      }
      if (String.IsNullOrWhiteSpace(label) && WorldUsesLegacyContent)
        label = WorldLegacyPrototypeName(id, null);
      if (String.IsNullOrWhiteSpace(label)) label = "Speaker " + id.ToString(CultureInfo.InvariantCulture);
      worldConversationSpeakerNames[id] = label;
      return label;
    }

    private static string WorldConversationChoiceOrLine(DialogNode node) {
      if (node == null) return null;
      string option = WorldLocalizedText(node.LocalizedOptionText, null);
      if (node.IsPlayerNode && !String.IsNullOrWhiteSpace(option)) return option;
      return WorldLocalizedDialogText(node);
    }

    private static string OneLineWorldConversationText(string text) {
      if (String.IsNullOrWhiteSpace(text)) return String.Empty;
      return String.Join(" ", text.Replace('\r', ' ').Replace('\n', ' ').Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries));
    }

    private static TreeNode FirstWorldConversationDialogTreeNode(TreeNodeCollection nodes) {
      if (nodes == null) return null;
      foreach (TreeNode node in nodes) {
        if (node?.Tag is WorldConversationTreeTag tag && tag.Node != null) return node;
        TreeNode child = FirstWorldConversationDialogTreeNode(node?.Nodes);
        if (child != null) return child;
      }
      return null;
    }

    private void UpdateWorldConversationNodeDetails(TreeNode selected) {
      if (worldConversationPreviewDetails == null) return;
      if (!(selected?.Tag is WorldConversationTreeTag tag)) {
        worldConversationPreviewDetails.Text = selected?.Text ?? String.Empty;
        return;
      }
      if (tag.Node == null) {
        worldConversationPreviewDetails.Text = tag.Note ?? selected.Text;
        return;
      }

      DialogNode node = tag.Node;
      Conversation conversation = worldConversationPreviewConversation;
      var sb = new StringBuilder();
      sb.AppendLine("Dialog node #" + node.NodeId);
      if (tag.RawNodeId != tag.NodeId) sb.AppendLine("Reached through link node: " + tag.RawNodeId + " → " + tag.NodeId);
      if (tag.IsCycle) sb.AppendLine("Tree edge: cycle back to an ancestor");
      else if (tag.IsRejoin) sb.AppendLine("Tree edge: rejoins a node already shown elsewhere");
      sb.AppendLine("Speaker: " + WorldConversationSpeakerName(conversation, node));
      ulong speakerId = node.SpeakerId != 0 ? node.SpeakerId : conversation?.DefaultSpeakerId ?? 0;
      if (speakerId != 0) sb.AppendLine("Speaker id: " + speakerId);
      sb.AppendLine("Player choice: " + node.IsPlayerNode);
      sb.AppendLine("Ambient: " + node.IsAmbient);
      if (node.IsEmpty) sb.AppendLine("Empty node: true");
      if (node.AbortsConversation) sb.AppendLine("Aborts conversation: true");
      if (node.JoinDisabledForHolocom) sb.AppendLine("Holocom join disabled: true");
      if (node.ChoiceDisabledForHolocom) sb.AppendLine("Holocom choice disabled: true");
      if (node.MinLevel >= 0 || node.MaxLevel >= 0) sb.AppendLine("Level condition: " + (node.MinLevel >= 0 ? node.MinLevel.ToString() : "-") + " .. " + (node.MaxLevel >= 0 ? node.MaxLevel.ToString() : "-"));
      if (!String.IsNullOrWhiteSpace(node.ActionHook)) sb.AppendLine("Action hook: " + node.ActionHook);
      if (node.AlignmentGain.ToString() != "None") sb.AppendLine("Alignment reward: " + node.AlignmentGain);
      if (node.GenericNodeNumber != 0) sb.AppendLine("Generic line node: " + node.GenericNodeNumber);
      if (!String.IsNullOrWhiteSpace(node.CnvAlienVOFQN)) sb.AppendLine("Alien VO: " + node.CnvAlienVOFQN + " #" + node.CnvAlienVONode);
      if (!String.IsNullOrWhiteSpace(node.Stb)) sb.AppendLine("String table: " + node.Stb);
      string condition = WorldConversationConditionSummary(conversation, node.NodeId);
      if (!String.IsNullOrWhiteSpace(condition)) {
        sb.AppendLine();
        sb.AppendLine("Condition (quest/state):");
        sb.AppendLine("  " + condition);
      }

      string option = WorldLocalizedText(node.LocalizedOptionText, null);
      string line = WorldLocalizedDialogText(node);
      if (!String.IsNullOrWhiteSpace(option)) {
        sb.AppendLine(); sb.AppendLine("Option text:"); sb.AppendLine(option.Trim());
      }
      if (!String.IsNullOrWhiteSpace(line)) {
        sb.AppendLine(); sb.AppendLine("Dialog text:"); sb.AppendLine(line.Trim());
      }

      AppendWorldConversationQuestList(sb, "Grants quest", node.QuestsGranted);
      AppendWorldConversationQuestList(sb, "Ends quest", node.QuestsEnded);
      AppendWorldConversationQuestList(sb, "Progresses quest", node.QuestsProgressed);
      if (node.ActionQuest != 0) AppendWorldConversationQuestList(sb, "Action quest", new[] { node.ActionQuest });
      if (node.QuestReward != 0) AppendWorldConversationQuestList(sb, "Quest reward", new[] { node.QuestReward });

      if (node.AffectionRewardEvents != null && node.AffectionRewardEvents.Count > 0) {
        sb.AppendLine(); sb.AppendLine("Companion reactions:");
        foreach (var reward in node.AffectionRewardEvents) sb.AppendLine("  " + reward.Key + ": " + reward.Value.Key + (String.IsNullOrWhiteSpace(reward.Value.Value) ? String.Empty : " — " + reward.Value.Value));
      }

      if (node.ChildIds != null && node.ChildIds.Count > 0) {
        sb.AppendLine(); sb.AppendLine("Children:");
        foreach (int child in node.ChildIds) {
          long target = ResolveWorldConversationLinkTarget(conversation, child, out List<long> links);
          if (links != null && links.Count > 0) sb.AppendLine("  " + child + " → link → " + target);
          else sb.AppendLine("  " + child);
        }
      } else {
        sb.AppendLine(); sb.AppendLine("Children: none (terminal node)");
      }
      worldConversationPreviewDetails.Text = sb.ToString().TrimEnd();
    }

    private void AppendWorldConversationQuestList(StringBuilder sb, string label, IEnumerable<ulong> ids) {
      if (sb == null || ids == null) return;
      foreach (ulong id in ids.Where(x => x != 0).Distinct()) {
        WorldMissionBoardQuest quest = ResolveWorldMissionBoardQuest(id);
        string name = !String.IsNullOrWhiteSpace(quest?.Name) ? quest.Name : !String.IsNullOrWhiteSpace(quest?.Fqn) ? quest.Fqn : id.ToString(CultureInfo.InvariantCulture);
        sb.AppendLine(); sb.Append(label).Append(": ").Append(name).Append(" [").Append(id).Append(']');
        if (quest != null && !String.IsNullOrWhiteSpace(quest.Fqn) && !String.Equals(name, quest.Fqn, StringComparison.Ordinal)) sb.Append("  ").Append(quest.Fqn);
        sb.AppendLine();
      }
    }

    private void CopyWorldConversationTree() {
      if (worldConversationPreviewTree == null) return;
      var sb = new StringBuilder();
      if (worldConversationPreviewConversation != null) sb.AppendLine(worldConversationPreviewConversation.Fqn ?? "Conversation");
      foreach (TreeNode node in worldConversationPreviewTree.Nodes) AppendWorldConversationTreeText(sb, node, 0);
      string text = sb.ToString().TrimEnd();
      if (String.IsNullOrWhiteSpace(text)) return;
      try { Clipboard.SetText(text); SetStatusLabel("Conversation tree copied to clipboard."); } catch (Exception ex) { SetStatusLabel("Could not copy conversation tree: " + ex.Message); }
    }

    private static void AppendWorldConversationTreeText(StringBuilder sb, TreeNode node, int depth) {
      if (sb == null || node == null) return;
      sb.Append(' ', Math.Max(0, depth) * 2).AppendLine(node.Text);
      foreach (TreeNode child in node.Nodes) AppendWorldConversationTreeText(sb, child, depth + 1);
    }
  }
}
