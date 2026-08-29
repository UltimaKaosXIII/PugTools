using System;

namespace GomLib.DomTypeLoaders
{
    static class LoaderHelper
    {
        internal static void ParseShared(GomBinaryReader reader, DomType dom)
        {
            Int64 offset = reader.BaseStream.Position;

            // DBLB v1 (beta): length, flags, dataOffset, id, nameOffset, descOffset
            // DBLB v2 (live): length, nameHash, id, flags, dataOffset, nameOffset, descOffset
            reader.BaseStream.Position = reader.DblbVersion == 1 ? 0x10 : 0x14;
            UInt16 nameOffset = reader.ReadUInt16();
            UInt16 descOffset = reader.ReadUInt16();

            if (nameOffset > 0 && nameOffset < reader.BaseStream.Length)
            {
                reader.BaseStream.Position = nameOffset;
                dom.Name = reader.ReadNullTerminatedString();
            }

            if (descOffset > 0 && descOffset < reader.BaseStream.Length)
            {
                reader.BaseStream.Position = descOffset;
                dom.Description = reader.ReadNullTerminatedString();
            }

            reader.BaseStream.Position = offset;
        }
    }
}
