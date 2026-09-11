using System;
using System.Collections.Generic;
using System.Data;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using GomLib;

namespace PugTools {
  internal enum IconParamter {
    PngFormat,
  }

  internal partial class Tools {
    internal void CreatCompressedOutput(String xmlRoot,
                                      IEnumerable<GomLib.Models.Tooltip> itmList,
                                      String language) {

      String file = String.Format("tooltips\\{0}tips({1}).zip", xmlRoot, language);
      WriteFile("", file, false);
      HashSet<String> iconNames = new HashSet<String>();

      using (MemoryStream compressStream = new MemoryStream()) {
        //create the zip in memory
        using (ZipArchive zipArchive =
          new ZipArchive(compressStream, ZipArchiveMode.Create, true)) {

          String compressedFolder = "tooltips/html/";

          switch (language) {
            case "fr-fr":
              compressedFolder = "tooltips/html/fr-fr/";
              GomLib.Models.Tooltip.Language = "frMale";
              break;
            case "de-de":
              compressedFolder = "tooltips/html/de-de/";
              GomLib.Models.Tooltip.Language = "deMale";
              break;
            default:
              GomLib.Models.Tooltip.Language = "enMale";
              break;
          }

          foreach (var t in itmList) {
            ZipArchiveEntry torcEntry =
              zipArchive.CreateEntry(
                String.Format(
                  "{0}{1}.torctip",
                  compressedFolder,
                  t.Base62Id
                ),
                CompressionLevel.Fastest
              );

            // old method. Race conditions led to Central Directory corruption.
            using (StreamWriter writer = new StreamWriter(torcEntry.Open(), Encoding.UTF8))
              writer.Write(t.HTML);

            /*
            using (MemoryStream htmlStream = // see if this solves the Central Directory corruption.
                     new MemoryStream(Encoding.UTF8.GetBytes(t.HTML ?? ""))) {
                using (var html = torcEntry.Open())
                    htmlStream.WriteTo(html);
            }
            */

            if (language != "en-us") continue;

            String icon = "";
            String secondaryicon = "";

            if (t.Obj != null) {
              switch (t.Obj.GetType().ToString()) {
                case "GomLib.Models.Item":
                  icon =
                    String.Format("icons/{0}", ((GomLib.Models.Item)t.Obj).Icon);
                  secondaryicon =
                    String.Format("icons/{0}", ((GomLib.Models.Item)t.Obj).RepublicIcon);
                  break;
                case "GomLib.Models.Ability":
                  icon =
                    String.Format("icons/{0}", ((GomLib.Models.Ability)t.Obj).Icon);
                  break;
                case "GomLib.Models.Quest":
                  icon =
                    String.Format("codex/{0}", ((GomLib.Models.Quest)t.Obj).Icon);
                  break;
                case "GomLib.Models.Talent":
                  icon =
                    String.Format("icons/{0}", ((GomLib.Models.Talent)t.Obj).Icon);
                  break;
                case "GomLib.Models.Achievement":
                  icon =
                    String.Format("icons/{0}", ((GomLib.Models.Achievement)t.Obj).Icon);
                  break;
                case "GomLib.Models.Codex":
                  icon =
                    String.Format("codex/{0}", ((GomLib.Models.Codex)t.Obj).Image);
                  break;
                case "GomLib.Models.NewCompanion":
                  icon =
                    String.Format("portraits/{0}", ((GomLib.Models.NewCompanion)t.Obj).Icon);
                  break;
              }
            }

            if (t.PObj != null) {
              switch (t.PObj.GetType().ToString()) {
                case "GomLib.Models.Discipline":
                  icon =
                    String.Format("icons/{0}", ((GomLib.Models.Item)t.Obj).Icon);
                  break;
                case "GomLib.Models.Collection":
                  break;
                case "GomLib.Models.MtxStorefrontEntry":
                  icon =
                    String.Format(
                      "mtxstore/{0}_260x260",
                      ((GomLib.Models.MtxStorefrontEntry)t.PObj).Icon
                    );
                  break;
              }
            }

            if (!String.IsNullOrEmpty(icon)) {
              if (iconNames.Contains(icon)) continue;
              else iconNames.Add(icon);

              IconParamter[] parms = new IconParamter[1];

              if (icon.StartsWith("portraits/")) parms[0] = IconParamter.PngFormat;

              using (MemoryStream iconStream = GetIcon(icon, parms)) {
                if (iconStream != null) {
                  ZipArchiveEntry iconEntry;

                  if (icon.StartsWith("codex/")) {
                    iconEntry =
                      zipArchive.CreateEntry(
                        String.Format("codex/{0}.jpg", GetIconFilename(icon)),
                        CompressionLevel.Fastest
                      );
                  } else if (icon.StartsWith("portraits/"))
                    iconEntry =
                      zipArchive.CreateEntry(
                        String.Format("portraits/{0}.png", GetIconFilename(icon)),
                        CompressionLevel.Fastest
                      );
                  else if (icon.StartsWith("mtxstore/"))
                    iconEntry =
                      zipArchive.CreateEntry(
                        String.Format("{0}.jpg", icon.ToLower()),
                        CompressionLevel.Fastest
                      );
                  else
                    iconEntry =
                      zipArchive.CreateEntry(
                        String.Format("icons/{0}.jpg", GetIconFilename(icon)),
                        CompressionLevel.Fastest
                      );

                  using Stream a = iconEntry.Open();
                  iconStream.WriteTo(a);
                  //using (Writer writer = new BinaryWriter(iconEntry.Open()))
                  //writer.(iconStream);
                }
              }

              if (icon.StartsWith("codex/")) {
                using MemoryStream iconStream = GetIcon(icon, true);

                if (iconStream != null) {
                  ZipArchiveEntry iconEntry;
                  iconEntry =
                    zipArchive.CreateEntry(
                      String.Format("codex/{0}_thumb.jpg", GetIconFilename(icon)),
                      CompressionLevel.Fastest
                    );
                  using Stream a = iconEntry.Open();
                  iconStream.WriteTo(a);
                }
              } else if (icon.StartsWith("portraits/")) {
                using MemoryStream iconStream = GetIcon(icon, true, IconParamter.PngFormat);
                if (iconStream != null) {
                  ZipArchiveEntry iconEntry;
                  iconEntry =
                    zipArchive.CreateEntry(
                      String.Format("portraits/{0}_thumb.png", GetIconFilename(icon)),
                      CompressionLevel.Fastest
                    );
                  using Stream a = iconEntry.Open();
                  iconStream.WriteTo(a);
                }
              } else if (icon.StartsWith("mtxstore/")) {
                List<String> sizes = new List<String>() { "120x120", "260x400", "400x400" };

                foreach (String size in sizes) {
                  String sizedIcon = icon.Replace("260x260", size);
                  using MemoryStream iconStream = GetIcon(sizedIcon);

                  if (iconStream != null) {
                    ZipArchiveEntry iconEntry;
                    iconEntry =
                      zipArchive.CreateEntry(
                        String.Format("{0}.jpg", sizedIcon.ToLower()),
                        CompressionLevel.Fastest
                      );
                    using Stream a = iconEntry.Open();
                    iconStream.WriteTo(a);
                  }
                }
              }
            }

            if (!String.IsNullOrEmpty(secondaryicon)) {
              if (iconNames.Contains(secondaryicon)) continue;
              else iconNames.Add(secondaryicon);

              using (MemoryStream iconStream = GetIcon(secondaryicon)) {
                if (iconStream != null) {
                  ZipArchiveEntry iconEntry;

                  if (icon.StartsWith("codex/")) {
                    iconEntry =
                      zipArchive.CreateEntry(
                        String.Format("codex/{0}.jpg", GetIconFilename(secondaryicon)),
                        CompressionLevel.Fastest
                      );
                  } else if (icon.StartsWith("mtxstore/"))
                    iconEntry =
                      zipArchive.CreateEntry(
                        String.Format("{0}.jpg", secondaryicon.ToLower()),
                        CompressionLevel.Fastest
                      );
                  else
                    iconEntry =
                      zipArchive.CreateEntry(
                        String.Format("icons/{0}.jpg", GetIconFilename(secondaryicon)),
                        CompressionLevel.Fastest
                      );
                  using Stream a = iconEntry.Open();
                  iconStream.WriteTo(a);
                  //using (Writer writer = new BinaryWriter(iconEntry.Open()))
                  //writer.(iconStream);
                }
              }
              if (icon.StartsWith("codex/")) {
                using MemoryStream iconStream = GetIcon(secondaryicon, true);

                if (iconStream != null) {
                  ZipArchiveEntry iconEntry;
                  iconEntry =
                    zipArchive.CreateEntry(
                      String.Format("codex/{0}_thumb.jpg", GetIconFilename(secondaryicon)),
                      CompressionLevel.Fastest
                    );
                  using Stream a = iconEntry.Open();
                  iconStream.WriteTo(a);
                }
              }
            }
          }
        }

        compressStream.Position = 0;
        WriteFile(compressStream, file);
      }

      GomLib.Models.Tooltip.Language = GomLib.StringTable.SelectedLocalization;
    }
    private static ImageCodecInfo GetEncoder(ImageFormat format) {
      ImageCodecInfo[] codecs = ImageCodecInfo.GetImageDecoders();

      foreach (ImageCodecInfo codec in codecs) {
        if (codec.FormatID == format.Guid) return codec;
      }

      return null;
    }
    private static MemoryStream GetIcon(String filename,
                                        TorArchive.File file,
                                        Boolean generateThumb,
                                        params IconParamter[] encodingParams) {

      if (filename == null) throw new ArgumentNullException(nameof(filename));

      if (file == null) return null;

      MemoryStream outputStream = new MemoryStream();
      DevIL.ImageImporter imp = new DevIL.ImageImporter();
      DevIL.Image dds;

      using (MemoryStream iconStream = (MemoryStream)file.OpenCopyInMemory())
        dds = imp.LoadImageFromStream(DevIL.ImageType.Dds, iconStream);

      EncoderParameters myparams = new EncoderParameters(1);
      ImageCodecInfo encoder;

      if (encodingParams.Contains(IconParamter.PngFormat)) {
        myparams.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 90L);
        encoder = GetEncoder(ImageFormat.Png);
      } else {
        myparams.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 90L);
        encoder = GetEncoder(ImageFormat.Jpeg);
      }

