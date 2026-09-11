using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;

using Newtonsoft.Json;
using TorArchive;

namespace GomLib.Models {
  public class Quest : GameObject, IEquatable<Quest> {

    #region Constructors
    #endregion Constructors

    #region Fields
    // private List<Quest> m_bonusMissions;
    // [JsonIgnore] private List<String> m_bonusMissionsB62Ids;
    // [JsonIgnore] private readonly List<UInt64> m_bonusMissionsIds;
    [JsonIgnore] private List<String> m_branchIds;
    [JsonIgnore] private List<String> m_classesAllowed;
    [JsonIgnore] private List<String> m_classesB62;
    [JsonIgnore] private QuestAffectionGainTable m_conversationGains;
    [JsonIgnore] internal Dictionary<String, XElement> m_loadedNpcs;
    [JsonIgnore] private Dictionary<String, XElement> m_loadedQuests;
    private SortedSet<UInt64> m_questsNext;
    private List<String> m_questsNextB62;
    private SortedSet<UInt64> m_questsPrevious;
    private List<String> m_questsPreviousB62;
    [JsonIgnore] private List<String> m_rewardIds;

    #endregion Fields

    #region Methods
    private static void AddQuestAffectionGains(String reference,
                                               List<UInt64> ConvosParsed,
                                               Quest qst) {
      foreach (UInt64 convoKey in qst.References[reference]) {
        if (ConvosParsed.Contains(convoKey)) {
          continue;
        }

        Conversation convo = qst.Dom_.ConversationLoader.Load(convoKey);

        if (convo == null) {
          continue;
        }

        ConvosParsed.Add(convoKey);
        IEnumerable<DialogNode> dNodes =
          convo.DialogNodes.Where(x => x.IsPlayerNode)
                           .Where(x => x.AffectionRewardEvents.Count > 0);

        foreach (DialogNode dNode in dNodes) {
          Dictionary<Npc, KeyValuePair<Int32, String>> affects = dNode.AffectionRewards;
          String NodeLookupId = $"{convo.Base62Id}_{dNode.NodeId}";
          qst.m_conversationGains.NodeText.Add(NodeLookupId, dNode.LocalizedText);
          qst.m_conversationGains.AffectionGainTable.Add(NodeLookupId,
                                                         new List<QuestAffectionGain>());

          foreach (KeyValuePair<Npc, KeyValuePair<Int32, String>> kvp in affects) {
            if (kvp.Key == null) {
              continue;
            }

            if (!qst.m_conversationGains.Companions.ContainsKey(kvp.Key.Base62Id)) {
              Npc tempc = kvp.Key;

              if (tempc.LocalizedName[GomLib.StringTable.SelectedLocalization] == "Jaesa Willsaam") {
                if (kvp.Key.Fqn == "npc.companion.sith_warrior.jaesa_dark") {
                  tempc.LocalizedName[GomLib.StringTable.SelectedLocalization] = "Jaesa Willsaam (Dark)";
                } else {
                  tempc.LocalizedName[GomLib.StringTable.SelectedLocalization] = "Jaesa Willsaam (Light)";
                }
              }

              qst.m_conversationGains.Companions.Add(kvp.Key.Base62Id, tempc);
            }

            QuestAffectionGain qag = new QuestAffectionGain(kvp.Key.Base62Id, kvp.Value.Key);
            qst.m_conversationGains.AffectionGainTable[NodeLookupId].Add(qag);
          }
        }
      }
    }

    public override Boolean Equals(Object obj) {
      if (obj == null) return false;
      if (ReferenceEquals(this, obj)) return true;
      if (obj is not Quest qst) return false;
      return Equals(qst);
    }

