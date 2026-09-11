using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;

namespace PugTools {
  public static class Config {
    private static String _assetsPath = ".";
    private static Boolean _assetsUsePTS;
    private static Configuration _configFile;
    private static Boolean _crossLinkDOM;
    private static String _extractAssetsPath = ".";
    private static String _extractPath = ".";
    private static String _language = "en-us";
    private static String _prevAssetsPath = ".";
    private static Boolean _prevAssetsUsePTS;
    private static Int32 _nodePreviewTextHeight = 260;

    public static String AssetsPath {
      get => _assetsPath;
      set => _assetsPath = value;
    }
    public static Boolean AssetsUsePTS {
      get => _assetsUsePTS;
      set => _assetsUsePTS = value;
    }
    public static Configuration ConfigFile {
      get => _configFile;
      set => _configFile = value;
    }
    public static Boolean CrossLinkDOM {
      get => _crossLinkDOM;
      set => _crossLinkDOM = value;
    }
    public static String ExtractAssetsPath {
      get => _extractAssetsPath;
      set => _extractAssetsPath = value;
    }
    public static String ExtractPath {
      get => _extractPath;
      set => _extractPath = value;
    }
    public static String Language {
      get => _language;
      set {
        string locale = (value ?? String.Empty).Trim().ToLowerInvariant();
        _language = locale == "de-de" || locale == "fr-fr" ? locale : "en-us";
      }
    }
    public static String PrevAssetsPath {
      get => _prevAssetsPath;
      set => _prevAssetsPath = value;
    }
    public static Boolean PrevAssetsUsePTS {
      get => _prevAssetsUsePTS;
      set => _prevAssetsUsePTS = value;
    }
    public static Int32 NodePreviewTextHeight {
      get => _nodePreviewTextHeight;
      set => _nodePreviewTextHeight = Math.Max(100, Math.Min(1400, value));
    }

    private static readonly List<String> liveGamePaths = new List<String> {
      "C:\\Program Files (x86)\\EA\\BioWare\\Star Wars - The Old Republic\\",
      "C:\\Program Files (x86)\\Electronic Arts\\BioWare\\Star Wars - The Old Republic\\",
      "C:\\Program Files (x86)\\Steam\\steamapps\\common\\Star Wars - The Old Republic\\"
    };

    private static readonly List<String> ptsGamePaths = new List<String> {
      "C:\\Program Files (x86)\\EA\\BioWare\\Star Wars - The Old Republic\\",
      "C:\\Program Files (x86)\\Electronic Arts\\BioWare\\Star Wars - The Old Republic\\",
      "C:\\Program Files (x86)\\Steam\\steamapps\\common\\Star Wars - The Old Republic - PTS\\"
    };

    private static String NormalizeGamePath(String path) {
      if (String.IsNullOrWhiteSpace(path)) return String.Empty;

      String normalized = TorArchive.Assets.NormalizeGamePath(path);
      if (String.IsNullOrEmpty(normalized)) return String.Empty;
      return normalized.EndsWith("\\") ? normalized : normalized + "\\";
    }

