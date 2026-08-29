using System;
using System.Collections.Generic;

namespace GomLib.GomTypes
{
    /// <summary>
    /// DOM type 24 is Tuple (first used by the later 64-bit client), not a fixed four-byte
    /// payload.  client.gom stores a varint element count followed by recursive type
    /// definitions; NODE data stores two equal counts and indexed, explicitly typed values.
    /// </summary>
    public class Tuple : GomType
    {
        public System.Collections.Generic.List<GomType> ElementTypes { get; } = new System.Collections.Generic.List<GomType>();

        public Tuple() : base(GomTypeId.Tuple) { }

        internal override void Link(DataObjectModel dom)
        {
            _dom = dom;
            foreach (GomType type in ElementTypes) type?.Link(dom);
        }

        public override object ReadData(DataObjectModel dom, GomBinaryReader reader)
        {
            int total = ReadCount(reader, "tuple total count");
            int stored = ReadCount(reader, "tuple stored count");
            if (total != stored)
                throw new InvalidOperationException($"Tuple counts do not match ({total} != {stored}).");

            var result = new System.Collections.Generic.List<object>(stored);
            for (int i = 0; i < stored; i++)
            {
                int index = ReadCount(reader, "tuple index");
                if (index != i + 1)
                    throw new InvalidOperationException($"Unexpected tuple index {index}; expected {i + 1}.");

                GomType actual = dom.GomTypeLoader.Load(reader, dom, false);
                GomType declared = i < ElementTypes.Count ? ElementTypes[i] : null;
                GomType type = actual;
                if (declared != null && actual.TypeId == declared.TypeId) type = declared;
                result.Add(type.ReadItem(dom, reader));
            }
            return result;
        }

        private static int ReadCount(GomBinaryReader reader, string what)
        {
            long value = reader.ReadSignedNumber();
            if (value < 0 || value > Int32.MaxValue) throw new InvalidOperationException($"Invalid {what}: {value}.");
            return (int)value;
        }

        public override string ToString() => "Tuple<" + System.String.Join(", ", ElementTypes) + ">";
    }
}
