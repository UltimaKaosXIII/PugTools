using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using GomLib.Models;

namespace GomLib.ModelLoader {
  public class QuestLoader {
    Dictionary<ulong, Quest> idMap;
    Dictionary<string, Quest> nameMap;

    Dictionary<object, object> fullQuestRewardsTable;
    Dictionary<object, object> newfullQuestRewardsTable;
    public Dictionary<long, Dictionary<long, float>> fullCreditRewardsTable;
    public Dictionary<long, long> experienceTable;
    //public Dictionary<QuestDifficulty, float> experienceDifficultyMultiplierTable;

    public Dictionary<ulong, ClassSpec> classSpecTable;
    readonly DataObjectModel _dom;

    public QuestLoader(DataObjectModel dom) {
      _dom = dom;
      Flush();
    }

    public void Flush() {
      idMap = new Dictionary<ulong, Quest>();
      nameMap = new Dictionary<string, Quest>();
      fullQuestRewardsTable = new Dictionary<object, object>();
      newfullQuestRewardsTable = new Dictionary<object, object>();
      fullCreditRewardsTable = new Dictionary<long, Dictionary<long, float>>();
      experienceTable = new Dictionary<long, long>();
      classSpecTable = new Dictionary<ulong, ClassSpec>();
      //experienceDifficultyMultiplierTable = new Dictionary<QuestDifficulty, float>();
    }

    public static string ClassName {
      get { return "qstQuestDefinition"; }
    }

    public Quest Load(ulong nodeId) {
      if (idMap.TryGetValue(nodeId, out Quest result)) {
        return result;
      }

      GomObject obj = _dom.GetObject(nodeId);
      Quest qst = new Quest();
      return Load(qst, obj);
    }

    public Quest Load(string fqn) {
      if (nameMap.TryGetValue(fqn, out Quest result)) {
        return result;
      }

      GomObject obj = _dom.GetObject(fqn);
      Quest qst = new Quest();
      return Load(qst, obj);
    }

    public Quest Load(GomObject obj) {
      Quest qst = new Quest();
      return Load(qst, obj);
    }

    public static GameObject CreateObject() {
      return new Quest();
    }

    public Quest Load(GameObject obj, GomObject gom) {
      if (gom == null) { return (Quest)obj; }

      return Load(obj as Quest, gom);
    }

