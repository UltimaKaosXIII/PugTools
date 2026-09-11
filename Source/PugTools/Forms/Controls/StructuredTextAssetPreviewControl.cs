using System;
using System.Collections.Generic;
using System.Data;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using System.Xml;

namespace PugTools {
  /// <summary>
  /// Database-free structured views for SWTOR text assets where Jedipedia exposes a table rather than raw XML.
  /// The control intentionally keeps a Raw tab and caps very large tables so selecting a file cannot stall WinForms.
  /// </summary>
  internal sealed class StructuredTextAssetPreviewControl : UserControl {
    private const Int32 VisibleRowLimit = 5000;
    private readonly ToolStrip _toolbar;
    private readonly ToolStripLabel _title;
    private readonly ToolStripLabel _summary;
    private readonly TabControl _tabs;
    private readonly TextBox _raw;
    private readonly Dictionary<String, Boolean> _existsCache = new Dictionary<String, Boolean>(StringComparer.OrdinalIgnoreCase);

    public Func<String, Boolean> ResourceExists { get; set; }
    public Action<String> OpenResourceRequested { get; set; }

    public StructuredTextAssetPreviewControl() {
      Dock = DockStyle.Fill;
      BackColor = SystemColors.Window;
      _toolbar = new ToolStrip { Dock = DockStyle.Top, GripStyle = ToolStripGripStyle.Hidden };
      _title = new ToolStripLabel { Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold) };
      _summary = new ToolStripLabel();
      _toolbar.Items.Add(_title);
      _toolbar.Items.Add(new ToolStripSeparator());
      _toolbar.Items.Add(_summary);
      _tabs = new TabControl { Dock = DockStyle.Fill };
      _raw = new TextBox {
        Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, WordWrap = false,
        ScrollBars = ScrollBars.Both, Font = new Font(FontFamily.GenericMonospace, 9f)
      };
      Controls.Add(_tabs);
      Controls.Add(_toolbar);
    }

    public void ClearPreview() {
      _title.Text = String.Empty;
      _summary.Text = String.Empty;
      _tabs.TabPages.Clear();
      _raw.Clear();
      _existsCache.Clear();
    }

    public void LoadManifest(String sourcePath, String text) {
      ClearPreview();
      _raw.Text = text ?? String.Empty;
      var files = new List<String>();
      try {
        var doc = new XmlDocument();
        doc.LoadXml(text ?? String.Empty);
        foreach (XmlNode node in doc.GetElementsByTagName("file")) {
          String value = node.Attributes?["val"]?.Value;
          if (!String.IsNullOrWhiteSpace(value)) files.Add(value.Trim());
        }
      } catch { }

      _title.Text = "MANIFEST";
      _summary.Text = files.Count.ToString("n0", CultureInfo.InvariantCulture) + " entries";
      var grid = CreateGrid();
      grid.Columns.Add("name", "String table / file");
      grid.Columns.Add("de", "de-de");
      grid.Columns.Add("en", "en-us");
      grid.Columns.Add("fr", "fr-fr");
      grid.Columns[0].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
      grid.Columns[1].Width = grid.Columns[2].Width = grid.Columns[3].Width = 75;
      grid.Tag = "manifest";
      foreach (String file in files.Take(VisibleRowLimit)) {
        String path = file.Replace('.', '/');
        String de = "/resources/de-de/" + path + ".stb";
        String en = "/resources/en-us/" + path + ".stb";
        String fr = "/resources/fr-fr/" + path + ".stb";
        Int32 row = grid.Rows.Add(file, ExistsMark(de), ExistsMark(en), ExistsMark(fr));
        grid.Rows[row].Tag = new[] { de, en, fr };
      }
      grid.CellDoubleClick += ManifestCellDoubleClick;
      AddTab("Files", grid);
      AddRawTab();
      if (files.Count > VisibleRowLimit) _summary.Text += "   showing first " + VisibleRowLimit.ToString("n0", CultureInfo.InvariantCulture);
    }

