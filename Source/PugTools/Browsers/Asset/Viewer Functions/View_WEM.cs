using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NAudio.Vorbis;
using NAudio.Wave;

namespace PugTools {
  internal class ViewWEM {
    private readonly UInt32 _id;
    internal UInt32 Id => _id;
    internal Byte[] Data { get; set; }
    internal Int64 Length { get; set; }
    internal String WemName { get; set; }
    internal Int64 Offset { get; set; }
    internal String OggName { get; set; }
    internal WaveStream Vorbis { get; set; }
    internal String LastError { get; private set; }
    internal Boolean IsBeta { get; set; }

    internal ViewWEM(BinaryReader br) {
      _id = br.ReadUInt32();
      Offset = br.ReadUInt32();
      Length = br.ReadUInt32();
      WemName = _id.ToString() + ".wem";
      OggName = _id.ToString() + ".ogg";
    }

    internal ViewWEM(String name = null, Stream inputStream = null, Boolean? isBeta = null) {
      if (name != null) {
        String normalized = name.Replace('\\', '/');
        String leaf = Path.GetFileName(normalized);
        if (String.IsNullOrWhiteSpace(leaf)) leaf = "audio.wem";
        WemName = leaf;
        OggName = Path.ChangeExtension(leaf, ".ogg");

        // Beta streamed audio is shipped as /resources/bnk/streamed/*.ogg even though the payload
        // is still a RIFF/Wwise WEM. Current-client streamed audio lives below bnk2 as *.wem.
        IsBeta = isBeta ?? normalized.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase)
          || normalized.IndexOf("/bnk/", StringComparison.OrdinalIgnoreCase) >= 0;
      } else {
        IsBeta = isBeta ?? false;
      }

      if (inputStream != null) {
        if (inputStream.CanSeek) inputStream.Position = 0;

        using MemoryStream copy = new MemoryStream();
        inputStream.CopyTo(copy);
        Data = copy.ToArray();
      } else {
        Data = Array.Empty<Byte>();
      }
    }

    private static Boolean StartsWith(Byte[] data, params Byte[] magic) {
      if (data == null || magic == null || data.Length < magic.Length) return false;
      for (Int32 i = 0; i < magic.Length; i++) if (data[i] != magic[i]) return false;
      return true;
    }

    private static async Task<String> RunAudioTool(String toolPath, String workingDirectory, params String[] arguments) {
      if (!File.Exists(toolPath))
        throw new FileNotFoundException("Required audio conversion tool was not found.", toolPath);

      ProcessStartInfo info = new ProcessStartInfo {
        CreateNoWindow = true,
        FileName = toolPath,
        WorkingDirectory = workingDirectory,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        WindowStyle = ProcessWindowStyle.Hidden
      };
      foreach (String argument in arguments) info.ArgumentList.Add(argument);

      using Process process = Process.Start(info);
      if (process == null)
        throw new InvalidOperationException("Could not start " + Path.GetFileName(toolPath) + ".");

      Task<String> stdOutTask = process.StandardOutput.ReadToEndAsync();
      Task<String> stdErrTask = process.StandardError.ReadToEndAsync();
      await process.WaitForExitAsync();
      String stdOut = await stdOutTask;
      String stdErr = await stdErrTask;

      if (process.ExitCode != 0) {
        String detail = String.Join(Environment.NewLine, new[] { stdOut, stdErr }).Trim();
        throw new InvalidOperationException(
          Path.GetFileName(toolPath) + " failed with exit code " + process.ExitCode +
          (String.IsNullOrWhiteSpace(detail) ? String.Empty : ": " + detail));
      }

      return String.Join(Environment.NewLine, new[] { stdOut, stdErr }).Trim();
    }

    private IEnumerable<String> CodebookCandidates(String toolsDirectory) {
      String beta = Path.Combine(toolsDirectory, "packed_codebooks.bin");
      String betaJedipediaName = Path.Combine(toolsDirectory, "packed_codebooks-beta.bin");
      String current = Path.Combine(toolsDirectory, "packed_codebooks_aoTuV_603.bin");

      if (IsBeta) {
        // Legacy RED/BLUE banks use the original Wwise codebook set. Do not fall back to
        // aoTuV_603: that set belongs to the later BNK2 generation and can decode the wrong
        // packet tables even when ww2ogg happens not to reject the file immediately.
        yield return beta;
        yield return betaJedipediaName;
      } else {
        yield return current;
      }
    }

