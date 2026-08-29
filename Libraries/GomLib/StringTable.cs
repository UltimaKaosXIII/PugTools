using System;
using System.Collections.Generic;
using System.Globalization;
using System.Xml.Linq;
// using System.Diagnostics;

namespace GomLib {
  public class StringTable : IEquatable<StringTable> {
    // Selected extraction/UI localization. Values use the same keys as StringTableEntry.
    public static string SelectedLocalization { get; set; } = "enMale";
    public static string SelectedLocale { get; set; } = "en-us";

    private Dictionary<string, StringTable> fqnMap;
    public Dictionary<string, string> failedFqns;
    readonly DataObjectModel Dom_;

    public StringTable(DataObjectModel dom) {
      Dom_ = dom;
      Flush();
    }

    public void Flush() {
      fqnMap = new Dictionary<string, StringTable>();
      failedFqns = new Dictionary<string, string>();
    }

    public StringTable Find(string fqn) {
      if (string.IsNullOrEmpty(fqn)) { return null; }

      if (failedFqns.ContainsKey(fqn)) { return null; }

      if (fqnMap.TryGetValue(fqn, out StringTable result)) {
        return result;
      }

      result = new StringTable(fqn, Dom_);
      if (result.StbFileExists()) {
        try {
          result.Load();
          fqnMap.Add(fqn, result);
          return result;
        }
        catch (Exception ex) {
          failedFqns.Add(fqn, ex.Message);
          return null;
        }
      }

      return null;
    }

    public Dictionary<long, StringTableEntry> data;
    public string Fqn { get; private set; }
    public int Version { get; private set; }
    public long Guid { get; private set; }
    public string OwnerFqn { get; private set; }
    public long OwnerId { get; private set; }

    private StringTable(string fqn, DataObjectModel dom) {
      Dom_ = dom;
      Fqn = fqn;
    }

    public StringTableEntry GetEntry(long id) {
      if (data.TryGetValue(id, out StringTableEntry result)) {
        return result;
      }

      return null;
    }

    public string GetText(long id, string forFqn) {
      return GetText(id, forFqn, SelectedLocalization);
    }

    public string GetText(long id, string forFqn, string localization) {
      if (forFqn == null) {
        throw new ArgumentNullException(nameof(forFqn));
      }

      if (data.TryGetValue(id, out StringTableEntry entry)) {
        if (entry.LocalizedText.ContainsKey(localization)) {
          return entry.LocalizedText[localization];
        } else return "";
      }

      //Debug.WriteLine("Cannot find String {0} in StringTable {1} for {2}", id, this.Fqn, forFqn);
      return string.Empty;
    }

    public Dictionary<string, string> GetLocalizedText(long id, string _) {
      if (data.TryGetValue(id, out StringTableEntry entry)) {
        if (entry.LocalizedText.Count > 0) {
          return entry.LocalizedText;
        } else return null;
      }

      //Debug.WriteLine("Cannot find String {0} in StringTable {1} for {2}", id, this.Fqn, forFqn);
      return null;
    }

    public Dictionary<string, string> GetOptionText(long id, string _) {
      if (data.TryGetValue(id, out StringTableEntry entry)) {
        if (entry.HasOptionText) {
          return entry.OptionText;
        } else return null;
      }

      //Debug.WriteLine("Cannot find String {0} in StringTable {1} for {2}", id, this.Fqn, forFqn);
      return null;
    }

    public bool StbFileExists() {
      foreach (string localization in StringTableLocalizations()) {
        string basePath = "/resources/" + localization + "/" + Fqn.Replace('.', '/');
        if (Dom_.Assets.HasFile(basePath + ".stb") || Dom_.Assets.HasFile(basePath + ".str"))
          return true;
      }

      return false;
    }

    private static IEnumerable<string> StringTableLocalizations() {
      yield return "en-us";
      yield return "fr-fr";
      yield return "de-de";
    }

    private static Dictionary<string, string> EmptyLocalizedText() {
      return new Dictionary<string, string> {
        { "enMale", "" },
        { "enFemale", "" },
        { "frMale", "" },
        { "frFemale", "" },
        { "deMale", "" },
        { "deFemale", "" },
      };
    }

    private StringTableEntry EnsureEntry(long entryId) {
      if (!data.TryGetValue(entryId, out StringTableEntry entry)) {
        entry = new StringTableEntry {
          Id = entryId,
          LocalizedText = EmptyLocalizedText(),
          OptionText = EmptyLocalizedText()
        };
        data[entryId] = entry;
      }
      return entry;
    }

