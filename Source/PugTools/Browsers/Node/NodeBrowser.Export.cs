using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using GomLib;
using Newtonsoft.Json;

namespace PugTools {
  internal partial class NodeBrowser {
    private ToolStripMenuItem _nodeExportJsonMenuItem;
    private ToolStripMenuItem _nodeExportBranchJsonMenuItem;

    private void InitializeNodeExportMenu() {
      if (contextMenuStrip1 == null) return;

      contextMenuStrip1.Items.Add(new ToolStripSeparator());
      _nodeExportJsonMenuItem = new ToolStripMenuItem("Export node as JSON...");
      _nodeExportJsonMenuItem.Click += NodeExportJsonMenuItemClick;
      contextMenuStrip1.Items.Add(_nodeExportJsonMenuItem);

      _nodeExportBranchJsonMenuItem = new ToolStripMenuItem("Export branch as JSON ZIP...");
      _nodeExportBranchJsonMenuItem.Click += NodeExportBranchJsonMenuItemClick;
      contextMenuStrip1.Items.Add(_nodeExportBranchJsonMenuItem);

      contextMenuStrip1.Opening += delegate {
        TreeNode selected = ActiveNodeTree?.SelectedNode;
        NodeAsset asset = selected?.Tag as NodeAsset;
        if (_nodeExportJsonMenuItem != null)
          _nodeExportJsonMenuItem.Enabled = asset?.Obj != null;
        if (_nodeExportBranchJsonMenuItem != null)
          _nodeExportBranchJsonMenuItem.Enabled = asset != null;
      };
    }

    private async void NodeExportJsonMenuItemClick(Object sender, EventArgs e) {
      NodeAsset asset = ActiveNodeTree?.SelectedNode?.Tag as NodeAsset;
      GomObject obj = asset?.Obj;
      if (obj == null) return;

      using SaveFileDialog dialog = new SaveFileDialog {
        AddExtension = true,
        DefaultExt = "json",
        FileName = SafeFileName(obj.Name) + ".json",
        Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
        InitialDirectory = ExistingDirectoryOrNull(txtExtractPath?.Text)
      };
      if (dialog.ShowDialog(this) != DialogResult.OK) return;

      try {
        StatusLabel1Text("Exporting node JSON ...");
        String json = await Task.Run(() => SerializeNodeJson(obj));
        await File.WriteAllTextAsync(dialog.FileName, json, new UTF8Encoding(false));
        StatusLabel1Text("Exported " + obj.Name + " as JSON.");
      }
      catch (Exception ex) {
        MessageBox.Show(this, ex.Message, "JSON Export", MessageBoxButtons.OK, MessageBoxIcon.Error);
        StatusLabel1Text("JSON export failed.");
      }
    }

    private async void NodeExportBranchJsonMenuItemClick(Object sender, EventArgs e) {
      NodeAsset root = ActiveNodeTree?.SelectedNode?.Tag as NodeAsset;
      if (root == null || _assetDict == null) return;

      String defaultName = SafeFileName(root.Obj?.Name ?? root.displayName ?? root.id ?? "nodes") + "_nodes.zip";
      using SaveFileDialog dialog = new SaveFileDialog {
        AddExtension = true,
        DefaultExt = "zip",
        FileName = defaultName,
        Filter = "ZIP archives (*.zip)|*.zip|All files (*.*)|*.*",
        InitialDirectory = ExistingDirectoryOrNull(txtExtractPath?.Text)
      };
      if (dialog.ShowDialog(this) != DialogResult.OK) return;

      try {
        StatusLabel1Text("Exporting node branch as JSON ZIP ...");
        List<GomObject> objects = await Task.Run(() => EnumerateBranchObjects(root).ToList());
        if (objects.Count == 0) {
          MessageBox.Show(this, "The selected branch contains no prototype nodes.", "JSON Export",
                          MessageBoxButtons.OK, MessageBoxIcon.Information);
          StatusLabel1Text("Nothing to export.");
          return;
        }

        await Task.Run(() => WriteBranchJsonZip(dialog.FileName, root, objects));
        StatusLabel1Text("Exported " + objects.Count.ToString("N0", CultureInfo.InvariantCulture) + " nodes as JSON ZIP.");
      }
      catch (Exception ex) {
        MessageBox.Show(this, ex.Message, "JSON ZIP Export", MessageBoxButtons.OK, MessageBoxIcon.Error);
        StatusLabel1Text("JSON ZIP export failed.");
      }
    }

    private IEnumerable<GomObject> EnumerateBranchObjects(NodeAsset root) {
      if (root == null || _assetDict == null) yield break;

      Dictionary<String, List<NodeAsset>> byParent = new Dictionary<String, List<NodeAsset>>(StringComparer.OrdinalIgnoreCase);
      foreach (NodeAsset item in _assetDict.Values) {
        if (item == null || String.IsNullOrWhiteSpace(item.parentId)) continue;
        if (!byParent.TryGetValue(item.parentId, out List<NodeAsset> list)) {
          list = new List<NodeAsset>();
          byParent[item.parentId] = list;
        }
        list.Add(item);
      }

      Queue<NodeAsset> queue = new Queue<NodeAsset>();
      HashSet<String> seen = new HashSet<String>(StringComparer.OrdinalIgnoreCase);
      queue.Enqueue(root);
      while (queue.Count > 0) {
        if (_closing) yield break;
        NodeAsset item = queue.Dequeue();
        String key = item?.id ?? item?.displayName;
        if (item == null || String.IsNullOrWhiteSpace(key) || !seen.Add(key)) continue;
        if (item.Obj != null) yield return item.Obj;
        if (byParent.TryGetValue(item.id, out List<NodeAsset> children)) {
          foreach (NodeAsset child in children.OrderBy(x => x.id, StringComparer.OrdinalIgnoreCase))
            queue.Enqueue(child);
        }
      }
    }