    public Boolean Equals(Quest qst) {
      if (qst == null) return false;

      if (ReferenceEquals(this, qst)) return true;

      // if (m_bonusMissionsIds != null) {
      //   if (qst.m_bonusMissionsIds == null) {
      //     return false;
      //   } else {
      //     if (!Enumerable.SequenceEqual(m_bonusMissionsIds, qst.m_bonusMissionsIds))
      //       return false;
      //   }
      // }

      if (BonusShareable != qst.BonusShareable)
        return false;

      if (Branches != null) {
        if (qst.Branches == null) {
          return false;
        } else {
          if (!Enumerable.SequenceEqual(Branches, qst.Branches))
            return false;
        }
      }

      if (CanAbandon != qst.CanAbandon)
        return false;

      if (!String.Equals(Category, qst.Category, StringComparison.Ordinal)) {
        return false;
      }

      if (CategoryId != qst.CategoryId)
        return false;

      if (Classes != null) {
        if (qst.Classes == null) {
          return false;
        } else {
          if (!Classes.Equals(qst.Classes, false))
            return false;
        }
      }

      if (!String.Equals(Difficulty, qst.Difficulty, StringComparison.Ordinal))
        return false;

      if (Fqn != qst.Fqn)
        return false;

      if (Icon != qst.Icon)
        return false;

      if (Id != qst.Id)
        return false;

      if (IsBonus != qst.IsBonus)
        return false;

      if (IsClassQuest != qst.IsClassQuest)
        return false;

      if (IsDaily != qst.IsDaily)
        return false;

      if (IsHidden != qst.IsHidden)
        return false;

      if (IsRepeatable != qst.IsRepeatable)
        return false;

      var uQIComp = new DictionaryComparer<UInt64, QuestItem>();

      if (!uQIComp.Equals(Items, qst.Items))
        return false;

      var ssComp = new DictionaryComparer<String, String>();

      if (!ssComp.Equals(LocalizedName, qst.LocalizedName))
        return false;

      if (Name != qst.Name)
        return false;

      if (NodeId != qst.NodeId)
        return false;

      if (ReqPrivacy != qst.ReqPrivacy)
        return false;

      if (RequiredLevel != qst.RequiredLevel)
        return false;

      if (Rewards != null) {
        if (qst.Rewards == null) {
          return false;
        } else {
          if (!Enumerable.SequenceEqual(Rewards, qst.Rewards))
            return false;
        }
      }

      if (XpLevel != qst.XpLevel)
        return false;

      return true;
    }

    public override Int32 GetHashCode() {
      Int32 hash = (Name ?? String.Empty).GetHashCode();

      if (Icon != null) {
        hash ^= Icon.GetHashCode();
      }

      hash ^= IsRepeatable.GetHashCode();
      hash ^= RequiredLevel.GetHashCode();
      hash ^= XpLevel.GetHashCode();
      hash ^= (Difficulty ?? String.Empty).GetHashCode();
      hash ^= CanAbandon.GetHashCode();
      hash ^= IsHidden.GetHashCode();
      hash ^= IsClassQuest.GetHashCode();
      hash ^= IsBonus.GetHashCode();
      hash ^= BonusShareable.GetHashCode();
      hash ^= (Category ?? String.Empty).GetHashCode();

      foreach (QuestBranch branch in Branches ?? new List<QuestBranch>()) {
        if (branch != null) hash ^= branch.GetHashCode();
      }

      foreach (ClassSpec classSpec in Classes ?? new ClassSpecList()) {
        if (classSpec != null) hash ^= classSpec.Id.GetHashCode();
      }

      return hash;
    }

    public static void QuestItemsGivenOrTakenToXElement(XElement questNode,
                                                        List<QuestItem> givenItems,
                                                        List<QuestItem> takenItems) {
      XElement itemsGiven = QuestItemListToXElement("ItemsGiven", givenItems);
      XElement itemsTaken = QuestItemListToXElement("itemsTaken", takenItems);

      questNode.Add(itemsGiven, itemsTaken);
    }

    private static XElement QuestItemListToXElement(String elementName, List<QuestItem> Items) {
      XElement itemsElement = new XElement(elementName);
      if (Items != null) {
        if (Items.Count != 0) {
          itemsElement.Add(new XAttribute("Id", Items.Count));
          for (var i = 0; i < Items.Count; i++) {
            var item = Items.ElementAt(i);
            XElement questItem = item.ToXElement(true);
            itemsElement.Add(questItem);
          }
        }
      }
      return itemsElement;
    }

