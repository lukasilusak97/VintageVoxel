using System.Collections.Generic;

namespace VintageVoxel;

/// <summary>
/// Result of meshing a single chunk — vertex and index arrays ready to upload to the GPU.
///
/// Vertex layout (5 floats per vertex, tightly packed):
///   float x, y, z   — world-space position (in local chunk coordinates)
///   float u, v       — texture atlas UV coordinates
/// </summary>
public readonly struct ChunkMesh
{
    public readonly float[] Vertices; // 5 floats per vertex: x,y,z,u,v
    public readonly uint[] Indices;

    public ChunkMesh(float[] vertices, uint[] indices)
    {
        Vertices = vertices;
        Indices = indices;
    }
}

/// <summary>
/// Converts a Chunk's block data into a textured triangle mesh using
/// neighbour-based face culling.
///
/// FACE-CULLING RULE:
///   A face is emitted ONLY when the neighbour on that side is transparent.
///   Hidden interior faces are never generated and never reach the GPU.
///
/// VERTEX FORMAT:  x  y  z  u  v  (5 floats, stride = 20 bytes)
///   position (xyz) — local block coordinate corner
///   texcoord (uv)  — UV into the texture atlas tile for this block/face
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
    /// Builds a mesh for <paramref name="chunk"/>.
    ///
    /// <paramref name="world"/> is optional.  When supplied, boundary faces are
    /// checked against the neighbouring chunk in the world so that interior faces
    /// at chunk seams are properly culled.  Without a world reference every
    /// out-of-bounds face is treated as exposed (original single-chunk behaviour).
    /// </summary>
    public static ChunkMesh Build(Chunk chunk, World? world = null)
    {
        // 4 verts × 5 floats + 6 indices per face.  Preallocate a generous upper bound
        // to avoid repeated List resizes during the inner loop.
        var verts = new List<float>(4096 * 20);
        var indices = new List<uint>(4096 * 6);

        for (int z = 0; z < Chunk.Size; z++)
            for (int y = 0; y < Chunk.Size; y++)
                for (int x = 0; x < Chunk.Size; x++)
                {
                    ref Block block = ref chunk.GetBlock(x, y, z);
                    if (block.IsTransparent)
                        continue;

                    for (int face = 0; face < 6; face++)
                    {
                        var (dx, dy, dz) = NeighbourOffsets[face];
                        int nx = x + dx, ny = y + dy, nz = z + dz;

                        bool exposed;
                        if (Chunk.InBounds(nx, ny, nz))
                        {
                            // Neighbour is inside this chunk — fast path.
                            exposed = chunk.GetBlock(nx, ny, nz).IsTransparent;
                        }
                        else if (world != null)
                        {
                            // Neighbour is in an adjacent chunk.  Convert to world-space
                            // coordinates and query the world (returns Air for unloaded
                            // chunks, keeping boundary faces exposed until the neighbour
                            // arrives and triggers a re-mesh).
                            int worldX = chunk.Position.X * Chunk.Size + nx;
                            int worldY = chunk.Position.Y * Chunk.Size + ny;
                            int worldZ = chunk.Position.Z * Chunk.Size + nz;
                            exposed = world.GetBlock(worldX, worldY, worldZ).IsTransparent;
                        }
                        else
                        {
                            // No world reference — always expose boundary faces.
                            exposed = true;
                        }

                        if (exposed)
                            EmitFace(verts, indices, face, x, y, z, block.Id);
                    }
                }

        return new ChunkMesh(verts.ToArray(), indices.ToArray());
    }

    // -----------------------------------------------------------------------
    // Face emission
    // -----------------------------------------------------------------------

    /// <summary>
    /// Appends 4 vertices (each: x y z u v) and 6 indices for one quad face.
    ///
    /// UV mapping:
    ///   The atlas tile for this block/face maps [u0, u1] horizontally and
    ///   [0, 1] vertically onto the quad.  The four corners are assigned UVs
    ///   such that the tile fully covers the face, irrespective of face direction.
    ///
    ///   v0 (u0, 0)  v1 (u0, 1)  v2 (u1, 1)  v3 (u1, 0)
    ///   Triangle 0: v0, v1, v2   Triangle 1: v0, v2, v3  (CCW)
    /// </summary>
    private static void EmitFace(
        List<float> verts, List<uint> indices,
        int face, int bx, int by, int bz, ushort blockId)
    {
        // baseIdx: the vertex index of the first vertex we're about to add.
        // Each vertex occupies 5 floats, so vertex count = verts.Count / 5.
        uint baseIdx = (uint)(verts.Count / 5);

        float x0 = bx, x1 = bx + 1f;
        float y0 = by, y1 = by + 1f;
        float z0 = bz, z1 = bz + 1f;

        // Resolve atlas UV columns for this block face.
        int tileIdx = BlockRegistry.TileForFace(blockId, face);
        float u0 = tileIdx * TextureAtlas.TileUvWidth;
        float u1 = (tileIdx + 1) * TextureAtlas.TileUvWidth;

        // Add 4 vertices (position + UV) in CCW order from outside the face.
        // UV corners: (u0,0) → (u0,1) → (u1,1) → (u1,0) for consistent tile coverage.
        switch (face)
        {
            case 0: // Top (+Y) — viewed from above
                AddV(verts, x0, y1, z0, u0, 0f);
                AddV(verts, x0, y1, z1, u0, 1f);
                AddV(verts, x1, y1, z1, u1, 1f);
                AddV(verts, x1, y1, z0, u1, 0f);
                break;
            case 1: // Bottom (-Y) — viewed from below
                AddV(verts, x0, y0, z0, u0, 0f);
                AddV(verts, x1, y0, z0, u1, 0f);
                AddV(verts, x1, y0, z1, u1, 1f);
                AddV(verts, x0, y0, z1, u0, 1f);
                break;
            case 2: // North (-Z) — viewed from -Z
                AddV(verts, x0, y0, z0, u0, 0f);
                AddV(verts, x0, y1, z0, u0, 1f);
                AddV(verts, x1, y1, z0, u1, 1f);
                AddV(verts, x1, y0, z0, u1, 0f);
                break;
            case 3: // South (+Z) — viewed from +Z
                AddV(verts, x1, y0, z1, u0, 0f);
                AddV(verts, x1, y1, z1, u0, 1f);
                AddV(verts, x0, y1, z1, u1, 1f);
                AddV(verts, x0, y0, z1, u1, 0f);
                break;
            case 4: // West (-X) — viewed from -X
                AddV(verts, x0, y0, z1, u0, 0f);
                AddV(verts, x0, y1, z1, u0, 1f);
                AddV(verts, x0, y1, z0, u1, 1f);
                AddV(verts, x0, y0, z0, u1, 0f);
                break;
            case 5: // East (+X) — viewed from +X
                AddV(verts, x1, y0, z0, u0, 0f);
                AddV(verts, x1, y1, z0, u0, 1f);
                AddV(verts, x1, y1, z1, u1, 1f);
                AddV(verts, x1, y0, z1, u1, 0f);
                break;
        }

        // Two triangles per quad: (v0,v1,v2) and (v0,v2,v3).
        indices.Add(baseIdx); indices.Add(baseIdx + 1); indices.Add(baseIdx + 2);
        indices.Add(baseIdx); indices.Add(baseIdx + 2); indices.Add(baseIdx + 3);
    }

    private static void AddV(List<float> v,
        float x, float y, float z, float u, float vt)
    {
        v.Add(x); v.Add(y); v.Add(z); v.Add(u); v.Add(vt);
    }
}
