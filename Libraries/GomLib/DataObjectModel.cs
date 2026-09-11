using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using System.Xml.XPath;

using GomLib.ModelLoader;

using TorArchive;
using File = TorArchive.File;

namespace GomLib {
  public class DataObjectModel : IDisposable {

    #region Constructors
    public DataObjectModel(Assets assets) {
      Assets = assets;

      m_bucketFiles = new List<String>();
      m_namedMap = new Dictionary<String, HashSet<String>>();
      m_prototypeLoader = new DomTypeLoaders.FileInstanceLoader();
      m_storedIdMap = new Dictionary<UInt64, String>();
      m_storedNameMap = new Dictionary<String, UInt64>();
      m_typeLoaderMap = new Dictionary<Int32, DomTypeLoaders.IDomTypeLoader>();
      m_unnamedMap = new Dictionary<String, HashSet<UInt64>>();

      DomTypeMap = new Dictionary<UInt64, DomType>();
      NodeLookup = new Dictionary<Type, Dictionary<String, DomType>>();

      AddTypeLoader(new DomTypeLoaders.EnumLoader());
      AddTypeLoader(new DomTypeLoaders.FieldLoader());
      AddTypeLoader(new DomTypeLoaders.AssociationLoader());
      AddTypeLoader(new DomTypeLoaders.ClassLoader());
      AddTypeLoader(new DomTypeLoaders.InstanceLoader());
    }

    #endregion Constructors

    #region Fields
    private List<String> m_bucketFiles;
    private Boolean m_crossLinked;
    private Boolean m_loaded;
    private Boolean m_schemaLoaded;
    private Boolean m_legacyGom;
    private readonly Dictionary<String, HashSet<String>> m_namedMap;
    private DomTypeLoaders.FileInstanceLoader m_prototypeLoader;
    private Dictionary<UInt64, String> m_storedIdMap;
    private Dictionary<String, UInt64> m_storedNameMap;
    private Dictionary<Int32, DomTypeLoaders.IDomTypeLoader> m_typeLoaderMap;
    private Dictionary<String, HashSet<UInt64>> m_unnamedMap;

    #endregion Fields

    #region IDisposable
    private Boolean m_disposed = false;

    ~DataObjectModel() {
      Dispose(false);
    }