    public override String ToString(Boolean verbose) {
      String n = Environment.NewLine;
      var txtFile = new StringBuilder();

      txtFile.Append("------------------------------------------------------------" + n);
      txtFile.Append("Quest Name: " + Name + n);
      txtFile.Append("Quest NodeId: " + NodeId + n);
      txtFile.Append("Quest Id: " + Id + n);
      txtFile.Append("------------------------------------------------------------" + n);
      txtFile.Append("Quest INFO" + n);
      txtFile.Append("  IsBonus: " + IsBonus + n);
      txtFile.Append("  BonusShareable: " + BonusShareable + n);
      txtFile.Append("  Branches: " + Branches.ToList().ToString() + n);
      txtFile.Append("  CanAbandon: " + CanAbandon + n);
      txtFile.Append("  Category: " + Category + n);
      txtFile.Append("  CategoryId: " + CategoryId + n);
      txtFile.Append("  Classes: " + Classes.ToList().ToString() + n);
      txtFile.Append("  Difficulty: " + Difficulty + n);
      txtFile.Append("  Fqn: " + Fqn + n);
      txtFile.Append("  Icon: " + Icon + n);
      txtFile.Append("  IsClassQuest: " + IsClassQuest + n);
      txtFile.Append("  IsDaily: " + IsDaily + n);
      txtFile.Append("  IsHidden: " + IsHidden + n);
      txtFile.Append("  IsRepeatable: " + IsRepeatable + n);
      txtFile.Append("  Items: " + Items + n);
      txtFile.Append("  RequiredLevel: " + RequiredLevel + n);
      txtFile.Append("  XpLevel: " + XpLevel + n);
      txtFile.Append("------------------------------------------------------------" + n + n);

      return txtFile.ToString();
    }

    public override XElement ToXElement(Boolean verbose) {
      var questNode = new XElement("Quest", new XElement("Name", Name),
                //new XAttribute("Name", itm.Name),
                new XElement("Fqn", Fqn,
                    new XAttribute("Id", NodeId)),
                new XAttribute("Id", Id),
                new XElement("Category", Category,
                    new XAttribute("Id", CategoryId)),
                new XElement("RequiredLevel", RequiredLevel),
                new XElement("XpLevel", XpLevel));
      if (verbose) {
        //Intialize our repeat XElement holders for this quest.
        m_loadedNpcs = new Dictionary<String, XElement>();
        m_loadedQuests = new Dictionary<String, XElement>();

        questNode.Add(
        //new XAttribute("Hash", itm.GetHashCode()),
        new XElement("IsBonus", IsBonus),
        new XElement("BonusShareable", BonusShareable),
        new XElement("CanAbandon", CanAbandon),
        new XElement("IsClassQuest", IsClassQuest),
        new XElement("IsDaily", IsDaily),
        new XElement("IsHidden", IsHidden),
        new XElement("IsRepeatable", IsRepeatable),
        new XElement("Difficulty", Difficulty),
        new XElement("Icon", Icon),
        new XElement("RequiredPrivacy", ReqPrivacy));
        String classString = null;
        if (Classes != null) {
          foreach (var classy in Classes) {
            classString += classy.Name + ", ";
          }
          if (classString != null) { classString = classString[0..^2]; }
        }
        questNode.Add(new XElement("Classes", classString));

        XElement questItems = new XElement("Items");
        if (Items != null) {
          questItems.Add(new XAttribute("Id", Items.Count));
          foreach (var item in Items) {
            if (item.Value != null) {
              questItems.Add(item.Value.ToXElement(true));
            }
          }
        }
        questNode.Add(questItems);


        //XElement rewards = new XElement("Rewards");
        //int r = 1;
        if (Rewards != null) {
          foreach (var rewardEntry in Rewards.OrderBy(x => x.RewardItemId)) {
            try {
              if (rewardEntry.RewardItem != null) questNode.Add(rewardEntry.ToXElement(verbose));
            } catch (Exception ex) {
              Debug.WriteLine($"Quest reward output failed for {Fqn}: {ex.Message}");
            }
          }
        }
        //questNode.Add(rewards);

        foreach (var branch in Branches ?? new List<QuestBranch>()) {
          try {
            XElement branchNode = branch.ToXElement(verbose);
            questNode.Add(branchNode);
          } catch (Exception ex) {
            Debug.WriteLine($"Quest branch output failed for {Fqn}: {ex.Message}");
          }
        }
        //Trash our repeat XElement holders
        m_loadedNpcs = null;
        m_loadedQuests = null;
      }
      return questNode;
    }

