using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using GomLib.Models;
using File = TorArchive.File;

namespace GomLib.ModelLoader {
  public class AppearanceLoader {
    private readonly DataObjectModel _dom;
    internal Dictionary<String, WeaponAppearance> itmAppearanceDatatable;
    private readonly Dictionary<String, (String Bt, String Gen)> partsMacroCache =
      new Dictionary<String, (String Bt, String Gen)>(StringComparer.OrdinalIgnoreCase);

    public AppearanceLoader(DataObjectModel dom) {
      _dom = dom;
    }
    public void Flush() {
      itmAppearanceDatatable = null;
      partsMacroCache.Clear();
    }

    /// <summary>
    /// Resolve the [bt]/[gen] macros from a character spec. These are not aliases for the
    /// character-spec name: old creature rigs frequently reuse another body's dynamic art.
    /// Jedipedia reads the [PARTSMACROS] section from art/dynamic/spec/&lt;bodyType&gt;.dyc.
    /// </summary>
    public (String Bt, String Gen) ResolvePartsMacros(String bodyType) {
      String name = (bodyType ?? String.Empty).Trim().ToLowerInvariant();
      if (partsMacroCache.TryGetValue(name, out var cached)) return cached;

      String bt = name;
      String gen = name.StartsWith("bf", StringComparison.OrdinalIgnoreCase) ? "f" : "m";
      if (String.IsNullOrWhiteSpace(name) || _dom?.Assets == null) return (bt, gen);

      String path = "/resources/art/dynamic/spec/" + name + ".dyc";
      File file = _dom.Assets.FindFile(path);
      // Jedipedia deliberately does not cache a missing spec: another TOR can be added later in
      // the same application session. Return the historical fallback now and retry on the next use.
      if (file == null) return (bt, gen);

      try {
        using (file)
        using (Stream stream = file.OpenCopyInMemory())
        using (var reader = new StreamReader(stream, System.Text.Encoding.UTF8, true)) {
          bool inMacros = false;
          while (!reader.EndOfStream) {
            String line = (reader.ReadLine() ?? String.Empty).Trim();
            if (line.Length == 0 || line.StartsWith("!", StringComparison.Ordinal)) continue;
            if (line.StartsWith("[", StringComparison.Ordinal)) {
              inMacros = line.Equals("[PARTSMACROS]", StringComparison.OrdinalIgnoreCase);
              continue;
            }
            if (!inMacros) continue;
            Int32 equals = line.IndexOf('=');
            if (equals <= 0) continue;
            String key = line.Substring(0, equals).Trim();
            String value = line.Substring(equals + 1).Trim().ToLowerInvariant();
            if (String.Equals(key, "bt", StringComparison.OrdinalIgnoreCase) && value.Length > 0) bt = value;
            else if (String.Equals(key, "gen", StringComparison.OrdinalIgnoreCase) && value.Length > 0) gen = value;
          }
        }
      } catch (Exception ex) {
        System.Diagnostics.Debug.WriteLine("Character appearance macros failed " + path + ": " + ex.Message);
        // A malformed/unavailable file should be retryable for the same reason as a missing archive.
        return (bt, gen);
      }

      var result = (Bt: bt, Gen: gen);
      partsMacroCache[name] = result;
      return result;
    }

    public String ApplyPartsMacros(String value, String bodyType) {
      if (String.IsNullOrEmpty(value)) return value ?? String.Empty;
      var macros = ResolvePartsMacros(bodyType);
      return value.Replace("[bt]", macros.Bt, StringComparison.OrdinalIgnoreCase)
        .Replace("[gen]", macros.Gen, StringComparison.OrdinalIgnoreCase);
    }
    public GameObject Load(GameObject obj, GomObject gom) {
      if (obj == null) throw new ArgumentNullException(nameof(obj));
      if (gom == null) return null;

      return Load(gom);
    }
    public GameObject Load(GomObject obj) {
      if (obj == null) return null;

      switch (obj.Name.Substring(0, 3)) {
        case "ipp":
          return LoadIpp(obj);
        case "npp":
          return LoadNpp(obj);
        default:
          throw new IndexOutOfRangeException();
      }
    }