    public void LoadTbl(String sourcePath, String text) {
      ClearPreview();
      _raw.Text = text ?? String.Empty;
      _title.Text = "TBL DataTable";
      try {
        var doc = new XmlDocument();
        doc.LoadXml(text ?? String.Empty);
        XmlElement root = doc.DocumentElement;
        if (root == null || !root.Name.Equals("DataTable", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("DataTable root not found.");
        String fqn = root.GetAttribute("fqn"), guid = root.GetAttribute("GUID");
        if (String.IsNullOrWhiteSpace(guid)) guid = root.GetAttribute("guid");
        String assetVersion = root.GetAttribute("assetVersion");

        XmlNodeList columnNodes = root.SelectNodes("./Table/Columns/Column");
        XmlNodeList rowNodes = root.SelectNodes("./Table/Rows/Row");
        var columns = new List<TblColumn>();
        if (columnNodes != null) {
          foreach (XmlElement col in columnNodes.OfType<XmlElement>()) {
            var constraints = new List<String>();
            XmlNodeList constraintNodes = col.SelectNodes("./Constraints/*");
            if (constraintNodes != null) foreach (XmlNode c in constraintNodes) constraints.Add(c.Name);
            columns.Add(new TblColumn {
              Name = col.GetAttribute("name"), Type = col.GetAttribute("type"), RefType = col.GetAttribute("refType"),
              Position = col.GetAttribute("position"), Primary = col.GetAttribute("primaryKey").Equals("True", StringComparison.OrdinalIgnoreCase),
              Constraints = String.Join(", ", constraints)
            });
          }
        }

        var table = CreateGrid();
        for (Int32 columnIndex = 0; columnIndex < columns.Count; columnIndex++) {
          TblColumn col = columns[columnIndex];
          String header = String.IsNullOrWhiteSpace(col.Name) ? "Column " + columnIndex.ToString(CultureInfo.InvariantCulture) : col.Name;
          Int32 index = table.Columns.Add("c" + columnIndex.ToString(CultureInfo.InvariantCulture), header + (col.Primary ? " *" : String.Empty));
          table.Columns[index].MinimumWidth = 70;
          if (col.Primary) table.Columns[index].DefaultCellStyle.BackColor = Color.FromArgb(225, 245, 225);
        }
        if (table.Columns.Count > 0) table.Columns[0].Frozen = true;
        Int32 totalRows = rowNodes?.Count ?? 0;
        if (rowNodes != null) {
          Int32 visible = 0;
          foreach (XmlElement row in rowNodes.OfType<XmlElement>()) {
            if (visible++ >= VisibleRowLimit) break;
            Object[] values = new Object[columns.Count];
            for (Int32 i = 0; i < columns.Count; i++) values[i] = row[columns[i].Name]?.InnerText ?? String.Empty;
            table.Rows.Add(values);
          }
        }
        table.Tag = columns;
        table.CellDoubleClick += TblCellDoubleClick;

        var schema = CreateGrid();
        schema.Columns.Add("position", "Pos");
        schema.Columns.Add("name", "Name");
        schema.Columns.Add("type", "Type");
        schema.Columns.Add("primary", "Primary");
        schema.Columns.Add("ref", "Ref type");
        schema.Columns.Add("constraints", "Constraints");
        schema.Columns[1].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
        schema.Columns[5].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
        foreach (TblColumn col in columns) schema.Rows.Add(col.Position, col.Name, col.Type, col.Primary ? "yes" : String.Empty, col.RefType, col.Constraints);

        _summary.Text = (String.IsNullOrWhiteSpace(fqn) ? String.Empty : fqn + "   ")
          + "GUID " + (String.IsNullOrWhiteSpace(guid) ? "?" : guid)
          + (String.IsNullOrWhiteSpace(assetVersion) ? String.Empty : "   asset v" + assetVersion)
          + "   " + columns.Count.ToString("n0", CultureInfo.InvariantCulture) + " cols × " + totalRows.ToString("n0", CultureInfo.InvariantCulture) + " rows";
        if (totalRows > VisibleRowLimit) _summary.Text += "   showing first " + VisibleRowLimit.ToString("n0", CultureInfo.InvariantCulture);
        AddTab("Table", table);
        AddTab("Schema", schema);
      }
      catch (Exception ex) {
        _summary.Text = "Structured parse failed: " + ex.Message;
      }
      AddRawTab();
    }

    public void LoadRul(String sourcePath, String text) {
      ClearPreview();
      _raw.Text = text ?? String.Empty;
      _title.Text = "RUL dynamic rules";
      Int32 ruleCount = 0, exclusionCount = 0, groupCount = 0;
      try {
        var doc = new XmlDocument();
        doc.LoadXml(text ?? String.Empty);
        var rules = CreateGrid();
        rules.Columns.Add("slot", "Slot");
        rules.Columns.Add("archetype", "Archetype");
        rules.Columns.Add("attachment", "Attachment name");
        rules.Columns.Add("tags", "Tags");
        rules.Columns[3].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
        foreach (XmlElement element in doc.GetElementsByTagName("Rule").OfType<XmlElement>()) {
          String slot = element.GetAttribute("Slot");
          Int32 row = rules.Rows.Add(slot, element.GetAttribute("Archetype"), element.GetAttribute("AttachmentName"), element.GetAttribute("Tags"));
          if (!String.IsNullOrWhiteSpace(slot)) rules.Rows[row].Tag = "/resources/art/dynamic/" + slot.Trim('/').Replace('\\', '/') + "/index.xml";
          ruleCount++;
        }
        rules.CellDoubleClick += RulRuleCellDoubleClick;

        var exclusions = CreateGrid();
        exclusions.Columns.Add("excluded", "Excluded tag");
        exclusions.Columns.Add("tags", "Tags");
        exclusions.Columns[1].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
        foreach (XmlElement element in doc.GetElementsByTagName("TagExclusion").OfType<XmlElement>()) {
          exclusions.Rows.Add(element.GetAttribute("ExcludedTag"), element.GetAttribute("Tags"));
          exclusionCount++;
        }

        var groups = CreateGrid();
        groups.Columns.Add("name", "Name");
        groups.Columns.Add("tags", "Tags");
        groups.Columns[1].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
        foreach (XmlElement element in doc.GetElementsByTagName("Group").OfType<XmlElement>()) {
          groups.Rows.Add(element.GetAttribute("Name"), element.GetAttribute("Tags"));
          groupCount++;
        }
        AddTab("Rules", rules);
        AddTab("Tag exclusions", exclusions);
        AddTab("Groups", groups);
        _summary.Text = ruleCount.ToString("n0", CultureInfo.InvariantCulture) + " rules   "
          + exclusionCount.ToString("n0", CultureInfo.InvariantCulture) + " exclusions   "
          + groupCount.ToString("n0", CultureInfo.InvariantCulture) + " groups";
      }
      catch (Exception ex) { _summary.Text = "Structured parse failed: " + ex.Message; }
      AddRawTab();
    }

    public void LoadLst(String sourcePath, String text) {
      ClearPreview();
      _raw.Text = text ?? String.Empty;
      _title.Text = "LST Scaleform GUI list";

      String baseDirectory = "/resources/guixml/";
      if (!String.IsNullOrWhiteSpace(sourcePath)) {
        String normalizedSource = sourcePath.Replace('\\', '/');
        Int32 slash = normalizedSource.LastIndexOf('/');
        if (slash >= 0) baseDirectory = normalizedSource.Substring(0, slash + 1);
      }

      List<String> files = (text ?? String.Empty)
        .Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.RemoveEmptyEntries)
        .Select(x => x.Trim())
        .Where(x => !String.IsNullOrWhiteSpace(x))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

      var grid = CreateGrid();
      grid.Columns.Add("file", "GUI XML file");
      grid.Columns.Add("exists", "Present");
      grid.Columns.Add("path", "Resource path");
      grid.Columns[0].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
      grid.Columns[2].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;

      foreach (String file in files.Take(VisibleRowLimit)) {
        String path = (baseDirectory + file.TrimStart('/', '\\')).Replace('\\', '/').ToLowerInvariant();
        Int32 row = grid.Rows.Add(file, ExistsMark(path), path);
        grid.Rows[row].Tag = path;
      }
      grid.CellDoubleClick += ResourcePathRowDoubleClick;
      AddTab("Files", grid);
      AddRawTab();

      _summary.Text = files.Count.ToString("n0", CultureInfo.InvariantCulture) + " GUI XML files";
      if (files.Count > VisibleRowLimit)
        _summary.Text += "   showing first " + VisibleRowLimit.ToString("n0", CultureInfo.InvariantCulture);
    }

    public void LoadIni(String sourcePath, String text) {
      ClearPreview();
      _raw.Text = text ?? String.Empty;
      _title.Text = "INI keybindings";

      var rows = new List<IniRow>();
      String section = String.Empty;
      Int32 comments = 0;
      foreach (String rawLine in (text ?? String.Empty).Replace("\0", String.Empty)
                 .Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None)) {
        String line = rawLine.Trim();
        if (line.Length == 0) continue;
        if (line.StartsWith(";", StringComparison.Ordinal) || line.StartsWith("#", StringComparison.Ordinal)
            || line.StartsWith("//", StringComparison.Ordinal)) {
          comments++;
          continue;
        }
        if (line.Length >= 2 && line[0] == '[' && line[line.Length - 1] == ']') {
          section = line.Substring(1, line.Length - 2).Trim();
          continue;
        }

        Int32 equals = line.IndexOf('=');
        String key = equals >= 0 ? line.Substring(0, equals).Trim() : line;
        String value = equals >= 0 ? line.Substring(equals + 1).Trim() : String.Empty;
        String device = String.Empty, control = String.Empty;
        SplitBinding(value, out device, out control);
        rows.Add(new IniRow { Section = section, Key = key, Value = value, Device = device, Control = control });
      }

      var bindings = CreateGrid();
      bindings.Columns.Add("section", "Section");
      bindings.Columns.Add("key", "Command / key");
      bindings.Columns.Add("value", "Binding / value");
      bindings.Columns.Add("device", "Device");
      bindings.Columns.Add("control", "Control");
      bindings.Columns[1].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
      bindings.Columns[2].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
      foreach (IniRow row in rows.Take(VisibleRowLimit))
        bindings.Rows.Add(row.Section, row.Key, row.Value, row.Device, row.Control);
      AddTab("Bindings", bindings);

      var sections = CreateGrid();
      sections.Columns.Add("section", "Section");
      sections.Columns.Add("entries", "Entries");
      sections.Columns[0].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
      foreach (var group in rows.GroupBy(x => x.Section ?? String.Empty, StringComparer.OrdinalIgnoreCase)
                                .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
        sections.Rows.Add(String.IsNullOrWhiteSpace(group.Key) ? "(global)" : group.Key, group.Count());
      AddTab("Sections", sections);
      AddRawTab();

      _summary.Text = rows.Count.ToString("n0", CultureInfo.InvariantCulture) + " entries   "
        + rows.Select(x => x.Section ?? String.Empty).Distinct(StringComparer.OrdinalIgnoreCase).Count().ToString("n0", CultureInfo.InvariantCulture)
        + " sections" + (comments > 0 ? "   " + comments.ToString("n0", CultureInfo.InvariantCulture) + " comments" : String.Empty);
      if (rows.Count > VisibleRowLimit)
        _summary.Text += "   showing first " + VisibleRowLimit.ToString("n0", CultureInfo.InvariantCulture);
    }

    public void LoadLod(String sourcePath, String text) {
      ClearPreview();
      _raw.Text = text ?? String.Empty;
      _title.Text = "LOD schemas";
      try {
        var doc = new XmlDocument();
        doc.LoadXml(text ?? String.Empty);
        var schemas = new List<LodSchemaRow>();
        XmlNodeList schemaNodes = doc.SelectNodes("//*[local-name()='LODSchema' or local-name()='lodschema']");
        if (schemaNodes != null) {
          foreach (XmlElement schema in schemaNodes.OfType<XmlElement>()) {
            String name = AttributeIgnoreCase(schema, "name");
            if (String.IsNullOrWhiteSpace(name)) name = "<unnamed>";
            var levels = new List<LodLevelRow>();
            Int32 index = 0;
            foreach (XmlElement level in schema.ChildNodes.OfType<XmlElement>()
                     .Where(x => x.Name.Equals("LOD", StringComparison.OrdinalIgnoreCase))) {
              String threshold = AttributeIgnoreCase(level, "threshold");
              var extras = new List<String>();
              foreach (XmlAttribute attribute in level.Attributes) {
                if (attribute.Name.Equals("threshold", StringComparison.OrdinalIgnoreCase)) continue;
                extras.Add(attribute.Name + "=" + attribute.Value);
              }
              levels.Add(new LodLevelRow {
                Schema = name,
                Index = index++,
                Threshold = threshold,
                Attributes = String.Join("; ", extras)
              });
            }
            schemas.Add(new LodSchemaRow { Name = name, Levels = levels });
          }
        }

        var overview = CreateGrid();
        overview.Columns.Add("schema", "Schema");
        overview.Columns.Add("levels", "LOD levels");
        overview.Columns.Add("thresholds", "Thresholds");
        overview.Columns[0].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
        overview.Columns[2].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
        foreach (LodSchemaRow schema in schemas.Take(VisibleRowLimit))
          overview.Rows.Add(schema.Name, schema.Levels.Count,
            String.Join(", ", schema.Levels.Select(x => String.IsNullOrWhiteSpace(x.Threshold) ? "?" : x.Threshold)));
        AddTab("Schemas", overview);

        var levelsGrid = CreateGrid();
        levelsGrid.Columns.Add("schema", "Schema");
        levelsGrid.Columns.Add("level", "Level");
        levelsGrid.Columns.Add("threshold", "Threshold");
        levelsGrid.Columns.Add("attributes", "Other attributes");
        levelsGrid.Columns[0].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
        levelsGrid.Columns[3].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
        Int32 emitted = 0;
        foreach (LodLevelRow level in schemas.SelectMany(x => x.Levels)) {
          if (emitted++ >= VisibleRowLimit) break;
          levelsGrid.Rows.Add(level.Schema, level.Index, level.Threshold, level.Attributes);
        }
        AddTab("Levels", levelsGrid);
        AddRawTab();

        Int32 totalLevels = schemas.Sum(x => x.Levels.Count);
        _summary.Text = schemas.Count.ToString("n0", CultureInfo.InvariantCulture) + " schemas   "
          + totalLevels.ToString("n0", CultureInfo.InvariantCulture) + " LOD levels";
        if (schemas.Count > VisibleRowLimit || totalLevels > VisibleRowLimit)
          _summary.Text += "   preview capped at " + VisibleRowLimit.ToString("n0", CultureInfo.InvariantCulture) + " rows";
      }
      catch (Exception ex) {
        _summary.Text = "Structured parse failed: " + ex.Message;
        AddRawTab();
      }
    }

    private static String AttributeIgnoreCase(XmlElement element, String name) {
      if (element == null || String.IsNullOrWhiteSpace(name)) return String.Empty;
      foreach (XmlAttribute attribute in element.Attributes)
        if (attribute.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return attribute.Value;
      return String.Empty;
    }

    private static void SplitBinding(String value, out String device, out String control) {
      device = String.Empty;
      control = String.Empty;
      if (String.IsNullOrWhiteSpace(value)) return;
      String first = value.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? String.Empty;
      Int32 separator = first.IndexOf("::", StringComparison.Ordinal);
      if (separator <= 0 || separator + 2 >= first.Length) return;
      device = first.Substring(0, separator).Trim();
      control = first.Substring(separator + 2).Trim();
    }

    public void LoadVersionTxt(String sourcePath, String text) {
      ClearPreview();
      _raw.Text = text ?? String.Empty;
      _title.Text = "Client version metadata";

      var grid = CreateGrid();
      grid.Columns.Add("key", "Key");
      grid.Columns.Add("value", "Value");
      grid.Columns[1].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;

      Int32 properties = 0;
      foreach (String rawLine in (text ?? String.Empty).Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None)) {
        String line = rawLine.Trim();
        if (line.Length == 0) continue;
        Int32 equals = line.IndexOf('=');
        if (equals < 0) {
          grid.Rows.Add(line, String.Empty);
          continue;
        }

        String key = line.Substring(0, equals).Trim();
        String value = line.Substring(equals + 1).Trim();
        if (key.Equals("dbVersion", StringComparison.OrdinalIgnoreCase)
            && value.Length == 14 && value.All(Char.IsDigit)) {
          value = value.Substring(0, 4) + "-" + value.Substring(4, 2) + "-" + value.Substring(6, 2)
            + " " + value.Substring(8, 2) + ":" + value.Substring(10, 2) + ":" + value.Substring(12, 2);
        } else if (value.Length > 0 && value.All(Char.IsDigit)
                   && UInt64.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out UInt64 numericValue)) {
          value = numericValue.ToString("N0", CultureInfo.InvariantCulture);
        }
        grid.Rows.Add(key, value);
        properties++;
      }

      AddTab("Parsed", grid);
      AddRawTab();
      _summary.Text = properties.ToString("n0", CultureInfo.InvariantCulture) + " properties";
    }

