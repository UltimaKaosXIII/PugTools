using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TorArchive;
using FileFormats;

namespace PugTools {
  internal class Format_GR2 {
    private readonly String _dest;
    private readonly List<String> _errors;
    private readonly String _extension;

    internal HashSet<String> MatNames { get; set; }
    internal Dictionary<String, Archive> MeshNames { get; set; }

    internal Format_GR2(String dest, String ext) {
      _dest = dest;
      _errors = new List<String>();
      _extension = ext;
      MatNames = new HashSet<String>();
      MeshNames = new Dictionary<String, Archive>();
    }

    internal void ParseGR2(Stream fileStream, String fullFileName, Archive arch) {
      try {
        if (fileStream.CanSeek) fileStream.Position = 0;
        using BinaryReader br = new BinaryReader(fileStream, System.Text.Encoding.UTF8, leaveOpen: true);
        GR2 model = new GR2(br, fullFileName);
        try {
          foreach (GR2_Mesh mesh in model.meshes) {
            if (String.IsNullOrWhiteSpace(mesh.meshName)) continue;
            if (!MeshNames.ContainsKey(mesh.meshName)) MeshNames.Add(mesh.meshName, arch);
            MatNames.Add(mesh.meshName);
          }
          foreach (GR2_Material material in model.materials) {
            if (!String.IsNullOrWhiteSpace(material.materialName)) MatNames.Add(material.materialName);
          }
        } finally {
          model.Dispose();
        }
      } catch (Exception ex) {
        _errors.Add("File: " + fullFileName);
        _errors.Add(ex.Message);
      }
    }

    internal void WriteFile(Boolean _ = false) {
      if (!Directory.Exists(_dest + "\\File_Names"))
        Directory.CreateDirectory(_dest + "\\File_Names");

      if (MeshNames.Count > 0) {
        using StreamWriter outputMeshNames = new StreamWriter(_dest + "\\File_Names\\" + _extension + "_mesh_file_names.txt", false);
        foreach (KeyValuePair<String, Archive> file in MeshNames) {
          String output = "";
          if (file.Value.FileName.Contains("_dynamic_")) {
            if (file.Key.Contains('_')) {
              String type = file.Key.Split('_').First();
              output += "/resources/art/dynamic/" + type + "/model/" + file.Key + ".gr2\r\n";
              output += "/resources/art/dynamic/" + type + "/model/" + file.Key + ".lod.gr2\r\n";
              output += "/resources/art/dynamic/" + type + "/model/" + file.Key + ".clo\r\n";
            }
          } else {
            output += file.Key + ".gr2\r\n";
          }
          outputMeshNames.Write(output.Replace("//", "/"));
        }
        MeshNames.Clear();
      }

      if (MatNames.Count > 0) {
        using StreamWriter outputMatNames = new StreamWriter(_dest + "\\File_Names\\" + _extension + "_material_file_names.txt", false);
        foreach (String file in MatNames)
          outputMatNames.Write("/resources/art/shaders/materials/" + file + ".mat\r\n");
        MatNames.Clear();
      }

      if (_errors.Count > 0) {
        using StreamWriter outputErrors = new StreamWriter(_dest + "\\File_Names\\" + _extension + "_error_list.txt", false);
        foreach (String error in _errors) outputErrors.Write(error + "\r\n");
        _errors.Clear();
      }
    }
  }
}
