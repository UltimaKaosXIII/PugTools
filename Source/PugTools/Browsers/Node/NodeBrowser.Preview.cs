using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

using FileFormats;
using GomLib;
using GomLib.Models;
using SlimDX;

using TorFile = TorArchive.File;

namespace PugTools {
  internal partial class NodeBrowser {
    private SplitContainer _nodePreviewSplit;
    private SplitContainer _nodePreviewContentSplit;
    private Panel _nodePreviewInfoPanel;
    private Panel _nodePreviewModelPanel;
    private PictureBox _nodePreviewIcon;
    private Label _nodePreviewTitle;
    private Label _nodePreviewMeta;
    private RichTextBox _nodePreviewText;
    private Label _nodePreviewModelLabel;
    private CheckBox _nodePreviewIppSetCheck;
    private Button _nodeGameplayButton;
    private NodeGameplayExplorer _nodeGameplayExplorer;
    private static readonly Object NodePreviewDevIlLock = new Object();
    private View_NPC_GR2 _nodePreviewRenderer;
    private Thread _nodePreviewRenderThread;
    private Dictionary<String, GR2> _nodePreviewModels;
    private Dictionary<String, Object> _nodePreviewResources;
    private Boolean _nodePreviewUpdatingIppSetCheck;
    private Boolean _nodePreviewApplyingLayout;
    private Int32 _nodePreviewRememberedModelHeight = -1;
    private const String NodePreviewModelPanelName = "nodePreviewModelPanel";
    private const String NodePreviewBodyType = "bmn";

    private sealed class PreviewField {
      public String Label { get; init; }
      public String Text { get; init; }
      public Int32 Priority { get; init; }
    }

    private sealed class PreviewContent {
      public String Title { get; set; }
      public String Icon { get; set; }
      public List<PreviewField> Fields { get; } = new List<PreviewField>();
    }

    private void InitializeNodePreviewUi() {
      // Jedipedia-style layout: the interpreted/localized information belongs directly above the
      // raw node values, not in the narrow property sidebar. The original property grid and
      // extraction/actions pane on the right therefore stay untouched.
      if (_nodePreviewSplit != null || splitContainer3?.Panel1 == null || treeViewGrid1 == null) return;

      splitContainer3.Panel1.Controls.Remove(treeViewGrid1);
      splitContainer3.Panel1.Controls.Remove(loadingSwirl1);

      _nodePreviewSplit = new SplitContainer {
        Dock = DockStyle.Fill,
        Orientation = Orientation.Horizontal,
        FixedPanel = FixedPanel.Panel1,
        SplitterWidth = 4,
        Panel1Collapsed = true,
        Name = "nodePreviewSplit"
      };
      _nodePreviewSplit.Panel2.Controls.Add(treeViewGrid1);
      treeViewGrid1.Dock = DockStyle.Fill;
      _nodePreviewSplit.Panel2.Controls.Add(loadingSwirl1);
      loadingSwirl1.Dock = DockStyle.Fill;
      loadingSwirl1.BringToFront();

      splitContainer3.Panel1.Controls.Add(_nodePreviewSplit);
      _nodePreviewSplit.BringToFront();
      toolStrip1?.BringToFront();

      _nodePreviewContentSplit = new SplitContainer {
        Dock = DockStyle.Fill,
        // Keep the model preview in the main/centre column and give it the full available width.
        // The interpreted text sits above it, Jedipedia-style, instead of squeezing the model into
        // a narrow strip on the right.
        Orientation = Orientation.Horizontal,
        FixedPanel = FixedPanel.Panel1,
        SplitterWidth = 4,
        Panel2Collapsed = true,
        IsSplitterFixed = true,
        Name = "nodePreviewContentSplit"
      };
      _nodePreviewSplit.Panel1.Controls.Add(_nodePreviewContentSplit);

      _nodePreviewInfoPanel = new Panel {
        Dock = DockStyle.Fill,
        Padding = new Padding(8, 8, 8, 6)
      };
      _nodePreviewContentSplit.Panel1.Controls.Add(_nodePreviewInfoPanel);

      _nodePreviewIcon = new PictureBox {
        Location = new Point(8, 8),
        Size = new Size(68, 68),
        SizeMode = PictureBoxSizeMode.Zoom,
        BorderStyle = BorderStyle.FixedSingle,
        Visible = false
      };
      _nodePreviewInfoPanel.Controls.Add(_nodePreviewIcon);

      _nodePreviewTitle = new Label {
        AutoEllipsis = true,
        AutoSize = false,
        Font = new Font(Font, FontStyle.Bold),
        Location = new Point(86, 8),
        Height = 26,
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
      };
      _nodePreviewInfoPanel.Controls.Add(_nodePreviewTitle);

      _nodePreviewMeta = new Label {
        AutoEllipsis = true,
        AutoSize = false,
        ForeColor = SystemColors.GrayText,
        Location = new Point(86, 35),
        Height = 38,
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
      };
      _nodePreviewInfoPanel.Controls.Add(_nodePreviewMeta);

      _nodeGameplayButton = new Button {
        AutoSize = false,
        Size = new Size(136, 28),
        Text = "Gameplay explorer",
        Anchor = AnchorStyles.Top | AnchorStyles.Right,
        Location = new Point(Math.Max(8, _nodePreviewInfoPanel.ClientSize.Width - 144), 8),
        Visible = false
      };
      _nodeGameplayButton.Click += delegate { OpenNodeGameplayExplorer(); };
      _nodePreviewInfoPanel.Controls.Add(_nodeGameplayButton);

      _nodePreviewIppSetCheck = new CheckBox {
        AutoSize = true,
        Text = "Show complete armor set",
        Location = new Point(8, 79),
        Visible = false
      };
      _nodePreviewIppSetCheck.CheckedChanged += NodePreviewIppSetCheckChanged;
      _nodePreviewInfoPanel.Controls.Add(_nodePreviewIppSetCheck);

      _nodePreviewText = new RichTextBox {
        ReadOnly = true,
        DetectUrls = false,
        BorderStyle = BorderStyle.FixedSingle,
        ScrollBars = RichTextBoxScrollBars.Vertical,
        Location = new Point(8, 105),
        Height = 76,
        Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
        BackColor = SystemColors.Window
      };
      _nodePreviewInfoPanel.Controls.Add(_nodePreviewText);
      InitializeNodeReferenceUi();
      InitializeNodeConversationUi();

      _nodePreviewModelPanel = new Panel {
        Dock = DockStyle.Fill,
        Name = NodePreviewModelPanelName,
        BackColor = Color.Black
      };
      _nodePreviewContentSplit.Panel2.Controls.Add(_nodePreviewModelPanel);

      _nodePreviewModelLabel = new Label {
        AutoSize = false,
        Dock = DockStyle.Top,
        Height = 22,
        Text = "3D model preview",
        TextAlign = ContentAlignment.MiddleLeft,
        ForeColor = Color.Gainsboro,
        BackColor = Color.FromArgb(35, 35, 35)
      };
      _nodePreviewModelPanel.Controls.Add(_nodePreviewModelLabel);
      _nodePreviewModelPanel.Resize += delegate {
        if (_nodePreviewRenderer != null && _nodePreviewModelPanel.Width > 0 && _nodePreviewModelPanel.Height > 0)
          _nodePreviewRenderer.SetSize(_nodePreviewModelPanel.Height, _nodePreviewModelPanel.Width);
      };
      _nodePreviewContentSplit.Resize += delegate { ResizeNodePreviewContent(); };
      _nodePreviewSplit.SplitterMoved += delegate {
        // Keep the user's chosen model-preview height while moving between nodes.  Programmatic
        // layout changes (text-only nodes, initial sizing, window constraints) must not overwrite it.
        if (_nodePreviewApplyingLayout || _nodePreviewSplit.Panel1Collapsed) return;
        if (_nodePreviewContentSplit == null || _nodePreviewContentSplit.Panel2Collapsed) return;
        _nodePreviewRememberedModelHeight = _nodePreviewSplit.SplitterDistance;
      };

      ResizeNodePreviewLayout();
    }

    private void ResizeNodePreviewContent() {
      if (_nodePreviewContentSplit == null || _nodePreviewContentSplit.Panel2Collapsed) return;

      // The model now spans the whole centre column. Only reserve as much height for the textual
      // information as is actually needed, so an empty RichTextBox never leaves a large blank row.
      Int32 infoHeight = CalculateNodePreviewInfoDesiredHeight();

      Int32 height = _nodePreviewContentSplit.ClientSize.Height;
      if (height <= 0) return;
      // Always leave a useful model viewport when possible.
      Int32 maxInfoHeight = Math.Max(60, height - 200 - _nodePreviewContentSplit.SplitterWidth);
      infoHeight = Math.Min(infoHeight, maxInfoHeight);
      try { _nodePreviewContentSplit.SplitterDistance = Math.Max(60, infoHeight); } catch { }
    }

    private void ResizeNodePreviewLayout() {
      if (_nodePreviewSplit == null || splitContainer3 == null) return;

      // Restore the original compact right sidebar. The preview now uses the main values area.
      Int32 desiredRightWidth = 350;
      if (splitContainer3.Width > desiredRightWidth + 300) {
        try { splitContainer3.SplitterDistance = splitContainer3.Width - desiredRightWidth; } catch { }
      }

      if (_nodePreviewSplit.Panel1Collapsed) return;
      Boolean hasModel = _nodePreviewContentSplit != null && !_nodePreviewContentSplit.Panel2Collapsed;
      Boolean hasTextBody = _nodePreviewText != null && _nodePreviewText.Visible
        && !String.IsNullOrWhiteSpace(_nodePreviewText.Text);
      Boolean hasIppSetOption = _nodePreviewIppSetCheck != null && _nodePreviewIppSetCheck.Visible;

      Int32 desiredPreviewHeight;
      if (hasModel) {
        // Model nodes intentionally open almost like a dedicated model browser.  Keep just a small
        // raw-value strip visible below, matching the large viewport used in the Model Browser.
        // Once the user drags the outer splitter, that exact height is reused for every later
        // model node instead of snapping back on each selection change.
        if (_nodePreviewRememberedModelHeight > 0) {
          desiredPreviewHeight = _nodePreviewRememberedModelHeight;
        } else {
          desiredPreviewHeight = Math.Max(560, _nodePreviewSplit.Height * 90 / 100);
        }
      } else if (hasTextBody) {
        desiredPreviewHeight = Math.Max(220, CalculateNodePreviewInfoDesiredHeight());
      } else if (hasIppSetOption) {
        desiredPreviewHeight = Math.Max(116, CalculateNodePreviewInfoDesiredHeight());
      } else {
        desiredPreviewHeight = Math.Max(84, CalculateNodePreviewInfoDesiredHeight());
      }

      Int32 minimumRawValuesHeight = hasModel ? 72 : 180;
      Int32 max = Math.Max(135, _nodePreviewSplit.Height - minimumRawValuesHeight);
      Int32 min = hasModel ? Math.Min(160, max) : 84;
      Int32 target = Math.Max(min, Math.Min(desiredPreviewHeight, max));
      try {
        _nodePreviewApplyingLayout = true;
        _nodePreviewSplit.SplitterDistance = target;
      } catch {
      } finally {
        _nodePreviewApplyingLayout = false;
      }
      ResizeNodePreviewContent();
    }

