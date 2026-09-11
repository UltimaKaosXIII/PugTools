using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

using GomLib;
using GomLib.Models;

namespace PugTools {
  /// <summary>
  /// Database-free Jedipedia-style semantic explorer for common GOM gameplay nodes.  It deliberately
  /// uses only the currently loaded DataObjectModel and the model loaders that PugTools already ships.
  /// </summary>
  internal sealed class NodeGameplayExplorer : Form {
    private const Int32 MaxTreeNodes = 5000;
    private readonly DataObjectModel _dom;
    private readonly Action<String> _navigate;
    private readonly Action<MapNote> _openWorldMapNote;
    private readonly Action<String> _openModelPreview;
    private readonly Func<String, Bitmap> _graphIconLoader;
    private readonly Func<Bitmap> _rootIconProvider;
    private readonly TreeView _tree;
    private readonly Font _linkFont;
    private readonly ListView _details;
    private readonly TabControl _tabs;
    private readonly GameplayRelationshipGraphControl _graph;
    private readonly ToolStripLabel _status;
    private readonly ToolStripTextBox _filter;
    private readonly ToolStripButton _go;
    private readonly ToolStripButton _world;
    private readonly ToolStripButton _model;
    private readonly PictureBox _heroIcon;
    private readonly Label _heroTitle;
    private readonly Label _heroMeta;
    private Int32 _createdNodes;
    private ExplorerEntry _selectedEntry;

    private sealed class ExplorerEntry {
      public String TargetFqn { get; set; }
      public UInt64 TargetId { get; set; }
      public MapNote WorldMapNote { get; set; }
      public String ModelPreviewFqn { get; set; }
      public List<KeyValuePair<String, String>> Details { get; } = new List<KeyValuePair<String, String>>();
    }

    internal NodeGameplayExplorer(DataObjectModel dom, GomObject gom, Action<String> navigate, Action<MapNote> openWorldMapNote,
                                  Action<String> openModelPreview, Func<String, Bitmap> graphIconLoader, Func<Bitmap> rootIconProvider) {
      _dom = dom ?? throw new ArgumentNullException(nameof(dom));
      _navigate = navigate;
      _openWorldMapNote = openWorldMapNote;
      _openModelPreview = openModelPreview;
      _graphIconLoader = graphIconLoader;
      _rootIconProvider = rootIconProvider;
      Text = "Gameplay Explorer — " + (gom?.Name ?? "Node");
      StartPosition = FormStartPosition.CenterParent;
      Width = 1180;
      Height = 760;
      MinimumSize = new Size(760, 480);
      ShowInTaskbar = false;

      ToolStrip tools = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Dock = DockStyle.Top };
      ToolStripButton expand = new ToolStripButton("Expand all");
      expand.Click += delegate { _tree.ExpandAll(); };
      ToolStripButton collapse = new ToolStripButton("Collapse");
      collapse.Click += delegate { _tree.CollapseAll(); if (_tree.Nodes.Count > 0) _tree.Nodes[0].Expand(); };
      _go = new ToolStripButton("Go to node") { Enabled = false };
      _go.Click += delegate { NavigateSelected(); };
      _world = new ToolStripButton("Show in World") { Enabled = false, ToolTipText = "Find this map note in the installed world data and teleport to it" };
      _world.Click += delegate { OpenSelectedInWorld(); };
      _model = new ToolStripButton("3D / Model") { Enabled = false, ToolTipText = "Open this item/appearance in Model Browser" };
      _model.Click += delegate { OpenSelectedModel(); };
      ToolStripButton copy = new ToolStripButton("Copy details");
      copy.Click += delegate { CopySelectedDetails(); };
      _filter = new ToolStripTextBox { AutoSize = false, Width = 220, ToolTipText = "Find next tree entry" };
      _filter.KeyDown += delegate (Object sender, KeyEventArgs e) { if (e.KeyCode == Keys.Enter) { FindNext(); e.SuppressKeyPress = true; } };
      ToolStripButton find = new ToolStripButton("Find next");
      find.Click += delegate { FindNext(); };
      _status = new ToolStripLabel();
      tools.Items.Add(expand);
      tools.Items.Add(collapse);
      tools.Items.Add(new ToolStripSeparator());
      tools.Items.Add(_go);
      tools.Items.Add(_world);
      tools.Items.Add(_model);
      tools.Items.Add(copy);
      tools.Items.Add(new ToolStripSeparator());
      tools.Items.Add(new ToolStripLabel("Find:"));
      tools.Items.Add(_filter);
      tools.Items.Add(find);
      tools.Items.Add(new ToolStripSeparator());
      tools.Items.Add(_status);

      SplitContainer split = new SplitContainer {
        Dock = DockStyle.Fill
      };
      _tree = new TreeView { Dock = DockStyle.Fill, HideSelection = false, FullRowSelect = true };
      _linkFont = new Font(_tree.Font, FontStyle.Underline);
      _tree.AfterSelect += TreeAfterSelect;
      _tree.NodeMouseDoubleClick += delegate { NavigateSelected(); };
      _tree.KeyDown += delegate (Object sender, KeyEventArgs e) { if (e.KeyCode == Keys.Enter) { NavigateSelected(); e.SuppressKeyPress = true; } };
      split.Panel1.Controls.Add(_tree);

      _details = new ListView {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        GridLines = true,
        HideSelection = false
      };
      _details.Columns.Add("Field", 165);
      _details.Columns.Add("Value", 470);
      _details.DoubleClick += delegate { NavigateSelected(); };
      split.Panel2.Controls.Add(_details);

      _tabs = new TabControl { Dock = DockStyle.Fill };
      TabPage treePage = new TabPage("Structured tree");
      treePage.Controls.Add(split);
      _graph = new GameplayRelationshipGraphControl { Dock = DockStyle.Fill, NavigateRequested = _navigate, IconLoader = _graphIconLoader };
      _graph.WorldMapNoteRequested = OpenWorldMapNoteFqn;
      _graph.ModelPreviewRequested = delegate (String fqn) { _openModelPreview?.Invoke(fqn); };
      TabPage graphPage = new TabPage("Relationship graph");
      graphPage.Controls.Add(_graph);
      _tabs.TabPages.Add(treePage);
      _tabs.TabPages.Add(graphPage);

