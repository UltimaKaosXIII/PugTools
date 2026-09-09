using System;
using System.Collections.Generic;
using FileFormats;
using SlimDX;
using SlimDX.Direct3D11;
using SlimDXNet.Vertex;

namespace PugTools {
  internal sealed partial class View_AREA {
    // Jedipedia's web viewer can hide pre-classified roof regions using a generated roofs.json sidecar. PugTools
    // deliberately stays data-file independent here: any selected static placement can be hidden temporarily, which
    // combines with the Y-slice to inspect interiors without requiring a baked roof database.
    private readonly HashSet<AssetInstance> manuallyHiddenWorldEntries = new HashSet<AssetInstance>();

    private bool IsWorldEntryManuallyHidden(RenderEntry entry) {
      return entry?.Instance != null && manuallyHiddenWorldEntries.Contains(entry.Instance);
    }

    public int HiddenWorldObjectCount => manuallyHiddenWorldEntries.Count;
    public bool CanHideSelectedWorldObject => selectedWorldRenderEntry != null;

    public string HideSelectedWorldObject() {
      RenderEntry entry = selectedWorldRenderEntry;
      if (entry?.Instance == null) return String.Empty;
      string summary = selectedWorldModelSummary ?? String.Empty;
      manuallyHiddenWorldEntries.Add(entry.Instance);
      ClearWorldModelSelection();
      InvalidateTemporalHistory();
      InvalidateObjectOcclusionVisibility();
      return summary;
    }

    public int RestoreHiddenWorldObjects() {
      int count = manuallyHiddenWorldEntries.Count;
      if (count == 0) return 0;
      manuallyHiddenWorldEntries.Clear();
      InvalidateTemporalHistory();
      InvalidateObjectOcclusionVisibility();
      return count;
    }


    private void DrawWorldStreamingDebugBounds(Matrix viewProj, WorldRenderSettings settings) {
      if (settings == null || !settings.ShowStreamingDebugBounds || settings.Mode == WorldRenderMode.Map || settings.Mode == WorldRenderMode.Heightmap) return;
      if (roomStreamingBounds.Count == 0) return;
      EnsureSelectedWorldBoxBuffer();
      if (selectedWorldBoxBuffer == null) return;

      var roomsToDraw = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      roomsToDraw.UnionWith(activeStreamRoomNames);
      roomsToDraw.UnionWith(floorStreamRoomNames);
      if (initialStreamLoading) roomsToDraw.UnionWith(initialStreamRoomNames);
      if (currentCameraRoom != null) roomsToDraw.Add(currentCameraRoom.RoomName);
      if (roomsToDraw.Count == 0) return;

      fx.SetMaterial(null);
      fx.SetViewProj(viewProj);
      ImmediateContext.InputAssembler.InputLayout = inputLayout;
      ImmediateContext.InputAssembler.PrimitiveTopology = PrimitiveTopology.LineList;
      ImmediateContext.InputAssembler.SetVertexBuffers(0, new VertexBufferBinding(selectedWorldBoxBuffer, PosNormalTexTan.Stride, 0));

      int drawn = 0;
      foreach (string roomName in roomsToDraw) {
        if (drawn >= 128) break;
        if (String.IsNullOrWhiteSpace(roomName)) continue;
        if (!roomStreamingBounds.TryGetValue(roomName, out RoomStreamingBounds bounds) || bounds == null || !IsFinite(bounds.Min) || !IsFinite(bounds.Max)) continue;
        Vector3 half = (bounds.Max - bounds.Min) * .5f;
        if (half.X <= .0001f || half.Y <= .0001f || half.Z <= .0001f) continue;
        Vector3 center = (bounds.Min + bounds.Max) * .5f;
        fx.SetWorld(Matrix.Scaling(half.X, half.Y, half.Z) * Matrix.Translation(center));
        bool active = activeStreamRoomNames.Contains(roomName);
        bool floor = floorStreamRoomNames.Contains(roomName);
        Vector4 color = active ? new Vector4(.15f, 1f, .38f, .78f)
          : floor ? new Vector4(.18f, .72f, 1f, .78f)
          : new Vector4(1f, .68f, .16f, .72f);
        if (bounds.Coarse) color.W *= .55f;
        fx.SetOverlay(color);
        fx.Overlay.GetPassByIndex(0).Apply(ImmediateContext);
        ImmediateContext.Draw(24, 0);
        drawn++;
      }
      ImmediateContext.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
    }

    public float VerticalSliceHeightForFraction(float fraction) {
      float f = Math.Max(0f, Math.Min(1f, fraction));
      float min = boundsMin.Y;
      float max = boundsMax.Y;
      if (Single.IsNaN(min) || Single.IsInfinity(min) || Single.IsNaN(max) || Single.IsInfinity(max) || max <= min) return 0f;
      return min + (max - min) * f;
    }
  }
}