    private void Load() {
      bool foundAtLeastOneTable = false;
      data = new Dictionary<long, StringTableEntry>();

      foreach (string localization in StringTableLocalizations()) {
        string basePath = "/resources/" + localization + "/" + Fqn.Replace('.', '/');

        var stbFile = Dom_.Assets.FindFile(basePath + ".stb");
        if (stbFile != null) {
          foundAtLeastOneTable = true;
          LoadBinaryStb(localization, stbFile);
          continue;
        }

        // Early BLUE beta string tables are XML .str resources. They predate both
        // stb.manifest and the binary STB format used by RED/later clients.
        var strFile = Dom_.Assets.FindFile(basePath + ".str");
        if (strFile != null) {
          foundAtLeastOneTable = true;
          LoadLegacyStr(localization, strFile);
        }
      }

      if (!foundAtLeastOneTable) throw new Exception("File not found");
    }

    private void LoadBinaryStb(string localization, TorArchive.File file) {
      using var fs = file.OpenCopyInMemory();
      var br = new GomBinaryReader(fs, Dom_);
      br.ReadBytes(3);
      int numStrings = br.ReadInt32();

      for (var i = 0; i < numStrings; i++) {
        long entryId = br.ReadInt64();
        byte entryType = br.ReadByte();
        _ = br.ReadByte();
        _ = br.ReadSingle();
        int entryLength = br.ReadInt32();
        int entryOffset = br.ReadInt32();
        _ = br.ReadInt32();

        string text = "";
        if (entryLength > 0) {
          long streamPos = fs.Position;
          fs.Position = entryOffset;
          text = br.ReadFixedLengthString(entryLength);
          fs.Position = streamPos;
        }

        StringTableEntry entry = EnsureEntry(entryId);
        if (text.Length == 0) continue;

        string textTag = localization switch {
          "en-us" => (entryType == 65 || entryType == 80) ? "enMale" : "enFemale",
          "de-de" => (entryType == 65 || entryType == 80) ? "deMale" : "deFemale",
          "fr-fr" => (entryType == 65 || entryType == 80) ? "frMale" : "frFemale",
          _ => null
        };
        if (textTag == null) continue;

        if (entryType == 65 || entryType == 70) {
          entry.LocalizedText[textTag] = text;
        } else if (entryType == 80 || entryType == 81) {
          entry.HasOptionText = true;
          entry.OptionText[textTag] = text;
        }
      }
    }

    private void LoadLegacyStr(string localization, TorArchive.File file) {
      using var fs = file.OpenCopyInMemory();
      XDocument doc = XDocument.Load(fs, LoadOptions.None);
      XElement root = doc.Root;
      if (root == null) return;

      if (Version == 0 && Int32.TryParse(root.Attribute("version")?.Value, NumberStyles.Integer,
        CultureInfo.InvariantCulture, out int parsedVersion)) Version = parsedVersion;
      if (String.IsNullOrWhiteSpace(OwnerFqn)) OwnerFqn = root.Attribute("owner")?.Value;
      if (OwnerId == 0) OwnerId = ParseLegacyInt64(root.Attribute("ownerID")?.Value);
      if (Guid == 0) Guid = ParseLegacyInt64(root.Attribute("GUID")?.Value);

      foreach (XElement row in root.Descendants("string")) {
        XAttribute idAttribute = row.Attribute("id");
        if (idAttribute == null || String.IsNullOrWhiteSpace(idAttribute.Value)) continue;
        long entryId = ParseLegacyInt64(idAttribute.Value);
        StringTableEntry entry = EnsureEntry(entryId);

        string male = row.Element("text")?.Value ?? String.Empty;
        string female = row.Element("textFemale")?.Value;
        if (String.IsNullOrEmpty(female)) female = male;

        switch (localization) {
          case "en-us":
            entry.LocalizedText["enMale"] = male;
            entry.LocalizedText["enFemale"] = female;
            break;
          case "fr-fr":
            entry.LocalizedText["frMale"] = male;
            entry.LocalizedText["frFemale"] = female;
            break;
          case "de-de":
            entry.LocalizedText["deMale"] = male;
            entry.LocalizedText["deFemale"] = female;
            break;
        }
      }
    }

