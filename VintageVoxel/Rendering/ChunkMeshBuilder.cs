using System.Collections.Generic;

namespace VintageVoxel;

/// <summary>
/// Result of meshing a single chunk — vertex and index arrays ready to upload to the GPU.
///
/// Vertex layout (8 floats per vertex, tightly packed):
///   float x, y, z       — world-space position (local chunk coordinates)
///   float u, v           — texture atlas UV coordinates
///   float sunLight       — sky-light level [0,1], attenuated by face direction
///   float blockLight     — emitter-light level [0,1] (torches etc.)
///   float ao             — ambient occlusion factor [0.4, 1.0]
///
/// Separating sunLight from blockLight lets the fragment shader apply different
/// color temperatures: a cool white for sunlight and a warm orange for torches.
/// </summary>
public readonly struct ChunkMesh
{
    public readonly float[] Vertices; // 8 floats per vertex: x,y,z,u,v,sunLight,blockLight,ao
    public readonly uint[] Indices;
    public readonly float[] TransparentVertices;
    public readonly uint[] TransparentIndices;

    public ChunkMesh(float[] vertices, uint[] indices,
                     float[] transparentVertices, uint[] transparentIndices)
    {
        Vertices = vertices;
        Indices = indices;
        TransparentVertices = transparentVertices;
        TransparentIndices = transparentIndices;
    }
}

/// <summary>
/// Converts a Chunk's block data into a textured triangle mesh using
/// neighbour-based face culling, Ambient Occlusion, and smooth per-vertex lighting.
///
/// FACE-CULLING RULE:
///   A face is emitted ONLY when the neighbour on that side is transparent.
///   Hidden interior faces are never generated.
///
/// VERTEX FORMAT:  x  y  z  u  v  sunLight  blockLight  ao  (8 floats, stride = 32 bytes)
///   position (xyz)   — local block coordinate corner
///   texcoord (uv)    — UV into the texture atlas tile for this block/face
///   sunLight         — smooth sky-light [0,1] averaged from 4 corner voxels,
///                      then attenuated by a per-face directional factor
///   blockLight       — smooth emitter-light [0,1] averaged from 4 corner voxels
///   ao               — ambient occlusion factor per vertex corner [0.4, 1.0]
///
/// SMOOTH LIGHTING:
///   For each vertex corner of a quad, light is averaged across the four voxels
///   that share that corner (face-adjacent + 3 AO neighbor positions), matching
///   the smooth-lighting algorithm used in Minecraft Java Edition.
///
/// DIRECTIONAL SHADING:
///   The sunLight value is multiplied by a per-face scalar (1.0 top, 0.5 bottom,
///   0.8 N/S, 0.65 E/W) before being stored.  This gives chunks a natural sense
///   of depth and form even without a dynamic sun direction.
///
/// WINDING ORDER:
///   CCW from outside, consistent with OpenGL's default front-face convention.
/// </summary>
public static class ChunkMeshBuilder
{
    private static readonly (int dx, int dy, int dz)[] NeighbourOffsets =
    {
        ( 0, +1,  0), // 0 Top    (+Y)
        ( 0, -1,  0), // 1 Bottom (-Y)
        ( 0,  0, -1), // 2 North  (-Z)
        ( 0,  0, +1), // 3 South  (+Z)
        (-1,  0,  0), // 4 West   (-X)
        (+1,  0,  0), // 5 East   (+X)
    };

