using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using SlimDX;
using SlimDX.Direct3D11;
using SlimDXNet.Vertex;

namespace PugTools {
  internal sealed partial class View_AREA {
    // A deliberately small, world-hosted port of Jedipedia's fxspec-render.js/prt-sim.js path. It is not a DDS
    // extractor: the FXSPEC marshal is parsed into timed emitter nodes, each node owns a running PRT system, and the
    // PRT system emits/lifetimes/moves/animates real billboard particles. Unsupported particle classes fall through to
    // the old resolver so RED/Beta oddities remain visible while this runtime is expanded toward the full player.
    private const int WorldFxParticleCapPerSystem = 384;
    private const int WorldFxEmitterCapPerSystem = 64;
    private const int WorldFxMaxRenderParticlesPerIcon = 96;
    private const float WorldFxPreRollSeconds = 2f;
    private const float WorldFxPreRollStep = 1f / 60f;
    private const float WorldFxMaxFrameStep = 1f / 30f;

    private enum WorldFxValueKind { Empty, Scalar, Bool, String, Range, Vector, Color, Gradient, Sine }

    private sealed class WorldFxValue {
      public WorldFxValueKind Kind;
      public double Scalar;
      public bool Bool;
      public string Text;
      public double Min, Max, Variation = 1, Median;
      public List<WorldFxValue> Slots;
      public List<WorldFxGradientStop> Stops;
      public char LoopMode = 'O';
      public double? Duration;
      public Dictionary<string, double> Sine;

      public static WorldFxValue Parse(string raw) {
        string s = (raw ?? String.Empty).Trim();
        if (s.Length == 0) return new WorldFxValue { Kind = WorldFxValueKind.Empty };
        if (s[0] == '[' || (s.Length > 1 && Char.IsDigit(s[0]) && s[1] == '[')) {
          WorldFxValue g = ParseGradient(s[0] == '[' ? s : s.Substring(1));
          if (g != null) return g;
        }
        if (s[0] == '#') {
          List<string> parts = SplitTop(s.Substring(1), ',');
          var slots = new List<WorldFxValue>();
          foreach (string part in parts) slots.Add(ParseSlot(part));
          if (slots.All(x => x != null)) {
            while (slots.Count < 4) slots.Add(new WorldFxValue { Kind = WorldFxValueKind.Scalar, Scalar = 1 });
            return new WorldFxValue { Kind = WorldFxValueKind.Color, Slots = slots };
          }
        }
        if (s[0] == '(' && s.EndsWith(")", StringComparison.Ordinal)) {
          List<string> parts = SplitTop(s.Substring(1, s.Length - 2), ',');
          var slots = new List<WorldFxValue>();
          foreach (string part in parts) slots.Add(ParseSlot(part));
          if (slots.Count > 0 && slots.All(x => x != null)) return new WorldFxValue { Kind = WorldFxValueKind.Vector, Slots = slots };
        }
        if (s[0] == '<' && s.EndsWith(">", StringComparison.Ordinal)) {
          List<string> parts = SplitTop(s.Substring(1, s.Length - 2), ',');
          var nums = new List<double>();
          foreach (string part in parts) {
            if (!Double.TryParse(part.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double number)) { nums.Clear(); break; }
            nums.Add(number);
          }
          if (nums.Count > 0) return new WorldFxValue {
            Kind = WorldFxValueKind.Range, Min = nums[0], Max = nums.Count > 1 ? nums[1] : nums[0],
            Variation = nums.Count > 2 ? nums[2] : 1, Median = nums.Count > 3 ? nums[3] : 0
          };
        }
        if (s[0] == '~') {
          var sine = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
          if (s == "~" || String.Equals(s, "~SIN()", StringComparison.OrdinalIgnoreCase) || String.Equals(s, "~SINE()", StringComparison.OrdinalIgnoreCase))
            return new WorldFxValue { Kind = WorldFxValueKind.Sine, Sine = sine };
          int lp = s.IndexOf('('), rp = s.LastIndexOf(')');
          if (lp >= 0 && rp > lp && s.Substring(1, lp - 1).Trim().Equals("SINE", StringComparison.OrdinalIgnoreCase)) {
            foreach (string part in SplitTop(s.Substring(lp + 1, rp - lp - 1), ',')) {
              int eq = part.IndexOf('='); if (eq <= 0) continue;
              if (Double.TryParse(part.Substring(eq + 1).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) sine[part.Substring(0, eq).Trim()] = v;
            }
            return new WorldFxValue { Kind = WorldFxValueKind.Sine, Sine = sine };
          }
        }
        if (Double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double scalar)) return new WorldFxValue { Kind = WorldFxValueKind.Scalar, Scalar = scalar };
        if (Boolean.TryParse(s, out bool flag)) return new WorldFxValue { Kind = WorldFxValueKind.Bool, Bool = flag };
        return new WorldFxValue { Kind = WorldFxValueKind.String, Text = s };
      }

      private static WorldFxValue ParseSlot(string text) {
        string s = (text ?? String.Empty).Trim();
        if (s.StartsWith("<", StringComparison.Ordinal)) {
          WorldFxValue value = Parse(s); return value.Kind == WorldFxValueKind.Range ? value : null;
        }
        if (Double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double n)) return new WorldFxValue { Kind = WorldFxValueKind.Scalar, Scalar = n };
        return null;
      }

      private static WorldFxValue ParseGradient(string source) {
        string s = (source ?? String.Empty).Trim();
        var stops = new List<WorldFxGradientStop>();
        int at = 0;
        while (at < s.Length && s[at] == '[') {
          int depth = 0, end = -1;
          for (int i = at; i < s.Length; i++) {
            if (s[i] == '[') depth++;
            else if (s[i] == ']' && --depth == 0) { end = i; break; }
          }
          if (end < 0) return null;
          string body = s.Substring(at + 1, end - at - 1);
          int c1 = TopDelimiter(body, ':', false), c2 = TopDelimiter(body, ':', true);
          if (c1 < 0 || c2 <= c1) return null;
          if (!Double.TryParse(body.Substring(0, c1).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double t)) return null;
          string valueText = body.Substring(c1 + 1, c2 - c1 - 1).Trim();
          WorldFxValue value = Parse(valueText);
          if (value == null || value.Kind == WorldFxValueKind.String || value.Kind == WorldFxValueKind.Empty) return null;
          string interpText = body.Substring(c2 + 1).Trim().ToUpperInvariant();
          stops.Add(new WorldFxGradientStop { T = t, Value = value, Interpolation = interpText.Length > 0 ? interpText[0] : 'L' });
          at = end + 1;
        }
        if (stops.Count == 0) return null;
        stops.Sort((a, b) => a.T.CompareTo(b.T));
        string rest = s.Substring(at).Trim();
        double? duration = null;
        int semi = rest.IndexOf(';');
        if (semi >= 0) {
          if (Double.TryParse(rest.Substring(semi + 1).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double d)) duration = d;
          rest = rest.Substring(0, semi).Trim();
        }
        char mode = rest.Length == 0 ? 'O' : Char.ToUpperInvariant(rest[0]);
        if (mode != 'O' && mode != 'L' && mode != 'P') mode = 'O';
        return new WorldFxValue { Kind = WorldFxValueKind.Gradient, Stops = stops, LoopMode = mode, Duration = duration };
      }

      private static int TopDelimiter(string s, char wanted, bool last) {
        int depth = 0, found = -1;
        for (int i = 0; i < s.Length; i++) {
          char c = s[i];
          if (c == '<' || c == '(' || c == '[') depth++;
          else if (c == '>' || c == ')' || c == ']') depth--;
          else if (c == wanted && depth == 0) { found = i; if (!last) return i; }
        }
        return found;
      }

      private static List<string> SplitTop(string s, char separator) {
        var result = new List<string>(); int depth = 0, start = 0;
        for (int i = 0; i < s.Length; i++) {
          char c = s[i];
          if (c == '<' || c == '(' || c == '[') depth++;
          else if (c == '>' || c == ')' || c == ']') depth--;
          else if (c == separator && depth == 0) { result.Add(s.Substring(start, i - start)); start = i + 1; }
        }
        result.Add(s.Substring(start)); return result;
      }

      public float Number(float t, WorldFxRandom random, float fallback = 0f) {
        object value = Evaluate(t, random, null);
        return value is double d && !Double.IsNaN(d) && !Double.IsInfinity(d) ? (float)d : fallback;
      }

