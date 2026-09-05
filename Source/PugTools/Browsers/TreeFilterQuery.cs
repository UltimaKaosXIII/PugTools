using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace PugTools {
  /// <summary>
  /// Shared Jedipedia-style text filter parser used by the Asset and Node browsers.
  /// Plain terms are ANDed, "-term" excludes matches, and path separators are normalized.
  /// Version operators follow Jedipedia's firstSeen semantics: &gt; means this patch and later,
  /// &lt; means strictly earlier, and = matches the explicitly supplied version components.
  /// </summary>
  internal sealed class TreeFilterQuery {
    private static readonly Regex VersionFilterPattern = new Regex(
      @"^[><=]\d+(?:\.\d+){0,2}[a-z]?(?:[#-]\d+)?\.?$",
      RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    );
    private static readonly Regex FirstSeenPattern = new Regex(
      @"^(\d+)(?:\.(\d+))?(?:\.(\d+))?([a-z]?)(?: #(\d+))?(?=$| )",
      RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    );
    private static readonly Regex ParsedVersionFilterPattern = new Regex(
      @"^(\d+)(?:\.(\d+))?(?:\.(\d+))?([a-z]?)(?:[#-](\d+))?\.?$",
      RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    );

    private readonly struct VersionKey : IComparable<VersionKey> {
      internal readonly Int32 Major;
      internal readonly Int32 Minor;
      internal readonly Int32 Patch;
      internal readonly Int32 Letter;
      internal readonly Int32 Build;

      internal VersionKey(Int32 major, Int32 minor, Int32 patch, Int32 letter, Int32 build) {
        Major = major;
        Minor = minor;
        Patch = patch;
        Letter = letter;
        Build = build;
      }

      internal Int32 Component(Int32 index) => index switch {
        0 => Major,
        1 => Minor,
        2 => Patch,
        3 => Letter,
        _ => Build
      };

      public Int32 CompareTo(VersionKey other) {
        for (Int32 i = 0; i < 5; i++) {
          Int32 cmp = Component(i).CompareTo(other.Component(i));
          if (cmp != 0) return cmp;
        }
        return 0;
      }
    }

    private readonly struct ParsedVersionFilter {
      internal readonly Char Operator;
      internal readonly Int32[] Numbers;
      internal readonly Int32 Letter;
      internal readonly Boolean HasLetter;
      internal readonly Int32 Build;
      internal readonly Boolean HasBuild;

      internal ParsedVersionFilter(Char op, Int32[] numbers, Int32 letter, Boolean hasLetter,
                                   Int32 build, Boolean hasBuild) {
        Operator = op;
        Numbers = numbers;
        Letter = letter;
        HasLetter = hasLetter;
        Build = build;
        HasBuild = hasBuild;
      }
    }

    internal String RawText { get; }
    internal IReadOnlyList<String> RequiredTerms { get; }
    internal IReadOnlyList<String> BlockedTerms { get; }
    internal IReadOnlyList<String> VersionTerms { get; }
    internal Boolean ShowUnnamedOnly { get; }

    private IReadOnlyList<ParsedVersionFilter> ParsedVersionFilters { get; }

    internal Boolean HasTextTerms => RequiredTerms.Count > 0 || BlockedTerms.Count > 0;
    internal Boolean HasVersionTerms => VersionTerms.Count > 0;
    internal Boolean IsEmpty => !ShowUnnamedOnly && !HasTextTerms && !HasVersionTerms;

    private TreeFilterQuery(
      String rawText,
      IReadOnlyList<String> requiredTerms,
      IReadOnlyList<String> blockedTerms,
      IReadOnlyList<String> versionTerms,
      IReadOnlyList<ParsedVersionFilter> parsedVersionFilters,
      Boolean showUnnamedOnly
    ) {
      RawText = rawText;
      RequiredTerms = requiredTerms;
      BlockedTerms = blockedTerms;
      VersionTerms = versionTerms;
      ParsedVersionFilters = parsedVersionFilters;
      ShowUnnamedOnly = showUnnamedOnly;
    }

    internal static TreeFilterQuery Parse(String text, Boolean allowUnnamedShortcut) {
      String raw = (text ?? String.Empty).Trim();
      if (allowUnnamedShortcut && raw == "?") {
        return new TreeFilterQuery(
          raw,
          Array.Empty<String>(),
          Array.Empty<String>(),
          Array.Empty<String>(),
          Array.Empty<ParsedVersionFilter>(),
          true
        );
      }

      var required = new List<String>();
      var blocked = new List<String>();
      var versions = new List<String>();
      var parsedVersions = new List<ParsedVersionFilter>();

      foreach (String sourceWord in raw.Split((Char[])null, StringSplitOptions.RemoveEmptyEntries)) {
        String word = sourceWord.Replace('\\', '/').Trim();
        if (word.Length == 0 || word == "-" || word == ">" || word == "<" || word == "=") continue;

        if (VersionFilterPattern.IsMatch(word) && TryParseVersionFilter(word, out ParsedVersionFilter vf)) {
          versions.Add(word);
          parsedVersions.Add(vf);
          continue;
        }

        if (word[0] == '-' && word.Length > 1)
          blocked.Add(word.Substring(1));
        else
          required.Add(word);
      }

      // Check the most selective terms first. With millions of asset names this avoids doing
      // extra IndexOf calls for broad one- or two-character terms when a longer term already
      // rejects the candidate.
      required = required.OrderByDescending(x => x.Length).ToList();
      blocked = blocked.OrderByDescending(x => x.Length).ToList();

      return new TreeFilterQuery(raw, required, blocked, versions, parsedVersions, false);
    }

    internal Boolean Matches(params String[] candidates) {
      if (ShowUnnamedOnly) return false;

      foreach (String term in RequiredTerms) {
        if (!AnyCandidateContains(candidates, term)) return false;
      }

      foreach (String term in BlockedTerms) {
        if (AnyCandidateContains(candidates, term)) return false;
      }

      return true;
    }

    // Hot-path overloads used by the live browsers. The params overload above allocates a new
    // String[] for every candidate. At SWTOR scale that means millions of short-lived arrays per
    // keystroke, which can trigger stop-the-world GC pauses and make the whole WinForms process
    // look frozen even though the actual scan is running on a worker thread.
    internal Boolean Matches(String candidate1, String candidate2) {
      if (ShowUnnamedOnly) return false;

      // Use indexed loops rather than foreach over IReadOnlyList. The latter can box a List<T>
      // enumerator behind the interface on every candidate, reintroducing the exact high-volume
      // allocation pressure these hot-path overloads are meant to remove.
      for (Int32 i = 0; i < RequiredTerms.Count; i++) {
        String term = RequiredTerms[i];
        if (!Contains(candidate1, term) && !Contains(candidate2, term)) return false;
      }
      for (Int32 i = 0; i < BlockedTerms.Count; i++) {
        String term = BlockedTerms[i];
        if (Contains(candidate1, term) || Contains(candidate2, term)) return false;
      }
      return true;
    }

    internal Boolean Matches(
      String candidate1,
      String candidate2,
      String candidate3,
      String candidate4,
      String candidate5
    ) {
      if (ShowUnnamedOnly) return false;

      for (Int32 i = 0; i < RequiredTerms.Count; i++) {
        String term = RequiredTerms[i];
        if (!Contains(candidate1, term)
            && !Contains(candidate2, term)
            && !Contains(candidate3, term)
            && !Contains(candidate4, term)
            && !Contains(candidate5, term)) return false;
      }
      for (Int32 i = 0; i < BlockedTerms.Count; i++) {
        String term = BlockedTerms[i];
        if (Contains(candidate1, term)
            || Contains(candidate2, term)
            || Contains(candidate3, term)
            || Contains(candidate4, term)
            || Contains(candidate5, term)) return false;
      }
      return true;
    }

    /// <summary>
    /// Tests a first-seen label using Jedipedia's version-filter behavior. Missing history is a
    /// non-match whenever a version term is present; text-only searches never call this method.
    /// </summary>
    internal Boolean MatchesVersion(String firstSeen) {
      if (!HasVersionTerms) return true;
      if (!TryParseFirstSeen(firstSeen, out VersionKey key)) return false;
      for (Int32 i = 0; i < ParsedVersionFilters.Count; i++) {
        if (!MatchVersionFilter(key, ParsedVersionFilters[i])) return false;
      }
      return true;
    }

    private static Boolean AnyCandidateContains(IEnumerable<String> candidates, String term) {
      foreach (String candidate in candidates) {
        if (Contains(candidate, term)) return true;
      }
      return false;
    }

    private static Boolean Contains(String candidate, String term) {
      return !String.IsNullOrEmpty(candidate)
        && candidate.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static Boolean TryParseVersionFilter(String word, out ParsedVersionFilter filter) {
      filter = default;
      if (String.IsNullOrWhiteSpace(word) || word.Length < 2) return false;
      Char op = word[0];
      if (op != '>' && op != '<' && op != '=') return false;

      Match match = ParsedVersionFilterPattern.Match(word.Substring(1));
      if (!match.Success || !TryParseInt(match.Groups[1].Value, out Int32 major)) return false;
      var numbers = new List<Int32> { major };
      if (match.Groups[2].Success) {
        if (!TryParseInt(match.Groups[2].Value, out Int32 minor)) return false;
        numbers.Add(minor);
      }
      if (match.Groups[3].Success) {
        if (!TryParseInt(match.Groups[3].Value, out Int32 patch)) return false;
        numbers.Add(patch);
      }
      Boolean hasLetter = match.Groups[4].Success && match.Groups[4].Value.Length > 0;
      Int32 letter = hasLetter ? Char.ToLowerInvariant(match.Groups[4].Value[0]) - 'a' + 1 : 0;
      Boolean hasBuild = match.Groups[5].Success;
      Int32 build = 0;
      if (hasBuild && !TryParseInt(match.Groups[5].Value, out build)) return false;
      filter = new ParsedVersionFilter(op, numbers.ToArray(), letter, hasLetter, build, hasBuild);
      return true;
    }

    private static Boolean TryParseFirstSeen(String value, out VersionKey key) {
      key = default;
      if (String.IsNullOrWhiteSpace(value)) return false;
      String text = value.Trim();
      if (text.Equals("Beta", StringComparison.OrdinalIgnoreCase)
          || text.StartsWith("Beta ", StringComparison.OrdinalIgnoreCase)) {
        key = new VersionKey(-1, 0, 0, 0, Int32.MaxValue);
        return true;
      }
      if (text.StartsWith("PTS ", StringComparison.OrdinalIgnoreCase)) text = text.Substring(4);

      Match match = FirstSeenPattern.Match(text);
      if (!match.Success || !TryParseInt(match.Groups[1].Value, out Int32 major)) return false;
      Int32 minor = 0;
      Int32 patch = 0;
      if (match.Groups[2].Success && !TryParseInt(match.Groups[2].Value, out minor)) return false;
      if (match.Groups[3].Success && !TryParseInt(match.Groups[3].Value, out patch)) return false;
      Int32 letter = match.Groups[4].Success && match.Groups[4].Value.Length > 0
        ? Char.ToLowerInvariant(match.Groups[4].Value[0]) - 'a' + 1
        : 0;
      Int32 build = Int32.MaxValue;
      if (match.Groups[5].Success && !TryParseInt(match.Groups[5].Value, out build)) return false;
      key = new VersionKey(major, minor, patch, letter, build);
      return true;
    }

    private static Boolean TryParseInt(String value, out Int32 result) {
      return Int32.TryParse(value, System.Globalization.NumberStyles.None,
                            System.Globalization.CultureInfo.InvariantCulture, out result);
    }

    private static Boolean MatchVersionFilter(VersionKey key, ParsedVersionFilter filter) {
      if (filter.Operator == '=') {
        for (Int32 i = 0; i < filter.Numbers.Length; i++) {
          if (key.Component(i) != filter.Numbers[i]) return false;
        }
        if (filter.HasLetter && key.Letter != filter.Letter) return false;
        return !filter.HasBuild || key.Build == filter.Build;
      }

      var target = new VersionKey(
        filter.Numbers[0],
        filter.Numbers.Length > 1 ? filter.Numbers[1] : 0,
        filter.Numbers.Length > 2 ? filter.Numbers[2] : 0,
        filter.Letter,
        filter.Build
      );
      Int32 comparison = key.CompareTo(target);
      return filter.Operator == '>' ? comparison >= 0 : comparison < 0;
    }
  }
}
