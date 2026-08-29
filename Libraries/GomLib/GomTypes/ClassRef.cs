using System;

namespace GomLib.GomTypes {
  public class ClassRef : GomType {
    public DomClass DomClass { get; internal set; }
    public ulong DomClassId { get; internal set; }

    public ClassRef() : base(GomTypeId.ClassRef) { }
    internal override void Link(DataObjectModel dom) {
      _dom = dom;
      DomClass = DomClassId != 0 ? _dom.Get<DomClass>(DomClassId) : DomClass;
    }
    public override Object ReadData(DataObjectModel dom, GomBinaryReader reader) {
      _dom ??= dom;
      return _dom.Get<GomObject>(reader.ReadIdNumber());
    }
    public override System.String ToString() {
      return System.String.Format("ClassRef {0}", DomClass);
    }
  }
}
