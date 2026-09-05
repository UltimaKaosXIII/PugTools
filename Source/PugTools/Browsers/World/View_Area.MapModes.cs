using System;
using System.Collections.Generic;
using System.Linq;
using FileFormats;
using SlimDX;

namespace PugTools {
  internal sealed partial class View_AREA {
    // Interactive M-map scope/source.  The scope is deliberately separate from mapShowEntireArea: the latter is the
    // old generated-world crop choice, while this one mirrors SWTOR's map hierarchy (innermost area page vs root).
    // Every time M opens we re-evaluate the area page under the actual 3D camera and default to it.  The source choice
    // is a session preference and survives closing/reopening M.
    private AreaMapPage interactiveMapAreaPage;
    private AreaMapPage interactiveMapWorldPage;
    private bool interactiveMapWorldScope = true;
    private bool interactiveMapPreferOriginalArt;

    private AreaMapPage miniMapAreaPage;
    private AreaMapPage miniMapWorldPage;
    private bool miniMapWorldScope = true;
    private bool miniMapPreferOriginalArt;
    private bool miniMapCaptureUseAutoScope = true;
    private bool miniMapCaptureForceFullExtent;
    private long miniMapTrackedCameraPageGuid = Int64.MinValue;
    private long miniMapLinkedMapNameSId;
    private long miniMapLinkedSubmapNameSId;
    private bool miniMapLinkedPageActive;

    public bool MiniMapIsWorldScope => miniMapWorldScope || miniMapAreaPage == null;
    public bool MiniMapHasAreaScope => miniMapAreaPage != null;
    public bool MiniMapPrefersOriginalArt => miniMapPreferOriginalArt;
    public bool MiniMapOriginalArtAvailable => MiniMapSelectedPage?.HasImage == true;
    public bool MiniMapOriginalArtActive => miniMapPreferOriginalArt && MiniMapOriginalArtAvailable;
    public string MiniMapAreaName => miniMapAreaPage?.MapName ?? String.Empty;
    public string MiniMapWorldName => miniMapWorldPage?.MapName ?? String.Empty;
    private AreaMapPage MiniMapSelectedPage => MiniMapIsWorldScope ? miniMapWorldPage : miniMapAreaPage;

    public bool InteractiveMapIsWorldScope => interactiveMapWorldScope || interactiveMapAreaPage == null;
    public bool InteractiveMapHasAreaScope => interactiveMapAreaPage != null;
    public bool InteractiveMapPrefersOriginalArt => interactiveMapPreferOriginalArt;
    public bool InteractiveMapOriginalArtAvailable => InteractiveMapSelectedPage?.HasImage == true;
    public bool InteractiveMapOriginalArtActive => mapOpen && interactiveMapPreferOriginalArt && InteractiveMapOriginalArtAvailable;
    public string InteractiveMapAreaName => interactiveMapAreaPage?.MapName ?? String.Empty;
    public string InteractiveMapWorldName => interactiveMapWorldPage?.MapName ?? String.Empty;
    public bool InteractiveMapTravelMode => IsTaxiRouteMapActive || IsQuickTravelMapActive;

    private AreaMapPage InteractiveMapSelectedPage => InteractiveMapIsWorldScope ? interactiveMapWorldPage : interactiveMapAreaPage;

    private static bool InteractiveMapPageHasBounds(AreaMapPage page) {
      if (page == null) return false;
      float minX = Math.Min(page.Min.X, page.Max.X), maxX = Math.Max(page.Min.X, page.Max.X);
      float minZ = Math.Min(page.Min.Z, page.Max.Z), maxZ = Math.Max(page.Min.Z, page.Max.Z);
      return Single.IsFinite(minX) && Single.IsFinite(maxX) && Single.IsFinite(minZ) && Single.IsFinite(maxZ) &&
        maxX - minX > .001f && maxZ - minZ > .001f;
    }

