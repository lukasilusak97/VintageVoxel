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

    public ChunkMesh(float[] vertices, uint[] indices)
    {
        Vertices = vertices;
        Indices = indices;
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

    // Opposite face index for each of the 6 faces (used for slope culling queries).
    private static int OppositeFace(int face) => face ^ 1;

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

        // Reusable buffers for per-face slope lighting (hoisted to avoid CA2014).
        Span<float> slopeSun = stackalloc float[6];
        Span<float> slopeBlk = stackalloc float[6];

        for (int z = 0; z < Chunk.Size; z++)
            for (int y = 0; y < Chunk.Size; y++)
                for (int x = 0; x < Chunk.Size; x++)
                {
                    ref Block block = ref chunk.GetBlock(x, y, z);
                    if (block.Id == 0 || BlockRegistry.HasModel(block.Id))
                        continue;

                    if (block.Id == Block.ChiseledId)
                    {
                        int cidx = Chunk.Index(x, y, z);
                        if (chunk.ChiseledBlocks.TryGetValue(cidx, out var chiseled))
                            EmitChiseledBlock(verts, indices, x, y, z, chiseled, chunk, world);
                        continue;
                    }

                    // --- Slope blocks: emit their triangular prism geometry ---
                    if (block.IsSlope)
                    {
                        var slopeShape = (SlopeShape)block.Shape;
                        int tileIdx = BlockRegistry.TileForFace(block.Id, 0 /*top*/);
                        float su0 = tileIdx * TextureAtlas.TileUvWidth;
                        float su1 = (tileIdx + 1) * TextureAtlas.TileUvWidth;
                        // For slopes at terrain surface, side-face neighbors are solid
                        // terrain with sunLight = 0. Use the sky-light from above for all
                        // faces and apply per-face directional scale for shading depth.
                        // Block light (torches) is taken as the max across all 6 neighbors.
                        GetLightAt(x, y + 1, z, chunk, world, out float skyLight, out _);
                        float maxBlockLight = 0f;
                        for (int face = 0; face < 6; face++)
                        {
                            var (fdx, fdy, fdz) = NeighbourOffsets[face];
                            GetLightAt(x + fdx, y + fdy, z + fdz, chunk, world,
                                out _, out float bv);
                            if (bv > maxBlockLight) maxBlockLight = bv;
                        }
                        for (int face = 0; face < 6; face++)
                        {
                            slopeSun[face] = skyLight * FaceSunScale[face];
                            slopeBlk[face] = maxBlockLight;
                        }
                        FaceEmitter.EmitSlopeFaces(verts, indices, slopeShape,
                            x, y, z, su0, su1, slopeSun, slopeBlk, 1.0f);
                        continue;
                    }

                    for (int face = 0; face < 6; face++)
                    {
                        var (dx, dy, dz) = NeighbourOffsets[face];
                        int nx = x + dx, ny = y + dy, nz = z + dz;

                        bool exposed;
                        if (Chunk.InBounds(nx, ny, nz))
                        {
                            Block nb = chunk.GetBlock(nx, ny, nz);
                            // A cube face is hidden when the neighbour fully covers it:
                            // either a solid cube neighbour, or a slope whose matching
                            // face is fully solid (flush with the boundary).
                            bool neighbourCovers = nb.IsSlope
                                ? SlopeGeometry.IsFaceSolid((SlopeShape)nb.Shape, OppositeFace(face))
                                : !nb.IsTransparent;
                            exposed = block.IsTransparent ? nb.Id == 0 : !neighbourCovers;
                        }
                        else if (world != null)
                        {
                            int worldX = chunk.Position.X * Chunk.Size + nx;
                            int worldY = chunk.Position.Y * Chunk.Size + ny;
                            int worldZ = chunk.Position.Z * Chunk.Size + nz;
                            Block nb = world.GetBlock(worldX, worldY, worldZ);
                            bool neighbourCovers = nb.IsSlope
                                ? SlopeGeometry.IsFaceSolid((SlopeShape)nb.Shape, OppositeFace(face))
                                : !nb.IsTransparent;
                            exposed = block.IsTransparent ? nb.Id == 0 : !neighbourCovers;
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
    /// for one quad.
    ///
    /// AO is computed per-vertex from the three surrounding blocks at each corner.
    /// Both light channels are smoothly interpolated per-vertex by averaging the
    /// four voxels that share each corner (face-adjacent + 3 AO neighbors).
    /// The sunLight value is then scaled by the per-face directional factor.
    /// </summary>
    private static void EmitFace(
        List<float> verts, List<uint> indices,
        int face, int bx, int by, int bz, ushort blockId,
        Chunk chunk, World? world)
    {
        uint baseIdx = (uint)(verts.Count / 8);

        float x0 = bx, x1 = bx + 1f;
        float y0 = by, y1 = by + 1f;
        float z0 = bz, z1 = bz + 1f;

        int tileIdx = BlockRegistry.TileForFace(blockId, face);
        float u0 = tileIdx * TextureAtlas.TileUvWidth;
        float u1 = (tileIdx + 1) * TextureAtlas.TileUvWidth;

        float dirScale = FaceSunScale[face];

        float[] ao = new float[4];
        float[] sunLight = new float[4];
        float[] blkLight = new float[4];

        for (int v = 0; v < 4; v++)
        {
            // --- Ambient occlusion ---
            int solidCount = 0;
            for (int k = 0; k < 3; k++)
            {
                var (ox, oy, oz) = AoNeighbors[face, v, k];
                if (IsSolid(bx + ox, by + oy, bz + oz, chunk, world))
                    solidCount++;
            }
            ao[v] = solidCount switch { 0 => 1.0f, 1 => 0.8f, 2 => 0.6f, _ => 0.4f };

            // --- Smooth per-vertex lighting: average face-adjacent + 3 AO neighbors ---
            // The AO neighbor offsets already include the face normal component, so they
            // land on the same layer of voxels as the face-adjacent sample.
            SampleSmoothLight(bx, by, bz, face, v, chunk, world, out float sv, out float bv);

            sunLight[v] = sv * dirScale;
            blkLight[v] = bv;
        }

        // Flip the quad diagonal when AO values create a concave pattern,
        // matching Minecraft's smooth-lighting quad flipping for consistent gradients.
        bool flip = (ao[0] + ao[2] < ao[1] + ao[3]);

        switch (face)
        {
            case 0: // Top (+Y)
                AddV(verts, x0, y1, z0, u0, 0f, sunLight[0], blkLight[0], ao[0]);
                AddV(verts, x0, y1, z1, u0, 1f, sunLight[1], blkLight[1], ao[1]);
                AddV(verts, x1, y1, z1, u1, 1f, sunLight[2], blkLight[2], ao[2]);
                AddV(verts, x1, y1, z0, u1, 0f, sunLight[3], blkLight[3], ao[3]);
                break;
            case 1: // Bottom (-Y)
                AddV(verts, x0, y0, z0, u0, 0f, sunLight[0], blkLight[0], ao[0]);
                AddV(verts, x1, y0, z0, u1, 0f, sunLight[1], blkLight[1], ao[1]);
                AddV(verts, x1, y0, z1, u1, 1f, sunLight[2], blkLight[2], ao[2]);
                AddV(verts, x0, y0, z1, u0, 1f, sunLight[3], blkLight[3], ao[3]);
                break;
            case 2: // North (-Z)
                AddV(verts, x0, y0, z0, u0, 1f, sunLight[0], blkLight[0], ao[0]);
                AddV(verts, x0, y1, z0, u0, 0f, sunLight[1], blkLight[1], ao[1]);
                AddV(verts, x1, y1, z0, u1, 0f, sunLight[2], blkLight[2], ao[2]);
                AddV(verts, x1, y0, z0, u1, 1f, sunLight[3], blkLight[3], ao[3]);
                break;
            case 3: // South (+Z)
                AddV(verts, x1, y0, z1, u0, 1f, sunLight[0], blkLight[0], ao[0]);
                AddV(verts, x1, y1, z1, u0, 0f, sunLight[1], blkLight[1], ao[1]);
                AddV(verts, x0, y1, z1, u1, 0f, sunLight[2], blkLight[2], ao[2]);
                AddV(verts, x0, y0, z1, u1, 1f, sunLight[3], blkLight[3], ao[3]);
                break;
            case 4: // West (-X)
                AddV(verts, x0, y0, z1, u0, 1f, sunLight[0], blkLight[0], ao[0]);
                AddV(verts, x0, y1, z1, u0, 0f, sunLight[1], blkLight[1], ao[1]);
                AddV(verts, x0, y1, z0, u1, 0f, sunLight[2], blkLight[2], ao[2]);
                AddV(verts, x0, y0, z0, u1, 1f, sunLight[3], blkLight[3], ao[3]);
                break;
            case 5: // East (+X)
                AddV(verts, x1, y0, z0, u0, 1f, sunLight[0], blkLight[0], ao[0]);
                AddV(verts, x1, y1, z0, u0, 0f, sunLight[1], blkLight[1], ao[1]);
                AddV(verts, x1, y1, z1, u1, 0f, sunLight[2], blkLight[2], ao[2]);
                AddV(verts, x1, y0, z1, u1, 1f, sunLight[3], blkLight[3], ao[3]);
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

    private static float SampleLight(int lx, int ly, int lz, Chunk chunk, World? world)
    {
        if (Chunk.InBounds(lx, ly, lz))
        {
            int idx = Chunk.Index(lx, ly, lz);
            byte sun = chunk.SunLight[idx];
            byte blk = chunk.BlockLight[idx];
            return Math.Max(sun, blk) / 15f;
        }
        if (world != null)
        {
            int wx = chunk.Position.X * Chunk.Size + lx;
            int wy = chunk.Position.Y * Chunk.Size + ly;
            int wz = chunk.Position.Z * Chunk.Size + lz;
            return world.GetLight(wx, wy, wz);
        }
        return 1.0f;
    }

    private static bool IsSolid(int lx, int ly, int lz, Chunk chunk, World? world)
    {
        if (Chunk.InBounds(lx, ly, lz))
        {
            Block b = chunk.GetBlock(lx, ly, lz);
            return !b.IsTransparent && !b.IsSlope;
        }
        if (world != null)
        {
            int wx = chunk.Position.X * Chunk.Size + lx;
            int wy = chunk.Position.Y * Chunk.Size + ly;
            int wz = chunk.Position.Z * Chunk.Size + lz;
            Block b = world.GetBlock(wx, wy, wz);
            return !b.IsTransparent && !b.IsSlope;
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
                                int wwy = chunk.Position.Y * Chunk.Size + aby;
                                int wwz = chunk.Position.Z * Chunk.Size + abz;
                                exposed = world.GetBlock(wwx, wwy, wwz).IsTransparent;
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
    /// Full-bright (sunLight=1, blockLight=0, ao=1) — sub-voxel granularity makes
    /// per-vertex BFS impractical; the parent block's light tints the result.
    /// </summary>
    private static void EmitSubFace(
        List<float> verts, List<uint> indices,
        int face, float ox, float oy, float oz, float size, ushort blockId)
    {
        uint baseIdx = (uint)(verts.Count / 8);
        float x0 = ox, x1 = ox + size;
        float y0 = oy, y1 = oy + size;
        float z0 = oz, z1 = oz + size;

        int tileIdx = BlockRegistry.TileForFace(blockId, face);
        float u0 = tileIdx * TextureAtlas.TileUvWidth;
        float u1 = (tileIdx + 1) * TextureAtlas.TileUvWidth;

        float dir = FaceSunScale[face];

        switch (face)
        {
            case 0: // Top (+Y)
                AddV(verts, x0, y1, z0, u0, 0f, dir, 0f, 1f);
                AddV(verts, x0, y1, z1, u0, 1f, dir, 0f, 1f);
                AddV(verts, x1, y1, z1, u1, 1f, dir, 0f, 1f);
                AddV(verts, x1, y1, z0, u1, 0f, dir, 0f, 1f);
                break;
            case 1: // Bottom (-Y)
                AddV(verts, x0, y0, z0, u0, 0f, dir, 0f, 1f);
                AddV(verts, x1, y0, z0, u1, 0f, dir, 0f, 1f);
                AddV(verts, x1, y0, z1, u1, 1f, dir, 0f, 1f);
                AddV(verts, x0, y0, z1, u0, 1f, dir, 0f, 1f);
                break;
            case 2: // North (-Z)
                AddV(verts, x0, y0, z0, u0, 1f, dir, 0f, 1f);
                AddV(verts, x0, y1, z0, u0, 0f, dir, 0f, 1f);
                AddV(verts, x1, y1, z0, u1, 0f, dir, 0f, 1f);
                AddV(verts, x1, y0, z0, u1, 1f, dir, 0f, 1f);
                break;
            case 3: // South (+Z)
                AddV(verts, x1, y0, z1, u0, 1f, dir, 0f, 1f);
                AddV(verts, x1, y1, z1, u0, 0f, dir, 0f, 1f);
                AddV(verts, x0, y1, z1, u1, 0f, dir, 0f, 1f);
                AddV(verts, x0, y0, z1, u1, 1f, dir, 0f, 1f);
                break;
            case 4: // West (-X)
                AddV(verts, x0, y0, z1, u0, 1f, dir, 0f, 1f);
                AddV(verts, x0, y1, z1, u0, 0f, dir, 0f, 1f);
                AddV(verts, x0, y1, z0, u1, 0f, dir, 0f, 1f);
                AddV(verts, x0, y0, z0, u1, 1f, dir, 0f, 1f);
                break;
            case 5: // East (+X)
                AddV(verts, x1, y0, z0, u0, 1f, dir, 0f, 1f);
                AddV(verts, x1, y1, z0, u0, 0f, dir, 0f, 1f);
                AddV(verts, x1, y1, z1, u1, 0f, dir, 0f, 1f);
                AddV(verts, x1, y0, z1, u1, 1f, dir, 0f, 1f);
                break;
        }

        indices.Add(baseIdx); indices.Add(baseIdx + 1); indices.Add(baseIdx + 2);
        indices.Add(baseIdx); indices.Add(baseIdx + 2); indices.Add(baseIdx + 3);
    }
}