    public void LoadMapNotes(String sourcePath, String rawText) {
      ClearPreview();
      _raw.Text = rawText ?? String.Empty;
      _title.Text = "NOT map notes";

      List<MapNoteRow> notes = ParseMapNotes(rawText);
      String normalizedSource = (sourcePath ?? String.Empty).Replace('\\', '/');
      Int32 slash = normalizedSource.LastIndexOf('/');
      String baseDirectory = slash >= 0 ? normalizedSource.Substring(0, slash + 1) : String.Empty;

      var grid = CreateGrid();
      grid.Columns.Add("id", "ID");
      grid.Columns.Add("fqn", "MPN prototype");
      grid.Columns.Add("x", "X ×10");
      grid.Columns.Add("z", "Z ×10");
      grid.Columns.Add("y", "Y ×10");
      grid.Columns.Add("rx", "Rot X");
      grid.Columns.Add("rz", "Rot Z");
      grid.Columns.Add("ry", "Rot Y");
      grid.Columns.Add("tags", "Map tags");
      grid.Columns.Add("parents", "Parent map tags");
      grid.Columns.Add("ghost", "Ghosted");
      grid.Columns.Add("arrow", "Off-map arrow");
      grid.Columns.Add("crumbOnly", "Quest breadcrumb only");
      grid.Columns.Add("noCrumb", "No breadcrumb");
      grid.Columns.Add("fow", "Ignore FoW");
      grid.Columns.Add("map", "Map DDS");
      grid.Columns[1].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
      grid.Columns[8].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
      grid.Columns[9].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;

      Int32 visibleCount = Math.Min(notes.Count, VisibleRowLimit);
      for (Int32 i = 0; i < visibleCount; i++) {
        MapNoteRow note = notes[i];
        note.MapPaths = BuildMapPaths(baseDirectory, note);
        String preferredMap = note.MapPaths.FirstOrDefault(path => ResourceExists == null || SafeExists(path));
        if (preferredMap == null) preferredMap = note.MapPaths.FirstOrDefault();
        note.PreferredMapPath = preferredMap;

        Int32 row = grid.Rows.Add(
          note.Id, note.Fqn,
          MapCoordinate(note.Position, 0), MapCoordinate(note.Position, 2), MapCoordinate(note.Position, 1),
          MapRotation(note.Rotation, 0), MapRotation(note.Rotation, 2), MapRotation(note.Rotation, 1),
          String.Join(", ", note.Tags), String.Join(", ", note.ParentTags),
          BoolText(note.ShowGhosted), BoolText(note.ShowOffmapArrow), BoolText(note.ShowQuestBreadcrumbOnly),
          BoolText(note.DoNotBreadcrumb), BoolText(note.IgnoreFoW),
          preferredMap == null ? "—" : ExistsMark(preferredMap)
        );
        grid.Rows[row].Tag = note;
      }
      grid.CellDoubleClick += MapNoteCellDoubleClick;
      AddTab("Notes", grid);

      TextBox decoded = new TextBox {
        Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, WordWrap = false,
        ScrollBars = ScrollBars.Both, Font = new Font(FontFamily.GenericMonospace, 9f),
        Text = DecodeMapNotesPayload(rawText)
      };
      AddTab("Decoded", decoded);
      AddRawTab();

      _summary.Text = notes.Count.ToString("n0", CultureInfo.InvariantCulture) + " map notes";
      if (notes.Count > VisibleRowLimit)
        _summary.Text += "   showing first " + VisibleRowLimit.ToString("n0", CultureInfo.InvariantCulture);
    }

