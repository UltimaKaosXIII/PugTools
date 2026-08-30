using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace PugTools {
  /// <summary>Structured reader for FaceFX animation-set (*.fxe) files.</summary>
  internal static class ViewFXE {
    internal sealed class Key {
      internal Single Time;
      internal Single Value;
      internal Single SlopeIn;
      internal Single SlopeOut;
    }
    internal sealed class Curve {
      internal String Name = String.Empty;
      internal readonly List<Key> Keys = new List<Key>();
    }
    internal sealed class Animation {
      internal String Name = String.Empty;
      internal readonly List<Curve> Curves = new List<Curve>();
      internal Single BlendInTime;
      internal Single BlendOutTime;
    }
    internal sealed class FxeInfo {
      internal ViewFaceFX.HeaderInfo Header;
      internal String Name = String.Empty;
      internal String OwningActor = String.Empty;
      internal readonly List<Animation> Animations = new List<Animation>();
      internal Int64 TrailingBytes;
    }

    internal static FxeInfo Parse(Stream input) {
      if (input == null) throw new ArgumentNullException(nameof(input));
      input.Position = 0;
      using BinaryReader br = new BinaryReader(input, Encoding.UTF8, true);
      ViewFaceFX.HeaderInfo header = ViewFaceFX.ReadHeader(br);
      FxeInfo info = new FxeInfo {
        Header = header,
        Name = header.Strings.Count > 0 ? header.Strings[0] : String.Empty
      };

      UInt32 actorIndex = br.ReadUInt32();
      info.OwningActor = ViewFaceFX.StringAt(header, actorIndex, "FXE owning actor");
      ViewFaceFX.ExpectZero(br.ReadUInt32(), "FXE owning actor marker");
      UInt32 numAnimations = br.ReadUInt32();
      if (numAnimations > 100000) throw new InvalidDataException("FXE has an unreasonable animation count: " + numAnimations + ".");

      for (UInt32 animIndex = 0; animIndex < numAnimations; animIndex++) {
        Animation anim = new Animation {
          Name = ViewFaceFX.StringAt(header, br.ReadUInt32(), "FXE animation")
        };
        UInt32 numCurveNames = br.ReadUInt32();
        if (numCurveNames > 100000) throw new InvalidDataException("FXE animation has an unreasonable curve count: " + numCurveNames + ".");
        List<String> curveNames = new List<String>(checked((Int32)numCurveNames));
        for (UInt32 curveIndex = 0; curveIndex < numCurveNames; curveIndex++) {
          curveNames.Add(ViewFaceFX.StringAt(header, br.ReadUInt32(), "FXE curve"));
          UInt32 interpolation = br.ReadUInt32();
          // 0 = Hermite, 1 = linear. SWTOR's shipped conversation sets are normally Hermite, but Jedipedia only
          // asserts that fact and keeps reading. The binary shape is identical for both values.
          if (interpolation > 1) System.Diagnostics.Debug.WriteLine("Unexpected FXE interpolation type " + interpolation + ".");
        }

        UInt32 numKeys = br.ReadUInt32();
        if (numKeys > 10000000) throw new InvalidDataException("FXE animation has an unreasonable key count: " + numKeys + ".");
        List<Key> keys = new List<Key>(checked((Int32)numKeys));
        for (UInt32 keyIndex = 0; keyIndex < numKeys; keyIndex++) {
          keys.Add(new Key {
            Time = br.ReadSingle(),
            Value = br.ReadSingle(),
            SlopeIn = br.ReadSingle(),
            SlopeOut = br.ReadSingle()
          });
        }

        if (numKeys > 0) {
          UInt32 mappingCount = br.ReadUInt32();
          if (mappingCount != numCurveNames)
            System.Diagnostics.Debug.WriteLine("FXE curve mapping count " + mappingCount + " differs from curve-name count " + numCurveNames + ".");
          if (mappingCount > 100000) throw new InvalidDataException("FXE has an unreasonable curve mapping count: " + mappingCount + ".");
          Int32 dataPos = 0;
          for (UInt32 curveIndex = 0; curveIndex < mappingCount; curveIndex++) {
            UInt32 rows = br.ReadUInt32();
            if (rows > Int32.MaxValue) throw new InvalidDataException("FXE curve row count is too large.");
            Int32 available = Math.Max(0, keys.Count - dataPos);
            Int32 take = Math.Min(available, (Int32)rows);
            String curveName = curveIndex < (UInt32)curveNames.Count ? curveNames[(Int32)curveIndex] : "curve_" + curveIndex;
            Curve curve = new Curve { Name = curveName };
            for (Int32 k = 0; k < take; k++) curve.Keys.Add(keys[dataPos + k]);
            dataPos += take;
            anim.Curves.Add(curve);
            if (take != (Int32)rows) System.Diagnostics.Debug.WriteLine("FXE curve mapping exceeds the available key array.");
          }
          if (dataPos != keys.Count) System.Diagnostics.Debug.WriteLine("FXE curve mappings leave " + (keys.Count - dataPos) + " unassigned keys.");
        } else {
          for (Int32 curveIndex = 0; curveIndex < curveNames.Count; curveIndex++)
            anim.Curves.Add(new Curve { Name = curveNames[curveIndex] });
        }

        anim.BlendInTime = br.ReadSingle();
        anim.BlendOutTime = br.ReadSingle();
        ViewFaceFX.ExpectZero(br.ReadUInt32(), "FXE bone-weight count");
        ViewFaceFX.ExpectZero(br.ReadUInt32(), "FXE sound-cue path");
        ViewFaceFX.ExpectZero(br.ReadUInt32(), "FXE sound-node wave");
        Int32 soundCueIndex = br.ReadInt32();
        if (soundCueIndex != -1) System.Diagnostics.Debug.WriteLine("Unexpected FXE sound cue index " + soundCueIndex + ".");
        info.Animations.Add(anim);
      }

      // Keep a structured result even when a legacy writer appended bytes unknown to this reader. Jedipedia reports
      // the mismatch as an assertion and still exposes the animation set; HEX is available separately if needed.
      info.TrailingBytes = Math.Max(0, input.Length - input.Position);
      return info;
    }

    internal static ArrayList BuildTree(FxeInfo info) {
      ArrayList roots = new ArrayList();
      if (info == null) return roots;
      NodeListItem header = new NodeListItem("FaceFX animation set", info.Name);
      header.children.Add(new NodeListItem("Owning actor", info.OwningActor));
      header.children.Add(new NodeListItem("SDK version", info.Header?.SdkVersion ?? 0));
      header.children.Add(new NodeListItem("Animations", info.Animations.Count));
      if (info.TrailingBytes > 0) header.children.Add(new NodeListItem("Unparsed trailing bytes", info.TrailingBytes));
      roots.Add(header);

      NodeListItem animations = new NodeListItem("Animations", info.Animations.Count + " entries");
      for (Int32 i = 0; i < info.Animations.Count; i++) {
        Animation anim = info.Animations[i];
        GetAnimationRange(anim, out Single start, out Single end);
        String range = end > start ? String.Format(CultureInfo.InvariantCulture, ", {0:0.###}s keys", end - start) : String.Empty;
        NodeListItem animNode = new NodeListItem("#" + i + " " + anim.Name,
          String.Format(CultureInfo.InvariantCulture, "{0} curves{1}, blend {2:0.###}/{3:0.###}s", anim.Curves.Count, range, anim.BlendInTime, anim.BlendOutTime));
        if (end > start) animNode.children.Add(new NodeListItem("Key range", String.Format(CultureInfo.InvariantCulture, "{0:0.######} .. {1:0.######} s", start, end)));
        animNode.children.Add(new NodeListItem("Blend in", anim.BlendInTime.ToString("0.######", CultureInfo.InvariantCulture) + " s"));
        animNode.children.Add(new NodeListItem("Blend out", anim.BlendOutTime.ToString("0.######", CultureInfo.InvariantCulture) + " s"));
        NodeListItem curves = new NodeListItem("Curves", anim.Curves.Count + " entries");
        foreach (Curve curve in anim.Curves) {
          NodeListItem curveNode = new NodeListItem(curve.Name, curve.Keys.Count + " Hermite keys");
          for (Int32 k = 0; k < curve.Keys.Count; k++) {
            Key key = curve.Keys[k];
            curveNode.children.Add(new NodeListItem("#" + k,
              String.Format(CultureInfo.InvariantCulture, "t {0:0.######}  value {1:0.######}  slope {2:0.######}/{3:0.######}",
                key.Time, key.Value, key.SlopeIn, key.SlopeOut)));
          }
          curves.children.Add(curveNode);
        }
        animNode.children.Add(curves);
        animations.children.Add(animNode);
      }
      roots.Add(animations);

      if (info.Header != null) {
        NodeListItem archive = new NodeListItem("FaceFX archive", info.Header.Strings.Count + " strings, " + info.Header.ClassVersions.Count + " class versions");
        archive.children.Add(new NodeListItem("Licensee", info.Header.LicenseeName));
        archive.children.Add(new NodeListItem("Project", info.Header.LicenseeProjectName));
        NodeListItem versions = new NodeListItem("Class versions", info.Header.ClassVersions.Count + " entries");
        foreach (KeyValuePair<String, UInt16> pair in info.Header.ClassVersions) versions.children.Add(new NodeListItem(pair.Key, pair.Value));
        archive.children.Add(versions);
        NodeListItem strings = new NodeListItem("String table", info.Header.Strings.Count + " entries");
        for (Int32 i = 0; i < info.Header.Strings.Count; i++) strings.children.Add(new NodeListItem("#" + i, info.Header.Strings[i]));
        archive.children.Add(strings);
        roots.Add(archive);
      }
      return roots;
    }

    private static void GetAnimationRange(Animation animation, out Single start, out Single end) {
      start = Single.MaxValue;
      end = Single.MinValue;
      foreach (Curve curve in animation.Curves) {
        foreach (Key key in curve.Keys) {
          if (key.Time < start) start = key.Time;
          if (key.Time > end) end = key.Time;
        }
      }
      if (start == Single.MaxValue) { start = 0f; end = 0f; }
    }
  }
}
