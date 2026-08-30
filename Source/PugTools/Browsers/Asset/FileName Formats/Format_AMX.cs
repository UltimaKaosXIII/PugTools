using System;
using System.Collections.Generic;
using System.IO;

namespace PugTools {
  class Format_AMX {
    private readonly String _dest;
    private readonly List<String> _errors;
    private readonly String _extension;

    internal HashSet<String> FileNames { get; set; }

    internal Format_AMX(String dest, String ext) {
      _dest = dest;
      _errors = new List<String>();
      _extension = ext;
      FileNames = new HashSet<String>(StringComparer.OrdinalIgnoreCase);
    }

    internal void ParseAMX(Stream fileStream, String fullFileName) {
      try {
        using BinaryReader br = new BinaryReader(fileStream);
        ViewAMX.AmxFileInfo amx = ViewAMX.Parse(br);
        String source = (fullFileName ?? String.Empty).Replace('\\', '/');
        Int32 slash = source.LastIndexOf('/');
        String sourceDir = slash >= 0 ? source.Substring(0, slash + 1) : String.Empty;

        foreach (ViewAMX.AmxAnimationEntry entry in amx.Animations) {
          if (String.IsNullOrWhiteSpace(entry.Animation)) continue;
          String body = (entry.BodyType ?? String.Empty).Replace('\\', '/').Trim('/');

          // AMX records reference JBA clips. The old filename finder also
          // invented <animation>.mph and <animation>.mph.amx paths, but those
          // are not referenced by this format and polluted the hash candidate
          // list. Match the actual AnimShare semantics instead.
          if (!String.IsNullOrEmpty(body))
            FileNames.Add(("/resources/anim/" + body + "/" + entry.Animation + ".jba").Replace("//", "/"));

          // Some AMX sidecars use an empty/noncanonical bodyType and rely on
          // the network folder. Jedipedia probes the sibling clip as a second
          // candidate; keep it as a conservative fallback.
          if (!String.IsNullOrEmpty(sourceDir))
            FileNames.Add(sourceDir + entry.Animation + ".jba");
        }
      }
      catch (Exception ex) {
        _errors.Add("File: " + fullFileName);
        _errors.Add(ex.GetType().Name + ": " + ex.Message);
      }
    }

    internal void WriteFile(Boolean _ = false) {
      if (!Directory.Exists(_dest + "\\File_Names"))
        Directory.CreateDirectory(_dest + "\\File_Names");

      if (FileNames.Count > 0) {
        using StreamWriter outputFileNames =
          new StreamWriter(_dest + "\\File_Names\\" + _extension + "_file_names.txt", false);
        foreach (String file in FileNames) outputFileNames.WriteLine(file);
        FileNames.Clear();
      }

      if (_errors.Count > 0) {
        using StreamWriter outputErrors =
          new StreamWriter(_dest + "\\File_Names\\" + _extension + "_error_list.txt", false);
        foreach (String error in _errors) outputErrors.Write(error + "\r\n");
        _errors.Clear();
      }
    }
  }
}
