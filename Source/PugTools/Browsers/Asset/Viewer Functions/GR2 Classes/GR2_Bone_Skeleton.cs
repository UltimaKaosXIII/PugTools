using System;
using System.IO;

namespace FileFormats {
  public class GR2_Bone_Skeleton {
    public Int32 boneIndex;
    public String boneName;
    public UInt64 offsetBoneName;
    public SlimDX.Matrix parent;
    public SlimDX.Matrix boneToParentRaw;
    public Int32 parentBoneIndex;
    public SlimDX.Matrix root;
    public SlimDX.Matrix rootToBoneRaw;

    public GR2_Bone_Skeleton() { }

    public GR2_Bone_Skeleton(BinaryReader br, Int32 index, Boolean is64Bit) {
      offsetBoneName = is64Bit ? br.ReadUInt64() : br.ReadUInt32();
      parentBoneIndex = br.ReadInt32();

      if (is64Bit)
        br.ReadUInt32(); // unknown/padding field in the 64-bit skeleton bone record

      // Preserve the matrices exactly as they are stored in the GR2.  The
      // legacy PugTools fields remain inverted for all existing render paths,
      // while JBA playback can now consume the on-disk inverse-bind matrix
      // directly instead of numerically inverting the already inverted copy a
      // second time.  This matters on exporter matrices with scale/shear (the
      // Ithorian skeleton is a particularly visible example).
      boneToParentRaw = FileHelpers.ReadMatrix(br, false);
      parent = boneToParentRaw;
      parent.Invert();

      rootToBoneRaw = FileHelpers.ReadMatrix(br, false);
      root = rootToBoneRaw;
      root.Invert();

      boneName = FileHelpers.ReadString(br, offsetBoneName);
      boneIndex = index;
    }
  }
}
