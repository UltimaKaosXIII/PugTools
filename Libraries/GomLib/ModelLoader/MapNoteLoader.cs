using System;
using System.Collections.Generic;
using GomLib.Models;
// using System.Diagnostics;

namespace GomLib.ModelLoader {
  public class MapNoteLoader {
    const long NameLookupKey = -2761358831308646330;

    private StringTable taxiTerminals;

    Dictionary<ulong, MapNote> idMap;
    Dictionary<string, MapNote> nameMap;
    readonly DataObjectModel _dom;

    public MapNoteLoader(DataObjectModel dom) {
      _dom = dom;
      Flush();
    }

    public void Flush() {
      taxiTerminals = null;
      idMap = new Dictionary<ulong, MapNote>();
      nameMap = new Dictionary<string, MapNote>();
    }

    public static string ClassName {
      get { return "mpnMapNoteInfo"; }
    }

    public MapNote Load(ulong nodeId) {
      if (idMap.TryGetValue(nodeId, out MapNote result)) {
        return result;
      }

      GomObject obj = _dom.GetObject(nodeId);
      MapNote mpn = new MapNote();
      return Load(mpn, obj);
    }

    public MapNote Load(string fqn) {
      if (nameMap.TryGetValue(fqn, out MapNote result)) {
        return result;
      }

      GomObject obj = _dom.GetObject(fqn);
      MapNote mpn = new MapNote();
      return Load(mpn, obj);
    }

    public MapNote Load(GomObject obj) {
      MapNote mpn = new MapNote();
      return Load(mpn, obj);
    }

    public static GameObject CreateObject() {
      return new MapNote();
    }

    public MapNote Load(MapNote mpn, GomObject obj) {
      if (obj == null) { return null; }
      if (mpn == null) { return null; }

      if (taxiTerminals == null) {
        taxiTerminals = _dom.StringTable.Find("str.tax.terminals");
      }

      mpn.Fqn = obj.Name;
      mpn.Id = obj.Id;
      mpn.References = obj.References;
      if (mpn.References != null && mpn.References.ContainsKey("usedByArea")) {
        if (mpn.References["usedByArea"].Count > 1) {
          // var sfoin = "";
        }
      }

      mpn.AssetID = obj.Data.ValueOrDefault<long>("mpnAssetID", 0);
      //mpn.Id = (ulong)(mpnAssetID >> 32);

      // Not every authored mapnote has a display-name retriever (utility/debug/link-only notes are common).
      // The old hard index threw before mpnIconAsset / Wonkavator / map-link metadata could be read, which made
      // otherwise valid symbols disappear from the map and left their tooltips empty. Resolve the optional name
      // defensively and continue loading the structural metadata even when no localized label exists.
      var textLookup = obj.Data.ValueOrDefault<Dictionary<object, object>>("locTextRetrieverMap", null);
      GomObjectData nameLookupData = null;
      if (textLookup != null && textLookup.TryGetValue(NameLookupKey, out object rawNameLookup))
        nameLookupData = rawNameLookup as GomObjectData;
      if (nameLookupData != null) {
        mpn.Name = _dom.StringTable.TryGetString(mpn.Fqn, nameLookupData);
        mpn.LocalizedName = _dom.StringTable.TryGetLocalizedStrings(mpn.Fqn, nameLookupData);
      }

      //mpn.Icon = MapNoteIconExtensions.ToMapNoteIcon(obj.Data.Get<string>("mpnIconAsset"));
      mpn.Icon = obj.Data.ValueOrDefault<string>("mpnIconAsset", null);
      if (obj.Data.ContainsKey("mpnConditionEType")) { mpn.Condition = MapNoteConditionExtensions.ToMapNoteCondition(obj.Data.Get<object>("mpnConditionEType").ToString()); }
      if (obj.Data.ContainsKey("mpnHuntingRadius")) { mpn.HuntingRadius = _dom.Data.huntingRadius.GetRadius(obj.Data.Get<long>("mpnHuntingRadius")); }
      if (obj.Data.ContainsKey("mpnHuntingRadiusBonus")) { mpn.BonusHuntingRadius = _dom.Data.huntingRadius.GetRadius(obj.Data.Get<long>("mpnHuntingRadiusBonus")); }

      if (obj.Data.ContainsKey("mpnMapLink")) {
        var mapLink = obj.Data.Get<GomObjectData>("mpnMapLink");
        ulong areaId = mapLink.Get<ulong>("mapLinkAreaId");
        long mId = mapLink.ValueOrDefault<long>("mapLinkMapNameSId");
        long sId = mapLink.ValueOrDefault<long>("mapLinkSubmapNameSId");

        mpn.MapLink = new MapLink(areaId, mId, sId);
        //GetLinkedMapId(mpn, areaId, sId);
      }

      // if (obj.Data.ContainsKey("mpnMetadataFullFQN") && mpn.Icon != MapNoteIcon.Taxi && mpn.Icon != MapNoteIcon.Wonkavator) { Debug.WriteLine(mpn.Fqn); }
      if (obj.Data.ContainsKey("mpnMetadataInt")) { mpn.WonkaPackageId = obj.Data.Get<long>("mpnMetadataInt"); }
      if (obj.Data.ContainsKey("mpnMetadataID")) { mpn.WonkaDestinationId = obj.Data.Get<ulong>("mpnMetadataID"); }
      //0x2F63800000000

      switch (mpn.Icon) {
        case "Taxi":
          if (obj.Data.ContainsKey("mpnMetadataFullFQN")) {
            ulong taxiNodeId = obj.Data.Get<ulong>("mpnMetadataFullFQN");
            var taxiNode = _dom.GetObject(taxiNodeId);
            if (taxiNode != null) {
              long taxNameId = taxiNode.Data.ValueOrDefault<long>("taxNameId", -1);
              long taxFaction = taxiNode.Data.ValueOrDefault<long>("taxFaction", 0);
              if (taxiTerminals != null) {
                mpn.Name = taxiTerminals.GetText(0x7D60500000000 + taxNameId, mpn.Fqn);
                mpn.LocalizedName = taxiTerminals.GetLocalizedText(0x7D60500000000 + taxNameId, mpn.Fqn);
              }
              mpn.Faction = _dom.FactionData.ToFaction(taxFaction);
            }
          }
          break;
        case "Wonkavator":
          _ = obj.Data.ValueOrDefault<ulong>("mpnMetadataFullFQN");
          //var unknownNode = _dom.GetObject(unknownNodeId);

          break;
      }

      return mpn;
    }

