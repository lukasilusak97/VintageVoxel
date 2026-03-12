using System;
using System.Collections.Generic;

namespace VintageVoxel;

/// <summary>
/// Shared quad-emission logic used by both full-block and sub-voxel mesh builders.
///
/// Callers are responsible for computing per-vertex light and AO values;
/// FaceEmitter only handles vertex layout and winding order.
///
/// Vertex format: x y z u v light ao  (7 floats per vertex, stride = 28 bytes)
/// Winding: CCW from outside for every face (matches OpenGL default front-face).
/// </summary>
internal static class FaceEmitter
{
    /// <summary>
    /// Appends 4 vertices and 6 indices for one quad face to the supplied lists.
    ///
    /// <paramref name="light"/> and <paramref name="ao"/> must each contain exactly
    /// 4 elements corresponding to vertices v0–v3 in CCW winding order for the face.
    /// </summary>
    /// <param name="verts">Destination vertex list (7 floats per vertex).</param>
    /// <param name="indices">Destination index list.</param>
    /// <param name="face">Face index 0–5: Top, Bottom, North, South, West, East.</param>
    /// <param name="ox">X origin of the voxel (lower-left-front corner).</param>
    /// <param name="oy">Y origin of the voxel.</param>
    /// <param name="oz">Z origin of the voxel.</param>
    /// <param name="size">Side length of the voxel (1.0 for full blocks, 1/SubSize for sub-voxels).</param>
    /// <param name="u0">Left UV edge of this face's tile in the texture atlas.</param>
    /// <param name="u1">Right UV edge of this face's tile in the texture atlas.</param>
    /// <param name="light">Per-vertex light values [0, 1], indexed v0–v3.</param>
    /// <param name="ao">Per-vertex ambient occlusion factors [0.4, 1.0], indexed v0–v3.</param>
    public static void Emit(
        List<float> verts, List<uint> indices,
        int face,
        float ox, float oy, float oz, float size,
        float u0, float u1,
        ReadOnlySpan<float> light, ReadOnlySpan<float> ao)
    {
        uint baseIdx = (uint)(verts.Count / 7);

        float x0 = ox, x1 = ox + size;
        float y0 = oy, y1 = oy + size;
        float z0 = oz, z1 = oz + size;

        switch (face)
        {
            case 0: // Top (+Y)
                Add(verts, x0, y1, z0, u0, 0f, light[0], ao[0]);
                Add(verts, x0, y1, z1, u0, 1f, light[1], ao[1]);
                Add(verts, x1, y1, z1, u1, 1f, light[2], ao[2]);
                Add(verts, x1, y1, z0, u1, 0f, light[3], ao[3]);
                break;
            case 1: // Bottom (-Y)
                Add(verts, x0, y0, z0, u0, 0f, light[0], ao[0]);
                Add(verts, x1, y0, z0, u1, 0f, light[1], ao[1]);
                Add(verts, x1, y0, z1, u1, 1f, light[2], ao[2]);
                Add(verts, x0, y0, z1, u0, 1f, light[3], ao[3]);
                break;
            case 2: // North (-Z)
                Add(verts, x0, y0, z0, u0, 1f, light[0], ao[0]);
                Add(verts, x0, y1, z0, u0, 0f, light[1], ao[1]);
                Add(verts, x1, y1, z0, u1, 0f, light[2], ao[2]);
                Add(verts, x1, y0, z0, u1, 1f, light[3], ao[3]);
                break;
            case 3: // South (+Z)
                Add(verts, x1, y0, z1, u0, 1f, light[0], ao[0]);
                Add(verts, x1, y1, z1, u0, 0f, light[1], ao[1]);
                Add(verts, x0, y1, z1, u1, 0f, light[2], ao[2]);
                Add(verts, x0, y0, z1, u1, 1f, light[3], ao[3]);
                break;
            case 4: // West (-X)
                Add(verts, x0, y0, z1, u0, 1f, light[0], ao[0]);
                Add(verts, x0, y1, z1, u0, 0f, light[1], ao[1]);
                Add(verts, x0, y1, z0, u1, 0f, light[2], ao[2]);
                Add(verts, x0, y0, z0, u1, 1f, light[3], ao[3]);
                break;
            case 5: // East (+X)
                Add(verts, x1, y0, z0, u0, 1f, light[0], ao[0]);
                Add(verts, x1, y1, z0, u0, 0f, light[1], ao[1]);
                Add(verts, x1, y1, z1, u1, 0f, light[2], ao[2]);
                Add(verts, x1, y0, z1, u1, 1f, light[3], ao[3]);
                break;
        }

        indices.Add(baseIdx); indices.Add(baseIdx + 1); indices.Add(baseIdx + 2);
        indices.Add(baseIdx); indices.Add(baseIdx + 2); indices.Add(baseIdx + 3);
    }

