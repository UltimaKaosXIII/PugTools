using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text;

namespace FileFormats {
  public sealed class JBAAnimation {
    public UInt32 Version { get; internal set; }
    public Boolean Is64BitLayout { get; internal set; }
    public Boolean IsBetaHeapLayout { get; internal set; }
    public String ClipName { get; internal set; }
    public Single Length { get; internal set; }
    public Single FPS { get; internal set; }
    public Int32 BlockCount { get; internal set; }
    public Int32 BoneCount { get; internal set; }
    public List<String> BoneNames { get; } = new List<String>();

    internal List<JBA_Bone> Bones { get; } = new List<JBA_Bone>();
    internal List<JBA_Block> Blocks { get; } = new List<JBA_Block>();

    // JBA key data is already decoded by the reader. Build one compact pose
    // per source frame once, then playback only interpolates between arrays.
    private JBATransform[][] _sampleFrames;

    public Int32 FrameCount =>
      FPS > 0 ? Math.Max(1, (Int32)Math.Round(Length * FPS) + 1) : 1;

    public void PrepareSamples() {
      EnsureSampleFrames();
    }

    // Jedipedia treats a translation channel whose complete quantized reach is
    // below 0.01 Morpheme centimetres as a repeated rig offset, not motion.
    // Such channels must use the target GR2 skeleton's bind-local translation;
    // otherwise a clip authored on a slightly different rig can visibly pull
    // bones toward the source rig and distort the motion arc.
    internal Boolean UsesRigBindTranslation(Int32 channel) {
      if (channel < 0 || channel >= Bones.Count)
        return false;

      Vector3 stride = Bones[channel].TranslationStride;
      const Single constantReach = 0.01F;

      return Math.Abs(stride.X) * 2047.0F <= constantReach
        && Math.Abs(stride.Y) * 2047.0F <= constantReach
        && Math.Abs(stride.Z) * 1023.0F <= constantReach;
    }

    private void EnsureSampleFrames() {
      if (_sampleFrames != null
          && _sampleFrames.Length == FrameCount)
        return;

      Int32 count = Math.Max(1, FrameCount);
      _sampleFrames = new JBATransform[count][];

      for (Int32 frame = 0; frame < count; frame++) {
        JBATransform[] result = new JBATransform[BoneCount];

        for (Int32 i = 0; i < result.Length; i++) {
          result[i] = new JBATransform(
            Vector3.Zero,
            Quaternion.Identity,
            false
          );
        }

        foreach (JBA_Block block in Blocks) {
          if (frame < block.StartFrame
              || frame >= block.StartFrame + block.NumFrames)
            continue;

          Int32 localFrame = frame - block.StartFrame;

          for (Int32 b = 0;
               b < block.Layout.Count && b < result.Length;
               b++) {

            JBA_KeyLayout k = block.Layout[b];

            Quaternion rot = k.Rotations.Count > 0
              ? k.Rotations[
                  Math.Min(
                    localFrame,
                    k.Rotations.Count - 1
                  )
                ]
              : Quaternion.Identity;

            Vector3 pos = k.Translations.Count > 0
              ? k.Translations[
                  Math.Min(
                    localFrame,
                    k.Translations.Count - 1
                  )
                ]
              : Vector3.Zero;

            result[b] = new JBATransform(
              pos,
              rot,
              k.HasTranslation
            );
          }

          break;
        }

        _sampleFrames[frame] = result;
      }
    }

