using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace PugTools {
  /// <summary>
  /// Jedipedia-compatible readers for HeroEngine's text based dynamic-character specifications.
  /// MAG and DYC used to fall through to the raw text view even though they contain useful asset references.
  /// </summary>
  internal static class ViewTextSpecs {
    internal sealed class Parameter {
      internal String Key = String.Empty;
      internal String Value = String.Empty;
    }

    internal sealed class TextSpecInfo {
      internal String Type = String.Empty;
      internal String Character = String.Empty;
      internal String FullText = String.Empty;
      internal readonly List<Parameter> Parameters = new List<Parameter>();
      internal readonly List<String> GrannyReferences = new List<String>();
    }

    internal static TextSpecInfo ParseMag(Stream input) {
      String text = ReadAllText(input);
      String[] lines = SplitLines(text);
      var info = new TextSpecInfo { FullText = text, Type = "MAG / Morpheme Animated Granny" };
      String first = lines.Length > 0 ? lines[0].Trim() : String.Empty;
      String second = lines.Length > 1 ? lines[1].Trim() : String.Empty;
      String third = lines.Length > 2 ? lines[2].Trim() : String.Empty;
      Boolean namedHeader = first == "!" && third == "!" &&
        (second.StartsWith("! Character Specification for ", StringComparison.OrdinalIgnoreCase) || second.StartsWith("! Mag Specification for ", StringComparison.OrdinalIgnoreCase));
      Boolean legacyHeader = first == "[]" && String.Equals(second, "Version=1", StringComparison.OrdinalIgnoreCase) && String.Equals(third, "[PARTS]", StringComparison.OrdinalIgnoreCase);
      Boolean compactHeader = first.StartsWith("! Character Specification for ", StringComparison.OrdinalIgnoreCase) && String.IsNullOrEmpty(second);
      Boolean headerless = first.StartsWith("Model=", StringComparison.OrdinalIgnoreCase);

      Int32 start = headerless ? 0 : compactHeader ? 1 : (namedHeader || legacyHeader ? 3 : 0);
      if (namedHeader) {
        if (second.StartsWith("! Character Specification for ", StringComparison.OrdinalIgnoreCase)) {
          info.Character = second.Substring("! Character Specification for ".Length).Trim();
          info.Type = "Character Specification, version 1 (MAG)";
        } else {
          info.Character = second.Substring("! Mag Specification for ".Length).Trim();
        }
      } else if (legacyHeader || compactHeader || headerless) {
        info.Type = "Character Specification, version 1 (MAG)";
      }

      for (Int32 i = start; i < lines.Length; i++) {
        String line = lines[i].Trim();
        if (line.Length == 0 || line[0] == '!') continue;
        if (line[0] == '[' && line.EndsWith("]", StringComparison.Ordinal)) continue;
        Int32 equals = line.IndexOf('=');
        String key = equals < 0 ? line : line.Substring(0, equals).Trim();
        String value = equals < 0 ? String.Empty : line.Substring(equals + 1).Trim();
        info.Parameters.Add(new Parameter { Key = key, Value = value });
      }

      Parameter model = info.Parameters.FirstOrDefault(x => String.Equals(x.Key, "Model", StringComparison.OrdinalIgnoreCase));
      if (String.IsNullOrWhiteSpace(info.Character) && model != null) {
        String file = model.Value.Replace('\\', '/');
        Int32 slash = file.LastIndexOf('/'); if (slash >= 0) file = file.Substring(slash + 1);
        if (file.EndsWith(".dyc", StringComparison.OrdinalIgnoreCase)) file = file.Substring(0, file.Length - 4);
        info.Character = file;
      }
      AddGrannyReferences(info, text);
      return info;
    }

    internal static TextSpecInfo ParseDyc(Stream input) {
      String text = ReadAllText(input);
      var info = new TextSpecInfo { FullText = text, Type = "DYC / Dynamic Character Specification" };
      String section = String.Empty;
      foreach (String raw in SplitLines(text)) {
        String line = raw.Trim();
        if (line.Length == 0 || line[0] == '!' || line[0] == ';' || line[0] == '#') continue;
        if (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal)) {
          section = line.Substring(1, line.Length - 2).Trim();
          continue;
        }
        Int32 equals = line.IndexOf('=');
        if (equals < 0) continue;
        String key = line.Substring(0, equals).Trim();
        if (!String.IsNullOrWhiteSpace(section)) key = section + "." + key;
        info.Parameters.Add(new Parameter { Key = key, Value = line.Substring(equals + 1).Trim().TrimEnd('<') });
      }
      AddGrannyReferences(info, text);
      return info;
    }

    internal static ArrayList BuildTree(TextSpecInfo info) {
      var roots = new ArrayList();
      if (info == null) return roots;
      var header = new NodeListItem("Specification", info.Type);
      if (!String.IsNullOrWhiteSpace(info.Character)) header.children.Add(new NodeListItem("Character / model", info.Character));
      header.children.Add(new NodeListItem("Parameters", info.Parameters.Count));
      header.children.Add(new NodeListItem("GR2 references", info.GrannyReferences.Count));
      roots.Add(header);

      var parameters = new NodeListItem("Parameters", info.Parameters.Count + " entries");
      for (Int32 i = 0; i < info.Parameters.Count; i++) {
        Parameter p = info.Parameters[i];
        parameters.children.Add(new NodeListItem(String.IsNullOrWhiteSpace(p.Key) ? "#" + i : p.Key, p.Value));
      }
      roots.Add(parameters);

      if (info.GrannyReferences.Count > 0) {
        var refs = new NodeListItem("Referenced Granny assets", info.GrannyReferences.Count + " entries");
        foreach (String path in info.GrannyReferences) {
          var node = new NodeListItem(path, "GR2");
          String collision = Regex.Replace(path, "\\.gr2$", ".collision", RegexOptions.IgnoreCase);
          if (!String.Equals(collision, path, StringComparison.OrdinalIgnoreCase)) node.children.Add(new NodeListItem("Collision candidate", collision));
          refs.children.Add(node);
        }
        roots.Add(refs);
      }
      return roots;
    }

    private static String ReadAllText(Stream input) {
      if (input == null) throw new ArgumentNullException(nameof(input));
      input.Position = 0;
      using var reader = new StreamReader(input, Encoding.UTF8, true, 4096, true);
      String text = reader.ReadToEnd();
      input.Position = 0;
      return text;
    }

    private static String[] SplitLines(String text) => (text ?? String.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

    private static void AddGrannyReferences(TextSpecInfo info, String text) {
      var seen = new HashSet<String>(StringComparer.OrdinalIgnoreCase);
      foreach (Match match in Regex.Matches(text ?? String.Empty, @"(?i)([a-z0-9_./\\-]+\.gr2)")) {
        String path = match.Groups[1].Value.Trim().TrimEnd('<').Replace('\\', '/').ToLowerInvariant();
        if (path.Length > 0 && seen.Add(path)) info.GrannyReferences.Add(path);
      }
    }
  }
}