    internal async Task<Boolean> ConvertWEM() {
      String wemPath = null;
      String oggPath = null;

      try {
        LastError = null;
        if (Data == null || Data.Length == 0)
          throw new InvalidDataException("WEM file is empty.");

        // A few archive generations contain genuine Ogg/Vorbis streams. Do not send an already
        // valid Ogg through ww2ogg just because the archive/hash name happens to say WEM.
        if (StartsWith(Data, 0x4F, 0x67, 0x67, 0x53)) { // OggS
          Vorbis?.Dispose();
          Vorbis = new VorbisWaveReader(new MemoryStream(Data, writable: false), true);
          return true;
        }

        // SWTOR's streamed beta *.ogg files are RIFF/WAVE Wwise payloads, exactly like *.wem.
        if (!StartsWith(Data, 0x52, 0x49, 0x46, 0x46)) // RIFF
          throw new InvalidDataException("Audio payload is neither Ogg/Vorbis nor RIFF/Wwise WEM.");

        String appDirectory = AppContext.BaseDirectory;
        String toolsDirectory = Path.Combine(appDirectory, "Tools");
        String ww2oggPath = Path.Combine(toolsDirectory, "ww2ogg.exe");
        String revorbPath = Path.Combine(toolsDirectory, "revorb.exe");

        List<String> codebooks = CodebookCandidates(toolsDirectory)
          .Where(File.Exists)
          .Distinct(StringComparer.OrdinalIgnoreCase)
          .ToList();
        if (codebooks.Count == 0)
          throw new FileNotFoundException(IsBeta
            ? "Beta Wwise packed codebooks were not found. Expected packed_codebooks.bin in Tools."
            : "Current Wwise packed codebooks were not found. Expected packed_codebooks_aoTuV_603.bin in Tools.");

        String tempDirectory = Path.Combine(Path.GetTempPath(), "PugTools", "Audio");
        Directory.CreateDirectory(tempDirectory);
        String tempBase = Guid.NewGuid().ToString("N");
        wemPath = Path.Combine(tempDirectory, tempBase + ".wem");
        oggPath = Path.Combine(tempDirectory, tempBase + ".ogg");
        await File.WriteAllBytesAsync(wemPath, Data);

        List<String> failures = new List<String>();
        Boolean converted = false;
        foreach (String codebooksPath in codebooks) {
          try {
            try { if (File.Exists(oggPath)) File.Delete(oggPath); } catch { }
            await RunAudioTool(ww2oggPath, toolsDirectory,
              wemPath, "-o", oggPath, "--pcb", codebooksPath);
            if (!File.Exists(oggPath) || new FileInfo(oggPath).Length == 0)
              throw new InvalidDataException("ww2ogg did not produce an OGG file.");
            converted = true;
            break;
          }
          catch (Exception ex) {
            failures.Add(Path.GetFileName(codebooksPath) + ": " + ex.Message);
          }
        }

        if (!converted)
          throw new InvalidOperationException("Wwise Vorbis conversion failed with all available codebooks: " + String.Join(" | ", failures));

        await RunAudioTool(revorbPath, toolsDirectory, oggPath);

        Byte[] oggData = await File.ReadAllBytesAsync(oggPath);
        Vorbis?.Dispose();
        Vorbis = new VorbisWaveReader(new MemoryStream(oggData, writable: false), true);
        return true;
      }
      catch (Exception ex) {
        LastError = ex.Message;
        Debug.WriteLine("WEM conversion failed: " + ex);
        Vorbis?.Dispose();
        Vorbis = null;
        return false;
      }
      finally {
        try { if (!String.IsNullOrWhiteSpace(wemPath) && File.Exists(wemPath)) File.Delete(wemPath); } catch { }
        try { if (!String.IsNullOrWhiteSpace(oggPath) && File.Exists(oggPath)) File.Delete(oggPath); } catch { }
      }
    }
  }
}