    internal Int32 SampleInto(Single time, JBATransform[] result) {
      EnsureSampleFrames();

      if (result == null)
        throw new ArgumentNullException(nameof(result));

      if (result.Length < BoneCount)
        throw new ArgumentException(
          "The JBA sample buffer is smaller than the animation bone count.",
          nameof(result)
        );

      if (_sampleFrames == null || _sampleFrames.Length == 0) {
        for (Int32 i = 0; i < BoneCount; i++) {
          result[i] = new JBATransform(
            Vector3.Zero,
            Quaternion.Identity,
            false
          );
        }
        return 0;
      }

      if (FPS <= 0.0F || _sampleFrames.Length == 1) {
        Array.Copy(_sampleFrames[0], result, BoneCount);
        return 0;
      }

      Single framePosition = time * FPS;

      if (!Single.IsFinite(framePosition))
        framePosition = 0.0F;

      framePosition = Math.Max(
        0.0F,
        Math.Min(
          _sampleFrames.Length - 1,
          framePosition
        )
      );

      Int32 frame0 = Math.Min(
        _sampleFrames.Length - 1,
        (Int32)Math.Floor(framePosition)
      );

      Int32 frame1 = Math.Min(
        _sampleFrames.Length - 1,
        frame0 + 1
      );

      Single alpha = framePosition - frame0;

      if (frame0 == frame1 || alpha <= 0.00001F) {
        Array.Copy(_sampleFrames[frame0], result, BoneCount);
        return frame0;
      }

      JBATransform[] a = _sampleFrames[frame0];
      JBATransform[] b = _sampleFrames[frame1];
      Int32 boneCount = Math.Min(a.Length, b.Length);

      for (Int32 i = 0; i < boneCount; i++) {
        JBATransform ta = a[i];
        JBATransform tb = b[i];

        Quaternion qa = ta.Rotation;
        Quaternion qb = tb.Rotation;

        if (qa.LengthSquared() <= 0.000001F)
          qa = Quaternion.Identity;
        else
          qa = Quaternion.Normalize(qa);

        if (qb.LengthSquared() <= 0.000001F)
          qb = Quaternion.Identity;
        else
          qb = Quaternion.Normalize(qb);

        Quaternion rotation =
          Quaternion.Slerp(qa, qb, alpha);

        Boolean hasTranslation =
          ta.HasTranslation || tb.HasTranslation;

        Vector3 translation;

        if (ta.HasTranslation && tb.HasTranslation) {
          translation = Vector3.Lerp(
            ta.Translation,
            tb.Translation,
            alpha
          );
        }
        else if (ta.HasTranslation) {
          translation = ta.Translation;
        }
        else if (tb.HasTranslation) {
          translation = tb.Translation;
        }
        else {
          translation = Vector3.Zero;
        }

        result[i] = new JBATransform(
          translation,
          rotation,
          hasTranslation
        );
      }

      for (Int32 i = boneCount; i < BoneCount; i++) {
        result[i] = new JBATransform(
          Vector3.Zero,
          Quaternion.Identity,
          false
        );
      }

      return frame0;
    }

    public JBAFrame Sample(Single time) {
      EnsureSampleFrames();

      if (_sampleFrames == null || _sampleFrames.Length == 0)
        return new JBAFrame(
          0,
          Array.Empty<JBATransform>()
        );

      JBATransform[] result = new JBATransform[BoneCount];
      Int32 frame = SampleInto(time, result);
      return new JBAFrame(frame, result);
    }

  }

  public readonly struct JBATransform {
    public readonly Vector3 Translation;
    public readonly Quaternion Rotation;
    public readonly Boolean HasTranslation;

    public JBATransform(
      Vector3 translation,
      Quaternion rotation,
      Boolean hasTranslation
    ) {
      Translation = translation;
      Rotation = rotation;
      HasTranslation = hasTranslation;
    }
  }

  public sealed class JBAFrame {
    public Int32 Frame { get; }
    public IReadOnlyList<JBATransform> Bones { get; }

    internal JBAFrame(Int32 frame, IReadOnlyList<JBATransform> bones) {
      Frame = frame;
      Bones = bones;
    }
  }

  internal sealed class JBA_Bone {
    public Vector3 TranslationStride;
    public Vector3 TranslationBase;
    public Vector3 RotationStride;
    public Vector3 RotationBase;
  }

  internal sealed class JBA_Block {
    public Int32 StartFrame;
    public Int32 Size;
    public Int32 BoneCount;
    public Int32 NumFrames;
    public List<JBA_KeyLayout> Layout = new List<JBA_KeyLayout>();
  }

  internal sealed class JBA_KeyLayout {
    public Boolean HasTranslation;
    public List<Quaternion> Rotations = new List<Quaternion>();
    public List<Vector3> Translations = new List<Vector3>();
  }

  public static class JBAReader {
    private const UInt32 JAWB = 0x4257414A;

    public static JBAAnimation Read(BinaryReader br) {
      if (br == null) throw new ArgumentNullException(nameof(br));

      Int64 start = br.BaseStream.Position;
      if (br.BaseStream.Length - start < 0x28)
        throw new InvalidDataException("JBA file is too short.");

      UInt32 magic = ReadUInt32At(br, start);
      if (magic == JAWB) return ReadCurrent64(br, start);
      if (magic == 0) return ReadLegacy32(br, start);

      // Very early beta assets are not an on-disk JBA/MBA container at all.
      // They are a 32-bit debug-heap snapshot of the live animation object.
      // Jedipedia identifies these by their plausible object header (30 fps)
      // and reconstructs the heap base from ptr@0x1c -> file offset 0x110.
      return ReadBetaHeap(br, start);
    }

