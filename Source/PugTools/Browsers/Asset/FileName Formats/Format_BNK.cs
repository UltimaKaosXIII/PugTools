using System;
using System.Collections.Generic;
using System.IO;

namespace PugTools {
  internal class FileFormat_BNK {
    private readonly FileFormat_BNK_DATA _data;

    internal FileFormat_BNK_BKHD BKHD { get; private set; }
    internal FileFormat_BNK_DIDX DIDX { get; set; }
    internal FileFormat_BNK_HIRC HIRC { get; set; }
    internal FileFormat_BNK_ENVS ENVS { get; set; }
    internal FileFormat_BNK_STID STID { get; set; }

    internal FileFormat_BNK(BinaryReader br, Boolean loadWEMs = false) {
      Char[] section_header;

      while (br.BaseStream.Position != br.BaseStream.Length) {
        section_header = br.ReadChars(4);
        String header_str = String.Join("", section_header);

        switch (header_str) {
          case "BKHD":
            BKHD = new FileFormat_BNK_BKHD(br);
            break;

          case "DIDX":
            DIDX = new FileFormat_BNK_DIDX(br);
            break;

          case "DATA":
            _data = new FileFormat_BNK_DATA(br);
            break;

          case "HIRC":
            HIRC = new FileFormat_BNK_HIRC(br, BKHD?.Version ?? 0);
            break;

          case "ENVS":
            ENVS = new FileFormat_BNK_ENVS(br);
            break;

          case "STID":
            STID = new FileFormat_BNK_STID(br);
            break;

          default:
            UInt32 length = br.ReadUInt32();
            br.BaseStream.Seek(length, SeekOrigin.Current);
            break;
        }
      }

      if (loadWEMs) {
        if (DIDX != null && _data != null) {
          Boolean isBeta = BKHD != null && BKHD.Version <= 56;
          foreach (ViewWEM wem in DIDX.Wems) {
            br.BaseStream.Seek(_data.Offset /*+4*/, SeekOrigin.Begin);
            br.BaseStream.Seek(wem.Offset, SeekOrigin.Current);
            wem.Data = br.ReadBytes((Int32)wem.Length);
            // Jedipedia keys the legacy codebook set off BKHD version 56. Carry that
            // information with embedded WEMs so preview does not have to guess.
            wem.IsBeta = isBeta;
          }
        }
      }
    }
  }
  internal sealed class FileFormat_BNK_BKHD {
    internal UInt32 Length { get; }
    internal UInt32 Version { get; }
    internal UInt32 Id { get; }

    internal FileFormat_BNK_BKHD(BinaryReader br) {
      Length = br.ReadUInt32();
      Int64 payloadStart = br.BaseStream.Position;
      Int64 payloadEnd = payloadStart + Length;

      // Every SWTOR bank we care about stores version/id first, but do not assume
      // the remainder has one fixed Wwise revision-specific size.
      if (Length >= 4) Version = br.ReadUInt32();
      if (Length >= 8) Id = br.ReadUInt32();

      if (br.BaseStream.CanSeek) {
        br.BaseStream.Seek(payloadEnd, SeekOrigin.Begin);
      } else {
        Int64 consumed = br.BaseStream.Position - payloadStart;
        Int64 remaining = Math.Max(0, (Int64)Length - consumed);
        while (remaining > 0) {
          Int32 take = (Int32)Math.Min(8192, remaining);
          Byte[] skipped = br.ReadBytes(take);
          if (skipped.Length == 0) throw new EndOfStreamException("Unexpected end of BKHD section.");
          remaining -= skipped.Length;
        }
      }
    }
  }
  internal class FileFormat_BNK_DATA {
    private readonly UInt32 _length;

    internal Int64 Offset { get; }

    internal FileFormat_BNK_DATA(BinaryReader br) {
      _length = br.ReadUInt32();
      Offset = br.BaseStream.Position;
      br.BaseStream.Seek(_length, SeekOrigin.Current);
    }
  }
  internal class FileFormat_BNK_DIDX {
    private readonly UInt32 _length;
    // private Int64 _offset;

    internal List<ViewWEM> Wems { get; set; }

    internal FileFormat_BNK_DIDX(BinaryReader br) {
      Wems = new List<ViewWEM>();

      // _offset = br.BaseStream.Position;
      _length = br.ReadUInt32();

      Int32 intFileCount = (Int32)_length / 12;

      for (Int32 intCount = 0; intCount < intFileCount; intCount++) {
        ViewWEM wem = new ViewWEM(br);
        Wems.Add(wem);
      }
    }
  }
  internal sealed class FileFormat_BNK_ENVS_Point {
    internal Single X { get; set; }
    internal Single Y { get; set; }
    internal UInt32 Shape { get; set; }
  }

  internal sealed class FileFormat_BNK_ENVS_Curve {
    internal Boolean Enabled { get; set; }
    internal Byte Type { get; set; }
    internal List<FileFormat_BNK_ENVS_Point> Points { get; } = new List<FileFormat_BNK_ENVS_Point>();
  }

  internal sealed class FileFormat_BNK_ENVS {
    internal List<FileFormat_BNK_ENVS_Curve> Curves { get; } = new List<FileFormat_BNK_ENVS_Curve>();