      DevIL.ImageExporter exp = new DevIL.ImageExporter();
      if (dds.Width == 52 && dds.Height == 52) { // needs cropped
        DevIL.ImageData iconData = dds.GetImageData(0);

        Bitmap iconBM = new Bitmap(iconData.Width, iconData.Height, PixelFormat.Format32bppArgb);

        for (Int32 k = 0; k < iconData.Height * iconData.Width; k++) { // loop through image data
          Color iconPixel =
            Color.FromArgb(
              iconData.Data[k * 4 + 3], // copy pixel values
              iconData.Data[k * 4 + 0],
              iconData.Data[k * 4 + 1],
              iconData.Data[k * 4 + 2]
            );

          // Save pixel in new bitmap
          iconBM.SetPixel(k % iconData.Width, k / iconData.Width, iconPixel);
        }

        Bitmap croppedIconBM = iconBM.Clone(new Rectangle(0, 0, 50, 50), iconBM.PixelFormat);
        croppedIconBM.Save(outputStream, encoder, myparams); //Bitmap to PNG Stream
      } else {
        // _ = dds.GetImageData(0);

        // System.Drawing.Bitmap iconBM = 
        // new System.Drawing.Bitmap(
        //   iconData.Width, 
        //   iconData.Height, 
        //   System.Drawing.Imaging.PixelFormat.Format32bppArgb
        // );

        // Loop through image data
        // for (Int32 k = 0; k < iconData.Height * iconData.Width; k++) {
        //   Color iconPixel = Color.FromArgb(iconData.Data[k * 4 + 3], // copy pixel values
        //     iconData.Data[k * 4 + 0],
        //     iconData.Data[k * 4 + 1],
        //     iconData.Data[k * 4 + 2]);
        //
        //   iconBM.SetPixel(
        //     k % iconData.Width, 
        //     (Int32)k / iconData.Width, 
        //     iconPixel
        //   ); //save pixel in new bitmap
        //}

        // iconBM.Save(outputStream, jpgEncoder, myparams); //Bitmap to PNG Stream

        if (encodingParams.Contains(IconParamter.PngFormat)) {
          if (generateThumb)
            dds.Resize(dds.Width / 4, dds.Height / 4, 4, DevIL.SamplingFilter.ScaleLanczos3, true);

          exp.SaveImageToStream(dds, DevIL.ImageType.Png, outputStream);
        } else {
          using MemoryStream taco = new MemoryStream();
          exp.SaveImageToStream(dds, DevIL.ImageType.Bmp, taco); //save DDS to stream in jpg format
          Bitmap iconBM = new Bitmap(taco);

          if (generateThumb) {
            Bitmap resized = new Bitmap(iconBM, new Size(iconBM.Width / 4, iconBM.Height / 4));
            resized.Save(outputStream, encoder, myparams);
          } else
            iconBM.Save(outputStream, encoder, myparams); //Bitmap to JPG Stream
        }
      }

