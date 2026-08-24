using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml;

using Be.HexEditor;
using Be.Windows.Forms;
using ColorCode;
using DevIL;
using FileFormats;
using GomLib;
using NAudio.Wave;
using nsHashDictionary;
using TorArchive;

namespace PugTools {
  internal partial class AssetBrowser : Form {

    #region Fields
    private Dictionary<String, TreeListItem> m_assetDict; // = new Dictionary<string, TreeListItem>();
    private readonly String m_assetsLocation;
    private readonly String m_previousAssetsLocation;
    private readonly Boolean m_previousAssetsUsePts;
    private readonly Boolean m_compareFiles;
    private Boolean m_audioPlaying; // = false;
    private Boolean m_autoPreview; // = true;
    internal Boolean m_closing; // = false;
    private Assets m_currentAssets;
    private Assets m_previousAssets;
    private Boolean m_extractByExtensions; // = false;
    private Int32 m_extractCount; // = 0;
    private HashSet<String> m_extractExtensions; // = new HashSet<String>();
    private String m_extractPath;
    private Int32 m_filesSearched; // = 0;
    private HashSet<String> m_foundFiles; // = new HashSet<String>();
    private Int32 m_foundNewFileCount; // = 0;
    private readonly HashDictionaryInstance m_hashData;
    private Stream m_inputStream;
    private UInt64 m_modNewCount; // = 0;
    private Int32 m_namesFound; // = 0;
    private TreeNode[] m_nodeMatch;
    private ViewGR2 m_panelRender;
    private Thread m_render;
    private ArrayList m_rootList; // = new ArrayList();
    private Int32 m_searchIndex; // = 0;
    private List<String> m_searchNodes; // = new List<String>();
    private Int32 m_totalFilesSearched; // = 0;
    private Int32 m_totalNamesFound; // = 0;
    private readonly Boolean m_assetsUsePts;
    private WaveOutEvent m_waveOut;
    private XmlDocument m_xmlDoc;

    // JBA playback reuses the existing audio ToolStrip so the controls stay
    // consistent with WEM/BNK playback.
    private Boolean m_jbaActive;
    private System.Windows.Forms.Timer m_jbaUiTimer;
    private ToolStripButton m_jbaSkeletonButton;
    private JBAAppearanceIndex m_jbaAppearanceIndex;
    private DataObjectModel m_jbaDom;
    private readonly Object m_jbaDependencyLock = new Object();
    private Dictionary<String, List<String>> m_jbaMphParentsByJba;
    private Boolean m_jbaDependencyIndexLoaded;

    #endregion

    #region Asset Browser
    internal AssetBrowser(String assetLocation, Boolean usePTS,
                          String previousAssetLocation = null, Boolean previousUsePTS = false,
                          Boolean compareFiles = false) {
      InitializeComponent();
      Config.Load();

      m_assetsLocation = assetLocation;
      m_assetsUsePts = usePTS;
      m_previousAssetsLocation = previousAssetLocation;
      m_previousAssetsUsePts = previousUsePTS;
      m_compareFiles = compareFiles && !String.IsNullOrWhiteSpace(previousAssetLocation);
      m_autoPreview = true;
      m_extractPath = Config.ExtractAssetsPath;
      m_hashData = HashDictionaryInstance.Instance;

      m_jbaUiTimer = new System.Windows.Forms.Timer { Interval = 50 };
      m_jbaUiTimer.Tick += JbaUiTimerTick;

      // JBA-only diagnostic overlay. Keep this dynamic so the existing audio
      // toolbar/designer stays untouched; when a JBA is selected the button is
      // inserted immediately before the progress bar.
      m_jbaSkeletonButton = new ToolStripButton {
        AutoSize = true,
        CheckOnClick = true,
        DisplayStyle = ToolStripItemDisplayStyle.Text,
        Text = "Skeleton",
        ToolTipText = "Show animation skeleton",
        Visible = false
      };
      m_jbaSkeletonButton.CheckedChanged += JbaSkeletonButtonCheckedChanged;

      Int32 jbaProgressIndex = toolStrip1.Items.IndexOf(toolStrip1ProgressBar1);
      if (jbaProgressIndex >= 0)
        toolStrip1.Items.Insert(jbaProgressIndex, m_jbaSkeletonButton);
      else
        toolStrip1.Items.Add(m_jbaSkeletonButton);

      if (!m_hashData.Loaded) m_hashData.Load();

      txtExtractPath.Text = m_extractPath;
      StatusLabel1Text("Loading Assets ...");
      LoadingSwirl1Show();
      ProgressBar1Show();

      treeViewGrid1.CanExpandGetter = delegate (Object x) {
        if (x.GetType() == typeof(NodeListItem))
          return ((NodeListItem)x).children.Count > 0;

        if (x.GetType() == typeof(WemListItem))
          return ((WemListItem)x).Children.Count > 0;

        return false;
      };

      treeViewGrid1.ChildrenGetter = delegate (Object x) {
        if (x.GetType() == typeof(NodeListItem))
          return new ArrayList(((NodeListItem)x).children);

        if (x.GetType() == typeof(WemListItem))
          return new ArrayList(((WemListItem)x).Children);

        return null;
      };

      backgroundWorker1.RunWorkerAsync();
    }

    private void AssetBrowserFormClosed(Object sender, FormClosedEventArgs e) {
      // Do NOT unload the shared hash dictionary here. Unload() calls
      // GC.Collect(), and with the current large SWTOR dictionary this can
      // freeze the entire desktop for seconds/minutes. The main application
      // can keep this shared cache alive.

      if (m_panelRender != null) {
        m_panelRender.StopRender();

        Boolean renderStopped = m_render == null || !m_render.IsAlive || m_render.Join(750);
        if (renderStopped) {
          try { m_panelRender.Clear(); } catch { }
          try { m_panelRender.Dispose(); } catch { }
        }

        m_panelRender = null;
        m_render = null;
      }

      if (treeViewFast1 != null) {
        treeViewFast1.Dispose();
        treeViewFast1 = null;
      }

      try {
        if (m_audioPlaying) m_waveOut?.Stop();
        m_waveOut?.Dispose();
      } catch { }
      m_waveOut = null;
      m_audioPlaying = false;

      try {
        m_jbaUiTimer?.Stop();
        m_jbaUiTimer?.Dispose();
      } catch { }
      m_jbaUiTimer = null;
      m_jbaActive = false;

      try { m_inputStream?.Dispose(); } catch { }
      m_inputStream = null;

      m_assetDict = null;

      /*
      if (Directory.Exists(@".\Temp\")) {
        String[] list = Directory.GetFiles(@".\Temp\", "*.ogg");

        foreach (String item in list) {
          try {
            System.IO.File.Delete(item);
          }
          catch (IOException) { }
        }

        list = Directory.GetFiles(@".\Temp\", "*.wem");

        foreach (String item in list) {
          try {
            System.IO.File.Delete(item);
          }
          catch (IOException) { }
        }
      }
      */

      System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.Interactive;
    }

    private void AssetBrowserFormClosing(Object sender, FormClosingEventArgs e) {
      System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.Interactive;
      m_closing = true;

      // Stop high-frequency work immediately, before controls and D3D handles
      // are destroyed.
      try { m_panelRender?.StopRender(); } catch { }
      try {
        if (m_audioPlaying) m_waveOut?.Stop();
      } catch { }

      if (m_hashData.Dictionary.NeedsSave && (m_modNewCount > 2 || m_foundNewFileCount > 0)) {
        DialogResult save = MessageBox.Show(
          "The hash dictionary needs to be saved. \nThere were " + m_foundNewFileCount.ToString()
            + " new files found this session.\n\nSave the dictionary changes?", "Save Dictionary?",
          MessageBoxButtons.YesNo
        );

        if (save == DialogResult.Yes) m_hashData.Dictionary.SaveBinaryHashList();
      }
    }

    private void AssetBrowserFormResize(Object sender, EventArgs e) {
      treeViewFast1.Size =
        new Size(splitContainer2.Panel1.Width, splitContainer2.Panel1.Height - 70);
    }

    #endregion

    #region Background Wokers Methods
    private void BackgroundWorker1Run(Object sender, DoWorkEventArgs e) {
      if (m_closing) return;

      System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.Interactive;

      m_currentAssets = AssetHandler.Instance.GetCurrentAssets(m_assetsLocation, m_assetsUsePts);

      if (m_compareFiles) {
        m_previousAssets =
          AssetHandler.Instance.GetPreviousAssets(m_previousAssetsLocation, m_previousAssetsUsePts);

        // GetPreviousAssets historically unloads the shared hash dictionary. The browser
        // needs it again to resolve paths for both snapshots.
        if (!m_hashData.Loaded) m_hashData.Load();
      }
    }

    private void BackgroundWorker1Completed(Object sender, RunWorkerCompletedEventArgs e) {
      if (m_closing) return;

      if (e.Error != null) {
        StatusLabel1Text("Unable to load assets for comparison.");
        MessageBox.Show(e.Error.Message, "Asset Browser", MessageBoxButtons.OK, MessageBoxIcon.Error);
        LoadingSwirl1Hide();
        ProgressBar1Hide();
        return;
      }

      m_assetDict = new Dictionary<String, TreeListItem>();

      ProgressBar1Style(ProgressBarStyle.Continuous);
      StatusLabel1Text("Loading Files ...");

      backgroundWorker2.RunWorkerAsync();
    }

    private void BackgroundWorker2ProgressChanged(Object sender, ProgressChangedEventArgs e) {
      ProgressBar1Value(e.ProgressPercentage);
    }

    private void BackgroundWorker2Run(Object sender, DoWorkEventArgs e) {
      if (m_closing) return;

      if (m_compareFiles && m_previousAssets != null) {
        BuildCompareFileTree();
        return;
      }

      HashSet<String> allDirs = new HashSet<String>();
      HashSet<String> fileDirs = new HashSet<String>();

      const String prefixNam = "/root/named";
      const String prefixNew = "/root/new";
      const String prefixMod = "/root/modified";
      const String prefixUnk = "/root/unnamed";

      Int32 intNamCount = 0;
      Int32 intModCount = 0;
      Int32 intNewCount = 0;
      Int32 intUnkCount = 0;

      Int32 libsDone = 0;
      Int32 maxLibs = m_currentAssets.Libraries.Count;

      foreach (Library lib in m_currentAssets.Libraries) {
        if (m_closing) return;

        lib.Load();
        if (m_closing) return;

        foreach (KeyValuePair<Int32, Archive> archive in lib.Archives) {
          if (m_closing) return;

          foreach (TorArchive.File file in archive.Value.EnumerateFiles()) {
            if (m_closing) return;
            HashFileInfo hashInfo = new HashFileInfo(file.FileInfo.PrimaryHash,
                                                     file.FileInfo.SecondaryHash,
                                                     file);

            if (hashInfo.IsNamed) {
              if (hashInfo.FileName == "metadata.bin"
                  || hashInfo.FileName == "ft.sig"
                  || hashInfo.FileName == "groupmanifest.bin") continue;

              TreeListItem assetAll = new TreeListItem(
                prefixNam + hashInfo.Directory + "/" + hashInfo.FileName,
                prefixNam + hashInfo.Directory,
                hashInfo.FileName,
                hashInfo
              );

              if (!m_assetDict.ContainsKey(
                prefixNam + hashInfo.Directory + "/" + hashInfo.FileName))
                m_assetDict.Add(prefixNam + hashInfo.Directory + "/" + hashInfo.FileName, assetAll);
              else {
                // String pausehere = "";
              }

              fileDirs.Add(prefixNam + hashInfo.Directory);
              intNamCount++;

              if (hashInfo.FileState == HashFileInfo.State.New) {
                TreeListItem assetNew = new TreeListItem(
                  prefixNew + hashInfo.Directory + "/" + hashInfo.FileName,
                  prefixNew + hashInfo.Directory,
                  hashInfo.FileName,
                  hashInfo
                );
                String fileName = String.Format(
                  "{0}{1}/{2}",
                  prefixNew,
                  hashInfo.Directory,
                  hashInfo.FileName
                );

                if (!m_assetDict.ContainsKey(fileName)) {
                  m_assetDict.Add(
                    prefixNew + hashInfo.Directory + "/" + hashInfo.FileName,
                    assetNew
                  );
                  fileDirs.Add(prefixNew + hashInfo.Directory);
                  intNewCount++;
                }
              }

              if (hashInfo.FileState == HashFileInfo.State.Modified) {
                TreeListItem assetMod = new TreeListItem(
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

                if (!m_assetDict.ContainsKey(fileName)) {
                  m_assetDict.Add(
                    prefixMod + hashInfo.Directory + "/" + hashInfo.FileName,
                    assetMod
                  );
                  fileDirs.Add(prefixMod + hashInfo.Directory);
                  intModCount++;
                }
              }
            } else {
              hashInfo.Directory = "/" + hashInfo.Source.Replace(".tor", String.Empty);
              TreeListItem assetUnk = new TreeListItem(
                prefixUnk + hashInfo.Directory + "/" + hashInfo.Extension + "/"
                  + hashInfo.FileName + "." + hashInfo.Extension,
                  prefixUnk + hashInfo.Directory + "/" + hashInfo.Extension,
                  hashInfo.FileName + "." + hashInfo.Extension,
                hashInfo
              );

              m_assetDict.Add(
                prefixUnk + hashInfo.Directory + "/" + hashInfo.Extension + "/"
                  + hashInfo.FileName + "." + hashInfo.Extension,
                assetUnk
              );
              fileDirs.Add(prefixUnk + hashInfo.Directory + "/" + hashInfo.Extension);
              intUnkCount++;

              if (hashInfo.FileState == HashFileInfo.State.New) {
                TreeListItem assetNew = new TreeListItem(
                  prefixNew + hashInfo.Directory + "/" + hashInfo.Extension + "/"
                    + hashInfo.FileName + "." + hashInfo.Extension,
                  prefixNew + hashInfo.Directory + "/" + hashInfo.Extension,
                  hashInfo.FileName + "." + hashInfo.Extension,
                  hashInfo
                );

                m_assetDict.Add(
                  prefixNew + hashInfo.Directory + "/" + hashInfo.Extension + "/"
                    + hashInfo.FileName + "." + hashInfo.Extension,
                  assetNew
                );
                fileDirs.Add(prefixNew + hashInfo.Directory + "/" + hashInfo.Extension);
                intNewCount++;
              }

              if (hashInfo.FileState == HashFileInfo.State.Modified) {
                TreeListItem assetMod = new TreeListItem(
                  prefixMod + hashInfo.Directory + "/" + hashInfo.Extension + "/"
                    + hashInfo.FileName + "." + hashInfo.Extension,
                  prefixMod + hashInfo.Directory + "/" + hashInfo.Extension,
                  hashInfo.FileName + "." + hashInfo.Extension,
                  hashInfo
                );

                m_assetDict.Add(
                  prefixMod + hashInfo.Directory + "/" + hashInfo.Extension + "/"
                    + hashInfo.FileName + "." + hashInfo.Extension,
                  assetMod
                );
                fileDirs.Add(prefixMod + hashInfo.Directory + "/" + hashInfo.Extension);
                intModCount++;
              }
            }
          }
        }

        libsDone++;
        backgroundWorker2.ReportProgress(libsDone * 100 / maxLibs);
      }

      m_modNewCount = (UInt64)(intModCount + intNewCount);

      HashFileInfo empty = new HashFileInfo(0, 0, null);
      m_assetDict.Add(
        "/root",
        new TreeListItem("/root", String.Empty, "Root", empty)
      );
      m_assetDict.Add(
        "/root/named",
        new TreeListItem("/root/named", "/root", "Named Files (" + intNamCount + ")", empty)
      );
      m_assetDict.Add(
        "/root/modified",
        new TreeListItem("/root/modified", "/root", "Modified Files (" + intModCount + ")", empty)
      );
      m_assetDict.Add(
        "/root/new",
        new TreeListItem("/root/new", "/root", "New Files (" + intNewCount + ")", empty)
      );
      m_assetDict.Add(
        "/root/unnamed",
        new TreeListItem("/root/unnamed", "/root", "Unnamed Files (" + intUnkCount + ")", empty)
      );

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

        if (parentDir.Length == 0) parentDir = "/root";

        String display = temp.Last();
        TreeListItem asset = new TreeListItem(dir, parentDir, display, empty);

        if (!m_assetDict.ContainsKey(dir)) m_assetDict.Add(dir, asset);
      }
    }

