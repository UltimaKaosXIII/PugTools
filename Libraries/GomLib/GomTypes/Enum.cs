using System;

namespace GomLib.GomTypes {
  public class Enum : GomType {
    public DomEnum DomEnum { get; internal set; }
    public System.UInt64 DomEnumId { get; internal set; }

    public Enum() : base(GomTypeId.Enum) { }
    internal override void Link(DataObjectModel dom) {
      _dom = dom;
      DomEnum = _dom.Get<DomEnum>(DomEnumId);
    }
    public override object ReadData(DataObjectModel dom, GomBinaryReader reader) {
      ScriptEnum result = new ScriptEnum();

      Int32 val = checked((Int32)reader.ReadSignedNumber());
      // The DomEnum is zero-indexed, but the value that was stored to reference it wasn't. 
      // Fixed this discrepancy by making the stored value zero-indexed when read in.
      result.Value = val - 1;
      result.EnumType = DomEnum;

      return result;
    }
    public override System.String ToString() => System.String.Format("Enum {0}", DomEnum);
  }
}
