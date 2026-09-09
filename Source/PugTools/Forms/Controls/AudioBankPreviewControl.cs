using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace PugTools {
  /// <summary>
  /// Offline Wwise/BNK inspector.  Everything in this view comes from the bank
  /// itself; BnkIdDict is only an optional label source when the user already
  /// generated it.  The semantic graph therefore works on a clean install.
  /// </summary>
  internal sealed class AudioBankPreviewControl : UserControl {
    private const Int32 MaxGraphNodes = 900;
    private const Int32 MaxGraphEdges = 2400;
    private readonly TabControl _tabs;
    private readonly AudioBankGraphCanvas _graph;
    private readonly ListView _objects;
    private readonly ListView _controls;
    private readonly WwiseCurvePreviewControl _curvePreview;
    private readonly ListView _media;
    private readonly TextBox _details;
    private readonly ToolStripTextBox _search;
    private readonly ToolStripLabel _summary;
    private readonly ToolStripButton _fit;
    private readonly ToolStripButton _relations;
    private readonly ToolStripButton _mediaNodes;

    private FileFormat_BNK _bank;
    private String _sourcePath;
    private readonly Dictionary<UInt32, ViewWEM> _embedded = new Dictionary<UInt32, ViewWEM>();

    internal Func<String, Boolean> ResourceExists { get; set; }
    internal Action<String> OpenResourceRequested { get; set; }
    internal Action<ViewWEM> PlayEmbeddedRequested { get; set; }

    internal AudioBankPreviewControl() {
      BackColor = SystemColors.Window;

      ToolStrip strip = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Dock = DockStyle.Top };
      _fit = new ToolStripButton("Fit") { ToolTipText = "Fit the complete graph" };
      _relations = new ToolStripButton("Relations") { CheckOnClick = true, Checked = true, ToolTipText = "Show semantic relation lines" };
      _mediaNodes = new ToolStripButton("Media") { CheckOnClick = true, Checked = true, ToolTipText = "Show WEM/bank resource nodes" };
      _search = new ToolStripTextBox { AutoSize = false, Width = 190, ToolTipText = "Find object/event/resource by id or name" };
      _summary = new ToolStripLabel { Alignment = ToolStripItemAlignment.Right };
      strip.Items.Add(_fit);
      strip.Items.Add(_relations);
      strip.Items.Add(_mediaNodes);
      strip.Items.Add(new ToolStripSeparator());
      strip.Items.Add(new ToolStripLabel("Find:"));
      strip.Items.Add(_search);
      strip.Items.Add(_summary);
      Controls.Add(strip);

      _tabs = new TabControl { Dock = DockStyle.Fill };
      Controls.Add(_tabs);
      _tabs.BringToFront();

      TabPage graphPage = new TabPage("Graph");
      SplitContainer graphSplit = new SplitContainer {
        Dock = DockStyle.Fill,
        Orientation = Orientation.Vertical
      };
      _graph = new AudioBankGraphCanvas { Dock = DockStyle.Fill, BackColor = Color.FromArgb(29, 31, 36) };
      _details = new TextBox {
        Dock = DockStyle.Fill,
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Vertical,
        BackColor = SystemColors.Window
      };
      graphSplit.Panel1.Controls.Add(_graph);
      graphSplit.Panel2.Controls.Add(_details);
      graphPage.Controls.Add(graphSplit);
      _tabs.TabPages.Add(graphPage);

      TabPage objectsPage = new TabPage("HIRC objects");
      _objects = NewListView();
      _objects.Columns.Add("Type", 150);
      _objects.Columns.Add("ID", 105);
      _objects.Columns.Add("Name", 260);
      _objects.Columns.Add("Relations / media", 500);
      objectsPage.Controls.Add(_objects);
      _tabs.TabPages.Add(objectsPage);

      TabPage controlsPage = new TabPage("Controls / RTPC");
      SplitContainer controlsSplit = new SplitContainer {
        Dock = DockStyle.Fill,
        Orientation = Orientation.Horizontal
      };
      _controls = NewListView();
      _controls.Columns.Add("Kind", 120);
      _controls.Columns.Add("Owner", 210);
      _controls.Columns.Add("Group / parameter", 220);
      _controls.Columns.Add("Target / value", 260);
      _controls.Columns.Add("Details", 440);
      _curvePreview = new WwiseCurvePreviewControl { Dock = DockStyle.Fill };
      controlsSplit.Panel1.Controls.Add(_controls);
      controlsSplit.Panel2.Controls.Add(_curvePreview);
      controlsPage.Controls.Add(controlsSplit);
      _tabs.TabPages.Add(controlsPage);

      TabPage mediaPage = new TabPage("Media / banks");
      _media = NewListView();
      _media.Columns.Add("Kind", 110);
      _media.Columns.Add("ID / bank", 170);
      _media.Columns.Add("Path", 480);
      _media.Columns.Add("Status", 120);
      mediaPage.Controls.Add(_media);
      _tabs.TabPages.Add(mediaPage);

      _fit.Click += delegate { _graph.FitGraph(); };
      _relations.CheckedChanged += delegate { _graph.ShowRelations = _relations.Checked; };
      _mediaNodes.CheckedChanged += delegate { _graph.ShowMediaNodes = _mediaNodes.Checked; };
      _search.TextChanged += delegate { _graph.FindAndSelect(_search.Text); };
      _graph.SelectionChanged += node => _details.Text = BuildNodeDetails(node);
      _graph.NodeActivated += ActivateNode;
      _objects.DoubleClick += delegate { ActivateTaggedListItem(_objects); };
      _controls.DoubleClick += delegate { ActivateTaggedListItem(_controls); };
      _controls.SelectedIndexChanged += delegate { ShowSelectedControlCurve(); };
      _media.DoubleClick += delegate { ActivateTaggedListItem(_media); };

      // SplitContainer validates SplitterDistance against its *current* client size. During a
      // UserControl constructor that size is still the tiny design/default size, so assigning a
      // large distance/minimum can throw before the Asset Browser is even shown. Apply the desired
      // layout only after WinForms has completed the first real layout pass and always clamp it.
      SplitContainerSafeLayout.ApplyOnLoad(this, graphSplit, 760, 0, 220);
      SplitContainerSafeLayout.ApplyOnLoad(this, controlsSplit, 430, 0, 150);
    }

    private static ListView NewListView() {
      return new ListView {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        GridLines = true,
        HideSelection = false,
        MultiSelect = false
      };
    }

    internal void ClearPreview() {
      _bank = null;
      _sourcePath = null;
      _embedded.Clear();
      _objects.Items.Clear();
      _controls.Items.Clear();
      _curvePreview.SetCurve(null, null);
      _media.Items.Clear();
      _details.Clear();
      _summary.Text = String.Empty;
      _search.Text = String.Empty;
      _graph.LoadGraph(null);
    }

    internal void LoadBank(String sourcePath, FileFormat_BNK bank) {
      ClearPreview();
      _sourcePath = sourcePath ?? String.Empty;
      _bank = bank;
      if (bank == null) return;

      if (bank.DIDX?.Wems != null) {
        foreach (ViewWEM wem in bank.DIDX.Wems) {
          if (wem != null && !_embedded.ContainsKey(wem.Id)) _embedded.Add(wem.Id, wem);
        }
      }

      List<AudioBankGraphNode> nodes = new List<AudioBankGraphNode>();
      List<AudioBankGraphEdge> edges = new List<AudioBankGraphEdge>();
      Dictionary<String, AudioBankGraphNode> byKey = new Dictionary<String, AudioBankGraphNode>(StringComparer.OrdinalIgnoreCase);
      Dictionary<UInt32, FileFormat_BNK_HIRC_Object> hircById = bank.HIRC != null && bank.HIRC.Objects != null
        ? bank.HIRC.Objects.GroupBy(x => x.Id).ToDictionary(x => x.Key, x => x.First())
        : new Dictionary<UInt32, FileFormat_BNK_HIRC_Object>();

      AudioBankGraphNode AddNode(String key, String title, String subtitle, Int32 level,
                                 AudioGraphNodeKind kind, Object tag = null, String resourcePath = null) {
        if (byKey.TryGetValue(key, out AudioBankGraphNode existing)) return existing;
        if (nodes.Count >= MaxGraphNodes) {
          const String omittedKey = "__graph_omitted__";
          if (byKey.TryGetValue(omittedKey, out AudioBankGraphNode omitted)) return omitted;
          omitted = new AudioBankGraphNode {
            Key = omittedKey, Title = "More nodes omitted",
            Subtitle = "Graph capped at " + MaxGraphNodes + " nodes; use HIRC / Controls tabs for full data",
            Level = 3, Kind = AudioGraphNodeKind.Missing
          };
          byKey.Add(omittedKey, omitted);
          nodes.Add(omitted);
          return omitted;
        }
        AudioBankGraphNode node = new AudioBankGraphNode {
          Key = key, Title = title ?? key, Subtitle = subtitle ?? String.Empty,
          Level = level, Kind = kind, Tag = tag, ResourcePath = resourcePath
        };
        byKey.Add(key, node);
        nodes.Add(node);
        return node;
      }

      AudioBankGraphNode AddHirc(FileFormat_BNK_HIRC_Object obj) {
        String label = ResolveOptionalName(obj.Id);
        String title = String.IsNullOrWhiteSpace(label) ? obj.Id.ToString() : label;
        return AddNode("h:" + obj.Id, title, HircTypeName(obj.Type) + " | " + obj.Id, HircLevel(obj.Type), AudioGraphNodeKind.Hirc, obj);
      }

      if (bank.HIRC?.Objects != null) {
        // Prioritize semantic event/action/audio objects.  Very large banks can
        // contain thousands of low-level Wwise helpers which add little value to
        // an overview and are still available in the HIRC table below.
        IEnumerable<FileFormat_BNK_HIRC_Object> ordered = bank.HIRC.Objects
          .OrderBy(o => HircPriority(o.Type)).ThenBy(o => o.Id)
          .Take(520);
        foreach (FileFormat_BNK_HIRC_Object obj in ordered) AddHirc(obj);

        foreach (FileFormat_BNK_HIRC_Object obj in ordered) {
          AudioBankGraphNode from = AddHirc(obj);

          if (obj.Type == 4) {
            foreach (UInt32 actionId in obj.EventActions) {
              AudioBankGraphNode to = hircById.TryGetValue(actionId, out FileFormat_BNK_HIRC_Object action)
                ? AddHirc(action)
                : AddNode("missing-h:" + actionId, actionId.ToString(), "Missing Event Action", 1, AudioGraphNodeKind.Missing);
              AddGraphEdge(edges, from, to, "action");
            }
          }

          if (obj.Type == 3 && obj.ActionObjectId != 0) {
            AudioBankGraphNode to = hircById.TryGetValue(obj.ActionObjectId, out FileFormat_BNK_HIRC_Object target)
              ? AddHirc(target)
              : AddNode("missing-h:" + obj.ActionObjectId, obj.ActionObjectId.ToString(), "External / unresolved HIRC target", 2, AudioGraphNodeKind.Missing);
            AddGraphEdge(edges, from, to, "target");
          }

          if (obj.ParentId != 0) AddHircRelation(from, obj.ParentId, "parent", hircById, AddHirc, AddNode, edges);
          if (obj.OutputBusId != 0) AddHircRelation(from, obj.OutputBusId, "output bus", hircById, AddHirc, AddNode, edges);
          if (obj.AttenuationId != 0) AddHircRelation(from, obj.AttenuationId, "attenuation", hircById, AddHirc, AddNode, edges);
          foreach (UInt32 effectId in obj.EffectIds.Take(8))
            AddHircRelation(from, effectId, "effect", hircById, AddHirc, AddNode, edges);

          foreach (UInt32 childId in obj.Children.Take(80))
            AddHircRelation(from, childId, "child", hircById, AddHirc, AddNode, edges);

          foreach (WwiseSwitchGrouping grouping in obj.SwitchGroupings.Take(80)) {
            foreach (UInt32 itemId in grouping.Items.Take(40))
              AddHircRelation(from, itemId, "switch " + grouping.SwitchId, hircById, AddHirc, AddNode, edges);
          }

          foreach (WwiseDuckedBus duck in obj.DuckedBusses.Take(32))
            AddHircRelation(from, duck.BusId, "ducks", hircById, AddHirc, AddNode, edges);

          if (obj.Type == 10 && obj.Children.Count == 0) {
            foreach (UInt32 childId in obj.AudioIds.Take(80))
              AddHircRelation(from, childId, "child", hircById, AddHirc, AddNode, edges);
          }

          if (obj.Type == 2 || obj.Type == 11) {
            AddMediaRelation(from, obj.AudioId, "audio", edges, byKey, AddNode);
            if (obj.AudioSourceId != 0 && obj.AudioSourceId != obj.AudioId)
              AddMediaRelation(from, obj.AudioSourceId, "source", edges, byKey, AddNode);
          }
        }
      }

      if (bank.STID?.SoundBanks != null) {
        foreach (FileFormat_BNK_STID_SoundBank linked in bank.STID.SoundBanks.Take(120)) {
          String path = ResolveLinkedBankPath(linked.Name);
          Boolean exists = !String.IsNullOrWhiteSpace(path) && ResourceExists?.Invoke(path) == true;
          AudioBankGraphNode node = AddNode("bank:" + linked.Name, linked.Name,
            (exists ? "Linked sound bank ✓" : "Linked sound bank") + " | " + linked.Id, 3,
            AudioGraphNodeKind.Bank, linked, path);
          node.Exists = exists;
          if (bank.HIRC?.Objects != null) {
            foreach (FileFormat_BNK_HIRC_Object action in bank.HIRC.Objects.Where(o => o.Type == 3 && o.SoundBankId == linked.Id).Take(80)) {
              AudioBankGraphNode from = AddHirc(action);
              AddGraphEdge(edges, from, node, "bank");
            }
          }
        }
      }

      FillObjectList(bank, hircById);
      FillControlsList(bank);
      FillMediaList(bank);
      _graph.LoadGraph(new AudioBankGraphModel(nodes, edges));
      _summary.Text = "Bank " + (bank.BKHD?.Id.ToString() ?? "?")
        + " | Wwise " + (bank.BKHD?.Version.ToString() ?? "?")
        + " | HIRC " + (bank.HIRC?.Objects?.Count ?? 0)
        + " | RTPC " + (bank.HIRC?.Objects?.Sum(o => o.Rtpcs.Count) ?? 0)
        + " | ENVS " + (bank.ENVS?.Curves?.Count ?? 0)
        + " | WEM " + _embedded.Count;
      _tabs.SelectedIndex = 0;
      _graph.FitGraph();
    }

    private static void AddGraphEdge(List<AudioBankGraphEdge> edges, AudioBankGraphNode from, AudioBankGraphNode to, String label) {
      if (edges == null || from == null || to == null || edges.Count >= MaxGraphEdges) return;
      if (!edges.Any(e => e.From == from && e.To == to && String.Equals(e.Label, label, StringComparison.Ordinal)))
        edges.Add(new AudioBankGraphEdge(from, to, label));
    }

    private static void AddHircRelation(AudioBankGraphNode from, UInt32 targetId, String label,
                                        Dictionary<UInt32, FileFormat_BNK_HIRC_Object> hircById,
                                        Func<FileFormat_BNK_HIRC_Object, AudioBankGraphNode> addHirc,
                                        Func<String, String, String, Int32, AudioGraphNodeKind, Object, String, AudioBankGraphNode> addNode,
                                        List<AudioBankGraphEdge> edges) {
      if (targetId == 0 || from == null) return;
      AudioBankGraphNode to = hircById.TryGetValue(targetId, out FileFormat_BNK_HIRC_Object target)
        ? addHirc(target)
        : addNode("missing-h:" + targetId, targetId.ToString(), "External / unresolved HIRC object", 2, AudioGraphNodeKind.Missing, null, null);
      AddGraphEdge(edges, from, to, label);
    }

    private void AddMediaRelation(AudioBankGraphNode from, UInt32 mediaId, String label,
                                  List<AudioBankGraphEdge> edges, Dictionary<String, AudioBankGraphNode> byKey,
                                  Func<String, String, String, Int32, AudioGraphNodeKind, Object, String, AudioBankGraphNode> addNode) {
      if (mediaId == 0) return;
      String key = "wem:" + mediaId;
      AudioBankGraphNode media;
      if (byKey.TryGetValue(key, out media)) {
        AddGraphEdge(edges, from, media, label);
        return;
      }

      if (_embedded.TryGetValue(mediaId, out ViewWEM embedded)) {
        media = addNode(key, FriendlyMediaName(mediaId), "Embedded WEM | " + FormatBytes(embedded.Data?.Length ?? 0), 3,
          AudioGraphNodeKind.Media, embedded, null);
        media.Exists = true;
      } else {
        String path = StreamedMediaPath(mediaId);
        Boolean exists = ResourceExists?.Invoke(path) == true;
        media = addNode(key, FriendlyMediaName(mediaId), exists ? "Streamed WEM ✓" : "Streamed WEM ✕", 3,
          AudioGraphNodeKind.Media, null, path);
        media.Exists = exists;
      }
      AddGraphEdge(edges, from, media, label);
    }

    private void FillObjectList(FileFormat_BNK bank, Dictionary<UInt32, FileFormat_BNK_HIRC_Object> byId) {
      _objects.BeginUpdate();
      try {
        if (bank.HIRC?.Objects == null) return;
        List<FileFormat_BNK_HIRC_Object> sorted = bank.HIRC.Objects.OrderBy(o => o.Type).ThenBy(o => o.Id).ToList();
        foreach (FileFormat_BNK_HIRC_Object obj in sorted.Take(5000)) {
          String relations = DescribeRelations(obj, byId);
          ListViewItem item = new ListViewItem(HircTypeName(obj.Type));
          item.SubItems.Add(obj.Id.ToString());
          item.SubItems.Add(ResolveOptionalName(obj.Id));
          item.SubItems.Add(relations);
          item.Tag = obj;
          _objects.Items.Add(item);
        }
        if (sorted.Count > 5000) {
          ListViewItem more = new ListViewItem("…");
          more.SubItems.Add(String.Empty);
          more.SubItems.Add((sorted.Count - 5000).ToString("N0") + " more HIRC objects");
          more.SubItems.Add("Display capped at 5,000 rows; graph prioritizes event/action/audio objects.");
          _objects.Items.Add(more);
        }
      } finally { _objects.EndUpdate(); }
    }

    private void FillControlsList(FileFormat_BNK bank) {
      _controls.BeginUpdate();
      try {
        Int32 rows = 0;
        if (bank.ENVS?.Curves != null) {
          for (Int32 i = 0; i < bank.ENVS.Curves.Count; i++) {
            FileFormat_BNK_ENVS_Curve curve = bank.ENVS.Curves[i];
            String envName = i switch { 0 => "Obstruction Volume", 1 => "Obstruction LPF", 2 => "Occlusion Volume", 3 => "Occlusion LPF", _ => "Environment curve " + i };
            String points = String.Join("; ", curve.Points.Take(8).Select(p => p.X.ToString("0.###") + "→" + p.Y.ToString("0.###")));
            AddControlRow("ENVS", "SoundBank environment", envName, curve.Enabled ? "enabled" : "disabled", points, null, curve); rows++;
          }
        }
        if (bank.HIRC?.Objects == null) return;
        foreach (FileFormat_BNK_HIRC_Object obj in bank.HIRC.Objects.OrderBy(o => o.Id)) {
          String owner = ResolveOptionalName(obj.Id);
          owner = String.IsNullOrWhiteSpace(owner) ? HircTypeName(obj.Type) + " [" + obj.Id + "]" : owner + " [" + obj.Id + "]";

          if (obj.Type == 3 && (obj.StateGroupId != 0 || obj.SwitchGroupId != 0)) {
            AddControlRow(obj.StateGroupId != 0 ? "Set State" : "Set Switch", owner,
              (obj.StateGroupId != 0 ? obj.StateGroupId : obj.SwitchGroupId).ToString(),
              (obj.StateId != 0 ? obj.StateId : obj.SwitchId).ToString(),
              "Event Action " + ActionTypeName(obj.ActionType), obj); rows++;
          }

          foreach (WwiseStateGroup group in obj.StateGroups) {
            if (group.States.Count == 0) {
              AddControlRow("State group", owner, FriendlyId(group.GroupId), "(default)", "sync " + group.SyncType, obj); rows++;
            }
            foreach (WwiseStateAssignment state in group.States.Take(128)) {
              AddControlRow("State", owner, FriendlyId(group.GroupId), FriendlyId(state.StateId),
                "settings " + FriendlyId(state.SettingsId) + " | sync " + group.SyncType, obj); rows++;
            }
          }

          if (obj.SwitchGroupId != 0 && obj.Type != 3) {
            AddControlRow("Switch group", owner, FriendlyId(obj.SwitchGroupId), FriendlyId(obj.DefaultSwitchId),
              obj.SwitchGroupings.Count + " mappings", obj); rows++;
          }
          foreach (WwiseSwitchGrouping grouping in obj.SwitchGroupings.Take(256)) {
            AddControlRow("Switch mapping", owner, FriendlyId(obj.SwitchGroupId), FriendlyId(grouping.SwitchId),
              String.Join(", ", grouping.Items.Take(16).Select(FriendlyId)), obj); rows++;
          }

          foreach (WwiseRtpcCurve rtpc in obj.Rtpcs.Take(128)) {
            String points = String.Join("; ", rtpc.Points.Take(6).Select(p => p.X.ToString("0.###") + "→" + p.Y.ToString("0.###")));
            if (rtpc.Points.Count > 6) points += "; …";
            AddControlRow("RTPC", owner, FriendlyId(rtpc.ParameterId), RtpcTargetName(rtpc.TargetType),
              rtpc.Points.Count + " points" + (String.IsNullOrWhiteSpace(points) ? String.Empty : " | " + points), obj, rtpc); rows++;
          }

          for (Int32 i = 0; i < obj.AttenuationCurves.Count; i++) {
            WwiseAttenuationCurve curve = obj.AttenuationCurves[i];
            String points = String.Join("; ", curve.Points.Take(6).Select(p => p.X.ToString("0.###") + "→" + p.Y.ToString("0.###")));
            if (curve.Points.Count > 6) points += "; …";
            AddControlRow("Attenuation", owner, "curve " + i, curve.Points.Count + " points", points, obj, curve); rows++;
          }

          foreach (WwiseDuckedBus duck in obj.DuckedBusses.Take(64)) {
            AddControlRow("Ducking", owner, FriendlyId(duck.BusId), duck.Volume.ToString("0.##") + " dB",
              "out " + duck.FadeOutMs + " ms | in " + duck.FadeInMs + " ms | shape " + duck.Shape, obj); rows++;
          }

          if (rows >= 6000) break;
        }
        if (rows >= 6000) AddControlRow("…", "Display capped", String.Empty, String.Empty, "First 6,000 control rows shown.", null);
      } finally { _controls.EndUpdate(); }
    }

    private void AddControlRow(String kind, String owner, String group, String target, String details, Object tag, Object curve = null) {
      ListViewItem item = new ListViewItem(kind ?? String.Empty);
      item.SubItems.Add(owner ?? String.Empty);
      item.SubItems.Add(group ?? String.Empty);
      item.SubItems.Add(target ?? String.Empty);
      item.SubItems.Add(details ?? String.Empty);
      item.Tag = curve != null ? new WwiseControlRowTag { Owner = tag as FileFormat_BNK_HIRC_Object, Curve = curve, Title = group } : tag;
      _controls.Items.Add(item);
    }

    private void ShowSelectedControlCurve() {
      if (_controls.SelectedItems.Count == 0) { _curvePreview.SetCurve(null, null); return; }
      Object tag = _controls.SelectedItems[0].Tag;
      if (tag is WwiseControlRowTag row) _curvePreview.SetCurve(row.Curve, row.Title);
      else _curvePreview.SetCurve(null, null);
    }

    private String FriendlyId(UInt32 id) {
      if (id == 0) return "0";
      String name = ResolveOptionalName(id);
      return String.IsNullOrWhiteSpace(name) ? id.ToString() : name + " [" + id + "]";
    }

    private static String RtpcTargetName(UInt32 type) {
      return type switch {
        0 => "Voice Volume", 3 => "Voice LPF", 5 => "Blend / Dry Level", 6 => "Feedforward",
        8 => "Priority", 9 => "Instance Limit", 15 => "Aux Send 0", 16 => "Aux Send 1",
        17 => "Aux Send 2", 18 => "Aux Send 3", 19 => "Game Aux Send", 22 => "Output Bus Volume",
        23 => "Output Bus LPF", 24 => "Bypass FX0", 25 => "Bypass FX1", 26 => "Bypass FX2",
        27 => "Bypass FX3", 28 => "Bypass All FX", 29 => "Motion Volume", 30 => "Motion LPF",
        _ => "Target " + type
      };
    }

    private static String ActionTypeName(UInt32 type) {
      return type switch {
        1 => "Stop", 2 => "Pause", 3 => "Resume", 4 => "Play", 5 => "Play and continue",
        8 => "Set Pitch", 9 => "Reset Pitch", 10 => "Set Volume", 11 => "Reset Volume",
        14 => "Set LPF", 15 => "Reset LPF", 18 => "Set State", 19 => "Set Game Parameter",
        20 => "Reset Game Parameter", 25 => "Set Switch", 26 => "Bypass", 30 => "Seek",
        _ => "Action " + type
      };
    }

    private void FillMediaList(FileFormat_BNK bank) {
      _media.BeginUpdate();
      try {
        foreach (KeyValuePair<UInt32, ViewWEM> pair in _embedded.OrderBy(x => x.Key)) {
          ListViewItem item = new ListViewItem("Embedded WEM");
          item.SubItems.Add(FriendlyMediaName(pair.Key));
          item.SubItems.Add("inside " + System.IO.Path.GetFileName(_sourcePath));
          item.SubItems.Add(FormatBytes(pair.Value.Data?.Length ?? 0));
          item.Tag = pair.Value;
          _media.Items.Add(item);
        }

        HashSet<UInt32> externalIds = new HashSet<UInt32>();
        if (bank.HIRC?.Objects != null) {
          foreach (FileFormat_BNK_HIRC_Object obj in bank.HIRC.Objects.Where(o => o.Type == 2 || o.Type == 11)) {
            if (obj.AudioId != 0 && !_embedded.ContainsKey(obj.AudioId)) externalIds.Add(obj.AudioId);
            if (obj.AudioSourceId != 0 && !_embedded.ContainsKey(obj.AudioSourceId)) externalIds.Add(obj.AudioSourceId);
          }
        }
        foreach (UInt32 id in externalIds.OrderBy(x => x).Take(2000)) {
          String path = StreamedMediaPath(id);
          Boolean exists = ResourceExists?.Invoke(path) == true;
          ListViewItem item = new ListViewItem("Streamed WEM");
          item.SubItems.Add(FriendlyMediaName(id));
          item.SubItems.Add(path);
          item.SubItems.Add(exists ? "Found" : "Missing");
          item.Tag = path;
          _media.Items.Add(item);
        }
        if (externalIds.Count > 2000) {
          ListViewItem more = new ListViewItem("…");
          more.SubItems.Add((externalIds.Count - 2000).ToString("N0") + " more streamed WEM IDs");
          more.SubItems.Add(String.Empty);
          more.SubItems.Add("Display capped");
          _media.Items.Add(more);
        }

        if (bank.STID?.SoundBanks != null) {
          foreach (FileFormat_BNK_STID_SoundBank linked in bank.STID.SoundBanks) {
            String path = ResolveLinkedBankPath(linked.Name);
            Boolean exists = !String.IsNullOrWhiteSpace(path) && ResourceExists?.Invoke(path) == true;
            ListViewItem item = new ListViewItem("Sound bank");
            item.SubItems.Add(linked.Name + (linked.Id != 0 ? " [" + linked.Id + "]" : String.Empty));
            item.SubItems.Add(path ?? String.Empty);
            item.SubItems.Add(exists ? "Found" : "Missing");
            item.Tag = path;
            _media.Items.Add(item);
          }
        }
      } finally { _media.EndUpdate(); }
    }

    private String DescribeRelations(FileFormat_BNK_HIRC_Object obj, Dictionary<UInt32, FileFormat_BNK_HIRC_Object> byId) {
      List<String> parts = new List<String>();
      if (obj.EventActions.Count > 0) parts.Add("actions: " + String.Join(", ", obj.EventActions.Take(12)));
      if (obj.ActionObjectId != 0) parts.Add("target: " + obj.ActionObjectId + NameSuffix(obj.ActionObjectId));
      if (obj.AudioIds.Count > 0) parts.Add("children: " + String.Join(", ", obj.AudioIds.Take(12)));
      if (obj.AudioId != 0) parts.Add("audio: " + FriendlyMediaName(obj.AudioId));
      if (obj.AudioSourceId != 0 && obj.AudioSourceId != obj.AudioId) parts.Add("source: " + FriendlyMediaName(obj.AudioSourceId));
      if (obj.ParentId != 0) parts.Add("parent: " + obj.ParentId + NameSuffix(obj.ParentId));
      if (obj.OutputBusId != 0) parts.Add("bus: " + obj.OutputBusId + NameSuffix(obj.OutputBusId));
      if (obj.AttenuationId != 0) parts.Add("atten: " + obj.AttenuationId + NameSuffix(obj.AttenuationId));
      if (obj.SwitchGroupId != 0) parts.Add("switch group: " + obj.SwitchGroupId + " (" + obj.SwitchGroupings.Count + " mappings)");
      if (obj.StateGroups.Count > 0) parts.Add("states: " + obj.StateGroups.Count + " groups");
      if (obj.Rtpcs.Count > 0) parts.Add("RTPC: " + obj.Rtpcs.Count);
      if (!String.IsNullOrWhiteSpace(obj.ParseWarning)) parts.Add("partial parse");
      return String.Join(" | ", parts);
    }

    private String NameSuffix(UInt32 id) {
      String name = ResolveOptionalName(id);
      return String.IsNullOrWhiteSpace(name) ? String.Empty : " (" + name + ")";
    }

    private String ResolveOptionalName(UInt32 id) {
      try {
        if (BnkIdDict.Instance.Data.TryGetValue(id, out String name)) return name ?? String.Empty;
      } catch { }
      return String.Empty;
    }

    private String FriendlyMediaName(UInt32 id) {
      String name = ResolveOptionalName(id);
      return String.IsNullOrWhiteSpace(name) ? id.ToString() : name + " [" + id + "]";
    }

    private String StreamedMediaPath(UInt32 id) {
      Boolean beta = (_bank?.BKHD?.Version ?? UInt32.MaxValue) <= 56;
      return beta ? "/resources/bnk/streamed/" + id + ".ogg" : "/resources/bnk2/streamed/" + id + ".wem";
    }

    private String ResolveLinkedBankPath(String bankName) {
      if (String.IsNullOrWhiteSpace(bankName)) return null;
      Boolean beta = (_bank?.BKHD?.Version ?? UInt32.MaxValue) <= 56;
      String root = beta ? "/resources/bnk/" : "/resources/bnk2/";
      String localeRoot = beta ? "/resources/en-us/bnk/" : "/resources/en-us/bnk2/";
      String local = root + bankName + ".bnk";
      if (ResourceExists?.Invoke(local) == true) return local;
      String locale = localeRoot + bankName + ".bnk";
      return ResourceExists?.Invoke(locale) == true ? locale : local;
    }

    private void ActivateTaggedListItem(ListView list) {
      if (list.SelectedItems.Count == 0) return;
      Object tag = list.SelectedItems[0].Tag;
      if (tag is ViewWEM wem) PlayEmbeddedRequested?.Invoke(wem);
      else if (tag is String path && !String.IsNullOrWhiteSpace(path)) OpenResourceRequested?.Invoke(path);
      else if (tag is WwiseControlRowTag row && row.Owner != null) _graph.SelectByKey("h:" + row.Owner.Id);
      else if (tag is FileFormat_BNK_HIRC_Object obj) _graph.SelectByKey("h:" + obj.Id);
    }

    private void ActivateNode(AudioBankGraphNode node) {
      if (node == null) return;
      if (node.Tag is ViewWEM wem) {
        PlayEmbeddedRequested?.Invoke(wem);
        return;
      }
      if (!String.IsNullOrWhiteSpace(node.ResourcePath)) OpenResourceRequested?.Invoke(node.ResourcePath);
    }

    private String BuildNodeDetails(AudioBankGraphNode node) {
      if (node == null) return String.Empty;
      StringBuilder sb = new StringBuilder();
      sb.AppendLine(node.Title);
      sb.AppendLine(node.Subtitle);
      sb.AppendLine();
      if (node.Tag is FileFormat_BNK_HIRC_Object obj) {
        sb.AppendLine("HIRC type: " + HircTypeName(obj.Type));
        sb.AppendLine("Object ID: " + obj.Id);
        String named = ResolveOptionalName(obj.Id);
        if (!String.IsNullOrWhiteSpace(named)) sb.AppendLine("Known event/action name: " + named);
        if (obj.EventActions.Count > 0) sb.AppendLine("Event actions: " + String.Join(", ", obj.EventActions));
        if (obj.ActionObjectId != 0) sb.AppendLine("Action target: " + obj.ActionObjectId + NameSuffix(obj.ActionObjectId));
        if (obj.AudioIds.Count > 0) sb.AppendLine("Music children: " + String.Join(", ", obj.AudioIds));
        if (obj.AudioId != 0) sb.AppendLine("Audio ID: " + FriendlyMediaName(obj.AudioId));
        if (obj.AudioSourceId != 0) sb.AppendLine("Audio source ID: " + FriendlyMediaName(obj.AudioSourceId));
        if (obj.Type == 2) sb.AppendLine("Embedded/stream mode: " + obj.Embed);
        if (obj.ParentId != 0) sb.AppendLine("Parent: " + FriendlyId(obj.ParentId));
        if (obj.OutputBusId != 0) sb.AppendLine("Output bus: " + FriendlyId(obj.OutputBusId));
        if (obj.AttenuationId != 0) sb.AppendLine("Attenuation: " + FriendlyId(obj.AttenuationId));
        if (obj.Is3DPositioned) sb.AppendLine("Positioning: 3D (source type " + obj.PositioningSourceType + ")");
        if (obj.ActionType != 0) sb.AppendLine("Action: " + ActionTypeName(obj.ActionType) + " | scope " + obj.ActionScope);
        if (obj.StateGroupId != 0) sb.AppendLine("Set state: group " + FriendlyId(obj.StateGroupId) + " → " + FriendlyId(obj.StateId));
        if (obj.SwitchGroupId != 0) sb.AppendLine("Switch group: " + FriendlyId(obj.SwitchGroupId) + " | default " + FriendlyId(obj.DefaultSwitchId != 0 ? obj.DefaultSwitchId : obj.SwitchId));
        if (obj.SoundBankId != 0) sb.AppendLine("Action sound bank ID: " + FriendlyId(obj.SoundBankId));
        if (obj.Children.Count > 0) sb.AppendLine("Children: " + String.Join(", ", obj.Children.Take(40)) + (obj.Children.Count > 40 ? ", …" : String.Empty));
        if (obj.EffectIds.Count > 0) sb.AppendLine("Effects: " + String.Join(", ", obj.EffectIds));
        if (obj.StateGroups.Count > 0) sb.AppendLine("State groups: " + obj.StateGroups.Count);
        if (obj.SwitchGroupings.Count > 0) sb.AppendLine("Switch mappings: " + obj.SwitchGroupings.Count);
        if (obj.Rtpcs.Count > 0) sb.AppendLine("RTPC curves: " + obj.Rtpcs.Count);
        if (obj.AttenuationCurves.Count > 0) sb.AppendLine("Attenuation curves: " + obj.AttenuationCurves.Count);
        if (obj.DuckedBusses.Count > 0) sb.AppendLine("Ducked busses: " + obj.DuckedBusses.Count);
        if (!String.IsNullOrWhiteSpace(obj.ParseWarning)) sb.AppendLine("Parser note: partial details (" + obj.ParseWarning + ")");
      } else if (node.Tag is ViewWEM wem) {
        sb.AppendLine("Embedded WEM ID: " + wem.Id);
        sb.AppendLine("Size: " + FormatBytes(wem.Data?.Length ?? 0));
        sb.AppendLine("Double-click to play with the existing WEM decoder.");
      } else if (!String.IsNullOrWhiteSpace(node.ResourcePath)) {
        sb.AppendLine("Resource: " + node.ResourcePath);
        sb.AppendLine(node.Exists ? "Status: found in loaded build" : "Status: not found in loaded build");
        sb.AppendLine("Double-click to open in Asset Browser.");
      }
      return sb.ToString().TrimEnd();
    }

    private static Int32 HircPriority(Byte type) {
      return type switch { 4 => 0, 3 => 1, 5 => 2, 6 => 2, 7 => 2, 10 => 2, 12 => 2, 13 => 2, 2 => 3, 11 => 3, 8 => 4, 14 => 4, _ => 5 };
    }

    private static Int32 HircLevel(Byte type) {
      return type switch { 4 => 0, 3 => 1, 5 => 2, 6 => 2, 7 => 2, 10 => 2, 12 => 2, 13 => 2, 2 => 3, 11 => 3, 8 => 3, 14 => 3, _ => 2 };
    }

    internal static String HircTypeName(Byte type) {
      return type switch {
        1 => "Settings", 2 => "Sound SFX / Voice", 3 => "Event Action", 4 => "Event",
        5 => "Random / Sequence Container", 6 => "Switch Container", 7 => "Actor-Mixer",
        8 => "Audio Bus", 9 => "Blend Container", 10 => "Music Segment", 11 => "Music Track",
        12 => "Music Switch Container", 13 => "Music Playlist Container", 14 => "Attenuation",
        15 => "Dialogue Event", 16 => "Motion Bus", 17 => "Motion FX", 18 => "Effect (ShareSet)", 19 => "Effect (Custom)",
        20 => "Auxiliary Bus", _ => "HIRC type " + type
      };
    }

    private static String FormatBytes(Int64 bytes) {
      if (bytes >= 1024 * 1024) return (bytes / 1048576.0).ToString("0.00") + " MB";
      if (bytes >= 1024) return (bytes / 1024.0).ToString("0.0") + " KB";
      return bytes + " B";
    }
  }

  internal sealed class WwiseControlRowTag {
    internal FileFormat_BNK_HIRC_Object Owner;
    internal Object Curve;
    internal String Title;
  }

  internal sealed class WwiseCurvePreviewControl : Control {
    private Object _curve;
    private String _title;
    private readonly Font _titleFont = new Font("Segoe UI", 9F, FontStyle.Bold);
    private readonly Font _axisFont = new Font("Segoe UI", 7.5F, FontStyle.Regular);
    private readonly Pen _gridPen = new Pen(Color.FromArgb(58, 75, 85), 1F);
    private readonly Pen _curvePen = new Pen(Color.FromArgb(100, 190, 235), 2F);
    private readonly SolidBrush _textBrush = new SolidBrush(Color.Gainsboro);

    internal WwiseCurvePreviewControl() {
      DoubleBuffered = true;
      BackColor = Color.FromArgb(28, 31, 36);
    }

    internal void SetCurve(Object curve, String title) {
      _curve = curve;
      _title = title ?? String.Empty;
      Invalidate();
    }

    protected override void Dispose(Boolean disposing) {
      if (disposing) { _titleFont.Dispose(); _axisFont.Dispose(); _gridPen.Dispose(); _curvePen.Dispose(); _textBrush.Dispose(); }
      base.Dispose(disposing);
    }

    protected override void OnPaint(PaintEventArgs e) {
      base.OnPaint(e);
      e.Graphics.Clear(BackColor);
      List<WwiseRtpcPoint> points = ExtractPoints(_curve);
      if (points == null || points.Count == 0) {
        e.Graphics.DrawString("Select an RTPC, attenuation or ENVS curve to preview it.", _titleFont, _textBrush, new PointF(12, 12));
        return;
      }

      RectangleF plot = new RectangleF(58, 32, Math.Max(20, ClientSize.Width - 78), Math.Max(20, ClientSize.Height - 64));
      for (Int32 i = 0; i <= 4; i++) {
        Single x = plot.Left + plot.Width * i / 4F;
        Single y = plot.Top + plot.Height * i / 4F;
        e.Graphics.DrawLine(_gridPen, x, plot.Top, x, plot.Bottom);
        e.Graphics.DrawLine(_gridPen, plot.Left, y, plot.Right, y);
      }

      Single minX = points.Min(p => p.X), maxX = points.Max(p => p.X);
      Single minY = points.Min(p => p.Y), maxY = points.Max(p => p.Y);
      if (Math.Abs(maxX - minX) < .000001F) { minX -= .5F; maxX += .5F; }
      if (Math.Abs(maxY - minY) < .000001F) { minY -= .5F; maxY += .5F; }
      PointF Map(WwiseRtpcPoint p) => new PointF(
        plot.Left + (p.X - minX) / (maxX - minX) * plot.Width,
        plot.Bottom - (p.Y - minY) / (maxY - minY) * plot.Height);
      for (Int32 i = 1; i < points.Count; i++) e.Graphics.DrawLine(_curvePen, Map(points[i - 1]), Map(points[i]));
      foreach (WwiseRtpcPoint point in points) {
        PointF p = Map(point);
        e.Graphics.FillEllipse(Brushes.WhiteSmoke, p.X - 2.5F, p.Y - 2.5F, 5F, 5F);
      }
      e.Graphics.DrawString(String.IsNullOrWhiteSpace(_title) ? "Wwise curve" : _title, _titleFont, _textBrush, new PointF(12, 8));
      e.Graphics.DrawString(minY.ToString("0.###"), _axisFont, _textBrush, new PointF(4, plot.Bottom - 10));
      e.Graphics.DrawString(maxY.ToString("0.###"), _axisFont, _textBrush, new PointF(4, plot.Top - 3));
      e.Graphics.DrawString(minX.ToString("0.###"), _axisFont, _textBrush, new PointF(plot.Left, plot.Bottom + 4));
      SizeF xmax = e.Graphics.MeasureString(maxX.ToString("0.###"), _axisFont);
      e.Graphics.DrawString(maxX.ToString("0.###"), _axisFont, _textBrush, new PointF(plot.Right - xmax.Width, plot.Bottom + 4));
    }

    private static List<WwiseRtpcPoint> ExtractPoints(Object curve) {
      if (curve is WwiseRtpcCurve rtpc) return rtpc.Points;
      if (curve is WwiseAttenuationCurve attenuation) return attenuation.Points;
      if (curve is FileFormat_BNK_ENVS_Curve env) return env.Points.Select(p => new WwiseRtpcPoint { X = p.X, Y = p.Y, Shape = p.Shape }).ToList();
      return null;
    }
  }

  internal enum AudioGraphNodeKind { Hirc, Media, Bank, Missing }

  internal sealed class AudioBankGraphNode {
    internal String Key;
    internal String Title;
    internal String Subtitle;
    internal Int32 Level;
    internal AudioGraphNodeKind Kind;
    internal Object Tag;
    internal String ResourcePath;
    internal Boolean Exists;
    internal RectangleF Bounds;
  }

  internal sealed class AudioBankGraphEdge {
    internal AudioBankGraphNode From;
    internal AudioBankGraphNode To;
    internal String Label;
    internal AudioBankGraphEdge(AudioBankGraphNode from, AudioBankGraphNode to, String label) { From = from; To = to; Label = label; }
  }

  internal sealed class AudioBankGraphModel {
    internal readonly List<AudioBankGraphNode> Nodes;
    internal readonly List<AudioBankGraphEdge> Edges;
    internal AudioBankGraphModel(IEnumerable<AudioBankGraphNode> nodes, IEnumerable<AudioBankGraphEdge> edges) {
      Nodes = nodes?.ToList() ?? new List<AudioBankGraphNode>();
      Edges = edges?.ToList() ?? new List<AudioBankGraphEdge>();
    }
  }

  internal sealed class AudioBankGraphCanvas : Control {
    private AudioBankGraphModel _model;
    private AudioBankGraphNode _selected;
    private Single _zoom = 1.0F;
    private PointF _pan = new PointF(20, 20);
    private Point _lastMouse;
    private Boolean _panning;
    private Boolean _showRelations = true;
    private Boolean _showMediaNodes = true;
    private readonly Font _titleFont;
    private readonly Font _smallFont;
    private readonly Pen _edgePen;
    private readonly Pen _borderPen;
    private readonly Pen _selectedBorderPen;
    private readonly SolidBrush _edgeTextBrush;
    private readonly SolidBrush _titleBrush;
    private readonly SolidBrush _subtitleBrush;
    private readonly SolidBrush _eventBrush;
    private readonly SolidBrush _actionBrush;
    private readonly SolidBrush _hircBrush;
    private readonly SolidBrush _mediaBrush;
    private readonly SolidBrush _bankBrush;
    private readonly SolidBrush _missingBrush;
    private readonly SolidBrush _missingResourceBrush;

    internal event Action<AudioBankGraphNode> SelectionChanged;
    internal event Action<AudioBankGraphNode> NodeActivated;

    internal Boolean ShowRelations { get => _showRelations; set { _showRelations = value; Invalidate(); } }
    internal Boolean ShowMediaNodes { get => _showMediaNodes; set { _showMediaNodes = value; LayoutGraph(); Invalidate(); } }

    internal AudioBankGraphCanvas() {
      DoubleBuffered = true;
      SetStyle(ControlStyles.ResizeRedraw, true);
      _titleFont = new Font("Segoe UI", 9F, FontStyle.Bold);
      _smallFont = new Font("Segoe UI", 7.5F, FontStyle.Regular);
      _edgePen = new Pen(Color.FromArgb(110, 165, 175, 190), 1.4F);
      _borderPen = new Pen(Color.FromArgb(155, 175, 190), 1F);
      _selectedBorderPen = new Pen(Color.Gold, 2.4F);
      _edgeTextBrush = new SolidBrush(Color.FromArgb(190, 205, 210, 220));
      _titleBrush = new SolidBrush(Color.WhiteSmoke);
      _subtitleBrush = new SolidBrush(Color.FromArgb(205, 215, 225));
      _eventBrush = new SolidBrush(Color.FromArgb(58, 91, 66));
      _actionBrush = new SolidBrush(Color.FromArgb(97, 78, 42));
      _hircBrush = new SolidBrush(Color.FromArgb(55, 62, 80));
      _mediaBrush = new SolidBrush(Color.FromArgb(48, 78, 108));
      _bankBrush = new SolidBrush(Color.FromArgb(75, 65, 100));
      _missingBrush = new SolidBrush(Color.FromArgb(105, 48, 48));
      _missingResourceBrush = new SolidBrush(Color.FromArgb(92, 49, 49));
    }

    protected override void Dispose(Boolean disposing) {
      if (disposing) {
        _titleFont.Dispose(); _smallFont.Dispose(); _edgePen.Dispose(); _borderPen.Dispose(); _selectedBorderPen.Dispose();
        _edgeTextBrush.Dispose(); _titleBrush.Dispose(); _subtitleBrush.Dispose(); _eventBrush.Dispose(); _actionBrush.Dispose();
        _hircBrush.Dispose(); _mediaBrush.Dispose(); _bankBrush.Dispose(); _missingBrush.Dispose(); _missingResourceBrush.Dispose();
      }
      base.Dispose(disposing);
    }

    internal void LoadGraph(AudioBankGraphModel model) {
      _model = model;
      _selected = null;
      _zoom = 1.0F;
      _pan = new PointF(20, 20);
      LayoutGraph();
      Invalidate();
    }

    internal void SelectByKey(String key) {
      if (_model == null || String.IsNullOrWhiteSpace(key)) return;
      AudioBankGraphNode node = _model.Nodes.FirstOrDefault(x => String.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase));
      if (node == null) return;
      SelectNode(node);
      CenterNode(node);
    }

    internal void FindAndSelect(String text) {
      if (_model == null || String.IsNullOrWhiteSpace(text)) return;
      String q = text.Trim();
      AudioBankGraphNode node = _model.Nodes.FirstOrDefault(x =>
        (x.Title?.IndexOf(q, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0
        || (x.Subtitle?.IndexOf(q, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0
        || (x.Key?.IndexOf(q, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0
        || (x.ResourcePath?.IndexOf(q, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0);
      if (node != null && (_showMediaNodes || node.Kind == AudioGraphNodeKind.Hirc || node.Kind == AudioGraphNodeKind.Missing)) {
        SelectNode(node);
        CenterNode(node);
      }
    }

    internal void FitGraph() {
      List<AudioBankGraphNode> visible = VisibleNodes().ToList();
      if (visible.Count == 0 || ClientSize.Width <= 20 || ClientSize.Height <= 20) return;
      RectangleF b = visible[0].Bounds;
      foreach (AudioBankGraphNode node in visible.Skip(1)) b = RectangleF.Union(b, node.Bounds);
      Single zx = (ClientSize.Width - 40F) / Math.Max(1F, b.Width);
      Single zy = (ClientSize.Height - 40F) / Math.Max(1F, b.Height);
      _zoom = Math.Max(.20F, Math.Min(1.35F, Math.Min(zx, zy)));
      _pan = new PointF(20F - b.X * _zoom, 20F - b.Y * _zoom);
      Invalidate();
    }

    private IEnumerable<AudioBankGraphNode> VisibleNodes() {
      if (_model == null) yield break;
      foreach (AudioBankGraphNode node in _model.Nodes) {
        if (!_showMediaNodes && (node.Kind == AudioGraphNodeKind.Media || node.Kind == AudioGraphNodeKind.Bank)) continue;
        yield return node;
      }
    }

    private void LayoutGraph() {
      if (_model == null) return;
      const Single w = 188F, h = 56F, xgap = 56F, ygap = 16F;
      for (Int32 level = 0; level <= 3; level++) {
        List<AudioBankGraphNode> levelNodes = VisibleNodes().Where(n => Math.Max(0, Math.Min(3, n.Level)) == level)
          .OrderBy(n => n.Kind).ThenBy(n => n.Title, StringComparer.OrdinalIgnoreCase).ToList();
        Single y = 20F;
        foreach (AudioBankGraphNode node in levelNodes) {
          node.Bounds = new RectangleF(20F + level * (w + xgap), y, w, h);
          y += h + ygap;
        }
      }
    }

    protected override void OnPaint(PaintEventArgs e) {
      base.OnPaint(e);
      if (_model == null) return;
      e.Graphics.TranslateTransform(_pan.X, _pan.Y);
      e.Graphics.ScaleTransform(_zoom, _zoom);
      e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

      HashSet<AudioBankGraphNode> visible = new HashSet<AudioBankGraphNode>(VisibleNodes());
      if (_showRelations) {
        foreach (AudioBankGraphEdge edge in _model.Edges) {
          if (!visible.Contains(edge.From) || !visible.Contains(edge.To)) continue;
          PointF a = new PointF(edge.From.Bounds.Right, edge.From.Bounds.Top + edge.From.Bounds.Height / 2F);
          PointF b = new PointF(edge.To.Bounds.Left, edge.To.Bounds.Top + edge.To.Bounds.Height / 2F);
          Single cx = (a.X + b.X) / 2F;
          e.Graphics.DrawBezier(_edgePen, a, new PointF(cx, a.Y), new PointF(cx, b.Y), b);
          if (_zoom >= .55F && !String.IsNullOrWhiteSpace(edge.Label))
            e.Graphics.DrawString(edge.Label, _smallFont, _edgeTextBrush, new PointF(cx + 3F, (a.Y + b.Y) / 2F - 7F));
        }
      }

      foreach (AudioBankGraphNode node in visible) DrawNode(e.Graphics, node);
    }

    private void DrawNode(Graphics g, AudioBankGraphNode node) {
      SolidBrush brush = node.Kind switch {
        AudioGraphNodeKind.Media => _mediaBrush,
        AudioGraphNodeKind.Bank => _bankBrush,
        AudioGraphNodeKind.Missing => _missingBrush,
        _ => node.Level switch { 0 => _eventBrush, 1 => _actionBrush, _ => _hircBrush }
      };
      if (!node.Exists && (node.Kind == AudioGraphNodeKind.Media || node.Kind == AudioGraphNodeKind.Bank)
          && !String.IsNullOrWhiteSpace(node.ResourcePath)) brush = _missingResourceBrush;
      g.FillRectangle(brush, node.Bounds);
      g.DrawRectangle(node == _selected ? _selectedBorderPen : _borderPen, node.Bounds.X, node.Bounds.Y, node.Bounds.Width, node.Bounds.Height);
      RectangleF t = new RectangleF(node.Bounds.X + 7, node.Bounds.Y + 6, node.Bounds.Width - 14, 20);
      RectangleF s = new RectangleF(node.Bounds.X + 7, node.Bounds.Y + 29, node.Bounds.Width - 14, 21);
      g.DrawString(Trim(node.Title, 31), _titleFont, _titleBrush, t);
      g.DrawString(Trim(node.Subtitle, 42), _smallFont, _subtitleBrush, s);
    }

    private static String Trim(String value, Int32 max) {
      value ??= String.Empty;
      return value.Length <= max ? value : value.Substring(0, Math.Max(1, max - 1)) + "…";
    }

    private PointF ScreenToWorld(Point p) => new PointF((p.X - _pan.X) / _zoom, (p.Y - _pan.Y) / _zoom);

    private AudioBankGraphNode HitTest(Point p) {
      PointF w = ScreenToWorld(p);
      return VisibleNodes().LastOrDefault(n => n.Bounds.Contains(w));
    }

    protected override void OnMouseDown(MouseEventArgs e) {
      base.OnMouseDown(e);
      Focus();
      _lastMouse = e.Location;
      AudioBankGraphNode hit = HitTest(e.Location);
      if (e.Button == MouseButtons.Left && hit != null) SelectNode(hit);
      else if (e.Button == MouseButtons.Left || e.Button == MouseButtons.Middle || e.Button == MouseButtons.Right) _panning = true;
    }

    protected override void OnMouseMove(MouseEventArgs e) {
      base.OnMouseMove(e);
      if (!_panning) return;
      _pan.X += e.X - _lastMouse.X;
      _pan.Y += e.Y - _lastMouse.Y;
      _lastMouse = e.Location;
      Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); _panning = false; }

    protected override void OnMouseWheel(MouseEventArgs e) {
      base.OnMouseWheel(e);
      Single old = _zoom;
      Single factor = e.Delta > 0 ? 1.12F : 1F / 1.12F;
      _zoom = Math.Max(.18F, Math.Min(3F, _zoom * factor));
      PointF before = new PointF((e.X - _pan.X) / old, (e.Y - _pan.Y) / old);
      _pan = new PointF(e.X - before.X * _zoom, e.Y - before.Y * _zoom);
      Invalidate();
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e) {
      base.OnMouseDoubleClick(e);
      AudioBankGraphNode node = HitTest(e.Location);
      if (node != null) NodeActivated?.Invoke(node);
      else FitGraph();
    }

    private void SelectNode(AudioBankGraphNode node) {
      _selected = node;
      SelectionChanged?.Invoke(node);
      Invalidate();
    }

    private void CenterNode(AudioBankGraphNode node) {
      if (node == null) return;
      _pan.X = ClientSize.Width / 2F - (node.Bounds.Left + node.Bounds.Width / 2F) * _zoom;
      _pan.Y = ClientSize.Height / 2F - (node.Bounds.Top + node.Bounds.Height / 2F) * _zoom;
      Invalidate();
    }
  }
}
