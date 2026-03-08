# VintageVoxel — Project Status

## Tech Stack
- **Language:** C# (.NET 8.0)
- **Graphics:** OpenTK 4.9.3
- **API:** OpenGL 4.5 Core Profile
- **Math:** OpenTK.Mathematics

---

## Phase Status

| Phase | Title                        | Status      |
|-------|------------------------------|-------------|
| 1     | Foundation (Window & Loop)   | ✅ Done     |
| 2     | Graphics Pipeline (Shaders)  | ✅ Done     |
| 3     | 3D World (Camera & Cube)     | ✅ Done     |
| 4     | Voxel Data Structure         | ✅ Done     |
| 5     | Mesher (Procedural Geometry) | ✅ Done     |
| 6     | Texturing (Texture Atlas)    | ✅ Done     |
| 7     | Infinite World Generation    | ✅ Done     |
| 8     | Interaction (Raycasting)     | ✅ Done     |
| 9     | UI Dashboard & Debugging     | ✅ Done     |
| 10    | Physics & Movement           | ✅ Done     |
| 11    | Advanced Lighting (Flood Fill) | ✅ Done   |
| 12    | Optimization (Frustum Culling) | ✅ Done   |
| 13    | Chiseling (Micro-Blocks)       | ✅ Done   |
| 14    | Persistence (Saving/Loading)   | ✅ Done   |
| 15    | Game State Management (Menus)  | ✅ Done   |
| 16    | Inventory Architecture         | ✅ Done   |
| 17    | HUD (Heads-Up Display)         | ✅ Done   |
| E1    | Asset Editor: Architecture & ImGui Layout | ✅ Done |
| E2    | Asset Editor: Microblock Canvas (3D Voxel Editing) | 🔲 Todo |
| E3    | Asset Editor: 2D Texture Painting | 🔲 Todo |
| E4    | Asset Editor: JSON Model Exporter  | 🔲 Todo |

---

## What Works Right Now
- 800×600 window with sky-blue background
- OpenGL 4.5 Core Profile context (depth test + back-face culling enabled)
- Shader system: compiles `.vert`/`.frag` GLSL files, links GPU program, exposes uniform helpers
- Static 3D cube rendered at the world origin
- `Block` struct: `ushort Id` + `bool IsTransparent`; `Block.Air` sentinel; `IsEmpty` helper
- `Chunk` class: 32×32×32 flat `Block[]` array; index formula `x + 32*(y + 32*z)`; bottom 16 layers filled with Dirt (ID 1), top 16 with Air
- `ChunkMeshBuilder` + `ChunkMesh`: iterates every block, checks 6 neighbours, emits only exposed faces (CCW quads) as vertex+index arrays uploaded to the GPU in one draw call
- One 32×32×32 chunk rendered — camera positioned above terrain at (16, 22, 50) looking in
- Texture atlas: procedurally generated 48×16 atlas (3×16×16 tiles — Dirt, Stone, Grass top); nearest-neighbour filtering for crisp voxel look
- `Texture` class: uploads raw RGBA bytes to GPU, configures filtering/wrapping, manages lifetime
- `BlockRegistry`: maps block IDs to per-face atlas tile indices (top / bottom / side)
- Vertex format upgraded to 5 floats (xyz + uv); `ChunkMeshBuilder` writes atlas UVs per face using `BlockRegistry`
- First-person fly camera:
  - WASD — horizontal movement
  - E / Q — fly up / down
  - Mouse — look (yaw + pitch, pitch clamped ±89°)
  - Escape — close window
- Infinite procedural world:
  - `NoiseGenerator`: classic Perlin gradient noise with a 256-entry permutation table; `Octave()` stacks 4 fBm layers (lacunarity 2, persistence 0.5) for natural terrain shape
  - `World` class: `Dictionary<Vector2i, Chunk>` keyed by 2-D chunk-space (X, Z) coordinate; `RenderDistance = 4` → 9×9 = 81 chunks always loaded; `UnloadDistance = 6` prevents thrashing at chunk edges
  - `World.Update(playerPos)` called every frame — generates missing chunks within `RenderDistance`, unloads those beyond `UnloadDistance`, returns `added` / `removed` lists so `Game` can manage GPU resources
  - `World.GetBlock(worldX, worldY, worldZ)` — cross-world block query used by the mesher for seam culling (returns Air for unloaded chunks / out-of-range Y)
  - Terrain profile per column: Stone (ID 2) below surface−3, Dirt (ID 1) from surface−3 to surface−1, Grass (ID 3) at surface, Air above; height sampled via 4-octave Perlin noise, range 6–22 blocks
  - `ChunkMeshBuilder.Build(chunk, world)` — updated to accept an optional `World` reference; boundary faces check the neighbouring chunk via `World.GetBlock()` instead of always being exposed, eliminating wasted geometry at seams
  - `Game`: per-chunk `ChunkGpu` record (VAO + VBO + EBO + IndexCount) stored in `Dictionary<Vector2i, ChunkGpu>`; `UploadChunk()` / `DeleteChunkGpu()` helpers manage GPU lifetime; on new-chunk arrival the four cardinal neighbours are also re-meshed so previously-exposed seam faces get culled
  - Per-frame render loop translates each chunk mesh to world space via `Matrix4.CreateTranslation(cx * 32, 0, cz * 32)` before the draw call
