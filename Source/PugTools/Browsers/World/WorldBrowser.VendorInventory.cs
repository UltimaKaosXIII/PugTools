using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using GomLib;
using GomLib.Models;
using Newtonsoft.Json.Linq;

namespace PugTools {
  public partial class WorldBrowser {
    private sealed class WorldVendorCostPart {
      public ulong ItemId;
      public string Name;
      public long Amount;
      public bool Credits;
    }

    private sealed class WorldVendorInventoryRow {
      public string Package;
      public ulong ItemId;
      public string ItemFqn;
      public string Name;
      public int Quantity = 1;
      public int Level;
      public string Quality;
      public string Cost;
      public bool FromOverride;
    }

    private sealed class WorldVendorInventoryResult {
      public readonly List<WorldVendorInventoryRow> Rows = new List<WorldVendorInventoryRow>();
      public readonly List<string> MissingPackages = new List<string>();
      public readonly List<string> FoundPackages = new List<string>();
      public string OverridePath;
    }

    private Form worldVendorInventoryForm;
    private Label worldVendorInventoryTitle;
    private Label worldVendorInventoryStatus;
    private ListView worldVendorInventoryList;
    private Dictionary<string, JArray> worldVendorOverridePackages;
    private string worldVendorOverrideLoadedPath;
    private bool worldVendorOverrideLoadAttempted;