    private static float InteractiveMapPageFootprint(AreaMapPage page) {
      if (!InteractiveMapPageHasBounds(page)) return 0f;
      return Math.Abs(page.Max.X - page.Min.X) * Math.Abs(page.Max.Z - page.Min.Z);
    }

    private static bool InteractiveMapPageContainsXZ(AreaMapPage page, Vector3 position) {
      if (!InteractiveMapPageHasBounds(page) || !IsFinite(position)) return false;
      float minX = Math.Min(page.Min.X, page.Max.X), maxX = Math.Max(page.Min.X, page.Max.X);
      float minZ = Math.Min(page.Min.Z, page.Max.Z), maxZ = Math.Max(page.Min.Z, page.Max.Z);
      const float epsilon = .02f;
      return position.X >= minX - epsilon && position.X <= maxX + epsilon && position.Z >= minZ - epsilon && position.Z <= maxZ + epsilon;
    }

    // SWTOR map pages carry the same three-dimensional mapPageMinCoord/mapPageMaxCoord volumes that the client uses
    // to distinguish stacked floors.  Treat a non-degenerate Y range as authoritative; pages with no usable vertical
    // extent remain valid legacy fallbacks.  The small margin is only eye-height tolerance -- it is deliberately not
    // large enough to make a tunnel page win while the camera is on the floor above it.
    private static bool InteractiveMapPageHasHeightBounds(AreaMapPage page) {
      if (page == null || !Single.IsFinite(page.Min.Y) || !Single.IsFinite(page.Max.Y)) return false;
      return Math.Abs(page.Max.Y - page.Min.Y) > .01f;
    }

    private static bool InteractiveMapPageContainsHeight(AreaMapPage page, float y) {
      if (!InteractiveMapPageHasHeightBounds(page)) return true;
      float minY = Math.Min(page.Min.Y, page.Max.Y), maxY = Math.Max(page.Min.Y, page.Max.Y);
      const float eyeHeightMargin = .35f;
      return y >= minY - eyeHeightMargin && y <= maxY + eyeHeightMargin;
    }

    private AreaMapPage ResolveInteractiveWorldPage() {
      List<AreaMapPage> pages = area?.MapPages?.Where(InteractiveMapPageHasBounds).ToList();
      if (pages == null || pages.Count == 0) return null;
      // Same choice as Jedipedia's rootMapPage(): parentless root, largest first.  If malformed client data has no
      // parentless page, the largest page is still the safest world overview.
      List<AreaMapPage> roots = pages.Where(x => x.ParentId == 0).ToList();
      AreaMapPage root = roots.Where(x => x.HasImage).OrderByDescending(InteractiveMapPageFootprint).FirstOrDefault()
        ?? roots.OrderByDescending(InteractiveMapPageFootprint).FirstOrDefault();
      return root ?? pages.Where(x => x.HasImage).OrderByDescending(InteractiveMapPageFootprint).FirstOrDefault()
        ?? pages.OrderByDescending(InteractiveMapPageFootprint).FirstOrDefault();
    }

    private AreaMapPage ResolveInteractiveAreaPage(Vector3 position, AreaMapPage worldPage) {
      List<AreaMapPage> pages = area?.MapPages?.Where(x => InteractiveMapPageHasBounds(x) && InteractiveMapPageContainsXZ(x, position)).ToList();
      if (pages == null || pages.Count == 0) return null;
      float worldFootprint = InteractiveMapPageFootprint(worldPage);
      pages = pages.Where(x => !ReferenceEquals(x, worldPage) &&
          (x.ParentId != 0 || worldFootprint <= 0f || InteractiveMapPageFootprint(x) < worldFootprint * .98f)).ToList();
      if (pages.Count == 0) return null;

      // Prefer authored 3-D page volumes that actually contain the current camera height.  Crucially, if the only
      // X/Z-overlapping child page is a bounded tunnel/other floor and the camera is outside its Y range, do NOT pick
      // the nearest floor -- fall back to the world map unless an unbounded legacy child exists.
      List<AreaMapPage> verticalMatches = pages.Where(x => InteractiveMapPageHasHeightBounds(x) && InteractiveMapPageContainsHeight(x, position.Y)).ToList();
      List<AreaMapPage> unbounded = pages.Where(x => !InteractiveMapPageHasHeightBounds(x)).ToList();
      List<AreaMapPage> candidates = verticalMatches.Count > 0 ? verticalMatches : unbounded;
      if (candidates.Count == 0) return null;

      // Nested MAP pages are normal in SWTOR. The smallest matching page is the page the client shows.
      return candidates.OrderBy(InteractiveMapPageFootprint).FirstOrDefault();
    }