    internal FileFormat_BNK_ENVS(BinaryReader br) {
      UInt32 length = br.ReadUInt32();
      Int64 end = br.BaseStream.Position + length;
      try {
        while (br.BaseStream.Position + 4 <= end) {
          FileFormat_BNK_ENVS_Curve curve = new FileFormat_BNK_ENVS_Curve {
            Enabled = br.ReadByte() != 0,
            Type = br.ReadByte()
          };
          UInt16 points = br.ReadUInt16();
          if (points > 1024 || br.BaseStream.Position + points * 12L > end) break;
          for (Int32 i = 0; i < points; i++) {
            curve.Points.Add(new FileFormat_BNK_ENVS_Point {
              X = br.ReadSingle(), Y = br.ReadSingle(), Shape = br.ReadUInt32()
            });
          }
          Curves.Add(curve);
        }
      } finally {
        if (br.BaseStream.CanSeek) br.BaseStream.Seek(end, SeekOrigin.Begin);
      }
    }
  }

  internal class FileFormat_BNK_HIRC {
    internal UInt32 NumObject { get; set; }
    internal List<FileFormat_BNK_HIRC_Object> Objects { get; set; }

    internal FileFormat_BNK_HIRC(BinaryReader br, UInt32 version) {
      Objects = new List<FileFormat_BNK_HIRC_Object>();

      UInt32 sectionLength = br.ReadUInt32();
      Int64 sectionEnd = br.BaseStream.Position + sectionLength;
      NumObject = br.ReadUInt32();

      for (Int32 intCount = 0; intCount < NumObject && br.BaseStream.Position < sectionEnd; intCount++) {
        // Wwise <= 48 stored the HIRC type as uint32; later SWTOR banks use uint8.
        UInt32 rawType = version != 0 && version <= 48 ? br.ReadUInt32() : br.ReadByte();
        UInt32 length = br.ReadUInt32();
        if (length < 4 || length > Int32.MaxValue || br.BaseStream.Position + length > sectionEnd) {
          // A corrupt/unknown object must never desynchronise the remainder of the bank.
          br.BaseStream.Seek(sectionEnd, SeekOrigin.Begin);
          break;
        }

        Byte[] objectBytes = br.ReadBytes((Int32)length);
        if (objectBytes.Length != length) break;
        Objects.Add(new FileFormat_BNK_HIRC_Object((Byte)rawType, objectBytes, version));
      }

      if (br.BaseStream.CanSeek && br.BaseStream.Position != sectionEnd)
        br.BaseStream.Seek(sectionEnd, SeekOrigin.Begin);
    }
  }

  internal sealed class WwiseStateAssignment {
    internal UInt32 StateId { get; set; }
    internal UInt32 SettingsId { get; set; }
  }

  internal sealed class WwiseStateGroup {
    internal UInt32 GroupId { get; set; }
    internal Byte SyncType { get; set; }
    internal List<WwiseStateAssignment> States { get; } = new List<WwiseStateAssignment>();
  }

  internal sealed class WwiseRtpcPoint {
    internal Single X { get; set; }
    internal Single Y { get; set; }
    internal UInt32 Shape { get; set; }
  }

  internal sealed class WwiseRtpcCurve {
    internal UInt32 ParameterId { get; set; }
    internal UInt32 TargetType { get; set; }
    internal UInt32 CurveId { get; set; }
    internal Byte Scaling { get; set; }
    internal List<WwiseRtpcPoint> Points { get; } = new List<WwiseRtpcPoint>();
  }

  internal sealed class WwiseSwitchGrouping {
    internal UInt32 SwitchId { get; set; }
    internal List<UInt32> Items { get; } = new List<UInt32>();
  }

  internal sealed class WwiseDuckedBus {
    internal UInt32 BusId { get; set; }
    internal Single Volume { get; set; }
    internal Int32 FadeOutMs { get; set; }
    internal Int32 FadeInMs { get; set; }
    internal Byte Shape { get; set; }
  }

  internal sealed class WwiseAttenuationCurve {
    internal Byte Scaling { get; set; }
    internal List<WwiseRtpcPoint> Points { get; } = new List<WwiseRtpcPoint>();
  }

  internal class FileFormat_BNK_HIRC_Object {
    private readonly List<UInt32> _audioIds = new List<UInt32>();
    private readonly List<UInt32> _eventActions = new List<UInt32>();
    private readonly List<UInt32> _children = new List<UInt32>();
    private readonly List<UInt32> _effectIds = new List<UInt32>();

    internal UInt32 Id { get; private set; }
    internal Byte Type { get; private set; }
    internal UInt32 Version { get; private set; }
    internal Byte[] RawPayload { get; private set; }
    internal String ParseWarning { get; private set; }

    internal UInt32 AudioId { get; private set; }
    internal UInt32 AudioSourceId { get; private set; }
    internal UInt32 Embed { get; private set; }
    internal UInt32 ActionObjectId { get; private set; }
    internal UInt32 ParentId { get; private set; }
    internal UInt32 OutputBusId { get; private set; }
    internal UInt32 AttenuationId { get; private set; }
    internal UInt32 ActionType { get; private set; }
    internal UInt32 ActionScope { get; private set; }
    internal UInt32 StateGroupId { get; private set; }
    internal UInt32 StateId { get; private set; }
    internal UInt32 SwitchGroupId { get; private set; }
    internal UInt32 SwitchId { get; private set; }
    internal UInt32 DefaultSwitchId { get; private set; }
    internal UInt32 SoundBankId { get; private set; }
    internal UInt32 PositioningSourceType { get; private set; }
    internal Boolean Is3DPositioned { get; private set; }