    // Faithful C# port of Jedipedia's static-dev/js/reader/filereader/jba-read.js.
    private static JBAAnimation ReadCurrent64(BinaryReader br, Int64 start) {
      if (ReadUInt32At(br, start) != JAWB)
        throw new InvalidDataException("Invalid JAWB header.");

      UInt32 version = ReadUInt32At(br, start + 0x04);
      if (version != 2)
        throw new InvalidDataException("Unsupported JAWB version " + version + ".");

      JBAAnimation a = new JBAAnimation {
        Version = version,
        Is64BitLayout = true,
        Length = ReadSingleAt(br, start + 0x10),
        FPS = ReadSingleAt(br, start + 0x14),
        BlockCount = checked((Int32)ReadUInt32At(br, start + 0x18)),
        BoneCount = checked((Int32)ReadUInt32At(br, start + 0x30))
      };
      ValidateCounts(a);

      Int64 pos = start + 0x50;
      List<(Int32 startFrame, Int32 byteLength)> headers =
        new List<(Int32, Int32)>();

      for (Int32 i = 0; i < a.BlockCount; i++) {
        headers.Add((
          checked((Int32)ReadUInt32At(br, pos)),
          checked((Int32)ReadUInt32At(br, pos + 4))
        ));
        pos += 8;
      }

      // One runtime pointer slot per block, plus another u64 slot per block in
      // current 64-bit files. They are zero on disk.
      pos += 8L * a.BlockCount;
      pos = Align(pos, 0x80);

      // 64-bit bone list has an 8-byte zero prefix.
      Ensure(br, pos, 8);
      pos += 8;

      Ensure(br, pos, 0x30L * a.BoneCount);
      br.BaseStream.Position = pos;
      ReadBoneCompressionData(br, a);
      pos = br.BaseStream.Position;

      // First block begins at the first (0x80*k + 8) offset at/after bone-list end.
      pos = AlignPlus8(pos);

      for (Int32 blockIndex = 0; blockIndex < headers.Count; blockIndex++) {
        var h = headers[blockIndex];
        Int32 numFrames = blockIndex + 1 < headers.Count
          ? 1 + headers[blockIndex + 1].startFrame - h.startFrame
          : a.FrameCount - h.startFrame;
        numFrames = Math.Max(1, numFrames);

        // For all but the first block, navigate from what was actually consumed,
        // not Header.byteLength. This is the behavior documented by Jedipedia's
        // newer jba-spec: current 64-bit byteLength is not always navigational.
        if (blockIndex > 0)
          pos = AlignPlus8(pos);

        Ensure(br, pos, 8);
        br.BaseStream.Position = pos;

        Int32 blockBones = checked((Int32)br.ReadUInt32());
        UInt32 blockBoneOffset = br.ReadUInt32();

        if (blockBones != a.BoneCount)
          throw new InvalidDataException(
            $"JAWB block #{blockIndex}: expected {a.BoneCount} bones, got {blockBones}."
          );
        if (blockBoneOffset != 0)
          throw new InvalidDataException(
            $"JAWB block #{blockIndex}: unexpected bone offset {blockBoneOffset}."
          );

        Boolean[] hasTranslation = new Boolean[blockBones];

        // 64-bit per-bone metadata is 0x20 bytes:
        // u64 zero, u32 rotationCount/u32 rotationOffset,
        // u64 zero, u32 translationCount/u32 translationOffset.
        for (Int32 boneIndex = 0; boneIndex < blockBones; boneIndex++) {
          br.ReadUInt64();

          UInt32 rotationCount = br.ReadUInt32();
          UInt32 rotationOffset = br.ReadUInt32();

          br.ReadUInt64();

          UInt32 translationCount = br.ReadUInt32();
          UInt32 translationOffset = br.ReadUInt32();

          if (rotationCount != (UInt32)numFrames || rotationOffset != 0)
            throw new InvalidDataException(
              $"JAWB block #{blockIndex}, bone {boneIndex}: rotation metadata mismatch."
            );

          if ((translationCount != 0 && translationCount != (UInt32)numFrames)
              || translationOffset != 0)
            throw new InvalidDataException(
              $"JAWB block #{blockIndex}, bone {boneIndex}: translation metadata mismatch."
            );

          hasTranslation[boneIndex] = translationCount != 0;
        }

        // Two zero dwords precede the packed streams in current 64-bit files.
        br.ReadUInt64();

        JBA_Block block = new JBA_Block {
          StartFrame = h.startFrame,
          Size = h.byteLength,
          BoneCount = blockBones,
          NumFrames = numFrames
        };

        for (Int32 boneIndex = 0; boneIndex < blockBones; boneIndex++) {
          JBA_Bone bone = a.Bones[boneIndex];
          JBA_KeyLayout key = new JBA_KeyLayout {
            HasTranslation = hasTranslation[boneIndex]
          };

          for (Int32 frame = 0; frame < numFrames; frame++)
            key.Rotations.Add(
              ReadRotation(br, bone.RotationBase, bone.RotationStride)
            );

          br.BaseStream.Position = Align(br.BaseStream.Position, 4);

          if (hasTranslation[boneIndex]) {
            for (Int32 frame = 0; frame < numFrames; frame++)
              key.Translations.Add(
                ReadTranslation(
                  br,
                  bone.TranslationBase,
                  bone.TranslationStride
                )
              );
          } else {
            // Jedipedia correction: when a translation channel has no keys,
            // TranslationBase itself is the stored constant. Do NOT add the
            // quantization span (that was a major source of bad offsets/jitter).
            for (Int32 frame = 0; frame < numFrames; frame++)
              key.Translations.Add(bone.TranslationBase);
          }

          block.Layout.Add(key);
        }

        a.Blocks.Add(block);
        pos = br.BaseStream.Position;
      }

      // Bone names are optional in 64-bit. Humanoid/shared-skeleton clips usually
      // omit them; creature/self-skeleton clips append a u64-offset string table.
      TryReadCurrentNames(br, a, pos);

      while (a.BoneNames.Count < a.BoneCount)
        a.BoneNames.Add("bone_" + a.BoneNames.Count);

      return a;
    }

