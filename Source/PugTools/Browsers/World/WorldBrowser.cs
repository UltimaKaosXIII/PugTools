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
using Newtonsoft.Json;
using nsHashDictionary;
using TorArchive;
using File = TorArchive.File;
//using TreeViewFast.Controls;

namespace PugTools {
  public partial class WorldBrowser : Form, IMessageFilter {
    private sealed class WorldBookmarkRecord {
      public int Id { get; set; }
      public string Name { get; set; }
      public float X { get; set; }
      public float Y { get; set; }
      public float Z { get; set; }
      public float RotX { get; set; }
      public float RotY { get; set; }
      public float RotZ { get; set; }
    }

    private sealed class WonkDestinationItem {
      public string Name;
      public string Description;
      public ulong DestinationId;
      public AreaMapNote Note;
    }

    private sealed class WonkPackageInfo {
      public long PackageId;
      public string Title;
      public readonly List<WonkDestinationItem> Destinations = new List<WonkDestinationItem>();
    }

    public bool _closing = false;
    private readonly String worldAssetLocation;
    private readonly Boolean worldUsePts;
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
    private ToolStripComboBox cmbWorldTextureQuality;
    private ToolStripComboBox cmbWorldShadowQuality;
    private ToolStripComboBox cmbWorldViewDistance;
    private ToolStripComboBox cmbWorldFov;
    private ToolStripComboBox cmbWorldCameraSpeed;
    private ToolStrip worldListToolbar;
    private ToolStripTextBox txtWorldSearch;
    private ToolStripDropDownButton btnWorldRenderMenu;
    private ToolStripDropDownButton btnWorldLayersMenu;
    private ToolStripDropDownButton btnWorldNavigationMenu;
    private ToolStripDropDownButton btnWorldToolsMenu;
    private ToolStripMenuItem btnWorldMiniMap;
    private ToolStripMenuItem btnWorldMapEntireArea;
    private ToolStripMenuItem btnWorldMapnoteNavigator;
    private ToolStripMenuItem btnWorldBookmarks;
    private Dictionary<string, List<WorldBookmarkRecord>> worldBookmarkStore = new Dictionary<string, List<WorldBookmarkRecord>>(StringComparer.Ordinal);
    private bool worldBookmarksLoaded;
    private ToolStripMenuItem btnWorldDecorationHooks;
    private ToolStripMenuItem btnWorldInspectModel;
    private ToolStripMenuItem btnWorldSelectedObject;
    private ToolStripMenuItem btnWorldCurrentRegions;
    private ToolStripMenuItem btnWorldVolumeList;
    private ToolStripMenuItem btnWorldUtilities;
    private bool worldAuthoringGeometryLoaded;
    private bool worldAuthoringGeometryReloading;
    private ToolStripMenuItem btnWorldNpcs;
    private ToolStripMenuItem btnWorldRooms;
    private string worldRoomFilterText = String.Empty;
    private ToolStripMenuItem btnWorldWalkingMode;
    private ToolStripMenuItem btnWorldStats;
    private bool worldModelInspectPending;
    private bool worldSelectionClickPending;
    private Point worldSelectionClickStart;
    private MouseButtons worldSelectionClickButton;
    private bool worldTaxiClickPending;
    private Point worldTaxiClickStart;
    private MouseButtons worldTaxiClickButton;
    private Form worldSelectionInfoForm;
    private Label worldSelectionInfoTitle;
    private RichTextBox worldSelectionInfoText;
    private bool worldInterfaceHidden;
    private bool worldInterfaceLeftPanelWasCollapsed;
    private bool worldInterfaceMiniMapWasVisible;
    private readonly Dictionary<string, GR2> worldUtilityModels = new Dictionary<string, GR2>(StringComparer.OrdinalIgnoreCase);
    private readonly List<WorldNpcPlacement> worldNpcPlacements = new List<WorldNpcPlacement>();
    private readonly List<WorldSpnPlacement> worldSpnPlacements = new List<WorldSpnPlacement>();
    private Panel miniMapPanel;
    private Panel worldVolumePanel;
    private Label worldVolumeTitle;
    private FlowLayoutPanel worldVolumeEntries;
    private Button worldVolumeClose;
    private ToolTip worldVolumeToolTip;
    private string worldVolumeSignature = String.Empty;
    private Panel miniMapTitleBar;
    private Label miniMapTitle;
    private Button miniMapClose;
    private Button miniMapScopeButton;
    private Button miniMapSourceButton;
    private PictureBox miniMapPicture;
    private Panel miniMapResizeGrip;
    private readonly List<Panel> miniMapResizeHandles = new List<Panel>();
    [Flags]
    private enum MiniMapResizeEdges { None = 0, Left = 1, Right = 2, Top = 4, Bottom = 8 }
    private MiniMapResizeEdges miniMapResizeEdges;
    private bool miniMapUserResized;
    private Point miniMapResizeLocationStart;
    private Bitmap miniMapImage;
    private readonly Dictionary<string, Image> worldMapIconImages = new Dictionary<string, Image>(StringComparer.OrdinalIgnoreCase);
    private ToolTip worldMapNoteToolTip;
    private AreaMapNote worldMapHoveredNote;
    private Control worldMapHoverControl;
    private float miniMapMinX, miniMapMaxX, miniMapMinZ, miniMapMaxZ;
    private float miniMapZoom = 1f;
    private float miniMapCenterX, miniMapCenterZ;
    private const float MiniMapMaxZoom = 32f;
    private const float MiniMapDefaultFollowZoom = 2f;
    private bool miniMapMoving, miniMapResizing, miniMapPanning, miniMapSuppressClick;
    private bool miniMapFollowRefreshPending;
    private bool miniMapFollowRestoreView;
    private bool miniMapFollowFullExtentActive;
    private float miniMapFollowRestoreZoom = 1f;
    private bool worldFullMapActive;
    private Point miniMapDragStart, miniMapPanelStart, miniMapPanStart;
    private Size miniMapResizeStart;
    private float miniMapPanStartCenterX, miniMapPanStartCenterZ;
    private System.Windows.Forms.Timer worldOverlayTimer;
    private ToolStripStatusLabel toolStripPositionStatus;
    private ToolStripStatusLabel toolStripPhaseStatus;
    private ToolStripDropDownButton toolStripShipDestinationStatus;
    private ToolStripStatusLabel toolStripRoomStatus;
    private ToolStripStatusLabel toolStripPerfStatus;
    private Label phaseBannerLabel;
    private Panel worldLoadingOverlay;
    private Panel worldLoadingCard;
    private Label worldLoadingTitle;
    private Label worldLoadingDetails;
    private ProgressBar worldLoadingProgress;
    private bool worldStreamingLoading;
    private string lastPhaseBannerText = String.Empty;
    private bool updatingWorldToolbar;
    private volatile bool worldWindowActive;
    private volatile bool worldTextInputActive;
    private volatile bool worldRenderInputActive;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_MOUSEWHEEL = 0x020A;

    internal bool WorldWindowInputEnabled => !_closing && worldWindowActive && !worldTextInputActive;
    internal bool WorldKeyboardInputEnabled => WorldWindowInputEnabled && worldRenderInputActive;

    public WorldBrowser(string assetLocation, bool usePTS) {
      worldAssetLocation = assetLocation;
      worldUsePts = usePTS;
      InitializeComponent();
      Activated += (_, __) => worldWindowActive = true;
      Deactivate += (_, __) => worldWindowActive = false;
      renderPanel.MouseEnter += (_, __) => ActivateWorldRenderInput();
      renderPanel.MouseDown += RenderPanel_MouseDown;
      renderPanel.MouseMove += RenderPanel_MouseMove;
      renderPanel.MouseUp += RenderPanel_MouseUp;
      treeViewFast1.MouseEnter += (_, __) => worldRenderInputActive = false;
      treeViewFast1.MouseDown += (_, __) => worldRenderInputActive = false;
      Application.AddMessageFilter(this);
      InitializeWorldListToolbar();
      InitializeWorldRenderToolbar();
      InitializeWorldInstanceFinder();
      InitializeWorldCutawayUi();
      InitializeWorldMiniMap();
      InitializeWorldVolumePanel();
      InitializeWorldRoomStatus();
      InitializeWorldLoadingOverlay();
      splitContainer1.Panel2.Resize += (_, __) => {
        if (!worldInterfaceHidden) return;
        LayoutWorldViewerHost();
        LayoutWorldRenderPanel();
      };

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
    private readonly Dictionary<ulong, string> worldAreaInternalNames = new Dictionary<ulong, string>();
    private readonly HashSet<ulong> worldSystemGeneratedAreaIds = new HashSet<ulong>();
    private const string WorldLiveContentCategory = "Live Content";
    private const string WorldSystemGeneratedGroup = "System Generated";

    // Shared with the Asset Browser so both trees use the authored area.dat name when the
    // installed client knows more than the bundled Jedipedia catalog.
    internal static string ReadAreaInternalName(File areaFile) {
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
      ResetWorldLegacyPrototypeNameIndex();
      LocalizationResolver.Apply(currentAssets, Config.Language);
      currentDom = DomHandler.Instance.GetCurrentDOM(currentAssets);
      //this.currentDom.ami.Load();

      // mapareasdata exists in most releases, but early 32-bit/Beta GOMs may omit its resolved name or the table
      // entirely.  Treat it as an optional convenience index rather than a prerequisite for opening the World
      // Browser.  The installed area.dat scan below is the authoritative fallback.
      mapAreaData = new List<object>();
      try {
        GomObject mapAreasNode = WorldResolveGomObject("mapareasdata");
        object rawRows = WorldInteractionDataValue(mapAreasNode?.Data, "utlDatatableRows", "4611686018434320018");
        mapAreaData.AddRange(WorldInteractionListEntries(rawRows));
      } catch (Exception ex) {
        System.Diagnostics.Debug.WriteLine("World mapareasdata unavailable: " + ex.Message);
      }
      StringTable strTable = null;
      try { strTable = currentDom.StringTable.Find("str.sys.worldmap"); } catch { }

      foreach (object rawArea in mapAreaData) {
        List<object> row = WorldInteractionListEntries(rawArea);
        if (row.Count == 0 && rawArea is List<object> directRow) row = directRow;
        if (row.Count < 1) continue;
        ulong id = WonkUInt64(row[0]);
        if (id == 0) continue;
        string name = id.ToString();
        if (row.Count > 1 && row[1] != null && !String.IsNullOrWhiteSpace(row[1].ToString())) {
          long nameId = WonkInt64(row[1]);
          try {
            string localized = strTable?.GetText(nameId, String.Empty);
            if (!String.IsNullOrWhiteSpace(localized)) name = localized;
          } catch { }
        }
        mapAreas[id] = name;
      }

      Dictionary<string, NodeAsset> newAssetDict = new Dictionary<string, NodeAsset>();
      worldAreaOverrides = WorldAreaNameOverrides.LoadEntries();
      worldAreaInternalNames.Clear();
      worldSystemGeneratedAreaIds.Clear();
      var detectedAreaNames = new List<(ulong Id, string InternalName, string Category, string Group)>();

      // mapareasdata is not a complete world list: class phases, old/development maps and some newer areas are
      // absent even though area.dat is present in the client. Probe the union of SWTOR's table, Jedipedia's
      // current catalog snapshot and every ID the user has put in WorldAreaNames.xml. A candidate only reaches
      // the tree when its area.dat really exists in the installed client.
      var candidateIds = new HashSet<ulong>(mapAreas.Keys);
      foreach (WorldAreaCatalogEntry entry in WorldAreaCatalog.Entries) candidateIds.Add(entry.Id);
      foreach (ulong id in worldAreaOverrides.Keys) candidateIds.Add(id);

      // Live clients can contain generated worlds that never appear in mapareasdata or the bundled Jedipedia
      // catalog. Recover every numeric ID referenced by a KNOWN filename below
      // /resources/world/livecontent/systemgenerated/ in the filename dictionary, then probe the installed client
      // for the deterministic <id>/area.dat path. This also discovers an area when only one of its room DAT filenames is
      // known. The compact PFD1 pool is prefix-searched directly, so this does not walk/materialise millions
      // of unrelated filenames.
      foreach (ulong installedAreaId in DiscoverInstalledSystemGeneratedWorldAreaIds()) {
        candidateIds.Add(installedAreaId);
        worldSystemGeneratedAreaIds.Add(installedAreaId);
      }

      // RED/HE32/assets_* releases contain development and removed worlds that are not in any current map-area
      // table/catalog. Recover those IDs only for a detected pre-64-bit client so the established Retail/64-bit
      // normal-world list remains unchanged. System-generated worlds above are intentionally discovered on all
      // client generations.
      if (WorldUsesLegacyContent)
        foreach (ulong installedAreaId in DiscoverInstalledWorldAreaIds()) candidateIds.Add(installedAreaId);

      foreach (ulong id in candidateIds.OrderBy(x => x)) {
        WorldAreaCatalogEntry catalogEntry = WorldAreaCatalog.Entries.FirstOrDefault(x => x.Id == id);
        worldAreaOverrides.TryGetValue(id, out WorldAreaOverride userEntry);

        string[] candidatePaths = {
          "/resources/world/areas/" + id + "/area.dat",
          "/resources/world/livecontent/systemgenerated/" + id + "/area.dat"
        };

        File areaFile = null;
        string areaPath = null;
        foreach (string candidate in candidatePaths) {
          areaFile = currentAssets.FindFile(candidate);
          if (areaFile == null) continue;
          areaPath = candidate;
          break;
        }
        if (areaFile == null) continue;

        bool systemGenerated = IsSystemGeneratedWorldPath(areaPath);
        if (systemGenerated) worldSystemGeneratedAreaIds.Add(id);

        string internalName = catalogEntry?.InternalName;
        if (String.IsNullOrWhiteSpace(internalName)) internalName = userEntry?.InternalName;
        if (String.IsNullOrWhiteSpace(internalName)) internalName = ReadAreaInternalName(areaFile);
        if (!String.IsNullOrWhiteSpace(internalName)) worldAreaInternalNames[id] = internalName.Trim();

        string defaultCategory = systemGenerated
          ? WorldLiveContentCategory
          : catalogEntry?.Category ?? WorldAreaNameOverrides.UnassignedCategory;
        string defaultGroup = systemGenerated
          ? WorldSystemGeneratedGroup
          : catalogEntry?.Group ?? String.Empty;
        detectedAreaNames.Add((id, internalName, defaultCategory, defaultGroup));

        string name;
        if (userEntry != null && !String.IsNullOrWhiteSpace(userEntry.Name)) name = userEntry.Name.Trim();
        else if (mapAreas.TryGetValue(id, out string mapName) && !String.IsNullOrWhiteSpace(mapName) && mapName != id.ToString()) name = mapName;
        else if (catalogEntry != null && !String.IsNullOrWhiteSpace(catalogEntry.Comment)) name = StripJedipediaMarkup(catalogEntry.Comment).Replace("\r", " ").Replace("\n", " / ");
        else name = id.ToString();

        string displayName = String.IsNullOrWhiteSpace(internalName) || String.Equals(name, internalName, StringComparison.OrdinalIgnoreCase)
          ? name
          : name + " - " + internalName;

        HashFileInfo hashInfo = new HashFileInfo(
          areaFile.FileInfo.PrimaryHash, areaFile.FileInfo.SecondaryHash, areaFile, true, false
        );
        newAssetDict.Add("/" + id, new NodeAsset("/" + id, "/", displayName, hashInfo));
      }

      // Append newly discovered IDs after the scan. Existing user name/category/group values are preserved. Unknown
      // ordinary areas default to Unassigned; generated live-content areas default to Live Content / System Generated.
      WorldAreaNameOverrides.Synchronize(detectedAreaNames);

      newAssetDict.Add("/", new NodeAsset("/", "", "Worlds", null));
      loadedAssetDict = newAssetDict;
    }

    private IEnumerable<ulong> DiscoverInstalledSystemGeneratedWorldAreaIds() {
      var result = new HashSet<ulong>();
      const string prefix = "/resources/world/livecontent/systemgenerated/";
      try {
        IReadOnlyList<string> knownPaths = HashDictionaryInstance.Instance.Dictionary.FindKnownFileNamesByPathPrefix(prefix);
        foreach (string path in knownPaths) TryAddSystemGeneratedWorldAreaId(path, result);
      } catch { }
      return result;
    }

    private static void TryAddSystemGeneratedWorldAreaId(string rawPath, HashSet<ulong> result) {
      if (result == null || String.IsNullOrWhiteSpace(rawPath)) return;
      string path = rawPath.Replace('\\', '/').Trim();
      const string prefix = "/resources/world/livecontent/systemgenerated/";
      int start = path.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
      if (start < 0) return;
      start += prefix.Length;
      int slash = path.IndexOf('/', start);
      string idText = slash < 0 ? path.Substring(start) : path.Substring(start, slash - start);
      if (UInt64.TryParse(idText, out ulong id) && id != 0) result.Add(id);
    }

    private static bool IsSystemGeneratedWorldPath(string rawPath) {
      if (String.IsNullOrWhiteSpace(rawPath)) return false;
      string path = rawPath.Replace('\\', '/');
      return path.IndexOf("/resources/world/livecontent/systemgenerated/", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private IEnumerable<ulong> DiscoverInstalledWorldAreaIds() {
      var result = new HashSet<ulong>();
      if (currentAssets?.Libraries == null) return result;
      foreach (TorArchive.Library library in currentAssets.Libraries) {
        try { if (!library.Loaded) library.Load(); } catch { continue; }
        foreach (TorArchive.Archive archive in library.Archives.Values) {
          if (archive == null) continue;
          bool sawAreaNames = false;
          IEnumerable<HashData> namedFiles = null;
          try { namedFiles = HashDictionaryInstance.Instance.Dictionary.EnumerateArchiveFiles(archive.StrippedFileName); }
          catch { }
          if (namedFiles != null) foreach (HashData named in namedFiles) {
            int before = result.Count;
            TryAddInstalledWorldAreaId(named?.FileName, result);
            if (result.Count != before) sawAreaNames = true;
          }

          // Some RED/HE32 filename packs were catalogued under a different archive family.  Only for an explicitly
          // legacy DOM, probe the global hash dictionary as a read-only name oracle.  Do this here instead of changing
          // HashFileInfo globally: Retail/64-bit file naming and CRC semantics therefore remain completely untouched.
          if (!sawAreaNames && WorldUsesLegacyContent) {
            foreach (TorArchive.File file in archive.EnumerateFiles()) {
              try {
                HashData global = HashDictionaryInstance.Instance.Dictionary.SearchHashList(
                  file.FileInfo.PrimaryHash, file.FileInfo.SecondaryHash);
                if (global == null || String.IsNullOrWhiteSpace(global.FileName)) continue;
                TryAddInstalledWorldAreaId(global.FileName, result);
              } catch { }
            }
          }
        }
      }
      return result;
    }

    private static void TryAddInstalledWorldAreaId(string rawPath, HashSet<ulong> result) {
      if (result == null || String.IsNullOrWhiteSpace(rawPath)) return;
      string path = rawPath.Replace('\\', '/').Trim().ToLowerInvariant();
      if (!path.EndsWith("/area.dat", StringComparison.OrdinalIgnoreCase)) return;
      string[] parts = path.Trim('/').Split('/');
      for (int i = 0; i + 2 < parts.Length; i++) {
        if (!String.Equals(parts[i], "world", StringComparison.OrdinalIgnoreCase)) continue;
        int idIndex = -1;
        if (i + 2 < parts.Length && String.Equals(parts[i + 1], "areas", StringComparison.OrdinalIgnoreCase)) idIndex = i + 2;
        else if (i + 3 < parts.Length && String.Equals(parts[i + 1], "livecontent", StringComparison.OrdinalIgnoreCase) &&
                 String.Equals(parts[i + 2], "systemgenerated", StringComparison.OrdinalIgnoreCase)) idIndex = i + 3;
        if (idIndex >= 0 && idIndex < parts.Length && UInt64.TryParse(parts[idIndex], out ulong id) && id != 0) result.Add(id);
        return;
      }
    }


    private void BackgroundWorker1_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e) {
      if (e.Error != null) {
        toolStripStatusLabel1.Text = "World Browser could not load the shared SWTOR data.";
        toolStripProgressBar1.Visible = false;
        pictureBox1.Visible = false;
        MessageBox.Show(
          e.Error.GetBaseException().Message,
          "World Browser",
          MessageBoxButtons.OK,
          MessageBoxIcon.Error
        );
        return;
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
      if (m.Msg == WM_KEYDOWN || m.Msg == WM_SYSKEYDOWN) {
        bool repeated = (m.LParam.ToInt64() & (1L << 30)) != 0;
        if (HandleWorldViewerShortcut((Keys)(int)m.WParam, repeated)) return true;
      }
      if (m.Msg != WM_MOUSEWHEEL) return false;
      int delta = unchecked((short)((m.WParam.ToInt64() >> 16) & 0xffff));
      // Let the vertical Jedipedia-style cutaway control own its wheel; otherwise the camera also zooms/speeds up.
      if (CursorIsOverWorldSliceControl()) { HandleWorldSliceMouseWheel(delta); return true; }
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

    private bool HandleWorldViewerShortcut(Keys key, bool repeated) {
      if (!WorldWindowInputEnabled || panelRender == null) return false;
      Keys modifiers = Control.ModifierKeys;
      bool ctrl = (modifiers & Keys.Control) != 0;
      bool shift = (modifiers & Keys.Shift) != 0;
      bool alt = (modifiers & Keys.Alt) != 0;

      // Mirror the interface shortcuts exposed by Jedipedia's key-binding reference. Ignore repeat messages
      // for toggles/dialogs so holding a chord cannot rapidly reopen or flip the same command.
      if (ctrl && shift && key == Keys.F) {
        if (repeated) return true;
        if (btnWorldStats != null) btnWorldStats.Checked = !btnWorldStats.Checked;
        SetStatusLabel(btnWorldStats?.Checked == true ? "Live FPS / occlusion statistics enabled." : "Live FPS / occlusion statistics disabled.");
        return true;
      }
      if (ctrl && !shift && key == Keys.F) {
        if (repeated) return true;
        ShowMapnoteNavigatorDialog();
        return true;
      }
      if (ctrl && !shift && key == Keys.E) {
        if (repeated) return true;
        ShowCoordinateTeleportDialog();
        return true;
      }
      if (alt && !ctrl && (key == Keys.Z || key == Keys.Y)) {
        if (repeated) return true;
        ToggleWorldInterface();
        return true;
      }
      if (!ctrl && !alt && key == Keys.Escape && !panelRender.IsFullMapOpen && !String.IsNullOrWhiteSpace(panelRender.SelectedWorldModelSummary)) {
        panelRender.ClearWorldModelSelection();
        HideWorldSelectionInfo();
        UpdateWorldSelectedObjectMenu();
        SetStatusLabel("World selection cleared.");
        return true;
      }
      return false;
    }


    private static string StripJedipediaMarkup(string textValue) {
      return (textValue ?? String.Empty).Replace("*", String.Empty).Trim();
    }

    private sealed class WorldTreeEntry {
      public ulong Id;
      public NodeAsset Asset;
      public WorldAreaCatalogEntry Catalog;
      public WorldAreaOverride Override;
      public string InternalName;
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
      return Has(item.Id.ToString()) || Has(item.Asset?.displayName) || Has(item.InternalName) || Has(item.Override?.Name) || Has(item.Override?.InternalName)
        || Has(item.Catalog?.InternalName) || Has(item.Catalog?.Comment) || Has(item.Category) || Has(item.Group);
    }

    private TreeNode CreateWorldTreeLeaf(WorldTreeEntry item) {
      string internalName = item.InternalName;
      string technicalLabel = item.Id.ToString();
      if (!String.IsNullOrWhiteSpace(internalName)) technicalLabel += "  " + internalName;

      // Match Jedipedia's file tree: numeric area id first, authored internal name directly beside it.
      // Keep PugTools' friendly/localized label as a suffix where it adds information.
      string friendly = item.Asset?.displayName ?? String.Empty;
      if (!String.IsNullOrWhiteSpace(internalName)) {
        string suffix = " - " + internalName;
        if (friendly.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
          friendly = friendly.Substring(0, friendly.Length - suffix.Length).Trim();
      }
      if (!String.IsNullOrWhiteSpace(friendly)
          && !String.Equals(friendly, item.Id.ToString(), StringComparison.OrdinalIgnoreCase)
          && !String.Equals(friendly, internalName, StringComparison.OrdinalIgnoreCase))
        technicalLabel += " — " + friendly;

      var node = new TreeNode {
        Name = "/" + item.Id,
        Text = technicalLabel,
        Tag = item.Asset,
        ImageIndex = 2,
        SelectedImageIndex = 2
      };
      string comment = StripJedipediaMarkup(item.Catalog?.Comment);
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
        bool systemGenerated = worldSystemGeneratedAreaIds.Contains(id);
        bool userHasCustomFolder = userEntry != null
          && !String.IsNullOrWhiteSpace(userEntry.Category)
          && (catalog == null
              || !String.Equals(userEntry.Category.Trim(), catalog.Category ?? String.Empty, StringComparison.OrdinalIgnoreCase)
              || !String.Equals((userEntry.Group ?? String.Empty).Trim(), catalog.Group ?? String.Empty, StringComparison.OrdinalIgnoreCase));

        string category;
        string group;
        if (systemGenerated && !userHasCustomFolder) {
          // v3 wrote the old Jedipedia category/group into WorldAreaNames.xml. Treat an unchanged copy of that
          // stock metadata as migratable so existing users immediately get the new Live Content folder, while a
          // genuinely custom folder assignment remains respected.
          category = WorldLiveContentCategory;
          group = WorldSystemGeneratedGroup;
        } else {
          category = !String.IsNullOrWhiteSpace(userEntry?.Category)
            ? userEntry.Category.Trim()
            : catalog?.Category ?? WorldAreaNameOverrides.UnassignedCategory;
          group = userEntry != null && userEntry.Group != null
            ? userEntry.Group.Trim()
            : catalog?.Group ?? String.Empty;
        }
        int entryOrder = catalog != null
          && String.Equals(catalog.Category, category, StringComparison.OrdinalIgnoreCase)
          && String.Equals(catalog.Group ?? String.Empty, group ?? String.Empty, StringComparison.OrdinalIgnoreCase)
            ? catalog.EntryOrder : Int32.MaxValue;
        worldAreaInternalNames.TryGetValue(id, out string internalName);
        if (String.IsNullOrWhiteSpace(internalName)) internalName = !String.IsNullOrWhiteSpace(userEntry?.InternalName) ? userEntry.InternalName : catalog?.InternalName;
        items.Add(new WorldTreeEntry {
          Id = id, Asset = kvp.Value, Catalog = catalog, Override = userEntry, InternalName = internalName,
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

      // The world viewer accumulated enough Jedipedia-style switches that a flat toolbar no longer fits on ordinary
      // displays. Keep the common actions one click away, but group the controls by intent so resizing the left pane
      // never pushes important buttons off the right edge.
      btnWorldRenderMenu = new ToolStripDropDownButton("Render") { DisplayStyle = ToolStripItemDisplayStyle.Text, ToolTipText = "Render mode, device, quality and visual layers" };
      btnWorldLayersMenu = new ToolStripDropDownButton("World") { DisplayStyle = ToolStripItemDisplayStyle.Text, ToolTipText = "World layers, population, authoring helpers and culling" };
      btnWorldNavigationMenu = new ToolStripDropDownButton("Navigation") { DisplayStyle = ToolStripItemDisplayStyle.Text, ToolTipText = "Walking, map navigation and saved views" };
      btnWorldToolsMenu = new ToolStripDropDownButton("Tools") { DisplayStyle = ToolStripItemDisplayStyle.Text, ToolTipText = "Inspection, diagnostics and screenshots" };
      KeepCheckMenuOpen(btnWorldRenderMenu);
      KeepCheckMenuOpen(btnWorldLayersMenu);
      worldToolbar.Items.Add(btnWorldRenderMenu);
      worldToolbar.Items.Add(btnWorldLayersMenu);
      worldToolbar.Items.Add(btnWorldNavigationMenu);
      worldToolbar.Items.Add(btnWorldToolsMenu);

      btnWorldRenderMenu.DropDownItems.Add(new ToolStripLabel("Mode:"));
      cmbWorldMode = new ToolStripComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 };
      cmbWorldMode.Items.AddRange(new object[] { "In-game", "Lit", "Unlit", "Heightmap", "Wireframe", "Map" });
      cmbWorldMode.SelectedIndex = 0;
      cmbWorldMode.SelectedIndexChanged += (_, __) => {
        if (updatingWorldToolbar) return;
        worldSettings.Mode = (WorldRenderMode)Math.Max(0, cmbWorldMode.SelectedIndex);
        if (btnWorldRenderMenu != null && cmbWorldMode.SelectedItem != null) btnWorldRenderMenu.Text = "Render: " + cmbWorldMode.SelectedItem;
        ApplyWorldSettings();
        if (worldSettings.Mode == WorldRenderMode.Map) panelRender?.FitArea();
      };
      btnWorldRenderMenu.DropDownItems.Add(cmbWorldMode);
      btnWorldRenderMenu.Text = "Render: " + (cmbWorldMode.SelectedItem ?? "In-game");

      btnWorldRenderMenu.DropDownItems.Add(new ToolStripSeparator());
      btnWorldRenderMenu.DropDownItems.Add(new ToolStripLabel("Device:"));
      cmbWorldBackend = new ToolStripComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 };
      cmbWorldBackend.Items.AddRange(new object[] { "GPU (D3D11)", "WARP (CPU)" });
      cmbWorldBackend.SelectedIndex = 0;
      cmbWorldBackend.SelectedIndexChanged += async (_, __) => {
        if (updatingWorldToolbar) return;
        WorldRenderBackend backend = cmbWorldBackend.SelectedIndex == 1 ? WorldRenderBackend.Warp : WorldRenderBackend.Hardware;
        await SwitchWorldBackend(backend);
      };
      btnWorldRenderMenu.DropDownItems.Add(cmbWorldBackend);

      btnWorldRenderMenu.DropDownItems.Add(new ToolStripSeparator());
      btnWorldRenderMenu.DropDownItems.Add(new ToolStripLabel("Anti-aliasing:"));
      cmbWorldAntiAliasing = new ToolStripComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 };
      cmbWorldAntiAliasing.Items.AddRange(new object[] { "Off", "FXAA", "TAA", "TAA + FXAA" });
      cmbWorldAntiAliasing.SelectedIndex = (int)worldSettings.AntiAliasing;
      cmbWorldAntiAliasing.SelectedIndexChanged += (_, __) => {
        if (updatingWorldToolbar) return;
        worldSettings.AntiAliasing = (WorldAntiAliasing)Math.Max(0, cmbWorldAntiAliasing.SelectedIndex);
        ApplyWorldSettings();
      };
      btnWorldRenderMenu.DropDownItems.Add(cmbWorldAntiAliasing);

      btnWorldRenderMenu.DropDownItems.Add(new ToolStripLabel("Texture quality:"));
      cmbWorldTextureQuality = new ToolStripComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 };
      cmbWorldTextureQuality.Items.AddRange(new object[] { "Low", "Medium", "High" });
      cmbWorldTextureQuality.SelectedIndex = (int)worldSettings.TextureQuality;
      cmbWorldTextureQuality.ToolTipText = "SWTOR DDS mip limit: Low skips two top mip levels, Medium one, High loads the authored full resolution";
      cmbWorldTextureQuality.SelectedIndexChanged += (_, __) => {
        if (updatingWorldToolbar) return;
        worldSettings.TextureQuality = (WorldTextureQuality)Math.Max(0, cmbWorldTextureQuality.SelectedIndex);
        ApplyWorldSettings();
        SetStatusLabel("Texture quality: " + worldSettings.TextureQuality + " — world textures will be reloaded at the selected mip level.");
      };
      btnWorldRenderMenu.DropDownItems.Add(cmbWorldTextureQuality);

      btnWorldRenderMenu.DropDownItems.Add(new ToolStripSeparator());
      btnWorldRenderMenu.DropDownItems.Add(new ToolStripLabel("View distance:"));
      cmbWorldViewDistance = new ToolStripComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 };
      cmbWorldViewDistance.Items.AddRange(new object[] { "1x", "2x", "3x", "5x", "10x" });
      cmbWorldViewDistance.SelectedIndex = 2;
      cmbWorldViewDistance.SelectedIndexChanged += (_, __) => {
        if (updatingWorldToolbar) return;
        float[] scales = { 1f, 2f, 3f, 5f, 10f };
        int index = Math.Max(0, Math.Min(scales.Length - 1, cmbWorldViewDistance.SelectedIndex));
        worldSettings.ViewDistanceScale = scales[index];
        ApplyWorldSettings();
      };
      btnWorldRenderMenu.DropDownItems.Add(cmbWorldViewDistance);

      btnWorldRenderMenu.DropDownItems.Add(new ToolStripSeparator());
      btnWorldRenderMenu.DropDownItems.Add(new ToolStripLabel("Field of view:"));
      cmbWorldFov = new ToolStripComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 };
      cmbWorldFov.Items.AddRange(new object[] { "50°", "55°", "60°", "65°", "70°", "75°" });
      cmbWorldFov.SelectedIndex = 0;
      cmbWorldFov.SelectedIndexChanged += (_, __) => {
        if (updatingWorldToolbar) return;
        float[] values = { 50f, 55f, 60f, 65f, 70f, 75f };
        int index = Math.Max(0, Math.Min(values.Length - 1, cmbWorldFov.SelectedIndex));
        worldSettings.FieldOfViewDegrees = values[index];
        ApplyWorldSettings();
      };
      btnWorldRenderMenu.DropDownItems.Add(cmbWorldFov);

      btnWorldRenderMenu.DropDownItems.Add(new ToolStripLabel("Camera speed:"));
      cmbWorldCameraSpeed = new ToolStripComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 };
      cmbWorldCameraSpeed.Items.AddRange(new object[] { "1", "2", "5", "10", "25", "50", "100" });
      cmbWorldCameraSpeed.SelectedIndex = 0;
      cmbWorldCameraSpeed.SelectedIndexChanged += (_, __) => {
        if (updatingWorldToolbar) return;
        float[] values = { 1f, 2f, 5f, 10f, 25f, 50f, 100f };
        int index = Math.Max(0, Math.Min(values.Length - 1, cmbWorldCameraSpeed.SelectedIndex));
        panelRender?.SetCameraSpeed(values[index]);
      };
      btnWorldRenderMenu.DropDownOpening += (_, __) => {
        if (cmbWorldCameraSpeed == null || panelRender == null) return;
        float[] values = { 1f, 2f, 5f, 10f, 25f, 50f, 100f };
        float speed = panelRender.CurrentCameraSpeed; int best = 0; float bestDelta = Math.Abs(values[0] - speed);
        for (int i = 1; i < values.Length; i++) { float delta = Math.Abs(values[i] - speed); if (delta < bestDelta) { bestDelta = delta; best = i; } }
        updatingWorldToolbar = true;
        try { cmbWorldCameraSpeed.SelectedIndex = best; } finally { updatingWorldToolbar = false; }
      };

      btnWorldOrthographic = AddWorldDropDownToggle(btnWorldRenderMenu, "Orthographic projection", worldSettings.OrthographicProjection, v => {
        worldSettings.OrthographicProjection = v;
        SetStatusLabel(v
          ? "Orthographic: wheel = zoom/cut height, right drag = pan, A/D/J/L + I/K = look, Q/E = strafe"
          : "Perspective projection enabled");
      }, "Parallel plan/cutaway view; zoom also follows the floor height");

      btnWorldRenderMenu.DropDownItems.Add(new ToolStripSeparator());
      var geometryMenu = new ToolStripMenuItem("Geometry");
      KeepCheckMenuOpen(geometryMenu);
      AddWorldDropDownToggle(geometryMenu, "Terrain", worldSettings.ShowTerrain, v => worldSettings.ShowTerrain = v);
      AddWorldDropDownToggle(geometryMenu, "Models", worldSettings.ShowModels, v => worldSettings.ShowModels = v);
      AddWorldDropDownToggle(geometryMenu, "SpeedTrees", worldSettings.ShowSpeedTrees, v => worldSettings.ShowSpeedTrees = v);
      AddWorldDropDownToggle(geometryMenu, "Grass / DYD", worldSettings.ShowDynamicDetails, v => worldSettings.ShowDynamicDetails = v);
      AddWorldDropDownToggle(geometryMenu, "Roads", worldSettings.ShowRoads, v => worldSettings.ShowRoads = v);
      btnWorldRenderMenu.DropDownItems.Add(geometryMenu);

      var lightingMenu = new ToolStripMenuItem("Lighting & atmosphere");
      KeepCheckMenuOpen(lightingMenu);
      AddWorldDropDownToggle(lightingMenu, "Lighting", worldSettings.EnableLighting, v => worldSettings.EnableLighting = v);
      AddWorldDropDownToggle(lightingMenu, "Local lights", worldSettings.EnableLocalLights, v => worldSettings.EnableLocalLights = v);
      lightingMenu.DropDownItems.Add(new ToolStripLabel("Sun shadows:"));
      cmbWorldShadowQuality = new ToolStripComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 170 };
      cmbWorldShadowQuality.Items.AddRange(new object[] { "Off", "On", "High (longer range)" });
      cmbWorldShadowQuality.SelectedIndex = (int)worldSettings.ShadowQuality;
      cmbWorldShadowQuality.ToolTipText = "On: four 2048 cascades to 25 world units; High: four 4096 cascades with an extended 50-unit range";
      cmbWorldShadowQuality.SelectedIndexChanged += (_, __) => {
        if (updatingWorldToolbar) return;
        worldSettings.ShadowQuality = (WorldShadowQuality)Math.Max(0, cmbWorldShadowQuality.SelectedIndex);
        ApplyWorldSettings();
      };
      lightingMenu.DropDownItems.Add(cmbWorldShadowQuality);
      AddWorldDropDownToggle(lightingMenu, "Fog", worldSettings.EnableFog, v => worldSettings.EnableFog = v);
      AddWorldDropDownToggle(lightingMenu, "Sky", worldSettings.ShowSky, v => worldSettings.ShowSky = v);
      AddWorldDropDownToggle(lightingMenu, "Water", worldSettings.ShowWater, v => worldSettings.ShowWater = v);
      AddWorldDropDownToggle(lightingMenu, "Color grading", worldSettings.EnablePostProcessing, v => worldSettings.EnablePostProcessing = v, "Apply the area's authored color lookup table; anti-aliasing remains independent");
      btnWorldRenderMenu.DropDownItems.Add(lightingMenu);

