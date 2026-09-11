using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
// using Newtonsoft.Json;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using GomLib;
using GomLib.Models;
using MongoDB.Bson;
using MongoDB.Driver;

namespace ConsoleTools {
  static class Program {
    public static String patch;
    public static String patchDir;
    public static String outputDir;
    public static Boolean processAll;
    public static DataObjectModel dom;
    public static MongoClient _client;
    public static IMongoDatabase _database;
    public static Dictionary<String, String> DBLookup = new Dictionary<String, String>
        {
            { "Abilities", "ability"},
            { "AbilityPackages", "abilitypackage"},
            { "Achievements", "achievement"},
            { "AdvancedClasses", "advclass"},
            { "Areas", "area"},
            { "Classes", "classspec"},
            { "CodexEntries", "codex"},
            { "Collections", "collection"},
            { "Companions", "companion"},
            { "Conquests", "conquest"},
            { "Conversations", "conversation"},
            { "Decorations", "decoration"},
            { "NewDisciplines", "newdiscipline"},
            { "Encounters", "encounter" },
            { "Ships", "gsf"},
            { "Items", "item"},
            //{ "ItemAppearances", "ipp" },
            { "Quests", "mission"},
            { "MtxStoreFronts", "mtx"},
            { "NewCompanions", "newcompanion"},
            { "Npcs", "npc"},
            //{ "NpcAppearances", "npp" },
            { "Placeables", "Object"},
            { "Schematics", "schematic"},
            { "SetBonuses", "setbonus"},
            { "Spawners", "spawner" },
            { "Strongholds", "stronghold"},
            { "Talents", "talent"}
        };
    // Configuration
    public static String patchLocation = "H:\\SWTOR\\patches\\"; // Point to patches folder
    public static String processedLocation = "H:\\SWTOR\\processed\\"; // Point to processed folder
    public static String patchVersion = "7.0.0"; // Point to patch version
    public static String diskCacheLocation = "D:\\SWTOR Datamining\\Live Game\\swtor\\DiskCacheArena"; // Point to DisckCacheArena location
                                                                                                       // public static String phpLocation = "C:\\Bitnami\\wampstack-7.4.11-0\\php\\php.exe"; // Point to php.exe location
                                                                                                       // public static String mongodumpLocation = "C:\\Program Files\\MongoDB\\Tools\\100\\bin\\mongodump.exe"; // Point to mongodump.exe 
    public static String phpLocation = "php.exe"; // Point to php.exe location
    public static String mongodumpLocation = "mongodump.exe"; // Point to mongodump.exe location
    public static String mongoIP = "127.0.0.1"; // Point to MongoDB Server IP Address
    public static Int32 mongoPort = 27017; // Point to MongoDB Server Port
    public static String mongoDB = "torc_db"; // Point to MongoDB Server Database
    public static Boolean changed_only = false; // Only output changes in JSON?

    static void OutputHandler(Object sendingProcess, DataReceivedEventArgs outLine) {
      //* Do your stuff with the output (write to console/log/StringBuilder)
      Console.WriteLine(outLine.Data);

    }

    public static void ProbeCache() {
      // Fix Ending slashes for consistency
      if (patchLocation.EndsWith("\\")) {
        patchLocation = patchLocation.TrimEnd('\\');
      }
      if (diskCacheLocation.EndsWith("\\")) {
        diskCacheLocation = diskCacheLocation.TrimEnd('\\');
      }
      TorArchive.Assets assets = new TorArchive.Assets(patchLocation + "\\" + patchVersion + "\\Assets\\");
      assets.Load(false);
      if (assets != null) {
        dom = new DataObjectModel(assets);
      }

      using FileStream fs = File.Open(diskCacheLocation, FileMode.Open);
      using GomBinaryReader br = new GomBinaryReader(fs, dom);
      //br.BaseStream.Position = 1007427584;
      //var bytes = br.ReadBytes(129032);Offset: 872,456,192, Length: 16,384
      dom.ReadAllItems(br, 872456192);

    }

    static void Main(String[] args) {
      //ProbeCache();
      if (args == null) {
        Console.WriteLine("Missing Arguments");
        return;
      } else {
        MongoDefaults.GuidRepresentation = MongoDB.Bson.GuidRepresentation.Standard;
        //_client = new MongoClient("mongodb://127.0.0.1:5151"); //mongotunnel
        MongoClientSettings settings = new MongoClientSettings
                {
          WaitQueueSize = Int32.MaxValue,
          WaitQueueTimeout = new TimeSpan(0, 2, 0),
          //settings.Server = new MongoServerAddress("127.0.0.1", 27017);
          Server = new MongoServerAddress(mongoIP, mongoPort)
        };
        _client = new MongoClient(settings); //local mongodb
        _database = _client.GetDatabase(mongoDB);

        Console.WriteLine(String.Join(", ", args));
        if (args.Length == 0) {
          args = new String[] { patchVersion, patchLocation, processedLocation, "false" };
        }

        // Fix Ending slashes for consistency
        if (args[0].EndsWith("\\")) {
          // patchVersion
          args[0] = args[0].TrimEnd('\\');
        }
        if (args[1].EndsWith("\\")) {
          // patchLocation
          args[1] = args[1].TrimEnd('\\');
        }
        if (args[2].EndsWith("\\")) {
          // processedLocation
          args[2] = args[2].TrimEnd('\\');
        }

        patch = args[0]; // patchVersion
        patchDir = args[1]; // patchLocation
        outputDir = String.Format("{0}\\{1}\\", args[2], patch); // processedLocation\\patchVersion
        _ = Boolean.TryParse(args[3], out processAll);

        if (processAll) {
          var patches = XDocument.Load(@"pts.xml"); //@"patches.xml");
          var nodes = patches.Element("patches").Elements("patch");
          //HashSet<String> filenames = new HashSet<String>();
          //String[] langs = { "en-us", "fr-fr", "de-de"};
          //String[] gameObjects = {
          //    "Achievement",
          //    "Ability",
          //    "Codex",
          //    "Item",
          //    "Companion" ,
          //    "Npc" ,
          //    "Mission",
          //    "Talent",
          //    "Schematic",
          //    "Collections" ,
          //    "MtxStoreFronts",
          //};
          //using (var compressStream = new MemoryStream())
          //{
          //    //create the zip in memory
          //    using (var zipArchive = new ZipArchive(compressStream, ZipArchiveMode.Create, true))
          //    {
          //        foreach (var node in nodes.Reverse())
          //        {
          //            patch = node.Attribute("version").Value;
          //            String filename = "";
          //            Console.WriteLine(patch);
          //            foreach (var type in gameObjects) {
          //                foreach (var lang in langs)
          //                {
          //                    filename = String.Format("{0}{1}\\tooltips\\{2}tips({3}).zip", args[2], patch, type, lang);
          //                    if (!File.Exists(filename))
          //                        continue;
          //                    using (ZipArchive oldArchive = System.IO.Compression.ZipFile.OpenRead(filename)) {
          //                        foreach(var entry in oldArchive.Entries)
          //                        {
          //                            if (filenames.Contains(entry.FullName)) continue;
          //                            filenames.Add(entry.FullName);
          //                            var newEntry = zipArchive.CreateEntry(entry.FullName, CompressionLevel.Optimal);
          //                            using (var str = newEntry.Open())
          //                            using (var ostr = entry.Open()) {
          //                                ostr.CopyTo(str);
          //                            }
          //                        }
          //                    }
          //                }
          //            }
          //            filename = String.Format("{0}{1}\\newIcons.zip", args[2], patch);
          //            if (!File.Exists(filename))
          //                continue;
          //            using (ZipArchive oldArchive = System.IO.Compression.ZipFile.OpenRead(filename))
          //            {
          //                foreach (var entry in oldArchive.Entries)
          //                {
          //                    if (filenames.Contains(entry.FullName)) continue;
          //                    filenames.Add(entry.FullName);
          //                    var newEntry = zipArchive.CreateEntry(entry.FullName, CompressionLevel.Optimal);
          //                    using (var str = newEntry.Open())
          //                    using (var ostr = entry.Open())
          //                    {
          //                        ostr.CopyTo(str);
          //                    }
          //                }
          //            }
          foreach (var node in nodes) {
            patch = node.Attribute("version").Value;
            if (node.Attribute("processed") != null) {
              Console.WriteLine(String.Format("Skipping {0}", patch));
              continue;
            }
            Console.Write(String.Format("Starting {0}", patch));
            outputDir = String.Format("{0}\\{1}\\", args[2], patch); // processedLocation\\patchVersion
            var start = DateTime.Now;

            //if (Int32.Parse(patch) < 175)
            //    continue;
            //FindFile(String.Format("\\resources\\world\\areas\\{0}\\area.dat", 4611686303442284043), String.Format("{0}{1}\\Assets\\", patchDir, patch), patch);

            //_client = new MongoClient("mongodb://127.0.0.1:27017"); //local mongodb
            //_database = _client.GetDatabase("torc_db");

            FileStream ostrm;
            StreamWriter writer;
            TextWriter oldOut = Console.Out;
            try {
              var filename = String.Format("{0}console_tools.log", outputDir); // processedLocation\\patchVersion\\console_tools.log
              if (!Directory.Exists(outputDir)) { Directory.CreateDirectory(outputDir); }
              ostrm = new FileStream(filename, FileMode.OpenOrCreate, FileAccess.Write);
              writer = new StreamWriter(ostrm);
            }
            catch (Exception e) {
              Console.WriteLine("Cannot open Redirect.txt for writing");
              Console.WriteLine(e.Message);
              return;
            }
            Console.SetOut(writer);
            Console.WriteLine(start.ToString());

            ProcessAssets(String.Format("{0}\\{1}\\Assets\\", patchDir, patch)); // patchLocation\\patchVersion\\Assets\\

            // Old Mongo Import (now handled by bulk_import.php server-side TORCommunity scripts
            //using (Process deltaProcess = new Process())
            //{
            //    //Maybe this shouldn't be hard coded? Could do this in memory instead to.
            //    deltaProcess.StartInfo.FileName = phpLocation;

            //    deltaProcess.StartInfo.CreateNoWindow = false;
            //    deltaProcess.StartInfo.UseShellExecute = false;
            //    deltaProcess.StartInfo.RedirectStandardOutput = true;
            //    deltaProcess.StartInfo.RedirectStandardError = true;
            //    deltaProcess.StartInfo.WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory;
            //    deltaProcess.StartInfo.Arguments = String.Format("{0} {1}", "u_import.php", outputDir);
            //    deltaProcess.Start();
            //    String output = deltaProcess.StandardOutput.ReadToEnd();
            //    Console.WriteLine(output);
            //    deltaProcess.WaitForExit();
            //}
            //Console.SetOut(oldOut);
            // Old Mongo Backuo
            //using (Process dumpProcess = new Process())
            //{
            //    //Maybe this shouldn't be hard coded? Could do this in memory instead to.
            //    dumpProcess.StartInfo.FileName = mongodumpLocation;

            //    dumpProcess.StartInfo.CreateNoWindow = false;
            //    dumpProcess.StartInfo.UseShellExecute = false;
            //    dumpProcess.StartInfo.RedirectStandardOutput = true;
            //    dumpProcess.StartInfo.RedirectStandardError = true;
            //    dumpProcess.StartInfo.WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory;
            //    dumpProcess.StartInfo.Arguments = String.Format("--gzip --out {0}backup\\", outputDir);
            //    dumpProcess.OutputDataReceived += new DataReceivedEventHandler(OutputHandler);
            //    dumpProcess.ErrorDataReceived += new DataReceivedEventHandler(OutputHandler);
            //    dumpProcess.Start();
            //    dumpProcess.BeginOutputReadLine();
            //    dumpProcess.BeginErrorReadLine();
            //    dumpProcess.WaitForExit();
            //}
            //Console.SetOut(writer);
            var end = DateTime.Now;
            var span = end - start;
            Console.WriteLine(String.Format("Processed in {0} seconds", Math.Round(span.TotalSeconds, 2)));
            Console.WriteLine(end.ToString());
            node.Add(new XAttribute("processed", true));
            using (StreamWriter file2 = new StreamWriter(@"patches.xml")) {
              patches.Save(file2, SaveOptions.None);
            }
            Console.SetOut(oldOut);
            Console.WriteLine(" - Finished");
            writer.Close();
            ostrm.Close();
          }
          //    }
          //    outputDir = args[2];
          //    compressStream.Position = 0;
          //    WriteFile(compressStream, "fulltips.zip");
          //}
        } else {
          ProcessAssets(String.Format("{0}\\{1}\\Assets\\", patchDir, patch)); // patchLocation\\patchVersion\\Assets
        }
      }
      Console.WriteLine("Finished.");
      //Console.ReadLine();
    }

