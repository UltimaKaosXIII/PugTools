using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using System.Xml;

using FileFormats;

using GomLib;
using GomLib.Models;

using SlimDX;
using TorArchive;

using File = TorArchive.File;

namespace PugTools {
  public partial class ModelBrowser : Form {
    private class TestRule {
      public String slot = "";
      public String archetype = "";
      public String attachmentName = "";
      public List<String> tags = new List<String>();
    }

    #region Fields

    private Dictionary<String, NodeAsset> _assetDict;
    private readonly String _assetsLocation;
    private readonly Boolean _assetsUsePts;
    private readonly Boolean _compareFiles;
    private String _bodyType = "bmn";
    private readonly Dictionary<String, String> _bodyTypes;
    internal Boolean _closing;
    private Assets _currentAssets;
    private DataObjectModel _currentDom;
    private Dictionary<String, NodeAsset> _dataViewDict;
    private HashDictionaryInstance _hashData;
    private List<ItemAppearance> _items;
    private Dictionary<Object, Object> _mntMountInfoData;
    private Dictionary<String, GR2> _models;
    private View_NPC_GR2 _panelRender;
    private Assets _previousAssets;
    private readonly String _previousAssetsLocation;
    private readonly Boolean _previousAssetsUsePts;
    private DataObjectModel _previousDom;
    private Thread _render;
    private Dictionary<String, Object> _resources;
    private Dictionary<String, List<String>> _tagExclusions;
    private Dictionary<String, List<String>> _testGroups;
    private List<TestRule> _testRules;
    private Dictionary<String, Object> _weaponAppearance;
    private readonly HashSet<String> _parsedFxSpecs = new HashSet<String>(StringComparer.OrdinalIgnoreCase);

    #endregion Fields

    #region Model Browser Form
    internal ModelBrowser(String assetLocation,
                        Boolean usePTS,
                        String previousAssetLocation,
                        Boolean previousUsePTS,
                        Boolean loadprevious) {
      InitializeComponent();
      Config.Load();

      _assetsLocation = assetLocation;
      _assetsUsePts = usePTS;
      _previousAssetsLocation = previousAssetLocation;
      _previousAssetsUsePts = previousUsePTS;
      _compareFiles = loadprevious;
      _hashData = HashDictionaryInstance.Instance;

      if (!_hashData.Loaded) _hashData.Load();

      _bodyTypes = new Dictionary<String, String> {
        {"bfa", "Female BT1"},
        {"bfn", "Female BT2"},
        {"bfs", "Female BT3"},
        {"bfb", "Female BT4"},
        {"bma", "Male BT1"},
        {"bmn", "Male BT2"},
        {"bms", "Male BT3"},
        {"bmf", "Male BT4"}
      };

      StatusBarText("Loading Assets ...");
      RenderPanelHide();
      LoadingSwirlShow();
      ProgressBarShow();

      backgroundWorker1.RunWorkerAsync(loadprevious);
    }
    private void ModelBrowserFormClosed(Object sender, FormClosedEventArgs e) {
      Hide();

      HashDictionaryInstance.Instance.Unload();

      if (_panelRender != null) {
        _panelRender.StopRender();

        if (_render != null) _render.Join();

        _panelRender.Clear();
        _panelRender.Dispose();
        _panelRender = null;
      }

      if (treeViewFast1 != null) {
        treeViewFast1.Dispose();
        treeViewFast1 = null;
      }

      if (treeViewFast2 != null) {
        treeViewFast2.Dispose();
        treeViewFast2 = null;
      }

      if (dataGridView1 != null) {
        dataGridView1.Dispose();
        dataGridView1 = null;
      }

      _assetDict = null;
      _currentAssets = null;
      _currentDom = null;
      _dataViewDict = null;
      _items = null;
      _mntMountInfoData = null;
      _hashData = null;
      _models = null;
      _previousAssets = null;
      _previousDom = null;
      _resources = null;
      _tagExclusions = null;
      _testGroups = null;
      _testRules = null;
      _weaponAppearance = null;

      Dispose(true);

      System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.Interactive;
    }
    private void ModelBrowserFormClosing(Object sender, FormClosingEventArgs e) {
      System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.Interactive;
      _closing = true;
    }
    private void ModelBrowserFormResize(Object sender, EventArgs e) {
      treeViewFast1.Size =
        new System.Drawing.Size(splitContainer2.Panel1.Width, splitContainer2.Panel1.Height - 40);
    }
    #endregion

    private static GomObjectData FindFirstGomObjectData(Object value) {
      if (value == null)
        return null;

      if (value is GomObjectData data)
        return data;

      if (value is IEnumerable<Object> values) {
        foreach (Object entry in values) {
          GomObjectData result = FindFirstGomObjectData(entry);
          if (result != null)
            return result;
        }
      }

      if (value is IDictionary<Object, Object> map) {
        foreach (Object entry in map.Values) {
          GomObjectData result = FindFirstGomObjectData(entry);
          if (result != null)
            return result;
        }
      }

      return null;
    }

    #region Background Worker Methods
    private void BackgroundWorker1DoWork(Object sender, DoWorkEventArgs e) {
      if (_closing) return;

      System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.Interactive;

      // Load current assets.
      _currentAssets = AssetHandler.Instance.GetCurrentAssets(_assetsLocation, _assetsUsePts);
      _currentDom = DomHandler.Instance.GetCurrentDOM(_currentAssets);

      if ((Boolean)e.Argument) {
        // Load previous assets.
        _previousAssets =
          AssetHandler.Instance.GetPreviousAssets(_previousAssetsLocation, _previousAssetsUsePts);
        _previousDom = DomHandler.Instance.GetPreviousDOM(_previousAssets);
        if (!_hashData.Loaded) _hashData.Load();
      }
    }
    private void BackgroundWorker1RunWorkerCompleted(Object sender, RunWorkerCompletedEventArgs e) {
      if (_closing) return;

      if (e.Error != null) {
        throw new Exception("Echter Fehler beim Laden (Worker 1): " + e.Error, e.Error);
      }

      backgroundWorker2.RunWorkerAsync();
    }
    private void BackgroundWorker2ProgressChanged(Object sender, ProgressChangedEventArgs e) {
      toolStripProgressBar1.Value = e.ProgressPercentage;
    }
    private void BackgroundWorker2DoWork(Object sender, DoWorkEventArgs e) {
      if (_closing) return;

      _assetDict = new Dictionary<String, NodeAsset>();

      HashSet<String> fileDirs = new HashSet<String>();
      HashSet<String> allDirs = new HashSet<String>();
      HashSet<String> nodeDirs = new HashSet<String>();

      #region Nodes

      Dictionary<Object, Object> weaponApp = _currentDom.GetObject("itmAppearanceDatatable").Data
          .Get<Dictionary<Object, Object>>("itmAppearances");
      _weaponAppearance = new Dictionary<String, Object>();

      foreach (KeyValuePair<Object, Object> app in weaponApp) {
        _weaponAppearance.Add(app.Key.ToString().ToLower(), app.Value);
      }

      // Mount table names changed over the lifetime of SWTOR.  Older PugTools
      // expected only mntMountInfoData; current GOM data exposes mntIdToDataMap
      // (and some intermediate builds used mntDataMap).  Treat all of them as
      // aliases and do not abort ModelBrowser startup when one is absent.
      _mntMountInfoData = new Dictionary<Object, Object>();
      GomObject mountInfoPrototype = _currentDom.GetObject("mntMountInfoPrototype");
      if (mountInfoPrototype?.Data != null) {
        Object mountTable = null;
        if (!mountInfoPrototype.Data.Dictionary.TryGetValue("mntMountInfoData", out mountTable))
          if (!mountInfoPrototype.Data.Dictionary.TryGetValue("mntIdToDataMap", out mountTable))
            mountInfoPrototype.Data.Dictionary.TryGetValue("mntDataMap", out mountTable);

        if (mountTable is Dictionary<Object, Object> objectMap)
          _mntMountInfoData = objectMap;
        else if (mountTable is IDictionary<Object, Object> objectDictionary)
          _mntMountInfoData = objectDictionary.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
      }

      // Get the relevant nodes from the new dom.
      List<GomObject> itmList =
        _currentDom.GetObjectsStartingWith("npp.")
        .Union(_currentDom.GetObjectsStartingWith("ipp."))
        .Union(_currentDom.GetObjectsStartingWith("itm."))
        .Union(_currentDom.GetObjectsStartingWith("dyn.housing"))
        .Union(_currentDom.GetObjectsStartingWith("dyn.stronghold"))
        .ToList();

      ProgressBarStyle(System.Windows.Forms.ProgressBarStyle.Continuous);
      StatusBarText("Loading Node Data ...");

      Int32 nodesDone = 0;
      Int32 nodesTotal = itmList.Count;

      foreach (GomObject item in itmList) {
        if (item.Name.StartsWith("itm.")) {
          String appearSpec = item.Data.ValueOrDefault<String>("cbtWeaponAppearanceSpec", null);

          if (appearSpec == null) {
            nodesDone++;
            continue;
          }
        }

        String parent = String.Empty;
        String display = item.Name;

        if (item.Name.Contains(".")) {
          String[] temp = item.Name.Split('.');
          parent = String.Join(".", temp.Take(temp.Length - 1));
          display = display.Replace(parent, String.Empty).Replace(".", String.Empty);

          if (item.Name.StartsWith("itm.")) {
            // Try and get the item name.
            if (item.Data.ContainsKey("locTextRetrieverMap")) {
              GomObjectData nameLookupData =
                (GomObjectData)item.Data.Get<Dictionary<Object, Object>>(
                  "locTextRetrieverMap"
                )[-2761358831308646330];
              String itmName = _currentDom.StringTable.TryGetString(item.Name, nameLookupData);

              if (itmName.Length > 0) {
                // Found the item name, put it in brackets.
                display = display + " (" + itmName + ")";
              }
            }
          }

          nodeDirs.Add(parent);
        }

        NodeAsset asset = new NodeAsset(item.Name, parent, display, _currentDom);

        _assetDict.Add(item.Name, asset);
        item.Unload(); // Make sure these aren't hanging around

        nodesDone++;
        backgroundWorker2.ReportProgress(nodesDone * 100 / nodesTotal);
      }

      // Determine which nodes are new.
      List<Int32> newNodeIndexes = new List<Int32>();
      if (_previousDom != null) {
        for (Int32 i = 0; i < itmList.Count; i++) {
          GomObject newObj = itmList[i];
          GomObject oldObj = _previousDom.GetObject(newObj.Name);

          if (oldObj == null)
            // Node is new.
            newNodeIndexes.Add(i);
        }

        // Build the new list.
        foreach (Int32 i in newNodeIndexes) {
          GomObject item = itmList[i];

          if (item.Name.StartsWith("itm.")) {
            String appearSpec = item.Data.ValueOrDefault<String>("cbtWeaponAppearanceSpec", null);

            if (appearSpec == null) continue;
          }

          String parent = String.Empty;
          String display = item.Name;

          if (item.Name.Contains(".")) {
            String[] temp = item.Name.Split('.');
            parent = String.Join(".", temp.Take(temp.Length - 1));
            display = display.Replace(parent, String.Empty).Replace(".", String.Empty);
            parent = "new." + parent;

            if (item.Name.StartsWith("itm.")) {
              // Try and get the item name.
              if (item.Data.ContainsKey("locTextRetrieverMap")) {
                GomObjectData nameLookupData =
                (GomObjectData)item.Data.Get<Dictionary<Object, Object>>(
                  "locTextRetrieverMap"
                )[-2761358831308646330];
                String itmName = _currentDom.StringTable.TryGetString(item.Name, nameLookupData);

                if (itmName.Length > 0)
                  // Found the item name, put it in brackets.
                  display = display + " (" + itmName + ")";
              }
            }

            nodeDirs.Add(parent);
          }

          newNodeIndexes = null;
          NodeAsset asset = new NodeAsset("new." + item.Name, parent, display, item);

          _assetDict.Add("new." + item.Name, asset);
          item.Unload(); // Make sure these aren't hanging around
        }
      }

      foreach (KeyValuePair<Object, Object> item in _mntMountInfoData) {
        GomObjectData value = FindFirstGomObjectData(item.Value);
        if (value == null)
          continue;

        Object spec = null;
        if (!value.Dictionary.TryGetValue("mntDataSpecString", out spec))
          value.Dictionary.TryGetValue("mntDataSpec", out spec);

        if (spec == null)
          continue;

        String specString = spec.ToString();
        if (String.IsNullOrWhiteSpace(specString))
          continue;

        String parent;
        String display = specString.Split('.').Last();

        if (specString.Contains(".")) {
          String[] temp = specString.Split('.');
          parent = String.Join(".", temp.Take(temp.Length - 1));
          display = display.Replace(parent, "").Replace(".", "");

          nodeDirs.Add(parent);
        } else
          parent = "/nodes";

        NodeAsset asset = new NodeAsset(specString, parent, display, value);

        if (!_assetDict.ContainsKey(specString))
          _assetDict.Add(specString, asset);
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

        if (String.IsNullOrEmpty(parentDir)) parentDir = "/nodes";

        String display = temp.Last();
        NodeAsset asset = new NodeAsset(dir, parentDir, display, null);

        if (!_assetDict.ContainsKey(dir)) _assetDict.Add(dir, asset);
      }

      allDirs.Clear();
      #endregion

      #region Assets

      const String prefixAll = "/assets/all";
      const String prefixNew = "/assets/new";
      const String prefixMod = "/assets/modified";
      const String prefixUnn = "/assets/unnamed";

      ProgressBarValue(0);
      ProgressBarStyle(System.Windows.Forms.ProgressBarStyle.Continuous);
      StatusBarText("Loading Files ...");

      Int32 libsDone = 0;
      Int32 totalLibs = _currentAssets.Libraries.Count;

      if (_compareFiles && _previousAssets != null) {
        BuildComparedGr2Assets(fileDirs);
      } else {
        foreach (Library lib in _currentAssets.Libraries) {
          String path = lib.Location;

          if (!lib.Loaded) lib.Load();

          foreach (KeyValuePair<Int32, Archive> arch in lib.Archives) {
            foreach (File file in arch.Value.EnumerateFiles()) {
              HashFileInfo hashInfo =
                new HashFileInfo(file.FileInfo.PrimaryHash, file.FileInfo.SecondaryHash, file);

              if (hashInfo.IsNamed) {
                if (hashInfo.FileName == "metadata.bin" || hashInfo.FileName == "ft.sig"
                    // || hashInfo.FileName == "groupmanifest.bin") continue;
                    || hashInfo.Extension.ToUpper() != "GR2") continue;

                NodeAsset assetAll =
                  new NodeAsset(
                    prefixAll + hashInfo.Directory + "/" + hashInfo.FileName,
                    prefixAll + hashInfo.Directory,
                    hashInfo.FileName,
                    hashInfo
                  );

                if (!_assetDict.ContainsKey(
                  prefixAll + hashInfo.Directory + "/" + hashInfo.FileName)) {
                  _assetDict.Add(prefixAll + hashInfo.Directory + "/" + hashInfo.FileName, assetAll);
                } else {
                  // String pauseHere = "";
                }

                fileDirs.Add(prefixAll + hashInfo.Directory);

                if (hashInfo.FileState == HashFileInfo.State.New) {
                  NodeAsset assetNew = new NodeAsset(
                      prefixNew + hashInfo.Directory + "/" + hashInfo.FileName,
                      prefixNew + hashInfo.Directory,
                      hashInfo.FileName, hashInfo
                  );
                  String fileName = String.Format(
                    "{0}{1}/{2}",
                    prefixNew,
                    hashInfo.Directory,
                    hashInfo.FileName
                  );

                  if (!_assetDict.ContainsKey(fileName)) {
                    // NodeAsset assetNew = new NodeAsset(
                    //   prefixNew + hashInfo.Directory + "/" + hashInfo.FileName,
                    //   prefixNew + hashInfo.Directory,
                    //   hashInfo.FileName, hashInfo
                    // );
                    _assetDict.Add(
                      prefixNew + hashInfo.Directory + "/" + hashInfo.FileName,
                      assetNew
                    );
                    fileDirs.Add(prefixNew + hashInfo.Directory);
                  }
                }

                if (hashInfo.FileState == HashFileInfo.State.Modified) {
                  NodeAsset assetMod = new NodeAsset(
                      prefixMod + hashInfo.Directory + "/" + hashInfo.FileName,
                      prefixMod + hashInfo.Directory,
                      hashInfo.FileName,
                      hashInfo
                  );
                  String fileName = String.Format(
                    "{0}{1}/{2}",
                    prefixMod,
                    hashInfo.Directory,
                    hashInfo.FileName
                  );

                  if (!_assetDict.ContainsKey(fileName)) {
                    // NodeAsset assetMod = new NodeAsset(
                    //   fileName, 
                    //   prefixMod + hashInfo.Directory, 
                    //   hashInfo.FileName, 
                    //   hashInfo
                    // );
                    _assetDict.Add(fileName, assetMod);
                    fileDirs.Add(prefixMod + hashInfo.Directory);
                  }
                }
              } else {
                if (hashInfo.Extension.ToUpper() != "GR2") continue;

                hashInfo.Directory = "/unknown/" + hashInfo.Source.Replace(".tor", String.Empty);
                NodeAsset assetUnn = new NodeAsset(
                  prefixUnn + hashInfo.Directory + "/" + hashInfo.Extension + "/"
                    + hashInfo.FileName + "." + hashInfo.Extension,
                    prefixUnn + hashInfo.Directory + "/" + hashInfo.Extension,
                  hashInfo.FileName + "." + hashInfo.Extension,
                  hashInfo
                );

                _assetDict.Add(
                  prefixUnn + hashInfo.Directory + "/" + hashInfo.Extension + "/"
                    + hashInfo.FileName + "." + hashInfo.Extension,
                  assetUnn
                );
                fileDirs.Add(prefixUnn + hashInfo.Directory + "/" + hashInfo.Extension);

                if (hashInfo.FileState == HashFileInfo.State.New) {
                  NodeAsset assetNew = new NodeAsset(
                    prefixNew + hashInfo.Directory + "/" + hashInfo.Extension + "/"
                      + hashInfo.FileName + "." + hashInfo.Extension,
                    prefixNew + hashInfo.Directory + "/" + hashInfo.Extension,
                    hashInfo.FileName + "." + hashInfo.Extension,
                    hashInfo
                  );

                  _assetDict.Add(
                    prefixNew + hashInfo.Directory + "/" + hashInfo.Extension + "/"
                      + hashInfo.FileName + "." + hashInfo.Extension,
                    assetNew
                  );
                  fileDirs.Add(prefixNew + hashInfo.Directory + "/" + hashInfo.Extension);
                }

                if (hashInfo.FileState == HashFileInfo.State.Modified) {
                  NodeAsset assetMod = new NodeAsset(
                    prefixMod + hashInfo.Directory + "/" + hashInfo.Extension + "/"
                      + hashInfo.FileName + "." + hashInfo.Extension,
                    prefixMod + hashInfo.Directory + "/" + hashInfo.Extension,
                    hashInfo.FileName + "." + hashInfo.Extension,
                    hashInfo
                  );

                  _assetDict.Add(
                    prefixMod + hashInfo.Directory + "/" + hashInfo.Extension + "/"
                      + hashInfo.FileName + "." + hashInfo.Extension,
                    assetMod
                  );
                  fileDirs.Add(prefixMod + hashInfo.Directory + "/" + hashInfo.Extension);
                }
              }
            }
          }

          libsDone++;
          backgroundWorker2.ReportProgress(libsDone * 100 / totalLibs);
        }
      }
      #endregion

      _assetDict.Add("/", new NodeAsset("/", String.Empty, "Root", null));
      _assetDict.Add("/assets", new NodeAsset("/assets", "/", "Assets", null));
      _assetDict.Add("/nodes", new NodeAsset("/nodes", "/", "Nodes", null));


      foreach (String dir in fileDirs) {
        String[] temp = dir.Split('/');
        Int32 intLength = temp.Length;

        for (Int32 intCount2 = 0; intCount2 <= intLength; intCount2++) {
          String output = String.Join("/", temp, 0, intCount2);

          if (output.Length > 0) allDirs.Add(output);
        }
      }

      foreach (String dir in allDirs) {
        String[] temp = dir.Split('/');
        String parentDir = String.Join("/", temp.Take(temp.Length - 1));

        if (parentDir.Length == 0) parentDir = "/assets";

        String display = temp.Last();
        NodeAsset asset = new NodeAsset(dir, parentDir, display, null);

        if (!_assetDict.ContainsKey(dir)) _assetDict.Add(dir, asset);
      }
    }
    private void BuildComparedGr2Assets(HashSet<String> fileDirs) {
      List<BuildFileDifference> differences = BuildAssetComparer
        .Compare(_currentAssets, _previousAssets)
        .Where(x => x.DisplayRecord?.HashInfo != null
          && x.DisplayRecord.HashInfo.Extension.Equals("GR2", StringComparison.OrdinalIgnoreCase))
        .ToList();

      foreach (BuildFileDifference difference in differences) {
        HashFileInfo info = difference.DisplayRecord.HashInfo;
        String prefix = difference.State switch {
          BuildFileState.New => "/assets/new",
          BuildFileState.Changed => "/assets/changed",
          BuildFileState.Removed => "/assets/removed",
          _ => "/assets"
        };

        String directory;
        String displayName;
        if (info.IsNamed) {
          directory = info.Directory;
          displayName = info.FileName;
        } else {
          directory = "/unknown/" + info.Source.Replace(".tor", String.Empty)
            + "/" + info.Extension;
          displayName = info.FileName + "." + info.Extension;
        }

        String parent = prefix + directory;
        String id = parent + "/" + displayName;
        if (_assetDict.ContainsKey(id)) id += " [" + info.Source + "]";

        NodeAsset asset = new NodeAsset(id, parent, displayName, info) {
          compareState = difference.State,
          previousHashInfo = difference.Previous?.HashInfo
        };
        _assetDict.Add(id, asset);
        fileDirs.Add(parent);
      }

      backgroundWorker2.ReportProgress(100);
    }