- Block interaction via DDA raycasting:
  - `Raycaster.Cast(origin, direction, world)` — Amanatides & Woo DDA voxel traversal; steps through the voxel grid one cell at a time along the ray (always advancing to the nearest axis-aligned boundary), guaranteeing no voxels are skipped; returns the first solid block hit within 8 world units plus the outward face normal of the entered face
  - Left click — breaks the targeted block (sets it to Air, ID 0); triggers a mesh rebuild of the owning chunk and any adjacent chunks on whose boundary the block sits
  - Right click — places a Stone block (ID 2) at the face-adjacent position indicated by the hit normal; same re-mesh logic applies
  - `World.SetBlock(worldX, worldY, worldZ, block)` — mutates a block in-place via the existing `ref Block GetBlock()` handle; returns false for out-of-range / unloaded positions
  - `Camera.Front` — exposes the normalised look direction to the raycaster
  - `Game.RebuildChunk(key)` / `Game.RebuildAffectedChunks(wx, wy, wz)` — re-meshes the owning chunk plus the four cardinal neighbours when the modified block is on a chunk boundary, keeping seam culling correct after edits
- **ImGui developer dashboard (Phase 9):**
  - `ImGui.NET 1.89.7.1` integrated via a custom `ImGuiController` OpenTK-4 backend: font atlas uploaded to GPU, inline GLSL shader compiled at runtime, dynamic VAO/VBO/EBO for per-frame draw lists; input relayed from OpenTK `MouseState`/`KeyboardState` each frame
  - **`F3`** toggles the debug overlay on/off; cursor is released (Normal) when the overlay is open so checkboxes are interactive, and re-grabbed when hidden for FPS camera operation
  - `DebugWindow` — ImGui window pinned to the top-left corner showing:
    - **FPS** (exponential moving-average smoothed) and **Frame Time** (ms)
    - **Player Position** (X / Y / Z) updated live every frame
    - **Chunks Loaded** count
    - Three interactive checkboxes:
      - **Wireframe Mode** — `GL.PolygonMode(Line/Fill)` toggled before/after the chunk draw loop; ImGui always renders in fill mode regardless
      - **Show Chunk Borders** — `ChunkBorderRenderer` draws 12 red GL_LINES edges around each loaded chunk AABB (depth test disabled so borders are always visible); geometry lazily rebuilt into a dynamic VBO whenever the chunk set changes
      - **No Textures (White)** — sets the `uNoTexture` uniform on `shader.frag`; when non-zero the fragment shader returns `vec4(1.0)` (solid white) instead of sampling the atlas
  - `ChunkBorderRenderer` — owns a separate `line.vert`/`line.frag` shader pair and a single VBO; `UpdateGeometry()` iterates all loaded chunk keys and emits 24 line-endpoint vertices (3 floats each) per chunk; `Render()` binds the shader, uploads view/projection, and issues a single `GL.DrawArrays(Lines)` call
  - `shader.frag` updated with `uniform int uNoTexture` toggle — backward compatible (defaults to 0 = textured) so all prior rendering is unchanged
- **View Frustum Culling (Phase 12):**
  - `Frustum` struct (`Frustum.cs`) — extracts 6 clipping planes from the combined view-projection matrix using the Gribb-Hartmann method; planes are stored as normalised `Vector4` (a, b, c, d) with convention a·x + b·y + c·z + d ≥ 0 for inside
  - Because OpenTK sends matrices with `transpose=false` (row-major storage reinterpreted as column-major by OpenGL), the effective clip transform is `Transpose(view × projection) × worldPos`; plane extraction runs on `Matrix4.Transpose(view * projection)` so the planes sit correctly in world space
  - `Frustum.ContainsAabb(min, max)` — AABB vs. frustum test using the **positive-vertex optimisation**: for each of the 6 planes, only the single corner most aligned with the plane normal is tested; if that corner is outside the plane the entire box is outside → O(1) per chunk instead of 8 corner tests
  - `Frustum.FromViewProjection(view, projection)` — factory method takes the already-computed matrices to avoid redundant matrix construction
  - `Game.OnRenderFrame` — builds the frustum once per frame (after computing `view`/`projection` for the shader uniforms); each chunk's world-space AABB `[cx×32 .. (cx+1)×32] × [0..32] × [cz×32 .. (cz+1)×32]` is tested before issuing any draw call; chunks behind/beside the camera are skipped with zero GPU cost
  - Chunks are still fully loaded and meshed regardless of visibility; frustum culling is a draw-call filter only, so streaming, lighting, and interaction logic are unaffected
