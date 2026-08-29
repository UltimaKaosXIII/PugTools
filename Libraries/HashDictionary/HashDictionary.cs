/******************************************************************************
 * This file only creates a list of hashes which can then be searched based on 
 * a text file which should be placed in Hash/hashes_filename.txt
 * 
 * 
 * 
 * Chryso
 *****************************************************************************/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;

namespace nsHashDictionary {
  public enum DictionaryState {
    Building,
    Finished
  }

  public enum UpdateResults {
    NOT_FOUND,
    UPTODATE,
    NAME_UPDATED,
    ARCHIVE_UPDATED
  }

  public class HashData {
    public String ArchiveName { get; internal set; }
    public String FileName { get; set; }
    public Int32 Crc { get; internal set; }
    public UInt32 Ph { get; }
    public UInt32 Sh { get; }

    public HashData(UInt32 ph, UInt32 sh, String filename, Int32 crc, String archiveName) {
      Ph = ph;
      Sh = sh;
      FileName = filename;
      Crc = crc;
      ArchiveName = archiveName;
    }
  }

  internal delegate void DictionaryEventHandler(Object sender, DictionaryEventArgs e);

  public class HashDictionary {

    #region Constructors

    /// <summary>
    /// Creates a new hasher.
    /// </summary>
    public HashDictionary() {
      m_archiveList = new SortedList<Int16, String>();
      m_archiveReverseList = new SortedList<String, Int16>();
      m_dirListing = new HashSet<String>();
      m_extListing = new HashSet<String>();
      m_fileListing = new HashSet<String>();
      m_hashList = new SortedList<String, SortedList<UInt64, HashData>>();
      m_masterArchiveHashList = new Dictionary<UInt64, HashSet<String>>();
    }

    #endregion

    #region Fields
    private readonly SortedList<Int16, String> m_archiveList;
    private readonly SortedList<String, Int16> m_archiveReverseList;
    private readonly HashSet<String> m_dirListing;
    private readonly HashSet<String> m_extListing;
    private readonly HashSet<String> m_fileListing;
    private const String m_hashFile = "hashes_filename.bin";
    private readonly SortedList<String, SortedList<UInt64, HashData>> m_hashList;
    private Boolean m_helpersCreated;
    private readonly Dictionary<UInt64, HashSet<String>> m_masterArchiveHashList;

    #endregion Fields

    #region Hash Event
    internal event DictionaryEventHandler HashEvent;

    private void OnHashEvent(DictionaryEventArgs e) {
      HashEvent?.Invoke(this, e);
    }

    #endregion Hash Event

    #region Methods
    private void AddHash(UInt32 ph, UInt32 sh, String name, Int32 crc, Int16 archive) {
      AddHash(ph, sh, name, crc, m_archiveList[archive]);
    }

    /// <summary>
    /// Add/update a hash entry
    /// </summary>
    public void AddHash(UInt32 ph, UInt32 sh, String name, Int32 crc, String archiveName) {
      UInt64 sig = (UInt64)ph << 32 | sh;

      AddArchiveHashToMaster(sig, archiveName);

      if (!m_hashList[archiveName].ContainsKey(sig)) {
        m_hashList[archiveName].Add(sig, new HashData(ph, sh, name, crc, archiveName));

        NeedsSave = true;

        if (name.CompareTo("") != 0) {
          AddDirectory(name);
          AddFileandExtension(name);
        }
      } else {
        UpdateHash(ph, sh, name, crc, archiveName);
      }
    }

    private void AddArchiveHashToMaster(UInt64 sig, String archiveName) {
      if (m_masterArchiveHashList.ContainsKey(sig)) {
        m_masterArchiveHashList[sig].Add(archiveName);
      } else {
        m_masterArchiveHashList.Add(sig, new HashSet<String>() { archiveName });
      }
    }

