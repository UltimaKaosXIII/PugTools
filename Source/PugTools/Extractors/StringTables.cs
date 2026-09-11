using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Xml.Linq;

using nsHashDictionary;
using TorArchive;

using GomLib;

namespace PugTools {
  internal partial class Tools {
    private static HashSet<String> DiscoverStringTables(DataObjectModel dom) {
      dom.StringTable.Flush(); // flushing out any loaded string tables that might have been altered.
      var foundStringTables = new HashSet<String>(StringComparer.OrdinalIgnoreCase);

      // Retail clients have a manifest, but RED/BLUE beta archives predate it. Never make
      // the extractor dependent on stb.manifest being present.
      TorArchive.File manifest = dom.Assets.FindFile("/resources/gamedata/str/stb.manifest");
      if (manifest != null) {
        try {
          XDocument doc = XDocument.Load(manifest.OpenCopyInMemory());
          XElement root = doc.Element("manifest");
          if (root != null) foreach (XElement element in root.Elements("file")) {
            String value = element.Attribute("val")?.Value?.Trim();
            if (!String.IsNullOrWhiteSpace(value)) foundStringTables.Add(value);
          }
        } catch (Exception ex) {
          System.Diagnostics.Debug.WriteLine("String table manifest could not be read: " + ex.Message);
        }
      }

      // Beta has no manifest. Discover named .stb (RED) and legacy XML .str (BLUE)
      // entries from the hash dictionary for the physical TORs that are actually loaded.
      if (manifest == null || foundStringTables.Count == 0) try {
        foreach (Library library in dom.Assets.Libraries) {
          if (library == null) continue;
          try { library.Load(); } catch { continue; }

          foreach (Archive archive in library.Archives.Values) {
            if (archive == null || String.IsNullOrWhiteSpace(archive.FileName)) continue;
            String archiveName = Path.GetFileNameWithoutExtension(archive.FileName);

            Boolean foundArchiveSpecificNames = false;
            foreach (HashData hash in HashDictionaryInstance.Instance.Dictionary.EnumerateArchiveFiles(archiveName)) {
              String fqn = StringTableFqnFromResourcePath(hash?.FileName);
              if (fqn == null) continue;
              foundStringTables.Add(fqn);
              foundArchiveSpecificNames = true;
            }

            // Some early beta builds use bare TOR names such as main_1.tor/system_1.tor while
            // the filename pack learned the same PH/SH values from assets_*, red_* or later
            // archive families. Resolve those names globally, but only for hashes physically
            // present in this TOR so resources from another build cannot leak into discovery.
            if (!foundArchiveSpecificNames) {
              foreach (TorArchive.File physicalFile in archive.EnumerateFiles()) {
                TorArchive.FileInfo info = physicalFile.FileInfo;
                HashData global = HashDictionaryInstance.Instance.Dictionary.SearchHashList(
                  info.PrimaryHash, info.SecondaryHash);
                String fqn = StringTableFqnFromResourcePath(global?.FileName);
                if (fqn != null) foundStringTables.Add(fqn);
              }
            }
          }
        }
      } catch (Exception ex) {
        System.Diagnostics.Debug.WriteLine("Legacy string table discovery failed: " + ex.Message);
      }

      // Conversation tables are a useful fallback for partially named/custom archives.
      List<GomObject> itmList = null;
      try { itmList = dom.GetObjectsStartingWith("cnv."); } catch { }
      if (itmList != null) foreach (GomObject itm in itmList) {
        if (itm == null || String.IsNullOrWhiteSpace(itm.Name)) continue;
        foundStringTables.Add("str." + itm.Name);
        try { itm.Unload(); } catch { }
      }

      return foundStringTables;
    }

    private static String StringTableFqnFromResourcePath(String fileName) {
      if (String.IsNullOrWhiteSpace(fileName)) return null;
      String normalized = fileName.Replace('\\', '/').Trim().ToLowerInvariant();
      if (!normalized.EndsWith(".stb", StringComparison.OrdinalIgnoreCase) &&
          !normalized.EndsWith(".str", StringComparison.OrdinalIgnoreCase))
        return null;

      int marker = normalized.IndexOf("/str/", StringComparison.OrdinalIgnoreCase);
      if (marker < 0) return null;

      String relative = normalized.Substring(marker + 5);
      int dot = relative.LastIndexOf('.');
      if (dot <= 0) return null;
      relative = relative.Substring(0, dot).Trim('/');
      if (relative.Length == 0) return null;
      return "str." + relative.Replace('/', '.');
    }