    private void BackgroundWorker2Completed(Object sender, RunWorkerCompletedEventArgs e) {
      if (_closing) return;

      if (e.Error != null) {
        throw new Exception("Echter Fehler beim Laden (Worker 2): " + e.Error, e.Error);
      }

      ParseTestRules();

      ProgressBarValue(0);
      ProgressBarStyle(System.Windows.Forms.ProgressBarStyle.Marquee);
      StatusBarText("Loading Tree View Items ...");

      backgroundWorker3.RunWorkerAsync();
    }
    private void BackgroundWorker3Run(Object sender, DoWorkEventArgs e) {
      if (_closing) return;

      String getId(NodeAsset x) => x.id;
      String getParentId(NodeAsset x) => x.parentId;
      String getDisplayName(NodeAsset x) => x.displayName;

      treeViewFast1.BeginUpdate();
      treeViewFast1.LoadItems<NodeAsset>(_assetDict, getId, getParentId, getDisplayName);
      treeViewFast1.EndUpdate();
      TreeViewFast1Show();
    }
    private void BackgroundWorker3RunWorkerCompleted(Object sender, RunWorkerCompletedEventArgs e) {
      if (_closing) return;

      if (e.Error != null) {
        throw new Exception("Echter Fehler beim Laden (Worker 3): " + e.Error, e.Error);
      }

      // treeViewFast1.Visible = true;

      ProgressBarHide();
      StatusBarText(
        _compareFiles
          ? "Comparison loaded. GR2 Assets show New, Changed and Removed files."
          : "Loading Complete."
      );
      ProgressBarValue(0);
      ProgressBarStyle(System.Windows.Forms.ProgressBarStyle.Continuous);

      _panelRender = new View_NPC_GR2(Handle, this, "renderPanel");
      _panelRender.Init();

      System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.Interactive;

      LoadingSwirlHide();
      RenderPanelShow();
      ButtonsEnable();

      if (treeViewFast1.Nodes.Count > 0) {
        treeViewFast1.Nodes[0].Expand();

        if (treeViewFast1.Nodes[0].Nodes.Count > 0)
          foreach (TreeNode node in treeViewFast1.Nodes[0].Nodes)
            node.Expand();
      }
    }
    #endregion

    #region Buttons
    private void BtnExportClick(Object sender, EventArgs e) {
      // _panelRender.ExportGeometry(Bodytype);
    }
    private void BtnHelpClick(Object sender, EventArgs e) {
      ModelBrowserHelp helpForm = new ModelBrowserHelp();
      helpForm.Show();
    }
    private void BtnHideDataClick(Object sender, EventArgs e) {
      Boolean current = splitContainer3.Panel2Collapsed;

      if (current) {
        splitContainer3.Panel2Collapsed = false;
        btnToggleData.Text = "Hide Data Panel";
      } else {
        splitContainer3.Panel2Collapsed = true;
        btnToggleData.Text = "Show Data Panel";
      }
    }
    private void BtnStopRenderClick(Object sender, EventArgs e) {
      if (_panelRender != null) {
        RenderPanelHide();

        if (_render != null) {
          _panelRender.StopRender();
          _render.Join();
          _panelRender.Clear();
        }
      }
    }
    private void ButtonsEnable() {
      btnStopRender.Enabled = true;
      btnToggleData.Enabled = true;
      btnHelp.Enabled = true;
    }
    #endregion

    #region Context Menu Strips
    private void ContextMenuStrip2Click(Object sender, EventArgs evnt) {
      _bodyType = ((ToolStripMenuItem)sender).Name;
    }
    #endregion

    #region DataGridView
    private void DataGridViewBuild() {
      if (InvokeRequired) Invoke(new Action(() => DataGridViewBuild()));
      else {
        TreeViewFast2Clear();
        DataGridViewClear();

        _dataViewDict ??= new Dictionary<String, NodeAsset>();

        _dataViewDict.Clear();
        _dataViewDict.Add("/", new NodeAsset("/", "", "Root", null));

        if (_models != null && _models.Count > 0) {
          _dataViewDict.Add("/models", new NodeAsset("/models", "/", "Models", null));

          foreach (KeyValuePair<String, GR2> model in _models) {
            NodeAsset asset = new NodeAsset(
              "/models/" + model.Key, "/models", model.Key, model.Value
            );
            _dataViewDict.Add("/models/" + model.Key, asset);

            if (model.Value.attachedModels.Count > 0) {
              _dataViewDict.Add(
                "/models/" + model.Key + "/attached",
                new NodeAsset(
                  "/models/" + model.Key + "/attached",
                  "/models/" + model.Key,
                  "Attached Models",
                  null
                )
              );

              foreach (GR2 attach in model.Value.attachedModels) {
                NodeAsset attachAsset = new NodeAsset(
                "/models/" + model.Key + "/attached/" + attach.filename,
                "/models/" + model.Key + "/attached",
                attach.filename,
                attach
              );
                _dataViewDict.Add(
                  "/models/" + model.Key + "/attached/" + attach.filename,
                  attachAsset
                );

                if (attach.numMaterials > 0) {
                  if (!_dataViewDict.ContainsKey(
                    "/models/" + model.Key + "/attached/" + attach.filename + "/materials"))
                    _dataViewDict.Add(
                      "/models/" + model.Key + "/attached/" + attach.filename + "/materials",
                      new NodeAsset(
                        "/models/" + model.Key + "/attached/" + attach.filename + "/materials",
                        "/models/" + model.Key + "/attached/" + attach.filename,
                        "Materials",
                        null
                      )
                    );

                  foreach (GR2_Material material in attach.materials) {
                    NodeAsset attachMaterial = new NodeAsset(
                    "/models/" + model.Key + "/attached/" + attach.filename + "/materials/"
                      + material.materialName,
                    "/models/" + model.Key + "/attached/" + attach.filename + "/materials",
                    material.materialName,
                    material
                  );

                    if (!_dataViewDict.ContainsKey(
                      "/models/" + model.Key + "/attached/" + attach.filename + "/materials/"
                        + material.materialName))
                      _dataViewDict.Add(
                        "/models/" + model.Key + "/attached/" + attach.filename + "/materials/"
                          + material.materialName,
                        attachMaterial
                      );
                  }
                }

                if (attach.numMeshes > 0) {
                  if (!_dataViewDict.ContainsKey(
                    "/models/" + model.Key + "/attached/" + attach.filename + "/meshes"))
                    _dataViewDict.Add(
                      "/models/" + model.Key + "/attached/" + attach.filename + "/meshes",
                      new NodeAsset(
                        "/models/" + model.Key + "/attached/" + attach.filename + "/meshes",
                        "/models/" + model.Key + "/attached/" + attach.filename, "Meshes",
                        null
                      )
                    );

                  foreach (GR2_Mesh mesh in attach.meshes) {
                    NodeAsset meshAsset = new NodeAsset(
                      "/models/" + model.Key + "/meshes/" + mesh.meshName,
                      "/models/" + model.Key + "/attached/" + attach.filename + "/meshes",
                      mesh.meshName,
                      mesh
                    );

                    if (!_dataViewDict.ContainsKey(
                      "/models/" + model.Key + "/meshes/" + mesh.meshName))
                      _dataViewDict.Add(
                        "/models/" + model.Key + "/meshes/" + mesh.meshName,
                        meshAsset
                      );
                  }
                }
              }
            }

            if (model.Value.meshes != null && model.Value.meshes.Count > 0) {
              if (!_dataViewDict.ContainsKey("/models/" + model.Key + "/meshes"))
                _dataViewDict.Add(
                  "/models/" + model.Key + "/meshes",
                  new NodeAsset(
                    "/models/" + model.Key + "/meshes",
                    "/models/" + model.Key, "Meshes",
                    null
                  )
                );

              foreach (GR2_Mesh mesh in model.Value.meshes) {
                NodeAsset meshAsset = new NodeAsset(
                  "/models/" + model.Key + "/meshes/" + mesh.meshName, "/models/" + model.Key
                    + "/meshes",
                    mesh.meshName,
                    mesh
                  );

                if (!_dataViewDict.ContainsKey(
                  "/models/" + model.Key + "/meshes/" + mesh.meshName))
                  _dataViewDict.Add(
                    "/models/" + model.Key + "/meshes/" + mesh.meshName,
                    meshAsset
                  );
              }
            }

            if (model.Value.materials != null && model.Value.materials.Count > 0) {
              if (!_dataViewDict.ContainsKey("/models/" + model.Key + "/materials"))
                _dataViewDict.Add(
                  "/models/" + model.Key + "/materials",
                  new NodeAsset(
                    "/models/" + model.Key + "/materials",
                    "/models/" + model.Key,
                    "Materials",
                    null
                  )
                );

              foreach (GR2_Material material in model.Value.materials) {
                NodeAsset materialAsset = new NodeAsset(
                  "/models/" + model.Key + "/materials/" + material.materialName,
                  "/models/" + model.Key + "/materials",
                  material.materialName,
                  material
                );

                if (!_dataViewDict.ContainsKey(
                  "/models/" + model.Key + "/materials/" + material.materialName))
                  _dataViewDict.Add(
                    "/models/" + model.Key + "/materials/" + material.materialName,
                    materialAsset
                  );
              }
            }

            if (model.Value.numBones > 0) {
              if (!_dataViewDict.ContainsKey("/models/" + model.Key + "/bones"))
                _dataViewDict.Add(
                  "/models/" + model.Key + "/bones",
                  new NodeAsset(
                    "/models/" + model.Key + "/bones",
                    "/models/" + model.Key,
                    "Bones",
                    null
                  )
                );

              foreach (GR2_Bone_Skeleton bone in model.Value.skeleton_bones) {
                NodeAsset materialAsset = new NodeAsset(
                  "/models/" + model.Key + "/bones/" + bone.boneName,
                  "/models/" + model.Key + "/bones",
                  bone.boneIndex.ToString() + " - " + bone.boneName,
                  bone
                );

                if (!_dataViewDict.ContainsKey("/models/" + model.Key + "/bones/" + bone.boneName))
                  _dataViewDict.Add(
                    "/models/" + model.Key + "/bones/" + bone.boneName, materialAsset
                  );
              }
            }
          }
        }

        if (_resources != null && _resources.Count > 0) {
          _dataViewDict.Add("/resources", new NodeAsset("/resources", "/", "Resources", null));

          foreach (KeyValuePair<String, Object> resource in _resources) {
            NodeAsset asset = new NodeAsset(
              "/resources/" + resource.Key,
              "/resources",
              resource.Key,
              null
            );
            _dataViewDict.Add("/resources/" + resource.Key, asset);
          }
        }

        String getId(NodeAsset x) => x.id;
        String getParentId(NodeAsset x) => x.parentId;
        String getDisplayName(NodeAsset x) => x.displayName;

        treeViewFast2.SuspendLayout();
        treeViewFast2.BeginUpdate();
        treeViewFast2.LoadItems<NodeAsset>(_dataViewDict, getId, getParentId, getDisplayName);
        treeViewFast2.Sort();
        treeViewFast2.EndUpdate();
        treeViewFast2.ResumeLayout();
        treeViewFast2.Enabled = true;
        treeViewFast2.Nodes[0].Expand();

        DataGridViewEnable();
      }
    }
    private void DataGridViewClear() {
      if (dataGridView1.InvokeRequired)
        dataGridView1.Invoke(new Action(() => DataGridViewClear()));
      else
        dataGridView1.DataSource = null;
    }
    private void DataGridViewDisable() {
      if (dataGridView1.InvokeRequired)
        dataGridView1.Invoke(new Action(() => DataGridViewDisable()));
      else
        dataGridView1.Enabled = false;
    }
    private void DataGridViewEnable() {
      if (dataGridView1.InvokeRequired)
        dataGridView1.Invoke(new Action(() => DataGridViewEnable()));
      else
        dataGridView1.Enabled = true;
    }
    #endregion