    internal IReadOnlyList<UInt32> EventActions => _eventActions;
    internal IReadOnlyList<UInt32> AudioIds => _audioIds;
    internal IReadOnlyList<UInt32> Children => _children;
    internal IReadOnlyList<UInt32> EffectIds => _effectIds;
    internal List<WwiseStateGroup> StateGroups { get; } = new List<WwiseStateGroup>();
    internal List<WwiseRtpcCurve> Rtpcs { get; } = new List<WwiseRtpcCurve>();
    internal List<WwiseSwitchGrouping> SwitchGroupings { get; } = new List<WwiseSwitchGrouping>();
    internal List<WwiseDuckedBus> DuckedBusses { get; } = new List<WwiseDuckedBus>();
    internal List<WwiseAttenuationCurve> AttenuationCurves { get; } = new List<WwiseAttenuationCurve>();

    internal FileFormat_BNK_HIRC_Object(Byte type, Byte[] objectBytes, UInt32 version) {
      Type = type;
      Version = version;
      if (objectBytes == null || objectBytes.Length < 4) {
        RawPayload = Array.Empty<Byte>();
        ParseWarning = "HIRC object was shorter than its 4-byte id.";
        return;
      }

      using MemoryStream ms = new MemoryStream(objectBytes, false);
      using BinaryReader br = new BinaryReader(ms);
      Id = br.ReadUInt32();
      RawPayload = br.ReadBytes((Int32)(ms.Length - ms.Position));

      try {
        using MemoryStream payload = new MemoryStream(RawPayload, false);
        using BinaryReader pr = new BinaryReader(payload);
        ParsePayload(pr);
      } catch (Exception ex) when (ex is EndOfStreamException || ex is IOException || ex is ArgumentException) {
        ParseWarning = ex.Message;
      }
    }

    private void ParsePayload(BinaryReader br) {
      switch (Type) {
        case 2: ParseSound(br); break;
        case 3: ParseEventAction(br); break;
        case 4: ParseEvent(br); break;
        case 5: ParseRandomSequence(br); break;
        case 6: ParseSwitchContainer(br); break;
        case 7: ParseActorMixer(br); break;
        case 8: ParseAudioBus(br); break;
        case 9: ParseBlendContainer(br); break;
        case 10: ParseMusicSegment(br); break;
        case 11: ParseMusicTrack(br); break;
        case 12: ParseMusicSwitch(br); break;
        case 13: ParseMusicPlaylist(br); break;
        case 14: ParseAttenuation(br); break;
        case 18:
        case 19: ParseEffect(br); break;
      }
    }

    private static void Require(BinaryReader br, Int64 bytes) {
      if (bytes < 0 || br.BaseStream.Position + bytes > br.BaseStream.Length)
        throw new EndOfStreamException("Unexpected end of Wwise HIRC payload.");
    }

    private static void Skip(BinaryReader br, Int64 bytes) {
      Require(br, bytes);
      br.BaseStream.Seek(bytes, SeekOrigin.Current);
    }

    private void ParseEvent(BinaryReader br) {
      UInt32 count = br.ReadUInt32();
      if (count > 100000) throw new InvalidDataException("Unreasonable Wwise event action count.");
      for (UInt32 i = 0; i < count; i++) _eventActions.Add(br.ReadUInt32());
    }

    private void ParseEventAction(BinaryReader br) {
      if (Version != 0 && Version <= 56) {
        UInt32 raw = br.ReadUInt32();
        ActionType = (raw >> 12) & 0xFF;
        ActionScope = raw & 0xFFF;
        ActionObjectId = br.ReadUInt32();
        Skip(br, 12); // delay value/min/max
        UInt32 subSize = br.ReadUInt32();
        Int64 subEnd = br.BaseStream.Position + subSize;
        if (subEnd > br.BaseStream.Length) throw new EndOfStreamException("Invalid beta EventAction subsection.");
        if (subSize >= 8 && ActionType == 0x12) {
          StateGroupId = br.ReadUInt32(); StateId = br.ReadUInt32();
        } else if (subSize >= 8 && ActionType == 0x19) {
          SwitchGroupId = br.ReadUInt32(); SwitchId = br.ReadUInt32();
        }
        br.BaseStream.Position = subEnd;
        if ((ActionType == 0x04 || ActionType == 0x05) && br.BaseStream.Position + 4 <= br.BaseStream.Length)
          SoundBankId = br.ReadUInt32();
        return;
      }

      ActionScope = br.ReadByte();
      ActionType = br.ReadByte();
      ActionObjectId = br.ReadUInt32();
      Byte numParams = br.ReadByte();
      Skip(br, numParams);
      Skip(br, numParams * 4L);
      Byte numRandom = br.ReadByte();
      Skip(br, numRandom);
      Skip(br, numRandom * 8L);

      if (ActionType == 1 || ActionType == 4) {
        Skip(br, 1);
        SoundBankId = br.ReadUInt32();
      } else if (ActionType == 2 || ActionType == 3) {
        Skip(br, 6);
      } else if (ActionType == 8 || ActionType == 9 || ActionType == 10 || ActionType == 11 ||
                 ActionType == 14 || ActionType == 15 || ActionType == 19 || ActionType == 20) {
        Skip(br, 18);
      } else if (ActionType == 18) {
        StateGroupId = br.ReadUInt32(); StateId = br.ReadUInt32();
      } else if (ActionType == 25) {
        SwitchGroupId = br.ReadUInt32(); SwitchId = br.ReadUInt32();
      }
    }

    private void ParseSound(BinaryReader br) {
      if (Version != 0 && Version <= 56) {
        ParseOldSource(br);
        ParseOldNodeBase(br);
        return;
      }

      UInt16 unknown1 = br.ReadUInt16();
      br.ReadUInt16();
      if (unknown1 == 1) {
        ParseLiveFileRef(br);
        ParseLiveNodeBase(br, false, true);
      }
    }

