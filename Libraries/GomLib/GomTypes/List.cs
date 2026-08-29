using System;
using System.Collections.Generic;

namespace GomLib.GomTypes {
  public class List : GomType {
    public GomType ContainedType { get; internal set; }

    public List() : base(GomTypeId.List) { }
    internal override void Link(DataObjectModel dom) {
      _dom = dom;
      ContainedType?.Link(dom);
    }
    public override Object ReadData(DataObjectModel dom, GomBinaryReader reader) {
      GomType itemType = dom.GomTypeLoader.Load(reader, dom, false);

      // Keep schema metadata (Enum/ClassView/nested container definitions) when the
      // serialized type agrees. Type 0 is a real zero-byte Void value, not a signal to
      // substitute the schema type.
      if ((ContainedType != null) && (itemType.TypeId == ContainedType.TypeId))
        itemType = ContainedType;

      int len = ReadCount(reader, "list total count");
      int stored = ReadCount(reader, "list stored count");
      if (len != stored)
        throw new InvalidOperationException($"List length values aren't the same ({len} != {stored}).");

      var result = new List<Object>(stored);
      for (int i = 0; i < stored; i++) {
        int index = ReadCount(reader, "list index");
        if (index != i + 1)
          throw new InvalidOperationException($"Unexpected list index {index}; expected {i + 1}.");
        result.Add(itemType.ReadItem(dom, reader));
      }
      return result;
    }

    private static int ReadCount(GomBinaryReader reader, string what) {
      long value = reader.ReadSignedNumber();
      if (value < 0 || value > Int32.MaxValue) throw new InvalidOperationException($"Invalid {what}: {value}.");
      return (int)value;
    }

    public override System.String ToString() => System.String.Format("List<{0}>", ContainedType);
  }
}