    private Boolean SafeExists(String path) {
      if (String.IsNullOrWhiteSpace(path)) return false;
      if (_existsCache.TryGetValue(path, out Boolean cached)) return cached;
      Boolean exists = false;
      try { exists = ResourceExists?.Invoke(path) == true; } catch { exists = false; }
      _existsCache[path] = exists;
      return exists;
    }

    private static String BoolText(Boolean value) => value ? "true" : "false";

    private static String MapCoordinate(Single[] values, Int32 index) {
      if (values == null || index < 0 || index >= values.Length) return String.Empty;
      return (values[index] * 10.0F).ToString("0.00", CultureInfo.InvariantCulture);
    }

    private static String MapRotation(Single[] values, Int32 index) {
      if (values == null || index < 0 || index >= values.Length) return String.Empty;
      return values[index].ToString("0.##", CultureInfo.InvariantCulture);
    }

    private static List<String> BuildMapPaths(String baseDirectory, MapNoteRow note) {
      var result = new List<String>();
      void AddTag(String raw) {
        String tag = (raw ?? String.Empty).Trim();
        if (tag.Length == 0) return;
        Int32 submap = tag.IndexOf(".submap", StringComparison.OrdinalIgnoreCase);
        if (submap >= 0) tag = tag.Substring(0, submap);
        String path = (baseDirectory + tag + "_r.dds").Replace('\\', '/').ToLowerInvariant();
        if (!result.Contains(path, StringComparer.OrdinalIgnoreCase)) result.Add(path);
      }
      foreach (String tag in note.Tags) AddTag(tag);
      foreach (String tag in note.ParentTags) AddTag(tag);
      return result;
    }

