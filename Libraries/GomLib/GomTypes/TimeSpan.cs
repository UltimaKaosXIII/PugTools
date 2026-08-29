using System;

namespace GomLib.GomTypes {
  public class TimeSpan : GomType {
    public TimeSpan() : base(GomTypeId.TimeSpan) { }
    public override Object ReadData(DataObjectModel dom, GomBinaryReader reader)
      => reader.ReadSignedNumber();

    public override System.String ToString() => "TimeSpan";
  }
}
