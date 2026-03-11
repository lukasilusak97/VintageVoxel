using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using VintageVoxel.Rendering;

namespace VintageVoxel;

/// <summary>
/// Renders all dropped <see cref="EntityItem"/> entities as small spinning
/// 3-D mini-blocks, reusing the world <see cref="Shader"/> and texture atlas.
///
/// Six faces are emitted per entity using the same CCW winding order as
/// <see cref="ChunkMeshBuilder"/> so GPU back-face culling works correctly.
/// Per-face light values give a convincing pseudo-directional shading without
/// requiring normals or a second shader.
/// </summary>
public sealed class EntityItemRenderer : IDisposable
{
    private const int FaceCount = 6;
    private const int VertsPerFace = 4;
    private const int FloatsPerVertex = 7; // x y z u v light ao

    private readonly GpuMesh _mesh;
    private readonly GpuResourceManager _gpuResources;

    // 6 faces × 4 vertices × 7 floats — refilled before every draw call.
    private readonly float[] _verts = new float[FaceCount * VertsPerFace * FloatsPerVertex];

    // 6 faces × 2 triangles × 3 indices = 36 indices — never changes.
    private static readonly uint[] CubeIndices;

    static EntityItemRenderer()
    {
        CubeIndices = new uint[FaceCount * 6];
        for (uint f = 0; f < FaceCount; f++)
        {
            uint b = f * 4; // base vertex for this face
            uint i = f * 6;
            CubeIndices[i + 0] = b + 0;
            CubeIndices[i + 1] = b + 1;
            CubeIndices[i + 2] = b + 2;
            CubeIndices[i + 3] = b + 2;
            CubeIndices[i + 4] = b + 3;
            CubeIndices[i + 5] = b + 0;
        }
    }

    public EntityItemRenderer(GpuResourceManager gpuResources)
    {
        _gpuResources = gpuResources;
        _mesh = _gpuResources.AllocateDynamicMesh(_verts.Length, CubeIndices, 7);
    }

    // -------------------------------------------------------------------------
    // Rendering
    // -------------------------------------------------------------------------

