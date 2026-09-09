using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml;

namespace PugTools {
  /// <summary>
  /// Database-free Jedipedia-style inspector for SWTOR MAT/TEX/EMT assets.
  /// The parser intentionally stays independent from the renderer, while following the same
  /// resource-path/fallback rules used by the world GR2 material runtime.
  /// </summary>
  internal sealed class MaterialSpecPreviewControl : UserControl {
    private readonly ToolStrip _toolbar;
    private readonly ToolStripLabel _kindLabel;
    private readonly ToolStripLabel _summaryLabel;
    private readonly SplitContainer _split;
    private readonly DataGridView _grid;
    private readonly TabControl _details;
    private readonly DdsPreviewControl _materialPreview;
    private readonly Label _materialPreviewLabel;
    private readonly SplitContainer _textureSplit;
    private readonly ListView _textures;
    private readonly DdsPreviewControl _texturePreview;
    private readonly ToolStrip _textureTools;
    private readonly ToolStripComboBox _channelBox;
    private readonly Label _textureLabel;
    private readonly ListView _references;
    private readonly TextBox _raw;

    private readonly List<MaterialRow> _rows = new List<MaterialRow>();
    private readonly List<MaterialTexture> _materialTextures = new List<MaterialTexture>();
    private readonly List<MaterialReference> _allReferences = new List<MaterialReference>();
    private CancellationTokenSource _textureCts;
    private CancellationTokenSource _materialCts;
    private Int32 _textureGeneration;
    private Int32 _materialGeneration;
    private Bitmap _textureSourceBitmap;
    private String _format;
    private String _sourcePath;

    public Func<String, Bitmap> TextureLoader { get; set; }
    public Func<String, Boolean> ResourceExists { get; set; }
    public Action<String> OpenResourceRequested { get; set; }

    private static readonly Dictionary<String, String> MatDescriptions = new Dictionary<String, String>(StringComparer.OrdinalIgnoreCase) {
      { "Derived", "Shader used to render this material" },
      { "PolyType", "Collision/surface polygon behavior" },
      { "Visibility", "Whether the material is visible in-game" },
      { "GeometryType", "Default or Decal; decals are depth-offset to reduce Z-fighting" },
      { "AlphaMode", "Transparency / alpha-test mode" },
      { "AlphaTestValue", "Alpha threshold below which pixels are discarded" },
      { "IsTwoSided", "Disables back-face culling when enabled" },
      { "AudioMat", "Footstep / surface audio material" },
      { "DefaultPath", "Authoring-time default resource path" },
      { "DefaultTextureObject", "Authoring-time default texture object" },
      { "Alias", "Authoring alias" }
    };

    private static readonly Dictionary<String, String> TexDescriptions = new Dictionary<String, String>(StringComparer.OrdinalIgnoreCase) {
      { "MagFilter", "Magnification filter (Anisotropic, Linear, Point)" },
      { "MinFilter", "Minification filter (Anisotropic, Linear, Point)" },
      { "MipMapFilter", "Mipmap filter used during minification" },
      { "MipFilter", "Legacy typo; normally named MipMapFilter" },
      { "UAddress", "Texture address mode for the U coordinate" },
      { "VAddress", "Texture address mode for the V coordinate" },
      { "WAddress", "Texture address mode for the W coordinate" },
      { "LODBias", "Bias toward a sharper or blurrier mip level" },
      { "MaxMipMap", "Largest mip level allowed; usually 0" },
      { "Compression", "DDS encoding class (BumpMap, Color, ColorWithAlpha, RotationMap, etc.)" },
      { "Type", "Texture object type; SWTOR image textures normally use Image" }
    };

    public MaterialSpecPreviewControl() {
      Dock = DockStyle.Fill;
      BackColor = SystemColors.Window;

      _toolbar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Dock = DockStyle.Top };
      _kindLabel = new ToolStripLabel { Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold) };
      _summaryLabel = new ToolStripLabel();
      _toolbar.Items.Add(_kindLabel);
      _toolbar.Items.Add(new ToolStripSeparator());
      _toolbar.Items.Add(_summaryLabel);