    private void ParseLiveFileRef(BinaryReader br) {
      Embed = br.ReadUInt32(); // 0 embedded, 1 streamed, 2 streamed with prefetch
      AudioId = br.ReadUInt32();
      AudioSourceId = br.ReadUInt32();
      if (Embed == 0 || Embed == 2) Skip(br, 8);
      Skip(br, 1);
    }

    private void ParseOldSource(BinaryReader br) {
      UInt32 pluginId = br.ReadUInt32();
      UInt32 streamType = br.ReadUInt32();
      if (Version <= 46) Skip(br, 8);
      AudioSourceId = br.ReadUInt32();
      AudioId = br.ReadUInt32();
      Embed = streamType;
      if (streamType != 1) Skip(br, 8);
      Skip(br, 1);
      UInt32 pluginType = pluginId & 0x0F;
      if (pluginType == 2 || pluginType == 5) {
        UInt32 size = br.ReadUInt32(); Skip(br, size);
      }
    }

    private void ParseRandomSequence(BinaryReader br) {
      if (Version != 0 && Version <= 56) {
        ParseOldNodeBase(br);
        Skip(br, 2 + 12 + 2 + 8);
        ReadChildren(br);
        return;
      }
      ParseLiveNodeBase(br, false, true);
      Skip(br, 2 + 12 + 2 + 1 + 1 + 2 + 1 + 1 + 1 + 1);
      ReadChildren(br);
    }

    private void ParseSwitchContainer(BinaryReader br) {
      if (Version != 0 && Version <= 56) ParseOldNodeBase(br);
      else ParseLiveNodeBase(br, false, true);

      UInt32 groupType = br.ReadUInt32();
      SwitchGroupId = br.ReadUInt32();
      DefaultSwitchId = br.ReadUInt32();
      Skip(br, 1);
      ReadChildren(br);
      UInt32 groupCount = br.ReadUInt32();
      if (groupCount > 100000) throw new InvalidDataException("Unreasonable Wwise switch grouping count.");
      for (UInt32 i = 0; i < groupCount; i++) {
        WwiseSwitchGrouping grouping = new WwiseSwitchGrouping { SwitchId = br.ReadUInt32() };
        UInt32 itemCount = br.ReadUInt32();
        if (itemCount > 100000) throw new InvalidDataException("Unreasonable Wwise switch item count.");
        for (UInt32 j = 0; j < itemCount; j++) grouping.Items.Add(br.ReadUInt32());
        SwitchGroupings.Add(grouping);
      }
      // Grouping behavior follows; relations above are the useful semantic part.
      _ = groupType;
    }

    private void ParseActorMixer(BinaryReader br) {
      if (Version != 0 && Version <= 56) ParseOldNodeBase(br);
      else ParseLiveNodeBase(br, false, true);
      ReadChildren(br);
    }

    private void ParseBlendContainer(BinaryReader br) {
      if (Version != 0 && Version <= 56) ParseOldNodeBase(br);
      else ParseLiveNodeBase(br, false, true);
      ReadChildren(br);
    }

    private void ParseMusicSegment(BinaryReader br) {
      if (Version != 0 && Version <= 56) {
        ParseOldNodeBase(br);
        ReadChildren(br);
      } else {
        ParseLiveNodeBase(br, false, false);
        ReadChildren(br);
      }
    }

    private void ParseMusicTrack(BinaryReader br) {
      if (Version != 0 && Version <= 56) {
        UInt32 sources = br.ReadUInt32();
        if (sources > 10000) throw new InvalidDataException("Unreasonable beta music-track source count.");
        for (UInt32 i = 0; i < sources; i++) {
          UInt32 oldAudio = AudioId, oldSource = AudioSourceId, oldEmbed = Embed;
          ParseOldSource(br);
          if (i > 0) { AudioId = oldAudio; AudioSourceId = oldSource; Embed = oldEmbed; }
        }
        UInt32 playlist = br.ReadUInt32();
        if (playlist > 100000) throw new InvalidDataException("Unreasonable beta music-track playlist count.");
        Skip(br, playlist * 40L);
        if (playlist > 0) Skip(br, 4); // numSubTrack
        ParseOldNodeBase(br);
        return;
      }

      Skip(br, 8); // uint32 const1 + uint16 const1 + uint16 format
      ParseLiveFileRef(br);

      UInt32 clips = br.ReadUInt32();
      if (clips > 100000) throw new InvalidDataException("Unreasonable Wwise music clip count.");
      Skip(br, clips * 40L);
      Skip(br, 4); // const 1
      UInt32 curves = br.ReadUInt32();
      if (curves > 100000) throw new InvalidDataException("Unreasonable Wwise music property-curve count.");
      for (UInt32 i = 0; i < curves; i++) {
        Skip(br, 8);
        UInt32 points = br.ReadUInt32();
        if (points > 100000) throw new InvalidDataException("Unreasonable Wwise music property-curve point count.");
        Skip(br, points * 12L);
      }
      ParseLiveNodeBase(br, true, false);
      if (br.BaseStream.Position + 8 <= br.BaseStream.Length) Skip(br, 8); // track type + look-ahead
    }

    private void ParseMusicSwitch(BinaryReader br) {
      if (Version != 0 && Version <= 56) {
        // Parse enough of the beta shared structure to expose hierarchy/control data.
        ParseOldNodeBase(br);
        ReadChildren(br);
        TryParseMusicSwitchTail(br);
        return;
      }
      ParseLiveNodeBase(br, false, false);
      ReadChildren(br);
      // The transition table is variable-sized; parse switch/state mapping by scanning the validated tail conservatively.
      TryParseMusicSwitchTail(br);
    }

