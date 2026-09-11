using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using System.Xml;
using System.Xml.Linq;
using GomLib;
using GomLib.Models;
using MessageBox = System.Windows.Forms.MessageBox;

namespace PugTools {
  internal partial class Tools : Form {
    private TorArchive.Assets _currentAssets;
    private DataObjectModel _currentDom;
    private TorArchive.Assets _previousAssets;
    private DataObjectModel _previousDom;
    private Boolean _sql = false;
    private const Boolean _verbose = true;
    static Boolean s_loaded = false;
    private static String s_outputTypeName = "XML";
    static readonly String s_prefix = ""; //"Verbose";
    static Boolean s_removeUnchanged = true;
    private static Boolean s_verbose = true;

    public TorArchive.Assets CurrentAssets {
      get => _currentAssets;
      set => _currentAssets = value;
    }
    public DataObjectModel CurrentDom {
      get => _currentDom;
      set => _currentDom = value;
    }
    public static Boolean FormOpen { get; set; } = _verbose;
    public static List<String> Localizations { get; } = new List<String> {
      "enMale",
      "enFemale",
      "frMale",
      "frFemale",
      "deMale",
      "deFemale"
    };
    public TorArchive.Assets PreviousAssets {
      get => _previousAssets;
      set => _previousAssets = value;
    }
    public DataObjectModel PreviousDom {
      get => _previousDom;
      set => _previousDom = value;
    }
    public static Boolean Verbose {
      get => s_verbose;
      set => s_verbose = value;
    }

    public Tools() {
      Config.Load();
      LocalizationResolver.ApplyRequested(Config.Language);
      InitializeComponent();
      InitializeShaderBrowserButton();
      InitializeDomBrowserButton();
      txtAssetsPath.Text = Config.AssetsPath;
      chkAssetsUsePTS.Checked = Config.AssetsUsePTS;
      txtPrevAssetsPath.Text = Config.PrevAssetsPath;
      chkPrevAssetsUsePTS.Checked = Config.PrevAssetsUsePTS;
      txtExtractPath.Text = Config.ExtractPath;
      chkCrossLinkDom.Checked = Config.CrossLinkDOM;
      cbxExtractors.Items.AddRange(new Object[] {
        "Abilities",
        "Codex",
        "NPCs",
        "Quests",
        "Areas",
        "Collections",
        "Achievements",
        "Cartel Market",
        "Companions",
        "GSF Ships",
        "Items",
        "Item Appearances",
        "Raw GOM",
        "Icons",
        "(Everything)",
        "Conversations",
        // "Filenames",  // Disabled by SWTOR_Miner
        "Talents",
        "String Tables",
        "Schematics",
        "Decorations",
        "Strongholds",
        "Conquests",
        "Advanced Classes",
        "Disciplines",
        "Find New MTX Images",
        "Achievement Categories",
        "Verify Hashes",
        "Tooltips",
        "Set Bonuses",
        "Codex Category Totals",
        "Schematic Variations",
        "Build Bnk ID Dict"
      });
      cbxExtractors.SelectedIndex = 0;
      cbxExtractFormat.SelectedIndex = 3;
      int languageIndex = cbxLanguage.Items.IndexOf(Config.Language);
      cbxLanguage.SelectedIndex = languageIndex >= 0 ? languageIndex : 0;
      FormOpen = false;
      txtSqlUsername.Enabled = false;
      txtSqlAddress.Enabled = false;
      txtSqlName.Enabled = false;
      txtSqlPassword.Enabled = false;
      FormClosing += ToolsFormClosing;
    }

