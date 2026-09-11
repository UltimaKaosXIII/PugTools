using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using TorArchive;

namespace PugTools {
  /// <summary>
  /// Database-free browser for the compiled Direct3D shader cache shipped beside SWTOR.
  /// It can open the retail shaders.bin directly or standalone D3D9/DXBC compiled shader objects.
  /// </summary>
  internal sealed class ShaderBrowser : Form {
    private readonly String _gamePath;
    private readonly Boolean _usePts;
    private ShaderBinaryReader.ShaderFileInfo _file;
    private List<ShaderBinaryReader.ShaderBlobInfo> _filtered = new List<ShaderBinaryReader.ShaderBlobInfo>();

    private readonly ToolStrip _toolStrip;
    private readonly ToolStripButton _openButton;
    private readonly ToolStripButton _exportButton;
    private readonly ToolStripButton _exportAssemblyButton;
    private readonly ToolStripLabel _searchLabel;
    private readonly ToolStripTextBox _searchBox;
    private readonly SplitContainer _split;
    private readonly ListView _shaderList;
    private readonly TreeView _details;
    private readonly TabControl _detailTabs;
    private readonly TextBox _assembly;
    private readonly StatusStrip _status;
    private readonly ToolStripStatusLabel _statusLabel;

    internal ShaderBrowser(String gamePath, Boolean usePts) {
      _gamePath = gamePath;
      _usePts = usePts;
      Text = "Shader Browser";
      StartPosition = FormStartPosition.CenterParent;
      Size = new Size(1120, 720);
      MinimumSize = new Size(760, 480);

      _toolStrip = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Dock = DockStyle.Top };
      _openButton = new ToolStripButton("Open...");
      _openButton.Click += async delegate { await ChooseAndOpenFile(); };
      _exportButton = new ToolStripButton("Export selected shader...") { Enabled = false };
      _exportButton.Click += delegate { ExportSelected(); };
      _exportAssemblyButton = new ToolStripButton("Export assembly...") { Enabled = false };
      _exportAssemblyButton.Click += delegate { ExportAssembly(); };
      _searchLabel = new ToolStripLabel("Filter:");
      _searchBox = new ToolStripTextBox { AutoSize = false, Width = 260, ToolTipText = "Profile, resource, constant buffer, creator or shader index" };
      _searchBox.TextChanged += delegate { ApplyFilter(); };
      _toolStrip.Items.Add(_openButton);
      _toolStrip.Items.Add(_exportButton);
      _toolStrip.Items.Add(_exportAssemblyButton);
      _toolStrip.Items.Add(new ToolStripSeparator());
      _toolStrip.Items.Add(_searchLabel);
      _toolStrip.Items.Add(_searchBox);

      _split = new SplitContainer {
        Dock = DockStyle.Fill,
        Orientation = Orientation.Vertical,
        SplitterDistance = 470
      };

      _shaderList = new ListView {
        Dock = DockStyle.Fill,
        View = View.Details,
        HeaderStyle = ColumnHeaderStyle.Clickable,
        FullRowSelect = true,
        GridLines = true,
        HideSelection = false,
        MultiSelect = false,
        Margin = Padding.Empty
      };
      _shaderList.Columns.Add("#", 58);
      _shaderList.Columns.Add("Format", 68);
      _shaderList.Columns.Add("Profile", 88);
      _shaderList.Columns.Add("Offset", 110);
      _shaderList.Columns.Add("Size", 90);
      _shaderList.Columns.Add("Resources", 76);
      _shaderList.SelectedIndexChanged += delegate { ShowSelectedDetails(); };
      _split.Panel1.Controls.Add(_shaderList);

      _details = new TreeView {
        Dock = DockStyle.Fill,
        HideSelection = false,
        Font = new Font(FontFamily.GenericMonospace, 9F)
      };
      _assembly = new TextBox {
        Dock = DockStyle.Fill,
        Multiline = true,
        ReadOnly = true,
        WordWrap = false,
        ScrollBars = ScrollBars.Both,
        Font = new Font(FontFamily.GenericMonospace, 9F),
        AcceptsTab = true
      };
      _detailTabs = new TabControl { Dock = DockStyle.Fill };
      TabPage reflectionPage = new TabPage("Reflection");
      reflectionPage.Controls.Add(_details);
      TabPage assemblyPage = new TabPage("Assembly");
      assemblyPage.Controls.Add(_assembly);
      _detailTabs.TabPages.Add(reflectionPage);
      _detailTabs.TabPages.Add(assemblyPage);
      _split.Panel2.Controls.Add(_detailTabs);

