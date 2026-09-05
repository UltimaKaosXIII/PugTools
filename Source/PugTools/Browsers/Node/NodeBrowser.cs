using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml;
using System.Xml.Linq;

using BrightIdeasSoftware;

using GomLib;
using TorArchive;

namespace PugTools {
  internal partial class NodeBrowser : Form {
    #region Fields
    private Dictionary<String, NodeAsset> _assetDict;
    private readonly String _assetsLocation;
    private readonly Boolean _assetsUsePts;
    private readonly String _previousAssetsLocation;
    private readonly Boolean _previousAssetsUsePts;
    private readonly Boolean _compareNodes;
    private Boolean _buildCsv;
    private Boolean _closing;
    private Boolean _collapsed;
    private Assets _currentAssets;
    private DataObjectModel _currentDom;
    private Assets _previousAssets;
    private DataObjectModel _previousDom;
    private String[] _current; // 0 = Item, 1 = Node, 2 = Parent.
    private String _currentTreeNode;
    private Dictionary<String, String> _customNodeSort;
    private HashSet<String> _customRoots;
    private readonly Boolean _customSort;
    private DataTable _dataTable;
    private Boolean _filter;
    private Dictionary<String, DomType> _nodeDict;
    private Dictionary<String, DomType> _previousNodeDict;
    private TreeNode[] _nodeMatch;
    private HashSet<String> _outputData;
    private List<NodeOutput> _outputList;
    private ArrayList _rootList;
    private Int32 _searchIndex;
    private List<String> _searchNodes;
    private System.Windows.Forms.Timer _nodeTreeFilterTimer;
    private CancellationTokenSource _nodeTreeFilterCancellation;
    private Int32 _nodeTreeFilterGeneration;
    private const Int32 NodeTreeFilterDisplayLimit = 2500;
    private const Int32 NodeTreeFilterNodeLimit = 8000;
    private const Int32 NodeTreeFilterAutoExpandLimit = 100;
    private NodeSearchEntry[] _nodeSearchIndex = Array.Empty<NodeSearchEntry>();
    private TreeViewFast.Controls.TreeViewFast.PreparedTree _fullNodeTree;
    private TreeViewFast.Controls.TreeViewFast _nodeFilterTree;
    #endregion

    #region NodeBrowser
    internal NodeBrowser(String assetLocation, Boolean usePTS, String extractLocation,
                         String previousAssetLocation = null, Boolean previousUsePTS = false,
                         Boolean compareNodes = false) {
      if (extractLocation == null) throw new ArgumentNullException(nameof(extractLocation));

      InitializeComponent();
      InitializeNodeFilterTreeView();
      InitializeNodeTreeLiveFilter();
      InitializeNodePreviewUi();
      Config.Load();

      _assetsLocation = assetLocation;
      _assetsUsePts = usePTS;
      _previousAssetsLocation = previousAssetLocation;
      _previousAssetsUsePts = previousUsePTS;
      _compareNodes = compareNodes && !String.IsNullOrWhiteSpace(previousAssetLocation);
      InitializeNodeFieldCompare();

      using System.IO.StringReader stringReader =
        new System.IO.StringReader(Properties.Resources.CustomNodeSorting);
      XmlDocument xmlDoc = new XmlDocument();
      xmlDoc.Load(stringReader);

      _customRoots = new HashSet<String>();
      _customNodeSort = new Dictionary<String, String>();
      _customSort = Convert.ToBoolean(
        xmlDoc.SelectSingleNode("/custom_sort").Attributes["enabled"].Value
      );
      txtExtractPath.Text = Config.ExtractAssetsPath;

      foreach (XmlNode node in xmlDoc.SelectNodes("/custom_sort/roots/root")) {
        _customRoots.Add(node.Attributes["name"].Value);
      }

      foreach (XmlNode node in xmlDoc.SelectNodes("/custom_sort/nodes/node")) {
        _customNodeSort.Add(node.Attributes["name"].Value, node.Attributes["parent"].Value);
      }

      StatusLabel1Text("Loading Assets ...");
      LoadingSwirlShow();
      ProgressBarShow();

      backgroundWorker1.RunWorkerAsync();

      treeViewGrid1.CanExpandGetter = delegate (Object x) {
        return ((NodeListItem)x).children.Count > 0;
      };

      treeViewGrid1.ChildrenGetter = delegate (Object x) {
        NodeListItem obj = (NodeListItem)x;
        ArrayList children = new ArrayList();

        foreach (NodeListItem child in obj.children) {
          if (child.DisplayName.Contains("Script_")) continue;

          children.Add(child);
        }

        return children;
      };

      // Jedipedia-style navigation: a node reference in the raw value table behaves like a link.
      // The existing right-click “Go to Node” remains available; double-click is the fast path.
      treeViewGrid1.MouseDoubleClick += TreeViewGrid1MouseDoubleClickNavigate;
    }
    private void NodeBrowserFormClosed(Object sender, FormClosedEventArgs e) {
      try { _nodeTreeFilterTimer?.Stop(); _nodeTreeFilterTimer?.Dispose(); } catch { }
      _nodeTreeFilterTimer = null;
      try { _nodeTreeFilterCancellation?.Cancel(); _nodeTreeFilterCancellation?.Dispose(); } catch { }
      _nodeTreeFilterCancellation = null;
      Hide();
      DisposeNodePreview();

      if (_nodeFilterTree != null) {
        try { _nodeFilterTree.Dispose(); } catch { }
        _nodeFilterTree = null;
      }

      if (treeViewFast1 != null) {
        treeViewFast1.Dispose();
        treeViewFast1 = null;
      }

      if (treeViewGrid1 != null) {
        treeViewGrid1.Dispose();
        treeViewGrid1 = null;
      }

      if (dataGridView1 != null) {
        dataGridView1.Dispose();
        dataGridView1 = null;
      }

      _assetDict = null;
      _currentAssets = null;
      _currentDom = null;
      _previousAssets = null;
      _previousDom = null;
      _previousNodeDict = null;
      _customNodeSort = null;
      _customRoots = null;
      _dataTable = null;
      _nodeDict = null;
      _outputData = null;
      _outputList = null;
      _rootList = null;
      _searchNodes = null;
      if (_fieldDiffForm != null && !_fieldDiffForm.IsDisposed) _fieldDiffForm.Dispose();
      _fieldDiffForm = null;

      Dispose(true);

      System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.Interactive;
    }
    private void NodeBrowserFormClosing(Object sender, FormClosingEventArgs e) {
      System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.LowLatency;
      _closing = true;
    }
    private void NodeBrowserFormResize(Object sender, EventArgs e) {
      var treeSize =
        new System.Drawing.Size(splitContainer2.Panel1.Width, splitContainer2.Panel1.Height - 70);
      if (treeViewFast1 != null) treeViewFast1.Size = treeSize;
      if (_nodeFilterTree != null) _nodeFilterTree.Size = treeSize;
      ResizeNodePreviewLayout();
    }
    #endregion

