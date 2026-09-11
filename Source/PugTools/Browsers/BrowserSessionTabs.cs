using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace PugTools {
  /// <summary>
  /// Lightweight in-memory document tabs for the large data browsers. The control stores only
  /// navigation keys and display titles; it never creates a persistent history/index database.
  /// </summary>
  internal sealed class BrowserSessionTabs : UserControl {
    internal sealed class NavigateEventArgs : EventArgs {
      internal String Key { get; }
      internal NavigateEventArgs(String key) { Key = key; }
    }

    private sealed class PageState {
      internal String Key;
      internal String Title;
      internal PageState(String key, String title) { Key = key; Title = title; }
      internal PageState Clone() => new PageState(Key, Title);
    }

    private readonly TabControl _tabs;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _closeItem;
    private readonly ToolStripMenuItem _closeOthersItem;
    private readonly ToolStripMenuItem _reopenItem;
    private readonly Stack<PageState> _closed = new Stack<PageState>();
    private Boolean _suppressSelection;

    internal event EventHandler<NavigateEventArgs> NavigateRequested;

    internal BrowserSessionTabs() {
      Height = 29;
      MinimumSize = new Size(80, 29);
      Margin = Padding.Empty;
      Padding = Padding.Empty;

      _tabs = new TabControl {
        Dock = DockStyle.Fill,
        Appearance = TabAppearance.FlatButtons,
        DrawMode = TabDrawMode.Normal,
        HotTrack = true,
        Multiline = false,
        ShowToolTips = true,
        SizeMode = TabSizeMode.Fixed,
        ItemSize = new Size(135, 24),
        Padding = new Point(8, 3),
        Margin = Padding.Empty
      };
      _tabs.SelectedIndexChanged += TabsSelectedIndexChanged;
      _tabs.MouseDown += TabsMouseDown;

      _menu = new ContextMenuStrip();
      ToolStripMenuItem newItem = new ToolStripMenuItem("New tab");
      newItem.ShortcutKeys = Keys.Control | Keys.T;
      newItem.Click += delegate { NewTab(); };
      ToolStripMenuItem duplicateItem = new ToolStripMenuItem("Duplicate tab");
      duplicateItem.Click += delegate { DuplicateCurrent(); };
      _closeItem = new ToolStripMenuItem("Close tab");
      _closeItem.ShortcutKeys = Keys.Control | Keys.W;
      _closeItem.Click += delegate { CloseCurrent(); };
      _closeOthersItem = new ToolStripMenuItem("Close other tabs");
      _closeOthersItem.Click += delegate { CloseOthers(); };
      ToolStripMenuItem closeAllItem = new ToolStripMenuItem("Close all tabs");
      closeAllItem.Click += delegate { CloseAll(); };
      _reopenItem = new ToolStripMenuItem("Reopen closed tab");
      _reopenItem.ShortcutKeys = Keys.Control | Keys.Shift | Keys.T;
      _reopenItem.Click += delegate { ReopenClosed(); };
      _menu.Items.Add(newItem);
      _menu.Items.Add(duplicateItem);
      _menu.Items.Add(new ToolStripSeparator());
      _menu.Items.Add(_closeItem);
      _menu.Items.Add(_closeOthersItem);
      _menu.Items.Add(closeAllItem);
      _menu.Items.Add(_reopenItem);
      _menu.Opening += delegate {
        _closeItem.Enabled = _tabs.TabPages.Count > 1 || CurrentState()?.Key != null;
        _closeOthersItem.Enabled = _tabs.TabPages.Count > 1;
        _reopenItem.Enabled = _closed.Count > 0;
      };
      _tabs.ContextMenuStrip = _menu;
      Controls.Add(_tabs);
      NewTab(false);
    }

    internal String CurrentKey => CurrentState()?.Key;

    internal void UpdateCurrent(String key, String title) {
      PageState state = CurrentState();
      if (state == null) {
        NewTab(false);
        state = CurrentState();
      }
      state.Key = key;
      state.Title = NormalizeTitle(title, key);
      if (_tabs.SelectedTab != null) {
        _tabs.SelectedTab.Text = state.Title;
        _tabs.SelectedTab.ToolTipText = key ?? String.Empty;
      }
    }

    internal void OpenInNewTab(String key, String title, Boolean navigate = true) {
      // Reuse the initial/last blank page instead of leaving an empty tab behind when a
      // cross-browser link is the first document opened in this browser.
      PageState current = CurrentState();
      if (_tabs.TabPages.Count == 1 && current != null && String.IsNullOrWhiteSpace(current.Key)) {
        UpdateCurrent(key, title);
        if (navigate && !String.IsNullOrWhiteSpace(key)) RaiseNavigate(key);
        return;
      }
      PageState state = new PageState(key, NormalizeTitle(title, key));
      TabPage page = CreatePage(state);
      _suppressSelection = true;
      try {
        _tabs.TabPages.Add(page);
        _tabs.SelectedTab = page;
      }
      finally { _suppressSelection = false; }
      if (navigate && !String.IsNullOrWhiteSpace(key)) RaiseNavigate(key);
    }

    internal void NewTab() => NewTab(true);

    private void NewTab(Boolean select) {
      PageState state = new PageState(null, "New tab");
      TabPage page = CreatePage(state);
      _suppressSelection = true;
      try {
        _tabs.TabPages.Add(page);
        if (select) _tabs.SelectedTab = page;
      }
      finally { _suppressSelection = false; }
    }

    internal void DuplicateCurrent() {
      PageState current = CurrentState();
      if (current == null) { NewTab(); return; }
      OpenInNewTab(current.Key, current.Title, !String.IsNullOrWhiteSpace(current.Key));
    }

    internal void CloseCurrent() {
      TabPage page = _tabs.SelectedTab;
      if (page == null) return;
      PageState state = page.Tag as PageState;
      if (_tabs.TabPages.Count == 1) {
        if (state != null && !String.IsNullOrWhiteSpace(state.Key)) _closed.Push(state.Clone());
        if (state != null) { state.Key = null; state.Title = "New tab"; }
        page.Text = "New tab";
        return;
      }
      Int32 oldIndex = _tabs.SelectedIndex;
      if (state != null && !String.IsNullOrWhiteSpace(state.Key)) _closed.Push(state.Clone());
      _suppressSelection = true;
      try {
        _tabs.TabPages.Remove(page);
        page.Dispose();
        _tabs.SelectedIndex = Math.Min(oldIndex, _tabs.TabPages.Count - 1);
      }
      finally { _suppressSelection = false; }
      PageState selected = CurrentState();
      if (selected != null && !String.IsNullOrWhiteSpace(selected.Key)) RaiseNavigate(selected.Key);
    }

    internal void CloseOthers() {
      TabPage keep = _tabs.SelectedTab;
      if (keep == null) return;
      List<TabPage> remove = new List<TabPage>();
      foreach (TabPage page in _tabs.TabPages) if (page != keep) remove.Add(page);
      _suppressSelection = true;
      try {
        foreach (TabPage page in remove) {
          if (page.Tag is PageState state && !String.IsNullOrWhiteSpace(state.Key)) _closed.Push(state.Clone());
          _tabs.TabPages.Remove(page);
          page.Dispose();
        }
      }
      finally { _suppressSelection = false; }
    }

    internal void CloseAll() {
      List<TabPage> pages = new List<TabPage>();
      foreach (TabPage page in _tabs.TabPages) pages.Add(page);
      _suppressSelection = true;
      try {
        foreach (TabPage page in pages) {
          if (page.Tag is PageState state && !String.IsNullOrWhiteSpace(state.Key)) _closed.Push(state.Clone());
          _tabs.TabPages.Remove(page);
          page.Dispose();
        }
        PageState blank = new PageState(null, "New tab");
        _tabs.TabPages.Add(CreatePage(blank));
        _tabs.SelectedIndex = 0;
      }
      finally { _suppressSelection = false; }
    }

    internal void ReopenClosed() {
      if (_closed.Count == 0) return;
      PageState state = _closed.Pop();
      OpenInNewTab(state.Key, state.Title, !String.IsNullOrWhiteSpace(state.Key));
    }

    internal Boolean ProcessShortcut(Keys keyData) {
      if (keyData == (Keys.Control | Keys.T)) { NewTab(); return true; }
      if (keyData == (Keys.Control | Keys.W)) { CloseCurrent(); return true; }
      if (keyData == (Keys.Control | Keys.Shift | Keys.W)) { CloseAll(); return true; }
      if (keyData == (Keys.Control | Keys.Shift | Keys.T)) { ReopenClosed(); return true; }
      if (keyData == (Keys.Control | Keys.Shift | Keys.R)) { DuplicateCurrent(); return true; }
      if (keyData == (Keys.Control | Keys.Tab)) { SelectRelative(1); return true; }
      if (keyData == (Keys.Control | Keys.Shift | Keys.Tab)) { SelectRelative(-1); return true; }
      return false;
    }

    private void SelectRelative(Int32 delta) {
      if (_tabs.TabPages.Count <= 1) return;
      Int32 current = Math.Max(0, _tabs.SelectedIndex);
      Int32 next = (current + delta) % _tabs.TabPages.Count;
      if (next < 0) next += _tabs.TabPages.Count;
      _tabs.SelectedIndex = next;
    }

    private void TabsSelectedIndexChanged(Object sender, EventArgs e) {
      if (_suppressSelection) return;
      PageState state = CurrentState();
      if (state != null && !String.IsNullOrWhiteSpace(state.Key)) RaiseNavigate(state.Key);
    }

    private void TabsMouseDown(Object sender, MouseEventArgs e) {
      if (e.Button != MouseButtons.Middle) return;
      for (Int32 i = 0; i < _tabs.TabPages.Count; i++) {
        if (!_tabs.GetTabRect(i).Contains(e.Location)) continue;
        _tabs.SelectedIndex = i;
        CloseCurrent();
        return;
      }
    }

    private PageState CurrentState() => _tabs.SelectedTab?.Tag as PageState;

    private static TabPage CreatePage(PageState state) => new TabPage(state?.Title ?? "New tab") {
      Tag = state,
      ToolTipText = state?.Key ?? String.Empty,
      UseVisualStyleBackColor = true
    };

    private static String NormalizeTitle(String title, String key) {
      String value = String.IsNullOrWhiteSpace(title) ? key : title;
      if (String.IsNullOrWhiteSpace(value)) return "New tab";
      value = value.Trim();
      const Int32 max = 24;
      return value.Length > max ? value.Substring(0, max - 1) + "…" : value;
    }

    private void RaiseNavigate(String key) {
      NavigateRequested?.Invoke(this, new NavigateEventArgs(key));
    }
  }
}
