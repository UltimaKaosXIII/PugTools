using System;

namespace GomLib.DomTypeLoaders
{
    class FieldLoader : IDomTypeLoader
    {
        public int SupportedType { get { return (int)DomTypes.Field; } }

        public DomType Load(GomBinaryReader reader)
        {
            DomField result = new DomField();
            LoaderHelper.ParseShared(reader, result);

            // Field payload starts at 0x14 in DBLB v1 and 0x18 in DBLB v2:
            // modifiers, typeLength, typeOffset.
            reader.BaseStream.Position = reader.DblbVersion == 1 ? 0x18 : 0x1C;
            UInt16 typeOffset = reader.ReadUInt16();

            if (typeOffset == 0 || typeOffset >= reader.BaseStream.Length)
                throw new InvalidOperationException($"Invalid GOM field type offset 0x{typeOffset:X} for DBLB v{reader.DblbVersion}.");

            reader.BaseStream.Position = typeOffset;
            result.GomType = reader.ReadGomType();

            return result;
        }
    }
}