    #region Background Workers
    private void BackgroundWorker1Run(Object sender, DoWorkEventArgs e) {
      if (_closing) return;

      System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.LowLatency;

      _currentAssets = AssetHandler.Instance.GetCurrentAssets(_assetsLocation, _assetsUsePts);
      LocalizationResolver.Apply(_currentAssets, Config.Language);
      _currentDom = DomHandler.Instance.GetCurrentDOM(_currentAssets);

      if (_compareNodes) {
        _previousAssets =
          AssetHandler.Instance.GetPreviousAssets(_previousAssetsLocation, _previousAssetsUsePts);
        _previousDom = DomHandler.Instance.GetPreviousDOM(_previousAssets);
      }
    }
    private void BackgroundWorker1Completed(Object sender, RunWorkerCompletedEventArgs e) {
      if (_closing) return;

      if (e.Error != null) {
        throw new Exception("Echter Fehler beim Laden: " + e.Error, e.Error);
      }

      _assetDict = new Dictionary<String, NodeAsset> {
        { "/", new NodeAsset("/", "", "Root", null) }
      };

      _currentDom.NodeLookup.TryGetValue(typeof(GomObject), out _nodeDict);
      if (_compareNodes && _previousDom != null)
        _previousDom.NodeLookup.TryGetValue(typeof(GomObject), out _previousNodeDict);

      ProgressBarStyle(System.Windows.Forms.ProgressBarStyle.Continuous);
      StatusLabel1Text("Loading Nodes ...");

      backgroundWorker2.RunWorkerAsync();
    }
    private void BackgroundWorker2Progress(Object sender, ProgressChangedEventArgs e) {
      ProgressBarValue(e.ProgressPercentage);
    }
    private void BackgroundWorker2Run(Object sender, DoWorkEventArgs e) {
      if (_closing) return;

      if (_compareNodes && _previousNodeDict != null) {
        BuildCompareNodeTree();
        return;
      }

      HashSet<String> allDirs = new HashSet<String>();
      HashSet<String> nodeDirs = new HashSet<String>();

      if (_customSort)
        if (_customRoots.Count > 0)
          foreach (String customRoot in _customRoots) {
            _assetDict.Add(customRoot, new NodeAsset(customRoot, "/", customRoot, null));
            nodeDirs.Add(customRoot);
          }

      if (_nodeDict != null) {
        Int32 nodesDone = 0;
        Int32 nodesTotal = _nodeDict.Count;

        foreach (KeyValuePair<String, DomType> node in _nodeDict) {
          GomObject obj = (GomObject)node.Value;
          String display = node.Key;
          String parent;

          if (obj.Name.Contains(".")) {
            String[] temp = obj.Name.Split('.');
            parent = String.Join(".", temp.Take(temp.Length - 1));

            if (_customSort)
              if (_customNodeSort.Count > 0)
                foreach (KeyValuePair<String, String> n in _customNodeSort)
                  if (obj.Name.StartsWith(n.Key)) {
                    String origParent = parent;
                    parent = n.Value + "." + parent;
                    display = display.Replace(origParent, "").Replace(".", "");

                  } else {
                    display = display.Replace(parent, "").Replace(".", "");
                  }

            nodeDirs.Add(parent);
          } else {
            parent = "/";

            if (_customSort)
              if (_customNodeSort.Count > 0)
                foreach (KeyValuePair<String, String> n in _customNodeSort)
                  if (obj.Name.StartsWith(n.Key)) parent = n.Value;
          }

          NodeAsset asset = new NodeAsset(node.Key, parent, display, obj);

          _assetDict.Add(node.Key, asset);

          nodesDone++;
          backgroundWorker2.ReportProgress(nodesDone * 100 / nodesTotal);
        }

        foreach (String dir in nodeDirs) {
          String[] temp = dir.Split('.');
          Int32 intLength = temp.Length;

          for (Int32 intCount2 = 0; intCount2 <= intLength; intCount2++) {
            String output = String.Join(".", temp, 0, intCount2);

            if (!String.IsNullOrEmpty(output)) allDirs.Add(output);
          }
        }

        foreach (String dir in allDirs) {
          String[] temp = dir.Split('.');
          String parentDir = String.Join(".", temp.Take(temp.Length - 1));

          if (String.IsNullOrEmpty(parentDir)) parentDir = "/";

          String display = temp.Last();
          NodeAsset asset = new NodeAsset(dir, parentDir, display, null);

          if (!_assetDict.ContainsKey(dir)) _assetDict.Add(dir, asset);
        }
      }
    }
    private void BuildCompareNodeTree() {
      _assetDict = new Dictionary<String, NodeAsset>();

      const String rootId = "/root";
      const String newRoot = "/root/new";
      const String changedRoot = "/root/changed";
      const String removedRoot = "/root/removed";

      Int32 newCount = 0;
      Int32 changedCount = 0;
      Int32 removedCount = 0;
      HashSet<String> directoryIds = new HashSet<String>(StringComparer.Ordinal);

      HashSet<String> names = new HashSet<String>(StringComparer.Ordinal);
      if (_nodeDict != null) names.UnionWith(_nodeDict.Keys);
      if (_previousNodeDict != null) names.UnionWith(_previousNodeDict.Keys);

      Int32 done = 0;
      Int32 total = Math.Max(1, names.Count);
      foreach (String name in names.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)) {
        if (_closing) return;

        GomObject current = null;
        GomObject previous = null;
        if (_nodeDict != null && _nodeDict.TryGetValue(name, out DomType currentType))
          current = currentType as GomObject;
        if (_previousNodeDict != null && _previousNodeDict.TryGetValue(name, out DomType previousType))
          previous = previousType as GomObject;

        BuildFileState state;
        GomObject displayObject;
        String categoryRoot;
        if (current == null && previous != null) {
          state = BuildFileState.Removed;
          displayObject = previous;
          categoryRoot = removedRoot;
          removedCount++;
        } else if (current != null && previous == null) {
          state = BuildFileState.New;
          displayObject = current;
          categoryRoot = newRoot;
          newCount++;
        } else if (current != null && previous != null && NodeContentsDiffer(current, previous)) {
          state = BuildFileState.Changed;
          displayObject = current;
          categoryRoot = changedRoot;
          changedCount++;
        } else {
          done++;
          backgroundWorker2.ReportProgress(done * 100 / total);
          continue;
        }

        GetNodeTreeLocation(name, out String logicalParent, out String displayName);
        String parentId = logicalParent == "/"
          ? categoryRoot
          : categoryRoot + "/" + logicalParent;
        String itemId = categoryRoot + "/" + name;
        if (_assetDict.ContainsKey(itemId)) itemId += " [node]";

        NodeAsset asset = new NodeAsset(itemId, parentId, displayName, displayObject) {
          compareState = state
        };
        _assetDict.Add(itemId, asset);

        AddCompareDirectories(categoryRoot, logicalParent, directoryIds);
        done++;
        backgroundWorker2.ReportProgress(done * 100 / total);
      }

      _assetDict[rootId] = new NodeAsset(rootId, String.Empty, "Root", null);
      _assetDict[changedRoot] = new NodeAsset(
        changedRoot, rootId, "Changed Nodes (" + changedCount.ToString("N0") + ")", null
      );
      _assetDict[newRoot] = new NodeAsset(
        newRoot, rootId, "New Nodes (" + newCount.ToString("N0") + ")", null
      );
      _assetDict[removedRoot] = new NodeAsset(
        removedRoot, rootId, "Removed Nodes (" + removedCount.ToString("N0") + ")", null
      );

      foreach (String dirId in directoryIds.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)) {
        if (_assetDict.ContainsKey(dirId)) continue;

        Int32 slash = dirId.LastIndexOf('/');
        String categoryRoot = dirId.StartsWith(changedRoot + "/", StringComparison.OrdinalIgnoreCase)
          ? changedRoot
          : dirId.StartsWith(newRoot + "/", StringComparison.OrdinalIgnoreCase)
            ? newRoot
            : removedRoot;
        String logical = slash >= 0 ? dirId.Substring(slash + 1) : dirId;
        Int32 dot = logical.LastIndexOf('.');
        String display = dot >= 0 ? logical.Substring(dot + 1) : logical;
        String parentLogical = dot >= 0 ? logical.Substring(0, dot) : String.Empty;
        String parentId = String.IsNullOrEmpty(parentLogical)
          ? categoryRoot
          : categoryRoot + "/" + parentLogical;

        _assetDict.Add(dirId, new NodeAsset(dirId, parentId, display, null));
      }