    // private static Boolean FindFile(String fileName, String assetsLocation, String patch)
    // {
    //     Boolean found = false;
    //     dom = null;
    //     GC.Collect();
    //     GC.WaitForPendingFinalizers();
    //     TorArchive.Assets assets = new TorArchive.Assets(assetsLocation);
    //     assets.Load(false);
    //     if (assets != null)
    //     {
    //         dom = new DataObjectModel(assets);
    //         var datFile = dom._assets.FindFile(fileName);
    //         if (datFile != null)
    //             Console.WriteLine(patch + " - " + datFile.FileInfo.Checksum);
    //         dom.Unload();
    //         dom._assets.Dispose();
    //         dom.Dispose();
    //         RequiredFiles = new HashSet<String>();
    //         GC.Collect();
    //         GC.WaitForPendingFinalizers();
    //     }
    //     return found;
    // }

    private static void ProcessAssets(String assetsLocation) {
      dom = null;
      GC.Collect();
      GC.WaitForPendingFinalizers();
      TorArchive.Assets assets = new TorArchive.Assets(assetsLocation);
      assets.Load(false);
      if (assets != null) {
        dom = new DataObjectModel(assets) {
          Version = patch
        };

        //time = time.AddSeconds(13126665600000 / 1000).ToLocalTime();
        //Console.WriteLine(time);

        //DateTime time = new DateTime(1601, 1, 1, 0, 0, 0, 0, System.DateTimeKind.Utc);
        //DateTime local = DateTime.Now;
        //var span = local - time.ToLocalTime();
        //Console.WriteLine(span.TotalMilliseconds);

        //var cal = System.Xml.Linq.XDocument.Load("J:\\swtor_db\\processed\\5.0P7\\GOM\\Removed\\qstActivityHighlightCalendarPrototype.xml");
        //var nodes = cal.Element("None").Element("IList").Elements("Node");
        //using (var file = new System.IO.StreamWriter("c:\\swtor\\2\\calendar.txt"))
        //{
        //    foreach (var node in nodes)
        //    {
        //        DateTime time = new DateTime(1601, 1, 1, 0, 0, 0, 0, System.DateTimeKind.Utc);
        //        time = time.AddSeconds(UInt64.Parse(node.Attribute("Id").Value) / 1000).ToLocalTime();
        //        file.WriteLine(String.Format("{0}|{1}", time, node.Value));
        //    }
        //}

        //Console.ReadLine();
        Console.WriteLine("Loading Assets");
        dom.Load();
        Smart.Link(dom, Console.WriteLine);
        // Advanced Classes
        GetObjects("class.pc.advanced", "AdvancedClasses");
        // Abilities
        GetObjects("abl.", "Abilities");
        if (dom.GetObjectsStartingWith("pkg.abilities").Count > 0) {
          GetObjects("pkg.abilities", "AbilityPackages");
        } else {
          GetObjects("apn.", "AbilityPackages");
        }
        // Achievements
        GetObjects("ach.", "Achievements");
        // Conquests
        GetPrototypeObjects("Conquests", "wevConquestInfosPrototype", "wevConquestTable");
        // Areas
        GetPrototypeObjects("Areas", "mapAreasDataProto", "mapAreasDataObjectList"); // getAreas();
                                                                                     // Map Notes
        GetObjects("mpn.", "MapNotes");
        // Codex Entries
        GetObjects("cdx.", "CodexEntries");
        // Codex Category Totals
        GetPrototypeObjects("CodexCategoryTotals", "cdxCategoryTotalsPrototype", "cdxFactionToClassToPlanetToTotalLookupList");
        // Classes
        GetObjects("class.", "Classes");
        // Collections
        GetPrototypeObjects("Collections", "colCollectionItemsPrototype", "colCollectionItemsData");
        // Conversations
        GetObjects("cnv.", "Conversations");
        // Decorations
        GetObjects("dec.", "Decorations");
        // Talents
        GetObjects("dis.", "Disciplines");
        // Effects
        GetObjects("eff.", "Effects");
        // Encounters
        //getObjects("enc.", "Encounters");
        // Items
        GetObjects("itm.", "Items");
        // Item Appearances
        //getObjects("ipp.", "ItemAppearances");
        // NPCs
        GetObjects("npc.", "Npcs");
        // NPC Appearances
        //getObjects("npp.", "NpcAppearances");
        // Quests
        GetObjects("qst.", "Quests");
        // Schematics
        GetObjects("schem.", "Schematics");
        // Set Bonuses
        GetPrototypeObjects("SetBonuses", "itmSetBonusesPrototype", "itmSetBonuses");
        // Set Tables
        GetPrototypeObjects("SetTable", "itmSetTablePrototype", "itmSetTable");
        // Spawners
        //getObjects("spn.", "Spawners");
        // Strongholds
        GetObjects("apt.", "Strongholds");
        // Talents
        GetObjects("tal.", "Talents");
        // New Companions
        GetObjects("nco.", "NewCompanions");
        // Companions
        GetPrototypeObjects("Companions", "chrCompanionInfo_Prototype", "chrCompanionInfoData");
        // MTX Store Fronts
        GetPrototypeObjects("MtxStoreFronts", "mtxStorefrontInfoPrototype", "mtxStorefrontData");
        // Ships
        GetPrototypeObjects("Ships", "scFFShipsDataPrototype", "scFFShipsData");
        // Placeables
        GetObjects("plc.", "Placeables");

        //Reload hash dict.
        //TorArchive.HashDictionaryInstance.Instance.Unload();
        //TorArchive.HashDictionaryInstance.Instance.Load();
        //TorArchive.HashDictionaryInstance.Instance.dictionary.CreateHelpers();
        GetReqFiles();
        GetIcons();
        TorArchive.HashDictionaryInstance.Instance.Dictionary.SaveBinaryHashList();
        TorArchive.HashDictionaryInstance.Instance.Unload();
        dom.Unload();
        dom.Assets.Dispose();
        dom.Dispose();
        RequiredFiles = new HashSet<String>();
        GC.Collect();
        GC.WaitForPendingFinalizers();
      } else {
        Console.WriteLine("Assets not found.");
      }
    }