    private static void Add(List<float> v,
        float x, float y, float z, float u, float vt, float light, float ao)
    {
        v.Add(x); v.Add(y); v.Add(z); v.Add(u); v.Add(vt); v.Add(light); v.Add(ao);
    }

    // -----------------------------------------------------------------------
    // Slope geometry emission (8-float vertex: x y z u v sunLight blockLight ao)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Emits triangles for a slope-shaped block.
    /// The geometry consists of:
    ///   • A bottom quad (always present, full block-bottom face)
    ///   • The solid vertical sides dictated by the shape (full quads)
    ///   • A diagonal top surface computed from SlopeGeometry.HeightAt
    ///
    /// <paramref name="sunLight"/> and <paramref name="blockLight"/> each hold 6
    /// per-face values indexed as: 0=Top(+Y), 1=Bottom(-Y), 2=North(-Z),
    /// 3=South(+Z), 4=West(-X), 5=East(+X). Each entry should already have the
    /// appropriate directional scale applied by the caller.
    /// </summary>
    public static void EmitSlopeFaces(
        List<float> verts, List<uint> indices,
        SlopeShape shape,
        float ox, float oy, float oz,
        float u0, float u1,
        ReadOnlySpan<float> sunLight, ReadOnlySpan<float> blockLight, float ao)
    {
        // Helper: emit a triangle (3 vertices, 3 indices).
        void Tri(
            float ax, float ay, float az, float au, float av,
            float bx, float by, float bz, float bu, float bv,
            float cx, float cy, float cz, float cu, float cv,
            float sl, float bl)
        {
            uint b = (uint)(verts.Count / 8);
            AddS(verts, ax, ay, az, au, av, sl, bl, ao);
            AddS(verts, bx, by, bz, bu, bv, sl, bl, ao);
            AddS(verts, cx, cy, cz, cu, cv, sl, bl, ao);
            indices.Add(b); indices.Add(b + 1); indices.Add(b + 2);
        }

        // Helper: emit a quad (4 vertices, 2 triangles, CCW winding).
        void Quad(
            float ax, float ay, float az, float au, float av,
            float bx, float by, float bz, float bu, float bv,
            float cx, float cy, float cz, float cu, float cv,
            float dx, float dy, float dz, float du, float dv,
            float sl, float bl)
        {
            uint b = (uint)(verts.Count / 8);
            AddS(verts, ax, ay, az, au, av, sl, bl, ao);
            AddS(verts, bx, by, bz, bu, bv, sl, bl, ao);
            AddS(verts, cx, cy, cz, cu, cv, sl, bl, ao);
            AddS(verts, dx, dy, dz, du, dv, sl, bl, ao);
            indices.Add(b); indices.Add(b + 1); indices.Add(b + 2);
            indices.Add(b); indices.Add(b + 2); indices.Add(b + 3);
        }

        float x0 = ox, x1 = ox + 1f;
        float y0 = oy, y1 = oy + 1f;
        float z0 = oz, z1 = oz + 1f;

        // Corner heights — y at (x0,z0), (x1,z0), (x1,z1), (x0,z1)
        float h00 = oy + SlopeGeometry.HeightAt(shape, 0f, 0f);  // NW
        float h10 = oy + SlopeGeometry.HeightAt(shape, 1f, 0f);  // NE
        float h11 = oy + SlopeGeometry.HeightAt(shape, 1f, 1f);  // SE
        float h01 = oy + SlopeGeometry.HeightAt(shape, 0f, 1f);  // SW

        // Face index constants matching ChunkMeshBuilder.NeighbourOffsets:
        //   0=Top(+Y)  1=Bottom(-Y)  2=North(-Z)  3=South(+Z)  4=West(-X)  5=East(+X)

        // --- Bottom face (face 1: -Y) ---
        Quad(x0, y0, z0, u0, 0f,
             x1, y0, z0, u1, 0f,
             x1, y0, z1, u1, 1f,
             x0, y0, z1, u0, 1f,
             sunLight[1], blockLight[1]);

        // --- Diagonal top surface (face 0: +Y, two triangles split NW–SE) ---
        Tri(x0, h00, z0, u0, 0f,
            x0, h01, z1, u0, 1f,
            x1, h11, z1, u1, 1f,
            sunLight[0], blockLight[0]);
        Tri(x0, h00, z0, u0, 0f,
            x1, h11, z1, u1, 1f,
            x1, h10, z0, u1, 0f,
            sunLight[0], blockLight[0]);

        // --- Solid vertical sides based on shape ---
        // North face (-Z, face 2): solid when h00==y1 && h10==y1.
        if (h00 >= y1 - 0.001f && h10 >= y1 - 0.001f)
            Quad(x0, y0, z0, u0, 1f,
                 x0, y1, z0, u0, 0f,
                 x1, y1, z0, u1, 0f,
                 x1, y0, z0, u1, 1f,
                 sunLight[2], blockLight[2]);
        else if (h00 > y0 + 0.001f || h10 > y0 + 0.001f)
            // Partial triangular north face.
            Tri(x0, y0, z0, u0, 1f,
                x0, h00, z0, u0, 0f,
                x1, h10, z0, u1, 0f,
                sunLight[2], blockLight[2]);

        // South face (+Z, face 3): solid when h01==y1 && h11==y1.
        if (h01 >= y1 - 0.001f && h11 >= y1 - 0.001f)
            Quad(x1, y0, z1, u0, 1f,
                 x1, y1, z1, u0, 0f,
                 x0, y1, z1, u1, 0f,
                 x0, y0, z1, u1, 1f,
                 sunLight[3], blockLight[3]);
        else if (h01 > y0 + 0.001f || h11 > y0 + 0.001f)
            Tri(x1, y0, z1, u0, 1f,
                x1, h11, z1, u0, 0f,
                x0, h01, z1, u1, 0f,
                sunLight[3], blockLight[3]);

        // West face (-X, face 4): solid when h00==y1 && h01==y1.
        if (h00 >= y1 - 0.001f && h01 >= y1 - 0.001f)
            Quad(x0, y0, z1, u0, 1f,
                 x0, y1, z1, u0, 0f,
                 x0, y1, z0, u1, 0f,
                 x0, y0, z0, u1, 1f,
                 sunLight[4], blockLight[4]);
        else if (h00 > y0 + 0.001f || h01 > y0 + 0.001f)
            Tri(x0, y0, z1, u0, 1f,
                x0, h01, z1, u0, 0f,
                x0, h00, z0, u1, 0f,
                sunLight[4], blockLight[4]);

        // East face (+X, face 5): solid when h10==y1 && h11==y1.
        if (h10 >= y1 - 0.001f && h11 >= y1 - 0.001f)
            Quad(x1, y0, z0, u0, 1f,
                 x1, y1, z0, u0, 0f,
                 x1, y1, z1, u1, 0f,
                 x1, y0, z1, u1, 1f,
                 sunLight[5], blockLight[5]);
        else if (h10 > y0 + 0.001f || h11 > y0 + 0.001f)
            Tri(x1, y0, z0, u0, 1f,
                x1, h10, z0, u0, 0f,
                x1, h11, z1, u1, 0f,
                sunLight[5], blockLight[5]);
    }

    private static void AddS(List<float> v,
        float x, float y, float z, float u, float vt,
        float sunLight, float blockLight, float ao)
    {
        v.Add(x); v.Add(y); v.Add(z); v.Add(u); v.Add(vt);
        v.Add(sunLight); v.Add(blockLight); v.Add(ao);
    }
}