      _status = new StatusStrip {
        Dock = DockStyle.Fill,
        SizingGrip = false,
        Margin = Padding.Empty
      };
      _statusLabel = new ToolStripStatusLabel("Ready.");
      _status.Items.Add(_statusLabel);

      // Do not rely on WinForms' z-order dependent DockStyle.Fill layout here.
      // The old layout allowed the SplitContainer/ListView to extend underneath
      // the ToolStrip and StatusStrip, hiding the ListView column header and
      // making the toolbar look as if it were part of the content area.
      _toolStrip.Dock = DockStyle.Fill;
      _toolStrip.Margin = Padding.Empty;
      _split.Margin = Padding.Empty;

      TableLayoutPanel windowLayout = new TableLayoutPanel {
        Dock = DockStyle.Fill,
        ColumnCount = 1,
        RowCount = 3,
        Margin = Padding.Empty,
        Padding = Padding.Empty,
        CellBorderStyle = TableLayoutPanelCellBorderStyle.None
      };
      windowLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
      windowLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
      windowLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
      windowLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
      windowLayout.Controls.Add(_toolStrip, 0, 0);
      windowLayout.Controls.Add(_split, 0, 1);
      windowLayout.Controls.Add(_status, 0, 2);
      Controls.Add(windowLayout);

      Shown += async delegate { await OpenDefaultShaderCache(); };
    }

    protected override Boolean ProcessCmdKey(ref Message msg, Keys keyData) {
      if (keyData == (Keys.Control | Keys.F) || keyData == (Keys.Control | Keys.E)) {
        _searchBox.Focus();
        _searchBox.SelectAll();
        return true;
      }
      return base.ProcessCmdKey(ref msg, keyData);
    }

    private async Task OpenDefaultShaderCache() {
      String retail = Assets.GetRetailClientDirectory(_gamePath, _usePts);
      String path = String.IsNullOrWhiteSpace(retail) ? null : Path.Combine(retail, "shaders", "shaders.bin");
      if (!String.IsNullOrWhiteSpace(path) && System.IO.File.Exists(path)) {
        await LoadShaderFile(path);
        return;
      }
      _statusLabel.Text = "shaders.bin was not found beside the configured SWTOR client. Use Open... to select a file.";
    }

    private async Task ChooseAndOpenFile() {
      using OpenFileDialog dialog = new OpenFileDialog {
        Filter = "SWTOR shader cache / compiled shader (*.bin;*.fxo;*.vso;*.pso)|*.bin;*.fxo;*.vso;*.pso|All files (*.*)|*.*",
        FilterIndex = 1,
        CheckFileExists = true
      };
      String retail = Assets.GetRetailClientDirectory(_gamePath, _usePts);
      String shaderDir = String.IsNullOrWhiteSpace(retail) ? null : Path.Combine(retail, "shaders");
      if (!String.IsNullOrWhiteSpace(shaderDir) && Directory.Exists(shaderDir)) dialog.InitialDirectory = shaderDir;
      if (dialog.ShowDialog(this) != DialogResult.OK) return;
      await LoadShaderFile(dialog.FileName);
    }

