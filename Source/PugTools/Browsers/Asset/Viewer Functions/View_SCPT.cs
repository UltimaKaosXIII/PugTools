using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using GomLib;

namespace PugTools {
  /// <summary>
  /// Reader for SWTOR's compiled HeroScript (SCPT) container.
  ///
  /// The original PugTools implementation only stripped the old 0x35/0x36 XOR layer and
  /// displayed the payload as hex.  SWTOR has three generations of the payload layout
  /// (beta 4.4, live 5.5 and current 6.5) and changed the XOR stream several times after
  /// 7.4.  Keep the reader self-contained so old archives and current archives can be
  /// inspected with the same Asset Browser.
  /// </summary>
  internal static class ViewSCPT {
    private const UInt32 ScptMagic = 0x54504353; // "SCPT" as little-endian UInt32.
    private const Int32 MaxCollectionCount = 1000000;
    private const Int32 MaxDTypeDepth = 64;

    internal sealed class ScptFileInfo {
      internal UInt16 ContentVersion;
      internal UInt16 TransportVersion;
      internal UInt64 Id;
      internal String Name;
      internal Boolean IsEncrypted;
      internal readonly List<ScptPayloadInfo> Payloads = new List<ScptPayloadInfo>();
    }

    internal sealed class ScptPayloadInfo {
      internal UInt64 Architecture;
      internal Byte[] Bytes;
      internal String DecryptionScheme;
      internal readonly List<String> LiteralConstants = new List<String>();
      internal readonly List<ScptNameInfo> Names = new List<ScptNameInfo>();
      internal readonly List<ScptValueInfo> Values = new List<ScptValueInfo>();
      internal readonly List<ScptTypeInfo> Types = new List<ScptTypeInfo>();
      internal readonly List<ScptSignatureInfo> Signatures = new List<ScptSignatureInfo>();
      internal readonly List<ScptFunctionInfo> Functions = new List<ScptFunctionInfo>();
      internal readonly List<ScptReferenceConstantInfo> ReferenceConstants = new List<ScptReferenceConstantInfo>();
      internal readonly List<ScptGlobalConstantInfo> GlobalConstants = new List<ScptGlobalConstantInfo>();
      internal readonly List<ScptIntegerValue> Channels = new List<ScptIntegerValue>();
      internal readonly List<ScptIntegerValue> ConfigVariables = new List<ScptIntegerValue>();
      internal readonly List<ScptSectionInfo> CodeSections = new List<ScptSectionInfo>();
      internal readonly List<ScptSectionInfo> DataSections = new List<ScptSectionInfo>();
      internal readonly List<ScptSymbolInfo> Symbols = new List<ScptSymbolInfo>();
      internal readonly List<ScptRelocationInfo> Relocations = new List<ScptRelocationInfo>();
      internal readonly List<ScptFlaggableInfo> Flaggables = new List<ScptFlaggableInfo>();
      internal readonly List<Int64> LineNumbers = new List<Int64>();
      internal readonly List<ScptFieldInfo> Fields = new List<ScptFieldInfo>();
      internal ScptElfInfo Elf;
    }

    internal sealed class ScptNameInfo {
      internal Int64 Hash;
      internal String Text;
    }

    internal sealed class ScptTypeInfo {
      internal Byte Type;
      internal ScptIntegerValue ReferenceId;
      internal ScptTypeInfo ValueType;
      internal ScptTypeInfo IndexType;
      internal Byte ReturnDirection;
      internal ScptTypeInfo ReturnType;
      internal readonly List<ScptTypeArgumentInfo> Arguments = new List<ScptTypeArgumentInfo>();
      internal readonly List<ScptTypeInfo> ElementTypes = new List<ScptTypeInfo>();
    }

    internal sealed class ScptTypeArgumentInfo {
      internal Byte Direction;
      internal ScptTypeInfo Type;
    }

    internal sealed class ScptValueInfo {
      internal ScptTypeInfo Type;
      internal Object Value;
    }

    internal sealed class ScptLookupValueInfo {
      internal ScptValueInfo Index;
      internal ScptValueInfo Value;
    }

    internal sealed class ScptClassFieldValueInfo {
      internal BigInteger Id;
      internal ScptIntegerValue IdOffset;
      internal Byte FieldType;
      internal ScptValueInfo Value;
    }

    internal sealed class ScptSignatureInfo {
      internal readonly List<ScptSignatureArgumentInfo> Arguments = new List<ScptSignatureArgumentInfo>();
    }

    internal sealed class ScptSignatureArgumentInfo {
      internal Byte Direction;
      internal Int32 TypeIndex;
    }

    internal sealed class ScptFunctionInfo {
      internal Int32 NameIndex;
      internal Int32 SignatureIndex;
    }

    internal sealed class ScptReferenceConstantInfo {
      internal Int32 NameIndex;
      internal Int32 TypeIndex;
      internal Int32 ValueIndex;
    }

    internal sealed class ScptGlobalConstantInfo {
      internal Int32 NameIndex;
      internal Int32 TypeIndex;
    }

    internal sealed class ScptSectionInfo {
      internal Boolean HasData;
      internal Int64 Alignment;
      internal Int64 Length;
      internal Byte[] Data;
    }

    internal sealed class ScptSymbolInfo {
      internal Int32 Type;
      internal Int32 FunctionNameIndex = -1;
      internal Int32 FunctionIndex = -1;
      internal Int32 SectionIndex = -1;
      internal Int64 Offset;
    }

    internal sealed class ScptRelocationInfo {
      internal Int32 SymbolIndex;
      internal Int32 Type;
      internal Int32 SectionIndex;
      internal Int64 Offset;
      internal Int64 Addend;
    }

    internal sealed class ScptFlaggableInfo {
      internal Int32 NameIndex;
      internal Int32 SignatureIndex;
      internal Int64 Line;
      internal Int64 Flags;
    }

    internal sealed class ScptFieldInfo {
      internal ScptIntegerValue ClassId;
      internal ScptIntegerValue FieldId;
    }

    internal sealed class ScptIntegerValue {
      internal Boolean Negative;
      internal UInt64 Magnitude;
      internal Boolean IsInt64Min;

      internal BigInteger BigIntegerValue {
        get {
          BigInteger value = new BigInteger(Magnitude);
          return Negative ? -value : value;
        }
      }

      internal String DecimalText => BigIntegerValue.ToString(CultureInfo.InvariantCulture);

      internal String HexText {
        get {
          if (IsInt64Min) return "-0x8000000000000000";
          return (Negative ? "-0x" : "0x") + Magnitude.ToString("X", CultureInfo.InvariantCulture);
        }
      }
    }

    internal sealed class ScptElfInfo {
      internal Byte Class;
      internal Byte DataEncoding;
      internal UInt16 Type;
      internal UInt16 Machine;
      internal UInt32 Entry;
      internal UInt32 ProgramHeaderOffset;
      internal UInt32 SectionHeaderOffset;
      internal UInt32 Flags;
      internal UInt16 ProgramHeaderCount;
      internal UInt16 SectionHeaderCount;
      internal UInt16 SectionNameIndex;
      internal Int32 Length;
    }

    private sealed class ScptReader {
      private readonly BinaryReader reader;
      internal Int64 Position => reader.BaseStream.Position;
      internal Int64 Length => reader.BaseStream.Length;
      internal Int64 Remaining => Length - Position;

      internal ScptReader(Byte[] data) {
        reader = new BinaryReader(new MemoryStream(data, false), Encoding.UTF8, false);
      }

      internal Byte ReadByte() {
        Ensure(1);
        return reader.ReadByte();
      }

      internal UInt16 ReadUInt16() {
        Ensure(2);
        return reader.ReadUInt16();
      }

      internal UInt32 ReadUInt32() {
        Ensure(4);
        return reader.ReadUInt32();
      }

      internal Single ReadSingle() {
        Ensure(4);
        return reader.ReadSingle();
      }