    /// <summary>
    /// Ambient-occlusion corner offsets for each face.
    ///
    /// Indexed as [face, vertex, neighborIndex] — a true 3D array.
    /// For each face we have 4 vertices. Each vertex sits at a corner of the face
    /// quad; the corner is surrounded by up to 3 other blocks (2 edge-sharing + 1
    /// diagonal). We count how many of those 3 positions are solid to compute AO.
    ///
    /// Each triple is (dx, dy, dz) relative to the block being meshed.
    /// </summary>
    private static readonly (int dx, int dy, int dz)[,,] AoNeighbors = new (int, int, int)[6, 4, 3]
    {
        // face 0 Top (+Y):  v0=(x0,y1,z0)  v1=(x0,y1,z1)  v2=(x1,y1,z1)  v3=(x1,y1,z0)
        {
            { (-1,+1, 0), ( 0,+1,-1), (-1,+1,-1) }, // v0: west, north, NW
            { (-1,+1, 0), ( 0,+1,+1), (-1,+1,+1) }, // v1: west, south, SW
            { (+1,+1, 0), ( 0,+1,+1), (+1,+1,+1) }, // v2: east, south, SE
            { (+1,+1, 0), ( 0,+1,-1), (+1,+1,-1) }, // v3: east, north, NE
        },
        // face 1 Bottom (-Y):  v0=(x0,y0,z0)  v1=(x1,y0,z0)  v2=(x1,y0,z1)  v3=(x0,y0,z1)
        {
            { (-1,-1, 0), ( 0,-1,-1), (-1,-1,-1) }, // v0
            { (+1,-1, 0), ( 0,-1,-1), (+1,-1,-1) }, // v1
            { (+1,-1, 0), ( 0,-1,+1), (+1,-1,+1) }, // v2
            { (-1,-1, 0), ( 0,-1,+1), (-1,-1,+1) }, // v3
        },
        // face 2 North (-Z):  v0=(x0,y0,z0)  v1=(x0,y1,z0)  v2=(x1,y1,z0)  v3=(x1,y0,z0)
        {
            { (-1, 0,-1), ( 0,-1,-1), (-1,-1,-1) }, // v0: west, down, W-down
            { (-1, 0,-1), ( 0,+1,-1), (-1,+1,-1) }, // v1: west, up,   W-up
            { (+1, 0,-1), ( 0,+1,-1), (+1,+1,-1) }, // v2: east, up,   E-up
            { (+1, 0,-1), ( 0,-1,-1), (+1,-1,-1) }, // v3: east, down, E-down
        },
        // face 3 South (+Z):  v0=(x1,y0,z1)  v1=(x1,y1,z1)  v2=(x0,y1,z1)  v3=(x0,y0,z1)
        {
            { (+1, 0,+1), ( 0,-1,+1), (+1,-1,+1) }, // v0
            { (+1, 0,+1), ( 0,+1,+1), (+1,+1,+1) }, // v1
            { (-1, 0,+1), ( 0,+1,+1), (-1,+1,+1) }, // v2
            { (-1, 0,+1), ( 0,-1,+1), (-1,-1,+1) }, // v3
        },
        // face 4 West (-X):  v0=(x0,y0,z1)  v1=(x0,y1,z1)  v2=(x0,y1,z0)  v3=(x0,y0,z0)
        {
            { (-1, 0,+1), (-1,-1, 0), (-1,-1,+1) }, // v0
            { (-1, 0,+1), (-1,+1, 0), (-1,+1,+1) }, // v1
            { (-1, 0,-1), (-1,+1, 0), (-1,+1,-1) }, // v2
            { (-1, 0,-1), (-1,-1, 0), (-1,-1,-1) }, // v3
        },
        // face 5 East (+X):  v0=(x1,y0,z0)  v1=(x1,y1,z0)  v2=(x1,y1,z1)  v3=(x1,y0,z1)
        {
            { (+1, 0,-1), (+1,-1, 0), (+1,-1,-1) }, // v0
            { (+1, 0,-1), (+1,+1, 0), (+1,+1,-1) }, // v1
            { (+1, 0,+1), (+1,+1, 0), (+1,+1,+1) }, // v2
            { (+1, 0,+1), (+1,-1, 0), (+1,-1,+1) }, // v3
        },
    };