      public Vector3 Vector3(float t, WorldFxRandom random, Vector3 fallback) {
        object value = Evaluate(t, random, new double[4]);
        if (value is double[] v && v.Length >= 3) return new Vector3((float)v[0], (float)v[1], (float)v[2]);
        if (value is double d) return new Vector3((float)d, (float)d, (float)d);
        return fallback;
      }

      public Vector4 Color(float t, WorldFxRandom random, Vector4 fallback) {
        object value = Evaluate(t, random, new double[4]);
        if (value is double[] v && v.Length > 0) return new Vector4((float)v[0], (float)(v.Length > 1 ? v[1] : v[0]),
          (float)(v.Length > 2 ? v[2] : v[0]), (float)(v.Length > 3 ? v[3] : 1));
        if (value is double d) return new Vector4((float)d, (float)d, (float)d, 1f);
        return fallback;
      }

      private object Evaluate(double t, WorldFxRandom random, double[] output) {
        switch (Kind) {
          case WorldFxValueKind.Scalar: return Scalar;
          case WorldFxValueKind.Bool: return Bool ? 1d : 0d;
          case WorldFxValueKind.Range: return SampleRange(random);
          case WorldFxValueKind.Vector:
          case WorldFxValueKind.Color: {
            int n = Slots?.Count ?? 0; double[] values = output != null && output.Length >= n ? output : new double[Math.Max(1, n)];
            for (int i = 0; i < n; i++) values[i] = Slots[i].Kind == WorldFxValueKind.Range ? Slots[i].SampleRange(random) : Slots[i].Scalar;
            return values;
          }
          case WorldFxValueKind.Sine: {
            double frq = SineValue("frq", 1), cyc = SineValue("cyc", 1), amp = SineValue("amp", 1), off = SineValue("off", 0);
            double min = SineValue("min", -1), max = SineValue("max", 1), pha = SineValue("pha", 0) * Math.PI / 180.0;
            double angle = t * frq * Math.PI * 2.0; if (cyc != 0) angle /= cyc;
            return Math.Max(min, Math.Min(max, off + amp * Math.Sin(angle + pha)));
          }
          case WorldFxValueKind.Gradient: return EvaluateGradient(t, random, output);
          default: return null;
        }
      }

      private double SineValue(string key, double fallback) => Sine != null && Sine.TryGetValue(key, out double v) ? v : fallback;

      private double SampleRange(WorldFxRandom random) {
        if (Math.Abs(Max - Min) < 1e-12) return Min;
        double m = Math.Max(-1, Math.Min(1, Median)); double w = m * m * m * m * m * .5;
        double lo = Math.Max(0, w), hi = Math.Min(1, w + 1) - lo;
        for (int attempt = 0; attempt < 2; attempt++) {
          double p = hi * random.NextDouble() + lo; if (p <= 0) p = .001; else if (p >= 1) p = .999;
          double q = p > .5 ? 1 - p : p; double tt = Math.Sqrt(Math.Log(1 / (q * q)));
          double z = tt - (2.515517 + .802853 * tt + .010328 * tt * tt) / (1 + 1.432788 * tt + .189269 * tt * tt + .001308 * tt * tt * tt);
          if (p > .5) z = -z; double x = z * Variation + m;
          if (x > -1 && x < 1) return Min + ((x + 1) * .5) * (Max - Min);
        }
        return Min + random.NextDouble() * (Max - Min);
      }

      private object EvaluateGradient(double t, WorldFxRandom random, double[] output) {
        if (Stops == null || Stops.Count == 0) return null;
        if (Duration.HasValue && Duration.Value > 0) t /= Duration.Value;
        if (LoopMode == 'L') { t %= 1; if (t < 0) t += 1; }
        else if (LoopMode == 'P') { int n = (int)Math.Floor(t); double f = t - n; t = (n & 1) == 0 ? 1 - f : f; }
        else t = Math.Max(0, Math.Min(1, t));
        int index = 0; while (index < Stops.Count - 1 && Stops[index + 1].T <= t) index++;
        WorldFxGradientStop a = Stops[index], b = Stops[Math.Min(index + 1, Stops.Count - 1)];
        double u = b.T > a.T ? Math.Max(0, Math.Min(1, (t - a.T) / (b.T - a.T))) : (t >= b.T ? 1 : 0);
        double f2 = a == b ? 0 : Interpolate(a.Interpolation, u);
        object av = a.Value.Evaluate(t, random, output), bv = b.Value.Evaluate(t, random, null);
        if (av is double ad) { double bd = bv is double bd0 ? bd0 : ad; return ad + (bd - ad) * f2; }
        if (av is double[] aa) {
          double[] bb = bv as double[] ?? aa; double[] res = output != null && output.Length >= aa.Length ? output : new double[aa.Length];
          for (int i = 0; i < aa.Length; i++) { double y = i < bb.Length ? bb[i] : aa[i]; res[i] = aa[i] + (y - aa[i]) * f2; }
          return res;
        }
        return null;
      }

      private static double Interpolate(char interpolation, double u) {
        switch (Char.ToUpperInvariant(interpolation)) {
          case 'H': return 0;
          case 'I': return 1 - Math.Pow(1 - u, 3);
          case 'O': return u * u * u;
          case 'S': return u * u * (3 - 2 * u);
          case 'T': return u * (3 - u * (6 - 4 * u));
          case 'Q': return u * (2 - u);
          case 'P': return u * u;
          default: return u;
        }
      }
    }

    private sealed class WorldFxGradientStop { public double T; public WorldFxValue Value; public char Interpolation; }

    private sealed class WorldFxRandom {
      private uint state;
      public WorldFxRandom(uint seed) { state = seed == 0 ? 0x6D2B79F5u : seed; }
      public double NextDouble() {
        uint z = state += 0x6D2B79F5u; z = (z ^ (z >> 15)) * (z | 1u); z ^= z + ((z ^ (z >> 7)) * (z | 61u));
        return ((z ^ (z >> 14)) & 0xFFFFFFFFu) / 4294967296.0;
      }
      public int Next(int maximum) => maximum <= 1 ? 0 : Math.Min(maximum - 1, (int)(NextDouble() * maximum));
    }

    private sealed class WorldPrtSpec {
      public string Path;
      public string Type = "BILLBOARD_POINT";
      public readonly Dictionary<string, string> Raw = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
      public readonly Dictionary<string, WorldFxValue> Values = new Dictionary<string, WorldFxValue>(StringComparer.OrdinalIgnoreCase);

      public string Text(string key, string fallback = null) => Raw.TryGetValue(key, out string value) && !String.IsNullOrWhiteSpace(value) ? value.Trim() : fallback;
      public bool Bool(string key, bool fallback = false) {
        if (!Values.TryGetValue(key, out WorldFxValue value)) return fallback;
        if (value.Kind == WorldFxValueKind.Bool) return value.Bool;
        return fallback;
      }
      public float Number(string key, float fallback = 0f) {
        if (!Values.TryGetValue(key, out WorldFxValue value)) return fallback;
        if (value.Kind == WorldFxValueKind.Scalar) return (float)value.Scalar;
        if (value.Kind == WorldFxValueKind.Bool) return value.Bool ? 1f : 0f;
        return fallback;
      }
      public WorldFxValue Track(string key) => Values.TryGetValue(key, out WorldFxValue value) && value.Kind != WorldFxValueKind.Empty ? value : null;
      public static WorldPrtSpec Parse(string path, string text) {
        if (String.IsNullOrWhiteSpace(text)) return null;
        var result = new WorldPrtSpec { Path = path };
        using var reader = new StringReader(text);
        string line;
        while ((line = reader.ReadLine()) != null) {
          line = line.Trim(); if (line.Length == 0 || line == "!" || line.StartsWith("! Particle Specification", StringComparison.OrdinalIgnoreCase) || line == "[SETTINGS]") continue;
          int eq = line.IndexOf('='); if (eq < 0) continue;
          string key = line.Substring(0, eq).Trim().TrimStart('.'); string value = line.Substring(eq + 1).Trim();
          if (key.Length == 0) continue; result.Raw[key] = value;
        }
        foreach (KeyValuePair<string, string> pair in result.Raw) result.Values[pair.Key] = WorldFxValue.Parse(pair.Value);
        result.Type = result.Text("ParticleType", "BILLBOARD_POINT").Trim().ToUpperInvariant();
        return result;
      }
    }