      internal Byte[] ReadBytes(Int32 count) {
        if (count < 0) throw new InvalidDataException("Negative SCPT byte count.");
        Ensure(count);
        Byte[] value = reader.ReadBytes(count);
        if (value.Length != count) throw new EndOfStreamException();
        return value;
      }

      internal ScptIntegerValue ReadVarInteger() {
        Byte first = ReadByte();
        if (first < 0xC0) return new ScptIntegerValue { Magnitude = first };
        if (first == 0xD0) return new ScptIntegerValue { Negative = true, Magnitude = 0x8000000000000000UL, IsInt64Min = true };
        if (first > 0xCF)
          throw new InvalidDataException("Invalid SCPT varint lead byte 0x" + first.ToString("X2", CultureInfo.InvariantCulture) + ".");

        Int32 numBytes = (first & 0x07) + 1;
        UInt64 magnitude = 0;
        for (Int32 i = 0; i < numBytes; i++) magnitude = (magnitude << 8) | ReadByte();
        return new ScptIntegerValue { Negative = first < 0xC8, Magnitude = magnitude };
      }

      internal Int64 ReadVarInt() {
        ScptIntegerValue value = ReadVarInteger();
        if (value.IsInt64Min) return Int64.MinValue;
        if (value.Magnitude > Int64.MaxValue)
          throw new InvalidDataException("SCPT structural varint exceeds Int64 range: " + value.DecimalText + ".");
        Int64 signed = (Int64)value.Magnitude;
        return value.Negative ? -signed : signed;
      }

      internal Int32 ReadCount(String what) {
        Int64 value = ReadVarInt();
        if (value < 0 || value > MaxCollectionCount)
          throw new InvalidDataException("Invalid SCPT " + what + " count: " + value.ToString(CultureInfo.InvariantCulture));
        return (Int32)value;
      }

      internal String ReadStringWithVarLength() {
        Int32 length = ReadCount("string length");
        return Encoding.UTF8.GetString(ReadBytes(length));
      }

      internal void Expect(Byte expected, String what) {
        Byte actual = ReadByte();
        if (actual != expected)
          throw new InvalidDataException(
            "Expected " + what + " 0x" + expected.ToString("X2", CultureInfo.InvariantCulture) +
            " but found 0x" + actual.ToString("X2", CultureInfo.InvariantCulture) +
            " at payload offset 0x" + (Position - 1).ToString("X", CultureInfo.InvariantCulture) + ".");
      }

      private void Ensure(Int64 count) {
        if (count < 0 || Remaining < count) throw new EndOfStreamException("Unexpected end of SCPT payload.");
      }
    }

    private readonly struct XorScheme {
      internal readonly Byte Start;
      internal readonly Byte Step;
      internal readonly String Label;

      internal XorScheme(Byte start, Byte step, String label) {
        Start = start;
        Step = step;
        Label = label;
      }
    }

    internal static ScptFileInfo Parse(BinaryReader br) {
      if (br == null) throw new ArgumentNullException(nameof(br));
      if (!br.BaseStream.CanSeek) throw new InvalidDataException("SCPT reader requires a seekable stream.");
      br.BaseStream.Position = 0;

      if (br.BaseStream.Length < 24) throw new InvalidDataException("SCPT file is shorter than its header.");
      UInt32 magic = br.ReadUInt32();
      if (magic != ScptMagic) throw new InvalidDataException("Wrong SCPT magic 0x" + magic.ToString("X8", CultureInfo.InvariantCulture) + ".");

      ScptFileInfo file = new ScptFileInfo {
        ContentVersion = br.ReadUInt16(),
        TransportVersion = br.ReadUInt16()
      };

      if (!((file.ContentVersion == 4 && file.TransportVersion == 4) ||
            (file.ContentVersion == 5 && file.TransportVersion == 5) ||
            (file.ContentVersion == 6 && file.TransportVersion == 5)))
        throw new InvalidDataException(
          "Unsupported SCPT version " + file.ContentVersion.ToString(CultureInfo.InvariantCulture) + "." +
          file.TransportVersion.ToString(CultureInfo.InvariantCulture) + ".");

      UInt64 constant = br.ReadUInt64();
      if (constant != 1) throw new InvalidDataException("SCPT header constant at offset 8 is not 1.");
      file.Id = br.ReadUInt64();

      if (file.TransportVersion == 4) {
        UInt32 nameLength = br.ReadUInt32();
        if (nameLength > br.BaseStream.Length - br.BaseStream.Position || nameLength > Int32.MaxValue)
          throw new InvalidDataException("Invalid beta SCPT name length.");
        file.Name = Encoding.UTF8.GetString(br.ReadBytes((Int32)nameLength));
        file.IsEncrypted = false;
      } else {
        Byte encrypted = br.ReadByte();
        if (encrypted > 1) throw new InvalidDataException("SCPT encrypted flag must be 0 or 1.");
        file.IsEncrypted = encrypted != 0;
      }

      Int32 payloadIndex = 0;
      while (br.BaseStream.Position < br.BaseStream.Length) {
        if (br.BaseStream.Length - br.BaseStream.Position < 12)
          throw new InvalidDataException("Incomplete SCPT payload directory entry at end of file.");

        UInt64 architecture = br.ReadUInt64();
        if (architecture != 0 && architecture != 2)
          throw new InvalidDataException("SCPT payload architecture must be 0 (x86) or 2 (x64), not " + architecture.ToString(CultureInfo.InvariantCulture) + ".");
        UInt32 dataLength = br.ReadUInt32();
        if (dataLength > br.BaseStream.Length - br.BaseStream.Position || dataLength > Int32.MaxValue)
          throw new InvalidDataException("Invalid SCPT payload length " + dataLength.ToString(CultureInfo.InvariantCulture) + ".");
        Byte[] payloadBytes = br.ReadBytes((Int32)dataLength);

        ScptPayloadInfo payload;
        if (file.IsEncrypted) {
          payload = ParseEncryptedPayload(payloadBytes, file.ContentVersion, file.TransportVersion, payloadIndex);
        } else {
          payload = file.ContentVersion == 4
            ? ParsePayload(payloadBytes, file.ContentVersion, file.TransportVersion)
            : ParsePayloadUnencryptedLive(payloadBytes, file.ContentVersion, file.TransportVersion);
          payload.DecryptionScheme = "not encrypted";
        }
        payload.Architecture = architecture;
        payload.Bytes = payload.Bytes ?? payloadBytes;
        file.Payloads.Add(payload);
        payloadIndex++;
      }

      if (file.Payloads.Count == 0) throw new InvalidDataException("SCPT contains no payloads.");
      return file;
    }

    /// <summary>
    /// Compatibility helper retained for callers that only need the first decrypted payload.
    /// Unlike the old implementation it also understands beta files and all known live XOR streams.
    /// </summary>
    internal static MemoryStream DecryptSCPT(BinaryReader br) {
      ScptFileInfo file = Parse(br);
      Byte[] bytes = file.Payloads.First().Bytes ?? Array.Empty<Byte>();
      return new MemoryStream(bytes, false);
    }

    private static ScptPayloadInfo ParseEncryptedPayload(Byte[] encrypted, UInt16 contentVersion, UInt16 transportVersion, Int32 payloadIndex) {
      Exception lastError = null;
      foreach (XorScheme scheme in GetXorSchemes(contentVersion)) {
        Byte[] decrypted = ApplyXor(encrypted, scheme.Start, scheme.Step);
        try {
          ScptPayloadInfo payload = ParsePayload(decrypted, contentVersion, transportVersion);
          payload.Bytes = decrypted;
          payload.DecryptionScheme = scheme.Label + " (start 0x" + scheme.Start.ToString("X2", CultureInfo.InvariantCulture) +
                                     ", step 0x" + scheme.Step.ToString("X2", CultureInfo.InvariantCulture) + ")";
          return payload;
        }
        catch (Exception ex) when (ex is InvalidDataException || ex is EndOfStreamException || ex is OverflowException || ex is ArgumentException) {
          lastError = ex;
        }
      }

      throw new InvalidDataException(
        "Could not decrypt/parse SCPT payload #" + payloadIndex.ToString(CultureInfo.InvariantCulture) +
        " with any known SWTOR XOR stream.", lastError);
    }