    #region File Functions
    public static String PrepExtractPath(String filename) {
      String subPath = "";
      if (filename.Contains('\\')) {
        subPath = filename.Substring(0, filename.LastIndexOf('\\'));
      }
      if (!Directory.Exists(outputDir + subPath)) {
        Directory.CreateDirectory(outputDir + subPath);
      }
      return outputDir + filename;
    }
    public static void WriteFile(String content, String filename, Boolean append) {
      String subPath = "";
      if (filename.Contains('\\')) {
        subPath = filename.Substring(0, filename.LastIndexOf('\\'));
      }
      if (!Directory.Exists(outputDir + subPath)) {
        Directory.CreateDirectory(outputDir + subPath);
      }
      using StreamWriter file2 = new StreamWriter(outputDir + filename, append);
      file2.Write(content);
    }
    public static void WriteFile(MemoryStream content, String filename) {
      String subPath = "";
      filename = filename.Replace('/', '\\');
      if (filename.Contains('\\')) {
        subPath = filename.Substring(0, filename.LastIndexOf('\\'));
      }
      if (!Directory.Exists(outputDir + subPath)) {
        Directory.CreateDirectory(outputDir + subPath);
      }
      using FileStream file2 = new FileStream(outputDir + filename, FileMode.OpenOrCreate);
      content.Position = 0;
      content.CopyTo(file2);
    }
    public static void CreateGzip(String filename) {
      String filepath = String.Join("", outputDir, filename);
      if (!File.Exists(filepath)) {
        return;
      }
      using (FileStream readstream = new FileStream(filepath, FileMode.Open, FileAccess.Read)) {
        if (readstream == null) {
          return;
        }
        if (readstream.Length == 0) {
          return;
        }
        using FileStream outFileStream = new FileStream(String.Join("", filepath, ".gz"), FileMode.Create, FileAccess.Write);
        using GZipStream gzip = new GZipStream(outFileStream, CompressionMode.Compress);
        readstream.CopyTo(gzip);
      }
      File.Delete(filepath);
    }
    public static void DeleteEmptyFile(String filename, Int32 count) //deletes empty files that were created for streaming output
    {
      String filepath = String.Join("", outputDir, filename);
      FileInfo fInfo = new FileInfo(filepath);
      if (fInfo != null)
        if (fInfo.Length == 0 || count == 0)
          File.Delete(filepath);
    }
    #endregion
    public static HashSet<String> RequiredFiles = new HashSet<String>();
    #region Processing Functions
    public static void GetObjects(String prefix, String elementName) {
      Console.Write(String.Format("Getting {0}", elementName));
      switch (elementName) {
        case "Abilities": //This section is for exploring Ability Effects
          FullyProcessGameObjects(prefix, elementName);
          //dom.abilityLoader.effKeys.Sort();
          //String effKeyList = String.Join(Environment.NewLine, dom.abilityLoader.effKeys);
          //WriteFile(effKeyList, "effKeys.txt", false);

          //dom.abilityLoader.effWithUnknowns = dom.abilityLoader.effWithUnknowns.Distinct().OrderBy(o => o).ToList();
          //String effUnknowns = String.Join(Environment.NewLine, dom.abilityLoader.effWithUnknowns);
          //WriteFile(effUnknowns, "effUnknowns.txt", false);

          //dom.abilityLoader.effWithUnknowns = new List<String>();
          //dom.abilityLoader.effKeys = new List<String>();
          break;
        default:
          FullyProcessGameObjects(prefix, elementName);
          break;
      }
    }
    public static void GetPrototypeObjects(String xmlRoot, String prototype, String dataTable) {
      Console.Write(String.Format("Getting {0} ", xmlRoot));
      FullyProcessProcessProtoData(xmlRoot, prototype, dataTable);
    }
    public static void GetReqFiles() {
      Console.Write(String.Format("Getting {0} ", "Icons"));
      //return;
      String file = "reqFiles.zip";
      if (RequiredFiles.Count == 0) {
        return;
      }
      WriteFile("", file, false);
      ProgressBar progress = null;
      if (!processAll)
        progress = new ProgressBar();
      Int32 i = 0;
      Int32 count = RequiredFiles.Count;
      using var compressStream = new MemoryStream();
      //create the zip in memory
      using (var zipArchive = new ZipArchive(compressStream, ZipArchiveMode.Create, true)) {
        foreach (var t in RequiredFiles) {
          i++;
          if (!processAll)
            progress.Report((Double)i / count);
          TorArchive.File f = dom.Assets.FindFile(t);
          if (f == null) continue;
          switch (t[^3..]) {
            case "dds":
              IconParamter[] parms = Array.Empty<IconParamter>();
              using (MemoryStream iconStream = GetIcon(t, f, false, 90L, parms)) {
                if (iconStream != null) {
                  ZipArchiveEntry iconEntry = zipArchive.CreateEntry(String.Format("{0}.jpg", t[1..^4]), CompressionLevel.Fastest);
                  using var a = iconEntry.Open();
                  iconStream.WriteTo(a);
                }
              }

              using (MemoryStream iconStream = GetIcon(t, f, true, 95L, parms)) {
                if (iconStream != null) {
                  ZipArchiveEntry iconEntry = zipArchive.CreateEntry(String.Format("{0}_thumb.jpg", t[1..^4]), CompressionLevel.Fastest);
                  using var a = iconEntry.Open();
                  iconStream.WriteTo(a);
                }
              }
              break;
            default:
              using (MemoryStream memStream = (MemoryStream)f.OpenCopyInMemory()) {
                ZipArchiveEntry fileEntry = zipArchive.CreateEntry(t, CompressionLevel.Fastest);
                using var a = fileEntry.Open();
                memStream.WriteTo(a);
              }
              break;
          }

        }
      }
      compressStream.Position = 0;
      WriteFile(compressStream, file);
    }
    public static void GetIcons() {
      Console.Write(String.Format("Getting {0} ", "Icons"));
      TorArchive.HashDictionaryInstance.Instance.Unload();
      TorArchive.HashDictionaryInstance.Instance.Load();
      TorArchive.HashDictionaryInstance.Instance.Dictionary.CreateHelpers();
      //try
      //{
      var libs = dom.Assets.Libraries.Where(x => x.Name.Contains("gfx_assets"));
      if (libs == null || !libs.Any()) {
        libs = dom.Assets.Libraries.Where(x => x.Name.Contains("main_gfx"));
        if (libs == null || !libs.Any())
          return;
      }
      var archive = libs.First().Archives.First();
      List<TorArchive.HashFileInfo> iconsToOutput = new List<TorArchive.HashFileInfo>();
      foreach (var file in archive.Value.EnumerateFiles()) {
        TorArchive.HashFileInfo hashInfo = new TorArchive.HashFileInfo(file.FileInfo.PrimaryHash, file.FileInfo.SecondaryHash, file);
        if (hashInfo.FileState != TorArchive.HashFileInfo.State.Unchanged) {
          using BinaryReader bin = new BinaryReader(new MemoryStream(file.PeakBytes(20)));
          Int32 magic = bin.ReadInt32();
          if (magic != 0x20534444)
            continue;
          bin.ReadInt32(); //header size
          bin.ReadInt32(); //flags
          Int32 height = bin.ReadInt32();
          Int32 width = bin.ReadInt32();
          bin.Close();
          if (height == 52 && width == 52) {
            iconsToOutput.Add(hashInfo);
          } else {
            continue;
          }
        }
      }
      CreatCompressedIconOutput(iconsToOutput);
      return;
      //}
      //catch (Exception ex)
      //{
      //    Console.WriteLine(ex.InnerException);
      //}
    }

