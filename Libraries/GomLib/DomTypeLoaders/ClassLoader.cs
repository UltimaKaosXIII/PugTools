using System;

namespace GomLib.DomTypeLoaders
{
    class ClassLoader : IDomTypeLoader
    {
        public int SupportedType { get { return (int)DomTypes.Class; } }

        public DomType Load(GomBinaryReader reader)
        {
            DomClass result = new DomClass();
            LoaderHelper.ParseShared(reader, result);

            Int16 numComponents;
            UInt16 componentOffset;
            Int16 numFields;
            UInt16 fieldsOffset;

            if (reader.DblbVersion == 1)
            {
                // Beta classes do not contain the two 64-bit script-method IDs.
                // Layout: archetype, parentCount, parentOffset, fieldCount, fieldOffset.
                reader.BaseStream.Position = 0x14;
                result.Archetype = reader.ReadUInt16();
                numComponents = reader.ReadInt16();
                componentOffset = reader.ReadUInt16();
                numFields = reader.ReadInt16();
                fieldsOffset = reader.ReadUInt16();
            }
            else
            {
                // Live DBLB v2 inserts two 64-bit method identifiers between the
                // archetype and composition directory. Preserve them as raw schema
                // metadata instead of silently skipping those bytes.
                reader.BaseStream.Position = 0x18;
                result.Archetype = reader.ReadUInt16();
                result.ScriptMethodId1 = reader.ReadUInt64();
                result.ScriptMethodId2 = reader.ReadUInt64();
                numComponents = reader.ReadInt16();
                componentOffset = reader.ReadUInt16();
                numFields = reader.ReadInt16();
                fieldsOffset = reader.ReadUInt16();
            }

            if (numComponents > 0)
            {
                if (componentOffset == 0 || componentOffset + (numComponents * 8L) > reader.BaseStream.Length)
                    throw new InvalidOperationException($"Invalid GOM class parent offset 0x{componentOffset:X} for DBLB v{reader.DblbVersion}.");

                reader.BaseStream.Position = componentOffset;
                for (var i = 0; i < numComponents; i++)
                    result.ComponentIds.Add(reader.ReadUInt64());
            }

            if (numFields > 0)
            {
                if (fieldsOffset == 0 || fieldsOffset + (numFields * 8L) > reader.BaseStream.Length)
                    throw new InvalidOperationException($"Invalid GOM class field offset 0x{fieldsOffset:X} for DBLB v{reader.DblbVersion}.");

                reader.BaseStream.Position = fieldsOffset;
                for (var i = 0; i < numFields; i++)
                    result.FieldIds.Add(reader.ReadUInt64());
            }

            return result;
        }
    }
}