    #region Loaders
    private void LoadIPP(ItemAppearance itemData) {
      _models ??= new Dictionary<String, GR2>();
      _resources ??= new Dictionary<String, Object>();
      if (itemData?.IPP == null) return;
      String model = _currentDom.AppearanceLoader.ApplyPartsMacros(itemData.IPP.Model, _bodyType).Replace('\\', '/');

      if (!String.IsNullOrWhiteSpace(model) && model.Contains(".gr2", StringComparison.OrdinalIgnoreCase)) {
        File modelFile = _currentAssets.FindFile("/resources" + model);

        if (modelFile != null) {
          using BinaryReader br = new BinaryReader(modelFile.OpenCopyInMemory());
          String name = model.Split('/').Last();
          GR2 gr2Model = new GR2(br, name);

          String mat0 = itemData.IPP.Material0;
          String matMirror = itemData.IPP.MaterialMirror;

          String palette1XML = "";
          String palette2XML = "";

          if (!String.IsNullOrEmpty(mat0)) {
            if (!String.IsNullOrEmpty(itemData.IPP.PrimaryHue))
              palette1XML = "/resources" + itemData.IPP.PrimaryHue.Split(';').First();

            if (!String.IsNullOrEmpty(itemData.IPP.SecondaryHue))
              palette2XML = "/resources" + itemData.IPP.SecondaryHue.Split(';').First();

            mat0 = _currentDom.AppearanceLoader.ApplyPartsMacros(mat0, _bodyType);
            matMirror = _currentDom.AppearanceLoader.ApplyPartsMacros(matMirror, _bodyType);

            if (gr2Model.numMaterials == 0) {
              gr2Model.numMaterials = 1;
              gr2Model.materials = new List<GR2_Material> { new GR2_Material(mat0) };

              if (!String.IsNullOrEmpty(palette1XML))
                gr2Model.materials[0].palette1XML = palette1XML;

              if (!String.IsNullOrEmpty(palette2XML))
                gr2Model.materials[0].palette2XML = palette2XML;

            } else if (gr2Model.numMaterials == 1) {
              gr2Model.materials[0] = new GR2_Material(mat0);

              if (!String.IsNullOrEmpty(palette1XML))
                gr2Model.materials[0].palette1XML = palette1XML;

              if (!String.IsNullOrEmpty(palette2XML))
                gr2Model.materials[0].palette2XML = palette2XML;

            } else if (gr2Model.numMaterials == 2) {
              gr2Model.materials[0] = new GR2_Material(mat0);

              if (!String.IsNullOrEmpty(palette1XML))
                gr2Model.materials[0].palette1XML = palette1XML;

              if (!String.IsNullOrEmpty(palette2XML))
                gr2Model.materials[0].palette2XML = palette2XML;

              String appSlot = model.Split('/').Last().Split('_').First();

              if (!String.IsNullOrEmpty(matMirror))
                matMirror = appSlot + "_naked_caucasian_young_a01c01_" + _bodyType;

              gr2Model.materials[1] = new GR2_Material(matMirror);
            }
          }

          if (itemData.IPP.AttachedModels.Count > 0) {
            foreach (String attach in itemData.IPP.AttachedModels) {
              String attachFileName = _currentDom.AppearanceLoader.ApplyPartsMacros(attach, _bodyType).Replace('\\', '/');
              File attachFile = _currentAssets.FindFile("/resources" + attachFileName);

              if (attachFile != null) {
                using BinaryReader br2 = new BinaryReader(attachFile.OpenCopyInMemory());
                String attachName = attachFileName.Split('/').Last();
                GR2 attachModel = new GR2(br2, attachName);

                if (attachModel.numMaterials == 0) {
                  attachModel.numMaterials = 1;
                  attachModel.materials = new List<GR2_Material> { new GR2_Material(mat0) };

                  if (!String.IsNullOrEmpty(palette1XML))
                    attachModel.materials[0].palette1XML = palette1XML;

                  if (!String.IsNullOrEmpty(palette2XML))
                    attachModel.materials[0].palette2XML = palette2XML;

                } else if (attachModel.numMaterials == 1) {
                  attachModel.materials[0] = new GR2_Material(mat0);

                  if (!String.IsNullOrEmpty(palette1XML))
                    attachModel.materials[0].palette1XML = palette1XML;

                  if (!String.IsNullOrEmpty(palette2XML))
                    attachModel.materials[0].palette2XML = palette2XML;

                } else if (attachModel.numMaterials == 2) {
                  attachModel.materials[0] = new GR2_Material(mat0);

                  if (!String.IsNullOrEmpty(palette1XML))
                    attachModel.materials[0].palette1XML = palette1XML;

                  if (!String.IsNullOrEmpty(palette2XML))
                    attachModel.materials[0].palette2XML = palette2XML;

                  String appSlot = attachFileName.Split('/').Last().Split('_').First();

                  if (!String.IsNullOrEmpty(matMirror))
                    matMirror = appSlot + "_naked_caucasian_young_a01c01_" + _bodyType;

                  attachModel.materials[1] = new GR2_Material(matMirror);
                }

                attachModel.transformMatrix = Matrix.Scaling(new Vector3(1.0F, 1.0F, 1.0F));
                gr2Model.attachedModels.Add(attachModel);
              }
            }
          }

          gr2Model.transformMatrix = Matrix.Scaling(new Vector3(1.0F, 1.0F, 1.0F));
          _models.Add(model[(model.LastIndexOf('/') + 1)..], gr2Model);
        }
      }

      if (model.Contains(".dds", StringComparison.OrdinalIgnoreCase)) {
        File textureFile = _currentAssets.FindFile("/resources" + model);
        if (textureFile != null) {
          using (textureFile) {
            using Stream inputStream = textureFile.OpenCopyInMemory();
            if (inputStream != null)
              _resources[model[(model.LastIndexOf('/') + 1)..]] = inputStream;
          }
        }
      }
    }
    #endregion

    #region LoadingSwirl
    private void LoadingSwirlHide() {
      if (InvokeRequired) Invoke(new Action(() => LoadingSwirlHide()));
      else loadingSwirl1.Visible = false;
    }
    private void LoadingSwirlShow() {
      if (InvokeRequired) Invoke(new Action(() => LoadingSwirlShow()));
      else loadingSwirl1.Visible = true;
    }
    #endregion

    #region Parsers
    private static Boolean TryParseFxFloat(String value, out Single result) {
      result = 0.0F;
      if (String.IsNullOrWhiteSpace(value))
        return false;

      value = value.Trim();

      // SWTOR FxSpec numeric values are stored with a dot decimal separator,
      // independent of the Windows UI locale.
      if (Single.TryParse(
            value,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out result))
        return true;

      return Single.TryParse(
        value,
        System.Globalization.NumberStyles.Float,
        System.Globalization.CultureInfo.CurrentCulture,
        out result
      );
    }

    private static Boolean TryParseFxVector3(String text, out Vector3 result) {
      result = new Vector3();
      if (String.IsNullOrWhiteSpace(text))
        return false;

      String cleaned = text
        .Replace("(", " ")
        .Replace(")", " ")
        .Replace("[", " ")
        .Replace("]", " ")
        .Trim();

      // Normal FxSpecs use commas. A few newer marshalled values use
      // semicolons or plain whitespace instead.
      String[] parts = cleaned
        .Split(new Char[] { ',', ';', ' ', '\t', '\r', '\n' },
               StringSplitOptions.RemoveEmptyEntries);

      if (parts.Length < 3)
        return false;

      if (!TryParseFxFloat(parts[0], out Single x)
          || !TryParseFxFloat(parts[1], out Single y)
          || !TryParseFxFloat(parts[2], out Single z))
        return false;

      result = new Vector3(x, y, z);
      return true;
    }

