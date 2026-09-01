using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using GomLib;

namespace PugTools {
  public partial class WorldBrowser {
    // Shared locTextRetrieverMap slots. The meaning of a slot is class-specific: 9583... is an NPC title but a Codex
    // name, while 1568... is the normal NPC/placeable name. The retriever VALUE is universal, though: (bucket,id).
    private const ulong WorldLocNameSlot = 15685385242400905286UL;
    private const ulong WorldLocTitleOrCodexNameSlot = 9583395879878673097UL;
    private const ulong WorldLocCodexDescriptionSlot = 1078249248256508798UL;
    private const ulong WorldLocCodexCategorySlot = 18079279596637806545UL;

    private static object WorldLocMapValue(object map, ulong wanted) {
      if (map is GomObjectData gom) {
        foreach (KeyValuePair<string, object> pair in gom.Dictionary)
          if (WorldLocUnsigned(pair.Key) == wanted) return pair.Value;
      }
      if (map is IDictionary dictionary) {
        IDictionaryEnumerator iterator = dictionary.GetEnumerator();
        while (iterator.MoveNext()) if (WorldLocUnsigned(iterator.Key) == wanted) return iterator.Value;
      }
      return null;
    }

    private static ulong WorldLocUnsigned(object value) {
      if (value == null) return 0;
      try {
        if (value is ulong ul) return ul;
        if (value is long l) return unchecked((ulong)l);
        if (value is uint ui) return ui;
        if (value is int i) return unchecked((ulong)(long)i);
        string text = value.ToString()?.Trim();
        if (UInt64.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong parsed)) return parsed;
        if (Int64.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long signed)) return unchecked((ulong)signed);
      } catch { }
      return 0;
    }

    private static bool TryWorldLocEntry(object retriever, out StringTable.LocEntry entry) {
      if (retriever is GomObjectData data && StringTable.TryGetLocEntry(data, out entry)) return true;
      entry = default;
      return false;
    }

    private string WorldLocText(object retriever, string context = null, bool option = false) {
      if (currentDom?.StringTable == null || !TryWorldLocEntry(retriever, out StringTable.LocEntry entry)) return null;
      try {
        string text = option ? currentDom.StringTable.TryGetOptionString(entry, context ?? String.Empty)
          : currentDom.StringTable.TryGetString(entry, context ?? String.Empty);
        return String.IsNullOrWhiteSpace(text) ? null : text.Trim();
      } catch { return null; }
    }

    private Dictionary<string, string> WorldLocAll(object retriever, bool option = false) {
      if (currentDom?.StringTable == null || !TryWorldLocEntry(retriever, out StringTable.LocEntry entry)) return null;
      try { return option ? currentDom.StringTable.TryGetLocalizedOptionStrings(entry) : currentDom.StringTable.TryGetLocalizedStrings(entry); }
      catch { return null; }
    }

    private string WorldLocMapText(object map, ulong slot, string context = null, bool option = false) {
      return WorldLocText(WorldLocMapValue(map, slot), context, option);
    }

    private static string WorldLocRetrieverKey(object retriever) {
      return TryWorldLocEntry(retriever, out StringTable.LocEntry entry) ? entry.Key : null;
    }
  }
}