      var mapLayersMenu = new ToolStripMenuItem("Map layers");
      KeepCheckMenuOpen(mapLayersMenu);
      AddWorldDropDownToggle(mapLayersMenu, "Map art", worldSettings.ShowMapArt, v => worldSettings.ShowMapArt = v);
      var mapIconsMenu = new ToolStripMenuItem("Map icons") {
        ToolTipText = "Original SWTOR map-note icons. All icon classes are disabled by default."
      };
      KeepCheckMenuOpen(mapIconsMenu);
      AddWorldDropDownToggle(mapIconsMenu, "Exploration missions", worldSettings.ShowMapIconExplorationQuests, v => {
        worldSettings.ShowMapIconExplorationQuests = v; worldSettings.ShowMapNotes = worldSettings.AnyMapIconEnabled;
      });
      AddWorldDropDownToggle(mapIconsMenu, "Quests / mission boards", worldSettings.ShowMapIconQuests, v => {
        worldSettings.ShowMapIconQuests = v; worldSettings.ShowMapNotes = worldSettings.AnyMapIconEnabled;
      });
      AddWorldDropDownToggle(mapIconsMenu, "Vendors", worldSettings.ShowMapIconVendors, v => {
        worldSettings.ShowMapIconVendors = v; worldSettings.ShowMapNotes = worldSettings.AnyMapIconEnabled;
      });
      AddWorldDropDownToggle(mapIconsMenu, "Taxi terminals", worldSettings.ShowMapIconTaxi, v => {
        worldSettings.ShowMapIconTaxi = v; worldSettings.ShowMapNotes = worldSettings.AnyMapIconEnabled;
      });
      AddWorldDropDownToggle(mapIconsMenu, "Class trainers", worldSettings.ShowMapIconClassTrainers, v => {
        worldSettings.ShowMapIconClassTrainers = v; worldSettings.ShowMapNotes = worldSettings.AnyMapIconEnabled;
      });
      AddWorldDropDownToggle(mapIconsMenu, "Crew-skill trainers", worldSettings.ShowMapIconCrewTrainers, v => {
        worldSettings.ShowMapIconCrewTrainers = v; worldSettings.ShowMapNotes = worldSettings.AnyMapIconEnabled;
      });
      AddWorldDropDownToggle(mapIconsMenu, "Quick travel / bind points", worldSettings.ShowMapIconBindpoints, v => {
        worldSettings.ShowMapIconBindpoints = v; worldSettings.ShowMapNotes = worldSettings.AnyMapIconEnabled;
      });
      AddWorldDropDownToggle(mapIconsMenu, "Resource nodes", worldSettings.ShowMapIconResources, v => {
        worldSettings.ShowMapIconResources = v; worldSettings.ShowMapNotes = worldSettings.AnyMapIconEnabled;
      });
      AddWorldDropDownToggle(mapIconsMenu, "Mailboxes", worldSettings.ShowMapIconMailboxes, v => {
        worldSettings.ShowMapIconMailboxes = v; worldSettings.ShowMapNotes = worldSettings.AnyMapIconEnabled;
      });
      AddWorldDropDownToggle(mapIconsMenu, "Modification stations", worldSettings.ShowMapIconEnhancementStations, v => {
        worldSettings.ShowMapIconEnhancementStations = v; worldSettings.ShowMapNotes = worldSettings.AnyMapIconEnabled;
      });
      AddWorldDropDownToggle(mapIconsMenu, "Cargo hold / banks", worldSettings.ShowMapIconCargoHold, v => {
        worldSettings.ShowMapIconCargoHold = v; worldSettings.ShowMapNotes = worldSettings.AnyMapIconEnabled;
      });
      AddWorldDropDownToggle(mapIconsMenu, "Galactic Trade Network", worldSettings.ShowMapIconGalacticMarket, v => {
        worldSettings.ShowMapIconGalacticMarket = v; worldSettings.ShowMapNotes = worldSettings.AnyMapIconEnabled;
      });
      AddWorldDropDownToggle(mapIconsMenu, "Map exits / links", worldSettings.ShowMapIconMapLinks, v => {
        worldSettings.ShowMapIconMapLinks = v; worldSettings.ShowMapNotes = worldSettings.AnyMapIconEnabled;
      });
      AddWorldDropDownToggle(mapIconsMenu, "Elevators / Wonkavators", worldSettings.ShowMapIconWonkavator, v => {
        worldSettings.ShowMapIconWonkavator = v; worldSettings.ShowMapNotes = worldSettings.AnyMapIconEnabled;
      });
      AddWorldDropDownToggle(mapIconsMenu, "Other / unknown notes", worldSettings.ShowMapIconOther, v => {
        worldSettings.ShowMapIconOther = v; worldSettings.ShowMapNotes = worldSettings.AnyMapIconEnabled;
      });
      mapLayersMenu.DropDownItems.Add(mapIconsMenu);
      btnWorldRenderMenu.DropDownItems.Add(mapLayersMenu);

      InitializeUtilitiesMenu();
      InitializeNpcMenu();
      btnWorldLayersMenu.DropDownItems.Add(new ToolStripSeparator());
      var visibilityMenu = new ToolStripMenuItem("Visibility / culling");
      KeepCheckMenuOpen(visibilityMenu);
      AddWorldDropDownToggle(visibilityMenu, "Local room streaming (current + adjacent)", worldSettings.EnableLocalRoomStreaming, v => worldSettings.EnableLocalRoomStreaming = v,
        "Keep the normal 3D renderer/material working set to the current SWTOR room plus directly adjacent rooms. Enabled by default; full map/minimap still render the whole area.");
      AddWorldDropDownToggle(visibilityMenu, "Room culling", worldSettings.EnableRoomVisibility, v => worldSettings.EnableRoomVisibility = v,
        "Authored portal/VisibleRooms culling. This is a render visibility layer, not whole-world loading; outdoor rooms intentionally fail open unless local room streaming is enabled.");
      AddWorldDropDownToggle(visibilityMenu, "Occluder depth prepass (experimental)", worldSettings.EnableOccluderPrepass, v => worldSettings.EnableOccluderPrepass = v,
        "Depth-only dPVS approximation. Disabled by default because authored doorway/portal occluders can appear as black masks in the offline renderer.");
      AddWorldDropDownToggle(visibilityMenu, "Object occlusion culling", worldSettings.EnableObjectOcclusionCulling, v => worldSettings.EnableObjectOcclusionCulling = v);
      btnWorldLayersMenu.DropDownItems.Add(visibilityMenu);

      btnWorldRooms = new ToolStripMenuItem("Rooms") {
        ToolTipText = "Show or hide individual SWTOR AREA rooms. This manual layer is independent of portal/dPVS culling."
      };
      KeepCheckMenuOpen(btnWorldRooms);
      btnWorldRooms.DropDownOpening += (_, __) => RebuildWorldRoomsMenu();
      btnWorldLayersMenu.DropDownItems.Add(btnWorldRooms);

      btnWorldDecorationHooks = new ToolStripMenuItem("Decoration hooks") {
        CheckOnClick = true,
        Checked = worldSettings.ShowDecorationHooks,
        Visible = false,
        ToolTipText = "Show/hide Stronghold decoration placement fields"
      };
      btnWorldDecorationHooks.CheckedChanged += (_, __) => {
        if (updatingWorldToolbar) return;
        worldSettings.ShowDecorationHooks = btnWorldDecorationHooks.Checked;
        ApplyWorldSettings();
      };
      btnWorldLayersMenu.DropDownItems.Add(btnWorldDecorationHooks);

      // Navigation. Search/bookmark items build their own nested menus.
      btnWorldWalkingMode = new ToolStripMenuItem("Walking mode") {
        CheckOnClick = true, Checked = worldSettings.WalkingMode,
        ToolTipText = "Ground-following first-person movement (WASD, Shift run, Space jump, mouse wheel speed)"
      };
      btnWorldWalkingMode.CheckedChanged += (_, __) => {
        if (updatingWorldToolbar) return;
        worldSettings.WalkingMode = btnWorldWalkingMode.Checked;
        ApplyWorldSettings();
        SetStatusLabel(btnWorldWalkingMode.Checked ? "Walking mode: WASD, Shift = run, Space = jump, wheel = speed" : "Free-fly camera enabled");
      };
      btnWorldNavigationMenu.DropDownItems.Add(btnWorldWalkingMode);
      AddWorldDropDownToggle(btnWorldNavigationMenu, "Area title on room change", worldSettings.ShowRoomLocationBanner,
        v => worldSettings.ShowRoomLocationBanner = v,
        "Show the SWTOR-style localized area/map-page title when entering a different room. Disabled by default; if no localized area title exists, the current room name is shown.");
      AddWorldDropDownToggle(btnWorldNavigationMenu, "Use room names instead of map names", worldSettings.UseRoomNamesForLocationBanner,
        v => worldSettings.UseRoomNamesForLocationBanner = v,
        "Use the technical name from the current .room file for the location banner. In this mode every room-name change is announced, even when several rooms belong to the same SWTOR map area.");
      InitializeTaxiRoutesMenu();
      InitializeSpaceFlypathMenu();
      var fit = new ToolStripMenuItem("Fit area");
      fit.Click += (_, __) => panelRender?.FitArea();
      btnWorldNavigationMenu.DropDownItems.Add(fit);
      btnWorldMapEntireArea = new ToolStripMenuItem("Show entire area on map") {
        CheckOnClick = true,
        Checked = false,
        ToolTipText = "Smart map framing: off crops isolated outliers/giant scenery when a useful main-area crop exists"
      };
      btnWorldMapEntireArea.CheckedChanged += (_, __) => {
        if (updatingWorldToolbar) return;
        panelRender?.SetMapShowEntireArea(btnWorldMapEntireArea.Checked);
      };
      btnWorldNavigationMenu.DropDownItems.Add(btnWorldMapEntireArea);
      btnWorldNavigationMenu.DropDownOpening += (_, __) => {
        if (btnWorldMapEntireArea == null || panelRender == null) return;
        btnWorldMapEntireArea.Enabled = panelRender.MapHasSmartCrop;
        updatingWorldToolbar = true;
        try { btnWorldMapEntireArea.Checked = panelRender.MapShowEntireArea; }
        finally { updatingWorldToolbar = false; }
        btnWorldMapEntireArea.Text = panelRender.MapHasSmartCrop ? "Show entire area on map" : "Show entire area on map (no outliers)";
      };
      btnWorldMiniMap = new ToolStripMenuItem("Minimap") { CheckOnClick = true };
      btnWorldMiniMap.CheckedChanged += (_, __) => { if (!updatingWorldToolbar) SetMiniMapVisible(btnWorldMiniMap.Checked); };
      btnWorldNavigationMenu.DropDownItems.Add(btnWorldMiniMap);
      btnWorldNavigationMenu.DropDownItems.Add(new ToolStripSeparator());
      InitializeMapnoteNavigator();
      InitializeWorldBookmarks();

      // Tools / diagnostics.
      btnWorldInspectModel = new ToolStripMenuItem("Inspect model") { CheckOnClick = true, ToolTipText = "Click, then click visible GR2 geometry to show its room/asset/instance/mesh/material diagnostics" };
      btnWorldInspectModel.CheckedChanged += (_, __) => {
        worldModelInspectPending = btnWorldInspectModel.Checked;
        if (worldModelInspectPending) SetStatusLabel("Model inspector armed — click the stray object in the 3D view.");
      };
      btnWorldToolsMenu.DropDownItems.Add(btnWorldInspectModel);

      btnWorldSelectedObject = new ToolStripMenuItem("Selected object") { Enabled = false };
      var showSelectionDetails = new ToolStripMenuItem("Show details…");
      showSelectionDetails.Click += (_, __) => {
        string details = panelRender?.SelectedWorldModelDetails ?? String.Empty;
        if (String.IsNullOrWhiteSpace(details)) return;
        MessageBox.Show(this, details + "\r\n\r\nTip: Press Ctrl+C while this dialog is focused to copy the complete diagnostic text.", "Selected world object", MessageBoxButtons.OK, MessageBoxIcon.Information);
      };
      var copySelectionDetails = new ToolStripMenuItem("Copy details");
      copySelectionDetails.Click += (_, __) => {
        string details = panelRender?.SelectedWorldModelDetails ?? String.Empty;
        if (String.IsNullOrWhiteSpace(details)) return;
        try { Clipboard.SetText(details); SetStatusLabel("Selected object details copied to clipboard."); } catch (Exception ex) { SetStatusLabel("Could not copy selection: " + ex.Message); }
      };
      btnWorldSelectedConversation = new ToolStripMenuItem("Open conversation…") { Visible = false };
      btnWorldSelectedConversation.Click += (_, __) => OpenSelectedWorldConversationPreview();
      var clearSelection = new ToolStripMenuItem("Clear selection");
      clearSelection.Click += (_, __) => { panelRender?.ClearWorldModelSelection(); HideWorldSelectionInfo(); UpdateWorldSelectedObjectMenu(); SetStatusLabel("World selection cleared."); };
      var showSelectionBounds = new ToolStripMenuItem("Show selection bounds (yellow)") { CheckOnClick = true, Checked = worldSettings.ShowSelectionBounds,
        ToolTipText = "Show the yellow wireframe bounds box around the selected world object." };
      showSelectionBounds.CheckedChanged += (_, __) => { if (updatingWorldToolbar) return; worldSettings.ShowSelectionBounds = showSelectionBounds.Checked; ApplyWorldSettings(); };
      btnWorldSelectedObject.DropDownItems.Add(showSelectionDetails);
      btnWorldSelectedObject.DropDownItems.Add(copySelectionDetails);
      btnWorldSelectedObject.DropDownItems.Add(btnWorldSelectedConversation);
      btnWorldSelectedObject.DropDownItems.Add(showSelectionBounds);
      btnWorldSelectedObject.DropDownItems.Add(new ToolStripSeparator());
      btnWorldSelectedObject.DropDownItems.Add(clearSelection);
      btnWorldToolsMenu.DropDownItems.Add(btnWorldSelectedObject);

      btnWorldCurrentRegions = new ToolStripMenuItem("Current volumes") { ToolTipText = "Show SWTOR region/trigger volumes that currently contain the camera" };
      btnWorldCurrentRegions.DropDownOpening += (_, __) => RebuildCurrentRegionsMenu();
      btnWorldToolsMenu.DropDownItems.Add(btnWorldCurrentRegions);
      btnWorldVolumeList = new ToolStripMenuItem("Current volume list") {
        CheckOnClick = true, Checked = worldSettings.ShowVolumeList,
        ToolTipText = "Live region/trigger panel; click an entry to pin it"
      };
      btnWorldVolumeList.CheckedChanged += (_, __) => {
        if (updatingWorldToolbar) return;
        worldSettings.ShowVolumeList = btnWorldVolumeList.Checked;
        ApplyWorldSettings();
        UpdateWorldVolumePanel(true);
      };
      btnWorldToolsMenu.DropDownItems.Add(btnWorldVolumeList);

      btnWorldStats = new ToolStripMenuItem("Live stats") {
        CheckOnClick = true, Checked = false,
        ToolTipText = "Show lightweight FPS and occlusion statistics in the status bar"
      };
      btnWorldStats.CheckedChanged += (_, __) => { if (toolStripPerfStatus != null) toolStripPerfStatus.Visible = btnWorldStats.Checked; };
      btnWorldToolsMenu.DropDownItems.Add(btnWorldStats);
      btnWorldToolsMenu.DropDownItems.Add(new ToolStripSeparator());
      var shot = new ToolStripMenuItem("Screenshot");
      shot.Click += (_, __) => panelRender?.RequestScreenshot();
      btnWorldToolsMenu.DropDownItems.Add(shot);

      splitContainer3.Panel1.Controls.Add(worldToolbar);
      worldToolbar.BringToFront();
      LayoutWorldRenderPanel();
      splitContainer3.Panel1.Resize += (_, __) => LayoutWorldRenderPanel();
    }

