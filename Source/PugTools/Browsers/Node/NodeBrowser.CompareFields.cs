using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using GomLib;

namespace PugTools {
  internal partial class NodeBrowser {
    private sealed class NodeFieldDiffRow {
      public string State;
      public string Path;
      public string Previous;
      public string Current;
    }

    private ToolStripMenuItem _compareFieldsMenuItem;
    private Form _fieldDiffForm;
    private Label _fieldDiffHeader;
    private TextBox _fieldDiffFilter;
    private DataGridView _fieldDiffGrid;
    private List<NodeFieldDiffRow> _fieldDiffRows = new List<NodeFieldDiffRow>();

    private void InitializeNodeFieldCompare() {
      if (!_compareNodes || contextMenuStrip1 == null) return;
      _compareFieldsMenuItem = new ToolStripMenuItem("Show field diff…") { Visible = false };
      _compareFieldsMenuItem.Click += (_, __) => ShowSelectedNodeFieldDiff();
      if (contextMenuStrip1.Items.Count > 0) contextMenuStrip1.Items.Add(new ToolStripSeparator());
      contextMenuStrip1.Items.Add(_compareFieldsMenuItem);
      contextMenuStrip1.Opening += (_, __) => UpdateNodeFieldCompareMenu();
    }

    private void UpdateNodeFieldCompareMenu() {
      if (_compareFieldsMenuItem == null) return;
      NodeAsset asset = treeViewFast1?.SelectedNode?.Tag as NodeAsset;
      bool visible = asset?.Obj != null && asset.compareState != BuildFileState.None;
      _compareFieldsMenuItem.Visible = visible;
      _compareFieldsMenuItem.Enabled = visible;
      if (visible) {
        _compareFieldsMenuItem.Text = asset.compareState == BuildFileState.Changed ? "Show field diff…" :
          asset.compareState == BuildFileState.New ? "Show added fields…" : "Show removed fields…";
      }
    }

