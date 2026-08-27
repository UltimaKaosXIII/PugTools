using System;
using System.Collections.Generic;
using System.IO;

namespace TorArchive {

  /// <summary>
  /// Manages multiple Archive files together as a single library
  /// </summary>
  public class Library : IDisposable {

    #region Constructor
    private Library() {
      m_duplicateDict = new Dictionary<UInt64, String>();
      m_lockObject = new Object();
      m_metadataLookup = new Dictionary<UInt64, MetadataEntry>();

      Archives = new Dictionary<Int32, Archive>();
    }

    // BETA ASSETS
    public Library(String name, String location) : this() {
      Location = location;
      Name = name;

      if (Directory.GetFiles(location, "red_*.tor").Length > 0) {
        Environment = RED;
        return;
      }

      if (Directory.GetFiles(location, "assets_*.tor").Length > 0) {
        Environment = BETA;
        return;
      }
    }

    // LIVE & PTS ASSETS
    public Library(String name, String location, Boolean isPTS) : this() {
      Environment = isPTS ? PTS : LIVE;

      Location = location;
      Name = name;
    }

    // Direct archive (for swtor/publictest retailclient TOR files). These archives do not use
    // the swtor_<library>_<n>.tor naming convention, so keep the exact path and query the
    // Archive hash table directly instead of duplicating it in a Library metadata index.
    public Library(String name, String[] archiveFilePaths) : this() {
      if (archiveFilePaths == null || archiveFilePaths.Length == 0)
        throw new ArgumentException("No archive paths were provided", nameof(archiveFilePaths));

      Environment = CLIENT;
      Location = Path.GetDirectoryName(archiveFilePaths[0]) ?? String.Empty;
      Name = name;
      m_explicitArchivePaths = archiveFilePaths;
    }

    #endregion Constructor

    #region Fields
    private readonly Dictionary<UInt64, String> m_duplicateDict;
    private readonly Object m_lockObject;
    private readonly Dictionary<UInt64, MetadataEntry> m_metadataLookup;
    private readonly String[] m_explicitArchivePaths;

    private const Byte LIVE = 0;
    private const Byte PTS  = 1;
    private const Byte RED    = 2;
    private const Byte BETA   = 3;
    private const Byte CLIENT = 4;

    #endregion Fields

    #region IDisposable
    private Boolean m_disposed = false;

    ~Library() {
      Dispose(false);
    }

    public void Dispose() {
      Dispose(true);
      GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(Boolean disposing) {
      if (m_disposed) {
        return;
      }

      if (disposing) {
        foreach (KeyValuePair<UInt64, MetadataEntry> meta in m_metadataLookup) {
          meta.Value.Dispose();
        }

        m_metadataLookup.Clear();

        foreach (KeyValuePair<Int32, Archive> arch in Archives) {
          arch.Value.Dispose();
        }

        Archives.Clear();
      }

      m_disposed = true;
      Loaded = false;
    }

    #endregion IDisposable

    #region Methods
    public File FindFile(String path) {
      if (!Loaded) {
        Load();
      }

      FileId fileId = FileId.FromFilePath(path);

      if (m_explicitArchivePaths != null) {
        foreach (Archive archive in Archives.Values) {
          File directResult = archive.FindFile(fileId);
          if (directResult == null) continue;

          directResult.FilePath = path;
          return directResult;
        }

        return null;
      }

      if (!m_metadataLookup.TryGetValue(fileId.AsUInt64(), out MetadataEntry metadata)) {
        return null;
      }

      Archive archiveWithMetadata = Archives[metadata.Archive];
      File result = archiveWithMetadata.FindFile(fileId);

      if (result != null) {
        result.FilePath = path;
      }

      return result;
    }

    public void Load() {
      if (Loaded) {
        return;
      }

      lock (m_lockObject) {
        if (Loaded) {
          //Check again just in case.
          return;
        }

        if (m_explicitArchivePaths != null) {
          Int32 archiveIndex = 1;
          foreach (String archivePath in m_explicitArchivePaths) {
            if (!System.IO.File.Exists(archivePath)) continue;
            Archives[archiveIndex++] = new Archive(archivePath, this);
          }

          if (Archives.Count == 0)
            throw new InvalidOperationException($"Cannot find archive files for library named {Name} in {Location}");

          Loaded = true;
          return;
        }

        Archive archive = null;
        Boolean hasFile;

        for (Int32 i = 1; ; i++) {
          String getFileName() => Environment switch {
            RED  => $"red_{Name}_{i}.tor",
            BETA => $"assets_{Name}_{i}.tor",
            PTS  => $"swtor_test_{Name}_{i}.tor",
            _    => $"swtor_{Name}_{i}.tor",
          };

          String filePath = Path.Combine(Location, getFileName());

          hasFile = System.IO.File.Exists(filePath);

          if (!hasFile) {
            if (archive == null) {
              // Can't find a single file for this library?! Something is quite wrong with this.
              throw new InvalidOperationException(
                $"Cannot find any files for library named {Name} in {Location}");
            }

            // What is currently in 'archive' is the last archive in this library --
            // we need to get metadata from it!
            File metadataFile = archive.FindFile(FileId.FromFilePath("metadata.bin"));

            if (metadataFile == null) {
              throw new InvalidOperationException(
                $"Cannot Load metadata.bin for this library from {archive.FileName}");
            }

            LoadMetadataFromFile(metadataFile);

            break;
          }

          archive = new Archive(filePath, this);
          Archives[i] = archive;
        }

        Loaded = true;
        return;
      }
    }

    private void LoadMetadataFromFile(File metadataFile) {
      UInt32 numFiles = metadataFile.FileInfo.UncompressedSize / 32;

      using (Stream stream = metadataFile.Open()) {
        using (BinaryReader reader = new BinaryReader(stream)) {
          for (Int32 i = 0; i < numFiles; i++) {
            reader.ReadBytes(16); // Unknown usage.. CRC of some type perhaps?

            UInt32 ph = reader.ReadUInt32();
            UInt32 sh = reader.ReadUInt32();
            Byte fileNum = reader.ReadByte();

            reader.ReadBytes(7); // Unknown usage

            FileId fileId = new FileId() {
              Ph = ph,
              Sh = sh
            };
            MetadataEntry entry = new MetadataEntry() {
              FileId = fileId,
              Archive = fileNum
            };

            // Considerably faster than throwing exceptions.
            UInt64 fid = fileId.AsUInt64();

            if (!m_metadataLookup.ContainsKey(fid)) {
              m_metadataLookup.Add(fid, entry);
            }
          }
        }
      }
    }

    public override String ToString() {
      return Name;
    }

    #endregion Methods

    #region Properties
    public Dictionary<Int32, Archive> Archives { get; }
    public Dictionary<UInt64, String> DuplicateDict => m_duplicateDict;
    public Byte Environment { get; }
    public String Name { get; }
    public Boolean Loaded { get; private set; }
    public String Location { get; }

    #endregion Properties
  }
}