    private static JBAAnimation ReadLegacy32(BinaryReader br, Int64 start) {
      if (ReadUInt32At(br, start) != 0)
        throw new InvalidDataException("Invalid legacy JBA header.");

      JBAAnimation a = new JBAAnimation {
        Version = 1,
        Is64BitLayout = false,
        Length = ReadSingleAt(br, start + 0x04),
        FPS = ReadSingleAt(br, start + 0x08),
        BlockCount = checked((Int32)ReadUInt32At(br, start + 0x0C)),
        BoneCount = checked((Int32)ReadUInt32At(br, start + 0x18))
      };
      ValidateCounts(a);

      Int64 pos = start + 0x28;
      List<(Int32 startFrame, Int32 byteLength)> headers =
        new List<(Int32, Int32)>();

      for (Int32 i = 0; i < a.BlockCount; i++) {
        headers.Add((
          checked((Int32)ReadUInt32At(br, pos)),
          checked((Int32)ReadUInt32At(br, pos + 4))
        ));
        pos += 8;
      }

      pos += 4L * a.BlockCount;
      pos = Align(pos, 0x80);

      br.BaseStream.Position = pos;
      ReadBoneCompressionData(br, a);
      pos = Align(br.BaseStream.Position, 0x80);
      Int64 lastBlockDataEnd = pos;

      for (Int32 blockIndex = 0; blockIndex < headers.Count; blockIndex++) {
        var h = headers[blockIndex];
        Int32 numFrames = blockIndex + 1 < headers.Count
          ? 1 + headers[blockIndex + 1].startFrame - h.startFrame
          : a.FrameCount - h.startFrame;
        numFrames = Math.Max(1, numFrames);

        br.BaseStream.Position = pos;
        Int32 blockBones = checked((Int32)br.ReadUInt32());
        UInt32 blockBoneOffset = br.ReadUInt32();

        if (blockBones != a.BoneCount || blockBoneOffset != 0)
          throw new InvalidDataException("Invalid legacy JBA block header.");

        Boolean[] hasTranslation = new Boolean[blockBones];

        for (Int32 boneIndex = 0; boneIndex < blockBones; boneIndex++) {
          UInt32 rotationCount = br.ReadUInt32();
          UInt32 rotationOffset = br.ReadUInt32();
          UInt32 translationCount = br.ReadUInt32();
          UInt32 translationOffset = br.ReadUInt32();

          if (rotationCount != (UInt32)numFrames || rotationOffset != 0)
            throw new InvalidDataException("Invalid legacy rotation metadata.");
          if ((translationCount != 0 && translationCount != (UInt32)numFrames)
              || translationOffset != 0)
            throw new InvalidDataException("Invalid legacy translation metadata.");

          hasTranslation[boneIndex] = translationCount != 0;
        }

        JBA_Block block = new JBA_Block {
          StartFrame = h.startFrame,
          Size = h.byteLength,
          BoneCount = blockBones,
          NumFrames = numFrames
        };

        for (Int32 boneIndex = 0; boneIndex < blockBones; boneIndex++) {
          JBA_Bone bone = a.Bones[boneIndex];
          JBA_KeyLayout key = new JBA_KeyLayout {
            HasTranslation = hasTranslation[boneIndex]
          };

          for (Int32 frame = 0; frame < numFrames; frame++)
            key.Rotations.Add(
              ReadRotation(br, bone.RotationBase, bone.RotationStride)
            );

          br.BaseStream.Position = Align(br.BaseStream.Position, 4);

          if (hasTranslation[boneIndex]) {
            for (Int32 frame = 0; frame < numFrames; frame++)
              key.Translations.Add(
                ReadTranslation(
                  br,
                  bone.TranslationBase,
                  bone.TranslationStride
                )
              );
          } else {
            for (Int32 frame = 0; frame < numFrames; frame++)
              key.Translations.Add(bone.TranslationBase);
          }

          block.Layout.Add(key);
        }

        a.Blocks.Add(block);
        lastBlockDataEnd = br.BaseStream.Position;
        pos += h.byteLength;
      }

      TryReadLegacy32Names(br, a, lastBlockDataEnd);
      while (a.BoneNames.Count < a.BoneCount)
        a.BoneNames.Add("bone_" + a.BoneNames.Count);

      return a;
    }

