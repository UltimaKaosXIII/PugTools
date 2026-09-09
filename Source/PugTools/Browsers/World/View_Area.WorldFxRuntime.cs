using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using FileFormats;
using SlimDX;
using SlimDX.Direct3D11;
using SlimDXNet.Vertex;

namespace PugTools {
  internal sealed partial class View_AREA {
    // A deliberately small, world-hosted port of Jedipedia's fxspec-render.js/prt-sim.js path. It is not a DDS
    // extractor: the FXSPEC marshal is parsed into timed emitter nodes, each node owns a running PRT system, and the
    // PRT system emits/lifetimes/moves/animates real billboard and GRANNY mesh particles. FXSPEC _fxModelList entries
    // are submitted through the same GR2 material pipeline as ordinary world geometry. Unsupported particle classes
    // still fall through to the old resolver so RED/Beta oddities remain visible while this runtime is expanded.
    private const int WorldFxParticleCapPerSystem = 384;
    private const int WorldFxEmitterCapPerSystem = 64;
    private const int WorldFxNestedSpecCapPerSystem = 24;
    private const int WorldFxNestedSpecRenderCapPerPlayer = 16;
    private const int WorldFxNestedSpecDepthCap = 4;
    private const int WorldFxMaxRenderParticlesPerIcon = 96;
    private const int WorldFxMaxRenderParticlesPerWorldEffect = 384;
    private const int WorldFxMaxRenderModelsPerIcon = 12;
    private const int WorldFxMaxRenderModelsPerWorldEffect = 96;
    private const int WorldFxMaxRibbonSegmentsPerPlayer = 320;
    private const int WorldFxMaxProjectorDecalsPerPlayer = 8;
    private const int WorldFxMaxProjectorDecalsPerFrame = 32;
    private const int WorldFxTrailMaxNodes = 100;
    private const int WorldFxLightningMaxSamples = 250;
    private const int WorldFxLightningMaxControlPoints = 128;
    private const float WorldFxPreRollSeconds = 2f;
    private const float WorldFxPreRollStep = 1f / 60f;
    private const float WorldFxMaxFrameStep = 1f / 30f;
    private const float WorldAmbientFxMaxDistance = 20f;
    private const int WorldAmbientFxMaxLive = 40;
    private const int WorldAmbientFxNewPerFrame = 2;
    // Current Jedipedia gives scene-owned VFX their own much larger reach/quota so ambient room FX cannot starve a
    // holocall/fleet/impact the active shot explicitly asked to see. Values are in the viewer's internal world units.
    private const float WorldConversationFxMaxDistance = 200f;
    private const int WorldConversationFxMaxLive = 24;

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

    private sealed class WorldFxDynamicPair { public string Key, Value; }

    private sealed class WorldFxNodeDefinition {
      public string Name, Resource, WhenToStart, StartFxName, WhenToStop, StopFxName, OwnerName;
      // FxSpec elements can be mounted on other named elements. These are not cosmetic references: a quest icon,
      // for example, builds several particle layers in one hierarchy. Resolving every layer against the actor root
      // tears the composite apart (the green flourish remains at NamePlate while the gold glyph rides above it).
      public string AttachTo, StartLoc;
      // Direct CASTER/TARGET AttachTo nodes ride the live actor/bone frame after spawning. StartLoc is spawn-only;
      // this metadata is therefore populated only for AttachTo and stores the frame used when the player was built.
      public string HostFollowAnchor, HostFollowBone;
      public Matrix? HostFollowInitialFrame;
      public float StartDelay, StopDelay, StartAt, StopAt = Single.PositiveInfinity;
      public Vector3 Offset, Scale = new Vector3(1f, 1f, 1f), Rotation;
      public Vector4 Tint = new Vector4(1f, 1f, 1f, 1f);
      public float LifeModifier = 1f;
      public bool HideOnStop;
      public bool IsGroup;
      public bool IsModel, IsFader, IsTransformer, IsAttacher, IsArc, IsLightning, IsLight, IsSound, IsProjector, IsEmitter;
      public bool IsChaser;
      public Vector3 ChaseDirection;
      public float ChaseSpeed, ChaseReach, ChaseTravelTime;
      public bool ModelFadeIn, ModelFadeOut;
      public float ModelFadeInTime, ModelFadeOutTime;
      // _fxCheckDynData enables BOTH substitution and gate semantics for _fxStartIF. Standalone world previews have
      // no caller payload, so gates remain open; callers that do provide a payload get the client's exact ordinal match.
      public bool CheckDynamicData, DynamicGateOpen = true;
      public List<WorldFxDynamicPair> StartIf;
      public Dictionary<string, string> Fields;
    }

    private sealed class WorldFxSpecDefinition {
      public string Path;
      public string MasterName;
      // Per-player host anchors, expressed in the FXSPEC local frame. The parsed cache deliberately leaves this null;
      // player clones receive the current CASTER/TARGET and optional skeleton resolver before external anchors/chases
      // are resolved, matching Jedipedia's FxPlayer opts without baking one actor into the shared definition cache.
      public WorldFxHostContext HostContext;
      public string TimerType;
      public float FireRate;
      public float GroupEnd = Single.PositiveInfinity;
      public bool DynamicGateOpen = true, MasterCheckDynamicData;
      public List<WorldFxDynamicPair> MasterStartIf;
      public Dictionary<string, string> MasterFields;
      public readonly List<WorldFxNodeDefinition> Nodes = new List<WorldFxNodeDefinition>();
      public int SupportedVisualCount => Nodes.Count(x =>
        x != null && (x.IsLightning || x.IsLight || x.IsProjector ||
          (!x.IsGroup && !x.IsFader && !x.IsTransformer && !x.IsAttacher && !x.IsArc && !x.IsSound && !String.IsNullOrWhiteSpace(x.Resource))));
    }

    private sealed class WorldPrtEmitter {
      public WorldPrtSpec Spec;
      public WorldPrtEmitter Parent;
      public int Depth;
      public float Age, Life = Single.PositiveInfinity, Clock, StepAccum, NextEmit;
      public bool NeverEmitted = true, Dead;
      public Vector3 Position, Velocity, Rotation, RotationVelocity;
    }

    private sealed class WorldPrtTrailPoint {
      public Vector3 A, B;
      public float Born;
    }

    private sealed class WorldPrtTrail {
      public readonly List<WorldPrtTrailPoint> Points = new List<WorldPrtTrailPoint>();
      public float LastAdd = -1f, Decay;
      public bool VelocityAligned, Secondary;
      public Vector3 LastPosition, LastDirection;
      public bool HasLastPosition, HasLastDirection;
    }

    private sealed class WorldPrtParticle {
      public WorldPrtSpec Spec;
      public WorldPrtEmitter Owner;
      // LinkChildren lives on the EMITTER. Linked leaves keep their position/velocity in that emitter's local frame
      // and are resolved through the emitter's live position/rotation at draw/trail time, matching prt-sim.js.
      public bool LinkedToOwner;
      public Vector3 Position, Velocity;
      public float Age, Life, Rotation, RotationVelocity, FrameClock;
      public Vector3 Rotation3D, RotationVelocity3D;
      public int Frame, PreviousFrame;
      public float SizeX = 1f, SizeY = 1f;
      public uint Seed;
      public WorldPrtTrail Trail, Trail2;
    }

    private sealed class WorldPrtFxInstance {
      public readonly object Owner = new object();
      public WorldPrtSpec Spec;
      public string Path;
      public WorldPrtEmitter EmitterOwner;
      public bool LinkedToOwner;
      public Vector3 Position, Velocity, Rotation, RotationVelocity;
      // The nested FxPlayer is placed once at the particle's birth pose. SWTOR continues simulating the hidden
      // FXSPEC particle afterwards, but the spawned effect itself is not dragged along with that particle.
      public Vector3 RenderPosition, RenderRotation;
      public float Age, Life;
    }

    private sealed class WorldPrtSystem {
      private readonly Func<string, WorldPrtSpec> loader;
      private readonly WorldFxRandom random;
      private readonly List<WorldPrtEmitter> emitters = new List<WorldPrtEmitter>();
      private readonly List<WorldPrtParticle> particles = new List<WorldPrtParticle>();
      private readonly List<WorldPrtFxInstance> fxInstances = new List<WorldPrtFxInstance>();
      private bool stopped, hideStopped;
      private float lifeModifier = 1f, time;
      public WorldPrtSpec Root { get; }
      public float Time => time;
      public IReadOnlyList<WorldPrtParticle> Particles => particles;
      public IReadOnlyList<WorldPrtFxInstance> FxInstances => fxInstances;

      public WorldPrtSystem(WorldPrtSpec root, Func<string, WorldPrtSpec> loader, uint seed, float lifeModifier) {
        Root = root; this.loader = loader; random = new WorldFxRandom(seed); this.lifeModifier = Math.Max(.001f, lifeModifier);
        if (root == null) return;
        if (IsEmitter(root)) SpawnEmitter(root, null, 0, Vector3.Zero);
        else if (IsFxSpec(root)) SpawnFxInstance(root, null, Vector3.Zero, Vector3.Zero);
        else SpawnParticle(root, null, Vector3.Zero, Vector3.Zero);
      }

      public void Stop(bool hide) { stopped = true; hideStopped = hide; if (hide) { particles.Clear(); fxInstances.Clear(); } }

      private static bool IsEmitter(WorldPrtSpec spec) => spec != null && (spec.Type == "BASIC_EMITTER" || spec.Type == "DUMMY_EMITTER");
      private static bool IsBillboard(WorldPrtSpec spec) => spec != null && (spec.Type.StartsWith("BILLBOARD", StringComparison.OrdinalIgnoreCase) || spec.Type == "QUAD" || spec.Type == "POINT");
      private static bool IsGranny(WorldPrtSpec spec) => spec != null && spec.Type == "GRANNY";
      private static bool IsFxSpec(WorldPrtSpec spec) => spec != null && spec.Type == "FXSPEC";
      private static bool IsVisualParticle(WorldPrtSpec spec) => IsBillboard(spec) || IsGranny(spec);

      private WorldPrtEmitter SpawnEmitter(WorldPrtSpec spec, WorldPrtEmitter parent, int depth, Vector3 position) {
        if (spec == null || depth > 8 || emitters.Count >= WorldFxEmitterCapPerSystem) return null;
        var emitter = new WorldPrtEmitter { Spec = spec, Parent = parent, Depth = depth, Position = position };
        WorldFxValue life = spec.Track("LifeSpan"); if (life != null) emitter.Life = Math.Max(.001f, life.Number(0, random, Single.PositiveInfinity));
        WorldFxValue trajectory = spec.Track("Trajectory"); if (trajectory != null) emitter.Velocity = trajectory.Vector3(0, random, Vector3.Zero);
        WorldFxValue initialRotation = spec.Track("InitialRotation3D"); if (initialRotation != null) emitter.Rotation = initialRotation.Vector3(0f, random, Vector3.Zero) * ((float)Math.PI / 180f);
        WorldFxValue deltaRotation = spec.Track("DeltaRotation3D"); if (deltaRotation != null) emitter.RotationVelocity = deltaRotation.Vector3(0f, random, Vector3.Zero);
        emitters.Add(emitter); return emitter;
      }

      private void SpawnParticle(WorldPrtSpec spec, WorldPrtEmitter owner, Vector3 position, Vector3 outward) {
        if (spec == null || particles.Count >= WorldFxParticleCapPerSystem || !IsVisualParticle(spec)) return;
        bool linked = owner != null && owner.Spec.Bool("LinkChildren");
        var p = new WorldPrtParticle { Spec = spec, Owner = owner, LinkedToOwner = linked, Position = position, Seed = (uint)(random.NextDouble() * UInt32.MaxValue) };
        WorldFxValue life = spec.Track("LifeSpan"); p.Life = Math.Max(.001f, (life?.Number(0, random, 1f) ?? 1f) * lifeModifier);
        WorldFxValue trajectory = spec.Track("Trajectory"); p.Velocity = trajectory?.Vector3(0, random, Vector3.Zero) ?? Vector3.Zero;
        if (owner != null) {
          // Unlinked leaves are baked into the emitter's current world frame at birth. LinkChildren leaves keep both
          // trajectory and shape direction in emitter-local space; ResolveParticlePosition/Velocity carries them
          // through the emitter's LIVE frame at draw time.
          if (!linked) p.Velocity = RotateWorldFxVectorRadians(p.Velocity, owner.Rotation);
          float speed = owner.Spec.Track("InitialParticleVelocity")?.Number(Normalized(owner.Age, owner.Life), random, 0f) ?? owner.Spec.Number("InitialParticleVelocity", 0f);
          p.Velocity += outward * speed;
          // The shipped runtime does not consume ChildrenInheritMomentum for ordinary billboard/GRANNY leaves;
          // Jedipedia only applies that flag to ParticleType=FXSPEC instances. Keep the same split here.
          WorldFxValue initialRot = owner.Spec.Track("InitialRotation2D"); if (initialRot != null) p.Rotation = initialRot.Number(Normalized(owner.Age, owner.Life), random, 0f) * (float)Math.PI / 180f;
        }
        WorldFxValue deltaRot = spec.Track("DeltaRotation2D"); if (deltaRot != null) p.RotationVelocity = deltaRot.Number(0, random, 0f);
        if (IsGranny(spec)) {
          var seeded = new WorldFxRandom(p.Seed);
          WorldFxValue initial3d = spec.Track("InitialRotation3D");
          if (initial3d != null) {
            Vector3 degrees = initial3d.Vector3(0f, seeded, Vector3.Zero);
            p.Rotation3D = degrees * ((float)Math.PI / 180f);
          }
          WorldFxValue delta3d = spec.Track("DeltaRotation3D");
          if (delta3d != null) p.RotationVelocity3D = delta3d.Vector3(0f, seeded, Vector3.Zero); // authored radians/sec
          if (spec.Bool("LieFlat")) { p.Rotation3D = Vector3.Zero; p.RotationVelocity3D = Vector3.Zero; }
          else {
            string axis = (spec.Text("OrientationAxis", "XYZ") ?? "XYZ").Trim().ToUpperInvariant();
            float spin() => (float)(seeded.NextDouble() * Math.PI * 2.0);
            if (axis == "XYZ") p.Rotation3D += new Vector3(spin(), spin(), spin());
            else if (axis == "X") p.Rotation3D.X += spin();
            else if (axis == "Y") p.Rotation3D.Y += spin();
            else if (axis == "Z") p.Rotation3D.Z += spin();
          }
        }
        int columns = Math.Max(1, (int)Math.Round(spec.Number("RowSize", 1f))), rows = Math.Max(1, (int)Math.Round(spec.Number("ColumnSize", 1f)));
        int total = Math.Max(1, columns * rows), start = (int)Math.Floor(spec.Number("StartFrame", 0f));
        if (start < 0) start = random.Next(total); else start = Math.Min(total - 1, start);
        p.Frame = p.PreviousFrame = start;
        WorldFxValue xs = spec.Track("XScale2D"), ys = spec.Track("YScale2D");
        if (xs != null && ys != null) { p.SizeX = xs.Number(0, random, 1f); p.SizeY = ys.Number(0, random, 1f); }
        if (spec.Bool("DoTrail")) {
          bool velocityAligned = spec.Bool("DoSecondaryTrail");
          p.Trail = new WorldPrtTrail { VelocityAligned = velocityAligned, Secondary = false };
          if (velocityAligned) p.Trail2 = new WorldPrtTrail { VelocityAligned = true, Secondary = true };
        }
        particles.Add(p);
      }

      private WorldPrtFxInstance SpawnFxInstance(WorldPrtSpec spec, WorldPrtEmitter owner, Vector3 position, Vector3 outward) {
        if (spec == null || fxInstances.Count >= WorldFxNestedSpecCapPerSystem) return null;
        string path = NormalizeWorldAmbientFxSpecPath(spec.Text("EmitFXSpec")); if (String.IsNullOrWhiteSpace(path)) return null;
        bool linked = owner != null && owner.Spec.Bool("LinkChildren");
        var fxInstance = new WorldPrtFxInstance { Spec = spec, Path = path, EmitterOwner = owner, LinkedToOwner = linked, Position = position };
        WorldFxValue life = spec.Track("LifeSpan"); fxInstance.Life = Math.Max(.001f, (life?.Number(0f, random, 1f) ?? 1f) * lifeModifier);
        fxInstance.Velocity = spec.Track("Trajectory")?.Vector3(0f, random, Vector3.Zero) ?? Vector3.Zero;
        if (owner != null) {
          if (!linked) fxInstance.Velocity = RotateWorldFxVectorRadians(fxInstance.Velocity, owner.Rotation);
          float speed = owner.Spec.Track("InitialParticleVelocity")?.Number(Normalized(owner.Age, owner.Life), random, 0f) ?? owner.Spec.Number("InitialParticleVelocity", 0f);
          fxInstance.Velocity += outward * speed;
          if (owner.Spec.Bool("ChildrenInheritMomentum")) fxInstance.Velocity += owner.Velocity;
          // Fxspec particles inherit the emitter orientation at birth. Their spawned effect is placed ONCE at that
          // birth pose; later particle motion remains simulation state only and does not drag the child FxPlayer.
          fxInstance.Rotation = owner.Rotation;
        }
        WorldFxValue initial = spec.Track("InitialRotation3D");
        if (initial != null) fxInstance.Rotation += initial.Vector3(0f, random, Vector3.Zero) * ((float)Math.PI / 180f);
        WorldFxValue delta = spec.Track("DeltaRotation3D"); if (delta != null) fxInstance.RotationVelocity = delta.Vector3(0f, random, Vector3.Zero);
        fxInstance.RenderPosition = ResolveLinkedPosition(position, owner, linked);
        fxInstance.RenderRotation = fxInstance.Rotation;
        fxInstances.Add(fxInstance); return fxInstance;
      }

      private static Vector3 ResolveLinkedPosition(Vector3 position, WorldPrtEmitter owner, bool linked) {
        if (!linked || owner == null) return position;
        return owner.Position + RotateWorldFxVectorRadians(position, owner.Rotation);
      }

      public Vector3 ResolveParticlePosition(WorldPrtParticle particle) {
        if (particle == null) return Vector3.Zero;
        return ResolveLinkedPosition(particle.Position, particle.Owner, particle.LinkedToOwner);
      }

      public Vector3 ResolveParticleVelocity(WorldPrtParticle particle) {
        if (particle == null) return Vector3.Zero;
        if (!particle.LinkedToOwner || particle.Owner == null) return particle.Velocity;
        return RotateWorldFxVectorRadians(particle.Velocity, particle.Owner.Rotation);
      }

      public Vector3 ResolveParticleOwnerRotation(WorldPrtParticle particle) {
        return particle != null && particle.LinkedToOwner && particle.Owner != null ? particle.Owner.Rotation : Vector3.Zero;
      }

      public void Update(float dt, Vector3 cameraLocal) {
        if (Root == null || dt <= 0) return;
        dt = Math.Min(.1f, dt);
        time += dt;
        for (int eIndex = 0; eIndex < emitters.Count; eIndex++) {
          WorldPrtEmitter e = emitters[eIndex]; if (e.Dead) continue;
          e.Age += dt; e.Position += e.Velocity * dt; e.Rotation += e.RotationVelocity * dt;
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
            if (!String.IsNullOrWhiteSpace(death)) SpawnDeath(death, ResolveParticlePosition(p));
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
          p.Rotation3D += p.RotationVelocity3D * dt;
          if (p.Trail != null) UpdateTrail(p, p.Trail, "TrailVertex1", "TrailVertex2");
          if (p.Trail2 != null) UpdateTrail(p, p.Trail2, "SecondaryTrailVertex1", "SecondaryTrailVertex2");
          int total = Math.Max(1, (int)Math.Round(p.Spec.Number("RowSize", 1f)) * (int)Math.Round(p.Spec.Number("ColumnSize", 1f)));
          float animationPeriod = p.Spec.Number("AnimationSpeed", 0f);
          if (animationPeriod > 0f && total > 1) {
            p.FrameClock += dt; int guard = 0;
            while (p.FrameClock > animationPeriod && guard++ < 64) {
              p.PreviousFrame = p.Frame; p.Frame = NextFrame(p.Spec, p.Frame, total); p.FrameClock -= animationPeriod;
            }
          }
        }
        for (int i = fxInstances.Count - 1; i >= 0; i--) {
          WorldPrtFxInstance nested = fxInstances[i]; nested.Age += dt;
          if (nested.Age >= nested.Life) { fxInstances.RemoveAt(i); continue; }
          float t = Normalized(nested.Age, nested.Life); float buoyancy = nested.Spec.Track("Buoyancy")?.Number(t, random, 0f) ?? 0f;
          nested.Velocity.Y += (1f - buoyancy) * -.5f * dt; nested.Position += nested.Velocity * dt; nested.Rotation += nested.RotationVelocity * dt;
        }
        if (hideStopped) { particles.Clear(); fxInstances.Clear(); }
      }

      private void UpdateTrail(WorldPrtParticle particle, WorldPrtTrail trail, string vertex1Key, string vertex2Key) {
        if (particle == null || trail == null) return;
        float t = Normalized(particle.Age, particle.Life);
        float addRate = Math.Max(0f, particle.Spec.Number("TrailAddRate", 0f));
        WorldFxValue decayTrack = particle.Spec.Track("TrailDecay");
        trail.Decay = Math.Max(0f, decayTrack?.Number(t, random, 0f) ?? particle.Spec.Number("TrailDecay", 0f));

        Vector3 position = ResolveParticlePosition(particle);
        if (trail.LastAdd < 0f || time - trail.LastAdd >= addRate) {
          Vector3 a = particle.Spec.Track(vertex1Key)?.Vector3(t, random, Vector3.Zero) ?? Vector3.Zero;
          Vector3 b = particle.Spec.Track(vertex2Key)?.Vector3(t, random, Vector3.Zero) ?? Vector3.Zero;
          if (trail.VelocityAligned) {
            Vector3 dir = trail.HasLastPosition ? position - trail.LastPosition : Vector3.Zero;
            if (dir.LengthSquared() <= 1e-8f && trail.HasLastDirection) dir = trail.LastDirection;
            if (dir.LengthSquared() <= 1e-8f) dir = ResolveParticleVelocity(particle);
            if (dir.LengthSquared() > 1e-8f) {
              dir.Normalize();
              Vector3 right = new Vector3(-dir.Z, 0f, dir.X);
              if (right.LengthSquared() <= 1e-8f) right = Vector3.UnitX; else right.Normalize();
              Vector3 up = Vector3.Cross(right, dir);
              if (up.LengthSquared() <= 1e-8f) up = Vector3.UnitY; else up.Normalize();
              a = right * a.X + up * a.Y + dir * a.Z;
              b = right * b.X + up * b.Y + dir * b.Z;
              trail.LastDirection = dir; trail.HasLastDirection = true;
            } else if (particle.Owner != null) {
              a = RotateWorldFxVectorRadians(a, particle.Owner.Rotation);
              b = RotateWorldFxVectorRadians(b, particle.Owner.Rotation);
            }
          } else if (particle.Owner != null) {
            a = RotateWorldFxVectorRadians(a, particle.Owner.Rotation);
            b = RotateWorldFxVectorRadians(b, particle.Owner.Rotation);
          }
          a += position; b += position;
          WorldPrtTrailPoint head = trail.Points.Count > 0 ? trail.Points[trail.Points.Count - 1] : null;
          bool same = head != null && (head.A - a).LengthSquared() <= 1e-12f && (head.B - b).LengthSquared() <= 1e-12f;
          if (!same) {
            trail.LastAdd = time;
            trail.Points.Add(new WorldPrtTrailPoint { A = a, B = b, Born = time });
            if (trail.Points.Count > WorldFxTrailMaxNodes) trail.Points.RemoveAt(0);
          }
        }

        trail.LastPosition = position; trail.HasLastPosition = true;
        if (trail.Decay > 0f) {
          while (trail.Points.Count > 1 && time - trail.Points[0].Born > trail.Decay && time - trail.Points[1].Born > trail.Decay)
            trail.Points.RemoveAt(0);
        }
      }

      private void SpawnDeath(string path, Vector3 position) {
        WorldPrtSpec child = loader?.Invoke(path); if (child == null) return;
        if (IsEmitter(child)) SpawnEmitter(child, null, 1, position); else if (IsFxSpec(child)) SpawnFxInstance(child, null, position, Vector3.Zero); else SpawnParticle(child, null, position, Vector3.Zero);
      }

