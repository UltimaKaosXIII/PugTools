using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using FileFormats;
using GomLib;
using GomLib.Models;
using SlimDX;
using TorArchive;
using File = TorArchive.File;
//using TreeViewFast.Controls;

namespace PugTools {
  public partial class WorldBrowser : Form, IMessageFilter {
    public bool _closing = false;
    public FileFormats.Area area;
    private readonly Dictionary<string, NodeAsset> assetDict = new Dictionary<string, NodeAsset>();
    public Assets currentAssets;
    private DataObjectModel currentDom;
    private readonly Dictionary<string, NodeAsset> dataviewDict = new Dictionary<string, NodeAsset>();
    private bool showDBO = false;
    private HashFileInfo info;
    private ulong currentAreaId;
    public Dictionary<string, GR2_Material> materials = new Dictionary<string, GR2_Material>();
    public Dictionary<ulong, GR2> models = new Dictionary<ulong, GR2>();
    List<object> mapAreaData = new List<object>();
    private readonly List<string> nodeKeys = new List<string>();
    private View_AREA panelRender;
    private Thread render;
    private readonly WorldRenderSettings worldSettings = new WorldRenderSettings();
    private ToolStrip worldToolbar;
    private ToolStripComboBox cmbWorldMode;
    private ToolStripComboBox cmbWorldBackend;
    private ToolStripComboBox cmbWorldAntiAliasing;
    private ToolStripComboBox cmbWorldViewDistance;
    private ToolStrip worldListToolbar;
    private ToolStripTextBox txtWorldSearch;
    private ToolStripButton btnWorldMiniMap;
    private ToolStripButton btnWorldDecorationHooks;
    private ToolStripButton btnWorldInspectModel;
    private ToolStripDropDownButton btnWorldUtilities;
    private ToolStripDropDownButton btnWorldNpcs;
    private ToolStripButton btnWorldWalkingMode;
    private bool worldModelInspectPending;
    private readonly Dictionary<string, GR2> worldUtilityModels = new Dictionary<string, GR2>(StringComparer.OrdinalIgnoreCase);
    private readonly List<WorldNpcPlacement> worldNpcPlacements = new List<WorldNpcPlacement>();
    private readonly List<WorldSpnPlacement> worldSpnPlacements = new List<WorldSpnPlacement>();
    private Panel miniMapPanel;
    private Panel miniMapTitleBar;
    private Label miniMapTitle;
    private Button miniMapClose;
    private PictureBox miniMapPicture;
    private Panel miniMapResizeGrip;
    private Bitmap miniMapImage;
    private float miniMapMinX, miniMapMaxX, miniMapMinZ, miniMapMaxZ;
    private float miniMapZoom = 1f;
    private float miniMapCenterX, miniMapCenterZ;
    private const float MiniMapMaxZoom = 32f;
    private bool miniMapMoving, miniMapResizing, miniMapPanning, miniMapSuppressClick;
    private bool worldFullMapActive;
    private Point miniMapDragStart, miniMapPanelStart, miniMapPanStart;
    private Size miniMapResizeStart;
    private float miniMapPanStartCenterX, miniMapPanStartCenterZ;
    private System.Windows.Forms.Timer worldOverlayTimer;
    private ToolStripStatusLabel toolStripPositionStatus;
    private ToolStripStatusLabel toolStripPhaseStatus;
    private ToolStripStatusLabel toolStripRoomStatus;
    private Label phaseBannerLabel;
    private Panel worldLoadingOverlay;
    private Panel worldLoadingCard;
    private Label worldLoadingTitle;
    private Label worldLoadingDetails;
    private ProgressBar worldLoadingProgress;
    private string lastPhaseBannerText = String.Empty;
    private bool updatingWorldToolbar;
    private volatile bool worldWindowActive;
    private volatile bool worldTextInputActive;
    private volatile bool worldRenderInputActive;
    private const int WM_MOUSEWHEEL = 0x020A;

    internal bool WorldWindowInputEnabled => !_closing && worldWindowActive && !worldTextInputActive;
    internal bool WorldKeyboardInputEnabled => WorldWindowInputEnabled && worldRenderInputActive;


    public WorldBrowser(string assetLocation, bool usePTS) {
      InitializeComponent();
      Activated += (_, __) => worldWindowActive = true;
      Deactivate += (_, __) => worldWindowActive = false;
      renderPanel.MouseEnter += (_, __) => ActivateWorldRenderInput();
      renderPanel.MouseDown += RenderPanel_MouseDown;
      treeViewFast1.MouseEnter += (_, __) => worldRenderInputActive = false;
      treeViewFast1.MouseDown += (_, __) => worldRenderInputActive = false;
      Application.AddMessageFilter(this);
      InitializeWorldListToolbar();
      InitializeWorldRenderToolbar();
      InitializeWorldMiniMap();
      InitializeWorldRoomStatus();
      InitializeWorldLoadingOverlay();

      List<object> args = new List<object>
            {
                assetLocation,
                usePTS
            };
      toolStripProgressBar1.Visible = true;
      toolStripStatusLabel1.Text = "Loading Assets";
      DisableUI();
      backgroundWorker1.RunWorkerAsync(args);
    }

    private Dictionary<ulong, string> mapAreas = new Dictionary<ulong, string>();
    private Dictionary<string, NodeAsset> loadedAssetDict;
    private Dictionary<ulong, WorldAreaOverride> worldAreaOverrides = new Dictionary<ulong, WorldAreaOverride>();

    private static string ReadAreaInternalName(File areaFile) {
      if (areaFile == null) return null;
      try {
        using Stream stream = areaFile.OpenCopyInMemory();
        using var br = new BinaryReader(stream);
        if (br.BaseStream.Length < 0x3C || br.ReadUInt32() != 0x18) return null;
        br.BaseStream.Position = 0x38;
        uint settingsOffset = br.ReadUInt32();
        if (settingsOffset == 0 || settingsOffset >= br.BaseStream.Length) return null;
        br.BaseStream.Position = settingsOffset;
        if (br.BaseStream.Position + 4 > br.BaseStream.Length) return null;
        uint environmentMaterialCount = br.ReadUInt32();
        for (uint i = 0; i < environmentMaterialCount; i++) {
          if (br.BaseStream.Position + 8 > br.BaseStream.Length) return null;
          br.ReadUInt32();
          uint materialNameLength = br.ReadUInt32();
          if (materialNameLength > br.BaseStream.Length - br.BaseStream.Position) return null;
          br.BaseStream.Position += materialNameLength;
        }
        if (br.BaseStream.Position + 12 > br.BaseStream.Length) return null;
        br.ReadUInt64(); // area id
        uint areaNameLength = br.ReadUInt32();
        if (areaNameLength == 0 || areaNameLength > br.BaseStream.Length - br.BaseStream.Position || areaNameLength > Int32.MaxValue) return null;
        return Encoding.UTF8.GetString(br.ReadBytes((int)areaNameLength)).TrimEnd('\0').Trim();
      } catch {
        return null;
      }
    }

    private void BackgroundWorker1_DoWork(object sender, DoWorkEventArgs e) {
      List<object> args = e.Argument as List<object>;
      currentAssets = AssetHandler.Instance.GetCurrentAssets((string)args[0], (bool)args[1]);
      currentDom = DomHandler.Instance.GetCurrentDOM(currentAssets);
      //this.currentDom.ami.Load();

      mapAreaData = currentDom.GetObject("mapareasdata").Data.Get<List<object>>("utlDatatableRows");
      StringTable strTable = currentDom.StringTable.Find("str.sys.worldmap");

      foreach (List<object> area in mapAreaData) {
        ulong id = ulong.Parse(area[0].ToString());
        string name;

        if ((string)area[1] != "") {
          long nameId = long.Parse(area[1].ToString());
          name = strTable.GetText(nameId, string.Empty);
          if (name == "")
            name = id.ToString();
        } else {
          name = id.ToString();
        }

        mapAreas[id] = name;
      }

      Dictionary<string, NodeAsset> newAssetDict = new Dictionary<string, NodeAsset>();
      worldAreaOverrides = WorldAreaNameOverrides.LoadEntries();
      var detectedAreaNames = new List<(ulong Id, string InternalName, string Category, string Group)>();

      // mapareasdata is not a complete world list: class phases, old/development maps and some newer areas are
      // absent even though area.dat is present in the client. Probe the union of SWTOR's table, Jedipedia's
      // current catalog snapshot and every ID the user has put in WorldAreaNames.xml. A candidate only reaches
      // the tree when its area.dat really exists in the installed client.
      var candidateIds = new HashSet<ulong>(mapAreas.Keys);
      foreach (WorldAreaCatalogEntry entry in WorldAreaCatalog.Entries) candidateIds.Add(entry.Id);
      foreach (ulong id in worldAreaOverrides.Keys) candidateIds.Add(id);

      foreach (ulong id in candidateIds.OrderBy(x => x)) {
        WorldAreaCatalogEntry catalogEntry = WorldAreaCatalog.Entries.FirstOrDefault(x => x.Id == id);
        worldAreaOverrides.TryGetValue(id, out WorldAreaOverride userEntry);

        string[] candidatePaths = {
          "/resources/world/areas/" + id + "/area.dat",
          "/resources/world/livecontent/systemgenerated/" + id + "/area.dat"
        };

        File areaFile = null;
        foreach (string candidate in candidatePaths) {
          areaFile = currentAssets.FindFile(candidate);
          if (areaFile != null) break;
        }
        if (areaFile == null) continue;

        string internalName = catalogEntry?.InternalName;
        if (String.IsNullOrWhiteSpace(internalName)) internalName = userEntry?.InternalName;
        if (String.IsNullOrWhiteSpace(internalName)) internalName = ReadAreaInternalName(areaFile);

        string defaultCategory = catalogEntry?.Category ?? WorldAreaNameOverrides.UnassignedCategory;
        string defaultGroup = catalogEntry?.Group ?? String.Empty;
        detectedAreaNames.Add((id, internalName, defaultCategory, defaultGroup));

        string name;
        if (userEntry != null && !String.IsNullOrWhiteSpace(userEntry.Name)) name = userEntry.Name.Trim();
        else if (mapAreas.TryGetValue(id, out string mapName) && !String.IsNullOrWhiteSpace(mapName) && mapName != id.ToString()) name = mapName;
        else if (catalogEntry != null && !String.IsNullOrWhiteSpace(catalogEntry.Comment)) name = StripJedipediaMarkup(catalogEntry.Comment).Replace("\r", " ").Replace("\n", " / ");
        else name = id.ToString();

        string displayName = String.IsNullOrWhiteSpace(internalName) || String.Equals(name, internalName, StringComparison.OrdinalIgnoreCase)
          ? name
          : name + " - " + internalName;

        HashFileInfo hashInfo = new HashFileInfo(areaFile.FileInfo.PrimaryHash, areaFile.FileInfo.SecondaryHash, areaFile);
        newAssetDict.Add("/" + id, new NodeAsset("/" + id, "/", displayName, hashInfo));
      }

      // Append newly discovered IDs after the scan. Existing name/category/group values are preserved. Unknown
      // areas are generated as category="Unassigned" so they immediately appear in the dedicated catch-all folder.
      WorldAreaNameOverrides.Synchronize(detectedAreaNames);

      newAssetDict.Add("/", new NodeAsset("/", "", "Worlds", null));
      loadedAssetDict = newAssetDict;
    }

    private void BackgroundWorker1_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e) {
      if (e.Error != null) {
        throw new Exception("Echter Fehler beim Laden: " + e.Error, e.Error);
      }

      foreach (var kvp in loadedAssetDict) {
        assetDict[kvp.Key] = kvp.Value;
      }

      toolStripStatusLabel1.Text = "Loading Tree View Items ...";
      Refresh();

      treeViewFast1.SuspendLayout();
      RebuildWorldTree();
      treeViewFast1.ResumeLayout();
      toolStripStatusLabel1.Text = "Loading Complete";
      toolStripProgressBar1.Visible = false;
      EnableUI();
      pictureBox1.Visible = false;
      treeViewFast1.Visible = true;
      InitializeWorldRenderer(worldSettings.Backend);
      if (treeViewFast1.Nodes.Count > 0) treeViewFast1.Nodes[0].Expand();
    }


