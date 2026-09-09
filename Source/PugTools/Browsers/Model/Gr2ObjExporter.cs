using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using FileFormats;
using SlimDX;

namespace PugTools {
  /// <summary>
  /// Jedipedia-style Wavefront OBJ export for the models currently assembled in Model Browser.
  /// Geometry uses the same GR2 mesh-piece ranges as the renderer. UV V is flipped to OBJ convention.
  /// </summary>
  internal static class Gr2ObjExporter {
    private sealed class ExportModel {
      internal GR2 Model;
      internal Matrix Transform;
      internal String Label;
    }

    internal static (Int32 Models, Int32 Meshes, Int32 Faces) Export(
      String objPath,
      IEnumerable<KeyValuePair<String, GR2>> roots) {
      if (String.IsNullOrWhiteSpace(objPath)) throw new ArgumentException("OBJ path is empty.", nameof(objPath));
      var exportModels = Flatten(roots).ToList();
      if (exportModels.Count == 0) throw new InvalidOperationException("No GR2 model geometry is loaded.");

      String directory = Path.GetDirectoryName(objPath) ?? String.Empty;
      if (directory.Length > 0) Directory.CreateDirectory(directory);
      String mtlPath = Path.ChangeExtension(objPath, ".mtl");
      String mtlName = Path.GetFileName(mtlPath);

      var materialNames = new Dictionary<GR2_Material, String>();
      var materialDefinitions = new Dictionary<String, GR2_Material>(StringComparer.OrdinalIgnoreCase);
      foreach (ExportModel entry in exportModels) {
        if (entry.Model?.materials == null) continue;
        for (Int32 i = 0; i < entry.Model.materials.Count; i++) {
          GR2_Material material = entry.Model.materials[i];
          if (material == null || materialNames.ContainsKey(material)) continue;
          String source = material.sourceMaterialName ?? material.materialName ?? ("material_" + i);
          String unique = UniqueName(SanitizeName(source), materialDefinitions.Keys);
          materialNames[material] = unique;
          materialDefinitions[unique] = material;
        }
      }

      Int32 vertexOffset = 0;
      Int32 texOffset = 0;
      Int32 meshCount = 0;
      Int32 faceCount = 0;
      using (var writer = new StreamWriter(objPath, false, new UTF8Encoding(false))) {
        writer.WriteLine("# Wavefront OBJ exported by PugTools from Star Wars: The Old Republic GR2 data");
        writer.WriteLine("# Coordinates are written in SWTOR display scale (x10), matching the in-game/world viewer scale.");
        if (materialDefinitions.Count > 0) writer.WriteLine("mtllib " + QuoteObjName(mtlName));
        writer.WriteLine();

        foreach (ExportModel entry in exportModels) {
          GR2 model = entry.Model;
          if (model?.meshes == null) continue;
          String modelLabel = SanitizeName(entry.Label ?? model.filename ?? "model");

          foreach (GR2_Mesh mesh in model.meshes) {
            if (mesh?.meshVerts == null || mesh.meshVertIndex == null || mesh.meshPieces == null || mesh.meshVerts.Count == 0)
              continue;
            if (!String.IsNullOrWhiteSpace(mesh.meshName)
                && mesh.meshName.IndexOf("collision", StringComparison.OrdinalIgnoreCase) >= 0)
              continue;

            Boolean hasTex = (mesh.bitFlag2 & 0x20u) != 0;
            String meshName = SanitizeName(modelLabel + "_" + (mesh.meshName ?? ("mesh_" + meshCount)));
            writer.WriteLine("# Model: " + (entry.Label ?? model.filename ?? "model"));
            writer.WriteLine("# Mesh: " + (mesh.meshName ?? String.Empty));
            writer.WriteLine("o " + QuoteObjName(meshName));

            foreach (GR2_Mesh_Vertex vertex in mesh.meshVerts) {
              Vector3 transformed = Vector3.TransformCoordinate(new Vector3(vertex.X, vertex.Y, vertex.Z), entry.Transform);
              writer.WriteLine(String.Format(CultureInfo.InvariantCulture, "v {0:R} {1:R} {2:R}", transformed.X * 10f, transformed.Y * 10f, transformed.Z * 10f));
            }
            if (hasTex) {
              foreach (GR2_Mesh_Vertex vertex in mesh.meshVerts)
                writer.WriteLine(String.Format(CultureInfo.InvariantCulture, "vt {0:R} {1:R}", vertex.texU, 1f - vertex.texV));
            }

            for (Int32 pieceIndex = 0; pieceIndex < mesh.meshPieces.Count; pieceIndex++) {
              GR2_Mesh_Piece piece = mesh.meshPieces[pieceIndex];
              if (piece == null) continue;
              writer.WriteLine("g " + QuoteObjName(meshName + "_submesh" + pieceIndex));

              String useMaterial = ResolveMaterialName(model, piece, pieceIndex, materialNames);
              if (!String.IsNullOrWhiteSpace(useMaterial)) writer.WriteLine("usemtl " + QuoteObjName(useMaterial));

              UInt64 firstIndex = (UInt64)piece.startIndex * 3UL;
              UInt64 indexCount = (UInt64)piece.numPieceFaces * 3UL;
              if (firstIndex >= (UInt64)mesh.meshVertIndex.Count) continue;
              UInt64 available = (UInt64)mesh.meshVertIndex.Count - firstIndex;
              indexCount = Math.Min(indexCount, available - (available % 3UL));

              for (UInt64 n = 0; n + 2 < indexCount; n += 3) {
                Int32 a = mesh.meshVertIndex[(Int32)(firstIndex + n)].index;
                Int32 b = mesh.meshVertIndex[(Int32)(firstIndex + n + 1)].index;
                Int32 c = mesh.meshVertIndex[(Int32)(firstIndex + n + 2)].index;
                if ((UInt32)a >= (UInt32)mesh.meshVerts.Count
                    || (UInt32)b >= (UInt32)mesh.meshVerts.Count
                    || (UInt32)c >= (UInt32)mesh.meshVerts.Count)
                  continue;

                Int32 va = vertexOffset + a + 1, vb = vertexOffset + b + 1, vc = vertexOffset + c + 1;
                if (hasTex) {
                  Int32 ta = texOffset + a + 1, tb = texOffset + b + 1, tc = texOffset + c + 1;
                  writer.WriteLine("f " + va + "/" + ta + " " + vb + "/" + tb + " " + vc + "/" + tc);
                } else {
                  writer.WriteLine("f " + va + " " + vb + " " + vc);
                }
                faceCount++;
              }
            }

            vertexOffset += mesh.meshVerts.Count;
            if (hasTex) texOffset += mesh.meshVerts.Count;
            meshCount++;
            writer.WriteLine();
          }
        }
      }

      WriteMtl(mtlPath, materialDefinitions);
      return (exportModels.Count, meshCount, faceCount);
    }

