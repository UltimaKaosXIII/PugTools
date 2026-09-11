using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using GomLib;
using Newtonsoft.Json;
using TorArchive;

namespace PugTools {
  /// <summary>
  /// Database-free browser for SWTOR's client-side Data Object Model (client.gom).
  /// The browser uses only the DOM already shipped with the selected client and builds
  /// all reverse references in memory. No history/index database or sidecar file is created.
  /// </summary>
  internal sealed class DomBrowser : Form {
    private sealed class SchemaEntry {
      internal DomType Type;
      internal String Kind;
      internal String Name;
      internal String IdText;
      internal String Description;
      internal String SearchText;
    }

    private readonly String _assetLocation;
    private readonly Boolean _usePts;
    private Assets _assets;
    private DataObjectModel _dom;
    private Boolean _ownsDom;
    private Boolean _prototypeLinksAvailable;
    private volatile Boolean _closing;
    private volatile Boolean _loadInProgress;
    private List<SchemaEntry> _entries = new List<SchemaEntry>();
    private List<SchemaEntry> _visibleEntries = new List<SchemaEntry>();
    private readonly Dictionary<UInt64, List<DomClass>> _fieldOwners = new Dictionary<UInt64, List<DomClass>>();
    private readonly Dictionary<UInt64, List<DomClass>> _componentUsers = new Dictionary<UInt64, List<DomClass>>();
    private readonly Dictionary<UInt64, List<DomField>> _typeUsers = new Dictionary<UInt64, List<DomField>>();
    private readonly Dictionary<UInt64, List<GomObject>> _baseClassPrototypes = new Dictionary<UInt64, List<GomObject>>();
    private readonly List<UInt64> _history = new List<UInt64>();
    private Int32 _historyIndex = -1;
    private Boolean _historyNavigation;
    private UInt64 _pendingDomNavigation;

    private readonly ToolStrip _toolbar;
    private readonly ToolStripButton _openButton;
    private readonly ToolStripButton _backButton;
    private readonly ToolStripButton _forwardButton;
    private readonly ToolStripButton _exportButton;
    private readonly ToolStripButton _loadNodeLinksButton;
    private readonly ToolStripLabel _filterLabel;
    private readonly ToolStripTextBox _filterBox;
    private readonly ToolStripComboBox _kindBox;
    private readonly SplitContainer _split;
    private readonly ListView _list;
    private readonly TabControl _tabs;
    private readonly TreeView _details;
    private readonly TextBox _summary;
    private readonly StatusStrip _status;
    private readonly ToolStripStatusLabel _statusLabel;

    internal DomBrowser(String assetLocation, Boolean usePts) {
      _assetLocation = assetLocation;
      _usePts = usePts;

      Text = "DOM Browser";
      StartPosition = FormStartPosition.CenterParent;
      Size = new Size(1180, 760);
      MinimumSize = new Size(820, 520);
      KeyPreview = true;

      _toolbar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Dock = DockStyle.Fill, Margin = Padding.Empty };
      _openButton = new ToolStripButton("Quick Open") { Enabled = false, ToolTipText = "Open a DOM class, field, enum or association (Ctrl+E)" };
      _openButton.Click += delegate { ShowQuickOpen(); };
      _backButton = new ToolStripButton("Back") { Enabled = false, ToolTipText = "Previous DOM entry (Alt+Left)" };
      _backButton.Click += delegate { NavigateHistory(-1); };
      _forwardButton = new ToolStripButton("Forward") { Enabled = false, ToolTipText = "Next DOM entry (Alt+Right)" };
      _forwardButton.Click += delegate { NavigateHistory(1); };
      _exportButton = new ToolStripButton("Export selected JSON...") { Enabled = false };
      _exportButton.Click += delegate { ExportSelected(); };
      _loadNodeLinksButton = new ToolStripButton("Load node links") {
        Enabled = false,
        ToolTipText = "Load the full prototype/node model on demand. The DOM schema itself does not require it."
      };
      _loadNodeLinksButton.Click += async delegate { await LoadPrototypeLinks(); };
      _filterLabel = new ToolStripLabel("Filter:");
      _filterBox = new ToolStripTextBox { AutoSize = false, Width = 280, ToolTipText = "Name, ID, type or description. Terms are ANDed; prefix a term with - to exclude." };
      _filterBox.TextChanged += delegate { ApplyFilter(); };
      _kindBox = new ToolStripComboBox { DropDownStyle = ComboBoxStyle.DropDownList, AutoSize = false, Width = 120 };
      _kindBox.Items.AddRange(new Object[] { "All", "Classes", "Fields", "Enums", "Associations" });
      _kindBox.SelectedIndex = 0;
      _kindBox.SelectedIndexChanged += delegate { ApplyFilter(); };
      _toolbar.Items.Add(_openButton);
      _toolbar.Items.Add(_backButton);
      _toolbar.Items.Add(_forwardButton);
      _toolbar.Items.Add(_exportButton);
      _toolbar.Items.Add(_loadNodeLinksButton);
      _toolbar.Items.Add(new ToolStripSeparator());
      _toolbar.Items.Add(_filterLabel);
      _toolbar.Items.Add(_filterBox);
      _toolbar.Items.Add(new ToolStripLabel("Type:"));
      _toolbar.Items.Add(_kindBox);

      _split = new SplitContainer {
        Dock = DockStyle.Fill,
        Orientation = Orientation.Vertical,
        SplitterDistance = 500,
        Margin = Padding.Empty
      };

