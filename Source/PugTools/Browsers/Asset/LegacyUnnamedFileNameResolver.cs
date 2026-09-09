using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using nsHashDictionary;
using TorArchive;

namespace PugTools {
  /// <summary>
  /// Completes the legacy partial-path hints from Jedipedia's file_unnamedOld.js against
  /// filenames already visible in the loaded SWTOR build and candidates produced by PugTools.
  /// The hints contain no invented final names: a candidate is accepted only when SWTOR's
  /// primary/secondary path hash matches the unresolved dictionary entry exactly.
  /// </summary>
  internal static class LegacyUnnamedFileNameResolver {
    private sealed class PatternSpec {
      internal readonly UInt32 Ph;
      internal readonly UInt32 Sh;
      internal readonly String Template;
      internal readonly String Directory;
      internal readonly String LeafPrefix;
      internal readonly String LeafSuffix;

      internal PatternSpec(UInt32 ph, UInt32 sh, String template) {
        Ph = ph;
        Sh = sh;
        Template = template.ToLowerInvariant().Replace('\\', '/');
        Int32 slash = Template.LastIndexOf('/');
        Directory = slash >= 0 ? Template.Substring(0, slash + 1) : String.Empty;
        String leaf = slash >= 0 ? Template.Substring(slash + 1) : Template;
        Int32 wildcard = leaf.IndexOf('?');
        LeafPrefix = wildcard >= 0 ? leaf.Substring(0, wildcard) : leaf;
        LeafSuffix = wildcard >= 0 ? leaf.Substring(wildcard + 1) : String.Empty;
      }

      internal String Build(String token) => Template.Replace("?", token ?? String.Empty);
    }