    public Quest Load(Quest qst, GomObject obj) {
      if (obj == null) { return qst; }
      if (qst == null) { return null; }

      qst.Fqn = obj.Name;
      qst.NodeId = obj.Id;
      qst.Dom_ = _dom;
      qst.References = obj.References;
      var textMap = obj.Data.ValueOrDefault<Dictionary<object, object>>("locTextRetrieverMap", null);
      qst.TextLookup = textMap;

      long questGuid = obj.Data.ValueOrDefault<long>("qstQuestDefinitionGUID", 0);
      //qst.Id = (ulong)(questGuid >> 32);
      qst.Id = obj.Id;
      qst.RequiredLevel = (int)obj.Data.ValueOrDefault<long>("qstReqMinLevel", 0);
      qst.IsRepeatable = obj.Data.ValueOrDefault("qstIsRepeatable", false);
      qst.XpLevel = (int)obj.Data.ValueOrDefault<long>("qstXpLevel", 0);
      qst.Difficulty = (obj.Data.ValueOrDefault<ScriptEnum>("qstDifficulty", null) ?? new ScriptEnum()).ToString();
      qst.ReqPrivacy = Convert.ToString(obj.Data.ValueOrDefault<Object>("qstReqPrivacy", String.Empty))
        ?.Replace("qstPrivacy", String.Empty) ?? String.Empty;
      qst.CanAbandon = obj.Data.ValueOrDefault("qstAllowAbandonment", false);
      qst.Icon = (obj.Data.ValueOrDefault<String>("qstMissionIcon", String.Empty) ?? String.Empty).Replace(" ", String.Empty);
      qst.IsHidden = obj.Data.ValueOrDefault("qstIsHiddenQuest", false);
      qst.IsClassQuest = obj.Data.ValueOrDefault("qstIsClassQuest", false);
      qst.IsBonus = obj.Data.ValueOrDefault("qstIsBonusQuest", false);
      qst.BonusShareable = obj.Data.ValueOrDefault("qstIsBonusQuestShareable", false);
      qst.CategoryId = obj.Data.ValueOrDefault<long>("qstCategoryDisplayName", 0);
      //if (qst.CategoryId == 2466269005611293) { throw new IndexOutOfRangeException(); } // enable/disable to catch the data in the qst variable when QuestCategory.ToQuestCategory throws it's exception. To figure out which category it belongs to.
      StringTable Categories = _dom.StringTable.Find("str.gui.questcategories");
      try {
        qst.Category = Categories?.GetText(qst.CategoryId, "str.gui.questcategories") ?? String.Empty;
        qst.LocalizedCategory = Categories?.GetLocalizedText(qst.CategoryId, "str.gui.questcategories")
          ?? new Dictionary<string, string>();
      } catch (Exception ex) {
        Debug.WriteLine($"Quest category decode failed for {qst.Fqn}: {ex.Message}");
        qst.Category = String.Empty;
        qst.LocalizedCategory = new Dictionary<string, string>();
      }

      qst.ItemMap = obj.Data.ValueOrDefault<List<object>>("qstItemVariableDefinition_ProtoVarList", null);
      try { qst.Items = LoadItems(qst.ItemMap); }
      catch (Exception ex) { Debug.WriteLine($"Quest item variables failed for {qst.Fqn}: {ex.Message}"); qst.Items = new Dictionary<ulong, QuestItem>(); }

      qst.Rewards = new List<QuestReward>();
      qst.Branches = new List<QuestBranch>();
      qst.Classes = new ClassSpecList();
      try { LoadRewards(ref qst, obj); }
      catch (Exception ex) { Debug.WriteLine($"Quest rewards failed for {qst.Fqn}: {ex.Message}"); }
      try { LoadBranches(ref qst, obj); }
      catch (Exception ex) { Debug.WriteLine($"Quest branches failed for {qst.Fqn}: {ex.Message}"); }
      _ = obj.Data.ValueOrDefault<List<object>>("qstSimpleBoolVariableDefinition_ProtoVarList", null);
      _ = obj.Data.ValueOrDefault<List<object>>("qstStringIdVariableDefinition_ProtoVarList", null);
      try { LoadRequiredClasses(qst, obj); }
      catch (Exception ex) { Debug.WriteLine($"Quest class requirements failed for {qst.Fqn}: {ex.Message}"); }

      qst.NameId = questGuid + 0x58;
      if (textMap != null && TryGetQuestTextRetriever(textMap, qst.NameId, out GomObjectData nameLookup)) {
        // Jedipedia reads the quest title from qstQuestDefinitionGUID + 88 through
        // locTextRetrieverMap.  Do not index the dictionary directly here: newer
        // client builds can omit a retriever on internal/hidden quests and one such
        // node used to abort the complete quest export.
        try { qst.LocalizedName = _dom.StringTable.TryGetLocalizedStrings(qst.Fqn, nameLookup); }
        catch (Exception ex) {
          Debug.WriteLine($"Quest title retriever failed for {qst.Fqn}: {ex.Message}");
          qst.LocalizedName = new Dictionary<String, String>();
        }
      }
      if (qst.LocalizedName == null || qst.LocalizedName.Count == 0
          || qst.LocalizedName.Values.All(String.IsNullOrWhiteSpace)) {
        // Current clients also expose quest-variable strings in str.qst.  This is
        // the same fallback used by Jedipedia for the modern integer string ids.
        try { qst.LocalizedName = _dom.StringTable.TryGetLocalizedStrings("str.qst", qst.NameId); }
        catch (Exception ex) {
          Debug.WriteLine($"Quest title fallback failed for {qst.Fqn}: {ex.Message}");
          qst.LocalizedName = new Dictionary<String, String>();
        }
      }
      qst.LocalizedName = Normalize.Dictionary(qst.LocalizedName, qst.Fqn);
      if (!qst.LocalizedName.TryGetValue(GomLib.StringTable.SelectedLocalization, out String localizedName)
          || String.IsNullOrWhiteSpace(localizedName))
        localizedName = qst.LocalizedName.Values.FirstOrDefault(x => !String.IsNullOrWhiteSpace(x)) ?? qst.Fqn;
      qst.Name = localizedName;

      /*            List<string> strings2 = new List<string>();
                  foreach (var key in textMap.Keys)
                  {
                      var lookup = (GomObjectData)textMap[key];
                      var tempstring = _dom.stringTable.TryGetString(qst.Fqn, lookup);
                      strings2.Add(tempstring);
                  }
       */

      if (qst.Name.StartsWith("CUT", StringComparison.InvariantCulture)) {
        qst.IsHidden = true;
      }

      qst.CommandXP = obj.Data.ValueOrDefault<long>("qstCommandXP", 0);

      try { _dom.Assets.Icons.AddCodex(qst.Icon); } catch { }

      obj.Unload(); // These GomObjects are staying loaded in memory and cause our massive memory issues.
      return qst;
    }

