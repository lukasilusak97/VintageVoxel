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
                Add(verts, x0, y0, z0, u0, 0f, light[0], ao[0]);
                Add(verts, x0, y1, z0, u0, 1f, light[1], ao[1]);
                Add(verts, x1, y1, z0, u1, 1f, light[2], ao[2]);
                Add(verts, x1, y0, z0, u1, 0f, light[3], ao[3]);
                break;
            case 3: // South (+Z)
                Add(verts, x1, y0, z1, u0, 0f, light[0], ao[0]);
                Add(verts, x1, y1, z1, u0, 1f, light[1], ao[1]);
                Add(verts, x0, y1, z1, u1, 1f, light[2], ao[2]);
                Add(verts, x0, y0, z1, u1, 0f, light[3], ao[3]);
                break;
            case 4: // West (-X)
                Add(verts, x0, y0, z1, u0, 0f, light[0], ao[0]);
                Add(verts, x0, y1, z1, u0, 1f, light[1], ao[1]);
                Add(verts, x0, y1, z0, u1, 1f, light[2], ao[2]);
                Add(verts, x0, y0, z0, u1, 0f, light[3], ao[3]);
                break;
            case 5: // East (+X)
                Add(verts, x1, y0, z0, u0, 0f, light[0], ao[0]);
                Add(verts, x1, y1, z0, u0, 1f, light[1], ao[1]);
                Add(verts, x1, y1, z1, u1, 1f, light[2], ao[2]);
                Add(verts, x1, y0, z1, u1, 0f, light[3], ao[3]);
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
}