    private static readonly PatternSpec[] Patterns = new PatternSpec[] {
      new PatternSpec(0xEE18AC62u, 0x98325F5Bu, "/resources/anim/humanoid/bfanew/?.jba"),
      new PatternSpec(0x74253076u, 0xFC6C7C05u, "/resources/anim/humanoid/bfanew/?.jba"),
      new PatternSpec(0x917B1D65u, 0xB469073Du, "/resources/anim/humanoid/bfanew/?.jba"),
      new PatternSpec(0x3F14EABAu, 0x4E668D64u, "/resources/anim/humanoid/bfanew/?.jba"),
      new PatternSpec(0x9E20101Cu, 0xA032A8C0u, "/resources/anim/humanoid/bfanew/?.jba"),
      new PatternSpec(0x9289F878u, 0x7CD341FFu, "/resources/anim/humanoid/bfanew/?.jba"),
      new PatternSpec(0x2A3A1ADBu, 0x8DC53693u, "/resources/anim/humanoid/bfanew/?.jba"),
      new PatternSpec(0xEE94FD92u, 0xEFC7261Eu, "/resources/anim/humanoid/bfanew/?.jba"),
      new PatternSpec(0x0A0D01FEu, 0xB8173FBEu, "/resources/anim/humanoid/bfbnew/?.jba"),
      new PatternSpec(0x38FE78E3u, 0x048B62EDu, "/resources/anim/humanoid/bfbnew/?.jba"),
      new PatternSpec(0x3A8FAFA5u, 0xD09B601Bu, "/resources/anim/humanoid/bfbnew/?.jba"),
      new PatternSpec(0xD56F87C3u, 0x962F3CA3u, "/resources/anim/humanoid/bfnnew/?.jba"),
      new PatternSpec(0x1073A817u, 0xEAD90864u, "/resources/anim/humanoid/bfnnew/?.jba"),
      new PatternSpec(0x99A36C85u, 0x64DFE308u, "/resources/anim/humanoid/bfnnew/?.jba"),
      new PatternSpec(0x8FFC43FEu, 0x835AEBADu, "/resources/anim/humanoid/bfnnew/?.jba"),
      new PatternSpec(0x8D341892u, 0xE1799683u, "/resources/anim/humanoid/bfnnew/?.jba"),
      new PatternSpec(0x113DF787u, 0x2168F9BEu, "/resources/anim/humanoid/bfnnew/?.jba"),
      new PatternSpec(0xFA957DE6u, 0xE532960Fu, "/resources/anim/humanoid/bfnnew/?.jba"),
      new PatternSpec(0x05C111BDu, 0xA3438564u, "/resources/anim/humanoid/bfnnew/?.jba"),
      new PatternSpec(0x7556291Bu, 0x17B1E916u, "/resources/anim/humanoid/bfnnew/?.jba"),
      new PatternSpec(0x2371A9E9u, 0x76B9F419u, "/resources/anim/humanoid/bfnnew/?.jba"),
      new PatternSpec(0xF433AE79u, 0xC0CC4112u, "/resources/anim/humanoid/bmanew/cb_?.jba"),
      new PatternSpec(0xFED576BFu, 0x50083F36u, "/resources/anim/humanoid/bmanew/cb_?.jba"),
      new PatternSpec(0xEA5A9DADu, 0x7BE389DCu, "/resources/anim/humanoid/bmanew/cb_?.jba"),
      new PatternSpec(0x52F28779u, 0x9281A14Du, "/resources/anim/humanoid/bmanew/cb_?.jba"),
      new PatternSpec(0x58D34F1Fu, 0xF28F6A0Cu, "/resources/anim/humanoid/bmanew/?.jba"),
      new PatternSpec(0x97FA315Eu, 0xF82307D2u, "/resources/anim/humanoid/bmanew/?.jba"),
      new PatternSpec(0xA5A078E2u, 0xD0AB47EAu, "/resources/anim/humanoid/bmanew/?.jba"),
      new PatternSpec(0xC215FC52u, 0x630CD794u, "/resources/anim/humanoid/bmfnew/cb_?.jba"),
      new PatternSpec(0xD6E5A545u, 0x9AB798BDu, "/resources/anim/humanoid/bmfnew/?.jba"),
      new PatternSpec(0x2422EE3Bu, 0xADB3C3FAu, "/resources/anim/humanoid/bmfnew/?.jba"),
      new PatternSpec(0x2DFDC763u, 0x99D44DCEu, "/resources/anim/humanoid/bmfnew/?.jba"),
      new PatternSpec(0xF10ED80Eu, 0x4C726E2Eu, "/resources/anim/humanoid/bmfnew/?.jba"),
      new PatternSpec(0x8C7659ECu, 0x4F8A0CB8u, "/resources/anim/humanoid/bmfnew/?.jba"),
      new PatternSpec(0x7BED0012u, 0x59A2AE5Eu, "/resources/anim/humanoid/bmfnew/?.jba"),
      new PatternSpec(0xE1256D90u, 0x235023DEu, "/resources/anim/humanoid/bmfnew/?.jba"),
      new PatternSpec(0xFA73514Bu, 0x63F9EA0Du, "/resources/anim/humanoid/bmfnew/?.jba"),
      new PatternSpec(0xA93B7EC9u, 0x6B894D7Bu, "/resources/anim/humanoid/bmfnew/?.jba"),
      new PatternSpec(0x36ACBD25u, 0x96D935C7u, "/resources/anim/humanoid/bmfnew/?.jba"),
      new PatternSpec(0xCED3B6C1u, 0xCE6E22FFu, "/resources/anim/humanoid/bmfnew/?.jba"),
      new PatternSpec(0x2BB5B09Eu, 0x47C8B836u, "/resources/anim/humanoid/bmfnew/?.jba"),
      new PatternSpec(0x90A7C95Au, 0xD08ECEB5u, "/resources/anim/humanoid/bmfnew/?.jba"),
      new PatternSpec(0xA1EB1596u, 0x69AADA3Eu, "/resources/anim/humanoid/bmfnew/?.jba"),
      new PatternSpec(0x71E9E0E6u, 0x51E7C02Du, "/resources/anim/humanoid/bmfnew/?.jba"),
      new PatternSpec(0x3E9BCE01u, 0xE572F31Au, "/resources/anim/humanoid/bmnnew/a?.jba"),
      new PatternSpec(0x19158B8Fu, 0xE704320Eu, "/resources/anim/humanoid/bmnnew/dg_?.jba"),
      new PatternSpec(0x672DE49Fu, 0x6A6B5414u, "/resources/anim/humanoid/bmnnew/dg_?.jba"),
      new PatternSpec(0x99005CD2u, 0xBC48312Eu, "/resources/anim/humanoid/bmnnew/dg_?.jba"),
      new PatternSpec(0xBCA88CDDu, 0xCF0EE7A1u, "/resources/anim/humanoid/bmnnew/dg_?.jba"),
      new PatternSpec(0x5C02FEA8u, 0xDCA83D98u, "/resources/anim/humanoid/bmnnew/dg_?.jba"),
      new PatternSpec(0xB5E059D3u, 0x3EC657F6u, "/resources/anim/humanoid/bmnnew/dg_?.jba"),
      new PatternSpec(0x78354153u, 0xA820EB22u, "/resources/anim/humanoid/bmnnew/dg_?.jba"),
      new PatternSpec(0xB18F5730u, 0x310D3B56u, "/resources/anim/humanoid/bmnnew/?.jba"),
      new PatternSpec(0xE1D34E89u, 0x70980D5Fu, "/resources/anim/humanoid/bmnnew/?.jba"),
      new PatternSpec(0xB45CE08Au, 0x222C1924u, "/resources/anim/humanoid/bmnnew/?.jba"),
      new PatternSpec(0x1492F341u, 0x4EBF5C25u, "/resources/anim/humanoid/bmsnew/cb_?.jba"),
      new PatternSpec(0x50363203u, 0xA944C49Au, "/resources/anim/humanoid/bmsnew/?.jba"),
      new PatternSpec(0xDDAE5BB2u, 0x3C536717u, "/resources/anim/humanoid/bmsnew/?.jba"),
      new PatternSpec(0xF78633CFu, 0x7AC9CEABu, "/resources/anim/humanoid/bmsnew/?.jba"),
      new PatternSpec(0x4085B8C9u, 0x97983493u, "/resources/anim/humanoid/bmsnew/?.jba"),
      new PatternSpec(0x9AF6434Eu, 0x1ED15637u, "/resources/anim/humanoid/bmsnew/?.jba"),
      new PatternSpec(0x468FC481u, 0x5F181190u, "/resources/anim/placeable/shipneugarbagescow/?.jba"),
      new PatternSpec(0xF6774561u, 0xD6A375E3u, "/resources/anim/creature/terentatek/?.jba"),
      new PatternSpec(0xCCA6B349u, 0xAF196C43u, "/resources/anim/creature/turretneusmall/turret_?.mph"),
      new PatternSpec(0xC42F5FD5u, 0x62C596CAu, "/resources/anim/creature/turretneusmall/turret_?.mph.amx"),
      new PatternSpec(0x1A601D29u, 0xCE848038u, "/resources/anim/creature/turretneusmall/turret_?.mph"),
      new PatternSpec(0xE7C7C764u, 0x120B2DBFu, "/resources/anim/creature/turretneusmall/turret_?.mph.amx"),
      new PatternSpec(0x6E730EAFu, 0xBA1388FAu, "/resources/anim/creature/greebeast?.jba"),
      new PatternSpec(0x4FBBF7D4u, 0x4A3B7F3Eu, "/resources/anim/creature/greebeast?.jba"),
      new PatternSpec(0x1272CCC9u, 0x658131FCu, "/resources/anim/creature/greebeast?.jba"),
      new PatternSpec(0xBE16E189u, 0x37D13D06u, "/resources/anim/creature/greebeastterror?.jba"),
      new PatternSpec(0x14BD2829u, 0x8BB5E533u, "/resources/anim/creature/greebeastterror?.jba"),
      new PatternSpec(0x15B56823u, 0x21CC1FB5u, "/resources/anim/creature/greebeastterror?.jba"),
      new PatternSpec(0xC2BD1BE7u, 0xCF5135DFu, "/resources/art/defaultassets/mi?.tex"),
      new PatternSpec(0x71D80A79u, 0x50FEBF84u, "/resources/art/defaultassets/mi?.tiny.dds"),
      new PatternSpec(0xAD701647u, 0x6A3CE5F2u, "/resources/art/defaultassets/mi?.dds"),
      new PatternSpec(0x5C6AC1B0u, 0x7E181197u, "/resources/art/defaultassets/?.tex"),
      new PatternSpec(0x1BF3008Du, 0x69B04872u, "/resources/art/defaultassets/?.tiny.dds"),
      new PatternSpec(0x1A9E5015u, 0xB5842862u, "/resources/art/defaultassets/?.dds"),
      new PatternSpec(0x7C2FCD7Au, 0x34C50DC8u, "/resources/art/defaultassets/?.tex"),
      new PatternSpec(0x0B1F8A76u, 0xBCEBD48Eu, "/resources/art/defaultassets/?.tiny.dds"),
      new PatternSpec(0xD204AFE9u, 0xC6EA3AFAu, "/resources/art/defaultassets/?.dds"),
      new PatternSpec(0x1137E072u, 0x7EB89DBBu, "/resources/art/defaultassets/?.tex"),
      new PatternSpec(0xEF50F02Cu, 0xE5465288u, "/resources/art/defaultassets/?.tiny.dds"),
      new PatternSpec(0x221DF683u, 0x47073522u, "/resources/art/defaultassets/?.dds"),
      new PatternSpec(0x6F4096D4u, 0x347FF182u, "/resources/art/defaultassets/?.tex"),
      new PatternSpec(0xFD4C2372u, 0x529AD63Au, "/resources/art/defaultassets/?.tiny.dds"),
      new PatternSpec(0x98EF8943u, 0x752B3C29u, "/resources/art/defaultassets/?.dds"),
      new PatternSpec(0xBE85B3E6u, 0xFC08E9C5u, "/resources/world/areas/4611686282649150465/map_?_r.dds"),
      new PatternSpec(0xFCE32EECu, 0x6053E068u, "/resources/world/areas/4611686282649150465/minimaps/map_?_00_00_r.dds"),
      new PatternSpec(0xA1A82C38u, 0x8363282Eu, "/resources/gfx/fonts/?.xml"),
      new PatternSpec(0xBCBEF197u, 0xE1B61A76u, "/resources/gfx/icons/?.dds"),
      new PatternSpec(0x033D318Fu, 0x0AEA2000u, "/resources/gfx/icons/generic?.dds"),
      new PatternSpec(0x5CE2F1AFu, 0x772B3452u, "/resources/gfx/icons/ic?.dds"),
      new PatternSpec(0x2E1901AFu, 0x836E1C15u, "/resources/gfx/icons/ipp.?.chest.dds"),
      new PatternSpec(0x2BB93896u, 0xE455DA6Du, "/resources/gfx/icons/ipp.?.feet.dds"),
      new PatternSpec(0x708E0008u, 0x97D8BB51u, "/resources/gfx/icons/ipp.?.hands.dds"),
      new PatternSpec(0x9C54B744u, 0xFE57F1A4u, "/resources/gfx/icons/ipp.?.head.dds"),
      new PatternSpec(0xF7256730u, 0x432B4A43u, "/resources/gfx/icons/ipp.?.legs.dds"),
      new PatternSpec(0x2251AA33u, 0xA0F0668Eu, "/resources/gfx/icons/ipp.?.waist.dds"),
      new PatternSpec(0x3231F39Eu, 0x6456E4F7u, "/resources/gfx/icons/ipp.?.wrists.dds"),
      new PatternSpec(0x7E95B150u, 0x06135239u, "/resources/gfx/icons/ipp.?.chest.dds"),
      new PatternSpec(0xCCE02B8Cu, 0x5095A902u, "/resources/gfx/icons/ipp.?.feet.dds"),
      new PatternSpec(0x9800756Cu, 0x42480C42u, "/resources/gfx/icons/ipp.?.hands.dds"),
      new PatternSpec(0xCA3D6EFAu, 0xC022965Eu, "/resources/gfx/icons/ipp.?.head.dds"),
      new PatternSpec(0x05E5F660u, 0xEB127187u, "/resources/gfx/icons/ipp.?.legs.dds"),
      new PatternSpec(0x503E7039u, 0x8F27851Fu, "/resources/gfx/icons/ipp.?.waist.dds"),
      new PatternSpec(0x393935A3u, 0x1AABF8CDu, "/resources/gfx/icons/ipp.?.wrists.dds"),
      new PatternSpec(0x3CB89B03u, 0x77F69BD1u, "/resources/gfx/icons/i?.dds"),
      new PatternSpec(0x5418F692u, 0x42084165u, "/resources/gfx/icons/m?_6_0.dds"),
      new PatternSpec(0xCD09D513u, 0x8BF22EDBu, "/resources/gfx/icons/mtx0?.dds"),
      new PatternSpec(0x32AB85E6u, 0x7C9E6D1Fu, "/resources/gfx/icons/mtx_s?.dds"),
      new PatternSpec(0x60B16AE4u, 0xFF74BD3Du, "/resources/gfx/icons/mtx_strongholds_?.dds"),
      new PatternSpec(0xD09A5CC3u, 0xF376F50Eu, "/resources/gfx/icons/t?.dds"),
      new PatternSpec(0x7BDC49F6u, 0xF232A641u, "/resources/gfx/icons/t?.dds"),
      new PatternSpec(0x2EAAAA9Eu, 0x043AE052u, "/resources/gfx/icons/t?.dds"),
      new PatternSpec(0x0C2813D8u, 0x151B6317u, "/resources/gfx/launcher/?.dds"),
      new PatternSpec(0x0E514126u, 0xAE3B09B0u, "/resources/gfx/mtxstore/mtx_s?_260x260.dds"),
      new PatternSpec(0xEF255668u, 0xD1A9DF6Au, "/resources/gfx/mtxstore/mtx_s?_400x400.dds"),
      new PatternSpec(0xD80B2531u, 0x6B3B7C33u, "/resources/gfx/mtxstore/mtx_strongholds_?_120x120.dds"),
      new PatternSpec(0x31BC7A7Bu, 0x962A8D8Fu, "/resources/gfx/mtxstore/mtx_strongholds_?_260x260.dds"),
      new PatternSpec(0xB0F83BB6u, 0x92D62E6Bu, "/resources/gfx/mtxstore/mtx_strongholds_?_260x400.dds"),
      new PatternSpec(0x8D422E2Cu, 0x9373327Du, "/resources/gfx/mtxstore/mtx_strongholds_?_328x160.dds"),
      new PatternSpec(0xBF39C952u, 0xAA7E0ADEu, "/resources/gfx/mtxstore/mtx_strongholds_?_400x400.dds"),
      new PatternSpec(0xEDDF8455u, 0xD99CE9CCu, "/resources/gfx/?.dds"),
      new PatternSpec(0xA91F8129u, 0x65E388ACu, "/resources/gfx/?.dds"),
      new PatternSpec(0xD69EA729u, 0xFE1755D1u, "/resources/gfx/?.dds"),
      new PatternSpec(0x8A148CD3u, 0xEDD60F4Fu, "/resources/gfx/?.dds"),
      new PatternSpec(0x1B68CCAAu, 0x9C37F39Fu, "/resources/gfx/?.dds"),
      new PatternSpec(0xDA7679CFu, 0x6B2836D5u, "/resources/gfx/?.dds"),
      new PatternSpec(0x734EBE33u, 0x196763B1u, "/resources/gfx/?.dds"),
      new PatternSpec(0xDC96EAC5u, 0xBACB314Au, "/resources/gfx/?.dds"),
    };