    private void RebuildWorldRoomsMenu() {
      if (btnWorldRooms == null) return;
      foreach (ToolStripItem old in btnWorldRooms.DropDownItems.Cast<ToolStripItem>().ToArray()) old.Dispose();
      btnWorldRooms.DropDownItems.Clear();

      List<string> names = area?.RoomList == null
        ? new List<string>()
        : area.RoomList.Where(r => r != null && !String.IsNullOrWhiteSpace(r.RoomName))
            .Select(r => r.RoomName.Trim()).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
      if (names.Count == 0 || panelRender == null) {
        btnWorldRooms.Text = "Rooms";
        btnWorldRooms.DropDownItems.Add(new ToolStripMenuItem("(load an area first)") { Enabled = false });
        return;
      }

      int visibleCount = names.Count(x => panelRender.IsRoomManuallyVisible(x));
      btnWorldRooms.Text = visibleCount == names.Count ? "Rooms" : "Rooms (" + visibleCount + "/" + names.Count + ")";

      var filterLabel = new ToolStripLabel("Filter rooms:");
      var filterBox = new ToolStripTextBox {
        AutoSize = false,
        Width = 300,
        Text = worldRoomFilterText ?? String.Empty,
        ToolTipText = "Live substring filter. Matches anywhere inside the room name."
      };
      filterBox.TextBox.Enter += (_, __) => { worldTextInputActive = true; worldRenderInputActive = false; };
      filterBox.TextBox.Leave += (_, __) => worldTextInputActive = false;
      btnWorldRooms.DropDownItems.Add(filterLabel);
      btnWorldRooms.DropDownItems.Add(filterBox);
      btnWorldRooms.DropDownItems.Add(new ToolStripSeparator());

      var showAll = new ToolStripMenuItem("Show all rooms");
      showAll.Click += (_, __) => {
        panelRender?.SetAllRoomsManuallyVisible(true);
        updatingWorldToolbar = true;
        try { foreach (ToolStripMenuItem roomItem in btnWorldRooms.DropDownItems.OfType<ToolStripMenuItem>().Where(x => x.Tag is string)) roomItem.Checked = true; }
        finally { updatingWorldToolbar = false; }
        btnWorldRooms.Text = "Rooms";
        InvalidateMiniMapSnapshot();
      };
      var hideAll = new ToolStripMenuItem("Hide all rooms");
      hideAll.Click += (_, __) => {
        panelRender?.SetAllRoomsManuallyVisible(false);
        updatingWorldToolbar = true;
        try { foreach (ToolStripMenuItem roomItem in btnWorldRooms.DropDownItems.OfType<ToolStripMenuItem>().Where(x => x.Tag is string)) roomItem.Checked = false; }
        finally { updatingWorldToolbar = false; }
        btnWorldRooms.Text = "Rooms (0/" + names.Count + ")";
        InvalidateMiniMapSnapshot();
      };
      btnWorldRooms.DropDownItems.Add(showAll);
      btnWorldRooms.DropDownItems.Add(hideAll);
      btnWorldRooms.DropDownItems.Add(new ToolStripSeparator());

      var roomItems = new List<ToolStripMenuItem>(names.Count);
      foreach (string roomName in names) {
        string captured = roomName;
        var item = new ToolStripMenuItem(captured) {
          CheckOnClick = true,
          Checked = panelRender.IsRoomManuallyVisible(captured),
          Tag = captured,
          ToolTipText = "Toggle every renderable object owned by this AREA room."
        };
        item.CheckedChanged += (_, __) => {
          if (updatingWorldToolbar || panelRender == null) return;
          panelRender.SetRoomManuallyVisible(captured, item.Checked);
          int nowVisible = names.Count(x => panelRender.IsRoomManuallyVisible(x));
          btnWorldRooms.Text = nowVisible == names.Count ? "Rooms" : "Rooms (" + nowVisible + "/" + names.Count + ")";
          InvalidateMiniMapSnapshot();
        };
        roomItems.Add(item);
        btnWorldRooms.DropDownItems.Add(item);
      }

      void ApplyRoomFilter() {
        string query = (filterBox.Text ?? String.Empty).Trim();
        int matches = 0;
        foreach (ToolStripMenuItem item in roomItems) {
          string roomName = item.Tag as string ?? item.Text ?? String.Empty;
          bool match = query.Length == 0 || roomName.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
          item.Visible = match;
          if (match) matches++;
        }
        filterLabel.Text = query.Length == 0 ? "Filter rooms:" : "Filter rooms (" + matches + "/" + names.Count + "):";
      }
      filterBox.TextChanged += (_, __) => { worldRoomFilterText = filterBox.Text ?? String.Empty; ApplyRoomFilter(); };
      ApplyRoomFilter();
      filterBox.KeyDown += (_, e) => {
        if (e.KeyCode == Keys.Escape && !String.IsNullOrEmpty(filterBox.Text)) {
          filterBox.Text = String.Empty;
          e.Handled = true;
          e.SuppressKeyPress = true;
        }
      };
    }

    private ToolStripMenuItem AddWorldDropDownToggle(ToolStripDropDownItem menu, string text, bool initialValue, Action<bool> setter, string tooltip = null) {
      var item = new ToolStripMenuItem(text) { CheckOnClick = true, Checked = initialValue, ToolTipText = tooltip ?? String.Empty };
      item.CheckedChanged += (_, __) => {
        if (updatingWorldToolbar) return;
        setter(item.Checked);
        ApplyWorldSettings();
      };
      menu.DropDownItems.Add(item);
      return item;
    }

    private static void KeepCheckMenuOpen(ToolStripDropDownItem menu) {
      if (menu == null) return;
      menu.DropDown.Closing += (_, e) => {
        // A checkbox menu is meant for changing several layers in one pass. Keep it open on item clicks,
        // but still close normally via Escape, clicking outside, or clicking the toolbar button again.
        if (e.CloseReason == ToolStripDropDownCloseReason.ItemClicked) e.Cancel = true;
      };
    }

    private void InitializeMapnoteNavigator() {
      // Do not host a ToolStripTextBox inside a nested ToolStripDropDown. On .NET 6 WinForms this can
      // intermittently fail while the parent Navigation dropdown is creating its native window handle
      // (Win32 ERROR_INVALID_WINDOW_HANDLE / 1400). Keep Navigation made of plain menu items and open
      // the searchable navigator as an ordinary owned dialog instead.
      btnWorldMapnoteNavigator = new ToolStripMenuItem("Find note…") {
        DisplayStyle = ToolStripItemDisplayStyle.Text,
        ToolTipText = "Search mapnotes/bookmarks and teleport to a result"
      };
      btnWorldMapnoteNavigator.Click += (_, __) => ShowMapnoteNavigatorDialog();
      btnWorldNavigationMenu?.DropDownItems.Add(btnWorldMapnoteNavigator);
    }

    private sealed class WorldMapnoteSearchResult {
      public AreaMapNote Note { get; set; }
      public string Display { get; set; }
      public override string ToString() => Display ?? String.Empty;
    }