    private void OpenWorldVendorInventory(WorldInteractionInfo interaction) {
      if (interaction == null || interaction.Kind != WorldInteractionKind.Vendor) return;
      string[] packages = interaction.VendorPackages?.Where(x => !String.IsNullOrWhiteSpace(x))
        .Select(x => x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() ?? Array.Empty<string>();

      string vendorName = panelRender?.InteractionTargetWorldNpcPlacement?.Name;
      if (String.IsNullOrWhiteSpace(vendorName)) vendorName = panelRender?.InteractionTargetWorldSpnPlacement?.Name;
      if (String.IsNullOrWhiteSpace(vendorName)) vendorName = "Vendor";

      Cursor oldCursor = Cursor.Current;
      try {
        Cursor.Current = Cursors.WaitCursor;
        WorldVendorInventoryResult inventory = ResolveWorldVendorInventory(packages);
        EnsureWorldVendorInventoryForm();
        worldVendorInventoryTitle.Text = vendorName + " — inventory";
        PopulateWorldVendorInventoryList(inventory);
        if (!worldVendorInventoryForm.Visible) worldVendorInventoryForm.Show(this);
        else worldVendorInventoryForm.BringToFront();
      } catch (Exception ex) {
        MessageBox.Show(this, "Vendor inventory could not be resolved.\r\n\r\n" + ex.Message,
          "Vendor inventory", MessageBoxButtons.OK, MessageBoxIcon.Warning);
      } finally {
        Cursor.Current = oldCursor;
      }
    }

    private void EnsureWorldVendorInventoryForm() {
      if (worldVendorInventoryForm != null && !worldVendorInventoryForm.IsDisposed) return;
      worldVendorInventoryForm = new Form {
        Text = "Vendor inventory",
        FormBorderStyle = FormBorderStyle.SizableToolWindow,
        ShowInTaskbar = false,
        StartPosition = FormStartPosition.CenterParent,
        MinimumSize = new Size(650, 340),
        Size = new Size(900, 540),
        BackColor = Color.FromArgb(13, 24, 29)
      };
      worldVendorInventoryTitle = new Label {
        Dock = DockStyle.Top,
        Height = 42,
        Padding = new Padding(10, 8, 10, 5),
        Font = new Font(Font, FontStyle.Bold),
        ForeColor = Color.FromArgb(135, 220, 242),
        BackColor = Color.FromArgb(8, 19, 23),
        AutoEllipsis = true
      };
      worldVendorInventoryStatus = new Label {
        Dock = DockStyle.Bottom,
        Height = 54,
        Padding = new Padding(9, 6, 9, 6),
        ForeColor = Color.Gainsboro,
        BackColor = Color.FromArgb(8, 19, 23),
        AutoEllipsis = true
      };
      worldVendorInventoryList = new ListView {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        GridLines = true,
        HideSelection = false,
        MultiSelect = false,
        BackColor = Color.FromArgb(13, 24, 29),
        ForeColor = Color.Gainsboro,
        BorderStyle = BorderStyle.None
      };
      worldVendorInventoryList.Columns.Add("#", 48, HorizontalAlignment.Right);
      worldVendorInventoryList.Columns.Add("Item", 280, HorizontalAlignment.Left);
      worldVendorInventoryList.Columns.Add("Cost", 215, HorizontalAlignment.Left);
      worldVendorInventoryList.Columns.Add("Level", 60, HorizontalAlignment.Right);
      worldVendorInventoryList.Columns.Add("Quality", 95, HorizontalAlignment.Left);
      worldVendorInventoryList.Columns.Add("Package", 180, HorizontalAlignment.Left);
      worldVendorInventoryForm.Controls.Add(worldVendorInventoryList);
      worldVendorInventoryForm.Controls.Add(worldVendorInventoryStatus);
      worldVendorInventoryForm.Controls.Add(worldVendorInventoryTitle);
      worldVendorInventoryForm.FormClosed += (_, __) => {
        worldVendorInventoryForm = null;
        worldVendorInventoryTitle = null;
        worldVendorInventoryStatus = null;
        worldVendorInventoryList = null;
      };
    }

    private void PopulateWorldVendorInventoryList(WorldVendorInventoryResult result) {
      if (worldVendorInventoryList == null || result == null) return;
      worldVendorInventoryList.BeginUpdate();
      try {
        worldVendorInventoryList.Items.Clear();
        foreach (WorldVendorInventoryRow row in result.Rows
          .OrderBy(x => x.Name ?? String.Empty, StringComparer.CurrentCultureIgnoreCase)
          .ThenBy(x => x.Package ?? String.Empty, StringComparer.OrdinalIgnoreCase)) {
          var item = new ListViewItem(Math.Max(1, row.Quantity).ToString(CultureInfo.CurrentCulture));
          item.SubItems.Add(String.IsNullOrWhiteSpace(row.Name) ? (row.ItemId != 0 ? row.ItemId.ToString(CultureInfo.InvariantCulture) : row.ItemFqn ?? "(unknown item)") : row.Name);
          item.SubItems.Add(String.IsNullOrWhiteSpace(row.Cost) ? "—" : row.Cost);
          item.SubItems.Add(row.Level > 0 ? row.Level.ToString(CultureInfo.CurrentCulture) : String.Empty);
          item.SubItems.Add(row.Quality ?? String.Empty);
          item.SubItems.Add(row.Package ?? String.Empty);
          item.Tag = row;
          if (row.FromOverride) item.ToolTipText = "Loaded from local VendorInventory.json override.";
          worldVendorInventoryList.Items.Add(item);
        }
      } finally {
        worldVendorInventoryList.EndUpdate();
      }

      string status;
      if (result.Rows.Count > 0) {
        status = result.Rows.Count.ToString(CultureInfo.CurrentCulture) + " item" + (result.Rows.Count == 1 ? String.Empty : "s") +
          " from " + result.FoundPackages.Count.ToString(CultureInfo.CurrentCulture) + " local vendor package" + (result.FoundPackages.Count == 1 ? String.Empty : "s") + ".";
        if (result.MissingPackages.Count > 0) status += "  No client-side stock for: " + String.Join(", ", result.MissingPackages) + ".";
      } else if (result.MissingPackages.Count > 0) {
        status = "The client exposes the vendor package name(s), but no local stock/price rows for: " + String.Join(", ", result.MissingPackages) + ".";
      } else {
        status = "No static vendor inventory was found in the client data for this vendor.";
      }
      if (!String.IsNullOrWhiteSpace(result.OverridePath)) status += "  Local override: " + result.OverridePath;
      worldVendorInventoryStatus.Text = status;
    }

    private WorldVendorInventoryResult ResolveWorldVendorInventory(string[] packages) {
      var result = new WorldVendorInventoryResult();
      EnsureWorldVendorOverrideLoaded();
      result.OverridePath = worldVendorOverrideLoadedPath;
      foreach (string package in packages ?? Array.Empty<string>()) {
        int before = result.Rows.Count;
        if (worldVendorOverridePackages != null && worldVendorOverridePackages.TryGetValue(package, out JArray overrideRows)) {
          ReadWorldVendorOverrideRows(package, overrideRows, result.Rows);
        }
        if (currentDom != null) {
          GomObject packageObject = ResolveWorldVendorPackageObject(package);
          if (packageObject != null) {
            try {
              var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
              ScanWorldVendorValue(packageObject.Data, package, result.Rows, seen, 0, false);
            } finally {
              try { packageObject.Unload(); } catch { }
            }
          }
        }
        if (result.Rows.Count > before) result.FoundPackages.Add(package);
        else result.MissingPackages.Add(package);
      }
      DeduplicateWorldVendorRows(result.Rows);
      return result;
    }

    private GomObject ResolveWorldVendorPackageObject(string package) {
      if (currentDom == null || String.IsNullOrWhiteSpace(package)) return null;
      string value = package.Trim();
      foreach (string candidate in new[] { value, value.TrimStart('/'), value.Replace('/', '.') }.Distinct(StringComparer.OrdinalIgnoreCase)) {
        try {
          GomObject obj = currentDom.GetObject(candidate);
          if (obj != null) return obj;
        } catch { }
      }
      return null;
    }

    private void ScanWorldVendorValue(object value, string package, List<WorldVendorInventoryRow> rows, HashSet<object> seen, int depth, bool insideCost) {
      if (value == null || rows == null || depth > 12) return;
      if (value is string || value.GetType().IsValueType) return;
      if (!seen.Add(value)) return;

      if (value is GomObjectData data) {
        if (!insideCost) TryAddWorldVendorRow(data.Dictionary, package, rows);
        foreach (KeyValuePair<string, object> pair in data.Dictionary) {
          bool childCost = insideCost || WorldVendorIsCostField(pair.Key);
          ScanWorldVendorValue(pair.Value, package, rows, seen, depth + 1, childCost);
        }
        return;
      }
      if (value is IDictionary<object, object> genericDict) {
        var dict = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<object, object> pair in genericDict) dict[pair.Key?.ToString() ?? String.Empty] = pair.Value;
        if (!insideCost) TryAddWorldVendorRow(dict, package, rows);
        foreach (KeyValuePair<object, object> pair in genericDict) {
          string key = pair.Key?.ToString() ?? String.Empty;
          ScanWorldVendorValue(pair.Value, package, rows, seen, depth + 1, insideCost || WorldVendorIsCostField(key));
        }
        return;
      }
      if (value is IDictionary dictionary) {
        var dict = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry pair in dictionary) dict[pair.Key?.ToString() ?? String.Empty] = pair.Value;
        if (!insideCost) TryAddWorldVendorRow(dict, package, rows);
        foreach (DictionaryEntry pair in dictionary) {
          string key = pair.Key?.ToString() ?? String.Empty;
          ScanWorldVendorValue(pair.Value, package, rows, seen, depth + 1, insideCost || WorldVendorIsCostField(key));
        }
        return;
      }
      if (value is IEnumerable sequence) {
        foreach (object child in sequence) ScanWorldVendorValue(child, package, rows, seen, depth + 1, insideCost);
      }
    }

