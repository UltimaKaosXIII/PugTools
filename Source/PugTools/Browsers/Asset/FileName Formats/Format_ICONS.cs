using System;
using System.Collections.Generic;
using System.IO;
using GomLib;

namespace PugTools {
  internal class Format_ICONS {
    private readonly String _dest;
    private readonly List<String> _errors;
    private readonly String _extension;

    internal HashSet<String> FileNames { get; set; }
    internal Int32 Searched { get; set; }

    internal Format_ICONS(String dest, String ext) {
      _dest = dest;
      _errors = new List<String>();
      _extension = ext;
      FileNames = new HashSet<String>();
    }
    internal void ParseICONS(DataObjectModel currentDom) {
      List<GomObject> itmList = currentDom.GetObjectsStartingWith("itm.");

      foreach (GomObject gomItm in itmList) {
        Searched++;
        String icon = gomItm.Data.ValueOrDefault<String>("itmIcon", null);

        if (icon != null) {
          FileNames.Add("/resources/gfx/icons/" + icon + ".dds");
          FileNames.Add("/resources/gfx/mtxstore/" + icon + "_120x120.dds");
          FileNames.Add("/resources/gfx/mtxstore/" + icon + "_260x400.dds");
          FileNames.Add("/resources/gfx/mtxstore/" + icon + "_260x260.dds");
          FileNames.Add("/resources/gfx/mtxstore/" + icon + "_328x160.dds");
          FileNames.Add("/resources/gfx/mtxstore/" + icon + "_400x400.dds");
        }

        gomItm.Unload();
      }

      itmList.Clear();

      List<GomObject> ablList = currentDom.GetObjectsStartingWith("abl.");

      foreach (GomObject gomItm in ablList) {
        Searched++;
        String icon = gomItm.Data.ValueOrDefault<String>("ablIconSpec", null);

        if (icon != null)
          FileNames.Add("/resources/gfx/icons/" + icon + ".dds");

        icon = gomItm.Data.ValueOrDefault<String>("effIcon", null);

        if (icon != null)
          FileNames.Add("/resources/gfx/icons/" + icon + ".dds");

        gomItm.Unload();
      }

      ablList.Clear();

      List<GomObject> qstList = currentDom.GetObjectsStartingWith("qst.");

      foreach (GomObject gomItm in qstList) {
        Searched++;
        String icon = gomItm.Data.ValueOrDefault<String>("qstMissionIcon", null);

        if (icon != null)
          FileNames.Add("/resources/gfx/codex/" + icon + ".dds");

        gomItm.Unload();
      }

      qstList.Clear();

      List<GomObject> ippList = currentDom.GetObjectsStartingWith("ipp.");

      foreach (GomObject gomItm in ippList) {
        Searched++;
        String icon = gomItm.Name.ToString();

        if (icon != null) {
          FileNames.Add("/resources/gfx/icons/" + icon + ".dds");
          FileNames.Add("/resources/gfx/icons/" + icon.Replace("ipp.", "") + ".dds");

          FileNames.Add("/resources/gfx/mtxstore/" + icon + "_120x120.dds");
          FileNames.Add("/resources/gfx/mtxstore/" + icon + "_260x260.dds");
          FileNames.Add("/resources/gfx/mtxstore/" + icon + "_260x400.dds");
          FileNames.Add("/resources/gfx/mtxstore/" + icon + "_328x160.dds");
          FileNames.Add("/resources/gfx/mtxstore/" + icon + "_400x400.dds");

          FileNames.Add("/resources/gfx/mtxstore/" + icon.Replace("ipp.", "") + "_120x120.dds");
          FileNames.Add("/resources/gfx/mtxstore/" + icon.Replace("ipp.", "") + "_260x260.dds");
          FileNames.Add("/resources/gfx/mtxstore/" + icon.Replace("ipp.", "") + "_260x400.dds");
          FileNames.Add("/resources/gfx/mtxstore/" + icon.Replace("ipp.", "") + "_328x160.dds");
          FileNames.Add("/resources/gfx/mtxstore/" + icon.Replace("ipp.", "") + "_400x400.dds");
        }

        gomItm.Unload();
      }

      ippList.Clear();

      List<GomObject> cdxList = currentDom.GetObjectsStartingWith("cdx.");

      foreach (GomObject gomItm in cdxList) {
        Searched++;
        String icon = gomItm.Data.ValueOrDefault<String>("cdxImage", null);

        if (icon != null)
          FileNames.Add("/resources/gfx/codex/" + icon + ".dds");

        gomItm.Unload();
      }
      cdxList.Clear();

      List<GomObject> achList = currentDom.GetObjectsStartingWith("ach.");

      foreach (GomObject gomItm in achList) {
        Searched++;
        String icon = gomItm.Data.ValueOrDefault<String>("achIcon", null);

        if (icon != null)
          FileNames.Add("/resources/gfx/icons/" + icon + ".dds");

        gomItm.Unload();
      }

      achList.Clear();

      List<GomObject> talList = currentDom.GetObjectsStartingWith("tal.");

      foreach (GomObject gomItm in talList) {
        Searched++;
        String icon = gomItm.Data.ValueOrDefault<String>("talTalentIcon", null);

        if (icon != null)
          FileNames.Add("/resources/gfx/icons/" + icon + ".dds");

        gomItm.Unload();
      }

      talList.Clear();

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
        List<GomObject> spvpList1 = currentDom.GetObjectsStartingWith(cmp);

        foreach (GomObject gomItm in spvpList1) {
          Searched++;
          String icon = gomItm.Data.ValueOrDefault<String>("scFFComponentIcon", null);

          if (icon != null)
            FileNames.Add("/resources/gfx/icons/" + icon + ".dds");

          gomItm.Unload();
        }

        spvpList1.Clear();
      }

