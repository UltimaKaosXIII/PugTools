using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using FileFormats;
using SlimDX;

namespace PugTools {
  /// <summary>
  /// Lightweight, native legacy-SpeedTree geometry generator for the world viewer.
  ///
  /// SWTOR's *.spt files store procedural tree parameters rather than a ready-to-draw
  /// vertex buffer.  The original game used SpeedTreeRT to expand those parameters at
  /// runtime.  PugTools cannot ship that proprietary runtime, so this class reconstructs
  /// a deterministic static representation from the authored branch splines, leaf maps
  /// and frond settings that ViewSPT already decodes.
  ///
  /// It is deliberately conservative: geometry is capped so one malformed tree cannot
  /// monopolise a streaming worker, and the world streamer falls back to a same-stem GR2
  /// whenever native generation fails.
  /// </summary>
  internal static class SpeedTreeProceduralModel {
    private const Int32 MaxStems = 900;
    private const Int32 MaxLeaves = 7200;
    private const Int32 MaxVerticesPerMesh = 60000;
    private const Single Epsilon = 0.00001f;

    private sealed class BranchLevel {
      internal ViewSPT.BezierInfo Disturbance;
      internal ViewSPT.BezierInfo Gravity;
      internal ViewSPT.BezierInfo Length;
      internal ViewSPT.BezierInfo Radius;
      internal ViewSPT.BezierInfo RadiusScale;
      internal ViewSPT.BezierInfo StartAngle;
      internal ViewSPT.BezierInfo AngleProfile;
      internal Int32 CrossSegments = 6;
      internal Int32 LengthSegments = 5;
      internal Single First = 0.12f;
      internal Single Last = 0.96f;
      internal Single Frequency = 1f;
      internal Single STile = 1f;
      internal Single TTile = 1f;
    }

    private sealed class LeafMap {
      internal String Texture;
      internal Vector2 Size = new Vector2(0.18f, 0.22f);
      internal Vector2 Origin = new Vector2(0.5f, 0.5f);
      internal Boolean Blossom;
    }

    private sealed class FrondSettings {
      internal Boolean Enabled;
      internal Int32 Level;
      internal Int32 Blades = 2;
      internal Single Aspect = 2f;
      internal Single SizeScale = 1f;
      internal String Texture;
    }

    private sealed class Stem {
      internal Int32 Level;
      internal Single StoredLength;
      internal Single Length;
      internal Single BaseRadius;
      internal readonly List<Vector3> Points = new List<Vector3>();
      internal readonly List<Vector3> Tangents = new List<Vector3>();
    }

    private sealed class MeshBuilder {
      internal readonly String Name;
      internal readonly Int32 MaterialIndex;
      internal readonly List<GR2_Mesh_Vertex> Vertices = new List<GR2_Mesh_Vertex>();
      internal readonly List<UInt16> Indices = new List<UInt16>();

      internal MeshBuilder(String name, Int32 materialIndex) { Name = name; MaterialIndex = materialIndex; }
      internal Boolean CanAdd(Int32 vertices) => vertices >= 0 && Vertices.Count + vertices < MaxVerticesPerMesh && Vertices.Count + vertices < UInt16.MaxValue;

      internal UInt16 AddVertex(Vector3 position, Vector3 normal, Vector3 tangent, Single u, Single v) {
        Vector3 n = SafeNormalize(normal, Vector3.UnitY);
        Vector3 t = SafeNormalize(tangent, Vector3.UnitX);
        UInt16 index = checked((UInt16)Vertices.Count);
        Vertices.Add(new GR2_Mesh_Vertex {
          X = position.X, Y = position.Y, Z = position.Z,
          normX = n.X, normY = n.Y, normZ = n.Z, normW = 1f,
          tanX = t.X, tanY = t.Y, tanZ = t.Z, tanW = 1f,
          texU = u, texV = v
        });
        return index;
      }

      internal void Triangle(UInt16 a, UInt16 b, UInt16 c) { Indices.Add(a); Indices.Add(b); Indices.Add(c); }
      internal Boolean HasGeometry => Vertices.Count >= 3 && Indices.Count >= 3;
    }