    private sealed class WorldFxNodeDefinition {
      public string Name, Resource, WhenToStart, StartFxName, WhenToStop, StopFxName, OwnerName;
      // FxSpec elements can be mounted on other named elements. These are not cosmetic references: a quest icon,
      // for example, builds several particle layers in one hierarchy. Resolving every layer against the actor root
      // tears the composite apart (the green flourish remains at NamePlate while the gold glyph rides above it).
      public string AttachTo, StartLoc;
      public float StartDelay, StopDelay, StartAt, StopAt = Single.PositiveInfinity;
      public Vector3 Offset, Scale = new Vector3(1f, 1f, 1f), Rotation;
      public Vector4 Tint = new Vector4(1f, 1f, 1f, 1f);
      public float LifeModifier = 1f;
      public bool HideOnStop;
      public bool IsGroup;
      public Dictionary<string, string> Fields;
    }

    private sealed class WorldFxSpecDefinition {
      public string Path;
      public string MasterName;
      public string TimerType;
      public float FireRate;
      public float GroupEnd = Single.PositiveInfinity;
      public readonly List<WorldFxNodeDefinition> Nodes = new List<WorldFxNodeDefinition>();
      public int SupportedEmitterCount => Nodes.Count(x => !x.IsGroup && !String.IsNullOrWhiteSpace(x.Resource));
    }

    private sealed class WorldPrtEmitter {
      public WorldPrtSpec Spec;
      public WorldPrtEmitter Parent;
      public int Depth;
      public float Age, Life = Single.PositiveInfinity, Clock, StepAccum, NextEmit;
      public bool NeverEmitted = true, Dead;
      public Vector3 Position, Velocity;
    }

    private sealed class WorldPrtParticle {
      public WorldPrtSpec Spec;
      public Vector3 Position, Velocity;
      public float Age, Life, Rotation, RotationVelocity, FrameClock;
      public int Frame, PreviousFrame;
      public float SizeX = 1f, SizeY = 1f;
      public uint Seed;
    }

    private sealed class WorldPrtSystem {
      private readonly Func<string, WorldPrtSpec> loader;
      private readonly WorldFxRandom random;
      private readonly List<WorldPrtEmitter> emitters = new List<WorldPrtEmitter>();
      private readonly List<WorldPrtParticle> particles = new List<WorldPrtParticle>();
      private bool stopped, hideStopped;
      private float lifeModifier = 1f;
      public WorldPrtSpec Root { get; }
      public IReadOnlyList<WorldPrtParticle> Particles => particles;

      public WorldPrtSystem(WorldPrtSpec root, Func<string, WorldPrtSpec> loader, uint seed, float lifeModifier) {
        Root = root; this.loader = loader; random = new WorldFxRandom(seed); this.lifeModifier = Math.Max(.001f, lifeModifier);
        if (root == null) return;
        if (IsEmitter(root)) SpawnEmitter(root, null, 0, Vector3.Zero);
        else SpawnParticle(root, null, Vector3.Zero, Vector3.Zero);
      }

      public void Stop(bool hide) { stopped = true; hideStopped = hide; if (hide) particles.Clear(); }

      private static bool IsEmitter(WorldPrtSpec spec) => spec != null && (spec.Type == "BASIC_EMITTER" || spec.Type == "DUMMY_EMITTER");
      private static bool IsBillboard(WorldPrtSpec spec) => spec != null && (spec.Type.StartsWith("BILLBOARD", StringComparison.OrdinalIgnoreCase) || spec.Type == "QUAD" || spec.Type == "POINT");

      private WorldPrtEmitter SpawnEmitter(WorldPrtSpec spec, WorldPrtEmitter parent, int depth, Vector3 position) {
        if (spec == null || depth > 8 || emitters.Count >= WorldFxEmitterCapPerSystem) return null;
        var emitter = new WorldPrtEmitter { Spec = spec, Parent = parent, Depth = depth, Position = position };
        WorldFxValue life = spec.Track("LifeSpan"); if (life != null) emitter.Life = Math.Max(.001f, life.Number(0, random, Single.PositiveInfinity));
        WorldFxValue trajectory = spec.Track("Trajectory"); if (trajectory != null) emitter.Velocity = trajectory.Vector3(0, random, Vector3.Zero);
        emitters.Add(emitter); return emitter;
      }

      private void SpawnParticle(WorldPrtSpec spec, WorldPrtEmitter owner, Vector3 position, Vector3 outward) {
        if (spec == null || particles.Count >= WorldFxParticleCapPerSystem || !IsBillboard(spec)) return;
        var p = new WorldPrtParticle { Spec = spec, Position = position, Seed = (uint)(random.NextDouble() * UInt32.MaxValue) };
        WorldFxValue life = spec.Track("LifeSpan"); p.Life = Math.Max(.001f, (life?.Number(0, random, 1f) ?? 1f) * lifeModifier);
        WorldFxValue trajectory = spec.Track("Trajectory"); p.Velocity = trajectory?.Vector3(0, random, Vector3.Zero) ?? Vector3.Zero;
        if (owner != null) {
          float speed = owner.Spec.Track("InitialParticleVelocity")?.Number(Normalized(owner.Age, owner.Life), random, 0f) ?? owner.Spec.Number("InitialParticleVelocity", 0f);
          p.Velocity += outward * speed;
          WorldFxValue initialRot = owner.Spec.Track("InitialRotation2D"); if (initialRot != null) p.Rotation = initialRot.Number(Normalized(owner.Age, owner.Life), random, 0f) * (float)Math.PI / 180f;
        }
        WorldFxValue deltaRot = spec.Track("DeltaRotation2D"); if (deltaRot != null) p.RotationVelocity = deltaRot.Number(0, random, 0f);
        int columns = Math.Max(1, (int)Math.Round(spec.Number("RowSize", 1f))), rows = Math.Max(1, (int)Math.Round(spec.Number("ColumnSize", 1f)));
        int total = Math.Max(1, columns * rows), start = (int)Math.Floor(spec.Number("StartFrame", 0f));
        if (start < 0) start = random.Next(total); else start = Math.Min(total - 1, start);
        p.Frame = p.PreviousFrame = start;
        WorldFxValue xs = spec.Track("XScale2D"), ys = spec.Track("YScale2D");
        if (xs != null && ys != null) { p.SizeX = xs.Number(0, random, 1f); p.SizeY = ys.Number(0, random, 1f); }
        particles.Add(p);
      }

      public void Update(float dt, Vector3 cameraLocal) {
        if (Root == null || dt <= 0) return;
        dt = Math.Min(.1f, dt);
        for (int eIndex = 0; eIndex < emitters.Count; eIndex++) {
          WorldPrtEmitter e = emitters[eIndex]; if (e.Dead) continue;
          e.Age += dt; e.Position += e.Velocity * dt;
          if (!Single.IsPositiveInfinity(e.Life) && e.Age >= e.Life) { e.Dead = true; continue; }
          if (stopped || !IsEmitter(e.Spec)) continue;
          float step = Math.Max(dt, UpdateRateStep(e.Spec));
          if (e.NeverEmitted) {
            if (TryEmit(e, cameraLocal)) { e.NeverEmitted = false; e.NextEmit = e.Clock + EmitPeriod(e); }
          } else {
            e.StepAccum += dt; int guard = 0;
            while (e.StepAccum >= step && guard++ < 64) {
              e.StepAccum -= step; e.Clock += step;
              if (e.Clock >= e.NextEmit) { e.NextEmit = e.Clock + EmitPeriod(e); TryEmit(e, cameraLocal); }
            }
          }
        }
        for (int i = particles.Count - 1; i >= 0; i--) {
          WorldPrtParticle p = particles[i]; p.Age += dt;
          if (p.Age >= p.Life) {
            string death = p.Spec.Text("EmitAtDeathSpec");
            if (!String.IsNullOrWhiteSpace(death)) SpawnDeath(death, p.Position);
            particles.RemoveAt(i); continue;
          }
          float t = Normalized(p.Age, p.Life);
          float drag = p.Spec.Track("Drag")?.Number(t, random, 0f) ?? 0f; float k = 1f - drag;
          if (Math.Abs(k) > .000001f) {
            float buoyancy = p.Spec.Track("Buoyancy")?.Number(t, random, 0f) ?? 0f;
            Vector3 motion = p.Spec.Track("Motion")?.Vector3(t, random, Vector3.Zero) ?? Vector3.Zero;
            p.Velocity.Y += (1f - buoyancy) * -.5f * dt;
            p.Position += (p.Velocity + motion) * (dt * k);
          }
          p.Rotation += p.RotationVelocity * dt;
          int total = Math.Max(1, (int)Math.Round(p.Spec.Number("RowSize", 1f)) * (int)Math.Round(p.Spec.Number("ColumnSize", 1f)));
          float animationPeriod = p.Spec.Number("AnimationSpeed", 0f);
          if (animationPeriod > 0f && total > 1) {
            p.FrameClock += dt; int guard = 0;
            while (p.FrameClock > animationPeriod && guard++ < 64) {
              p.PreviousFrame = p.Frame; p.Frame = NextFrame(p.Spec, p.Frame, total); p.FrameClock -= animationPeriod;
            }
          }
        }
        if (hideStopped) particles.Clear();
      }

