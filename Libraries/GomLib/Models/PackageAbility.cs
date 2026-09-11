using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;

namespace GomLib.Models {
  public class PackageAbility : IEquatable<PackageAbility> {
    [JsonIgnore]
    public DataObjectModel _dom;
    public PackageAbility() {
      Levels = new List<int>();
      AttackWaves = new List<long>();
    }
    [JsonIgnore]
    public ulong PackageId { get; set; }
    public string PackageB62Id {
      get {
        if (PackageId == 0) return "";
        return PackageId.ToMaskedBase62();
      }
    }

    internal Ability Ability_ { get; set; }
    [JsonIgnore]
    public Ability Ability {
      get {
        if (Ability_ == null) {
          Ability_ = _dom.AbilityLoader.Load(AbilityId);
        }
        return Ability_;
      }
    }

    [JsonIgnore]
    public ulong AbilityId { get; set; }
    public string AbilityB62Id {
      get {
        if (AbilityId == 0) return "";
        return AbilityId.ToMaskedBase62();
      }
    }
    public List<int> Levels { get; set; }
    public bool Scales { get; set; }
    public int Level { get; set; }
    public bool AutoAcquire { get; set; }
    public string Toughness { get; set; }
    public long AiUsePriority { get; set; }
    public List<long> AttackWaves { get; set; }
    public bool IsUtilityPackage { get; set; }
    public long UtilityTier { get; set; }
    public long UtilityPosition { get; set; }

    public override int GetHashCode() {
      int hash = PackageId.GetHashCode();
      if (Ability != null) hash ^= Ability.GetHashCode();
      hash ^= AbilityId.GetHashCode();
      if (Levels != null) foreach (var x in Levels) { hash ^= x.GetHashCode(); }
      hash ^= Scales.GetHashCode();
      hash ^= Level.GetHashCode();
      hash ^= AutoAcquire.GetHashCode();
      if (Toughness != null) hash ^= Toughness.GetHashCode();
      if (AttackWaves != null) foreach (var x in AttackWaves) hash ^= x.GetHashCode();
      hash ^= IsUtilityPackage.GetHashCode();
      hash ^= UtilityTier.GetHashCode();
      hash ^= UtilityPosition.GetHashCode();
      return hash;
    }

    public override bool Equals(object obj) {
      if (obj == null) return false;

      if (ReferenceEquals(this, obj)) return true;

      if (obj is not PackageAbility pkga) return false;

      return Equals(pkga);
    }

    public bool Equals(PackageAbility pkga) {
      if (pkga == null) return false;

      if (ReferenceEquals(this, pkga)) return true;

      if (!Ability.Equals(pkga.Ability))
        return false;
      if (AbilityId != pkga.AbilityId)
        return false;
      if (AutoAcquire != pkga.AutoAcquire)
        return false;
      if (Level != pkga.Level)
        return false;
      if (!Levels.SequenceEqual(pkga.Levels))
        return false;
      if (PackageId != pkga.PackageId)
        return false;
      if (Scales != pkga.Scales)
        return false;
      return true;
    }
  }

  public class PackageTalent : IEquatable<PackageTalent> {

    #region Constructors
    public PackageTalent(DataObjectModel dom, UInt64 packageId) {
      _dom = dom;
      _packageId = packageId;
    }

    #endregion Constructors

    #region Fields
    [JsonIgnore]
    private readonly DataObjectModel _dom;
    [JsonIgnore]
    private readonly UInt64 _packageId;
    private Talent _talent;

    #endregion Fields

    #region Methods
    public override Boolean Equals(Object obj) {
      if (obj == null) return false;

      if (ReferenceEquals(this, obj)) return true;

      if (obj is not PackageTalent pkga) return false;

      return Equals(pkga);
    }

    public Boolean Equals(PackageTalent pkga) {
      if (pkga == null) return false;

      if (ReferenceEquals(this, pkga)) return true;

      if (Talent != null) {
        if (!Talent.Equals(pkga.Talent))
          return false;
      } else if (pkga.Talent != null)
        return false;

      if (UtilityPosition != pkga.UtilityPosition)
        return false;

      if (UtilityTier != pkga.UtilityTier)
        return false;

      if (_packageId != pkga._packageId)
        return false;

      return true;
    }

    public override Int32 GetHashCode() {
      Int32 hash = _packageId.GetHashCode();

      if (Talent != null) hash ^= Talent.GetHashCode();

      hash ^= UtilityTier.GetHashCode();
      hash ^= UtilityPosition.GetHashCode();

      return hash;
    }

    #endregion Methods

    #region Properties
    public Int64 Level { get; init; }

    [JsonIgnore]
    public Talent Talent => _talent ??= _dom.TalentLoader.Load(_packageId);
    public String TalentB62Id => _packageId == 0 ? "" : _packageId.ToMaskedBase62();
    public Int64 UtilityTier { get; init; }
    public Int64 UtilityPosition { get; init; }

    #endregion Properties
  }
}