    private void NodePreviewIppSetCheckChanged(Object sender, EventArgs e) {
      if (_nodePreviewUpdatingIppSetCheck || treeViewFast1?.SelectedNode?.Tag is not NodeAsset asset || asset.Obj == null) return;
      if (!asset.Obj.Name.StartsWith("ipp.", StringComparison.OrdinalIgnoreCase)) return;
      UpdateNodePreview(asset);
    }

    private void UpdateNodePreview(NodeAsset asset) {
      if (_nodePreviewSplit == null) return;
      GomObject gom = asset?.Obj;
      if (gom == null) {
        HideNodePreview();
        return;
      }

      PreviewContent content = BuildPreviewContent(gom);
      UpdateNodeConversationButton(gom);
      if (_nodeGameplayButton != null) {
        _nodeGameplayButton.Visible = NodeGameplayExplorer.Supports(gom);
        _nodeGameplayButton.Enabled = _nodeGameplayButton.Visible;
      }
      Boolean isIpp = gom.Name.StartsWith("ipp.", StringComparison.OrdinalIgnoreCase);
      Int32 ippSetCount = isIpp ? GetIppSetObjects(gom).Count : 0;
      _nodePreviewUpdatingIppSetCheck = true;
      try {
        _nodePreviewIppSetCheck.Visible = isIpp && ippSetCount > 1;
        _nodePreviewIppSetCheck.Enabled = ippSetCount > 1;
        if (!isIpp) _nodePreviewIppSetCheck.Checked = false;
        _nodePreviewIppSetCheck.Text = ippSetCount > 1
          ? "Show complete armor set (" + ippSetCount + " parts)"
          : "Show complete armor set";
      } finally {
        _nodePreviewUpdatingIppSetCheck = false;
      }

      Boolean hasText = content != null && (!String.IsNullOrWhiteSpace(content.Title) || content.Fields.Count > 0);
      Boolean hasModel = TryRenderNodeModel(gom);

      if (!hasText && !hasModel) {
        HideNodePreview();
        return;
      }

      _nodePreviewSplit.Panel1Collapsed = false;
      _nodePreviewTitle.Text = content?.Title ?? gom.Name;
      _nodePreviewMeta.Text = BuildNodePreviewMeta(gom);

      StringBuilder text = new StringBuilder();
      if (content != null) {
        foreach (PreviewField field in content.Fields
                   .OrderBy(x => x.Priority)
                   .ThenBy(x => x.Label, StringComparer.OrdinalIgnoreCase)
                   .Take(40)) {
          if (String.IsNullOrWhiteSpace(field.Text)) continue;
          if (text.Length > 0) text.AppendLine().AppendLine();
          text.Append(field.Label).Append(": ").Append(field.Text);
          if (text.Length > 10000) break;
        }
      }
      _nodePreviewText.Text = text.ToString();
      // Do not render the otherwise empty white information row. The title/FQN/language header
      // remains visible, and a model can start immediately below it.
      _nodePreviewText.Visible = text.Length > 0;

      System.Drawing.Image oldImage = _nodePreviewIcon.Image;
      _nodePreviewIcon.Image = null;
      if (oldImage != null) oldImage.Dispose();
      Bitmap icon = TryLoadNodeIcon(content?.Icon, gom);
      if (icon != null) {
        _nodePreviewIcon.Image = icon;
        _nodePreviewIcon.Visible = true;
        _nodePreviewTitle.Left = 86;
        _nodePreviewMeta.Left = 86;
      } else {
        _nodePreviewIcon.Visible = false;
        _nodePreviewTitle.Left = 8;
        _nodePreviewMeta.Left = 8;
      }
      Int32 gameplayReserve = _nodeGameplayButton != null && _nodeGameplayButton.Visible ? _nodeGameplayButton.Width + 16 : 8;
      _nodePreviewTitle.Width = Math.Max(10, _nodePreviewInfoPanel.ClientSize.Width - _nodePreviewTitle.Left - gameplayReserve);
      _nodePreviewMeta.Width = Math.Max(10, _nodePreviewInfoPanel.ClientSize.Width - _nodePreviewMeta.Left - gameplayReserve);
      UpdateNodeReferencePanel(gom);
      LayoutNodePreviewInformation();

      _nodePreviewContentSplit.Panel2Collapsed = !hasModel;
      ResizeNodePreviewLayout();
    }

    private void OpenNodeGameplayExplorer() {
      if (treeViewFast1?.SelectedNode?.Tag is not NodeAsset asset || asset.Obj == null || _currentDom == null) return;
      if (!NodeGameplayExplorer.Supports(asset.Obj)) return;
      try {
        if (_nodeGameplayExplorer != null && !_nodeGameplayExplorer.IsDisposed) _nodeGameplayExplorer.Dispose();
      } catch { }
      _nodeGameplayExplorer = new NodeGameplayExplorer(
        _currentDom, asset.Obj, delegate (String fqn) { NavigateToNodeName(fqn); }, OpenGameplayWorldMapNote, OpenGameplayModelPreview,
        TryLoadGameplayGraphIcon,
        delegate { return _nodePreviewIcon?.Image is Bitmap bitmap ? new Bitmap(bitmap) : null; });
      _nodeGameplayExplorer.FormClosed += delegate { _nodeGameplayExplorer = null; };
      _nodeGameplayExplorer.Show(this);
    }

    private void OpenGameplayWorldMapNote(MapNote note) {
      if (note == null || String.IsNullOrWhiteSpace(note.Fqn)) return;
      WorldBrowser world = null;
      try {
        world = Application.OpenForms.OfType<WorldBrowser>().FirstOrDefault(x => x != null && !x.IsDisposed && x.MatchesGameplayAssetSource(_assetsLocation, _assetsUsePts));
      } catch { }
      if (world == null) {
        world = new WorldBrowser(_assetsLocation, _assetsUsePts);
        world.Show();
      } else {
        if (!world.Visible) world.Show();
        if (world.WindowState == FormWindowState.Minimized) world.WindowState = FormWindowState.Normal;
      }
      world.BringToFront();
      world.Focus();
      world.NavigateToGameplayMapNote(note.Fqn);
    }

    private void OpenGameplayModelPreview(String fqn) {
      if (String.IsNullOrWhiteSpace(fqn)) return;
      ModelBrowser model = null;
      try {
        model = Application.OpenForms.OfType<ModelBrowser>()
          .FirstOrDefault(x => x != null && !x.IsDisposed && x.MatchesGameplayAssetSource(_assetsLocation, _assetsUsePts));
      } catch { }
      if (model == null) {
        model = new ModelBrowser(_assetsLocation, _assetsUsePts, _previousAssetsLocation, _previousAssetsUsePts, _compareNodes);
        model.Show();
      } else {
        if (!model.Visible) model.Show();
        if (model.WindowState == FormWindowState.Minimized) model.WindowState = FormWindowState.Normal;
      }
      model.BringToFront();
      model.Focus();
      model.NavigateToGameplayNode(fqn);
    }

    private Bitmap TryLoadGameplayGraphIcon(String fqn) {
      if (_closing || _currentDom == null || String.IsNullOrWhiteSpace(fqn)) return null;
      try {
        GomObject gom = _currentDom.GetObject(fqn);
        if (gom == null) return null;
        GameObject model = null;
        try { model = GameObject.Load(gom, true); } catch { }
        String icon = FindIconValue(model);
        if (String.IsNullOrWhiteSpace(icon)) icon = FindRawIconValue(gom);
        return TryLoadNodeIcon(icon, gom);
      } catch { return null; }
    }

    private void HideNodePreview() {
      StopNodePreviewRenderer(false);
      if (_nodeGameplayButton != null) { _nodeGameplayButton.Visible = false; _nodeGameplayButton.Enabled = false; }
      if (_nodePreviewContentSplit != null) _nodePreviewContentSplit.Panel2Collapsed = true;
      if (_nodePreviewSplit != null) _nodePreviewSplit.Panel1Collapsed = true;
      if (_nodePreviewIcon?.Image != null) {
        System.Drawing.Image image = _nodePreviewIcon.Image;
        _nodePreviewIcon.Image = null;
        image.Dispose();
      }
    }

