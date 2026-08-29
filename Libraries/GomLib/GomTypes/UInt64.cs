using System;

namespace GomLib.GomTypes {
  public class UInt64 : GomType {
    public UInt64() : base(GomTypeId.UInt64) { }
    public override System.Boolean ConfirmType(GomBinaryReader reader)
      => reader.ReadByte() == (Byte)TypeId;
    public override Object ReadData(DataObjectModel dom, GomBinaryReader reader)
      => reader.ReadIdNumber();

    public override System.String ToString() => "UInt64";
  }
}