    #endregion Methods

    #region Properties
    [JsonIgnore]
    public String AllowedClasses =>
      Classes == null ? "" : String.Join(',', Classes.Select(x => x.Name).ToList());

    // [JsonIgnore]
    // internal List<Quest> BonusMissions {
    //   get {
    //     m_bonusMissions ??= new List<Quest>();
    //     foreach (UInt64 Id in m_bonusMissionsIds) {
    //       m_bonusMissions.Add(Dom_.QuestLoader.Load(Id));
    //     }
    //     return m_bonusMissions;
    //   }
    // }
    // [JsonIgnore]
    // internal List<String> BonusMissionsB62Ids =>
    //   m_bonusMissionsB62Ids ??= (m_bonusMissionsIds?.Select(x => x.ToMaskedBase62()).ToList()
    //                              ?? new List<String>());

    internal Boolean BonusShareable { get; set; }
    public Int32 BranchCount => Branches.Count;
    [JsonIgnore]
    public List<String> BranchIds =>
      m_branchIds ??= Branches.Select(x => x.Id.ToMaskedBase62()).ToList();
    internal List<QuestBranch> Branches { get; set; }
    internal Boolean CanAbandon { get; set; }
    [JsonIgnore] internal String Category { get; set; }
    [JsonConverter(typeof(LongConverter))] internal Int64 CategoryId { get; set; }
    [JsonIgnore] internal ClassSpecList Classes { get; set; }
    public List<String> ClassesAllowed =>
      m_classesAllowed ??= (Classes?.Select(x => x.Name).ToList() ?? new List<String>());
    public List<String> ClassesB62 =>
      m_classesB62 ??= (Classes?.Select(x => x.Base62Id).ToList() ?? new List<String>());
    [JsonIgnore]
    public String CleanName =>
      String.IsNullOrEmpty(Name)
        ? "Unnamed_Quest"
        : Path.GetInvalidFileNameChars().Aggregate(Name, (cur, chr) => cur.Replace($"{chr}", ""))
                                        .Replace("'", "").Replace(" ", "_");
    internal Int64 CommandXP { get; set; }
    public QuestAffectionGainTable ConversationGains {
      get {
        List<UInt64> convosParsed = new List<UInt64>();
        m_conversationGains ??= new QuestAffectionGainTable();

        if (References != null) {
          if (References.ContainsKey("conversationEnds")) {
            AddQuestAffectionGains("conversationEnds", convosParsed, this);
          }
          if (References.ContainsKey("conversationProgresses")) {
            AddQuestAffectionGains("conversationProgresses", convosParsed, this);
          }
          if (References.ContainsKey("conversationStarts")) {
            AddQuestAffectionGains("conversationStarts", convosParsed, this);
          }
        }

        return m_conversationGains;
      }
    }
    [JsonConverter(typeof(LongConverter))] internal Int64 CreditRewardType { get; set; }
    internal Single CreditsRewarded { get; set; }
    internal String Difficulty { get; set; }
    internal Int64 F2PXP { get; set; }
    public String HashedIcon {
      get {
        FileId fileId = FileId.FromFilePath($"/resources/gfx/codex/{Icon}.dds");
        return $"{fileId.Ph}_{fileId.Sh}";
      }
    }
    public String Icon { get; set; }
    internal Boolean IsBonus { get; set; }
    internal Boolean IsClassQuest { get; set; }
    public Boolean IsDaily => Name.Contains("[DAILY]");
    internal Boolean IsHidden { get; set; }
    internal Boolean IsRepeatable { get; set; }
    [JsonIgnore] internal List<Object> ItemMap { get; set; }
    internal Dictionary<UInt64, QuestItem> Items { get; set; }
    internal Dictionary<String, String> LocalizedCategory { get; set; }
    internal Dictionary<String, String> LocalizedName { get; set; }
    internal String Name { get; set; }
    [JsonConverter(typeof(LongConverter))] internal Int64 NameId { get; set; }
    [JsonIgnore] internal UInt64 NodeId { get; set; }
    internal SortedSet<UInt64> QuestsNext {
      get {
        m_questsNext ??= new SortedSet<UInt64>();

        if (References != null && References.ContainsKey("conversationEnds")) {
          foreach (UInt64 cnvId in References["conversationEnds"]) {
            GomObject cnvObj = Dom_.GetObject(cnvId);
            if (cnvObj.References != null && cnvObj.References.ContainsKey("startsQuest")) {
              m_questsNext = cnvObj.References["startsQuest"];
            }
          }
        }

        return m_questsNext;
      }
    }
    public List<String> QuestsNextB62 =>
      m_questsNextB62 ??= (QuestsNext?.Select(x => x.ToMaskedBase62()).ToList()
                           ?? new List<String>());
    private SortedSet<UInt64> QuestsPrevious {
      get {
        m_questsPrevious ??= new SortedSet<UInt64>();
        if (References != null && References.ContainsKey("conversationStarts")) {
          foreach (UInt64 cnvId in References["conversationStarts"]) {
            GomObject cnvObj = Dom_.GetObject(cnvId);
            if (cnvObj.References != null && cnvObj.References.ContainsKey("endsQuest")) {
              m_questsPrevious = cnvObj.References["endsQuest"];
            }
          }
        }

        return m_questsPrevious;
      }
    }
    public List<String> QuestsPreviousB62 =>
      m_questsPreviousB62 ??= (QuestsPrevious?.Select(x => x.ToMaskedBase62()).ToList()
                               ?? new List<String>());
    internal String ReqPrivacy { get; set; }
    internal Int32 RequiredLevel { get; set; }
    [JsonIgnore]
    public List<String> RewardIds =>
      m_rewardIds ??= (Rewards?.Select(x => x.Id.ToMaskedBase62()).ToList() ?? new List<String>());
    internal List<QuestReward> Rewards { get; set; }
    public override List<SQLProperty> SQLProperties => new List<SQLProperty> { 
      //(SQL Column Name, C# Property Name, SQL Column type statement, isUnique/PrimaryKey, Serialize value to json)
      new SQLProperty("Name", "Name", "varchar(255) COLLATE utf8_unicode_ci NOT NULL", SQLPropSetting.AddIndex),
      new SQLProperty("FrName", "LocalizedName[frMale]", "varchar(255) COLLATE utf8_unicode_ci NOT NULL", SQLPropSetting.AddIndex),
      new SQLProperty("DeName", "LocalizedName[deMale]", "varchar(255) COLLATE utf8_unicode_ci NOT NULL", SQLPropSetting.AddIndex),
      new SQLProperty("CleanName", "CleanName", "varchar(255) COLLATE utf8_unicode_ci NOT NULL", SQLPropSetting.AddIndex),
      new SQLProperty("Base62Id", "Base62Id", "varchar(7) COLLATE latin1_general_cs NOT NULL", SQLPropSetting.PrimaryKey),
      new SQLProperty("Fqn", "Fqn", "varchar(255) COLLATE utf8_unicode_ci NOT NULL"),
      new SQLProperty("Icon", "HashedIcon", "varchar(255) COLLATE utf8_unicode_ci NOT NULL"),
      new SQLProperty("IsRepeatable", "IsRepeatable", "tinyint(1) NOT NULL", SQLPropSetting.AddIndex),
      new SQLProperty("RequiredLevel", "RequiredLevel", "int(11) NOT NULL", SQLPropSetting.AddIndex),
      new SQLProperty("XpLevel", "XpLevel", "int(11) NOT NULL", SQLPropSetting.AddIndex),
      new SQLProperty("XP", "XP", "int(11) NOT NULL"),
      new SQLProperty("Difficulty", "Difficulty", "varchar(255) COLLATE utf8_unicode_ci NOT NULL", SQLPropSetting.AddIndex),
      new SQLProperty("CanAbandon", "CanAbandon", "tinyint(1) NOT NULL", SQLPropSetting.AddIndex),
      new SQLProperty("IsHidden", "IsHidden", "tinyint(1) NOT NULL", SQLPropSetting.AddIndex),
      new SQLProperty("IsClassQuest", "IsClassQuest", "tinyint(1) NOT NULL", SQLPropSetting.AddIndex),
      new SQLProperty("IsDaily", "IsDaily", "tinyint(1) NOT NULL", SQLPropSetting.AddIndex),
      new SQLProperty("IsBonus", "IsBonus", "tinyint(1) NOT NULL", SQLPropSetting.AddIndex),
      new SQLProperty("BonusShareable", "BonusShareable", "tinyint(1) NOT NULL", SQLPropSetting.AddIndex),
      new SQLProperty("Category", "Category", "varchar(255) COLLATE latin1_general_cs NOT NULL", SQLPropSetting.AddIndex),
      new SQLProperty("FrCategory", "LocalizedCategory[frMale]", "varchar(255) COLLATE utf8_unicode_ci NOT NULL", SQLPropSetting.AddIndex),
      new SQLProperty("DeCategory", "LocalizedCategory[deMale]", "varchar(255) COLLATE utf8_unicode_ci NOT NULL", SQLPropSetting.AddIndex),
      new SQLProperty("BranchCount", "BranchCount", "int(11) NOT NULL"),
      new SQLProperty("Branches", "Branches", "TEXT NOT NULL", SQLPropSetting.JsonSerialize),
      //new SQLProperty("Items", "Items", "TEXT NOT NULL", false, true),
      new SQLProperty("Classes", "ClassesB62", "varchar(255) COLLATE latin1_general_cs NOT NULL", SQLPropSetting.JsonSerialize),
      new SQLProperty("RewardIds", "RewardIds", "varchar(255) COLLATE latin1_general_cs NOT NULL", SQLPropSetting.JsonSerialize),
      new SQLProperty("Rewards", "Rewards", "TEXT NOT NULL", SQLPropSetting.JsonSerialize),
      new SQLProperty("CreditsRewarded", "CreditsRewarded", "int(11) NOT NULL"),
      new SQLProperty("ReqPrivacy", "ReqPrivacy", "varchar(255) COLLATE latin1_general_cs NOT NULL"),
      new SQLProperty("BonusMissionsIds", "BonusMissionsB62Ids", "TEXT NOT NULL", SQLPropSetting.JsonSerialize),
      new SQLProperty("ConversationGains","ConversationGains", "TEXT NOT NULL", SQLPropSetting.JsonSerialize),
      new SQLProperty("AllowedClasses", "AllowedClasses", "varchar(505) NOT NULL", SQLPropSetting.JsonSerialize, SQLPropSetting.AddFullTextIndex),
      //new SQLProperty("ItemMap", "ItemMap", "TEXT NOT NULL", false, true),
    };
    internal Int64 SubXP { get; set; }
    internal Dictionary<Object, Object> TextLookup { get; set; }
    internal Int64 XP { get; set; }
    internal Int32 XpLevel { get; set; }