    /// <summary>
    /// Adds a directory to the directory list Used for generation purposes
    /// </summary>
    /// <param name="fileName"></param>
    private void AddDirectory(String fileName) {
      String file = fileName.Replace('\\', '/');

      if (file.Contains('/') && file.LastIndexOf('.') >= 0) {
        String dir = file[..fileName.LastIndexOf('/')];

        // why this check?
        if (dir.IndexOf(' ') < 0) {
          // we check explicitly to cut the loop if the root of the directory is already known.
          while (!m_dirListing.Contains(dir)) {
            m_dirListing.Add(dir);

            if (dir.LastIndexOf('/') >= 0) {
              dir = dir[..dir.LastIndexOf('/')];
            } else {
              break;
            }
          }
        }
      } else if (file.IndexOf(' ') < 0) {
        m_dirListing.Add(file);
      }
    }

    /// <summary>
    /// Adds an extension to the extension list
    /// Used for generation purposes
    /// </summary>
    /// <param name="filename"></param>
    private void AddExtension(String filename) {
      if (filename.Contains(".", StringComparison.CurrentCulture)) {
        String ext = filename[(filename.LastIndexOf('.') + 1)..];
        m_extListing.Add(ext);
      }
    }

    /// <summary>
    /// Adds a filename to the filename list without extension, if there is an extension it is 
    /// removed. Used for generation purposes
    /// </summary>
    /// <param name="filename"></param>
    private void AddFileandExtension(String filename) {
      String cur_fn = filename.Replace('\\', '/');

      if (cur_fn.Contains('/')) {
        cur_fn = cur_fn[(cur_fn.LastIndexOf('/') + 1)..];
        if (cur_fn.Contains(".")) {
          cur_fn = cur_fn[..cur_fn.LastIndexOf('.')];
        };
      }

      m_fileListing.Add(cur_fn);

      AddExtension(filename);
    }

    public void CreateArchiveHashMasterList() {
      foreach (KeyValuePair<String, SortedList<UInt64, HashData>> hashList in m_hashList) {
        foreach (UInt64 sig in hashList.Value.Keys) {
          AddArchiveHashToMaster(sig, hashList.Key);
        }
      }
    }

    public void CreateHelpers() {
      // Check if this is not already created
      if (!m_helpersCreated) {
        SortedList<UInt64, HashData> subHashList;

        for (Int32 j = 0; j < m_hashList.Count; j++) {
          subHashList = m_hashList.Values[j];

          for (Int32 i = 0; i < subHashList.Count; i++) {
            AddDirectory(subHashList.Values[i].FileName);
            AddFileandExtension(subHashList.Values[i].FileName);
          }
        }

        m_helpersCreated = true;
      }
    }

    /// <summary>
    /// Creates a sorted list based on the dictionary file
    /// </summary>
    public void LoadBinaryHashList() {
      Assembly assembly = Assembly.GetEntryAssembly();
      String path = Path.GetDirectoryName(assembly.Location);
      String parentPath = Directory.GetParent(path).FullName;

      String fullPath = File.Exists($"{parentPath}\\PugTools.exe")
        ? $"{parentPath}\\Hash\\"
        : $"{path}\\Hash\\";

      String filePath = $"{fullPath}{m_hashFile}.gz";

      if (File.Exists(filePath)) {
        FileStream fs = new FileStream(filePath, FileMode.Open);

        using (GZipStream gzip = new GZipStream(fs, CompressionMode.Decompress)) {
          using (MemoryStream ms = new MemoryStream()) {
            gzip.CopyTo(ms);

            ms.Position = 0;

            using (BinaryReader br = new BinaryReader(ms)) {
              Int32 i = 0;
              Int32 magic = 0x32736168; // has2
              UInt32 test = br.ReadUInt32();

              if (test != magic) {
                return;
              }

              Int16 archives = br.ReadInt16();

              while (archives > 0) {
                Int16 id = br.ReadInt16();
                String archiveName = br.ReadString();
                m_archiveList[id] = archiveName;
                m_archiveReverseList[archiveName] = id;
                archives--;
              }

              while (br.BaseStream.Position != br.BaseStream.Length) {
                i++;

                UInt32 ph = br.ReadUInt32();
                UInt32 sh = br.ReadUInt32();
                Int32 crc = br.ReadInt32();
                Int16 archiveId = br.ReadInt16();
                String fileName = br.ReadString();

                // LoadHash(ph, sh, fileName, crc, archiveId);
                LoadHashFile(ph, sh, fileName, crc, archiveId);

                if (i % 200 == 0) {
                  Single percentProgress = br.BaseStream.Position / br.BaseStream.Length;
                  OnHashEvent(
                    new DictionaryEventArgs(DictionaryState.Building, percentProgress));
                }
              }
            }
          }
        }
      }

      OnHashEvent(new DictionaryEventArgs(DictionaryState.Finished, 100F));
    }

