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
            // modifiers, typeLength, typeOffset.  Preserve the first two values for
            // client.gom inspection; modifier semantics are deliberately left raw.
            reader.BaseStream.Position = reader.DblbVersion == 1 ? 0x14 : 0x18;
            result.Modifiers = reader.ReadUInt16();
            result.SerializedTypeLength = reader.ReadUInt16();
            result.SerializedTypeOffset = reader.ReadUInt16();
            UInt16 typeOffset = result.SerializedTypeOffset;

            if (typeOffset == 0 || typeOffset >= reader.BaseStream.Length)
                throw new InvalidOperationException($"Invalid GOM field type offset 0x{typeOffset:X} for DBLB v{reader.DblbVersion}.");

            reader.BaseStream.Position = typeOffset;
            result.GomType = reader.ReadGomType();

            return result;
        }
    }
}