    private void ParseMusicPlaylist(BinaryReader br) {
      if (Version != 0 && Version <= 56) ParseOldNodeBase(br);
      else ParseLiveNodeBase(br, false, false);
      ReadChildren(br);
    }

    private void TryParseMusicSwitchTail(BinaryReader br) {
      // Wwise v62 music switch containers end with: groupType, groupId, default, continue, count, pairs.
      // Search from the current position for a tail whose pair count lands exactly at EOF.
      Byte[] remaining = br.ReadBytes((Int32)(br.BaseStream.Length - br.BaseStream.Position));
      for (Int32 off = Math.Max(0, remaining.Length - 4096); off + 17 <= remaining.Length; off++) {
        UInt32 groupType = BitConverter.ToUInt32(remaining, off);
        if (groupType > 1) continue;
        UInt32 count = BitConverter.ToUInt32(remaining, off + 13);
        Int64 end = off + 17L + count * 8L;
        if (count > 10000 || end != remaining.Length) continue;
        SwitchGroupId = BitConverter.ToUInt32(remaining, off + 4);
        DefaultSwitchId = BitConverter.ToUInt32(remaining, off + 8);
        for (UInt32 i = 0; i < count; i++) {
          Int32 p = off + 17 + (Int32)i * 8;
          WwiseSwitchGrouping g = new WwiseSwitchGrouping { SwitchId = BitConverter.ToUInt32(remaining, p) };
          g.Items.Add(BitConverter.ToUInt32(remaining, p + 4));
          SwitchGroupings.Add(g);
        }
        return;
      }
    }

    private void ParseAudioBus(BinaryReader br) {
      if (Version != 0 && Version <= 56) {
        ParentId = br.ReadUInt32();
        Skip(br, 16); // volume/LFE/pitch/LPF
        Skip(br, 2);  // kill newest/use virtual behavior
        Skip(br, 2);  // max instances
        Skip(br, 1);  // override parent
        Skip(br, 2);  // channel config
        Skip(br, 2);  // legacy unused bytes
        Boolean envBus = br.ReadByte() != 0;
        Skip(br, 4 + 4); // recovery time + max duck volume
        UInt32 ducked = br.ReadUInt32();
        for (UInt32 i = 0; i < ducked; i++) {
          WwiseDuckedBus d = new WwiseDuckedBus {
            BusId = br.ReadUInt32(), Volume = br.ReadSingle(), FadeOutMs = br.ReadInt32(), FadeInMs = br.ReadInt32(), Shape = br.ReadByte()
          };
          DuckedBusses.Add(d);
        }
        Byte fxCount = br.ReadByte();
        if (fxCount > 0 || envBus) {
          Skip(br, 1); // bypass mask
          for (Int32 i = 0; i < fxCount; i++) { Skip(br, 1); _effectIds.Add(br.ReadUInt32()); Skip(br, 2); }
        }
        ParseRtpcs(br, true);
        ParseOldStateChunk(br);
        return;
      }

      ParentId = br.ReadUInt32();
      Skip(br, 2 + 4 + 4);
      Byte extra = br.ReadByte();
      Skip(br, 4 + 4);
      UInt32 count = br.ReadUInt32();
      for (UInt32 i = 0; i < count; i++) {
        WwiseDuckedBus d = new WwiseDuckedBus {
          BusId = br.ReadUInt32(), Volume = br.ReadSingle(), FadeOutMs = br.ReadInt32(), FadeInMs = br.ReadInt32(), Shape = br.ReadByte()
        };
        DuckedBusses.Add(d);
      }
      Byte effects = br.ReadByte();
      if (effects > 0) {
        Skip(br, 1);
        for (Int32 i = 0; i < effects; i++) { Skip(br, 1); _effectIds.Add(br.ReadUInt32()); Skip(br, 2); }
      }
      ParseRtpcs(br, false);
      if (br.BaseStream.Position + 4 <= br.BaseStream.Length) Skip(br, 4);
      if (extra == 1 && br.BaseStream.Position < br.BaseStream.Length) Skip(br, 1);
    }

    private void ParseEffect(BinaryReader br) {
      // Effect ShareSet/Custom: plugin header + opaque plugin properties + media flag + RTPCs.
      Require(br, 8);
      Skip(br, 4); // plugin type/company + plugin id
      UInt32 propertyBytes = br.ReadUInt32();
      Skip(br, propertyBytes);
      Byte mediaChildren = br.ReadByte();
      if (mediaChildren > 0) {
        // SWTOR normally stores zero here. Keep payload bounded instead of guessing a newer layout.
        throw new InvalidDataException("Unsupported Wwise effect media-child layout.");
      }
      ParseRtpcs(br, Version != 0 && Version <= 56);
    }

    private void ParseAttenuation(BinaryReader br) {
      if (Version != 0 && Version <= 56) {
        Byte cone = br.ReadByte();
        if ((cone & 1) != 0) Skip(br, 16);
        Skip(br, 5);
        Byte curveCount = br.ReadByte();
        for (Int32 i = 0; i < curveCount; i++) {
          WwiseAttenuationCurve curve = new WwiseAttenuationCurve { Scaling = br.ReadByte() };
          UInt16 points = br.ReadUInt16();
          for (Int32 j = 0; j < points; j++) curve.Points.Add(ReadCurvePoint(br));
          AttenuationCurves.Add(curve);
        }
        ParseRtpcs(br, true);
        return;
      }

      Byte hasVector = br.ReadByte();
      if (hasVector == 1) Skip(br, 16);
      Skip(br, 5); // const/bool + three signed curve selectors
      Byte outer = br.ReadByte();
      for (Int32 i = 0; i < outer; i++) {
        WwiseAttenuationCurve curve = new WwiseAttenuationCurve { Scaling = br.ReadByte() };
        UInt16 points = br.ReadUInt16();
        for (Int32 j = 0; j < points; j++) curve.Points.Add(ReadCurvePoint(br));
        AttenuationCurves.Add(curve);
      }
    }