      private void SpawnDeath(string path, Vector3 position) {
        WorldPrtSpec child = loader?.Invoke(path); if (child == null) return;
        if (IsEmitter(child)) SpawnEmitter(child, null, 1, position); else SpawnParticle(child, null, position, Vector3.Zero);
      }

      private bool TryEmit(WorldPrtEmitter e, Vector3 cameraLocal) {
        float maxD = e.Spec.Number("MaxDistanceFromCamera", 0f); if (maxD == 0f) maxD = 20f;
        float minD = e.Spec.Number("MinDistanceFromCamera", 0f); float d2 = (e.Position - cameraLocal).LengthSquared();
        if ((maxD > 0 && d2 > maxD * maxD) || (minD > 0 && d2 < minD * minD)) return false;
        int count = Math.Min(50, Math.Max(e.Spec.Bool("SubFrameParticleDistribution") ? 0 : 1, (int)Math.Floor(e.Spec.Number("NumberToEmit", 1f))));
        if (count <= 0) return false;
        string childName = e.Spec.Text("EmitSpec"); if (String.IsNullOrWhiteSpace(childName)) return false;
        WorldPrtSpec child = loader?.Invoke(childName); if (child == null) return false;
        for (int i = 0; i < count; i++) {
          SampleShape(e.Spec, e.Age, e.Life, out Vector3 offset, out Vector3 direction);
          Vector3 position = e.Position + offset;
          if (IsEmitter(child)) SpawnEmitter(child, e, e.Depth + 1, position);
          else if (child.Type == "FXSPEC") { /* nested FxSpec particles are intentionally deferred to the full host */ }
          else SpawnParticle(child, e, position, direction);
        }
        return true;
      }

      private float EmitPeriod(WorldPrtEmitter e) {
        WorldFxValue frequency = e.Spec.Track("EmitFrequency"); return frequency?.Number(Normalized(e.Age, e.Life), random, 0f) ?? 0f;
      }

      private static float UpdateRateStep(WorldPrtSpec spec) {
        switch ((spec.Text("UpdateRate", "NORMAL") ?? "NORMAL").ToUpperInvariant()) {
          case "LOW": return .1f; case "LOWNORMAL": return .057142857f; case "HIGH": return .016666668f; default: return .04f;
        }
      }

      private int NextFrame(WorldPrtSpec spec, int current, int total) {
        int direction = (int)Math.Floor(spec.Number("FrameMoveDirection", 0f));
        if (direction == 0) return total < 2 ? current : (current + 1 + random.Next(total - 1)) % total;
        int next = current + direction; while (next < 0) next += total; return next % total;
      }

      private void SampleShape(WorldPrtSpec spec, float age, float life, out Vector3 position, out Vector3 direction) {
        position = Vector3.Zero; direction = Vector3.Zero;
        string shape = spec.Type == "DUMMY_EMITTER" ? "POINT" : (spec.Text("EmitterShape", "Point") ?? "Point").ToUpperInvariant();
        float t = Normalized(age, life);
        if (shape == "BOX") {
          Vector3 size = spec.Track("BoxSize")?.Vector3(t, random, new Vector3(1f, 1f, 1f)) ?? new Vector3(1f, 1f, 1f);
          position = new Vector3(((float)random.NextDouble() - .5f) * size.X, ((float)random.NextDouble() - .5f) * size.Y, ((float)random.NextDouble() - .5f) * size.Z);
          direction = position; if (direction.LengthSquared() > 1e-8f) direction.Normalize(); return;
        }
        if (shape == "ELLIPSOID") {
          Vector3 radii = spec.Track("EllipsoidRadii")?.Vector3(t, random, new Vector3(1f, 1f, 1f)) ?? new Vector3(1f, 1f, 1f);
          Vector3 v = new Vector3((float)random.NextDouble() * 2 - 1, (float)random.NextDouble() * 2 - 1, (float)random.NextDouble() * 2 - 1);
          if (v.LengthSquared() > 1e-8f) v.Normalize(); float shell = Math.Max(0f, Math.Min(1f, spec.Track("ShellThickness")?.Number(t, random, 1f) ?? 1f));
          float radial = 1f - shell + shell * (float)random.NextDouble(); position = new Vector3(v.X * radii.X, v.Y * radii.Y, v.Z * radii.Z) * radial; direction = v; return;
        }
        if (shape == "SPRAY") {
          float angle = spec.Track("SprayAngle")?.Number(t, random, 0f) ?? 0f; double phi = random.NextDouble() * Math.PI * 2; double theta = random.NextDouble() * angle * Math.PI / 180.0;
          direction = new Vector3((float)(Math.Sin(theta) * Math.Cos(phi)), (float)(Math.Sin(theta) * Math.Sin(phi)), (float)Math.Cos(theta)); return;
        }
        // HeroEngine's Point shape can still spread by EmitterRadius. This compact host treats it as a sphere.
        float radius = spec.Track("EmitterRadius")?.Number(t, random, 0f) ?? 0f;
        if (radius > 0f) {
          Vector3 v = new Vector3((float)random.NextDouble() * 2 - 1, (float)random.NextDouble() * 2 - 1, (float)random.NextDouble() * 2 - 1);
          if (v.LengthSquared() > 1e-8f) v.Normalize(); position = v * (radius * .5f * (float)random.NextDouble()); direction = v;
        }
      }

      private static float Normalized(float age, float life) => Single.IsInfinity(life) || life <= 0 ? 0f : Math.Max(0f, Math.Min(1f, age / life));
    }

    private sealed class WorldFxNodeRuntime { public WorldFxNodeDefinition Definition; public WorldPrtSystem System; public bool Started, Stopped; }

    private sealed class WorldFxPlayer {
      public object Owner;
      public string Path;
      public WorldFxSpecDefinition Definition;
      public float LastWorldTime;
      public float Clock;
      public float LastSeenWorldTime;
      public bool PreRolled;
      public readonly List<WorldFxNodeRuntime> Nodes = new List<WorldFxNodeRuntime>();
    }

    private sealed class WorldFxRenderParticle {
      public string TexturePath;
      public Vector3 Position;
      public float HalfWidth, HalfHeight, Rotation;
      // SWTOR offsets a billboard's pivot in its already-scaled local X/Y plane. These are not texture UV pivots:
      // they move the rendered quad around the particle center and are used by several overhead icon PRTs.
      public float PivotU, PivotV;
      // The particle renderer also scales sprites by camera distance. Zero-valued distances retain the ordinary 1x
      // scale, matching the PRT defaults.
      public float MinDistance, MaxDistance, ScaleClampDistance, DistanceScaleAdjustment = 1f;
      public int Columns = 1, Rows = 1, Frame;
      public Vector4 Color = new Vector4(1f, 1f, 1f, 1f);
    }

    private readonly Dictionary<string, WorldFxSpecDefinition> worldFxSpecCache = new Dictionary<string, WorldFxSpecDefinition>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, WorldPrtSpec> worldPrtSpecCache = new Dictionary<string, WorldPrtSpec>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<object, WorldFxPlayer> worldFxPlayers = new Dictionary<object, WorldFxPlayer>();
    private float worldFxLastPrune;

    private void ClearWorldFxRuntime() {
      worldFxPlayers.Clear(); worldFxSpecCache.Clear(); worldPrtSpecCache.Clear(); worldFxLastPrune = 0f;
    }