    /// <summary>
    /// Builds a mesh for <paramref name="chunk"/>.
    ///
    /// <paramref name="world"/> is optional.  When supplied, boundary faces are
    /// checked against the neighbouring chunk so seam-faces are properly culled.
    /// </summary>
    public static ChunkMesh Build(Chunk chunk, World? world = null)
    {
        // 4 verts × 8 floats + 6 indices per face.
        var verts = new List<float>(4096 * 32);
        var indices = new List<uint>(4096 * 6);
        var transVerts = new List<float>(512 * 32);
        var transIndices = new List<uint>(512 * 6);

        for (int z = 0; z < Chunk.Size; z++)
            for (int y = 0; y < Chunk.Size; y++)
                for (int x = 0; x < Chunk.Size; x++)
                {
                    ref Block block = ref chunk.GetBlock(x, y, z);
                    if (block.IsEmpty && !block.HasWater)
                        continue;
                    if (block.Id != 0 && BlockRegistry.HasModel(block.Id))
                        continue;

                    bool hasTerrain = block.Id != 0;
                    bool hasWater = block.HasWater;

                    // --- Emit terrain faces ---
                    if (hasTerrain)
                    {
                        bool partial = block.IsPartial;
                        for (int face = 0; face < 6; face++)
                        {
                            var (dx, dy, dz) = NeighbourOffsets[face];
                            int nx = x + dx, ny = y + dy, nz = z + dz;
                            Block nb = GetNeighbour(nx, ny, nz, chunk, world);

                            bool exposed;
                            if (partial)
                            {
                                if (face == 0)
                                    exposed = true;
                                else if (face == 1)
                                    exposed = block.IsTransparent ? nb.Id == 0 && !nb.HasWater : nb.IsTransparent;
                                else
                                    exposed = block.IsTransparent ? nb.Id == 0 && !nb.HasWater
                                        : (nb.IsTransparent || nb.IsPartial);
                            }
                            else
                            {
                                if (block.IsTransparent)
                                    exposed = nb.Id == 0 && !nb.HasWater;
                                else
                                    exposed = nb.IsTransparent || nb.IsPartial;
                            }

                            if (exposed)
                                EmitFace(verts, indices, face, x, y, z, block, chunk, world);
                        }
                    }

                    // --- Emit water overlay faces ---
                    if (hasWater)
                    {
                        // Build a virtual water block for geometry emission.
                        // Water top is at WaterLevel/16; water bottom is at terrain TopOffset (or 0 if no terrain).
                        float waterTop = block.WaterTopOffset;
                        float waterBot = hasTerrain ? block.TopOffset : 0f;

                        var waterBlock = new Block
                        {
                            Id = 15,
                            IsTransparent = true,
                            Layer = block.WaterLevel,
                        };

                        for (int face = 0; face < 6; face++)
                        {
                            var (dx, dy, dz) = NeighbourOffsets[face];
                            int nx = x + dx, ny = y + dy, nz = z + dz;
                            Block nb = GetNeighbour(nx, ny, nz, chunk, world);

                            bool exposed;
                            if (face == 0) // top
                            {
                                // Show water top if block above has no water.
                                exposed = !nb.HasWater;
                            }
                            else if (face == 1) // bottom
                            {
                                // Hide bottom if this cell has terrain (terrain covers the bottom).
                                // Show bottom if no terrain below and neighbour below has no water.
                                exposed = !hasTerrain && !nb.HasWater && (nb.IsTransparent || nb.IsPartial);
                            }
                            else // sides
                            {
                                // Show side if neighbour has no water or has lower water.
                                exposed = !nb.HasWater;
                            }

                            if (exposed)
                                EmitFace(transVerts, transIndices, face, x, y, z, waterBlock, chunk, world);
                        }
                    }
                }

        return new ChunkMesh(verts.ToArray(), indices.ToArray(),
                             transVerts.ToArray(), transIndices.ToArray());
    }

    /// <summary>Returns the block at the given local coordinate, crossing chunk boundaries via world.</summary>
    private static Block GetNeighbour(int lx, int ly, int lz, Chunk chunk, World? world)
    {
        if (Chunk.InBounds(lx, ly, lz))
            return chunk.GetBlock(lx, ly, lz);
        if (world != null)
        {
            int wx = chunk.Position.X * Chunk.Size + lx;
            int wy = chunk.Position.Y * Chunk.Size + ly;
            int wz = chunk.Position.Z * Chunk.Size + lz;
            return world.GetBlock(wx, wy, wz);
        }
        return Block.Air;
    }

    // -----------------------------------------------------------------------
    // Per-face directional sun-light scale
    // -----------------------------------------------------------------------

    /// <summary>
    /// Multiplier applied to the sunLight channel for each face to simulate
    /// directional sunlight from above.  Block-light is unaffected.
    /// </summary>
    private static readonly float[] FaceSunScale =
    {
        1.00f, // Top    (+Y) — faces the sun directly
        0.50f, // Bottom (-Y) — fully shaded underneath
        0.80f, // North  (-Z)
        0.80f, // South  (+Z)
        0.65f, // West   (-X)
        0.65f, // East   (+X)
    };

    // -----------------------------------------------------------------------
    // Face emission
    // -----------------------------------------------------------------------

