using System;
using System.Collections.Generic;
using System.Linq;
using SlimDX;

namespace FileFormats {
  // Snapshot of Jedipedia's generated viewer/hooks.js registry. The SWTOR room placement property
  // aptPlacementLayoutLayoutId (0xD576D2CF) points at one of these layouts. Each layout expands
  // into the colored GR2 decoration-hook field models the game/World Viewer uses in Strongholds.
  public sealed class StrongholdDecorationHookPart {
    public string ModelPath { get; }
    public Vector3 Offset { get; }
    public StrongholdDecorationHookPart(string modelPath, Vector3 offset) { ModelPath = modelPath; Offset = offset; }
  }

  public static class StrongholdDecorationHooks {
    private static StrongholdDecorationHookPart Part(string path, float x, float y, float z) => new StrongholdDecorationHookPart(path, new Vector3(x, y, z));
    public static readonly IReadOnlyDictionary<uint, StrongholdDecorationHookPart[]> Layouts =
      new Dictionary<uint, StrongholdDecorationHookPart[]> {
        [479u] = new[] {
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_wall_small_01.gr2", 0.0f, 0.0f, 0.0f),
        },
        [481u] = new[] {
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_med_01.gr2", 0.0f, 0.0f, 0.0f),
        },
        [483u] = new[] {
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_wall_large_01.gr2", 0.0f, 0.0f, 0.0f),
        },
        [576u] = new[] {
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_small_01.gr2", 0.0f, 0.0f, 0.0f),
        },
        [578u] = new[] {
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_med_01.gr2", 0.0f, 0.0f, 0.0f),
        },
        [580u] = new[] {
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_large_01.gr2", 0.0f, 0.0f, 0.0f),
        },
        [584u] = new[] {
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_ceiling_large_01.gr2", 0.0f, 0.0f, 0.0f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_wall_small_01.gr2", -0.3f, 0.0f, 0.3f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_wall_small_01.gr2", -0.3f, 0.0f, 0.0f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_wall_small_01.gr2", -0.3f, 0.0f, -0.3f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_wall_small_01.gr2", 0.0f, 0.0f, -0.3f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_wall_small_01.gr2", 0.3f, 0.0f, -0.3f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_wall_small_01.gr2", 0.3f, 0.0f, 0.0f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_wall_small_01.gr2", 0.3f, 0.0f, 0.3f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_wall_small_01.gr2", 0.0f, 0.0f, 0.3f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_wall_small_01.gr2", 0.0f, 0.0f, 0.0f),
        },
        [586u] = new[] {
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_med_01.gr2", 0.0f, 0.0f, 0.0f),
        },
        [598u] = new[] {
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_wall_large_01.gr2", 0.0f, 0.0f, 0.0f),
        },
        [631u] = new[] {
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_large_01.gr2", 0.0f, 0.0f, 0.0f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_small_01.gr2", 0.3f, 0.0f, 0.3f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_small_01.gr2", -0.3f, 0.0f, 0.3f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_small_01.gr2", 0.0f, 0.0f, 0.3f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_small_01.gr2", -0.3f, 0.0f, 0.0f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_small_01.gr2", 0.3f, 0.0f, 0.0f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_small_01.gr2", 0.3f, 0.0f, -0.3f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_small_01.gr2", -0.3f, 0.0f, -0.3f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_small_01.gr2", 0.0f, 0.0f, -0.3f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_small_01.gr2", 0.0f, 0.0f, 0.0f),
        },
        [663u] = new[] {
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_ceiling_large_01.gr2", 0.0f, 0.0f, 0.0f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_wall_small_01.gr2", -0.3f, 0.0f, 0.3f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_wall_small_01.gr2", -0.3f, 0.0f, 0.0f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_wall_small_01.gr2", -0.3f, 0.0f, -0.3f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_wall_small_01.gr2", 0.0f, 0.0f, -0.3f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_wall_small_01.gr2", 0.3f, 0.0f, -0.3f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_wall_small_01.gr2", 0.3f, 0.0f, 0.0f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_wall_small_01.gr2", 0.3f, 0.0f, 0.3f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_wall_small_01.gr2", 0.0f, 0.0f, 0.3f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_wall_small_01.gr2", 0.0f, 0.0f, 0.0f),
        },
        [1148u] = new[] {
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_wall_large_01.gr2", -0.2f, 0.0f, 0.0f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_wall_large_01.gr2", 0.2f, 0.0f, 0.0f),
        },
        [1203u] = new[] {
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_med_01.gr2", 0.0f, 0.0f, 0.0f),
        },
        [1402u] = new[] {
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_med_narrow_01.gr2", 0.0f, 0.0f, 0.0f),
        },
        [1487u] = new[] {
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_med_01.gr2", 0.0f, 0.0f, 0.0f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_wall_small_01.gr2", -0.1f, 0.0f, -0.275f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_wall_small_01.gr2", 0.1f, 0.0f, -0.275f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_wall_small_01.gr2", 0.1f, 0.0f, 0.275f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_wall_small_01.gr2", -0.1f, 0.0f, 0.275f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_wall_small_01.gr2", 0.275f, 0.0f, -0.1f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_wall_small_01.gr2", 0.275f, 0.0f, 0.1f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_wall_small_01.gr2", -0.275f, 0.0f, -0.1f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_wall_small_01.gr2", -0.275f, 0.0f, 0.1f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_wall_small_01.gr2", 0.275f, 0.0f, -0.275f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_wall_small_01.gr2", 0.275f, 0.0f, 0.275f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_wall_small_01.gr2", -0.275f, 0.0f, -0.275f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_wall_small_01.gr2", -0.275f, 0.0f, 0.275f),
        },
        [1495u] = new[] {
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_large_01.gr2", 0.0f, 0.0f, 0.0f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_med_narrow_01.gr2", -0.6f, 0.0f, -0.3f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_med_narrow_01.gr2", 0.6f, 0.0f, -0.3f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_med_narrow_01.gr2", -0.6f, 0.0f, 0.3f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_med_narrow_01.gr2", 0.6f, 0.0f, 0.3f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_small_01.gr2", 0.7f, 0.0f, -0.1f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_small_01.gr2", 0.5f, 0.0f, -0.1f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_small_01.gr2", -0.7f, 0.0f, -0.1f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_small_01.gr2", -0.5f, 0.0f, -0.1f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_small_01.gr2", -0.5f, 0.0f, 0.1f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_small_01.gr2", -0.7f, 0.0f, 0.1f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_small_01.gr2", 0.7f, 0.0f, 0.1f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_small_01.gr2", 0.5f, 0.0f, 0.1f),
        },
        [1776u] = new[] {
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_small_01.gr2", -0.1f, 0.0f, 0.0f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_small_01.gr2", 0.1f, 0.0f, 0.0f),
        },
        [1809u] = new[] {
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_cover_01.gr2", 0.0f, 0.0f, 0.0f),
        },
        [1815u] = new[] {
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_large_centerpiece_01.gr2", 0.0f, 0.0f, 0.0f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_med_01.gr2", 0.5f, 0.0f, 0.0f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_med_01.gr2", -0.5f, 0.0f, 0.0f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_med_01.gr2", 0.0f, 0.0f, -0.5f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_med_01.gr2", 0.0f, 0.0f, 0.5f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_small_01.gr2", 0.2f, 0.0f, 0.0f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_small_01.gr2", -0.2f, 0.0f, 0.0f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_small_01.gr2", 0.0f, 0.0f, -0.2f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_small_01.gr2", 0.0f, 0.0f, 0.2f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_small_01.gr2", 0.5f, 0.0f, -0.5f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_small_01.gr2", -0.5f, 0.0f, 0.5f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_small_01.gr2", 0.5f, 0.0f, 0.5f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_small_01.gr2", -0.5f, 0.0f, -0.5f),
        },
        [1852u] = new[] {
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_skyscraper_01.gr2", 0.0f, 0.0f, 0.0f),
        },
        [1856u] = new[] {
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_starship_01.gr2", 0.0f, 0.0f, 0.0f),
        },
        [1890u] = new[] {
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_large_01.gr2", 0.0f, 0.0f, 0.0f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_med_narrow_01.gr2", 0.0f, 0.0f, -0.5f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_med_narrow_01.gr2", 0.5f, 0.0f, 0.0f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_med_narrow_01.gr2", 0.0f, 0.0f, 0.5f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_med_narrow_01.gr2", -0.5f, 0.0f, 0.0f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_small_01.gr2", -0.3f, 0.0f, -0.3f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_small_01.gr2", 0.0f, 0.0f, -0.3f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_small_01.gr2", 0.3f, 0.0f, -0.3f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_small_01.gr2", 0.3f, 0.0f, 0.3f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_small_01.gr2", 0.0f, 0.0f, 0.3f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_small_01.gr2", -0.3f, 0.0f, 0.3f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_small_01.gr2", 0.3f, 0.0f, 0.0f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_small_01.gr2", -0.3f, 0.0f, 0.0f),
        },
        [1992u] = new[] {
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_large_01.gr2", 0.0f, 0.0f, 0.0f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_med_01.gr2", -0.6f, 0.0f, 0.2f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_med_01.gr2", 0.6f, 0.0f, 0.2f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_small_01.gr2", 0.6f, 0.0f, -0.15f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_small_01.gr2", -0.6f, 0.0f, -0.15f),
        },
        [2227u] = new[] {
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_starship_01.gr2", 0.0f, 0.0f, 0.0f),
        },
        [2361u] = new[] {
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_large_01.gr2", 0.4f, 0.0f, -0.4f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_large_01.gr2", -0.4f, 0.0f, 0.4f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_large_01.gr2", -0.4f, 0.0f, -0.4f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_large_01.gr2", 0.4f, 0.0f, 0.4f),
        },
        [2417u] = new[] {
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_arena_01.gr2", 0.0f, 0.0f, 0.0f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_large_centerpiece_01.gr2", 0.0f, 0.0f, -2.0f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_med_narrow_01.gr2", 0.0f, 0.0f, -3.3f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_med_narrow_01.gr2", 0.0f, 0.0f, 3.3f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_large_01.gr2", 4.2f, 0.0f, -3.0f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_large_01.gr2", 4.2f, 0.0f, 0.0f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_large_01.gr2", 4.2f, 0.0f, 3.0f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_large_01.gr2", -4.2f, 0.0f, -3.0f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_large_01.gr2", -4.2f, 0.0f, 0.0f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_large_01.gr2", -4.2f, 0.0f, 3.0f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_large_01.gr2", 4.2f, 0.0f, 4.8f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_large_01.gr2", -4.2f, 0.0f, 4.8f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_large_01.gr2", 0.0f, 0.0f, -4.8f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_large_01.gr2", 0.0f, 0.0f, 4.8f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_large_01.gr2", 4.2f, 0.0f, -4.8f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_large_01.gr2", -4.2f, 0.0f, -4.8f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_large_01.gr2", 6.0f, 0.0f, -4.8f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_large_01.gr2", -6.0f, 0.0f, -4.8f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_large_01.gr2", 6.0f, 0.0f, 4.8f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_large_01.gr2", -6.0f, 0.0f, 4.8f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_large_centerpiece_01.gr2", 0.0f, 0.0f, 2.0f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_med_01.gr2", 1.0f, 0.0f, 4.8f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_med_01.gr2", 2.0f, 0.0f, 4.8f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_med_01.gr2", 3.0f, 0.0f, 4.8f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_med_01.gr2", -1.0f, 0.0f, 4.8f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_med_01.gr2", -2.0f, 0.0f, 4.8f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_med_01.gr2", -3.0f, 0.0f, 4.8f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_med_01.gr2", 1.0f, 0.0f, -4.8f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_med_01.gr2", 2.0f, 0.0f, -4.8f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_med_01.gr2", 3.0f, 0.0f, -4.8f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_med_01.gr2", -1.0f, 0.0f, -4.8f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_med_01.gr2", -2.0f, 0.0f, -4.8f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_med_01.gr2", -3.0f, 0.0f, -4.8f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_large_01.gr2", 2.1f, 0.0f, -4.0f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_large_01.gr2", -2.1f, 0.0f, -4.0f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_large_01.gr2", 2.1f, 0.0f, 4.0f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_large_01.gr2", -2.1f, 0.0f, 4.0f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_large_01.gr2", -5.5f, 0.0f, -1.65f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_large_01.gr2", -5.5f, 0.0f, 1.65f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_large_01.gr2", 5.5f, 0.0f, -1.65f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_large_01.gr2", 5.5f, 0.0f, 1.65f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_large_01.gr2", 6.5f, 0.0f, -1.65f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_large_01.gr2", 6.5f, 0.0f, 1.65f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_large_01.gr2", -6.5f, 0.0f, -1.65f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_large_01.gr2", -6.5f, 0.0f, 1.65f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_med_narrow_01.gr2", 0.0f, 0.0f, 4.0f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_med_narrow_01.gr2", 0.0f, 0.0f, -4.0f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_med_narrow_01.gr2", 3.0f, 0.0f, 4.0f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_med_narrow_01.gr2", 3.0f, 0.0f, -4.0f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_med_narrow_01.gr2", -3.0f, 0.0f, -4.0f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_med_narrow_01.gr2", -3.0f, 0.0f, 4.0f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_large_01.gr2", -5.5f, 0.0f, 3.0f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_large_01.gr2", -5.5f, 0.0f, -3.0f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_large_01.gr2", 5.5f, 0.0f, -3.0f),
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_large_01.gr2", 5.5f, 0.0f, 3.0f),
        },
        [2815u] = new[] {
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_floor_med_01.gr2", 0.0f, 0.0f, 0.0f),
        },
        [2816u] = new[] {
          Part("/resources/art/static/area/str_stronghold/hooks/str_hook_wall_small_01.gr2", 0.0f, 0.0f, 0.0f),
        },
      };

    public static bool TryGetLayout(uint layoutId, out StrongholdDecorationHookPart[] parts) => Layouts.TryGetValue(layoutId, out parts);
    public static IEnumerable<string> AllModelPaths => Layouts.Values.SelectMany(parts => parts).Select(part => part.ModelPath).Distinct(StringComparer.OrdinalIgnoreCase);
  }
}
