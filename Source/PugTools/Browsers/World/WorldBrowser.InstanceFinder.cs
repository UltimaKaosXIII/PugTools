using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using FileFormats;
using SlimDX;

namespace PugTools {
  public partial class WorldBrowser {
    private sealed class WorldInstanceSearchResult {
      internal Room Room;
      internal AssetInstance Instance;
      internal AreaAsset Asset;
      internal Vector3 Position;
      internal String AssetPath;
    }

    private ToolStripMenuItem btnWorldFindInstance;

    private void InitializeWorldInstanceFinder() {
      if (btnWorldToolsMenu == null || btnWorldFindInstance != null) return;
      btnWorldFindInstance = new ToolStripMenuItem("Find instance…") {
        ToolTipText = "Search the loaded area by asset path/name, instance ID or asset ID, then teleport to the placement."
      };
      btnWorldFindInstance.Click += (_, __) => ShowWorldInstanceFinder();
      btnWorldToolsMenu.DropDownItems.Insert(0, btnWorldFindInstance);
      btnWorldToolsMenu.DropDownItems.Insert(1, new ToolStripSeparator());
    }

    private void ShowWorldInstanceFinder() {
      if (area?.RoomList == null || area.RoomList.Count == 0 || panelRender == null) {
        MessageBox.Show(this, "Load an area first.", "Find world instance", MessageBoxButtons.OK, MessageBoxIcon.Information);
        return;
      }

      using Form dialog = new Form {
        Text = "Find world instance — " + currentAreaId,
        StartPosition = FormStartPosition.CenterParent,
        Width = 1120,
        Height = 680,
        MinimizeBox = false
      };
      Label hint = new Label {
        Dock = DockStyle.Top,
        Height = 24,
        Padding = new Padding(8, 5, 8, 0),
        Text = "Asset path/name, decimal/hex instance ID, or asset ID. Double-click a result to teleport."
      };
      TextBox query = new TextBox { Dock = DockStyle.Top };
      ListView results = new ListView {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        GridLines = true,
        HideSelection = false
      };
      results.Columns.Add("Room", 180);
      results.Columns.Add("Instance ID", 155);
      results.Columns.Add("Asset ID", 155);
      results.Columns.Add("Asset", 450);
      results.Columns.Add("Position", 170);
      Label status = new Label { Dock = DockStyle.Bottom, Height = 24, Padding = new Padding(8, 5, 8, 0) };
      dialog.Controls.Add(results);
      dialog.Controls.Add(status);
      dialog.Controls.Add(query);
      dialog.Controls.Add(hint);

      const Int32 displayLimit = 2000;
      void Rebuild() {
        String needle = (query.Text ?? String.Empty).Trim();
        List<WorldInstanceSearchResult> found = SearchWorldInstances(needle, displayLimit, out Int32 total);
        results.BeginUpdate();
        try {
          results.Items.Clear();
          foreach (WorldInstanceSearchResult match in found) {
            var row = new ListViewItem(match.Room?.RoomName ?? String.Empty) { Tag = match };
            row.SubItems.Add(match.Instance.ID.ToString(CultureInfo.InvariantCulture) + "  [0x" + match.Instance.ID.ToString("X") + "]");
            row.SubItems.Add(match.Instance.assetID.ToString(CultureInfo.InvariantCulture) + "  [0x" + match.Instance.assetID.ToString("X") + "]");
            row.SubItems.Add(match.AssetPath);
            row.SubItems.Add(String.Format(CultureInfo.InvariantCulture, "{0:0.##}, {1:0.##}, {2:0.##}", match.Position.X * 10f, match.Position.Z * 10f, match.Position.Y * 10f));
            results.Items.Add(row);
          }
        } finally { results.EndUpdate(); }
        status.Text = total > displayLimit
          ? total.ToString("N0") + " matches; showing first " + displayLimit.ToString("N0") + "."
          : total.ToString("N0") + " matches.";
      }

      void TeleportSelected() {
        if (results.SelectedItems.Count == 0 || results.SelectedItems[0].Tag is not WorldInstanceSearchResult selected) return;
        panelRender.TeleportToDisplayCoordinates(selected.Position.X * 10f, selected.Position.Z * 10f, selected.Position.Y * 10f);
        SetStatusLabel("Teleported to instance " + selected.Instance.ID + " — " + selected.AssetPath + " — room " + (selected.Room?.RoomName ?? "unknown"));
        ActivateWorldRenderInput();
      }

      query.TextChanged += (_, __) => Rebuild();
      query.KeyDown += (_, e) => {
        if (e.KeyCode == Keys.Enter) { e.Handled = true; e.SuppressKeyPress = true; TeleportSelected(); }
      };
      results.DoubleClick += (_, __) => TeleportSelected();
      results.KeyDown += (_, e) => {
        if (e.KeyCode == Keys.Enter) { e.Handled = true; e.SuppressKeyPress = true; TeleportSelected(); }
      };
      dialog.Shown += (_, __) => { Rebuild(); query.Focus(); };
      dialog.ShowDialog(this);
    }