    private PreviewContent BuildPreviewContent(GomObject gom) {
      PreviewContent result = new PreviewContent { Title = gom.Name };
      GameObject model = null;
      try { model = GameObject.Load(gom, true); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }

      HashSet<String> seenText = new HashSet<String>(StringComparer.Ordinal);
      if (model != null) {
        // Ability text deserves a special path because this property expands the familiar SWTOR
        // <<token>> placeholders (duration, talents, etc.) before it is shown.
        if (model is Ability ability) {
          AddLocalizedField(result, "Name", ability.LocalizedName, 0, seenText, true);
          AddLocalizedField(result, "Description", ability.ParsedLocalizedDescription ?? ability.LocalizedDescription, 10, seenText);
        }

        foreach (PropertyInfo property in model.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public)) {
          if (!property.CanRead || property.GetIndexParameters().Length != 0) continue;
          if (!property.Name.Contains("Localized", StringComparison.OrdinalIgnoreCase)) continue;
          if (model is Ability && (property.Name == nameof(Ability.LocalizedName)
                                   || property.Name == nameof(Ability.LocalizedDescription)
                                   || property.Name == nameof(Ability.ParsedLocalizedDescription))) continue;
          if (!typeof(Dictionary<String, String>).IsAssignableFrom(property.PropertyType)) continue;
          try {
            Dictionary<String, String> values = property.GetValue(model) as Dictionary<String, String>;
            String label = FriendlyPreviewLabel(property.Name);
            Int32 priority = PreviewFieldPriority(property.Name);
            AddLocalizedField(result, label, values, priority, seenText,
              property.Name.IndexOf("Name", StringComparison.OrdinalIgnoreCase) >= 0);
          } catch { }
        }

        // Some loaders expose the resolved UI text only as scalar strings (Achievement is a common example).
        // Pick semantic text properties broadly instead of hard-coding just four names, while deliberately ignoring
        // technical identifiers/model paths. This covers ach/cdx/qst/npc and many smaller node families.
        foreach (PropertyInfo property in model.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public)) {
          if (!property.CanRead || property.GetIndexParameters().Length != 0 || property.PropertyType != typeof(String)) continue;
          if (!IsPreviewTextProperty(property.Name)) continue;
          try {
            String value = CleanPreviewText(property.GetValue(model) as String);
            if (String.IsNullOrWhiteSpace(value) || !seenText.Add(value)) continue;
            Int32 priority = PreviewFieldPriority(property.Name) + 50;
            result.Fields.Add(new PreviewField { Label = FriendlyPreviewLabel(property.Name), Text = value, Priority = priority });
            if ((property.Name.Equals("Name", StringComparison.OrdinalIgnoreCase)
                 || property.Name.EndsWith("Name", StringComparison.OrdinalIgnoreCase)
                 || property.Name.IndexOf("DisplayName", StringComparison.OrdinalIgnoreCase) >= 0)
                && (result.Title == gom.Name || String.IsNullOrWhiteSpace(result.Title))) result.Title = value;
          } catch { }
        }

        // Jedipedia does not stop at name/description: for common node families it also shows the
        // gameplay metadata that makes a node understandable without reading the raw GOM fields. Keep
        // those compact facts in the same interpreted panel.
        AddJedipediaSemanticFields(model, result);

        result.Icon = FindIconValue(model);
      }

      // Generic fallback for node families that do not have a high-level GomLib model yet. A very
      // large part of SWTOR's user-facing GOM data uses locTextRetrieverMap, so this also makes the
      // preview useful for many node types beyond abl/ach/npc/qst/cdx/etc.
      AddRawLocalizedFields(gom, result, seenText);