    private Dictionary<ulong, QuestItem> LoadItems(List<object> items) {
      var parsedItems = new Dictionary<ulong, QuestItem>();
      if (items != null) {
        QuestItem parsedItem;
        foreach (object rawItem in items) {
          if (rawItem is not GomObjectData item) continue;
          parsedItem = new QuestItem { Dom_ = _dom };
          parsedItem.Id = item.ValueOrDefault<ulong>("qstItemSpecId", 0);
          parsedItem.Fqn = _dom.GetStoredTypeName(parsedItem.Id);

          parsedItem.Name = item.ValueOrDefault("qstVariableName", "");
          parsedItem.GUID = item.ValueOrDefault<long>("qstVariableGUID", 0);
          parsedItem.VariableId = item.ValueOrDefault<ulong>("qstVariableFqnId", 0);

          parsedItem.MaxCount = item.ValueOrDefault<long>("qstMaxCount", 0);
          parsedItem.Min = item.ValueOrDefault<long>("qstItemMin", 0);
          parsedItem.Max = item.ValueOrDefault<long>("qstItemMax", 0);

          if (parsedItem.VariableId != 0) parsedItems[parsedItem.VariableId] = parsedItem;
          /*if (obj != null)
          {
              switch(obj.Name.Substring(0, 3))
              {
                  case "itm":
                      Item itm = new Item();
                      ItemLoader.Load(itm, obj);
                      parsedItems.Add(itm);
                      break;
                  case "npc":
                      Npc npc = new Npc();
                      NpcLoader.Load(npc, obj);
                      parsedItems.Add(npc);
                      break;
                  case "abl":
                      Ability abl = new Ability();
                      AbilityLoader.Load(abl, obj);
                      parsedItems.Add(abl);
                      break;
                  case "spn":
                      break; //do nothing
                  default:
                      throw new NotImplementedException();
              }
          }*/
        }
      }
      return parsedItems;
    }

    private void LoadRequiredClasses(Quest qst, GomObject obj) {
      qst.Classes = new ClassSpecList();
      var reqClasses = obj.Data.ValueOrDefault<Dictionary<object, object>>("qstReqClasses", null);
      if (reqClasses != null) {
        foreach (var kvp in reqClasses) {
          if (!classSpecTable.TryGetValue((ulong)kvp.Key, out ClassSpec spec)) {
            spec = _dom.ClassSpecLoader.Load((ulong)kvp.Key);
            classSpecTable.Add((ulong)kvp.Key, spec);
          }
          var enabled = (bool)kvp.Value;
          if (enabled) {
            qst.Classes.Add(spec);
            //switch ((ulong)kvp.Key)
            //{
            //    case 16140902893827567561: qst.AllowedWarrior = true; break;
            //    case 16140943676484767978: qst.AllowedAgent = true; break;
            //    case 16141010271067846579: qst.AllowedInquisitor = true; break;
            //    case 16141170711935532310: qst.AllowedBH = true; break;

            //    case 16141179471541245792: qst.AllowedConsular = true; break;
            //    case 16140912704077491401: qst.AllowedSmuggler = true; break;
            //    case 16140973599688231714: qst.AllowedTrooper = true; break;
            //    case 16141119516274073244: qst.AllowedKnight = true; break;
            //}
          }
        }
      }
    }

