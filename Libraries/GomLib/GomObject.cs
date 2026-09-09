using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace GomLib {
  public class GomObject : DomType, IEquatable<GomObject> {
    internal ulong ClassId { get; set; }
    public DomClass DomClass { get; internal set; }

    public List<DomClass> GlommedClasses { get; internal set; }

    internal int DataLength { get; set; }
    internal byte[] DataBuffer { get; set; }

    public int Offset20 { get; set; }
    public short NumGlommed { get; set; }
    public short Offset26 { get; set; }
    public int ObjectSizeInFile { get; set; }
    public short Offset2C { get; set; }
    public short Offset2E { get; set; }
    public byte Offset30 { get; set; }
    public byte Offset31 { get; set; }

    /// <summary>Adler32 Checksum</summary>
    public long Checksum { get; set; }

    public int Zeroes { get; private set; }

    public int InstanceType { get; internal set; }
    public int NumFields { get; internal set; }

    private GomObjectData _data;
    private readonly Object _loadSync = new Object();
    public GomObjectData Data {
      get {
        lock (_loadSync) {
          if (!IsLoaded) LoadCore();
          return _data;
        }
      }
    }

    internal bool IsCompressed { get; set; }
    internal int NodeDataOffset { get; set; }
    private bool IsLoaded { get; set; }

    private bool isUnloaded;

    private void SetIsUnloaded(bool value) {
      IsUnloaded = value;
    }

    public int DecompressedLength { get; set; }
    //public byte[] FirstBytes { get; set; }

    public Dictionary<string, SortedSet<ulong>> References { get; set; }
    public Dictionary<ulong, Dictionary<string, SortedSet<ulong>>> ProtoReferences { get; set; }
    public Dictionary<ulong, string> FullReferences { get; set; }
    public bool IsUnloaded { get => isUnloaded; set => isUnloaded = value; }

    public override void Link(DataObjectModel dom) {
      Dom_ = dom;
      base.Link(dom);
      DomClass = Dom_.Get<DomClass>(ClassId);
    }

    public XElement Print() {
      if (!IsLoaded) Load();

      //writer.WriteLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
      string domClass = DomClass.ToString();
      if (domClass == "")
        domClass = "None";
      XElement root = new XElement(domClass, new XAttribute("Id", Id), //writer.WriteLine("<{0}>", domClass);
                new XAttribute("Name", Name)); //writer.WriteLine("  <node name=\"{0}\" nodeid=\"{1}\">", Name, Id);
      if (!string.IsNullOrEmpty(Description)) {
        root.Add(new XElement("Description", Description)); //writer.WriteLine("<description text=\"{0}\" />", Description);
      }

      //writer.WriteLine("Compressed Length: 0x{0:X}", DataLength);
      //writer.WriteLine("Offs 0x14: 0x{0:X}", Offset20);
      //writer.WriteLine("NumGlommed: 0x{0:X}", NumGlommed);
      //writer.WriteLine("Offs 0x22: 0x{0:X}", Offset26);
      //writer.WriteLine("ObjSizeInFile: 0x{0:X}", ObjectSizeInFile);
      //writer.WriteLine("Offs 0x28: 0x{0:X}", Offset2C);
      //writer.WriteLine("Offs 0x2A: 0x{0:X}", Offset2E);
      //writer.WriteLine("Offs 0x2C: 0x{0:X}", Offset30);
      //writer.WriteLine("Offs 0x2D: 0x{0:X}", Offset31);
      //writer.WriteLine("Zeroes: {0}", Zeroes);

      //writer.Write("Data: ");
      //for (var i = 0; i < 0xF; i++)
      //{
      //    writer.Write("{0:X2}", FirstBytes[i]);
      //}
      //writer.WriteLine();

      var dataDict = Data;
      if (dataDict != null) {
        foreach (var kvp in dataDict.Dictionary.Skip(3)) {
          root.Add(PrintVal(kvp.Key, kvp.Value));
        }
      }
      //writer.WriteLine("  </node>");
      //writer.WriteLine("</{0}>", domClass);
      return root;
    }

    private XElement PrintVal(string key, object val) {
      XElement element;
      string verifiedKey = key;
      //if (System.Text.RegularExpressions.Regex.IsMatch(key, @"^\-?\d"))
      //verifiedKey = "Field_" + key;
      if (val is System.Collections.IList) {
        System.Collections.IList valList = val as System.Collections.IList;
        element = new XElement("List", new XAttribute("Id", verifiedKey)); //writer.WriteLine("{0}<List Id=\"{1}\">", tabs, verifiedKey);
        for (var i = 0; i < valList.Count; i++) {
          element.Add(PrintVal(i.ToString(), valList[i]));
        }
        //writer.WriteLine("{0}</List>", tabs);
      } else if (val is IDictionary<object, object>) {
        var valDict = val as IDictionary<object, object>;
        element = new XElement("IList", new XAttribute("Id", verifiedKey)); //writer.WriteLine("{0}<IList Id=\"{1}\">", tabs, verifiedKey);
        foreach (var valKvp in valDict) {
          var value = valKvp.Value;
          if (value != null) {
            if (value.GetType() == typeof(string)) {
              string stringValue = null;
              if (value != null) {
                stringValue = System.Security.SecurityElement.Escape(value.ToString()).Replace("\r\n", "&#xA;");
              }
              element.Add(PrintVal(valKvp.Key.ToString(), stringValue));
            } else element.Add(PrintVal(valKvp.Key.ToString(), valKvp.Value));
          } else element.Add(PrintVal(valKvp.Key.ToString(), value));
        }
        //writer.WriteLine("{0}</IList>", tabs);
      } else if (val is GomObjectData) {
        var valDict = (val as GomObjectData).Dictionary.Skip(3);
        element = new XElement("Class", new XAttribute("Id", verifiedKey)); //writer.WriteLine("{0}<Class Id=\"{1}\">", tabs, verifiedKey);
        foreach (var valKvp in valDict) {
          var value = valKvp.Value;
          if (value != null) {
            if (value.GetType() == typeof(string)) {
              string stringValue = null;
              if (value != null) {
                stringValue = System.Security.SecurityElement.Escape(value.ToString()).Replace("\r\n", "&#xA;");
              }
              element.Add(PrintVal(valKvp.Key.ToString(), stringValue));
            } else element.Add(PrintVal(valKvp.Key.ToString(), value));
          } else element.Add(PrintVal(valKvp.Key.ToString(), value));
        }
        //writer.WriteLine("{0}</Class>", tabs);
      } else {
        string stringValue = null;
        if (val != null) {
          stringValue = System.Security.SecurityElement.Escape(val.ToString()).Replace("\r\n", "&#xA;");
        }
        if (stringValue == " ") stringValue = "";
        element = new XElement("Node", new XAttribute("Id", verifiedKey), stringValue); //writer.WriteLine("{0}<node Id=\"{1}\" value=\"{2}\" />", tabs, verifiedKey, stringValue);
      }
      return element;
    }

    public int FindReferences() {
      try {
        if (!IsLoaded) Load();

        var references = new List<ulong>();

        var dataDict = Data;
        if (dataDict != null) {
          foreach (var kvp in dataDict.Dictionary) {
            references.AddRange(FindReferencedVal(kvp.Key, kvp.Value));
          }
        }
        foreach (ulong refe in references.Distinct().ToList()) {
          if (refe != Id) {
            if (Dom_.DomTypeMap.ContainsKey(refe)) {
              if (Dom_.DomTypeMap[refe].GetType() == typeof(GomObject)) {
                GomObject gObj = Dom_.DomTypeMap[refe] as GomObject;
                if (gObj.FullReferences == null) gObj.FullReferences = new Dictionary<ulong, string>();
                if (!gObj.FullReferences.ContainsKey(Id)) gObj.FullReferences.Add(Id, Name);

                if (FullReferences == null) FullReferences = new Dictionary<ulong, string>();
                if (!FullReferences.ContainsKey(gObj.Id)) FullReferences.Add(gObj.Id, gObj.Name);
              }
            }
          }
        }
        return references.Count;
      }
      catch (Exception ex) {
        Debug.WriteLine(ex.Message);
      }
      return 0;
    }

    private List<ulong> FindReferencedVal(string key, object val) {
      List<ulong> references = new List<ulong>();

      if (key.StartsWith("1614") && key.Length == 20) { references.Add(Convert.ToUInt64(key)); }
      ulong unsignedLong;
      _ = long.TryParse(key, out long signedLong);
      unsignedLong = Convert.ToUInt64(string.Format("{0:x8}", signedLong), 16);
      string unsignedString = unsignedLong.ToString();
      if (unsignedString.StartsWith("1614") && unsignedString.Length == 20) { references.Add(unsignedLong); }

      if (val is System.Collections.IList) {
        System.Collections.IList valList = val as System.Collections.IList;
        for (var i = 0; i < valList.Count; i++) {
          references.AddRange(FindReferencedVal(i.ToString(), valList[i]));
        }
      } else if (val is IDictionary<object, object>) {
        var valDict = val as IDictionary<object, object>;
        foreach (var valKvp in valDict) {
          references.AddRange(FindReferencedVal(valKvp.Key.ToString(), valKvp.Value));
        }
      } else if (val is GomObjectData) {
        var valDict = (val as GomObjectData).Dictionary;
        foreach (var valKvp in valDict) {
          references.AddRange(FindReferencedVal(valKvp.Key.ToString(), valKvp.Value));
        }
      } else if (val is GomObject) {
        var valDict = (val as GomObject).Data.Dictionary;
        references.Add((val as GomObject).Id);
        foreach (var valKvp in valDict) {
          references.AddRange(FindReferencedVal(valKvp.Key.ToString(), valKvp.Value));
        }
      } else {
        /*var type = val.GetType().ToString();
        if (type != "GomLib.DomClass"
            && !type.Contains("Int32")
            && !type.Contains("Int64")
            && !type.Contains("System.Single")
            && !type.Contains("GomLib.ScriptEnum")
            && !type.Contains("System.Boolean")
            && !type.Contains("System.String"))
        {
            string breakhere = "";
        }*/
        if (val != null) {
          if (val.ToString().Length == 20) {
            if (val.ToString().StartsWith("1614") && val.ToString().Length == 20) { references.Add(Convert.ToUInt64(val)); }
            _ = long.TryParse(val.ToString(), out long valParsed);
            ulong valU = Convert.ToUInt64(string.Format("{0:x8}", valParsed), 16);
            if (valU.ToString().StartsWith("1614")) {
              references.Add(valU);
            }
          }
        }
      }

      return references;
    }

    public void Load() {
      lock (_loadSync) {
        LoadCore();
      }
    }

    private void LoadCore() {
      if (IsLoaded) { return; }
      //if (this.Name == "chrPaidPermissionDefsTablePrototype") { return; } //bandaid, need to probe this failure. Probed. Was a 0xD0 variable length int error.
      //if (IsUnloaded) { throw new InvalidOperationException("Cannot reload object once it's unloaded"); } //Fuck you yes I can reload it.

      if ((NumGlommed > 0) || (ObjectSizeInFile > 0)) {
        byte[] buffer = GetRawUncompressedNode();

        // Load data from decompressed buffer
        using var ms = new System.IO.MemoryStream(buffer);
        using var br = new GomBinaryReader(ms, Dom_);
        //System.IO.File.WriteAllBytes("j://tempfile.txt", buffer);
        ms.Position = Zeroes;
        GlommedClasses = new List<DomClass>();
        for (var glomIdx = 0; glomIdx < NumGlommed; glomIdx++) {
          var glomClassId = br.ReadUInt64();
          var glomClass = Dom_.Get<DomClass>(glomClassId);
          GlommedClasses.Add(glomClass);
        }

        _data = Dom_.ScriptObjectReader.ReadObject(DomClass, br, Dom_);
        //if(this.Id == 16141050636868461855)
        //{
        //    using (var stream = System.IO.File.OpenRead("j:\\16141050636868461855.uncompressed"))
        //    using (var ms = new System.IO.MemoryStream(DataBuffer))
        //    {
        //        //using (var istream = new ICSharpCode.SharpZipLib.Zip.Compression.Streams.DeflaterOutputStream(ms, new ICSharpCode.SharpZipLib.Zip.Compression.Deflater()))
        //        //{
        //        //    stream.CopyTo(istream);
        //            using (var output = System.IO.File.OpenWrite("j:\\16141050636868461855.compressed"))
        //            {
        //                ms.Position = 0;
        //                ms.WriteTo(output);
        //            }
        //        //}

        //    }
        //}
        //FirstBytes = buffer.Take(0xF).ToArray();
      }

      //this.DataBuffer = null; // Since we're loaded, we don't need to hold on to the compressed data anymore // Why the fuck shouldn't I unload the damn object and reclaim the memory. This idiocy prevented me from reloading it.
      IsLoaded = true;
    }

    public byte[] GetRawUncompressedNode() {
      byte[] buffer;
      if (IsCompressed) {
        int dataLen = 8 * NumGlommed + ObjectSizeInFile;
        int maxLen = dataLen + 8;
        buffer = new byte[maxLen];

        // The "IsCompressed" flag we read is a coarse heuristic (see InstanceLoader.cs) - it does
        // NOT reliably mean the bytes are actually run through a general-purpose compressor. Some
        // nodes (e.g. plain data tables) turn out to carry raw, uncompressed payload here. Rather
        // than trust the flag blindly, sniff the actual bytes and dispatch accordingly:
        //   - Zstandard frame magic (28 B5 2F FD)      -> zstd (64-bit client nodes)
        //   - Valid zlib header (CMF/FLG per RFC 1950)  -> classic zlib/deflate (32-bit client nodes)
        //   - Neither                                   -> not actually compressed; use as-is
        bool isZstd = DataBuffer.Length >= 4 &&
                      DataBuffer[0] == 0x28 && DataBuffer[1] == 0xB5 &&
                      DataBuffer[2] == 0x2F && DataBuffer[3] == 0xFD;

        bool isZlib = false;
        if (!isZstd && DataBuffer.Length >= 2) {
          int cmf = DataBuffer[0];
          int flg = DataBuffer[1];
          isZlib = (cmf & 0x0F) == 8 && ((cmf << 8) + flg) % 31 == 0;
        }

        int readBytes;
        if (isZstd) {
          using var decompressor = new ZstdSharp.Decompressor();
          System.Span<byte> span = decompressor.Unwrap(DataBuffer, maxLen);
          if (span.Length > maxLen)
            throw new InvalidDataException($"GOM node {Id} decompressed to {span.Length} bytes; expected at most {maxLen}.");
          readBytes = span.Length;
          span.CopyTo(buffer);
        } else if (isZlib) {
          try {
            using var ms = new System.IO.MemoryStream(DataBuffer);
            using var istream = new ICSharpCode.SharpZipLib.Zip.Compression.Streams.InflaterInputStream(ms, new ICSharpCode.SharpZipLib.Zip.Compression.Inflater(false));
            readBytes = 0;
            // Stream.Read is allowed to return a short read. The previous single call could therefore
            // truncate perfectly valid nodes and make the following field byte look like a GomType.
            while (readBytes < maxLen) {
              int n = istream.Read(buffer, readBytes, maxLen - readBytes);
              if (n <= 0) break;
              readBytes += n;
            }
            // Detect output larger than our expected content+alignment envelope as a format error.
            if (readBytes == maxLen && istream.ReadByte() >= 0)
              throw new InvalidDataException($"GOM node {Id} decompressed beyond the expected {maxLen} bytes.");
          } catch (Exception ex) {
            DumpFailedNodeBuffer(ex);
            throw;
          }
        } else {
          // Not actually compressed - use the raw bytes directly.
          if (DataBuffer.Length > maxLen)
            throw new InvalidDataException($"Raw GOM node {Id} has {DataBuffer.Length} bytes; expected at most {maxLen}.");
          readBytes = DataBuffer.Length;
          System.Array.Copy(DataBuffer, buffer, readBytes);
        }

        Zeroes = readBytes - dataLen;
        if (Zeroes < 0 || Zeroes > 7)
          throw new InvalidDataException($"GOM node {Id} has invalid leading alignment padding {Zeroes} (decoded={readBytes}, content={dataLen}).");
        if (readBytes != buffer.Length) System.Array.Resize(ref buffer, readBytes);
      } else {
        string path = string.Format("/resources/systemgenerated/prototypes/{0}.node", Id);
        TorArchive.File protoFile = Dom_.Assets.FindFile(path);
        if (protoFile != null) {
          using var fs = protoFile.Open();
          using var br = new GomBinaryReader(fs, Encoding.UTF8, Dom_);
          br.ReadBytes(NodeDataOffset);
          buffer = br.ReadBytes(ObjectSizeInFile);
          Zeroes = 0;
        } else {
          throw new System.IO.FileNotFoundException($"Prototype node file not found for {Id}.", path);
        }
      }

      return buffer;
    }

    public void Unload() {
      lock (_loadSync) {
        _data = null;
        GlommedClasses = new List<DomClass>();
        IsLoaded = false;
        SetIsUnloaded(true);
      }
    }

    /// <summary>
    /// Diagnostic helper: when zlib decompression of a node fails, dump the raw compressed
    /// bytes to disk so the actual codec/layout can be figured out offline, instead of guessing.
    /// </summary>
    private void DumpFailedNodeBuffer(Exception ex) {
      try {
        string dir = AppDomain.CurrentDomain.BaseDirectory;
        int counter = 0;
        string baseName;
        do {
          counter++;
          baseName = System.IO.Path.Combine(dir, string.Format("gomobject_decompress_fail_{0}", counter));
        } while (System.IO.File.Exists(baseName + ".txt"));

        using (var fs = System.IO.File.Create(baseName + ".bin")) {
          fs.Write(DataBuffer, 0, DataBuffer.Length);
        }

        using (var w = new System.IO.StreamWriter(baseName + ".txt", false)) {
          w.WriteLine("Exception: {0}", ex.GetType().FullName);
          w.WriteLine("Message: {0}", ex.Message);
          w.WriteLine("GomObject Id: {0}", Id);
          w.WriteLine("Name: {0}", Name);
          w.WriteLine("DataBuffer length: {0}", DataBuffer.Length);
          w.WriteLine("NumGlommed: {0}", NumGlommed);
          w.WriteLine("ObjectSizeInFile: {0}", ObjectSizeInFile);
          w.WriteLine();
          int previewLen = Math.Min(64, DataBuffer.Length);
          w.WriteLine("--- first {0} bytes ---", previewLen);
          w.WriteLine(BitConverter.ToString(DataBuffer, 0, previewLen).Replace("-", " "));
        }
      } catch {
        // Best-effort diagnostic only.
      }
    }

    public override int GetHashCode() {
      int hash = Id.GetHashCode();
      hash ^= ClassId.GetHashCode();
      if (Name != null) hash ^= Name.GetHashCode();
      hash ^= DataLength.GetHashCode();
      hash ^= Checksum.GetHashCode();
      return hash;
    }

    public override bool Equals(object obj) {
      if (obj is not GomObject other) return false;

      return Equals(other);
    }

    public bool Equals(GomObject other) {
      if (other == null) {
        return false;
      }

      if (ReferenceEquals(this, other)) {
        return true;
      }

      if (Name != other.Name)
        return false;
      if (ClassId != other.ClassId)
        return false;
      if (DataLength != other.DataLength)
        return false;
      if (Checksum != 0)
        if (Checksum != other.Checksum)
          return false;

      return true;

    }
  }
}