    private void ShowMapnoteNavigatorDialog() {
      if (area == null || panelRender == null) {
        MessageBox.Show(this, "Load an area first.", "Find note", MessageBoxButtons.OK, MessageBoxIcon.Information);
        return;
      }

      using var dialog = new Form {
        Text = "Find note",
        StartPosition = FormStartPosition.CenterParent,
        FormBorderStyle = FormBorderStyle.Sizable,
        MinimizeBox = false,
        MaximizeBox = false,
        ShowInTaskbar = false,
        Width = 720,
        Height = 520,
        MinimumSize = new Size(520, 360)
      };

      var searchLabel = new Label { Text = "Search:", AutoSize = true, Left = 12, Top = 16 };
      var searchBox = new TextBox { Left = 72, Top = 12, Width = 620, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
      var hint = new Label {
        Text = "Label, FQN, tags or coordinates; prefix a term with - to exclude it.",
        AutoSize = true, Left = 72, Top = 39, ForeColor = SystemColors.GrayText
      };
      var results = new ListBox {
        Left = 12, Top = 64, Width = 680, Height = 370,
        Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
        HorizontalScrollbar = true, IntegralHeight = false
      };
      var go = new Button { Text = "Go", Left = 536, Top = 444, Width = 75, Anchor = AnchorStyles.Bottom | AnchorStyles.Right };
      var cancel = new Button { Text = "Close", Left = 617, Top = 444, Width = 75, Anchor = AnchorStyles.Bottom | AnchorStyles.Right, DialogResult = DialogResult.Cancel };
      var countLabel = new Label { Text = "", AutoSize = true, Left = 12, Top = 450, Anchor = AnchorStyles.Bottom | AnchorStyles.Left };

      void RebuildResults() {
        string search = searchBox.Text ?? String.Empty;
        Vector3 cameraPosition = panelRender.CurrentCameraPosition;
        List<AreaMapNote> notes = CurrentMapnoteNavigationEntries()
          .Where(n => MapnoteMatchesSearch(n, search))
          .OrderBy(n => String.Equals(n.Id, "arrival-path", StringComparison.OrdinalIgnoreCase) ? 0 : (IsBookmarkMapnote(n) ? 1 : (IsHearthstoneMapnote(n) ? 2 : 3)))
          .ThenBy(n => n.Label ?? n.Fqn ?? String.Empty, StringComparer.OrdinalIgnoreCase)
          .ThenBy(n => n.Fqn ?? String.Empty, StringComparer.OrdinalIgnoreCase)
          .ToList();

        results.BeginUpdate();
        try {
          results.Items.Clear();
          const int maxResults = 500;
          foreach (AreaMapNote note in notes.Take(maxResults)) {
            float distanceMeters = (cameraPosition - (note.Position + new Vector3(0, .18f, 0))).Length() * 10f;
            string label = String.IsNullOrWhiteSpace(note.Label) ? (note.Fqn ?? "Mapnote") : note.Label;
            string display = String.Format(System.Globalization.CultureInfo.InvariantCulture,
              "{0}   ({1:0} m)   [{2:0}, {3:0}, {4:0}]",
              label, distanceMeters, note.Position.X * 10f, note.Position.Z * 10f, note.Position.Y * 10f);
            results.Items.Add(new WorldMapnoteSearchResult { Note = note, Display = display });
          }
          countLabel.Text = notes.Count > maxResults
            ? String.Format(System.Globalization.CultureInfo.InvariantCulture, "{0} matches; showing first {1}", notes.Count, maxResults)
            : String.Format(System.Globalization.CultureInfo.InvariantCulture, "{0} match{1}", notes.Count, notes.Count == 1 ? "" : "es");
          if (results.Items.Count > 0) results.SelectedIndex = 0;
        } finally {
          results.EndUpdate();
        }
        go.Enabled = results.SelectedItem is WorldMapnoteSearchResult;
      }

      void TeleportSelected(bool closeDialog = true) {
        if (!(results.SelectedItem is WorldMapnoteSearchResult selected) || selected.Note == null) return;
        panelRender.TeleportToMapNote(selected.Note);
        SetStatusLabel("Teleported to mapnote: " + (selected.Note.Label ?? selected.Note.Fqn ?? selected.Note.Id ?? "Mapnote"));
        if (!closeDialog) return;
        dialog.DialogResult = DialogResult.OK;
        dialog.Close();
      }

      void StepResult(int delta) {
        if (results.Items.Count == 0) return;
        int index = results.SelectedIndex;
        if (index < 0) index = 0;
        else index = Math.Max(0, Math.Min(results.Items.Count - 1, index + delta));
        results.SelectedIndex = index;
        TeleportSelected(false);
      }

      searchBox.TextChanged += (_, __) => RebuildResults();
      searchBox.Enter += (_, __) => { worldTextInputActive = true; worldRenderInputActive = false; };
      searchBox.Leave += (_, __) => worldTextInputActive = false;
      searchBox.KeyDown += (_, e) => {
        if (e.KeyCode == Keys.Down || e.KeyCode == Keys.Up) {
          e.Handled = true; e.SuppressKeyPress = true; StepResult(e.KeyCode == Keys.Down ? 1 : -1);
        } else if (e.KeyCode == Keys.Enter) {
          e.Handled = true; e.SuppressKeyPress = true; TeleportSelected();
        }
      };
      results.SelectedIndexChanged += (_, __) => go.Enabled = results.SelectedItem is WorldMapnoteSearchResult;
      results.DoubleClick += (_, __) => TeleportSelected();
      results.KeyDown += (_, e) => {
        if (e.KeyCode == Keys.Enter) {
          e.Handled = true; e.SuppressKeyPress = true; TeleportSelected();
        } else if (e.KeyCode == Keys.Down || e.KeyCode == Keys.Up) {
          e.Handled = true; e.SuppressKeyPress = true; StepResult(e.KeyCode == Keys.Down ? 1 : -1);
        }
      };
      go.Click += (_, __) => TeleportSelected();
      dialog.FormClosed += (_, __) => { worldTextInputActive = false; ActivateWorldRenderInput(); };

      dialog.Controls.Add(searchLabel);
      dialog.Controls.Add(searchBox);
      dialog.Controls.Add(hint);
      dialog.Controls.Add(results);
      dialog.Controls.Add(countLabel);
      dialog.Controls.Add(go);
      dialog.Controls.Add(cancel);
      dialog.AcceptButton = go;
      dialog.CancelButton = cancel;
      dialog.Shown += (_, __) => { RebuildResults(); searchBox.Focus(); };
      dialog.ShowDialog(this);
    }

    private void ShowCoordinateTeleportDialog() {
      if (area == null || panelRender == null) {
        MessageBox.Show(this, "Load an area first.", "Teleport", MessageBoxButtons.OK, MessageBoxIcon.Information);
        return;
      }

      Vector3 display = panelRender.CurrentDisplayPosition;
      using var dialog = new Form {
        Text = "Teleport to coordinates",
        StartPosition = FormStartPosition.CenterParent,
        FormBorderStyle = FormBorderStyle.FixedDialog,
        MinimizeBox = false,
        MaximizeBox = false,
        ShowInTaskbar = false,
        ClientSize = new Size(470, 156)
      };
      var label = new Label {
        AutoSize = true, Left = 14, Top = 18,
        Text = "SWTOR display coordinates (X, Y, Z):"
      };
      var input = new TextBox {
        Left = 14, Top = 43, Width = 442,
        Text = String.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:0}, {1:0}, {2:0}", display.X, display.Y, display.Z)
      };
      var hint = new Label {
        AutoSize = false, Left = 14, Top = 72, Width = 442, Height = 34,
        ForeColor = SystemColors.GrayText,
        Text = "Two values are also accepted as X, Y and keep the current height. Use semicolons when entering decimal commas."
      };
      var go = new Button { Text = "Teleport", Left = 300, Top = 116, Width = 75 };
      var cancel = new Button { Text = "Cancel", Left = 381, Top = 116, Width = 75, DialogResult = DialogResult.Cancel };

      bool TryTeleport() {
        string raw = (input.Text ?? String.Empty).Trim();
        // Jedipedia copies comma-separated values. Also accept semicolon-separated input so decimal commas are
        // convenient on German/European systems (for example: 123,4; 567,8; 9,1).
        string[] parts = raw.IndexOf(';') >= 0
          ? raw.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
          // Current Jedipedia accepts commas, whitespace, or any mixture. Preserve semicolon handling above for
          // decimal-comma locales, then make pasted coordinate readouts equally forgiving here.
          : raw.Split(new[] { ',', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 && parts.Length != 3) {
          MessageBox.Show(dialog, "Enter X, Y or X, Y, Z. Semicolons may be used with decimal commas.", "Teleport", MessageBoxButtons.OK, MessageBoxIcon.Warning);
          input.Focus(); input.SelectAll(); return false;
        }
        var values = new List<float>();
        foreach (string part in parts) {
          string token = part.Trim();
          bool parsed = float.TryParse(token, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float value);
          if (!parsed) parsed = float.TryParse(token, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture, out value);
          // The UI can run under an invariant process culture even on a German desktop. Semicolon-separated decimal
          // commas remain unambiguous, so accept them explicitly as a final fallback.
          if (!parsed && raw.IndexOf(';') >= 0 && token.IndexOf(',') >= 0 && token.IndexOf('.') < 0)
            parsed = float.TryParse(token.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value);
          if (!parsed || float.IsNaN(value) || float.IsInfinity(value)) {
            MessageBox.Show(dialog, "Coordinates must be finite numbers.", "Teleport", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            input.Focus(); input.SelectAll(); return false;
          }
          values.Add(value);
        }
        panelRender.TeleportToDisplayCoordinates(values[0], values[1], values.Count == 3 ? values[2] : (float?)null);
        Vector3 now = panelRender.CurrentDisplayPosition;
        SetStatusLabel(String.Format(System.Globalization.CultureInfo.InvariantCulture,
          "Teleported to {0:0}, {1:0}, {2:0}", now.X, now.Y, now.Z));
        return true;
      }

      go.Click += (_, __) => { if (TryTeleport()) { dialog.DialogResult = DialogResult.OK; dialog.Close(); } };
      input.KeyDown += (_, e) => {
        if (e.KeyCode != Keys.Enter) return;
        e.Handled = true; e.SuppressKeyPress = true;
        if (TryTeleport()) { dialog.DialogResult = DialogResult.OK; dialog.Close(); }
      };
      input.Enter += (_, __) => { worldTextInputActive = true; worldRenderInputActive = false; };
      dialog.FormClosed += (_, __) => { worldTextInputActive = false; ActivateWorldRenderInput(); };
      dialog.Controls.Add(label); dialog.Controls.Add(input); dialog.Controls.Add(hint); dialog.Controls.Add(go); dialog.Controls.Add(cancel);
      dialog.AcceptButton = go; dialog.CancelButton = cancel;
      dialog.Shown += (_, __) => { input.Focus(); input.SelectAll(); };
      dialog.ShowDialog(this);
    }

    private void ToggleWorldInterface() {
      if (splitContainer1 == null || splitContainer3 == null) return;
      worldInterfaceHidden = !worldInterfaceHidden;
      if (worldInterfaceHidden) {
        worldInterfaceLeftPanelWasCollapsed = splitContainer1.Panel1Collapsed;
        worldInterfaceMiniMapWasVisible = miniMapPanel?.Visible == true;
        splitContainer1.Panel1Collapsed = true;
        if (worldToolbar != null) worldToolbar.Visible = false;
        if (statusStrip1 != null) statusStrip1.Visible = false;
        if (miniMapPanel != null) miniMapPanel.Visible = false;
        if (worldVolumePanel != null) worldVolumePanel.Visible = false;
        if (phaseBannerLabel != null) phaseBannerLabel.Visible = false;
        LayoutWorldViewerHost();
        LayoutWorldRenderPanel();
        ActivateWorldRenderInput();
      } else {
        splitContainer1.Panel1Collapsed = worldInterfaceLeftPanelWasCollapsed;
        if (worldToolbar != null) worldToolbar.Visible = true;
        if (statusStrip1 != null) statusStrip1.Visible = true;
        LayoutWorldViewerHost();
        LayoutWorldRenderPanel();
        if (miniMapPanel != null) miniMapPanel.Visible = worldInterfaceMiniMapWasVisible && !worldFullMapActive;
        UpdateWorldVolumePanel(true);
        if (phaseBannerLabel != null) {
          phaseBannerLabel.Visible = !String.IsNullOrWhiteSpace(lastPhaseBannerText);
          if (phaseBannerLabel.Visible) phaseBannerLabel.BringToFront();
        }
        ActivateWorldRenderInput();
        SetStatusLabel("Viewer interface restored. Alt+Z / Alt+Y hides it again.");
      }
    }

    private void LayoutWorldViewerHost() {
      if (splitContainer1?.Panel2 == null || splitContainer3 == null) return;
      Control host = splitContainer1.Panel2;
      const int left = 4, top = 3, right = 1;
      int bottom = statusStrip1?.Visible == true ? statusStrip1.Height + 5 : 3;
      splitContainer3.Bounds = new Rectangle(
        left, top,
        Math.Max(1, host.ClientSize.Width - left - right),
        Math.Max(1, host.ClientSize.Height - top - bottom));
    }

    private static string WorldBookmarkFilePath() {
      string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
      return Path.Combine(root, "PugTools", "world-bookmarks.json");
    }

    private void LoadWorldBookmarks() {
      if (worldBookmarksLoaded) return;
      worldBookmarksLoaded = true;
      try {
        string path = WorldBookmarkFilePath();
        if (!System.IO.File.Exists(path)) return;
        string json = System.IO.File.ReadAllText(path);
        Dictionary<string, List<WorldBookmarkRecord>> loaded = JsonConvert.DeserializeObject<Dictionary<string, List<WorldBookmarkRecord>>>(json);
        if (loaded != null) worldBookmarkStore = new Dictionary<string, List<WorldBookmarkRecord>>(loaded, StringComparer.Ordinal);
      } catch (Exception ex) {
        System.Diagnostics.Debug.WriteLine("Could not read world bookmarks: " + ex.Message);
        worldBookmarkStore = new Dictionary<string, List<WorldBookmarkRecord>>(StringComparer.Ordinal);
      }
    }

    private void SaveWorldBookmarks() {
      try {
        string path = WorldBookmarkFilePath();
        string directory = Path.GetDirectoryName(path);
        if (!String.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        string json = JsonConvert.SerializeObject(worldBookmarkStore, Formatting.Indented);
        System.IO.File.WriteAllText(path, json);
      } catch (Exception ex) {
        MessageBox.Show(this, "Could not save world bookmarks:\n" + ex.Message, "PugTools", MessageBoxButtons.OK, MessageBoxIcon.Warning);
      }
    }

    private List<WorldBookmarkRecord> CurrentWorldBookmarks(bool create = false) {
      LoadWorldBookmarks();
      string key = currentAreaId.ToString(System.Globalization.CultureInfo.InvariantCulture);
      if (!worldBookmarkStore.TryGetValue(key, out List<WorldBookmarkRecord> list) && create) {
        list = new List<WorldBookmarkRecord>();
        worldBookmarkStore[key] = list;
      }
      return list ?? new List<WorldBookmarkRecord>();
    }

    private static AreaMapNote BookmarkToMapNote(WorldBookmarkRecord bookmark) {
      if (bookmark == null) return null;
      var note = new AreaMapNote {
        Id = "bookmark-" + bookmark.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
        Fqn = "Bookmark",
        Label = String.IsNullOrWhiteSpace(bookmark.Name) ? "Bookmark" : bookmark.Name,
        Position = new Vector3(bookmark.X, bookmark.Y, bookmark.Z),
        Rotation = new Vector3(bookmark.RotX, bookmark.RotY, bookmark.RotZ)
      };
      note.Tags.Add("bookmark");
      return note;
    }

    private static WorldBookmarkRecord MapNoteToBookmark(AreaMapNote note, int id, string name) {
      if (note == null) return null;
      return new WorldBookmarkRecord {
        Id = id,
        Name = String.IsNullOrWhiteSpace(name) ? "Bookmark" : name.Trim(),
        X = note.Position.X, Y = note.Position.Y, Z = note.Position.Z,
        RotX = note.Rotation.X, RotY = note.Rotation.Y, RotZ = note.Rotation.Z
      };
    }

    private string PromptWorldBookmarkName(string title, string initial) {
      using var dialog = new Form {
        Text = title,
        FormBorderStyle = FormBorderStyle.FixedDialog,
        StartPosition = FormStartPosition.CenterParent,
        MinimizeBox = false,
        MaximizeBox = false,
        ShowInTaskbar = false,
        ClientSize = new Size(360, 96)
      };
      var label = new Label { Left = 12, Top = 12, Width = 336, Text = "Name:" };
      var input = new TextBox { Left = 12, Top = 31, Width = 336, Text = initial ?? String.Empty };
      var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Left = 192, Width = 75, Top = 62 };
      var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 273, Width = 75, Top = 62 };
      dialog.Controls.Add(label); dialog.Controls.Add(input); dialog.Controls.Add(ok); dialog.Controls.Add(cancel);
      dialog.AcceptButton = ok; dialog.CancelButton = cancel;
      dialog.Shown += (_, __) => { input.SelectAll(); input.Focus(); };
      if (dialog.ShowDialog(this) != DialogResult.OK) return null;
      string result = input.Text?.Trim();
      return String.IsNullOrWhiteSpace(result) ? null : result;
    }

    private void InitializeWorldBookmarks() {
      LoadWorldBookmarks();
      btnWorldBookmarks = new ToolStripMenuItem("Bookmarks") {
        DisplayStyle = ToolStripItemDisplayStyle.Text,
        ToolTipText = "Local camera bookmarks; stores position and view direction per area"
      };
      btnWorldBookmarks.DropDownOpening += (_, __) => RebuildWorldBookmarksMenu();
      btnWorldNavigationMenu?.DropDownItems.Add(btnWorldBookmarks);
    }

    private void AddWorldBookmarkAtCamera() {
      if (panelRender == null || area == null || currentAreaId == 0) return;
      List<WorldBookmarkRecord> list = CurrentWorldBookmarks(true);
      string name = PromptWorldBookmarkName("Add world bookmark", "Bookmark " + (list.Count + 1));
      if (name == null) return;
      int id = list.Count == 0 ? 1 : list.Max(x => x?.Id ?? 0) + 1;
      AreaMapNote note = panelRender.CaptureCameraMapNote("bookmark-" + id, name);
      WorldBookmarkRecord record = MapNoteToBookmark(note, id, name);
      if (record == null) return;
      list.Add(record);
      SaveWorldBookmarks();
      RebuildMapnoteNavigator();
      SetStatusLabel("Saved bookmark: " + name);
    }

    private void MoveWorldBookmarkToCamera(WorldBookmarkRecord bookmark) {
      if (bookmark == null || panelRender == null) return;
      AreaMapNote note = panelRender.CaptureCameraMapNote("bookmark-" + bookmark.Id, bookmark.Name);
      if (note == null) return;
      bookmark.X = note.Position.X; bookmark.Y = note.Position.Y; bookmark.Z = note.Position.Z;
      bookmark.RotX = note.Rotation.X; bookmark.RotY = note.Rotation.Y; bookmark.RotZ = note.Rotation.Z;
      SaveWorldBookmarks();
      RebuildMapnoteNavigator();
      SetStatusLabel("Moved bookmark to current view: " + bookmark.Name);
    }

    private void RenameWorldBookmark(WorldBookmarkRecord bookmark) {
      if (bookmark == null) return;
      string name = PromptWorldBookmarkName("Rename world bookmark", bookmark.Name);
      if (name == null || String.Equals(name, bookmark.Name, StringComparison.Ordinal)) return;
      bookmark.Name = name;
      SaveWorldBookmarks();
      RebuildMapnoteNavigator();
    }

    private void DeleteWorldBookmark(WorldBookmarkRecord bookmark) {
      if (bookmark == null) return;
      if (MessageBox.Show(this, "Delete bookmark ‘" + bookmark.Name + "’?", "PugTools", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
      List<WorldBookmarkRecord> list = CurrentWorldBookmarks(false);
      list.RemoveAll(x => x != null && x.Id == bookmark.Id);
      if (list.Count == 0) worldBookmarkStore.Remove(currentAreaId.ToString(System.Globalization.CultureInfo.InvariantCulture));
      SaveWorldBookmarks();
      RebuildMapnoteNavigator();
    }

    private void RebuildWorldBookmarksMenu() {
      if (btnWorldBookmarks == null) return;
      foreach (ToolStripItem item in btnWorldBookmarks.DropDownItems.Cast<ToolStripItem>().ToArray()) item.Dispose();
      btnWorldBookmarks.DropDownItems.Clear();
      var add = new ToolStripMenuItem("Add current view…") { Enabled = panelRender != null && area != null && currentAreaId != 0 };
      add.Click += (_, __) => AddWorldBookmarkAtCamera();
      btnWorldBookmarks.DropDownItems.Add(add);
      btnWorldBookmarks.DropDownItems.Add(new ToolStripSeparator());
      if (area == null || panelRender == null || currentAreaId == 0) {
        btnWorldBookmarks.DropDownItems.Add(new ToolStripMenuItem("(load an area first)") { Enabled = false });
        return;
      }
      List<WorldBookmarkRecord> list = CurrentWorldBookmarks(false).Where(x => x != null).OrderBy(x => x.Id).ToList();
      if (list.Count == 0) {
        btnWorldBookmarks.DropDownItems.Add(new ToolStripMenuItem("(no bookmarks for this area)") { Enabled = false });
        return;
      }
      foreach (WorldBookmarkRecord bookmark in list) {
        WorldBookmarkRecord captured = bookmark;
        AreaMapNote note = BookmarkToMapNote(captured);
        float distanceMeters = (panelRender.CurrentCameraPosition - (note.Position + new Vector3(0, .18f, 0))).Length() * 10f;
        var go = new ToolStripMenuItem(String.Format(System.Globalization.CultureInfo.InvariantCulture, "{0}   ({1:0} m)", captured.Name, distanceMeters)) {
          ToolTipText = String.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:0.###}, {1:0.###}, {2:0.###}", captured.X, captured.Y, captured.Z)
        };
        go.Click += (_, __) => { panelRender?.TeleportToMapNote(BookmarkToMapNote(captured)); ActivateWorldRenderInput(); };
        btnWorldBookmarks.DropDownItems.Add(go);
      }
      btnWorldBookmarks.DropDownItems.Add(new ToolStripSeparator());
      var manage = new ToolStripMenuItem("Manage bookmarks");
      foreach (WorldBookmarkRecord bookmark in list) {
        WorldBookmarkRecord captured = bookmark;
        var item = new ToolStripMenuItem(captured.Name);
        var move = new ToolStripMenuItem("Move to current view"); move.Click += (_, __) => MoveWorldBookmarkToCamera(captured);
        var rename = new ToolStripMenuItem("Rename…"); rename.Click += (_, __) => RenameWorldBookmark(captured);
        var delete = new ToolStripMenuItem("Delete"); delete.Click += (_, __) => DeleteWorldBookmark(captured);
        item.DropDownItems.Add(move); item.DropDownItems.Add(rename); item.DropDownItems.Add(delete);
        manage.DropDownItems.Add(item);
      }
      btnWorldBookmarks.DropDownItems.Add(manage);
    }

    private static bool IsBookmarkMapnote(AreaMapNote note) {
      return note != null && ((note.Id ?? String.Empty).StartsWith("bookmark-", StringComparison.OrdinalIgnoreCase) ||
        String.Equals(note.Fqn, "Bookmark", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsHearthstoneMapnote(AreaMapNote note) {
      if (note == null) return false;
      string text = ((note.Fqn ?? String.Empty) + " " + (note.Label ?? String.Empty) + " " +
        String.Join(" ", note.Tags ?? new List<string>())).ToLowerInvariant();
      return text.Contains("hearthstone") || text.Contains("hearth_") || text.Contains("bind_to_mapnote");
    }

    private IEnumerable<AreaMapNote> CurrentMapnoteNavigationEntries() {
      if (area == null) yield break;
      if (area.ArrivalPoint != null) {
        const float toDeg = 180f / (float)Math.PI;
        yield return new AreaMapNote {
          Id = "arrival-path",
          Fqn = "Arrival path",
          Label = "Arrival path",
          Position = area.ArrivalPoint.Position,
          Rotation = new Vector3(area.ArrivalPoint.Rotation.X * toDeg, area.ArrivalPoint.Rotation.Y * toDeg, 0f)
        };
      }
      foreach (WorldBookmarkRecord bookmark in CurrentWorldBookmarks(false)) {
        AreaMapNote bookmarkNote = BookmarkToMapNote(bookmark);
        if (bookmarkNote != null) yield return bookmarkNote;
      }
      foreach (AreaMapNote note in area.MapNotes ?? new List<AreaMapNote>()) if (note != null) yield return note;
    }

    private static string MapnoteSearchText(AreaMapNote note) {
      if (note == null) return String.Empty;
      return String.Join(" ", new[] {
        note.Id ?? String.Empty, note.Fqn ?? String.Empty, note.Label ?? String.Empty, note.Icon ?? String.Empty, note.ServiceKind ?? String.Empty,
        String.Join(" ", note.QuestNames ?? new List<string>()), String.Join(" ", note.QuestObjectives ?? new List<string>()),
        String.Join(" ", note.Tags ?? new List<string>()), String.Join(" ", note.ParentTags ?? new List<string>()),
        note.Position.X.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
        note.Position.Y.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
        note.Position.Z.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
        // Jedipedia/SWTOR display coordinates: X = world X, Y = world Z, Z = world Y, all ×10.
        Math.Round(note.Position.X * 10f).ToString(System.Globalization.CultureInfo.InvariantCulture),
        Math.Round(note.Position.Z * 10f).ToString(System.Globalization.CultureInfo.InvariantCulture),
        Math.Round(note.Position.Y * 10f).ToString(System.Globalization.CultureInfo.InvariantCulture)
      }).ToLowerInvariant();
    }

    private static bool MapnoteMatchesSearch(AreaMapNote note, string search) {
      string[] terms = (search ?? String.Empty).Trim().ToLowerInvariant().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
      if (terms.Length == 0) return true;
      string haystack = MapnoteSearchText(note);
      foreach (string raw in terms) {
        bool exclude = raw.Length > 1 && raw[0] == '-';
        string term = exclude ? raw.Substring(1) : raw;
        if (term.Length == 0) continue;
        bool contains = haystack.Contains(term);
        if ((!exclude && !contains) || (exclude && contains)) return false;
      }
      return true;
    }

    private void RebuildMapnoteNavigator() {
      // The navigator is now an owned dialog, rebuilt when it is opened/typed in. Kept as a no-op so
      // bookmark mutation and area-loading call sites remain simple and backward compatible.
    }

    private void InitializeUtilitiesMenu() {
      btnWorldUtilities = new ToolStripMenuItem("Utilities") { ToolTipText = "Editor and authoring overlays" };
      KeepCheckMenuOpen(btnWorldUtilities);

      var enableAuthoring = new ToolStripMenuItem("Enable authoring helpers") {
        ToolTipText = "Enable the common editor markers, paths, utility boards and helper geometry without exposing collision/occluder hulls"
      };
      enableAuthoring.Click += (_, __) => SetWorldUtilityPreset(true);
      var disableAuthoring = new ToolStripMenuItem("Disable authoring helpers") {
        ToolTipText = "Disable editor markers, paths, helper boards and utility overlays"
      };
      disableAuthoring.Click += (_, __) => SetWorldUtilityPreset(false);
      btnWorldUtilities.DropDownItems.Add(enableAuthoring);
      btnWorldUtilities.DropDownItems.Add(disableAuthoring);
      btnWorldUtilities.DropDownItems.Add(new ToolStripSeparator());

      AddWorldDropDownToggle(btnWorldUtilities, "Spawner / encounter markers", worldSettings.ShowUtilitySpawners, v => worldSettings.ShowUtilitySpawners = v);
      AddWorldDropDownToggle(btnWorldUtilities, "Cover points", worldSettings.ShowUtilityCoverPoints, v => worldSettings.ShowUtilityCoverPoints = v);
      AddWorldDropDownToggle(btnWorldUtilities, "Lights", worldSettings.ShowUtilityLights, v => worldSettings.ShowUtilityLights = v);
      AddWorldDropDownToggle(btnWorldUtilities, "Kynapse seed points", worldSettings.ShowUtilitySeedPoints, v => worldSettings.ShowUtilitySeedPoints = v);
      btnWorldUtilities.DropDownItems.Add(new ToolStripSeparator());
      AddWorldDropDownToggle(btnWorldUtilities, "Paths", worldSettings.ShowUtilityPaths, v => worldSettings.ShowUtilityPaths = v);
      AddWorldDropDownToggle(btnWorldUtilities, "Map roads with paths", worldSettings.ShowUtilityMapRoadPaths, v => worldSettings.ShowUtilityMapRoadPaths = v);
      AddWorldDropDownToggle(btnWorldUtilities, "Parent / child connections", worldSettings.ShowUtilityConnections, v => worldSettings.ShowUtilityConnections = v);
      AddWorldDropDownToggle(btnWorldUtilities, "Region / trigger volumes", worldSettings.ShowUtilityVolumes, v => worldSettings.ShowUtilityVolumes = v);
      AddWorldDropDownToggle(btnWorldUtilities, "Phase gateways (green gate)", worldSettings.ShowPhaseGateways, v => worldSettings.ShowPhaseGateways = v, "Show INSTANCE_GATEWAY boundaries as translucent green phase gates; enabled by default");
      AddWorldDropDownToggle(btnWorldUtilities, "Other helpers / authoring boards", worldSettings.ShowUtilityOther, v => {
        worldSettings.ShowUtilityOther = v;
        if (v) QueueWorldAuthoringGeometryReload();
      }, "Show stage/map/effect/audio/camera helpers plus EditorOnly and Hidden+PolyType=Ignore authoring geometry");
      btnWorldUtilities.DropDownItems.Add(new ToolStripSeparator());
      AddWorldDropDownToggle(btnWorldUtilities, "Show hidden occluders && colliders", worldSettings.ShowHiddenGeometry, v => worldSettings.ShowHiddenGeometry = v, "Reveal hidden collision and occlusion geometry for diagnostics");
      btnWorldLayersMenu?.DropDownItems.Add(btnWorldUtilities);
    }

    private void SetWorldUtilityPreset(bool enabled) {
      worldSettings.ShowUtilitySpawners = enabled;
      worldSettings.ShowUtilityCoverPoints = enabled;
      worldSettings.ShowUtilityLights = enabled;
      worldSettings.ShowUtilitySeedPoints = enabled;
      worldSettings.ShowUtilityPaths = enabled;
      worldSettings.ShowUtilityMapRoadPaths = enabled;
      worldSettings.ShowUtilityConnections = enabled;
      worldSettings.ShowUtilityVolumes = enabled;
      worldSettings.ShowPhaseGateways = enabled;
      worldSettings.ShowUtilityOther = enabled;
      // Collision/occluder hulls are intentionally not part of the normal authoring preset; they can be very noisy.
      if (enabled) worldSettings.ShowHiddenGeometry = false;
      SyncWorldUtilityMenuChecks();
      ApplyWorldSettings();
      if (enabled) QueueWorldAuthoringGeometryReload();
    }

    private void QueueWorldAuthoringGeometryReload() {
      if (_closing || worldAuthoringGeometryLoaded || worldAuthoringGeometryReloading || !worldSettings.ShowUtilityOther) return;
      if (area == null || info == null || currentAreaId == 0 || panelRender == null) return;
      // Keep the checkbox interaction instantaneous.  The actual reload runs after the current menu event has
      // unwound, otherwise WinForms can be left inside ToolStrip layout while the render panel is torn down.
      try { BeginInvoke(new Action(async () => await ReloadWorldAuthoringGeometryAsync())); } catch { }
    }

    private async Task ReloadWorldAuthoringGeometryAsync() {
      if (_closing || worldAuthoringGeometryLoaded || worldAuthoringGeometryReloading || !worldSettings.ShowUtilityOther) return;
      if (area == null || info == null || currentAreaId == 0 || panelRender == null) return;
      worldAuthoringGeometryReloading = true;
      bool wasVisible = renderPanel?.Visible == true;
      try {
        SetStatusLabel("Loading authoring boards and helper geometry…");
        if (renderPanel != null) renderPanel.Visible = false;
        if (render != null && render.IsAlive) {
          panelRender.StopRender();
          // Do not re-introduce an unbounded UI-thread Join while adding a convenience reload.
          if (!render.Join(2000)) {
            SetStatusLabel("Authoring geometry reload postponed because the renderer is still stopping.");
            return;
          }
          panelRender.Clear();
        }
        await PreviewAREA(info, currentAreaId);
        worldAuthoringGeometryLoaded = true;
        SetStatusLabel("Authoring boards and helper geometry loaded.");
      } catch (Exception ex) {
        worldAuthoringGeometryLoaded = false;
        SetStatusLabel("Could not load authoring geometry: " + ex.Message);
      } finally {
        worldAuthoringGeometryReloading = false;
        if (!_closing && renderPanel != null) renderPanel.Visible = wasVisible || render != null;
      }
    }

    private void SyncWorldUtilityMenuChecks() {
      if (btnWorldUtilities == null) return;
      updatingWorldToolbar = true;
      try {
        foreach (ToolStripItem raw in btnWorldUtilities.DropDownItems) {
          if (!(raw is ToolStripMenuItem item) || !item.CheckOnClick) continue;
          switch (item.Text) {
            case "Spawner / encounter markers": item.Checked = worldSettings.ShowUtilitySpawners; break;
            case "Cover points": item.Checked = worldSettings.ShowUtilityCoverPoints; break;
            case "Lights": item.Checked = worldSettings.ShowUtilityLights; break;
            case "Kynapse seed points": item.Checked = worldSettings.ShowUtilitySeedPoints; break;
            case "Paths": item.Checked = worldSettings.ShowUtilityPaths; break;
            case "Map roads with paths": item.Checked = worldSettings.ShowUtilityMapRoadPaths; break;
            case "Parent / child connections": item.Checked = worldSettings.ShowUtilityConnections; break;
            case "Region / trigger volumes": item.Checked = worldSettings.ShowUtilityVolumes; break;
            case "Phase gateways (green gate)": item.Checked = worldSettings.ShowPhaseGateways; break;
            case "Other helpers / authoring boards": item.Checked = worldSettings.ShowUtilityOther; break;
            case "Show hidden occluders && colliders": item.Checked = worldSettings.ShowHiddenGeometry; break;
          }
        }
      } finally { updatingWorldToolbar = false; }
    }

    private void InitializeNpcMenu() {
      btnWorldNpcs = new ToolStripMenuItem("NPCs / spawned objects") { ToolTipText = "Spawned/client-only NPC and SPN preview" };
      KeepCheckMenuOpen(btnWorldNpcs);
      AddWorldDropDownToggle(btnWorldNpcs, "Show NPC models", worldSettings.ShowNpcs, v => worldSettings.ShowNpcs = v);
      AddWorldDropDownToggle(btnWorldNpcs, "Localized names", worldSettings.ShowNpcNames, v => worldSettings.ShowNpcNames = v);
      AddWorldDropDownToggle(btnWorldNpcs, "Equipped item names", worldSettings.ShowNpcItems, v => worldSettings.ShowNpcItems = v);
      AddWorldDropDownToggle(btnWorldNpcs, "Animations", worldSettings.AnimateNpcs, v => worldSettings.AnimateNpcs = v, "Play authored NPC animation clips; dispenser spawn points stay at a stable preview position");
      btnWorldNpcs.DropDownItems.Add(new ToolStripSeparator());
      AddWorldDropDownToggle(btnWorldNpcs, "SPN placeable objects", worldSettings.ShowSpnObjects, v => worldSettings.ShowSpnObjects = v, "Render plc.* objects referenced by SPN spawners");
      AddWorldDropDownToggle(btnWorldNpcs, "SPN animations", worldSettings.AnimateSpnObjects, v => worldSettings.AnimateSpnObjects = v, "Animate MAG/Morpheme-driven SPN placeables when an idle clip can be resolved");
      AddWorldDropDownToggle(btnWorldNpcs, "Interactable blue glow", worldSettings.ShowPlaceableGlow, v => worldSettings.ShowPlaceableGlow = v, "SWTOR blue interaction tint for usable SPN placeables and usable DYN-state parts");
      AddWorldDropDownToggle(btnWorldNpcs, "Overhead service / quest symbols", worldSettings.ShowInteractionIcons, v => worldSettings.ShowInteractionIcons = v, "Quest/conversation, mail, vendor, taxi, quick-travel, bank, codex and other resolved interaction markers over NPC/SPN objects");
      btnWorldLayersMenu?.DropDownItems.Add(btnWorldNpcs);
    }

    private void RenderPanel_MouseDown(object sender, MouseEventArgs e) {
      ActivateWorldRenderInput();

      if (e.Button == MouseButtons.Right && (Control.ModifierKeys & Keys.Control) != 0) {
        worldSelectionClickPending = false;
        worldTaxiClickPending = false;
        panelRender?.ClearWorldModelSelection();
        HideWorldSelectionInfo();
        UpdateWorldSelectedObjectMenu();
        SetStatusLabel("World selection cleared.");
        return;
      }
      if (e.Button != MouseButtons.Left && e.Button != MouseButtons.Right) return;

      // The one-shot model inspector stays a left-click tool. A plain right-click is reserved for
      // service interaction when it is a click; a right-drag still rotates the camera.
      if (e.Button == MouseButtons.Left && worldModelInspectPending) {
        worldModelInspectPending = false;
        if (btnWorldInspectModel != null) btnWorldInspectModel.Checked = false;
        string details = panelRender?.InspectModelAtScreen(e.X, e.Y) ?? "World renderer is not ready.";
        SetStatusLabel(details.StartsWith("World model inspector", StringComparison.Ordinal) ? "Model identified — details opened." : details.Replace("\r", " ").Replace("\n", " "));
        MessageBox.Show(this, details + "\r\n\r\nTip: Press Ctrl+C while this dialog is focused to copy the complete diagnostic text.", "World model inspector", MessageBoxButtons.OK, MessageBoxIcon.Information);
        return;
      }

      // Ctrl+left-click is *only* the object/info inspector. Plain left OR plain right click is
      // service interaction (Wonkavator, taxi, quick travel, ...). Drags remain camera input and
      // never trigger either action. Ctrl+right-click keeps the existing "clear selection" shortcut.
      bool ctrl = (Control.ModifierKeys & Keys.Control) != 0;
      if (ctrl && e.Button == MouseButtons.Left && panelRender?.IsFullMapOpen != true) {
        worldSelectionClickPending = true;
        worldSelectionClickButton = e.Button;
        worldSelectionClickStart = e.Location;
        worldTaxiClickPending = false;
      } else if (!ctrl && panelRender?.IsFullMapOpen != true) {
        worldSelectionClickPending = false;
        worldTaxiClickPending = true;
        worldTaxiClickButton = e.Button;
        worldTaxiClickStart = e.Location;
      }
    }

    private void RenderPanel_MouseMove(object sender, MouseEventArgs e) {
      if (worldSelectionClickPending && worldSelectionClickButton == MouseButtons.Left) {
        int dx = e.X - worldSelectionClickStart.X;
        int dy = e.Y - worldSelectionClickStart.Y;
        if (dx * dx + dy * dy >= 16) worldSelectionClickPending = false;
      }
      if (worldTaxiClickPending && (e.Button & worldTaxiClickButton) != 0) {
        int dx = e.X - worldTaxiClickStart.X;
        int dy = e.Y - worldTaxiClickStart.Y;
        if (dx * dx + dy * dy >= 16) worldTaxiClickPending = false;
      }
    }

    private void RenderPanel_MouseUp(object sender, MouseEventArgs e) {
      if (e.Button != MouseButtons.Left && e.Button != MouseButtons.Right) return;
      if (e.Button == MouseButtons.Left && worldSelectionClickPending && worldSelectionClickButton == MouseButtons.Left) {
        worldSelectionClickPending = false;
        worldTaxiClickPending = false;
        string details = panelRender?.SelectWorldObjectAtScreen(e.X, e.Y, false, true) ?? "World renderer is not ready.";
        WorldInteractionInfo selectedInteraction = panelRender?.SelectedWorldInteraction;
        if (selectedInteraction?.Kind == WorldInteractionKind.MissionBoard) {
          PopulateWorldMissionBoard(selectedInteraction);
          panelRender?.RefreshSelectedWorldInteractionDetails();
          details = panelRender?.SelectedWorldModelDetails ?? details;
        }
        UpdateWorldSelectedObjectMenu();
        string summary = panelRender?.SelectedWorldModelSummary ?? String.Empty;
        if (!String.IsNullOrWhiteSpace(summary)) {
          ShowWorldSelectionInfo(e.Location);
          SetStatusLabel("Selected: " + summary + "  (Ctrl+click again to cycle)");
        } else {
          HideWorldSelectionInfo();
          SetStatusLabel(details.Replace("\r", " ").Replace("\n", " "));
        }
        return;
      }

      if (!worldTaxiClickPending || e.Button != worldTaxiClickButton) return;
      worldTaxiClickPending = false;
      string interactionPick = panelRender?.SelectWorldSpawnAtScreen(e.X, e.Y) ?? "World renderer is not ready.";
      if (TryOpenWonkavatorForSelectedObject()) { HideWorldSelectionInfo(); panelRender?.ClearWorldInteractionTarget(); return; }
      if (TryOpenTaxiMapForSelectedObject()) { HideWorldSelectionInfo(); panelRender?.ClearWorldInteractionTarget(); return; }
      if (panelRender?.TryOpenQuickTravelMapForSelectedSpawn() == true) { HideWorldSelectionInfo(); panelRender.ClearWorldInteractionTarget(); return; }

      WorldInteractionInfo interaction = panelRender?.InteractionTargetWorldInteraction;
      // Tutorial/knowledge PLCs are identified structurally, not by their localized high-level Placeable model. In
      // de-de some legacy objects are classified as another generic service before PlaceableLoader reaches the codex
      // field. Give a lore-looking selected PLC its language-independent raw/FQN resolution before generic services.
      // The resolver itself refuses non-lore objects unless it finds an explicit plcCodexSpec, so normal terminals are
      // unaffected by this precedence.
      if (TryOpenWorldLoreCodexForInteractionTarget()) {
        HideWorldSelectionInfo();
        panelRender?.ClearWorldInteractionTarget();
        return;
      }
      if (interaction?.Kind == WorldInteractionKind.Conversation) {
        HideWorldSelectionInfo();
        StartWorldConversationPlayback(interaction);
        panelRender?.ClearWorldInteractionTarget();
        return;
      }
      if (interaction?.Kind == WorldInteractionKind.MissionBoard) {
        HideWorldSelectionInfo();
        PopulateWorldMissionBoard(interaction);
        OpenWorldConversationPreview(interaction);
        panelRender?.ClearWorldInteractionTarget();
        return;
      }
      if (interaction?.Kind == WorldInteractionKind.Codex) {
        HideWorldSelectionInfo();
        OpenWorldCodexPreview(interaction);
        panelRender?.ClearWorldInteractionTarget();
        return;
      }
      // Services whose game UI is not emulated yet still participate in the interaction pick, so the click semantics
      // stay consistent without opening the read-only Ctrl inspector. This also makes it obvious which service type
      // has already been identified and is the next candidate for a dedicated preview.
      if (interaction != null) SetStatusLabel("Interaction recognized: " + WorldInteractionKindLabel(interaction.Kind) + ".");
      else SetStatusLabel(interactionPick.Replace("\r", " ").Replace("\n", " "));
      panelRender?.ClearWorldInteractionTarget();
    }

    private static string WorldInteractionKindLabel(WorldInteractionKind kind) {
      return kind switch {
        WorldInteractionKind.Wonkavator => "Wonkavator",
        WorldInteractionKind.Taxi => "taxi terminal",
        WorldInteractionKind.QuickTravel => "quick-travel bindpoint",
        WorldInteractionKind.Bank => "cargo hold / bank",
        WorldInteractionKind.GuildBank => "guild bank",
        WorldInteractionKind.Mailbox => "mailbox",
        WorldInteractionKind.Vendor => "vendor",
        WorldInteractionKind.ProfessionTrainer => "crafting trainer",
        WorldInteractionKind.ClassTrainer => "class trainer",
        WorldInteractionKind.Harvest => "harvesting node",
        WorldInteractionKind.MissionBoard => "mission board",
        WorldInteractionKind.Conversation => "conversation",
        WorldInteractionKind.Codex => "codex / knowledge object",
        WorldInteractionKind.AuctionHouse => "auction house",
        WorldInteractionKind.EnhancementStation => "enhancement station",
        _ => "service"
      };
    }

    private static ulong WonkUInt64(object value) {
      if (value == null) return 0;
      try {
        if (value is ulong ul) return ul;
        if (value is long l) return unchecked((ulong)l);
        if (value is uint ui) return ui;
        if (value is int i) return unchecked((ulong)i);
        return Convert.ToUInt64(value, System.Globalization.CultureInfo.InvariantCulture);
      } catch { return 0; }
    }

    private static long WonkInt64(object value) {
      if (value == null) return 0;
      try {
        if (value is long l) return l;
        if (value is ulong ul) return unchecked((long)ul);
        if (value is int i) return i;
        if (value is uint ui) return ui;
        return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
      } catch { return 0; }
    }

    private static string WonkStringText(StringTable table, long rawId, string context) {
      if (table == null || rawId == 0) return null;
      string localization = GomLib.StringTable.SelectedLocalization ?? "enMale";
      try {
        string direct = table.GetText(rawId, context ?? String.Empty, localization);
        if (!String.IsNullOrWhiteSpace(direct)) return direct;

        // SWTOR's wnkDestination*StringIDs (and the package display-name id) are authored as the low 32-bit
        // portion of an STB id. Jedipedia's stb.get(low,{file}) recomposes that low value with the table's high
        // half; on the handful of dual-high tables the larger matching high wins. Mirror that behavior here.
        uint low = unchecked((uint)rawId);
        long matchedId = 0;
        bool haveMatch = false;
        if (table.data != null) {
          foreach (long fullId in table.data.Keys) {
            if (unchecked((uint)fullId) != low) continue;
            if (!haveMatch || unchecked((ulong)fullId) > unchecked((ulong)matchedId)) {
              matchedId = fullId;
              haveMatch = true;
            }
          }
        }
        if (haveMatch && table.data.TryGetValue(matchedId, out StringTableEntry entry) && entry?.LocalizedText != null) {
          if (entry.LocalizedText.TryGetValue(localization, out string localized) && !String.IsNullOrWhiteSpace(localized)) return localized;
          // Keep the UI useful if a PTS table happens to omit the selected gender variant. Stay in the selected
          // language before falling back to English, matching the rest of the tool's localization behavior.
          string languagePrefix = localization.Length >= 2 ? localization.Substring(0, 2) : "en";
          foreach (KeyValuePair<string, string> variant in entry.LocalizedText)
            if (variant.Key.StartsWith(languagePrefix, StringComparison.OrdinalIgnoreCase) && !String.IsNullOrWhiteSpace(variant.Value)) return variant.Value;
          if (entry.LocalizedText.TryGetValue("enMale", out string english) && !String.IsNullOrWhiteSpace(english)) return english;
        }
      } catch { }
      return null;
    }

    private static object WonkDataValue(GomObjectData data, string name, string numericId = null) {
      if (data == null) return null;
      try {
        if (!String.IsNullOrWhiteSpace(name) && data.Dictionary.TryGetValue(name, out object value)) return value;
        if (!String.IsNullOrWhiteSpace(numericId) && data.Dictionary.TryGetValue(numericId, out value)) return value;
      } catch { }
      return null;
    }

    private static List<object> WonkValueList(object raw) {
      var result = new List<object>();
      if (raw == null) return result;
      if (raw is string) { result.Add(raw); return result; }
      if (raw is GomObjectData gomList) {
        // RED/HE32 serializes many GOM arrays as anonymous object-data maps instead of IDictionary. Preserve the
        // same authored index order as the retail branch below; the modern representation is otherwise untouched.
        var indexed = new List<Tuple<long, int, object>>();
        int sequence = 0;
        foreach (KeyValuePair<string, object> entry in gomList.Dictionary) {
          string key = entry.Key ?? String.Empty;
          if (String.Equals(key, "_count", StringComparison.OrdinalIgnoreCase)) { sequence++; continue; }
          long index;
          bool numeric = Int64.TryParse(key, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out index);
          indexed.Add(Tuple.Create(numeric ? index : Int64.MaxValue, sequence++, entry.Value));
        }
        foreach (Tuple<long, int, object> item in indexed.OrderBy(x => x.Item1).ThenBy(x => x.Item2)) result.Add(item.Item3);
        return result;
      }
      if (raw is System.Collections.IDictionary dictionary) {
        // GOM lists are commonly exposed as an index-keyed dictionary plus an optional _count member. Preserve the
        // authored list order explicitly; IDictionary enumeration order is not a format guarantee and older .NET
        // runtimes can otherwise pair a floor name with the wrong destination GUID.
        var indexed = new List<Tuple<long, int, object>>();
        int sequence = 0;
        foreach (System.Collections.DictionaryEntry entry in dictionary) {
          string key = Convert.ToString(entry.Key, System.Globalization.CultureInfo.InvariantCulture) ?? String.Empty;
          if (String.Equals(key, "_count", StringComparison.OrdinalIgnoreCase)) { sequence++; continue; }
          long index;
          bool numeric = Int64.TryParse(key, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out index);
          indexed.Add(Tuple.Create(numeric ? index : Int64.MaxValue, sequence++, entry.Value));
        }
        foreach (Tuple<long, int, object> item in indexed.OrderBy(x => x.Item1).ThenBy(x => x.Item2)) result.Add(item.Item3);
        return result;
      }
      if (raw is System.Collections.IEnumerable enumerable) {
        foreach (object value in enumerable) result.Add(value);
        return result;
      }
      result.Add(raw);
      return result;
    }

    private static GomObjectData FindWonkPackageRow(object raw, long packageId) {
      if (raw == null) return null;
      if (raw is GomObjectData row) {
        long rowId = WonkInt64(WonkDataValue(row, "wnkPackageID", "4611686061108531204"));
        if (rowId == packageId) return row;
        // Some parsers wrap a row in one additional anonymous object/list layer.
        foreach (object value in row.Dictionary.Values) {
          GomObjectData nested = FindWonkPackageRow(value, packageId);
          if (nested != null) return nested;
        }
        return null;
      }
      if (raw is System.Collections.IDictionary dictionary) {
        foreach (System.Collections.DictionaryEntry entry in dictionary) {
          if (WonkInt64(entry.Key) == packageId && entry.Value is GomObjectData keyedRow) return keyedRow;
          GomObjectData nested = FindWonkPackageRow(entry.Value, packageId);
          if (nested != null) return nested;
        }
        return null;
      }
      if (!(raw is string) && raw is System.Collections.IEnumerable enumerable) {
        foreach (object value in enumerable) {
          GomObjectData nested = FindWonkPackageRow(value, packageId);
          if (nested != null) return nested;
        }
      }
      return null;
    }

    private GomObject FindWonkPackagesPrototype() {
      if (currentDom == null) return null;
      try {
        GomObject direct = WorldResolveGomObject("wnkPackagesPrototype");
        if (direct?.Data != null && WonkDataValue(direct.Data, "wnkPackages", "4611686061108531209") != null) return direct;
      } catch { }

      // Client revisions have moved/aliased a few prototype object names. The field stable ID has not changed, so
      // resolve by content as a fallback instead of silently dropping to the duplicated map-note button labels.
      try {
        IEnumerable<string> names = WorldKnownPrototypeNames();
        foreach (string name in names.Where(x => !String.IsNullOrWhiteSpace(x) &&
          (x.IndexOf("wnk", StringComparison.OrdinalIgnoreCase) >= 0 ||
           x.IndexOf("wonka", StringComparison.OrdinalIgnoreCase) >= 0 ||
           x.IndexOf("elev", StringComparison.OrdinalIgnoreCase) >= 0))) {
          try {
            GomObject candidate = WorldResolveGomObject(name);
            if (candidate?.Data != null && WonkDataValue(candidate.Data, "wnkPackages", "4611686061108531209") != null) return candidate;
          } catch { }
        }
      } catch { }
      return null;
    }

    // wnkDestinationGUIDs does not point at mpnMetadataID. It contains the world-area instance GUID of the
    // destination object (normally the .spn_p panel at the far end). Most live-era areas happened to mirror that
    // GUID into their wonkavator map-note metadata, which made the old map-note-only lookup appear correct. Older
    // clients and a few current areas (notably Ziost) do not. Resolve the authored area instance first and use the
    // map-note metadata only as a compatibility fallback.
    private AreaMapNote ResolveWonkDestinationNote(long packageId, ulong destinationId, List<AreaMapNote> packageNotes) {
      if (destinationId == 0) return null;

      if (area?.RoomList != null) {
        foreach (FileFormats.Room room in area.RoomList) {
          if (room?.InstancesById == null || !room.InstancesById.TryGetValue(destinationId, out FileFormats.AssetInstance instance) || instance == null) continue;
          try {
            Matrix world = instance.GetAbsoluteTransform(room);
            Vector3 position = new Vector3(world.M41, world.M42, world.M43);

            // If this client also supplied a nearby wonkavator map note, retain its authored camera rotation and
            // slightly safer landing point. Do not require its metadata id to equal the destination GUID: that is
            // exactly the assumption that breaks RED and some later worlds.
            AreaMapNote nearby = packageNotes?.Where(n => n != null)
              .OrderBy(n => {
                float dx = n.Position.X - position.X, dy = n.Position.Y - position.Y, dz = n.Position.Z - position.Z;
                return dx * dx + dy * dy + dz * dz;
              })
              .FirstOrDefault();
            if (nearby != null) {
              float dx = nearby.Position.X - position.X, dy = nearby.Position.Y - position.Y, dz = nearby.Position.Z - position.Z;
              // Area coordinates are one tenth of the displayed coordinates. 2.5 here is therefore a generous
              // 25-display-unit tolerance for an icon/interaction-point offset, while still keeping stacked or
              // neighbouring elevator packages from being cross-wired.
              if (dx * dx + dy * dy + dz * dz <= 6.25f) return nearby;
            }

            return new AreaMapNote {
              Id = "wnk-instance-" + destinationId.ToString(System.Globalization.CultureInfo.InvariantCulture),
              Fqn = "wonkavator.destination",
              Label = "Wonkavator",
              Icon = "Wonkavator",
              WonkaPackageId = packageId,
              WonkaDestinationId = destinationId,
              Position = position,
              // The instance's authored local rotation is a useful fallback. Parent transforms can alter it, but
              // destination availability and position are authoritative; camera facing is cosmetic.
              Rotation = instance.rotation
            };
          } catch (Exception ex) {
            System.Diagnostics.Debug.WriteLine("Could not resolve Wonkavator destination instance " + destinationId + ": " + ex.Message);
          }
        }
      }

      // Compatibility fallback for areas/tools where the package GUID was copied into mpnMetadataID.
      return packageNotes?.FirstOrDefault(n => n != null && n.WonkaDestinationId == destinationId);
    }

    private WonkPackageInfo LoadWonkPackage(long packageId, string fallbackTitle) {
      if (packageId == 0 || currentDom == null) return null;
      List<AreaMapNote> packageNotes = area?.MapNotes?.Where(n => n != null && n.WonkaPackageId == packageId).ToList() ?? new List<AreaMapNote>();
      var result = new WonkPackageInfo { PackageId = packageId, Title = fallbackTitle };
      try {
        GomObject prototype = FindWonkPackagesPrototype();
        object rawPackages = WonkDataValue(prototype?.Data, "wnkPackages", "4611686061108531209");
        GomObjectData package = FindWonkPackageRow(rawPackages, packageId);
        if (package != null) {
          StringTable packageNames = currentDom.StringTable.Find("str.gui.elev_package_display_names");
          StringTable destinationNames = currentDom.StringTable.Find("str.gui.elev_dest_names");
          StringTable destinationDescriptions = currentDom.StringTable.Find("str.gui.elev_dest_descriptions");
          long titleId = WonkInt64(WonkDataValue(package, "wnkDisplayNameStringID", "4611686061108531205"));
          string title = titleId != 0 ? WonkStringText(packageNames, titleId, prototype?.Name ?? "wnkPackagesPrototype") : null;
          if (!String.IsNullOrWhiteSpace(title)) result.Title = title.Trim();

          List<object> nameIds = WonkValueList(WonkDataValue(package, "wnkDestinationNameStringIDs", "4611686061108531206"));
          List<object> descriptionIds = WonkValueList(WonkDataValue(package, "wnkDestinationDescriptionStringIDs", "4611686061108531207"));
          List<object> destinationGuids = WonkValueList(WonkDataValue(package, "wnkDestinationGUIDs", "4611686061132531191"));
          int floorCount = Math.Max(nameIds.Count, Math.Max(descriptionIds.Count, destinationGuids.Count));
          for (int i = 0; i < floorCount; i++) {
            long nameId = i < nameIds.Count ? WonkInt64(nameIds[i]) : 0;
            long descriptionId = i < descriptionIds.Count ? WonkInt64(descriptionIds[i]) : 0;
            ulong destinationId = i < destinationGuids.Count ? WonkUInt64(destinationGuids[i]) : 0;
            string name = nameId != 0 ? WonkStringText(destinationNames, nameId, prototype?.Name ?? "wnkPackagesPrototype") : null;
            string description = descriptionId != 0 ? WonkStringText(destinationDescriptions, descriptionId, prototype?.Name ?? "wnkPackagesPrototype") : null;
            if (String.IsNullOrWhiteSpace(name)) {
              string locale = GomLib.StringTable.SelectedLocalization ?? "enMale";
              bool german = locale.StartsWith("de", StringComparison.OrdinalIgnoreCase);
              bool french = locale.StartsWith("fr", StringComparison.OrdinalIgnoreCase);
              // Preserve the actual stable string id in the fallback so a bad/missing localization is diagnosable
              // instead of producing six indistinguishable copies of the terminal's button label.
              name = (german ? "Etage " : french ? "Étage " : "Floor ") + (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
              if (nameId != 0) name += "  [" + nameId.ToString(System.Globalization.CultureInfo.InvariantCulture) + "]";
            }
            AreaMapNote note = ResolveWonkDestinationNote(packageId, destinationId, packageNotes);
            result.Destinations.Add(new WonkDestinationItem {
              Name = name.Trim(),
              Description = String.IsNullOrWhiteSpace(description) ? null : description.Trim(),
              DestinationId = destinationId,
              Note = note
            });
          }

          // Some older clients omit wnkDestinationGUIDs even though their mapnotes carry mpnMetadataID. If the two
          // authored lists have the same cardinality, preserve package order and pair only otherwise-unresolved rows.
          if (result.Destinations.Count == packageNotes.Count && result.Destinations.Any(x => x.Note == null) && packageNotes.Count > 0) {
            List<AreaMapNote> ordered = packageNotes.OrderBy(n => n.WonkaDestinationId).ThenBy(n => n.Fqn, StringComparer.OrdinalIgnoreCase).ToList();
            for (int i = 0; i < result.Destinations.Count; i++)
              if (result.Destinations[i].Note == null && i < ordered.Count) result.Destinations[i].Note = ordered[i];
          }
        }
      } catch (Exception ex) {
        System.Diagnostics.Debug.WriteLine("Wonkavator package lookup failed for " + packageId + ": " + ex.Message);
      }

      if (String.IsNullOrWhiteSpace(result.Title)) result.Title = "Wonkavator";
      if (result.Destinations.Count == 0) {
        // Last-resort fallback only. Do not use the map-note FQN as the visible description: it is useful in the
        // inspector tooltip, but the actual elevator UI should stay readable and not masquerade as localized text.
        int index = 0;
        foreach (AreaMapNote note in packageNotes.OrderBy(n => n.WonkaDestinationId).ThenBy(n => n.Fqn, StringComparer.OrdinalIgnoreCase)) {
          index++;
          string locale = GomLib.StringTable.SelectedLocalization ?? "enMale";
          bool german = locale.StartsWith("de", StringComparison.OrdinalIgnoreCase);
          bool french = locale.StartsWith("fr", StringComparison.OrdinalIgnoreCase);
          string floor = (german ? "Etage " : french ? "Étage " : "Floor ") + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
          result.Destinations.Add(new WonkDestinationItem {
            Name = floor, Description = null, DestinationId = note.WonkaDestinationId, Note = note
          });
        }
      }
      return result;
    }

    private bool TryOpenWonkavatorForSelectedObject() {
      if (panelRender == null || currentDom == null) return false;
      string selectedFqn = panelRender.SelectedWorldSpawnFqn;
      if (String.IsNullOrWhiteSpace(selectedFqn)) return false;
      string selectedName = panelRender.SelectedWorldSpawnName;
      long packageId = 0;
      try {
        Placeable placeable = currentDom.PlaceableLoader.Load(selectedFqn);
        packageId = placeable?.WonkaPackageId ?? 0;
      } catch { }
      if (packageId == 0) {
        try { packageId = WonkInt64(WorldInteractionDataValue(WorldResolveGomObject(selectedFqn)?.Data, "wnkPackageID", "4611686061108531204")); } catch { }
      }
      if (packageId == 0) return false;

      Vector3? currentPosition = null;
      if (panelRender.TryGetSelectedWorldSpawnPosition(out float selectedX, out float selectedY, out float selectedZ))
        currentPosition = new Vector3(selectedX, selectedY, selectedZ);
      return OpenWonkavatorDialog(packageId, selectedName, currentPosition, false);
    }

    internal bool TryOpenWonkavatorForMapNote(AreaMapNote note, bool closeFullMapOnTeleport = false) {
      if (note == null || panelRender == null || currentDom == null) return false;
      string key = (note.Icon ?? String.Empty).ToLowerInvariant();
      bool wonka = note.WonkaPackageId != 0 || String.Equals(note.ServiceKind, "Wonkavator", StringComparison.OrdinalIgnoreCase) ||
        key.Contains("wonka") || key.Contains("elevator") || key.Contains("lift");
      if (!wonka) return false;

      long packageId = note.WonkaPackageId;
      if (packageId == 0 && !String.IsNullOrWhiteSpace(note.Fqn)) {
        string fqn = note.Fqn.Trim().Trim('.');
        foreach (string candidate in new[] { fqn, fqn.StartsWith("mpn.", StringComparison.OrdinalIgnoreCase) ? fqn : "mpn." + fqn }
          .Distinct(StringComparer.OrdinalIgnoreCase)) {
          try {
            GomLib.Models.MapNote resolved = currentDom.MapNoteLoader.Load(candidate);
            packageId = resolved?.WonkaPackageId ?? 0;
            if (packageId != 0) break;
          } catch { }
          if (packageId == 0 && WorldUsesLegacyContent) {
            try {
              GomObject rawNote = WorldResolveGomObject(candidate);
              packageId = WonkInt64(WorldInteractionDataValue(rawNote?.Data, "mpnMetadataInt", "4611686226320720002"));
              if (packageId != 0) break;
            } catch { }
          }
        }
      }
      if (packageId == 0 && worldSpnPlacements != null) {
        // Synthetic/service markers normally carry the package id directly. If an authored icon does not, bind it
        // to the nearest structurally identified Wonkavator so old client data remains clickable as well.
        const float radiusSq = 9f;
        WorldSpnPlacement nearest = worldSpnPlacements
          .Where(x => x?.Interaction?.Kind == WorldInteractionKind.Wonkavator &&
            (x.Interaction.WonkaPackageId != 0 || x.WonkaPackageId != 0) && x.Instance != null && x.Room != null)
          .Select(x => new { Spn = x, Pos = TryWorldMapPlacementPosition(x.Room, x.Instance, x.SpawnPoints, out Vector3 pos) ? (Vector3?)pos : null })
          .Where(x => x.Pos.HasValue && HorizontalMapDistanceSquared(x.Pos.Value, note.Position) <= radiusSq)
          .OrderBy(x => HorizontalMapDistanceSquared(x.Pos.Value, note.Position))
          .Select(x => x.Spn).FirstOrDefault();
        packageId = nearest?.Interaction?.WonkaPackageId ?? nearest?.WonkaPackageId ?? 0;
      }
      if (packageId == 0) {
        SetStatusLabel("Wonkavator map symbol found, but no elevator package is available in the local client data.");
        return true;
      }

      string title = WorldMapNoteDisplayName(note);
      UpdateWorldMapNoteToolTip(null, Point.Empty);
      return OpenWonkavatorDialog(packageId, title, note.Position, closeFullMapOnTeleport);
    }

    private bool OpenWonkavatorDialog(long packageId, string fallbackTitle, Vector3? currentPosition, bool closeFullMapOnTeleport) {
      if (panelRender == null || currentDom == null || packageId == 0) return false;
      WonkPackageInfo package = LoadWonkPackage(packageId, fallbackTitle);
      if (package == null) return true;

      // Preselect the destination that best matches the terminal/map symbol position. Elevator packages are frequently
      // reused on several floors; Y is included so stacked floors resolve to the correct current destination.
      WonkDestinationItem currentDestination = null;
      if (currentPosition.HasValue) {
        Vector3 origin = currentPosition.Value;
        currentDestination = package.Destinations
          .Where(x => x?.Note != null)
          .OrderBy(x => {
            float dx = x.Note.Position.X - origin.X, dy = x.Note.Position.Y - origin.Y, dz = x.Note.Position.Z - origin.Z;
            return dx * dx + dy * dy + dz * dz;
          }).FirstOrDefault();
      }
      string loc = GomLib.StringTable.SelectedLocalization ?? "enMale";
      bool de = loc.StartsWith("de", StringComparison.OrdinalIgnoreCase), fr = loc.StartsWith("fr", StringComparison.OrdinalIgnoreCase);
      string teleportText = de ? "Teleportieren" : fr ? "Téléporter" : "Teleport";
      string closeText = de ? "Schließen" : fr ? "Fermer" : "Close";
      string noDestination = de ? "Für dieses Ziel wurde in der geladenen Welt keine Position gefunden." :
        fr ? "Aucune position n'a été trouvée pour cette destination dans la zone chargée." :
        "No position for this destination was found in the loaded area.";

      WonkDestinationItem selectedDestination = null;
      using (var dialog = new Form {
        Text = package.Title, StartPosition = FormStartPosition.CenterParent, ShowInTaskbar = false,
        FormBorderStyle = FormBorderStyle.SizableToolWindow, MinimumSize = new Size(440, 300), Size = new Size(560, 390),
        BackColor = Color.FromArgb(3, 31, 45), ForeColor = Color.FromArgb(88, 200, 235)
      }) {
        var header = new Label { Dock = DockStyle.Top, Height = 42, Text = package.Title, Font = new Font(Font, FontStyle.Bold),
          Padding = new Padding(12, 10, 8, 4), ForeColor = Color.FromArgb(232, 202, 94) };
        var list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false,
          BackColor = Color.FromArgb(6, 40, 56), ForeColor = Color.FromArgb(202, 231, 240), BorderStyle = BorderStyle.FixedSingle };
        list.Columns.Add(de ? "Ziel" : fr ? "Destination" : "Destination", 210);
        list.Columns.Add(de ? "Beschreibung" : fr ? "Description" : "Description", 300);
        foreach (WonkDestinationItem destination in package.Destinations) {
          string itemName = destination.Name;
          if (destination.Note == null) itemName += de ? "  (nicht in dieser Welt)" : fr ? "  (hors de cette zone)" : "  (not in this area)";
          var item = new ListViewItem(itemName) { Tag = destination };
          item.SubItems.Add(destination.Description ?? String.Empty);
          list.Items.Add(item);
          if (ReferenceEquals(destination, currentDestination)) { item.Selected = true; item.Focused = true; }
        }
        list.SelectedIndexChanged += (_, __) => {
          if (list.SelectedItems.Count != 1) return;
          if (list.SelectedItems[0].Tag is WonkDestinationItem warm && warm.Note != null) panelRender?.RequestTeleportWarmup(warm.Note);
        };
        if (currentDestination?.Note != null) panelRender?.RequestTeleportWarmup(currentDestination.Note);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 48, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
        var close = new Button { Text = closeText, Width = 100, DialogResult = DialogResult.Cancel };
        var teleport = new Button { Text = teleportText, Width = 120 };
        buttons.Controls.Add(close); buttons.Controls.Add(teleport);
        dialog.Controls.Add(list); dialog.Controls.Add(buttons); dialog.Controls.Add(header);
        dialog.CancelButton = close;
        Action choose = () => {
          if (list.SelectedItems.Count == 0) return;
          selectedDestination = list.SelectedItems[0].Tag as WonkDestinationItem;
          if (selectedDestination?.Note == null) { MessageBox.Show(dialog, noDestination, package.Title, MessageBoxButtons.OK, MessageBoxIcon.Information); selectedDestination = null; return; }
          dialog.DialogResult = DialogResult.OK; dialog.Close();
        };
        teleport.Click += (_, __) => choose();
        list.DoubleClick += (_, __) => choose();
        if (list.Items.Count > 0 && list.SelectedItems.Count == 0) { list.Items[0].Selected = true; list.Items[0].Focused = true; }
        dialog.Shown += (_, __) => { if (list.SelectedItems.Count > 0) { list.SelectedItems[0].EnsureVisible(); list.Select(); } };
        dialog.ShowDialog(this);
      }

      if (selectedDestination?.Note != null) {
        panelRender.RequestTeleportWarmup(selectedDestination.Note);
        panelRender.TeleportToMapNote(selectedDestination.Note);
        if (closeFullMapOnTeleport && panelRender.IsFullMapOpen) panelRender.CloseInteractiveMap();
        SetStatusLabel((de ? "Wonkavator: " : fr ? "Ascenseur : " : "Elevator: ") + selectedDestination.Name);
      }
      if (selectedDestination?.Note == null) panelRender.CancelTeleportWarmup();
      panelRender.ClearWorldInteractionTarget();
      ActivateWorldRenderInput();
      return true;
    }

    private void ShowWorldSelectionInfo(Point renderPoint) {
      string summary = panelRender?.SelectedWorldModelSummary ?? String.Empty;
      string details = panelRender?.SelectedWorldModelDetails ?? String.Empty;
      if (String.IsNullOrWhiteSpace(summary) || String.IsNullOrWhiteSpace(details)) { HideWorldSelectionInfo(); return; }
      if (worldSelectionInfoForm == null || worldSelectionInfoForm.IsDisposed) {
        worldSelectionInfoForm = new Form {
          Text = "World object",
          FormBorderStyle = FormBorderStyle.SizableToolWindow,
          ShowInTaskbar = false,
          StartPosition = FormStartPosition.Manual,
          MinimumSize = new Size(330, 210),
          Size = new Size(440, 310),
          BackColor = Color.FromArgb(13, 24, 29)
        };
        worldSelectionInfoTitle = new Label {
          Dock = DockStyle.Top,
          Height = 42,
          Padding = new Padding(9, 7, 9, 5),
          AutoEllipsis = true,
          Font = new Font(Font, FontStyle.Bold),
          ForeColor = Color.FromArgb(135, 220, 242),
          BackColor = Color.FromArgb(8, 19, 23)
        };
        worldSelectionInfoText = new RichTextBox {
          Dock = DockStyle.Fill,
          ReadOnly = true,
          BorderStyle = BorderStyle.None,
          BackColor = Color.FromArgb(13, 24, 29),
          ForeColor = Color.Gainsboro,
          Font = new Font(FontFamily.GenericMonospace, 9f),
          DetectUrls = false,
          ScrollBars = RichTextBoxScrollBars.Vertical
        };
        worldSelectionInfoActions = new Panel {
          Dock = DockStyle.Bottom,
          Height = 42,
          Padding = new Padding(8, 6, 8, 5),
          BackColor = Color.FromArgb(8, 19, 23),
          Visible = false
        };
        worldSelectionConversationButton = WorldDarkActionButton("Open conversation tree…", 170);
        worldSelectionConversationButton.AutoSize = true; worldSelectionConversationButton.Dock = DockStyle.Left;
        worldSelectionConversationButton.Click += (_, __) => OpenSelectedWorldConversationPreview();
        worldSelectionCodexButton = WorldDarkActionButton("Open codex entry…", 140);
        worldSelectionCodexButton.AutoSize = true; worldSelectionCodexButton.Dock = DockStyle.Left; worldSelectionCodexButton.Margin = new Padding(6, 0, 0, 0);
        worldSelectionCodexButton.Click += (_, __) => OpenSelectedWorldCodexPreview();
        worldSelectionInfoActions.Controls.Add(worldSelectionCodexButton);
        worldSelectionInfoActions.Controls.Add(worldSelectionConversationButton);
        worldSelectionInfoForm.Controls.Add(worldSelectionInfoText);
        worldSelectionInfoForm.Controls.Add(worldSelectionInfoActions);
        worldSelectionInfoForm.Controls.Add(worldSelectionInfoTitle);
        worldSelectionInfoForm.FormClosed += (_, __) => {
          worldSelectionInfoForm = null; worldSelectionInfoTitle = null; worldSelectionInfoText = null;
          worldSelectionInfoActions = null; worldSelectionConversationButton = null; worldSelectionCodexButton = null;
        };
      }
      worldSelectionInfoTitle.Text = summary;
      worldSelectionInfoText.Text = details;
      UpdateWorldSelectionInfoActions();
      Point screen = renderPanel.PointToScreen(renderPoint);
      Rectangle work = Screen.FromPoint(screen).WorkingArea;
      int x = Math.Min(work.Right - worldSelectionInfoForm.Width, Math.Max(work.Left, screen.X + 16));
      int y = Math.Min(work.Bottom - worldSelectionInfoForm.Height, Math.Max(work.Top, screen.Y + 16));
      worldSelectionInfoForm.Location = new Point(x, y);
      if (!worldSelectionInfoForm.Visible) worldSelectionInfoForm.Show(this); else worldSelectionInfoForm.BringToFront();
      renderPanel.Focus();
    }

    private void HideWorldSelectionInfo() {
      if (worldSelectionInfoForm != null && !worldSelectionInfoForm.IsDisposed) worldSelectionInfoForm.Hide();
    }


    private void UpdateWorldSelectedObjectMenu() {
      if (btnWorldSelectedObject == null) return;
      string summary = panelRender?.SelectedWorldModelSummary ?? String.Empty;
      btnWorldSelectedObject.Enabled = !String.IsNullOrWhiteSpace(summary);
      btnWorldSelectedObject.Text = String.IsNullOrWhiteSpace(summary) ? "Selected object" : "Selected: " + EllipsizeWorldMenuText(summary, 52);
      btnWorldSelectedObject.ToolTipText = summary;
      if (btnWorldSelectedConversation != null) {
        WorldInteractionInfo interaction = panelRender?.SelectedWorldInteraction;
        bool canOpenConversation = CanOpenSelectedWorldConversation();
        btnWorldSelectedConversation.Visible = canOpenConversation;
        btnWorldSelectedConversation.Enabled = canOpenConversation;
        btnWorldSelectedConversation.Text = interaction?.Kind == WorldInteractionKind.MissionBoard ? "Open notice tree…" : "Open conversation…";
      }
      UpdateWorldSelectionInfoActions();
      UpdateWorldHiddenObjectsMenu();
    }

    private static string EllipsizeWorldMenuText(string text, int maxChars) {
      string value = text ?? String.Empty;
      if (maxChars < 4 || value.Length <= maxChars) return value;
      return value.Substring(0, maxChars - 1) + "…";
    }

    private void RebuildCurrentRegionsMenu() {
      if (btnWorldCurrentRegions == null) return;
      foreach (ToolStripItem item in btnWorldCurrentRegions.DropDownItems.Cast<ToolStripItem>().ToArray()) item.Dispose();
      btnWorldCurrentRegions.DropDownItems.Clear();

      List<View_AREA.WorldVolumeInfo> pinned = panelRender?.PinnedWorldVolumeInfos ?? new List<View_AREA.WorldVolumeInfo>();
      List<View_AREA.WorldVolumeInfo> current = panelRender?.CurrentVolumeInfos(panelRender.CurrentCameraPosition, 32) ?? new List<View_AREA.WorldVolumeInfo>();
      var pinnedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

      Action<View_AREA.WorldVolumeInfo> addVolumeItem = captured => {
        if (captured == null) return;
        string text = captured.Description ?? String.Empty;
        var item = new ToolStripMenuItem(EllipsizeWorldMenuText(text, 100)) {
          ToolTipText = text + "\nClick: pin/unpin   Ctrl+Click: copy",
          Checked = panelRender?.IsWorldVolumePinned(captured.InstanceId, captured.RoomName) == true
        };
        item.Click += (_, __) => {
          if ((Control.ModifierKeys & Keys.Control) != 0) {
            try { Clipboard.SetText(text); SetStatusLabel("Volume description copied to clipboard."); } catch { }
            return;
          }
          bool nowPinned = panelRender?.TogglePinnedWorldVolume(captured.InstanceId, captured.RoomName) == true;
          SetStatusLabel(nowPinned ? "Volume pinned/highlighted." : "Volume pin cleared.");
          UpdateWorldVolumePanel(true);
          miniMapPicture?.Invalidate();
        };
        btnWorldCurrentRegions.DropDownItems.Add(item);
      };

      if (pinned.Count > 0) {
        btnWorldCurrentRegions.DropDownItems.Add(new ToolStripMenuItem("Pinned volumes (" + pinned.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")") { Enabled = false });
        foreach (View_AREA.WorldVolumeInfo volume in pinned) {
          if (volume == null) continue;
          pinnedKeys.Add((volume.RoomName ?? String.Empty) + "#" + volume.InstanceId.ToString(System.Globalization.CultureInfo.InvariantCulture));
          addVolumeItem(volume);
        }
        var clear = new ToolStripMenuItem("Clear all pinned volumes");
        clear.Click += (_, __) => { panelRender?.ClearPinnedWorldVolume(); SetStatusLabel("Pinned volumes cleared."); UpdateWorldVolumePanel(true); miniMapPicture?.Invalidate(); };
        btnWorldCurrentRegions.DropDownItems.Add(clear);
      }

      var unpinnedCurrent = current.Where(volume => volume != null && !pinnedKeys.Contains((volume.RoomName ?? String.Empty) + "#" + volume.InstanceId.ToString(System.Globalization.CultureInfo.InvariantCulture))).ToList();
      if (pinned.Count > 0 && (unpinnedCurrent.Count > 0 || current.Count == 0)) btnWorldCurrentRegions.DropDownItems.Add(new ToolStripSeparator());
      if (unpinnedCurrent.Count > 0) {
        btnWorldCurrentRegions.DropDownItems.Add(new ToolStripMenuItem("Current volumes (" + current.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")") { Enabled = false });
        foreach (View_AREA.WorldVolumeInfo volume in unpinnedCurrent) addVolumeItem(volume);
      } else if (current.Count == 0) {
        btnWorldCurrentRegions.DropDownItems.Add(new ToolStripMenuItem("(not inside an authored region/trigger volume)") { Enabled = false });
      }

      btnWorldCurrentRegions.DropDownItems.Add(new ToolStripSeparator());
      btnWorldCurrentRegions.DropDownItems.Add(new ToolStripMenuItem("Click = pin/unpin; Ctrl+Click = copy") { Enabled = false });
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
      int top = worldToolbar.Visible ? worldToolbar.Height : 0;
      renderPanel.Location = new System.Drawing.Point(0, top);
      renderPanel.Size = new System.Drawing.Size(splitContainer3.Panel1.ClientSize.Width, Math.Max(1, splitContainer3.Panel1.ClientSize.Height - top));
      renderPanel.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
      LayoutMiniMapWithinHost();
      LayoutPhaseBanner();
      LayoutWorldCutawayControl();
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

    internal void BeginWorldStreamingLoading(int catalogModels) {
      worldStreamingLoading=true;
      int total=Math.Max(1,catalogModels);
      ShowWorldLoading("Preparing visible world…", "Visible rooms: waiting for first camera frame • Catalog: 0 / "+catalogModels);
      UpdateWorldLoading("Visible rooms: waiting for first camera frame • Catalog: 0 / "+catalogModels,0,total);
    }

    internal void UpdateWorldStreamingLoading(int decoded,int catalog,int activeDecoded,int activeTotal,int outstanding,int gpuPending,int materialPending,bool initial) {
      if(!worldStreamingLoading)return;
      int total=Math.Max(1,catalog);int ready=Math.Max(0,Math.Min(total,decoded));
      string details=String.Format(System.Globalization.CultureInfo.InvariantCulture,
        "Visible rooms: {0} / {1} decoded • Catalog: {2} / {3} • Queue: {4} • GPU: {5} • Materials: {6}",
        activeDecoded,activeTotal,ready,total,Math.Max(0,outstanding),Math.Max(0,gpuPending),Math.Max(0,materialPending));
      UpdateWorldLoading(details,ready,total);
      if(!initial){worldStreamingLoading=false;HideWorldLoading();}
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
      miniMapSourceButton = new Button { Dock = DockStyle.Right, Width = 82, Text = "In-game", TabStop = false, FlatStyle = FlatStyle.Flat };
      miniMapSourceButton.FlatAppearance.BorderSize = 0;
      miniMapSourceButton.Click += (_, __) => {
        if(panelRender==null)return;
        panelRender.SetMiniMapOriginalArt(!panelRender.MiniMapPrefersOriginalArt);
        miniMapTitle.Text="Minimap — rendering…";
      };
      miniMapScopeButton = new Button { Dock = DockStyle.Right, Width = 72, Text = "Area", TabStop = false, FlatStyle = FlatStyle.Flat };
      miniMapScopeButton.FlatAppearance.BorderSize = 0;
      miniMapScopeButton.Click += (_, __) => {
        if(panelRender==null)return;
        if(panelRender.MiniMapIsWorldScope&&!panelRender.MiniMapHasAreaScope)SetStatusLabel("No separate area map exists at the current height; the world map remains active.");
        else panelRender.SetMiniMapWorldScope(!panelRender.MiniMapIsWorldScope);
        miniMapTitle.Text="Minimap — rendering…";
      };
      miniMapTitleBar.Controls.Add(miniMapTitle);
      miniMapTitleBar.Controls.Add(miniMapScopeButton);
      miniMapTitleBar.Controls.Add(miniMapSourceButton);
      miniMapTitleBar.Controls.Add(miniMapClose);

      miniMapPicture = new PictureBox { Dock = DockStyle.Fill, BackColor = Color.FromArgb(28, 28, 28), Cursor = Cursors.Cross };
      worldMapNoteToolTip = new ToolTip { InitialDelay = 220, ReshowDelay = 60, AutoPopDelay = 12000, ShowAlways = true };
      miniMapPicture.Paint += MiniMapPicture_Paint;
      miniMapPicture.MouseDown += MiniMapPicture_MouseDown;
      miniMapPicture.MouseMove += MiniMapPicture_MouseMove;
      miniMapPicture.MouseUp += MiniMapPicture_MouseUp;
      miniMapPicture.MouseClick += MiniMapPicture_MouseClick;
      miniMapPicture.MouseLeave += (_, __) => UpdateWorldMapNoteToolTip(null, Point.Empty, miniMapPicture);

      // Eight resize handles let the minimap grow/shrink independently on either axis.  They deliberately overlay
      // only a few pixels at the panel border, leaving normal map drag/click behaviour untouched in the content.
      miniMapResizeGrip = CreateMiniMapResizeHandle(MiniMapResizeEdges.Right | MiniMapResizeEdges.Bottom, Cursors.SizeNWSE, true);
      CreateMiniMapResizeHandle(MiniMapResizeEdges.Left, Cursors.SizeWE);
      CreateMiniMapResizeHandle(MiniMapResizeEdges.Right, Cursors.SizeWE);
      CreateMiniMapResizeHandle(MiniMapResizeEdges.Top, Cursors.SizeNS);
      CreateMiniMapResizeHandle(MiniMapResizeEdges.Bottom, Cursors.SizeNS);
      CreateMiniMapResizeHandle(MiniMapResizeEdges.Left | MiniMapResizeEdges.Top, Cursors.SizeNWSE);
      CreateMiniMapResizeHandle(MiniMapResizeEdges.Right | MiniMapResizeEdges.Top, Cursors.SizeNESW);
      CreateMiniMapResizeHandle(MiniMapResizeEdges.Left | MiniMapResizeEdges.Bottom, Cursors.SizeNESW);

      miniMapPanel.Controls.Add(miniMapPicture);
      miniMapPanel.Controls.Add(miniMapTitleBar);
      foreach (Panel handle in miniMapResizeHandles) miniMapPanel.Controls.Add(handle);
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

    private void InitializeWorldVolumePanel() {
      worldVolumePanel = new Panel {
        Visible = false,
        BorderStyle = BorderStyle.FixedSingle,
        BackColor = SystemColors.Control,
        Size = new Size(410, 180)
      };
      var titleBar = new Panel { Dock = DockStyle.Top, Height = 27, BackColor = SystemColors.ControlDark };
      worldVolumeTitle = new Label {
        Dock = DockStyle.Fill,
        Text = "Current volumes",
        TextAlign = ContentAlignment.MiddleLeft,
        Padding = new Padding(7, 0, 0, 0),
        AutoEllipsis = true
      };
      worldVolumeClose = new Button { Dock = DockStyle.Right, Width = 27, Text = "×", TabStop = false, FlatStyle = FlatStyle.Flat };
      worldVolumeClose.FlatAppearance.BorderSize = 0;
      worldVolumeClose.Click += (_, __) => {
        worldSettings.ShowVolumeList = false;
        updatingWorldToolbar = true;
        try { if (btnWorldVolumeList != null) btnWorldVolumeList.Checked = false; }
        finally { updatingWorldToolbar = false; }
        ApplyWorldSettings();
        UpdateWorldVolumePanel(true);
      };
      titleBar.Controls.Add(worldVolumeTitle);
      titleBar.Controls.Add(worldVolumeClose);

      worldVolumeEntries = new FlowLayoutPanel {
        Dock = DockStyle.Fill,
        FlowDirection = FlowDirection.TopDown,
        WrapContents = false,
        AutoScroll = true,
        Padding = new Padding(4),
        BackColor = SystemColors.Control
      };
      worldVolumeToolTip = new ToolTip { AutoPopDelay = 12000, InitialDelay = 300, ReshowDelay = 100 };
      worldVolumePanel.Controls.Add(worldVolumeEntries);
      worldVolumePanel.Controls.Add(titleBar);
      splitContainer3.Panel1.Controls.Add(worldVolumePanel);
      splitContainer3.Panel1.Resize += (_, __) => LayoutWorldVolumePanel();
      LayoutWorldVolumePanel();
    }

    private static string WorldVolumePanelText(View_AREA.WorldVolumeInfo info, bool pinned) {
      if (info == null) return String.Empty;
      string kind = info.IsRegion ? "REGION" : "TRIGGER";
      string text = kind + " " + (String.IsNullOrWhiteSpace(info.ClassType) ? "GENERIC" : info.ClassType);
      if (!String.IsNullOrWhiteSpace(info.Detail)) text += " — " + info.Detail;
      else if (!String.IsNullOrWhiteSpace(info.AssetPath)) text += " — " + Path.GetFileName(info.AssetPath.Replace('\\', '/'));
      if (!String.IsNullOrWhiteSpace(info.RoomName)) text += " [" + info.RoomName + "]";
      return (pinned ? "[PIN] " : String.Empty) + text;
    }

    private void UpdateWorldVolumePanel(bool force = false) {
      if (worldVolumePanel == null || worldVolumeEntries == null) return;
      bool allowed = worldSettings.ShowVolumeList && !worldInterfaceHidden && !worldFullMapActive && panelRender != null && worldLoadingOverlay?.Visible != true;
      if (!allowed) { worldVolumePanel.Visible = false; return; }

      List<View_AREA.WorldVolumeInfo> current;
      List<View_AREA.WorldVolumeInfo> pinned;
      try {
        current = panelRender.CurrentVolumeInfos(panelRender.CurrentCameraPosition, 32) ?? new List<View_AREA.WorldVolumeInfo>();
        pinned = panelRender.PinnedWorldVolumeInfos ?? new List<View_AREA.WorldVolumeInfo>();
      } catch {
        // Area/volume indices can be replaced while a new world is loading. The next timer tick will rebuild the panel.
        worldVolumePanel.Visible = false;
        return;
      }
      string pinnedKey = String.Join("|", pinned.Select(v => (v?.RoomName ?? String.Empty) + "#" + (v?.InstanceId ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture)));
      string signature = pinnedKey + "||" + String.Join("|", current.Select(v => (v?.RoomName ?? String.Empty) + "#" + (v?.InstanceId ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture)));
      // If the user enabled the live list, keep the panel itself visible even when a teleport places the camera
      // outside every authored volume. Hiding the whole panel made a map teleport look as if the feature had been
      // switched off; an explicit empty-state row is both clearer and lets it recover naturally on the next tick.
      if (force || !String.Equals(worldVolumeSignature, signature, StringComparison.Ordinal)) {
        worldVolumeSignature = signature;
        worldVolumeEntries.SuspendLayout();
        worldVolumeToolTip?.RemoveAll();
        foreach (Control control in worldVolumeEntries.Controls.Cast<Control>().ToArray()) control.Dispose();
        worldVolumeEntries.Controls.Clear();

        var rows = new List<View_AREA.WorldVolumeInfo>();
        var pinnedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (View_AREA.WorldVolumeInfo info in pinned) {
          if (info == null) continue;
          rows.Add(info);
          pinnedKeys.Add((info.RoomName ?? String.Empty) + "#" + info.InstanceId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        foreach (View_AREA.WorldVolumeInfo info in current) {
          if (info == null) continue;
          string key = (info.RoomName ?? String.Empty) + "#" + info.InstanceId.ToString(System.Globalization.CultureInfo.InvariantCulture);
          if (pinnedKeys.Contains(key)) continue;
          rows.Add(info);
        }
        foreach (View_AREA.WorldVolumeInfo info in rows) {
          View_AREA.WorldVolumeInfo captured = info;
          bool isPinned = panelRender.IsWorldVolumePinned(captured.InstanceId, captured.RoomName);
          var row = new Button {
            Height = 34,
            Width = Math.Max(120, worldVolumeEntries.ClientSize.Width - 12),
            FlatStyle = FlatStyle.Flat,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
            UseMnemonic = false,
            Text = WorldVolumePanelText(captured, isPinned),
            Margin = new Padding(1, 1, 1, 2),
            TabStop = false
          };
          row.FlatAppearance.BorderSize = isPinned ? 2 : 1;
          worldVolumeToolTip?.SetToolTip(row, (captured.Description ?? String.Empty) + "\nClick: pin/highlight   Ctrl+Click: copy");
          row.Click += (_, __) => {
            if ((Control.ModifierKeys & Keys.Control) != 0) {
              try { Clipboard.SetText(captured.Description ?? String.Empty); SetStatusLabel("Volume description copied to clipboard."); } catch { }
              return;
            }
            bool nowPinned = panelRender?.TogglePinnedWorldVolume(captured.InstanceId, captured.RoomName) == true;
            SetStatusLabel(nowPinned ? "Volume pinned/highlighted." : "Volume pin cleared.");
            UpdateWorldVolumePanel(true);
            miniMapPicture?.Invalidate();
          };
          worldVolumeEntries.Controls.Add(row);
        }
        if (rows.Count == 0) {
          var empty = new Label {
            Height = 34,
            Width = Math.Max(120, worldVolumeEntries.ClientSize.Width - 12),
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
            Text = "(not inside an authored region/trigger volume)",
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(5, 4, 1, 2)
          };
          worldVolumeEntries.Controls.Add(empty);
        }
        worldVolumeEntries.ResumeLayout(true);
        worldVolumeTitle.Text = "Current volumes (" + current.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")" + (pinned.Count > 0 ? " — " + pinned.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + " pinned" : String.Empty);
      }

      worldVolumePanel.Visible = true;
      LayoutWorldVolumePanel();
      worldVolumePanel.BringToFront();
    }

    private void LayoutWorldVolumePanel() {
      if (worldVolumePanel == null || splitContainer3?.Panel1 == null) return;
      Control host = splitContainer3.Panel1;
      int topLimit = (worldToolbar?.Visible == true ? worldToolbar.Bottom : 0) + 6;
      int width = Math.Max(220, Math.Min(430, host.ClientSize.Width - 16));
      int rows = Math.Max(1, worldVolumeEntries?.Controls.Count ?? 1);
      int height = Math.Min(270, 35 + rows * 37);
      height = Math.Max(74, Math.Min(height, Math.Max(74, host.ClientSize.Height - topLimit - 8)));
      worldVolumePanel.Size = new Size(width, height);
      worldVolumePanel.Left = Math.Max(4, host.ClientSize.Width - width - 8);
      worldVolumePanel.Top = Math.Max(topLimit, host.ClientSize.Height - height - 8);
      if (worldVolumeEntries != null) {
        int rowWidth = Math.Max(120, worldVolumeEntries.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 10);
        foreach (Control row in worldVolumeEntries.Controls) row.Width = rowWidth;
      }
    }

    private void InitializeWorldRoomStatus() {
      toolStripPositionStatus = new ToolStripStatusLabel("Position: 0, 0, 0") {
        Spring = false,
        TextAlign = ContentAlignment.MiddleLeft,
        IsLink = true,
        ToolTipText = "SWTOR display coordinates. Click or press Ctrl+E to teleport."
      };
      toolStripPositionStatus.Click += (_, __) => ShowCoordinateTeleportDialog();
      toolStripPhaseStatus = new ToolStripStatusLabel("Phase: none") {
        Spring = false,
        TextAlign = ContentAlignment.MiddleLeft,
        ToolTipText = "Current SWTOR phs phase (INSTANCE_REGION or implicit area phase)"
      };
      toolStripShipDestinationStatus = new ToolStripDropDownButton("Ship: --") {
        Visible = false,
        ToolTipText = "Choose what is visible through the ship cockpit/window."
      };
      toolStripShipDestinationStatus.DropDownOpening += (_, __) => RebuildShipDestinationMenu();
      toolStripPerfStatus = new ToolStripStatusLabel("FPS: --   Occ S/D: 0/0") {
        Spring = false,
        Visible = btnWorldStats?.Checked == true,
        TextAlign = ContentAlignment.MiddleLeft,
        ToolTipText = "FPS, static/dynamic occlusion suppression, active streaming rooms/assets/decoded models and decode/GPU/material queue depths"
      };
      toolStripRoomStatus = new ToolStripStatusLabel("Current room: unknown") {
        Spring = true,
        TextAlign = ContentAlignment.MiddleRight
      };
      int insertIndex = Math.Max(0, statusStrip1.Items.Count - 1);
      statusStrip1.Items.Insert(insertIndex, toolStripPositionStatus);
      statusStrip1.Items.Insert(insertIndex + 1, toolStripPhaseStatus);
      statusStrip1.Items.Insert(insertIndex + 2, toolStripShipDestinationStatus);
      statusStrip1.Items.Insert(insertIndex + 3, toolStripPerfStatus);
      statusStrip1.Items.Insert(insertIndex + 4, toolStripRoomStatus);

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
        Vector3 position = panelRender?.CurrentDisplayPosition ?? new Vector3();
        toolStripPositionStatus.Text = string.Format(System.Globalization.CultureInfo.InvariantCulture, "Position: {0:0}, {1:0}, {2:0}", position.X, position.Y, position.Z);
        toolStripPhaseStatus.Text = "Phase: " + (String.IsNullOrWhiteSpace(phase) ? "none" : phase);
        RefreshShipDestinationStatus();
        if (toolStripPerfStatus != null && toolStripPerfStatus.Visible) toolStripPerfStatus.Text = panelRender?.CurrentRenderStats ?? "FPS: --   Occ S/D: 0/0";
        if (btnWorldSelectedObject?.Enabled == true && String.IsNullOrWhiteSpace(panelRender?.SelectedWorldModelSummary)) UpdateWorldSelectedObjectMenu();
        toolStripRoomStatus.Text = "Current room: " + room;
        UpdatePhaseBanner(phase);
        UpdateWorldVolumePanel();
        UpdateSpaceCombatEncounterTimeline();
        if (miniMapPanel != null && miniMapPanel.Visible) {
          if(panelRender?.MiniMapNeedsAutoScopeRefresh()==true) RefreshMiniMapForCamera();
          FollowMiniMapPlayer();
          miniMapPicture?.Invalidate();
        }
      };
      worldOverlayTimer.Start();
    }

    private void RefreshShipDestinationStatus(View_AREA.WorldShipVfxStatus status = null) {
      if (toolStripShipDestinationStatus == null) return;
      status ??= panelRender?.GetShipVfxStatus(false);
      if (status == null) { toolStripShipDestinationStatus.Visible = false; return; }
      toolStripShipDestinationStatus.Visible = !worldInterfaceHidden;
      toolStripShipDestinationStatus.Enabled = !status.Jumping;
      string label = status.Jumping ? "Traveling…" : (!String.IsNullOrWhiteSpace(status.Label) ? status.Label : "Off");
      toolStripShipDestinationStatus.Text = "Ship: " + label;
    }

    private void RebuildShipDestinationMenu() {
      if (toolStripShipDestinationStatus == null) return;
      View_AREA.WorldShipVfxStatus status = panelRender?.GetShipVfxStatus(true);
      toolStripShipDestinationStatus.DropDownItems.Clear();
      if (status == null) return;
      void AddDestination(string place, string label) {
        var item = new ToolStripMenuItem(String.IsNullOrWhiteSpace(label) ? (String.IsNullOrWhiteSpace(place) ? "Off" : place) : label) {
          Checked = String.Equals(status.Place ?? String.Empty, place ?? String.Empty, StringComparison.OrdinalIgnoreCase),
          CheckOnClick = false,
          Tag = place ?? String.Empty
        };
        item.Click += (_, __) => {
          View_AREA.WorldShipVfxStatus changed = panelRender?.SelectShipVfxDestination(item.Tag as string ?? String.Empty);
          RefreshShipDestinationStatus(changed);
        };
        toolStripShipDestinationStatus.DropDownItems.Add(item);
      }
      AddDestination(String.Empty, String.IsNullOrWhiteSpace(status.OffLabel) ? "Off" : status.OffLabel);
      if (status.Places.Count > 0) toolStripShipDestinationStatus.DropDownItems.Add(new ToolStripSeparator());
      foreach (View_AREA.WorldShipVfxDestination destination in status.Places)
        if (destination != null && !String.IsNullOrWhiteSpace(destination.Place)) AddDestination(destination.Place, destination.Label);
    }

    private void UpdatePhaseBanner(string phase) {
      if (phaseBannerLabel == null) return;
      string text = (phase ?? String.Empty).Trim();
      if (String.Equals(text, lastPhaseBannerText, StringComparison.Ordinal)) return;
      lastPhaseBannerText = text;
      phaseBannerLabel.Text = text;
      phaseBannerLabel.Visible = !worldInterfaceHidden && !String.IsNullOrWhiteSpace(text);
      LayoutPhaseBanner();
      if (phaseBannerLabel.Visible) phaseBannerLabel.BringToFront();
    }

    private void LayoutPhaseBanner() {
      if (phaseBannerLabel == null || splitContainer3?.Panel1 == null) return;
      int toolbarHeight = worldToolbar?.Visible == true ? worldToolbar.Height : 0;
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
      EnsureWorldMapModeControls();
      if (!active) UpdateWorldMapNoteToolTip(null, Point.Empty, renderPanel);
      bool showMini = !worldInterfaceHidden && !active && btnWorldMiniMap != null && btnWorldMiniMap.Checked;
      if (miniMapPanel != null) {
        miniMapPanel.Visible = showMini;
        if (showMini) { LayoutMiniMapWithinHost(); miniMapPanel.BringToFront(); }
      }
      UpdateWorldVolumePanel(true);
      RefreshWorldMapModeControls();
    }

    private void SetMiniMapVisible(bool visible) {
      if (miniMapPanel == null) return;
      miniMapPanel.Visible = visible && !worldFullMapActive && !worldInterfaceHidden;
      if (visible && !worldFullMapActive && !worldInterfaceHidden) {
        LayoutMiniMapWithinHost();
        miniMapPanel.BringToFront();
        ActivateWorldRenderInput();
        miniMapTitle.Text = "Minimap — rendering…";
        panelRender?.RequestMiniMapSnapshot(true);
        RefreshMiniMapModeControls();
      }
    }

    internal void RefreshMiniMapForCamera(){
      if(miniMapPanel?.Visible!=true||panelRender==null)return;
      miniMapTitle.Text="Minimap — rendering…";
      panelRender.RequestMiniMapSnapshot(true);
    }

    internal void RefreshMiniMapModeControls(){
      if(_closing||IsDisposed||Disposing)return;
      if(InvokeRequired){try{BeginInvoke(new Action(RefreshMiniMapModeControls));}catch{}return;}
      if(miniMapScopeButton==null||miniMapSourceButton==null||panelRender==null)return;
      bool world=panelRender.MiniMapIsWorldScope,hasArea=panelRender.MiniMapHasAreaScope;
      bool preferOriginal=panelRender.MiniMapPrefersOriginalArt,originalAvailable=panelRender.MiniMapOriginalArtAvailable;
      miniMapScopeButton.Text=world?(hasArea?"Area":"World"):"World";
      miniMapScopeButton.Enabled=true;
      miniMapSourceButton.Text=preferOriginal?"PugTools":"In-game";
      miniMapSourceButton.ForeColor=preferOriginal&&!originalAvailable?Color.Khaki:SystemColors.ControlText;
      worldMapNoteToolTip?.SetToolTip(miniMapScopeButton,hasArea?"Switch between the current SWTOR submap and the world map.":"No separate area map exists at the current height.");
      worldMapNoteToolTip?.SetToolTip(miniMapSourceButton,originalAvailable?"Switch between the generated PugTools map and the original SWTOR map art.":"No original SWTOR art exists for this page.");
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
      bool hadImage = miniMapImage != null;
      float previousZoom = miniMapZoom;
      bool restoringFollowView = miniMapFollowRestoreView;
      miniMapImage?.Dispose();
      miniMapImage = bitmap;
      miniMapFollowRefreshPending = false;
      // A forced full-extent refresh is attempted only once for the current page. A normal page/scope snapshot clears
      // the flag again, so malformed/incomplete area bounds cannot cause an endless render-refresh loop.
      miniMapFollowFullExtentActive = restoringFollowView;
      miniMapMinX = minX; miniMapMaxX = maxX; miniMapMinZ = minZ; miniMapMaxZ = maxZ;
      miniMapZoom = restoringFollowView
        ? Math.Max(1f, Math.Min(MiniMapMaxZoom, miniMapFollowRestoreZoom))
        : (hadImage ? Math.Max(1f, Math.Min(MiniMapMaxZoom, previousZoom)) : MiniMapDefaultFollowZoom);
      miniMapCenterX = (minX + maxX) * .5f; miniMapCenterZ = (minZ + maxZ) * .5f;
      if (panelRender != null) {
        panelRender.GetMapPose(out float playerX, out float playerZ, out _, out _);
        if (Single.IsFinite(playerX) && Single.IsFinite(playerZ)) {
          miniMapCenterX = playerX; miniMapCenterZ = playerZ;
          MiniMapVisibleWorldBounds(out _, out _, out _, out _);
        }
      }
      miniMapFollowRestoreView = false;
      UpdateMiniMapTitle();
      RefreshMiniMapModeControls();
      // Preserve a user-resized rectangle across minimap refreshes/page changes. Automatic aspect fitting is only
      // used until the first manual edge/corner resize.
      if (!miniMapUserResized) ResizeMiniMapToImageAspect();
      else LayoutMiniMapWithinHost();
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

      // Preserve world-space aspect when the user freely resizes the minimap. A wider window reveals more world to
      // the left/right; a taller window reveals more above/below instead of stretching the cached map bitmap.
      float viewportAspect = miniMapPicture == null || miniMapPicture.ClientSize.Height <= 0
        ? fullW / fullH
        : Math.Max(.05f, miniMapPicture.ClientSize.Width / (float)Math.Max(1, miniMapPicture.ClientSize.Height));
      float fullAspect = fullW / fullH;
      float baseW = fullW, baseH = fullH;
      if (viewportAspect > fullAspect) baseW = fullH * viewportAspect;
      else if (viewportAspect < fullAspect) baseH = fullW / viewportAspect;
      float visibleW = baseW / zoom, visibleH = baseH / zoom;
      float halfW = visibleW * .5f, halfH = visibleH * .5f;
      // Do not clamp the minimap centre to the cached page bounds.  The player marker is supposed to stay in the
      // exact centre even at an authored map edge.  Painting clips the backing bitmap to the available source area
      // and leaves the uncovered part as the normal minimap background until a neighbouring/expanded page arrives.
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


    private void DrawMiniMapBackingImage(Graphics graphics, Rectangle destination, RectangleF requestedSource) {
      if (graphics == null || miniMapImage == null || destination.IsEmpty) return;
      if (requestedSource.IsEmpty) { graphics.DrawImage(miniMapImage, destination); return; }

      RectangleF imageBounds = new RectangleF(0f, 0f, miniMapImage.Width, miniMapImage.Height);
      RectangleF clippedSource = RectangleF.Intersect(requestedSource, imageBounds);
      if (clippedSource.Width <= 0f || clippedSource.Height <= 0f) return;

      float scaleX = destination.Width / Math.Max(.0001f, requestedSource.Width);
      float scaleY = destination.Height / Math.Max(.0001f, requestedSource.Height);
      RectangleF clippedDestination = new RectangleF(
        destination.Left + (clippedSource.Left - requestedSource.Left) * scaleX,
        destination.Top + (clippedSource.Top - requestedSource.Top) * scaleY,
        clippedSource.Width * scaleX,
        clippedSource.Height * scaleY);
      graphics.DrawImage(miniMapImage, clippedDestination, clippedSource, GraphicsUnit.Pixel);
    }

    // Keep the player in the exact centre of the minimap, matching the in-game behaviour.  A drag may temporarily
    // move the view while the mouse button is held, but following resumes on the first overlay tick after release.
    private void FollowMiniMapPlayer() {
      if (miniMapImage == null || panelRender == null || miniMapPanning ||
          miniMapMaxX <= miniMapMinX || miniMapMaxZ <= miniMapMinZ) return;

      panelRender.GetMapPose(out float playerX, out float playerZ, out _, out _);
      if (!Single.IsFinite(playerX) || !Single.IsFinite(playerZ)) return;

      // Recenter before doing any cache/page work, so even the frame which triggers an expanded backing snapshot
      // keeps the marker in the exact middle instead of drifting to the edge while the new image is rendered.
      miniMapCenterX = playerX;
      miniMapCenterZ = playerZ;

      // The generated world-map backing image can use PugTools' smart-cropped main-area extent. If the player walks
      // beyond that crop while no authored child map becomes active, request one full-area backing snapshot once.
      // If that expanded page still does not cover the position, keep following anyway and simply show background
      // outside the cached art instead of letting the player marker leave the centre.
      if (panelRender.MiniMapIsWorldScope &&
          (playerX < miniMapMinX || playerX > miniMapMaxX || playerZ < miniMapMinZ || playerZ > miniMapMaxZ) &&
          !miniMapFollowFullExtentActive) {
        if (!miniMapFollowRefreshPending) {
          miniMapFollowRefreshPending = true;
          miniMapFollowRestoreView = true;
          miniMapFollowRestoreZoom = miniMapZoom;
          miniMapTitle.Text = "Minimap — rendering…";
          panelRender.RequestMiniMapSnapshot(true, true);
        }
        return;
      }

    }

    private void UpdateMiniMapTitle() {
      if (miniMapTitle == null) return;
      if(miniMapImage==null){miniMapTitle.Text="Minimap — rendering…";return;}
      string page=panelRender==null?String.Empty:(panelRender.MiniMapIsWorldScope?panelRender.MiniMapWorldName:panelRender.MiniMapAreaName);
      miniMapTitle.Text=string.Format(System.Globalization.CultureInfo.InvariantCulture,"Minimap{0} — {1:0.#}×",String.IsNullOrWhiteSpace(page)?String.Empty:" — "+page,miniMapZoom);
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
      if (panelRender != null) {
        panelRender.GetMapPose(out float playerX, out float playerZ, out _, out _);
        if (Single.IsFinite(playerX) && Single.IsFinite(playerZ)) { miniMapCenterX = playerX; miniMapCenterZ = playerZ; }
        else { miniMapCenterX = anchorX - (u - .5f) * newW; miniMapCenterZ = anchorZ - (v - .5f) * newH; }
      } else {
        miniMapCenterX = anchorX - (u - .5f) * newW;
        miniMapCenterZ = anchorZ - (v - .5f) * newH;
      }
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
      DrawMiniMapBackingImage(e.Graphics, dst, src);
      if (panelRender == null || miniMapMaxX <= miniMapMinX || miniMapMaxZ <= miniMapMinZ) return;
      MiniMapVisibleWorldBounds(out float visibleMinX, out float visibleMaxX, out float visibleMinZ, out float visibleMaxZ);

      // Paint the authored SWTOR mapnote icons live instead of baking them into the cached minimap image. The map
      // itself can stay completely static (and cheap) while quest/link/taxi/bindpoint/elevator markers remain crisp.
      if (worldSettings.ShowMapNotes && area?.MapNotes != null) {
        float worldWidth = Math.Max(.0001f, visibleMaxX - visibleMinX);
        float worldHeight = Math.Max(.0001f, visibleMaxZ - visibleMinZ);
        foreach (AreaMapNote note in area.MapNotes) {
          if (!WorldMapNoteVisible(note)) continue;
          Image icon = GetWorldMapIcon(note.Icon);
          if (icon == null) continue;
          float mapX = dst.Left + (note.Position.X - visibleMinX) / worldWidth * dst.Width;
          float mapY = dst.Top + (note.Position.Z - visibleMinZ) / worldHeight * dst.Height;
          if (mapX < dst.Left - icon.Width || mapX > dst.Right + icon.Width || mapY < dst.Top - icon.Height || mapY > dst.Bottom + icon.Height) continue;
          DrawWorldMapIcon(e.Graphics, icon, note, mapX, mapY);
        }
      }

      // Volume pins are a live overlay, not part of the cached minimap bitmap. This makes pin/unpin immediately
      // visible and, just as importantly, removes a pin immediately instead of leaving it baked into an old snapshot.
      List<Vector2> pinnedSegments = panelRender.PinnedWorldVolumeMapSegments;
      if (pinnedSegments != null && pinnedSegments.Count >= 2) {
        float worldWidth = Math.Max(.0001f, visibleMaxX - visibleMinX);
        float worldHeight = Math.Max(.0001f, visibleMaxZ - visibleMinZ);
        using var pinPen = new Pen(Color.FromArgb(235, 50, 235, 225), 2f);
        for (int i = 0; i + 1 < pinnedSegments.Count; i += 2) {
          Vector2 a = pinnedSegments[i], b = pinnedSegments[i + 1];
          PointF pa = new PointF(dst.Left + (a.X - visibleMinX) / worldWidth * dst.Width, dst.Top + (a.Y - visibleMinZ) / worldHeight * dst.Height);
          PointF pb = new PointF(dst.Left + (b.X - visibleMinX) / worldWidth * dst.Width, dst.Top + (b.Y - visibleMinZ) / worldHeight * dst.Height);
          e.Graphics.DrawLine(pinPen, pa, pb);
        }
      }

      panelRender.GetMapPose(out float x, out float z, out float lookX, out float lookZ);
      float u = (x - visibleMinX) / Math.Max(.0001f, visibleMaxX - visibleMinX);
      float v = (z - visibleMinZ) / Math.Max(.0001f, visibleMaxZ - visibleMinZ);
      float px = dst.Left + u * dst.Width, py = dst.Top + v * dst.Height;
      if (px < dst.Left - 8 || px > dst.Right + 8 || py < dst.Top - 8 || py > dst.Bottom + 8) return;
      // FpsCamera uses an RH projection while its Look vector is stored toward the target, so the actually visible
      // forward direction in this viewer is -Look (the same convention used by walking/taxi mode).
      lookX = -lookX; lookZ = -lookZ;
      float len = (float)Math.Sqrt(lookX * lookX + lookZ * lookZ);
      if (len < 0.0001f) { lookX = 0; lookZ = -1; len = 1; }
      lookX /= len; lookZ /= len;
      Image playerMarker = GetWorldMapPlayerMarkerImage();
      if (playerMarker == null) return;
      // The SWTOR/Jedipedia marker artwork points downward and uses an authored pivot at (11,6). Rotate that
      // down-vector onto the camera's X/Z look vector rather than approximating the icon with debug geometry.
      float angle = (float)(Math.Atan2(-lookX, lookZ) * 180.0 / Math.PI);
      System.Drawing.Drawing2D.GraphicsState markerState = e.Graphics.Save();
      try {
        e.Graphics.TranslateTransform(px, py);
        e.Graphics.RotateTransform(angle);
        e.Graphics.DrawImage(playerMarker, -11f, -6f, 22f, 27f);
      } finally { e.Graphics.Restore(markerState); }
    }

    private bool WorldMapNoteVisible(AreaMapNote note) {
      if (note == null || !worldSettings.ShowMapNotes) return false;
      string key = (note.Icon ?? String.Empty).ToLowerInvariant();
      if (key.Contains("bindpoint") || key.Contains("bind_point") || key.Contains("bind") || key.Contains("quicktravel")) return worldSettings.ShowMapIconBindpoints;
      if (key.Contains("maplink") || key.Contains("map_link") || key.Contains("exit")) return worldSettings.ShowMapIconMapLinks;
      if (key.Contains("explorationquest") || key.Contains("questarc")) return worldSettings.ShowMapIconExplorationQuests;
      if (key.Contains("quest") || key.Contains("missionboard")) return worldSettings.ShowMapIconQuests;
      if (key.Contains("taxi")) return worldSettings.ShowMapIconTaxi;
      if (key.Contains("vendor")) return worldSettings.ShowMapIconVendors;
      if (key.Contains("crewtrainer") || key.Contains("professiontrainer")) return worldSettings.ShowMapIconCrewTrainers;
      if (key.Contains("classtrainer") || key == "trainer") return worldSettings.ShowMapIconClassTrainers;
      if (key.Contains("resource") || key.Contains("harvest")) return worldSettings.ShowMapIconResources;
      if (key.Contains("mail")) return worldSettings.ShowMapIconMailboxes;
      if (key.Contains("enhancement") || key.Contains("modification")) return worldSettings.ShowMapIconEnhancementStations;
      if (key.Contains("guildbank") || key.Contains("bank") || key.Contains("cargohold")) return worldSettings.ShowMapIconCargoHold;
      if (key.Contains("auction") || key.Contains("galacticmarket")) return worldSettings.ShowMapIconGalacticMarket;
      if (key.Contains("wonkavator") || key.Contains("wonka") || key.Contains("elevator") || key.Contains("lift")) return worldSettings.ShowMapIconWonkavator;
      return worldSettings.ShowMapIconOther;
    }

    private static string WorldMapIconFileName(string iconName) {
      string key = (iconName ?? String.Empty).ToLowerInvariant();
      if (key.Contains("bindpoint") || key.Contains("bind_point") || key.Contains("quicktravel")) return "mpn-bindpoint.png";
      if (key.Contains("maplink") || key.Contains("map_link") || key.Contains("exit")) return "mpn-maplink.png";
      if (key.Contains("explorationquest") || key.Contains("questarc")) return "mpn-explorationquest.png";
      if (key.Contains("quest") && !key.Contains("missionboard")) return "mpn-quest.png";
      if (key.Contains("taxi")) return "mpn-taxi.png";
      if (key.Contains("vendor")) return "mpn-vendor.png";
      if (key.Contains("crewtrainer") || key.Contains("professiontrainer")) return "mpn-crewtrainer.png";
      if (key.Contains("classtrainer") || key == "trainer") return "mpn-classtrainer.png";
      if (key.Contains("resource") || key.Contains("harvest")) return "mpn-resource.png";
      if (key.Contains("mail")) return "mpn-mailbox.png";
      if (key.Contains("enhancement") || key.Contains("modification")) return "mpn-enhancement.png";
      if (key.Contains("guildbank")) return "mpn-guildbank.png";
      if (key.Contains("bank") || key.Contains("cargohold")) return "mpn-bank.png";
      if (key.Contains("auction") || key.Contains("galacticmarket")) return "mpn-auction.png";
      if (key.Contains("missionboard")) return "mpn-missionboard.png";
      if (key.Contains("wonkavator") || key.Contains("elevator") || key.Contains("lift")) return "mpn-wonkavator.png";
      if (key == "defaulticon" || key == "default") return "mpn-default.png";
      return null;
    }

    private Image GetWorldMapIcon(string iconName) {
      return GetWorldMapIconFile(WorldMapIconFileName(iconName));
    }

    private Image GetWorldMapPlayerMarkerImage() {
      return GetWorldMapIconFile("player-marker.png");
    }

    private Image GetWorldMapIconFile(string fileName) {
      if (String.IsNullOrWhiteSpace(fileName)) return null;
      if (worldMapIconImages.TryGetValue(fileName, out Image cached)) return cached;
      try {
        string path = Path.Combine(AppContext.BaseDirectory, "Resources", "WorldMapIcons", fileName);
        if (System.IO.File.Exists(path)) {
          using (Image source = Image.FromFile(path)) cached = new Bitmap(source);
        } else {
          string generatedKey = Path.GetFileNameWithoutExtension(fileName ?? String.Empty);
          if (generatedKey.StartsWith("mpn-", StringComparison.OrdinalIgnoreCase)) generatedKey = generatedKey.Substring(4);
          cached = WorldMapIconFactory.Create(generatedKey);
          if (cached == null) return null;
        }
        worldMapIconImages[fileName] = cached;
        return cached;
      } catch (Exception ex) {
        System.Diagnostics.Debug.WriteLine("World map icon load failed for " + fileName + ": " + ex.Message);
        return null;
      }
    }

    private static void DrawWorldMapIcon(Graphics graphics, Image icon, AreaMapNote note, float centerX, float centerY) {
      if (graphics == null || icon == null || note == null) return;
      string fileName = WorldMapIconFileName(note.Icon);
      // Jedipedia/game map links use the authored mapnote rotation (second vector component), negated for the
      // screen-space map. Other marker classes intentionally stay upright.
      if (String.Equals(fileName, "mpn-maplink.png", StringComparison.OrdinalIgnoreCase) && Math.Abs(note.Rotation.Y) > .001f) {
        System.Drawing.Drawing2D.GraphicsState state = graphics.Save();
        try {
          graphics.TranslateTransform(centerX, centerY);
          graphics.RotateTransform(-note.Rotation.Y);
          graphics.DrawImage(icon, -icon.Width * .5f, -icon.Height * .5f, icon.Width, icon.Height);
        } finally { graphics.Restore(state); }
        return;
      }
      graphics.DrawImage(icon, centerX - icon.Width * .5f, centerY - icon.Height * .5f, icon.Width, icon.Height);
    }

    private static string WorldMapNoteDisplayName(AreaMapNote note) {
      if (note == null) return String.Empty;
      string locale = GomLib.StringTable.SelectedLocalization;
      if (note.LocalizedName != null && !String.IsNullOrWhiteSpace(locale) &&
          note.LocalizedName.TryGetValue(locale, out string localized) && !String.IsNullOrWhiteSpace(localized)) return localized.Trim();
      if (!String.IsNullOrWhiteSpace(note.Label)) return note.Label.Trim();
      if (!String.IsNullOrWhiteSpace(note.Fqn)) return note.Fqn.Trim();
      return note.Id ?? String.Empty;
    }

    private static string LocalizedMapNoteType(string icon) {
      string key = (icon ?? String.Empty).ToLowerInvariant();
      string loc = GomLib.StringTable.SelectedLocalization ?? "enMale";
      bool de = loc.StartsWith("de", StringComparison.OrdinalIgnoreCase), fr = loc.StartsWith("fr", StringComparison.OrdinalIgnoreCase);
      if (key.Contains("taxi")) return de ? "Taxi" : fr ? "Taxi" : "Taxi";
      if (key.Contains("vendor")) return de ? "Händler" : fr ? "Marchand" : "Vendor";
      if (key.Contains("crewtrainer") || key.Contains("professiontrainer")) return de ? "Crew-Fähigkeiten-Ausbilder" : fr ? "Formateur de métier" : "Crew-skill trainer";
      if (key.Contains("classtrainer") || key == "trainer") return de ? "Ausbilder" : fr ? "Entraîneur" : "Trainer";
      if (key.Contains("resource") || key.Contains("harvest")) return de ? "Ressource" : fr ? "Ressource" : "Resource";
      if (key.Contains("mail")) return de ? "Post" : fr ? "Boîte aux lettres" : "Mailbox";
      if (key.Contains("enhancement") || key.Contains("modification")) return de ? "Modifikationsstation" : fr ? "Station de modification" : "Modification station";
      if (key.Contains("guildbank") || key.Contains("bank") || key.Contains("cargohold")) return de ? "Laderaumzugang" : fr ? "Soute" : "Cargo hold";
      if (key.Contains("auction") || key.Contains("galacticmarket")) return de ? "Galaktischer Markt" : fr ? "Marché galactique" : "Galactic Trade Network";
      if (key.Contains("wonka") || key.Contains("elevator") || key.Contains("lift")) return de ? "Aufzug" : fr ? "Ascenseur" : "Elevator";
      if (key.Contains("bind") || key.Contains("quicktravel")) return de ? "Schnellreisepunkt" : fr ? "Point de voyage rapide" : "Quick travel point";
      if (key.Contains("maplink") || key.Contains("exit")) return de ? "Kartenausgang" : fr ? "Sortie de carte" : "Map exit";
      if (key.Contains("explorationquest") || key.Contains("questarc")) return de ? "Erkundungsmission" : fr ? "Mission d'exploration" : "Exploration mission";
      if (key.Contains("missionboard")) return de ? "Missionsbrett" : fr ? "Terminal de mission" : "Mission board";
      if (key.Contains("quest")) return de ? "Quest" : fr ? "Quête" : "Quest";
      return String.IsNullOrWhiteSpace(icon) ? (de ? "Kartensymbol" : fr ? "Icône de carte" : "Map icon") : icon;
    }

    private string BuildWorldMapNoteToolTip(AreaMapNote note) {
      if (note == null) return String.Empty;
      EnsureWorldMapNoteQuestInfo(note);
      string loc = GomLib.StringTable.SelectedLocalization ?? "enMale";
      bool de = loc.StartsWith("de", StringComparison.OrdinalIgnoreCase), fr = loc.StartsWith("fr", StringComparison.OrdinalIgnoreCase);
      string nameLabel = de ? "Name" : fr ? "Nom" : "Name";
      string typeLabel = de ? "Typ" : fr ? "Type" : "Type";
      string conditionLabel = de ? "Bedingung" : fr ? "Condition" : "Condition";
      string positionLabel = de ? "Position" : fr ? "Position" : "Position";
      string packageLabel = de ? "Aufzugspaket" : fr ? "Paquet d'ascenseur" : "Elevator package";
      string destinationLabel = de ? "Ziel-ID" : fr ? "ID de destination" : "Destination ID";
      string iconLabel = de ? "Symbol" : fr ? "Icône" : "Icon";
      string mapLinkLabel = de ? "Kartenziel" : fr ? "Carte cible" : "Map target";
      string questLabel = de ? "Quest" : fr ? "Quête" : "Quest";
      string objectiveLabel = de ? "Ziel" : fr ? "Objectif" : "Objective";
      var sb = new StringBuilder();
      sb.AppendLine(nameLabel + ": " + WorldMapNoteDisplayName(note));
      sb.AppendLine("FQN: " + (String.IsNullOrWhiteSpace(note.Fqn) ? "-" : note.Fqn));
      sb.AppendLine(typeLabel + ": " + LocalizedMapNoteType(note.Icon));
      if (!String.IsNullOrWhiteSpace(note.Icon)) sb.AppendLine(iconLabel + ": " + note.Icon);
      if (note.QuestNames != null && note.QuestNames.Count > 0) {
        foreach (string questName in note.QuestNames.Where(x => !String.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
          sb.AppendLine(questLabel + ": " + questName.Trim());
      }
      if (note.QuestObjectives != null && note.QuestObjectives.Count > 0) {
        foreach (string objective in note.QuestObjectives.Where(x => !String.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
          sb.AppendLine(objectiveLabel + ": " + objective.Trim().Replace("\r", " ").Replace("\n", " / "));
      }
      if (!String.IsNullOrWhiteSpace(note.Id)) sb.AppendLine("ID: " + note.Id);
      if (note.AssetId != 0) sb.AppendLine("Asset ID: " + note.AssetId.ToString(System.Globalization.CultureInfo.InvariantCulture));
      if (!String.IsNullOrWhiteSpace(note.Condition) && !String.Equals(note.Condition, "None", StringComparison.OrdinalIgnoreCase)) sb.AppendLine(conditionLabel + ": " + note.Condition);
      if (note.WonkaPackageId != 0) sb.AppendLine(packageLabel + ": " + note.WonkaPackageId.ToString(System.Globalization.CultureInfo.InvariantCulture));
      if (note.WonkaDestinationId != 0) sb.AppendLine(destinationLabel + ": " + note.WonkaDestinationId.ToString(System.Globalization.CultureInfo.InvariantCulture));
      if (note.MapLinkAreaId != 0) {
        sb.Append(mapLinkLabel).Append(": ").Append(note.MapLinkAreaId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (note.MapLinkMapNameSId != 0) sb.Append(" / ").Append(note.MapLinkMapNameSId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (note.MapLinkSubmapNameSId != 0) sb.Append(" / ").Append(note.MapLinkSubmapNameSId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        sb.AppendLine();
      }
      sb.Append(positionLabel).Append(": ")
        .Append((note.Position.X * 10f).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)).Append(", ")
        .Append((note.Position.Z * 10f).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)).Append(", ")
        .Append((note.Position.Y * 10f).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
      if (note.Tags != null && note.Tags.Count > 0) sb.AppendLine().Append("Tags: ").Append(String.Join(", ", note.Tags));
      return sb.ToString();
    }

    internal void UpdateWorldMapNoteToolTip(AreaMapNote note, Point location, Control control = null) {
      if (_closing || IsDisposed || Disposing) return;
      if (InvokeRequired) {
        try { BeginInvoke(new Action<AreaMapNote, Point, Control>(UpdateWorldMapNoteToolTip), note, location, control); } catch { }
        return;
      }
      Control target = control ?? renderPanel;
      if (worldMapNoteToolTip == null || target == null || target.IsDisposed) return;
      if (note == null) {
        if (worldMapHoverControl != null && !worldMapHoverControl.IsDisposed) worldMapNoteToolTip.Hide(worldMapHoverControl);
        worldMapHoveredNote = null; worldMapHoverControl = null;
        return;
      }
      if (ReferenceEquals(note, worldMapHoveredNote) && ReferenceEquals(target, worldMapHoverControl)) return;
      if (worldMapHoverControl != null && !worldMapHoverControl.IsDisposed) worldMapNoteToolTip.Hide(worldMapHoverControl);
      worldMapHoveredNote = note; worldMapHoverControl = target;
      Point pt = new Point(Math.Min(Math.Max(0, location.X + 14), Math.Max(0, target.ClientSize.Width - 30)),
        Math.Min(Math.Max(0, location.Y + 18), Math.Max(0, target.ClientSize.Height - 30)));
      worldMapNoteToolTip.Show(BuildWorldMapNoteToolTip(note), target, pt, 12000);
    }

    private AreaMapNote HitTestMiniMapNote(Point point) {
      if (!worldSettings.ShowMapNotes || area?.MapNotes == null || miniMapImage == null) return null;
      Rectangle dst = MiniMapImageRectangle();
      if (dst.IsEmpty || !dst.Contains(point)) return null;
      MiniMapVisibleWorldBounds(out float visibleMinX, out float visibleMaxX, out float visibleMinZ, out float visibleMaxZ);
      float worldW = Math.Max(.0001f, visibleMaxX - visibleMinX), worldH = Math.Max(.0001f, visibleMaxZ - visibleMinZ);
      AreaMapNote best = null; float bestD2 = float.MaxValue;
      foreach (AreaMapNote note in area.MapNotes) {
        if (!WorldMapNoteVisible(note)) continue;
        Image icon = GetWorldMapIcon(note.Icon); if (icon == null) continue;
        float x = dst.Left + (note.Position.X - visibleMinX) / worldW * dst.Width;
        float y = dst.Top + (note.Position.Z - visibleMinZ) / worldH * dst.Height;
        float hx = Math.Max(8f, icon.Width * .6f), hy = Math.Max(8f, icon.Height * .6f);
        if (Math.Abs(point.X - x) > hx || Math.Abs(point.Y - y) > hy) continue;
        float dx = point.X - x, dy = point.Y - y, d2 = dx * dx + dy * dy;
        if (d2 < bestD2) { bestD2 = d2; best = note; }
      }
      return best;
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
      if (!miniMapPanning && (e.Button & MouseButtons.Left) == 0) UpdateWorldMapNoteToolTip(HitTestMiniMapNote(e.Location), e.Location, miniMapPicture);
      else UpdateWorldMapNoteToolTip(null, Point.Empty, miniMapPicture);
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
      FollowMiniMapPlayer();
      miniMapPicture?.Invalidate();
    }

    private void MiniMapPicture_MouseClick(object sender, MouseEventArgs e) {
      if (miniMapSuppressClick) { miniMapSuppressClick = false; return; }
      if (e.Button != MouseButtons.Left || panelRender == null || miniMapImage == null) return;
      Rectangle dst = MiniMapImageRectangle();
      if (dst.IsEmpty || !dst.Contains(e.Location) || miniMapMaxX <= miniMapMinX || miniMapMaxZ <= miniMapMinZ) return;
      AreaMapNote clickedNote = HitTestMiniMapNote(e.Location);
      if (clickedNote != null && TryOpenTaxiMapForMapNote(clickedNote)) return;
      if (clickedNote != null && TryOpenWonkavatorForMapNote(clickedNote, false)) return;
      if (clickedNote != null && TryOpenMapLinkForMapNote(clickedNote, true)) return;
      if (clickedNote != null && IsWorldQuestMapNote(clickedNote)) {
        // Quest pins are always informational. Never fall through to the minimap teleport path just because the
        // reverse quest link is missing in this particular client/cache build.
        if (!TryShowWorldMapQuestDetails(clickedNote))
          SetStatusLabel("Quest information is not available for this map symbol in the local client data.");
        return;
      }
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

    private Panel CreateMiniMapResizeHandle(MiniMapResizeEdges edges, Cursor cursor, bool visibleGrip = false) {
      var handle = new Panel {
        Tag = edges,
        Cursor = cursor,
        BackColor = visibleGrip ? SystemColors.ControlDark : Color.FromArgb(28, 28, 28),
        TabStop = false
      };
      handle.MouseDown += MiniMapResizeHandle_MouseDown;
      handle.MouseMove += MiniMapResizeHandle_MouseMove;
      handle.MouseUp += MiniMapResizeHandle_MouseUp;
      miniMapResizeHandles.Add(handle);
      return handle;
    }

    private void MiniMapResizeHandle_MouseDown(object sender, MouseEventArgs e) {
      if (e.Button != MouseButtons.Left || miniMapPanel == null || sender is not Control handle || handle.Tag is not MiniMapResizeEdges edges) return;
      miniMapResizing = true;
      miniMapResizeEdges = edges;
      miniMapDragStart = Cursor.Position;
      miniMapResizeStart = miniMapPanel.Size;
      miniMapResizeLocationStart = miniMapPanel.Location;
      handle.Capture = true;
    }

    private void MiniMapResizeHandle_MouseMove(object sender, MouseEventArgs e) {
      if (!miniMapResizing || miniMapPanel == null || splitContainer3?.Panel1 == null) return;
      Point now = Cursor.Position;
      int dx = now.X - miniMapDragStart.X;
      int dy = now.Y - miniMapDragStart.Y;
      Rectangle start = new Rectangle(miniMapResizeLocationStart, miniMapResizeStart);
      Rectangle requested = start;

      if ((miniMapResizeEdges & MiniMapResizeEdges.Left) != 0) { requested.X = start.X + dx; requested.Width = start.Width - dx; }
      if ((miniMapResizeEdges & MiniMapResizeEdges.Right) != 0) requested.Width = start.Width + dx;
      if ((miniMapResizeEdges & MiniMapResizeEdges.Top) != 0) { requested.Y = start.Y + dy; requested.Height = start.Height - dy; }
      if ((miniMapResizeEdges & MiniMapResizeEdges.Bottom) != 0) requested.Height = start.Height + dy;

      var host = splitContainer3.Panel1;
      int hostTop = (worldToolbar?.Bottom ?? 0) + 6;
      const int margin = 4;
      const int minWidth = 160;
      int minHeight = Math.Max(120, (miniMapTitleBar?.Height ?? 25) + 60);

      // Clamp the moving side first so dragging the left/top edge keeps the opposite edge stationary whenever possible.
      if ((miniMapResizeEdges & MiniMapResizeEdges.Left) != 0) {
        int right = requested.Right;
        requested.X = Math.Max(margin, Math.Min(requested.X, right - minWidth));
        requested.Width = right - requested.X;
      } else {
        requested.Width = Math.Max(minWidth, requested.Width);
      }
      if ((miniMapResizeEdges & MiniMapResizeEdges.Top) != 0) {
        int bottom = requested.Bottom;
        requested.Y = Math.Max(hostTop, Math.Min(requested.Y, bottom - minHeight));
        requested.Height = bottom - requested.Y;
      } else {
        requested.Height = Math.Max(minHeight, requested.Height);
      }

      int maxRight = Math.Max(margin + minWidth, host.ClientSize.Width - margin);
      int maxBottom = Math.Max(hostTop + minHeight, host.ClientSize.Height - margin);
      if (requested.Right > maxRight) {
        if ((miniMapResizeEdges & MiniMapResizeEdges.Left) != 0 && (miniMapResizeEdges & MiniMapResizeEdges.Right) == 0)
          requested.X = Math.Max(margin, maxRight - requested.Width);
        else requested.Width = Math.Max(minWidth, maxRight - requested.X);
      }
      if (requested.Bottom > maxBottom) {
        if ((miniMapResizeEdges & MiniMapResizeEdges.Top) != 0 && (miniMapResizeEdges & MiniMapResizeEdges.Bottom) == 0)
          requested.Y = Math.Max(hostTop, maxBottom - requested.Height);
        else requested.Height = Math.Max(minHeight, maxBottom - requested.Y);
      }

      requested.Width = Math.Min(requested.Width, Math.Max(minWidth, maxRight - requested.X));
      requested.Height = Math.Min(requested.Height, Math.Max(minHeight, maxBottom - requested.Y));
      miniMapPanel.Bounds = requested;
      miniMapUserResized = true;
      PositionMiniMapResizeGrip();
      miniMapPicture?.Invalidate();
    }

    private void MiniMapResizeHandle_MouseUp(object sender, MouseEventArgs e) {
      if (sender is Control handle) handle.Capture = false;
      miniMapResizing = false;
      miniMapResizeEdges = MiniMapResizeEdges.None;
      LayoutMiniMapWithinHost(false);
    }

    // Keep the historical method names as tiny wrappers so any designer/event hookup from an older workspace still
    // behaves correctly. The new handles use the generic edge-aware handlers above.
    private void MiniMapResizeGrip_MouseDown(object sender, MouseEventArgs e) => MiniMapResizeHandle_MouseDown(sender, e);
    private void MiniMapResizeGrip_MouseMove(object sender, MouseEventArgs e) => MiniMapResizeHandle_MouseMove(sender, e);

    private void PositionMiniMapResizeGrip() {
      if (miniMapPanel == null || miniMapResizeHandles.Count == 0) return;
      const int edge = 5;
      const int corner = 12;
      int width = miniMapPanel.ClientSize.Width;
      int height = miniMapPanel.ClientSize.Height;
      foreach (Panel handle in miniMapResizeHandles) {
        if (handle?.Tag is not MiniMapResizeEdges edges) continue;
        bool left = (edges & MiniMapResizeEdges.Left) != 0;
        bool right = (edges & MiniMapResizeEdges.Right) != 0;
        bool top = (edges & MiniMapResizeEdges.Top) != 0;
        bool bottom = (edges & MiniMapResizeEdges.Bottom) != 0;
        bool cornerHandle = (left || right) && (top || bottom);
        if (cornerHandle) {
          handle.Bounds = new Rectangle(left ? 0 : Math.Max(0, width - corner), top ? 0 : Math.Max(0, height - corner), corner, corner);
        } else if (left || right) {
          handle.Bounds = new Rectangle(left ? 0 : Math.Max(0, width - edge), corner, edge, Math.Max(1, height - corner * 2));
        } else {
          handle.Bounds = new Rectangle(corner, top ? 0 : Math.Max(0, height - edge), Math.Max(1, width - corner * 2), edge);
        }
      }
      // Edge strips first, corners last so a corner always wins hit-testing where the handles overlap.
      foreach (Panel handle in miniMapResizeHandles) {
        if (handle?.Tag is MiniMapResizeEdges edges && (((edges & MiniMapResizeEdges.Left) != 0 || (edges & MiniMapResizeEdges.Right) != 0) ^ ((edges & MiniMapResizeEdges.Top) != 0 || (edges & MiniMapResizeEdges.Bottom) != 0)))
          handle.BringToFront();
      }
      foreach (Panel handle in miniMapResizeHandles) {
        if (handle?.Tag is MiniMapResizeEdges edges && ((edges & (MiniMapResizeEdges.Left | MiniMapResizeEdges.Right)) != 0) && ((edges & (MiniMapResizeEdges.Top | MiniMapResizeEdges.Bottom)) != 0))
          handle.BringToFront();
      }
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
          ApplyPendingMapLinkAfterAreaLoad();
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

    private static List<float> WorldFloatVector(object raw) {
      var result = new List<float>();
      foreach (object value in WorldInteractionListEntries(raw)) {
        try { result.Add(Convert.ToSingle(value, System.Globalization.CultureInfo.InvariantCulture)); }
        catch { }
      }
      return result;
    }

    private void LoadWorldMapPages(FileFormats.Area targetArea, ulong areaId) {
      if (targetArea == null || currentDom == null) return;
      try {
        // In RED/HE32 the prototype is often present by id but absent from the GOM name lookup.  Resolve the
        // deterministic FQN fold and read fields by both modern name and numeric field id.
        GomObject mapDataObj = WorldResolveGomObject("world.areas." + areaId + ".mapdata");
        if (mapDataObj == null) return;
        object rawPages = WorldInteractionDataValue(mapDataObj.Data, "mapDataContainerMapDataList", "4611686042955270002");
        List<object> pages = WorldInteractionListEntries(rawPages);
        if (pages.Count == 0) return;

        foreach (object rawPage in pages) {
          GomObjectData page = WorldInteractionObjectData(rawPage);
          if (page == null) continue;
          string mapName = WorldInteractionText(WorldInteractionDataValue(page, "mapName", "4611686020073980011"));
          if (String.IsNullOrWhiteSpace(mapName)) continue;
          List<float> min = WorldFloatVector(WorldInteractionDataValue(page, "mapPageMinCoord", "4611686020073980015"));
          List<float> max = WorldFloatVector(WorldInteractionDataValue(page, "mapPageMaxCoord", "4611686020073980016"));
          if (min.Count < 3 || max.Count < 3) continue;
          List<float> miniMin = WorldFloatVector(WorldInteractionDataValue(page, "mapPageMiniMinCoord", "4611686035821970007"));
          List<float> miniMax = WorldFloatVector(WorldInteractionDataValue(page, "mapPageMiniMaxCoord", "4611686035821970006"));

          // Prefer the directory from which area.dat was actually loaded. This automatically covers every
          // discovered systemgenerated world instead of hard-coding the two IDs known to older Jedipedia builds.
          // Keep the alternate location and the pre-release unsuffixed DDS as fallbacks for legacy clients.
          bool retailSystemGenerated = IsSystemGeneratedWorldPath(targetArea.Path);
          string preferredPrefix = retailSystemGenerated ? "livecontent/systemgenerated" : "areas";
          string alternatePrefix = retailSystemGenerated ? "areas" : "livecontent/systemgenerated";
          string[] imageCandidates = {
            "/resources/world/" + preferredPrefix + "/" + areaId + "/" + mapName + "_r.dds",
            "/resources/world/" + alternatePrefix + "/" + areaId + "/" + mapName + "_r.dds",
            "/resources/world/" + preferredPrefix + "/" + areaId + "/" + mapName + ".dds",
            "/resources/world/" + alternatePrefix + "/" + areaId + "/" + mapName + ".dds"
          };
          string image = imageCandidates[0];
          bool hasImage = false;
          foreach (string candidate in imageCandidates) {
            using File imageFile = currentAssets.FindFile(candidate);
            if (imageFile == null) continue;
            image = candidate;
            hasImage = true;
            break;
          }

          long pageGuid = WonkInt64(WorldInteractionDataValue(page, "mapPageGUID", "4611686020668180062"));
          string pageDisplayName = null;
          try {
            StringTable worldMapStrings = currentDom.StringTable.Find("str.sys.worldmap");
            string localized = worldMapStrings?.GetText(pageGuid, "MapPage." + mapName);
            if (!String.IsNullOrWhiteSpace(localized)) pageDisplayName = localized.Trim();
          } catch { }

          targetArea.MapPages.Add(new AreaMapPage {
            Guid = pageGuid,
            SId = WonkInt64(WorldInteractionDataValue(page, "mapNameSId", "4611686141823655043")),
            ParentId = WonkInt64(WorldInteractionDataValue(page, "mapParentNameSId", "4611686141823655046")),
            MapName = mapName,
            DisplayName = pageDisplayName,
            ImagePath = image,
            HasImage = hasImage,
            Min = new SlimDX.Vector3(min[0], min[1], min[2]),
            Max = new SlimDX.Vector3(max[0], max[1], max[2]),
            MiniMapMin = miniMin.Count >= 3 ? new SlimDX.Vector2(miniMin[0], miniMin[2]) : new SlimDX.Vector2(min[0], min[2]),
            MiniMapMax = miniMax.Count >= 3 ? new SlimDX.Vector2(miniMax[0], miniMax[2]) : new SlimDX.Vector2(max[0], max[2])
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
            // Design-blockout/editor assets are part of the authoring view.  They still stay out of the normal
            // geometry pass, but must be admitted to the streaming catalogue while authoring helpers are enabled;
            // their EditorOnly/Hidden materials remain gated by View_AREA.IsMaterialHiddenFromWorld.  Previously
            // these assets were discarded before the renderer ever saw them, which is why the large Act/levels/
            // faction/Quest Start boards could never appear no matter which utility visibility flags were enabled.
            if (worldSettings?.ShowUtilityOther == true) break;
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

    private void EnrichWorldMapNotes() {
      if (area?.MapNotes == null || area.MapNotes.Count == 0 || currentDom == null) return;
      foreach (AreaMapNote note in area.MapNotes) {
        if (note == null || String.IsNullOrWhiteSpace(note.Fqn)) continue;
        GomLib.Models.MapNote resolved = null;
        GomObject rawNode = null;
        string fqn = note.Fqn.Trim().Trim('.');
        foreach (string candidate in new[] {
          fqn,
          fqn.StartsWith("mpn.", StringComparison.OrdinalIgnoreCase) ? fqn : "mpn." + fqn
        }.Distinct(StringComparer.OrdinalIgnoreCase)) {
          try {
            resolved = currentDom.MapNoteLoader?.Load(candidate);
            if (resolved != null) break;
          } catch { }
          try {
            rawNode = WorldResolveGomObject(candidate);
            if (rawNode != null) {
              resolved = currentDom.MapNoteLoader?.Load(rawNode);
              if (resolved != null) break;
            }
          } catch { }
        }

        if (rawNode == null) {
          string candidate = fqn.StartsWith("mpn.", StringComparison.OrdinalIgnoreCase) ? fqn : "mpn." + fqn;
          rawNode = WorldResolveGomObject(candidate);
        }

        if (resolved != null) {
          if (!String.IsNullOrWhiteSpace(resolved.Icon)) note.Icon = resolved.Icon.Trim();
          if (!String.IsNullOrWhiteSpace(resolved.Name)) note.Label = resolved.Name.Trim();
          note.LocalizedName = resolved.LocalizedName == null ? null : new Dictionary<string, string>(resolved.LocalizedName, StringComparer.OrdinalIgnoreCase);
          note.Condition = resolved.Condition.ToString();
          note.WonkaPackageId = resolved.WonkaPackageId;
          note.WonkaDestinationId = resolved.WonkaDestinationId;
          note.AssetId = resolved.AssetID;
          if (resolved.MapLink != null) {
            note.MapLinkAreaId = resolved.MapLink.AreaId;
            note.MapLinkMapNameSId = resolved.MapLink.MapNameSId;
            note.MapLinkSubmapNameSId = resolved.MapLink.SubmapNameSId;
          }
          EnrichWorldMapNoteQuestInfo(note, resolved);
        }

        // MapNoteLoader was written around the modern class shape. Keep a raw numeric-field pass behind it only for
        // detected pre-64-bit content so the established Retail/64-bit enrichment path stays byte-for-byte in control.
        if (WorldUsesLegacyContent && rawNode != null) EnrichWorldMapNoteLegacyRaw(note, rawNode);
      }
    }

    private void EnrichWorldMapNoteLegacyRaw(AreaMapNote note, GomObject node) {
      if (note == null || node?.Data == null) return;
      try {
        string icon = WorldInteractionText(WorldInteractionDataValue(node.Data, "mpnIconAsset", "4611686041936871466"));
        if (!String.IsNullOrWhiteSpace(icon)) note.Icon = icon.Trim();
        long assetId = WonkInt64(WorldInteractionDataValue(node.Data, "mpnAssetID", "4611686058671931193"));
        if (assetId != 0) note.AssetId = assetId;
        long packageId = WonkInt64(WorldInteractionDataValue(node.Data, "mpnMetadataInt", "4611686226320720002"));
        if (packageId != 0) note.WonkaPackageId = packageId;
        ulong destinationId = WonkUInt64(WorldInteractionDataValue(node.Data, "mpnMetadataID", "4611686226320720003"));
        if (destinationId != 0) note.WonkaDestinationId = destinationId;
        object condition = WorldInteractionDataValue(node.Data, "mpnConditionEType", "4611686078179865199");
        if (condition != null && String.IsNullOrWhiteSpace(note.Condition)) note.Condition = condition.ToString();

        object rawMapLink = WorldInteractionDataValue(node.Data, "mpnMapLink", "4611686142517280007");
        GomObjectData mapLink = WorldInteractionObjectData(rawMapLink);
        if (mapLink != null) {
          ulong linkedArea = WonkUInt64(WorldInteractionDataValue(mapLink, "mapLinkAreaId", "4611686142517280003"));
          long linkedMap = WonkInt64(WorldInteractionDataValue(mapLink, "mapLinkMapNameSId", "4611686142517280004"));
          long linkedSubmap = WonkInt64(WorldInteractionDataValue(mapLink, "mapLinkSubmapNameSId", "4611686142517280005"));
          if (linkedArea != 0) note.MapLinkAreaId = linkedArea;
          if (linkedMap != 0) note.MapLinkMapNameSId = linkedMap;
          if (linkedSubmap != 0) note.MapLinkSubmapNameSId = linkedSubmap;
        }

        object locMap = WorldInteractionDataValue(node.Data, "locTextRetrieverMap", "4611686102842470023");
        if (String.IsNullOrWhiteSpace(note.Label)) {
          string localized = WorldLocMapText(locMap, WorldLocNameSlot, node.Name ?? note.Fqn);
          if (!String.IsNullOrWhiteSpace(localized)) note.Label = localized.Trim();
        }
      } catch (Exception ex) {
        System.Diagnostics.Debug.WriteLine("Legacy map-note enrichment failed (" + (note.Fqn ?? "?") + "): " + ex.Message);
      }
    }

    private async Task PreviewAREA(HashFileInfo info, ulong areaId) {
      ShowWorldLoading("Loading world…", "Reading area.dat and phase metadata…");
      // If authoring helpers are active during this load, ShouldIgnoreAreaModel admits DBO/editor models into the
      // streamer catalogue.  Remember that fact so toggling other utility checkboxes does not repeatedly reload.
      worldAuthoringGeometryLoaded = worldSettings.ShowUtilityOther;
      try {
        ResetWorldSpaceCombatEncounterCache();
        area = new FileFormats.Area(info, currentAssets, areaId);
        area.Read();
        UpdateWorldLoading("Loading map pages and phase metadata…");
        LoadWorldMapPages(area, areaId);
        EnrichWorldMapNotes();
        worldImplicitPhaseName = ResolveWorldImplicitPhaseName(areaId);

        int decorationHookPlacements = area.RoomList.Sum(room => room.InstancesById.Values.Count(instance => instance.HasDecorationHook));
        SetDecorationHookAvailability(IsKnownStrongholdArea(areaId), decorationHookPlacements);

        List<AreaAsset> gr2Assets = area.AssetsByExtension.ContainsKey("gr2")
          ? area.AssetsByExtension["gr2"]
          : new List<AreaAsset>();
        List<AreaAsset> speedTreeAssets = area.AssetsByExtension.ContainsKey("spt")
          ? area.AssetsByExtension["spt"]
          : new List<AreaAsset>();
        // A large share of shipped .spt placements has a same-stem .gr2 companion. Jedipedia decodes the raw
        // SpeedTree file in WASM; PugTools can already render Granny robustly, so use the companion as a static
        // fallback until the native .spt decoder/wind path is ported. Do NOT use .spt.gr2 here: those are the
        // SpeedTree collision/proxy companions and would visibly render collision geometry.
        var renderAssetById = new Dictionary<ulong, AreaAsset>();
        foreach (AreaAsset asset in gr2Assets) if (asset != null) renderAssetById[asset.Id] = asset;
        foreach (AreaAsset asset in speedTreeAssets) if (asset != null && !renderAssetById.ContainsKey(asset.Id)) renderAssetById[asset.Id] = asset;

        int totalInstances = 0, matchedAssets = 0, ignoredGr2Assets = 0, missingGr2Files = 0, failedGr2Loads = 0;
        int speedTreeLoaded = 0, speedTreeMissingFallback = 0;
        var matchedSpeedTreeAssets = new HashSet<ulong>();
        var attemptedRenderAssets = new HashSet<ulong>();
        var modelLoadRequests = new List<WorldModelStreamRequest>();
        int roomIndex = 0;
        foreach (FileFormats.Room room in area.RoomList) {
          roomIndex++;
          UpdateWorldLoading("Indexing room geometry " + roomIndex + " / " + Math.Max(1, area.RoomList.Count) + "…", roomIndex, Math.Max(1, area.RoomList.Count));
          foreach (KeyValuePair<ulong, List<FileFormats.AssetInstance>> kvp in room.InstancesByAssetId) {
            totalInstances += kvp.Value?.Count ?? 0;
            if (!renderAssetById.TryGetValue(kvp.Key, out AreaAsset asset) || asset == null) continue;
            matchedAssets++;
            bool isSpeedTree = String.Equals((asset.Extension ?? String.Empty).Trim().TrimStart('.'), "spt", StringComparison.OrdinalIgnoreCase);
            if (isSpeedTree) matchedSpeedTreeAssets.Add(asset.Id);
            if (ShouldIgnoreAreaModel(asset)) { ignoredGr2Assets++; continue; }
            // Match Jedipedia's area asset queue rather than dropping an asset merely because every placement is
            // authored hidden. Hidden/viewability is a draw-time decision in View_AREA, and phase/editor-authored
            // rooms can otherwise end up with an incomplete streaming catalog and a permanently missing shell.
            if (models.ContainsKey(asset.Id) || kvp.Value == null || !kvp.Value.Any(instance => instance != null)) continue;
            // v6 keeps only this tiny asset catalog at area-load time. Actual TOR reads and GR2 parsing happen when
            // the room enters the camera working set, rather than decoding every normal world model up front.
            if (!attemptedRenderAssets.Add(asset.Id)) continue;
            string modelPath = "/resources/" + asset.Path.Replace("\\", "/") + ".gr2";
            File modelFile = currentAssets.FindFile(modelPath);
            if (modelFile == null) {
              if (isSpeedTree) speedTreeMissingFallback++; else missingGr2Files++;
              continue;
            }
            modelLoadRequests.Add(new WorldModelStreamRequest(asset.Id, asset.Path, isSpeedTree));
          }
        }
        int speedTreeMatched = matchedSpeedTreeAssets.Count;
        UpdateWorldLoading(string.Format(System.Globalization.CultureInfo.InvariantCulture,
          "Indexed: {0} rooms • {1:n0} placements • {2:n0} / {3:n0} world models scheduled",
          roomIndex,totalInstances,modelLoadRequests.Count,renderAssetById.Count),roomIndex,Math.Max(1,area.RoomList.Count));

        // Four workers keep the visible-room queue fed while render-thread uploads remain bounded. The old
        // two-worker setup was a principal reason a Corellia block assembled for 20+ seconds.
        int streamWorkers = Math.Max(1, Math.Min(6, Environment.ProcessorCount > 1 ? Environment.ProcessorCount - 1 : 1));
        var modelStreamer = new WorldModelStreamer(area, streamWorkers);

        UpdateWorldLoading("Loading utilities, NPCs, SPN objects and animations…");
        LoadWorldUtilityModels();
        LoadWorldNpcPlacements();
        BuildWorldServiceMapNotes();
        LoadWorldTaxiRoutes();
        PreloadWorldSpaceCombatEncounterAssets();

        SetStatusLabel(string.Format(
          "Rooms:{0} GR2Assets:{1} SPTAssets:{2} Instanzen:{3} zugeordnet:{4} ignoriert:{5} vorgemerkt:{6} GR2fehlt:{7} GR2Fehler:{8} | SpeedTree fallback:{9}/{10} (ohne GR2:{11}) | Utilities:{12} NPCs:{13} SPN:{14} | {15}",
          area.RoomList.Count, gr2Assets.Count, speedTreeAssets.Count, totalInstances, matchedAssets, ignoredGr2Assets,
          modelLoadRequests.Count, missingGr2Files, failedGr2Loads, speedTreeLoaded, speedTreeMatched, speedTreeMissingFallback,
          worldUtilityModels.Count, worldNpcPlacements.Count, worldSpnPlacements.Count, area.DebugHeaderInfo));

        UpdateWorldLoading("Starting visible-room streaming…",0,Math.Max(1,modelLoadRequests.Count));
        panelRender.SetSettings(worldSettings);
        panelRender.SetImplicitPhaseName(worldImplicitPhaseName);
        panelRender.LoadModel(models, materials, area.RoomList, area.Id.ToString(), area, worldUtilityModels, worldNpcPlacements, worldSpnPlacements, modelStreamer, modelLoadRequests);
        RebuildMapnoteNavigator();
        // Put the local first-frame gate in front before the D3D thread can present an empty spawn frame.
        BeginWorldStreamingLoading(modelLoadRequests.Count);
        render = new Thread(panelRender.StartRender) { IsBackground = true };
        render.Start();
        if (miniMapPanel != null && miniMapPanel.Visible) panelRender.RequestMiniMapSnapshot();
      } finally {
        if(!worldStreamingLoading)HideWorldLoading();
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
      foreach (Image image in worldMapIconImages.Values) image?.Dispose();
      worldMapIconImages.Clear();
      if (worldSelectionInfoForm != null && !worldSelectionInfoForm.IsDisposed) worldSelectionInfoForm.Dispose();
      worldSelectionInfoForm = null;
      if (worldConversationPreviewForm != null && !worldConversationPreviewForm.IsDisposed) worldConversationPreviewForm.Dispose();
      worldConversationPreviewForm = null;
      StopWorldConversationPlayback(true);
      DisposeWorldConversationAudioCache();
      if (worldConversationPlaybackForm != null && !worldConversationPlaybackForm.IsDisposed) worldConversationPlaybackForm.Dispose();
      worldConversationPlaybackForm = null;
      if (worldCodexPreviewForm != null && !worldCodexPreviewForm.IsDisposed) worldCodexPreviewForm.Dispose();
      worldCodexPreviewForm = null;

      View_AREA renderer = panelRender;
      Thread renderThread = render;
      panelRender = null;
      render = null;

      void CleanupWorldRenderResources() {
        try { renderer?.Clear(); } catch { }
        try { renderer?.Dispose(); } catch { }
        try { materials.Clear(); } catch { }
        try { models.Clear(); } catch { }
        try { assetDict.Clear(); } catch { }
        try { nodeKeys.Clear(); } catch { }
      }

      if (renderer == null) {
        CleanupWorldRenderResources();
        return;
      }

      try { renderer.StopRender(); } catch { }
      Boolean stopped = renderThread == null || !renderThread.IsAlive;
      if (!stopped) {
        try { stopped = renderThread.Join(750); } catch { }
      }

      if (stopped) {
        CleanupWorldRenderResources();
      } else {
        // A slow D3D shutdown must not freeze every open PugTools browser. Finish cleanup after
        // the render thread has actually exited instead of blocking the WinForms message loop.
        ThreadPool.QueueUserWorkItem(_ => {
          try { renderThread.Join(); } catch { }
          CleanupWorldRenderResources();
        });
      }
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
