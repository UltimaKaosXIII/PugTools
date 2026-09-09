using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using System.Xml.Linq;

namespace PugTools {
  /// <summary>
  /// Interactive, database-free dependency/relationship graph for SWTOR effect assets.
  /// FXSPEC parsing mirrors the semantic lists used by the World Browser runtime instead of
  /// treating the XML as an unstructured bag of resource strings.
  /// </summary>
  internal sealed class EffectDependencyGraphControl : UserControl {
    private const Int32 MaxComponentNodes = 320;
    private const Int32 MaxResourceNodes = 420;

    private readonly ToolStrip _tools;
    private readonly ToolStripButton _fit;
    private readonly ToolStripButton _relations;
    private readonly ToolStripButton _resources;
    private readonly ToolStripButton _copyDot;
    private readonly ToolStripLabel _stats;
    private readonly ToolStripLabel _selection;
    private readonly Panel _canvas;
    private readonly ToolTip _tooltip;
    private readonly Font _nodeFont;
    private readonly Font _nodeBoldFont;

    private readonly List<GraphNode> _nodes = new List<GraphNode>();
    private readonly List<GraphEdge> _edges = new List<GraphEdge>();
    private readonly Dictionary<String, GraphNode> _nodeById = new Dictionary<String, GraphNode>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<String, GraphNode> _componentByName = new Dictionary<String, GraphNode>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<String, Boolean> _resourceExistenceCache = new Dictionary<String, Boolean>(StringComparer.OrdinalIgnoreCase);

    private Single _zoom = 1f;
    private PointF _pan = PointF.Empty;
    private Boolean _dragging;
    private Point _dragStart;
    private PointF _panStart;
    private GraphNode _selected;
    private GraphNode _hovered;
    private String _sourcePath;
    private String _format;
    private Boolean _layoutReady;

    public Func<String, Boolean> ResourceExists { get; set; }
    public Action<String> OpenResourceRequested { get; set; }

    private sealed class FxListDescriptor {
      public String Field { get; }
      public String Kind { get; }
      public FxListDescriptor(String field, String kind) { Field = field; Kind = kind; }
    }

    private static readonly FxListDescriptor[] FxLists = {
      new FxListDescriptor("_fxEmitterList", "Emitter"),
      new FxListDescriptor("_fxModelList", "Model"),
      new FxListDescriptor("_fxModelFaderList", "Model fader"),
      new FxListDescriptor("_fxTransformerList", "Transformer"),
      new FxListDescriptor("_fxAttacherList", "Attacher"),
      new FxListDescriptor("_fxLightList", "Light"),
      new FxListDescriptor("_fxLightningList", "Lightning"),
      new FxListDescriptor("_fxProjectorList", "Projector"),
      new FxListDescriptor("_fx3DSoundList", "3D sound"),
      new FxListDescriptor("_fxTransformArcList", "Transform arc"),
      new FxListDescriptor("_fxList", "Timing group"),
      // These are intentionally shown even though the World Browser currently defers some of them.
      new FxListDescriptor("_fxSoundList", "Sound"),
      new FxListDescriptor("_fxFollowerList", "Follower"),
      new FxListDescriptor("_fxFlareList", "Flare"),
      new FxListDescriptor("_fxMirrorList", "Mirror"),
      new FxListDescriptor("_fxAnimationCmdList", "Animation cmd"),
      new FxListDescriptor("_fxZapList", "Zap")
    };

    public EffectDependencyGraphControl() {
      Dock = DockStyle.Fill;
      BackColor = Color.FromArgb(22, 24, 28);

      _tools = new ToolStrip { Dock = DockStyle.Top, GripStyle = ToolStripGripStyle.Hidden };
      _fit = new ToolStripButton("Fit");
      _fit.Click += delegate { FitGraph(); };
      _relations = new ToolStripButton("Relations") { CheckOnClick = true, Checked = true, ToolTipText = "Show StartLoc/AttachTo/trigger/operator relations" };
      _relations.CheckedChanged += delegate { _canvas.Invalidate(); };
      _resources = new ToolStripButton("Resources") { CheckOnClick = true, Checked = true, ToolTipText = "Show referenced PRT/GR2/DDS/audio/resource nodes" };
      _resources.CheckedChanged += delegate { _canvas.Invalidate(); };
      _copyDot = new ToolStripButton("Copy DOT") { ToolTipText = "Copy the current graph as Graphviz DOT text" };
      _copyDot.Click += delegate { CopyDot(); };
      _stats = new ToolStripLabel("No graph");
      _selection = new ToolStripLabel();
      _tools.Items.Add(_fit);
      _tools.Items.Add(_relations);
      _tools.Items.Add(_resources);
      _tools.Items.Add(_copyDot);
      _tools.Items.Add(new ToolStripSeparator());
      _tools.Items.Add(_stats);
      _tools.Items.Add(new ToolStripSeparator());
      _tools.Items.Add(_selection);

      _canvas = new Panel { Dock = DockStyle.Fill, BackColor = BackColor, TabStop = true };
      _canvas.Paint += CanvasPaint;
      _canvas.MouseWheel += CanvasMouseWheel;
      _canvas.MouseDown += CanvasMouseDown;
      _canvas.MouseMove += CanvasMouseMove;
      _canvas.MouseUp += delegate { _dragging = false; };
      _canvas.MouseDoubleClick += CanvasMouseDoubleClick;
      _canvas.MouseEnter += delegate { if (!_canvas.Focused) _canvas.Focus(); };
      _canvas.Resize += delegate { if (_layoutReady && _nodes.Count > 0) _canvas.Invalidate(); };

      _tooltip = new ToolTip { InitialDelay = 350, ReshowDelay = 120, AutoPopDelay = 12000, ShowAlways = true };
      _nodeFont = new Font(SystemFonts.MessageBoxFont.FontFamily, Math.Max(7f, SystemFonts.MessageBoxFont.Size - .5f), FontStyle.Regular);
      _nodeBoldFont = new Font(SystemFonts.MessageBoxFont.FontFamily, Math.Max(7f, SystemFonts.MessageBoxFont.Size), FontStyle.Bold);

      Controls.Add(_canvas);
      Controls.Add(_tools);
    }