    private bool TryDrawWorldInteractionFxPlayer(WorldInteractionIconEntry entry, string fxSpecPath, Matrix viewProj, out bool drawn) {
      drawn = false; if (entry?.Owner == null || String.IsNullOrWhiteSpace(fxSpecPath) || area == null || camera == null) return false;
      WorldFxSpecDefinition definition = LoadWorldFxSpecDefinition(fxSpecPath); if (definition == null || definition.SupportedEmitterCount == 0) return false;
      // Only claim ownership when at least one authored emitter really resolves to a parseable PRT. This keeps the
      // legacy sprite extraction reachable for incomplete beta resources and nested particle types we do not host yet.
      if (!definition.Nodes.Any(x => !x.IsGroup && !String.IsNullOrWhiteSpace(x.Resource) && LoadWorldPrtSpec(x.Resource) != null)) return false;
      if (!worldFxPlayers.TryGetValue(entry.Owner, out WorldFxPlayer player) || !String.Equals(player.Path, definition.Path, StringComparison.OrdinalIgnoreCase)) {
        player = CreateWorldFxPlayer(entry.Owner, definition, elapsed); worldFxPlayers[entry.Owner] = player;
      }
      player.LastSeenWorldTime = elapsed;
      Matrix fxFrame = entry.FxFrame ?? Matrix.Translation(entry.Anchor);
      Matrix inverseFx;
      try { inverseFx = Matrix.Invert(fxFrame); }
      catch { inverseFx = Matrix.Translation(-entry.Anchor); }
      Vector3 cameraLocal = Vector3.TransformCoordinate(camera.Position, inverseFx);
      UpdateWorldFxPlayer(player, elapsed, cameraLocal);
      int count = 0;
      foreach (WorldFxRenderParticle particle in BuildWorldFxRenderParticles(player)) {
        if (particle == null || ++count > WorldFxMaxRenderParticlesPerIcon) break;
        // Structured FXSPEC/PRT effects already author their offset in the attachment frame. The old -24px lift was
        // only for flat fallback sprites and double-offsets real SWTOR markers.
        drawn |= DrawWorldFxParticle(particle, fxFrame, 0f, viewProj);
      }
      if (elapsed - worldFxLastPrune > 2f) {
        worldFxLastPrune = elapsed;
        foreach (object stale in worldFxPlayers.Where(x => elapsed - x.Value.LastSeenWorldTime > 5f).Select(x => x.Key).ToArray()) worldFxPlayers.Remove(stale);
      }
      return true; // parsed and owned by the real runtime even if the authored effect is momentarily between particles
    }

    private WorldFxPlayer CreateWorldFxPlayer(object owner, WorldFxSpecDefinition definition, float worldTime) {
      var player = new WorldFxPlayer { Owner = owner, Path = definition.Path, Definition = definition, LastWorldTime = worldTime, LastSeenWorldTime = worldTime };
      foreach (WorldFxNodeDefinition node in definition.Nodes.Where(x => !x.IsGroup && !String.IsNullOrWhiteSpace(x.Resource))) player.Nodes.Add(new WorldFxNodeRuntime { Definition = node });
      return player;
    }

    private void UpdateWorldFxPlayer(WorldFxPlayer player, float worldTime, Vector3 cameraLocal) {
      if (player == null) return;
      if (!player.PreRolled) {
        player.PreRolled = true;
        float left = WorldFxPreRollSeconds;
        while (left > 0f) { float step = Math.Min(WorldFxPreRollStep, left); StepWorldFxPlayer(player, step, cameraLocal); left -= step; }
        player.LastWorldTime = worldTime; return;
      }
      float dt = worldTime - player.LastWorldTime; player.LastWorldTime = worldTime;
      if (dt < 0f || dt > .5f) { ResetWorldFxPlayer(player); return; }
      while (dt > 0f) { float step = Math.Min(WorldFxMaxFrameStep, dt); StepWorldFxPlayer(player, step, cameraLocal); dt -= step; }
    }

    private void ResetWorldFxPlayer(WorldFxPlayer player) {
      player.Clock = 0; player.PreRolled = false; foreach (WorldFxNodeRuntime node in player.Nodes) { node.System = null; node.Started = node.Stopped = false; }
    }

    private void StepWorldFxPlayer(WorldFxPlayer player, float dt, Vector3 cameraLocal) {
      if (player == null || dt <= 0f) return;
      player.Clock += dt;
      float cycle = player.Definition.GroupEnd;
      if (!Single.IsInfinity(cycle) && cycle > .05f && player.Clock > cycle) {
        player.Clock %= cycle; foreach (WorldFxNodeRuntime runtime in player.Nodes) { runtime.System = null; runtime.Started = runtime.Stopped = false; }
      }
      foreach (WorldFxNodeRuntime runtime in player.Nodes) {
        WorldFxNodeDefinition node = runtime.Definition;
        if (!runtime.Started && player.Clock >= node.StartAt && !Single.IsInfinity(node.StartAt)) {
          runtime.Started = true; WorldPrtSpec root = LoadWorldPrtSpec(node.Resource);
          if (root != null) runtime.System = new WorldPrtSystem(root, LoadWorldPrtSpec, (uint)(node.Name?.GetHashCode() ?? 1), node.LifeModifier);
        }
        if (!runtime.Stopped && player.Clock >= node.StopAt && !Single.IsInfinity(node.StopAt)) { runtime.Stopped = true; runtime.System?.Stop(node.HideOnStop); }
        runtime.System?.Update(dt, cameraLocal - node.Offset);
      }
    }

    private IEnumerable<WorldFxRenderParticle> BuildWorldFxRenderParticles(WorldFxPlayer player) {
      if (player == null) yield break;
      foreach (WorldFxNodeRuntime runtime in player.Nodes) {
        WorldFxNodeDefinition node = runtime.Definition; WorldPrtSystem system = runtime.System; if (system == null) continue;
        foreach (WorldPrtParticle p in system.Particles) {
          WorldPrtSpec spec = p.Spec; string texture = NormalizeWorldOverheadTexturePath(spec.Text("TextureName"));
          if (String.IsNullOrWhiteSpace(texture) || !WorldOverheadTextureExists(texture)) continue;
          float t = p.Life > 0 ? Math.Max(0f, Math.Min(1f, p.Age / p.Life)) : 0f;
          var seeded = new WorldFxRandom(p.Seed);
          float size = spec.Track("Size2D")?.Number(t, seeded, .01f) ?? .01f; if (size < .00001f) continue; if (size > 1000f) size = 1f;
          float sx = p.SizeX, sy = p.SizeY;
          if (spec.Bool("ScaleEachUpdate", true) && spec.Track("XScale2D") is WorldFxValue xTrack && spec.Track("YScale2D") is WorldFxValue yTrack) {
            sx = xTrack.Number(t, seeded, sx); sy = yTrack.Number(t, seeded, sy);
          }
          Vector4 color = spec.Track("DiffuseColor")?.Color(t, seeded, new Vector4(1f, 1f, 1f, 1f)) ?? new Vector4(1f, 1f, 1f, 1f);
          color = new Vector4(color.X * node.Tint.X, color.Y * node.Tint.Y, color.Z * node.Tint.Z, color.W * node.Tint.W);
          int columns = Math.Max(1, (int)Math.Round(spec.Number("RowSize", 1f))), rows = Math.Max(1, (int)Math.Round(spec.Number("ColumnSize", 1f)));
          Vector3 pos = RotateWorldFxVector(new Vector3(p.Position.X * node.Scale.X, p.Position.Y * node.Scale.Y, p.Position.Z * node.Scale.Z), node.Rotation) + node.Offset;
          yield return new WorldFxRenderParticle {
            TexturePath = texture, Position = pos,
            HalfWidth = Math.Abs(size * sx * node.Scale.X), HalfHeight = Math.Abs(size * sy * node.Scale.Y), Rotation = p.Rotation,
            PivotU = spec.Number("UPivotOffset", 0f), PivotV = spec.Number("VPivotOffset", 0f),
            MinDistance = spec.Number("MinDistanceFromCamera", 0f), MaxDistance = spec.Number("MaxDistanceFromCamera", 0f),
            ScaleClampDistance = spec.Number("ScaleClampDistance", 0f), DistanceScaleAdjustment = spec.Number("DistanceScaleAdjustment", 1f),
            Columns = columns, Rows = rows, Frame = p.Frame, Color = color
          };
        }
      }
    }