    public GameObject Load(String fqn) => Load(_dom.GetObject(fqn));

    public GameObject Load(UInt64 nodeId) => Load(_dom.GetObject(nodeId));

    private static String AppSlotName(Object raw) {
      if (raw is ScriptEnum scriptEnum) {
        String resolved = scriptEnum.ToString();
        if (!String.IsNullOrWhiteSpace(resolved) && !resolved.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
          return resolved;
        raw = scriptEnum.Value;
      }

      Int64 value;
      try { value = Convert.ToInt64(raw); }
      catch { return raw?.ToString(); }

      return value switch {
        1 => "appSlotAge", 2 => "appSlotBoot", 3 => "appSlotBracer", 4 => "appSlotChest",
        5 => "appSlotComplexion", 6 => "appSlotCreature", 7 => "appSlotEyeColor", 8 => "appSlotFace",
        9 => "appSlotFaceHair", 10 => "appSlotFacePaint", 11 => "appSlotHair", 12 => "appSlotHairColor",
        13 => "appSlotHand", 14 => "appSlotHead", 15 => "appSlotLeg", 16 => "appSlotSkinColor",
        17 => "appSlotWaist", 19 => "appSlotGarmentHue", 20 => "appSlotColorScheme",
        _ => null
      };
    }

    public AppSlot LoadAppSlot(GomObjectData obj, String btOverride, String slotTypeOverride = null) {
      if (obj == null) return new AppSlot(_dom) { BodyType = btOverride, Type = slotTypeOverride ?? "appSlotAge" };

      AppSlot app = new AppSlot(_dom) {
        Dom = _dom,
        BodyType = btOverride
      };
      String slotType = !String.IsNullOrWhiteSpace(slotTypeOverride)
        ? slotTypeOverride
        : AppSlotName(obj.ValueOrDefault<Object>("appAppearanceSlotType", null));
      app.Type = String.IsNullOrWhiteSpace(slotType) ? "appSlotAge" : slotType;

      app.ModelID = obj.ValueOrDefault<Int64>("appAppearanceSlotModelID", 0);
      app.MaterialIndex = obj.ValueOrDefault<Int64>("appAppearanceSlotMaterialIndex", 0);
      app.Attachments = obj.ValueOrDefault("appAppearanceSlotAttachments", new List<Object>())
        .ConvertAll(x => Convert.ToInt64(x));
      // Historical appearance slots omit defaults far more aggressively than the live schema.
      app.RandomWeight = obj.ValueOrDefault<Int64>("appAppearanceSlotRandomWeight", 0);
      app.PrimaryHueId = obj.ValueOrDefault<Int64>("appAppearanceSlotHuePrimary", 0);
      app.SecondaryHueId = obj.ValueOrDefault<Int64>("appAppearanceSlotHueSecondary", 0);

      return app;
    }
    public ItemAppearance LoadIpp(GomObject obj) {

      ItemAppearance pkg = new ItemAppearance(_dom) {
        Fqn = obj.Name,
        Id = obj.Id,
        Dom_ = _dom,
        References = obj.References,
        ColorScheme = obj.Data.ValueOrDefault<Int64>("ippColorScheme", 0),
        VOSoundTypeOverride = obj.Data.ValueOrDefault("ippVOSoundTypeOverride", ""),
        IPP = LoadAppSlot(obj.Data, "")
      };

      return pkg;
    }
    public NpcAppearance LoadNpp(GomObject obj) {
      NpcAppearance pkg = new NpcAppearance(_dom) {
        Fqn = obj.Name,
        //Debug.WriteLine(obj.Name);
        Id = obj.Id,
        Dom_ = _dom,
        References = obj.References,
        BodyType = obj.Data.ValueOrDefault<String>("nppBodyType", "bfn")
      };

      Dictionary<Object, Object> slotMap =
        obj.Data.ValueOrDefault<Dictionary<Object, Object>>(
          "nppAppearanceSlotMap_ForPrototype",
          null
        );

      pkg.AppearanceSlotMap = new Dictionary<String, List<AppSlot>>();

      if (slotMap != null) {
        foreach (var kvp in slotMap) {
          // The map key is the authoritative appValidSlot. In pre-2.1 NPP data the embedded
          // appAppearanceSlotType is commonly omitted because it is redundant; defaulting that
          // missing field to Age made every RED slot look in ami.age/index.xml and yielded only a rig.
          String key = AppSlotName(kvp.Key);
          if (String.IsNullOrWhiteSpace(key)) continue;
          List<AppSlot> appList = new List<AppSlot>();

          if (kvp.Value is List<Object> values) {
            for (Int32 i = 0; i < values.Count; i++) {
              if (values[i] is not GomObjectData slotData) continue;
              appList.Add(LoadAppSlot(slotData, pkg.BodyType, key));
            }
          } else if (kvp.Value is GomObjectData oneSlot) {
            appList.Add(LoadAppSlot(oneSlot, pkg.BodyType, key));
          }

          if (appList.Count > 0) pkg.AppearanceSlotMap[key] = appList;
        }
      }

      pkg.NppType =
        ((ScriptEnum)obj.Data.ValueOrDefault<Object>("nppNppType")
          ?? new ScriptEnum()).ToString();

      pkg.SoundPackage = obj.Data.ValueOrDefault("nppSoundPackage", "");
      pkg.ArmorSoundsetOverride = obj.Data.ValueOrDefault("nppArmorSoundsetOverride", "");

      Dictionary<Object, Object> vocalOverrides =
        obj.Data.ValueOrDefault("nppVocalSoundsetOverride", new Dictionary<Object, Object>());
      pkg.VocalSoundsetOverride = new Dictionary<Int64, String>();

      foreach (var kvp in vocalOverrides) {
        pkg.VocalSoundsetOverride.Add((Int64)kvp.Key, (String)kvp.Value);
      }

      return pkg;
    }
    public WeaponAppearance LoadWeaponAppearance(String name, GomObjectData obj) {
      WeaponAppearance pkg = new WeaponAppearance(_dom) {
        Prototype = "itmAppearanceDatatable",
        ProtoDataTable = "itmAppearances",
        Name = name,
        BoneName = obj.ValueOrDefault<String>("itmBoneName", null),
        CombatStance = obj.ValueOrDefault<String>("itmCombatStance", null),
        DrawnOffset = obj.ValueOrDefault<List<Single>>("itmDrawnOffset", null),
        DrawnRotation = obj.ValueOrDefault<List<Single>>("itmDrawnRotation", null),
        DrawnScale = obj.ValueOrDefault<List<Single>>("itmDrawnScale", null),
        DynamicData = obj.ValueOrDefault<String>("itmDynamicData", null),
        FxSpec = obj.ValueOrDefault<String>("itmFxSpec", null),
        Model = obj.ValueOrDefault<String>("itmModel", null),
        StowedOffset = obj.ValueOrDefault<List<Single>>("itmStowedOffset", null),
        StowedRotation = obj.ValueOrDefault<List<Single>>("itmStowedRotation", null),
        StowedScale = obj.ValueOrDefault<List<Single>>("itmStowedScale", null),
        WeaponType = obj.ValueOrDefault("itmWeaponType", new ScriptEnum()).ToString()
      };

      return pkg;
    }
    public WeaponAppearance LoadWeaponAppearance(String name) {
      if (itmAppearanceDatatable == null) {
        GomObject dataTable = _dom.GetObject("itmAppearanceDatatable");
        Dictionary<Object, Object> tempDict =
          dataTable.Data.Get<Dictionary<Object, Object>>("itmAppearances");
        dataTable.Unload();
        itmAppearanceDatatable = new Dictionary<String, WeaponAppearance>();

        foreach (var kvp in tempDict) {
          itmAppearanceDatatable.Add(
            (String)kvp.Key,
            LoadWeaponAppearance((String)kvp.Key, (GomObjectData)kvp.Value)
          );
        }
      }

      itmAppearanceDatatable.TryGetValue(name, out WeaponAppearance output);
      return output;
    }
  }
  public class DetailedAppearanceColorLoader {
    public DataObjectModel _dom;
    private Dictionary<Int64, DetailedAppearanceColor> idMap;

