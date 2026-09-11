using System;
using System.Globalization;
using System.Windows.Forms;
using BrightIdeasSoftware;
using GomLib;

namespace PugTools {
  internal partial class NodeBrowser {
    private OLVColumn _nodeFieldIdColumn;
    private ToolStripMenuItem _nodeShowFieldIdsMenuItem;
    private ToolStripMenuItem _nodeCopyFieldIdMenuItem;
    private ToolStripMenuItem _nodeCopyValueMenuItem;
    private ToolStripMenuItem _nodeOpenDomMenuItem;
    private NodeListItem _nodeReaderContextItem;

    /// <summary>
    /// Small reader conveniences that mirror the useful parts of browser-based GOM inspectors:
    /// raw DOM field ids are available on demand, but stay hidden by default so the classic
    /// three-column Node Browser layout remains unchanged for normal use.
    /// </summary>
    private void InitializeNodeReaderFeatures() {
      if (treeViewGrid1 == null || contextMenuStrip2 == null) return;

      _nodeFieldIdColumn = new OLVColumn("Field ID", nameof(NodeListItem.FieldId)) {
        Width = 176,
        IsVisible = false,
        Searchable = true,
        ToolTipText = "Raw numeric DOM field id"
      };
      treeViewGrid1.AllColumns.Add(_nodeFieldIdColumn);

      _nodeShowFieldIdsMenuItem = new ToolStripMenuItem("Show field ID column") {
        CheckOnClick = true,
        ShortcutKeyDisplayString = "Ctrl+I"
      };
      _nodeShowFieldIdsMenuItem.CheckedChanged += delegate {
        SetNodeFieldIdColumnVisible(_nodeShowFieldIdsMenuItem.Checked);
      };

      _nodeOpenDomMenuItem = new ToolStripMenuItem("Open field/class in DOM Browser");
      _nodeOpenDomMenuItem.Click += delegate {
        if (_nodeReaderContextItem?.NavigationTarget is DomType domType)
          BrowserNavigation.OpenDom(this, _assetsLocation, _assetsUsePts, domType.Id);
      };

      _nodeCopyFieldIdMenuItem = new ToolStripMenuItem("Copy field ID");
      _nodeCopyFieldIdMenuItem.Click += delegate {
        String id = _nodeReaderContextItem?.FieldId;
        if (!String.IsNullOrWhiteSpace(id)) Clipboard.SetText(id);
      };

      _nodeCopyValueMenuItem = new ToolStripMenuItem("Copy value");
      _nodeCopyValueMenuItem.Click += delegate {
        String value = _nodeReaderContextItem?.DisplayValue;
        if (!String.IsNullOrEmpty(value)) Clipboard.SetText(value);
      };

      contextMenuStrip2.Items.Add(new ToolStripSeparator());
      contextMenuStrip2.Items.Add(_nodeOpenDomMenuItem);
      contextMenuStrip2.Items.Add(_nodeCopyFieldIdMenuItem);
      contextMenuStrip2.Items.Add(_nodeCopyValueMenuItem);
      contextMenuStrip2.Items.Add(new ToolStripSeparator());
      contextMenuStrip2.Items.Add(_nodeShowFieldIdsMenuItem);
    }

    private Boolean ProcessNodeReaderShortcut(Keys keyData) {
      if (keyData != (Keys.Control | Keys.I)) return false;
      if (_nodeShowFieldIdsMenuItem == null) return false;
      _nodeShowFieldIdsMenuItem.Checked = !_nodeShowFieldIdsMenuItem.Checked;
      return true;
    }

    private void UpdateNodeReaderContextMenu(NodeListItem item) {
      _nodeReaderContextItem = item;
      if (_nodeOpenDomMenuItem != null)
        _nodeOpenDomMenuItem.Enabled = item?.NavigationTarget is DomType;
      if (_nodeCopyFieldIdMenuItem != null)
        _nodeCopyFieldIdMenuItem.Enabled = !String.IsNullOrWhiteSpace(item?.FieldId);
      if (_nodeCopyValueMenuItem != null)
        _nodeCopyValueMenuItem.Enabled = !String.IsNullOrEmpty(item?.DisplayValue);
    }

    private void SetNodeFieldIdColumnVisible(Boolean visible) {
      if (_nodeFieldIdColumn == null || treeViewGrid1 == null) return;
      _nodeFieldIdColumn.IsVisible = visible;
      treeViewGrid1.RebuildColumns();
      if (visible && treeViewGrid1.Columns.Contains(_nodeFieldIdColumn))
        _nodeFieldIdColumn.Width = Math.Max(150, _nodeFieldIdColumn.Width);
      StatusLabel1Text(visible
        ? "Field ID column enabled. Double-click DOM-linked rows or right-click for DOM navigation."
        : "Field ID column hidden.");
    }
  }
}
