using System;
using System.Collections.Generic;
using System.Linq;
using GomLib.Models;

namespace GomLib.ModelLoader {
  public class MtxStorefrontEntryLoader {
    public Dictionary<object, object> MtxStoreFrontData;
    public string MtxStoreFrontDataTable { get; private set; }
    readonly DataObjectModel _dom;

    public MtxStorefrontEntryLoader(DataObjectModel dom) {
      _dom = dom;
      Flush();
    }

    public void Flush() {
      MtxStoreFrontData = new Dictionary<object, object>();
      MtxStoreFrontDataTable = null;
    }

    public Dictionary<object, object> EnsureStorefrontData() {
      if (MtxStoreFrontData != null && MtxStoreFrontData.Count > 0) return MtxStoreFrontData;
      GomObject prototype = _dom.GetObject("mtxStorefrontInfoPrototype");
      if (prototype == null) {
        MtxStoreFrontData = new Dictionary<object, object>();
        return MtxStoreFrontData;
      }

      // Current clients use mtxStorefrontItems.  Keep mtxStorefrontData as a legacy
      // fallback so older extracted builds remain supported.
      MtxStoreFrontData = prototype.Data.ValueOrDefault<Dictionary<object, object>>(
        "mtxStorefrontItems", null);
      if (MtxStoreFrontData != null) {
        MtxStoreFrontDataTable = "mtxStorefrontItems";
      } else {
        MtxStoreFrontData = prototype.Data.ValueOrDefault(
          "mtxStorefrontData", new Dictionary<object, object>());
        MtxStoreFrontDataTable = "mtxStorefrontData";
      }
      prototype.Unload();
      return MtxStoreFrontData;
    }

    public MtxStorefrontEntry Load(long id) {
      EnsureStorefrontData();

      _ = new object();
      MtxStoreFrontData.TryGetValue(id, out object mtxData);

      MtxStorefrontEntry mtx = new MtxStorefrontEntry();
      return Load(mtx, id, (GomObjectData)mtxData);
    }

