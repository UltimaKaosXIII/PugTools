using System;
using System.Collections.Generic;
using System.Linq;
using SlimDX;

namespace PugTools {
  internal sealed partial class View_AREA {
    internal sealed class PrtPreviewParticleState {
      public Single X, Y, Z;
      public Single HalfWidth, HalfHeight;
      public Single Rotation;
      public Single R = 1f, G = 1f, B = 1f, A = 1f;
      public String TexturePath;
      public Int32 Columns = 1, Rows = 1, Frame;
    }

    internal sealed class PrtPreviewTrailSegmentState {
      public Single AX, AY, AZ, BX, BY, BZ, CX, CY, CZ, DX, DY, DZ;
      public Single R = 1f, G = 1f, B = 1f, A = 1f;
      public String TexturePath;
    }

    internal sealed class PrtPreviewSnapshot {
      public Single Time;
      public String ParticleType;
      public Int32 NestedFxCount;
      public readonly List<PrtPreviewParticleState> Particles = new List<PrtPreviewParticleState>();
      public readonly List<PrtPreviewTrailSegmentState> Trails = new List<PrtPreviewTrailSegmentState>();
    }

    /// <summary>
    /// Thin adapter around the same WorldPrtSystem used by the live World Browser.
    /// The Asset Browser can therefore preview PRT motion without duplicating the engine approximation.
    /// The Asset Browser renders the snapshots as an orbitable 3D sprite/trail preview while simulation state
    /// itself remains shared with the World Browser VFX runtime.
    /// </summary>
    internal sealed class PrtPreviewSession : IDisposable {
      private readonly Object _sync = new Object();
      private readonly String _rootPath;
      private readonly String _rootText;
      private readonly Func<String, String> _textLoader;
      private readonly Dictionary<String, WorldPrtSpec> _cache = new Dictionary<String, WorldPrtSpec>(StringComparer.OrdinalIgnoreCase);
      private WorldPrtSystem _system;
      private Boolean _disposed;

      private PrtPreviewSession(String rootPath, String rootText, Func<String, String> textLoader) {
        _rootPath = NormalizePreviewPrtPath(rootPath);
        _rootText = rootText ?? String.Empty;
        _textLoader = textLoader;
        ResetCore();
      }

      public static PrtPreviewSession Create(String rootPath, String rootText, Func<String, String> textLoader) {
        if (String.IsNullOrWhiteSpace(rootText)) return null;
        return new PrtPreviewSession(rootPath, rootText, textLoader);
      }

      public PrtPreviewSnapshot Step(Single dt) {
        lock (_sync) {
          if (_disposed || _system == null) return null;
          _system.Update(Math.Max(0.001f, Math.Min(0.1f, dt)), new Vector3(0f, 0f, -4f));
          return SnapshotCore();
        }
      }

      public PrtPreviewSnapshot Snapshot() {
        lock (_sync) return _disposed || _system == null ? null : SnapshotCore();
      }

      public void Reset() {
        lock (_sync) {
          if (_disposed) return;
          ResetCore();
        }
      }

      private void ResetCore() {
        _cache.Clear();
        WorldPrtSpec root = WorldPrtSpec.Parse(_rootPath, _rootText);
        if (root == null) { _system = null; return; }
        _cache[_rootPath] = root;
        _system = new WorldPrtSystem(root, LoadSpec, 0x50525431u, 1f);
      }

      private WorldPrtSpec LoadSpec(String requested) {
        foreach (String candidate in PreviewPrtCandidates(requested)) {
          if (_cache.TryGetValue(candidate, out WorldPrtSpec cached)) return cached;
          String text = null;
          try { text = _textLoader?.Invoke(candidate); } catch { }
          if (String.IsNullOrWhiteSpace(text)) continue;
          WorldPrtSpec parsed = WorldPrtSpec.Parse(candidate, text);
          if (parsed == null) continue;
          _cache[candidate] = parsed;
          return parsed;
        }
        return null;
      }