    /// <summary>
    /// Appends 4 vertices (each: x y z u v sunLight blockLight ao) and 6 indices
    /// for one quad. For partial-layer blocks the top height is adjusted to
    /// by + layer/16 instead of by + 1.
    /// </summary>
    private static void EmitFace(
        List<float> verts, List<uint> indices,
        int face, int bx, int by, int bz, Block block,
        Chunk chunk, World? world)
    {
        uint baseIdx = (uint)(verts.Count / 8);

        float topOffset = block.TopOffset; // 1.0 for full, < 1.0 for partial
        float x0 = bx, x1 = bx + 1f;
        float y0 = by, y1 = by + topOffset;
        float z0 = bz, z1 = bz + 1f;

        int tileIdx = BlockRegistry.TileForFace(block.Id, face);
        float u0 = tileIdx * TextureAtlas.TileUvWidth;
        float u1 = (tileIdx + 1) * TextureAtlas.TileUvWidth;

        // For side faces of partial blocks, compress the V coordinate to
        // show only the bottom portion of the texture matching the layer height.
        float vTop = face >= 2 ? 1f - topOffset : 0f;

        float dirScale = FaceSunScale[face];

        // Use stack variables instead of heap-allocated arrays.
        float ao0, ao1, ao2, ao3;
        float sun0, sun1, sun2, sun3;
        float blk0, blk1, blk2, blk3;

        ComputeVertexLighting(bx, by, bz, face, 0, dirScale, chunk, world, out ao0, out sun0, out blk0);
        ComputeVertexLighting(bx, by, bz, face, 1, dirScale, chunk, world, out ao1, out sun1, out blk1);
        ComputeVertexLighting(bx, by, bz, face, 2, dirScale, chunk, world, out ao2, out sun2, out blk2);
        ComputeVertexLighting(bx, by, bz, face, 3, dirScale, chunk, world, out ao3, out sun3, out blk3);

        bool flip = (ao0 + ao2 < ao1 + ao3);

        switch (face)
        {
            case 0: // Top (+Y)
                AddV(verts, x0, y1, z0, u0, 0f, sun0, blk0, ao0);
                AddV(verts, x0, y1, z1, u0, 1f, sun1, blk1, ao1);
                AddV(verts, x1, y1, z1, u1, 1f, sun2, blk2, ao2);
                AddV(verts, x1, y1, z0, u1, 0f, sun3, blk3, ao3);
                break;
            case 1: // Bottom (-Y)
                AddV(verts, x0, y0, z0, u0, 0f, sun0, blk0, ao0);
                AddV(verts, x1, y0, z0, u1, 0f, sun1, blk1, ao1);
                AddV(verts, x1, y0, z1, u1, 1f, sun2, blk2, ao2);
                AddV(verts, x0, y0, z1, u0, 1f, sun3, blk3, ao3);
                break;
            case 2: // North (-Z)
                AddV(verts, x0, y0, z0, u0, 1f, sun0, blk0, ao0);
                AddV(verts, x0, y1, z0, u0, vTop, sun1, blk1, ao1);
                AddV(verts, x1, y1, z0, u1, vTop, sun2, blk2, ao2);
                AddV(verts, x1, y0, z0, u1, 1f, sun3, blk3, ao3);
                break;
            case 3: // South (+Z)
                AddV(verts, x1, y0, z1, u0, 1f, sun0, blk0, ao0);
                AddV(verts, x1, y1, z1, u0, vTop, sun1, blk1, ao1);
                AddV(verts, x0, y1, z1, u1, vTop, sun2, blk2, ao2);
                AddV(verts, x0, y0, z1, u1, 1f, sun3, blk3, ao3);
                break;
            case 4: // West (-X)
                AddV(verts, x0, y0, z1, u0, 1f, sun0, blk0, ao0);
                AddV(verts, x0, y1, z1, u0, vTop, sun1, blk1, ao1);
                AddV(verts, x0, y1, z0, u1, vTop, sun2, blk2, ao2);
                AddV(verts, x0, y0, z0, u1, 1f, sun3, blk3, ao3);
                break;
            case 5: // East (+X)
                AddV(verts, x1, y0, z0, u0, 1f, sun0, blk0, ao0);
                AddV(verts, x1, y1, z0, u0, vTop, sun1, blk1, ao1);
                AddV(verts, x1, y1, z1, u1, vTop, sun2, blk2, ao2);
                AddV(verts, x1, y0, z1, u1, 1f, sun3, blk3, ao3);
                break;
        }

        if (flip)
        {
            // Triangle 1: 0,1,3  Triangle 2: 1,2,3
            indices.Add(baseIdx); indices.Add(baseIdx + 1); indices.Add(baseIdx + 3);
            indices.Add(baseIdx + 1); indices.Add(baseIdx + 2); indices.Add(baseIdx + 3);
        }
        else
        {
            // Triangle 1: 0,1,2  Triangle 2: 0,2,3
            indices.Add(baseIdx); indices.Add(baseIdx + 1); indices.Add(baseIdx + 2);
            indices.Add(baseIdx); indices.Add(baseIdx + 2); indices.Add(baseIdx + 3);
        }
    }