    private void ToolsFormClosing(Object sender, FormClosingEventArgs e) {
      // Do not create/load the multi-million-entry dictionary just because the application is
      // closing. If no browser/tool used it during this process there cannot be session changes.
      if (!TorArchive.HashDictionaryInstance.IsCreated) return;

      TorArchive.HashDictionaryInstance hashData = TorArchive.HashDictionaryInstance.Instance;
      Int32 pendingNames = hashData.Dictionary.PendingFileNameChanges;
      if (pendingNames <= 0) return;

      DialogResult save = MessageBox.Show(
        $"{pendingNames:N0} new or updated filename(s) were discovered during this session.\r\n\r\n"
          + "Write these filename changes to the hash dictionary before closing?",
        "Save discovered filenames?",
        MessageBoxButtons.YesNoCancel,
        MessageBoxIcon.Question
      );

      if (save == DialogResult.Cancel) {
        e.Cancel = true;
        return;
      }

      if (save != DialogResult.Yes) return;

      try {
        hashData.Dictionary.SaveCompactHashListOnly();
      }
      catch (Exception ex) {
        DialogResult closeWithoutSaving = MessageBox.Show(
          "The hash dictionary could not be saved:\r\n\r\n" + ex.Message
            + "\r\n\r\nClose PugTools without saving these filename changes?",
          "Hash dictionary save failed",
          MessageBoxButtons.YesNo,
          MessageBoxIcon.Error
        );
        if (closeWithoutSaving != DialogResult.Yes) e.Cancel = true;
      }
    }
    public void CreateGzip(String filename) {
      String filepath = String.Join("", Config.ExtractPath, s_prefix, filename);
      FlushTempTables(); //need to clear out memory
      Clearlist2();
      AddToList2("Writing compressed data file");

      if (!File.Exists(filepath)) return;

      using (FileStream readstream = new FileStream(filepath, FileMode.Open, FileAccess.Read)) {
        if (readstream == null) return;
        if (readstream.Length == 0) return;

        // byte[] byteArray = new byte[readstream.Length - 1];
        // Reading a 300mb+ files into memory caused issues
        // readstream.Read(byteArray, 0, byteArray.Length);

        using FileStream outFileStream =
          new FileStream(String.Join("", filepath, ".gz"), FileMode.Create, FileAccess.Write);
        using System.IO.Compression.GZipStream gzip =
          new System.IO.Compression.GZipStream(
            outFileStream,
            System.IO.Compression.CompressionMode.Compress
          );
        //gzip.Write(byteArray, 0, byteArray.Length);
        readstream.CopyTo(gzip);
      }

      File.Delete(filepath);
      AddToList2("Done.");
    }
    // Deletes empty files that were created for streaming output
    public static void DeleteEmptyFile(String filename, Int32 count) {
      String filepath = String.Join("", Config.ExtractPath, s_prefix, filename);
      FileInfo fInfo = new FileInfo(filepath);

      if (fInfo != null)
        if (fInfo.Length == 0 || count == 0) File.Delete(filepath);
    }
    private void FlushTempTables() {
      CurrentDom.StringTable.Flush();
      CurrentDom.DecorationLoader.Flush();
      CurrentDom.Ami.Flush();
      CurrentDom.CollectionLoader.Flush();
      CurrentDom.MtxStorefrontEntryLoader.Flush();
      CurrentDom.CompanionLoader.Flush();

      if (PreviousDom != null) {
        PreviousDom.StringTable.Flush();
        PreviousDom.DecorationLoader.Flush();
        PreviousDom.Ami.Flush();
        PreviousDom.CollectionLoader.Flush();
        PreviousDom.MtxStorefrontEntryLoader.Flush();
        PreviousDom.CompanionLoader.Flush();
      }
    }
    public void GetAll() {
      Clearlist2();

      ExtractCheckForm testFile = new ExtractCheckForm();
      DialogResult result = testFile.ShowDialog();

      if (result == DialogResult.OK) {
        LoadData();
        DisableButtons();
        List<String> extensions = testFile.GetTypes();
        ExportICONS1 = extensions.Contains("ICONS");

        if (extensions.Contains("CDXCAT")) {
          DisableButtons();
          GetPrototypeObjects(
            "CodexCategoryTotals",
            "cdxCategoryTotalsPrototype",
            "cdxFactionToClassToPlanetToTotalLookupList"
          );
        }

        if (extensions.Contains("SBN")) {
          DisableButtons();
          GetPrototypeObjects("SetBonuses", "itmSetBonusesPrototype", "itmSetBonuses");
        }

        if (extensions.Contains("TORC")) {
          DisableButtons();
          GetTorc();
        }

        if (extensions.Contains("MISC")) {
          DisableButtons();
          FindNewMtxImages();
        }

        if (extensions.Contains("STB")) {
          DisableButtons();
          AddToList1("Getting String Tables.");
          GetStrings();
        }

        if (extensions.Contains("GOM")) {
          DisableButtons();
          AddToList1("Getting Raw GOM.");
          _exportGOM = extensions.Contains("EXP");
          GetRaw();
        }

        if (extensions.Contains("AC")) {
          DisableButtons();
          GetObjects("class.pc.advanced", "AdvancedClasses");
        }

        if (extensions.Contains("CNQ")) {
          DisableButtons();
          GetPrototypeObjects("Conquests", "wevConquestInfosPrototype", "wevConquestTable");
        }

        if (extensions.Contains("ABL")) {
          DisableButtons();
          GetObjects("abl.", "Abilities");
        }

        if (extensions.Contains("APN")) {
          DisableButtons();
          GetObjects("apn.", "AbilityPackages");
        }

        if (extensions.Contains("ACH")) {
          DisableButtons();
          GetObjects("ach.", "Achievements");
        }

        if (extensions.Contains("APT")) {
          DisableButtons();
          GetObjects("apt.", "Strongholds");
        }

        if (extensions.Contains("SPN")) {
          DisableButtons();
          GetObjects("spn.", "Spawners");
        }

        if (extensions.Contains("AREA")) {
          DisableButtons();
          AddToList1("Getting Areas.");
          GetPrototypeObjects(
            "Areas",
            "mapAreasDataProto",
            "mapAreasDataObjectList"
          ); // getAreas();
        }

        if (extensions.Contains("CDX")) {
          DisableButtons();
          GetObjects("cdx.", "CodexEntries");
        }

        if (extensions.Contains("CLASS")) {
          DisableButtons();
          GetObjects("class.", "Classes");
        }

        if (extensions.Contains("CNV")) {
          DisableButtons();
          GetObjects("cnv.", "Conversations");
        }

        if (extensions.Contains("DIS")) {
          DisableButtons();
          GetObjects("dis.", "Disciplines");
        }

        if (extensions.Contains("DEC")) {
          DisableButtons();
          GetObjects("dec.", "Decorations");
        }

        if (extensions.Contains("ABLEFF")) {
          DisableButtons();
          GetObjects("eff.", "Effects");
        }

        if (extensions.Contains("ITM")) {
          DisableButtons();
          GetObjects("itm.", "Items");
        }

        if (extensions.Contains("NPC")) {
          DisableButtons();
          ExportNPP1 = extensions.Contains("NPP");
          GetObjects("npc.", "Npcs");
        }

        if (extensions.Contains("QST")) {
          DisableButtons();
          GetObjects("qst.", "Quests");
        }

        if (extensions.Contains("SCHEM")) {
          DisableButtons();
          GetObjects("schem.", "Schematics");
        }

        if (extensions.Contains("TAL")) {
          DisableButtons();
          GetObjects("tal.", "Talents");
        }

        if (extensions.Contains("COL")) {
          DisableButtons();
          GetPrototypeObjects(
            "Collections",
            "colCollectionItemsPrototype",
            "colCollectionItemsData"
          );

          // New/updated filenames are already applied to the live process-wide dictionary.
          // Re-reading the complete multi-million-row hash list here was both expensive and capable
          // of discarding unsaved session discoveries. Helper sets are incremental after creation.
          TorArchive.HashDictionaryInstance.Instance.Dictionary.CreateHelpers();
        }

        if (extensions.Contains("CMP")) {
          DisableButtons();
          GetObjects("nco.", "NewCompanions");
          GetPrototypeObjects(
            "Companions",
            "chrCompanionInfo_Prototype",
            "chrCompanionInfoData"
          );
        }

        if (extensions.Contains("MTX")) {
          DisableButtons();
          GetPrototypeObjects(
            "MtxStoreFronts",
            "mtxStorefrontInfoPrototype",
            "mtxStorefrontData"
          );

          //Reload hash dict.
          // New/updated filenames are already applied to the live process-wide dictionary.
          // Re-reading the complete multi-million-row hash list here was both expensive and capable
          // of discarding unsaved session discoveries. Helper sets are incremental after creation.
          TorArchive.HashDictionaryInstance.Instance.Dictionary.CreateHelpers();
        }

        if (extensions.Contains("GSF")) {
          DisableButtons();
          GetPrototypeObjects("Ships", "scFFShipsDataPrototype", "scFFShipsData");
        }

        if (extensions.Contains("IPP")) {
          DisableButtons();
          AddToList1("Getting Appearances.");
          GetObjects("ipp.", "ItemAppearances");
          GetObjects("npp.", "NpcAppearances");
          //getItemApps();
        }

        if (extensions.Contains("SCHVARI")) {
          DisableButtons();
          AddToList1("Getting Schematic Variations");
          GetPrototypeObjects(
            "SchematicVariations",
            "prfSchematicVariationsPrototype",
            "prfSchematicVariationMasterList"
          );
        }

        AddToList1("Completed extraction of all supported objects.");
      }

      GC.Collect();
      EnableButtons();
    }
    public void GetFqn(String itemid) {
      Clearlist2();

      LoadData();
      IEnumerable<GomObject> itmList =
        CurrentDom.GetObjectsStartingWith("").Where(obj => obj.Name.Contains(itemid));
      Double i = 0;
      Double ttl = itmList.Count();
      Boolean append = false;
      String cleanedQuery =
        String.Join("_", itemid.Split(Path.GetInvalidFileNameChars())).TrimEnd('.');
      String filename = "searchOutput-" + cleanedQuery + ".xml";
      XElement root = new XElement("Root");

      ProcessList(ref itmList, ref root);

      XDocument xmlDoc = new XDocument(root);

      WriteFile(xmlDoc, filename, append);

      String txt = "Output GOM files for analysis?";
      String cpt = "GOm file Output";
      if (MessageBox.Show(txt, cpt, MessageBoxButtons.YesNo) == DialogResult.Yes) {
        foreach (var gomItm in itmList) {
          String path = Config.ExtractPath;
          String file = "\\GOM\\" + gomItm.ToString().Replace("/", ".") + ".xml";

          if (!Directory.Exists(path + "GOM\\")) { Directory.CreateDirectory(path + "GOM\\"); }

          WriteFile(new XDocument(gomItm.Print()), file, false, true);
          i++;
        }
      }
      itmList = null;
      AddToList1(
        "the xml files have been generated there were "
        + ttl
        + " objects that matched your criteria"
      );
      MessageBox.Show(
        "the xml files have been generated there were "
        + ttl
        + " objects that matched your criteria"
      );
      EnableButtons();
    }
    public void GetRaw() {
      Clearlist2();
      Double i = 0;
      String n = Environment.NewLine;

      LoadData();
      CurrentDom.OutputTypeNames(Config.ExtractPath + s_prefix);

      List<GomObject> itmList = CurrentDom.GetObjectsStartingWith(""); // .Where(obj => obj.Name.Contains("."));
      Boolean append = false;
      String changed = "";

      if (chkBuildCompare.Checked) changed = "Changed";

      if (s_outputTypeName == "Text") {
        String filename = "GOM_Items.txt";
        StringBuilder txtFile = new StringBuilder();

        foreach (GomObject gomItm in itmList) {
          txtFile.Append(gomItm + n);
          i++;
        }

        WriteFile(txtFile.ToString(), changed + filename, append);
      } else {
        ProcessGom();

        if (chkBuildCompare.Checked && _exportGOM) ProcessEffectChanges();

        //ProcessGomFields();
        //ProcessGomFields(); //this is providing no useful info currently, and takes a fuckton of time to run.
      }

      //addtolist("The GOM Item List has been generated there are " + i + " GOM Items");
      //MessageBox.Show("the raw list has been generated there are " + i + " Objects");
      EnableButtons();
    }
    private void ReportArchiveLoadWarnings(TorArchive.Assets assets, String label) {
      if (assets == null) return;
      String[] warnings = assets.ArchiveLoadWarnings
        .Where(warning => !String.IsNullOrWhiteSpace(warning))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
      if (warnings.Length == 0) return;

      AddToList1($"{label}: skipped {warnings.Length:N0} invalid TOR archive(s); details are shown below.");
      foreach (String warning in warnings) AddToList2(label + ": " + warning);
    }