    private void ResolveMiniMapPages(bool autoSelectScope) {
      miniMapWorldPage = ResolveInteractiveWorldPage();
      miniMapAreaPage = ResolveInteractiveAreaPage(camera.Position, miniMapWorldPage);
      if (autoSelectScope) miniMapWorldScope = miniMapAreaPage == null;
      if (autoSelectScope) miniMapTrackedCameraPageGuid = miniMapAreaPage?.Guid ?? 0L;
      else if (!miniMapWorldScope && miniMapAreaPage == null) miniMapWorldScope = true;
    }

    private void PrepareMiniMapCaptureScope() {
      if (miniMapCaptureUseAutoScope) {
        miniMapLinkedMapNameSId = 0; miniMapLinkedSubmapNameSId = 0; miniMapLinkedPageActive = false;
        ResolveMiniMapPages(true);
      } else if (miniMapLinkedPageActive) {
        AreaMapPage linked = ResolveLinkedMapPage(miniMapLinkedMapNameSId, miniMapLinkedSubmapNameSId);
        if (linked != null) {
          miniMapWorldPage = ResolveLinkedMapRoot(linked, miniMapLinkedMapNameSId) ?? ResolveInteractiveWorldPage();
          bool linkedWorld = ReferenceEquals(linked, miniMapWorldPage) || linked.ParentId == 0;
          miniMapAreaPage = linkedWorld ? null : linked;
          if (linkedWorld) miniMapWorldScope = true;
        } else ResolveMiniMapPages(false);
      } else ResolveMiniMapPages(false);
      miniMapCaptureUseAutoScope = true;
      ResetMapCamera();
      AreaMapPage selected = MiniMapSelectedPage;
      bool forceFullExtent = miniMapCaptureForceFullExtent;
      miniMapCaptureForceFullExtent = false;
      bool usePageBounds = !MiniMapIsWorldScope || (miniMapPreferOriginalArt && selected?.HasImage == true);
      if (usePageBounds && InteractiveMapPageHasBounds(selected)) SetInteractiveMapExtentFromPage(selected);
      else if (forceFullExtent && mapFullExtentMaxX > mapFullExtentMinX && mapFullExtentMaxZ > mapFullExtentMinZ) {
        mapExtentMinX = mapFullExtentMinX; mapExtentMaxX = mapFullExtentMaxX;
        mapExtentMinZ = mapFullExtentMinZ; mapExtentMaxZ = mapFullExtentMaxZ;
      } else ApplyMapExtentSelection();
      mapCenter = new Vector2((mapExtentMinX + mapExtentMaxX) * .5f, (mapExtentMinZ + mapExtentMaxZ) * .5f);
      mapZoom = 1f;
      UpdateMapCamera();
      if (Window is WorldBrowser browser) browser.RefreshMiniMapModeControls();
    }

    public bool MiniMapNeedsAutoScopeRefresh() {
      AreaMapPage world = ResolveInteractiveWorldPage();
      AreaMapPage current = ResolveInteractiveAreaPage(camera.Position, world);
      return (current?.Guid ?? 0L) != miniMapTrackedCameraPageGuid;
    }

    public void RequestMiniMapSnapshot(bool autoSelectScope) {
      RequestMiniMapSnapshot(autoSelectScope, false);
    }