    private void WriteBranchJsonZip(String fileName, NodeAsset root, IList<GomObject> objects) {
      String temp = fileName + ".tmp";
      try {
        if (File.Exists(temp)) File.Delete(temp);
        using (FileStream fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (ZipArchive zip = new ZipArchive(fs, ZipArchiveMode.Create, false, Encoding.UTF8)) {
          foreach (GomObject obj in objects) {
            if (_closing) throw new OperationCanceledException("Node Browser is closing.");
            String entryName = JsonZipEntryName(obj.Name, obj.Id);
            ZipArchiveEntry entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
            using Stream stream = entry.Open();
            using StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false), 65536, false);
            writer.Write(SerializeNodeJson(obj));
          }

          ZipArchiveEntry manifest = zip.CreateEntry("_manifest.json", CompressionLevel.Optimal);
          using Stream manifestStream = manifest.Open();
          using StreamWriter manifestWriter = new StreamWriter(manifestStream, new UTF8Encoding(false), 4096, false);
          Object manifestObject = new {
            format = "PugTools GOM JSON branch",
            exportedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            root = root.Obj?.Name ?? root.id,
            count = objects.Count
          };
          manifestWriter.Write(JsonConvert.SerializeObject(manifestObject, Formatting.Indented));
        }

        if (File.Exists(fileName)) File.Delete(fileName);
        File.Move(temp, fileName);
      }
      finally {
        try { if (File.Exists(temp)) File.Delete(temp); } catch { }
      }
    }

    private static String SerializeNodeJson(GomObject obj) {
      if (obj == null) return "{}";

      // Loading Data also resolves the node's GLOM classes, which makes the export useful for
      // inspecting prototype composition without a separate database/index.
      GomObjectData data = null;
      try { data = obj.Data; } catch { }

      Object payload = new {
        name = obj.Name,
        id = obj.Id,
        idHex = "0x" + obj.Id.ToString("X16", CultureInfo.InvariantCulture),
        description = obj.Description,
        baseClass = obj.DomClass == null ? null : new {
          name = obj.DomClass.Name,
          id = obj.DomClass.Id,
          idHex = "0x" + obj.DomClass.Id.ToString("X16", CultureInfo.InvariantCulture)
        },
        glommedClasses = (obj.GlommedClasses ?? new List<DomClass>())
          .Where(x => x != null)
          .Select(x => new {
            name = x.Name,
            id = x.Id,
            idHex = "0x" + x.Id.ToString("X16", CultureInfo.InvariantCulture)
          }).ToArray(),
        data = NormalizeJsonValue(data, 0)
      };
      return JsonConvert.SerializeObject(payload, Formatting.Indented);
    }

    private static Object NormalizeJsonValue(Object value, Int32 depth) {
      if (value == null) return null;
      if (depth > 48) return "<maximum nesting depth reached>";

      if (value is String || value is Boolean || value is Byte || value is SByte
          || value is Int16 || value is UInt16 || value is Int32 || value is UInt32
          || value is Int64 || value is UInt64 || value is Single || value is Double
          || value is Decimal) return value;

      if (value is Enum) return value.ToString();
      if (value is Byte[] bytes) return new {
        binaryBase64 = Convert.ToBase64String(bytes),
        length = bytes.Length
      };

      if (value is GomObject gom) return new {
        reference = gom.Name,
        id = gom.Id,
        idHex = "0x" + gom.Id.ToString("X16", CultureInfo.InvariantCulture)
      };
      if (value is DomType domType) return new {
        type = domType.GetType().Name,
        name = domType.Name,
        id = domType.Id,
        idHex = "0x" + domType.Id.ToString("X16", CultureInfo.InvariantCulture)
      };

      if (value is GomObjectData objectData) {
        Dictionary<String, Object> result = new Dictionary<String, Object>(StringComparer.Ordinal);
        if (objectData.Dictionary != null) {
          foreach (KeyValuePair<String, Object> pair in objectData.Dictionary)
            result[pair.Key] = NormalizeJsonValue(pair.Value, depth + 1);
        }
        return result;
      }

      if (value is IDictionary dictionary) {
        List<Object> entries = new List<Object>();
        foreach (DictionaryEntry pair in dictionary) {
          entries.Add(new {
            key = NormalizeJsonValue(pair.Key, depth + 1),
            value = NormalizeJsonValue(pair.Value, depth + 1)
          });
        }
        return entries;
      }

      if (value is IEnumerable enumerable && value is not String) {
        List<Object> list = new List<Object>();
        foreach (Object item in enumerable) list.Add(NormalizeJsonValue(item, depth + 1));
        return list;
      }

      return Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    private static String JsonZipEntryName(String nodeName, UInt64 id) {
      String[] pieces = (nodeName ?? id.ToString(CultureInfo.InvariantCulture))
        .Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries);
      if (pieces.Length == 0) return id.ToString(CultureInfo.InvariantCulture) + ".json";
      for (Int32 i = 0; i < pieces.Length; i++) pieces[i] = SafeFileName(pieces[i]);
      return String.Join("/", pieces) + ".json";
    }

    private static String SafeFileName(String value) {
      String text = String.IsNullOrWhiteSpace(value) ? "node" : value.Trim();
      foreach (Char invalid in Path.GetInvalidFileNameChars()) text = text.Replace(invalid, '_');
      return text;
    }

    private static String ExistingDirectoryOrNull(String path) {
      try {
        if (!String.IsNullOrWhiteSpace(path) && Directory.Exists(path)) return path;
      } catch { }
      return String.Empty;
    }
  }
}
