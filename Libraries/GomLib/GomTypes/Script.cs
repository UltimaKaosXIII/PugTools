using System;

namespace GomLib.GomTypes {
  public class Script : GomType {
    public DomClass DomClass { get; internal set; }
    public System.UInt64 DomClassId { get; internal set; }

    public Script() : base(GomTypeId.Script) { }
    internal override void Link(DataObjectModel dom) {
      _dom = dom;

      if (DomClassId != 0)
        DomClass = _dom.Get<DomClass>(DomClassId);
    }
    public override Object ReadData(DataObjectModel dom, GomBinaryReader reader) {
      if (_dom == null) _dom = dom;

      return _dom.Get<GomObject>(reader.ReadIdNumber());
    }
    public override System.String ToString() => System.String.Format("Script {0}", DomClass);
  }
}
