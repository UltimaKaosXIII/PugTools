using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
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
using DrawingColor = System.Drawing.Color;
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
    private DdsPreviewControl m_ddsPreview;
    private EffectSpecPreviewControl m_effectSpecPreview;
    private MaterialSpecPreviewControl m_materialSpecPreview;
    private StructuredTextAssetPreviewControl m_structuredTextPreview;
    private AudioBankPreviewControl m_audioBankPreview;
    private Thread m_render;
    private ArrayList m_rootList; // = new ArrayList();
    private Int32 m_searchIndex; // = 0;
    private List<String> m_searchNodes; // = new List<String>();
    private System.Windows.Forms.Timer m_assetTreeFilterTimer;
    private CancellationTokenSource m_assetTreeFilterCancellation;
    private Int32 m_assetTreeFilterGeneration;
    private const Int32 AssetTreeFilterDisplayLimit = 2500;
    private const Int32 AssetTreeFilterNodeLimit = 8000;
    private const Int32 AssetTreeFilterAutoExpandLimit = 100;
    private AssetSearchEntry[] m_assetSearchIndex = Array.Empty<AssetSearchEntry>();
    private TreeViewFast.Controls.TreeViewFast.PreparedTree m_fullAssetTree;
    private TreeViewFast.Controls.TreeViewFast m_assetFilterTree;
    private Int32 m_totalFilesSearched; // = 0;
    private Int32 m_totalNamesFound; // = 0;
    private readonly Boolean m_assetsUsePts;
    private WaveOutEvent m_waveOut;
    private XmlDocument m_xmlDoc;
    private DataObjectModel m_navigationDom;
    private readonly Object m_previewTextureDecodeLock = new Object();
    // HashDictionary is process-wide. Multiple Asset Browser windows may coexist, but two
    // Filename Finder passes must not mutate its SortedLists at the same time.
    private static readonly Object s_filenameFinderLock = new Object();
    // Jedipedia-style aliases for /resources/world/areas/<id> and
    // /resources/world/livecontent/systemgenerated/<id>.  The dictionary keys stay numeric so
    // extraction/navigation paths are unchanged; only the visible folder label and search gain
    // the authored internal map name (for example 4611686019802841877  hut_main).
    private readonly Dictionary<UInt64, String> m_worldAreaInternalNames = new Dictionary<UInt64, String>();

    // File-reader style navigation. This is deliberately an in-memory history over the already
    // built asset tree; it does not create a database or rescan the TOR archives.
    private readonly List<String> m_assetNavigationHistory = new List<String>();
    private Int32 m_assetNavigationHistoryIndex = -1;
    private Boolean m_assetHistoryNavigation;
    private BrowserSessionTabs m_assetPageTabs;
    private String m_pendingResourceNavigation;
    private Boolean m_pendingResourceNavigationNewTab;

    // DirectMusic SGT playback. SGTs are decoded to an in-memory PCM WAV so beta
    // Microsoft-ADPCM assets do not depend on an installed Windows ACM codec.
    private Boolean m_sgtActive;
    private Byte[] m_sgtDecodedWave;
    private String m_sgtDecodeError;
    private MemoryStream m_sgtPcmStream;
    private WaveFileReader m_sgtWaveReader;
    private WaveOutEvent m_sgtWaveOut;
    private System.Windows.Forms.Timer m_sgtUiTimer;
    private ToolStripButton m_sgtSaveButton;

    // JBA playback reuses the existing audio ToolStrip so the controls stay
    // consistent with WEM/BNK playback.
    private Boolean m_jbaActive;
    private System.Windows.Forms.Timer m_jbaUiTimer;
    private ToolStripButton m_jbaSkeletonButton;
    private ToolStripComboBox m_jbaSpeedCombo;
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
      InitializeDdsPreview();
      InitializeEffectSpecPreview();
      InitializeMaterialSpecPreview();
      InitializeStructuredTextPreview();
      InitializeAudioBankPreview();
      InitializeAssetFilterTreeView();
      InitializeAssetTreeLiveFilter();
      InitializeAssetPageTabs();
      InitializeIdenticalFilesContextMenu();
      // File-reader style cross-links for all structured tree previews, not only the
      // dedicated MAT/FX controls. Double-clicking a resource path opens it in-place.
      treeViewGrid1.MouseDoubleClick += TreeViewGrid1MouseDoubleClickNavigateAsset;
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

      m_sgtUiTimer = new System.Windows.Forms.Timer { Interval = 100 };
      m_sgtUiTimer.Tick += SgtUiTimerTick;

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

      m_jbaSpeedCombo = new ToolStripComboBox {
        AutoSize = false,
        Width = 62,
        DropDownStyle = ComboBoxStyle.DropDownList,
        ToolTipText = "JBA playback speed",
        Visible = false
      };
      m_jbaSpeedCombo.Items.AddRange(new Object[] { "0.25x", "0.5x", "1x", "1.5x", "2x", "4x" });
      m_jbaSpeedCombo.SelectedItem = "1x";
      m_jbaSpeedCombo.SelectedIndexChanged += JbaSpeedComboSelectedIndexChanged;
      Int32 jbaSpeedIndex = toolStrip1.Items.IndexOf(toolStrip1ProgressBar1);
      if (jbaSpeedIndex >= 0) toolStrip1.Items.Insert(jbaSpeedIndex, m_jbaSpeedCombo);
      else toolStrip1.Items.Add(m_jbaSpeedCombo);

      // ToolStripProgressBar itself does not expose a Click event in the same
      // useful way as a normal ProgressBar.  Its hosted control does, which lets
      // the JBA viewer scrub directly to a frame without adding another timeline.
      toolStrip1ProgressBar1.ProgressBar.MouseDown += JbaProgressBarMouseDown;

      m_sgtSaveButton = new ToolStripButton {
        AutoSize = true,
        DisplayStyle = ToolStripItemDisplayStyle.Text,
        Text = "Save WAV",
        ToolTipText = "Save the decoded SGT audio as a standard PCM WAV",
        Visible = false
      };
      m_sgtSaveButton.Click += SgtSaveButtonClick;
      Int32 sgtProgressIndex = toolStrip1.Items.IndexOf(toolStrip1ProgressBar1);
      if (sgtProgressIndex >= 0) toolStrip1.Items.Insert(sgtProgressIndex, m_sgtSaveButton);
      else toolStrip1.Items.Add(m_sgtSaveButton);

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
      try { m_assetTreeFilterTimer?.Stop(); m_assetTreeFilterTimer?.Dispose(); } catch { }
      m_assetTreeFilterTimer = null;
      try { m_assetTreeFilterCancellation?.Cancel(); m_assetTreeFilterCancellation?.Dispose(); } catch { }
      m_assetTreeFilterCancellation = null;
      // Do NOT unload the shared hash dictionary here. It is process-wide and can contain
      // millions of rows; another open browser may still be using it. The main application keeps
      // this cache alive and decides once, at process shutdown, whether filename changes are saved.

      if (m_panelRender != null) {
        ViewGR2 renderer = m_panelRender;
        Thread renderThread = m_render;
        m_panelRender = null;
        m_render = null;

        try { renderer.StopRender(); } catch { }

        // D3D resource release can take a noticeable amount of time on some
        // drivers. Never make FormClosed wait for it. The render loop has
        // already been told to stop; finish cleanup off the WinForms thread.
        BackgroundCleanup.Enqueue(() => {
          Boolean stopped = renderThread == null || !renderThread.IsAlive;
          if (!stopped) {
            try { stopped = renderThread.Join(5000); } catch { }
          }
          if (!stopped) return; // Prefer a bounded leak to freezing the whole app.
          try { renderer.Clear(); } catch { }
          try { renderer.Dispose(); } catch { }
        });
      }

      if (m_assetFilterTree != null) {
        try { m_assetFilterTree.Dispose(); } catch { }
        m_assetFilterTree = null;
      }

      // Form.Dispose() owns the main tree. Avoid explicitly traversing it a second time here.
      treeViewFast1 = null;

      try { StopSgtPreview(true); } catch { }
      try {
        if (m_audioPlaying) m_waveOut?.Stop();
        m_waveOut?.Dispose();
      } catch { }
      m_waveOut = null;
      m_audioPlaying = false;

      try {
        m_sgtUiTimer?.Stop();
        m_sgtUiTimer?.Dispose();
      } catch { }
      m_sgtUiTimer = null;

      try {
        m_jbaUiTimer?.Stop();
        m_jbaUiTimer?.Dispose();
      } catch { }
      m_jbaUiTimer = null;
      m_jbaActive = false;

      try { m_inputStream?.Dispose(); } catch { }
      m_inputStream = null;
      try { m_effectSpecPreview?.Dispose(); } catch { }
      m_effectSpecPreview = null;
      try { m_materialSpecPreview?.Dispose(); } catch { }
      m_materialSpecPreview = null;
      try { m_structuredTextPreview?.Dispose(); } catch { }
      m_structuredTextPreview = null;

      m_assetDict = null;
      m_navigationDom = null;
      m_assetSearchIndex = Array.Empty<AssetSearchEntry>();
      m_fullAssetTree = null;

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

    }

    private void AssetBrowserFormClosing(Object sender, FormClosingEventArgs e) {
      m_closing = true;

      // Stop high-frequency/background work before controls and D3D handles
      // are destroyed. Tree preparation and live filtering both observe these
      // cancellation signals and can now terminate during a large sort/build.
      try { m_assetTreeFilterCancellation?.Cancel(); } catch { }
      try { m_assetTreeFilterTimer?.Stop(); } catch { }
      try { m_panelRender?.StopRender(); } catch { }
      try {
        if (m_audioPlaying) m_waveOut?.Stop();
      } catch { }
    }

    private void AssetBrowserFormResize(Object sender, EventArgs e) {
      Int32 tabHeight = m_assetPageTabs?.Height ?? 0;
      Size treeSize = new Size(splitContainer2.Panel1.Width, Math.Max(20, splitContainer2.Panel1.Height - 70 - tabHeight));
      if (treeViewFast1 != null) treeViewFast1.Size = treeSize;
      if (m_assetFilterTree != null) m_assetFilterTree.Size = treeSize;
      if (m_assetPageTabs != null) m_assetPageTabs.Width = splitContainer2.Panel1.Width;
    }

    #endregion

    #region Background Wokers Methods
    private void BackgroundWorker1Run(Object sender, DoWorkEventArgs e) {
      if (m_closing) return;

      m_currentAssets = AssetHandler.Instance.GetCurrentAssets(m_assetsLocation, m_assetsUsePts);
      LocalizationResolver.Apply(m_currentAssets, Config.Language);

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

      InitializeWorldAreaInternalNames();

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
            HashFileInfo hashInfo = new HashFileInfo(
              file.FileInfo.PrimaryHash, file.FileInfo.SecondaryHash, file, true, false
            );
            RegisterWorldAreaInternalName(hashInfo);

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
        if (m_closing) return;
        String[] temp = dir.Split('/');
        Int32 intLength = temp.Length;

        for (Int32 intCount2 = 0; intCount2 <= intLength; intCount2++) {
          String output = String.Join("/", temp, 0, intCount2);

          if (output.Length > 0) allDirs.Add(output);
        }
      }
      foreach (String dir in allDirs) {
        if (m_closing) return;
        String[] temp = dir.Split('/');
        String parentDir = String.Join("/", temp.Take(temp.Length - 1));

        if (parentDir.Length == 0) parentDir = "/root";

        String display = GetWorldAreaDirectoryDisplayName(dir, temp.Last());
        TreeListItem asset = new TreeListItem(dir, parentDir, display, empty);

        if (!m_assetDict.ContainsKey(dir)) m_assetDict.Add(dir, asset);
      }
    }

    private void BuildCompareFileTree() {
      HashSet<String> allDirs = new HashSet<String>();
      HashSet<String> fileDirs = new HashSet<String>();
      List<BuildFileDifference> differences =
        BuildAssetComparer.Compare(m_currentAssets, m_previousAssets, true, () => m_closing);

      Int32 newCount = 0;
      Int32 changedCount = 0;
      Int32 removedCount = 0;
      Int32 unchangedCount = 0;

      foreach (BuildFileDifference difference in differences) {
        if (m_closing) return;

        BuildFileRecord display = difference.DisplayRecord;
        if (display?.HashInfo == null) continue;

        HashFileInfo info = display.HashInfo;
        RegisterWorldAreaInternalName(info);
        String prefix = difference.State switch {
          BuildFileState.New => "/root/new",
          BuildFileState.Changed => "/root/changed",
          BuildFileState.Removed => "/root/removed",
          BuildFileState.Unchanged => "/root/unchanged",
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
          case BuildFileState.Unchanged:
            unchangedCount++;
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
      m_assetDict.Add(
        "/root/unchanged",
        new TreeListItem(
          "/root/unchanged", "/root", "Unchanged Files (" + unchangedCount + ")", empty
        )
      );

      foreach (String dir in fileDirs) {
        if (m_closing) return;
        String[] temp = dir.Split('/');
        for (Int32 i = 0; i <= temp.Length; i++) {
          String output = String.Join("/", temp, 0, i);
          if (output.Length > 0) allDirs.Add(output);
        }
      }

      foreach (String dir in allDirs) {
        if (m_closing) return;
        String[] temp = dir.Split('/');
        String parentDir = String.Join("/", temp.Take(temp.Length - 1));
        if (parentDir.Length == 0) parentDir = "/root";

        String display = GetWorldAreaDirectoryDisplayName(dir, temp.Last());
        if (!m_assetDict.ContainsKey(dir))
          m_assetDict.Add(dir, new TreeListItem(dir, parentDir, display, empty));
      }

      // This count is used only for hash-dictionary save prompts. Build comparison is
      // read-only and deliberately does not change dictionary CRC baselines.
      m_modNewCount = 0;
      backgroundWorker2.ReportProgress(100);
    }

    private void InitializeWorldAreaInternalNames() {
      m_worldAreaInternalNames.Clear();

      // Jedipedia's area-id catalog is the quickest complete source for known maps.  Runtime
      // WorldAreaNames.xml entries can extend it, and an installed area.dat wins below when we
      // encounter it so renamed/new maps do not require a PugTools update.
      foreach (WorldAreaCatalogEntry entry in WorldAreaCatalog.Entries) {
        if (entry == null || String.IsNullOrWhiteSpace(entry.InternalName)) continue;
        m_worldAreaInternalNames[entry.Id] = entry.InternalName.Trim();
      }

      foreach (KeyValuePair<UInt64, WorldAreaOverride> pair in WorldAreaNameOverrides.LoadEntries()) {
        if (pair.Value == null || String.IsNullOrWhiteSpace(pair.Value.InternalName)) continue;
        m_worldAreaInternalNames[pair.Key] = pair.Value.InternalName.Trim();
      }
    }

    private void RegisterWorldAreaInternalName(HashFileInfo info) {
      if (info == null || !info.IsNamed || info.File == null
          || !String.Equals(info.FileName, "area.dat", StringComparison.OrdinalIgnoreCase)) return;
      if (!TryGetWorldAreaIdFromDirectory(info.Directory, out UInt64 areaId)) return;

      String internalName = WorldBrowser.ReadAreaInternalName(info.File);
      if (!String.IsNullOrWhiteSpace(internalName))
        m_worldAreaInternalNames[areaId] = internalName.Trim();
    }

    private static Boolean TryGetWorldAreaIdFromDirectory(String directory, out UInt64 areaId) {
      areaId = 0;
      if (String.IsNullOrWhiteSpace(directory)) return false;
      String[] parts = directory.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
      if (parts.Length < 4 || !UInt64.TryParse(parts[parts.Length - 1], out areaId)) return false;

      Int32 last = parts.Length - 1;
      Boolean regularArea = last >= 3
        && String.Equals(parts[last - 1], "areas", StringComparison.OrdinalIgnoreCase)
        && String.Equals(parts[last - 2], "world", StringComparison.OrdinalIgnoreCase)
        && String.Equals(parts[last - 3], "resources", StringComparison.OrdinalIgnoreCase);
      Boolean generatedArea = last >= 4
        && String.Equals(parts[last - 1], "systemgenerated", StringComparison.OrdinalIgnoreCase)
        && String.Equals(parts[last - 2], "livecontent", StringComparison.OrdinalIgnoreCase)
        && String.Equals(parts[last - 3], "world", StringComparison.OrdinalIgnoreCase)
        && String.Equals(parts[last - 4], "resources", StringComparison.OrdinalIgnoreCase);
      return regularArea || generatedArea;
    }

    private static Boolean TryGetWorldAreaIdFromTreeDirectory(String directory, out UInt64 areaId) {
      areaId = 0;
      if (String.IsNullOrWhiteSpace(directory)) return false;
      String[] parts = directory.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
      if (parts.Length < 4 || !UInt64.TryParse(parts[parts.Length - 1], out areaId)) return false;

      Int32 last = parts.Length - 1;
      Boolean regularArea = last >= 3
        && String.Equals(parts[last - 1], "areas", StringComparison.OrdinalIgnoreCase)
        && String.Equals(parts[last - 2], "world", StringComparison.OrdinalIgnoreCase)
        && String.Equals(parts[last - 3], "resources", StringComparison.OrdinalIgnoreCase);
      Boolean generatedArea = last >= 4
        && String.Equals(parts[last - 1], "systemgenerated", StringComparison.OrdinalIgnoreCase)
        && String.Equals(parts[last - 2], "livecontent", StringComparison.OrdinalIgnoreCase)
        && String.Equals(parts[last - 3], "world", StringComparison.OrdinalIgnoreCase)
        && String.Equals(parts[last - 4], "resources", StringComparison.OrdinalIgnoreCase);
      return regularArea || generatedArea;
    }

    private String GetWorldAreaDirectoryDisplayName(String directory, String fallback) {
      if (!TryGetWorldAreaIdFromTreeDirectory(directory, out UInt64 areaId)
          || !m_worldAreaInternalNames.TryGetValue(areaId, out String internalName)
          || String.IsNullOrWhiteSpace(internalName)) return fallback;
      return fallback + "  " + internalName;
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

      // Build the expensive managed representation without touching the WinForms control.
      // The old implementation called BeginUpdate/LoadItems directly from this worker thread,
      // which was a cross-thread UI access and still forced every later filter reset to allocate
      // and sort the complete tree again.
      Dictionary<String, String> heroScriptAliases = BuildHeroScriptQuickOpenAliases(m_currentAssets);
      m_assetSearchIndex = m_assetDict.Values
        .Where(item => item?.HashInfo?.File != null)
        .Select(item => {
          String resourcePath = BuildAssetQuickOpenPath(item.HashInfo);
          heroScriptAliases.TryGetValue(resourcePath ?? String.Empty, out String scriptAlias);
          return new AssetSearchEntry(
            item.Id,
            item.DisplayName,
            item.HashInfo.IsNamed,
            item.HashInfo.FirstSeenVersion,
            resourcePath,
            item.HashInfo.File == null ? String.Empty : item.HashInfo.File.FileInfo.FileId.ToString("X16", CultureInfo.InvariantCulture),
            scriptAlias
          );
        })
        .ToArray();

      String getId(TreeListItem x) => x.Id;
      String getParentId(TreeListItem x) => x.ParentId;
      String getDisplayName(TreeListItem x) => x.DisplayName;
      Int32 getImageIndex(TreeListItem x) => x?.HashInfo?.File != null ? 2 : 1;
      Int32 compare(TreeListItem x, TreeListItem y) {
        Boolean xFile = x?.HashInfo?.File != null;
        Boolean yFile = y?.HashInfo?.File != null;
        if (xFile != yFile) return xFile ? 1 : -1;
        return String.Compare(x?.Id, y?.Id, StringComparison.Ordinal);
      }

      m_fullAssetTree = TreeViewFast.Controls.TreeViewFast.PrepareItems(
        m_assetDict.Values, getId, getParentId, getDisplayName, getImageIndex, compare,
        () => m_closing
      );
    }

    private void BackgroundWorker3Completed(Object sender, RunWorkerCompletedEventArgs e) {
      if (m_closing) return;

      if (e.Error != null) {
        StatusLabel1Text("Unable to build asset tree.");
        MessageBox.Show(e.Error.Message, "Asset Browser", MessageBoxButtons.OK, MessageBoxIcon.Error);
        LoadingSwirl1Hide();
        ProgressBar1Hide();
        return;
      }

      treeViewFast1.BeginUpdate();
      try {
        if (m_fullAssetTree != null) treeViewFast1.LoadPrepared(m_fullAssetTree);
      } finally {
        treeViewFast1.EndUpdate();
      }

      if (treeViewFast1.Nodes.Count > 0) treeViewFast1.Nodes[0].Expand();
      treeViewFast1.Show();

      m_panelRender = new ViewGR2(Handle, this, "renderPanel");
      m_panelRender.Init();

      loadingSwirl1.Hide();
      toolStripStatusLabel1.Text = m_compareFiles
        ? "Comparison loaded. Showing New, Changed, Removed and Unchanged files."
        : "Loading Complete.";
      toolStripProgressBar1.Visible = false;
      toolStripProgressBar1.Value = 0;
      toolStripProgressBar1.Style = ProgressBarStyle.Continuous;

      ButtonsEnable();

      txtSearch.Focus();
      ApplyPendingResourceNavigation();
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
      if (txtSearch == null) return;
      if (txtSearch.TextLength > 0) txtSearch.Clear();
      else ApplyAssetTreeLiveFilter();
      txtSearch.Focus();
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
      TreeNode node = ActiveAssetTree?.SelectedNode;

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

    #region DDS Preview
    private void InitializeDdsPreview() {
      m_ddsPreview = new DdsPreviewControl {
        Dock = DockStyle.Fill,
        Visible = false,
        BackColor = DrawingColor.White,
        Checkerboard = true
      };
      splitContainer3.Panel1.Controls.Add(m_ddsPreview);
      m_ddsPreview.BringToFront();
    }

    private void ConfigureDdsPreviewBackground(String directory) {
      Boolean solidBackground = !String.IsNullOrEmpty(directory)
        && (directory.Contains("codex", StringComparison.OrdinalIgnoreCase)
            || directory.Contains("reputation", StringComparison.OrdinalIgnoreCase)
            || directory.Contains("tutorials", StringComparison.OrdinalIgnoreCase));

      m_ddsPreview.Checkerboard = !solidBackground;
      m_ddsPreview.BackColor = solidBackground ? DrawingColor.Black : DrawingColor.White;
    }

    #endregion

    #region Effect Spec Preview
    private void InitializeEffectSpecPreview() {
      m_effectSpecPreview = new EffectSpecPreviewControl {
        Dock = DockStyle.Fill,
        Visible = false,
        TextureLoader = LoadEffectTextureBitmap,
        PrtTextLoader = LoadEffectPrtText,
        ResourceExists = MaterialResourceExists,
        OpenResourceRequested = NavigateToEffectResource
      };
      splitContainer3.Panel1.Controls.Add(m_effectSpecPreview);
      m_effectSpecPreview.BringToFront();
    }

    private void InitializeMaterialSpecPreview() {
      m_materialSpecPreview = new MaterialSpecPreviewControl {
        Dock = DockStyle.Fill,
        Visible = false,
        TextureLoader = LoadEffectTextureBitmap,
        ResourceExists = MaterialResourceExists,
        OpenResourceRequested = NavigateToEffectResource
      };
      splitContainer3.Panel1.Controls.Add(m_materialSpecPreview);
      m_materialSpecPreview.BringToFront();
    }

    private void InitializeStructuredTextPreview() {
      m_structuredTextPreview = new StructuredTextAssetPreviewControl {
        Dock = DockStyle.Fill,
        Visible = false,
        ResourceExists = MaterialResourceExists,
        OpenResourceRequested = NavigateToEffectResource
      };
      splitContainer3.Panel1.Controls.Add(m_structuredTextPreview);
      m_structuredTextPreview.BringToFront();
    }

    private void InitializeAudioBankPreview() {
      m_audioBankPreview = new AudioBankPreviewControl {
        Dock = DockStyle.Fill,
        Visible = false,
        ResourceExists = MaterialResourceExists,
        OpenResourceRequested = NavigateToEffectResource,
        PlayEmbeddedRequested = PlayAudioBankEmbedded
      };
      splitContainer3.Panel1.Controls.Add(m_audioBankPreview);
      m_audioBankPreview.BringToFront();
    }

    private Boolean MaterialResourceExists(String requestedPath) {
      if (String.IsNullOrWhiteSpace(requestedPath)) return false;
      Assets assets = m_currentAssets ?? m_previousAssets;
      if (assets == null) return false;
      String normalized = requestedPath.Trim().Replace('\\', '/');
      if (!normalized.StartsWith("/resources/", StringComparison.OrdinalIgnoreCase))
        normalized = "/resources/" + normalized.TrimStart('/');
      using TorArchive.File file = assets.FindFile(normalized);
      return file != null;
    }

    private String ReadCurrentPreviewTextSmart() {
      if (m_inputStream == null) return String.Empty;
      m_inputStream.Position = 0;
      using MemoryStream copy = new MemoryStream();
      m_inputStream.CopyTo(copy);
      Byte[] bytes = copy.ToArray();
      m_inputStream.Position = 0;
      if (bytes.Length == 0) return String.Empty;

      // SWTOR text assets are not consistently encoded. In particular FXSPEC files are often
      // UTF-16LE (sometimes without a BOM) and some end in an extra NUL code unit. Reading those
      // as UTF-8 produces "<\0..." and XmlDocument then fails at line 1, position 2. This mirrors
      // Jedipedia's FXSPEC reader: detect UTF-16 from BOM/alternating NUL bytes and strip the
      // optional BOM/trailing zero before handing the text to the structured parsers.
      Encoding encoding = Encoding.UTF8;
      Int32 offset = 0;
      if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) {
        encoding = Encoding.UTF8;
        offset = 3;
      } else if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) {
        encoding = Encoding.Unicode;
        offset = 2;
      } else if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) {
        encoding = Encoding.BigEndianUnicode;
        offset = 2;
      } else if (bytes.Length >= 4) {
        Int32 evenZero = 0, oddZero = 0, sample = Math.Min(bytes.Length, 256);
        for (Int32 i = 0; i < sample; i++) {
          if (bytes[i] != 0) continue;
          if ((i & 1) == 0) evenZero++; else oddZero++;
        }
        if (oddZero >= 2 && oddZero > evenZero * 2) encoding = Encoding.Unicode;
        else if (evenZero >= 2 && evenZero > oddZero * 2) encoding = Encoding.BigEndianUnicode;
      }

      Int32 byteCount = bytes.Length - offset;
      if ((encoding == Encoding.Unicode || encoding == Encoding.BigEndianUnicode)
          && (byteCount & 1) != 0 && bytes[bytes.Length - 1] == 0) {
        // A few cooked text files append a single zero byte rather than a complete UTF-16 NUL
        // code unit. Ignore that dangling byte so the decoder does not append U+FFFD.
        byteCount--;
      }
      String text = encoding.GetString(bytes, offset, byteCount);
      if (text.Length > 0 && text[0] == '\uFEFF') text = text.Substring(1);
      // Text specs must not contain NULs. Shipped FXSPECs commonly have one at EOF; removing any
      // remaining NUL also makes BOM-less UTF-16 edge cases fail soft rather than poison XML parsing.
      if (text.IndexOf('\0') >= 0) text = text.Replace("\0", String.Empty);
      return text;
    }

    private String ReadCurrentPreviewText() {
      return ReadCurrentPreviewTextSmart();
    }

    private String LoadEffectPrtText(String requestedPath) {
      if (String.IsNullOrWhiteSpace(requestedPath)) return null;
      Assets assets = m_currentAssets ?? m_previousAssets;
      if (assets == null) return null;
      String normalized = requestedPath.Trim().Replace('\\', '/');
      if (!normalized.StartsWith("/resources/", StringComparison.OrdinalIgnoreCase))
        normalized = "/resources/" + normalized.TrimStart('/');
      using TorArchive.File file = assets.FindFile(normalized);
      if (file == null) return null;
      using Stream stream = file.OpenCopyInMemory();
      using var reader = new StreamReader(stream, Encoding.UTF8, true, 8192, false);
      return reader.ReadToEnd();
    }

    private Bitmap LoadEffectTextureBitmap(String requestedPath) {
      if (String.IsNullOrWhiteSpace(requestedPath)) return null;
      Assets assets = m_currentAssets ?? m_previousAssets;
      if (assets == null) return null;

      String normalized = requestedPath.Replace('\\', '/');
      if (!normalized.StartsWith("/resources/", StringComparison.OrdinalIgnoreCase))
        normalized = "/resources/" + normalized.TrimStart('/');

      var candidates = new List<String> { normalized };
      if (!normalized.EndsWith(".dds", StringComparison.OrdinalIgnoreCase)) {
        String withoutExtension = Path.ChangeExtension(normalized, null);
        candidates.Add(withoutExtension + ".dds");
        candidates.Add(withoutExtension + ".tiny.dds");
      } else if (!normalized.EndsWith(".tiny.dds", StringComparison.OrdinalIgnoreCase)) {
        candidates.Add(normalized.Substring(0, normalized.Length - 4) + ".tiny.dds");
      }

      foreach (String candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase)) {
        using TorArchive.File file = assets.FindFile(candidate);
        if (file == null) continue;
        // DevIL uses process-global native state and is not guaranteed to be re-entrant. The material
        // inspector may request its sphere maps while the selected texture preview is decoding, so
        // serialize only the native decode section while keeping all callers off the UI thread.
        lock (m_previewTextureDecodeLock) {
          using Stream stream = file.OpenCopyInMemory();
          using ImageImporter importer = new ImageImporter();
          using DevIL.Image image = importer.LoadImageFromStream(ImageType.Dds, stream);
          using MemoryStream png = new MemoryStream();
          using ImageExporter exporter = new ImageExporter();
          exporter.SaveImageToStream(image, ImageType.Png, png);
          png.Position = 0;
          using Bitmap decoded = new Bitmap(png);
          return new Bitmap(decoded);
        }
      }
      return null;
    }

    private void NavigateToEffectResource(String requestedPath) {
      if (m_assetDict == null || treeViewFast1 == null || String.IsNullOrWhiteSpace(requestedPath)) return;
      String normalized = requestedPath.Trim().Replace('\\', '/');
      if (!normalized.StartsWith("/resources/", StringComparison.OrdinalIgnoreCase))
        normalized = "/resources/" + normalized.TrimStart('/');
      normalized = normalized.ToLowerInvariant();
      String id = "/root/named" + normalized;

      if (!m_assetDict.ContainsKey(id)) {
        StatusLabel2Text("Referenced asset is not present in the loaded build: " + normalized);
        return;
      }

      if (!String.IsNullOrEmpty(txtSearch.Text)) {
        txtSearch.Clear();
        ApplyAssetTreeLiveFilter();
      }
      try {
        TreeNode node = treeViewFast1.GetNode(id);
        if (node == null) {
          StatusLabel2Text("Referenced asset exists but its tree node is not available: " + normalized);
          return;
        }
        treeViewFast1.SelectedNode = node;
        node.EnsureVisible();
        treeViewFast1.Focus();
      }
      catch (Exception ex) {
        StatusLabel2Text("Could not navigate to referenced asset: " + ex.Message);
      }
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
      if (m_closing || asset?.HashInfo?.File == null) return;
      if (asset.HashInfo.File != null) {

        // A preview owns its playback source. Tear down SGT cleanly and tell the
        // existing WEM loop to stop before replacing the archive stream.
        if (m_sgtActive || m_sgtWaveReader != null) StopSgtPreview(true);
        else if (m_audioPlaying) {
          m_audioPlaying = false;
          try { m_waveOut?.Stop(); } catch { }
        }

        // Stop JBA UI state before switching to another asset.
        m_jbaActive = false;
        m_jbaUiTimer?.Stop();
        if (m_jbaSkeletonButton != null) {
          m_jbaSkeletonButton.Checked = false;
          m_jbaSkeletonButton.Visible = false;
        }
        if (m_jbaSpeedCombo != null) {
          m_jbaSpeedCombo.Visible = false;
          m_jbaSpeedCombo.SelectedItem = "1x";
        }
        if (m_sgtSaveButton != null) m_sgtSaveButton.Visible = false;
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
        m_ddsPreview.ClearPreview();
        m_ddsPreview.Visible = false;
        m_effectSpecPreview?.ClearPreview();
        if (m_effectSpecPreview != null) m_effectSpecPreview.Visible = false;
        m_materialSpecPreview?.ClearPreview();
        if (m_materialSpecPreview != null) m_materialSpecPreview.Visible = false;
        m_structuredTextPreview?.ClearPreview();
        if (m_structuredTextPreview != null) m_structuredTextPreview.Visible = false;
        m_audioBankPreview?.ClearPreview();
        if (m_audioBankPreview != null) m_audioBankPreview.Visible = false;
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
          Thread previousRender = m_render;
          try { m_panelRender?.StopRender(); } catch { }
          Boolean stopped = !previousRender.IsAlive;
          if (!stopped) {
            try { stopped = previousRender.Join(1000); } catch { }
          }
          if (!stopped) {
            // Never freeze the UI because the D3D driver is still returning from Present().
            // The renderer has already received StopRender(); leave its resources alone until
            // the render thread actually exits instead of Clear() racing DrawScene/Present.
            toolStripStatusLabel1.Text = "Previous 3D preview is still stopping.";
            loadingSwirl1.Visible = false;
            toolStripProgressBar1.Visible = false;
            return;
          }
          m_render = null;
          try { m_panelRender?.Clear(); } catch { }
        }

        await Task.Run(() => PreviewAssetLoadObject(asset.HashInfo.File));
        if (m_closing || IsDisposed || Disposing) {
          try { m_inputStream?.Dispose(); } catch { }
          m_inputStream = null;
          return;
        }

        // DynamicFileByteProvider byteProvider = new DynamicFileByteProvider(this.inputStream);
        // hexBox1.ByteProvider = byteProvider;
        // this.inputStream.Position = 0;

        m_rootList = new ArrayList();

        string selectedAssetPath = ((asset.HashInfo.Directory ?? String.Empty).TrimEnd('/', '\\') + "/" + asset.HashInfo.FileName).Replace("//", "/");
        String declaredExtension = (asset.HashInfo.Extension ?? String.Empty).Trim().TrimStart('.').ToUpperInvariant();
        String previewExtension = JedipediaFileTypeResolver.Resolve(m_inputStream, declaredExtension);
        Boolean previewTypeWasDetected = !String.Equals(previewExtension, declaredExtension, StringComparison.OrdinalIgnoreCase);
        bool heroScriptList = selectedAssetPath.Equals("/resources/systemgenerated/scriptdef.list", StringComparison.OrdinalIgnoreCase)
                           || selectedAssetPath.Equals("/resources/systemgenerated/scripts.list", StringComparison.OrdinalIgnoreCase);

        if (heroScriptList) {
          m_rootList.Clear();
          try {
            await Task.Run(PreviewAssetHeroScriptList);
            FinishStructuredTreePreview();
          }
          catch (Exception ex) {
            System.Diagnostics.Debug.WriteLine("HeroScript list structured preview failed: " + ex);
            toolStripStatusLabel2.Text = "HeroScript list parse failed: " + ex.Message;
            m_inputStream.Position = 0;
            await Task.Run(PreviewAssetHEX);
            txtRawView.Visible = true;
          }
        } else if (asset.HashInfo.Directory == "/resources/systemgenerated/compilednative") {
          m_rootList.Clear();
          try {
            await Task.Run(PreviewAssetSCPT);
            FinishStructuredTreePreview();
          }
          catch (Exception ex) {
            System.Diagnostics.Debug.WriteLine("SCPT structured preview failed: " + ex);
            toolStripStatusLabel2.Text = "SCPT parse failed: " + ex.Message;
            m_inputStream.Position = 0;
            await Task.Run(PreviewAssetHEX);
            txtRawView.Visible = true;
          }
        } else {
          switch (previewExtension) {
            case "DDS":
              await Task.Run(PreviewAssetDDS);
              ConfigureDdsPreviewBackground(asset.HashInfo.Directory);
              splitContainer3.Panel1.AutoScrollPosition = Point.Empty;
              m_ddsPreview.Visible = true;
              m_ddsPreview.BringToFront();
              break;

            case "PNG":
              await Task.Run(PreviewAssetPNG);
              pictureBox1.Visible = true;
              break;

            case "MANIFEST": {
              String manifestText = await Task.Run(ReadCurrentPreviewTextSmart);
              m_structuredTextPreview.LoadManifest(selectedAssetPath, manifestText);
              m_structuredTextPreview.Visible = true;
              m_structuredTextPreview.BringToFront();
              LoadingSwirl1Hide();
              ProgressBar1Hide();
              return;
            }

            case "TBL": {
              String tableText = await Task.Run(ReadCurrentPreviewTextSmart);
              m_structuredTextPreview.LoadTbl(selectedAssetPath, tableText);
              m_structuredTextPreview.Visible = true;
              m_structuredTextPreview.BringToFront();
              LoadingSwirl1Hide();
              ProgressBar1Hide();
              return;
            }

            case "RUL": {
              String ruleText = await Task.Run(ReadCurrentPreviewTextSmart);
              m_structuredTextPreview.LoadRul(selectedAssetPath, ruleText);
              m_structuredTextPreview.Visible = true;
              m_structuredTextPreview.BringToFront();
              LoadingSwirl1Hide();
              ProgressBar1Hide();
              return;
            }

            case "AAM": {
              String aamText = await Task.Run(ReadCurrentPreviewTextSmart);
              m_rootList.Clear();
              try {
                await Task.Run(() => m_rootList = ViewAAM.Parse(aamText, selectedAssetPath));
                FinishStructuredTreePreview();
              }
              catch (Exception ex) {
                System.Diagnostics.Debug.WriteLine("AAM structured preview failed: " + ex);
                toolStripStatusLabel2.Text = "AAM parse failed: " + ex.Message;
                await Task.Run(PreviewAssetXML);
                if (m_xmlDoc?.DocumentElement != null) webBrowser1.Visible = true;
                else txtRawView.Visible = true;
              }
              break;
            }

            case "LST": {
              Boolean guiXmlList = selectedAssetPath.Equals("/resources/guixml/_heguixml.lst", StringComparison.OrdinalIgnoreCase)
                                 || selectedAssetPath.Equals("/resources/guixml/guixml.lst", StringComparison.OrdinalIgnoreCase);
              if (guiXmlList) {
                String listText = await Task.Run(ReadCurrentPreviewTextSmart);
                m_structuredTextPreview.LoadLst(selectedAssetPath, listText);
                m_structuredTextPreview.Visible = true;
                m_structuredTextPreview.BringToFront();
                LoadingSwirl1Hide();
                ProgressBar1Hide();
                toolStripStatusLabel1.Text = "LST GUI XML list";
                toolStripStatusLabel2.Text = "Double-click an entry to open the referenced GUI XML asset.";
                return;
              }

              await Task.Run(PreviewAssetXML);
              if (m_xmlDoc?.DocumentElement != null) webBrowser1.Visible = true;
              else txtRawView.Visible = true;
              break;
            }

            case "TXT": {
              if (selectedAssetPath.Equals("/resources/version.txt", StringComparison.OrdinalIgnoreCase)) {
                String versionText = await Task.Run(ReadCurrentPreviewTextSmart);
                m_structuredTextPreview.LoadVersionTxt(selectedAssetPath, versionText);
                m_structuredTextPreview.Visible = true;
                m_structuredTextPreview.BringToFront();
                LoadingSwirl1Hide();
                ProgressBar1Hide();
                toolStripStatusLabel1.Text = "Client version metadata";
                toolStripStatusLabel2.Text = "Parsed key/value metadata from /resources/version.txt.";
                return;
              }

              await Task.Run(PreviewAssetXML);
              if (m_xmlDoc?.DocumentElement != null) webBrowser1.Visible = true;
              else txtRawView.Visible = true;
              break;
            }

            case "INI": {
              String iniText = await Task.Run(ReadCurrentPreviewTextSmart);
              m_structuredTextPreview.LoadIni(selectedAssetPath, iniText);
              m_structuredTextPreview.Visible = true;
              m_structuredTextPreview.BringToFront();
              LoadingSwirl1Hide();
              ProgressBar1Hide();
              toolStripStatusLabel1.Text = "INI keybinding inspector";
              toolStripStatusLabel2.Text = "Sections, commands and device/control bindings parsed from the client file.";
              return;
            }

            case "LOD": {
              String lodText = await Task.Run(ReadCurrentPreviewTextSmart);
              m_structuredTextPreview.LoadLod(selectedAssetPath, lodText);
              m_structuredTextPreview.Visible = true;
              m_structuredTextPreview.BringToFront();
              LoadingSwirl1Hide();
              ProgressBar1Hide();
              toolStripStatusLabel1.Text = "LOD schema inspector";
              toolStripStatusLabel2.Text = "Schema thresholds used by model LOD selection.";
              return;
            }

            case "XML":
            case "SVY":
            case "TAB":
            case "ABL":
            case "CAM":
            case "CDX":
            case "CNV":
            case "COS":
            case "DYN":
            case "ENC":
            case "HYD":
            case "ITM":
            case "IPP":
            case "MPN":
            case "NAM":
            case "NPC":
            case "NPP":
            case "PCS":
            case "PLC":
            case "PTH":
            case "QST":
            case "RDD":
            case "SPN_C":
            case "SPN_CRF":
            case "SPN_LST":
            case "SPN_P":
            case "STG":
            case "STR":
              await Task.Run(PreviewAssetXML);
              if (m_xmlDoc?.DocumentElement != null) webBrowser1.Visible = true;
              else txtRawView.Visible = true;
              break;

            case "BIN":
            case "BKT":
            case "FBX":
            case "GOM":
            case "NODE":
            case "INFO":
            case "LIST":
              m_rootList.Clear();
              try {
                String jedipediaExtension = previewExtension;
                String jedipediaFileName = asset.HashInfo.FileName;
                await Task.Run(() => PreviewAssetJedipediaStructured(jedipediaExtension, jedipediaFileName));
                FinishStructuredTreePreview();
              }
              catch (Exception ex) {
                System.Diagnostics.Debug.WriteLine(previewExtension + " structured preview failed: " + ex);
                toolStripStatusLabel2.Text = previewExtension + " parse failed: " + ex.Message;
                m_inputStream.Position = 0;
                await Task.Run(PreviewAssetHEX);
                txtRawView.Visible = true;
              }
              break;

            case "MAT": {
              String materialText = await Task.Run(ReadCurrentPreviewTextSmart);
              m_materialSpecPreview.LoadMat(selectedAssetPath, materialText);
              m_materialSpecPreview.Visible = true;
              m_materialSpecPreview.BringToFront();
              LoadingSwirl1Hide();
              ProgressBar1Hide();
              toolStripStatusLabel1.Text = "MAT material inspector";
              toolStripStatusLabel2.Text = "Select a texture input for interactive DDS preview; double-click references to open assets.";
              break;
            }

            case "TEX": {
              String textureObjectText = await Task.Run(ReadCurrentPreviewTextSmart);
              m_materialSpecPreview.LoadTex(selectedAssetPath, textureObjectText);
              m_materialSpecPreview.Visible = true;
              m_materialSpecPreview.BringToFront();
              LoadingSwirl1Hide();
              ProgressBar1Hide();
              toolStripStatusLabel1.Text = "TEX texture-object inspector";
              toolStripStatusLabel2.Text = "Sampler/address/compression parameters plus linked DDS/tiny DDS.";
              break;
            }

            case "EMT": {
              String environmentMaterialText = await Task.Run(ReadCurrentPreviewTextSmart);
              m_materialSpecPreview.LoadEmt(selectedAssetPath, environmentMaterialText);
              m_materialSpecPreview.Visible = true;
              m_materialSpecPreview.BringToFront();
              LoadingSwirl1Hide();
              ProgressBar1Hide();
              toolStripStatusLabel1.Text = "EMT environment-material inspector";
              toolStripStatusLabel2.Text = "BlendDiffuse / BlendNormal references are resolved directly from the loaded build.";
              break;
            }

            case "NOT": {
              String mapNotesText = await Task.Run(ReadCurrentPreviewTextSmart);
              m_structuredTextPreview.LoadMapNotes(selectedAssetPath, mapNotesText);
              m_structuredTextPreview.Visible = true;
              m_structuredTextPreview.BringToFront();
              LoadingSwirl1Hide();
              ProgressBar1Hide();
              toolStripStatusLabel1.Text = "Map notes";
              toolStripStatusLabel2.Text = "Structured map-note metadata; double-click a row to open a related map DDS when available.";
              return;
            }

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

            case "AMX":
              m_rootList.Clear();
              try {
                String amxSourcePath = asset.HashInfo.Directory + "/" + asset.HashInfo.FileName;
                await Task.Run(() => PreviewAssetAMX(amxSourcePath));
                FinishStructuredTreePreview();
              }
              catch (Exception ex) {
                System.Diagnostics.Debug.WriteLine("AMX structured preview failed: " + ex);
                toolStripStatusLabel2.Text = "AMX parse failed: " + ex.Message;
                m_inputStream.Position = 0;
                await Task.Run(PreviewAssetHEX);
                txtRawView.Visible = true;
              }
              break;

            case "MPH":
              m_rootList.Clear();
              try {
                String mphSourcePath = asset.HashInfo.Directory + "/" + asset.HashInfo.FileName;
                await Task.Run(() => PreviewAssetMPH(mphSourcePath));
                FinishStructuredTreePreview();
              }
              catch (Exception ex) {
                System.Diagnostics.Debug.WriteLine("MPH structured preview failed: " + ex);
                toolStripStatusLabel2.Text = "MPH parse failed: " + ex.Message;
                m_inputStream.Position = 0;
                await Task.Run(PreviewAssetHEX);
                txtRawView.Visible = true;
              }
              break;

            case "CLO":
              m_rootList.Clear();
              try {
                await Task.Run(PreviewAssetCLO);
                FinishStructuredTreePreview();
              }
              catch (Exception ex) {
                System.Diagnostics.Debug.WriteLine("CLO structured preview failed: " + ex);
                toolStripStatusLabel2.Text = "CLO parse failed: " + ex.Message;
                m_inputStream.Position = 0;
                await Task.Run(PreviewAssetHEX);
                txtRawView.Visible = true;
              }
              break;

            case "SPT":
              m_rootList.Clear();
              try {
                String sptSourcePath = asset.HashInfo.Directory + "/" + asset.HashInfo.FileName;
                await Task.Run(() => PreviewAssetSPT(sptSourcePath));
                FinishStructuredTreePreview();
              }
              catch (Exception ex) {
                System.Diagnostics.Debug.WriteLine("SPT structured preview failed: " + ex);
                toolStripStatusLabel2.Text = "SPT parse failed: " + ex.Message;
                m_inputStream.Position = 0;
                await Task.Run(PreviewAssetHEX);
                txtRawView.Visible = true;
              }
              break;

            case "COLLISION":
              m_rootList.Clear();
              try {
                await Task.Run(PreviewAssetCollision);
                FinishStructuredTreePreview();
              }
              catch (Exception ex) {
                System.Diagnostics.Debug.WriteLine("Collision structured preview failed: " + ex);
                toolStripStatusLabel2.Text = "Collision parse failed: " + ex.Message;
                m_inputStream.Position = 0;
                await Task.Run(PreviewAssetHEX);
                txtRawView.Visible = true;
              }
              break;

            case "SIG":
              m_rootList.Clear();
              try {
                await Task.Run(PreviewAssetSIG);
                FinishStructuredTreePreview();
              }
              catch (Exception ex) {
                System.Diagnostics.Debug.WriteLine("SIG structured preview failed: " + ex);
                toolStripStatusLabel2.Text = "SIG parse failed: " + ex.Message;
                m_inputStream.Position = 0;
                await Task.Run(PreviewAssetHEX);
                txtRawView.Visible = true;
              }
              break;

            case "FXE":
              m_rootList.Clear();
              try {
                await Task.Run(PreviewAssetFXE);
                FinishStructuredTreePreview();
              }
              catch (Exception ex) {
                System.Diagnostics.Debug.WriteLine("FXE structured preview failed: " + ex);
                // FXE and FXA use the same FACE archive magic, and a few beta manifests label actor archives as FXE.
                // Jedipedia identifies them by structure. Try that structure before dropping to a raw hex dump.
                try {
                  m_inputStream.Position = 0;
                  await Task.Run(PreviewAssetFXA);
                  toolStripStatusLabel2.Text = "FACE actor data (FXA layout; archive entry is typed FXE)";
                  FinishStructuredTreePreview();
                }
                catch (Exception actorEx) {
                  System.Diagnostics.Debug.WriteLine("FXE-as-FXA structured preview failed: " + actorEx);
                  toolStripStatusLabel2.Text = "FXE parse failed: " + ex.Message;
                  m_inputStream.Position = 0;
                  await Task.Run(PreviewAssetHEX);
                  txtRawView.Visible = true;
                }
              }
              break;

            case "FXA":
              m_rootList.Clear();
              try {
                await Task.Run(PreviewAssetFXA);
                FinishStructuredTreePreview();
              }
              catch (Exception ex) {
                System.Diagnostics.Debug.WriteLine("FXA structured preview failed: " + ex);
                toolStripStatusLabel2.Text = "FXA parse failed: " + ex.Message;
                m_inputStream.Position = 0;
                await Task.Run(PreviewAssetHEX);
                txtRawView.Visible = true;
              }
              break;

            case "DYC":
              m_rootList.Clear();
              try {
                await Task.Run(PreviewAssetDYC);
                FinishStructuredTreePreview();
              }
              catch (Exception ex) {
                System.Diagnostics.Debug.WriteLine("DYC structured preview failed: " + ex);
                toolStripStatusLabel2.Text = "DYC parse failed: " + ex.Message;
                m_inputStream.Position = 0;
                await Task.Run(PreviewAssetRAW);
                txtRawView.Visible = true;
              }
              break;

            case "MAG":
              m_rootList.Clear();
              try {
                await Task.Run(PreviewAssetMAG);
                FinishStructuredTreePreview();
              }
              catch (Exception ex) {
                System.Diagnostics.Debug.WriteLine("MAG structured preview failed: " + ex);
                toolStripStatusLabel2.Text = "MAG parse failed: " + ex.Message;
                m_inputStream.Position = 0;
                await Task.Run(PreviewAssetRAW);
                txtRawView.Visible = true;
              }
              break;

            case "PRT": {
              String effectText = await Task.Run(ReadCurrentPreviewText);
              m_effectSpecPreview.LoadPrt(selectedAssetPath, effectText);
              m_effectSpecPreview.Visible = true;
              m_effectSpecPreview.BringToFront();
              LoadingSwirl1Hide();
              ProgressBar1Hide();
              toolStripStatusLabel1.Text = "PRT particle inspector";
              toolStripStatusLabel2.Text = "Graph: wheel zoom, drag to pan, double-click resource to open; Simulation uses the World PRT runtime.";
              break;
            }

            case "FXSPEC":
            case "EPP": {
              String effectText = await Task.Run(ReadCurrentPreviewText);
              m_effectSpecPreview.LoadXmlEffect(previewExtension, selectedAssetPath, effectText);
              m_effectSpecPreview.Visible = true;
              m_effectSpecPreview.BringToFront();
              LoadingSwirl1Hide();
              ProgressBar1Hide();
              toolStripStatusLabel1.Text = previewExtension + " effect inspector";
              toolStripStatusLabel2.Text = "Graph: wheel zoom, drag to pan, double-click resource to open; missing references are marked.";
              break;
            }

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

            case "BNK": {
              FileFormat_BNK bank = await Task.Run(ParseCurrentBnk);
              m_audioBankPreview.LoadBank(selectedAssetPath, bank);
              m_audioBankPreview.Visible = true;
              m_audioBankPreview.BringToFront();
              loadingSwirl1.Visible = false;
              toolStripProgressBar1.Visible = false;
              toolStripStatusLabel1.Text = "BNK Wwise semantic graph";
              toolStripStatusLabel2.Text = "Event → Action → HIRC object → embedded/streamed WEM. Double-click media to play/open.";
              break;
            }

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

            case "SGT":
              m_rootList.Clear();
              try {
                await Task.Run(PreviewAssetSGT);
                FinishStructuredTreePreview();
                if (m_sgtDecodedWave != null && m_sgtDecodedWave.Length > 44)
                  StartSgtPlayback(m_sgtDecodedWave);
                else if (!String.IsNullOrWhiteSpace(m_sgtDecodeError))
                  toolStripStatusLabel2.Text = "SGT playback unavailable: " + m_sgtDecodeError;
              }
              catch (Exception ex) {
                System.Diagnostics.Debug.WriteLine("SGT structured preview failed: " + ex);
                toolStripStatusLabel2.Text = "SGT parse failed: " + ex.Message;
                m_inputStream.Position = 0;
                await Task.Run(PreviewAssetHEX);
                txtRawView.Visible = true;
              }
              break;

            case "WAV":
            case "WEM":
            case "OGG":
              toolStrip1.Visible = true;
              m_audioPlaying = false;
              // Beta streamed audio is named *.ogg but contains RIFF/Wwise data. Pass the
              // archive path so ViewWEM can select the beta codebook family.
              await PreviewAssetWEM(asset.HashInfo.Directory + "/" + asset.HashInfo.FileName);
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
              m_rootList.Clear();
              try {
                await Task.Run(PreviewAssetSCPT);
                FinishStructuredTreePreview();
              }
              catch (Exception ex) {
                System.Diagnostics.Debug.WriteLine("SCPT structured preview failed: " + ex);
                toolStripStatusLabel2.Text = "SCPT parse failed: " + ex.Message;
                m_inputStream.Position = 0;
                await Task.Run(PreviewAssetHEX);
                txtRawView.Visible = true;
              }
              break;

            case "FX":
              await Task.Run(PreviewAssetFX);
              txtRawView.Visible = true;
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
              previewExtension,
              "JBA",
              StringComparison.OrdinalIgnoreCase)) {
          toolStripStatusLabel1.Text = "File Loaded.";
          if (String.Equals(previewExtension, "DDS", StringComparison.OrdinalIgnoreCase)) {
            toolStripStatusLabel2.Text = m_ddsPreview.IsCubeMap
              ? "Cubemap: drag to look around | mouse wheel zoom | double-click reset"
              : "DDS: mouse wheel zoom | drag to pan | double-click reset";
          } else if (previewTypeWasDetected) {
            String listed = String.IsNullOrWhiteSpace(declaredExtension) ? "unknown" : declaredExtension;
            toolStripStatusLabel2.Text = "Content type detected as " + previewExtension + " (listed as " + listed + ").";
          } else if (String.Equals(previewExtension, "AAM", StringComparison.OrdinalIgnoreCase)) {
            toolStripStatusLabel2.Text = "Animation Actor Model: inputs, MPH network selection, actions, events, tracks, switches and masks.";
          } else {
            toolStripStatusLabel2.Text = String.Empty;
          }
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

    private FileFormat_BNK ParseCurrentBnk() {
      if (m_inputStream == null) return null;
      m_inputStream.Position = 0;
      using BinaryReader br = new BinaryReader(m_inputStream, Encoding.UTF8, true);
      return new FileFormat_BNK(br, true);
    }

    private async void PlayAudioBankEmbedded(ViewWEM wem) {
      if (wem == null) return;
      if (m_audioPlaying) {
        m_audioPlaying = false;
        try { m_waveOut?.Stop(); } catch { }
      }
      toolStrip1.Visible = true;
      await PreviewAssetWEM(wem);
    }

    private void PreviewAssetDAT(String directory, String fileName, Assets assets) {
      if (m_inputStream == null) return;

      m_inputStream.Position = 0;
      using BinaryReader br = new BinaryReader(m_inputStream, Encoding.UTF8, true);
      m_rootList = View_DAT.Parse(br, assets, directory, fileName);
    }

    private void PreviewAssetJedipediaStructured(String extension, String fileName) {
      if (m_inputStream == null) return;
      m_inputStream.Position = 0;
      m_rootList = View_JedipediaFormats.Parse(m_inputStream, extension, fileName);
    }

    private void PreviewAssetDDS() {
      try {
        if (m_inputStream == null) return;
        m_inputStream.Position = 0;

        using ImageImporter imp = new ImageImporter();
        using DevIL.Image dds = imp.LoadImageFromStream(ImageType.Dds, m_inputStream);

        if (dds.IsCubeMap || dds.FaceCount > 1) {
          Dictionary<CubeMapFace, ImageData> faces = new Dictionary<CubeMapFace, ImageData>();
          CubeMapFace[] cubeFaces = {
            CubeMapFace.PositiveX, CubeMapFace.NegativeX,
            CubeMapFace.PositiveY, CubeMapFace.NegativeY,
            CubeMapFace.PositiveZ, CubeMapFace.NegativeZ
          };

          foreach (CubeMapFace face in cubeFaces) {
            ImageData imageData = dds.GetImageData(face, 0);
            if (imageData != null) faces[face] = imageData;
          }

          if (faces.Count > 0) {
            DdsPreviewControl.CubeMapPixels cube = DdsPreviewControl.PrepareCubeMap(faces);
            m_ddsPreview.Invoke(new Action(() => m_ddsPreview.SetCubeMap(cube)));
            return;
          }
        }

        using MemoryStream stream = new MemoryStream();
        using ImageExporter exp = new ImageExporter();
        exp.SaveImageToStream(dds, ImageType.Png, stream);
        stream.Position = 0;
        using Bitmap decoded = new Bitmap(stream);
        Bitmap bmp = new Bitmap(decoded);
        m_ddsPreview.Invoke(new Action(() => m_ddsPreview.SetBitmap(bmp)));
      }
      catch (Exception ex) {
        Debug.WriteLine("DDS preview failed: " + ex);
      }
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
          if (m_jbaSpeedCombo != null) {
            m_jbaSpeedCombo.Visible = true;
            m_jbaSpeedCombo.SelectedItem = "1x";
          }
          m_panelRender.SetAnimationSpeed(1.0F);
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

    private void PreviewAssetHeroScriptList() {
      if (m_inputStream == null) return;
      m_inputStream.Position = 0;
      using BinaryReader br = new BinaryReader(m_inputStream, Encoding.UTF8, true);
      ViewHeroScriptLists.HeroScriptListInfo list = ViewHeroScriptLists.Parse(br);
      m_rootList = ViewHeroScriptLists.BuildTree(list);
    }

    private void PreviewAssetSCPT() {
      if (m_inputStream == null) return;
      m_inputStream.Position = 0;
      using BinaryReader br = new BinaryReader(m_inputStream, Encoding.UTF8, true);
      ViewSCPT.ScptFileInfo scpt = ViewSCPT.Parse(br);
      m_rootList = ViewSCPT.BuildTree(scpt);
    }

    private void PreviewAssetAMX(String sourcePath) {
      if (m_inputStream == null) return;
      m_inputStream.Position = 0;
      using BinaryReader br = new BinaryReader(m_inputStream, Encoding.UTF8, true);
      ViewAMX.AmxFileInfo amx = ViewAMX.Parse(br);
      m_rootList = ViewAMX.BuildTree(amx, sourcePath);
    }

    private void PreviewAssetMPH(String sourcePath) {
      if (m_inputStream == null) return;
      m_inputStream.Position = 0;
      using BinaryReader br = new BinaryReader(m_inputStream, Encoding.UTF8, true);
      ViewMPH.MphFileInfo mph = ViewMPH.Parse(br);
      m_rootList = ViewMPH.BuildTree(mph, sourcePath);
    }

    private void PreviewAssetCLO() {
      if (m_inputStream == null) return;
      m_inputStream.Position = 0;
      ViewCLO.CloInfo clo = ViewCLO.Parse(m_inputStream);
      m_rootList = ViewCLO.BuildTree(clo);
    }

    private void PreviewAssetSPT(String sourcePath) {
      if (m_inputStream == null) return;
      m_inputStream.Position = 0;
      ViewSPT.SptInfo spt = ViewSPT.Parse(m_inputStream);
      m_rootList = ViewSPT.BuildTree(spt, sourcePath);
    }

    private void PreviewAssetCollision() {
      if (m_inputStream == null) return;
      m_inputStream.Position = 0;
      ViewCollision.CollisionInfo collision = ViewCollision.ParseStandalone(m_inputStream);
      m_rootList = ViewCollision.BuildTree(collision);
    }

    private void PreviewAssetSIG() {
      if (m_inputStream == null) return;
      m_inputStream.Position = 0;
      ViewSIG.SigInfo sig = ViewSIG.Parse(m_inputStream);
      m_rootList = ViewSIG.BuildTree(sig);
    }

    private void PreviewAssetFXE() {
      if (m_inputStream == null) return;
      m_inputStream.Position = 0;
      ViewFXE.FxeInfo fxe = ViewFXE.Parse(m_inputStream);
      m_rootList = ViewFXE.BuildTree(fxe);
    }

    private void PreviewAssetFXA() {
      if (m_inputStream == null) return;
      m_inputStream.Position = 0;
      ViewFXA.FxaInfo fxa = ViewFXA.Parse(m_inputStream);
      m_rootList = ViewFXA.BuildTree(fxa);
    }

    private void PreviewAssetDYC() {
      if (m_inputStream == null) return;
      m_inputStream.Position = 0;
      ViewTextSpecs.TextSpecInfo dyc = ViewTextSpecs.ParseDyc(m_inputStream);
      m_rootList = ViewTextSpecs.BuildTree(dyc);
    }

    private void PreviewAssetMAG() {
      if (m_inputStream == null) return;
      m_inputStream.Position = 0;
      ViewTextSpecs.TextSpecInfo mag = ViewTextSpecs.ParseMag(m_inputStream);
      m_rootList = ViewTextSpecs.BuildTree(mag);
    }

    private void PreviewAssetSGT() {
      if (m_inputStream == null) return;
      m_inputStream.Position = 0;
      ViewSGT.SgtInfo sgt = ViewSGT.Parse(m_inputStream);
      m_rootList = ViewSGT.BuildTree(sgt);
      m_sgtDecodedWave = null;
      m_sgtDecodeError = null;
      try { m_sgtDecodedWave = ViewSGT.DecodeToPcmWave(m_inputStream, sgt); }
      catch (Exception ex) { m_sgtDecodeError = ex.Message; }
    }

    private void StartSgtPlayback(Byte[] pcmWave) {
      if (pcmWave == null || pcmWave.Length <= 44 || m_closing) return;

      // Do not reuse m_waveOut: an older WEM conversion/playback task can still be winding down
      // for a few milliseconds after an asset change. A dedicated output keeps SGT from re-arming
      // that task's m_audioPlaying flag or disposing the device under its loop.
      m_audioPlaying = false;
      try { m_waveOut?.Stop(); } catch { }
      try { m_sgtWaveOut?.Stop(); m_sgtWaveOut?.Dispose(); } catch { }
      try { m_sgtWaveReader?.Dispose(); } catch { }
      try { m_sgtPcmStream?.Dispose(); } catch { }
      m_sgtWaveOut = null;
      m_sgtWaveReader = null;
      m_sgtPcmStream = null;

      m_sgtPcmStream = new MemoryStream(pcmWave, false);
      m_sgtWaveReader = new WaveFileReader(m_sgtPcmStream);
      m_sgtWaveOut = new WaveOutEvent { Volume = 1.0F };
      m_sgtWaveOut.Init(m_sgtWaveReader);
      m_sgtActive = true;

      toolStrip1Button1.Enabled = true;
      toolStrip1Button2.Enabled = true;
      toolStrip1Button3.Enabled = true;
      toolStrip1Button1.Text = ";";
      toolStrip1Button1.ToolTipText = "Pause";
      toolStrip1Button3.Checked = false;
      toolStrip1Button3.ToolTipText = "Mute";
      toolStrip1ProgressBar1.Enabled = true;
      if (m_sgtSaveButton != null) m_sgtSaveButton.Visible = true;
      toolStrip1.Visible = true;

      UpdateSgtToolbar();
      m_sgtWaveOut.Play();
      m_sgtUiTimer?.Start();
    }

    private void StopSgtPreview(Boolean disposeSource) {
      if (!m_sgtActive && m_sgtWaveReader == null && m_sgtPcmStream == null) {
        if (disposeSource) { m_sgtDecodedWave = null; m_sgtDecodeError = null; }
        return;
      }

      try { m_sgtWaveOut?.Stop(); } catch { }
      if (disposeSource) {
        try { m_sgtUiTimer?.Stop(); } catch { }
        try { m_sgtWaveOut?.Dispose(); } catch { }
        m_sgtWaveOut = null;
        try { m_sgtWaveReader?.Dispose(); } catch { }
        try { m_sgtPcmStream?.Dispose(); } catch { }
        m_sgtWaveReader = null;
        m_sgtPcmStream = null;
        m_sgtDecodedWave = null;
        m_sgtDecodeError = null;
        m_sgtActive = false;
        if (m_sgtSaveButton != null) m_sgtSaveButton.Visible = false;
      }
    }

    private void SgtSaveButtonClick(Object sender, EventArgs e) {
      if (m_sgtDecodedWave == null || m_sgtDecodedWave.Length <= 44) return;
      String suggested = "audio.wav";
      if (treeViewFast1?.SelectedNode?.Tag is TreeListItem item && !String.IsNullOrWhiteSpace(item.HashInfo.FileName))
        suggested = Path.GetFileNameWithoutExtension(item.HashInfo.FileName) + ".wav";
      using SaveFileDialog dialog = new SaveFileDialog {
        Filter = "Wave audio (*.wav)|*.wav|All files (*.*)|*.*",
        FileName = suggested,
        AddExtension = true,
        DefaultExt = "wav",
        OverwritePrompt = true
      };
      if (dialog.ShowDialog(this) != DialogResult.OK) return;
      try {
        System.IO.File.WriteAllBytes(dialog.FileName, m_sgtDecodedWave);
        StatusLabel1Text("Saved decoded SGT audio: " + dialog.FileName);
      }
      catch (Exception ex) {
        MessageBox.Show(this, ex.Message, "Unable to save WAV", MessageBoxButtons.OK, MessageBoxIcon.Error);
      }
    }

    private void SgtUiTimerTick(Object sender, EventArgs e) {
      if (!m_sgtActive || m_sgtWaveReader == null || m_sgtWaveOut == null) {
        m_sgtUiTimer?.Stop();
        return;
      }
      UpdateSgtToolbar();
    }

    private void UpdateSgtToolbar() {
      if (!m_sgtActive || m_sgtWaveReader == null) return;
      TimeSpan current = m_sgtWaveReader.CurrentTime;
      TimeSpan total = m_sgtWaveReader.TotalTime;
      toolStrip1Label1.Text = String.Format(
        CultureInfo.InvariantCulture,
        "{0:D2}:{1:D2}/{2:D2}:{3:D2}",
        current.Minutes, current.Seconds, total.Minutes, total.Seconds
      );
      Int32 maximum = (Int32)Math.Max(1, Math.Min(Int32.MaxValue, total.TotalMilliseconds));
      Int32 value = (Int32)Math.Max(0, Math.Min(maximum, current.TotalMilliseconds));
      toolStrip1ProgressBar1.Maximum = maximum;
      toolStrip1ProgressBar1.Value = value;
      if (m_sgtWaveOut != null && m_sgtWaveOut.PlaybackState == PlaybackState.Playing) {
        toolStrip1Button1.Text = ";";
        toolStrip1Button1.ToolTipText = "Pause";
      } else {
        toolStrip1Button1.Text = "4";
        toolStrip1Button1.ToolTipText = "Play";
      }
    }

    private void FinishStructuredTreePreview() {
      NodeListItem.ResetTreeListViewColumns(treeViewGrid1);
      treeViewGrid1.Roots = m_rootList;
      foreach (NodeListItem root in m_rootList.Cast<NodeListItem>()) treeViewGrid1.Expand(root);
      treeViewGrid1.AutoResizeColumns(ColumnHeaderAutoResizeStyle.ColumnContent);
      loadingSwirl1.Visible = false;
      toolStripProgressBar1.Visible = false;
      treeViewGrid1.Visible = true;
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
              // A newly selected SGT owns these same controls with a separate WaveOutEvent.
              // Do not let the tail of an older WEM task disable its toolbar.
              if (m_sgtActive) return;
              toolStrip1Button1.Enabled = false;
              toolStrip1Button2.Enabled = false;
              toolStrip1Button3.Enabled = false;

              toolStrip1Label1.Text = "00:00/00:00";

              toolStrip1ProgressBar1.Enabled = false;
              toolStrip1ProgressBar1.Value = 0;
            }));

          } else {
            StatusLabel1Text(String.IsNullOrWhiteSpace(wem?.LastError)
              ? "Audio Processing Failed."
              : "Audio Processing Failed: " + wem.LastError);
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

    private void PreviewAssetFX() {
      if (InvokeRequired) { Invoke(PreviewAssetFX); return; }
      if (m_inputStream == null) return;
      m_inputStream.Position = 0;
      Byte[] data;
      using (MemoryStream copy = new MemoryStream()) { m_inputStream.CopyTo(copy); data = copy.ToArray(); }
      Encoding encoding = data.Length >= 4 && data[3] == 0 ? Encoding.Unicode : Encoding.UTF8;
      Int32 offset = 0;
      if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF) { encoding = Encoding.UTF8; offset = 3; }
      else if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE) { encoding = Encoding.Unicode; offset = 2; }
      else if (data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF) { encoding = Encoding.BigEndianUnicode; offset = 2; }
      String text = encoding.GetString(data, offset, data.Length - offset).Replace("\0", String.Empty);
      txtRawView.ReadOnly = false;
      txtRawView.Text = text;
      txtRawView.ReadOnly = true;
      toolStripStatusLabel2.Text = "Legacy FX source (" + encoding.WebName + ")";
    }

    private void PreviewAssetXML() {
      if (InvokeRequired) Invoke(PreviewAssetXML);
      else {
        String output = ReadCurrentPreviewTextSmart();

        txtRawView.ReadOnly = false;
        txtRawView.Text = output;
        txtRawView.ReadOnly = true;

        m_xmlDoc = null;
        String candidate = output.TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
        if (candidate.StartsWith("<", StringComparison.Ordinal)) {
          try {
            XmlDocument doc = new XmlDocument();
            doc.LoadXml(candidate);
            if (doc.DocumentElement != null) {
              m_xmlDoc = doc;
              webBrowser1.DocumentText =
                new CodeColorizer().Colorize(PreviewAssetXMLBeautify(doc), Languages.Xml);
            }
          }
          catch (XmlException) {
            // A large part of the beta-only extension set is plain text rather
            // than XML. Keep the exact text visible instead of showing an empty
            // browser page when an old file only looks XML-like.
          }
        }

        if (m_xmlDoc == null) webBrowser1.DocumentText = String.Empty;
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
      // D3DPanelApp normally subscribes the renderer directly.  Keep this
      // WinForms handler functional as a fallback instead of throwing if the
      // designer/event wiring invokes it.
      m_panelRender?.ZoomByWheelDelta(e.Delta);
    }

    private void RenderPanelResize(Object sender, EventArgs e) {
      if (m_panelRender != null)
        if (renderPanel.Width != m_panelRender.ClientWidth
            || renderPanel.Height != m_panelRender.ClientHeight)
          m_panelRender.SetSize(renderPanel.Height, renderPanel.Width);
    }

    #endregion

    private TreeViewFast.Controls.TreeViewFast ActiveAssetTree =>
      m_assetFilterTree != null && m_assetFilterTree.Visible ? m_assetFilterTree : treeViewFast1;

    private void InitializeAssetFilterTreeView() {
      // Keep the enormous native WinForms TreeView intact while a filter is active. Clearing the
      // full control destroys every native tree item and was the remaining source of multi-second
      // UI freezes even after the filename scan itself moved to Task.Run.
      m_assetFilterTree = new TreeViewFast.Controls.TreeViewFast {
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
      m_assetFilterTree.AfterSelect += TreeViewFast1AfterSelect;
      m_assetFilterTree.KeyDown += TreeViewFast1KeyDown;
      m_assetFilterTree.MouseHover += TreeViewFast1MouseHover;
      m_assetFilterTree.MouseUp += TreeViewFast1MouseUp;
      splitContainer2.Panel1.Controls.Add(m_assetFilterTree);
      m_assetFilterTree.BringToFront();
    }

    private void InitializeAssetTreeLiveFilter() {
      m_assetTreeFilterTimer = new System.Windows.Forms.Timer { Interval = 180 };
      m_assetTreeFilterTimer.Tick += (_, __) => {
        m_assetTreeFilterTimer.Stop();
        ApplyAssetTreeLiveFilter();
      };
      txtSearch.TextChanged += (_, __) => {
        if (m_closing) return;
        Boolean hasFilter = txtSearch.Enabled && !String.IsNullOrWhiteSpace(txtSearch.Text);
        btnClearSearch.Enabled = hasFilter;
        m_assetTreeFilterTimer.Stop();
        if (hasFilter) m_assetTreeFilterTimer.Start();
        else ApplyAssetTreeLiveFilter();
      };

      // Search is now a live hierarchy-preserving filter rather than a jump/find-next workflow.
      btnSearch.Visible = false;
      btnFindNext.Visible = false;
      btnClearSearch.Text = "Clear filter";
      btnClearSearch.Location = new System.Drawing.Point(9, 37);
      btnClearSearch.Size = new System.Drawing.Size(335, 27);
      btnClearSearch.Enabled = false;
      txtSearch.PlaceholderText = "Filter: words AND, -word excludes, ? unnamed, >/< /= patch first-seen";
    }

    private readonly struct AssetSearchEntry {
      internal String Id { get; }
      internal String DisplayName { get; }
      internal Boolean IsNamed { get; }
      internal String FirstSeenVersion { get; }
      internal String ResourcePath { get; }
      internal String FileId { get; }
      internal String SearchAlias { get; }

      internal AssetSearchEntry(String id, String displayName, Boolean isNamed, String firstSeenVersion, String resourcePath, String fileId, String searchAlias = null) {
        Id = id ?? String.Empty;
        DisplayName = displayName ?? String.Empty;
        IsNamed = isNamed;
        FirstSeenVersion = firstSeenVersion;
        ResourcePath = resourcePath ?? String.Empty;
        FileId = fileId ?? String.Empty;
        SearchAlias = searchAlias ?? String.Empty;
      }
    }

    private static Dictionary<String, String> BuildHeroScriptQuickOpenAliases(Assets assets) {
      Dictionary<String, String> result = new Dictionary<String, String>(StringComparer.OrdinalIgnoreCase);
      if (assets == null) return result;

      foreach (String listPath in new[] {
        "/resources/systemgenerated/scriptdef.list",
        "/resources/systemgenerated/scripts.list"
      }) {
        try {
          using TorArchive.File listFile = assets.FindFile(listPath);
          if (listFile == null) continue;
          using System.IO.Stream stream = listFile.OpenCopyInMemory();
          using System.IO.BinaryReader reader = new System.IO.BinaryReader(stream, System.Text.Encoding.UTF8, false);
          ViewHeroScriptLists.HeroScriptListInfo list = ViewHeroScriptLists.Parse(reader);
          foreach (ViewHeroScriptLists.HeroScriptListEntry script in list.Scripts) {
            if (script == null || String.IsNullOrWhiteSpace(script.Name)) continue;
            String compiledPath = "/resources/systemgenerated/compilednative/"
              + script.Id.ToString(CultureInfo.InvariantCulture);
            result[compiledPath] = script.Name;
          }
          // Live clients use scriptdef.list; beta clients use scripts.list. The first
          // successfully named list is authoritative and avoids parsing both formats.
          if (result.Count > 0) break;
        }
        catch (Exception ex) {
          System.Diagnostics.Debug.WriteLine("HeroScript Quick Open index failed for " + listPath + ": " + ex.Message);
        }
      }
      return result;
    }

    private static String BuildAssetQuickOpenPath(HashFileInfo info) {
      if (info == null) return String.Empty;
      if (!info.IsNamed) return "File ID 0x" + (info.File == null ? String.Empty : info.File.FileInfo.FileId.ToString("X16", CultureInfo.InvariantCulture));
      String directory = (info.Directory ?? String.Empty).TrimEnd('/', '\\');
      String name = info.FileName ?? String.Empty;
      return (directory + "/" + name).Replace("//", "/");
    }

    private sealed class AssetTreeFilterResult {
      internal Dictionary<String, TreeListItem> Items { get; }
      internal List<String> DisplayMatches { get; }
      internal Int32 TotalMatches { get; }
      internal Int32 HistoryUnknownCount { get; }
      internal Boolean IsTruncated { get; }
      internal TreeViewFast.Controls.TreeViewFast.PreparedTree PreparedTree { get; }

      internal AssetTreeFilterResult(
        Dictionary<String, TreeListItem> items,
        List<String> displayMatches,
        Int32 totalMatches,
        Int32 historyUnknownCount = 0,
        Boolean isTruncated = false,
        TreeViewFast.Controls.TreeViewFast.PreparedTree preparedTree = null
      ) {
        Items = items;
        DisplayMatches = displayMatches;
        TotalMatches = totalMatches;
        HistoryUnknownCount = historyUnknownCount;
        IsTruncated = isTruncated;
        PreparedTree = preparedTree;
      }
    }

    private AssetTreeFilterResult BuildAssetTreeFilter(TreeFilterQuery filter, CancellationToken token) {
      Dictionary<String, TreeListItem> assets = m_assetDict;
      if (assets == null) return new AssetTreeFilterResult(
        new Dictionary<String, TreeListItem>(StringComparer.OrdinalIgnoreCase),
        new List<String>(),
        0
      );

      var displayMatches = new List<String>(AssetTreeFilterDisplayLimit);
      Int32 totalMatches = 0;
      Int32 historyUnknownCount = 0;
      Int32 scanned = 0;
      Boolean truncated = false;
      AssetSearchEntry[] searchIndex = m_assetSearchIndex ?? Array.Empty<AssetSearchEntry>();

      // This is deliberately a bounded display search rather than an exact-result-count scan.
      // Broad queries such as "a" used to keep scanning all ~millions of names merely to compute
      // a status-bar number after the first 2,500 visible hits had already been found. Stop on the
      // first additional hit instead and report 2,500+; narrow searches still scan to completion.
      foreach (AssetSearchEntry entry in searchIndex) {
        if ((++scanned & 0xff) == 0) token.ThrowIfCancellationRequested();

        Boolean match;
        if (filter.ShowUnnamedOnly) {
          match = !entry.IsNamed;
        } else {
          Boolean textMatch = entry.IsNamed
            ? filter.Matches(entry.Id, entry.DisplayName)
            : filter.RequiredTerms.Count == 0;
          if (!textMatch) continue;

          if (filter.HasVersionTerms && String.IsNullOrWhiteSpace(entry.FirstSeenVersion)) {
            historyUnknownCount++;
            continue;
          }
          match = filter.MatchesVersion(entry.FirstSeenVersion);
        }
        if (!match) continue;

        totalMatches++;
        if (displayMatches.Count < AssetTreeFilterDisplayLimit) {
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
          if (!assets.TryGetValue(current, out TreeListItem currentItem)
              || currentItem == null
              || String.IsNullOrWhiteSpace(currentItem.ParentId)) break;
          current = currentItem.ParentId;
        }

        if (include.Count + path.Count > AssetTreeFilterNodeLimit) {
          truncated = true;
          break;
        }
        foreach (String pathId in path) include.Add(pathId);
        retainedMatches.Add(id);
      }
      displayMatches = retainedMatches;

      var filtered = new Dictionary<String, TreeListItem>(include.Count, StringComparer.OrdinalIgnoreCase);
      foreach (String id in include) {
        if (assets.TryGetValue(id, out TreeListItem item) && item != null)
          filtered[id] = item;
      }

      // Prepare the small result tree on this worker thread as well. The UI thread only attaches
      // the already-linked roots to the dedicated result TreeView; it never sorts/allocates the
      // result hierarchy and, crucially, never clears the gigantic full TreeView.
      String getId(TreeListItem x) => x.Id;
      String getParentId(TreeListItem x) => x.ParentId;
      String getDisplayName(TreeListItem x) => x.DisplayName;
      Int32 getImageIndex(TreeListItem x) => x?.HashInfo?.File != null ? 2 : 1;
      Int32 compare(TreeListItem x, TreeListItem y) {
        Boolean xFile = x?.HashInfo?.File != null;
        Boolean yFile = y?.HashInfo?.File != null;
        if (xFile != yFile) return xFile ? 1 : -1;
        return String.Compare(x?.Id, y?.Id, StringComparison.Ordinal);
      }
      TreeViewFast.Controls.TreeViewFast.PreparedTree prepared =
        TreeViewFast.Controls.TreeViewFast.PrepareItems(
          filtered.Values, getId, getParentId, getDisplayName, getImageIndex, compare,
          () => m_closing || token.IsCancellationRequested
        );

      return new AssetTreeFilterResult(
        filtered, displayMatches, totalMatches, historyUnknownCount, truncated, prepared
      );
    }

    private async void ApplyAssetTreeLiveFilter() {
      if (m_closing || m_assetDict == null || treeViewFast1 == null || txtSearch == null) return;
      if (InvokeRequired) { BeginInvoke(new Action(ApplyAssetTreeLiveFilter)); return; }

      TreeFilterQuery filter = TreeFilterQuery.Parse(txtSearch.Text, true);
      String query = filter.RawText;
      String selectedId = ActiveAssetTree?.SelectedNode?.Name;

      CancellationTokenSource previous = m_assetTreeFilterCancellation;
      var cancellation = new CancellationTokenSource();
      m_assetTreeFilterCancellation = cancellation;
      try { previous?.Cancel(); previous?.Dispose(); } catch { }
      Int32 generation = ++m_assetTreeFilterGeneration;

      if (filter.IsEmpty) {
        ApplyAssetTreeFilterResult(
          filter,
          selectedId,
          new AssetTreeFilterResult(m_assetDict, new List<String>(), 0)
        );
        return;
      }

      // Feedback is immediate while the expensive part runs off-thread. Without this separation
      // TextChanged used to scan and rebuild the entire SWTOR tree on the UI thread after every
      // debounce tick, which makes the whole application appear hung for broad queries.
      StatusLabel1Text("Filtering assets ...");

      AssetTreeFilterResult result;
      try {
        result = await Task.Run(
          () => BuildAssetTreeFilter(filter, cancellation.Token),
          cancellation.Token
        );
      }
      catch (OperationCanceledException) {
        return;
      }
      catch (ObjectDisposedException) {
        return;
      }

      if (m_closing || cancellation.IsCancellationRequested
          || generation != m_assetTreeFilterGeneration
          || !String.Equals(query, (txtSearch.Text ?? String.Empty).Trim(), StringComparison.Ordinal)) return;

      ApplyAssetTreeFilterResult(filter, selectedId, result);
    }

    private void ApplyAssetTreeFilterResult(
      TreeFilterQuery filter,
      String selectedId,
      AssetTreeFilterResult result
    ) {
      if (m_closing || treeViewFast1 == null || m_assetFilterTree == null) return;

      String query = filter?.RawText ?? String.Empty;
      if (filter == null || filter.IsEmpty) {
        // Do not call Nodes.Clear()/LoadPrepared() here. The full native tree has remained alive
        // and untouched behind the result view, so clearing the filter is now only a visibility
        // swap instead of destruction/recreation of hundreds of thousands of Win32 tree items.
        m_assetFilterTree.Visible = false;
        treeViewFast1.Visible = true;
        treeViewFast1.BringToFront();
        btnClearSearch.Enabled = false;
        StatusLabel1Text(
          m_compareFiles
            ? "Comparison loaded. Showing New, Changed, Removed and Unchanged files."
            : "Showing all assets."
        );
        return;
      }

      if (result == null || result.PreparedTree == null) return;
      Dictionary<String, TreeListItem> filtered = result.Items;
      List<String> directMatches = result.DisplayMatches;

      m_assetFilterTree.BeginUpdate();
      try {
        // Clearing this control is cheap: it contains at most the bounded filter result, never the
        // full SWTOR asset tree.
        m_assetFilterTree.LoadPrepared(result.PreparedTree);

        if (directMatches.Count <= AssetTreeFilterAutoExpandLimit) {
          var expanded = new HashSet<String>(StringComparer.Ordinal);
          foreach (String id in directMatches) {
            if (!result.PreparedTree.NodeMap.TryGetValue(id, out TreeNode node)) continue;
            for (TreeNode parent = node.Parent; parent != null; parent = parent.Parent) {
              if (expanded.Add(parent.Name)) parent.Expand();
            }
          }
        } else {
          // For broad filters, expanding thousands of paths produces another native TreeView storm.
          // Show the top-level result folders and let the user narrow the query or expand manually.
          foreach (TreeNode root in m_assetFilterTree.Nodes) root.Expand();
        }

        if (!String.IsNullOrWhiteSpace(selectedId) && filtered.ContainsKey(selectedId)) {
          try { m_assetFilterTree.SelectedNode = m_assetFilterTree.GetNode(selectedId); } catch { }
        }
      } finally {
        m_assetFilterTree.EndUpdate();
      }

      treeViewFast1.Visible = false;
      m_assetFilterTree.Visible = true;
      m_assetFilterTree.BringToFront();

      btnClearSearch.Enabled = txtSearch.Enabled && query.Length > 0;
      String historyNote = filter.HasVersionTerms && result.HistoryUnknownCount > 0
        ? " " + result.HistoryUnknownCount.ToString("n0")
          + " otherwise matching files encountered before the display limit have no local first-seen history."
        : String.Empty;

      if (result.IsTruncated) {
        String countText = result.TotalMatches > AssetTreeFilterDisplayLimit
          ? AssetTreeFilterDisplayLimit.ToString("n0") + "+"
          : result.TotalMatches.ToString("n0");
        StatusLabel1Text(
          "Filter: " + countText + " matches. Showing first "
          + directMatches.Count.ToString("n0") + " (" + filtered.Count.ToString("n0")
          + " nodes including folders); type more characters to narrow the result." + historyNote
        );
      } else {
        StatusLabel1Text(
          "Filter: " + result.TotalMatches.ToString("n0") + " matches ("
          + filtered.Count.ToString("n0") + " nodes including folders)." + historyNote
        );
      }
    }

    #region Search
    private async void Search() {
      StatusLabel1Text("Performing Search ...");
      m_searchNodes ??= new List<String>();
      String search = txtSearch.Text ?? String.Empty;
      m_searchNodes = m_assetDict
        .Where(pair => pair.Key.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0
          || (!String.IsNullOrWhiteSpace(pair.Value?.DisplayName)
              && pair.Value.DisplayName.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0))
        .Select(pair => pair.Key)
        .ToList();

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

          // RED/Beta builds can predate the global STB manifest. It is an
          // optional filename source, so do not abort the complete finder.
          if (manifest != null) {
            using Stream manifestStream = manifest.OpenCopyInMemory();
            stb_parser.ParseSTBManifest(manifestStream);
            m_filesSearched++;
          } else {
            System.Diagnostics.Debug.WriteLine("Filename Finder: stb.manifest is absent in this client build.");
          }

          m_namesFound = stb_parser.FileNames.Count;

          stb_parser.WriteFile();
          break;

        default:
          break;
      }

      m_totalFilesSearched += m_filesSearched;
      m_totalNamesFound += m_namesFound;

      return;
    }

    /// <summary>
    /// Jedipedia derives a surprising number of otherwise unknown paths from small shipped index
    /// files instead of brute forcing names. Pull the same cheap references into the candidate set:
    /// the Scaleform GUI .lst files contain literal GUI XML names, while stb.manifest lists every
    /// global string table stem. Final acceptance still happens only after exact PH+SH validation.
    /// </summary>
    private void HarvestJedipediaReferenceLists(HashSet<String> candidates) {
      if (candidates == null || m_currentAssets == null) return;

      foreach (String listPath in new[] {
        "/resources/guixml/_heguixml.lst",
        "/resources/guixml/guixml.lst"
      }) {
        try {
          using TorArchive.File listFile = m_currentAssets.FindFile(listPath);
          if (listFile == null) continue;
          using Stream stream = listFile.OpenCopyInMemory();
          using var reader = new StreamReader(stream, Encoding.UTF8, true, 8192, false);
          Int32 read = 0;
          while (!reader.EndOfStream && read++ < 100000) {
            String name = reader.ReadLine()?.Trim();
            if (String.IsNullOrWhiteSpace(name)) continue;
            String normalized = name.Replace('\\', '/').TrimStart('/');
            if (normalized.StartsWith("resources/", StringComparison.OrdinalIgnoreCase))
              normalized = normalized.Substring("resources/".Length);
            if (!normalized.StartsWith("guixml/", StringComparison.OrdinalIgnoreCase))
              normalized = "guixml/" + normalized;
            candidates.Add(("/resources/" + normalized).Replace("//", "/").ToLowerInvariant());
          }
        }
        catch (Exception ex) {
          System.Diagnostics.Debug.WriteLine("Filename Finder LST harvest failed for " + listPath + ": " + ex.Message);
        }
      }

      try {
        using TorArchive.File manifestFile = m_currentAssets.FindFile("/resources/gamedata/str/stb.manifest");
        if (manifestFile != null) {
          using Stream stream = manifestFile.OpenCopyInMemory();
          using var reader = new StreamReader(stream, Encoding.UTF8, true, 8192, false);
          String manifestText = reader.ReadToEnd();
          var doc = new XmlDocument();
          doc.LoadXml(manifestText);
          Int32 count = 0;
          foreach (XmlElement file in doc.GetElementsByTagName("file").OfType<XmlElement>()) {
            if (count++ >= 100000) break;
            String stem = file.GetAttribute("val")?.Trim();
            if (String.IsNullOrWhiteSpace(stem)) continue;
            stem = stem.Replace('.', '/').Replace('\\', '/').Trim('/').ToLowerInvariant();
            if (stem.Length == 0) continue;
            // Seed one canonical locale only. The final exact-hash validation pass expands
            // en-us/de-de/fr-fr centrally, so the manifest harvester follows the same rules as
            // CNV, BNK, STB, tutorial images and imported candidate lists.
            candidates.Add("/resources/en-us/" + stem + ".stb");
          }
        }
      }
      catch (Exception ex) {
        System.Diagnostics.Debug.WriteLine("Filename Finder STB manifest harvest failed: " + ex.Message);
      }
    }

    private void TestHashFiles(String singleFile = null) {
      lock (s_filenameFinderLock) TestHashFilesCore(singleFile);
    }

    private void TestHashFilesCore(String singleFile = null) {
      m_foundFiles?.Clear();

      String[] testFiles;

      if (singleFile != null) testFiles = new String[] { singleFile };
      else {
        String candidateDirectory = m_extractPath + "\\File_Names\\";
        testFiles = Directory.Exists(candidateDirectory)
          ? Directory.GetFiles(candidateDirectory)
          : Array.Empty<String>();
      }

      m_foundFiles = new HashSet<String>(StringComparer.OrdinalIgnoreCase);

      // Aggregate candidates first. The old implementation rebuilt a complete multi-million-row
      // hash->archive index (and used to save the whole dictionary) before testing parser output.
      // Neither is necessary: a candidate can be validated directly against the currently loaded
      // TOR libraries by its PH+SH lookup.
      HashSet<String> testLines = new HashSet<String>(StringComparer.OrdinalIgnoreCase);

      foreach (String file in testFiles) {
        if (file.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)) { // Import jedipedia hashes.bin format
          using FileStream fs = new FileStream(
            file, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete
          );
          using BinaryReader br = new BinaryReader(fs);

          while (br.BaseStream.Position != br.BaseStream.Length) {
            _ = br.ReadUInt32(); //ph
            _ = br.ReadUInt32(); //sh

            Byte len = br.ReadByte(); //filename length
            Byte nul = br.ReadByte();

            if (nul != 0x00) { /* string second_len = "????"; */ }

            String filename = Encoding.Default.GetString(br.ReadBytes(len));
            if (!String.IsNullOrWhiteSpace(filename)) testLines.Add(filename.ToLowerInvariant());
          }

        } else {
          String[] lines = System.IO.File.ReadAllLines(file);

          foreach (String line in lines) {
            if (line.Contains('#')) { // Old hash dict format
              String[] temp = line.Split('#');

              if (temp.Length < 3 || temp[2].Length == 0) continue;
              else testLines.Add(temp[2].ToLowerInvariant());

            } else if (line.Contains('?')) { // New hash dict format
              String[] temp = line.Split('?');

              if (temp.Length < 4 || temp[3].Length == 0) continue;
              else testLines.Add(temp[3].ToLowerInvariant());

            } else if (!String.IsNullOrWhiteSpace(line)) {
              testLines.Add(line.ToLowerInvariant());
            }
          }
        }
      }

      // Use REAL SWTOR paths from HashInfo for the historical Jedipedia wildcard resolver. At
      // the same time keep direct references to the unresolved files in the CURRENT build. This
      // turns final validation into O(candidates) hash lookups instead of O(candidates * TORs).
      var currentUnnamedHashes = new HashSet<UInt64>();
      var currentUnnamedFiles = new Dictionary<UInt64, List<HashFileInfo>>();
      var legacyTargetFiles = new Dictionary<UInt64, HashFileInfo>();
      IEnumerable<String> knownPaths = Enumerable.Empty<String>();
      if (m_assetDict != null) {
        foreach (TreeListItem item in m_assetDict.Values) {
          if (item != null && item.CompareState == BuildFileState.Removed) continue;
          HashFileInfo info = item?.HashInfo;
          TorArchive.File sourceFile = info?.File;
          if (sourceFile?.FileInfo == null || sourceFile.Archive == null) continue;
          if (!info.IsNamed) {
            UInt64 signature = ((UInt64)sourceFile.FileInfo.PrimaryHash << 32)
                             | sourceFile.FileInfo.SecondaryHash;
            currentUnnamedHashes.Add(signature);

            if (!currentUnnamedFiles.TryGetValue(signature, out List<HashFileInfo> targets)) {
              targets = new List<HashFileInfo>();
              currentUnnamedFiles.Add(signature, targets);
            }
            // The same HashFileInfo is intentionally present below /unnamed and /new or /modified.
            // Keep one physical target reference so a discovered name is not written repeatedly.
            if (!targets.Contains(info)) targets.Add(info);

            if (LegacyUnnamedFileNameResolver.IsHintHash(signature)
                && !legacyTargetFiles.ContainsKey(signature))
              legacyTargetFiles.Add(signature, info);
          }
        }

        knownPaths = m_assetDict.Values
          .Where(x => x?.HashInfo != null && x.HashInfo.IsNamed
                   && !String.IsNullOrWhiteSpace(x.HashInfo.FileName)
                   && LegacyUnnamedFileNameResolver.MayHelpKnownPath(
                        x.HashInfo.Directory, x.HashInfo.FileName))
          .Select(x => {
            String directory = (x.HashInfo.Directory ?? String.Empty).TrimEnd('/', '\\');
            return String.IsNullOrEmpty(directory)
              ? x.HashInfo.FileName
              : directory + "/" + x.HashInfo.FileName;
          })
          .Distinct(StringComparer.OrdinalIgnoreCase);
      }

      // A patch can move a file to another TOR while the old archive entry already has the name.
      // Recover those names with one linear dictionary pass, without materializing the global
      // archive master index. They are still validated against the CURRENT build below.
      if (currentUnnamedHashes.Count > 0) {
        Dictionary<UInt64, String> knownCurrentNames =
          m_hashData.Dictionary.FindKnownFileNames(currentUnnamedHashes);
        foreach (String knownName in knownCurrentNames.Values)
          if (!String.IsNullOrWhiteSpace(knownName)) testLines.Add(knownName.ToLowerInvariant());
      }

      // Jedipedia also learns names from small reference lists shipped by the client. This costs only
      // three direct archive lookups and avoids brute forcing every GUI XML / global string-table name.
      // As with every other finder source, these are merely candidates until the exact PH+SH check below.
      if (currentUnnamedFiles.Count > 0) HarvestJedipediaReferenceLists(testLines);

      // Some cooked SWTOR formats contain their own resource path. This is a high-value source for
      // otherwise completely unknown hashes (for example generated .dat files). The scan is bounded
      // and every path is accepted only if it reproduces the containing file's exact PH+SH.
      foreach (String resolved in LegacyUnnamedFileNameResolver.ResolveEmbeddedPaths(currentUnnamedFiles))
        testLines.Add(resolved);

      foreach (String resolved in LegacyUnnamedFileNameResolver.Resolve(
                 m_hashData.Dictionary,
                 knownPaths,
                 testLines,
                 currentUnnamedHashes,
                 legacyTargetFiles)) {
        testLines.Add(resolved);
      }

      if (currentUnnamedFiles.Count == 0) return;

      // Locale variants are deliberately generated only here, after every parser/heuristic has
      // produced its structural candidates. A de-de/fr-fr filename is therefore persisted only
      // when its own SWTOR PH+SH is present among the unresolved files in THIS loaded build. This
      // discovers localized siblings without polluting the dictionary merely because an en-us
      // spelling was plausible.
      foreach (String structuralCandidate in LegacyUnnamedFileNameResolver.ExpandCandidates(testLines)) {
        foreach (String line in LegacyUnnamedFileNameResolver.ExpandLocalizedCandidate(structuralCandidate)) {
          if (String.IsNullOrWhiteSpace(line)) continue;

          FileId candidateId = FileId.FromFilePath(line);
          UInt64 signature = ((UInt64)candidateId.Ph << 32) | candidateId.Sh;
          if (!currentUnnamedFiles.TryGetValue(signature, out List<HashFileInfo> targets)) continue;

          // Hash equality already proves the candidate belongs to these unresolved current-build
          // entries. Persist it for each physical TOR copy represented by the Asset Browser without
          // calling Library.FindFile() for every generated probe.
          Boolean persisted = false;
          foreach (HashFileInfo target in targets) {
            TorArchive.File currentFile = target?.File;
            if (currentFile?.FileInfo == null || currentFile.Archive == null) continue;
            if (currentFile.FileInfo.PrimaryHash != candidateId.Ph
                || currentFile.FileInfo.SecondaryHash != candidateId.Sh) continue;

            m_hashData.Dictionary.AddHash(
              candidateId.Ph,
              candidateId.Sh,
              line,
              currentFile.FileInfo.CRC,
              currentFile.Archive.StrippedFileName
            );
            persisted = true;
          }

          if (persisted) m_foundFiles.Add(line);
        }
      }
    }

    #endregion Hash List Methods

    private void JbaSpeedComboSelectedIndexChanged(Object sender, EventArgs e) {
      if (!m_jbaActive || m_panelRender == null || m_jbaSpeedCombo == null) return;
      String text = m_jbaSpeedCombo.SelectedItem?.ToString() ?? "1x";
      if (text.EndsWith("x", StringComparison.OrdinalIgnoreCase)) text = text.Substring(0, text.Length - 1);
      if (!Single.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out Single speed)) speed = 1.0F;
      m_panelRender.SetAnimationSpeed(Math.Max(0.05F, Math.Min(8.0F, speed)));
    }

    private void JbaProgressBarMouseDown(Object sender, MouseEventArgs e) {
      if (!m_jbaActive || m_panelRender == null || toolStrip1ProgressBar1.ProgressBar.Width <= 1) return;
      Single ratio = Math.Max(0.0F, Math.Min(1.0F, e.X / (Single)Math.Max(1, toolStrip1ProgressBar1.ProgressBar.ClientSize.Width - 1)));
      m_panelRender.SeekAnimation(m_panelRender.AnimationLength * ratio);
      UpdateJbaToolbar();
    }

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

      if (m_sgtActive && m_sgtWaveReader != null && m_sgtWaveOut != null) {
        if (m_sgtWaveOut.PlaybackState == PlaybackState.Playing) {
          m_sgtWaveOut.Pause();
        } else {
          if (m_sgtWaveReader.Position >= m_sgtWaveReader.Length) m_sgtWaveReader.Position = 0;
          m_sgtWaveOut.Play();
        }
        UpdateSgtToolbar();
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

      if (m_sgtActive && m_sgtWaveReader != null && m_sgtWaveOut != null) {
        m_sgtWaveOut.Stop();
        m_sgtWaveReader.Position = 0;
        UpdateSgtToolbar();
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

      if (m_sgtActive && m_sgtWaveOut != null) {
        if (m_sgtWaveOut.Volume == 0) {
          toolStrip1Button3.Checked = false;
          toolStrip1Button3.ToolTipText = "Mute";
          m_sgtWaveOut.Volume = 1.0F;
        } else {
          toolStrip1Button3.Checked = true;
          toolStrip1Button3.ToolTipText = "Unmute";
          m_sgtWaveOut.Volume = 0.0F;
        }
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
      TreeNode node = e?.Node ?? (sender as TreeViewFast.Controls.TreeViewFast)?.SelectedNode;
      if (node?.Tag is not TreeListItem asset) return;

      if (asset.HashInfo?.File != null && !m_assetHistoryNavigation) RecordAssetNavigation(asset.Id);
      if (asset.HashInfo?.File != null) m_assetPageTabs?.UpdateCurrent(asset.Id, AssetTabTitle(asset));
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
        if (!String.IsNullOrWhiteSpace(info.FirstSeenVersion)) {
          dt.Rows.Add(new String[] {
            "First Seen",
            info.FirstSeenVersion
          });
        }
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
      else if (e.Control && e.KeyCode == Keys.E) {
        ShowAssetQuickOpen();
        e.Handled = true;
        e.SuppressKeyPress = true;
      }
    }

    protected override Boolean ProcessCmdKey(ref Message msg, Keys keyData) {
      if (m_assetPageTabs != null && m_assetPageTabs.ProcessShortcut(keyData)) return true;
      if (keyData == (Keys.Control | Keys.E)) { ShowAssetQuickOpen(); return true; }
      if (keyData == (Keys.Alt | Keys.Left)) { NavigateAssetHistory(-1); return true; }
      if (keyData == (Keys.Alt | Keys.Right)) { NavigateAssetHistory(1); return true; }
      return base.ProcessCmdKey(ref msg, keyData);
    }

    private void InitializeAssetPageTabs() {
      if (splitContainer2?.Panel1 == null) return;
      m_assetPageTabs = new BrowserSessionTabs {
        Location = new Point(0, 68),
        Width = splitContainer2.Panel1.Width,
        Height = 29,
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
      };
      m_assetPageTabs.NavigateRequested += delegate (Object sender, BrowserSessionTabs.NavigateEventArgs e) {
        if (String.IsNullOrWhiteSpace(e.Key)) return;
        m_assetHistoryNavigation = true;
        try { NavigateToAssetId(e.Key); }
        finally { m_assetHistoryNavigation = false; }
      };
      splitContainer2.Panel1.Controls.Add(m_assetPageTabs);
      m_assetPageTabs.BringToFront();
      AssetBrowserFormResize(this, EventArgs.Empty);
    }

    private static String AssetTabTitle(TreeListItem asset) {
      if (asset == null) return "Asset";
      try {
        if (asset.HashInfo?.IsNamed == true && !String.IsNullOrWhiteSpace(asset.HashInfo.FileName))
          return Path.GetFileName(asset.HashInfo.FileName);
      } catch { }
      return String.IsNullOrWhiteSpace(asset.DisplayName) ? asset.Id : asset.DisplayName;
    }

    internal Boolean MatchesAssetSource(String gamePath, Boolean usePts) {
      if (m_assetsUsePts != usePts) return false;
      try {
        return String.Equals(Assets.NormalizeGamePath(m_assetsLocation), Assets.NormalizeGamePath(gamePath), StringComparison.OrdinalIgnoreCase);
      } catch {
        return String.Equals(m_assetsLocation?.TrimEnd('\\', '/'), gamePath?.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
      }
    }

    internal void NavigateOrQueueResource(String resourcePath, Boolean newTab = false) {
      if (String.IsNullOrWhiteSpace(resourcePath)) return;
      if (TryNavigateResourcePath(resourcePath, newTab)) return;
      m_pendingResourceNavigation = resourcePath;
      m_pendingResourceNavigationNewTab = newTab;
      StatusLabel2Text("Waiting for asset index to open " + resourcePath + " ...");
    }

    private void ApplyPendingResourceNavigation() {
      if (String.IsNullOrWhiteSpace(m_pendingResourceNavigation)) return;
      String path = m_pendingResourceNavigation;
      Boolean newTab = m_pendingResourceNavigationNewTab;
      m_pendingResourceNavigation = null;
      m_pendingResourceNavigationNewTab = false;
      if (!TryNavigateResourcePath(path, newTab))
        StatusLabel2Text("Referenced asset was not found: " + path);
    }

    internal Boolean TryNavigateResourcePath(String raw, Boolean newTab = false) {
      if (String.IsNullOrWhiteSpace(raw) || m_assetDict == null) return false;
      foreach (String candidate in BuildStructuredAssetCandidates(raw)) {
        String normalized = candidate.Replace('\\', '/').Trim();
        if (!normalized.StartsWith("/resources/", StringComparison.OrdinalIgnoreCase))
          normalized = "/resources/" + normalized.TrimStart('/');
        String id = "/root/named" + normalized.ToLowerInvariant();
        if (!m_assetDict.TryGetValue(id, out TreeListItem item) || item?.HashInfo?.File == null) continue;
        try { if (treeViewFast1?.GetNode(id) == null) return false; } catch { return false; }
        if (newTab) m_assetPageTabs?.OpenInNewTab(id, AssetTabTitle(item), false);
        return NavigateToAssetId(id);
      }
      return false;
    }

    private void RecordAssetNavigation(String id) {
      if (String.IsNullOrWhiteSpace(id)) return;
      if (m_assetNavigationHistoryIndex >= 0 && m_assetNavigationHistoryIndex < m_assetNavigationHistory.Count
          && String.Equals(m_assetNavigationHistory[m_assetNavigationHistoryIndex], id, StringComparison.OrdinalIgnoreCase)) return;

      if (m_assetNavigationHistoryIndex + 1 < m_assetNavigationHistory.Count)
        m_assetNavigationHistory.RemoveRange(m_assetNavigationHistoryIndex + 1, m_assetNavigationHistory.Count - m_assetNavigationHistoryIndex - 1);
      m_assetNavigationHistory.Add(id);
      if (m_assetNavigationHistory.Count > 200) m_assetNavigationHistory.RemoveAt(0);
      m_assetNavigationHistoryIndex = m_assetNavigationHistory.Count - 1;
    }

    private void NavigateAssetHistory(Int32 direction) {
      Int32 target = m_assetNavigationHistoryIndex + direction;
      if (target < 0 || target >= m_assetNavigationHistory.Count) return;
      String id = m_assetNavigationHistory[target];
      m_assetHistoryNavigation = true;
      try {
        if (NavigateToAssetId(id)) m_assetNavigationHistoryIndex = target;
      } finally { m_assetHistoryNavigation = false; }
    }

    private Boolean NavigateToAssetId(String id) {
      if (String.IsNullOrWhiteSpace(id) || treeViewFast1 == null) return false;
      if (!String.IsNullOrWhiteSpace(txtSearch.Text)) {
        txtSearch.Clear();
        ApplyAssetTreeLiveFilter();
      }
      try {
        TreeNode node = treeViewFast1.GetNode(id);
        if (node == null) return false;
        treeViewFast1.SelectedNode = node;
        node.EnsureVisible();
        treeViewFast1.Focus();
        return true;
      } catch { return false; }
    }

    private void ShowAssetQuickOpen() {
      AssetSearchEntry[] entries = m_assetSearchIndex ?? Array.Empty<AssetSearchEntry>();
      if (entries.Length == 0) {
        StatusLabel2Text("Quick Open is available after the asset tree has finished loading.");
        return;
      }

      using QuickOpenDialog dialog = new QuickOpenDialog(
        "Open asset",
        "Filename, /resources/path, extension or file ID...",
        (query, token) => SearchAssetQuickOpen(entries, query, token)
      );
      if (dialog.ShowDialog(this) == DialogResult.OK && dialog.SelectedItem != null)
        NavigateToAssetId(dialog.SelectedItem.Key);
    }

    private static IEnumerable<QuickOpenDialog.Item> SearchAssetQuickOpen(AssetSearchEntry[] entries, String query, CancellationToken token) {
      String[] terms = (query ?? String.Empty).Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
      Int32 yielded = 0;
      Int32 scanned = 0;
      foreach (AssetSearchEntry entry in entries ?? Array.Empty<AssetSearchEntry>()) {
        if (((++scanned) & 0xFF) == 0) token.ThrowIfCancellationRequested();
        if (terms.Length != 0) {
          Boolean matches = true;
          foreach (String term in terms) {
            if (entry.Id.IndexOf(term, StringComparison.OrdinalIgnoreCase) < 0
                && entry.DisplayName.IndexOf(term, StringComparison.OrdinalIgnoreCase) < 0
                && entry.ResourcePath.IndexOf(term, StringComparison.OrdinalIgnoreCase) < 0
                && entry.FileId.IndexOf(term, StringComparison.OrdinalIgnoreCase) < 0
                && entry.SearchAlias.IndexOf(term, StringComparison.OrdinalIgnoreCase) < 0) {
              matches = false;
              break;
            }
          }
          if (!matches) continue;
        }

        String primary = String.IsNullOrWhiteSpace(entry.DisplayName) ? entry.ResourcePath : entry.DisplayName;
        String secondary = entry.ResourcePath;
        if (!String.IsNullOrWhiteSpace(entry.SearchAlias)) secondary += "   [HeroScript: " + entry.SearchAlias + "]";
        if (!String.IsNullOrWhiteSpace(entry.FileId)) secondary += "   [0x" + entry.FileId + "]";
        yield return new QuickOpenDialog.Item(entry.Id, primary, secondary, primary + " " + secondary + " " + entry.SearchAlias);
        yielded++;
        if (yielded > 500) yield break;
      }
    }

    private void TreeViewFast1MouseHover(Object sender, EventArgs e) {
      if (!m_closing && sender is TreeViewFast.Controls.TreeViewFast tree) tree.Focus();
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
    private void TreeViewGrid1MouseDoubleClickNavigateAsset(Object sender, MouseEventArgs e) {
      if (e.Button != MouseButtons.Left || treeViewGrid1?.SelectedObject is not NodeListItem item) return;
      Boolean newTab = (ModifierKeys & Keys.Control) == Keys.Control;
      if (TryNavigateStructuredAssetReference(item, newTab)) return;
      TryOpenStructuredNodeReference(item, newTab);
    }

    private Boolean TryNavigateStructuredAssetReference(NodeListItem item, Boolean newTab = false) {
      if (item == null || m_assetDict == null) return false;
      foreach (String raw in new[] { item.value as String, item.DisplayValue, item.DisplayName })
        if (TryNavigateResourcePath(raw, newTab)) return true;
      return false;
    }

    private Boolean TryOpenStructuredNodeReference(NodeListItem item, Boolean newTab) {
      if (item == null || m_currentAssets == null) return false;
      try {
        m_navigationDom ??= DomHandler.Instance.GetCurrentDOM(m_currentAssets);
      } catch { return false; }
      if (m_navigationDom == null) return false;

      GomObject obj = null;
      try {
        if (item.value is UInt64 u) obj = m_navigationDom.GetObject(u);
        else if (item.value is Int64 i && i >= 0) obj = m_navigationDom.GetObject((UInt64)i);
        else if (item.value is UInt32 u32) obj = m_navigationDom.GetObject((UInt64)u32);
        else if (item.value is Int32 i32 && i32 >= 0) obj = m_navigationDom.GetObject((UInt64)i32);
        else if (item.value is String text) {
          String candidate = text.Trim();
          if (UInt64.TryParse(candidate, out UInt64 id)) obj = m_navigationDom.GetObject(id);
          else if (candidate.IndexOf('/') < 0 && candidate.IndexOf('\\') < 0 && candidate.Contains('.'))
            obj = m_navigationDom.GetObject(candidate);
        }
      } catch { }
      if (obj == null) return false;
      BrowserNavigation.OpenNode(this, m_assetsLocation, m_assetsUsePts, obj.Name ?? obj.Id.ToString(CultureInfo.InvariantCulture), newTab);
      return true;
    }

    private static IEnumerable<String> BuildStructuredAssetCandidates(String raw) {
      if (String.IsNullOrWhiteSpace(raw)) yield break;
      String value = raw.Trim().Trim('"', '\'', '(', ')', '[', ']', '{', '}');
      Int32 resource = value.IndexOf("/resources/", StringComparison.OrdinalIgnoreCase);
      if (resource >= 0) value = value.Substring(resource);
      value = value.TrimEnd(',', ';', ':');
      if (value.IndexOf('/') < 0 && value.IndexOf('\\') < 0) yield break;

      yield return value;
      if (value.StartsWith("resources/", StringComparison.OrdinalIgnoreCase)) yield return "/" + value;

      String ext = Path.GetExtension(value);
      if (!String.IsNullOrWhiteSpace(ext)) yield break;
      foreach (String suffix in new[] { ".gr2", ".dds", ".tex", ".mat", ".fxspec", ".prt", ".jba", ".mph", ".mag", ".spt", ".stg", ".dyn", ".xml" })
        yield return value + suffix;
    }

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
      if (e.KeyCode == Keys.Enter) {
        m_assetTreeFilterTimer?.Stop();
        ApplyAssetTreeLiveFilter();
        e.Handled = true;
        e.SuppressKeyPress = true;
      } else if (e.KeyCode == Keys.Escape && !String.IsNullOrEmpty(txtSearch.Text)) {
        txtSearch.Clear();
        e.Handled = true;
        e.SuppressKeyPress = true;
      }
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
        file.CopyTo(outputStream, 128 * 1024);
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
      m_ddsPreview.ClearPreview();
      m_ddsPreview.Visible = false;
      m_materialSpecPreview?.ClearPreview();
      if (m_materialSpecPreview != null) m_materialSpecPreview.Visible = false;
      m_audioBankPreview?.ClearPreview();
      if (m_audioBankPreview != null) m_audioBankPreview.Visible = false;
      renderPanel.Visible = false;
      treeViewGrid1.Visible = false;
      txtRawView.Visible = false;
      webBrowser1.Visible = false;
    }

    #endregion
  }
}