    private void InitializeWorldListToolbar() {
      worldListToolbar = new ToolStrip {
        Dock = DockStyle.None,
        GripStyle = ToolStripGripStyle.Hidden,
        ImageScalingSize = new System.Drawing.Size(16, 16),
        Stretch = true
      };
      worldListToolbar.Items.Add(new ToolStripLabel("Search:"));
      txtWorldSearch = new ToolStripTextBox {
        AutoSize = false,
        Width = 230,
        ToolTipText = "Search world name, internal name, ID, category or group"
      };
      txtWorldSearch.TextChanged += (_, __) => {
        if (loadedAssetDict != null) RebuildWorldTree(txtWorldSearch.Text);
      };
      txtWorldSearch.TextBox.Enter += (_, __) => { worldTextInputActive = true; worldRenderInputActive = false; };
      txtWorldSearch.TextBox.Leave += (_, __) => worldTextInputActive = false;
      worldListToolbar.Items.Add(txtWorldSearch);
      var clear = new ToolStripButton("×") { DisplayStyle = ToolStripItemDisplayStyle.Text, ToolTipText = "Clear search" };
      clear.Click += (_, __) => { if (txtWorldSearch != null) txtWorldSearch.Text = String.Empty; };
      worldListToolbar.Items.Add(clear);

      treeViewFast1.Dock = DockStyle.None;
      treeViewFast1.ShowNodeToolTips = true;
      splitContainer2.Panel1.Controls.Add(worldListToolbar);
      worldListToolbar.BringToFront();
      LayoutWorldListPanel();
      splitContainer2.Panel1.Resize += (_, __) => LayoutWorldListPanel();
    }

    private void LayoutWorldListPanel() {
      if (worldListToolbar == null || treeViewFast1 == null) return;
      var panel = splitContainer2.Panel1;
      int margin = 3;
      worldListToolbar.Location = new System.Drawing.Point(margin, margin);
      worldListToolbar.Size = new System.Drawing.Size(Math.Max(1, panel.ClientSize.Width - margin * 2), worldListToolbar.PreferredSize.Height);
      int treeTop = worldListToolbar.Bottom + 2;
      treeViewFast1.Location = new System.Drawing.Point(margin, treeTop);
      treeViewFast1.Size = new System.Drawing.Size(
        Math.Max(1, panel.ClientSize.Width - margin * 2),
        Math.Max(1, panel.ClientSize.Height - treeTop - margin));
      treeViewFast1.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
    }

    private void ActivateWorldRenderInput() {
      if (_closing) return;
      worldRenderInputActive = true;
      worldTextInputActive = false;
      // A normal WinForms Panel is not selectable, so renderPanel.Focus() never actually gave
      // the 3D view keyboard focus. Clear the previously focused TreeView/ToolStrip control and
      // let the renderer use GetAsyncKeyState while this view is the active input surface.
      if (!InvokeRequired) ActiveControl = null;
    }

    public bool PreFilterMessage(ref Message m) {
      if (m.Msg != WM_MOUSEWHEEL) return false;
      int delta = unchecked((short)((m.WParam.ToInt64() >> 16) & 0xffff));
      if (miniMapPanel != null && miniMapPanel.Visible && miniMapPanel.ClientRectangle.Contains(miniMapPanel.PointToClient(Cursor.Position))) {
        if (miniMapPicture != null) {
          Point miniPoint = miniMapPicture.PointToClient(Cursor.Position);
          if (miniMapPicture.ClientRectangle.Contains(miniPoint)) MiniMapHandleMouseWheel(miniPoint, delta);
        }
        return true;
      }
      if (!WorldKeyboardInputEnabled || panelRender == null || renderPanel == null || !renderPanel.Visible) return false;
      System.Drawing.Point point = renderPanel.PointToClient(Cursor.Position);
      if (!renderPanel.ClientRectangle.Contains(point)) return false;
      panelRender.HandleMouseWheel(point, delta);
      return true;
    }

    private static string StripJedipediaMarkup(string textValue) {
      return (textValue ?? String.Empty).Replace("*", String.Empty).Trim();
    }

    private sealed class WorldTreeEntry {
      public ulong Id;
      public NodeAsset Asset;
      public WorldAreaCatalogEntry Catalog;
      public WorldAreaOverride Override;
      public string Category;
      public string Group;
      public int CategoryOrder;
      public int GroupOrder;
      public int EntryOrder;
    }

    private static int WorldCategoryOrder(string category) {
      if (String.Equals(category, WorldAreaNameOverrides.UnassignedCategory, StringComparison.OrdinalIgnoreCase)) return 100000;
      var known = WorldAreaCatalog.Entries.Where(x => String.Equals(x.Category, category, StringComparison.OrdinalIgnoreCase)).Select(x => x.CategoryOrder).ToList();
      return known.Count > 0 ? known.Min() : 50000;
    }

    private static int WorldGroupOrder(string category, string group) {
      if (String.IsNullOrWhiteSpace(group)) return -1;
      var known = WorldAreaCatalog.Entries
        .Where(x => String.Equals(x.Category, category, StringComparison.OrdinalIgnoreCase) && String.Equals(x.Group, group, StringComparison.OrdinalIgnoreCase))
        .Select(x => x.GroupOrder).ToList();
      return known.Count > 0 ? known.Min() : 50000;
    }

    private bool WorldMatchesSearch(WorldTreeEntry item, string needle) {
      if (String.IsNullOrWhiteSpace(needle)) return true;
      string q = needle.Trim();
      bool Has(string candidate) => !String.IsNullOrWhiteSpace(candidate) && candidate.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
      return Has(item.Id.ToString()) || Has(item.Asset?.displayName) || Has(item.Override?.Name) || Has(item.Override?.InternalName)
        || Has(item.Catalog?.InternalName) || Has(item.Catalog?.Comment) || Has(item.Category) || Has(item.Group);
    }

    private TreeNode CreateWorldTreeLeaf(WorldTreeEntry item) {
      var node = new TreeNode {
        Name = "/" + item.Id,
        Text = item.Asset.displayName,
        Tag = item.Asset,
        ImageIndex = 2,
        SelectedImageIndex = 2
      };
      string comment = StripJedipediaMarkup(item.Catalog?.Comment);
      string internalName = !String.IsNullOrWhiteSpace(item.Override?.InternalName) ? item.Override.InternalName : item.Catalog?.InternalName;
      string folder = String.IsNullOrWhiteSpace(item.Group) ? item.Category : item.Category + " / " + item.Group;
      node.ToolTipText = folder
        + (String.IsNullOrWhiteSpace(comment) ? String.Empty : Environment.NewLine + comment)
        + (String.IsNullOrWhiteSpace(internalName) ? String.Empty : Environment.NewLine + internalName)
        + Environment.NewLine + item.Id;
      return node;
    }

    private void RebuildWorldTree(string filter = null) {
      if (loadedAssetDict == null) return;
      string needle = filter?.Trim() ?? String.Empty;
      var items = new List<WorldTreeEntry>();
      foreach (var kvp in loadedAssetDict) {
        if (kvp.Key == "/" || !ulong.TryParse(kvp.Key.Trim('/'), out ulong id)) continue;
        WorldAreaCatalogEntry catalog = WorldAreaCatalog.Entries.FirstOrDefault(x => x.Id == id);
        worldAreaOverrides.TryGetValue(id, out WorldAreaOverride userEntry);
        string category = !String.IsNullOrWhiteSpace(userEntry?.Category)
          ? userEntry.Category.Trim()
          : catalog?.Category ?? WorldAreaNameOverrides.UnassignedCategory;
        string group = userEntry != null && userEntry.Group != null
          ? userEntry.Group.Trim()
          : catalog?.Group ?? String.Empty;
        int entryOrder = catalog != null
          && String.Equals(catalog.Category, category, StringComparison.OrdinalIgnoreCase)
          && String.Equals(catalog.Group ?? String.Empty, group ?? String.Empty, StringComparison.OrdinalIgnoreCase)
            ? catalog.EntryOrder : Int32.MaxValue;
        items.Add(new WorldTreeEntry {
          Id = id, Asset = kvp.Value, Catalog = catalog, Override = userEntry,
          Category = String.IsNullOrWhiteSpace(category) ? WorldAreaNameOverrides.UnassignedCategory : category,
          Group = group ?? String.Empty,
          CategoryOrder = WorldCategoryOrder(category), GroupOrder = WorldGroupOrder(category, group), EntryOrder = entryOrder
        });
      }
      items = items.Where(x => WorldMatchesSearch(x, needle)).ToList();

      treeViewFast1.BeginUpdate();
      treeViewFast1.Nodes.Clear();
      var rootAsset = new NodeAsset("/", "", "Worlds", null);
      var root = new TreeNode("Worlds") { Name = "/", Tag = rootAsset, ImageIndex = 1, SelectedImageIndex = 1 };

      foreach (var categoryItems in items
        .GroupBy(x => new { x.CategoryOrder, x.Category })
        .OrderBy(x => x.Key.CategoryOrder).ThenBy(x => x.Key.Category, StringComparer.OrdinalIgnoreCase)) {
        var categoryNode = new TreeNode(categoryItems.Key.Category) {
          Tag = new NodeAsset("/category/" + categoryItems.Key.Category, "/", categoryItems.Key.Category, null),
          ImageIndex = 1, SelectedImageIndex = 1
        };

        foreach (WorldTreeEntry item in categoryItems.Where(x => String.IsNullOrWhiteSpace(x.Group))
          .OrderBy(x => x.EntryOrder).ThenBy(x => x.Asset.displayName, StringComparer.OrdinalIgnoreCase)) {
          categoryNode.Nodes.Add(CreateWorldTreeLeaf(item));
        }

        foreach (var groupItems in categoryItems.Where(x => !String.IsNullOrWhiteSpace(x.Group))
          .GroupBy(x => new { x.GroupOrder, x.Group })
          .OrderBy(x => x.Key.GroupOrder).ThenBy(x => x.Key.Group, StringComparer.OrdinalIgnoreCase)) {
          var groupNode = new TreeNode(groupItems.Key.Group) {
            Tag = new NodeAsset("/category/" + categoryItems.Key.Category + "/group/" + groupItems.Key.Group, "", groupItems.Key.Group, null),
            ImageIndex = 1, SelectedImageIndex = 1
          };
          foreach (WorldTreeEntry item in groupItems.OrderBy(x => x.EntryOrder).ThenBy(x => x.Asset.displayName, StringComparer.OrdinalIgnoreCase))
            groupNode.Nodes.Add(CreateWorldTreeLeaf(item));
          categoryNode.Nodes.Add(groupNode);
        }
        root.Nodes.Add(categoryNode);
      }

      treeViewFast1.Nodes.Add(root);
      if (!String.IsNullOrWhiteSpace(needle)) root.ExpandAll(); else root.Expand();
      treeViewFast1.EndUpdate();
    }


    private void InitializeWorldRenderToolbar() {
      worldToolbar = new ToolStrip { Dock = DockStyle.Top, GripStyle = ToolStripGripStyle.Hidden, ImageScalingSize = new System.Drawing.Size(20, 20) };
      worldToolbar.Items.Add(new ToolStripLabel("Render:"));
      cmbWorldMode = new ToolStripComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 100 };
      cmbWorldMode.Items.AddRange(new object[] { "In-game", "Lit", "Unlit", "Heightmap", "Wireframe", "Map" });
      cmbWorldMode.SelectedIndex = 0;
      cmbWorldMode.SelectedIndexChanged += (_, __) => {
        if (updatingWorldToolbar) return;
        worldSettings.Mode = (WorldRenderMode)Math.Max(0, cmbWorldMode.SelectedIndex);
        ApplyWorldSettings();
        if (worldSettings.Mode == WorldRenderMode.Map) panelRender?.FitArea();
      };
      worldToolbar.Items.Add(cmbWorldMode);

