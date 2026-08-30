using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace PugTools {
  /// <summary>
  /// Structured reader and decoder for DirectMusic Segment (*.sgt) containers. SWTOR beta SGT files are RIFF/DMSG
  /// containers which wrap a normal Windows RIFF/WAVE stream. The embedded media is usually Microsoft ADPCM rather
  /// than Wwise, so decoding it here avoids depending on an installed ACM codec and works the same on every machine.
  /// </summary>
  internal static class ViewSGT {
    internal sealed class RiffChunk {
      internal String Id = String.Empty;
      internal UInt32 Size;
      internal Int64 Offset;
      internal String FormType = String.Empty;
      internal readonly List<RiffChunk> Children = new List<RiffChunk>();
    }

    internal sealed class AdpcmCoefficient {
      internal Int16 A;
      internal Int16 B;
    }

    internal sealed class WaveInfo {
      internal Int64 Offset;
      internal UInt32 RiffSize;
      internal UInt16 FormatTag;
      internal UInt16 Channels;
      internal UInt32 SampleRate;
      internal UInt32 AverageBytesPerSecond;
      internal UInt16 BlockAlign;
      internal UInt16 BitsPerSample;
      internal UInt16 ExtraSize;
      internal UInt16 SamplesPerBlock;
      internal readonly List<AdpcmCoefficient> Coefficients = new List<AdpcmCoefficient>();
      internal UInt32? FactSamples;
      internal Int64 DataOffset = -1;
      internal UInt32 DataBytes;
      internal Int64 DecodedSamplesPerChannel;
      internal Double DurationSeconds;
      internal String FormatName = String.Empty;
    }

    internal sealed class SgtInfo {
      internal RiffChunk Root;
      internal WaveInfo Wave;
    }

    private static readonly Int32[] MsAdaptTable = {
      230, 230, 230, 230, 307, 409, 512, 614,
      768, 614, 512, 409, 307, 230, 230, 230
    };

    private static readonly AdpcmCoefficient[] MsDefaultCoefficients = {
      new AdpcmCoefficient { A = 256, B = 0 },
      new AdpcmCoefficient { A = 512, B = -256 },
      new AdpcmCoefficient { A = 0, B = 0 },
      new AdpcmCoefficient { A = 192, B = 64 },
      new AdpcmCoefficient { A = 240, B = 0 },
      new AdpcmCoefficient { A = 460, B = -208 },
      new AdpcmCoefficient { A = 392, B = -232 }
    };

    internal static SgtInfo Parse(Stream input) {
      if (input == null) throw new ArgumentNullException(nameof(input));
      if (!input.CanSeek) throw new InvalidDataException("SGT reader requires a seekable stream.");
      input.Position = 0;
      using BinaryReader br = new BinaryReader(input, Encoding.ASCII, true);
      if (input.Length < 12) throw new InvalidDataException("SGT is too short for a RIFF header.");

      SgtInfo info = new SgtInfo();
      info.Root = ReadRiffRoot(br, 0, input.Length, 0, out WaveInfo wave);
      info.Wave = wave ?? throw new InvalidDataException("SGT contains no embedded RIFF/WAVE stream.");
      return info;
    }

    /// <summary>
    /// Decode the first embedded WAVE to a conventional 16-bit PCM RIFF/WAVE file. Supports the formats present in
    /// SWTOR's SGTs (PCM and Microsoft ADPCM) and IEEE-float WAV as a compatibility extra.
    /// </summary>
    internal static Byte[] DecodeToPcmWave(Stream input, SgtInfo info) {
      if (input == null) throw new ArgumentNullException(nameof(input));
      if (info?.Wave == null) throw new ArgumentNullException(nameof(info));
      if (!input.CanSeek) throw new InvalidDataException("SGT decoder requires a seekable stream.");

      WaveInfo wave = info.Wave;
      if (wave.Channels == 0 || wave.SampleRate == 0)
        throw new InvalidDataException("SGT embedded WAVE has an invalid channel count or sample rate.");
      if (wave.DataOffset < 0 || wave.DataBytes == 0)
        throw new InvalidDataException("SGT embedded WAVE contains no audio data chunk.");

      Int64 oldPosition = input.Position;
      try {
        input.Position = wave.DataOffset;
        using BinaryReader br = new BinaryReader(input, Encoding.ASCII, true);
        Int16[] pcm;
        switch (wave.FormatTag) {
          case 0x0001:
            pcm = DecodePcm(br, wave);
            break;
          case 0x0002:
            pcm = DecodeMicrosoftAdpcm(br, wave);
            break;
          case 0x0003:
            pcm = DecodeIeeeFloat(br, wave);
            break;
          default:
            throw new InvalidDataException("SGT/WAVE codec 0x" + wave.FormatTag.ToString("X4", CultureInfo.InvariantCulture) + " is not supported for playback.");
        }

        wave.DecodedSamplesPerChannel = pcm.LongLength / wave.Channels;
        if (wave.SampleRate > 0) wave.DurationSeconds = wave.DecodedSamplesPerChannel / (Double)wave.SampleRate;
        return BuildPcmWave(pcm, wave.Channels, wave.SampleRate);
      }
      finally {
        input.Position = Math.Min(oldPosition, input.Length);
      }
    }

    private static RiffChunk ReadRiffRoot(BinaryReader br, Int64 offset, Int64 limit, Int32 depth, out WaveInfo wave) {
      wave = null;
      if (depth > 32) throw new InvalidDataException("SGT RIFF nesting is unreasonably deep.");
      br.BaseStream.Position = offset;
      String id = ReadFourCC(br);
      if (id != "RIFF") throw new InvalidDataException("SGT is not a RIFF container.");
      UInt32 size = br.ReadUInt32();
      if (size < 4) throw new InvalidDataException("SGT RIFF chunk is too small.");
      Int64 end = CheckedEnd(offset + 8, size, limit, "RIFF");
      String form = ReadFourCC(br);
      if (depth == 0 && form == "WAVE") throw new InvalidDataException("SGT expected a DirectMusic container, got a bare RIFF/WAVE file.");

      RiffChunk root = new RiffChunk { Id = id, Size = size, Offset = offset, FormType = form };
      if (form == "WAVE") wave = ParseWave(br, offset, size, end);
      ReadChildren(br, root, offset + 12, end, depth + 1, ref wave);
      return root;
    }

    private static void ReadChildren(BinaryReader br, RiffChunk parent, Int64 start, Int64 end, Int32 depth, ref WaveInfo firstWave) {
      if (depth > 32) throw new InvalidDataException("SGT RIFF nesting is unreasonably deep.");
      Int64 pos = start;
      while (pos + 8 <= end) {
        br.BaseStream.Position = pos;
        String id = ReadFourCC(br);
        UInt32 size = br.ReadUInt32();
        Int64 dataStart = pos + 8;
        Int64 dataEnd = CheckedEnd(dataStart, size, end, id);
        RiffChunk chunk = new RiffChunk { Id = id, Size = size, Offset = pos };
        parent.Children.Add(chunk);

        if ((id == "RIFF" || id == "LIST") && size >= 4) {
          br.BaseStream.Position = dataStart;
          chunk.FormType = ReadFourCC(br);
          if (id == "RIFF" && chunk.FormType == "WAVE" && firstWave == null)
            firstWave = ParseWave(br, pos, size, dataEnd);
          ReadChildren(br, chunk, dataStart + 4, dataEnd, depth + 1, ref firstWave);
        }

        Int64 aligned = dataEnd + (size & 1u);
        if (aligned <= pos) throw new InvalidDataException("SGT contains a non-advancing RIFF chunk.");
        pos = Math.Min(aligned, end);
      }
    }

    private static WaveInfo ParseWave(BinaryReader br, Int64 riffOffset, UInt32 riffSize, Int64 riffEnd) {
      WaveInfo wave = new WaveInfo { Offset = riffOffset, RiffSize = riffSize };
      Int64 pos = riffOffset + 12;
      while (pos + 8 <= riffEnd) {
        br.BaseStream.Position = pos;
        String id = ReadFourCC(br);
        UInt32 size = br.ReadUInt32();
        Int64 dataStart = pos + 8;
        Int64 dataEnd = CheckedEnd(dataStart, size, riffEnd, "WAVE/" + id);

        if (id == "fmt " && size >= 16 && wave.FormatTag == 0) {
          wave.FormatTag = br.ReadUInt16();
          wave.Channels = br.ReadUInt16();
          wave.SampleRate = br.ReadUInt32();
          wave.AverageBytesPerSecond = br.ReadUInt32();
          wave.BlockAlign = br.ReadUInt16();
          wave.BitsPerSample = br.ReadUInt16();
          wave.FormatName = WaveFormatName(wave.FormatTag);

          if (size >= 18 && br.BaseStream.Position + 2 <= dataEnd) {
            wave.ExtraSize = br.ReadUInt16();
            if (wave.FormatTag == 0x0002 && wave.ExtraSize >= 4 && br.BaseStream.Position + 4 <= dataEnd) {
              wave.SamplesPerBlock = br.ReadUInt16();
              UInt16 coefficientCount = br.ReadUInt16();
              if (coefficientCount > 1024) throw new InvalidDataException("SGT ADPCM coefficient table is unreasonably large.");
              for (Int32 i = 0; i < coefficientCount && br.BaseStream.Position + 4 <= dataEnd; i++) {
                wave.Coefficients.Add(new AdpcmCoefficient { A = br.ReadInt16(), B = br.ReadInt16() });
              }
            }
          }
        } else if (id == "fact" && size >= 4 && !wave.FactSamples.HasValue) {
          wave.FactSamples = br.ReadUInt32();
        } else if (id == "data" && wave.DataOffset < 0) {
          wave.DataOffset = dataStart;
          wave.DataBytes = size;
        }

        Int64 aligned = dataEnd + (size & 1u);
        if (aligned <= pos) break;
        pos = Math.Min(aligned, riffEnd);
      }

      if (wave.FactSamples.HasValue && wave.SampleRate > 0)
        wave.DurationSeconds = wave.FactSamples.Value / (Double)wave.SampleRate;
      else if (wave.AverageBytesPerSecond > 0)
        wave.DurationSeconds = wave.DataBytes / (Double)wave.AverageBytesPerSecond;
      return wave;
    }

    private static Int16[] DecodePcm(BinaryReader br, WaveInfo wave) {
      Int32 bytesPerSample = wave.BitsPerSample / 8;
      if (wave.BitsPerSample != 8 && wave.BitsPerSample != 16 && wave.BitsPerSample != 24 && wave.BitsPerSample != 32)
        throw new InvalidDataException("SGT PCM uses unsupported " + wave.BitsPerSample + "-bit samples.");
      if (bytesPerSample <= 0) throw new InvalidDataException("SGT PCM has an invalid sample size.");

      Int64 sampleCount64 = wave.DataBytes / bytesPerSample;
      if (sampleCount64 > Int32.MaxValue) throw new InvalidDataException("SGT PCM stream is too large to decode in memory.");
      Int16[] pcm = new Int16[(Int32)sampleCount64];
      for (Int32 i = 0; i < pcm.Length; i++) {
        switch (wave.BitsPerSample) {
          case 8:
            pcm[i] = (Int16)((br.ReadByte() - 128) << 8);
            break;
          case 16:
            pcm[i] = br.ReadInt16();
            break;
          case 24: {
            Int32 lo = br.ReadByte();
            Int32 mid = br.ReadByte();
            Int32 hi = (SByte)br.ReadByte();
            Int32 sample24 = lo | (mid << 8) | (hi << 16);
            pcm[i] = (Int16)(sample24 >> 8);
            break;
          }
          case 32:
            pcm[i] = (Int16)(br.ReadInt32() >> 16);
            break;
        }
      }
      return pcm;
    }

    private static Int16[] DecodeIeeeFloat(BinaryReader br, WaveInfo wave) {
      if (wave.BitsPerSample != 32 && wave.BitsPerSample != 64)
        throw new InvalidDataException("SGT IEEE-float WAVE uses unsupported " + wave.BitsPerSample + "-bit samples.");
      Int32 bytesPerSample = wave.BitsPerSample / 8;
      Int64 sampleCount64 = wave.DataBytes / bytesPerSample;
      if (sampleCount64 > Int32.MaxValue) throw new InvalidDataException("SGT float stream is too large to decode in memory.");
      Int16[] pcm = new Int16[(Int32)sampleCount64];
      for (Int32 i = 0; i < pcm.Length; i++) {
        Double value = wave.BitsPerSample == 32 ? br.ReadSingle() : br.ReadDouble();
        if (Double.IsNaN(value)) value = 0;
        value = Math.Max(-1.0, Math.Min(1.0, value));
        pcm[i] = (Int16)Math.Round(value * (value < 0 ? 32768.0 : 32767.0));
      }
      return pcm;
    }

    private static Int16[] DecodeMicrosoftAdpcm(BinaryReader br, WaveInfo wave) {
      if (wave.Channels != 1 && wave.Channels != 2)
        throw new InvalidDataException("Microsoft ADPCM playback supports mono/stereo; SGT has " + wave.Channels + " channels.");
      if (wave.SamplesPerBlock <= 2)
        throw new InvalidDataException("SGT Microsoft ADPCM has an invalid samples-per-block value " + wave.SamplesPerBlock + ".");
      if (wave.BlockAlign <= 7 * wave.Channels)
        throw new InvalidDataException("SGT Microsoft ADPCM has an invalid block alignment " + wave.BlockAlign + ".");

      Int32 blockAlign = wave.BlockAlign;
      Int32 channels = wave.Channels;
      Int32 samplesPerBlock = wave.SamplesPerBlock;
      Int32 blockCount = checked((Int32)(wave.DataBytes / (UInt32)blockAlign));
      Int64 totalSamples64 = (Int64)blockCount * samplesPerBlock * channels;
      if (totalSamples64 > Int32.MaxValue) throw new InvalidDataException("SGT ADPCM stream is too large to decode in memory.");
      Int16[] pcm = new Int16[(Int32)totalSamples64];

      IList<AdpcmCoefficient> coefficients = wave.Coefficients.Count > 0 ? wave.Coefficients : MsDefaultCoefficients;
      Int32[] sample1 = new Int32[channels];
      Int32[] sample2 = new Int32[channels];
      Int32[] delta = new Int32[channels];
      Int32[] coefA = new Int32[channels];
      Int32[] coefB = new Int32[channels];

      for (Int32 block = 0; block < blockCount; block++) {
        Int64 blockStart = wave.DataOffset + (Int64)block * blockAlign;
        br.BaseStream.Position = blockStart;

        for (Int32 ch = 0; ch < channels; ch++) {
          Int32 coefficientIndex = br.ReadByte();
          if (coefficientIndex >= coefficients.Count) coefficientIndex = coefficients.Count - 1;
          if (coefficientIndex < 0) coefficientIndex = 0;
          coefA[ch] = coefficients[coefficientIndex].A;
          coefB[ch] = coefficients[coefficientIndex].B;
        }
        for (Int32 ch = 0; ch < channels; ch++) delta[ch] = br.ReadInt16();
        for (Int32 ch = 0; ch < channels; ch++) sample1[ch] = br.ReadInt16();
        for (Int32 ch = 0; ch < channels; ch++) sample2[ch] = br.ReadInt16();

        Int32 outBase = checked(block * samplesPerBlock * channels);
        for (Int32 ch = 0; ch < channels; ch++) {
          pcm[outBase + ch] = Clamp16(sample2[ch]);
          pcm[outBase + channels + ch] = Clamp16(sample1[ch]);
        }

        Int32 nibbleBytes = blockAlign - 7 * channels;
        Int32 outputIndex = outBase + 2 * channels;
        Int32 samplesRemaining = (samplesPerBlock - 2) * channels;
        Int32 produced = 0;

        if (channels == 2) {
          for (Int32 b = 0; b < nibbleBytes && produced < samplesRemaining; b++) {
            Byte value = br.ReadByte();
            pcm[outputIndex++] = AdpcmStep(value >> 4, 0, sample1, sample2, delta, coefA, coefB);
            produced++;
            if (produced < samplesRemaining) {
              pcm[outputIndex++] = AdpcmStep(value & 0x0F, 1, sample1, sample2, delta, coefA, coefB);
              produced++;
            }
          }
        } else {
          for (Int32 b = 0; b < nibbleBytes && produced < samplesRemaining; b++) {
            Byte value = br.ReadByte();
            pcm[outputIndex++] = AdpcmStep(value >> 4, 0, sample1, sample2, delta, coefA, coefB);
            produced++;
            if (produced < samplesRemaining) {
              pcm[outputIndex++] = AdpcmStep(value & 0x0F, 0, sample1, sample2, delta, coefA, coefB);
              produced++;
            }
          }
        }
      }

      return pcm;
    }

    private static Int16 AdpcmStep(Int32 nibble, Int32 channel, Int32[] sample1, Int32[] sample2, Int32[] delta, Int32[] coefA, Int32[] coefB) {
      Int32 predictor = (sample1[channel] * coefA[channel] + sample2[channel] * coefB[channel]) >> 8;
      Int32 signedNibble = (nibble & 8) != 0 ? nibble - 16 : nibble;
      predictor += signedNibble * delta[channel];
      predictor = Math.Max(Int16.MinValue, Math.Min(Int16.MaxValue, predictor));
      sample2[channel] = sample1[channel];
      sample1[channel] = predictor;
      Int32 newDelta = (MsAdaptTable[nibble & 0x0F] * delta[channel]) >> 8;
      if (newDelta < 16) newDelta = 16;
      delta[channel] = newDelta;
      return (Int16)predictor;
    }

    private static Int16 Clamp16(Int32 value) => (Int16)Math.Max(Int16.MinValue, Math.Min(Int16.MaxValue, value));

    private static Byte[] BuildPcmWave(Int16[] samples, UInt16 channels, UInt32 sampleRate) {
      if (samples == null) throw new ArgumentNullException(nameof(samples));
      Int64 dataBytes64 = samples.LongLength * 2L;
      if (dataBytes64 > UInt32.MaxValue - 36L) throw new InvalidDataException("Decoded SGT audio is too large for a RIFF/WAVE file.");
      UInt32 dataBytes = (UInt32)dataBytes64;
      UInt32 byteRate = checked(sampleRate * channels * 2u);
      UInt16 blockAlign = checked((UInt16)(channels * 2));

      using MemoryStream output = new MemoryStream(checked((Int32)(44L + dataBytes)));
      using BinaryWriter bw = new BinaryWriter(output, Encoding.ASCII, true);
      WriteFourCC(bw, "RIFF");
      bw.Write(36u + dataBytes);
      WriteFourCC(bw, "WAVE");
      WriteFourCC(bw, "fmt ");
      bw.Write(16u);
      bw.Write((UInt16)1);
      bw.Write(channels);
      bw.Write(sampleRate);
      bw.Write(byteRate);
      bw.Write(blockAlign);
      bw.Write((UInt16)16);
      WriteFourCC(bw, "data");
      bw.Write(dataBytes);
      foreach (Int16 sample in samples) bw.Write(sample);
      bw.Flush();
      return output.ToArray();
    }

    internal static ArrayList BuildTree(SgtInfo info) {
      ArrayList roots = new ArrayList();
      NodeListItem header = new NodeListItem("DirectMusic Segment", info.Root.FormType + " / " + info.Root.Size.ToString(CultureInfo.InvariantCulture) + " bytes");
      header.children.Add(new NodeListItem("RIFF form", info.Root.FormType));
      header.children.Add(new NodeListItem("Container size", info.Root.Size.ToString(CultureInfo.InvariantCulture)));
      roots.Add(header);

      NodeListItem wave = new NodeListItem("Embedded WAVE", info.Wave.FormatName);
      wave.children.Add(new NodeListItem("Offset", "0x" + info.Wave.Offset.ToString("X", CultureInfo.InvariantCulture)));
      wave.children.Add(new NodeListItem("Format tag", "0x" + info.Wave.FormatTag.ToString("X4", CultureInfo.InvariantCulture) + " (" + info.Wave.FormatName + ")"));
      wave.children.Add(new NodeListItem("Channels", info.Wave.Channels.ToString(CultureInfo.InvariantCulture)));
      wave.children.Add(new NodeListItem("Sample rate", info.Wave.SampleRate.ToString(CultureInfo.InvariantCulture) + " Hz"));
      wave.children.Add(new NodeListItem("Average bytes/sec", info.Wave.AverageBytesPerSecond.ToString(CultureInfo.InvariantCulture)));
      wave.children.Add(new NodeListItem("Block align", info.Wave.BlockAlign.ToString(CultureInfo.InvariantCulture)));
      wave.children.Add(new NodeListItem("Bits/sample", info.Wave.BitsPerSample.ToString(CultureInfo.InvariantCulture)));
      if (info.Wave.ExtraSize > 0) wave.children.Add(new NodeListItem("Format extra bytes", info.Wave.ExtraSize.ToString(CultureInfo.InvariantCulture)));
      if (info.Wave.SamplesPerBlock > 0) wave.children.Add(new NodeListItem("Samples/block/channel", info.Wave.SamplesPerBlock.ToString(CultureInfo.InvariantCulture)));
      if (info.Wave.Coefficients.Count > 0) {
        NodeListItem coefficients = new NodeListItem("ADPCM coefficients", info.Wave.Coefficients.Count + " pairs");
        for (Int32 i = 0; i < info.Wave.Coefficients.Count; i++) {
          AdpcmCoefficient pair = info.Wave.Coefficients[i];
          coefficients.children.Add(new NodeListItem("#" + i, pair.A.ToString(CultureInfo.InvariantCulture) + ", " + pair.B.ToString(CultureInfo.InvariantCulture)));
        }
        wave.children.Add(coefficients);
      }
      wave.children.Add(new NodeListItem("Audio data", info.Wave.DataBytes.ToString(CultureInfo.InvariantCulture) + " bytes @ 0x" + info.Wave.DataOffset.ToString("X", CultureInfo.InvariantCulture)));
      if (info.Wave.FactSamples.HasValue) wave.children.Add(new NodeListItem("FACT samples", info.Wave.FactSamples.Value.ToString(CultureInfo.InvariantCulture)));
      if (info.Wave.DecodedSamplesPerChannel > 0) wave.children.Add(new NodeListItem("Decoded samples/channel", info.Wave.DecodedSamplesPerChannel.ToString("N0", CultureInfo.InvariantCulture)));
      if (info.Wave.DurationSeconds > 0) wave.children.Add(new NodeListItem("Estimated duration", info.Wave.DurationSeconds.ToString("0.###", CultureInfo.InvariantCulture) + " s"));
      roots.Add(wave);

      roots.Add(BuildChunkNode(info.Root));
      return roots;
    }

    private static NodeListItem BuildChunkNode(RiffChunk chunk) {
      String value = chunk.FormType.Length == 0
        ? chunk.Size.ToString(CultureInfo.InvariantCulture) + " bytes"
        : chunk.FormType + ", " + chunk.Size.ToString(CultureInfo.InvariantCulture) + " bytes";
      NodeListItem node = new NodeListItem(chunk.Id + " @ 0x" + chunk.Offset.ToString("X", CultureInfo.InvariantCulture), value);
      foreach (RiffChunk child in chunk.Children) node.children.Add(BuildChunkNode(child));
      return node;
    }

    private static String WaveFormatName(UInt16 tag) {
      switch (tag) {
        case 0x0001: return "PCM";
        case 0x0002: return "Microsoft ADPCM";
        case 0x0003: return "IEEE float";
        case 0x0011: return "IMA ADPCM";
        case 0xFFFE: return "Extensible";
        default: return "WAVE format " + tag.ToString(CultureInfo.InvariantCulture);
      }
    }

    private static Int64 CheckedEnd(Int64 start, UInt32 size, Int64 limit, String label) {
      Int64 end;
      try { end = checked(start + size); }
      catch (OverflowException) { throw new InvalidDataException("SGT " + label + " chunk size overflows the stream."); }
      if (end > limit) throw new InvalidDataException("SGT " + label + " chunk extends beyond its RIFF parent.");
      return end;
    }

    private static String ReadFourCC(BinaryReader br) {
      Byte[] data = br.ReadBytes(4);
      if (data.Length != 4) throw new EndOfStreamException();
      return Encoding.ASCII.GetString(data);
    }

    private static void WriteFourCC(BinaryWriter bw, String value) {
      Byte[] data = Encoding.ASCII.GetBytes(value);
      if (data.Length != 4) throw new ArgumentException("FourCC must contain exactly four ASCII bytes.", nameof(value));
      bw.Write(data);
    }
  }
}