    internal static GR2 Build(ViewSPT.SptInfo info, String sourcePath) {
      if (info == null) throw new ArgumentNullException(nameof(info));
      List<BranchLevel> levels = ReadBranchLevels(info);
      if (levels.Count == 0) throw new InvalidOperationException("SPT has no procedural branch levels.");

      Single treeSize = Math.Abs(FirstSingle(info, 2006u, 1f));
      if (!Finite(treeSize) || treeSize < 0.01f) treeSize = 1f;
      treeSize = Clamp(treeSize, 0.05f, 500f);
      UInt32 seed = FirstUInt(info, 2005u, StableHash(sourcePath));
      Random random = new Random(unchecked((Int32)(seed == 0 ? 1u : seed)));

      String branchTexture = ResolveTexture(FirstString(info, 2000u), sourcePath);
      if (String.IsNullOrWhiteSpace(branchTexture)) branchTexture = FirstMapCollectionTexture(info, 60002u, sourcePath);
      List<LeafMap> leafMaps = ReadLeafMaps(info, sourcePath);
      FrondSettings frond = ReadFrond(info, sourcePath);

      GR2 model = new GR2 {
        filename = (sourcePath ?? "speedtree") + " [native SPT]",
        lodSchemaName = "granny_legacy_default",
        lodThresholds = Array.Empty<Single>(),
        transformMatrix = Matrix.Identity,
        attachMatrix = Matrix.Identity,
        positionMatrix = Matrix.Identity,
        rotationMatrix = Matrix.Identity,
        scaleMatrix = Matrix.Identity,
        parentPosMatrix = Matrix.Identity,
        parentRotMatrix = Matrix.Identity
      };

      Int32 branchMaterial = AddMaterial(model, sourcePath, "branch", branchTexture, false, new Vector4(0.38f, 0.28f, 0.16f, 1f));
      var leafMaterialByPath = new Dictionary<String, Int32>(StringComparer.OrdinalIgnoreCase);
      foreach (LeafMap leaf in leafMaps) {
        String key = leaf.Texture ?? String.Empty;
        if (!leafMaterialByPath.ContainsKey(key))
          leafMaterialByPath[key] = AddMaterial(model, sourcePath, "leaf" + leafMaterialByPath.Count.ToString(CultureInfo.InvariantCulture), leaf.Texture, true, new Vector4(0.28f, 0.55f, 0.20f, 1f));
      }
      Int32 frondMaterial = -1;
      if (frond.Enabled) frondMaterial = AddMaterial(model, sourcePath, "frond", frond.Texture, true, new Vector4(0.28f, 0.52f, 0.18f, 1f));

      MeshBuilder branches = new MeshBuilder("speedtree_branches", branchMaterial);
      var leafBuilders = new Dictionary<Int32, MeshBuilder>();
      foreach (Int32 materialIndex in leafMaterialByPath.Values.Distinct()) leafBuilders[materialIndex] = new MeshBuilder("speedtree_leaves_" + materialIndex, materialIndex);
      MeshBuilder fronds = frondMaterial >= 0 ? new MeshBuilder("speedtree_fronds", frondMaterial) : null;

      Boolean hasLeaves = leafMaps.Count > 0;
      Int32 branchLevelCount = hasLeaves && levels.Count > 1 ? levels.Count - 1 : levels.Count;
      branchLevelCount = Math.Max(1, branchLevelCount);
      var stems = new List<Stem>();
      Int32 stemCount = 0;
      Int32 leafCount = 0;

      GenerateStem(
        levels, branchLevelCount, 0, 0.5f,
        Vector3.Zero, Vector3.UnitY,
        treeSize, random, branches, stems,
        leafMaps, leafMaterialByPath, leafBuilders,
        ref stemCount, ref leafCount
      );

      if (fronds != null) BuildFronds(frond, stems, treeSize, random, fronds);

      // The SPT footer contains the bounds produced by the original SpeedTree runtime.  Use its
      // dimensions to correct unit-system differences in the procedural approximation without
      // translating the authored placement origin away from the tree base.
      Single nativeScale = ComputeFooterScale(info, branches, leafBuilders.Values, fronds);
      if (Finite(nativeScale) && nativeScale > 0f && Math.Abs(nativeScale - 1f) > 0.0001f)
        ScaleGeometry(nativeScale, branches, leafBuilders.Values, fronds);

      AddMesh(model, branches);
      foreach (MeshBuilder leaf in leafBuilders.Values) AddMesh(model, leaf);
      AddMesh(model, fronds);
      if (model.meshes.Count == 0) throw new InvalidOperationException("SPT procedural generation produced no drawable geometry.");

      model.numMeshes = checked((UInt16)Math.Min(UInt16.MaxValue, model.meshes.Count));
      model.numMaterials = checked((UInt16)Math.Min(UInt16.MaxValue, model.materials.Count));
      model.globalBox = ComputeBounds(model);
      return model;
    }