    public static void FullyProcessGameObjects(String gomPrefix, String xmlRoot) {
      Boolean classOverride = xmlRoot == "AdvancedClasses";
      IEnumerable<GomObject> curItmList = GetMatchingGomObjects(dom, gomPrefix);

      Int16 e = 0;
      Int32 ie = 0;
      String n = Environment.NewLine;
      var txtFile = new StringBuilder();
      String filename = String.Format("\\json\\Full{0}.json", xmlRoot);
      WriteFile(String.Format("{0}{1}", patch, n), filename, false);
      HashTableHashing.MurmurHash2Unsafe jsonHasher = new HashTableHashing.MurmurHash2Unsafe();

      Int32 i = 0;
      Int32 count = 0;
      Boolean found_items = false;
      count = curItmList.Count();
      List<Tooltip> iList = new List<Tooltip>();
      Boolean frLoaded = dom.Assets.LoadedFileGroups.Contains("fr-fr");
      Boolean deLoaded = dom.Assets.LoadedFileGroups.Contains("de-de");
      ProgressBar progress = null;
      if (!processAll)
        progress = new ProgressBar();
      DBLookup.TryGetValue(xmlRoot, out String db_table);
      Dictionary<UInt64, UInt32> current_hashes = new Dictionary<UInt64, UInt32>();
      IMongoCollection<BsonDocument> collection = null;
      if (db_table != null) {
        var filter = new BsonDocument("name", db_table);
        //filter by collection name
        var collections = _database.ListCollections(new ListCollectionsOptions { Filter = filter });
        //check for existence
        if (collections.ToList().Any()) {
          collection = _database.GetCollection<BsonDocument>(db_table);

          var cursor = collection.Find(Builders<BsonDocument>.Filter.Empty);
          var fields = Builders<BsonDocument>.Projection.Include("Id").Include("hash");
          var result = cursor.Project(fields).ToList();
          current_hashes = result.ToDictionary(x => UInt64.Parse(x["Id"].AsString), x => UInt32.Parse(x["hash"].AsString));
        }
      }
      foreach (var curObject in curItmList) {
        if (!processAll)
          progress.Report((Double)i / count);

        if (e >= 1000) {
          found_items = true;
          WriteFile(txtFile.ToString(), filename, true);
          txtFile.Clear();
          e = 0;
        }

        var obj = GameObject.Load(curObject, classOverride);
        curObject.Unload();
        if (obj != null && obj.Id != 0) //apparently if the loader passes null back, sometimes data comes, too.....
        {
          if (collection != null) {
            var filter = Builders<BsonDocument>.Filter.Eq("Base62Id", obj.Base62Id);
            var update = Builders<BsonDocument>.Update
                            .Set("last_seen", patch);
            var result = collection.UpdateOneAsync(filter, update);
          }
          String jsonString = obj.ToJSON();
          UInt32 hash = jsonHasher.Hash(Encoding.ASCII.GetBytes(jsonString));
          if (current_hashes.TryGetValue(obj.Id, out UInt32 oldhash)) {
            if (hash != oldhash || changed_only == false) {
              // The MongoDB c# Driver is so fucking awful that we're going to output the json for php insertion.
              // Because fucking trying to deserialized uint64s using a deserializer that doesn't support them, and won't just make them strings
              txtFile.Append(String.Format("{0},{1},{2}{3}", obj.Base62Id, hash, jsonString, Environment.NewLine)); //Append it with a newline to the output.
              e++; ie++;
              Tooltip t = new Tooltip(obj);
              if (t.IsImplemented())
                iList.Add(t);
            } else {
              obj = null;
            }
          } else // new
            {
            txtFile.Append(String.Format("{0},{1},{2}{3}", obj.Base62Id, hash, jsonString, Environment.NewLine)); //Append it with a newline to the output.
            Tooltip t = new Tooltip(obj);
            e++; ie++;
            if (t.IsImplemented())
              iList.Add(t);
          }
        }
        i++;
      }
      WriteFile(txtFile.ToString(), filename, true);
      txtFile.Clear();
      if (ie != 0) {
        Console.WriteLine(String.Format(" ({0})", ie));
        found_items = true;
      }
      if (!processAll)
        progress.Dispose();

      if (!found_items)
        DeleteEmptyFile(filename, 0);
      curItmList = null;
      GC.Collect();
      CreateGzip(filename);
      Dictionary<String, String> gameObjects = new Dictionary<String, String>
            {

                {"ach.", "Achievement"},
                {"abl.", "Ability"},
                {"cdx.", "Codex"},
                {"itm.", "Item"},
                {"nco.", "Companion" },
                {"npc.", "Npc" },
                {"qst.", "Mission"},
                {"tal.", "Talent"},
                {"sche", "Schematic"},
            };
      //return;
      if (iList.Count > 0) {
        if (!processAll)
          progress = new ProgressBar();

        gameObjects.TryGetValue(iList.First().Fqn.Substring(0, 4), out String gameObj);
        if (!String.IsNullOrWhiteSpace(gameObj)) {
          Console.Write(String.Format(" - Generating Tooltips. ({0})", ie));
          if (!processAll)
            progress.Report(0);
          CreateCompressedTooltipOutput(gameObj, iList, "en-us");
          if (!processAll)
            progress.Report(.3333);
          if (frLoaded)
            CreateCompressedTooltipOutput(gameObj, iList, "fr-fr");
          if (!processAll)
            progress.Report(.6666);
          if (deLoaded)
            CreateCompressedTooltipOutput(gameObj, iList, "de-de");
          Console.Write(" - Done.");
        } else
          Console.Write(String.Format(" - Done. ({0})", ie));
        if (!processAll)
          progress.Dispose();

      }

      Console.WriteLine();
    }
    public static void FullyProcessProcessProtoData(String xmlRoot, String prototype, String dataTable) {
      Dictionary<Object, Object> currentDataProto = new Dictionary<Object, Object>();
      GomObject currentDataObject = dom.GetObject(prototype);
      if (currentDataObject != null) //fix to ensure old game assets don't throw exceptions.
      {
        if (String.Equals(dataTable, "mtxStorefrontData", StringComparison.OrdinalIgnoreCase)) {
          currentDataProto = currentDataObject.Data.ValueOrDefault<Dictionary<Object, Object>>(
            "mtxStorefrontItems", null)
            ?? currentDataObject.Data.ValueOrDefault<Dictionary<Object, Object>>(
              "mtxStorefrontData", new Dictionary<Object, Object>());
        } else if (String.Equals(dataTable, "colCollectionItemsData", StringComparison.OrdinalIgnoreCase)
                   || String.Equals(dataTable, "colMtxItemIdToCollectionItem", StringComparison.OrdinalIgnoreCase)) {
          currentDataProto = currentDataObject.Data.ValueOrDefault<Dictionary<Object, Object>>(
            "colMtxItemIdToCollectionItem", null)
            ?? currentDataObject.Data.ValueOrDefault<Dictionary<Object, Object>>(
              "4611686297655094008", null)
            ?? currentDataObject.Data.ValueOrDefault<Dictionary<Object, Object>>(
              "colCollectionItemsData", new Dictionary<Object, Object>());
        } else {
          currentDataProto = currentDataObject.Data.ValueOrDefault<Dictionary<Object, Object>>(
            dataTable, new Dictionary<Object, Object>());
        }
        currentDataObject.Unload();
      }

      var curIds = currentDataProto.Keys;
      var curItems = new List<PseudoGameObject>();

      Int16 e = 0;
      Int32 ie = 0;
      Int32 ies = 0;
      String n = Environment.NewLine;
      var txtFile = new StringBuilder();
      String filename = String.Format("\\json\\Full{0}.json", xmlRoot);
      WriteFile(String.Format("{0}{1}", patch, n), filename, false);
      Boolean has_second_output = false;
      StringBuilder secondFile = null;
      String secondFilename = null;
      String second_db_table = "";
      switch (xmlRoot) {
        case "Areas":
          has_second_output = true;
          secondFile = new StringBuilder();
          secondFilename = "\\json\\FullRooms.json";
          WriteFile(String.Format("{0}{1}", patch, n), secondFilename, false);
          second_db_table = "room";
          break;
      }
      HashTableHashing.MurmurHash2Unsafe jsonHasher = new HashTableHashing.MurmurHash2Unsafe();

      Int32 i = 0;
      Int32 count = 0;
      Boolean found_items = false;
      count = curIds.Count;
      List<Tooltip> iList = new List<Tooltip>();
      Boolean frLoaded = dom.Assets.LoadedFileGroups.Contains("fr-fr");
      Boolean deLoaded = dom.Assets.LoadedFileGroups.Contains("de-de");
      ProgressBar progress = null;
      if (!processAll)
        progress = new ProgressBar();
      DBLookup.TryGetValue(xmlRoot, out String db_table);
      Dictionary<Int64, UInt32> current_hashes = new Dictionary<Int64, UInt32>();
      IMongoCollection<BsonDocument> collection = null;
      IMongoCollection<BsonDocument> second_collection = null;
      Dictionary<Int64, UInt32> second_hashes = new Dictionary<Int64, UInt32>();
      if (db_table != null) {
        var filter = new BsonDocument("name", db_table);
        //filter by collection name
        var collections = _database.ListCollections(new ListCollectionsOptions { Filter = filter });
        //check for existence
        if ((collections.ToList()).Any()) {
          collection = _database.GetCollection<BsonDocument>(db_table);

          var cursor = collection.Find(Builders<BsonDocument>.Filter.Empty);
          var fields = Builders<BsonDocument>.Projection.Include("Id").Include("hash");
          var result = cursor.Project(fields).ToList();
          current_hashes = result.ToDictionary(
              x => Int64.Parse(x["Id"].AsString),
              x => UInt32.Parse(
                  x.Contains("hash") ? x["hash"].AsString : "0"));
        }
      }
      if (second_db_table != null) {
        var filter = new BsonDocument("name", second_db_table);
        //filter by collection name
        var collections = _database.ListCollections(new ListCollectionsOptions { Filter = filter });
        //check for existence
        if (collections.ToList().Any()) {
          second_collection = _database.GetCollection<BsonDocument>(second_db_table);

          var cursor = collection.Find(Builders<BsonDocument>.Filter.Empty);
          var fields = Builders<BsonDocument>.Projection.Include("Id").Include("hash");
          var result = cursor.Project(fields).ToList();
          second_hashes = result.ToDictionary(
              x => Int64.Parse(x["Id"].AsString),
              x => UInt32.Parse(
                  x.Contains("hash") ? x["hash"].AsString : "0"));
        }
      }
      foreach (var id in curIds) {
        if (!processAll)
          progress.Report((Double)i / count);

        if (e % 1000 == 1) {
          found_items = true;
          WriteFile(txtFile.ToString(), filename, true);
          txtFile.Clear();
          e = 0;
        }

        currentDataProto.TryGetValue(id, out Object curData);
        PseudoGameObject curObj = PseudoGameObject.Load(xmlRoot, dom, id, curData);
        if (collection != null) {
          var filter = Builders<BsonDocument>.Filter.Eq("Base62Id", curObj.Base62Id);
          var update = Builders<BsonDocument>.Update
                        .Set("last_seen", patch);
          var result = collection.UpdateOneAsync(filter, update);
        }
        String jsonString = curObj.ToJSON();
        UInt32 hash = jsonHasher.Hash(Encoding.ASCII.GetBytes(jsonString));
        if (current_hashes.TryGetValue(curObj.Id, out UInt32 oldhash)) {
          if (hash != oldhash || changed_only == false) {
            // The MongoDB c# Driver is so fucking awful that we're going to output the json for php insertion.
            // Because fucking trying to deserialized uint64s using a deserializer that doesn't support them, and won't just make them strings
            txtFile.Append(String.Format("{0},{1},{2}{3}", curObj.Base62Id, hash, jsonString, Environment.NewLine)); //Append it with a newline to the output.
            e++; ie++;
            Tooltip t = new Tooltip(curObj);
            if (t.IsImplemented())
              iList.Add(t);
            if (curObj.RequiredFiles != null)
              RequiredFiles.UnionWith(curObj.RequiredFiles);
          }
          //else //do nothing
        } else // new
          {
          txtFile.Append(String.Format("{0},{1},{2}{3}", curObj.Base62Id, hash, jsonString, Environment.NewLine)); //Append it with a newline to the output.
          Tooltip t = new Tooltip(curObj);
          e++; ie++;
          if (t.IsImplemented())
            iList.Add(t);
          if (curObj.RequiredFiles != null)
            RequiredFiles.UnionWith(curObj.RequiredFiles);
        }
        if (has_second_output && curObj != null) {
          IEnumerable<PseudoGameObject> second_objects = new List<PseudoGameObject>();
          switch (xmlRoot) {
            case "Areas":
              if (((Area)curObj).AreaDat != null)
                second_objects = ((Area)curObj).AreaDat.Rooms.Values.Cast<PseudoGameObject>();
              break;
          }
          foreach (var obj in second_objects) {
            if (second_collection != null) {
              var filter = Builders<BsonDocument>.Filter.Eq("Base62Id", obj.Base62Id);
              var update = Builders<BsonDocument>.Update
                                .Set("last_seen", patch);
              var result = second_collection.UpdateOneAsync(filter, update);
            }
            jsonString = obj.ToJSON();
            hash = jsonHasher.Hash(Encoding.ASCII.GetBytes(jsonString));
            if (current_hashes.TryGetValue(obj.Id, out UInt32 second_oldhash)) {
              if (hash != second_oldhash || changed_only == false) {
                // The MongoDB c# Driver is so fucking awful that we're going to output the json for php insertion.
                // Because fucking trying to deserialized uint64s using a deserializer that doesn't support them, and won't just make them strings
                secondFile.Append(String.Format("{0},{1},{2}{3}", obj.Base62Id, hash, jsonString, Environment.NewLine)); //Append it with a newline to the output.
                ies++;
                Tooltip t = new Tooltip(obj);
                if (t.IsImplemented())
                  iList.Add(t);
                if (obj.RequiredFiles != null)
                  RequiredFiles.UnionWith(obj.RequiredFiles);
              }
              //else //do nothing
            } else // new
              {
              secondFile.Append(String.Format("{0},{1},{2}{3}", obj.Base62Id, hash, jsonString, Environment.NewLine)); //Append it with a newline to the output.
              Tooltip t = new Tooltip(obj);
              ies++;
              if (t.IsImplemented())
                iList.Add(t);
              if (obj.RequiredFiles != null)
                RequiredFiles.UnionWith(obj.RequiredFiles);
            }
          }
        }
        i++;
      }
      WriteFile(txtFile.ToString(), filename, true);
      txtFile.Clear();
      if (ie != 0) {
        Console.WriteLine(String.Format(" ({0})", ie));
        found_items = true;
      }
      if (has_second_output) {
        WriteFile(secondFile.ToString(), secondFilename, true);
        secondFile.Clear();
        if (ies != 0) {
          Console.WriteLine(String.Format(" ({0})", ies));
          CreateGzip(secondFilename);
        } else
          DeleteEmptyFile(secondFilename, 0);
      }
      if (!processAll)
        progress.Dispose();

      if (!found_items)
        DeleteEmptyFile(filename, 0);
      if (currentDataObject != null)
        currentDataObject.Unload();
      curIds = null;
      GC.Collect();
      CreateGzip(filename);
      Dictionary<String, String> protoGameObjects = new Dictionary<String, String>
            {
                { "GomLib.Models.Collection", "Collections" },
                { "GomLib.Models.MtxStorefrontEntry", "MtxStoreFronts" },
                { "GomLib.Models.Area", "Areas" },
            };
      if (iList.Count > 0) {
        if (!processAll)
          progress = new ProgressBar();

        protoGameObjects.TryGetValue(iList.First().PObj.GetType().ToString(), out String gameObj);
        if (!String.IsNullOrWhiteSpace(gameObj)) {
          Console.Write(String.Format(" - Generating Tooltips. ({0})", ie));
          if (!processAll)
            progress.Report(0);
          CreateCompressedTooltipOutput(gameObj, iList, "en-us");
          if (!processAll)
            progress.Report(.3333);
          if (frLoaded)
            CreateCompressedTooltipOutput(gameObj, iList, "fr-fr");
          if (!processAll)
            progress.Report(.6666);
          if (deLoaded)
            CreateCompressedTooltipOutput(gameObj, iList, "de-de");
          Console.Write(" - Done.");

          if (!processAll)
            progress = new ProgressBar();
        } else
          Console.Write(String.Format(" - Done. ({0})", ie));

      }

      Console.WriteLine();
    }