    public void ClearGraph() {
      _nodes.Clear();
      _edges.Clear();
      _nodeById.Clear();
      _componentByName.Clear();
      _resourceExistenceCache.Clear();
      _selected = null;
      _hovered = null;
      _sourcePath = null;
      _format = null;
      _layoutReady = false;
      _zoom = 1f;
      _pan = PointF.Empty;
      _stats.Text = "No graph";
      _selection.Text = String.Empty;
      _canvas.Invalidate();
    }

    public void LoadPrt(String sourcePath, String text) {
      ClearGraph();
      _sourcePath = NormalizeResourcePath(sourcePath);
      _format = "PRT";
      GraphNode root = AddNode("root", "PRT", Path.GetFileName(_sourcePath), _sourcePath, null, false, 0);
      var fields = new Dictionary<String, String>(StringComparer.OrdinalIgnoreCase);
      using (var reader = new StringReader(text ?? String.Empty)) {
        String line;
        while ((line = reader.ReadLine()) != null) {
          String trimmed = line.Trim();
          Int32 equal = trimmed.IndexOf('=');
          if (equal <= 0) continue;
          fields[trimmed.Substring(0, equal).Trim().TrimStart('.')] = trimmed.Substring(equal + 1).Trim();
        }
      }
      AddPrtResource(root, fields, "TextureName", "DDS");
      AddPrtResource(root, fields, "TextureName2", "DDS");
      AddPrtResource(root, fields, "TrailTexture", "DDS");
      AddPrtResource(root, fields, "GrannyFileName", "GR2");
      if (fields.TryGetValue("EmitFXSpec", out String childFx) && !String.IsNullOrWhiteSpace(childFx)) {
        String path = NormalizeFxSpec(childFx);
        GraphNode resource = AddResourceNode(path, "FXSPEC", "EmitFXSpec");
        AddEdge(root, resource, "emit at death/child FX", EdgeKind.Resource);
      }
      FinalizeGraph();
    }

    public void LoadXmlEffect(String format, String sourcePath, String text) {
      ClearGraph();
      _sourcePath = NormalizeResourcePath(sourcePath);
      _format = String.IsNullOrWhiteSpace(format) ? "XML" : format.ToUpperInvariant();
      if (_format == "FXSPEC") BuildFxSpec(text);
      else if (_format == "EPP") BuildEpp(text);
      else BuildGenericXml(text);
      FinalizeGraph();
    }

    private void BuildFxSpec(String text) {
      GraphNode rootGraph = AddNode("root", "FXSPEC", Path.GetFileName(_sourcePath), _sourcePath, null, false, 0);
      XDocument doc;
      try { doc = XDocument.Parse(text ?? String.Empty, LoadOptions.None); }
      catch (Exception ex) {
        rootGraph.Detail = "Parse error: " + ex.Message;
        return;
      }
      XElement marshal = doc.Descendants().FirstOrDefault(x => x.Name.LocalName == "marshalData");
      XElement root = marshal?.Elements().FirstOrDefault(x => x.Name.LocalName == "node") ?? doc.Root;
      if (root == null) return;

      XElement masterField = root.Elements().FirstOrDefault(x => x.Name.LocalName == "f" && String.Equals((String)x.Attribute("name"), "_fxMasterGroup", StringComparison.Ordinal));
      Dictionary<String, String> master = ParseFieldMap(masterField);
      String masterName = Fx(master, "_fxName", "PARENT");
      String timer = Fx(master, "_fxTimerType", "NEVERFIRE");
      String fire = Fx(master, "_fxFireRate");
      String timeout = Fx(master, "_fxTimeOut");
      rootGraph.Title = String.IsNullOrWhiteSpace(masterName) ? Path.GetFileName(_sourcePath) : masterName;
      rootGraph.Detail = "FXSPEC\n" + _sourcePath + "\nTimer: " + timer
        + (String.IsNullOrWhiteSpace(fire) ? String.Empty : "  FireRate: " + fire)
        + (String.IsNullOrWhiteSpace(timeout) ? String.Empty : "  Timeout: " + timeout);
      if (!String.IsNullOrWhiteSpace(masterName)) _componentByName[masterName] = rootGraph;

      Int32 componentCount = 0;
      foreach (FxListDescriptor list in FxLists) {
        XElement listElement = root.Elements().FirstOrDefault(x => x.Name.LocalName == "f" && String.Equals((String)x.Attribute("name"), list.Field, StringComparison.Ordinal));
        if (listElement == null) continue;
        Int32 index = 0;
        foreach (XElement entry in listElement.Elements().Where(x => x.Name.LocalName == "e")) {
          if (componentCount >= MaxComponentNodes) break;
          Dictionary<String, String> fields = ParseFieldMap(entry);
          if (fields.Count == 0) { index++; continue; }
          String fallback = list.Kind.ToLowerInvariant().Replace(' ', '-') + "#" + index.ToString(CultureInfo.InvariantCulture);
          String name = Fx(fields, "_fxName", fallback);
          String id = "component:" + list.Field + ":" + index.ToString(CultureInfo.InvariantCulture);
          String resource = Fx(fields, "_fxResourceName");
          String detail = BuildComponentDetail(list.Kind, name, fields);
          String dynamicGate = ParseStartIf(entry);
          if (!String.IsNullOrWhiteSpace(dynamicGate)) detail += "\nDynamic gate: " + dynamicGate;
          else if (String.Equals(Fx(fields, "_fxCheckDynData"), "true", StringComparison.OrdinalIgnoreCase)) detail += "\nDynamic gate: enabled (no static values in preview)";
          GraphNode node = AddNode(id, list.Kind, name, detail, null, false, 1);
          node.Fields = fields;
          if (!String.IsNullOrWhiteSpace(name) && !_componentByName.ContainsKey(name)) _componentByName[name] = node;
          AddEdge(rootGraph, node, list.Kind, EdgeKind.Contains);
          AddFxResources(node, list.Kind, fields, resource);
          componentCount++;
          index++;
        }
        if (componentCount >= MaxComponentNodes) break;
      }

      if (componentCount >= MaxComponentNodes) {
        GraphNode more = AddNode("more-components", "Info", "More components…", "Graph display capped at " + MaxComponentNodes.ToString("n0") + " effect components.", null, false, 1);
        AddEdge(rootGraph, more, "display cap", EdgeKind.Contains);
      }

      // Relationship pass must run after every typed list has been indexed because SWTOR FXSPEC lists are
      // sorted by KIND, not hierarchy; children regularly point at a model/group parsed later in the XML.
      foreach (GraphNode node in _nodes.Where(x => x.Level == 1 && x.Fields != null).ToArray()) {
        AddNamedRelation(node, Fx(node.Fields, "_fxAttachTo"), "attach", EdgeKind.Attach);
        AddNamedRelation(node, Fx(node.Fields, "_fxStartLoc"), "start loc", EdgeKind.Anchor);
        AddNamedRelation(node, Fx(node.Fields, "_fxStartFxName"), Fx(node.Fields, "_fxWhenToStart", "start"), EdgeKind.Trigger);
        AddNamedRelation(node, Fx(node.Fields, "_fxStopFxName"), Fx(node.Fields, "_fxWhenToStop", "stop"), EdgeKind.Trigger);
        AddNamedRelation(node, Fx(node.Fields, "_fxOwnerName"), "owner", EdgeKind.Operator);
        AddNamedRelationFromOperator(node, Fx(node.Fields, "_fxAssetName"), "operates on", EdgeKind.Operator);
        AddNamedRelationFromOperator(node, Fx(node.Fields, "_fxAttachThis"), "attaches", EdgeKind.Operator);
        AddNamedRelation(node, Fx(node.Fields, "_fxTargetName"), "target", EdgeKind.Target);
        AddNamedRelation(node, Fx(node.Fields, "_fxTargetLoc"), "target loc", EdgeKind.Target);
      }
    }