    public void LoadData() {
      if (!s_loaded) {
        Clearlist();
        ContinuousProgress();
        AddToList1("Loading Current Data Object Model.");

        Boolean usePTS = chkAssetsUsePTS.Checked;
        CurrentAssets =
          TorArchive.AssetHandler.Instance.GetCurrentAssets(txtAssetsPath.Text, usePTS);
        LocalizationResolver.Apply(CurrentAssets, Config.Language);
        CurrentDom = DomHandler.Instance.GetCurrentDOM(CurrentAssets);
        CurrentDom.Version = PatchVersion;

        Clearlist();
        AddToList1("Loading Current Data Object Model. - Done");
        ReportArchiveLoadWarnings(CurrentAssets, "Current assets");

        if (chkBuildCompare.Checked && txtPrevAssetsPath.Text != "") {
          AddToList1("Loading Previous Data Object Model.");

          PreviousAssets =
            TorArchive.AssetHandler.Instance.GetPreviousAssets(
              txtPrevAssetsPath.Text,
              chkPrevAssetsUsePTS.Checked
            );
          PreviousDom = DomHandler.Instance.GetPreviousDOM(_previousAssets);

          Clearlist();
          AddToList1("Loading Current Data Object Model. - Done");
          AddToList1("Loading Previous Data Object Model. - Done");
          ReportArchiveLoadWarnings(CurrentAssets, "Current assets");
          ReportArchiveLoadWarnings(PreviousAssets, "Previous assets");
        }

        if (chkCrossLinkDom.Checked) {
          AddToList1("Crosslinking Current Data Object Model.");
          CurrentDom.CrossLink();
          Clearlist();
          AddToList1("Crosslinking Data Object Model. - Done");

          if (chkBuildCompare.Checked) {
            AddToList1("Crosslinking Previous Data Object Model.");
            PreviousDom.CrossLink();
            ProcessGomFields();
            Clearlist();
            AddToList1("Crosslinking Data Object Model. - Done");
            AddToList1("Crosslinking Previous Data Object Model. - Done");
          }
        } else if (chkSmartLinkDom.Checked) {
          AddToList1("Crosslinking Current Data Object Model.");
          Smart.Link(CurrentDom, AddToList2);
          Clearlist();
          AddToList1("Crosslinking Data Object Model. - Done");

          if (chkBuildCompare.Checked) {
            AddToList1("Crosslinking Previous Data Object Model.");
            ProcessGomFields();
            Clearlist();
            AddToList1("Crosslinking Data Object Model. - Done");
            AddToList1("Crosslinking Previous Data Object Model. - Done");
          }
        }

        s_loaded = true;
        ClearProgress();
      }
    }
    private static GameObject LoadGameObject(DataObjectModel dom,
                                                           GomObject gObject,
                                                           Boolean classOverride) {

      GameObject obj;
      String gomPrefix = gObject.Name[..4];

      switch (gomPrefix) {
        case "itm.":
          obj = new Item();
          dom.ItemLoader.Load(obj, gObject);
          break;
        case "abl.":
          obj = new Ability();
          if (!gObject.Name.Contains('/')) {
            dom.AbilityLoader.Load(obj, gObject);
          } else {
            obj = new Effect();
            dom.EffectLoader.Load(obj, gObject);
          }
          break;
        case "npc.":
          obj = new Npc();
          dom.NpcLoader.Load(obj, gObject);
          break;
        case "qst.":
          obj = new Quest();
          dom.QuestLoader.Load(obj, gObject);
          break;
        case "cdx.":
          obj = new Codex();
          dom.CodexLoader.Load(obj, gObject);
          break;
        case "cnv.":
          obj = new Conversation();
          dom.ConversationLoader.Load(obj, gObject);
          break;
        case "dis.":
          obj = new NewDiscipline();
          dom.NewDisciplineLoader.Load(obj, gObject);
          break;
        case "ach.":
          obj = new Achievement();
          dom.AchievementLoader.Load(obj, gObject);
          break;
        case "tal.":
          obj = new Talent();
          dom.TalentLoader.Load(obj, gObject);
          break;
        case "sche":
          obj = new Schematic();
          dom.SchematicLoader.Load((Schematic)obj, gObject);
          break;
        case "ipp.":
          obj = (ItemAppearance)dom.AppearanceLoader.Load(gObject);
          break;
        case "npp.":
          obj = dom.AppearanceLoader.LoadNpp(gObject);
          break;
        case "dec.":
          obj = new Decoration();
          dom.DecorationLoader.Load(obj, gObject);
          break;
        case "apt.":
          obj = new Stronghold();
          dom.StrongholdLoader.Load(obj, gObject);
          break;
        case "apc.":
          obj = dom.AbilityPackageLoader.Load(gObject);
          break;
        case "clas":
          if (classOverride && gObject.Name.StartsWith("class.pc.advanced."))
            obj = dom.AdvancedClassLoader.Load(gObject);
          else
            obj = dom.ClassSpecLoader.Load(gObject);
          break;
        case "nco.":
          obj = dom.NewCompanionLoader.Load(gObject);
          break;
        case "apn.":
          obj = dom.AbilityPackageLoader.Load(gObject);
          break;
        case "spn.":
          obj = dom.SpawnerLoader.Load(gObject);
          break;
        default:
          throw new NotImplementedException();
      }

      gObject.Unload();
      return obj;
    }
    public static Boolean PathContainsLiveAssets(String path) {
      String assetsPath = TorArchive.Assets.GetAssetsDirectory(path);
      if (!Directory.Exists(assetsPath)) return false;

      // Do not gate valid beta/legacy/current installations on one historical "main" filename.
      // Explicit swtor_ archives identify LIVE; neutral beta/dev archives count as LIVE/legacy only
      // when there is no explicit PTS branch, mirroring Jedipedia's "common archive" handling.
      String[] torFiles = Directory.EnumerateFiles(assetsPath, "*", SearchOption.TopDirectoryOnly)
        .Where(file => Path.GetExtension(file).Equals(".tor", StringComparison.OrdinalIgnoreCase))
        .ToArray();
      Boolean hasPts = torFiles.Any(file => Path.GetFileName(file).StartsWith("swtor_test_", StringComparison.OrdinalIgnoreCase));
      Boolean hasExplicitLive = torFiles.Any(file => {
        String name = Path.GetFileName(file);
        return name.StartsWith("swtor_", StringComparison.OrdinalIgnoreCase)
          && !name.StartsWith("swtor_test_", StringComparison.OrdinalIgnoreCase);
      });
      return hasExplicitLive || (!hasPts && torFiles.Length > 0);
    }
    public static Boolean PathContainsPTSAssets(String path) {
      String assetsPath = TorArchive.Assets.GetAssetsDirectory(path);
      if (!Directory.Exists(assetsPath)) return false;

      return Directory.EnumerateFiles(assetsPath, "*", SearchOption.TopDirectoryOnly)
        .Where(file => Path.GetExtension(file).Equals(".tor", StringComparison.OrdinalIgnoreCase))
        .Any(file => Path.GetFileName(file).StartsWith("swtor_test_", StringComparison.OrdinalIgnoreCase));
    }
    public static String PrepExtractPath(String filename) {
      String subPath = "";

      if (filename.Contains('\\'))
        subPath = filename[..filename.LastIndexOf('\\')];

      if (!Directory.Exists(Config.ExtractPath + s_prefix + subPath))
        Directory.CreateDirectory(Config.ExtractPath + s_prefix + subPath);

      return Config.ExtractPath + s_prefix + filename;
    }
    private void ProcessList(ref IEnumerable<GomObject> itmList, ref XElement root) {

      root.Add(
        new XElement("Abilities"),
        new XElement("Achievements"),
        new XElement("CodexEntries"),
        new XElement("Conversations"),
        new XElement("Items"),
        new XElement("Npcs"),
        new XElement("Quests"),
        new XElement("Schematics"),
        new XElement("Talents")
      );

      foreach (GomObject gomItm in itmList) {
        XElement convertedElement = ConvertToXElement(gomItm);

        if (convertedElement != null) {
          switch (convertedElement.Name.LocalName) {
            case "Item":
              root.Element("Items").Add(convertedElement);
              break;
            case "Ability":
              root.Element("Abilities").Add(convertedElement);
              break;
            case "Codex":
              root.Element("CodexEntries").Add(convertedElement);
              break;
            case "Npc":
              root.Element("Npcs").Add(convertedElement);
              break;
            case "Quest":
              root.Element("Quests").Add(convertedElement);
              break;
            case "Talent":
              root.Element("Talents").Add(convertedElement);
              break;
            case "Achievement":
              root.Element("Achievements").Add(convertedElement);
              break;
            case "Conversation":
              root.Element("Conversations").Add(convertedElement);
              break;
            case "Schematic":
              root.Element("Schematics").Add(convertedElement);
              break;
            default:
              break;
          }
        }
      }

      AddToList1(
        root.Element("Items").Elements("Item").Count()
        + " matching Items found."
      );

      root.Element("Items").ReplaceWith(Sort(root.Element("Items")));

      AddToList1(
        root.Element("Abilities").Elements("Ability").Count()
        + " matching Abilities found."
      );
      AddToList1(
        root.Element("CodexEntries").Elements("Codex").Count()
        + " matching CodexEntries found."
      );
      AddToList1(
        root.Element("Npcs").Elements("Npc").Count()
        + " matching Npcs found."
      );
      AddToList1(
        root.Element("Quests").Elements("Quest").Count()
        + " matching Quests found."
      );
      AddToList1(
        root.Element("Talents").Elements("Talent").Count()
        + " matching Talents found."
      );
      AddToList1(
        root.Element("Achievements").Elements("Achievement").Count()
        + " matching Achievements found."
      );
      AddToList1(
        root.Element("Conversations").Elements("Conversation").Count()
        + " matching Conversations found."
      );

      Clearlist2();
    }
    private static XElement Sort(XElement element) {
      String root = element.Name.ToString();

      switch (root) {
        case "Items": return SortItems(element);
        case "Abilities": return SortAbilities(element);
        case "Achievements": return SortAchievements(element);
        case "Npcs": return SortNpcs(element);
        case "CodexEntries": return SortCodices(element);
        case "Conversations": return SortConversations(element);
        case "Quests": return SortQuests(element);
        case "Talents": return SortTalents(element);
        case "MtxStoreFronts": return SortMtxStoreFronts(element);
        case "Collections": return SortCollections(element);
        case "Companions": return SortCompanions(element);
        case "Ships": return SortShips(element);
        case "Schematics": return SortSchematics(element);
        case "Decorations": return SortDefault(element);
        case "ItemAppearances": return SortDefault(element);
        default: return element;
      }
    }
    private static XElement SortDefault(XElement element) {
      element.ReplaceNodes(
        element.Elements().OrderBy(
          x => (String)x.Attribute("Status")
        ).ThenBy(x => (String)x.Element("Fqn"))
      );

      return element;
    }
    public void Unload() {
      if (s_loaded) {
        Clearlist();
        AddToList1("Unloading Data Object Model.");
        s_loaded = false;
        _currentAssets = null;
        CurrentDom = null;
        _previousAssets = null;
        PreviousDom = null;
        Clearlist2();
        Clearlist();
        AddToList1("Unloading Data Object Model. - Done");
      }
    }
    private static void UnloadAll() {
      TorArchive.AssetHandler.Instance.UnloadAllAssets();
      DomHandler.Instance.UnloadAllDOM();
      s_loaded = false;
    }
    private static void UnloadCurrent() {
      TorArchive.AssetHandler.Instance.UnloadCurrentAssets();
      DomHandler.Instance.UnloadCurrentDOM();
      s_loaded = false;
    }
    private static void UnloadPrevious() {
      TorArchive.AssetHandler.Instance.UnloadPreviousAssets();
      DomHandler.Instance.UnloadPreviousDOM();
      s_loaded = false;
    }
    private void VerifyHashes() {
      Clearlist2();

      LoadData();
      AddToList1("Verifying Game Object Hashes");

      Dictionary<String, Boolean> gameObjects = new Dictionary<String, Boolean> {
        {"mpn.", true },
        {"ach.", true},
        {"abl.", true},
        {"apn.", true},
        {"cdx.", true},
        {"cnv.", true},
        {"dis.", true},
        {"npc.", true},
        {"qst.", true},
        {"tal.", true},
        {"sche", true},
        {"dec.", true},
        {"itm.", true},
        {"apt.", true},
        {"apc.", true},
        {"class.",true},
        {"ipp.",true},
        {"npp.",true},
      };

      foreach (KeyValuePair<String, Boolean> gameObj in gameObjects) {
        ClearProgress();
        IEnumerable<GomObject> gomList = CurrentDom.GetObjectsStartingWith(gameObj.Key)
                                                   .Where(x => !x.Name.Contains('/'));
        Int32 count = gomList.Count();
        Int32 i = 0;

        AddToList2($"Checking {gameObj.Key}");

        foreach (GomObject gomObj in gomList) {
          ProgressUpdate(i, count);

          GameObject item1 = GameObject.Load(gomObj);
          GameObject item2 = GameObject.Load(gomObj);

          if (item1 == null) continue;

          if (item1.GetHashCode() != item2.GetHashCode()) {
            gameObjects[gameObj.Key] = false;
            AddToList2($"Failed: {gameObj.Key}");
            break; // breaks inner loop.
          }

          i++;
        }

        if (gameObjects[gameObj.Key]) {
          AddToList2($"Passed: {gameObj.Key}");
        }
      }

      String completeStr = gameObjects.Values.ToList().TrueForAll(x => x) ? "Passed." : "Failed.";

      AddToList1(completeStr);
      AddToList1("Verifying Prototype Game Object Hashes");

      Boolean failed = false;
      Dictionary<String, String> protoGameObjects = new Dictionary<String, String> {
        {"mtxStorefrontInfoPrototype", "mtxStorefrontData"},
        {"colCollectionItemsPrototype", "colCollectionItemsData"},
        {"chrCompanionInfo_Prototype", "chrCompanionInfoData"},
        {"scFFShipsDataPrototype", "scFFShipsData"},
        {"wevConquestInfosPrototype", "wevConquestTable"},
        {"achCategoriesTable_Prototype", "achCategoriesData"}
      };

      foreach (KeyValuePair<String, String> gameObj in protoGameObjects) {
        Dictionary<Object, Object> currentDataProto = new Dictionary<Object, Object>();
        GomObject currentDataObject = CurrentDom.GetObject(gameObj.Key);

        if (currentDataObject != null) { // Fix to ensure old game assets don't throw exceptions.
          currentDataProto = ReadPrototypeTable(currentDataObject, gameObj.Value, out _);
          currentDataObject.Unload();
        }

        ClearProgress();

        Int32 count = currentDataProto.Count;
        Int32 i = 0;

        AddToList2($"Checking {gameObj.Key}");

        Boolean localFail = false;

        foreach (KeyValuePair<Object, Object> kvp in currentDataProto) {
          ProgressUpdate(i, count);

          PseudoGameObject item1 = PseudoGameObject.LoadFromProtoName(gameObj.Key,
                                                                      CurrentDom,
                                                                      kvp.Key,
                                                                      (GomObjectData)kvp.Value);
          PseudoGameObject item2 = PseudoGameObject.LoadFromProtoName(gameObj.Key,
                                                                      CurrentDom,
                                                                      kvp.Key,
                                                                      (GomObjectData)kvp.Value);

          if (item1 == null) continue;

          Int32 hash1 = item1.GetHashCode();
          Int32 hash2 = item2.GetHashCode();

          if (item1.GetHashCode() != item2.GetHashCode()) {
            AddToList2($"Failed: {gameObj.Key}");
            failed = true;
            localFail = true;
            break; // breaks inner loop. 
          }

          i++;
        }

        if (!localFail) AddToList2($"Passed: {gameObj.Key}");
      }

      completeStr = failed ? "Failed." : "Passed.";

      AddToList1(completeStr);
      EnableButtons();
    }
    public static void WriteFile(MemoryStream content, String filename) {
      String subPath = "";
      filename = filename.Replace('/', '\\');

      if (filename.Contains('\\'))
        subPath = filename[..filename.LastIndexOf('\\')];

      if (!Directory.Exists(Config.ExtractPath + s_prefix + subPath))
        Directory.CreateDirectory(Config.ExtractPath + s_prefix + subPath);

      using FileStream file2 =
        new FileStream(Config.ExtractPath + s_prefix + filename, FileMode.OpenOrCreate);
      content.Position = 0;
      content.CopyTo(file2); // this works when writeto doesn't for some streams.
    }
    public static void WriteFile(String content, String filename, Boolean append) {
      String subPath = "";

      if (filename.Contains('\\'))
        subPath = filename[..filename.LastIndexOf('\\')];

      if (!Directory.Exists(Config.ExtractPath + s_prefix + subPath))
        Directory.CreateDirectory(Config.ExtractPath + s_prefix + subPath);

      using StreamWriter file2 =
        new StreamWriter(Config.ExtractPath + s_prefix + filename, append);
      file2.Write(content);
    }
    private void WriteFile(XDocument content, String filename, Boolean append, Boolean trimEmpty) {
      if (trimEmpty)
        content.Descendants().Where(e => e.IsEmpty || String.IsNullOrWhiteSpace(e.Value)).Remove();
      if (content.Root.IsEmpty) return;

      switch (content.Root.Name.ToString()) {
        case "AdvancedClasses":
          foreach (var child in content.Root.Elements()) {
            if (child.Descendants().Count() >= 3
                || child.Descendants().Attributes("Status").Any()
                || child.Descendants().Attributes("OldValue").Any())
              WriteFile(
                new XDocument(child),
                String.Format(
                  "AdvancedClasses\\{0}{1}.xml",
                  chkBuildCompare.Checked ? "Changed" : "",
                  child.Element("Name").Value.Replace(" ", "_")
                ),
                append,
                trimEmpty
              );
          }

          return;
      }
      String subPath = "";
      filename = filename.Replace('/', '.');

      if (filename.Contains('\\'))
        subPath = filename[..filename.LastIndexOf('\\')];

      if (!Directory.Exists(Config.ExtractPath + s_prefix + subPath))
        Directory.CreateDirectory(Config.ExtractPath + s_prefix + subPath);

      using StreamWriter file2 =
        new StreamWriter(Config.ExtractPath + s_prefix + filename, append);
      content.Save(file2, SaveOptions.None);
    }
    private void WriteFile(XDocument content, String filename, Boolean append) {
      if (!content.Root.IsEmpty) { // skip outputting empty XDocuments
        WriteFile(content, filename, append, false);
      }
    }
  }

  public static class DocumentExtensions {
    public static XDocument ToXDocument(this XmlDocument xmlDocument) {
      using XmlNodeReader nodeReader = new XmlNodeReader(xmlDocument);
      nodeReader.MoveToContent();
      return XDocument.Load(nodeReader);
    }
    public static XmlDocument ToXmlDocument(this XDocument xDocument) {
      XmlDocument xmlDocument = new XmlDocument();
      using (XmlReader xmlReader = xDocument.CreateReader()) {
        xmlDocument.Load(xmlReader);
      }
      return xmlDocument;
    }
  }
}
