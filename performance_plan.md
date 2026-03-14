# GPU Offloading Performance Plan

Goal: Move the three heaviest CPU-bound systems to compute shaders to eliminate
frame-rate bottlenecks and allow faster chunk streaming.

Current bottlenecks (from profiling):
- `MaxMeshRebuildsPerFrame = 2` — meshing is too slow to do more
- `MaxChunksPerFrame = 1` — lighting BFS limits streaming throughput
- `MaxBfsNodesPerFrame = 20,000` — light propagation is amortized across frames
- Terrain noise: ~28,672 Perlin evaluations per chunk (28 samples x 1024 columns)

---

## 1. Chunk Meshing Compute Shader

**Priority:** Highest — single biggest performance bottleneck.

### Current CPU Pipeline
1. `ChunkMeshBuilder.Build()` iterates 32x32x32 blocks
2. Per block: 6 neighbour checks for face culling
3. Per exposed face: 4 vertices x (3 AO neighbour checks + 4 light samples)
4. Output: `List<float>` vertices + `List<uint>` indices
5. `WorldRenderer.RebuildChunk()` uploads via `GL.BufferData`

### Target GPU Pipeline
1. Upload chunk block data (32x32x32 ushort IDs + metadata) as SSBO
2. Upload 6 neighbour chunk border slices (32x32 each) as SSBO
3. Upload light arrays (SunLight + BlockLight) as SSBO
4. Compute shader Phase 1 — **Vote**: one thread per block, writes a 1-bit
   "has visible face on side N" flag into a compact buffer
5. Compute shader Phase 2 — **Prefix Sum**: parallel scan over the vote buffer
   to compute output offsets (where each block writes its vertices)
6. Compute shader Phase 3 — **Emit**: each thread with visible faces writes
   vertices + indices at its computed offset into output SSBO
7. Render directly from the output SSBO (no CPU readback, no `GL.BufferData`)

### Data Layout
```
Input SSBO 0 — Block IDs:       uint16[32768]  (64 KB)
Input SSBO 1 — Block metadata:   uint8[32768]   (32 KB) — layer, transparency, water
Input SSBO 2 — SunLight:         uint8[32768]   (32 KB)
Input SSBO 3 — BlockLight:       uint8[32768]   (32 KB)
Input SSBO 4 — Neighbour borders: uint16[6][32][32] (12 KB)
Input SSBO 5 — Block registry:   tile indices, transparency flags per block ID

Output SSBO 0 — Vertices:  float[8] per vertex (pos, uv, sun, blk, ao)
Output SSBO 1 — Indices:   uint32
Output SSBO 2 — Draw args:  indirect draw command (vertex count, instance count, etc.)
```

### Shader Workgroup Layout
- Workgroup size: 4x4x4 = 64 threads (fits one warp/wavefront)
- Dispatch: 8x8x8 = 512 workgroups per chunk
- Shared memory: 6x6x6 block cache per workgroup (neighbours included)

### AO + Smooth Lighting in GLSL
- Port the `AoNeighbors[6,4,3]` lookup table as a constant array
- Port `FaceSunScale[6]` as a constant array
- Each thread samples 4 corner lights from shared memory (no global reads)

### Implementation Steps
1. Create `Shaders/chunk_mesh.comp` with the block data structures
2. Create `ComputeMesher.cs` — manages SSBOs, dispatches, indirect draw
3. Modify `WorldRenderer.RebuildChunk()` to use compute path
4. Add GPU fence/sync so mesh is ready before the draw call
5. Remove CPU `ChunkMeshBuilder` fallback once validated
6. Increase `MaxMeshRebuildsPerFrame` (no longer CPU-bound)

### Risks
- Prefix sum adds complexity; use a well-known parallel scan pattern
- Water faces need separate output buffers (transparent pass)
- Cross-chunk neighbours require uploading border slices
- Partial-layer blocks (topOffset < 1.0) need special vertex positions

---

## 2. Light Engine Compute Shader

**Priority:** High — unlocks faster chunk streaming.

