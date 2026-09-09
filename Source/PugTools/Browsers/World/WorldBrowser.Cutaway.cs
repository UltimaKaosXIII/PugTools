using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace PugTools {
  public partial class WorldBrowser {
    private ToolStripMenuItem btnWorldYSlice;
    private ToolStripMenuItem btnWorldRestoreHiddenObjects;
    private ToolStripMenuItem btnWorldHideSelectedObject;
    private ToolStripMenuItem btnWorldStreamingDebug;
    private Panel worldSlicePanel;
    private Label worldSliceTitle;
    private Label worldSliceValue;
    private TrackBar worldSliceTrack;

    private void InitializeWorldCutawayUi() {
      if (btnWorldRenderMenu == null || worldSlicePanel != null) return;

      btnWorldYSlice = new ToolStripMenuItem("Y-slice / cutaway") {
        CheckOnClick = true,
        Checked = false,
        ToolTipText = "Vertical world cutaway. Move the slider down to clip geometry above a chosen world Y height."
      };
      btnWorldYSlice.CheckedChanged += (_, __) => {
        if (worldSlicePanel != null) worldSlicePanel.Visible = btnWorldYSlice.Checked;
        worldSettings.EnableVerticalSlice = btnWorldYSlice.Checked && worldSliceTrack != null && worldSliceTrack.Value < worldSliceTrack.Maximum - 1;
        ApplyWorldSettings();
        UpdateWorldSliceCaption();
        LayoutWorldCutawayControl();
      };
      btnWorldRenderMenu.DropDownItems.Add(new ToolStripSeparator());
      btnWorldRenderMenu.DropDownItems.Add(btnWorldYSlice);

      worldSlicePanel = new Panel {
        Size = new Size(86, 310),
        BackColor = Color.FromArgb(24, 29, 36),
        Visible = false,
        TabStop = false,
        Padding = new Padding(4)
      };
      worldSliceTitle = new Label {
        AutoSize = false,
        Text = "Y cut",
        ForeColor = Color.White,
        TextAlign = ContentAlignment.MiddleCenter,
        Location = new Point(4, 5),
        Size = new Size(78, 22)
      };
      worldSliceValue = new Label {
        AutoSize = false,
        Text = "Off",
        ForeColor = Color.Gainsboro,
        TextAlign = ContentAlignment.MiddleCenter,
        Location = new Point(4, 282),
        Size = new Size(78, 22)
      };
      worldSliceTrack = new TrackBar {
        Orientation = Orientation.Vertical,
        Minimum = 0,
        Maximum = 1000,
        Value = 1000,
        TickStyle = TickStyle.None,
        SmallChange = 10,
        LargeChange = 50,
        Location = new Point(19, 31),
        Size = new Size(50, 247),
        TabStop = false
      };
      worldSliceTrack.ValueChanged += (_, __) => {
        float fraction = worldSliceTrack.Value / 1000f;
        worldSettings.VerticalSliceFraction = fraction;
        worldSettings.EnableVerticalSlice = btnWorldYSlice?.Checked == true && worldSliceTrack.Value < 999;
        ApplyWorldSettings();
        UpdateWorldSliceCaption();
      };
      worldSliceTrack.DoubleClick += (_, __) => worldSliceTrack.Value = worldSliceTrack.Maximum;
      worldSlicePanel.Controls.Add(worldSliceTitle);
      worldSlicePanel.Controls.Add(worldSliceTrack);
      worldSlicePanel.Controls.Add(worldSliceValue);
      splitContainer3.Panel1.Controls.Add(worldSlicePanel);

      if (btnWorldSelectedObject != null) {
        btnWorldHideSelectedObject = new ToolStripMenuItem("Hide selected object") {
          ToolTipText = "Temporarily hide this static world placement. Useful with Y-slice for inspecting interiors without a roofs database."
        };
        btnWorldHideSelectedObject.Click += (_, __) => HideSelectedWorldObjectForCutaway();
        int insertIndex = Math.Max(0, btnWorldSelectedObject.DropDownItems.Count - 2);
        btnWorldSelectedObject.DropDownItems.Insert(insertIndex, btnWorldHideSelectedObject);
      }

      if (btnWorldToolsMenu != null) {
        btnWorldStreamingDebug = new ToolStripMenuItem("Streaming debug bounds") {
          CheckOnClick = true,
          Checked = worldSettings.ShowStreamingDebugBounds,
          ToolTipText = "Streaming diagnostics: green = active room, blue = floor/collision residency, orange = startup/prefetch; dim boxes use coarse bounds."
        };
        btnWorldStreamingDebug.CheckedChanged += (_, __) => {
          if (updatingWorldToolbar) return;
          worldSettings.ShowStreamingDebugBounds = btnWorldStreamingDebug.Checked;
          ApplyWorldSettings();
        };
        btnWorldToolsMenu.DropDownItems.Add(btnWorldStreamingDebug);

        btnWorldRestoreHiddenObjects = new ToolStripMenuItem("Restore hidden objects") { Enabled = false };
        btnWorldRestoreHiddenObjects.Click += (_, __) => RestoreHiddenWorldObjects();
        int selectedIndex = btnWorldSelectedObject == null ? -1 : btnWorldToolsMenu.DropDownItems.IndexOf(btnWorldSelectedObject);
        if (selectedIndex >= 0) btnWorldToolsMenu.DropDownItems.Insert(selectedIndex + 1, btnWorldRestoreHiddenObjects);
        else btnWorldToolsMenu.DropDownItems.Add(btnWorldRestoreHiddenObjects);
        btnWorldToolsMenu.DropDownOpening += (_, __) => UpdateWorldHiddenObjectsMenu();
      }

      LayoutWorldCutawayControl();
      UpdateWorldSliceCaption();
      UpdateWorldHiddenObjectsMenu();
    }

    private void LayoutWorldCutawayControl() {
      if (worldSlicePanel == null || splitContainer3?.Panel1 == null) return;
      int toolbarBottom = worldToolbar?.Visible == true ? worldToolbar.Bottom : 0;
      int availableHeight = Math.Max(1, splitContainer3.Panel1.ClientSize.Height - toolbarBottom);
      int height = Math.Min(310, Math.Max(190, availableHeight - 24));
      worldSlicePanel.Height = height;
      if (worldSliceTrack != null) worldSliceTrack.Height = Math.Max(110, height - 63);
      if (worldSliceValue != null) worldSliceValue.Top = height - 27;
      int x = Math.Max(4, splitContainer3.Panel1.ClientSize.Width - worldSlicePanel.Width - 12);
      int y = toolbarBottom + Math.Max(8, (availableHeight - height) / 2);
      worldSlicePanel.Location = new Point(x, y);
      if (worldSlicePanel.Visible) worldSlicePanel.BringToFront();
    }

    private void UpdateWorldSliceCaption() {
      if (worldSliceValue == null || worldSliceTrack == null) return;
      if (worldSliceTrack.Value >= 999 || btnWorldYSlice?.Checked != true) {
        worldSliceValue.Text = "Off";
        return;
      }
      float fraction = worldSliceTrack.Value / 1000f;
      float y = panelRender?.VerticalSliceHeightForFraction(fraction) ?? 0f;
      worldSliceValue.Text = String.Format(CultureInfo.InvariantCulture, "{0:0}%\nY {1:0.0}", fraction * 100f, y);
    }


    private void HandleWorldSliceMouseWheel(int delta) {
      if (worldSliceTrack == null || delta == 0) return;
      int steps = Math.Max(1, Math.Abs(delta) / 120);
      int change = steps * worldSliceTrack.LargeChange * Math.Sign(delta);
      worldSliceTrack.Value = Math.Max(worldSliceTrack.Minimum, Math.Min(worldSliceTrack.Maximum, worldSliceTrack.Value + change));
    }

    private bool CursorIsOverWorldSliceControl() {
      if (worldSlicePanel?.Visible != true) return false;
      try { return worldSlicePanel.ClientRectangle.Contains(worldSlicePanel.PointToClient(Cursor.Position)); }
      catch { return false; }
    }

    private void HideSelectedWorldObjectForCutaway() {
      if (panelRender == null) return;
      string summary = panelRender.HideSelectedWorldObject();
      if (String.IsNullOrWhiteSpace(summary)) {
        SetStatusLabel("Only selected static world geometry can be hidden.");
        return;
      }
      HideWorldSelectionInfo();
      UpdateWorldSelectedObjectMenu();
      UpdateWorldHiddenObjectsMenu();
      SetStatusLabel("Hidden for cutaway: " + summary);
    }

    private void RestoreHiddenWorldObjects() {
      int count = panelRender?.RestoreHiddenWorldObjects() ?? 0;
      UpdateWorldHiddenObjectsMenu();
      SetStatusLabel(count > 0 ? "Restored " + count.ToString(CultureInfo.InvariantCulture) + " hidden world object(s)." : "No hidden world objects to restore.");
    }

    private void UpdateWorldHiddenObjectsMenu() {
      if (btnWorldRestoreHiddenObjects == null) return;
      int count = panelRender?.HiddenWorldObjectCount ?? 0;
      btnWorldRestoreHiddenObjects.Enabled = count > 0;
      btnWorldRestoreHiddenObjects.Text = count > 0
        ? "Restore hidden objects (" + count.ToString(CultureInfo.InvariantCulture) + ")"
        : "Restore hidden objects";
      if (btnWorldHideSelectedObject != null) btnWorldHideSelectedObject.Enabled = panelRender?.CanHideSelectedWorldObject == true;
    }
  }
}