    public void RequestMiniMapSnapshot(bool autoSelectScope, bool forceFullExtent) {
      miniMapCaptureUseAutoScope = autoSelectScope;
      miniMapCaptureForceFullExtent = forceFullExtent;
      miniMapCaptureRequested = true;
    }

    public void SetMiniMapWorldScope(bool world) {
      if (miniMapLinkedPageActive) {
        AreaMapPage linked = ResolveLinkedMapPage(miniMapLinkedMapNameSId, miniMapLinkedSubmapNameSId);
        if (linked != null) {
          miniMapWorldPage = ResolveLinkedMapRoot(linked, miniMapLinkedMapNameSId) ?? ResolveInteractiveWorldPage();
          miniMapAreaPage = ReferenceEquals(linked, miniMapWorldPage) || linked.ParentId == 0 ? null : linked;
        } else { miniMapLinkedMapNameSId = 0; miniMapLinkedSubmapNameSId = 0; miniMapLinkedPageActive = false; ResolveMiniMapPages(false); }
      } else ResolveMiniMapPages(false);
      if (!world && miniMapAreaPage == null) world = true;
      miniMapWorldScope = world;
      RequestMiniMapSnapshot(false);
      if (Window is WorldBrowser browser) browser.RefreshMiniMapModeControls();
    }

    public void SetMiniMapOriginalArt(bool original) {
      miniMapPreferOriginalArt = original;
      RequestMiniMapSnapshot(false);
      if (Window is WorldBrowser browser) browser.RefreshMiniMapModeControls();
    }

    // Map-link notes store the destination as the stable string id of the root map plus an optional submap id.
    // Resolve the submap first; when it is missing or unavailable, fall back to the named root page. This mirrors
    // the client's breadcrumb/map-exit behaviour while still tolerating older area data that only carries one id.
    private AreaMapPage ResolveLinkedMapPage(long mapNameSId, long submapNameSId) {
      List<AreaMapPage> pages = area?.MapPages?.Where(InteractiveMapPageHasBounds).ToList();
      if (pages == null || pages.Count == 0) return null;
      if (submapNameSId != 0) {
        AreaMapPage submap = pages.Where(x => x.SId == submapNameSId).OrderBy(InteractiveMapPageFootprint).FirstOrDefault();
        if (submap != null) return submap;
      }
      if (mapNameSId != 0) {
        AreaMapPage map = pages.Where(x => x.SId == mapNameSId)
          .OrderBy(x => x.ParentId == 0 ? 0 : 1).ThenByDescending(InteractiveMapPageFootprint).FirstOrDefault();
        if (map != null) return map;
      }
      return ResolveInteractiveWorldPage();
    }

    private AreaMapPage ResolveLinkedMapRoot(AreaMapPage target, long mapNameSId) {
      List<AreaMapPage> pages = area?.MapPages?.Where(InteractiveMapPageHasBounds).ToList();
      if (pages == null || pages.Count == 0) return null;
      if (mapNameSId != 0) {
        AreaMapPage named = pages.Where(x => x.SId == mapNameSId && x.ParentId == 0)
          .OrderByDescending(InteractiveMapPageFootprint).FirstOrDefault();
        if (named != null) return named;
      }
      AreaMapPage cursor = target;
      var seen = new HashSet<long>();
      while (cursor != null && cursor.ParentId != 0 && seen.Add(cursor.SId)) {
        AreaMapPage parent = pages.Where(x => x.SId == cursor.ParentId)
          .OrderByDescending(InteractiveMapPageFootprint).FirstOrDefault();
        if (parent == null) break;
        cursor = parent;
      }
      if (cursor?.ParentId == 0) return cursor;
      return ResolveInteractiveWorldPage();
    }