### Current CPU Pipeline
1. `LightEngine.SeedSunlightChunk()` — scans top-down per column, seeds queue
2. `LightEngine.SeedBlockLightChunk()` — finds emitters, seeds queue
3. `LightEngine.BfsPropagate()` — BFS flood-fill, decays by 1 per step
4. Special rule: sunlight propagates downward without decay at level 15
5. Amortized via `ContinueStreamBfs()` with 20K node budget per frame

### Target GPU Pipeline — Iterative Relaxation
BFS maps poorly to GPU (inherently serial wavefront expansion). Instead, use
**iterative parallel relaxation** (similar to jump flooding):

1. Upload block data (opacity) as 3D texture or SSBO
2. Upload emitter positions + sky-open columns as seed texture
3. **Init pass**: Set sky-open voxels to 15 (sun), emitters to 14 (block)
4. **Relaxation loop** (run N iterations, N = max light level = 15):
   - Each thread reads its 6 neighbours' light values
   - Computes `max(neighbour - 1)` for each channel
   - Special case: if sun channel, neighbour above at 15 -> keep 15 (no decay down)
   - Writes result if greater than current value
   - Ping-pong between two 3D textures (read from A, write to B, swap)
5. After 15 iterations, light is fully converged
6. Read back into chunk arrays (or keep on GPU if mesher is also GPU-side)

### Data Layout
```
3D Texture A — Light data: RGBA8 (R=sunlight, G=blocklight, B=unused, A=opacity)
3D Texture B — Ping-pong target: same format
Uniform — iteration index, chunk world offset
```

### Workgroup Layout
- Workgroup: 8x8x4 = 256 threads
- Dispatch: 4x4x8 = 128 workgroups per chunk (32^3 / 256)
- Shared memory: 10x10x6 tile with 1-voxel halo for neighbour reads

### Cross-Chunk Light Bleeding
- After a chunk's local light converges, border voxels are compared with
  loaded neighbour chunks
- If a border voxel would receive higher light from a neighbour, re-run
  relaxation with updated seeds (typically 1-2 extra iterations)
- This replaces the current `FlushDirty()` mechanism

### Implementation Steps
1. Create `Shaders/light_propagate.comp` with the relaxation kernel
2. Create `Shaders/light_seed.comp` for sky + emitter seeding
3. Create `GpuLightEngine.cs` — manages 3D textures, dispatches iterations
4. Modify `WorldStreamer` to use GPU light path
5. Remove `MaxBfsNodesPerFrame` budget (GPU converges in one dispatch batch)
6. Increase `MaxChunksPerFrame` since lighting is no longer the bottleneck

### Risks
- 15 iterations over 32^3 voxels is 15 dispatches per chunk — may need to
  batch multiple chunks into one large 3D texture to amortize dispatch overhead
- Cross-chunk bleeding requires careful synchronization
- Sunlight "no decay downward" rule needs vertical column state — may require
  a separate top-down seeding pass before the relaxation loop
- Read-back latency if mesher is still CPU-side (solved if meshing is also GPU)

---

## 3. Terrain Noise Compute Shader

**Priority:** Medium — easiest to implement, moderate performance gain.

### Current CPU Pipeline
1. `Chunk.Generate()` runs per column (32x32 = 1024 columns per chunk)
2. Per column: `ComputeSurfaceHeight()` calls `NoiseGenerator.Octave()` with:
   - Continentalness: 4 octaves
   - Continent shape: 6 octaves
   - Detail hills: 4 octaves
   - Erosion: 3 octaves
   - Ridge (conditional): 5 octaves
   - Biome temp: 3 octaves
   - Biome humidity: 3 octaves
3. ~28 noise samples per column = ~28,672 Perlin evaluations per chunk
4. Result: heightmap + biome map, then column fill logic places blocks

### Target GPU Pipeline
1. Upload permutation table (512 ints) as SSBO (one-time, shared)
2. Dispatch 32x32 threads (one per column)
3. Each thread computes full `ComputeSurfaceHeight()` + `ComputeBiome()`
4. Output: two buffers
   - `float[1024]` heightmap
   - `uint[1024]` biome map
5. CPU reads back heightmap + biome, runs block fill logic

