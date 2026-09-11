using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using GomLib;

namespace PugTools {
  internal partial class NodeBrowser {
    /// <summary>
    /// Builds a database-free view of the prototype's DOM class composition. The information is
    /// already present in client.gom and in the node's GLOM header; loading the node resolves the
    /// GLOM class IDs, so no external index or build-history database is required.
    /// </summary>
    private static NodeListItem BuildPrototypeStructureItem(GomObject obj) {
      if (obj == null) return null;

      try { _ = obj.Data; } catch { }

      NodeListItem root = new NodeListItem("Prototype structure", String.Empty);
      root.children.Add(new NodeListItem("Prototype ID", FormatDomId(obj.Id)));
      root.children.Add(new NodeListItem("Instance type", obj.InstanceType.ToString(CultureInfo.InvariantCulture)));
      root.children.Add(new NodeListItem("Serialized fields", obj.NumFields.ToString(CultureInfo.InvariantCulture)));
      root.children.Add(new NodeListItem("GLOM class count", obj.NumGlommed.ToString(CultureInfo.InvariantCulture)));

      if (obj.DomClass != null) {
        NodeListItem baseClass = BuildDomClassItem("Base class", obj.DomClass, new HashSet<UInt64>(), 0);
        if (baseClass != null) root.children.Add(baseClass);
      }

      List<DomClass> glommed = (obj.GlommedClasses ?? new List<DomClass>())
        .Where(x => x != null)
        .GroupBy(x => x.Id)
        .Select(x => x.First())
        .ToList();
      NodeListItem glomRoot = new NodeListItem("GLOM classes", glommed.Count.ToString(CultureInfo.InvariantCulture));
      foreach (DomClass glom in glommed) {
        NodeListItem item = BuildDomClassItem("GLOM", glom, new HashSet<UInt64>(), 0);
        if (item != null) glomRoot.children.Add(item);
      }
      root.children.Add(glomRoot);

      // Show the effective field set as a compact cross-check. This is particularly useful for
      // nodes where the same field is provided by a component or a dynamically glommed class.
      Dictionary<UInt64, String> effectiveFields = new Dictionary<UInt64, String>();
      AddEffectiveFields(obj.DomClass, effectiveFields, new HashSet<UInt64>());
      foreach (DomClass glom in glommed) AddEffectiveFields(glom, effectiveFields, new HashSet<UInt64>());
      NodeListItem effective = new NodeListItem("Effective DOM fields", effectiveFields.Count.ToString(CultureInfo.InvariantCulture));
      foreach (KeyValuePair<UInt64, String> field in effectiveFields.OrderBy(x => x.Value, StringComparer.OrdinalIgnoreCase))
        effective.children.Add(new NodeListItem(field.Value, FormatDomId(field.Key)) {
          FieldId = field.Key.ToString(CultureInfo.InvariantCulture)
        });
      root.children.Add(effective);

      return root;
    }

    private static NodeListItem BuildDomClassItem(String role, DomClass domClass, HashSet<UInt64> path, Int32 depth) {
      if (domClass == null) return null;
      String title = role + ": " + (String.IsNullOrWhiteSpace(domClass.Name) ? "<unnamed>" : domClass.Name);
      NodeListItem item = new NodeListItem(title, FormatDomId(domClass.Id)) { NavigationTarget = domClass };
      item.children.Add(new NodeListItem("Archetype", domClass.Archetype.ToString(CultureInfo.InvariantCulture)
        + " (0x" + domClass.Archetype.ToString("X4", CultureInfo.InvariantCulture) + ")"));
      if (domClass.ScriptMethodId1 != 0 || domClass.ScriptMethodId2 != 0) {
        item.children.Add(new NodeListItem("Script method ID 1", FormatDomId(domClass.ScriptMethodId1)));
        item.children.Add(new NodeListItem("Script method ID 2", FormatDomId(domClass.ScriptMethodId2)));
      }
      if (depth >= 12 || !path.Add(domClass.Id)) {
        item.children.Add(new NodeListItem("Composition", depth >= 12 ? "depth limit" : "recursive reference"));
        return item;
      }

      if (!String.IsNullOrWhiteSpace(domClass.Description))
        item.children.Add(new NodeListItem("Description", domClass.Description));

      List<DomField> fields = domClass.Fields?.Where(x => x != null).ToList() ?? new List<DomField>();
      NodeListItem fieldRoot = new NodeListItem("Fields", fields.Count.ToString(CultureInfo.InvariantCulture));
      foreach (DomField field in fields.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)) {
        String type = field.GomType?.ToString() ?? "unknown";
        NodeListItem fieldItem = new NodeListItem(field.Name ?? FormatDomId(field.Id), type + "   " + FormatDomId(field.Id)) {
          NavigationTarget = field,
          FieldId = field.Id.ToString(CultureInfo.InvariantCulture)
        };
        fieldItem.children.Add(new NodeListItem("Modifiers", field.Modifiers.ToString(CultureInfo.InvariantCulture)
          + " (0x" + field.Modifiers.ToString("X4", CultureInfo.InvariantCulture) + ")"));
        fieldItem.children.Add(new NodeListItem("Serialized type length", field.SerializedTypeLength.ToString(CultureInfo.InvariantCulture)));
        fieldItem.children.Add(new NodeListItem("Serialized type offset", "0x" + field.SerializedTypeOffset.ToString("X4", CultureInfo.InvariantCulture)));
        if (!String.IsNullOrWhiteSpace(field.Description))
          fieldItem.children.Add(new NodeListItem("Description", field.Description));
        fieldRoot.children.Add(fieldItem);
      }
      item.children.Add(fieldRoot);

      List<DomClass> components = domClass.Components?.Where(x => x != null).ToList() ?? new List<DomClass>();
      NodeListItem componentRoot = new NodeListItem("Components", components.Count.ToString(CultureInfo.InvariantCulture));
      foreach (DomClass component in components.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)) {
        HashSet<UInt64> childPath = new HashSet<UInt64>(path);
        NodeListItem child = BuildDomClassItem("Component", component, childPath, depth + 1);
        if (child != null) componentRoot.children.Add(child);
      }
      item.children.Add(componentRoot);
      return item;
    }

    private static void AddEffectiveFields(DomClass domClass, IDictionary<UInt64, String> fields, HashSet<UInt64> seen) {
      if (domClass == null || fields == null || seen == null || !seen.Add(domClass.Id)) return;
      if (domClass.Fields != null) {
        foreach (DomField field in domClass.Fields) {
          if (field == null || fields.ContainsKey(field.Id)) continue;
          fields[field.Id] = String.IsNullOrWhiteSpace(field.Name) ? FormatDomId(field.Id) : field.Name;
        }
      }
      if (domClass.Components != null)
        foreach (DomClass component in domClass.Components) AddEffectiveFields(component, fields, seen);
    }

    private static String FormatDomId(UInt64 id) => id.ToString(CultureInfo.InvariantCulture)
      + " (0x" + id.ToString("X16", CultureInfo.InvariantCulture) + ")";
  }
}