    private static void GenerateStem(
      List<BranchLevel> levels,
      Int32 branchLevelCount,
      Int32 levelIndex,
      Single xRelative,
      Vector3 origin,
      Vector3 initialDirection,
      Single treeSize,
      Random random,
      MeshBuilder branches,
      List<Stem> stems,
      List<LeafMap> leafMaps,
      Dictionary<String, Int32> leafMaterialByPath,
      Dictionary<Int32, MeshBuilder> leafBuilders,
      ref Int32 stemCount,
      ref Int32 leafCount
    ) {
      if (stemCount >= MaxStems || levelIndex < 0 || levelIndex >= branchLevelCount || levelIndex >= levels.Count) return;
      BranchLevel level = levels[levelIndex];
      Single defaultStoredLength = levelIndex == 0 ? 1f : Math.Max(0.20f, 0.66f - levelIndex * 0.08f);
      Single storedLength = PositiveSpline(level.Length, xRelative, defaultStoredLength, random, 0.025f);
      Single length = Clamp(storedLength * treeSize, treeSize * 0.025f, treeSize * (levelIndex == 0 ? 4f : 2f));
      Single defaultRadius = levelIndex == 0 ? 0.045f : 0.022f;
      Single storedRadius = PositiveSpline(level.Radius, xRelative, defaultRadius, random, 0.01f);
      Single baseRadius = Clamp(storedRadius * treeSize, Math.Max(0.0025f, treeSize * 0.0015f), Math.Max(treeSize * 0.45f, length * 0.35f));

      Stem stem = new Stem { Level = levelIndex, StoredLength = storedLength, Length = length, BaseRadius = baseRadius };
      stemCount++;
      BuildCenterLine(stem, level, origin, initialDirection, random);
      stems.Add(stem);
      AppendTube(branches, stem, level);

      if (levelIndex + 1 < branchLevelCount) {
        Single first = Clamp01(level.First);
        Single last = Clamp01(level.Last);
        if (last < first) { Single swap = first; first = last; last = swap; }
        if (last - first < 0.02f) last = Math.Min(1f, first + 0.02f);
        Single frequency = Math.Abs(level.Frequency);
        Int32 children = (Int32)Math.Round(frequency * Math.Max(0.6f, storedLength));
        Int32 cap = levelIndex == 0 ? 42 : (levelIndex == 1 ? 18 : 10);
        children = Math.Max(frequency > 0.05f ? 1 : 0, Math.Min(cap, children));
        BranchLevel childLevel = levels[levelIndex + 1];
        Single golden = 2.39996323f;
        for (Int32 i = 0; i < children && stemCount < MaxStems; i++) {
          Single d = children == 1 ? 0.5f : i / (Single)(children - 1);
          Single jitter = (Single)(random.NextDouble() - 0.5) * Math.Min(0.08f, (last - first) / Math.Max(2, children));
          Single t = Clamp(first + (last - first) * d + jitter, first, last);
          Single childX = (t - first) / Math.Max(Epsilon, last - first);
          Vector3 p = PointAt(stem, t);
          Vector3 tangent = TangentAt(stem, t);
          Single authoredAngle = Evaluate(childLevel.StartAngle, childX, levelIndex == 0 ? 42f : 50f);
          Single angle = ToRadians(authoredAngle);
          if (Math.Abs(angle) < 0.08f) angle = ToRadians(levelIndex == 0 ? 42f : 50f);
          Single profile = Evaluate(childLevel.AngleProfile, childX, 1f);
          if (Finite(profile) && Math.Abs(profile) > 0.01f && Math.Abs(profile) < 3f) angle *= Math.Abs(profile);
          Single azimuth = golden * i + (Single)random.NextDouble() * 1.5f;
          Vector3 radial = PerpendicularAround(tangent, azimuth);
          Vector3 childDirection = SafeNormalize(tangent * (Single)Math.Cos(angle) + radial * (Single)Math.Sin(angle), tangent);
          GenerateStem(levels, branchLevelCount, levelIndex + 1, childX, p, childDirection, treeSize, random, branches, stems,
            leafMaps, leafMaterialByPath, leafBuilders, ref stemCount, ref leafCount);
        }
      } else if (leafMaps.Count > 0 && leafCount < MaxLeaves) {
        AppendLeaves(stem, level, treeSize, random, leafMaps, leafMaterialByPath, leafBuilders, ref leafCount);
      }
    }

    private static void BuildCenterLine(Stem stem, BranchLevel level, Vector3 origin, Vector3 initialDirection, Random random) {
      Int32 segments = Math.Max(2, Math.Min(14, level.LengthSegments <= 0 ? 5 : level.LengthSegments));
      Vector3 direction = SafeNormalize(initialDirection, Vector3.UnitY);
      Vector3 position = origin;
      stem.Points.Add(position);
      stem.Tangents.Add(direction);
      Single segmentLength = stem.Length / segments;
      Single phase = (Single)random.NextDouble() * 6.2831853f;
      for (Int32 i = 1; i <= segments; i++) {
        Single t = i / (Single)segments;
        Single disturbance = Evaluate(level.Disturbance, t, 0f);
        Single gravity = Evaluate(level.Gravity, t, 0f);
        Single disturbanceScale = Clamp(Math.Abs(disturbance), 0f, 100f) * 0.0025f;
        Single gravityScale = Clamp(gravity, -100f, 100f) * 0.0018f;
        Vector3 side = PerpendicularAround(direction, phase + t * 8.13f);
        direction = SafeNormalize(direction + side * disturbanceScale - Vector3.UnitY * gravityScale, direction);
        position += direction * segmentLength;
        stem.Points.Add(position);
        stem.Tangents.Add(direction);
      }
    }

