using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using GomLib.Models;

namespace GomLib.ModelLoader {
  public class CollectionLoader {
    public Dictionary<object, object> CollectionItemsData;

    private readonly DataObjectModel _dom;

    public CollectionLoader(DataObjectModel dom) {
      _dom = dom;
      _dom.MtxStorefrontEntryLoader = new MtxStorefrontEntryLoader(_dom);
      _dom.MtxStorefrontEntryLoader.Flush();
    }

    public void Flush() {
      CollectionItemsData = new Dictionary<object, object>();
    }

    public Collection Load(long id) {
      if (CollectionItemsData.Count == 0) {
        GomObject collectionPrototype = _dom.GetObject("colCollectionItemsPrototype");
        if (collectionPrototype != null) {
          CollectionItemsData =
            collectionPrototype.Data.ValueOrDefault<Dictionary<object, object>>(
              "colMtxItemIdToCollectionItem", null)
            ?? collectionPrototype.Data.ValueOrDefault<Dictionary<object, object>>(
              "4611686297655094008", null)
            ?? collectionPrototype.Data.ValueOrDefault<Dictionary<object, object>>(
              "colCollectionItemsData", new Dictionary<object, object>());
        }
      }

      _ = new object();
      CollectionItemsData.TryGetValue(id, out object mtxData);

      Collection mtx = new Collection();
      return Load(mtx, id, (GomObjectData)mtxData);
    }

