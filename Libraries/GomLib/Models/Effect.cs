using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace GomLib.Models {
  public class Effect : GameObject {
    //Fields that are directly contained in the eff node, ordered by field id
    [JsonIgnore]
    private Ability _ability;
    [JsonIgnore]
    public Ability Ability { get => _ability ??= Dom_.AbilityLoader.Load(AbilitySpec); }
    [JsonConverter(typeof(ULongConverter))]
    public UInt64 AbilitySpec { get; set; }
    public Single AuraDistance { get; set; }
    [JsonConverter(typeof(LongConverter))]
    public Int64 Charges { get; set; }
    public List<Int64> ChildEffects { get; set; }
    [JsonConverter(typeof(ULongConverter))]
    // 4611686019692990768, duplicated values from effInitializer->effTimeIntervalParams for faster
    // processing.
    public List<UInt64> CustomIntervalPerSwing { get; set; }
    public String Description { get; set; }
    [JsonConverter(typeof(LongConverter))]
    public Int64 DescriptionStringId { get; set; }
    public Boolean DoesPersistAfterDeath { get; set; }
    [JsonConverter(typeof(ULongConverter))]
    public UInt64 Duration { get; set; }
    [JsonConverter(typeof(ULongConverter))]
    public UInt64 DurationAddedDelay { get; set; }
    public String DurationAddedDelayMaxToughness { get; set; }
    [JsonConverter(typeof(ULongConverter))]
    public UInt64 GCD { get; set; }
    public String HashedIcon {
      get {
        String icon = Icon ?? Ability.Icon ?? "none";
        TorArchive.FileId fileId =
          TorArchive.FileId.FromFilePath(String.Format("/resources/gfx/icons/{0}.dds", icon));

        return String.Format("{0}_{1}", fileId.Ph, fileId.Sh);
      }
    }
    public Boolean HasStackLimit { get; set; }
    public Boolean Hidden { get; set; }
    [JsonConverter(typeof(ULongConverter))]
    public UInt64 Hydra { get; set; }
    public String Icon { get; set; }
    public Boolean IgnoresCover { get; set; }
    [JsonConverter(typeof(LongConverter))]
    public Int64 InitialCharges { get; set; }
    [JsonConverter(typeof(ULongConverter))]
    public UInt64 Interval { get; set; }
    public Boolean IsDebuff { get; set; }
    public Boolean IsDurationHidden { get; set; }
    public Boolean IsDurationRealtime { get; set; }
    public Boolean IsInstant { get; set; }
    public Boolean IsInterruptible { get; set; }
    public Boolean IsReverse { get; set; }
    public Boolean IsRootEffect { get; set; }
    public Boolean IsUseableOnTaxi { get; set; }
    public Dictionary<String, String> LocalizedDescription { get; set; }
    public Dictionary<String, String> LocalizedName { get; set; }
    [JsonConverter(typeof(LongConverter))]
    public Int64 MaxCharges { get; set; }
    public String Name { get; set; }
    [JsonConverter(typeof(LongConverter))]
    public Int64 NameStringId { get; set; }
    [JsonConverter(typeof(LongConverter))]
    public Int64 Number { get; set; }
    // Pseudo fields that are added by PugTools
    // public Dictionary<String, Boolean> ParsedStackLimitRelevantTags { get; set; } //DEPRECATED
    public List<String> ParsedTags { get; set; }
    public Boolean Passive { get; set; }
    [JsonConverter(typeof(ULongConverter))]
    public UInt64 ProjectileTravelSpeed { get; set; }
    [JsonConverter(typeof(ULongConverter))]
    public UInt64 SelfReference { get; set; }
    public EffectSlot SlotType { get; set; }
    [JsonConverter(typeof(LongConverter))]
    public Int64 StackLimit { get; set; }
    public Boolean StackLimitIsByCaster { get; set; }
    public Boolean StackLimitIsByTag { get; set; }
    public Boolean StackLimitIsMultiTarget { get; set; }
    public Dictionary<Int64, Boolean> StackLimitRelevantTags { get; set; }
    public List<SubEffectEppDetail> SubEffectEppDetails { get; set; }
    public List<SubEffect> SubEffects { get; set; }
    public List<Int64> Tags { get; set; }
    public Boolean UnknownBool1 { get; set; } //4611686297079480000 - True when it exists
    public Boolean UnknownBool2 { get; set; } //4611686297079480001 - True when it exists
    public Boolean UnknownBool3 { get; set; } //4611686085914561591
    public Boolean UnknownBool4 { get; set; } //4611686300275404002
    public Boolean UnknownBool5 { get; set; } //4611686299759854002
    public Boolean UnknownBool6 { get; set; } //4611686299759854003
    public Boolean UnknownBool7 { get; set; } //4611686299759854004
    [JsonConverter(typeof(LongConverter))]
    public Int64 UnknownLong1 { get; set; } //4611686051963471065, duplicate value of effInitializer->SetStaminaCost; not used

    public override Int32 GetHashCode() {
      Int32 hash = Duration.GetHashCode();

      if (Description != null) hash ^= Description.GetHashCode();

      hash ^= SlotType.GetHashCode();
      hash ^= Interval.GetHashCode();
      hash ^= StackLimit.GetHashCode();

      if (SubEffectEppDetails != null) hash ^= SubEffectEppDetails.GetHashCode().GetHashCode();

      hash ^= SubEffects.GetHashCode().GetHashCode();
      hash ^= Tags.GetHashCode();
      hash ^= SelfReference.GetHashCode();
      hash ^= AbilitySpec.GetHashCode();
      hash ^= NameStringId.GetHashCode();

      if (Name != null) hash ^= Name.GetHashCode();

      hash ^= DescriptionStringId.GetHashCode();

      if (Description != null) hash ^= Description.GetHashCode();

      return hash;
    }

    public override String ToString()
      => String.Format("{0} {1} {2}", NameStringId, Name, Description);
  }

  //Which container slot this effect should be placed in
  public enum EffectSlot {
    conSlotEffectPositive = 23, // This effect is positive and should be in the buff bar
    conSlotEffectNegative = 24, // This effect is negative and should be in the debuff bar
    conSlotEffectOther    = 29  // This effect is should be placed in the other effect bar that is not visible in the UI
  }

  public class SubEffect {
    public List<SubEffectFunction> Actions { get; set; }
    public List<UInt64> ConditionOrder { get; set; }
    public List<SubEffectFunction> Conditions { get; set; }
    public List<SubEffectFunction> Initializers { get; set; }
    public List<SubEffectFunction> TargetOverrides { get; set; }
    public List<SubEffectFunction> Triggers { get; set; }

    /*public override Int64 GetHashCode() Disabled till I get time to parse all possible values
    {
        Int64 hash = Script_NumFields.GetHashCode();
        hash ^= Script_Type.GetHashCode();
        hash ^= Script_TypeId.GetHashCode();
        //if (effSubEffectEppSpec != null) { hash ^= effSubEffectEppSpec.GetHashCode(); }
        //if (effSubEffectEppOnApply != null) { hash ^= effSubEffectEppOnApply.GetHashCode(); }
        return hash;
    }*/

    public override String ToString() => String.Format("{0}", "");
  }


  public class SubEffectEppDetail {
    public Boolean Dependent { get; set; }
    [JsonConverter(typeof(ULongConverter))]
    public UInt64 EppId { get; set; }
    public String EppSpec { get; set; }
    [JsonConverter(typeof(LongConverter))]
    public Int64 Index { get; set; }
    public Boolean OnApply { get; set; }

    public override Int32 GetHashCode() {
      Int32 hash = OnApply.GetHashCode();
      if (EppSpec != null) hash ^= EppSpec.GetHashCode();
      return hash;
    }

    public override String ToString() => String.Format("{0}", EppSpec);
  }

  public class SubEffectFunction {
    //Only for conditions
    [JsonConverter(typeof(ULongConverter))]
    public UInt64 CondId { get; set; }
    public Dictionary<String, String> FailureLocalizedString { get; set; }
    public String FailureString { get; set; }
    [JsonConverter(typeof(LongConverter))]
    public Int64 FailureStringId { get; set; }
    public List<SubEffectFunctionParam> Params { get; set; }
    public List<String> ParsedTags { get; set; }
    public List<Int64> Tags { get; set; }
    //TODO: unknown 4611686346551820000
    [JsonConverter(typeof(LongConverter))]
    public Int64 Type { get; set; }

    public override Int32 GetHashCode() => "".GetHashCode();

    public override String ToString() => String.Format("{0}", "");
  }

  public class SubEffectFunctionParam {
    [JsonConverter(typeof(LongConverter))]
    public Int64 Key { get; set; }
    /// <summary>
    /// Human-readable ScriptEnum key from the GOM schema when available.  Older PugTools builds
    /// discarded this and kept only <see cref="Key"/>, which made Jedipedia-style effect
    /// inspection unnecessarily opaque.
    /// </summary>
    public String KeyName { get; set; }
    public Int32 Type { get; set; }
    public Object Value { get; set; }
    public SubEffectFunctionParam(Int64 key, Int32 type, Object value, String keyName = null) {
      Key = key;
      KeyName = keyName;
      Type = type;
      Value = value;
    }
  }
}
