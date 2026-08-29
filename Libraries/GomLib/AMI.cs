using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;

using File = TorArchive.File;

namespace GomLib {
  public class AMI : Models.GameObject, IEquatable<AMI> {
    [Newtonsoft.Json.JsonIgnore]
    private Dictionary<string, AMI> fqnMap;
    private HashSet<string> legacyMisses;
    public Dictionary<long, AMIEntry> data;
    bool loaded = false;

    private static readonly Dictionary<string, string> LegacySlotNames =
      new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
        { "1", "age" }, { "2", "boot" }, { "3", "bracer" }, { "4", "chest" },
        { "5", "complexion" }, { "6", "creature" }, { "7", "eyecolor" }, { "8", "face" },
        { "9", "facehair" }, { "10", "facepaint" }, { "11", "hair" }, { "12", "haircolor" },
        { "13", "hand" }, { "14", "head" }, { "15", "leg" }, { "16", "skincolor" },
        { "17", "waist" }, { "19", "garmenthue" }, { "20", "colorscheme" }
      };

    public AMI(DataObjectModel dom) {
      Dom_ = dom;
      Flush();
    }

    public void Flush() {
      fqnMap = new Dictionary<string, AMI>(StringComparer.OrdinalIgnoreCase);
      legacyMisses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      data = new Dictionary<long, AMIEntry>();
      loaded = false;
    }

    public AMIEntry Find(string fqn, long id) {
      if (string.IsNullOrEmpty(fqn)) return null;
      if (!loaded) Load();

      string category = NormalizeCategory(fqn);
      string tableFqn = "ami." + category;
      if (!fqnMap.TryGetValue(tableFqn, out AMI table) || table?.data == null || table.data.Count == 0) {
        table = TryLoadLegacyTable(category);
      }

      return table?.GetEntry(id);
    }

    public AMIEntry GetEntry(long id) {
      if (!loaded) Load();
      return data != null && data.TryGetValue(id, out AMIEntry result) ? result : null;
    }

    public AMI GetAMI(string fqn) {
      if (!loaded) Load();
      if (String.IsNullOrWhiteSpace(fqn)) return null;

      string tableFqn = fqn.StartsWith("ami.", StringComparison.OrdinalIgnoreCase)
        ? fqn
        : "ami." + NormalizeCategory(fqn);
      if (fqnMap.TryGetValue(tableFqn, out AMI result) && result?.data?.Count > 0) return result;
      return TryLoadLegacyTable(NormalizeCategory(tableFqn));
    }

    public void Load() {
      if (loaded) return;
      var amis = Dom_.GetObjectsStartingWith("ami.");

      foreach (var ami in amis) {
        AMI table = new AMI(Dom_) {
          Fqn = ami.Name,
          Id = ami.Id
        };
        // appModelDetails was renamed to appModelsById in newer GOM data.
        // Keep the old name as a fallback so older clients still work.
        Dictionary<object, object> entries =
          ami.Data.ValueOrDefault<Dictionary<object, object>>("appModelsById", null)
          ?? ami.Data.ValueOrDefault<Dictionary<object, object>>("appModelDetails", null);

        table.data = new Dictionary<long, AMIEntry>();
        if (entries != null) {
          foreach (var entry in entries) {
            long entryId;
            try { entryId = Convert.ToInt64(entry.Key); }
            catch { continue; }

            GomObjectData entryData = entry.Value as GomObjectData;
            if (entryData == null && entry.Value is IEnumerable<object> wrappedValues) {
              foreach (object wrappedValue in wrappedValues) {
                entryData = wrappedValue as GomObjectData;
                if (entryData != null) break;
              }
            }
            if (entryData == null) continue;

            AMIEntry ame = new AMIEntry();
            ame.Load(entryData);
            if (ame.Id == 0) ame.Id = entryId;
            table.data[entryId] = ame;
          }
        }

        // Some current data can expose the same AMI FQN more than once.
        // Last valid table wins instead of terminating the browser.
        table.loaded = true;
        fqnMap[table.Fqn] = table;
      }

      // A pre-2.1 client legitimately has no ami.* GOM objects at all: its equivalent data is
      // /resources/art/dynamic/<slot>/index.xml. Mark the GOM scan complete anyway and let Find()
      // lazily resolve only the historical slot actually requested, mirroring Jedipedia's appModelIndex.
      loaded = true;
    }