      spvpIcons.Clear();

      GomObject shipDataProto = currentDom.GetObject("scFFShipsDataPrototype");

      if (shipDataProto != null) {
        Dictionary<Object, Object> shipData =
          shipDataProto.Data.ValueOrDefault<Dictionary<Object, Object>>("scFFShipsData", null);

        if (shipData != null) {
          foreach (KeyValuePair<Object, Object> item in shipData) {
            Searched++;

            GomObjectData item2 = (GomObjectData)item.Value;
            item2.Dictionary.TryGetValue("scFFShipHullIcon", out Object icon1_string);
            item2.Dictionary.TryGetValue("scFFShipIcon", out Object icon2_string);

            if (icon1_string != null) {
              FileNames.Add("/resources/gfx/icons/" + icon1_string + ".dds");
              FileNames.Add("/resources/gfx/textures/" + icon1_string + ".dds");
            }

            if (icon2_string != null) {
              FileNames.Add("/resources/gfx/icons/" + icon2_string + ".dds");
              FileNames.Add("/resources/gfx/textures/" + icon2_string + ".dds");
            }
          }

          shipData.Clear();
        }

        shipDataProto.Unload();
      }

      GomObject shipColorOptionProto = currentDom.GetObject("scFFColorOptionMasterPrototype");

      if (shipColorOptionProto != null) {
        Dictionary<Object, Object> shipColors =
          shipColorOptionProto.Data.ValueOrDefault<Dictionary<Object, Object>>(
            "scFFComponentColorUIData",
            null
          );

        if (shipColors != null) {
          foreach (KeyValuePair<Object, Object> item in shipColors) {
            Searched++;
            GomObjectData item2 = (GomObjectData)item.Value;
            item2.Dictionary.TryGetValue("scFFComponentColorIcon", out Object icon_string);

            if (icon_string != null) {
              FileNames.Add("/resources/gfx/icons/" + icon_string + ".dds");
              FileNames.Add("/resources/gfx/textures/" + icon_string + ".dds");
            }
          }

          shipColors.Clear();
        }

        shipColorOptionProto.Unload();
      }