      _list = new ListView {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        GridLines = true,
        HideSelection = false,
        MultiSelect = false,
        HeaderStyle = ColumnHeaderStyle.Clickable,
        VirtualMode = true
      };
      _list.Columns.Add("Type", 90);
      _list.Columns.Add("Name", 220);
      _list.Columns.Add("ID", 170);
      _list.Columns.Add("Description", 420);
      _list.RetrieveVirtualItem += ListRetrieveVirtualItem;
      _list.SelectedIndexChanged += delegate { ShowSelectedDetails(); };
      _list.DoubleClick += delegate { if (_list.SelectedIndices.Count == 1) ShowSelectedDetails(); };
      _split.Panel1.Controls.Add(_list);

      _details = new TreeView {
        Dock = DockStyle.Fill,
        HideSelection = false,
        Font = new Font(FontFamily.GenericMonospace, 9F)
      };
      _details.NodeMouseDoubleClick += DetailsNodeMouseDoubleClick;

      _summary = new TextBox {
        Dock = DockStyle.Fill,
        Multiline = true,
        ReadOnly = true,
        WordWrap = false,
        ScrollBars = ScrollBars.Both,
        Font = new Font(FontFamily.GenericMonospace, 9F)
      };

      _tabs = new TabControl { Dock = DockStyle.Fill };
      TabPage structureTab = new TabPage("Structure");
      structureTab.Controls.Add(_details);
      TabPage summaryTab = new TabPage("Text");
      summaryTab.Controls.Add(_summary);
      _tabs.TabPages.Add(structureTab);
      _tabs.TabPages.Add(summaryTab);
      _split.Panel2.Controls.Add(_tabs);

      _status = new StatusStrip { Dock = DockStyle.Fill, SizingGrip = false, Margin = Padding.Empty };
      _statusLabel = new ToolStripStatusLabel("Loading client.gom...");
      _status.Items.Add(_statusLabel);

      TableLayoutPanel layout = new TableLayoutPanel {
        Dock = DockStyle.Fill,
        ColumnCount = 1,
        RowCount = 3,
        Margin = Padding.Empty,
        Padding = Padding.Empty
      };
      layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
      layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
      layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
      layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
      layout.Controls.Add(_toolbar, 0, 0);
      layout.Controls.Add(_split, 0, 1);
      layout.Controls.Add(_status, 0, 2);
      Controls.Add(layout);

      FormClosing += delegate { _closing = true; };
      FormClosed += delegate { if (!_loadInProgress) DisposeOwnedDom(); };
      Shown += async delegate { await LoadDom(); };
    }

    protected override Boolean ProcessCmdKey(ref Message msg, Keys keyData) {
      if (keyData == (Keys.Control | Keys.E)) { ShowQuickOpen(); return true; }
      if (keyData == (Keys.Control | Keys.F)) { _filterBox.Focus(); _filterBox.SelectAll(); return true; }
      if (keyData == (Keys.Alt | Keys.Left)) { NavigateHistory(-1); return true; }
      if (keyData == (Keys.Alt | Keys.Right)) { NavigateHistory(1); return true; }
      return base.ProcessCmdKey(ref msg, keyData);
    }

    private async Task LoadDom() {
      _loadInProgress = true;
      try {
        UseWaitCursor = true;
        _statusLabel.Text = "Loading client.gom schema...";
        _openButton.Enabled = false;
        _loadNodeLinksButton.Enabled = false;
        _visibleEntries.Clear();
        _list.VirtualListSize = 0;
        _details.Nodes.Clear();
        _summary.Clear();

        await Task.Run(() => {
          _assets = AssetHandler.Instance.GetCurrentAssets(_assetLocation, _usePts);

          // Reuse the shared full DOM when another browser has already loaded it.
          // Otherwise open only client.gom: loading all buckets/prototypes and gameplay
          // helper tables just to inspect the schema can take minutes on a live client.
          if (DomHandler.Instance.CurrentLoaded) {
            _dom = DomHandler.Instance.GetCurrentDOM();
            _ownsDom = false;
          } else {
            _dom = new DataObjectModel(_assets);
            _dom.LoadSchemaOnly();
            _ownsDom = true;
          }

          // Even when the shared full DOM is already available, do not enumerate
          // every prototype during window startup. That reverse index is useful but
          // optional and can contain hundreds of thousands of nodes.
          _prototypeLinksAvailable = false;
          BuildSchemaIndex(false);
        });

        if (_closing || IsDisposed) return;
        ApplyFilter();
        _openButton.Enabled = true;
        _loadNodeLinksButton.Enabled = !_prototypeLinksAvailable;
        Int32 classes = _entries.Count(x => x.Type is DomClass);
        Int32 fields = _entries.Count(x => x.Type is DomField);
        Int32 enums = _entries.Count(x => x.Type is DomEnum);
        Int32 associations = _entries.Count(x => x.Type is DomAssociation);
        _statusLabel.Text = String.Format(CultureInfo.InvariantCulture,
          "{0:N0} DOM entries: {1:N0} classes, {2:N0} fields, {3:N0} enums, {4:N0} associations.{5}",
          _entries.Count, classes, fields, enums, associations,
          _prototypeLinksAvailable ? " Full node links available." : " Schema-only mode; node links load on demand.");
        if (_pendingDomNavigation != 0) {
          UInt64 pending = _pendingDomNavigation;
          _pendingDomNavigation = 0;
          NavigateTo(pending, true);
        }
      }
      catch (Exception ex) {
        if (_closing || IsDisposed) return;
        _statusLabel.Text = "DOM load failed.";
        MessageBox.Show(this, ex.GetBaseException().Message, "DOM Browser", MessageBoxButtons.OK, MessageBoxIcon.Error);
      }
      finally {
        _loadInProgress = false;
        if (_closing || IsDisposed) DisposeOwnedDom();
        else UseWaitCursor = false;
      }
    }

