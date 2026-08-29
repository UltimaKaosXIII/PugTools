using System;
using System.Collections.Generic;

namespace GomLib.DomTypeLoaders {
    class InstanceLoader : IDomTypeLoader {
        private static readonly HashSet<string> outDirs = new HashSet<string>();
        private static readonly ICSharpCode.SharpZipLib.Checksum.Adler32 adlerCalc = new ICSharpCode.SharpZipLib.Checksum.Adler32();

        public static HashSet<string> OutDirs => outDirs;

        public int SupportedType { get { return (int)DomTypes.Instance; } }

        public DomType Load(GomBinaryReader reader) {
            GomObject result = new GomObject();
            LoaderHelper.ParseShared(reader, result);

            UInt16 headerEnd;

            if (reader.DblbVersion == 1) {
                // DBLB v1 / beta layout:
                // flags@04, compressedDataOffset@06, id@08, name@10, desc@12,
                // unknown@14, classId@18, glomCount@20, glomOffset@22,
                // contentLength@24, dataOffset@28, minor@2A, style@2C, nodeType@2D.
                reader.BaseStream.Position = 0x06;
                headerEnd = reader.ReadUInt16();

                reader.BaseStream.Position = 0x14;
                result.Offset20 = reader.ReadInt32();
                result.ClassId = reader.ReadUInt64();
                result.NumGlommed = reader.ReadInt16();
                result.Offset26 = reader.ReadInt16();
                result.ObjectSizeInFile = reader.ReadInt32();
                result.Offset2C = reader.ReadInt16();
                result.Offset2E = reader.ReadInt16();
                result.Offset30 = reader.ReadByte();
                result.Offset31 = reader.ReadByte();
            } else {
                reader.BaseStream.Position = 0x12;
                headerEnd = reader.ReadUInt16();

                reader.BaseStream.Position = 0x18;
                result.ClassId = reader.ReadUInt64();
                result.Offset20 = reader.ReadInt32();
                result.NumGlommed = reader.ReadInt16();
                result.Offset26 = reader.ReadInt16();
                result.ObjectSizeInFile = reader.ReadInt32();
                result.Offset2C = reader.ReadInt16();
                result.Offset2E = reader.ReadInt16();
                result.Offset30 = reader.ReadByte();
                result.Offset31 = reader.ReadByte();
            }

            if (headerEnd > reader.BaseStream.Length)
                throw new InvalidOperationException($"Invalid GOM node data offset 0x{headerEnd:X} for DBLB v{reader.DblbVersion}.");

            reader.BaseStream.Position = headerEnd;

            if (headerEnd != reader.BaseStream.Length) {
                int compressedLength = (int)(reader.BaseStream.Length - reader.BaseStream.Position);
                var buff = reader.ReadBytes(compressedLength);

                adlerCalc.Update(buff);
                result.Checksum = adlerCalc.Value;
                adlerCalc.Reset();

                result.IsCompressed = true;
                result.DataLength = compressedLength;
                result.DataBuffer = buff;
            } else {
                result.DataLength = 0;
            }

            return result;
        }
    }
}