    private static IEnumerable<XorScheme> GetXorSchemes(UInt16 contentVersion) {
      // Up to 7.3 the original 0x35 / +0x36 stream was used. Content version 6 then
      // changed the pair in 7.4, 7.5 and 7.5.1.  We validate a candidate by parsing
      // the entire payload including the final D3 marker, so trying all known streams
      // is more reliable than tying the reader to a particular installed patch.
      if (contentVersion <= 5) yield return new XorScheme(0x35, 0x36, "legacy/live <= 5.x");
      yield return new XorScheme(0x49, 0x4A, "content 6 pre-7.4");
      yield return new XorScheme(0x2F, 0x05, "7.4");
      yield return new XorScheme(0x35, 0x07, "7.5");
      yield return new XorScheme(0x43, 0x0B, "7.5.1+");
      if (contentVersion > 5) yield return new XorScheme(0x35, 0x36, "legacy fallback");
    }

    private static Byte[] ApplyXor(Byte[] input, Byte start, Byte step) {
      Byte[] output = new Byte[input.Length];
      Int32 xor = start;
      for (Int32 i = 0; i < input.Length; i++) {
        output[i] = (Byte)(input[i] ^ xor);
        xor = (xor + step) & 0xFF;
      }
      return output;
    }

    private static ScptPayloadInfo ParsePayload(Byte[] bytes, UInt16 contentVersion, UInt16 transportVersion) {
      ScptReader reader = new ScptReader(bytes);
      ScptPayloadInfo payload = new ScptPayloadInfo { Bytes = bytes };

      Int32 literalCount = reader.ReadCount("literal constant");
      for (Int32 i = 0; i < literalCount; i++) payload.LiteralConstants.Add(reader.ReadStringWithVarLength());

      if (contentVersion == 4) {
        Int32 elfLength = reader.ReadCount("ELF length");
        Byte[] elfBytes = reader.ReadBytes(elfLength);
        payload.Elf = ParseElfSummary(elfBytes);
      } else {
        Int32 nameCount = reader.ReadCount("name");
        for (Int32 i = 0; i < nameCount; i++) {
          ScptNameInfo name = new ScptNameInfo { Hash = reader.ReadVarInt() };
          // Encrypted SCPT payloads intentionally omit plaintext names from the dictionary.
          // The caller tells us only the layout version here, so detect their presence by the
          // outer parse: decrypted/encrypted payloads are structurally identical except for
          // this field.  ParsePayload is invoked with a flag encoded below through the helper.
          payload.Names.Add(name);
        }
      }

      // This is the beta/encrypted-live core. Unencrypted live payloads are handled by
      // ParsePayloadUnencryptedLive because their name dictionary also stores plaintext.
      ParsePayloadTail(reader, payload, contentVersion, transportVersion);

      if (reader.Position != reader.Length)
        throw new InvalidDataException("SCPT payload has " + reader.Remaining.ToString(CultureInfo.InvariantCulture) + " unread byte(s).");
      return payload;
    }

    private static ScptPayloadInfo ParsePayloadUnencryptedLive(Byte[] bytes, UInt16 contentVersion, UInt16 transportVersion) {
      ScptReader reader = new ScptReader(bytes);
      ScptPayloadInfo payload = new ScptPayloadInfo { Bytes = bytes };
      Int32 literalCount = reader.ReadCount("literal constant");
      for (Int32 i = 0; i < literalCount; i++) payload.LiteralConstants.Add(reader.ReadStringWithVarLength());
      Int32 nameCount = reader.ReadCount("name");
      for (Int32 i = 0; i < nameCount; i++) {
        payload.Names.Add(new ScptNameInfo { Hash = reader.ReadVarInt(), Text = reader.ReadStringWithVarLength() });
      }
      ParsePayloadTail(reader, payload, contentVersion, transportVersion);
      if (reader.Position != reader.Length)
        throw new InvalidDataException("SCPT payload has " + reader.Remaining.ToString(CultureInfo.InvariantCulture) + " unread byte(s).");
      return payload;
    }

    private static void ParsePayloadTail(ScptReader reader, ScptPayloadInfo payload, UInt16 contentVersion, UInt16 transportVersion) {
      payload.Values.AddRange(ReadDHeroContext(reader, contentVersion, transportVersion));

      Int32 typeCount = reader.ReadCount("type");
      for (Int32 i = 0; i < typeCount; i++) payload.Types.Add(ReadDType(reader, 0));

      Int32 signatureCount = reader.ReadCount("signature");
      for (Int32 i = 0; i < signatureCount; i++) {
        ScptSignatureInfo signature = new ScptSignatureInfo();
        Int32 argCount = reader.ReadCount("signature argument");
        for (Int32 j = 0; j < argCount; j++) {
          Byte direction = reader.ReadByte();
          if (direction > 4) throw new InvalidDataException("Unknown SCPT signature direction " + direction.ToString(CultureInfo.InvariantCulture) + ".");
          signature.Arguments.Add(new ScptSignatureArgumentInfo {
            Direction = direction,
            TypeIndex = CheckedIndex(reader.ReadVarInt(), "signature type")
          });
        }
        payload.Signatures.Add(signature);
      }

      Int32 functionCount = reader.ReadCount("function");
      for (Int32 i = 0; i < functionCount; i++) {
        payload.Functions.Add(new ScptFunctionInfo {
          NameIndex = CheckedIndex(reader.ReadVarInt(), "function name"),
          SignatureIndex = CheckedIndex(reader.ReadVarInt(), "function signature")
        });
      }

      Int32 referenceConstantCount = reader.ReadCount("reference constant");
      for (Int32 i = 0; i < referenceConstantCount; i++) {
        payload.ReferenceConstants.Add(new ScptReferenceConstantInfo {
          NameIndex = CheckedIndex(reader.ReadVarInt(), "reference constant name"),
          TypeIndex = CheckedIndex(reader.ReadVarInt(), "reference constant type"),
          ValueIndex = CheckedIndex(reader.ReadVarInt(), "reference constant value")
        });
      }

      Int32 globalConstantCount = reader.ReadCount("global constant");
      for (Int32 i = 0; i < globalConstantCount; i++) {
        payload.GlobalConstants.Add(new ScptGlobalConstantInfo {
          NameIndex = CheckedIndex(reader.ReadVarInt(), "global constant name"),
          TypeIndex = CheckedIndex(reader.ReadVarInt(), "global constant type")
        });
      }

      Int32 channelCount = reader.ReadCount("channel");
      for (Int32 i = 0; i < channelCount; i++) payload.Channels.Add(reader.ReadVarInteger());

      Int32 configCount = reader.ReadCount("config variable");
      for (Int32 i = 0; i < configCount; i++) payload.ConfigVariables.Add(reader.ReadVarInteger());

      if (contentVersion != 4) {
        Int32 declaredCodeSize = CheckedNonNegativeInt(reader.ReadVarInt(), "code size");
        Int32 codeSectionCount = reader.ReadCount("code section");
        Int32 declaredDataSize = CheckedNonNegativeInt(reader.ReadVarInt(), "data size");
        Int32 dataSectionCount = reader.ReadCount("data section");

        for (Int32 i = 0; i < codeSectionCount; i++) payload.CodeSections.Add(ReadSection(reader));
        for (Int32 i = 0; i < dataSectionCount; i++) payload.DataSections.Add(ReadSection(reader));

        Int64 actualCodeSize = payload.CodeSections.Sum(section => section.Length);
        Int64 actualDataSize = payload.DataSections.Sum(section => section.Length);
        if (Math.Abs(actualCodeSize - declaredCodeSize) > 16)
          throw new InvalidDataException("SCPT code section directory size does not match section lengths.");
        if (Math.Abs(actualDataSize - declaredDataSize) > 16)
          throw new InvalidDataException("SCPT data section directory size does not match section lengths.");

        while (true) {
          Int64 rawType = reader.ReadVarInt();
          if (rawType == 0) break;
          Int32 type = CheckedIndex(rawType, "symbol type");
          ScptSymbolInfo symbol = new ScptSymbolInfo { Type = type };
          switch (type) {
            case 1:
              symbol.SectionIndex = CheckedIndex(reader.ReadVarInt(), "symbol section");
              symbol.Offset = reader.ReadVarInt();
              break;
            case 2:
            case 3:
              symbol.FunctionNameIndex = CheckedIndex(reader.ReadVarInt(), "symbol function name");
              symbol.SectionIndex = CheckedIndex(reader.ReadVarInt(), "symbol section");
              symbol.Offset = reader.ReadVarInt();
              break;
            case 4:
              symbol.Offset = reader.ReadVarInt();
              break;
            case 5:
              symbol.FunctionNameIndex = CheckedIndex(reader.ReadVarInt(), "symbol function name");
              symbol.FunctionIndex = CheckedIndex(reader.ReadVarInt(), "symbol function");
              break;
            case 6:
            case 7:
              symbol.FunctionNameIndex = CheckedIndex(reader.ReadVarInt(), "symbol function name");
              break;
            default:
              throw new InvalidDataException("Unknown SCPT symbol type " + type.ToString(CultureInfo.InvariantCulture) + ".");
          }
          payload.Symbols.Add(symbol);
        }

        while (true) {
          Int64 rawSymbolIndex = reader.ReadVarInt();
          if (rawSymbolIndex == -1) break;
          ScptRelocationInfo relocation = new ScptRelocationInfo {
            SymbolIndex = CheckedIndex(rawSymbolIndex, "relocation symbol"),
            Type = CheckedIndex(reader.ReadVarInt(), "relocation type"),
            SectionIndex = CheckedIndex(reader.ReadVarInt(), "relocation section"),
            Offset = reader.ReadVarInt(),
            Addend = reader.ReadVarInt()
          };
          if (relocation.Type != 1 && relocation.Type != 2)
            throw new InvalidDataException("Unknown SCPT relocation type " + relocation.Type.ToString(CultureInfo.InvariantCulture) + ".");
          payload.Relocations.Add(relocation);
        }
      }

      Int32 flaggableCount = reader.ReadCount("flaggable");
      for (Int32 i = 0; i < flaggableCount; i++) {
        payload.Flaggables.Add(new ScptFlaggableInfo {
          NameIndex = CheckedIndex(reader.ReadVarInt(), "flaggable name"),
          SignatureIndex = CheckedIndex(reader.ReadVarInt(), "flaggable signature"),
          Line = reader.ReadVarInt(),
          Flags = reader.ReadVarInt()
        });
      }

      Int32 lineNumberCount = reader.ReadCount("line number");
      for (Int32 i = 0; i < lineNumberCount; i++) payload.LineNumbers.Add(reader.ReadVarInt());

      Int32 fieldCount = reader.ReadCount("field");
      for (Int32 i = 0; i < fieldCount; i++) {
        payload.Fields.Add(new ScptFieldInfo {
          ClassId = reader.ReadVarInteger(),
          FieldId = reader.ReadVarInteger()
        });
      }

      reader.Expect(0xD3, "SCPT final marker");
    }

