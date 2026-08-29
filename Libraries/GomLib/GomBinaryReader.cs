using System;
using System.Collections.Generic;
// using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace GomLib {
  public enum Endianness {
    LittleEndian,
    BigEndian
  }

  public class GomBinaryReader : BinaryReader {
    readonly DataObjectModel _dom;

    /// <summary>
    /// DBLB container version for definition-entry readers. Version 2 is the
    /// live-client layout; version 1 is used by the beta client and beta PBUK
    /// buckets. Non-DBLB readers keep the default value.
    /// </summary>
    public Int32 DblbVersion { get; }

    public GomBinaryReader(Stream str, DataObjectModel dom, Int32 dblbVersion = 2) : base(str) {
      _dom = dom;
      DblbVersion = dblbVersion;
    }
    public GomBinaryReader(Stream str, Encoding encoding, DataObjectModel dom, Int32 dblbVersion = 2) : base(str, encoding) {
      _dom = dom;
      DblbVersion = dblbVersion;
    }

    /// <summary>
    /// Reads SWTOR's unsigned varint form. The client accepts only literal 0x00-0xBF
    /// and positive 0xC8-0xCF length prefixes here; negative prefixes and INT64_MIN
    /// are invalid for an unsigned value.
    /// </summary>
    public ulong ReadNumber() {
      byte b0 = ReadByte();
      if (b0 < 0xC0) return b0;
      if (b0 < 0xC8 || b0 >= 0xD0)
        throw new InvalidOperationException(string.Format("Invalid unsigned number prefix: 0x{0:X2}", b0));

      int length = (b0 & 0x07) + 1;
      ulong value = 0;
      for (int i = 0; i < length; i++)
        value = (value << 8) | ReadByte();
      return value;
    }

    public long ReadSignedNumber() {
      byte b0 = ReadByte();
      if (b0 < 0xC0) return b0;
      if (b0 == 0xD0) return long.MinValue;
      if (b0 >= 0xD1)
        throw new InvalidOperationException(string.Format("Invalid signed number prefix: 0x{0:X2}", b0));

      int length = (b0 & 0x07) + 1;
      ulong magnitude = 0;
      for (int i = 0; i < length; i++)
        magnitude = (magnitude << 8) | ReadByte();

      if (b0 < 0xC8) {
        if (magnitude > 0x7FFFFFFFFFFFFFFFUL)
          throw new InvalidOperationException("Negative varint magnitude exceeds Int64 range.");
        return -(long)magnitude;
      }
      // Current x64 GOM data can serialize the full positive 64-bit magnitude even for
      // DOM type Int64 (for example table/map keys that are really 64-bit ids). Jedipedia
      // preserves that full 64-bit value. PugTools historically stores these values in a
      // System.Int64, so preserve the raw two's-complement bit pattern instead of rejecting
      // magnitudes above Int64.MaxValue. Counts still reject the resulting negative value
      // in ReadCount32, while true object/node ids use ReadIdNumber/ReadNumber (UInt64).
      return unchecked((long)magnitude);
    }

    /// <summary>
    /// Reads a SWTOR object/node id. IDs use the non-negative/unsigned half of
    /// the GOM varint alphabet and may legitimately occupy the full UInt64
    /// range (many beta prototype ids are greater than Int64.MaxValue).
    /// </summary>
    public ulong ReadIdNumber() => ReadNumber();

    /// <summary>Reads a non-negative 32-bit count/length encoded with SWTOR's signed varint.</summary>
    public int ReadCount32(string what) {
      long value = ReadSignedNumber();
      if (value < 0 || value > Int32.MaxValue)
        throw new InvalidDataException($"Invalid {what}: {value}.");
      return (int)value;
    }

    /// <summary>Consumes a one-byte container marker only when it is actually present.</summary>
    public bool TryConsumeMarker(byte marker) {
      if (!BaseStream.CanSeek) return false;
      long pos = BaseStream.Position;
      int value = BaseStream.ReadByte();
      if (value == marker) return true;
      BaseStream.Position = pos;
      return false;
    }

    public TypedValue ReadTypedValue() {
      TypedValueType valType = (TypedValueType)ReadByte();
      TypedValue result = new TypedValue(valType);
      result.Parse(this);

      return result;
    }

    public ulong ReadVariableWidthUInt64(int lengthInBytes) {
      if ((lengthInBytes < 1) || (lengthInBytes > 8)) {
        throw new ArgumentOutOfRangeException(nameof(lengthInBytes), "Length in bytes must be between 1 and 8");
      }

      ulong result = 0;

      byte[] bytes = ReadBytes(lengthInBytes);
      int shiftAmt = lengthInBytes - 1;
      for (var i = 0; i < lengthInBytes; i++) {
        ulong val = bytes[i];
        val <<= (8 * shiftAmt);
        shiftAmt--;
        result += val;
      }

      return result;
    }

    public GomType ReadGomType() {
      return _dom.GomTypeLoader.Load(this, _dom);
    }

    public string ReadNullTerminatedString() {
      return ReadNullTerminatedString(Encoding.UTF8);
    }

    public string ReadNullTerminatedString(Encoding encoding) {
      List<byte> byteBuffer = new List<byte>();
      byte b = ReadByte();
      // Read until we encounter a null byte
      while (b != 0) {
        byteBuffer.Add(b);
        b = ReadByte();
      }

      return encoding.GetString(byteBuffer.ToArray());
    }

    public short ReadInt16(Endianness endianness) {
      short val = base.ReadInt16();
      if (endianness == Endianness.LittleEndian) { return val; } else { return System.Net.IPAddress.NetworkToHostOrder(val); }
    }

    public ushort ReadUInt16(Endianness endianness) {
      if (endianness == Endianness.LittleEndian) { return base.ReadUInt16(); }

      byte[] b = base.ReadBytes(2);
      return BitConverter.ToUInt16(b.Reverse().ToArray(), 0);
    }

    public int ReadInt32(Endianness endianness) {
      int val = base.ReadInt32();

      if (endianness == Endianness.LittleEndian) { return val; } else { return System.Net.IPAddress.NetworkToHostOrder(val); }
    }

    public uint ReadUInt32(Endianness endianness) {
      if (endianness == Endianness.LittleEndian) { return base.ReadUInt32(); }

      byte[] b = base.ReadBytes(4);
      return BitConverter.ToUInt32(b.Reverse().ToArray(), 0);
    }

    public float ReadSingle(Endianness endianness) {
      if (endianness == Endianness.LittleEndian) { return base.ReadSingle(); }

      byte[] b = base.ReadBytes(4);
      return BitConverter.ToSingle(b.Reverse().ToArray(), 0);
    }

    public string ReadLengthPrefixString() {
      return ReadLengthPrefixString(Encoding.UTF8);
    }

    public string ReadLengthPrefixString(Encoding encoding) {
      int len = ReadCount32("string length");
      if (BaseStream.CanSeek && len > BaseStream.Length - BaseStream.Position)
        throw new EndOfStreamException($"String declares {len} bytes but only {BaseStream.Length - BaseStream.Position} remain.");
      return ReadFixedLengthString(len, encoding);
    }

    public string ReadFixedLengthString(int length) {
      return ReadFixedLengthString(length, Encoding.UTF8);
    }

    public string ReadFixedLengthString(int length, Encoding encoding) {
      byte[] buff = ReadBytes(length);
      string result = encoding.GetString(buff);
      //if (result.Equals("plc.location.tatooine.item.treasure_chest.chest", StringComparison.InvariantCultureIgnoreCase))
      //{
      //    Debug.WriteLine("Gotcha! string");
      //}
      return result;
    }
  }
}
