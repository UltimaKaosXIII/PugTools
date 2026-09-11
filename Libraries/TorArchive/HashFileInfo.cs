using System;
using System.Linq;

using nsHashDictionary;

namespace TorArchive {
  public class HashFileInfo {

    public enum State {
      New,
      Modified,
      Unchanged
    }

    #region Constructors
    public HashFileInfo(UInt32 ph, UInt32 sh, File file)
      : this(ph, sh, file, true) {
    }

    /// <summary>
    /// Creates hash metadata without probing file contents when detectUnknownExtension is false.
    /// Browser indexes use this overload to avoid opening/decompressing unknown TOR entries at startup.
    /// </summary>
    public HashFileInfo(UInt32 ph, UInt32 sh, File file, Boolean detectUnknownExtension)
      : this(ph, sh, file, detectUnknownExtension, true) {
    }

    /// <summary>
    /// Creates hash metadata and optionally avoids updating the persistent hash dictionary CRC.
    /// Build-to-build comparisons use updateDictionary=false so inspecting an older build cannot
    /// make that older CRC become the new baseline.
    /// </summary>
    public HashFileInfo(UInt32 ph, UInt32 sh, File file, Boolean detectUnknownExtension,
                        Boolean updateDictionary) {
      if (ph == 0 && sh == 0 && file == null) {
        return;
      }

      FileInfo info = file.FileInfo;
      nsHashDictionary.HashDictionary dictionary = HashDictionaryInstance.Instance.Dictionary;
      HashData archiveData = dictionary.SearchHashList(ph, sh, file.Archive.StrippedFileName);

      // A resource path hashes to the same PH/SH regardless of which physical TOR filename a
      // particular SWTOR build uses. Beta/dev clients have used main_1.tor, assets_main_*.tor,
      // red_*.tor and other layouts for the same named resources. Keep exact archive data for
      // CRC/change tracking, but fall back to the global PH/SH name when this archive alias is
      // absent from the filename pack.
      HashData nameData = archiveData;
      if (nameData == null || String.IsNullOrEmpty(nameData.FileName)) {
        HashData globalData = dictionary.SearchHashList(ph, sh);
        if (globalData != null && !String.IsNullOrEmpty(globalData.FileName)) nameData = globalData;
      }

      _FileRef = file;
      Source = file.Archive.FileName.Split('\\').Last();
      if (nameData != null) FirstSeenVersion = nameData.FirstSeenVersion;

      if (nameData != null && nameData.FileName.Length > 0) {
        IsNamed = true;
        FileName = nameData.FileName;
        Extension = FileName.Split('.').Last();

        String[] temp = FileName.Split('/');

        Directory = String.Join("/", temp.Take(temp.Length - 1));
        FileName = temp.Last();

        if (archiveData == null) {
          // The name came from another archive family/version. Do not copy that archive's CRC
          // into this one and do not create a duplicate PFD1 row merely to preserve the name.
          FileState = State.New;
        } else if (info.CRC != archiveData.Crc) {
          FileState = State.Modified;
          if (updateDictionary)
            dictionary.UpdateCRC(info.PrimaryHash,
                                 info.SecondaryHash,
                                 info.CRC,
                                 file.Archive.StrippedFileName);
        } else {
          FileState = State.Unchanged;
        }
      } else {
        IsNamed = false;
        Directory = "/" + Source;
        Extension = detectUnknownExtension ? FileExtension.Instance.GuessExtension(file) : "";

        if (archiveData == null) {
          FileState = State.New;
          FileName = $"{info.Checksum:X8}_{info.FileId:X16}";
          if (updateDictionary)
            dictionary.AddHash(info.PrimaryHash,
                               info.SecondaryHash,
                               "",
                               info.CRC,
                               file.Archive.StrippedFileName);
        } else if (info.CRC != archiveData.Crc) {
          FileState = State.Modified;
          if (updateDictionary)
            dictionary.UpdateCRC(info.PrimaryHash,
                                 info.SecondaryHash,
                                 info.CRC,
                                 file.Archive.StrippedFileName);
        } else {
          FileState = State.Unchanged;
        }

        if (FileName == null) {
          FileName = $"{info.Checksum:X8}_{info.FileId:X16}";
        }
      }
    }

    #endregion Constructors

    #region Fields
    private File _FileRef;

    #endregion Fields

    #region Properties
    public String Directory { get; set; }
    public String Extension { get; private set; }
    public File File {
      get => _FileRef;
      set => _FileRef = value;
    }
    public String FileName { get; private set; }
    /// <summary>Earliest patch known from the compact hash-history index, if available.</summary>
    public String FirstSeenVersion { get; private set; }
    public State FileState { get; private set; }
    public Boolean IsNamed { get; private set; }
    public String Source { get; private set; }

    #endregion Properties
  }
}