      GomObject scffCrewProto = currentDom.GetObject("scffCrewPrototype");

      if (scffCrewProto != null) {
        Dictionary<Object, Object> shipCrew =
          scffCrewProto.Data.ValueOrDefault<Dictionary<Object, Object>>(
            "scFFShipsCrewAndPatternData",
            null
          );

        if (shipCrew != null) {
          foreach (KeyValuePair<Object, Object> item in shipCrew) {
            Searched++;
            GomObjectData item2 = (GomObjectData)item.Value;
            item2.Dictionary.TryGetValue("scFFCrewIcon", out Object icon_string);

            if (icon_string != null) {
              FileNames.Add("/resources/gfx/icons/" + icon_string + ".dds");
              FileNames.Add("/resources/gfx/textures/" + icon_string + ".dds");
            }
          }

          shipCrew.Clear();
        }

        scffCrewProto.Unload();
      }

      GomObject mtxStore = currentDom.GetObject("mtxStorefrontInfoPrototype");

      if (mtxStore != null) {
        Dictionary<Object, Object> mtxItems =
          mtxStore.Data.ValueOrDefault<Dictionary<Object, Object>>("mtxStorefrontItems", null)
          ?? mtxStore.Data.ValueOrDefault<Dictionary<Object, Object>>("mtxStorefrontData", null);

        if (mtxItems != null) {
          foreach (KeyValuePair<Object, Object> item in mtxItems) {
            Searched++;
            GomObjectData item2 = (GomObjectData)item.Value;
            if (!item2.Dictionary.TryGetValue("mtxStorefrontItemImage", out Object icon_string))
              item2.Dictionary.TryGetValue("mtxStorefrontIcon", out icon_string);

            if (icon_string != null) {
              String icon = icon_string.ToString().ToLower();
              FileNames.Add("/resources/gfx/icons/" + icon + ".dds");
              FileNames.Add("/resources/gfx/mtxstore/" + icon + "_120x120.dds");
              FileNames.Add("/resources/gfx/mtxstore/" + icon + "_260x400.dds");
              FileNames.Add("/resources/gfx/mtxstore/" + icon + "_260x260.dds");
              FileNames.Add("/resources/gfx/mtxstore/" + icon + "_328x160.dds");
              FileNames.Add("/resources/gfx/mtxstore/" + icon + "_400x400.dds");
            }
          }

          mtxItems.Clear();
        }

        mtxStore.Unload();
      }

      GomObject colCategoriesProto = currentDom.GetObject("colCollectionCategoriesPrototype");

      if (colCategoriesProto != null) {
        Dictionary<Object, Object> colCats =
          colCategoriesProto.Data.ValueOrDefault<Dictionary<Object, Object>>(
            "colCollectionCategoryData",
            null
          );

        if (colCats != null) {
          foreach (KeyValuePair<Object, Object> item in colCats) {
            Searched++;
            GomObjectData item2 = (GomObjectData)item.Value;
            item2.Dictionary.TryGetValue("colCollectionCategoryIcon", out Object icon_string);

            if (icon_string != null) {
              String icon = icon_string.ToString().ToLower();
              FileNames.Add("/resources/gfx/icons/" + icon + ".dds");
              FileNames.Add("/resources/gfx/mtxstore/" + icon + "_120x120.dds");
              FileNames.Add("/resources/gfx/mtxstore/" + icon + "_260x400.dds");
              FileNames.Add("/resources/gfx/mtxstore/" + icon + "_260x260.dds");
              FileNames.Add("/resources/gfx/mtxstore/" + icon + "_328x160.dds");
              FileNames.Add("/resources/gfx/mtxstore/" + icon + "_400x400.dds");
            }
          }

          colCats.Clear();
        }

        colCategoriesProto.Unload();
      }