    private void LoadHashFile(UInt32 ph, UInt32 sh, String fileName, Int32 crc, Int16 archiveId) {
      LoadHashFile(ph, sh, fileName, crc, m_archiveList[archiveId]);
    }

    private void LoadHashFile(UInt32 ph, UInt32 sh, String fileName, Int32 crc, String archive) {
      UInt64 sig = (UInt64)ph << 32 | sh;

      if (!m_hashList.ContainsKey(archive)) {
        m_hashList.Add(archive, new SortedList<UInt64, HashData>());
      }

      m_hashList[archive].Add(sig, new HashData(ph, sh, fileName, crc, archive));
    }

    /// <summary>
    /// Saves the hashlist to a new binary files
    /// </summary>
    public void SaveBinaryHashList() {
      Assembly assembly = Assembly.GetEntryAssembly();
      String path = Path.GetDirectoryName(assembly.Location);
      String parentPath = Directory.GetParent(path).FullName;

      String fullPath = File.Exists($"{parentPath}\\PugTools.exe") ? parentPath : path;

      DateTime centuryBegin = new DateTime(2001, 1, 1);
      DateTime currentDate = DateTime.Now;
      Int64 elapsedTicks = currentDate.Ticks - centuryBegin.Ticks;
      TimeSpan elapsedSpan = new TimeSpan(elapsedTicks);

      if (!Directory.Exists($"{fullPath}\\Hash")) {
        _ = Directory.CreateDirectory($"{fullPath}\\Hash");
      }

      String dictFile = $"{fullPath}\\Hash\\{m_hashFile}";

      // Save dictionary
      String gFile = $"{dictFile}.gz";

      if (File.Exists(gFile)) {
        File.Move(gFile, $"{fullPath}\\Hash\\oldHashList_{elapsedSpan.TotalSeconds}.bin.gz");
      }

      using (FileStream fs = new FileStream(dictFile, FileMode.OpenOrCreate)) {
        using (BinaryWriter bw = new BinaryWriter(fs)) {
          bw.Write(0x32736168); // magic
          bw.Write((Int16)m_hashList.Count);

          Dictionary<String, Int16> reverseHashList = new Dictionary<String, Int16>();

          foreach (KeyValuePair<String, SortedList<UInt64, HashData>> archive in m_hashList) {
            Int16 id = (Int16)m_hashList.IndexOfKey(archive.Key);

            bw.Write(id);
            bw.Write(archive.Key);
            reverseHashList.Add(archive.Key, id);
          }

          SortedList<UInt64, HashData> subHashList;

          for (Int32 i = 0; i < m_hashList.Count; i++) {
            subHashList = m_hashList.Values[i];

            for (Int32 j = 0; j < subHashList.Count; j++) {
              bw.Write((UInt32)(subHashList.Keys[j] >> 32)); // Primary Hash
              bw.Write((UInt32)(subHashList.Keys[j] & 0xFFFFFFFF)); // Secondary hash
              bw.Write(subHashList.Values[j].Crc); // CRC

              if (reverseHashList.TryGetValue(subHashList.Values[j].ArchiveName, out Int16 id)) {
                bw.Write(id); // Archive Id
              }

              bw.Write(subHashList.Values[j].FileName);

              if (j % 200 == 0) {
                OnHashEvent(
                  new DictionaryEventArgs(DictionaryState.Building, j / m_hashList.Count));
              }
            }
          }
        }
      }

      using (FileStream readFS = new FileStream(dictFile, FileMode.Open, FileAccess.Read)) {
        if (readFS == null) {
          return;
        }

        if (readFS.Length == 0) {
          return;
        }

        String filePath = String.Join("", dictFile, ".gz");

        using (FileStream outFS = new FileStream(filePath, FileMode.Create, FileAccess.Write)) {
          using (GZipStream gzip = new GZipStream(outFS, CompressionMode.Compress)) {
            readFS.CopyTo(gzip);
          }
        }
      }

      File.Delete(dictFile);
      OnHashEvent(new DictionaryEventArgs(DictionaryState.Finished, 100f));
    }

