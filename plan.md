# Plan: VintageVoxel Refactor for Readability & Organization

## TL;DR
The codebase has solid individual components but suffers from a 1141-line God Object (Game.cs), widespread GPU boilerplate duplication, physics/collision mixed into Camera, duplicated DDA logic in Raycaster, and scattered magic numbers. The plan splits responsibilities into focused classes, extracts reusable utilities, and unifies naming.

---

## Phase 1 — Extract GpuResourceManager (CRITICAL, unblocks everything)

**Goal**: Eliminate 4 copies of VAO/VBO/EBO boilerplate

1. Create `VintageVoxel/Rendering/GpuResourceManager.cs`
   - Single method: `UploadMesh(float[] vertices, uint[] indices, int stride) → GpuMesh`
   - `GpuMesh` struct holds `Vao, Vbo, Ebo, IndexCount`
   - Tracks all allocated meshes for lifecycle management (`Free(GpuMesh)`)
2. Refactor `Game.cs` UploadChunk() and GetOrCreateModelGpu() to use GpuResourceManager
3. Refactor `HUDRenderer.cs` constructor GPU setup
4. Refactor `EntityItemRenderer.cs` constructor GPU setup

**Dependencies**: None — can start immediately
**Files**: Game.cs (lines ~160-245), HUDRenderer.cs (ctor), EntityItemRenderer.cs (ctor)

---

## Phase 2 — Extract PhysicsSystem + CollisionSystem (HIGH)

**Goal**: Remove physics & collision from Camera.cs

5. Create `VintageVoxel/Physics/PhysicsSystem.cs`
   - Move: Gravity, JumpSpeed, MaxFallSpeed constants (currently line 27-41 Camera.cs)
   - Move: PhysicsUpdate() body (gravity integration, line 100-135 Camera.cs)
   - Move: CreativeMode flag logic
   - `PhysicsSystem.Update(float dt, Camera camera, World world)`
6. Create `VintageVoxel/Physics/CollisionSystem.cs`
   - Move: IsCollidingAt() from Camera.cs (lines 185-210)
   - Move: IsOnGround() probe logic
   - Takes `World` reference, returns bool
7. Slim down `Camera.cs` to view/projection matrices + input vectors only (~120 lines)

**Dependencies**: None (Camera.cs is self-contained)

---

## Phase 3 — Split Game.cs God Object (CRITICAL)

**Goal**: Reduce Game.cs from 1141 lines to ~300 (window lifecycle + loop orchestration only)

8. Create `VintageVoxel/Rendering/WorldRenderer.cs`
   - Move: chunk rendering, model rendering from OnRenderFrame (lines ~785-970)
   - Move: frustum culling call, HUD 3D item rendering (RenderHotbarItems3D)
   - Move: `_chunkGpuData`, `_modelGpu`, `_placedModels` dictionaries
9. Create `VintageVoxel/World/WorldStreamer.cs`
   - Move: chunk load/unload logic, streaming radius logic from OnUpdateFrame (lines ~520-580)
   - Move: RebuildChunk, RebuildAffectedChunks calls
10. Create `VintageVoxel/Input/InteractionHandler.cs`
    - Move: raycasting call + block placement/breaking from OnKeyDown/OnMouseDown (~1014-1080)
    - Move: selected block highlight rendering
11. Create `VintageVoxel/Debug/DebugState.cs`
    - Value object: WireframeEnabled, ShowChunkBorders, ShowLightValues (public bools)
    - Remove scattered bools from Game.cs / DebugWindow.cs coupling
12. Reduce `Game.cs` to: window setup, game loop orchestration, subsystem initialization

**Dependencies**: Phase 1 (GpuResourceManager) should be done before Step 8

---

## Phase 4 — Extract DDA Traversal (HIGH, DRY)

**Goal**: Eliminate 150 lines of duplicate DDA code in Raycaster.cs

13. Create `VintageVoxel/World/DdaTraversal.cs`
    - Generic DDA stepping struct: `StepX/Y/Z`, `tMax`, `tDelta`, `Normal`
    - Static `Initialize(origin, dir, gridSize)` factory
    - `Step()` method advances one cell
14. Refactor `Raycaster.Cast()` to use DdaTraversal (lines 30-100)
15. Refactor `Raycaster.CastSubVoxel()` to use DdaTraversal (lines 140-250) — parameterize gridSize=16

**Dependencies**: None

---

## Phase 5 — Extract FaceEmitter (MEDIUM, DRY)

**Goal**: Remove 90% duplication between EmitFace and EmitSubFace in ChunkMeshBuilder.cs

16. Create `VintageVoxel/Rendering/FaceEmitter.cs`
    - Accepts face params: `position, normal, uvOffset, light, ao`
    - Produces 4 vertices + 6 indices
    - Used by both regular and sub-voxel mesh building
17. Refactor `ChunkMeshBuilder.EmitFace()` to use FaceEmitter
18. Refactor `ChunkMeshBuilder.EmitSubFace()` to use FaceEmitter
19. Fix sub-voxel emission hardcoding light=1.0, ao=1.0 (line ~350-370 ChunkMeshBuilder.cs) to compute actual values

**Dependencies**: None (internal to ChunkMeshBuilder)

---

## Phase 6 — Extract Constants & Fix Naming (MEDIUM)

**Goal**: Remove magic numbers; unify naming conventions