    private static readonly HashSet<UInt64> PatternSignatures = new HashSet<UInt64>(
      Patterns.Select(x => ((UInt64)x.Ph << 32) | x.Sh)
    );
    private static readonly HashSet<String> PatternDirectories = new HashSet<String>(
      Patterns.Select(x => x.Directory), StringComparer.OrdinalIgnoreCase
    );

    internal static Boolean IsHintHash(UInt64 signature) => PatternSignatures.Contains(signature);

    /// <summary>
    /// Fast pre-filter for the very large Asset Browser name list. The legacy resolver only learns
    /// from sibling paths in one of its hinted directories plus global JBA/MPH/XML stems; passing
    /// unrelated DDS/GR2/etc. names would waste hundreds of thousands of normalizations.
    /// </summary>
    internal static Boolean MayHelpKnownPath(String directory, String fileName) {
      if (String.IsNullOrWhiteSpace(fileName)) return false;
      String dir = NormalizePath((directory ?? String.Empty).TrimEnd('/', '\\') + "/");
      if (PatternDirectories.Contains(dir)) return true;

      return fileName.EndsWith(".jba", StringComparison.OrdinalIgnoreCase)
        || fileName.EndsWith(".mph", StringComparison.OrdinalIgnoreCase)
        || fileName.EndsWith(".mph.amx", StringComparison.OrdinalIgnoreCase)
        || fileName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase);
    }