    private List<WorldInstanceSearchResult> SearchWorldInstances(String query, Int32 displayLimit, out Int32 total) {
      total = 0;
      var shown = new List<WorldInstanceSearchResult>();
      String needle = (query ?? String.Empty).Trim();
      Boolean hasNumeric = TryParseWorldInstanceId(needle, out UInt64 numeric);

      foreach (Room room in (IEnumerable<Room>)(area?.RoomList) ?? Enumerable.Empty<Room>()) {
        if (room?.InstancesById == null) continue;
        foreach (AssetInstance instance in room.InstancesById.Values) {
          if (instance == null) continue;
          AreaAsset asset = null;
          if (area?.AssetIdMap != null) area.AssetIdMap.TryGetValue(instance.assetID, out asset);
          String path = WorldInstanceAssetPath(asset, instance.assetID);
          Boolean match = needle.Length == 0;
          if (!match && hasNumeric) match = instance.ID == numeric || instance.assetID == numeric;
          if (!match) {
            match = path.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0
                 || (room.RoomName ?? String.Empty).IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0
                 || instance.ID.ToString(CultureInfo.InvariantCulture).IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0
                 || instance.ID.ToString("X").IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0
                 || instance.assetID.ToString(CultureInfo.InvariantCulture).IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0
                 || instance.assetID.ToString("X").IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
          }
          if (!match) continue;

          total++;
          if (shown.Count >= displayLimit) continue;
          Vector3 position;
          try {
            var world = instance.GetAbsoluteTransform(room);
            position = new Vector3(world.M41, world.M42, world.M43);
          } catch { position = instance.position; }
          shown.Add(new WorldInstanceSearchResult { Room = room, Instance = instance, Asset = asset, Position = position, AssetPath = path });
        }
      }
      return shown;
    }

    private static String WorldInstanceAssetPath(AreaAsset asset, UInt64 assetId) {
      if (asset == null) return "[asset " + assetId.ToString(CultureInfo.InvariantCulture) + "]";
      String path = (asset.Path ?? String.Empty).Trim().Replace('\\', '/');
      String extension = (asset.Extension ?? String.Empty).Trim().TrimStart('.');
      if (extension.Length > 0 && !path.EndsWith("." + extension, StringComparison.OrdinalIgnoreCase)) path += "." + extension;
      return path.Length > 0 ? path : "[asset " + assetId.ToString(CultureInfo.InvariantCulture) + "]";
    }

    private static Boolean TryParseWorldInstanceId(String text, out UInt64 value) {
      value = 0;
      if (String.IsNullOrWhiteSpace(text)) return false;
      String input = text.Trim();
      if (input.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        return UInt64.TryParse(input.Substring(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value);
      if (UInt64.TryParse(input, NumberStyles.Integer, CultureInfo.InvariantCulture, out value)) return true;
      if (input.All(Uri.IsHexDigit) && input.Any(Char.IsLetter))
        return UInt64.TryParse(input, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value);
      return false;
    }
  }
}
