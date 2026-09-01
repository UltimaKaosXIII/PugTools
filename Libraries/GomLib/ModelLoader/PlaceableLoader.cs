using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using GomLib.Models;

namespace GomLib.ModelLoader {
  public class PlaceableLoader {
    const long NameLookupKey = -2761358831308646330;

    Dictionary<ulong, Placeable> idMap;
    Dictionary<string, Placeable> nameMap;
    readonly DataObjectModel _dom;

    public PlaceableLoader(DataObjectModel dom) {
      _dom = dom;
      Flush();
    }

    public void Flush() {
      idMap = new Dictionary<ulong, Placeable>();
      nameMap = new Dictionary<string, Placeable>();
    }

    public static string ClassName {
      get { return "plcTemplate"; }
    }

    public Placeable Load(ulong nodeId) {
      if (idMap.TryGetValue(nodeId, out Placeable result)) {
        return result;
      }

      GomObject obj = _dom.GetObject(nodeId);
      Placeable plc = new Placeable();
      return Load(plc, obj);
    }

    public Placeable Load(string fqn) {
      if (nameMap.TryGetValue(fqn, out Placeable result)) {
        return result;
      }

      GomObject obj = _dom.GetObject(fqn);
      Placeable plc = new Placeable();
      return Load(plc, obj);
    }

    public Placeable Load(GomObject obj) {
      Placeable plc = new Placeable();
      return Load(plc, obj);
    }

    public Placeable Load(Placeable plc, GomObject obj) {
      if (obj == null) { return null; }
      if (plc == null) { return null; }

      plc.Fqn = obj.Name;
      plc.Id = obj.Id;
      plc.References = obj.References;

      var textLookup = obj.Data.ValueOrDefault<Dictionary<object, object>>("locTextRetrieverMap", null);
      GomObjectData nameLookupData = null;
      if (textLookup != null) {
        if (textLookup.TryGetValue(NameLookupKey, out object nameValue)) nameLookupData = nameValue as GomObjectData;
        if (nameLookupData == null) {
          ulong unsignedNameLookupKey = unchecked((ulong)NameLookupKey);
          foreach (KeyValuePair<object, object> pair in textLookup) {
            bool match = false;
            try { match = Convert.ToUInt64(pair.Key) == unsignedNameLookupKey; } catch {
              match = String.Equals(pair.Key?.ToString(), unsignedNameLookupKey.ToString(), StringComparison.Ordinal);
            }
            if (match) { nameLookupData = pair.Value as GomObjectData; break; }
          }
        }
      }
      if (nameLookupData != null) {
        plc.LocalizedName = _dom.StringTable.TryGetLocalizedStrings(plc.Fqn, nameLookupData);
      }
      plc.LocalizedName = Normalize.Dictionary(plc.LocalizedName, plc.Fqn);
      string selectedName = GomLib.StringTable.SelectLocalizedText(plc.LocalizedName, null);
      plc.Name = String.IsNullOrWhiteSpace(selectedName) ? plc.Fqn.Split('.').Last() : selectedName.Trim();

      //public Conversation Conversation { get; set; }
      plc.ConversationFqn = obj.Data.ValueOrDefault<string>("plcConvo", null);

      //public Codex Codex { get; set; }
      plc.CodexId = obj.Data.ValueOrDefault<ulong>("plcCodexSpec", 0);

      //public Profession RequiredProfession { get; set; }
      plc.RequiredProfession = ProfessionExtensions.ToProfession(obj.Data.ValueOrDefault<ScriptEnum>("prfProfessionRequired", null));

      //public int RequiredProfessionLevel { get; set; }
      plc.RequiredProfessionLevel = (int)obj.Data.ValueOrDefault<long>("prfProfessionLevelRequired", 0);

      //public bool IsBank { get; set; }
      plc.IsBank = obj.Data.ValueOrDefault("plcIsBank", false);

      //public bool IsMailbox { get; set; }
      plc.IsMailbox = obj.Data.ValueOrDefault("plcIsMailbox", false);

      //public AuctionHouseNetwork AuctionNetwork { get; set; }
      plc.AuctionNetwork = AuctionHouseNetworkExtensions.ToAuctionHouseNetwork(obj.Data.ValueOrDefault<ScriptEnum>("plcAuctionNetwork", null));

      //public bool IsAuctionHouse { get; set; }
      plc.IsAuctionHouse = plc.AuctionNetwork != AuctionHouseNetwork.None;

      //public bool IsEnhancementStation { get; set; }
      plc.IsEnhancementStation = EnhancementStationType.None != EnhancementStationTypeExtensions.ToEnhancementStationType(obj.Data.ValueOrDefault<ScriptEnum>("itmEnhancementStationType", null));

      //public Faction Faction { get; set; }
      plc.Faction = FactionExtensions.ToFaction(obj.Data.ValueOrDefault<long>("plcFaction", 0));

      //public int LootLevel { get; set; }
      plc.LootLevel = (int)obj.Data.ValueOrDefault<long>("plcdynLootLevel", 0);

      //public long LootPackageId { get; set; }
      plc.LootPackageId = obj.Data.ValueOrDefault<long>("plcdynLootPackage", 0);

      //public long WonkaPackageId { get; set; }
      plc.WonkaPackageId = obj.Data.ValueOrDefault<long>("wnkPackageID", 0);

      //public DifficultyLevel Difficulty { get; set; }
      plc.DifficultyFlags = (int)obj.Data.ValueOrDefault<long>("spnDifficultyLevelFlags", 0);

      //public HydraScript HydraScript { get; set; }
      ulong hydNodeId = obj.Data.ValueOrDefault<ulong>("plcHydra", 0);
      if (hydNodeId > 0) {
        //plc.HydraScript = HydraScriptLoader.Load(hydNodeId);
      }

      plc.TemplateNoGlow = obj.Data.ValueOrDefault("plcTemplateNoGlow", false);

      plc.PropState = obj.Data.ValueOrDefault<long>("plcPropState", 0);

      plc.AbilitySpecOnUseId = obj.Data.ValueOrDefault<ulong>("plcAbilitySpecOnUse", 0);

      plc.TemplateComment = obj.Data.ValueOrDefault<string>("utlTemplateComment", null);

      plc.Model = obj.Data.ValueOrDefault<string>("plcModel", null);

      Categorize(plc, obj);

      return plc;
    }