    private static void AppendTube(MeshBuilder mesh, Stem stem, BranchLevel level) {
      if (mesh == null || stem == null || stem.Points.Count < 2) return;
      Int32 sides = Math.Max(4, Math.Min(10, level.CrossSegments <= 0 ? 6 : level.CrossSegments));
      Int32 rings = stem.Points.Count;
      Int32 needed = rings * sides;
      if (!mesh.CanAdd(needed)) return;
      UInt16[,] ring = new UInt16[rings, sides];
      for (Int32 i = 0; i < rings; i++) {
        Single t = i / (Single)(rings - 1);
        Vector3 tangent = TangentAt(stem, t);
        Vector3 axisA = MakePerpendicular(tangent);
        Vector3 axisB = SafeNormalize(Vector3.Cross(tangent, axisA), Vector3.UnitZ);
        Single profile = Evaluate(level.RadiusScale, t, 1f);
        if (!Finite(profile) || profile <= 0.01f || profile > 4f) profile = 1f;
        Single taper = Math.Max(0.035f, 1f - 0.88f * t);
        Single radius = stem.BaseRadius * taper * Clamp(profile, 0.08f, 2.5f);
        for (Int32 side = 0; side < sides; side++) {
          Single angle = side / (Single)sides * 6.2831853f;
          Vector3 normal = axisA * (Single)Math.Cos(angle) + axisB * (Single)Math.Sin(angle);
          Vector3 p = stem.Points[i] + normal * radius;
          Single u = side / (Single)sides * Math.Max(0.01f, level.STile);
          Single v = t * Math.Max(0.01f, level.TTile);
          ring[i, side] = mesh.AddVertex(p, normal, tangent, u, v);
        }
      }
      for (Int32 i = 0; i < rings - 1; i++) {
        for (Int32 side = 0; side < sides; side++) {
          Int32 next = (side + 1) % sides;
          UInt16 a = ring[i, side], b = ring[i + 1, side], c = ring[i + 1, next], d = ring[i, next];
          mesh.Triangle(a, b, c); mesh.Triangle(a, c, d);
        }
      }
    }

    private static void AppendLeaves(
      Stem stem,
      BranchLevel level,
      Single treeSize,
      Random random,
      List<LeafMap> leafMaps,
      Dictionary<String, Int32> materialByPath,
      Dictionary<Int32, MeshBuilder> builders,
      ref Int32 leafCount
    ) {
      Single first = Clamp01(level.First), last = Clamp01(level.Last);
      if (last < first) { Single swap = first; first = last; last = swap; }
      Single frequency = Math.Abs(level.Frequency);
      Int32 count = (Int32)Math.Round(frequency * Math.Max(1f, stem.StoredLength));
      count = Math.Max(2, Math.Min(28, count));
      for (Int32 i = 0; i < count && leafCount < MaxLeaves; i++) {
        Single t = count == 1 ? 0.75f : first + (last - first) * (i / (Single)(count - 1));
        t = Clamp01(t + (Single)(random.NextDouble() - 0.5) * 0.035f);
        LeafMap leaf = leafMaps[random.Next(leafMaps.Count)];
        Int32 materialIndex = materialByPath[leaf.Texture ?? String.Empty];
        MeshBuilder mesh = builders[materialIndex];
        if (!mesh.CanAdd(8)) continue;
        Vector3 center = PointAt(stem, t);
        Vector3 tangent = TangentAt(stem, t);
        Single width = Math.Abs(leaf.Size.X) * treeSize;
        Single height = Math.Abs(leaf.Size.Y) * treeSize;
        if (!Finite(width) || width < treeSize * 0.006f) width = treeSize * 0.11f;
        if (!Finite(height) || height < treeSize * 0.006f) height = treeSize * 0.14f;
        width = Clamp(width, treeSize * 0.012f, treeSize * 0.75f);
        height = Clamp(height, treeSize * 0.012f, treeSize * 0.85f);
        Single azimuth = (Single)random.NextDouble() * 6.2831853f;
        Vector3 right = PerpendicularAround(tangent, azimuth);
        Vector3 up = SafeNormalize(tangent * 0.35f + Vector3.UnitY * 0.65f, Vector3.UnitY);
        if (Math.Abs(Vector3.Dot(right, up)) > 0.92f) up = SafeNormalize(Vector3.Cross(right, tangent), Vector3.UnitY);
        center += up * height * (0.5f - Clamp01(leaf.Origin.Y));
        AddLeafQuad(mesh, center, right, up, width, height);
        Vector3 right2 = SafeNormalize(Vector3.Cross(up, right), MakePerpendicular(up));
        AddLeafQuad(mesh, center, right2, up, width, height);
        leafCount++;
      }
    }

    private static void AddLeafQuad(MeshBuilder mesh, Vector3 center, Vector3 right, Vector3 up, Single width, Single height) {
      Vector3 r = SafeNormalize(right, Vector3.UnitX) * (width * 0.5f);
      Vector3 u = SafeNormalize(up, Vector3.UnitY) * (height * 0.5f);
      Vector3 normal = SafeNormalize(Vector3.Cross(r, u), Vector3.UnitZ);
      UInt16 a = mesh.AddVertex(center - r - u, normal, right, 0f, 1f);
      UInt16 b = mesh.AddVertex(center - r + u, normal, right, 0f, 0f);
      UInt16 c = mesh.AddVertex(center + r + u, normal, right, 1f, 0f);
      UInt16 d = mesh.AddVertex(center + r - u, normal, right, 1f, 1f);
      mesh.Triangle(a, b, c); mesh.Triangle(a, c, d);
      // The world effect does not switch rasterizer culling from GR2_Material.isTwoSided,
      // so explicitly duplicate the back-facing winding for foliage cards.
      mesh.Triangle(c, b, a); mesh.Triangle(d, c, a);
    }