    public void SaveTextHashList() {
      Assembly assembly = Assembly.GetEntryAssembly();
      String path = Path.GetDirectoryName(assembly.Location);
      String parentPath = Directory.GetParent(path).FullName;

      String fullPath = File.Exists($"{parentPath}\\PugTools.exe") ? parentPath : path;

      DateTime centuryBegin = new DateTime(2001, 1, 1);
      DateTime currentDate = DateTime.Now;
      Int64 elapsedTicks = currentDate.Ticks - centuryBegin.Ticks;
      TimeSpan elapsedSpan = new TimeSpan(elapsedTicks);

      if (!Directory.Exists($"{fullPath}\\Hash")) {
        _ = Directory.CreateDirectory($"{fullPath}\\Hash");
      }

      String dictFile = $"{fullPath}\\Hash\\hashes_filename.txt";

      if (File.Exists(dictFile)) {
        File.Move(dictFile, $"{fullPath}\\Hash\\oldHashList_{elapsedSpan.TotalSeconds}.txt");
      }

      using (FileStream fs = new FileStream(dictFile, FileMode.OpenOrCreate)) {
        using (StreamWriter writer = new StreamWriter(fs)) {
          SortedList<UInt64, HashData> hashList;

          for (Int32 j = 0; j < m_hashList.Count; j++) {
            hashList = m_hashList.Values[j];
            for (Int32 i = 0; i < hashList.Count; i++) {
              writer.WriteLine("{0:X8}" + '#' + "{1:X8}" + '#' + "{2}" + '#' + "{3:X8}",
                               (UInt32)(hashList.Keys[i] >> 32),
                               (UInt32)(hashList.Keys[i] & 0xFFFFFFFF),
                               hashList.Values[i].FileName,
                               hashList.Values[i].Crc);

              if (i % 200 == 0)
                HashEvent?.Invoke(this,
                                  new DictionaryEventArgs(DictionaryState.Building,
                                                          i / (Single)m_hashList.Count));
            }
          }
        }
      }
    }

    /// <summary>
    /// Searches in all the archives hashlists
    /// </summary>
    /// <param name="ph"></param>
    /// <param name="sh"></param>
    /// <returns>returns the HashData object or null</returns> 
    public HashData SearchHashList(UInt32 ph, UInt32 sh) {
      UInt64 sig = (UInt64)ph << 32 | sh;
      HashData result = null;

      for (Int32 i = 0; i < m_hashList.Count; i++) {
        if (m_hashList.Values[i].ContainsKey(sig)) {
          result = m_hashList.Values[i][sig];
          break;
        }
      }

      return result;
    }

    /// <summary>
    /// Searches in all the archives hashlists
    /// </summary>
    /// <param name="ph"></param>
    /// <param name="sh"></param>
    /// <returns>returns the HashData object or null</returns> 
    public HashData SearchHashList(UInt32 ph, UInt32 sh, String archiveName) {
      UInt64 sig = (UInt64)ph << 32 | sh;

      if (!m_hashList.ContainsKey(archiveName)) {
        m_hashList.Add(archiveName, new SortedList<UInt64, HashData>());
      }

      if (m_hashList[archiveName].ContainsKey(sig)) {
        return m_hashList[archiveName][sig];
      }

      return null;
    }

