using System;
using System.Collections.Generic;

namespace GomLib
{
    public class GomTypeLoader
    {
        private readonly Dictionary<GomTypeId, GomTypeLoaders.IGomTypeLoader> gomTypeLoaderMap;

        private void AddLoader(GomTypeLoaders.IGomTypeLoader loader)
        {
            gomTypeLoaderMap.Add(loader.SupportedType, loader);
        }

        private DataObjectModel _dom;

        public DataObjectModel Dom { get => _dom; set => _dom = value; }

        public GomTypeLoader(DataObjectModel dom)
        {
            Dom = dom;

            gomTypeLoaderMap = new Dictionary<GomTypeId, GomTypeLoaders.IGomTypeLoader>();
            AddLoader(new GomTypeLoaders.UInt64Loader());
            AddLoader(new GomTypeLoaders.IntegerLoader());
            AddLoader(new GomTypeLoaders.BooleanLoader());
            AddLoader(new GomTypeLoaders.FloatLoader());
            AddLoader(new GomTypeLoaders.EnumLoader());
            AddLoader(new GomTypeLoaders.StringLoader());
            AddLoader(new GomTypeLoaders.ListLoader(dom));
            AddLoader(new GomTypeLoaders.MapLoader(dom));
            AddLoader(new GomTypeLoaders.EmbeddedClassLoader());
            // Jedipedia's client.gom reader treats these legacy declarations as one-byte
            // schema types. Their stored NODE payloads are not generically decoded there,
            // either, so recognise the schema without guessing at instance byte lengths.
            AddLoader(new GomTypeLoaders.OpaqueLoader(GomTypeId.Association));
            AddLoader(new GomTypeLoaders.OpaqueLoader(GomTypeId.Array));
            AddLoader(new GomTypeLoaders.OpaqueLoader(GomTypeId.Table));
            AddLoader(new GomTypeLoaders.OpaqueLoader(GomTypeId.Cubic));
            AddLoader(new GomTypeLoaders.ScriptLoader());
            AddLoader(new GomTypeLoaders.ClassRefLoader());
            AddLoader(new GomTypeLoaders.OpaqueLoader(GomTypeId.GuiControl));
            AddLoader(new GomTypeLoaders.TimerLoader());
            AddLoader(new GomTypeLoaders.VectorLoader());
            AddLoader(new GomTypeLoaders.TimeSpanLoader());
            AddLoader(new GomTypeLoaders.TimeLoader());
            AddLoader(new GomTypeLoaders.OpaqueLoader(GomTypeId.FsGuid));
            AddLoader(new GomTypeLoaders.OpaqueLoader(GomTypeId.Rawdata));
            AddLoader(new GomTypeLoaders.OpaqueLoader(GomTypeId.FuncRef));
            AddLoader(new GomTypeLoaders.TupleLoader());
            AddLoader(new GomTypeLoaders.OpaqueLoader(GomTypeId.Any));
        }

        public void Flush()
        {
            Dom = null;
        }

        public GomType Load(GomBinaryReader reader, DataObjectModel dom, bool fromGom = true)
        {
            long typeIdPos = reader.BaseStream.CanSeek ? reader.BaseStream.Position : -1;
            GomTypeId typeId = (GomTypeId)reader.ReadByte();

            // Type 0 is a real Void/None DOM type. It consumes no value bytes.
            // Do not use it as a generic "missing inline type" sentinel: Jedipedia and
            // SWTOR both preserve the tag. The only data-level special case is a
            // LookupList whose *key* type is Void; Map.ReadData maps that key to String.
            if (typeId == GomTypeId.None)
            {
                return new GomTypes.Void();
            }

            if (!gomTypeLoaderMap.TryGetValue(typeId, out GomTypeLoaders.IGomTypeLoader gomTypeLoader))
            {
                DumpUnknownTypeContext(reader, typeId, typeIdPos);
                throw new InvalidOperationException(String.Format("Unknown GomType with Type ID {0}", (byte)typeId));
            }

            return gomTypeLoader.Load(reader, fromGom, dom);
        }

        /// <summary>
        /// Diagnostic helper: dumps the bytes surrounding an unrecognized GomType ID to disk,
        /// so the raw layout can be reverse-engineered offline. Writes both a .bin (raw bytes)
        /// and a .txt (hex + context) file next to the running executable.
        /// </summary>
        private static void DumpUnknownTypeContext(GomBinaryReader reader, GomTypeId typeId, long typeIdPos)
        {
            try
            {
                var stream = reader.BaseStream;
                const int contextBefore = 64;
                const int contextAfter = 256;

                byte[] before = Array.Empty<byte>();
                byte[] after = Array.Empty<byte>();
                long dumpStart = typeIdPos;

                if (stream.CanSeek)
                {
                    long start = Math.Max(0, typeIdPos - contextBefore);
                    dumpStart = start;
                    stream.Position = start;
                    before = new byte[typeIdPos - start];
                    if (before.Length > 0)
                    {
                        stream.Read(before, 0, before.Length);
                    }

                    // typeIdPos byte itself already consumed by ReadByte(); resume right after it.
                    stream.Position = typeIdPos + 1;
                }

                int toRead = (int)Math.Min(contextAfter, Math.Max(0, stream.CanSeek ? stream.Length - (typeIdPos + 1) : contextAfter));
                after = new byte[toRead];
                int read = 0;
                while (read < toRead)
                {
                    int n = stream.Read(after, read, toRead - read);
                    if (n <= 0) break;
                    read += n;
                }
                if (read != after.Length)
                {
                    Array.Resize(ref after, read);
                }

                string dir = AppDomain.CurrentDomain.BaseDirectory;
                int counter = 0;
                string baseName;
                do
                {
                    counter++;
                    baseName = System.IO.Path.Combine(dir, string.Format("gomtype_unknown_{0}_{1}", (byte)typeId, counter));
                } while (System.IO.File.Exists(baseName + ".txt"));

                using (var fs = System.IO.File.Create(baseName + ".bin"))
                {
                    fs.Write(before, 0, before.Length);
                    fs.WriteByte((byte)typeId);
                    fs.Write(after, 0, after.Length);
                }

                using (var w = new System.IO.StreamWriter(baseName + ".txt", false))
                {
                    w.WriteLine("Unknown GomType ID: {0} (0x{0:X2})", (byte)typeId);
                    w.WriteLine("Stream position of type-id byte: {0}", typeIdPos);
                    w.WriteLine("Stream seekable: {0}", stream.CanSeek);
                    if (stream.CanSeek)
                    {
                        w.WriteLine("Stream length: {0}", stream.Length);
                    }
                    w.WriteLine();
                    w.WriteLine("--- {0} bytes BEFORE the type-id byte (offset {1}) ---", before.Length, dumpStart);
                    w.WriteLine(BitConverter.ToString(before).Replace("-", " "));
                    w.WriteLine();
                    w.WriteLine("--- type-id byte ---");
                    w.WriteLine("{0:X2}", (byte)typeId);
                    w.WriteLine();
                    w.WriteLine("--- {0} bytes AFTER the type-id byte ---", after.Length);
                    w.WriteLine(BitConverter.ToString(after).Replace("-", " "));
                }
            }
            catch
            {
                // Best-effort diagnostic only - never let dumping itself crash the app differently.
            }
        }
    }
}