    private static string NormalizeCategory(string fqn) {
      string value = (fqn ?? String.Empty).Trim();
      if (value.StartsWith("ami.", StringComparison.OrdinalIgnoreCase)) value = value.Substring(4);
      if (value.StartsWith("appSlot", StringComparison.OrdinalIgnoreCase)) value = value.Substring(7);
      return value.Trim().ToLowerInvariant();
    }

    private AMI TryLoadLegacyTable(string category) {
      category = NormalizeCategory(category);
      if (String.IsNullOrWhiteSpace(category) || Dom_?.Assets == null) return null;
      string tableFqn = "ami." + category;
      if (fqnMap.TryGetValue(tableFqn, out AMI existing) && existing?.data?.Count > 0) return existing;

      string path = "/resources/art/dynamic/" + category + "/index.xml";
      File file = Dom_.Assets.FindFile(path);
      if (file == null) return existing;

      try {
        string xmlText;
        using (file)
        using (Stream stream = file.OpenCopyInMemory()) {
          xmlText = ReadLegacyXmlText(stream);
        }
        AMI parsed = ParseLegacyTable(category, xmlText);
        if (parsed != null && parsed.data.Count > 0) {
          fqnMap[tableFqn] = parsed;
          legacyMisses.Remove(category);
          return parsed;
        }
      }
      catch (Exception ex) {
        System.Diagnostics.Debug.WriteLine("Historical appearance index failed " + path + ": " + ex.Message);
      }

      return existing;
    }

    private static string ReadLegacyXmlText(Stream stream) {
      if (stream == null) return String.Empty;
      using var copy = new MemoryStream();
      stream.CopyTo(copy);
      byte[] bytes = copy.ToArray();
      if (bytes.Length == 0) return String.Empty;

      if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
      if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
      if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);