    private static long ParseLegacyInt64(string text) {
      if (String.IsNullOrWhiteSpace(text)) return 0;
      text = text.Trim();
      if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
          UInt64.TryParse(text.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong hex))
        return unchecked((long)hex);
      if (Int64.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long signed))
        return signed;
      if (UInt64.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong unsigned))
        return unchecked((long)unsigned);
      return 0;
    }

    //private StringTableEntry LoadString(XElement row)
    //{
    //    StringTableEntry result = new StringTableEntry();
    //    result.Id = row.Attribute("id").AsLong();
    //    result.Text = (string)row.Element("text");
    //    result.TextFemale = (string)row.Element("textFemale");
    //    result.InAlien = row.Element("inAlien").AsBool();
    //    result.DisableVoRecording = row.Element("disableVoRecording").AsBool();
    //    return result;
    //}

    public string TryGetString(string fqn, GomObjectData textRetriever) {
      return TryGetString(fqn, textRetriever, SelectedLocalization);
    }

    public string TryGetString(string fqn, GomObjectData textRetriever, string localization) {
      string locBucket = textRetriever.ValueOrDefault<string>("strLocalizedTextRetrieverBucket", null);
      long strId = textRetriever.ValueOrDefault<long>("strLocalizedTextRetrieverStringID", -1);
      string defaultStr = textRetriever.ValueOrDefault("strLocalizedTextRetrieverDesignModeText", string.Empty);

      if ((locBucket == null) || (strId == -1)) {
        return defaultStr;
      }

      StringTable strTable;
      try {
        strTable = Find(locBucket);
      }
      catch {
        strTable = null;
      }

      if (strTable == null) {
        return defaultStr;
      }

      string result = strTable.GetText(strId, fqn, localization);
      return result ?? defaultStr;
    }

    public string TryGetString(string bucket, long textRetriever) {
      return TryGetString(bucket, textRetriever, SelectedLocalization);
    }

    public string TryGetString(string bucket, long textRetriever, string localization) {
      string locBucket = bucket;
      long strId = textRetriever;
      string defaultStr = null;

      if ((locBucket == null) || (strId == -1)) {
        return defaultStr;
      }

      StringTable strTable;
      try {
        strTable = Find(locBucket);
      }
      catch {
        strTable = null;
      }

      if (strTable == null) {
        return defaultStr;
      }

      string result = strTable.GetText(strId, "", localization);
      return result ?? defaultStr;
    }

    public Dictionary<string, string> TryGetLocalizedStrings(string fqn, GomObjectData textRetriever) {
      string locBucket = textRetriever.ValueOrDefault<string>("strLocalizedTextRetrieverBucket", null);
      long strId = textRetriever.ValueOrDefault<long>("strLocalizedTextRetrieverStringID", -1);
      Dictionary<string, string> defaultStr = new Dictionary<string, string> {
            { "enMale", textRetriever.ValueOrDefault("strLocalizedTextRetrieverDesignModeText", string.Empty) },
            //{ "enFemale", "" },
            { "frMale", "" },
            { "frFemale", "" },
            { "deMale", "" },
            { "deFemale", "" },
            };

      if ((locBucket == null) || (strId == -1)) {
        return defaultStr;
      }

      StringTable strTable;
      try {
        strTable = Find(locBucket);
      }
      catch {
        strTable = null;
      }

      if (strTable == null) {
        return defaultStr;
      }

      Dictionary<string, string> result = strTable.GetLocalizedText(strId, fqn);
      return result ?? defaultStr;
    }

    public Dictionary<string, string> TryGetLocalizedStrings(string bucket, long textRetriever) {
      string locBucket = bucket;
      long strId = textRetriever;

      if ((locBucket == null) || (strId == -1)) {
        return null;
      }

      StringTable strTable;
      try {
        strTable = Find(locBucket);
      }
      catch {
        strTable = null;
      }

      if (strTable == null) {
        return null;
      }

      return strTable.GetLocalizedText(strId, "");
    }

    public Dictionary<string, string> TryGetLocalizedOptionStrings(string fqn, GomObjectData textRetriever) {
      string locBucket = textRetriever.ValueOrDefault<string>("strLocalizedTextRetrieverBucket", null);
      long strId = textRetriever.ValueOrDefault<long>("strLocalizedTextRetrieverStringID", -1);

      if ((locBucket == null) || (strId == -1)) {
        return null;
      }

      StringTable strTable;
      try {
        strTable = Find(locBucket);
      }
      catch {
        strTable = null;
      }

      if (strTable == null) {
        return null;
      }

      Dictionary<string, string> result = strTable.GetOptionText(strId, fqn);
      return result;
    }

    public override bool Equals(object obj) {
      if (obj == null) return false;

      if (ReferenceEquals(this, obj)) return true;

      if (obj is not StringTable stb) return false;

      return Equals(stb);
    }

    public bool Equals(StringTable stb) {
      if (stb == null) return false;

      if (ReferenceEquals(this, stb)) return true;

      if (Fqn != stb.Fqn)
        return false;

      var ssComp = new Models.DictionaryComparer<long, StringTableEntry>();
      if (!ssComp.Equals(data, stb.data))
        return false;

      //foreach (var entry in data)
      bool isEqual = true;
      System.Threading.Tasks.Parallel.ForEach(data, (entry, loopState) => {
        if (isEqual) {
          if (stb.data.TryGetValue(entry.Key, out StringTableEntry prevEntry)) {
            if (prevEntry == null) {
              isEqual = false;
              loopState.Stop();
            }

            if (!entry.Value.Equals(prevEntry)) {
              isEqual = false;
              loopState.Stop();
            }
          } else {
            isEqual = false;
            loopState.Stop();
          }
        }
      });

      return isEqual;
    }

    public override int GetHashCode() {
      int hash = Fqn.GetHashCode();
      hash ^= Version.GetHashCode();
      if (fqnMap != null) { hash ^= fqnMap.GetHashCode(); }
      if (data != null) { hash ^= data.GetHashCode(); }
      if (failedFqns != null) { hash ^= failedFqns.GetHashCode(); }
      return hash;
    }
  }
}