    private void BuildCompareFileTree() {
      HashSet<String> allDirs = new HashSet<String>();
      HashSet<String> fileDirs = new HashSet<String>();
      List<BuildFileDifference> differences =
        BuildAssetComparer.Compare(m_currentAssets, m_previousAssets);

      Int32 newCount = 0;
      Int32 changedCount = 0;
      Int32 removedCount = 0;

      foreach (BuildFileDifference difference in differences) {
        if (m_closing) return;

        BuildFileRecord display = difference.DisplayRecord;
        if (display?.HashInfo == null) continue;

        HashFileInfo info = display.HashInfo;
        String prefix = difference.State switch {
          BuildFileState.New => "/root/new",
          BuildFileState.Changed => "/root/changed",
          BuildFileState.Removed => "/root/removed",
          _ => "/root"
        };

        String directory;
        String displayName;

        if (info.IsNamed) {
          directory = info.Directory;
          displayName = info.FileName;
        } else {
          // Keep the archive and guessed extension in the tree for unknown files,
          // matching the normal Asset Browser layout.
          directory = "/" + info.Source.Replace(".tor", String.Empty)
            + "/" + info.Extension;
          displayName = info.FileName + "." + info.Extension;
        }

        String parentId = prefix + directory;
        String itemId = parentId + "/" + displayName;

        // A named path should normally be unique. If a language/library collision does
        // occur, retain both entries by adding the archive source to the internal id.
        if (m_assetDict.ContainsKey(itemId))
          itemId += " [" + info.Source + "]";

        TreeListItem asset = new TreeListItem(itemId, parentId, displayName, info) {
          CompareState = difference.State,
          PreviousHashInfo = difference.Previous?.HashInfo
        };

        m_assetDict.Add(itemId, asset);
        fileDirs.Add(parentId);

        switch (difference.State) {
          case BuildFileState.New:
            newCount++;
            break;
          case BuildFileState.Changed:
            changedCount++;
            break;
          case BuildFileState.Removed:
            removedCount++;
            break;
        }
      }

      HashFileInfo empty = new HashFileInfo(0, 0, null);
      m_assetDict.Add("/root", new TreeListItem("/root", String.Empty, "Root", empty));
      m_assetDict.Add(
        "/root/changed",
        new TreeListItem(
          "/root/changed", "/root", "Changed Files (" + changedCount + ")", empty
        )
      );
      m_assetDict.Add(
        "/root/new",
        new TreeListItem("/root/new", "/root", "New Files (" + newCount + ")", empty)
      );
      m_assetDict.Add(
        "/root/removed",
        new TreeListItem(
          "/root/removed", "/root", "Removed Files (" + removedCount + ")", empty
        )
      );

      foreach (String dir in fileDirs) {
        String[] temp = dir.Split('/');
        for (Int32 i = 0; i <= temp.Length; i++) {
          String output = String.Join("/", temp, 0, i);
          if (output.Length > 0) allDirs.Add(output);
        }
      }

      foreach (String dir in allDirs) {
        String[] temp = dir.Split('/');
        String parentDir = String.Join("/", temp.Take(temp.Length - 1));
        if (parentDir.Length == 0) parentDir = "/root";

        String display = temp.Last();
        if (!m_assetDict.ContainsKey(dir))
          m_assetDict.Add(dir, new TreeListItem(dir, parentDir, display, empty));
      }

      // This count is used only for hash-dictionary save prompts. Build comparison is
      // read-only and deliberately does not change dictionary CRC baselines.
      m_modNewCount = 0;
      backgroundWorker2.ReportProgress(100);
    }

    private void BackgroundWorker2Completed(Object sender, RunWorkerCompletedEventArgs e) {
      if (m_closing) return;

      if (e.Error != null) {
        StatusLabel1Text("Unable to compare asset builds.");
        MessageBox.Show(e.Error.Message, "Asset Browser", MessageBoxButtons.OK, MessageBoxIcon.Error);
        LoadingSwirl1Hide();
        ProgressBar1Hide();
        return;
      }

      ProgressBar1Value(0);
      ProgressBar1Style(ProgressBarStyle.Marquee);
      StatusLabel1Text("Loading Tree View Items ...");

      backgroundWorker3.RunWorkerAsync();
    }

    private void BackgroundWorker3Run(Object sender, DoWorkEventArgs e) {
      if (m_closing) return;

      Task task = Task.Run(new Action(() => {
        String getId(TreeListItem x) => x.Id;
        String getParentId(TreeListItem x) => x.ParentId;
        String getDisplayName(TreeListItem x) => x.DisplayName;

        treeViewFast1.BeginUpdate();
        treeViewFast1.LoadItems<TreeListItem>(m_assetDict, getId, getParentId, getDisplayName);
        treeViewFast1.EndUpdate();
      }));

      task.Wait();
    }

    private void BackgroundWorker3Completed(Object sender, RunWorkerCompletedEventArgs e) {
      if (m_closing) return;

      if (treeViewFast1.Nodes.Count > 0) treeViewFast1.Nodes[0].Expand();
      treeViewFast1.Show();

      m_panelRender = new ViewGR2(Handle, this, "renderPanel");
      m_panelRender.Init();

      loadingSwirl1.Hide();
      toolStripStatusLabel1.Text = m_compareFiles
        ? "Comparison loaded. Showing New, Changed and Removed files only."
        : "Loading Complete.";
      toolStripProgressBar1.Visible = false;
      toolStripProgressBar1.Value = 0;
      toolStripProgressBar1.Style = ProgressBarStyle.Continuous;

      ButtonsEnable();

      txtSearch.Focus();

      System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.Interactive;
    }

    #endregion

    #region Buttons
    // private void BtnAudioStopClick(Object sender, EventArgs e) {
    //   _audioState = false;
    //   btnAudioStop.Enabled = false;
    // }

    private void BtnChooseExtractClick(Object sender, EventArgs e) {
      FolderBrowserDialog fbd = new FolderBrowserDialog { SelectedPath = txtExtractPath.Text };
      _ = fbd.ShowDialog();
      txtExtractPath.Text = fbd.SelectedPath + "\\";
    }

    private void BtnClearSearchClick(Object sender, EventArgs e) {
      m_searchNodes = new List<String>();
      m_searchIndex = 0;

      txtSearch.Enabled = true;
      txtSearch.Text = "";

      btnFindNext.Enabled = false;
      btnSearch.Enabled = true;
      btnClearSearch.Enabled = false;

    }

    private void ButtonsDisable() {
      if (InvokeRequired) Invoke(new Action(() => ButtonsDisable()));
      else {
        txtSearch.Enabled = false;
        btnSearch.Enabled = false;

        btnExtractPath.Enabled = false;

        btnPreview.Enabled = false;
        btnHelp.Enabled = false;
        btnExtract.Enabled = false;
        btnSaveTxtHash.Enabled = false;
        btnViewHex.Enabled = false;
        btnViewRaw.Enabled = false;
        btnFindFileNames.Enabled = false;
        btnTestHashFile.Enabled = false;
        btnFileTable.Enabled = false;
        btnHashStatus.Enabled = false;
      }
    }

    private async void BtnExtractClick(Object sender, EventArgs e) {
      m_extractCount = 0;
      m_extractPath = txtExtractPath.Text;
      TreeNode node = treeViewFast1.SelectedNode;

      if (node == null) {
        MessageBox.Show(
          "Please select a node before trying to extract any objects.",
          "ERROR: No Node Selected",
          MessageBoxButtons.OK,
          MessageBoxIcon.Warning
        );

      } else {
        TreeListItem asset = (TreeListItem)node.Tag;

        if (asset.HashInfo.File != null) {
          LoadingSwirl1Show();
          ProgressBar1Show();
          ExtractAsset(asset.HashInfo);
          LoadingSwirl1Hide();
          ProgressBar1Hide();

        } else {
          if (node.Nodes.Count > 0) {
            String messageText = "";

            if (m_extractByExtensions) {
              String temp = String.Join(", ", m_extractExtensions);
              messageText = "Extract (" + temp + ") objects from " + node.Name + "?";

            } else
              messageText = "Extract all objects from " + node.Name + "?";

            DialogResult dr = MessageBox.Show(messageText,
                                              "Extract Confirm",
                                              MessageBoxButtons.YesNo,
                                              MessageBoxIcon.Question);

            if (dr == DialogResult.Yes) {
              LoadingSwirl1Show();
              ProgressBar1Show();

              await Task.Run(() => ExtractByNode(node.Nodes));

              LoadingSwirl1Hide();
              ProgressBar1Hide();

              MessageBox.Show("Extracted " + String.Format("{0:n0}", m_extractCount) + " objects",
                              "Extraction Completed",
                              MessageBoxButtons.OK,
                              MessageBoxIcon.Information);
            }
          }
        }
      }
    }

    private void BtnFileTableClick(Object sender, EventArgs e) {
      AssetBrowserFileTable frmFileTable = new AssetBrowserFileTable();
      frmFileTable.Show();
    }