    /// <summary>
    /// Draws every entity in <paramref name="entities"/> as a spinning mini representation.
    /// Block items render as a mini cube using the atlas.
    /// Model items render their actual <see cref="ModelMesh"/> when
    /// <paramref name="modelGpuGetter"/> is provided and returns data.
    /// <paramref name="atlasHandle"/> is the GL texture handle for the world atlas;
    /// it is rebound automatically whenever the active texture needs to switch back.
    /// </summary>
    public void Render(IReadOnlyList<EntityItem> entities, Shader shader,
                       int atlasHandle,
                       Func<Item, (int Vao, int IndexCount, int TexHandle)?>? modelGpuGetter = null)
    {
        if (entities.Count == 0) return;

        // Track which GL texture is currently bound so we only rebind on switches.
        int currentTex = atlasHandle;

        foreach (var entity in entities)
        {
            // --- MODEL items: render the actual mesh ---
            if (entity.Item.Type == ItemType.Model && modelGpuGetter != null)
            {
                var gpuData = modelGpuGetter(entity.Item);
                if (gpuData.HasValue)
                {
                    var (mVao, mIdxCount, mTexHandle) = gpuData.Value;

                    if (mTexHandle != 0 && mTexHandle != currentTex)
                    {
                        GL.ActiveTexture(TextureUnit.Texture0);
                        GL.BindTexture(TextureTarget.Texture2D, mTexHandle);
                        currentTex = mTexHandle;
                    }

                    // Translate origin from Minecraft 0-16 centre → scale to block space → tilt → spin → world position.
                    var model =
                        Matrix4.CreateTranslation(-8f, -8f, -8f) *
                        Matrix4.CreateScale(1f / 16f) *
                        Matrix4.CreateRotationX(MathF.PI / 8f) *
                        Matrix4.CreateRotationY(entity.SpinAngle) *
                        Matrix4.CreateTranslation(entity.Position.X,
                                                  entity.Position.Y + EntityItem.HoverHeight,
                                                  entity.Position.Z);
                    shader.SetMatrix4("model", ref model);

                    GL.Disable(EnableCap.CullFace);
                    GL.BindVertexArray(mVao);
                    GL.DrawElements(PrimitiveType.Triangles, mIdxCount,
                                    DrawElementsType.UnsignedInt, 0);
                    GL.Enable(EnableCap.CullFace);
                    continue;
                }
            }

            // --- BLOCK items (and MODEL fallback): render as mini cube ---
            // Ensure the atlas is bound — a previous MODEL entity may have swapped the texture.
            if (currentTex != atlasHandle)
            {
                GL.ActiveTexture(TextureUnit.Texture0);
                GL.BindTexture(TextureTarget.Texture2D, atlasHandle);
                currentTex = atlasHandle;
            }

            GL.BindVertexArray(_mesh.Vao);
            GL.BindBuffer(BufferTarget.ArrayBuffer, _mesh.Vbo);

            // Look up per-face tile indices from the registry so blocks like grass
            // show the correct texture on each face (e.g. grass top vs. side).
            ushort bid = (ushort)entity.Item.BlockId;

            float FaceU0(int face) => BlockRegistry.TileForFace(bid, face) * TextureAtlas.TileUvWidth;
            float FaceU1(int face) => FaceU0(face) + TextureAtlas.TileUvWidth;

            // ---- 6 faces of a unit cube centred at the local origin ----
            // Winding/vertex order matches ChunkMeshBuilder so back-face culling
            // correctly removes faces pointing away from the camera.
            // Per-face light simulates directional shading without normals.

            int v = 0; // running vertex index (0..23)

            // Face 0 — Top (+Y), light 1.0
            {
                float u0 = FaceU0(0), u1 = FaceU1(0);
                WriteVertex(v++, -0.5f, 0.5f, -0.5f, u0, 0f, 1.0f);
                WriteVertex(v++, -0.5f, 0.5f, 0.5f, u0, 1f, 1.0f);
                WriteVertex(v++, 0.5f, 0.5f, 0.5f, u1, 1f, 1.0f);
                WriteVertex(v++, 0.5f, 0.5f, -0.5f, u1, 0f, 1.0f);
            }

            // Face 1 — Bottom (-Y), light 0.5
            {
                float u0 = FaceU0(1), u1 = FaceU1(1);
                WriteVertex(v++, -0.5f, -0.5f, -0.5f, u0, 0f, 0.5f);
                WriteVertex(v++, 0.5f, -0.5f, -0.5f, u1, 0f, 0.5f);
                WriteVertex(v++, 0.5f, -0.5f, 0.5f, u1, 1f, 0.5f);
                WriteVertex(v++, -0.5f, -0.5f, 0.5f, u0, 1f, 0.5f);
            }

            // Face 2 — North (-Z), light 0.8
            {
                float u0 = FaceU0(2), u1 = FaceU1(2);
                WriteVertex(v++, -0.5f, -0.5f, -0.5f, u0, 0f, 0.8f);
                WriteVertex(v++, -0.5f, 0.5f, -0.5f, u0, 1f, 0.8f);
                WriteVertex(v++, 0.5f, 0.5f, -0.5f, u1, 1f, 0.8f);
                WriteVertex(v++, 0.5f, -0.5f, -0.5f, u1, 0f, 0.8f);
            }

            // Face 3 — South (+Z), light 0.8
            {
                float u0 = FaceU0(3), u1 = FaceU1(3);
                WriteVertex(v++, 0.5f, -0.5f, 0.5f, u0, 0f, 0.8f);
                WriteVertex(v++, 0.5f, 0.5f, 0.5f, u0, 1f, 0.8f);
                WriteVertex(v++, -0.5f, 0.5f, 0.5f, u1, 1f, 0.8f);
                WriteVertex(v++, -0.5f, -0.5f, 0.5f, u1, 0f, 0.8f);
            }

            // Face 4 — West (-X), light 0.65
            {
                float u0 = FaceU0(4), u1 = FaceU1(4);
                WriteVertex(v++, -0.5f, -0.5f, 0.5f, u0, 0f, 0.65f);
                WriteVertex(v++, -0.5f, 0.5f, 0.5f, u0, 1f, 0.65f);
                WriteVertex(v++, -0.5f, 0.5f, -0.5f, u1, 1f, 0.65f);
                WriteVertex(v++, -0.5f, -0.5f, -0.5f, u1, 0f, 0.65f);
            }

            // Face 5 — East (+X), light 0.65
            {
                float u0 = FaceU0(5), u1 = FaceU1(5);
                WriteVertex(v++, 0.5f, -0.5f, -0.5f, u0, 0f, 0.65f);
                WriteVertex(v++, 0.5f, 0.5f, -0.5f, u0, 1f, 0.65f);
                WriteVertex(v++, 0.5f, 0.5f, 0.5f, u1, 1f, 0.65f);
                WriteVertex(v++, 0.5f, -0.5f, 0.5f, u1, 0f, 0.65f);
            }

            GL.BufferSubData(BufferTarget.ArrayBuffer, IntPtr.Zero,
                             _verts.Length * sizeof(float), _verts);

            // Scale → tilt → spin → translate to world position.
            var blockModel =
                Matrix4.CreateScale(0.35f) *
                Matrix4.CreateRotationX(MathF.PI / 8f) *   // slight tilt so top face is visible
                Matrix4.CreateRotationY(entity.SpinAngle) *
                Matrix4.CreateTranslation(entity.Position.X,
                                          entity.Position.Y + EntityItem.HoverHeight,
                                          entity.Position.Z);
            shader.SetMatrix4("model", ref blockModel);

            GL.DrawElements(PrimitiveType.Triangles, CubeIndices.Length,
                            DrawElementsType.UnsignedInt, 0);
        }

        // Always restore the atlas at the end so subsequent draw calls are unaffected.
        if (currentTex != atlasHandle)
        {
            GL.ActiveTexture(TextureUnit.Texture0);
            GL.BindTexture(TextureTarget.Texture2D, atlasHandle);
        }

        GL.BindVertexArray(0);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private void WriteVertex(int index, float x, float y, float z,
                              float u, float v, float light)
    {
        int i = index * FloatsPerVertex;
        _verts[i + 0] = x;
        _verts[i + 1] = y;
        _verts[i + 2] = z;
        _verts[i + 3] = u;
        _verts[i + 4] = v;
        _verts[i + 5] = light;
        _verts[i + 6] = 1.0f; // ao
    }

    // -------------------------------------------------------------------------
    // Lifetime
    // -------------------------------------------------------------------------

    public void Dispose()
    {
        _gpuResources.Free(_mesh);
    }
}
