using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace PugTools {
  /// <summary>
  /// Lightweight reader for SWTOR's retailclient/shaders/shaders.bin and standalone Direct3D
  /// compiled shader objects. SWTOR's cache contains legacy Direct3D 9 token streams (SM2/SM3)
  /// and newer/standalone DXBC objects depending on the client/tooling. The reader scans for both
  /// validated formats so it does not depend on a database or a version-specific cache index.
  /// </summary>
  internal static class ShaderBinaryReader {
    internal sealed class ShaderFileInfo {
      internal Byte[] Bytes;
      internal String SourcePath;
      internal readonly List<ShaderBlobInfo> Shaders = new List<ShaderBlobInfo>();
      internal Int32 DxbcSignatureCount;
      internal Int32 D3D9VersionCandidateCount;
    }

    internal sealed class ShaderBlobInfo {
      internal Int32 Index;
      internal Int64 Offset;
      internal Int32 Length;
      internal String Hash;
      internal String Format = "unknown";
      internal String Profile = "unknown";
      internal UInt32 ProgramType;
      internal Int32 Major;
      internal Int32 Minor;
      internal readonly List<ShaderChunkInfo> Chunks = new List<ShaderChunkInfo>();
      internal readonly List<ShaderConstantBufferInfo> ConstantBuffers = new List<ShaderConstantBufferInfo>();
      internal readonly List<ShaderResourceInfo> Resources = new List<ShaderResourceInfo>();
      internal readonly List<ShaderSignatureInfo> Inputs = new List<ShaderSignatureInfo>();
      internal readonly List<ShaderSignatureInfo> Outputs = new List<ShaderSignatureInfo>();
      internal String Creator;
    }

    internal sealed class ShaderChunkInfo {
      internal String FourCC;
      internal Int32 Offset;
      internal Int32 Length;
    }

    internal sealed class ShaderConstantBufferInfo {
      internal String Name;
      internal UInt32 Size;
      internal UInt32 Type;
      internal UInt32 Flags;
      internal readonly List<ShaderVariableInfo> Variables = new List<ShaderVariableInfo>();
    }

    internal sealed class ShaderVariableInfo {
      internal String Name;
      internal UInt32 StartOffset;
      internal UInt32 Size;
      internal UInt32 Flags;
      internal String RegisterSet;
      internal UInt32 RegisterIndex;
      internal UInt32 RegisterCount;
      internal String TypeName;
    }

    internal sealed class ShaderResourceInfo {
      internal String Name;
      internal UInt32 Type;
      internal UInt32 ReturnType;
      internal UInt32 Dimension;
      internal UInt32 Samples;
      internal UInt32 BindPoint;
      internal UInt32 BindCount;
      internal UInt32 Flags;
    }

    internal sealed class ShaderSignatureInfo {
      internal String Semantic;
      internal UInt32 SemanticIndex;
      internal UInt32 SystemValue;
      internal UInt32 ComponentType;
      internal UInt32 Register;
      internal Byte Mask;
      internal Byte ReadWriteMask;
      internal UInt32 Stream;
    }

    internal static ShaderFileInfo Parse(String path) {
      if (String.IsNullOrWhiteSpace(path)) throw new ArgumentNullException(nameof(path));
      Byte[] bytes = System.IO.File.ReadAllBytes(path);
      ShaderFileInfo file = new ShaderFileInfo { Bytes = bytes, SourcePath = path };
      ParseBytes(file);
      return file;
    }

    internal static ShaderFileInfo Parse(Byte[] bytes, String sourcePath = null) {
      if (bytes == null) throw new ArgumentNullException(nameof(bytes));
      ShaderFileInfo file = new ShaderFileInfo { Bytes = bytes, SourcePath = sourcePath };
      ParseBytes(file);
      return file;
    }

    internal static Byte[] GetShaderBytes(ShaderFileInfo file, ShaderBlobInfo shader) {
      if (file?.Bytes == null || shader == null || shader.Offset < 0 || shader.Length <= 0
          || shader.Offset + shader.Length > file.Bytes.LongLength) return Array.Empty<Byte>();
      Byte[] result = new Byte[shader.Length];
      Buffer.BlockCopy(file.Bytes, checked((Int32)shader.Offset), result, 0, shader.Length);
      return result;
    }

    private static void ParseBytes(ShaderFileInfo file) {
      Byte[] data = file.Bytes;
      if (data.Length < 8) return;

      List<ShaderBlobInfo> found = new List<ShaderBlobInfo>();

      // DXBC (D3D10/11) containers.
      Int32 offset = 0;
      while (offset <= data.Length - 32) {
        Int32 dxbc = FindDxbc(data, offset);
        if (dxbc < 0) break;
        file.DxbcSignatureCount++;
        if (TryParseDxbc(data, dxbc, out ShaderBlobInfo shader)) {
          found.Add(shader);
          offset = dxbc + Math.Max(shader.Length, 4);
        } else {
          offset = dxbc + 4;
        }
      }

      // SWTOR's shipped shaders.bin is primarily a cache of legacy D3D9 shader token streams.
      // They do not have a "DXBC" magic; they start with a shader-version DWORD such as
      // 0xFFFE0300 (vs_3_0) or 0xFFFF0300 (ps_3_0) and terminate with D3DSIO_END.
      offset = 0;
      while (offset <= data.Length - 8) {
        Int32 candidate = FindD3D9Version(data, offset);
        if (candidate < 0) break;
        file.D3D9VersionCandidateCount++;

        // Ignore version-looking DWORDs that are embedded in an already validated DXBC blob.
        ShaderBlobInfo containingDxbc = found.FirstOrDefault(x => x.Format == "DXBC"
          && candidate >= x.Offset && candidate < x.Offset + x.Length);
        if (containingDxbc != null) {
          offset = checked((Int32)Math.Min(data.LongLength, containingDxbc.Offset + containingDxbc.Length));
          continue;
        }

        if (TryParseD3D9(data, candidate, out ShaderBlobInfo shader)) {
          found.Add(shader);
          offset = candidate + Math.Max(shader.Length, 4);
        } else {
          offset = candidate + 4;
        }
      }

      foreach (ShaderBlobInfo shader in found.OrderBy(x => x.Offset)) {
        shader.Index = file.Shaders.Count;
        file.Shaders.Add(shader);
      }
    }

    private static Int32 FindDxbc(Byte[] data, Int32 start) {
      for (Int32 i = Math.Max(0, start); i <= data.Length - 4; i++) {
        if (data[i] == (Byte)'D' && data[i + 1] == (Byte)'X'
            && data[i + 2] == (Byte)'B' && data[i + 3] == (Byte)'C') return i;
      }
      return -1;
    }

    private static Int32 FindD3D9Version(Byte[] data, Int32 start) {
      // Byte scan rather than a 4-byte aligned scan: shaders.bin records are normally aligned, but
      // standalone cache variants can prepend an odd-sized key/name field.
      for (Int32 i = Math.Max(0, start); i <= data.Length - 4; i++) {
        // Little endian version tokens are 00 03 FE FF (vs_3_0) / 00 03 FF FF (ps_3_0).
        if (data[i + 3] != 0xFF || (data[i + 2] != 0xFE && data[i + 2] != 0xFF)) continue;
        Int32 major = data[i + 1];
        Int32 minor = data[i];
        if (major >= 1 && major <= 3 && minor >= 0 && minor <= 4) return i;
      }
      return -1;
    }

    private static Boolean TryParseD3D9(Byte[] data, Int32 start, out ShaderBlobInfo shader) {
      shader = null;
      if (start < 0 || start > data.Length - 8) return false;

      UInt32 version = ReadU32(data, start);
      UInt16 kind = (UInt16)(version >> 16);
      if (kind != 0xFFFE && kind != 0xFFFF) return false;
      Int32 major = (Int32)((version >> 8) & 0xFF);
      Int32 minor = (Int32)(version & 0xFF);
      if (major < 1 || major > 3 || minor < 0 || minor > 4) return false;

      Int32 pos = start + 4;
      Int32 instructionCount = 0;
      Int32 maxEnd = Math.Min(data.Length, start + 8 * 1024 * 1024);
      ShaderBlobInfo parsed = new ShaderBlobInfo {
        Offset = start,
        Format = "D3D9",
        ProgramType = kind == 0xFFFE ? 1u : 0u,
        Major = major,
        Minor = minor,
        Profile = (kind == 0xFFFE ? "vs_" : "ps_")
          + major.ToString(CultureInfo.InvariantCulture) + "_" + minor.ToString(CultureInfo.InvariantCulture)
      };

      while (pos <= maxEnd - 4) {
        UInt32 token = ReadU32(data, pos);
        if (token == 0x0000FFFFu) { // D3DSIO_END
          Int32 length = pos + 4 - start;
          if (length < 8 || instructionCount == 0) return false;
          parsed.Length = length;
          parsed.Hash = ComputeMd5(data, start, length);
          shader = parsed;
          return true;
        }

        UInt32 opcode = token & 0xFFFFu;
        if (opcode == 0xFFFEu) { // D3DSIO_COMMENT
          Int32 dwordCount = checked((Int32)((token >> 16) & 0x7FFFu));
          if (dwordCount <= 0 || pos + 4L + dwordCount * 4L > maxEnd) return false;
          if (dwordCount >= 1 && ReadU32(data, pos + 4) == 0x42415443u) { // "CTAB"
            parsed.Chunks.Add(new ShaderChunkInfo {
              FourCC = "CTAB",
              Offset = pos - start,
              Length = dwordCount * 4
            });
            ParseD3D9ConstantTable(data, pos + 8, (dwordCount - 1) * 4, parsed);
          }
          pos += 4 + dwordCount * 4;
          continue;
        }

        // PHASE is a single token used by ps_1_4.
        if (opcode == 0xFFFDu) {
          instructionCount++;
          pos += 4;
          continue;
        }

        Int32 instructionLength;
        if (major >= 2) {
          // For SM2/SM3 the encoded length is the number of parameter DWORDs following the
          // instruction token (Mesa/Wine advance by 1 + this value).
          instructionLength = checked((Int32)((token >> 24) & 0x0Fu));
          if (instructionLength <= 0) return false;
          instructionLength += 1;
        } else {
          instructionLength = D3D9ShaderModel1InstructionLength(opcode);
          if (instructionLength <= 0) return false;
        }
        if (pos + instructionLength * 4L > maxEnd) return false;
        instructionCount++;
        if (instructionCount > 1_000_000) return false;
        pos += instructionLength * 4;
      }
      return false;
    }

    private static Int32 D3D9ShaderModel1InstructionLength(UInt32 opcode) {
      // Lengths include the instruction token itself. SWTOR's modern cache is SM3, but accepting
      // common SM1 opcodes makes the standalone reader useful for older extracted objects too.
      return opcode switch {
        0 => 1,   // nop
        1 => 3,   // mov
        2 => 4, 3 => 4, 4 => 4, 5 => 4, 6 => 4, 7 => 4, 8 => 4, // add/sub/mad/mul/rcp/rsq/dp3
        9 => 4, 10 => 4, 11 => 4, 12 => 4, 13 => 4,              // dp4/min/max/slt/sge
        14 => 2, 15 => 2, 16 => 2,                              // exp/log/lit
        17 => 4, 18 => 2, 19 => 3, 20 => 3,                     // dst/lrp/frc/m4x4
        21 => 3, 22 => 3, 23 => 3, 24 => 3, 25 => 3,            // matrix ops
        31 => 3,                                                 // dcl
        65 => 2, 66 => 2,                                       // texcoord/texkill
        67 => 2, 68 => 3, 69 => 3, 70 => 3, 71 => 3,            // tex/texbem/texbeml/texreg2ar/texreg2gb
        72 => 3, 73 => 2, 74 => 3, 75 => 3, 76 => 2,             // texm3x2pad/texm3x2tex/texm3x3pad/texm3x3tex/reserved0
        77 => 3, 78 => 3, 79 => 2, 80 => 3, 81 => 3, 82 => 4,    // texm3x3spec/texm3x3vspec/expp/logp/cnd/def
        83 => 2, 84 => 2, 85 => 3, 86 => 3, 87 => 3, 88 => 3,    // texreg2rgb/texdp3tex/texm3x2depth/texdp3/texm3x3/dp2add
        89 => 3, 90 => 2,                                       // dsx/dsy (defensive)
        _ => 0
      };
    }

    private static void ParseD3D9ConstantTable(Byte[] data, Int32 start, Int32 length, ShaderBlobInfo shader) {
      if (length < 28 || start < 0 || start + (Int64)length > data.Length) return;
      Int32 end = start + length;
      UInt32 headerSize = ReadU32(data, start);
      if (headerSize != 28) return;

      UInt32 creatorOffset = ReadU32(data, start + 4);
      UInt32 version = ReadU32(data, start + 8);
      UInt32 constantCount = ReadU32(data, start + 12);
      UInt32 constantInfoOffset = ReadU32(data, start + 16);
      UInt32 targetOffset = ReadU32(data, start + 24);
      if (constantCount > 65536 || constantInfoOffset > Int32.MaxValue) return;

      shader.Creator = ReadRelativeCString(data, start, end, creatorOffset);
      String target = ReadRelativeCString(data, start, end, targetOffset);
      if (!String.IsNullOrWhiteSpace(target) && (target.StartsWith("vs_", StringComparison.OrdinalIgnoreCase)
          || target.StartsWith("ps_", StringComparison.OrdinalIgnoreCase))) shader.Profile = target;
      if (shader.Profile == "unknown") ParseD3D9Version(shader, version);

      Dictionary<UInt16, ShaderConstantBufferInfo> groups = new Dictionary<UInt16, ShaderConstantBufferInfo>();
      for (UInt32 i = 0; i < constantCount; i++) {
        Int64 record = start + (Int64)constantInfoOffset + i * 20L;
        if (record < start || record + 20 > end) break;
        Int32 r = checked((Int32)record);
        UInt32 nameOffset = ReadU32(data, r);
        UInt16 registerSet = ReadU16(data, r + 4);
        UInt16 registerIndex = ReadU16(data, r + 6);
        UInt16 registerCount = ReadU16(data, r + 8);
        UInt32 typeInfoOffset = ReadU32(data, r + 12);
        String name = ReadRelativeCString(data, start, end, nameOffset) ?? "constant#" + i.ToString(CultureInfo.InvariantCulture);

        UInt16 parameterClass = 0;
        UInt16 parameterType = 0;
        UInt16 rows = 0;
        UInt16 columns = 0;
        UInt16 elements = 0;
        if (typeInfoOffset <= Int32.MaxValue) {
          Int32 ti = start + checked((Int32)typeInfoOffset);
          if (ti >= start && ti + 16 <= end) {
            parameterClass = ReadU16(data, ti);
            parameterType = ReadU16(data, ti + 2);
            rows = ReadU16(data, ti + 4);
            columns = ReadU16(data, ti + 6);
            elements = ReadU16(data, ti + 8);
          }
        }
        String typeName = D3D9ParameterTypeName(parameterType, parameterClass, rows, columns, elements);

        if (registerSet == 3 || IsD3D9SamplerType(parameterType)) {
          shader.Resources.Add(new ShaderResourceInfo {
            Name = name,
            Type = 3,
            Dimension = D3D9SamplerDimension(parameterType),
            BindPoint = registerIndex,
            BindCount = Math.Max((UInt32)registerCount, 1u)
          });
          continue;
        }

        if (!groups.TryGetValue(registerSet, out ShaderConstantBufferInfo group)) {
          group = new ShaderConstantBufferInfo {
            Name = D3D9RegisterSetName(registerSet),
            Type = registerSet,
            Size = 0
          };
          groups.Add(registerSet, group);
          shader.ConstantBuffers.Add(group);
        }
        group.Size = Math.Max(group.Size, (UInt32)registerIndex + registerCount);
        group.Variables.Add(new ShaderVariableInfo {
          Name = name,
          RegisterSet = D3D9RegisterPrefix(registerSet),
          RegisterIndex = registerIndex,
          RegisterCount = registerCount,
          TypeName = typeName,
          StartOffset = registerIndex,
          Size = registerCount
        });
      }
    }

    private static void ParseD3D9Version(ShaderBlobInfo shader, UInt32 version) {
      UInt16 kind = (UInt16)(version >> 16);
      Int32 major = (Int32)((version >> 8) & 0xFF);
      Int32 minor = (Int32)(version & 0xFF);
      if ((kind != 0xFFFE && kind != 0xFFFF) || major < 1 || major > 3) return;
      shader.ProgramType = kind == 0xFFFE ? 1u : 0u;
      shader.Major = major;
      shader.Minor = minor;
      shader.Profile = (kind == 0xFFFE ? "vs_" : "ps_")
        + major.ToString(CultureInfo.InvariantCulture) + "_" + minor.ToString(CultureInfo.InvariantCulture);
    }

    private static Boolean IsD3D9SamplerType(UInt16 value) => value >= 10 && value <= 14;

    private static UInt32 D3D9SamplerDimension(UInt16 value) => value switch {
      11 => 2, // sampler1D
      12 => 4, // sampler2D
      13 => 8, // sampler3D
      14 => 9, // samplerCUBE
      _ => 0
    };

    private static String D3D9RegisterSetName(UInt16 value) => value switch {
      0 => "Boolean constants",
      1 => "Integer constants",
      2 => "Float constants",
      3 => "Samplers",
      _ => "Register set " + value.ToString(CultureInfo.InvariantCulture)
    };

    private static String D3D9RegisterPrefix(UInt16 value) => value switch {
      0 => "b",
      1 => "i",
      2 => "c",
      3 => "s",
      _ => "r"
    };

    private static String D3D9ParameterTypeName(UInt16 type, UInt16 parameterClass, UInt16 rows, UInt16 columns, UInt16 elements) {
      String baseName = type switch {
        0 => "void", 1 => "bool", 2 => "int", 3 => "float", 4 => "string", 5 => "texture",
        6 => "texture1D", 7 => "texture2D", 8 => "texture3D", 9 => "textureCube",
        10 => "sampler", 11 => "sampler1D", 12 => "sampler2D", 13 => "sampler3D", 14 => "samplerCube",
        15 => "pixelShader", 16 => "vertexShader", 17 => "pixelFragment", 18 => "vertexFragment",
        _ => "type" + type.ToString(CultureInfo.InvariantCulture)
      };
      if (type >= 1 && type <= 3) {
        if (parameterClass == 1 && columns > 1) baseName += columns.ToString(CultureInfo.InvariantCulture);
        else if ((parameterClass == 2 || parameterClass == 3) && rows > 0 && columns > 0)
          baseName += rows.ToString(CultureInfo.InvariantCulture) + "x" + columns.ToString(CultureInfo.InvariantCulture);
      }
      if (elements > 1) baseName += "[" + elements.ToString(CultureInfo.InvariantCulture) + "]";
      return baseName;
    }

    private static Boolean TryParseDxbc(Byte[] data, Int32 start, out ShaderBlobInfo shader) {
      shader = null;
      if (start < 0 || start > data.Length - 32) return false;
      UInt32 totalSize = ReadU32(data, start + 24);
      UInt32 chunkCount = ReadU32(data, start + 28);
      if (totalSize < 32 || totalSize > Int32.MaxValue || start + (Int64)totalSize > data.Length) return false;
      if (chunkCount == 0 || chunkCount > 512 || 32L + chunkCount * 4L > totalSize) return false;

      ShaderBlobInfo parsed = new ShaderBlobInfo {
        Offset = start,
        Length = checked((Int32)totalSize),
        Hash = Hex(data, start + 4, 16),
        Format = "DXBC"
      };

      HashSet<Int32> seenChunkOffsets = new HashSet<Int32>();
      for (UInt32 i = 0; i < chunkCount; i++) {
        UInt32 relative = ReadU32(data, start + 32 + checked((Int32)i * 4));
        if (relative > totalSize - 8 || relative > Int32.MaxValue) return false;
        Int32 chunkStart = start + checked((Int32)relative);
        if (!seenChunkOffsets.Add(chunkStart)) continue;
        UInt32 chunkLength = ReadU32(data, chunkStart + 4);
        if (chunkLength > Int32.MaxValue || chunkStart + 8L + chunkLength > start + (Int64)totalSize) return false;
        String fourcc = Encoding.ASCII.GetString(data, chunkStart, 4);
        ShaderChunkInfo chunk = new ShaderChunkInfo {
          FourCC = fourcc,
          Offset = checked((Int32)relative),
          Length = checked((Int32)chunkLength)
        };
        parsed.Chunks.Add(chunk);

        Int32 payload = chunkStart + 8;
        Int32 payloadLength = checked((Int32)chunkLength);
        if ((fourcc == "SHDR" || fourcc == "SHEX") && payloadLength >= 4)
          ParseProgramVersion(parsed, ReadU32(data, payload));
        else if (fourcc == "RDEF")
          ParseRdef(data, payload, payloadLength, parsed);
        else if (fourcc == "ISGN" || fourcc == "ISG1")
          ParseSignature(data, payload, payloadLength, parsed.Inputs, fourcc == "ISG1");
        else if (fourcc == "OSGN" || fourcc == "OSG1" || fourcc == "OSG5")
          ParseSignature(data, payload, payloadLength, parsed.Outputs, fourcc != "OSGN");
      }

      // Some very old blobs omit/rename the shader bytecode chunk. RDEF also stores the target
      // profile token, so use it as a fallback when possible.
      if (parsed.Profile == "unknown") {
        ShaderChunkInfo rdef = parsed.Chunks.FirstOrDefault(x => x.FourCC == "RDEF");
        if (rdef != null) {
          Int32 p = start + rdef.Offset + 8;
          if (rdef.Length >= 20) ParseProgramVersion(parsed, ReadU32(data, p + 16));
        }
      }

      shader = parsed;
      return true;
    }

    private static void ParseProgramVersion(ShaderBlobInfo shader, UInt32 token) {
      UInt32 programType = token >> 16;
      Int32 major = checked((Int32)((token >> 4) & 0xF));
      Int32 minor = checked((Int32)(token & 0xF));
      if (major <= 0 || major > 9) return;
      shader.ProgramType = programType;
      shader.Major = major;
      shader.Minor = minor;
      String prefix = programType switch {
        0 => "ps",
        1 => "vs",
        2 => "gs",
        3 => "hs",
        4 => "ds",
        5 => "cs",
        _ => "shader" + programType.ToString(CultureInfo.InvariantCulture)
      };
      shader.Profile = prefix + "_" + major.ToString(CultureInfo.InvariantCulture) + "_" + minor.ToString(CultureInfo.InvariantCulture);
    }

    private static void ParseRdef(Byte[] data, Int32 start, Int32 length, ShaderBlobInfo shader) {
      if (length < 28) return;
      Int32 end = start + length;
      UInt32 cbCount = ReadU32(data, start);
      UInt32 cbOffset = ReadU32(data, start + 4);
      UInt32 resourceCount = ReadU32(data, start + 8);
      UInt32 resourceOffset = ReadU32(data, start + 12);
      UInt32 creatorOffset = ReadU32(data, start + 24);
      shader.Creator = ReadRelativeCString(data, start, end, creatorOffset);

      if (cbCount <= 4096 && cbOffset < length) {
        for (UInt32 i = 0; i < cbCount; i++) {
          Int64 descriptor = start + (Int64)cbOffset + i * 24L;
          if (descriptor < start || descriptor + 24 > end) break;
          Int32 d = checked((Int32)descriptor);
          UInt32 nameOffset = ReadU32(data, d);
          UInt32 variableCount = ReadU32(data, d + 4);
          UInt32 variableOffset = ReadU32(data, d + 8);
          ShaderConstantBufferInfo cb = new ShaderConstantBufferInfo {
            Name = ReadRelativeCString(data, start, end, nameOffset) ?? "cbuffer#" + i.ToString(CultureInfo.InvariantCulture),
            Size = ReadU32(data, d + 12),
            Flags = ReadU32(data, d + 16),
            Type = ReadU32(data, d + 20)
          };

          if (variableCount <= 65536 && variableOffset < length) {
            for (UInt32 v = 0; v < variableCount; v++) {
              Int64 vd = start + (Int64)variableOffset + v * 24L;
              if (vd < start || vd + 24 > end) break;
              Int32 variableDescriptor = checked((Int32)vd);
              cb.Variables.Add(new ShaderVariableInfo {
                Name = ReadRelativeCString(data, start, end, ReadU32(data, variableDescriptor)) ?? "var#" + v.ToString(CultureInfo.InvariantCulture),
                StartOffset = ReadU32(data, variableDescriptor + 4),
                Size = ReadU32(data, variableDescriptor + 8),
                Flags = ReadU32(data, variableDescriptor + 12)
              });
            }
          }
          shader.ConstantBuffers.Add(cb);
        }
      }

      if (resourceCount <= 65536 && resourceOffset < length) {
        for (UInt32 i = 0; i < resourceCount; i++) {
          Int64 descriptor = start + (Int64)resourceOffset + i * 32L;
          if (descriptor < start || descriptor + 32 > end) break;
          Int32 d = checked((Int32)descriptor);
          shader.Resources.Add(new ShaderResourceInfo {
            Name = ReadRelativeCString(data, start, end, ReadU32(data, d)) ?? "resource#" + i.ToString(CultureInfo.InvariantCulture),
            Type = ReadU32(data, d + 4),
            ReturnType = ReadU32(data, d + 8),
            Dimension = ReadU32(data, d + 12),
            Samples = ReadU32(data, d + 16),
            BindPoint = ReadU32(data, d + 20),
            BindCount = ReadU32(data, d + 24),
            Flags = ReadU32(data, d + 28)
          });
        }
      }
    }

    private static void ParseSignature(Byte[] data, Int32 start, Int32 length,
                                       List<ShaderSignatureInfo> output, Boolean extended) {
      if (length < 8) return;
      Int32 end = start + length;
      UInt32 count = ReadU32(data, start);
      if (count > 4096) return;
      Int32 stride = extended ? 32 : 24;
      Int32 table = start + 8;
      if (table + (Int64)count * stride > end && extended) {
        // A few containers use the original 24-byte layout with ISG1/OSG1 names.
        stride = 24;
      }
      for (UInt32 i = 0; i < count; i++) {
        Int64 record = table + i * (Int64)stride;
        if (record < start || record + 24 > end) break;
        Int32 r = checked((Int32)record);
        ShaderSignatureInfo item = new ShaderSignatureInfo();
        Int32 baseOffset = 0;
        if (stride >= 32) {
          item.Stream = ReadU32(data, r);
          baseOffset = 4;
        }
        item.Semantic = ReadRelativeCString(data, start, end, ReadU32(data, r + baseOffset)) ?? "?";
        item.SemanticIndex = ReadU32(data, r + baseOffset + 4);
        item.SystemValue = ReadU32(data, r + baseOffset + 8);
        item.ComponentType = ReadU32(data, r + baseOffset + 12);
        item.Register = ReadU32(data, r + baseOffset + 16);
        Int32 maskOffset = r + baseOffset + 20;
        if (maskOffset + 2 <= end) {
          item.Mask = data[maskOffset];
          item.ReadWriteMask = data[maskOffset + 1];
        }
        output.Add(item);
      }
    }


    internal static String Disassemble(ShaderFileInfo file, ShaderBlobInfo shader, Int32 maxInstructions = 20000) {
      if (file?.Bytes == null || shader == null) return String.Empty;
      if (!String.Equals(shader.Format, "D3D9", StringComparison.OrdinalIgnoreCase))
        return "Assembly disassembly is currently available for Direct3D 9 shader bytecode.\r\n"
          + "This shader is " + (shader.Format ?? "unknown") + ".";
      if (shader.Offset < 0 || shader.Length < 8 || shader.Offset + shader.Length > file.Bytes.LongLength)
        return "Invalid shader byte range.";

      Byte[] data = file.Bytes;
      Int32 start = checked((Int32)shader.Offset);
      Int32 end = checked(start + shader.Length);
      Int32 pos = start + 4;
      Int32 instructionIndex = 0;
      StringBuilder output = new StringBuilder(Math.Max(2048, shader.Length * 4));
      output.Append("// ").Append(shader.Profile).Append("  shader #").Append(shader.Index)
        .Append("  @0x").Append(shader.Offset.ToString("X", CultureInfo.InvariantCulture)).AppendLine();
      output.Append("// ").Append(shader.Length.ToString("N0", CultureInfo.InvariantCulture)).Append(" bytes  MD5 ")
        .Append(shader.Hash ?? String.Empty).AppendLine();
      if (!String.IsNullOrWhiteSpace(shader.Creator)) output.Append("// Creator: ").Append(shader.Creator).AppendLine();
      output.AppendLine(shader.Profile);

      while (pos <= end - 4 && instructionIndex < maxInstructions) {
        UInt32 token = ReadU32(data, pos);
        UInt32 opcode = token & 0xFFFFu;
        Int32 relative = pos - start;

        if (opcode == 0xFFFFu) {
          output.Append(relative.ToString("X4", CultureInfo.InvariantCulture)).Append(": end").AppendLine();
          break;
        }
        if (opcode == 0xFFFEu) {
          Int32 dwords = checked((Int32)((token >> 16) & 0x7FFFu));
          String tag = dwords > 0 && pos + 8 <= end ? FourCC(ReadU32(data, pos + 4)) : String.Empty;
          output.Append(relative.ToString("X4", CultureInfo.InvariantCulture)).Append(": // comment ")
            .Append(dwords.ToString(CultureInfo.InvariantCulture)).Append(" dwords");
          if (!String.IsNullOrWhiteSpace(tag)) output.Append("  ").Append(tag);
          output.AppendLine();
          Int64 next = pos + 4L + dwords * 4L;
          if (dwords < 0 || next > end) break;
          pos = checked((Int32)next);
          continue;
        }
        if (opcode == 0xFFFDu) {
          output.Append(relative.ToString("X4", CultureInfo.InvariantCulture)).Append(": phase").AppendLine();
          pos += 4;
          instructionIndex++;
          continue;
        }

        Int32 tokenCount;
        if (shader.Major >= 2) {
          Int32 parameterCount = checked((Int32)((token >> 24) & 0x0Fu));
          if (parameterCount <= 0) parameterCount = D3D9NoParameterInstruction(opcode) ? 0 : 1;
          tokenCount = 1 + parameterCount;
        } else {
          tokenCount = D3D9ShaderModel1InstructionLength(opcode);
        }
        if (tokenCount <= 0 || pos + tokenCount * 4L > end) {
          output.Append(relative.ToString("X4", CultureInfo.InvariantCulture)).Append(": .token 0x")
            .Append(token.ToString("X8", CultureInfo.InvariantCulture)).Append("  // unable to decode length").AppendLine();
          pos += 4;
          instructionIndex++;
          continue;
        }

        UInt32[] parameters = new UInt32[Math.Max(0, tokenCount - 1)];
        for (Int32 i = 0; i < parameters.Length; i++) parameters[i] = ReadU32(data, pos + 4 + i * 4);
        output.Append(relative.ToString("X4", CultureInfo.InvariantCulture)).Append(": ")
          .Append(DisassembleD3D9Instruction(shader, token, opcode, parameters)).AppendLine();
        pos += tokenCount * 4;
        instructionIndex++;
      }

      if (instructionIndex >= maxInstructions)
        output.AppendLine("// ... disassembly truncated at " + maxInstructions.ToString("N0", CultureInfo.InvariantCulture) + " instructions ...");
      return output.ToString();
    }

    private static String DisassembleD3D9Instruction(ShaderBlobInfo shader, UInt32 instructionToken, UInt32 opcode, UInt32[] p) {
      String mnemonic = D3D9OpcodeName(opcode);
      if (mnemonic.StartsWith("op_", StringComparison.Ordinal)) {
        return mnemonic + (p.Length == 0 ? String.Empty : " " + String.Join(", ", p.Select(x => "0x" + x.ToString("X8", CultureInfo.InvariantCulture))));
      }

      if (opcode == 31u) { // dcl
        if (p.Length < 2) return mnemonic + " " + String.Join(", ", p.Select(x => "0x" + x.ToString("X8", CultureInfo.InvariantCulture)));
        UInt32 declaration = p[0];
        UInt32 register = p[1];
        Int32 regType = D3D9RegisterType(register);
        if (regType == 10) {
          Int32 textureType = checked((Int32)((declaration >> 27) & 0xFu));
          String suffix = textureType switch { 2 => "_2d", 3 => "_cube", 4 => "_volume", _ => String.Empty };
          return "dcl" + suffix + " " + FormatD3D9Register(shader, register, true);
        }
        Int32 usage = checked((Int32)(declaration & 0x1Fu));
        Int32 usageIndex = checked((Int32)((declaration >> 16) & 0xFu));
        String semantic = D3D9UsageName(usage) + (usageIndex == 0 ? String.Empty : usageIndex.ToString(CultureInfo.InvariantCulture));
        return "dcl_" + semantic + " " + FormatD3D9Register(shader, register, true);
      }

      if (opcode == 81u && p.Length >= 5) { // def
        return "def " + FormatD3D9Register(shader, p[0], true) + ", "
          + UIntAsFloat(p[1]).ToString("0.########", CultureInfo.InvariantCulture) + ", "
          + UIntAsFloat(p[2]).ToString("0.########", CultureInfo.InvariantCulture) + ", "
          + UIntAsFloat(p[3]).ToString("0.########", CultureInfo.InvariantCulture) + ", "
          + UIntAsFloat(p[4]).ToString("0.########", CultureInfo.InvariantCulture);
      }
      if (opcode == 48u && p.Length >= 5) { // defi
        return "defi " + FormatD3D9Register(shader, p[0], true) + ", "
          + unchecked((Int32)p[1]).ToString(CultureInfo.InvariantCulture) + ", "
          + unchecked((Int32)p[2]).ToString(CultureInfo.InvariantCulture) + ", "
          + unchecked((Int32)p[3]).ToString(CultureInfo.InvariantCulture) + ", "
          + unchecked((Int32)p[4]).ToString(CultureInfo.InvariantCulture);
      }
      if (opcode == 47u && p.Length >= 2) { // defb
        return "defb " + FormatD3D9Register(shader, p[0], true) + ", " + (p[1] == 0 ? "false" : "true");
      }

      Boolean hasDestination = D3D9HasDestination(opcode);
      if (hasDestination && p.Length > 0 && (p[0] & (1u << 20)) != 0) mnemonic += "_sat";
      if ((opcode == 41u || opcode == 45u || opcode == 94u) && ((instructionToken >> 16) & 0x7u) != 0)
        mnemonic += D3D9ComparisonSuffix(checked((Int32)((instructionToken >> 16) & 0x7u)));

      List<String> operands = new List<String>();
      for (Int32 i = 0; i < p.Length; i++) {
        Boolean destination = hasDestination && i == 0;
        operands.Add(FormatD3D9Register(shader, p[i], destination));
      }
      return mnemonic + (operands.Count == 0 ? String.Empty : " " + String.Join(", ", operands));
    }

    private static Boolean D3D9NoParameterInstruction(UInt32 opcode) => opcode switch {
      0u => true, 28u => true, 29u => true, 39u => true, 42u => true, 43u => true, 44u => true, _ => false
    };

    private static Boolean D3D9HasDestination(UInt32 opcode) => opcode switch {
      0u => false, 25u => false, 26u => false, 27u => false, 28u => false, 29u => false, 30u => false,
      31u => false, 38u => false, 39u => false, 40u => false, 41u => false, 42u => false, 43u => false,
      44u => false, 45u => false, 47u => false, 48u => false, 81u => false, 96u => false, _ => true
    };

    private static String D3D9OpcodeName(UInt32 opcode) => opcode switch {
      0 => "nop", 1 => "mov", 2 => "add", 3 => "sub", 4 => "mad", 5 => "mul", 6 => "rcp", 7 => "rsq",
      8 => "dp3", 9 => "dp4", 10 => "min", 11 => "max", 12 => "slt", 13 => "sge", 14 => "exp", 15 => "log",
      16 => "lit", 17 => "dst", 18 => "lrp", 19 => "frc", 20 => "m4x4", 21 => "m4x3", 22 => "m3x4",
      23 => "m3x3", 24 => "m3x2", 25 => "call", 26 => "callnz", 27 => "loop", 28 => "ret", 29 => "endloop",
      30 => "label", 31 => "dcl", 32 => "pow", 33 => "crs", 34 => "sgn", 35 => "abs", 36 => "nrm",
      37 => "sincos", 38 => "rep", 39 => "endrep", 40 => "if", 41 => "ifc", 42 => "else", 43 => "endif",
      44 => "break", 45 => "breakc", 46 => "mova", 47 => "defb", 48 => "defi",
      64 => "texcoord", 65 => "texkill", 66 => "texld", 67 => "texbem", 68 => "texbeml", 69 => "texreg2ar",
      70 => "texreg2gb", 71 => "texm3x2pad", 72 => "texm3x2tex", 73 => "texm3x3pad", 74 => "texm3x3tex",
      76 => "texm3x3spec", 77 => "texm3x3vspec", 78 => "expp", 79 => "logp", 80 => "cnd", 81 => "def",
      82 => "texreg2rgb", 83 => "texdp3tex", 84 => "texm3x2depth", 85 => "texdp3", 86 => "texm3x3",
      87 => "texdepth", 88 => "cmp", 89 => "bem", 90 => "dp2add", 91 => "dsx", 92 => "dsy",
      93 => "texldd", 94 => "setp", 95 => "texldl", 96 => "breakp",
      _ => "op_" + opcode.ToString("X4", CultureInfo.InvariantCulture)
    };

    private static String D3D9ComparisonSuffix(Int32 control) => control switch {
      1 => "_gt", 2 => "_eq", 3 => "_ge", 4 => "_lt", 5 => "_ne", 6 => "_le", _ => String.Empty
    };

    private static String D3D9UsageName(Int32 usage) => usage switch {
      0 => "position", 1 => "blendweight", 2 => "blendindices", 3 => "normal", 4 => "psize", 5 => "texcoord",
      6 => "tangent", 7 => "binormal", 8 => "tessfactor", 9 => "positiont", 10 => "color", 11 => "fog",
      12 => "depth", 13 => "sample", _ => "usage" + usage.ToString(CultureInfo.InvariantCulture)
    };

    private static Int32 D3D9RegisterType(UInt32 token) => checked((Int32)(((token >> 28) & 0x7u) | ((token >> 8) & 0x18u)));

    private static String FormatD3D9Register(ShaderBlobInfo shader, UInt32 token, Boolean destination) {
      Int32 regType = D3D9RegisterType(token);
      Int32 regNum = checked((Int32)(token & 0x7FFu));
      Boolean pixel = shader?.Profile?.StartsWith("ps_", StringComparison.OrdinalIgnoreCase) == true;
      String name = regType switch {
        0 => "r" + regNum.ToString(CultureInfo.InvariantCulture),
        1 => "v" + regNum.ToString(CultureInfo.InvariantCulture),
        2 => "c" + regNum.ToString(CultureInfo.InvariantCulture),
        3 => (pixel ? "t" : "a") + regNum.ToString(CultureInfo.InvariantCulture),
        4 => regNum switch { 0 => "oPos", 1 => "oFog", 2 => "oPts", _ => "oRast" + regNum.ToString(CultureInfo.InvariantCulture) },
        5 => "oD" + regNum.ToString(CultureInfo.InvariantCulture),
        6 => shader != null && shader.Major >= 3 ? "o" + regNum.ToString(CultureInfo.InvariantCulture) : "oT" + regNum.ToString(CultureInfo.InvariantCulture),
        7 => "i" + regNum.ToString(CultureInfo.InvariantCulture),
        8 => "oC" + regNum.ToString(CultureInfo.InvariantCulture),
        9 => "oDepth",
        10 => "s" + regNum.ToString(CultureInfo.InvariantCulture),
        11 => "c" + (2048 + regNum).ToString(CultureInfo.InvariantCulture),
        12 => "c" + (4096 + regNum).ToString(CultureInfo.InvariantCulture),
        13 => "c" + (6144 + regNum).ToString(CultureInfo.InvariantCulture),
        14 => "b" + regNum.ToString(CultureInfo.InvariantCulture),
        15 => "aL",
        16 => "h" + regNum.ToString(CultureInfo.InvariantCulture),
        17 => regNum == 0 ? "vPos" : regNum == 1 ? "vFace" : "misc" + regNum.ToString(CultureInfo.InvariantCulture),
        18 => "l" + regNum.ToString(CultureInfo.InvariantCulture),
        19 => "p" + regNum.ToString(CultureInfo.InvariantCulture),
        _ => "reg" + regType.ToString(CultureInfo.InvariantCulture) + "[" + regNum.ToString(CultureInfo.InvariantCulture) + "]"
      };

      if (destination) {
        Int32 mask = checked((Int32)((token >> 16) & 0xFu));
        if (mask != 0 && mask != 0xF) {
          StringBuilder m = new StringBuilder(4);
          if ((mask & 1) != 0) m.Append('x');
          if ((mask & 2) != 0) m.Append('y');
          if ((mask & 4) != 0) m.Append('z');
          if ((mask & 8) != 0) m.Append('w');
          name += "." + m;
        }
        return name;
      }

      Int32 swizzle = checked((Int32)((token >> 16) & 0xFFu));
      if (swizzle != 0xE4) {
        const String components = "xyzw";
        char[] chars = new char[4];
        for (Int32 i = 0; i < 4; i++) chars[i] = components[(swizzle >> (i * 2)) & 3];
        name += "." + new String(chars);
      }
      Int32 modifier = checked((Int32)((token >> 24) & 0xFu));
      name = modifier switch {
        1 => "-" + name,
        2 => name + "_bias",
        3 => "-" + name + "_bias",
        4 => name + "_bx2",
        5 => "-" + name + "_bx2",
        6 => "1-" + name,
        7 => name + "_x2",
        8 => "-" + name + "_x2",
        9 => name + "_dz",
        10 => name + "_dw",
        11 => "abs(" + name + ")",
        12 => "-abs(" + name + ")",
        13 => "!" + name,
        _ => name
      };
      return name;
    }

    private static Single UIntAsFloat(UInt32 value) {
      Byte[] bytes = BitConverter.GetBytes(value);
      return BitConverter.ToSingle(bytes, 0);
    }

    private static String FourCC(UInt32 value) {
      char a = (char)(value & 0xFFu), b = (char)((value >> 8) & 0xFFu), c = (char)((value >> 16) & 0xFFu), d = (char)((value >> 24) & 0xFFu);
      if (a < 32 || b < 32 || c < 32 || d < 32 || a > 126 || b > 126 || c > 126 || d > 126) return "0x" + value.ToString("X8", CultureInfo.InvariantCulture);
      return new String(new[] { a, b, c, d });
    }

    internal static String ResourceTypeName(UInt32 value) => value switch {
      0 => "CBuffer",
      1 => "TBuffer",
      2 => "Texture",
      3 => "Sampler",
      4 => "UAV RWTyped",
      5 => "Structured",
      6 => "UAV RWStructured",
      7 => "ByteAddress",
      8 => "UAV RWByteAddress",
      9 => "UAV AppendStructured",
      10 => "UAV ConsumeStructured",
      11 => "UAV RWStructuredCounter",
      _ => value.ToString(CultureInfo.InvariantCulture)
    };

    internal static String DimensionName(UInt32 value) => value switch {
      0 => "Unknown",
      1 => "Buffer",
      2 => "Texture1D",
      3 => "Texture1DArray",
      4 => "Texture2D",
      5 => "Texture2DArray",
      6 => "Texture2DMS",
      7 => "Texture2DMSArray",
      8 => "Texture3D",
      9 => "TextureCube",
      10 => "TextureCubeArray",
      11 => "BufferEx",
      _ => value.ToString(CultureInfo.InvariantCulture)
    };

    private static UInt16 ReadU16(Byte[] data, Int32 offset) {
      if (data == null || offset < 0 || offset > data.Length - 2) return 0;
      return (UInt16)(data[offset] | (data[offset + 1] << 8));
    }

    private static String ComputeMd5(Byte[] data, Int32 offset, Int32 length) {
      if (data == null || offset < 0 || length <= 0 || offset + (Int64)length > data.Length) return String.Empty;
      using MD5 md5 = MD5.Create();
      Byte[] digest = md5.ComputeHash(data, offset, length);
      StringBuilder sb = new StringBuilder(digest.Length * 2);
      foreach (Byte value in digest) sb.Append(value.ToString("x2", CultureInfo.InvariantCulture));
      return sb.ToString();
    }

    private static UInt32 ReadU32(Byte[] data, Int32 offset) {
      if (data == null || offset < 0 || offset > data.Length - 4) return 0;
      return (UInt32)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24));
    }

    private static String ReadRelativeCString(Byte[] data, Int32 baseOffset, Int32 end, UInt32 relative) {
      if (relative > Int32.MaxValue) return null;
      Int32 start = baseOffset + checked((Int32)relative);
      if (start < baseOffset || start >= end || start >= data.Length) return null;
      Int32 finish = start;
      Int32 max = Math.Min(end, Math.Min(data.Length, start + 4096));
      while (finish < max && data[finish] != 0) finish++;
      if (finish == start) return String.Empty;
      try { return Encoding.UTF8.GetString(data, start, finish - start); }
      catch { return null; }
    }

    private static String Hex(Byte[] data, Int32 offset, Int32 length) {
      if (data == null || offset < 0 || length <= 0 || offset + length > data.Length) return String.Empty;
      StringBuilder sb = new StringBuilder(length * 2);
      for (Int32 i = 0; i < length; i++) sb.Append(data[offset + i].ToString("x2", CultureInfo.InvariantCulture));
      return sb.ToString();
    }
  }
}