    private static ScptSectionInfo ReadSection(ScptReader reader) {
      Byte hasData = reader.ReadByte();
      if (hasData > 1) throw new InvalidDataException("SCPT section hasData must be 0 or 1.");
      ScptSectionInfo section = new ScptSectionInfo {
        HasData = hasData != 0,
        Alignment = reader.ReadVarInt(),
        Length = reader.ReadVarInt()
      };
      if (section.Alignment < 0 || section.Length < 0 || section.Length > Int32.MaxValue)
        throw new InvalidDataException("Invalid SCPT section alignment/length.");
      if (section.HasData) section.Data = reader.ReadBytes((Int32)section.Length);
      return section;
    }

    private static List<ScptValueInfo> ReadDHeroContext(ScptReader reader, UInt16 contentVersion, UInt16 transportVersion) {
      reader.Expect(0xD1, "D section start");
      reader.Expect(0x03, "Hero Context section kind");
      Int32 count = reader.ReadCount("Hero Context value");
      List<ScptValueInfo> values = new List<ScptValueInfo>(count);
      for (Int32 i = 0; i < count; i++) {
        ScptTypeInfo type = ReadDType(reader, 0);
        if (transportVersion == 4) reader.Expect(0x00, "beta Hero Context value prefix");
        values.Add(new ScptValueInfo {
          Type = type,
          Value = ReadDValue(reader, type, contentVersion, transportVersion, 0)
        });
      }
      reader.Expect(0xD3, "Hero Context section end");
      return values;
    }

    private static ScptTypeInfo ReadDType(ScptReader reader, Int32 depth) {
      if (depth > MaxDTypeDepth) throw new InvalidDataException("SCPT D type nesting is too deep.");
      reader.Expect(0xD1, "D type section start");
      reader.Expect(0x00, "D type section kind");
      ScptTypeInfo type = new ScptTypeInfo { Type = reader.ReadByte() };
      switch (type.Type) {
        case 5: // Enum
        case 9: // ClassView
        case 15: // NodeRef
          type.ReferenceId = reader.ReadVarInteger();
          break;
        case 7: // List
          type.ValueType = ReadDType(reader, depth + 1);
          break;
        case 8: // LookupList
          type.IndexType = ReadDType(reader, depth + 1);
          type.ValueType = ReadDType(reader, depth + 1);
          break;
        case 23: // FuncRef
          type.ReturnDirection = reader.ReadByte();
          type.ReturnType = ReadDType(reader, depth + 1);
          Int32 argCount = reader.ReadCount("FuncRef argument");
          for (Int32 i = 0; i < argCount; i++) {
            Byte direction = reader.ReadByte();
            if (direction != 2 && direction != 3 && direction != 4)
              throw new InvalidDataException("Unknown FuncRef argument direction " + direction.ToString(CultureInfo.InvariantCulture) + ".");
            type.Arguments.Add(new ScptTypeArgumentInfo { Direction = direction, Type = ReadDType(reader, depth + 1) });
          }
          break;
        case 24: // Tuple
          Int32 elementCount = reader.ReadCount("tuple element");
          for (Int32 i = 0; i < elementCount; i++) type.ElementTypes.Add(ReadDType(reader, depth + 1));
          break;
      }
      reader.Expect(0xD3, "D type section end");
      return type;
    }