    public void Dispose() {
      Dispose(true);
      GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(Boolean disposing) {
      if (m_disposed) {
        return;
      }

      if (disposing) {
        m_typeLoaderMap.Clear();
        NodeLookup.Clear();
        DomTypeMap = null;
        m_storedNameMap.Clear();
        m_storedNameMap = null;
        m_unnamedMap.Clear();
        m_unnamedMap = null;
        m_bucketFiles.Clear();

        if (Data != null) {
          Data.Dispose();
        }
      }

      m_disposed = true;
    }

    #endregion IDisposable

    #region Methods
    public void AddCrossLink(Int64 id, String type, UInt64 reference) {
      UInt64 id2 = Math.Sign(id) == -1 ? UInt64.MaxValue - (UInt64)Math.Abs(id) + 1UL : (UInt64)id;

      AddCrossLink(id2, type, reference);
    }

    public void AddCrossLink(String name, String type, UInt64 reference) {
      GomObject testNode = GetObjectNoLoad(name);

      if (testNode != null) {
        if (testNode.References == null) {
          testNode.References = new Dictionary<String, SortedSet<UInt64>>();
        }

        if (!testNode.References.ContainsKey(type)) {
          testNode.References.Add(type, new SortedSet<UInt64>());
        }

        if (!testNode.References[type].Contains(reference)) {
          testNode.References[type].Add(reference);
        }
      }
    }

    public void AddCrossLink(UInt64 id, String type, UInt64 reference) {
      GomObject testNode = GetObjectNoLoad(id);

      if (testNode != null) {
        if (testNode.References == null) {
          testNode.References = new Dictionary<String, SortedSet<UInt64>>();
        }

        if (!testNode.References.ContainsKey(type)) {
          testNode.References.Add(type, new SortedSet<UInt64>());
        }

        if (!testNode.References[type].Contains(reference)) {
          testNode.References[type].Add(reference);
        }
      }
    }

    public void AddCrossLinkRange(UInt64 id, String type, List<UInt64> reference) {
      GomObject testNode = GetObjectNoLoad(id);

      if (testNode != null) {
        if (testNode.References == null) {
          testNode.References = new Dictionary<String, SortedSet<UInt64>>();
        }

        if (!testNode.References.ContainsKey(type)) {
          testNode.References.Add(type, new SortedSet<UInt64>());
        }

        testNode.References[type].UnionWith(reference);
        testNode.References[type].Remove(id); //remove self references
      }
    }

    public void AddProtoCrossLink(UInt64 protoId, UInt64 id, String type, UInt64 reference) {
      GomObject testNode = GetObjectNoLoad(protoId);

      if (testNode != null) {
        if (testNode.ProtoReferences == null) {
          testNode.ProtoReferences =
            new Dictionary<UInt64, Dictionary<String, SortedSet<UInt64>>>();
        }

        if (!testNode.ProtoReferences.ContainsKey(id)) {
          testNode.ProtoReferences.Add(id, new Dictionary<String, SortedSet<UInt64>>());
        }

        if (!testNode.ProtoReferences[id].ContainsKey(type)) {
          testNode.ProtoReferences[id].Add(type, new SortedSet<UInt64>());
        }

        if (!testNode.ProtoReferences[id][type].Contains(reference)) {
          testNode.ProtoReferences[id][type].Add(reference);
        }
      }
    }

    private void AddToNameLookup(DomType type) {
      if (String.IsNullOrEmpty(type.Name)) {
        return;
      }

      Type typeType = type.GetType();

      if (!NodeLookup.TryGetValue(typeType, out _)) {
        Dictionary<String, DomType> nameMap = new Dictionary<String, DomType>();
        NodeLookup[typeType] = nameMap;
      }

      // Multiple GOM IDs can share the same resolved display name.
      // NodeLookup is keyed only by name, so such aliases are inherently
      // ambiguous. Keep the first loaded entry instead of aborting the
      // complete DOM load with Dictionary.Add(). ID-based access remains
      // available through DomTypeMap for every type.
      if (!NodeLookup[typeType].ContainsKey(type.Name)) {
        NodeLookup[typeType].Add(type.Name, type);
      }
    }

    private void AddTypeLoader(DomTypeLoaders.IDomTypeLoader loader) {
      Int32 type = loader.SupportedType;
      m_typeLoaderMap.Add(type, loader);
    }

    public void CrossLink() {
      if (m_crossLinked) {
        return;
      }

      //foreach (DomType t in DomTypeMap.Values)

      Parallel.ForEach(DomTypeMap.Values, t => {
        if (t.GetType() == typeof(GomObject)) {
          GomObject tG = t as GomObject;
          tG.FindReferences();
          tG.Unload();
        }
      });

      m_crossLinked = true;
    }

    public T Get<T>(String name) where T : DomType {
      if (name == null) {
        return null;
      }

      Type resultType = typeof(T);

      if (!NodeLookup.TryGetValue(resultType, out Dictionary<String, DomType> nameMap)) {
        return null;
      }

      if (!nameMap.TryGetValue(name, out DomType t)) {
        return null;
      }

      return t as T;
    }

    public T Get<T>(UInt64 typeId) where T : DomType {
      if (!DomTypeMap.TryGetValue(typeId, out DomType t)) {
        return null;
      }

      T result = t as T;

      if (result == null) {
        Debug.WriteLine("Type 0x{0:X} is not of type {1}", t.Id, typeof(T));
      }

      return result;
    }

    public SortedDictionary<String, Int64> GetAllInstanceNames() {
      SortedDictionary<String, Int64> results = new SortedDictionary<String, Int64>();
      Type resultType = typeof(GomObject);

      if (!NodeLookup.TryGetValue(resultType, out Dictionary<String, DomType> nameMap)) {
        return results;
      }

      foreach (KeyValuePair<String, DomType> kvp in nameMap) {
        results.Add(kvp.Key, ((GomObject)kvp.Value).Checksum);
      }

      return results;
    }

    public GomObject GetObject(String name) {
      GomObject result = Get<GomObject>(name);
      result?.Load();
      return result;
    }

    public GomObject GetObject(UInt64 id) {
      GomObject result = Get<GomObject>(id);
      result?.Load();
      return result;
    }

    public UInt64 GetObjectId(String name) {
      GomObject result = Get<GomObject>(name);
      return result != null ? result.Id : 0;
    }

    public GomObject GetObjectNoLoad(String name) {
      return Get<GomObject>(name);
    }

    public GomObject GetObjectNoLoad(UInt64 id) {
      return Get<GomObject>(id);
    }

    public List<GomObject> GetObjectsStartingWith(String txt) {
      List<GomObject> results = new List<GomObject>();
      Type resultType = typeof(GomObject);

      if (!NodeLookup.TryGetValue(resultType, out Dictionary<String, DomType> nameMap)) {
        return results;
      }

      foreach (KeyValuePair<String, DomType> kvp in nameMap) {
        if (kvp.Key.StartsWith(txt)) {
          results.Add((GomObject)kvp.Value);
        }
      }

      return results;
    }

    public UInt64 GetStoredTypeId(String name) {
      m_storedNameMap.TryGetValue(name, out UInt64 id);
      return id;
    }

    public String GetStoredTypeName(UInt64 id) {
      if (m_storedIdMap.TryGetValue(id, out String result)) {
        return result;
      }

      return null;
    }

    private void InitializeModelLoaders() {
      // THESE MUST BE IN THIS ORDER!
      ScriptObjectReader = new ScriptObjectReader(this);
      // DBLB v1 (the RED/BLUE beta client) uses older table schemas. The DOM itself is valid, but
      // several modern helper tables eagerly cast live-era keys/values in their constructors. Keep
      // those helpers best-effort in legacy mode so Node/Model/World Browser can use the correctly
      // parsed GOM instead of aborting on the first incompatible convenience table.
      Data = new Data(this, m_legacyGom);
      StringTable = new StringTable(this);
      Ami = new AMI(this);

      //ADD NEW MODELS HERE
      AbilityLoader = new AbilityLoader(this);
      AbilityPackageLoader = new AbilityPackageLoader(this);
      AchievementLoader = new AchievementLoader(this);
      AchievementCategoryLoader = new AchievementCategoryLoader(this);
      AdvancedClassLoader = new AdvancedClassLoader(this);
      AppearanceLoader = new AppearanceLoader(this);
      AreaLoader = new AreaLoader(this);
      ClassSpecLoader = new ClassSpecLoader(this);
      CodexLoader = new CodexLoader(this);
      MtxStorefrontEntryLoader = new MtxStorefrontEntryLoader(this);
      CollectionLoader = new CollectionLoader(this);
      CompanionLoader = new CompanionLoader(this);
      NewCompanionLoader = new NewCompanionLoader(this);
      ConquestLoader = new ConquestLoader(this);
      ConversationLoader = new ConversationLoader(this);
      DecorationLoader = new DecorationLoader(this);
      DisciplineLoader = new DisciplineLoader(this);
      NewDisciplineLoader = new NewDisciplineLoader(this);
      EffectLoader = new EffectLoader(this);
      EncounterLoader = new EncounterLoader(this);
      ItemLoader = new ItemLoader(this);
      MapNoteLoader = new MapNoteLoader(this);
      NpcLoader = new NpcLoader(this);
      PackageAbilityLoader = new PackageAbilityLoader(this);
      PlaceableLoader = new PlaceableLoader(this);
      QuestBranchLoader = new QuestBranchLoader(this);
      QuestLoader = new QuestLoader(this);
      QuestStepLoader = new QuestStepLoader(this);
      QuestTaskLoader = new QuestTaskLoader(this);
      SCFFColorOptionLoader = new SCFFColorOptionLoader(this);
      SCFFComponentLoader = new SCFFComponentLoader(this);
      SCFFPatternLoader = new SCFFPatternLoader(this);
      SCFFShipLoader = new SCFFShipLoader(this);
      SchematicLoader = new SchematicLoader(this);
      SpawnerLoader = new SpawnerLoader(this);
      StrongholdLoader = new StrongholdLoader(this);
      TalentLoader = new TalentLoader(this);
      SetBonusLoader = new SetBonusLoader(this);
      CdxCatTotalsLoader = new CodexCatByFactionLoader(this);
      SchemVariationLoader = new SchematicVariationLoader(this);
      LegacyTitleLoader = new LegacyTitleLoader(this);
      ReputationGroupLoader = new ReputationGroupLoader(this);
      ReputationRankLoader = new ReputationRankLoader(this);
      DetailedAppearanceColorLoader = new DetailedAppearanceColorLoader(this);
      PlayerTitleLoader = new PlayerTitleLoader(this);

      AreaDatLoader = new FileLoaders.AreaDatLoader(this);
      RoomDatLoader = new FileLoaders.RoomDatLoader(this);

      Models.Tooltip.Flush();
    }

    /// <summary>
    /// Loads only the schema stored in client.gom. This intentionally skips buckets,
    /// prototypes and gameplay helper tables and is therefore suitable for schema/DOM
    /// inspection tools that should open quickly without building the complete node model.
    /// A later call to Load() upgrades the same instance to the full model.
    /// </summary>
    public void LoadSchemaOnly() {
      if (m_loaded || m_schemaLoaded) return;

      LoadTypeNames();
      GomTypeLoader = new GomTypeLoader(this);
      LoadClientGom();
      LinkDomTypes();
      m_schemaLoaded = true;
    }

    private void LinkDomTypes() {
      // Link() is deliberately repeatable: LoadSchemaOnly() may be followed by Load().
      // DomClass.Link clears its resolved lists before rebuilding them.
      foreach (DomType domType in DomTypeMap.Values) domType.Link(this);
    }

    public void Load() {
      if (m_loaded) {
        return;
      }

      if (!m_schemaLoaded) {
        LoadTypeNames();
        GomTypeLoader = new GomTypeLoader(this);
        LoadClientGom();
        m_schemaLoaded = true;
      }

      LoadBuckets();
      LoadPrototypes();

      // Debug.WriteLine(
      //   "Warning: loading of individual (non-bucketed) prototype files is currently disabled");

      m_loaded = true;

      StatData = new Models.StatData(this);
      FactionData = new Models.FactionData(this);
      EnhancementData = new Models.EnhancementData(this);
      SocialTierData = new Models.SocialTierData(this);
      AlignmentData = new Models.AlignmentData(this);
      GroupFinderContentData = new Models.GroupFinderContentData(this);

      LinkDomTypes();

      InitializeModelLoaders();
    }

    private void LoadBucketFiles() {
      foreach (String bucketFileName in m_bucketFiles) {
        String path = $"/resources/systemgenerated/buckets/{bucketFileName}";
        File bucketFile = Assets.FindFile(path);

        if (bucketFile == null) {
          Debug.WriteLine("Unable to find GOM bucket {0}", path);
          continue;
        }

        // DBLB/PBUK parsing requires Length/seek to hop between contained sections. A compressed
        // TOR entry exposes an InflaterInputStream whose Length is intentionally unsupported, so
        // materialize the (small) bucket into a seekable MemoryStream first.
        using (Stream fs = bucketFile.OpenCopyInMemory())
        using (GomBinaryReader br = new GomBinaryReader(fs, Encoding.UTF8, this)) {
          Int32 magic = br.ReadInt32();
          UInt16 majorVersion = br.ReadUInt16();
          UInt16 minorVersion = br.ReadUInt16();

          if (magic != 0x4B554250) {
            throw new InvalidOperationException($"{path} does not begin with PBUK.");
          }

          if (majorVersion != 2 || (minorVersion != 4 && minorVersion != 5)) {
            throw new InvalidOperationException($"Unsupported PBUK version {majorVersion}.{minorVersion} in {path}.");
          }

          // PBUK is a wrapper containing DBLB sections. Beta buckets carry
          // DBLB v1 entries, while live buckets use DBLB v2. The old reader
          // skipped a fixed 0x24-byte header and therefore interpreted v1
          // entries with v2 offsets.
          while (br.BaseStream.Position + 4 <= br.BaseStream.Length) {
            UInt32 sectionLength = br.ReadUInt32();
            if (sectionLength == 0) break;

            Int64 sectionStart = br.BaseStream.Position;
            Int64 sectionEnd = sectionStart + sectionLength;
            if (sectionLength < 12 || sectionEnd > br.BaseStream.Length) {
              throw new InvalidDataException($"Invalid DBLB section length {sectionLength} in {path}.");
            }

            Int32 dblbMagic = br.ReadInt32();
            Int32 dblbVersion = br.ReadInt32();
            if (dblbMagic != 0x424C4244) {
              throw new InvalidOperationException($"DBLB section in {path} has an invalid magic value.");
            }
            if (dblbVersion != 1 && dblbVersion != 2) {
              throw new InvalidOperationException($"Unsupported DBLB version {dblbVersion} in {path}.");
            }
            if (dblbVersion == 1) m_legacyGom = true;

            ReadAllItems(br, br.BaseStream.Position, dblbVersion);
            br.BaseStream.Position = sectionEnd;
          }
        }
      }
    }

    private void LoadBucketList() {
      File gomFile = Assets.FindFile("/resources/systemgenerated/buckets.info");
      if (gomFile == null)
        throw new FileNotFoundException("Unable to find /resources/systemgenerated/buckets.info.");

      // PBCK is small, and reading it through a seekable copy lets us validate the final marker/EOF
      // exactly like Jedipedia does for both beta (1.4 / 500 buckets) and live (1.5 / 997 buckets).
      using (Stream fs = gomFile.OpenCopyInMemory())
      using (GomBinaryReader br = new GomBinaryReader(fs, Encoding.UTF8, this)) {
        Byte[] magic = br.ReadBytes(4);
        if (magic.Length != 4 || magic[0] != (Byte)'P' || magic[1] != (Byte)'B' ||
            magic[2] != (Byte)'C' || magic[3] != (Byte)'K')
          throw new InvalidDataException("buckets.info does not begin with PBCK.");

        UInt16 majorVersion = br.ReadUInt16();
        UInt16 minorVersion = br.ReadUInt16();
        if (majorVersion != 1 || (minorVersion != 4 && minorVersion != 5))
          throw new InvalidDataException(
            $"Unsupported PBCK version {majorVersion}.{minorVersion}; expected 1.4 or 1.5.");

        Int32 numEntries = br.ReadCount32("bucket count");
        if (numEntries != 500 && numEntries != 997)
          throw new InvalidDataException(
            $"Unexpected buckets.info bucket count {numEntries}; expected 500 (32-bit/beta) or 997 (64-bit/live).");

        m_bucketFiles.Clear();
        for (Int32 i = 0; i < numEntries; i++) {
          // PBCK stores bucket-name lengths as a literal byte, not a generic GOM string varint.
          Int32 length = br.ReadByte();
          if (length > br.BaseStream.Length - br.BaseStream.Position)
            throw new EndOfStreamException($"Truncated bucket name at index {i}.");
          String fileName = br.ReadFixedLengthString(length);
          m_bucketFiles.Add(fileName);
        }

        Byte finalMarker = br.ReadByte();
        if (finalMarker != 0xD3)
          throw new InvalidDataException(
            $"Invalid buckets.info final marker 0x{finalMarker:X2}; expected 0xD3.");
        if (br.BaseStream.Position != br.BaseStream.Length)
          throw new InvalidDataException(
            $"Unexpected trailing data in buckets.info ({br.BaseStream.Length - br.BaseStream.Position} bytes).");
      }
    }

    private void LoadBuckets() {
      LoadBucketList();
      LoadBucketFiles();
    }

    private void LoadClientGom() {
      File gomFile = Assets.FindFile("/resources/systemgenerated/client.gom");

      // client.gom is commonly zlib-compressed in beta TOR archives. InflaterInputStream does not
      // implement Length, while DBLB validation needs a bounded/seekable stream.
      using (Stream fs = gomFile.OpenCopyInMemory())
      using (GomBinaryReader br = new GomBinaryReader(fs, Encoding.UTF8, this)) {
        Int32 magic = br.ReadInt32(); // Check DBLB

        if (magic != 0x424C4244) {
          throw new InvalidOperationException("client.gom does not begin with DBLB.");
        }

        Int32 dblbVersion = br.ReadInt32();
        if (dblbVersion != 1 && dblbVersion != 2) {
          throw new InvalidOperationException($"Unsupported client.gom DBLB version {dblbVersion}.");
        }
        if (dblbVersion == 1) m_legacyGom = true;

        ReadAllItems(br, 8, dblbVersion);
      }
    }

    private void LoadPrototype(UInt64 id) {
      String path = $"/resources/systemgenerated/prototypes/{id}.node";
      File protoFile = Assets.FindFile(path);

      if (protoFile == null) {
        Debug.WriteLine("Unable to find {0}", path);
        return;
      }

      using (Stream fs = protoFile.OpenCopyInMemory())
      using (GomBinaryReader br = new GomBinaryReader(fs, Encoding.UTF8, this)) {
        Int32 magicNum = br.ReadInt32(); // Check PROT

        if (magicNum != 0x544F5250) {
          throw new InvalidOperationException($"{path} does not begin with PROT");
        }

        UInt16 versionMajor = br.ReadUInt16();
        UInt16 versionMinor = br.ReadUInt16();
        if (versionMajor != 2 || (versionMinor != 4 && versionMinor != 5))
          throw new InvalidDataException($"Unsupported PROT version {versionMajor}.{versionMinor} in {path}.");

        GomObject proto = m_prototypeLoader.Load(br) as GomObject;
        proto.Dom_ = this;
        proto.Checksum = protoFile.FileInfo.Checksum;

        if (!DomTypeMap.ContainsKey(proto.Id)) {
          DomTypeMap.Add(proto.Id, proto);
          AddToNameLookup(proto);
        }
      }
    }

    private void LoadPrototypes() {
      File prototypeList = Assets.FindFile("/resources/systemgenerated/prototypes.info");

      using (Stream fs = prototypeList.Open())
      using (GomBinaryReader br = new GomBinaryReader(fs, Encoding.UTF8, this)) {
        Int32 magicNum = br.ReadInt32(); // Check PINF

        if (magicNum != 0x464E4950) {
          throw new InvalidOperationException("prototypes.info does not begin with PINF");
        }

        UInt16 versionMajor = br.ReadUInt16();
        UInt16 versionMinor = br.ReadUInt16();
        if (versionMajor != 1 || (versionMinor != 4 && versionMinor != 5))
          throw new InvalidDataException($"Unsupported PINF version {versionMajor}.{versionMinor}; expected 1.4 or 1.5.");

        Int32 numPrototypes = br.ReadCount32("prototype count");
        Int32 protoLoaded = 0;

        for (Int32 i = 0; i < numPrototypes; i++) {
          UInt64 protId = br.ReadIdNumber();
          Byte flag = br.ReadByte();
          if (flag < 1 || flag > 3)
            throw new InvalidDataException($"Invalid prototypes.info node type {flag} at index {i}.");

          if (flag == 1) {
            LoadPrototype(protId);
            protoLoaded++;
          }
        }

        Byte finalMarker = br.ReadByte();
        if (finalMarker != 0xD3)
          throw new InvalidDataException($"Invalid prototypes.info final marker 0x{finalMarker:X2}; expected 0xD3.");
        if (br.BaseStream.CanSeek && br.BaseStream.Position != br.BaseStream.Length)
          throw new InvalidDataException($"Unexpected trailing data in prototypes.info ({br.BaseStream.Length - br.BaseStream.Position} bytes).");

        Debug.WriteLine("Loaded {0} prototype files", protoLoaded);
      }
    }

    private void LoadTypeNames() {
      using (StringReader fs = new(Properties.Resources.gom_type_names)) {
        //var inFilePath = Path.Combine(System.IO.Directory.GetCurrentDirectory(), "gom_type_names.xml");
        //using var fs = System.IO.File.OpenRead(inFilePath);

        XmlDocument doc = new XmlDocument();

        doc.Load(fs);

        XPathNavigator nav = doc.DocumentElement.CreateNavigator();

        foreach (XPathNavigator node in nav.Select("//gom_type")) {
          node.MoveToAttribute("Id", "");

          UInt64 id = UInt64.Parse(node.Value);

          node.MoveToParent();
          node.MoveToAttribute("name", "");

          String name = node.Value;

          // gom_type_names can legitimately contain the same display name for
          // different GOM type IDs (for example GUIAnimation is both an
          // association and a class). Keep the first name -> id mapping so
          // existing lookups remain stable, while still retaining every unique
          // id -> name mapping.
          if (!m_storedNameMap.ContainsKey(name))
            m_storedNameMap.Add(name, id);

          if (!m_storedIdMap.ContainsKey(id))
            m_storedIdMap.Add(id, name);
        }
      }
    }

    public void OutputTypeNames(String path) {
      // Create XML mapping GomType IDs to names
      String outFilePath = $"{path}Gom_Fields.xml";

      using (XmlTextWriter writer = new XmlTextWriter(outFilePath, Encoding.UTF8)) {
        writer.WriteStartDocument();
        writer.WriteStartElement("Gom_Fields");

        foreach (KeyValuePair<Type, Dictionary<String, DomType>> nodeTypeMap in NodeLookup) {
          Type type = nodeTypeMap.Key;

          if (type == typeof(GomObject)) { continue; }

          foreach (KeyValuePair<String, DomType> kvp in nodeTypeMap.Value) {
            DomType domType = kvp.Value;
            String name = kvp.Key;

            writer.WriteStartElement("Gom_Field");
            writer.WriteAttributeString("Id", domType.Id.ToString());
            writer.WriteString(name);
            writer.WriteEndElement();
            writer.WriteString(Environment.NewLine);
          }
        }

        writer.WriteEndElement();
        writer.WriteEndDocument();
      }
    }

    public void ReadAllItems(GomBinaryReader br, Int64 offset, Int32 dblbVersion = 2) {
      if (dblbVersion != 1 && dblbVersion != 2) {
        throw new ArgumentOutOfRangeException(nameof(dblbVersion), "DBLB version must be 1 or 2.");
      }

      while (true) {
        Int64 entryStart;
        try {
          entryStart = br.BaseStream.Position;
        } catch (NotSupportedException) {
          // Some decompression streams expose neither Position nor Length. The loaders below do
          // not need an absolute file offset; keep diagnostics usable without making stream
          // capabilities part of the DBLB format contract.
          entryStart = offset;
        }

        Int32 defLength;
        try {
          defLength = br.ReadInt32();
        } catch (EndOfStreamException) {
          break;
        }

        // Length == 0 means we've read them all.
        if (defLength == 0) {
          break;
        }

        Int32 minimumLength = dblbVersion == 1 ? 20 : 24;
        if (defLength < minimumLength) {
          throw new InvalidDataException(
            $"Invalid DBLB v{dblbVersion} definition length {defLength} at offset 0x{entryStart:X}.");
        }

        // Keep the complete raw entry. DomType loaders use version-aware offsets
        // so beta/v1 and live/v2 definitions can share the same model classes.
        Byte[] defBuffer = new Byte[defLength];
        Buffer.BlockCopy(BitConverter.GetBytes(defLength), 0, defBuffer, 0, 4);
        Int32 remaining = defLength - 4;
        Byte[] entryRemainder = br.ReadBytes(remaining);
        if (entryRemainder.Length != remaining) {
          throw new EndOfStreamException(
            $"Unexpected end of DBLB definition at offset 0x{entryStart:X}.");
        }
        Buffer.BlockCopy(entryRemainder, 0, defBuffer, 4, remaining);

        UInt64 defId = BitConverter.ToUInt64(defBuffer, 8);
        Int32 flagsOffset = dblbVersion == 1 ? 4 : 16;
        UInt16 defFlags = BitConverter.ToUInt16(defBuffer, flagsOffset);
        Int32 defType = (defFlags >> 3) & 0xF;

        using (MemoryStream memStream = new MemoryStream(defBuffer, false))
        using (GomBinaryReader defReader = new GomBinaryReader(memStream, Encoding.UTF8, this, dblbVersion)) {
          if (m_typeLoaderMap.TryGetValue(defType, out DomTypeLoaders.IDomTypeLoader loader)) {
            DomType domType = loader.Load(defReader);
            domType.Dom_ = this;
            domType.Id = defId;

            if (!DomTypeMap.ContainsKey(domType.Id)) {
              DomTypeMap.Add(domType.Id, domType);

              if (String.IsNullOrEmpty(domType.Name)) {
                if (m_storedIdMap.TryGetValue(domType.Id, out String storedTypeName)) {
                  domType.Name = storedTypeName;
                }
              }

              AddToNameLookup(domType);
            }
          } else {
            throw new InvalidOperationException(
              $"No loader for DomType 0x{defType:X} at offset 0x{offset:X} (DBLB v{dblbVersion}).");
          }
        }

        // Definitions are aligned to 8-byte boundaries outside the length field.
        Int32 padding = (8 - (defLength & 0x7)) & 0x7;
        Int64 nextOffset = entryStart + defLength + padding;
        if (nextOffset > br.BaseStream.Length) {
          throw new EndOfStreamException(
            $"DBLB padding exceeds the stream at offset 0x{entryStart:X}.");
        }
        br.BaseStream.Position = nextOffset;
        offset = nextOffset;
      }
    }

    public XDocument ReturnTypeNames() {
      XElement typeNames = new XElement("Gom_Fields");

      foreach (KeyValuePair<Type, Dictionary<String, DomType>> nodeTypeMap in NodeLookup) {
        Type type = nodeTypeMap.Key;

        if (type == typeof(GomObject)) { continue; }

        foreach (KeyValuePair<String, DomType> kvp in nodeTypeMap.Value) {
          DomType domType = kvp.Value;
          String name = kvp.Key;

          typeNames.Add(new XElement("Gom_Field",
                                     new XAttribute("Id", domType.Id.ToString()),
                                     name));
        }
      }

      //typeNames.ReplaceNodes(typeNames.Elements("Gom_Field")
      //.OrderBy(x => (string)x.Attribute("Id")));

      XElement fieldUseInDomClass = new XElement("FieldUseInDomClass");

      CrossLink(); // need to scan nodes to find all values

      foreach (KeyValuePair<String, HashSet<UInt64>> kvp in m_unnamedMap) {
        fieldUseInDomClass.Add(
          new XElement("DomClass",
                       new XAttribute("Id", kvp.Key.ToString()),
                       new XElement("Gom_Fields",
                                    new XAttribute("Id", "UnNamed"),
                                    kvp.Value.ToList()
                                             .Select(x => new XElement("Gom_Field", new XAttribute("Id", x))))));
      }

      foreach (KeyValuePair<String, HashSet<String>> kvp in m_namedMap) {
        IEnumerable<XElement> xe = fieldUseInDomClass.Elements()
                                                     .Where(x => x.Attribute("Id").Value == kvp.Key);
        XElement ta = new XElement("Gom_Fields",
                                   new XAttribute("Id", "Named"),
                                   kvp.Value.ToList()
                                            .Select(x => new XElement("Gom_Field", new XAttribute("Id", x))));

        if (!xe.Any()) {
          fieldUseInDomClass.Add(new XElement("DomClass",
                                              new XAttribute("Id", kvp.Key.ToString()),
                                              ta));
        } else {
          xe.First().Add(ta);
        }
      }

      return new XDocument(new XElement("Wrapper", typeNames, fieldUseInDomClass));
    }

    public void Unload() { // This unloads Assets allowing the loading of different assets without relaunching
      if (!m_loaded) {
        return;
      }

      DomTypeMap = new Dictionary<UInt64, DomType>();
      NodeLookup = new Dictionary<Type, Dictionary<String, DomType>>();

      m_bucketFiles = new List<String>();
      m_prototypeLoader = new DomTypeLoaders.FileInstanceLoader();
      m_storedIdMap = new Dictionary<UInt64, String>();
      m_storedNameMap = new Dictionary<String, UInt64>();
      m_typeLoaderMap = new Dictionary<Int32, DomTypeLoaders.IDomTypeLoader>();

      AlignmentData = null;
      EnhancementData = null;
      FactionData = null;
      GomTypeLoader = null;
      GroupFinderContentData = null;
      StatData = null;
      SocialTierData = null;

      // Flush the ModelLoader Stored entries
      Data.Flush();
      ScriptObjectReader.Flush();
      StringTable.Flush();

      AbilityLoader.Flush();
      AbilityPackageLoader.Flush();
      AchievementLoader.Flush();
      AdvancedClassLoader.Flush();
      AppearanceLoader.Flush();
      AreaLoader.Flush();
      ClassSpecLoader.Flush();
      CodexLoader.Flush();
      CompanionLoader.Flush();
      DecorationLoader.Flush();
      DetailedAppearanceColorLoader.Flush();
      DisciplineLoader.Flush();
      NewDisciplineLoader.Flush();
      EffectLoader.Flush();
      EncounterLoader.Flush();
      ItemLoader.Flush();
      LegacyTitleLoader.Flush();
      MapNoteLoader.Flush();
      MtxStorefrontEntryLoader.Flush();
      NewCompanionLoader.Flush();
      NpcLoader.Flush();
      PackageAbilityLoader.Flush();
      PlaceableLoader.Flush();
      PlayerTitleLoader.Flush();
      QuestLoader.Flush();
      ReputationGroupLoader.Flush();
      ReputationRankLoader.Flush();
      SCFFComponentLoader.Flush();
      SCFFPatternLoader.Flush();
      SCFFShipLoader.Flush();
      SchematicLoader.Flush();
      SchemVariationLoader.Flush();
      SetBonusLoader.Flush();
      StrongholdLoader.Flush();
      TalentLoader.Flush();

      /*foreach (var DomEntry in DomTypeMap)
      {
          if (DomEntry.Value.GetType() == typeof(GomObject))
          {
              ((GomObject)DomEntry.Value).Unload();
          }
      }*/

      GC.Collect();
      m_loaded = false;
      m_schemaLoaded = false;
    }

    #endregion Methods

    #region Properties
    public AMI Ami { get; private set; }
    public Assets Assets { get; }
    public Boolean IsLegacyGom { get { return m_legacyGom; } }
    // 500 buckets is the pre-64-bit client layout (beta/early 32-bit); live 64-bit uses 997.
    // This is intentionally derived from the already-parsed bucket list so compatibility detection does not alter
    // the established GOM parsing path or maintain extra parser state.
    public Boolean IsPre64BitClient { get { return m_bucketFiles != null && m_bucketFiles.Count == 500; } }
    public Data Data { get; private set; }
    public Dictionary<UInt64, DomType> DomTypeMap { get; private set; }
    public GomTypeLoader GomTypeLoader { get; private set; }
    public Dictionary<String, HashSet<String>> NamedMap { get => m_namedMap; }
    public Dictionary<Type, Dictionary<String, DomType>> NodeLookup { get; private set; }
    public ScriptObjectReader ScriptObjectReader { get; private set; }
    public StringTable StringTable { get; private set; }
    public Dictionary<String, HashSet<UInt64>> UnnamedMap { get => m_unnamedMap; }
    public String Version { get; set; }

    // ADD NEW MODELS HERE
    public AbilityLoader AbilityLoader { get; private set; }
    public AbilityPackageLoader AbilityPackageLoader { get; private set; }
    public AchievementCategoryLoader AchievementCategoryLoader { get; private set; }
    public AchievementLoader AchievementLoader { get; private set; }
    public AdvancedClassLoader AdvancedClassLoader { get; private set; }
    public Models.AlignmentData AlignmentData { get; private set; }
    public AppearanceLoader AppearanceLoader { get; private set; }
    public FileLoaders.AreaDatLoader AreaDatLoader { get; private set; }
    public AreaLoader AreaLoader { get; private set; }
    public CodexCatByFactionLoader CdxCatTotalsLoader { get; private set; }
    public ClassSpecLoader ClassSpecLoader { get; private set; }
    public CodexLoader CodexLoader { get; private set; }
    public CollectionLoader CollectionLoader { get; private set; }
    public CompanionLoader CompanionLoader { get; private set; }
    public ConquestLoader ConquestLoader { get; private set; }
    public ConversationLoader ConversationLoader { get; private set; }
    public DecorationLoader DecorationLoader { get; private set; }
    public DetailedAppearanceColorLoader DetailedAppearanceColorLoader { get; private set; }
    public DisciplineLoader DisciplineLoader { get; private set; }
    public NewDisciplineLoader NewDisciplineLoader { get; private set; }
    public EffectLoader EffectLoader { get; private set; }
    public EncounterLoader EncounterLoader { get; private set; }
    public Models.EnhancementData EnhancementData { get; private set; }
    public Models.FactionData FactionData { get; private set; }
    public Models.GroupFinderContentData GroupFinderContentData { get; private set; }
    public ItemLoader ItemLoader { get; private set; }
    public LegacyTitleLoader LegacyTitleLoader { get; private set; }
    public MapNoteLoader MapNoteLoader { get; private set; }
    public MtxStorefrontEntryLoader MtxStorefrontEntryLoader { get; internal set; }
    public NewCompanionLoader NewCompanionLoader { get; private set; }
    public NpcLoader NpcLoader { get; private set; }
    public PackageAbilityLoader PackageAbilityLoader { get; private set; }
    public PlaceableLoader PlaceableLoader { get; private set; }
    public PlayerTitleLoader PlayerTitleLoader { get; private set; }
    public QuestBranchLoader QuestBranchLoader { get; private set; }
    public QuestLoader QuestLoader { get; private set; }
    public QuestStepLoader QuestStepLoader { get; private set; }
    public QuestTaskLoader QuestTaskLoader { get; private set; }
    public ReputationGroupLoader ReputationGroupLoader { get; private set; }
    public ReputationRankLoader ReputationRankLoader { get; private set; }
    public FileLoaders.RoomDatLoader RoomDatLoader { get; private set; }
    public SCFFColorOptionLoader SCFFColorOptionLoader { get; private set; }
    public SCFFComponentLoader SCFFComponentLoader { get; private set; }
    public SCFFPatternLoader SCFFPatternLoader { get; private set; }
    public SCFFShipLoader SCFFShipLoader { get; private set; }
    public SchematicLoader SchematicLoader { get; private set; }
    public SchematicVariationLoader SchemVariationLoader { get; private set; }
    public SetBonusLoader SetBonusLoader { get; private set; }
    public Models.SocialTierData SocialTierData { get; private set; }
    public SpawnerLoader SpawnerLoader { get; private set; }
    public Models.StatData StatData { get; private set; }
    public StrongholdLoader StrongholdLoader { get; private set; }
    public TalentLoader TalentLoader { get; private set; }

    #endregion Properties

  }
}