    private void ParseFxSpec(String fxspec, String type = "") {
      if (fxspec == null) return;

      // Standardise filepath formatting
      fxspec = fxspec.StartsWith("/") ? fxspec[1..] : fxspec;
      fxspec += fxspec.Contains(".fxspec") ? "" : ".fxspec";

      // Define full filepath
      fxspec = fxspec.Replace("\\", "/");
      String normalizedFxSpec = fxspec.StartsWith("/") ? fxspec : "/resources/art/fx/fxspec/" + fxspec;
      if (!normalizedFxSpec.EndsWith(".fxspec", StringComparison.OrdinalIgnoreCase)) normalizedFxSpec += ".fxspec";
      if (!_parsedFxSpecs.Add(normalizedFxSpec)) return;
      File fxFile = _currentAssets.FindFile(normalizedFxSpec);

      // If filepath is invalid, return
      if (fxFile == null) return;

      // Load FxSpec as XML document
      XmlDocument xmlDoc = new XmlDocument();
      xmlDoc.Load(fxFile.OpenCopyInMemory());

      // Load the emitter list from the FxSpec
      XmlNode emitterList =
            xmlDoc.SelectSingleNode("/nodeWClasses/marshalData/node/f[@name='_fxEmitterList']");

      // Load the model list from the FxSpec
      XmlNode modelList =
            xmlDoc.SelectSingleNode("//*[@name='_fxModelList']");

      // Relative transforms
      Vector3 relPosVec = new Vector3();
      Vector3 relRotVec = new Vector3();

      // Newer MNT/glider FxSpecs can be wrapper specs. Follow displayName
      // references to the actual visual FxSpec.
      foreach (XmlNode displayNode in xmlDoc.SelectNodes("//*[@name='displayName']")) {
        String referenced = displayNode.InnerText?.Trim();
        if (String.IsNullOrWhiteSpace(referenced)) continue;
        if (referenced.EndsWith(".fxspec", StringComparison.OrdinalIgnoreCase))
          ParseFxSpec(referenced, type);
        else if (type == "mnt")
          ParseFxSpec(referenced + ".fxspec", type);
      }

      // Vehicle/glider FxSpecs are often wrappers.  Some of them reference
      // another FxSpec through fields other than displayName/_fxResourceName.
      // Follow every textual *.fxspec reference in the document. _parsedFxSpecs
      // prevents cycles.
      if (type == "mnt") {
        XmlNodeList allFxNodes = xmlDoc.SelectNodes("//*[not(*)]");
        if (allFxNodes != null) {
          foreach (XmlNode valueNode in allFxNodes) {
            String valueText = valueNode.InnerText?.Trim().Replace("\\", "/");
            if (String.IsNullOrWhiteSpace(valueText)) continue;

            Int32 fxIndex = valueText.IndexOf(".fxspec", StringComparison.OrdinalIgnoreCase);
            if (fxIndex < 0) continue;

            // Values are normally a single path. Strip simple surrounding
            // punctuation used by marshalled string representations.
            String nestedFx = valueText.Substring(0, fxIndex + 7)
              .Trim(' ', '\t', '\r', '\n', '"', '\'', '{', '}', '[', ']', '(', ')');

            if (!String.IsNullOrWhiteSpace(nestedFx))
              ParseFxSpec(nestedFx, type);
          }
        }
      }

      // Collect every GR2 resource in the whole FxSpec. This covers nested
      // visual resources used by newer gliders.
      XmlNodeList fallbackResources = xmlDoc.SelectNodes("//*[@name='_fxResourceName']");
      if (fallbackResources != null) {
        foreach (XmlNode resourceNode in fallbackResources) {
          String resourceName = resourceNode.InnerText?.Trim().Replace("\\", "/");
          if (String.IsNullOrWhiteSpace(resourceName) || !resourceName.EndsWith(".gr2", StringComparison.OrdinalIgnoreCase)) continue;
          if (resourceName.IndexOf("vfx", StringComparison.OrdinalIgnoreCase) >= 0 || resourceName.IndexOf("spawn", StringComparison.OrdinalIgnoreCase) >= 0 || resourceName.IndexOf("_distortion", StringComparison.OrdinalIgnoreCase) >= 0) continue;
          String modelPath = resourceName.StartsWith("/resources/", StringComparison.OrdinalIgnoreCase) ? resourceName : "/resources" + (resourceName.StartsWith("/") ? resourceName : "/" + resourceName);
          File attachModel = _currentAssets.FindFile(modelPath);
          if (attachModel == null) continue;
          String name = modelPath.Split('/').Last();
          if (_models.ContainsKey(name)) continue;
          using BinaryReader br = new BinaryReader(attachModel.OpenCopyInMemory());
          _models[name] = new GR2(br, name) { transformMatrix = Matrix.Identity };
        }
      }

      // A few current vehicle specs store their model path in another field
      // entirely. As a final MNT fallback, inspect every leaf value containing
      // a .gr2 path and load it as a root model.
      if (type == "mnt") {
        XmlNodeList allValueNodes = xmlDoc.SelectNodes("//*[not(*)]");
        if (allValueNodes != null) {
          foreach (XmlNode valueNode in allValueNodes) {
            String rawValue = valueNode.InnerText?.Trim().Replace("\\", "/");
            if (String.IsNullOrWhiteSpace(rawValue)) continue;

            Int32 gr2Index = rawValue.IndexOf(".gr2", StringComparison.OrdinalIgnoreCase);
            if (gr2Index < 0) continue;

            String resourceName = rawValue.Substring(0, gr2Index + 4)
              .Trim(' ', '\t', '\r', '\n', '"', '\'', '{', '}', '[', ']', '(', ')');

            if (resourceName.IndexOf("vfx", StringComparison.OrdinalIgnoreCase) >= 0
                || resourceName.IndexOf("spawn", StringComparison.OrdinalIgnoreCase) >= 0
                || resourceName.IndexOf("_distortion", StringComparison.OrdinalIgnoreCase) >= 0)
              continue;

            String modelPath = resourceName.StartsWith("/resources/", StringComparison.OrdinalIgnoreCase)
              ? resourceName
              : "/resources" + (resourceName.StartsWith("/") ? resourceName : "/" + resourceName);

            File modelFile = _currentAssets.FindFile(modelPath);
            if (modelFile == null) continue;

            String name = modelPath.Split('/').Last();
            if (_models.ContainsKey(name)) continue;

            using BinaryReader br = new BinaryReader(modelFile.OpenCopyInMemory());
            _models[name] = new GR2(br, name) {
              transformMatrix = Matrix.Identity,
              scaleMatrix = Matrix.Identity
            };
          }
        }
      }

      // Some newer mount FxSpecs do not contain an emitter list. The model
      // list is still useful on its own.
      if (modelList == null || modelList.ChildNodes.Count < 1) return;

      foreach (XmlNode modelNode in modelList.ChildNodes) {
        // Ignore models that explicitly never start. Older/newer mount FxSpecs
        // may omit this optional field, in which case the model should load.
        XmlNode whenToStartNode =
          modelNode.SelectSingleNode("./node()[@name='_fxWhenToStart']");
        if (whenToStartNode != null && whenToStartNode.InnerText == "NEVER")
          continue;

        // Get the resource name
        XmlNode resourceNameNode =
          modelNode.SelectSingleNode("./node()[@name='_fxResourceName']");
        XmlNode resourceFxName =
          modelNode.SelectSingleNode("./node()[@name='_fxName']");

        if (resourceNameNode == null || String.IsNullOrWhiteSpace(resourceNameNode.InnerText))
          continue;

        String resourceName = resourceNameNode.InnerText.Trim();
        // Some vehicle/glider FxSpecs omit _fxName. Use the model filename as
        // the dictionary key instead of aborting the complete FxSpec parse.
        String modelKey = resourceFxName != null && !String.IsNullOrWhiteSpace(resourceFxName.InnerText)
          ? resourceFxName.InnerText
          : resourceName.Replace("\\", "/").Split('/').Last().Split('.').First();

        // Transform vectors
        Vector3 positionVec = new Vector3();
        Vector3 rotationVec = new Vector3();

        // Missing _fxScale means identity scale.  The old zero vector made
        // otherwise valid vehicle models collapse to a point.
        Vector3 scaleVec = new Vector3(1.0F, 1.0F, 1.0F);

        // Transform vector nodes
        XmlNode positionVecNode =
          modelNode.SelectSingleNode("./node()[@name='_fxAttachPosition']");
        XmlNode rotationVecNode =
          modelNode.SelectSingleNode("./node()[@name='_fxAttachRotation']");
        XmlNode scaleVecNode =
          modelNode.SelectSingleNode("./node()[@name='_fxScale']");

        // Attach nodes
        XmlNode attachToNode =
          modelNode.SelectSingleNode("./node()[@name='_fxAttachTo']");
        XmlNode attachRelativeNode =
          modelNode.SelectSingleNode("./node()[@name='_fxAttachRelative']");
        XmlNode boneAttachNode =
          modelNode.SelectSingleNode("./node()[@name='_fxAttachBone']");

        // Parent nodes
        XmlNode parentAttachToNode = null;
        XmlNode parentBoneAttachNode = null;
        XmlNode parentPositionVecNode = null;
        XmlNode parentRotationVecNode = null;

        // Ignore certain models
        if (resourceName.Contains("vfx") || resourceName.Contains("spawn")
            || resourceName.Contains("fx_all_lasersight_flare")
            || resourceName.Contains("_distortion") || resourceName.Contains("bh_jetpack"))
          continue;

        // Hide creature handles and weapon crystals
        if (resourceFxName != null &&
            (resourceFxName.InnerText.Contains("handle")
             || resourceFxName.InnerText.Contains("m_crystal")))
          continue;

        // Check the resource name is valid
        if (resourceName.Contains(".gr2")) {
          // Standardise model filepath
          String normalizedResource = resourceName.Replace("\\", "/");
          String modelPath = normalizedResource.StartsWith("/resources/", StringComparison.OrdinalIgnoreCase)
            ? normalizedResource
            : "/resources" + (normalizedResource.StartsWith("/") ? normalizedResource : "/" + normalizedResource);

          // Find the model file in the game assets
          File attachModel = _currentAssets.FindFile(modelPath);

          // Check the model file was successfully found
          if (attachModel != null) {
            // Open the mode file
            using BinaryReader br = new BinaryReader(attachModel.OpenCopyInMemory());
            String name = modelPath.Split('/').Last();
            GR2 gr2Model = new GR2(br, name);

            // Parse the emitter node chain
            XmlNode emitterNode = null;
            if (emitterList != null && attachToNode != null) {
              emitterNode =
                emitterList.SelectSingleNode(
                  ".//node()[@name='_fxName' and text() = '" + attachToNode.InnerText + "']");
            }

            if (emitterNode != null) {
              parentAttachToNode =
                emitterNode.ParentNode.SelectSingleNode("./node()[@name='_fxAttachTo']");
              parentBoneAttachNode =
                emitterNode.ParentNode.SelectSingleNode("./node()[@name='_fxAttachBone']");
              parentPositionVecNode =
                emitterNode.ParentNode.SelectSingleNode("./node()[@name='_fxAttachPosition']");
              parentRotationVecNode =
                emitterNode.ParentNode.SelectSingleNode("./node()[@name='_fxAttachRotation']");

              // Position Transform
              if (parentPositionVecNode != null
                  && TryParseFxVector3(parentPositionVecNode.InnerText, out Vector3 parentPos))
                positionVec += parentPos;

              // Rotation Transform
              if (parentRotationVecNode != null
                  && TryParseFxVector3(parentRotationVecNode.InnerText, out Vector3 parentRot))
                rotationVec += parentRot;

              ParseFxSpecEmitters(
                emitterNode,
                emitterList,
                ref positionVec,
                ref rotationVec,
                type);
            }

            // Check if attachments should be relative
            if (attachRelativeNode != null && attachRelativeNode.InnerText == "true"
                && resourceFxName != null && resourceFxName.InnerText == "speeder") {
              XmlNode startLocNode =
                modelNode.SelectSingleNode("./node()[@name='_fxStartLocOffset']");
              if (startLocNode != null
                  && TryParseFxVector3(startLocNode.InnerText, out Vector3 relPos))
                relPosVec = relPos;

              XmlNode fxRotationNode =
                modelNode.SelectSingleNode("./node()[@name='_fxRotation']");
              if (fxRotationNode != null
                  && TryParseFxVector3(fxRotationNode.InnerText, out Vector3 relRot))
                relRotVec = relRot;
            }

            // Position Transform
            if (positionVecNode != null
                && TryParseFxVector3(positionVecNode.InnerText, out Vector3 parsedPosition)) {
              positionVec += parsedPosition;
              if (relPosVec != new Vector3()) positionVec += relPosVec;
            }

            // Rotation Transform
            if (rotationVecNode != null
                && TryParseFxVector3(rotationVecNode.InnerText, out Vector3 parsedRotation)) {
              rotationVec += parsedRotation;
              if (relRotVec != new Vector3()) rotationVec += relRotVec;
            }

            // Scale Transform
            if (scaleVecNode != null
                && TryParseFxVector3(scaleVecNode.InnerText, out Vector3 parsedScale))
              scaleVec = parsedScale;

            // Check if model is attached to a valid attachment point or bone
            if (parentBoneAttachNode != null && parentBoneAttachNode.InnerText != "") {
              foreach (KeyValuePair<String, GR2> model in _models) {
                GR2_Attachment attach = model.Value.attachments.SingleOrDefault(
                  x => x.attachName.ToLower() == parentBoneAttachNode.InnerText.ToLower());

                if (attach != null) gr2Model.attachMatrix = attach.attachMatrix;



                GR2_Bone_Skeleton boneAttach = model.Value.skeleton_bones?.SingleOrDefault(
                  x => x.boneName.ToLower() == parentBoneAttachNode.InnerText.ToLower());

                if (boneAttach != null) gr2Model.attachMatrix = boneAttach.root;
              }
            } else if (boneAttachNode != null && boneAttachNode.InnerText != "") {
              foreach (KeyValuePair<String, GR2> model in _models) {
                GR2_Attachment attach = model.Value.attachments.SingleOrDefault(
                  x => x.attachName.ToLower() == boneAttachNode.InnerText.ToLower());

                if (attach != null) gr2Model.attachMatrix = attach.attachMatrix;

                GR2_Bone_Skeleton boneAttach = model.Value.skeleton_bones.SingleOrDefault(
                  x => x.boneName.ToLower() == boneAttachNode.InnerText.ToLower());

                if (boneAttach != null) gr2Model.attachMatrix = boneAttach.root;
              }
            }

            // Axis adjustments
            if (type == "itm") {
              String itemName = fxFile.FilePath.Split('/').Last().Split('.').First();

              if (itemName.Contains("assaultcannon_")) {
                positionVec = new Vector3(positionVec.Z, positionVec.Y, positionVec.X);
                rotationVec = new Vector3(rotationVec.X, rotationVec.Z, rotationVec.Y);
                scaleVec = new Vector3(scaleVec.X, scaleVec.Z, scaleVec.Y);
              } else if (itemName.Contains("blaster_") || itemName.Contains("rifle_")) {
                positionVec = new Vector3(positionVec.X, positionVec.Z, positionVec.Y);
                rotationVec = new Vector3(rotationVec.X, rotationVec.Y, rotationVec.Z);
                scaleVec = new Vector3(scaleVec.X, scaleVec.Y, scaleVec.Z);
              } else {
                positionVec = new Vector3(positionVec.X, positionVec.Y, positionVec.Z);
                rotationVec = new Vector3(rotationVec.X, rotationVec.Z, rotationVec.Y);
                scaleVec = new Vector3(scaleVec.X, scaleVec.Z, scaleVec.Y);
              }
            }

            // Scale matrix from vector
            gr2Model.scaleMatrix = Matrix.Scaling(scaleVec);

            // Transform matrix from vectors
            gr2Model.transformMatrix =
              Matrix.Scaling(scaleVec)
                * Matrix.RotationZ((Single)(rotationVec.Z * Math.PI / 180.0))
                * Matrix.RotationX((Single)(rotationVec.X * Math.PI / 180.0))
                * Matrix.RotationY((Single)(rotationVec.Y * Math.PI / 180.0))
                * Matrix.Translation(positionVec);

            // Add model to models list
            _models[modelKey] = gr2Model;
          }
        }
      }

    }

    private void ParseFxSpecEmitters(XmlNode emitterNode,
                                     XmlNode emitterList,
                                     ref Vector3 positionVec,
                                     ref Vector3 rotationVec,
                                     String type) {
      XmlNode checkMe = emitterNode.ParentNode;

      XmlNode checkBoneNode = checkMe?.SelectSingleNode("./node()[@name='_fxAttachBone']");
      XmlNode checkAttachToNode = checkMe?.SelectSingleNode("./node()[@name='_fxAttachTo']");
      if (checkMe == null || checkAttachToNode == null) return;

      if ((checkBoneNode == null || checkBoneNode.InnerText == "")
          && checkAttachToNode.InnerText != "CASTER"
          && checkAttachToNode.InnerText != "TARGET") {
        XmlNode attachToNode =
          emitterNode.ParentNode.SelectSingleNode("./node()[@name='_fxAttachTo']");
        emitterNode =
          emitterList.SelectSingleNode(
            ".//node()[@name='_fxName' and text() = '" + attachToNode.InnerText + "']"
          );

        XmlNode parentAttachToNode;
        XmlNode parentPositionVecNode;
        XmlNode parentRotationVecNode;


        if (emitterNode == null) return;

        parentAttachToNode =
          emitterNode.ParentNode.SelectSingleNode("./node()[@name='_fxAttachTo']");
        parentPositionVecNode =
          emitterNode.ParentNode.SelectSingleNode("./node()[@name='_fxAttachPosition']");
        parentRotationVecNode =
          emitterNode.ParentNode.SelectSingleNode("./node()[@name='_fxAttachRotation']");

        // if (parentAttachToNode.InnerText != "CASTER" && parentAttachToNode.InnerText != "TARGET")
        if (type == "itm" || parentAttachToNode.InnerText != "CASTER"
            && parentAttachToNode.InnerText != "TARGET") {
          // Position Transform
          if (parentPositionVecNode != null
              && TryParseFxVector3(parentPositionVecNode.InnerText, out Vector3 emitterPos))
            positionVec += emitterPos;

          // Rotation Transform
          if (parentRotationVecNode != null
              && TryParseFxVector3(parentRotationVecNode.InnerText, out Vector3 emitterRot))
            rotationVec += emitterRot;
        }

        // Recursive
        ParseFxSpecEmitters(emitterNode, emitterList, ref positionVec, ref rotationVec, type);
      }
    }