    // -----------------------------------------------------------------------
    // Smooth lighting helper
    // -----------------------------------------------------------------------

    /// <summary>
    /// Computes AO, sunlight and blocklight for a single vertex of a face in one pass,
    /// avoiding per-vertex heap allocations.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static void ComputeVertexLighting(
        int bx, int by, int bz, int face, int vertex, float dirScale,
        Chunk chunk, World? world,
        out float ao, out float sun, out float blk)
    {
        int solidCount = 0;
        for (int k = 0; k < 3; k++)
        {
            var (ox, oy, oz) = AoNeighbors[face, vertex, k];
            if (IsSolid(bx + ox, by + oy, bz + oz, chunk, world))
                solidCount++;
        }
        ao = solidCount switch { 0 => 1.0f, 1 => 0.8f, 2 => 0.6f, _ => 0.4f };

        SampleSmoothLight(bx, by, bz, face, vertex, chunk, world, out float sv, out float bv);
        sun = sv * dirScale;
        blk = bv;
    }

    /// <summary>
    /// Computes smooth sunLight and blockLight for a single vertex corner by
    /// averaging the four voxels that share that corner:
    ///   1. The face-adjacent voxel (directly across the face normal).
    ///   2. The three AO-neighbor voxels for this vertex (same layer).
    ///
    /// Voxels that are solid contribute 0 to the average, naturally darkening
    /// corners that are enclosed by geometry (similar to AO darkening).
    /// </summary>
    private static void SampleSmoothLight(
        int bx, int by, int bz, int face, int vertex,
        Chunk chunk, World? world,
        out float sunOut, out float blockOut)
    {
        var (fdx, fdy, fdz) = NeighbourOffsets[face];

        float sumSun = 0f, sumBlk = 0f;
        int count = 0;

        // Sample 1: face-adjacent voxel.
        GetLightAt(bx + fdx, by + fdy, bz + fdz, chunk, world, out float s, out float b);
        sumSun += s; sumBlk += b; count++;

        // Samples 2-4: the three AO neighbors (they already include the face offset).
        for (int k = 0; k < 3; k++)
        {
            var (ox, oy, oz) = AoNeighbors[face, vertex, k];
            GetLightAt(bx + ox, by + oy, bz + oz, chunk, world, out s, out b);
            sumSun += s; sumBlk += b; count++;
        }

        sunOut = sumSun / count;
        blockOut = sumBlk / count;
    }

    // -----------------------------------------------------------------------
    // Light & AO helpers
    // -----------------------------------------------------------------------

    private static void GetLightAt(int lx, int ly, int lz, Chunk chunk, World? world,
                                    out float sun, out float block)
    {
        if (Chunk.InBounds(lx, ly, lz))
        {
            int idx = Chunk.Index(lx, ly, lz);
            sun = chunk.SunLight[idx] / 15f;
            block = chunk.BlockLight[idx] / 15f;
            return;
        }
        if (world != null)
        {
            int wx = chunk.Position.X * Chunk.Size + lx;
            int wy = chunk.Position.Y * Chunk.Size + ly;
            int wz = chunk.Position.Z * Chunk.Size + lz;
            (sun, block) = world.GetSunAndBlockLight(wx, wy, wz);
            return;
        }
        // Unloaded boundary: full sun, no block light.
        sun = 1.0f; block = 0f;
    }

    private static bool IsSolid(int lx, int ly, int lz, Chunk chunk, World? world)
    {
        if (Chunk.InBounds(lx, ly, lz))
        {
            Block b = chunk.GetBlock(lx, ly, lz);
            return !b.IsTransparent && b.IsFullBlock;
        }
        if (world != null)
        {
            int wx = chunk.Position.X * Chunk.Size + lx;
            int wy = chunk.Position.Y * Chunk.Size + ly;
            int wz = chunk.Position.Z * Chunk.Size + lz;
            Block b = world.GetBlock(wx, wy, wz);
            return !b.IsTransparent && b.IsFullBlock;
        }
        return false;
    }

    private static void AddV(List<float> v,
        float x, float y, float z, float u, float vt,
        float sunLight, float blockLight, float ao)
    {
        v.Add(x); v.Add(y); v.Add(z); v.Add(u); v.Add(vt);
        v.Add(sunLight); v.Add(blockLight); v.Add(ao);
    }
}