      Panel hero = new Panel { Dock = DockStyle.Top, Height = 92, Padding = new Padding(8, 7, 8, 7) };
      _heroIcon = new PictureBox { Location = new Point(8, 7), Size = new Size(76, 76), SizeMode = PictureBoxSizeMode.Zoom, BorderStyle = BorderStyle.FixedSingle, Visible = false };
      _heroTitle = new Label { Location = new Point(94, 9), Height = 28, AutoEllipsis = true, Font = new Font(Font, FontStyle.Bold), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
      _heroMeta = new Label { Location = new Point(94, 39), Height = 40, AutoEllipsis = true, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
      hero.Controls.Add(_heroIcon);
      hero.Controls.Add(_heroTitle);
      hero.Controls.Add(_heroMeta);
      hero.Resize += delegate {
        Int32 left = _heroIcon.Visible ? 94 : 8;
        _heroTitle.Left = left; _heroMeta.Left = left;
        _heroTitle.Width = Math.Max(80, hero.ClientSize.Width - left - 10);
        _heroMeta.Width = Math.Max(80, hero.ClientSize.Width - left - 10);
      };

      Controls.Add(_tabs);
      Controls.Add(hero);
      Controls.Add(tools);

      SplitContainerSafeLayout.ApplyOnLoad(this, split, 650, 280, 260);
      Build(gom);
    }

    protected override void Dispose(Boolean disposing) {
      if (disposing) {
        _linkFont?.Dispose();
        _heroTitle?.Font?.Dispose();
        Image image = _heroIcon?.Image;
        if (_heroIcon != null) _heroIcon.Image = null;
        image?.Dispose();
      }
      base.Dispose(disposing);
    }

    internal static Boolean Supports(GomObject gom) {
      if (gom == null || String.IsNullOrWhiteSpace(gom.Name)) return false;
      String name = gom.Name;
      return name.StartsWith("abl.", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("itm.", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("npc.", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("qst.", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("ach.", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("cdx.", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("sche", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("tal.", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("mpn.", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("plc.", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("cnv.", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("dec.", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("apt.", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("spn.", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("apc.", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("apn.", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("pkg.", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("class.", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("ipp.", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("npp.", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("nco.", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("dyn.", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("epp.", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("hyd.", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("pcs.", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("stg.", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("tbl.", StringComparison.OrdinalIgnoreCase)
        || name.Equals("mtxStorefrontInfoPrototype", StringComparison.OrdinalIgnoreCase)
        || name.Equals("achCategoriesTable_Prototype", StringComparison.OrdinalIgnoreCase)
        || name.Equals("chrCurrencyTablePrototype", StringComparison.OrdinalIgnoreCase)
        || name.Equals("colCollectionCategoriesPrototype", StringComparison.OrdinalIgnoreCase)
        || name.Equals("decorationsPrototype", StringComparison.OrdinalIgnoreCase)
        || name.Equals("mntMountInfoPrototype", StringComparison.OrdinalIgnoreCase)
        || name.Equals("tutTutorialDataTablePrototype", StringComparison.OrdinalIgnoreCase)
        || name.Equals("itmSetTablePrototype", StringComparison.OrdinalIgnoreCase)
        || name.Equals("cnqConquestInfoPrototype", StringComparison.OrdinalIgnoreCase)
        || name.Equals("ablVanityPetsPrototype", StringComparison.OrdinalIgnoreCase)
        || name.Equals("cbtWeaponPerLevelPrototype", StringComparison.OrdinalIgnoreCase)
        || name.Equals("chrFactionPackagesPrototype", StringComparison.OrdinalIgnoreCase)
        || name.Equals("cnqSchedulePrototype", StringComparison.OrdinalIgnoreCase)
        || name.Equals("guiInfoPopupsPrototype", StringComparison.OrdinalIgnoreCase)
        || name.Equals("itmUpgradesPrototype", StringComparison.OrdinalIgnoreCase)
        || name.Equals("lgcPerkPrototypeMap", StringComparison.OrdinalIgnoreCase)
        || name.Equals("lgcSeasonsPrototype", StringComparison.OrdinalIgnoreCase)
        || name.Equals("lgcUnlockPrototypeMap", StringComparison.OrdinalIgnoreCase)
        || name.Equals("lgcVentures_Prototype", StringComparison.OrdinalIgnoreCase)
        || name.Equals("optOptionsDescriptionsPrototype", StringComparison.OrdinalIgnoreCase)
        || name.Equals("pcsSliderDataTablePrototype", StringComparison.OrdinalIgnoreCase)
        || name.Equals("utlTweakablePerLevelInfoPrototype", StringComparison.OrdinalIgnoreCase)
        || name.Equals("wevObjectsPrototype_Client", StringComparison.OrdinalIgnoreCase)
        || name.Equals("MasterComponentMap", StringComparison.OrdinalIgnoreCase)
        || name.Equals("worldMapData", StringComparison.OrdinalIgnoreCase)
        || name.Equals("apthookdata", StringComparison.OrdinalIgnoreCase);
    }

    private void Build(GomObject gom) {
      _tree.BeginUpdate();
      try {
        _tree.Nodes.Clear();
        _createdNodes = 0;
        if (gom == null) return;
        GameObject model = null;
        try { model = GameObject.Load(gom, true); } catch { }
        UpdateHero(gom, model);
        TreeNode root = AddNode(null, gom.Name, gom.Name, gom.Id, Detail("FQN", gom.Name, "Node ID", gom.Id.ToString(), "Base class", gom.DomClass?.Name),
          model as MapNote, ModelPreviewFqnFor(gom, model));
        if (root == null) return;

        switch (model) {
          case Ability ability: BuildAbility(root, ability); break;
          case Item item: BuildItem(root, item); break;
          case Npc npc: BuildNpc(root, npc); break;
          case Quest quest: BuildQuest(root, quest); BuildQuestGomMetadata(root, gom); break;
          case Achievement achievement: BuildAchievement(root, achievement); break;
          case Codex codex: BuildCodex(root, codex); break;
          case Schematic schematic: BuildSchematic(root, schematic); break;
          case Talent talent: BuildTalent(root, talent); break;
          case MapNote mapNote: BuildMapNote(root, mapNote); break;
          case Placeable placeable: BuildPlaceable(root, placeable); break;
          case Conversation conversation: BuildConversation(root, conversation); break;
          case AbilityPackage package: BuildAbilityPackage(root, package); break;
          case Decoration decoration: BuildReflectedDetails(root, decoration, "Decoration details"); break;
          case Stronghold stronghold: BuildReflectedDetails(root, stronghold, "Stronghold details"); break;
          case Spawner spawner: BuildReflectedDetails(root, spawner, "Spawner details"); break;
          case ClassSpec classSpec: BuildReflectedDetails(root, classSpec, "Class / specialization details"); break;
          case ItemAppearance itemAppearance: BuildItemAppearance(root, itemAppearance); break;
          case NpcAppearance npcAppearance: BuildNpcAppearance(root, npcAppearance); break;
          case NewCompanion companion: BuildReflectedDetails(root, companion, "Companion details"); break;
          default:
            if (!BuildJedipediaPrototype(root, gom))
              AddNode(root, "No specialized model", null, 0, Detail("Info", "This node currently has no database-free gameplay specialization."));
            break;
        }
        // Jedipedia's parsed view exposes substantially more than the compact headline fields.
        // Add a bounded semantic property tree for every loaded GomLib model so newly-added client
        // fields become inspectable without waiting for a hand-written viewer for each node class.
        if (model != null && model is not Conversation && model is not AbilityPackage
            && model is not Decoration && model is not Stronghold && model is not Spawner
            && model is not ClassSpec && model is not ItemAppearance && model is not NpcAppearance
            && model is not NewCompanion)
          BuildReflectedDetails(root, model, "Detailed parsed fields");

        // Jedipedia also keeps the complete decoded GOM structure next to its hand-written view.
        // Preserve that capability here for fields that are newer than GomLib's typed models. The
        // tree remains bounded by MaxTreeNodes and collection limits in AddGomValue.
        if (model != null && gom.Data != null && _createdNodes < MaxTreeNodes) {
          TreeNode raw = AddNode(root, "Complete parsed GOM fields", null, 0,
            Detail("Top-level fields", gom.Data.Dictionary.Count.ToString(), "Depth", "5 (bounded)"));
          AddGomDataMembers(raw, gom.Data, 0, 5);
        }
        BuildGameplayGraph(gom, model);
        // Jedipedia opens on its parsed details. Keep the relationship graph one click away but
        // make the detailed structured view the default for every supported node.
        _tabs.SelectedIndex = 0;
        root.Expand();
        if (root.Nodes.Count == 1) root.Nodes[0].Expand();
        _tree.SelectedNode = root;
        _status.Text = _createdNodes.ToString("N0") + " entries" + (_createdNodes >= MaxTreeNodes ? " (display capped)" : String.Empty);
      } finally {
        _tree.EndUpdate();
      }
    }

    private Boolean BuildJedipediaPrototype(TreeNode root, GomObject gom) {
      if (root == null || gom?.Data == null) return false;
      String table = null;
      String title = null;
      switch (gom.Name) {
        case "mtxStorefrontInfoPrototype": table = "mtxStorefrontItems"; title = "Cartel Market storefront"; break;
        case "achCategoriesTable_Prototype": table = "achCategoriesTableRowMap"; title = "Achievement categories"; break;
        case "chrCurrencyTablePrototype": table = "chrCurrencyData"; title = "Currencies"; break;
        case "colCollectionCategoriesPrototype": table = "colCollectionIdToCategory"; title = "Collections categories"; break;
        case "decorationsPrototype": table = "decDecorationsById"; title = "Decoration registry"; break;
        case "mntMountInfoPrototype": table = "mntIdToDataMap"; title = "Mount registry"; break;
        case "tutTutorialDataTablePrototype": table = "tutTutorialDefinitionLookupList"; title = "Tutorial definitions"; break;
        case "itmSetTablePrototype": table = "itmSetTablePackageMap"; title = "Item sets / packages"; break;
        case "cnqConquestInfoPrototype": table = "cnqConquestInfoMap"; title = "Galactic Conquests"; break;
        default:
          if (Supports(gom)) {
            String label = gom.Name.StartsWith("dyn.", StringComparison.OrdinalIgnoreCase)
              ? "Dynamic object parsed data"
              : "Jedipedia-style parsed GOM data";
            TreeNode parsed = AddNode(root, label, null, 0,
              Detail("Fields", gom.Data.Dictionary.Count.ToString(), "Base class", gom.DomClass?.Name));
            AddGomDataMembers(parsed, gom.Data, 0, 5);
            return true;
          }
          return false;
      }

      Dictionary<Object, Object> map = gom.Data.ValueOrDefault<Dictionary<Object, Object>>(table, null);
      if (map == null) {
        TreeNode missing = AddNode(root, title, null, 0, Detail("Table", table, "Status", "Field not present in this client build"));
        AddGomDataMembers(missing, gom.Data, 0, 2);
        return true;
      }
      TreeNode group = AddNode(root, title, null, 0, Detail("Table", table, "Entries", map.Count.ToString()));
      Int32 index = 0;
      foreach (KeyValuePair<Object, Object> entry in map) {
        if (++index > 600 || _createdNodes >= MaxTreeNodes) {
          AddNode(group, "… " + Math.Max(0, map.Count - index + 1).ToString("N0") + " more entries", null, 0, null);
          break;
        }
        UInt64 id = TryUInt64(entry.Key);
        String target = id == 0 ? null : ResolveFqn(id);
        GomObjectData data = entry.Value as GomObjectData;
        String label = entry.Key?.ToString() ?? "entry";
        List<KeyValuePair<String, String>> summary = Detail("Key", label);
        if (data != null && gom.Name == "mtxStorefrontInfoPrototype") {
          Int64 nameId = data.ValueOrDefault<Int64>("mtxStorefrontItemDisplayName", 0);
          String display = nameId == 0 ? null : _dom.StringTable.TryGetString("str.gui.mtxstorefrontitems", nameId);
          String image = data.ValueOrDefault("mtxStorefrontItemImage", "");
          Int64 cost = data.ValueOrDefault<Int64>("mtxStorefrontItemCost", 0);
          if (!String.IsNullOrWhiteSpace(display)) label += " — " + OneLine(display);
          summary.Add(new KeyValuePair<String, String>("Name", display));
          summary.Add(new KeyValuePair<String, String>("Name string ID", nameId == 0 ? null : nameId.ToString()));
          summary.Add(new KeyValuePair<String, String>("Image", image));
          summary.Add(new KeyValuePair<String, String>("Cost", cost == 0 ? null : cost.ToString()));
          summary.Add(new KeyValuePair<String, String>("Active", YesNo(data.ValueOrDefault("mtxStorefrontItemIsActive", false))));
          summary.Add(new KeyValuePair<String, String>("On sale", YesNo(data.ValueOrDefault("mtxStorefrontItemIsOnSale", false))));
        }
        TreeNode row = AddNode(group, label, target, id, summary);
        if (data != null) AddGomDataMembers(row, data, 0, 3);
        else AddGomValue(row, "Value", entry.Value, 0, 3);
      }
      return true;
    }

    private void AddGomDataMembers(TreeNode parent, GomObjectData data, Int32 depth, Int32 maxDepth) {
      if (parent == null || data == null || depth > maxDepth) return;
      foreach (KeyValuePair<String, Object> field in data.Dictionary.OrderBy(x => x.Key)) {
        if (_createdNodes >= MaxTreeNodes) return;
        AddGomValue(parent, field.Key, field.Value, depth, maxDepth);
      }
    }

    private void AddGomValue(TreeNode parent, String name, Object value, Int32 depth, Int32 maxDepth) {
      if (parent == null || _createdNodes >= MaxTreeNodes) return;
      if (value == null) { AddNode(parent, name + ": null", null, 0, null); return; }
      if (value is GomObjectData child) {
        TreeNode n = AddNode(parent, name, null, 0, Detail("Type", "structure", "Fields", child.Dictionary.Count.ToString()));
        if (depth < maxDepth) AddGomDataMembers(n, child, depth + 1, maxDepth);
        return;
      }
      if (value is IDictionary dictionary) {
        TreeNode n = AddNode(parent, name + " (" + dictionary.Count + ")", null, 0, Detail("Count", dictionary.Count.ToString()));
        if (depth >= maxDepth) return;
        Int32 count = 0;
        foreach (DictionaryEntry entry in dictionary) {
          if (++count > 200 || _createdNodes >= MaxTreeNodes) break;
          AddGomValue(n, Convert.ToString(entry.Key), entry.Value, depth + 1, maxDepth);
        }
        return;
      }
      if (value is IEnumerable enumerable && value is not String) {
        TreeNode n = AddNode(parent, name, null, 0, null);
        if (depth >= maxDepth) return;
        Int32 count = 0;
        foreach (Object item in enumerable) {
          if (++count > 200 || _createdNodes >= MaxTreeNodes) break;
          AddGomValue(n, "[" + (count - 1) + "]", item, depth + 1, maxDepth);
        }
        return;
      }
      UInt64 id = TryUInt64(value);
      String target = id == 0 ? null : ResolveFqn(id);
      String text = FormatReflectedValue(value);
      AddNode(parent, name + ": " + text, target, target == null ? 0 : id, Detail(name, text, "Resolved node", target));
    }

    private static UInt64 TryUInt64(Object value) {
      if (value == null) return 0;
      try {
        if (value is Int64 signed && signed < 0) return 0;
        return Convert.ToUInt64(value);
      } catch { return 0; }
    }

    private void BuildConversation(TreeNode root, Conversation conversation) {
      TreeNode overview = AddNode(root, "Conversation", conversation.Fqn, conversation.Id, Detail(
        "STB", conversation.Stb, "KOTOR style", YesNo(conversation.IsKOTORStyle),
        "Default speaker", conversation.DefaultSpeaker?.Fqn ?? ResolveFqn(conversation.DefaultSpeakerId),
        "Speakers", (conversation.SpeakersIds?.Count ?? 0).ToString(),
        "Dialog nodes", (conversation.DialogNodes?.Count ?? 0).ToString()));
      foreach (UInt64 id in conversation.SpeakersIds ?? new List<UInt64>()) AddId(overview, "Speaker", id);

      TreeNode effects = AddNode(root, "Quest effects", null, 0, null);
      foreach (UInt64 id in conversation.QuestStarted ?? new List<UInt64>()) AddId(effects, "Starts quest", id);
      foreach (UInt64 id in conversation.QuestProgressed ?? new List<UInt64>()) AddId(effects, "Progresses quest", id);
      foreach (UInt64 id in conversation.QuestEnded ?? new List<UInt64>()) AddId(effects, "Ends quest", id);

      TreeNode graph = AddNode(root, "Dialog graph", null, 0, Detail(
        "Root nodes", (conversation.RootNodes?.Count ?? 0).ToString(),
        "Links", (conversation.NodeLinkList?.Count ?? 0).ToString()));
      HashSet<Int64> roots = new HashSet<Int64>((conversation.RootNodes ?? new Dictionary<Int32, Int64>()).Values);
      foreach (DialogNode dialog in conversation.DialogNodes ?? new List<DialogNode>()) {
        if (dialog == null || _createdNodes >= MaxTreeNodes) continue;
        String label = (roots.Contains(dialog.NodeId) ? "Root " : "Node ") + dialog.NodeId;
        String text = Localized(dialog.LocalizedText, dialog.Text);
        if (!String.IsNullOrWhiteSpace(text)) label += " — " + OneLine(text);
        TreeNode node = AddNode(graph, label, null, 0, Detail(
          "Node ID", dialog.NodeId.ToString(), "Player node", YesNo(dialog.IsPlayerNode),
          "Speaker", ResolveFqn(dialog.SpeakerId), "Text", text,
          "Option text", Localized(dialog.LocalizedOptionText, null),
          "Min level", dialog.MinLevel.ToString(), "Max level", dialog.MaxLevel.ToString(),
          "Alignment", dialog.AlignmentGain.ToString(), "Credits", dialog.CreditsGained.ToString(),
          "Ambient", YesNo(dialog.IsAmbient), "Aborts conversation", YesNo(dialog.AbortsConversation),
          "Action hook", dialog.ActionHook));
        foreach (UInt64 id in dialog.QuestsGranted ?? new List<UInt64>()) AddId(node, "Grants quest", id);
        foreach (UInt64 id in dialog.QuestsProgressed ?? new List<UInt64>()) AddId(node, "Progresses quest", id);
        foreach (UInt64 id in dialog.QuestsEnded ?? new List<UInt64>()) AddId(node, "Ends quest", id);
        if (dialog.QuestReward != 0) AddId(node, "Quest reward", dialog.QuestReward);
        if (dialog.ActionQuest != 0) AddId(node, "Action quest", dialog.ActionQuest);
        if (dialog.ChildIds != null && dialog.ChildIds.Count > 0)
          AddNode(node, "Children: " + String.Join(", ", dialog.ChildIds), null, 0, Detail("Child node IDs", String.Join(", ", dialog.ChildIds)));
      }
      BuildReflectedDetails(root, conversation, "All conversation fields", 2);
    }


    private void BuildItemAppearance(TreeNode root, ItemAppearance appearance) {
      TreeNode group = AddNode(root, "Item appearance (IPP)", appearance.Fqn, appearance.Id, Detail(
        "Color scheme", appearance.ColorScheme.ToString(), "VO sound override", appearance.VOSoundTypeOverride));
      if (appearance.IPP != null) AddAppearanceSlot(group, "Appearance slot", appearance.IPP);
      BuildReflectedDetails(root, appearance, "All IPP fields", 3);
    }

    private void BuildNpcAppearance(TreeNode root, NpcAppearance appearance) {
      TreeNode group = AddNode(root, "NPC appearance (NPP)", appearance.Fqn, appearance.Id, Detail(
        "Body type", appearance.BodyType, "NPP type", appearance.NppType,
        "Sound package", appearance.SoundPackage, "Armor sound override", appearance.ArmorSoundsetOverride,
        "Slot groups", (appearance.AppearanceSlotMap?.Count ?? 0).ToString()));
      if (appearance.VocalSoundsetOverride != null && appearance.VocalSoundsetOverride.Count > 0) {
        TreeNode voices = AddNode(group, "Vocal sound overrides", null, 0, Detail("Count", appearance.VocalSoundsetOverride.Count.ToString()));
        foreach (KeyValuePair<Int64, String> voice in appearance.VocalSoundsetOverride.OrderBy(x => x.Key))
          AddNode(voices, voice.Key + " — " + voice.Value, null, 0, Detail("Key", voice.Key.ToString(), "Soundset", voice.Value));
      }
      if (appearance.AppearanceSlotMap != null) {
        foreach (KeyValuePair<String, List<AppSlot>> slotGroup in appearance.AppearanceSlotMap.OrderBy(x => x.Key)) {
          TreeNode slots = AddNode(group, slotGroup.Key + " (" + (slotGroup.Value?.Count ?? 0) + ")", null, 0, null);
          Int32 index = 0;
          foreach (AppSlot slot in slotGroup.Value ?? new List<AppSlot>()) AddAppearanceSlot(slots, "Choice " + (++index), slot);
        }
      }
      BuildReflectedDetails(root, appearance, "All NPP fields", 3);
    }

    private void AddAppearanceSlot(TreeNode parent, String label, AppSlot slot) {
      if (parent == null || slot == null) return;
      String type = null, model = null, material = null, mirror = null, primaryHue = null, secondaryHue = null;
      try { type = slot.Type; } catch { }
      try { model = slot.Model; } catch { }
      try { material = slot.Material0; } catch { }
      try { mirror = slot.MaterialMirror; } catch { }
      try { primaryHue = slot.PrimaryHue; } catch { }
      try { secondaryHue = slot.SecondaryHue; } catch { }
      TreeNode node = AddNode(parent, label + (String.IsNullOrWhiteSpace(type) ? String.Empty : " — " + type), null, 0, Detail(
        "Body type", slot.BodyType, "Slot type", type, "Model ID", slot.ModelID.ToString(), "Model", model,
        "Material index", slot.MaterialIndex.ToString(), "Material", material, "Mirror material", mirror,
        "Primary hue ID", slot.PrimaryHueId.ToString(), "Primary hue", primaryHue,
        "Secondary hue ID", slot.SecondaryHueId.ToString(), "Secondary hue", secondaryHue,
        "Random weight", slot.RandomWeight.ToString()));
      List<Int64> attachments = slot.Attachments ?? new List<Int64>();
      List<String> attachedModels = null;
      try { attachedModels = slot.AttachedModels; } catch { }
      if (attachments.Count > 0) {
        TreeNode attached = AddNode(node, "Attachments", null, 0, Detail("Count", attachments.Count.ToString()));
        for (Int32 i = 0; i < attachments.Count; i++) {
          String attachedModel = attachedModels != null && i < attachedModels.Count ? attachedModels[i] : null;
          AddNode(attached, attachments[i] + (String.IsNullOrWhiteSpace(attachedModel) ? String.Empty : " — " + attachedModel), null, 0,
            Detail("Attachment ID", attachments[i].ToString(), "Model", attachedModel));
        }
      }
    }

    private void BuildAbilityPackage(TreeNode root, AbilityPackage package) {
      TreeNode abilities = AddNode(root, "Package abilities", null, 0, Detail(
        "Utility package", YesNo(package.IsUtilityPackage), "Count", (package.PackageAbilities?.Count ?? 0).ToString()));
      foreach (PackageAbility entry in package.PackageAbilities ?? new List<PackageAbility>()) {
        if (entry == null) continue;
        Ability ability = null;
        try { ability = entry.Ability; } catch { }
        AddNode(abilities, DisplayName(ability, "Ability"), ability?.Fqn, ability?.Id ?? 0, Detail(
          "Level", entry.Level.ToString(), "Levels", entry.Levels == null ? null : String.Join(", ", entry.Levels),
          "Auto acquire", YesNo(entry.AutoAcquire), "Scales", entry.Scales.ToString(),
          "Utility tier", entry.UtilityTier.ToString(), "Utility position", entry.UtilityPosition.ToString(),
          "Attack wave", entry.AttackWaves == null || entry.AttackWaves.Count == 0 ? null : String.Join(", ", entry.AttackWaves)));
      }
      TreeNode talents = AddNode(root, "Package talents", null, 0, Detail("Count", (package.PackageTalents?.Count ?? 0).ToString()));
      foreach (PackageTalent entry in package.PackageTalents ?? new List<PackageTalent>()) {
        if (entry == null) continue;
        Talent talent = null;
        try { talent = entry.Talent; } catch { }
        AddNode(talents, DisplayName(talent, "Talent"), talent?.Fqn, talent?.Id ?? 0,
          Detail("Utility tier", entry.UtilityTier.ToString(), "Utility position", entry.UtilityPosition.ToString()));
      }
      BuildReflectedDetails(root, package, "All package fields", 2);
    }

    private void BuildReflectedDetails(TreeNode root, Object model, String title, Int32 maxDepth = 3) {
      if (root == null || model == null || _createdNodes >= MaxTreeNodes) return;
      TreeNode group = AddNode(root, title, null, 0, Detail("Type", model.GetType().Name));
      AddReflectedMembers(group, model, 0, maxDepth, new HashSet<Object>(ReferenceEqualityComparer.Instance));
    }

    private void AddReflectedMembers(TreeNode parent, Object value, Int32 depth, Int32 maxDepth, HashSet<Object> visited) {
      if (parent == null || value == null || depth > maxDepth || _createdNodes >= MaxTreeNodes) return;
      Type type = value.GetType();
      if (!type.IsValueType && value is not String && !visited.Add(value)) return;
      foreach (PropertyInfo property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public).OrderBy(x => x.Name)) {
        if (!property.CanRead || property.GetIndexParameters().Length != 0) continue;
        if (property.Name is "Dom_" or "Dom" or "SQLProperties" or "References") continue;
        Object prop;
        try { prop = property.GetValue(value); } catch { continue; }
        if (prop == null) continue;
        if (IsSimpleValue(prop)) {
          AddNode(parent, property.Name + ": " + FormatReflectedValue(prop), null, 0, Detail(property.Name, FormatReflectedValue(prop)));
          continue;
        }
        if (prop is GameObject go) {
          AddGameObject(parent, property.Name + " — " + DisplayName(go, go.Fqn), go);
          continue;
        }
        if (prop is IDictionary dictionary) {
          TreeNode map = AddNode(parent, property.Name + " (" + dictionary.Count + ")", null, 0, Detail("Count", dictionary.Count.ToString()));
          Int32 count = 0;
          foreach (DictionaryEntry entry in dictionary) {
            if (++count > 250 || _createdNodes >= MaxTreeNodes) break;
            AddReflectedEntry(map, Convert.ToString(entry.Key), entry.Value, depth + 1, maxDepth, visited);
          }
          continue;
        }
        if (prop is IEnumerable enumerable && prop is not String) {
          TreeNode list = AddNode(parent, property.Name, null, 0, null);
          Int32 count = 0;
          foreach (Object item in enumerable) {
            if (++count > 250 || _createdNodes >= MaxTreeNodes) break;
            AddReflectedEntry(list, "[" + (count - 1) + "]", item, depth + 1, maxDepth, visited);
          }
          continue;
        }
        if (depth < maxDepth) {
          TreeNode nested = AddNode(parent, property.Name, null, 0, Detail("Type", prop.GetType().Name));
          AddReflectedMembers(nested, prop, depth + 1, maxDepth, visited);
        }
      }
    }

    private void AddReflectedEntry(TreeNode parent, String label, Object value, Int32 depth, Int32 maxDepth, HashSet<Object> visited) {
      if (value == null) { AddNode(parent, label + ": null", null, 0, null); return; }
      if (IsSimpleValue(value)) { AddNode(parent, label + ": " + FormatReflectedValue(value), null, 0, Detail(label, FormatReflectedValue(value))); return; }
      if (value is GameObject go) { AddGameObject(parent, label + " — " + DisplayName(go, go.Fqn), go); return; }
      TreeNode node = AddNode(parent, label, null, 0, Detail("Type", value.GetType().Name));
      if (depth <= maxDepth) AddReflectedMembers(node, value, depth, maxDepth, visited);
    }

    private static Boolean IsSimpleValue(Object value) {
      if (value == null) return true;
      Type type = value.GetType();
      return type.IsPrimitive || type.IsEnum || value is String || value is Decimal || value is DateTime || value is Guid || value is ScriptEnum;
    }

    private static String FormatReflectedValue(Object value) {
      if (value == null) return String.Empty;
      String text = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? String.Empty;
      return text.Length <= 1200 ? text : text.Substring(0, 1200) + "…";
    }

    private sealed class ReferenceEqualityComparer : IEqualityComparer<Object> {
      internal static readonly ReferenceEqualityComparer Instance = new ReferenceEqualityComparer();
      public new Boolean Equals(Object x, Object y) { return ReferenceEquals(x, y); }
      public Int32 GetHashCode(Object obj) { return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj); }
    }

    private void BuildAbility(TreeNode root, Ability ability) {
      AddNode(root, "Ability", null, 0, Detail(
        "Level", ability.Level.ToString(), "Passive", YesNo(ability.IsPassive), "Hidden", YesNo(ability.IsHidden),
        "Cooldown", Seconds(ability.Cooldown), "Cast time", Seconds(ability.CastingTime), "Channel time", Seconds(ability.ChannelingTime),
        "Range", ability.MinRange + " - " + ability.MaxRange, "GCD", Seconds(ability.GCD), "Target rule", ability.TargetRule.ToString(),
        "Line of sight", YesNo(ability.LineOfSightCheck), "Ignore alacrity", YesNo(ability.IgnoreAlacrity)));

      List<Effect> effects = new List<Effect>();
      foreach (UInt64 id in ability.EffectIds ?? new List<UInt64>()) {
        try {
          Effect effect = _dom.EffectLoader.Load(id);
          if (effect != null) effects.Add(effect);
        } catch { }
      }
      if (effects.Count == 0) return;
      Dictionary<Int64, Effect> byNumber = effects.GroupBy(x => x.Number).ToDictionary(x => x.Key, x => x.First());
      HashSet<Int64> called = new HashSet<Int64>(effects.SelectMany(x => x.ChildEffects ?? new List<Int64>()));

      TreeNode flow = AddNode(root, "Effect flow", null, 0, Detail("Effects", effects.Count.ToString(), "Root effects", effects.Count(x => x.IsRootEffect).ToString()));
      HashSet<Int64> path = new HashSet<Int64>();
      foreach (Effect effect in effects.Where(x => x.IsRootEffect || !called.Contains(x.Number)).OrderBy(x => x.Number))
        AddEffectFlow(flow, effect, byNumber, path, 0);
      foreach (Effect effect in effects.OrderBy(x => x.Number)) {
        if (!flow.Nodes.Cast<TreeNode>().Any(x => Entry(x)?.TargetFqn == effect.Fqn)) AddEffectFlow(flow, effect, byNumber, path, 0);
      }

      TreeNode definitions = AddNode(root, "Effect definitions", null, 0, Detail("Count", effects.Count.ToString()));
      foreach (Effect effect in effects.OrderBy(x => x.Number)) AddEffectDefinition(definitions, effect);
    }

    private void AddEffectFlow(TreeNode parent, Effect effect, Dictionary<Int64, Effect> byNumber, HashSet<Int64> path, Int32 depth) {
      if (effect == null || depth > 18 || _createdNodes >= MaxTreeNodes) return;
      String label = "#" + effect.Number + " " + EffectDisplayName(effect);
      TreeNode node = AddNode(parent, label, effect.Fqn, effect.Id, EffectDetails(effect));
      if (node == null) return;
      if (!path.Add(effect.Number)) {
        AddNode(node, "↻ cycle", effect.Fqn, effect.Id, Detail("Effect", effect.Number.ToString()));
        return;
      }
      foreach (Int64 childNo in effect.ChildEffects ?? new List<Int64>()) {
        if (byNumber.TryGetValue(childNo, out Effect child)) AddEffectFlow(node, child, byNumber, path, depth + 1);
        else AddNode(node, "Call effect #" + childNo, null, 0, Detail("Effect number", childNo.ToString(), "Status", "Not present in this ability's loaded EffectIds"));
      }
      path.Remove(effect.Number);
    }

    private void AddEffectDefinition(TreeNode parent, Effect effect) {
      TreeNode node = AddNode(parent, "#" + effect.Number + " " + EffectDisplayName(effect), effect.Fqn, effect.Id, EffectDetails(effect));
      if (node == null) return;
      Int32 subIndex = 0;
      foreach (SubEffect sub in effect.SubEffects ?? new List<SubEffect>()) {
        subIndex++;
        TreeNode subNode = AddNode(node, "Subeffect " + subIndex, null, 0,
          Detail("Conditions", CountItems(sub.Conditions), "Initializers", CountItems(sub.Initializers), "Actions", CountItems(sub.Actions),
                 "Triggers", CountItems(sub.Triggers), "Target overrides", CountItems(sub.TargetOverrides)));
        AddFunctions(subNode, "Conditions", sub.Conditions, EffectFunctionNames.Condition);
        AddFunctions(subNode, "Initializers", sub.Initializers, EffectFunctionNames.Initializer);
        AddFunctions(subNode, "Actions", sub.Actions, EffectFunctionNames.Action);
        AddFunctions(subNode, "Triggers", sub.Triggers, EffectFunctionNames.Trigger);
        AddFunctions(subNode, "Target overrides", sub.TargetOverrides, EffectFunctionNames.TargetOverride);
      }
      if (effect.SubEffectEppDetails != null && effect.SubEffectEppDetails.Count > 0) {
        TreeNode epps = AddNode(node, "EPP links", null, 0, Detail("Count", effect.SubEffectEppDetails.Count.ToString()));
        foreach (SubEffectEppDetail epp in effect.SubEffectEppDetails) {
          String target = epp.EppId != 0 ? ResolveFqn(epp.EppId) : null;
          AddNode(epps, epp.EppSpec ?? target ?? "EPP", target, epp.EppId,
            Detail("Index", epp.Index.ToString(), "Dependent", YesNo(epp.Dependent), "On apply", YesNo(epp.OnApply), "Spec", epp.EppSpec));
        }
      }
    }

    private void AddFunctions(TreeNode parent, String title, List<SubEffectFunction> functions, Func<Int64, String> nameResolver) {
      if (functions == null || functions.Count == 0 || parent == null) return;
      TreeNode group = AddNode(parent, title + " (" + functions.Count + ")", null, 0, null);
      Int32 index = 0;
      foreach (SubEffectFunction function in functions) {
        if (function == null) continue;
        String name = nameResolver(function.Type);
        String target = TryResolveFunctionTarget(function);
        List<KeyValuePair<String, String>> details = Detail("Type", function.Type.ToString(), "Name", name);
        if (function.CondId != 0) details.Add(new KeyValuePair<String, String>("Condition ID", function.CondId.ToString()));
        if (!String.IsNullOrWhiteSpace(function.FailureString)) details.Add(new KeyValuePair<String, String>("Failure", function.FailureString));
        if (function.ParsedTags != null && function.ParsedTags.Count > 0) details.Add(new KeyValuePair<String, String>("Tags", String.Join(", ", function.ParsedTags)));
        if (function.Params != null) {
          foreach (SubEffectFunctionParam param in function.Params.Take(80))
            details.Add(new KeyValuePair<String, String>("Param " + ParamName(param) + " (" + ParamType(param.Type) + ")", FormatParamValue(param)));
        }
        AddNode(group, (++index) + ". " + name, target, TargetId(target), details);
      }
    }

    private String TryResolveFunctionTarget(SubEffectFunction function) {
      if (function?.Params == null) return null;
      foreach (SubEffectFunctionParam param in function.Params) {
        if (param.Value is UInt64 u && u != 0) {
          String fqn = ResolveFqn(u);
          if (!String.IsNullOrWhiteSpace(fqn)) return fqn;
        }
        if (param.Value is Int64 l && l > 0) {
          String fqn = ResolveFqn(unchecked((UInt64)l));
          if (!String.IsNullOrWhiteSpace(fqn)) return fqn;
        }
        if (param.Value is String s && LooksLikeFqn(s) && _dom.GetObject(s) != null) return s;
      }
      return null;
    }

    private void BuildItem(TreeNode root, Item item) {
      TreeNode abilities = AddNode(root, "Abilities", null, 0, null);
      AddGameObject(abilities, "Use ability", item.UseAbility);
      AddGameObject(abilities, "Equip ability", item.EquipAbility);

      if (item.EnhancementSlots != null && item.EnhancementSlots.Count > 0) {
        TreeNode mods = AddNode(root, "Modifications", null, 0, Detail("Slots", item.EnhancementSlots.Count.ToString()));
        foreach (ItemEnhancement enhancement in item.EnhancementSlots) {
          if (enhancement == null) continue;
          Item modification = null;
          try { modification = enhancement.Modification ?? _dom.ItemLoader.Load(enhancement.ModificationId); } catch { }
          AddNode(mods, enhancement.Slot + " — " + DisplayName(modification, ResolveFqn(enhancement.ModificationId)),
            modification?.Fqn ?? ResolveFqn(enhancement.ModificationId), enhancement.ModificationId,
            Detail("Slot", enhancement.Slot.ToString(), "Detailed slot", enhancement.DetailedSlot.ToString(), "Item ID", enhancement.ModificationId.ToString()));
        }
      }

      if (item.SetBonusId != 0) {
        TreeNode setRoot = AddNode(root, "Set bonus", null, 0, Detail("Set ID", item.SetBonusId.ToString()));
        try {
          SetBonusEntry set = item.SetBonus;
          if (set != null) {
            if (set.BonusAbilityByNum != null) foreach (KeyValuePair<Int64, Ability> bonus in set.BonusAbilityByNum.OrderBy(x => x.Key))
              AddGameObject(setRoot, bonus.Key + " pieces — " + DisplayName(bonus.Value, null), bonus.Value);
            if (set.Sources != null) foreach (Item source in set.Sources.Take(80)) AddGameObject(setRoot, "Set item — " + DisplayName(source, null), source);
          }
        } catch { }
      }

      if (item.SchematicId != 0) {
        Schematic schematic = null;
        try { schematic = item.Schematic; } catch { }
        AddGameObject(root, "Schematic — " + DisplayName(schematic, ResolveFqn(item.SchematicId)), schematic,
          item.SchematicId, ResolveFqn(item.SchematicId));
      }
      AddFqn(root, "Conversation", item.ConversationFqn);
      AddFqn(root, "Imperial appearance", item.AppearanceImperial ?? item.ImperialAppearanceTag);
      AddFqn(root, "Republic appearance", item.AppearanceRepublic ?? item.RepublicAppearanceTag);

      TreeNode req = AddNode(root, "Requirements", null, 0, Detail(
        "Required level", Math.Max(item.RequiredLevel, item.CombinedRequiredLevel).ToString(),
        "Classes", item.RequiredClasses == null ? null : String.Join(", ", item.RequiredClasses.Select(x => x.Name)),
        "Profession", item.RequiredProfession == Profession.None ? null : item.RequiredProfession + " " + item.RequiredProfessionLevel,
        "Valor", item.RequiredValorRank.ToString(), "Reputation", item.RequiredReputationName,
        "Reputation rank", item.RequiredReputationLevelName));
      if (req != null && req.Tag == null && req.Nodes.Count == 0 && Entry(req)?.Details.All(x => String.IsNullOrWhiteSpace(x.Value)) == true) req.Remove();
    }

    private void BuildNpc(TreeNode root, Npc npc) {
      TreeNode combat = AddNode(root, "Combat / packages", null, 0, Detail(
        "Level", npc.MinLevel == npc.MaxLevel ? npc.MinLevel.ToString() : npc.MinLevel + " - " + npc.MaxLevel,
        "Faction", npc.Faction.ToString(), "Toughness", npc.Toughness.ToString(), "Class", npc.ClassSpec?.Name,
        "Movement", npc.MovementPackage, "Cover", npc.CoverPackage, "Wander", npc.WanderPackage, "Aggro", npc.AggroPackage));
      if (npc.ClassSpec != null) AddGameObject(combat, "Class — " + npc.ClassSpec.Name, npc.ClassSpec);
      foreach (UInt64 id in npc.AbilityPackageIdList ?? new HashSet<UInt64>()) AddId(combat, "Ability package", id);

      TreeNode interactions = AddNode(root, "Interactions", null, 0, Detail("Vendor", YesNo(npc.IsVendor), "Class trainer", YesNo(npc.IsClassTrainer), "Profession", npc.ProfessionTrained.ToString()));
      AddFqn(interactions, "Conversation", npc.CnvConversationName);
      AddId(interactions, "Codex", npc.CodexId);
      if (npc.LootTableId > 0) AddId(interactions, "Loot table", unchecked((UInt64)npc.LootTableId));
      foreach (String vendor in npc.VendorPackages ?? new List<String>()) AddFqn(interactions, "Vendor package", vendor);

      if (npc.VisualDataList != null && npc.VisualDataList.Count > 0) {
        TreeNode visuals = AddNode(root, "Visuals", null, 0, Detail("Variants", npc.VisualDataList.Count.ToString()));
        Int32 index = 0;
        foreach (NpcVisualData visual in npc.VisualDataList) {
          if (visual == null) continue;
          TreeNode v = AddNode(visuals, "Visual " + (++index), visual.AppearanceFqn, visual.AppearanceId,
            Detail("Appearance", visual.AppearanceFqn, "Char spec", visual.CharSpec, "Scale", visual.ScaleAdjustment.ToString("0.###"), "Species scale", visual.SpeciesScale.ToString()));
          AddId(v, "Melee weapon", visual.MeleeWepId);
          AddId(v, "Melee offhand", visual.MeleeOffWepId);
          AddId(v, "Ranged weapon", visual.RangedWepId);
          AddId(v, "Ranged offhand", visual.RangedOffWepId);
        }
      }
    }

    private void BuildQuest(TreeNode root, Quest quest) {
      TreeNode sequence = AddNode(root, "Quest sequence", null, 0, null);
      foreach (String id in quest.QuestsPreviousB62 ?? new List<String>()) AddBase62(sequence, "Previous quest", id);
      foreach (String id in quest.QuestsNextB62 ?? new List<String>()) AddBase62(sequence, "Next quest", id);

      IEnumerable branches = ReflectedEnumerable(quest, "Branches");
      TreeNode journal = AddNode(root, "Journal flow", null, 0, null);
      Int32 branchNo = 0;
      if (branches != null) foreach (Object raw in branches) {
        if (raw is not QuestBranch branch) continue;
        TreeNode branchNode = AddNode(journal, "Branch " + (++branchNo) + " — " + branch.Id, null, 0, Detail("Branch ID", branch.Id.ToString()));
        Int32 stepNo = 0;
        foreach (QuestStep step in branch.Steps ?? new List<QuestStep>()) {
          String journalText = Localized(step.LocalizedJournalText, step.JournalText);
          TreeNode stepNode = AddNode(branchNode, "Step " + (++stepNo) + (String.IsNullOrWhiteSpace(journalText) ? String.Empty : " — " + OneLine(journalText)), null, 0,
            Detail("Journal", journalText, "Shareable", YesNo(step.IsShareable),
              "Failure timer", step.FailTime > 0 ? step.FailTime + " s" : null, "Timer hidden", YesNo(step.HideTimer)));
          Int32 taskNo = 0;
          foreach (QuestTask task in step.Tasks ?? new List<QuestTask>()) {
            String text = Localized(task.LocalizedString, task.Text);
            TreeNode taskNode = AddNode(stepNode, "Task " + (++taskNo) + (String.IsNullOrWhiteSpace(text) ? String.Empty : " — " + OneLine(text)), null, 0,
              Detail("Text", text, "String ID", task.StringId == 0 ? null : task.StringId.ToString(),
                "Hook", task.Hook, "Stable hook ID", task.HookId == 0 ? null : task.HookId.ToString(),
                "Hook flags", task.HookFlags == 0 ? null : task.HookFlags.ToString(),
                "Banner hidden", YesNo(task.HideBannerText),
                "Count", task.ShowCount ? task.CountMax.ToString() : null, "Tracking", YesNo(task.ShowTracking)));
            foreach (UInt64 id in task.TaskNpcIds ?? new List<UInt64>()) AddId(taskNode, "NPC", id);
            foreach (UInt64 id in task.TaskPlcIds ?? new List<UInt64>()) AddId(taskNode, "Placeable", id);
            foreach (UInt64 id in task.TaskQuestIds ?? new List<UInt64>()) AddId(taskNode, "Quest", id);
            foreach (String fqn in task.MapNoteFqnList ?? new List<String>()) AddMapNoteFqn(taskNode, "Map note", fqn);
            foreach (UInt64 id in task.BonusMissionsIds ?? new List<UInt64>()) AddId(taskNode, "Bonus mission", id);
            AddQuestItems(taskNode, "Gives", task.ItemsGiven);
            AddQuestItems(taskNode, "Takes", task.ItemsTaken);
          }
          foreach (UInt64 id in step.BonusMissionsIds ?? new List<UInt64>()) AddId(stepNode, "Bonus mission", id);
          AddQuestItems(stepNode, "Gives", step.ItemsGiven);
          AddQuestItems(stepNode, "Takes", step.ItemsTaken);
        }
        foreach (Item item in branch.RewardAll ?? new List<Item>()) AddGameObject(branchNode, "Reward (all) — " + DisplayName(item, null), item);
        foreach (Item item in branch.RewardOne ?? new List<Item>()) AddGameObject(branchNode, "Reward (choose) — " + DisplayName(item, null), item);
      }

      IEnumerable rewards = ReflectedEnumerable(quest, "Rewards");
      TreeNode rewardRoot = AddNode(root, "Quest rewards", null, 0, null);
      if (rewards != null) foreach (Object raw in rewards) {
        if (raw is not QuestReward reward || reward.RewardItemId == 0) continue;
        String fqn = ResolveFqn(reward.RewardItemId);
        Item item = null;
        try { item = reward.RewardItem; } catch { }
        AddNode(rewardRoot, (reward.NumberOfItem > 1 ? reward.NumberOfItem + "× " : String.Empty) + DisplayName(item, fqn),
          item?.Fqn ?? fqn, reward.RewardItemId,
          Detail("Always provided", YesNo(reward.IsAlwaysProvided), "Min level", reward.MinLevel.ToString(), "Max level", reward.MaxLevel.ToString(),
                 "Classes", reward.Classes == null ? null : String.Join(", ", reward.Classes.Select(x => x.Name))));
      }
    }

    private void BuildQuestGomMetadata(TreeNode root, GomObject gom) {
      if (root == null || gom?.Data == null) return;
      GomObjectData data = gom.Data;
      Int64 guid = data.ValueOrDefault<Int64>("qstQuestDefinitionGUID", 0);
      Int64 version = data.ValueOrDefault<Int64>("qstVersion", 0);
      Int64 priority = data.ValueOrDefault<Int64>("qstLoadingBlurbPriority", 0);
      TreeNode metadata = AddNode(root, "Jedipedia quest metadata", null, 0, Detail(
        "Definition GUID", guid == 0 ? null : guid.ToString(),
        "Version", version == 0 ? null : version.ToString(),
        "Name string ID", guid == 0 ? null : (guid + 88).ToString(),
        "Loading blurb priority", priority == 0 ? null : priority.ToString()));
      GomObjectData story = data.ValueOrDefault<GomObjectData>("qstCodexStory", null);
      if (story != null) AddNode(metadata, "Loading-screen story blurb", null, 0, Detail(
        "Title bit", story.ValueOrDefault<Int64>("cdxStoryTitleBitIndex", 0).ToString(),
        "Arc bit", story.ValueOrDefault<Int64>("cdxStoryArcBitIndex", 0).ToString(),
        "Class bit", story.ValueOrDefault<Int64>("cdxStoryClassBitIndex", 0).ToString(),
        "Quest bit", story.ValueOrDefault<Int64>("cdxStoryQuestBitIndex", 0).ToString(),
        "Title codex PID", story.ValueOrDefault<UInt64>("cdxStoryTitleCodexPid", 0).ToString(),
        "Arc codex PID", story.ValueOrDefault<UInt64>("cdxStoryArcCodexPid", 0).ToString(),
        "Class codex PID", story.ValueOrDefault<UInt64>("cdxStoryClassCodexPid", 0).ToString(),
        "Quest codex PID", story.ValueOrDefault<UInt64>("cdxStoryQuestCodexPid", 0).ToString()));

      BuildQuestJedipediaRawFlow(metadata, data);
    }

    private void BuildQuestJedipediaRawFlow(TreeNode parent, GomObjectData questData) {
      List<Object> branches = questData.ValueOrDefault<List<Object>>("qstBranches", null);
      if (branches == null || branches.Count == 0) return;

      TreeNode flow = AddNode(parent, "Jedipedia raw branch / step / task data", null, 0,
        Detail("Branches", branches.Count.ToString(), "Source", "qstBranches"));
      Int32 branchIndex = 0;
      foreach (Object rawBranch in branches) {
        if (rawBranch is not GomObjectData branch || _createdNodes >= MaxTreeNodes) continue;
        Int64 branchId = branch.ValueOrDefault<Int64>("qstBranchId", branchIndex);
        List<Object> steps = branch.ValueOrDefault<List<Object>>("qstSteps", null) ?? new List<Object>();
        TreeNode branchNode = AddNode(flow, "Branch " + branchId, null, 0,
          Detail("Branch ID", branchId.ToString(), "Steps", steps.Count.ToString()));
        branchIndex++;

        foreach (Object rawStep in steps) {
          if (rawStep is not GomObjectData step || _createdNodes >= MaxTreeNodes) continue;
          Int64 stepId = step.ValueOrDefault<Int64>("qstStepId", 0);
          List<Object> tasks = step.ValueOrDefault<List<Object>>("qstTasks", null) ?? new List<Object>();
          TreeNode stepNode = AddNode(branchNode, "Step " + stepId, null, 0, Detail(
            "Step ID", stepId.ToString(),
            "Tasks", tasks.Count.ToString(),
            "Shareable", YesNo(step.ValueOrDefault("qstStepIsShareable", false)),
            "Failure time", step.ValueOrDefault<Int64>("qstFailTime", 0).ToString(),
            "Hide timer", YesNo(step.ValueOrDefault("qstHideTimer", false)),
            "Checkpoint", YesNo(step.ValueOrDefault("qstStepCheckpoint", false)),
            "Show play button", YesNo(step.ValueOrDefault("qstStepShowPlayButton", false)),
            "Requires paid permission", YesNo(step.ValueOrDefault("qstStepRequiresPaidPermission", false))));

          List<Object> journalIds = step.ValueOrDefault<List<Object>>("qstStepJournalEntryStringIdList", null);
          if (journalIds != null && journalIds.Count > 0) {
            TreeNode journal = AddNode(stepNode, "Journal strings", null, 0, Detail("Entries", journalIds.Count.ToString()));
            foreach (Object rawId in journalIds) {
              Int64 stringId;
              try { stringId = Convert.ToInt64(rawId); } catch { continue; }
              String text = null;
              try { text = _dom.StringTable.TryGetString("str.qst", stringId); } catch { }
              AddNode(journal, stringId + (String.IsNullOrWhiteSpace(text) ? String.Empty : " — " + OneLine(text)), null, 0,
                Detail("String ID", stringId.ToString(), "Text", text));
            }
          }

          AddQuestBonusMissionRaw(stepNode, step.ValueOrDefault<List<Object>>("qstBonusMissions", null));
          AddQuestHydraRaw(stepNode, step, "Step scripts");
          AddQuestRawField(stepNode, step, "qstSpawners");
          AddQuestRawField(stepNode, step, "qstStepInstanceData");
          AddQuestRawField(stepNode, step, "qstStepPhaseAndAreaList");
          AddQuestRawField(stepNode, step, "qstStepCnvOverride");
          AddQuestRawField(stepNode, step, "qstStepCnvOverrideArea");
          AddQuestRawField(stepNode, step, "qstStepCnvOverridePhase");
          AddQuestRawField(stepNode, step, "qstStepDate");

          foreach (Object rawTask in tasks) {
            if (rawTask is not GomObjectData task || _createdNodes >= MaxTreeNodes) continue;
            Int64 taskId = task.ValueOrDefault<Int64>("qstTaskId", 0);
            Int64 stringId = QuestRawInt64(task, "qstTaskStringid");
            String text = null;
            if (stringId != 0) try { text = _dom.StringTable.TryGetString("str.qst", stringId); } catch { }
            String hook = task.ValueOrDefault<String>("qstHook", null)
              ?? task.ValueOrDefault<String>("qstHookName", null);
            UInt64 hookId = task.ValueOrDefault<UInt64>("qstHookId", 0);
            Int64 hookFlags = QuestRawInt64(task, "qstHookFlags");
            Int64 countMax = QuestRawInt64(task, "qstTaskCountMax");

            String label = "Task " + taskId;
            if (!String.IsNullOrWhiteSpace(text)) label += " — " + OneLine(text);
            TreeNode taskNode = AddNode(stepNode, label, null, 0, Detail(
              "Task ID", taskId.ToString(), "Text", text,
              "String ID", stringId == 0 ? null : stringId.ToString(),
              "Hook", hook, "Stable hook ID", hookId == 0 ? null : hookId.ToString(),
              "Hook flags", hookFlags == 0 ? null : hookFlags.ToString(),
              "Max count", countMax == 0 ? null : countMax.ToString(),
              "Banner hidden", YesNo(task.ValueOrDefault("qstTaskHideBannerText", false)),
              "Show tracking", YesNo(task.ValueOrDefault("qstTaskShowTracking", false)),
              "Show count", YesNo(task.ValueOrDefault("qstTaskShowTrackingCount", false)),
              "Progress as %", YesNo(task.ValueOrDefault("qstTaskShowProgressAsPercentage", false)),
              "Conditional", YesNo(task.ValueOrDefault("qstTaskConditional", false)),
              "Surrender %", QuestRawInt64(task, "qstTaskSurrenderPercentage").ToString()));

            AddQuestTargetDictionary(taskNode, "Objects", task.ValueOrDefault<Dictionary<Object, Object>>("qstTaskObjects", null));
            AddQuestTargetDictionary(taskNode, "Interaction objects", task.ValueOrDefault<Dictionary<Object, Object>>("qstTaskInteractionObjects", null));
            AddQuestBonusMissionRaw(taskNode, task.ValueOrDefault<List<Object>>("qstBonusMissions", null));
            AddQuestHydraRaw(taskNode, task, "Task scripts");
            AddQuestRawField(taskNode, task, "qstTaskMapNoteList");
            AddQuestRawField(taskNode, task, "qstTaskIndicatorOverrideList");
            AddQuestRawField(taskNode, task, "qstTaskIndicatorSourceNameIds");
            AddQuestRawField(taskNode, task, "qstTaskMapLink");
            AddQuestRawField(taskNode, task, "qstTaskMapIconOverride");
            AddQuestRawField(taskNode, task, "qstTaskMapIconRadius");
            AddQuestRawField(taskNode, task, "qstTaskTrackingDisplayType");
            AddQuestRawField(taskNode, task, "qstTaskTrackingItemId");
            AddQuestRawField(taskNode, task, "qstTaskCounterVariable");
            AddQuestRawField(taskNode, task, "qstTaskAutoCompleteVariable");
            AddQuestRawField(taskNode, task, "qstTaskHolocomConversation");
            AddQuestRawField(taskNode, task, "qstTaskHolocomStringid");
            AddQuestRawField(taskNode, task, "qstTaskHolocomOverrideId");
            AddQuestRawField(taskNode, task, "qstTaskForceConversationAssociatedObject");
            AddQuestRawField(taskNode, task, "qstItemsGivenOnCompletion");
            AddQuestRawField(taskNode, task, "qstItemsTakenOnCompletion");
          }
        }
      }
    }

    private static Int64 QuestRawInt64(GomObjectData data, String field) {
      if (data == null || String.IsNullOrWhiteSpace(field)) return 0;
      if (!data.Dictionary.TryGetValue(field, out Object value) || value == null) return 0;
      try { return Convert.ToInt64(value); } catch { return 0; }
    }

    private void AddQuestBonusMissionRaw(TreeNode parent, List<Object> bonuses) {
      if (bonuses == null || bonuses.Count == 0) return;
      TreeNode group = AddNode(parent, "Bonus missions", null, 0, Detail("Count", bonuses.Count.ToString()));
      foreach (Object raw in bonuses) {
        if (raw is not GomObjectData bonus) continue;
        UInt64 id = bonus.ValueOrDefault<UInt64>("qstTaskBonusMissionNodeId", 0);
        UInt64 trigger = bonus.ValueOrDefault<UInt64>("qstTaskBonusMissionStartTrigger", 0);
        String fqn = id == 0 ? null : ResolveFqn(id);
        AddNode(group, fqn ?? (id == 0 ? "Bonus mission" : id.ToString()), fqn, id,
          Detail("Quest ID", id == 0 ? null : id.ToString(), "Start trigger", trigger == 0 ? null : trigger.ToString()));
      }
    }

    private void AddQuestTargetDictionary(TreeNode parent, String title, Dictionary<Object, Object> targets) {
      if (targets == null || targets.Count == 0) return;
      TreeNode group = AddNode(parent, title, null, 0, Detail("Count", targets.Count.ToString()));
      foreach (KeyValuePair<Object, Object> target in targets) {
        UInt64 id = TryUInt64(target.Key);
        String fqn = id == 0 ? null : ResolveFqn(id);
        AddNode(group, fqn ?? Convert.ToString(target.Key), fqn, id,
          Detail("ID", id == 0 ? Convert.ToString(target.Key) : id.ToString(), "Value", FormatReflectedValue(target.Value)));
      }
    }

    private void AddQuestHydraRaw(TreeNode parent, GomObjectData data, String title) {
      if (data == null) return;
      String[] fields = { "qstHydraScriptOnStart", "qstHydraScriptOnSuccess", "qstHydraScriptOnFailure", "qstHydraScriptOnAbandon", "qstHydraScriptOnIncrement" };
      TreeNode group = null;
      foreach (String field in fields) {
        if (!data.Dictionary.TryGetValue(field, out Object value) || value == null) continue;
        group ??= AddNode(parent, title, null, 0, null);
        AddGomValue(group, field, value, 0, 4);
      }
    }

    private void AddQuestRawField(TreeNode parent, GomObjectData data, String field) {
      if (data == null || !data.Dictionary.TryGetValue(field, out Object value) || value == null) return;
      AddGomValue(parent, field, value, 0, 4);
    }

    private void AddQuestItems(TreeNode parent, String relation, List<QuestItem> items) {
      if (items == null) return;
      foreach (QuestItem qi in items) {
        if (qi == null) continue;
        UInt64 id = GetUInt64(qi, "ItemId", "Id");
        if (id == 0) continue;
        String fqn = ResolveFqn(id);
        AddNode(parent, relation + " — " + (fqn ?? id.ToString()), fqn, id,
          Detail("Min", qi.Min.ToString(), "Max", qi.Max.ToString(), "Max count", qi.MaxCount.ToString(),
                 "Variable", qi.VariableId == 0 ? null : qi.VariableId.ToString()));
      }
    }

    private void BuildAchievement(TreeNode root, Achievement achievement) {
      TreeNode tasks = AddNode(root, "Tasks", null, 0, Detail("Count", CountItems(achievement.Tasks)));
      Int32 index = 0;
      foreach (AchTask task in achievement.Tasks ?? new List<AchTask>()) {
        String name = Localized(task.LocalizedNames, task.Name);
        TreeNode taskNode = AddNode(tasks, (++index) + ". " + (String.IsNullOrWhiteSpace(name) ? "Task" : name), null, 0,
          Detail("Required count", task.Count.ToString(), "Index", task.Index.ToString(), "Index 2", task.Index2.ToString()));
        foreach (AchEvent evt in task.Events ?? new List<AchEvent>()) AddId(taskNode, "Event target" + (evt.Value != 0 ? " (" + evt.Value + ")" : String.Empty), evt.Id);
      }
      TreeNode conditions = AddNode(root, "Conditions", null, 0, Detail("Count", CountItems(achievement.Conditions)));
      foreach (AchCondition condition in achievement.Conditions ?? new List<AchCondition>())
        AddNode(conditions, condition.Type + " → " + condition.Target, null, 0, Detail("Flagged", YesNo(condition.UnknownBoolean)));

      Rewards rewards = achievement.Rewards;
      if (rewards != null) {
        TreeNode rewardRoot = AddNode(root, "Rewards", null, 0, Detail("Achievement points", rewards.AchievementPoints.ToString(), "Cartel Coins", rewards.CartelCoins.ToString(),
          "Requisition", rewards.Requisition.ToString(), "Legacy title", Localized(rewards.LocalizedLegacyTitle, rewards.LegacyTitle)));
        if (rewards.ItemRewardList != null) foreach (KeyValuePair<UInt64, Int64> reward in rewards.ItemRewardList)
          AddId(rewardRoot, (reward.Value > 1 ? reward.Value + "× " : String.Empty) + "Item reward", reward.Key);
      }
    }

    private void BuildCodex(TreeNode root, Codex codex) {
      AddNode(root, "Classification", null, 0, Detail("Category", Localized(codex.LocalizedCategoryName, codex.CategoryName), "Level", codex.Level.ToString(),
        "Faction", codex.Faction.ToString(), "Hidden", YesNo(codex.IsHidden), "Planet entry", YesNo(codex.IsPlanet),
        "Classes", codex.Classes == null ? null : String.Join(", ", codex.Classes.Select(x => x.Name))));
      if (codex.Planets != null && codex.Planets.Count > 0) {
        TreeNode links = AddNode(root, "Linked codex / planets", null, 0, Detail("Count", codex.Planets.Count.ToString()));
        foreach (Codex linked in codex.Planets) AddGameObject(links, DisplayName(linked, linked?.Fqn), linked);
      }
    }

    private void BuildSchematic(TreeNode root, Schematic schematic) {
      AddGameObject(root, "Crafted item — " + DisplayName(schematic.Item, null), schematic.Item);
      TreeNode materials = AddNode(root, "Materials", null, 0, Detail("Count", schematic.Materials?.Count.ToString()));
      if (schematic.Materials != null) foreach (KeyValuePair<UInt64, Int32> material in schematic.Materials)
        AddId(materials, material.Value + "× material", material.Key);
      TreeNode research = AddNode(root, "Research / reverse engineering", null, 0, null);
      AddResearch(research, "Tier 1", schematic.Research1, schematic.ResearchQuantity1, schematic.ResearchChance1);
      AddResearch(research, "Tier 2", schematic.Research2, schematic.ResearchQuantity2, schematic.ResearchChance2);
      AddResearch(research, "Tier 3", schematic.Research3, schematic.ResearchQuantity3, schematic.ResearchChance3);
      if (schematic.LearnedIds != null) foreach (UInt64 id in schematic.LearnedIds) AddId(research, "Learns schematic/item", id);
      AddNode(root, "Crafting", null, 0, Detail("Crew skill", schematic.CrewSkill.ToString(), "Category", Localized(schematic.LocalizedCategory, schematic.Category),
        "Subcategory", Localized(schematic.LocalizedSubCategory, schematic.SubCategory), "Quality", schematic.Quality.ToString(),
        "Crafting time", schematic.CraftingTime.ToString(), "Training cost", schematic.TrainingCost.ToString(), "Trainer taught", YesNo(schematic.TrainerTaught)));
    }

    private void AddResearch(TreeNode parent, String label, Item item, Int32 quantity, SchematicResearchChance chance) {
      if (item == null) return;
      AddGameObject(parent, label + " — " + (quantity > 1 ? quantity + "× " : String.Empty) + DisplayName(item, null), item,
        item.Id, item.Fqn, Detail("Chance", chance.ToString()));
    }

    private void BuildTalent(TreeNode root, Talent talent) {
      AddNode(root, "Talent", null, 0, Detail("Ranks", talent.Ranks.ToString(), "Visibility", talent.TalentVisibility.ToString()));
      if (talent.RankStats == null) return;
      TreeNode ranks = AddNode(root, "Rank stats", null, 0, Detail("Ranks", talent.RankStats.Count.ToString()));
      Int32 rank = 0;
      foreach (Talent.RankStatData data in talent.RankStats) {
        TreeNode r = AddNode(ranks, "Rank " + (++rank), null, 0, null);
        IEnumerable<Talent.StatData> stats = (data?.OffensiveStats ?? new List<Talent.StatData>()).Concat(data?.DefensiveStats ?? new List<Talent.StatData>());
        foreach (Talent.StatData stat in stats) {
          if (stat == null || !stat.Enabled) continue;
          String fqn = stat.AffectedNodeId != 0 ? ResolveFqn(stat.AffectedNodeId) : null;
          AddNode(r, stat.Stat + " " + stat.Modifier + " " + stat.Value.ToString("0.###"), fqn, stat.AffectedNodeId,
            Detail("Stat", stat.Stat.ToString(), "Modifier", stat.Modifier.ToString(), "Value", stat.Value.ToString("0.###"), "Affected node", fqn));
        }
      }
    }

    private void BuildMapNote(TreeNode root, MapNote note) {
      AddNode(root, "Map note", null, 0, Detail("Faction", note.Faction.ToString(), "Hunting radius", note.HuntingRadius.ToString(),
        "Bonus radius", note.BonusHuntingRadius.ToString(), "Asset ID", note.AssetID.ToString(), "Wonkavator package", note.WonkaPackageId.ToString()), note);
      if (note.MapLink != null) AddNode(root, "Map link", ResolveFqn(note.MapLink.AreaId), note.MapLink.AreaId,
        Detail("Area", ResolveFqn(note.MapLink.AreaId), "Map name string ID", note.MapLink.MapNameSId.ToString(), "Submap string ID", note.MapLink.SubmapNameSId.ToString()));
      AddId(root, "Wonkavator destination", note.WonkaDestinationId);
    }

    private void BuildPlaceable(TreeNode root, Placeable placeable) {
      AddNode(root, "Placeable", null, 0, Detail("Category", placeable.Category.ToString(), "Faction", placeable.Faction.ToString(),
        "Mailbox", YesNo(placeable.IsMailbox), "Bank", YesNo(placeable.IsBank), "Auction house", YesNo(placeable.IsAuctionHouse),
        "Enhancement station", YesNo(placeable.IsEnhancementStation), "Wonkavator package", placeable.WonkaPackageId.ToString()));
      AddFqn(root, "Conversation", placeable.ConversationFqn);
    }


    private void BuildGameplayGraph(GomObject gom, GameObject model) {
      _graph.BeginGraph(gom?.Name ?? "Gameplay");
      String rootId = "root";
      String rootTitle = model == null ? (gom?.Name ?? "Node") : DisplayName(model, gom?.Name);
      _graph.AddNode(rootId, model?.GetType().Name ?? "Node", rootTitle, gom?.Name, gom?.Name, gom?.Id ?? 0, 0,
        model is MapNote ? gom?.Name : null, ModelPreviewFqnFor(gom, model));
      switch (model) {
        case Ability ability: BuildAbilityGraph(rootId, ability); break;
        case Quest quest: BuildQuestGraph(rootId, quest); break;
        case Item item: BuildItemGraph(rootId, item); break;
        case Npc npc: BuildNpcGraph(rootId, npc); break;
        case Achievement achievement: BuildAchievementGraph(rootId, achievement); break;
        case Codex codex: BuildCodexGraph(rootId, codex); break;
        case Schematic schematic: BuildSchematicGraph(rootId, schematic); break;
        case Talent talent: BuildTalentGraph(rootId, talent); break;
        case MapNote mapNote: BuildMapNoteGraph(rootId, mapNote); break;
        case Placeable placeable: BuildPlaceableGraph(rootId, placeable); break;
        default: BuildGenericGraph(rootId, model); break;
      }
      _graph.EndGraph();
    }

    private void BuildAbilityGraph(String rootId, Ability ability) {
      List<Effect> effects = new List<Effect>();
      foreach (UInt64 id in ability.EffectIds ?? new List<UInt64>()) {
        try { Effect effect = _dom.EffectLoader.Load(id); if (effect != null) effects.Add(effect); } catch { }
      }
      Dictionary<Int64, Effect> byNumber = effects.GroupBy(x => x.Number).ToDictionary(x => x.Key, x => x.First());
      HashSet<Int64> called = new HashSet<Int64>(effects.SelectMany(x => x.ChildEffects ?? new List<Int64>()));
      foreach (Effect effect in effects.OrderBy(x => x.Number)) {
        String id = "effect:" + effect.Number;
        _graph.AddNode(id, "Effect", "#" + effect.Number + " " + EffectDisplayName(effect),
          GraphDetail("Duration", effect.Duration, "Interval", effect.Interval, "Slot", effect.SlotType, "Tags", effect.ParsedTags == null ? null : String.Join(", ", effect.ParsedTags)),
          effect.Fqn, effect.Id, 1);
        if (effect.IsRootEffect || !called.Contains(effect.Number)) _graph.AddEdge(rootId, id, "root effect");
      }
      foreach (Effect effect in effects.OrderBy(x => x.Number)) {
        String effectId = "effect:" + effect.Number;
        foreach (Int64 childNo in effect.ChildEffects ?? new List<Int64>()) {
          if (byNumber.ContainsKey(childNo)) _graph.AddEdge(effectId, "effect:" + childNo, "child effect");
        }
        Int32 subIndex = 0;
        foreach (SubEffect sub in effect.SubEffects ?? new List<SubEffect>()) {
          String subId = effectId + ":sub:" + (++subIndex);
          _graph.AddNode(subId, "Subeffect", "Subeffect " + subIndex,
            GraphDetail("Actions", sub.Actions?.Count ?? 0, "Conditions", sub.Conditions?.Count ?? 0, "Initializers", sub.Initializers?.Count ?? 0,
                        "Triggers", sub.Triggers?.Count ?? 0, "Targets", sub.TargetOverrides?.Count ?? 0), null, 0, 2);
          _graph.AddEdge(effectId, subId, "subeffect");
          AddFunctionGraph(subId, sub.Conditions, "Condition", EffectFunctionNames.Condition);
          AddFunctionGraph(subId, sub.Initializers, "Initializer", EffectFunctionNames.Initializer);
          AddFunctionGraph(subId, sub.Actions, "Action", EffectFunctionNames.Action);
          AddFunctionGraph(subId, sub.Triggers, "Trigger", EffectFunctionNames.Trigger);
          AddFunctionGraph(subId, sub.TargetOverrides, "Target", EffectFunctionNames.TargetOverride);
        }
        Int32 eppIndex = 0;
        foreach (SubEffectEppDetail epp in effect.SubEffectEppDetails ?? new List<SubEffectEppDetail>()) {
          String target = epp.EppId != 0 ? ResolveFqn(epp.EppId) : null;
          String nodeId = effectId + ":epp:" + (++eppIndex);
          _graph.AddNode(nodeId, "EPP", epp.EppSpec ?? target ?? "EPP",
            GraphDetail("Dependent", YesNo(epp.Dependent), "On apply", YesNo(epp.OnApply), "Index", epp.Index), target, epp.EppId, 2);
          _graph.AddEdge(effectId, nodeId, "EPP");
        }
      }
    }

    private void AddFunctionGraph(String parentId, List<SubEffectFunction> functions, String kind, Func<Int64, String> nameResolver) {
      if (functions == null) return;
      Int32 index = 0;
      foreach (SubEffectFunction function in functions) {
        if (function == null) continue;
        String id = parentId + ":" + kind + ":" + (++index);
        String name = nameResolver(function.Type);
        StringBuilder detail = new StringBuilder();
        if (function.CondId != 0) detail.AppendLine("Condition ID: " + function.CondId);
        if (!String.IsNullOrWhiteSpace(function.FailureString)) detail.AppendLine("Failure: " + function.FailureString);
        if (function.ParsedTags != null && function.ParsedTags.Count > 0) detail.AppendLine("Tags: " + String.Join(", ", function.ParsedTags));
        foreach (SubEffectFunctionParam param in function.Params ?? new List<SubEffectFunctionParam>())
          detail.Append(ParamName(param)).Append(" = ").AppendLine(FormatParamValue(param));
        String target = TryResolveFunctionTarget(function);
        _graph.AddNode(id, kind, name, detail.ToString().Trim(), target, TargetId(target), 3);
        _graph.AddEdge(parentId, id, kind.ToLowerInvariant());
        AddParamTargetsToGraph(id, function);
      }
    }

    private void AddParamTargetsToGraph(String functionId, SubEffectFunction function) {
      if (function?.Params == null) return;
      Int32 ordinal = 0;
      foreach (SubEffectFunctionParam param in function.Params) {
        foreach (KeyValuePair<UInt64, String> target in ResolveParamTargets(param)) {
          if (String.IsNullOrWhiteSpace(target.Value)) continue;
          String targetId = "gom:" + target.Key;
          _graph.AddNode(targetId, "Target node", target.Value, "Referenced by " + ParamName(param), target.Value, target.Key, 4);
          _graph.AddEdge(functionId, targetId, ParamName(param));
          if (++ordinal >= 24) return;
        }
      }
    }

    private IEnumerable<KeyValuePair<UInt64, String>> ResolveParamTargets(SubEffectFunctionParam param) {
      if (param == null || param.Value == null) yield break;
      // effTimeIntervalParams are durations, not node ids.  Avoid accidental resolution if a tiny
      // numeric value happens to collide with a loaded object id in a custom/legacy dataset.
      if (param.Type == 5) yield break;
      if (param.Value is UInt64 u && u != 0) {
        String fqn = ResolveFqn(u); if (!String.IsNullOrWhiteSpace(fqn)) yield return new KeyValuePair<UInt64, String>(u, fqn);
        yield break;
      }
      if (param.Value is Int64 l && l > 0) {
        UInt64 id = unchecked((UInt64)l); String fqn = ResolveFqn(id); if (!String.IsNullOrWhiteSpace(fqn)) yield return new KeyValuePair<UInt64, String>(id, fqn);
        yield break;
      }
      if (param.Value is IEnumerable<UInt64> ids) {
        Int32 count = 0;
        foreach (UInt64 id in ids) { String fqn = ResolveFqn(id); if (!String.IsNullOrWhiteSpace(fqn)) yield return new KeyValuePair<UInt64, String>(id, fqn); if (++count >= 24) yield break; }
        yield break;
      }
      if (param.Value is IEnumerable<Int64> signedIds) {
        Int32 count = 0;
        foreach (Int64 raw in signedIds) {
          if (raw > 0) { UInt64 id = unchecked((UInt64)raw); String fqn = ResolveFqn(id); if (!String.IsNullOrWhiteSpace(fqn)) yield return new KeyValuePair<UInt64, String>(id, fqn); }
          if (++count >= 24) yield break;
        }
        yield break;
      }
      if (param.Value is String text && LooksLikeFqn(text)) {
        GomObject obj = null;
        try { obj = _dom.GetObject(text); } catch { }
        if (obj != null) yield return new KeyValuePair<UInt64, String>(obj.Id, obj.Name);
      }
    }

    private void BuildQuestGraph(String rootId, Quest quest) {
      Int32 previous = 0;
      foreach (String id in quest.QuestsPreviousB62 ?? new List<String>()) {
        String nodeId = "previous:" + (++previous);
        _graph.AddNode(nodeId, "Previous quest", id, "Base62 quest id", null, 0, 0);
        _graph.AddEdge(nodeId, rootId, "previous");
      }
      Int32 next = 0;
      foreach (String id in quest.QuestsNextB62 ?? new List<String>()) {
        String nodeId = "next:" + (++next);
        _graph.AddNode(nodeId, "Next quest", id, "Base62 quest id", null, 0, 5);
        _graph.AddEdge(rootId, nodeId, "next");
      }

      IEnumerable branches = ReflectedEnumerable(quest, "Branches");
      Int32 branchNo = 0;
      if (branches != null) foreach (Object raw in branches) {
        if (raw is not QuestBranch branch) continue;
        String branchId = "branch:" + (++branchNo) + ":" + branch.Id;
        _graph.AddNode(branchId, "Quest branch", "Branch " + branchNo, "Branch ID: " + branch.Id, null, 0, 1);
        _graph.AddEdge(rootId, branchId, "branch");
        Int32 stepNo = 0;
        foreach (QuestStep step in branch.Steps ?? new List<QuestStep>()) {
          String journal = Localized(step.LocalizedJournalText, step.JournalText);
          String stepId = branchId + ":step:" + (++stepNo);
          _graph.AddNode(stepId, "Quest step", String.IsNullOrWhiteSpace(journal) ? "Step " + stepNo : OneLine(journal),
            GraphDetail("Shareable", YesNo(step.IsShareable), "Journal", journal), null, 0, 2);
          _graph.AddEdge(branchId, stepId, "step");
          Int32 taskNo = 0;
          foreach (QuestTask task in step.Tasks ?? new List<QuestTask>()) {
            String text = Localized(task.LocalizedString, task.Text);
            String taskId = stepId + ":task:" + (++taskNo);
            _graph.AddNode(taskId, "Quest task", String.IsNullOrWhiteSpace(text) ? "Task " + taskNo : OneLine(text),
              GraphDetail("Hook", task.Hook, "Count", task.ShowCount ? task.CountMax.ToString() : null, "Tracking", YesNo(task.ShowTracking)), null, 0, 3);
            _graph.AddEdge(stepId, taskId, "task");
            foreach (UInt64 id in task.TaskNpcIds ?? new List<UInt64>()) GraphTarget(taskId, "NPC", id, "target");
            foreach (UInt64 id in task.TaskPlcIds ?? new List<UInt64>()) GraphTarget(taskId, "Placeable", id, "target");
            foreach (UInt64 id in task.TaskQuestIds ?? new List<UInt64>()) GraphTarget(taskId, "Quest", id, "quest");
            foreach (String fqn in task.MapNoteFqnList ?? new List<String>()) GraphMapNoteTarget(taskId, fqn, "map note");
            foreach (UInt64 id in task.BonusMissionsIds ?? new List<UInt64>()) GraphTarget(taskId, "Bonus quest", id, "bonus");
            GraphQuestItems(taskId, task.ItemsGiven, "gives");
            GraphQuestItems(taskId, task.ItemsTaken, "takes");
          }
          foreach (UInt64 id in step.BonusMissionsIds ?? new List<UInt64>()) GraphTarget(stepId, "Bonus quest", id, "bonus");
          GraphQuestItems(stepId, step.ItemsGiven, "gives");
          GraphQuestItems(stepId, step.ItemsTaken, "takes");
        }
        foreach (Item item in branch.RewardAll ?? new List<Item>()) GraphTarget(branchId, "Reward", item, "reward all");
        foreach (Item item in branch.RewardOne ?? new List<Item>()) GraphTarget(branchId, "Reward", item, "choose reward");
      }
      IEnumerable rewards = ReflectedEnumerable(quest, "Rewards");
      if (rewards != null) foreach (Object raw in rewards) {
        if (raw is not QuestReward reward || reward.RewardItemId == 0) continue;
        String fqn = ResolveFqn(reward.RewardItemId);
        String id = "gom:" + reward.RewardItemId;
        _graph.AddNode(id, "Quest reward", fqn ?? reward.RewardItemId.ToString(),
          GraphDetail("Quantity", reward.NumberOfItem, "Always", YesNo(reward.IsAlwaysProvided), "Min level", reward.MinLevel, "Max level", reward.MaxLevel),
          fqn, reward.RewardItemId, 4);
        _graph.AddEdge(rootId, id, "quest reward");
      }
    }

    private void GraphQuestItems(String fromId, List<QuestItem> items, String relation) {
      if (items == null) return;
      foreach (QuestItem qi in items) {
        if (qi == null) continue;
        UInt64 id = GetUInt64(qi, "ItemId", "Id");
        if (id != 0) GraphTarget(fromId, "Item", id, relation);
      }
    }

    private void BuildItemGraph(String rootId, Item item) {
      GraphTarget(rootId, "Use ability", item.UseAbility, "use ability");
      GraphTarget(rootId, "Equip ability", item.EquipAbility, "equip ability");
      if (item.SchematicId != 0) GraphTarget(rootId, "Schematic", item.SchematicId, "schematic");
      AddFqnGraph(rootId, "Conversation", item.ConversationFqn, "conversation");
      AddFqnGraph(rootId, "Appearance", item.AppearanceImperial ?? item.ImperialAppearanceTag, "imperial appearance");
      AddFqnGraph(rootId, "Appearance", item.AppearanceRepublic ?? item.RepublicAppearanceTag, "republic appearance");
      foreach (ItemEnhancement enhancement in item.EnhancementSlots ?? new ItemEnhancementList()) {
        if (enhancement?.ModificationId > 0) GraphTarget(rootId, "Modification", enhancement.ModificationId, enhancement.Slot.ToString());
      }
      try {
        SetBonusEntry set = item.SetBonus;
        if (set?.BonusAbilityByNum != null) foreach (KeyValuePair<Int64, Ability> bonus in set.BonusAbilityByNum.OrderBy(x => x.Key))
          GraphTarget(rootId, "Set bonus ability", bonus.Value, bonus.Key + " pieces");
        if (set?.Sources != null) foreach (Item source in set.Sources.Take(40)) GraphTarget(rootId, "Set item", source, "same set");
      } catch { }
    }

    private void BuildNpcGraph(String rootId, Npc npc) {
      if (npc.ClassSpec != null) GraphTarget(rootId, "Class", npc.ClassSpec, "class spec");
      foreach (UInt64 id in npc.AbilityPackageIdList ?? new HashSet<UInt64>()) {
        AbilityPackage package = null;
        try { package = _dom.AbilityPackageLoader.Load(id); } catch { }
        String fqn = package?.Fqn ?? ResolveFqn(id);
        String packageId = "gom:" + id;
        _graph.AddNode(packageId, "Ability package", DisplayName(package, fqn ?? id.ToString()), fqn, fqn, id, 1);
        _graph.AddEdge(rootId, packageId, "ability package");
        if (package?.PackageAbilities != null) foreach (PackageAbility packageAbility in package.PackageAbilities.Take(100)) {
          if (packageAbility == null || packageAbility.AbilityId == 0) continue;
          String abilityFqn = ResolveFqn(packageAbility.AbilityId);
          Ability packageModel = null;
          try { packageModel = packageAbility.Ability; } catch { }
          String abilityId = "gom:" + packageAbility.AbilityId;
          _graph.AddNode(abilityId, "Ability", DisplayName(packageModel, abilityFqn ?? packageAbility.AbilityId.ToString()),
            GraphDetail("Level", packageAbility.Level, "Auto acquire", YesNo(packageAbility.AutoAcquire), "Scales", YesNo(packageAbility.Scales),
                        "AI priority", packageAbility.AiUsePriority), abilityFqn, packageAbility.AbilityId, 2);
          _graph.AddEdge(packageId, abilityId, "contains");
        }
      }
      AddFqnGraph(rootId, "Conversation", npc.CnvConversationName, "conversation");
      if (npc.CodexId != 0) GraphTarget(rootId, "Codex", npc.CodexId, "codex");
      if (npc.LootTableId > 0) GraphTarget(rootId, "Loot", unchecked((UInt64)npc.LootTableId), "loot");
      foreach (String vendor in npc.VendorPackages ?? new List<String>()) AddFqnGraph(rootId, "Vendor", vendor, "vendor package");
      Int32 visual = 0;
      foreach (NpcVisualData data in npc.VisualDataList ?? new List<NpcVisualData>()) {
        if (data == null) continue;
        String vId = "visual:" + (++visual);
        _graph.AddNode(vId, "NPC visual", data.AppearanceFqn ?? "Visual " + visual,
          GraphDetail("Character spec", data.CharSpec, "Scale", data.ScaleAdjustment), data.AppearanceFqn, data.AppearanceId, 1);
        _graph.AddEdge(rootId, vId, "appearance");
        if (data.MeleeWepId != 0) GraphTarget(vId, "Weapon", data.MeleeWepId, "melee");
        if (data.MeleeOffWepId != 0) GraphTarget(vId, "Weapon", data.MeleeOffWepId, "melee offhand");
        if (data.RangedWepId != 0) GraphTarget(vId, "Weapon", data.RangedWepId, "ranged");
        if (data.RangedOffWepId != 0) GraphTarget(vId, "Weapon", data.RangedOffWepId, "ranged offhand");
      }
    }

    private void BuildAchievementGraph(String rootId, Achievement achievement) {
      Int32 taskNo = 0;
      foreach (AchTask task in achievement.Tasks ?? new List<AchTask>()) {
        String taskId = "ach-task:" + (++taskNo);
        String name = Localized(task.LocalizedNames, task.Name);
        _graph.AddNode(taskId, "Achievement task", String.IsNullOrWhiteSpace(name) ? "Task " + taskNo : name,
          GraphDetail("Required count", task.Count, "Index", task.Index, "Index 2", task.Index2), null, 0, 1);
        _graph.AddEdge(rootId, taskId, "task");
        Int32 evtNo = 0;
        foreach (AchEvent evt in task.Events ?? new List<AchEvent>()) {
          String evtId = taskId + ":event:" + (++evtNo);
          String fqn = ResolveFqn(evt.Id);
          _graph.AddNode(evtId, "Achievement event", fqn ?? evt.Id.ToString(), GraphDetail("Value", evt.Value), fqn, evt.Id, 2);
          _graph.AddEdge(taskId, evtId, "event");
        }
      }
      if (achievement.Rewards?.ItemRewardList != null) foreach (KeyValuePair<UInt64, Int64> reward in achievement.Rewards.ItemRewardList)
        GraphTarget(rootId, "Item reward", reward.Key, reward.Value > 1 ? reward.Value + "× reward" : "reward");
    }

    private void BuildCodexGraph(String rootId, Codex codex) {
      Int32 index = 0;
      foreach (Codex linked in codex.Planets ?? new List<Codex>()) {
        if (linked == null) continue;
        String id = "codex:" + (++index) + ":" + linked.Id;
        _graph.AddNode(id, linked.IsPlanet ? "Planet codex" : "Codex", DisplayName(linked, linked.Fqn),
          GraphDetail("Level", linked.Level, "Category", Localized(linked.LocalizedCategoryName, linked.CategoryName)), linked.Fqn, linked.Id, 1);
        _graph.AddEdge(rootId, id, linked.IsPlanet ? "planet" : "linked codex");
      }
    }

    private void BuildSchematicGraph(String rootId, Schematic schematic) {
      GraphTarget(rootId, "Crafted item", schematic.Item, "crafts");
      if (schematic.Materials != null) foreach (KeyValuePair<UInt64, Int32> material in schematic.Materials)
        GraphTarget(rootId, "Material", material.Key, material.Value + "× material");
      if (schematic.Research1 != null) GraphTarget(rootId, "Research result", schematic.Research1, "tier 1");
      if (schematic.Research2 != null) GraphTarget(rootId, "Research result", schematic.Research2, "tier 2");
      if (schematic.Research3 != null) GraphTarget(rootId, "Research result", schematic.Research3, "tier 3");
      foreach (UInt64 id in schematic.LearnedIds ?? new List<UInt64>()) GraphTarget(rootId, "Learned result", id, "learns");
    }

    private void BuildTalentGraph(String rootId, Talent talent) {
      Int32 rankNo = 0;
      foreach (Talent.RankStatData data in talent.RankStats ?? new List<Talent.RankStatData>()) {
        String rankId = "rank:" + (++rankNo);
        _graph.AddNode(rankId, "Talent rank", "Rank " + rankNo, null, null, 0, 1);
        _graph.AddEdge(rootId, rankId, "rank");
        IEnumerable<Talent.StatData> stats = (data?.OffensiveStats ?? new List<Talent.StatData>()).Concat(data?.DefensiveStats ?? new List<Talent.StatData>());
        Int32 statNo = 0;
        foreach (Talent.StatData stat in stats) {
          if (stat == null || !stat.Enabled) continue;
          String statId = rankId + ":stat:" + (++statNo);
          String target = stat.AffectedNodeId != 0 ? ResolveFqn(stat.AffectedNodeId) : null;
          _graph.AddNode(statId, "Talent stat", stat.Stat + " " + stat.Modifier + " " + stat.Value.ToString("0.###"),
            GraphDetail("Affected node", target), target, stat.AffectedNodeId, 2);
          _graph.AddEdge(rankId, statId, "modifier");
        }
      }
    }

    private void BuildMapNoteGraph(String rootId, MapNote note) {
      if (note.MapLink != null && note.MapLink.AreaId != 0) GraphTarget(rootId, "Map-link area", note.MapLink.AreaId, "links to area");
      if (note.WonkaDestinationId != 0) GraphTarget(rootId, "Wonkavator destination", note.WonkaDestinationId, "destination");
      _graph.AddNode("world-location", "World location", "Find authored placement",
        "Scans the installed mapnotes.not files on demand; no external location database is required.", null, 0, 1, note.Fqn);
      _graph.AddEdge(rootId, "world-location", "world placement");
    }

    private void BuildPlaceableGraph(String rootId, Placeable placeable) {
      AddFqnGraph(rootId, "Conversation", placeable.ConversationFqn, "conversation");
      if (placeable.WonkaPackageId != 0) {
        String wonkaId = "wonka:" + placeable.WonkaPackageId;
        _graph.AddNode(wonkaId, "Wonkavator package", placeable.WonkaPackageId.ToString(),
          "Runtime transportation package id", null, 0, 1);
        _graph.AddEdge(rootId, wonkaId, "transport");
      }
    }

    private void BuildGenericGraph(String rootId, GameObject model) {
      if (model == null) return;
      _graph.AddNode("summary", "Summary", model.GetType().Name, "No dedicated relation graph is needed for this node type yet. Use the Structured tree and References tabs for all fields.", null, 0, 1);
      _graph.AddEdge(rootId, "summary", "summary");
    }

    private void GraphTarget(String fromId, String kind, GameObject target, String relation) {
      if (target == null) return;
      String id = "gom:" + target.Id;
      _graph.AddNode(id, kind, DisplayName(target, target.Fqn), target.Fqn, target.Fqn, target.Id, 4);
      _graph.AddEdge(fromId, id, relation);
    }

    private void GraphTarget(String fromId, String kind, UInt64 targetId, String relation) {
      if (targetId == 0) return;
      String fqn = ResolveFqn(targetId);
      String id = "gom:" + targetId;
      _graph.AddNode(id, kind, fqn ?? targetId.ToString(), fqn == null ? "Node is not present in the loaded DOM" : fqn, fqn, targetId, 4);
      _graph.AddEdge(fromId, id, relation);
    }

    private void GraphTarget(String fromId, String kind, String targetFqn, String relation) {
      if (String.IsNullOrWhiteSpace(targetFqn)) return;
      GomObject obj = null;
      try { obj = _dom.GetObject(targetFqn); } catch { }
      String id = obj != null ? "gom:" + obj.Id : "fqn:" + targetFqn;
      _graph.AddNode(id, kind, targetFqn, obj == null ? "Referenced node is not present in the loaded DOM" : targetFqn, obj == null ? null : targetFqn, obj?.Id ?? 0, 4);
      _graph.AddEdge(fromId, id, relation);
    }

    private void GraphMapNoteTarget(String fromId, String fqn, String relation) {
      if (String.IsNullOrWhiteSpace(fqn)) return;
      GomObject obj = null;
      try { obj = _dom.GetObject(fqn); } catch { }
      String id = obj != null ? "gom:" + obj.Id : "fqn:" + fqn;
      _graph.AddNode(id, "Map note", fqn, obj == null ? "Referenced map note is not present in the loaded DOM" : fqn,
        obj == null ? null : fqn, obj?.Id ?? 0, 4, fqn);
      _graph.AddEdge(fromId, id, relation);
    }

    private void AddFqnGraph(String fromId, String kind, String fqn, String relation) => GraphTarget(fromId, kind, fqn, relation);

    private static String GraphDetail(params Object[] values) {
      if (values == null) return null;
      StringBuilder s = new StringBuilder();
      for (Int32 i = 0; i + 1 < values.Length; i += 2) {
        if (values[i] == null || values[i + 1] == null) continue;
        String value = values[i + 1].ToString();
        if (String.IsNullOrWhiteSpace(value)) continue;
        s.Append(values[i]).Append(": ").AppendLine(value);
      }
      return s.ToString().Trim();
    }

    private void TreeAfterSelect(Object sender, TreeViewEventArgs e) {
      _details.BeginUpdate();
      try {
        _details.Items.Clear();
        _selectedEntry = Entry(e.Node);
        if (_selectedEntry != null) {
          if (!String.IsNullOrWhiteSpace(_selectedEntry.TargetFqn)) AddDetailRow("Node", _selectedEntry.TargetFqn);
          if (_selectedEntry.TargetId != 0) AddDetailRow("Node ID", _selectedEntry.TargetId.ToString());
          if (!String.IsNullOrWhiteSpace(_selectedEntry.ModelPreviewFqn)) AddDetailRow("3D / Model", _selectedEntry.ModelPreviewFqn);
          foreach (KeyValuePair<String, String> pair in _selectedEntry.Details) AddDetailRow(pair.Key, pair.Value);
        }
        _go.Enabled = _selectedEntry != null && !String.IsNullOrWhiteSpace(_selectedEntry.TargetFqn);
        _world.Enabled = _selectedEntry?.WorldMapNote != null && _openWorldMapNote != null;
        _model.Enabled = _selectedEntry != null && !String.IsNullOrWhiteSpace(_selectedEntry.ModelPreviewFqn) && _openModelPreview != null;
      } finally { _details.EndUpdate(); }
    }

    private void AddDetailRow(String field, String value) {
      if (String.IsNullOrWhiteSpace(value)) return;
      ListViewItem row = new ListViewItem(field ?? String.Empty);
      row.SubItems.Add(value);
      _details.Items.Add(row);
    }

    private void NavigateSelected() {
      if (_selectedEntry == null || String.IsNullOrWhiteSpace(_selectedEntry.TargetFqn)) return;
      _navigate?.Invoke(_selectedEntry.TargetFqn);
    }

    private void OpenSelectedModel() {
      if (_selectedEntry == null || String.IsNullOrWhiteSpace(_selectedEntry.ModelPreviewFqn) || _openModelPreview == null) return;
      _openModelPreview(_selectedEntry.ModelPreviewFqn);
    }

    private void CopySelectedDetails() {
      if (_details.Items.Count == 0) return;
      StringBuilder text = new StringBuilder();
      foreach (ListViewItem row in _details.Items) text.Append(row.Text).Append(": ").AppendLine(row.SubItems.Count > 1 ? row.SubItems[1].Text : String.Empty);
      try { Clipboard.SetText(text.ToString()); } catch { }
    }

    private void FindNext() {
      String query = _filter.Text?.Trim();
      if (String.IsNullOrWhiteSpace(query) || _tree.Nodes.Count == 0) return;
      List<TreeNode> all = Flatten(_tree.Nodes).ToList();
      Int32 start = _tree.SelectedNode == null ? -1 : all.IndexOf(_tree.SelectedNode);
      for (Int32 offset = 1; offset <= all.Count; offset++) {
        TreeNode node = all[(start + offset + all.Count) % all.Count];
        if (node.Text.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0) continue;
        _tree.SelectedNode = node;
        node.EnsureVisible();
        return;
      }
      System.Media.SystemSounds.Beep.Play();
    }

    private static IEnumerable<TreeNode> Flatten(TreeNodeCollection nodes) {
      foreach (TreeNode node in nodes) {
        yield return node;
        foreach (TreeNode child in Flatten(node.Nodes)) yield return child;
      }
    }

    private TreeNode AddNode(TreeNode parent, String text, String targetFqn, UInt64 targetId, List<KeyValuePair<String, String>> details,
                             MapNote worldMapNote = null, String modelPreviewFqn = null) {
      if (_createdNodes >= MaxTreeNodes) return null;
      if (String.IsNullOrWhiteSpace(modelPreviewFqn)) modelPreviewFqn = NormalizeModelPreviewFqn(targetFqn);
      ExplorerEntry entry = new ExplorerEntry { TargetFqn = targetFqn, TargetId = targetId, WorldMapNote = worldMapNote, ModelPreviewFqn = modelPreviewFqn };
      if (details != null) entry.Details.AddRange(details.Where(x => !String.IsNullOrWhiteSpace(x.Value)));
      TreeNode node = new TreeNode(String.IsNullOrWhiteSpace(text) ? "(unnamed)" : text) { Tag = entry };
      if (!String.IsNullOrWhiteSpace(targetFqn)) node.NodeFont = _linkFont;
      if (parent == null) _tree.Nodes.Add(node); else parent.Nodes.Add(node);
      _createdNodes++;
      return node;
    }

    private void AddGameObject(TreeNode parent, String label, GameObject value) => AddGameObject(parent, label, value, value?.Id ?? 0, value?.Fqn, null);
    private void AddGameObject(TreeNode parent, String label, GameObject value, UInt64 fallbackId, String fallbackFqn) => AddGameObject(parent, label, value, fallbackId, fallbackFqn, null);
    private void AddGameObject(TreeNode parent, String label, GameObject value, UInt64 fallbackId, String fallbackFqn, List<KeyValuePair<String, String>> extra) {
      String fqn = value?.Fqn ?? fallbackFqn ?? ResolveFqn(fallbackId);
      UInt64 id = value?.Id ?? fallbackId;
      List<KeyValuePair<String, String>> details = Detail("FQN", fqn, "ID", id == 0 ? null : id.ToString());
      if (extra != null) details.AddRange(extra);
      AddNode(parent, label, fqn, id, details);
    }

    private void AddId(TreeNode parent, String relation, UInt64 id) {
      if (id == 0 || parent == null) return;
      String fqn = ResolveFqn(id);
      AddNode(parent, relation + " — " + (fqn ?? id.ToString()), fqn, id, Detail("Relation", relation));
    }

    private void AddBase62(TreeNode parent, String relation, String base62) {
      if (String.IsNullOrWhiteSpace(base62) || parent == null) return;
      // Base62 strings are presentation helpers; search the current DOM by comparing known node IDs only
      // when a direct name is not already available. Avoid a global scan here.
      AddNode(parent, relation + " — " + base62, null, 0, Detail("Base62 ID", base62));
    }

    private void AddFqn(TreeNode parent, String relation, String fqn) {
      if (String.IsNullOrWhiteSpace(fqn) || parent == null) return;
      GomObject obj = null;
      try { obj = _dom.GetObject(fqn); } catch { }
      AddNode(parent, relation + " — " + fqn, obj == null ? null : fqn, obj?.Id ?? 0,
        Detail("Relation", relation, "Status", obj == null ? "Referenced node is not present in the loaded DOM" : "Present"));
    }

    private void AddMapNoteFqn(TreeNode parent, String relation, String fqn) {
      if (String.IsNullOrWhiteSpace(fqn) || parent == null) return;
      GomObject obj = null; MapNote note = null;
      try { obj = _dom.GetObject(fqn); if (obj != null) note = GameObject.Load(obj, true) as MapNote; } catch { }
      AddNode(parent, relation + " — " + fqn, obj == null ? null : fqn, obj?.Id ?? 0,
        Detail("Relation", relation, "Status", obj == null ? "Referenced node is not present in the loaded DOM" : "Present",
               "World", note == null ? "No map-note model available" : "Can locate this authored note in World Browser"), note);
    }

    private void OpenSelectedInWorld() {
      if (_selectedEntry?.WorldMapNote == null || _openWorldMapNote == null) return;
      _openWorldMapNote(_selectedEntry.WorldMapNote);
    }

    private void OpenWorldMapNoteFqn(String fqn) {
      if (_openWorldMapNote == null || String.IsNullOrWhiteSpace(fqn)) return;
      try {
        GomObject obj = _dom.GetObject(fqn);
        MapNote note = obj == null ? null : GameObject.Load(obj, true) as MapNote;
        if (note != null) _openWorldMapNote(note);
      } catch { }
    }

    private static String NormalizeModelPreviewFqn(String fqn) {
      if (String.IsNullOrWhiteSpace(fqn)) return null;
      String value = fqn.Trim();
      if (value.StartsWith("npp.", StringComparison.OrdinalIgnoreCase)
          || value.StartsWith("ipp.", StringComparison.OrdinalIgnoreCase)
          || value.StartsWith("itm.", StringComparison.OrdinalIgnoreCase)
          || value.StartsWith("dyn.", StringComparison.OrdinalIgnoreCase)
          || value.StartsWith("mnt.", StringComparison.OrdinalIgnoreCase)) return value;
      return null;
    }

    private static String ModelPreviewFqnFor(GomObject gom, GameObject model) {
      if (model is Npc npc) {
        String appearance = npc.VisualDataList?.FirstOrDefault(x => x != null && !String.IsNullOrWhiteSpace(x.AppearanceFqn))?.AppearanceFqn;
        if (!String.IsNullOrWhiteSpace(appearance)) return NormalizeModelPreviewFqn(appearance);
      }
      if (model is Item item) {
        String[] appearances = { item.AppearanceImperial, item.AppearanceRepublic, item.ImperialAppearanceTag, item.RepublicAppearanceTag };
        foreach (String appearance in appearances) {
          String resolved = NormalizeModelPreviewFqn(appearance);
          if (!String.IsNullOrWhiteSpace(resolved)) return resolved;
        }
        try {
          String weaponSpec = gom?.Data?.ValueOrDefault<String>("cbtWeaponAppearanceSpec", null);
          if (!String.IsNullOrWhiteSpace(weaponSpec)) return gom.Name;
        } catch { }
      }
      return NormalizeModelPreviewFqn(gom?.Name);
    }

    private void UpdateHero(GomObject gom, GameObject model) {
      _heroTitle.Text = DisplayName(model, gom?.Name);
      _heroMeta.Text = (model?.GetType().Name ?? gom?.DomClass?.Name ?? "Node") + (gom == null ? String.Empty : Environment.NewLine + gom.Name);
      Image old = _heroIcon.Image; _heroIcon.Image = null; old?.Dispose();
      Bitmap icon = null;
      try { icon = _rootIconProvider?.Invoke(); } catch { }
      if (icon != null) { _heroIcon.Image = icon; _heroIcon.Visible = true; } else _heroIcon.Visible = false;
      Int32 left = _heroIcon.Visible ? 94 : 8;
      _heroTitle.Left = left; _heroMeta.Left = left;
      _heroTitle.Width = Math.Max(80, (_heroTitle.Parent?.ClientSize.Width ?? Width) - left - 10);
      _heroMeta.Width = _heroTitle.Width;
    }

    private String ResolveFqn(UInt64 id) {
      if (id == 0) return null;
      try { return _dom.GetObject(id)?.Name; } catch { return null; }
    }

    private UInt64 TargetId(String fqn) {
      if (String.IsNullOrWhiteSpace(fqn)) return 0;
      try { return _dom.GetObject(fqn)?.Id ?? 0; } catch { return 0; }
    }

    private static ExplorerEntry Entry(TreeNode node) => node?.Tag as ExplorerEntry;

    private static List<KeyValuePair<String, String>> EffectDetails(Effect effect) {
      return Detail("FQN", effect.Fqn, "Effect number", effect.Number.ToString(), "Root", YesNo(effect.IsRootEffect),
        "Duration", effect.Duration == 0 ? null : effect.Duration.ToString(), "Interval", effect.Interval == 0 ? null : effect.Interval.ToString(),
        "Slot", effect.SlotType.ToString(), "Instant", YesNo(effect.IsInstant), "Passive", YesNo(effect.Passive), "Debuff", YesNo(effect.IsDebuff),
        "Stack limit", effect.HasStackLimit ? effect.StackLimit.ToString() : null,
        "Tags", effect.ParsedTags == null ? null : String.Join(", ", effect.ParsedTags), "Children", effect.ChildEffects == null ? null : String.Join(", ", effect.ChildEffects));
    }

    private static String EffectDisplayName(Effect effect) {
      if (effect == null) return "Effect";
      if (!String.IsNullOrWhiteSpace(effect.Name)) return effect.Name;
      return effect.Fqn ?? "Effect";
    }

    private static String DisplayName(GameObject value, String fallback) {
      if (value == null) return fallback ?? "(missing)";
      try {
        PropertyInfo localized = value.GetType().GetProperty("LocalizedName", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (localized?.GetValue(value) is Dictionary<String, String> dict) {
          String text = StringTable.SelectLocalizedText(dict, null);
          if (!String.IsNullOrWhiteSpace(text)) return text;
        }
        PropertyInfo name = value.GetType().GetProperty("Name", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        String raw = name?.GetValue(value)?.ToString();
        if (!String.IsNullOrWhiteSpace(raw)) return raw;
      } catch { }
      return value.Fqn ?? fallback ?? value.Id.ToString();
    }

    private static String Localized(Dictionary<String, String> values, String fallback) {
      try { return StringTable.SelectLocalizedText(values, fallback) ?? fallback; } catch { return fallback; }
    }

    private static IEnumerable ReflectedEnumerable(Object obj, String propertyName) {
      try {
        return obj?.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(obj) as IEnumerable;
      } catch { return null; }
    }

    private static Object GetObject(Object obj, params String[] names) {
      if (obj == null) return null;
      foreach (String name in names) {
        try {
          PropertyInfo property = obj.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
          if (property != null) return property.GetValue(obj);
        } catch { }
      }
      return null;
    }

    private static UInt64 GetUInt64(Object obj, params String[] names) {
      Object value = GetObject(obj, names);
      if (value == null) return 0;
      try { return Convert.ToUInt64(value); } catch { return 0; }
    }


    private static String ParamName(SubEffectFunctionParam param) {
      if (param == null) return "?";
      String name = param.KeyName;
      if (!String.IsNullOrWhiteSpace(name)) {
        // Most schema names use an eff* prefix.  Keep the meaningful part while preserving unknown
        // custom enum names exactly as BioWare shipped them.
        if (name.StartsWith("eff", StringComparison.OrdinalIgnoreCase) && name.Length > 3) name = name.Substring(3);
        return name + " [" + param.Key + "]";
      }
      return "#" + param.Key;
    }

    private String FormatParamValue(SubEffectFunctionParam param) {
      if (param == null || param.Value == null) return "null";
      Object value = param.Value;
      if (value is UInt64 u && u != 0) {
        String fqn = ResolveFqn(u);
        return fqn == null ? u.ToString() : u + " → " + fqn;
      }
      if (value is Int64 l && l > 0) {
        String fqn = ResolveFqn(unchecked((UInt64)l));
        return fqn == null ? l.ToString() : l + " → " + fqn;
      }
      if (value is IEnumerable<UInt64> ids) {
        List<String> parts = new List<String>();
        foreach (UInt64 id in ids) {
          String fqn = ResolveFqn(id);
          parts.Add(fqn == null ? id.ToString() : id + " → " + fqn);
          if (parts.Count >= 24) { parts.Add("…"); break; }
        }
        return String.Join(", ", parts);
      }
      if (value is IEnumerable<Int64> signedIds && param.Type == 7) {
        List<String> parts = new List<String>();
        foreach (Int64 raw in signedIds) {
          String fqn = raw > 0 ? ResolveFqn(unchecked((UInt64)raw)) : null;
          parts.Add(fqn == null ? raw.ToString() : raw + " → " + fqn);
          if (parts.Count >= 24) { parts.Add("…"); break; }
        }
        return String.Join(", ", parts);
      }
      if (value is String text && LooksLikeFqn(text)) {
        GomObject obj = null;
        try { obj = _dom.GetObject(text); } catch { }
        if (obj != null) return text + " → present node #" + obj.Id;
      }
      return FormatValue(value);
    }

    private static String ParamType(Int32 type) {
      return type switch { 1 => "bool", 2 => "string", 3 => "int", 4 => "float", 5 => "time", 6 => "float[]", 7 => "int[]", 8 => "id[]", _ => "type " + type };
    }

    private static String FormatValue(Object value) {
      if (value == null) return "null";
      if (value is String text) return text;
      if (value is IEnumerable enumerable) {
        List<String> parts = new List<String>();
        foreach (Object item in enumerable) { parts.Add(item?.ToString() ?? "null"); if (parts.Count >= 32) { parts.Add("…"); break; } }
        return String.Join(", ", parts);
      }
      return value.ToString();
    }

    private static Boolean LooksLikeFqn(String value) {
      return !String.IsNullOrWhiteSpace(value) && value.IndexOf('.') > 1 && value.IndexOf(' ') < 0 && value.Length < 512;
    }

    private static String Seconds(Single value) => Math.Abs(value) < .0001f ? null : value.ToString("0.###") + " s";
    private static String YesNo(Boolean value) => value ? "Yes" : "No";
    private static String CountItems(IEnumerable collection) {
      if (collection == null) return "0";
      if (collection is ICollection counted) return counted.Count.ToString();
      Int32 count = 0;
      foreach (Object _ in collection) count++;
      return count.ToString();
    }
    private static String OneLine(String text) {
      if (String.IsNullOrWhiteSpace(text)) return text;
      String value = text.Replace("\r", " ").Replace("\n", " ").Trim();
      return value.Length > 120 ? value.Substring(0, 117) + "…" : value;
    }

    private static List<KeyValuePair<String, String>> Detail(params String[] values) {
      List<KeyValuePair<String, String>> result = new List<KeyValuePair<String, String>>();
      if (values == null) return result;
      for (Int32 i = 0; i + 1 < values.Length; i += 2) {
        if (!String.IsNullOrWhiteSpace(values[i + 1])) result.Add(new KeyValuePair<String, String>(values[i], values[i + 1]));
      }
      return result;
    }
  }
}