    private async Task LoadShaderFile(String path) {
      try {
        _openButton.Enabled = false;
        _exportButton.Enabled = false;
        _exportAssemblyButton.Enabled = false;
        _shaderList.Items.Clear();
        _details.Nodes.Clear();
        _assembly.Clear();
        _statusLabel.Text = "Scanning compiled shaders...";
        UseWaitCursor = true;

        ShaderBinaryReader.ShaderFileInfo parsed = await Task.Run(() => ShaderBinaryReader.Parse(path));
        _file = parsed;
        Text = "Shader Browser - " + Path.GetFileName(path);
        ApplyFilter();
        Int32 d3d9 = parsed.Shaders.Count(x => x.Format == "D3D9");
        Int32 dxbc = parsed.Shaders.Count(x => x.Format == "DXBC");
        _statusLabel.Text = parsed.Shaders.Count.ToString("N0", CultureInfo.InvariantCulture)
          + " compiled shaders found (" + d3d9.ToString("N0", CultureInfo.InvariantCulture) + " D3D9, "
          + dxbc.ToString("N0", CultureInfo.InvariantCulture) + " DXBC) in " + Path.GetFileName(path)
          + " (" + FormatBytes(parsed.Bytes?.LongLength ?? 0) + ").";
        if (parsed.Shaders.Count == 0)
          _statusLabel.Text += " Signatures: " + parsed.D3D9VersionCandidateCount.ToString("N0", CultureInfo.InvariantCulture)
            + " D3D9 candidates, " + parsed.DxbcSignatureCount.ToString("N0", CultureInfo.InvariantCulture) + " DXBC.";
      }
      catch (Exception ex) {
        _file = null;
        _filtered.Clear();
        _shaderList.Items.Clear();
        _details.Nodes.Clear();
        _assembly.Clear();
        _statusLabel.Text = "Shader parse failed.";
        MessageBox.Show(this, ex.Message, "Shader Browser", MessageBoxButtons.OK, MessageBoxIcon.Error);
      }
      finally {
        UseWaitCursor = false;
        _openButton.Enabled = true;
      }
    }

    private void ApplyFilter() {
      if (_file == null) return;
      String query = (_searchBox.Text ?? String.Empty).Trim();
      IEnumerable<ShaderBinaryReader.ShaderBlobInfo> shaders = _file.Shaders;
      if (!String.IsNullOrWhiteSpace(query)) {
        String[] terms = query.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        shaders = shaders.Where(shader => terms.All(term => ShaderMatches(shader, term)));
      }
      _filtered = shaders.ToList();

      _shaderList.BeginUpdate();
      try {
        _shaderList.Items.Clear();
        foreach (ShaderBinaryReader.ShaderBlobInfo shader in _filtered) {
          ListViewItem item = new ListViewItem(shader.Index.ToString(CultureInfo.InvariantCulture)) { Tag = shader };
          item.SubItems.Add(shader.Format);
          item.SubItems.Add(shader.Profile);
          item.SubItems.Add("0x" + shader.Offset.ToString("X", CultureInfo.InvariantCulture));
          item.SubItems.Add(FormatBytes(shader.Length));
          item.SubItems.Add(shader.Resources.Count.ToString(CultureInfo.InvariantCulture));
          _shaderList.Items.Add(item);
        }
      }
      finally { _shaderList.EndUpdate(); }
      _details.Nodes.Clear();
      _assembly.Clear();
      _exportButton.Enabled = false;
      _exportAssemblyButton.Enabled = false;
      if (_filtered.Count > 0 && !String.IsNullOrWhiteSpace(query))
        _statusLabel.Text = _filtered.Count.ToString("N0", CultureInfo.InvariantCulture) + " matching shaders.";
    }

    private static Boolean ShaderMatches(ShaderBinaryReader.ShaderBlobInfo shader, String term) {
      if (shader == null || String.IsNullOrWhiteSpace(term)) return true;
      String q = term.Trim();
      if (shader.Index.ToString(CultureInfo.InvariantCulture).Equals(q, StringComparison.OrdinalIgnoreCase)) return true;
      if ((shader.Format ?? String.Empty).IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) return true;
      if ((shader.Profile ?? String.Empty).IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) return true;
      if ((shader.Creator ?? String.Empty).IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) return true;
      if (shader.Resources.Any(x => (x.Name ?? String.Empty).IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)) return true;
      if (shader.ConstantBuffers.Any(x => (x.Name ?? String.Empty).IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
          || x.Variables.Any(v => (v.Name ?? String.Empty).IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0))) return true;
      if (shader.Inputs.Any(x => (x.Semantic ?? String.Empty).IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)) return true;
      if (shader.Outputs.Any(x => (x.Semantic ?? String.Empty).IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)) return true;
      return false;
    }

