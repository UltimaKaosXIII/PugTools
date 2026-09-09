using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml;

namespace PugTools {
  /// <summary>
  /// Offline Jedipedia-style inspector for text effect formats (PRT/FXSPEC/EPP).
  /// It deliberately depends only on the currently loaded SWTOR assets: no generated DB is required.
  /// </summary>
  internal sealed class EffectSpecPreviewControl : UserControl {
    private readonly ToolStrip _toolbar;
    private readonly ToolStripLabel _kindLabel;
    private readonly ToolStripLabel _summaryLabel;
    private readonly ToolStripButton _hideDefaultsButton;
    private readonly SplitContainer _split;
    private readonly DataGridView _grid;
    private readonly TabControl _details;
    private readonly EffectValueGraphControl _graph;
    private readonly ParticleSimulationControl _simulation;
    private readonly EffectDependencyGraphControl _dependencyGraph;
    private readonly PictureBox _texture;
    private readonly Label _textureLabel;
    private readonly ListView _resources;
    private readonly TextBox _raw;
    private readonly List<EffectRow> _allRows = new List<EffectRow>();
    private readonly List<EffectResource> _allResources = new List<EffectResource>();
    private CancellationTokenSource _textureCts;
    private Int32 _textureGeneration;
    private String _sourcePath;
    private String _format;

    private Func<String, Bitmap> _textureLoader;
    public Func<String, Bitmap> TextureLoader {
      get => _textureLoader;
      set { _textureLoader = value; if (_simulation != null) _simulation.TextureLoader = value; }
    }
    public Func<String, String> PrtTextLoader { get; set; }

    private Func<String, Boolean> _resourceExists;
    public Func<String, Boolean> ResourceExists {
      get => _resourceExists;
      set { _resourceExists = value; if (_dependencyGraph != null) _dependencyGraph.ResourceExists = value; }
    }

    private Action<String> _openResourceRequested;
    public Action<String> OpenResourceRequested {
      get => _openResourceRequested;
      set { _openResourceRequested = value; if (_dependencyGraph != null) _dependencyGraph.OpenResourceRequested = value; }
    }

    private static readonly Dictionary<String, String> PrtDefaults = new Dictionary<String, String>(StringComparer.OrdinalIgnoreCase) {
      { "SourceBlend", "D3DBLEND_SRCALPHA" }, { "DestBlend", "D3DBLEND_ONE" },
      { "BackFace", "true" }, { "DepthBias", "1" }, { "BlurFactor", "0" },
      { "MaxDistanceFromCamera", "0" }, { "MinDistanceFromCamera", "0" },
      { "AlphaFalloff", "0" }, { "FarAlphaFalloff", "0" }, { "IgnoreFog", "false" },
      { "UseLighting", "false" }, { "UseSoftParticles", "false" },
      { "AlphaRefUsesEmitterLifeSpan", "false" }, { "UseParticleHueing", "false" },
      { "OrientationAxis", "XYZ" }, { "LieFlat", "false" }, { "AlignToTrajectory", "false" },
      { "ResizeEachUpdate", "true" }, { "ScaleEachUpdate", "true" },
      { "DistanceScaleAdjustment", "1" }, { "ScaleClampDistance", "0" },
      { "LockRotation", "false" }, { "DeltaRotationUseLocalSpace", "false" },
      { "TextureNumber", "0" }, { "RowSize", "1" }, { "ColumnSize", "1" },
      { "StartFrame", "0" }, { "FrameMoveDirection", "0" }, { "AnimationSpeed", "0" },
      { "Texture2Atlassed", "false" }, { "Texture2AnimBlend", "false" },
      { "EmitterShape", "Point" }, { "NumberToEmit", "1" }, { "MaxActiveEmissions", "1000" },
      { "UpdateRate", "NORMAL" }, { "DistanceRateAdjustment", "1" }, { "MinParticlesPerMeter", "0" },
      { "SubFrameParticleDistribution", "false" }, { "ChildrenInheritMomentum", "false" },
      { "LinkChildren", "false" }, { "LinkToCamera", "false" }, { "UseChaseTarget", "false" },
      { "RunUp", "false" }, { "DoChildEmitter", "false" }, { "AlignToNormal", "false" },
      { "AreaGUID", "0" }, { "CollisionResponse", "COLLIDE_IGNORE" },
      { "CollideWithAttractor", "false" }, { "WindMultiplier", "1" }, { "WindColorVariation", "80" },
      { "DoTrail", "false" }, { "DoSecondaryTrail", "false" }, { "TrailAddRate", "0" }
    };

    public EffectSpecPreviewControl() {
      Dock = DockStyle.Fill;
      BackColor = SystemColors.Window;

      _toolbar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Dock = DockStyle.Top };
      _kindLabel = new ToolStripLabel { Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold) };
      _summaryLabel = new ToolStripLabel();
      _hideDefaultsButton = new ToolStripButton("Hide defaults") { CheckOnClick = true, Checked = true };
      _hideDefaultsButton.CheckedChanged += delegate { ApplyRowFilter(); };
      _toolbar.Items.Add(_kindLabel);
      _toolbar.Items.Add(new ToolStripSeparator());
      _toolbar.Items.Add(_summaryLabel);
      _toolbar.Items.Add(new ToolStripSeparator());
      _toolbar.Items.Add(_hideDefaultsButton);

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
      _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Property", DataPropertyName = nameof(EffectRow.Path), Width = 260 });
      _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Value", DataPropertyName = nameof(EffectRow.Value), AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
      _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Kind", DataPropertyName = nameof(EffectRow.Kind), Width = 90 });
      _grid.SelectionChanged += GridSelectionChanged;
      _grid.CellDoubleClick += GridCellDoubleClick;
      _split.Panel1.Controls.Add(_grid);

      _details = new TabControl { Dock = DockStyle.Fill };
      var valuePage = new TabPage("Value");
      _graph = new EffectValueGraphControl { Dock = DockStyle.Fill, BackColor = Color.FromArgb(28, 28, 28) };
      valuePage.Controls.Add(_graph);

      var simulationPage = new TabPage("Simulation");
      _simulation = new ParticleSimulationControl { Dock = DockStyle.Fill };
      simulationPage.Controls.Add(_simulation);

      var graphPage = new TabPage("Graph");
      _dependencyGraph = new EffectDependencyGraphControl { Dock = DockStyle.Fill };
      graphPage.Controls.Add(_dependencyGraph);

