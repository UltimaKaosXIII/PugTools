using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PugTools {
  /// <summary>
  /// Small database-free quick-open palette used by the Asset and Node browsers.
  /// It intentionally works on the already loaded in-memory indexes, so opening it
  /// never starts another archive/GOM scan.
  /// </summary>
  internal sealed class QuickOpenDialog : Form {
    internal sealed class Item {
      internal String Key { get; }
      internal String Primary { get; }
      internal String Secondary { get; }
      internal String SearchText { get; }

      internal Item(String key, String primary, String secondary, String searchText = null) {
        Key = key ?? String.Empty;
        Primary = primary ?? String.Empty;
        Secondary = secondary ?? String.Empty;
        SearchText = String.IsNullOrWhiteSpace(searchText)
          ? (Primary + " " + Secondary + " " + Key)
          : searchText;
      }
    }

    private const Int32 DisplayLimit = 500;
    private readonly List<Item> _items;
    private readonly Func<String, CancellationToken, IEnumerable<Item>> _searchProvider;
    private CancellationTokenSource _searchCancellation;
    private Int32 _searchGeneration;
    private readonly TextBox _query;
    private readonly ListView _results;
    private readonly Label _status;

    internal Item SelectedItem { get; private set; }

    internal QuickOpenDialog(String title, String placeholder, IEnumerable<Item> items, Func<String, CancellationToken, IEnumerable<Item>> searchProvider = null) {
      _items = searchProvider == null ? (items ?? Enumerable.Empty<Item>()).ToList() : new List<Item>();
      _searchProvider = searchProvider;
      Text = title ?? "Quick Open";
      StartPosition = FormStartPosition.CenterParent;
      Size = new Size(760, 520);
      MinimumSize = new Size(520, 340);
      KeyPreview = true;

      _query = new TextBox {
        Dock = DockStyle.Top,
        PlaceholderText = placeholder ?? "Type to search...",
        Margin = new Padding(8),
        Font = new Font(SystemFonts.MessageBoxFont.FontFamily, 10F)
      };
      _query.TextChanged += delegate { RebuildResults(); };
      _query.KeyDown += QueryKeyDown;

      _results = new ListView {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        GridLines = false,
        HideSelection = false,
        MultiSelect = false,
        HeaderStyle = ColumnHeaderStyle.Nonclickable
      };
      _results.Columns.Add("Name", 265);
      _results.Columns.Add("Location / ID", 455);
      _results.DoubleClick += delegate { AcceptSelection(); };
      _results.KeyDown += ResultsKeyDown;

      _status = new Label {
        Dock = DockStyle.Bottom,
        Height = 24,
        Padding = new Padding(6, 3, 6, 0),
        TextAlign = ContentAlignment.MiddleLeft
      };

      Panel searchPanel = new Panel { Dock = DockStyle.Top, Height = 38, Padding = new Padding(6, 6, 6, 4) };
      _query.Dock = DockStyle.Fill;
      searchPanel.Controls.Add(_query);

      Controls.Add(_results);
      Controls.Add(_status);
      Controls.Add(searchPanel);

      Shown += delegate {
        _query.Focus();
        RebuildResults();
      };
      FormClosed += delegate {
        try { _searchCancellation?.Cancel(); _searchCancellation?.Dispose(); } catch { }
        _searchCancellation = null;
      };
    }

    internal QuickOpenDialog(String title, String placeholder, Func<String, CancellationToken, IEnumerable<Item>> searchProvider)
      : this(title, placeholder, null, searchProvider ?? throw new ArgumentNullException(nameof(searchProvider))) {
    }

    protected override Boolean ProcessCmdKey(ref Message msg, Keys keyData) {
      if (keyData == Keys.Escape) {
        DialogResult = DialogResult.Cancel;
        Close();
        return true;
      }
      return base.ProcessCmdKey(ref msg, keyData);
    }

    private void QueryKeyDown(Object sender, KeyEventArgs e) {
      if (e.KeyCode == Keys.Down || e.KeyCode == Keys.Up) {
        if (_results.Items.Count > 0) {
          Int32 index = _results.SelectedIndices.Count > 0 ? _results.SelectedIndices[0] : 0;
          index += e.KeyCode == Keys.Down ? 1 : -1;
          index = Math.Max(0, Math.Min(_results.Items.Count - 1, index));
          SelectResult(index);
        }
        e.Handled = true;
        e.SuppressKeyPress = true;
      } else if (e.KeyCode == Keys.Enter) {
        AcceptSelection();
        e.Handled = true;
        e.SuppressKeyPress = true;
      }
    }

    private void ResultsKeyDown(Object sender, KeyEventArgs e) {
      if (e.KeyCode == Keys.Enter) {
        AcceptSelection();
        e.Handled = true;
        e.SuppressKeyPress = true;
      }
    }

    private async void RebuildResults() {
      String query = _query.Text ?? String.Empty;
      String[] terms = query.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);

      if (_searchProvider != null) {
        CancellationTokenSource old = _searchCancellation;
        _searchCancellation = new CancellationTokenSource();
        try { old?.Cancel(); old?.Dispose(); } catch { }
        CancellationToken token = _searchCancellation.Token;
        Int32 generation = ++_searchGeneration;
        _status.Text = "Searching...";
        try {
          List<Item> providerItems = await Task.Run(() => {
            token.ThrowIfCancellationRequested();
            IEnumerable<Item> found = _searchProvider(query, token) ?? Enumerable.Empty<Item>();
            return found.Take(DisplayLimit + 1).ToList();
          }, token);
          if (token.IsCancellationRequested || generation != _searchGeneration || IsDisposed) return;
          ApplyVisibleResults(providerItems);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) {
          if (!IsDisposed && generation == _searchGeneration) _status.Text = "Search failed: " + ex.Message;
        }
        return;
      }

      IEnumerable<Item> filtered = _items;
      if (terms.Length != 0) {
        filtered = filtered.Where(item => terms.All(term =>
          (item.SearchText ?? String.Empty).IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0));
      }
      ApplyVisibleResults(filtered.Take(DisplayLimit + 1).ToList());
    }

    private void ApplyVisibleResults(List<Item> visible) {
      visible ??= new List<Item>();
      Boolean truncated = visible.Count > DisplayLimit;
      if (truncated) visible.RemoveAt(visible.Count - 1);

      _results.BeginUpdate();
      try {
        _results.Items.Clear();
        foreach (Item item in visible) {
          ListViewItem row = new ListViewItem(item.Primary) { Tag = item };
          row.SubItems.Add(item.Secondary);
          _results.Items.Add(row);
        }
      } finally { _results.EndUpdate(); }

      if (_results.Items.Count > 0) SelectResult(0);
      _status.Text = visible.Count.ToString("N0") + (truncated ? "+" : String.Empty)
        + " matches   •   Enter: open   Esc: cancel";
    }

    private void SelectResult(Int32 index) {
      if (index < 0 || index >= _results.Items.Count) return;
      if (_results.SelectedIndices.Count > 0) _results.Items[_results.SelectedIndices[0]].Selected = false;
      ListViewItem item = _results.Items[index];
      item.Selected = true;
      item.Focused = true;
      item.EnsureVisible();
    }

    private void AcceptSelection() {
      if (_results.SelectedItems.Count != 1 || _results.SelectedItems[0].Tag is not Item item) return;
      SelectedItem = item;
      DialogResult = DialogResult.OK;
      Close();
    }
  }
}