    private static Object ReadDValue(ScptReader reader, ScptTypeInfo type, UInt16 contentVersion, UInt16 transportVersion, Int32 depth) {
      if (depth > MaxDTypeDepth) throw new InvalidDataException("SCPT D value nesting is too deep.");
      switch (type.Type) {
        case 1: // ID
        case 2: // Integer
        case 5: // Enum
        case 15: // NodeRef
        case 20: // TimeInterval
        case 21: // DateTime
          return reader.ReadVarInteger();
        case 3:
          Byte boolByte = reader.ReadByte();
          if (boolByte > 1) throw new InvalidDataException("SCPT Boolean value is not 0/1.");
          return boolByte != 0;
        case 4:
          return reader.ReadSingle();
        case 6:
          return reader.ReadStringWithVarLength();
        case 7: {
          ScptTypeInfo valueType = type.ValueType;
          if (contentVersion >= 6) {
            Byte storedType = reader.ReadByte();
            if (valueType != null && valueType.Type != storedType)
              throw new InvalidDataException("SCPT List value type does not match its D type.");
            if (valueType == null) valueType = new ScptTypeInfo { Type = storedType };
          }
          if (valueType == null) throw new InvalidDataException("SCPT List is missing its value type.");
          Int32 totalCount = reader.ReadCount("list total");
          Int32 storedCount = reader.ReadCount("list stored");
          if (totalCount != storedCount) throw new InvalidDataException("SCPT List total/stored counts differ.");
          List<ScptValueInfo> list = new List<ScptValueInfo>(storedCount);
          for (Int32 i = 0; i < storedCount; i++) {
            list.Add(new ScptValueInfo {
              Type = valueType,
              Value = ReadDValue(reader, valueType, contentVersion, transportVersion, depth + 1)
            });
          }
          return list;
        }
        case 8: {
          ScptTypeInfo indexType = type.IndexType;
          ScptTypeInfo valueType = type.ValueType;
          if (contentVersion >= 6) {
            Byte storedIndexType = reader.ReadByte();
            Byte storedValueType = reader.ReadByte();
            if (indexType != null && indexType.Type != storedIndexType)
              throw new InvalidDataException("SCPT LookupList index type does not match its D type.");
            if (valueType != null && valueType.Type != storedValueType)
              throw new InvalidDataException("SCPT LookupList value type does not match its D type.");
            if (indexType == null) indexType = new ScptTypeInfo { Type = storedIndexType };
            if (valueType == null) valueType = new ScptTypeInfo { Type = storedValueType };
          }
          if (indexType == null || valueType == null) throw new InvalidDataException("SCPT LookupList is missing its index/value type.");
          Int32 totalCount = reader.ReadCount("lookup total");
          Int32 storedCount = reader.ReadCount("lookup stored");
          if (totalCount != storedCount) throw new InvalidDataException("SCPT LookupList total/stored counts differ.");
          List<ScptLookupValueInfo> list = new List<ScptLookupValueInfo>(storedCount);
          for (Int32 i = 0; i < storedCount; i++) {
            if (indexType.Type == 6) reader.Expect(0xD2, "LookupList string marker");
            ScptValueInfo index = new ScptValueInfo {
              Type = indexType,
              Value = ReadDValue(reader, indexType, contentVersion, transportVersion, depth + 1)
            };
            if (transportVersion == 4) reader.Expect(0x00, "beta LookupList value prefix");
            ScptValueInfo value = new ScptValueInfo {
              Type = valueType,
              Value = ReadDValue(reader, valueType, contentVersion, transportVersion, depth + 1)
            };
            list.Add(new ScptLookupValueInfo { Index = index, Value = value });
          }
          return list;
        }
        case 9: {
          Int32 totalFields = reader.ReadCount("ClassView total field");
          Int32 storedFields = reader.ReadCount("ClassView stored field");
          if (totalFields != storedFields) throw new InvalidDataException("SCPT ClassView total/stored counts differ.");
          List<ScptClassFieldValueInfo> fields = new List<ScptClassFieldValueInfo>(storedFields);
          BigInteger previousId = BigInteger.Zero;
          for (Int32 i = 0; i < storedFields; i++) {
            ScptIntegerValue idOffset = reader.ReadVarInteger();
            previousId += idOffset.BigIntegerValue;
            Byte fieldType = reader.ReadByte();
            ScptTypeInfo fieldDType = new ScptTypeInfo { Type = fieldType };
            fields.Add(new ScptClassFieldValueInfo {
              Id = previousId,
              IdOffset = idOffset,
              FieldType = fieldType,
              Value = new ScptValueInfo {
                Type = fieldDType,
                Value = ReadDValue(reader, fieldDType, contentVersion, transportVersion, depth + 1)
              }
            });
          }
          return fields;
        }
        case 18:
          return new Single[] { reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle() };
        default:
          // Void and the handle/reference-only DOM kinds do not carry an inline value here.
          return null;
      }
    }

    private static ScptElfInfo ParseElfSummary(Byte[] bytes) {
      if (bytes == null || bytes.Length < 52) throw new InvalidDataException("Beta SCPT ELF object is truncated.");
      using BinaryReader br = new BinaryReader(new MemoryStream(bytes, false));
      UInt32 magic = br.ReadUInt32();
      if (magic != 0x464C457F) throw new InvalidDataException("Beta SCPT payload does not contain an ELF object.");
      ScptElfInfo elf = new ScptElfInfo {
        Class = br.ReadByte(),
        DataEncoding = br.ReadByte(),
        Length = bytes.Length
      };
      br.ReadByte(); // EI_VERSION
      br.ReadByte(); // EI_OSABI
      br.ReadBytes(8);
      elf.Type = br.ReadUInt16();
      elf.Machine = br.ReadUInt16();
      br.ReadUInt32(); // e_version
      elf.Entry = br.ReadUInt32();
      elf.ProgramHeaderOffset = br.ReadUInt32();
      elf.SectionHeaderOffset = br.ReadUInt32();
      elf.Flags = br.ReadUInt32();
      br.ReadUInt16(); // e_ehsize
      br.ReadUInt16(); // e_phentsize
      elf.ProgramHeaderCount = br.ReadUInt16();
      br.ReadUInt16(); // e_shentsize
      elf.SectionHeaderCount = br.ReadUInt16();
      elf.SectionNameIndex = br.ReadUInt16();
      return elf;
    }

    private static Int32 CheckedIndex(Int64 value, String what) {
      if (value < 0 || value > Int32.MaxValue) throw new InvalidDataException("Invalid SCPT " + what + " index: " + value.ToString(CultureInfo.InvariantCulture));
      return (Int32)value;
    }

    private static Int32 CheckedNonNegativeInt(Int64 value, String what) {
      if (value < 0 || value > Int32.MaxValue) throw new InvalidDataException("Invalid SCPT " + what + ": " + value.ToString(CultureInfo.InvariantCulture));
      return (Int32)value;
    }

    internal static ArrayList BuildTree(ScptFileInfo file) {
      ArrayList roots = new ArrayList();
      NodeListItem header = new NodeListItem("Compiled HeroScript (SCPT)", "");
      header.children.Add(new NodeListItem("Version", file.ContentVersion.ToString(CultureInfo.InvariantCulture) + "." + file.TransportVersion.ToString(CultureInfo.InvariantCulture)));
      header.children.Add(new NodeListItem("Script ID", file.Id.ToString(CultureInfo.InvariantCulture) + "  (0x" + file.Id.ToString("X16", CultureInfo.InvariantCulture) + ")"));
      if (!String.IsNullOrWhiteSpace(file.Name)) header.children.Add(new NodeListItem("Script name", file.Name));
      header.children.Add(new NodeListItem("Encrypted payload", file.IsEncrypted));
      header.children.Add(new NodeListItem("Payload count", file.Payloads.Count));
      roots.Add(header);

      for (Int32 i = 0; i < file.Payloads.Count; i++) roots.Add(BuildPayloadTree(file, file.Payloads[i], i));
      return roots;
    }

