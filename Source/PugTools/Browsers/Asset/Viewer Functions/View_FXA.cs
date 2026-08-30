using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace PugTools {
  /// <summary>Structured reader for SWTOR FaceFX actor (*.fxa) files.</summary>
  internal static class ViewFXA {
    internal sealed class PoseNode {
      internal UInt32 NodeIndex;
      internal Single[] Position;
      internal Single[] Rotation;
      internal Single[] Scale;
    }
    internal sealed class Bone {
      internal String Name = String.Empty;
      internal Single[] Position;
      internal Single[] Rotation;
      internal Single[] Scale;
      internal Single[] InverseRotation;
      internal UInt32 ClientIndex;
      internal Single ReferenceWeight;
      internal readonly List<PoseNode> PoseNodes = new List<PoseNode>();
    }
    internal sealed class FaceLink {
      internal UInt32 NodeIndex;
      internal UInt32 FunctionType;
      internal readonly List<Single> Parameters = new List<Single>();
    }
    internal sealed class FaceNode {
      internal UInt32 NodeType;
      internal String Name = String.Empty;
      internal Single Min;
      internal Single Max;
      internal UInt32 InputOperation;
      internal readonly List<FaceLink> InputLinks = new List<FaceLink>();
    }
    internal sealed class PhonemeMapping {
      internal UInt32 PhonemeIndex;
      internal String Target = String.Empty;
      internal Single Amount;
    }
    internal sealed class AnimationGroup {
      internal String Name = String.Empty;
      internal readonly List<ViewFXE.Animation> Animations = new List<ViewFXE.Animation>();
    }
    internal sealed class FxaInfo {
      internal ViewFaceFX.HeaderInfo Header;
      internal String Name = String.Empty;
      internal String RootBone = String.Empty;
      internal readonly List<Bone> Bones = new List<Bone>();
      internal readonly List<FaceNode> FaceGraph = new List<FaceNode>();
      internal UInt32 NodeHashBucketCount;
      internal readonly List<ViewFXE.Animation> DefaultAnimations = new List<ViewFXE.Animation>();
      internal readonly List<AnimationGroup> AnimationGroups = new List<AnimationGroup>();
      internal readonly List<PhonemeMapping> PhonemeMappings = new List<PhonemeMapping>();
      internal readonly List<String> PhonemeTargetNames = new List<String>();
    }

    internal static FxaInfo Parse(Stream input) {
      if (input == null) throw new ArgumentNullException(nameof(input));
      input.Position = 0;
      using BinaryReader br = new BinaryReader(input, Encoding.UTF8, true);
      ViewFaceFX.HeaderInfo header = ViewFaceFX.ReadHeader(br);
      FxaInfo info = new FxaInfo {
        Header = header,
        Name = header.Strings.Count > 0 ? header.Strings[0] : String.Empty
      };

      UInt32 boneCount = br.ReadUInt32();
      if (boneCount > 100000) throw new InvalidDataException("FXA has an unreasonable bone count: " + boneCount + ".");
      for (UInt32 i = 0; i < boneCount; i++) {
        UInt32 nameIndex = br.ReadUInt32();
        Bone bone = new Bone {
          Name = ViewFaceFX.StringAt(header, nameIndex, "FXA bone"),
          Position = ReadVec3(br),
          Rotation = ReadFaceQuat(br),
          Scale = ReadVec3(br),
          InverseRotation = ReadFaceQuat(br),
          ClientIndex = br.ReadUInt32(),
          ReferenceWeight = br.ReadSingle()
        };
        CheckInverseQuaternion(bone.Rotation, bone.InverseRotation, bone.Name);
        if (bone.ReferenceWeight != 0f && bone.ReferenceWeight != 1f)
          throw new InvalidDataException("FXA bone " + bone.Name + " has unexpected reference weight " + bone.ReferenceWeight + ".");
        if (bone.ReferenceWeight == 0f) {
          if (!String.IsNullOrEmpty(info.RootBone))
            throw new InvalidDataException("FXA contains more than one root/reference-weight-zero bone.");
          info.RootBone = bone.Name;
        }

        UInt32 poseCount = br.ReadUInt32();
        if (poseCount > 100000) throw new InvalidDataException("FXA bone has an unreasonable pose-node count: " + poseCount + ".");
        for (UInt32 j = 0; j < poseCount; j++) {
          PoseNode pose = new PoseNode { NodeIndex = br.ReadUInt32() };
          UInt32 repeatedNameIndex = br.ReadUInt32();
          if (repeatedNameIndex != nameIndex)
            throw new InvalidDataException("FXA pose-node bone index does not match its master bone.");
          pose.Position = ReadVec3(br);
          pose.Rotation = ReadFaceQuat(br);
          pose.Scale = ReadVec3(br);
          bone.PoseNodes.Add(pose);
        }
        info.Bones.Add(bone);
      }
      if (boneCount > 0 && String.IsNullOrEmpty(info.RootBone))
        throw new InvalidDataException("FXA master bone list has no reference-weight-zero root bone.");

      UInt32 graphCount = br.ReadUInt32();
      if (graphCount > 1000000) throw new InvalidDataException("FXA has an unreasonable face-graph node count: " + graphCount + ".");
      for (UInt32 i = 0; i < graphCount; i++) {
        FaceNode node = new FaceNode {
          NodeType = br.ReadUInt32(),
          Name = ViewFaceFX.StringAt(header, br.ReadUInt32(), "FXA face-graph node"),
          Min = br.ReadSingle()
        };
        Single inverseMin = br.ReadSingle();
        node.Max = br.ReadSingle();
        Single inverseMax = br.ReadSingle();
        node.InputOperation = br.ReadUInt32();
        CheckStoredInverse(node.Min, inverseMin, "node minimum", node.Name);
        CheckStoredInverse(node.Max, inverseMax, "node maximum", node.Name);

        UInt32 linkCount = br.ReadUInt32();
        if (linkCount > 1000000) throw new InvalidDataException("FXA face-graph node has an unreasonable link count.");
        for (UInt32 j = 0; j < linkCount; j++) {
          FaceLink link = new FaceLink {
            NodeIndex = br.ReadUInt32(),
            FunctionType = br.ReadUInt32()
          };
          UInt32 parameterCount = br.ReadUInt32();
          if (parameterCount > 1024) throw new InvalidDataException("FXA face link has an unreasonable parameter count.");
          for (UInt32 k = 0; k < parameterCount; k++) link.Parameters.Add(br.ReadSingle());
          node.InputLinks.Add(link);
        }
        UInt32 userProperties = br.ReadUInt32();
        if (userProperties != 0) throw new InvalidDataException("FXA face-graph user properties are not supported (count " + userProperties + ").");
        info.FaceGraph.Add(node);
      }

      UInt32 nodeHashCount = br.ReadUInt32();
      info.NodeHashBucketCount = nodeHashCount;
      if (nodeHashCount > 100000) throw new InvalidDataException("FXA node hash has an unreasonable bucket count.");
      for (UInt32 i = 0; i < nodeHashCount; i++) {
        br.ReadUInt32(); // serialized bucket index
        UInt32 nameCount = br.ReadUInt32();
        if (nameCount > 1000000) throw new InvalidDataException("FXA node-hash bucket has an unreasonable name count.");
        for (UInt32 j = 0; j < nameCount; j++) {
          ViewFaceFX.ReadString32(br);
          br.ReadUInt32(); // node index
        }
      }

      String defaultGroup = ViewFaceFX.StringAt(header, br.ReadUInt32(), "FXA default animation group");
      if (!String.Equals(defaultGroup, "Default", StringComparison.Ordinal))
        throw new InvalidDataException("FXA default animation group is \"" + defaultGroup + "\", expected \"Default\".");
      UInt32 defaultAnimCount = br.ReadUInt32();
      if (defaultAnimCount > 100000) throw new InvalidDataException("FXA has an unreasonable default-animation count.");
      for (UInt32 i = 0; i < defaultAnimCount; i++) info.DefaultAnimations.Add(ReadAnimation(br, header));

      UInt32 groupCount = br.ReadUInt32();
      if (groupCount > 100000) throw new InvalidDataException("FXA has an unreasonable animation-group count.");
      for (UInt32 i = 0; i < groupCount; i++) {
        AnimationGroup group = new AnimationGroup {
          Name = ViewFaceFX.StringAt(header, br.ReadUInt32(), "FXA animation group")
        };
        UInt32 animCount = br.ReadUInt32();
        if (animCount > 100000) throw new InvalidDataException("FXA animation group has an unreasonable animation count.");
        for (UInt32 j = 0; j < animCount; j++) group.Animations.Add(ReadAnimation(br, header));
        info.AnimationGroups.Add(group);
      }

      UInt32 mappingCount = br.ReadUInt32();
      if (mappingCount > 100000) throw new InvalidDataException("FXA has an unreasonable phoneme mapping count.");
      for (UInt32 i = 0; i < mappingCount; i++) {
        info.PhonemeMappings.Add(new PhonemeMapping {
          PhonemeIndex = br.ReadUInt32(),
          Target = ViewFaceFX.StringAt(header, br.ReadUInt32(), "FXA phoneme target"),
          Amount = br.ReadSingle()
        });
      }

      UInt32 targetCount = br.ReadUInt32();
      if (targetCount > 100000) throw new InvalidDataException("FXA has an unreasonable phoneme-target count.");
      for (UInt32 i = 0; i < targetCount; i++)
        info.PhonemeTargetNames.Add(ViewFaceFX.StringAt(header, br.ReadUInt32(), "FXA phoneme target name"));

      if (input.Position != input.Length)
        throw new InvalidDataException("FXA parser stopped at 0x" + input.Position.ToString("X") + " of 0x" + input.Length.ToString("X") + ".");
      return info;
    }

    internal static ArrayList BuildTree(FxaInfo info) {
      ArrayList roots = new ArrayList();
      if (info == null) return roots;
      NodeListItem header = new NodeListItem("FaceFX actor", info.Name);
      header.children.Add(new NodeListItem("Root bone", info.RootBone));
      header.children.Add(new NodeListItem("Master bones", info.Bones.Count));
      header.children.Add(new NodeListItem("Face graph nodes", info.FaceGraph.Count));
      header.children.Add(new NodeListItem("Default animations", info.DefaultAnimations.Count));
      header.children.Add(new NodeListItem("Animation groups", info.AnimationGroups.Count));
      header.children.Add(new NodeListItem("Phoneme mappings", info.PhonemeMappings.Count));
      roots.Add(header);

      NodeListItem bones = new NodeListItem("Master bone list", info.Bones.Count + " bones");
      for (Int32 i = 0; i < info.Bones.Count; i++) {
        Bone b = info.Bones[i];
        NodeListItem row = new NodeListItem("#" + i + " " + b.Name, "client #" + b.ClientIndex + (b.ReferenceWeight == 0f ? " (root)" : String.Empty));
        row.children.Add(new NodeListItem("Position", FormatVec(b.Position)));
        row.children.Add(new NodeListItem("Rotation", FormatQuat(b.Rotation)));
        row.children.Add(new NodeListItem("Scale", FormatVec(b.Scale)));
        row.children.Add(new NodeListItem("Inverse rotation", FormatQuat(b.InverseRotation)));
        row.children.Add(new NodeListItem("Reference weight", b.ReferenceWeight.ToString("0.######", CultureInfo.InvariantCulture)));
        NodeListItem poses = new NodeListItem("Bone-pose links", b.PoseNodes.Count + " entries");
        foreach (PoseNode pose in b.PoseNodes) {
          String poseName = pose.NodeIndex < (UInt32)info.FaceGraph.Count ? info.FaceGraph[(Int32)pose.NodeIndex].Name : "?";
          NodeListItem p = new NodeListItem("Node #" + pose.NodeIndex + " " + poseName, FormatVec(pose.Position));
          p.children.Add(new NodeListItem("Position", FormatVec(pose.Position)));
          p.children.Add(new NodeListItem("Rotation", FormatQuat(pose.Rotation)));
          p.children.Add(new NodeListItem("Scale", FormatVec(pose.Scale)));
          poses.children.Add(p);
        }
        row.children.Add(poses);
        bones.children.Add(row);
      }
      roots.Add(bones);

      NodeListItem graph = new NodeListItem("Compiled face graph", info.FaceGraph.Count + " nodes");
      for (Int32 i = 0; i < info.FaceGraph.Count; i++) {
        FaceNode n = info.FaceGraph[i];
        NodeListItem row = new NodeListItem("#" + i + " " + n.Name, NodeTypeName(n.NodeType) + ", " + InputOperationName(n.InputOperation));
        row.children.Add(new NodeListItem("Range", String.Format(CultureInfo.InvariantCulture, "{0:0.######} .. {1:0.######}", n.Min, n.Max)));
        NodeListItem links = new NodeListItem("Input links", n.InputLinks.Count + " entries");
        foreach (FaceLink link in n.InputLinks) {
          String parameters = link.Parameters.Count == 0 ? String.Empty : " (" + String.Join(", ", link.Parameters.ConvertAll(v => v.ToString("0.######", CultureInfo.InvariantCulture))) + ")";
          String sourceName = link.NodeIndex < (UInt32)info.FaceGraph.Count ? info.FaceGraph[(Int32)link.NodeIndex].Name : "?";
          links.children.Add(new NodeListItem("Node #" + link.NodeIndex + " " + sourceName, LinkFunctionName(link.FunctionType) + parameters));
        }
        row.children.Add(links);
        graph.children.Add(row);
      }
      roots.Add(graph);

      // Inputs without incoming links are the values fed into the compiled graph by
      // FaceFX analysis or BioWare's RoboBrad gesture layer. Keeping them separate
      // makes the actor useful for reverse engineering without needing a graph UI.
      List<String> faceFxInputs = new List<String>();
      List<String> roboBradInputs = new List<String>();
      for (Int32 i = 0; i < info.FaceGraph.Count; i++) {
        FaceNode node = info.FaceGraph[i];
        if (node.InputLinks.Count != 0 || info.PhonemeTargetNames.Contains(node.Name)) continue;
        if (node.Name.Contains(" ", StringComparison.Ordinal) || String.Equals(node.Name, "Blink", StringComparison.OrdinalIgnoreCase))
          faceFxInputs.Add(node.Name);
        else
          roboBradInputs.Add(node.Name);
      }
      faceFxInputs.Sort(StringComparer.OrdinalIgnoreCase);
      roboBradInputs.Sort(StringComparer.OrdinalIgnoreCase);
      if (faceFxInputs.Count > 0 || roboBradInputs.Count > 0) {
        NodeListItem inputs = new NodeListItem("External graph inputs", (faceFxInputs.Count + roboBradInputs.Count) + " inputs");
        NodeListItem analysis = new NodeListItem("FaceFX analysis", faceFxInputs.Count + " inputs");
        foreach (String name in faceFxInputs) analysis.children.Add(new NodeListItem(name, "analysis input"));
        NodeListItem robobrad = new NodeListItem("RoboBrad gestures", roboBradInputs.Count + " inputs");
        foreach (String name in roboBradInputs) robobrad.children.Add(new NodeListItem(name, "gesture input"));
        if (faceFxInputs.Count > 0) inputs.children.Add(analysis);
        if (roboBradInputs.Count > 0) inputs.children.Add(robobrad);
        roots.Add(inputs);
      }

      if (info.DefaultAnimations.Count > 0) roots.Add(BuildAnimationsNode("Default animations", info.DefaultAnimations));
      foreach (AnimationGroup group in info.AnimationGroups) roots.Add(BuildAnimationsNode("Animation group: " + group.Name, group.Animations));

      NodeListItem phonemes = new NodeListItem("Phoneme map", info.PhonemeMappings.Count + " mappings");
      foreach (PhonemeMapping mapping in info.PhonemeMappings)
        phonemes.children.Add(new NodeListItem(PhonemeName(mapping.PhonemeIndex), mapping.Target + " × " + mapping.Amount.ToString("0.######", CultureInfo.InvariantCulture)));
      NodeListItem targets = new NodeListItem("Target names", info.PhonemeTargetNames.Count + " entries");
      for (Int32 i = 0; i < info.PhonemeTargetNames.Count; i++) targets.children.Add(new NodeListItem("#" + i, info.PhonemeTargetNames[i]));
      phonemes.children.Add(targets);
      roots.Add(phonemes);

      if (info.Header != null) {
        NodeListItem archive = new NodeListItem("FaceFX archive", info.Header.Strings.Count + " strings, " + info.Header.ClassVersions.Count + " class versions");
        archive.children.Add(new NodeListItem("SDK version", info.Header.SdkVersion));
        archive.children.Add(new NodeListItem("File-format version", info.Header.FileFormatVersion));
        archive.children.Add(new NodeListItem("Licensee", info.Header.LicenseeName));
        archive.children.Add(new NodeListItem("Project", info.Header.LicenseeProjectName));
        archive.children.Add(new NodeListItem("Archive-directory version", info.Header.ArchiveDirectoryVersion));
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

    private static ViewFXE.Animation ReadAnimation(BinaryReader br, ViewFaceFX.HeaderInfo header) {
      ViewFXE.Animation anim = new ViewFXE.Animation {
        Name = ViewFaceFX.StringAt(header, br.ReadUInt32(), "FXA animation")
      };
      UInt32 numCurveNames = br.ReadUInt32();
      if (numCurveNames > 100000) throw new InvalidDataException("FXA animation has an unreasonable curve count.");
      List<String> curveNames = new List<String>(checked((Int32)numCurveNames));
      for (UInt32 i = 0; i < numCurveNames; i++) {
        curveNames.Add(ViewFaceFX.StringAt(header, br.ReadUInt32(), "FXA curve"));
        UInt32 interpolation = br.ReadUInt32();
        if (interpolation != 0) throw new InvalidDataException("FXA curve uses unsupported interpolation type " + interpolation + ".");
      }
      UInt32 numKeys = br.ReadUInt32();
      if (numKeys > 10000000) throw new InvalidDataException("FXA animation has an unreasonable key count.");
      List<ViewFXE.Key> keys = new List<ViewFXE.Key>(checked((Int32)numKeys));
      for (UInt32 i = 0; i < numKeys; i++) keys.Add(new ViewFXE.Key {
        Time = br.ReadSingle(), Value = br.ReadSingle(), SlopeIn = br.ReadSingle(), SlopeOut = br.ReadSingle()
      });
      if (numKeys > 0) {
        UInt32 mappingCount = br.ReadUInt32();
        if (mappingCount != numCurveNames) throw new InvalidDataException("FXA curve mapping count mismatch.");
        Int32 dataPos = 0;
        for (Int32 curveIndex = 0; curveIndex < curveNames.Count; curveIndex++) {
          UInt32 rows = br.ReadUInt32();
          if (rows > Int32.MaxValue || dataPos > keys.Count - (Int32)rows) throw new InvalidDataException("FXA curve mapping points outside key data.");
          ViewFXE.Curve curve = new ViewFXE.Curve { Name = curveNames[curveIndex] };
          for (Int32 k = 0; k < (Int32)rows; k++) curve.Keys.Add(keys[dataPos + k]);
          dataPos += (Int32)rows;
          anim.Curves.Add(curve);
        }
        if (dataPos != keys.Count) throw new InvalidDataException("FXA curve mappings do not consume all keys.");
      } else {
        foreach (String name in curveNames) anim.Curves.Add(new ViewFXE.Curve { Name = name });
      }
      anim.BlendInTime = br.ReadSingle();
      anim.BlendOutTime = br.ReadSingle();
      ViewFaceFX.ExpectZero(br.ReadUInt32(), "FXA animation bone-weight count");
      ViewFaceFX.ExpectZero(br.ReadUInt32(), "FXA animation sound-cue path");
      ViewFaceFX.ExpectZero(br.ReadUInt32(), "FXA animation sound-node wave");
      Int32 soundCueIndex = br.ReadInt32();
      if (soundCueIndex != -1) throw new InvalidDataException("FXA animation sound cue index expected -1, got " + soundCueIndex + ".");
      return anim;
    }

    private static NodeListItem BuildAnimationsNode(String title, IList<ViewFXE.Animation> animations) {
      NodeListItem root = new NodeListItem(title, animations.Count + " animations");
      for (Int32 i = 0; i < animations.Count; i++) {
        ViewFXE.Animation anim = animations[i];
        GetAnimationRange(anim, out Single start, out Single end);
        String duration = end > start ? String.Format(CultureInfo.InvariantCulture, ", {0:0.###} s", end - start) : String.Empty;
        NodeListItem row = new NodeListItem("#" + i + " " + anim.Name, anim.Curves.Count + " curves" + duration);
        if (end > start) row.children.Add(new NodeListItem("Key range", String.Format(CultureInfo.InvariantCulture, "{0:0.######} .. {1:0.######} s", start, end)));
        row.children.Add(new NodeListItem("Blend", String.Format(CultureInfo.InvariantCulture, "{0:0.###} / {1:0.###} s", anim.BlendInTime, anim.BlendOutTime)));
        NodeListItem curves = new NodeListItem("Curves", anim.Curves.Count + " entries");
        foreach (ViewFXE.Curve curve in anim.Curves) {
          NodeListItem c = new NodeListItem(curve.Name, curve.Keys.Count + " keys");
          for (Int32 k = 0; k < curve.Keys.Count; k++) {
            ViewFXE.Key key = curve.Keys[k];
            c.children.Add(new NodeListItem("#" + k, String.Format(CultureInfo.InvariantCulture,
              "t {0:0.######} value {1:0.######} slope {2:0.######}/{3:0.######}", key.Time, key.Value, key.SlopeIn, key.SlopeOut)));
          }
          curves.children.Add(c);
        }
        row.children.Add(curves);
        root.children.Add(row);
      }
      return root;
    }

    private static void GetAnimationRange(ViewFXE.Animation animation, out Single start, out Single end) {
      start = Single.MaxValue;
      end = Single.MinValue;
      foreach (ViewFXE.Curve curve in animation.Curves) {
        foreach (ViewFXE.Key key in curve.Keys) {
          if (key.Time < start) start = key.Time;
          if (key.Time > end) end = key.Time;
        }
      }
      if (start == Single.MaxValue) { start = 0f; end = 0f; }
    }

    private static Single[] ReadVec3(BinaryReader br) => new[] { br.ReadSingle(), br.ReadSingle(), br.ReadSingle() };
    private static Single[] ReadFaceQuat(BinaryReader br) {
      Single w = br.ReadSingle(); Single x = br.ReadSingle(); Single y = br.ReadSingle(); Single z = br.ReadSingle();
      return new[] { x, y, z, w };
    }
    private static void CheckStoredInverse(Single value, Single inverse, String label, String nodeName) {
      Single expected = value == 0f ? 1f : 1f / value;
      if (Math.Abs(inverse - expected) > 0.0001f)
        throw new InvalidDataException("FXA " + label + " inverse is inconsistent for node " + nodeName + ".");
    }
    private static void CheckInverseQuaternion(Single[] rotation, Single[] inverse, String boneName) {
      if (rotation == null || inverse == null || rotation.Length != 4 || inverse.Length != 4)
        throw new InvalidDataException("FXA bone " + boneName + " has an invalid quaternion.");
      if (Math.Abs(inverse[0] + rotation[0]) > 0.000001f
          || Math.Abs(inverse[1] + rotation[1]) > 0.000001f
          || Math.Abs(inverse[2] + rotation[2]) > 0.000001f
          || Math.Abs(inverse[3] - rotation[3]) > 0.000001f)
        throw new InvalidDataException("FXA inverse rotation does not match the reference rotation for bone " + boneName + ".");
    }
    private static String FormatVec(Single[] v) => String.Format(CultureInfo.InvariantCulture, "({0:0.#####}, {1:0.#####}, {2:0.#####})", v[0], v[1], v[2]);
    private static String FormatQuat(Single[] q) => String.Format(CultureInfo.InvariantCulture, "({0:0.#####}, {1:0.#####}, {2:0.#####}, {3:0.#####})", q[0], q[1], q[2], q[3]);
    private static String NodeTypeName(UInt32 v) => v switch { 0 => "Combiner", 1 => "Delta", 2 => "CurrentTime", 3 => "GenericTarget", 4 => "BonePose", 5 => "MorphTarget", 6 => "MaterialParameterUE3", 7 => "MorphTargetUE3", _ => "Invalid(" + v + ")" };
    private static String InputOperationName(UInt32 v) => v switch { 0 => "Sum", 1 => "Multiply", 2 => "Max", 3 => "Min", _ => "Invalid(" + v + ")" };
    private static String LinkFunctionName(UInt32 v) => v switch { 0 => "Null", 1 => "Linear", 2 => "Quadratic", 3 => "Cubic", 4 => "Sqrt", 5 => "Negate", 6 => "Inverse", 7 => "OneClamp", 8 => "Constant", 9 => "Corrective", 10 => "ClampedLinear", _ => "Invalid(" + v + ")" };

    private static readonly String[] Phonemes = {
      "SIL","P","B","T","D","K","G","M","N","NG","RA","RU","FLAP","PH","F","V","TH","DH","S","Z","SH","ZH","CX","X","GH","HH","R","Y","L","W","H","TS","CH","JH","IY","E","EN","EH","A","AA","AAN","AO","AON","O","ON","UW","UY","EU","OE","OEN","AH","IH","UU","UH","AX","UX","AE","ER","AXR","EXR","EY","AW","AY","OY","OW"
    };
    private static String PhonemeName(UInt32 index) => index < (UInt32)Phonemes.Length ? Phonemes[(Int32)index] : "Invalid(" + index + ")";
  }
}
