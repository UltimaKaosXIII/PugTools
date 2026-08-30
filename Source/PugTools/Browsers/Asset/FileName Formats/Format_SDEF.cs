using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PugTools {
  internal class Format_SDEF {
    private readonly String _dest;
    private readonly List<String> _errors;
    private readonly String _extension;
    private readonly HashSet<String> _fileNames;

    internal Int32 Found { get; set; }

    internal Format_SDEF(String dest, String ext) {
      _dest = dest;
      _errors = new List<string>();
      _extension = ext;
      _fileNames = new HashSet<string>();
    }

    internal void ParseSDEF(Stream fileStream) {
      if (fileStream == null) return;
      try {
        using BinaryReader br = new BinaryReader(fileStream, Encoding.UTF8, false);
        ViewHeroScriptLists.HeroScriptListInfo list = ViewHeroScriptLists.Parse(br);
        if (!String.Equals(list.Kind, "SDEF", StringComparison.Ordinal)) {
          _errors.Add("scriptdef.list: expected SDEF but parsed " + list.Kind + ".");
          return;
        }

        foreach (ViewHeroScriptLists.HeroScriptListEntry script in list.Scripts)
          _fileNames.Add("/resources/systemgenerated/compilednative/" + script.Id);
      }
      catch (Exception ex) {
        _errors.Add("scriptdef.list: " + ex.Message);
      }
    }

    internal void WriteFile() {
      if (!Directory.Exists(_dest + "\\File_Names"))
        Directory.CreateDirectory(_dest + "\\File_Names");

      Found = _fileNames.Count;

      if (_fileNames.Count > 0) {
        StreamWriter outputNames =
          new StreamWriter(_dest + "\\File_Names\\" + _extension + "_file_names.txt", false);

        foreach (String file in _fileNames) {
          outputNames.WriteLine(file.Replace("\\", "/"));
        }

        outputNames.Close();
        _fileNames.Clear();
      }

      if (_errors.Count > 0) {
        StreamWriter outputErrors =
          new StreamWriter(_dest + "\\File_Names\\" + _extension + "_error_list.txt", false);

        foreach (String error in _errors) {
          outputErrors.WriteLine(error);
        }

        outputErrors.Close();
        _errors.Clear();
      }
    }
  }
}