    #endregion Properties

  }

  //---------------------------------------------------------------------------------------------//

  public class QuestReward : GameObject, IEquatable<QuestReward> {
    public Int64 UnknownNum { get; set; }
    public Boolean IsAlwaysProvided { get; set; }
    [JsonIgnore]
    public Item RewardItem {
      get { return Dom_.ItemLoader.Load(RewardItemId); }
    }
    public new String Base62Id => RewardItemId.ToMaskedBase62();
    [JsonIgnore]
    public UInt64 RewardItemId { get; set; }
    #region Classes
    [JsonIgnore]
    public ClassSpecList Classes { get; set; }
    [JsonIgnore]
    internal List<String> ClassesB62_ { get; set; }
    public List<String> ClassesB62 {
      get {
        if (ClassesB62_ == null) {
          if (Classes == null) return new List<String>();
          ClassesB62_ = Classes.Select(x => x.Base62Id).ToList();
        }
        return ClassesB62_;
      }
    }
    #endregion
    public Int64 NumberOfItem { get; set; }
    public Int64 MinLevel { get; set; }
    public Int64 MaxLevel { get; set; }
    public override List<SQLProperty> SQLProperties { get => base.SQLProperties; set => base.SQLProperties = value; }

    public override Boolean Equals(Object obj) {
      if (obj == null) return false;

      if (ReferenceEquals(this, obj)) return true;

      if (obj is not QuestReward qsr) return false;

      return Equals(qsr);
    }