    public Collection Load(Collection col, long Id, GomObjectData obj) {
      if (obj == null) { return col; }
      if (col == null) { return null; }

      if (_dom.MtxStorefrontEntryLoader.MtxStoreFrontData.Count == 0) {
        _dom.MtxStorefrontEntryLoader.EnsureStorefrontData();
      }
      _dom.MtxStorefrontEntryLoader.MtxStoreFrontData.TryGetValue(Id, out object mtxData);

      col.Dom = _dom;
      col.Prototype = "colCollectionItemsPrototype";
      Boolean currentCollectionSchema =
        obj.ContainsKey("colItemImage") || obj.ContainsKey("4611686297655094004");
      col.ProtoDataTable = currentCollectionSchema
        ? "colMtxItemIdToCollectionItem"
        : "colCollectionItemsData";

      GomObjectData mtxObject = mtxData as GomObjectData;
      Boolean currentMtx = _dom.MtxStorefrontEntryLoader.MtxStoreFrontDataTable == "mtxStorefrontItems";
      var unknownId = currentMtx
        ? mtxObject?.ValueOrDefault<long>("mtxStorefrontItemDisplayDescription", 0) ?? 0
        : mtxObject?.ValueOrDefault<long>("4611686297592334024", 0) ?? 0; //Always 3042172580397056 for collection items
      col.UnknowntextId = unknownId;
      col.Unknowntext = _dom.StringTable.TryGetString("str.gui.mtxstorefrontitems", unknownId); // need to find the right stringtable for this.
      col.Localizedunknowntext = _dom.StringTable.TryGetLocalizedStrings("str.gui.mtxstorefrontitems", unknownId);

      var rarityId = mtxObject?.ValueOrDefault<long>("mtxRarityDescriptionId", 0) ?? 0;
      col.RarityDescId = rarityId;
      col.RarityDesc = _dom.StringTable.TryGetString("str.gui.mtxstorefrontitems", rarityId);
      col.LocalizedRarityDesc = _dom.StringTable.TryGetLocalizedStrings("str.gui.mtxstorefrontitems", rarityId);

      List<Object> rawBulletPointIds = currentMtx
        ? (mtxObject?.ValueOrDefault("mtxStorefrontItemDisplayBullets", new List<object>()) ?? new List<object>())
        : (mtxObject?.ValueOrDefault("mtxBulletPointDescriptionIds", new List<object>()) ?? new List<object>());
      var bulletPointIds = rawBulletPointIds
        .Select(x => { try { return Convert.ToInt64(x); } catch { return 0L; } })
        .Where(x => x != 0)
        .ToList();
      col.BulletPoints = new List<string>();
      foreach (var bullet in bulletPointIds) { col.BulletPoints.Add(_dom.StringTable.TryGetString("str.gui.mtxstorefrontitems", bullet)); }
      col.LocalizedBulletPoints = new List<Dictionary<string, string>>();
      foreach (var bullet in bulletPointIds) { col.LocalizedBulletPoints.Add(_dom.StringTable.TryGetLocalizedStrings("str.gui.mtxstorefrontitems", bullet)); }

      var nameId = currentMtx
        ? mtxObject?.ValueOrDefault<long>("mtxStorefrontItemDisplayName", 0) ?? 0
        : mtxObject?.ValueOrDefault<long>("mtxName", 0) ?? 0;
      col.Name = _dom.StringTable.TryGetString("str.gui.mtxstorefrontitems", nameId);
      col.LocalizedName = _dom.StringTable.TryGetLocalizedStrings("str.gui.mtxstorefrontitems", nameId);

      col.Id = Id;
      col.CreationIndex = currentCollectionSchema
        ? obj.ValueOrDefault<long>("colItemBitIndex",
            obj.ValueOrDefault<long>("4611686347564387001", 0))
        : obj.ValueOrDefault<long>("colCreationIndex", 0);
      col.Icon = currentCollectionSchema
        ? obj.ValueOrDefault("colItemImage",
            obj.ValueOrDefault("4611686297655094004", ""))
        : obj.ValueOrDefault("colCollectionIcon", "");
      if (!String.IsNullOrWhiteSpace(col.Icon)) _dom.Assets.Icons.AddMtx(col.Icon);

      col.IsFoundInPacks = obj.ValueOrDefault("colItemIsFoundInPacks", false);

      col.LinkedId = obj.ValueOrDefault<long>("colLinkedId", 0);

      List<object> unknownList = obj.ValueOrDefault<List<object>>("4611686297968184000", null); // seems to be always empty.

      /*if (unknownList.Count != 0) {                                         //This is some code to isolate cases where this list might have values
          var u = DataObjectModel.GetObject((ulong)unknownList[0]);
          if (u != null) {
              string stopHere = ""; } }*/

      List<object> unknownList2 = obj.ValueOrDefault("colItemAssociatedMountSpecs",
        obj.ValueOrDefault("4611686297983034002", new List<object>()));
      /*if (unknownList.Count != 0) {                                         //This is some code to isolate cases where this list might have values
          var u = DataObjectModel.GetObject((ulong)unknownList2[0]);
          if (u != null) {
              string stopHere = ""; } }*/

      //col.CategoryId = (long)obj.ValueOrDefault<object>("mtxStorefrontMainCategory", new object()); // 610 looked up in colCollectionItemsPrototype("colCollectionItemsCategoryData")

      col.ItemIdsList = currentCollectionSchema
        ? obj.ValueOrDefault("colItemAssociatedItemIds",
            obj.ValueOrDefault("4611686347564387005", new List<object>())).ConvertAll(Convert.ToUInt64)
        : obj.ValueOrDefault("colItemList", new List<object>()).ConvertAll(Convert.ToUInt64);
      /*{ 16141048636041134811, 16140999226559259282, 16141134542521957469, 
      * 16140928499777528367, 16141006708961340344, 16141053294373613055, 
      * 16140959691716914276,  } - items*/
      //Add some code here to load each item.
      /*col.ItemList = new List<Item>();
      foreach (var item in col.ItemIdsList)
      {
          col.ItemList.Add(_dom.itemLoader.Load(item));
      }*/

      col.AbilityIdsList = currentCollectionSchema
        ? obj.ValueOrDefault("colItemAssociatedAbilities",
            obj.ValueOrDefault("4611686347564747001", new List<object>())).ConvertAll(Convert.ToUInt64)
        : obj.ValueOrDefault("colAbilityList", new List<object>()).ConvertAll(Convert.ToUInt64);
      /*col.AbilityList = new List<Ability>();
      foreach (var ability in col.AbilityIdsList)
      {
          col.AbilityList.Add(_dom.abilityLoader.Load(ability));
      }*/

      var titleShortIdLookupList = currentCollectionSchema
        ? obj.ValueOrDefault("colItemAssociatedTitle",
            obj.ValueOrDefault("4611686347564747003", new List<object>())).ConvertAll(Convert.ToInt64)
        : obj.ValueOrDefault("colCollectionsTitleId", new List<object>()).ConvertAll(Convert.ToInt64); /* legacy title ids; current data stores associated title indices
                                                                                                                                 * colCollectionItemsPrototype
                                                                                                                                 * colCollectionsTitleData                   */

      var emoteShortIdLookupList = obj.ValueOrDefault("colCollectionsEmoteId", new List<object>()).ConvertAll(Convert.ToInt64); /* legacy-only field
                                                                                                                                 * colCollectionItemsPrototype
                                                                                                                                 * colCollectionsEmoteData                   */

      var longBoolDic = obj.ValueOrDefault("colItemCollectionTags",
        obj.ValueOrDefault("4611686347575727004", new Dictionary<object, object>())); /* current: collection tags
                                                                                              * need to figure out what the heck these are */

      List<object> linkedListO = obj.ValueOrDefault<List<object>>("4611686347582697000", null);
      if (linkedListO != null) {
        List<long> linkedList = linkedListO.ConvertAll(Convert.ToInt64);
      }

      var unknownlong = obj.ValueOrDefault<long>("4611686348190277001", 0); // -7824174851411027002 - not sure what this is

      col.RequiredLevel = currentCollectionSchema
        ? obj.ValueOrDefault<long>("colItemMinimumLevel",
            obj.ValueOrDefault<long>("4611686348190437000", 1))
        : obj.ValueOrDefault<long>("colCollectionsRequiredLevel", 1);

      var alternateUnlocks = obj.ValueOrDefault("colCollectionItemAlternateItemsMapping",
        obj.ValueOrDefault("4611686348190657005", new Dictionary<object, object>()));
      col.HasAlternateUnlocks = (alternateUnlocks.Count > 0);
      col.AlternateUnlocksMap = new Dictionary<ulong, List<ulong>>();

      if (alternateUnlocks.Count > 0) {
        col.AlternateUnlocksMap = alternateUnlocks.ToDictionary(p => Convert.ToUInt64(p.Key), p => ((List<object>)p.Value).ConvertAll(Convert.ToUInt64));
      }


      List<ulong> collectionItemsList2 = new List<ulong>(); // Might be items granted.
      collectionItemsList2 = obj.ValueOrDefault("colItemDisplayItemIds",
        obj.ValueOrDefault("4611686348671327000", new List<object>())).ConvertAll(Convert.ToUInt64);
      /*{ 16141048636041134811, 16140999226559259282, 16141134542521957469, 
      * 16140928499777528367, 16141006708961340344, 16141053294373613055, 
      * 16140959691716914276,  } - items*/

      return col;
    }
  }
}