      var texturePage = new TabPage("Texture");
      _texture = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(32, 32, 32) };
      _textureLabel = new Label { Dock = DockStyle.Top, Height = 38, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(6, 0, 6, 0) };
      _texture.DoubleClick += delegate { OpenSelectedTexture(); };
      texturePage.Controls.Add(_texture);
      texturePage.Controls.Add(_textureLabel);

      var resourcesPage = new TabPage("References");
      _resources = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = true, HideSelection = false };
      _resources.Columns.Add("Type", 80);
      _resources.Columns.Add("Path", 520);
      _resources.Columns.Add("From", 220);
      _resources.DoubleClick += delegate { OpenSelectedResource(); };
      _resources.KeyDown += ResourcesKeyDown;
      resourcesPage.Controls.Add(_resources);

      var rawPage = new TabPage("Raw");
      _raw = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Font = new Font(FontFamily.GenericMonospace, 9f) };
      rawPage.Controls.Add(_raw);

      _details.TabPages.Add(valuePage);
      _details.TabPages.Add(simulationPage);
      _details.TabPages.Add(graphPage);
      _details.TabPages.Add(texturePage);
      _details.TabPages.Add(resourcesPage);
      _details.TabPages.Add(rawPage);
      _split.Panel2.Controls.Add(_details);

      Controls.Add(_split);
      Controls.Add(_toolbar);

      SplitContainerSafeLayout.ApplyOnLoad(this, _split, 650);
    }

    public void ClearPreview() {
      CancelTextureLoad();
      _allRows.Clear();
      _allResources.Clear();
      _grid.DataSource = null;
      _resources.Items.Clear();
      _graph.ValueText = null;
      _simulation.SetSession(null);
      _dependencyGraph.ClearGraph();
      ReplaceTexture(null);
      _textureLabel.Text = String.Empty;
      _raw.Clear();
      _kindLabel.Text = String.Empty;
      _summaryLabel.Text = String.Empty;
      _sourcePath = null;
      _format = null;
    }

    public void LoadPrt(String sourcePath, String text) {
      ClearPreview();
      _sourcePath = NormalizeResourcePath(sourcePath);
      _format = "PRT";
      _raw.Text = text ?? String.Empty;

      String areaGuid = null;
      String version = null;
      using (var reader = new System.IO.StringReader(text ?? String.Empty)) {
        String line;
        while ((line = reader.ReadLine()) != null) {
          String trimmed = line.Trim();
          if (trimmed.StartsWith("Version=", StringComparison.OrdinalIgnoreCase)) version = trimmed.Substring(8).Trim();
          if (trimmed.StartsWith("AreaGUID=", StringComparison.OrdinalIgnoreCase)) areaGuid = trimmed.Substring(9).Trim();
          if (trimmed.Length == 0 || trimmed == "!" || trimmed == "[SETTINGS]" || trimmed.StartsWith("! Particle Specification", StringComparison.OrdinalIgnoreCase)) continue;
          Int32 equal = trimmed.IndexOf('=');
          if (equal < 0) continue;
          String key = trimmed.Substring(0, equal).Trim().TrimStart('.');
          String value = trimmed.Substring(equal + 1).Trim();
          if (key.Length == 0 || key.Equals("Version", StringComparison.OrdinalIgnoreCase) || key.Equals("AreaGUID", StringComparison.OrdinalIgnoreCase)) continue;
          String kind = ClassifyPrtValue(value);
          Boolean isDefault = PrtDefaults.TryGetValue(key, out String defaultValue) && ValuesEqual(value, defaultValue);
          var row = new EffectRow(key, value, kind, isDefault);
          _allRows.Add(row);
          AddResourcesForPrt(key, value, row.Path);
        }
      }

      String particleType = _allRows.FirstOrDefault(r => r.Path.Equals("ParticleType", StringComparison.OrdinalIgnoreCase))?.Value;
      _kindLabel.Text = "PRT particle";
      _summaryLabel.Text = "Type: " + (String.IsNullOrWhiteSpace(particleType) ? "BILLBOARD_POINT" : particleType)
        + (String.IsNullOrWhiteSpace(version) ? String.Empty : "   Version: " + version)
        + (String.IsNullOrWhiteSpace(areaGuid) ? String.Empty : "   Area: " + areaGuid)
        + "   Parameters: " + _allRows.Count.ToString("n0");
      _hideDefaultsButton.Visible = true;
      _hideDefaultsButton.Checked = true;
      try {
        _simulation.SetSession(View_AREA.PrtPreviewSession.Create(_sourcePath, text, PrtTextLoader));
      } catch { _simulation.SetSession(null); }
      _dependencyGraph.LoadPrt(_sourcePath, text);
      FinalizeLoad();
      _details.SelectedIndex = 1; // simulation is the most useful first view for PRT
    }

    public void LoadXmlEffect(String format, String sourcePath, String text) {
      ClearPreview();
      _sourcePath = NormalizeResourcePath(sourcePath);
      _format = String.IsNullOrWhiteSpace(format) ? "XML" : format.ToUpperInvariant();
      _raw.Text = text ?? String.Empty;
      _hideDefaultsButton.Visible = false;

      try {
        var doc = new XmlDocument { PreserveWhitespace = false };
        doc.LoadXml(text ?? String.Empty);
        XmlElement root = doc.DocumentElement;
        if (root != null) FlattenXml(root, root.Name, 0);
        ExtractKnownXmlEffectResources(doc);
        _kindLabel.Text = _format + " effect";
        String id = root?.GetAttribute("id");
        _summaryLabel.Text = (String.IsNullOrWhiteSpace(id) ? String.Empty : "Node: " + id + "   ")
          + "Fields: " + _allRows.Count.ToString("n0") + "   References: " + _allResources.Count.ToString("n0");
      }
      catch (Exception ex) {
        _kindLabel.Text = _format + " effect";
        _summaryLabel.Text = "Structured parse failed: " + ex.Message;
        _allRows.Add(new EffectRow("Parse error", ex.Message, "error", false));
      }
      _dependencyGraph.LoadXmlEffect(_format, _sourcePath, text);
      FinalizeLoad();
      if (_format == "FXSPEC" || _format == "EPP") _details.SelectedIndex = 2;
    }

    private void FlattenXml(XmlElement element, String path, Int32 depth) {
      if (depth > 32) return;
      if (element.HasAttributes) {
        foreach (XmlAttribute attribute in element.Attributes) {
          String attrPath = path + ".@" + attribute.Name;
          _allRows.Add(new EffectRow(attrPath, attribute.Value, ClassifyPrtValue(attribute.Value), false));
          AddResourcesGeneric(attribute.Value, attrPath);
        }
      }

      List<XmlElement> childElements = element.ChildNodes.OfType<XmlElement>().ToList();
      String ownText = String.Concat(element.ChildNodes.OfType<XmlText>().Select(x => x.Value)).Trim();
      if (childElements.Count == 0) {
        if (ownText.Length > 0) {
          _allRows.Add(new EffectRow(path, ownText, ClassifyPrtValue(ownText), false));
          AddResourcesGeneric(ownText, path);
        } else if (!element.HasAttributes) {
          _allRows.Add(new EffectRow(path, String.Empty, "empty", false));
        }
        return;
      }

      var counts = new Dictionary<String, Int32>(StringComparer.OrdinalIgnoreCase);
      foreach (XmlElement child in childElements) {
        counts.TryGetValue(child.Name, out Int32 index);
        counts[child.Name] = index + 1;
        String label = child.Name;
        String name = child.GetAttribute("name");
        String id = child.GetAttribute("id");
        if (!String.IsNullOrWhiteSpace(name)) label += "[" + name + "]";
        else if (!String.IsNullOrWhiteSpace(id)) label += "[" + id + "]";
        else if (childElements.Count(x => x.Name.Equals(child.Name, StringComparison.OrdinalIgnoreCase)) > 1) label += "[" + index + "]";
        FlattenXml(child, path + "." + label, depth + 1);
      }
    }

    private void ExtractKnownXmlEffectResources(XmlDocument doc) {
      if (doc == null) return;

      if (_format == "EPP") {
        foreach (String tag in new[] { "fxSpecString", "projectileFXString" }) {
          XmlNodeList nodes = doc.GetElementsByTagName(tag);
          foreach (XmlNode node in nodes) {
            String value = (node.InnerText ?? String.Empty).Trim();
            if (value.Length == 0) continue;
            if (!value.EndsWith(".fxspec", StringComparison.OrdinalIgnoreCase)) value += ".fxspec";
            _allResources.Add(new EffectResource("FXSPEC", NormalizeFxSpec(value), tag));
          }
        }
      }

      if (_format != "FXSPEC") return;
      XmlNodeList named = doc.SelectNodes("//*[@name]");
      if (named == null) return;
      foreach (XmlNode node in named) {
        String field = node.Attributes?["name"]?.Value ?? String.Empty;
        String value = (node.InnerText ?? String.Empty).Trim();
        if (value.Length == 0) continue;

        if (field.Equals("displayName", StringComparison.OrdinalIgnoreCase)
            || field.Equals("SpecName", StringComparison.OrdinalIgnoreCase)) {
          String fx = value.EndsWith(".fxspec", StringComparison.OrdinalIgnoreCase) ? value : value + ".fxspec";
          _allResources.Add(new EffectResource("FXSPEC", NormalizeFxSpec(fx), field));
          String relative = NormalizeRelativeToSource(fx);
          if (!String.IsNullOrWhiteSpace(relative)) _allResources.Add(new EffectResource("FXSPEC", relative, field + " (relative)"));
          continue;
        }

        if (field.Equals("_fxResourceName", StringComparison.OrdinalIgnoreCase)) {
          String lower = value.ToLowerInvariant();
          if (lower.EndsWith(".prt")) {
            String prt = value;
            if (!prt.StartsWith("/", StringComparison.Ordinal) && !prt.StartsWith("\\", StringComparison.Ordinal)
                && prt.IndexOf("art/fx/particles", StringComparison.OrdinalIgnoreCase) < 0)
              prt = "art/fx/particles/" + prt;
            _allResources.Add(new EffectResource("PRT", NormalizeResourcePath(prt), field));
          } else if (lower.EndsWith(".gr2") || lower.EndsWith(".lit") || lower.EndsWith(".ext") || lower.EndsWith(".zzp")) {
            _allResources.Add(new EffectResource(System.IO.Path.GetExtension(value).TrimStart('.').ToUpperInvariant(), NormalizeResourcePath(value), field));
          }
          continue;
        }

        if (field.Equals("_fxProjectionTexture", StringComparison.OrdinalIgnoreCase)
            || field.Equals("_fxProjectionTexture_layer1", StringComparison.OrdinalIgnoreCase)
            || field.Equals("_fxTextureName", StringComparison.OrdinalIgnoreCase)
            || field.Equals("_fxRampMap", StringComparison.OrdinalIgnoreCase)) {
          String basePath = value.Replace(".tiny.dds", String.Empty, StringComparison.OrdinalIgnoreCase)
                                 .Replace(".dds", String.Empty, StringComparison.OrdinalIgnoreCase)
                                 .Replace(".tex", String.Empty, StringComparison.OrdinalIgnoreCase);
          String normalized = NormalizeResourcePath(basePath);
          _allResources.Add(new EffectResource("DDS", normalized + ".dds", field));
          _allResources.Add(new EffectResource("DDS", normalized + ".tiny.dds", field));
          _allResources.Add(new EffectResource("TEX", normalized + ".tex", field));
        }
      }
    }

    private String NormalizeRelativeToSource(String value) {
      if (String.IsNullOrWhiteSpace(_sourcePath) || String.IsNullOrWhiteSpace(value)) return null;
      String source = _sourcePath.Replace('\\', '/');
      Int32 slash = source.LastIndexOf('/');
      if (slash < 0) return null;
      return NormalizeResourcePath(source.Substring(0, slash + 1) + value.TrimStart('/', '\\'));
    }

    private void FinalizeLoad() {
      DeduplicateResources();
      RebuildResourceList();
      ApplyRowFilter();
      if (_grid.Rows.Count > 0) {
        _grid.ClearSelection();
        _grid.Rows[0].Selected = true;
        _grid.CurrentCell = _grid.Rows[0].Cells[0];
      }
    }

    private void ApplyRowFilter() {
      IEnumerable<EffectRow> rows = _allRows;
      if (_format == "PRT" && _hideDefaultsButton.Checked) rows = rows.Where(r => !r.IsDefault);
      _grid.DataSource = rows.ToList();
    }

    private void GridSelectionChanged(Object sender, EventArgs e) {
      if (_grid.CurrentRow?.DataBoundItem is not EffectRow row) return;
      _graph.Caption = row.Path;
      _graph.ValueText = row.Value;
      EffectResource linked = _allResources.FirstOrDefault(r => r.Source.Equals(row.Path, StringComparison.OrdinalIgnoreCase));
      if (linked != null && linked.Path.EndsWith(".dds", StringComparison.OrdinalIgnoreCase)) {
        _details.SelectedIndex = 3;
        BeginTextureLoad(linked.Path);
      } else {
        _details.SelectedIndex = 0;
        CancelTextureLoad();
        _textureLabel.Text = linked?.Path ?? String.Empty;
        ReplaceTexture(null);
      }
    }

    private void GridCellDoubleClick(Object sender, DataGridViewCellEventArgs e) {
      if (e.RowIndex < 0 || _grid.Rows[e.RowIndex].DataBoundItem is not EffectRow row) return;
      EffectResource linked = _allResources.FirstOrDefault(r => r.Source.Equals(row.Path, StringComparison.OrdinalIgnoreCase));
      if (linked != null) OpenResourceRequested?.Invoke(linked.Path);
    }

    private async void BeginTextureLoad(String path) {
      CancelTextureLoad();
      _textureLabel.Text = path + "   (double-click to open asset)";
      ReplaceTexture(null);
      if (TextureLoader == null || String.IsNullOrWhiteSpace(path)) return;
      var cts = new CancellationTokenSource();
      _textureCts = cts;
      Int32 generation = ++_textureGeneration;
      Bitmap bitmap = null;
      try {
        bitmap = await Task.Run(() => {
          cts.Token.ThrowIfCancellationRequested();
          Bitmap loaded = TextureLoader(path);
          cts.Token.ThrowIfCancellationRequested();
          return loaded;
        }, cts.Token);
      }
      catch (OperationCanceledException) { bitmap?.Dispose(); return; }
      catch (Exception ex) { _textureLabel.Text = path + "   (preview failed: " + ex.Message + ")"; bitmap?.Dispose(); return; }
      if (cts.IsCancellationRequested || generation != _textureGeneration || IsDisposed) { bitmap?.Dispose(); return; }
      ReplaceTexture(bitmap);
    }

    private void CancelTextureLoad() {
      try { _textureCts?.Cancel(); _textureCts?.Dispose(); } catch { }
      _textureCts = null;
      _textureGeneration++;
    }

    private void ReplaceTexture(Bitmap bitmap) {
      Image previous = _texture.Image;
      _texture.Image = bitmap;
      if (previous != null && !ReferenceEquals(previous, bitmap)) previous.Dispose();
    }

    private void OpenSelectedTexture() {
      if (_textureLabel.Text.Length == 0) return;
      String path = _textureLabel.Text.Split(new[] { "   " }, StringSplitOptions.None)[0];
      if (!String.IsNullOrWhiteSpace(path)) OpenResourceRequested?.Invoke(path);
    }

    private void OpenSelectedResource() {
      if (_resources.SelectedItems.Count == 0) return;
      if (_resources.SelectedItems[0].Tag is EffectResource resource) OpenResourceRequested?.Invoke(resource.Path);
    }

    private void ResourcesKeyDown(Object sender, KeyEventArgs e) {
      if (e.KeyCode == Keys.Enter) { OpenSelectedResource(); e.Handled = true; e.SuppressKeyPress = true; }
    }

    private void RebuildResourceList() {
      _resources.BeginUpdate();
      try {
        _resources.Items.Clear();
        foreach (EffectResource resource in _allResources.OrderBy(r => r.Type).ThenBy(r => r.Path, StringComparer.OrdinalIgnoreCase)) {
          var item = new ListViewItem(resource.Type);
          item.SubItems.Add(resource.Path);
          item.SubItems.Add(resource.Source);
          item.Tag = resource;
          _resources.Items.Add(item);
        }
      } finally { _resources.EndUpdate(); }
    }

    private void DeduplicateResources() {
      var seen = new HashSet<String>(StringComparer.OrdinalIgnoreCase);
      for (Int32 i = _allResources.Count - 1; i >= 0; i--) {
        String key = _allResources[i].Path + "\n" + _allResources[i].Source;
        if (!seen.Add(key)) _allResources.RemoveAt(i);
      }
    }

    private void AddResourcesForPrt(String key, String value, String source) {
      if (String.IsNullOrWhiteSpace(value)) return;
      String normalized = value.Trim().Replace('\\', '/');
      if (key.Equals("TextureName", StringComparison.OrdinalIgnoreCase)
          || key.Equals("TextureName2", StringComparison.OrdinalIgnoreCase)
          || key.Equals("TrailTexture", StringComparison.OrdinalIgnoreCase)) {
        String dds = NormalizeResourcePath(normalized);
        if (!HasKnownExtension(dds)) dds += ".dds";
        _allResources.Add(new EffectResource("DDS", dds, source));
        return;
      }
      if (key.Equals("EmitFXSpec", StringComparison.OrdinalIgnoreCase)) {
        String fx = NormalizeFxSpec(normalized);
        _allResources.Add(new EffectResource("FXSPEC", fx, source));
        return;
      }
      if (key.Equals("GrannyFileName", StringComparison.OrdinalIgnoreCase)) {
        _allResources.Add(new EffectResource("GR2", NormalizeResourcePath(normalized), source));
        return;
      }
      AddResourcesGeneric(value, source);
    }

    private void AddResourcesGeneric(String value, String source) {
      if (String.IsNullOrWhiteSpace(value)) return;
      foreach (Match match in Regex.Matches(value, @"(?i)(?:[\\/A-Za-z0-9_.$@()\- ]+?\.(?:fxspec|prt|dds|gr2|mat|tex|epp|jba|mph|bnk|wem|lit|ext|zzp|sgt|wav))(?=$|[\s,;\]\)\}<>""'])")) {
        String raw = match.Value.Trim().Trim('"', '\'', ',', ';', ')', ']', '}');
        if (raw.Length == 0) continue;
        String ext = System.IO.Path.GetExtension(raw).TrimStart('.').ToUpperInvariant();
        String path;
        if (ext == "FXSPEC") path = NormalizeFxSpec(raw);
        else if (ext == "PRT" && !raw.StartsWith("/", StringComparison.Ordinal) && !raw.StartsWith("\\", StringComparison.Ordinal)
                 && raw.IndexOf("art/fx/particles", StringComparison.OrdinalIgnoreCase) < 0)
          path = NormalizeResourcePath("art/fx/particles/" + raw);
        else path = NormalizeResourcePath(raw);
        _allResources.Add(new EffectResource(ext, path, source));
      }
    }

    private static String NormalizeFxSpec(String value) {
      String p = (value ?? String.Empty).Trim().Replace('\\', '/');
      if (p.StartsWith("/resources/", StringComparison.OrdinalIgnoreCase)) return NormalizeResourcePath(p);
      if (p.StartsWith("resources/", StringComparison.OrdinalIgnoreCase)) return NormalizeResourcePath("/" + p);
      if (p.StartsWith("art/fx/fxspec/", StringComparison.OrdinalIgnoreCase)) return NormalizeResourcePath("/resources/" + p);
      return NormalizeResourcePath("/resources/art/fx/fxspec/" + p.TrimStart('/'));
    }

    private static String NormalizeResourcePath(String value) {
      String p = (value ?? String.Empty).Trim().Replace('\\', '/');
      while (p.Contains("//", StringComparison.Ordinal)) p = p.Replace("//", "/", StringComparison.Ordinal);
      if (p.StartsWith("resources/", StringComparison.OrdinalIgnoreCase)) p = "/" + p;
      else if (!p.StartsWith("/resources/", StringComparison.OrdinalIgnoreCase)) p = "/resources/" + p.TrimStart('/');
      return p.ToLowerInvariant();
    }

    private static Boolean HasKnownExtension(String path) {
      String ext = System.IO.Path.GetExtension(path);
      return !String.IsNullOrWhiteSpace(ext);
    }

    private static String ClassifyPrtValue(String value) {
      String s = (value ?? String.Empty).Trim();
      if (s.Length == 0) return "empty";
      if (s.Equals("true", StringComparison.OrdinalIgnoreCase) || s.Equals("false", StringComparison.OrdinalIgnoreCase)) return "bool";
      if (Regex.IsMatch(s, @"^#(?:[0-9a-fA-F]{6}|[0-9a-fA-F]{8})$")) return "color";
      if (Regex.IsMatch(s, @"\[[^\]]+\].*\[[^\]]+\]")) return s.IndexOf('#') >= 0 ? "gradient" : "track";
      if (Regex.IsMatch(s, @"<\s*[-+0-9.eE]+\s*,\s*[-+0-9.eE]+")) return "random";
      if (Regex.IsMatch(s, @"^\([^\)]*,[^\)]*\)$")) return "vector";
      if (Double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) return "number";
      if (Regex.IsMatch(s, @"(?i)\.(dds|gr2|prt|fxspec|mat|tex|epp|lit|ext|zzp|sgt|wav)$")) return "resource";
      return "text";
    }

    private static Boolean ValuesEqual(String a, String b) {
      if (String.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase)) return true;
      if (Double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out Double da)
          && Double.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out Double db)) return Math.Abs(da - db) < 0.0000001;
      return false;
    }

    protected override void Dispose(Boolean disposing) {
      if (disposing) {
        CancelTextureLoad();
        ReplaceTexture(null);
      }
      base.Dispose(disposing);
    }

    private sealed class EffectRow {
      public String Path { get; }
      public String Value { get; }
      public String Kind { get; }
      public Boolean IsDefault { get; }
      public EffectRow(String path, String value, String kind, Boolean isDefault) { Path = path; Value = value; Kind = kind; IsDefault = isDefault; }
    }

    private sealed class EffectResource {
      public String Type { get; }
      public String Path { get; }
      public String Source { get; }
      public EffectResource(String type, String path, String source) { Type = type; Path = path; Source = source; }
    }
  }

  internal sealed class ParticleSimulationControl : UserControl {
    private readonly ToolStrip _tools;
    private readonly ToolStripButton _pause;
    private readonly ToolStripButton _reset;
    private readonly ToolStripButton _textures;
    private readonly ToolStripButton _grid;
    private readonly ToolStripComboBox _projection;
    private readonly ToolStripLabel _stats;
    private readonly Panel _canvas;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly Dictionary<String, Bitmap> _textureCache = new Dictionary<String, Bitmap>(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<String> _textureLoading = new HashSet<String>(StringComparer.OrdinalIgnoreCase);
    private View_AREA.PrtPreviewSession _session;
    private View_AREA.PrtPreviewSnapshot _snapshot;
    private Func<String, Bitmap> _textureLoader;
    private Boolean _stepping;
    private Single _zoom = 70f;
    private PointF _pan = PointF.Empty;
    private Boolean _dragging;
    private MouseButtons _dragButton;
    private Point _dragStart;
    private PointF _panStart;
    private Single _yaw = -.65f;
    private Single _pitch = -.32f;
    private Single _distance = 3f;
    private Single _dragYaw, _dragPitch;
    private Boolean _autoFrame3d = true;
    private Int32 _textureGeneration;

    public Func<String, Bitmap> TextureLoader {
      get => _textureLoader;
      set {
        if (ReferenceEquals(_textureLoader, value)) return;
        _textureLoader = value;
        ClearTextureCache();
      }
    }

    public ParticleSimulationControl() {
      BackColor = Color.FromArgb(24, 24, 24);
      _tools = new ToolStrip { Dock = DockStyle.Top, GripStyle = ToolStripGripStyle.Hidden };
      _pause = new ToolStripButton("Pause") { CheckOnClick = true };
      _reset = new ToolStripButton("Reset");
      _textures = new ToolStripButton("Textures") { CheckOnClick = true, Checked = true, ToolTipText = "Render authored DDS sprite frames when available" };
      _grid = new ToolStripButton("Grid") { CheckOnClick = true, Checked = true, ToolTipText = "Show 3D ground grid and axes" };
      _projection = new ToolStripComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 68 };
      _projection.Items.AddRange(new Object[] { "3D", "XY", "XZ", "YZ" });
      _projection.SelectedIndex = 0;
      _projection.SelectedIndexChanged += delegate { ResetView(); _canvas.Invalidate(); };
      _textures.CheckedChanged += delegate { _canvas.Invalidate(); };
      _grid.CheckedChanged += delegate { _canvas.Invalidate(); };
      _reset.Click += delegate { ResetSimulation(); };
      _stats = new ToolStripLabel("No simulation");
      _tools.Items.Add(_pause);
      _tools.Items.Add(_reset);
      _tools.Items.Add(new ToolStripSeparator());
      _tools.Items.Add(new ToolStripLabel("View"));
      _tools.Items.Add(_projection);
      _tools.Items.Add(_textures);
      _tools.Items.Add(_grid);
      _tools.Items.Add(new ToolStripSeparator());
      _tools.Items.Add(_stats);

      _canvas = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(20, 20, 20) };
      _canvas.Paint += CanvasPaint;
      _canvas.MouseWheel += CanvasMouseWheel;
      _canvas.MouseEnter += delegate { if (!_canvas.Focused) _canvas.Focus(); };
      _canvas.MouseDown += CanvasMouseDown;
      _canvas.MouseMove += CanvasMouseMove;
      _canvas.MouseUp += CanvasMouseUp;
      _canvas.DoubleClick += delegate { ResetView(); };
      _canvas.TabStop = true;

      Controls.Add(_canvas);
      Controls.Add(_tools);

      _timer = new System.Windows.Forms.Timer { Interval = 33 };
      _timer.Tick += TimerTick;
      _timer.Start();
    }

    public void SetSession(View_AREA.PrtPreviewSession session) {
      View_AREA.PrtPreviewSession previous = _session;
      _session = session;
      _snapshot = session?.Snapshot();
      if (previous != null && !ReferenceEquals(previous, session)) {
        _ = Task.Run(() => { try { previous.Dispose(); } catch { } });
      }
      ClearTextureCache();
      _pause.Checked = false;
      ResetView();
      UpdateStats();
      _canvas.Invalidate();
    }

    private async void TimerTick(Object sender, EventArgs e) {
      if (_session == null || _pause.Checked || _stepping || !Visible || IsDisposed) return;
      _stepping = true;
      View_AREA.PrtPreviewSession session = _session;
      try {
        View_AREA.PrtPreviewSnapshot next = await Task.Run(() => session.Step(1f / 30f));
        if (!IsDisposed && ReferenceEquals(session, _session) && next != null) {
          _snapshot = next;
          AutoFrame3dIfNeeded(next);
          UpdateStats();
          _canvas.Invalidate();
        }
      }
      catch (ObjectDisposedException) { }
      catch (Exception ex) { _stats.Text = "Simulation: " + ex.Message; }
      finally { _stepping = false; }
    }

    private void ResetSimulation() {
      try { _session?.Reset(); _snapshot = _session?.Snapshot(); } catch { }
      _pause.Checked = false;
      ResetView();
      UpdateStats();
      _canvas.Invalidate();
    }

    private Boolean Is3d => String.Equals(_projection.SelectedItem?.ToString(), "3D", StringComparison.OrdinalIgnoreCase);

    private void ResetView() {
      _zoom = 70f;
      _pan = PointF.Empty;
      _yaw = -.65f;
      _pitch = -.32f;
      _distance = 3f;
      _autoFrame3d = true;
      AutoFrame3dIfNeeded(_snapshot);
      _canvas.Invalidate();
    }

    private void AutoFrame3dIfNeeded(View_AREA.PrtPreviewSnapshot snapshot) {
      if (!_autoFrame3d || snapshot == null || snapshot.Particles.Count == 0) return;
      Single radius = .01f;
      foreach (View_AREA.PrtPreviewParticleState p in snapshot.Particles) {
        Double r = Math.Sqrt((Double)p.X * p.X + (Double)p.Y * p.Y + (Double)p.Z * p.Z) + Math.Max(p.HalfWidth, p.HalfHeight);
        if (Double.IsFinite(r)) radius = Math.Max(radius, (Single)r);
      }
      _distance = Math.Max(.035f, radius * 2.8f);
      _autoFrame3d = false;
    }

    private void UpdateStats() {
      View_AREA.PrtPreviewSnapshot snap = _snapshot;
      if (snap == null) { _stats.Text = "No simulation"; return; }
      Int32 textured = 0;
      foreach (View_AREA.PrtPreviewParticleState p in snap.Particles) {
        if (!String.IsNullOrWhiteSpace(p.TexturePath) && _textureCache.ContainsKey(p.TexturePath)) textured++;
      }
      _stats.Text = String.Format(CultureInfo.InvariantCulture, "{0}  t={1:0.00}s  particles={2}  trails={3}  nested FX={4}  tex={5}/{6}",
        snap.ParticleType ?? "PRT", snap.Time, snap.Particles.Count, snap.Trails.Count, snap.NestedFxCount, textured, _textureCache.Count);
    }

    private void CanvasPaint(Object sender, PaintEventArgs e) {
      Graphics g = e.Graphics;
      g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
      g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighSpeed;
      g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
      g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighSpeed;
      g.Clear(_canvas.BackColor);

      View_AREA.PrtPreviewSnapshot snap = _snapshot;
      if (snap == null) {
        using var brush = new SolidBrush(Color.Silver);
        g.DrawString("No PRT simulation available.", Font, brush, 12, 12);
        return;
      }

      if (Is3d) Draw3d(g, snap);
      else Draw2d(g, snap, _projection.SelectedItem?.ToString() ?? "XY");

      using var legend = new SolidBrush(Color.Silver);
      String help = Is3d
        ? "World PRT runtime • LMB orbit • RMB/MMB pan • wheel dolly • double-click reset"
        : "World PRT runtime • wheel zoom • drag pan • double-click reset";
      g.DrawString(help + "\nDDS sprite frames are decoded from the loaded SWTOR assets; simulation/emission is shared with World Browser.", Font, legend, 10, 10);
    }

    private void Draw2d(Graphics g, View_AREA.PrtPreviewSnapshot snap, String projection) {
      Rectangle bounds = _canvas.ClientRectangle;
      Single ox = bounds.Width * .5f + _pan.X, oy = bounds.Height * .5f + _pan.Y;
      using var axis = new Pen(Color.FromArgb(70, Color.White));
      g.DrawLine(axis, 0, oy, bounds.Width, oy);
      g.DrawLine(axis, ox, 0, ox, bounds.Height);

      foreach (View_AREA.PrtPreviewTrailSegmentState trail in snap.Trails) DrawTrail2d(g, trail, projection, ox, oy);

      foreach (View_AREA.PrtPreviewParticleState p in snap.Particles) {
        Project2d(p, projection, out Single px, out Single py);
        Single x = ox + px * _zoom;
        Single y = oy - py * _zoom;
        Single w = Math.Max(3f, Math.Min(220f, p.HalfWidth * _zoom * 2f));
        Single h = Math.Max(3f, Math.Min(220f, p.HalfHeight * _zoom * 2f));
        DrawParticleSprite(g, p, x, y, w, h, p.Rotation);
      }
    }

    private void Draw3d(Graphics g, View_AREA.PrtPreviewSnapshot snap) {
      AutoFrame3dIfNeeded(snap);
      Rectangle bounds = _canvas.ClientRectangle;
      if (bounds.Width < 2 || bounds.Height < 2) return;
      if (_grid.Checked) Draw3dGrid(g, bounds);

      var projectedTrails = new List<ProjectedTrail>(snap.Trails.Count);
      foreach (View_AREA.PrtPreviewTrailSegmentState trail in snap.Trails) {
        if (TryProjectTrail3d(trail, bounds, out ProjectedTrail projectedTrail)) projectedTrails.Add(projectedTrail);
      }
      projectedTrails.Sort((a, b) => b.Depth.CompareTo(a.Depth));
      foreach (ProjectedTrail trail in projectedTrails) DrawProjectedTrail(g, trail);

      var projected = new List<ProjectedParticle>(snap.Particles.Count);
      foreach (View_AREA.PrtPreviewParticleState p in snap.Particles) {
        if (!Project3d(p.X, p.Y, p.Z, bounds, out Single sx, out Single sy, out Single depth, out Single pixelsPerUnit)) continue;
        Single w = Math.Max(2f, Math.Min(420f, p.HalfWidth * pixelsPerUnit * 2f));
        Single h = Math.Max(2f, Math.Min(420f, p.HalfHeight * pixelsPerUnit * 2f));
        projected.Add(new ProjectedParticle(p, sx, sy, depth, w, h));
      }
      projected.Sort((a, b) => b.Depth.CompareTo(a.Depth)); // painter's algorithm: far to near
      foreach (ProjectedParticle item in projected) DrawParticleSprite(g, item.Particle, item.X, item.Y, item.Width, item.Height, item.Particle.Rotation);
    }

    private void Draw3dGrid(Graphics g, Rectangle bounds) {
      Single radius = Math.Max(.05f, _distance / 2.8f);
      Single rawStep = radius / 5f;
      Single step = NiceGridStep(rawStep);
      Int32 count = Math.Max(2, Math.Min(12, (Int32)Math.Ceiling(radius / step)));
      using var minor = new Pen(Color.FromArgb(34, 180, 180, 180));
      using var xPen = new Pen(Color.FromArgb(150, 230, 90, 90), 1.5f);
      using var yPen = new Pen(Color.FromArgb(150, 90, 220, 110), 1.5f);
      using var zPen = new Pen(Color.FromArgb(150, 90, 150, 240), 1.5f);
      for (Int32 i = -count; i <= count; i++) {
        Single v = i * step;
        Draw3dLine(g, minor, -count * step, v, 0, count * step, v, 0, bounds);
        Draw3dLine(g, minor, v, -count * step, 0, v, count * step, 0, bounds);
      }
      Single axisLen = count * step;
      Draw3dLine(g, xPen, 0, 0, 0, axisLen, 0, 0, bounds);
      Draw3dLine(g, yPen, 0, 0, 0, 0, axisLen, 0, bounds);
      Draw3dLine(g, zPen, 0, 0, 0, 0, 0, axisLen, bounds);
    }

    private void DrawTrail2d(Graphics g, View_AREA.PrtPreviewTrailSegmentState trail, String projection, Single ox, Single oy) {
      ProjectPoint2d(trail.AX, trail.AY, trail.AZ, projection, out Single ax, out Single ay);
      ProjectPoint2d(trail.BX, trail.BY, trail.BZ, projection, out Single bx, out Single by);
      ProjectPoint2d(trail.CX, trail.CY, trail.CZ, projection, out Single cx, out Single cy);
      ProjectPoint2d(trail.DX, trail.DY, trail.DZ, projection, out Single dx, out Single dy);
      PointF[] points = {
        new PointF(ox + ax * _zoom, oy - ay * _zoom), new PointF(ox + bx * _zoom, oy - by * _zoom),
        new PointF(ox + cx * _zoom, oy - cy * _zoom), new PointF(ox + dx * _zoom, oy - dy * _zoom)
      };
      Int32 alpha = Math.Max(10, Math.Min(210, (Int32)(Clamp01(trail.A) * 190f)));
      using var brush = new SolidBrush(Color.FromArgb(alpha, (Int32)(Clamp01(trail.R) * 255f), (Int32)(Clamp01(trail.G) * 255f), (Int32)(Clamp01(trail.B) * 255f)));
      try { g.FillPolygon(brush, points); } catch { }
    }

    private Boolean TryProjectTrail3d(View_AREA.PrtPreviewTrailSegmentState trail, Rectangle bounds, out ProjectedTrail projected) {
      projected = null;
      if (!Project3d(trail.AX, trail.AY, trail.AZ, bounds, out Single ax, out Single ay, out Single ad, out _)) return false;
      if (!Project3d(trail.BX, trail.BY, trail.BZ, bounds, out Single bx, out Single by, out Single bd, out _)) return false;
      if (!Project3d(trail.CX, trail.CY, trail.CZ, bounds, out Single cx, out Single cy, out Single cd, out _)) return false;
      if (!Project3d(trail.DX, trail.DY, trail.DZ, bounds, out Single dx, out Single dy, out Single dd, out _)) return false;
      projected = new ProjectedTrail(trail, new[] { new PointF(ax, ay), new PointF(bx, by), new PointF(cx, cy), new PointF(dx, dy) }, (ad + bd + cd + dd) * .25f);
      return true;
    }

    private void DrawProjectedTrail(Graphics g, ProjectedTrail projected) {
      View_AREA.PrtPreviewTrailSegmentState trail = projected.Trail;
      Int32 alpha = Math.Max(10, Math.Min(210, (Int32)(Clamp01(trail.A) * 190f)));
      using var brush = new SolidBrush(Color.FromArgb(alpha, (Int32)(Clamp01(trail.R) * 255f), (Int32)(Clamp01(trail.G) * 255f), (Int32)(Clamp01(trail.B) * 255f)));
      try { g.FillPolygon(brush, projected.Points); } catch { }
      if (_textures.Checked && !String.IsNullOrWhiteSpace(trail.TexturePath)) QueueTextureLoad(trail.TexturePath);
    }

    private static Single NiceGridStep(Single raw) {
      if (!(raw > 0f) || Single.IsNaN(raw) || Single.IsInfinity(raw)) return 1f;
      Double power = Math.Pow(10, Math.Floor(Math.Log10(raw)));
      Double n = raw / power;
      Double step = n < 2 ? 1 : n < 5 ? 2 : 5;
      return (Single)(step * power);
    }

    private void Draw3dLine(Graphics g, Pen pen, Single ax, Single ay, Single az, Single bx, Single by, Single bz, Rectangle bounds) {
      if (!Project3d(ax, ay, az, bounds, out Single x1, out Single y1, out _, out _)) return;
      if (!Project3d(bx, by, bz, bounds, out Single x2, out Single y2, out _, out _)) return;
      g.DrawLine(pen, x1, y1, x2, y2);
    }

    private Boolean Project3d(Single x, Single y, Single z, Rectangle bounds, out Single screenX, out Single screenY, out Single depth, out Single pixelsPerUnit) {
      Single cy = (Single)Math.Cos(_yaw), sy = (Single)Math.Sin(_yaw);
      Single cp = (Single)Math.Cos(_pitch), sp = (Single)Math.Sin(_pitch);
      Single x1 = cy * x - sy * y;
      Single y1 = sy * x + cy * y;
      Single z1 = z;
      Single y2 = cp * y1 - sp * z1;
      Single z2 = sp * y1 + cp * z1;
      depth = _distance + y2;
      Single near = Math.Max(.0005f, _distance * .015f);
      if (!(depth > near) || Single.IsNaN(depth) || Single.IsInfinity(depth)) {
        screenX = screenY = pixelsPerUnit = 0f;
        return false;
      }
      const Single fov = 50f * (Single)Math.PI / 180f;
      Single focal = Math.Max(1f, bounds.Height * .5f) / (Single)Math.Tan(fov * .5f);
      pixelsPerUnit = focal / depth;
      screenX = bounds.Width * .5f + _pan.X + x1 * pixelsPerUnit;
      screenY = bounds.Height * .5f + _pan.Y - z2 * pixelsPerUnit;
      return Single.IsFinite(screenX) && Single.IsFinite(screenY) && Single.IsFinite(pixelsPerUnit);
    }

    private void DrawParticleSprite(Graphics g, View_AREA.PrtPreviewParticleState p, Single x, Single y, Single width, Single height, Single rotationRadians) {
      if (width < .5f || height < .5f || x < -width || y < -height || x > _canvas.Width + width || y > _canvas.Height + height) return;
      Bitmap texture = null;
      if (_textures.Checked && !String.IsNullOrWhiteSpace(p.TexturePath)) {
        _textureCache.TryGetValue(p.TexturePath, out texture);
        if (texture == null) QueueTextureLoad(p.TexturePath);
      }

      Int32 alpha = Math.Max(8, Math.Min(255, (Int32)(Clamp01(p.A) * 235f)));
      Int32 red = Math.Max(0, Math.Min(255, (Int32)(Clamp01(p.R) * 255f)));
      Int32 green = Math.Max(0, Math.Min(255, (Int32)(Clamp01(p.G) * 255f)));
      Int32 blue = Math.Max(0, Math.Min(255, (Int32)(Clamp01(p.B) * 255f)));

      if (texture == null || texture.Width <= 0 || texture.Height <= 0) {
        using var fill = new SolidBrush(Color.FromArgb(alpha, red, green, blue));
        using var outline = new Pen(Color.FromArgb(Math.Min(255, alpha + 25), red, green, blue));
        g.FillEllipse(fill, x - width * .5f, y - height * .5f, width, height);
        g.DrawEllipse(outline, x - width * .5f, y - height * .5f, width, height);
        return;
      }

      Int32 columns = Math.Max(1, p.Columns), rows = Math.Max(1, p.Rows);
      Int32 total = Math.Max(1, columns * rows), frame = Math.Max(0, Math.Min(total - 1, p.Frame));
      Int32 sourceWidth = Math.Max(1, texture.Width / columns), sourceHeight = Math.Max(1, texture.Height / rows);
      Int32 sourceX = Math.Min(texture.Width - sourceWidth, (frame % columns) * sourceWidth);
      Int32 sourceY = Math.Min(texture.Height - sourceHeight, (frame / columns) * sourceHeight);

      System.Drawing.Drawing2D.GraphicsState state = g.Save();
      try {
        g.TranslateTransform(x, y);
        if (Math.Abs(rotationRadians) > .0001f) g.RotateTransform(rotationRadians * 180f / (Single)Math.PI);
        RectangleF destination = new RectangleF(-width * .5f, -height * .5f, width, height);
        // The Graphics.DrawImage overload that accepts ImageAttributes requires an integer
        // destination Rectangle. Keep the layout calculations in floating point, then round only
        // at the GDI+ boundary.
        Rectangle destinationPixels = Rectangle.Round(destination);
        using var attributes = new System.Drawing.Imaging.ImageAttributes();
        var matrix = new System.Drawing.Imaging.ColorMatrix();
        matrix.Matrix00 = Clamp01(p.R);
        matrix.Matrix11 = Clamp01(p.G);
        matrix.Matrix22 = Clamp01(p.B);
        matrix.Matrix33 = Clamp01(p.A);
        attributes.SetColorMatrix(matrix, System.Drawing.Imaging.ColorMatrixFlag.Default, System.Drawing.Imaging.ColorAdjustType.Bitmap);
        g.DrawImage(texture, destinationPixels, sourceX, sourceY, sourceWidth, sourceHeight, GraphicsUnit.Pixel, attributes);
      }
      catch { }
      finally { g.Restore(state); }
    }

    private static Single Clamp01(Single value) => value < 0f ? 0f : value > 1f ? 1f : value;

    private void QueueTextureLoad(String path) {
      Func<String, Bitmap> loader = _textureLoader;
      if (loader == null || String.IsNullOrWhiteSpace(path) || _textureCache.ContainsKey(path) || _textureLoading.Contains(path)) return;
      if (_textureCache.Count + _textureLoading.Count >= 48) return;
      _textureLoading.Add(path);
      Int32 generation = _textureGeneration;
      _ = Task.Run(() => {
        Bitmap bitmap = null;
        try { bitmap = loader(path); } catch { }
        if (IsDisposed) { bitmap?.Dispose(); return; }
        try {
          BeginInvoke((Action)(() => {
            _textureLoading.Remove(path);
            if (IsDisposed || generation != _textureGeneration) { bitmap?.Dispose(); return; }
            if (bitmap != null) {
              if (_textureCache.TryGetValue(path, out Bitmap old)) old?.Dispose();
              _textureCache[path] = bitmap;
            }
            UpdateStats();
            _canvas.Invalidate();
          }));
        }
        catch { bitmap?.Dispose(); }
      });
    }

    private void ClearTextureCache() {
      _textureGeneration++;
      _textureLoading.Clear();
      foreach (Bitmap bitmap in _textureCache.Values) try { bitmap?.Dispose(); } catch { }
      _textureCache.Clear();
    }

    private static void Project2d(View_AREA.PrtPreviewParticleState p, String projection, out Single x, out Single y) {
      ProjectPoint2d(p.X, p.Y, p.Z, projection, out x, out y);
    }

    private static void ProjectPoint2d(Single px, Single py, Single pz, String projection, out Single x, out Single y) {
      switch (projection) {
        case "XZ": x = px; y = pz; break;
        case "YZ": x = pz; y = py; break;
        default: x = px; y = py; break;
      }
    }

    private void CanvasMouseWheel(Object sender, MouseEventArgs e) {
      if (Is3d) {
        Single factor = e.Delta > 0 ? 1f / 1.14f : 1.14f;
        _distance = Math.Max(.002f, Math.Min(100000f, _distance * factor));
        _autoFrame3d = false;
        _canvas.Invalidate();
        return;
      }
      Single oldZoom = _zoom;
      Single factor2 = e.Delta > 0 ? 1.15f : 1f / 1.15f;
      _zoom = Math.Max(4f, Math.Min(1200f, _zoom * factor2));
      if (Math.Abs(_zoom - oldZoom) < .001f) return;
      Single cx = _canvas.ClientSize.Width * .5f + _pan.X;
      Single cy = _canvas.ClientSize.Height * .5f + _pan.Y;
      Single wx = (e.X - cx) / oldZoom, wy = (e.Y - cy) / oldZoom;
      _pan.X = e.X - _canvas.ClientSize.Width * .5f - wx * _zoom;
      _pan.Y = e.Y - _canvas.ClientSize.Height * .5f - wy * _zoom;
      _canvas.Invalidate();
    }

    private void CanvasMouseDown(Object sender, MouseEventArgs e) {
      if (e.Button != MouseButtons.Left && e.Button != MouseButtons.Right && e.Button != MouseButtons.Middle) return;
      _dragging = true;
      _dragButton = e.Button;
      _dragStart = e.Location;
      _panStart = _pan;
      _dragYaw = _yaw;
      _dragPitch = _pitch;
      _canvas.Focus();
      _canvas.Capture = true;
    }

    private void CanvasMouseMove(Object sender, MouseEventArgs e) {
      if (!_dragging) return;
      Int32 dx = e.X - _dragStart.X, dy = e.Y - _dragStart.Y;
      if (Is3d && _dragButton == MouseButtons.Left) {
        _yaw = _dragYaw + dx * .009f;
        _pitch = Math.Max(-1.45f, Math.Min(1.45f, _dragPitch + dy * .009f));
        _autoFrame3d = false;
      } else {
        _pan = new PointF(_panStart.X + dx, _panStart.Y + dy);
      }
      _canvas.Invalidate();
    }

    private void CanvasMouseUp(Object sender, MouseEventArgs e) {
      if (!_dragging || e.Button != _dragButton) return;
      _dragging = false;
      _canvas.Capture = false;
    }

    protected override void Dispose(Boolean disposing) {
      if (disposing) {
        try { _timer?.Stop(); _timer?.Dispose(); } catch { }
        try { _session?.Dispose(); } catch { }
        _session = null;
        ClearTextureCache();
      }
      base.Dispose(disposing);
    }

    private sealed class ProjectedTrail {
      public readonly View_AREA.PrtPreviewTrailSegmentState Trail;
      public readonly PointF[] Points;
      public readonly Single Depth;
      public ProjectedTrail(View_AREA.PrtPreviewTrailSegmentState trail, PointF[] points, Single depth) { Trail = trail; Points = points; Depth = depth; }
    }

    private sealed class ProjectedParticle {
      public readonly View_AREA.PrtPreviewParticleState Particle;
      public readonly Single X, Y, Depth, Width, Height;
      public ProjectedParticle(View_AREA.PrtPreviewParticleState particle, Single x, Single y, Single depth, Single width, Single height) {
        Particle = particle; X = x; Y = y; Depth = depth; Width = width; Height = height;
      }
    }
  }

  internal sealed class EffectValueGraphControl : Control {
    private String _valueText;
    public String Caption { get; set; }
    public String ValueText { get => _valueText; set { _valueText = value; Invalidate(); } }

    public EffectValueGraphControl() { DoubleBuffered = true; ForeColor = Color.Gainsboro; }

    protected override void OnPaint(PaintEventArgs e) {
      base.OnPaint(e);
      Graphics g = e.Graphics;
      g.Clear(BackColor);
      Rectangle bounds = ClientRectangle;
      if (bounds.Width < 20 || bounds.Height < 20) return;
      using var captionBrush = new SolidBrush(Color.Gainsboro);
      using var mutedBrush = new SolidBrush(Color.Silver);
      g.DrawString(Caption ?? "Value", Font, captionBrush, 8, 8);
      String value = ValueText ?? String.Empty;
      Rectangle plot = new Rectangle(12, 38, Math.Max(10, bounds.Width - 24), Math.Max(10, bounds.Height - 80));

      List<ColorStop> colors = ParseColorStops(value);
      if (colors.Count > 0) {
        DrawColorGradient(g, plot, colors);
        g.DrawString(value, Font, mutedBrush, 12, Math.Max(42, bounds.Height - 34));
        return;
      }

      List<PointF> points = ParseScalarTrack(value);
      if (points.Count >= 2) {
        DrawTrack(g, plot, points);
        g.DrawString("x = normalized lifetime / authored track time", Font, mutedBrush, 12, Math.Max(42, bounds.Height - 34));
        return;
      }

      Match range = Regex.Match(value, @"<\s*(?<lo>[-+0-9.eE]+)\s*,\s*(?<hi>[-+0-9.eE]+)(?:\s*,\s*(?<var>[-+0-9.eE]+))?(?:\s*,\s*(?<med>[-+0-9.eE]+))?\s*>");
      if (range.Success && TryNumber(range.Groups["lo"].Value, out Double lo) && TryNumber(range.Groups["hi"].Value, out Double hi)) {
        Double med = TryNumber(range.Groups["med"].Value, out Double parsedMed) ? parsedMed : (lo + hi) * 0.5;
        DrawRange(g, plot, lo, hi, med);
        g.DrawString("Randomizer: min " + lo.ToString("0.###", CultureInfo.InvariantCulture) + "   median " + med.ToString("0.###", CultureInfo.InvariantCulture) + "   max " + hi.ToString("0.###", CultureInfo.InvariantCulture), Font, mutedBrush, 12, Math.Max(42, bounds.Height - 34));
        return;
      }

      using var valueFont = new Font(FontFamily.GenericMonospace, Math.Max(9f, Font.Size));
      g.DrawString(value, valueFont, captionBrush, new RectangleF(12, 46, bounds.Width - 24, bounds.Height - 58));
    }

    private static void DrawTrack(Graphics g, Rectangle plot, List<PointF> points) {
      Single minX = points.Min(p => p.X), maxX = points.Max(p => p.X);
      Single minY = points.Min(p => p.Y), maxY = points.Max(p => p.Y);
      if (Math.Abs(maxX - minX) < 0.00001f) { minX -= 1; maxX += 1; }
      if (Math.Abs(maxY - minY) < 0.00001f) { minY -= 1; maxY += 1; }
      using var border = new Pen(Color.DimGray);
      using var axis = new Pen(Color.FromArgb(90, Color.Silver)) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash };
      using var line = new Pen(Color.DeepSkyBlue, 2f);
      using var dot = new SolidBrush(Color.WhiteSmoke);
      g.DrawRectangle(border, plot);
      if (minY < 0 && maxY > 0) {
        Single zy = plot.Bottom - (Single)((0 - minY) / (maxY - minY)) * plot.Height;
        g.DrawLine(axis, plot.Left, zy, plot.Right, zy);
      }
      PointF[] screen = points.Select(p => new PointF(
        plot.Left + (p.X - minX) / (maxX - minX) * plot.Width,
        plot.Bottom - (p.Y - minY) / (maxY - minY) * plot.Height)).ToArray();
      if (screen.Length >= 2) g.DrawLines(line, screen);
      foreach (PointF p in screen) g.FillEllipse(dot, p.X - 2, p.Y - 2, 4, 4);
      using var label = new SolidBrush(Color.Silver);
      g.DrawString(maxY.ToString("0.###", CultureInfo.InvariantCulture), SystemFonts.SmallCaptionFont, label, plot.Left + 3, plot.Top + 3);
      g.DrawString(minY.ToString("0.###", CultureInfo.InvariantCulture), SystemFonts.SmallCaptionFont, label, plot.Left + 3, plot.Bottom - 16);
    }

    private static void DrawColorGradient(Graphics g, Rectangle plot, List<ColorStop> stops) {
      Rectangle gradient = new Rectangle(plot.Left, plot.Top + Math.Max(0, plot.Height / 3), plot.Width, Math.Max(30, plot.Height / 3));
      using var border = new Pen(Color.DimGray);
      g.DrawRectangle(border, gradient);
      for (Int32 x = 0; x < gradient.Width; x++) {
        Single t = gradient.Width <= 1 ? 0 : (Single)x / (gradient.Width - 1);
        Color c = InterpolateColor(stops, t);
        using var pen = new Pen(c);
        g.DrawLine(pen, gradient.Left + x, gradient.Top + 1, gradient.Left + x, gradient.Bottom - 1);
      }
      using var text = new SolidBrush(Color.Silver);
      g.DrawString("0", SystemFonts.SmallCaptionFont, text, gradient.Left, gradient.Bottom + 4);
      g.DrawString("1", SystemFonts.SmallCaptionFont, text, gradient.Right - 10, gradient.Bottom + 4);
    }

    private static void DrawRange(Graphics g, Rectangle plot, Double lo, Double hi, Double med) {
      if (hi < lo) { Double swap = lo; lo = hi; hi = swap; }
      Double span = Math.Max(0.000001, hi - lo);
      Single y = plot.Top + plot.Height / 2f;
      using var line = new Pen(Color.DeepSkyBlue, 5f);
      using var tick = new Pen(Color.WhiteSmoke, 2f);
      g.DrawLine(line, plot.Left + 12, y, plot.Right - 12, y);
      g.DrawLine(tick, plot.Left + 12, y - 10, plot.Left + 12, y + 10);
      g.DrawLine(tick, plot.Right - 12, y - 10, plot.Right - 12, y + 10);
      Single mx = plot.Left + 12 + (Single)((med - lo) / span) * (plot.Width - 24);
      mx = Math.Max(plot.Left + 12, Math.Min(plot.Right - 12, mx));
      g.DrawLine(tick, mx, y - 16, mx, y + 16);
    }

    private static List<PointF> ParseScalarTrack(String value) {
      var result = new List<PointF>();
      foreach (Match m in Regex.Matches(value ?? String.Empty, @"\[\s*(?<t>[-+0-9.eE]+)\s*:\s*(?<v>[-+0-9.eE]+)(?:\s*:[^\]]*)?\]")) {
        if (TryNumber(m.Groups["t"].Value, out Double t) && TryNumber(m.Groups["v"].Value, out Double v)) result.Add(new PointF((Single)t, (Single)v));
      }
      return result;
    }

    private static List<ColorStop> ParseColorStops(String value) {
      var colors = new List<ColorStop>();
      MatchCollection timed = Regex.Matches(value ?? String.Empty, @"\[\s*(?<t>[-+0-9.eE]+)\s*:\s*(?<c>#[0-9a-fA-F]{6,8})[^\]]*\]");
      foreach (Match m in timed) if (TryNumber(m.Groups["t"].Value, out Double t) && TryColor(m.Groups["c"].Value, out Color c)) colors.Add(new ColorStop((Single)t, c));
      if (colors.Count > 0) return colors.OrderBy(x => x.T).ToList();
      MatchCollection plain = Regex.Matches(value ?? String.Empty, @"#[0-9a-fA-F]{6,8}");
      for (Int32 i = 0; i < plain.Count; i++) if (TryColor(plain[i].Value, out Color c)) colors.Add(new ColorStop(plain.Count <= 1 ? 0 : (Single)i / (plain.Count - 1), c));
      return colors;
    }

    private static Boolean TryColor(String value, out Color color) {
      color = Color.Transparent;
      String s = (value ?? String.Empty).Trim().TrimStart('#');
      try {
        if (s.Length == 6) { color = Color.FromArgb(255, Convert.ToInt32(s.Substring(0, 2), 16), Convert.ToInt32(s.Substring(2, 2), 16), Convert.ToInt32(s.Substring(4, 2), 16)); return true; }
        if (s.Length == 8) { color = Color.FromArgb(Convert.ToInt32(s.Substring(0, 2), 16), Convert.ToInt32(s.Substring(2, 2), 16), Convert.ToInt32(s.Substring(4, 2), 16), Convert.ToInt32(s.Substring(6, 2), 16)); return true; }
      } catch { }
      return false;
    }

    private static Color InterpolateColor(List<ColorStop> stops, Single t) {
      if (stops.Count == 0) return Color.Transparent;
      if (stops.Count == 1 || t <= stops[0].T) return stops[0].Color;
      if (t >= stops[stops.Count - 1].T) return stops[stops.Count - 1].Color;
      for (Int32 i = 0; i < stops.Count - 1; i++) {
        ColorStop a = stops[i], b = stops[i + 1];
        if (t < a.T || t > b.T) continue;
        Single u = Math.Abs(b.T - a.T) < 0.00001f ? 0 : (t - a.T) / (b.T - a.T);
        return Color.FromArgb(
          (Int32)(a.Color.A + (b.Color.A - a.Color.A) * u),
          (Int32)(a.Color.R + (b.Color.R - a.Color.R) * u),
          (Int32)(a.Color.G + (b.Color.G - a.Color.G) * u),
          (Int32)(a.Color.B + (b.Color.B - a.Color.B) * u));
      }
      return stops[stops.Count - 1].Color;
    }

    private static Boolean TryNumber(String value, out Double number) => Double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number);
    private readonly struct ColorStop { public readonly Single T; public readonly Color Color; public ColorStop(Single t, Color color) { T = t; Color = color; } }
  }
}