    public Boolean Equals(QuestReward qsr) {
      if (qsr == null) return false;

      if (ReferenceEquals(this, qsr)) return true;

      if (Classes != null) {
        if (qsr.Classes == null) {
          return false;
        } else {
          if (!Classes.Equals(qsr.Classes, false))
            return false;
        }
      }
      if (Fqn != qsr.Fqn)
        return false;
      if (Id != qsr.Id)
        return false;
      if (IsAlwaysProvided != qsr.IsAlwaysProvided)
        return false;
      if (MaxLevel != qsr.MaxLevel)
        return false;
      if (MinLevel != qsr.MinLevel)
        return false;
      if (NumberOfItem != qsr.NumberOfItem)
        return false;
      //if (this.RewardItem.Equals(qsr.RewardItem))
      //return false;
      if (RewardItemId != qsr.RewardItemId)
        return false;
      if (UnknownNum != qsr.UnknownNum)
        return false;
      return true;
    }

    public override XElement ToXElement(Boolean verbose) {
      XElement reward = new XElement("Reward", new XAttribute("Id", Id));
      if (verbose) {
        reward.Add(new XElement("IsAlwaysProvided", IsAlwaysProvided),
        new XElement("NumberProvided", NumberOfItem),
        new XElement("MinLevel", MinLevel),
        new XElement("MaxLevel", MaxLevel));

        XElement clas = new XElement("Classes");
        foreach (var c in Classes) {
          clas.Add(new XElement("Class", c.Name, new XAttribute("Id", c.Id)));
        }
        reward.Add(clas);
      }
      reward.Add(RewardItem.ToXElement(true));
      return reward;
    }