      if (String.IsNullOrWhiteSpace(result.Icon)) result.Icon = FindRawIconValue(gom);
      return result;
    }


    private String BuildNodePreviewMeta(GomObject gom) {
      if (gom == null) return "Language: " + LocalizationResolver.GetStatusText();
      String className = gom.DomClass?.Name;
      String classText = String.IsNullOrWhiteSpace(className)
        ? "unknown"
        : className + " (" + gom.DomClass.Id + ")";
      // Match Jedipedia's useful header density: FQN/ID on the first line, base class and the
      // actually resolved locale on the second. The selected-language fallback is therefore
      // visible immediately instead of being hidden in a settings dialog.
      return gom.Name + "   ID: " + gom.Id
        + Environment.NewLine + "Base class: " + classText
        + "   Language: " + LocalizationResolver.GetStatusText();
    }

    private void AddJedipediaSemanticFields(GameObject model, PreviewContent result) {
      if (model == null || result == null) return;
      try {
        switch (model) {
          case Ability ability:
            AddSemanticField(result, "Level", ability.Level, 40);
            AddSemanticField(result, "Passive", ability.IsPassive, 41, false);
            AddSemanticField(result, "Cooldown", FormatSeconds(ability.Cooldown), 42);
            AddSemanticField(result, "Cast time", FormatSeconds(ability.CastingTime), 43);
            AddSemanticField(result, "Channel time", FormatSeconds(ability.ChannelingTime), 44);
            if (ability.MaxRange > 0 || ability.MinRange > 0)
              AddSemanticField(result, "Range", ability.MinRange + " - " + ability.MaxRange, 45, false);
            String cost = BuildAbilityCost(ability);
            AddSemanticField(result, "Cost", cost, 46, false);
            break;

          case Item item:
            AddSemanticField(result, "Quality", item.Quality, 40, false);
            AddSemanticField(result, "Item level", item.ItemLevel, 41);
            AddSemanticField(result, "Rating", item.CombinedRating > 0 ? item.CombinedRating : item.Rating, 42);
            AddSemanticField(result, "Required level", item.CombinedRequiredLevel > 0 ? item.CombinedRequiredLevel : item.RequiredLevel, 43);
            AddSemanticField(result, "Binding", item.Binding, 44, false);
            AddSemanticField(result, "Category", item.Category, 45, false);
            AddSemanticField(result, "Subcategory", item.SubCategory, 46, false);
            AddSemanticField(result, "Value", item.Value, 47);
            AddSemanticField(result, "Max stack", item.MaxStack, 48);
            if (item.Slots != null && item.Slots.Count > 0)
              AddSemanticField(result, "Slots", String.Join(", ", item.Slots.Select(x => x.ToString())), 49, false);
            String stats = BuildItemStats(item);
            AddSemanticField(result, "Stats", stats, 50, false);
            if (item.RequiredClasses != null && item.RequiredClasses.Count > 0)
              AddSemanticField(result, "Classes", String.Join(", ", item.RequiredClasses.Select(x => x.Name)), 51, false);
            AddSemanticField(result, "Modifications", BuildItemEnhancementPreview(item), 52, false);
            AddSemanticField(result, "Use ability", BuildItemAbilityPreview(item.UseAbility), 53, false);
            AddSemanticField(result, "Equip ability", BuildItemAbilityPreview(item.EquipAbility), 54, false);
            AddSemanticField(result, "Set bonus", BuildItemSetBonusPreview(item), 55, false);
            if (item.RequiredProfession != Profession.None)
              AddSemanticField(result, "Required profession", item.RequiredProfession + " " + item.RequiredProfessionLevel, 56, false);
            if (item.RequiresSocial)
              AddSemanticField(result, "Required social tier", item.RequiredSocialTier, 57, false);
            AddSemanticField(result, "Required valor rank", item.RequiredValorRank, 58);
            if (item.RequiredReputationId > 0)
              AddSemanticField(result, "Required reputation", item.RequiredReputationName, 59, false);
            if (item.RequiredReputationLevelId > 0)
              AddSemanticField(result, "Required reputation rank", item.RequiredReputationLevelName, 60, false);
            if (!String.IsNullOrWhiteSpace(item.TeachesType))
              AddSemanticField(result, "Teaches", item.TeachesType + (item.TeachesRef != 0 ? " — " + ResolveNodeName(item.TeachesRef) : String.Empty), 61, false);
            AddSemanticField(result, "Conversation", item.ConversationFqn, 62, false);
            AddSemanticField(result, "Imperial appearance", item.AppearanceImperial ?? item.ImperialAppearanceTag, 63, false);
            AddSemanticField(result, "Republic appearance", item.AppearanceRepublic ?? item.RepublicAppearanceTag, 64, false);
            break;

          case Npc npc:
            String levels = npc.MinLevel == npc.MaxLevel || npc.MaxLevel <= 0
              ? npc.MinLevel.ToString()
              : npc.MinLevel + " - " + npc.MaxLevel;
            AddSemanticField(result, "Level", levels, 40, false);
            AddSemanticField(result, "Faction", npc.Faction, 41, false);
            AddSemanticField(result, "Toughness", npc.Toughness, 42, false);
            AddSemanticField(result, "Gender", npc.Gender, 43, false);
            AddSemanticField(result, "Vendor", npc.IsVendor, 44, false);
            AddSemanticField(result, "Class trainer", npc.IsClassTrainer, 45, false);
            if (npc.ProfessionTrained != Profession.None)
              AddSemanticField(result, "Profession trained", npc.ProfessionTrained, 46, false);
            AddSemanticField(result, "Conversation", npc.CnvConversationName, 47, false);
            break;

          case Placeable plc:
            AddSemanticField(result, "Category", plc.Category, 40, false);
            AddSemanticField(result, "Faction", plc.Faction, 41, false);
            AddSemanticField(result, "Mailbox", plc.IsMailbox, 42, false);
            AddSemanticField(result, "Bank", plc.IsBank, 43, false);
            AddSemanticField(result, "Auction house", plc.IsAuctionHouse, 44, false);
            AddSemanticField(result, "Enhancement station", plc.IsEnhancementStation, 45, false);
            AddSemanticField(result, "Conversation", plc.ConversationFqn, 46, false);
            if (plc.WonkaPackageId != 0)
              AddSemanticField(result, "Wonkavator package", plc.WonkaPackageId, 47, false);
            break;

          case Achievement achievement:
            AddSemanticField(result, "Level", achievement.Level, 40);
            AddSemanticField(result, "Visibility", achievement.Visibility, 41, false);
            AddSemanticField(result, "Achievement points", achievement.Rewards?.AchievementPoints, 42);
            AddSemanticField(result, "Cartel coins", achievement.Rewards?.CartelCoins, 43);
            AddSemanticField(result, "Tasks", achievement.Tasks?.Count ?? 0, 44);
            AddSemanticField(result, "Conditions", achievement.Conditions?.Count ?? 0, 45);
            AddSemanticField(result, "Achievement tasks", BuildAchievementTasksPreview(achievement), 25, false);
            AddSemanticField(result, "Conditions", BuildAchievementConditionsPreview(achievement), 26, false);
            AddSemanticField(result, "Rewards", BuildAchievementRewardsPreview(achievement), 27, false);
            break;

          case Codex codex:
            AddSemanticField(result, "Level", codex.Level, 40);
            AddSemanticField(result, "Faction", codex.Faction, 42, false);
            AddSemanticField(result, "Hidden", codex.IsHidden, 43, false);
            AddSemanticField(result, "Planet entry", codex.IsPlanet, 44, false);
            AddLocalizedSemanticField(result, "Category", codex.LocalizedCategoryName, 25);
            if (codex.Classes != null && codex.Classes.Count > 0)
              AddSemanticField(result, "Classes", String.Join(", ", codex.Classes.Select(x => x.Name)), 45, false);
            AddSemanticField(result, "Linked codex entries", BuildCodexLinksPreview(codex), 46, false);
            break;

          case Talent talent:
            AddSemanticField(result, "Ranks", talent.Ranks, 40);
            AddSemanticField(result, "Visibility", talent.TalentVisibility, 41, false);
            AddSemanticField(result, "Rank details", BuildTalentRanksPreview(talent), 25, false);
            break;

          case Schematic schematic:
            AddSemanticField(result, "Quality", schematic.Quality, 41, false);
            AddSemanticField(result, "Crew skill", schematic.CrewSkill, 42, false);
            AddLocalizedSemanticField(result, "Category", schematic.LocalizedCategory, 43);
            AddLocalizedSemanticField(result, "Subcategory", schematic.LocalizedSubCategory, 44);
            try {
              String crafted = StringTable.SelectLocalizedText(schematic.Item?.LocalizedName, null);
              AddSemanticField(result, "Crafted item", crafted, 45, false);
            } catch { }
            AddSemanticField(result, "Materials", schematic.Materials?.Count ?? 0, 46);
            AddSemanticField(result, "Material list", BuildSchematicMaterialsPreview(schematic), 25, false);
            AddSemanticField(result, "Research", BuildSchematicResearchPreview(schematic), 26, false);
            if (schematic.SkillOrange > 0 || schematic.SkillGrey > 0)
              AddSemanticField(result, "Skill thresholds", schematic.SkillOrange + " / " + schematic.SkillYellow + " / "
                + schematic.SkillGreen + " / " + schematic.SkillGrey, 47, false);
            AddSemanticField(result, "Crafting time", FormatSeconds(schematic.CraftingTime), 48, false);
            AddSemanticField(result, "Training cost", schematic.TrainingCost, 49);
            AddSemanticField(result, "Trainer taught", schematic.TrainerTaught, 50, false);
            break;

          case Quest quest:
            AddReflectedSemanticField(result, quest, "RequiredLevel", "Required level", 40);
            AddReflectedSemanticField(result, quest, "XpLevel", "XP level", 41);
            AddReflectedSemanticField(result, quest, "XP", "XP", 42);
            AddReflectedSemanticField(result, quest, "Difficulty", "Difficulty", 43, false);
            AddReflectedSemanticField(result, quest, "IsRepeatable", "Repeatable", 44, false);
            AddReflectedSemanticField(result, quest, "IsClassQuest", "Class quest", 45, false);
            AddReflectedSemanticField(result, quest, "IsBonus", "Bonus mission", 46, false);
            AddReflectedSemanticField(result, quest, "CanAbandon", "Can abandon", 47, false);
            AddSemanticField(result, "Branches", quest.BranchCount, 48);
            AddSemanticField(result, "Classes", quest.AllowedClasses, 49, false);
            AddSemanticField(result, "Quest journal", BuildQuestJournalPreview(quest), 25, false);
            AddSemanticField(result, "Rewards", BuildQuestRewardsPreview(quest), 31, false);
            break;

          case Conversation conversation:
            AddSemanticField(result, "Dialogue nodes", conversation.DialogNodes?.Count ?? 0, 40);
            AddSemanticField(result, "Root nodes", conversation.RootNodes?.Count ?? 0, 41);
            AddSemanticField(result, "Speakers", conversation.SpeakersIds?.Count ?? 0, 42);
            AddSemanticField(result, "KOTOR style", conversation.IsKOTORStyle, 43, false);
            AddSemanticField(result, "Starts quests", conversation.QuestStarted?.Count ?? 0, 44);
            AddSemanticField(result, "Ends quests", conversation.QuestEnded?.Count ?? 0, 45);
            break;
        }
      } catch (Exception ex) {
        System.Diagnostics.Debug.WriteLine("Node semantic preview failed: " + ex.Message);
      }
    }

    private static String FormatSeconds(Object raw) {
      if (raw == null) return null;
      try {
        Double value = Convert.ToDouble(raw, System.Globalization.CultureInfo.InvariantCulture);
        if (Math.Abs(value) < 0.0001) return null;
        // Some GOM fields are already seconds and some legacy loaders expose milliseconds. Do not
        // invent a conversion here: preserve the loader's value and make the unit explicit.
        return value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + " s";
      } catch { return raw.ToString(); }
    }

    private static String BuildAbilityCost(Ability ability) {
      if (ability == null) return null;
      List<String> parts = new List<String>();
      if (ability.ForceCost > 0) parts.Add(ability.ForceCost.ToString("0.##") + " Force");
      if (ability.EnergyCost > 0) parts.Add(ability.EnergyCost.ToString("0.##") + " Energy");
      if (ability.ApCost > 0) parts.Add(ability.ApCost.ToString("0.##") + " " + ability.ApType);
      return parts.Count == 0 ? null : String.Join(", ", parts);
    }

    private static String BuildItemStats(Item item) {
      if (item?.StatModifiers == null || item.StatModifiers.Count == 0) return null;
      List<String> parts = new List<String>();
      foreach (ItemStat stat in item.StatModifiers.Take(12)) {
        if (stat == null || stat.Modifier == 0) continue;
        String name = null;
        try { name = StringTable.SelectLocalizedText(stat.DetailedStat?.LocalizedDisplayName, null); } catch { }
        if (String.IsNullOrWhiteSpace(name)) name = stat.Stat.ToString();
        parts.Add((stat.Modifier > 0 ? "+" : String.Empty) + stat.Modifier + " " + name);
      }
      if (item.StatModifiers.Count > 12) parts.Add("+" + (item.StatModifiers.Count - 12) + " more");
      return parts.Count == 0 ? null : String.Join(", ", parts);
    }

    private static void AddLocalizedSemanticField(PreviewContent result, String label,
                                                   Dictionary<String, String> localized, Int32 priority) {
      String text = null;
      try { text = StringTable.SelectLocalizedText(localized, null); } catch { }
      AddSemanticField(result, label, text, priority, false);
    }

    private static void AddReflectedSemanticField(PreviewContent result, Object model, String propertyName,
                                                   String label, Int32 priority, Boolean omitDefault = true) {
      if (model == null) return;
      try {
        PropertyInfo property = model.GetType().GetProperty(propertyName,
          BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (property == null || !property.CanRead) return;
        AddSemanticField(result, label, property.GetValue(model), priority, omitDefault);
      } catch { }
    }

    private static void AddSemanticField(PreviewContent result, String label, Object value,
                                         Int32 priority, Boolean omitDefault = true) {
      if (result == null || value == null) return;
      String text;
      if (value is String s) text = s.Trim();
      else if (value is Boolean b) {
        if (omitDefault && !b) return;
        text = b ? "Yes" : "No";
      } else if (value is IEnumerable enumerable && value is not String) {
        List<String> entries = new List<String>();
        foreach (Object entry in enumerable) {
          if (entry == null) continue;
          entries.Add(entry.ToString());
          if (entries.Count >= 20) break;
        }
        text = String.Join(", ", entries);
      } else text = value.ToString();

      if (String.IsNullOrWhiteSpace(text)) return;
      if (omitDefault && (text == "0" || text == "0.0" || text.Equals("None", StringComparison.OrdinalIgnoreCase)
          || text.Equals("Unknown", StringComparison.OrdinalIgnoreCase))) return;
      result.Fields.Add(new PreviewField { Label = label, Text = text, Priority = priority });
    }

    private void AddLocalizedField(PreviewContent result, String label, Dictionary<String, String> values,
                                   Int32 priority, HashSet<String> seenText, Boolean canBecomeTitle = false) {
      String value = CleanPreviewText(StringTable.SelectLocalizedText(values, null));
      if (String.IsNullOrWhiteSpace(value) || !seenText.Add(value)) return;
      result.Fields.Add(new PreviewField { Label = label, Text = value, Priority = priority });
      if (canBecomeTitle) result.Title = value;
    }

    private void AddRawLocalizedFields(GomObject gom, PreviewContent result, HashSet<String> seenText) {
      if (gom?.Data?.Dictionary == null || _currentDom?.StringTable == null) return;

      // First preserve the common SWTOR locTextRetrieverMap path. It carries semantic keys and therefore gives
      // the nicest labels in the preview.
      if (gom.Data.Dictionary.TryGetValue("locTextRetrieverMap", out Object mapObject) && mapObject is IEnumerable entries) {
        Int32 count = 0;
        foreach (Object rawEntry in entries) {
          if (count++ > 30) break;
          Object key = null;
          Object value = null;
          Type entryType = rawEntry?.GetType();
          try {
            key = entryType?.GetProperty("Key")?.GetValue(rawEntry);
            value = entryType?.GetProperty("Value")?.GetValue(rawEntry);
          } catch { }
          if (value is not GomObjectData retriever) continue;
          AddRawLocalizedRetriever(result, seenText, key?.ToString(), retriever, gom.Name, 100 + count);
        }
      }

      // A lot of less common node families do not expose a GomLib model and do not use locTextRetrieverMap. Their
      // name/description retrievers are nested directly in the raw node instead. Walk a small, bounded portion of the
      // raw structure and recognize any LocEntry-shaped GomObjectData. This makes the information panel useful for
      // many more prefixes without hard-coding every individual GOM class.
      Int32 remaining = 48;
      HashSet<Object> visited = new HashSet<Object>(ReferenceEqualityComparer.Instance);
      ScanRawLocalizedValues(gom.Data, gom.Name, result, seenText, visited, ref remaining, 0, "");
    }

    private void ScanRawLocalizedValues(Object value, String context, PreviewContent result, HashSet<String> seenText,
                                        HashSet<Object> visited, ref Int32 remaining, Int32 depth, String labelHint) {
      if (value == null || remaining <= 0 || depth > 5) return;
      Type valueType = value.GetType();
      if (!valueType.IsValueType && value is not String && !visited.Add(value)) return;

      if (value is GomObjectData data) {
        try {
          if (StringTable.TryGetLocEntry(data, out StringTable.LocEntry loc)) {
            String text = CleanPreviewText(_currentDom.StringTable.TryGetString(loc, context));
            if (!String.IsNullOrWhiteSpace(text) && seenText.Add(text)) {
              String label = String.IsNullOrWhiteSpace(labelHint) ? "Localized text" : FriendlyPreviewLabel(labelHint);
              result.Fields.Add(new PreviewField { Label = label, Text = text, Priority = 120 + (48 - remaining) });
              if ((result.Title == context || String.IsNullOrWhiteSpace(result.Title)) &&
                  label.IndexOf("name", StringComparison.OrdinalIgnoreCase) >= 0) result.Title = text;
              remaining--;
            }
          }
        } catch { }

        if (data.Dictionary == null) return;
        foreach (KeyValuePair<String, Object> pair in data.Dictionary) {
          if (remaining <= 0) break;
          // locTextRetrieverMap was already handled above with its authored keys; recursing into it would only
          // duplicate the same strings under implementation-detail labels.
          if (depth == 0 && String.Equals(pair.Key, "locTextRetrieverMap", StringComparison.OrdinalIgnoreCase)) continue;
          ScanRawLocalizedValues(pair.Value, context, result, seenText, visited, ref remaining, depth + 1, pair.Key);
        }
        return;
      }

      if (value is IDictionary dictionary) {
        foreach (DictionaryEntry entry in dictionary) {
          if (remaining <= 0) break;
          ScanRawLocalizedValues(entry.Value, context, result, seenText, visited, ref remaining, depth + 1,
            entry.Key?.ToString() ?? labelHint);
        }
        return;
      }

      if (value is IEnumerable enumerable && value is not String) {
        Int32 count = 0;
        foreach (Object item in enumerable) {
          if (remaining <= 0 || count++ >= 40) break;
          // Generic KeyValuePair<,> objects are common in GOM dictionaries that only implement IEnumerable.
          Object child = item;
          String childLabel = labelHint;
          try {
            Type t = item?.GetType();
            PropertyInfo key = t?.GetProperty("Key"), val = t?.GetProperty("Value");
            if (val != null) {
              child = val.GetValue(item);
              childLabel = key?.GetValue(item)?.ToString() ?? labelHint;
            }
          } catch { }
          ScanRawLocalizedValues(child, context, result, seenText, visited, ref remaining, depth + 1, childLabel);
        }
      }
    }

    private void AddRawLocalizedRetriever(PreviewContent result, HashSet<String> seenText, String labelHint,
                                          GomObjectData retriever, String context, Int32 priority) {
      try {
        if (!StringTable.TryGetLocEntry(retriever, out StringTable.LocEntry loc)) return;
        String text = CleanPreviewText(_currentDom.StringTable.TryGetString(loc, context));
        if (String.IsNullOrWhiteSpace(text) || !seenText.Add(text)) return;
        String label = String.IsNullOrWhiteSpace(labelHint) ? "Localized text" : FriendlyPreviewLabel(labelHint);
        result.Fields.Add(new PreviewField { Label = label, Text = text, Priority = priority });
        if ((result.Title == context || String.IsNullOrWhiteSpace(result.Title)) &&
            label.IndexOf("name", StringComparison.OrdinalIgnoreCase) >= 0) result.Title = text;
      } catch { }
    }

    private sealed class ReferenceEqualityComparer : IEqualityComparer<Object> {
      public static readonly ReferenceEqualityComparer Instance = new ReferenceEqualityComparer();
      public new Boolean Equals(Object x, Object y) => ReferenceEquals(x, y);
      public Int32 GetHashCode(Object obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }

    private static String FriendlyPreviewLabel(String name) {
      if (String.IsNullOrWhiteSpace(name)) return "Text";
      String value = name.Replace("Localized", String.Empty, StringComparison.OrdinalIgnoreCase)
                         .Replace("locTextRetriever", String.Empty, StringComparison.OrdinalIgnoreCase)
                         .Replace('_', ' ');
      value = Regex.Replace(value, "([a-z0-9])([A-Z])", "$1 $2").Trim();
      if (value.Length == 0) value = "Text";
      return Char.ToUpperInvariant(value[0]) + value.Substring(1);
    }

    private static Boolean IsPreviewTextProperty(String name) {
      if (String.IsNullOrWhiteSpace(name)) return false;
      String value = name.ToLowerInvariant();
      // Never show implementation/path fields as prose in the information card.
      if (value.Contains("fqn") || value.Contains("model") || value.Contains("spec") || value.Contains("package")
          || value.Contains("sound") || value.Contains("icon") || value.Contains("portrait") || value.Contains("texture")
          || value.Contains("appearance") || value.Contains("tag") || value.Contains("bucket") || value.Contains("stb")) return false;
      return value == "name" || value.EndsWith("name") || value.Contains("displayname")
        || value.Contains("title") || value.Contains("description") || value.EndsWith("desc")
        || value.Contains("journaltext") || value == "text" || value.EndsWith("text")
        || value.Contains("summary") || value.Contains("message") || value.Contains("category")
        || value.Contains("toughness") || value.Contains("invasionbonus");
    }

    private static Int32 PreviewFieldPriority(String name) {
      String value = name ?? String.Empty;
      if (value.IndexOf("Name", StringComparison.OrdinalIgnoreCase) >= 0) return 0;
      if (value.IndexOf("Title", StringComparison.OrdinalIgnoreCase) >= 0) return 5;
      if (value.IndexOf("NonSpoiler", StringComparison.OrdinalIgnoreCase) >= 0) return 12;
      if (value.IndexOf("Description", StringComparison.OrdinalIgnoreCase) >= 0
          || value.IndexOf("Desc", StringComparison.OrdinalIgnoreCase) >= 0) return 10;
      if (value.IndexOf("Summary", StringComparison.OrdinalIgnoreCase) >= 0) return 20;
      if (value.IndexOf("Category", StringComparison.OrdinalIgnoreCase) >= 0) return 30;
      return 40;
    }

    private static String CleanPreviewText(String text) {
      if (String.IsNullOrWhiteSpace(text)) return null;
      String value = text.Replace("<br />", Environment.NewLine, StringComparison.OrdinalIgnoreCase)
                         .Replace("<br/>", Environment.NewLine, StringComparison.OrdinalIgnoreCase)
                         .Replace("<br>", Environment.NewLine, StringComparison.OrdinalIgnoreCase)
                         .Replace("\\n", Environment.NewLine)
                         .Replace("&nbsp;", " ", StringComparison.OrdinalIgnoreCase)
                         .Trim();
      return value;
    }

    private static String FindIconValue(GameObject model) {
      if (model == null) return null;
      String[] preferred = { "Portrait", "Image", "Icon", "SpaceIcon", "ShipIcon", "PublicIcon", "ImperialIcon", "RepublicIcon" };
      foreach (String name in preferred) {
        try {
          PropertyInfo property = model.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
          if (property?.PropertyType != typeof(String)) continue;
          String value = property.GetValue(model) as String;
          if (!String.IsNullOrWhiteSpace(value)) return value;
        } catch { }
      }
      foreach (PropertyInfo property in model.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public)) {
        if (property.PropertyType != typeof(String)
            || (property.Name.IndexOf("Icon", StringComparison.OrdinalIgnoreCase) < 0
                && property.Name.IndexOf("Portrait", StringComparison.OrdinalIgnoreCase) < 0)) continue;
        try {
          String value = property.GetValue(model) as String;
          if (!String.IsNullOrWhiteSpace(value)) return value;
        } catch { }
      }
      return null;
    }

    private static String FindRawIconValue(GomObject gom) {
      if (gom?.Data?.Dictionary == null) return null;
      foreach (KeyValuePair<String, Object> pair in gom.Data.Dictionary) {
        if (pair.Value is not String text || String.IsNullOrWhiteSpace(text)) continue;
        if (pair.Key.IndexOf("Icon", StringComparison.OrdinalIgnoreCase) >= 0
            || pair.Key.IndexOf("Portrait", StringComparison.OrdinalIgnoreCase) >= 0)
          return text;
      }
      return null;
    }

    private Bitmap TryLoadNodeIcon(String icon, GomObject gom) {
      if (_currentAssets == null || String.IsNullOrWhiteSpace(icon)) return null;
      String normalized = icon.Trim().Replace('\\', '/').TrimStart('/');
      List<String> candidates = new List<String>();
      void AddCandidate(String path) {
        if (String.IsNullOrWhiteSpace(path)) return;
        String candidate = path.StartsWith("/", StringComparison.Ordinal) ? path : "/" + path;
        if (!candidate.EndsWith(".dds", StringComparison.OrdinalIgnoreCase)) candidate += ".dds";
        if (!candidates.Contains(candidate, StringComparer.OrdinalIgnoreCase)) candidates.Add(candidate);
      }

      if (normalized.StartsWith("resources/", StringComparison.OrdinalIgnoreCase)) AddCandidate(normalized);
      else if (normalized.StartsWith("gfx/", StringComparison.OrdinalIgnoreCase)) AddCandidate("resources/" + normalized);
      else {
        AddCandidate("resources/gfx/icons/" + normalized);
        AddCandidate("resources/gfx/" + normalized);
        AddCandidate("resources/gfx/codex/" + normalized);
        AddCandidate("resources/gfx/textures/" + normalized);
        AddCandidate("resources/gfx/mtxstore/" + normalized);
      }

      foreach (String path in candidates) {
        try {
          using TorFile file = _currentAssets.FindFile(path);
          if (file == null) continue;
          using Stream input = file.OpenCopyInMemory();
          lock (NodePreviewDevIlLock) {
            DevIL.ImageImporter importer = new DevIL.ImageImporter();
            DevIL.Image image = importer.LoadImageFromStream(DevIL.ImageType.Dds, input);
            using MemoryStream output = new MemoryStream();
            DevIL.ImageExporter exporter = new DevIL.ImageExporter();
            exporter.SaveImageToStream(image, DevIL.ImageType.Png, output);
            output.Position = 0;
            using Bitmap temporary = new Bitmap(output);
            return new Bitmap(temporary);
          }
        } catch { }
      }
      return null;
    }

    private Boolean TryRenderNodeModel(GomObject gom) {
      StopNodePreviewRenderer(false);
      if (gom == null || _currentAssets == null || _currentDom == null) return false;

      _nodePreviewModels = new Dictionary<String, GR2>(StringComparer.OrdinalIgnoreCase);
      _nodePreviewResources = new Dictionary<String, Object>(StringComparer.OrdinalIgnoreCase);
      if (_nodePreviewModelLabel != null) _nodePreviewModelLabel.Text = "3D model preview";
      String renderType = String.Empty;
      Boolean built = false;

      try {
        String prefix = gom.Name.Length >= 4 ? gom.Name.Substring(0, 4).ToLowerInvariant() : gom.Name.ToLowerInvariant();
        switch (prefix) {
          case "dyn.":
            built = BuildDynPreview(gom);
            renderType = "dyn";
            break;
          case "ipp.": {
            ItemAppearance appearance = _currentDom.AppearanceLoader.Load(gom) as ItemAppearance;
            built = _nodePreviewIppSetCheck != null && _nodePreviewIppSetCheck.Checked
              ? BuildIppSetPreview(gom, appearance)
              : BuildIppPreview(appearance);
            renderType = "ipp";
            break;
          }
          case "npp.": {
            NpcAppearance appearance = _currentDom.AppearanceLoader.Load(gom) as NpcAppearance;
            built = BuildNpcAppearancePreview(appearance);
            renderType = appearance?.NppType ?? String.Empty;
            break;
          }
          case "npc.": {
            Npc npc = _currentDom.NpcLoader.Load(gom);
            NpcAppearance appearance = null;
            foreach (NpcVisualData visual in npc?.VisualDataList ?? new List<NpcVisualData>()) {
              if (String.IsNullOrWhiteSpace(visual?.AppearanceFqn)) continue;
              try { appearance = _currentDom.AppearanceLoader.Load(visual.AppearanceFqn) as NpcAppearance; } catch { }
              if (appearance != null) break;
            }
            built = BuildNpcAppearancePreview(appearance);
            renderType = appearance?.NppType ?? String.Empty;
            break;
          }
          case "spn.": {
            built = BuildSpawnerPreview(gom, out renderType);
            break;
          }
          case "enc.": {
            built = BuildEncounterSpawnerPreview(gom, out renderType);
            break;
          }
          case "plc.": {
            Placeable placeable = _currentDom.PlaceableLoader.Load(gom);
            if (!String.IsNullOrWhiteSpace(placeable?.Model) && placeable.Model.StartsWith("dyn.", StringComparison.OrdinalIgnoreCase)) {
              GomObject dyn = _currentDom.GetObject(placeable.Model);
              built = dyn != null && BuildDynPreview(dyn);
              renderType = "dyn";
            } else {
              built = BuildDirectGr2Preview(placeable?.Model, "placeable");
              renderType = "plc";
            }
            break;
          }
          case "itm.": {
            Item item = _currentDom.ItemLoader.Load(gom);
            built = BuildDirectGr2Preview(item?.Model, "item");
            renderType = "itm";
            break;
          }
        }

        // Best-effort fallback for node families without a dedicated model loader. Many GOM nodes still contain a
        // direct *.gr2 value somewhere in their raw data (visual/model/mesh fields). Reuse the same GR2 renderer for
        // those instead of limiting the Node Browser to a fixed list of prefixes.
        if (!built) built = BuildGenericGr2Preview(gom);
      } catch (Exception ex) {
        System.Diagnostics.Debug.WriteLine("Node model preview failed for " + gom.Name + ": " + ex);
        built = false;
      }

      if (!built || _nodePreviewModels.Count == 0) {
        DisposeUnrenderedNodeModels();
        return false;
      }

      try {
        // Direct3D needs a real, visible host control when its swap chain is created.
        _nodePreviewSplit.Panel1Collapsed = false;
        if (_nodePreviewContentSplit != null) _nodePreviewContentSplit.Panel2Collapsed = false;
        _nodePreviewModelPanel.Visible = true;
        ResizeNodePreviewLayout();
        _nodePreviewModelPanel.PerformLayout();
        if (_nodePreviewRenderer == null) {
          _nodePreviewRenderer = new View_NPC_GR2(Handle, this, NodePreviewModelPanelName);
          if (!_nodePreviewRenderer.Init()) {
            _nodePreviewRenderer = null;
            DisposeUnrenderedNodeModels();
            return false;
          }
        }
        _nodePreviewRenderer.LoadModel(_nodePreviewModels, _nodePreviewResources, gom.Name, renderType);
        _nodePreviewRenderer.SetSize(_nodePreviewModelPanel.Height, _nodePreviewModelPanel.Width);
        _nodePreviewRenderThread = new Thread(_nodePreviewRenderer.StartRender) { IsBackground = true, Name = "NodeBrowserModelPreview" };
        _nodePreviewRenderThread.Start();
        return true;
      } catch (Exception ex) {
        System.Diagnostics.Debug.WriteLine("Node renderer initialization failed: " + ex);
        DisposeUnrenderedNodeModels();
        return false;
      }
    }


    private Boolean BuildSpawnerPreview(GomObject spawner, out String renderType) {
      renderType = "spn";
      if (spawner?.Data == null) return false;
      try {
        List<Object> rows = spawner.Data.ValueOrDefault<List<Object>>("spnEntityList", null);
        if (rows == null) return false;
        foreach (Object entry in rows) {
          if (entry is not GomObjectData row) continue;
          Object raw = row.ValueOrDefault<Object>("spnEntityFqn", null)
            ?? row.ValueOrDefault<Object>("spnEntityId", null);
          String fqn = ResolveNodeReferenceName(raw);
          if (String.IsNullOrWhiteSpace(fqn)) continue;
          GomObject entity = null;
          try { entity = _currentDom.GetObject(fqn); } catch { }
          if (entity == null) continue;
          if (BuildReferencedNodeModel(entity, out String entityType)) {
            renderType = String.IsNullOrWhiteSpace(entityType) ? "spn" : entityType;
            if (_nodePreviewModelLabel != null) _nodePreviewModelLabel.Text = "3D model preview — " + fqn;
            return true;
          }
        }
      } catch (Exception ex) {
        System.Diagnostics.Debug.WriteLine("Spawner preview failed: " + ex.Message);
      }
      return false;
    }

    private Boolean BuildEncounterSpawnerPreview(GomObject encounter, out String renderType) {
      renderType = "enc";
      if (encounter?.Data == null) return false;
      try {
        Object mapRaw = encounter.Data.ValueOrDefault<Object>("spnEncounterSpawnerIdsToFqns", null);
        if (mapRaw is IDictionary dictionary) {
          foreach (DictionaryEntry pair in dictionary) {
            String spnFqn = ResolveNodeReferenceName(pair.Value);
            if (String.IsNullOrWhiteSpace(spnFqn)) continue;
            GomObject spn = null;
            try { spn = _currentDom.GetObject(spnFqn); } catch { }
            if (spn == null || !spn.Name.StartsWith("spn.", StringComparison.OrdinalIgnoreCase)) continue;
            if (BuildSpawnerPreview(spn, out renderType)) return true;
          }
        }
      } catch (Exception ex) {
        System.Diagnostics.Debug.WriteLine("Encounter preview failed: " + ex.Message);
      }
      return false;
    }

    private String ResolveNodeReferenceName(Object raw) {
      if (raw == null || _currentDom == null) return null;
      try {
        if (raw is String text) {
          String value = text.Trim();
          if (value.Length == 0) return null;
          if (!UInt64.TryParse(value, out UInt64 numeric)) return value;
          return _currentDom.GetObject(numeric)?.Name ?? value;
        }
        if (raw is UInt64 u) return _currentDom.GetObject(u)?.Name;
        if (raw is Int64 i && i >= 0) return _currentDom.GetObject((UInt64)i)?.Name;
        String s = raw.ToString()?.Trim();
        if (UInt64.TryParse(s, out UInt64 id)) return _currentDom.GetObject(id)?.Name ?? s;
        return s;
      } catch { return null; }
    }

    private Boolean BuildReferencedNodeModel(GomObject gom, out String renderType) {
      renderType = String.Empty;
      if (gom == null) return false;
      String prefix = gom.Name.Length >= 4 ? gom.Name.Substring(0, 4).ToLowerInvariant() : gom.Name.ToLowerInvariant();
      switch (prefix) {
        case "dyn.":
          renderType = "dyn";
          return BuildDynPreview(gom);
        case "npp.": {
          NpcAppearance appearance = _currentDom.AppearanceLoader.Load(gom) as NpcAppearance;
          renderType = appearance?.NppType ?? String.Empty;
          return BuildNpcAppearancePreview(appearance);
        }
        case "npc.": {
          Npc npc = _currentDom.NpcLoader.Load(gom);
          NpcAppearance appearance = null;
          foreach (NpcVisualData visual in npc?.VisualDataList ?? new List<NpcVisualData>()) {
            if (String.IsNullOrWhiteSpace(visual?.AppearanceFqn)) continue;
            try { appearance = _currentDom.AppearanceLoader.Load(visual.AppearanceFqn) as NpcAppearance; } catch { }
            if (appearance != null) break;
          }
          renderType = appearance?.NppType ?? String.Empty;
          return BuildNpcAppearancePreview(appearance);
        }
        case "plc.": {
          Placeable plc = _currentDom.PlaceableLoader.Load(gom);
          renderType = "plc";
          if (!String.IsNullOrWhiteSpace(plc?.Model) && plc.Model.StartsWith("dyn.", StringComparison.OrdinalIgnoreCase)) {
            GomObject dyn = _currentDom.GetObject(plc.Model);
            if (dyn != null) return BuildDynPreview(dyn);
          }
          return BuildDirectGr2Preview(plc?.Model, "placeable");
        }
        case "ipp.": {
          ItemAppearance appearance = _currentDom.AppearanceLoader.Load(gom) as ItemAppearance;
          renderType = "ipp";
          return BuildIppPreview(appearance);
        }
        case "itm.": {
          Item item = _currentDom.ItemLoader.Load(gom);
          renderType = "itm";
          return BuildDirectGr2Preview(item?.Model, "item");
        }
      }
      return BuildGenericGr2Preview(gom);
    }

    private Boolean BuildDynPreview(GomObject gom) {
      List<Object> visualList = gom.Data.ValueOrDefault<List<Object>>("dynVisualList", null);
      if (visualList == null) return false;
      Int32 index = 0;
      foreach (Object raw in visualList) {
        if (raw is not GomObjectData visualItem) continue;
        String model = visualItem.ValueOrDefault<String>("dynVisualFqn", null);
        if (String.IsNullOrWhiteSpace(model) || !model.Contains(".gr2", StringComparison.OrdinalIgnoreCase)
            || model.Contains("designblockout", StringComparison.OrdinalIgnoreCase)) continue;
        String visualName = visualItem.ValueOrDefault<String>("dynVisualName", "visual" + index++);
        Vector3 rotation = ReadVector3(visualItem.ValueOrDefault<List<Single>>("dynRotation", null), new Vector3());
        Vector3 scale = ReadVector3(visualItem.ValueOrDefault<List<Single>>("dynScale", null), new Vector3(1, 1, 1));
        Vector3 position = ReadVector3(visualItem.ValueOrDefault<List<Single>>("dynPosition", null), new Vector3());
        TorFile file = _currentAssets.FindFile("/resources" + model.Replace('\\', '/'));
        if (file == null) continue;
        using (file)
        using (BinaryReader reader = new BinaryReader(file.OpenCopyInMemory())) {
          String name = model.Split('/', '\\').Last();
          GR2 gr2 = new GR2(reader, name) {
            transformMatrix = Matrix.Scaling(scale)
              * Matrix.RotationZ((Single)(rotation.Z * Math.PI / 180.0))
              * Matrix.RotationX((Single)(rotation.X * Math.PI / 180.0))
              * Matrix.RotationY((Single)(rotation.Y * Math.PI / 180.0))
              * Matrix.Translation(position)
          };
          EnsureNodePreviewMaterials(gr2);
          _nodePreviewModels[visualName ?? name] = gr2;
        }
      }
      return _nodePreviewModels.Count > 0;
    }

    private static Vector3 ReadVector3(List<Single> values, Vector3 fallback) {
      if (values == null || values.Count < 3) return fallback;
      return new Vector3(values[0], values[1], values[2]);
    }

    private Boolean BuildGenericGr2Preview(GomObject gom) {
      if (gom?.Data == null) return false;
      List<String> modelPaths = new List<String>();
      HashSet<Object> visited = new HashSet<Object>(ReferenceEqualityComparer.Instance);
      CollectRawGr2Paths(gom.Data, modelPaths, visited, 0, String.Empty);
      Int32 index = 0;
      foreach (String raw in modelPaths.Distinct(StringComparer.OrdinalIgnoreCase).Take(12)) {
        String path = raw.Replace('\\', '/');
        if (path.IndexOf("designblockout", StringComparison.OrdinalIgnoreCase) >= 0) continue;
        if (!LoadDirectGr2IntoDictionary(path, "model" + index, true)) continue;
        index++;
      }
      return _nodePreviewModels.Count > 0;
    }

    private static void CollectRawGr2Paths(Object value, List<String> paths, HashSet<Object> visited,
                                           Int32 depth, String fieldName) {
      if (value == null || paths.Count >= 16 || depth > 5) return;
      Type type = value.GetType();
      if (!type.IsValueType && value is not String && !visited.Add(value)) return;

      if (value is String text) {
        if (text.IndexOf(".gr2", StringComparison.OrdinalIgnoreCase) < 0) return;
        String key = fieldName ?? String.Empty;
        if (key.Length > 0 && key.IndexOf("model", StringComparison.OrdinalIgnoreCase) < 0 &&
            key.IndexOf("visual", StringComparison.OrdinalIgnoreCase) < 0 &&
            key.IndexOf("mesh", StringComparison.OrdinalIgnoreCase) < 0 &&
            key.IndexOf("skeleton", StringComparison.OrdinalIgnoreCase) < 0 &&
            key.IndexOf("gr2", StringComparison.OrdinalIgnoreCase) < 0) return;
        paths.Add(text);
        return;
      }

      if (value is GomObjectData data) {
        if (data.Dictionary == null) return;
        foreach (KeyValuePair<String, Object> pair in data.Dictionary) {
          if (paths.Count >= 16) break;
          CollectRawGr2Paths(pair.Value, paths, visited, depth + 1, pair.Key);
        }
        return;
      }

      if (value is IDictionary dictionary) {
        foreach (DictionaryEntry entry in dictionary) {
          if (paths.Count >= 16) break;
          CollectRawGr2Paths(entry.Value, paths, visited, depth + 1, entry.Key?.ToString() ?? fieldName);
        }
        return;
      }

      if (value is IEnumerable enumerable && value is not String) {
        Int32 count = 0;
        foreach (Object item in enumerable) {
          if (paths.Count >= 16 || count++ >= 48) break;
          Object child = item;
          String childField = fieldName;
          try {
            Type itemType = item?.GetType();
            PropertyInfo key = itemType?.GetProperty("Key"), val = itemType?.GetProperty("Value");
            if (val != null) {
              child = val.GetValue(item);
              childField = key?.GetValue(item)?.ToString() ?? fieldName;
            }
          } catch { }
          CollectRawGr2Paths(child, paths, visited, depth + 1, childField);
        }
      }
    }

    private List<GomObject> GetIppSetObjects(GomObject selected) {
      List<GomObject> fallback = new List<GomObject>();
      if (selected == null || !selected.Name.StartsWith("ipp.", StringComparison.OrdinalIgnoreCase)) return fallback;
      fallback.Add(selected);
      if (_assetDict == null || !_assetDict.TryGetValue(selected.Name, out NodeAsset selectedAsset)) return fallback;

      String parent = selectedAsset.parentId;
      // IPP armor sets are represented in the Node/Model Browser hierarchy as a directory whose
      // direct children are the individual slots. Search the nearest few ancestors so a variant
      // leaf can still find the enclosing set, while refusing very broad categories.
      for (Int32 depth = 0; depth < 3 && !String.IsNullOrWhiteSpace(parent) && parent != "/"; depth++) {
        List<GomObject> candidates = _assetDict.Values
          .Where(x => x != null
                      && String.Equals(x.parentId, parent, StringComparison.OrdinalIgnoreCase)
                      && !String.IsNullOrWhiteSpace(x.id)
                      && x.id.StartsWith("ipp.", StringComparison.OrdinalIgnoreCase))
          .Select(x => x.Obj)
          .Where(x => x != null)
          .Where(x => !x.Name.Split('.').Last().Contains("_", StringComparison.Ordinal))
          .Distinct()
          .Take(17)
          .ToList();

        if (candidates.Count >= 2 && candidates.Count <= 16) {
          if (!candidates.Any(x => String.Equals(x.Name, selected.Name, StringComparison.OrdinalIgnoreCase))
              && candidates.Count < 16)
            candidates.Insert(0, selected);
          return candidates;
        }

        if (_assetDict.TryGetValue(parent, out NodeAsset parentAsset)) parent = parentAsset.parentId;
        else {
          Int32 dot = parent.LastIndexOf('.');
          parent = dot > 0 ? parent.Substring(0, dot) : "/";
        }
      }
      return fallback;
    }

    private Boolean BuildIppSetPreview(GomObject selected, ItemAppearance selectedAppearance) {
      List<GomObject> setObjects = GetIppSetObjects(selected);
      if (setObjects.Count <= 1) return BuildIppPreview(selectedAppearance);

      Boolean built = false;
      foreach (GomObject setObject in setObjects) {
        ItemAppearance appearance = null;
        try {
          appearance = String.Equals(setObject.Name, selected?.Name, StringComparison.OrdinalIgnoreCase)
            ? selectedAppearance
            : _currentDom.AppearanceLoader.Load(setObject) as ItemAppearance;
        } catch (Exception ex) {
          System.Diagnostics.Debug.WriteLine("Unable to load IPP set member " + setObject.Name + ": " + ex.Message);
        }
        if (appearance == null) continue;
        built |= BuildIppPreview(appearance);
      }
      return built;
    }

    private Boolean BuildIppPreview(ItemAppearance itemData) {
      if (itemData?.IPP == null) return false;
      String model = _currentDom.AppearanceLoader.ApplyPartsMacros(itemData.IPP.Model, NodePreviewBodyType).Replace('\\', '/');
      if (String.IsNullOrWhiteSpace(model)) return false;
      if (model.EndsWith(".dds", StringComparison.OrdinalIgnoreCase)) {
        _nodePreviewResources[model.Split('/').Last()] = model;
        return false;
      }
      if (!model.Contains(".gr2", StringComparison.OrdinalIgnoreCase)) return false;
      TorFile modelFile = _currentAssets.FindFile("/resources" + model);
      if (modelFile == null) return false;

      using (modelFile)
      using (BinaryReader reader = new BinaryReader(modelFile.OpenCopyInMemory())) {
        String name = model.Split('/').Last();
        GR2 gr2 = new GR2(reader, name);
        ApplyIppMaterials(gr2, itemData.IPP, model);
        if (itemData.IPP.AttachedModels != null) {
          foreach (String attach in itemData.IPP.AttachedModels.Where(x => !String.IsNullOrWhiteSpace(x))) {
            String attachPath = _currentDom.AppearanceLoader.ApplyPartsMacros(attach, NodePreviewBodyType).Replace('\\', '/');
            TorFile attachFile = _currentAssets.FindFile("/resources" + attachPath);
            if (attachFile == null) continue;
            using (attachFile)
            using (BinaryReader attachReader = new BinaryReader(attachFile.OpenCopyInMemory())) {
              GR2 attached = new GR2(attachReader, attachPath.Split('/').Last());
              ApplyIppMaterials(attached, itemData.IPP, attachPath);
              attached.transformMatrix = Matrix.Scaling(new Vector3(1, 1, 1));
              gr2.attachedModels.Add(attached);
            }
          }
        }
        gr2.transformMatrix = Matrix.Scaling(new Vector3(1, 1, 1));
        _nodePreviewModels[name] = gr2;
      }
      return true;
    }

    private void ApplyIppMaterials(GR2 model, AppSlot slot, String modelPath) {
      String material0 = _currentDom.AppearanceLoader.ApplyPartsMacros(slot.Material0, NodePreviewBodyType);
      String materialMirror = _currentDom.AppearanceLoader.ApplyPartsMacros(slot.MaterialMirror, NodePreviewBodyType);
      String palette1 = !String.IsNullOrWhiteSpace(slot.PrimaryHue) ? "/resources" + slot.PrimaryHue.Split(';').First().Replace('\\', '/') : String.Empty;
      String palette2 = !String.IsNullOrWhiteSpace(slot.SecondaryHue) ? "/resources" + slot.SecondaryHue.Split(';').First().Replace('\\', '/') : String.Empty;
      if (String.IsNullOrWhiteSpace(material0)) return;

      Int32 wanted = Math.Max(1, Math.Min(2, (Int32)model.numMaterials));
      model.numMaterials = checked((UInt16)wanted);
      model.materials = new List<GR2_Material>();
      GR2_Material first = new GR2_Material(material0) { palette1XML = palette1, palette2XML = palette2 };
      model.materials.Add(first);
      if (wanted > 1) {
        if (String.IsNullOrWhiteSpace(materialMirror)) {
          String appSlot = modelPath.Split('/').Last().Split('_').First();
          materialMirror = appSlot + "_naked_caucasian_young_a01c01_" + NodePreviewBodyType;
        }
        model.materials.Add(new GR2_Material(materialMirror) { palette1XML = palette1, palette2XML = palette2 });
      }
    }

    private Boolean BuildNpcAppearancePreview(NpcAppearance appearance) {
      if (appearance == null) return false;
      String body = appearance.BodyType ?? String.Empty;
      if (!String.IsNullOrWhiteSpace(body)) {
        String skeleton = (body.StartsWith("bf", StringComparison.OrdinalIgnoreCase) || body.StartsWith("bm", StringComparison.OrdinalIgnoreCase))
          ? "/resources/art/dynamic/spec/" + body + "new_skeleton.gr2"
          : "/resources/art/dynamic/spec/" + body + "_skeleton.gr2";
        LoadDirectGr2IntoDictionary(skeleton, "skeleton", false);
      }

      if (appearance.AppearanceSlotMap == null) return _nodePreviewModels.Count > 0;
      AppSlot head = appearance.AppearanceSlotMap.TryGetValue("appSlotHead", out List<AppSlot> heads)
        ? heads?.FirstOrDefault(x => x != null) : null;

      foreach (KeyValuePair<String, List<AppSlot>> pair in appearance.AppearanceSlotMap) {
        AppSlot slot = pair.Value?.FirstOrDefault(x => x != null);
        if (slot == null) continue;
        String slotBody = !String.IsNullOrWhiteSpace(slot.BodyType) ? slot.BodyType : body;
        String modelPath = _currentDom.AppearanceLoader.ApplyPartsMacros(slot.Model, slotBody);
        if (pair.Key.IndexOf("FaceHair", StringComparison.OrdinalIgnoreCase) >= 0 && String.IsNullOrWhiteSpace(modelPath))
          modelPath = "/art/defaultassets/blank.gr2";
        if (String.IsNullOrWhiteSpace(modelPath)) continue;

        if (modelPath.EndsWith(".dds", StringComparison.OrdinalIgnoreCase)) {
          _nodePreviewResources[pair.Key] = modelPath;
          continue;
        }
        if (modelPath.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) {
          String dynFqn = modelPath.Replace('\\', '/').Replace("/art/", "", StringComparison.OrdinalIgnoreCase)
            .Replace(".xml", "", StringComparison.OrdinalIgnoreCase).Replace('/', '.');
          GomObject dynObject = _currentDom.GetObject(dynFqn);
          if (dynObject != null) _nodePreviewResources[pair.Key] = dynObject;
          continue;
        }
        if (!modelPath.EndsWith(".gr2", StringComparison.OrdinalIgnoreCase)) continue;

        TorFile modelFile = _currentAssets.FindFile("/resources" + modelPath.Replace('\\', '/'));
        if (modelFile == null) continue;
        using (modelFile)
        using (BinaryReader reader = new BinaryReader(modelFile.OpenCopyInMemory())) {
          GR2 gr2 = new GR2(reader, modelPath.Split('/', '\\').Last());
          String material0 = _currentDom.AppearanceLoader.ApplyPartsMacros(slot.Material0, slotBody);
          String materialMirror = _currentDom.AppearanceLoader.ApplyPartsMacros(slot.MaterialMirror, slotBody);
          if (head?.AMI?.ChildSkinMaterials != null) {
            Dictionary<String, String> skin = head.AMI.ChildSkinMaterials;
            if (!String.IsNullOrWhiteSpace(material0)
                && material0.IndexOf("_naked_", StringComparison.OrdinalIgnoreCase) >= 0
                && skin.TryGetValue(pair.Key, out String skin0))
              material0 = _currentDom.AppearanceLoader.ApplyPartsMacros(skin0, slotBody);
            if (String.IsNullOrWhiteSpace(materialMirror) && skin.TryGetValue(pair.Key, out String skin1))
              materialMirror = _currentDom.AppearanceLoader.ApplyPartsMacros(skin1, slotBody);
          }
          String palette1 = !String.IsNullOrWhiteSpace(slot.PrimaryHue) ? "/resources" + slot.PrimaryHue.Split(';').First().Replace('\\', '/') : String.Empty;
          String palette2 = !String.IsNullOrWhiteSpace(slot.SecondaryHue) ? "/resources" + slot.SecondaryHue.Split(';').First().Replace('\\', '/') : String.Empty;
          gr2.materials = new List<GR2_Material>();
          if (!String.IsNullOrWhiteSpace(material0)) gr2.materials.Add(new GR2_Material(material0) { palette1XML = palette1, palette2XML = palette2 });
          if (!String.IsNullOrWhiteSpace(materialMirror)) gr2.materials.Add(new GR2_Material(materialMirror) { palette1XML = palette1, palette2XML = palette2 });

          if (slot.AttachedModels != null) {
            foreach (String attach in slot.AttachedModels.Where(x => !String.IsNullOrWhiteSpace(x))) {
              String attachPath = _currentDom.AppearanceLoader.ApplyPartsMacros(attach, slotBody).Replace('\\', '/');
              TorFile attachFile = _currentAssets.FindFile("/resources" + attachPath);
              if (attachFile == null) continue;
              using (attachFile)
              using (BinaryReader attachReader = new BinaryReader(attachFile.OpenCopyInMemory())) {
                GR2 attached = new GR2(attachReader, attachPath.Split('/').Last()) {
                  materials = new List<GR2_Material>(gr2.materials),
                  transformMatrix = Matrix.Scaling(new Vector3(1, 1, 1))
                };
                gr2.attachedModels.Add(attached);
              }
            }
          }
          gr2.transformMatrix = Matrix.Scaling(new Vector3(1, 1, 1));
          _nodePreviewModels[pair.Key] = gr2;
        }
      }
      return _nodePreviewModels.Count > 0;
    }

    private Boolean BuildDirectGr2Preview(String modelPath, String key) {
      if (String.IsNullOrWhiteSpace(modelPath) || !modelPath.Contains(".gr2", StringComparison.OrdinalIgnoreCase)) return false;
      String path = modelPath.Replace('\\', '/');
      if (!path.StartsWith("/resources", StringComparison.OrdinalIgnoreCase)) path = "/resources" + (path.StartsWith("/") ? path : "/" + path);
      return LoadDirectGr2IntoDictionary(path, key, true);
    }

    private Boolean LoadDirectGr2IntoDictionary(String resourcePath, String key, Boolean setTransform) {
      if (String.IsNullOrWhiteSpace(resourcePath)) return false;
      resourcePath = resourcePath.Replace('\\', '/');
      if (!resourcePath.StartsWith("/resources", StringComparison.OrdinalIgnoreCase))
        resourcePath = "/resources" + (resourcePath.StartsWith("/", StringComparison.Ordinal) ? resourcePath : "/" + resourcePath);
      TorFile file = _currentAssets.FindFile(resourcePath);
      if (file == null) return false;
      using (file)
      using (BinaryReader reader = new BinaryReader(file.OpenCopyInMemory())) {
        String name = resourcePath.Split('/', '\\').Last();
        GR2 gr2 = new GR2(reader, name);
        EnsureNodePreviewMaterials(gr2);
        if (setTransform) gr2.transformMatrix = Matrix.Scaling(new Vector3(1, 1, 1));
        _nodePreviewModels[key ?? name] = gr2;
      }
      return true;
    }

    private static void EnsureNodePreviewMaterials(GR2 model) {
      if (model == null) return;
      if (model.materials == null) model.materials = new List<GR2_Material>();
      if (model.materials.Count == 0) {
        Int32 pieces = 0;
        foreach (GR2_Mesh mesh in model.meshes ?? new List<GR2_Mesh>()) {
          if (mesh == null || (mesh.meshName ?? String.Empty).IndexOf("collision", StringComparison.OrdinalIgnoreCase) >= 0) continue;
          pieces = Math.Max(pieces, mesh.numPieces);
        }
        model.numMaterials = checked((UInt16)Math.Min(UInt16.MaxValue, Math.Max((Int32)model.numMaterials, pieces)));
        if (model.numMaterials <= 0) model.numMaterials = 1;
        model.materials.Add(new GR2_Material("all_test_grey_128"));
        if (model.numMaterials > 1) model.materials.Add(new GR2_Material("defaultMirror"));
      } else if (String.Equals(model.materials[0]?.materialName, "default", StringComparison.OrdinalIgnoreCase)) {
        model.materials[0] = new GR2_Material("all_test_grey_128");
      }
    }

    private void StopNodePreviewRenderer(Boolean disposeRenderer) {
      View_NPC_GR2 renderer = _nodePreviewRenderer;
      Thread renderThread = _nodePreviewRenderThread;

      try { renderer?.StopRender(); } catch { }

      Boolean stopped = renderThread == null || !renderThread.IsAlive;
      if (!stopped) {
        try { stopped = renderThread.Join(disposeRenderer ? 100 : 500); } catch { }
      }

      _nodePreviewRenderThread = null;

      if (renderer == null) {
        DisposeUnrenderedNodeModels();
        _nodePreviewModels = null;
        _nodePreviewResources = null;
        return;
      }

      if (disposeRenderer || !stopped) {
        // Never perform the expensive D3D release on the WinForms close path,
        // and never Clear()/Dispose() while the render thread is still inside
        // DrawScene/Present. Detach it and finish bounded cleanup in the
        // background. A later preview creates a fresh renderer if necessary.
        _nodePreviewRenderer = null;
        _nodePreviewModels = null;
        _nodePreviewResources = null;
        ThreadPool.QueueUserWorkItem(_ => {
          Boolean eventuallyStopped = stopped || renderThread == null || !renderThread.IsAlive;
          if (!eventuallyStopped) {
            try { eventuallyStopped = renderThread.Join(5000); } catch { }
          }
          if (!eventuallyStopped) return;
          try { renderer.Clear(); } catch { }
          try { renderer.Dispose(); } catch { }
        });
        return;
      }

      // Normal preview-to-preview replacement: the render thread has stopped,
      // so Clear() is safe and the initialized renderer can be reused.
      try { renderer.Clear(); } catch { }
      _nodePreviewModels = null;
      _nodePreviewResources = null;
    }

    private void DisposeUnrenderedNodeModels() {
      if (_nodePreviewModels != null) {
        foreach (GR2 model in _nodePreviewModels.Values) {
          try { model?.Dispose(); } catch { }
        }
        _nodePreviewModels.Clear();
      }
      _nodePreviewResources?.Clear();
    }

    private void DisposeNodePreview() {
      StopNodePreviewRenderer(true);
      if (_nodePreviewIcon?.Image != null) {
        System.Drawing.Image image = _nodePreviewIcon.Image;
        _nodePreviewIcon.Image = null;
        image.Dispose();
      }
      DisposeNodeReferenceUi();
      DisposeNodeConversationUi();
      if (_nodeGameplayExplorer != null && !_nodeGameplayExplorer.IsDisposed) {
        try { _nodeGameplayExplorer.Dispose(); } catch { }
      }
      _nodeGameplayExplorer = null;
    }
  }
}
