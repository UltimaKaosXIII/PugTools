using System;
using System.Collections.Generic;

namespace GomLib.GomTypes {
  public class Map : GomType {
    public GomType KeyType { get; internal set; }
    public GomType ValueType { get; internal set; }

    public Map() : base(GomTypeId.Map) { }
    internal override void Link(DataObjectModel dom) {
      _dom = dom;
      KeyType?.Link(dom);
      ValueType?.Link(dom);
    }
    public override Object ReadData(DataObjectModel dom, GomBinaryReader reader) {
      GomType keyType = dom.GomTypeLoader.Load(reader, dom, false);
      GomType valType = dom.GomTypeLoader.Load(reader, dom, false);

      // Jedipedia documents $tst_CowFieldsModified as "LookupList indexed by Void
      // of String" while the stored keys are actually strings. This exception applies
      // only to LookupList keys; Void values elsewhere remain zero-byte Void values.
      if (keyType.TypeId == GomTypeId.None)
        keyType = new String();
      else if (KeyType != null && keyType.TypeId == KeyType.TypeId)
        keyType = KeyType;

      if (ValueType != null && valType.TypeId == ValueType.TypeId)
        valType = ValueType;

      int len = ReadCount(reader, "map total count");
      int stored = ReadCount(reader, "map stored count");
      if (len != stored)
        throw new InvalidOperationException($"Map length values aren't the same ({len} != {stored}).");

      var result = new Dictionary<Object, Object>(stored);
      for (int i = 0; i < stored; i++) {
        // D2 is a LookupList entry marker, not a varint prefix. The old reader consumed it
        // globally inside ReadNumber(), which could hide stream desynchronisation elsewhere.
        reader.TryConsumeMarker(0xD2);
        Object key = keyType.ReadItem(dom, reader);
        Object val = valType.ReadItem(dom, reader);
        result[key] = val;
      }
      return result;
    }

    private static int ReadCount(GomBinaryReader reader, string what) {
      long value = reader.ReadSignedNumber();
      if (value < 0 || value > Int32.MaxValue) throw new InvalidOperationException($"Invalid {what}: {value}.");
      return (int)value;
    }

    public override System.String ToString() => System.String.Format("Map<{0}, {1}>", KeyType, ValueType);
  }
}
