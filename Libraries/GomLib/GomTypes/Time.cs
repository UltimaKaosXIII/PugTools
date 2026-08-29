using System;

namespace GomLib.GomTypes {
  public class Time : GomType {
    public Time() : base(GomTypeId.Time) { }
    public override Object ReadData(DataObjectModel dom, GomBinaryReader reader)
      => reader.ReadSignedNumber();

    public override System.String ToString() => "Time";
  }
}