    private void BuildEpp(String text) {
      GraphNode root = AddNode("root", "EPP", Path.GetFileName(_sourcePath), _sourcePath, null, false, 0);
      try {
        XDocument doc = XDocument.Parse(text ?? String.Empty, LoadOptions.None);
        Int32 count = 0;
        foreach (XElement e in doc.Descendants()) {
          String tag = e.Name.LocalName;
          if (!tag.Equals("fxSpecString", StringComparison.OrdinalIgnoreCase) && !tag.Equals("projectileFXString", StringComparison.OrdinalIgnoreCase)) continue;
          String value = (e.Value ?? String.Empty).Trim();
          if (value.Length == 0) continue;
          String path = NormalizeFxSpec(value.EndsWith(".fxspec", StringComparison.OrdinalIgnoreCase) ? value : value + ".fxspec");
          GraphNode fx = AddResourceNode(path, "FXSPEC", tag);
          AddEdge(root, fx, tag.Equals("projectileFXString", StringComparison.OrdinalIgnoreCase) ? "projectile FX" : "play FX", EdgeKind.Resource);
          count++;
        }
        foreach (String tag in new[] { "casterAnim", "targetAnim" }) {
          foreach (XElement e in doc.Descendants().Where(x => x.Name.LocalName.Equals(tag, StringComparison.OrdinalIgnoreCase))) {
            String value = (e.Value ?? String.Empty).Trim();
            if (value.Length == 0) continue;
            GraphNode anim = AddNode("anim:" + tag + ":" + value, "Animation", value, tag, null, false, 2);
            AddEdge(root, anim, tag, EdgeKind.Resource);
          }
        }
        root.Detail = _sourcePath + "\nReferenced FX: " + count.ToString("n0");
      }
      catch (Exception ex) { root.Detail = "Parse error: " + ex.Message; }
    }

    private void BuildGenericXml(String text) {
      GraphNode root = AddNode("root", _format ?? "XML", Path.GetFileName(_sourcePath), _sourcePath, null, false, 0);
      try {
        XDocument doc = XDocument.Parse(text ?? String.Empty, LoadOptions.None);
        Int32 found = 0;
        foreach (XElement e in doc.Descendants()) {
          String value = (e.Value ?? String.Empty).Trim();
          if (value.Length == 0 || value.Length > 1024) continue;
          foreach (String path in ExtractResourcePaths(value)) {
            if (++found > MaxResourceNodes) break;
            String ext = Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
            GraphNode resource = AddResourceNode(path, ext, e.Name.LocalName);
            AddEdge(root, resource, e.Name.LocalName, EdgeKind.Resource);
          }
          if (found > MaxResourceNodes) break;
        }
      }
      catch (Exception ex) { root.Detail = "Parse error: " + ex.Message; }
    }

    private void AddPrtResource(GraphNode root, Dictionary<String, String> fields, String key, String type) {
      if (!fields.TryGetValue(key, out String value) || String.IsNullOrWhiteSpace(value)) return;
      String path;
      if (type == "DDS") {
        path = NormalizeResourcePath(value);
        if (String.IsNullOrWhiteSpace(Path.GetExtension(path))) path += ".dds";
      } else path = NormalizeResourcePath(value);
      GraphNode resource = AddResourceNode(path, type, key);
      AddEdge(root, resource, key, EdgeKind.Resource);
    }

    private void AddFxResources(GraphNode component, String kind, Dictionary<String, String> fields, String resourceValue) {
      if (!String.IsNullOrWhiteSpace(resourceValue)) {
        if (kind.IndexOf("sound", StringComparison.OrdinalIgnoreCase) >= 0 && !LooksLikeFile(resourceValue)) {
          foreach (String evt in resourceValue.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)) {
            String value = evt.Trim();
            if (value.Length == 0) continue;
            GraphNode audio = AddNode("audio:" + value, "Audio event", value, "Wwise/event name from _fxResourceName", null, false, 2);
            AddEdge(component, audio, "audio", EdgeKind.Resource);
          }
        } else {
          foreach (String raw in resourceValue.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)) {
            String value = raw.Trim();
            if (value.Length == 0) continue;
            String path = NormalizeFxResource(value);
            if (String.IsNullOrWhiteSpace(path)) continue;
            String type = Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
            GraphNode resource = AddResourceNode(path, type.Length == 0 ? "Resource" : type, "_fxResourceName");
            AddEdge(component, resource, "resource", EdgeKind.Resource);
          }
        }
      }