    public bool OpenMiniMapLink(long mapNameSId, long submapNameSId) {
      AreaMapPage target = ResolveLinkedMapPage(mapNameSId, submapNameSId);
      if (target == null) return false;
      miniMapLinkedMapNameSId = mapNameSId; miniMapLinkedSubmapNameSId = submapNameSId; miniMapLinkedPageActive = true;
      miniMapWorldPage = ResolveLinkedMapRoot(target, mapNameSId) ?? ResolveInteractiveWorldPage();
      bool targetIsWorld = ReferenceEquals(target, miniMapWorldPage) || target.ParentId == 0;
      miniMapAreaPage = targetIsWorld ? null : target;
      miniMapWorldScope = targetIsWorld;
      // Keep the linked page visible while the camera remains in the same physical page. As soon as the player moves
      // into another map volume, MiniMapNeedsAutoScopeRefresh() detects the new camera page and resumes auto-follow.
      AreaMapPage cameraWorld = ResolveInteractiveWorldPage();
      AreaMapPage cameraPage = ResolveInteractiveAreaPage(camera.Position, cameraWorld);
      miniMapTrackedCameraPageGuid = cameraPage?.Guid ?? 0L;
      RequestMiniMapSnapshot(false);
      if (Window is WorldBrowser browser) browser.RefreshMiniMapModeControls();
      return true;
    }

    public bool OpenInteractiveMapLink(long mapNameSId, long submapNameSId) {
      AreaMapPage target = ResolveLinkedMapPage(mapNameSId, submapNameSId);
      if (target == null) return false;
      if (!mapOpen) SetMapOpen(true);
      interactiveMapWorldPage = ResolveLinkedMapRoot(target, mapNameSId) ?? ResolveInteractiveWorldPage();
      bool targetIsWorld = ReferenceEquals(target, interactiveMapWorldPage) || target.ParentId == 0;
      interactiveMapAreaPage = targetIsWorld ? null : target;
      interactiveMapWorldScope = targetIsWorld;
      ApplyInteractiveMapScopeExtents(true);
      if (Window is WorldBrowser browser) {
        string name = target.MapName;
        browser.SetStatusLabel(String.IsNullOrWhiteSpace(name) ? InteractiveMapScopeStatus() : "Map link: " + name + " • drag = pan, wheel = zoom, M/Esc = close");
      }
      NotifyInteractiveMapModeChanged();
      return true;
    }

    private void SelectDefaultInteractiveMapScope() {
      interactiveMapWorldPage = ResolveInteractiveWorldPage();
      interactiveMapAreaPage = ResolveInteractiveAreaPage(camera.Position, interactiveMapWorldPage);
      interactiveMapWorldScope = interactiveMapAreaPage == null;
      ApplyInteractiveMapScopeExtents(true);
      NotifyInteractiveMapModeChanged();
    }

    private void SetInteractiveMapExtentFromPage(AreaMapPage page) {
      mapExtentMinX = Math.Min(page.Min.X, page.Max.X);
      mapExtentMaxX = Math.Max(page.Min.X, page.Max.X);
      mapExtentMinZ = Math.Min(page.Min.Z, page.Max.Z);
      mapExtentMaxZ = Math.Max(page.Min.Z, page.Max.Z);
    }

    private void ApplyInteractiveMapScopeExtents(bool resetView) {
      if (!mapOpen) return;
      AreaMapPage selected = InteractiveMapSelectedPage;
      bool usePageBounds = !InteractiveMapIsWorldScope || (interactiveMapPreferOriginalArt && selected?.HasImage == true);
      if (usePageBounds && InteractiveMapPageHasBounds(selected)) SetInteractiveMapExtentFromPage(selected);
      else ApplyMapExtentSelection();
      if (resetView) {
        mapCenter = new Vector2((mapExtentMinX + mapExtentMaxX) * .5f, (mapExtentMinZ + mapExtentMaxZ) * .5f);
        mapZoom = 1f;
      }
      UpdateMapCamera();
      InvalidateTemporalHistory();
    }