    public MtxStorefrontEntry Load(MtxStorefrontEntry mtx, long Id, GomObjectData obj) {
      if (obj == null) { return mtx; }
      if (mtx == null) { return null; }

      mtx.Dom = _dom;
      mtx.Prototype = "mtxStorefrontInfoPrototype";
      mtx.ProtoDataTable = MtxStoreFrontDataTable ?? "mtxStorefrontData";

      Boolean currentSchema = mtx.ProtoDataTable == "mtxStorefrontItems"
        || obj.ContainsKey("mtxStorefrontItemDisplayName")
        || obj.ContainsKey("mtxStorefrontItemImage");

      if (currentSchema) {
        long descriptionId = obj.ValueOrDefault<long>("mtxStorefrontItemDisplayDescription", 0);
        mtx.UnknowntextId = descriptionId;
        mtx.Unknowntext = _dom.StringTable.TryGetString("str.gui.mtxstorefrontitems", descriptionId);
        mtx.Localizedunknowntext = _dom.StringTable.TryGetLocalizedStrings("str.gui.mtxstorefrontitems", descriptionId);

        var currentBulletPointIds = obj.ValueOrDefault("mtxStorefrontItemDisplayBullets", new List<object>())
          .Select(x => { try { return Convert.ToInt64(x); } catch { return 0L; } })
          .Where(x => x != 0)
          .ToList();
        mtx.BulletPoints = currentBulletPointIds
          .Select(x => _dom.StringTable.TryGetString("str.gui.mtxstorefrontitems", x)).ToList();
        mtx.LocalizedBulletPoints = currentBulletPointIds
          .Select(x => _dom.StringTable.TryGetLocalizedStrings("str.gui.mtxstorefrontitems", x)).ToList();

        long currentNameId = obj.ValueOrDefault<long>("mtxStorefrontItemDisplayName", 0);
        mtx.Name = _dom.StringTable.TryGetString("str.gui.mtxstorefrontitems", currentNameId);
        mtx.LocalizedName = _dom.StringTable.TryGetLocalizedStrings("str.gui.mtxstorefrontitems", currentNameId);
        mtx.Id = Id;
        mtx.Icon = obj.ValueOrDefault("mtxStorefrontItemImage", "");
        _dom.Assets.Icons.AddMtx(mtx.Icon);

        mtx.Categories = obj.ValueOrDefault("mtxStorefrontItemCategories", new Dictionary<object, object>());
        mtx.FullPriceCost = obj.ValueOrDefault<long>("mtxStorefrontItemCost", 0);
        mtx.DiscountCost = obj.ValueOrDefault<long>("mtxStorefrontItemPresaleCost", 0);
        mtx.ItemIdsList = obj.ValueOrDefault("mtxStorefrontItemAssociatedItems", new List<object>())
          .Select(x => { try { return Convert.ToUInt64(x); } catch { return 0UL; } })
          .Where(x => x != 0)
          .ToList();
        mtx.LinkedMTXEntryId = obj.ValueOrDefault<long>("mtxStorefrontItemLinkedMtxid", 0);
        mtx.UnknownNumber = obj.ValueOrDefault<long>("mtxStorefrontItemPurchaseType", 0);
        mtx.UnknownBool2 = obj.ValueOrDefault("mtxStorefrontItemIsActive", false);
        mtx.IsAccountUnlock = obj.ValueOrDefault("mtxIsAccountUnlock", false);
        mtx.IsOnSale = obj.ValueOrDefault("mtxStorefrontItemIsOnSale", false);
        mtx.IsPlatform = obj.ValueOrDefault("mtxStorefrontItemIsPlatform", false);
        mtx.CanGift = obj.ValueOrDefault("mtxStorefrontItemCanGift", false);
        mtx.Flags = obj.ValueOrDefault<long>("mtxStorefrontItemFlags", 0);
        mtx.VisibilityConditionId = obj.ValueOrDefault<ulong>("mtxStorefrontItemVisibilityConditionalId", 0);
        return mtx;
      }

      var unknownId = obj.ValueOrDefault<long>("4611686297592334024", 0); //Always 3042172580397056 for collection items
      mtx.UnknowntextId = unknownId;
      mtx.Unknowntext = _dom.StringTable.TryGetString("str.gui.mtxstorefrontitems", unknownId); // need to find the right stringtable for this.
      mtx.Localizedunknowntext = _dom.StringTable.TryGetLocalizedStrings("str.gui.mtxstorefrontitems", unknownId);

      var rarityId = obj.ValueOrDefault<long>("mtxRarityDescriptionId", 0);
      mtx.RarityDescId = rarityId;
      mtx.RarityDesc = _dom.StringTable.TryGetString("str.gui.mtxstorefrontitems", rarityId);
      mtx.LocalizedRarityDesc = _dom.StringTable.TryGetLocalizedStrings("str.gui.mtxstorefrontitems", rarityId);

      var bulletPointIds = obj.ValueOrDefault("mtxBulletPointDescriptionIds", new List<object>()).ConvertAll(x => (long)x);
      mtx.BulletPoints = new List<string>();
      mtx.LocalizedBulletPoints = new List<Dictionary<string, string>>();
      foreach (var bullet in bulletPointIds) {
        mtx.BulletPoints.Add(_dom.StringTable.TryGetString("str.gui.mtxstorefrontitems", bullet));
        mtx.LocalizedBulletPoints.Add(_dom.StringTable.TryGetLocalizedStrings("str.gui.mtxstorefrontitems", bullet));
      }

      var nameId = obj.ValueOrDefault<long>("mtxName", 0);
      mtx.Name = _dom.StringTable.TryGetString("str.gui.mtxstorefrontitems", nameId);
      mtx.LocalizedName = _dom.StringTable.TryGetLocalizedStrings("str.gui.mtxstorefrontitems", nameId);

      mtx.Id = Id;
      mtx.Icon = obj.ValueOrDefault("mtxStorefrontIcon", ""); // "Mtx.Season3.Bikini_V02"
      _dom.Assets.Icons.AddMtx(mtx.Icon);

      mtx.UnknownNumber = obj.ValueOrDefault<long>("4611686296598030002", 0);
      mtx.Categories = obj.ValueOrDefault("mtxCategories", new Dictionary<object, object>());

      mtx.DiscountCost = obj.ValueOrDefault<long>("mtxDiscountPrice", 0);
      mtx.FullPriceCost = obj.ValueOrDefault<long>("mtxFullPrice", 0);

      //mtx.CategoryId = (long)obj.ValueOrDefault("mtxMainCategory", new object()); // 610 looked up in colCollectionItemsPrototype("colCollectionItemsCategoryData")

      mtx.ItemIdsList = obj.ValueOrDefault("mtxCollection", new List<object>()).ConvertAll(x => (ulong)x);
      /*{ 16141048636041134811, 16140999226559259282, 16141134542521957469, 
      * 16140928499777528367, 16141006708961340344, 16141053294373613055, 
      * 16140959691716914276,  } - items*/
      /*mtx.ItemList = new List<Item>();
      foreach (var item in mtx.ItemIdsList)
      {
          mtx.ItemList.Add(_dom.itemLoader.Load(item));
      }*/

      var retiredItemsLookupDictionary = obj.ValueOrDefault("4611686348190657002", new Dictionary<object, object>());

      mtx.IsAccountUnlock = obj.ValueOrDefault("mtxIsAccountUnlock", false); // if they have it, it's true
      mtx.UnknownBool2 = obj.ValueOrDefault("4611686297975974006", false); // if they have it, it's true

      mtx.LinkedMTXEntryId = obj.ValueOrDefault<long>("mtxLinkedId", 0);

      return mtx;
    }
  }
}