    private void DisposeOwnedDom() {
      if (!_ownsDom || _dom == null) return;
      DataObjectModel owned = _dom;
      _dom = null;
      _ownsDom = false;
      try { owned.Dispose(); } catch { }
    }

    private async Task LoadPrototypeLinks() {
      if (_prototypeLinksAvailable || _assets == null || _closing) return;
      try {
        _loadNodeLinksButton.Enabled = false;
        _statusLabel.Text = "Loading full node/prototype links on demand...";
        DataObjectModel fullDom = await Task.Run(() => DomHandler.Instance.GetCurrentDOM(_assets));
        if (_closing || IsDisposed) return;

        Dictionary<UInt64, List<GomObject>> prototypeIndex = await Task.Run(() => CreatePrototypeIndex(fullDom));
        if (_closing || IsDisposed) return;
        ReplacePrototypeIndex(prototypeIndex);
        _prototypeLinksAvailable = true;
        _statusLabel.Text = "Full prototype/node links loaded.";

        SchemaEntry selected = GetSelectedEntry();
        if (selected?.Type is DomClass) ShowEntry(selected.Type, false);
      }
      catch (Exception ex) {
        if (_closing || IsDisposed) return;
        _loadNodeLinksButton.Enabled = true;
        _statusLabel.Text = "Could not load full node links.";
        MessageBox.Show(this, ex.GetBaseException().Message, "DOM Browser", MessageBoxButtons.OK, MessageBoxIcon.Error);
      }
    }

    private void BuildSchemaIndex(Boolean includePrototypeLinks) {
      _fieldOwners.Clear();
      _componentUsers.Clear();
      _typeUsers.Clear();
      _baseClassPrototypes.Clear();

      List<DomType> schema = _dom?.DomTypeMap?.Values
        .Where(x => x is DomClass || x is DomField || x is DomEnum || x is DomAssociation)
        .ToList() ?? new List<DomType>();

      foreach (DomClass cls in schema.OfType<DomClass>()) {
        foreach (DomField field in cls.Fields ?? new List<DomField>()) AddMapItem(_fieldOwners, field?.Id ?? 0, cls);
        foreach (DomClass component in cls.Components ?? new List<DomClass>()) AddMapItem(_componentUsers, component?.Id ?? 0, cls);
      }

      foreach (DomField field in schema.OfType<DomField>()) {
        HashSet<UInt64> references = new HashSet<UInt64>();
        CollectTypeReferences(field.GomType, references);
        foreach (UInt64 id in references) AddMapItem(_typeUsers, id, field);
      }

      if (includePrototypeLinks) ReplacePrototypeIndex(CreatePrototypeIndex(_dom));

      _entries = schema.Select(type => {
        String kind = TypeKind(type);
        String name = String.IsNullOrWhiteSpace(type.Name) ? "<unnamed>" : type.Name;
        String id = FormatId(type.Id);
        String description = type.Description ?? String.Empty;
        String extra = type is DomField field ? DescribeGomType(field.GomType) : String.Empty;
        return new SchemaEntry {
          Type = type,
          Kind = kind,
          Name = name,
          IdText = id,
          Description = description,
          SearchText = kind + " " + name + " " + id + " " + description + " " + extra
        };
      }).OrderBy(x => x.Kind, StringComparer.OrdinalIgnoreCase)
        .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
        .ToList();
    }

    private static Dictionary<UInt64, List<GomObject>> CreatePrototypeIndex(DataObjectModel sourceDom) {
      Dictionary<UInt64, List<GomObject>> result = new Dictionary<UInt64, List<GomObject>>();
      if (sourceDom?.NodeLookup == null) return result;
      if (!sourceDom.NodeLookup.TryGetValue(typeof(GomObject), out Dictionary<String, DomType> objects)) return result;
      foreach (GomObject obj in objects.Values.OfType<GomObject>()) {
        if (obj?.DomClass != null) AddMapItem(result, obj.DomClass.Id, obj);
      }
      return result;
    }

    private void ReplacePrototypeIndex(Dictionary<UInt64, List<GomObject>> source) {
      _baseClassPrototypes.Clear();
      if (source == null) return;
      foreach (KeyValuePair<UInt64, List<GomObject>> pair in source)
        _baseClassPrototypes[pair.Key] = pair.Value;
    }

    private void ListRetrieveVirtualItem(Object sender, RetrieveVirtualItemEventArgs e) {
      if (e.ItemIndex < 0 || e.ItemIndex >= _visibleEntries.Count) {
        e.Item = new ListViewItem(String.Empty);
        return;
      }
      SchemaEntry entry = _visibleEntries[e.ItemIndex];
      ListViewItem item = new ListViewItem(entry.Kind) { Tag = entry };
      item.SubItems.Add(entry.Name);
      item.SubItems.Add(entry.IdText);
      item.SubItems.Add(entry.Description);
      e.Item = item;
    }

    private SchemaEntry GetSelectedEntry() {
      if (_list.SelectedIndices.Count != 1) return null;
      Int32 index = _list.SelectedIndices[0];
      return index >= 0 && index < _visibleEntries.Count ? _visibleEntries[index] : null;
    }