      GomObject colCollectionItemsProto = currentDom.GetObject("colCollectionItemsPrototype");

      if (colCollectionItemsProto != null) {
        Dictionary<Object, Object> colItems =
          colCollectionItemsProto.Data.ValueOrDefault<Dictionary<Object, Object>>(
            "colMtxItemIdToCollectionItem", null)
          ?? colCollectionItemsProto.Data.ValueOrDefault<Dictionary<Object, Object>>(
            "4611686297655094008", null)
          ?? colCollectionItemsProto.Data.ValueOrDefault<Dictionary<Object, Object>>(
            "colCollectionItemsData", null);

        if (colItems != null) {
          foreach (KeyValuePair<Object, Object> item in colItems) {
            Searched++;
            GomObjectData item2 = (GomObjectData)item.Value;
            item2.Dictionary.TryGetValue("colItemImage", out Object icon_string);
            if (icon_string == null) item2.Dictionary.TryGetValue("4611686297655094004", out icon_string);
            if (icon_string == null) item2.Dictionary.TryGetValue("colCollectionIcon", out icon_string);

            if (icon_string != null) {
              String icon = icon_string.ToString().ToLower();
              FileNames.Add("/resources/gfx/icons/" + icon + ".dds");
              FileNames.Add("/resources/gfx/mtxstore/" + icon + "_120x120.dds");
              FileNames.Add("/resources/gfx/mtxstore/" + icon + "_260x400.dds");
              FileNames.Add("/resources/gfx/mtxstore/" + icon + "_260x260.dds");
              FileNames.Add("/resources/gfx/mtxstore/" + icon + "_328x160.dds");
              FileNames.Add("/resources/gfx/mtxstore/" + icon + "_400x400.dds");
            }
          }

          colItems.Clear();
        }

        colCollectionItemsProto.Unload();
      }

      GomObject achCategoriesTable_Proto = currentDom.GetObject("achCategoriesTable_Prototype");

      if (achCategoriesTable_Proto != null) {
        Dictionary<Object, Object> achCategories =
          achCategoriesTable_Proto.Data.ValueOrDefault<Dictionary<Object, Object>>(
            "achCategoriesData",
            null
          );

        if (achCategories != null) {
          foreach (KeyValuePair<Object, Object> item in achCategories) {
            Searched++;
            GomObjectData item2 = (GomObjectData)item.Value;
            item2.Dictionary.TryGetValue("achCategoriesIcon", out Object icon_string1);

            if (icon_string1 != null) {
              String icon = icon_string1.ToString().ToLower();
              FileNames.Add("/resources/gfx/icons/" + icon + ".dds");
            }

            item2.Dictionary.TryGetValue("achCategoriesCodexIcon", out Object icon_string2);

            if (icon_string2 != null) {
              String icon = icon_string2.ToString().ToLower();
              FileNames.Add("/resources/gfx/codex/" + icon + ".dds");
            }
          }

          achCategories.Clear();
        }

        achCategoriesTable_Proto.Unload();
      }
    }
    internal void WriteFile(Boolean _ = false) {
      if (!Directory.Exists(_dest + "\\File_Names"))
        Directory.CreateDirectory(_dest + "\\File_Names");

      if (FileNames.Count > 0) {
        StreamWriter outputAnimFileNames =
          new StreamWriter(_dest + "\\File_Names\\" + _extension + "_file_names.txt", false);

        foreach (String item in FileNames) {
          if (item != "")
            outputAnimFileNames.WriteLine(item);
        }

        outputAnimFileNames.Close();
      }

      if (_errors.Count > 0) {
        StreamWriter outputErrors =
          new StreamWriter(_dest + "\\File_Names\\" + _extension + "_error_list.txt", false);

        foreach (String error in _errors) {
          outputErrors.Write(error + "\r\n");
        }

        outputErrors.Close();
      }
    }
  }
}