      private bool TryEmit(WorldPrtEmitter e, Vector3 cameraLocal) {
        float maxD = e.Spec.Number("MaxDistanceFromCamera", 0f); if (maxD == 0f) maxD = 20f;
        float minD = e.Spec.Number("MinDistanceFromCamera", 0f); float d2 = (e.Position - cameraLocal).LengthSquared();
        if ((maxD > 0 && d2 > maxD * maxD) || (minD > 0 && d2 < minD * minD)) return false;
        int count = Math.Min(50, Math.Max(e.Spec.Bool("SubFrameParticleDistribution") ? 0 : 1, (int)Math.Floor(e.Spec.Number("NumberToEmit", 1f))));
        if (count <= 0) return false;
        string childName = e.Spec.Text("EmitSpec"); if (String.IsNullOrWhiteSpace(childName)) return false;
        WorldPrtSpec child = loader?.Invoke(childName); if (child == null) return false;
        bool linked = e.Spec.Bool("LinkChildren");
        for (int i = 0; i < count; i++) {
          SampleShape(e.Spec, e.Age, e.Life, out Vector3 offset, out Vector3 direction);
          if (IsEmitter(child)) {
            // Child emitters themselves are spawned in the parent's current world frame. LinkChildren governs the
            // LEAVES emitted by this emitter, not a recursively local emitter tree.
            Vector3 worldOffset = RotateWorldFxVectorRadians(offset, e.Rotation);
            SpawnEmitter(child, e, e.Depth + 1, e.Position + worldOffset);
          } else if (linked) {
            if (IsFxSpec(child)) SpawnFxInstance(child, e, offset, direction);
            else SpawnParticle(child, e, offset, direction);
          } else {
            offset = RotateWorldFxVectorRadians(offset, e.Rotation); direction = RotateWorldFxVectorRadians(direction, e.Rotation);
            Vector3 position = e.Position + offset;
            if (IsFxSpec(child)) SpawnFxInstance(child, e, position, direction);
            else SpawnParticle(child, e, position, direction);
          }
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

      private static Vector3 RotateWorldFxVectorRadians(Vector3 value, Vector3 radians) {
        if (Math.Abs(radians.X) > .000001f) { float c = (float)Math.Cos(radians.X), q = (float)Math.Sin(radians.X); float y = value.Y * c - value.Z * q, z = value.Y * q + value.Z * c; value.Y = y; value.Z = z; }
        if (Math.Abs(radians.Y) > .000001f) { float c = (float)Math.Cos(radians.Y), q = (float)Math.Sin(radians.Y); float x = value.X * c + value.Z * q, z = -value.X * q + value.Z * c; value.X = x; value.Z = z; }
        if (Math.Abs(radians.Z) > .000001f) { float c = (float)Math.Cos(radians.Z), q = (float)Math.Sin(radians.Z); float x = value.X * c - value.Y * q, y = value.X * q + value.Y * c; value.X = x; value.Y = y; }
        return value;
      }

      private static float Normalized(float age, float life) => Single.IsInfinity(life) || life <= 0 ? 0f : Math.Max(0f, Math.Min(1f, age / life));
    }

    private sealed class WorldFxLightningPoint {
      public float T, Rise, LocalX, LocalY;
      public Vector3 Position;
      public bool Seeded;
    }

    private sealed class WorldFxLightningState {
      public uint Seed;
      public WorldFxRandom Random;
      public readonly List<WorldFxLightningPoint> Points = new List<WorldFxLightningPoint>();
      public WorldFxValue EnvelopeShape, LightningShape, SpacingTrack, RecreateTrack, TravelTrack, RotateTrack, RiseTrack,
        SimulationTrack, RandomWidthTrack, UvScrollTrack, TextureTimerTrack, EnvelopeWidthTrack, EnvelopeHeightTrack,
        EnvelopeDepthTrack, LengthColor, RampStartTrack, LengthRangeTrack, DynamicColor;
      public float EnvelopeMaxRadius = .5f, EnvelopeMinRadius = .1f, EnvelopeLength = 5f;
      public float MaxWidth = .05f, MinWidth = .025f, TessellationDistance = .1f, TextureTileDistance = 1f, Tension = 1f;
      public bool EvalRotatePerFrame, EvalRandomWidthPerFrame, EvalUvPerFrame, RandomInitialUv, ResetDynamicColor, Additive = true;
      public string TexturePath, TargetName;
      public Vector3 Start, End;
      public float Length, Spacing = .25f, RecreateRemaining, TextureTimerRemaining, RotationRadians, RandomWidth = 1f,
        UvRate, UvOffset, WidthTaper = 1f, WidthAt = .05f, RadiusAt = .5f, RampStart, InvLengthColorRange = 1f, ColorClock;
      public bool Started;
    }

    private sealed class WorldFxNodeRuntime {
      public WorldFxNodeDefinition Definition;
      public WorldPrtSystem System;
      public GR2 Model;
      public WorldFxLightningState Lightning;
      public bool Started, Stopped;
    }

    private sealed class WorldFxHostContext {
      public Matrix CasterFrame = Matrix.Identity;
      public Matrix TargetFrame = Matrix.Translation(0f, 0f, 2f);
      // Returns a frame in the effect-local coordinate system. null means this host has no matching actor/model bone.
      public Func<string, string, Matrix?> BoneFrame;
    }

    private sealed class WorldFxPlayer {
      public object Owner;
      public string Path;
      public WorldFxSpecDefinition Definition;
      public float LastWorldTime;
      public float Clock;
      public float LastSeenWorldTime;
      public bool PreRolled;
      // Conversation/cinematic effects start on the beat that requested them. They do not receive the ambient
      // two-second warm-up and they do not loop after the authored group end; Play VFX starts a fresh player instead.
      public bool Cinematic;
      public string DynamicDataFingerprint;
      public IReadOnlyDictionary<string, string> DynamicData;
      public readonly List<WorldFxNodeRuntime> Nodes = new List<WorldFxNodeRuntime>();
    }

    private sealed class WorldAmbientFxPlacement {
      public FileFormats.Room Room;
      public FileFormats.AssetInstance Instance;
      public string Path;
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
      // PRT billboard orientation is authored. XYZ is the only fully camera-facing mode; NONE is a fixed
      // world/effect XY plane and X/Y/Z only rotate around the named axis. Treating every sprite as XYZ made
      // forcefields follow the camera. Keep the node rotation so fixed/axis-locked sheets follow their placement.
      public string OrientationAxis = "XYZ";
      public bool LieFlat, AlignToTrajectory;
      public Vector3 NodeRotation;
      // Effect-local velocity used only for AlignToTrajectory. It includes the live particle velocity and, when the
      // leaf itself is stationary, the parent FXSPEC projectile/TransformArc velocity recovered by Jedipedia.
      public Vector3 TrajectoryVelocity;
      public string SourceBlend, DestBlend;
    }

    private sealed class WorldFxRenderModel {
      public GR2 Model;
      public Matrix World;
      public Vector4 Tint = new Vector4(1f, 1f, 1f, 1f);
    }

    private sealed class WorldFxRenderRibbonSegment {
      public string TexturePath;
      public Vector3 A0, B0, A1, B1;
      public float U0, U1, V0 = 0f, V1 = 1f;
      public Vector4 Color = new Vector4(1f, 1f, 1f, 1f);
      public bool Additive = true, Multiply;
    }

    private sealed class WorldFxRenderGlow {
      public Vector3 Position;
      public float Radius;
      public Vector4 Color = new Vector4(1f, 1f, 1f, 1f);
    }

    private sealed class WorldFxProjectorDecal {
      public WorldFxNodeDefinition Node;
      public string TexturePath;
      // Local effect-space projector frame. Scaling is the authored projection half-extents, not the enlarged
      // visibility floor used by the old stand-in volume. The shader inverts World*FxFrame to recover box space.
      public Matrix LocalWorld;
      public Vector4 Color = new Vector4(1f, 1f, 1f, 1f);
      public Vector4 Uv = new Vector4(1f, 1f, 0f, 0f);
      public Vector4 Falloff = new Vector4(0f, 0f, 1f, 1f);
      public Vector4 Hue0 = new Vector4(1f, 1f, 1f, 0f), Hue1 = new Vector4(1f, 1f, 1f, .333333f),
        Hue2 = new Vector4(1f, 1f, 1f, .666667f), Hue3 = new Vector4(1f, 1f, 1f, 1f);
      public int Shape;
      public bool TwoSided, Additive, Multiply, HueOn;
    }

    private long worldFxProjectorBudgetFrame = -1;
    private int worldFxProjectorBudgetRemaining;

    private readonly Dictionary<string, WorldFxSpecDefinition> worldFxSpecCache = new Dictionary<string, WorldFxSpecDefinition>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, WorldPrtSpec> worldPrtSpecCache = new Dictionary<string, WorldPrtSpec>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, GR2> worldFxModelCache = new Dictionary<string, GR2>(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> worldFxMissingModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<object, WorldFxPlayer> worldFxPlayers = new Dictionary<object, WorldFxPlayer>();
    private sealed class WorldConversationFxEntry {
      public WorldNpcPlacement Placement;
      public string Path;
      public object Owner;
    }
    private readonly object worldConversationFxLock = new object();
    private readonly Dictionary<WorldNpcPlacement, Dictionary<string, WorldConversationFxEntry>> worldConversationFx =
      new Dictionary<WorldNpcPlacement, Dictionary<string, WorldConversationFxEntry>>();
    private volatile WorldConversationFxEntry[] worldConversationFxSnapshot = Array.Empty<WorldConversationFxEntry>();
    private readonly List<WorldAmbientFxPlacement> worldAmbientFxPlacements = new List<WorldAmbientFxPlacement>();
    private SlimDX.Direct3D11.Buffer worldFxRibbonBuffer;
    private float worldFxLastPrune;

    private void ClearWorldFxRuntime() {
      worldFxPlayers.Clear(); worldFxSpecCache.Clear(); worldPrtSpecCache.Clear(); worldAmbientFxPlacements.Clear(); worldFxLastPrune = 0f;
      worldVendorOverheadReferenceParticle = null;
      lock (worldConversationFxLock) { worldConversationFx.Clear(); worldConversationFxSnapshot = Array.Empty<WorldConversationFxEntry>(); }
      foreach (GR2 model in worldFxModelCache.Values.Distinct()) {
        try { ReleaseModelBuffers(model); model?.Dispose(); } catch { }
      }
      worldFxModelCache.Clear(); worldFxMissingModels.Clear();
      try { worldFxRibbonBuffer?.Dispose(); } catch { }
      worldFxRibbonBuffer = null;
    }

    // Jedipedia treats every placed .fxp asset as an ambient FXSPEC host. The placement owns the transform while
    // FxSpecName names a spec relative to art/fx/fxspec. Keep these records separate from spawned DYN/placeable
    // effects so they can use the same nearest-first 200 m / 40-live admission policy as the browser viewer.
    private void BuildWorldAmbientFxPlacements() {
      worldAmbientFxPlacements.Clear();
      if (area == null || rooms == null) return;
      foreach (FileFormats.Room room in rooms) {
        if (room?.InstancesById == null) continue;
        foreach (FileFormats.AssetInstance instance in room.InstancesById.Values) {
          if (instance == null || String.IsNullOrWhiteSpace(instance.FxSpecName)) continue;
          if (!area.AssetIdMap.TryGetValue(instance.assetID, out FileFormats.AreaAsset asset) || asset == null) continue;
          string extension = (asset.Extension ?? String.Empty).Trim().TrimStart('.');
          if (!extension.Equals("fxp", StringComparison.OrdinalIgnoreCase)) continue;
          string path = NormalizeWorldAmbientFxSpecPath(instance.FxSpecName);
          if (!String.IsNullOrWhiteSpace(path)) worldAmbientFxPlacements.Add(new WorldAmbientFxPlacement { Room = room, Instance = instance, Path = path });
        }
      }
    }

    private static string NormalizeWorldAmbientFxSpecPath(string fxSpecName) {
      string path = (fxSpecName ?? String.Empty).Trim().Trim('\"').Replace('\\', '/');
      while (path.Contains("//")) path = path.Replace("//", "/");
      path = path.TrimStart('/');
      if (path.EndsWith(".fxspec", StringComparison.OrdinalIgnoreCase)) path = path.Substring(0, path.Length - ".fxspec".Length);
      if (path.StartsWith("resources/art/fx/fxspec/", StringComparison.OrdinalIgnoreCase)) path = path.Substring("resources/art/fx/fxspec/".Length);
      else if (path.StartsWith("art/fx/fxspec/", StringComparison.OrdinalIgnoreCase)) path = path.Substring("art/fx/fxspec/".Length);
      if (String.IsNullOrWhiteSpace(path)) return null;
      return "/resources/art/fx/fxspec/" + path.TrimStart('/').ToLowerInvariant() + ".fxspec";
    }

    internal void PlayWorldConversationFx(WorldNpcPlacement placement, string fxSpecName) {
      if (placement == null) return;
      string path = NormalizeWorldAmbientFxSpecPath(fxSpecName);
      if (String.IsNullOrWhiteSpace(path)) return;
      lock (worldConversationFxLock) {
        if (!worldConversationFx.TryGetValue(placement, out Dictionary<string, WorldConversationFxEntry> playing))
          worldConversationFx[placement] = playing = new Dictionary<string, WorldConversationFxEntry>(StringComparer.OrdinalIgnoreCase);
        // Play VFX is a new one-shot instance even when the same named spec was already running. Replacing the owner
        // makes TryDrawWorldFxPlayer create a fresh clock; the old cached player simply ages out through the normal
        // five-second prune without cross-thread mutation of the renderer-owned dictionary.
        playing[path] = new WorldConversationFxEntry { Placement = placement, Path = path, Owner = new object() };
        worldConversationFxSnapshot = worldConversationFx.Values.SelectMany(x => x.Values).ToArray();
      }
    }

    internal void StopWorldConversationFx(WorldNpcPlacement placement, string fxSpecName) {
      if (placement == null) return;
      string requested = (fxSpecName ?? String.Empty).Trim();
      bool all = requested.Length == 0 || String.Equals(requested, "All", StringComparison.OrdinalIgnoreCase);
      string path = all ? null : NormalizeWorldAmbientFxSpecPath(requested);
      lock (worldConversationFxLock) {
        if (!worldConversationFx.TryGetValue(placement, out Dictionary<string, WorldConversationFxEntry> playing)) return;
        if (all || String.IsNullOrWhiteSpace(path)) playing.Clear(); else playing.Remove(path);
        if (playing.Count == 0) worldConversationFx.Remove(placement);
        worldConversationFxSnapshot = worldConversationFx.Values.SelectMany(x => x.Values).ToArray();
      }
    }

    internal void ClearWorldConversationFx() {
      lock (worldConversationFxLock) {
        worldConversationFx.Clear();
        worldConversationFxSnapshot = Array.Empty<WorldConversationFxEntry>();
      }
    }

    private void DrawWorldConversationFx(Matrix viewProj, WorldRenderSettings settings) {
      if (settings == null || settings.Mode == WorldRenderMode.Map || settings.Mode == WorldRenderMode.Heightmap || camera == null) return;
      WorldConversationFxEntry[] snapshot = worldConversationFxSnapshot ?? Array.Empty<WorldConversationFxEntry>();
      if (snapshot.Length == 0) return;
      float reachSq = WorldConversationFxMaxDistance * WorldConversationFxMaxDistance;
      var candidates = new List<(WorldConversationFxEntry Entry, Matrix Frame, float DistanceSquared)>();
      foreach (WorldConversationFxEntry entry in snapshot) {
        WorldNpcPlacement npc = entry?.Placement;
        if (npc == null || npc.ConversationHidden || npc.ConversationUnplaced || String.IsNullOrWhiteSpace(entry.Path)) continue;
        Matrix frame;
        try { frame = npc.ConversationFxAnchor ? NpcPlacementBaseWorld(npc) : NpcNameplateWorld(npc); } catch { continue; }
        Vector3 position = new Vector3(frame.M41, frame.M42, frame.M43);
        float distanceSquared = (position - camera.Position).LengthSquared();
        if (!Single.IsFinite(distanceSquared) || distanceSquared > reachSq) continue;
        candidates.Add((entry, frame, distanceSquared));
      }
      foreach (var candidate in candidates.OrderBy(x => x.DistanceSquared).Take(WorldConversationFxMaxLive)) {
        WorldNpcPlacement npc = candidate.Entry.Placement;
        WorldFxHostContext host = BuildWorldFxNpcHostContext(npc, candidate.Frame, settings);
        TryDrawWorldFxPlayer(candidate.Entry.Owner, candidate.Entry.Path, candidate.Frame, viewProj, WorldFxMaxRenderParticlesPerWorldEffect, out _, null,
          settings, false, 0, null, true, host, true);
      }
    }

    private void DrawWorldAmbientFx(Matrix viewProj, HashSet<string> visible, WorldRenderSettings settings) {
      if (settings == null || settings.Mode == WorldRenderMode.Map || settings.Mode == WorldRenderMode.Heightmap || camera == null || worldAmbientFxPlacements.Count == 0) return;
      float maxDistanceSquared = WorldAmbientFxMaxDistance * WorldAmbientFxMaxDistance;
      var candidates = new List<(WorldAmbientFxPlacement Placement, Matrix World, float DistanceSquared)>();
      foreach (WorldAmbientFxPlacement placement in worldAmbientFxPlacements) {
        if (placement?.Instance == null || placement.Room == null) continue;
        // Sky-room placements are drawn by DrawWorldAmbientSkyFx in the camera-relative skyscene pass.
        if (skyRoomNames.Contains(placement.Room.RoomName)) continue;
        if (!InstanceRoomVisible(placement.Instance, placement.Room, visible) || !InstanceVisibleInWorld(placement.Instance, settings)) continue;
        Matrix world = InstanceWorld(placement.Instance, placement.Room);
        Vector3 center = new Vector3(world.M41, world.M42, world.M43);
        float distanceSquared = (center - camera.Position).LengthSquared();
        if (!Single.IsFinite(distanceSquared) || distanceSquared > maxDistanceSquared) continue;
        candidates.Add((placement, world, distanceSquared));
      }
      candidates.Sort((a, b) => a.DistanceSquared.CompareTo(b.DistanceSquared));
      int admitted = 0, newBudget = WorldAmbientFxNewPerFrame;
      foreach (var candidate in candidates) {
        if (admitted >= WorldAmbientFxMaxLive) break;
        object owner = candidate.Placement.Instance;
        bool existing = worldFxPlayers.ContainsKey(owner);
        if (!existing && newBudget <= 0) continue;
        if (!existing) newBudget--;
        admitted++;
        TryDrawWorldFxPlayer(owner, candidate.Placement.Path, candidate.World, viewProj, WorldFxMaxRenderParticlesPerWorldEffect, out _, null, settings, false);
      }
      PruneWorldFxPlayers();
    }

    // Same ambient-FXP admission path, but in the skyscene's camera-relative frame and BEFORE the world depth clear.
    // This is the missing half that previously forced sky-room effects off to avoid drawing stars/fog in world space.
    private void DrawWorldAmbientSkyFx(Matrix viewProj, WorldRenderSettings settings, FileFormats.Room activeSkyRoom, Matrix? skyCameraInverse) {
      if (settings == null || activeSkyRoom == null || settings.Mode == WorldRenderMode.Map || settings.Mode == WorldRenderMode.Heightmap || worldAmbientFxPlacements.Count == 0) return;
      float maxDistanceSquared = WorldAmbientFxMaxDistance * WorldAmbientFxMaxDistance;
      var candidates = new List<(WorldAmbientFxPlacement Placement, Matrix World, float DistanceSquared)>();
      foreach (WorldAmbientFxPlacement placement in worldAmbientFxPlacements) {
        if (placement?.Instance == null || placement.Room == null || !String.Equals(placement.Room.RoomName, activeSkyRoom.RoomName, StringComparison.Ordinal)) continue;
        if (!InstanceVisibleInWorld(placement.Instance, settings)) continue;
        Matrix world;
        try { world = InstanceWorldForSky(placement.Instance, placement.Room); if (skyCameraInverse.HasValue) world *= skyCameraInverse.Value; } catch { continue; }
        Vector3 center = new Vector3(world.M41, world.M42, world.M43); float distanceSquared = center.LengthSquared();
        if (!Single.IsFinite(distanceSquared) || distanceSquared > maxDistanceSquared) continue;
        candidates.Add((placement, world, distanceSquared));
      }
      candidates.Sort((a, b) => a.DistanceSquared.CompareTo(b.DistanceSquared));
      int admitted = 0, newBudget = WorldAmbientFxNewPerFrame;
      foreach (var candidate in candidates) {
        if (admitted >= WorldAmbientFxMaxLive) break;
        object owner = candidate.Placement.Instance; bool existing = worldFxPlayers.ContainsKey(owner);
        if (!existing && newBudget <= 0) continue; if (!existing) newBudget--; admitted++;
        TryDrawWorldFxPlayer(owner, candidate.Placement.Path, candidate.World, viewProj, WorldFxMaxRenderParticlesPerWorldEffect, out _, Vector3.Zero, settings, true);
      }
      PruneWorldFxPlayers();
    }

    private static WorldFxHostContext RebaseWorldFxHostContext(WorldFxHostContext host, Matrix childLocalFrame) {
      if (host == null) return null;
      Matrix inverse;
      try { inverse = Matrix.Invert(childLocalFrame); } catch { return null; }
      var rebased = new WorldFxHostContext {
        CasterFrame = host.CasterFrame * inverse,
        TargetFrame = host.TargetFrame * inverse
      };
      if (host.BoneFrame != null) rebased.BoneFrame = (actor, boneName) => {
        Matrix? parentFrame;
        try { parentFrame = host.BoneFrame(actor, boneName); } catch { return null; }
        return parentFrame.HasValue ? parentFrame.Value * inverse : (Matrix?)null;
      };
      return rebased;
    }

    private WorldFxHostContext BuildWorldFxInteractionHostContext(WorldInteractionIconEntry entry, Matrix fxFrame, WorldRenderSettings renderSettings) {
      if (!(entry?.Owner is WorldNpcPlacement npc)) return null;
      return BuildWorldFxNpcHostContext(npc, fxFrame, renderSettings);
    }

    private WorldFxHostContext BuildWorldFxNpcHostContext(WorldNpcPlacement npc, Matrix fxFrame, WorldRenderSettings renderSettings) {
      if (npc == null) return null;
      Matrix invFx;
      try { invFx = Matrix.Invert(fxFrame); } catch { return null; }
      Matrix npcWorld;
      try { npcWorld = npc.ConversationFxAnchor ? NpcPlacementBaseWorld(npc) : NpcNameplateWorld(npc); } catch { return null; }
      var host = new WorldFxHostContext { CasterFrame = npcWorld * invFx };
      host.BoneFrame = (actor, boneName) => {
        if (!String.Equals(actor, "CASTER", StringComparison.OrdinalIgnoreCase) || String.IsNullOrWhiteSpace(boneName)) return null;
        WorldNpcAnimationClip clip = npc.ConversationAnimation ?? npc.Animation;
        bool conversationClip = npc.ConversationAnimation != null;
        Vector3 npcPosition = new Vector3(npcWorld.M41, npcWorld.M42, npcWorld.M43);
        bool animate = clip?.Animation != null && (conversationClip ||
          ((renderSettings?.AnimateNpcs ?? true) && (renderSettings == null || renderSettings.Mode != WorldRenderMode.Map) &&
            (camera == null || (npcPosition - camera.Position).LengthSquared() <= NpcAnimationRenderDistance * NpcAnimationRenderDistance)));
        float animationTime = 0f;
        if (animate) {
          float length = clip.Animation.Length;
          float baseTime = conversationClip
            ? Math.Max(0f, (elapsed - npc.ConversationAnimationStart) * Math.Max(.0001f, npc.ConversationAnimationSpeed) + npc.ConversationAnimationOffset)
            : elapsed + npc.AnimationPhase * length;
          if (conversationClip && !npc.ConversationAnimationLoop) animationTime = length > 0f ? Math.Min(baseTime, Math.Max(0f, length - .0001f)) : 0f;
          else { animationTime = length > 0f ? baseTime % length : 0f; if (animationTime < 0f) animationTime += length; }
        }
        if (!TryNpcWeaponBoneFrame(npc, clip, animationTime, animate, boneName, out Matrix boneLocal)) return null;
        return boneLocal * npcWorld * invFx;
      };
      return host;
    }

    private WorldFxHostContext BuildWorldFxSpnHostContext(WorldSpnPlacement placement, Matrix placementWorld, Matrix fxFrame, bool animateStates) {
      if (placement == null) return null;
      Matrix invFx;
      try { invFx = Matrix.Invert(fxFrame); } catch { return null; }
      var host = new WorldFxHostContext { CasterFrame = placementWorld * invFx };
      host.BoneFrame = (actor, boneName) => {
        if (!String.Equals(actor, "CASTER", StringComparison.OrdinalIgnoreCase) || String.IsNullOrWhiteSpace(boneName)) return null;
        if (!TryWorldFxSpnBoneWorld(placement, placementWorld, animateStates, boneName, out Matrix boneWorld)) return null;
        return boneWorld * invFx;
      };
      return host;
    }

    private bool TryWorldFxSpnBoneWorld(WorldSpnPlacement placement, Matrix placementWorld, bool animateStates, string boneName, out Matrix boneWorld) {
      boneWorld = Matrix.Identity;
      string wanted = NpcCanonicalAnimationBoneName(boneName);
      if (String.IsNullOrWhiteSpace(wanted) || placement == null) return false;
      WorldSpnDynState dyn = ActiveSpnDynState(placement, animateStates);
      if (dyn != null) {
        foreach (WorldSpnDynPart part in dyn.Parts) {
          if (part?.Model == null) continue;
          if (animateStates && TryWorldFxAnimatedModelBone(part.Model, part.Animation, placement.AnimationPhase, wanted, out Matrix animatedPartBone)) {
            boneWorld = animatedPartBone * part.LocalMatrix * placementWorld;
            return NpcFinite(boneWorld);
          }
          if (part.Model.skeleton_bones == null) continue;
          foreach (GR2_Bone_Skeleton bone in part.Model.skeleton_bones) {
            if (bone == null || !String.Equals(NpcCanonicalAnimationBoneName(bone.boneName), wanted, StringComparison.OrdinalIgnoreCase)) continue;
            boneWorld = bone.root * part.LocalMatrix * placementWorld;
            return NpcFinite(boneWorld);
          }
        }
      }
      if (placement.Models != null) {
        foreach (GR2 model in placement.Models) {
          if (model == null) continue;
          if (animateStates && TryWorldFxAnimatedModelBone(model, placement.Animation, placement.AnimationPhase, wanted, out Matrix animatedBone)) {
            boneWorld = animatedBone * placementWorld;
            return NpcFinite(boneWorld);
          }
          if (model.skeleton_bones == null) continue;
          foreach (GR2_Bone_Skeleton bone in model.skeleton_bones) {
            if (bone == null || !String.Equals(NpcCanonicalAnimationBoneName(bone.boneName), wanted, StringComparison.OrdinalIgnoreCase)) continue;
            boneWorld = bone.root * placementWorld;
            return NpcFinite(boneWorld);
          }
        }
      }
      return false;
    }

    private bool TryWorldFxAnimatedModelBone(GR2 model, WorldNpcAnimationClip clip, float phase, string wanted, out Matrix boneFrame) {
      boneFrame = Matrix.Identity;
      if (model == null || clip?.Animation == null || String.IsNullOrWhiteSpace(wanted)) return false;
      float length = clip.Animation.Length;
      float animationTime = length > 0f ? (elapsed + phase * length) % length : 0f;
      if (animationTime < 0f) animationTime += length;
      NpcSkinState state = GetNpcSkinState(model, clip);
      if (state?.Skeleton == null || state.BoundBoneCount <= 0 || !UpdateNpcSkinState(state, animationTime, null)) return false;
      for (int i = 0; i < state.Skeleton.Count && i < state.CurrentWorld.Length; i++) {
        if (!state.CurrentValid[i] || !String.Equals(NpcCanonicalAnimationBoneName(state.Skeleton[i]?.boneName), wanted, StringComparison.OrdinalIgnoreCase)) continue;
        boneFrame = state.CurrentWorld[i];
        return NpcFinite(boneFrame);
      }
      return false;
    }

    private static WorldFxHostContext BuildWorldFxStableOverheadHostContext(WorldInteractionIconEntry entry) {
      // Quest/conversation overhead effects are already mounted on entry.FxFrame, whose translation is rebased onto
      // the visible attach_nameplate anchor before rendering.  Returning the effect-local origin for NamePlate avoids
      // a second animated/bind-pose skeleton lookup (the source of the old feet/nameplate flicker) while preserving
      // the FXSPEC hierarchy and PRT camera-distance scaling used by the vendor/service icon renderer.
      if (!(entry?.Owner is WorldNpcPlacement)) return null;
      var host = new WorldFxHostContext { CasterFrame = Matrix.Identity };
      host.BoneFrame = (actor, boneName) => {
        if (!String.Equals(actor, "CASTER", StringComparison.OrdinalIgnoreCase) || String.IsNullOrWhiteSpace(boneName)) return null;
        string bone = boneName.Trim();
        if (String.Equals(bone, "NamePlate", StringComparison.OrdinalIgnoreCase) ||
            String.Equals(bone, "attach_nameplate", StringComparison.OrdinalIgnoreCase) ||
            String.Equals(bone, "attach_nameplate_fallback", StringComparison.OrdinalIgnoreCase) ||
            String.Equals(bone, "nameplate", StringComparison.OrdinalIgnoreCase)) return Matrix.Identity;
        return null;
      };
      return host;
    }

    private bool TryDrawWorldInteractionFxPlayer(WorldInteractionIconEntry entry, string fxSpecPath, Matrix viewProj, WorldRenderSettings renderSettings, out bool drawn,
        bool stableOverheadAnchor = false, float? screenYOffsetOverride = null) {
      drawn = false; if (entry?.Owner == null) return false;
      Matrix fxFrame = entry.FxFrame ?? Matrix.Identity;
      // The exact SWTOR NamePlate frame is still useful for the authored FX orientation, but some GR2/JBA rigs in
      // PugTools expose a bind-frame translation that is noticeably below the text attach_nameplate point.  Keep the
      // full orientation and rebase only the translation onto the same anchor used by the visible nameplate.  Then
      // apply the same small screen-space lift used by the static sprite fallback.  This makes the quest/service FX
      // sit above the NPC name instead of around the actor's feet while preserving the spec's local -Z/up semantics.
      fxFrame.M41 = entry.Anchor.X; fxFrame.M42 = entry.Anchor.Y; fxFrame.M43 = entry.Anchor.Z; fxFrame.M44 = 1f;
      float screenYOffset = screenYOffsetOverride ?? entry.ScreenYOffset;
      if (Math.Abs(screenYOffset) > .01f && camera != null) {
        Vector3 visibleForward = -camera.Look;
        if (visibleForward.LengthSquared() > .000001f) {
          visibleForward.Normalize();
          float depth = Vector3.Dot(entry.Anchor - camera.Position, visibleForward);
          int viewportHeight = Math.Max(1, (int)Viewport.Height);
          if (depth > .001f && viewportHeight > 0) {
            float worldPerPixel = (float)(2.0 * depth * Math.Tan(camera.FovY * .5f) / viewportHeight);
            Vector3 lift = camera.Up * (-screenYOffset * worldPerPixel);
            fxFrame.M41 += lift.X; fxFrame.M42 += lift.Y; fxFrame.M43 += lift.Z;
          }
        }
      }
      // This pass is rendered after TAA/post-processing with the stable label matrix, while scene depth was produced
      // with the jittered world matrix. Receiver-space projectors would reconstruct from mismatched matrices here,
      // so keep interaction-icon projectors on their geometry fallback path.
      WorldFxHostContext hostContext = stableOverheadAnchor
        ? BuildWorldFxStableOverheadHostContext(entry)
        : BuildWorldFxInteractionHostContext(entry, fxFrame, renderSettings);
      return TryDrawWorldFxPlayer(entry.Owner, fxSpecPath, fxFrame, viewProj, WorldFxMaxRenderParticlesPerIcon, out drawn,
        null, renderSettings, false, 0, null, false, hostContext);
    }

    // Shared by overhead icons, DYN .fxspec rows, plcUsableVFX and ship-window effects. Keeping one player path matters:
    // all of them are authored FXSPEC/PRT systems and should advance on the same paused/seekable world effect clock.
    private bool TryDrawWorldFxPlayer(object owner, string fxSpecPath, Matrix fxFrame, Matrix viewProj, int maxParticles, out bool drawn,
        Vector3? cameraPositionOverride = null, WorldRenderSettings renderSettings = null, bool sky = false, int nestedDepth = 0,
        IReadOnlyDictionary<string, string> dynamicData = null, bool allowReceiverProjectors = true, WorldFxHostContext hostContext = null,
        bool cinematic = false) {
      drawn = false; if (owner == null || String.IsNullOrWhiteSpace(fxSpecPath) || area == null || camera == null) return false;
      WorldFxSpecDefinition sourceDefinition = LoadWorldFxSpecDefinition(fxSpecPath); if (sourceDefinition == null || sourceDefinition.SupportedVisualCount == 0) return false;
      string dynamicFingerprint = WorldFxDynamicDataFingerprint(dynamicData);
      WorldFxSpecDefinition definition = CloneWorldFxDefinitionForDynamicData(sourceDefinition, dynamicData, hostContext);
      if (definition == null || !definition.DynamicGateOpen) return false;
      // Claim ownership only when at least one visual resource resolves. This preserves the legacy icon fallback for
      // malformed/beta specs, while model-only FXSPECs no longer get rejected just because they contain no PRT node.
      bool supported = definition.Nodes.Any(x => x != null && x.DynamicGateOpen && (
        x.IsLightning || x.IsLight || x.IsProjector ||
        (!x.IsGroup && !x.IsFader && !x.IsTransformer && !x.IsAttacher && !x.IsArc && !String.IsNullOrWhiteSpace(x.Resource) &&
          (x.IsModel ? LoadWorldFxModel(x.Resource) != null : LoadWorldPrtSpec(x.Resource) != null))));
      if (!supported) return false;
      if (!worldFxPlayers.TryGetValue(owner, out WorldFxPlayer player) || !String.Equals(player.Path, definition.Path, StringComparison.OrdinalIgnoreCase) ||
          !String.Equals(player.DynamicDataFingerprint ?? String.Empty, dynamicFingerprint, StringComparison.Ordinal) || player.Cinematic != cinematic) {
        player = CreateWorldFxPlayer(owner, definition, elapsed, cinematic);
        player.DynamicData = dynamicData; player.DynamicDataFingerprint = dynamicFingerprint;
        worldFxPlayers[owner] = player;
      }
      if (player.Definition != null) player.Definition.HostContext = hostContext;
      player.LastSeenWorldTime = elapsed;
      Matrix inverseFx;
      try { inverseFx = Matrix.Invert(fxFrame); }
      catch { inverseFx = Matrix.Translation(new Vector3(-fxFrame.M41, -fxFrame.M42, -fxFrame.M43)); }
      Vector3 effectCamera = cameraPositionOverride ?? camera.Position;
      Vector3 cameraLocal = Vector3.TransformCoordinate(effectCamera, inverseFx);
      UpdateWorldFxPlayer(player, elapsed, cameraLocal);
      // Jedipedia/client ordering: effect models establish receiver depth first, projectors paint those receivers,
      // then translucent particles/trails/lightning/glows composite on top. Drawing a full-screen decal after the
      // particle pass would incorrectly paint over smoke, sparks and force-field particles that never wrote depth.
      int modelCap = maxParticles <= WorldFxMaxRenderParticlesPerIcon ? WorldFxMaxRenderModelsPerIcon : WorldFxMaxRenderModelsPerWorldEffect;
      int modelsDrawn = 0;
      WorldRenderSettings settings = renderSettings ?? new WorldRenderSettings();
      foreach (WorldFxRenderModel model in BuildWorldFxRenderModels(player, fxFrame)) {
        if (model?.Model == null || ++modelsDrawn > modelCap) break;
        drawn |= DrawWorldFxModel(model, viewProj, settings, sky);
      }
      // True receiver-space decals need the receiver depth to exist already, so paint them after the effect's own
      // GR2 models. Projectors we cannot safely classify (UVSPACE, caster/target-only, FX-only, sky) retain the
      // authored volume/card stand-in rather than painting unrelated world geometry.
      var paintedProjectors = new HashSet<WorldFxNodeDefinition>();
      if (!sky && allowReceiverProjectors) {
        int decalCount = 0;
        foreach (WorldFxProjectorDecal decal in BuildWorldFxProjectorDecals(player)) {
          if (decal == null || ++decalCount > WorldFxMaxProjectorDecalsPerPlayer) break;
          if (DrawWorldFxProjectorDecal(decal, fxFrame, viewProj)) { drawn = true; paintedProjectors.Add(decal.Node); }
        }
      }
      int ribbonCount = 0;
      foreach (WorldFxRenderRibbonSegment projector in BuildWorldFxRenderProjectorSegments(player, paintedProjectors)) {
        if (projector == null || ++ribbonCount > WorldFxMaxRibbonSegmentsPerPlayer) break;
        drawn |= DrawWorldFxRibbonSegment(projector, fxFrame, viewProj);
      }
      int count = 0, cap = Math.Max(1, maxParticles);
      foreach (WorldFxRenderParticle particle in BuildWorldFxRenderParticles(player)) {
        if (particle == null || ++count > cap) break;
        drawn |= DrawWorldFxParticle(particle, fxFrame, 0f, viewProj, cameraPositionOverride);
      }
      foreach (WorldFxRenderRibbonSegment ribbon in BuildWorldFxRenderTrailSegments(player)) {
        if (ribbon == null || ++ribbonCount > WorldFxMaxRibbonSegmentsPerPlayer) break;
        drawn |= DrawWorldFxRibbonSegment(ribbon, fxFrame, viewProj);
      }
      foreach (WorldFxRenderRibbonSegment ribbon in BuildWorldFxRenderLightningSegments(player, cameraLocal)) {
        if (ribbon == null || ++ribbonCount > WorldFxMaxRibbonSegmentsPerPlayer) break;
        drawn |= DrawWorldFxRibbonSegment(ribbon, fxFrame, viewProj);
      }
      foreach (WorldFxRenderGlow glow in BuildWorldFxRenderGlows(player)) drawn |= DrawWorldFxGlow(glow, fxFrame, viewProj);
      if (nestedDepth < WorldFxNestedSpecDepthCap) {
        int nestedCount = 0;
        foreach (WorldFxNodeRuntime runtime in player.Nodes) {
          if (runtime?.System == null || runtime.Definition == null) continue;
          Matrix nodeLocalFrame = WorldFxNodeLocalMatrix(runtime.Definition, player.Definition, player.Clock);
          Matrix nodeFrame = nodeLocalFrame * fxFrame;
          foreach (WorldPrtFxInstance nested in runtime.System.FxInstances) {
            if (nested == null || String.IsNullOrWhiteSpace(nested.Path) || ++nestedCount > WorldFxNestedSpecRenderCapPerPlayer) break;
            Matrix local = Matrix.RotationX(nested.RenderRotation.X) * Matrix.RotationY(nested.RenderRotation.Y) * Matrix.RotationZ(nested.RenderRotation.Z) * Matrix.Translation(nested.RenderPosition);
            WorldFxHostContext nestedHost = RebaseWorldFxHostContext(player.Definition?.HostContext, local * nodeLocalFrame);
            bool nestedDrawn; TryDrawWorldFxPlayer(nested.Owner, nested.Path, local * nodeFrame, viewProj, Math.Min(maxParticles, 96), out nestedDrawn,
              cameraPositionOverride, settings, sky, nestedDepth + 1, player.DynamicData, allowReceiverProjectors, nestedHost, player.Cinematic);
            drawn |= nestedDrawn;
          }
          if (nestedCount >= WorldFxNestedSpecRenderCapPerPlayer) break;
        }
      }
      PruneWorldFxPlayers();
      return true; // parsed and owned even if the authored effect is momentarily between visible particles/models
    }

    private void PruneWorldFxPlayers() {
      if (elapsed - worldFxLastPrune <= 2f) return;
      worldFxLastPrune = elapsed;
      foreach (object stale in worldFxPlayers.Where(x => elapsed - x.Value.LastSeenWorldTime > 5f).Select(x => x.Key).ToArray()) worldFxPlayers.Remove(stale);
    }

    private WorldFxPlayer CreateWorldFxPlayer(object owner, WorldFxSpecDefinition definition, float worldTime, bool cinematic = false) {
      var player = new WorldFxPlayer { Owner = owner, Path = definition.Path, Definition = definition, LastWorldTime = worldTime, LastSeenWorldTime = worldTime, Cinematic = cinematic };
      foreach (WorldFxNodeDefinition node in definition.Nodes.Where(x => x != null && x.DynamicGateOpen && (
        x.IsLightning || x.IsLight || x.IsProjector ||
        (!x.IsGroup && !x.IsFader && !x.IsTransformer && !x.IsAttacher && !x.IsArc && !x.IsSound && !String.IsNullOrWhiteSpace(x.Resource)))))
        player.Nodes.Add(new WorldFxNodeRuntime { Definition = node });
      return player;
    }

    private void UpdateWorldFxPlayer(WorldFxPlayer player, float worldTime, Vector3 cameraLocal) {
      if (player == null) return;
      if (!player.PreRolled) {
        player.PreRolled = true;
        if (player.Cinematic) {
          // Advance only enough to fire nodes authored at t=0. Ambient effects deliberately pre-roll so a newly
          // visible steam/fire system looks settled; a conversation beat must instead begin exactly when Play VFX fires.
          StepWorldFxPlayer(player, .0001f, cameraLocal);
        } else {
          float left = WorldFxPreRollSeconds;
          while (left > 0f) { float step = Math.Min(WorldFxPreRollStep, left); StepWorldFxPlayer(player, step, cameraLocal); left -= step; }
        }
        player.LastWorldTime = worldTime; return;
      }
      float dt = worldTime - player.LastWorldTime; player.LastWorldTime = worldTime;
      if (dt < 0f) { ResetWorldFxPlayer(player); return; }
      if (dt > .5f) {
        // Ambient loops can safely rebuild after a long discontinuity. A cinematic one-shot cannot: restarting it
        // after a hitch would re-fire an impact/fleet-arrival the beat already played. Bound catch-up instead.
        if (!player.Cinematic) { ResetWorldFxPlayer(player); return; }
        dt = .5f;
      }
      while (dt > 0f) { float step = Math.Min(WorldFxMaxFrameStep, dt); StepWorldFxPlayer(player, step, cameraLocal); dt -= step; }
    }

    private void ResetWorldFxPlayer(WorldFxPlayer player) {
      player.Clock = 0; player.PreRolled = false; foreach (WorldFxNodeRuntime node in player.Nodes) { node.System = null; node.Model = null; node.Lightning = null; node.Started = node.Stopped = false; }
    }

    private void StepWorldFxPlayer(WorldFxPlayer player, float dt, Vector3 cameraLocal) {
      if (player == null || dt <= 0f) return;
      player.Clock += dt;
      float cycle = player.Definition.GroupEnd;
      if (!player.Cinematic && !Single.IsInfinity(cycle) && cycle > .05f && player.Clock > cycle) {
        player.Clock %= cycle; foreach (WorldFxNodeRuntime runtime in player.Nodes) { runtime.System = null; runtime.Model = null; runtime.Lightning = null; runtime.Started = runtime.Stopped = false; }
      }
      foreach (WorldFxNodeRuntime runtime in player.Nodes) {
        WorldFxNodeDefinition node = runtime.Definition;
        if (!runtime.Started && player.Clock >= node.StartAt && !Single.IsInfinity(node.StartAt)) {
          runtime.Started = true;
          if (node.IsModel) runtime.Model = LoadWorldFxModel(node.Resource);
          else if (node.IsLightning) runtime.Lightning = CreateWorldFxLightningState(node);
          else if (!node.IsLight && !node.IsProjector) {
            WorldPrtSpec root = LoadWorldPrtSpec(node.Resource);
            if (root != null) runtime.System = new WorldPrtSystem(root, LoadWorldPrtSpec, (uint)(node.Name?.GetHashCode() ?? 1), node.LifeModifier);
          }
        }
        if (!runtime.Stopped && player.Clock >= node.StopAt && !Single.IsInfinity(node.StopAt)) { runtime.Stopped = true; runtime.System?.Stop(node.HideOnStop); }
        runtime.System?.Update(dt, cameraLocal - node.Offset);
        if (runtime.Started && !runtime.Stopped && node.IsLightning && runtime.Lightning != null)
          UpdateWorldFxLightning(player, runtime, dt);
      }
    }

    private static WorldFxLightningState CreateWorldFxLightningState(WorldFxNodeDefinition node) {
      if (node == null) return null;
      uint seed = unchecked((uint)((node.Name?.GetHashCode() ?? 1) * 397 + 2000));
      var state = new WorldFxLightningState {
        Seed = seed,
        Random = new WorldFxRandom(seed),
        EnvelopeShape = WorldFxValue.Parse(FxString(node.Fields, "_fxEnvelopeShape")),
        LightningShape = WorldFxValue.Parse(FxString(node.Fields, "_fxLightningShape")),
        SpacingTrack = WorldFxValue.Parse(FxString(node.Fields, "_fxCPointSpacing")),
        RecreateTrack = WorldFxValue.Parse(FxString(node.Fields, "_fxCPointRecreateTime")),
        TravelTrack = WorldFxValue.Parse(FxString(node.Fields, "_fxCPointTravelDistPerSec")),
        RotateTrack = WorldFxValue.Parse(FxString(node.Fields, "_fxCPointRotateDegPerSec")),
        RiseTrack = WorldFxValue.Parse(FxString(node.Fields, "_fxCPointRisePerSec")),
        SimulationTrack = WorldFxValue.Parse(FxString(node.Fields, "_fxCPointSimulationInfluence")),
        RandomWidthTrack = WorldFxValue.Parse(FxString(node.Fields, "_fxLightningRandomWidthPercent")),
        UvScrollTrack = WorldFxValue.Parse(FxString(node.Fields, "_fxUVScrollRate")),
        TextureTimerTrack = WorldFxValue.Parse(FxString(node.Fields, "_fxTextureAnimationTimer")),
        EnvelopeWidthTrack = WorldFxValue.Parse(FxString(node.Fields, "_fxEnvelopeWidthAmt")),
        EnvelopeHeightTrack = WorldFxValue.Parse(FxString(node.Fields, "_fxEnvelopeHeightAmt")),
        EnvelopeDepthTrack = WorldFxValue.Parse(FxString(node.Fields, "_fxEnvelopeDepthAmt")),
        LengthColor = WorldFxValue.Parse(FxString(node.Fields, "_fxLightningLengthColor")),
        RampStartTrack = WorldFxValue.Parse(FxString(node.Fields, "_fxLightningLengthColorRampStart")),
        LengthRangeTrack = WorldFxValue.Parse(FxString(node.Fields, "_fxLightningLengthColorRange")),
        DynamicColor = WorldFxValue.Parse(FxString(node.Fields, "_fxLightningDynamicColor")),
        TexturePath = NormalizeWorldOverheadTexturePath(FxString(node.Fields, "_fxTextureName")),
        TargetName = FxString(node.Fields, "_fxTargetName")
      };
      state.EnvelopeMaxRadius = Math.Abs(WorldFxFieldScalar(node.Fields, "_fxEnvelopeMaxRadius", 0f, seed + 1u, .5f));
      state.EnvelopeMinRadius = Math.Abs(WorldFxFieldScalar(node.Fields, "_fxEnvelopeMinRadius", 0f, seed + 2u, .1f));
      state.EnvelopeLength = WorldFxFieldScalar(node.Fields, "_fxEnvelopeLength", 0f, seed + 3u, 5f);
      state.MaxWidth = Math.Abs(WorldFxFieldScalar(node.Fields, "_fxLightningMaxWidth", 0f, seed + 4u, .05f));
      state.MinWidth = Math.Max(.001f, Math.Abs(WorldFxFieldScalar(node.Fields, "_fxLightningMinWidth", 0f, seed + 5u, .025f)));
      state.TessellationDistance = Math.Max(.01f, Math.Abs(WorldFxFieldScalar(node.Fields, "_fxTessellationDist", 0f, seed + 6u, .1f)));
      state.TextureTileDistance = Math.Max(.0001f, Math.Abs(WorldFxFieldScalar(node.Fields, "_fxTextureTileDist", 0f, seed + 7u, 1f)));
      float authoredTension = WorldFxFieldScalar(node.Fields, "_fxCPointTension", 0f, seed + 8u, 1f);
      state.Tension = Math.Min(1f, (float)Math.Pow(authoredTension, 4) + .01f);
      bool flag(string key) => String.Equals(FxString(node.Fields, key), "true", StringComparison.OrdinalIgnoreCase);
      state.EvalRotatePerFrame = flag("_fxEvalCPointRotatePerFrame");
      state.EvalRandomWidthPerFrame = flag("_fxEvalLightningRandomWidthPerFrame");
      state.EvalUvPerFrame = flag("_fxEvalUVScrollRatePerFrame");
      state.RandomInitialUv = flag("_fxRandomInitialUV");
      state.ResetDynamicColor = flag("_fxResetDynamicColorAtCPointRegenerate");
      state.Additive = !flag("_fxUseAlphaBlend");
      return state;
    }

    private static float WorldFxLightningScalar(WorldFxValue value, float elapsed, WorldFxLightningState state, float fallback) {
      return value != null && value.Kind != WorldFxValueKind.Empty ? value.Number(elapsed, state.Random, fallback) : fallback;
    }

    private static float WorldFxLightningSpacing(WorldFxLightningState state, float elapsed) {
      return Math.Max(.05f, Math.Abs(WorldFxLightningScalar(state.SpacingTrack, elapsed, state, .25f)));
    }

    private static float WorldFxLightningRandomWidth(WorldFxLightningState state, float elapsed) {
      float amount = WorldFxLightningScalar(state.RandomWidthTrack, elapsed, state, 0f);
      return Math.Abs(1f + amount * (float)(2.0 * state.Random.NextDouble() - 1.0));
    }

    private static void WorldFxLightningRollUv(WorldFxLightningState state) {
      state.UvOffset = state.RandomInitialUv ? (float)state.Random.NextDouble() : 0f;
    }

    private static WorldFxLightningPoint WorldFxLightningNewPoint(WorldFxLightningState state, float t) {
      float r1 = (float)(2.0 * state.Random.NextDouble() - 1.0), r2 = (float)(2.0 * state.Random.NextDouble() - 1.0);
      float len = (float)Math.Sqrt(r1 * r1 + r2 * r2); if (!(len > 1e-6f)) len = 1f;
      return new WorldFxLightningPoint {
        T = t,
        LocalX = Math.Sign(r1) * r1 * r1 / len,
        LocalY = Math.Sign(r2) * r2 * r2 / len
      };
    }

    private static void WorldFxLightningEndpoints(WorldFxPlayer player, WorldFxNodeDefinition node, WorldFxLightningState state,
        out Vector3 start, out Vector3 end) {
      WorldFxNodeDynamicTransform(player.Definition, node, player.Clock, out start, out _, out _);
      string targetName = String.IsNullOrWhiteSpace(state?.TargetName) ? "TARGET" : state.TargetName.Trim();
      if (WorldFxActorAnchor(targetName)) {
        Matrix targetFrame = WorldFxHostAnchorFrame(player.Definition, targetName, FxString(node?.Fields, "_fxTargetBoneName"));
        end = new Vector3(targetFrame.M41, targetFrame.M42, targetFrame.M43);
        return;
      }
      WorldFxNodeDefinition target = player.Definition.Nodes.FirstOrDefault(x => x != null && String.Equals(x.Name, targetName, StringComparison.OrdinalIgnoreCase));
      if (target != null) WorldFxNodeDynamicTransform(player.Definition, target, player.Clock, out end, out _, out _);
      else {
        Matrix fallback = WorldFxHostAnchorFrame(player.Definition, "TARGET", FxString(node?.Fields, "_fxTargetBoneName"));
        end = new Vector3(fallback.M41, fallback.M42, fallback.M43);
      }
    }

    private static void UpdateWorldFxLightning(WorldFxPlayer player, WorldFxNodeRuntime runtime, float dt) {
      WorldFxNodeDefinition node = runtime?.Definition; WorldFxLightningState state = runtime?.Lightning;
      if (player == null || node == null || state == null || !(dt > 0f)) return;
      WorldFxLightningEndpoints(player, node, state, out Vector3 start, out Vector3 end);
      Vector3 delta = end - start; float length = delta.Length();
      state.Start = start; state.End = end; state.Length = length;
      if (!(length >= .001f)) return;

      float elapsedNode = Math.Max(0f, player.Clock - node.StartAt);
      if (!state.Started) {
        state.Started = true;
        state.ColorClock = player.Clock;
        state.RecreateRemaining = WorldFxLightningScalar(state.RecreateTrack, elapsedNode, state, 0f);
        state.TextureTimerRemaining = WorldFxLightningScalar(state.TextureTimerTrack, elapsedNode, state, 0f);
        state.RotationRadians = WorldFxLightningScalar(state.RotateTrack, elapsedNode, state, 0f) * ((float)Math.PI / 180f);
        state.RandomWidth = WorldFxLightningRandomWidth(state, elapsedNode);
        state.UvRate = WorldFxLightningScalar(state.UvScrollTrack, elapsedNode, state, 0f);
        WorldFxLightningRollUv(state);
      }

      if (state.RecreateRemaining > 0f) {
        state.RecreateRemaining -= dt;
        if (state.RecreateRemaining <= 0f) {
          state.RecreateRemaining = WorldFxLightningScalar(state.RecreateTrack, elapsedNode, state, 0f);
          state.RotationRadians = WorldFxLightningScalar(state.RotateTrack, elapsedNode, state, 0f) * ((float)Math.PI / 180f);
          state.RandomWidth = WorldFxLightningRandomWidth(state, elapsedNode);
          state.UvRate = WorldFxLightningScalar(state.UvScrollTrack, elapsedNode, state, 0f);
          WorldFxLightningRollUv(state);
          state.Points.Clear();
          if (state.ResetDynamicColor) state.ColorClock = player.Clock;
        }
      }
      if (state.TextureTimerRemaining > 0f) {
        state.TextureTimerRemaining -= dt;
        if (state.TextureTimerRemaining <= 0f) {
          state.TextureTimerRemaining = WorldFxLightningScalar(state.TextureTimerTrack, elapsedNode, state, 0f);
          WorldFxLightningRollUv(state);
        }
      }

      if (state.Points.Count == 0) {
        state.Spacing = WorldFxLightningSpacing(state, elapsedNode);
        float acc = 0f; int count = 0;
        while (acc < length && state.Points.Count < WorldFxLightningMaxControlPoints - 1) {
          state.Points.Add(WorldFxLightningNewPoint(state, acc / length));
          acc += WorldFxLightningSpacing(state, elapsedNode); count++;
        }
        if (count < 2 && state.Points.Count < WorldFxLightningMaxControlPoints - 1) state.Points.Add(WorldFxLightningNewPoint(state, .5f));
        state.Points.Add(WorldFxLightningNewPoint(state, 1f));
      }

      if (state.EvalRotatePerFrame) state.RotationRadians = WorldFxLightningScalar(state.RotateTrack, elapsedNode, state, 0f) * ((float)Math.PI / 180f);
      if (state.EvalRandomWidthPerFrame) state.RandomWidth = WorldFxLightningRandomWidth(state, elapsedNode);
      if (state.EvalUvPerFrame) state.UvRate = WorldFxLightningScalar(state.UvScrollTrack, elapsedNode, state, 0f);
      state.InvLengthColorRange = 1f / Math.Max(.001f, Math.Abs(WorldFxLightningScalar(state.LengthRangeTrack, elapsedNode, state, 1f)));
      state.RampStart = WorldFxLightningScalar(state.RampStartTrack, elapsedNode, state, 0f);

      Vector3 axis = delta / length;
      Vector3 helper = Math.Abs(axis.Y) > .99f ? Vector3.UnitX : Vector3.UnitY;
      Vector3 right = Vector3.Cross(axis, helper); if (right.LengthSquared() <= 1e-8f) right = Vector3.UnitX; else right.Normalize();
      Vector3 up = Vector3.Cross(right, axis); if (up.LengthSquared() <= 1e-8f) up = Vector3.UnitY; else up.Normalize();

      float travelStep = WorldFxLightningScalar(state.TravelTrack, elapsedNode, state, 0f) * dt;
      float riseStep = WorldFxLightningScalar(state.RiseTrack, elapsedNode, state, 0f) * dt;
      float widthAmount = Math.Abs(WorldFxLightningScalar(state.EnvelopeWidthTrack, elapsedNode, state, 1f));
      float heightAmount = Math.Abs(WorldFxLightningScalar(state.EnvelopeHeightTrack, elapsedNode, state, 1f));
      float depthAmount = Math.Abs(WorldFxLightningScalar(state.EnvelopeDepthTrack, elapsedNode, state, 1f));
      float simulationInfluence = Math.Max(0f, WorldFxLightningScalar(state.SimulationTrack, elapsedNode, state, 1f));

      if (state.Points.Count >= 2 && state.Points.Count < WorldFxLightningMaxControlPoints && state.Points[1].T > state.Spacing) {
        state.Points[0].T = state.Points[1].T - state.Spacing;
        state.Points.Insert(0, WorldFxLightningNewPoint(state, 0f));
        state.Spacing = WorldFxLightningSpacing(state, elapsedNode);
      }

      float shortening = state.EnvelopeLength > length && Math.Abs(state.EnvelopeLength) > .0001f ? length / state.EnvelopeLength : 0f;
      state.RadiusAt = shortening > 0f ? Math.Max(state.EnvelopeMinRadius, shortening * state.EnvelopeMaxRadius) : state.EnvelopeMaxRadius;
      state.WidthAt = shortening > 0f ? Math.Max(state.MinWidth, shortening * state.MaxWidth) : state.MaxWidth;
      state.WidthTaper = state.MaxWidth > 0f ? state.WidthAt / state.MaxWidth : 1f;

      float rotation = state.RotationRadians * dt, cs = (float)Math.Cos(rotation), sn = (float)Math.Sin(rotation);
      for (int i = 0; i < state.Points.Count; i++) {
        WorldFxLightningPoint point = state.Points[i];
        if (i != 0) point.T = Math.Min(1f, point.T + travelStep);
        float lx0 = point.LocalX;
        point.LocalX = point.LocalX * cs - point.LocalY * sn;
        point.LocalY = lx0 * sn + point.LocalY * cs;
        float envelope = Math.Abs(state.EnvelopeShape.Number(point.T, state.Random, 0f));
        float radius = state.RadiusAt * envelope;
        float lx = radius * point.LocalX * widthAmount;
        float ly = radius * (((point.LocalY + 1f) * .5f) * (heightAmount + depthAmount) - depthAmount);
        Vector3 wanted = start + right * lx + up * ly + axis * (length * point.T);
        point.Rise += riseStep; wanted.Y += simulationInfluence * point.Rise;
        float fac = state.Tension >= 1f || !point.Seeded ? 1f : 1f + (state.Tension - 1f) * simulationInfluence;
        point.Position += (wanted - point.Position) * fac; point.Seeded = true;
      }
      if (state.Points.Count >= 3 && state.Points[state.Points.Count - 2].T >= 1f) state.Points.RemoveAt(state.Points.Count - 1);
      state.UvOffset -= dt * state.UvRate;
    }

    private static Vector3 WorldFxHermite(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float u) {
      float u2 = u * u, u3 = u2 * u;
      float h00 = 2f * u3 - 3f * u2 + 1f, h10 = u3 - 2f * u2 + u;
      float h01 = -2f * u3 + 3f * u2, h11 = u3 - u2;
      return p1 * h00 + (p2 - p0) * (.5f * h10) + p2 * h01 + (p3 - p1) * (.5f * h11);
    }

    private IEnumerable<WorldFxRenderParticle> BuildWorldFxRenderParticles(WorldFxPlayer player) {
      if (player == null) yield break;
      foreach (WorldFxNodeRuntime runtime in player.Nodes) {
        WorldFxNodeDefinition node = runtime.Definition; WorldPrtSystem system = runtime.System; if (system == null) continue;
        WorldFxNodeDynamicTransform(player.Definition, node, player.Clock, out Vector3 nodeOffset, out Vector3 nodeRotation, out Vector3 nodeScale);
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
          Vector3 particlePosition = system.ResolveParticlePosition(p);
          Vector3 pos = RotateWorldFxVector(new Vector3(particlePosition.X * nodeScale.X, particlePosition.Y * nodeScale.Y, particlePosition.Z * nodeScale.Z), nodeRotation) + nodeOffset;
          Vector3 particleVelocity = system.ResolveParticleVelocity(p);
          particleVelocity = RotateWorldFxVector(new Vector3(particleVelocity.X * nodeScale.X, particleVelocity.Y * nodeScale.Y, particleVelocity.Z * nodeScale.Z), nodeRotation);
          if (spec.Bool("AlignToTrajectory") && particleVelocity.LengthSquared() <= 1e-8f)
            particleVelocity = WorldFxNodeExternalVelocity(player.Definition, node, player.Clock);
          Vector3 particleNodeRotation = nodeRotation;
          if (p.LinkedToOwner && p.Owner != null) {
            const float radToDeg = 180f / (float)Math.PI;
            Vector3 ownerDegrees = system.ResolveParticleOwnerRotation(p) * radToDeg;
            particleNodeRotation = ComposeWorldFxEulerDegrees(nodeRotation, ownerDegrees);
          }
          yield return new WorldFxRenderParticle {
            TexturePath = texture, Position = pos,
            HalfWidth = Math.Abs(size * sx * nodeScale.X), HalfHeight = Math.Abs(size * sy * nodeScale.Y), Rotation = p.Rotation,
            PivotU = spec.Number("UPivotOffset", 0f), PivotV = spec.Number("VPivotOffset", 0f),
            MinDistance = spec.Number("MinDistanceFromCamera", 0f), MaxDistance = spec.Number("MaxDistanceFromCamera", 0f),
            ScaleClampDistance = spec.Number("ScaleClampDistance", 0f), DistanceScaleAdjustment = spec.Number("DistanceScaleAdjustment", 1f),
            Columns = columns, Rows = rows, Frame = p.Frame, Color = color,
            OrientationAxis = (spec.Text("OrientationAxis", "XYZ") ?? "XYZ").Trim().ToUpperInvariant(),
            LieFlat = spec.Bool("LieFlat"), AlignToTrajectory = spec.Bool("AlignToTrajectory"), NodeRotation = particleNodeRotation,
            TrajectoryVelocity = particleVelocity,
            SourceBlend = (spec.Text("SourceBlend", "D3DBLEND_SRCALPHA") ?? "D3DBLEND_SRCALPHA").Trim().ToUpperInvariant(),
            DestBlend = (spec.Text("DestBlend", "D3DBLEND_ONE") ?? "D3DBLEND_ONE").Trim().ToUpperInvariant()
          };
        }
      }
    }

    private IEnumerable<WorldFxRenderRibbonSegment> BuildWorldFxRenderTrailSegments(WorldFxPlayer player) {
      if (player == null) yield break;
      foreach (WorldFxNodeRuntime runtime in player.Nodes) {
        WorldFxNodeDefinition node = runtime?.Definition; WorldPrtSystem system = runtime?.System;
        if (node == null || system == null) continue;
        WorldFxNodeDynamicTransform(player.Definition, node, player.Clock, out Vector3 nodeOffset, out Vector3 nodeRotation, out Vector3 nodeScale);
        foreach (WorldPrtParticle particle in system.Particles) {
          if (particle?.Spec == null) continue;
          foreach (WorldPrtTrail trail in new[] { particle.Trail, particle.Trail2 }) {
            if (trail == null || trail.Points.Count < 2) continue;
            string texture = NormalizeWorldOverheadTexturePath(particle.Spec.Text("TrailTexture"));
            if (String.IsNullOrWhiteSpace(texture) || !WorldOverheadTextureExists(texture)) continue;
            float lenMod = particle.Spec.Number("TrailLengthModifier", 1f);
            if (!(lenMod > .00001f)) lenMod = 1f;
            float span = trail.Decay > 0f ? trail.Decay * lenMod : 0f;
            Vector4 c1 = WorldFxTrackColor(particle.Spec, "TrailColor1", 0f, particle.Seed + 101u, new Vector4(0f, 0f, 0f, 0f));
            Vector4 c2 = WorldFxTrackColor(particle.Spec, "TrailColor2", 0f, particle.Seed + 102u, new Vector4(0f, 0f, 0f, 0f));
            Vector4 c3 = WorldFxTrackColor(particle.Spec, "TrailColor3", 0f, particle.Seed + 103u, new Vector4(1f, 1f, 1f, 1f));
            Vector4 c4 = WorldFxTrackColor(particle.Spec, "TrailColor4", 0f, particle.Seed + 104u, new Vector4(1f, 1f, 1f, 1f));
            for (int i = 0; i + 1 < trail.Points.Count; i++) {
              WorldPrtTrailPoint p0 = trail.Points[i], p1 = trail.Points[i + 1];
              float rel0 = span > 0f ? (system.Time - p0.Born) / span : (i == trail.Points.Count - 1 ? 0f : 1f);
              float rel1 = span > 0f ? (system.Time - p1.Born) / span : (i + 1 == trail.Points.Count - 1 ? 0f : 1f);
              float t0 = Math.Max(0f, Math.Min(1f, rel0)), t1 = Math.Max(0f, Math.Min(1f, rel1));
              Vector4 a0 = WorldFxLerp(c3, c1, t0), b0 = WorldFxLerp(c4, c2, t0);
              Vector4 a1 = WorldFxLerp(c3, c1, t1), b1 = WorldFxLerp(c4, c2, t1);
              Vector4 color = (a0 + b0 + a1 + b1) * .25f;
              color = new Vector4(color.X * node.Tint.X, color.Y * node.Tint.Y, color.Z * node.Tint.Z, color.W * node.Tint.W);
              Vector3 aStart = WorldFxApplyNodeTransform(p0.A, nodeOffset, nodeRotation, nodeScale);
              Vector3 bStart = WorldFxApplyNodeTransform(p0.B, nodeOffset, nodeRotation, nodeScale);
              Vector3 aEnd = WorldFxApplyNodeTransform(p1.A, nodeOffset, nodeRotation, nodeScale);
              Vector3 bEnd = WorldFxApplyNodeTransform(p1.B, nodeOffset, nodeRotation, nodeScale);
              yield return new WorldFxRenderRibbonSegment {
                TexturePath = texture, A0 = aStart, B0 = bStart, A1 = aEnd, B1 = bEnd,
                U0 = 1f - rel0, U1 = 1f - rel1, Color = color, Additive = true
              };
            }
          }
        }
      }
    }

    private IEnumerable<WorldFxRenderRibbonSegment> BuildWorldFxRenderLightningSegments(WorldFxPlayer player, Vector3 cameraLocal) {
      if (player == null) yield break;
      foreach (WorldFxNodeRuntime runtime in player.Nodes) {
        WorldFxNodeDefinition node = runtime?.Definition; WorldFxLightningState state = runtime?.Lightning;
        if (node == null || !node.IsLightning || state == null || !runtime.Started || runtime.Stopped ||
            state.Points.Count < 2 || !(state.Length >= .001f)) continue;
        string texture = state.TexturePath;
        if (String.IsNullOrWhiteSpace(texture) || !WorldOverheadTextureExists(texture)) continue;

        var positions = new List<Vector3>(Math.Min(WorldFxLightningMaxSamples, state.Points.Count * 4));
        var positionsT = new List<float>(positions.Capacity);
        for (int i = 0; i < state.Points.Count - 1 && positions.Count < WorldFxLightningMaxSamples; i++) {
          WorldFxLightningPoint p1 = state.Points[i], p2 = state.Points[i + 1];
          WorldFxLightningPoint p0 = i > 0 ? state.Points[i - 1] : p1;
          WorldFxLightningPoint p3 = i + 2 < state.Points.Count ? state.Points[i + 2] : p2;
          float distance = (p2.Position - p1.Position).Length();
          int segments = Math.Max(2, (int)(distance / state.TessellationDistance + .25f));
          for (int k = 0; k < segments && positions.Count < WorldFxLightningMaxSamples; k++) {
            float u = k / (float)segments;
            positions.Add(WorldFxHermite(p0.Position, p1.Position, p2.Position, p3.Position, u));
            positionsT.Add(p1.T + (p2.T - p1.T) * u);
          }
        }
        WorldFxLightningPoint tail = state.Points[state.Points.Count - 1];
        if (positions.Count < WorldFxLightningMaxSamples) { positions.Add(tail.Position); positionsT.Add(tail.T); }
        if (positions.Count < 2) continue;

        float dynamicAge = Math.Max(0f, player.Clock - state.ColorClock);
        var renderRandom = new WorldFxRandom(state.Seed);
        Vector4 dynamicColor = state.DynamicColor.Color(dynamicAge, renderRandom, new Vector4(1f, 1f, 1f, 1f));
        float tileLength = Math.Max(.0001f, state.WidthTaper * state.TextureTileDistance);
        Vector3 previousA = Vector3.Zero, previousB = Vector3.Zero, previousCenter = Vector3.Zero;
        Vector4 previousColor = new Vector4(1f, 1f, 1f, 1f);
        float previousU = state.UvOffset, along = state.UvOffset;
        bool havePrevious = false;
        for (int i = 0; i < positions.Count; i++) {
          Vector3 center = positions[i]; float t = positionsT[i];
          if (havePrevious) along += (center - previousCenter).Length() / tileLength;
          float half = Math.Abs(state.LightningShape.Number(t, renderRandom, 1f)) * state.RandomWidth * state.WidthAt * .5f;
          Vector3 tangent;
          if (i == 0) tangent = positions[1] - center;
          else if (i == positions.Count - 1) tangent = center - positions[i - 1];
          else tangent = positions[i + 1] - positions[i - 1];
          Vector3 toCamera = cameraLocal - center;
          Vector3 side = Vector3.Cross(tangent, toCamera);
          if (side.LengthSquared() <= 1e-8f) {
            Vector3 axis = state.End - state.Start;
            side = Math.Abs(axis.Y) > .99f ? Vector3.UnitX : Vector3.Cross(axis, Vector3.UnitY);
          }
          if (side.LengthSquared() <= 1e-8f) side = Vector3.UnitX; else side.Normalize();
          Vector3 a = center - side * half, b = center + side * half;
          float lengthColorT = (t - state.RampStart) * state.InvLengthColorRange;
          Vector4 lengthColor = state.LengthColor.Color(lengthColorT, renderRandom, new Vector4(1f, 1f, 1f, 1f));
          Vector4 color = new Vector4(lengthColor.X * dynamicColor.X * node.Tint.X, lengthColor.Y * dynamicColor.Y * node.Tint.Y,
            lengthColor.Z * dynamicColor.Z * node.Tint.Z, lengthColor.W * dynamicColor.W * node.Tint.W);
          if (havePrevious) {
            yield return new WorldFxRenderRibbonSegment {
              TexturePath = texture, A0 = previousA, B0 = previousB, A1 = a, B1 = b,
              U0 = previousU, U1 = along, Color = WorldFxLerp(previousColor, color, .5f), Additive = state.Additive
            };
          }
          previousCenter = center; previousA = a; previousB = b; previousColor = color; previousU = along; havePrevious = true;
        }
      }
    }

    private static void WorldFxProjectorHueRamp(Dictionary<string, string> fields, string suffix, uint seed,
        out bool enabled, out Vector4 h0, out Vector4 h1, out Vector4 h2, out Vector4 h3) {
      enabled = String.Equals(FxString(fields, "_fxProjectorUseHueing" + suffix), "true", StringComparison.OrdinalIgnoreCase);
      h0 = new Vector4(1f, 1f, 1f, 0f); h1 = new Vector4(1f, 1f, 1f, .333333f);
      h2 = new Vector4(1f, 1f, 1f, .666667f); h3 = new Vector4(1f, 1f, 1f, 1f);
      if (!enabled) return;
      WorldFxValue ramp = WorldFxValue.Parse(FxString(fields, "_fxProjectorHueRamp" + suffix));
      if (ramp == null || ramp.Kind == WorldFxValueKind.Empty || ramp.Kind == WorldFxValueKind.String) { enabled = false; return; }
      var rng = new WorldFxRandom(seed);
      if (ramp.Kind == WorldFxValueKind.Gradient && ramp.Stops != null && ramp.Stops.Count > 0) {
        Vector4[] baked = new Vector4[4];
        for (int i = 0; i < 4; i++) {
          WorldFxGradientStop stop = ramp.Stops[Math.Min(i, ramp.Stops.Count - 1)];
          Vector4 c = stop.Value.Color((float)stop.T, rng, new Vector4(1f, 1f, 1f, 1f));
          baked[i] = new Vector4(c.X, c.Y, c.Z, (float)stop.T);
        }
        h0 = baked[0]; h1 = baked[1]; h2 = baked[2]; h3 = baked[3];
        // Four-key engine baker duplicates the last colour when fewer keys are authored. Keep key positions monotonic
        // so malformed one-key ramps cannot divide by zero in the decal shader.
        h1.W = Math.Max(h1.W, h0.W + .0001f); h2.W = Math.Max(h2.W, h1.W + .0001f); h3.W = Math.Max(h3.W, h2.W + .0001f);
        return;
      }
      Vector4 single = ramp.Color(0f, rng, new Vector4(1f, 1f, 1f, 1f));
      h0 = new Vector4(single.X, single.Y, single.Z, 0f); h1 = new Vector4(single.X, single.Y, single.Z, .333333f);
      h2 = new Vector4(single.X, single.Y, single.Z, .666667f); h3 = new Vector4(single.X, single.Y, single.Z, 1f);
    }

    private IEnumerable<WorldFxProjectorDecal> BuildWorldFxProjectorDecals(WorldFxPlayer player) {
      if (player?.Definition?.Nodes == null) yield break;
      foreach (WorldFxNodeDefinition node in player.Definition.Nodes) {
        if (node == null || !node.IsProjector || player.Clock < node.StartAt) continue;
        string shapeText = FxString(node.Fields, "_fxProjectionShape", "PLANAR").Trim().ToUpperInvariant();
        // UVSPACE paints the receiver's own UV0. The screen-space depth pass deliberately has no fake UV source.
        if (shapeText == "UVSPACE") continue;
        bool onCharacters = String.Equals(FxString(node.Fields, "_fxProjectOnCharacters"), "true", StringComparison.OrdinalIgnoreCase);
        bool onCaster = String.Equals(FxString(node.Fields, "_fxProjectOnCaster"), "true", StringComparison.OrdinalIgnoreCase);
        bool onTarget = String.Equals(FxString(node.Fields, "_fxProjectOnTarget"), "true", StringComparison.OrdinalIgnoreCase);
        bool onObjects = String.Equals(FxString(node.Fields, "_fxProjectOnObjects"), "true", StringComparison.OrdinalIgnoreCase);
        bool anyReceiver = onCharacters || onCaster || onTarget || onObjects;
        bool wantsCaster = onCharacters || onCaster, wantsTarget = onCharacters || onTarget;
        // All-false is the engine default for "all receivers". A depth-only full-screen pass has no receiver ID, so
        // it is also safe when the explicit mask is effectively ALL (object + caster + target). Any partial mask
        // stays on the geometry fallback instead of accidentally painting a character as an object or vice versa.
        bool unrestrictedReceivers = !anyReceiver || (onObjects && wantsCaster && wantsTarget);
        if (!unrestrictedReceivers) continue;
        if (String.Equals(FxString(node.Fields, "_fxProjectOnModelsInFxOnly"), "true", StringComparison.OrdinalIgnoreCase)) continue;

        float elapsedNode = Math.Max(0f, player.Clock - node.StartAt);
        uint seed = unchecked((uint)(node.Name?.GetHashCode() ?? 1));
        float fade = 1f;
        WorldFxValue fadeIn = WorldFxValue.Parse(FxString(node.Fields, "_fxFadeIn"));
        WorldFxValue fadeOut = WorldFxValue.Parse(FxString(node.Fields, "_fxFadeOut"));
        if (WorldFxMethodToken(FxString(node.Fields, "_fxHowToStart")) == "FADEIN" && fadeIn.Kind == WorldFxValueKind.Gradient) {
          float dur = (float)(fadeIn.Duration ?? 1d);
          if (elapsedNode < Math.Max(.001f, dur)) fade *= fadeIn.Number(elapsedNode, new WorldFxRandom(seed + 11u), 1f);
        }
        if (!Single.IsInfinity(node.StopAt) && player.Clock >= node.StopAt) {
          if (WorldFxMethodToken(FxString(node.Fields, "_fxHowToStop")) != "FADEOUT" || fadeOut.Kind != WorldFxValueKind.Gradient) continue;
          float stopAge = Math.Max(0f, player.Clock - node.StopAt), dur = (float)(fadeOut.Duration ?? 1d);
          if (stopAge > Math.Max(.001f, dur)) continue;
          fade *= fadeOut.Number(stopAge, new WorldFxRandom(seed + 12u), 1f);
        }
        if (!(fade > .0005f)) continue;

        WorldFxNodeDynamicTransform(player.Definition, node, player.Clock, out Vector3 offset, out Vector3 rotation, out Vector3 scale);
        scale = new Vector3(Math.Abs(scale.X), Math.Abs(scale.Y), Math.Abs(scale.Z));
        if (!(scale.X > .00001f) || !(scale.Y > .00001f) || !(scale.Z > .00001f)) continue;
        float rad = (float)Math.PI / 180f;
        Matrix localWorld = Matrix.Scaling(scale) * Matrix.RotationX(rotation.X * rad) * Matrix.RotationY(rotation.Y * rad) *
          Matrix.RotationZ(rotation.Z * rad) * Matrix.Translation(offset);
        int shape = shapeText == "CYLINDER" ? 1 : (shapeText == "CUBE" || shapeText == "STATICCUBE" || shapeText == "STATICCUBE_PLANAR") ? 2 : 0;
        bool twoSided = String.Equals(FxString(node.Fields, "_fxProjectTwoSided"), "true", StringComparison.OrdinalIgnoreCase);

        foreach (string suffix in new[] { String.Empty, "_layer1" }) {
          string texture = FxString(node.Fields, "_fxProjectionTexture" + suffix);
          if (String.IsNullOrWhiteSpace(texture)) continue;
          WorldFxValue colorTrack = WorldFxValue.Parse(FxString(node.Fields, "_fxProjectionColor" + suffix, "#1,1,1,1"));
          Vector4 c = colorTrack.Color(elapsedNode, new WorldFxRandom(seed + (suffix.Length == 0 ? 20u : 30u)), new Vector4(1f, 1f, 1f, 1f));
          float intensity = Math.Max(0f, WorldFxFieldScalar(node.Fields, "_fxProjectionColorIntensity" + suffix, elapsedNode, seed + 21u, 1f)) * fade;
          Vector4 color = new Vector4(c.X * intensity * node.Tint.X, c.Y * intensity * node.Tint.Y, c.Z * intensity * node.Tint.Z, c.W * node.Tint.W);
          if (color.W <= .0001f && color.X <= .0001f && color.Y <= .0001f && color.Z <= .0001f) continue;
          float uTile = WorldFxFieldScalar(node.Fields, "_fxUTileRate" + suffix, elapsedNode, seed + 22u, 1f);
          float vTile = WorldFxFieldScalar(node.Fields, "_fxVTileRate" + suffix, elapsedNode, seed + 23u, 1f);
          float uScroll = WorldFxIntegrateTrack(WorldFxValue.Parse(FxString(node.Fields, "_fxUScrollRate" + suffix)), elapsedNode, seed + 24u);
          float vScroll = WorldFxIntegrateTrack(WorldFxValue.Parse(FxString(node.Fields, "_fxVScrollRate" + suffix)), elapsedNode, seed + 25u);
          float falloffStart = WorldFxFieldScalar(node.Fields, "_fxFalloffAngleStart" + suffix, elapsedNode, seed + 26u, 90f);
          float falloffStop = WorldFxFieldScalar(node.Fields, "_fxFalloffAngleStop" + suffix, elapsedNode, seed + 27u, 90f);
          float fresnelEdge = WorldFxFieldScalar(node.Fields, "_fxFresnelEdge" + suffix, elapsedNode, seed + 28u, 1f);
          float fresnelFacing = WorldFxFieldScalar(node.Fields, "_fxFresnelFacing" + suffix, elapsedNode, seed + 29u, 1f);
          float d2r = (float)Math.PI / 180f;
          string blend = (suffix.Length == 0 ? FxString(node.Fields, "_fxProjectionType") : FxString(node.Fields, "_fxBlendMode_layer1")).Trim().ToUpperInvariant();
          WorldFxProjectorHueRamp(node.Fields, suffix, seed + (suffix.Length == 0 ? 40u : 50u), out bool hueOn,
            out Vector4 hue0, out Vector4 hue1, out Vector4 hue2, out Vector4 hue3);
          yield return new WorldFxProjectorDecal {
            Node = node, TexturePath = texture, LocalWorld = localWorld, Color = color,
            Uv = new Vector4(uTile, vTile, uScroll, vScroll),
            Falloff = new Vector4((float)Math.Cos(falloffStart * d2r), (float)Math.Cos(falloffStop * d2r), fresnelEdge, fresnelFacing),
            HueOn = hueOn, Hue0 = hue0, Hue1 = hue1, Hue2 = hue2, Hue3 = hue3,
            Shape = shape, TwoSided = twoSided, Additive = blend == "ADDITIVE" || blend == "ADD", Multiply = blend == "MULT" || blend == "MULTIPLY"
          };
        }
      }
    }

    // Fallback for projectors the receiver-space pass cannot classify safely: render the authored projection VOLUME
    // as Jedipedia's own preview fallback does (UVSPACE, caster/target-only, FX-only, sky, or unavailable depth):
    // planar/UV-space cards, cylinder shells and cube-family boxes, with both texture layers, UV tiling/scroll and
    // additive/alpha/multiply blending. This keeps projector-heavy scan/shield effects visible without pretending the
    // stand-in is already the final receiver-paint pass.
    private IEnumerable<WorldFxRenderRibbonSegment> BuildWorldFxRenderProjectorSegments(WorldFxPlayer player, HashSet<WorldFxNodeDefinition> painted = null) {
      if (player?.Definition?.Nodes == null) yield break;
      foreach (WorldFxNodeDefinition node in player.Definition.Nodes) {
        if (node == null || !node.IsProjector || player.Clock < node.StartAt || (painted != null && painted.Contains(node))) continue;
        float elapsedNode = Math.Max(0f, player.Clock - node.StartAt);
        uint seed = unchecked((uint)(node.Name?.GetHashCode() ?? 1));
        float fade = 1f;
        string startMethod = WorldFxMethodToken(FxString(node.Fields, "_fxHowToStart"));
        string stopMethod = WorldFxMethodToken(FxString(node.Fields, "_fxHowToStop"));
        WorldFxValue fadeIn = WorldFxValue.Parse(FxString(node.Fields, "_fxFadeIn"));
        WorldFxValue fadeOut = WorldFxValue.Parse(FxString(node.Fields, "_fxFadeOut"));
        if (startMethod == "FADEIN" && fadeIn.Kind == WorldFxValueKind.Gradient) {
          float dur = (float)(fadeIn.Duration ?? 1d);
          if (elapsedNode < Math.Max(.001f, dur)) fade *= fadeIn.Number(elapsedNode, new WorldFxRandom(seed + 11u), 1f);
        }
        if (!Single.IsInfinity(node.StopAt) && player.Clock >= node.StopAt) {
          if (stopMethod != "FADEOUT" || fadeOut.Kind != WorldFxValueKind.Gradient) continue;
          float stopAge = Math.Max(0f, player.Clock - node.StopAt), dur = (float)(fadeOut.Duration ?? 1d);
          if (stopAge > Math.Max(.001f, dur)) continue;
          fade *= fadeOut.Number(stopAge, new WorldFxRandom(seed + 12u), 1f);
        }
        if (!(fade > .0005f)) continue;

        WorldFxNodeDynamicTransform(player.Definition, node, player.Clock, out Vector3 offset, out Vector3 rotation, out Vector3 scale);
        string shape = FxString(node.Fields, "_fxProjectionShape", "PLANAR").Trim().ToUpperInvariant();
        float maxExtent = Math.Max(Math.Abs(scale.X), Math.Max(Math.Abs(scale.Y), Math.Abs(scale.Z)));
        float visualFloor = (shape == "PLANAR" || shape == "DUAL_PLANAR" || shape == "UVSPACE") ? .15f : .2f;
        if (!(maxExtent > .000001f)) scale = new Vector3(visualFloor, visualFloor, visualFloor);
        else if (maxExtent < visualFloor) { float k = visualFloor / maxExtent; scale *= k; }

        foreach (string suffix in new[] { String.Empty, "_layer1" }) {
          string texture = FxString(node.Fields, "_fxProjectionTexture" + suffix);
          if (String.IsNullOrWhiteSpace(texture)) continue;
          WorldFxValue colorTrack = WorldFxValue.Parse(FxString(node.Fields, "_fxProjectionColor" + suffix, "#1,1,1,1"));
          Vector4 color = colorTrack.Color(elapsedNode, new WorldFxRandom(seed + (suffix.Length == 0 ? 20u : 30u)), new Vector4(1f, 1f, 1f, 1f));
          float intensity = Math.Max(0f, WorldFxFieldScalar(node.Fields, "_fxProjectionColorIntensity" + suffix, elapsedNode, seed + 21u, 1f));
          color = new Vector4(color.X * intensity * fade * node.Tint.X, color.Y * intensity * fade * node.Tint.Y,
            color.Z * intensity * fade * node.Tint.Z, color.W * fade * node.Tint.W);
          if (color.W <= .0001f && color.X <= .0001f && color.Y <= .0001f && color.Z <= .0001f) continue;

          float uTile = WorldFxFieldScalar(node.Fields, "_fxUTileRate" + suffix, elapsedNode, seed + 22u, 1f);
          float vTile = WorldFxFieldScalar(node.Fields, "_fxVTileRate" + suffix, elapsedNode, seed + 23u, 1f);
          float uScroll = WorldFxIntegrateTrack(WorldFxValue.Parse(FxString(node.Fields, "_fxUScrollRate" + suffix)), elapsedNode, seed + 24u);
          float vScroll = WorldFxIntegrateTrack(WorldFxValue.Parse(FxString(node.Fields, "_fxVScrollRate" + suffix)), elapsedNode, seed + 25u);
          string blend = (suffix.Length == 0 ? FxString(node.Fields, "_fxProjectionType") : FxString(node.Fields, "_fxBlendMode_layer1")).Trim().ToUpperInvariant();
          bool additive = blend == "ADDITIVE" || blend == "ADD";
          bool multiply = blend == "MULT" || blend == "MULTIPLY";

          Func<Vector3, Vector3> tx = local => WorldFxApplyNodeTransform(local, offset, rotation, scale);
          Func<Vector3, Vector3, Vector3, Vector3, WorldFxRenderRibbonSegment> quad = (p00, p01, p11, p10) => new WorldFxRenderRibbonSegment {
            TexturePath = texture, A0 = tx(p00), B0 = tx(p01), B1 = tx(p11), A1 = tx(p10),
            U0 = uScroll, U1 = uScroll + uTile, V0 = vScroll, V1 = vScroll + vTile,
            Color = color, Additive = additive, Multiply = multiply
          };

          if (shape == "CYLINDER") {
            const int sides = 12;
            for (int i = 0; i < sides; i++) {
              float a0 = (float)(i * Math.PI * 2.0 / sides), a1 = (float)((i + 1) * Math.PI * 2.0 / sides);
              Vector3 p00 = new Vector3((float)Math.Cos(a0), (float)Math.Sin(a0), -1f);
              Vector3 p01 = new Vector3((float)Math.Cos(a0), (float)Math.Sin(a0), 1f);
              Vector3 p11 = new Vector3((float)Math.Cos(a1), (float)Math.Sin(a1), 1f);
              Vector3 p10 = new Vector3((float)Math.Cos(a1), (float)Math.Sin(a1), -1f);
              WorldFxRenderRibbonSegment seg = quad(p00, p01, p11, p10);
              seg.U0 = uScroll + uTile * i / sides; seg.U1 = uScroll + uTile * (i + 1) / sides;
              yield return seg;
            }
          } else if (shape == "CUBE" || shape == "STATICCUBE" || shape == "STATICCUBE_PLANAR") {
            yield return quad(new Vector3(-1,-1,-1), new Vector3(-1,1,-1), new Vector3(1,1,-1), new Vector3(1,-1,-1));
            yield return quad(new Vector3(1,-1,1), new Vector3(1,1,1), new Vector3(-1,1,1), new Vector3(-1,-1,1));
            yield return quad(new Vector3(-1,-1,1), new Vector3(-1,1,1), new Vector3(-1,1,-1), new Vector3(-1,-1,-1));
            yield return quad(new Vector3(1,-1,-1), new Vector3(1,1,-1), new Vector3(1,1,1), new Vector3(1,-1,1));
            yield return quad(new Vector3(-1,1,-1), new Vector3(-1,1,1), new Vector3(1,1,1), new Vector3(1,1,-1));
            yield return quad(new Vector3(-1,-1,1), new Vector3(-1,-1,-1), new Vector3(1,-1,-1), new Vector3(1,-1,1));
          } else {
            // PLANAR, DUAL_PLANAR and UVSPACE all use the local XY footprint/card in Jedipedia's stand-in path.
            yield return quad(new Vector3(-1,-1,0), new Vector3(-1,1,0), new Vector3(1,1,0), new Vector3(1,-1,0));
            if (shape == "DUAL_PLANAR")
              yield return quad(new Vector3(1,-1,0), new Vector3(1,1,0), new Vector3(-1,1,0), new Vector3(-1,-1,0));
          }
        }
      }
    }

    private IEnumerable<WorldFxRenderGlow> BuildWorldFxRenderGlows(WorldFxPlayer player) {
      if (player?.Definition?.Nodes == null) yield break;
      foreach (WorldFxNodeDefinition node in player.Definition.Nodes) {
        if (node == null || !node.IsLight || player.Clock < node.StartAt) continue;
        bool stopping = !Single.IsInfinity(node.StopAt) && player.Clock >= node.StopAt;
        float elapsedNode = Math.Max(0f, player.Clock - node.StartAt);
        uint seed = unchecked((uint)(node.Name?.GetHashCode() ?? 1));
        WorldFxValue colorTrack = WorldFxValue.Parse(FxString(node.Fields, "_fxColor", "#1,1,1,1"));
        Vector4 color = colorTrack.Color(elapsedNode, new WorldFxRandom(seed + 1u), new Vector4(1f, 1f, 1f, 1f));

        float intensity;
        WorldFxValue fadeIn = WorldFxValue.Parse(FxString(node.Fields, "_fxFadeIn"));
        WorldFxValue fadeOut = WorldFxValue.Parse(FxString(node.Fields, "_fxFadeOut"));
        bool usableFadeIn = WorldFxScalarTrackUsable(fadeIn), usableFadeOut = WorldFxScalarTrackUsable(fadeOut);
        if (stopping) {
          // LightTask::Stop replaces the intensity evaluator with _fxFadeOut and keeps the light alive only for that
          // track's own duration. Without FADEOUT the light disappears immediately at StopAt.
          if (WorldFxMethodToken(FxString(node.Fields, "_fxHowToStop")) != "FADEOUT" || !usableFadeOut) continue;
          float stopAge = Math.Max(0f, player.Clock - node.StopAt), duration = (float)(fadeOut.Duration ?? 1d);
          if (stopAge > Math.Max(.001f, duration)) continue;
          intensity = fadeOut.Number(stopAge, new WorldFxRandom(seed + 2u), 1f);
        } else if (WorldFxMethodToken(FxString(node.Fields, "_fxHowToStart")) == "FADEIN" && usableFadeIn &&
                   elapsedNode < Math.Max(.001f, (float)(fadeIn.Duration ?? 1d))) {
          // Same replacement semantics on start: while FadeIn runs, it IS the light intensity rather than a multiplier.
          intensity = fadeIn.Number(elapsedNode, new WorldFxRandom(seed + 2u), 1f);
        } else {
          intensity = WorldFxFieldScalar(node.Fields, "_fxIntensity", elapsedNode, seed + 2u, 1f);
        }

        float range = Math.Max(0f, WorldFxFieldScalar(node.Fields, "_fxRange", elapsedNode, seed + 3u, 0f));
        if (!(range > .0001f) || !(intensity > .0001f)) continue;
        WorldFxNodeDynamicTransform(player.Definition, node, player.Clock, out Vector3 position, out _, out _);
        yield return new WorldFxRenderGlow {
          Position = position, Radius = range,
          Color = new Vector4(color.X * intensity * node.Tint.X, color.Y * intensity * node.Tint.Y,
            color.Z * intensity * node.Tint.Z, Math.Max(0f, color.W * node.Tint.W))
        };
      }
    }

    private static bool WorldFxScalarTrackUsable(WorldFxValue value) {
      return value != null && value.Kind != WorldFxValueKind.Empty && value.Kind != WorldFxValueKind.String && value.Kind != WorldFxValueKind.Bool;
    }

    private static Vector3 WorldFxApplyNodeTransform(Vector3 value, Vector3 offset, Vector3 rotation, Vector3 scale) {
      value = new Vector3(value.X * scale.X, value.Y * scale.Y, value.Z * scale.Z);
      return RotateWorldFxVector(value, rotation) + offset;
    }

    private static Vector4 WorldFxTrackColor(WorldPrtSpec spec, string key, float t, uint seed, Vector4 fallback) {
      WorldFxValue track = spec?.Track(key); return track?.Color(t, new WorldFxRandom(seed), fallback) ?? fallback;
    }

    private static float WorldFxFieldScalar(Dictionary<string, string> fields, string key, float t, uint seed, float fallback) {
      string raw = FxString(fields, key); if (String.IsNullOrWhiteSpace(raw)) return fallback;
      return WorldFxValue.Parse(raw).Number(t, new WorldFxRandom(seed), fallback);
    }

    private static float WorldFxIntegrateTrack(WorldFxValue track, float duration, uint seed) {
      if (track == null || track.Kind == WorldFxValueKind.Empty || !(duration > 0f)) return 0f;
      const int steps = 8;
      float h = duration / steps, sum = 0f;
      for (int i = 0; i <= steps; i++) {
        float t = h * i, y = track.Number(t, new WorldFxRandom(seed + (uint)i), 0f);
        sum += y * (i == 0 || i == steps ? .5f : 1f);
      }
      return sum * h;
    }

    private static string WorldFxMethodToken(string raw) {
      return (raw ?? String.Empty).Trim().Replace("_", String.Empty).Replace(" ", String.Empty).ToUpperInvariant();
    }

    private static Vector4 WorldFxLerp(Vector4 a, Vector4 b, float t) {
      return new Vector4(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Z + (b.Z - a.Z) * t, a.W + (b.W - a.W) * t);
    }

    private IEnumerable<WorldFxRenderModel> BuildWorldFxRenderModels(WorldFxPlayer player, Matrix fxFrame) {
      if (player == null) yield break;
      foreach (WorldFxNodeRuntime runtime in player.Nodes) {
        WorldFxNodeDefinition node = runtime.Definition; if (node == null || !runtime.Started) continue;
        if (node.IsModel) {
          GR2 model = runtime.Model ?? LoadWorldFxModel(node.Resource); if (model == null) continue;
          float alpha = WorldFxModelFadeAlpha(node, player.Clock) * WorldFxModelFaderAlpha(player.Definition, node, player.Clock); if (!(alpha > .0005f)) continue;
          Matrix local = WorldFxNodeLocalMatrix(node, player.Definition, player.Clock);
          Vector4 faderColor = WorldFxModelFaderColor(player.Definition, node, player.Clock);
          Vector4 tint = new Vector4(node.Tint.X * faderColor.X, node.Tint.Y * faderColor.Y, node.Tint.Z * faderColor.Z,
            node.Tint.W * faderColor.W * alpha);
          yield return new WorldFxRenderModel { Model = model, World = local * fxFrame, Tint = tint };
          continue;
        }
        WorldPrtSystem system = runtime.System; if (system == null) continue;
        WorldFxNodeDynamicTransform(player.Definition, node, player.Clock, out _, out Vector3 liveNodeRotation, out Vector3 liveNodeScale);
        Matrix nodeFrame = WorldFxNodeLocalMatrix(node, player.Definition, player.Clock) * fxFrame;
        foreach (WorldPrtParticle particle in system.Particles) {
          WorldPrtSpec spec = particle?.Spec; if (spec == null || spec.Type != "GRANNY") continue;
          string granny = spec.Text("GrannyFileName"); GR2 model = LoadWorldFxModel(granny); if (model == null) continue;
          float t = particle.Life > 0 ? Math.Max(0f, Math.Min(1f, particle.Age / particle.Life)) : 0f;
          var seeded = new WorldFxRandom(particle.Seed);
          float uniform = spec.Track("UniformScale3D")?.Number(t, seeded, 1f) ?? 1f;
          if (!Single.IsFinite(uniform) || Math.Abs(uniform) < .000001f) uniform = 1f;
          Vector3 scale = spec.Track("Scale3D")?.Vector3(t, seeded, new Vector3(1f, 1f, 1f)) ?? new Vector3(1f, 1f, 1f);
          scale *= uniform;
          Vector3 particlePosition = system.ResolveParticlePosition(particle);
          Vector3 particleVelocity = system.ResolveParticleVelocity(particle);
          Vector3 ownerRotation = system.ResolveParticleOwnerRotation(particle);
          Matrix local = Matrix.Scaling(scale);
          if (spec.Bool("AlignToTrajectory")) {
            // A stationary mesh leaf on a moving projectile borrows the parent's chase/arc velocity. Convert that
            // effect-space vector back through the node frame because the GR2 alignment matrix is composed before
            // WorldFxNodeLocalMatrix below. This is the same extVel fallback used by Jedipedia's PRT drawer.
            if (particleVelocity.LengthSquared() <= 1e-8f) {
              Vector3 ext = WorldFxNodeExternalVelocity(player.Definition, node, player.Clock);
              if (ext.LengthSquared() > 1e-8f) {
                particleVelocity = InverseRotateWorldFxVector(ext, liveNodeRotation);
                if (Math.Abs(liveNodeScale.X) > 1e-6f) particleVelocity.X /= liveNodeScale.X;
                if (Math.Abs(liveNodeScale.Y) > 1e-6f) particleVelocity.Y /= liveNodeScale.Y;
                if (Math.Abs(liveNodeScale.Z) > 1e-6f) particleVelocity.Z /= liveNodeScale.Z;
              }
            }
            if (particleVelocity.LengthSquared() > 1e-8f) local *= WorldFxAlignYTo(particleVelocity);
          }
          local *= Matrix.RotationX(particle.Rotation3D.X) * Matrix.RotationY(particle.Rotation3D.Y) * Matrix.RotationZ(particle.Rotation3D.Z);
          if (particle.LinkedToOwner && particle.Owner != null)
            local *= Matrix.RotationX(ownerRotation.X) * Matrix.RotationY(ownerRotation.Y) * Matrix.RotationZ(ownerRotation.Z);
          local *= Matrix.Translation(particlePosition);
          Vector4 color = spec.Track("DiffuseColor")?.Color(t, seeded, new Vector4(1f, 1f, 1f, 1f)) ?? new Vector4(1f, 1f, 1f, 1f);
          color = new Vector4(color.X * node.Tint.X, color.Y * node.Tint.Y, color.Z * node.Tint.Z, color.W * node.Tint.W);
          yield return new WorldFxRenderModel { Model = model, World = local * nodeFrame, Tint = color };
        }
      }
    }

    private static Matrix WorldFxNodeLocalMatrix(WorldFxNodeDefinition node, WorldFxSpecDefinition definition, float clock) {
      WorldFxNodeDynamicTransform(definition, node, clock, out Vector3 offset, out Vector3 rotation, out Vector3 scale);
      float r = (float)Math.PI / 180f;
      return Matrix.Scaling(scale) * Matrix.RotationX(rotation.X * r) * Matrix.RotationY(rotation.Y * r) *
        Matrix.RotationZ(rotation.Z * r) * Matrix.Translation(offset);
    }

    private static void ApplyWorldFxHostFollow(WorldFxSpecDefinition definition, WorldFxNodeDefinition node,
        ref Vector3 offset, ref Vector3 rotation, ref Vector3 scale) {
      if (definition?.HostContext == null || node == null || !node.HostFollowInitialFrame.HasValue || String.IsNullOrWhiteSpace(node.HostFollowAnchor)) return;
      Matrix initial = node.HostFollowInitialFrame.Value;
      Matrix current = WorldFxHostAnchorFrame(definition, node.HostFollowAnchor, node.HostFollowBone);
      Matrix delta;
      try { delta = Matrix.Invert(initial) * current; } catch { return; }
      offset = Vector3.TransformCoordinate(offset, delta);
      WorldFxMatrixParts(delta, out _, out Vector3 deltaRotation, out Vector3 deltaScale);
      if (deltaRotation.LengthSquared() > 1e-10f) rotation = ComposeWorldFxEulerDegrees(deltaRotation, rotation);
      scale = new Vector3(scale.X * deltaScale.X, scale.Y * deltaScale.Y, scale.Z * deltaScale.Z);
    }

    // Transformer/attacher/arc operators share one compact overlay path. Transformers preserve the authored per-axis
    // curves; attachers can re-parent an asset at runtime; TransformArc moves the named asset (and its attachment
    // closure/owner bundle) along the HeroEngine parabolic +Y bow. The latter two are deliberately resolved against a
    // transformer-only target pose so operator cycles cannot recurse forever.
    private static void WorldFxNodeDynamicTransform(WorldFxSpecDefinition definition, WorldFxNodeDefinition node, float clock,
        out Vector3 offset, out Vector3 rotation, out Vector3 scale) {
      var resolving = new HashSet<WorldFxNodeDefinition>();
      WorldFxNodeDynamicTransformCore(definition, node, clock, resolving, out offset, out rotation, out scale);
    }

    private static void WorldFxNodeDynamicTransformCore(WorldFxSpecDefinition definition, WorldFxNodeDefinition node, float clock,
        HashSet<WorldFxNodeDefinition> resolving, out Vector3 offset, out Vector3 rotation, out Vector3 scale) {
      WorldFxNodeTransformerOnlyTransform(definition, node, clock, out offset, out rotation, out scale);
      ApplyWorldFxHostFollow(definition, node, ref offset, ref rotation, ref scale);
      if (definition?.Nodes == null || node == null || String.IsNullOrWhiteSpace(node.Name)) return;
      if (resolving == null) resolving = new HashSet<WorldFxNodeDefinition>();
      if (!resolving.Add(node)) return;
      try {

      // _fxAttacherList is a one-shot operator: when its delay expires, move _fxAttachThis into a new attachment frame.
      // Keep the pre-attacher pose so a later DEFAULT/NOTHING operator can detach it again in this stateless replay.
      Vector3 preAttachOffset = offset, preAttachRotation = rotation, preAttachScale = scale;
      foreach (WorldFxNodeDefinition attacher in definition.Nodes) {
        if (attacher == null || !attacher.IsAttacher) continue;
        float attachAt = attacher.StartAt + ParseWorldFxTime(FxString(attacher.Fields, "_fxAttachDelay"));
        string movedName = FxString(attacher.Fields, "_fxAttachThis", FxString(attacher.Fields, "_fxAssetName"));
        if (clock < attachAt || !String.Equals(movedName, node.Name, StringComparison.OrdinalIgnoreCase)) continue;
        string targetName = FxString(attacher.Fields, "_fxAttachTo");
        // Attachers are one-shot re-parent operators, not start/stop visuals. DEFAULT/NOTHING detaches the asset into
        // world space; in this stateless overlay that means stop applying a parent override from this operator.
        if (String.Equals(targetName, "DEFAULT", StringComparison.OrdinalIgnoreCase) || String.Equals(targetName, "NOTHING", StringComparison.OrdinalIgnoreCase)) {
          offset = preAttachOffset; rotation = preAttachRotation; scale = preAttachScale; continue;
        }
        if (String.IsNullOrWhiteSpace(targetName)) targetName = "CASTER";
        WorldFxResolveOperatorAnchor(definition, attacher, targetName, clock, out Vector3 parentPos, out Vector3 parentRot, out Vector3 parentScale,
          FxString(attacher.Fields, "_fxAttachBone"), resolving, true);
        Vector3 local = ParseWorldFxVector(FxString(attacher.Fields, "_fxAttachPosition"), Vector3.Zero);
        local = new Vector3(local.X * parentScale.X, local.Y * parentScale.Y, local.Z * parentScale.Z);
        offset = parentPos + RotateWorldFxVector(local, parentRot);
        Vector3 attachRot = WorldFxAuthoredYprToEulerDegrees(ParseWorldFxVector(FxString(attacher.Fields, "_fxAttachRotation"), Vector3.Zero));
        if (attachRot.LengthSquared() > 1e-8f) rotation = ComposeWorldFxEulerDegrees(parentRot, attachRot);
        scale = new Vector3(scale.X * parentScale.X, scale.Y * parentScale.Y, scale.Z * parentScale.Z);
      }

      // Chasing emitters write displacement only to their own node. The named-attachment overlay below carries that
      // live motion through the hierarchy, which lets child transformers keep their own motion on top of the chase.
      foreach (WorldFxNodeDefinition chaser in definition.Nodes) {
        // Jedipedia writes chase displacement only onto the chaser itself. Named children inherit that live overlay
        // in the attachment pass below; stamping the displacement onto the whole closure would double-move them.
        if (chaser == null || !chaser.IsChaser || clock < chaser.StartAt || !String.Equals(node.Name, chaser.Name, StringComparison.OrdinalIgnoreCase)) continue;
        float sampleClock = !Single.IsInfinity(chaser.StopAt) ? Math.Min(clock, chaser.StopAt) : clock;
        float elapsed = Math.Max(0f, Math.Min(chaser.ChaseTravelTime, sampleClock - chaser.StartAt));
        float travel = Math.Min(chaser.ChaseReach, chaser.ChaseSpeed * elapsed);
        offset += chaser.ChaseDirection * travel;
        if (travel > .0001f && chaser.ChaseDirection.LengthSquared() > 1e-8f)
          rotation = ComposeWorldFxEulerDegrees(WorldFxArrowEulerDegrees(chaser.ChaseDirection), rotation);
      }

      // TransformArc SETS a rigid displacement for the moved asset and the authored bundle riding it.
      bool arcStamped = false;
      foreach (WorldFxNodeDefinition arc in definition.Nodes) {
        if (arc == null || !arc.IsArc || clock < arc.StartAt || !WorldFxArcMovesNode(definition, arc, node)) continue;
        arcStamped = true;
        float sampleClock = !Single.IsInfinity(arc.StopAt) ? Math.Min(clock, arc.StopAt) : clock;
        float duration = Math.Max(.001f, ParseWorldFxTime(FxString(arc.Fields, "_fxDuration")));
        float u = Math.Max(0f, Math.Min(1f, (sampleClock - arc.StartAt) / duration));
        Vector3 startPos = WorldFxResolveArcLocation(definition, arc, FxString(arc.Fields, "_fxStartLoc", "DEFAULT"),
          FxString(arc.Fields, "_fxOffset"), clock, FxString(arc.Fields, "_fxStartLocBone"), resolving);
        Vector3 targetPos = WorldFxResolveArcLocation(definition, arc, FxString(arc.Fields, "_fxTargetLoc", "TARGET"),
          FxString(arc.Fields, "_fxTargetOffset"), clock, FxString(arc.Fields, "_fxTargetLocBone"), resolving);
        Vector3 delta = targetPos - startPos;
        float height = 0f; Single.TryParse(FxString(arc.Fields, "_fxArcSize", "0"), NumberStyles.Float, CultureInfo.InvariantCulture, out height);
        Vector3 travel = delta * u; travel.Y += height * 4f * u * (1f - u);
        offset += travel;

        if (String.Equals(FxString(arc.Fields, "_fxRotateWithArc"), "true", StringComparison.OrdinalIgnoreCase)) {
          Vector3 tangent = new Vector3(delta.X, delta.Y + height * 4f * (1f - 2f * u), delta.Z);
          if (tangent.LengthSquared() > 1e-8f) rotation = ComposeWorldFxEulerDegrees(WorldFxArrowEulerDegrees(tangent), rotation);
        }
      }

      // Named AttachTo children inherit the PARENT'S LIVE position/rotation overlay after operators. The base
      // hierarchy was already baked once by ResolveWorldFxNodeTransforms; here we add only what changed since that
      // authored base pose. Dynamic scale is deliberately not inherited, matching Jedipedia's applyAttachOverlays.
      // TransformArc already stamps the entire attachment closure, so its riders skip this pass for the frame.
      if (!arcStamped && !WorldFxNodeHasActiveAttacher(definition, node, clock))
        ApplyWorldFxNamedAttachmentFollow(definition, node, clock, resolving, ref offset, ref rotation);
      } finally {
        resolving.Remove(node);
      }
    }

    private static bool WorldFxNodeHasActiveAttacher(WorldFxSpecDefinition definition, WorldFxNodeDefinition node, float clock) {
      if (definition?.Nodes == null || node == null || String.IsNullOrWhiteSpace(node.Name)) return false;
      foreach (WorldFxNodeDefinition attacher in definition.Nodes) {
        if (attacher == null || !attacher.IsAttacher) continue;
        string movedName = FxString(attacher.Fields, "_fxAttachThis", FxString(attacher.Fields, "_fxAssetName"));
        if (!String.Equals(movedName, node.Name, StringComparison.OrdinalIgnoreCase)) continue;
        float attachAt = attacher.StartAt + ParseWorldFxTime(FxString(attacher.Fields, "_fxAttachDelay"));
        if (clock >= attachAt) return true;
      }
      return false;
    }

    private static Matrix WorldFxRotationMatrix(Vector3 rotation) {
      float r = (float)Math.PI / 180f;
      return Matrix.RotationX(rotation.X * r) * Matrix.RotationY(rotation.Y * r) * Matrix.RotationZ(rotation.Z * r);
    }

    private static void ApplyWorldFxNamedAttachmentFollow(WorldFxSpecDefinition definition, WorldFxNodeDefinition node, float clock,
        HashSet<WorldFxNodeDefinition> resolving, ref Vector3 offset, ref Vector3 rotation) {
      string parentName = (node?.AttachTo ?? String.Empty).Trim();
      if (parentName.Length == 0 || WorldFxExternalAnchor(parentName) || definition?.Nodes == null) return;
      WorldFxNodeDefinition parent = definition.Nodes.FirstOrDefault(x => x != null && String.Equals(x.Name, parentName, StringComparison.OrdinalIgnoreCase));
      if (parent == null || ReferenceEquals(parent, node) || (resolving != null && resolving.Contains(parent))) return;

      WorldFxNodeDynamicTransformCore(definition, parent, clock, resolving, out Vector3 parentLiveOffset, out Vector3 parentLiveRotation, out _);
      Vector3 parentDelta = parentLiveOffset - parent.Offset;
      Vector3 baseRelative = node.Offset - parent.Offset;
      Matrix deltaRotation;
      try { deltaRotation = Matrix.Invert(WorldFxRotationMatrix(parent.Rotation)) * WorldFxRotationMatrix(parentLiveRotation); }
      catch { deltaRotation = Matrix.Identity; }
      Vector3 swung = Vector3.TransformNormal(baseRelative, deltaRotation);
      offset += parentDelta + (swung - baseRelative);
      WorldFxMatrixParts(deltaRotation, out _, out Vector3 deltaEuler, out _);
      if (deltaEuler.LengthSquared() > 1e-10f) rotation = ComposeWorldFxEulerDegrees(deltaEuler, rotation);
    }

    private static bool TryWorldFxResolveBaseLocation(WorldFxSpecDefinition definition, string name, string boneName, out Vector3 position) {
      position = Vector3.Zero;
      string up = (name ?? String.Empty).Trim();
      if (up.Length == 0 || String.Equals(up, "DEFAULT", StringComparison.OrdinalIgnoreCase) || String.Equals(up, "NOTHING", StringComparison.OrdinalIgnoreCase)) return false;
      if (WorldFxActorAnchor(up)) {
        Matrix frame = WorldFxHostAnchorFrame(definition, up, boneName);
        position = new Vector3(frame.M41, frame.M42, frame.M43);
        return true;
      }
      WorldFxNodeDefinition target = definition?.Nodes?.FirstOrDefault(x => x != null && String.Equals(x.Name, up, StringComparison.OrdinalIgnoreCase));
      if (target == null) return false;
      // Transformer endpoints are resolved from the other element's BASE placement, not its live transformer overlay;
      // otherwise mutually-driven rubble/door specs feed back into themselves and run away to infinity.
      position = target.Offset;
      return true;
    }

    private static void WorldFxNodeTransformerOnlyTransform(WorldFxSpecDefinition definition, WorldFxNodeDefinition node, float clock,
        out Vector3 offset, out Vector3 rotation, out Vector3 scale) {
      offset = node?.Offset ?? Vector3.Zero; rotation = node?.Rotation ?? Vector3.Zero; scale = node?.Scale ?? new Vector3(1f, 1f, 1f);
      if (definition?.Nodes == null || node == null || String.IsNullOrWhiteSpace(node.Name)) return;
      foreach (WorldFxNodeDefinition transformer in definition.Nodes) {
        if (transformer == null || !transformer.IsTransformer || !String.Equals(FxString(transformer.Fields, "_fxAssetName"), node.Name, StringComparison.OrdinalIgnoreCase)) continue;
        if (clock < transformer.StartAt) continue;
        float sampleClock = !Single.IsInfinity(transformer.StopAt) ? Math.Min(clock, transformer.StopAt) : clock;
        float elapsed = Math.Max(0f, sampleClock - transformer.StartAt);
        Dictionary<string, string> f = transformer.Fields;
        if (!String.Equals(FxString(f, "_trIgnorePosition", "false"), "true", StringComparison.OrdinalIgnoreCase)) {
          Vector3 a, b;
          bool useOffset = String.Equals(FxString(f, "_trUseOffsetPosition", "false"), "true", StringComparison.OrdinalIgnoreCase);
          if (useOffset) {
            a = Vector3.Zero; b = ParseWorldFxVector(FxString(f, "_trOffsetPosition"), Vector3.Zero);
          } else {
            bool attached = !String.IsNullOrWhiteSpace(node.AttachTo);
            Vector3 authoredBase = ParseWorldFxVector(FxString(node.Fields, attached ? "_fxAttachPosition" : "_fxStartLocOffset"), Vector3.Zero);
            if (TryWorldFxResolveBaseLocation(definition, FxString(f, "_fxStartLoc"), FxString(f, "_fxStartLocBone"), out Vector3 startLoc))
              a = InverseRotateWorldFxVector(startLoc - node.Offset, node.Rotation);
            else a = ParseWorldFxVector(FxString(f, "_trStartPosition"), Vector3.Zero) - authoredBase;
            if (TryWorldFxResolveBaseLocation(definition, FxString(f, "_fxTargetLoc"), FxString(f, "_fxTargetLocBone"), out Vector3 targetLoc))
              b = InverseRotateWorldFxVector(targetLoc - node.Offset, node.Rotation);
            else b = ParseWorldFxVector(FxString(f, "_trTargetPosition"), Vector3.Zero) - authoredBase;
          }
          Vector3 travel = new Vector3(
            a.X + (b.X - a.X) * WorldFxTransformerU(f, elapsed, "Position", "X"),
            a.Y + (b.Y - a.Y) * WorldFxTransformerU(f, elapsed, "Position", "Y"),
            a.Z + (b.Z - a.Z) * WorldFxTransformerU(f, elapsed, "Position", "Z"));
          offset += RotateWorldFxVector(travel, node.Rotation);
        }
        if (!String.Equals(FxString(f, "_trIgnoreRotation", "false"), "true", StringComparison.OrdinalIgnoreCase)) {
          bool useOffset = String.Equals(FxString(f, "_trUseOffsetRotation", "false"), "true", StringComparison.OrdinalIgnoreCase);
          Vector3 a = useOffset ? Vector3.Zero : ParseWorldFxVector(FxString(f, "_trStartRotation"), Vector3.Zero);
          Vector3 b = useOffset ? ParseWorldFxVector(FxString(f, "_trOffsetRotation"), Vector3.Zero)
            : ParseWorldFxVector(FxString(f, "_trTargetRotation"), Vector3.Zero);
          rotation += new Vector3(
            a.X + (b.X - a.X) * WorldFxTransformerU(f, elapsed, "Rotation", "X"),
            a.Y + (b.Y - a.Y) * WorldFxTransformerU(f, elapsed, "Rotation", "Y"),
            a.Z + (b.Z - a.Z) * WorldFxTransformerU(f, elapsed, "Rotation", "Z"));
        }
        if (!String.Equals(FxString(f, "_trIgnoreScale", "false"), "true", StringComparison.OrdinalIgnoreCase)) {
          Vector3 a = ParseWorldFxVector(FxString(f, "_trStartScale"), new Vector3(1f, 1f, 1f));
          Vector3 b = String.Equals(FxString(f, "_trUseOffsetScale", "false"), "true", StringComparison.OrdinalIgnoreCase)
            ? a + ParseWorldFxVector(FxString(f, "_trOffsetScale"), Vector3.Zero)
            : ParseWorldFxVector(FxString(f, "_trTargetScale"), new Vector3(1f, 1f, 1f));
          Vector3 animated = new Vector3(
            a.X + (b.X - a.X) * WorldFxTransformerU(f, elapsed, "Scale", "X"),
            a.Y + (b.Y - a.Y) * WorldFxTransformerU(f, elapsed, "Scale", "Y"),
            a.Z + (b.Z - a.Z) * WorldFxTransformerU(f, elapsed, "Scale", "Z"));
          scale = new Vector3(scale.X * animated.X, scale.Y * animated.Y, scale.Z * animated.Z);
        }
      }
    }

    private static void WorldFxResolveOperatorAnchor(WorldFxSpecDefinition definition, WorldFxNodeDefinition source, string name, float clock,
        out Vector3 position, out Vector3 rotation, out Vector3 scale, string boneName = null,
        HashSet<WorldFxNodeDefinition> resolving = null, bool liveNamed = false) {
      position = Vector3.Zero; rotation = Vector3.Zero; scale = new Vector3(1f, 1f, 1f);
      string up = (name ?? String.Empty).Trim();
      if (up.Length == 0 || String.Equals(up, "DEFAULT", StringComparison.OrdinalIgnoreCase)) {
        WorldFxNodeTransformerOnlyTransform(definition, source, clock, out position, out rotation, out scale); return;
      }
      if (WorldFxActorAnchor(up)) {
        Matrix frame = WorldFxHostAnchorFrame(definition, up, boneName);
        WorldFxMatrixParts(frame, out position, out rotation, out scale);
        return;
      }
      if (String.Equals(up, "NOTHING", StringComparison.OrdinalIgnoreCase)) return;
      WorldFxNodeDefinition target = definition?.Nodes?.FirstOrDefault(x => x != null && String.Equals(x.Name, up, StringComparison.OrdinalIgnoreCase));
      if (target != null) {
        if (liveNamed && (resolving == null || !resolving.Contains(target)))
          WorldFxNodeDynamicTransformCore(definition, target, clock, resolving, out position, out rotation, out scale);
        else WorldFxNodeTransformerOnlyTransform(definition, target, clock, out position, out rotation, out scale);
      }
    }

    private static Vector3 WorldFxResolveArcLocation(WorldFxSpecDefinition definition, WorldFxNodeDefinition arc, string location, string offsetText, float clock,
        string boneName = null, HashSet<WorldFxNodeDefinition> resolving = null) {
      WorldFxResolveOperatorAnchor(definition, arc, location, clock, out Vector3 position, out Vector3 rotation, out Vector3 scale, boneName, resolving, true);
      Vector3 offset = ParseWorldFxVector(offsetText, Vector3.Zero);
      if (WorldFxActorAnchor(location) && !String.IsNullOrWhiteSpace(boneName)) {
        offset = new Vector3(offset.X * scale.X, offset.Y * scale.Y, offset.Z * scale.Z);
        offset = RotateWorldFxVector(offset, rotation);
      }
      return position + offset;
    }

    private static bool WorldFxArcMovesNode(WorldFxSpecDefinition definition, WorldFxNodeDefinition arc, WorldFxNodeDefinition node) {
      string asset = FxString(arc.Fields, "_fxAssetName");
      if (!String.IsNullOrWhiteSpace(asset) && WorldFxNodeRidesAsset(definition, node, asset)) return true;
      string owner = FxString(arc.Fields, "_fxOwnerName");
      if (!String.IsNullOrWhiteSpace(owner) && !WorldFxExternalAnchor(owner) &&
          String.Equals(node.OwnerName, owner, StringComparison.OrdinalIgnoreCase) && String.IsNullOrWhiteSpace(node.AttachTo)) return true;
      return false;
    }

    private static bool WorldFxNodeRidesAsset(WorldFxSpecDefinition definition, WorldFxNodeDefinition node, string assetName) {
      WorldFxNodeDefinition current = node;
      for (int guard = 0; current != null && guard < 16; guard++) {
        if (String.Equals(current.Name, assetName, StringComparison.OrdinalIgnoreCase)) return true;
        string parentName = current.AttachTo;
        if (String.IsNullOrWhiteSpace(parentName) || WorldFxExternalAnchor(parentName)) break;
        current = definition?.Nodes?.FirstOrDefault(x => x != null && String.Equals(x.Name, parentName, StringComparison.OrdinalIgnoreCase));
      }
      return false;
    }

    // Local +Y is the projectile forward axis for Fx/GRANNY objects.
    private static Vector3 WorldFxArrowEulerDegrees(Vector3 direction) {
      if (direction.LengthSquared() <= 1e-8f) return Vector3.Zero;
      direction.Normalize();
      float z = (float)(Math.Atan2(-direction.X, direction.Y) * 180.0 / Math.PI);
      float xy = (float)Math.Sqrt(direction.X * direction.X + direction.Y * direction.Y);
      float x = (float)(Math.Atan2(direction.Z, Math.Max(1e-8f, xy)) * 180.0 / Math.PI);
      return new Vector3(x, 0f, z);
    }

    private static float WorldFxTransformerU(Dictionary<string, string> fields, float elapsed, string channel, string axis) {
      float duration = Math.Max(.001f, ParseWorldFxTime(FxString(fields, "_trDuration")));
      float repeat = 1f; if (!Single.TryParse(FxString(fields, "_trRepeatCount", "1"), NumberStyles.Float, CultureInfo.InvariantCulture, out repeat) || repeat <= 0f) repeat = Single.PositiveInfinity;
      float phase = elapsed / duration; int cycle = (int)Math.Floor(phase); float u = phase - cycle;
      if (!Single.IsInfinity(repeat) && phase >= repeat) { cycle = Math.Max(0, (int)Math.Ceiling(repeat) - 1); u = 1f; }
      bool reversed = String.Equals(FxString(fields, "_trReverse", "false"), "true", StringComparison.OrdinalIgnoreCase) && (cycle & 1) != 0;
      if (reversed) {
        bool one = !String.Equals(FxString(fields, "_trUseOneReverseCurve", "true"), "false", StringComparison.OrdinalIgnoreCase);
        string curve = one ? FxString(fields, "_trReverseCurveType", FxString(fields, "_trCurveType", "LINEAR"))
          : FxString(fields, "_trReverseCurve" + channel + "_" + axis, FxString(fields, "_trReverseCurveType", "LINEAR"));
        return 1f - WorldFxFadeCurve(curve, u);
      } else {
        bool one = !String.Equals(FxString(fields, "_trUseOneCurveType", "true"), "false", StringComparison.OrdinalIgnoreCase);
        string curve = one ? FxString(fields, "_trCurveType", "LINEAR")
          : FxString(fields, "_trCurve" + channel + "_" + axis, FxString(fields, "_trCurveType", "LINEAR"));
        return WorldFxFadeCurve(curve, u);
      }
    }

    private static float WorldFxModelFadeAlpha(WorldFxNodeDefinition node, float clock) {
      if (node == null) return 0f;
      float alpha = 1f;
      if (node.ModelFadeIn && node.ModelFadeInTime > .000001f) {
        float age = clock - node.StartAt;
        if (age < node.ModelFadeInTime) alpha *= WorldFxFadeCurve(FxString(node.Fields, "_fxFadeInCurve", "LINEAR"), Math.Max(0f, age) / node.ModelFadeInTime);
      }
      if (!Single.IsInfinity(node.StopAt) && clock >= node.StopAt) {
        if (!node.ModelFadeOut || node.ModelFadeOutTime <= .000001f) return 0f;
        float age = clock - node.StopAt; if (age >= node.ModelFadeOutTime) return 0f;
        alpha *= 1f - WorldFxFadeCurve(FxString(node.Fields, "_fxFadeOutCurve", "LINEAR"), Math.Max(0f, age) / node.ModelFadeOutTime);
      }
      return Math.Max(0f, Math.Min(1f, alpha));
    }

    private static float WorldFxModelFaderAlpha(WorldFxSpecDefinition definition, WorldFxNodeDefinition model, float clock) {
      if (definition?.Nodes == null || model == null || String.IsNullOrWhiteSpace(model.Name)) return 1f;
      float result = 1f;
      foreach (WorldFxNodeDefinition fader in definition.Nodes) {
        if (fader == null || !fader.IsFader || !String.Equals(FxString(fader.Fields, "_fxAssetName"), model.Name, StringComparison.OrdinalIgnoreCase)) continue;
        float at = Math.Max(fader.StartAt, 0f); if (clock < at) continue;
        float sampleClock = !Single.IsInfinity(fader.StopAt) ? Math.Min(clock, fader.StopAt) : clock;
        float delay = ParseWorldFxTime(FxString(fader.Fields, "_mfDelay")); float elapsed = sampleClock - at - delay; if (elapsed < 0f) continue;
        float duration = Math.Max(.001f, ParseWorldFxTime(FxString(fader.Fields, "_mfDuration")));
        float repeat = 1f; Single.TryParse(FxString(fader.Fields, "_mfRepeatCount", "1"), NumberStyles.Float, CultureInfo.InvariantCulture, out repeat); if (!(repeat > 0f)) repeat = Single.PositiveInfinity;
        float phase = elapsed / duration; int cycle = (int)Math.Floor(phase); float u = phase - cycle;
        if (!Single.IsInfinity(repeat) && phase >= repeat) { cycle = Math.Max(0, (int)Math.Ceiling(repeat) - 1); u = 1f; }
        bool reverse = String.Equals(FxString(fader.Fields, "_mfReverse"), "true", StringComparison.OrdinalIgnoreCase) && (cycle & 1) != 0;
        if (reverse) u = 1f - WorldFxFadeCurve(FxString(fader.Fields, "_mfReverseCurve", FxString(fader.Fields, "_mfCurveType", "LINEAR")), u);
        else u = WorldFxFadeCurve(FxString(fader.Fields, "_mfCurveType", "LINEAR"), u);
        float a = 1f, b = 1f; Single.TryParse(FxString(fader.Fields, "_mfStart", "1"), NumberStyles.Float, CultureInfo.InvariantCulture, out a); Single.TryParse(FxString(fader.Fields, "_mfTarget", "1"), NumberStyles.Float, CultureInfo.InvariantCulture, out b);
        result *= Math.Max(0f, Math.Min(1f, a + (b - a) * u));
      }
      return result;
    }

    private static Vector4 WorldFxModelFaderColor(WorldFxSpecDefinition definition, WorldFxNodeDefinition model, float clock) {
      if (definition?.Nodes == null || model == null || String.IsNullOrWhiteSpace(model.Name)) return new Vector4(1f, 1f, 1f, 1f);
      Vector4 result = new Vector4(1f, 1f, 1f, 1f);
      foreach (WorldFxNodeDefinition fader in definition.Nodes) {
        if (fader == null || !fader.IsFader || !String.Equals(FxString(fader.Fields, "_fxAssetName"), model.Name, StringComparison.OrdinalIgnoreCase)) continue;
        float at = Math.Max(fader.StartAt, 0f); if (clock < at) continue;
        float sampleClock = !Single.IsInfinity(fader.StopAt) ? Math.Min(clock, fader.StopAt) : clock;
        float delay = ParseWorldFxTime(FxString(fader.Fields, "_mfDelay")); float elapsed = sampleClock - at - delay; if (elapsed < 0f) continue;
        float duration = Math.Max(.001f, ParseWorldFxTime(FxString(fader.Fields, "_mfDuration")));
        float repeat = 1f; Single.TryParse(FxString(fader.Fields, "_mfRepeatCount", "1"), NumberStyles.Float, CultureInfo.InvariantCulture, out repeat); if (!(repeat > 0f)) repeat = Single.PositiveInfinity;
        float phase = elapsed / duration; int cycle = (int)Math.Floor(phase); float u = phase - cycle;
        if (!Single.IsInfinity(repeat) && phase >= repeat) { cycle = Math.Max(0, (int)Math.Ceiling(repeat) - 1); u = 1f; }
        bool reverse = String.Equals(FxString(fader.Fields, "_mfReverse"), "true", StringComparison.OrdinalIgnoreCase) && (cycle & 1) != 0;
        if (reverse) u = 1f - WorldFxFadeCurve(FxString(fader.Fields, "_mfReverseCurve", FxString(fader.Fields, "_mfCurveType", "LINEAR")), u);
        else u = WorldFxFadeCurve(FxString(fader.Fields, "_mfCurveType", "LINEAR"), u);
        Vector4 c0 = ParseWorldFxColor(FxString(fader.Fields, "_mfStartDiffuseColor"), Vector4.Zero);
        Vector4 c1 = ParseWorldFxColor(FxString(fader.Fields, "_mfLastDiffuseColor"), Vector4.Zero);
        if (c0.X > 0f || c0.Y > 0f || c0.Z > 0f || c0.W > 0f || c1.X > 0f || c1.Y > 0f || c1.Z > 0f || c1.W > 0f)
          result = WorldFxLerp(c0, c1, u); // FxPlayer stores one live fadeColor on the target; later active faders replace it.
      }
      return result;
    }

    private static float WorldFxFadeCurve(string curve, float value) {
      float t = Math.Max(0f, Math.Min(1f, value));
      switch ((curve ?? String.Empty).Trim().ToUpperInvariant()) {
        case "HOLD": return 0f;
        case "EASE_IN": return 1f - (float)Math.Pow(1f - t, 3);
        case "EASE_OUT": return t * t * t;
        case "SMOOTH": return t * t * (3f - 2f * t);
        case "TWIST": return t * (3f - t * (6f - 4f * t));
        case "SQUARE": return t * (2f - t);
        case "INV_SQUARE": return t * t;
        default: return t;
      }
    }

    // Build an orthonormal row-vector frame where local +Y follows velocity, matching Jedipedia's GRANNY particle
    // convention for projectile/bolt meshes. The fallback helper avoids degeneracy when the direction is near +Y/-Y.
    private static Matrix WorldFxAlignYTo(Vector3 direction) {
      if (direction.LengthSquared() <= 1e-8f) return Matrix.Identity;
      Vector3 y = direction; y.Normalize();
      Vector3 helper = Math.Abs(y.Y) > .99f ? Vector3.UnitX : Vector3.UnitY;
      Vector3 x = Vector3.Cross(helper, y); if (x.LengthSquared() <= 1e-8f) x = Vector3.UnitX; else x.Normalize();
      Vector3 z = Vector3.Cross(x, y); if (z.LengthSquared() <= 1e-8f) z = Vector3.UnitZ; else z.Normalize();
      Matrix m = Matrix.Identity;
      m.M11 = x.X; m.M12 = x.Y; m.M13 = x.Z;
      m.M21 = y.X; m.M22 = y.Y; m.M23 = y.Z;
      m.M31 = z.X; m.M32 = z.Y; m.M33 = z.Z;
      return m;
    }

    private GR2 LoadWorldFxModel(string requestedPath) {
      if (String.IsNullOrWhiteSpace(requestedPath) || area == null) return null;
      string key = requestedPath.Trim().Trim('"').Replace('\\', '/').ToLowerInvariant();
      if (worldFxModelCache.TryGetValue(key, out GR2 cached)) return cached;
      if (worldFxMissingModels.Contains(key)) return null;
      foreach (string candidate in WorldOverheadResourceCandidates(requestedPath, ".gr2")) {
        try {
          using TorArchive.File file = area.FindFile(candidate); if (file == null) continue;
          using Stream stream = file.OpenCopyInMemory(); using var br = new BinaryReader(stream);
          var model = new GR2(br, candidate, materials) { transformMatrix = Matrix.Identity };
          worldFxModelCache[key] = model; return model;
        } catch (Exception ex) { System.Diagnostics.Debug.WriteLine("World FXSPEC GR2 load failed " + candidate + ": " + ex.Message); }
      }
      worldFxMissingModels.Add(key); return null;
    }

    private bool DrawWorldFxModel(WorldFxRenderModel instance, Matrix viewProj, WorldRenderSettings settings, bool sky) {
      if (instance?.Model == null || fx == null || ImmediateContext == null || settings == null || instance.Tint.W <= .0005f) return false;
      bool any = false;
      DrawWorldFxModelRecursive(instance.Model, instance.World, viewProj, settings, sky, instance.Tint, ref any);
      return any;
    }

    private void DrawWorldFxModelRecursive(GR2 model, Matrix world, Matrix viewProj, WorldRenderSettings settings, bool sky, Vector4 tint, ref bool any) {
      if (model == null || !EnsureModelGeometryPrepared(model)) return;
      int selectedLod = SelectModelLodLevel(model, world, sky || settings.Mode == WorldRenderMode.Map);
      fx.SetWorld(world); fx.SetViewProj(viewProj); fx.SetPlaceableBlueGlow(false);
      foreach (var mesh in model.meshes) {
        if (mesh.vertBuffer == null || mesh.idxBuffer == null || !MeshVisibleForLod(model, mesh, selectedLod)) continue;
        ImmediateContext.InputAssembler.SetVertexBuffers(0, new VertexBufferBinding(mesh.vertBuffer, PosNormalTexTan.Stride, 0));
        ImmediateContext.InputAssembler.SetIndexBuffer(mesh.idxBuffer, SlimDX.DXGI.Format.R16_UInt, 0);
        foreach (var piece in mesh.meshPieces) {
          GR2_Material mat = ResolvePieceMaterial(model, piece); if (IsMaterialHiddenFromWorld(mat, settings)) continue;
          fx.SetMaterial(mat);
          Vector4 baseColor = mat?.diffuseFlatColor ?? new Vector4(1f, 1f, 1f, 1f);
          fx.SetMaterialFlatColor(new Vector4(baseColor.X * tint.X, baseColor.Y * tint.Y, baseColor.Z * tint.Z, baseColor.W * tint.W));
          // FXSPEC GR2s use the normal VFX/material passes even when their host lives in a skyscene. The special
          // Skydome technique intentionally discards tint/alpha and is only correct for authored room skydomes.
          var tech = PickModelTech(settings, mat, false);
          if (tint.W < .999f && tech != fx.AddLit && tech != fx.MultiplyLit) tech = fx.AlphaLit;
          tech.GetPassByIndex(0).Apply(ImmediateContext);
          ImmediateContext.DrawIndexed((int)piece.numPieceFaces * 3, (int)piece.startIndex * 3, 0); any = true;
        }
      }
      foreach (GR2 attached in model.attachedModels) DrawWorldFxModelRecursive(attached, world, viewProj, settings, sky, tint, ref any);
    }

    private SlimDX.Direct3D11.Buffer EnsureWorldFxRibbonBuffer() {
      if (worldFxRibbonBuffer != null || Device == null) return worldFxRibbonBuffer;
      try {
        var bd = new BufferDescription(PosNormalTexTan.Stride * 6, ResourceUsage.Dynamic, BindFlags.VertexBuffer,
          CpuAccessFlags.Write, ResourceOptionFlags.None, 0);
        worldFxRibbonBuffer = new SlimDX.Direct3D11.Buffer(Device, bd);
      } catch (Exception ex) { System.Diagnostics.Debug.WriteLine("World FXSPEC ribbon buffer create failed: " + ex.Message); }
      return worldFxRibbonBuffer;
    }

    private bool DrawWorldFxProjectorDecal(WorldFxProjectorDecal decal, Matrix fxFrame, Matrix viewProj) {
      if (decal == null || fx == null || ImmediateContext == null || sceneDepthShaderResource == null || sceneDepthReadOnlyView == null || worldActiveRenderTarget == null) return false;
      MapNoteIconGpu gpu = EnsureWorldOverheadTextureGpu(decal.TexturePath, 6);
      if (gpu?.Texture == null) return false;
      if (worldFxProjectorBudgetFrame != worldRenderFrame) {
        worldFxProjectorBudgetFrame = worldRenderFrame;
        worldFxProjectorBudgetRemaining = WorldFxMaxProjectorDecalsPerFrame;
      }
      if (worldFxProjectorBudgetRemaining <= 0) return false;
      worldFxProjectorBudgetRemaining--;
      Matrix world = decal.LocalWorld * fxFrame;
      Matrix fromWorld, invViewProj;
      try { fromWorld = Matrix.Invert(world); invViewProj = Matrix.Invert(viewProj); }
      catch { return false; }
      EffectPass pass = null;
      try {
        // Swap only the DSV view, not the resource: depth becomes read-only and can legally be sampled as an SRV
        // while the active scene/backbuffer RTV remains bound for blending.
        ImmediateContext.OutputMerger.SetTargets(sceneDepthReadOnlyView, worldActiveRenderTarget);
        ImmediateContext.InputAssembler.InputLayout = null;
        ImmediateContext.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
        fx.SetFxProjectorDecal(sceneDepthShaderResource, gpu.Texture, invViewProj, fromWorld, decal.Color, decal.Uv, decal.Falloff,
          decal.Hue0, decal.Hue1, decal.Hue2, decal.Hue3, decal.Shape, decal.TwoSided, true, decal.HueOn);
        pass = (decal.Multiply ? fx.FxProjectorDecalMultiply : decal.Additive ? fx.FxProjectorDecalAdditive : fx.FxProjectorDecalAlpha).GetPassByIndex(0);
        pass.Apply(ImmediateContext);
        ImmediateContext.Draw(3, 0);
        return true;
      } catch (Exception ex) {
        System.Diagnostics.Debug.WriteLine("World FXSPEC projector decal draw failed: " + ex.Message);
        return false;
      } finally {
        try {
          fx.ClearFxProjectorDecal();
          // Push the null SRV through the effect before rebinding the writable DSV, otherwise the D3D11 debug layer
          // quite correctly reports the depth resource as simultaneously bound for read and write.
          pass?.Apply(ImmediateContext);
        } catch { }
        try { ImmediateContext.OutputMerger.SetTargets(DepthStencilView, worldActiveRenderTarget); } catch { }
        try { ImmediateContext.InputAssembler.InputLayout = inputLayout; ImmediateContext.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList; } catch { }
      }
    }

    private bool DrawWorldFxRibbonSegment(WorldFxRenderRibbonSegment segment, Matrix fxFrame, Matrix viewProj) {
      if (segment == null || camera == null || fx == null || ImmediateContext == null) return false;
      MapNoteIconGpu gpu = EnsureWorldOverheadTextureGpu(segment.TexturePath, 6);
      SlimDX.Direct3D11.Buffer buffer = EnsureWorldFxRibbonBuffer();
      if (gpu?.Texture == null || buffer == null) return false;
      Vector3 a0 = Vector3.TransformCoordinate(segment.A0, fxFrame), b0 = Vector3.TransformCoordinate(segment.B0, fxFrame);
      Vector3 a1 = Vector3.TransformCoordinate(segment.A1, fxFrame), b1 = Vector3.TransformCoordinate(segment.B1, fxFrame);
      Vector3 normal = -camera.Look; if (normal.LengthSquared() <= 1e-8f) normal = Vector3.UnitZ; else normal.Normalize();
      Vector3 tangent = a1 - a0; if (tangent.LengthSquared() <= 1e-8f) tangent = camera.Right; else tangent.Normalize();
      var vertices = new[] {
        new PosNormalTexTan(a0, normal, new Vector2(segment.U0,segment.V0), tangent),
        new PosNormalTexTan(b0, normal, new Vector2(segment.U0,segment.V1), tangent),
        new PosNormalTexTan(b1, normal, new Vector2(segment.U1,segment.V1), tangent),
        new PosNormalTexTan(a0, normal, new Vector2(segment.U0,segment.V0), tangent),
        new PosNormalTexTan(b1, normal, new Vector2(segment.U1,segment.V1), tangent),
        new PosNormalTexTan(a1, normal, new Vector2(segment.U1,segment.V0), tangent)
      };
      try {
        DataBox mapped = ImmediateContext.MapSubresource(buffer, MapMode.WriteDiscard, SlimDX.Direct3D11.MapFlags.None);
        mapped.Data.WriteRange(vertices); ImmediateContext.UnmapSubresource(buffer, 0);
        ImmediateContext.InputAssembler.InputLayout = inputLayout; ImmediateContext.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
        ImmediateContext.InputAssembler.SetVertexBuffers(0, new VertexBufferBinding(buffer, PosNormalTexTan.Stride, 0));
        fx.SetWorld(Matrix.Identity); fx.SetViewProj(viewProj); fx.SetPlaceableBlueGlow(false); fx.SetMapArt(gpu.Texture, 1f, segment.Color);
        (segment.Multiply ? fx.FxRibbonMultiply : segment.Additive ? fx.FxRibbonAdditive : fx.FxRibbonAlpha).GetPassByIndex(0).Apply(ImmediateContext);
        ImmediateContext.Draw(6, 0); fx.ClearMapArt(); return true;
      } catch (Exception ex) { System.Diagnostics.Debug.WriteLine("World FXSPEC ribbon draw failed: " + ex.Message); return false; }
    }

    private bool DrawWorldFxGlow(WorldFxRenderGlow glow, Matrix fxFrame, Matrix viewProj) {
      if (glow == null || !(glow.Radius > .0001f) || glow.Color.W <= .0001f || camera == null || fx == null || ImmediateContext == null) return false;
      SlimDX.Direct3D11.Buffer buffer = EnsureWorldFxRibbonBuffer(); if (buffer == null) return false;
      Vector3 center = Vector3.TransformCoordinate(glow.Position, fxFrame);
      float sx = (float)Math.Sqrt(fxFrame.M11 * fxFrame.M11 + fxFrame.M12 * fxFrame.M12 + fxFrame.M13 * fxFrame.M13);
      float sy = (float)Math.Sqrt(fxFrame.M21 * fxFrame.M21 + fxFrame.M22 * fxFrame.M22 + fxFrame.M23 * fxFrame.M23);
      float sz = (float)Math.Sqrt(fxFrame.M31 * fxFrame.M31 + fxFrame.M32 * fxFrame.M32 + fxFrame.M33 * fxFrame.M33);
      float hostScale = Math.Max(.0001f, (sx + sy + sz) / 3f);
      Vector3 right = camera.Right * (glow.Radius * hostScale), up = camera.Up * (glow.Radius * hostScale);
      Vector3 tl = center - right + up, tr = center + right + up, br = center + right - up, bl = center - right - up;
      Vector3 normal = -camera.Look, tangent = camera.Right;
      var vertices = new[] {
        new PosNormalTexTan(tl, normal, new Vector2(0f,0f), tangent), new PosNormalTexTan(tr, normal, new Vector2(1f,0f), tangent),
        new PosNormalTexTan(br, normal, new Vector2(1f,1f), tangent), new PosNormalTexTan(tl, normal, new Vector2(0f,0f), tangent),
        new PosNormalTexTan(br, normal, new Vector2(1f,1f), tangent), new PosNormalTexTan(bl, normal, new Vector2(0f,1f), tangent)
      };
      try {
        DataBox mapped = ImmediateContext.MapSubresource(buffer, MapMode.WriteDiscard, SlimDX.Direct3D11.MapFlags.None);
        mapped.Data.WriteRange(vertices); ImmediateContext.UnmapSubresource(buffer, 0);
        ImmediateContext.InputAssembler.InputLayout = inputLayout; ImmediateContext.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
        ImmediateContext.InputAssembler.SetVertexBuffers(0, new VertexBufferBinding(buffer, PosNormalTexTan.Stride, 0));
        fx.SetWorld(Matrix.Identity); fx.SetViewProj(viewProj); fx.SetPlaceableBlueGlow(false); fx.SetMapArt(null, 1f, glow.Color);
        fx.FxGlow.GetPassByIndex(0).Apply(ImmediateContext); ImmediateContext.Draw(6, 0); fx.ClearMapArt(); return true;
      } catch (Exception ex) { System.Diagnostics.Debug.WriteLine("World FXSPEC light glow draw failed: " + ex.Message); return false; }
    }

    private bool DrawWorldFxParticle(WorldFxRenderParticle particle, Matrix fxFrame, float screenYOffset, Matrix viewProj, Vector3? cameraPositionOverride = null) {
      if (particle == null || particle.HalfWidth <= 0f || particle.HalfHeight <= 0f || camera == null) return false;
      MapNoteIconGpu gpu = EnsureWorldOverheadTextureGpu(particle.TexturePath, 6); if (gpu?.Texture == null || gpu.Buffer == null) return false;
      // Particle simulation stays in FXSPEC-local space. Apply the complete attachment transform only here so an
      // authored local -Z offset follows NamePlate bone orientation instead of becoming an arbitrary world-Z shift.
      Vector3 center = Vector3.TransformCoordinate(particle.Position, fxFrame);
      Vector3 drawCameraPosition = cameraPositionOverride ?? camera.Position;

      // FxPlayer multiplies an element attached to a bone by that bone frame's scale. PugTools previously transformed
      // the particle POSITION through fxFrame but left the billboard extent unscaled, so template/body scale changes
      // moved an icon correctly while leaving it visibly too large or too small. Row lengths are the local axes in
      // SlimDX's row-vector convention.
      float attachScaleX = (float)Math.Sqrt(fxFrame.M11 * fxFrame.M11 + fxFrame.M12 * fxFrame.M12 + fxFrame.M13 * fxFrame.M13);
      float attachScaleY = (float)Math.Sqrt(fxFrame.M21 * fxFrame.M21 + fxFrame.M22 * fxFrame.M22 + fxFrame.M23 * fxFrame.M23);
      if (!(attachScaleX > .000001f) || Single.IsNaN(attachScaleX) || Single.IsInfinity(attachScaleX)) attachScaleX = 1f;
      if (!(attachScaleY > .000001f) || Single.IsNaN(attachScaleY) || Single.IsInfinity(attachScaleY)) attachScaleY = 1f;

      float distanceScale = WorldPrtCameraDistanceScale(particle, (center - drawCameraPosition).Length());
      if (!(distanceScale > .000001f)) return false;
      float halfWidth = particle.HalfWidth * attachScaleX * distanceScale;
      float halfHeight = particle.HalfHeight * attachScaleY * distanceScale;
      if (!(halfWidth > .000001f) || !(halfHeight > .000001f)) return false;

      if (Math.Abs(screenYOffset) > .001f) {
        Vector3 forward = -camera.Look; if (forward.LengthSquared() > .000001f) { forward.Normalize(); float depth = Vector3.Dot(center - drawCameraPosition, forward);
          if (depth > .001f) { float worldPerPixel = (float)(2.0 * depth * Math.Tan(camera.FovY * .5f) / Math.Max(1f, Viewport.Height)); center += camera.Up * (-screenYOffset * worldPerPixel); }
        }
      }
      float inPlaneRotation = particle.Rotation;
      if (particle.AlignToTrajectory && particle.TrajectoryVelocity.LengthSquared() > 1e-8f) {
        // The client overwrites iRot with the SCREEN-space direction of the projected velocity. Projecting a distant
        // point reproduces that rule without changing the billboard basis (OrientationAxis still owns the basis).
        Vector3 worldVelocity = Vector3.TransformNormal(particle.TrajectoryVelocity, fxFrame);
        if (worldVelocity.LengthSquared() > 1e-10f) {
          worldVelocity.Normalize();
          Vector3 p0 = Vector3.Project(center, 0f, 0f, Math.Max(1f, Viewport.Width), Math.Max(1f, Viewport.Height), 0f, 1f, viewProj);
          Vector3 p1 = Vector3.Project(center + worldVelocity * 8192f, 0f, 0f, Math.Max(1f, Viewport.Width), Math.Max(1f, Viewport.Height), 0f, 1f, viewProj);
          float dx = p1.X - p0.X, dyNdcSign = -(p1.Y - p0.Y); // viewport Y is opposite NDC Y
          if (Single.IsFinite(dx) && Single.IsFinite(dyNdcSign) && dx * dx + dyNdcSign * dyNdcSign > .001f)
            inPlaneRotation = (float)Math.Atan2(dx, dyNdcSign);
        }
      }
      float c = (float)Math.Cos(inPlaneRotation), s = (float)Math.Sin(inPlaneRotation);
      // Match SWTOR/Jedipedia's OrientationAxis semantics. Only XYZ is a free camera-facing billboard. NONE stays
      // on the authored XY plane; X/Y/Z may turn around that one world/effect axis. This is especially important for
      // large forcefield sheets and holographic panels, which otherwise rotate with the viewer.
      Vector3 baseRight, baseUp;
      string orient = String.IsNullOrWhiteSpace(particle.OrientationAxis) ? "XYZ" : particle.OrientationAxis;
      if (orient == "XYZ") {
        baseRight = camera.Right; baseUp = camera.Up;
      } else {
        baseRight = Vector3.UnitX; baseUp = particle.LieFlat ? -Vector3.UnitZ : Vector3.UnitY;
        if (orient == "Y") {
          Vector3 d = drawCameraPosition - center; float a = (float)Math.Atan2(-d.Z, d.X) + (float)Math.PI * .5f;
          baseRight = RotateWorldFxVectorRadians(baseRight, new Vector3(0f, a, 0f));
          baseUp = RotateWorldFxVectorRadians(baseUp, new Vector3(0f, a, 0f));
        } else if (orient == "X") {
          float pitch = (float)Math.Asin(Math.Max(-1f, Math.Min(1f, camera.Look.Y)));
          baseRight = RotateWorldFxVectorRadians(baseRight, new Vector3(-pitch, 0f, 0f));
          baseUp = RotateWorldFxVectorRadians(baseUp, new Vector3(-pitch, 0f, 0f));
        } else if (orient == "Z") {
          float roll = (float)Math.Atan2(camera.Right.Y, camera.Up.Y);
          baseRight = RotateWorldFxVectorRadians(baseRight, new Vector3(0f, 0f, roll));
          baseUp = RotateWorldFxVectorRadians(baseUp, new Vector3(0f, 0f, roll));
        }
        baseRight = RotateWorldFxVector(baseRight, particle.NodeRotation);
        baseUp = RotateWorldFxVector(baseUp, particle.NodeRotation);
        baseRight = Vector3.TransformNormal(baseRight, fxFrame);
        baseUp = Vector3.TransformNormal(baseUp, fxFrame);
        if (baseRight.LengthSquared() > 1e-8f) baseRight.Normalize();
        if (baseUp.LengthSquared() > 1e-8f) baseUp.Normalize();
      }
      // SWTOR rotates the already-scaled half-extents about the negated quad normal.
      Vector3 rightAxis = baseRight * c - baseUp * s;
      Vector3 upAxis = baseRight * s + baseUp * c;

      // Jedipedia's shared PRT shader computes `corner * 2 - pivot` BEFORE the in-plane rotation. Expressing that as
      // a shifted center plus the same symmetric extents is algebraically identical and keeps the existing six-vertex
      // path. This is the small but visible offset the overhead icons were still missing.
      center += rightAxis * (-particle.PivotU * halfWidth) + upAxis * (-particle.PivotV * halfHeight);
      Vector3 right = rightAxis * halfWidth, up = upAxis * halfHeight;
      Vector3 tl = center - right + up, tr = center + right + up, br = center + right - up, bl = center - right - up;
      int columns = Math.Max(1, particle.Columns), rows = Math.Max(1, particle.Rows), total = Math.Max(1, columns * rows), frame = Math.Max(0, Math.Min(total - 1, particle.Frame));
      int col = frame % columns, row = frame / columns; float u0 = col / (float)columns, v0 = row / (float)rows, u1 = (col + 1) / (float)columns, v1 = (row + 1) / (float)rows;
      Vector3 normal = Vector3.Cross(rightAxis, upAxis); if (normal.LengthSquared() <= 1e-8f) normal = -camera.Look; else normal.Normalize();
      Vector3 tangent = rightAxis;
      var vertices = new[] {
        new PosNormalTexTan(tl, normal, new Vector2(u0,v0), tangent), new PosNormalTexTan(tr, normal, new Vector2(u1,v0), tangent), new PosNormalTexTan(br, normal, new Vector2(u1,v1), tangent),
        new PosNormalTexTan(tl, normal, new Vector2(u0,v0), tangent), new PosNormalTexTan(br, normal, new Vector2(u1,v1), tangent), new PosNormalTexTan(bl, normal, new Vector2(u0,v1), tangent)
      };
      try {
        DataBox mapped = ImmediateContext.MapSubresource(gpu.Buffer, MapMode.WriteDiscard, SlimDX.Direct3D11.MapFlags.None); mapped.Data.WriteRange(vertices); ImmediateContext.UnmapSubresource(gpu.Buffer, 0);
        ImmediateContext.InputAssembler.InputLayout = inputLayout; ImmediateContext.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
        ImmediateContext.InputAssembler.SetVertexBuffers(0, new VertexBufferBinding(gpu.Buffer, PosNormalTexTan.Stride, 0));
        fx.SetWorld(Matrix.Identity); fx.SetViewProj(viewProj); fx.SetPlaceableBlueGlow(false); fx.SetMapArt(gpu.Texture, 1f, particle.Color);
        // World particles must be depth-tested. The old generic MapArt pass deliberately disables depth for UI/map
        // overlays, which made forcefields and VFX draw through walls. Respect the common PRT blend modes too:
        // DestBlend=ONE is additive (opaque black contributes nothing), avoiding the moving black rectangles seen on
        // streak/energy textures.
        var particleTech = fx.FxParticleAlpha;
        if (particle.DestBlend == "D3DBLEND_ONE") particleTech = fx.FxParticleAdditive;
        else if (particle.DestBlend == "D3DBLEND_SRCCOLOR" || particle.SourceBlend == "D3DBLEND_ZERO") particleTech = fx.FxParticleMultiply;
        particleTech.GetPassByIndex(0).Apply(ImmediateContext); ImmediateContext.Draw(6, 0); fx.ClearMapArt(); return true;
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
        result.MasterFields = master;
        result.MasterCheckDynamicData = String.Equals(FxString(master, "_fxCheckDynData"), "true", StringComparison.OrdinalIgnoreCase);
        result.MasterStartIf = ParseWorldFxStartIf(masterField);
        result.MasterName = FxString(master, "_fxName", "PARENT"); result.TimerType = FxString(master, "_fxTimerType", "NEVERFIRE"); result.FireRate = ParseWorldFxTime(FxString(master, "_fxFireRate"));
        float timeout = ParseWorldFxTime(FxString(master, "_fxTimeOut"));
        if (timeout > 0) result.GroupEnd = timeout; else if (String.Equals(result.TimerType, "ONESHOT", StringComparison.OrdinalIgnoreCase) && result.FireRate > 0) result.GroupEnd = result.FireRate;
        ParseWorldFxNodeList(root, "_fxEmitterList", false, false, false, false, result.Nodes);
        ParseWorldFxNodeList(root, "_fxModelList", false, true, false, false, result.Nodes);
        ParseWorldFxNodeList(root, "_fxModelFaderList", false, false, true, false, result.Nodes);
        ParseWorldFxNodeList(root, "_fxTransformerList", false, false, false, true, result.Nodes);
        ParseWorldFxNodeList(root, "_fxAttacherList", false, false, false, false, result.Nodes, attacher: true);
        ParseWorldFxNodeList(root, "_fxLightList", false, false, false, false, result.Nodes, light: true);
        ParseWorldFxNodeList(root, "_fxLightningList", false, false, false, false, result.Nodes, lightning: true);
        ParseWorldFxNodeList(root, "_fxProjectorList", false, false, false, false, result.Nodes, projector: true);
        // Silent placement nodes still matter as named anchors: Jedipedia found thousands of emitters/transformers/lightning endpoints
        // attached to _fx3DSoundList entries even though the preview does not play Wwise audio.
        ParseWorldFxNodeList(root, "_fx3DSoundList", false, false, false, false, result.Nodes, sound: true);
        ParseWorldFxNodeList(root, "_fxTransformArcList", false, false, false, false, result.Nodes, arc: true);
        ParseWorldFxNodeList(root, "_fxList", true, false, false, false, result.Nodes);
        ResolveWorldFxNodeTransforms(result);
        ResolveWorldFxChases(result);
        ResolveWorldFxTimings(result);
      } catch (Exception ex) { System.Diagnostics.Debug.WriteLine("SWTOR structured FXSPEC parse failed " + path + ": " + ex.Message); result = null; }
      if (result != null) worldFxSpecCache[path] = result; return result;
    }

    private void ParseWorldFxNodeList(XElement root, string fieldName, bool group, bool model, bool fader, bool transformer,
        List<WorldFxNodeDefinition> output, bool attacher = false, bool arc = false, bool lightning = false, bool light = false, bool sound = false, bool projector = false) {
      XElement list = root.Elements().FirstOrDefault(x => x.Name.LocalName == "f" && String.Equals((string)x.Attribute("name"), fieldName, StringComparison.Ordinal));
      if (list == null) return; int index = 0;
      foreach (XElement entry in list.Elements().Where(x => x.Name.LocalName == "e")) {
        Dictionary<string, string> fields = ParseWorldFxFieldMap(entry); if (fields.Count == 0) continue;
        var node = new WorldFxNodeDefinition {
          Fields = fields, IsGroup = group, IsModel = model, IsFader = fader, IsTransformer = transformer,
          IsAttacher = attacher, IsArc = arc, IsLightning = lightning, IsLight = light, IsSound = sound, IsProjector = projector,
          IsEmitter = !group && !model && !fader && !transformer && !attacher && !arc && !lightning && !light && !sound && !projector,
          CheckDynamicData = String.Equals(FxString(fields, "_fxCheckDynData"), "true", StringComparison.OrdinalIgnoreCase),
          StartIf = ParseWorldFxStartIf(entry)
        };
        string kind = group ? "group#" : model ? "model#" : fader ? "fader#" : transformer ? "transformer#" :
          attacher ? "attacher#" : arc ? "arc#" : lightning ? "lightning#" : light ? "light#" : sound ? "sound#" : projector ? "projector#" : "emitter#";
        node.Name = FxString(fields, "_fxName", kind + index++); node.Resource = FxString(fields, "_fxResourceName");
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
        node.HideOnStop = !group && !model && !fader && !transformer && !attacher && !arc && !lightning && !light && !sound && !projector &&
          String.Equals(FxString(fields, "_fxHowToStop"), "HIDE", StringComparison.OrdinalIgnoreCase);
        if (model) {
          node.ModelFadeIn = String.Equals(FxString(fields, "_fxHowToStartModel"), "FADE_IN", StringComparison.OrdinalIgnoreCase);
          node.ModelFadeOut = String.Equals(FxString(fields, "_fxHowToStopModel"), "FADE_OUT", StringComparison.OrdinalIgnoreCase);
          node.ModelFadeInTime = ParseWorldFxTime(FxString(fields, "_fxFadeInTime"));
          node.ModelFadeOutTime = ParseWorldFxTime(FxString(fields, "_fxFadeOutTime"));
        }
        output.Add(node);
      }
    }

    private static bool WorldFxActorAnchor(string name) {
      return String.Equals(name, "CASTER", StringComparison.OrdinalIgnoreCase) ||
        String.Equals(name, "TARGET", StringComparison.OrdinalIgnoreCase);
    }

    private static Matrix WorldFxHostAnchorFrame(WorldFxSpecDefinition definition, string anchor, string boneName = null) {
      string up = (anchor ?? String.Empty).Trim().ToUpperInvariant();
      WorldFxHostContext host = definition?.HostContext;
      if ((up == "CASTER" || up == "TARGET") && host?.BoneFrame != null && !String.IsNullOrWhiteSpace(boneName)) {
        try {
          Matrix? bone = host.BoneFrame(up, boneName.Trim());
          if (bone.HasValue) return bone.Value;
        } catch { }
      }
      // Jedipedia's opts.caster/opts.target are points, not actor orientation frames. Preserve only translation when no
      // explicit bone was requested; otherwise a rotated NPC root would incorrectly tip every CASTER attachment.
      Matrix frame = up == "TARGET" ? (host?.TargetFrame ?? Matrix.Translation(0f, 0f, 2f)) : (host?.CasterFrame ?? Matrix.Identity);
      return Matrix.Translation(frame.M41, frame.M42, frame.M43);
    }

    private static void WorldFxMatrixParts(Matrix frame, out Vector3 position, out Vector3 rotation, out Vector3 scale) {
      position = new Vector3(frame.M41, frame.M42, frame.M43);
      float sx = (float)Math.Sqrt(frame.M11 * frame.M11 + frame.M12 * frame.M12 + frame.M13 * frame.M13);
      float sy = (float)Math.Sqrt(frame.M21 * frame.M21 + frame.M22 * frame.M22 + frame.M23 * frame.M23);
      float sz = (float)Math.Sqrt(frame.M31 * frame.M31 + frame.M32 * frame.M32 + frame.M33 * frame.M33);
      if (!(sx > 1e-7f)) sx = 1f; if (!(sy > 1e-7f)) sy = 1f; if (!(sz > 1e-7f)) sz = 1f;
      scale = new Vector3(sx, sy, sz);
      float m11 = frame.M11 / sx, m12 = frame.M12 / sx, m13 = frame.M13 / sx;
      float m22 = frame.M22 / sy, m23 = frame.M23 / sy;
      float m32 = frame.M32 / sz, m33 = frame.M33 / sz;
      double y = Math.Asin(Math.Max(-1.0, Math.Min(1.0, -m13)));
      double cy = Math.Cos(y), x, z;
      if (Math.Abs(cy) > 1e-6) { x = Math.Atan2(m23, m33); z = Math.Atan2(m12, m11); }
      else { x = Math.Atan2(-m32, m22); z = 0.0; }
      const double R = 180.0 / Math.PI;
      rotation = new Vector3((float)(x * R), (float)(y * R), (float)(z * R));
    }

    // Apply the FxPlayer host's CASTER/TARGET and actor-bone anchors before resolving named element chains. This is
    // intentionally a per-player clone operation: the same cached FXSPEC may be placed on many actors with different
    // skeletons. StartLoc is a spawn frame; AttachTo additionally inherits the frame orientation/scale when a bone is
    // named, which is the important weapon/hand/chest case recovered by Jedipedia.
    private static void ApplyWorldFxHostAnchors(WorldFxSpecDefinition definition) {
      if (definition?.Nodes == null) return;
      foreach (WorldFxNodeDefinition node in definition.Nodes) {
        if (node?.Fields == null) continue;
        bool attached = WorldFxActorAnchor(node.AttachTo);
        bool started = !attached && WorldFxActorAnchor(node.StartLoc);
        if (!attached && !started) continue;
        string anchor = attached ? node.AttachTo : node.StartLoc;
        string boneName = FxString(node.Fields, attached ? "_fxAttachBone" : "_fxStartLocBone");
        Matrix frame = WorldFxHostAnchorFrame(definition, anchor, boneName);
        if (attached) { node.HostFollowAnchor = anchor; node.HostFollowBone = boneName; node.HostFollowInitialFrame = frame; }
        WorldFxMatrixParts(frame, out _, out Vector3 frameRotation, out Vector3 frameScale);
        Vector3 authored = ParseWorldFxVector(FxString(node.Fields, attached ? "_fxAttachPosition" : "_fxStartLocOffset"), Vector3.Zero);
        node.Offset = Vector3.TransformCoordinate(authored, frame);
        bool orientedByBone = !String.IsNullOrWhiteSpace(boneName) && definition.HostContext?.BoneFrame != null;
        if (attached || orientedByBone) {
          node.Rotation = ComposeWorldFxEulerDegrees(frameRotation, node.Rotation);
          node.Scale = new Vector3(node.Scale.X * frameScale.X, node.Scale.Y * frameScale.Y, node.Scale.Z * frameScale.Z);
        } else if (node.IsProjector) {
          node.Scale = new Vector3(node.Scale.X * frameScale.X, node.Scale.Y * frameScale.Y, node.Scale.Z * frameScale.Z);
        }
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
      // Projectors are the one StartLoc element whose offset AND projection box live in the anchor frame. Jedipedia
      // carries that parent scale even without AttachTo (e.g. scan grids mounted on scaled drone locators).
      if (attach || node.IsProjector) {
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

    private static List<WorldFxDynamicPair> ParseWorldFxStartIf(XElement entry) {
      if (entry == null) return null;
      XElement field = entry.Elements().FirstOrDefault(x => x.Name.LocalName == "f" && String.Equals((string)x.Attribute("name"), "_fxStartIF", StringComparison.Ordinal));
      if (field == null) return null;
      var result = new List<WorldFxDynamicPair>();
      foreach (XElement keyNode in field.Descendants().Where(x => x.Name.LocalName == "k")) {
        XElement valueNode = keyNode.ElementsAfterSelf().FirstOrDefault();
        if (valueNode == null || valueNode.Name.LocalName != "e") continue;
        string key = (keyNode.Value ?? String.Empty).Trim();
        if (key.Length == 0) continue;
        result.Add(new WorldFxDynamicPair { Key = key, Value = (valueNode.Value ?? String.Empty).Trim() });
      }
      return result.Count > 0 ? result : null;
    }

    private static bool TryGetWorldFxDynamicExact(IReadOnlyDictionary<string, string> data, string key, out string value) {
      value = null; if (data == null || key == null) return false;
      foreach (KeyValuePair<string, string> pair in data) {
        if (String.Equals(pair.Key, key, StringComparison.Ordinal)) { value = pair.Value ?? String.Empty; return true; }
      }
      return false;
    }

    private static string WorldFxDynamicDataFingerprint(IReadOnlyDictionary<string, string> data) {
      if (data == null) return "N;";
      var b = new StringBuilder("D;");
      foreach (KeyValuePair<string, string> pair in data.OrderBy(x => x.Key, StringComparer.Ordinal)) {
        b.Append(pair.Key?.Length ?? 0).Append(':').Append(pair.Key ?? String.Empty).Append('=')
          .Append(pair.Value?.Length ?? 0).Append(':').Append(pair.Value ?? String.Empty).Append(';');
      }
      return b.ToString();
    }

    private static WorldFxSpecDefinition CloneWorldFxDefinitionForDynamicData(WorldFxSpecDefinition source, IReadOnlyDictionary<string, string> data, WorldFxHostContext hostContext = null) {
      if (source == null) return null;
      // Always clone for a live player. Host CASTER/TARGET/bone resolution is instance-specific even when no dynamic
      // data was supplied, so mutating the shared parsed cache would make the next placement inherit the first actor.
      var result = new WorldFxSpecDefinition {
        HostContext = hostContext,
        Path = source.Path, MasterName = source.MasterName, TimerType = source.TimerType, FireRate = source.FireRate, GroupEnd = source.GroupEnd,
        MasterCheckDynamicData = source.MasterCheckDynamicData,
        MasterStartIf = source.MasterStartIf == null ? null : source.MasterStartIf.Select(x => new WorldFxDynamicPair { Key = x.Key, Value = x.Value }).ToList(),
        MasterFields = source.MasterFields == null ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) :
          new Dictionary<string, string>(source.MasterFields, StringComparer.OrdinalIgnoreCase)
      };
      if (data != null && result.MasterCheckDynamicData && result.MasterStartIf != null) {
        bool masterGateOpen = true;
        foreach (WorldFxDynamicPair pair in result.MasterStartIf) {
          if (pair == null || String.IsNullOrEmpty(pair.Key)) continue;
          if (pair.Key[0] == '@') {
            if (TryGetWorldFxDynamicExact(data, pair.Key, out string replacement) && !String.IsNullOrWhiteSpace(pair.Value))
              result.MasterFields[pair.Value.Trim()] = replacement;
          } else if (!TryGetWorldFxDynamicExact(data, pair.Key, out string actual) || !String.Equals(actual, pair.Value ?? String.Empty, StringComparison.Ordinal)) {
            masterGateOpen = false;
          }
        }
        result.DynamicGateOpen = masterGateOpen;
        result.MasterName = FxString(result.MasterFields, "_fxName", result.MasterName);
        result.TimerType = FxString(result.MasterFields, "_fxTimerType", result.TimerType);
        result.FireRate = ParseWorldFxTime(FxString(result.MasterFields, "_fxFireRate"));
        float masterTimeout = ParseWorldFxTime(FxString(result.MasterFields, "_fxTimeOut"));
        if (masterTimeout > 0f) result.GroupEnd = masterTimeout;
        else if (String.Equals(result.TimerType, "ONESHOT", StringComparison.OrdinalIgnoreCase) && result.FireRate > 0f) result.GroupEnd = result.FireRate;
      }
      foreach (WorldFxNodeDefinition original in source.Nodes) {
        if (original == null) continue;
        var node = new WorldFxNodeDefinition {
          IsGroup = original.IsGroup, IsModel = original.IsModel, IsFader = original.IsFader, IsTransformer = original.IsTransformer,
          IsAttacher = original.IsAttacher, IsArc = original.IsArc, IsLightning = original.IsLightning, IsLight = original.IsLight,
          IsSound = original.IsSound, IsProjector = original.IsProjector, IsEmitter = original.IsEmitter,
          CheckDynamicData = original.CheckDynamicData,
          StartIf = original.StartIf == null ? null : original.StartIf.Select(x => new WorldFxDynamicPair { Key = x.Key, Value = x.Value }).ToList(),
          Fields = original.Fields == null ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) :
            new Dictionary<string, string>(original.Fields, StringComparer.OrdinalIgnoreCase),
          Name = original.Name
        };
        if (data != null && node.CheckDynamicData && node.StartIf != null) {
          bool gateOpen = true;
          foreach (WorldFxDynamicPair pair in node.StartIf) {
            if (pair == null || String.IsNullOrEmpty(pair.Key)) continue;
            if (pair.Key[0] == '@') {
              if (TryGetWorldFxDynamicExact(data, pair.Key, out string replacement) && !String.IsNullOrWhiteSpace(pair.Value))
                node.Fields[pair.Value.Trim()] = replacement;
            } else {
              if (!TryGetWorldFxDynamicExact(data, pair.Key, out string actual) || !String.Equals(actual, pair.Value ?? String.Empty, StringComparison.Ordinal))
                gateOpen = false;
            }
          }
          node.DynamicGateOpen = gateOpen;
        }
        RefreshWorldFxNodeFromFields(node, original.Name);
        if (!node.DynamicGateOpen) { node.WhenToStart = "NEVER"; node.WhenToStop = "NEVER"; }
        result.Nodes.Add(node);
      }
      ApplyWorldFxHostAnchors(result);
      ResolveWorldFxNodeTransforms(result);
      ResolveWorldFxChases(result);
      ResolveWorldFxTimings(result);
      return result;
    }

    private static void RefreshWorldFxNodeFromFields(WorldFxNodeDefinition node, string fallbackName) {
      if (node?.Fields == null) return;
      Dictionary<string, string> fields = node.Fields;
      node.Name = FxString(fields, "_fxName", fallbackName ?? node.Name);
      node.Resource = FxString(fields, "_fxResourceName");
      node.WhenToStart = FxString(fields, "_fxWhenToStart", "ONFXSTART").ToUpperInvariant();
      node.StartFxName = FxString(fields, "_fxStartFxName"); node.StartDelay = ParseWorldFxTime(FxString(fields, "_fxStartDelay"));
      node.WhenToStop = FxString(fields, "_fxWhenToStop", "ONFXSTOP").ToUpperInvariant();
      node.StopFxName = FxString(fields, "_fxStopFxName"); node.StopDelay = ParseWorldFxTime(FxString(fields, "_fxStopDelay"));
      node.OwnerName = FxString(fields, "_fxOwnerName"); node.AttachTo = FxString(fields, "_fxAttachTo"); node.StartLoc = FxString(fields, "_fxStartLoc");
      bool attached = !String.IsNullOrWhiteSpace(node.AttachTo);
      node.Offset = ParseWorldFxVector(FxString(fields, attached ? "_fxAttachPosition" : "_fxStartLocOffset"), Vector3.Zero);
      node.Scale = ParseWorldFxVector(FxString(fields, "_fxScale"), new Vector3(1f, 1f, 1f));
      node.Rotation = WorldFxAuthoredYprToEulerDegrees(ParseWorldFxVector(FxString(fields, attached ? "_fxAttachRotation" : "_fxRotation"), Vector3.Zero));
      node.Tint = ParseWorldFxColor(FxString(fields, "_fxTintColor"), new Vector4(1f, 1f, 1f, 1f));
      Vector4 diffuse = ParseWorldFxColor(FxString(fields, "_fxDiffuseColor"), new Vector4(1f, 1f, 1f, 1f));
      node.Tint = new Vector4(node.Tint.X * diffuse.X, node.Tint.Y * diffuse.Y, node.Tint.Z * diffuse.Z, node.Tint.W * diffuse.W);
      node.LifeModifier = 1f;
      if (Single.TryParse(FxString(fields, "_fxParticleLifeSpanModifier"), NumberStyles.Float, CultureInfo.InvariantCulture, out float lifeMod) && lifeMod > 0f) node.LifeModifier = lifeMod;
      node.HideOnStop = node.IsEmitter && String.Equals(FxString(fields, "_fxHowToStop"), "HIDE", StringComparison.OrdinalIgnoreCase);
      node.IsChaser = false; node.ChaseDirection = Vector3.Zero; node.ChaseSpeed = node.ChaseReach = node.ChaseTravelTime = 0f;
      if (node.IsModel) {
        node.ModelFadeIn = String.Equals(FxString(fields, "_fxHowToStartModel"), "FADE_IN", StringComparison.OrdinalIgnoreCase);
        node.ModelFadeOut = String.Equals(FxString(fields, "_fxHowToStopModel"), "FADE_OUT", StringComparison.OrdinalIgnoreCase);
        node.ModelFadeInTime = ParseWorldFxTime(FxString(fields, "_fxFadeInTime"));
        node.ModelFadeOutTime = ParseWorldFxTime(FxString(fields, "_fxFadeOutTime"));
      }
    }

    private static Dictionary<string, string> ParseWorldFxFieldMap(XElement container) {
      var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); if (container == null) return result;
      foreach (XElement field in container.Elements().Where(x => x.Name.LocalName == "f")) {
        string name = (string)field.Attribute("name"); if (String.IsNullOrWhiteSpace(name)) continue;
        if (!field.Elements().Any()) { result[name] = (field.Value ?? String.Empty).Trim(); continue; }
        // Model-fader diffuse endpoints are nested four-channel structs rather than leaf values. Flatten just that
        // well-defined color shape into the same #r,g,b,a syntax WorldFxValue already evaluates. Other nested
        // marshal lists stay out of this flat map; _fxStartIF is parsed separately by ParseWorldFxStartIf so its
        // ordered key/value pairs keep their substitution/gate semantics.
        var channels = field.Descendants().Where(x => x.Name.LocalName == "f" && !x.Elements().Any())
          .Select(x => new { Name = ((string)x.Attribute("name") ?? String.Empty).Trim().ToLowerInvariant(), Value = (x.Value ?? String.Empty).Trim() })
          .Where(x => x.Name == "colorr" || x.Name == "colorg" || x.Name == "colorb" || x.Name == "colora")
          .GroupBy(x => x.Name).ToDictionary(x => x.Key, x => x.First().Value, StringComparer.OrdinalIgnoreCase);
        if (channels.Count >= 3) result[name] = "#" +
          (channels.TryGetValue("colorr", out string r) ? r : "0") + "," +
          (channels.TryGetValue("colorg", out string g) ? g : "0") + "," +
          (channels.TryGetValue("colorb", out string b) ? b : "0") + "," +
          (channels.TryGetValue("colora", out string a) ? a : "1");
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

    private static Vector3 InverseRotateWorldFxVector(Vector3 value, Vector3 degrees) {
      // RotateWorldFxVector applies X -> Y -> Z. Its inverse must therefore apply -Z -> -Y -> -X.
      float rx = -degrees.X * (float)Math.PI / 180f, ry = -degrees.Y * (float)Math.PI / 180f, rz = -degrees.Z * (float)Math.PI / 180f;
      if (Math.Abs(rz) > .000001f) { float c = (float)Math.Cos(rz), s = (float)Math.Sin(rz); float x = value.X * c - value.Y * s, y = value.X * s + value.Y * c; value.X = x; value.Y = y; }
      if (Math.Abs(ry) > .000001f) { float c = (float)Math.Cos(ry), s = (float)Math.Sin(ry); float x = value.X * c + value.Z * s, z = -value.X * s + value.Z * c; value.X = x; value.Z = z; }
      if (Math.Abs(rx) > .000001f) { float c = (float)Math.Cos(rx), s = (float)Math.Sin(rx); float y = value.Y * c - value.Z * s, z = value.Y * s + value.Z * c; value.Y = y; value.Z = z; }
      return value;
    }

    // Velocity contributed by the FXSPEC node graph itself rather than by the leaf PRT. Jedipedia exposes this as
    // item.extVel so trajectory-aligned smoke cores/mesh leaves follow missiles and TransformArc projectiles even
    // when their own PRT Trajectory is zero. The value is in effect-local/world-graph coordinates.
    private static Vector3 WorldFxNodeExternalVelocity(WorldFxSpecDefinition definition, WorldFxNodeDefinition node, float clock) {
      if (definition?.Nodes == null || node == null) return Vector3.Zero;
      Vector3 velocity = Vector3.Zero;
      foreach (WorldFxNodeDefinition chaser in definition.Nodes) {
        if (chaser == null || !chaser.IsChaser || !WorldFxNodeRidesAsset(definition, node, chaser.Name)) continue;
        float end = chaser.StartAt + chaser.ChaseTravelTime;
        if (clock >= chaser.StartAt && clock < end && (Single.IsInfinity(chaser.StopAt) || clock < chaser.StopAt))
          velocity += chaser.ChaseDirection * chaser.ChaseSpeed;
      }
      foreach (WorldFxNodeDefinition arc in definition.Nodes) {
        if (arc == null || !arc.IsArc || !WorldFxArcMovesNode(definition, arc, node) || clock < arc.StartAt || (!Single.IsInfinity(arc.StopAt) && clock >= arc.StopAt)) continue;
        float duration = Math.Max(.001f, ParseWorldFxTime(FxString(arc.Fields, "_fxDuration")));
        float u = (clock - arc.StartAt) / duration;
        if (u < 0f || u >= 1f) continue;
        Vector3 startPos = WorldFxResolveArcLocation(definition, arc, FxString(arc.Fields, "_fxStartLoc", "DEFAULT"), FxString(arc.Fields, "_fxOffset"), clock, FxString(arc.Fields, "_fxStartLocBone"));
        Vector3 targetPos = WorldFxResolveArcLocation(definition, arc, FxString(arc.Fields, "_fxTargetLoc", "TARGET"), FxString(arc.Fields, "_fxTargetOffset"), clock, FxString(arc.Fields, "_fxTargetLocBone"));
        Vector3 delta = targetPos - startPos;
        float height = 0f; Single.TryParse(FxString(arc.Fields, "_fxArcSize", "0"), NumberStyles.Float, CultureInfo.InvariantCulture, out height);
        velocity += new Vector3(delta.X / duration, (delta.Y + height * 4f * (1f - 2f * u)) / duration, delta.Z / duration);
      }
      return velocity;
    }

    // Same rotation helper in radians for renderer-side orientation code. A similarly named helper also exists inside
    // WorldPrtSystem; that nested method is intentionally not visible from the outer View_AREA renderer scope.
    private static Vector3 RotateWorldFxVectorRadians(Vector3 value, Vector3 radians) {
      if (Math.Abs(radians.X) > .000001f) { float c = (float)Math.Cos(radians.X), s = (float)Math.Sin(radians.X); float y = value.Y * c - value.Z * s, z = value.Y * s + value.Z * c; value.Y = y; value.Z = z; }
      if (Math.Abs(radians.Y) > .000001f) { float c = (float)Math.Cos(radians.Y), s = (float)Math.Sin(radians.Y); float x = value.X * c + value.Z * s, z = -value.X * s + value.Z * c; value.X = x; value.Z = z; }
      if (Math.Abs(radians.Z) > .000001f) { float c = (float)Math.Cos(radians.Z), s = (float)Math.Sin(radians.Z); float x = value.X * c - value.Y * s, y = value.X * s + value.Y * c; value.X = x; value.Y = y; }
      return value;
    }

    // HeroEngine emitter chase: an emitter with _fxSpeed and a target flies its whole attachment bundle toward that
    // target, stopping _fxTolerance short. This is the built-in projectile path used by many missile/bolt FXSPECs.
    // Generic world effects do not carry a separate target skeleton, so CASTER/TARGET use the same host-local
    // approximations as lightning/arcs; named effect elements resolve exactly from the parsed graph.
    private static void ResolveWorldFxChases(WorldFxSpecDefinition definition) {
      if (definition?.Nodes == null) return;
      foreach (WorldFxNodeDefinition node in definition.Nodes) {
        if (node == null || !node.IsEmitter) continue;
        if (!Single.TryParse(FxString(node.Fields, "_fxSpeed"), NumberStyles.Float, CultureInfo.InvariantCulture, out float speed) || !(speed > 0f)) continue;
        string targetName = FxString(node.Fields, "_fxTargetName").Trim();
        if (targetName.Length == 0 || String.Equals(targetName, "NOTARGET", StringComparison.OrdinalIgnoreCase) || String.Equals(targetName, "NOTHING", StringComparison.OrdinalIgnoreCase)) continue;
        Vector3 target;
        if (WorldFxActorAnchor(targetName)) {
          Matrix targetFrame = WorldFxHostAnchorFrame(definition, targetName, FxString(node.Fields, "_fxTargetBoneName"));
          target = new Vector3(targetFrame.M41, targetFrame.M42, targetFrame.M43);
        } else {
          WorldFxNodeDefinition targetNode = definition.Nodes.FirstOrDefault(x => x != null && String.Equals(x.Name, targetName, StringComparison.OrdinalIgnoreCase));
          if (targetNode != null) target = targetNode.Offset;
          else {
            Matrix fallbackFrame = WorldFxHostAnchorFrame(definition, "TARGET", FxString(node.Fields, "_fxTargetBoneName"));
            target = new Vector3(fallbackFrame.M41, fallbackFrame.M42, fallbackFrame.M43);
          }
        }
        target += ParseWorldFxVector(FxString(node.Fields, "_fxOffset"), Vector3.Zero);
        Vector3 delta = target - node.Offset; float distance = delta.Length();
        float tolerance = 0f; Single.TryParse(FxString(node.Fields, "_fxTolerance"), NumberStyles.Float, CultureInfo.InvariantCulture, out tolerance);
        float reach = Math.Max(0f, distance - Math.Max(0f, tolerance)); if (!(reach > .0001f) || !(distance > .000001f)) continue;
        node.IsChaser = true; node.ChaseSpeed = speed; node.ChaseReach = reach; node.ChaseTravelTime = reach / speed;
        node.ChaseDirection = delta / distance;
      }
    }

    private static float WorldFxArrivalTime(WorldFxNodeDefinition node) {
      if (node == null || !node.IsChaser || Single.IsInfinity(node.StartAt)) return Single.PositiveInfinity;
      return node.StartAt + node.ChaseTravelTime;
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
            else if (node.WhenToStart == "ONTARGETACHIEVED" || node.WhenToStart == "ONTARGETINGCOMPLETE") start = SafeWorldFxTime(WorldFxArrivalTime(startRef), node.StartDelay);
          }
          node.StartAt = start;
          float stop = Single.PositiveInfinity;
          if (node.WhenToStop == "NEVER") stop = Single.PositiveInfinity;
          else if (!String.IsNullOrWhiteSpace(node.StopFxName) && byName.TryGetValue(node.StopFxName, out WorldFxNodeDefinition stopRef)) {
            if (node.WhenToStop == "ONFXSTART") stop = SafeWorldFxTime(stopRef.StartAt, node.StopDelay);
            else if (node.WhenToStop == "ONFXSTOP") stop = SafeWorldFxTime(stopRef.StopAt, node.StopDelay);
            else if (node.WhenToStop == "ONTARGETACHIEVED" || node.WhenToStop == "ONTARGETINGCOMPLETE") stop = SafeWorldFxTime(WorldFxArrivalTime(stopRef), node.StopDelay);
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