    public static void Load() {
      // Path to the asset files
      String str = ConfigFile.AppSettings.Settings["AssetsPath"].Value;

      if (str != null)
        // Load from config, if directory exists
        if (Directory.Exists(str)) AssetsPath = NormalizeGamePath(str);
        // Otherwise check some default directories
        else if (Directory.Exists(liveGamePaths[0])) AssetsPath = NormalizeGamePath(liveGamePaths[0]);
        else if (Directory.Exists(liveGamePaths[1])) AssetsPath = NormalizeGamePath(liveGamePaths[1]);
        else if (Directory.Exists(liveGamePaths[2])) AssetsPath = NormalizeGamePath(liveGamePaths[2]);
        else AssetsPath = String.Empty;

      // Load PTS assets if checked
      str = ConfigFile.AppSettings.Settings["AssetsUsePTS"].Value;
      if (str != null) AssetsUsePTS = Convert.ToBoolean(str);

      // Path to the previous asset files
      str = ConfigFile.AppSettings.Settings["PrevAssetsPath"].Value;

      if (str != null)
        // Load from config, if directory exists
        if (Directory.Exists(str)) PrevAssetsPath = NormalizeGamePath(str);
        //otherwise check some default directories
        else if (Directory.Exists(ptsGamePaths[0])) PrevAssetsPath = NormalizeGamePath(ptsGamePaths[0]);
        else if (Directory.Exists(ptsGamePaths[1])) PrevAssetsPath = NormalizeGamePath(ptsGamePaths[1]);
        else if (Directory.Exists(ptsGamePaths[2])) PrevAssetsPath = NormalizeGamePath(ptsGamePaths[2]);
        else PrevAssetsPath = NormalizeGamePath(str);

      // Load PTS assets if checked
      str = ConfigFile.AppSettings.Settings["PrevAssetsUsePTS"].Value;

      if (str != null) PrevAssetsUsePTS = Convert.ToBoolean(str);

      // Path to the extract files
      str = ConfigFile.AppSettings.Settings["ExtractPath"].Value;

      if (str != null) {
        if (!str.EndsWith("\\")) ExtractPath = str + "\\";
        else ExtractPath = str;
      }

      // Path Where to Extract Assets
      str = ConfigFile.AppSettings.Settings["ExtractAssetsPath"].Value;

      if (str != null) {
        if (!str.EndsWith("\\")) ExtractAssetsPath = str + "\\";
        else ExtractAssetsPath = str;
      }

      // Cross Link DOM
      str = ConfigFile.AppSettings.Settings["CrossLinkDOM"]?.Value;

      if (str != null) CrossLinkDOM = Convert.ToBoolean(str);

      // UI / localized asset language
      str = ConfigFile.AppSettings.Settings["Language"]?.Value;
      if (!String.IsNullOrWhiteSpace(str)) Language = str;

      // Remember the Node Browser interpreted-text height so abl/ach/qst/etc. do
      // not snap back to the default after every selection or application restart.
      str = ConfigFile.AppSettings.Settings["NodePreviewTextHeight"]?.Value;
      if (Int32.TryParse(str, out Int32 previewHeight)) NodePreviewTextHeight = previewHeight;
    }
    public static void Save() {
      // Path to the asset files
      String str = AssetsPath;
      if (str != null) ConfigFile.AppSettings.Settings["AssetsPath"].Value = str;

      // Load PTS assets if checked
      str = AssetsUsePTS.ToString();
      if (str != null) ConfigFile.AppSettings.Settings["AssetsUsePTS"].Value = str;

      // Path to the previous asset files
      str = PrevAssetsPath;
      if (str != null) ConfigFile.AppSettings.Settings["PrevAssetsPath"].Value = str;

      // Load PTS assets if checked
      str = PrevAssetsUsePTS.ToString();
      if (str != null) ConfigFile.AppSettings.Settings["PrevAssetsUsePTS"].Value = str;

      // Path to the extract files
      str = ExtractPath;
      if (str != null) ConfigFile.AppSettings.Settings["ExtractPath"].Value = str;

      // Path to the extract files
      str = ExtractAssetsPath;
      if (str != null) ConfigFile.AppSettings.Settings["ExtractAssetsPath"].Value = str;

      // Cross Link DOM
      str = CrossLinkDOM.ToString();
      if (str != null) ConfigFile.AppSettings.Settings["CrossLinkDOM"].Value = str;

      // UI / localized asset language
      str = Language;
      if (ConfigFile.AppSettings.Settings["Language"] == null) ConfigFile.AppSettings.Settings.Add("Language", str);
      else ConfigFile.AppSettings.Settings["Language"].Value = str;

      str = NodePreviewTextHeight.ToString();
      if (ConfigFile.AppSettings.Settings["NodePreviewTextHeight"] == null)
        ConfigFile.AppSettings.Settings.Add("NodePreviewTextHeight", str);
      else
        ConfigFile.AppSettings.Settings["NodePreviewTextHeight"].Value = str;

      ConfigFile.Save(ConfigurationSaveMode.Modified);
      ConfigurationManager.RefreshSection("appSettings");
    }
  }
}
