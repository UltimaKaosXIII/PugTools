using System;

namespace GomLib.GomTypes {
  /// <summary>
  /// GOM DOM type 0 (Void/None).  It is a real serialized type tag and consumes no
  /// payload bytes.  LookupList is the one documented exception: when its *index*
  /// type is Void SWTOR treats the key data as String (handled by Map.ReadData).
  /// </summary>
  public sealed class Void : GomType {
    public Void() : base(GomTypeId.None) { }

    public override object ReadData(DataObjectModel dom, GomBinaryReader reader) => null;

    public override string ToString() => "Void";
  }
}