    /// <summary>
    /// Enumerates the known named files for one physical TOR archive.
    /// This is used by legacy clients that predate manifest files (for example beta string tables).
    /// The returned HashData objects are the dictionary entries themselves and must not be modified.
    /// </summary>
    public IEnumerable<HashData> EnumerateArchiveFiles(String archiveName) {
      if (String.IsNullOrWhiteSpace(archiveName)) yield break;
      if (!m_hashList.TryGetValue(archiveName, out SortedList<UInt64, HashData> archiveHashes) ||
          archiveHashes == null) yield break;

      foreach (HashData data in archiveHashes.Values) {
        if (data != null) yield return data;
      }
    }

    public void UpdateCRC(UInt32 ph, UInt32 sh, Int32 crc, String archiveName) {
      UInt64 sig = (UInt64)ph << 32 | sh;

      if (m_hashList[archiveName].ContainsKey(sig)) {
        m_hashList[archiveName][sig].Crc = crc;
        NeedsSave = true;
      }
    }

    /// <summary>
    /// Update hash with name if the hash can be found in the hash list
    /// This is used for generation purposes
    /// </summary>
    /// <param name="ph">ph value</param>
    /// <param name="sh">sh value</param>
    /// <param name="name">equivalent of the hash as a string</param>
    /// <param name="archive">the name of the archive in which to look / update</param>
    /// <returns>0=not found, 1=already up-to-date, 2= name updated, 3=archive updated</returns>
    public UpdateResults UpdateHash(UInt32 ph, UInt32 sh, String name, Int32 crc, String archive) {
      UInt64 sig = (UInt64)ph << 32 | sh;
      UpdateResults result = UpdateResults.NOT_FOUND;

      // If the list contains the sig, then we update
      if (m_hashList[archive].ContainsKey(sig)) {
        result = UpdateResults.UPTODATE;

        if (!String.IsNullOrEmpty(name) && m_hashList[archive][sig].FileName != name) {
          // Updates the filename if it has changed
          m_hashList[archive][sig].FileName = name;
          result = UpdateResults.NAME_UPDATED;

          AddDirectory(name);
          AddFileandExtension(name);

          NeedsSave = true;
        }

        if (archive != m_hashList[archive][sig].ArchiveName) {
          // Updates the archivename if the file has switched archive
          m_hashList[archive][sig].ArchiveName = archive;
          result = UpdateResults.ARCHIVE_UPDATED;
          NeedsSave = true;
        }

        if (crc != 0) {
          m_hashList[archive][sig].Crc = crc;
          NeedsSave = true;
        }
      }
      return result;
    }

    /// <summary>
    /// Lookup in all the archives if a hash matches
    /// Update hash with name if the hash can be found in the hash list
    /// This is used for generation purposes
    /// </summary>
    /// <param name="ph">ph value</param>
    /// <param name="sh">sh value</param>
    /// <param name="name">equivalent of the hash as a string</param>
    /// <returns>0=not found, 1=already up-to-date, 2= name updated, 3=archive updated</returns>
    public List<UpdateResults> UpdateHash(UInt32 ph,
                                          UInt32 sh,
                                          String name,
                                          Int32 crc,
                                          Boolean updateOnly = false) {
      UInt64 sig = (UInt64)ph << 32 | sh;
      List<UpdateResults> result = new List<UpdateResults>();

      m_masterArchiveHashList.TryGetValue(sig, out HashSet<String> archives);

      if (archives != null) {
        foreach (String arch in archives) {
          UpdateResults upd = UpdateHash(ph, sh, name, crc, arch);

          if (updateOnly) {
            if ((Int32)upd > 1) result.Add(upd);
          } else {
            result.Add(upd);
          }
        }
      }

      return result;
    }

    #endregion Methods

    #region Properties
    public Boolean NeedsSave { get; private set; }

    #endregion Properties

  }

  internal class DictionaryEventArgs : EventArgs {
    public Single Value { get; }
    public DictionaryState State { get; }

    public DictionaryEventArgs(DictionaryState state, Single value) {
      State = state;
      Value = value;
    }
  }
}
