using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;

namespace PugTools {
  /// <summary>
  /// Parsed view for SWTOR Animation Actor Model XML (am_*.xml / root &lt;aam&gt;).
  /// Jedipedia treats AAM as the human-readable companion to Morpheme MPH networks:
  /// inputs, network-selection literals, actions, events, tracks, switches and masks.
  /// </summary>
  internal static class ViewAAM {
    private static readonly String[] SectionOrder = {
      "inputs", "networks", "actions", "events", "tracks", "switches", "masks"
    };

    private static readonly Dictionary<String, String> SectionDescriptions =
      new Dictionary<String, String>(StringComparer.OrdinalIgnoreCase) {
        { "inputs", "Control-parameter inputs that drive the Morpheme network." },
        { "networks", "Runtime selection table linking symbolic slots to compiled MPH networks." },
        { "actions", "Playable actor actions and their animation/network controls." },
        { "events", "Animation event channels used by clips and the MPH discrete-event data." },
        { "tracks", "Named synchronization / footstep event tracks." },
        { "switches", "Overlay switch declarations such as mood, aim or cinematic layers." },
        { "masks", "Named bone-mask selection rules." }
      };

    internal static ArrayList Parse(String text, String sourcePath) {
      if (String.IsNullOrWhiteSpace(text)) throw new InvalidDataException("AAM document is empty.");

      XmlDocument doc = new XmlDocument();
      doc.LoadXml(text.TrimStart('\uFEFF', ' ', '\t', '\r', '\n'));
      XmlElement root = doc.DocumentElement;
      if (root == null || !String.Equals(root.LocalName, "aam", StringComparison.OrdinalIgnoreCase))
        throw new InvalidDataException("XML root is not <aam>.");

      var roots = new ArrayList();
      XmlElement header = Child(root, "header");

      Int32 inputCount = CountDirectChildren(Child(root, "inputs"));
      Int32 actionCount = CountDirectChildren(Child(root, "actions"));
      Int32 eventCount = CountDirectChildren(Child(root, "events"));
      Int32 trackCount = CountDirectChildren(Child(root, "tracks"));
      Int32 switchCount = CountDirectChildren(Child(root, "switches"));
      Int32 maskCount = CountDirectChildren(Child(root, "masks"));
      Int32 networkLiteralCount = Descendants(Child(root, "networks"), "literal").Count;

      NodeListItem overview = new NodeListItem("Animation Actor Model", sourcePath ?? String.Empty);
      if (header != null) {
        AddAttributeIfPresent(overview, header, "NetworkType");
        AddAttributeIfPresent(overview, header, "BodyType");
        AddAttributeIfPresent(overview, header, "DefaultMask");
        AddAttributeIfPresent(overview, header, "blendInTime");
        AddAttributeIfPresent(overview, header, "blendOutTime");
        AddAttributeIfPresent(overview, header, "expirationDuration");
        AddAttributeIfPresent(overview, header, "DoubleActionPath");
      }
      overview.children.Add(new NodeListItem("Inputs", inputCount));
      overview.children.Add(new NodeListItem("Network literals", networkLiteralCount));
      overview.children.Add(new NodeListItem("Actions", actionCount));
      overview.children.Add(new NodeListItem("Events", eventCount));
      overview.children.Add(new NodeListItem("Tracks", trackCount));
      overview.children.Add(new NodeListItem("Switches", switchCount));
      overview.children.Add(new NodeListItem("Masks", maskCount));
      roots.Add(overview);

      if (header != null) roots.Add(BuildElementBranch(header, "Header"));

      foreach (String sectionName in SectionOrder) {
        XmlElement section = Child(root, sectionName);
        if (section == null) continue;
        Int32 count = CountDirectChildren(section);
        String label = Char.ToUpperInvariant(sectionName[0]) + sectionName.Substring(1);
        String description = SectionDescriptions.TryGetValue(sectionName, out String found)
          ? found
          : count + " entries";
        NodeListItem sectionBranch = new NodeListItem(label + " (" + count.ToString("N0") + ")", description);
        foreach (XmlNode child in section.ChildNodes) {
          if (child is XmlElement element) sectionBranch.children.Add(BuildElementBranch(element, null));
        }
        roots.Add(sectionBranch);
      }

      List<String> mphPaths = BuildMphReferences(root, sourcePath);
      if (mphPaths.Count > 0) {
        NodeListItem refs = new NodeListItem("Referenced MPH networks", mphPaths.Count + " unique files");
        foreach (String path in mphPaths) {
          NodeListItem item = new NodeListItem(path, "Morpheme network");
          item.children.Add(new NodeListItem("AMX companion candidate", path + ".amx"));
          refs.children.Add(item);
        }
        roots.Add(refs);
      }

      List<String> animationNames = Descendants(root, "action")
        .Select(x => x.GetAttribute("animName"))
        .Where(x => !String.IsNullOrWhiteSpace(x))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
        .ToList();
      if (animationNames.Count > 0) {
        NodeListItem animations = new NodeListItem("Action animation names", animationNames.Count + " unique names");
        foreach (String name in animationNames) animations.children.Add(new NodeListItem(name, "JBA/action name"));
        roots.Add(animations);
      }

      return roots;
    }