    private static void AddMapItem<T>(Dictionary<UInt64, List<T>> map, UInt64 id, T value) where T : class {
      if (id == 0 || value == null) return;
      if (!map.TryGetValue(id, out List<T> list)) {
        list = new List<T>();
        map[id] = list;
      }
      if (!list.Contains(value)) list.Add(value);
    }

    private static void CollectTypeReferences(GomType type, ISet<UInt64> references) {
      if (type == null || references == null) return;
      switch (type) {
        case GomLib.GomTypes.Enum enumType:
          if (enumType.DomEnumId != 0) references.Add(enumType.DomEnumId);
          break;
        case GomLib.GomTypes.EmbeddedClass embedded:
          if (embedded.DomClassId != 0) references.Add(embedded.DomClassId);
          break;
        case GomLib.GomTypes.ClassRef classRef:
          if (classRef.DomClassId != 0) references.Add(classRef.DomClassId);
          break;
        case GomLib.GomTypes.Script script:
          if (script.DomClassId != 0) references.Add(script.DomClassId);
          break;
        case GomLib.GomTypes.List list:
          CollectTypeReferences(list.ContainedType, references);
          break;
        case GomLib.GomTypes.Map map:
          CollectTypeReferences(map.KeyType, references);
          CollectTypeReferences(map.ValueType, references);
          break;
        case GomLib.GomTypes.Tuple tuple:
          foreach (GomType element in tuple.ElementTypes) CollectTypeReferences(element, references);
          break;
      }
    }

    private void ApplyFilter() {
      if (_entries == null || _list == null) return;
      String selectedKind = _kindBox.SelectedItem as String ?? "All";
      String query = (_filterBox.Text ?? String.Empty).Trim();
      String[] rawTerms = query.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
      String[] include = rawTerms.Where(x => !x.StartsWith("-", StringComparison.Ordinal) || x.Length == 1).ToArray();
      String[] exclude = rawTerms.Where(x => x.StartsWith("-", StringComparison.Ordinal) && x.Length > 1).Select(x => x.Substring(1)).ToArray();

      IEnumerable<SchemaEntry> result = _entries;
      if (!String.Equals(selectedKind, "All", StringComparison.OrdinalIgnoreCase)) {
        String singular = selectedKind switch {
          "Classes" => "Class",
          "Fields" => "Field",
          "Enums" => "Enum",
          "Associations" => "Association",
          _ => selectedKind
        };
        result = result.Where(x => x.Kind.Equals(singular, StringComparison.OrdinalIgnoreCase));
      }
      if (include.Length != 0)
        result = result.Where(x => include.All(term => x.SearchText.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0));
      if (exclude.Length != 0)
        result = result.Where(x => exclude.All(term => x.SearchText.IndexOf(term, StringComparison.OrdinalIgnoreCase) < 0));

      _visibleEntries = result.ToList();
      _list.BeginUpdate();
      try {
        if (_list.SelectedIndices.Count > 0) _list.Items[_list.SelectedIndices[0]].Selected = false;
        _list.VirtualListSize = _visibleEntries.Count;
        _list.Invalidate();
      }
      finally { _list.EndUpdate(); }
      _statusLabel.Text = _visibleEntries.Count.ToString("N0", CultureInfo.InvariantCulture) + " of "
        + _entries.Count.ToString("N0", CultureInfo.InvariantCulture) + " DOM entries shown.";
    }

    private void ShowSelectedDetails() {
      SchemaEntry entry = GetSelectedEntry();
      if (entry == null) {
        _details.Nodes.Clear();
        _summary.Clear();
        _exportButton.Enabled = false;
        return;
      }
      ShowEntry(entry.Type, true);
    }

    private void ShowEntry(DomType type, Boolean recordHistory) {
      if (type == null) return;
      SchemaEntry entry = _entries.FirstOrDefault(x => x.Type.Id == type.Id && x.Type.GetType() == type.GetType());
      if (entry == null) return;

      _details.BeginUpdate();
      try {
        _details.Nodes.Clear();
        TreeNode root = new TreeNode(entry.Kind + ": " + entry.Name) { Tag = type };
        root.Nodes.Add("ID: " + FormatId(type.Id));
        if (!String.IsNullOrWhiteSpace(type.Description)) root.Nodes.Add("Description: " + type.Description);

        if (type is DomClass cls) BuildClassDetails(root, cls);
        else if (type is DomField field) BuildFieldDetails(root, field);
        else if (type is DomEnum domEnum) BuildEnumDetails(root, domEnum);
        else if (type is DomAssociation association) BuildAssociationDetails(root, association);

        _details.Nodes.Add(root);
        root.Expand();
      }
      finally { _details.EndUpdate(); }

      _summary.Text = BuildTextSummary(type);
      _exportButton.Enabled = true;
      Text = "DOM Browser - " + entry.Name;
      SelectListEntry(type.Id, false);
      if (recordHistory && !_historyNavigation) RecordHistory(type.Id);
      UpdateHistoryButtons();
    }