    //public static void ProcessGameObjects(String gomPrefix, String xmlRoot)
    //{
    //    Boolean classOverride = (xmlRoot == "AdvancedClasses");
    //    IEnumerable<GomObject> curItmList = getMatchingGomObjects(dom, gomPrefix);

    //    var curItmNames = curItmList.Select(x => x.Name).ToList();

    //    Dictionary<String, List<GomLib.Models.GameObject>> ObjectLists = new Dictionary<String, List<GomLib.Models.GameObject>>();
    //    var newItems = new List<GomLib.Models.GameObject>();

    //    Int32 i = 0;
    //    Int32 count = 0;

    //    i = 0;
    //    count = curItmList.Count();
    //    using (var progress = new ProgressBar())
    //    {

    //        foreach (var curObject in curItmList)
    //        {
    //            progress.Report((Double)i / count);
    //            var obj = GameObject.Load(curObject, classOverride);
    //            if (obj != null && obj.Id != 0) //apparently if the loader passes null back, sometimes data comes, too.....
    //                newItems.Add(obj);
    //            i++;
    //        }
    //    }
    //    ObjectLists.Add("Full", newItems);

    //    Console.Write(" - Generating Output ");
    //    count = 0;
    //    i = 0;
    //    foreach (var itmList in ObjectLists)
    //    {
    //        count += itmList.Value.Count;
    //    }