    internal static List<String> Resolve(HashDictionary dictionary,
                                         IEnumerable<String> knownPaths,
                                         IEnumerable<String> seedCandidates,
                                         ISet<UInt64> hashesPresentInCurrentBuild = null,
                                         IReadOnlyDictionary<UInt64, HashFileInfo> targetFiles = null) {
      var unresolved = new List<PatternSpec>();
      foreach (PatternSpec pattern in Patterns) {
        UInt64 signature = ((UInt64)pattern.Ph << 32) | pattern.Sh;
        HashData data = dictionary.SearchHashList(pattern.Ph, pattern.Sh);
        Boolean presentButNotYetInDictionary = data == null
          && hashesPresentInCurrentBuild != null
          && hashesPresentInCurrentBuild.Contains(signature);
        if (presentButNotYetInDictionary
            || (data != null && String.IsNullOrWhiteSpace(data.FileName))) {
          unresolved.Add(pattern);
        }
      }

      if (unresolved.Count == 0) return new List<String>();

      var byDirectory = unresolved
        .GroupBy(x => x.Directory, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(x => x.Key, x => x.ToList(), StringComparer.OrdinalIgnoreCase);
      var patternTokens = unresolved.ToDictionary(
        x => x,
        x => new HashSet<String>(StringComparer.OrdinalIgnoreCase)
      );

      var globalJbaTokens = new HashSet<String>(StringComparer.OrdinalIgnoreCase);
      var globalMphTokens = new HashSet<String>(StringComparer.OrdinalIgnoreCase);
      var globalXmlTokens = new HashSet<String>(StringComparer.OrdinalIgnoreCase);
      var seedTokens = new HashSet<String>(StringComparer.OrdinalIgnoreCase);

      void Harvest(String value, Boolean fromSeed) {
        String path = NormalizePath(value);
        if (String.IsNullOrEmpty(path)) return;

        Int32 slash = path.LastIndexOf('/');
        String directory = slash >= 0 ? path.Substring(0, slash + 1) : String.Empty;
        String leaf = slash >= 0 ? path.Substring(slash + 1) : path;

        if (byDirectory.TryGetValue(directory, out List<PatternSpec> specs)) {
          foreach (PatternSpec spec in specs) {
            if (!leaf.StartsWith(spec.LeafPrefix, StringComparison.OrdinalIgnoreCase)
                || !leaf.EndsWith(spec.LeafSuffix, StringComparison.OrdinalIgnoreCase)
                || leaf.Length < spec.LeafPrefix.Length + spec.LeafSuffix.Length) continue;

            Int32 tokenLength = leaf.Length - spec.LeafPrefix.Length - spec.LeafSuffix.Length;
            patternTokens[spec].Add(leaf.Substring(spec.LeafPrefix.Length, tokenLength));
          }
        }

        if (leaf.EndsWith(".jba", StringComparison.OrdinalIgnoreCase))
          globalJbaTokens.Add(leaf.Substring(0, leaf.Length - 4));
        else if (leaf.EndsWith(".mph.amx", StringComparison.OrdinalIgnoreCase))
          globalMphTokens.Add(leaf.Substring(0, leaf.Length - 8));
        else if (leaf.EndsWith(".mph", StringComparison.OrdinalIgnoreCase))
          globalMphTokens.Add(leaf.Substring(0, leaf.Length - 4));
        else if (leaf.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
          globalXmlTokens.Add(leaf.Substring(0, leaf.Length - 4));

        if (fromSeed) {
          String stem = StripCompoundExtension(leaf);
          if (!String.IsNullOrWhiteSpace(stem) && stem.Length <= 160) {
            seedTokens.Add(stem);
            foreach (String part in stem.Split(new[] { '_', '-', '.' }, StringSplitOptions.RemoveEmptyEntries))
              if (part.Length >= 2 && part.Length <= 80) seedTokens.Add(part);
          }
        }
      }

      if (knownPaths != null) {
        foreach (String path in knownPaths) Harvest(path, false);
      }
      if (seedCandidates != null) {
        foreach (String path in seedCandidates) Harvest(path, true);
      }

      var resolved = new HashSet<String>(StringComparer.OrdinalIgnoreCase);
      foreach (IGrouping<String, PatternSpec> templateGroup in
               unresolved.GroupBy(x => x.Template, StringComparer.OrdinalIgnoreCase)) {
        List<PatternSpec> group = templateGroup.ToList();
        var targets = new Dictionary<UInt64, PatternSpec>();
        foreach (PatternSpec spec in group) targets[((UInt64)spec.Ph << 32) | spec.Sh] = spec;

        var tokens = new HashSet<String>(StringComparer.OrdinalIgnoreCase);
        foreach (PatternSpec spec in group) tokens.UnionWith(patternTokens[spec]);

        String template = group[0].Template;
        if (template.EndsWith(".jba", StringComparison.OrdinalIgnoreCase))
          tokens.UnionWith(globalJbaTokens);
        else if (template.EndsWith(".mph", StringComparison.OrdinalIgnoreCase)
                 || template.EndsWith(".mph.amx", StringComparison.OrdinalIgnoreCase))
          tokens.UnionWith(globalMphTokens);
        else if (template.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
          tokens.UnionWith(globalXmlTokens);

        // Parser/DOM discoveries are especially valuable because they can contain identifiers that
        // are not present as filenames anywhere else in the current hash dictionary.
        tokens.UnionWith(seedTokens);
        tokens.Add(String.Empty);

        // Old AMX/MPH/JBA assets often retain authored identifiers or source names even when the
        // final cooked filename itself was stripped. Scan only the small set of unresolved legacy
        // hint targets, and keep the scan bounded so Filename Finder never becomes another full
        // extraction pass. Exact PH+SH matching below remains the authority.
        if (targetFiles != null) {
          foreach (PatternSpec spec in group) {
            UInt64 signature = ((UInt64)spec.Ph << 32) | spec.Sh;
            if (targetFiles.TryGetValue(signature, out HashFileInfo info))
              AddPrintableContentTokens(info, tokens, 120000);
          }
        }
        AddNumericFilenameVariations(tokens, 120000);

        foreach (String token in tokens) {
          String candidate = group[0].Build(token);
          FileId id = FileId.FromFilePath(candidate);
          UInt64 signature = ((UInt64)id.Ph << 32) | id.Sh;
          if (!targets.ContainsKey(signature)) continue;
          resolved.Add(candidate);
          targets.Remove(signature);
          if (targets.Count == 0) break;
        }
      }

      return resolved.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Looks for a file's own resource path inside small unresolved cooked files. Several generated
    /// SWTOR formats retain this string in their payload even though the archive directory stores
    /// only PH+SH. Work is intentionally bounded and no string is returned unless its normalized
    /// path reproduces the containing file's exact hash.
    /// </summary>
    internal static List<String> ResolveEmbeddedPaths(
      IReadOnlyDictionary<UInt64, List<HashFileInfo>> targetFiles) {
      var resolved = new HashSet<String>(StringComparer.OrdinalIgnoreCase);
      if (targetFiles == null || targetFiles.Count == 0) return resolved.ToList();

      const Int32 maxFiles = 768;
      Int32 scanned = 0;

      // Filter first, then sort only text-bearing candidates. Sorting every unresolved asset (which
      // can be hundreds of thousands of DDS/GR2/etc. files in a fresh build) just to scan 768 small
      // metadata files wastes a large amount of CPU and temporary memory.
      var scanTargets = new List<KeyValuePair<UInt64, HashFileInfo>>();
      foreach (KeyValuePair<UInt64, List<HashFileInfo>> pair in targetFiles) {
        if (pair.Value == null || pair.Value.Count == 0) continue;
        HashFileInfo info = pair.Value.FirstOrDefault(x => ShouldScanForEmbeddedPath(x));
        if (info != null) scanTargets.Add(new KeyValuePair<UInt64, HashFileInfo>(pair.Key, info));
      }

      foreach (KeyValuePair<UInt64, HashFileInfo> pair in scanTargets
               .OrderBy(x => x.Value?.File?.FileInfo?.UncompressedSize ?? UInt32.MaxValue)
               .Take(maxFiles)) {
        scanned++;
        if (TryFindEmbeddedPath(pair.Value, pair.Key, out String candidate)) resolved.Add(candidate);
      }

      return resolved.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static Boolean ShouldScanForEmbeddedPath(HashFileInfo info) {
      if (info?.File?.FileInfo == null) return false;
      UInt32 size = info.File.FileInfo.UncompressedSize;
      if (size < 8 || size > 8 * 1024 * 1024) return false;

      String ext = (info.Extension ?? String.Empty).TrimStart('.').ToLowerInvariant();
      switch (ext) {
        case "dat":
        case "xml":
        case "txt":
        case "zero.txt":
        case "amx":
        case "mph":
        case "mat":
        case "tex":
        case "fxspec":
        case "epp":
        case "prt":
        case "dyn":
        case "hyd":
        case "plc":
        case "cnv":
        case "list":
        case "info":
        case "stb":
        case "gom":
        case "gr2":
        case "unknown":
        case "":
          return true;
        default:
          return false;
      }
    }

    private static Boolean TryFindEmbeddedPath(HashFileInfo info,
                                                UInt64 targetSignature,
                                                out String match) {
      match = null;
      String foundMatch = null;
      const Int32 maxBytes = 2 * 1024 * 1024;
      const Int32 maxSequences = 8192;
      Byte[] buffer = new Byte[64 * 1024];
      Int32 readTotal = 0;
      Int32 sequences = 0;
      var current = new StringBuilder(512);

      Boolean TestSequence() {
        if (current.Length < 6 || sequences >= maxSequences) {
          current.Clear();
          return false;
        }

        String raw = current.ToString();
        current.Clear();
        sequences++;

        foreach (String candidate in EmbeddedPathCandidates(raw)) {
          FileId id = FileId.FromFilePath(candidate);
          UInt64 signature = ((UInt64)id.Ph << 32) | id.Sh;
          if (signature != targetSignature) continue;
          foundMatch = candidate;
          return true;
        }
        return false;
      }

      try {
        using Stream stream = info.File.Open();
        while (readTotal < maxBytes && sequences < maxSequences) {
          Int32 wanted = Math.Min(buffer.Length, maxBytes - readTotal);
          Int32 read = stream.Read(buffer, 0, wanted);
          if (read <= 0) break;
          readTotal += read;

          for (Int32 i = 0; i < read; i++) {
            Char c = (Char)buffer[i];
            // Resource paths use a conservative ASCII subset. A delimiter flushes the current
            // sequence; this also prevents surrounding binary bytes from becoming part of a path.
            Boolean allowed = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')
              || (c >= '0' && c <= '9') || c == '_' || c == '-' || c == '.'
              || c == '/' || c == '\\';
            if (allowed && current.Length < 1024) current.Append(c);
            else if (TestSequence()) { match = foundMatch; return true; }
          }
        }
        if (TestSequence()) { match = foundMatch; return true; }
      }
      catch (Exception ex) when (ex is IOException || ex is InvalidDataException
                                 || ex is ObjectDisposedException || ex is ArgumentException) {
        Debug.WriteLine("Embedded filename path scan failed: " + ex.Message);
      }
      return false;
    }

    private static IEnumerable<String> EmbeddedPathCandidates(String raw) {
      var result = new HashSet<String>(StringComparer.OrdinalIgnoreCase);
      if (String.IsNullOrWhiteSpace(raw)) return result;

      String value = raw.Replace('\\', '/').ToLowerInvariant();
      while (value.Contains("//")) value = value.Replace("//", "/");

      void Add(String candidate) {
        String normalized = NormalizePath(candidate);
        if (String.IsNullOrWhiteSpace(normalized) || normalized.Contains('?')) return;
        if (!normalized.StartsWith("/resources/", StringComparison.Ordinal)) return;
        if (normalized.Length > 512) return;
        result.Add(normalized);
      }

      Int32 resources = value.IndexOf("/resources/", StringComparison.Ordinal);
      if (resources >= 0) Add(value.Substring(resources));
      resources = value.IndexOf("resources/", StringComparison.Ordinal);
      if (resources >= 0) Add("/" + value.Substring(resources));

      // Cooked files frequently omit the literal /resources prefix and store /world/..., /gfx/...
      // etc. Try path suffixes beginning at slash boundaries; exact PH+SH verification below makes
      // this generic normalization safe without a hard-coded list of resource root folders.
      for (Int32 slash = value.IndexOf('/'); slash >= 0; slash = value.IndexOf('/', slash + 1)) {
        if (slash + 1 >= value.Length) continue;
        String suffix = value.Substring(slash);
        if (suffix.StartsWith("/resources/", StringComparison.Ordinal)) Add(suffix);
        else Add("/resources" + suffix);
      }

      if (!value.Contains('/')) return result;
      if (!value.StartsWith("/", StringComparison.Ordinal)
          && !value.StartsWith("resources/", StringComparison.Ordinal))
        Add("/resources/" + value.TrimStart('/'));

      return result;
    }

    /// <summary>
    /// Expands cheap, structurally-related filename candidates before TOR validation. These are
    /// probes only; callers must still require an exact current-build hash hit before persisting.
    /// </summary>
    internal static IEnumerable<String> ExpandCandidates(IEnumerable<String> input) {
      var candidates = new HashSet<String>(StringComparer.OrdinalIgnoreCase);
      String[] bodyTypes = { "bfa", "bfb", "bfn", "bfs", "bma", "bmf", "bmn", "bms" };
      String[] mtxResolutions = { "120x120", "260x260", "260x400", "328x160", "400x400" };

      Boolean LooksLikeResolution(String value) {
        if (String.IsNullOrWhiteSpace(value)) return false;
        Int32 x = value.IndexOf('x');
        if (x <= 0 || x >= value.Length - 1) return false;
        for (Int32 i = 0; i < value.Length; i++) {
          if (i == x) continue;
          if (!Char.IsDigit(value[i])) return false;
        }
        return true;
      }

      if (input == null) return candidates;
      foreach (String raw in input) {
        String line = NormalizePath(raw);
        if (String.IsNullOrWhiteSpace(line) || line.Contains('?')) continue;
        candidates.Add(line);

        if (line.EndsWith(".mph", StringComparison.OrdinalIgnoreCase))
          candidates.Add(line + ".amx");
        else if (line.EndsWith(".mph.amx", StringComparison.OrdinalIgnoreCase))
          candidates.Add(line.Substring(0, line.Length - 4));

        if (line.EndsWith(".tex", StringComparison.OrdinalIgnoreCase)) {
          String stem = line.Substring(0, line.Length - 4);
          candidates.Add(stem + ".dds");
          candidates.Add(stem + ".tiny.dds");
        } else if (line.EndsWith(".tiny.dds", StringComparison.OrdinalIgnoreCase)) {
          String stem = line.Substring(0, line.Length - 9);
          candidates.Add(stem + ".tex");
          candidates.Add(stem + ".dds");
        } else if (line.EndsWith(".dds", StringComparison.OrdinalIgnoreCase)) {
          String stem = line.Substring(0, line.Length - 4);
          candidates.Add(stem + ".tex");
          candidates.Add(stem + ".tiny.dds");

          // Jedipedia's findMtxStoreNames: Cartel Market/store art is commonly cooked at a
          // fixed family of dimensions. Once one member is known, probe the sibling sizes and
          // the equivalent /gfx/icons leaf. These remain hash-verified probes only.
          if (stem.StartsWith("/resources/gfx/mtxstore/", StringComparison.OrdinalIgnoreCase)) {
            Int32 underscore = stem.LastIndexOf('_');
            if (underscore > "/resources/gfx/mtxstore/".Length
                && LooksLikeResolution(stem.Substring(underscore + 1))) {
              String root = stem.Substring(0, underscore);
              foreach (String resolution in mtxResolutions)
                candidates.Add(root + "_" + resolution + ".dds");
              candidates.Add(root.Replace("/gfx/mtxstore/", "/gfx/icons/", StringComparison.OrdinalIgnoreCase) + ".dds");
            }
          }
        }

        foreach (String body in bodyTypes) {
          String marker = "/" + body + "new/";
          Int32 pos = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
          if (pos < 0) continue;
          foreach (String replacement in bodyTypes) {
            candidates.Add(line.Substring(0, pos) + "/" + replacement + "new/"
              + line.Substring(pos + marker.Length));
          }
          break;
        }
      }
      return candidates;
    }

    private static void AddPrintableContentTokens(HashFileInfo info,
                                                   HashSet<String> tokens,
                                                   Int32 maxCount) {
      if (info?.File?.FileInfo == null || tokens.Count >= maxCount) return;
      const Int32 maxBytes = 2 * 1024 * 1024;
      const UInt32 maxFileSize = 8 * 1024 * 1024;
      const Int32 maxSequences = 4096;
      if (info.File.FileInfo.UncompressedSize > maxFileSize) return;

      Byte[] buffer = new Byte[64 * 1024];
      Int32 readTotal = 0;
      Int32 sequences = 0;
      var current = new StringBuilder(192);

      void FlushToken() {
        if (current.Length < 3 || current.Length > 180 || sequences >= maxSequences) {
          current.Clear();
          return;
        }

        String raw = current.ToString();
        current.Clear();
        sequences++;
        String normalized = raw.Replace('\\', '/').Trim('/');
        Int32 slash = normalized.LastIndexOf('/');
        if (slash >= 0) normalized = normalized.Substring(slash + 1);
        String stem = StripCompoundExtension(normalized);
        AddToken(stem, tokens, maxCount);

        String snake = IdentifierToSnakeCase(stem);
        AddToken(snake, tokens, maxCount);
        if (snake.StartsWith("cre_", StringComparison.OrdinalIgnoreCase))
          AddToken(snake.Substring(4), tokens, maxCount);

        foreach (String part in snake.Split(new[] { '_', '-', '.' }, StringSplitOptions.RemoveEmptyEntries))
          AddToken(part, tokens, maxCount);
      }

      try {
        using Stream stream = info.File.Open();
        while (readTotal < maxBytes && sequences < maxSequences && tokens.Count < maxCount) {
          Int32 wanted = Math.Min(buffer.Length, maxBytes - readTotal);
          Int32 read = stream.Read(buffer, 0, wanted);
          if (read <= 0) break;
          readTotal += read;

          for (Int32 i = 0; i < read; i++) {
            Char c = (Char)buffer[i];
            Boolean allowed = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')
              || (c >= '0' && c <= '9') || c == '_' || c == '-' || c == '.'
              || c == '/' || c == '\\' || c == '|';
            if (allowed && current.Length < 181) current.Append(c);
            else FlushToken();
          }
        }
        FlushToken();
      }
      catch (Exception ex) when (ex is IOException || ex is InvalidDataException
                                 || ex is ObjectDisposedException) {
        Debug.WriteLine("Legacy filename hint content scan failed: " + ex.Message);
      }
    }

    private static void AddToken(String value, HashSet<String> tokens, Int32 maxCount) {
      if (tokens.Count >= maxCount || value == null) return;
      String token = value.Trim().Trim('"', '\'', '\0').Replace('\\', '/').ToLowerInvariant();
      if (token.Length <= 180 && !token.Contains('/')) tokens.Add(token);
    }

    private static String IdentifierToSnakeCase(String value) {
      if (String.IsNullOrWhiteSpace(value)) return String.Empty;
      var result = new StringBuilder(value.Length + 8);
      Char previous = '\0';
      foreach (Char c in value) {
        if (Char.IsUpper(c) && result.Length > 0
            && (Char.IsLower(previous) || Char.IsDigit(previous))
            && result[result.Length - 1] != '_') result.Append('_');
        result.Append(Char.ToLowerInvariant(c));
        previous = c;
      }
      return result.ToString();
    }

    private static void AddNumericFilenameVariations(HashSet<String> tokens, Int32 maxCount) {
      if (tokens.Count >= maxCount) return;
      String[] source = tokens.ToArray();
      var groups = new Dictionary<String, NumericTokenGroup>(StringComparer.OrdinalIgnoreCase);

      foreach (String token in source) {
        if (String.IsNullOrEmpty(token)) continue;
        Int32 end = -1;
        for (Int32 i = token.Length - 1; i >= 0; i--) {
          if (Char.IsDigit(token[i])) { end = i; break; }
        }
        if (end < 0) continue;
        Int32 start = end;
        while (start > 0 && Char.IsDigit(token[start - 1])) start--;
        Int32 width = end - start + 1;
        if (width > 9 || !Int32.TryParse(token.Substring(start, width), out Int32 number)) continue;
        String prefix = token.Substring(0, start);
        String suffix = token.Substring(end + 1);
        String key = prefix + "\u0001" + suffix + "\u0001" + width.ToString(CultureInfo.InvariantCulture);
        if (!groups.TryGetValue(key, out NumericTokenGroup group)) {
          group = new NumericTokenGroup(prefix, suffix, width, number);
          groups.Add(key, group);
        } else group.Add(number);
      }

      foreach (NumericTokenGroup group in groups.Values) {
        if (tokens.Count >= maxCount) break;
        Int32 low = Math.Max(0, group.Min - 4);
        Int32 high = group.Max + 4;
        if (group.Count < 2 && group.Prefix.IndexOf("autogen", StringComparison.OrdinalIgnoreCase) < 0)
          continue;
        if (high - low + 1 > 4096) high = low + 4095;
        for (Int32 value = low; value <= high && tokens.Count < maxCount; value++) {
          String digits = value.ToString("D" + group.Width.ToString(CultureInfo.InvariantCulture),
                                         CultureInfo.InvariantCulture);
          AddToken(group.Prefix + digits + group.Suffix, tokens, maxCount);
        }
      }
    }

    private sealed class NumericTokenGroup {
      internal readonly String Prefix;
      internal readonly String Suffix;
      internal readonly Int32 Width;
      internal Int32 Min;
      internal Int32 Max;
      internal Int32 Count;

      internal NumericTokenGroup(String prefix, String suffix, Int32 width, Int32 value) {
        Prefix = prefix; Suffix = suffix; Width = width; Min = value; Max = value; Count = 1;
      }
      internal void Add(Int32 value) {
        if (value < Min) Min = value;
        if (value > Max) Max = value;
        Count++;
      }
    }

    private static String NormalizePath(String value) {
      if (String.IsNullOrWhiteSpace(value)) return String.Empty;
      String path = value.Trim().Trim('"', '\'').Replace('\\', '/').ToLowerInvariant();
      while (path.Contains("//")) path = path.Replace("//", "/");
      if (path.StartsWith("resources/", StringComparison.Ordinal)) path = "/" + path;
      if (path.StartsWith("/world/", StringComparison.Ordinal)) path = "/resources" + path;
      return path;
    }

    private static String StripCompoundExtension(String leaf) {
      if (leaf.EndsWith(".tiny.dds", StringComparison.OrdinalIgnoreCase))
        return leaf.Substring(0, leaf.Length - 9);
      if (leaf.EndsWith(".mph.amx", StringComparison.OrdinalIgnoreCase))
        return leaf.Substring(0, leaf.Length - 8);
      Int32 dot = leaf.LastIndexOf('.');
      return dot > 0 ? leaf.Substring(0, dot) : leaf;
    }
  }
}