    private static List<MapNoteRow> ParseMapNotes(String rawText) {
      var notes = new List<MapNoteRow>();
      String payload = DecodeMapNotesPayload(rawText);
      if (String.IsNullOrWhiteSpace(payload)) return notes;

      XmlDocument doc = new XmlDocument();
      try { doc.LoadXml("<mapnotesRoot>" + payload + "</mapnotesRoot>"); }
      catch {
        try { doc.LoadXml(payload); }
        catch { return notes; }
      }

      XmlNodeList keyNodes = doc.SelectNodes("//k");
      if (keyNodes == null) return notes;
      foreach (XmlNode key in keyNodes) {
        XmlNode entry = NextElementSibling(key);
        if (entry == null || !entry.Name.Equals("e", StringComparison.OrdinalIgnoreCase)) continue;
        XmlNode node = entry.SelectSingleNode("./node") ?? entry.SelectSingleNode(".//node");
        if (node == null) continue;

        var fields = new Dictionary<String, XmlElement>(StringComparer.OrdinalIgnoreCase);
        XmlNodeList fieldNodes = node.SelectNodes("./f");
        if (fieldNodes == null) continue;
        foreach (XmlElement field in fieldNodes.OfType<XmlElement>()) {
          String name = field.GetAttribute("name");
          if (!String.IsNullOrWhiteSpace(name) && !fields.ContainsKey(name)) fields.Add(name, field);
        }
        if (!fields.ContainsKey("mpnPosition") || !fields.ContainsKey("mpnTemplateFQN")) continue;

        var note = new MapNoteRow {
          Id = key.InnerText.Trim(),
          Position = ParseVector(fields["mpnPosition"].InnerText),
          Rotation = fields.TryGetValue("mpnRotation", out XmlElement rot) ? ParseVector(rot.InnerText) : Array.Empty<Single>(),
          ShowGhosted = FieldBool(fields, "ShowGhosted"),
          ShowOffmapArrow = FieldBool(fields, "ShowOffmapArrow"),
          ShowQuestBreadcrumbOnly = FieldBool(fields, "ShowQuestBreadcrumbOnly"),
          DoNotBreadcrumb = FieldBool(fields, "DoNotBreadcrumb"),
          IgnoreFoW = FieldBool(fields, "mpnIgnoreFoW")
        };

        String fqn = fields["mpnTemplateFQN"].InnerText.Trim();
        if (fqn.StartsWith("\\server\\", StringComparison.OrdinalIgnoreCase)) fqn = fqn.Substring("\\server\\".Length);
        if (fqn.EndsWith(".mpn", StringComparison.OrdinalIgnoreCase)) fqn = fqn.Substring(0, fqn.Length - 4);
        note.Fqn = fqn.Replace('\\', '.').Trim('.');

        if (fields.TryGetValue("mpnMapTags", out XmlElement tags)) {
          foreach (XmlElement tag in tags.SelectNodes("./k")?.OfType<XmlElement>() ?? Enumerable.Empty<XmlElement>()) {
            String value = tag.InnerText.Trim();
            if (value.Length > 0) note.Tags.Add(value);
          }
        }
        if (fields.TryGetValue("ParentMapTag", out XmlElement parents)) {
          note.ParentTags.AddRange(parents.InnerText.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0));
        }
        notes.Add(note);
      }
      return notes;
    }

    private static XmlNode NextElementSibling(XmlNode node) {
      for (XmlNode cur = node?.NextSibling; cur != null; cur = cur.NextSibling)
        if (cur.NodeType == XmlNodeType.Element) return cur;
      return null;
    }

    private static Boolean FieldBool(Dictionary<String, XmlElement> fields, String name) =>
      fields.TryGetValue(name, out XmlElement field)
      && field.InnerText.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);

    private static Single[] ParseVector(String text) {
      String cleaned = (text ?? String.Empty).Trim().Trim('(', ')');
      String[] parts = cleaned.Split(',');
      if (parts.Length < 3) return Array.Empty<Single>();
      var result = new Single[3];
      for (Int32 i = 0; i < 3; i++) {
        if (!Single.TryParse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out result[i]))
          return Array.Empty<Single>();
      }
      return result;
    }

    private static String DecodeMapNotesPayload(String rawText) {
      String value = (rawText ?? String.Empty).Replace("\0", String.Empty);
      if (value.Length > 48) {
        String jedipediaSlice = value.Substring(30, value.Length - 48);
        if (jedipediaSlice.IndexOf("&lt;", StringComparison.OrdinalIgnoreCase) >= 0
            || jedipediaSlice.IndexOf("<k", StringComparison.OrdinalIgnoreCase) >= 0)
          value = jedipediaSlice;
      }

      // mapnotes.not is nested escaped markup. Jedipedia decodes one level after removing
      // the wrapper; a few historical files contain a second escaped level, so stop after
      // at most three stable passes rather than unescaping arbitrary user data forever.
      for (Int32 pass = 0; pass < 3; pass++) {
        String next = value.Replace("&amp;", "&").Replace("&lt;", "<").Replace("&gt;", ">")
          .Replace("&apos;", "'").Replace("&quot;", "\"");
        if (String.Equals(next, value, StringComparison.Ordinal)) break;
        value = next;
      }
      return value.Trim();
    }

    private void ResourcePathRowDoubleClick(Object sender, DataGridViewCellEventArgs e) {
      if (e.RowIndex < 0 || sender is not DataGridView grid || grid.Rows[e.RowIndex].Tag is not String path) return;
      OpenIfPresent(path);
    }

    private void MapNoteCellDoubleClick(Object sender, DataGridViewCellEventArgs e) {
      if (e.RowIndex < 0 || sender is not DataGridView grid || grid.Rows[e.RowIndex].Tag is not MapNoteRow note) return;
      if (!String.IsNullOrWhiteSpace(note.PreferredMapPath) && OpenIfPresent(note.PreferredMapPath)) return;
      foreach (String path in note.MapPaths) if (OpenIfPresent(path)) return;
    }

    private String ExistsMark(String path) => SafeExists(path) ? "✓" : "—";

    private void ManifestCellDoubleClick(Object sender, DataGridViewCellEventArgs e) {
      if (e.RowIndex < 0 || sender is not DataGridView grid || grid.Rows[e.RowIndex].Tag is not String[] candidates) return;
      // Language columns map directly; double-clicking the name picks en -> de -> fr.
      if (e.ColumnIndex >= 1 && e.ColumnIndex <= 3) { OpenIfPresent(candidates[e.ColumnIndex - 1]); return; }
      foreach (Int32 i in new[] { 1, 0, 2 }) if (OpenIfPresent(candidates[i])) return;
    }

    private void TblCellDoubleClick(Object sender, DataGridViewCellEventArgs e) {
      if (e.RowIndex < 0 || e.ColumnIndex < 0 || sender is not DataGridView grid || grid.Tag is not List<TblColumn> columns || e.ColumnIndex >= columns.Count) return;
      String value = Convert.ToString(grid.Rows[e.RowIndex].Cells[e.ColumnIndex].Value, CultureInfo.InvariantCulture)?.Trim();
      if (String.IsNullOrWhiteSpace(value)) return;
      String column = columns[e.ColumnIndex].Name ?? String.Empty;
      if (column.Equals("spec", StringComparison.OrdinalIgnoreCase)) OpenIfPresent("/resources/art/dynamic/spec/" + value + ".dat");
      else if (column.Equals("flurry_package", StringComparison.OrdinalIgnoreCase)) OpenIfPresent(value);
      else if (value.StartsWith("/resources/", StringComparison.OrdinalIgnoreCase)) OpenIfPresent(value);
    }

    private void RulRuleCellDoubleClick(Object sender, DataGridViewCellEventArgs e) {
      if (e.RowIndex < 0 || sender is not DataGridView grid || grid.Rows[e.RowIndex].Tag is not String path) return;
      OpenIfPresent(path);
    }

    private Boolean OpenIfPresent(String path) {
      if (String.IsNullOrWhiteSpace(path)) return false;
      String normalized = path.Replace('\\', '/');
      if (!normalized.StartsWith("/resources/", StringComparison.OrdinalIgnoreCase)) normalized = "/resources/" + normalized.TrimStart('/');
      try {
        if (ResourceExists != null && !ResourceExists(normalized)) return false;
        OpenResourceRequested?.Invoke(normalized);
        return true;
      } catch { return false; }
    }

    private DataGridView CreateGrid() {
      return new DataGridView {
        Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false, RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        MultiSelect = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.DisplayedCells,
        BackgroundColor = SystemColors.Window, BorderStyle = BorderStyle.None
      };
    }

    private void AddTab(String name, Control control) {
      var page = new TabPage(name);
      page.Controls.Add(control);
      _tabs.TabPages.Add(page);
    }

    private void AddRawTab() {
      var rawPage = new TabPage("Raw");
      rawPage.Controls.Add(_raw);
      _tabs.TabPages.Add(rawPage);
    }

    private sealed class IniRow {
      public String Section = String.Empty;
      public String Key = String.Empty;
      public String Value = String.Empty;
      public String Device = String.Empty;
      public String Control = String.Empty;
    }

    private sealed class LodSchemaRow {
      public String Name = String.Empty;
      public List<LodLevelRow> Levels = new List<LodLevelRow>();
    }

    private sealed class LodLevelRow {
      public String Schema = String.Empty;
      public Int32 Index;
      public String Threshold = String.Empty;
      public String Attributes = String.Empty;
    }

    private sealed class MapNoteRow {
      public String Id = String.Empty;
      public String Fqn = String.Empty;
      public Single[] Position = Array.Empty<Single>();
      public Single[] Rotation = Array.Empty<Single>();
      public readonly List<String> Tags = new List<String>();
      public readonly List<String> ParentTags = new List<String>();
      public Boolean ShowGhosted;
      public Boolean ShowOffmapArrow;
      public Boolean ShowQuestBreadcrumbOnly;
      public Boolean DoNotBreadcrumb;
      public Boolean IgnoreFoW;
      public List<String> MapPaths = new List<String>();
      public String PreferredMapPath;
    }

    private sealed class TblColumn {
      public String Name, Type, RefType, Position, Constraints;
      public Boolean Primary;
    }
  }
}