    private static bool WorldVendorIsCostField(string key) {
      if (String.IsNullOrWhiteSpace(key)) return false;
      return String.Equals(key, "vndItemCost", StringComparison.OrdinalIgnoreCase) ||
        String.Equals(key, "4611686061193531191", StringComparison.OrdinalIgnoreCase) ||
        String.Equals(key, "vndItemPrice", StringComparison.OrdinalIgnoreCase) ||
        String.Equals(key, "4611686033861670018", StringComparison.OrdinalIgnoreCase) ||
        key.IndexOf("cost", StringComparison.OrdinalIgnoreCase) >= 0 ||
        key.IndexOf("currency", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private void TryAddWorldVendorRow(IEnumerable<KeyValuePair<string, object>> fields, string package, List<WorldVendorInventoryRow> rows) {
      if (fields == null) return;
      var dict = fields.ToDictionary(x => x.Key ?? String.Empty, x => x.Value, StringComparer.OrdinalIgnoreCase);
      object itemSpec = WorldVendorField(dict, "vndItemSpec", "4611686061183631193");
      if (itemSpec == null) return;
      if (!TryResolveWorldVendorItem(itemSpec, out Item item, out ulong itemId, out string itemFqn)) return;

      object rawCost = WorldVendorField(dict, "vndItemCost", "4611686061193531191");
      rawCost ??= WorldVendorField(dict, "vndItemPrice", "4611686033861670018");
      List<WorldVendorCostPart> costs = ParseWorldVendorCosts(rawCost);
      int quantity = WorldVendorQuantity(dict, item);
      rows.Add(new WorldVendorInventoryRow {
        Package = package,
        ItemId = itemId,
        ItemFqn = itemFqn,
        Name = item?.Name,
        Quantity = quantity,
        Level = item?.ItemLevel ?? 0,
        Quality = item?.Quality.ToString(),
        Cost = FormatWorldVendorCost(costs)
      });
    }

    private static object WorldVendorField(Dictionary<string, object> fields, params string[] names) {
      if (fields == null) return null;
      foreach (string name in names) if (fields.TryGetValue(name, out object value)) return value;
      return null;
    }

    private bool TryResolveWorldVendorItem(object spec, out Item item, out ulong itemId, out string itemFqn) {
      item = null; itemId = 0; itemFqn = null;
      if (currentDom == null || spec == null) return false;
      try {
        if (spec is GomObject gom) {
          itemId = gom.Id; itemFqn = gom.Name; item = currentDom.ItemLoader.Load(gom); return item != null;
        }
        if (TryWorldVendorUInt64(spec, out ulong id) && id != 0) {
          itemId = id; item = currentDom.ItemLoader.Load(id); itemFqn = item?.Fqn; return item != null || itemId != 0;
        }
        string text = spec.ToString()?.Trim();
        if (String.IsNullOrWhiteSpace(text)) return false;
        itemFqn = text;
        item = currentDom.ItemLoader.Load(text);
        if (item != null) itemId = item.Id;
        return item != null;
      } catch { return itemId != 0 || !String.IsNullOrWhiteSpace(itemFqn); }
    }

    private static int WorldVendorQuantity(Dictionary<string, object> fields, Item item) {
      foreach (string key in new[] { "vndItemQuantity", "vndItemCount", "itmStackVendor", "4611686033861670023", "quantity", "count", "num" }) {
        if (!fields.TryGetValue(key, out object value)) continue;
        if (TryWorldVendorInt64(value, out long number) && number > 0 && number <= Int32.MaxValue) return (int)number;
      }
      return item?.VendorStackSize > 0 ? item.VendorStackSize : 1;
    }

    private List<WorldVendorCostPart> ParseWorldVendorCosts(object rawCost) {
      var result = new List<WorldVendorCostPart>();
      var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
      ParseWorldVendorCostValue(rawCost, result, seen, 0, null);
      return result.Where(x => x.Amount > 0).GroupBy(x => (x.Credits, x.ItemId, x.Name ?? String.Empty))
        .Select(g => new WorldVendorCostPart { Credits = g.Key.Credits, ItemId = g.Key.ItemId, Name = g.Key.Item3, Amount = g.Sum(x => x.Amount) }).ToList();
    }

    private void ParseWorldVendorCostValue(object value, List<WorldVendorCostPart> output, HashSet<object> seen, int depth, string keyHint) {
      if (value == null || output == null || depth > 10) return;
      if (TryWorldVendorInt64(value, out long scalar)) {
        if (scalar > 0 && !WorldVendorLooksLikeReferenceKey(keyHint)) output.Add(new WorldVendorCostPart { Credits = true, Name = "Credits", Amount = scalar });
        return;
      }
      if (value is string || value.GetType().IsValueType) return;
      if (!seen.Add(value)) return;

      Dictionary<string, object> dict = null;
      if (value is GomObjectData data) dict = data.Dictionary.ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);
      else if (value is IDictionary<object, object> generic) dict = generic.ToDictionary(x => x.Key?.ToString() ?? String.Empty, x => x.Value, StringComparer.OrdinalIgnoreCase);
      else if (value is IDictionary nongeneric) {
        dict = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry pair in nongeneric) dict[pair.Key?.ToString() ?? String.Empty] = pair.Value;
      }

      if (dict != null) {
        object currencySpec = WorldVendorField(dict, "vndItemSpec", "4611686061183631193", "currencyItem", "currencyItemId", "itemId", "spec");
        if (currencySpec != null) {
          long amount = 0;
          foreach (string key in new[] { "vndItemPrice", "4611686033861670018", "amount", "quantity", "count", "num", "value" }) {
            if (dict.TryGetValue(key, out object raw) && TryWorldVendorInt64(raw, out long n) && n > 0) { amount = n; break; }
          }
          if (amount > 0 && TryResolveWorldVendorItem(currencySpec, out Item currency, out ulong currencyId, out string currencyFqn)) {
            output.Add(new WorldVendorCostPart { ItemId = currencyId, Name = currency?.Name ?? currencyFqn ?? currencyId.ToString(CultureInfo.InvariantCulture), Amount = amount });
          }
        }
        foreach (KeyValuePair<string, object> pair in dict) {
          // Some cost maps are serialized directly as currency-item-id -> amount.
          if (TryWorldVendorUInt64(pair.Key, out ulong keyId) && TryWorldVendorInt64(pair.Value, out long amount) && amount > 0) {
            if (keyId == 0) {
              output.Add(new WorldVendorCostPart { Credits = true, Name = "Credits", Amount = amount });
              continue;
            }
            if (keyId > 1000000000UL) {
              Item currency = null; try { currency = currentDom?.ItemLoader.Load(keyId); } catch { }
              output.Add(new WorldVendorCostPart { ItemId = keyId, Name = currency?.Name ?? keyId.ToString(CultureInfo.InvariantCulture), Amount = amount });
              continue;
            }
          }
          if (currencySpec != null && (String.Equals(pair.Key, "vndItemPrice", StringComparison.OrdinalIgnoreCase) || String.Equals(pair.Key, "4611686033861670018", StringComparison.OrdinalIgnoreCase))) continue;
          ParseWorldVendorCostValue(pair.Value, output, seen, depth + 1, pair.Key);
        }
        return;
      }
      if (value is IEnumerable sequence) foreach (object child in sequence) ParseWorldVendorCostValue(child, output, seen, depth + 1, keyHint);
    }

