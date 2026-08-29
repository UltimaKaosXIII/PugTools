using System;
using System.Collections.Generic;
using System.IO;

namespace GomLib.DomTypeLoaders
{
    /// <summary>
    /// Reads the header of an external PROT prototype node.
    ///
    /// SWTOR shipped two layouts which are still present in archives:
    ///   2.4 - beta; contains one extra UInt32 after the base-class id
    ///   2.5 - live; no extra UInt32
    ///
    /// The old PugTools reader skipped a fixed number of bytes and therefore started beta
    /// node data four bytes early.  Once that happens arbitrary payload bytes are interpreted
    /// as GomType ids (for example 114) and List/Map counts become desynchronised.
    /// </summary>
    class FileInstanceLoader : IDomTypeLoader
    {
        private readonly HashSet<string> outDirs = new HashSet<string>();

        public int SupportedType { get { return (int)DomTypes.Instance; } }

        public HashSet<string> OutDirs => outDirs;

        public DomType Load(GomBinaryReader reader)
        {
            if (reader == null) throw new ArgumentNullException(nameof(reader));
            if (!reader.BaseStream.CanSeek)
                throw new InvalidDataException("External PROT prototype stream must be seekable.");

            // DataObjectModel.LoadPrototype has already consumed the 8-byte PROT/version header.
            long bodyStart = reader.BaseStream.Position;
            if (bodyStart < 8)
                throw new InvalidDataException("Invalid external PROT header position.");

            long saved = reader.BaseStream.Position;
            reader.BaseStream.Position = bodyStart - 4;
            ushort versionMajor = reader.ReadUInt16();
            ushort versionMinor = reader.ReadUInt16();
            reader.BaseStream.Position = saved;

            if (versionMajor != 2 || (versionMinor != 4 && versionMinor != 5))
                throw new InvalidDataException($"Unsupported external PROT version {versionMajor}.{versionMinor}; expected 2.4 or 2.5.");

            GomObject result = new GomObject
            {
                Id = reader.ReadUInt64()
            };

            uint nameLen = reader.ReadUInt32();
            result.Name = ReadCString(reader, nameLen, "prototype name");

            uint descriptionLen = reader.ReadUInt32();
            result.Description = ReadCString(reader, descriptionLen, "prototype description");

            // These are fixed values in the client format (3 and 1).  Consume them instead
            // of folding them into a magic byte skip so beta/live headers stay aligned.
            uint const3 = reader.ReadUInt32();
            uint const1 = reader.ReadUInt32();
            if (const3 != 3 || const1 != 1)
                throw new InvalidDataException($"Invalid PROT constants {const3}/{const1} in {result.Name}; expected 3/1.");
            result.ClassId = reader.ReadUInt64();

            // PROT 2.4 (beta) has one additional 32-bit bookkeeping value here.
            if (versionMinor == 4)
                _ = reader.ReadUInt32();

            uint numGlommed = reader.ReadUInt32();
            if (numGlommed > short.MaxValue)
                throw new InvalidDataException($"Invalid glommed-class count {numGlommed} in {result.Name}.");

            // External prototype files store glommed ids in the header, not in the following
            // root-field payload.  Current shipped files normally use zero, but consume any
            // authored ids so the content offset remains correct.
            for (uint i = 0; i < numGlommed; i++)
                _ = reader.ReadUInt64();
            result.NumGlommed = 0;

            byte nodeKind = reader.ReadByte();
            ushort repeatedMinor = reader.ReadUInt16();
            if (nodeKind != 1)
                throw new InvalidDataException($"Invalid PROT node kind {nodeKind} in {result.Name}; expected 1.");
            if (repeatedMinor != versionMinor)
                throw new InvalidDataException($"PROT repeated minor version {repeatedMinor} does not match {versionMinor} in {result.Name}.");
            if (repeatedMinor >= 3) {
                byte streamStyle = reader.ReadByte();
                if (streamStyle != 1)
                    throw new InvalidDataException($"Invalid PROT stream style {streamStyle} in {result.Name}; expected 1.");
            }

            uint contentLength = reader.ReadUInt32();
            if (contentLength > int.MaxValue)
                throw new InvalidDataException($"Prototype content is too large ({contentLength} bytes).");

            long contentStart = reader.BaseStream.Position;
            long remaining = reader.BaseStream.Length - contentStart;
            if (contentLength > remaining)
                throw new InvalidDataException($"Prototype {result.Name} declares {contentLength} bytes, but only {remaining} remain.");

            // Keep mildly malformed historical files readable, but record the values for diagnostics.
            result.Offset30 = nodeKind;
            result.Offset2E = unchecked((short)repeatedMinor);
            result.ObjectSizeInFile = checked((int)contentLength);
            result.DataLength = result.ObjectSizeInFile;
            result.IsCompressed = false;
            result.NodeDataOffset = checked((int)contentStart);

            return result;
        }

        private static string ReadCString(GomBinaryReader reader, uint length, string fieldName)
        {
            if (length == 0) return String.Empty;
            if (length > int.MaxValue || length > reader.BaseStream.Length - reader.BaseStream.Position)
                throw new InvalidDataException($"Invalid {fieldName} length {length}.");

            byte[] bytes = reader.ReadBytes(checked((int)length));
            int count = bytes.Length;
            if (count > 0 && bytes[count - 1] == 0) count--;
            return System.Text.Encoding.UTF8.GetString(bytes, 0, count);
        }
    }
}