    private static void BuildFronds(FrondSettings settings, List<Stem> stems, Single treeSize, Random random, MeshBuilder mesh) {
      if (settings == null || !settings.Enabled || mesh == null || stems == null) return;
      List<Stem> candidates = stems.Where(s => s != null && s.Level == Math.Max(0, settings.Level)).Take(300).ToList();
      if (candidates.Count == 0 && stems.Count > 0) candidates = stems.Where(s => s != null && s.Level == stems.Max(x => x.Level)).Take(300).ToList();
      Int32 bladeCount = Math.Max(1, Math.Min(8, settings.Blades));
      foreach (Stem stem in candidates) {
        if (!mesh.CanAdd(bladeCount * 4)) break;
        Vector3 root = PointAt(stem, 0.62f);
        Vector3 tangent = TangentAt(stem, 0.75f);
        Single length = Clamp(stem.Length * 0.65f * Clamp(settings.SizeScale, 0.15f, 4f), treeSize * 0.06f, treeSize * 1.8f);
        Single width = length / Clamp(settings.Aspect, 0.35f, 8f);
        for (Int32 i = 0; i < bladeCount; i++) {
          Single azimuth = i / (Single)bladeCount * 6.2831853f + (Single)random.NextDouble() * 0.3f;
          Vector3 radial = PerpendicularAround(tangent, azimuth);
          Vector3 forward = SafeNormalize(tangent * 0.42f + radial * 0.91f, radial);
          Vector3 center = root + forward * (length * 0.45f);
          Vector3 right = SafeNormalize(Vector3.Cross(forward, Vector3.UnitY), radial);
          if (right.LengthSquared() < Epsilon) right = radial;
          Vector3 up = forward;
          AddLeafQuad(mesh, center, right, up, width, length);
        }
      }
    }

    private static List<BranchLevel> ReadBranchLevels(ViewSPT.SptInfo info) {
      var levels = new List<BranchLevel>();
      BranchLevel current = null;
      foreach (ViewSPT.SptRecord record in info.Records) {
        if (record.Token == 1016u) { current = new BranchLevel(); levels.Add(current); continue; }
        if (record.Token == 1017u) { current = null; continue; }
        if (current == null) continue;
        switch (record.Token) {
          case 6000u: current.Disturbance = record.Value as ViewSPT.BezierInfo; break;
          case 6001u: current.Gravity = record.Value as ViewSPT.BezierInfo; break;
          case 6004u: current.Length = record.Value as ViewSPT.BezierInfo; break;
          case 6005u: current.Radius = record.Value as ViewSPT.BezierInfo; break;
          case 6006u: current.RadiusScale = record.Value as ViewSPT.BezierInfo; break;
          case 6007u: current.StartAngle = record.Value as ViewSPT.BezierInfo; break;
          case 6008u: if (record.Value is Int32 cross) current.CrossSegments = cross; break;
          case 6009u: if (record.Value is Int32 seg) current.LengthSegments = seg; break;
          case 6010u: if (record.Value is Single first) current.First = first; break;
          case 6011u: if (record.Value is Single last) current.Last = last; break;
          case 6012u: if (record.Value is Single freq) current.Frequency = freq; break;
          case 6013u: if (record.Value is Single s) current.STile = s; break;
          case 6014u: if (record.Value is Single t) current.TTile = t; break;
          case 6017u: current.AngleProfile = record.Value as ViewSPT.BezierInfo; break;
        }
      }
      return levels;
    }

    private static List<LeafMap> ReadLeafMaps(ViewSPT.SptInfo info, String sourcePath) {
      var maps = new List<LeafMap>();
      LeafMap current = null;
      foreach (ViewSPT.SptRecord record in info.Records) {
        if (record.Token == 1009u) { current = new LeafMap(); maps.Add(current); continue; }
        if (record.Token == 1010u) { current = null; continue; }
        if (current == null) continue;
        if (record.Token == 4000u && record.Value is Boolean blossom) current.Blossom = blossom;
        else if (record.Token == 4003u && record.Value is String texture) current.Texture = ResolveTexture(texture, sourcePath);
        else if (record.Token == 4004u && record.Value is Single[] origin && origin.Length >= 2) current.Origin = new Vector2(origin[0], origin[1]);
        else if (record.Token == 4005u && record.Value is Single[] size && size.Length >= 2) current.Size = new Vector2(size[0], size[1]);
      }
      maps.RemoveAll(x => x == null);

      List<String> bankLeaves = MapCollectionTextures(info, 60003u, sourcePath);
      if (maps.Count == 0) {
        foreach (String texture in bankLeaves) maps.Add(new LeafMap { Texture = texture });
      } else {
        for (Int32 i = 0; i < maps.Count; i++) if (String.IsNullOrWhiteSpace(maps[i].Texture) && i < bankLeaves.Count) maps[i].Texture = bankLeaves[i];
      }
      return maps;
    }