    private static NodeListItem BuildPayloadTree(ScptFileInfo file, ScptPayloadInfo payload, Int32 payloadIndex) {
      String arch = payload.Architecture == 0 ? "x86 / 32-bit" : payload.Architecture == 2 ? "x64 / 64-bit" : "architecture " + payload.Architecture.ToString(CultureInfo.InvariantCulture);
      NodeListItem root = new NodeListItem("Payload #" + payloadIndex.ToString(CultureInfo.InvariantCulture) + " — " + arch, payload.Bytes?.Length.ToString("N0", CultureInfo.InvariantCulture) + " bytes");
      root.children.Add(new NodeListItem("Decryption", payload.DecryptionScheme ?? "n/a"));

      if (payload.Elf != null) {
        NodeListItem elf = new NodeListItem("Beta ELF32 object", payload.Elf.Length.ToString("N0", CultureInfo.InvariantCulture) + " bytes");
        elf.children.Add(new NodeListItem("Class", payload.Elf.Class == 1 ? "ELFCLASS32" : payload.Elf.Class.ToString(CultureInfo.InvariantCulture)));
        elf.children.Add(new NodeListItem("Data", payload.Elf.DataEncoding == 1 ? "little-endian" : payload.Elf.DataEncoding.ToString(CultureInfo.InvariantCulture)));
        elf.children.Add(new NodeListItem("Type", payload.Elf.Type == 1 ? "ET_REL (relocatable)" : payload.Elf.Type.ToString(CultureInfo.InvariantCulture)));
        elf.children.Add(new NodeListItem("Machine", payload.Elf.Machine == 3 ? "EM_386" : payload.Elf.Machine.ToString(CultureInfo.InvariantCulture)));
        elf.children.Add(new NodeListItem("Section headers", payload.Elf.SectionHeaderCount));
        elf.children.Add(new NodeListItem("Program headers", payload.Elf.ProgramHeaderCount));
        elf.children.Add(new NodeListItem("Section header offset", Hex(payload.Elf.SectionHeaderOffset)));
        root.children.Add(elf);
      }

      root.children.Add(BuildStringGroup("Literal constants", payload.LiteralConstants));
      if (file.ContentVersion != 4) root.children.Add(BuildNameGroup(payload));
      root.children.Add(BuildValuesGroup(payload.Values));
      root.children.Add(BuildTypesGroup(payload.Types));
      root.children.Add(BuildSignaturesGroup(payload));
      root.children.Add(BuildFunctionsGroup(file, payload));
      root.children.Add(BuildReferenceConstantsGroup(file, payload));
      root.children.Add(BuildGlobalConstantsGroup(file, payload));
      root.children.Add(BuildIntegerGroup("Channels", payload.Channels));
      root.children.Add(BuildIntegerGroup("Config variables", payload.ConfigVariables));

      if (file.ContentVersion != 4) {
        root.children.Add(BuildSectionsGroup("Code sections", payload.CodeSections));
        root.children.Add(BuildSectionsGroup("Data sections", payload.DataSections));
        root.children.Add(BuildSymbolsGroup(file, payload));
        root.children.Add(BuildRelocationsGroup(payload));
      }

      root.children.Add(BuildFlaggablesGroup(file, payload));
      root.children.Add(BuildIntegerGroup("Line numbers", payload.LineNumbers));
      root.children.Add(BuildFieldsGroup(payload));
      return root;
    }

    private static NodeListItem BuildStringGroup(String title, IList<String> values) {
      NodeListItem root = new NodeListItem(title, values.Count.ToString(CultureInfo.InvariantCulture));
      for (Int32 i = 0; i < values.Count; i++) root.children.Add(new NodeListItem("#" + i.ToString(CultureInfo.InvariantCulture), values[i]));
      return root;
    }

    private static NodeListItem BuildNameGroup(ScptPayloadInfo payload) {
      NodeListItem root = new NodeListItem("Name dictionary", payload.Names.Count.ToString(CultureInfo.InvariantCulture));
      for (Int32 i = 0; i < payload.Names.Count; i++) {
        ScptNameInfo name = payload.Names[i];
        String hash = "0x" + unchecked((UInt64)name.Hash).ToString("X8", CultureInfo.InvariantCulture);
        root.children.Add(new NodeListItem("#" + i.ToString(CultureInfo.InvariantCulture), String.IsNullOrEmpty(name.Text) ? hash : name.Text + "  [" + hash + "]"));
      }
      return root;
    }

    private static NodeListItem BuildValuesGroup(IList<ScptValueInfo> values) {
      NodeListItem root = new NodeListItem("Hero Context / values", values.Count.ToString(CultureInfo.InvariantCulture));
      Int32 max = Math.Min(values.Count, 5000);
      for (Int32 i = 0; i < max; i++) root.children.Add(BuildValueNode("#" + i.ToString(CultureInfo.InvariantCulture), values[i], 0));
      if (values.Count > max) root.children.Add(new NodeListItem("…", (values.Count - max).ToString(CultureInfo.InvariantCulture) + " more values"));
      return root;
    }

    private static NodeListItem BuildValueNode(String name, ScptValueInfo value, Int32 depth) {
      String type = FormatType(value?.Type);
      NodeListItem node = new NodeListItem(name + (String.IsNullOrEmpty(type) ? String.Empty : "  :  " + type), FormatValue(value?.Value));
      if (value == null || depth >= 12) return node;

      if (value.Value is List<ScptValueInfo> list) {
        Int32 max = Math.Min(list.Count, 1000);
        for (Int32 i = 0; i < max; i++) node.children.Add(BuildValueNode("[" + i.ToString(CultureInfo.InvariantCulture) + "]", list[i], depth + 1));
        if (list.Count > max) node.children.Add(new NodeListItem("…", (list.Count - max).ToString(CultureInfo.InvariantCulture) + " more items"));
      } else if (value.Value is List<ScptLookupValueInfo> lookup) {
        Int32 max = Math.Min(lookup.Count, 1000);
        for (Int32 i = 0; i < max; i++) {
          ScptLookupValueInfo entry = lookup[i];
          NodeListItem pair = new NodeListItem("[" + i.ToString(CultureInfo.InvariantCulture) + "]", FormatValue(entry.Index?.Value) + " → " + FormatValue(entry.Value?.Value));
          pair.children.Add(BuildValueNode("Index", entry.Index, depth + 1));
          pair.children.Add(BuildValueNode("Value", entry.Value, depth + 1));
          node.children.Add(pair);
        }
        if (lookup.Count > max) node.children.Add(new NodeListItem("…", (lookup.Count - max).ToString(CultureInfo.InvariantCulture) + " more entries"));
      } else if (value.Value is List<ScptClassFieldValueInfo> fields) {
        Int32 max = Math.Min(fields.Count, 1000);
        for (Int32 i = 0; i < max; i++) {
          ScptClassFieldValueInfo field = fields[i];
          String fieldName = ResolveDomId(field.Id);
          node.children.Add(BuildValueNode(fieldName + "  [+" + field.IdOffset.DecimalText + ", " + DomTypeName(field.FieldType) + "]", field.Value, depth + 1));
        }
        if (fields.Count > max) node.children.Add(new NodeListItem("…", (fields.Count - max).ToString(CultureInfo.InvariantCulture) + " more fields"));
      }
      return node;
    }

    private static NodeListItem BuildTypesGroup(IList<ScptTypeInfo> types) {
      NodeListItem root = new NodeListItem("Types", types.Count.ToString(CultureInfo.InvariantCulture));
      for (Int32 i = 0; i < types.Count; i++) root.children.Add(new NodeListItem("#" + i.ToString(CultureInfo.InvariantCulture), FormatType(types[i])));
      return root;
    }

    private static NodeListItem BuildSignaturesGroup(ScptPayloadInfo payload) {
      NodeListItem root = new NodeListItem("Signatures", payload.Signatures.Count.ToString(CultureInfo.InvariantCulture));
      for (Int32 i = 0; i < payload.Signatures.Count; i++) root.children.Add(new NodeListItem("#" + i.ToString(CultureInfo.InvariantCulture), FormatSignature(payload, i)));
      return root;
    }

    private static NodeListItem BuildFunctionsGroup(ScptFileInfo file, ScptPayloadInfo payload) {
      NodeListItem root = new NodeListItem("Functions", payload.Functions.Count.ToString(CultureInfo.InvariantCulture));
      for (Int32 i = 0; i < payload.Functions.Count; i++) {
        ScptFunctionInfo function = payload.Functions[i];
        String name = ResolveName(file, payload, function.NameIndex);
        root.children.Add(new NodeListItem("#" + i.ToString(CultureInfo.InvariantCulture), name + FormatSignature(payload, function.SignatureIndex)));
      }
      return root;
    }

    private static NodeListItem BuildReferenceConstantsGroup(ScptFileInfo file, ScptPayloadInfo payload) {
      NodeListItem root = new NodeListItem("Reference constants", payload.ReferenceConstants.Count.ToString(CultureInfo.InvariantCulture));
      for (Int32 i = 0; i < payload.ReferenceConstants.Count; i++) {
        ScptReferenceConstantInfo item = payload.ReferenceConstants[i];
        String value = ResolveName(file, payload, item.NameIndex) + " : " + ResolveType(payload, item.TypeIndex) + " = " + ResolveValue(payload, item.ValueIndex);
        root.children.Add(new NodeListItem("#" + i.ToString(CultureInfo.InvariantCulture), value));
      }
      return root;
    }

