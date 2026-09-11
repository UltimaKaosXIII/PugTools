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
    private String m_fileName;
    private CompactFileNameStore m_nameStore;
    private Int32 m_nameIndex = -1;

    public String ArchiveName { get; internal set; }
    public String FileName {
      get {
        String current = System.Threading.Volatile.Read(ref m_fileName);
        if (current != null) return current;

        // Multiple browser workers can resolve the same lazy filename at the same time. Decode
        // from the immutable shared store and publish exactly one string without clearing the
        // backing store out from underneath another reader.
        String decoded = m_nameStore?.GetName(m_nameIndex) ?? String.Empty;
        String previous = System.Threading.Interlocked.CompareExchange(ref m_fileName, decoded, null);
        return previous ?? decoded;
      }
      set {
        System.Threading.Interlocked.Exchange(ref m_fileName, value ?? String.Empty);
        m_nameStore = null;
        m_nameIndex = -1;
      }
    }

    // Serialization should not permanently materialize every lazy filename just because the
    // user saves the dictionary. The returned string may be temporary and is intentionally not
    // cached in m_fileName.
    internal String FileNameForSerialization =>
      m_fileName ?? m_nameStore?.GetName(m_nameIndex) ?? String.Empty;
    public Int32 Crc { get; internal set; }
    /// <summary>Earliest reliably known SWTOR patch for this hash, or null when history is unavailable.</summary>
    public String FirstSeenVersion { get; internal set; }
    public UInt32 Ph { get; }
    public UInt32 Sh { get; }

    public HashData(UInt32 ph, UInt32 sh, String filename, Int32 crc, String archiveName) {
      Ph = ph;
      Sh = sh;
      m_fileName = filename ?? String.Empty;
      Crc = crc;
      ArchiveName = archiveName;
    }

    internal HashData(UInt32 ph, UInt32 sh, CompactFileNameStore nameStore, Int32 nameIndex,
                      Int32 crc, String archiveName) {
      Ph = ph;
      Sh = sh;
      m_nameStore = nameStore;
      m_nameIndex = nameIndex;
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
      m_runtimeFileNameChanges = new HashSet<String>(StringComparer.OrdinalIgnoreCase);
    }

    #endregion

    #region Fields
    private readonly SortedList<Int16, String> m_archiveList;
    private readonly SortedList<String, Int16> m_archiveReverseList;
    private readonly HashSet<String> m_dirListing;
    private readonly HashSet<String> m_extListing;
    private readonly HashSet<String> m_fileListing;
    private CompactFileNameStore m_compactNameStore;
    private const String m_hashFile = "hashes_filename.bin";
    private const String m_compactHashFile = "hashes_filename.pfd1";
    private const UInt32 CompactMagic = 0x31444650; // PFD1
    private const UInt16 CompactVersion = 2;
    private readonly SortedList<String, SortedList<UInt64, HashData>> m_hashList;
    private Boolean m_helpersCreated;
    private readonly Dictionary<UInt64, HashSet<String>> m_masterArchiveHashList;
    private readonly HashSet<String> m_runtimeFileNameChanges;
    private Boolean m_masterArchiveHashListCreated;
    // Asset/Model/Node browsers share this dictionary. Protect the mutable SortedLists so two
    // browser workers can resolve/add hashes concurrently without corrupting collection state.
    private readonly Object m_hashListLock = new Object();
    private readonly Object m_pendingFileNameLock = new Object();
    private readonly HashSet<UInt64> m_pendingFileNameHashes = new HashSet<UInt64>();

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
      lock (m_hashListLock) {
        UInt64 sig = (UInt64)ph << 32 | sh;

        AddArchiveHashToMaster(sig, archiveName);

        if (!m_hashList.ContainsKey(archiveName))
          m_hashList.Add(archiveName, new SortedList<UInt64, HashData>());

        if (!m_hashList[archiveName].ContainsKey(sig)) {
          m_hashList[archiveName].Add(sig, new HashData(ph, sh, name, crc, archiveName));

          NeedsSave = true;

          if (!String.IsNullOrEmpty(name)) {
            m_runtimeFileNameChanges.Add(name.Replace('\\', '/'));
            MarkFileNameChanged(sig);
            AddDirectory(name);
            AddFileandExtension(name);
          }
        } else {
          UpdateHash(ph, sh, name, crc, archiveName);
        }
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
      lock (m_hashListLock) {
        if (m_masterArchiveHashListCreated) return;

        // AddHash() keeps this index current after it has been built. Before the first full build it
        // may contain only hashes added during this process, so always rebuild once from the source
        // dictionary instead of repeatedly walking millions of rows for every filename test file.
        m_masterArchiveHashList.Clear();
        foreach (KeyValuePair<String, SortedList<UInt64, HashData>> hashList in m_hashList) {
          foreach (UInt64 sig in hashList.Value.Keys) {
            AddArchiveHashToMaster(sig, hashList.Key);
          }
        }
        m_masterArchiveHashListCreated = true;
      }
    }

    private void MarkFileNameChanged(UInt64 sig) {
      lock (m_pendingFileNameLock) m_pendingFileNameHashes.Add(sig);
      NeedsSave = true;
    }

    public void CreateHelpers() {
      lock (m_hashListLock) {
        // Check if this is not already created. Helper collections are shared by every browser, so
        // build them under the same lock as filename mutations to avoid concurrent HashSet writes.
        if (!m_helpersCreated) {
          SortedList<UInt64, HashData> subHashList;

          for (Int32 j = 0; j < m_hashList.Count; j++) {
            subHashList = m_hashList.Values[j];

            for (Int32 i = 0; i < subHashList.Count; i++) {
              // Helper generation may touch millions of rows. Read lazy PFD1 names transiently so
              // this maintenance operation does not permanently inflate every HashData into a String.
              String fileName = subHashList.Values[i].FileNameForSerialization;
              AddDirectory(fileName);
              AddFileandExtension(fileName);
            }
          }

          m_helpersCreated = true;
        }
      }
    }

    private static String GetHashDirectory() {
      Assembly assembly = Assembly.GetEntryAssembly();
      String path = Path.GetDirectoryName(assembly.Location);
      String parentPath = Directory.GetParent(path).FullName;
      return File.Exists($"{parentPath}\\PugTools.exe")
        ? $"{parentPath}\\Hash\\"
        : $"{path}\\Hash\\";
    }

    /// <summary>
    /// Loads the filename dictionary. PFD1 is preferred because it keeps all names in one
    /// front-coded pool and gives each HashData a lazy name reference. The legacy has2 gzip
    /// remains a fully compatible fallback for user dictionaries created by older PugTools.
    /// </summary>
    public void LoadBinaryHashList() {
      String fullPath = GetHashDirectory();
      String compactPath = $"{fullPath}{m_compactHashFile}.gz";
      String legacyPath = $"{fullPath}{m_hashFile}.gz";

      Boolean loaded = false;
      Boolean compactIsCurrent = File.Exists(compactPath)
        && (!File.Exists(legacyPath)
            || File.GetLastWriteTimeUtc(compactPath) >= File.GetLastWriteTimeUtc(legacyPath));
      if (compactIsCurrent) {
        try {
          loaded = LoadCompactHashList(compactPath);
        }
        catch (Exception ex) when (ex is InvalidDataException || ex is EndOfStreamException
                                   || ex is IOException) {
          Debug.WriteLine("Unable to read compact PFD1 hash dictionary: " + ex.Message);
          ClearLoadedHashData();
        }
      }

      if (!loaded && File.Exists(legacyPath)) {
        LoadLegacyHashList(legacyPath);
        loaded = true;
      }

      OnHashEvent(new DictionaryEventArgs(DictionaryState.Finished, loaded ? 100F : 0F));
    }

    private void ClearLoadedHashData() {
      m_archiveList.Clear();
      m_archiveReverseList.Clear();
      m_hashList.Clear();
      m_masterArchiveHashList.Clear();
      m_dirListing.Clear();
      m_extListing.Clear();
      m_fileListing.Clear();
      m_compactNameStore = null;
      m_runtimeFileNameChanges.Clear();
      m_helpersCreated = false;
      m_masterArchiveHashListCreated = false;
      lock (m_pendingFileNameLock) m_pendingFileNameHashes.Clear();
      NeedsSave = false;
    }

    private Boolean LoadCompactHashList(String filePath) {
      using FileStream fs = new FileStream(
        filePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1024 * 128, FileOptions.SequentialScan
      );
      using GZipStream gzip = new GZipStream(fs, CompressionMode.Decompress);
      using BinaryReader br = new BinaryReader(gzip, System.Text.Encoding.UTF8, false);

      UInt32 magic = br.ReadUInt32();
      if (magic != CompactMagic) return false;
      UInt16 version = br.ReadUInt16();
      UInt16 flags = br.ReadUInt16();
      // v1 was an internal preview of PFD1. Its public-patch firstSeen inference treated
      // assets_* archive counters as live releases, but Jedipedia's archive reader confirms
      // assets_*, he32_* and red_* are beta environments. Keep v1 readable for filename
      // compatibility, but only trust its Beta marker. v2 stores only defensible history.
      if (version != 1 && version != CompactVersion)
        throw new InvalidDataException($"PFD1: unsupported version {version}.");
      if (flags != 0)
        throw new InvalidDataException($"PFD1: unsupported flags 0x{flags:X4}.");

      Int32 archiveCount = br.ReadInt32();
      Int32 nameCount = br.ReadInt32();
      Int32 rowCount = br.ReadInt32();
      Int32 patchCount = br.ReadInt32();
      Int32 poolBytes = br.ReadInt32();
      Int32 suffixBytes = br.ReadInt32();
      Int32 maxNameLength = br.ReadInt32();
      _ = br.ReadInt32(); // reserved
      _ = br.ReadInt32(); // reserved

      if (archiveCount < 0 || archiveCount > Int16.MaxValue || nameCount < 0 || rowCount < 0
          || patchCount < 0 || patchCount > UInt16.MaxValue - 1 || poolBytes < 0
          || suffixBytes < 0 || maxNameLength < 0)
        throw new InvalidDataException("PFD1: invalid header counts.");

      for (Int32 i = 0; i < archiveCount; i++) {
        Int16 id = br.ReadInt16();
        UInt16 byteLength = br.ReadUInt16();
        Byte[] bytes = br.ReadBytes(byteLength);
        if (bytes.Length != byteLength) throw new EndOfStreamException("PFD1: truncated archive table.");
        String archiveName = System.Text.Encoding.UTF8.GetString(bytes);
        m_archiveList[id] = archiveName;
        m_archiveReverseList[archiveName] = id;
      }

      String[] patches = new String[patchCount];
      for (Int32 i = 0; i < patchCount; i++) {
        UInt16 id = br.ReadUInt16();
        UInt16 byteLength = br.ReadUInt16();
        Byte[] bytes = br.ReadBytes(byteLength);
        if (bytes.Length != byteLength) throw new EndOfStreamException("PFD1: truncated patch table.");
        if (id >= patchCount) throw new InvalidDataException("PFD1: patch id outside table.");
        patches[id] = System.Text.Encoding.ASCII.GetString(bytes);
      }

      CompactFileNameStore names = CompactFileNameStore.Read(br, nameCount, poolBytes, suffixBytes);
      if (names.Count != nameCount) throw new InvalidDataException("PFD1: filename count mismatch.");
      m_compactNameStore = names;

      for (Int32 i = 0; i < rowCount; i++) {
        UInt32 ph = br.ReadUInt32();
        UInt32 sh = br.ReadUInt32();
        Int32 crc = br.ReadInt32();
        Int16 archiveId = br.ReadInt16();
        Int32 nameIndex = br.ReadInt32();
        UInt16 firstSeenId = br.ReadUInt16();

        if (!m_archiveList.ContainsKey(archiveId))
          throw new InvalidDataException($"PFD1: row {i} references archive {archiveId}.");
        if (nameIndex < -1 || nameIndex >= nameCount)
          throw new InvalidDataException($"PFD1: row {i} references filename {nameIndex}.");
        if (firstSeenId != UInt16.MaxValue && firstSeenId >= patchCount)
          throw new InvalidDataException($"PFD1: row {i} references patch {firstSeenId}.");

        String firstSeenVersion = firstSeenId == UInt16.MaxValue ? null : patches[firstSeenId];
        if (version == 1 && !String.Equals(firstSeenVersion, "Beta", StringComparison.OrdinalIgnoreCase))
          firstSeenVersion = null;

        LoadHashFile(ph, sh, names, nameIndex, crc, archiveId, firstSeenVersion);

        if ((i & 0x7fff) == 0 && rowCount > 0) {
          OnHashEvent(new DictionaryEventArgs(DictionaryState.Building, i / (Single)rowCount));
        }
      }

      return true;
    }

    private void LoadLegacyHashList(String filePath) {
      using FileStream fs = new FileStream(
        filePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1024 * 128, FileOptions.SequentialScan
      );
      using GZipStream gzip = new GZipStream(fs, CompressionMode.Decompress);
      using BinaryReader br = new BinaryReader(gzip);

      const UInt32 magic = 0x32736168; // has2
      if (br.ReadUInt32() != magic) return;

      Int16 archives = br.ReadInt16();
      while (archives > 0) {
        Int16 id = br.ReadInt16();
        String archiveName = br.ReadString();
        m_archiveList[id] = archiveName;
        m_archiveReverseList[archiveName] = id;
        archives--;
      }

      Int32 i = 0;
      while (true) {
        try {
          UInt32 ph = br.ReadUInt32();
          UInt32 sh = br.ReadUInt32();
          Int32 crc = br.ReadInt32();
          Int16 archiveId = br.ReadInt16();
          String fileName = br.ReadString();
          LoadHashFile(ph, sh, fileName, crc, archiveId);
          i++;

          if (i % 2000 == 0) {
            Single percentProgress = fs.Length > 0
              ? Math.Min(1.0F, (Single)fs.Position / fs.Length)
              : 0.0F;
            OnHashEvent(new DictionaryEventArgs(DictionaryState.Building, percentProgress));
          }
        }
        catch (EndOfStreamException) {
          break;
        }
      }

      DeriveLegacyBetaFirstSeenHistory();
    }

    /// <summary>
    /// The legacy has2 dictionary contains no explicit release timeline. The archive namespace does,
    /// however, provide one trustworthy historical fact: Jedipedia's archive reader classifies
    /// assets_*, he32_* and red_* as beta environments. If a hash occurs in any of those archives,
    /// every duplicate occurrence of that hash can safely be marked as first seen in Beta.
    ///
    /// No public patch is inferred here. Modern themed archives do not encode the patch in which a
    /// file first appeared, and the old patches.xml counters are patcher package counts rather than
    /// a reliable archive-name-to-live-version mapping.
    /// </summary>
    private void DeriveLegacyBetaFirstSeenHistory() {
      var betaHashes = new HashSet<UInt64>();

      foreach (KeyValuePair<String, SortedList<UInt64, HashData>> archive in m_hashList) {
        if (!IsBetaArchiveName(archive.Key)) continue;
        foreach (UInt64 signature in archive.Value.Keys) betaHashes.Add(signature);
      }

      if (betaHashes.Count == 0) return;

      foreach (SortedList<UInt64, HashData> archive in m_hashList.Values) {
        for (Int32 i = 0; i < archive.Count; i++) {
          if (betaHashes.Contains(archive.Keys[i])) archive.Values[i].FirstSeenVersion = "Beta";
        }
      }
    }

    private static Boolean IsBetaArchiveName(String archiveName) {
      if (String.IsNullOrWhiteSpace(archiveName)) return false;
      return archiveName.StartsWith("assets_", StringComparison.OrdinalIgnoreCase)
        || archiveName.StartsWith("he32_", StringComparison.OrdinalIgnoreCase)
        || archiveName.StartsWith("red_", StringComparison.OrdinalIgnoreCase);
    }

    private void LoadHashFile(UInt32 ph, UInt32 sh, String fileName, Int32 crc, Int16 archiveId) {
      LoadHashFile(ph, sh, fileName, crc, m_archiveList[archiveId]);
    }

    private void LoadHashFile(UInt32 ph, UInt32 sh, String fileName, Int32 crc, String archive) {
      UInt64 sig = (UInt64)ph << 32 | sh;
      if (!m_hashList.ContainsKey(archive))
        m_hashList.Add(archive, new SortedList<UInt64, HashData>());
      m_hashList[archive].Add(sig, new HashData(ph, sh, fileName, crc, archive));
    }

    private void LoadHashFile(UInt32 ph, UInt32 sh, CompactFileNameStore names, Int32 nameIndex,
                              Int32 crc, Int16 archiveId, String firstSeenVersion) {
      String archive = m_archiveList[archiveId];
      UInt64 sig = (UInt64)ph << 32 | sh;
      if (!m_hashList.ContainsKey(archive))
        m_hashList.Add(archive, new SortedList<UInt64, HashData>());

      HashData data = nameIndex >= 0
        ? new HashData(ph, sh, names, nameIndex, crc, archive)
        : new HashData(ph, sh, String.Empty, crc, archive);
      data.FirstSeenVersion = firstSeenVersion;
      m_hashList[archive].Add(sig, data);
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

              bw.Write(subHashList.Values[j].FileNameForSerialization);

              if (j % 200 == 0) {
                OnHashEvent(
                  new DictionaryEventArgs(DictionaryState.Building, j / m_hashList.Count));
              }
            }
          }
        }
      }

      using (FileStream readFS = new FileStream(
        dictFile, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete
      )) {
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

      // Also publish the compact PFD1 sibling. Older PugTools can keep using has2 while current
      // builds prefer PFD1 on the next start. Saving the legacy file first deliberately preserves
      // the existing recovery path if compact generation is interrupted.
      SaveCompactHashList($"{fullPath}\\Hash");
      lock (m_pendingFileNameLock) m_pendingFileNameHashes.Clear();
      NeedsSave = false;
      OnHashEvent(new DictionaryEventArgs(DictionaryState.Finished, 100f));
    }

    /// <summary>
    /// Saves only the compact PFD1 dictionary used by current PugTools builds. This deliberately
    /// avoids rewriting and recompressing the legacy has2 file as well, so accepting filename
    /// discoveries at application shutdown performs one full dictionary serialization instead of two.
    /// </summary>
    public void SaveCompactHashListOnly() {
      lock (m_hashListLock) {
        String hashDirectory = GetHashDirectory().TrimEnd('\\', '/');
        if (!Directory.Exists(hashDirectory)) Directory.CreateDirectory(hashDirectory);

        SaveCompactHashList(hashDirectory);
        lock (m_pendingFileNameLock) m_pendingFileNameHashes.Clear();
        NeedsSave = false;
        OnHashEvent(new DictionaryEventArgs(DictionaryState.Finished, 100f));
      }
    }

    private void SaveCompactHashList(String hashDirectory) {
      if (m_hashList.Count > Int16.MaxValue)
        throw new InvalidDataException("PFD1: archive table exceeds the Int16 id range.");

      String rawPath = Path.Combine(hashDirectory, m_compactHashFile);
      String gzipPath = rawPath + ".gz";
      String tempPath = rawPath + ".tmp";
      String tempGzipPath = gzipPath + ".tmp";

      var nameSet = new HashSet<String>(StringComparer.Ordinal);
      var patchSet = new HashSet<String>(StringComparer.OrdinalIgnoreCase);
      Int32 rowCount = 0;
      foreach (SortedList<UInt64, HashData> archive in m_hashList.Values) {
        rowCount += archive.Count;
        foreach (HashData data in archive.Values) {
          String serializedName = data.FileNameForSerialization;
          if (!String.IsNullOrEmpty(serializedName)) nameSet.Add(serializedName);
          if (!String.IsNullOrWhiteSpace(data.FirstSeenVersion)) patchSet.Add(data.FirstSeenVersion);
        }
      }

      var names = new List<String>(nameSet);
      names.Sort(StringComparer.Ordinal);
      var nameIds = new Dictionary<String, Int32>(names.Count, StringComparer.Ordinal);
      for (Int32 i = 0; i < names.Count; i++) nameIds[names[i]] = i;

      var patches = new List<String>(patchSet);
      patches.Sort(StringComparer.OrdinalIgnoreCase);
      if (patches.Count >= UInt16.MaxValue)
        throw new InvalidDataException("PFD1: patch table exceeds the UInt16 id range.");
      var patchIds = new Dictionary<String, UInt16>(patches.Count, StringComparer.OrdinalIgnoreCase);
      for (Int32 i = 0; i < patches.Count; i++) patchIds[patches[i]] = (UInt16)i;

      Int32 poolBytes = 0;
      Int32 suffixBytes = 0;
      Int32 maxNameLength = 0;
      Byte[][] encodedNames = new Byte[names.Count][];
      Byte[] shared = new Byte[names.Count];
      Byte[] previous = Array.Empty<Byte>();
      for (Int32 i = 0; i < names.Count; i++) {
        Byte[] current = System.Text.Encoding.UTF8.GetBytes(names[i]);
        encodedNames[i] = current;
        Int32 prefix = 0;
        Int32 limit = Math.Min(Math.Min(previous.Length, current.Length), Byte.MaxValue);
        while (prefix < limit && previous[prefix] == current[prefix]) prefix++;
        shared[i] = (Byte)prefix;
        poolBytes = checked(poolBytes + current.Length);
        suffixBytes = checked(suffixBytes + current.Length - prefix + 1);
        if (current.Length > maxNameLength) maxNameLength = current.Length;
        previous = current;
      }

      try {
        using (FileStream fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        using (BinaryWriter bw = new BinaryWriter(fs, System.Text.Encoding.UTF8, false)) {
          bw.Write(CompactMagic);
          bw.Write(CompactVersion);
          bw.Write((UInt16)0); // flags
          bw.Write(m_hashList.Count);
          bw.Write(names.Count);
          bw.Write(rowCount);
          bw.Write(patches.Count);
          bw.Write(poolBytes);
          bw.Write(suffixBytes);
          bw.Write(maxNameLength);
          bw.Write(0); // reserved
          bw.Write(0); // reserved

          var reverseArchive = new Dictionary<String, Int16>(m_hashList.Count, StringComparer.Ordinal);
          for (Int32 i = 0; i < m_hashList.Count; i++) {
            String archiveName = m_hashList.Keys[i];
            Int16 id = (Int16)i;
            Byte[] bytes = System.Text.Encoding.UTF8.GetBytes(archiveName);
            if (bytes.Length > UInt16.MaxValue)
              throw new InvalidDataException("PFD1: archive name exceeds 65535 UTF-8 bytes.");
            bw.Write(id);
            bw.Write((UInt16)bytes.Length);
            bw.Write(bytes);
            reverseArchive[archiveName] = id;
          }

          for (Int32 i = 0; i < patches.Count; i++) {
            Byte[] bytes = System.Text.Encoding.ASCII.GetBytes(patches[i]);
            if (bytes.Length > UInt16.MaxValue)
              throw new InvalidDataException("PFD1: patch label exceeds 65535 bytes.");
            bw.Write((UInt16)i);
            bw.Write((UInt16)bytes.Length);
            bw.Write(bytes);
          }

          bw.Write(shared);
          for (Int32 i = 0; i < encodedNames.Length; i++) {
            Byte[] bytes = encodedNames[i];
            Int32 prefix = shared[i];
            bw.Write(bytes, prefix, bytes.Length - prefix);
            bw.Write((Byte)0);
          }

          Int32 written = 0;
          for (Int32 i = 0; i < m_hashList.Count; i++) {
            SortedList<UInt64, HashData> archive = m_hashList.Values[i];
            foreach (KeyValuePair<UInt64, HashData> pair in archive) {
              HashData data = pair.Value;
              bw.Write((UInt32)(pair.Key >> 32));
              bw.Write((UInt32)(pair.Key & 0xFFFFFFFF));
              bw.Write(data.Crc);
              bw.Write(reverseArchive[data.ArchiveName]);
              String serializedName = data.FileNameForSerialization;
              bw.Write(String.IsNullOrEmpty(serializedName) ? -1 : nameIds[serializedName]);
              bw.Write(!String.IsNullOrWhiteSpace(data.FirstSeenVersion)
                       && patchIds.TryGetValue(data.FirstSeenVersion, out UInt16 patchId)
                ? patchId
                : UInt16.MaxValue);

              written++;
              if ((written & 0x7fff) == 0 && rowCount > 0)
                OnHashEvent(new DictionaryEventArgs(DictionaryState.Building, written / (Single)rowCount));
            }
          }
        }

        using (FileStream input = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (FileStream output = new FileStream(tempGzipPath, FileMode.Create, FileAccess.Write, FileShare.None))
        using (GZipStream gzip = new GZipStream(output, CompressionLevel.Optimal)) {
          input.CopyTo(gzip, 1024 * 1024);
        }

        if (File.Exists(gzipPath)) File.Delete(gzipPath);
        File.Move(tempGzipPath, gzipPath);
      }
      finally {
        if (File.Exists(tempPath)) File.Delete(tempPath);
        if (File.Exists(tempGzipPath)) File.Delete(tempGzipPath);
      }
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
                               hashList.Values[i].FileNameForSerialization,
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
      lock (m_hashListLock) {
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
    }

    /// <summary>
    /// Searches in all the archives hashlists
    /// </summary>
    /// <param name="ph"></param>
    /// <param name="sh"></param>
    /// <returns>returns the HashData object or null</returns> 
    public HashData SearchHashList(UInt32 ph, UInt32 sh, String archiveName) {
      lock (m_hashListLock) {
        UInt64 sig = (UInt64)ph << 32 | sh;

        // A lookup must be side-effect free. Older code created a new archive bucket here,
        // which made ordinary browser reads mutate the process-wide dictionary and could race
        // another browser enumerating it.
        if (!m_hashList.ContainsKey(archiveName)) return null;

        if (m_hashList[archiveName].ContainsKey(sig)) {
          return m_hashList[archiveName][sig];
        }

        return null;
      }
    }

    /// <summary>
    /// Enumerates the known named files for one physical TOR archive.
    /// This is used by legacy clients that predate manifest files (for example beta string tables).
    /// The returned HashData objects are the dictionary entries themselves and must not be modified.
    /// </summary>
    public IEnumerable<HashData> EnumerateArchiveFiles(String archiveName) {
      if (String.IsNullOrWhiteSpace(archiveName)) yield break;

      HashData[] snapshot = null;
      lock (m_hashListLock) {
        if (m_hashList.TryGetValue(archiveName, out SortedList<UInt64, HashData> archiveHashes)
            && archiveHashes != null) {
          snapshot = new HashData[archiveHashes.Count];
          archiveHashes.Values.CopyTo(snapshot, 0);
        }
      }
      if (snapshot == null) yield break;

      foreach (HashData data in snapshot) {
        if (data != null) yield return data;
      }
    }

    /// <summary>
    /// Finds known resource paths by prefix without materialising the complete PFD1 filename pool.
    /// Compact dictionaries use a binary search over their sorted unique-name store; the legacy has2
    /// fallback scans existing rows only when no compact store is available.
    /// </summary>
    public IReadOnlyList<String> FindKnownFileNamesByPathPrefix(String pathPrefix) {
      if (String.IsNullOrWhiteSpace(pathPrefix)) return Array.Empty<String>();
      String normalizedPrefix = pathPrefix.Replace('\\', '/').Trim().ToLowerInvariant();

      lock (m_hashListLock) {
        if (m_compactNameStore != null) {
          var compactMatches = new List<String>(m_compactNameStore.FindByPrefix(normalizedPrefix));
          if (m_runtimeFileNameChanges.Count == 0) return compactMatches;
          var seenCompact = new HashSet<String>(compactMatches, StringComparer.OrdinalIgnoreCase);
          foreach (String liveName in m_runtimeFileNameChanges) {
            if (liveName.StartsWith(normalizedPrefix, StringComparison.OrdinalIgnoreCase) && seenCompact.Add(liveName))
              compactMatches.Add(liveName);
          }
          compactMatches.Sort(StringComparer.Ordinal);
          return compactMatches;
        }

        var result = new List<String>();
        var seen = new HashSet<String>(StringComparer.OrdinalIgnoreCase);
        foreach (SortedList<UInt64, HashData> archive in m_hashList.Values) {
          for (Int32 i = 0; i < archive.Count; i++) {
            HashData data = archive.Values[i];
            if (data == null) continue;
            String fileName = data.FileNameForSerialization;
            if (String.IsNullOrWhiteSpace(fileName)) continue;
            String normalizedName = fileName.Replace('\\', '/');
            if (normalizedName.StartsWith(normalizedPrefix, StringComparison.OrdinalIgnoreCase) && seen.Add(normalizedName))
              result.Add(normalizedName);
          }
        }
        result.Sort(StringComparer.Ordinal);
        return result;
      }
    }

    /// <summary>
    /// Finds numeric immediate child directories below a known resource-path prefix. Unlike
    /// FindKnownFileNamesByPathPrefix this does not allocate one String entry for every matching
    /// filename in a compact PFD1 dictionary, which matters for /resources/world/areas/.
    /// </summary>
    public IReadOnlyCollection<UInt64> FindKnownNumericChildIdsByPathPrefix(String pathPrefix) {
      if (String.IsNullOrWhiteSpace(pathPrefix)) return Array.Empty<UInt64>();
      String normalizedPrefix = pathPrefix.Replace('\\', '/').Trim().ToLowerInvariant();
      if (!normalizedPrefix.EndsWith("/", StringComparison.Ordinal)) normalizedPrefix += "/";

      static void TryAddId(String rawName, String prefix, HashSet<UInt64> ids) {
        if (String.IsNullOrWhiteSpace(rawName) || ids == null) return;
        String name = rawName.Replace('\\', '/').Trim();
        if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return;
        Int32 start = prefix.Length;
        Int32 slash = name.IndexOf('/', start);
        if (slash <= start) return;
        if (UInt64.TryParse(name.Substring(start, slash - start), out UInt64 id) && id != 0) ids.Add(id);
      }

      lock (m_hashListLock) {
        var result = new HashSet<UInt64>();
        if (m_compactNameStore != null) {
          foreach (UInt64 id in m_compactNameStore.FindNumericChildIdsByPrefix(normalizedPrefix)) result.Add(id);
          foreach (String liveName in m_runtimeFileNameChanges) TryAddId(liveName, normalizedPrefix, result);
          return new List<UInt64>(result);
        }

        foreach (SortedList<UInt64, HashData> archive in m_hashList.Values) {
          for (Int32 i = 0; i < archive.Count; i++) {
            HashData data = archive.Values[i];
            if (data == null) continue;
            TryAddId(data.FileNameForSerialization, normalizedPrefix, result);
          }
        }
        foreach (String liveName in m_runtimeFileNameChanges) TryAddId(liveName, normalizedPrefix, result);
        return new List<UInt64>(result);
      }
    }

    /// <summary>
    /// Finds already-known names for a small set of hashes without constructing the global
    /// hash-to-archive master index. Filename Finder uses this after a patch so a name that is
    /// already known in an older/other TOR can be copied to the current archive cheaply.
    /// </summary>
    public Dictionary<UInt64, String> FindKnownFileNames(ISet<UInt64> signatures) {
      lock (m_hashListLock) {
        var result = new Dictionary<UInt64, String>();
        if (signatures == null || signatures.Count == 0) return result;

        foreach (SortedList<UInt64, HashData> archive in m_hashList.Values) {
          for (Int32 i = 0; i < archive.Count; i++) {
            UInt64 signature = archive.Keys[i];
            if (!signatures.Contains(signature) || result.ContainsKey(signature)) continue;
            HashData data = archive.Values[i];
            if (data == null) continue;
            String fileName = data.FileNameForSerialization;
            if (!String.IsNullOrWhiteSpace(fileName)) result[signature] = fileName;
          }
          if (result.Count == signatures.Count) break;
        }
        return result;
      }
    }

    public void UpdateCRC(UInt32 ph, UInt32 sh, Int32 crc, String archiveName) {
      lock (m_hashListLock) {
        UInt64 sig = (UInt64)ph << 32 | sh;

        if (m_hashList[archiveName].ContainsKey(sig) && m_hashList[archiveName][sig].Crc != crc) {
          m_hashList[archiveName][sig].Crc = crc;
          NeedsSave = true;
        }
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
      lock (m_hashListLock) {
        UInt64 sig = (UInt64)ph << 32 | sh;
        UpdateResults result = UpdateResults.NOT_FOUND;

        // If the list contains the sig, then we update
        if (m_hashList[archive].ContainsKey(sig)) {
          result = UpdateResults.UPTODATE;

          if (!String.IsNullOrEmpty(name) && m_hashList[archive][sig].FileName != name) {
            // Updates the filename if it has changed
            m_hashList[archive][sig].FileName = name;
            result = UpdateResults.NAME_UPDATED;
            m_runtimeFileNameChanges.Add(name.Replace('\\', '/'));

            AddDirectory(name);
            AddFileandExtension(name);

            MarkFileNameChanged(sig);
          }

          if (archive != m_hashList[archive][sig].ArchiveName) {
            // Updates the archivename if the file has switched archive
            m_hashList[archive][sig].ArchiveName = archive;
            result = UpdateResults.ARCHIVE_UPDATED;
            NeedsSave = true;
          }

          if (crc != 0 && m_hashList[archive][sig].Crc != crc) {
            m_hashList[archive][sig].Crc = crc;
            NeedsSave = true;
          }
        }
        return result;
      }
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
      lock (m_hashListLock) {
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
    }

    #endregion Methods

    #region Properties
    public Boolean NeedsSave { get; private set; }
    public Int32 PendingFileNameChanges {
      get {
        lock (m_pendingFileNameLock) return m_pendingFileNameHashes.Count;
      }
    }

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
