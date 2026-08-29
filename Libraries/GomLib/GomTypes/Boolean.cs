using System;
using System.Diagnostics;

namespace GomLib.GomTypes {
  public class Boolean : GomType {
    public Boolean() : base(GomTypeId.Boolean) { }
    public override Object ReadData(DataObjectModel dom, GomBinaryReader reader) {
      byte value = reader.ReadByte();
      if (value == 0) return false;
      if (value == 1) return true;

      // Jedipedia rejects values other than 0/1. PugTools deliberately fails soft after
      // consuming the byte so extraction can continue without desynchronising the stream.
      Debug.WriteLine($"Unexpected GOM boolean value {value}; treating it as false.");
      return false;
    }

    public override System.String ToString() => "Boolean";
  }
}
