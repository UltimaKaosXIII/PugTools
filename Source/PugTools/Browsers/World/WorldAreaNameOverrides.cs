using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace PugTools {
  internal sealed class WorldAreaOverride {
    public ulong Id { get; set; }
    public string Name { get; set; }
    public string InternalName { get; set; }
    public string Category { get; set; }
    public string Group { get; set; }
  }

  /// <summary>
  /// User-editable World Browser metadata. The source copy is shipped next to PugTools.exe; at runtime every
  /// detected area ID is merged into it. name overrides the display name, while category/group control where
  /// the area appears in the tree. Existing user values are never overwritten by the automatic synchronizer.
  /// </summary>
  internal static class WorldAreaNameOverrides {
    private const string FileName = "WorldAreaNames.xml";
    public const string UnassignedCategory = "Unassigned";

    public static string RuntimePath => Path.Combine(AppContext.BaseDirectory, FileName);

    public static Dictionary<ulong, WorldAreaOverride> LoadEntries() {
      var result = new Dictionary<ulong, WorldAreaOverride>();
      try {
        XDocument doc = LoadDocument(RuntimePath);
        foreach (XElement element in doc.Root?.Elements("Area") ?? Enumerable.Empty<XElement>()) {
          if (!ulong.TryParse((string)element.Attribute("id"), NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong id)) continue;
          if (result.ContainsKey(id)) continue;
          result[id] = new WorldAreaOverride {
            Id = id,
            Name = ((string)element.Attribute("name") ?? String.Empty).Trim(),
            InternalName = ((string)element.Attribute("internalName") ?? String.Empty).Trim(),
            Category = ((string)element.Attribute("category") ?? String.Empty).Trim(),
            Group = ((string)element.Attribute("group") ?? String.Empty).Trim()
          };
        }
      } catch (Exception ex) {
        System.Diagnostics.Debug.WriteLine("Could not read " + FileName + ": " + ex.Message);
      }
      return result;
    }

    public static Dictionary<ulong, string> LoadNames() {
      return LoadEntries()
        .Where(kvp => !String.IsNullOrWhiteSpace(kvp.Value.Name))
        .ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Name);
    }

    public static void Synchronize(IEnumerable<(ulong Id, string InternalName, string Category, string Group)> detected) {
      if (detected == null) return;
      try {
        XDocument doc = LoadDocument(RuntimePath);
        XElement root = doc.Root;
        if (root == null || !String.Equals(root.Name.LocalName, "AreaNames", StringComparison.OrdinalIgnoreCase)) {
          root = CreateDocument().Root;
          doc = new XDocument(root);
        }

        var existing = new Dictionary<ulong, XElement>();
        foreach (XElement element in root.Elements("Area").ToList()) {
          if (ulong.TryParse((string)element.Attribute("id"), NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong id) && !existing.ContainsKey(id)) existing[id] = element;
        }

        bool changed = false;
        foreach (var item in detected.GroupBy(x => x.Id).Select(x => x.First()).OrderBy(x => x.Id)) {
          string defaultCategory = String.IsNullOrWhiteSpace(item.Category) ? UnassignedCategory : item.Category.Trim();
          string defaultGroup = item.Group?.Trim() ?? String.Empty;
          if (!existing.TryGetValue(item.Id, out XElement element)) {
            element = new XElement("Area",
              new XAttribute("id", item.Id.ToString(CultureInfo.InvariantCulture)),
              new XAttribute("name", String.Empty),
              new XAttribute("internalName", item.InternalName ?? String.Empty),
              new XAttribute("category", defaultCategory),
              new XAttribute("group", defaultGroup));
            root.Add(element);
            existing[item.Id] = element;
            changed = true;
          } else {
            // internalName is informational, so it is safe to refresh it. name/category/group are user-editable
            // and therefore only receive defaults when the attribute is missing or empty.
            string internalName = item.InternalName ?? String.Empty;
            string oldInternal = ((string)element.Attribute("internalName") ?? String.Empty).Trim();
            if (!String.IsNullOrWhiteSpace(internalName) && !String.Equals(oldInternal, internalName, StringComparison.Ordinal)) {
              element.SetAttributeValue("internalName", internalName);
              changed = true;
            }
            if (element.Attribute("name") == null) { element.SetAttributeValue("name", String.Empty); changed = true; }
            string oldCategory = ((string)element.Attribute("category") ?? String.Empty).Trim();
            if (String.IsNullOrWhiteSpace(oldCategory)) { element.SetAttributeValue("category", defaultCategory); changed = true; }
            if (element.Attribute("group") == null) { element.SetAttributeValue("group", defaultGroup); changed = true; }
          }
        }

        // Keep generated files deterministic and pleasant to edit by hand. Category/group are deliberately not
        // used for sorting here so moving an entry between folders does not cause an enormous unrelated XML diff.
        List<XElement> ordered = root.Elements("Area")
          .OrderBy(e => ulong.TryParse((string)e.Attribute("id"), out ulong id) ? id : ulong.MaxValue)
          .ToList();
        if (!root.Elements("Area").SequenceEqual(ordered)) {
          foreach (XElement e in ordered) e.Remove();
          root.Add(ordered);
          changed = true;
        }

        if (changed || !File.Exists(RuntimePath)) SaveDocument(doc, RuntimePath);
      } catch (Exception ex) {
        System.Diagnostics.Debug.WriteLine("Could not update " + FileName + ": " + ex.Message);
      }
    }

    private static XDocument LoadDocument(string path) {
      if (!File.Exists(path)) return CreateDocument();
      try { return XDocument.Load(path); }
      catch { return CreateDocument(); }
    }

    private static XDocument CreateDocument() {
      return new XDocument(
        new XDeclaration("1.0", "utf-8", null),
        new XElement("AreaNames",
          new XComment("PugTools World Browser metadata. Missing area IDs are added automatically. Set name=\"...\" to override the display name. category/group control the tree folder; use category=\"Unassigned\" and group=\"\" for the catch-all folder. internalName is informational and may be refreshed automatically.")));
    }

    private static void SaveDocument(XDocument doc, string path) {
      string directory = Path.GetDirectoryName(path);
      if (!String.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
      var settings = new XmlWriterSettings { Indent = true, IndentChars = "  ", NewLineChars = Environment.NewLine, NewLineHandling = NewLineHandling.Replace, Encoding = new System.Text.UTF8Encoding(false) };
      string temp = path + ".tmp";
      using (XmlWriter writer = XmlWriter.Create(temp, settings)) doc.Save(writer);
      try {
        if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
      } catch {
        File.Copy(temp, path, true);
        File.Delete(temp);
      }
    }
  }
}
