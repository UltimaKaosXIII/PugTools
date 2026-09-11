using System;
using System.Linq;
using System.Windows.Forms;

namespace PugTools {
  /// <summary>
  /// Cross-browser links used by the database-free reader workflow. Existing windows are reused
  /// when they point at the same client so following a reference does not create a window storm.
  /// </summary>
  internal static class BrowserNavigation {
    internal static void OpenAsset(Form owner, String gamePath, Boolean usePts, String resourcePath, Boolean newTab = false) {
      if (String.IsNullOrWhiteSpace(resourcePath)) return;
      AssetBrowser browser = Application.OpenForms.OfType<AssetBrowser>()
        .FirstOrDefault(x => x != null && !x.IsDisposed && x.MatchesAssetSource(gamePath, usePts));
      if (browser == null) {
        browser = new AssetBrowser(gamePath, usePts);
        browser.Show();
      } else {
        if (!browser.Visible) browser.Show();
        browser.Activate();
      }
      browser.NavigateOrQueueResource(resourcePath, newTab);
      browser.Focus();
    }

    internal static void OpenNode(Form owner, String gamePath, Boolean usePts, String nodeReference, Boolean newTab = false) {
      if (String.IsNullOrWhiteSpace(nodeReference)) return;
      NodeBrowser browser = Application.OpenForms.OfType<NodeBrowser>()
        .FirstOrDefault(x => x != null && !x.IsDisposed && x.MatchesNodeSource(gamePath, usePts));
      if (browser == null) {
        browser = new NodeBrowser(gamePath, usePts, Config.ExtractAssetsPath ?? String.Empty);
        browser.Show();
      } else {
        if (!browser.Visible) browser.Show();
        browser.Activate();
      }
      browser.NavigateOrQueueNode(nodeReference, newTab);
      browser.Focus();
    }

    internal static void OpenDom(Form owner, String gamePath, Boolean usePts, UInt64 domTypeId) {
      if (domTypeId == 0) return;
      DomBrowser browser = Application.OpenForms.OfType<DomBrowser>()
        .FirstOrDefault(x => x != null && !x.IsDisposed && x.MatchesDomSource(gamePath, usePts));
      if (browser == null) {
        browser = new DomBrowser(gamePath, usePts);
        browser.Show();
      } else {
        if (!browser.Visible) browser.Show();
        browser.Activate();
      }
      browser.NavigateOrQueueDom(domTypeId);
      browser.Focus();
    }

    internal static Boolean TryExtractResourcePath(String raw, out String path) {
      path = null;
      if (String.IsNullOrWhiteSpace(raw)) return false;
      String value = raw.Trim().Trim('"', '\'', '(', ')', '[', ']', '{', '}').TrimEnd(',', ';', ':');
      Int32 index = value.IndexOf("/resources/", StringComparison.OrdinalIgnoreCase);
      if (index >= 0) value = value.Substring(index);
      else if (value.StartsWith("resources/", StringComparison.OrdinalIgnoreCase)) value = "/" + value;
      else if (value.StartsWith("resources\\", StringComparison.OrdinalIgnoreCase)) value = "/" + value.Replace('\\', '/');
      else return false;
      value = value.Replace('\\', '/');
      if (!value.StartsWith("/resources/", StringComparison.OrdinalIgnoreCase)) return false;
      path = value;
      return true;
    }
  }
}