    private async void BtnFindFileNamesClick(Object sender, EventArgs e) {
      AssetBrowserFindFileNames findNamesDialog = new AssetBrowserFindFileNames();

      if (findNamesDialog.ShowDialog(this) == DialogResult.OK) {
        ButtonsDisable();
        HideViewers();
        LoadingSwirl1Show();
        ProgressBar1Show();
        ProgressBar1Style(ProgressBarStyle.Marquee);
        StatusLabel1Text("Loading Data Object Model ...");

        m_totalFilesSearched = 0;
        m_totalNamesFound = 0;

        dataGridView1.Enabled = true;

        List<String> extensions = findNamesDialog.GetTypes();

        DataObjectModel dom =
          await Task.Run(() =>
            DomHandler.Instance.GetCurrentDOM(AssetHandler.Instance.GetCurrentAssets()));

        StatusLabel1Text("Running File Name Finders ...");

        DataTable dt = new DataTable();

        dt.Columns.Add("File Type");
        dt.Columns.Add("# Searched");
        dt.Columns.Add("# Parsed");

        dataGridView1.DataSource = dt;
        dataGridView1.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.AllCells;

        foreach (String ext in extensions) {
          StatusLabel2Text("Looking for " + ext + " Files");

          await Task.Run(() => ParseFiles(ext, dom));

          dt.Rows.Add(new String[] {
            ext,
            m_filesSearched.ToString("n0"),
            m_namesFound.ToString("n0")
          });
          StatusLabel2Text($"Found {m_namesFound:n0} File Names From {ext} Files");
        }

        dt.Rows.Add(new String[] {
          "Total Parsed",
          m_totalFilesSearched.ToString("n0"),
          m_totalNamesFound.ToString("n0")
        });

        StatusLabel2Text(String.Empty);
        StatusLabel1Text("Testing Parsed Files ...");

        await Task.Run(() => TestHashFiles());

        HideViewers();
        ButtonsEnable();

        if (m_foundFiles.Count > 0) {
          txtRawView.Text = "Found Files\r\n\r\n";
          txtRawView.Text += String.Join("\r\n", m_foundFiles);
          txtRawView.Visible = true;
        }

        dt.Rows.Add(new String[] { "Total Files Found", m_foundFiles.Count.ToString("n0") });

        LoadingSwirl1Hide();
        ProgressBar1Hide();
        ProgressBar1Style(ProgressBarStyle.Continuous);

        String finished = $"Parsed {m_totalNamesFound:n0} Potential File Names\r\n\r\n"
                          + $"Found {m_foundFiles.Count:n0} New Files";

        m_foundNewFileCount += m_foundFiles.Count;

        StatusLabel1Text(finished);

        MessageBox.Show(finished,
                        "File Finder Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
      }
    }

    private async void BtnFindNextClick(Object sender, EventArgs e) {
      if (m_searchNodes.ElementAtOrDefault(m_searchIndex) != null) {
        await Task.Run(() => SearchTreeNodes());

        treeViewFast1.SelectedNode = m_nodeMatch[0];
        treeViewFast1.Focus();

        StatusLabel2Text("Item " + (m_searchIndex + 1) + " of " + m_searchNodes.Count);
        m_searchIndex++;

      } else {
        StatusLabel1Text("Search Complete.");
        MessageBox.Show("No more search terms found");
      }
    }

    private void BtnHashStatusClick(Object sender, EventArgs e) {
      AssetBrowserHashStatus hashStatus = new AssetBrowserHashStatus();
      hashStatus.Show();
    }

    private void BtnHelpClick(Object sender, EventArgs e) {
      AssetBrowserHelp helpForm = new AssetBrowserHelp();
      helpForm.Show();
    }

    private void BtnPreviewClick(Object sender, EventArgs e) {
      if (m_autoPreview) {
        m_autoPreview = false;
        btnPreview.Text = "Auto Preview Off";
      } else {
        m_autoPreview = true;
        btnPreview.Text = "Auto Preview On";
      }
    }

    private void BtnSaveTxtHashClick(Object sender, EventArgs e) {
      m_hashData.Dictionary.SaveTextHashList();
      MessageBox.Show("Saved hashes_filenames.txt");
    }

    private void BtnSearchClick(Object sender, EventArgs e) => Search();

    private async void BtnTestHashFileClick(Object sender, EventArgs e) {
      OpenFileDialog ofd = new OpenFileDialog {
        Filter = "Text Files (.txt)|*.txt|Bin Files (.bin)|*.bin|All Files (*.*)|*.*",
        FilterIndex = 1
      };

      if (ofd.ShowDialog() == DialogResult.OK) {
        LoadingSwirl1Show();
        ProgressBar1Show();
        StatusLabel1Text("Testing Hash File ...");

        await Task.Run(() => TestHashFiles(ofd.FileName));

        HideViewers();

        if (m_foundFiles.Count > 0) {
          txtRawView.Text = "Found Files\r\n\r\n";
          StringBuilder sb = new StringBuilder(txtRawView.Text);

          foreach (String file in m_foundFiles) {
            sb.Append(file);
            sb.Append("\r\n");
          }

          txtRawView.Text = sb.ToString();
          txtRawView.Visible = true;
        }

        ProgressBar1Hide();
        LoadingSwirl1Hide();

        m_foundNewFileCount += m_foundFiles.Count;

        String finished = "Found " + m_foundFiles.Count.ToString("n0") + " New Files";
        StatusLabel1Text(finished);

        MessageBox.Show(finished,
                        "Test Hash File Complete",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
      }
    }

    private async void BtnViewHexClick(Object sender, EventArgs e) {
      HideViewers();
      await Task.Run(() => PreviewAssetHEX());
      hexBox1.Visible = true;
      HexBoxPositionChanged(null, null);
    }

    private void BtnViewRawClick(Object sender, EventArgs e) {
      HideViewers();
      txtRawView.Visible = true;
    }

    private void ButtonsEnable() {
      if (InvokeRequired) Invoke(new Action(() => ButtonsEnable()));
      else {
        txtSearch.Enabled = true;
        btnSearch.Enabled = true;

        btnExtractPath.Enabled = true;

        btnPreview.Enabled = true;
        btnHelp.Enabled = true;
        btnExtract.Enabled = true;
        btnSaveTxtHash.Enabled = true;
        btnViewHex.Enabled = true;
        btnViewRaw.Enabled = true;
        btnFindFileNames.Enabled = true;
        btnTestHashFile.Enabled = true;
        btnFileTable.Enabled = true;
        btnHashStatus.Enabled = true;
      }
    }

    #endregion

    #region HexBox1
    private void HexBoxPositionChanged(Object sender, EventArgs e) {
      String position =
        String.Format("Ln {0}    Col {1}", hexBox1.CurrentLine, hexBox1.CurrentPositionInLine);

      String bitPresentation = String.Empty;

      Byte? currentByte =
        hexBox1.ByteProvider != null && hexBox1.ByteProvider.Length > hexBox1.SelectionStart
          ? hexBox1.ByteProvider.ReadByte(hexBox1.SelectionStart)
          : null;

      BitInfo bitInfo =
        currentByte != null ? new BitInfo((Byte)currentByte, hexBox1.SelectionStart) : null;

      if (bitInfo != null) {
        bitPresentation = String.Format(
          "Bits of Byte {0}: {1}", hexBox1.SelectionStart, bitInfo.ToString()
        );

        StatusLabel1Text(position + " " + bitInfo.ToString());
      }

      StatusLabel2Text(bitPresentation);
    }

    #endregion

    #region LoadingSwirl1
    private void LoadingSwirl1Hide() {
      if (InvokeRequired) Invoke(new Action(LoadingSwirl1Hide));
      else loadingSwirl1.Visible = false;
    }

    private void LoadingSwirl1Show() {
      if (InvokeRequired) Invoke(new Action(LoadingSwirl1Show));
      else loadingSwirl1.Visible = true;
    }

    #endregion

    #region Preview Methods
    private async void PreviewAsset(TreeListItem asset) {
      if (asset.HashInfo.File != null) {

        // Stop JBA UI state before switching to another asset.
        m_jbaActive = false;
        m_jbaUiTimer?.Stop();
        if (m_jbaSkeletonButton != null) {
          m_jbaSkeletonButton.Checked = false;
          m_jbaSkeletonButton.Visible = false;
        }
        m_panelRender?.SetShowSkeleton(false);

        // Restore the audio strip defaults; JBA temporarily repurposes button 3
        // as a Loop toggle.
        toolStrip1Button1.Font = new Font("Webdings", 13F);
        toolStrip1Button2.Font = new Font("Webdings", 13F);
        toolStrip1Button3.Font = new Font("Webdings", 13F);
        toolStrip1Button3.Text = "X";
        toolStrip1Button3.ToolTipText = "Mute";
        toolStrip1Button3.Checked = false;

        // Hide all the viewers
        hexBox1.Visible = false;
        pictureBox1.Visible = false;
        renderPanel.Visible = false;
        toolStrip1.Visible = false;
        treeViewGrid1.Visible = false;
        txtRawView.Visible = false;
        webBrowser1.Visible = false;

        // Show the loading swirl and progress bar.
        loadingSwirl1.Visible = true;
        toolStripProgressBar1.Visible = true;

        // Set the status bar text
        toolStripStatusLabel1.Text = "Loading File ...";

        // Clear the tree view grid
        treeViewGrid1.SelectedIndices.Clear();

        if (m_render != null) {
          m_panelRender.StopRender();
          m_render.Join();
          m_panelRender.Clear();
        }

        await Task.Run(() => PreviewAssetLoadObject(asset.HashInfo.File));

        // DynamicFileByteProvider byteProvider = new DynamicFileByteProvider(this.inputStream);
        // hexBox1.ByteProvider = byteProvider;
        // this.inputStream.Position = 0;

        m_rootList = new ArrayList();

        if (asset.HashInfo.Directory == "/resources/systemgenerated/compilednative") {
          await Task.Run(PreviewAssetSCPT);
        } else {
          switch (asset.HashInfo.Extension.ToUpper()) {
            case "DDS":
              await Task.Run(PreviewAssetDDS);
              PreviewAssetDDSCheckPath(asset.HashInfo.Directory);
              pictureBox1.Visible = true;
              break;

            case "PNG":
              await Task.Run(PreviewAssetPNG);
              pictureBox1.Visible = true;
              break;

            case "XML":
            case "MAT":
            case "TEX":
            case "EMT":
            case "EPP":
            case "FXSPEC":
            case "RUL":
            case "MANIFEST":
            case "SVY":
            case "TBL":
            case "LOD":
              await Task.Run(PreviewAssetXML);
              webBrowser1.Visible = true;
              break;

            case "NOT":
              await Task.Run(PreviewAssetNOT);
              webBrowser1.Visible = true;
              break;

            case "DAT": {
              // area.dat and room .dat files now get the same structured view as
              // Jedipedia. Other DAT variants still fall back to the hex viewer.
              Assets datAssets = asset.CompareState == BuildFileState.Removed
                ? m_previousAssets
                : m_currentAssets;

              try {
                m_rootList.Clear();
                await Task.Run(() => PreviewAssetDAT(
                  asset.HashInfo.Directory,
                  asset.HashInfo.FileName,
                  datAssets
                ));
                NodeListItem.ResetTreeListViewColumns(treeViewGrid1);
                treeViewGrid1.Roots = m_rootList;

                // Expand only the top-level sections. This gives the Jedipedia-style
                // long room/asset/instance lists without recursively expanding every
                // path point and every instance property.
                foreach (NodeListItem root in m_rootList.Cast<NodeListItem>())
                  treeViewGrid1.Expand(root);

                treeViewGrid1.AutoResizeColumns(ColumnHeaderAutoResizeStyle.ColumnContent);
                LoadingSwirl1Hide();
                ProgressBar1Hide();
                treeViewGrid1.Visible = true;
              }
              catch (Exception ex) {
                System.Diagnostics.Debug.WriteLine("DAT structured preview failed: " + ex);
                m_inputStream.Position = 0;
                await Task.Run(PreviewAssetHEX);
                LoadingSwirl1Hide();
                ProgressBar1Hide();
              }
              break;
            }

            case "DYC":
            case "MAG":
            case "PRT":
              await Task.Run(PreviewAssetRAW);
              txtRawView.Visible = true;
              break;

            case "JBA":
              await Task.Run(() => PreviewAssetJBA(asset.HashInfo.Directory, asset.HashInfo.FileName));
              renderPanel.Visible = true;
              break;

            case "GR2":
              await Task.Run(() => PreviewAssetGR2(asset.HashInfo.FileName));
              renderPanel.Visible = true;
              break;

            case "STB":
              m_rootList.Clear();
              await Task.Run(PreviewAssetSTB);
              treeViewGrid1.Roots = m_rootList;
              treeViewGrid1.ExpandAll();
              treeViewGrid1.AutoResizeColumn(0, ColumnHeaderAutoResizeStyle.ColumnContent);
              loadingSwirl1.Visible = false;
              toolStripProgressBar1.Visible = false;
              treeViewGrid1.Visible = true;
              break;

            case "BNK":
              m_rootList.Clear();
              await Task.Run(PreviewAssetBNK);
              treeViewGrid1.Roots = m_rootList;
              treeViewGrid1.ExpandAll();
              treeViewGrid1.AutoResizeColumns(ColumnHeaderAutoResizeStyle.ColumnContent);
              loadingSwirl1.Visible = false;
              toolStripProgressBar1.Visible = false;
              toolStrip1.Visible = true;
              treeViewGrid1.Visible = true;
              break;

            case "ACB":
              m_rootList.Clear();
              await Task.Run(PreviewAssetACB);
              treeViewGrid1.Roots = m_rootList;
              treeViewGrid1.ExpandAll();
              treeViewGrid1.AutoResizeColumns(ColumnHeaderAutoResizeStyle.ColumnContent);
              loadingSwirl1.Visible = false;
              toolStripProgressBar1.Visible = false;
              toolStrip1.Visible = true;
              treeViewGrid1.Visible = true;
              break;

            case "WAV":
            case "WEM":
              toolStrip1.Visible = true;
              m_audioPlaying = false;
              await PreviewAssetWEM(asset.HashInfo.FileName);
              break;

            case "DEP":
              toolStripStatusLabel1.Text = "Parsing DEP ...";
              await Task.Run(PreviewAssetDEP);
              treeViewGrid1.Roots = m_rootList;
              treeViewGrid1.ExpandAll();
              loadingSwirl1.Visible = false;
              toolStripProgressBar1.Visible = false;
              treeViewGrid1.Visible = true;
              break;

            case "SCPT":
              await Task.Run(PreviewAssetSCPT);
              break;

            case "GFX":
            case "SWF":
              await Task.Run(PreviewAssetGFX);
              break;

            default:
              await Task.Run(PreviewAssetHEX);
              txtRawView.Visible = true;
              break;
          }
        }

        treeViewGrid1.TopItemIndex = 0;
        loadingSwirl1.Visible = false;

        // PreviewAssetJBA writes a detailed mapping/skeleton/appearance status
        // from its UI callback. Do not overwrite it with the generic message
        // after the background preview task completes.
        if (!String.Equals(
              asset.HashInfo.Extension,
              "JBA",
              StringComparison.OrdinalIgnoreCase)) {
          toolStripStatusLabel1.Text = "File Loaded.";
          toolStripStatusLabel2.Text = String.Empty;
        }

        toolStripProgressBar1.Visible = false;

        ButtonsEnable();
      }
    }

    private void PreviewAssetLoadObject(TorArchive.File file) {
      m_inputStream = file.OpenCopyInMemory();
      return;
    }

    private void PreviewAssetACB() {
      if (InvokeRequired) Invoke(PreviewAssetACB);
      else {
        try {
          using (BinaryReader br = new BinaryReader(m_inputStream)) {
            List<ViewWEM> wems = ViewACB.ParseACB(br);

            WemListItem.ResetTreeListViewColumns(treeViewGrid1);

            foreach (ViewWEM wem in wems) {
              m_rootList.Add(new WemListItem(wem.WemName.ToString(), wem));
            }
          }
        }
        catch (Exception) { }
      }
    }

    private void PreviewAssetBNK() {
      if (InvokeRequired) Invoke(PreviewAssetBNK);
      else {
        try {
          using (BinaryReader br = new BinaryReader(m_inputStream)) {
            FileFormat_BNK bnk = new FileFormat_BNK(br, true);
            List<ViewWEM> wems = new List<ViewWEM>();

            if (bnk.DIDX != null && bnk.DIDX.Wems.Count > 0) wems = bnk.DIDX.Wems;

            WemListItem.ResetTreeListViewColumns(treeViewGrid1);

            if (bnk.HIRC != null) {
              WemListItem hirc = new WemListItem("HIRC", bnk.HIRC);
              m_rootList.Add(hirc);
            }

            if (bnk.DIDX != null) {
              WemListItem didx = new WemListItem("DIDX", bnk.DIDX);
              m_rootList.Add(didx);
            }

            if (bnk.STID != null) {
              WemListItem stid = new WemListItem("STID", bnk.STID);
              m_rootList.Add(stid);
            }
          }
        }
        catch (Exception) { }
      }
    }

    private void PreviewAssetDAT(String directory, String fileName, Assets assets) {
      if (m_inputStream == null) return;

      m_inputStream.Position = 0;
      using BinaryReader br = new BinaryReader(m_inputStream, Encoding.UTF8, true);
      m_rootList = View_DAT.Parse(br, assets, directory, fileName);
    }

    private void PreviewAssetDDS() {
      try {
        using (MemoryStream stream = new MemoryStream()) {
          ImageImporter imp = new ImageImporter();
          DevIL.Image dds = imp.LoadImageFromStream(ImageType.Dds, m_inputStream);

          ImageExporter exp = new ImageExporter();
          exp.SaveImageToStream(dds, ImageType.Png, stream);

          Bitmap bmp = new Bitmap(stream);
          pictureBox1.Invoke(new Action(() => { pictureBox1.Image = bmp; }));
        }
      }
      catch (Exception) { }
    }

    private void PreviewAssetDDSCheckPath(String directory) {
      if (directory.Contains("codex")
          || directory.Contains("reputation")
          || directory.Contains("tutorials")) {

        pictureBox1.BackgroundImageLayout = ImageLayout.None;
        pictureBox1.BackgroundImage = null;
        pictureBox1.BackColor = System.Drawing.Color.Black;

      } else {
        pictureBox1.BackgroundImageLayout = ImageLayout.Tile;
        pictureBox1.BackColor = System.Drawing.Color.White;
        pictureBox1.BackgroundImage = Properties.Resources.Transparent;
      }
    }

    private void PreviewAssetDEP() {
      if (InvokeRequired) Invoke(PreviewAssetDEP);
      else {
        try {
          using (BinaryReader br = new BinaryReader(m_inputStream)) {
            List<DEP_Entry> entires = ViewDEP.Read(br, m_hashData.Dictionary);

            NodeListItem.ResetTreeListViewColumns(treeViewGrid1);

            foreach (DEP_Entry entry in entires) {
              m_rootList.Add(new NodeListItem(entry.Filename, entry));
            }
          }
        }
        catch (Exception) { }
      }
    }

    private void PreviewAssetGFX() {
      try {
        using (BinaryReader br = new BinaryReader(m_inputStream)) {
          MemoryStream stream = ViewGFX.DecompressGFX(br);
          DynamicFileByteProvider byteProvider = new DynamicFileByteProvider(stream);

          hexBox1.ByteProvider = byteProvider;
          hexBox1.Visible = true;
        }
      }
      catch (Exception) { }
    }

    private static String NormalizeJbaAssetPath(String raw) {
      String path = (raw ?? String.Empty).Replace('\\', '/').Trim();
      const String namedPrefix = "/root/named";
      if (path.StartsWith(namedPrefix, StringComparison.OrdinalIgnoreCase))
        path = path.Substring(namedPrefix.Length);

      while (path.Contains("//"))
        path = path.Replace("//", "/");

      if (path.Length > 0 && path[0] != '/')
        path = "/" + path;

      return path;
    }

    /// <summary>
    /// Jedipedia resolves standalone JBA clips through global.dep, whose edge is
    /// MPH -> JBA. PugTools already has a global.dep reader; build only the
    /// animation reverse edges once, on demand, so a generic clip such as
    /// placeable/airlockdoor/open.jba resolves to placeable_openclose.mph
    /// without filename guessing.
    /// </summary>
    private void EnsureJbaDependencyIndex() {
      if (m_jbaDependencyIndexLoaded)
        return;

      lock (m_jbaDependencyLock) {
        if (m_jbaDependencyIndexLoaded)
          return;

        var result = new Dictionary<String, List<String>>(
          StringComparer.OrdinalIgnoreCase
        );

        try {
          TorArchive.File depFile = m_currentAssets?.FindFile(
            "/resources/global.dep"
          );

          if (depFile != null) {
            using Stream depStream = depFile.OpenCopyInMemory();
            using BinaryReader depReader = new BinaryReader(depStream);
            List<DEP_Entry> entries = ViewDEP.Read(
              depReader,
              m_hashData.Dictionary
            );

            foreach (DEP_Entry entry in entries ?? new List<DEP_Entry>()) {
              String parent = NormalizeJbaAssetPath(entry?.Filename);
              if (!parent.EndsWith(
                    ".mph",
                    StringComparison.OrdinalIgnoreCase)) {
                continue;
              }

              foreach (String dependency in entry.Dependencies
                ?? new List<String>()) {
                String child = NormalizeJbaAssetPath(dependency);
                if (!child.EndsWith(
                      ".jba",
                      StringComparison.OrdinalIgnoreCase)) {
                  continue;
                }

                if (!result.TryGetValue(child, out List<String> parents)) {
                  parents = new List<String>();
                  result.Add(child, parents);
                }

                if (!parents.Contains(
                      parent,
                      StringComparer.OrdinalIgnoreCase)) {
                  parents.Add(parent);
                }
              }
            }
          }
        }
        catch (Exception ex) {
          System.Diagnostics.Debug.WriteLine(
            "JBA global.dep index failed: " + ex
          );
        }

        m_jbaMphParentsByJba = result;
        m_jbaDependencyIndexLoaded = true;
      }
    }

    private List<String> GetJbaMphParents(
      String directory,
      String clipName
    ) {
      EnsureJbaDependencyIndex();

      String fullPath = NormalizeJbaAssetPath(
        directory.TrimEnd('/', '\\') + "/" + clipName
      );

      if (m_jbaMphParentsByJba != null
          && m_jbaMphParentsByJba.TryGetValue(
            fullPath,
            out List<String> parents)) {
        return parents.ToList();
      }

      return new List<String>();
    }

    private Boolean TryApplyJbaAmxMapping(
      String directory,
      String clipName,
      JBAAnimation animation,
      out String sourcePath
    ) {
      sourcePath = null;
      if (animation == null
          || m_currentAssets == null
          || m_jbaAppearanceIndex == null
          || String.IsNullOrWhiteSpace(directory)) {
        return false;
      }

      String folder = NormalizeJbaAssetPath(directory).TrimEnd('/') + "/";
      String clipBase = Path.GetFileNameWithoutExtension(
        clipName ?? String.Empty
      ) ?? String.Empty;

      var candidates = new List<String>();

      void AddCandidate(String path) {
        path = NormalizeJbaAssetPath(path);
        if (String.IsNullOrWhiteSpace(path)) return;
        if (!candidates.Contains(path, StringComparer.OrdinalIgnoreCase))
          candidates.Add(path);
      }

      // Exact dependency parents first, matching Jedipedia's depParents path.
      foreach (String parent in GetJbaMphParents(directory, clipName))
        AddCandidate(parent + ".amx");

      // Folder-wide AnimShare table is the next cheap/direct source.
      AddCandidate(folder + "anim_sharing.mph.amx");

      // Per-clip network naming fallback used by Jedipedia when global.dep is
      // unavailable. Keep the sidecar before the MPH itself.
      foreach (String mph in m_jbaAppearanceIndex.AnimationMphFiles
        .Where(path =>
          path.StartsWith(folder, StringComparison.OrdinalIgnoreCase)
          && Path.GetFileNameWithoutExtension(path)
            .EndsWith(clipBase, StringComparison.OrdinalIgnoreCase)
        )
        .OrderBy(path => path.Length)
        .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)) {
        AddCandidate(mph + ".amx");
      }

      // AMX is tiny and each record names its clip explicitly. Probe any other
      // sibling sidecars as a final AMX fallback; unrelated files reject
      // themselves without changing the animation.
      foreach (String path in m_jbaAppearanceIndex.AnimationAmxFiles
        .Where(path => path.StartsWith(
          folder,
          StringComparison.OrdinalIgnoreCase))
        .OrderBy(path => path.Length)
        .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)) {
        AddCandidate(path);
      }

      foreach (String path in candidates) {
        TorArchive.File file = m_currentAssets.FindFile(path);
        if (file == null) continue;

        try {
          using Stream stream = file.OpenCopyInMemory();
          using BinaryReader reader = new BinaryReader(stream);
          if (AMXAnimationReader.TryApplyBoneNames(
                reader,
                clipName,
                animation)) {
            sourcePath = path;
            return true;
          }
        }
        catch (Exception ex) {
          System.Diagnostics.Debug.WriteLine(
            "JBA AMX mapping failed for " + clipName
            + " via " + path + ": " + ex.Message
          );
        }
      }

      return false;
    }