      //WriteFile(outputStream, String.Format("/{0}/Images/{1}.png", directory, filename));
      return outputStream;
      //}
    }
    private MemoryStream GetIcon(String icon,
                                Boolean generateThumb,
                                params IconParamter[] encodingParams) {

      if (icon == null) return null;

      using TorArchive.File file =
        CurrentDom.Assets.FindFile(String.Format("/resources/gfx/{0}.dds", icon));
      if (file == null) return null;

      String filename = String.Join("_", file.FileInfo.PrimaryHash, file.FileInfo.SecondaryHash);
      return GetIcon(filename, file, generateThumb, encodingParams);
    }
    private MemoryStream GetIcon(String icon, params IconParamter[] encodingParams) {
      return GetIcon(icon, false, encodingParams);
    }
    private String GetIconFilename(String icon) {
      if (icon == null) return "";

      using TorArchive.File file =
        CurrentDom.Assets.FindFile(String.Format("/resources/gfx/{0}.dds", icon));

      if (file == null)
        using (TorArchive.File file2 =
          CurrentDom.Assets.FindFile(String.Format("/resources/gfx/icons/{0}.dds", icon))) {

          if (file2 == null) return "";
          else return String.Join("_", file2.FileInfo.PrimaryHash, file2.FileInfo.SecondaryHash);
        }

      return String.Join("_", file.FileInfo.PrimaryHash, file.FileInfo.SecondaryHash);
    }
    internal void GetTooltips() {
      Clearlist2();
      LoadData();

      Dictionary<String, String> gameObjects = new Dictionary<String, String> {
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
      Boolean frLoaded = _currentAssets.LoadedFileGroups.Contains("fr-fr");
      Boolean deLoaded = _currentAssets.LoadedFileGroups.Contains("de-de");

      for (Int32 f = 0; f < gameObjects.Count; f++) {
        KeyValuePair<String, String> gameObj = gameObjects.ElementAt(f);
        ClearProgress();
        IEnumerable<GomObject> gomList =
          CurrentDom.GetObjectsStartingWith(gameObj.Key).Where(x => !x.Name.Contains("/"));
        Int32 count = gomList.Count();
        Int32 i = 0;
        AddToList2(String.Format("Checking {0}", gameObj.Key));
        List<GomLib.Models.Tooltip> iList = new List<GomLib.Models.Tooltip>();
        //List<GomLib.Models.Tooltip> frList = new List<GomLib.Models.Tooltip>();
        //List<GomLib.Models.Tooltip> deList = new List<GomLib.Models.Tooltip>();

        foreach (GomObject gom in gomList) {
          ProgressUpdate(i, count);
          Boolean okToOutput = true;

          if (chkBuildCompare.Checked) {
            GomLib.Models.GameObject itm = GomLib.Models.GameObject.Load(gom);
            GomLib.Models.GameObject itm2 = GomLib.Models.GameObject.Load(gom.Id, PreviousDom);

            if (itm2 != null && itm != null)
              if (itm.GetHashCode() == itm2.GetHashCode()) okToOutput = false;
          }

          if (okToOutput) {
            GomLib.Models.Tooltip t = new GomLib.Models.Tooltip(gom.Id, CurrentDom);
            /*
            if (itm.GetType() == typeof(GomLib.Models.Item)) {
                OutputIcon(((GomLib.Models.Item)itm).Icon, "TORC");
            }
            */

            // if (((GomLib.Models.Schematic)t.obj).NameId != 0)
            //   WriteFile(
            //     t.Base62Id 
            //       + ";" 
            //       + ((GomLib.Models.Schematic)t.obj).Fqn 
            //       + ";" 
            //       + ((GomLib.Models.Schematic)t.obj).MissionFaction 
            //       + ";" 
            //       + ((GomLib.Models.Schematic)t.obj).Name 
            //       + Environment.NewLine, "schematics.txt", true
            //   );

            iList.Add(t);

            // if (frLoaded) {
            //   GomLib.Models.Tooltip.language = "frMale";
            //   t = new GomLib.Models.Tooltip(gom.Id, currentDom);
            //   var table = currentDom.stringTable.Find("str.gui.alignment");
            //   frList.Add(t);
            //   GomLib.Models.Tooltip.language = "enMale";
            // }

            // if (deLoaded) {
            //   GomLib.Models.Tooltip.language = "deMale";
            //   t = new GomLib.Models.Tooltip(gom.Id, currentDom);
            //   deList.Add(t);
            //   GomLib.Models.Tooltip.language = "enMale";
            // }
          }

          i++;
        }
        //ObjectListAsSql(gameObj.Value, "Tooltip", iList);
        CreatCompressedOutput(gameObj.Value, iList, GomLib.StringTable.SelectedLocale);
      }

      // Dictionary<String, String> protoGameObjects = new Dictionary<String, String> {
      //   {"mtxStorefrontInfoPrototype", "mtxStorefrontData"},
      //   {"colCollectionItemsPrototype", "colCollectionItemsData"},
      //   {"chrCompanionInfo_Prototype", "chrCompanionInfoData"},
      //   {"scFFShipsDataPrototype", "scFFShipsData"},
      //   {"wevConquestInfosPrototype", "wevConquestTable"},
      //   {"achCategoriesTable_Prototype", "achCategoriesData"}
      // };

      Dictionary<String, String[]> protoGameObjects = new Dictionary<String, String[]> {
        { "Collections", new String[2] {
          "colCollectionItemsPrototype", "colCollectionItemsData" }
        },
        { "MtxStoreFronts", new String[2] {
          "mtxStorefrontInfoPrototype", "mtxStorefrontItems" }
        },
        { "SetBonuses", new String[2] {
          "itmSetBonusesPrototype", "itmSetBonuses" }
        },
        // {"Discipline", new String[2] {"ablPackagePrototype", "classDisciplinesTable"}},
      };


      for (Int32 f = 0; f < protoGameObjects.Count; f++) {
        KeyValuePair<String, String[]> gameObj = protoGameObjects.ElementAt(f);
        Dictionary<Object, Object> currentDataProto = new Dictionary<Object, Object>();
        GomObject currentDataObject = CurrentDom.GetObject(gameObj.Value[0]);

        if (currentDataObject != null) { // fix to ensure old game assets don't throw exceptions.
          currentDataProto = currentDataObject.Data.ValueOrDefault<Dictionary<Object, Object>>(
            gameObj.Value[1], null)
            ?? (gameObj.Key == "Collections"
              ? currentDataObject.Data.ValueOrDefault<Dictionary<Object, Object>>(
                  "colMtxItemIdToCollectionItem", null)
                ?? currentDataObject.Data.ValueOrDefault<Dictionary<Object, Object>>(
                  "4611686297655094008", new Dictionary<Object, Object>())
              : gameObj.Key == "MtxStoreFronts"
                ? currentDataObject.Data.ValueOrDefault<Dictionary<Object, Object>>(
                    "mtxStorefrontData", new Dictionary<Object, Object>())
                : new Dictionary<Object, Object>());
          currentDataObject.Unload();
        }

        ClearProgress();
        Int32 count = currentDataProto.Count;
        Int32 i = 0;
        AddToList2(String.Format("Checking {0}", gameObj.Key));
        List<GomLib.Models.Tooltip> iList = new List<GomLib.Models.Tooltip>();

        foreach (var gom in currentDataProto) {
          ProgressUpdate(i, count);
          GomLib.Models.PseudoGameObject itm =
            GomLib.Models.PseudoGameObject.Load(
              gameObj.Key,
              CurrentDom,
              gom.Key,
              (GomObjectData)gom.Value
            );
          Boolean okToOutput = true;

          // if (chkBuildCompare.Checked) {
          //   var itm2 = 
          //     GomLib.Models.PseudoGameObject.LoadFromProtoName(
          //       gameObj.Value[0], 
          //       previousDom, 
          //       gom.Key, 
          //       (GomObjectData)gom.Value
          //     );
          //
          //   if (itm2 != null) {
          //     if (itm.GetHashCode() == itm2.GetHashCode()) okToOutput = false;
          //   }
          // }

          if (okToOutput) {
            GomLib.Models.Tooltip t = new GomLib.Models.Tooltip(itm);

            /*
            if (itm.GetType() == typeof(GomLib.Models.Item)) {
                OutputIcon(((GomLib.Models.Item)itm).Icon, "TORC");
            }
            */

            // if (((GomLib.Models.Schematic)t.obj).NameId != 0)
            //   WriteFile(
            //     t.Base62Id 
            //       + ";" 
            //       + ((GomLib.Models.Schematic)t.obj).Fqn 
            //       + ";" 
            //       + ((GomLib.Models.Schematic)t.obj).MissionFaction 
            //       + ";" 
            //       + ((GomLib.Models.Schematic)t.obj).Name 
            //       + Environment.NewLine, 
            //     "schematics.txt", 
            //     true
            //   );

            iList.Add(t);
          }

          i++;
        }

        CreatCompressedOutput(gameObj.Key, iList, GomLib.StringTable.SelectedLocale);
      }

      OutputDiscIcons();
      EnableButtons();
    }
    internal void OutputDiscIcons() {
      Dictionary<String, String[]> protoGameObjects = new Dictionary<String, String[]> {
        {"Discipline", new String[] {"ablPackagePrototype", "classDisciplinesTable"}},
      };

      for (Int32 f = 0; f < protoGameObjects.Count; f++) {
        KeyValuePair<String, String[]> gameObj = protoGameObjects.ElementAt(f);
        Dictionary<Object, Object> currentDataProto = new Dictionary<Object, Object>();
        GomObject currentDataObject = CurrentDom.GetObject(gameObj.Value[0]);

        if (currentDataObject != null) { //fix to ensure old game assets don't throw exceptions.
          currentDataProto =
            currentDataObject.Data.Get<Dictionary<Object, Object>>(gameObj.Value[1]);
          currentDataObject.Unload();
        }

        ClearProgress();
        Int32 count = currentDataProto.Count;
        Int32 i = 0;
        AddToList2(String.Format("Checking {0}", gameObj.Key));
        List<GomLib.Models.Discipline> iList = new List<GomLib.Models.Discipline>();

        foreach (var gom in currentDataProto) {
          ProgressUpdate(i, count);
          List<GomObjectData> discData =
            ((List<Object>)gom.Value).ConvertAll(x => (GomObjectData)x);

          foreach (GomObjectData disc in discData) {
            GomLib.Models.Discipline dis = new GomLib.Models.Discipline();
            CurrentDom.DisciplineLoader.Load(dis, disc);
            iList.Add(dis);
          }

          i++;
        }

        WriteFile("", "disciplineIcons.zip", false);
        HashSet<String> iconNames = new HashSet<String>();

        using MemoryStream compressStream = new MemoryStream();

        //create the zip in memory
        using (ZipArchive zipArchive =
          new ZipArchive(compressStream, ZipArchiveMode.Create, true)) {

          foreach (GomLib.Models.Discipline t in iList) {
            String icon = "icons/" + t.Icon;

            if (!String.IsNullOrEmpty(icon)) {
              if (iconNames.Contains(icon)) continue;
              else iconNames.Add(icon);

              using MemoryStream iconStream = GetIcon(icon);

              if (iconStream != null) {
                ZipArchiveEntry iconEntry =
                  zipArchive.CreateEntry(
                    String.Format(
                      "icons/{0}.jpg",
                      GetIconFilename(icon)
                    ),
                    CompressionLevel.Fastest
                  );
                using Stream a = iconEntry.Open();
                iconStream.WriteTo(a);
                // using (Writer writer = new BinaryWriter(iconEntry.Open()))
                //   writer.(iconStream);
              }
            }
          }
        }

        compressStream.Position = 0;
        WriteFile(compressStream, "disciplineIcons.zip");
      }
    }
    private static void OutputIcon(String filename, TorArchive.File file, String directory) {
      Boolean fileExists = !File.Exists(String.Format(
                                 "{0}{1}/{2}/Images/{3}.dds",
                                 Config.ExtractPath,
                                 s_prefix,
                                 directory,
                                 filename
                               )
                   );

      if (fileExists) {
        if (file != null) {
          DevIL.ImageImporter imp = new DevIL.ImageImporter();
          DevIL.Image dds;

          using (MemoryStream iconStream = (MemoryStream)file.OpenCopyInMemory())
            dds = imp.LoadImageFromStream(DevIL.ImageType.Dds, iconStream);

          using MemoryStream outputStream = new MemoryStream();
          DevIL.ImageExporter exp = new DevIL.ImageExporter();

          if (dds.Width == 52 && dds.Height == 52) { // needs cropped
            DevIL.ImageData iconData = dds.GetImageData(0);
            Bitmap iconBM = new Bitmap(iconData.Width, iconData.Height, PixelFormat.Format32bppArgb);

            // Loop through image data
            for (Int32 k = 0; k < iconData.Height * iconData.Width; k++) {
              Color iconPixel =
                Color.FromArgb(
                  iconData.Data[k * 4 + 3], // copy pixel values
                  iconData.Data[k * 4 + 0],
                  iconData.Data[k * 4 + 1],
                  iconData.Data[k * 4 + 2]
                );

              // Save pixel in new bitmap
              iconBM.SetPixel(k % iconData.Width, k / iconData.Width, iconPixel);
            }

            // Crop Bitmap
            Bitmap croppedIconBM = iconBM.Clone(new Rectangle(0, 0, 50, 50), iconBM.PixelFormat);
            // Bitmap to PNG Stream
            croppedIconBM.Save(outputStream, ImageFormat.Png);
          } else {
            // Save DDS to stream in PNG format
            exp.SaveImageToStream(dds, DevIL.ImageType.Png, outputStream);
          }

          WriteFile(outputStream, String.Format("/{0}/Images/{1}.png", directory, filename));
        }
      }
    }
    private void OutputIcon(String icon, String directory) {
      if (icon == null) return;

      using TorArchive.File file =
        CurrentDom.Assets.FindFile(String.Format("/resources/gfx/icons/{0}.dds", icon));

      if (file == null) return;

      String filename = String.Join("_", file.FileInfo.PrimaryHash, file.FileInfo.SecondaryHash);
      OutputIcon(filename, file, directory);
    }
  }
}