    //    foreach (var itmList in ObjectLists)
    //    {
    //        GameObjectListAsJSON(String.Join("", itmList.Key, xmlRoot), itmList.Value);
    //        //GetTips(xmlRoot, itmList.Value);
    //    }
    //    Console.WriteLine();
    //}

    //public static void ProcessProtoData(String xmlRoot, String prototype, String dataTable)
    //{
    //    Int32 i = 0;
    //    Int32 count = 0;

    //    Dictionary<Object, Object> currentDataProto = new Dictionary<Object, Object>();
    //    GomObject currentDataObject = dom.GetObject(prototype);
    //    if (currentDataObject != null) //fix to ensure old game assets don't throw exceptions.
    //    {
    //        currentDataProto = currentDataObject.Data.Get<Dictionary<Object, Object>>(dataTable);
    //        currentDataObject.Unload();
    //    }

    //    var curIds = currentDataProto.Keys;
    //    Dictionary<String, List<GomLib.Models.PseudoGameObject>> ObjectLists = new Dictionary<String, List<GomLib.Models.PseudoGameObject>>();
    //    var newItems = new List<GomLib.Models.PseudoGameObject>();

    //    i = 0;
    //    count = curIds.Count();
    //    using (var progress = new ProgressBar())
    //    {
    //        foreach (var id in curIds)
    //        {
    //            progress.Report((Double)i / count);
    //            Object curData;
    //            currentDataProto.TryGetValue(id, out curData);
    //            GomLib.Models.PseudoGameObject curObj = PseudoGameObject.Load(xmlRoot, dom, id, curData);
    //            newItems.Add(curObj);
    //            i++;
    //        }
    //    }
    //    ObjectLists.Add("Full", newItems);

    //    Console.Write(" - Generating Output ");
    //    count = 0;
    //    i = 0;
    //    foreach (var itmList in ObjectLists)
    //    {
    //        count += itmList.Value.Count;
    //    }


    //    foreach (var itmList in ObjectLists)
    //    {
    //        PseudoGameObjectListAsJSON(String.Join("", itmList.Key, xmlRoot), itmList.Value);
    //    }
    //    Console.WriteLine();
    //}

    private static IEnumerable<GomObject> GetMatchingGomObjects(DataObjectModel dom, String gomPrefix) { // This function is meant to handle unique cases of loading a list of objects from the GOM
      IEnumerable<GomObject> itmList;
      switch (gomPrefix) {
        case "abl.":
          itmList = dom.GetObjectsStartingWith(gomPrefix).Where(x => !x.Name.Contains("/")); // Abilities with a / in the name are Effects.
          break;
        case "apn.":
          itmList = dom.GetObjectsStartingWith(gomPrefix).Union(dom.GetObjectsStartingWith("apc.")); // Union APC/APN
          break;
        case "eff.":
          itmList = dom.GetObjectsStartingWith("abl.").Where(x => x.Name.Contains("/"));
          break;
        default:
          itmList = dom.GetObjectsStartingWith(gomPrefix); //No need for the extra Linq statement for non-unique cases
          break;
      }
      return itmList;
    }
    //private static void GameObjectListAsJSON(String prefix, List<GomLib.Models.GameObject> itmList)
    //{
    //    Int32 i = 0;
    //    Int16 e = 0;
    //    String n = Environment.NewLine;
    //    var txtFile = new StringBuilder();
    //    String filename = String.Format("\\json\\{0}{1}", prefix, ".json");
    //    WriteFile(String.Format("{0}{1}", patch, n), filename, false);
    //    var count = itmList.Count();
    //    HashTableHashing.MurmurHash2Unsafe jsonHasher = new HashTableHashing.MurmurHash2Unsafe();
    //    //var blocksize = 100;
    //    //var blocks = count % blocksize;
    //    //if (blocks > 0)
    //    //{
    //    //    progressUpdate(i, count);
    //    //    for (Int32 b = blocks - 1; b >= 0; b--) //go backwards so we can delete items :)
    //    //    {
    //    //        System.Collections.Concurrent.BlockingCollection<String> blockCollection = new System.Collections.Concurrent.BlockingCollection<String>();
    //    //        var block = itmList.GetRange(b * blocksize, blocksize);
    //    //        Parallel.ForEach(block, itm =>
    //    //        {
    //    //            String jsonBlock = itm.ToJSON();
    //    //            blockCollection.Add(jsonBlock);
    //    //        });
    //    //        i += blocksize;
    //    //        txtFile.Append(String.Join(Environment.NewLine, blockCollection));
    //    //        itmList.RemoveRange(b * blocksize, blocksize);
    //    //    }
    //    //}
    //    using (var progress = new ProgressBar())
    //    {
    //        for (Int32 b = itmList.Count - 1; b >= 0; b--) //go backwards so we can delete values
    //        {
    //            progress.Report((Double)i / count);
    //            if (e % 1000 == 1)
    //            {
    //                WriteFile(txtFile.ToString(), filename, true);
    //                txtFile.Clear();
    //                e = 0;
    //            }

    //            String jsonString = itmList[b].ToJSON(); // ConvertToJson(itm); //added method in Tools.cs
    //            UInt32 hash = jsonHasher.Hash(Encoding.ASCII.GetBytes(jsonString));
    //            txtFile.Append(String.Format("{0},{1},{2}{3}", itmList[b].Base62Id, hash, jsonString, Environment.NewLine)); //Append it with a newline to the output.
    //            itmList[b] = null;
    //            i++;
    //            e++;
    //        }
    //    }
    //    //foreach (var itm in itmList) //clean up what's left
    //    //{
    //    //    progressUpdate(i, count);
    //    //    if (e % 1000 == 1)
    //    //    {
    //    //        WriteFile(txtFile.ToString(), filename, true);
    //    //        txtFile.Clear();
    //    //        e = 0;
    //    //    }

    //    //    addtolist2(String.Format("{0}: {1}", prefix, itm.Fqn));

    //    //    String jsonString = itm.ToJSON(); // ConvertToJson(itm); //added method in Tools.cs
    //    //    txtFile.Append(jsonString + Environment.NewLine); //Append it with a newline to the output.
    //    //    i++;
    //    //    e++;
    //    //}
    //    Console.Write(String.Format(" - Done. ({0})", i));
    //    WriteFile(txtFile.ToString(), filename, true);
    //    DeleteEmptyFile(filename, i);
    //    itmList = null;
    //    GC.Collect();
    //    CreateGzip(filename);
    //}
    //private static void PseudoGameObjectListAsJSON(String prefix, List<GomLib.Models.PseudoGameObject> itmList)
    //{
    //    Int32 i = 0;
    //    Int16 e = 0;
    //    String n = Environment.NewLine;
    //    var txtFile = new StringBuilder();
    //    String filename = String.Format("\\json\\{0}{1}", prefix, ".json");
    //    WriteFile(String.Format("{0}{1}", patch, n), filename, false);
    //    var count = itmList.Count();
    //    HashTableHashing.MurmurHash2Unsafe jsonHasher = new HashTableHashing.MurmurHash2Unsafe();
    //    using (var progress = new ProgressBar())
    //    {
    //        for (Int32 c = 0; c < count; c++)
    //        {
    //            progress.Report((Double)i / count);
    //            if (e % 1000 == 1)
    //            {
    //                WriteFile(txtFile.ToString(), filename, true);
    //                txtFile.Clear();
    //                e = 0;
    //            }