- **Chiseling — Micro-Block System (Phase 13):**
  - **`ChiseledBlockData`** — new class holding a flat `bool[4096]` array representing a 16×16×16 sub-voxel grid; `Index(x,y,z)` = `x + 16*(y + 16*z)`; `Get()` / `Set()` / `InBounds()` / `HasAnyFilled()` helpers; `SourceBlockId` preserves the original block type for texture lookup
  - **`Block.ChiseledId = 999`** — sentinel ID that marks a block as a chiseled container; stored as a normal `Block` in the chunk array with `IsTransparent = false` so meshing and physics treat it as solid until interacted with
  - **`Chunk.ChiseledBlocks`** — `Dictionary<int, ChiseledBlockData>` keyed by flat block array index; `GetOrCreateChiseled(x, y, z, sourceId)` creates a fully-filled entry on first access; loaded/unloaded with the owning chunk
  - **`ChunkMeshBuilder`** — detects `Block.ChiseledId` in the main block loop and calls `EmitChiseledBlock()`; sub-voxel loop iterates 16³ positions, applying the same six-face culling rule at sub-voxel granularity (intra-block neighbours first, then the adjacent full block at the outer boundary); `EmitSubFace()` emits a 0.0625-unit quad with the source block's atlas tile and `light = ao = 1.0`; normal blocks skip the chiseled path entirely
  - **`Raycaster.HitResult`** — extended with `IsChiseled` bool, `SubVoxelPos` (Vector3i [0,15]³), and `SubNormal` (sub-voxel face normal); two constructors — one for regular blocks, one for chiseled hits; backward-compatible defaults for non-chiseled hits
  - **`Raycaster.CastSubVoxel()`** — private helper that, when the outer DDA enters a `ChiseledId` block, computes the ray entry point via slab intersection, maps it into 16×16×16 integer sub-voxel space (scaled by N=16), and runs a standard DDA limited to the [0,16)³ grid; returns default (no hit) if all sub-voxels along the ray are empty, allowing the outer DDA to continue past the block
  - **`Raycaster.Cast()`** — modified check: when the hit block is `ChiseledId`, dispatches to `CastSubVoxel`; if that returns no hit (fully-chiseled block), the outer loop continues instead of stopping
  - **`World.GetChiselData(wx, wy, wz)`** — cross-chunk look-up returning the `ChiseledBlockData` for a world position, or `null` if not chiseled / not loaded
  - **Interaction controls:**
    - **Middle Click** — converts the targeted solid block into a chiseled container (all 4096 sub-voxels filled, inheriting source block's texture); triggers mesh rebuild
    - **Left Click on chiseled block** — removes the specific sub-voxel the ray hit; if the last sub-voxel is removed the container reverts to Air and its `ChiseledBlocks` entry is cleaned up
    - **Right Click on chiseled block** — fills the adjacent sub-voxel (using `SubNormal`); if the adjacent position is outside the chiseled block, falls back to placing a Stone block in world space
  - `DebugWindow` hint line updated to display the Middle / Left / Right click chiseling controls
- **Inventory Architecture (Phase 16):****
  - **`Item`** (`Item.cs`) — immutable class with `Id`, `Name`, `MaxStackSize`, and `TextureId` (atlas tile index); three pre-defined block-item singletons: `Item.Dirt` (ID 1), `Item.Stone` (ID 2), `Item.Grass` (ID 3)
  - **`ItemStack`** (`ItemStack.cs`) — lightweight mutable struct pairing an `Item?` reference and an integer `Count`; `IsEmpty` property; `ItemStack.Empty` sentinel matches the `default` value
  - **`Inventory`** (`Inventory.cs`) — fixed-size `ItemStack[]` array with `HotbarSize = 10` constant; `SelectedSlot` int tracks the active hotbar index; key methods:
    - `ScrollHotbar(int delta)` — wraps `SelectedSlot` cyclically (positive = next slot); called from `OnMouseWheel` with delta ±1
    - `SelectSlot(int index)` — direct selection clamped to [0, HotbarSize)
    - `AddItem(item, count)` — first merges into partial stacks, then fills empty slots; returns overflow count
    - `RemoveItem(item, count)` — removes from first matching stacks; returns actual count removed
    - `HasItem(item, count)` — returns true when total owned quantity meets the requirement
    - `HeldStack` — `ref` property giving direct access to the stack in `SelectedSlot`
  - **`Game._inventory`** — `Inventory(HotbarSize)` created at startup; pre-seeded with 64× Grass, 64× Dirt, 64× Stone stacks for testing
  - **`OnMouseWheel` override** — catches scroll events when `_gameState == Playing` and the cursor is grabbed; calls `_inventory.ScrollHotbar(±1)` so the mouse wheel cycles the hotbar
  - **`PlaceHeldBlock(wx, wy, wz)`** — new helper in `Game`; reads `_inventory.HeldStack` to determine the block ID to place (falls back to Stone ID 2 on an empty slot); replaces all hardcoded `Id = 2` right-click placements
  - **`DebugWindow.Draw`** gains `heldItem` (`ItemStack`) and `hotbarSlot` (`int`) parameters; overlay now shows `Held [N] : ItemName xCount` (or "(empty)") below the Mode line; hint row updated to include `[Scroll] Cycle hotbar`
- **HUD — Heads-Up Display (Phase 17):**
  - **`HUDRenderer`** (`HUDRenderer.cs`) — self-contained 2-D rendering class; owns a dedicated `hud.vert`/`hud.frag` shader pair, a single shared VAO/VBO/EBO (quad, 4 vertices × 4 floats — dynamic draw), and the orthographic projection matrix; renders every frame between the 3-D world pass and ImGui
  - **`hud.vert` / `hud.frag`** — minimal 2-D GLSL pair; vertex shader applies `uProjection` (orthographic, pixel-space) to a `vec2 aPosition`; fragment shader selects between flat `uColor` (solid quads) and `texture(uTexture, vTexCoord) * uColor` (atlas-tiled icons) via the `uUseTexture` toggle
  - **`Shader.SetVector4`** — new uniform helper added to `Shader.cs` to support the `uColor` `vec4` uniform
  - **2-D GL state management** — `Render()` enables `GL_BLEND` (SrcAlpha / OneMinusSrcAlpha) and disables `GL_DEPTH_TEST` + `GL_CULL_FACE` before drawing; restores all three after, ensuring 3-D state is never corrupted
  - **Crosshair** — two overlapping white quads (horizontal 20×2 px, vertical 2×20 px) centered at the exact screen midpoint; a 1-pixel-wider dark shadow quad under each bar keeps it readable against bright sky and snow
  - **Hotbar** — 10 × 50×50 px slots laid out horizontally at the bottom-center of the screen (4 px gap, 6 px bottom padding); each slot comprises: a 2-px dark border (`DrawQuad`), an inner background quad, and an optional item icon; the selected slot gets a bright white border and a lighter background tint
  - **Item icons** — textured via `DrawAtlasTile()`; tile index resolved with `BlockRegistry.TileForFace(itemId, 0)` (top face) so icons match the block's appearance in-world; 4 px inner padding shrinks the icon to 42×42 px inside the 50-px slot
  - **`SetScreenSize(w, h)`** — rebuilds the ortho matrix on window resize; hooked into `Game.OnResize` alongside the camera aspect-ratio update and ImGui resize
  - **`Game` integration** — `_hud` field initialised in `OnLoad` after the atlas is ready; `_hud.Render()` called in `OnRenderFrame` only when `_gameState == Playing` (HUD hidden on main menu and pause screen); `_hud.Dispose()` called in `OnUnload`
- **Game State Management (Phase 15):**
  - **`GameState` enum** (`GameState.cs`) — four named states: `MainMenu`, `Playing`, `Paused`, `Exiting`; documents the intended flow in XML comments
  - **`_gameState` field** in `Game` — starts as `GameState.MainMenu` so the game opens on the main menu rather than jumping straight into gameplay
  - **State-gated `OnUpdateFrame`** — physics (`Camera.PhysicsUpdate`), mouse-look, and chunk streaming are skipped unless `_gameState == Playing`; ImGui input relay always runs so menus receive input regardless of state
  - **`TransitionToPlaying()`** — sets state to `Playing`, resets the mouse-delta first-move flag, and grabs the cursor (respects the F3 debug overlay: cursor stays free if the overlay is open)
  - **`TransitionToPaused()`** — sets state to `Paused`, releases the cursor so the pause-menu buttons are clickable, resets first-move flag
  - **`DrawMainMenu()`** — ImGui overlay centered on screen; "VintageVoxel" title in cyan; two buttons: **Play** (calls `TransitionToPlaying`) and **Quit** (calls `Close`)
  - **`DrawPauseMenu()`** — ImGui overlay centered on screen; "— Paused —" title in gold; three buttons: **Resume** (`TransitionToPlaying`), **Save & Resume** (saves all chunks via `WorldPersistence.SaveAll` then transitions to Playing), **Quit** (`Close`)
  - **ESC key** re-bound in `OnKeyDown`: `Playing → Paused` (calls `TransitionToPaused`); `Paused → Playing` (calls `TransitionToPlaying`); no-op in `MainMenu` (use the Quit button to exit)
  - **`OnMouseDown` guard** — block interaction (break/place/chisel) is skipped unless `_gameState == Playing`, preventing accidental clicks through the menus
  - **F3 / F / Ctrl+S** key handlers gated to `Playing` state only — debug overlay, creative-mode toggle, and manual save are unavailable from menus
  - **Debug overlay** in `OnRenderFrame` also gated: `_debugVisible && _gameState == Playing`, so `DebugWindow.Draw` is never called from the frozen pause frame
  - World and chunks are fully loaded before the main menu is shown; the frozen world renders as a background behind both menus giving a Minecraft-style "world preview" feel
- **Persistence — Saving/Loading (Phase 14):**
  - **`WorldPersistence`** — new static class; handles all binary I/O for chunk data:
    - `DefaultSavePath` — `%AppData%\VintageVoxel\Saves\default`; created on demand
    - `SaveChunk(folder, key, chunk)` — writes one chunk to `c_{X}_{Z}.bin` using `BinaryWriter`
    - `SaveAll(folder, world)` — iterates all loaded chunks and calls `SaveChunk`; returns the count of files written
    - `TryLoadChunk(folder, key, out chunk)` — reads and validates a chunk file; returns `false` (regenerate fresh) if the file is missing or corrupt; catches all IO/parse exceptions
  - **Binary file format** (`c_{X}_{Z}.bin`): 4-byte magic `"VVCK"` + 1-byte version `1` + chunk XZ int32 pair followed by block RLE and chiseled block RLE sections
  - **RLE compression** — Run-Length Encoding applied to both the flat 32,768-block array and each 4,096 sub-voxel grid; typical terrain chunks compress from ~64 KB of raw IDs to tens of bytes; represented as `(ushort id, ushort runLength)` pairs for blocks and `(byte filled, ushort runLength)` pairs for sub-voxels
  - **`Chunk`** gains three internal members for serialization:
    - `CreateForDeserialization(Vector3i pos)` — static factory that creates a chunk without calling `Generate()`, avoiding wasted terrain computation when loading a saved chunk
    - `GetRawBlockId(int flatIndex)` — flat-array read used by RLE encoder
    - `LoadBlocksFromSave(ushort[] savedIds)` — overwrites the entire `_blocks` array; derives `IsTransparent` from `id == 0`
  - **`ChiseledBlockData`** gains two internal raw accessors `GetRaw(int index)` and `SetRaw(int index, bool)` for the sub-voxel encoder/decoder
  - **`World.ReplaceChunk(key, chunk)`** — internal method that swaps a generated chunk for a loaded one after `Update()` adds the slot
  - **Game integration:**
    - Initial load (`OnLoad`): after `_world.Update()` generates the first ring, each key is checked against the save folder; matching chunks are swapped in before `LightEngine.PropagateSunlight()` runs, so BFS propagates over restored terrain data
    - Streaming load (`OnUpdateFrame`): same swap applied to every newly added chunk before lighting and mesh rebuild, so saves load transparently as the player explores
    - **Ctrl+S** — manual save: calls `WorldPersistence.SaveAll`, updates `_lastSaveStatus` (shown in green in the debug overlay)
    - **Auto-save on exit** — `OnUnload` calls `WorldPersistence.SaveAll` before releasing GPU resources; world state is always preserved on clean close
  - **`DebugWindow.Draw`** gains a `saveStatus` optional parameter; when non-null the last save message is shown in green below the Mode line; hint line updated to include `[Ctrl+S] Save world`
- **Physics & Movement (Phase 10):**
  - `Camera.PhysicsUpdate(world, keyboard, dt)` — unified physics tick called from `Game.OnUpdateFrame`; completely replaces the old `ProcessKeyboard`
  - **Survival mode** (default): gravity (`−25 world units/s²`) accumulates in `Velocity.Y`; horizontal velocity (`5 u/s`) is set directly from WASD input every frame (no sliding momentum); **Space** jumps with an initial upward velocity of `8 u/s`; `Velocity.Y` is clamped at `−60 u/s` terminal velocity
  - **AABB collision** — player body is a `0.6 × 1.8 × 0.6` axis-aligned box; `IsCollidingAt(world, eyePos)` queries `World.GetBlock()` for every integer cell the box overlaps; the region below world Y = 0 is treated as solid bedrock
  - **Per-axis sliding resolution** — movement is resolved independently for X, then Y, then Z; if a collision is detected on one axis the position is reverted and that axis's velocity is zeroed, while the other two axes continue unaffected — this is what gives smooth wall-sliding with no sticking
  - **Ground probe** — after resolving all axes a 5 cm downward sample of `IsCollidingAt` determines `IsOnGround`; jumping is only allowed when `IsOnGround` is true
  - **Creative mode** — `Camera.CreativeMode` (default `false`); when `true` restores unconstrained fly movement with WASD + E/Q vertical; velocity and `IsOnGround` are zeroed; toggled by the **F** key in `Game.OnKeyDown` (velocity reset on switch to prevent launch/halt impulse)
  - `MoveSpeed` raised to `10 u/s` for creative fly (survival walk speed is a separate constant `5 u/s`)
  - `DebugWindow.Draw` gains a `creativeMode` parameter; the dashboard now shows `Mode: Creative / Survival` and the hint line reads `[F] Toggle Creative/Survival`
- **Advanced Lighting: Ambient Occlusion + Flood-Fill (Phase 11):**
  - **Vertex format** extended from 5 → 7 floats per vertex: `x y z u v light ao`; stride increases from 20 → 28 bytes; `Game.UploadChunk()` registers two new vertex attributes: location 2 (light, 1 float at offset 20) and location 3 (AO, 1 float at offset 24)
  - **`LightEngine`** — static class implementing BFS flood-fill lighting:
    - `PropagateSunlight(world)` — full recompute pass; clears all `SunLight` arrays, column-fills sky-lit air voxels to level 15 from the top of each chunk, then runs BFS horizontally through air (level decrements by 1 per step); used at initial world load
    - `ComputeChunk(chunk, world)` — incremental compute for a newly streamed-in chunk; seeds BFS for just that chunk and lets light bleed across chunk seams
    - `UpdateAtBlock(wx, wy, wz, world)` — targeted recompute after a block is placed or broken; clears and reseeds the owning chunk plus the four cardinal XZ neighbours to keep seam accuracy
    - `EmittedBlockLight()` — emission table scaffold (no blocks emit light yet; torch ID 4 → level 14 is ready to uncomment)
  - **`Chunk`** gains two `byte[Volume]` arrays: `SunLight` and `BlockLight`; values are [0, 15]; stored separately (not bit-packed) for zero-overhead mesher access
  - **`World.GetLight(wx, wy, wz)`** — cross-chunk light query returning `max(SunLight, BlockLight) / 15f` as a `float [0,1]`; returns 1.0 for unloaded / out-of-range positions (keeps boundary verts at full-bright until the neighbour arrives)
  - **`ChunkMeshBuilder`** — major update:
    - `AoNeighbors` — static `(int,int,int)[6,4,3]` 3D array encoding the 3 canonical neighbour offsets for each of the 4 vertices of each of the 6 faces; used to count solid neighbours for AO
    - Per-vertex AO: counts solid blocks in the 3 corner positions (2 axial + 1 diagonal); maps 0/1/2/3 solids → 1.0 / 0.8 / 0.6 / 0.4 AO factor
    - Per-vertex light: sampled at the transparent voxel directly adjacent on the face normal side via `SampleLight()` / `World.GetLight()`; defaults to 1.0 at unloaded boundaries
    - `EmitFace()` now accepts `Chunk` and `World?`; `AddV()` takes two extra floats (`light`, `ao`)
  - **`shader.vert`** — two new `in` attributes: `aLight` (location 2) and `aAo` (location 3); passes `vLight` and `vAo` to the fragment stage
  - **`shader.frag`** — computes `lighting = max(0.05, vLight * vAo)` (5% minimum ambient so caves are never pitch-black); multiplies final texture colour by `lighting`; `uNoTexture = 2` activates a new **Lighting Debug** mode rendering `lighting` as greyscale
  - **`DebugWindow`** — new `LightingDebug` bool property; new **"Lighting Debug (AO+Light)"** checkbox in the Toggles section; `Game.cs` maps the toggle to `uNoTexture = 2`
  - **`Game.cs`** integration: `LightEngine.PropagateSunlight(_world)` called once after initial chunks are loaded; `LightEngine.ComputeChunk()` called for each arriving chunk during streaming; `LightEngine.UpdateAtBlock()` called before `RebuildAffectedChunks()` on every left-click break and right-click place
- **Asset Editor Architecture (Phase E1):**
  - **Solution restructured into three projects** inside `VintageVoxel.sln`:
    - **`EngineCore` (Class Library)** — all shared engine infrastructure: `Shader`, `Texture`, `TextureAtlas`, `BlockRegistry`, `Frustum`, `NoiseGenerator`, `ImGuiController`, `Camera`, `Block`, `ChiseledBlockData`, `Chunk`, `ChunkMeshBuilder`, `World`, `WorldPersistence`, `LightEngine`, `Raycaster`; NuGet: OpenTK 4.9.3 + ImGui.NET 1.89.7.1
    - **`VintageVoxel` / GameClient (Executable)** — game-specific application code: `Game`, `Program`, `GameState`, `DebugWindow`, `ChunkBorderRenderer`, `HUDRenderer`, `Inventory`, `Item`, `ItemStack`; references `EngineCore`
    - **`AssetEditor` (Executable)** — skeleton asset editor window; references `EngineCore`
  - **`World.ReplaceChunk`** visibility raised from `internal` → `public` so GameClient can hot-swap loaded chunks across assembly boundaries
  - **`EditorWindow`** (`AssetEditor/EditorWindow.cs`) — Phase E1 complete: 1280×720 window, dark background, `ImGuiController` from EngineCore; three-panel ImGui layout (200-px left toolbar, 220-px right properties panel, centre 3D viewport label)
  - **`OrbitCamera`** (`EngineCore/OrbitCamera.cs`) — spherical orbit camera; stores `Azimuth`, `Elevation`, `Radius` in spherical coordinates around a world-space `Target` (default = origin); produces `GetViewMatrix()` / `GetProjectionMatrix()`; `Orbit(dAzimuth, dElevation)` and `Zoom(delta)` helpers called from mouse events; `UpdateAspect(float)` called on window resize
  - **3D viewport** — `EditorWindow` renders a live 3D scene before the ImGui pass: orbit camera driven by RMB-drag (azimuth + elevation) and scroll-wheel (zoom); `editor.vert` / `editor.frag` GLSL pair (AssetEditor/Shaders/) — minimal position-only vertex shader + per-draw `uColor` flat-colour fragment shader; XYZ axis lines (X=red, Y=green, Z=blue, 2.5 units each) + flat reference grid on XZ plane (−8 to +8, 1-unit spacing, dim grey); left toolbar shows orbit-camera control hints
  - **JSON export format** (`EngineCore/VoxelModel.cs`) — `VoxelModel` (name, type, gridSize, voxels[]) + `VoxelEntry` (x, y, z, color hex); `System.Text.Json` serialisation; matches the `asset_editor.md` §2 spec exactly; shared by both the Editor and the GameClient
  - **`AssetExporter`** (`AssetEditor/AssetExporter.cs`) — `Export(model, dir)` saves formatted JSON to any folder; `ExportToSharedData(model)` convenience overload writes to `SharedData/Models/` beside the solution root; Export button + Model Name field wired into the right properties panel
  - **`ModelLoader`** (`EngineCore/ModelLoader.cs`) — `Load(path)` deserialises a `.json` file into `VoxelModel` (case-insensitive); `TryLoad(path, out model)` non-throwing variant; ready to use from the GameClient to ingest exported assets

---

## Project Structure

```
VintageVoxel/                    ← Solution root
├── VintageVoxel.sln             # Three-project solution
├── .vscode/
│   ├── launch.json              # F5 run/debug configurations
│   └── tasks.json               # Default build task (Ctrl+Shift+B)
│
├── EngineCore/                  ← Shared Class Library (referenced by GameClient + AssetEditor)
│   ├── EngineCore.csproj        # Class Library; OpenTK 4.9.3 + ImGui.NET
│   ├── Shader.cs                # Compiles GLSL, links program, uniform setters
│   ├── Texture.cs               # GL texture wrapper — uploads RGBA bytes, nearest filter
│   ├── TextureAtlas.cs          # Procedural 48x16 atlas (Dirt / Stone / Grass tiles)
│   ├── BlockRegistry.cs         # Block ID → per-face atlas tile index lookup
│   ├── Frustum.cs               # Gribb-Hartmann frustum extraction; AABB vs. frustum test
│   ├── NoiseGenerator.cs        # Classic Perlin noise (permutation table) + Octave() fBm helper
│   ├── ImGuiController.cs       # OpenTK 4 ImGui backend — font-atlas GPU upload, inline shader, dynamic VBO, input relay
│   ├── Camera.cs                # FPS fly-camera + AABB physics; view/projection matrices
│   ├── Block.cs                 # Block struct — Id, IsTransparent, Air sentinel
│   ├── ChiseledBlockData.cs     # 16×16×16 boolean sub-voxel grid; SourceBlockId; Get/Set/InBounds/HasAnyFilled helpers
│   ├── Chunk.cs                 # 32×32×32 flat array; Generate() Perlin terrain; SunLight/BlockLight arrays
│   ├── ChunkMeshBuilder.cs      # Face-culling mesher → ChunkMesh; handles chiseled sub-voxels + AO + light
│   ├── World.cs                 # Chunk dictionary (Vector2i → Chunk); Update() streams load/unload; GetBlock/GetLight cross-chunk queries
│   ├── WorldPersistence.cs      # Binary save/load: per-chunk .bin files with RLE compression
│   ├── LightEngine.cs           # BFS flood-fill lighting: PropagateSunlight, ComputeChunk, UpdateAtBlock
│   ├── Raycaster.cs             # DDA voxel raycast — Cast() + CastSubVoxel() for chiseled blocks
│   ├── OrbitCamera.cs           # Spherical orbit camera (azimuth/elevation/radius); Orbit() + Zoom() helpers
│   ├── VoxelModel.cs            # JSON data model: VoxelModel + VoxelEntry; matches asset_editor.md §2 spec
│   └── ModelLoader.cs           # Deserialises .json → VoxelModel; Load() + TryLoad()
│
├── VintageVoxel/                ← GameClient Executable (references EngineCore)
│   ├── VintageVoxel.csproj      # Exe; references EngineCore.csproj
│   ├── Program.cs               # Entry point — configures and runs the Game window
│   ├── Game.cs                  # GameWindow subclass — game loop, VAO/VBO/EBO, render
│   ├── GameState.cs             # GameState enum: MainMenu, Playing, Paused, Exiting
│   ├── DebugWindow.cs           # ImGui overlay: FPS/pos/chunk metrics + mode + toggles
│   ├── ChunkBorderRenderer.cs   # GL_LINES AABB wireframe per chunk (line.vert/frag)
│   ├── HUDRenderer.cs           # 2-D orthographic HUD: crosshair + hotbar slots + item icons
│   ├── Item.cs                  # Item class: Id, Name, MaxStackSize, TextureId; Dirt/Stone/Grass singletons
│   ├── ItemStack.cs             # ItemStack struct: Item? + Count; IsEmpty; Empty sentinel
│   ├── Inventory.cs             # 10-slot hotbar inventory; ScrollHotbar(); AddItem/RemoveItem/HasItem; HeldStack ref
│   └── Shaders/
│       ├── shader.vert          # Vertex shader — MVP transform + passes UV, light, AO to fragment stage
│       ├── shader.frag          # Fragment shader — atlas sample × (light × AO); uNoTexture=2 for AO+light greyscale debug
│       ├── line.vert            # Minimal vertex shader for chunk border lines (position only)
│       ├── line.frag            # Solid-colour fragment shader for debug lines (uColor uniform)
│       ├── hud.vert             # 2-D HUD vertex shader — orthographic pixel-space projection
│       └── hud.frag             # 2-D HUD fragment shader — flat uColor or atlas tile × uColor
│
└── AssetEditor/                 ← Asset Editor Executable (references EngineCore)
    ├── AssetEditor.csproj       # Exe; references EngineCore.csproj
    ├── Program.cs               # Entry point — launches EditorWindow
    ├── EditorWindow.cs          # GameWindow subclass — Phase E1 complete: orbit camera, 3D axes+grid, ImGui layout, Export button
    ├── AssetExporter.cs         # Serialises VoxelModel to JSON; ExportToSharedData() writes to SharedData/Models/
    └── Shaders/
        ├── editor.vert          # Position-only vertex shader (view + projection matrices)
        └── editor.frag          # Flat-colour fragment shader (uColor vec3 uniform)
```
```

---

## Notes
- GLSL files must be **ASCII only** — NVIDIA's driver rejects non-ASCII bytes even inside comments.
- Shader files are copied to the output directory via `<None Update="Shaders\**"><CopyToOutputDirectory>PreserveNewest` in the `.csproj`.
- Back-face culling uses CCW winding order (OpenGL default).
- `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>` is enabled in the `.csproj` — required by `ImGuiController` to dereference the `ImDrawList**` native pointer (`data.CmdLists` returns `nint` in ImGui.NET 1.89.7.1).
- **F3** toggles the ImGui debug overlay; cursor must be in Normal mode for checkbox interaction so F3 also toggles `CursorState` between `Normal` and `Grabbed`.