    private WwiseRtpcPoint ReadCurvePoint(BinaryReader br) {
      return new WwiseRtpcPoint { X = br.ReadSingle(), Y = br.ReadSingle(), Shape = br.ReadUInt32() };
    }

    private void ReadChildren(BinaryReader br) {
      UInt32 count = br.ReadUInt32();
      if (count > 100000) throw new InvalidDataException("Unreasonable Wwise child count.");
      for (UInt32 i = 0; i < count; i++) _children.Add(br.ReadUInt32());
      // v10 exposed music segment children as AudioIds; preserve that API.
      if (Type == 10) _audioIds.AddRange(_children);
    }

    private void ParseLiveNodeBase(BinaryReader br, Boolean isMusicTrack, Boolean isSound) {
      Skip(br, 1); // Override parent effects
      Byte effects = br.ReadByte();
      if (effects > 0) {
        Skip(br, 1);
        for (Int32 i = 0; i < effects; i++) {
          Skip(br, 1); _effectIds.Add(br.ReadUInt32()); Skip(br, 2);
        }
      }

      if (!isMusicTrack) OutputBusId = br.ReadUInt32();
      ParentId = br.ReadUInt32();
      Skip(br, 2);

      Byte additional = br.ReadByte(); Skip(br, additional); Skip(br, additional * 4L);
      Byte doubles = br.ReadByte(); Skip(br, doubles); Skip(br, doubles * 8L);

      Byte positioning = br.ReadByte();
      if (positioning == 1) {
        Byte posType = br.ReadByte();
        Is3DPositioned = posType != 0;
        if (posType == 0) Skip(br, 1);
        else {
          PositioningSourceType = br.ReadUInt32();
          AttenuationId = br.ReadUInt32();
          Skip(br, 1);
          if (PositioningSourceType == 2) {
            Skip(br, 10);
            UInt32 n1 = br.ReadUInt32(); Skip(br, n1 * 16L);
            UInt32 n2 = br.ReadUInt32(); Skip(br, n2 * 16L);
          } else if (PositioningSourceType == 3) Skip(br, 1);
        }
      }

      if (!isSound) {
        Skip(br, 3);
        Byte aux = br.ReadByte(); if (aux == 1) Skip(br, 16);
        Byte limit = br.ReadByte(); if (limit > 0) Skip(br, 4);
      } else Skip(br, 5);
      Skip(br, 4);

      UInt32 stateGroups = br.ReadUInt32();
      if (stateGroups > 10000) throw new InvalidDataException("Unreasonable Wwise state group count.");
      for (UInt32 i = 0; i < stateGroups; i++) {
        WwiseStateGroup group = new WwiseStateGroup { GroupId = br.ReadUInt32(), SyncType = br.ReadByte() };
        UInt16 custom = br.ReadUInt16();
        for (Int32 j = 0; j < custom; j++) group.States.Add(new WwiseStateAssignment { StateId = br.ReadUInt32(), SettingsId = br.ReadUInt32() });
        StateGroups.Add(group);
      }
      ParseRtpcs(br, false);
    }

    private void ParseOldNodeBase(BinaryReader br) {
      Skip(br, 1);
      Byte effects = br.ReadByte();
      if (effects > 0) {
        Skip(br, 1);
        for (Int32 i = 0; i < effects; i++) {
          Skip(br, 1); _effectIds.Add(br.ReadUInt32());
          if (Version <= 48) {
            Skip(br, 1);
            UInt32 preset = br.ReadUInt32(); Skip(br, preset);
            if (Version > 46) { UInt32 bankData = br.ReadUInt32(); Skip(br, bankData * 8L); }
          } else Skip(br, 2);
        }
      }
      OutputBusId = br.ReadUInt32();
      ParentId = br.ReadUInt32();
      Skip(br, 4);
      Skip(br, 12 * 4L);
      if (Version <= 52) StateGroupId = br.ReadUInt32();
      ParseOldPositioning(br);
      if (Version <= 53) Skip(br, 7); else Skip(br, 9);
      ParseOldStateChunk(br);
      ParseRtpcs(br, true);
    }

    private void ParseOldPositioning(BinaryReader br) {
      Byte flags = br.ReadByte();
      if ((flags & 1) == 0) return;
      Skip(br, 12);
      Byte has3d = br.ReadByte();
      Is3DPositioned = has3d != 0;
      if (has3d == 0) { Skip(br, 1); return; }
      PositioningSourceType = br.ReadUInt32();
      AttenuationId = br.ReadUInt32();
      Skip(br, 1);
      if (PositioningSourceType == 3) Skip(br, 1);
      else if (PositioningSourceType == 2) {
        Skip(br, 4 + 1 + 4 + 1);
        UInt32 vertices = br.ReadUInt32(); Skip(br, vertices * 16L);
        UInt32 items = br.ReadUInt32(); Skip(br, items * 8L); Skip(br, items * 8L);
      }
    }