    private void ParseNpcData(NpcAppearance npcData) {
      if (npcData == null) return;
      _models ??= new Dictionary<String, GR2>();
      _resources ??= new Dictionary<String, Object>();

      // Load the rig named by the character spec. The art-slot [bt] macro is intentionally NOT
      // used for the skeleton: creature specs can wear bmn/bfn art while retaining their own rig.
      String npcBodyType = npcData.BodyType ?? String.Empty;
      if (!String.IsNullOrWhiteSpace(npcBodyType)) {
        String skeletonModel = (npcBodyType.StartsWith("bf", StringComparison.OrdinalIgnoreCase)
                                || npcBodyType.StartsWith("bm", StringComparison.OrdinalIgnoreCase))
          ? "/resources/art/dynamic/spec/" + npcBodyType + "new_skeleton.gr2"
          : "/resources/art/dynamic/spec/" + npcBodyType + "_skeleton.gr2";
        File file = _currentAssets.FindFile(skeletonModel);
        if (file != null) {
          using BinaryReader br = new BinaryReader(file.OpenCopyInMemory());
          String name = skeletonModel.Split('/').Last();
          _models[name] = new GR2(br, name);
        }
      }

      if (npcData.AppearanceSlotMap == null) return;
      AppSlot headSlot = npcData.AppearanceSlotMap.TryGetValue("appSlotHead", out List<AppSlot> headSlots)
        ? headSlots?.FirstOrDefault(x => x != null)
        : null;

      // Jedipedia renders one deterministic variant per static NPP preview. Older appearances can
      // contain several weighted alternatives; the previous `Count == 1 ... else break` discarded
      // this slot and every slot following it, which made many RED NPPs completely model-less.
      foreach (KeyValuePair<String, List<AppSlot>> appSlot in npcData.AppearanceSlotMap) {
        AppSlot slot = appSlot.Value?.FirstOrDefault(x => x != null);
        if (slot == null) continue;
        String bodyType = !String.IsNullOrWhiteSpace(slot.BodyType) ? slot.BodyType : npcBodyType;
        String model = _currentDom.AppearanceLoader.ApplyPartsMacros(slot.Model, bodyType);

        if (appSlot.Key.IndexOf("FaceHair", StringComparison.OrdinalIgnoreCase) >= 0
            && String.IsNullOrWhiteSpace(model))
          model = "/art/defaultassets/blank.gr2";

        if (!String.IsNullOrWhiteSpace(model) && model.EndsWith(".gr2", StringComparison.OrdinalIgnoreCase)) {
          File modelFile = _currentAssets.FindFile("/resources" + model.Replace('\\', '/'));
          if (modelFile != null) {
            using BinaryReader br = new BinaryReader(modelFile.OpenCopyInMemory());
            String name = model.Split('/', '\\').Last();
            GR2 gr2Model = new GR2(br, name);

            String material0 = _currentDom.AppearanceLoader.ApplyPartsMacros(slot.Material0, bodyType);
            String materialMirror = _currentDom.AppearanceLoader.ApplyPartsMacros(slot.MaterialMirror, bodyType);

            // The head owns the skin material used by the bare-skin submesh of the other parts.
            if (headSlot?.AMI?.ChildSkinMaterials != null) {
              Dictionary<String, String> skin = headSlot.AMI.ChildSkinMaterials;
              if (!String.IsNullOrWhiteSpace(material0)
                  && material0.IndexOf("_naked_", StringComparison.OrdinalIgnoreCase) >= 0
                  && skin.TryGetValue(appSlot.Key, out String skin0))
                material0 = _currentDom.AppearanceLoader.ApplyPartsMacros(skin0, bodyType);
              if (gr2Model.numMaterials > 1 && String.IsNullOrWhiteSpace(materialMirror)
                  && skin.TryGetValue(appSlot.Key, out String skin1))
                materialMirror = _currentDom.AppearanceLoader.ApplyPartsMacros(skin1, bodyType);
            }

            String palette1XML = !String.IsNullOrWhiteSpace(slot.PrimaryHue)
              ? "/resources" + slot.PrimaryHue.Split(';').First().Replace('\\', '/')
              : String.Empty;
            String palette2XML = !String.IsNullOrWhiteSpace(slot.SecondaryHue)
              ? "/resources" + slot.SecondaryHue.Split(';').First().Replace('\\', '/')
              : String.Empty;

            gr2Model.materials = new List<GR2_Material>();
            if (!String.IsNullOrWhiteSpace(material0)) {
              var material = new GR2_Material(material0) { palette1XML = palette1XML, palette2XML = palette2XML };
              gr2Model.materials.Add(material);
            }
            if (!String.IsNullOrWhiteSpace(materialMirror)) {
              var material = new GR2_Material(materialMirror) { palette1XML = palette1XML, palette2XML = palette2XML };
              gr2Model.materials.Add(material);
            }

            if (slot.AttachedModels != null) {
              foreach (String attach in slot.AttachedModels.Where(x => !String.IsNullOrWhiteSpace(x))) {
                String attachFile = _currentDom.AppearanceLoader.ApplyPartsMacros(attach, bodyType).Replace('\\', '/');
                File attachmentFile = _currentAssets.FindFile("/resources" + attachFile);
                if (attachmentFile == null) continue;
                using BinaryReader abr = new BinaryReader(attachmentFile.OpenCopyInMemory());
                String attachName = attachFile.Split('/').Last();
                GR2 attachModel = new GR2(abr, attachName) {
                  materials = new List<GR2_Material>(gr2Model.materials),
                  transformMatrix = Matrix.Scaling(new Vector3(1.0F, 1.0F, 1.0F))
                };
                gr2Model.attachedModels.Add(attachModel);
              }
            }

            gr2Model.transformMatrix = Matrix.Scaling(new Vector3(1.0F, 1.0F, 1.0F));
            _models[appSlot.Key] = gr2Model;
          }
        }

        if (!String.IsNullOrWhiteSpace(model) && model.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
          _resources[appSlot.Key] = model;

        if (!String.IsNullOrWhiteSpace(model) && model.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) {
          // A number of modern appearance XML references are actually GOM dynamic-data nodes.
          String dynFqn = model.Replace('\\', '/').Replace("/art/", "").Replace(".xml", "").Replace("/", ".");
          GomObject dynObj = _currentDom.GetObject(dynFqn);
          if (dynObj != null) _resources[appSlot.Key] = dynObj;
        }
      }
    }

    private void ParseTestRules() {
      File file = _currentAssets.FindFile("/resources/art/dynamic/testrules.rul");

      if (file == null) return;

      XmlDocument xmlDoc = new XmlDocument();
      xmlDoc.Load(file.OpenCopyInMemory());

      XmlNodeList ruleList = xmlDoc.SelectNodes("/Rules/Rule");
      XmlNodeList exclusionList = xmlDoc.SelectNodes("/Rules/TagExclusion");
      XmlNodeList groupList = xmlDoc.SelectNodes("/Rules/Group");

      static String Attr(XmlNode node, String name) =>
        node?.Attributes?.GetNamedItem(name)?.InnerText?.Trim() ?? String.Empty;

      static List<String> Tags(XmlNode node) {
        String raw = Attr(node, "Tags");
        return raw.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
          .Select(tag => tag.Trim())
          .Where(tag => tag.Length > 0)
          .Distinct(StringComparer.OrdinalIgnoreCase)
          .ToList();
      }

      _testRules ??= new List<TestRule>();

      if (ruleList != null) foreach (XmlNode rule in ruleList) {
        // Early/beta rule files omit attributes that later clients always emit.
        // Jedipedia treats every RUL attribute as optional and defaults it to empty.
        String slot = Attr(rule, "Slot");
        if (slot.Length > 0)
          slot = String.Equals(slot, "facehair", StringComparison.OrdinalIgnoreCase)
            ? "FaceHair"
            : Char.ToUpperInvariant(slot[0]) + slot.Substring(1);

        TestRule testRule = new TestRule {
          slot = slot,
          archetype = Attr(rule, "Archetype"),
          attachmentName = Attr(rule, "AttachmentName"),
          tags = Tags(rule)
        };

        _testRules.Add(testRule);
      }

      _tagExclusions ??= new Dictionary<String, List<String>>();

      if (exclusionList != null) foreach (XmlNode tagExclusion in exclusionList) {
        String excludedTag = Attr(tagExclusion, "ExcludedTag");
        if (excludedTag.Length == 0) continue;

        List<String> tags = new List<String>();

        foreach (String tagEx in Tags(tagExclusion)) {
          XmlNode group = groupList?.Cast<XmlNode>().FirstOrDefault(
            candidate => String.Equals(Attr(candidate, "Name"), tagEx, StringComparison.OrdinalIgnoreCase)
          );

          if (group != null)
            tags.AddRange(Tags(group));
          else
            tags.Add(tagEx);
        }

        _tagExclusions[excludedTag] = tags.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
      }

      _testGroups ??= new Dictionary<String, List<String>>();

      if (groupList != null) foreach (XmlNode group in groupList) {
        String name = Attr(group, "Name");
        if (name.Length == 0) continue;

        List<String> tags = Tags(group);

        // Historical client compatibility retained from the original viewer.
        if (String.Equals(name, "rubberheadshands", StringComparison.OrdinalIgnoreCase) &&
            !tags.Contains("rakata", StringComparer.OrdinalIgnoreCase))
          tags.Add("rakata");

        _testGroups[name] = tags;
      }
    }

    #endregion

    #region Preview Methods
    private void PreviewAsset(NodeAsset asset) {
      if (asset.Obj != null || asset.objData != null || asset.dynObject != null) {
        // FxSpec recursion is cached only for the duration of a single preview.
        // Keeping this set across selections made the same MNT load differently
        // depending on what had been viewed before.
        _parsedFxSpecs.Clear();
        RenderPanelHide();
        LoadingSwirlShow();
        ProgressBarShow();

        if (_panelRender != null) {
          if (_render != null) {
            _panelRender.StopRender();
            _render.Join();
            _panelRender.Clear();
          }
        }

        _models ??= new Dictionary<String, GR2>();
        _resources ??= new Dictionary<String, Object>();

        // Do not carry models/resources from the previous preview into the
        // next MNT/ITM selection.
        _models.Clear();
        _resources.Clear();

        DataGridViewClear();
        DataGridViewDisable();
        TreeViewFast2Clear();
        TreeViewFast2Disable();

        if (asset.dynObject != null && asset.dynObject is HashFileInfo info) {
          Refresh();
          PreviewGR2(info);
          DataGridViewBuild();
          LoadingSwirlHide();
          ProgressBarHide();
          RenderPanelShow();
          // treeViewFast1.Enabled = true;
          StatusBarText(
            asset.compareState != BuildFileState.None
              ? "GR2 File Loaded. Compare State: " + asset.compareState
              : "GR2 File Loaded."
          );

        } else if (asset.Obj != null) {
          GomObject obj = asset.Obj;
          NpcAppearance npcData;
          ItemAppearance itemData;
          List<Object> visualList;
          Object weaponData;

          try {
            switch (obj.Name[..3]) {
              case "npp":
                npcData = (NpcAppearance)_currentDom.AppearanceLoader.Load(obj.Name);
                StatusBarText("Loading NPP Data ...");
                Refresh();
                PreviewNPC(npcData);
                StatusBarText("NPP Loaded.");
                break;
              case "ipp":
                itemData = (ItemAppearance)_currentDom.AppearanceLoader.Load(obj.Name);
                StatusBarText("Loading IPP Data ...");
                Refresh();
                PreviewIPP(itemData);
                StatusBarText("IPP Loaded.");
                break;
              case "itm":
                String appearSpec =
                  obj.Data.ValueOrDefault<String>("cbtWeaponAppearanceSpec", null);
                _weaponAppearance.TryGetValue(appearSpec.ToLower(), out weaponData);
                StatusBarText("Loading ITM Data ...");
                Refresh();

                if (weaponData != null)
                  PreviewITM(obj, (GomObjectData)weaponData);
                else
                  MessageBox.Show(
                    "ERROR: Cannot load model! \r\nWeapon Apperance Spec Missing",
                    "Missing Weapon Appearance Spec"
                  );

                StatusBarText("ITM Loaded");
                break;
              case "dyn":
                try {
                  visualList = obj.Data.ValueOrDefault<List<Object>>("dynVisualList", null);
                  StatusBarText("Loading DYN Data ...");
                  Refresh();

                  if (visualList != null) PreviewDYN(obj, visualList);
                  else
                    MessageBox.Show(
                      "ERROR: Cannot load model! \r\nVisual List Missing",
                      "Missing Visual List Spec"
                    );

                  StatusBarText("DYN Loaded");
                }
                catch (Exception ex) {
                  MessageBox.Show(
                    ex.Message.ToString() + "\r\n" + ex.InnerException.ToString() + "\r\n"
                      + ex.StackTrace.ToString(),
                    "Error"
                  );
                }
                break;
            }

            DataGridViewBuild();
            LoadingSwirlHide();
            ProgressBarHide();
            RenderPanelShow();
            // treeViewFast1.Enabled = true;
          }
          catch (Exception ex) {
            MessageBox.Show("Could not load NPC \r\n" + ex.ToString());
            StatusBarText("NPC Load Error");
            LoadingSwirlHide();
            ProgressBarHide();
            RenderPanelShow();
          }
        } else if (asset.objData != null) {
          GomObjectData obj = asset.objData;
          if (obj.Dictionary.ContainsKey("mntDataSpecString") ||
              obj.Dictionary.ContainsKey("mntDataSpec")) {
            try {
              StatusBarText("Loading MNT Data ...");
              Refresh();
              PreviewMNT(obj);
            }
            catch (Exception ex) {
              MessageBox.Show(
                ex.ToString(),
                "Error"
              );
            }
          }

          DataGridViewBuild();
          LoadingSwirlHide();
          ProgressBarHide();
          RenderPanelShow();
          // treeViewFast1.Enabled = true;
          StatusBarText("MNT Loaded.");
        }
      }
    }

    private void PreviewDYN(GomObject obj, List<Object> visualList) {
      foreach (GomObjectData visualItem in visualList) {
        String model = "";
        String visualName = "";

        Vector3 rotationVec = new Vector3();
        Vector3 scaleVec = new Vector3(1.0F, 1.0F, 1.0F);
        Vector3 positionVec = new Vector3();

        foreach (KeyValuePair<String, Object> item in visualItem.Dictionary) {
          if (item.Key == "dynVisualFqn") {
            if (item.Value.ToString().Contains(".gr2"))
              model = item.Value.ToString();
            else
              continue;
          } else if (item.Key == "dynVisualName") {
            visualName = item.Value.ToString();
          } else if (item.Key == "dynRotation" || item.Key == "dynScale"
                     || item.Key == "dynPosition") {
            List<Single> value = (List<Single>)item.Value;

            if (item.Key == "dynRotation")
              rotationVec = new Vector3(value[0], value[1], value[2]);
            else if (item.Key == "dynScale")
              scaleVec = new Vector3(value[0], value[1], value[2]);
            else if (item.Key == "dynPosition")
              positionVec = new Vector3(value[0], value[1], value[2]);
          } else {
            continue;
          }
        }

        if (model.Contains("designblockout")) continue;

        File file = _currentAssets.FindFile("/resources" + model);

        if (file != null) {
          using BinaryReader br = new BinaryReader(file.OpenCopyInMemory());
          String name = model.Split('/').Last();

          GR2 gr2Model = new GR2(br, name) {
            transformMatrix = Matrix.Scaling(scaleVec)
              * Matrix.RotationZ((Single)(rotationVec.Z * Math.PI / 180))
              * Matrix.RotationX((Single)(rotationVec.X * Math.PI / 180))
              * Matrix.RotationY((Single)(rotationVec.Y * Math.PI / 180))
              * Matrix.Translation(positionVec)
          };

          try {
            _models.Add(visualName, gr2Model);
          }
          catch (Exception ex) {
            Debug.WriteLine(ex.StackTrace.ToString());
          }
        }
      }

      _panelRender.LoadModel(_models, _resources, obj.Name, "dyn");

      _render = new Thread(_panelRender.StartRender) { IsBackground = true };

      _render.Start();
    }

    public void PreviewGR2(HashFileInfo hashInfo) {
      String model = hashInfo.FileName;
      File file = hashInfo.File;

      if (file != null) {
        String name = model.Split('/').Last();

        using BinaryReader br = new BinaryReader(file.OpenCopyInMemory());
        GR2 gr2 = new GR2(br, name) {
          transformMatrix = Matrix.Scaling(new Vector3(1.0f, 1.0f, 1.0f))
        };

        if (gr2.materials.Count == 0) {
          foreach (GR2_Mesh mesh in gr2.meshes) {
            if (mesh.meshName.Contains("collision")) continue;
            else gr2.numMaterials = mesh.numPieces;
          }

          if (gr2.numMaterials == 1)
            gr2.materials = new List<GR2_Material> { new GR2_Material("all_test_grey_128") };

          if (gr2.numMaterials == 2)
            gr2.materials = new List<GR2_Material> {
                new GR2_Material("all_test_grey_128"),
                new GR2_Material("defaultMirror")
              };
        }

        if (gr2.materials.Count > 0) {
          if (gr2.materials[0].materialName == "default")
            gr2.materials[0] = new GR2_Material("all_test_grey_128");
        }

        _models.Add(model[(model.LastIndexOf('/') + 1)..], gr2);
        _panelRender.LoadModel(_models, _resources, name, "");

        _render = new Thread(_panelRender.StartRender) { IsBackground = true };

        _render.Start();
      }
    }

    private void PreviewIPP(ItemAppearance itemData) {
      LoadIPP(itemData);
      if (_models == null || _models.Count == 0) {
        StatusBarText("IPP contains no resolvable model for " + (_bodyType ?? "<unknown body type>"));
        return;
      }

      _panelRender.LoadModel(_models, _resources, itemData.Fqn, "ipp");
      _render = new Thread(_panelRender.StartRender) { IsBackground = true };
      _render.Start();
    }

    private void PreviewIPPs(List<ItemAppearance> itemsData) {
      Int32 itemsDone = 0;
      Int32 itemsTotal = itemsData.Count;

      foreach (ItemAppearance itemData in itemsData) {
        LoadIPP(itemData);
        itemsDone++;
        ProgressBarValue(itemsDone * 100 / itemsTotal);
      }

      if (itemsData.Count > 0 && _models != null && _models.Count > 0) {
        _panelRender.LoadModel(_models, _resources, itemsData.First().Fqn, "ipp");
        _render = new Thread(_panelRender.StartRender) { IsBackground = true };
        _render.Start();
      } else if (itemsData.Count > 0) {
        StatusBarText("IPP set contains no resolvable models for " + (_bodyType ?? "<unknown body type>"));
      }
    }

    private void PreviewITM(GomObject obj, GomObjectData itemData) {
      String model = itemData.ValueOrDefault<String>("itmModel", null).Replace('\\', '/');
      String fxspec = itemData.ValueOrDefault<String>("itmFxSpec", null);

      if (model.Contains(".gr2")) {
        File modelFile = _currentAssets.FindFile("/resources" + model);

        if (modelFile != null) {
          using BinaryReader br = new BinaryReader(modelFile.OpenCopyInMemory());
          String name = model.Split('/').Last();
          GR2 gr2_model = new GR2(br, name) {
            transformMatrix = Matrix.Scaling(new Vector3(1.0F, 1.0F, 1.0F))
          };

          _models.Add(model[(model.LastIndexOf('/') + 1)..], gr2_model);
        }
      }

      ParseFxSpec(fxspec, "itm");
      _panelRender.LoadModel(_models, _resources, obj.Name, "itm");

      _render = new Thread(_panelRender.StartRender) { IsBackground = true };

      _render.Start();
    }

    private Boolean LoadGR2Model(String modelPath, String key = null) {
      if (String.IsNullOrWhiteSpace(modelPath)) return false;

      modelPath = modelPath.Replace('\\', '/');
      if (!modelPath.Contains(".gr2", StringComparison.OrdinalIgnoreCase)) return false;

      if (!modelPath.StartsWith("/resources", StringComparison.OrdinalIgnoreCase))
        modelPath = "/resources" + (modelPath.StartsWith("/") ? "" : "/") + modelPath;

      File file = _currentAssets.FindFile(modelPath);
      if (file == null) return false;

      using BinaryReader br = new BinaryReader(file.OpenCopyInMemory());
      String name = modelPath.Split('/').Last();
      GR2 gr2Model = new GR2(br, name) {
        transformMatrix = Matrix.Scaling(new Vector3(1.0F, 1.0F, 1.0F))
      };

      _models[key ?? name] = gr2Model;
      return true;
    }

    private Boolean LoadMountDecoration(UInt64 nodeId) {
      return LoadMountDecoration(nodeId, new HashSet<UInt64>());
    }

    private Boolean LoadMountDecoration(UInt64 nodeId, HashSet<UInt64> visited) {
      if (nodeId == 0 || visited == null || !visited.Add(nodeId)) return false;

      GomObject node = _currentDom.GetObject(nodeId);
      if (node == null || node.Data == null) return false;

      Boolean loaded = false;
      String key = !String.IsNullOrWhiteSpace(node.Name)
        ? node.Name.Split('.').Last()
        : nodeId.ToString();

      // mntDataDecorationId does not necessarily point at the drawable object.
      // On current clients it can point at a decDecoration wrapper whose
      // decDecorationId references the actual dyn/plc/npc asset.
      UInt64 nestedDecorationId =
        node.Data.ValueOrDefault<UInt64>("decDecorationId", 0);
      if (nestedDecorationId != 0 && nestedDecorationId != nodeId)
        loaded |= LoadMountDecoration(nestedDecorationId, visited);

      // Vehicle appearance nodes used by vehicle/speeder/glider mounts.
      String vehicleModel = node.Data.ValueOrDefault<String>("vehAppModel", null);
      if (!String.IsNullOrWhiteSpace(vehicleModel))
        loaded |= LoadGR2Model(vehicleModel, key);

      // Placeables occur both with the old plcModel field and the newer
      // asset-spec variant.
      String placeableModel =
        node.Data.ValueOrDefault<String>("plcModel", null)
        ?? node.Data.ValueOrDefault<String>("plcModelAssetSpec", null);
      if (!String.IsNullOrWhiteSpace(placeableModel))
        loaded |= LoadGR2Model(placeableModel, key);

      // Dynamic decoration nodes can contain one or several visual GR2s.
      List<Object> visualList =
        node.Data.ValueOrDefault<List<Object>>("dynVisualList", null);
      if (visualList != null) {
        foreach (GomObjectData visualItem in visualList.OfType<GomObjectData>()) {
          String model = visualItem.ValueOrDefault<String>("dynVisualFqn", null);
          if (!String.IsNullOrWhiteSpace(model))
            loaded |= LoadGR2Model(model, model.Replace('\\', '/').Split('/').Last());
        }
      }

      // Some wrappers point at another object by FQN rather than by node ID.
      foreach (String refField in new [] {
        "dynVisualFqn", "mntDataSpec", "mntDataSpecString"
      }) {
        String referencedFqn = node.Data.ValueOrDefault<String>(refField, null);
        if (String.IsNullOrWhiteSpace(referencedFqn)
            || referencedFqn.Contains(".gr2", StringComparison.OrdinalIgnoreCase))
          continue;

        GomObject referencedNode = _currentDom.GetObject(referencedFqn);
        if (referencedNode != null && referencedNode.Id != nodeId)
          loaded |= LoadMountDecoration(referencedNode.Id, visited);
      }

      return loaded;
    }


    private String DescribeMountValue(Object value, Int32 depth = 0) {
      if (value == null) return "<null>";
      if (depth > 2) return value.ToString();

      if (value is GomObjectData gomData) {
        List<String> parts = new List<String>();
        foreach (KeyValuePair<String, Object> kvp in gomData.Dictionary) {
          if (kvp.Key == "Script_Type" || kvp.Key == "Script_TypeId" || kvp.Key == "Script_NumFields")
            continue;
          parts.Add(kvp.Key + "=" + DescribeMountValue(kvp.Value, depth + 1));
        }
        return "{ " + String.Join(", ", parts) + " }";
      }

      if (value is IDictionary<Object, Object> objectDict) {
        List<String> parts = new List<String>();
        Int32 count = 0;
        foreach (KeyValuePair<Object, Object> kvp in objectDict) {
          parts.Add(DescribeMountValue(kvp.Key, depth + 1) + " => "
                    + DescribeMountValue(kvp.Value, depth + 1));
          if (++count >= 20) {
            parts.Add("...");
            break;
          }
        }
        return "{ " + String.Join(", ", parts) + " }";
      }

      if (value is System.Collections.IDictionary dict) {
        List<String> parts = new List<String>();
        Int32 count = 0;
        foreach (System.Collections.DictionaryEntry entry in dict) {
          parts.Add(DescribeMountValue(entry.Key, depth + 1) + " => "
                    + DescribeMountValue(entry.Value, depth + 1));
          if (++count >= 20) {
            parts.Add("...");
            break;
          }
        }
        return "{ " + String.Join(", ", parts) + " }";
      }

      if (value is System.Collections.IEnumerable enumerable && value is not String) {
        List<String> parts = new List<String>();
        Int32 count = 0;
        foreach (Object item in enumerable) {
          parts.Add(DescribeMountValue(item, depth + 1));
          if (++count >= 20) {
            parts.Add("...");
            break;
          }
        }
        return "[ " + String.Join(", ", parts) + " ]";
      }

      String text = value.ToString();

      // If this looks like a node id, append the resolved object name.
      try {
        UInt64 nodeId = Convert.ToUInt64(value);
        if (nodeId > 0) {
          GomObject node = _currentDom.GetObject(nodeId);
          if (node != null && !String.IsNullOrWhiteSpace(node.Name))
            text += "  ->  " + node.Name;
        }
      } catch {
        // Not a numeric node id.
      }

      // If this looks like a FQN, show whether the DOM can resolve it.
      if (!String.IsNullOrWhiteSpace(text)
          && !text.Contains(" -> ")
          && text.Contains(".")
          && !text.EndsWith(".gr2", StringComparison.OrdinalIgnoreCase)
          && !text.EndsWith(".fxspec", StringComparison.OrdinalIgnoreCase)) {
        try {
          GomObject node = _currentDom.GetObject(text);
          if (node != null)
            text += "  -> nodeId " + node.Id;
        } catch {
          // Not a resolvable FQN.
        }
      }

      return text;
    }

    private String BuildMountDebugDump(GomObjectData obj, String fqn) {
      System.Text.StringBuilder sb = new System.Text.StringBuilder();
      sb.AppendLine("MNT DEBUG");
      sb.AppendLine("=========");
      sb.AppendLine("FQN: " + (String.IsNullOrWhiteSpace(fqn) ? "<empty>" : fqn));
      sb.AppendLine();

      if (obj == null || obj.Dictionary == null) {
        sb.AppendLine("<no GOM data>");
        return sb.ToString();
      }

      foreach (KeyValuePair<String, Object> kvp in obj.Dictionary.OrderBy(x => x.Key)) {
        if (kvp.Key == "Script_Type" || kvp.Key == "Script_TypeId" || kvp.Key == "Script_NumFields")
          continue;

        sb.Append(kvp.Key);
        sb.Append(" [");
        sb.Append(kvp.Value?.GetType().FullName ?? "null");
        sb.Append("] = ");
        sb.AppendLine(DescribeMountValue(kvp.Value));
      }

      return sb.ToString();
    }


    private void CollectMountFxSpecs(Object value, HashSet<String> fxSpecs, Int32 depth = 0) {
      if (value == null || fxSpecs == null || depth > 8) return;

      if (value is String text) {
        if (!String.IsNullOrWhiteSpace(text)
            && text.EndsWith(".fxspec", StringComparison.OrdinalIgnoreCase))
          fxSpecs.Add(text.Trim());
        return;
      }

      if (value is GomObjectData gomData) {
        foreach (KeyValuePair<String, Object> kvp in gomData.Dictionary) {
          // Prefer explicitly named FxSpec/VFX fields, but recurse through all
          // values because current mntDataSeatConfigs nests mntDataFxspec
          // several levels below the mount row.
          if ((kvp.Key.Contains("Fxspec", StringComparison.OrdinalIgnoreCase)
               || kvp.Key.Contains("VFX", StringComparison.OrdinalIgnoreCase))
              && kvp.Value != null) {
            String fx = kvp.Value.ToString();
            if (!String.IsNullOrWhiteSpace(fx)
                && fx.EndsWith(".fxspec", StringComparison.OrdinalIgnoreCase))
              fxSpecs.Add(fx.Trim());
          }

          CollectMountFxSpecs(kvp.Value, fxSpecs, depth + 1);
        }
        return;
      }

      if (value is System.Collections.IDictionary dict) {
        foreach (System.Collections.DictionaryEntry entry in dict) {
          CollectMountFxSpecs(entry.Key, fxSpecs, depth + 1);
          CollectMountFxSpecs(entry.Value, fxSpecs, depth + 1);
        }
        return;
      }

      if (value is System.Collections.IEnumerable enumerable && value is not String) {
        foreach (Object item in enumerable)
          CollectMountFxSpecs(item, fxSpecs, depth + 1);
      }
    }


    private void NormalizeMountModels(HashSet<String> mountFxSpecs) {
      if (_models == null || _models.Count == 0 || mountFxSpecs == null || mountFxSpecs.Count == 0)
        return;

      // The recursive FxSpec resolver deliberately sees helper/VFX GR2s as well
      // as the visible mount.  For a preview we want the main vehicle, not
      // speeder_bike anchors, light helpers, or multiple alternate vehicle
      // variants rendered on top of each other.
      List<KeyValuePair<String, GR2>> vehicleCandidates =
        _models.Where(x => {
          String key = x.Key ?? String.Empty;
          String file = x.Value?.filename ?? String.Empty;
          return key.Contains("veh_", StringComparison.OrdinalIgnoreCase)
              || file.Contains("veh_", StringComparison.OrdinalIgnoreCase);
        }).ToList();

      if (vehicleCandidates.Count == 0)
        return;

      Single ModelSize(GR2 model) {
        if (model == null) return 0.0F;

        Single dx = Math.Abs(model.globalBox.maxX - model.globalBox.minX);
        Single dy = Math.Abs(model.globalBox.maxY - model.globalBox.minY);
        Single dz = Math.Abs(model.globalBox.maxZ - model.globalBox.minZ);

        if (Single.IsNaN(dx) || Single.IsInfinity(dx)) dx = 0;
        if (Single.IsNaN(dy) || Single.IsInfinity(dy)) dy = 0;
        if (Single.IsNaN(dz) || Single.IsInfinity(dz)) dz = 0;

        return (Single)Math.Sqrt(dx * dx + dy * dy + dz * dz);
      }

      // Prefer a drawable mesh, then the largest vehicle-shaped GR2.  Current
      // mount FxSpecs frequently include small veh_* light/effect meshes next
      // to the actual speeder/glider.
      KeyValuePair<String, GR2> primary =
        vehicleCandidates
          .OrderByDescending(x => x.Value?.numMeshes > 0 ? 1 : 0)
          .ThenByDescending(x => ModelSize(x.Value))
          .First();

      HashSet<String> keep = new HashSet<String>(StringComparer.OrdinalIgnoreCase) {
        primary.Key
      };

      foreach (String key in _models.Keys.ToList()) {
        if (keep.Contains(key))
          continue;

        GR2 model = _models[key];
        String file = model?.filename ?? String.Empty;

        Boolean helper =
          key.Equals("speeder", StringComparison.OrdinalIgnoreCase)
          || key.Equals("speeder_bike", StringComparison.OrdinalIgnoreCase)
          || file.Equals("speeder", StringComparison.OrdinalIgnoreCase)
          || file.Equals("speeder_bike", StringComparison.OrdinalIgnoreCase)
          || key.Contains("light_", StringComparison.OrdinalIgnoreCase)
          || file.Contains("light_", StringComparison.OrdinalIgnoreCase)
          || key.Contains("_light", StringComparison.OrdinalIgnoreCase)
          || file.Contains("_light", StringComparison.OrdinalIgnoreCase)
          || key.Contains("vfx", StringComparison.OrdinalIgnoreCase)
          || file.Contains("vfx", StringComparison.OrdinalIgnoreCase)
          || key.Contains("fx_", StringComparison.OrdinalIgnoreCase)
          || file.Contains("fx_", StringComparison.OrdinalIgnoreCase);

        // Once a real vehicle was resolved, remove other root FxSpec models.
        // This avoids duplicate/triple mounts and wildly separated helper
        // geometry affecting the camera.  Creature mounts do not enter this
        // path because they have no vehicle candidate.
        if (helper || vehicleCandidates.Any(x => x.Key == key) || mountFxSpecs.Count > 0) {
          try {
            model?.Dispose();
          } catch {
            // The render panel has not taken ownership yet.
          }
          _models.Remove(key);
        }
      }

      System.Diagnostics.Debug.WriteLine(
        "MNT primary model: " + primary.Key
        + " (" + ModelSize(primary.Value).ToString("0.###") + ")"
      );
    }

    private void PreviewMNT(GomObjectData obj) {
      String fqn = obj.ValueOrDefault<String>("mntDataSpecString", null)
        ?? obj.ValueOrDefault<String>("mntDataSpec", null)
        ?? String.Empty;

      String mountDebugDump = BuildMountDebugDump(obj, fqn);

      // Current vehicle/glider mount rows can hide their visible vehicle
      // FxSpec deep inside mntDataSeatConfigs.  Traverse the complete mount
      // object instead of only looking at a few top-level mntData* fields.
      Int32 modelsBeforeFx = _models?.Count ?? 0;
      HashSet<String> mountFxSpecs =
        new HashSet<String>(StringComparer.OrdinalIgnoreCase);

      CollectMountFxSpecs(obj, mountFxSpecs);

      // Keep the legacy top-level values too, including old entries that omit
      // the .fxspec suffix.
      foreach (String fxField in new [] {
        "mntDataFxspec",
        "mntDataVFX",
        "mntDataPreviewVfx",
        "mntDataFlourishVFX"
      }) {
        if (!obj.Dictionary.TryGetValue(fxField, out Object fxValue) || fxValue == null)
          continue;

        String fxString = fxValue.ToString()?.Trim();
        if (String.IsNullOrWhiteSpace(fxString)) continue;

        if (!fxString.EndsWith(".fxspec", StringComparison.OrdinalIgnoreCase))
          fxString += ".fxspec";

        mountFxSpecs.Add(fxString);
      }

      foreach (String mountFxSpec in mountFxSpecs) {
        Int32 before = _models?.Count ?? 0;
        ParseFxSpec(mountFxSpec, "mnt");

        System.Diagnostics.Debug.WriteLine(
          "MNT FxSpec: " + mountFxSpec + " -> models +" + ((_models?.Count ?? 0) - before)
        );
      }

      Boolean loaded = (_models?.Count ?? 0) > modelsBeforeFx;

      // Creature mounts: current GOM calls this field mntDataNpcSpec; older
      // client data used mntDataNpc. Support both so the viewer works with
      // both generations of GOM data.
      Object npcNodeId = null;
      if (!obj.Dictionary.TryGetValue("mntDataNpcSpec", out npcNodeId))
        obj.Dictionary.TryGetValue("mntDataNpc", out npcNodeId);

      if (npcNodeId != null) {
        try {
          GomObject npcNode = _currentDom.GetObject(Convert.ToUInt64(npcNodeId));
          List<Object> npcVisualList =
            npcNode?.Data?.ValueOrDefault<List<Object>>("npcVisualDataList", null);

          if (npcVisualList != null) {
            foreach (GomObjectData visualItem in npcVisualList.OfType<GomObjectData>()) {
              if (!visualItem.Dictionary.ContainsKey("npcTemplateVisualDataAppearance")) continue;

              NpcAppearance npcData =
                (NpcAppearance)_currentDom.AppearanceLoader.Load(
                  Convert.ToUInt64(visualItem.Dictionary["npcTemplateVisualDataAppearance"])
                );

              ParseNpcData(npcData);
              loaded = true;
            }
          }
        } catch (Exception ex) {
          System.Diagnostics.Debug.WriteLine("MNT NPC load failed: " + ex);
        }
      }

      // Newer mounts, especially vehicle/glider-style mounts, can point to a
      // decoration instead of an NPC. The decoration is a normal dyn/npc/plc
      // node and can therefore carry the actual GR2 model.
      Object decorationId = null;
      if (!obj.Dictionary.TryGetValue("mntDataDecorationId", out decorationId))
        obj.Dictionary.TryGetValue("mntDataDecoration", out decorationId);

      if (decorationId != null) {
        try {
          loaded |= LoadMountDecoration(Convert.ToUInt64(decorationId));
        } catch (Exception ex) {
          System.Diagnostics.Debug.WriteLine("MNT decoration load failed: " + ex);
        }
      }

      // Direct vehicle appearance fallback: the mount spec itself may be a
      // vehAppearanceProtoData node.
      if (!loaded && !String.IsNullOrWhiteSpace(fqn)) {
        try {
          GomObject specNode = _currentDom.GetObject(fqn);
          if (specNode != null) {
            loaded |= LoadMountDecoration(specNode.Id);
          }
        } catch (Exception ex) {
          System.Diagnostics.Debug.WriteLine("MNT vehicle appearance load failed: " + ex);
        }
      }

      // Reduce recursive vehicle FxSpecs to one stable visible root model.
      // Do this only after all FX/decor/NPC resolution has finished.
      NormalizeMountModels(mountFxSpecs);
      loaded = _models != null && _models.Count > 0;

      // Only use the humanoid skeleton as a last resort for legacy/creature
      // mount data. If a vehicle/glider explicitly supplied an FX reference
      // but it could not be resolved, adding the skeleton merely produces a
      // misleading empty blue preview with human bones.
      if (!loaded && mountFxSpecs.Count == 0) {
        String skeletonModel = "/resources/art/dynamic/spec/" + _bodyType + "new_skeleton.gr2";
        loaded = LoadGR2Model(skeletonModel);
      }

      if (_models.Count > 0) {
        _panelRender.LoadModel(_models, _resources, fqn, "mnt");
        _render = new Thread(_panelRender.StartRender) { IsBackground = true };
        _render.Start();

      } else {
        mountDebugDump += "\r\nResolved FxSpecs:\r\n  "
          + (mountFxSpecs.Count > 0
            ? String.Join("\r\n  ", mountFxSpecs)
            : "<none>")
          + "\r\n";

        MessageBox.Show(
          mountDebugDump,
          "MNT Debug - no models resolved",
          MessageBoxButtons.OK,
          MessageBoxIcon.Information
        );
      }
    }

    private void PreviewNPC(NpcAppearance npcData) {
      ParseNpcData(npcData);

      // ================================================================================
      // Override appSlot "rules" parsed from /resources/art/dynamic/testrules.rul
      // ================================================================================
      foreach (TestRule rule in _testRules) {
        String appSlot = "appSlot" + rule.slot;
        String aType = rule.archetype;
        String aName = rule.attachmentName;
        GR2 model = _models.ContainsKey(appSlot) ? _models[appSlot] : null;

        Boolean excluded = false;

        switch (rule.slot) {
          case "Hair":
            if (model != null) {
              // Check for exclusions first
              CheckForExclusions(model, rule.tags, ref excluded);

              // Check if hair model should be disabled
              TagModelAsDisabled(excluded, "appSlotHead", rule.tags, ref model);
              TagModelAsDisabled(excluded, "appSlotFace", rule.tags, ref model);

              if (!excluded && _models.ContainsKey("appSlotChest")) {
                // This is an attachment so we have to dig a bit deeper!
                if (_models["appSlotChest"].attachedModels.Any(
                  x => x.filename.Contains("hoodup"))) {
                  GR2 attach =
                      _models["appSlotChest"].attachedModels.Where(
                        x => x.filename.Contains("hoodup")).FirstOrDefault();

                  if (rule.tags.Any(x => attach.filename.Contains(x))) {
                    // Attachment model filename contains a rule tag
                    model.enabled = false;
                  } else if (rule.tags.Any(x => _testGroups.ContainsKey(x))) {
                    // Attachment model filename didn't contain any rule tags,
                    // let's check if it contains a group tag
                    foreach (String tag in rule.tags) {
                      if (_testGroups.ContainsKey(tag) &&
                          _testGroups[tag].Any(x => attach.filename.Contains(x))) {
                        // Attachment model filename contains a group tag
                        model.enabled = false;
                      }
                    }
                  }
                }
              }
            }
            break;

          case "FaceHair":
            // if (aName == "" && model != null)
            // {
            //     // TODO: General face hair rule.
            // }
            if (aName == "chops" && model != null) {
              // We're after attachments, so we have to dig a bit deeper!
              if (model.attachedModels.Any(x => x.filename.Contains("chops"))) {
                GR2 attach = model.attachedModels.Where(
                    x => x.filename.Contains("chops")
                  ).FirstOrDefault();

                // Check for exclusions first
                CheckForExclusions(model, rule.tags, ref excluded);

                // Check if waist attachment models should be disabled
                TagModelAsDisabled(excluded, "appSlotHead", rule.tags, ref attach);
                TagModelAsDisabled(excluded, "appSlotFace", rule.tags, ref attach);
              }
            }
            if (aName == "mustache" && model != null) {
              // We're after attachments, so we have to dig a bit deeper!
              if (model.attachedModels.Any(x => x.filename.Contains("mustache"))) {
                GR2 attach = model.attachedModels.Where(
                    x => x.filename.Contains("mustache")
                  ).FirstOrDefault();

                // Check for exclusions first
                CheckForExclusions(model, rule.tags, ref excluded);

                // Check if waist attachment models should be disabled
                TagModelAsDisabled(excluded, "appSlotHead", rule.tags, ref attach);
                TagModelAsDisabled(excluded, "appSlotFace", rule.tags, ref attach);
              }
            }
            if (aName == "goatee" && model != null) {
              // We're after attachments, so we have to dig a bit deeper!
              if (model.attachedModels.Any(x => x.filename.Contains("goatee"))) {
                GR2 attach = model.attachedModels.Where(
                    x => x.filename.Contains("goatee")
                  ).FirstOrDefault();

                // Check for exclusions first
                CheckForExclusions(model, rule.tags, ref excluded);

                // Check if waist attachment models should be disabled
                TagModelAsDisabled(excluded, "appSlotHead", rule.tags, ref attach);
                TagModelAsDisabled(excluded, "appSlotFace", rule.tags, ref attach);
              }
            }
            // if (aType == "miralukan" && model != null)
            // {
            //     // TODO: Miralukan "FaceHair" rule.
            // }
            break;

          case "Head":
            if (model != null) {
              // Check for exclusions first
              CheckForExclusions(model, rule.tags, ref excluded);

              // Check if appSlotHair contains "pctogruta"
              if (!excluded && _models.ContainsKey("appSlotHair")
                  && _models["appSlotHair"].filename.Contains("pctogruta")) excluded = true;

              // Check if head model should be disabled
              TagModelAsDisabled(excluded, "appSlotFace", rule.tags, ref model);
            }
            break;

          case "Chest":
            if (aName == "hoodup" && model != null) {
              // Hood is an attachment so we have to dig a bit deeper!
              if (model.attachedModels.Any(x => x.filename.Contains("hoodup"))) {
                GR2 attach = model.attachedModels.Where(
                    x => x.filename.Contains("hoodup")
                  ).FirstOrDefault();

                // Check for exclusions first
                CheckForExclusions(model, rule.tags, ref excluded);

                // Check if hood attachment model should be disabled
                TagModelAsDisabled(excluded, "appSlotHead", rule.tags, ref attach);
                TagModelAsDisabled(excluded, "appSlotFace", rule.tags, ref attach);
              }
            }
            break;

          case "Boot":
            if (aName == "bootattachments" && model != null) {
              // We're after attachments, so we have to dig a bit deeper!
              if (model.attachedModels.Count > 0) {
                // Check for exclusions first
                CheckForExclusions(model, rule.tags, ref excluded);

                // Check if boot attachment models should be disabled
                TagModelsAsDisabled(excluded, "appSlotLeg", rule.tags, ref model);
              }
            }
            if (aName == "" && model != null) {
              // Check for exclusions first
              CheckForExclusions(model, rule.tags, ref excluded);

              // Check if boot model should be disabled
              TagModelAsDisabled(excluded, "appSlotHead", rule.tags, ref model);
            }
            break;

          case "Hand":
            if (aName == "handattachments" && model != null) {
              // We're after attachments, so we have to dig a bit deeper!
              if (model.attachedModels.Count > 0) {
                // Check for exclusions first
                CheckForExclusions(model, rule.tags, ref excluded);

                // Check if hand attachment models should be disabled
                TagModelsAsDisabled(excluded, "appSlotChest", rule.tags, ref model);
              }
            }
            if (aName == "" && model != null) {
              // Check for exclusions first
              CheckForExclusions(model, rule.tags, ref excluded);

              // Check if hand (glove) model should be disabled
              TagModelAsDisabled(excluded, "appSlotHead", rule.tags, ref model);
            }
            break;

          case "Waist":
            if (aName == "back" && model != null) {
              // We're after attachments, so we have to dig a bit deeper!
              if (model.attachedModels.Any(x => x.filename.Contains("back"))) {
                GR2 attach = model.attachedModels.Where(
                    x => x.filename.Contains("back")
                  ).FirstOrDefault();

                // Check for exclusions first
                CheckForExclusions(model, rule.tags, ref excluded);

                // Check if waist attachment models should be disabled
                TagModelAsDisabled(excluded, "appSlotChest", rule.tags, ref attach);
              }
            }
            break;

          case "Face":
            if (model != null) {
              // Check for exclusions first
              CheckForExclusions(model, rule.tags, ref excluded);

              // Check if face model should be disabled
              TagModelAsDisabled(excluded, "appSlotHead", rule.tags, ref model);
              TagModelAsDisabled(excluded, "appSlotHair", rule.tags, ref model);
            }
            break;
        }
      }
      // End of Override Rules ==========================================================

      _panelRender.LoadModel(_models, _resources, npcData.Fqn, npcData.NppType);
      _render = new Thread(_panelRender.StartRender) { IsBackground = true };
      _render.Start();
    }

    #endregion

    #region RenderPanel
    private void RenderPanelHide() {
      if (InvokeRequired) Invoke(new Action(() => RenderPanelHide()));
      else renderPanel.Visible = false;
    }

    private void RenderPanelMouseHover(Object sender, EventArgs e) {
      if (!_closing) renderPanel.Focus();
    }

    private void RenderPanelResize(Object sender, EventArgs e) {
      if (_panelRender != null) {
        if (renderPanel.Width != _panelRender.ClientWidth
            || renderPanel.Height != _panelRender.ClientHeight)
          _panelRender.SetSize(renderPanel.Height, renderPanel.Width);
      }
    }

    private void RenderPanelShow() {
      if (InvokeRequired) Invoke(new Action(() => RenderPanelShow()));
      else renderPanel.Visible = true;
    }

    #endregion

    #region Progress Bar
    private void ProgressBarHide() {
      if (InvokeRequired) Invoke(new Action(() => ProgressBarHide()));
      else toolStripProgressBar1.Visible = false;
    }

    private void ProgressBarShow() {
      if (InvokeRequired) Invoke(new Action(() => ProgressBarShow()));
      else toolStripProgressBar1.Visible = true;
    }

    private void ProgressBarStyle(ProgressBarStyle style) {
      if (InvokeRequired) Invoke(new Action(() => ProgressBarStyle(style)));
      else toolStripProgressBar1.Style = style;
    }

    private void ProgressBarValue(Int32 value) {
      if (InvokeRequired) Invoke(new Action(() => ProgressBarValue(value)));
      else toolStripProgressBar1.Value = value;
    }

    #endregion

    #region Status Bar Text
    internal void StatusBarText(String text) {
      if (InvokeRequired) Invoke(new Action(() => StatusBarText(text)));
      else toolStripStatusLabel1.Text = text;
    }

    private void StatusBarTextHide() {
      if (InvokeRequired) Invoke(new Action(() => StatusBarTextHide()));
      else toolStripStatusLabel1.Visible = false;
    }

    private void StatusBarTextShow() {
      if (InvokeRequired) Invoke(new Action(() => StatusBarTextShow()));
      else toolStripStatusLabel1.Visible = true;
    }

    #endregion

    #region Test Rule Methods
    private void CheckForExclusions(GR2 model, List<String> tags, ref Boolean excluded) {
      if (tags.Any(x => _tagExclusions.ContainsKey(x)))
        // There are exclusions for one or more of the rule tags
        foreach (String tag in tags)
          if (_tagExclusions.ContainsKey(tag) &&
             _tagExclusions[tag].Any(x => model.filename.Contains(x)))
            // Model filename contains an exclusion tag
            excluded = true;
    }

    private void TagModelAsDisabled(Boolean excluded,
                                    String appSlot,
                                    List<String> tags,
                                    ref GR2 model) {

      if (!excluded && _models.ContainsKey(appSlot))
        if (tags.Any(x => _models[appSlot].filename.Contains(x)))
          // Model filename contains a rule tag
          model.enabled = false;
        else if (tags.Any(x => _testGroups.ContainsKey(x)))
          // Model filename didn't contain any rule tags,
          // let's check if it contains a group tag
          foreach (String tag in tags)
            if (_testGroups.ContainsKey(tag))
              if (_testGroups[tag].Any(x => _models[appSlot].filename.Contains(x)))
                // Model filename contains a group tag
                model.enabled = false;
    }

    private void TagModelsAsDisabled(Boolean excluded,
                                     String appSlot,
                                     List<String> tags,
                                     ref GR2 model) {

      if (!excluded && _models.ContainsKey(appSlot))
        if (tags.Any(x => _models[appSlot].filename.Contains(x)))
          // Chest model filename contains a rule tag
          foreach (GR2 attach in model.attachedModels)
            attach.enabled = false;
        else if (tags.Any(x => _testGroups.ContainsKey(x)))
          // Chest model filename didn't contain any rule tags,
          // let's check if it contains a group tag
          foreach (String tag in tags)
            if (_testGroups.ContainsKey(tag) &&
                _testGroups[tag].Any(x => _models[appSlot].filename.Contains(x)))
              // Chest model filename contains a group tag
              foreach (GR2 attach in model.attachedModels)
                attach.enabled = false;
    }

    #endregion

    #region Tool Strip Menu Items
    private void ToolStripMenuItem1Click(Object sender, EventArgs e) {
      _items ??= new List<ItemAppearance>();

      _items.Clear();
      TreeViewFast2Disable();
      ProgressBarShow();
      StatusBarText("Loading IPP Data ...");
      Refresh();

      TreeNode selectedNode = treeViewFast1.SelectedNode;

      foreach (TreeNode node in selectedNode.Nodes) {
        NodeAsset asset = (NodeAsset)node.Tag;

        if (asset.Obj != null) {
          if (asset.Obj.Name.Split('.').Last().Contains("_")) continue;

          _items.Add((ItemAppearance)_currentDom.AppearanceLoader.Load(asset.Obj.Name));
        }
      }

      if (_panelRender != null) {
        renderPanel.Visible = false;

        if (_render != null) {
          _panelRender.StopRender();
          _render.Join();
          _panelRender.Clear();
        }
      }

      PreviewIPPs(_items);
      DataGridViewBuild();
      ProgressBarHide();
      StatusBarText("IPP Set Loaded.");

      renderPanel.Visible = true;
    }

    private void ToolStripMenuItem2Click(Object sender, EventArgs evnt) {
      NodeAsset asset = (NodeAsset)treeViewFast2.SelectedNode.Tag;

      if (asset.dynObject != null && asset.dynObject is GR2 gr2)
        gr2.enabled = !gr2.enabled;
    }

    private void ToolStripMenuItem3Click(Object sender, EventArgs evnt) {
      NodeAsset asset = (NodeAsset)treeViewFast2.SelectedNode.Tag;

      if (asset.dynObject != null && asset.dynObject is GR2_Material material) {
        ModelBrowserViewMaterial form = new ModelBrowserViewMaterial(material);

        form.Show();
      }
    }

    #endregion

    #region TreeViewFast1
    private void TreeViewFast1AfterSelect(Object sender, TreeViewEventArgs e) {
      TreeNode node = treeViewFast1.SelectedNode;
      NodeAsset asset = (NodeAsset)node.Tag;

      Text = "Model Browser - " + asset.id.ToString();

      PreviewAsset(asset);
    }

    private void TreeViewFast1Hide() {
      if (InvokeRequired) Invoke(new Action(() => TreeViewFast1Hide()));
      else treeViewFast1.Visible = false;
    }

    private void TreeViewFast1MouseHover(Object sender, EventArgs e) {
      if (!_closing) treeViewFast1.Focus();
    }

    private void TreeViewFast1MouseUp(Object sender, MouseEventArgs e) {
      if (e.Button == MouseButtons.Right) {
        treeViewFast1.SelectedNode = treeViewFast1.GetNodeAt(e.X, e.Y);

        if (treeViewFast1.SelectedNode != null && treeViewFast1.SelectedNode.Nodes.Count > 0) {
          if (treeViewFast1.SelectedNode.Name.Contains("ipp."))
            contextMenuStrip1.Show(treeViewFast1, e.Location);
          else if (treeViewFast1.SelectedNode.Name == "ipp") {
            if (contextMenuStrip2.Items.Count == 0) {

              foreach (KeyValuePair<String, String> bt in _bodyTypes) {
                ToolStripMenuItem btStripItem = new ToolStripMenuItem(
                  bt.Value, //text
                  null, //image
                  new EventHandler(ContextMenuStrip2Click), //event handler
                  bt.Key);
                contextMenuStrip2.Items.Add(btStripItem);
              }
            }

            contextMenuStrip2.Show(treeViewFast1, e.Location);
          }
        }
      }
    }

    private void TreeViewFast1Show() {
      if (InvokeRequired) Invoke(new Action(() => TreeViewFast1Show()));
      else treeViewFast1.Visible = true;
    }

    #endregion

    #region TreeViewFast2
    private void TreeViewFast2AfterSelect(Object sender, TreeViewEventArgs e) {
      TreeNode node = treeViewFast2.SelectedNode;
      NodeAsset asset = (NodeAsset)node.Tag;

      if (asset.dynObject != null) {
        DataTable dt = new DataTable();

        dt.Columns.Add("Property");
        dt.Columns.Add("Value");

        dataGridView1.DataSource = dt;
        dataGridView1.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.AllCells;

        if (asset.dynObject is GR2 model) {
          dt.Rows.Add(new String[] {
            "Render",
            model.enabled.NullSafeToString()
          });
          dt.Rows.Add(new String[] {
            "# Attachments",
            model.numAttach.NullSafeToString()
          });
          dt.Rows.Add(new String[] {
            "# Bones",
            model.numBones.NullSafeToString()
          });
          dt.Rows.Add(new String[] {
            "# Meshes",
            model.numMeshes.NullSafeToString()
          });
          dt.Rows.Add(new String[] {
            "# Materials",
            model.numMaterials.NullSafeToString()
          });
        } else if (asset.dynObject is GR2_Material material) {
          dt.Rows.Add(new String[] {
            "Type",
            material.derived.NullSafeToString()
          });
          dt.Rows.Add(new String[] {
            "DiffuseMap",
            material.diffuseDDS.NullSafeToString()
          });
          dt.Rows.Add(new String[] {
            "RotationMap1",
            material.rotationDDS.NullSafeToString()
          });
          dt.Rows.Add(new String[] {
            "GlossMap",
            material.glossDDS.NullSafeToString()
          });
          dt.Rows.Add(new String[] {
            "PaletteMask",
            material.paletteDDS.NullSafeToString()
          });
          dt.Rows.Add(new String[] {
            "PaletteMaskMap",
            material.paletteMaskDDS.NullSafeToString()
          });
          dt.Rows.Add(new String[] {
            "UsesEmissive",
            material.useEmissive.NullSafeToString()
          });
          dt.Rows.Add(new String[] {
            "Pal 1",
            material.palette1.NullSafeToString()
          });
          dt.Rows.Add(new String[] {
            "Pal 1 Met Spec",
            material.palette1MetSpec.NullSafeToString()
          });
          dt.Rows.Add(new String[] {
            "Pal 1 Spec",
            material.palette1Spec.NullSafeToString()
          });
          dt.Rows.Add(new String[] {
            "Pal 2",
            material.palette2.NullSafeToString()
          });
          dt.Rows.Add(new String[] {
            "Pal 2 Met Spec",
            material.palette2MetSpec.NullSafeToString()
          });
          dt.Rows.Add(new String[] {
            "Pal 2 Spec",
            material.palette2Spec.NullSafeToString()
          });
          dt.Rows.Add(new String[] {
            "FacePaint Map",
            material.facepaintDDS.NullSafeToString()
          });
          dt.Rows.Add(new String[] {
            "Complexion Map",
            material.complexionDDS.NullSafeToString()
          });
          dt.Rows.Add(new String[] {
            "Age Map",
            material.ageDDS.NullSafeToString()
          });
        } else if (asset.dynObject is GR2_Mesh mesh) {
          dt.Rows.Add(new String[] {
            "# Bones",
            mesh.numBones.NullSafeToString()
          });
          dt.Rows.Add(new String[] {
            "# Pieces",
            mesh.numPieces.NullSafeToString()
          });
          dt.Rows.Add(new String[] {
            "# Vertices",
            mesh.numVerts.NullSafeToString()
          });
          dt.Rows.Add(new String[] {
            "# Faces",
            (mesh.numVertIndex / 3).NullSafeToString()
          });
          dt.Rows.Add(new String[] {
            "Bones",
            String.Join(", ", mesh.meshBones).NullSafeToString()
          });
        } else if (asset.dynObject is GR2_Bone_Skeleton bone) {
          dt.Rows.Add(new String[] {
            "Bone Name",
            bone.boneName.NullSafeToString()
          });
          dt.Rows.Add(new String[] {
            "Bone Index",
            bone.boneIndex.NullSafeToString()
          });
          dt.Rows.Add(new String[] {
            "Bone Parent Index",
            bone.parentBoneIndex.NullSafeToString()
          });
        }
      }
    }

    private void TreeViewFast2Clear() {
      if (InvokeRequired) Invoke(new Action(() => TreeViewFast2Clear()));
      else treeViewFast2.Nodes.Clear();
    }

    private void TreeViewFast2Disable() {
      if (InvokeRequired) Invoke(new Action(() => TreeViewFast2Disable()));
      else treeViewFast2.Enabled = false;
    }

    // private void TreeViewFast2Enable() {
    //   if (treeViewFast2.InvokeRequired)
    //     treeViewFast2.Invoke(new Action(() => TreeViewFast2Enable()));
    //   else
    //     treeViewFast2.Enabled = true;
    // }

    private void TreeViewFast2MouseUp(Object sender, MouseEventArgs e) {
      if (e.Button == MouseButtons.Right) {
        treeViewFast2.SelectedNode = treeViewFast2.GetNodeAt(e.X, e.Y);

        if (treeViewFast2.SelectedNode != null
            && treeViewFast2.SelectedNode.Tag is NodeAsset asset) {

          if (asset.dynObject != null && asset.dynObject is GR2)
            contextMenuStrip3.Show(treeViewFast2, e.Location);
          else if (asset.dynObject != null && asset.dynObject is GR2_Material)
            contextMenuStrip4.Show(treeViewFast2, e.Location);
        }
      }
    }

    #endregion
  }
}