    private bool DrawWorldFxParticle(WorldFxRenderParticle particle, Matrix fxFrame, float screenYOffset, Matrix viewProj) {
      if (particle == null || particle.HalfWidth <= 0f || particle.HalfHeight <= 0f || camera == null) return false;
      MapNoteIconGpu gpu = EnsureWorldOverheadTextureGpu(particle.TexturePath, 6); if (gpu?.Texture == null || gpu.Buffer == null) return false;
      // Particle simulation stays in FXSPEC-local space. Apply the complete attachment transform only here so an
      // authored local -Z offset follows NamePlate bone orientation instead of becoming an arbitrary world-Z shift.
      Vector3 center = Vector3.TransformCoordinate(particle.Position, fxFrame);

      // FxPlayer multiplies an element attached to a bone by that bone frame's scale. PugTools previously transformed
      // the particle POSITION through fxFrame but left the billboard extent unscaled, so template/body scale changes
      // moved an icon correctly while leaving it visibly too large or too small. Row lengths are the local axes in
      // SlimDX's row-vector convention.
      float attachScaleX = (float)Math.Sqrt(fxFrame.M11 * fxFrame.M11 + fxFrame.M12 * fxFrame.M12 + fxFrame.M13 * fxFrame.M13);
      float attachScaleY = (float)Math.Sqrt(fxFrame.M21 * fxFrame.M21 + fxFrame.M22 * fxFrame.M22 + fxFrame.M23 * fxFrame.M23);
      if (!(attachScaleX > .000001f) || Single.IsNaN(attachScaleX) || Single.IsInfinity(attachScaleX)) attachScaleX = 1f;
      if (!(attachScaleY > .000001f) || Single.IsNaN(attachScaleY) || Single.IsInfinity(attachScaleY)) attachScaleY = 1f;

      float distanceScale = WorldPrtCameraDistanceScale(particle, (center - camera.Position).Length());
      if (!(distanceScale > .000001f)) return false;
      float halfWidth = particle.HalfWidth * attachScaleX * distanceScale;
      float halfHeight = particle.HalfHeight * attachScaleY * distanceScale;
      if (!(halfWidth > .000001f) || !(halfHeight > .000001f)) return false;

      if (Math.Abs(screenYOffset) > .001f) {
        Vector3 forward = -camera.Look; if (forward.LengthSquared() > .000001f) { forward.Normalize(); float depth = Vector3.Dot(center - camera.Position, forward);
          if (depth > .001f) { float worldPerPixel = (float)(2.0 * depth * Math.Tan(camera.FovY * .5f) / Math.Max(1f, Viewport.Height)); center += camera.Up * (-screenYOffset * worldPerPixel); }
        }
      }
      float c = (float)Math.Cos(particle.Rotation), s = (float)Math.Sin(particle.Rotation);
      // SWTOR rotates the already-scaled half-extents about the NEGATED quad normal:
      //   rc = (x*cos + y*sin, y*cos - x*sin)
      // Keep that sign convention here rather than the ordinary 2-D rotation PugTools used before. It matters for
      // pivoted/rotating markers and makes the CPU quad algebraically identical to Jedipedia's PRT vertex shader.
      Vector3 rightAxis = camera.Right * c - camera.Up * s;
      Vector3 upAxis = camera.Right * s + camera.Up * c;

      // Jedipedia's shared PRT shader computes `corner * 2 - pivot` BEFORE the in-plane rotation. Expressing that as
      // a shifted center plus the same symmetric extents is algebraically identical and keeps the existing six-vertex
      // path. This is the small but visible offset the overhead icons were still missing.
      center += rightAxis * (-particle.PivotU * halfWidth) + upAxis * (-particle.PivotV * halfHeight);
      Vector3 right = rightAxis * halfWidth, up = upAxis * halfHeight;
      Vector3 tl = center - right + up, tr = center + right + up, br = center + right - up, bl = center - right - up;
      int columns = Math.Max(1, particle.Columns), rows = Math.Max(1, particle.Rows), total = Math.Max(1, columns * rows), frame = Math.Max(0, Math.Min(total - 1, particle.Frame));
      int col = frame % columns, row = frame / columns; float u0 = col / (float)columns, v0 = row / (float)rows, u1 = (col + 1) / (float)columns, v1 = (row + 1) / (float)rows;
      Vector3 normal = -camera.Look, tangent = rightAxis;
      var vertices = new[] {
        new PosNormalTexTan(tl, normal, new Vector2(u0,v0), tangent), new PosNormalTexTan(tr, normal, new Vector2(u1,v0), tangent), new PosNormalTexTan(br, normal, new Vector2(u1,v1), tangent),
        new PosNormalTexTan(tl, normal, new Vector2(u0,v0), tangent), new PosNormalTexTan(br, normal, new Vector2(u1,v1), tangent), new PosNormalTexTan(bl, normal, new Vector2(u0,v1), tangent)
      };
      try {
        DataBox mapped = ImmediateContext.MapSubresource(gpu.Buffer, MapMode.WriteDiscard, SlimDX.Direct3D11.MapFlags.None); mapped.Data.WriteRange(vertices); ImmediateContext.UnmapSubresource(gpu.Buffer, 0);
        ImmediateContext.InputAssembler.InputLayout = inputLayout; ImmediateContext.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
        ImmediateContext.InputAssembler.SetVertexBuffers(0, new VertexBufferBinding(gpu.Buffer, PosNormalTexTan.Stride, 0));
        fx.SetWorld(Matrix.Identity); fx.SetViewProj(viewProj); fx.SetPlaceableBlueGlow(false); fx.SetMapArt(gpu.Texture, 1f, particle.Color);
        fx.MapArt.GetPassByIndex(0).Apply(ImmediateContext); ImmediateContext.Draw(6, 0); fx.ClearMapArt(); return true;
      } catch (Exception ex) { System.Diagnostics.Debug.WriteLine("World FXSPEC particle draw failed: " + ex.Message); return false; }
    }


    private static float WorldPrtCameraDistanceScale(WorldFxRenderParticle particle, float distance) {
      if (particle == null || Single.IsNaN(distance) || Single.IsInfinity(distance) || distance < 0f) return 1f;
      float clamp = Math.Max(0f, particle.ScaleClampDistance);
      float maxDistance = Math.Max(0f, particle.MaxDistance);
      float low = Math.Max(clamp, Math.Max(0f, particle.MinDistance));
      float scale = 1f;
      if (maxDistance > 0f) {
        if (distance > maxDistance) scale = 0f;
        else if (distance >= low && maxDistance > low + .000001f) {
          float u = (distance - low) / (maxDistance - low);
          float t = u * u;
          scale = t * particle.DistanceScaleAdjustment + (1f - t);
        }
      }
      if (clamp > .000001f && distance < clamp) scale = Math.Min(distance / clamp, scale);
      return Math.Max(0f, scale);
    }

    private WorldFxSpecDefinition LoadWorldFxSpecDefinition(string requestedPath) {
      string path = NormalizeWorldOverheadResourcePath(requestedPath, ".fxspec"); if (String.IsNullOrWhiteSpace(path)) return null;
      if (worldFxSpecCache.TryGetValue(path, out WorldFxSpecDefinition cached)) return cached;
      WorldFxSpecDefinition result = null;
      try {
        using TorArchive.File file = area?.FindFile(path); if (file == null) return null;
        string xml = ReadWorldFxText(file); if (String.IsNullOrWhiteSpace(xml)) return null;
        XDocument doc = XDocument.Parse(xml, LoadOptions.None); XElement marshal = doc.Descendants().FirstOrDefault(x => x.Name.LocalName == "marshalData"); XElement root = marshal?.Elements().FirstOrDefault(x => x.Name.LocalName == "node");
        if (root == null) return null;
        result = new WorldFxSpecDefinition { Path = path };
        XElement masterField = root.Elements().FirstOrDefault(x => x.Name.LocalName == "f" && String.Equals((string)x.Attribute("name"), "_fxMasterGroup", StringComparison.Ordinal));
        Dictionary<string, string> master = ParseWorldFxFieldMap(masterField);
        result.MasterName = FxString(master, "_fxName", "PARENT"); result.TimerType = FxString(master, "_fxTimerType", "NEVERFIRE"); result.FireRate = ParseWorldFxTime(FxString(master, "_fxFireRate"));
        float timeout = ParseWorldFxTime(FxString(master, "_fxTimeOut"));
        if (timeout > 0) result.GroupEnd = timeout; else if (String.Equals(result.TimerType, "ONESHOT", StringComparison.OrdinalIgnoreCase) && result.FireRate > 0) result.GroupEnd = result.FireRate;
        ParseWorldFxNodeList(root, "_fxEmitterList", false, result.Nodes);
        ParseWorldFxNodeList(root, "_fxList", true, result.Nodes);
        ResolveWorldFxNodeTransforms(result);
        ResolveWorldFxTimings(result);
      } catch (Exception ex) { System.Diagnostics.Debug.WriteLine("SWTOR structured FXSPEC parse failed " + path + ": " + ex.Message); result = null; }
      if (result != null) worldFxSpecCache[path] = result; return result;
    }