    private void ShowSelectedNodeFieldDiff() {
      NodeAsset asset = treeViewFast1?.SelectedNode?.Tag as NodeAsset;
      if (asset?.Obj == null || asset.compareState == BuildFileState.None) return;
      string name = asset.Obj.Name ?? asset.displayName ?? asset.id;
      if (String.IsNullOrWhiteSpace(name)) return;

      GomObject current = null;
      GomObject previous = null;
      var errors = new List<string>();
      if (asset.compareState != BuildFileState.Removed && _currentDom != null) {
        try { current = _currentDom.GetObject(name); } catch (Exception ex) { errors.Add("Current: " + ex.Message); }
      }
      if (asset.compareState != BuildFileState.New && _previousDom != null) {
        try { previous = _previousDom.GetObject(name); } catch (Exception ex) { errors.Add("Previous: " + ex.Message); }
      }
      if (current == null && previous == null) {
        MessageBox.Show(this, "Neither version of " + name + " could be loaded." +
          (errors.Count == 0 ? String.Empty : "\r\n\r\n" + String.Join("\r\n", errors)),
          "Node field diff", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return;
      }

      Dictionary<string, string> currentFields = FlattenNodeFields(current);
      Dictionary<string, string> previousFields = FlattenNodeFields(previous);
      _fieldDiffRows = BuildNodeFieldDiffRows(previousFields, currentFields);
      EnsureNodeFieldDiffForm();

      int added = _fieldDiffRows.Count(x => x.State == "Added");
      int removed = _fieldDiffRows.Count(x => x.State == "Removed");
      int changed = _fieldDiffRows.Count(x => x.State == "Changed");
      _fieldDiffForm.Text = "Node field diff - " + name;
      _fieldDiffHeader.Text = name + "\r\n" + changed.ToString("N0") + " changed   •   " + added.ToString("N0") + " added   •   " + removed.ToString("N0") + " removed";
      _fieldDiffFilter.Text = String.Empty;
      RebuildNodeFieldDiffGrid();
      if (!_fieldDiffForm.Visible) _fieldDiffForm.Show(this); else _fieldDiffForm.BringToFront();
    }

    private static Dictionary<string, string> FlattenNodeFields(GomObject obj) {
      var result = new Dictionary<string, string>(StringComparer.Ordinal);
      if (obj == null) return result;
      result["@Id"] = obj.Id.ToString(CultureInfo.InvariantCulture);
      result["@InstanceType"] = obj.InstanceType.ToString(CultureInfo.InvariantCulture);
      result["@ObjectSizeInFile"] = obj.ObjectSizeInFile.ToString(CultureInfo.InvariantCulture);
      result["@NumGlommed"] = obj.NumGlommed.ToString(CultureInfo.InvariantCulture);
      result["@NumFields"] = obj.NumFields.ToString(CultureInfo.InvariantCulture);
      result["@Checksum"] = obj.Checksum.ToString(CultureInfo.InvariantCulture);
      if (obj.Data?.Dictionary == null) return result;
      foreach (KeyValuePair<string, object> field in obj.Data.Dictionary.OrderBy(x => x.Key, StringComparer.Ordinal)) {
        if (field.Key.StartsWith("Script_", StringComparison.Ordinal)) continue;
        FlattenNodeValue(field.Key, field.Value, result, 0);
      }
      return result;
    }

    private static void FlattenNodeValue(string path, object value, Dictionary<string, string> output, int depth) {
      if (output == null || String.IsNullOrEmpty(path)) return;
      if (depth > 48) { output[path] = "<depth limit>"; return; }
      if (value == null) { output[path] = "<null>"; return; }
      if (value is string || value is char || value is bool || value is decimal || value is DateTime || value is TimeSpan || value.GetType().IsPrimitive || value.GetType().IsEnum) {
        output[path] = FormatNodeFieldValue(value);
        return;
      }
      if (value is byte[] bytes) {
        int take = Math.Min(64, bytes.Length);
        output[path] = "byte[" + bytes.Length + "] " + BitConverter.ToString(bytes, 0, take).Replace("-", " ") + (take < bytes.Length ? " …" : String.Empty);
        return;
      }
      if (value is GomObjectData data) {
        if (data.Dictionary == null || data.Dictionary.Count == 0) { output[path] = "{}"; return; }
        foreach (KeyValuePair<string, object> item in data.Dictionary.OrderBy(x => x.Key, StringComparer.Ordinal)) {
          if (item.Key.StartsWith("Script_", StringComparison.Ordinal)) continue;
          FlattenNodeValue(path + "." + item.Key, item.Value, output, depth + 1);
        }
        return;
      }
      if (value is IDictionary dictionary) {
        if (dictionary.Count == 0) { output[path] = "{}"; return; }
        var entries = new List<DictionaryEntry>();
        foreach (DictionaryEntry entry in dictionary) entries.Add(entry);
        foreach (DictionaryEntry entry in entries.OrderBy(x => FormatNodeFieldKey(x.Key), StringComparer.Ordinal)) {
          string key = FormatNodeFieldKey(entry.Key);
          FlattenNodeValue(path + "[" + key + "]", entry.Value, output, depth + 1);
        }
        return;
      }
      if (value is IEnumerable enumerable) {
        int index = 0;
        bool any = false;
        foreach (object item in enumerable) {
          any = true;
          if (index >= 100000) { output[path + "[…]"] = "<item limit>"; break; }
          FlattenNodeValue(path + "[" + index.ToString(CultureInfo.InvariantCulture) + "]", item, output, depth + 1);
          index++;
        }
        if (!any) output[path] = "[]";
        return;
      }
      output[path] = FormatNodeFieldValue(value);
    }

    private static string FormatNodeFieldKey(object value) {
      if (value == null) return "<null>";
      string text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? String.Empty;
      return text.Replace("\\", "\\\\").Replace("]", "\\]").Replace("\r", "\\r").Replace("\n", "\\n");
    }

    private static string FormatNodeFieldValue(object value) {
      if (value == null) return "<null>";
      if (value is float f) return f.ToString("R", CultureInfo.InvariantCulture);
      if (value is double d) return d.ToString("R", CultureInfo.InvariantCulture);
      if (value is IFormattable formattable) {
        try { return NormalizeNodeFieldText(formattable.ToString(null, CultureInfo.InvariantCulture)); } catch { }
      }
      return NormalizeNodeFieldText(value.ToString());
    }

    private static string NormalizeNodeFieldText(string text) {
      return (text ?? String.Empty).Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
    }

    private static List<NodeFieldDiffRow> BuildNodeFieldDiffRows(Dictionary<string, string> previous, Dictionary<string, string> current) {
      previous ??= new Dictionary<string, string>(StringComparer.Ordinal);
      current ??= new Dictionary<string, string>(StringComparer.Ordinal);
      var keys = new SortedSet<string>(previous.Keys, StringComparer.Ordinal);
      keys.UnionWith(current.Keys);
      var rows = new List<NodeFieldDiffRow>();
      foreach (string key in keys) {
        bool hadPrevious = previous.TryGetValue(key, out string before);
        bool hasCurrent = current.TryGetValue(key, out string after);
        if (hadPrevious && hasCurrent && String.Equals(before, after, StringComparison.Ordinal)) continue;
        rows.Add(new NodeFieldDiffRow {
          State = !hadPrevious ? "Added" : !hasCurrent ? "Removed" : "Changed",
          Path = key,
          Previous = hadPrevious ? before : String.Empty,
          Current = hasCurrent ? after : String.Empty
        });
      }
      return rows;
    }

    private void EnsureNodeFieldDiffForm() {
      if (_fieldDiffForm != null && !_fieldDiffForm.IsDisposed) return;
      _fieldDiffForm = new Form {
        Text = "Node field diff",
        ShowInTaskbar = false,
        StartPosition = FormStartPosition.CenterParent,
        MinimumSize = new System.Drawing.Size(760, 430),
        Size = new System.Drawing.Size(1180, 720)
      };
      _fieldDiffHeader = new Label {
        Dock = DockStyle.Top,
        Height = 48,
        Padding = new Padding(8, 6, 8, 4),
        AutoEllipsis = true,
        Font = new System.Drawing.Font(Font, System.Drawing.FontStyle.Bold)
      };
      var filterPanel = new Panel { Dock = DockStyle.Top, Height = 36, Padding = new Padding(8, 5, 8, 4) };
      var filterLabel = new Label { Text = "Filter:", Dock = DockStyle.Left, Width = 44, TextAlign = System.Drawing.ContentAlignment.MiddleLeft };
      _fieldDiffFilter = new TextBox { Dock = DockStyle.Fill };
      _fieldDiffFilter.TextChanged += (_, __) => RebuildNodeFieldDiffGrid();
      filterPanel.Controls.Add(_fieldDiffFilter);
      filterPanel.Controls.Add(filterLabel);

      _fieldDiffGrid = new DataGridView {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        AllowUserToOrderColumns = true,
        AutoGenerateColumns = false,
        RowHeadersVisible = false,
        SelectionMode = DataGridViewSelectionMode.CellSelect,
        ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableAlwaysIncludeHeaderText,
        AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None
      };
      _fieldDiffGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "State", HeaderText = "State", Width = 80 });
      _fieldDiffGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Path", HeaderText = "Property path", Width = 340 });
      _fieldDiffGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Previous", HeaderText = "Previous", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 50 });
      _fieldDiffGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Current", HeaderText = "Current", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 50 });

