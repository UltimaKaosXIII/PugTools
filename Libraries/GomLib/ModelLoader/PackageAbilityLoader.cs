using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace GomLib.ModelLoader {
  public class PackageAbilityLoader {
    StringTable strTable;
    readonly DataObjectModel _dom;

    public PackageAbilityLoader(DataObjectModel dom) {
      _dom = dom;
      Flush();
    }

    public void Flush() {
      strTable = null;
    }

    public Models.PackageAbility Load(GomObjectData gomObj) {
      if (strTable == null) {
        strTable = _dom.StringTable.Find("str.abl.player.skill_trees");
      }

      Models.PackageAbility result = new Models.PackageAbility
            {
        _dom = _dom,
        AbilityId = gomObj.ValueOrDefault<ulong>("ablAbilityDataSpec", 0),
        AutoAcquire = gomObj.ValueOrDefault("ablAbilityDataAutoAcquire", false),
        PackageId = gomObj.ValueOrDefault<ulong>("ablAbilityDataPackage", 0)
      };
      List<object> ranks = gomObj.ValueOrDefault<List<object>>("ablAbilityDataRanks", null);
      if (ranks != null) foreach (var rank in ranks) {
        try { result.Levels.Add(Convert.ToInt32(rank)); } catch { }
      }
      List<object> attackWaves = gomObj.ValueOrDefault<List<object>>("ablAbilityActiveDuringAttackWaves", null);
      if (attackWaves != null) foreach (Object wave in attackWaves) {
        try { result.AttackWaves.Add(Convert.ToInt64(wave)); } catch { }
      }
      if (result.Levels.Count > 0) {
        result.Level = result.Levels[0];
      }
      result.Scales = (result.Levels.Count == 61 - result.Level);
      result.Toughness = gomObj.ValueOrDefault("apnCbtToughness", "");
      result.AiUsePriority = gomObj.ValueOrDefault<long>("AiUsagePriority", 0);

      //result.Ability = _dom.abilityLoader.Load(result.AbilityId);

      var utilityTier = gomObj.ValueOrDefault<long>("ablUtilityTier");
      if (utilityTier != 0) {
        result.IsUtilityPackage = true;
        result.UtilityTier = utilityTier;
        result.UtilityPosition = gomObj.ValueOrDefault<long>("ablUtilityPosition");
      }
      return result;
    }

    public Models.PackageTalent LoadTalent(GomObjectData gomObj) {
      Models.PackageTalent result =
        new Models.PackageTalent(_dom, gomObj.ValueOrDefault<ulong>("talTalentDataSpec", 0)) {
          //result.Talent = _dom.talentLoader.Load(result.PackageId);
          Level = gomObj.ValueOrDefault<long>("talTalentLevel"),
          UtilityTier = gomObj.ValueOrDefault<long>("talUtilityTier"),
          UtilityPosition = gomObj.ValueOrDefault<long>("talUtilityPosition")
        };

      return result;
    }
  }
}