    private static void TryReadLegacy32Names(BinaryReader br, JBAAnimation a, Int64 searchFrom) {
      Int64 length = br.BaseStream.Length;
      Int64 pos = Math.Max(0, searchFrom);

      // CharacterWorldSpaceTM starts after zero padding with {u32 0, f32 fps}.
      while (pos + 8 <= length) {
        if (ReadUInt32At(br, pos) == 0
            && Math.Abs(ReadSingleAt(br, pos + 4) - a.FPS) < 0.0001F)
          break;
        pos += 4;
      }
      if (pos + 8 > length) return;

      pos += 8;
      if (pos + 0x30 + 16 > length) return;
      pos += 0x30;

      UInt32 rotationCount = ReadUInt32At(br, pos); pos += 4;
      UInt32 rotationOffset = ReadUInt32At(br, pos); pos += 4;
      UInt32 translationCount = ReadUInt32At(br, pos); pos += 4;
      UInt32 translationOffset = ReadUInt32At(br, pos); pos += 4;
      if (rotationCount != (UInt32)a.FrameCount || rotationOffset != 0
          || translationCount != (UInt32)a.FrameCount || translationOffset != 0)
        return;

      pos += 6L * a.FrameCount;
      pos = Align(pos, 4);
      pos += 4L * a.FrameCount;
      if (pos + 20 > length) return;

      Int64 nameStart = pos;
      UInt32 count = ReadUInt32At(br, pos); pos += 4;
      if (count != (UInt32)a.BoneCount) return;
      pos += 4; // stringsLength
      UInt32 indicesOffset = ReadUInt32At(br, pos); pos += 4;
      UInt32 offsetsOffset = ReadUInt32At(br, pos); pos += 4;
      UInt32 namesOffset = ReadUInt32At(br, pos);
      if (indicesOffset != 20
          || offsetsOffset != 20U + 4U * count
          || namesOffset != 20U + 8U * count)
        return;

      for (Int32 i = 0; i < a.BoneCount; i++) {
        UInt32 index = ReadUInt32At(br, nameStart + indicesOffset + i * 4L);
        UInt32 offset = ReadUInt32At(br, nameStart + offsetsOffset + i * 4L);
        if (index != (UInt32)i) return;
        Int64 stringPos = nameStart + namesOffset + offset;
        if (stringPos < 0 || stringPos >= length) return;
        a.BoneNames.Add(ReadCString(br, stringPos));
      }
    }

    private sealed class BetaBlockMeta {
      public UInt32 TranslationCount;
      public Int64 TranslationOffset;
      public UInt32 RotationCount;
      public Int64 RotationOffset;
    }

