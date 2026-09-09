using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace nsHashDictionary {
  /// <summary>
  /// One flat UTF-8 filename byte pool plus offset boundaries. The on-disk representation uses the
  /// same front-coding idea as Jedipedia's FNM1 payload: each sorted name stores a one-byte
  /// shared-prefix length and only the remaining NUL-terminated suffix.
  /// </summary>
  internal sealed class CompactFileNameStore {
    private readonly Byte[] m_pool;
    private readonly Int32[] m_offsets;

    internal CompactFileNameStore(Byte[] pool, Int32[] offsets) {
      m_pool = pool ?? Array.Empty<Byte>();
      m_offsets = offsets ?? Array.Empty<Int32>();
    }

    internal Int32 Count => Math.Max(0, m_offsets.Length - 1);

    internal String GetName(Int32 index) {
      if (index < 0 || index + 1 >= m_offsets.Length) return String.Empty;
      Int32 start = m_offsets[index];
      Int32 length = m_offsets[index + 1] - start;
      if (start < 0 || length <= 0 || start + length > m_pool.Length) return String.Empty;
      return Encoding.UTF8.GetString(m_pool, start, length);
    }

    /// <summary>
    /// Finds a contiguous range in the sorted PFD1 filename pool without materialising the whole pool.
    /// PFD1 saves names with StringComparer.Ordinal, so a normal lower-bound search can jump directly to
    /// a resource-path prefix and only decode O(log n + matches) strings. SWTOR resource paths in the
    /// dictionary are normalized to lower-case; callers should pass a normalized lower-case prefix.
    /// </summary>
    internal IReadOnlyList<String> FindByPrefix(String prefix) {
      var result = new System.Collections.Generic.List<String>();
      if (String.IsNullOrEmpty(prefix) || Count == 0) return result;

      Int32 low = 0;
      Int32 high = Count;
      while (low < high) {
        Int32 mid = low + ((high - low) >> 1);
        String value = GetName(mid);
        if (StringComparer.Ordinal.Compare(value, prefix) < 0) low = mid + 1;
        else high = mid;
      }

      for (Int32 i = low; i < Count; i++) {
        String value = GetName(i);
        if (!value.StartsWith(prefix, StringComparison.Ordinal)) break;
        result.Add(value);
      }
      return result;
    }


    private sealed class BoundedByteReader {
      private readonly Stream m_stream;
      private readonly Byte[] m_buffer = new Byte[64 * 1024];
      private Int32 m_bufferAt;
      private Int32 m_bufferLength;
      private Int32 m_remaining;

      internal BoundedByteReader(Stream stream, Int32 length) {
        m_stream = stream ?? throw new ArgumentNullException(nameof(stream));
        m_remaining = length;
      }

      internal Int32 Remaining => m_remaining;

      internal Int32 ReadByte() {
        if (m_remaining <= 0) return -1;
        if (m_bufferAt >= m_bufferLength) {
          Int32 wanted = Math.Min(m_buffer.Length, m_remaining);
          m_bufferLength = m_stream.Read(m_buffer, 0, wanted);
          m_bufferAt = 0;
          if (m_bufferLength <= 0) throw new EndOfStreamException("PFD1: truncated filename suffix stream.");
        }
        m_remaining--;
        return m_buffer[m_bufferAt++];
      }
    }

    internal static CompactFileNameStore Read(BinaryReader reader, Int32 nameCount,
                                               Int32 poolBytes, Int32 suffixBytes) {
      if (reader == null) throw new ArgumentNullException(nameof(reader));
      if (nameCount < 0 || poolBytes < 0 || suffixBytes < 0)
        throw new InvalidDataException("PFD1: negative filename-store size.");
      if (nameCount == 0) {
        if (suffixBytes != 0 || poolBytes != 0)
          throw new InvalidDataException("PFD1: empty filename store has non-empty payload.");
        return new CompactFileNameStore(Array.Empty<Byte>(), new Int32[1]);
      }

      Byte[] shared = reader.ReadBytes(nameCount);
      if (shared.Length != nameCount)
        throw new EndOfStreamException("PFD1: truncated shared-prefix plane.");

      Byte[] pool = new Byte[poolBytes];
      Int32[] offsets = new Int32[nameCount + 1];
      var suffixReader = new BoundedByteReader(reader.BaseStream, suffixBytes);
      Int32 writeAt = 0;
      Int32 previousStart = 0;
      Int32 previousLength = 0;

      for (Int32 i = 0; i < nameCount; i++) {
        Int32 prefix = shared[i];
        if (prefix > previousLength)
          throw new InvalidDataException(
            $"PFD1: filename {i} shares {prefix} bytes with a {previousLength}-byte predecessor."
          );

        offsets[i] = writeAt;
        if (prefix > 0) {
          if (writeAt + prefix > pool.Length)
            throw new InvalidDataException("PFD1: filename pool overflow while copying prefix.");
          Buffer.BlockCopy(pool, previousStart, pool, writeAt, prefix);
          writeAt += prefix;
        }

        Int32 suffixLength = 0;
        Boolean terminated = false;
        while (suffixReader.Remaining > 0) {
          Int32 raw = suffixReader.ReadByte();
          if (raw < 0) break;
          Byte value = (Byte)raw;
          if (value == 0) {
            terminated = true;
            break;
          }
          if (writeAt >= pool.Length)
            throw new InvalidDataException("PFD1: filename pool overflow while copying suffix.");
          pool[writeAt++] = value;
          suffixLength++;
        }
        if (!terminated)
          throw new InvalidDataException($"PFD1: unterminated suffix for filename {i}.");

        previousStart = offsets[i];
        previousLength = prefix + suffixLength;
      }

      offsets[nameCount] = writeAt;
      if (writeAt != poolBytes)
        throw new InvalidDataException($"PFD1: filename pool contains {writeAt} bytes, header says {poolBytes}.");
      if (suffixReader.Remaining != 0)
        throw new InvalidDataException($"PFD1: {suffixReader.Remaining} unconsumed filename suffix bytes.");

      return new CompactFileNameStore(pool, offsets);
    }
  }
}
