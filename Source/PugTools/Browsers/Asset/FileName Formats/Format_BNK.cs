using System;
using System.Collections.Generic;
using System.IO;

namespace PugTools {
  internal class FileFormat_BNK {
    private readonly FileFormat_BNK_DATA _data;

    internal FileFormat_BNK_BKHD BKHD { get; private set; }
    internal FileFormat_BNK_DIDX DIDX { get; set; }
    internal FileFormat_BNK_HIRC HIRC { get; set; }
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
            HIRC = new FileFormat_BNK_HIRC(br);
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
          Boolean isBeta = BKHD != null && BKHD.Version == 56;
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
  internal class FileFormat_BNK_HIRC {
    // private UInt32 _length;
    // private Int64 _offset;

    internal UInt32 NumObject { get; set; }
    internal List<FileFormat_BNK_HIRC_Object> Objects { get; set; }

    internal FileFormat_BNK_HIRC(BinaryReader br) {
      Objects = new List<FileFormat_BNK_HIRC_Object>();

      // _length = 
      br.ReadUInt32();
      NumObject = br.ReadUInt32();

      for (Int32 intCount = 0; intCount < NumObject; intCount++) {
        FileFormat_BNK_HIRC_Object obj = new FileFormat_BNK_HIRC_Object(br);
        Objects.Add(obj);
      }
    }
  }
  internal class FileFormat_BNK_HIRC_Object {
    // All Objects
    private readonly UInt32 _length;
    // Music Segment
    private readonly List<UInt32> _audioIds;
    // Events
    private readonly List<UInt32> _eventActions;
    private readonly UInt32 _numEvents;
    // Event Action
    // private readonly UInt32 _actionObjectId;

    // SoundFX / Music Tracks
    internal UInt32 AudioId { get; }
    internal UInt32 AudioSourceId { get; }
    internal UInt32 Embed { get; }
    // All Objects
    internal UInt32 Id { get; set; }
    internal Byte Type { get; set; }

    internal FileFormat_BNK_HIRC_Object(BinaryReader br) {
      Type = br.ReadByte();
      _length = br.ReadUInt32();
      Id = br.ReadUInt32();

      switch (Type) {
        case 2:
          br.ReadBytes(4);
          Embed = br.ReadUInt32();
          AudioId = br.ReadUInt32();
          AudioSourceId = br.ReadUInt32();

          if (Embed == 0) {
            // Offset
            br.ReadUInt32();
            // Length
            br.ReadUInt32();
          }

          br.ReadByte();

          if (Embed == 0)
            br.BaseStream.Seek(_length - 29, SeekOrigin.Current);
          else
            br.BaseStream.Seek(_length - 21, SeekOrigin.Current);
          break;

        case 3:
          br.ReadByte();
          br.ReadByte();
          // _actionObjectId = 
          br.ReadUInt32();
          br.BaseStream.Seek(_length - 10, SeekOrigin.Current);
          /*
          // Disable this for now
          br.ReadByte();
          Byte numParam = br.ReadByte();
          List<Byte> adtlParam = new List<Byte>();
          Int32 numBytes = 17;
          
          for (Int32 c = 0; c < numParam; c++ ) {
            adtlParam.Add(br.ReadByte());
            numBytes++;
          }

          foreach(Byte param in adtlParam) {
            if (param == 0x0E || param == 0x0F) {
              br.ReadUInt32();
              numBytes += 4;
            } else {
              br.ReadSingle();
              numBytes += 4;
            }
          }

          br.ReadByte();
          numBytes += 1;

          if (type == 0x12) {
            UInt32 state_group_id = br.ReadUInt32();
            UInt32 state_id = br.ReadUInt32();
            numBytes += 8;
          } else if (type == 0x19) {
            UInt32 switch_group_id = br.ReadUInt32();
            UInt32 switch_id = br.ReadUInt32();
            numBytes += 8;
          }
          */
          break;

        case 4:
          _numEvents = br.ReadUInt32();

          if (_numEvents > 0) {
            _eventActions = new List<UInt32>();

            for (Int32 count = 0; count < _numEvents; count++) {
              _eventActions.Add(br.ReadUInt32());
            }
          }

          break;

        case 10:
          Int64 before = br.BaseStream.Position;
          UInt32 numChild = br.ReadUInt32();

          if (numChild > 0) {
            _audioIds = new List<UInt32>();

            for (Int32 count = 0; count < numChild; count++) {
              _audioIds.Add(br.ReadUInt32());
            }
          }

          Int64 after = br.BaseStream.Position;
          Int64 diff = after - before + 4;
          br.BaseStream.Seek(_length - diff, SeekOrigin.Current);
          break;

        case 11:
          br.ReadBytes(8);
          br.ReadBoolean();
          br.ReadBytes(3);
          AudioSourceId = br.ReadUInt32();
          AudioId = br.ReadUInt32();
          br.BaseStream.Seek(_length - 24, SeekOrigin.Current);
          break;

        default:
          // Skipping other HIRC Types
          br.BaseStream.Seek(_length - 4, SeekOrigin.Current);
          break;
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
    // private UInt32 _id;
    private readonly Byte _nameLength;
    private readonly Char[] _nameTemp;

    internal String Name { get; set; }

    internal FileFormat_BNK_STID_SoundBank(BinaryReader br) {
      // _id = 
      br.ReadUInt32();
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
    internal void ParseBNK(Stream fileStream, String _) {
      using BinaryReader br = new BinaryReader(fileStream);
      FileFormat_BNK bnk = new FileFormat_BNK(br);

      Boolean isBeta = bnk.BKHD != null && bnk.BKHD.Version == 56;
      String streamedRoot = isBeta ? "/resources/bnk/streamed/" : "/resources/bnk2/streamed/";
      String streamedExtension = isBeta ? ".ogg" : ".wem";
      String bankRoot = isBeta ? "/resources/bnk/" : "/resources/bnk2/";

      if (bnk.HIRC != null) {
        if (bnk.HIRC.NumObject != 0) {
          foreach (var obj in bnk.HIRC.Objects) {
            if (obj.Type == 2) {
              if (obj.Embed != 0) {
                if (obj.AudioId != 0)
                  _fileNames.Add(streamedRoot + obj.AudioId + streamedExtension);

                if (obj.AudioSourceId != 0)
                  _fileNames.Add(streamedRoot + obj.AudioSourceId + streamedExtension);
              }

            } else if (obj.Type == 11) {
              if (obj.AudioId != 0)
                _fileNames.Add(streamedRoot + obj.AudioId + streamedExtension);

              if (obj.AudioSourceId != 0)
                _fileNames.Add(streamedRoot + obj.AudioSourceId + streamedExtension);
            }
          }
        }
      }

      if (bnk.STID != null) {
        if (bnk.STID.NumSoundBanks != 0) {
          foreach (var obj in bnk.STID.SoundBanks) {
            _fileNames.Add(bankRoot + obj.Name + ".bnk");
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
