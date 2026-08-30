using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using GomLib;
using GomLib.Models;
using NAudio.Wave;

namespace PugTools {
  public partial class WorldBrowser {
    private sealed class WorldConversationCinematicAction {
      public string Type;
      public string Value;
      public string Actor;
      public ulong ActorId;
      public float Time;
      public string Params;
      public bool Ignored;
    }

    private Form worldConversationPlaybackForm;
    private Label worldConversationPlaybackSpeaker;
    private Label worldConversationPlaybackStatus;
    private RichTextBox worldConversationPlaybackText;
    private FlowLayoutPanel worldConversationPlaybackChoices;
    private Button worldConversationPlaybackNext;
    private Conversation worldConversationPlaybackConversation;
    private DialogNode worldConversationPlaybackNode;
    private WorldNpcPlacement worldConversationPrimaryNpc;
    private WaveOutEvent worldConversationWaveOut;
    private System.Windows.Forms.Timer worldConversationAdvanceTimer;
    private int worldConversationPlaybackSerial;
    private readonly Dictionary<string, List<ViewWEM>> worldConversationAudioCache = new Dictionary<string, List<ViewWEM>>(StringComparer.OrdinalIgnoreCase);
    private object worldConversationAudioCacheAssets;
    private readonly Dictionary<string, WorldNpcAnimationClip> worldConversationAnimationCache = new Dictionary<string, WorldNpcAnimationClip>(StringComparer.OrdinalIgnoreCase);
    private object worldConversationAnimationCacheAssets;
    private readonly HashSet<WorldNpcPlacement> worldConversationAnimatedNpcs = new HashSet<WorldNpcPlacement>();
    private readonly HashSet<WorldNpcPlacement> worldConversationStagedNpcs = new HashSet<WorldNpcPlacement>();
    private readonly List<System.Windows.Forms.Timer> worldConversationCinematicTimers = new List<System.Windows.Forms.Timer>();