    private static JBAAnimation ReadBetaHeap(BinaryReader br, Int64 start) {
      Int64 length = br.BaseStream.Length - start;
      if (length < 0x2C)
        throw new InvalidDataException("Beta JBA heap dump is too short.");

      Single duration = ReadSingleAt(br, start + 0x04);
      Single fps = ReadSingleAt(br, start + 0x08);
      Int32 numBlocks = checked((Int32)ReadUInt32At(br, start + 0x0C));
      UInt32 tag = ReadUInt32At(br, start + 0x18);

      if (!Single.IsFinite(duration) || duration < 0 || duration > 3600
          || !Single.IsFinite(fps) || Math.Abs(fps - 30.0F) > 0.001F)
        throw new InvalidDataException("Unrecognized JBA header.");

      JBAAnimation a = new JBAAnimation {
        Version = 0,
        Is64BitLayout = false,
        IsBetaHeapLayout = true,
        Length = duration,
        FPS = fps,
        BlockCount = Math.Max(1, numBlocks)
      };

      // A second beta class shares the broad signature but only exposes a clip
      // name; its remaining heap layout is unrelated/undecoded. Keep it readable
      // in the browser instead of treating it as a corrupt release JBA.
      if (tag != 87) {
        a.ClipName = ReadCStringLimited(br, start + 0x1C, 256);
        a.BoneCount = 0;
        return a;
      }

      if (numBlocks < 1 || numBlocks > 3 || length < 0x1190)
        throw new InvalidDataException("Invalid beta JBA block layout.");

      const Int64 boneReferenceStart = 0x110;
      const Int32 maxBones = 88;
      UInt32 keyframeBufferPtr = ReadUInt32At(br, start + 0x1C);
      if (keyframeBufferPtr < boneReferenceStart)
        throw new InvalidDataException("Invalid beta JBA heap base pointer.");
      UInt32 heapBase = keyframeBufferPtr - (UInt32)boneReferenceStart;

      Int64 ToOffset(UInt32 pointer) {
        if (pointer < heapBase)
          throw new InvalidDataException("Beta JBA pointer precedes the recovered heap base.");
        UInt64 relative = (UInt64)pointer - heapBase;
        if (relative > (UInt64)length)
          throw new EndOfStreamException("Beta JBA pointer points outside the heap dump.");
        return start + (Int64)relative;
      }

      Int32 frameCount = Math.Max(1, (Int32)Math.Round(duration * fps) + 1);
      Int64 pointerArray = start + 0x2C + (numBlocks - 1) * 8L;
      var headers = new List<(Int32 startFrame, Int32 byteLength, Int64 dataStart, Int32 numFrames)>();
      for (Int32 i = 0; i < numBlocks; i++) {
        Int32 startFrame = i == 0
          ? 0
          : checked((Int32)ReadUInt32At(br, start + 0x2C + (i - 1) * 8L));
        Int32 byteLength = i == 0
          ? checked((Int32)ReadUInt32At(br, start + 0x28))
          : checked((Int32)ReadUInt32At(br, start + 0x30 + (i - 1) * 8L));
        Int64 dataStart = ToOffset(ReadUInt32At(br, pointerArray + i * 4L));
        Int32 nextStart = i + 1 < numBlocks
          ? checked((Int32)ReadUInt32At(br, start + 0x2C + i * 8L))
          : frameCount;
        Int32 blockFrames = i + 1 < numBlocks
          ? 1 + nextStart - startFrame
          : frameCount - startFrame;
        if (blockFrames <= 0 || dataStart < start || dataStart >= br.BaseStream.Length)
          throw new InvalidDataException("Invalid beta JBA block descriptor.");
        headers.Add((startFrame, byteLength, dataStart, blockFrames));
      }

      // Recover the real bone count from the first block's 16-byte metadata
      // table; beta's field@0x18 is a class tag (87), not numBones.
      var allMeta = new List<List<BetaBlockMeta>>();
      Int32 boneCount = -1;
      for (Int32 bi = 0; bi < headers.Count; bi++) {
        var h = headers[bi];
        var metas = new List<BetaBlockMeta>();
        Int64 pos = h.dataStart;
        while (pos + 16 <= br.BaseStream.Length && metas.Count < maxBones) {
          UInt32 translationCount = ReadUInt32At(br, pos);
          UInt32 translationPtr = ReadUInt32At(br, pos + 4);
          UInt32 rotationCount = ReadUInt32At(br, pos + 8);
          UInt32 rotationPtr = ReadUInt32At(br, pos + 12);
          if (rotationCount != (UInt32)h.numFrames) break;
          if (translationCount != 0 && translationCount != (UInt32)h.numFrames)
            throw new InvalidDataException("Invalid beta JBA translation metadata.");
          metas.Add(new BetaBlockMeta {
            TranslationCount = translationCount,
            TranslationOffset = translationCount == 0 ? 0 : ToOffset(translationPtr),
            RotationCount = rotationCount,
            RotationOffset = ToOffset(rotationPtr)
          });
          pos += 16;
        }
        if (boneCount < 0) boneCount = metas.Count;
        if (metas.Count != boneCount || boneCount <= 0)
          throw new InvalidDataException("Inconsistent beta JBA bone metadata.");
        allMeta.Add(metas);
      }

      a.BoneCount = boneCount;
      for (Int32 i = 0; i < boneCount; i++) {
        Int64 p = start + boneReferenceStart + i * 0x30L;
        Ensure(br, p, 0x30);
        br.BaseStream.Position = p;
        a.Bones.Add(new JBA_Bone {
          TranslationStride = ReadV3(br),
          TranslationBase = ReadV3(br),
          RotationStride = ReadV3(br),
          RotationBase = ReadV3(br)
        });
      }

      for (Int32 bi = 0; bi < headers.Count; bi++) {
        var h = headers[bi];
        JBA_Block block = new JBA_Block {
          StartFrame = h.startFrame,
          Size = h.byteLength,
          BoneCount = boneCount,
          NumFrames = h.numFrames
        };

        for (Int32 boneIndex = 0; boneIndex < boneCount; boneIndex++) {
          JBA_Bone bone = a.Bones[boneIndex];
          BetaBlockMeta meta = allMeta[bi][boneIndex];
          JBA_KeyLayout key = new JBA_KeyLayout {
            HasTranslation = meta.TranslationCount != 0
          };

          Ensure(br, meta.RotationOffset, 6L * h.numFrames);
          br.BaseStream.Position = meta.RotationOffset;
          for (Int32 frame = 0; frame < h.numFrames; frame++)
            key.Rotations.Add(ReadRotation(br, bone.RotationBase, bone.RotationStride));

          if (meta.TranslationCount != 0) {
            Ensure(br, meta.TranslationOffset, 4L * h.numFrames);
            br.BaseStream.Position = meta.TranslationOffset;
            for (Int32 frame = 0; frame < h.numFrames; frame++)
              key.Translations.Add(ReadTranslation(br, bone.TranslationBase, bone.TranslationStride));
          } else {
            for (Int32 frame = 0; frame < h.numFrames; frame++)
              key.Translations.Add(bone.TranslationBase);
          }

          block.Layout.Add(key);
        }
        a.Blocks.Add(block);
      }

      while (a.BoneNames.Count < a.BoneCount)
        a.BoneNames.Add("bone_" + a.BoneNames.Count);
      return a;
    }

