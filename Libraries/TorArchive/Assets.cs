using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace TorArchive {
  public class Assets : IDisposable {

    #region Constructors
    public Assets(String gamePath) {
      m_gamePath = NormalizeGamePath(gamePath);
      m_assetPath = GetAssetsDirectory(m_gamePath);
      Icons = new Icons(this);
      LoadedFileGroups = new List<String>();
    }

    #endregion Constructors

    #region Fields
    private readonly String m_gamePath;
    private readonly String m_assetPath;
    private readonly HashSet<String> m_loadedArchivePaths = new HashSet<String>(StringComparer.OrdinalIgnoreCase);

    #endregion Fields

    #region IDisposable
    private Boolean m_disposed = false;

    ~Assets() {
      Dispose(false);
    }

    public void Dispose() {
      Dispose(true);
      GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(Boolean disposing) {
      if (m_disposed) {
        return;
      }

      if (disposing) {
        foreach (Library lib in Libraries) {
          lib.Dispose();
        }

        Libraries.Clear();
      }

      m_disposed = true;
    }

    #endregion IDisposable

    #region Methods
    /// <summary>
    /// Normalizes the configured SWTOR location to the installation root. Older PugTools
    /// configurations pointed directly at the Assets folder, so keep accepting that layout
    /// and transparently migrate it to the parent game directory.
    /// </summary>
    public static String NormalizeGamePath(String path) {
      if (String.IsNullOrWhiteSpace(path)) return String.Empty;

      String normalized = path.Trim().Trim('"');
      normalized = normalized.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
      if (normalized.Length == 0) return String.Empty;

      try {
        DirectoryInfo directory = new DirectoryInfo(normalized);

        if (directory.Name.Equals("Assets", StringComparison.OrdinalIgnoreCase)
            && directory.Parent != null) {
          return directory.Parent.FullName;
        }

        // Also accept either retailclient directory in case somebody pastes that path manually.
        if (directory.Name.Equals("retailclient", StringComparison.OrdinalIgnoreCase)
            && directory.Parent != null
            && (directory.Parent.Name.Equals("swtor", StringComparison.OrdinalIgnoreCase)
                || directory.Parent.Name.Equals("publictest", StringComparison.OrdinalIgnoreCase))
            && directory.Parent.Parent != null) {
          return directory.Parent.Parent.FullName;
        }

        return directory.FullName;
      } catch (Exception ex) when (ex is ArgumentException
                                   || ex is NotSupportedException
                                   || ex is PathTooLongException) {
        // Text boxes validate while the user is still typing. Keep incomplete/invalid input
        // non-fatal and let the normal path validation show the red shield instead.
        return normalized;
      }
    }

    public static String GetAssetsDirectory(String gamePath) {
      String normalized = NormalizeGamePath(gamePath);
      if (String.IsNullOrEmpty(normalized)) return String.Empty;

      String assetsDirectory = Path.Combine(normalized, "Assets");
      if (Directory.Exists(assetsDirectory)) return assetsDirectory;

      // Backwards compatibility for callers that still pass a directory containing TORs
      // directly instead of the installation root.
      if (Directory.Exists(normalized)
          && Directory.EnumerateFiles(normalized, "*.tor", SearchOption.TopDirectoryOnly).Any()) {
        return normalized;
      }

      return assetsDirectory;
    }

    public static String GetRetailClientDirectory(String gamePath, Boolean isPtr) {
      String normalized = NormalizeGamePath(gamePath);
      if (String.IsNullOrEmpty(normalized)) return String.Empty;

      return Path.Combine(normalized, isPtr ? "publictest" : "swtor", "retailclient");
    }

    public File FindFile(String path) {
      if (path == null) {
        return null;
      }

      path = path.Replace('\\', '/');
      File result = null;

      foreach (Library lib in Libraries) {
        result = lib.FindFile(path);

        if (result != null) {
          return result;
        }
      }

      return result;
    }

    public Boolean HasFile(String path) {
      return FindFile(path) != null;
    }

    public void Load(Boolean isPtr) {
      // Preserve the historical LIVE -> PTS auto-fallback even when neutral/common beta archives are
      // present in the same Assets directory. Common archives alone must not hide an explicit PTS branch.
      if (!isPtr && Directory.Exists(m_assetPath)) {
        String[] names = Directory.EnumerateFiles(m_assetPath, "*", SearchOption.TopDirectoryOnly)
          .Where(path => Path.GetExtension(path).Equals(".tor", StringComparison.OrdinalIgnoreCase))
          .Select(Path.GetFileName)
          .ToArray();
        Boolean hasPts = names.Any(name => name.StartsWith("swtor_test_", StringComparison.OrdinalIgnoreCase));
        Boolean hasExplicitLive = names.Any(name => name.StartsWith("swtor_", StringComparison.OrdinalIgnoreCase)
          && !name.StartsWith("swtor_test_", StringComparison.OrdinalIgnoreCase));
        if (!hasExplicitLive && hasPts) { Load(true); return; }
      }

      Libraries = new List<Library>();
      LoadedFileGroups.Clear();
      m_loadedArchivePaths.Clear();

      LoadAssetFiles("main", isPtr);
      LoadAssetFiles("en-us", isPtr);
      LoadAssetFiles("fr-fr", isPtr);
      LoadAssetFiles("de-de", isPtr);

      // Beta
      LoadAssetFiles("locale_en_us", isPtr);
      LoadAssetFiles("system", isPtr);

      // Do not assume that every valid archive belongs to one of the historical main/locale/system
      // families. SWTOR has shipped additional split libraries over the years and beta/dev builds
      // use several different prefixes. Load every remaining TOR as a direct archive library so the
      // Asset Browser can display it even when its naming convention is unknown to PugTools.
      LoadUnclaimedAssetFiles(isPtr);

      // Preserve the old LIVE -> PTS fallback, but base it only on the actual Assets folder.
      // A retailclient TOR by itself must not make an incomplete installation look valid.
      if (Libraries.Count == 0) {
        if (isPtr == false) {
          Load(true);
          return;
        } else {
          throw new Exception("Could not find asset files!");
        }
      }

      // UI/client resources live outside Assets. Load every TOR from the matching client
      // directory as an additional library so FindFile/Asset Browser can resolve them too.
      LoadRetailClientFiles(isPtr);
    }

    private void LoadAssetFiles(String fileGroup, Boolean isPTS) {
      if (!Directory.Exists(m_assetPath)) return;

      // Discover logical libraries from the TORs that are physically present. Modern
      // clients split e.g. "main" into main_global/main_art/main_area/... while old
      // 32-bit/beta clients commonly use just main_N. Group by the stem before the
      // final numeric archive suffix so both layouts work, archive-number gaps are OK,
      // and metadata.bin is not required merely to browse/extract an archive.
      String[] prefixes = isPTS
        ? new[] { "swtor_test_" }
        : new[] { "swtor_", "he32_", "red_", "assets_", "green_", "" };

      foreach (String prefix in prefixes) {
        String familyStart = prefix + fileGroup;
        var groups = Directory.EnumerateFiles(m_assetPath, familyStart + "*.tor", SearchOption.TopDirectoryOnly)
          .Select(path => ParseArchivePath(path, prefix))
          .Where(item => item != null
                      && (item.LogicalName.Equals(fileGroup, StringComparison.OrdinalIgnoreCase)
                          || item.LogicalName.StartsWith(fileGroup + "_", StringComparison.OrdinalIgnoreCase)))
          .GroupBy(item => item.LogicalName, StringComparer.OrdinalIgnoreCase)
          .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
          .ToArray();

        if (groups.Length == 0) continue;

        foreach (var group in groups) {
          String[] paths = group
            .OrderBy(item => item.Number)
            .ThenBy(item => Path.GetFileName(item.Path), StringComparer.OrdinalIgnoreCase)
            .Select(item => item.Path)
            .ToArray();

          String libraryName = String.IsNullOrEmpty(prefix)
            ? group.Key
            : prefix.TrimEnd('_') + "_" + group.Key;
          Libraries.Add(new Library(libraryName, paths));
          foreach (String path in paths) m_loadedArchivePaths.Add(Path.GetFullPath(path));
        }

        if (!LoadedFileGroups.Contains(fileGroup)) LoadedFileGroups.Add(fileGroup);
        return; // Prefer the first matching environment family, same as the old loader.
      }
    }

    private void LoadUnclaimedAssetFiles(Boolean isPtr) {
      if (!Directory.Exists(m_assetPath)) return;

      String[] archiveFiles = Directory.EnumerateFiles(m_assetPath, "*", SearchOption.TopDirectoryOnly)
        .Where(path => Path.GetExtension(path).Equals(".tor", StringComparison.OrdinalIgnoreCase))
        .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
        .ToArray();

      Boolean loadedAny = false;
      foreach (String archiveFile in archiveFiles) {
        String fullPath = Path.GetFullPath(archiveFile);
        if (m_loadedArchivePaths.Contains(fullPath)) continue;

        String name = Path.GetFileName(archiveFile);
        Boolean ptsArchive = name.StartsWith("swtor_test_", StringComparison.OrdinalIgnoreCase);
        Boolean liveArchive = name.StartsWith("swtor_", StringComparison.OrdinalIgnoreCase) && !ptsArchive;
        // Jedipedia treats non-swtor/non-swtor_test archives as common. Keep beta/dev/legacy TORs available
        // regardless of the selected LIVE/PTS branch, while never mixing the two explicit SWTOR branches.
        if ((isPtr && liveArchive) || (!isPtr && ptsArchive)) continue;

        String archiveName = Path.GetFileNameWithoutExtension(archiveFile);
        Libraries.Add(new Library("archive_" + archiveName, new[] { archiveFile }));
        m_loadedArchivePaths.Add(fullPath);
        loadedAny = true;
      }

      if (loadedAny && !LoadedFileGroups.Contains("other")) LoadedFileGroups.Add("other");
    }

    private sealed class ArchivePathInfo {
      public String Path { get; set; }
      public String LogicalName { get; set; }
      public Int32 Number { get; set; }
    }

    private static ArchivePathInfo ParseArchivePath(String path, String prefix) {
      String fileName = Path.GetFileNameWithoutExtension(path);
      if (!fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;

      String remainder = fileName.Substring(prefix.Length);
      Int32 split = remainder.LastIndexOf('_');
      if (split <= 0 || split == remainder.Length - 1) return null;
      if (!Int32.TryParse(remainder.Substring(split + 1), out Int32 number) || number <= 0) return null;

      return new ArchivePathInfo {
        Path = path,
        LogicalName = remainder.Substring(0, split),
        Number = number
      };
    }

    private void LoadRetailClientFiles(Boolean isPtr) {
      String clientPath = GetRetailClientDirectory(m_gamePath, isPtr);
      if (!Directory.Exists(clientPath)) return;

      String[] clientTorFiles = Directory.EnumerateFiles(clientPath, "*", SearchOption.TopDirectoryOnly)
        .Where(file => Path.GetExtension(file).Equals(".tor", StringComparison.OrdinalIgnoreCase))
        .OrderBy(file => Path.GetFileName(file), StringComparer.OrdinalIgnoreCase)
        .ToArray();

      foreach (String clientTorFile in clientTorFiles) {
        // One lightweight direct library per physical archive avoids building a second metadata
        // index for client TORs. Archive itself already has a hash lookup, so this keeps RAM use low.
        String archiveName = Path.GetFileNameWithoutExtension(clientTorFile);
        String libraryName = "retailclient_" + archiveName;
        Libraries.Add(new Library(libraryName, new[] { clientTorFile }));
        m_loadedArchivePaths.Add(Path.GetFullPath(clientTorFile));
      }
    }

    #endregion Methods

    #region Properties
    public Icons Icons { get; }
    public List<Library> Libraries { get; private set; }
    public IEnumerable<String> ArchiveLoadWarnings => Libraries == null
      ? Enumerable.Empty<String>()
      : Libraries.SelectMany(library => library.LoadWarnings);
    public List<String> LoadedFileGroups { get; private set; }
    public String GamePath => m_gamePath;
    public String AssetPath => m_assetPath;

    #endregion Properties
  }
}