    private void BuildClassDetails(TreeNode root, DomClass cls) {
      TreeNode metadata = root.Nodes.Add("Raw class metadata");
      metadata.Nodes.Add("Archetype: " + cls.Archetype.ToString(CultureInfo.InvariantCulture)
        + " (0x" + cls.Archetype.ToString("X4", CultureInfo.InvariantCulture) + ")");
      if (cls.ScriptMethodId1 != 0 || cls.ScriptMethodId2 != 0) {
        metadata.Nodes.Add("Script method ID 1: " + FormatId(cls.ScriptMethodId1));
        metadata.Nodes.Add("Script method ID 2: " + FormatId(cls.ScriptMethodId2));
      }

      List<DomClass> components = (cls.Components ?? new List<DomClass>()).Where(x => x != null).ToList();
      TreeNode componentRoot = root.Nodes.Add("Components (" + components.Count.ToString(CultureInfo.InvariantCulture) + ")");
      foreach (DomClass component in components.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
        componentRoot.Nodes.Add(LinkNode(component.Name, component));

      List<DomField> fields = (cls.Fields ?? new List<DomField>()).Where(x => x != null).ToList();
      TreeNode fieldRoot = root.Nodes.Add("Declared fields (" + fields.Count.ToString(CultureInfo.InvariantCulture) + ")");
      foreach (DomField field in fields.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)) {
        TreeNode node = LinkNode((field.Name ?? "<unnamed>") + " : " + DescribeGomType(field.GomType), field);
        if (!String.IsNullOrWhiteSpace(field.Description)) node.Nodes.Add(field.Description);
        fieldRoot.Nodes.Add(node);
      }

      List<(DomField Field, DomClass Source)> effective = new List<(DomField, DomClass)>();
      AddEffectiveFields(cls, cls, effective, new HashSet<UInt64>());
      TreeNode effectiveRoot = root.Nodes.Add("Effective fields (" + effective.Count.ToString(CultureInfo.InvariantCulture) + ")");
      foreach ((DomField Field, DomClass Source) item in effective.OrderBy(x => x.Field.Name, StringComparer.OrdinalIgnoreCase)) {
        String source = item.Source == cls ? "declared here" : "via " + (item.Source.Name ?? FormatId(item.Source.Id));
        effectiveRoot.Nodes.Add(LinkNode((item.Field.Name ?? "<unnamed>") + " : " + DescribeGomType(item.Field.GomType) + "   [" + source + "]", item.Field));
      }

      if (_componentUsers.TryGetValue(cls.Id, out List<DomClass> users)) {
        TreeNode usedBy = root.Nodes.Add("Used as component by (" + users.Count.ToString(CultureInfo.InvariantCulture) + ")");
        foreach (DomClass user in users.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)) usedBy.Nodes.Add(LinkNode(user.Name, user));
      }

