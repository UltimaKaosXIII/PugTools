using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

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
    private readonly Regex m_fileNameParse = new Regex("swtor_(?:test_)?(.*)_1");

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
      Libraries = new List<Library>();
      LoadedFileGroups.Clear();

      LoadAssetFiles("main", isPtr);
      LoadAssetFiles("en-us", isPtr);
      LoadAssetFiles("fr-fr", isPtr);
      LoadAssetFiles("de-de", isPtr);

      // Beta
      LoadAssetFiles("locale_en_us", isPtr);
      LoadAssetFiles("system", isPtr);

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

      // LIVE & PTS
      String searchPattern = isPTS ? $"swtor_test_{fileGroup}_*.tor" : $"swtor_{fileGroup}_*.tor";
      String[] assetFilePaths = Directory.GetFiles(m_assetPath, searchPattern, SearchOption.TopDirectoryOnly);

      if (assetFilePaths.Length > 0) {
        foreach (String assetFilePath in assetFilePaths) {
          String assetFileName = Path.GetFileNameWithoutExtension(assetFilePath);
          Match match = m_fileNameParse.Match(assetFileName);

          if (match.Success) {
            String libName = match.Groups[1].Value;
            Library lib = new Library(libName, m_assetPath, isPTS);
            Libraries.Add(lib);
          }
        }

        LoadedFileGroups.Add(fileGroup);
        return;
      }

      // BETA: RED
      assetFilePaths = Directory.GetFiles(m_assetPath, $"red_{fileGroup}_*.tor", SearchOption.TopDirectoryOnly);

      if (assetFilePaths.Length > 0) {
        Library lib = new Library(fileGroup, m_assetPath);
        Libraries.Add(lib);
        LoadedFileGroups.Add(fileGroup);
        return;
      }

      // BETA: ASSETS
      assetFilePaths = Directory.GetFiles(m_assetPath, $"assets_{fileGroup}_*.tor", SearchOption.TopDirectoryOnly);

      if (assetFilePaths.Length > 0) {
        Library lib = new Library(fileGroup, m_assetPath);
        Libraries.Add(lib);
        LoadedFileGroups.Add(fileGroup);
        return;
      }
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
      }
    }

    #endregion Methods

    #region Properties
    public Icons Icons { get; }
    public List<Library> Libraries { get; private set; }
    public List<String> LoadedFileGroups { get; private set; }
    public String GamePath => m_gamePath;
    public String AssetPath => m_assetPath;

    #endregion Properties
  }
}