    private static Boolean TryGetQuestTextRetriever(Dictionary<object, object> lookup, Int64 stringId,
                                                     out GomObjectData retriever) {
      retriever = null;
      if (lookup == null) return false;

      Object value;
      if (lookup.TryGetValue(stringId, out value)
          || lookup.TryGetValue(unchecked((UInt64)stringId), out value)
          || lookup.TryGetValue(stringId.ToString(), out value)) {
        retriever = value as GomObjectData;
        return retriever != null;
      }
      return false;
    }

    private static Dictionary<long, Dictionary<long, float>> ParseCreditRewardTable(Dictionary<object, object> raw) {
      var result = new Dictionary<long, Dictionary<long, float>>();
      if (raw == null) return result;
      foreach (KeyValuePair<object, object> outer in raw) {
        try {
          Int64 key = Convert.ToInt64(outer.Key);
          if (outer.Value is not Dictionary<object, object> levels) continue;
          var converted = new Dictionary<long, float>();
          foreach (KeyValuePair<object, object> level in levels) {
            try { converted[Convert.ToInt64(level.Key)] = Convert.ToSingle(level.Value); } catch { }
          }
          result[key] = converted;
        } catch { }
      }
      return result;
    }

    private static Dictionary<long, long> ParseExperienceTable(Dictionary<object, object> raw) {
      var result = new Dictionary<long, long>();
      if (raw == null) return result;
      foreach (KeyValuePair<object, object> entry in raw) {
        try { result[Convert.ToInt64(entry.Key)] = Convert.ToInt64(entry.Value); } catch { }
      }
      return result;
    }

