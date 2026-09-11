using System;
using System.Collections.Generic;
using System.IO;
using MessageBox = System.Windows.Forms.MessageBox;

namespace PugTools {
  internal partial class Tools {
    internal void GetIcons() { //needs updating
      Clearlist2();
      LoadData();

      Double i = 0;

      AddToList2("Icon Extract Started");
      StreamWriter file2 = new StreamWriter(Config.ExtractPath + "icons.txt", true);

      AddToList2("Starting Item Icon Extraction");
      List<GomLib.GomObject> itmList = CurrentDom.GetObjectsStartingWith("itm.");

      foreach (var gomItm in itmList) {
        string icon = gomItm.Data.ValueOrDefault<String>("itmIcon", null);

        if (icon != null) {
          file2.WriteLine("/resources/gfx/icons/" + icon + ".dds");
          file2.WriteLine("/resources/gfx/mtxstore/" + icon + "_120x120.dds");
          file2.WriteLine("/resources/gfx/mtxstore/" + icon + "_260x400.dds");
          file2.WriteLine("/resources/gfx/mtxstore/" + icon + "_260x260.dds");
          file2.WriteLine("/resources/gfx/mtxstore/" + icon + "_328x160.dds");
          file2.WriteLine("/resources/gfx/mtxstore/" + icon + "_400x400.dds");

          i++;
        }
      }

      GC.Collect();
      AddToList2("Starting Ability Icon Extraction");

      List<GomLib.GomObject> ablList = CurrentDom.GetObjectsStartingWith("abl.");

      foreach (GomLib.GomObject gomItm in ablList) {
        String icon = gomItm.Data.ValueOrDefault<String>("ablIconSpec", null);

        if (icon != null) {
          file2.WriteLine("/resources/gfx/icons/" + icon + ".dds");
          i++;
        }

        icon = gomItm.Data.ValueOrDefault<String>("effIcon", null);

        if (icon != null) {
          file2.WriteLine("/resources/gfx/icons/" + icon + ".dds");
          i++;
        }
      }

      GC.Collect();
      AddToList2("Starting Quest Icon Extraction");

      List<GomLib.GomObject> qstList = CurrentDom.GetObjectsStartingWith("qst.");

      foreach (GomLib.GomObject gomItm in qstList) {
        String icon = gomItm.Data.ValueOrDefault<String>("qstMissionIcon", null);

        if (icon != null) {
          file2.WriteLine("/resources/gfx/codex/" + icon + ".dds");
          i++;
        }
      }
      // _ = CurrentDom.GetObjectsStartingWith("invalid");
      GC.Collect();

      AddToList2("Starting Item Apperance Icon Extraction");
      List<GomLib.GomObject> ippList = CurrentDom.GetObjectsStartingWith("ipp.");

      foreach (GomLib.GomObject gomItm in ippList) {
        String icon = gomItm.Name.ToString();

        if (icon != null) {
          file2.WriteLine("/resources/gfx/icons/" + icon + ".dds");
          file2.WriteLine("/resources/gfx/icons/" + icon.Replace("ipp.", "") + ".dds");

          file2.WriteLine("/resources/gfx/mtxstore/" + icon + "_120x120.dds");
          file2.WriteLine("/resources/gfx/mtxstore/" + icon + "_260x260.dds");
          file2.WriteLine("/resources/gfx/mtxstore/" + icon + "_260x400.dds");
          file2.WriteLine("/resources/gfx/mtxstore/" + icon + "_328x160.dds");
          file2.WriteLine("/resources/gfx/mtxstore/" + icon + "_400x400.dds");

          file2.WriteLine("/resources/gfx/mtxstore/" + icon.Replace("ipp.", "") + "_120x120.dds");
          file2.WriteLine("/resources/gfx/mtxstore/" + icon.Replace("ipp.", "") + "_260x260.dds");
          file2.WriteLine("/resources/gfx/mtxstore/" + icon.Replace("ipp.", "") + "_260x400.dds");
          file2.WriteLine("/resources/gfx/mtxstore/" + icon.Replace("ipp.", "") + "_328x160.dds");
          file2.WriteLine("/resources/gfx/mtxstore/" + icon.Replace("ipp.", "") + "_400x400.dds");

          i++;
        }
      }

      GC.Collect();
      AddToList2("Starting Codex Icon/Image Extraction");

      List<GomLib.GomObject> cdxList = CurrentDom.GetObjectsStartingWith("cdx.");
      foreach (GomLib.GomObject gomItm in cdxList) {
        String icon = gomItm.Data.ValueOrDefault<String>("cdxImage", null);

        if (icon != null) {
          file2.WriteLine("/resources/gfx/codex/" + icon + ".dds");
          i++;
        }
      }

      GC.Collect();
      AddToList2("Starting Achievement Icon Extraction");

      List<GomLib.GomObject> achList = CurrentDom.GetObjectsStartingWith("ach.");
      foreach (GomLib.GomObject gomItm in achList) {
        String icon = gomItm.Data.ValueOrDefault<String>("achIcon", null);

        if (icon != null) {
          file2.WriteLine("/resources/gfx/icons/" + icon + ".dds");
          i++;
        }
      }

      GC.Collect();
      AddToList2("Starting Talent Icon Extraction");

      List<GomLib.GomObject> talList = CurrentDom.GetObjectsStartingWith("tal.");
      foreach (GomLib.GomObject gomItm in talList) {
        String icon = gomItm.Data.ValueOrDefault<String>("talTalentIcon", null);

        if (icon != null) {
          file2.WriteLine("/resources/gfx/icons/" + icon + ".dds");
          i++;
        }
      }

      GC.Collect();
      AddToList2("Starting Space PVP Icon Extraction");

      List<String> spvpIcons = new List<String> {
        "armor_",
        "capacitor_",
        "eng_",
        "magazine_",
        "pweap_",
        "reactor_",
        "sensor_",
        "shield_",
        "sweap_",
        "sys_",
        "thruster_"
      };

      foreach (String cmp in spvpIcons) {
        List<GomLib.GomObject> spvpList1 = CurrentDom.GetObjectsStartingWith(cmp);

        foreach (GomLib.GomObject gomItm in spvpList1) {
          String icon = gomItm.Data.ValueOrDefault<String>("scFFComponentIcon", null);

          if (icon != null) {
            file2.WriteLine("/resources/gfx/icons/" + icon + ".dds");
            i++;
          }
        }

        GC.Collect();
      }

      GomLib.GomObject shipDataProto = CurrentDom.GetObject("scFFShipsDataPrototype");
      Dictionary<Object, Object> shipData =
        shipDataProto.Data.ValueOrDefault<Dictionary<Object, Object>>("scFFShipsData", null);

      if (shipData != null) {
        foreach (var item in shipData) {
          GomLib.GomObjectData item2 = (GomLib.GomObjectData)item.Value;
          item2.Dictionary.TryGetValue("scFFShipHullIcon", out Object icon1_string);
          // This doesn't appear anywhere in this prototype. What was this from?
          item2.Dictionary.TryGetValue("scFFShipIcon", out Object icon2_string);

          if (icon1_string != null) {
            file2.WriteLine("/resources/gfx/icons/" + icon1_string + ".dds");
            file2.WriteLine("/resources/gfx/textures/" + icon1_string + ".dds");
            i++;
          }

          if (icon2_string != null) {
            file2.WriteLine("/resources/gfx/icons/" + icon2_string + ".dds");
            file2.WriteLine("/resources/gfx/textures/" + icon2_string + ".dds");
            i++;
          }
        }
      }

      GC.Collect();

      GomLib.GomObject shipColorOptionProto = CurrentDom.GetObject("scFFColorOptionMasterPrototype");
      Dictionary<Object, Object> shipColors =
        shipColorOptionProto.Data.ValueOrDefault<Dictionary<Object, Object>>(
          "scFFComponentColorUIData",
          null
        );

      if (shipColors != null) {
        foreach (var item in shipColors) {
          GomLib.GomObjectData item2 = (GomLib.GomObjectData)item.Value;
          item2.Dictionary.TryGetValue("scFFComponentColorIcon", out Object icon_string);

          if (icon_string != null) {
            file2.WriteLine("/resources/gfx/icons/" + icon_string + ".dds");
            file2.WriteLine("/resources/gfx/textures/" + icon_string + ".dds");
            i++;
          }
        }
      }

      GC.Collect();

      GomLib.GomObject scffCrewProto = CurrentDom.GetObject("scffCrewPrototype");
      Dictionary<Object, Object> shipCrew =
        scffCrewProto.Data.ValueOrDefault<Dictionary<Object, Object>>(
          "scFFShipsCrewAndPatternData",
          null
        );

      if (shipCrew != null) {
        foreach (var item in shipCrew) {
          GomLib.GomObjectData item2 = (GomLib.GomObjectData)item.Value;
          item2.Dictionary.TryGetValue("scFFCrewIcon", out Object icon_string);

          if (icon_string != null) {
            file2.WriteLine("/resources/gfx/icons/" + icon_string + ".dds");
            file2.WriteLine("/resources/gfx/textures/" + icon_string + ".dds");
            i++;
          }
        }
      }

      GC.Collect();
      AddToList2("Starting MTX Store Icon Extraction");

      GomLib.GomObject mtxStore = CurrentDom.GetObject("mtxStorefrontInfoPrototype");
      Dictionary<Object, Object> mtxItems =
        mtxStore?.Data.ValueOrDefault<Dictionary<Object, Object>>("mtxStorefrontItems", null)
        ?? mtxStore?.Data.ValueOrDefault<Dictionary<Object, Object>>("mtxStorefrontData", null);

      if (mtxItems != null) {
        foreach (var item in mtxItems) {
          GomLib.GomObjectData item2 = (GomLib.GomObjectData)item.Value;
          if (!item2.Dictionary.TryGetValue("mtxStorefrontItemImage", out Object icon_string))
            item2.Dictionary.TryGetValue("mtxStorefrontIcon", out icon_string);

          if (icon_string != null) {
            String icon = icon_string.ToString().ToLower();

            file2.WriteLine("/resources/gfx/icons/" + icon + ".dds");
            file2.WriteLine("/resources/gfx/mtxstore/" + icon + "_120x120.dds");
            file2.WriteLine("/resources/gfx/mtxstore/" + icon + "_260x400.dds");
            file2.WriteLine("/resources/gfx/mtxstore/" + icon + "_260x260.dds");
            file2.WriteLine("/resources/gfx/mtxstore/" + icon + "_328x160.dds");
            file2.WriteLine("/resources/gfx/mtxstore/" + icon + "_400x400.dds");
            i++;
          }
        }
      }

      GC.Collect();

      GomLib.GomObject colCategoriesProto =
        CurrentDom.GetObject("colCollectionCategoriesPrototype");
      Dictionary<Object, Object> colCats =
        colCategoriesProto.Data.ValueOrDefault<Dictionary<Object, Object>>(
          "colCollectionCategoryData",
          null
        );

      if (colCats != null) {
        foreach (var item in colCats) {
          GomLib.GomObjectData item2 = (GomLib.GomObjectData)item.Value;
          item2.Dictionary.TryGetValue("colCollectionCategoryIcon", out Object icon_string);

          if (icon_string != null) {
            String icon = icon_string.ToString().ToLower();

            file2.WriteLine("/resources/gfx/icons/" + icon + ".dds");
            file2.WriteLine("/resources/gfx/mtxstore/" + icon + "_120x120.dds");
            file2.WriteLine("/resources/gfx/mtxstore/" + icon + "_260x400.dds");
            file2.WriteLine("/resources/gfx/mtxstore/" + icon + "_260x260.dds");
            file2.WriteLine("/resources/gfx/mtxstore/" + icon + "_328x160.dds");
            file2.WriteLine("/resources/gfx/mtxstore/" + icon + "_400x400.dds");
            i++;
          }
        }
      }

      GC.Collect();

      GomLib.GomObject colCollectionItemsProto =
        CurrentDom.GetObject("colCollectionItemsPrototype");
      Dictionary<Object, Object> colItems = null;
      if (colCollectionItemsProto != null) {
        colItems =
          colCollectionItemsProto.Data.ValueOrDefault<Dictionary<Object, Object>>(
            "colMtxItemIdToCollectionItem", null)
          ?? colCollectionItemsProto.Data.ValueOrDefault<Dictionary<Object, Object>>(
            "4611686297655094008", null)
          ?? colCollectionItemsProto.Data.ValueOrDefault<Dictionary<Object, Object>>(
            "colCollectionItemsData", null);
      }

      if (colItems != null) {
        foreach (var item in colItems) {
          GomLib.GomObjectData item2 = (GomLib.GomObjectData)item.Value;
          item2.Dictionary.TryGetValue("colItemImage", out Object icon_string);
          if (icon_string == null) item2.Dictionary.TryGetValue("4611686297655094004", out icon_string);
          if (icon_string == null) item2.Dictionary.TryGetValue("colCollectionIcon", out icon_string);

          if (icon_string != null) {
            String icon = icon_string.ToString().ToLower();

            file2.WriteLine("/resources/gfx/icons/" + icon + ".dds");
            file2.WriteLine("/resources/gfx/mtxstore/" + icon + "_120x120.dds");
            file2.WriteLine("/resources/gfx/mtxstore/" + icon + "_260x400.dds");
            file2.WriteLine("/resources/gfx/mtxstore/" + icon + "_260x260.dds");
            file2.WriteLine("/resources/gfx/mtxstore/" + icon + "_328x160.dds");
            file2.WriteLine("/resources/gfx/mtxstore/" + icon + "_400x400.dds");
            i++;
          }
        }
      }

      GC.Collect();

      GomLib.GomObject achCategoriesTable_Proto =
        CurrentDom.GetObject("achCategoriesTable_Prototype");
      Dictionary<Object, Object> achCategories =
        achCategoriesTable_Proto.Data.ValueOrDefault<Dictionary<Object, Object>>(
          "achCategoriesData",
          null
        );

      if (achCategories != null) {
        foreach (var item in achCategories) {
          GomLib.GomObjectData item2 = (GomLib.GomObjectData)item.Value;
          item2.Dictionary.TryGetValue("achCategoriesIcon", out Object icon_string1);

          if (icon_string1 != null) {
            String icon = icon_string1.ToString().ToLower();
            file2.WriteLine("/resources/gfx/icons/" + icon + ".dds");
          }

          item2.Dictionary.TryGetValue("achCategoriesCodexIcon", out Object icon_string2);

          if (icon_string2 != null) {
            String icon = icon_string2.ToString().ToLower();
            file2.WriteLine("/resources/gfx/codex/" + icon + ".dds");
          }
        }
      }

      GC.Collect();
      AddToList2("Icon Extract Completed");
      file2.Close();
      AddToList1("the icon lists has been generated there are " + i + " icons");
      MessageBox.Show("the icon lists has been generated there are " + i + " icons");
      EnableButtons();
    }
  }
}