    private static FrondSettings ReadFrond(ViewSPT.SptInfo info, String sourcePath) {
      FrondSettings f = new FrondSettings();
      foreach (ViewSPT.SptRecord record in info.Records) {
        switch (record.Token) {
          case 13002u: if (record.Value is Int32 level) f.Level = Math.Max(0, level); break;
          case 13004u: if (record.Value is Int32 blades) f.Blades = blades; break;
          case 13007u: if (record.Value is Boolean enabled) f.Enabled = enabled; break;
          case 14002u: if (String.IsNullOrWhiteSpace(f.Texture) && record.Value is String texture) f.Texture = ResolveTexture(texture, sourcePath); break;
          case 14003u: if (record.Value is Single aspect) f.Aspect = Math.Abs(aspect); break;
          case 14004u: if (record.Value is Single scale) f.SizeScale = Math.Abs(scale); break;
        }
      }
      if (String.IsNullOrWhiteSpace(f.Texture)) f.Texture = FirstMapCollectionTexture(info, 60004u, sourcePath);
      if (String.IsNullOrWhiteSpace(f.Texture)) f.Enabled = false;
      return f;
    }

    private static Int32 AddMaterial(GR2 model, String sourcePath, String role, String texture, Boolean alphaTest, Vector4 flatColor) {
      String name = "__spt_" + role + "_" + StableHash(sourcePath + "|" + role + "|" + (texture ?? String.Empty)).ToString("X8", CultureInfo.InvariantCulture);
      GR2_Material material = new GR2_Material(name) {
        parsed = true,
        runtimeGenerated = true,
        diffuseDDS = texture,
        alphaMode = alphaTest ? "Test" : "None",
        alphaTestValue = alphaTest ? 0.30f : 0f,
        alphaClip = alphaTest,
        isTwoSided = alphaTest,
        derived = "Uber",
        diffuseFlatColor = flatColor,
        hasDiffuseFlatColor = true
      };
      model.materials.Add(material);
      return model.materials.Count - 1;
    }

    private static void AddMesh(GR2 model, MeshBuilder builder) {
      if (model == null || builder == null || !builder.HasGeometry) return;
      GR2_Mesh mesh = new GR2_Mesh {
        parent = model,
        lod = 0,
        bitFlag2 = 0x23,
        vertexSize = 32,
        meshName = builder.Name
      };
      mesh.meshVerts.AddRange(builder.Vertices);
      foreach (UInt16 index in builder.Indices) mesh.meshVertIndex.Add(new GR2_Mesh_Vertex_Index(index));
      mesh.meshPieces.Add(new GR2_Mesh_Piece {
        matId = builder.MaterialIndex,
        startIndex = 0,
        numPieceFaces = checked((UInt32)(builder.Indices.Count / 3))
      });
      mesh.numVerts = checked((UInt32)mesh.meshVerts.Count);
      mesh.numVertIndex = checked((UInt32)mesh.meshVertIndex.Count);
      mesh.numPieces = 1;
      model.meshes.Add(mesh);
    }

    private static GR2_Bounding_Box ComputeBounds(GR2 model) {
      Single minX = Single.PositiveInfinity, minY = Single.PositiveInfinity, minZ = Single.PositiveInfinity;
      Single maxX = Single.NegativeInfinity, maxY = Single.NegativeInfinity, maxZ = Single.NegativeInfinity;
      foreach (GR2_Mesh mesh in model.meshes) foreach (GR2_Mesh_Vertex v in mesh.meshVerts) {
        minX = Math.Min(minX, v.X); minY = Math.Min(minY, v.Y); minZ = Math.Min(minZ, v.Z);
        maxX = Math.Max(maxX, v.X); maxY = Math.Max(maxY, v.Y); maxZ = Math.Max(maxZ, v.Z);
      }
      if (!Finite(minX) || !Finite(maxX)) { minX = minY = minZ = -0.5f; maxX = maxY = maxZ = 0.5f; }
      return new GR2_Bounding_Box { minX = minX, minY = minY, minZ = minZ, maxX = maxX, maxY = maxY, maxZ = maxZ, minW = 1f, maxW = 1f };
    }

