using System;
using System.Collections.Generic;
using System.IO;
using System.Xml;

namespace PugTools {
  internal class Format_STB {
    private readonly String _dest;
    private readonly List<String> _errors;
    private readonly String _extension;

    internal HashSet<String> FileNames { get; set; }

    internal Format_STB(String dest, String ext) {
      _dest = dest;
      _errors = new List<String>();
      _extension = ext;
      FileNames = new HashSet<String>();
    }
    internal void ParseSTBManifest(Stream fileStream) {
      XmlDocument doc = new XmlDocument();
      doc.Load(fileStream);

      XmlNodeList fileList = doc.GetElementsByTagName("file");

      if (fileList.Count > 0) {
        foreach (XmlNode node in fileList) {
          XmlAttribute attr = node.Attributes["val"];

          if (attr != null) FileNames.Add(attr.Value);
        }
      }
    }
    internal void WriteFile(Boolean _ = false) {
      if (!Directory.Exists(_dest + "\\File_Names"))
        Directory.CreateDirectory(_dest + "\\File_Names");

      if (FileNames.Count > 0) {
        StreamWriter outputFileNames =
          new StreamWriter(_dest + "\\File_Names\\" + _extension + "_file_names.txt", false);

        foreach (String item in FileNames) {
          // en-us is only the canonical seed spelling here. TestHashFiles expands the locale
          // segment to en-us/de-de/fr-fr and persists only exact current-build hash hits.
          if (item != "")
            outputFileNames.WriteLine(
              ("/resources/en-us/" + item.Replace(".", "/") + ".stb").Replace("//", "/")
            );
        }

        outputFileNames.Close();
        FileNames.Clear();
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