    private void ParseWorldFxNodeList(XElement root, string fieldName, bool group, List<WorldFxNodeDefinition> output) {
      XElement list = root.Elements().FirstOrDefault(x => x.Name.LocalName == "f" && String.Equals((string)x.Attribute("name"), fieldName, StringComparison.Ordinal));
      if (list == null) return; int index = 0;
      foreach (XElement entry in list.Elements().Where(x => x.Name.LocalName == "e")) {
        Dictionary<string, string> fields = ParseWorldFxFieldMap(entry); if (fields.Count == 0) continue;
        var node = new WorldFxNodeDefinition { Fields = fields, IsGroup = group };
        node.Name = FxString(fields, "_fxName", (group ? "group#" : "emitter#") + index++); node.Resource = FxString(fields, "_fxResourceName");
        node.WhenToStart = FxString(fields, "_fxWhenToStart", "ONFXSTART").ToUpperInvariant(); node.StartFxName = FxString(fields, "_fxStartFxName"); node.StartDelay = ParseWorldFxTime(FxString(fields, "_fxStartDelay"));
        node.WhenToStop = FxString(fields, "_fxWhenToStop", "ONFXSTOP").ToUpperInvariant(); node.StopFxName = FxString(fields, "_fxStopFxName"); node.StopDelay = ParseWorldFxTime(FxString(fields, "_fxStopDelay")); node.OwnerName = FxString(fields, "_fxOwnerName");
        node.AttachTo = FxString(fields, "_fxAttachTo");
        node.StartLoc = FxString(fields, "_fxStartLoc");
        bool attached = !String.IsNullOrWhiteSpace(node.AttachTo);
        node.Offset = ParseWorldFxVector(FxString(fields, attached ? "_fxAttachPosition" : "_fxStartLocOffset"), Vector3.Zero);
        node.Scale = ParseWorldFxVector(FxString(fields, "_fxScale"), new Vector3(1f, 1f, 1f));
        // _fxAttachRotation/_fxRotation are D3DX YawPitchRoll triples. Convert once at the file boundary to the
        // X->Y->Z Euler convention used by RotateWorldFxVector and by Jedipedia below its parser.
        node.Rotation = WorldFxAuthoredYprToEulerDegrees(ParseWorldFxVector(FxString(fields, attached ? "_fxAttachRotation" : "_fxRotation"), Vector3.Zero));
        node.Tint = ParseWorldFxColor(FxString(fields, "_fxTintColor"), new Vector4(1f, 1f, 1f, 1f)); Vector4 diffuse = ParseWorldFxColor(FxString(fields, "_fxDiffuseColor"), new Vector4(1f, 1f, 1f, 1f));
        node.Tint = new Vector4(node.Tint.X * diffuse.X, node.Tint.Y * diffuse.Y, node.Tint.Z * diffuse.Z, node.Tint.W * diffuse.W);
        if (Single.TryParse(FxString(fields, "_fxParticleLifeSpanModifier"), NumberStyles.Float, CultureInfo.InvariantCulture, out float lifeMod) && lifeMod > 0) node.LifeModifier = lifeMod;
        node.HideOnStop = !group && String.Equals(FxString(fields, "_fxHowToStop"), "HIDE", StringComparison.OrdinalIgnoreCase); output.Add(node);
      }
    }

    // FxSpec lists are grouped by element KIND rather than by hierarchy, so a child can appear before its parent.
    // Resolve named StartLoc/AttachTo anchors depth-first, matching fxspec-render.js. AttachTo inherits position,
    // rotation and scale; StartLoc inherits position only. CASTER/TARGET/DEFAULT remain rooted on the host frame.
    private static void ResolveWorldFxNodeTransforms(WorldFxSpecDefinition definition) {
      if (definition?.Nodes == null || definition.Nodes.Count == 0) return;
      var byName = definition.Nodes
        .Where(x => x != null && !String.IsNullOrWhiteSpace(x.Name))
        .GroupBy(x => x.Name.Trim(), StringComparer.OrdinalIgnoreCase)
        .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
      var resolved = new HashSet<WorldFxNodeDefinition>();
      var resolving = new HashSet<WorldFxNodeDefinition>();

      Action<WorldFxNodeDefinition> resolve = null;
      resolve = node => {
        if (node == null || resolved.Contains(node) || resolving.Contains(node)) return;
        resolving.Add(node);
        ResolveWorldFxNodeAnchor(node, false, node.StartLoc, byName, resolve);
        // AttachTo wins over StartLoc when both are authored, exactly like the engine/Jedipedia second pass.
        ResolveWorldFxNodeAnchor(node, true, node.AttachTo, byName, resolve);
        resolving.Remove(node);
        resolved.Add(node);
      };
      foreach (WorldFxNodeDefinition node in definition.Nodes) resolve(node);
    }

    private static void ResolveWorldFxNodeAnchor(WorldFxNodeDefinition node, bool attach, string reference,
        Dictionary<string, WorldFxNodeDefinition> byName, Action<WorldFxNodeDefinition> resolve) {
      string name = (reference ?? String.Empty).Trim();
      if (name.Length == 0 || WorldFxExternalAnchor(name) || byName == null || !byName.TryGetValue(name, out WorldFxNodeDefinition parent) ||
          parent == null || ReferenceEquals(parent, node)) return;
      resolve?.Invoke(parent);

      Vector3 offset = ParseWorldFxVector(FxString(node.Fields, attach ? "_fxAttachPosition" : "_fxStartLocOffset"), Vector3.Zero);
      if (attach) {
        offset = new Vector3(offset.X * parent.Scale.X, offset.Y * parent.Scale.Y, offset.Z * parent.Scale.Z);
        node.Scale = new Vector3(node.Scale.X * parent.Scale.X, node.Scale.Y * parent.Scale.Y, node.Scale.Z * parent.Scale.Z);
      }
      offset = RotateWorldFxVector(offset, parent.Rotation);
      node.Offset = parent.Offset + offset;
      if (attach) node.Rotation = ComposeWorldFxEulerDegrees(parent.Rotation, node.Rotation);
    }

    private static bool WorldFxExternalAnchor(string name) {
      return String.Equals(name, "DEFAULT", StringComparison.OrdinalIgnoreCase) ||
        String.Equals(name, "CASTER", StringComparison.OrdinalIgnoreCase) ||
        String.Equals(name, "TARGET", StringComparison.OrdinalIgnoreCase) ||
        String.Equals(name, "NOTHING", StringComparison.OrdinalIgnoreCase);
    }