    private JBARig FindJbaRig(
      String directory,
      String clipName,
      out String sourcePath
    ) {
      sourcePath = null;
      if (m_currentAssets == null
          || m_jbaAppearanceIndex == null
          || String.IsNullOrWhiteSpace(directory)) {
        return null;
      }

      String folder = NormalizeJbaAssetPath(directory).TrimEnd('/') + "/";
      String clipBase = Path.GetFileNameWithoutExtension(
        clipName ?? String.Empty
      ) ?? String.Empty;

      var candidates = new List<String>();

      void AddCandidate(String path) {
        path = NormalizeJbaAssetPath(path);
        if (String.IsNullOrWhiteSpace(path)) return;
        if (!candidates.Contains(path, StringComparer.OrdinalIgnoreCase))
          candidates.Add(path);
      }

      // Authoritative global.dep parents first.
      foreach (String parent in GetJbaMphParents(directory, clipName))
        AddCandidate(parent);

      AddCandidate(folder + clipBase + ".mph");

      foreach (String path in m_jbaAppearanceIndex.AnimationMphFiles
        .Where(path =>
          path.StartsWith(folder, StringComparison.OrdinalIgnoreCase)
          && Path.GetFileNameWithoutExtension(path)
            .EndsWith(clipBase, StringComparison.OrdinalIgnoreCase)
        )
        .OrderBy(path => path.Length)
        .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)) {
        AddCandidate(path);
      }