    internal void GetStrings() {
      Clearlist2();
      LoadData();

      // String generatedContent = ConversationStringTables(itmList);

      XElement stringTables;
      HashSet<String> foundStringTables = DiscoverStringTables(CurrentDom);

      /*
      if (chkBuildCompare.Checked) {
        prevfoundStringTables = DiscoverStringTables(previousDom);
        if (foundStringTables.Count != prevfoundStringTables.Count) {
          string pausehere = "";
        }
      }
      */

      stringTables = new XElement("StringTables");

      Clearlist2();
      ClearProgress();
      AddToList2("Loading String Tables.");

      Int32 count = foundStringTables.Count;
      Int32 i = 0;

      foreach (String stb in foundStringTables) {
        if (chkBuildCompare.Checked) {
          StringTable curTbl = CurrentDom.StringTable.Find(stb);

          ProgressUpdate(i, count);

          StringTable prevTbl = PreviousDom.StringTable.Find(stb);

          if (curTbl != null) {
            if (prevTbl != null) {
              if (!curTbl.Equals(prevTbl)) {
                AddToList2(string.Format("Changed: {0}", curTbl.Fqn));

                XElement newElement = CompareElements(
                  StbToXElement(prevTbl),
                  StbToXElement(curTbl)
                );

                if (newElement != null) {
                  newElement.Add(new XAttribute("Status", "Changed"));

                  if (newElement.Elements().Any())
                    stringTables.Add(newElement);
                }
              }
            } else {
              AddToList2(string.Format("New: {0}", curTbl.Fqn));

              XElement newElement = StbToXElement(curTbl);
              newElement.Add(new XAttribute("Status", "New"));

              if (newElement.Elements().Any())
                stringTables.Add(newElement);
            }
          } else if (prevTbl != null) {
            AddToList2(string.Format("Removed: {0}", prevTbl.Fqn));

            XElement remElement = StbToXElement(prevTbl);
            remElement.Add(new XAttribute("Status", "Removed"));

            if (remElement.Elements().Any())
              stringTables.Add(remElement);
          }
        } else {
          AddToList2("String Table: " + stb);

          XElement stringTable = StbToXElement(CurrentDom, stb);
          // if (stringTable.Elements().Count() > 0) {
          stringTables.Add(stringTable);
          // }
        }
        CurrentDom.StringTable.Flush(); //Seeing if this helps memory issues

        if (PreviousDom != null) PreviousDom.StringTable.Flush();

        i++;
      }

      if (chkBuildCompare.Checked) {
        // addtolist("Comparing the Current Abilities to the loaded Patch");
        // XElement addedItems = FindChangedEntries(stringTables, "StringTables", "StringTable");

        XElement addedItems = stringTables;

        AddToList1(
          "The String Tables has been generated there are "
            + addedItems.Elements("StringTable").Count()
            + " new/changed String Tables"
        );
        WriteFile(new XDocument(addedItems), "ChangedStringTables.xml", false);
      } else {
        XDocument stringTablesXDocument = new XDocument(stringTables);
        WriteFile(stringTablesXDocument, "StringTables.xml", false);
      }
      EnableButtons();
    }
    private XElement StbToXElement(DataObjectModel dom, String stb) {
      StringTable stbTable = dom.StringTable.Find(stb);

      //Debug.WriteLine(stb);
      if (stbTable == null) {
        //Debug.WriteLine("Couldn't find " + stb + " string table.");
        return new XElement(
          "StringTable",
          new XAttribute("Id", stb),
          new XAttribute("Notfound", "true")
        );
      }

      return StbToXElement(stbTable);
    }
    private XElement StbToXElement(StringTable stb) {
      XElement stringTable = new XElement("StringTable", new XAttribute("Id", stb.Fqn));
      // String stb = "/resources/en-us/" + .Replace(".", "/") + ".stb";

      try {
        foreach (var entry in stb.data) {
          // If we are doing a compare build then strip the entries without any values.

          if (chkBuildCompare.Checked) {
            String value = entry.Value.LocalizedText.ContainsKey(GomLib.StringTable.SelectedLocalization) ? entry.Value.LocalizedText[GomLib.StringTable.SelectedLocalization] : String.Empty;

            if (value != null && value.Length > 0) {
              stringTable.Add(new XElement("Entry", new XAttribute("Id", entry.Key), value));
            }
          } else {
            String selected = entry.Value.LocalizedText.ContainsKey(GomLib.StringTable.SelectedLocalization) ? entry.Value.LocalizedText[GomLib.StringTable.SelectedLocalization] : String.Empty;
            String en = entry.Value.LocalizedText.ContainsKey("enMale") ? entry.Value.LocalizedText["enMale"] : String.Empty;
            String fr = entry.Value.LocalizedText.ContainsKey("frMale") ? entry.Value.LocalizedText["frMale"] : String.Empty;
            String de = entry.Value.LocalizedText.ContainsKey("deMale") ? entry.Value.LocalizedText["deMale"] : String.Empty;
            if (!String.IsNullOrEmpty(selected) || !String.IsNullOrEmpty(en) || !String.IsNullOrEmpty(fr) || !String.IsNullOrEmpty(de))
              stringTable.Add(
                new XElement("Entry", new XAttribute("Id", entry.Key),
                new XElement("selected", new XAttribute("locale", GomLib.StringTable.SelectedLocale), selected),
                new XElement("en", en), new XElement("fr", fr), new XElement("de", de))
              );
          }
        }
      }
      catch {
        // Debug.WriteLine("Couldn't find " + stb + " string table.");
      }

      return stringTable;
    }
  }
}