    public void SetInteractiveMapWorldScope(bool world) {
      if (!mapOpen) return;
      if (InteractiveMapTravelMode) world = true;
      if (!world && interactiveMapAreaPage == null) world = true;
      if (interactiveMapWorldScope == world) { NotifyInteractiveMapModeChanged(); return; }
      interactiveMapWorldScope = world;
      ApplyInteractiveMapScopeExtents(true);
      if (Window is WorldBrowser browser) browser.SetStatusLabel(InteractiveMapScopeStatus());
      NotifyInteractiveMapModeChanged();
    }

    public void SetInteractiveMapOriginalArt(bool original) {
      interactiveMapPreferOriginalArt = original;
      if (mapOpen && !InteractiveMapTravelMode) ApplyInteractiveMapScopeExtents(true);
      else if (mapOpen) { UpdateMapCamera(); InvalidateTemporalHistory(); }
      if (Window is WorldBrowser browser) {
        browser.SetStatusLabel(original && !InteractiveMapOriginalArtAvailable
          ? "No original SWTOR map art exists for this map page; showing the PugTools map."
          : InteractiveMapScopeStatus());
      }
      NotifyInteractiveMapModeChanged();
    }

    // Taxi/quick-travel maps span the area rather than the local submap.  Open them on the world hierarchy root,
    // then their existing Fit* method is free to zoom tighter around the actual destinations/routes.
    public void PrepareInteractiveTravelMapScope() {
      if (!mapOpen) return;
      interactiveMapWorldPage = ResolveInteractiveWorldPage();
      interactiveMapAreaPage = ResolveInteractiveAreaPage(camera.Position, interactiveMapWorldPage);
      interactiveMapWorldScope = true;
      ApplyInteractiveMapScopeExtents(true);
      NotifyInteractiveMapModeChanged();
    }

    // A click inside an area/submap must stay on that vertical level.  The old map sampler always chose the highest
    // height-map surface at X/Z, which teleported tunnel clicks onto the roof/ground above.  World scope deliberately
    // keeps the old highest-surface behaviour; area scope chooses the surface closest to the current camera and, when
    // the page has an authored Y volume, rejects floors outside that volume.
    private bool TrySampleInteractiveMapHeight(float worldX, float worldZ, out float worldY) {
      if (InteractiveMapIsWorldScope || interactiveMapAreaPage == null) return TrySampleMapHeight(worldX, worldZ, out worldY);
      worldY = 0f;
      if (!heightMapFloorGrid.TryGetValue((FloorCell(worldX), FloorCell(worldZ)), out List<HeightMapFloorEntry> entries)) return false;
      bool bounded = InteractiveMapPageHasHeightBounds(interactiveMapAreaPage);
      float minY = bounded ? Math.Min(interactiveMapAreaPage.Min.Y, interactiveMapAreaPage.Max.Y) - .35f : float.MinValue;
      float maxY = bounded ? Math.Max(interactiveMapAreaPage.Min.Y, interactiveMapAreaPage.Max.Y) + .35f : float.MaxValue;
      float referenceY = camera.Position.Y;
      bool found = false; float bestDistance = float.MaxValue;
      foreach (HeightMapFloorEntry entry in entries) {
        if (!TrySampleHeightMapEntry(entry, worldX, worldZ, out float y)) continue;
        if (bounded && (y < minY || y > maxY)) continue;
        float distance = Math.Abs(y - referenceY);
        if (!found || distance < bestDistance) { found = true; bestDistance = distance; worldY = y; }
      }
      return found;
    }

    private string InteractiveMapScopeStatus() {
      string scope = InteractiveMapIsWorldScope ? "World map" : "Area map" +
        (String.IsNullOrWhiteSpace(InteractiveMapAreaName) ? String.Empty : " — " + InteractiveMapAreaName);
      string source = InteractiveMapOriginalArtActive ? "original SWTOR art" : "PugTools render";
      return scope + " • " + source + " • click = teleport, drag = pan, wheel = zoom, M/Esc = close";
    }

    private void NotifyInteractiveMapModeChanged() {
      if (Window is WorldBrowser browser) browser.RefreshWorldMapModeControls();
    }
  }
}