    private static Single ComputeFooterScale(ViewSPT.SptInfo info, MeshBuilder branches, IEnumerable<MeshBuilder> leaves, MeshBuilder fronds) {
      ViewCollision.BoundingBox footer = BestFooterBounds(info);
      if (footer == null) return 1f;
      Single dx = Math.Abs(footer.Max[0] - footer.Min[0]);
      Single dy = Math.Abs(footer.Max[1] - footer.Min[1]);
      Single dz = Math.Abs(footer.Max[2] - footer.Min[2]);
      Single target = Math.Max(dx, Math.Max(dy, dz));
      if (!Finite(target) || target < 0.01f || target > 10000f) return 1f;

      Single minX = Single.PositiveInfinity, minY = Single.PositiveInfinity, minZ = Single.PositiveInfinity;
      Single maxX = Single.NegativeInfinity, maxY = Single.NegativeInfinity, maxZ = Single.NegativeInfinity;
      void Include(MeshBuilder b) {
        if (b == null) return;
        foreach (GR2_Mesh_Vertex v in b.Vertices) {
          minX = Math.Min(minX, v.X); minY = Math.Min(minY, v.Y); minZ = Math.Min(minZ, v.Z);
          maxX = Math.Max(maxX, v.X); maxY = Math.Max(maxY, v.Y); maxZ = Math.Max(maxZ, v.Z);
        }
      }
      Include(branches); if (leaves != null) foreach (MeshBuilder b in leaves) Include(b); Include(fronds);
      Single generated = Math.Max(maxX - minX, Math.Max(maxY - minY, maxZ - minZ));
      if (!Finite(generated) || generated < 0.001f) return 1f;
      return Clamp(target / generated, 0.05f, 20f);
    }

    private static ViewCollision.BoundingBox BestFooterBounds(ViewSPT.SptInfo info) {
      if (info == null) return null;
      Single Score(ViewCollision.BoundingBox b) {
        if (b == null) return 0f;
        Single dx = Math.Abs(b.Max[0] - b.Min[0]), dy = Math.Abs(b.Max[1] - b.Min[1]), dz = Math.Abs(b.Max[2] - b.Min[2]);
        Single s = Math.Max(dx, Math.Max(dy, dz));
        return Finite(s) ? s : 0f;
      }
      return Score(info.BoundingBox2) >= Score(info.BoundingBox1) ? info.BoundingBox2 : info.BoundingBox1;
    }

    private static void ScaleGeometry(Single scale, MeshBuilder branches, IEnumerable<MeshBuilder> leaves, MeshBuilder fronds) {
      void Scale(MeshBuilder b) {
        if (b == null) return;
        foreach (GR2_Mesh_Vertex v in b.Vertices) { v.X *= scale; v.Y *= scale; v.Z *= scale; }
      }
      Scale(branches); if (leaves != null) foreach (MeshBuilder b in leaves) Scale(b); Scale(fronds);
    }

    private static Vector3 PointAt(Stem stem, Single t) {
      if (stem == null || stem.Points.Count == 0) return Vector3.Zero;
      if (stem.Points.Count == 1) return stem.Points[0];
      t = Clamp01(t); Single f = t * (stem.Points.Count - 1); Int32 i = Math.Min(stem.Points.Count - 2, (Int32)Math.Floor(f)); Single a = f - i;
      return stem.Points[i] * (1f - a) + stem.Points[i + 1] * a;
    }

    private static Vector3 TangentAt(Stem stem, Single t) {
      if (stem == null || stem.Tangents.Count == 0) return Vector3.UnitY;
      if (stem.Tangents.Count == 1) return SafeNormalize(stem.Tangents[0], Vector3.UnitY);
      t = Clamp01(t); Single f = t * (stem.Tangents.Count - 1); Int32 i = Math.Min(stem.Tangents.Count - 2, (Int32)Math.Floor(f)); Single a = f - i;
      return SafeNormalize(stem.Tangents[i] * (1f - a) + stem.Tangents[i + 1] * a, Vector3.UnitY);
    }

    private static Vector3 MakePerpendicular(Vector3 direction) {
      Vector3 d = SafeNormalize(direction, Vector3.UnitY);
      Vector3 reference = Math.Abs(Vector3.Dot(d, Vector3.UnitY)) > 0.92f ? Vector3.UnitZ : Vector3.UnitY;
      return SafeNormalize(Vector3.Cross(d, reference), Vector3.UnitX);
    }

    private static Vector3 PerpendicularAround(Vector3 direction, Single angle) {
      Vector3 a = MakePerpendicular(direction);
      Vector3 b = SafeNormalize(Vector3.Cross(direction, a), Vector3.UnitZ);
      return SafeNormalize(a * (Single)Math.Cos(angle) + b * (Single)Math.Sin(angle), a);
    }

    private static Vector3 SafeNormalize(Vector3 value, Vector3 fallback) {
      if (!Finite(value.X) || !Finite(value.Y) || !Finite(value.Z) || value.LengthSquared() < Epsilon) return fallback;
      value.Normalize(); return value;
    }

    private static Single PositiveSpline(ViewSPT.BezierInfo spline, Single x, Single fallback, Random random, Single minimum) {
      Single value = Evaluate(spline, x, fallback);
      if (spline != null && Finite(spline.Variance) && spline.Variance != 0f)
        value += ((Single)random.NextDouble() * 2f - 1f) * Math.Abs(spline.Variance);
      if (!Finite(value) || value <= minimum) value = fallback;
      return Math.Max(minimum, Math.Abs(value));
    }