    private static String ReadCStringLimited(BinaryReader br, Int64 pos, Int32 maxBytes) {
      if (pos < 0 || pos >= br.BaseStream.Length) return String.Empty;
      Int64 old = br.BaseStream.Position;
      try {
        br.BaseStream.Position = pos;
        var bytes = new List<Byte>();
        for (Int32 i = 0; i < maxBytes && br.BaseStream.Position < br.BaseStream.Length; i++) {
          Byte b = br.ReadByte();
          if (b == 0) break;
          if (b < 0x20 || b > 0x7E) break;
          bytes.Add(b);
        }
        return Encoding.ASCII.GetString(bytes.ToArray());
      } finally {
        br.BaseStream.Position = old;
      }
    }

    private static void TryReadCurrentNames(
      BinaryReader br,
      JBAAnimation a,
      Int64 searchFrom
    ) {
      // World-space/root-motion data follows the blocks. Locate it via the
      // invariant {u32 zero, f32 frameRate}, walk the known packed streams, and
      // parse a trailing bone-name table only if bytes remain.
      Int64 pos = searchFrom;
      Int64 length = br.BaseStream.Length;

      while (pos + 8 <= length) {
        if (ReadUInt32At(br, pos) == 0
            && Math.Abs(ReadSingleAt(br, pos + 4) - a.FPS) < 0.0001F)
          break;
        pos += 4;
      }

      if (pos + 8 > length)
        return;

      pos += 8;      // zero + repeated FPS
      pos += 0x30;   // root-motion compression frame

      // 64-bit metadata:
      // zero, rotCount, rotOffset, zero+zero, translCount, translOffset, zero+zero
      if (pos + 0x28 > length) return;

      pos += 4;
      UInt32 rotationCount = ReadUInt32At(br, pos); pos += 4;
      pos += 4;
      pos += 8;
      UInt32 translationCount = ReadUInt32At(br, pos); pos += 4;
      pos += 4;
      pos += 8;

      if (rotationCount != (UInt32)a.FrameCount
          || translationCount != (UInt32)a.FrameCount)
        return;

      pos += 6L * a.FrameCount;
      pos = Align(pos, 4);
      pos += 4L * a.FrameCount;

      if (pos + 32 > length)
        return;

      Int64 nameStart = pos;
      UInt32 numStrings = ReadUInt32At(br, pos);
      if (numStrings != (UInt32)a.BoneCount)
        return;

      pos += 4;
      UInt32 stringLength = ReadUInt32At(br, pos); pos += 4;
      UInt64 indicesOffset = ReadUInt64At(br, pos); pos += 8;
      UInt64 offsetsOffset = ReadUInt64At(br, pos); pos += 8;
      UInt64 namesOffset = ReadUInt64At(br, pos); pos += 8;

      if (indicesOffset != 32
          || offsetsOffset != 32UL + 4UL * numStrings
          || namesOffset != 32UL + 8UL * numStrings)
        return;

      for (Int32 i = 0; i < a.BoneCount; i++) {
        UInt32 index = ReadUInt32At(br, nameStart + (Int64)indicesOffset + i * 4L);
        UInt32 off = ReadUInt32At(br, nameStart + (Int64)offsetsOffset + i * 4L);
        if (index != (UInt32)i) continue;
        a.BoneNames.Add(
          ReadCString(br, nameStart + (Int64)namesOffset + off)
        );
      }
    }