      if (_typeUsers.TryGetValue(cls.Id, out List<DomField> typeUsers)) {
        TreeNode referenced = root.Nodes.Add("Referenced by field types (" + typeUsers.Count.ToString(CultureInfo.InvariantCulture) + ")");
        foreach (DomField field in typeUsers.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
          referenced.Nodes.Add(LinkNode((field.Name ?? "<unnamed>") + " : " + DescribeGomType(field.GomType), field));
      }

      if (_baseClassPrototypes.TryGetValue(cls.Id, out List<GomObject> prototypes)) {
        TreeNode protoRoot = root.Nodes.Add("Direct prototype instances (" + prototypes.Count.ToString("N0", CultureInfo.InvariantCulture) + ")");
        Int32 shown = 0;
        foreach (GomObject obj in prototypes.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)) {
          if (shown++ >= 500) { protoRoot.Nodes.Add("… " + (prototypes.Count - 500).ToString("N0", CultureInfo.InvariantCulture) + " more"); break; }
          protoRoot.Nodes.Add(new TreeNode((obj.Name ?? "<unnamed>") + "   " + FormatId(obj.Id)) { Tag = obj });
        }
      } else if (!_prototypeLinksAvailable) {
        root.Nodes.Add("Direct prototype instances: not loaded (use 'Load node links' to enable)");
      }
    }

    private static void AddEffectiveFields(DomClass current, DomClass source, List<(DomField Field, DomClass Source)> result, HashSet<UInt64> seenClasses) {
      if (current == null || !seenClasses.Add(current.Id)) return;
      foreach (DomField field in current.Fields ?? new List<DomField>()) {
        if (field == null || result.Any(x => x.Field.Id == field.Id)) continue;
        result.Add((field, source));
      }
      foreach (DomClass component in current.Components ?? new List<DomClass>())
        AddEffectiveFields(component, component, result, seenClasses);
    }

    private void BuildFieldDetails(TreeNode root, DomField field) {
      TreeNode metadata = root.Nodes.Add("Raw field metadata");
      metadata.Nodes.Add("Modifiers: " + field.Modifiers.ToString(CultureInfo.InvariantCulture)
        + " (0x" + field.Modifiers.ToString("X4", CultureInfo.InvariantCulture) + ")");
      metadata.Nodes.Add("Serialized type length: " + field.SerializedTypeLength.ToString(CultureInfo.InvariantCulture));
      metadata.Nodes.Add("Serialized type offset: 0x" + field.SerializedTypeOffset.ToString("X4", CultureInfo.InvariantCulture));

      TreeNode typeRoot = root.Nodes.Add("GOM type: " + DescribeGomType(field.GomType));
      AddGomTypeTree(typeRoot, field.GomType, 0);

      if (_fieldOwners.TryGetValue(field.Id, out List<DomClass> owners)) {
        TreeNode ownerRoot = root.Nodes.Add("Declared by classes (" + owners.Count.ToString(CultureInfo.InvariantCulture) + ")");
        foreach (DomClass owner in owners.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)) ownerRoot.Nodes.Add(LinkNode(owner.Name, owner));
      }

      HashSet<UInt64> refs = new HashSet<UInt64>();
      CollectTypeReferences(field.GomType, refs);
      if (refs.Count > 0) {
        TreeNode refRoot = root.Nodes.Add("Referenced DOM types (" + refs.Count.ToString(CultureInfo.InvariantCulture) + ")");
        foreach (UInt64 id in refs) {
          DomType referenced = _dom.Get<DomType>(id);
          if (referenced != null) refRoot.Nodes.Add(LinkNode((referenced.Name ?? "<unnamed>") + "   " + TypeKind(referenced), referenced));
          else refRoot.Nodes.Add("Unresolved " + FormatId(id));
        }
      }
    }

    private void BuildEnumDetails(TreeNode root, DomEnum domEnum) {
      List<String> names = domEnum.names ?? new List<String>();
      TreeNode values = root.Nodes.Add("Values (" + names.Count.ToString(CultureInfo.InvariantCulture) + ")");
      for (Int32 i = 0; i < names.Count; i++)
        values.Nodes.Add(i.ToString(CultureInfo.InvariantCulture) + " (stored " + (i + 1).ToString(CultureInfo.InvariantCulture) + ") = " + names[i]);

      if (_typeUsers.TryGetValue(domEnum.Id, out List<DomField> users)) {
        TreeNode usedBy = root.Nodes.Add("Used by fields (" + users.Count.ToString(CultureInfo.InvariantCulture) + ")");
        foreach (DomField field in users.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
          usedBy.Nodes.Add(LinkNode((field.Name ?? "<unnamed>") + " : " + DescribeGomType(field.GomType), field));
      }
    }

    private void BuildAssociationDetails(TreeNode root, DomAssociation association) {
      if (_typeUsers.TryGetValue(association.Id, out List<DomField> users)) {
        TreeNode usedBy = root.Nodes.Add("Used by fields (" + users.Count.ToString(CultureInfo.InvariantCulture) + ")");
        foreach (DomField field in users.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)) usedBy.Nodes.Add(LinkNode(field.Name, field));
      } else {
        root.Nodes.Add("No direct field references were resolved in the loaded client DOM.");
      }
    }

    private void AddGomTypeTree(TreeNode parent, GomType type, Int32 depth) {
      if (parent == null || type == null || depth > 16) return;
      parent.Nodes.Add("Type ID: " + type.TypeId + " (0x" + ((Byte)type.TypeId).ToString("X2", CultureInfo.InvariantCulture) + ")");
      switch (type) {
        case GomLib.GomTypes.Enum enumType:
          AddReferencedTypeNode(parent, "Enum", enumType.DomEnumId);
          break;
        case GomLib.GomTypes.EmbeddedClass embedded:
          AddReferencedTypeNode(parent, "Embedded class", embedded.DomClassId);
          break;
        case GomLib.GomTypes.ClassRef classRef:
          AddReferencedTypeNode(parent, "Class reference", classRef.DomClassId);
          break;
        case GomLib.GomTypes.Script script:
          if (script.DomClassId != 0) AddReferencedTypeNode(parent, "Script class", script.DomClassId);
          break;
        case GomLib.GomTypes.List list:
          TreeNode item = parent.Nodes.Add("Element: " + DescribeGomType(list.ContainedType));
          AddGomTypeTree(item, list.ContainedType, depth + 1);
          break;
        case GomLib.GomTypes.Map map:
          TreeNode key = parent.Nodes.Add("Key: " + DescribeGomType(map.KeyType));
          AddGomTypeTree(key, map.KeyType, depth + 1);
          TreeNode value = parent.Nodes.Add("Value: " + DescribeGomType(map.ValueType));
          AddGomTypeTree(value, map.ValueType, depth + 1);
          break;
        case GomLib.GomTypes.Tuple tuple:
          for (Int32 i = 0; i < tuple.ElementTypes.Count; i++) {
            GomType element = tuple.ElementTypes[i];
            TreeNode node = parent.Nodes.Add("Element " + i.ToString(CultureInfo.InvariantCulture) + ": " + DescribeGomType(element));
            AddGomTypeTree(node, element, depth + 1);
          }
          break;
      }
    }

    private void AddReferencedTypeNode(TreeNode parent, String role, UInt64 id) {
      DomType referenced = id == 0 ? null : _dom.Get<DomType>(id);
      if (referenced != null) parent.Nodes.Add(LinkNode(role + ": " + (referenced.Name ?? "<unnamed>") + "   " + FormatId(id), referenced));
      else parent.Nodes.Add(role + ": " + FormatId(id));
    }

    private static TreeNode LinkNode(String text, DomType target) => new TreeNode(text ?? String.Empty) { Tag = target };

    private void DetailsNodeMouseDoubleClick(Object sender, TreeNodeMouseClickEventArgs e) {
      if (e?.Node?.Tag is DomType type) { ShowEntry(type, true); return; }
      if (e?.Node?.Tag is GomObject prototype)
        BrowserNavigation.OpenNode(this, _assetLocation, _usePts, prototype.Name ?? prototype.Id.ToString(CultureInfo.InvariantCulture), true);
    }

    internal Boolean MatchesDomSource(String gamePath, Boolean usePts) {
      if (_usePts != usePts) return false;
      try {
        return String.Equals(Assets.NormalizeGamePath(_assetLocation), Assets.NormalizeGamePath(gamePath), StringComparison.OrdinalIgnoreCase);
      } catch {
        return String.Equals(_assetLocation?.TrimEnd('\\', '/'), gamePath?.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
      }
    }

    internal void NavigateOrQueueDom(UInt64 domTypeId) {
      if (domTypeId == 0) return;
      if (_entries != null && _entries.Count > 0) { NavigateTo(domTypeId, true); return; }
      _pendingDomNavigation = domTypeId;
      _statusLabel.Text = "Waiting for DOM schema to open " + FormatId(domTypeId) + " ...";
    }

    private void ShowQuickOpen() {
      if (_entries == null || _entries.Count == 0) return;
      List<QuickOpenDialog.Item> items = _entries.Select(entry => new QuickOpenDialog.Item(
        entry.Type.Id.ToString(CultureInfo.InvariantCulture),
        entry.Name,
        entry.Kind + "   " + entry.IdText,
        entry.SearchText
      )).ToList();
      using QuickOpenDialog dialog = new QuickOpenDialog("Open DOM entry", "Class, field, enum, association, name or ID...", items);
      if (dialog.ShowDialog(this) != DialogResult.OK || dialog.SelectedItem == null) return;
      if (UInt64.TryParse(dialog.SelectedItem.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out UInt64 id)) NavigateTo(id, true);
    }

    private void NavigateTo(UInt64 id, Boolean recordHistory) {
      SchemaEntry entry = _entries.FirstOrDefault(x => x.Type.Id == id);
      if (entry == null) return;
      if (!String.IsNullOrWhiteSpace(_filterBox.Text) || _kindBox.SelectedIndex != 0) {
        _filterBox.Clear();
        _kindBox.SelectedIndex = 0;
        ApplyFilter();
      }
      SelectListEntry(id, true);
      ShowEntry(entry.Type, recordHistory);
    }

    private void SelectListEntry(UInt64 id, Boolean ensureVisible) {
      Int32 index = _visibleEntries.FindIndex(entry => entry.Type.Id == id);
      if (index < 0 || index >= _list.VirtualListSize) return;
      if (_list.SelectedIndices.Count > 0 && _list.SelectedIndices[0] != index)
        _list.Items[_list.SelectedIndices[0]].Selected = false;
      _list.Items[index].Selected = true;
      _list.Items[index].Focused = true;
      if (ensureVisible) _list.EnsureVisible(index);
    }

    private void RecordHistory(UInt64 id) {
      if (_historyIndex >= 0 && _historyIndex < _history.Count && _history[_historyIndex] == id) return;
      if (_historyIndex + 1 < _history.Count) _history.RemoveRange(_historyIndex + 1, _history.Count - _historyIndex - 1);
      _history.Add(id);
      if (_history.Count > 200) _history.RemoveAt(0);
      _historyIndex = _history.Count - 1;
      UpdateHistoryButtons();
    }

    private void NavigateHistory(Int32 direction) {
      Int32 target = _historyIndex + direction;
      if (target < 0 || target >= _history.Count) return;
      _historyNavigation = true;
      try {
        NavigateTo(_history[target], false);
        _historyIndex = target;
      }
      finally { _historyNavigation = false; }
      UpdateHistoryButtons();
    }

    private void UpdateHistoryButtons() {
      _backButton.Enabled = _historyIndex > 0;
      _forwardButton.Enabled = _historyIndex >= 0 && _historyIndex + 1 < _history.Count;
    }

    private void ExportSelected() {
      SchemaEntry entry = GetSelectedEntry();
      if (entry == null) return;
      using SaveFileDialog dialog = new SaveFileDialog {
        AddExtension = true,
        DefaultExt = "json",
        FileName = SafeFileName(entry.Name) + ".json",
        Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*"
      };
      if (dialog.ShowDialog(this) != DialogResult.OK) return;
      try {
        String json = JsonConvert.SerializeObject(BuildJsonEntry(entry.Type), Formatting.Indented);
        System.IO.File.WriteAllText(dialog.FileName, json, new System.Text.UTF8Encoding(false));
        _statusLabel.Text = "Exported " + entry.Name + " as JSON.";
      }
      catch (Exception ex) {
        MessageBox.Show(this, ex.Message, "DOM JSON Export", MessageBoxButtons.OK, MessageBoxIcon.Error);
      }
    }

    private Object BuildJsonEntry(DomType type) {
      Object common = new {
        kind = TypeKind(type),
        name = type.Name,
        id = type.Id,
        idHex = "0x" + type.Id.ToString("X16", CultureInfo.InvariantCulture),
        description = type.Description
      };
      if (type is DomClass cls) {
        List<(DomField Field, DomClass Source)> effective = new List<(DomField, DomClass)>();
        AddEffectiveFields(cls, cls, effective, new HashSet<UInt64>());
        return new {
          schema = common,
          rawMetadata = new { archetype = cls.Archetype, scriptMethodId1 = cls.ScriptMethodId1, scriptMethodId2 = cls.ScriptMethodId2 },
          components = (cls.Components ?? new List<DomClass>()).Where(x => x != null).Select(DomLink).ToArray(),
          declaredFields = (cls.Fields ?? new List<DomField>()).Where(x => x != null).Select(field => new { schema = DomLink(field), gomType = DescribeGomType(field.GomType), modifiers = field.Modifiers, serializedTypeLength = field.SerializedTypeLength, serializedTypeOffset = field.SerializedTypeOffset }).ToArray(),
          effectiveFields = effective.Select(x => new { field = DomLink(x.Field), sourceClass = DomLink(x.Source), gomType = DescribeGomType(x.Field.GomType) }).ToArray(),
          usedAsComponentBy = _componentUsers.TryGetValue(cls.Id, out List<DomClass> componentUsers) ? componentUsers.Select(DomLink).ToArray() : Array.Empty<Object>(),
          directPrototypeCount = _baseClassPrototypes.TryGetValue(cls.Id, out List<GomObject> prototypes) ? prototypes.Count : 0
        };
      }
      if (type is DomField fieldType) {
        HashSet<UInt64> refs = new HashSet<UInt64>();
        CollectTypeReferences(fieldType.GomType, refs);
        return new {
          schema = common,
          rawMetadata = new { modifiers = fieldType.Modifiers, serializedTypeLength = fieldType.SerializedTypeLength, serializedTypeOffset = fieldType.SerializedTypeOffset },
          gomType = DescribeGomType(fieldType.GomType),
          declaredBy = _fieldOwners.TryGetValue(fieldType.Id, out List<DomClass> owners) ? owners.Select(DomLink).ToArray() : Array.Empty<Object>(),
          referencedDomTypes = refs.Select(id => _dom.Get<DomType>(id)).Where(x => x != null).Select(DomLink).ToArray()
        };
      }
      if (type is DomEnum enumType) {
        return new {
          schema = common,
          values = (enumType.names ?? new List<String>()).Select((name, index) => new { index, storedValue = index + 1, name }).ToArray(),
          usedBy = _typeUsers.TryGetValue(enumType.Id, out List<DomField> users) ? users.Select(DomLink).ToArray() : Array.Empty<Object>()
        };
      }
      return new { schema = common };
    }

    private static Object DomLink(DomType type) => type == null ? null : new {
      type = TypeKind(type),
      name = type.Name,
      id = type.Id,
      idHex = "0x" + type.Id.ToString("X16", CultureInfo.InvariantCulture)
    };

    private String BuildTextSummary(DomType type) {
      System.Text.StringBuilder sb = new System.Text.StringBuilder();
      sb.AppendLine(TypeKind(type) + " " + (type.Name ?? "<unnamed>"));
      sb.AppendLine("ID: " + FormatId(type.Id));
      if (!String.IsNullOrWhiteSpace(type.Description)) sb.AppendLine("Description: " + type.Description);
      sb.AppendLine();
      if (type is DomClass cls) {
        sb.AppendLine("Archetype: " + cls.Archetype.ToString(CultureInfo.InvariantCulture) + " (0x" + cls.Archetype.ToString("X4", CultureInfo.InvariantCulture) + ")");
        if (cls.ScriptMethodId1 != 0 || cls.ScriptMethodId2 != 0) {
          sb.AppendLine("Script method ID 1: " + FormatId(cls.ScriptMethodId1));
          sb.AppendLine("Script method ID 2: " + FormatId(cls.ScriptMethodId2));
        }
        sb.AppendLine();
        sb.AppendLine("Components:");
        foreach (DomClass component in cls.Components ?? new List<DomClass>()) sb.AppendLine("  " + component?.Name + "  " + FormatId(component?.Id ?? 0));
        sb.AppendLine();
        sb.AppendLine("Declared fields:");
        foreach (DomField field in cls.Fields ?? new List<DomField>()) sb.AppendLine("  " + field?.Name + " : " + DescribeGomType(field?.GomType) + "  " + FormatId(field?.Id ?? 0));
      } else if (type is DomField field) {
        sb.AppendLine("GOM type: " + DescribeGomType(field.GomType));
        sb.AppendLine("Modifiers: " + field.Modifiers.ToString(CultureInfo.InvariantCulture) + " (0x" + field.Modifiers.ToString("X4", CultureInfo.InvariantCulture) + ")");
        sb.AppendLine("Serialized type length: " + field.SerializedTypeLength.ToString(CultureInfo.InvariantCulture));
        sb.AppendLine("Serialized type offset: 0x" + field.SerializedTypeOffset.ToString("X4", CultureInfo.InvariantCulture));
        if (_fieldOwners.TryGetValue(field.Id, out List<DomClass> owners)) {
          sb.AppendLine(); sb.AppendLine("Declared by:");
          foreach (DomClass owner in owners) sb.AppendLine("  " + owner.Name + "  " + FormatId(owner.Id));
        }
      } else if (type is DomEnum domEnum) {
        sb.AppendLine("Values:");
        for (Int32 i = 0; i < (domEnum.names?.Count ?? 0); i++) sb.AppendLine("  " + i.ToString(CultureInfo.InvariantCulture) + " / stored " + (i + 1).ToString(CultureInfo.InvariantCulture) + " = " + domEnum.names[i]);
      }
      return sb.ToString();
    }

    private static String DescribeGomType(GomType type) => type?.ToString() ?? "unknown";

    private static String TypeKind(DomType type) {
      if (type is DomClass) return "Class";
      if (type is DomField) return "Field";
      if (type is DomEnum) return "Enum";
      if (type is DomAssociation) return "Association";
      if (type is GomObject) return "Prototype";
      return type?.GetType().Name ?? "Unknown";
    }

    private static String FormatId(UInt64 id) => id.ToString(CultureInfo.InvariantCulture)
      + " (0x" + id.ToString("X16", CultureInfo.InvariantCulture) + ")";

    private static String SafeFileName(String value) {
      String result = String.IsNullOrWhiteSpace(value) ? "dom-entry" : value.Trim();
      foreach (Char c in System.IO.Path.GetInvalidFileNameChars()) result = result.Replace(c, '_');
      return result;
    }
  }
}