    private static IEnumerable<ExportModel> Flatten(IEnumerable<KeyValuePair<String, GR2>> roots) {
      var seen = new HashSet<GR2>();
      if (roots == null) yield break;
      foreach (KeyValuePair<String, GR2> pair in roots) {
        GR2 root = pair.Value;
        if (root == null || !seen.Add(root)) continue;
        Matrix transform;
        try { transform = root.GetTransform(); } catch { transform = Matrix.Identity; }
        foreach (ExportModel entry in FlattenModel(root, transform, pair.Key, seen, includeRoot: true)) yield return entry;
      }
    }

    private static IEnumerable<ExportModel> FlattenModel(GR2 model, Matrix transform, String label, HashSet<GR2> seen, Boolean includeRoot) {
      if (model == null) yield break;
      if (includeRoot) yield return new ExportModel { Model = model, Transform = transform, Label = label };
      if (model.attachedModels == null) yield break;
      Int32 index = 0;
      foreach (GR2 attached in model.attachedModels) {
        if (attached == null || !attached.enabled || !seen.Add(attached)) { index++; continue; }
        String childLabel = (label ?? "model") + "_attached_" + index + "_" + (attached.filename ?? "model");
        // View_NPC_GR2 renders attached meshes with the owning model's world transform.
        yield return new ExportModel { Model = attached, Transform = transform, Label = childLabel };
        foreach (ExportModel nested in FlattenModel(attached, transform, childLabel, seen, includeRoot: false)) yield return nested;
        index++;
      }
    }

    private static String ResolveMaterialName(GR2 model, GR2_Mesh_Piece piece, Int32 pieceIndex,
                                              Dictionary<GR2_Material, String> names) {
      if (model?.materials == null || model.materials.Count == 0) return null;
      Int32 materialIndex = piece.matId;
      if (materialIndex < 0) materialIndex = model.materials.Count == 2 && pieceIndex > 0 ? 1 : 0;
      if (materialIndex < 0 || materialIndex >= model.materials.Count) return null;
      GR2_Material material = model.materials[materialIndex];
      return material != null && names.TryGetValue(material, out String name) ? name : null;
    }

    private static void WriteMtl(String mtlPath, Dictionary<String, GR2_Material> definitions) {
      if (definitions.Count == 0) {
        if (System.IO.File.Exists(mtlPath)) try { System.IO.File.Delete(mtlPath); } catch { }
        return;
      }
      using var writer = new StreamWriter(mtlPath, false, new UTF8Encoding(false));
      writer.WriteLine("# PugTools material references. SWTOR textures remain resource paths and are kept as comments.");
      foreach (KeyValuePair<String, GR2_Material> pair in definitions.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)) {
        GR2_Material material = pair.Value;
        writer.WriteLine();
        writer.WriteLine("newmtl " + QuoteObjName(pair.Key));
        writer.WriteLine("Kd 1 1 1");
        writer.WriteLine("d 1");
        if (!String.IsNullOrWhiteSpace(material?.sourceMaterialName)) writer.WriteLine("# SWTOR MAT: " + material.sourceMaterialName);
        if (!String.IsNullOrWhiteSpace(material?.diffuseDDS)) writer.WriteLine("# Diffuse DDS: " + material.diffuseDDS);
        if (!String.IsNullOrWhiteSpace(material?.glossDDS)) writer.WriteLine("# Gloss DDS: " + material.glossDDS);
        if (!String.IsNullOrWhiteSpace(material?.paletteDDS)) writer.WriteLine("# Palette DDS: " + material.paletteDDS);
      }
    }

    private static String UniqueName(String desired, IEnumerable<String> existing) {
      var used = new HashSet<String>(existing ?? Enumerable.Empty<String>(), StringComparer.OrdinalIgnoreCase);
      String root = String.IsNullOrWhiteSpace(desired) ? "material" : desired;
      if (used.Add(root)) return root;
      for (Int32 i = 2; ; i++) if (used.Add(root + "_" + i)) return root + "_" + i;
    }

    private static String SanitizeName(String value) {
      String text = String.IsNullOrWhiteSpace(value) ? "unnamed" : value.Trim().Replace('\\', '_').Replace('/', '_');
      var sb = new StringBuilder(text.Length);
      foreach (Char c in text) sb.Append(Char.IsWhiteSpace(c) || Char.IsControl(c) ? '_' : c);
      return sb.ToString();
    }

    private static String QuoteObjName(String value) {
      // OBJ identifiers are most portable without spaces; SanitizeName already normalizes them.
      return SanitizeName(value);
    }
  }
}