    private static NodeListItem BuildGlobalConstantsGroup(ScptFileInfo file, ScptPayloadInfo payload) {
      NodeListItem root = new NodeListItem("Global constants", payload.GlobalConstants.Count.ToString(CultureInfo.InvariantCulture));
      for (Int32 i = 0; i < payload.GlobalConstants.Count; i++) {
        ScptGlobalConstantInfo item = payload.GlobalConstants[i];
        root.children.Add(new NodeListItem("#" + i.ToString(CultureInfo.InvariantCulture), ResolveName(file, payload, item.NameIndex) + " : " + ResolveType(payload, item.TypeIndex)));
      }
      return root;
    }

    private static NodeListItem BuildIntegerGroup(String title, IList<Int64> values) {
      NodeListItem root = new NodeListItem(title, values.Count.ToString(CultureInfo.InvariantCulture));
      for (Int32 i = 0; i < values.Count; i++) root.children.Add(new NodeListItem("#" + i.ToString(CultureInfo.InvariantCulture), values[i].ToString(CultureInfo.InvariantCulture)));
      return root;
    }

    private static NodeListItem BuildIntegerGroup(String title, IList<ScptIntegerValue> values) {
      NodeListItem root = new NodeListItem(title, values.Count.ToString(CultureInfo.InvariantCulture));
      for (Int32 i = 0; i < values.Count; i++) root.children.Add(new NodeListItem("#" + i.ToString(CultureInfo.InvariantCulture), FormatInteger(values[i])));
      return root;
    }

    private static NodeListItem BuildSectionsGroup(String title, IList<ScptSectionInfo> sections) {
      NodeListItem root = new NodeListItem(title, sections.Count.ToString(CultureInfo.InvariantCulture));
      for (Int32 i = 0; i < sections.Count; i++) {
        ScptSectionInfo section = sections[i];
        String preview = section.HasData && section.Data != null ? BytePreview(section.Data, 24) : "BSS / no stored bytes";
        NodeListItem node = new NodeListItem("#" + i.ToString(CultureInfo.InvariantCulture), section.Length.ToString("N0", CultureInfo.InvariantCulture) + " bytes; align " + section.Alignment.ToString(CultureInfo.InvariantCulture) + "; " + preview);
        root.children.Add(node);
      }
      return root;
    }

    private static NodeListItem BuildSymbolsGroup(ScptFileInfo file, ScptPayloadInfo payload) {
      NodeListItem root = new NodeListItem("Symbols", payload.Symbols.Count.ToString(CultureInfo.InvariantCulture));
      for (Int32 i = 0; i < payload.Symbols.Count; i++) {
        ScptSymbolInfo symbol = payload.Symbols[i];
        String text = SymbolTypeName(symbol.Type);
        if (symbol.FunctionNameIndex >= 0) text += " " + ResolveName(file, payload, symbol.FunctionNameIndex);
        if (symbol.FunctionIndex >= 0) text += " func#" + symbol.FunctionIndex.ToString(CultureInfo.InvariantCulture);
        if (symbol.SectionIndex >= 0) text += " section#" + symbol.SectionIndex.ToString(CultureInfo.InvariantCulture) + "+" + Hex(symbol.Offset);
        else if (symbol.Offset != 0) text += " @" + Hex(symbol.Offset);
        root.children.Add(new NodeListItem("#" + i.ToString(CultureInfo.InvariantCulture), text));
      }
      return root;
    }

    private static NodeListItem BuildRelocationsGroup(ScptPayloadInfo payload) {
      NodeListItem root = new NodeListItem("Relocations", payload.Relocations.Count.ToString(CultureInfo.InvariantCulture));
      for (Int32 i = 0; i < payload.Relocations.Count; i++) {
        ScptRelocationInfo relocation = payload.Relocations[i];
        String kind = relocation.Type == 1 ? "absolute (R_386_32)" : "PC-relative (R_386_PC32)";
        root.children.Add(new NodeListItem("#" + i.ToString(CultureInfo.InvariantCulture),
          "symbol#" + relocation.SymbolIndex.ToString(CultureInfo.InvariantCulture) + ", " + kind +
          ", section#" + relocation.SectionIndex.ToString(CultureInfo.InvariantCulture) + "+" + Hex(relocation.Offset) +
          ", addend " + relocation.Addend.ToString(CultureInfo.InvariantCulture)));
      }
      return root;
    }

    private static NodeListItem BuildFlaggablesGroup(ScptFileInfo file, ScptPayloadInfo payload) {
      NodeListItem root = new NodeListItem("HSL declarations / flaggables", payload.Flaggables.Count.ToString(CultureInfo.InvariantCulture));
      for (Int32 i = 0; i < payload.Flaggables.Count; i++) {
        ScptFlaggableInfo flaggable = payload.Flaggables[i];
        String declaration = FormatFlagPrefixes(flaggable.Flags) + " " + ResolveName(file, payload, flaggable.NameIndex) + FormatSignature(payload, flaggable.SignatureIndex);
        declaration += "  [line " + flaggable.Line.ToString(CultureInfo.InvariantCulture) + ", flags " + Hex(flaggable.Flags) + "]";
        root.children.Add(new NodeListItem("#" + i.ToString(CultureInfo.InvariantCulture), declaration));
      }
      return root;
    }

    private static NodeListItem BuildFieldsGroup(ScptPayloadInfo payload) {
      NodeListItem root = new NodeListItem("Referenced GOM fields", payload.Fields.Count.ToString(CultureInfo.InvariantCulture));
      for (Int32 i = 0; i < payload.Fields.Count; i++) {
        ScptFieldInfo field = payload.Fields[i];
        String cls = ResolveDomId(field.ClassId);
        String fld = ResolveDomId(field.FieldId);
        root.children.Add(new NodeListItem("#" + i.ToString(CultureInfo.InvariantCulture), cls + " → " + fld));
      }
      return root;
    }

    private static String ResolveName(ScptFileInfo file, ScptPayloadInfo payload, Int32 index) {
      if (file.ContentVersion == 4) {
        if (index >= 0 && index < payload.Values.Count) {
          Object value = payload.Values[index].Value;
          if (value is String text && !String.IsNullOrWhiteSpace(text)) return text;
        }
        return "value#" + index.ToString(CultureInfo.InvariantCulture);
      }

      if (index >= 0 && index < payload.Names.Count) {
        ScptNameInfo name = payload.Names[index];
        if (!String.IsNullOrWhiteSpace(name.Text)) return name.Text;
        return "fnv32:0x" + unchecked((UInt64)name.Hash).ToString("X8", CultureInfo.InvariantCulture);
      }
      return "name#" + index.ToString(CultureInfo.InvariantCulture);
    }

    private static String ResolveType(ScptPayloadInfo payload, Int32 index) {
      return index >= 0 && index < payload.Types.Count ? FormatType(payload.Types[index]) : "type#" + index.ToString(CultureInfo.InvariantCulture);
    }

    private static String ResolveValue(ScptPayloadInfo payload, Int32 index) {
      return index >= 0 && index < payload.Values.Count ? FormatValue(payload.Values[index].Value) : "value#" + index.ToString(CultureInfo.InvariantCulture);
    }