    private void StartWorldConversationPlayback(WorldInteractionInfo interaction) {
      if (!CanOpenWorldConversation(interaction)) return;
      WorldNpcPlacement primaryNpc = panelRender?.InteractionTargetWorldNpcPlacement;
      if (!TryLoadWorldConversation(interaction, out Conversation conversation, out string error)) {
        MessageBox.Show(this, error ?? "The conversation could not be loaded.", "Conversation playback", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return;
      }
      StartWorldConversationPlayback(conversation, primaryNpc);
    }

    private void StartWorldConversationPlayback(Conversation conversation) {
      StartWorldConversationPlayback(conversation, null, null);
    }

    private void StartWorldConversationPlayback(Conversation conversation, WorldNpcPlacement primaryNpc) {
      StartWorldConversationPlayback(conversation, primaryNpc, null);
    }

    private void StartWorldConversationPlayback(Conversation conversation, WorldNpcPlacement primaryNpc, long? startNode) {
      if (conversation?.NodeLookup == null || conversation.NodeLookup.Count == 0) return;
      EnsureWorldConversationPlaybackForm();
      StopWorldConversationPlayback(false);
      worldConversationPrimaryNpc = primaryNpc;
      worldConversationPlaybackConversation = conversation;
      worldConversationPlaybackForm.Text = "Conversation — " + (conversation.Fqn ?? "(unnamed)");
      worldConversationPlaybackStatus.Text = "Preparing conversation…";
      if (!worldConversationPlaybackForm.Visible) worldConversationPlaybackForm.Show(this);
      else worldConversationPlaybackForm.Activate();
      worldConversationPlaybackForm.BringToFront();

      // The tree preview is an authored-node debugger: Play Conversation must begin at the node the user selected,
      // not silently jump back to the server-selected root. Link nodes are intentionally accepted here because the
      // ordinary playback resolver follows them before playing and then continues through the normal child chain.
      if (startNode.HasValue && startNode.Value != 0) {
        worldConversationPlaybackStatus.Text = "Starting at selected node " + startNode.Value.ToString(CultureInfo.InvariantCulture) + "…";
        _ = PlayWorldConversationNodeAsync(startNode.Value);
        return;
      }

      List<KeyValuePair<int, long>> roots = conversation.RootNodes?.OrderBy(x => x.Key).ToList() ?? new List<KeyValuePair<int, long>>();
      if (roots.Count == 0) {
        long inferred = conversation.NodeLookup.Keys.OrderBy(x => x).FirstOrDefault();
        if (inferred != 0) roots.Add(new KeyValuePair<int, long>(0, inferred));
      }
      if (roots.Count == 0) {
        worldConversationPlaybackStatus.Text = "No playable root node was found.";
        return;
      }
      if (roots.Count == 1) { _ = PlayWorldConversationNodeAsync(roots[0].Value); return; }

      // There is no live player quest state in an offline viewer, so choosing Root 1 would be arbitrary. Present all
      // authored starts with their decoded predicates (same policy as Jedipedia) and let the user pick the state to preview.
      worldConversationPlaybackChoices.Controls.Clear();
      worldConversationPlaybackSpeaker.Text = "Select conversation start";
      worldConversationPlaybackText.Text = "The server picks the entry node from the player's quest state. Every root the tree offers is listed.";
      worldConversationPlaybackChoices.Height = Math.Min(330, Math.Max(150, roots.Count * 82 + 16));
      if (worldConversationPlaybackForm.Height < 560) worldConversationPlaybackForm.Height = 560;
      foreach (KeyValuePair<int, long> root in roots) {
        long resolved = ResolveWorldConversationLinkTarget(conversation, root.Value, out _);
        string condition = WorldConversationConditionSummary(conversation, resolved);
        DialogNode rootNode = resolved != 0 && conversation.NodeLookup.TryGetValue(resolved, out DialogNode found) ? found : null;
        string speaker = rootNode == null ? String.Empty : WorldConversationSpeakerName(conversation, rootNode);
        string line = rootNode == null ? String.Empty : WorldConversationChoiceOrLine(rootNode);
        if (String.IsNullOrWhiteSpace(line)) line = "Conversation root " + (root.Key + 1).ToString(CultureInfo.InvariantCulture);
        long selected = root.Value;
        AddWorldConversationStartControl(speaker, line.Trim(), condition, resolved, () => { worldConversationPlaybackChoices.Controls.Clear(); _ = PlayWorldConversationNodeAsync(selected); });
      }
      worldConversationPlaybackStatus.Text = "The server normally selects this root from the player's quest state.";
      worldConversationPlaybackNext.Enabled = false;
    }

    private void EnsureWorldConversationPlaybackForm() {
      if (worldConversationPlaybackForm != null && !worldConversationPlaybackForm.IsDisposed) return;
      worldConversationPlaybackForm = new Form {
        Text = "Conversation playback",
        FormBorderStyle = FormBorderStyle.SizableToolWindow,
        ShowInTaskbar = false,
        StartPosition = FormStartPosition.CenterParent,
        MinimumSize = new Size(560, 300),
        Size = new Size(760, 430),
        BackColor = Color.FromArgb(13, 24, 29)
      };
      worldConversationPlaybackSpeaker = new Label {
        Dock = DockStyle.Top, Height = 42, Padding = new Padding(12, 11, 10, 4), AutoEllipsis = true,
        Font = new Font(Font, FontStyle.Bold), ForeColor = Color.FromArgb(233, 189, 84), BackColor = Color.FromArgb(8, 19, 23)
      };
      worldConversationPlaybackText = new RichTextBox {
        Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None,
        BackColor = Color.FromArgb(13, 24, 29), ForeColor = Color.Gainsboro,
        Font = new Font(FontFamily.GenericSansSerif, 11f), DetectUrls = false, ScrollBars = RichTextBoxScrollBars.Vertical,
        Padding = new Padding(10)
      };
      worldConversationPlaybackChoices = new FlowLayoutPanel {
        Dock = DockStyle.Bottom, Height = 112, AutoScroll = true, FlowDirection = FlowDirection.TopDown,
        WrapContents = false, Padding = new Padding(9, 7, 9, 5), BackColor = Color.FromArgb(10, 21, 25)
      };
      worldConversationPlaybackStatus = new Label {
        Dock = DockStyle.Bottom, Height = 26, Padding = new Padding(10, 5, 5, 3), AutoEllipsis = true,
        ForeColor = Color.FromArgb(147, 178, 188), BackColor = Color.FromArgb(8, 19, 23)
      };
      var buttons = new FlowLayoutPanel {
        Dock = DockStyle.Bottom, Height = 43, FlowDirection = FlowDirection.RightToLeft, WrapContents = false,
        Padding = new Padding(7, 6, 7, 4), BackColor = Color.FromArgb(8, 19, 23)
      };
      var close = WorldDarkActionButton("Close", 92);
      var stop = WorldDarkActionButton("Stop", 92);
      var tree = WorldDarkActionButton("Show tree", 104);
      worldConversationPlaybackNext = WorldDarkActionButton("Next", 92);
      close.Click += (_, __) => { StopWorldConversationPlayback(true); worldConversationPlaybackForm.Hide(); };
      stop.Click += (_, __) => StopWorldConversationPlayback(false);
      tree.Click += (_, __) => {
        if (worldConversationPlaybackConversation == null) return;
        worldConversationPreviewPrimaryNpc = worldConversationPrimaryNpc;
        ShowWorldConversationPreview(worldConversationPlaybackConversation, false);
      };
      worldConversationPlaybackNext.Click += (_, __) => ContinueWorldConversationAfterLine();
      buttons.Controls.Add(close); buttons.Controls.Add(stop); buttons.Controls.Add(tree); buttons.Controls.Add(worldConversationPlaybackNext);

      worldConversationPlaybackForm.Controls.Add(worldConversationPlaybackText);
      worldConversationPlaybackForm.Controls.Add(worldConversationPlaybackChoices);
      worldConversationPlaybackForm.Controls.Add(worldConversationPlaybackStatus);
      worldConversationPlaybackForm.Controls.Add(buttons);
      worldConversationPlaybackForm.Controls.Add(worldConversationPlaybackSpeaker);
      worldConversationPlaybackForm.FormClosing += (_, e) => {
        if (_closing) return;
        e.Cancel = true;
        StopWorldConversationPlayback(true);
        worldConversationPlaybackForm.Hide();
      };
      worldConversationPlaybackForm.FormClosed += (_, __) => {
        StopWorldConversationPlayback(true);
        worldConversationPlaybackForm = null;
        worldConversationPlaybackSpeaker = null;
        worldConversationPlaybackStatus = null;
        worldConversationPlaybackText = null;
        worldConversationPlaybackChoices = null;
        worldConversationPlaybackNext = null;
      };
    }

    private async Task PlayWorldConversationNodeAsync(long rawNodeId) {
      Conversation conversation = worldConversationPlaybackConversation;
      if (conversation == null) return;
      long nodeId = ResolveWorldConversationLinkTarget(conversation, rawNodeId, out _);
      if (nodeId == 0 || conversation.NodeLookup == null || !conversation.NodeLookup.TryGetValue(nodeId, out DialogNode node) || node == null) {
        worldConversationPlaybackStatus.Text = "Missing dialog node " + rawNodeId.ToString(CultureInfo.InvariantCulture) + ".";
        return;
      }

      int serial = ++worldConversationPlaybackSerial;
      StopWorldConversationAudio();
      StopWorldConversationCinematicTimers();
      ClearWorldConversationAnimations();
      worldConversationPlaybackNode = node;
      worldConversationPlaybackChoices.Controls.Clear();
      worldConversationPlaybackChoices.Height = 112;
      worldConversationPlaybackNext.Enabled = false;

      string speaker = WorldConversationSpeakerName(conversation, node);
      string text = WorldConversationChoiceOrLine(node) ?? String.Empty;
      worldConversationPlaybackSpeaker.Text = speaker + "   •   node #" + node.NodeId.ToString(CultureInfo.InvariantCulture);
      worldConversationPlaybackText.Text = text;
      panelRender?.SetWorldConversationSubtitle(speaker, text);
      worldConversationPlaybackStatus.Text = "Applying cinematic actions…";
      StartWorldConversationCinematics(conversation, node, serial);

      bool audioStarted = false;
      try {
        worldConversationPlaybackStatus.Text = "Loading voice-over…";
        audioStarted = await TryPlayWorldConversationVoiceAsync(conversation, node, serial);
      } catch (Exception ex) {
        System.Diagnostics.Debug.WriteLine("Conversation VO failed: " + ex.Message);
      }
      if (serial != worldConversationPlaybackSerial || !ReferenceEquals(node, worldConversationPlaybackNode)) return;

      worldConversationPlaybackNext.Enabled = true;
      if (audioStarted) {
        worldConversationPlaybackStatus.Text = "Playing voice-over" + (node.IsPlayerNode ? " (first matching player voice)" : String.Empty) + ".";
      } else {
        worldConversationPlaybackStatus.Text = "No playable voice-over found; using subtitle timing.";
        ScheduleWorldConversationAdvance(text);
      }
    }

    private void ContinueWorldConversationAfterLine() {
      DialogNode node = worldConversationPlaybackNode;
      Conversation conversation = worldConversationPlaybackConversation;
      if (node == null || conversation == null) return;
      StopWorldConversationAudio();
      if (node.AbortsConversation) { FinishWorldConversation("Conversation aborted by this node."); return; }

      List<long> children = WorldConversationResolvedChildren(conversation, node);
      if (children.Count == 0) { FinishWorldConversation("End of conversation."); return; }
      if (children.Count == 1) { _ = PlayWorldConversationNodeAsync(children[0]); return; }
      ShowWorldConversationChoices(conversation, children);
    }

    private static List<long> WorldConversationResolvedChildren(Conversation conversation, DialogNode node) {
      var result = new List<long>();
      if (conversation == null || node?.ChildIds == null) return result;
      foreach (int raw in node.ChildIds) {
        long target = ResolveWorldConversationLinkTarget(conversation, raw, out _);
        if (target != 0 && !result.Contains(target)) result.Add(target);
      }
      return result;
    }

    private void ShowWorldConversationChoices(Conversation conversation, List<long> children) {
      StopWorldConversationAudio();
      worldConversationPlaybackChoices.Controls.Clear();
      int shown = 0;
      foreach (long child in children) {
        if (conversation?.NodeLookup == null || !conversation.NodeLookup.TryGetValue(child, out DialogNode option) || option == null) continue;
        string label = WorldConversationChoiceOrLine(option);
        if (String.IsNullOrWhiteSpace(label)) label = "Dialog node #" + option.NodeId.ToString(CultureInfo.InvariantCulture);
        string condition = WorldConversationConditionSummary(conversation, option.NodeId);
        long selected = child;
        AddWorldConversationChoiceControl(label.Trim(), condition, () => { worldConversationPlaybackChoices.Controls.Clear(); _ = PlayWorldConversationNodeAsync(selected); });
        shown++;
      }
      worldConversationPlaybackStatus.Text = shown > 0 ? "Choose a response." : "No readable child nodes.";
      worldConversationPlaybackNext.Enabled = false;
    }


    private void AddWorldConversationStartControl(string speaker, string text, string condition, long nodeId, Action click) {
      if (worldConversationPlaybackChoices == null) return;
      int width = Math.Max(500, worldConversationPlaybackChoices.ClientSize.Width - 36);
      bool hasCondition = !String.IsNullOrWhiteSpace(condition);
      var host = new Panel {
        Width = width, Height = hasCondition ? 76 : 58, Margin = new Padding(0, 0, 0, 6),
        BackColor = Color.FromArgb(10, 18, 22), BorderStyle = BorderStyle.FixedSingle, Cursor = Cursors.Hand
      };
      var speakerLabel = new Label {
        Location = new Point(10, 6), Width = Math.Max(1, width - 125), Height = 20, AutoEllipsis = true,
        Text = String.IsNullOrWhiteSpace(speaker) ? "Conversation" : speaker, Font = new Font(Font, FontStyle.Regular),
        ForeColor = Color.FromArgb(233, 189, 84), BackColor = host.BackColor
      };
      var nodeLabel = new Label {
        Location = new Point(Math.Max(10, width - 112), 6), Width = 98, Height = 20, TextAlign = ContentAlignment.TopRight,
        Text = nodeId == 0 ? String.Empty : "node " + nodeId.ToString(CultureInfo.InvariantCulture),
        ForeColor = Color.FromArgb(128, 151, 159), BackColor = host.BackColor
      };
      var textLabel = new Label {
        Location = new Point(10, 26), Width = Math.Max(1, width - 22), Height = hasCondition ? 24 : 28,
        AutoEllipsis = true, Text = text ?? String.Empty, Font = new Font(FontFamily.GenericSansSerif, 10.5f),
        ForeColor = Color.FromArgb(174, 229, 246), BackColor = host.BackColor
      };
      host.Controls.Add(speakerLabel); host.Controls.Add(nodeLabel); host.Controls.Add(textLabel);
      if (hasCondition) {
        var conditionLabel = new Label {
          Location = new Point(10, 51), Width = Math.Max(1, width - 22), Height = 19, AutoEllipsis = true,
          Text = condition.Trim(), Font = new Font(FontFamily.GenericMonospace, 8f),
          ForeColor = Color.FromArgb(145, 164, 170), BackColor = host.BackColor
        };
        host.Controls.Add(conditionLabel);
        conditionLabel.Click += (_, __) => click?.Invoke();
      }
      EventHandler choose = (_, __) => click?.Invoke();
      host.Click += choose; speakerLabel.Click += choose; nodeLabel.Click += choose; textLabel.Click += choose;
      worldConversationPlaybackChoices.Controls.Add(host);
    }

    // Jedipedia keeps branch predicates as metadata below a choice instead of concatenating them into the spoken/UI
    // text. Keep the response itself readable and show the decoded condition in a quieter secondary row.
    private void AddWorldConversationChoiceControl(string text, string condition, Action click) {
      if (worldConversationPlaybackChoices == null) return;
      int width = Math.Max(500, worldConversationPlaybackChoices.ClientSize.Width - 36);
      bool hasCondition = !String.IsNullOrWhiteSpace(condition);
      var host = new Panel {
        Width = width, Height = hasCondition ? 54 : 34, Margin = new Padding(0, 0, 0, 4),
        BackColor = worldConversationPlaybackChoices.BackColor
      };
      var button = WorldDarkActionButton(text ?? String.Empty, width);
      button.Location = new Point(0, 0); button.Height = 30; button.TextAlign = ContentAlignment.MiddleLeft; button.AutoEllipsis = true;
      button.Click += (_, __) => click?.Invoke();
      host.Controls.Add(button);
      if (hasCondition) {
        var conditionLabel = new Label {
          Location = new Point(10, 31), Width = Math.Max(1, width - 12), Height = 20, AutoEllipsis = true,
          Text = condition.Trim(), Font = new Font(FontFamily.GenericMonospace, 8f),
          ForeColor = Color.FromArgb(174, 164, 121), BackColor = host.BackColor
        };
        host.Controls.Add(conditionLabel);
      }
      worldConversationPlaybackChoices.Controls.Add(host);
    }

    private void ScheduleWorldConversationAdvance(string text) {
      worldConversationAdvanceTimer?.Stop();
      worldConversationAdvanceTimer?.Dispose();
      int milliseconds = (int)Math.Max(1600, Math.Min(12000, 900 + (text?.Length ?? 0) * 55));
      int serial = worldConversationPlaybackSerial;
      worldConversationAdvanceTimer = new System.Windows.Forms.Timer { Interval = milliseconds };
      worldConversationAdvanceTimer.Tick += (_, __) => {
        worldConversationAdvanceTimer.Stop();
        if (serial == worldConversationPlaybackSerial) ContinueWorldConversationAfterLine();
      };
      worldConversationAdvanceTimer.Start();
    }

    private async Task<bool> TryPlayWorldConversationVoiceAsync(Conversation conversation, DialogNode node, int serial) {
      if (conversation == null || node == null || currentAssets == null) return false;
      string audioFqn = !String.IsNullOrWhiteSpace(node.CnvAlienVOFQN) ? node.CnvAlienVOFQN : conversation.Fqn;
      long wantedNode = !String.IsNullOrWhiteSpace(node.CnvAlienVOFQN) && node.CnvAlienVONode != 0 ? node.CnvAlienVONode : node.NodeId;
      if (String.IsNullOrWhiteSpace(audioFqn) || wantedNode == 0) return false;

      foreach (var source in WorldConversationAudioCandidates(audioFqn, !String.IsNullOrWhiteSpace(node.CnvAlienVOFQN))) {
        List<ViewWEM> wems = LoadWorldConversationAcb(source.Path, source.BetaHint);
        if (wems == null || wems.Count == 0) continue;
        List<ViewWEM> matches = wems.Where(w => WorldConversationAudioNodeIndex(w?.WemName) == wantedNode).ToList();
        ViewWEM wem = ChooseWorldConversationWem(matches, node.IsPlayerNode);
        if (wem == null) continue;

        bool converted = wem.Vorbis != null;
        if (!converted) converted = await wem.ConvertWEM();
        if (!converted) {
          // RED/Beta ACBs can still live below a bnk2 path. Retry once with the legacy packed codebooks rather than
          // baking the directory generation into the VO decision.
          wem.IsBeta = !wem.IsBeta;
          converted = await wem.ConvertWEM();
        }
        if (!converted || wem.Vorbis == null || serial != worldConversationPlaybackSerial) continue;
        try {
          wem.Vorbis.Position = 0;
          var output = new WaveOutEvent { Volume = 1f };
          worldConversationWaveOut = output;
          output.Init(wem.Vorbis);
          output.PlaybackStopped += (_, __) => {
            if (IsDisposed || Disposing) return;
            try {
              BeginInvoke((Action)(() => {
                if (!ReferenceEquals(worldConversationWaveOut, output) || serial != worldConversationPlaybackSerial) { output.Dispose(); return; }
                worldConversationWaveOut = null;
                output.Dispose();
                ContinueWorldConversationAfterLine();
              }));
            } catch { try { output.Dispose(); } catch { } }
          };
          worldConversationPlaybackStatus.Text = "VO: " + wem.WemName;
          output.Play();
          return true;
        } catch (Exception ex) {
          System.Diagnostics.Debug.WriteLine("Conversation audio output failed: " + ex.Message);
          try { worldConversationWaveOut?.Dispose(); } catch { }
          worldConversationWaveOut = null;
        }
      }
      return false;
    }

    private IEnumerable<(string Path, bool BetaHint)> WorldConversationAudioCandidates(string fqn, bool alien) {
      string under = (fqn ?? String.Empty).Trim().ToLowerInvariant().Replace('.', '_');
      if (String.IsNullOrWhiteSpace(under)) yield break;
      if (alien || fqn.StartsWith("cnv.alien_vo", StringComparison.OrdinalIgnoreCase)) {
        yield return ("/resources/bnk2/" + under + ".acb", false);
        yield return ("/resources/bnk/" + under + ".acb", true);
        yield break;
      }
      string locale = WorldConversationAudioLocale();
      yield return ("/resources/" + locale + "/bnk2/" + under + ".acb", false);
      if (!String.Equals(locale, "en-us", StringComparison.OrdinalIgnoreCase)) yield return ("/resources/en-us/bnk2/" + under + ".acb", false);
      yield return ("/resources/" + locale + "/bnk/" + under + ".acb", true);
      if (!String.Equals(locale, "en-us", StringComparison.OrdinalIgnoreCase)) yield return ("/resources/en-us/bnk/" + under + ".acb", true);
    }

    private static string WorldConversationAudioLocale() {
      string selected = (GomLib.StringTable.SelectedLocalization ?? "enMale").ToLowerInvariant();
      if (selected.StartsWith("de", StringComparison.Ordinal)) return "de-de";
      if (selected.StartsWith("fr", StringComparison.Ordinal)) return "fr-fr";
      return "en-us";
    }

    private List<ViewWEM> LoadWorldConversationAcb(string path, bool betaHint) {
      if (String.IsNullOrWhiteSpace(path) || currentAssets == null) return null;
      if (!ReferenceEquals(worldConversationAudioCacheAssets, currentAssets)) {
        foreach (List<ViewWEM> oldList in worldConversationAudioCache.Values.Where(x => x != null))
          foreach (ViewWEM oldWem in oldList) try { oldWem?.Vorbis?.Dispose(); } catch { }
        worldConversationAudioCache.Clear();
        worldConversationAnimationCache.Clear();
        worldConversationAnimationCacheAssets = currentAssets;
        worldConversationAudioCacheAssets = currentAssets;
      }
      if (worldConversationAudioCache.TryGetValue(path, out List<ViewWEM> cached)) return cached;
      try {
        using var file = currentAssets.FindFile(path);
        if (file == null) { worldConversationAudioCache[path] = null; return null; }
        using Stream stream = file.OpenCopyInMemory();
        using var reader = new BinaryReader(stream);
        List<ViewWEM> parsed = ViewACB.ParseACB(reader);
        foreach (ViewWEM wem in parsed) if (wem != null) wem.IsBeta = betaHint;
        worldConversationAudioCache[path] = parsed;
        return parsed;
      } catch (Exception ex) {
        System.Diagnostics.Debug.WriteLine("Conversation ACB load failed " + path + ": " + ex.Message);
        worldConversationAudioCache[path] = null;
        return null;
      }
    }

    private static long WorldConversationAudioNodeIndex(string name) {
      if (String.IsNullOrWhiteSpace(name)) return -1;
      string clean = Path.GetFileNameWithoutExtension(name);
      int digit = -1;
      for (int i = clean.Length - 1; i >= 0; i--) if (Char.IsDigit(clean[i])) { digit = i; break; }
      if (digit < 0) return -1;
      int start = digit;
      while (start > 0 && Char.IsDigit(clean[start - 1])) start--;
      return Int64.TryParse(clean.Substring(start, digit - start + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed) ? parsed : -1;
    }

    private static string WorldConversationAudioSpeakerCode(string name) {
      if (String.IsNullOrWhiteSpace(name)) return String.Empty;
      string clean = Path.GetFileNameWithoutExtension(name).ToLowerInvariant();
      int digit = -1;
      for (int i = clean.Length - 1; i >= 0; i--) if (Char.IsDigit(clean[i])) { digit = i; break; }
      if (digit < 0 || digit + 1 >= clean.Length) return String.Empty;
      int suffix = digit + 1;
      while (suffix < clean.Length && clean[suffix] == '_') suffix++;
      return suffix < clean.Length ? clean.Substring(suffix) : String.Empty;
    }

    private static ViewWEM ChooseWorldConversationWem(List<ViewWEM> matches, bool playerNode) {
      if (matches == null || matches.Count == 0) return null;
      string localization = (GomLib.StringTable.SelectedLocalization ?? "enMale").ToLowerInvariant();
      bool female = localization.Contains("female");
      string genderSuffix = female ? "_f" : "_m";
      if (playerNode) {
        ViewWEM classVoice = matches.FirstOrDefault(w => {
          string speaker = WorldConversationAudioSpeakerCode(w?.WemName);
          return !String.IsNullOrWhiteSpace(speaker) && speaker.EndsWith(genderSuffix, StringComparison.Ordinal) &&
            speaker != "m" && speaker != "f" && speaker != "de_de_m" && speaker != "de_de_f" && speaker != "fr_fr_m" && speaker != "fr_fr_f";
        });
        if (classVoice != null) return classVoice;
      } else {
        string wanted = female ? "f" : "m";
        ViewWEM npcVoice = matches.FirstOrDefault(w => String.Equals(WorldConversationAudioSpeakerCode(w?.WemName), wanted, StringComparison.Ordinal));
        if (npcVoice == null && female) npcVoice = matches.FirstOrDefault(w => {
          string speaker = WorldConversationAudioSpeakerCode(w?.WemName);
          return speaker == "de_de_f" || speaker == "fr_fr_f";
        });
        if (npcVoice != null) return npcVoice;
      }
      return matches[0];
    }

    private void StopWorldConversationAudio() {
      worldConversationAdvanceTimer?.Stop();
      worldConversationAdvanceTimer?.Dispose();
      worldConversationAdvanceTimer = null;
      WaveOutEvent output = worldConversationWaveOut;
      worldConversationWaveOut = null;
      if (output != null) {
        try { output.Stop(); } catch { }
        try { output.Dispose(); } catch { }
      }
    }

    private void DisposeWorldConversationAudioCache() {
      foreach (List<ViewWEM> list in worldConversationAudioCache.Values.Where(x => x != null))
        foreach (ViewWEM wem in list) try { wem?.Vorbis?.Dispose(); } catch { }
      worldConversationAudioCache.Clear();
      worldConversationAudioCacheAssets = null;
      worldConversationAnimationCache.Clear();
      worldConversationAnimationCacheAssets = null;
    }

    private void StopWorldConversationPlayback(bool clearConversation) {
      ++worldConversationPlaybackSerial;
      StopWorldConversationAudio();
      StopWorldConversationCinematicTimers();
      ClearWorldConversationAnimations();
      ClearWorldConversationStaging();
      panelRender?.ClearWorldConversationCamera(true);
      panelRender?.ClearWorldConversationSubtitle();
      worldConversationPlaybackNode = null;
      if (worldConversationPlaybackChoices != null) worldConversationPlaybackChoices.Controls.Clear();
      if (worldConversationPlaybackNext != null) worldConversationPlaybackNext.Enabled = false;
      if (clearConversation) { worldConversationPlaybackConversation = null; worldConversationPrimaryNpc = null; }
      if (worldConversationPlaybackStatus != null) worldConversationPlaybackStatus.Text = clearConversation ? String.Empty : "Playback stopped.";
    }

    private void FinishWorldConversation(string status) {
      StopWorldConversationAudio();
      StopWorldConversationCinematicTimers();
      ClearWorldConversationAnimations();
      ClearWorldConversationStaging();
      panelRender?.ClearWorldConversationCamera(true);
      panelRender?.ClearWorldConversationSubtitle();
      worldConversationPlaybackStatus.Text = status;
      worldConversationPlaybackNext.Enabled = false;
      worldConversationPlaybackChoices.Controls.Clear();
    }

    private void ClearWorldConversationAnimations() {
      if (worldConversationAnimatedNpcs.Count == 0) return;
      foreach (WorldNpcPlacement placement in worldConversationAnimatedNpcs.ToArray()) panelRender?.ClearWorldConversationAnimation(placement);
      worldConversationAnimatedNpcs.Clear();
    }

    private void StartWorldConversationCinematics(Conversation conversation, DialogNode node, int serial) {
      ApplyWorldConversationStaging(conversation, node);
      List<WorldConversationCinematicAction> actions = ReadWorldConversationCinematicActions(conversation, node.NodeId);
      foreach (WorldConversationCinematicAction action in actions.OrderBy(x => x.Time)) {
        if (action == null || action.Ignored) continue;
        if (action.Time <= .001f) { ApplyWorldConversationCinematicAction(conversation, node, action); continue; }
        int milliseconds = Math.Max(1, (int)Math.Min(Int32.MaxValue, action.Time * 1000f));
        var timer = new System.Windows.Forms.Timer { Interval = milliseconds };
        timer.Tick += (_, __) => {
          timer.Stop(); timer.Dispose(); worldConversationCinematicTimers.Remove(timer);
          if (serial == worldConversationPlaybackSerial && ReferenceEquals(node, worldConversationPlaybackNode))
            ApplyWorldConversationCinematicAction(conversation, node, action);
        };
        worldConversationCinematicTimers.Add(timer);
        timer.Start();
      }
    }

    private void StopWorldConversationCinematicTimers() {
      foreach (System.Windows.Forms.Timer timer in worldConversationCinematicTimers.ToArray()) {
        try { timer.Stop(); timer.Dispose(); } catch { }
      }
      worldConversationCinematicTimers.Clear();
    }

    private void ClearWorldConversationStaging() {
      foreach (WorldNpcPlacement placement in worldConversationStagedNpcs.ToArray()) panelRender?.ClearWorldConversationActorState(placement);
      worldConversationStagedNpcs.Clear();
    }

    private WorldNpcPlacement FindWorldConversationSpeakerPlacement(Conversation conversation, DialogNode node) {
      if (node == null || node.IsPlayerNode) return null;
      ulong speakerId = node.SpeakerId != 0 ? node.SpeakerId : conversation?.DefaultSpeakerId ?? 0;
      string fqn = null;
      if (speakerId != 0) {
        try { fqn = currentDom?.GetObject(speakerId)?.Name; } catch { }
        if (String.IsNullOrWhiteSpace(fqn)) {
          try { fqn = currentDom?.ConversationLoader.LoadSpeaker(speakerId)?.Fqn; } catch { }
        }
      }
      return FindWorldConversationActorPlacement(fqn);
    }

    private WorldNpcPlacement FindWorldConversationActorPlacement(string actor) {
      if (String.IsNullOrWhiteSpace(actor) || worldNpcPlacements == null || worldNpcPlacements.Count == 0) return null;
      string clean = actor.Trim();
      if (worldConversationPrimaryNpc != null && String.Equals(worldConversationPrimaryNpc.SourceFqn, clean, StringComparison.OrdinalIgnoreCase)) return worldConversationPrimaryNpc;
      WorldNpcPlacement exact = worldNpcPlacements.FirstOrDefault(p => p != null && String.Equals(p.SourceFqn, clean, StringComparison.OrdinalIgnoreCase));
      if (exact != null) return exact;
      string leaf = clean;
      int colon = leaf.LastIndexOf(':'); if (colon >= 0 && colon + 1 < leaf.Length) leaf = leaf.Substring(colon + 1);
      int dot = leaf.LastIndexOf('.'); if (dot >= 0 && dot + 1 < leaf.Length) leaf = leaf.Substring(dot + 1);
      if (leaf.Length < 3) return null;
      List<WorldNpcPlacement> suffix = worldNpcPlacements.Where(p => p != null && !String.IsNullOrWhiteSpace(p.SourceFqn) &&
        (p.SourceFqn.EndsWith("." + leaf, StringComparison.OrdinalIgnoreCase) || p.SourceFqn.EndsWith("_" + leaf, StringComparison.OrdinalIgnoreCase))).Take(2).ToList();
      return suffix.Count == 1 ? suffix[0] : null;
    }

    private List<WorldConversationCinematicAction> ReadWorldConversationCinematicActions(Conversation conversation, long nodeId) {
      var result = new List<WorldConversationCinematicAction>();
      if (conversation == null || currentDom == null || nodeId == 0) return result;
      try {
        GomObject raw = conversation.Id != 0 ? currentDom.GetObject(conversation.Id) : null;
        if (raw == null && !String.IsNullOrWhiteSpace(conversation.Fqn)) raw = currentDom.GetObject(conversation.Fqn);
        if (raw?.Data == null) return result;

        var toolbox = new Dictionary<ulong, string>();
        object rawToolbox = WorldInteractionDataValue(raw.Data, "hydToolbox", "4611686142663780000");
        foreach (KeyValuePair<object, object> pair in WorldConversationMapEntries(rawToolbox)) {
          ulong id = WorldInteractionUnsigned(pair.Key);
          GomObjectData data = WorldConversationFirstObjectData(pair.Value, false);
          if (id == 0 || data == null) continue;
          string fqn = WorldInteractionText(WorldInteractionDataValue(data, "hydFQN", "4611686027152384722"));
          if (!String.IsNullOrWhiteSpace(fqn)) toolbox[id] = fqn;
        }

        GomObjectData nodeData = null;
        object rawDialogs = WorldInteractionDataValue(raw.Data, "cnvTreeDialogNodes_Prototype", "4611686050212071021");
        foreach (KeyValuePair<object, object> pair in WorldConversationMapEntries(rawDialogs)) {
          GomObjectData candidate = WorldConversationFirstObjectData(pair.Value, false);
          if (candidate == null) continue;
          long id = WonkInt64(WorldInteractionDataValue(candidate, "cnvNodeNumber", "4611686019044571365"));
          if (id == 0) id = WonkInt64(pair.Key);
          if (id == nodeId) { nodeData = candidate; break; }
        }
        if (nodeData == null) return result;

        // A node can contain class/gender/faction-specific cinematic variants. The client tests them in order; for a
        // preview with no player state Jedipedia deliberately chooses the first variant with NO predicates. Reading all
        // variants at once makes mutually-exclusive posture/animation actions overwrite each other at the same time and
        // commonly leaves the actor in the final static stance.
        List<object> variants = WorldInteractionListEntries(WorldInteractionDataValue(nodeData, "cnvHydraCinematics", "4611686031753695688")).ToList();
        GomObjectData selectedVariant = null;
        GomObjectData firstVariant = null;
        foreach (object cinematicRaw in variants) {
          GomObjectData candidate = WorldConversationFirstObjectData(cinematicRaw, false);
          if (candidate == null) continue;
          if (firstVariant == null) firstVariant = candidate;
          GomObjectData condition = WorldConversationFirstObjectData(
            WorldInteractionDataValue(candidate, "hydCondition", "4611686026567369621"), false);
          object rawPredicates = WorldInteractionDataValue(condition, "hydPredicates", "4611686029670182931");
          if (!WorldInteractionListEntries(rawPredicates).Any()) { selectedVariant = candidate; break; }
        }
        selectedVariant ??= firstVariant;
        if (selectedVariant == null) return result;

        // Later beats may omit hydTime; that means "same time as the previous beat", not zero. Resetting omitted times
        // to zero fires the end-of-line pose before the spoken gesture and makes the actor appear rigid even though the
        // action parser itself found valid animations.
        float beatTime = 0f;
        foreach (object blockRaw in WorldInteractionListEntries(WorldInteractionDataValue(selectedVariant, "hydActionBlocks", "4611686026567369455"))) {
          GomObjectData block = WorldConversationFirstObjectData(blockRaw, false);
          if (block == null) continue;
          object rawBeatTime = WorldInteractionDataValue(block, "hydTime", "4611686026643586509");
          if (rawBeatTime != null) beatTime = Math.Max(0f, SpnDynNumber(rawBeatTime, beatTime));
          foreach (object actionRaw in WorldInteractionListEntries(WorldInteractionDataValue(block, "hydActions", "4611686026567774554"))) {
            GomObjectData action = WorldConversationFirstObjectData(actionRaw, false);
            if (action == null) continue;
            string type = WorldInteractionText(WorldInteractionDataValue(action, "hydAction", "4611686026567774112"));
            string value = WorldInteractionText(WorldInteractionDataValue(action, "hydValue", "4611686026567774127"));
            ulong actorId = WorldInteractionUnsigned(WorldInteractionDataValue(action, "hydObject", "4611686142663780004"));
            toolbox.TryGetValue(actorId, out string actor);
            string parameters = WorldInteractionText(WorldInteractionDataValue(action, "hydParams", "4611686029434570001"));
            bool ignored = WorldConversationBool(WorldInteractionDataValue(action, "hydIgnore", "4611686034423470030"));
            if (!String.IsNullOrWhiteSpace(type)) result.Add(new WorldConversationCinematicAction {
              Type = type, Value = value, Actor = actor, ActorId = actorId, Time = beatTime, Params = parameters, Ignored = ignored
            });
          }
        }
      } catch (Exception ex) {
        System.Diagnostics.Debug.WriteLine("Conversation cinematic action read failed: " + ex.Message);
      }
      return result;
    }
  }
}