    private static NodeListItem BuildElementBranch(XmlElement element, String displayOverride) {
      String display = !String.IsNullOrWhiteSpace(displayOverride) ? displayOverride : ElementDisplayName(element);
      String summary = AttributeSummary(element);
      NodeListItem branch = new NodeListItem(display, summary);

      if (element.HasAttributes) {
        NodeListItem attrs = new NodeListItem("Attributes", element.Attributes.Count + " entries");
        foreach (XmlAttribute attr in element.Attributes)
          attrs.children.Add(new NodeListItem(attr.Name, attr.Value));
        branch.children.Add(attrs);
      }

      String directText = String.Concat(
        element.ChildNodes.Cast<XmlNode>()
          .Where(x => x.NodeType == XmlNodeType.Text || x.NodeType == XmlNodeType.CDATA)
          .Select(x => x.Value)
      ).Trim();
      if (!String.IsNullOrWhiteSpace(directText)) branch.children.Add(new NodeListItem("Text", directText));

      foreach (XmlNode node in element.ChildNodes)
        if (node is XmlElement child) branch.children.Add(BuildElementBranch(child, null));
      return branch;
    }

    private static String ElementDisplayName(XmlElement element) {
      String name = element.LocalName;
      String identity = FirstNonEmptyAttribute(element, "name", "id", "fqn", "actionProvider", "nodename", "maskname", "path", "value");
      return String.IsNullOrWhiteSpace(identity) ? name : name + "  " + identity;
    }

    private static String AttributeSummary(XmlElement element) {
      String[] preferred = { "fqn", "name", "id", "value", "actionProvider", "animName", "path", "mask", "request" };
      var parts = new List<String>();
      foreach (String key in preferred) {
        if (!element.HasAttribute(key)) continue;
        parts.Add(key + "=" + element.GetAttribute(key));
        if (parts.Count >= 3) break;
      }
      return String.Join(" | ", parts);
    }

    private static String FirstNonEmptyAttribute(XmlElement element, params String[] names) {
      foreach (String name in names) {
        String value = element.GetAttribute(name);
        if (!String.IsNullOrWhiteSpace(value)) return value;
      }
      return String.Empty;
    }

    private static void AddAttributeIfPresent(NodeListItem node, XmlElement element, String attribute) {
      if (element.HasAttribute(attribute)) node.children.Add(new NodeListItem(attribute, element.GetAttribute(attribute)));
    }

    private static XmlElement Child(XmlElement parent, String localName) {
      if (parent == null) return null;
      foreach (XmlNode node in parent.ChildNodes)
        if (node is XmlElement element && String.Equals(element.LocalName, localName, StringComparison.OrdinalIgnoreCase))
          return element;
      return null;
    }

    private static Int32 CountDirectChildren(XmlElement element) {
      if (element == null) return 0;
      Int32 count = 0;
      foreach (XmlNode node in element.ChildNodes) if (node is XmlElement) count++;
      return count;
    }

    private static List<XmlElement> Descendants(XmlElement parent, String localName) {
      var result = new List<XmlElement>();
      if (parent == null) return result;
      foreach (XmlNode node in parent.GetElementsByTagName("*")) {
        if (node is XmlElement element && String.Equals(element.LocalName, localName, StringComparison.OrdinalIgnoreCase))
          result.Add(element);
      }
      return result;
    }

    private static List<String> BuildMphReferences(XmlElement root, String sourcePath) {
      String directory = String.Empty;
      if (!String.IsNullOrWhiteSpace(sourcePath)) {
        String normalized = sourcePath.Replace('\\', '/');
        Int32 slash = normalized.LastIndexOf('/');
        if (slash >= 0) directory = normalized.Substring(0, slash + 1);
      }

      var paths = new HashSet<String>(StringComparer.OrdinalIgnoreCase);
      foreach (XmlElement literal in Descendants(root, "literal")) {
        String fqn = literal.GetAttribute("fqn").Trim().Replace('\\', '/');
        if (String.IsNullOrWhiteSpace(fqn)) continue;
        String path = fqn;
        if (!Path.HasExtension(path)) path += ".mph";
        if (!path.StartsWith("/resources/", StringComparison.OrdinalIgnoreCase)) path = directory + path.TrimStart('/');
        path = NormalizeResourcePath(path);
        if (!String.IsNullOrWhiteSpace(path)) paths.Add(path);
      }
      return paths.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static String NormalizeResourcePath(String path) {
      if (String.IsNullOrWhiteSpace(path)) return String.Empty;
      String result = path.Trim().Replace('\\', '/');
      while (result.Contains("//")) result = result.Replace("//", "/");
      if (!result.StartsWith("/", StringComparison.Ordinal)) result = "/" + result;
      return result.ToLowerInvariant();
    }
  }
}