      foreach (String field in new[] { "_fxProjectionTexture", "_fxProjectionTexture_layer1", "_fxTextureName", "_fxRampMap" }) {
        String value = Fx(fields, field);
        if (String.IsNullOrWhiteSpace(value)) continue;
        String basePath = value.Trim().Replace('\\', '/');
        foreach (String suffix in new[] { ".tiny.dds", ".dds", ".tex" }) {
          if (basePath.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) basePath = basePath.Substring(0, basePath.Length - suffix.Length);
        }
        String texturePath = ResolveTexturePath(basePath);
        String textureType = Path.GetExtension(texturePath).Equals(".tex", StringComparison.OrdinalIgnoreCase) ? "TEX" : "DDS";
        GraphNode texture = AddResourceNode(texturePath, textureType, field);
        AddEdge(component, texture, field.Replace("_fx", String.Empty), EdgeKind.Resource);
      }

      // A few authored sound fields use explicit start/stop event names instead of _fxResourceName.
      foreach (String field in new[] { "_fxAudioStartEvent", "_fxAudioStopEvent" }) {
        String value = Fx(fields, field);
        if (String.IsNullOrWhiteSpace(value)) continue;
        GraphNode audio = AddNode("audio:" + value, "Audio event", value, field, null, false, 2);
        AddEdge(component, audio, field == "_fxAudioStartEvent" ? "audio start" : "audio stop", EdgeKind.Resource);
      }
    }

    private void AddNamedRelation(GraphNode node, String referencedName, String label, EdgeKind kind) {
      String name = (referencedName ?? String.Empty).Trim();
      if (name.Length == 0 || IsExternalAnchor(name)) return;
      if (_componentByName.TryGetValue(name, out GraphNode other) && !ReferenceEquals(other, node)) AddEdge(other, node, label, kind);
      else AddDanglingAnchor(node, name, label, kind);
    }

    private void AddNamedRelationFromOperator(GraphNode operatorNode, String referencedName, String label, EdgeKind kind) {
      String name = (referencedName ?? String.Empty).Trim();
      if (name.Length == 0 || IsExternalAnchor(name)) return;
      if (_componentByName.TryGetValue(name, out GraphNode target) && !ReferenceEquals(target, operatorNode)) AddEdge(operatorNode, target, label, kind);
      else AddDanglingAnchor(operatorNode, name, label, kind);
    }

    private void AddDanglingAnchor(GraphNode node, String name, String label, EdgeKind kind) {
      String id = "anchor:" + name;
      GraphNode anchor = AddNode(id, "External/name", name, "Named relation target is not an element in this FXSPEC. SWTOR may resolve it from the host/model locator, or play it unplaced.", null, false, 0);
      AddEdge(anchor, node, label, kind);
    }

    private GraphNode AddResourceNode(String path, String type, String source) {
      if (_nodes.Count(x => x.Level == 2 && x.IsResource) >= MaxResourceNodes) {
        return AddNode("more-resources", "Info", "More resources…", "Resource display capped at " + MaxResourceNodes.ToString("n0") + ".", null, false, 2);
      }
      String normalized = NormalizeResourcePath(path);
      String id = "resource:" + normalized;
      Boolean exists = false;
      if (ResourceExists != null) {
        if (!_resourceExistenceCache.TryGetValue(normalized, out exists)) {
          try { exists = ResourceExists(normalized); } catch { exists = false; }
          _resourceExistenceCache[normalized] = exists;
        }
      }
      GraphNode node = AddNode(id, String.IsNullOrWhiteSpace(type) ? "Resource" : type, Path.GetFileName(normalized), normalized + "\nFrom: " + source + (ResourceExists == null ? String.Empty : exists ? "\nPresent in loaded build" : "\nMissing from loaded build"), normalized, true, 2);
      node.Exists = exists;
      return node;
    }

    private GraphNode AddNode(String id, String kind, String title, String detail, String resourcePath, Boolean isResource, Int32 level) {
      if (_nodeById.TryGetValue(id, out GraphNode existing)) return existing;
      var node = new GraphNode {
        Id = id,
        Kind = kind ?? String.Empty,
        Title = String.IsNullOrWhiteSpace(title) ? kind : title,
        Detail = detail ?? String.Empty,
        ResourcePath = resourcePath,
        IsResource = isResource,
        Level = level
      };
      _nodes.Add(node);
      _nodeById[id] = node;
      return node;
    }

    private void AddEdge(GraphNode from, GraphNode to, String label, EdgeKind kind) {
      if (from == null || to == null || ReferenceEquals(from, to)) return;
      if (_edges.Any(x => ReferenceEquals(x.From, from) && ReferenceEquals(x.To, to) && x.Kind == kind && String.Equals(x.Label, label, StringComparison.OrdinalIgnoreCase))) return;
      _edges.Add(new GraphEdge { From = from, To = to, Label = label ?? String.Empty, Kind = kind });
    }

    private void FinalizeGraph() {
      LayoutGraph();
      _layoutReady = true;
      Int32 componentCount = _nodes.Count(x => x.Level == 1);
      Int32 resourceCount = _nodes.Count(x => x.Level == 2 && x.IsResource);
      Int32 missing = _nodes.Count(x => x.IsResource && ResourceExists != null && !x.Exists);
      _stats.Text = _format + ": " + componentCount.ToString("n0") + " elements, " + resourceCount.ToString("n0") + " resources, " + _edges.Count.ToString("n0") + " relations"
        + (missing > 0 ? ", " + missing.ToString("n0") + " missing" : String.Empty);
      FitGraph();
    }

    private void LayoutGraph() {
      const Single externalX = 30f;
      const Single rootX = 300f;
      const Single componentX = 590f;
      const Single resourceX = 900f;
      const Single nodeW = 210f;
      const Single nodeH = 66f;
      const Single gap = 24f;

      GraphNode root = _nodes.FirstOrDefault(x => x.Id == "root");
      List<GraphNode> components = _nodes.Where(x => x.Level == 1).OrderBy(x => KindOrder(x.Kind)).ThenBy(x => x.Title, StringComparer.OrdinalIgnoreCase).ToList();
      List<GraphNode> anchors = _nodes.Where(x => x.Level == 0 && x.Id != "root").OrderBy(x => x.Title, StringComparer.OrdinalIgnoreCase).ToList();
      List<GraphNode> resources = _nodes.Where(x => x.Level == 2).OrderBy(x => x.Kind).ThenBy(x => x.Title, StringComparer.OrdinalIgnoreCase).ToList();

      Single componentHeight = Math.Max(nodeH, components.Count * (nodeH + gap));
      if (root != null) root.Bounds = new RectangleF(rootX, Math.Max(30f, componentHeight * .5f - nodeH * .5f), nodeW, nodeH);
      for (Int32 i = 0; i < components.Count; i++) components[i].Bounds = new RectangleF(componentX, 30f + i * (nodeH + gap), nodeW, nodeH);

      // External/dangling anchors occupy a compact lane left of the root. They are uncommon but keeping them visible
      // immediately explains why a placement looks unanchored in the World Browser.
      for (Int32 i = 0; i < anchors.Count; i++) anchors[i].Bounds = new RectangleF(externalX, 30f + i * (nodeH + 10f), nodeW, nodeH);

      // Sort resource nodes by their first component parent so related PRT/GR2/DDS nodes stay visually near each other.
      resources = resources.OrderBy(r => {
        GraphEdge edge = _edges.FirstOrDefault(e => ReferenceEquals(e.To, r) && e.From.Level == 1);
        return edge?.From.Bounds.Y ?? Single.MaxValue;
      }).ThenBy(r => r.Kind).ThenBy(r => r.Title, StringComparer.OrdinalIgnoreCase).ToList();
      for (Int32 i = 0; i < resources.Count; i++) resources[i].Bounds = new RectangleF(resourceX, 30f + i * (nodeH + 12f), nodeW, nodeH);
    }

    private static Int32 KindOrder(String kind) {
      String k = (kind ?? String.Empty).ToLowerInvariant();
      if (k == "timing group") return 0;
      if (k == "emitter") return 10;
      if (k == "model") return 20;
      if (k == "light") return 30;
      if (k == "projector") return 40;
      if (k == "lightning") return 50;
      if (k == "3d sound" || k == "sound") return 60;
      if (k == "attacher") return 70;
      if (k == "transformer") return 80;
      if (k == "transform arc") return 90;
      if (k == "model fader") return 100;
      return 110;
    }

    private void FitGraph() {
      if (_nodes.Count == 0 || _canvas.ClientSize.Width < 20 || _canvas.ClientSize.Height < 20) return;
      IEnumerable<GraphNode> visible = VisibleNodes();
      RectangleF bounds = GraphBounds(visible);
      if (bounds.Width <= 0 || bounds.Height <= 0) return;
      Single zx = (_canvas.ClientSize.Width - 50f) / bounds.Width;
      Single zy = (_canvas.ClientSize.Height - 50f) / bounds.Height;
      _zoom = Math.Max(.12f, Math.Min(1.35f, Math.Min(zx, zy)));
      _pan = new PointF((_canvas.ClientSize.Width - bounds.Width * _zoom) * .5f - bounds.Left * _zoom,
                        (_canvas.ClientSize.Height - bounds.Height * _zoom) * .5f - bounds.Top * _zoom);
      _canvas.Invalidate();
    }

    private IEnumerable<GraphNode> VisibleNodes() {
      foreach (GraphNode n in _nodes) {
        if (!_resources.Checked && n.Level == 2) continue;
        yield return n;
      }
    }

    private static RectangleF GraphBounds(IEnumerable<GraphNode> nodes) {
      Boolean any = false;
      RectangleF result = RectangleF.Empty;
      foreach (GraphNode node in nodes) {
        if (!any) { result = node.Bounds; any = true; }
        else result = RectangleF.Union(result, node.Bounds);
      }
      if (!any) return RectangleF.Empty;
      result.Inflate(20f, 20f);
      return result;
    }

    private void CanvasPaint(Object sender, PaintEventArgs e) {
      Graphics g = e.Graphics;
      g.SmoothingMode = _dragging ? SmoothingMode.HighSpeed : SmoothingMode.AntiAlias;
      g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
      g.Clear(_canvas.BackColor);
      if (_nodes.Count == 0) {
        using var emptyBrush = new SolidBrush(Color.Silver);
        g.DrawString("No dependency graph for this asset.", Font, emptyBrush, 12f, 12f);
        return;
      }

      foreach (GraphEdge edge in _edges) {
        if (!_relations.Checked && edge.Kind != EdgeKind.Contains && edge.Kind != EdgeKind.Resource) continue;
        if (!_resources.Checked && (edge.From.Level == 2 || edge.To.Level == 2)) continue;
        DrawEdge(g, edge);
      }
      foreach (GraphNode node in VisibleNodes()) DrawNode(g, node);

      using var help = new SolidBrush(Color.FromArgb(190, Color.White));
      g.DrawString("Wheel zoom • drag empty space pan • click select • double-click resource opens Asset Browser", Font, help, 8f, _canvas.ClientSize.Height - Font.Height - 8f);
    }

    private void DrawEdge(Graphics g, GraphEdge edge) {
      PointF a = WorldToScreen(new PointF(edge.From.Bounds.Right, edge.From.Bounds.Top + edge.From.Bounds.Height * .5f));
      PointF b = WorldToScreen(new PointF(edge.To.Bounds.Left, edge.To.Bounds.Top + edge.To.Bounds.Height * .5f));
      if (edge.To.Bounds.Left < edge.From.Bounds.Left) {
        a = WorldToScreen(new PointF(edge.From.Bounds.Left, edge.From.Bounds.Top + edge.From.Bounds.Height * .5f));
        b = WorldToScreen(new PointF(edge.To.Bounds.Right, edge.To.Bounds.Top + edge.To.Bounds.Height * .5f));
      }
      Color color = EdgeColor(edge.Kind);
      Single width = edge.Kind == EdgeKind.Contains ? 1f : 1.6f;
      using var pen = new Pen(color, width) { EndCap = LineCap.ArrowAnchor };
      if (edge.Kind == EdgeKind.Contains || edge.Kind == EdgeKind.Resource) {
        Single midX = (a.X + b.X) * .5f;
        using var path = new GraphicsPath();
        path.AddBezier(a, new PointF(midX, a.Y), new PointF(midX, b.Y), b);
        g.DrawPath(pen, path);
      } else {
        Single bend = Math.Max(35f, Math.Abs(b.X - a.X) * .28f);
        using var path = new GraphicsPath();
        path.AddBezier(a, new PointF(a.X + bend, a.Y), new PointF(b.X - bend, b.Y), b);
        g.DrawPath(pen, path);
      }
      if (!_dragging && _zoom >= .38f && !String.IsNullOrWhiteSpace(edge.Label) && edge.Kind != EdgeKind.Contains) {
        String label = edge.Label;
        if (label.Length > 24) label = label.Substring(0, 23) + "…";
        SizeF size = g.MeasureString(label, Font);
        PointF p = new PointF((a.X + b.X) * .5f - size.Width * .5f, (a.Y + b.Y) * .5f - size.Height * .5f);
        using var bg = new SolidBrush(Color.FromArgb(205, _canvas.BackColor));
        using var fg = new SolidBrush(Color.FromArgb(215, Color.White));
        g.FillRectangle(bg, p.X - 2f, p.Y, size.Width + 4f, size.Height);
        g.DrawString(label, Font, fg, p);
      }
    }

    private void DrawNode(Graphics g, GraphNode node) {
      RectangleF r = WorldToScreen(node.Bounds);
      if (r.Right < 0 || r.Bottom < 0 || r.Left > _canvas.ClientSize.Width || r.Top > _canvas.ClientSize.Height) return;
      Color fill = NodeColor(node.Kind);
      if (node.IsResource && ResourceExists != null && !node.Exists) fill = Color.FromArgb(85, 54, 57);
      if (ReferenceEquals(node, _selected)) fill = Blend(fill, Color.White, .28f);
      else if (ReferenceEquals(node, _hovered)) fill = Blend(fill, Color.White, .14f);
      using var brush = new SolidBrush(fill);
      using var border = new Pen(node.IsResource && ResourceExists != null && !node.Exists ? Color.IndianRed : Color.FromArgb(190, 205, 220), ReferenceEquals(node, _selected) ? 2f : 1f);
      g.FillRoundedRectangle(brush, r, Math.Max(3f, 7f * _zoom));
      g.DrawRoundedRectangle(border, r, Math.Max(3f, 7f * _zoom));
      if (_zoom < .22f) return;

      RectangleF kindRect = new RectangleF(r.Left + 8f * _zoom, r.Top + 5f * _zoom, r.Width - 16f * _zoom, 15f * _zoom);
      RectangleF titleRect = new RectangleF(r.Left + 8f * _zoom, r.Top + 23f * _zoom, r.Width - 16f * _zoom, r.Height - 27f * _zoom);
      using var kindBrush = new SolidBrush(Color.FromArgb(205, 220, 230));
      using var titleBrush = new SolidBrush(Color.White);
      using StringFormat ellipsis = EllipsisFormat();
      g.DrawString(node.Kind + (node.IsResource && ResourceExists != null ? node.Exists ? "  ✓" : "  ✕" : String.Empty), _nodeFont, kindBrush, kindRect, ellipsis);
      g.DrawString(node.Title, _nodeBoldFont, titleBrush, titleRect, ellipsis);
    }

    private static StringFormat EllipsisFormat() {
      return new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
    }

    private void CanvasMouseWheel(Object sender, MouseEventArgs e) {
      if (_nodes.Count == 0) return;
      Single oldZoom = _zoom;
      Single factor = e.Delta > 0 ? 1.18f : 1f / 1.18f;
      _zoom = Math.Max(.10f, Math.Min(3.2f, _zoom * factor));
      PointF worldAtCursor = new PointF((e.X - _pan.X) / oldZoom, (e.Y - _pan.Y) / oldZoom);
      _pan = new PointF(e.X - worldAtCursor.X * _zoom, e.Y - worldAtCursor.Y * _zoom);
      _canvas.Invalidate();
    }

    private void CanvasMouseDown(Object sender, MouseEventArgs e) {
      _canvas.Focus();
      GraphNode hit = HitTest(e.Location);
      if (e.Button == MouseButtons.Left && hit != null) {
        _selected = hit;
        _selection.Text = hit.Kind + ": " + hit.Title;
        _canvas.Invalidate();
        return;
      }
      if (e.Button == MouseButtons.Left || e.Button == MouseButtons.Middle || e.Button == MouseButtons.Right) {
        _dragging = true;
        _dragStart = e.Location;
        _panStart = _pan;
      }
    }

    private void CanvasMouseMove(Object sender, MouseEventArgs e) {
      if (_dragging) {
        _pan = new PointF(_panStart.X + e.X - _dragStart.X, _panStart.Y + e.Y - _dragStart.Y);
        _canvas.Invalidate();
        return;
      }
      GraphNode hit = HitTest(e.Location);
      if (!ReferenceEquals(hit, _hovered)) {
        _hovered = hit;
        _tooltip.SetToolTip(_canvas, hit?.Detail ?? String.Empty);
        _canvas.Invalidate();
      }
    }

    private void CanvasMouseDoubleClick(Object sender, MouseEventArgs e) {
      GraphNode hit = HitTest(e.Location);
      if (hit != null && hit.IsResource && !String.IsNullOrWhiteSpace(hit.ResourcePath)) OpenResourceRequested?.Invoke(hit.ResourcePath);
      else if (hit == null) FitGraph();
    }

    private GraphNode HitTest(Point p) {
      PointF world = ScreenToWorld(p);
      // Draw order is level/resources after relations; reverse makes the visually topmost node win.
      for (Int32 i = _nodes.Count - 1; i >= 0; i--) {
        GraphNode n = _nodes[i];
        if (!_resources.Checked && n.Level == 2) continue;
        if (n.Bounds.Contains(world)) return n;
      }
      return null;
    }

    private PointF WorldToScreen(PointF p) => new PointF(p.X * _zoom + _pan.X, p.Y * _zoom + _pan.Y);
    private RectangleF WorldToScreen(RectangleF r) => new RectangleF(r.X * _zoom + _pan.X, r.Y * _zoom + _pan.Y, r.Width * _zoom, r.Height * _zoom);
    private PointF ScreenToWorld(Point p) => new PointF((p.X - _pan.X) / _zoom, (p.Y - _pan.Y) / _zoom);

    private void CopyDot() {
      if (_nodes.Count == 0) return;
      var b = new StringBuilder();
      b.AppendLine("digraph SWTOR_FX {");
      b.AppendLine("  rankdir=LR;");
      foreach (GraphNode n in _nodes) {
        if (!_resources.Checked && n.Level == 2) continue;
        b.Append("  \"").Append(Dot(n.Id)).Append("\" [label=\"").Append(Dot(n.Kind + "\\n" + n.Title)).Append("\"];").AppendLine();
      }
      foreach (GraphEdge e in _edges) {
        if (!_relations.Checked && e.Kind != EdgeKind.Contains && e.Kind != EdgeKind.Resource) continue;
        if (!_resources.Checked && (e.From.Level == 2 || e.To.Level == 2)) continue;
        b.Append("  \"").Append(Dot(e.From.Id)).Append("\" -> \"").Append(Dot(e.To.Id)).Append("\"");
        if (!String.IsNullOrWhiteSpace(e.Label)) b.Append(" [label=\"").Append(Dot(e.Label)).Append("\"]");
        b.AppendLine(";");
      }
      b.AppendLine("}");
      try { Clipboard.SetText(b.ToString()); _selection.Text = "Graphviz DOT copied"; } catch { }
    }

    private static String Dot(String value) => (value ?? String.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", String.Empty).Replace("\n", "\\n");

    private static String ParseStartIf(XElement container) {
      if (container == null) return null;
      XElement field = container.Elements().FirstOrDefault(x => x.Name.LocalName == "f" && String.Equals((String)x.Attribute("name"), "_fxStartIF", StringComparison.Ordinal));
      if (field == null) return null;
      var pairs = new List<String>();
      List<XElement> children = field.Elements().ToList();
      for (Int32 i = 0; i < children.Count; i++) {
        if (children[i].Name.LocalName != "k") continue;
        String key = (children[i].Value ?? String.Empty).Trim();
        String value = i + 1 < children.Count && children[i + 1].Name.LocalName == "e" ? (children[i + 1].Value ?? String.Empty).Trim() : String.Empty;
        if (key.Length > 0) pairs.Add(key + (value.Length > 0 ? " -> " + value : String.Empty));
      }
      return pairs.Count == 0 ? null : String.Join(", ", pairs);
    }

    private static Dictionary<String, String> ParseFieldMap(XElement container) {
      var result = new Dictionary<String, String>(StringComparer.OrdinalIgnoreCase);
      if (container == null) return result;
      foreach (XElement field in container.Elements().Where(x => x.Name.LocalName == "f")) {
        String name = ((String)field.Attribute("name") ?? String.Empty).Trim();
        if (name.Length == 0) continue;
        if (!field.Elements().Any()) { result[name] = (field.Value ?? String.Empty).Trim(); continue; }
        // Keep simple nested values readable for graph labels without flattening list/map fields into nonsense.
        List<XElement> leaves = field.Descendants().Where(x => x.Name.LocalName == "f" && !x.Elements().Any()).ToList();
        if (leaves.Count > 0 && leaves.Count <= 4) {
          String joined = String.Join(", ", leaves.Select(x => ((String)x.Attribute("name") ?? String.Empty) + "=" + (x.Value ?? String.Empty).Trim()));
          if (joined.Length <= 220) result[name] = joined;
        }
      }
      return result;
    }

    private static String BuildComponentDetail(String kind, String name, Dictionary<String, String> fields) {
      var b = new StringBuilder();
      b.Append(kind).Append(": ").Append(name);
      AppendDetail(b, "Resource", Fx(fields, "_fxResourceName"));
      AppendDetail(b, "Start", Fx(fields, "_fxWhenToStart"));
      AppendDetail(b, "Start FX", Fx(fields, "_fxStartFxName"));
      AppendDetail(b, "Start loc", Fx(fields, "_fxStartLoc"));
      AppendDetail(b, "Attach to", Fx(fields, "_fxAttachTo"));
      AppendDetail(b, "Owner", Fx(fields, "_fxOwnerName"));
      AppendDetail(b, "Target", Fx(fields, "_fxTargetName"));
      AppendDetail(b, "Stop", Fx(fields, "_fxWhenToStop"));
      AppendDetail(b, "Stop FX", Fx(fields, "_fxStopFxName"));
      AppendDetail(b, "Delay", Fx(fields, "_fxStartDelay"));
      return b.ToString();
    }

    private static void AppendDetail(StringBuilder b, String label, String value) {
      if (!String.IsNullOrWhiteSpace(value)) b.Append("\n").Append(label).Append(": ").Append(value.Trim());
    }

    private static String Fx(Dictionary<String, String> fields, String key, String fallback = "") => fields != null && fields.TryGetValue(key, out String value) && !String.IsNullOrWhiteSpace(value) ? value.Trim() : fallback;

    private static Boolean IsExternalAnchor(String name) {
      return name.Equals("DEFAULT", StringComparison.OrdinalIgnoreCase)
        || name.Equals("CASTER", StringComparison.OrdinalIgnoreCase)
        || name.Equals("TARGET", StringComparison.OrdinalIgnoreCase)
        || name.Equals("NOTHING", StringComparison.OrdinalIgnoreCase)
        || name.Equals("NOTARGET", StringComparison.OrdinalIgnoreCase);
    }

    private static Boolean LooksLikeFile(String value) {
      if (String.IsNullOrWhiteSpace(value)) return false;
      return !String.IsNullOrWhiteSpace(Path.GetExtension(value.Trim()));
    }

    private String ResolveTexturePath(String basePath) {
      String normalized = NormalizeResourcePath(basePath);
      foreach (String candidate in new[] { normalized + ".dds", normalized + ".tiny.dds", normalized + ".tex" }) {
        if (ResourceExists == null) return candidate;
        Boolean exists;
        if (!_resourceExistenceCache.TryGetValue(candidate, out exists)) {
          try { exists = ResourceExists(candidate); } catch { exists = false; }
          _resourceExistenceCache[candidate] = exists;
        }
        if (exists) return candidate;
      }
      return normalized + ".dds";
    }

    private static String NormalizeFxResource(String value) {
      String raw = (value ?? String.Empty).Trim().Replace('\\', '/');
      if (raw.Length == 0) return null;
      String ext = Path.GetExtension(raw).ToLowerInvariant();
      if (ext == ".prt") {
        if (raw.StartsWith("/resources/", StringComparison.OrdinalIgnoreCase) || raw.StartsWith("resources/", StringComparison.OrdinalIgnoreCase)) return NormalizeResourcePath(raw);
        if (raw.IndexOf("art/fx/particles/", StringComparison.OrdinalIgnoreCase) < 0) raw = "art/fx/particles/" + raw.TrimStart('/');
        return NormalizeResourcePath(raw);
      }
      if (ext == ".fxspec") return NormalizeFxSpec(raw);
      if (ext.Length > 0) return NormalizeResourcePath(raw);
      return null;
    }

    private static String NormalizeFxSpec(String value) {
      String p = (value ?? String.Empty).Trim().Replace('\\', '/');
      if (!p.EndsWith(".fxspec", StringComparison.OrdinalIgnoreCase)) p += ".fxspec";
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

    private static IEnumerable<String> ExtractResourcePaths(String value) {
      if (String.IsNullOrWhiteSpace(value)) yield break;
      String normalized = value.Replace('\\', '/');
      foreach (String part in normalized.Split(new[] { ' ', '\t', '\r', '\n', ',', ';', '"', '\'', '<', '>', '[', ']', '(', ')' }, StringSplitOptions.RemoveEmptyEntries)) {
        String ext = Path.GetExtension(part).ToLowerInvariant();
        if (ext == ".fxspec" || ext == ".prt" || ext == ".dds" || ext == ".gr2" || ext == ".mat" || ext == ".tex" || ext == ".epp" || ext == ".jba" || ext == ".bnk" || ext == ".wem" || ext == ".wav") {
          yield return ext == ".fxspec" ? NormalizeFxSpec(part) : NormalizeResourcePath(part);
        }
      }
    }

    private static Color NodeColor(String kind) {
      String k = (kind ?? String.Empty).ToLowerInvariant();
      if (k == "fxspec" || k == "epp" || k == "prt") return Color.FromArgb(63, 83, 129);
      if (k.Contains("emitter")) return Color.FromArgb(117, 76, 42);
      if (k == "model") return Color.FromArgb(42, 99, 105);
      if (k.Contains("lightning")) return Color.FromArgb(82, 72, 130);
      if (k.Contains("light")) return Color.FromArgb(117, 103, 42);
      if (k.Contains("projector")) return Color.FromArgb(112, 61, 102);
      if (k.Contains("sound") || k.Contains("audio")) return Color.FromArgb(52, 105, 72);
      if (k.Contains("transform") || k.Contains("attacher") || k.Contains("fader") || k.Contains("group") || k.Contains("arc")) return Color.FromArgb(72, 76, 84);
      if (k == "dds" || k == "tex") return Color.FromArgb(70, 90, 116);
      if (k == "gr2") return Color.FromArgb(61, 105, 97);
      if (k == "animation") return Color.FromArgb(92, 72, 105);
      return Color.FromArgb(68, 73, 82);
    }

    private static Color EdgeColor(EdgeKind kind) {
      return kind switch {
        EdgeKind.Attach => Color.FromArgb(225, 158, 88),
        EdgeKind.Anchor => Color.FromArgb(105, 185, 225),
        EdgeKind.Trigger => Color.FromArgb(218, 105, 119),
        EdgeKind.Target => Color.FromArgb(192, 123, 220),
        EdgeKind.Operator => Color.FromArgb(160, 166, 175),
        EdgeKind.Resource => Color.FromArgb(113, 191, 135),
        _ => Color.FromArgb(76, 94, 110)
      };
    }

    private static Color Blend(Color a, Color b, Single t) {
      t = Math.Max(0f, Math.Min(1f, t));
      return Color.FromArgb(a.A,
        (Int32)(a.R + (b.R - a.R) * t),
        (Int32)(a.G + (b.G - a.G) * t),
        (Int32)(a.B + (b.B - a.B) * t));
    }

    protected override void Dispose(Boolean disposing) {
      if (disposing) {
        _tooltip?.Dispose();
        _nodeFont?.Dispose();
        _nodeBoldFont?.Dispose();
      }
      base.Dispose(disposing);
    }

    private enum EdgeKind { Contains, Resource, Attach, Anchor, Trigger, Operator, Target }

    private sealed class GraphNode {
      public String Id;
      public String Kind;
      public String Title;
      public String Detail;
      public String ResourcePath;
      public Boolean IsResource;
      public Boolean Exists;
      public Int32 Level;
      public RectangleF Bounds;
      public Dictionary<String, String> Fields;
    }

    private sealed class GraphEdge {
      public GraphNode From;
      public GraphNode To;
      public String Label;
      public EdgeKind Kind;
    }
  }

  internal static class EffectGraphDrawingExtensions {
    public static void FillRoundedRectangle(this Graphics graphics, Brush brush, RectangleF bounds, Single radius) {
      using GraphicsPath path = RoundedRectangle(bounds, radius);
      graphics.FillPath(brush, path);
    }

    public static void DrawRoundedRectangle(this Graphics graphics, Pen pen, RectangleF bounds, Single radius) {
      using GraphicsPath path = RoundedRectangle(bounds, radius);
      graphics.DrawPath(pen, path);
    }

    private static GraphicsPath RoundedRectangle(RectangleF bounds, Single radius) {
      Single diameter = Math.Max(1f, Math.Min(Math.Min(bounds.Width, bounds.Height), radius * 2f));
      var path = new GraphicsPath();
      RectangleF arc = new RectangleF(bounds.Left, bounds.Top, diameter, diameter);
      path.AddArc(arc, 180, 90);
      arc.X = bounds.Right - diameter; path.AddArc(arc, 270, 90);
      arc.Y = bounds.Bottom - diameter; path.AddArc(arc, 0, 90);
      arc.X = bounds.Left; path.AddArc(arc, 90, 90);
      path.CloseFigure();
      return path;
    }
  }
}
