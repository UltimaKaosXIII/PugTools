using System;
using System.Collections.Generic;
using System.Linq;

using TorArchive;

namespace PugTools {
  internal enum BuildFileState {
    None,
    New,
    Changed,
    Removed,
    Unchanged
  }

  internal sealed class BuildFileRecord {
    internal UInt64 Identity { get; set; }
    internal Int32 ArchiveIndex { get; set; }
    internal Library Library { get; set; }
    internal HashFileInfo HashInfo { get; set; }

    internal String DisplayPath {
      get {
        if (HashInfo == null) return String.Empty;

        if (HashInfo.IsNamed)
          return HashInfo.Directory + "/" + HashInfo.FileName;

        return HashInfo.Directory + "/" + HashInfo.Extension + "/"
          + HashInfo.FileName + "." + HashInfo.Extension;
      }
    }
  }

  internal sealed class BuildFileDifference {
    internal BuildFileState State { get; set; }
    internal BuildFileRecord Current { get; set; }
    internal BuildFileRecord Previous { get; set; }

    internal BuildFileRecord DisplayRecord => Current ?? Previous;
  }

  internal static class BuildAssetComparer {
    internal static List<BuildFileDifference> Compare(Assets currentAssets,
                                                       Assets previousAssets,
                                                       Boolean includeUnchanged = false,
                                                       Func<Boolean> shouldCancel = null) {
      Dictionary<UInt64, BuildFileRecord> current = BuildSnapshot(currentAssets, shouldCancel);
      ThrowIfCancelled(shouldCancel);
      Dictionary<UInt64, BuildFileRecord> previous = BuildSnapshot(previousAssets, shouldCancel);
      // When Unchanged is requested the result is usually close to the complete build. Reserve
      // the current-build size up front to avoid repeatedly growing a million-entry list.
      List<BuildFileDifference> differences = includeUnchanged
        ? new List<BuildFileDifference>(current.Count)
        : new List<BuildFileDifference>();

      Int32 cancellationCounter = 0;
      foreach (KeyValuePair<UInt64, BuildFileRecord> pair in current) {
        if ((cancellationCounter++ & 1023) == 0) ThrowIfCancelled(shouldCancel);
        if (!previous.TryGetValue(pair.Key, out BuildFileRecord oldRecord)) {
          differences.Add(new BuildFileDifference {
            State = BuildFileState.New,
            Current = pair.Value
          });
          continue;
        }

        if (FileContentsDiffer(pair.Value.HashInfo.File.FileInfo,
                               oldRecord.HashInfo.File.FileInfo)) {
          differences.Add(new BuildFileDifference {
            State = BuildFileState.Changed,
            Current = pair.Value,
            Previous = oldRecord
          });
        } else if (includeUnchanged) {
          differences.Add(new BuildFileDifference {
            State = BuildFileState.Unchanged,
            Current = pair.Value,
            Previous = oldRecord
          });
        }
      }

      cancellationCounter = 0;
      foreach (KeyValuePair<UInt64, BuildFileRecord> pair in previous) {
        if ((cancellationCounter++ & 1023) == 0) ThrowIfCancelled(shouldCancel);
        if (!current.ContainsKey(pair.Key)) {
          differences.Add(new BuildFileDifference {
            State = BuildFileState.Removed,
            Previous = pair.Value
          });
        }
      }

      cancellationCounter = 0;
      foreach (BuildFileDifference difference in differences) {
        if ((cancellationCounter++ & 1023) == 0) ThrowIfCancelled(shouldCancel);
        // Only the version shown by the browser needs an extension guess. Hydrating both sides
        // doubles header reads for Changed/Unchanged unknown files without changing the UI.
        HydrateUnknownExtension(difference.DisplayRecord);
      }

      // PreparedTree sorts the browser tree later. Sorting every unchanged file here as well can
      // cost seconds and substantial temporary memory on a full SWTOR build, so keep the large
      // include-Unchanged result in snapshot order. Preserve the old deterministic ordering for
      // callers that ask for differences only.
      if (includeUnchanged) return differences;

      return differences
        .OrderBy(x => x.State)
        .ThenBy(x => x.DisplayRecord?.DisplayPath, StringComparer.OrdinalIgnoreCase)
        .ToList();
    }

    private static void ThrowIfCancelled(Func<Boolean> shouldCancel) {
      if (shouldCancel != null && shouldCancel()) throw new OperationCanceledException();
    }

