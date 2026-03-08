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

---

## Project Structure

```
VintageVoxel/
├── .vscode/
│   ├── launch.json          # F5 run/debug configurations
│   └── tasks.json           # Default build task (Ctrl+Shift+B)
├── VintageVoxel/
│   ├── VintageVoxel.csproj  # .NET 8 project, OpenTK 4.9.3 reference
│   ├── Program.cs           # Entry point — configures and runs the Game window
│   ├── Game.cs              # GameWindow subclass — game loop, VAO/VBO/EBO, render
│   ├── Shader.cs            # Compiles GLSL, links program, uniform setters
│   ├── Camera.cs            # FPS fly-camera — view/projection matrices, input
│   ├── Block.cs             # Block struct — Id, IsTransparent, Air sentinel
│   ├── Chunk.cs             # 32x32x32 flat array, Generate() fills bottom 16 layers
│   ├── ChunkMeshBuilder.cs  # Face-culling mesher → ChunkMesh (float[] verts xyz+uv, uint[] indices); accepts optional World for cross-chunk seam culling
│   ├── NoiseGenerator.cs    # Classic Perlin noise (permutation table) + Octave() fBm helper → terrain heights
│   ├── World.cs             # Chunk dictionary (Vector2i → Chunk); Update() streams load/unload around player; GetBlock() for cross-chunk queries
│   ├── Texture.cs           # GL texture wrapper — uploads RGBA bytes, nearest filter
│   ├── TextureAtlas.cs      # Procedural 48x16 atlas (Dirt / Stone / Grass tiles)
│   ├── BlockRegistry.cs     # Block ID → per-face atlas tile index lookup
│   ├── Raycaster.cs         # DDA voxel raycast — Cast() returns hit block + face normal
│   └── Shaders/
│       ├── shader.vert      # Vertex shader — MVP transform + passes UV to fragment stage
│       └── shader.frag      # Fragment shader — samples uTexture atlas at vTexCoord
└── roadmap.md               # Full 8-phase build plan
```

---

## Notes
- GLSL files must be **ASCII only** — NVIDIA's driver rejects non-ASCII bytes even inside comments.
- Shader files are copied to the output directory via `<None Update="Shaders\**"><CopyToOutputDirectory>PreserveNewest` in the `.csproj`.
- Back-face culling uses CCW winding order (OpenGL default).