    private void LoadRewards(ref Quest qst, GomObject obj) {
      if (fullQuestRewardsTable.Count == 0 && newfullQuestRewardsTable.Count == 0) {
        var proto = _dom.GetObject("qstRewardsInfoPrototype");
        if (proto != null) {
          fullQuestRewardsTable = proto.Data.ValueOrDefault("qstRewardsInfoData", new Dictionary<object, object>());
          newfullQuestRewardsTable = proto.Data.ValueOrDefault("qstRewardsNewInfoData", new Dictionary<object, object>());
        }
        proto = _dom.GetObject("qstrewardscreditsData");
        if (proto != null)
          fullCreditRewardsTable = ParseCreditRewardTable(
            proto.Data.ValueOrDefault<Dictionary<object, object>>("qstRewardsPerLevelData", null));
        proto = _dom.GetObject("qstExperiencePrototype");
        if (proto != null) {
          // qstExperienceTable was the old name.  Current GOM schemas (and Jedipedia)
          // call the same per-level map qstExperiencePerLevel.
          Dictionary<object, object> experience =
            proto.Data.ValueOrDefault<Dictionary<object, object>>("qstExperiencePerLevel", null)
            ?? proto.Data.ValueOrDefault<Dictionary<object, object>>("qstExperienceTable", null);
          experienceTable = ParseExperienceTable(experience);
        }
        //var qstExperienceMultiplierPrototype = _dom.GetObject("qstExperienceMultiplierPrototype");
        //if(qstExperienceMultiplierPrototype != null)
        //    experienceDifficultyMultiplierTable = qstExperienceMultiplierPrototype.Data.ValueOrDefault<Dictionary<object, object>>("qstExperienceMultiplierTable", new Dictionary<object, object>())
        //        .ToDictionary(x=> QuestDifficultyExtensions.ToQuestDifficulty((ScriptEnum)x.Key), x=> (float)x.Value);
        // Subscriber XP: base xp * difficulty multiplier * (1.2853 - level * .0012)
        // F2P XP: base xp * difficulty multiplier * (1.2573 - level * .0012)
      }
      //try
      //{
      bool newFormat = false;
      var questRewardsTable = new List<object>();

      if (fullQuestRewardsTable.ContainsKey(obj.Id)) {
        questRewardsTable = (List<object>)fullQuestRewardsTable[obj.Id];
      } else if (newfullQuestRewardsTable.ContainsKey(obj.Id)) {
        questRewardsTable = (List<object>)newfullQuestRewardsTable[obj.Id];
        newFormat = true;
      }
      //else
      //{
      //    return;
      //}
      qst.Rewards = new List<QuestReward>();

      foreach (GomObjectData qReward in questRewardsTable) {
        var reward = new QuestReward
                {
          Dom_ = _dom
        };

        long unknownNum = qReward.ValueOrDefault<long>("4611686090803470052", 0);
        /*if (unknownNum != 1) //obsolete debugging code
        {
            var t = ""; //checking if always 1. Order of appearance in listing?!?
        }*/
        reward.UnknownNum = unknownNum;

        bool isAlwaysProvided = qReward.ValueOrDefault("qstRewardIsAlwaysProvided", false);
        reward.IsAlwaysProvided = isAlwaysProvided;

        GomObjectData rewardLookup = newFormat
          ? qReward
          : qReward.ValueOrDefault<GomObjectData>("qstRewardData", qReward);

        /*Item rewardItem = new Item(); 
        rewardItem = _dom.itemLoader.Load(rewardLookup.ValueOrDefault<ulong>("qstRewardItemId", 0));
        reward.RewardItem = rewardItem;*/
        reward.RewardItemId = rewardLookup.ValueOrDefault<ulong>("qstRewardItemId", 0);

        Dictionary<object, object> classes = rewardLookup.ValueOrDefault(
          "qstRewardRequiredClasses", new Dictionary<object, object>());
        reward.Classes = new ClassSpecList();
        foreach (var classLookup in classes) {
          ClassSpec classy = _dom.ClassSpecLoader.Load((ulong)classLookup.Key);
          reward.Classes.Add(classy);
          /*if (!(bool)classLookup.Value)//obsolete debugging code
          {
              var t = "";
          }*/
        }

        //Default to -1 if the data isn't properly set but set quantity to 0.
        reward.NumberOfItem = rewardLookup.ValueOrDefault<long>("qstRewardItemQty", 0);
        reward.MinLevel = rewardLookup.ValueOrDefault<long>("qstRewardMinLevel", -1);
        reward.MaxLevel = rewardLookup.ValueOrDefault<long>("qstRewardMaxLevel", -1);

        reward.Id = Convert.ToUInt64(reward.IsAlwaysProvided) + reward.RewardItemId + Convert.ToUInt64(reward.NumberOfItem);
        qst.Rewards.Add(reward);
      }
      qst.CreditRewardType = obj.Data.ValueOrDefault<long>("creditRewardType", 0);
      if (qst.CreditRewardType != 0
          && fullCreditRewardsTable.TryGetValue(qst.CreditRewardType, out Dictionary<long, float> creditByLevel)
          && creditByLevel.TryGetValue(qst.XpLevel, out Single credits)) {
        qst.CreditsRewarded = credits;
      }
      if (false) //qst.XpLevel != 0)
      {
      }
    }

    private void LoadBranches(ref Quest qst, GomObject obj) {
      var branches = obj.Data.ValueOrDefault<List<object>>("qstBranches", null);
      qst.Branches = new List<QuestBranch>();
      if (branches != null) {
        foreach (var br in branches) {
          if (br is not GomObjectData branchData) continue;
          try {
            QuestBranch branch = _dom.QuestBranchLoader.Load(branchData, qst);
            if (branch == null) continue;
            branch.Quest = qst;
            qst.Branches.Add(branch);
          } catch (Exception ex) {
            Debug.WriteLine($"Quest branch failed for {qst?.Fqn}: {ex.Message}");
          }
        }
      }
    }

    public static List<ulong> LoadBonusMissions(List<object> bonuses) {
      var bonusMissions = new List<ulong>();
      if (bonuses != null) {
        foreach (var bonus in bonuses) {
          if (bonus is not GomObjectData bonusData) continue;
          var bonusMissionId = bonusData.ValueOrDefault<ulong>("qstTaskBonusMissionNodeId", 0);
          /*var bonusMission = _dom.questLoader.Load(bonusMissionId);
          if (bonusMission.Fqn != null)
          {
              bonusMissions.Add(bonusMission);
          }*/
          if (bonusMissionId != 0) bonusMissions.Add(bonusMissionId);
        }
      }
      return bonusMissions;
    }