    public DetailedAppearanceColorLoader(DataObjectModel dom) {
      _dom = dom;
      Flush();
    }
    public void Flush() => idMap = new Dictionary<Int64, DetailedAppearanceColor>();
    private void Initialize() {
      GomObject itmAppearanceColorsPrototype =
        _dom.GetObject("itmAppearanceColorsPrototype");
      List<Object> itmAppColorTable =
        itmAppearanceColorsPrototype.Data.ValueOrDefault("itmAppColorTable", new List<Object>());
      Dictionary<Object, Object> itmAppColorIdLookup =
        itmAppearanceColorsPrototype.Data.ValueOrDefault(
          "itmAppColorIdLookup",
          new Dictionary<Object, Object>()
        );
      itmAppearanceColorsPrototype.Unload();
      StringTable stringTable = _dom.StringTable.Find("str.gui.colornames");

      foreach (GomObjectData gom in itmAppColorTable.ConvertAll(x => (GomObjectData)x)) {
        DetailedAppearanceColor det = new DetailedAppearanceColor {
          ColorId = gom.ValueOrDefault<Int64>("itmAppColorId", 0)
        };

        if (itmAppColorIdLookup.ContainsKey(det.ColorId)) {
          det.ShortId = (Int64)itmAppColorIdLookup[det.ColorId];
        }

        det.ColorNameId =
          gom.ValueOrDefault<Int64>("itmAppColorName", 0);
        det.ColorName =
          stringTable.GetText(det.ColorNameId, "str.gui.colornames");
        det.LocalizedColorName =
          stringTable.GetLocalizedText(det.ColorNameId, "str.gui.colornames");
        det.ColorSchemeId =
          gom.ValueOrDefault<Int64>("itmAppColorSchemeId", 0);
        det.HueName =
          gom.ValueOrDefault("itmAppColorHueName", "");
        det.UnknownBool1 =
          gom.ValueOrDefault("4611686298195974006", false);
        det.UnknownBool2 =
          gom.ValueOrDefault("4611686298195974007", false);

        GomObjectData pal1 =
          (GomObjectData)gom.ValueOrDefault<Object>("itmAppColorPalette1Rep", null);

        if (pal1 != null) {
          Byte a = Convert.ToByte(255f * pal1.ValueOrDefault("a", 0f));
          Byte r = Convert.ToByte(255f * pal1.ValueOrDefault("r", 0f));
          Byte g = Convert.ToByte(255f * pal1.ValueOrDefault("g", 0f));
          Byte b = Convert.ToByte(255f * pal1.ValueOrDefault("b", 0f));
          det.Palette1Rep = Color.FromArgb(a, r, g, b);
        }

        GomObjectData pal2 =
          (GomObjectData)gom.ValueOrDefault<Object>("itmAppColorPalette2Rep", null);

        if (pal2 != null) {
          Byte a = Convert.ToByte(255f * pal2.ValueOrDefault("a", 0f));
          Byte r = Convert.ToByte(255f * pal2.ValueOrDefault("r", 0f));
          Byte g = Convert.ToByte(255f * pal2.ValueOrDefault("g", 0f));
          Byte b = Convert.ToByte(255f * pal2.ValueOrDefault("b", 0f));
          det.Palette2Rep = Color.FromArgb(a, r, g, b);
        }

        idMap.Add(det.ShortId, det);
      }
    }
    public DetailedAppearanceColor Load(Int64 id) {
      if (idMap.Count == 0) {
        Initialize();
      }
      idMap.TryGetValue(id, out DetailedAppearanceColor ret);
      return ret;
    }
  }
}
