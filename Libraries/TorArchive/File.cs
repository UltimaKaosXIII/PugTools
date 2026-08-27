using System;
using System.IO;

using ICSharpCode.SharpZipLib.Zip.Compression.Streams;

namespace TorArchive {
  /// <summary>
  /// A file stored in a .tor archive
  /// </summary>
  public class File : IDisposable {

    // Zstandard frames always start with this 4-byte magic number (little-endian: 28 B5 2F FD).
    // Old (32-bit client) .tor archives compress entries with plain zlib/deflate instead, which
    // never starts with these bytes. We sniff the first 4 bytes of each compressed entry to pick
    // the right decompressor, so both old and new .tor files work side by side.
    private static readonly Byte[] ZstdMagic = { 0x28, 0xB5, 0x2F, 0xFD };

    #region Constructors
    public File(Archive arch, FileInfo info) {
      Archive = arch;
      FileInfo = info;
    }

    #endregion Constructors

    #region Fields
    private readonly Boolean _disposed = false;

    #endregion Fields

    #region Finalizer
    ~File() {
      Dispose(false);
    }

    #endregion Finalizer

    #region IDisposable
    public void Dispose() {
      Dispose(true);
      GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(Boolean disposing) {
      if (_disposed) {
        return;
      }

      if (disposing) {
        FileInfo.Dispose();
      }
    }

    #endregion IDisposable

    #region Methods
    public Stream Open() {
      FileStream archiveStream =
        Archive.OpenStreamAt((Int64)FileInfo.Offset + FileInfo.HeaderSize);

      if (!FileInfo.IsCompressed) {
        return archiveStream;
      }

      // Peek the first few bytes to figure out which codec this entry uses,
      // then rewind before handing off to the actual decompressor.
      Byte[] peek = new Byte[4];
      Int32 peekRead = archiveStream.Read(peek, 0, peek.Length);
      archiveStream.Seek(-peekRead, SeekOrigin.Current);

      Boolean isZstd = peekRead == ZstdMagic.Length && MatchesMagic(peek, ZstdMagic);

      if (isZstd) {
        return OpenZstd(archiveStream);
      } else {
        // Old-format (32-bit client) entries: classic zlib/deflate via SharpZipLib, as before.
        InflaterInputStream inflaterStream = new InflaterInputStream(archiveStream);
        return inflaterStream;
      }
    }

    /// <summary>
    /// Decompresses a Zstandard-compressed entry (new 64-bit client .tor format).
    /// Requires the "ZstdSharp.Port" NuGet package (pure managed, no native DLL needed).
    /// </summary>
    private Stream OpenZstd(FileStream archiveStream) {
      try {
        Byte[] compressed = new Byte[FileInfo.CompressedSize];
        Int32 totalRead = 0;
        while (totalRead < compressed.Length) {
          Int32 n = archiveStream.Read(compressed, totalRead, compressed.Length - totalRead);
          if (n <= 0) {
            break;
          }
          totalRead += n;
        }

        using ZstdSharp.Decompressor decompressor = new ZstdSharp.Decompressor();
        Span<Byte> span = decompressor.Unwrap(compressed, (Int32)FileInfo.UncompressedSize);
        return new MemoryStream(span.ToArray());
      } finally {
        archiveStream.Dispose();
      }
    }

    private static Boolean MatchesMagic(Byte[] data, Byte[] magic) {
      if (data.Length < magic.Length) {
        return false;
      }

      for (Int32 i = 0; i < magic.Length; i++) {
        if (data[i] != magic[i]) {
          return false;
        }
      }

      return true;
    }

    public Stream OpenCopyInMemory() {
      Stream fs = Open();

      if (FileInfo.UncompressedSize > Int32.MaxValue) {
        fs.Dispose();
        throw new InvalidDataException(
          $"File {FileInfo.FileId:X16} is too large for an in-memory stream: {FileInfo.UncompressedSize} bytes."
        );
      }

      // Zstd entries are already fully decompressed into a MemoryStream by OpenZstd(). The old implementation
      // copied that complete stream into a second equally large byte[] here, briefly doubling RAM and memory
      // bandwidth for every GR2/texture loaded from current 64-bit SWTOR archives. Transfer ownership of the
      // already-independent MemoryStream directly to the caller instead.
      if (fs is MemoryStream memory && memory.Position == 0 && memory.Length == (Int64)FileInfo.UncompressedSize) {
        return memory;
      }

      using (fs) {
        Byte[] buffer = new Byte[(Int32)FileInfo.UncompressedSize];
        Int32 totalRead = 0;

        while (totalRead < buffer.Length) {
          Int32 read = fs.Read(buffer, totalRead, buffer.Length - totalRead);
          if (read <= 0) break;
          totalRead += read;
        }

        if (totalRead != buffer.Length) {
          throw new EndOfStreamException(
            $"TOR entry {FileInfo.FileId:X16} was truncated: read {totalRead} of {buffer.Length} bytes."
          );
        }

        return new MemoryStream(buffer, writable: false) { Position = 0 };
      }
    }

    public Byte[] PeakBytes(Int32 bytes) {
      Stream fs = Open();
      Byte[] buffer = new Byte[bytes];

      // This will screw up if we have a single file greater than 2.1 GB.. probably not an issue.
      if (FileInfo.UncompressedSize < bytes) {
        fs.Read(buffer, 0, (Int32)FileInfo.UncompressedSize);
      } else {
        fs.Read(buffer, 0, bytes);
      }

      return buffer;
    }

    #endregion Methods

    #region Properties
    public Archive Archive { get; set; }
    //public string Directory { get; set; }
    //public string Extension { get; set; }
    public FileInfo FileInfo { get; set; }
    //public string FileName { get; set; }
    public String FilePath { get; set; }
    //public State FileState { get; set; }
    //public bool IsNamed { get; set; }
    //public string ParentDirectory { get; set; }
    //public string Source { get; set; }

    #endregion Properties
  }
}