    private void ShowSelectedDetails() {
      _details.BeginUpdate();
      try {
        _details.Nodes.Clear();
        _assembly.Clear();
        ShaderBinaryReader.ShaderBlobInfo shader = SelectedShader;
        _exportButton.Enabled = shader != null;
        _exportAssemblyButton.Enabled = shader != null && String.Equals(shader.Format, "D3D9", StringComparison.OrdinalIgnoreCase);
        if (shader == null) return;
        _assembly.Text = ShaderBinaryReader.Disassemble(_file, shader);

        TreeNode summary = new TreeNode(shader.Profile + "  shader #" + shader.Index.ToString(CultureInfo.InvariantCulture));
        summary.Nodes.Add("Format: " + shader.Format);
        summary.Nodes.Add("Offset: 0x" + shader.Offset.ToString("X", CultureInfo.InvariantCulture));
        summary.Nodes.Add("Length: " + FormatBytes(shader.Length) + " (" + shader.Length.ToString("N0", CultureInfo.InvariantCulture) + " bytes)");
        summary.Nodes.Add((shader.Format == "DXBC" ? "DXBC hash: " : "MD5: ") + shader.Hash);
        if (!String.IsNullOrWhiteSpace(shader.Creator)) summary.Nodes.Add("Creator: " + shader.Creator);
        _details.Nodes.Add(summary);

        TreeNode chunks = new TreeNode((shader.Format == "DXBC" ? "DXBC chunks" : "Embedded comments")
          + " (" + shader.Chunks.Count.ToString(CultureInfo.InvariantCulture) + ")");
        foreach (ShaderBinaryReader.ShaderChunkInfo chunk in shader.Chunks)
          chunks.Nodes.Add(chunk.FourCC + "  +0x" + chunk.Offset.ToString("X", CultureInfo.InvariantCulture)
            + "  " + FormatBytes(chunk.Length));
        _details.Nodes.Add(chunks);

        TreeNode resources = new TreeNode("Bound resources (" + shader.Resources.Count.ToString(CultureInfo.InvariantCulture) + ")");
        foreach (ShaderBinaryReader.ShaderResourceInfo resource in shader.Resources) {
          TreeNode row = new TreeNode(resource.Name + "  [" + ShaderBinaryReader.ResourceTypeName(resource.Type) + "]");
          row.Nodes.Add("Bind: " + resource.BindPoint.ToString(CultureInfo.InvariantCulture)
            + (resource.BindCount > 1 ? " .. " + (resource.BindPoint + resource.BindCount - 1).ToString(CultureInfo.InvariantCulture) : String.Empty));
          row.Nodes.Add("Dimension: " + ShaderBinaryReader.DimensionName(resource.Dimension));
          row.Nodes.Add("Return type: " + resource.ReturnType.ToString(CultureInfo.InvariantCulture));
          row.Nodes.Add("Samples: " + resource.Samples.ToString(CultureInfo.InvariantCulture));
          row.Nodes.Add("Flags: 0x" + resource.Flags.ToString("X", CultureInfo.InvariantCulture));
          resources.Nodes.Add(row);
        }
        _details.Nodes.Add(resources);

        TreeNode cbuffers = new TreeNode("Constant buffers (" + shader.ConstantBuffers.Count.ToString(CultureInfo.InvariantCulture) + ")");
        foreach (ShaderBinaryReader.ShaderConstantBufferInfo buffer in shader.ConstantBuffers) {
          TreeNode cb = new TreeNode(buffer.Name + "  " + buffer.Size.ToString(CultureInfo.InvariantCulture) + " bytes");
          foreach (ShaderBinaryReader.ShaderVariableInfo variable in buffer.Variables) {
            if (shader.Format == "D3D9" && !String.IsNullOrWhiteSpace(variable.RegisterSet)) {
              String register = variable.RegisterSet + variable.RegisterIndex.ToString(CultureInfo.InvariantCulture);
              if (variable.RegisterCount > 1)
                register += ".." + variable.RegisterSet + (variable.RegisterIndex + variable.RegisterCount - 1).ToString(CultureInfo.InvariantCulture);
              cb.Nodes.Add(variable.Name + "  " + register
                + (String.IsNullOrWhiteSpace(variable.TypeName) ? String.Empty : "  " + variable.TypeName));
            } else {
              cb.Nodes.Add(variable.Name + "  +" + variable.StartOffset.ToString(CultureInfo.InvariantCulture)
                + "  " + variable.Size.ToString(CultureInfo.InvariantCulture) + " bytes");
            }
          }
          cbuffers.Nodes.Add(cb);
        }
        _details.Nodes.Add(cbuffers);

        _details.Nodes.Add(BuildSignatureNode("Inputs", shader.Inputs));
        _details.Nodes.Add(BuildSignatureNode("Outputs", shader.Outputs));
        summary.Expand();
        resources.Expand();
        cbuffers.Expand();
      }
      finally { _details.EndUpdate(); }
    }

