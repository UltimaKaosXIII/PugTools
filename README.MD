## 2026-09-10 v24 – hash-verified en-us / de-de / fr-fr Filename Finder

Filename Finder locale handling is now centralized at the final validation boundary. Any candidate whose immediate resource directory is `en-us`, `de-de` or `fr-fr` is expanded to all three SWTOR locales only after the format-specific and Jedipedia candidate heuristics have run. Each generated spelling is hashed independently and is persisted only when that exact PH+SH belongs to an unresolved file in the currently loaded build. Missing language siblings therefore cost only a hash probe and never enter the session dictionary/PFD1. This applies equally to built-in finders and imported candidate lists, and it does not generate locale variants for ordinary `/resources/world`, `/resources/art`, `/resources/gfx`, etc. paths.

The existing STB-manifest harvest now emits one canonical `en-us` seed instead of triplicating every entry up front; CNV STB/ACB/FXE and localized BNK names use the same central expansion automatically. Grammar `<<grammar::locpath>>` placeholders are also expanded only at validation time. Tutorial image harvesting no longer reads just `enMale`: it scans every distinct localized/gendered tutorial string so de-de/fr-fr-only image references can become candidates, while exact hash matching still rejects nonexistent paths.

## 2026-09-10 v23 – incomplete in-game world maps overlay the full PugTools map

The large interactive M-map and the small minimap no longer use an authored SWTOR **world-map** DDS rectangle as a crop boundary for the generated PugTools world. In world scope, PugTools first keeps the normal generated map extent (smart-cropped or full, depending on the existing setting) and then expands that extent only if the authored page reaches farther. The original `_r.dds` therefore remains aligned in world coordinates and is drawn opaquely on top where it exists, while terrain/rooms outside an incomplete in-game world map stay visible underneath.

Area/room/submap scope is intentionally unchanged: local authored pages still define the visible page extent exactly, matching SWTOR's floor/submap behaviour. World-scope original-art mode also keeps the normal PugTools map padding instead of tightening the whole viewport to the DDS, and minimap snapshot cropping/coordinate overlays use the same combined extent.

## 2026-09-10 v22 – Jedipedia-style classification for newly patched world areas

The World Browser now keeps Jedipedia's curated `area-ids.json` as the authoritative exact catalog, but can also place a newly patched ordinary `/resources/world/areas/<id>/` tree before Jedipedia has added that ID to the public list. For an uncatalogued area, PugTools builds a fingerprint from its non-generic room `.dat` filenames and compares it with the installed room fingerprints of the bundled Jedipedia catalog. A folder is inherited only when the best Jaccard similarity is at least 75%, at least 20 room names are shared, and the best match beats the runner-up by at least 10 percentage points; ambiguous/new layouts remain under **Unassigned**. Exact future Jedipedia entries always override an inferred result.