    private static bool WorldVendorLooksLikeReferenceKey(string key) {
      if (String.IsNullOrWhiteSpace(key)) return false;
      string k = key.ToLowerInvariant();
      return k.Contains("spec") || k.EndsWith("id") || k.Contains("guid") || k.Contains("prototype") || k.Contains("displayorder");
    }

    private static string FormatWorldVendorCost(List<WorldVendorCostPart> costs) {
      if (costs == null || costs.Count == 0) return null;
      return String.Join(" + ", costs.Select(x => x.Credits
        ? x.Amount.ToString("N0", CultureInfo.CurrentCulture) + " Credits"
        : x.Amount.ToString("N0", CultureInfo.CurrentCulture) + " × " + (x.Name ?? x.ItemId.ToString(CultureInfo.InvariantCulture))));
    }

    private static bool TryWorldVendorUInt64(object value, out ulong result) {
      result = 0;
      if (value == null) return false;
      try {
        if (value is ulong ul) { result = ul; return true; }
        if (value is long l) { result = unchecked((ulong)l); return true; }
        if (value is uint ui) { result = ui; return true; }
        if (value is int i) { result = unchecked((ulong)i); return true; }
        string text = value.ToString()?.Trim();
        if (String.IsNullOrWhiteSpace(text)) return false;
        if (UInt64.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out result)) return true;
        if (Int64.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long signed)) { result = unchecked((ulong)signed); return true; }
      } catch { }
      return false;
    }

    private static bool TryWorldVendorInt64(object value, out long result) {
      result = 0;
      if (value == null) return false;
      try {
        if (value is long l) { result = l; return true; }
        if (value is int i) { result = i; return true; }
        if (value is short s) { result = s; return true; }
        if (value is byte b) { result = b; return true; }
        if (value is uint ui && ui <= Int64.MaxValue) { result = ui; return true; }
        if (value is ulong ul && ul <= Int64.MaxValue) { result = (long)ul; return true; }
        return Int64.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
      } catch { return false; }
    }

    private void EnsureWorldVendorOverrideLoaded() {
      if (worldVendorOverrideLoadAttempted) return;
      worldVendorOverrideLoadAttempted = true;
      worldVendorOverridePackages = new Dictionary<string, JArray>(StringComparer.OrdinalIgnoreCase);
      foreach (string path in WorldVendorOverrideCandidates()) {
        if (!File.Exists(path)) continue;
        try {
          JToken token = JToken.Parse(File.ReadAllText(path));
          if (token is JObject root) {
            JObject packages = root["packages"] as JObject ?? root;
            foreach (JProperty property in packages.Properties()) if (property.Value is JArray rows) worldVendorOverridePackages[property.Name] = rows;
          } else if (token is JArray vendorDocs) {
            // Jedipedia's historical vendor collection uses [{ name, items:[{ id, num, currencies:[...] }] }].
            foreach (JObject doc in vendorDocs.OfType<JObject>()) {
              string package = doc.Value<string>("name");
              if (!String.IsNullOrWhiteSpace(package) && doc["items"] is JArray rows) worldVendorOverridePackages[package] = rows;
            }
          }
          worldVendorOverrideLoadedPath = path;
          break;
        } catch { }
      }
    }

    private static IEnumerable<string> WorldVendorOverrideCandidates() {
      string baseDir = AppDomain.CurrentDomain.BaseDirectory;
      yield return Path.Combine(baseDir, "VendorInventory.json");
      yield return Path.Combine(baseDir, "vendors.json");
      yield return Path.Combine(baseDir, "Resources", "VendorInventory.json");
      yield return Path.Combine(baseDir, "Resources", "vendors.json");
    }

    private void ReadWorldVendorOverrideRows(string package, JArray source, List<WorldVendorInventoryRow> rows) {
      if (source == null || rows == null) return;
      foreach (JObject record in source.OfType<JObject>()) {
        object spec = record["itemId"]?.ToString() ?? record["id"]?.ToString() ?? record["item"]?.ToString() ?? record["fqn"]?.ToString();
        TryResolveWorldVendorItem(spec, out Item item, out ulong itemId, out string itemFqn);
        string name = item?.Name ?? record.Value<string>("name") ?? itemFqn;
        int quantity = Math.Max(1, record.Value<int?>("quantity") ?? record.Value<int?>("num") ?? item?.VendorStackSize ?? 1);
        var costs = new List<WorldVendorCostPart>();
        long credits = record.Value<long?>("credits") ?? 0;
        if (credits > 0) costs.Add(new WorldVendorCostPart { Credits = true, Name = "Credits", Amount = credits });
        JArray costArray = record["costs"] as JArray ?? record["currencies"] as JArray;
        if (costArray != null) {
          foreach (JObject c in costArray.OfType<JObject>()) {
            long amount = c.Value<long?>("amount") ?? c.Value<long?>("num") ?? 0;
            if (amount <= 0) continue;
            object costSpec = c["itemId"]?.ToString() ?? c["id"]?.ToString() ?? c["item"]?.ToString() ?? c["fqn"]?.ToString();
            if (TryWorldVendorUInt64(costSpec, out ulong rawCurrencyId) && rawCurrencyId == 0) {
              costs.Add(new WorldVendorCostPart { Credits = true, Name = "Credits", Amount = amount });
            } else if (TryResolveWorldVendorItem(costSpec, out Item currency, out ulong currencyId, out string currencyFqn)) {
              costs.Add(new WorldVendorCostPart { ItemId = currencyId, Name = currency?.Name ?? c.Value<string>("name") ?? currencyFqn, Amount = amount });
            } else if (!String.IsNullOrWhiteSpace(c.Value<string>("name"))) {
              costs.Add(new WorldVendorCostPart { Name = c.Value<string>("name"), Amount = amount });
            }
          }
        }
        rows.Add(new WorldVendorInventoryRow {
          Package = package,
          ItemId = itemId,
          ItemFqn = itemFqn,
          Name = name,
          Quantity = quantity,
          Level = item?.ItemLevel ?? record.Value<int?>("level") ?? 0,
          Quality = item?.Quality.ToString() ?? record.Value<string>("quality"),
          Cost = FormatWorldVendorCost(costs),
          FromOverride = true
        });
      }
    }

    private static void DeduplicateWorldVendorRows(List<WorldVendorInventoryRow> rows) {
      if (rows == null || rows.Count < 2) return;
      var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      for (int i = rows.Count - 1; i >= 0; i--) {
        WorldVendorInventoryRow row = rows[i];
        string item = row.ItemId != 0 ? row.ItemId.ToString(CultureInfo.InvariantCulture) : row.ItemFqn ?? row.Name ?? String.Empty;
        string key = (row.Package ?? String.Empty) + "|" + item + "|" + row.Quantity.ToString(CultureInfo.InvariantCulture) + "|" + (row.Cost ?? String.Empty);
        if (!seen.Add(key)) rows.RemoveAt(i);
      }
    }
  }
}