### Shader Structure
```glsl
// Shaders/terrain_noise.comp
layout(local_size_x = 8, local_size_y = 8) in;  // 64 threads

// Permutation table
layout(std430, binding = 0) readonly buffer PermTable { int perm[512]; };

// Output
layout(std430, binding = 1) writeonly buffer HeightMap { float heights[1024]; };
layout(std430, binding = 2) writeonly buffer BiomeMap  { uint biomes[1024]; };

// Uniforms
uniform ivec2 uChunkXZ;  // chunk position in chunk coords

// Port of NoiseGenerator.Sample2D
float perlinSample2D(float x, float y) { ... }

// Port of NoiseGenerator.Octave
float perlinOctave(float x, float y, int octaves, float lac, float pers) { ... }

// Port of Chunk.ComputeSurfaceHeight
float computeSurfaceHeight(float wx, float wz) { ... }

// Port of Chunk.ComputeBiome
uint computeBiome(float wx, float wz) { ... }
```

### Implementation Steps
1. Create `Shaders/terrain_noise.comp` — port Perlin + height + biome logic
2. Create `GpuTerrainGenerator.cs` — manages SSBOs, dispatch, readback
3. Modify `Chunk` constructor to accept pre-computed heightmap + biome arrays
4. Add `Chunk.GenerateFromHeightmap(float[], int[])` method
5. Modify `World.Update()` to dispatch GPU noise, read back, pass to Chunk
6. Remove CPU noise path from `Chunk.Generate()` once validated

### Settlement Flattening
- `SettlementMap.GetFlatteningFactor()` and `GetSettlementTargetHeight()` are
  also per-column and could run in the same shader
- If settlement logic is too complex to port, run it as a CPU post-pass on
  the GPU heightmap (only modifies a few columns, very cheap)

### Risks
- Minimal. Perlin noise is a textbook compute shader workload
- Read-back adds one frame of latency (acceptable during streaming)
- Block fill logic (ores, trees, caves) stays CPU-side — it's column-serial
  and has complex branching that doesn't benefit heavily from GPU

---

## Execution Order

### Phase 1 — Terrain Noise (easiest, builds GPU infrastructure)
- Create compute shader dispatch infrastructure (`GpuTerrainGenerator.cs`)
- Port Perlin noise to GLSL
- Validate heightmap matches CPU output
- This phase establishes the SSBO/dispatch/readback patterns reused later

### Phase 2 — Chunk Meshing (highest impact)
- Requires Phase 1 infrastructure (SSBO management, compute dispatch)
- Eliminates the main frame-rate limiter
- Enables increasing `MaxMeshRebuildsPerFrame` significantly
- Pairs with indirect rendering for zero-copy draw

### Phase 3 — Light Engine (unlocks full streaming speed)
- Requires confidence with 3D texture dispatches from Phase 2
- Removes `MaxBfsNodesPerFrame` budget entirely
- Combined with Phase 2, allows `MaxChunksPerFrame` to increase to 4-8+
- If both meshing and lighting are GPU-side, the data never leaves VRAM

### Phase 4 — Integration + Cleanup
- Remove CPU fallback code paths
- Tune workgroup sizes based on GPU profiling
- Add LOD meshing (distant chunks use simplified compute kernel)
- Profile and balance frame budget across the three compute passes

---

## Prerequisites

- OpenGL 4.3+ (compute shaders) — already using 4.5 core profile
- `GL.DispatchCompute`, `GL.MemoryBarrier`, `GL.DrawArraysIndirect`
- SSBO support (guaranteed in GL 4.3+)
- GPU timer queries (`GL.BeginQuery(QueryTarget.TimeElapsed)`) for profiling

## Expected Gains

| Metric | Before | After (estimated) |
|---|---|---|
| MaxMeshRebuildsPerFrame | 2 | 16+ |
| MaxChunksPerFrame | 1 | 4-8 |
| MaxBfsNodesPerFrame | 20,000 | unlimited (converges in 1 frame) |
| Noise per chunk | ~3ms CPU | <0.1ms GPU |
| Full chunk stream (load+light+mesh) | ~4-5 frames | ~1-2 frames |