    private static Dictionary<UInt64, BuildFileRecord> BuildSnapshot(Assets assets, Func<Boolean> shouldCancel) {
      Dictionary<UInt64, BuildFileRecord> snapshot =
        new Dictionary<UInt64, BuildFileRecord>();

      if (assets == null) return snapshot;

      foreach (Library lib in assets.Libraries) {
        ThrowIfCancelled(shouldCancel);
        if (!lib.Loaded) lib.Load();
        ThrowIfCancelled(shouldCancel);

        // A file can occur in several TORs inside one logical Library. Select the effective
        // physical copy for this library first, then merge it into the build-wide snapshot.
        // The build identity deliberately does NOT include the library name: BioWare can move
        // an unchanged path/hash between TOR/library groups from one patch to the next. Treating
        // "library:hash" as the identity made such moves look like one Removed + one New file.
        var libraryFiles = new Dictionary<UInt64, BuildFileRecord>();

        foreach (KeyValuePair<Int32, Archive> archive in lib.Archives) {
          ThrowIfCancelled(shouldCancel);
          Int32 fileCounter = 0;
          foreach (TorArchive.File file in archive.Value.EnumerateFiles()) {
            if ((fileCounter++ & 2047) == 0) ThrowIfCancelled(shouldCancel);
            UInt32 ph = file.FileInfo.PrimaryHash;
            UInt32 sh = file.FileInfo.SecondaryHash;
            UInt64 identity = ((UInt64)ph << 32) | sh;

            // The same file hash can exist in more than one TOR in a library.
            // The highest archive number is the newest effective copy.
            if (libraryFiles.TryGetValue(identity, out BuildFileRecord existing)
                && existing.ArchiveIndex >= archive.Key) {
              continue;
            }

            libraryFiles[identity] = new BuildFileRecord {
              Identity = identity,
              ArchiveIndex = archive.Key,
              Library = lib,
              HashInfo = new HashFileInfo(ph, sh, file, false, false)
            };
          }
        }
        // For named files, ask this Library's metadata table for the effective archive copy.
        // This is more precise than assuming the numerically highest TOR always wins.
        Int32 recordCounter = 0;
        foreach (BuildFileRecord record in libraryFiles.Values) {
          if ((recordCounter++ & 2047) == 0) ThrowIfCancelled(shouldCancel);
          if (record.HashInfo != null && record.HashInfo.IsNamed) {
            String path = record.HashInfo.Directory + "/" + record.HashInfo.FileName;
            TorArchive.File effective = record.Library?.FindFile(path);
            if (effective != null && !Object.ReferenceEquals(effective, record.HashInfo.File)) {
              record.HashInfo = new HashFileInfo(
                effective.FileInfo.PrimaryHash, effective.FileInfo.SecondaryHash,
                effective, false, false
              );
            }
          }

          if (IsIgnored(record.HashInfo)) continue;

          // Assets.FindFile() also walks Libraries in order and returns the first match. Mirroring
          // that precedence here prevents duplicate physical copies from being counted twice.
          if (!snapshot.ContainsKey(record.Identity)) snapshot.Add(record.Identity, record);
        }
      }

      return snapshot;
    }

    private static void HydrateUnknownExtension(BuildFileRecord record) {
      if (record?.HashInfo == null || record.HashInfo.IsNamed
          || !String.IsNullOrEmpty(record.HashInfo.Extension)) return;

      TorArchive.File file = record.HashInfo.File;
      record.HashInfo = new HashFileInfo(
        file.FileInfo.PrimaryHash, file.FileInfo.SecondaryHash, file, true, false
      );
    }

    private static Boolean IsIgnored(HashFileInfo info) {
      if (info == null) return true;

      if (!info.IsNamed) return false;

      return info.FileName.Equals("metadata.bin", StringComparison.OrdinalIgnoreCase)
        || info.FileName.Equals("ft.sig", StringComparison.OrdinalIgnoreCase)
        || info.FileName.Equals("groupmanifest.bin", StringComparison.OrdinalIgnoreCase);
    }

    private static Boolean FileContentsDiffer(TorArchive.FileInfo current,
                                               TorArchive.FileInfo previous) {
      if (current == null || previous == null) return true;

      // Checksum is SWTOR's CRC32 of the file data. Size is included as a
      // sanity check for very old archive variants with incomplete metadata.
      return current.Checksum != previous.Checksum
        || current.UncompressedSize != previous.UncompressedSize;
    }
  }
}