    private static String FormatSignature(ScptPayloadInfo payload, Int32 signatureIndex) {
      if (signatureIndex < 0 || signatureIndex >= payload.Signatures.Count) return "(signature#" + signatureIndex.ToString(CultureInfo.InvariantCulture) + ")";
      ScptSignatureInfo signature = payload.Signatures[signatureIndex];
      ScptSignatureArgumentInfo returnArg = signature.Arguments.FirstOrDefault(arg => arg.Direction == 1);
      List<String> args = new List<String>();
      Int32 inputIndex = 1;
      Int32 refIndex = 1;
      foreach (ScptSignatureArgumentInfo arg in signature.Arguments) {
        if (arg.Direction == 1) continue;
        String type = ResolveType(payload, arg.TypeIndex);
        if (arg.Direction == 0) {
          if (!String.Equals(type, "Void", StringComparison.Ordinal)) args.Add("Me as " + type);
          else args.Add("Me");
          continue;
        }
        String direction = arg.Direction == 3 ? "references" : arg.Direction == 2 ? "copies" : "as";
        String argName = arg.Direction == 3
          ? "o" + (refIndex++).ToString(CultureInfo.InvariantCulture)
          : "a" + (inputIndex++).ToString(CultureInfo.InvariantCulture);
        args.Add(argName + " " + direction + " " + type);
      }
      String result = "(" + String.Join(", ", args) + ")";
      if (returnArg != null) {
        String returnType = ResolveType(payload, returnArg.TypeIndex);
        if (!String.Equals(returnType, "Void", StringComparison.Ordinal)) result += " as " + returnType;
      }
      return result;
    }

    private static String FormatFlagPrefixes(Int64 flags) {
      if ((flags & 0x20) != 0) return "self handler";
      if ((flags & 0x40) != 0) return "other handler";
      String prefix = String.Empty;
      if ((flags & 0x200) != 0) prefix += "unk8 ";
      if ((flags & 0x100) != 0) prefix += "debug ";
      String suffix = (flags & 0x10) != 0 ? " method" : " function";
      if ((flags & 0x02) != 0) suffix = " shared" + suffix;
      if ((flags & 0x08) != 0) return prefix + "untrusted" + suffix;
      if ((flags & 0x04) != 0) return prefix + "remote" + suffix;
      if ((flags & 0x01) != 0) return prefix + "public" + suffix;
      return prefix + "private" + suffix;
    }

    private static String FormatType(ScptTypeInfo type) {
      if (type == null) return "Unknown";
      switch (type.Type) {
        case 5:
          return "Enum" + ReferenceSuffix(type.ReferenceId, false);
        case 9:
          return "ClassView" + ReferenceSuffix(type.ReferenceId, false);
        case 15:
          return "NodeRef" + ReferenceSuffix(type.ReferenceId, true);
        case 7:
          return type.ValueType == null || type.ValueType.Type == 25 ? "List" : "List of " + FormatType(type.ValueType);
        case 8: {
          String result = "LookupList";
          if (type.IndexType != null && type.IndexType.Type != 25) result += " indexed by " + FormatType(type.IndexType);
          if (type.ValueType != null && type.ValueType.Type != 25) result += " of " + FormatType(type.ValueType);
          return result;
        }
        case 23: {
          String args = String.Join(", ", type.Arguments.Select(arg => (arg.Direction == 3 ? "references " : "as ") + FormatType(arg.Type)));
          String result = "FuncRef(" + args + ")";
          if (type.ReturnType != null && type.ReturnType.Type != 0) result += " as " + FormatType(type.ReturnType);
          return result;
        }
        case 24:
          return "Tuple of " + String.Join(", ", type.ElementTypes.Select(FormatType));
        default:
          return DomTypeName(type.Type);
      }
    }

    private static String ReferenceSuffix(ScptIntegerValue id, Boolean nodeRef) {
      if (id == null || (!id.Negative && id.Magnitude == 0)) return String.Empty;
      String resolved = ResolveDomId(id);
      return nodeRef ? " of " + resolved : " " + resolved;
    }

    private static String ResolveDomId(ScptIntegerValue id) {
      if (id == null) return "0";
      String numeric = id.DecimalText;
      if (id.Negative) return numeric;
      return ResolveDomId(id.Magnitude, numeric);
    }

    private static String ResolveDomId(BigInteger id) {
      String numeric = id.ToString(CultureInfo.InvariantCulture);
      if (id.Sign < 0 || id > UInt64.MaxValue) return numeric;
      return ResolveDomId((UInt64)id, numeric);
    }

    private static String ResolveDomId(UInt64 id, String numeric) {
      try {
        DataObjectModel dom = DomHandler.Instance.GetCurrentDOM();
        if (dom != null && dom.DomTypeMap.TryGetValue(id, out DomType found) && found != null && !String.IsNullOrWhiteSpace(found.Name))
          return found.Name + " (" + numeric + ")";
      }
      catch { }
      return numeric;
    }

    private static String DomTypeName(Int32 type) {
      return type switch {
        0 => "Void",
        1 => "ID",
        2 => "Integer",
        3 => "Boolean",
        4 => "Float",
        5 => "Enum",
        6 => "String",
        7 => "List",
        8 => "LookupList",
        9 => "ClassView",
        10 => "Association",
        11 => "Array",
        12 => "Table",
        13 => "Cubic",
        14 => "ScriptRef",
        15 => "NodeRef",
        16 => "GuiControl",
        17 => "Timer",
        18 => "Vector3",
        19 => "FsGUID",
        20 => "TimeInterval",
        21 => "DateTime",
        22 => "RawData",
        23 => "FuncRef",
        24 => "Tuple",
        25 => "Any",
        100 => "TypeIdx",
        101 => "FuncIdx",
        102 => "FieldIdx",
        103 => "GlobalIdx",
        104 => "ValueIdx",
        105 => "Iterator",
        106 => "RPC",
        107 => "CallFrame",
        108 => "FrontOrBack",
        _ => "DOM type " + type.ToString(CultureInfo.InvariantCulture)
      };
    }

    private static String FormatValue(Object value) {
      if (value == null) return "";
      if (value is String text) return text;
      if (value is Boolean boolean) return boolean ? "true" : "false";
      if (value is Single single) return single.ToString("R", CultureInfo.InvariantCulture);
      if (value is ScptIntegerValue integer) return FormatInteger(integer);
      if (value is Int64 integer64) return integer64.ToString(CultureInfo.InvariantCulture) + "  (" + Hex(integer64) + ")";
      if (value is Single[] vector && vector.Length >= 3)
        return "(" + vector[0].ToString("R", CultureInfo.InvariantCulture) + ", " + vector[1].ToString("R", CultureInfo.InvariantCulture) + ", " + vector[2].ToString("R", CultureInfo.InvariantCulture) + ")";
      if (value is List<ScptValueInfo> list) return list.Count.ToString(CultureInfo.InvariantCulture) + " item(s)";
      if (value is List<ScptLookupValueInfo> lookup) return lookup.Count.ToString(CultureInfo.InvariantCulture) + " entr" + (lookup.Count == 1 ? "y" : "ies");
      if (value is List<ScptClassFieldValueInfo> fields) return fields.Count.ToString(CultureInfo.InvariantCulture) + " field(s)";
      return Convert.ToString(value, CultureInfo.InvariantCulture) ?? String.Empty;
    }

    private static String FormatInteger(ScptIntegerValue integer) {
      if (integer == null) return String.Empty;
      return integer.DecimalText + "  (" + integer.HexText + ")";
    }

    private static String SymbolTypeName(Int32 type) {
      return type switch {
        1 => "section",
        2 => "HSL function",
        3 => "HSL function !sep",
        4 => "literal constant",
        5 => "EF function",
        6 => "HeroMachine function",
        7 => "library/runtime function",
        _ => "symbol type " + type.ToString(CultureInfo.InvariantCulture)
      };
    }

    private static String BytePreview(Byte[] data, Int32 max) {
      if (data == null || data.Length == 0) return "empty";
      Int32 count = Math.Min(max, data.Length);
      StringBuilder sb = new StringBuilder(count * 3 + 4);
      for (Int32 i = 0; i < count; i++) {
        if (i != 0) sb.Append(' ');
        sb.Append(data[i].ToString("X2", CultureInfo.InvariantCulture));
      }
      if (data.Length > count) sb.Append(" …");
      return sb.ToString();
    }

    private static String Hex(Int64 value) {
      return "0x" + unchecked((UInt64)value).ToString("X", CultureInfo.InvariantCulture);
    }

    private static String Hex(UInt32 value) {
      return "0x" + value.ToString("X", CultureInfo.InvariantCulture);
    }
  }
}
