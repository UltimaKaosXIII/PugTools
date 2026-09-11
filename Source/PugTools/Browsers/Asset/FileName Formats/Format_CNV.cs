using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GomLib;

namespace PugTools {
  internal class Format_CNV {
    private readonly String _dest;
    private readonly List<String> _errors;
    private readonly String _extension;

    internal HashSet<String> AnimNames { get; set; }
    internal HashSet<String> FileNames { get; set; }
    internal HashSet<String> FxSpecNames { get; set; }

    internal Format_CNV(String dest, String ext) {
      _dest = dest;
      _errors = new List<String>();
      _extension = ext;
      AnimNames = new HashSet<String>();
      FileNames = new HashSet<String>();
      FxSpecNames = new HashSet<String>();
    }
    internal void ParseCNVNodes(List<GomObject> cnvNodes) {
      foreach (GomObject obj in cnvNodes) {
        String under = obj.Name.ToLower().ToString().Replace('.', '_');
        String slash = obj.Name.ToLower().ToString().Replace('.', '/');
        // Localized CNV resources use en-us only as a canonical seed. The central Filename Finder
        // validation pass expands de-de/fr-fr and accepts only hashes that exist in the build.
        String stb = "/resources/en-us/str/" + slash + ".stb";
        String acb = "/resources/en-us/bnk2/" + under + ".acb";
        String fxe = "/resources/en-us/fxe/" + slash + ".fxe";

        FileNames.Add(stb);
        FileNames.Add(acb);
        FileNames.Add(fxe);

        //Check for alien vo files.
        if (obj.Name.StartsWith("cnv.alien_vo")) {
          FileNames.Add("/resources/bnk2/" + under + ".acb");
        }

        if (obj.Data.Dictionary.ContainsKey("cnvActionList")) {
          List<Object> actionData = obj.Data.Get<List<Object>>("cnvActionList");

          if (actionData != null) {
            foreach (String action in actionData) {
              if (action.Contains("stg.")) continue;

              AnimNames.Add(action.Split('.').Last().ToLower());
            }
          }
        }

        if (obj.Data.Dictionary.ContainsKey("cnvActiveVFXList")) {
          Dictionary<Object, Object> vfxData =
            obj.Data.Get<Dictionary<Object, Object>>("cnvActiveVFXList");

          if (vfxData != null) {
            foreach (KeyValuePair<Object, Object> kvp in vfxData) {
              List<Object> value = (List<Object>)kvp.Value;

              if (value.Count > 0) {
                foreach (String vfx in value) {
                  FxSpecNames.Add(vfx.ToLower());
                }
              }
            }
          }
        }

        obj.Unload();
      }
    }
    internal void WriteFile() {
      if (!Directory.Exists(_dest + "\\File_Names"))
        Directory.CreateDirectory(_dest + "\\File_Names");

      if (FileNames.Count > 0) {
        StreamWriter outputNames =
          new StreamWriter(_dest + "\\File_Names\\" + _extension + "_file_names.txt", false);

        foreach (String file in FileNames) {
          outputNames.Write(file.Replace("\\", "/") + "\r\n");
        }

        outputNames.Close();
        FileNames.Clear();
      }

      if (AnimNames.Count > 0) {
        StreamWriter outputAnimNames =
          new StreamWriter(_dest + "\\File_Names\\" + _extension + "_anim_names.txt", false);

        foreach (String file in AnimNames) {
          outputAnimNames.Write(file.Replace("\\", "/") + "\r\n");
        }

        outputAnimNames.Close();
        AnimNames.Clear();
      }

      if (FxSpecNames.Count > 0) {
        StreamWriter outputfxSpecNames =
          new StreamWriter(_dest + "\\File_Names\\" + _extension + "_fxspec_names.txt", false);

        foreach (String file in FxSpecNames) {
          outputfxSpecNames.Write(file.Replace("\\", "/") + "\r\n");
        }

        outputfxSpecNames.Close();
        FxSpecNames.Clear();
      }

      if (_errors.Count > 0) {
        StreamWriter outputErrors =
          new StreamWriter(_dest + "\\File_Names\\" + _extension + "_error_list.txt", false);

        foreach (String error in _errors) {
          outputErrors.Write(error + "\r\n");
        }

        outputErrors.Close();
        _errors.Clear();
      }
    }
  }
}