    private void ParseOldStateChunk(BinaryReader br) {
      if (Version <= 52) {
        Byte sync = br.ReadByte();
        UInt16 count = br.ReadUInt16();
        WwiseStateGroup group = new WwiseStateGroup { GroupId = StateGroupId, SyncType = sync };
        for (Int32 i = 0; i < count; i++) {
          UInt32 state = br.ReadUInt32(); Skip(br, 1); UInt32 settings = br.ReadUInt32();
          group.States.Add(new WwiseStateAssignment { StateId = state, SettingsId = settings });
        }
        if (count > 0 || StateGroupId != 0) StateGroups.Add(group);
      } else {
        UInt32 groups = br.ReadUInt32();
        for (UInt32 i = 0; i < groups; i++) {
          WwiseStateGroup group = new WwiseStateGroup { GroupId = br.ReadUInt32(), SyncType = br.ReadByte() };
          UInt16 count = br.ReadUInt16();
          for (Int32 j = 0; j < count; j++) group.States.Add(new WwiseStateAssignment { StateId = br.ReadUInt32(), SettingsId = br.ReadUInt32() });
          StateGroups.Add(group);
        }
      }
    }

    private void ParseRtpcs(BinaryReader br, Boolean oldLayout) {
      UInt16 count = br.ReadUInt16();
      if (count > 512) throw new InvalidDataException("Unreasonable Wwise RTPC count.");
      for (Int32 i = 0; i < count; i++) {
        WwiseRtpcCurve rtpc = new WwiseRtpcCurve();
        if (oldLayout) {
          if (Version <= 36) Skip(br, 4);
          else if (Version <= 48) Skip(br, 5);
          rtpc.ParameterId = br.ReadUInt32();
          rtpc.TargetType = br.ReadUInt32();
          rtpc.CurveId = br.ReadUInt32();
          rtpc.Scaling = br.ReadByte();
        } else {
          rtpc.ParameterId = br.ReadUInt32();
          rtpc.TargetType = br.ReadUInt32();
          rtpc.CurveId = br.ReadUInt32();
          rtpc.Scaling = br.ReadByte();
        }
        UInt16 points = br.ReadUInt16();
        if (points > 4096) throw new InvalidDataException("Unreasonable Wwise RTPC point count.");
        for (Int32 j = 0; j < points; j++) rtpc.Points.Add(ReadCurvePoint(br));
        Rtpcs.Add(rtpc);
      }
    }
  }
  internal class FileFormat_BNK_HIRC_SoundStruct {
    internal FileFormat_BNK_HIRC_SoundStruct(BinaryReader br) {
      // Bool override
      br.ReadBoolean();
      // Number of effects
      Int32 numEffects = br.ReadByte();

      if (numEffects > 0) {
        // Bit mask
        br.ReadByte();


        for (Int32 count = 0; count < numEffects; count++) {
          // Effect index
          br.ReadByte();
          // Effect id
          br.ReadUInt32();
          // Unknown
          br.ReadBytes(2);
        }
      }

      // Id of output bus
      br.ReadUInt32();
      // Id of parent object
      br.ReadUInt32();
      // Override playback priority
      br.ReadBoolean();
      // Offset priority
      br.ReadBoolean();
      // Number of additional paramaters
      Int32 numParam = br.ReadByte();

      if (numParam > 0) {
        for (Int32 count = 0; count < numParam; count++) {
          br.ReadByte();
        }

        for (Int32 count = 0; count < numParam; count++) {
          br.ReadUInt32();
        }
      }

      // Unknown
      br.ReadByte();
      // Positioning section included
      Boolean positioning = br.ReadBoolean();

      if (positioning) {
        // Type 00 = 2d, 01 = 3d
        Byte position_type = br.ReadByte();

        if (position_type == 0) {
          br.ReadBoolean();
        } else if (position_type == 1) {
          // Type of source
          UInt32 position_source = br.ReadUInt32();
          // Id of attenuation object
          br.ReadUInt32();
          // Enable spatial
          br.ReadBoolean();

          if (position_source == 2) {
            // Play type
            br.ReadUInt32();
            // Loop?
            br.ReadBoolean();
            // Transition time 
            br.ReadUInt32();
            // Follow listener orientation
            br.ReadBoolean();
          } else if (position_source == 3) {
            // Update at each frame
            br.ReadBoolean();
          }
        }
      }

      // Overrite game defined aux sends
      br.ReadBoolean();
      // Use game defined aux sends
      br.ReadBoolean();
      // Override user aux sends  
      br.ReadBoolean();
      // Use user aux sends
      Boolean user_def_aux_sends = br.ReadBoolean();

      if (user_def_aux_sends) {
        // Id aux bus 0
        br.ReadUInt32();
        // Id aux bus 1
        br.ReadUInt32();
        // Id aux bus 2
        br.ReadUInt32();
        // Id aux bus 3
        br.ReadUInt32();
      }

      // Unknown playback limit
      Boolean unknown = br.ReadBoolean();

      if (unknown) {
        // Priority equal
        br.ReadByte();
        // Limit reached
        br.ReadByte();
        // Limit instances
        br.ReadUInt16();
      }

      // How limit instances
      br.ReadByte();
      // Virtual voice behave
      br.ReadByte();
      // Override plaback limit
      br.ReadBoolean();
      // Override virtual voice
      br.ReadBoolean();
      // Number state groups
      UInt32 state_groups = br.ReadUInt32();

      if (state_groups > 0) {
        for (Int32 count = 0; count < state_groups; count++) {
          // State group id
          br.ReadUInt32();
          // Change occurs at
          br.ReadByte();
          // Number of custom setting states
          UInt16 custom = br.ReadUInt16();

          if (custom > 0) {
            for (Int32 count2 = 0; count2 < custom; count2++) {
              // Id state object
              br.ReadUInt32();
              // Id object contains settings
              br.ReadUInt32();
            }
          }
        }
      }

      UInt16 rtpc = br.ReadUInt16();//number of rtpc

      if (rtpc > 0) {
        for (Int32 count = 0; count < rtpc; count++) {

          // Id of game param
          br.ReadUInt32();
          // Y-axis type
          br.ReadUInt32();
          // Unknown
          br.ReadUInt32();
          // Unkown
          br.ReadByte();

          // Number of points
          Byte points = br.ReadByte();

          // Unknown
          br.ReadByte();

          if (points > 0) {
            for (Int32 count2 = 0; count2 < points; count2++) {
              // Float x
              br.ReadUInt32();
              // Float y
              br.ReadUInt32();
              // Share of curve
              br.ReadUInt32();
            }
          }
        }
      }
    }
  }
  internal class FileFormat_BNK_STID {
    // private UInt32 _length;
    // private UInt32 _unkown;

