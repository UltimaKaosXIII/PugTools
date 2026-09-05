using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace TorArchive {
  /// <summary>
  /// Class used to manage a single .tor file
  /// </summary>
  public class Archive : IDisposable {

    #region Constructors
    public Archive(String fileName, Library library) {
      FileName = fileName;
      Library = library;
      Initialize();
    }

    #endregion Constructors

    #region Fields
    // public HashSet<string> directories = new HashSet<string>();
    private readonly Dictionary<UInt64, FileInfo> m_fileLookup = new Dictionary<UInt64, FileInfo>();
    private String m_strippedFileName;

    #endregion Fields

    #region IDisposable
    private Boolean m_disposed = false;

    public void Dispose() {
      Dispose(true);
      GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(Boolean disposing) {
      if (m_disposed) {
        return;
      }

      if (disposing) {
        foreach (KeyValuePair<UInt64, FileInfo> lookup in m_fileLookup) {
          lookup.Value.Dispose();
        }

        m_fileLookup.Clear();

        // File instances are lightweight wrappers around FileInfo and are now
        // created on demand by EnumerateFiles(). The FileInfo lookup remains
        // the authoritative index.
        // directories.Clear();
        // Library.Dispose();                
      }

      m_disposed = true;
    }

    #endregion IDisposable

    #region Methods
    public File FindFile(FileId fileId) {
      return FindFile(fileId.AsUInt64());
    }

    public File FindFile(UInt64 fileId) {
      if (!Initialized) {
        Initialize();
      }

      if (!m_fileLookup.TryGetValue(fileId, out FileInfo fileInfo)) {
        return null;
      }

      File file = new File(this, fileInfo);
      return file;
    }

    /// <summary>
    /// Load the MYP/TOR header and file-table chain. SWTOR archives use the same
    /// 34-byte table entry in the beta/32-bit (v5) and current 64-bit (v6) clients;
    /// v5 entries are zlib/DEFLATE when compressed while v6 entries are Zstandard.
    /// Beta archives are valid with HeaderSize/metadataLength == 0.
    /// </summary>
    private void Initialize() {
      using (FileStream fs = OpenStreamAt(0))
      using (BinaryReader reader = new BinaryReader(fs)) {
        if (fs.Length < 36)
          throw new InvalidDataException($"TOR archive '{FileName}' is too small to contain a valid header.");

        // These first three fields are read before the BOM changes the byte order. This
        // matches the format used by Jedipedia's TOR reader.
        UInt32 magicNumber = reader.ReadUInt32();
        if (magicNumber != 0x0050594D)
          throw new InvalidDataException($"'{FileName}' is not a MYP/TOR archive (bad magic 0x{magicNumber:X8}).");

        Version = reader.ReadUInt32();
        ByteOrderMarker = reader.ReadUInt32();
        IsLittleEndian = ByteOrderMarker switch {
          0xFD23EC43 => true,
          0x43EC23FD => false,
          _ => throw new InvalidDataException($"TOR archive '{FileName}' has an unknown byte-order marker 0x{ByteOrderMarker:X8}.")
        };

        UInt64 fileTableOffset = ReadUInt64(reader, IsLittleEndian);
        FileTableCapacity = ReadUInt32(reader, IsLittleEndian);
        TotalNumberOfFiles = ReadUInt32(reader, IsLittleEndian);
        ConfirmSequence = ReadUInt32(reader, IsLittleEndian);
        WriteSequence = ReadUInt32(reader, IsLittleEndian);

        var visitedTables = new HashSet<UInt64>();
        while (fileTableOffset != 0) {
          if (fileTableOffset > (UInt64)(fs.Length - 12))
            throw new InvalidDataException($"TOR archive '{FileName}' points to a file table outside the archive (0x{fileTableOffset:X}).");
          if (!visitedTables.Add(fileTableOffset))
            throw new InvalidDataException($"TOR archive '{FileName}' contains a loop in its file-table chain at 0x{fileTableOffset:X}.");

          fs.Seek((Int64)fileTableOffset, SeekOrigin.Begin);
          UInt32 numFiles = ReadUInt32(reader, IsLittleEndian);
          UInt64 nextFileTableOffset = ReadUInt64(reader, IsLittleEndian);

          // A table row is always 34 bytes. Validate the complete row block before
          // walking it so a truncated/corrupt archive cannot make us read into random data.
          UInt64 tableEnd = fileTableOffset + 12UL + (UInt64)numFiles * 34UL;
          if (tableEnd > (UInt64)fs.Length)
            throw new InvalidDataException($"TOR archive '{FileName}' contains a truncated file table at 0x{fileTableOffset:X}.");

          for (UInt32 i = 0; i < numFiles; i++) {
            UInt64 offset = ReadUInt64(reader, IsLittleEndian);
            UInt32 metadataLength = ReadUInt32(reader, IsLittleEndian);
            UInt32 compressedSize = ReadUInt32(reader, IsLittleEndian);
            UInt32 uncompressedSize = ReadUInt32(reader, IsLittleEndian);
            UInt32 onDiskPrimaryHash = ReadUInt32(reader, IsLittleEndian);
            UInt32 onDiskSecondaryHash = ReadUInt32(reader, IsLittleEndian);
            UInt32 checksum = ReadUInt32(reader, IsLittleEndian);
            UInt16 compressionMethod = ReadUInt16(reader, IsLittleEndian);

            if (offset == 0) continue;

            UInt64 storedSize = compressionMethod != 0 ? compressedSize : uncompressedSize;
            if (offset > (UInt64)fs.Length || metadataLength > (UInt64)fs.Length - offset
                || storedSize > (UInt64)fs.Length - offset - metadataLength)
              throw new InvalidDataException($"TOR archive '{FileName}' contains an out-of-range file entry in table 0x{fileTableOffset:X} (slot {i}).");

            var info = new FileInfo {
              Offset = offset,
              HeaderSize = metadataLength,
              CompressedSize = compressedSize,
              UncompressedSize = uncompressedSize,
              Checksum = checksum,
              CompressionMethod = compressionMethod,
              CRC = unchecked((Int32)checksum)
            };

            // PugTools' historic hash API names are opposite to the on-disk TOR spec:
            // FileId.FromFilePath().Ph is the spec's secondary hash and .Sh is the
            // spec's primary hash. Keep that public convention for compatibility, but
            // compose the key explicitly rather than relying on little-endian ReadUInt64.
            info.SecondaryHash = onDiskPrimaryHash;
            info.PrimaryHash = onDiskSecondaryHash;
            info.FileId = ((UInt64)info.PrimaryHash << 32) | info.SecondaryHash;

            m_fileLookup[info.FileId] = info;
          }

          fileTableOffset = nextFileTableOffset;
        }
      }

      Initialized = true;
    }

    private static UInt16 ReadUInt16(BinaryReader reader, Boolean littleEndian) {
      Span<Byte> buffer = stackalloc Byte[2];
      ReadExactly(reader.BaseStream, buffer);
      return littleEndian
        ? BinaryPrimitives.ReadUInt16LittleEndian(buffer)
        : BinaryPrimitives.ReadUInt16BigEndian(buffer);
    }

    private static UInt32 ReadUInt32(BinaryReader reader, Boolean littleEndian) {
      Span<Byte> buffer = stackalloc Byte[4];
      ReadExactly(reader.BaseStream, buffer);
      return littleEndian
        ? BinaryPrimitives.ReadUInt32LittleEndian(buffer)
        : BinaryPrimitives.ReadUInt32BigEndian(buffer);
    }

    private static UInt64 ReadUInt64(BinaryReader reader, Boolean littleEndian) {
      Span<Byte> buffer = stackalloc Byte[8];
      ReadExactly(reader.BaseStream, buffer);
      return littleEndian
        ? BinaryPrimitives.ReadUInt64LittleEndian(buffer)
        : BinaryPrimitives.ReadUInt64BigEndian(buffer);
    }

    private static void ReadExactly(Stream stream, Span<Byte> buffer) {
      Int32 totalRead = 0;
      while (totalRead < buffer.Length) {
        Int32 read = stream.Read(buffer.Slice(totalRead));
        if (read <= 0) throw new EndOfStreamException("Unexpected end of TOR archive.");
        totalRead += read;
      }
    }

    internal FileStream OpenStream(FileInfo fileInfo) {
      UInt64 offset = fileInfo.Offset + fileInfo.HeaderSize;
      return OpenStreamAt((Int64)offset);
    }

    internal FileStream OpenStreamAt(Int64 offset) {
      FileStream fs = System.IO.File.Open(FileName, FileMode.Open, FileAccess.Read, FileShare.Read);
      fs.Seek(offset, SeekOrigin.Begin);
      return fs;
    }

    public override String ToString() {
      return FileName.Split('\\').Last();
    }

    #endregion Methods

    #region Properties
    public String FileName { get; set; }
    /// <summary>
    /// Enumerates file wrappers without keeping one File object per archive
    /// entry alive for the lifetime of the archive.
    /// </summary>
    public IEnumerable<File> EnumerateFiles() {
      foreach (FileInfo info in m_fileLookup.Values) {
        yield return new File(this, info);
      }
    }

    /// <summary>
    /// Compatibility property. Prefer EnumerateFiles() in long-running views.
    /// </summary>
    public List<File> Files => EnumerateFiles().ToList();

    /// <summary>TOR/MYP format version (4 = legacy, 5 = SWTOR 32-bit/beta, 6 = SWTOR 64-bit).</summary>
    public UInt32 Version { get; private set; }
    public UInt32 ByteOrderMarker { get; private set; }
    public Boolean IsLittleEndian { get; private set; }
    public UInt32 FileTableCapacity { get; private set; }
    public UInt32 TotalNumberOfFiles { get; private set; }
    public UInt32 ConfirmSequence { get; private set; }
    public UInt32 WriteSequence { get; private set; }
    public Boolean Initialized { get; private set; }
    internal Library Library { get; set; }
    /// <summary>
    /// Gets the archive name, minus the "swtor_" or "swtor_test_" and .tor part of the name.
    /// </summary>
    public String StrippedFileName {
      get {
        if (m_strippedFileName == null && FileName != null) {
          StrippedFileName = FileName;
        }

        return m_strippedFileName;
      }
      set {
        // Remove the directory.
        String fileName = value.Split('/').Last();
        fileName = fileName.Split('\\').Last();

        // Preserve the established LIVE/PTS normalization exactly.  The legacy prefixes are additive
        // only, so current 64-bit archive naming keeps the same behavior it had before beta support.
        fileName = fileName.Replace("swtor_", String.Empty);
        fileName = fileName.Replace("test_", String.Empty);

        // RED/HE32/early beta archives use a different physical prefix but the hash dictionary is still
        // keyed by the logical archive name (main_1, en-us_1, ...). Remove only a leading legacy prefix.
        foreach (String prefix in new[] { "red_", "assets_", "he32_", "green_" }) {
          if (!fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
          fileName = fileName.Substring(prefix.Length);
          break;
        }

        // Remove .tor
        fileName = fileName.Replace(".tor", String.Empty);

        m_strippedFileName = fileName;
      }
    }

    #endregion Properties
  }
}