    //            String jsonString = itmList[c].ToJSON();
    //            UInt32 hash = jsonHasher.Hash(Encoding.ASCII.GetBytes(jsonString));
    //            txtFile.Append(String.Format("{0},{1},{2}{3}", itmList[c].Base62Id, hash, jsonString, Environment.NewLine)); //Append it with a newline to the output.
    //            itmList[c] = null;
    //            i++;
    //            e++;
    //        }
    //    }
    //    Console.Write(String.Format(" - Done. ({0})", i));
    //    WriteFile(txtFile.ToString(), filename, true);
    //    DeleteEmptyFile(filename, i);
    //    itmList = null;
    //    GC.Collect();
    //    CreateGzip(filename);
    //}
    //private static void GomObjectListAsJSON(String prefix, IEnumerable<GomLib.GomObject> itmList)
    //{
    //    Int32 i = 0;
    //    Int16 e = 0;
    //    String n = Environment.NewLine;
    //    var txtFile = new StringBuilder();
    //    String filename = String.Join("", prefix, ".json");
    //    WriteFile("", filename, false);
    //    var count = itmList.Count();
    //    using (var progress = new ProgressBar())
    //    {
    //        foreach (var itm in itmList)
    //        {
    //            progress.Report((Double)i / count);
    //            if (e % 1000 == 1)
    //            {
    //                WriteFile(txtFile.ToString(), filename, true);
    //                txtFile.Clear();
    //                e = 0;
    //            }

    //            //addtolist2(String.Format("{0}: {1}", prefix, itm.Name));

    //            String jsonString = GameObject.Load(itm, false).ToJSON(); // ConvertToJson(itm); //added method in Tools.cs
    //            txtFile.Append(jsonString + Environment.NewLine); //Append it with a newline to the output.
    //            i++;
    //            e++;
    //        }
    //    }
    //    Console.Write(String.Format(" - Done. ({0})", i));
    //    WriteFile(txtFile.ToString(), filename, true);
    //    DeleteEmptyFile(filename, i);
    //    itmList = null;
    //    GC.Collect();
    //    CreateGzip(filename);
    //}

    public static void CreateCompressedTooltipOutput(String xmlRoot, IEnumerable<Tooltip> itmList, String language) {
      //return;
      String file = String.Format("tooltips\\{0}tips({1}).zip", xmlRoot, language);
      WriteFile("", file, false);
      HashSet<String> iconNames = new HashSet<String>();
      using (var compressStream = new MemoryStream()) {
        //create the zip in memory
        using (var zipArchive = new ZipArchive(compressStream, ZipArchiveMode.Create, true)) {
          String compressedFolder = "tooltips/html/";
          switch (language) {
            case "fr-fr":
              compressedFolder = "tooltips/html/fr-fr/";
              Tooltip.Language = "frMale";
              break;
            case "de-de":
              compressedFolder = "tooltips/html/de-de/";
              Tooltip.Language = "deMale";
              break;
          }
          foreach (var t in itmList) {
            var torcEntry = zipArchive.CreateEntry(String.Format("{0}{1}.torctip", compressedFolder, t.Base62Id), CompressionLevel.Fastest);
            using (StreamWriter writer = new StreamWriter(torcEntry.Open(), Encoding.UTF8)) //old method. Race conditions led to Central Directory corruption.
              writer.Write(t.HTML);
            /*using (MemoryStream htmlStream = new MemoryStream(Encoding.UTF8.GetBytes(t.HTML ?? ""))) //see if this solves the Central Directory corruption.
            {
                using (var html = torcEntry.Open())
                    htmlStream.WriteTo(html);
            }*/
            if (language != "en-us") continue;
            String icon = "";
            String secondaryicon = "";
            if (t.Obj != null) {
              switch (t.Obj.GetType().ToString()) {
                case "GomLib.Models.Item":
                  icon = String.Format("icons/{0}", ((Item)t.Obj).Icon);
                  secondaryicon = String.Format("icons/{0}", ((Item)t.Obj).RepublicIcon);
                  break;
                case "GomLib.Models.Ability":
                  icon = String.Format("icons/{0}", ((Ability)t.Obj).Icon);
                  break;
                case "GomLib.Models.Quest":
                  icon = String.Format("codex/{0}", ((Quest)t.Obj).Icon);
                  break;
                case "GomLib.Models.Talent":
                  icon = String.Format("icons/{0}", ((Talent)t.Obj).Icon);
                  break;
                case "GomLib.Models.Achievement":
                  icon = String.Format("icons/{0}", ((Achievement)t.Obj).Icon);
                  break;
                case "GomLib.Models.Codex":
                  //break;
                  icon = String.Format("codex/{0}", ((Codex)t.Obj).Image);
                  break;
                case "GomLib.Models.NewCompanion":
                  //break;
                  icon = String.Format("portraits/{0}", ((NewCompanion)t.Obj).Icon);
                  break;
              }
            }
            if (t.PObj != null) {
              switch (t.PObj.GetType().ToString()) {
                case "GomLib.Models.Discipline":
                  icon = String.Format("icons/{0}", ((Item)t.Obj).Icon);
                  break;
                case "GomLib.Models.Collection":
                  icon = String.Format("mtxstore/{0}_260x260", ((Collection)t.PObj).Icon);
                  break;
                case "GomLib.Models.MtxStorefrontEntry":
                  icon = String.Format("mtxstore/{0}_260x260", ((MtxStorefrontEntry)t.PObj).Icon);
                  break;
              }
            }
            if (!String.IsNullOrEmpty(icon)) {
              icon = icon.ToLower();
              if (iconNames.Contains(icon))
                continue;
              else
                iconNames.Add(icon);
              IconParamter[] parms = new IconParamter[1];
              if (icon.StartsWith("portraits/"))
                parms[0] = IconParamter.PngFormat;
              using (MemoryStream iconStream = GetIcon(icon, parms)) {
                if (iconStream != null) {
                  ZipArchiveEntry iconEntry;
                  if (icon.StartsWith("codex/")) {
                    iconEntry = zipArchive.CreateEntry(String.Format("codex/{0}.jpg", GetIconFilename(icon)), CompressionLevel.Fastest);
                  } else if (icon.StartsWith("portraits/"))
                    iconEntry = zipArchive.CreateEntry(String.Format("portraits/{0}.png", GetIconFilename(icon)), CompressionLevel.Fastest);
                  else if (icon.StartsWith("mtxstore/"))
                    iconEntry = zipArchive.CreateEntry(String.Format("{0}.jpg", icon.ToLower()), CompressionLevel.Fastest);
                  else
                    iconEntry = zipArchive.CreateEntry(String.Format("icons/{0}.jpg", GetIconFilename(icon)), CompressionLevel.Fastest);
                  using var a = iconEntry.Open();
                  iconStream.WriteTo(a);
                  //using (Writer writer = new BinaryWriter(iconEntry.Open()))
                  //writer.(iconStream);
                }
              }
              if (icon.StartsWith("codex/")) {
                using MemoryStream iconStream = GetIcon(icon, true);
                if (iconStream != null) {
                  ZipArchiveEntry iconEntry;
                  iconEntry = zipArchive.CreateEntry(String.Format("codex/{0}_thumb.jpg", GetIconFilename(icon)), CompressionLevel.Fastest);
                  using var a = iconEntry.Open();
                  iconStream.WriteTo(a);
                }
              } else if (icon.StartsWith("portraits/")) {
                using MemoryStream iconStream = GetIcon(icon, true, IconParamter.PngFormat);
                if (iconStream != null) {
                  ZipArchiveEntry iconEntry;
                  iconEntry = zipArchive.CreateEntry(String.Format("portraits/{0}_thumb.png", GetIconFilename(icon)), CompressionLevel.Fastest);
                  using var a = iconEntry.Open();
                  iconStream.WriteTo(a);
                }
              } else if (icon.StartsWith("mtxstore/")) {
                List<String> sizes = new List<String>() { "120x120", "260x400", "400x400" };
                foreach (String size in sizes) {
                  String sizedIcon = icon.Replace("260x260", size);
                  using MemoryStream iconStream = GetIcon(sizedIcon);
                  if (iconStream != null) {
                    ZipArchiveEntry iconEntry;
                    iconEntry = zipArchive.CreateEntry(String.Format("{0}.jpg", sizedIcon.ToLower()), CompressionLevel.Fastest);
                    using var a = iconEntry.Open();
                    iconStream.WriteTo(a);
                  }
                }
              }

            }
            if (!String.IsNullOrEmpty(secondaryicon)) {
              secondaryicon = secondaryicon.ToLower();
              if (iconNames.Contains(secondaryicon))
                continue;
              else
                iconNames.Add(secondaryicon);
              using (MemoryStream iconStream = GetIcon(secondaryicon)) {
                if (iconStream != null) {
                  ZipArchiveEntry iconEntry;
                  if (icon.StartsWith("codex/")) {
                    iconEntry = zipArchive.CreateEntry(String.Format("codex/{0}.jpg", GetIconFilename(secondaryicon)), CompressionLevel.Fastest);
                  } else if (icon.StartsWith("mtxstore/"))
                    iconEntry = zipArchive.CreateEntry(String.Format("{0}.jpg", secondaryicon.ToLower()), CompressionLevel.Fastest);
                  else
                    iconEntry = zipArchive.CreateEntry(String.Format("icons/{0}.jpg", GetIconFilename(secondaryicon)), CompressionLevel.Fastest);
                  using var a = iconEntry.Open();
                  iconStream.WriteTo(a);
                  //using (Writer writer = new BinaryWriter(iconEntry.Open()))
                  //writer.(iconStream);
                }
              }
              if (icon.StartsWith("codex/")) {
                using MemoryStream iconStream = GetIcon(secondaryicon, true);
                if (iconStream != null) {
                  ZipArchiveEntry iconEntry;
                  iconEntry = zipArchive.CreateEntry(String.Format("codex/{0}_thumb.jpg", GetIconFilename(secondaryicon)), CompressionLevel.Fastest);
                  using var a = iconEntry.Open();
                  iconStream.WriteTo(a);
                }
              }
            }
          }
        }
        compressStream.Position = 0;
        WriteFile(compressStream, file);
      }
      Tooltip.Language = "enMale";
    }
    public static void CreatCompressedIconOutput(IEnumerable<TorArchive.HashFileInfo> iconList) {
      //return;
      String file = "newIcons.zip";
      if (!iconList.Any())
        return;
      WriteFile("", file, false);
      using var compressStream = new MemoryStream();
      //create the zip in memory
      using (var zipArchive = new ZipArchive(compressStream, ZipArchiveMode.Create, true)) {
        foreach (var t in iconList) {
          IconParamter[] parms = new IconParamter[1];
          var filename = String.Join("_", t.File.FileInfo.PrimaryHash, t.File.FileInfo.SecondaryHash);
          using MemoryStream iconStream = GetIcon(filename, t.File, false, parms);
          if (iconStream != null) {
            ZipArchiveEntry iconEntry = zipArchive.CreateEntry(String.Format("icons/{0}.jpg", filename), CompressionLevel.Fastest);
            using var a = iconEntry.Open();
            iconStream.WriteTo(a);
          }
        }
      }
      compressStream.Position = 0;
      WriteFile(compressStream, file);
    }