    private static void ReadBoneCompressionData(BinaryReader br, JBAAnimation a) {
      for (Int32 i = 0; i < a.BoneCount; i++) {
        a.Bones.Add(new JBA_Bone {
          TranslationStride = ReadV3(br),
          TranslationBase = ReadV3(br),
          RotationStride = ReadV3(br),
          RotationBase = ReadV3(br)
        });
      }
    }

    private static Quaternion ReadRotation(
      BinaryReader br,
      Vector3 baseV,
      Vector3 stride
    ) {
      UInt16 rawX = br.ReadUInt16();
      Single x = baseV.X + (rawX & 0x7FFF) * stride.X;
      Single y = baseV.Y + br.ReadUInt16() * stride.Y;
      Single z = baseV.Z + br.ReadUInt16() * stride.Z;

      Single sum = x * x + y * y + z * z;
      Single w = sum > 1.0F ? 0.0F : (Single)Math.Sqrt(1.0F - sum);
      if ((rawX & 0x8000) != 0)
        w = -w;

      Quaternion q = new Quaternion(x, y, z, w);
      Single lenSq = q.LengthSquared();
      return lenSq > 0.0000001F && Single.IsFinite(lenSq)
        ? Quaternion.Normalize(q)
        : Quaternion.Identity;
    }

    private static Vector3 ReadTranslation(
      BinaryReader br,
      Vector3 baseV,
      Vector3 stride
    ) {
      UInt32 raw = br.ReadUInt32();

      return new Vector3(
        baseV.X + (raw >> 21) * stride.X,
        baseV.Y + ((raw >> 10) & 0x7FF) * stride.Y,
        baseV.Z + (raw & 0x3FF) * stride.Z
      );
    }

    private static Vector3 ReadV3(BinaryReader br) =>
      new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());

    private static Int64 Align(Int64 value, Int32 alignment) =>
      (value + alignment - 1) & ~(alignment - 1L);

    private static Int64 AlignPlus8(Int64 value) =>
      ((value - 8 + 0x7F) & ~0x7FL) + 8;

    private static void ValidateCounts(JBAAnimation a) {
      if (!Single.IsFinite(a.Length) || a.Length < 0 || a.Length > 3600)
        throw new InvalidDataException("Invalid JBA duration.");
      if (!Single.IsFinite(a.FPS) || a.FPS <= 0 || a.FPS > 240)
        throw new InvalidDataException("Invalid JBA frame rate.");
      if (a.BlockCount <= 0 || a.BlockCount > 4096)
        throw new InvalidDataException("Invalid JBA block count.");
      if (a.BoneCount <= 0 || a.BoneCount > 8192)
        throw new InvalidDataException("Invalid JBA bone count.");
    }

    private static void Ensure(BinaryReader br, Int64 pos, Int64 count) {
      if (pos < 0 || count < 0 || pos + count > br.BaseStream.Length)
        throw new EndOfStreamException("JBA data runs beyond the end of the stream.");
    }

    private static UInt32 ReadUInt32At(BinaryReader br, Int64 pos) {
      Int64 old = br.BaseStream.Position;
      br.BaseStream.Position = pos;
      UInt32 value = br.ReadUInt32();
      br.BaseStream.Position = old;
      return value;
    }

    private static UInt64 ReadUInt64At(BinaryReader br, Int64 pos) {
      Int64 old = br.BaseStream.Position;
      br.BaseStream.Position = pos;
      UInt64 value = br.ReadUInt64();
      br.BaseStream.Position = old;
      return value;
    }

    private static Single ReadSingleAt(BinaryReader br, Int64 pos) {
      Int64 old = br.BaseStream.Position;
      br.BaseStream.Position = pos;
      Single value = br.ReadSingle();
      br.BaseStream.Position = old;
      return value;
    }

    private static String ReadCString(BinaryReader br, Int64 pos) {
      Int64 old = br.BaseStream.Position;
      br.BaseStream.Position = pos;

      List<Byte> bytes = new List<Byte>();
      while (br.BaseStream.Position < br.BaseStream.Length) {
        Byte b = br.ReadByte();
        if (b == 0) break;
        bytes.Add(b);
      }

      br.BaseStream.Position = old;
      return Encoding.UTF8.GetString(bytes.ToArray());
    }
  }
}