      // Without global.dep a placeable's network often has no lexical relation
      // to "open"/"close". Placeable folders are small, so this bounded sibling
      // probe is safe and FindRigForClip accepts only a network that actually
      // lists the requested clip.
      Boolean placeable = folder.IndexOf(
        "/anim/placeable/",
        StringComparison.OrdinalIgnoreCase
      ) >= 0;

      if (placeable) {
        foreach (String path in m_jbaAppearanceIndex.AnimationMphFiles
          .Where(path => path.StartsWith(
            folder,
            StringComparison.OrdinalIgnoreCase))
          .OrderBy(path => path.Length)
          .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)) {
          AddCandidate(path);
        }
      }

      AddCandidate(folder + "anim_library.mph");

      foreach (String path in candidates) {
        TorArchive.File file = m_currentAssets.FindFile(path);
        if (file == null) continue;

        try {
          using Stream stream = file.OpenCopyInMemory();
          using BinaryReader reader = new BinaryReader(stream);
          JBARig rig = MPHAnimationReader.FindRigForClip(
            reader,
            clipName
          );

          if (rig == null
              || rig.Bones == null
              || rig.Bones.Count == 0
              || rig.AnimToRig == null
              || rig.AnimToRig.Length == 0) {
            continue;
          }

          rig.Source = path;
          sourcePath = path;
          return rig;
        }
        catch (Exception ex) {
          System.Diagnostics.Debug.WriteLine(
            "JBA MPH mapping failed for " + clipName
            + " via " + path + ": " + ex
          );
        }
      }