      private PrtPreviewSnapshot SnapshotCore() {
        var result = new PrtPreviewSnapshot {
          Time = _system.Time,
          ParticleType = _system.Root?.Type,
          NestedFxCount = _system.FxInstances.Count
        };
        foreach (WorldPrtParticle p in _system.Particles.Take(384)) {
          WorldPrtSpec spec = p.Spec;
          if (spec == null) continue;
          Single t = p.Life > 0f ? Math.Max(0f, Math.Min(1f, p.Age / p.Life)) : 0f;
          var seeded = new WorldFxRandom(p.Seed);
          Single size = spec.Track("Size2D")?.Number(t, seeded, .01f) ?? .01f;
          if (size <= .000001f || size > 1000f) size = .01f;
          Single sx = p.SizeX, sy = p.SizeY;
          if (spec.Bool("ScaleEachUpdate", true)
              && spec.Track("XScale2D") is WorldFxValue xTrack
              && spec.Track("YScale2D") is WorldFxValue yTrack) {
            sx = xTrack.Number(t, seeded, sx);
            sy = yTrack.Number(t, seeded, sy);
          }
          Vector4 color = spec.Track("DiffuseColor")?.Color(t, seeded, new Vector4(1f, 1f, 1f, 1f))
            ?? new Vector4(1f, 1f, 1f, 1f);
          Vector3 pos = _system.ResolveParticlePosition(p);
          Int32 columns = Math.Max(1, (Int32)Math.Round(spec.Number("RowSize", 1f)));
          Int32 rows = Math.Max(1, (Int32)Math.Round(spec.Number("ColumnSize", 1f)));
          result.Particles.Add(new PrtPreviewParticleState {
            X = pos.X, Y = pos.Y, Z = pos.Z,
            HalfWidth = Math.Abs(size * sx), HalfHeight = Math.Abs(size * sy),
            Rotation = p.Rotation,
            R = color.X, G = color.Y, B = color.Z, A = color.W,
            TexturePath = NormalizeWorldOverheadTexturePath(spec.Text("TextureName")),
            Columns = columns, Rows = rows, Frame = p.Frame
          });
        }
        Int32 trailBudget = 640;
        foreach (WorldPrtParticle particle in _system.Particles) {
          if (trailBudget <= 0 || particle?.Spec == null) break;
          foreach (WorldPrtTrail trail in new[] { particle.Trail, particle.Trail2 }) {
            if (trail == null || trail.Points.Count < 2) continue;
            Single lenMod = particle.Spec.Number("TrailLengthModifier", 1f);
            if (!(lenMod > .00001f)) lenMod = 1f;
            Single span = trail.Decay > 0f ? trail.Decay * lenMod : 0f;
            Vector4 c1 = WorldFxTrackColor(particle.Spec, "TrailColor1", 0f, particle.Seed + 101u, new Vector4(0f, 0f, 0f, 0f));
            Vector4 c2 = WorldFxTrackColor(particle.Spec, "TrailColor2", 0f, particle.Seed + 102u, new Vector4(0f, 0f, 0f, 0f));
            Vector4 c3 = WorldFxTrackColor(particle.Spec, "TrailColor3", 0f, particle.Seed + 103u, new Vector4(1f, 1f, 1f, 1f));
            Vector4 c4 = WorldFxTrackColor(particle.Spec, "TrailColor4", 0f, particle.Seed + 104u, new Vector4(1f, 1f, 1f, 1f));
            String texture = NormalizeWorldOverheadTexturePath(particle.Spec.Text("TrailTexture"));
            for (Int32 i = 0; i + 1 < trail.Points.Count && trailBudget > 0; i++, trailBudget--) {
              WorldPrtTrailPoint p0 = trail.Points[i], p1 = trail.Points[i + 1];
              Single rel0 = span > 0f ? (_system.Time - p0.Born) / span : (i == trail.Points.Count - 1 ? 0f : 1f);
              Single rel1 = span > 0f ? (_system.Time - p1.Born) / span : (i + 1 == trail.Points.Count - 1 ? 0f : 1f);
              Single t0 = Math.Max(0f, Math.Min(1f, rel0)), t1 = Math.Max(0f, Math.Min(1f, rel1));
              Vector4 a0 = WorldFxLerp(c3, c1, t0), b0 = WorldFxLerp(c4, c2, t0);
              Vector4 a1 = WorldFxLerp(c3, c1, t1), b1 = WorldFxLerp(c4, c2, t1);
              Vector4 color = (a0 + b0 + a1 + b1) * .25f;
              result.Trails.Add(new PrtPreviewTrailSegmentState {
                AX = p0.A.X, AY = p0.A.Y, AZ = p0.A.Z,
                BX = p0.B.X, BY = p0.B.Y, BZ = p0.B.Z,
                CX = p1.B.X, CY = p1.B.Y, CZ = p1.B.Z,
                DX = p1.A.X, DY = p1.A.Y, DZ = p1.A.Z,
                R = color.X, G = color.Y, B = color.Z, A = color.W,
                TexturePath = texture
              });
            }
          }
        }
        return result;
      }

      public void Dispose() {
        lock (_sync) {
          _disposed = true;
          _system = null;
          _cache.Clear();
        }
      }
    }

    private static IEnumerable<String> PreviewPrtCandidates(String requested) {
      String raw = (requested ?? String.Empty).Trim().Trim('"', '\'').Replace('\\', '/');
      if (raw.Length == 0) yield break;
      if (!raw.EndsWith(".prt", StringComparison.OrdinalIgnoreCase)) raw += ".prt";

      var yielded = new HashSet<String>(StringComparer.OrdinalIgnoreCase);
      void Add(List<String> list, String candidate) {
        candidate = NormalizePreviewPrtPath(candidate);
        if (candidate.Length > 0 && yielded.Add(candidate)) list.Add(candidate);
      }
      var candidates = new List<String>();
      if (raw.StartsWith("/resources/", StringComparison.OrdinalIgnoreCase)) Add(candidates, raw);
      else if (raw.StartsWith("resources/", StringComparison.OrdinalIgnoreCase)) Add(candidates, "/" + raw);
      else if (raw.StartsWith("art/", StringComparison.OrdinalIgnoreCase)) Add(candidates, "/resources/" + raw);
      else {
        Add(candidates, "/resources/art/fx/particles/" + raw.TrimStart('/'));
        Add(candidates, "/resources/art/fx/particles/_testtrash/" + raw.TrimStart('/'));
        Add(candidates, "/resources/" + raw.TrimStart('/'));
      }
      foreach (String candidate in candidates) yield return candidate;
    }

    private static String NormalizePreviewPrtPath(String value) {
      String path = (value ?? String.Empty).Trim().Replace('\\', '/');
      while (path.Contains("//", StringComparison.Ordinal)) path = path.Replace("//", "/", StringComparison.Ordinal);
      if (path.StartsWith("resources/", StringComparison.OrdinalIgnoreCase)) path = "/" + path;
      else if (!path.StartsWith("/resources/", StringComparison.OrdinalIgnoreCase)) path = "/resources/" + path.TrimStart('/');
      return path.ToLowerInvariant();
    }
  }
}