With the supplied 2026-09-10 PFD1, the only ordinary area IDs not present in Jedipedia's 281-entry catalog are `4611686320204664000` and `4611690227550610000`. Their filtered room layouts match Jedipedia's `odessen_enigma` area at 95.3% and 87.8% respectively, so both are placed under **Solo phases / Post-Zakuul story**. The tree tooltip records the reference area and match percentage. Existing untouched `name="" category="Unassigned" group=""` placeholders written by v21 are migrated to the inferred folder, while named or otherwise custom folder assignments remain user-owned.
The full v22 source package also carries the supplied current `hashes_filename.pfd1.gz`, replacing the older bundled dictionary from v21.1 so a clean build sees these newly named area folders immediately. The small v21.1 -> v22 source patch intentionally omits that large binary replacement; when using the patch, keep/copy the current PFD1 into `Source/PugTools/Hash/` (or the built application's `Hash` folder).

# PugTools

## 2026-09-10 v20 – native heightmap topology / LOD rendering

The World Browser now consumes the validated `0xF1234567 ... 0xABCDEFAB` heightmap render-mesh topology when it can prove the native batch table is safe to draw. The `4*K-1` doubled boundaries are interpreted only when they form triangle-aligned cumulative offsets (tested as index units first and byte units second), the native grid dimensions match the decoded heightmap, every native vertex maps back to an in-range heightmap x/z sample, and every four-batch LOD level covers the authored solid X/Z footprint exactly once. Any ambiguity, future format change, malformed boundary, out-of-grid vertex, degenerate triangle or coverage mismatch falls back automatically to the existing procedural grid LODs.

PugTools deliberately keeps its existing float terrain vertex layout, splat UVs and reconstructed normals/tangents: native render records are used only to recover SWTOR's triangle topology and LOD batches, so the still-unproven first native vertex float is not guessed. Native vertex references are remapped to the regular height grid, complete source levels are ordered densest-to-coarsest for near/far selection, and the instance's existing IBO is reused by the terrain renderer and shadow pass. The temporary native CPU vertex/index arrays are released immediately after conversion, avoiding a second persistent terrain mesh per tile. Native trailers with more than four usable levels retain the established 50/120/250 distance transitions and double the far threshold for each additional level.

The Asset Browser now also reports whether the raw batch table has a triangle-aligned index/byte interpretation and shows the derived source-level index ranges. This diagnostic is intentionally weaker than World Browser activation: runtime use additionally requires the grid/topology coverage checks above.

## 2026-09-10 v19 – native heightmap render-mesh inspection

The Asset Browser now decodes Jedipedia's validated `0xF1234567 ... 0xABCDEFAB` trailer on legacy dense version-2 heightmap `VertexData`. The parser finds the variable-prefix anchor using the same doubled LOD/batch-boundary table and exact end-of-buffer invariants, then retains the complete 16-byte native vertex records and `ushort` triangle index buffer. The structured view exposes the /64 fixed-point tile corner, declared grid size, LOD/batch level count, grouped raw boundary values, vertex/index/triangle counts, opaque-prefix size, grid-coordinate range, packed normal/tangent components and bounded vertex/triangle previews. The first float in each native vertex remains deliberately named `U` because its exact client semantic is not yet proven.

Sparse heightmaps now preserve and display their redundant embedded instance position, and the parsed view reports unexplained trailing bytes in the same spirit as Jedipedia. Native render-mesh decoding is opt-in for inspection only; the World Browser does not duplicate these buffers for every loaded terrain tile yet, avoiding a large memory regression until native LOD rendering actually consumes them. Malformed mesh trailers are non-fatal and appear as a parse diagnostic while the normal elevation/splat data remains usable.

## 2026-09-10 v18 – embedded world-DAT inspection and TOR diagnostics

The Asset Browser now follows Jedipedia's embedded room-DAT readers for the large world-node payloads that used to appear only as opaque `char[]`/binary values. `DepthTexture` (`.wtr`) is decoded as its versioned depth grid with dimensions, range, mean, non-zero coverage and a compact histogram; `wtrVertexData` exposes water vertices, triangles, bounds and bounded geometry previews; `rgnVolumeData` exposes the extruded region footprint with per-edge height, triangle topology and bounds. Heightmap `VertexData` on `.hms` placements reuses PugTools' native terrain decoder and surfaces dimensions, sparse/dense layout, elevation range, holes, terrain-layer count and painted Dynamic Detail channels. Encoded UTF-16/ASCII-hex wrappers and zstd/gzip/zlib payloads are decoded before inspection, matching the same payload path already used by the World Browser.

Direct TOR libraries now also use Jedipedia-style fault isolation during lazy indexing: malformed or truncated archives are recorded as load warnings and skipped while healthy sibling archives remain searchable. The Tools data-load view reports any warnings that were actually encountered while locating/loading the GOM, including the archive path and parser message. If every archive in a library is invalid, loading still fails normally rather than hiding the problem.

## 2026-09-09 v17 – native static SpeedTree world geometry

The World Browser now streams legacy SWTOR `.spt` placements from their native procedural source instead of requiring a same-stem visual GR2. The existing structured SPT reader feeds a bounded deterministic generator for branch tubes, recursive child stems, alpha-tested crossed leaf cards and authored frond textures. Branch/leaf/frond materials use the texture paths stored in the SPT/MapBank records, and the generated model is fitted to the original SpeedTree footer bounds so SWTOR world placement scale remains useful. Generation runs on the existing world model workers and is capped per tree to protect large areas from malformed or unusually dense authored trees.

A same-stem `.gr2` is retained only as a compatibility fallback when an unusual/beta SPT cannot be expanded; `.spt.gr2` collision/proxy companions are still never rendered as the visible tree. Runtime-generated SpeedTree materials are marked separately so the world texture LRU can evict/recreate their DDS resources without trying to parse a non-existent `.mat`. This is a static reconstruction: SpeedTree wind animation, exact proprietary branch pruning/forking and native billboard LOD generation remain future refinements. No database or sidecar is required.

A suite of tools for datamining Star Wars: The Old Republic.

## 2026-09-09 v16 – database-free inspection tools

This branch adds four inspection features that operate directly on the selected SWTOR installation and loaded GOM, without a patch-history database or generated sidecar. The Node Browser can export a single prototype as readable JSON or an entire selected prototype branch as a JSON ZIP, including base/GLOM class metadata. Its raw-data view also exposes a new **Prototype structure** group with base class, component classes, dynamically glommed classes and the effective DOM field set.

The main Tools window now has a **Shader Browser** for `retailclient/shaders/shaders.bin` and standalone `.fxo` files. It scans validated DXBC containers and displays shader model, chunks, constant buffers, variables, bound resources and input/output signatures; an individual compiled shader can be exported as FXO. SCPT previews now add a **Native assembly** section for live payload code sections, using the Iced x86/x64 decoder with NASM formatting, SCPT symbols and relocation annotations. This adds an `Iced` NuGet dependency but no runtime database.

The native SpeedTree follow-up described above in v17 builds on this branch's existing structured `.spt` parser.

## 2026-09-08 v5 – more offline Jedipedia parity

The Asset Browser now performs bounded content-based type detection at preview time, adds a structured Animation Actor Model (`<aam>`) reader and old Character Specification DAT support, and can show Jedipedia-style same-checksum files from loaded TOR metadata without decompressing every match. Filename Finder gains exact-hash MTXStore siblings, correct SystemGenerated world-relative DAT candidates and HIRC-derived streamed WEM/OGG names using `AudioSourceId`; no unbounded numeric WEM brute force was added.

The Model Browser Export button now writes the assembled GR2 geometry to Wavefront OBJ/MTL, including transforms, UVs, mesh-piece material assignments and SWTOR source material/texture references as comments. The World Browser adds `Tools -> Find instance…` for the loaded area, with asset/room/instance/asset-ID search and double-click teleport.

See `PUGTOOLS_FIXES_2026-09-06.md` and `PUGTOOLS_VALIDATION_2026-09-08.txt` for implementation and validation notes. This package is source-validated only in the current environment; run a real Visual Studio/.NET build before replacing a production executable.


## 2026-09-06 browser / hash / filename-finder fixes

The Asset/Model/Node/World launch buttons now create independent browser windows instead of one process-wide Form instance per browser type. Closing a browser no longer unloads the shared filename dictionary or forces a full/LOH collection, and render-thread shutdown is bounded on the WinForms UI thread. Model Browser tree nodes explicitly use the existing folder/file icons.

Build comparison now identifies files by SWTOR's global 64-bit path hash `(PH << 32) | SH`, not by `library + hash`; a file that merely moves between TOR/library groups is therefore not reported as one New plus one Removed file. Content CRC/size changes remain Changed. Compare mode now exposes explicit **Unchanged** buckets in Asset Browser, Model Browser (GR2) and Node Browser as well.

FXSPEC/EPP/PRT text decoding is BOM-aware and also detects BOM-less UTF-16LE/BE. Cooked FXSPEC trailing NUL bytes are removed before XML parsing, fixing the typical `0x00 / line 1, position 2` failure from UTF-16 files being read as UTF-8.

Filename discoveries stay in memory for the session. PugTools asks once, when the main window closes, whether the unique new/updated filename hashes should be written to the compact PFD1 dictionary. The old COL/MTX path no longer discards and re-reads the complete dictionary. Filename Finder validates candidates directly against unresolved hashes in the current build, includes the 132 historical Jedipedia wildcard hints, and performs a bounded scan for exact-hash-verified resource paths embedded in small cooked files. See `PUGTOOLS_FIXES_2026-09-06.md` for details and validation limits.

The World Browser now discovers known `/resources/world/livecontent/systemgenerated/<id>/...` area IDs directly from the compact filename pool on every client generation, probes the installed `<id>/area.dat`, and groups installed matches under **Live Content / System Generated**. Room DATs already load relative to the selected `area.dat`; mapnotes and map-page textures now also follow the actual loaded area directory instead of hard-coding the two previously known generated-world IDs.


## Offline Jedipedia additions in this branch

### DYN / browser-close hardening (2026-09-08)

The Model Browser DYN preview now accepts current SWTOR `dynObjectDataList` data (including its numeric stable field IDs and numeric-keyed GOM list serialization) as well as the older `dynVisualList` layout. Non-renderable DYN helper/material nodes no longer raise the misleading `Missing Visual List Spec` modal. DYN transform vectors also accept current serialized list/map shapes.

Asset/Node/Model preview shutdown is now bounded: WinForms no longer waits indefinitely on a Direct3D render thread, and D3D `Clear`/`Dispose` is not raced against a thread still inside `Present`. Large Asset/Node/Model tree preparation and Asset build-comparison indexing observe close/cancel signals while enumerating, sorting and linking nodes, so closing a browser during startup/filtering does not leave a CPU-heavy tree build running in the background.

### Multi-browser stability (v15.4)

Asset, Model, Node and World Browser can now coexist against the same current/previous SWTOR data set without one browser tearing down process-wide caches owned by another. TOR entry streams are opened as shared read handles, browser indexing no longer mutates the shared filename/hash dictionary, GOM lazy load/unload transitions and lazy filename publication are synchronized/race-safe, and closing Model/FileTable windows no longer unloads the global hash cache. The Tools window also refuses destructive data unload or current/previous source changes while data-browser windows are open.

Two startup crashes found from real JIT traces are fixed as part of the same hardening pass: AudioBankPreview no longer applies invalid SplitContainer minima/distances before WinForms has a real client size, and ModelBrowser tolerates missing item localization/appearance data instead of dereferencing null. Effect/Material/Gameplay splitters use the same post-layout clamping helper to avoid equivalent DPI/initial-size crashes later. ModelBrowser's managed tree is prepared on the worker and attached to the native TreeView only on the UI thread.

The Node Browser contains an interpreted/localized preview layer, reverse references, large 3D previews and a dedicated offline conversation-tree viewer. See `JEDIPEDIA_OFFLINE_AUDIT_2026-09-05.md` for the implemented feature set, performance fixes, parity gaps and next planned ports from Jedipedia.

Asset and Node live filters now use compact runtime search arrays, cancellable/bounded background filtering and a **separate native result TreeView**. The large full TreeView is never cleared when a filter starts or ends; this removes the remaining Win32 `Nodes.Clear()`/re-attach freeze that could still occur in the previous performance branch. Broad queries stop after 2,500 candidate hits instead of scanning millions of additional names merely to compute an exact count; the materialized hierarchy is additionally capped at 8,000 result/ancestor nodes. The hot matcher avoids per-file `params`/interface-enumerator allocations that previously caused heavy GC pressure. Filename lookup prefers the bundled `PFD1` compact store: 1.46 million unique UTF-8 paths are front-coded into one shared pool and exposed lazily to the existing `HashData` API. The legacy `hashes_filename.bin.gz` remains a streaming fallback and is still written for backwards compatibility.

Asset filtering also understands Jedipedia-style first-seen predicates (`>3.0`, `<4.0`, `=3.0`, etc.) whenever trustworthy local history exists. From the supplied filename corpus alone, PugTools can prove only whether a hash already existed in a Beta archive (`assets_*`, `he32_*`, `red_*`); public live patch numbers are deliberately left unknown until local build snapshots provide real chronology. Unknown history is never guessed and is reported in the filter status. Node patch history likewise still requires local build snapshots.

DDS previews are interactive in the Asset Browser. Plain 2D DDS textures support mouse-wheel zoom around the cursor and left-button drag panning; double-click restores fit/1:1. DDS cubemaps are detected from the DevIL face data and shown as a navigable environment: drag to rotate the view, use the wheel to change field of view/zoom, and double-click to reset. Cubemap face conversion and perspective rendering stay off the UI thread, with a reduced render budget while dragging.

The World Browser now includes database-free Jedipedia-style inspection controls: `Render -> Y-slice / cutaway` clips world geometry above an interactive vertical plane on the GPU; selected static placements can be temporarily hidden and restored for interior/roof inspection; `Tools -> Streaming debug bounds` visualizes active/floor/prefetch room bounds; and Live stats includes streaming residency/queue counters. Jedipedia's automatic roof grouping itself still relies on a generated `roofs.json` sidecar, so PugTools intentionally uses manual placement hiding rather than inventing derived roof metadata.

The World Browser minimap is freely resizable from all four edges and all four corners. Horizontal/vertical edge drags change only that axis, manual dimensions survive minimap snapshot refreshes, and non-proportional window sizes reveal more world in the expanded direction rather than stretching the map image.

PRT/FX inspection is now database-free as well. `.prt` files open in a dedicated Jedipedia-style particle inspector with default-value hiding, value/track/gradient visualization, asset-reference extraction, inline DDS texture previews and an interactive simulation tab. The simulation tab is driven by the **same `WorldPrtSystem` runtime used by the World Browser**, so emission, lifetime, emitter shape, motion/tracks, child PRTs and nested FX state do not use a separate fake preview engine. The Simulation tab now defaults to an orbitable perspective 3D view with real DDS sprite-atlas frames, authored tint/alpha, trail ribbons, orbit/pan/dolly controls and automatic framing; XY/XZ/YZ debug projections remain available. `.fxspec` and `.epp` now use a structured recursive XML inspector with detected resource links instead of only the generic XML view; double-clicking references navigates directly to the linked offline asset.

MAT/TEX/EMT now have a dedicated database-free material inspector as well. MAT separates shader/material parameters from typed inputs, resolves `.tex`/`.tiny.dds`/`.dds` links with SWTOR `<variable>` fallback plus `[gen]`/`[bt]` placeholders, and adds an approximate shaded sphere using Diffuse + Rotation(A/G normal, B emissive, R inverse-alpha) + Gloss. The texture tab supports interactive zoom/pan plus RGB/R/G/B/A, reconstructed RotationMap normal, emissive and inverse-alpha channel views. TEX explains sampler/address/LOD/compression settings and links sibling DDS files; EMT resolves BlendDiffuse/BlendNormal and handles common UTF-16 encodings. All referenced files can be opened directly in the Asset Browser.


FXSPEC/EPP/PRT dependency inspection now also includes an interactive **Graph** tab. FXSPEC uses the same semantic element lists as the World Browser runtime (emitters, models, timing groups, lights, lightning, projectors, 3D sounds, attachers, transformers, model faders and transform arcs), then draws named StartLoc/AttachTo/start/stop/owner/target/operator relationships rather than a generic XML tree. Resource nodes are linked to PRT/GR2/DDS/TEX/etc., missing assets are marked against the currently loaded build, Wwise event names remain visible even when no file can be resolved, and double-click navigates directly to file-backed resources. The graph supports wheel zoom, drag panning, relation/resource toggles, Fit and Graphviz DOT export. PRT and EPP receive smaller dependency graphs as well (textures/GR2/EmitFXSpec and EPP fxSpec/projectile links).


BNK/Wwise inspection now has a database-free semantic graph as well. PugTools follows HIRC Event -> Event Action -> target object relationships, music-segment children and Sound/MusicTrack media IDs directly from the selected bank. Embedded DIDX/DATA WEMs can be played in place; streamed WEMs and STID-linked banks navigate back into the Asset Browser and are marked present/missing against the loaded build. The optional `bnk_id_dictionary.txt` is used only for friendly labels when it already exists and is not required by the graph. JBA playback also supports timeline scrubbing by clicking the progress bar and 0.25x-4x playback speed.

The Node Browser now exposes both directions of local GOM references: the existing background-built `Referenced by` index and an immediate per-node `References to` list with field paths and double-click navigation. The outgoing list is derived only from the selected node and therefore needs no persistent index/database.

MANIFEST/TBL/RUL now also have database-free Jedipedia-style structured views. MANIFEST lists string-table entries and available de-de/en-us/fr-fr STBs, TBL exposes DataTable rows plus schema/constraints with direct navigation for known resource columns, and RUL separates rules/tag exclusions/groups with dynamic-slot navigation. Large tables are display-capped while the full source remains in a Raw tab.

Gameplay presentation is extended again in v14. The specialized explorer now carries the current node's decoded SWTOR icon in a Jedipedia-style header; Achievement, Codex, Schematic and Map Note nodes receive dedicated relationship graphs instead of the generic summary. Quest/map-note entries expose **Show in World** from both tree and graph views. The World Browser resolves the authored placement lazily from the installed `mapnotes.not` files, switches to the matching area and teleports to the note; results are cached only for the current process and no location database/sidecar is generated.

### Jedipedia File Reader parity

The Asset Browser now also recognizes and gives structured previews for Jedipedia reader formats `BIN`, `BKT`, `FBX`, `GOM`, `INFO`, `LIST` and `NODE` (with bounded previews for very large indexes). See `JEDIPEDIA_OFFLINE_AUDIT_2026-09-05.md` for the current parity audit and intentionally partial areas.

### World Browser legacy data

The World Browser also contains an additive compatibility layer for SWTOR RED/Beta and early 32-bit data. Current 64-bit/Retail loaders and named GOM lookups stay first; old-client fallbacks are activated only for detected beta/500-bucket clients. The compatibility layer recovers unnamed prototype FQNs/IDs and old GOM container layouts for conversations, maps/map notes/icons, MapLinks/Wonkavators, taxi, Codex, world entities and space-combat data.

### World Browser: SWTOR-style area title overlay
- Optional Navigation toggle: `Area title on room change` (disabled by default).
- Blue/cyan SWTOR-style outlined label; authored `<br>` tags are rendered as real line breaks instead of literal markup.
- Uses `str.sys.worldmap` / `MapPage.<mapName>` where available and falls back to the technical room name shown in the status bar.
- Optional `Use room names instead of map names` mode shows the `.room` name and announces every room-name change.
- In normal map-name mode, consecutive technical rooms with the same player-visible area title do not repeatedly trigger the banner.

### Deep Wwise controls (v11)

BNK inspection now goes beyond Event/Action/WEM links. HIRC parsing is object-length bounded, handles the old Beta uint32 HIRC type layout, and exposes parent/output-bus hierarchy, child containers, Switch/State mappings, RTPCs, attenuation, effects and bus ducking where present. `init.bnk` ENVS obstruction/occlusion curves are decoded as well. The BNK viewer has a dedicated **Controls / RTPC** tab with curve preview, while the graph adds parent/child/bus/attenuation/effect/switch/ducking relations. Large banks are protected by explicit graph/row caps rather than materialising an unbounded WinForms graph.

### Gameplay / GOM semantic explorer (v12)

The Node Browser now offers a database-free **Gameplay explorer** for common SWTOR gameplay nodes. Abilities are expanded into their real Effect flow and SubEffect definitions with readable Jedipedia-compatible names for actions, conditions, initializers, target overrides and triggers instead of opaque numeric function ids. Items, NPCs, quests, achievements, codex entries, schematics, talents, map notes and placeables receive structured relationship trees with direct double-click navigation to locally present target nodes. Quest journal branches/steps/tasks, objective NPC/placeable/map-note targets, quest items and rewards are kept together instead of being scattered through the raw GOM field tree. The explorer is search/copy enabled and capped at 5,000 native TreeNodes to avoid turning a very large gameplay spec into another WinForms freeze path. It uses only the currently loaded GOM and existing GomLib loaders; no Jedipedia database or generated sidecar is required.

### Gameplay relationship graphs / readable Effect params (v13)

The database-free Gameplay Explorer now has a second **Relationship graph** tab. Abilities are graphed as Ability -> Effect -> SubEffect -> Action/Condition/Initializer/Trigger/TargetOverride, including child-effect calls, EPP links and any parameter values that resolve to locally loaded GOM nodes. Quests are graphed as Branch -> Step -> Task -> NPC/Placeable/Quest/MapNote/Item/Reward targets, while Items and NPCs expose their direct ability, set-bonus, schematic, package, appearance, conversation, weapon and related gameplay links. Graph nodes support wheel zoom, drag panning, Fit, edge-label toggling, Graphviz DOT export and double-click navigation back into the Node Browser; hard node/edge caps keep very large specs bounded.

Effect parameter loading now preserves each GOM `ScriptEnum` key name in addition to its numeric value. The structured tree and relationship graph therefore display authored names when BioWare's schema provides them instead of only `Param 12`, while retaining the numeric key for exact low-level inspection. ID/FQN values and ID lists are resolved against the currently loaded DOM on demand; no parameter-name database or generated sidecar is required.

### Gameplay visual bridge (v15)

The Node Gameplay Explorer now links semantic gameplay data directly to the existing visual tools. NPC visual appearances and item/model appearances expose **3D / Model** actions in both tree and graph views and open/reuse the matching Live/PTS Model Browser. Gameplay graph nodes can display a bounded set of asynchronously decoded SWTOR icons, avoiding synchronous DDS work when a large graph opens. Quest/MapNote **Show in World** navigation now leaves a visible world-space target ring/pillar at the authored map-note placement, including on the interactive M-map, without requiring a quest-location database.

### Jedipedia metadata/list parity (v6)

The Asset Browser now has structured offline views for `mapnotes.not`, the two shipped Scaleform GUI XML `.lst` indexes, and `/resources/version.txt`. Map-note rows expose authored placement/flags/tags and can navigate to locally present map-page DDS assets. The Filename Finder also harvests the GUI XML lists and `stb.manifest` as cheap candidate sources; all resulting names still require exact PH+SH validation before entering the hash dictionary. Presence checks inside the new structured tables are cached and visible rows remain bounded to avoid turning these metadata views into another UI stall.
