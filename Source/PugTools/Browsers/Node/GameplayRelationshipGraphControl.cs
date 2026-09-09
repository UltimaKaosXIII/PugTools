using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PugTools {
  /// <summary>
  /// Lightweight, database-free relationship graph used by the Gameplay Explorer.  The caller
  /// supplies semantic nodes/edges from the already loaded GOM models; this control only lays them
  /// out and provides pan/zoom/navigation.  Hard caps keep pathological abilities/quests from
  /// materializing an unbounded number of GDI objects.
  /// </summary>
  internal sealed class GameplayRelationshipGraphControl : UserControl {
    private const Int32 MaxNodes = 700;
    private const Int32 MaxEdges = 1600;
    private const Single NodeWidth = 218f;
    private const Single NodeHeight = 58f;
    private const Single ColumnGap = 72f;
    private const Single RowGap = 22f;

    private readonly ToolStrip _tools;
    private readonly ToolStripButton _fit;
    private readonly ToolStripButton _labels;
    private readonly ToolStripButton _copyDot;
    private readonly ToolStripButton _world;
    private readonly ToolStripButton _model;
    private readonly ToolStripLabel _stats;
    private readonly ToolStripLabel _selection;
    private readonly Panel _canvas;
    private readonly ToolTip _tooltip;
    private readonly Font _font;
    private readonly Font _boldFont;
    private readonly List<GraphNode> _nodes = new List<GraphNode>();
    private readonly List<GraphEdge> _edges = new List<GraphEdge>();
    private readonly Dictionary<String, GraphNode> _nodeById = new Dictionary<String, GraphNode>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<String, Bitmap> _iconCache = new Dictionary<String, Bitmap>(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource _iconLoadCancellation;
    private Int32 _iconLoadGeneration;

    private Single _zoom = 1f;
    private PointF _pan;
    private Boolean _dragging;
    private Point _dragStart;
    private PointF _panStart;
    private GraphNode _selected;
    private GraphNode _hovered;
    private String _title;
    private Int32 _omittedNodes;
    private Int32 _omittedEdges;
    private Boolean _layoutReady;

    public Action<String> NavigateRequested { get; set; }
    public Action<String> WorldMapNoteRequested { get; set; }
    public Action<String> ModelPreviewRequested { get; set; }
    public Func<String, Bitmap> IconLoader { get; set; }

    internal sealed class GraphNode {
      public String Id;
      public String Kind;
      public String Title;
      public String Detail;
      public String TargetFqn;
      public UInt64 TargetId;
      public String WorldMapNoteFqn;
      public String ModelPreviewFqn;
      public Int32 Column;
      public Int32 Order;
      public RectangleF Bounds;
    }

    private sealed class GraphEdge {
      public GraphNode From;
      public GraphNode To;
      public String Label;
    }

    public GameplayRelationshipGraphControl() {
      Dock = DockStyle.Fill;
      BackColor = Color.FromArgb(24, 26, 30);

      _tools = new ToolStrip { Dock = DockStyle.Top, GripStyle = ToolStripGripStyle.Hidden };
      _fit = new ToolStripButton("Fit");
      _fit.Click += delegate { FitGraph(); };
      _labels = new ToolStripButton("Edge labels") { CheckOnClick = true, Checked = true };
      _labels.CheckedChanged += delegate { _canvas.Invalidate(); };
      _copyDot = new ToolStripButton("Copy DOT") { ToolTipText = "Copy the displayed graph as Graphviz DOT" };
      _copyDot.Click += delegate { CopyDot(); };
      _world = new ToolStripButton("World") { Enabled = false, ToolTipText = "Find the selected map note in World Browser" };
      _world.Click += delegate { if (!String.IsNullOrWhiteSpace(_selected?.WorldMapNoteFqn)) WorldMapNoteRequested?.Invoke(_selected.WorldMapNoteFqn); };
      _model = new ToolStripButton("3D / Model") { Enabled = false, ToolTipText = "Open the selected appearance/item in Model Browser" };
      _model.Click += delegate { if (!String.IsNullOrWhiteSpace(_selected?.ModelPreviewFqn)) ModelPreviewRequested?.Invoke(_selected.ModelPreviewFqn); };
      _stats = new ToolStripLabel("No graph");
      _selection = new ToolStripLabel();
      _tools.Items.Add(_fit);
      _tools.Items.Add(_labels);
      _tools.Items.Add(_copyDot);
      _tools.Items.Add(_world);
      _tools.Items.Add(_model);
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
      _canvas.Resize += delegate { if (_layoutReady) _canvas.Invalidate(); };

      _tooltip = new ToolTip { InitialDelay = 300, ReshowDelay = 100, AutoPopDelay = 12000, ShowAlways = true };
      _font = new Font(SystemFonts.MessageBoxFont.FontFamily, Math.Max(7f, SystemFonts.MessageBoxFont.Size - .5f), FontStyle.Regular);
      _boldFont = new Font(SystemFonts.MessageBoxFont.FontFamily, Math.Max(7f, SystemFonts.MessageBoxFont.Size), FontStyle.Bold);
      Controls.Add(_canvas);
      Controls.Add(_tools);
    }

    protected override void Dispose(Boolean disposing) {
      if (disposing) {
        try { _iconLoadCancellation?.Cancel(); _iconLoadCancellation?.Dispose(); } catch { }
        _iconLoadCancellation = null;
        DisposeIcons();
        _font?.Dispose();
        _boldFont?.Dispose();
        _tooltip?.Dispose();
      }
      base.Dispose(disposing);
    }

    public void BeginGraph(String title) {
      try { _iconLoadCancellation?.Cancel(); _iconLoadCancellation?.Dispose(); } catch { }
      _iconLoadCancellation = null;
      _iconLoadGeneration++;
      DisposeIcons();
      _nodes.Clear();
      _edges.Clear();
      _nodeById.Clear();
      _selected = null;
      _hovered = null;
      _title = title ?? "Gameplay graph";
      _omittedNodes = 0;
      _omittedEdges = 0;
      _layoutReady = false;
      _zoom = 1f;
      _pan = PointF.Empty;
      _selection.Text = String.Empty;
      _world.Enabled = false;
      _model.Enabled = false;
      _stats.Text = "Building graph…";
      _canvas.Invalidate();
    }

    public String AddNode(String id, String kind, String title, String detail, String targetFqn, UInt64 targetId, Int32 column, String worldMapNoteFqn = null, String modelPreviewFqn = null) {
      if (String.IsNullOrWhiteSpace(id)) id = "node:" + (_nodes.Count + _omittedNodes).ToString(CultureInfo.InvariantCulture);
      if (String.IsNullOrWhiteSpace(modelPreviewFqn) && IsModelPreviewTarget(targetFqn)) modelPreviewFqn = targetFqn;
      if (_nodeById.TryGetValue(id, out GraphNode existing)) return existing.Id;
      if (_nodes.Count >= MaxNodes) { _omittedNodes++; return null; }
      GraphNode node = new GraphNode {
        Id = id,
        Kind = kind ?? "Node",
        Title = String.IsNullOrWhiteSpace(title) ? id : title,
        Detail = detail,
        TargetFqn = targetFqn,
        TargetId = targetId,
        WorldMapNoteFqn = worldMapNoteFqn,
        ModelPreviewFqn = modelPreviewFqn,
        Column = Math.Max(0, column),
        Order = _nodes.Count
      };
      _nodes.Add(node);
      _nodeById[id] = node;
      return id;
    }

    public void AddEdge(String fromId, String toId, String label) {
      if (String.IsNullOrWhiteSpace(fromId) || String.IsNullOrWhiteSpace(toId)) return;
      if (!_nodeById.TryGetValue(fromId, out GraphNode from) || !_nodeById.TryGetValue(toId, out GraphNode to)) return;
      if (_edges.Count >= MaxEdges) { _omittedEdges++; return; }
      _edges.Add(new GraphEdge { From = from, To = to, Label = label });
    }

    public void EndGraph() {
      LayoutGraph();
      _layoutReady = true;
      String omitted = (_omittedNodes > 0 || _omittedEdges > 0)
        ? " — capped: " + _omittedNodes.ToString("N0") + " nodes, " + _omittedEdges.ToString("N0") + " edges omitted"
        : String.Empty;
      _stats.Text = (_title ?? "Gameplay graph") + " — " + _nodes.Count.ToString("N0") + " nodes / " + _edges.Count.ToString("N0") + " relations" + omitted;
      if (_canvas.IsHandleCreated) {
        StartIconLoading();
        _canvas.BeginInvoke(new Action(FitGraph));
      } else {
        EventHandler handler = null;
        handler = delegate {
          _canvas.HandleCreated -= handler;
          if (!_canvas.IsDisposed) {
            StartIconLoading();
            _canvas.BeginInvoke(new Action(FitGraph));
          }
        };
        _canvas.HandleCreated += handler;
      }
    }

    private void LayoutGraph() {
      Dictionary<Int32, List<GraphNode>> columns = _nodes
        .GroupBy(x => x.Column)
        .OrderBy(x => x.Key)
        .ToDictionary(x => x.Key, x => x.OrderBy(n => n.Order).ToList());
      foreach (KeyValuePair<Int32, List<GraphNode>> column in columns) {
        Single x = 28f + column.Key * (NodeWidth + ColumnGap);
        Single y = 28f;
        foreach (GraphNode node in column.Value) {
          node.Bounds = new RectangleF(x, y, NodeWidth, NodeHeight);
          y += NodeHeight + RowGap;
        }
      }
    }

    private void FitGraph() {
      if (!_layoutReady || _nodes.Count == 0 || _canvas.ClientSize.Width < 20 || _canvas.ClientSize.Height < 20) return;
      RectangleF bounds = GraphBounds();
      if (bounds.Width <= 0 || bounds.Height <= 0) return;
      Single zx = (_canvas.ClientSize.Width - 36f) / bounds.Width;
      Single zy = (_canvas.ClientSize.Height - 36f) / bounds.Height;
      _zoom = Math.Max(.18f, Math.Min(1.45f, Math.Min(zx, zy)));
      _pan = new PointF(
        (_canvas.ClientSize.Width / _zoom - bounds.Width) * .5f - bounds.Left,
        (_canvas.ClientSize.Height / _zoom - bounds.Height) * .5f - bounds.Top);
      _canvas.Invalidate();
    }

    private RectangleF GraphBounds() {
      if (_nodes.Count == 0) return RectangleF.Empty;
      Single left = _nodes.Min(x => x.Bounds.Left);
      Single top = _nodes.Min(x => x.Bounds.Top);
      Single right = _nodes.Max(x => x.Bounds.Right);
      Single bottom = _nodes.Max(x => x.Bounds.Bottom);
      return RectangleF.FromLTRB(left, top, right, bottom);
    }

    private void CanvasPaint(Object sender, PaintEventArgs e) {
      Graphics g = e.Graphics;
      g.SmoothingMode = _dragging ? SmoothingMode.HighSpeed : SmoothingMode.AntiAlias;
      g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
      g.ScaleTransform(_zoom, _zoom);
      g.TranslateTransform(_pan.X, _pan.Y);

      using (Pen edgePen = new Pen(Color.FromArgb(115, 155, 165, 178), 1.25f / _zoom))
      using (Brush edgeLabelBack = new SolidBrush(Color.FromArgb(220, 34, 37, 42)))
      using (Brush edgeLabelText = new SolidBrush(Color.Gainsboro)) {
        foreach (GraphEdge edge in _edges) {
          PointF a = new PointF(edge.From.Bounds.Right, edge.From.Bounds.Top + edge.From.Bounds.Height / 2f);
          PointF b = new PointF(edge.To.Bounds.Left, edge.To.Bounds.Top + edge.To.Bounds.Height / 2f);
          if (edge.To.Column <= edge.From.Column) {
            a = new PointF(edge.From.Bounds.Left + edge.From.Bounds.Width / 2f, edge.From.Bounds.Bottom);
            b = new PointF(edge.To.Bounds.Left + edge.To.Bounds.Width / 2f, edge.To.Bounds.Top);
          }
          Single midX = (a.X + b.X) / 2f;
          using (GraphicsPath path = new GraphicsPath()) {
            if (Math.Abs(a.X - b.X) > 10f)
              path.AddBezier(a, new PointF(midX, a.Y), new PointF(midX, b.Y), b);
            else path.AddLine(a, b);
            g.DrawPath(edgePen, path);
          }
          if (_labels.Checked && !_dragging && _zoom > .38f && !String.IsNullOrWhiteSpace(edge.Label)) {
            String label = edge.Label.Length > 28 ? edge.Label.Substring(0, 25) + "…" : edge.Label;
            SizeF size = g.MeasureString(label, _font);
            PointF p = new PointF(midX - size.Width / 2f, (a.Y + b.Y) / 2f - size.Height / 2f);
            RectangleF r = new RectangleF(p.X - 3f, p.Y - 1f, size.Width + 6f, size.Height + 2f);
            g.FillRectangle(edgeLabelBack, r);
            g.DrawString(label, _font, edgeLabelText, p);
          }
        }
      }

      foreach (GraphNode node in _nodes) DrawNode(g, node);
    }

    private void DrawNode(Graphics g, GraphNode node) {
      RectangleF r = node.Bounds;
      Color fill = FillForKind(node.Kind);
      if (ReferenceEquals(node, _selected)) fill = Color.FromArgb(74, 94, 124);
      else if (ReferenceEquals(node, _hovered)) fill = Color.FromArgb(Math.Min(255, fill.R + 15), Math.Min(255, fill.G + 15), Math.Min(255, fill.B + 15));
      using (Brush background = new SolidBrush(fill))
      using (Pen border = new Pen(!String.IsNullOrWhiteSpace(node.TargetFqn) ? Color.FromArgb(225, 196, 115) : Color.FromArgb(105, 112, 125), 1.25f / _zoom))
      using (Brush kindBrush = new SolidBrush(Color.FromArgb(190, 199, 207)))
      using (Brush textBrush = new SolidBrush(Color.WhiteSmoke)) {
        g.FillRectangle(background, r);
        g.DrawRectangle(border, r.X, r.Y, r.Width, r.Height);
        Bitmap icon = null;
        if (!String.IsNullOrWhiteSpace(node.TargetFqn)) _iconCache.TryGetValue(node.TargetFqn, out icon);
        Single textLeft = r.X + 7f;
        if (icon != null) {
          RectangleF iconRect = new RectangleF(r.X + 7f, r.Y + 18f, 32f, 32f);
          g.DrawImage(icon, iconRect);
          textLeft = r.X + 46f;
        }
        g.DrawString(node.Kind ?? "Node", _font, kindBrush, new RectangleF(textLeft, r.Y + 5f, r.Right - textLeft - 7f, 17f));
        String title = OneLine(node.Title, icon == null ? 54 : 43);
        g.DrawString(title, _boldFont, textBrush, new RectangleF(textLeft, r.Y + 24f, r.Right - textLeft - 7f, r.Height - 28f));
      }
    }

    private static Color FillForKind(String kind) {
      String k = (kind ?? String.Empty).ToLowerInvariant();
      if (k.Contains("ability") || k.Contains("quest") || k.Contains("item") || k.Contains("npc")) return Color.FromArgb(48, 58, 76);
      if (k.Contains("effect") || k.Contains("step")) return Color.FromArgb(53, 63, 65);
      if (k.Contains("action") || k.Contains("task")) return Color.FromArgb(61, 53, 66);
      if (k.Contains("condition") || k.Contains("require")) return Color.FromArgb(65, 58, 47);
      if (k.Contains("reward") || k.Contains("target") || k.Contains("resource")) return Color.FromArgb(47, 64, 57);
      return Color.FromArgb(47, 50, 58);
    }

    private void CanvasMouseWheel(Object sender, MouseEventArgs e) {
      if (_nodes.Count == 0) return;
      PointF before = ScreenToGraph(e.Location);
      Single factor = e.Delta > 0 ? 1.13f : 1f / 1.13f;
      _zoom = Math.Max(.16f, Math.Min(3.5f, _zoom * factor));
      PointF after = ScreenToGraph(e.Location);
      _pan = new PointF(_pan.X + after.X - before.X, _pan.Y + after.Y - before.Y);
      _canvas.Invalidate();
    }

    private void CanvasMouseDown(Object sender, MouseEventArgs e) {
      _canvas.Focus();
      GraphNode hit = HitTest(e.Location);
      if (e.Button == MouseButtons.Left && hit != null) {
        _selected = hit;
        _world.Enabled = !String.IsNullOrWhiteSpace(hit.WorldMapNoteFqn) && WorldMapNoteRequested != null;
        _model.Enabled = !String.IsNullOrWhiteSpace(hit.ModelPreviewFqn) && ModelPreviewRequested != null;
        _selection.Text = (hit.Kind ?? "Node") + ": " + (hit.Title ?? hit.Id)
          + (!String.IsNullOrWhiteSpace(hit.TargetFqn) ? "  [double-click to open]" : String.Empty)
          + (!String.IsNullOrWhiteSpace(hit.WorldMapNoteFqn) ? "  [World available]" : String.Empty)
          + (!String.IsNullOrWhiteSpace(hit.ModelPreviewFqn) ? "  [3D available]" : String.Empty);
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
        _pan = new PointF(_panStart.X + (e.X - _dragStart.X) / _zoom, _panStart.Y + (e.Y - _dragStart.Y) / _zoom);
        _canvas.Invalidate();
        return;
      }
      GraphNode hit = HitTest(e.Location);
      if (!ReferenceEquals(hit, _hovered)) {
        _hovered = hit;
        _tooltip.SetToolTip(_canvas, hit == null ? String.Empty : BuildTooltip(hit));
        _canvas.Cursor = hit != null && !String.IsNullOrWhiteSpace(hit.TargetFqn) ? Cursors.Hand : Cursors.Default;
        _canvas.Invalidate();
      }
    }

    private void CanvasMouseDoubleClick(Object sender, MouseEventArgs e) {
      GraphNode hit = HitTest(e.Location);
      if (hit == null) return;
      if (!String.IsNullOrWhiteSpace(hit.TargetFqn)) NavigateRequested?.Invoke(hit.TargetFqn);
      else if (!String.IsNullOrWhiteSpace(hit.WorldMapNoteFqn)) WorldMapNoteRequested?.Invoke(hit.WorldMapNoteFqn);
    }

    private GraphNode HitTest(Point screen) {
      PointF p = ScreenToGraph(screen);
      for (Int32 i = _nodes.Count - 1; i >= 0; i--) if (_nodes[i].Bounds.Contains(p)) return _nodes[i];
      return null;
    }

    private PointF ScreenToGraph(Point p) {
      return new PointF(p.X / _zoom - _pan.X, p.Y / _zoom - _pan.Y);
    }

    private static String BuildTooltip(GraphNode node) {
      StringBuilder s = new StringBuilder();
      s.AppendLine(node.Kind ?? "Node");
      s.AppendLine(node.Title ?? node.Id);
      if (!String.IsNullOrWhiteSpace(node.TargetFqn)) s.AppendLine(node.TargetFqn);
      if (node.TargetId != 0) s.AppendLine("ID: " + node.TargetId);
      if (!String.IsNullOrWhiteSpace(node.WorldMapNoteFqn)) s.AppendLine("World map note: " + node.WorldMapNoteFqn);
      if (!String.IsNullOrWhiteSpace(node.ModelPreviewFqn)) s.AppendLine("3D/model preview: " + node.ModelPreviewFqn);
      if (!String.IsNullOrWhiteSpace(node.Detail)) s.Append(node.Detail);
      return s.ToString().Trim();
    }

    private void StartIconLoading() {
      if (IconLoader == null || _nodes.Count == 0 || IsDisposed) return;
      String[] fqns = _nodes
        .Where(x => ShouldLoadIcon(x) && !String.IsNullOrWhiteSpace(x.TargetFqn))
        .Select(x => x.TargetFqn)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Take(36)
        .ToArray();
      if (fqns.Length == 0) return;
      Int32 generation = ++_iconLoadGeneration;
      CancellationTokenSource cts = new CancellationTokenSource();
      _iconLoadCancellation = cts;
      Task.Run(() => {
        foreach (String fqn in fqns) {
          if (cts.IsCancellationRequested || IsDisposed || generation != _iconLoadGeneration) return;
          Bitmap icon = null;
          try { icon = IconLoader(fqn); } catch { }
          if (icon == null) continue;
          if (cts.IsCancellationRequested || IsDisposed || generation != _iconLoadGeneration) { icon.Dispose(); return; }
          try {
            BeginInvoke(new Action(() => {
              if (IsDisposed || generation != _iconLoadGeneration) { icon.Dispose(); return; }
              if (_iconCache.TryGetValue(fqn, out Bitmap old)) old.Dispose();
              _iconCache[fqn] = icon;
              _canvas.Invalidate();
            }));
          } catch { icon.Dispose(); return; }
        }
      }, cts.Token);
    }

    private static Boolean IsModelPreviewTarget(String fqn) {
      if (String.IsNullOrWhiteSpace(fqn)) return false;
      return fqn.StartsWith("npp.", StringComparison.OrdinalIgnoreCase)
        || fqn.StartsWith("ipp.", StringComparison.OrdinalIgnoreCase)
        || fqn.StartsWith("itm.", StringComparison.OrdinalIgnoreCase)
        || fqn.StartsWith("dyn.", StringComparison.OrdinalIgnoreCase)
        || fqn.StartsWith("mnt.", StringComparison.OrdinalIgnoreCase);
    }

    private static Boolean ShouldLoadIcon(GraphNode node) {
      if (node == null || String.IsNullOrWhiteSpace(node.TargetFqn)) return false;
      String fqn = node.TargetFqn;
      return fqn.StartsWith("abl.", StringComparison.OrdinalIgnoreCase)
        || fqn.StartsWith("itm.", StringComparison.OrdinalIgnoreCase)
        || fqn.StartsWith("ach.", StringComparison.OrdinalIgnoreCase)
        || fqn.StartsWith("cdx.", StringComparison.OrdinalIgnoreCase)
        || fqn.StartsWith("qst.", StringComparison.OrdinalIgnoreCase)
        || fqn.StartsWith("sche", StringComparison.OrdinalIgnoreCase)
        || fqn.StartsWith("tal.", StringComparison.OrdinalIgnoreCase)
        || fqn.StartsWith("mpn.", StringComparison.OrdinalIgnoreCase)
        || fqn.StartsWith("npc.", StringComparison.OrdinalIgnoreCase)
        || fqn.StartsWith("plc.", StringComparison.OrdinalIgnoreCase);
    }

    private void DisposeIcons() {
      foreach (Bitmap bitmap in _iconCache.Values) try { bitmap?.Dispose(); } catch { }
      _iconCache.Clear();
    }

    private void CopyDot() {
      if (_nodes.Count == 0) return;
      StringBuilder dot = new StringBuilder();
      dot.AppendLine("digraph gameplay {");
      dot.AppendLine("  rankdir=LR;");
      foreach (GraphNode node in _nodes) {
        dot.Append("  \"").Append(EscapeDot(node.Id)).Append("\" [label=\"").Append(EscapeDot((node.Kind ?? "Node") + "\\n" + (node.Title ?? node.Id))).AppendLine("\"]; ");
      }
      foreach (GraphEdge edge in _edges) {
        dot.Append("  \"").Append(EscapeDot(edge.From.Id)).Append("\" -> \"").Append(EscapeDot(edge.To.Id)).Append("\"");
        if (!String.IsNullOrWhiteSpace(edge.Label)) dot.Append(" [label=\"").Append(EscapeDot(edge.Label)).Append("\"]");
        dot.AppendLine(";");
      }
      dot.AppendLine("}");
      try { Clipboard.SetText(dot.ToString()); } catch { }
    }

    private static String EscapeDot(String value) => (value ?? String.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "").Replace("\n", "\\n");
    private static String OneLine(String value, Int32 max) {
      if (String.IsNullOrWhiteSpace(value)) return String.Empty;
      String text = value.Replace("\r", " ").Replace("\n", " ").Trim();
      return text.Length > max ? text.Substring(0, Math.Max(1, max - 1)) + "…" : text;
    }
  }
}
