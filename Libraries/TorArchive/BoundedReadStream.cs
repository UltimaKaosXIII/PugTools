using System;
using System.IO;

namespace TorArchive {
  /// <summary>
  /// Read-only window over another seekable stream. Used for TOR entries so a
  /// decompressor can never consume bytes belonging to the following archive entry.
  /// </summary>
  internal sealed class BoundedReadStream : Stream {
    private readonly Stream m_baseStream;
    private readonly Int64 m_start;
    private readonly Int64 m_length;
    private readonly Boolean m_leaveOpen;
    private Int64 m_position;

    internal BoundedReadStream(Stream baseStream, Int64 length, Boolean leaveOpen = false) {
      m_baseStream = baseStream ?? throw new ArgumentNullException(nameof(baseStream));
      if (!baseStream.CanRead || !baseStream.CanSeek)
        throw new ArgumentException("The base stream must be readable and seekable.", nameof(baseStream));
      if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));
      m_start = baseStream.Position;
      m_length = length;
      m_leaveOpen = leaveOpen;
    }

    public override Boolean CanRead => true;
    public override Boolean CanSeek => true;
    public override Boolean CanWrite => false;
    public override Int64 Length => m_length;
    public override Int64 Position {
      get => m_position;
      set => Seek(value, SeekOrigin.Begin);
    }

    public override void Flush() { }

    public override Int32 Read(Byte[] buffer, Int32 offset, Int32 count) {
      if (buffer == null) throw new ArgumentNullException(nameof(buffer));
      if (offset < 0 || count < 0 || offset > buffer.Length - count)
        throw new ArgumentOutOfRangeException();
      if (m_position >= m_length || count == 0) return 0;

      Int32 wanted = (Int32)Math.Min(count, m_length - m_position);
      m_baseStream.Position = m_start + m_position;
      Int32 read = m_baseStream.Read(buffer, offset, wanted);
      m_position += read;
      return read;
    }

    public override Int64 Seek(Int64 offset, SeekOrigin origin) {
      Int64 target = origin switch {
        SeekOrigin.Begin => offset,
        SeekOrigin.Current => m_position + offset,
        SeekOrigin.End => m_length + offset,
        _ => throw new ArgumentOutOfRangeException(nameof(origin))
      };
      if (target < 0) throw new IOException("Attempted to seek before the beginning of a TOR entry.");
      if (target > m_length) target = m_length;
      m_position = target;
      return m_position;
    }

    public override void SetLength(Int64 value) => throw new NotSupportedException();
    public override void Write(Byte[] buffer, Int32 offset, Int32 count) => throw new NotSupportedException();

    protected override void Dispose(Boolean disposing) {
      if (disposing && !m_leaveOpen) m_baseStream.Dispose();
      base.Dispose(disposing);
    }
  }
}
