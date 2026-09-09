using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

using TorFile = TorArchive.File;

namespace PugTools {
  public partial class WorldBrowser {
    private readonly Dictionary<String, UInt64> gameplayMapNoteAreaCache = new Dictionary<String, UInt64>(StringComparer.OrdinalIgnoreCase);
    private Int32 gameplayMapNoteNavigationGeneration;

    internal Boolean MatchesGameplayAssetSource(String assetLocation, Boolean usePts) {
      String left = TorArchive.Assets.NormalizeGamePath(worldAssetLocation ?? String.Empty).TrimEnd('\\', '/');
      String right = TorArchive.Assets.NormalizeGamePath(assetLocation ?? String.Empty).TrimEnd('\\', '/');
      return worldUsePts == usePts && String.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Database-free bridge used by the Node Gameplay Explorer. The authored placement of an mpn.* template
    /// lives in an area's mapnotes.not, not on the template node itself, so locate it lazily in the installed
    /// client and then use the normal World Browser area loader/teleporter.
    /// </summary>
    internal async void NavigateToGameplayMapNote(String fqn) {
      String normalized = NormalizeGameplayMapNoteFqn(fqn);
      if (String.IsNullOrWhiteSpace(normalized) || _closing) return;
      Int32 generation = ++gameplayMapNoteNavigationGeneration;
      panelRender?.ClearGameplayMapNoteHighlight();
      SetStatusLabel("Finding map note in installed world data: " + normalized + " …");

      // A World Browser may have just been opened by the Node Browser. Do not block its UI while its existing
      // background worker discovers the installed areas.
      for (Int32 i = 0; i < 300 && loadedAssetDict == null && !_closing && generation == gameplayMapNoteNavigationGeneration; i++)
        await Task.Delay(100);
      if (_closing || generation != gameplayMapNoteNavigationGeneration) return;
      if (loadedAssetDict == null) {
        SetStatusLabel("World list is not available; map-note navigation could not start.");
        return;
      }

      FileFormats.AreaMapNote current = FindGameplayMapNoteInLoadedArea(normalized);
      if (current != null) {
        TeleportToGameplayMapNote(current, normalized);
        return;
      }

      UInt64 areaId;
      if (!gameplayMapNoteAreaCache.TryGetValue(normalized, out areaId)) {
        areaId = await Task.Run(() => FindGameplayMapNoteArea(normalized, generation));
        if (generation != gameplayMapNoteNavigationGeneration || _closing) return;
        if (areaId != 0) gameplayMapNoteAreaCache[normalized] = areaId;
      }

      if (areaId == 0) {
        SetStatusLabel("Map note was not found in installed mapnotes.not files: " + normalized);
        return;
      }

      if (currentAreaId != areaId || area == null) {
        TreeNode areaNode = FindGameplayWorldAreaTreeNode(treeViewFast1?.Nodes, areaId);
        if (areaNode == null) {
          SetStatusLabel("Found map note in area " + areaId + ", but that area is not available in the World tree.");
          return;
        }
        treeViewFast1.SelectedNode = areaNode;
        areaNode.EnsureVisible();
      }

      // TreeViewFast1_AfterSelect already performs the expensive normal area load. Wait for that path rather than
      // constructing a second Area instance or competing for the renderer.
      for (Int32 i = 0; i < 900 && !_closing && generation == gameplayMapNoteNavigationGeneration; i++) {
        if (currentAreaId == areaId && area != null) {
          FileFormats.AreaMapNote note = FindGameplayMapNoteInLoadedArea(normalized);
          if (note != null) {
            TeleportToGameplayMapNote(note, normalized);
            return;
          }
        }
        await Task.Delay(100);
      }
      if (!_closing && generation == gameplayMapNoteNavigationGeneration)
        SetStatusLabel("Area loaded, but the requested map-note placement was not parsed: " + normalized);
    }

    private void TeleportToGameplayMapNote(FileFormats.AreaMapNote note, String normalizedFqn) {
      if (note == null || panelRender == null) return;
      panelRender.TeleportToMapNote(note);
      panelRender.SetGameplayMapNoteHighlight(note);
      ActivateWorldRenderInput();
      SetStatusLabel("Teleported to gameplay map note (highlighted): " + (note.Label ?? note.Fqn ?? normalizedFqn));
    }

    private FileFormats.AreaMapNote FindGameplayMapNoteInLoadedArea(String normalizedFqn) {
      if (area?.MapNotes == null || String.IsNullOrWhiteSpace(normalizedFqn)) return null;
      return area.MapNotes.FirstOrDefault(note => note != null && String.Equals(NormalizeGameplayMapNoteFqn(note.Fqn), normalizedFqn, StringComparison.OrdinalIgnoreCase));
    }

    private static TreeNode FindGameplayWorldAreaTreeNode(TreeNodeCollection nodes, UInt64 areaId) {
      if (nodes == null || areaId == 0) return null;
      foreach (TreeNode node in nodes) {
        if (node?.Tag is NodeAsset tag && tag.dynObject is TorArchive.HashFileInfo &&
          UInt64.TryParse((tag.id ?? String.Empty).Trim('/'), out UInt64 id) && id == areaId) return node;
        TreeNode child = FindGameplayWorldAreaTreeNode(node?.Nodes, areaId);
        if (child != null) return child;
      }
      return null;
    }

    private UInt64 FindGameplayMapNoteArea(String normalizedFqn, Int32 generation) {
      if (currentAssets == null || loadedAssetDict == null || String.IsNullOrWhiteSpace(normalizedFqn)) return 0;
      List<UInt64> areaIds = loadedAssetDict.Keys
        .Select(x => UInt64.TryParse((x ?? String.Empty).Trim('/'), out UInt64 id) ? id : 0)
        .Where(x => x != 0)
        .Distinct()
        .ToList();
      if (currentAreaId != 0 && areaIds.Remove(currentAreaId)) areaIds.Insert(0, currentAreaId);

      String slashFqn = "\\server\\mpn\\" + normalizedFqn.Replace('.', '\\') + ".mpn";
      foreach (UInt64 id in areaIds) {
        if (_closing || generation != gameplayMapNoteNavigationGeneration) return 0;
        String[] paths = {
          "/resources/world/areas/" + id + "/mapnotes.not",
          "/resources/world/livecontent/systemgenerated/" + id + "/mapnotes.not"
        };
        foreach (String path in paths) {
          try {
            using TorFile file = currentAssets.FindFile(path);
            if (file == null) continue;
            String text = ReadGameplayMapNoteSearchText(file);
            if (String.IsNullOrWhiteSpace(text)) continue;
            if (text.IndexOf(slashFqn, StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("mpn." + normalizedFqn, StringComparison.OrdinalIgnoreCase) >= 0)
              return id;
          } catch { }
        }
      }
      return 0;
    }

    private static String ReadGameplayMapNoteSearchText(TorFile file) {
      if (file == null) return String.Empty;
      using Stream stream = file.OpenCopyInMemory();
      using MemoryStream memory = new MemoryStream();
      stream.CopyTo(memory);
      Byte[] bytes = memory.ToArray();
      if (bytes.Length == 0) return String.Empty;
      if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
      if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
      if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
      Int32 probe = Math.Min(bytes.Length, 192), evenZero = 0, oddZero = 0;
      for (Int32 i = 0; i < probe; i++) if (bytes[i] == 0) { if ((i & 1) == 0) evenZero++; else oddZero++; }
      if (oddZero > probe / 8 && oddZero > evenZero * 2) return Encoding.Unicode.GetString(bytes);
      if (evenZero > probe / 8 && evenZero > oddZero * 2) return Encoding.BigEndianUnicode.GetString(bytes);
      return Encoding.UTF8.GetString(bytes);
    }

    private static String NormalizeGameplayMapNoteFqn(String fqn) {
      String value = (fqn ?? String.Empty).Trim().Replace('/', '.').Replace('\\', '.').Trim('.');
      if (value.StartsWith("server.mpn.", StringComparison.OrdinalIgnoreCase)) value = value.Substring("server.mpn.".Length);
      if (value.StartsWith("mpn.", StringComparison.OrdinalIgnoreCase)) value = value.Substring(4);
      if (value.EndsWith(".mpn", StringComparison.OrdinalIgnoreCase)) value = value.Substring(0, value.Length - 4);
      return value.Trim('.');
    }

  }
}