      return null;
    }

    private void PreviewAssetJBA(String directory, String fileName) {
      try {
        if (m_inputStream == null) return;
        m_inputStream.Position = 0;

        JBAAnimation animation;
        using (BinaryReader br = new BinaryReader(m_inputStream, Encoding.UTF8, true))
          animation = JBAReader.Read(br);

        // Build the named-file index before channel binding. Current 64-bit
        // JBA files often omit bone names, while sibling *.mph.amx files carry
        // the exact clip -> bone-list mapping.
        if (m_jbaAppearanceIndex == null) {
          IEnumerable<String> namedPaths = m_assetDict != null
            ? m_assetDict.Keys
            : Enumerable.Empty<String>();
          m_jbaAppearanceIndex = JBAAppearance.BuildIndex(namedPaths);
        }

        String amxSource;
        Boolean amxMapped = TryApplyJbaAmxMapping(
          directory,
          fileName,
          animation,
          out amxSource
        );

        // Resolve the exact parent network through global.dep first, then
        // Jedipedia's same-folder fallbacks. Keep the rig even when AMX also
        // supplied names: both now come from the same clip relationship, and
        // the RigToAnimMap is the authoritative channel order.
        String rigSource;
        JBARig rig = FindJbaRig(
          directory,
          fileName,
          out rigSource
        );

        // Morpheme files prefixed with ad_ are additive overlays, not complete
        // poses. Jedipedia resolves them against a deterministic sibling idle
        // in the same animation directory, falling back to the target GR2 bind
        // pose when neither base exists. Treating an ad_ file as an absolute
        // pose collapses most bones onto their parents (the "folded" model).
        String clipFileName = Path.GetFileName(fileName) ?? fileName;
        Boolean additive = clipFileName.StartsWith(
          "ad_",
          StringComparison.OrdinalIgnoreCase
        );

        JBAAnimation baseAnimation = null;
        JBARig baseRig = null;
        String additiveBaseName = null;
        String additiveBaseRigSource = null;
        String additiveBaseAmxSource = null;
        Boolean additiveBaseAmxMapped = false;

        if (additive) {
          String[] baseCandidates = {
            "ex_stand_idle_1.jba",
            "ex_idle_1.jba"
          };

          foreach (String candidateName in baseCandidates) {
            String candidatePath =
              (directory.TrimEnd('/') + "/" + candidateName)
              .Replace("//", "/");

            TorArchive.File candidateFile =
              m_currentAssets.FindFile(candidatePath);

            if (candidateFile == null)
              continue;

            try {
              using (Stream baseStream = candidateFile.OpenCopyInMemory())
              using (BinaryReader baseReader = new BinaryReader(baseStream))
                baseAnimation = JBAReader.Read(baseReader);

              additiveBaseAmxMapped = TryApplyJbaAmxMapping(
                directory,
                candidateName,
                baseAnimation,
                out additiveBaseAmxSource
              );

              try {
                baseRig = FindJbaRig(
                  directory,
                  candidateName,
                  out additiveBaseRigSource
                );
              }
              catch (Exception ex) {
                System.Diagnostics.Debug.WriteLine(
                  "JBA additive base MPH mapping failed for "
                  + candidateName + ": " + ex
                );
              }

              additiveBaseName = candidateName;
              break;
            }
            catch (Exception ex) {
              baseAnimation = null;
              baseRig = null;
              System.Diagnostics.Debug.WriteLine(
                "JBA additive base load failed for "
                + candidateName + ": " + ex
              );
            }
          }
        }

        String bodyType = JBAAppearance.BodyTypeFromAnimationDirectory(directory);

        // Prefer the authored body-type skeleton. Some MAG-driven placeables
        // do not ship a separately named *_skeleton.gr2; only in that case use
        // the skeleton embedded in the actual appearance model. This restores
        // the previously visible placeable preview without ever falling back
        // to an unrelated humanoid rig.
        String skeletonPath = JBAAppearance.ResolveSkeletonPath(
          m_currentAssets,
          bodyType,
          m_jbaAppearanceIndex,
          out String skeletonInfo
        );

        GR2 skeleton = null;
        TorArchive.File skeletonFile = String.IsNullOrWhiteSpace(skeletonPath)
          ? null
          : m_currentAssets.FindFile(skeletonPath);

        if (skeletonFile != null) {
          using Stream skeletonStream = skeletonFile.OpenCopyInMemory();
          using BinaryReader skeletonReader = new BinaryReader(skeletonStream);
          skeleton = new GR2(skeletonReader, skeletonPath);
        }

        // The game's default NPP appearance is the authoritative mapping from
        // animation body type to AMI model/material slots. DOM is cached by
        // DomHandler, so only the first JBA preview pays the initialization cost.
        if (m_jbaDom == null) {
          try {
            m_jbaDom = DomHandler.Instance.GetCurrentDOM(m_currentAssets);
          }
          catch (Exception ex) {
            System.Diagnostics.Debug.WriteLine(
              "JBA automatic NPP appearance initialization failed: " + ex
            );
          }
        }

        String appearanceInfo;
        List<JBAAppearancePart> appearanceParts = JBAAppearance.Resolve(
          m_currentAssets,
          m_jbaDom,
          skeleton,
          bodyType,
          directory,
          m_jbaAppearanceIndex,
          out appearanceInfo
        );

        if (skeleton == null || skeleton.skeleton_bones.Count == 0) {
          skeleton = JBAAppearance.LoadSkeletonFromAppearance(
            m_currentAssets,
            appearanceParts,
            out String appearanceSkeletonModel
          );

          if (skeleton != null && skeleton.skeleton_bones.Count > 0) {
            skeletonInfo = "appearance fallback: "
              + Path.GetFileName(appearanceSkeletonModel ?? String.Empty);
          }
        }

        if (skeleton == null || skeleton.skeleton_bones.Count == 0) {
          BeginInvoke(new Action(() => {
            txtRawView.Text = $"JBA: {fileName}\r\nVersion: {animation.Version}\r\nLength: {animation.Length:0.###} s\r\nFPS: {animation.FPS:0.###}\r\nFrames: {animation.FrameCount}\r\nBones: {animation.BoneCount}\r\n\r\nKein Body-Type- oder Appearance-Skeleton gefunden: {bodyType}";
            txtRawView.Visible = true;
            renderPanel.Visible = false;
            toolStripStatusLabel1.Text = "JBA | Skeleton fehlt: " + bodyType;
            toolStripStatusLabel2.Text = String.Empty;
          }));
          return;
        }

        GR2 previewModel = JBAAppearance.LoadComposite(
          m_currentAssets,
          skeleton,
          appearanceParts,
          "jba_" + bodyType
        );

        m_panelRender.LoadModel(previewModel);
        m_panelRender.LoadAnimation(
          animation,
          rig,
          baseAnimation,
          baseRig,
          additive
        );
        m_panelRender.SetAnimationLoop(true);
        m_render = new Thread(m_panelRender.StartRender) { IsBackground = true };
        m_render.Start();

        BeginInvoke(new Action(() => {
          m_jbaActive = true;

          // Reuse the audio toolbar: Play/Pause, Stop, Loop and progress.
          toolStrip1.Visible = true;
          toolStrip1Button1.Enabled = true;
          toolStrip1Button2.Enabled = true;
          toolStrip1Button3.Enabled = true;
          toolStrip1ProgressBar1.Enabled = true;

          toolStrip1Button1.Font = new Font("Webdings", 13F);
          toolStrip1Button1.Text = ";";
          toolStrip1Button1.ToolTipText = "Pause";

          toolStrip1Button2.Font = new Font("Webdings", 13F);
          toolStrip1Button2.Text = "<";
          toolStrip1Button2.ToolTipText = "Stop / Reset";

          toolStrip1Button3.Font = new Font("Segoe UI", 9F);
          toolStrip1Button3.Text = "Loop";
          toolStrip1Button3.ToolTipText = "Loop animation";
          toolStrip1Button3.Checked = true;

          if (m_jbaSkeletonButton != null) {
            m_jbaSkeletonButton.Checked = false;
            m_jbaSkeletonButton.Visible = true;
            m_jbaSkeletonButton.ToolTipText = "Show animation skeleton and bone names";
          }
          m_panelRender.SetShowSkeleton(false);

          toolStrip1ProgressBar1.Minimum = 0;
          toolStrip1ProgressBar1.Maximum = Math.Max(1, animation.FrameCount - 1);
          toolStrip1ProgressBar1.Value = 0;

          String additiveInfo = String.Empty;
          if (additive) {
            additiveInfo = " | Additive base: "
              + (additiveBaseName ?? "GR2 bind pose");

            if (baseAnimation != null) {
              additiveInfo += " [Bound "
                + m_panelRender.AnimationBaseBoundChannelCount
                + "/" + baseAnimation.BoneCount + " channels -> "
                + m_panelRender.AnimationBaseBoundSkeletonBoneCount
                + " bones";

              if (!String.IsNullOrWhiteSpace(additiveBaseRigSource))
                additiveInfo += " via " + Path.GetFileName(additiveBaseRigSource);
              else if (additiveBaseAmxMapped
                       && !String.IsNullOrWhiteSpace(additiveBaseAmxSource))
                additiveInfo += " via " + Path.GetFileName(additiveBaseAmxSource);

              additiveInfo += "]";
            }
          }

          Int32 namedChannels = animation.BoneNames
            .Take(Math.Min(animation.BoneCount, animation.BoneNames.Count))
            .Count(name => !String.IsNullOrWhiteSpace(name)
              && !name.StartsWith("bone_", StringComparison.OrdinalIgnoreCase));

          Int32 rigMappedChannels = rig?.AnimToRig == null
            ? 0
            : Math.Min(
              animation.BoneCount,
              rig.AnimToRig.Count(index => index >= 0)
            );

          String mappingInfo;
          if (rig != null) {
            mappingInfo = rigMappedChannels + "/" + animation.BoneCount
              + " channels via "
              + Path.GetFileName(rigSource ?? rig.Source ?? String.Empty);

            if (amxMapped)
              mappingInfo += " | AMX: "
                + Path.GetFileName(amxSource ?? String.Empty);
          }
          else if (amxMapped) {
            mappingInfo = namedChannels + "/" + animation.BoneCount
              + " names via "
              + Path.GetFileName(amxSource ?? String.Empty);
          }
          else {
            mappingInfo = namedChannels + "/" + animation.BoneCount
              + " embedded names";
          }

          List<String> depParents = GetJbaMphParents(directory, fileName);
          Int32 depParentCount = depParents.Count;
          String depInfo = depParentCount.ToString();
          if (depParentCount > 0) {
            depInfo += " [" + String.Join(
              ", ",
              depParents.Select(path => Path.GetFileName(path))
            ) + "]";
          }

          String boundInfo =
            m_panelRender.AnimationBoundChannelCount
            + "/" + animation.BoneCount + " channels -> "
            + m_panelRender.AnimationBoundSkeletonBoneCount
            + "/" + m_panelRender.AnimationSkeletonBoneCount
            + " skeleton bones";

          toolStripStatusLabel1.Text = "JBA | Mapping: " + mappingInfo
            + " | Bound: " + boundInfo
            + " | DEP: " + depInfo
            + " | Skeleton: " + skeletonInfo
            + " | Appearance: " + appearanceInfo
            + additiveInfo;
          toolStripStatusLabel2.Text = $"v{animation.Version} | {animation.Length:0.###} s | {animation.FPS:0.##} FPS | {animation.FrameCount} Frames";
          UpdateJbaToolbar();
          m_jbaUiTimer?.Start();
        }));
      }
      catch (Exception ex) {
        BeginInvoke(new Action(() => {
          txtRawView.Text = "JBA konnte nicht abgespielt werden:\r\n\r\n" + ex;
          txtRawView.Visible = true;
          renderPanel.Visible = false;
          toolStripStatusLabel1.Text = "JBA Fehler: " + ex.Message;
          toolStripStatusLabel2.Text = String.Empty;
        }));
      }
    }

    private void PreviewAssetGR2(String fileName) {
      try {
        using (BinaryReader br = new BinaryReader(m_inputStream)) {
          FileFormats.GR2 gr2_model = new FileFormats.GR2(br, fileName);

          if (gr2_model.materials.Count == 0) {
            foreach (FileFormats.GR2_Mesh mesh in gr2_model.meshes) {
              if (mesh.meshName.Contains("collision")) continue;
              else gr2_model.numMaterials = mesh.numPieces;
            }

            if (gr2_model.numMaterials == 1)
              gr2_model.materials = new List<FileFormats.GR2_Material> {
              new FileFormats.GR2_Material("all_test_grey_128")
            };

            if (gr2_model.numMaterials == 2) {
              gr2_model.materials = new List<FileFormats.GR2_Material> {
              new FileFormats.GR2_Material("all_test_grey_128"),
              new FileFormats.GR2_Material("defaultMirror")
            };
            }
          }

          if (gr2_model.materials.Count > 0) {
            if (gr2_model.materials[0].materialName == "default")
              gr2_model.materials[0] = new FileFormats.GR2_Material("all_test_grey_128");

            // if (gr2_model.materials.Count > 1)
            //     if (gr2_model.materials[1].materialName == "defaultMirror")
            //         gr2_model.materials[1] = new GR2_Material("defaultMirror");
          }

          Dictionary<String, FileFormats.GR2> models =
          new Dictionary<String, FileFormats.GR2> { { fileName, gr2_model } };

          Dictionary<String, Object> resources = new Dictionary<String, Object>();

          m_panelRender.LoadModel(gr2_model);

          m_render = new Thread(m_panelRender.StartRender) { IsBackground = true };

          m_render.Start();
        }
      }
      catch (Exception) { }
    }

    private void PreviewAssetHEX() {
      if (InvokeRequired) Invoke(PreviewAssetHEX);
      else {
        try {
          DynamicFileByteProvider byteProvider = new DynamicFileByteProvider(m_inputStream);

          hexBox1.ByteProvider = byteProvider;
          hexBox1.Visible = true;

          StreamReader sr = new StreamReader(m_inputStream);
          String myStr = sr.ReadToEnd();

          txtRawView.ReadOnly = false;
          txtRawView.Text = myStr;
          txtRawView.ReadOnly = true;
        }
        catch (Exception) { }
      }
    }

    private void PreviewAssetNOT() {
      try {
        m_xmlDoc = new XmlDocument();
        using (StreamReader reader = new StreamReader(m_inputStream)) {
          String output = reader.ReadToEnd();
          output = output.Replace("&lt;", "<").Replace("&gt;", ">").Replace("&amp;lt;", "<")
            .Replace("&amp;gt;", ">").Replace("&amp;apos;", "'").Replace("\0", "");

          m_xmlDoc.LoadXml(output);

          txtRawView.ReadOnly = false;
          webBrowser1.DocumentText =
            new CodeColorizer().Colorize(PreviewAssetXMLBeautify(m_xmlDoc), Languages.Xml);

          txtRawView.Text = output;
          txtRawView.ReadOnly = true;
        }
      }
      catch (Exception) { }
    }

    private void PreviewAssetPNG() {
      try {
        using (MemoryStream stream = new MemoryStream()) {
          ImageImporter imp = new ImageImporter();
          DevIL.Image png = imp.LoadImageFromStream(ImageType.Png, m_inputStream);

          ImageExporter exp = new ImageExporter();
          exp.SaveImageToStream(png, ImageType.Bmp, stream);

          Bitmap bmp = new Bitmap(stream);
          pictureBox1.Image = bmp;
        }
      }
      catch (Exception) { }
    }

    private void PreviewAssetRAW() {
      try {
        using (StreamReader reader = new StreamReader(m_inputStream)) {
          String myStr = reader.ReadToEnd();

          txtRawView.ReadOnly = false;
          txtRawView.Text = myStr;
          txtRawView.ReadOnly = true;
        }
      }
      catch (Exception) { }
    }

    private void PreviewAssetSCPT() {
      try {
        using (BinaryReader br = new BinaryReader(m_inputStream)) {
          using (MemoryStream stream = ViewSCPT.DecryptSCPT(br)) {
            DynamicFileByteProvider byteProvider = new DynamicFileByteProvider(stream);

            hexBox1.ByteProvider = byteProvider;
            hexBox1.Visible = true;
          }
        }
      }
      catch (Exception) { }
    }

    private void PreviewAssetSTB() {
      try {
        using (BinaryReader br = new BinaryReader(m_inputStream)) {
          List<STB_Entry> entries = ViewSTB.ParseSTB(br);

          NodeListItem.ResetTreeListViewColumns(treeViewGrid1);

          foreach (STB_Entry entry in entries) {
            m_rootList.Add(new NodeListItem(entry.ID.ToString(), entry.StringValue));
          }
        }
      }
      catch (Exception) { }
    }

    private async Task PreviewAssetWEM(ViewWEM wem) {
      if (InvokeRequired) Invoke(new Action(async () => await PreviewAssetWEM(wem)));
      else {
        if (wem != null && wem.Data.Length > 0) {
          Boolean converted = await Task.Run(wem.ConvertWEM);

          if (converted && wem.Vorbis != null) {
            await Task.Run(() => {
              m_waveOut ??= new WaveOutEvent();
              m_waveOut.Volume = 1.0F;
              m_waveOut.Init(wem.Vorbis);

              if (!m_audioPlaying) {
                m_audioPlaying = true;

                Invoke(new Action(() => {
                  toolStrip1Button1.Enabled = true;
                  toolStrip1Button2.Enabled = true;
                  toolStrip1Button3.Enabled = true;

                  toolStrip1ProgressBar1.Enabled = true;
                  toolStrip1ProgressBar1.Maximum = (Int32)wem.Vorbis.TotalTime.TotalMilliseconds;
                }));

                m_waveOut.Play();

                while (m_waveOut.PlaybackState != PlaybackState.Stopped) {
                  if (!m_audioPlaying) {
                    m_waveOut.Stop();
                    break;
                  } else {
                    Invoke(new Action(() => {
                      toolStrip1Label1.Text = String.Format(
                        "{0:D2}:{1:D2}/{2:D2}:{3:D2}",
                        wem.Vorbis.CurrentTime.Minutes,
                        wem.Vorbis.CurrentTime.Seconds,
                        wem.Vorbis.TotalTime.Minutes,
                        wem.Vorbis.TotalTime.Seconds
                      );
                      toolStrip1ProgressBar1.Value =
                        (Int32)wem.Vorbis.CurrentTime.TotalMilliseconds;
                    }));
                    Thread.Sleep(100);
                  }
                }
              }
            });

            Invoke(new Action(() => {
              toolStrip1Button1.Enabled = false;
              toolStrip1Button2.Enabled = false;
              toolStrip1Button3.Enabled = false;

              toolStrip1Label1.Text = "00:00/00:00";

              toolStrip1ProgressBar1.Enabled = false;
              toolStrip1ProgressBar1.Value = 0;
            }));

          } else {
            StatusLabel1Text("Audio Processing Failed.");
          }
        }
      }
    }

    private async Task PreviewAssetWEM(String fileName) {
      if (InvokeRequired) Invoke(new Action(async () => await PreviewAssetWEM(fileName)));
      else {
        ViewWEM wem = new ViewWEM(fileName, m_inputStream);
        await Task.Run(() => PreviewAssetWEM(wem));
        m_audioPlaying = false;
      }
    }

    private void PreviewAssetXML() {
      if (InvokeRequired) Invoke(PreviewAssetXML);
      else {
        using StreamReader reader = new StreamReader(m_inputStream);

        String output = reader.ReadToEnd();
        output = output.Replace("&lt;", "<").Replace("&gt;", ">").Replace("&amp;lt;", "<")
          .Replace("&amp;gt;", ">").Replace("&amp;apos;", "'").Replace("\0", "");

        m_xmlDoc = new XmlDocument();

        try {
          m_xmlDoc.LoadXml(output);
        }
        catch (Exception) { // ex) {
                            // Debug.WriteLine(ex.Message);
        }

        txtRawView.ReadOnly = false;
        txtRawView.Text = output;

        webBrowser1.DocumentText =
          new CodeColorizer().Colorize(PreviewAssetXMLBeautify(m_xmlDoc), Languages.Xml);

        txtRawView.ReadOnly = true;
      }
    }

    static internal String PreviewAssetXMLBeautify(XmlDocument doc) {
      StringBuilder sb = new StringBuilder();
      XmlWriterSettings settings = new XmlWriterSettings {
        Indent = true,
        IndentChars = "  ",
        NewLineChars = "\r\n",
        NewLineHandling = NewLineHandling.Replace
      };

      using (XmlWriter writer = XmlWriter.Create(sb, settings)) {
        doc.Save(writer);
      }

      return sb.ToString();
    }

    #endregion

    #region RenderPanel
    private void RenderPanelMouseHover(Object sender, EventArgs e) {
      if (!m_closing) renderPanel.Focus();
    }

    private void RenderPanelMouseWheel(Object sender, MouseEventArgs e) {
      throw new NotImplementedException();
    }

    private void RenderPanelResize(Object sender, EventArgs e) {
      if (m_panelRender != null)
        if (renderPanel.Width != m_panelRender.ClientWidth
            || renderPanel.Height != m_panelRender.ClientHeight)
          m_panelRender.SetSize(renderPanel.Height, renderPanel.Width);
    }

    #endregion

    #region Search
    private async void Search() {
      StatusLabel1Text("Performing Search ...");
      m_searchNodes ??= new List<String>();
      m_searchNodes = m_assetDict.Keys.Where(d => d.Contains(txtSearch.Text)).ToList();

      if (m_searchNodes.Count > 0) {
        txtSearch.Enabled = false;
        btnSearch.Enabled = false;
        btnFindNext.Enabled = true;
        btnClearSearch.Enabled = true;

        StatusLabel1Text("Found " + (m_searchNodes.Count + 1) + " Matches");
        LoadingSwirl1Show();
        ProgressBar1Show();
        await Task.Run(() => SearchTreeNodes());
        LoadingSwirl1Hide();
        ProgressBar1Hide();

        treeViewFast1.SelectedNode = m_nodeMatch[0];
        treeViewFast1.Focus();

        StatusLabel2Text("Item " + (m_searchIndex + 1) + " of " + m_searchNodes.Count);

        m_searchIndex++;

      } else {
        StatusLabel1Text("Search Complete.");
        MessageBox.Show("Search term not found.");
      }
    }

    private void SearchTreeNodes() {
      m_nodeMatch = treeViewFast1.Nodes.Find(m_searchNodes[m_searchIndex], true);
    }

    #endregion

    #region Hash List Methods
    private void ParseFiles(String extension, DataObjectModel dom) {
      List<String> assetDictKeys =
        m_assetDict.Keys.Where(d => d.Contains("." + extension.ToLower())).ToList();
      List<TreeListItem> matches = new List<TreeListItem>();

      m_filesSearched = 0;

      foreach (String assetKey in assetDictKeys) {
        if (assetKey.Split('.').Last().ToUpper() != extension) {
          continue;
        }

        if (m_assetDict.TryGetValue(assetKey, out TreeListItem asset)) {
          matches.Add(asset);
        }
      }

      switch (extension) {
        case "XML":
        case "MAT":
          Format_XML_MAT xml_mat_reader = new Format_XML_MAT(m_extractPath, extension);

          foreach (TreeListItem asset in matches) {
            m_filesSearched++;
            using Stream assetStream = asset.HashInfo.File.OpenCopyInMemory();
            xml_mat_reader.ParseXML(assetStream,
                                    asset.HashInfo.Directory + "/" + asset.HashInfo.FileName);
          }

          m_namesFound = xml_mat_reader.FileNames.Count + xml_mat_reader.AnimNames.Count;
          xml_mat_reader.WriteFile();
          break;

        case "EPP":
          Format_EPP epp_reader = new Format_EPP(m_extractPath, extension);
          List<GomObject> eppNodes = dom.GetObjectsStartingWith("epp.");

          foreach (TreeListItem asset in matches) {
            m_filesSearched++;
            using Stream assetStream = asset.HashInfo.File.OpenCopyInMemory();
            epp_reader.ParseEPP(assetStream,
                                asset.HashInfo.Directory + "/" + asset.HashInfo.FileName);
          }

          epp_reader.ParseEPPNodes(eppNodes);
          m_namesFound = epp_reader.FileNames.Count;
          epp_reader.WriteFile();
          break;

        case "PRT":
          Format_PRT prt_reader = new Format_PRT(m_extractPath, extension);

          foreach (TreeListItem asset in matches) {
            m_filesSearched++;
            using Stream assetStream = asset.HashInfo.File.OpenCopyInMemory();
            prt_reader.ParsePRT(assetStream,
                                asset.HashInfo.Directory + "/" + asset.HashInfo.FileName);
          }

          m_namesFound = prt_reader.FileNames.Count;
          prt_reader.WriteFile();
          break;

        case "GR2":
          Format_GR2 gr2_reader = new Format_GR2(m_extractPath, extension);

          foreach (TreeListItem asset in matches) {
            if (asset.HashInfo.IsNamed) continue;

            m_filesSearched++;

            using (Stream assetStream = asset.HashInfo.File.OpenCopyInMemory()) {
              gr2_reader.ParseGR2(assetStream,
                                  asset.HashInfo.Directory + "/" + asset.HashInfo.FileName,
                                  asset.HashInfo.File.Archive);
            }
          }

          m_namesFound = gr2_reader.MatNames.Count + gr2_reader.MeshNames.Count;
          gr2_reader.WriteFile(true);
          break;

        case "BNK":
          Format_BNK bnk_reader = new Format_BNK(m_extractPath, extension);

          foreach (TreeListItem asset in matches) {
            m_filesSearched++;

            using Stream assetStream = asset.HashInfo.File.OpenCopyInMemory();
            bnk_reader.ParseBNK(
              assetStream, asset.HashInfo.Directory + "/" + asset.HashInfo.FileName
            );
          }

          m_namesFound = bnk_reader.Found;
          bnk_reader.WriteFile();
          break;

        case "DAT":
          Format_DAT dat_reader = new Format_DAT(m_extractPath, extension);

          foreach (TreeListItem asset in matches) {
            m_filesSearched++;
            using Stream assetStream = asset.HashInfo.File.OpenCopyInMemory();
            dat_reader.ParseDAT(
              assetStream, asset.HashInfo.Directory + "/" + asset.HashInfo.FileName, this
            );
          }

          m_namesFound = dat_reader.FileNames.Count;
          dat_reader.WriteFile();
          break;

        case "CNV":
          List<GomObject> cnvNodes = dom.GetObjectsStartingWith("cnv.");
          Format_CNV cnv_node_parser = new Format_CNV(m_extractPath, extension);

          cnv_node_parser.ParseCNVNodes(cnvNodes);

          m_namesFound = cnv_node_parser.FileNames.Count
                         + cnv_node_parser.AnimNames.Count
                         + cnv_node_parser.FxSpecNames.Count;
          m_filesSearched += cnvNodes.Count;

          cnv_node_parser.WriteFile();
          cnvNodes.Clear();
          break;

        case "MISC":
          Format_MISC misc_parser = new Format_MISC(m_extractPath, extension);
          List<GomObject> ippNodes = dom.GetObjectsStartingWith("ipp.");

          misc_parser.ParseMISC_IPP(ippNodes);

          List<GomObject> cdxNodes = dom.GetObjectsStartingWith("cdx.");

          misc_parser.ParseMISC_CDX(cdxNodes);
          dom.NodeLookup.TryGetValue(typeof(GomObject), out Dictionary<String, DomType> nodeDict);
          misc_parser.ParseMISC_NODE(nodeDict);

          GomObject ldgNode = dom.Get<GomObject>("loadingAreaLoadScreenPrototype");
          Dictionary<Object, Object> itemApperances =
            dom.GetObject("itmAppearanceDatatable").Data
               .Get<Dictionary<Object, Object>>("itmAppearances");

          misc_parser.ParseMISC_LdnScn(ldgNode);
          misc_parser.ParseMISC_ITEM(itemApperances);
          misc_parser.ParseMISC_TUTORIAL(dom);
          misc_parser.WriteFile();

          m_namesFound = misc_parser.Found;
          m_filesSearched += misc_parser.Searched;
          break;

        case "MISC_WORLD":
          Format_MISC misc_world_parser = new Format_MISC(m_extractPath, extension);
          Dictionary<Object, Object> areaList = dom.GetObject(
            "mapAreasDataProto"
          ).Data.Get<Dictionary<Object, Object>>("mapAreasDataObjectList");
          List<GomObject> areaList2 = dom.GetObjectsStartingWith("world.areas.");

          misc_world_parser.ParseMISC_WORLD(areaList2, areaList, dom);
          areaList.Clear();
          areaList2.Clear();
          misc_world_parser.WriteFile();

          m_namesFound = misc_world_parser.Found;
          break;

        case "FXSPEC":
          Format_FXSPEC fxspec_parser = new Format_FXSPEC(m_extractPath, extension);

          foreach (TreeListItem asset in matches) {
            m_filesSearched++;
            Stream assetStream = asset.HashInfo.File.OpenCopyInMemory();
            fxspec_parser.ParseFXSPEC(assetStream,
                                      asset.HashInfo.Directory + "/" + asset.HashInfo.FileName);
          }

          m_namesFound = fxspec_parser.FileNames.Count;
          fxspec_parser.WriteFile();
          break;

        case "AMX":
          Format_AMX amx_parser = new Format_AMX(m_extractPath, extension);

          foreach (TreeListItem asset in matches) {
            m_filesSearched++;
            Stream assetStream = asset.HashInfo.File.OpenCopyInMemory();
            amx_parser.ParseAMX(assetStream,
                                asset.HashInfo.Directory + "/" + asset.HashInfo.FileName);
          }

          m_namesFound = amx_parser.FileNames.Count;
          amx_parser.WriteFile();
          break;

        case "SDEF":
          Format_SDEF sdef_parser = new Format_SDEF(m_extractPath, extension);
          TorArchive.File sdef = AssetHandler.Instance.GetCurrentAssets().FindFile(
            "/resources/systemgenerated/scriptdef.list"
          );

          sdef_parser.ParseSDEF(sdef.OpenCopyInMemory());
          sdef_parser.WriteFile();

          m_namesFound = sdef_parser.Found;
          m_filesSearched = 1;
          break;

        case "HYD":
          List<GomObject> hydNodes = dom.GetObjectsStartingWith("hyd.");
          Format_HYD hyd_parser = new Format_HYD(m_extractPath, extension);

          hyd_parser.ParseHYD(hydNodes);

          m_namesFound = hyd_parser.AnimFileNames.Count + hyd_parser.VfxFileNames.Count;
          m_filesSearched += hydNodes.Count;

          hyd_parser.WriteFile();
          hydNodes.Clear();
          break;

        case "DYN":
          List<GomObject> dynNodes = dom.GetObjectsStartingWith("dyn.");
          Format_DYN dyn_parser = new Format_DYN(m_extractPath, extension);

          dyn_parser.ParseDYN(dynNodes);

          m_namesFound = dyn_parser.FileNames.Count + dyn_parser.UnknownFileNames.Count;
          m_filesSearched += dynNodes.Count;

          dyn_parser.WriteFile();
          break;

        case "ICONS":
          Format_ICONS icon_parser = new Format_ICONS(m_extractPath, extension);

          icon_parser.ParseICONS(dom);

          m_namesFound = icon_parser.FileNames.Count;
          m_filesSearched += icon_parser.Searched;

          icon_parser.WriteFile();
          break;

        case "PLC":
          List<GomObject> plcNodes = dom.GetObjectsStartingWith("plc.");
          Format_PLC plc_parser = new Format_PLC(m_extractPath, extension);

          plc_parser.ParsePLC(plcNodes);

          m_namesFound = plc_parser.FileNames.Count;
          m_filesSearched += plcNodes.Count;

          plc_parser.WriteFile();
          break;

        case "STB":
          Format_STB stb_parser = new Format_STB(m_extractPath, extension);
          TorArchive.File manifest = AssetHandler.Instance.GetCurrentAssets().FindFile(
            "/resources/gamedata/str/stb.manifest"
          );

          stb_parser.ParseSTBManifest(manifest.OpenCopyInMemory());

          m_namesFound = stb_parser.FileNames.Count;
          m_filesSearched++;

          stb_parser.WriteFile();
          break;

        default:
          break;
      }

      m_totalFilesSearched += m_filesSearched;
      m_totalNamesFound += m_namesFound;

      return;
    }

    private void TestHashFiles(String singleFile = null) {
      m_hashData.Dictionary.SaveBinaryHashList();
      m_foundFiles?.Clear();

      String[] testFiles;

      if (singleFile != null) testFiles = new String[] { singleFile };
      else testFiles = Directory.GetFiles(m_extractPath + "\\File_Names\\");

      if (testFiles.Length > 0) {
        m_foundFiles = new HashSet<String>();

        foreach (String file in testFiles) {
          HashSet<String> testLines = new HashSet<String>();

          if (file.EndsWith(".bin")) { // Import jedipedia hashes.bin format
            using FileStream fs = new FileStream(file, FileMode.Open);
            using BinaryReader br = new BinaryReader(fs);

            while (br.BaseStream.Position != br.BaseStream.Length) {
              _ = br.ReadUInt32(); //ph
              _ = br.ReadUInt32(); //sh

              Byte len = br.ReadByte(); //filename length
              Byte nul = br.ReadByte();

              if (nul != 0x00) { /* string second_len = "????"; */ }

              String filename = Encoding.Default.GetString(br.ReadBytes(len));
              testLines.Add(filename.ToLower());
            }

          } else {
            String[] lines = System.IO.File.ReadAllLines(file);

            foreach (String line in lines) {
              if (line.Contains('#')) { // Old hash dict format
                String[] temp = line.Split('#');

                if (temp.Length < 3 || temp[2].Length == 0) continue;
                else testLines.Add(temp[2].ToLower());

              } else if (line.Contains('?')) { // New hash dict format
                String[] temp = line.Split('?');

                if (temp.Length < 4 || temp[3].Length == 0) continue;
                else testLines.Add(temp[3].ToLower());

              } else {
                testLines.Add(line.ToLower());
              }
            }
          }

          m_hashData.Dictionary.CreateArchiveHashMasterList();

          foreach (String line in testLines) {
            FileId fileId = FileId.FromFilePath(line);
            IEnumerable<UpdateResults> results = m_hashData.Dictionary.UpdateHash(fileId.Ph, fileId.Sh, line, 0, true);

            if (results.Any()) {
              m_foundFiles.Add(line);
            }
          }

          testLines.Clear();
        }
      }
    }

    #endregion Hash List Methods

    private void JbaSkeletonButtonCheckedChanged(Object sender, EventArgs e) {
      if (!m_jbaActive
          || m_panelRender == null
          || m_jbaSkeletonButton == null) {
        return;
      }

      m_panelRender.SetShowSkeleton(m_jbaSkeletonButton.Checked);
      m_jbaSkeletonButton.ToolTipText = m_jbaSkeletonButton.Checked
        ? "Hide animation skeleton and bone names"
        : "Show animation skeleton and bone names";
    }

    private void JbaUiTimerTick(Object sender, EventArgs e) {
      if (!m_jbaActive || m_panelRender == null) {
        m_jbaUiTimer?.Stop();
        return;
      }

      UpdateJbaToolbar();
    }

    private void UpdateJbaToolbar() {
      if (!m_jbaActive || m_panelRender == null) return;

      Single current = Math.Max(0.0F, m_panelRender.AnimationTime);
      Single total = Math.Max(0.0F, m_panelRender.AnimationLength);
      Int32 frame = Math.Max(0, m_panelRender.AnimationFrame);
      Int32 frameCount = Math.Max(1, m_panelRender.AnimationFrameCount);

      TimeSpan currentTime = TimeSpan.FromSeconds(current);
      TimeSpan totalTime = TimeSpan.FromSeconds(total);
      toolStrip1Label1.Text =
        $"{currentTime.Minutes:00}:{currentTime.Seconds:00}.{currentTime.Milliseconds:000}"
        + "/"
        + $"{totalTime.Minutes:00}:{totalTime.Seconds:00}.{totalTime.Milliseconds:000}";

      toolStrip1ProgressBar1.Maximum = Math.Max(1, frameCount - 1);
      toolStrip1ProgressBar1.Value =
        Math.Min(toolStrip1ProgressBar1.Maximum, Math.Max(0, frame));

      if (m_panelRender.AnimationPlaying) {
        toolStrip1Button1.Text = ";";
        toolStrip1Button1.ToolTipText = "Pause";
      } else {
        toolStrip1Button1.Text = "4";
        toolStrip1Button1.ToolTipText = "Play";
      }
    }

    #region ToolStrip1
    private void ToolStrip1Button1Click(Object sender, EventArgs e) {
      if (m_jbaActive && m_panelRender != null) {
        if (m_panelRender.AnimationPlaying)
          m_panelRender.PauseAnimation();
        else
          m_panelRender.PlayAnimation();

        UpdateJbaToolbar();
        return;
      }

      if (m_waveOut == null) return;

      if (m_waveOut.PlaybackState == PlaybackState.Paused) {
        toolStrip1Button1.Text = ";";
        toolStrip1Button1.ToolTipText = "Pause";
        m_waveOut.Play();
        return;
      }

      if (m_waveOut.PlaybackState == PlaybackState.Playing) {
        toolStrip1Button1.Text = "4";
        toolStrip1Button1.ToolTipText = "Play";
        m_waveOut.Pause();
        return;
      }
    }

    private void ToolStrip1Button2Click(Object sender, EventArgs e) {
      if (m_jbaActive && m_panelRender != null) {
        m_panelRender.StopAnimation();
        UpdateJbaToolbar();
        return;
      }

      if (m_waveOut == null) return;

      m_waveOut.Stop();
      m_audioPlaying = false;

      toolStrip1Label1.Text = "00:00/00:00";

      toolStrip1Button1.Text = ";";
      toolStrip1Button1.ToolTipText = "Pause";

      toolStrip1ProgressBar1.Value = 0;
    }

    private void ToolStrip1Button3Click(Object sender, EventArgs e) {
      if (m_jbaActive && m_panelRender != null) {
        toolStrip1Button3.Checked = !toolStrip1Button3.Checked;
        m_panelRender.SetAnimationLoop(toolStrip1Button3.Checked);
        toolStrip1Button3.ToolTipText =
          toolStrip1Button3.Checked ? "Loop animation: On" : "Loop animation: Off";
        return;
      }

      if (m_waveOut == null) return;

      if (m_waveOut.Volume == 0) {
        toolStrip1Button3.Checked = false;
        toolStrip1Button3.ToolTipText = "Mute";
        m_waveOut.Volume = 1.0F;
        return;
      }

      if (m_waveOut.Volume != 0) {
        toolStrip1Button3.Checked = true;
        toolStrip1Button3.ToolTipText = "Unmute";
        m_waveOut.Volume = 0.0F;
      }
    }

    private void ToolStrip1Hide() {
      if (InvokeRequired) Invoke(new Action(() => ToolStrip1Hide()));
      else toolStrip1.Visible = false;
    }

    private void ToolStrip1Show() {
      if (InvokeRequired) Invoke(new Action(() => ToolStrip1Show()));
      else toolStrip1.Visible = true;
    }

    #endregion

    #region ToolStripProgressBar1
    private void ProgressBar1Hide() {
      if (InvokeRequired) Invoke(new Action(() => ProgressBar1Hide()));
      else toolStripProgressBar1.Visible = false;
    }

    private void ProgressBar1Show() {
      if (InvokeRequired) Invoke(new Action(() => ProgressBar1Show()));
      else toolStripProgressBar1.Visible = true;
    }

    private void ProgressBar1Style(ProgressBarStyle style) {
      if (InvokeRequired) Invoke(new Action(() => ProgressBar1Style(style)));
      else toolStripProgressBar1.Style = style;
    }

    private void ProgressBar1Value(Int32 value) {
      if (InvokeRequired) Invoke(new Action(() => ProgressBar1Value(value)));
      else toolStripProgressBar1.Value = value;
    }

    #endregion

    #region ToolStripStatusLabel1
    // private void StatusLabel1Hide() {
    //   if (statusStrip1.InvokeRequired)
    //     statusStrip1.Invoke(new Action(() => StatusLabel1Hide()));
    //   else
    //     toolStripStatusLabel1.Visible = false;
    // }

    // private void StatusLabel1Show() {
    //   if (statusStrip1.InvokeRequired)
    //     statusStrip1.Invoke(new Action(() => StatusLabel1Show()));
    //   else
    //     toolStripStatusLabel1.Visible = true;
    // }

    internal void StatusLabel1Text(String text) {
      if (InvokeRequired) Invoke(new Action(() => StatusLabel1Text(text)));
      else toolStripStatusLabel1.Text = text;
    }

    #endregion

    #region ToolStripStatusLabel2
    // private void StatusLabel2Hide() {
    //   if (statusStrip1.InvokeRequired)
    //     statusStrip1.Invoke(new Action(() => StatusLabel2Hide()));
    //   else
    //     toolStripStatusLabel2.Visible = false;
    // }

    // private void StatusLabel2Show() {
    //   if (statusStrip1.InvokeRequired)
    //     statusStrip1.Invoke(new Action(() => StatusLabel2Show()));
    //   else
    //     toolStripStatusLabel2.Visible = true;
    // }

    private void StatusLabel2Text(String text) {
      if (InvokeRequired) Invoke(new Action(() => StatusLabel2Text(text)));
      else toolStripStatusLabel2.Text = text;
    }

    #endregion

    #region TreeViewFast1
    private void TreeViewFast1AfterSelect(Object sender, TreeViewEventArgs e) {
      TreeNode node = treeViewFast1.SelectedNode;
      TreeListItem asset = (TreeListItem)node.Tag;

      Text = "Asset Browser - " + asset.Id.ToString();

      if (m_waveOut != null && m_waveOut.PlaybackState != PlaybackState.Stopped) m_waveOut.Stop();

      if (asset.HashInfo.File != null) {
        DataTable dt = new DataTable();
        HashFileInfo info = asset.HashInfo;

        dt.Columns.Add("Property");
        dt.Columns.Add("Value");
        dt.Rows.Add(new String[] {
          "Archive",
          info.Source.ToString()
        });
        dt.Rows.Add(new String[] {
          "File ID",
          $"{info.File.FileInfo.FileId:X16}"
        });

        if (info.IsNamed) {
          dt.Rows.Add(new String[] {
            "File Name",
            info.FileName
          });
        } else {
          dt.Rows.Add(new String[] {
            "File Name",
            info.FileName + "." + info.Extension
          });
        }

        dt.Rows.Add(new String[] {
          "File Type",
          info.Extension
        });
        dt.Rows.Add(new String[] {
          "Path",
          info.Directory
        });
        if (m_compareFiles && asset.CompareState != BuildFileState.None) {
          dt.Rows.Add(new String[] {
            "State",
            asset.CompareState.ToString()
          });
          dt.Rows.Add(new String[] {
            "Build Source",
            asset.CompareState == BuildFileState.Removed ? "Previous" : "Current"
          });

          if (asset.CompareState == BuildFileState.Changed && asset.PreviousHashInfo?.File != null) {
            dt.Rows.Add(new String[] {
              "Previous Archive",
              asset.PreviousHashInfo.Source
            });
            dt.Rows.Add(new String[] {
              "Previous Checksum",
              $"{asset.PreviousHashInfo.File.FileInfo.Checksum:X8}"
            });
            dt.Rows.Add(new String[] {
              "Previous Size",
              asset.PreviousHashInfo.File.FileInfo.UncompressedSize.ToString()
            });
          }
        } else {
          dt.Rows.Add(new String[] {
            "State",
            info.FileState.ToString()
          });
        }
        dt.Rows.Add(new String[] {
          "Compressed Size",
          info.File.FileInfo.CompressedSize.ToString()
        });
        dt.Rows.Add(new String[] {
          "Uncompressed Size",
          info.File.FileInfo.UncompressedSize.ToString()
        });
        dt.Rows.Add(new String[] {
          "Header Size",
          info.File.FileInfo.HeaderSize.ToString()
        });
        dt.Rows.Add(new String[] {
          "Offset",
          ((Int64)info.File.FileInfo.Offset).ToString()
        });
        dt.Rows.Add(new String[] {
          "Primary Hash",
          $"{info.File.FileInfo.PrimaryHash:X8}"
        });
        dt.Rows.Add(new String[] {
            "Secondary Hash",
            $"{info.File.FileInfo.SecondaryHash:X8}"
        });
        dt.Rows.Add(new String[] {
          "Checksum",
          $"{info.File.FileInfo.Checksum:X8}"
        });
        dt.Rows.Add(new String[] {
          "Is Compressed",
          info.File.FileInfo.IsCompressed.ToString()
        });

        dataGridView1.DataSource = dt;
        dataGridView1.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.AllCells;

        if (m_autoPreview) PreviewAsset(asset);
      }
    }

    private void TreeViewFast1Disable() {
      if (treeViewFast1.InvokeRequired)
        treeViewFast1.Invoke(new Action(() => TreeViewFast1Disable()));
      else
        treeViewFast1.Enabled = false;
    }

    private void TreeViewFast1Enable() {
      if (treeViewFast1.InvokeRequired)
        treeViewFast1.Invoke(new Action(() => TreeViewFast1Enable()));
      else
        treeViewFast1.Enabled = true;
    }

    private void TreeViewFast1Hide() {
      if (treeViewFast1.InvokeRequired)
        treeViewFast1.Invoke(new Action(() => TreeViewFast1Hide()));
      else
        treeViewFast1.Visible = false;
    }

    private void TreeViewFast1KeyDown(Object sender, KeyEventArgs e) {
      if (e.Control && e.KeyCode == Keys.F)
        txtSearch.Focus();
    }

    private void TreeViewFast1MouseHover(Object sender, EventArgs e) {
      if (!m_closing) treeViewFast1.Focus();
    }

    private void TreeViewFast1MouseUp(Object sender, MouseEventArgs e) {
      if (e.Button == MouseButtons.Right) {
        treeViewFast1.SelectedNode = treeViewFast1.GetNodeAt(e.X, e.Y);

        if (treeViewFast1.SelectedNode != null)
          contextMenuStrip1.Show(treeViewFast1, e.Location);
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
      if (InvokeRequired) Invoke(new Action(() => TreeViewGrid1ExpandAll()));
      else treeViewGrid1.ExpandAll();
    }

    private void TreeViewGrid1Hide() {
      if (InvokeRequired) Invoke(new Action(() => TreeViewGrid1Hide()));
      else treeViewGrid1.Visible = false;
    }

    private void TreeViewGrid1Roots(ArrayList roots) {
      if (InvokeRequired) Invoke(new Action(() => TreeViewGrid1Roots(roots)));
      else treeViewGrid1.Roots = roots;
    }

    private async void TreeViewGrid1SelectedIndexChanged(Object sender, EventArgs e) {
      m_audioPlaying = false;

      if (treeViewGrid1.SelectedObjects != null && treeViewGrid1.SelectedObjects.Count > 1) {
        //Don't preview audio if we selected multiple files.
        m_audioPlaying = false;
        // btnAudioStop.Enabled = false;
        return;
      }

      if (treeViewGrid1.SelectedItem == null) return;

      Object selectedRow = treeViewGrid1.SelectedItem.RowObject;

      if (!m_audioPlaying && selectedRow.GetType() == typeof(WemListItem)) {
        WemListItem row = (WemListItem)selectedRow;
        ViewWEM wem = row.Obj;

        await Task.Run(() => PreviewAssetWEM(wem));
      }
    }

    private void TreeViewGrid1Show() {
      if (InvokeRequired) Invoke(new Action(() => TreeViewGrid1Show()));
      else treeViewGrid1.Visible = true;
    }

    #endregion

    #region TxtSearch
    private void TxtSearchKeyDown(Object sender, KeyEventArgs e) {
      if (e.KeyCode == Keys.Enter && !String.IsNullOrEmpty(txtSearch.Text))
        Search();
    }

    #endregion

















    #region Extract
    private void ExtractAsset(HashFileInfo assetFile) {
      // Don't need bucket files output all the time
      if (assetFile.FileName.EndsWith(".bkt")) return;

      String fileName;
      String directory;

      if (assetFile.IsNamed)
        fileName =
          m_extractPath
            + String.Join("\\", assetFile.Directory, assetFile.FileName).Replace("/", "\\");
      else
        fileName = m_extractPath + assetFile.Directory.Replace("/", "\\") + "\\"
          + assetFile.Extension.ToLower() + "\\" + assetFile.FileName + "." + assetFile.Extension;

      fileName = fileName.Replace("\\\\", "\\");
      directory = Path.GetDirectoryName(fileName);

      if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);

      using (Stream file = assetFile.File.Open()) {
        using FileStream outputStream = System.IO.File.Create(fileName);
        Byte[] fileBuffer = new Byte[assetFile.File.FileInfo.UncompressedSize];

        file.Read(fileBuffer, 0, fileBuffer.Length);
        outputStream.Write(fileBuffer, 0, fileBuffer.Length);
      }

      m_extractCount++;
    }
    private void ExtractByExtensionToolStripMenuItemClick(Object sender, EventArgs e) {
      AssetBrowserExtractExt frmExt = new AssetBrowserExtractExt();
      DialogResult result = frmExt.ShowDialog(this);

      if (result == DialogResult.OK) {
        m_extractByExtensions = true;
        m_extractExtensions = frmExt.GetExtensions();

        BtnExtractClick(this, null);
      }
    }
    private void ExtractByNode(TreeNodeCollection nodes) {
      foreach (TreeNode child in nodes) {
        TreeListItem asset = (TreeListItem)child.Tag;

        if (asset.HashInfo.File != null) {
          if (m_extractByExtensions) {
            if (m_extractExtensions.Contains(asset.HashInfo.Extension.ToUpper())) {
              ExtractAsset(asset.HashInfo);

            } else continue;
          } else ExtractAsset(asset.HashInfo);
        }

        if (child.Nodes.Count > 0) ExtractByNode(child.Nodes);
      }
    }
    private void ExtractToolStripMenuItemClick(Object sender, EventArgs e) {
      m_extractByExtensions = false;
      BtnExtractClick(this, null);
    }

    #endregion

    #region Hide Methods
    internal void HideViewers() {
      hexBox1.Visible = false;
      pictureBox1.Visible = false;
      renderPanel.Visible = false;
      treeViewGrid1.Visible = false;
      txtRawView.Visible = false;
      webBrowser1.Visible = false;
    }

    #endregion
  }
}