    internal UInt32 NumSoundBanks { get; set; }
    internal List<FileFormat_BNK_STID_SoundBank> SoundBanks { get; set; }

    internal FileFormat_BNK_STID(BinaryReader br) {
      SoundBanks = new List<FileFormat_BNK_STID_SoundBank>();

      // _length = 
      br.ReadUInt32();
      // _unkown = 
      br.ReadUInt32();
      NumSoundBanks = br.ReadUInt32();

      for (Int32 intCount = 0; intCount < NumSoundBanks; intCount++) {
        FileFormat_BNK_STID_SoundBank obj = new FileFormat_BNK_STID_SoundBank(br);
        SoundBanks.Add(obj);
      }
    }
  }
  internal class FileFormat_BNK_STID_SoundBank {
    private readonly Byte _nameLength;
    private readonly Char[] _nameTemp;

    internal UInt32 Id { get; }
    internal String Name { get; set; }

    internal FileFormat_BNK_STID_SoundBank(BinaryReader br) {
      Id = br.ReadUInt32();
      _nameLength = br.ReadByte();
      _nameTemp = br.ReadChars(_nameLength);
      Name = String.Join("", _nameTemp);
    }
  }
  class Format_BNK {
    private readonly String _dest;
    private readonly List<String> _errors;
    private readonly String _extension;
    private readonly HashSet<String> _fileNames;

    internal Int32 Found { get; set; }

    internal Format_BNK(String dest, String ext) {
      _dest = dest;
      _errors = new List<String>();
      _extension = ext;
      _fileNames = new HashSet<String>();
    }
    internal void ParseBNK(Stream fileStream, String sourcePath) {
      // Jedipedia skips *_media.bnk during its HIRC sweep: those banks contain the embedded
      // media payload rather than the authored event/source graph and cannot name streamed WEMs.
      if (!String.IsNullOrWhiteSpace(sourcePath)
          && sourcePath.EndsWith("_media.bnk", StringComparison.OrdinalIgnoreCase)) return;

      using BinaryReader br = new BinaryReader(fileStream);
      FileFormat_BNK bnk = new FileFormat_BNK(br);

      Boolean isBeta = bnk.BKHD != null && bnk.BKHD.Version <= 56;
      String streamedRoot = isBeta ? "/resources/bnk/streamed/" : "/resources/bnk2/streamed/";
      String streamedExtension = isBeta ? ".ogg" : ".wem";
      String bankRoot = isBeta ? "/resources/bnk/" : "/resources/bnk2/";

      if (bnk.HIRC != null) {
        if (bnk.HIRC.NumObject != 0) {
          foreach (var obj in bnk.HIRC.Objects) {
            // Wwise's HIRC file reference stores the standalone streamed filename verbatim in
            // AudioSourceId. Embed/isStreamed 0 is bank-local DATA; 1 and 2 point at
            // /resources/bnk2/streamed/<AudioSourceId>.wem (or beta OGG). Candidate hashes are
            // still verified by the Filename Finder before anything enters the dictionary.
            if ((obj.Type == 2 || obj.Type == 11) && obj.Embed > 0 && obj.AudioSourceId != 0)
              _fileNames.Add(streamedRoot + obj.AudioSourceId + streamedExtension);
          }
        }
      }

      if (bnk.STID != null) {
        if (bnk.STID.NumSoundBanks != 0) {
          foreach (var obj in bnk.STID.SoundBanks) {
            _fileNames.Add(bankRoot + obj.Name + ".bnk");
            // Localized bank names are seeded once; the final hash-validation pass expands the
            // locale and therefore does not invent de-de/fr-fr banks that are absent from SWTOR.
            _fileNames.Add((isBeta ? "/resources/en-us/bnk/" : "/resources/en-us/bnk2/") + obj.Name + ".bnk");
          }
        }
      }
    }
    internal void WriteFile() {
      if (!Directory.Exists(_dest + "\\File_Names"))
        Directory.CreateDirectory(_dest + "\\File_Names");

      Found = _fileNames.Count;

      if (_fileNames.Count > 0) {
        StreamWriter outputNames =
          new StreamWriter(_dest + "\\File_Names\\" + _extension + "_file_names.txt", false);

        foreach (String file in _fileNames) {
          outputNames.Write(file.Replace("\\", "/") + "\r\n");
        }

        outputNames.Close();
        _fileNames.Clear();
      }

      if (_errors.Count > 0) {
        StreamWriter outputErrors =
          new StreamWriter(_dest + "\\File_Names\\" + _extension + "_error_list.txt", false);

        foreach (String error in _errors) {
          outputErrors.Write(error + "\r\n");
        }

        outputErrors.Close();
        _errors.Clear();
      }
    }
  }
}