      backgroundWorker2.ReportProgress(100);
    }

    private void AddCompareDirectories(String categoryRoot, String logicalParent,
                                       HashSet<String> directories) {
      if (String.IsNullOrEmpty(logicalParent) || logicalParent == "/") return;

      String[] parts = logicalParent.Split('.');
      for (Int32 i = 1; i <= parts.Length; i++) {
        String logical = String.Join(".", parts, 0, i);
        directories.Add(categoryRoot + "/" + logical);
      }
    }

    private void GetNodeTreeLocation(String name, out String parent, out String display) {
      parent = "/";
      display = name;

      if (name.Contains(".")) {
        String[] parts = name.Split('.');
        parent = String.Join(".", parts.Take(parts.Length - 1));
        display = parts.Last();
      }

      if (!_customSort || _customNodeSort == null || _customNodeSort.Count == 0) return;

      KeyValuePair<String, String> custom = _customNodeSort
        .FirstOrDefault(item => name.StartsWith(item.Key, StringComparison.Ordinal));
      if (!String.IsNullOrEmpty(custom.Key)) {
        if (parent == "/")
          parent = custom.Value;
        else
          parent = custom.Value + "." + parent;
      }
    }

    private static Boolean NodeContentsDiffer(GomObject current, GomObject previous) {
      if (current == null || previous == null) return true;
      if (current.Checksum != previous.Checksum) return true;
      if (current.ObjectSizeInFile != previous.ObjectSizeInFile
          || current.NumGlommed != previous.NumGlommed
          || current.InstanceType != previous.InstanceType
          || current.NumFields != previous.NumFields)
        return true;

      // Compressed nodes have an Adler32 checksum calculated from their stored data.
      // If it matches and the structural metadata matches, no decompression is needed.
      if (current.Checksum != 0) return false;

      try {
        return !current.GetRawUncompressedNode().SequenceEqual(previous.GetRawUncompressedNode());
      }
      catch {
        // A node that cannot be compared safely should be surfaced rather than hidden.
        return true;
      }
    }

    private void BackgroundWorker2Completed(Object sender, RunWorkerCompletedEventArgs e) {
      if (_closing) return;

      ProgressBarValue(0);
      ProgressBarStyle(System.Windows.Forms.ProgressBarStyle.Marquee);
      StatusLabel1Text("Loading Tree View Items ...");

      backgroundWorker3.RunWorkerAsync();
    }
    private void BackgroundWorker3Run(Object sender, DoWorkEventArgs e) {
      if (_closing) return;

      // Build a compact search array and the managed TreeNode hierarchy off the UI thread.
      // Only the final attachment to WinForms happens in BackgroundWorker3Completed.
      var searchEntries = new List<NodeSearchEntry>(_assetDict.Count);
      foreach (NodeAsset item in _assetDict.Values) {
        GomObject obj = item?.Obj;
        if (obj == null) continue;

        searchEntries.Add(new NodeSearchEntry(
          item.id,
          item.displayName,
          obj.Name,
          obj.Id.ToString(),
          obj.DomClass?.Id.ToString(),
          obj.NumGlommed
        ));
      }
      _nodeSearchIndex = searchEntries.ToArray();

      String getId(NodeAsset x) => x.id;
      String getParentId(NodeAsset x) => x.parentId;
      String getDisplayName(NodeAsset x) => x.displayName;
      Int32 getImageIndex(NodeAsset x) =>
        x != null && (x.Obj != null || x.dynObject != null || x.objData != null) ? 2 : 1;
      Int32 compare(NodeAsset x, NodeAsset y) {
        Boolean xLeaf = x != null && (x.Obj != null || x.dynObject != null || x.objData != null);
        Boolean yLeaf = y != null && (y.Obj != null || y.dynObject != null || y.objData != null);
        if (xLeaf != yLeaf) return xLeaf ? 1 : -1;
        return String.Compare(x?.id, y?.id, StringComparison.Ordinal);
      }

      _fullNodeTree = TreeViewFast.Controls.TreeViewFast.PrepareItems(
        _assetDict.Values, getId, getParentId, getDisplayName, getImageIndex, compare
      );
    }
    private void BackgroundWorker3Completed(Object sender, RunWorkerCompletedEventArgs e) {
      if (_closing) return;

      if (e.Error != null) {
        StatusLabel1Text("Unable to build node tree.");
        MessageBox.Show(e.Error.Message, "Node Browser", MessageBoxButtons.OK, MessageBoxIcon.Error);
        ProgressBarHide();
        LoadingSwirlHide();
        return;
      }

      treeViewFast1.BeginUpdate();
      try {
        if (_fullNodeTree != null) treeViewFast1.LoadPrepared(_fullNodeTree);
      } finally {
        treeViewFast1.EndUpdate();
      }
      TreeViewFast1Show();

      ProgressBarHide();
      StatusLabel1Text(
        _compareNodes
          ? "Comparison loaded. Showing New, Changed and Removed nodes only."
          : "Loading Complete."
      );
      ProgressBarValue(0);
      ProgressBarStyle(System.Windows.Forms.ProgressBarStyle.Continuous);

      System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.Interactive;

      LoadingSwirlHide();
      TreeViewGrid1Show();
      ButtonsEnable();

      // Expand root node
      if (treeViewFast1.Nodes.Count > 0) treeViewFast1.Nodes[0].Expand();

      txtSearch.Focus();
    }
    #endregion

    #region Buttons
    private void BtnClearSearchClick(Object sender, EventArgs e) {
      if (txtSearch == null) return;
      txtSearch.Text = String.Empty;
      ApplyNodeTreeLiveFilter();
      txtSearch.Focus();
    }
    private void BtnExtractClick(Object sender, EventArgs e) {
      try {
        LoadingSwirlShow();
        ProgressBarShow();
        StatusLabel1Text("Extracting Objects ...");
      }
      finally {
        NodeExtraction();
        StatusLabel1Text("Finished Extracting Objects.");
        ProgressBarHide();
        LoadingSwirlHide();
      }
    }
    private void BtnExtractPathClick(Object sender, EventArgs e) {
      FolderBrowserDialog fbd = new FolderBrowserDialog {
        SelectedPath = txtExtractPath.Text
      };

      _ = fbd.ShowDialog();

      txtExtractPath.Text = fbd.SelectedPath + "\\";
    }
    private async void BtnFileFinderClick(Object sender, EventArgs e) {
      DialogResult result = MessageBox.Show(
        "Run File Name Finder?",
        "Confirm File Name Finder",
        MessageBoxButtons.YesNo,
        MessageBoxIcon.Question
      );

      if (result == DialogResult.Yes) {
        DialogResult resultBuild = MessageBox.Show(
          "Build CSV File?",
          "Build CSV",
          MessageBoxButtons.YesNo,
          MessageBoxIcon.Question
        );

        if (resultBuild == DialogResult.Yes) {
          _buildCsv = true;
        } else {
          _buildCsv = false;
        }

        LoadingSwirlShow();
        ProgressBarStyle(System.Windows.Forms.ProgressBarStyle.Marquee);
        ProgressBarShow();
        StatusLabel1Text("Running File Name Finder ...");

        await Task.Run(() => NodeFindFilenames());

        StatusLabel1Text("File Name Finder Complete.");

        MessageBox.Show(
          "File Name Files Generated",
          "Files Generated",
          MessageBoxButtons.OK,
          MessageBoxIcon.Information
        );

        ProgressBarHide();
        ProgressBarStyle(System.Windows.Forms.ProgressBarStyle.Continuous);
        LoadingSwirlHide();
      }
    }
    private void BtnFindNextClick(Object sender, EventArgs e) {
      if (_searchNodes.ElementAtOrDefault(_searchIndex) != null) {
        _nodeMatch = treeViewFast1.Nodes.Find(_searchNodes[_searchIndex], true);
        treeViewFast1.SelectedNode = _nodeMatch[0];
        btnFindNext.Focus();
        StatusLabel1Text("Item " + (_searchIndex + 1) + " of " + _searchNodes.Count);
        _searchIndex++;
      } else {
        StatusLabel1Text("Search Complete.");
        MessageBox.Show("No more search terms found");
      }
    }
    private void BtnSearchClick(Object sender, EventArgs e) {
      Search();
    }
    private void BtnToggleNodesClick(Object sender, EventArgs e) {
      // Check if we can collapse or expand the selected node.
      if (treeViewGrid1.SelectedObject != null) {
        TreeListView.Branch br = treeViewGrid1.TreeModel.GetBranch(treeViewGrid1.SelectedObject);

        if (br != null && br.CanExpand && !br.IsExpanded) {
          // Expand node and all child nodes.
          TreeViewGrid1Hide();
          LoadingSwirlShow();
          ProgressBarShow();

          foreach (Object nodeObj in treeViewGrid1.GetChildren(treeViewGrid1.SelectedObject))
            treeViewGrid1.Expand(nodeObj);

          treeViewGrid1.Expand(treeViewGrid1.SelectedObject);

          btnToggleCollapse.Text = "Collapse Child Nodes";

          TreeViewGrid1Show();
          ProgressBarHide();
          LoadingSwirlHide();
          return;

        } else if (br != null && br.CanExpand && br.IsExpanded) {
          // Collapse node and all child nodes.
          TreeViewGrid1Hide();
          LoadingSwirlShow();
          ProgressBarShow();

          foreach (Object nodeObj in treeViewGrid1.GetChildren(treeViewGrid1.SelectedObject))
            treeViewGrid1.Collapse(nodeObj);

          treeViewGrid1.Collapse(treeViewGrid1.SelectedObject);

          btnToggleCollapse.Text = "Expand Child Nodes";

          TreeViewGrid1Show();
          ProgressBarHide();
          LoadingSwirlHide();
          return;
        }
      }

      // Couldn't collapse or expand child node. Do whole page.
      if (_collapsed) {
        _collapsed = false;
        treeViewGrid1.ExpandAll();
        btnToggleCollapse.Text = "Collapse Child Nodes";
      } else {
        _collapsed = true;
        treeViewGrid1.CollapseAll();
        btnToggleCollapse.Text = "Expand Child Nodes";
      }
    }
    private void ButtonsEnable() {
      txtSearch.Enabled = true;
      btnSearch.Enabled = true;
      btnExtractPath.Enabled = true;
      btnExtract.Enabled = true;
      btnFileFinder.Enabled = true;
    }
    #endregion

    #region LoadingSwirl
    private void LoadingSwirlHide() {
      if (loadingSwirl1.InvokeRequired) loadingSwirl1.Invoke(new Action(() => LoadingSwirlHide()));
      else loadingSwirl1.Visible = false;
    }
    private void LoadingSwirlShow() {
      if (loadingSwirl1.InvokeRequired) loadingSwirl1.Invoke(new Action(() => LoadingSwirlShow()));
      else loadingSwirl1.Visible = true;
    }
    #endregion

    #region Node Methods
    // private void NodeExtractByNode(TreeNodeCollection nodes) {
    //   foreach (TreeNode child in nodes) {
    //     TreeListItem asset = (TreeListItem)child.Tag;

    //     if (asset.HashInfo.File != null) {
    //       // extractAsset(asset.file);
    //     }

    //     if (child.Nodes.Count > 0)
    //       NodeExtractByNode(child.Nodes);
    //   }
    // }
    private void NodeExtraction() {
      TreeViewFast.Controls.TreeViewFast tree = ActiveNodeTree;
      if (tree == null) return;
      tree.Invoke(new Action(() => {
        TreeNode node = tree.SelectedNode;
        if (node == null) {
          MessageBox.Show("Please select a node before extracting.", "Node Browser",
                          MessageBoxButtons.OK, MessageBoxIcon.Warning);
          return;
        }
        String extractResult = NodeExtraction(node, false);

        if (String.IsNullOrEmpty(extractResult))
          extractResult = "Extracted all objects to " + txtExtractPath.Text;

        MessageBox.Show(extractResult);
      }));
    }
    private String NodeExtraction(TreeNode node, Boolean bulkExtract = false) {
      NodeAsset asset = (NodeAsset)node.Tag;

      if (asset.Obj != null) {
        WriteFile(
          new XDocument(new XElement(asset.Obj.Print())),
          asset.Obj.Name + ".xml",
          false,
          false
        );

        Byte[] buffer = asset.Obj.GetRawUncompressedNode();

        WriteFile(buffer, asset.Obj.Name + ".node");

        if (bulkExtract == false)
          // MessageBox.Show("Extracted " + asset.Obj.Name + " to " + extractPath);
          return "Extracted " + asset.Obj.Name + " to " + txtExtractPath.Text;
      } else {
        foreach (TreeNode childNode in node.Nodes)
          NodeExtraction(childNode, true);
      }

      return String.Empty;
    }
    private void NodeFindFilenames() {
      Int32 nodeCount = 0;
      Boolean firstRun = true;
      NodeFileSource _nodeSource = new NodeFileSource();

      foreach (KeyValuePair<String, List<NodeFileSourceItem>> source in _nodeSource.sources) {
        _searchNodes ??= new List<String>();
        _searchNodes = _assetDict.Keys.Where(d => d.Contains(source.Key)).ToList();

        _current ??= new String[3];

        foreach (String nodeKey in _searchNodes) {
          _current[1] = nodeKey; // Node
          NodeAsset node = _assetDict[nodeKey];

          if (node.Obj != null && node.Obj.Data != null) {
            _current[0] = node.id; // Item

            foreach (KeyValuePair<String, Object> item in node.Obj.Data.Dictionary) {
              NodeListItem dataItem = new NodeListItem(item.Key.ToString(), item.Value);

              if (dataItem.children.Count > 0) {
                _current[2] = dataItem.Name.ToString(); // Parent

                foreach (NodeListItem child in dataItem.children)
                  NodeHandleChildData(child, source.Value);

                dataItem.children.Clear();
              }

              dataItem = null;
            }
            node.Obj.Unload();
            nodeCount++;
          }

          node = null;

          // if (nodeCount == 10000) {
          //   if (outputData.Count > 0 || outputList.Count > 0) {
          //     writeData(firstRun);
          //     outputData.Clear();
          //     outputList.Clear();
          //     firstRun = false;
          //   }

          //   GC.Collect();
          //   nodeCount = 0;                            
          // }
        }

        if (source.Value.Count > 0) source.Value.Clear();
      }

      _searchNodes.Clear();
      _nodeSource.sources.Clear();

      _current = null;
      _searchNodes = null;
      _nodeSource = null;

      GC.Collect();
      WriteData(firstRun);
    }
    private void NodeDataGet(NodeAsset asset) {
      if (asset.Obj.Data != null) {
        _rootList = new ArrayList();

        foreach (KeyValuePair<String, Object> item in asset.Obj.Data.Dictionary) {
          if (item.Key.Contains("Script_")) continue;

          DomClass classLookup = (DomClass)asset.Obj.Data.Dictionary["Script_Type"];
          DomField fieldLookup = classLookup.Fields.Find(x => x.Name == item.Key);

          if (fieldLookup == null) {
            try {
              UInt64 id = UInt64.Parse(item.Key);
              fieldLookup = classLookup.Fields.Find(x => x.Id == id);

              if (fieldLookup == null)
                _currentDom.DomTypeMap.TryGetValue(id, out _); // DomType fieldLookup2); // Hmmm ???
            }
            catch (Exception ex) {
              Debug.WriteLine("Could not parse string: '" + item.Key.ToString() + "'");
              Debug.WriteLine("Exception: " + ex.ToString());
            }
          }

          try {
            if (fieldLookup == null) {
              NodeListItem item3 = new NodeListItem(item.Key, item.Value, null);
              _rootList.Add(item3);
            } else {
              NodeListItem item3 = new NodeListItem(item.Key, item.Value, fieldLookup.GomType);
              _rootList.Add(item3);
            }
          }
          catch (Exception ex) {
            Debug.WriteLine("Exception: " + ex.ToString());
          }
        }
      }
    }
    private void NodeHandleChildData(NodeListItem item, List<NodeFileSourceItem> fields) {
      if (item != null) {
        if (item.children.Count > 0) {
          _current[2] = item.Name.ToString(); // Parent

          foreach (NodeListItem child in item.children) NodeHandleChildData(child, fields);

        } else {
          if (item.value != null) {
            _outputData ??= new HashSet<String>();
            _outputList ??= new List<NodeOutput>();

            foreach (NodeFileSourceItem field in fields) {
              if (item.DisplayName == field.field) {
                switch (field.type) {
                  case "fx":
                    break;
                  case "fxgr2":
                    break;
                  case "icon":
                    break;
                  case "/":
                    break;
                  case "cnv":
                    break;
                  case "gfximg":
                    break;
                  case "spec":
                    break;
                  case "anim":
                    break;
                  case "gr2":
                    break;
                  case "string":
                    break;
                  case "bnk":
                    break;
                  case "dds":
                    break;
                  case "load":
                    break;
                  case "tip":
                    break;
                  case "codex":
                    break;
                  default:
                    throw new ArgumentException("Unhandled field type: " + field.type);
                }

                if (item.value.ToString().StartsWith("stg.")) continue;

                if (_buildCsv) {
                  NodeOutput output = new NodeOutput(
                    _current[1], // Node
                    _current[0], // Item
                    _current[2], // Parent
                    item.Name.ToString(),
                    item.value.ToString()
                  );
                  _outputList.Add(output);
                }

                _outputData.Add(item.value.ToString());
              }
            }

            // if (item.value.GetType() != typeof(string))
            //   return;
            // else {
            //   if (item.displayName == "String Value")
            //     return;

            //   String value = (String)item.value;

            //   if (value.Contains("/") || value.Contains("\\")) {
            //     if (value.Contains("</text>") 
            //         || value.Contains("<locComment />") 
            //         || value.Contains("/%") 
            //         || value.Contains("/$"))
            //       return;

            //       if (BuildCSV) {
            //         NodeOutput output = new NodeOutput(
            //           this.currentNode, 
            //           this.currentItem, 
            //           this.currentParent, 
            //           item.name.ToString(), 
            //           value
            //         );
            //         outputList.Add(output);
            //       }                                

            //        outputData.Add(value);
            //   }
            // }

          }
        }
      }
    }
    #endregion

    private TreeViewFast.Controls.TreeViewFast ActiveNodeTree =>
      _nodeFilterTree != null && _nodeFilterTree.Visible ? _nodeFilterTree : treeViewFast1;

    private void InitializeNodeFilterTreeView() {
      _nodeFilterTree = new TreeViewFast.Controls.TreeViewFast {
        BorderStyle = treeViewFast1.BorderStyle,
        Dock = treeViewFast1.Dock,
        ImageIndex = treeViewFast1.ImageIndex,
        ImageList = treeViewFast1.ImageList,
        Margin = treeViewFast1.Margin,
        SelectedImageIndex = treeViewFast1.SelectedImageIndex,
        Size = treeViewFast1.Size,
        TabIndex = treeViewFast1.TabIndex,
        Visible = false
      };
      _nodeFilterTree.AfterSelect += TreeViewFast1AfterSelect;
      _nodeFilterTree.KeyDown += TreeViewFast1KeyDown;
      _nodeFilterTree.MouseHover += TreeViewFast1MouseHover;
      _nodeFilterTree.MouseUp += TreeViewFast1MouseUp;
      splitContainer2.Panel1.Controls.Add(_nodeFilterTree);
      _nodeFilterTree.BringToFront();
    }

    private void InitializeNodeTreeLiveFilter() {
      _nodeTreeFilterTimer = new System.Windows.Forms.Timer { Interval = 180 };
      _nodeTreeFilterTimer.Tick += (_, __) => {
        _nodeTreeFilterTimer.Stop();
        ApplyNodeTreeLiveFilter();
      };
      txtSearch.TextChanged += (_, __) => {
        if (_closing) return;
        Boolean hasFilter = txtSearch.Enabled && !String.IsNullOrWhiteSpace(txtSearch.Text);
        btnClearSearch.Enabled = hasFilter;
        _nodeTreeFilterTimer.Stop();
        if (hasFilter) _nodeTreeFilterTimer.Start();
        else ApplyNodeTreeLiveFilter();
      };

      btnSearch.Visible = false;
      btnFindNext.Visible = false;
      btnClearSearch.Text = "Clear filter";
      btnClearSearch.Location = new System.Drawing.Point(34, 37);
      btnClearSearch.Size = new System.Drawing.Size(335, 27);
      btnClearSearch.Enabled = false;
      txtSearch.PlaceholderText = "Filter: words are ANDed, -word excludes, #3 = 3+ GLOMmed";
    }

    private readonly struct NodeSearchEntry {
      internal String Id { get; }
      internal String DisplayName { get; }
      internal String NodeName { get; }
      internal String ObjectId { get; }
      internal String BaseClassId { get; }
      internal Int32 NumGlommed { get; }

      internal NodeSearchEntry(
        String id,
        String displayName,
        String nodeName,
        String objectId,
        String baseClassId,
        Int32 numGlommed
      ) {
        Id = id ?? String.Empty;
        DisplayName = displayName ?? String.Empty;
        NodeName = nodeName ?? String.Empty;
        ObjectId = objectId ?? String.Empty;
        BaseClassId = baseClassId ?? String.Empty;
        NumGlommed = numGlommed;
      }
    }

    private sealed class NodeTreeFilterResult {
      internal Dictionary<String, NodeAsset> Items { get; }
      internal List<String> DisplayMatches { get; }
      internal Int32 TotalMatches { get; }
      internal Boolean IsTruncated { get; }
      internal TreeViewFast.Controls.TreeViewFast.PreparedTree PreparedTree { get; }

      internal NodeTreeFilterResult(
        Dictionary<String, NodeAsset> items,
        List<String> displayMatches,
        Int32 totalMatches,
        Boolean isTruncated = false,
        TreeViewFast.Controls.TreeViewFast.PreparedTree preparedTree = null
      ) {
        Items = items;
        DisplayMatches = displayMatches;
        TotalMatches = totalMatches;
        IsTruncated = isTruncated;
        PreparedTree = preparedTree;
      }
    }

    private static Boolean TryParseNodeGlomFilter(String query, out Int32 minGlommed) {
      minGlommed = 0;
      if (String.IsNullOrWhiteSpace(query) || query[0] != '#') return false;
      if (query == "#") {
        minGlommed = 1;
        return true;
      }
      if (!Int32.TryParse(query.Substring(1), out minGlommed)) return false;
      return minGlommed >= 0;
    }

    private NodeTreeFilterResult BuildNodeTreeFilter(
      TreeFilterQuery filter,
      Int32? minGlommed,
      CancellationToken token
    ) {
      Dictionary<String, NodeAsset> assets = _assetDict;
      if (assets == null) return new NodeTreeFilterResult(
        new Dictionary<String, NodeAsset>(StringComparer.OrdinalIgnoreCase),
        new List<String>(),
        0
      );

      var displayMatches = new List<String>(NodeTreeFilterDisplayLimit);
      Int32 totalMatches = 0;
      Int32 scanned = 0;
      Boolean truncated = false;
      NodeSearchEntry[] searchIndex = _nodeSearchIndex ?? Array.Empty<NodeSearchEntry>();

      foreach (NodeSearchEntry entry in searchIndex) {
        if ((++scanned & 0xff) == 0) token.ThrowIfCancellationRequested();

        Boolean match = minGlommed.HasValue
          ? entry.NumGlommed >= minGlommed.Value
          : filter.Matches(
              entry.Id, entry.DisplayName, entry.NodeName, entry.ObjectId, entry.BaseClassId
            );
        if (!match) continue;

        totalMatches++;
        if (displayMatches.Count < NodeTreeFilterDisplayLimit) {
          displayMatches.Add(entry.Id);
        } else {
          truncated = true;
          break;
        }
      }

      token.ThrowIfCancellationRequested();

      var include = new HashSet<String>(StringComparer.OrdinalIgnoreCase);
      var retainedMatches = new List<String>(displayMatches.Count);
      foreach (String id in displayMatches) {
        token.ThrowIfCancellationRequested();
        var path = new List<String>(12);
        String current = id;
        while (!String.IsNullOrWhiteSpace(current) && !include.Contains(current)) {
          path.Add(current);
          if (!assets.TryGetValue(current, out NodeAsset currentItem)
              || currentItem == null
              || String.IsNullOrWhiteSpace(currentItem.parentId)) break;
          current = currentItem.parentId;
        }

        if (include.Count + path.Count > NodeTreeFilterNodeLimit) {
          truncated = true;
          break;
        }
        foreach (String pathId in path) include.Add(pathId);
        retainedMatches.Add(id);
      }
      displayMatches = retainedMatches;

      var filtered = new Dictionary<String, NodeAsset>(include.Count, StringComparer.OrdinalIgnoreCase);
      foreach (String id in include) {
        if (assets.TryGetValue(id, out NodeAsset item) && item != null)
          filtered[id] = item;
      }

      TreeViewFast.Controls.TreeViewFast.PreparedTree prepared = PrepareNodeFilterTree(filtered.Values);
      return new NodeTreeFilterResult(filtered, displayMatches, totalMatches, truncated, prepared);
    }

    private static TreeViewFast.Controls.TreeViewFast.PreparedTree PrepareNodeFilterTree(
      IEnumerable<NodeAsset> items
    ) {
      String getId(NodeAsset x) => x.id;
      String getParentId(NodeAsset x) => x.parentId;
      String getDisplayName(NodeAsset x) => x.displayName;
      Int32 getImageIndex(NodeAsset x) =>
        x != null && (x.Obj != null || x.dynObject != null || x.objData != null) ? 2 : 1;
      Int32 compare(NodeAsset x, NodeAsset y) {
        Boolean xLeaf = x != null && (x.Obj != null || x.dynObject != null || x.objData != null);
        Boolean yLeaf = y != null && (y.Obj != null || y.dynObject != null || y.objData != null);
        if (xLeaf != yLeaf) return xLeaf ? 1 : -1;
        return String.Compare(x?.id, y?.id, StringComparison.Ordinal);
      }
      return TreeViewFast.Controls.TreeViewFast.PrepareItems(
        items, getId, getParentId, getDisplayName, getImageIndex, compare
      );
    }

    private NodeTreeFilterResult BuildNodeUnsupportedVersionFilterResult() {
      var items = new Dictionary<String, NodeAsset>(StringComparer.OrdinalIgnoreCase);
      if (_assetDict != null) {
        foreach (KeyValuePair<String, NodeAsset> pair in _assetDict) {
          if (pair.Value != null && String.IsNullOrWhiteSpace(pair.Value.parentId)) {
            items[pair.Key] = pair.Value;
            break;
          }
        }
      }
      return new NodeTreeFilterResult(
        items, new List<String>(), 0, false, PrepareNodeFilterTree(items.Values)
      );
    }

    private async void ApplyNodeTreeLiveFilter() {
      if (_closing || _assetDict == null || treeViewFast1 == null || txtSearch == null) return;
      if (InvokeRequired) { BeginInvoke(new Action(ApplyNodeTreeLiveFilter)); return; }

      TreeFilterQuery filter = TreeFilterQuery.Parse(txtSearch.Text, false);
      String query = filter.RawText;
      String selectedId = ActiveNodeTree?.SelectedNode?.Name;
      Int32? minGlommed = TryParseNodeGlomFilter(query, out Int32 glomValue)
        ? glomValue
        : null;

      CancellationTokenSource previous = _nodeTreeFilterCancellation;
      var cancellation = new CancellationTokenSource();
      _nodeTreeFilterCancellation = cancellation;
      try { previous?.Cancel(); previous?.Dispose(); } catch { }
      Int32 generation = ++_nodeTreeFilterGeneration;

      if (filter.IsEmpty) {
        ApplyNodeTreeFilterResult(
          filter,
          selectedId,
          new NodeTreeFilterResult(_assetDict, new List<String>(), 0),
          minGlommed
        );
        return;
      }

      if (filter.HasVersionTerms) {
        ApplyNodeTreeFilterResult(
          filter,
          selectedId,
          BuildNodeUnsupportedVersionFilterResult(),
          minGlommed
        );
        StatusLabel1Text(
          "Patch filter " + String.Join(" ", filter.VersionTerms)
          + " requires a local first-seen/history index. Text, -exclude and # GLOM filters are available now."
        );
        return;
      }

      StatusLabel1Text("Filtering nodes ...");

      NodeTreeFilterResult result;
      try {
        result = await Task.Run(
          () => BuildNodeTreeFilter(filter, minGlommed, cancellation.Token),
          cancellation.Token
        );
      }
      catch (OperationCanceledException) {
        return;
      }
      catch (ObjectDisposedException) {
        return;
      }

      if (_closing || cancellation.IsCancellationRequested
          || generation != _nodeTreeFilterGeneration
          || !String.Equals(query, (txtSearch.Text ?? String.Empty).Trim(), StringComparison.Ordinal)) return;

      ApplyNodeTreeFilterResult(filter, selectedId, result, minGlommed);
    }

    private void ApplyNodeTreeFilterResult(
      TreeFilterQuery filter,
      String selectedId,
      NodeTreeFilterResult result,
      Int32? minGlommed
    ) {
      if (_closing || treeViewFast1 == null || _nodeFilterTree == null) return;

      String query = filter?.RawText ?? String.Empty;
      if (filter == null || filter.IsEmpty) {
        _nodeFilterTree.Visible = false;
        treeViewFast1.Visible = true;
        treeViewFast1.BringToFront();
        btnClearSearch.Enabled = false;
        StatusLabel1Text(
          _compareNodes
            ? "Comparison loaded. Showing New, Changed and Removed nodes only."
            : "Showing all nodes."
        );
        return;
      }

      if (result == null || result.PreparedTree == null) return;
      Dictionary<String, NodeAsset> filtered = result.Items;
      List<String> directMatches = result.DisplayMatches;

      _nodeFilterTree.BeginUpdate();
      try {
        _nodeFilterTree.LoadPrepared(result.PreparedTree);

        if (directMatches.Count <= NodeTreeFilterAutoExpandLimit) {
          var expanded = new HashSet<String>(StringComparer.Ordinal);
          foreach (String id in directMatches) {
            if (!result.PreparedTree.NodeMap.TryGetValue(id, out TreeNode node)) continue;
            for (TreeNode parent = node.Parent; parent != null; parent = parent.Parent) {
              if (expanded.Add(parent.Name)) parent.Expand();
            }
          }
        } else {
          foreach (TreeNode root in _nodeFilterTree.Nodes) root.Expand();
        }

        if (!String.IsNullOrWhiteSpace(selectedId) && filtered.ContainsKey(selectedId)) {
          try { _nodeFilterTree.SelectedNode = _nodeFilterTree.GetNode(selectedId); } catch { }
        }
      } finally {
        _nodeFilterTree.EndUpdate();
      }

      treeViewFast1.Visible = false;
      _nodeFilterTree.Visible = true;
      _nodeFilterTree.BringToFront();

      btnClearSearch.Enabled = txtSearch.Enabled && query.Length > 0;
      String noun = minGlommed.HasValue ? "GLOM matches" : "matches";
      if (result.IsTruncated) {
        String countText = result.TotalMatches > NodeTreeFilterDisplayLimit
          ? NodeTreeFilterDisplayLimit.ToString("n0") + "+"
          : result.TotalMatches.ToString("n0");
        StatusLabel1Text(
          "Filter: " + countText + " " + noun
          + ". Showing first " + directMatches.Count.ToString("n0") + " ("
          + filtered.Count.ToString("n0")
          + " nodes including parents); type more characters to narrow the result."
        );
      } else {
        StatusLabel1Text(
          "Filter: " + result.TotalMatches.ToString("n0") + " " + noun + " ("
          + filtered.Count.ToString("n0") + " nodes including parents)."
        );
      }
    }

    #region Search
    private void Search() {
      StatusLabel1Text("Performing Search ...");
      _searchNodes ??= new List<String>();
      _searchNodes = _assetDict.Keys.Where(d => d.Contains(txtSearch.Text)).ToList();

      if (_searchNodes.Count > 0) {
        Searching();
      } else {
        if (UInt64.TryParse(txtSearch.Text, out UInt64 nodeId)) {
          GomObject node = _currentDom.GetObject(nodeId);
          if (node == null && _compareNodes && _previousDom != null)
            node = _previousDom.GetObject(nodeId);

          if (node != null) {
            txtSearch.Text = node.Name;
            _searchNodes = _assetDict.Keys.Where(d => d.Contains(txtSearch.Text)).ToList();

            if (_searchNodes.Count > 0) {
              Searching();
              return;
            }
          }
        }

        StatusLabel1Text("Search Complete.");
        MessageBox.Show("Search term not found.");
      }
    }
    private void Searching() {
      txtSearch.Enabled = false;
      btnSearch.Enabled = false;
      btnFindNext.Enabled = true;
      btnClearSearch.Enabled = true;
      StatusLabel1Text("Found " + (_searchNodes.Count + 1) + " Matches.");
      LoadingSwirlShow();
      _nodeMatch = treeViewFast1.Nodes.Find(_searchNodes[_searchIndex], true);
      LoadingSwirlHide();
      treeViewFast1.SelectedNode = _nodeMatch[0];
      btnFindNext.Focus();
      StatusLabel1Text("Item " + (_searchIndex + 1) + " of " + _searchNodes.Count);
      _searchIndex++;
    }
    #endregion

    #region ToolStrip1
    private void ToolStripButton1Click(Object sender, EventArgs e) {
      if (!String.IsNullOrEmpty(toolStripTextBox1.Text)) {
        toolStripTextBox1.Enabled = false;
        toolStripButton1.Enabled = false;
        toolStripButton2.Enabled = true;
        treeViewGrid1.ModelFilter = TextMatchFilter.Contains(treeViewGrid1, toolStripTextBox1.Text);
        _filter = true;
      }
    }
    private void ToolStripButton2Click(Object sender, EventArgs e) {
      treeViewGrid1.ModelFilter = null;
      toolStripTextBox1.Text = String.Empty;
      toolStripTextBox1.Enabled = true;
      toolStripButton1.Enabled = true;
      toolStripButton2.Enabled = false;
      _filter = false;
    }
    private void ToolStripButton3Click(Object sender, EventArgs e) {
      toolStrip1.Visible = false;
    }
    private void ToolStripButton3MouseEnter(Object sender, EventArgs e) {
      toolStripButton3.BackColor = System.Drawing.Color.Red;
      toolStripButton3.ForeColor = System.Drawing.Color.White;
    }
    private void ToolStripButton3MouseLeave(Object sender, EventArgs e) {
      toolStripButton3.BackColor = System.Drawing.SystemColors.Control;
      toolStripButton3.ForeColor = System.Drawing.Color.Black;
    }
    private void ToolStripMenuItem1Click(Object sender, EventArgs e) {
      BtnExtractClick(this, null);
    }
    private void ToolStripMenuItem2Click(Object sender, EventArgs e) {
      NavigateToNodeReference(treeViewGrid1.SelectedObject as NodeListItem);
    }

    private Boolean NavigateToNodeReference(NodeListItem item) {
      if (!TryGetNodeReference(item, out String nodeString) || String.IsNullOrWhiteSpace(nodeString)) return false;

      // Navigation targets the persistent full tree. Leaving the filter view is an O(1) visibility
      // swap now, so references can reliably reveal their real hierarchy without rebuilding it.
      if (_nodeFilterTree != null && _nodeFilterTree.Visible && !String.IsNullOrEmpty(txtSearch.Text))
        txtSearch.Clear();

      TreeNode[] nodes = treeViewFast1.Nodes.Find(nodeString, true);
      if (nodes.Length == 0 && _compareNodes) {
        String compareKey = _assetDict.Keys.FirstOrDefault(key =>
          key.EndsWith("/" + nodeString, StringComparison.OrdinalIgnoreCase)
        );
        if (!String.IsNullOrEmpty(compareKey)) nodes = treeViewFast1.Nodes.Find(compareKey, true);
      }

      if (nodes.Length == 0) return false;
      treeViewFast1.SelectedNode = nodes[0];
      treeViewFast1.SelectedNode.EnsureVisible();
      treeViewFast1.Focus();
      return true;
    }

    private Boolean TryGetNodeReference(NodeListItem item, out String nodeString) {
      nodeString = null;
      if (item == null || _currentDom == null) return false;

      // First use the actual typed value; unlike parsing the display text this also catches FQN
      // strings (spnEntityFqn, plcModel -> dyn.*, conversation refs, etc.).
      try {
        if (item.value is UInt64 u) nodeString = _currentDom.GetObject(u)?.Name;
        else if (item.value is Int64 i) nodeString = _currentDom.GetObject(unchecked((UInt64)i))?.Name;
        else if (item.value is UInt32 u32) nodeString = _currentDom.GetObject((UInt64)u32)?.Name;
        else if (item.value is Int32 i32 && i32 >= 0) nodeString = _currentDom.GetObject((UInt64)i32)?.Name;
        else if (item.value is String direct) {
          String trimmed = direct.Trim();
          if (UInt64.TryParse(trimmed, out UInt64 numeric)) nodeString = _currentDom.GetObject(numeric)?.Name;
          else nodeString = _currentDom.GetObject(trimmed)?.Name;
        }
      } catch { }

      if (!String.IsNullOrWhiteSpace(nodeString)) return true;

      // Fall back to the legacy “123 (fqn)” decoration used by NodeListItem for node ids and map keys.
      foreach (String display in new[] { item.DisplayValue, item.DisplayName }) {
        if (String.IsNullOrWhiteSpace(display)) continue;
        Int32 open = display.LastIndexOf(" (", StringComparison.Ordinal);
        if (open < 0 || !display.EndsWith(")", StringComparison.Ordinal)) continue;
        String candidate = display.Substring(open + 2, display.Length - open - 3).Trim();
        if (candidate.Length == 0) continue;
        try {
          GomObject obj = _currentDom.GetObject(candidate);
          if (obj != null) {
            nodeString = obj.Name;
            return true;
          }
        } catch { }
      }
      return false;
    }

    private void ToolStripTextBox1KeyDown(Object sender, KeyEventArgs e) {
      if (e.KeyCode == Keys.Enter && !String.IsNullOrEmpty(toolStripTextBox1.Text)) {
        toolStripTextBox1.Enabled = false;
        toolStripButton1.Enabled = false;
        toolStripButton2.Enabled = true;
        treeViewGrid1.ModelFilter = TextMatchFilter.Contains(treeViewGrid1, toolStripTextBox1.Text);
        _filter = true;
      }
      if (e.Control && e.KeyCode == Keys.F) toolStrip1.Visible = false;
    }
    #endregion

    #region ToolStripProgressBar1
    private void ProgressBarHide() {
      if (statusStrip1.InvokeRequired)
        statusStrip1.Invoke(new Action(() => ProgressBarHide()));
      else
        toolStripProgressBar1.Visible = false;
    }
    private void ProgressBarShow() {
      if (statusStrip1.InvokeRequired)
        statusStrip1.Invoke(new Action(() => ProgressBarShow()));
      else
        toolStripProgressBar1.Visible = true;
    }
    private void ProgressBarStyle(ProgressBarStyle style) {
      if (statusStrip1.InvokeRequired)
        statusStrip1.Invoke(new Action(() => ProgressBarStyle(style)));
      else
        toolStripProgressBar1.Style = style;
    }
    private void ProgressBarValue(Int32 value) {
      if (statusStrip1.InvokeRequired)
        statusStrip1.Invoke(new Action(() => ProgressBarValue(value)));
      else
        toolStripProgressBar1.Value = value;
    }
    #endregion

    #region ToolStripStatusLabel1
    private void StatusLabel1Text(String text) {
      if (statusStrip1.InvokeRequired)
        statusStrip1.Invoke(new Action(() => StatusLabel1Text(text)));
      else
        toolStripStatusLabel1.Text = text;
    }
    private void StatusLabel1Hide() {
      if (statusStrip1.InvokeRequired)
        statusStrip1.Invoke(new Action(() => StatusLabel1Hide()));
      else
        toolStripStatusLabel1.Visible = false;
    }
    private void StatusLabel1Show() {
      if (statusStrip1.InvokeRequired)
        statusStrip1.Invoke(new Action(() => StatusLabel1Show()));
      else
        toolStripStatusLabel1.Visible = true;
    }
    #endregion

    #region TreeViewFast1
    private void TreeViewFast1AfterSelect(Object sender, TreeViewEventArgs e) {
      TreeNode node = e?.Node ?? (sender as TreeViewFast.Controls.TreeViewFast)?.SelectedNode;
      if (node?.Tag is not NodeAsset asset) return;

      String actualNodeName = asset?.Obj?.Name ?? asset?.displayName ?? asset?.id;
      Text = "Node Browser - " + actualNodeName;

      _collapsed = false;
      btnToggleCollapse.Enabled = true;
      btnToggleCollapse.Text = "Collapse Child Nodes";

      if (asset != null) {
        StatusLabel1Text("Loading Selected Node ...");

        _rootList = new ArrayList();
        treeViewGrid1.ModelFilter = null;

        if (asset.Obj != null) {
          TreeViewGrid1Hide();
          LoadingSwirlShow();
          ProgressBarShow();

          // await Task.Run(() => GetNodeData(asset));
          NodeDataGet(asset);
          TreeViewGrid1Roots(_rootList);
          TreeViewGrid1ExpandAll();

          if (_rootList != null && _rootList.Count > 0) {
            treeViewGrid1.AutoResizeColumn(0, ColumnHeaderAutoResizeStyle.ColumnContent);
            treeViewGrid1.AutoResizeColumn(1, ColumnHeaderAutoResizeStyle.ColumnContent);
          } else {
            treeViewGrid1.Columns[0].Width = splitContainer3.Panel1.Width / 3;
            treeViewGrid1.Columns[1].Width = splitContainer3.Panel1.Width / 3;
          }

          if (_filter)
            treeViewGrid1.ModelFilter =
              TextMatchFilter.Contains(treeViewGrid1, toolStripTextBox1.Text);
        }

        treeViewGrid1.TopItemIndex = 0;
      }

      StatusLabel1Text(
        asset.compareState != BuildFileState.None
          ? asset.compareState + " Node: " + actualNodeName
          : asset.id
      );

      _dataTable = new DataTable();
      _dataTable.Columns.Add("Property");
      _dataTable.Columns.Add("Value");
      _dataTable.Rows.Add(new String[] { "Node", actualNodeName });
      if (asset.compareState != BuildFileState.None)
        _dataTable.Rows.Add(new String[] { "Compare State", asset.compareState.ToString() });

      _currentTreeNode = actualNodeName;
      dataGridView1.DataSource = _dataTable;
      dataGridView1.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.AllCells;
      UpdateNodePreview(asset);

      LoadingSwirlHide();
      ProgressBarHide();
      TreeViewGrid1Show();

      return;
    }
    private void TreeViewFast1Hide() {
      if (treeViewFast1.InvokeRequired) treeViewFast1.Invoke(new Action(() => TreeViewFast1Hide()));
      else treeViewFast1.Visible = false;
    }
    private void TreeViewFast1KeyDown(Object sender, KeyEventArgs e) {
      if (e.Control && e.KeyCode == Keys.F)
        txtSearch.Focus();

    }
    private void TreeViewFast1MouseHover(Object sender, EventArgs e) {
      if (!_closing && !btnFindNext.Focused && sender is TreeViewFast.Controls.TreeViewFast tree)
        tree.Focus();
    }
    private void TreeViewFast1MouseUp(Object sender, MouseEventArgs e) {
      if (e.Button == MouseButtons.Right && sender is TreeViewFast.Controls.TreeViewFast tree) {
        tree.SelectedNode = tree.GetNodeAt(e.X, e.Y);
        if (tree.SelectedNode != null) contextMenuStrip1.Show(tree, e.Location);
      }
    }
    private void TreeViewFast1Show() {
      if (treeViewFast1.InvokeRequired)
        treeViewFast1.Invoke(new Action(() => TreeViewFast1Show()));
      else
        treeViewFast1.Visible = true;
    }
    #endregion

    #region TreeViewGrid1
    private void TreeViewGrid1ExpandAll() {
      if (treeViewGrid1.InvokeRequired)
        treeViewGrid1.Invoke(new Action(() => TreeViewGrid1ExpandAll()));
      else
        treeViewGrid1.ExpandAll();
    }
    private void TreeViewGrid1Hide() {
      if (treeViewGrid1.InvokeRequired)
        treeViewGrid1.Invoke(new Action(() => TreeViewGrid1Hide()));
      else
        treeViewGrid1.Visible = false;
    }
    private void TreeViewGrid1KeyDown(Object sender, KeyEventArgs e) {
      if (e.Control && e.KeyCode == Keys.F)
        if (toolStrip1.Visible) {
          toolStrip1.Visible = false;
          treeViewGrid1.Focus();
        } else {
          toolStrip1.Visible = true;
          toolStripTextBox1.Focus();
        }
    }
    private void TreeViewGrid1MouseDoubleClickNavigate(Object sender, MouseEventArgs e) {
      if (e.Button != MouseButtons.Left) return;
      if (treeViewGrid1.SelectedObject is NodeListItem item) NavigateToNodeReference(item);
    }

    private void TreeViewGrid1MouseHover(Object sender, EventArgs e) {
      if (!_closing && !btnFindNext.Focused && !toolStripTextBox1.Focused)
        treeViewGrid1.Focus();
    }
    private void TreeViewGrid1MouseUp(Object sender, MouseEventArgs e) {
      if (e.Button == MouseButtons.Right) {
        TreeListView tlv = sender as TreeListView;

        if (tlv.SelectedObject is NodeListItem item && TryGetNodeReference(item, out _))
          contextMenuStrip2.Show(treeViewGrid1, e.Location);
      }
    }
    private void TreeViewGrid1Roots(ArrayList roots) {
      if (treeViewGrid1.InvokeRequired)
        treeViewGrid1.Invoke(new Action(() => TreeViewGrid1Roots(roots)));
      else
        treeViewGrid1.Roots = roots;
    }
    private void TreeViewGrid1SelectedIndexChanged(Object sender, EventArgs e) {
      TreeListView tlv = sender as TreeListView;
      _dataTable = new DataTable();
      _dataTable.Columns.Add("Property");
      _dataTable.Columns.Add("Value");
      _dataTable.Rows.Add(new String[] { "Current Node", _currentTreeNode });

      if (tlv.SelectedObject is NodeListItem child) {
        if (child.Name != null)
          _dataTable.Rows.Add(new String[] { "Current Item", child.Name.ToString() });
        else
          _dataTable.Rows.Add(new String[] { "Current Item", "" });

        if (child.value != null)
          _dataTable.Rows.Add(new String[] { "Current Value", child.value.ToString() });
        else
          _dataTable.Rows.Add(new String[] { "Current Value", "" });
      }

      dataGridView1.DataSource = _dataTable;
      dataGridView1.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.AllCells;

      if (tlv.SelectedObject != null) {
        // Node selected. Lets see if it can collapse or not.
        TreeListView.Branch br = tlv.TreeModel.GetBranch(tlv.SelectedObject);

        if (br != null && br.CanExpand && br.IsExpanded) {
          btnToggleCollapse.Text = "Collapse Child Nodes";
        } else if (br != null && br.CanExpand && !br.IsExpanded) {
          btnToggleCollapse.Text = "Expand Child Nodes";
        }
      } else {
        // Page selected.
        if (_collapsed) {
          btnToggleCollapse.Text = "Expand Child Nodes";
        } else {
          btnToggleCollapse.Text = "Collapse Child Nodes";
        }
      }
    }
    private void TreeViewGrid1Show() {
      if (treeViewGrid1.InvokeRequired)
        treeViewGrid1.Invoke(new Action(() => TreeViewGrid1Show()));
      else
        treeViewGrid1.Visible = true;
    }
    #endregion

    #region TxtSearch
    private void TxtSearchKeyDown(Object sender, KeyEventArgs e) {
      if (e.KeyCode == Keys.Enter) {
        _nodeTreeFilterTimer?.Stop();
        ApplyNodeTreeLiveFilter();
        e.Handled = true;
        e.SuppressKeyPress = true;
      } else if (e.KeyCode == Keys.Escape && !String.IsNullOrEmpty(txtSearch.Text)) {
        txtSearch.Clear();
        e.Handled = true;
        e.SuppressKeyPress = true;
      }
    }
    #endregion

    #region Write Methods
    private void WriteData(Boolean firstRun) {
      if (!System.IO.Directory.Exists(txtExtractPath.Text + "File_Names"))
        System.IO.Directory.CreateDirectory(txtExtractPath.Text + "File_Names");

      if (_buildCsv) {
        if (_outputList != null && _outputList.Count > 0) {
          using System.IO.StreamWriter writeCsv = new System.IO.StreamWriter(
            txtExtractPath.Text + "File_Names\\node_string_data.csv", !firstRun
          );

          foreach (NodeOutput node in _outputList) {
            writeCsv.Write(
              node.node
                + ", "
                + node.item
                + ", "
                + node.parent
                + ", "
                + node.name
                + ", "
                + node.value
                + "\r\n"
            );
          }

          writeCsv.Close();
          _outputList.Clear();
        }
      }

      if (_outputData != null && _outputData.Count > 0) {
        using System.IO.StreamWriter writeTxt = new System.IO.StreamWriter(
          txtExtractPath.Text + "File_Names\\node_string_list.txt", !firstRun
        );

        foreach (String data in _outputData) writeTxt.Write(data + "\r\n");

        writeTxt.Close();
        _outputData.Clear();
      }

      GC.Collect();
    }
    private void WriteFile(Byte[] content, String filename) {
      if (content == null || content.Length == 0) return;

      filename = filename.Replace('/', '.');

      if (!System.IO.Directory.Exists(txtExtractPath.Text))
        System.IO.Directory.CreateDirectory(txtExtractPath.Text);

      System.IO.File.WriteAllBytes(txtExtractPath.Text + filename, content);
    }
    private void WriteFile(XDocument content, String filename, Boolean append, Boolean trimEmpty) {
      if (trimEmpty)
        content.Descendants().Where(e => e.IsEmpty || string.IsNullOrWhiteSpace(e.Value)).Remove();

      if (content.Root.IsEmpty) return;

      filename = filename.Replace('/', '.');

      if (!System.IO.Directory.Exists(txtExtractPath.Text))
        System.IO.Directory.CreateDirectory(txtExtractPath.Text);

      using System.IO.StreamWriter file2 =
        new System.IO.StreamWriter(txtExtractPath.Text + filename, append);

      content.Save(file2, SaveOptions.None);
    }
    #endregion
  }
}