      var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 42, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(7, 6, 7, 4), WrapContents = false };
      var close = new Button { Text = "Close", Width = 90, Height = 28 };
      var copy = new Button { Text = "Copy all", Width = 90, Height = 28 };
      close.Click += (_, __) => _fieldDiffForm.Hide();
      copy.Click += (_, __) => CopyNodeFieldDiff();
      buttons.Controls.Add(close);
      buttons.Controls.Add(copy);

      _fieldDiffForm.Controls.Add(_fieldDiffGrid);
      _fieldDiffForm.Controls.Add(buttons);
      _fieldDiffForm.Controls.Add(filterPanel);
      _fieldDiffForm.Controls.Add(_fieldDiffHeader);
      _fieldDiffForm.FormClosing += (_, e) => {
        if (_closing) return;
        e.Cancel = true;
        _fieldDiffForm.Hide();
      };
      _fieldDiffForm.FormClosed += (_, __) => {
        _fieldDiffForm = null; _fieldDiffHeader = null; _fieldDiffFilter = null; _fieldDiffGrid = null;
      };
    }

    private void RebuildNodeFieldDiffGrid() {
      if (_fieldDiffGrid == null || _fieldDiffRows == null) return;
      string filter = (_fieldDiffFilter?.Text ?? String.Empty).Trim();
      _fieldDiffGrid.SuspendLayout();
      try {
        _fieldDiffGrid.Rows.Clear();
        foreach (NodeFieldDiffRow row in _fieldDiffRows) {
          if (!String.IsNullOrEmpty(filter) &&
              (row.Path?.IndexOf(filter, StringComparison.CurrentCultureIgnoreCase) ?? -1) < 0 &&
              (row.Previous?.IndexOf(filter, StringComparison.CurrentCultureIgnoreCase) ?? -1) < 0 &&
              (row.Current?.IndexOf(filter, StringComparison.CurrentCultureIgnoreCase) ?? -1) < 0 &&
              (row.State?.IndexOf(filter, StringComparison.CurrentCultureIgnoreCase) ?? -1) < 0) continue;
          _fieldDiffGrid.Rows.Add(row.State, row.Path, row.Previous, row.Current);
        }
      } finally {
        _fieldDiffGrid.ResumeLayout();
      }
    }

    private void CopyNodeFieldDiff() {
      if (_fieldDiffRows == null || _fieldDiffRows.Count == 0) return;
      var sb = new StringBuilder();
      sb.AppendLine("State\tProperty path\tPrevious\tCurrent");
      foreach (NodeFieldDiffRow row in _fieldDiffRows) {
        sb.Append(EscapeNodeFieldDiffTsv(row.State)).Append('\t')
          .Append(EscapeNodeFieldDiffTsv(row.Path)).Append('\t')
          .Append(EscapeNodeFieldDiffTsv(row.Previous)).Append('\t')
          .Append(EscapeNodeFieldDiffTsv(row.Current)).AppendLine();
      }
      try { Clipboard.SetText(sb.ToString().TrimEnd()); StatusLabel1Text("Node field diff copied to clipboard."); }
      catch (Exception ex) { StatusLabel1Text("Could not copy node field diff: " + ex.Message); }
    }

    private static string EscapeNodeFieldDiffTsv(string value) {
      return (value ?? String.Empty).Replace("\t", " ").Replace("\r", " ").Replace("\n", " ");
    }
  }
}
