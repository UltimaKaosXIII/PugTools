using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using TorArchive;

namespace PugTools {
  internal partial class AssetBrowser {
    private sealed class IdenticalFileMatch {
      internal String Build { get; set; }
      internal HashFileInfo Info { get; set; }
      internal String Path => Info == null
        ? String.Empty
        : Info.IsNamed
          ? ((Info.Directory ?? String.Empty).TrimEnd('/', '\\') + "/" + Info.FileName).Replace("//", "/")
          : "[" + Info.File.FileInfo.FileId.ToString("X16") + "]";
    }

    private ToolStripMenuItem m_showIdenticalFilesMenuItem;

    private void InitializeIdenticalFilesContextMenu() {
      if (contextMenuStrip1 == null || m_showIdenticalFilesMenuItem != null) return;

      m_showIdenticalFilesMenuItem = new ToolStripMenuItem("Show identical files") {
        ToolTipText = "List loaded TOR entries with the same metadata checksum."
      };
      m_showIdenticalFilesMenuItem.Click += async (_, __) => await ShowIdenticalFilesForSelectionAsync();
      contextMenuStrip1.Items.Add(new ToolStripSeparator());
      contextMenuStrip1.Items.Add(m_showIdenticalFilesMenuItem);
      contextMenuStrip1.Opening += (_, __) => {
        TreeListItem selected = treeViewFast1?.SelectedNode?.Tag as TreeListItem;
        HashFileInfo info = SelectedPhysicalHashInfo(selected);
        m_showIdenticalFilesMenuItem.Enabled = info?.File?.FileInfo != null;
      };
    }

    private static HashFileInfo SelectedPhysicalHashInfo(TreeListItem item) {
      if (item == null) return null;
      if (item.CompareState == BuildFileState.Removed && item.PreviousHashInfo?.File?.FileInfo != null)
        return item.PreviousHashInfo;
      return item.HashInfo?.File?.FileInfo != null ? item.HashInfo : item.PreviousHashInfo;
    }

    private async Task ShowIdenticalFilesForSelectionAsync() {
      TreeListItem selected = treeViewFast1?.SelectedNode?.Tag as TreeListItem;
      HashFileInfo selectedInfo = SelectedPhysicalHashInfo(selected);
      if (selectedInfo?.File?.FileInfo == null) return;

      UInt32 checksum = selectedInfo.File.FileInfo.Checksum;
      const Int32 displayLimit = 1000;

      Form dialog = new Form {
        Text = "Identical files — checksum " + checksum.ToString("X8"),
        StartPosition = FormStartPosition.CenterParent,
        Width = Math.Min(1180, Screen.FromControl(this).WorkingArea.Width - 80),
        Height = Math.Min(720, Screen.FromControl(this).WorkingArea.Height - 80),
        MinimizeBox = false
      };

      Label heading = new Label {
        Dock = DockStyle.Top,
        Height = 34,
        Padding = new Padding(8, 9, 8, 0),
        Text = "Searching loaded TOR tables for checksum " + checksum.ToString("X8") + " …"
      };
      ListView list = new ListView {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        GridLines = true,
        HideSelection = false
      };
      list.Columns.Add("Build", 76);
      list.Columns.Add("Archive", 220);
      list.Columns.Add("File", 610);
      list.Columns.Add("PH/SH", 150);
      list.Columns.Add("Size", 100, HorizontalAlignment.Right);
      dialog.Controls.Add(list);
      dialog.Controls.Add(heading);
      dialog.Show(this);
      dialog.Refresh();

      IdenticalFileMatch[] shown = Array.Empty<IdenticalFileMatch>();
      Int32 total = 0;
      try {
        var result = await Task.Run(() => FindFilesByChecksum(checksum, displayLimit));
        if (dialog.IsDisposed) return;
        shown = result.Matches.ToArray();
        total = result.Total;
      } catch (Exception ex) {
        if (!dialog.IsDisposed) heading.Text = "Checksum search failed: " + ex.Message;
        return;
      }

      if (dialog.IsDisposed) return;
      list.BeginUpdate();
      try {
        foreach (IdenticalFileMatch match in shown) {
          HashFileInfo info = match.Info;
          TorArchive.File file = info?.File;
          if (file?.FileInfo == null) continue;
          var row = new ListViewItem(match.Build ?? String.Empty) { Tag = match };
          row.SubItems.Add(Path.GetFileName(file.Archive?.FileName ?? info.Source ?? String.Empty));
          row.SubItems.Add(match.Path);
          row.SubItems.Add(file.FileInfo.PrimaryHash.ToString("X8") + "/" + file.FileInfo.SecondaryHash.ToString("X8"));
          row.SubItems.Add(file.FileInfo.UncompressedSize.ToString("N0"));
          if (SamePhysicalFile(selectedInfo, info)) row.Font = new Font(list.Font, FontStyle.Bold);
          list.Items.Add(row);
        }
      } finally { list.EndUpdate(); }

      heading.Text = total <= displayLimit
        ? (total == 1 ? "Only one loaded file has checksum " : total.ToString("N0") + " loaded files have checksum ") + checksum.ToString("X8") + "."
        : total.ToString("N0") + " loaded files have checksum " + checksum.ToString("X8") + "; showing the first " + displayLimit.ToString("N0") + ".";

      list.DoubleClick += (_, __) => {
        if (list.SelectedItems.Count == 0 || list.SelectedItems[0].Tag is not IdenticalFileMatch match) return;
        TryNavigateToIdenticalMatch(match);
      };
    }