    private static Dictionary<string, string> ParseWorldFxFieldMap(XElement container) {
      var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); if (container == null) return result;
      foreach (XElement field in container.Elements().Where(x => x.Name.LocalName == "f")) {
        string name = (string)field.Attribute("name"); if (String.IsNullOrWhiteSpace(name)) continue;
        if (!field.Elements().Any()) result[name] = (field.Value ?? String.Empty).Trim();
      }
      return result;
    }

    private static string FxString(Dictionary<string, string> map, string key, string fallback = "") => map != null && map.TryGetValue(key, out string value) ? value?.Trim() ?? fallback : fallback;

    private static float ParseWorldFxTime(string text) {
      if (String.IsNullOrWhiteSpace(text)) return 0f; string[] parts = text.Trim().Split(':'); double seconds = 0, multiplier = 1;
      for (int i = parts.Length - 1; i >= 0; i--) { if (Double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out double value)) seconds += value * multiplier; multiplier *= 60; }
      return (float)seconds;
    }

    private static Vector3 ParseWorldFxVector(string text, Vector3 fallback) {
      string s = (text ?? String.Empty).Trim(); if (s.Length == 0) return fallback;
      if (s.IndexOf(',') >= 0 && !s.StartsWith("(", StringComparison.Ordinal)) s = "(" + s + ")";
      WorldFxValue value = WorldFxValue.Parse(s); return value.Vector3(0, new WorldFxRandom(5), fallback);
    }

    private static Vector4 ParseWorldFxColor(string text, Vector4 fallback) {
      WorldFxValue value = WorldFxValue.Parse(text); return value.Color(0, new WorldFxRandom(5), fallback);
    }

    // Convert D3DXMatrixRotationYawPitchRoll(Y=y, Pitch=x, Roll=z) to this runtime's Rz*Ry*Rx Euler triple.
    private static Vector3 WorldFxAuthoredYprToEulerDegrees(Vector3 degrees) {
      if (Math.Abs(degrees.Z) < .000001f || (Math.Abs(degrees.X) < .000001f && Math.Abs(degrees.Y) < .000001f)) return degrees;
      return ComposeWorldFxEulerDegrees(new Vector3(degrees.X, degrees.Y, 0f), new Vector3(0f, 0f, degrees.Z));
    }

    // Compose two X->Y->Z Euler frames. The result rotates by b first, then a, matching fxrComposeEuler().
    private static Vector3 ComposeWorldFxEulerDegrees(Vector3 aDegrees, Vector3 bDegrees) {
      const double D = Math.PI / 180.0;
      double ax = aDegrees.X * D, ay = aDegrees.Y * D, az = aDegrees.Z * D;
      double bx = bDegrees.X * D, by = bDegrees.Y * D, bz = bDegrees.Z * D;
      double acx = Math.Cos(ax), asx = Math.Sin(ax), acy = Math.Cos(ay), asy = Math.Sin(ay), acz = Math.Cos(az), asz = Math.Sin(az);
      double bcx = Math.Cos(bx), bsx = Math.Sin(bx), bcy = Math.Cos(by), bsy = Math.Sin(by), bcz = Math.Cos(bz), bsz = Math.Sin(bz);
      double[] A = {
        acz * acy, acz * asy * asx - asz * acx, acz * asy * acx + asz * asx,
        asz * acy, asz * asy * asx + acz * acx, asz * asy * acx - acz * asx,
        -asy, acy * asx, acy * acx
      };
      double[] B = {
        bcz * bcy, bcz * bsy * bsx - bsz * bcx, bcz * bsy * bcx + bsz * bsx,
        bsz * bcy, bsz * bsy * bsx + bcz * bcx, bsz * bsy * bcx - bcz * bsx,
        -bsy, bcy * bsx, bcy * bcx
      };
      double[] C = new double[9];
      for (int i = 0; i < 3; i++)
        for (int j = 0; j < 3; j++)
          C[i * 3 + j] = A[i * 3] * B[j] + A[i * 3 + 1] * B[3 + j] + A[i * 3 + 2] * B[6 + j];
      double x = Math.Atan2(C[7], C[8]);
      double y = Math.Asin(Math.Max(-1.0, Math.Min(1.0, -C[6])));
      double z = Math.Atan2(C[3], C[0]);
      const double R = 180.0 / Math.PI;
      return new Vector3((float)(x * R), (float)(y * R), (float)(z * R));
    }

    private static Vector3 RotateWorldFxVector(Vector3 value, Vector3 degrees) {
      float rx = degrees.X * (float)Math.PI / 180f, ry = degrees.Y * (float)Math.PI / 180f, rz = degrees.Z * (float)Math.PI / 180f;
      if (Math.Abs(rx) > .000001f) { float c = (float)Math.Cos(rx), s = (float)Math.Sin(rx); float y = value.Y * c - value.Z * s, z = value.Y * s + value.Z * c; value.Y = y; value.Z = z; }
      if (Math.Abs(ry) > .000001f) { float c = (float)Math.Cos(ry), s = (float)Math.Sin(ry); float x = value.X * c + value.Z * s, z = -value.X * s + value.Z * c; value.X = x; value.Z = z; }
      if (Math.Abs(rz) > .000001f) { float c = (float)Math.Cos(rz), s = (float)Math.Sin(rz); float x = value.X * c - value.Y * s, y = value.X * s + value.Y * c; value.X = x; value.Y = y; }
      return value;
    }

    private static void ResolveWorldFxTimings(WorldFxSpecDefinition definition) {
      if (definition == null) return;
      var byName = definition.Nodes.Where(x => !String.IsNullOrWhiteSpace(x.Name)).GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
      for (int pass = 0; pass < 24; pass++) {
        bool changed = false;
        foreach (WorldFxNodeDefinition node in definition.Nodes) {
          float oldStart = node.StartAt, oldStop = node.StopAt; float start = node.StartDelay;
          if (node.WhenToStart == "NEVER") start = Single.PositiveInfinity;
          else if (!String.IsNullOrWhiteSpace(node.StartFxName) && byName.TryGetValue(node.StartFxName, out WorldFxNodeDefinition startRef)) {
            if (node.WhenToStart == "ONFXSTOP") start = SafeWorldFxTime(startRef.StopAt, node.StartDelay);
            else if (node.WhenToStart == "ONFXSTART") start = SafeWorldFxTime(startRef.StartAt, node.StartDelay);
          }
          node.StartAt = start;
          float stop = Single.PositiveInfinity;
          if (node.WhenToStop == "NEVER") stop = Single.PositiveInfinity;
          else if (!String.IsNullOrWhiteSpace(node.StopFxName) && byName.TryGetValue(node.StopFxName, out WorldFxNodeDefinition stopRef)) {
            if (node.WhenToStop == "ONFXSTART") stop = SafeWorldFxTime(stopRef.StartAt, node.StopDelay);
            else if (node.WhenToStop == "ONFXSTOP") stop = SafeWorldFxTime(stopRef.StopAt, node.StopDelay);
          } else if (node.WhenToStop == "ONFXTIMEOUT") {
            float timeout = ParseWorldFxTime(FxString(node.Fields, "_fxTimeOut"));
            if (timeout > 0) stop = SafeWorldFxTime(node.StartAt, timeout + node.StopDelay);
            else if (!String.IsNullOrWhiteSpace(node.OwnerName) && byName.TryGetValue(node.OwnerName, out WorldFxNodeDefinition owner) && owner.IsGroup) stop = SafeWorldFxTime(owner.StopAt, node.StopDelay);
            else if (!Single.IsInfinity(definition.GroupEnd)) stop = definition.GroupEnd + node.StopDelay;
          } else if (node.WhenToStop == "ONFXSTOP" && !Single.IsInfinity(definition.GroupEnd)) stop = definition.GroupEnd + node.StopDelay;
          node.StopAt = stop;
          if (!SameWorldFxTime(oldStart, node.StartAt) || !SameWorldFxTime(oldStop, node.StopAt)) changed = true;
        }
        if (!changed) break;
      }
    }

    private static float SafeWorldFxTime(float basis, float add) => Single.IsInfinity(basis) ? Single.PositiveInfinity : basis + add;
    private static bool SameWorldFxTime(float a, float b) => (Single.IsInfinity(a) && Single.IsInfinity(b)) || Math.Abs(a - b) < .00001f;

    private WorldPrtSpec LoadWorldPrtSpec(string requested) {
      if (String.IsNullOrWhiteSpace(requested) || area == null) return null;
      foreach (string path in WorldOverheadPrtCandidates(requested)) {
        if (worldPrtSpecCache.TryGetValue(path, out WorldPrtSpec cached)) return cached;
        try {
          using TorArchive.File file = area.FindFile(path); if (file == null) continue;
          string text = ReadWorldFxText(file); WorldPrtSpec spec = WorldPrtSpec.Parse(path, text); if (spec != null) { worldPrtSpecCache[path] = spec; return spec; }
        } catch (Exception ex) { System.Diagnostics.Debug.WriteLine("SWTOR PRT runtime parse failed " + path + ": " + ex.Message); }
      }
      return null;
    }

    private static string ReadWorldFxText(TorArchive.File file) {
      if (file == null) return null; using Stream stream = file.OpenCopyInMemory(); byte[] bytes = new byte[stream.Length]; int read = 0;
      while (read < bytes.Length) { int got = stream.Read(bytes, read, bytes.Length - read); if (got <= 0) break; read += got; }
      if (read <= 0) return null; bool utf16 = read >= 2 && ((bytes[0] == 0xFF && bytes[1] == 0xFE) || bytes[1] == 0);
      return (utf16 ? Encoding.Unicode : Encoding.UTF8).GetString(bytes, 0, read).TrimStart('\uFEFF').TrimEnd('\0');
    }
  }
}
