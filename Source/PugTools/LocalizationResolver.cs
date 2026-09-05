using System;
using System.Collections.Generic;
using System.Linq;
using GomLib;
using TorArchive;

namespace PugTools {
  /// <summary>
  /// Keeps the configured language as the user's preference, but resolves the language actually
  /// used at runtime against the locale archives that are installed/mounted. This is intentionally
  /// separate from Config.Language: if a user normally uses German and opens an English-only beta
  /// client, the preference remains German and becomes active again when a German client is used.
  /// </summary>
  internal static class LocalizationResolver {
    private static readonly Object Sync = new Object();
    private static readonly String[] KnownLocales = { "en-us", "de-de", "fr-fr" };

    public static String RequestedLocale { get; private set; } = "en-us";
    public static String EffectiveLocale { get; private set; } = "en-us";
    public static Boolean UsingFallback =>
      !String.Equals(RequestedLocale, EffectiveLocale, StringComparison.OrdinalIgnoreCase);

    public static String ApplyRequested(String requested) {
      lock (Sync) {
        RequestedLocale = Normalize(requested);
        SetEffective(RequestedLocale);
        return EffectiveLocale;
      }
    }

    public static String Apply(Assets assets, String requested = null) {
      lock (Sync) {
        RequestedLocale = Normalize(requested ?? RequestedLocale);
        HashSet<String> installed = FindInstalledLocales(assets);

        String resolved = RequestedLocale;
        if (installed.Count > 0 && !installed.Contains(RequestedLocale)) {
          if (installed.Count == 1) {
            resolved = installed.First();
          } else if (installed.Contains("en-us")) {
            // Explicit user requirement: if several alternatives exist, prefer English.
            resolved = "en-us";
          } else {
            // English is not actually installed; use a real installed language rather than a
            // non-existent one. Keep this deterministic for German/French-only installations.
            resolved = KnownLocales.FirstOrDefault(installed.Contains) ?? installed.First();
          }
        }

        SetEffective(resolved);
        return EffectiveLocale;
      }
    }

    public static String GetStatusText() {
      return UsingFallback
        ? RequestedLocale + " -> " + EffectiveLocale + " (installed language fallback)"
        : EffectiveLocale;
    }

    private static HashSet<String> FindInstalledLocales(Assets assets) {
      HashSet<String> installed = new HashSet<String>(StringComparer.OrdinalIgnoreCase);
      if (assets == null) return installed;

      try {
        foreach (String group in assets.LoadedFileGroups ?? new List<String>())
          AddLocaleFromName(installed, group);
      } catch { }

      // LoadedFileGroups is authoritative for normal clients. Library names are also checked for
      // older/beta layouts where the locale lives in a differently named TOR family.
      try {
        foreach (Library library in assets.Libraries ?? new List<Library>())
          AddLocaleFromName(installed, library?.Name);
      } catch { }

      return installed;
    }

    private static void AddLocaleFromName(HashSet<String> installed, String value) {
      if (String.IsNullOrWhiteSpace(value)) return;
      String name = value.Trim().ToLowerInvariant().Replace('_', '-');
      if (name.Contains("en-us", StringComparison.OrdinalIgnoreCase)
          || name.Contains("locale-en-us", StringComparison.OrdinalIgnoreCase))
        installed.Add("en-us");
      if (name.Contains("de-de", StringComparison.OrdinalIgnoreCase)) installed.Add("de-de");
      if (name.Contains("fr-fr", StringComparison.OrdinalIgnoreCase)) installed.Add("fr-fr");
    }

    private static String Normalize(String locale) {
      String value = (locale ?? String.Empty).Trim().ToLowerInvariant().Replace('_', '-');
      if (value.StartsWith("de", StringComparison.OrdinalIgnoreCase)) return "de-de";
      if (value.StartsWith("fr", StringComparison.OrdinalIgnoreCase)) return "fr-fr";
      return "en-us";
    }

    private static void SetEffective(String locale) {
      EffectiveLocale = Normalize(locale);
      String localization = EffectiveLocale == "de-de"
        ? "deMale"
        : (EffectiveLocale == "fr-fr" ? "frMale" : "enMale");
      StringTable.SelectedLocale = EffectiveLocale;
      StringTable.SelectedLocalization = localization;
      GomLib.Models.Tooltip.Language = localization;
    }
  }
}