    private (List<IdenticalFileMatch> Matches, Int32 Total) FindFilesByChecksum(UInt32 checksum, Int32 displayLimit) {
      var matches = new List<IdenticalFileMatch>(Math.Min(displayLimit, 128));
      Int32 total = 0;

      void Scan(Assets assets, String build) {
        if (assets?.Libraries == null) return;
        foreach (Library library in assets.Libraries) {
          if (m_closing) return;
          if (library == null) continue;
          // Assets.Load() loads libraries before the browser tree is built; this is merely defensive.
          if (!library.Loaded) library.Load();
          foreach (Archive archive in library.Archives.Values) {
            if (archive == null) continue;
            foreach (TorArchive.FileInfo meta in archive.EnumerateFileInfos()) {
              if (meta == null || meta.Checksum != checksum) continue;
              total++;
              if (matches.Count >= displayLimit) continue;
              TorArchive.File file = new TorArchive.File(archive, meta);
              HashFileInfo info = new HashFileInfo(meta.PrimaryHash, meta.SecondaryHash, file, false, false);
              matches.Add(new IdenticalFileMatch { Build = build, Info = info });
            }
          }
        }
      }

      Scan(m_currentAssets, "Current");
      if (m_compareFiles && m_previousAssets != null && !ReferenceEquals(m_currentAssets, m_previousAssets))
        Scan(m_previousAssets, "Previous");

      return (matches, total);
    }

    private static Boolean SamePhysicalFile(HashFileInfo left, HashFileInfo right) {
      if (left?.File?.FileInfo == null || right?.File?.FileInfo == null) return false;
      return left.File.FileInfo.FileId == right.File.FileInfo.FileId
        && String.Equals(left.File.Archive?.FileName, right.File.Archive?.FileName, StringComparison.OrdinalIgnoreCase);
    }

    private void TryNavigateToIdenticalMatch(IdenticalFileMatch match) {
      HashFileInfo wanted = match?.Info;
      if (wanted?.File?.FileInfo == null || m_assetDict == null || treeViewFast1 == null) return;
      String archive = wanted.File.Archive?.FileName ?? String.Empty;
      UInt64 fileId = wanted.File.FileInfo.FileId;
      Boolean previous = String.Equals(match.Build, "Previous", StringComparison.OrdinalIgnoreCase);

      TreeListItem target = m_assetDict.Values.FirstOrDefault(item => {
        HashFileInfo info = previous ? item?.PreviousHashInfo : item?.HashInfo;
        if (info?.File?.FileInfo == null && previous && item?.CompareState == BuildFileState.Removed)
          info = item.HashInfo;
        return info?.File?.FileInfo != null
          && info.File.FileInfo.FileId == fileId
          && String.Equals(info.File.Archive?.FileName, archive, StringComparison.OrdinalIgnoreCase);
      });
      if (target == null) return;
      TreeNode node = treeViewFast1.GetNode(target.Id);
      if (node == null) return;
      treeViewFast1.SelectedNode = node;
      node.EnsureVisible();
      treeViewFast1.Focus();
    }
  }
}