    private static Single Evaluate(ViewSPT.BezierInfo spline, Single x, Single fallback) {
      if (spline == null) return fallback;
      x = Clamp01(x);
      Single y;
      if (spline.ControlPoints == null || spline.ControlPoints.Count == 0) y = x;
      else if (spline.ControlPoints.Count == 1) y = spline.ControlPoints[0].Y;
      else {
        ViewSPT.BezierControlPoint left = spline.ControlPoints[0], right = spline.ControlPoints[spline.ControlPoints.Count - 1];
        for (Int32 i = 0; i < spline.ControlPoints.Count - 1; i++) {
          ViewSPT.BezierControlPoint a = spline.ControlPoints[i], b = spline.ControlPoints[i + 1];
          if (x >= a.X && x <= b.X) { left = a; right = b; break; }
          if (x < spline.ControlPoints[0].X) { left = right = spline.ControlPoints[0]; break; }
        }
        Single span = right.X - left.X;
        Single t = Math.Abs(span) < Epsilon ? 0f : Clamp01((x - left.X) / span);
        // Cubic Hermite uses the tangent authored by SpeedTree when it is usable.  Falling back to the chord
        // keeps corrupt/beta splines stable and still preserves the important min/max/profile shape.
        Single m0 = TangentSlope(left, right.Y - left.Y);
        Single m1 = TangentSlope(right, right.Y - left.Y);
        Single t2 = t * t, t3 = t2 * t;
        y = (2f * t3 - 3f * t2 + 1f) * left.Y
          + (t3 - 2f * t2 + t) * m0 * span
          + (-2f * t3 + 3f * t2) * right.Y
          + (t3 - t2) * m1 * span;
      }
      if (!Finite(y)) return fallback;
      Single value = spline.Min + y * (spline.Max - spline.Min);
      return Finite(value) ? value : fallback;
    }

    private static Single TangentSlope(ViewSPT.BezierControlPoint point, Single fallback) {
      if (point == null || !Finite(point.TangentX) || !Finite(point.TangentY) || Math.Abs(point.TangentX) < Epsilon) return fallback;
      Single slope = point.TangentY / point.TangentX;
      return Finite(slope) ? Clamp(slope, -20f, 20f) : fallback;
    }

    private static String FirstString(ViewSPT.SptInfo info, UInt32 token) {
      foreach (ViewSPT.SptRecord r in info.Records) if (r.Token == token && r.Value is String s && !String.IsNullOrWhiteSpace(s)) return s;
      return null;
    }
    private static Single FirstSingle(ViewSPT.SptInfo info, UInt32 token, Single fallback) {
      foreach (ViewSPT.SptRecord r in info.Records) if (r.Token == token && r.Value is Single s) return s;
      return fallback;
    }
    private static UInt32 FirstUInt(ViewSPT.SptInfo info, UInt32 token, UInt32 fallback) {
      foreach (ViewSPT.SptRecord r in info.Records) if (r.Token == token && r.Value is UInt32 u) return u;
      return fallback;
    }

    private static String ResolveTexture(String raw, String sourcePath) => String.IsNullOrWhiteSpace(raw) ? null : ViewSPT.ResolveTexturePath(raw, sourcePath);

    private static String FirstMapCollectionTexture(ViewSPT.SptInfo info, UInt32 sectionToken, String sourcePath) {
      List<String> values = MapCollectionTextures(info, sectionToken, sourcePath);
      return values.Count > 0 ? values[0] : null;
    }

    private static List<String> MapCollectionTextures(ViewSPT.SptInfo info, UInt32 sectionToken, String sourcePath) {
      var values = new List<String>(); Boolean inSection = false;
      foreach (ViewSPT.SptRecord r in info.Records) {
        if (r.Token == sectionToken) { inSection = true; continue; }
        if (inSection && (r.Token == 60002u || r.Token == 60003u || r.Token == 60004u || r.Token == 60005u || r.Token == 60009u) && r.Token != sectionToken) break;
        if (inSection && r.Token == 70002u && r.Value is String raw) {
          String path = ResolveTexture(raw, sourcePath);
          if (!String.IsNullOrWhiteSpace(path) && !values.Contains(path, StringComparer.OrdinalIgnoreCase)) values.Add(path);
        }
      }
      return values;
    }

    private static Single ToRadians(Single value) {
      if (!Finite(value)) return 0f;
      return Math.Abs(value) > 6.4f ? value * ((Single)Math.PI / 180f) : value;
    }
    private static Single Clamp01(Single v) => Clamp(v, 0f, 1f);
    private static Single Clamp(Single v, Single min, Single max) => v < min ? min : (v > max ? max : v);
    private static Boolean Finite(Single v) => !Single.IsNaN(v) && !Single.IsInfinity(v);

    private static UInt32 StableHash(String text) {
      unchecked {
        UInt32 hash = 2166136261u;
        foreach (Char c in text ?? String.Empty) { hash ^= c; hash *= 16777619u; }
        return hash;
      }
    }
  }
}