      _split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical };
      _grid = new DataGridView {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false,
        AutoGenerateColumns = false,
        RowHeadersVisible = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        MultiSelect = false,
        BackgroundColor = SystemColors.Window,
        BorderStyle = BorderStyle.None
      };
      _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Section", DataPropertyName = nameof(MaterialRow.Section), Width = 90 });
      _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Name", DataPropertyName = nameof(MaterialRow.Name), Width = 205 });
      _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Type", DataPropertyName = nameof(MaterialRow.Type), Width = 90 });
      _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Value", DataPropertyName = nameof(MaterialRow.Value), AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
      _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Description", DataPropertyName = nameof(MaterialRow.Description), Width = 260 });
      _grid.SelectionChanged += GridSelectionChanged;
      _grid.CellDoubleClick += GridCellDoubleClick;
      _split.Panel1.Controls.Add(_grid);

      _details = new TabControl { Dock = DockStyle.Fill };

      var materialPage = new TabPage("Material preview");
      _materialPreview = new DdsPreviewControl { Dock = DockStyle.Fill, Checkerboard = false, BackColor = Color.FromArgb(30, 30, 30) };
      _materialPreviewLabel = new Label {
        Dock = DockStyle.Top,
        Height = 42,
        Padding = new Padding(6, 0, 6, 0),
        TextAlign = ContentAlignment.MiddleLeft,
        AutoEllipsis = true
      };
      materialPage.Controls.Add(_materialPreview);
      materialPage.Controls.Add(_materialPreviewLabel);

      var texturePage = new TabPage("Textures");
      _textureSplit = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
      _textures = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = true, HideSelection = false };
      _textures.Columns.Add("Semantic", 150);
      _textures.Columns.Add("Resolved DDS", 420);
      _textures.Columns.Add("Source", 130);
      _textures.Columns.Add("Status", 85);
      _textures.SelectedIndexChanged += TextureSelectionChanged;
      _textures.DoubleClick += delegate { OpenSelectedTexture(); };
      _textures.KeyDown += TextureListKeyDown;
      _textureSplit.Panel1.Controls.Add(_textures);
      _texturePreview = new DdsPreviewControl { Dock = DockStyle.Fill, Checkerboard = true, BackColor = Color.White };
      _textureTools = new ToolStrip { Dock = DockStyle.Top, GripStyle = ToolStripGripStyle.Hidden };
      _textureTools.Items.Add(new ToolStripLabel("View:"));
      _channelBox = new ToolStripComboBox { DropDownStyle = ComboBoxStyle.DropDownList, AutoSize = false, Width = 160 };
      _channelBox.Items.AddRange(new Object[] { "RGBA", "RGB", "Red", "Green", "Blue", "Alpha", "Rotation normal (A/G)", "Emissive (B)", "Inverse alpha (1-R)" });
      _channelBox.SelectedIndex = 0;
      _channelBox.SelectedIndexChanged += delegate { ApplyTextureChannel(); };
      _textureTools.Items.Add(_channelBox);
      _textureLabel = new Label {
        Dock = DockStyle.Top,
        Height = 38,
        Padding = new Padding(6, 0, 6, 0),
        TextAlign = ContentAlignment.MiddleLeft,
        AutoEllipsis = true
      };
      _textureSplit.Panel2.Controls.Add(_texturePreview);
      _textureSplit.Panel2.Controls.Add(_textureLabel);
      _textureSplit.Panel2.Controls.Add(_textureTools);
      texturePage.Controls.Add(_textureSplit);

      var referencesPage = new TabPage("References");
      _references = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = true, HideSelection = false };
      _references.Columns.Add("Type", 70);
      _references.Columns.Add("Path", 520);
      _references.Columns.Add("From", 180);
      _references.Columns.Add("Status", 85);
      _references.DoubleClick += delegate { OpenSelectedReference(); };
      _references.KeyDown += ReferenceListKeyDown;
      referencesPage.Controls.Add(_references);

      var rawPage = new TabPage("Raw");
      _raw = new TextBox {
        Dock = DockStyle.Fill,
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Both,
        WordWrap = false,
        Font = new Font(FontFamily.GenericMonospace, 9f)
      };
      rawPage.Controls.Add(_raw);

      _details.TabPages.Add(materialPage);
      _details.TabPages.Add(texturePage);
      _details.TabPages.Add(referencesPage);
      _details.TabPages.Add(rawPage);
      _split.Panel2.Controls.Add(_details);

      Controls.Add(_split);
      Controls.Add(_toolbar);

      SplitContainerSafeLayout.ApplyOnLoad(this, _split, 720);
      SplitContainerSafeLayout.ApplyOnLoad(this, _textureSplit, 190);
    }

    public void ClearPreview() {
      CancelTextureLoad();
      CancelMaterialLoad();
      _rows.Clear();
      _materialTextures.Clear();
      _allReferences.Clear();
      _grid.DataSource = null;
      _textures.Items.Clear();
      _references.Items.Clear();
      _texturePreview.ClearPreview();
      ReplaceTextureSource(null);
      _materialPreview.ClearPreview();
      _textureLabel.Text = String.Empty;
      _materialPreviewLabel.Text = String.Empty;
      _raw.Clear();
      _kindLabel.Text = String.Empty;
      _summaryLabel.Text = String.Empty;
      _format = null;
      _sourcePath = null;
    }

    public void LoadMat(String sourcePath, String text) {
      ClearPreview();
      _format = "MAT";
      _sourcePath = NormalizeResourcePath(sourcePath);
      _raw.Text = text ?? String.Empty;
      _kindLabel.Text = "MAT material";

      try {
        XmlDocument doc = LoadXml(text);
        XmlElement root = doc.DocumentElement ?? throw new InvalidDataException("MAT has no root element.");
        foreach (XmlNode node in root.ChildNodes) {
          if (node.NodeType != XmlNodeType.Element) continue;
          if (!node.Name.Equals("input", StringComparison.OrdinalIgnoreCase)) {
            String value = node.InnerText?.Trim() ?? String.Empty;
            if ((node.Name.Equals("DefaultPath", StringComparison.OrdinalIgnoreCase)
                 || node.Name.Equals("DefaultTextureObject", StringComparison.OrdinalIgnoreCase)
                 || node.Name.Equals("Alias", StringComparison.OrdinalIgnoreCase)) && value.Length == 0) continue;
            _rows.Add(new MaterialRow("Parameter", node.Name, String.Empty, value, DescriptionForMat(node.Name), null));
            continue;
          }

          String semantic = ChildText(node, "semantic");
          String type = ChildText(node, "type");
          String value2 = ChildText(node, "value");
          String variable = ChildText(node, "variable");
          String displayValue = value2;
          if (!String.IsNullOrWhiteSpace(variable) && !ValuesEqual(value2, variable)) displayValue += "   [variable: " + variable + "]";
          MaterialRow row = new MaterialRow("Input", semantic, type, displayValue, DescribeInput(semantic, type), null);
          _rows.Add(row);

          if (type.Equals("texture", StringComparison.OrdinalIgnoreCase)) {
            MaterialTexture texture = BuildMatTexture(semantic, value2, variable);
            row.Texture = texture;
            if (texture != null) {
              _materialTextures.Add(texture);
              AddTextureReferences(texture);
            }
          }
        }
        _summaryLabel.Text = "Shader: " + (FindValue("Derived") ?? "(unknown)")
          + "   Inputs: " + _rows.Count(r => r.Section == "Input").ToString("n0")
          + "   Textures: " + _materialTextures.Count.ToString("n0");
      }
      catch (Exception ex) {
        _summaryLabel.Text = "Parse failed: " + ex.Message;
        _rows.Add(new MaterialRow("Error", "Parse error", String.Empty, ex.Message, String.Empty, null));
      }
      FinalizeLoad();
      BeginMaterialPreview();
    }

    public void LoadTex(String sourcePath, String text) {
      ClearPreview();
      _format = "TEX";
      _sourcePath = NormalizeResourcePath(sourcePath);
      _raw.Text = text ?? String.Empty;
      _kindLabel.Text = "TEX texture object";
      try {
        XmlDocument doc = LoadXml(FixEscapedXmlDeclaration(text));
        XmlElement root = doc.DocumentElement ?? throw new InvalidDataException("TEX has no root element.");
        foreach (XmlNode node in root.ChildNodes) {
          if (node.NodeType != XmlNodeType.Element) continue;
          String value = node.InnerText?.Trim() ?? String.Empty;
          _rows.Add(new MaterialRow("Parameter", node.Name, String.Empty, value, TexDescriptions.TryGetValue(node.Name, out String d) ? d : String.Empty, null));
        }

        String basePath = RemoveExtension(_sourcePath);
        MaterialTexture texture = new MaterialTexture("Image", basePath + ".dds", basePath + ".tiny.dds", _sourcePath, "TEX sibling");
        texture.ResolvedDds = ResolveExisting(texture.DdsPath, texture.TinyDdsPath) ?? texture.DdsPath;
        _materialTextures.Add(texture);
        AddTextureReferences(texture);
        _summaryLabel.Text = "Parameters: " + _rows.Count.ToString("n0")
          + "   Compression: " + (FindValue("Compression") ?? "(unknown)")
          + "   Address: " + String.Join("/", new[] { FindValue("UAddress"), FindValue("VAddress") }.Where(x => !String.IsNullOrWhiteSpace(x)));
      }
      catch (Exception ex) {
        _summaryLabel.Text = "Parse failed: " + ex.Message;
        _rows.Add(new MaterialRow("Error", "Parse error", String.Empty, ex.Message, String.Empty, null));
      }
      FinalizeLoad();
    }

    public void LoadEmt(String sourcePath, String text) {
      ClearPreview();
      _format = "EMT";
      _sourcePath = NormalizeResourcePath(sourcePath);
      _raw.Text = text ?? String.Empty;
      _kindLabel.Text = "EMT environment material";
      try {
        XmlDocument doc = LoadXml(text);
        XmlElement root = doc.DocumentElement ?? throw new InvalidDataException("EMT has no root element.");
        foreach (XmlNode node in root.ChildNodes) {
          if (node.NodeType != XmlNodeType.Element) continue;
          String value = node.InnerText?.Trim() ?? String.Empty;
          MaterialRow row = new MaterialRow("Parameter", node.Name, InferValueType(value), PrettyVector(value), DescribeEmt(node.Name), null);
          _rows.Add(row);
          if (node.Name.Equals("BlendDiffuse", StringComparison.OrdinalIgnoreCase)
              || node.Name.Equals("BlendNormal", StringComparison.OrdinalIgnoreCase)) {
            String texPath = NormalizeResourcePath(value);
            if (!texPath.EndsWith(".tex", StringComparison.OrdinalIgnoreCase)) texPath += ".tex";
            String basePath = RemoveExtension(texPath);
            MaterialTexture texture = new MaterialTexture(node.Name, basePath + ".dds", basePath + ".tiny.dds", texPath, "EMT parameter");
            texture.ResolvedDds = ResolveExisting(texture.DdsPath, texture.TinyDdsPath) ?? texture.DdsPath;
            row.Texture = texture;
            _materialTextures.Add(texture);
            AddTextureReferences(texture);
          }
        }
        _summaryLabel.Text = "Parameters: " + _rows.Count.ToString("n0") + "   Blend maps: " + _materialTextures.Count.ToString("n0");
      }
      catch (Exception ex) {
        _summaryLabel.Text = "Parse failed: " + ex.Message;
        _rows.Add(new MaterialRow("Error", "Parse error", String.Empty, ex.Message, String.Empty, null));
      }
      FinalizeLoad();
    }

    private void FinalizeLoad() {
      DeduplicateReferences();
      _grid.DataSource = _rows.ToList();
      RebuildTextures();
      RebuildReferences();
      if (_grid.Rows.Count > 0) {
        _grid.ClearSelection();
        _grid.Rows[0].Selected = true;
        _grid.CurrentCell = _grid.Rows[0].Cells[0];
      }
      if (_textures.Items.Count > 0) _textures.Items[0].Selected = true;
    }

    private void RebuildTextures() {
      _textures.BeginUpdate();
      try {
        _textures.Items.Clear();
        foreach (MaterialTexture texture in _materialTextures) {
          String resolved = texture.ResolvedDds ?? texture.DdsPath;
          Boolean exists = Exists(resolved);
          ListViewItem item = new ListViewItem(texture.Semantic ?? String.Empty) { Tag = texture };
          item.SubItems.Add(resolved ?? String.Empty);
          item.SubItems.Add(texture.SourceKind ?? String.Empty);
          item.SubItems.Add(exists ? "present" : "missing");
          if (!exists) item.ForeColor = Color.Firebrick;
          _textures.Items.Add(item);
        }
      }
      finally { _textures.EndUpdate(); }
    }

    private void RebuildReferences() {
      _references.BeginUpdate();
      try {
        _references.Items.Clear();
        foreach (MaterialReference reference in _allReferences) {
          Boolean exists = Exists(reference.Path);
          ListViewItem item = new ListViewItem(reference.Type) { Tag = reference };
          item.SubItems.Add(reference.Path);
          item.SubItems.Add(reference.Source);
          item.SubItems.Add(exists ? "present" : "missing");
          if (!exists) item.ForeColor = Color.Firebrick;
          _references.Items.Add(item);
        }
      }
      finally { _references.EndUpdate(); }
    }

    private MaterialTexture BuildMatTexture(String semantic, String value, String variable) {
      String authoredBase = NormalizeTextureBase(value);
      String variableBase = NormalizeTextureBase(variable);
      String resolvedBase = FindExistingTextureBase(authoredBase);
      String resolution = "value";
      if (resolvedBase == null) {
        resolvedBase = FindExistingTextureBase(variableBase);
        if (resolvedBase != null) resolution = "variable fallback";
      }
      if (resolvedBase == null) resolvedBase = ExpandTextureBases(authoredBase).FirstOrDefault() ?? ExpandTextureBases(variableBase).FirstOrDefault();
      if (String.IsNullOrWhiteSpace(resolvedBase)) return null;
      if (!String.Equals(resolvedBase, authoredBase, StringComparison.OrdinalIgnoreCase)
          && resolution == "value") resolution = "placeholder resolved";

      String dds = resolvedBase + ".dds";
      String tiny = resolvedBase + ".tiny.dds";
      String tex = resolvedBase + ".tex";
      MaterialTexture texture = new MaterialTexture(semantic, dds, tiny, tex, resolution);
      texture.ResolvedDds = ResolveExisting(dds, tiny) ?? dds;
      return texture;
    }

    private String FindExistingTextureBase(String basePath) {
      foreach (String candidate in ExpandTextureBases(basePath)) {
        if (Exists(candidate + ".dds") || Exists(candidate + ".tiny.dds") || Exists(candidate + ".tex")) return candidate;
      }
      return null;
    }

    private static IEnumerable<String> ExpandTextureBases(String basePath) {
      if (String.IsNullOrWhiteSpace(basePath)) yield break;
      IEnumerable<String> current = new[] { basePath };
      if (basePath.IndexOf("[gen]", StringComparison.OrdinalIgnoreCase) >= 0) {
        current = current.SelectMany(path => new[] { "f", "m", "u" }.Select(gen => ReplaceIgnoreCase(path, "[gen]", gen)));
      }
      if (basePath.IndexOf("[bt]", StringComparison.OrdinalIgnoreCase) >= 0) {
        String[] bodyTypes = { "bfa", "bfb", "bfn", "bfs", "bma", "bmf", "bmn", "bms" };
        current = current.SelectMany(path => bodyTypes.Select(bt => ReplaceIgnoreCase(path, "[bt]", bt)));
      }
      foreach (String candidate in current.Distinct(StringComparer.OrdinalIgnoreCase)) yield return candidate;
    }

    private static String ReplaceIgnoreCase(String text, String token, String value) {
      Int32 index = text.IndexOf(token, StringComparison.OrdinalIgnoreCase);
      if (index < 0) return text;
      return text.Substring(0, index) + value + text.Substring(index + token.Length);
    }

    private void AddTextureReferences(MaterialTexture texture) {
      if (texture == null) return;
      AddReference("DDS", texture.DdsPath, texture.Semantic);
      AddReference("DDS", texture.TinyDdsPath, texture.Semantic + " (tiny)");
      AddReference("TEX", texture.TexPath, texture.Semantic);
    }

    private void AddReference(String type, String path, String source) {
      if (String.IsNullOrWhiteSpace(path)) return;
      _allReferences.Add(new MaterialReference(type, NormalizeResourcePath(path), source ?? String.Empty));
    }

    private void DeduplicateReferences() {
      var seen = new HashSet<String>(StringComparer.OrdinalIgnoreCase);
      _allReferences.RemoveAll(r => !seen.Add(r.Type + "\n" + r.Path));
    }

    private Boolean Exists(String path) {
      if (String.IsNullOrWhiteSpace(path)) return false;
      return ResourceExists == null || ResourceExists(path);
    }

    private String ResolveExisting(params String[] paths) {
      foreach (String path in paths) if (!String.IsNullOrWhiteSpace(path) && Exists(path)) return NormalizeResourcePath(path);
      return null;
    }

    private void GridSelectionChanged(Object sender, EventArgs e) {
      if (_grid.CurrentRow?.DataBoundItem is not MaterialRow row || row.Texture == null) return;
      SelectTexture(row.Texture);
      _details.SelectedIndex = 1;
    }

    private void GridCellDoubleClick(Object sender, DataGridViewCellEventArgs e) {
      if (e.RowIndex < 0 || _grid.Rows[e.RowIndex].DataBoundItem is not MaterialRow row || row.Texture == null) return;
      OpenResourceRequested?.Invoke(row.Texture.ResolvedDds ?? row.Texture.DdsPath);
    }

    private void TextureSelectionChanged(Object sender, EventArgs e) {
      if (_textures.SelectedItems.Count == 0 || _textures.SelectedItems[0].Tag is not MaterialTexture texture) return;
      BeginTextureLoad(texture.ResolvedDds ?? texture.DdsPath);
    }

    private void SelectTexture(MaterialTexture texture) {
      foreach (ListViewItem item in _textures.Items) {
        if (ReferenceEquals(item.Tag, texture)) { item.Selected = true; item.EnsureVisible(); return; }
      }
      BeginTextureLoad(texture.ResolvedDds ?? texture.DdsPath);
    }

    private void TextureListKeyDown(Object sender, KeyEventArgs e) {
      if (e.KeyCode == Keys.Enter) { OpenSelectedTexture(); e.Handled = true; e.SuppressKeyPress = true; }
    }

    private void ReferenceListKeyDown(Object sender, KeyEventArgs e) {
      if (e.KeyCode == Keys.Enter) { OpenSelectedReference(); e.Handled = true; e.SuppressKeyPress = true; }
    }

    private void OpenSelectedTexture() {
      if (_textures.SelectedItems.Count == 0 || _textures.SelectedItems[0].Tag is not MaterialTexture texture) return;
      String path = texture.ResolvedDds ?? texture.DdsPath;
      if (!String.IsNullOrWhiteSpace(path)) OpenResourceRequested?.Invoke(path);
    }

    private void OpenSelectedReference() {
      if (_references.SelectedItems.Count == 0 || _references.SelectedItems[0].Tag is not MaterialReference reference) return;
      OpenResourceRequested?.Invoke(reference.Path);
    }

    private async void BeginTextureLoad(String path) {
      CancelTextureLoad();
      _texturePreview.ClearPreview();
      ReplaceTextureSource(null);
      _textureLabel.Text = path + "   (wheel = zoom, drag = pan, double-click = reset/open from list)";
      if (TextureLoader == null || String.IsNullOrWhiteSpace(path)) return;
      CancellationTokenSource cts = new CancellationTokenSource();
      _textureCts = cts;
      Int32 generation = ++_textureGeneration;
      Bitmap bitmap = null;
      try {
        bitmap = await Task.Run(() => {
          cts.Token.ThrowIfCancellationRequested();
          Bitmap result = TextureLoader(path);
          cts.Token.ThrowIfCancellationRequested();
          return result;
        }, cts.Token);
      }
      catch (OperationCanceledException) { bitmap?.Dispose(); return; }
      catch (Exception ex) { _textureLabel.Text = path + "   (preview failed: " + ex.Message + ")"; bitmap?.Dispose(); return; }
      if (cts.IsCancellationRequested || generation != _textureGeneration || IsDisposed) { bitmap?.Dispose(); return; }
      if (bitmap == null) { _textureLabel.Text = path + "   (DDS not found)"; return; }
      ReplaceTextureSource(bitmap);
      ApplyTextureChannel();
    }

    private void ApplyTextureChannel() {
      Bitmap source = _textureSourceBitmap;
      if (source == null || source.Width <= 0 || source.Height <= 0) { _texturePreview.ClearPreview(); return; }
      String mode = _channelBox.SelectedItem?.ToString() ?? "RGBA";
      Bitmap view = CreateChannelBitmap(source, mode);
      _texturePreview.SetBitmap(view);
    }

    private void ReplaceTextureSource(Bitmap bitmap) {
      Bitmap previous = _textureSourceBitmap;
      _textureSourceBitmap = bitmap;
      if (previous != null && !ReferenceEquals(previous, bitmap)) previous.Dispose();
    }

    private static Bitmap CreateChannelBitmap(Bitmap source, String mode) {
      Bitmap input = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
      using (Graphics g = Graphics.FromImage(input)) g.DrawImageUnscaled(source, 0, 0);
      if (mode.Equals("RGBA", StringComparison.OrdinalIgnoreCase)) return input;

      BitmapData data = input.LockBits(new Rectangle(0, 0, input.Width, input.Height), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
      try {
        Int32 stride = Math.Abs(data.Stride);
        Byte[] bytes = new Byte[stride * input.Height];
        Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
        for (Int32 y = 0; y < input.Height; y++) {
          for (Int32 x = 0; x < input.Width; x++) {
            Int32 i = y * stride + x * 4;
            Byte b = bytes[i], g = bytes[i + 1], r = bytes[i + 2], a = bytes[i + 3];
            if (mode.Equals("RGB", StringComparison.OrdinalIgnoreCase)) { bytes[i + 3] = 255; continue; }
            if (mode.Equals("Red", StringComparison.OrdinalIgnoreCase)) { bytes[i] = r; bytes[i + 1] = r; bytes[i + 2] = r; bytes[i + 3] = 255; continue; }
            if (mode.Equals("Green", StringComparison.OrdinalIgnoreCase)) { bytes[i] = g; bytes[i + 1] = g; bytes[i + 2] = g; bytes[i + 3] = 255; continue; }
            if (mode.Equals("Blue", StringComparison.OrdinalIgnoreCase) || mode.Equals("Emissive (B)", StringComparison.OrdinalIgnoreCase)) { bytes[i] = b; bytes[i + 1] = b; bytes[i + 2] = b; bytes[i + 3] = 255; continue; }
            if (mode.Equals("Alpha", StringComparison.OrdinalIgnoreCase)) { bytes[i] = a; bytes[i + 1] = a; bytes[i + 2] = a; bytes[i + 3] = 255; continue; }
            if (mode.Equals("Inverse alpha (1-R)", StringComparison.OrdinalIgnoreCase)) { Byte v = (Byte)(255 - r); bytes[i] = v; bytes[i + 1] = v; bytes[i + 2] = v; bytes[i + 3] = 255; continue; }
            if (mode.Equals("Rotation normal (A/G)", StringComparison.OrdinalIgnoreCase)) {
              Double nx = a / 255.0 * 2.0 - 1.0;
              Double ny = g / 255.0 * 2.0 - 1.0;
              Double nz = Math.Sqrt(Math.Max(0.0, 1.0 - nx * nx - ny * ny));
              bytes[i + 2] = ClampByte((Single)((nx * 0.5 + 0.5) * 255.0));
              bytes[i + 1] = ClampByte((Single)((ny * 0.5 + 0.5) * 255.0));
              bytes[i] = ClampByte((Single)((nz * 0.5 + 0.5) * 255.0));
              bytes[i + 3] = 255;
            }
          }
        }
        Marshal.Copy(bytes, 0, data.Scan0, bytes.Length);
      }
      finally { input.UnlockBits(data); }
      return input;
    }

    private async void BeginMaterialPreview() {
      CancelMaterialLoad();
      _materialPreview.ClearPreview();
      if (!_format.Equals("MAT", StringComparison.OrdinalIgnoreCase) || TextureLoader == null) {
        _materialPreviewLabel.Text = "Material preview is available for MAT files.";
        return;
      }

      MaterialTexture diffuse = FindTexture("DiffuseMap");
      MaterialTexture rotation = FindTexture("RotationMap1") ?? FindTexture("RotationMap");
      MaterialTexture gloss = FindTexture("GlossMap");
      String flat = FindInputValue("diffuseFlatColorProp");
      if (diffuse == null && String.IsNullOrWhiteSpace(flat)) {
        _materialPreviewLabel.Text = "No DiffuseMap / flat diffuse color found; parameter and texture views are still available.";
        return;
      }

      CancellationTokenSource cts = new CancellationTokenSource();
      _materialCts = cts;
      Int32 generation = ++_materialGeneration;
      _materialPreviewLabel.Text = "Building offline shaded preview from Diffuse/Rotation/Gloss ...";
      Bitmap preview = null;
      try {
        preview = await Task.Run(() => {
          cts.Token.ThrowIfCancellationRequested();
          using Bitmap d = LoadOptional(diffuse?.ResolvedDds ?? diffuse?.DdsPath);
          cts.Token.ThrowIfCancellationRequested();
          using Bitmap n = LoadOptional(rotation?.ResolvedDds ?? rotation?.DdsPath);
          cts.Token.ThrowIfCancellationRequested();
          using Bitmap g = LoadOptional(gloss?.ResolvedDds ?? gloss?.DdsPath);
          Color flatColor = ParseFloatColor(flat, Color.White);
          return RenderMaterialSphere(d, n, g, flatColor, cts.Token);
        }, cts.Token);
      }
      catch (OperationCanceledException) { preview?.Dispose(); return; }
      catch (Exception ex) { _materialPreviewLabel.Text = "Material preview failed: " + ex.Message; preview?.Dispose(); return; }
      if (cts.IsCancellationRequested || generation != _materialGeneration || IsDisposed) { preview?.Dispose(); return; }
      if (preview == null) { _materialPreviewLabel.Text = "Material preview could not be generated."; return; }
      _materialPreview.SetBitmap(preview);
      _materialPreviewLabel.Text = "Approximate SWTOR preview: Diffuse + Rotation(A/G normal, B emissive, R alpha) + Gloss. Wheel/drag supported.";
    }

    private Bitmap LoadOptional(String path) {
      if (String.IsNullOrWhiteSpace(path) || !Exists(path)) return null;
      return TextureLoader(path);
    }

    private MaterialTexture FindTexture(String semantic) => _materialTextures.FirstOrDefault(t => t.Semantic.Equals(semantic, StringComparison.OrdinalIgnoreCase));

    private String FindInputValue(String name) {
      MaterialRow row = _rows.FirstOrDefault(r => r.Section == "Input" && r.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
      if (row == null) return null;
      Int32 marker = row.Value.IndexOf("   [variable:", StringComparison.Ordinal);
      return marker >= 0 ? row.Value.Substring(0, marker) : row.Value;
    }

    private String FindValue(String name) {
      MaterialRow row = _rows.FirstOrDefault(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
      return row?.Value;
    }

    private void CancelTextureLoad() {
      try { _textureCts?.Cancel(); _textureCts?.Dispose(); } catch { }
      _textureCts = null;
      _textureGeneration++;
    }

    private void CancelMaterialLoad() {
      try { _materialCts?.Cancel(); _materialCts?.Dispose(); } catch { }
      _materialCts = null;
      _materialGeneration++;
    }

    protected override void Dispose(Boolean disposing) {
      if (disposing) {
        CancelTextureLoad();
        CancelMaterialLoad();
        ReplaceTextureSource(null);
      }
      base.Dispose(disposing);
    }

    private static XmlDocument LoadXml(String text) {
      XmlDocument doc = new XmlDocument { XmlResolver = null };
      doc.LoadXml(text ?? String.Empty);
      return doc;
    }

    private static String FixEscapedXmlDeclaration(String text) {
      if (String.IsNullOrEmpty(text)) return text ?? String.Empty;
      const String broken = "<?xml version=\\\"1.0\\\"?>";
      return text.StartsWith(broken, StringComparison.Ordinal) ? text.Replace(broken, "<?xml version=\"1.0\"?>", StringComparison.Ordinal) : text;
    }

    private static String ChildText(XmlNode node, String childName) {
      XmlNode child = node?.ChildNodes.Cast<XmlNode>().FirstOrDefault(x => x.NodeType == XmlNodeType.Element && x.Name.Equals(childName, StringComparison.OrdinalIgnoreCase));
      return child?.InnerText?.Trim() ?? String.Empty;
    }

    private static String DescriptionForMat(String name) => MatDescriptions.TryGetValue(name ?? String.Empty, out String value) ? value : String.Empty;

    private static String DescribeInput(String semantic, String type) {
      if (type.Equals("texture", StringComparison.OrdinalIgnoreCase)) return "Texture input; .tex/.tiny.dds/.dds links are resolved from the loaded assets";
      if (semantic.EndsWith("HSLA", StringComparison.OrdinalIgnoreCase)) return "HSLA color/vector input";
      if (semantic.IndexOf("Color", StringComparison.OrdinalIgnoreCase) >= 0 || semantic.IndexOf("Tint", StringComparison.OrdinalIgnoreCase) >= 0) return "Color/tint material input";
      if (semantic.IndexOf("Uv", StringComparison.OrdinalIgnoreCase) >= 0) return "UV coordinate / tiling input";
      if (semantic.IndexOf("Opacity", StringComparison.OrdinalIgnoreCase) >= 0 || semantic.IndexOf("Alpha", StringComparison.OrdinalIgnoreCase) >= 0) return "Opacity / alpha control";
      if (semantic.IndexOf("Bloom", StringComparison.OrdinalIgnoreCase) >= 0 || semantic.IndexOf("Emiss", StringComparison.OrdinalIgnoreCase) >= 0) return "Bloom / emissive control";
      return String.Empty;
    }

    private static String DescribeEmt(String name) {
      if (name.Equals("BlendDiffuse", StringComparison.OrdinalIgnoreCase)) return "Environment blend diffuse texture object";
      if (name.Equals("BlendNormal", StringComparison.OrdinalIgnoreCase)) return "Environment blend normal texture object";
      if (name.Equals("EnvBlendParams1", StringComparison.OrdinalIgnoreCase) || name.Equals("EnvBlendParams2", StringComparison.OrdinalIgnoreCase)) return "Environment blend shader vector";
      return String.Empty;
    }

    private static String InferValueType(String value) {
      if (String.IsNullOrWhiteSpace(value)) return "empty";
      String[] parts = value.Split(',');
      if (parts.Length >= 2 && parts.All(p => Single.TryParse(p.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out _))) return "vector" + parts.Length;
      if (Boolean.TryParse(value, out _)) return "bool";
      if (Single.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) return "number";
      if (value.IndexOf('/') >= 0 || value.IndexOf('\\') >= 0) return "resource";
      return "string";
    }

    private static String PrettyVector(String value) {
      if (String.IsNullOrWhiteSpace(value) || value.IndexOf(',') < 0) return value ?? String.Empty;
      String[] parts = value.Split(',');
      if (!parts.All(p => Single.TryParse(p.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out _))) return value;
      return String.Join(", ", parts.Select(p => Single.Parse(p.Trim(), CultureInfo.InvariantCulture).ToString("0.#####", CultureInfo.InvariantCulture)));
    }

    private static Boolean ValuesEqual(String a, String b) => String.Equals((a ?? String.Empty).Trim(), (b ?? String.Empty).Trim(), StringComparison.OrdinalIgnoreCase);

    private static String NormalizeTextureBase(String raw) {
      if (String.IsNullOrWhiteSpace(raw)) return null;
      String path = raw.Trim().Replace('\\', '/');
      while (path.Contains("//", StringComparison.Ordinal)) path = path.Replace("//", "/", StringComparison.Ordinal);
      if (path.StartsWith("resources/", StringComparison.OrdinalIgnoreCase)) path = "/" + path;
      else if (!path.StartsWith("/resources/", StringComparison.OrdinalIgnoreCase)) path = "/resources/" + path.TrimStart('/');
      if (path.EndsWith(".tiny.dds", StringComparison.OrdinalIgnoreCase)) path = path.Substring(0, path.Length - 9);
      else if (path.EndsWith(".dds", StringComparison.OrdinalIgnoreCase)) path = path.Substring(0, path.Length - 4);
      else if (path.EndsWith(".tex", StringComparison.OrdinalIgnoreCase)) path = path.Substring(0, path.Length - 4);
      return path.ToLowerInvariant();
    }

    private static String NormalizeResourcePath(String raw) {
      if (String.IsNullOrWhiteSpace(raw)) return null;
      String path = raw.Trim().Replace('\\', '/');
      while (path.Contains("//", StringComparison.Ordinal)) path = path.Replace("//", "/", StringComparison.Ordinal);
      if (path.StartsWith("resources/", StringComparison.OrdinalIgnoreCase)) path = "/" + path;
      else if (!path.StartsWith("/resources/", StringComparison.OrdinalIgnoreCase)) path = "/resources/" + path.TrimStart('/');
      return path.ToLowerInvariant();
    }

    private static String RemoveExtension(String path) {
      if (String.IsNullOrWhiteSpace(path)) return path;
      if (path.EndsWith(".tiny.dds", StringComparison.OrdinalIgnoreCase)) return path.Substring(0, path.Length - 9);
      Int32 slash = path.LastIndexOf('/');
      Int32 dot = path.LastIndexOf('.');
      return dot > slash ? path.Substring(0, dot) : path;
    }

    private static Color ParseFloatColor(String value, Color fallback) {
      if (String.IsNullOrWhiteSpace(value)) return fallback;
      String[] parts = value.Trim().Trim('(', ')', '[', ']').Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries);
      if (parts.Length < 3) return fallback;
      if (!Single.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out Single r)
          || !Single.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out Single g)
          || !Single.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out Single b)) return fallback;
      return Color.FromArgb(255, ClampByte(r * 255f), ClampByte(g * 255f), ClampByte(b * 255f));
    }

    private static Bitmap RenderMaterialSphere(Bitmap diffuse, Bitmap rotation, Bitmap gloss, Color flatColor, CancellationToken token) {
      const Int32 size = 384;
      Bitmap output = new Bitmap(size, size, PixelFormat.Format32bppArgb);
      PixelSource d = PixelSource.FromBitmap(diffuse);
      PixelSource n = PixelSource.FromBitmap(rotation);
      PixelSource g = PixelSource.FromBitmap(gloss);
      BitmapData dst = output.LockBits(new Rectangle(0, 0, size, size), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
      try {
        Byte[] pixels = new Byte[Math.Abs(dst.Stride) * size];
        Double lx = -0.45, ly = 0.58, lz = 0.68;
        Normalize3(ref lx, ref ly, ref lz);
        Double vx = 0, vy = 0, vz = 1;
        for (Int32 y = 0; y < size; y++) {
          if ((y & 15) == 0) token.ThrowIfCancellationRequested();
          Double sy = 1.0 - (y + 0.5) * 2.0 / size;
          for (Int32 x = 0; x < size; x++) {
            Double sx = (x + 0.5) * 2.0 / size - 1.0;
            Double rr = sx * sx + sy * sy;
            Int32 index = y * Math.Abs(dst.Stride) + x * 4;
            if (rr > 1.0) {
              Int32 checker = ((x / 16) + (y / 16)) & 1;
              Byte bg = (Byte)(checker == 0 ? 38 : 48);
              pixels[index] = bg; pixels[index + 1] = bg; pixels[index + 2] = bg; pixels[index + 3] = 255;
              continue;
            }

            Double sz = Math.Sqrt(Math.Max(0, 1.0 - rr));
            Double u = 0.5 + Math.Atan2(sx, sz) / (2.0 * Math.PI);
            Double v = 0.5 - Math.Asin(Math.Max(-1.0, Math.Min(1.0, sy))) / Math.PI;
            Color dc = d.Valid ? d.Sample(u, v) : flatColor;
            Color nc = n.Valid ? n.Sample(u, v) : Color.FromArgb(255, 0, 128, 0);
            Color gc = g.Valid ? g.Sample(u, v) : Color.FromArgb(64, 20, 20, 20);

            // RotationMap1: A/G = tangent XY, B = emissive strength, R = inverse alpha.
            Double tx = n.Valid ? nc.A / 255.0 * 2.0 - 1.0 : 0.0;
            Double ty = n.Valid ? nc.G / 255.0 * 2.0 - 1.0 : 0.0;
            Double tz = Math.Sqrt(Math.Max(0.0, 1.0 - tx * tx - ty * ty));

            // Build a stable tangent frame for the procedural sphere and transform tangent normal.
            Double nx = sx, ny = sy, nz = sz;
            Double tangentX = nz, tangentY = 0, tangentZ = -nx;
            Normalize3(ref tangentX, ref tangentY, ref tangentZ);
            Double binormalX = ny * tangentZ - nz * tangentY;
            Double binormalY = nz * tangentX - nx * tangentZ;
            Double binormalZ = nx * tangentY - ny * tangentX;
            Double bumpX = tangentX * tx + binormalX * ty + nx * tz;
            Double bumpY = tangentY * tx + binormalY * ty + ny * tz;
            Double bumpZ = tangentZ * tx + binormalZ * ty + nz * tz;
            Normalize3(ref bumpX, ref bumpY, ref bumpZ);

            Double ndl = Math.Max(0.0, bumpX * lx + bumpY * ly + bumpZ * lz);
            Double hx = lx + vx, hy = ly + vy, hz = lz + vz;
            Normalize3(ref hx, ref hy, ref hz);
            Double ndh = Math.Max(0.0, bumpX * hx + bumpY * hy + bumpZ * hz);
            Double glossPower = 1.0 + gc.A / 255.0 * 63.0;
            Double specStrength = Math.Pow(ndh, glossPower);
            Double emissive = n.Valid ? nc.B / 255.0 : 0.0;
            Double diffuseLight = 0.32 + ndl * 0.68;
            Double fresnel = Math.Pow(1.0 - Math.Max(0.0, nz), 4.0) * 0.10;

            Double outR = dc.R / 255.0 * (diffuseLight + emissive) + gc.R / 255.0 * specStrength + fresnel;
            Double outG = dc.G / 255.0 * (diffuseLight + emissive) + gc.G / 255.0 * specStrength + fresnel;
            Double outB = dc.B / 255.0 * (diffuseLight + emissive) + gc.B / 255.0 * specStrength + fresnel;
            Double alpha = n.Valid ? 1.0 - nc.R / 255.0 : dc.A / 255.0;

            pixels[index] = ClampByte((Single)(outB * 255.0));
            pixels[index + 1] = ClampByte((Single)(outG * 255.0));
            pixels[index + 2] = ClampByte((Single)(outR * 255.0));
            pixels[index + 3] = ClampByte((Single)(Math.Max(0.1, alpha) * 255.0));
          }
        }
        Marshal.Copy(pixels, 0, dst.Scan0, pixels.Length);
      }
      finally { output.UnlockBits(dst); d.Dispose(); n.Dispose(); g.Dispose(); }
      return output;
    }

    private static void Normalize3(ref Double x, ref Double y, ref Double z) {
      Double length = Math.Sqrt(x * x + y * y + z * z);
      if (length < 0.000001) { x = 0; y = 0; z = 1; return; }
      x /= length; y /= length; z /= length;
    }

    private static Byte ClampByte(Single value) => (Byte)Math.Max(0, Math.Min(255, (Int32)Math.Round(value)));

    private sealed class PixelSource : IDisposable {
      public readonly Int32 Width;
      public readonly Int32 Height;
      public readonly Int32 Stride;
      public readonly Byte[] Data;
      public Boolean Valid => Data != null && Width > 0 && Height > 0;

      private PixelSource(Int32 width, Int32 height, Int32 stride, Byte[] data) { Width = width; Height = height; Stride = stride; Data = data; }

      public static PixelSource FromBitmap(Bitmap source) {
        if (source == null) return new PixelSource(0, 0, 0, null);
        Bitmap copy = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(copy)) graphics.DrawImageUnscaled(source, 0, 0);
        BitmapData data = copy.LockBits(new Rectangle(0, 0, copy.Width, copy.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try {
          Int32 stride = Math.Abs(data.Stride);
          Byte[] bytes = new Byte[stride * copy.Height];
          Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
          return new PixelSource(copy.Width, copy.Height, stride, bytes);
        }
        finally { copy.UnlockBits(data); copy.Dispose(); }
      }

      public Color Sample(Double u, Double v) {
        if (!Valid) return Color.White;
        u -= Math.Floor(u);
        v -= Math.Floor(v);
        Int32 x = Math.Max(0, Math.Min(Width - 1, (Int32)(u * Width)));
        Int32 y = Math.Max(0, Math.Min(Height - 1, (Int32)(v * Height)));
        Int32 index = y * Stride + x * 4;
        return Color.FromArgb(Data[index + 3], Data[index + 2], Data[index + 1], Data[index]);
      }

      public void Dispose() { }
    }

    private sealed class MaterialRow {
      public String Section { get; }
      public String Name { get; }
      public String Type { get; }
      public String Value { get; }
      public String Description { get; }
      public MaterialTexture Texture { get; set; }
      public MaterialRow(String section, String name, String type, String value, String description, MaterialTexture texture) {
        Section = section ?? String.Empty; Name = name ?? String.Empty; Type = type ?? String.Empty;
        Value = value ?? String.Empty; Description = description ?? String.Empty; Texture = texture;
      }
    }

    private sealed class MaterialTexture {
      public String Semantic { get; }
      public String DdsPath { get; }
      public String TinyDdsPath { get; }
      public String TexPath { get; }
      public String SourceKind { get; }
      public String ResolvedDds { get; set; }
      public MaterialTexture(String semantic, String dds, String tiny, String tex, String sourceKind) {
        Semantic = semantic ?? String.Empty; DdsPath = dds; TinyDdsPath = tiny; TexPath = tex; SourceKind = sourceKind ?? String.Empty;
      }
    }

    private sealed class MaterialReference {
      public String Type { get; }
      public String Path { get; }
      public String Source { get; }
      public MaterialReference(String type, String path, String source) { Type = type ?? String.Empty; Path = path ?? String.Empty; Source = source ?? String.Empty; }
    }
  }
}
