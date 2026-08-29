using System;

namespace GomLib.DomTypeLoaders
{
    class EnumLoader : IDomTypeLoader
    {
        public int SupportedType { get { return (int)DomTypes.Enum; } }

        public DomType Load(GomBinaryReader reader)
        {
            DomEnum result = new DomEnum();
            LoaderHelper.ParseShared(reader, result);

            // DBLB v1 payload starts at 0x14, DBLB v2 at 0x18.
            reader.BaseStream.Position = reader.DblbVersion == 1 ? 0x14 : 0x18;
            UInt16 numVals = reader.ReadUInt16();
            UInt16 offsetsOffset = reader.ReadUInt16();

            if (numVals == 0)
                return result;

            if (offsetsOffset == 0 || offsetsOffset + (numVals * 2L) > reader.BaseStream.Length)
                throw new InvalidOperationException($"Invalid GOM enum offset table 0x{offsetsOffset:X} for DBLB v{reader.DblbVersion}.");

            for (Int32 i = 0; i < numVals; i++)
            {
                reader.BaseStream.Position = offsetsOffset + (i * 2L);
                UInt16 stringOffset = reader.ReadUInt16();

                if (stringOffset == 0 || stringOffset >= reader.BaseStream.Length)
                    throw new InvalidOperationException($"Invalid GOM enum string offset 0x{stringOffset:X} for DBLB v{reader.DblbVersion}.");

                reader.BaseStream.Position = stringOffset;
                result.AddName(reader.ReadNullTerminatedString());

                // Legacy DomEnum keeps the raw enum table values. In this format the
                // table entries are offsets to the corresponding strings.
                result.AddValue(unchecked((Int16)stringOffset));
            }

            return result;
        }
    }
}