    ///// <summary>Retrieve the ID for the Map this map note should link to</summary>
    ///// <param name="mpn">The map note that links to a map</param>
    ///// <param name="areaId">Area ID of the linked map</param>
    ///// <param name="sId">SId of the linked map</param>
    //private void GetLinkedMapId(MapNote mpn, ulong areaId, long sId)
    //{
    //    switch (areaId)
    //    {
    //        case 4611686055156531852:
    //        case 4611686048166971304:
    //        case 4611686048166971255:
    //        case 4611686038902870211:
    //            {
    //                Debug.WriteLine("{0} links to map not in mapAreasProto", mpn.Fqn);
    //                return;
    //            }
    //    }

    //    string mapDataPath = String.Format("world.areas.{0}.mapdata", areaId);
    //    var mapDataObj = _dom.GetObject(mapDataPath);
    //    if (mapDataObj != null)
    //    {
    //        List<object> mapPages = (List<object>)mapDataObj.Data.ValueOrDefault<List<object>>("mapDataContainerMapDataList", null);

    //        if (mapPages != null)
    //        {
    //            foreach (GomObjectData mapPage in mapPages)
    //            {
    //                if (sId == mapPage.ValueOrDefault<long>("mapNameSId", 0))
    //                {
    //                    var guid = mapPage.ValueOrDefault<long>("mapPageGUID", 0);
    //                    mpn.LinkedMapId = (int)(guid & 0x7FFFFFFF);
    //                    return;
    //                }
    //            }
    //        }
    //    }
    //}

    public void LoadObject(GameObject loadMe, GomObject obj) {
      MapNote mpn = (MapNote)loadMe;
      Load(mpn, obj);
    }

    public static void LoadReferences(GameObject obj, GomObject gom) {
      if (obj == null) {
        throw new ArgumentNullException(nameof(obj));
      }

      if (gom == null) {
        throw new ArgumentNullException(nameof(gom));
      }
      // No references to load
    }
  }
}
