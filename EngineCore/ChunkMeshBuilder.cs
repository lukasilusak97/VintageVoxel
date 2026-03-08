using System.Collections.Generic;

namespace VintageVoxel;

/// <summary>
/// Result of meshing a single chunk — vertex and index arrays ready to upload to the GPU.
///
/// Vertex layout (7 floats per vertex, tightly packed):
///   float x, y, z   — world-space position (in local chunk coordinates)
///   float u, v       — texture atlas UV coordinates
///   float light      — combined light level [0, 1] (max of sun + block light)
///   float ao         — ambient occlusion factor [0.4, 1.0]
/// </summary>
public readonly struct ChunkMesh
{
    public readonly float[] Vertices; // 7 floats per vertex: x,y,z,u,v,light,ao
    public readonly uint[] Indices;

    public ChunkMesh(float[] vertices, uint[] indices)
    {
        Vertices = vertices;
        Indices = indices;
    }
}

/// <summary>
/// Converts a Chunk's block data into a textured triangle mesh using
/// neighbour-based face culling, Ambient Occlusion, and light levels.
///
/// FACE-CULLING RULE:
///   A face is emitted ONLY when the neighbour on that side is transparent.
///   Hidden interior faces are never generated and never reach the GPU.
///
/// VERTEX FORMAT:  x  y  z  u  v  light  ao  (7 floats, stride = 28 bytes)
///   position (xyz) — local block coordinate corner
///   texcoord (uv)  — UV into the texture atlas tile for this block/face
///   light          — combined light level [0,1] sampled near the vertex corner
///   ao             — ambient occlusion factor per vertex corner [0.4, 1.0]
///
/// WINDING ORDER:
///   CCW from outside for every face, consistent with OpenGL's default
///   front-face convention so GPU back-face culling works correctly.
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
    /// checked against the neighbouring chunk in the world so that interior faces
    /// at chunk seams are properly culled.  Without a world reference every
    /// out-of-bounds face is treated as exposed (original single-chunk behaviour).
    /// </summary>
    public static ChunkMesh Build(Chunk chunk, World? world = null)
    {
        // 4 verts × 7 floats + 6 indices per face.  Preallocate a generous upper bound
        // to avoid repeated List resizes during the inner loop.
        var verts = new List<float>(4096 * 28);
        var indices = new List<uint>(4096 * 6);

        for (int z = 0; z < Chunk.Size; z++)
            for (int y = 0; y < Chunk.Size; y++)
                for (int x = 0; x < Chunk.Size; x++)
                {
                    ref Block block = ref chunk.GetBlock(x, y, z);
                    if (block.IsTransparent)
                        continue;

                    // Phase 13: chiseled blocks are meshed at sub-voxel granularity.
                    if (block.Id == Block.ChiseledId)
                    {
                        int cidx = Chunk.Index(x, y, z);
                        if (chunk.ChiseledBlocks.TryGetValue(cidx, out var chiseled))
                            EmitChiseledBlock(verts, indices, x, y, z, chiseled, chunk, world);
                        continue; // skip normal full-block face emission
                    }

                    for (int face = 0; face < 6; face++)
                    {
                        var (dx, dy, dz) = NeighbourOffsets[face];
                        int nx = x + dx, ny = y + dy, nz = z + dz;

                        bool exposed;
                        if (Chunk.InBounds(nx, ny, nz))
                        {
                            exposed = chunk.GetBlock(nx, ny, nz).IsTransparent;
                        }
                        else if (world != null)
                        {
                            int worldX = chunk.Position.X * Chunk.Size + nx;
                            int worldY = chunk.Position.Y * Chunk.Size + ny;
                            int worldZ = chunk.Position.Z * Chunk.Size + nz;
                            exposed = world.GetBlock(worldX, worldY, worldZ).IsTransparent;
                        }
                        else
                        {
                            exposed = true;
                        }

                        if (exposed)
                            EmitFace(verts, indices, face, x, y, z, block.Id, chunk, world);
                    }
                }

        return new ChunkMesh(verts.ToArray(), indices.ToArray());
    }

    // -----------------------------------------------------------------------
    // Face emission
    // -----------------------------------------------------------------------

    /// <summary>
    /// Appends 4 vertices (each: x y z u v light ao) and 6 indices for one quad.
    ///
    /// AO is computed per-vertex from the three surrounding blocks at each corner.
    /// Light is sampled from the SunLight/BlockLight arrays at the face-adjacent
    /// transparent voxel for each corner to give a smooth interpolated appearance.
    /// </summary>
    private static void EmitFace(
        List<float> verts, List<uint> indices,
        int face, int bx, int by, int bz, ushort blockId,
        Chunk chunk, World? world)
    {
        uint baseIdx = (uint)(verts.Count / 7);

        float x0 = bx, x1 = bx + 1f;
        float y0 = by, y1 = by + 1f;
        float z0 = bz, z1 = bz + 1f;

        int tileIdx = BlockRegistry.TileForFace(blockId, face);
        float u0 = tileIdx * TextureAtlas.TileUvWidth;
        float u1 = (tileIdx + 1) * TextureAtlas.TileUvWidth;

        // Compute AO and Light for each of the 4 vertices.
        float[] ao = new float[4];
        float[] light = new float[4];

        // The "face normal" neighbour — the voxel directly adjacent on the open side.
        var (fdx, fdy, fdz) = NeighbourOffsets[face];

        for (int v = 0; v < 4; v++)
        {
            int solidCount = 0;
            for (int k = 0; k < 3; k++)
            {
                var (ox, oy, oz) = AoNeighbors[face, v, k];
                if (IsSolid(bx + ox, by + oy, bz + oz, chunk, world))
                    solidCount++;
            }
            ao[v] = solidCount switch { 0 => 1.0f, 1 => 0.8f, 2 => 0.6f, _ => 0.4f };

            // Sample light at the transparent voxel on the open face side,
            // offset toward the vertex corner so corner brightness is averaged.
            // For simplicity we sample the block directly adjacent (face normal).
            light[v] = SampleLight(bx + fdx, by + fdy, bz + fdz, chunk, world);
        }

        // Emit vertices per face in the same CCW winding as before.
        switch (face)
        {
            case 0: // Top (+Y)
                AddV(verts, x0, y1, z0, u0, 0f, light[0], ao[0]);
                AddV(verts, x0, y1, z1, u0, 1f, light[1], ao[1]);
                AddV(verts, x1, y1, z1, u1, 1f, light[2], ao[2]);
                AddV(verts, x1, y1, z0, u1, 0f, light[3], ao[3]);
                break;
            case 1: // Bottom (-Y)
                AddV(verts, x0, y0, z0, u0, 0f, light[0], ao[0]);
                AddV(verts, x1, y0, z0, u1, 0f, light[1], ao[1]);
                AddV(verts, x1, y0, z1, u1, 1f, light[2], ao[2]);
                AddV(verts, x0, y0, z1, u0, 1f, light[3], ao[3]);
                break;
            case 2: // North (-Z)
                AddV(verts, x0, y0, z0, u0, 0f, light[0], ao[0]);
                AddV(verts, x0, y1, z0, u0, 1f, light[1], ao[1]);
                AddV(verts, x1, y1, z0, u1, 1f, light[2], ao[2]);
                AddV(verts, x1, y0, z0, u1, 0f, light[3], ao[3]);
                break;
            case 3: // South (+Z)
                AddV(verts, x1, y0, z1, u0, 0f, light[0], ao[0]);
                AddV(verts, x1, y1, z1, u0, 1f, light[1], ao[1]);
                AddV(verts, x0, y1, z1, u1, 1f, light[2], ao[2]);
                AddV(verts, x0, y0, z1, u1, 0f, light[3], ao[3]);
                break;
            case 4: // West (-X)
                AddV(verts, x0, y0, z1, u0, 0f, light[0], ao[0]);
                AddV(verts, x0, y1, z1, u0, 1f, light[1], ao[1]);
                AddV(verts, x0, y1, z0, u1, 1f, light[2], ao[2]);
                AddV(verts, x0, y0, z0, u1, 0f, light[3], ao[3]);
                break;
            case 5: // East (+X)
                AddV(verts, x1, y0, z0, u0, 0f, light[0], ao[0]);
                AddV(verts, x1, y1, z0, u0, 1f, light[1], ao[1]);
                AddV(verts, x1, y1, z1, u1, 1f, light[2], ao[2]);
                AddV(verts, x1, y0, z1, u1, 0f, light[3], ao[3]);
                break;
        }

        indices.Add(baseIdx); indices.Add(baseIdx + 1); indices.Add(baseIdx + 2);
        indices.Add(baseIdx); indices.Add(baseIdx + 2); indices.Add(baseIdx + 3);
    }

    // -----------------------------------------------------------------------
    // Light & AO helpers
    // -----------------------------------------------------------------------

    private static float SampleLight(int lx, int ly, int lz, Chunk chunk, World? world)
    {
        if (Chunk.InBounds(lx, ly, lz))
        {
            int idx = Chunk.Index(lx, ly, lz);
            byte sun = chunk.SunLight[idx];
            byte block = chunk.BlockLight[idx];
            return Math.Max(sun, block) / 15f;
        }
        else if (world != null)
        {
            int wx = chunk.Position.X * Chunk.Size + lx;
            int wz = chunk.Position.Z * Chunk.Size + lz;
            return world.GetLight(wx, ly, wz);
        }
        // Default to full bright at chunk boundaries toward unloaded chunks.
        return 1.0f;
    }

    private static bool IsSolid(int lx, int ly, int lz, Chunk chunk, World? world)
    {
        if (Chunk.InBounds(lx, ly, lz))
            return !chunk.GetBlock(lx, ly, lz).IsTransparent;

        if (world != null)
        {
            int wx = chunk.Position.X * Chunk.Size + lx;
            int wz = chunk.Position.Z * Chunk.Size + lz;
            return !world.GetBlock(wx, ly, wz).IsTransparent;
        }
        return false;
    }

    private static void AddV(List<float> v,
        float x, float y, float z, float u, float vt, float light, float ao)
    {
        v.Add(x); v.Add(y); v.Add(z); v.Add(u); v.Add(vt); v.Add(light); v.Add(ao);
    }

    // -----------------------------------------------------------------------
    // Phase 13: Chiseled (micro-block) helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// Emits sub-voxel geometry for a chiseled block at local chunk position
    /// (<paramref name="bx"/>, <paramref name="by"/>, <paramref name="bz"/>).
    ///
    /// Each sub-voxel occupies 1/SubSize (0.0625) of a world unit.  Face culling
    /// is applied between sub-voxels first; at the outer boundary of the chiseled
    /// block the adjacent full block is tested just like normal face culling.
    /// </summary>
    private static void EmitChiseledBlock(
        List<float> verts, List<uint> indices,
        int bx, int by, int bz, ChiseledBlockData chiseled,
        Chunk chunk, World? world)
    {
        const float sub = 1f / ChiseledBlockData.SubSize;

        for (int sz = 0; sz < ChiseledBlockData.SubSize; sz++)
            for (int sy = 0; sy < ChiseledBlockData.SubSize; sy++)
                for (int sx = 0; sx < ChiseledBlockData.SubSize; sx++)
                {
                    if (!chiseled.Get(sx, sy, sz)) continue;

                    for (int face = 0; face < 6; face++)
                    {
                        var (dx, dy, dz) = NeighbourOffsets[face];
                        int nx = sx + dx, ny = sy + dy, nz = sz + dz;

                        bool exposed;
                        if (ChiseledBlockData.InBounds(nx, ny, nz))
                        {
                            // Neighbour is inside the same chiseled block.
                            exposed = !chiseled.Get(nx, ny, nz);
                        }
                        else
                        {
                            // Neighbour is across the outer boundary of the chiseled block.
                            // Test the adjacent full block the same way the normal mesher does.
                            int abx = bx + dx, aby = by + dy, abz = bz + dz;
                            if (Chunk.InBounds(abx, aby, abz))
                                exposed = chunk.GetBlock(abx, aby, abz).IsTransparent;
                            else if (world != null)
                            {
                                int wwx = chunk.Position.X * Chunk.Size + abx;
                                int wwz = chunk.Position.Z * Chunk.Size + abz;
                                exposed = world.GetBlock(wwx, aby, wwz).IsTransparent;
                            }
                            else
                                exposed = true;
                        }

                        if (exposed)
                            EmitSubFace(verts, indices, face,
                                bx + sx * sub, by + sy * sub, bz + sz * sub,
                                sub, chiseled.SourceBlockId);
                    }
                }
    }

    /// <summary>
    /// Appends 4 vertices and 6 indices for a single sub-voxel face.
    /// Uses the same CCW winding as <see cref="EmitFace"/>.
    /// Light and AO are set to 1.0 (full-bright) — sub-voxel granularity makes
    /// per-vertex BFS impractical; a future pass could sample the parent block.
    /// </summary>
    private static void EmitSubFace(
        List<float> verts, List<uint> indices,
        int face, float ox, float oy, float oz, float size, ushort blockId)
    {
        uint baseIdx = (uint)(verts.Count / 7);
        float x0 = ox, x1 = ox + size;
        float y0 = oy, y1 = oy + size;
        float z0 = oz, z1 = oz + size;

        int tileIdx = BlockRegistry.TileForFace(blockId, face);
        float u0 = tileIdx * TextureAtlas.TileUvWidth;
        float u1 = (tileIdx + 1) * TextureAtlas.TileUvWidth;

        switch (face)
        {
            case 0: // Top (+Y)
                AddV(verts, x0, y1, z0, u0, 0f, 1f, 1f);
                AddV(verts, x0, y1, z1, u0, 1f, 1f, 1f);
                AddV(verts, x1, y1, z1, u1, 1f, 1f, 1f);
                AddV(verts, x1, y1, z0, u1, 0f, 1f, 1f);
                break;
            case 1: // Bottom (-Y)
                AddV(verts, x0, y0, z0, u0, 0f, 1f, 1f);
                AddV(verts, x1, y0, z0, u1, 0f, 1f, 1f);
                AddV(verts, x1, y0, z1, u1, 1f, 1f, 1f);
                AddV(verts, x0, y0, z1, u0, 1f, 1f, 1f);
                break;
            case 2: // North (-Z)
                AddV(verts, x0, y0, z0, u0, 0f, 1f, 1f);
                AddV(verts, x0, y1, z0, u0, 1f, 1f, 1f);
                AddV(verts, x1, y1, z0, u1, 1f, 1f, 1f);
                AddV(verts, x1, y0, z0, u1, 0f, 1f, 1f);
                break;
            case 3: // South (+Z)
                AddV(verts, x1, y0, z1, u0, 0f, 1f, 1f);
                AddV(verts, x1, y1, z1, u0, 1f, 1f, 1f);
                AddV(verts, x0, y1, z1, u1, 1f, 1f, 1f);
                AddV(verts, x0, y0, z1, u1, 0f, 1f, 1f);
                break;
            case 4: // West (-X)
                AddV(verts, x0, y0, z1, u0, 0f, 1f, 1f);
                AddV(verts, x0, y1, z1, u0, 1f, 1f, 1f);
                AddV(verts, x0, y1, z0, u1, 1f, 1f, 1f);
                AddV(verts, x0, y0, z0, u1, 0f, 1f, 1f);
                break;
            case 5: // East (+X)
                AddV(verts, x1, y0, z0, u0, 0f, 1f, 1f);
                AddV(verts, x1, y1, z0, u0, 1f, 1f, 1f);
                AddV(verts, x1, y1, z1, u1, 1f, 1f, 1f);
                AddV(verts, x1, y0, z1, u1, 0f, 1f, 1f);
                break;
        }

        indices.Add(baseIdx); indices.Add(baseIdx + 1); indices.Add(baseIdx + 2);
        indices.Add(baseIdx); indices.Add(baseIdx + 2); indices.Add(baseIdx + 3);
    }
}