20. Create `VintageVoxel/Constants.cs` (or per-domain static classes)
    - `Physics`: Gravity=-25f, JumpSpeed=8f, MaxFallSpeed=60f, EyeHeight, PlayerWidth, PlayerHeight
    - `Light`: MaxSunLight=15, MaxBlockLight=14
    - `World`: ChunkSize=32, SubVoxelSize=16, ChiseledBlockId=999
    - `Render`: HotbarSlotSize=50, HotbarSlotGap=4
21. Replace all hardcoded values in Camera.cs, LightEngine.cs, ChunkMeshBuilder.cs, WorldPersistence.cs, HUDRenderer.cs, Raycaster.cs

**Naming unification**:
22. Standardize rendering entry point: use `Render()` everywhere (HUDRenderer uses `Draw()` — fix)
23. Standardize block position params: prefer `Vector3i blockPos` over `(wx, wy, wz)` tuples

---

## Phase 7 — Extract RleCodec + WorldPersistence cleanup (MEDIUM)

**Goal**: DRY up RLE encoding, improve format robustness

24. Create `VintageVoxel/World/RleCodec.cs`
    - `Encode(byte[] data) → byte[]`
    - `Decode(BinaryReader, int count) → byte[]`
25. Refactor `WorldPersistence.WriteBlockRle()` and `WriteSubVoxelRle()` to use RleCodec
26. Replace hardcoded 4096 in WorldPersistence with `ChiseledBlockData.SubVolume` constant

**Dependencies**: None

---

## Phase 8 — LightEngine Incremental Updates (MEDIUM, optional for readability)

**Goal**: Fix full-recompute-on-every-change performance issue (not pure readability but intertwined)

27. Add dirty-tracking: `_dirtyChunks HashSet` in LightEngine
28. Change `UpdateAtBlock()` to mark dirty region instead of immediate full recompute
29. Flush dirty regions once per frame in `Update()` method
30. Implement block light emission — wire up `SeedBlockLightChunk()` for torch block (ID 4)

**Dependencies**: Phase 3 (WorldStreamer owns update loop)

---

## Relevant Files

- [VintageVoxel/Game.cs](VintageVoxel/Game.cs) — Primary split target (~1141 lines)
- [VintageVoxel/Camera.cs](VintageVoxel/Camera.cs) — Extract PhysicsSystem, CollisionSystem
- [VintageVoxel/ChunkMeshBuilder.cs](VintageVoxel/ChunkMeshBuilder.cs) — Extract FaceEmitter
- [VintageVoxel/Raycaster.cs](VintageVoxel/Raycaster.cs) — Extract DdaTraversal
- [VintageVoxel/LightEngine.cs](VintageVoxel/LightEngine.cs) — Incremental updates
- [VintageVoxel/WorldPersistence.cs](VintageVoxel/WorldPersistence.cs) — Extract RleCodec
- [VintageVoxel/HUDRenderer.cs](VintageVoxel/HUDRenderer.cs) — Use GpuResourceManager, absorb RenderHotbarItems3D
- [VintageVoxel/EntityItemRenderer.cs](VintageVoxel/EntityItemRenderer.cs) — Use GpuResourceManager
- [VintageVoxel/DebugWindow.cs](VintageVoxel/DebugWindow.cs) — Use DebugState, decouple from Game

**New files to create**:
- `VintageVoxel/Rendering/GpuResourceManager.cs`
- `VintageVoxel/Rendering/WorldRenderer.cs`
- `VintageVoxel/Rendering/FaceEmitter.cs`
- `VintageVoxel/Physics/PhysicsSystem.cs`
- `VintageVoxel/Physics/CollisionSystem.cs`
- `VintageVoxel/World/WorldStreamer.cs`
- `VintageVoxel/World/DdaTraversal.cs`
- `VintageVoxel/World/RleCodec.cs`
- `VintageVoxel/Input/InteractionHandler.cs`
- `VintageVoxel/Debug/DebugState.cs`
- `VintageVoxel/Constants.cs`

---

## Verification

1. Build solution (`dotnet build VintageVoxel.sln --configuration Debug`) — zero errors after each phase
2. Run game: world loads, terrain renders, player moves with gravity, block placement/breaking works
3. Verify no regression in light propagation (place/remove blocks, observe light updates)
4. Verify chunk streaming: move far from origin, old chunks unload, new chunks load correctly
5. Verify persistence: save world, exit, reload — world state is identical
6. Confirm ImGui debug overlay renders, all toggles work (wireframe, chunk borders)
7. Drop item → item entity renders and can be picked up

---

## Decisions

- **Scope**: Refactoring only — no new gameplay features, no algorithm changes (except incremental lighting in Phase 8 which is also a correctness fix for torches)
- **Excluded**: EngineCore/ folder (duplicate code, appears unused — out of scope)
- **Phasing**: Each phase is independently buildable; work sequentially Phase 1→2→3, Phases 4-7 can run in parallel with Phase 3
- **No interfaces introduced** unless naturally needed — keep abstractions concrete until duplication forces abstraction (YAGNI)
- **Folder structure**: Group by domain (`Rendering/`, `Physics/`, `World/`, `Input/`, `Debug/`) rather than flat

---

## Further Considerations

1. **EngineCore/ folder**: Contains near-duplicate copies of many VintageVoxel/ files (Block.cs, Camera.cs, etc.) — appears to be an abandoned shared library. Should it be removed or integrated? Recommend: remove to avoid confusion (separate PR).
2. **Phase 8 (LightEngine)**: Incremental lighting overlaps with torch feature on roadmap — coordinate with that feature to avoid double-work.