    public override Int32 GetHashCode() {
      return base.GetHashCode();
    }

    public override String ToJSON() {
      return base.ToJSON();
    }

    public override String ToString() {
      return base.ToString();
    }

    public override String ToString(Boolean verbose) {
      return base.ToString(verbose);
    }

    public override XElement ToXElement(GomObject gomItm) {
      return base.ToXElement(gomItm);
    }

    public override XElement ToXElement(GomObject gomItm, Boolean verbose) {
      return base.ToXElement(gomItm, verbose);
    }

    public override XElement ToXElement(UInt64 nodeId, DataObjectModel dom) {
      return base.ToXElement(nodeId, dom);
    }

    public override XElement ToXElement(UInt64 nodeId, DataObjectModel dom, Boolean verbose) {
      return base.ToXElement(nodeId, dom, verbose);
    }

    public override XElement ToXElement(String fqn, DataObjectModel dom) {
      return base.ToXElement(fqn, dom);
    }

    public override XElement ToXElement(String fqn, DataObjectModel dom, Boolean verbose) {
      return base.ToXElement(fqn, dom, verbose);
    }

    public override XElement ToXElement() {
      return base.ToXElement();
    }

    public override HashSet<String> GetDependencies() {
      return base.GetDependencies();
    }
  }
  public class QuestAffectionGain {
    public QuestAffectionGain(String compId, Int32 gain) {
      CompanionId = compId;
      AffectionGainType = gain;
    }
    public String CompanionId { get; set; }
    public Int32 AffectionGainType { get; set; }
  }
  public class QuestAffectionGainTable {
    public QuestAffectionGainTable() {
      Companions = new Dictionary<String, Npc>();
      NodeText = new Dictionary<String, Dictionary<String, String>>();
      AffectionGainTable = new Dictionary<String, List<QuestAffectionGain>>();
    }

    [JsonIgnore]
    public Dictionary<String, Npc> Companions { get; set; }
    [JsonIgnore]
    internal Dictionary<String, Dictionary<String, String>> CompanionsParsed_ { get; set; }
    public Dictionary<String, Dictionary<String, String>> CompanionsParsed {
      get {
        if (CompanionsParsed_ == null) {
          CompanionsParsed_ = new Dictionary<String, Dictionary<String, String>>();
          if (Companions != null) {
            foreach (var comp in Companions) {
              CompanionsParsed_.Add(comp.Key, comp.Value.LocalizedName);
            }
          }
        }
        return CompanionsParsed_;
      }
    }
    public Dictionary<String, Dictionary<String, String>> NodeText { get; set; }
    public Dictionary<String, List<QuestAffectionGain>> AffectionGainTable { get; set; }
  }
}