      worldToolbar.Items.Add(new ToolStripSeparator());
      worldToolbar.Items.Add(new ToolStripLabel("Device:"));
      cmbWorldBackend = new ToolStripComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 112 };
      cmbWorldBackend.Items.AddRange(new object[] { "GPU (D3D11)", "WARP (CPU)" });
      cmbWorldBackend.SelectedIndex = 0;
      cmbWorldBackend.SelectedIndexChanged += async (_, __) => {
        if (updatingWorldToolbar) return;
        WorldRenderBackend backend = cmbWorldBackend.SelectedIndex == 1 ? WorldRenderBackend.Warp : WorldRenderBackend.Hardware;
        await SwitchWorldBackend(backend);
      };
      worldToolbar.Items.Add(cmbWorldBackend);

      worldToolbar.Items.Add(new ToolStripSeparator());
      worldToolbar.Items.Add(new ToolStripLabel("AA:"));
      cmbWorldAntiAliasing = new ToolStripComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 92 };
      cmbWorldAntiAliasing.Items.AddRange(new object[] { "Off", "FXAA", "TAA", "TAA + FXAA" });
      cmbWorldAntiAliasing.SelectedIndex = (int)worldSettings.AntiAliasing;
      cmbWorldAntiAliasing.SelectedIndexChanged += (_, __) => {
        if (updatingWorldToolbar) return;
        worldSettings.AntiAliasing = (WorldAntiAliasing)Math.Max(0, cmbWorldAntiAliasing.SelectedIndex);
        ApplyWorldSettings();
      };
      worldToolbar.Items.Add(cmbWorldAntiAliasing);

      worldToolbar.Items.Add(new ToolStripSeparator());
      worldToolbar.Items.Add(new ToolStripLabel("View:"));
      cmbWorldViewDistance = new ToolStripComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 56 };
      cmbWorldViewDistance.Items.AddRange(new object[] { "1x", "2x", "3x", "5x", "10x" });
      cmbWorldViewDistance.SelectedIndex = 2;
      cmbWorldViewDistance.SelectedIndexChanged += (_, __) => {
        if (updatingWorldToolbar) return;
        float[] scales = { 1f, 2f, 3f, 5f, 10f };
        int index = Math.Max(0, Math.Min(scales.Length - 1, cmbWorldViewDistance.SelectedIndex));
        worldSettings.ViewDistanceScale = scales[index];
        ApplyWorldSettings();
      };
      worldToolbar.Items.Add(cmbWorldViewDistance);
      worldToolbar.Items.Add(new ToolStripSeparator());

      AddWorldToggle("Terrain", true, v => worldSettings.ShowTerrain = v);
      AddWorldToggle("Models", true, v => worldSettings.ShowModels = v);
      btnWorldDecorationHooks = new ToolStripButton("Decoration hooks") {
        CheckOnClick = true,
        Checked = worldSettings.ShowDecorationHooks,
        DisplayStyle = ToolStripItemDisplayStyle.Text,
        Visible = false,
        ToolTipText = "Show/hide Stronghold decoration placement fields"
      };
      btnWorldDecorationHooks.CheckedChanged += (_, __) => {
        if (updatingWorldToolbar) return;
        worldSettings.ShowDecorationHooks = btnWorldDecorationHooks.Checked;
        ApplyWorldSettings();
      };
      worldToolbar.Items.Add(btnWorldDecorationHooks);
      AddWorldToggle("Grass/DYD", true, v => worldSettings.ShowDynamicDetails = v);
      AddWorldToggle("Lighting", true, v => worldSettings.EnableLighting = v);
      AddWorldToggle("Fog", true, v => worldSettings.EnableFog = v);
      AddWorldToggle("Shadows", true, v => worldSettings.EnableShadows = v);
      AddWorldToggle("Local lights", true, v => worldSettings.EnableLocalLights = v);
      AddWorldToggle("Roads", true, v => worldSettings.ShowRoads = v);
      AddWorldToggle("Map art", true, v => worldSettings.ShowMapArt = v);
      AddWorldToggle("Map notes", true, v => worldSettings.ShowMapNotes = v);
      AddWorldToggle("Room culling", worldSettings.EnableRoomVisibility, v => worldSettings.EnableRoomVisibility = v);
      AddWorldToggle("Sky", true, v => worldSettings.ShowSky = v);
      AddWorldToggle("Water", true, v => worldSettings.ShowWater = v);
      AddWorldToggle("Post FX", true, v => worldSettings.EnablePostProcessing = v);

      worldToolbar.Items.Add(new ToolStripSeparator());
      InitializeUtilitiesMenu();
      InitializeNpcMenu();
      btnWorldWalkingMode = new ToolStripButton("Walking mode") {
        CheckOnClick = true, Checked = worldSettings.WalkingMode, DisplayStyle = ToolStripItemDisplayStyle.Text,
        ToolTipText = "Ground-following first-person movement (WASD, Shift run, Space jump, mouse wheel speed)"
      };
      btnWorldWalkingMode.CheckedChanged += (_, __) => {
        if (updatingWorldToolbar) return;
        worldSettings.WalkingMode = btnWorldWalkingMode.Checked;
        ApplyWorldSettings();
        SetStatusLabel(btnWorldWalkingMode.Checked ? "Walking mode: WASD, Shift = run, Space = jump, wheel = speed" : "Free-fly camera enabled");
      };
      worldToolbar.Items.Add(btnWorldWalkingMode);

      worldToolbar.Items.Add(new ToolStripSeparator());
      btnWorldInspectModel = new ToolStripButton("Inspect model") { CheckOnClick = true, DisplayStyle = ToolStripItemDisplayStyle.Text, ToolTipText = "Click, then click visible GR2 geometry to show its room/asset/instance/mesh/material diagnostics" };
      btnWorldInspectModel.CheckedChanged += (_, __) => {
        worldModelInspectPending = btnWorldInspectModel.Checked;
        if (worldModelInspectPending) SetStatusLabel("Model inspector armed — click the stray object in the 3D view.");
      };
      worldToolbar.Items.Add(btnWorldInspectModel);
      var fit = new ToolStripButton("Fit Area");
      fit.Click += (_, __) => panelRender?.FitArea();
      worldToolbar.Items.Add(fit);
      btnWorldMiniMap = new ToolStripButton("Minimap") { CheckOnClick = true, DisplayStyle = ToolStripItemDisplayStyle.Text };
      btnWorldMiniMap.CheckedChanged += (_, __) => { if (!updatingWorldToolbar) SetMiniMapVisible(btnWorldMiniMap.Checked); };
      worldToolbar.Items.Add(btnWorldMiniMap);
      var shot = new ToolStripButton("Screenshot");
      shot.Click += (_, __) => panelRender?.RequestScreenshot();
      worldToolbar.Items.Add(shot);

      splitContainer3.Panel1.Controls.Add(worldToolbar);
      worldToolbar.BringToFront();
      LayoutWorldRenderPanel();
      splitContainer3.Panel1.Resize += (_, __) => LayoutWorldRenderPanel();
    }

    private ToolStripMenuItem AddWorldDropDownToggle(ToolStripDropDownButton menu, string text, bool initialValue, Action<bool> setter, string tooltip = null) {
      var item = new ToolStripMenuItem(text) { CheckOnClick = true, Checked = initialValue, ToolTipText = tooltip ?? String.Empty };
      item.CheckedChanged += (_, __) => {
        if (updatingWorldToolbar) return;
        setter(item.Checked);
        ApplyWorldSettings();
      };
      menu.DropDownItems.Add(item);
      return item;
    }

    private static void KeepCheckMenuOpen(ToolStripDropDownButton menu) {
      if (menu == null) return;
      menu.DropDown.Closing += (_, e) => {
        // A checkbox menu is meant for changing several layers in one pass. Keep it open on item clicks,
        // but still close normally via Escape, clicking outside, or clicking the toolbar button again.
        if (e.CloseReason == ToolStripDropDownCloseReason.ItemClicked) e.Cancel = true;
      };
    }

    private void InitializeUtilitiesMenu() {
      btnWorldUtilities = new ToolStripDropDownButton("Utilities") { DisplayStyle = ToolStripItemDisplayStyle.Text, ToolTipText = "Jedipedia-style editor/authoring overlays" };
      KeepCheckMenuOpen(btnWorldUtilities);
      AddWorldDropDownToggle(btnWorldUtilities, "Spawners / encounters", worldSettings.ShowUtilitySpawners, v => worldSettings.ShowUtilitySpawners = v);
      AddWorldDropDownToggle(btnWorldUtilities, "Cover points", worldSettings.ShowUtilityCoverPoints, v => worldSettings.ShowUtilityCoverPoints = v);
      AddWorldDropDownToggle(btnWorldUtilities, "Lights", worldSettings.ShowUtilityLights, v => worldSettings.ShowUtilityLights = v);
      AddWorldDropDownToggle(btnWorldUtilities, "Kynapse seed points", worldSettings.ShowUtilitySeedPoints, v => worldSettings.ShowUtilitySeedPoints = v);
      btnWorldUtilities.DropDownItems.Add(new ToolStripSeparator());
      AddWorldDropDownToggle(btnWorldUtilities, "Paths", worldSettings.ShowUtilityPaths, v => worldSettings.ShowUtilityPaths = v);
      AddWorldDropDownToggle(btnWorldUtilities, "Map roads with paths", worldSettings.ShowUtilityMapRoadPaths, v => worldSettings.ShowUtilityMapRoadPaths = v);
      AddWorldDropDownToggle(btnWorldUtilities, "Parent / child connections", worldSettings.ShowUtilityConnections, v => worldSettings.ShowUtilityConnections = v);
      AddWorldDropDownToggle(btnWorldUtilities, "Region / trigger volumes", worldSettings.ShowUtilityVolumes, v => worldSettings.ShowUtilityVolumes = v);
      AddWorldDropDownToggle(btnWorldUtilities, "Phase gateways", worldSettings.ShowPhaseGateways, v => worldSettings.ShowPhaseGateways = v, "Show INSTANCE_GATEWAY door planes between phased copies of the world");
      AddWorldDropDownToggle(btnWorldUtilities, "Other helpers", worldSettings.ShowUtilityOther, v => worldSettings.ShowUtilityOther = v);
      worldToolbar.Items.Add(btnWorldUtilities);
    }

    private void InitializeNpcMenu() {
      btnWorldNpcs = new ToolStripDropDownButton("NPCs") { DisplayStyle = ToolStripItemDisplayStyle.Text, ToolTipText = "Spawned/client-only NPC preview" };
      KeepCheckMenuOpen(btnWorldNpcs);
      AddWorldDropDownToggle(btnWorldNpcs, "Show NPC models", worldSettings.ShowNpcs, v => worldSettings.ShowNpcs = v);
      AddWorldDropDownToggle(btnWorldNpcs, "Localized names", worldSettings.ShowNpcNames, v => worldSettings.ShowNpcNames = v);
      AddWorldDropDownToggle(btnWorldNpcs, "Equipped item names", worldSettings.ShowNpcItems, v => worldSettings.ShowNpcItems = v);
      AddWorldDropDownToggle(btnWorldNpcs, "Idle animations", worldSettings.AnimateNpcs, v => worldSettings.AnimateNpcs = v);
      btnWorldNpcs.DropDownItems.Add(new ToolStripSeparator());
      AddWorldDropDownToggle(btnWorldNpcs, "SPN placeable objects", worldSettings.ShowSpnObjects, v => worldSettings.ShowSpnObjects = v, "Render plc.* objects referenced by SPN spawners");
      AddWorldDropDownToggle(btnWorldNpcs, "SPN animations", worldSettings.AnimateSpnObjects, v => worldSettings.AnimateSpnObjects = v, "Animate MAG/Morpheme-driven SPN placeables when an idle clip can be resolved");
      worldToolbar.Items.Add(btnWorldNpcs);
    }

    private void RenderPanel_MouseDown(object sender, MouseEventArgs e) {
      ActivateWorldRenderInput();
      if (!worldModelInspectPending || e.Button != MouseButtons.Left) return;
      worldModelInspectPending = false;
      if (btnWorldInspectModel != null) btnWorldInspectModel.Checked = false;
      string details = panelRender?.InspectModelAtScreen(e.X, e.Y) ?? "World renderer is not ready.";
      SetStatusLabel(details.StartsWith("World model inspector", StringComparison.Ordinal) ? "Model identified — details opened." : details.Replace("\r", " ").Replace("\n", " "));
      MessageBox.Show(this, details + "\r\n\r\nTip: Press Ctrl+C while this dialog is focused to copy the complete diagnostic text.", "World model inspector", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private bool IsKnownStrongholdArea(ulong areaId) {
      WorldAreaCatalogEntry catalog = WorldAreaCatalog.Entries.FirstOrDefault(x => x.Id == areaId);
      if (catalog != null && (String.Equals(catalog.Group, "Strongholds", StringComparison.OrdinalIgnoreCase)
        || (!String.IsNullOrWhiteSpace(catalog.InternalName) && catalog.InternalName.StartsWith("stronghold_", StringComparison.OrdinalIgnoreCase)))) return true;
      if (worldAreaOverrides != null && worldAreaOverrides.TryGetValue(areaId, out WorldAreaOverride entry)) {
        if (String.Equals(entry.Group, "Strongholds", StringComparison.OrdinalIgnoreCase)) return true;
        if (!String.IsNullOrWhiteSpace(entry.InternalName) && entry.InternalName.StartsWith("stronghold_", StringComparison.OrdinalIgnoreCase)) return true;
      }
      return false;
    }

    private void SetDecorationHookAvailability(bool available, int hookPlacementCount = -1) {
      if (_closing || IsDisposed || Disposing) return;
      if (InvokeRequired) {
        try { BeginInvoke(new Action<bool, int>(SetDecorationHookAvailability), available, hookPlacementCount); } catch { }
        return;
      }
      if (btnWorldDecorationHooks == null) return;
      btnWorldDecorationHooks.Visible = available;
      btnWorldDecorationHooks.ToolTipText = available
        ? (hookPlacementCount >= 0
          ? "Show/hide Stronghold decoration placement fields (" + hookPlacementCount + " placements)"
          : "Show/hide Stronghold decoration placement fields")
        : "Show/hide Stronghold decoration placement fields";
    }

    private void AddWorldToggle(string text, bool initial, Action<bool> setter) {
      var button = new ToolStripButton(text) { CheckOnClick = true, Checked = initial, DisplayStyle = ToolStripItemDisplayStyle.Text };
      button.CheckedChanged += (_, __) => { if (updatingWorldToolbar) return; setter(button.Checked); ApplyWorldSettings(); };
      worldToolbar.Items.Add(button);
    }

    private void LayoutWorldRenderPanel() {
      if (worldToolbar == null || renderPanel == null) return;
      int top = worldToolbar.Height;
      renderPanel.Location = new System.Drawing.Point(0, top);
      renderPanel.Size = new System.Drawing.Size(splitContainer3.Panel1.ClientSize.Width, Math.Max(1, splitContainer3.Panel1.ClientSize.Height - top));
      renderPanel.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
      LayoutMiniMapWithinHost();
      LayoutPhaseBanner();
    }

    private void InitializeWorldLoadingOverlay() {
      if (renderPanel == null || worldLoadingOverlay != null) return;
      worldLoadingOverlay = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(18, 22, 28), Visible = false, TabStop = false };
      worldLoadingCard = new Panel { Size = new Size(520, 150), BackColor = Color.FromArgb(29, 35, 43), TabStop = false };
      worldLoadingTitle = new Label {
        AutoSize = false, Location = new Point(26, 22), Size = new Size(468, 34), ForeColor = Color.White,
        Font = new Font(this.Font.FontFamily, 15f, FontStyle.Bold), Text = "Loading world…", TextAlign = ContentAlignment.MiddleLeft
      };
      worldLoadingDetails = new Label {
        AutoSize = false, Location = new Point(28, 62), Size = new Size(464, 28), ForeColor = Color.FromArgb(188, 202, 216),
        Font = new Font(this.Font.FontFamily, 9f, FontStyle.Regular), Text = "Reading area data…", TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true
      };
      worldLoadingProgress = new ProgressBar { Location = new Point(28, 105), Size = new Size(464, 16), Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 24 };
      worldLoadingCard.Controls.Add(worldLoadingTitle); worldLoadingCard.Controls.Add(worldLoadingDetails); worldLoadingCard.Controls.Add(worldLoadingProgress);
      worldLoadingOverlay.Controls.Add(worldLoadingCard);
      worldLoadingOverlay.Resize += (_, __) => LayoutWorldLoadingCard();
      renderPanel.Controls.Add(worldLoadingOverlay);
      LayoutWorldLoadingCard();
    }

    private void LayoutWorldLoadingCard() {
      if (worldLoadingOverlay == null || worldLoadingCard == null) return;
      worldLoadingCard.Left = Math.Max(8, (worldLoadingOverlay.ClientSize.Width - worldLoadingCard.Width) / 2);
      worldLoadingCard.Top = Math.Max(8, (worldLoadingOverlay.ClientSize.Height - worldLoadingCard.Height) / 2);
    }

    private void ShowWorldLoading(string title, string details) {
      void Apply() {
        if (_closing || worldLoadingOverlay == null) return;
        if (worldLoadingTitle != null) worldLoadingTitle.Text = String.IsNullOrWhiteSpace(title) ? "Loading world…" : title;
        if (worldLoadingDetails != null) worldLoadingDetails.Text = details ?? String.Empty;
        if (worldLoadingProgress != null) { worldLoadingProgress.Style = ProgressBarStyle.Marquee; worldLoadingProgress.MarqueeAnimationSpeed = 24; }
        renderPanel.Visible = true; worldLoadingOverlay.Visible = true; worldLoadingOverlay.BringToFront(); LayoutWorldLoadingCard();
      }
      if (InvokeRequired) BeginInvoke((Action)Apply); else Apply();
    }

    private void UpdateWorldLoading(string details, int current = -1, int maximum = -1) {
      void Apply() {
        if (_closing || worldLoadingOverlay == null || !worldLoadingOverlay.Visible) return;
        if (worldLoadingDetails != null) worldLoadingDetails.Text = details ?? String.Empty;
        if (worldLoadingProgress != null) {
          if (maximum > 0 && current >= 0) {
            worldLoadingProgress.Style = ProgressBarStyle.Continuous;
            worldLoadingProgress.MarqueeAnimationSpeed = 0;
            worldLoadingProgress.Minimum = 0; worldLoadingProgress.Maximum = Math.Max(1, maximum);
            worldLoadingProgress.Value = Math.Max(0, Math.Min(worldLoadingProgress.Maximum, current));
          } else {
            worldLoadingProgress.Style = ProgressBarStyle.Marquee; worldLoadingProgress.MarqueeAnimationSpeed = 24;
          }
        }
      }
      if (InvokeRequired) BeginInvoke((Action)Apply); else Apply();
    }

    private void HideWorldLoading() {
      void Apply() { if (worldLoadingOverlay != null) worldLoadingOverlay.Visible = false; }
      if (_closing || IsDisposed) return;
      if (InvokeRequired) BeginInvoke((Action)Apply); else Apply();
    }

    private void InitializeWorldMiniMap() {
      miniMapPanel = new Panel {
        Visible = false,
        BorderStyle = BorderStyle.FixedSingle,
        BackColor = Color.FromArgb(28, 28, 28),
        Size = new Size(390, 285)
      };
      miniMapTitleBar = new Panel { Dock = DockStyle.Top, Height = 25, BackColor = SystemColors.ControlDark, Cursor = Cursors.SizeAll };
      miniMapTitle = new Label {
        Dock = DockStyle.Fill,
        Text = "Minimap — click to teleport",
        TextAlign = ContentAlignment.MiddleLeft,
        Padding = new Padding(6, 0, 0, 0),
        AutoEllipsis = true,
        Cursor = Cursors.SizeAll
      };
      miniMapClose = new Button { Dock = DockStyle.Right, Width = 26, Text = "×", TabStop = false, FlatStyle = FlatStyle.Flat };
      miniMapClose.FlatAppearance.BorderSize = 0;
      miniMapClose.Click += (_, __) => { if (btnWorldMiniMap != null) btnWorldMiniMap.Checked = false; else SetMiniMapVisible(false); };
      miniMapTitleBar.Controls.Add(miniMapTitle);
      miniMapTitleBar.Controls.Add(miniMapClose);

      miniMapPicture = new PictureBox { Dock = DockStyle.Fill, BackColor = Color.FromArgb(28, 28, 28), Cursor = Cursors.Cross };
      miniMapPicture.Paint += MiniMapPicture_Paint;
      miniMapPicture.MouseDown += MiniMapPicture_MouseDown;
      miniMapPicture.MouseMove += MiniMapPicture_MouseMove;
      miniMapPicture.MouseUp += MiniMapPicture_MouseUp;
      miniMapPicture.MouseClick += MiniMapPicture_MouseClick;

      miniMapResizeGrip = new Panel { Width = 15, Height = 15, BackColor = SystemColors.ControlDark, Cursor = Cursors.SizeNWSE };
      miniMapResizeGrip.Anchor = AnchorStyles.Right | AnchorStyles.Bottom;
      miniMapResizeGrip.MouseDown += MiniMapResizeGrip_MouseDown;
      miniMapResizeGrip.MouseMove += MiniMapResizeGrip_MouseMove;
      miniMapResizeGrip.MouseUp += (_, __) => miniMapResizing = false;

      miniMapPanel.Controls.Add(miniMapPicture);
      miniMapPanel.Controls.Add(miniMapTitleBar);
      miniMapPanel.Controls.Add(miniMapResizeGrip);
      splitContainer3.Panel1.Controls.Add(miniMapPanel);
      miniMapPanel.BringToFront();

      miniMapTitleBar.MouseDown += MiniMapTitle_MouseDown;
      miniMapTitleBar.MouseMove += MiniMapTitle_MouseMove;
      miniMapTitleBar.MouseUp += (_, __) => miniMapMoving = false;
      miniMapTitle.MouseDown += MiniMapTitle_MouseDown;
      miniMapTitle.MouseMove += MiniMapTitle_MouseMove;
      miniMapTitle.MouseUp += (_, __) => miniMapMoving = false;
      LayoutMiniMapWithinHost();
    }

    private void InitializeWorldRoomStatus() {
      toolStripPositionStatus = new ToolStripStatusLabel("Position: 0, 0, 0") {
        Spring = false,
        TextAlign = ContentAlignment.MiddleLeft
      };
      toolStripPhaseStatus = new ToolStripStatusLabel("Phase: none") {
        Spring = false,
        TextAlign = ContentAlignment.MiddleLeft,
        ToolTipText = "Current SWTOR phs phase (INSTANCE_REGION or implicit area phase)"
      };
      toolStripRoomStatus = new ToolStripStatusLabel("Current room: unknown") {
        Spring = true,
        TextAlign = ContentAlignment.MiddleRight
      };
      int insertIndex = Math.Max(0, statusStrip1.Items.Count - 1);
      statusStrip1.Items.Insert(insertIndex, toolStripPositionStatus);
      statusStrip1.Items.Insert(insertIndex + 1, toolStripPhaseStatus);
      statusStrip1.Items.Insert(insertIndex + 2, toolStripRoomStatus);

      phaseBannerLabel = new Label {
        AutoSize = true,
        Visible = false,
        BackColor = Color.FromArgb(205, 38, 34, 24),
        ForeColor = Color.FromArgb(244, 202, 92),
        BorderStyle = BorderStyle.FixedSingle,
        Font = new Font(Font.FontFamily, Math.Max(8f, Font.Size), FontStyle.Bold),
        Padding = new Padding(12, 2, 12, 2),
        TextAlign = ContentAlignment.MiddleCenter,
        UseMnemonic = false
      };
      splitContainer3.Panel1.Controls.Add(phaseBannerLabel);
      phaseBannerLabel.BringToFront();
      LayoutPhaseBanner();

      worldOverlayTimer = new System.Windows.Forms.Timer { Interval = 125 };
      worldOverlayTimer.Tick += (_, __) => {
        if (_closing) return;
        string room = panelRender?.CurrentRoomName ?? "unknown";
        string phase = panelRender?.CurrentPhaseName ?? String.Empty;
        Vector3 position = panelRender?.CurrentCameraPosition ?? new Vector3();
        toolStripPositionStatus.Text = string.Format(System.Globalization.CultureInfo.InvariantCulture, "Position: {0:0.##}, {1:0.##}, {2:0.##}", position.X, position.Y, position.Z);
        toolStripPhaseStatus.Text = "Phase: " + (String.IsNullOrWhiteSpace(phase) ? "none" : phase);
        toolStripRoomStatus.Text = "Current room: " + room;
        UpdatePhaseBanner(phase);
        if (miniMapPanel != null && miniMapPanel.Visible) miniMapPicture?.Invalidate();
      };
      worldOverlayTimer.Start();
    }

    private void UpdatePhaseBanner(string phase) {
      if (phaseBannerLabel == null) return;
      string text = (phase ?? String.Empty).Trim();
      if (String.Equals(text, lastPhaseBannerText, StringComparison.Ordinal)) return;
      lastPhaseBannerText = text;
      phaseBannerLabel.Text = text;
      phaseBannerLabel.Visible = !String.IsNullOrWhiteSpace(text);
      LayoutPhaseBanner();
      if (phaseBannerLabel.Visible) phaseBannerLabel.BringToFront();
    }

    private void LayoutPhaseBanner() {
      if (phaseBannerLabel == null || splitContainer3?.Panel1 == null) return;
      int toolbarHeight = worldToolbar?.Height ?? 0;
      phaseBannerLabel.Left = Math.Max(4, (splitContainer3.Panel1.ClientSize.Width - phaseBannerLabel.Width) / 2);
      phaseBannerLabel.Top = toolbarHeight + 5;
    }

    internal void ToggleMiniMapFromRenderer() {
      if (_closing || IsDisposed || Disposing) return;
      if (InvokeRequired) {
        try { BeginInvoke(new Action(ToggleMiniMapFromRenderer)); } catch { }
        return;
      }
      if (btnWorldMiniMap != null) btnWorldMiniMap.Checked = !btnWorldMiniMap.Checked;
      else SetMiniMapVisible(miniMapPanel == null || !miniMapPanel.Visible);
    }

    internal void SetFullMapActive(bool active) {
      if (_closing || IsDisposed || Disposing) return;
      if (InvokeRequired) {
        try { BeginInvoke(new Action<bool>(SetFullMapActive), active); } catch { }
        return;
      }
      worldFullMapActive = active;
      bool showMini = !active && btnWorldMiniMap != null && btnWorldMiniMap.Checked;
      if (miniMapPanel != null) {
        miniMapPanel.Visible = showMini;
        if (showMini) { LayoutMiniMapWithinHost(); miniMapPanel.BringToFront(); }
      }
    }

    private void SetMiniMapVisible(bool visible) {
      if (miniMapPanel == null) return;
      miniMapPanel.Visible = visible && !worldFullMapActive;
      if (visible && !worldFullMapActive) {
        LayoutMiniMapWithinHost();
        miniMapPanel.BringToFront();
        ActivateWorldRenderInput();
        if (miniMapImage == null) {
          miniMapTitle.Text = "Minimap — rendering…";
          panelRender?.RequestMiniMapSnapshot();
        } else {
          UpdateMiniMapTitle();
          miniMapPicture.Invalidate();
        }
      }
    }

    private void InvalidateMiniMapSnapshot() {
      if (miniMapImage != null) { miniMapImage.Dispose(); miniMapImage = null; }
      miniMapMinX = miniMapMaxX = miniMapMinZ = miniMapMaxZ = 0;
      miniMapZoom = 1f; miniMapCenterX = miniMapCenterZ = 0f; miniMapPanning = miniMapSuppressClick = false;
      if (miniMapPicture != null) miniMapPicture.Invalidate();
      if (miniMapPanel != null && miniMapPanel.Visible) {
        miniMapTitle.Text = "Minimap — rendering…";
        panelRender?.RequestMiniMapSnapshot();
      }
    }

    internal void PostMiniMapSnapshot(Bitmap bitmap, float minX, float maxX, float minZ, float maxZ) {
      if (bitmap == null) return;
      if (_closing || IsDisposed || Disposing) { bitmap.Dispose(); return; }
      if (InvokeRequired) {
        try { BeginInvoke(new Action<Bitmap, float, float, float, float>(PostMiniMapSnapshot), bitmap, minX, maxX, minZ, maxZ); }
        catch { bitmap.Dispose(); }
        return;
      }
      miniMapImage?.Dispose();
      miniMapImage = bitmap;
      miniMapMinX = minX; miniMapMaxX = maxX; miniMapMinZ = minZ; miniMapMaxZ = maxZ;
      miniMapZoom = 1f; miniMapCenterX = (minX + maxX) * .5f; miniMapCenterZ = (minZ + maxZ) * .5f;
      UpdateMiniMapTitle();
      ResizeMiniMapToImageAspect();
      miniMapPicture?.Invalidate();
    }

    private Rectangle MiniMapImageRectangle() {
      if (miniMapPicture == null || miniMapImage == null) return Rectangle.Empty;
      return new Rectangle(0, 0, Math.Max(1, miniMapPicture.ClientSize.Width), Math.Max(1, miniMapPicture.ClientSize.Height));
    }

    private void ResizeMiniMapToImageAspect(int preferredWidth = -1) {
      if (miniMapPanel == null || miniMapImage == null || splitContainer3?.Panel1 == null) return;
      float aspect = miniMapImage.Width / (float)Math.Max(1, miniMapImage.Height);
      int titleHeight = miniMapTitleBar?.Height ?? 25;
      int top = (worldToolbar?.Bottom ?? 0) + 6;
      int maxW = Math.Max(160, splitContainer3.Panel1.ClientSize.Width - 8);
      int maxContentH = Math.Max(90, splitContainer3.Panel1.ClientSize.Height - top - titleHeight - 6);
      int width = preferredWidth > 0 ? preferredWidth : Math.Max(220, miniMapPanel.Width);
      width = Math.Min(maxW, Math.Max(1, width));
      int contentH = Math.Max(1, (int)Math.Round(width / Math.Max(.0001f, aspect)));
      if (contentH > maxContentH) {
        contentH = maxContentH;
        width = Math.Max(1, (int)Math.Round(contentH * aspect));
      }
      if (width > maxW) {
        width = maxW;
        contentH = Math.Max(1, (int)Math.Round(width / Math.Max(.0001f, aspect)));
      }
      miniMapPanel.Size = new Size(width, titleHeight + contentH);
      LayoutMiniMapWithinHost();
    }

    private void MiniMapVisibleWorldBounds(out float minX, out float maxX, out float minZ, out float maxZ) {
      float fullW = Math.Max(.0001f, miniMapMaxX - miniMapMinX), fullH = Math.Max(.0001f, miniMapMaxZ - miniMapMinZ);
      float zoom = Math.Max(1f, Math.Min(MiniMapMaxZoom, miniMapZoom));
      float visibleW = fullW / zoom, visibleH = fullH / zoom;
      float halfW = visibleW * .5f, halfH = visibleH * .5f;
      miniMapCenterX = Math.Max(miniMapMinX + halfW, Math.Min(miniMapMaxX - halfW, miniMapCenterX));
      miniMapCenterZ = Math.Max(miniMapMinZ + halfH, Math.Min(miniMapMaxZ - halfH, miniMapCenterZ));
      minX = miniMapCenterX - halfW; maxX = miniMapCenterX + halfW;
      minZ = miniMapCenterZ - halfH; maxZ = miniMapCenterZ + halfH;
    }

    private RectangleF MiniMapSourceRectangle() {
      if (miniMapImage == null || miniMapMaxX <= miniMapMinX || miniMapMaxZ <= miniMapMinZ) return RectangleF.Empty;
      MiniMapVisibleWorldBounds(out float minX, out float maxX, out float minZ, out float maxZ);
      float fullW = miniMapMaxX - miniMapMinX, fullH = miniMapMaxZ - miniMapMinZ;
      float x = (minX - miniMapMinX) / fullW * miniMapImage.Width;
      float y = (minZ - miniMapMinZ) / fullH * miniMapImage.Height;
      float w = (maxX - minX) / fullW * miniMapImage.Width;
      float h = (maxZ - minZ) / fullH * miniMapImage.Height;
      return new RectangleF(x, y, Math.Max(1f, w), Math.Max(1f, h));
    }

    private void UpdateMiniMapTitle() {
      if (miniMapTitle == null) return;
      miniMapTitle.Text = miniMapImage == null ? "Minimap — rendering…" : string.Format(System.Globalization.CultureInfo.InvariantCulture, "Minimap — {0:0.#}× — click teleport / drag pan", miniMapZoom);
    }

    private void MiniMapHandleMouseWheel(Point point, int delta) {
      if (miniMapImage == null || delta == 0 || miniMapMaxX <= miniMapMinX || miniMapMaxZ <= miniMapMinZ) return;
      Rectangle dst = MiniMapImageRectangle();
      if (dst.IsEmpty || !dst.Contains(point)) return;
      MiniMapVisibleWorldBounds(out float oldMinX, out float oldMaxX, out float oldMinZ, out float oldMaxZ);
      float u = Math.Max(0f, Math.Min(1f, (point.X - dst.Left) / (float)Math.Max(1, dst.Width)));
      float v = Math.Max(0f, Math.Min(1f, (point.Y - dst.Top) / (float)Math.Max(1, dst.Height)));
      float anchorX = oldMinX + u * (oldMaxX - oldMinX);
      float anchorZ = oldMinZ + v * (oldMaxZ - oldMinZ);
      float notch = delta / 120f;
      miniMapZoom = Math.Max(1f, Math.Min(MiniMapMaxZoom, miniMapZoom * (float)Math.Pow(1.25, notch)));
      float fullW = miniMapMaxX - miniMapMinX, fullH = miniMapMaxZ - miniMapMinZ;
      float newW = fullW / miniMapZoom, newH = fullH / miniMapZoom;
      miniMapCenterX = anchorX - (u - .5f) * newW;
      miniMapCenterZ = anchorZ - (v - .5f) * newH;
      MiniMapVisibleWorldBounds(out _, out _, out _, out _);
      UpdateMiniMapTitle();
      miniMapPicture.Invalidate();
    }

    private void MiniMapPicture_Paint(object sender, PaintEventArgs e) {
      e.Graphics.Clear(miniMapPicture?.BackColor ?? Color.FromArgb(28, 28, 28));
      Rectangle dst = MiniMapImageRectangle();
      if (miniMapImage == null || dst.IsEmpty) {
        using var brush = new SolidBrush(Color.Gainsboro);
        string text = "Rendering map…";
        SizeF size = e.Graphics.MeasureString(text, Font);
        e.Graphics.DrawString(text, Font, brush, (miniMapPicture.ClientSize.Width - size.Width) / 2f, (miniMapPicture.ClientSize.Height - size.Height) / 2f);
        return;
      }
      RectangleF src = MiniMapSourceRectangle();
      if (src.IsEmpty) e.Graphics.DrawImage(miniMapImage, dst); else e.Graphics.DrawImage(miniMapImage, dst, src, GraphicsUnit.Pixel);
      if (panelRender == null || miniMapMaxX <= miniMapMinX || miniMapMaxZ <= miniMapMinZ) return;
      MiniMapVisibleWorldBounds(out float visibleMinX, out float visibleMaxX, out float visibleMinZ, out float visibleMaxZ);
      panelRender.GetMapPose(out float x, out float z, out float lookX, out float lookZ);
      float u = (x - visibleMinX) / Math.Max(.0001f, visibleMaxX - visibleMinX);
      float v = (z - visibleMinZ) / Math.Max(.0001f, visibleMaxZ - visibleMinZ);
      float px = dst.Left + u * dst.Width, py = dst.Top + v * dst.Height;
      if (px < dst.Left - 8 || px > dst.Right + 8 || py < dst.Top - 8 || py > dst.Bottom + 8) return;
      float len = (float)Math.Sqrt(lookX * lookX + lookZ * lookZ);
      if (len < 0.0001f) { lookX = 0; lookZ = -1; len = 1; }
      lookX /= len; lookZ /= len;
      PointF tip = new PointF(px + lookX * 10f, py + lookZ * 10f);
      PointF side = new PointF(-lookZ * 5f, lookX * 5f);
      PointF back = new PointF(px - lookX * 5f, py - lookZ * 5f);
      PointF[] arrow = { tip, new PointF(back.X + side.X, back.Y + side.Y), new PointF(back.X - side.X, back.Y - side.Y) };
      using var shadow = new SolidBrush(Color.FromArgb(180, Color.Black));
      using var marker = new SolidBrush(Color.OrangeRed);
      e.Graphics.FillEllipse(shadow, px - 6, py - 6, 12, 12);
      e.Graphics.FillPolygon(marker, arrow);
      e.Graphics.FillEllipse(Brushes.White, px - 2, py - 2, 4, 4);
    }

    private void MiniMapPicture_MouseDown(object sender, MouseEventArgs e) {
      if (e.Button != MouseButtons.Left || miniMapImage == null) return;
      Rectangle dst = MiniMapImageRectangle();
      if (dst.IsEmpty || !dst.Contains(e.Location)) return;
      miniMapPanning = true;
      miniMapSuppressClick = false;
      miniMapPanStart = e.Location;
      miniMapPanStartCenterX = miniMapCenterX;
      miniMapPanStartCenterZ = miniMapCenterZ;
      miniMapPicture.Capture = true;
    }

    private void MiniMapPicture_MouseMove(object sender, MouseEventArgs e) {
      if (!miniMapPanning || (e.Button & MouseButtons.Left) == 0 || miniMapImage == null) return;
      int dx = e.X - miniMapPanStart.X, dy = e.Y - miniMapPanStart.Y;
      if (!miniMapSuppressClick && dx * dx + dy * dy < 16) return;
      miniMapSuppressClick = true;
      Rectangle dst = MiniMapImageRectangle();
      if (dst.IsEmpty) return;
      float fullW = Math.Max(.0001f, miniMapMaxX - miniMapMinX), fullH = Math.Max(.0001f, miniMapMaxZ - miniMapMinZ);
      float zoom = Math.Max(1f, Math.Min(MiniMapMaxZoom, miniMapZoom));
      float visibleW = fullW / zoom, visibleH = fullH / zoom;
      miniMapCenterX = miniMapPanStartCenterX - dx / (float)Math.Max(1, dst.Width) * visibleW;
      miniMapCenterZ = miniMapPanStartCenterZ - dy / (float)Math.Max(1, dst.Height) * visibleH;
      MiniMapVisibleWorldBounds(out _, out _, out _, out _);
      miniMapPicture.Invalidate();
    }

    private void MiniMapPicture_MouseUp(object sender, MouseEventArgs e) {
      if (e.Button != MouseButtons.Left) return;
      miniMapPanning = false;
      if (miniMapPicture != null) miniMapPicture.Capture = false;
    }

    private void MiniMapPicture_MouseClick(object sender, MouseEventArgs e) {
      if (miniMapSuppressClick) { miniMapSuppressClick = false; return; }
      if (e.Button != MouseButtons.Left || panelRender == null || miniMapImage == null) return;
      Rectangle dst = MiniMapImageRectangle();
      if (dst.IsEmpty || !dst.Contains(e.Location) || miniMapMaxX <= miniMapMinX || miniMapMaxZ <= miniMapMinZ) return;
      float u = (e.X - dst.Left) / (float)dst.Width;
      float v = (e.Y - dst.Top) / (float)dst.Height;
      MiniMapVisibleWorldBounds(out float visibleMinX, out float visibleMaxX, out float visibleMinZ, out float visibleMaxZ);
      float worldX = visibleMinX + u * (visibleMaxX - visibleMinX);
      float worldZ = visibleMinZ + v * (visibleMaxZ - visibleMinZ);
      panelRender.RequestMapTeleport(worldX, worldZ);
      ActivateWorldRenderInput();
      miniMapPicture.Invalidate();
    }

    private void MiniMapTitle_MouseDown(object sender, MouseEventArgs e) {
      if (e.Button != MouseButtons.Left || miniMapPanel == null) return;
      miniMapMoving = true;
      miniMapDragStart = Cursor.Position;
      miniMapPanelStart = miniMapPanel.Location;
    }

    private void MiniMapTitle_MouseMove(object sender, MouseEventArgs e) {
      if (!miniMapMoving || miniMapPanel == null) return;
      Point now = Cursor.Position;
      miniMapPanel.Location = new Point(miniMapPanelStart.X + now.X - miniMapDragStart.X, miniMapPanelStart.Y + now.Y - miniMapDragStart.Y);
      LayoutMiniMapWithinHost(false);
    }

    private void MiniMapResizeGrip_MouseDown(object sender, MouseEventArgs e) {
      if (e.Button != MouseButtons.Left || miniMapPanel == null) return;
      miniMapResizing = true;
      miniMapDragStart = Cursor.Position;
      miniMapResizeStart = miniMapPanel.Size;
    }

    private void MiniMapResizeGrip_MouseMove(object sender, MouseEventArgs e) {
      if (!miniMapResizing || miniMapPanel == null) return;
      Point now = Cursor.Position;
      int requestedWidth = Math.Max(160, miniMapResizeStart.Width + now.X - miniMapDragStart.X);
      if (miniMapImage != null) ResizeMiniMapToImageAspect(requestedWidth);
      else {
        int maxW = Math.Max(160, splitContainer3.Panel1.ClientSize.Width - miniMapPanel.Left - 4);
        int maxH = Math.Max(120, splitContainer3.Panel1.ClientSize.Height - miniMapPanel.Top - 4);
        miniMapPanel.Size = new Size(Math.Min(maxW, requestedWidth), Math.Max(120, Math.Min(maxH, miniMapResizeStart.Height + now.Y - miniMapDragStart.Y)));
      }
      PositionMiniMapResizeGrip();
      miniMapPicture.Invalidate();
    }

    private void PositionMiniMapResizeGrip() {
      if (miniMapResizeGrip == null || miniMapPanel == null) return;
      miniMapResizeGrip.Location = new Point(Math.Max(0, miniMapPanel.ClientSize.Width - miniMapResizeGrip.Width), Math.Max(0, miniMapPanel.ClientSize.Height - miniMapResizeGrip.Height));
      miniMapResizeGrip.BringToFront();
    }

    private void LayoutMiniMapWithinHost(bool keepCurrentPosition = true) {
      if (miniMapPanel == null || splitContainer3?.Panel1 == null) return;
      var host = splitContainer3.Panel1;
      int top = (worldToolbar?.Bottom ?? 0) + 6;
      int maxW = Math.Max(220, host.ClientSize.Width - 8);
      int maxH = Math.Max(170, host.ClientSize.Height - top - 4);
      miniMapPanel.Size = new Size(Math.Min(miniMapPanel.Width, maxW), Math.Min(miniMapPanel.Height, maxH));
      if (!keepCurrentPosition || miniMapPanel.Left < 4 || miniMapPanel.Top < top || miniMapPanel.Right > host.ClientSize.Width - 4 || miniMapPanel.Bottom > host.ClientSize.Height - 4) {
        int x = Math.Max(4, Math.Min(host.ClientSize.Width - miniMapPanel.Width - 4, miniMapPanel.Left));
        int y = Math.Max(top, Math.Min(host.ClientSize.Height - miniMapPanel.Height - 4, miniMapPanel.Top));
        if (miniMapPanel.Location == Point.Empty || miniMapPanel.Top < top) {
          x = Math.Max(4, host.ClientSize.Width - miniMapPanel.Width - 8);
          y = top;
        }
        miniMapPanel.Location = new Point(x, y);
      }
      PositionMiniMapResizeGrip();
      miniMapPanel.BringToFront();
    }

    private void ApplyWorldSettings() {
      panelRender?.SetSettings(worldSettings);
      if (panelRender != null && renderPanel.Visible) renderPanel.Focus();
    }

    private bool InitializeWorldRenderer(WorldRenderBackend backend) {
      panelRender?.Dispose();
      panelRender = new View_AREA(Handle, this, "renderPanel", backend);
      bool ok = panelRender.Init();
      if (!ok && backend == WorldRenderBackend.Hardware) {
        panelRender.Dispose();
        worldSettings.Backend = WorldRenderBackend.Warp;
        updatingWorldToolbar = true;
        cmbWorldBackend.SelectedIndex = 1;
        updatingWorldToolbar = false;
        panelRender = new View_AREA(Handle, this, "renderPanel", WorldRenderBackend.Warp);
        ok = panelRender.Init();
        if (ok) SetStatusLabel("Hardware D3D11 unavailable - using WARP software renderer.");
      }
      if (ok) panelRender.SetSettings(worldSettings);
      return ok;
    }

    private async Task SwitchWorldBackend(WorldRenderBackend backend) {
      if (worldSettings.Backend == backend || _closing) return;
      renderPanel.Visible = false;
      if (render != null && panelRender != null) {
        panelRender.StopRender();
        render.Join();
        render = null;
      }
      panelRender?.Clear();
      materials = new Dictionary<string, GR2_Material>();
      models = new Dictionary<ulong, GR2>();
      InvalidateMiniMapSnapshot();
      worldSettings.Backend = backend;
      if (!InitializeWorldRenderer(backend)) {
        MessageBox.Show("Could not initialize the selected Direct3D renderer.", "World Browser", MessageBoxButtons.OK, MessageBoxIcon.Error);
        return;
      }
      if (info != null && currentAreaId != 0) await Task.Run(() => PreviewAREA(info, currentAreaId));
      renderPanel.Visible = true;
    }

    private void DisableUI() {
      treeViewFast1.Enabled = false;
      if (txtWorldSearch != null) txtWorldSearch.Enabled = false;
      btnWorldBrowserHelp.Enabled = false;
      btnStopRender.Enabled = false;
      btnDataCollapse.Enabled = false;
      btnToggleDBO.Enabled = false;
      tvfDataViewer.Enabled = false;
      dgvDataViewer.Enabled = false;
    }

    private void EnableUI() {
      treeViewFast1.Enabled = true;
      if (txtWorldSearch != null) txtWorldSearch.Enabled = true;
      btnWorldBrowserHelp.Enabled = true;
      btnStopRender.Enabled = true;
      btnDataCollapse.Enabled = true;
      btnToggleDBO.Enabled = true;
      tvfDataViewer.Enabled = true;
      dgvDataViewer.Enabled = true;
    }


    private async void TreeViewFast1_AfterSelect(object sender, TreeViewEventArgs e) {
      TreeNode selectedNode = treeViewFast1.SelectedNode;
      NodeAsset tag = (NodeAsset)selectedNode.Tag;
      if (tag == null || !ulong.TryParse((tag.id ?? String.Empty).Trim('/'), out ulong selectedAreaId) || !IsKnownStrongholdArea(selectedAreaId))
        SetDecorationHookAvailability(false);
      if (tag.Obj != null || tag.objData != null || tag.dynObject != null) {
        if (panelRender != null) {
          if (render != null) {
            panelRender.StopRender();
            render.Join();
            panelRender.Clear();
          }
          renderPanel.Visible = false;
        }

        // ****************************************************************************************************
        materials = new Dictionary<string, GR2_Material>();
        models = new Dictionary<ulong, GR2>();
        // ****************************************************************************************************

        dgvDataViewer.DataSource = null;
        dgvDataViewer.Enabled = false;
        dataviewDict.Clear();
        tvfDataViewer.Nodes.Clear();
        tvfDataViewer.Enabled = false;
        if (tag.dynObject != null && tag.dynObject is HashFileInfo info1) {
          if (!ulong.TryParse(tag.id.Trim('/'), out currentAreaId)) {
            throw new InvalidOperationException("Could not determine area ID from world node: " + tag.id);
          }
          SetDecorationHookAvailability(IsKnownStrongholdArea(currentAreaId));
          Text = "World Browser - " + tag.displayName;
          InvalidateMiniMapSnapshot();
          toolStripStatusLabel1.Text = "Loading Area.dat File...";
          Refresh();
          info = info1;
          await Task.Run(() => PreviewAREA(info, currentAreaId));
          BuildDataViewer();
          renderPanel.Visible = true;
          treeViewFast1.Enabled = true;
          toolStripProgressBar1.Visible = false;

        } else if (tag.Obj != null) {
          GomObject obj = tag.Obj;
          NpcAppearance npcData = null;
          try {
            switch (obj.Name.Substring(0, 5)) {
              case "world":
                npcData = (NpcAppearance)currentDom.AppearanceLoader.Load(obj.Name);
                toolStripStatusLabel1.Text = "Loading WORLD Data ...";
                Refresh();
                //await Task.Run(() => previewNPC_GR2(npcData));
                toolStripStatusLabel1.Text = "NPP Loaded";
                break;
            }
            //buildDataViewer();
            renderPanel.Visible = true;
            treeViewFast1.Enabled = true;
            toolStripProgressBar1.Visible = false;
          }
          catch (Exception excep) {
            MessageBox.Show("Could not load NPC \r\n" + excep.ToString());
            toolStripStatusLabel1.Text = "NPC Load Error";
            toolStripProgressBar1.Visible = false;
            treeViewFast1.Enabled = true;
          }
        }
      }
    }

    private void BuildDataViewer() {
      tvfDataViewer.Nodes.Clear();
      dgvDataViewer.DataSource = null;

      if (models.Count > 0) {
        dataviewDict.Clear();
        dataviewDict.Add("/", new NodeAsset("/", "", "Root", null));
        dataviewDict.Add("/models", new NodeAsset("/models", "/", "Models", null));

        foreach (var model in models) {
          GR2 gr2 = model.Value;

          NodeAsset asset = new NodeAsset("/models/" + model.Key, "/models", model.Key.ToString(), gr2);
          dataviewDict.Add("/models/" + model.Key, asset);

          if (gr2.attachedModels.Count > 0) {
            dataviewDict.Add("/models/" + model.Key + "/attached", new NodeAsset("/models/" + model.Key + "/attached", "/models/" + model.Key, "Attached Models", null));
            foreach (var attachItem in model.Value.attachedModels) {
              GR2 gr2_attach = attachItem;
              NodeAsset attachAsset = new NodeAsset("/models/" + model.Key + "/attached/" + gr2_attach.filename, "/models/" + model.Key + "/attached", gr2_attach.filename, gr2_attach);
              dataviewDict.Add("/models/" + model.Key + "/attached/" + gr2_attach.filename, attachAsset);

              if (gr2_attach.numMaterials > 0) {
                if (!dataviewDict.ContainsKey("/models/" + model.Key + "/attached/" + gr2_attach.filename + "/materials"))
                  dataviewDict.Add("/models/" + model.Key + "/attached/" + gr2_attach.filename + "/materials", new NodeAsset("/models/" + model.Key + "/attached/" + gr2_attach.filename + "/materials", "/models/" + model.Key + "/attached/" + gr2_attach.filename, "Materials", null));
                foreach (var material in gr2_attach.materials) {
                  GR2_Material gr2_material = material;
                  NodeAsset attachMaterial = new NodeAsset("/models/" + model.Key + "/attached/" + gr2_attach.filename + "/materials/" + gr2_material.materialName, "/models/" + model.Key + "/attached/" + gr2_attach.filename + "/materials", gr2_material.materialName, gr2_material);
                  if (!dataviewDict.ContainsKey("/models/" + model.Key + "/attached/" + gr2_attach.filename + "/materials/" + gr2_material.materialName))
                    dataviewDict.Add("/models/" + model.Key + "/attached/" + gr2_attach.filename + "/materials/" + gr2_material.materialName, attachMaterial);
                }
              }

              if (gr2_attach.numMeshes > 0) {
                if (!dataviewDict.ContainsKey("/models/" + model.Key + "/attached/" + gr2_attach.filename + "/meshes"))
                  dataviewDict.Add("/models/" + model.Key + "/attached/" + gr2_attach.filename + "/meshes", new NodeAsset("/models/" + model.Key + "/attached/" + gr2_attach.filename + "/meshes", "/models/" + model.Key + "/attached/" + gr2_attach.filename, "Meshes", null));
                foreach (var mesh in gr2_attach.meshes) {
                  GR2_Mesh gr2_mesh = mesh;
                  NodeAsset meshAsset = new NodeAsset("/models/" + model.Key + "/meshes/" + gr2_mesh.meshName, "/models/" + model.Key + "/attached/" + gr2_attach.filename + "/meshes", gr2_mesh.meshName, gr2_mesh);
                  if (!dataviewDict.ContainsKey("/models/" + model.Key + "/meshes/" + gr2_mesh.meshName))
                    dataviewDict.Add("/models/" + model.Key + "/meshes/" + gr2_mesh.meshName, meshAsset);
                }
              }
            }
          }

          if (gr2.numMeshes > 0) {
            if (!dataviewDict.ContainsKey("/models/" + model.Key + "/meshes"))
              dataviewDict.Add("/models/" + model.Key + "/meshes", new NodeAsset("/models/" + model.Key + "/meshes", "/models/" + model.Key, "Meshes", null));
            foreach (var mesh in model.Value.meshes) {
              GR2_Mesh gr2_mesh = mesh;
              NodeAsset meshAsset = new NodeAsset("/models/" + model.Key + "/meshes/" + gr2_mesh.meshName, "/models/" + model.Key + "/meshes", gr2_mesh.meshName, gr2_mesh);
              if (!dataviewDict.ContainsKey("/models/" + model.Key + "/meshes/" + gr2_mesh.meshName))
                dataviewDict.Add("/models/" + model.Key + "/meshes/" + gr2_mesh.meshName, meshAsset);
            }
          }

          if (gr2.numMaterials > 0) {
            if (!dataviewDict.ContainsKey("/models/" + model.Key + "/materials"))
              dataviewDict.Add("/models/" + model.Key + "/materials", new NodeAsset("/models/" + model.Key + "/materials", "/models/" + model.Key, "Materials", null));
            foreach (var material in model.Value.materials) {
              GR2_Material gr2_material = material;
              NodeAsset materialAsset = new NodeAsset("/models/" + model.Key + "/materials/" + gr2_material.materialName, "/models/" + model.Key + "/materials", gr2_material.materialName, gr2_material);
              if (!dataviewDict.ContainsKey("/models/" + model.Key + "/materials/" + gr2_material.materialName))
                dataviewDict.Add("/models/" + model.Key + "/materials/" + gr2_material.materialName, materialAsset);
            }
          }
        }

        string getId(NodeAsset x) => x.id;
        string getParentId(NodeAsset x) => x.parentId;
        string getDisplayName(NodeAsset x) => x.displayName;

        tvfDataViewer.SuspendLayout();
        tvfDataViewer.BeginUpdate();
        tvfDataViewer.LoadItems<NodeAsset>(dataviewDict, getId, getParentId, getDisplayName);
        tvfDataViewer.Sort();
        tvfDataViewer.EndUpdate();
        tvfDataViewer.ResumeLayout();
        tvfDataViewer.Enabled = true;
        tvfDataViewer.Nodes[0].Expand();
        dgvDataViewer.Enabled = true;
      }
    }

    /*
    private void buildDataViewer()
    {
        tvfDataViewer.Nodes.Clear();
        dgvDataViewer.DataSource = null;
        if (models.Count > 0)
        {
            dataviewDict.Clear();
            dataviewDict.Add("/", new NodeAsset("/", "", "Root", (GomObject)null));
            dataviewDict.Add("/models", new NodeAsset("/models", "/", "Models", (GomObject)null));
            foreach (var model in models)
            {
                GR2 gr2 = model.Value;
                NodeAsset asset = new NodeAsset("/models/" + model.Key, "/models", model.Key, gr2);
                dataviewDict.Add("/models/" + model.Key, asset);
                if (gr2.attachedModels.Count > 0)
                {
                    dataviewDict.Add("/models/" + model.Key + "/attached", new NodeAsset("/models/" + model.Key + "/attached", "/models/" + model.Key, "Attached Models", (GomObject)null));
                    foreach (var attachItem in model.Value.attachedModels)
                    {
                        GR2 gr2_attach = attachItem;
                        NodeAsset attachAsset = new NodeAsset("/models/" + model.Key + "/attached/" + gr2_attach.filename, "/models/" + model.Key + "/attached", gr2_attach.filename, gr2_attach);
                        dataviewDict.Add("/models/" + model.Key + "/attached/" + gr2_attach.filename, attachAsset);

                        if (gr2_attach.numMaterials > 0)
                        {
                            if (!dataviewDict.ContainsKey("/models/" + model.Key + "/attached/" + gr2_attach.filename + "/materials"))
                                dataviewDict.Add("/models/" + model.Key + "/attached/" + gr2_attach.filename + "/materials", new NodeAsset("/models/" + model.Key + "/attached/" + gr2_attach.filename + "/materials", "/models/" + model.Key + "/attached/" + gr2_attach.filename, "Materials", (GomObject)null));
                            foreach (var material in gr2_attach.materials)
                            {
                                GR2_Material gr2_material = material;
                                NodeAsset attachMaterial = new NodeAsset("/models/" + model.Key + "/attached/" + gr2_attach.filename + "/materials/" + gr2_material.materialName, "/models/" + model.Key + "/attached/" + gr2_attach.filename + "/materials", gr2_material.materialName, gr2_material);
                                if (!dataviewDict.ContainsKey("/models/" + model.Key + "/attached/" + gr2_attach.filename + "/materials/" + gr2_material.materialName))
                                    dataviewDict.Add("/models/" + model.Key + "/attached/" + gr2_attach.filename + "/materials/" + gr2_material.materialName, attachMaterial);
                            }
                        }

                        if (gr2_attach.numMeshes > 0)
                        {
                            if (!dataviewDict.ContainsKey("/models/" + model.Key + "/attached/" + gr2_attach.filename + "/meshes"))
                                dataviewDict.Add("/models/" + model.Key + "/attached/" + gr2_attach.filename + "/meshes", new NodeAsset("/models/" + model.Key + "/attached/" + gr2_attach.filename + "/meshes", "/models/" + model.Key + "/attached/" + gr2_attach.filename, "Meshes", (GomObject)null));
                            foreach (var mesh in gr2_attach.meshes)
                            {
                                GR2_Mesh gr2_mesh = mesh;
                                NodeAsset meshAsset = new NodeAsset("/models/" + model.Key + "/meshes/" + gr2_mesh.meshName, "/models/" + model.Key + "/attached/" + gr2_attach.filename + "/meshes", gr2_mesh.meshName, gr2_mesh);
                                if (!dataviewDict.ContainsKey("/models/" + model.Key + "/meshes/" + gr2_mesh.meshName))
                                    dataviewDict.Add("/models/" + model.Key + "/meshes/" + gr2_mesh.meshName, meshAsset);
                            }
                        }
                    }
                }

                if (gr2.numMeshes > 0)
                {
                    if (!dataviewDict.ContainsKey("/models/" + model.Key + "/meshes"))
                        dataviewDict.Add("/models/" + model.Key + "/meshes", new NodeAsset("/models/" + model.Key + "/meshes", "/models/" + model.Key, "Meshes", (GomObject)null));
                    foreach (var mesh in model.Value.meshes)
                    {
                        GR2_Mesh gr2_mesh = mesh;
                        NodeAsset meshAsset = new NodeAsset("/models/" + model.Key + "/meshes/" + gr2_mesh.meshName, "/models/" + model.Key + "/meshes", gr2_mesh.meshName, gr2_mesh);
                        if (!dataviewDict.ContainsKey("/models/" + model.Key + "/meshes/" + gr2_mesh.meshName))
                            dataviewDict.Add("/models/" + model.Key + "/meshes/" + gr2_mesh.meshName, meshAsset);
                    }
                }

                if (gr2.numMaterials > 0)
                {
                    if (!dataviewDict.ContainsKey("/models/" + model.Key + "/materials"))
                        dataviewDict.Add("/models/" + model.Key + "/materials", new NodeAsset("/models/" + model.Key + "/materials", "/models/" + model.Key, "Materials", (GomObject)null));
                    foreach (var material in model.Value.materials)
                    {
                        GR2_Material gr2_material = material;
                        NodeAsset materialAsset = new NodeAsset("/models/" + model.Key + "/materials/" + gr2_material.materialName, "/models/" + model.Key + "/materials", gr2_material.materialName, gr2_material);
                        if (!dataviewDict.ContainsKey("/models/" + model.Key + "/materials/" + gr2_material.materialName))
                            dataviewDict.Add("/models/" + model.Key + "/materials/" + gr2_material.materialName, materialAsset);
                    }
                }

                if (gr2.numBones > 0)
                {
                    if (!dataviewDict.ContainsKey("/models/" + model.Key + "/bones"))
                        dataviewDict.Add("/models/" + model.Key + "/bones", new NodeAsset("/models/" + model.Key + "/bones", "/models/" + model.Key, "Bones", (GomObject)null));
                    foreach (var bone in model.Value.skeleton_bones)
                    {
                        GR2_Bone_Skeleton gr2_bone = bone;
                        NodeAsset materialAsset = new NodeAsset("/models/" + model.Key + "/bones/" + gr2_bone.boneName, "/models/" + model.Key + "/bones", gr2_bone.boneIndex.ToString() + " - " + gr2_bone.boneName, gr2_bone);
                        if (!dataviewDict.ContainsKey("/models/" + model.Key + "/bones/" + gr2_bone.boneName))
                            dataviewDict.Add("/models/" + model.Key + "/bones/" + gr2_bone.boneName, materialAsset);
                    }
                }
            }
            Func<NodeAsset, string> getId = (x => x.Id);
            Func<NodeAsset, string> getParentId = (x => x.parentId);
            Func<NodeAsset, string> getDisplayName = (x => x.displayName);
            tvfDataViewer.SuspendLayout();
            tvfDataViewer.BeginUpdate();
            tvfDataViewer.LoadItems<NodeAsset>(dataviewDict, getId, getParentId, getDisplayName);
            tvfDataViewer.Sort();
            tvfDataViewer.EndUpdate();
            tvfDataViewer.ResumeLayout();
            tvfDataViewer.Enabled = true;
            tvfDataViewer.Nodes[0].Expand();
            dgvDataViewer.Enabled = true;
        }
    }
    */

    private void LoadWorldMapPages(FileFormats.Area targetArea, ulong areaId) {
      if (targetArea == null || currentDom == null) return;
      try {
        GomObject mapDataObj = currentDom.GetObject("world.areas." + areaId + ".mapdata");
        List<object> pages = mapDataObj?.Data.ValueOrDefault<List<object>>("mapDataContainerMapDataList", null);
        if (pages == null) return;
        foreach (GomObjectData page in pages.OfType<GomObjectData>()) {
          string mapName = page.ValueOrDefault<string>("mapName", null);
          if (string.IsNullOrWhiteSpace(mapName)) continue;
          List<float> min = page.ValueOrDefault<List<float>>("mapPageMinCoord", null);
          List<float> max = page.ValueOrDefault<List<float>>("mapPageMaxCoord", null);
          if (min == null || min.Count < 3 || max == null || max.Count < 3) continue;
          List<float> miniMin = page.ValueOrDefault<List<float>>("mapPageMiniMinCoord", null);
          List<float> miniMax = page.ValueOrDefault<List<float>>("mapPageMiniMaxCoord", null);
          string image = "/resources/world/areas/" + areaId + "/" + mapName + "_r.dds";
          using File imageFile = currentAssets.FindFile(image);
          targetArea.MapPages.Add(new AreaMapPage {
            Guid = page.ValueOrDefault<long>("mapPageGUID", 0),
            SId = page.ValueOrDefault<long>("mapNameSId", 0),
            ParentId = page.ValueOrDefault<long>("mapParentNameSId", 0),
            MapName = mapName,
            ImagePath = image,
            HasImage = imageFile != null,
            Min = new SlimDX.Vector3(min[0], min[1], min[2]),
            Max = new SlimDX.Vector3(max[0], max[1], max[2]),
            MiniMapMin = miniMin != null && miniMin.Count >= 3 ? new SlimDX.Vector2(miniMin[0], miniMin[2]) : new SlimDX.Vector2(min[0], min[2]),
            MiniMapMax = miniMax != null && miniMax.Count >= 3 ? new SlimDX.Vector2(miniMax[0], miniMax[2]) : new SlimDX.Vector2(max[0], max[2])
          });
        }
      } catch (Exception ex) {
        System.Diagnostics.Debug.WriteLine("Could not load World Viewer map pages: " + ex.Message);
      }
    }

#pragma warning disable CS1998, CS4014
    private bool ShouldIgnoreAreaModel(AreaAsset asset) {
      if (asset == null || panelRender == null || panelRender.ignoreList == null || panelRender.ignoreList.Count == 0) return false;
      string path = (asset.Path ?? String.Empty).Replace('\\', '/').TrimStart('/').ToLowerInvariant();
      string file = path;
      int slash = file.LastIndexOf('/');
      if (slash >= 0) file = file.Substring(slash + 1);

      foreach (string raw in panelRender.ignoreList) {
        string token = (raw ?? String.Empty).Trim().ToLowerInvariant();
        if (token.Length == 0) continue;
        switch (token) {
          case "dbo":
            // DBO = design-blockout/editor placeholder geometry. Jedipedia's normal world view does not draw it.
            // Stronghold rooms contain several of these authored stand-ins; one of them looks like a giant turret
            // when rendered as ordinary art. Match both the common dbo_* filenames and designblockout folders.
            if (file.StartsWith("dbo_", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("/dbo/") || path.Contains("/dbo_") ||
                path.Contains("designblockout")) return true;
            break;
          case "fadeportal":
            if (file.Contains("fadeportal") || path.Contains("/fadeportal/")) return true;
            break;
          case "occluder":
            if (file.StartsWith("occluder", StringComparison.OrdinalIgnoreCase) ||
                file.Contains("_occluder") || path.Contains("/occluder/")) return true;
            break;
          case "collision":
            // Whole-file collision helpers only. Do not reject arbitrary visible GR2 meshes just because a mesh name
            // contains the word collision; mesh-level classification is handled by View_AREA.
            if (file.StartsWith("collision", StringComparison.OrdinalIgnoreCase) ||
                file.Contains("_collision") || path.Contains("/collision/")) return true;
            break;
          default:
            if (path.Contains(token)) return true;
            break;
        }
      }
      return false;
    }

    private async Task PreviewAREA(HashFileInfo info, ulong areaId) {
      ShowWorldLoading("Loading world…", "Reading area.dat and phase metadata…");
      try {
        area = new FileFormats.Area(info, currentAssets, areaId);
        area.Read();
        UpdateWorldLoading("Loading map pages and phase metadata…");
        LoadWorldMapPages(area, areaId);
        worldImplicitPhaseName = ResolveWorldImplicitPhaseName(areaId);

        int decorationHookPlacements = area.RoomList.Sum(room => room.InstancesById.Values.Count(instance => instance.HasDecorationHook));
        SetDecorationHookAvailability(IsKnownStrongholdArea(areaId), decorationHookPlacements);

        List<AreaAsset> assetModels = area.AssetsByExtension.ContainsKey("gr2")
          ? area.AssetsByExtension["gr2"]
          : new List<AreaAsset>();

        int totalInstances = 0, matchedAssets = 0, ignoredGr2Assets = 0, missingGr2Files = 0, loadedModels = 0, failedGr2Loads = 0;
        int roomIndex = 0;
        foreach (FileFormats.Room room in area.RoomList) {
          roomIndex++;
          UpdateWorldLoading("Loading room geometry " + roomIndex + " / " + Math.Max(1, area.RoomList.Count) + "…", roomIndex, Math.Max(1, area.RoomList.Count));
          foreach (KeyValuePair<ulong, List<FileFormats.AssetInstance>> kvp in room.InstancesByAssetId) {
            AreaAsset asset = assetModels.Find(x => x.Id == kvp.Key);
            if (asset != null) {
              matchedAssets++;
              if (ShouldIgnoreAreaModel(asset)) { ignoredGr2Assets++; continue; }
              foreach (FileFormats.AssetInstance instance in kvp.Value) {
                totalInstances++;
                if (instance.hidden || models.Keys.Contains(instance.assetID)) continue;
                string modelPath = "/resources/" + asset.Path.Replace("\\", "/") + ".gr2";
                File modelFile = currentAssets.FindFile(modelPath);
                if (modelFile == null) { missingGr2Files++; continue; }
                try {
                  using Stream modelStream = modelFile.OpenCopyInMemory();
                  using BinaryReader br = new BinaryReader(modelStream);
                  GR2 gr2_model = new GR2(br, asset.Path, materials);
                  models.Add(asset.Id, gr2_model);
                  loadedModels++;
                } catch { failedGr2Loads++; }
              }
            }
          }
        }

        UpdateWorldLoading("Loading utilities, NPCs, SPN objects and animations…");
        LoadWorldUtilityModels();
        LoadWorldNpcPlacements();

        SetStatusLabel(string.Format(
          "Rooms:{0} GR2Assets:{1} Instanzen:{2} zugeordnet:{3} ignoriert:{4} geladen:{5} GR2fehlt:{6} GR2Fehler:{7} | Utilities:{8} NPCs:{9} SPN:{10} | {11}",
          area.RoomList.Count, assetModels.Count, totalInstances, matchedAssets, ignoredGr2Assets,
          loadedModels, missingGr2Files, failedGr2Loads, worldUtilityModels.Count, worldNpcPlacements.Count, worldSpnPlacements.Count, area.DebugHeaderInfo));

        UpdateWorldLoading("Uploading scene to the renderer…");
        panelRender.SetSettings(worldSettings);
        panelRender.SetImplicitPhaseName(worldImplicitPhaseName);
        panelRender.LoadModel(models, materials, area.RoomList, area.Id.ToString(), area, worldUtilityModels, worldNpcPlacements, worldSpnPlacements);
        render = new Thread(panelRender.StartRender) { IsBackground = true };
        render.Start();
        if (miniMapPanel != null && miniMapPanel.Visible) panelRender.RequestMiniMapSnapshot();
      } finally {
        HideWorldLoading();
      }
    }
#pragma warning restore CS1998, CS4014

    private void WorldBrowser_FormClosing(object sender, FormClosingEventArgs e) {
      _closing = true;
      worldWindowActive = false;
      Application.RemoveMessageFilter(this);
      worldOverlayTimer?.Stop();
      worldOverlayTimer?.Dispose();
      worldOverlayTimer = null;
      miniMapImage?.Dispose();
      miniMapImage = null;
      if (render != null) {
        panelRender.StopRender();
        render.Join();
        panelRender.Clear();
      }
      materials.Clear();
      models.Clear();
      panelRender?.Dispose();
      assetDict.Clear();
      nodeKeys.Clear();
      Dispose();
    }

    public void SetStatusLabel(string message) {
      if (statusStrip1.InvokeRequired) {
        statusStrip1.Invoke(new Action(() => SetStatusLabel(message)));
      } else {
        toolStripStatusLabel1.Text = message;
      }
    }

    private void RenderPanel_MouseHover(object sender, EventArgs e) {
      ActivateWorldRenderInput();
    }

    private void RenderPanel_Resize(object sender, EventArgs e) {
      if (panelRender != null) {
        if (renderPanel.Width != panelRender.ClientWidth || renderPanel.Height != panelRender.ClientHeight)
          panelRender.SetSize(renderPanel.Height, renderPanel.Width);
      }
    }

    private void BtnWorldBrowserHelp_Click(object sender, EventArgs e) {
      WorldBrowserHelp formHelp = new WorldBrowserHelp();
      formHelp.Show();
    }

    private void BtnStopRender_Click(object sender, EventArgs e) {
      if (panelRender != null) {
        renderPanel.Visible = false;
        if (render != null) {
          panelRender.StopRender();
          render.Join();
          panelRender.Clear();
        }
      }
    }

    private void BtnDataCollapse_Click(object sender, EventArgs e) {
      bool current = splitContainer3.Panel2Collapsed;
      if (current) {
        splitContainer3.Panel2Collapsed = false;
        btnDataCollapse.Text = "Hide Data Viewer";
      } else {
        splitContainer3.Panel2Collapsed = true;
        btnDataCollapse.Text = "Show Data Viewer";
      }
    }

    private async void BtnToggleDBO_Click(object sender, EventArgs e) {
      if (showDBO) {
        if (panelRender != null) {
          renderPanel.Visible = false;

          if (render != null) {
            panelRender.StopRender();
            render.Join();
            panelRender.Clear();
            panelRender.ignoreList = new List<string> {
                            "collision", "dbo", "fadeportal", "occluder"
                        };
            showDBO = false;
            await PreviewAREA(info, currentAreaId);
            renderPanel.Visible = true;
            btnToggleDBO.Text = "Show DBO";
          } else {
            panelRender.ignoreList = new List<string> {
                            "collision", "dbo", "fadeportal", "occluder"
                        };
            showDBO = false;
            btnToggleDBO.Text = "Show DBO";
          }
        }
      } else {
        if (panelRender != null) {
          renderPanel.Visible = false;

          if (render != null) {
            panelRender.StopRender();
            render.Join();
            panelRender.Clear();
            panelRender.ignoreList = new List<string> {
                            "collision", "fadeportal", "occluder"
                        };
            showDBO = true;
            await PreviewAREA(info, currentAreaId);
            renderPanel.Visible = true;
            btnToggleDBO.Text = "Hide DBO";
          } else {
            panelRender.ignoreList = new List<string> {
                            "collision", "fadeportal", "occluder"
                        };
            showDBO = true;
            btnToggleDBO.Text = "Hide DBO";
          }
        }
      }
    }

    private void TvfDataViewer_AfterSelect(object sender, TreeViewEventArgs e) {
      TreeNode selectedNode = tvfDataViewer.SelectedNode;
      NodeAsset tag = (NodeAsset)selectedNode.Tag;
      if (tag.dynObject != null) {
        DataTable dt = new DataTable();
        dt.Columns.Add("Property");
        dt.Columns.Add("Value");
        dgvDataViewer.DataSource = dt;
        dgvDataViewer.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.AllCells;
        if (tag.dynObject is GR2 model) {
          dt.Rows.Add(new string[] { "Render", model.enabled.NullSafeToString() });
          dt.Rows.Add(new string[] { "# Attachments", model.numAttach.NullSafeToString() });
          dt.Rows.Add(new string[] { "# Bones", model.numBones.NullSafeToString() });
          dt.Rows.Add(new string[] { "# Meshes", model.numMeshes.NullSafeToString() });
          dt.Rows.Add(new string[] { "# Materials", model.numMaterials.NullSafeToString() });
        } else if (tag.dynObject is GR2_Material material) {
          dt.Rows.Add(new string[] { "Type", material.derived.NullSafeToString() });
          dt.Rows.Add(new string[] { "DiffuseMap", material.diffuseDDS.NullSafeToString() });
          dt.Rows.Add(new string[] { "RotationMap1", material.rotationDDS.NullSafeToString() });
          dt.Rows.Add(new string[] { "GlossMap", material.glossDDS.NullSafeToString() });
          dt.Rows.Add(new string[] { "PaletteMask", material.paletteMaskDDS.NullSafeToString() });
          dt.Rows.Add(new string[] { "PaletteMaskMap", material.paletteMaskDDS.NullSafeToString() });
          dt.Rows.Add(new string[] { "UsesEmissive", material.useEmissive.NullSafeToString() });
          dt.Rows.Add(new string[] { "Pal 1", material.palette1.NullSafeToString() });
          dt.Rows.Add(new string[] { "Pal 1 Met Spec", material.palette1MetSpec.NullSafeToString() });
          dt.Rows.Add(new string[] { "Pal 1 Spec", material.palette1Spec.NullSafeToString() });
          dt.Rows.Add(new string[] { "Pal 2", material.palette2.NullSafeToString() });
          dt.Rows.Add(new string[] { "Pal 2 Met Spec", material.palette2MetSpec.NullSafeToString() });
          dt.Rows.Add(new string[] { "Pal 2 Spec", material.palette2Spec.NullSafeToString() });
        } else if (tag.dynObject is GR2_Mesh mesh) {
          dt.Rows.Add(new string[] { "# Bones", mesh.numBones.NullSafeToString() });
          dt.Rows.Add(new string[] { "# Pieces", mesh.numPieces.NullSafeToString() });
          dt.Rows.Add(new string[] { "# Vertices", mesh.numVerts.NullSafeToString() });
        } else if (tag.dynObject is GR2_Bone_Skeleton bone) {
          dt.Rows.Add(new string[] { "Bone Name", bone.boneName.NullSafeToString() });
          dt.Rows.Add(new string[] { "Bone Index", bone.boneIndex.NullSafeToString() });
          dt.Rows.Add(new string[] { "Bone Parent Index", bone.parentBoneIndex.NullSafeToString() });
        } else {

        }
      }
    }

    private void TvfDataViewer_MouseUp(object sender, MouseEventArgs e) {
      if (e.Button == MouseButtons.Right) {
        tvfDataViewer.SelectedNode = tvfDataViewer.GetNodeAt(e.X, e.Y);

        if (tvfDataViewer.SelectedNode != null && tvfDataViewer.SelectedNode.Tag is NodeAsset asset1) {
          NodeAsset asset = asset1;
          if (asset.dynObject != null && asset.dynObject is GR2)
            contextMenuStrip2.Show(tvfDataViewer, e.Location);
          // else if (asset.dynObject is GR2_Material)
          //     contextMenuStrip4.Show(tvfDataViewer, e.Location);
        }
      }
    }

    private void ToolStripMenuItem1_Click(object sender, EventArgs e) {
      NodeAsset asset = (NodeAsset)tvfDataViewer.SelectedNode.Tag;
      if (asset.dynObject != null && asset.dynObject is GR2 gR) {
        GR2 model = gR;
        model.enabled = !model.enabled;
      }
    }

    private void ToolStripMenuItem2_Click(object sender, EventArgs e) {
      /*
      NodeAsset asset = (NodeAsset)tvfDataViewer.SelectedNode.Tag;
      if (asset.dynObject != null && asset.dynObject is GR2_Material)
      {
          GR2_Material material = (GR2_Material)asset.dynObject;
          WorldBrowserViewMaterial matView = new WorldBrowserViewMaterial(material);
          matView.Show();
      }*/
    }
  }
}