    public List<QuestItem> LoadGivenOrTakenItems(Quest qst, List<object> items) {
      var itemsGivenOrTaken = new List<QuestItem>();

      // Completion-item lists are optional in modern qstStep/qstTask data.  The old
      // loader dereferenced null here, which meant a single ordinary task could make
      // every quest fail to load and the main Extract -> Quests command returned 0.
      if (items != null && items.Count > 0) {
        foreach (var item in items) {
          UInt64 itemVariableId;
          try { itemVariableId = Convert.ToUInt64(item); }
          catch { continue; }
          //var itemObj = DataObjectModel.GetObject(itemVariableId);
          if (qst.Items != null && qst.Items.ContainsKey(itemVariableId)) //(itemObj == null)
          {
            if (qst.ItemMap == null) {
              Debug.WriteLine(qst.Fqn + " 's item Map == null. But the quest has items. Test Quest?");
              continue;
            }
            itemsGivenOrTaken.Add(qst.Items[itemVariableId]);
            /*var itemId = qst.ItemMap.Where(x => (ulong)((GomObjectData)x).Dictionary["qstVariableFqnId"] == (ulong)item)
                .Select(y => ((GomObjectData)y).ValueOrDefault<ulong>("qstItemSpecId", 0));

            if (itemId.Count() == 1 && itemId.Single() != 0 && qst.Items.Count > 0)
            {
                var potentialItems = qst.Items.Where(x => x.GetType() == typeof(GomLib.Models.Item))
                    .Where(x => ((Item)x).NodeId == itemId.Single());
                if (potentialItems.Count() == 1)
                {
                    var parsedItem = (Item)potentialItems.Single();
                    itemsGivenOrTaken.Add(parsedItem);
                }
                else if (itemId.Count() > 1)
                {
                    throw new IndexOutOfRangeException();
                }

                var potentialNpcs = qst.Items.Where(x => x.GetType() == typeof(GomLib.Models.Npc))
                    .Where(x => ((Npc)x).NodeId == itemId.Single());
                if (potentialNpcs.Count() == 1)
                {
                    var parsedNpc = (Npc)potentialNpcs.Single();
                    itemsGivenOrTaken.Add(parsedNpc);
                }
                else if (itemId.Count() > 1)
                {
                    throw new IndexOutOfRangeException();
                }

                var potentialAbilities = qst.Items.Where(x => x.GetType() == typeof(GomLib.Models.Ability))
                    .Where(x => ((Ability)x).NodeId == itemId.Single());
                if (potentialAbilities.Count() == 1)
                {
                    var parsedAbility = (Ability)potentialAbilities.Single();
                    itemsGivenOrTaken.Add(parsedAbility);
                }
                else if (itemId.Count() > 1)
                {
                    throw new IndexOutOfRangeException();
                }
            }
            else if (itemId.Count() > 1)
            {
                throw new IndexOutOfRangeException();
            }
            else
            {
                Debug.WriteLine("Test Quest?");
            }*/
          } else {
            /*var itemObj = DataObjectModel.GetObject((ulong)item);
            Item itm = new Item();
            ItemLoader.Load(itm, itemObj);*/
            var questItem = new QuestItem
                        {
              Dom_ = _dom,
              Id = itemVariableId
            };
            questItem.Fqn = _dom.GetStoredTypeName(questItem.Id);

            questItem.Name = "item";
            questItem.VariableId = itemVariableId;

            questItem.MaxCount = 1;
            questItem.Min = 1;
            questItem.Max = 1;
            itemsGivenOrTaken.Add(questItem);
          }
        }
      }
      return itemsGivenOrTaken;
    }

    public void LoadObject(GameObject loadMe, GomObject obj) {
      Quest qst = (Quest)loadMe;
      Load(qst, obj);
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