    private static TreeNode BuildSignatureNode(String title, IList<ShaderBinaryReader.ShaderSignatureInfo> values) {
      TreeNode root = new TreeNode(title + " (" + (values?.Count ?? 0).ToString(CultureInfo.InvariantCulture) + ")");
      if (values == null) return root;
      foreach (ShaderBinaryReader.ShaderSignatureInfo item in values) {
        String semantic = item.Semantic + (item.SemanticIndex == 0 ? String.Empty : item.SemanticIndex.ToString(CultureInfo.InvariantCulture));
        root.Nodes.Add(semantic + "  r" + item.Register.ToString(CultureInfo.InvariantCulture)
          + "  mask 0x" + item.Mask.ToString("X2", CultureInfo.InvariantCulture));
      }
      return root;
    }

    private void ExportAssembly() {
      ShaderBinaryReader.ShaderBlobInfo shader = SelectedShader;
      if (_file == null || shader == null || !String.Equals(shader.Format, "D3D9", StringComparison.OrdinalIgnoreCase)) return;
      String assembly = ShaderBinaryReader.Disassemble(_file, shader);
      using SaveFileDialog dialog = new SaveFileDialog {
        AddExtension = true,
        DefaultExt = "asm",
        FileName = "shader_" + shader.Index.ToString("D5", CultureInfo.InvariantCulture) + "_" + shader.Profile + ".asm",
        Filter = "Shader assembly (*.asm;*.txt)|*.asm;*.txt|Text files (*.txt)|*.txt|All files (*.*)|*.*"
      };
      if (dialog.ShowDialog(this) != DialogResult.OK) return;
      try {
        System.IO.File.WriteAllText(dialog.FileName, assembly ?? String.Empty);
        _statusLabel.Text = "Assembly exported to " + Path.GetFileName(dialog.FileName) + ".";
      }
      catch (Exception ex) {
        MessageBox.Show(this, ex.Message, "Shader Browser", MessageBoxButtons.OK, MessageBoxIcon.Error);
      }
    }

    private ShaderBinaryReader.ShaderBlobInfo SelectedShader =>
      _shaderList.SelectedItems.Count == 1 ? _shaderList.SelectedItems[0].Tag as ShaderBinaryReader.ShaderBlobInfo : null;

    private void ExportSelected() {
      ShaderBinaryReader.ShaderBlobInfo shader = SelectedShader;
      if (_file == null || shader == null) return;
      Boolean d3d9 = shader.Format == "D3D9";
      String ext = d3d9 ? (shader.Profile.StartsWith("vs_", StringComparison.OrdinalIgnoreCase) ? "vso" : "pso") : "fxo";
      using SaveFileDialog dialog = new SaveFileDialog {
        AddExtension = true,
        DefaultExt = ext,
        FileName = "shader_" + shader.Index.ToString("D5", CultureInfo.InvariantCulture) + "_" + shader.Profile + "." + ext,
        Filter = d3d9 ? "Direct3D 9 compiled shader (*." + ext + ")|*." + ext + "|All files (*.*)|*.*"
          : "Compiled shader (*.fxo)|*.fxo|All files (*.*)|*.*"
      };
      if (dialog.ShowDialog(this) != DialogResult.OK) return;
      try {
        System.IO.File.WriteAllBytes(dialog.FileName, ShaderBinaryReader.GetShaderBytes(_file, shader));
        _statusLabel.Text = "Exported shader #" + shader.Index.ToString(CultureInfo.InvariantCulture) + ".";
      }
      catch (Exception ex) {
        MessageBox.Show(this, ex.Message, "Shader Export", MessageBoxButtons.OK, MessageBoxIcon.Error);
      }
    }

    private static String FormatBytes(Int64 bytes) {
      if (bytes < 1024) return bytes.ToString(CultureInfo.InvariantCulture) + " B";
      if (bytes < 1024L * 1024L) return (bytes / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " KB";
      return (bytes / (1024.0 * 1024.0)).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
    }
  }
}