      // Several beta indexes are UTF-16LE without a BOM. Jedipedia detects that form by the
      // zero byte in the first XML token; use the same practical distinction here.
      bool looksUtf16Le = bytes.Length >= 4 && bytes[1] == 0 && bytes[3] == 0;
      return (looksUtf16Le ? Encoding.Unicode : Encoding.UTF8).GetString(bytes);
    }

    private AMI ParseLegacyTable(string category, string xmlText) {
      if (String.IsNullOrWhiteSpace(xmlText)) return null;
      XDocument document = XDocument.Parse(xmlText, LoadOptions.None);
      var table = new AMI(Dom_) {
        Fqn = "ami." + category,
        data = new Dictionary<long, AMIEntry>()
      };

      foreach (XElement asset in document.Descendants().Where(e => e.Name.LocalName.Equals("Asset", StringComparison.OrdinalIgnoreCase))) {
        XElement idElement = asset.Elements().FirstOrDefault(e => e.Name.LocalName.Equals("ID", StringComparison.OrdinalIgnoreCase));
        if (idElement == null || !Int64.TryParse(idElement.Value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long id)) continue;

        var entry = new AMIEntry {
          Id = id,
          BaseFile = ChildValue(asset, "BaseFile") ?? String.Empty,
          Attachments = new Dictionary<long, string>(),
          MaterialList = new Dictionary<long, Dictionary<long, string>>(),
          ChildSkinMaterials = new Dictionary<string, string>(),
          SkinMaterialIndex = ParseLong(ChildValue(asset, "SkinMaterialIndex"), 0),
          SkinHueIndex = ParseLong(ChildValue(asset, "SkinHueIndex"), 0),
          SlotType = "appSlot" + LegacySlotDisplayName(category)
        };

        foreach (XElement attachment in asset.Descendants().Where(e => e.Name.LocalName.Equals("Attachment", StringComparison.OrdinalIgnoreCase))) {
          if (TryLongAttribute(attachment, "id", out long attachmentId)) {
            string filename = AttributeValue(attachment, "filename");
            if (!String.IsNullOrWhiteSpace(filename)) entry.Attachments[attachmentId] = filename;
          }
        }

        foreach (XElement material in asset.Descendants().Where(e => e.Name.LocalName.Equals("Material", StringComparison.OrdinalIgnoreCase))) {
          if (!TryLongAttribute(material, "id", out long materialId)) continue;
          var bySubmesh = new Dictionary<long, string>();
          string baseMaterial = LegacyMaterialName(AttributeValue(material, "filename"));
          if (!String.IsNullOrWhiteSpace(baseMaterial)) bySubmesh[0] = baseMaterial;
          foreach (XElement ov in material.Descendants().Where(e => e.Name.LocalName.Equals("MaterialOverride", StringComparison.OrdinalIgnoreCase))) {
            if (!TryLongAttribute(ov, "index", out long index)) continue;
            string overrideMaterial = LegacyMaterialName(AttributeValue(ov, "filename"));
            if (!String.IsNullOrWhiteSpace(overrideMaterial)) bySubmesh[index] = overrideMaterial;
          }
          if (bySubmesh.Count > 0) entry.MaterialList[materialId] = bySubmesh;
        }

        foreach (XElement skinMaterial in asset.Descendants().Where(e => e.Name.LocalName.Equals("SkinMaterial", StringComparison.OrdinalIgnoreCase))) {
          string rawSlot = AttributeValue(skinMaterial, "slot");
          string slotName = LegacyAppSlotName(rawSlot);
          string material = LegacyMaterialName(AttributeValue(skinMaterial, "filename"));
          if (!String.IsNullOrWhiteSpace(slotName) && !String.IsNullOrWhiteSpace(material)) entry.ChildSkinMaterials[slotName] = material;
        }

        XElement dataElement = asset.Descendants().FirstOrDefault(e => e.Name.LocalName.Equals("Data", StringComparison.OrdinalIgnoreCase));
        string representative = dataElement == null ? null : AttributeValue(dataElement, "RepresentativeColor");
        if (!String.IsNullOrWhiteSpace(representative)) {
          string[] parts = representative.Split(',');
          if (parts.Length >= 3) {
            var color = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
            string[] names = { "r", "g", "b", "a" };
            for (int i = 0; i < Math.Min(4, parts.Length); i++)
              if (Single.TryParse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float component)) color[names[i]] = component;
            if (color.Count > 0) entry.RepresentativeColor = color;
          }
        }

        table.data[id] = entry;
      }
      table.loaded = true;
      return table;
    }

    private static string ChildValue(XElement parent, string localName) =>
      parent?.Elements().FirstOrDefault(e => e.Name.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase))?.Value?.Trim();

    private static string AttributeValue(XElement element, string name) =>
      element?.Attributes().FirstOrDefault(a => a.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value?.Trim();

    private static long ParseLong(string text, long fallback) =>
      Int64.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value) ? value : fallback;

    private static bool TryLongAttribute(XElement element, string name, out long value) =>
      Int64.TryParse(AttributeValue(element, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    private static string LegacyMaterialName(string filename) {
      if (String.IsNullOrWhiteSpace(filename)) return String.Empty;
      string normalized = filename.Trim().Replace('\\', '/');
      int slash = normalized.LastIndexOf('/');
      if (slash >= 0 && slash + 1 < normalized.Length) normalized = normalized.Substring(slash + 1);
      if (normalized.EndsWith(".mat", StringComparison.OrdinalIgnoreCase)) normalized = normalized.Substring(0, normalized.Length - 4);
      return normalized.ToLowerInvariant();
    }

    private static string LegacySlotDisplayName(string category) {
      string clean = NormalizeCategory(category);
      switch (clean) {
        case "facehair": return "FaceHair";
        case "eyecolor": return "EyeColor";
        case "facepaint": return "FacePaint";
        case "haircolor": return "HairColor";
        case "skincolor": return "SkinColor";
        case "garmenthue": return "GarmentHue";
        case "colorscheme": return "ColorScheme";
        default: return clean.Length == 0 ? String.Empty : Char.ToUpperInvariant(clean[0]) + clean.Substring(1);
      }
    }

    private static string LegacyAppSlotName(string rawSlot) {
      if (String.IsNullOrWhiteSpace(rawSlot)) return null;
      string clean = rawSlot.Trim();
      if (LegacySlotNames.TryGetValue(clean, out string numericName)) clean = numericName;
      return "appSlot" + LegacySlotDisplayName(clean);
    }

    public void UnLoad() {
      fqnMap = null;
      legacyMisses = null;
      data = null;
      Fqn = null;
      loaded = false;
    }

    public override int GetHashCode() {
      int hash = Id.GetHashCode();
      if (Fqn != null) hash ^= Fqn.GetHashCode();
      if (data != null) foreach (var x in data) { hash ^= x.GetHashCode(); }
      return hash;
    }

    public override bool Equals(object obj) {
      if (obj == null) return false;
      if (ReferenceEquals(this, obj)) return true;
      if (obj is not AMI stb) return false;
      return Equals(stb);
    }

    public bool Equals(AMI stb) {
      if (stb == null) return false;
      if (ReferenceEquals(this, stb)) return true;
      if (Fqn != stb.Fqn) return false;
      var ssComp = new Models.DictionaryComparer<long, AMIEntry>();
      if (!ssComp.Equals(data, stb.data)) return false;
      return true;
    }
  }
}