    private static void Categorize(Placeable plc, GomObject obj) {
      if (plc.IsMailbox) { plc.Category = PlaceableCategory.Mailbox; return; }
      if (plc.IsBank) { plc.Category = PlaceableCategory.Bank; return; }
      if (plc.IsAuctionHouse) { plc.Category = PlaceableCategory.AuctionHouse; return; }
      if (plc.IsEnhancementStation) { plc.Category = PlaceableCategory.EnhancementStation; return; }
      if (plc.Fqn.Contains("juke")) { plc.Category = PlaceableCategory.Jukebox; return; }

      // All Gathering Resource Nodes are in 'plc.generic.harvesting'
      if (plc.Fqn.StartsWith("plc.generic.harvesting.")) {
        plc.Category = PlaceableCategory.ResourceNode;
        return;
      }

      if (obj.Data.ValueOrDefault<ulong>("plcAbilitySpecOnUse", 0) == 16140902321107152398) {
        plc.Category = PlaceableCategory.Bindpoint;
        return;
      }

      if (plc.LootPackageId != 0) { plc.Category = PlaceableCategory.TreasureChest; return; }
      if (plc.Fqn.Contains("data_holocron")) { plc.Category = PlaceableCategory.Holocron; return; }
      if (obj.Data.ContainsKey("plcTaxiTerminalSpec")) { plc.Category = PlaceableCategory.TaxiTerminal; return; }

      if (plc.CodexId != 0) { plc.Category = PlaceableCategory.Codex; return; }
      if (plc.WonkaPackageId != 0) { plc.Category = PlaceableCategory.Elevator; return; }

      if (plc.Fqn.StartsWith("plc.location.")) { plc.Category = PlaceableCategory.Quest; }
    }

    public void LoadObject(GameObject loadMe, GomObject obj) {
      Placeable loadObj = (Placeable)loadMe;
      Load(loadObj, obj);
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
