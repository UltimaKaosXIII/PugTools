using System;

namespace GomLib.GomTypeLoaders
{
    class TupleLoader : IGomTypeLoader
    {
        public GomTypeId SupportedType { get { return GomTypeId.Tuple; } }

        public GomType Load(GomBinaryReader reader, bool fromGom, DataObjectModel dom)
        {
            var tuple = new GomTypes.Tuple();
            if (!fromGom) return tuple;

            long countRaw = reader.ReadSignedNumber();
            if (countRaw < 0 || countRaw > Int32.MaxValue)
                throw new InvalidOperationException($"Invalid Tuple type count {countRaw}.");
            int count = (int)countRaw;
            for (int i = 0; i < count; i++)
            {
                GomType element = dom.GomTypeLoader.Load(reader, dom, true);
                tuple.ElementTypes.Add(element);
            }
            return tuple;
        }
    }
}