    private static MemoryStream GetIcon(String icon, params IconParamter[] encodingParams) {
      return GetIcon(icon, false, encodingParams);
    }
    private static MemoryStream GetIcon(String icon, Boolean generateThumb, params IconParamter[] encodingParams) {
      if (icon == null) return null;
      using var file = dom.Assets.FindFile(String.Format("/resources/gfx/{0}.dds", icon));
      if (file == null) return null;
      var filename = String.Join("_", file.FileInfo.PrimaryHash, file.FileInfo.SecondaryHash);
      return GetIcon(filename, file, generateThumb, encodingParams);
    }

    private static MemoryStream GetIcon(String filename, TorArchive.File file, Boolean generateThumb, params IconParamter[] encodingParams) {
      return GetIcon(filename, file, generateThumb, 90L, encodingParams);
    }
    private static MemoryStream GetIcon(String filename, TorArchive.File file, Boolean generateThumb, Int64 qual, params IconParamter[] encodingParams) {
      _ = filename;

      if (file == null) return null;
      /*using (*/
      MemoryStream outputStream = new MemoryStream();//)
                                                     //{
      DevIL.ImageImporter imp = new DevIL.ImageImporter();

      DevIL.Image dds;
      using (MemoryStream iconStream = (MemoryStream)file.OpenCopyInMemory())
        dds = imp.LoadImageFromStream(DevIL.ImageType.Dds, iconStream);
      var myparams = new EncoderParameters(1);
      ImageCodecInfo encoder;
      if (encodingParams.Contains(IconParamter.PngFormat)) {
        myparams.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, qual);
        encoder = GetEncoder(ImageFormat.Png);
      } else {
        myparams.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, qual);
        encoder = GetEncoder(ImageFormat.Jpeg);
      }

      DevIL.ImageExporter exp = new DevIL.ImageExporter();
      if (dds.Width == 52 && dds.Height == 52) // needs cropped
      {
        var iconData = dds.GetImageData(0);

        Bitmap iconBM = new Bitmap(iconData.Width, iconData.Height, PixelFormat.Format32bppArgb);

        for (Int32 k = 0; k < iconData.Height * iconData.Width; k++) // loop through image data
        {
          Color iconPixel = Color.FromArgb(iconData.Data[k * 4 + 3], // copy pixel values
                                iconData.Data[k * 4 + 0],
                                iconData.Data[k * 4 + 1],
                                iconData.Data[k * 4 + 2]);

          iconBM.SetPixel(k % iconData.Width, k / iconData.Width, iconPixel); //save pixel in new bitmap
        }

        Bitmap croppedIconBM = iconBM.Clone(new Rectangle(0, 0, 50, 50), iconBM.PixelFormat); // crop Bitmap
                                                                                              //croppedIconBM.Save(outputStream, System.Drawing.Imaging.ImageFormat.Png); //Bitmap to PNG Stream
        croppedIconBM.Save(outputStream, encoder, myparams); //Bitmap to PNG Stream
      } else {
        // var iconData = dds.GetImageData(0);

        //System.Drawing.Bitmap iconBM = new System.Drawing.Bitmap(iconData.Width, iconData.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);

        //for (Int32 k = 0; k < iconData.Height * iconData.Width; k++) // loop through image data
        //{
        //    Color iconPixel = Color.FromArgb(iconData.Data[k * 4 + 3], // copy pixel values
        //                iconData.Data[k * 4 + 0],
        //                iconData.Data[k * 4 + 1],
        //                iconData.Data[k * 4 + 2]);

        //    iconBM.SetPixel(k % iconData.Width, (Int32)k / iconData.Width, iconPixel); //save pixel in new bitmap
        //}

        //iconBM.Save(outputStream, jpgEncoder, myparams); //Bitmap to PNG Stream
        if (encodingParams.Contains(IconParamter.PngFormat)) {

          if (generateThumb) {
            dds.Resize(dds.Width / 4, dds.Height / 4, 4, DevIL.SamplingFilter.ScaleLanczos3, true);
          }
          exp.SaveImageToStream(dds, DevIL.ImageType.Png, outputStream);
        } else {
          using MemoryStream taco = new MemoryStream();
          exp.SaveImageToStream(dds, DevIL.ImageType.Bmp, taco); //save DDS to stream in jpg format
          Bitmap iconBM = new Bitmap(taco);
          if (generateThumb) {
            //Bitmap resized = new Bitmap(iconBM, new System.Drawing.Size(iconBM.Width / 4, iconBM.Height / 4));
            Bitmap resized = ResizeImage(iconBM, iconBM.Width / 4, iconBM.Height / 4);
            resized.Save(outputStream, encoder, myparams);
          } else
            iconBM.Save(outputStream, encoder, myparams); //Bitmap to JPG Stream
        }
      }

      //WriteFile(outputStream, String.Format("/{0}/Images/{1}.png", directory, filename));
      return outputStream;
      //}
    }
    private static String GetIconFilename(String icon) {
      if (icon == null) return "";
      using var file = dom.Assets.FindFile(String.Format("/resources/gfx/{0}.dds", icon));
      if (file == null)
        using (var file2 = dom.Assets.FindFile(String.Format("/resources/gfx/icons/{0}.dds", icon))) {
          if (file2 == null)
            return "";
          return String.Join("_", file2.FileInfo.PrimaryHash, file2.FileInfo.SecondaryHash);
        }
      return String.Join("_", file.FileInfo.PrimaryHash, file.FileInfo.SecondaryHash);
    }

    private static ImageCodecInfo GetEncoder(ImageFormat format) {

      ImageCodecInfo[] codecs = ImageCodecInfo.GetImageDecoders();

      foreach (ImageCodecInfo codec in codecs) {
        if (codec.FormatID == format.Guid) {
          return codec;
        }
      }
      return null;
    }
    public enum IconParamter {
      PngFormat
    }

    /// <summary>
    /// Resize the image to the specified width and height.
    /// </summary>
    /// <param name="image">The image to resize.</param>
    /// <param name="width">The width to resize to.</param>
    /// <param name="height">The height to resize to.</param>
    /// <returns>The resized image.</returns>
    public static Bitmap ResizeImage(Image image, Int32 width, Int32 height) {
      var destRect = new Rectangle(0, 0, width, height);
      var destImage = new Bitmap(width, height);

      destImage.SetResolution(image.HorizontalResolution, image.VerticalResolution);

      using (var graphics = Graphics.FromImage(destImage)) {
        graphics.CompositingMode = CompositingMode.SourceCopy;
        graphics.CompositingQuality = CompositingQuality.HighQuality;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.SmoothingMode = SmoothingMode.HighQuality;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

        using var wrapMode = new ImageAttributes();
        wrapMode.SetWrapMode(WrapMode.TileFlipXY);
        graphics.DrawImage(image, destRect, 0, 0, image.Width, image.Height, GraphicsUnit.Pixel, wrapMode);
      }

      return destImage;
    }
    #endregion
  }
}
