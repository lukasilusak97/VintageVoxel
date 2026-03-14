using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using ImGuiNET;
using StbImageSharp;

namespace VintageVoxel.Rendering;

/// <summary>
/// Universal entity renderer. Every visible entity is rendered through a VSModel
/// loaded from JSON, similar to Unity's MeshRenderer: register a model path,
/// supply a world-space transform, and <see cref="RenderModel"/> handles the rest.
/// </summary>
public sealed class EntityRenderer : IDisposable
{
    // -- Shared GPU model cache (path-based) --------------------------------

    private sealed class MeshGpu : IDisposable
    {
        public int Vao, Vbo, Ebo, IndexCount;

        public void Dispose()
        {
            GL.DeleteVertexArray(Vao);
            GL.DeleteBuffer(Vbo);
            GL.DeleteBuffer(Ebo);
        }
    }

    private sealed class CachedModel
    {
        public required MeshGpu Gpu;
        public int TexHandle;
        public VSModel? Source;
        public string? FilePath;
        public VSAnimationController? Animation;
        public Vector3 BoundsMin;
        public Vector3 BoundsMax;
    }

    private readonly Dictionary<string, CachedModel?> _modelCache = new(StringComparer.OrdinalIgnoreCase);

    // -- Block item mini-cube rendering -------------------------------------

    private const int FaceCount = 6;
    private const int VertsPerFace = 4;
    private const int FloatsPerVertex = 7; // x y z u v light ao

    private readonly GpuMesh _blockMesh;
    private readonly GpuResourceManager _gpuResources;
    private readonly float[] _blockVerts = new float[FaceCount * VertsPerFace * FloatsPerVertex];
    private static readonly uint[] BlockCubeIndices;

    // -- Constants ----------------------------------------------------------

    private const string PlayerModelPath = "Entities/Player/player";

    // -- Static initializer -------------------------------------------------

    static EntityRenderer()
    {
        BlockCubeIndices = new uint[FaceCount * 6];
        for (uint f = 0; f < FaceCount; f++)
        {
            uint b = f * 4;
            uint i = f * 6;
            BlockCubeIndices[i + 0] = b + 0;
            BlockCubeIndices[i + 1] = b + 1;
            BlockCubeIndices[i + 2] = b + 2;
            BlockCubeIndices[i + 3] = b + 2;
            BlockCubeIndices[i + 4] = b + 3;
            BlockCubeIndices[i + 5] = b + 0;
        }
    }

    // -- Constructor --------------------------------------------------------

    public EntityRenderer(GpuResourceManager gpuResources)
    {
        _gpuResources = gpuResources;
        _blockMesh = _gpuResources.AllocateDynamicMesh(_blockVerts.Length, BlockCubeIndices, 7);
    }

    // =====================================================================
    //  Core: render any VSModel at a world-space transform
    // =====================================================================

    /// <summary>
    /// Draws the VSModel identified by <paramref name="modelRelPath"/> at the
    /// given <paramref name="modelMatrix"/>. The model is loaded, cached, and
    /// textured automatically. Pass a non-zero <paramref name="deltaTime"/> to
    /// advance keyframe animations.
    /// </summary>
    /// <param name="shader">Already-bound shader with view/projection set.</param>
    /// <param name="modelRelPath">
    /// Path relative to <c>Assets/Models/</c> without extension,
    /// e.g. <c>"Entities/Player/player"</c>.
    /// </param>
    public void RenderModel(Shader shader, string modelRelPath, ref Matrix4 modelMatrix,
                            float deltaTime = 0f)
    {
        var cached = GetOrUploadModel(modelRelPath);
        if (cached == null) return;

        AdvanceAnimation(cached, deltaTime);
        BindModelTexture(cached, shader);
        shader.SetMatrix4("model", ref modelMatrix);

        GL.Disable(EnableCap.CullFace);
        DrawCachedModel(cached);
        GL.Enable(EnableCap.CullFace);
    }

    // =====================================================================
    //  Entity items (dropped picks) -- block cubes + model items
    // =====================================================================

    /// <summary>
    /// Draws every entity in <paramref name="entities"/> as a spinning mini representation.
    /// Block items render as a mini cube using the atlas.
    /// Model items render their VSModel via the path-based cache.
    /// </summary>
    public void RenderEntityItems(IReadOnlyList<EntityItem> entities, Shader shader,
                                  int atlasHandle)
    {
        if (entities.Count == 0) return;

        int currentTex = atlasHandle;

        foreach (var entity in entities)
        {
            // --- MODEL / ENTITY items: render via VSModel path ---
            if ((entity.Item.Type == ItemType.Model || entity.Item.Type == ItemType.Entity)
                && entity.Item.ModelPath != null)
            {
                // Compute a transform that centres and fits the model in a unit cube.
                var model = Matrix4.Identity;
                if (TryGetModelBounds(entity.Item.ModelPath, out var bMin, out var bMax))
                {
                    var centre = (bMin + bMax) * 0.5f;
                    var size = bMax - bMin;
                    float maxDim = MathF.Max(size.X, MathF.Max(size.Y, size.Z));
                    float scale = maxDim > 0f ? 1f / maxDim : 1f;
                    model = Matrix4.CreateTranslation(-centre) *
                            Matrix4.CreateScale(scale);
                }
                model *= Matrix4.CreateRotationX(MathF.PI / 8f) *
                         Matrix4.CreateRotationY(entity.SpinAngle) *
                         Matrix4.CreateTranslation(entity.Position.X,
                                                   entity.Position.Y + EntityItem.HoverHeight,
                                                   entity.Position.Z);

                RenderModel(shader, entity.Item.ModelPath, ref model);

                // Restore atlas binding for subsequent block items.
                GL.ActiveTexture(TextureUnit.Texture0);
                GL.BindTexture(TextureTarget.Texture2D, atlasHandle);
                shader.SetInt("uNoTexture", 0);
                currentTex = atlasHandle;
                continue;
            }

            // --- BLOCK items (and MODEL fallback) ---
            if (currentTex != atlasHandle)
            {
                GL.ActiveTexture(TextureUnit.Texture0);
                GL.BindTexture(TextureTarget.Texture2D, atlasHandle);
                currentTex = atlasHandle;
            }

            GL.BindVertexArray(_blockMesh.Vao);
            GL.BindBuffer(BufferTarget.ArrayBuffer, _blockMesh.Vbo);

            ushort bid = (ushort)entity.Item.BlockId;
            float FaceU0(int face) => BlockRegistry.TileForFace(bid, face) * TextureAtlas.TileUvWidth;
            float FaceU1(int face) => FaceU0(face) + TextureAtlas.TileUvWidth;

            int v = 0;

            {
                float u0 = FaceU0(0), u1 = FaceU1(0);
                WriteBlockVertex(v++, -0.5f, 0.5f, -0.5f, u0, 0f, 1.0f);
                WriteBlockVertex(v++, -0.5f, 0.5f, 0.5f, u0, 1f, 1.0f);
                WriteBlockVertex(v++, 0.5f, 0.5f, 0.5f, u1, 1f, 1.0f);
                WriteBlockVertex(v++, 0.5f, 0.5f, -0.5f, u1, 0f, 1.0f);
            }

            {
                float u0 = FaceU0(1), u1 = FaceU1(1);
                WriteBlockVertex(v++, -0.5f, -0.5f, -0.5f, u0, 0f, 0.5f);
                WriteBlockVertex(v++, 0.5f, -0.5f, -0.5f, u1, 0f, 0.5f);
                WriteBlockVertex(v++, 0.5f, -0.5f, 0.5f, u1, 1f, 0.5f);
                WriteBlockVertex(v++, -0.5f, -0.5f, 0.5f, u0, 1f, 0.5f);
            }

            {
                float u0 = FaceU0(2), u1 = FaceU1(2);
                WriteBlockVertex(v++, -0.5f, -0.5f, -0.5f, u0, 0f, 0.8f);
                WriteBlockVertex(v++, -0.5f, 0.5f, -0.5f, u0, 1f, 0.8f);
                WriteBlockVertex(v++, 0.5f, 0.5f, -0.5f, u1, 1f, 0.8f);
                WriteBlockVertex(v++, 0.5f, -0.5f, -0.5f, u1, 0f, 0.8f);
            }

            {
                float u0 = FaceU0(3), u1 = FaceU1(3);
                WriteBlockVertex(v++, 0.5f, -0.5f, 0.5f, u0, 0f, 0.8f);
                WriteBlockVertex(v++, 0.5f, 0.5f, 0.5f, u0, 1f, 0.8f);
                WriteBlockVertex(v++, -0.5f, 0.5f, 0.5f, u1, 1f, 0.8f);
                WriteBlockVertex(v++, -0.5f, -0.5f, 0.5f, u1, 0f, 0.8f);
            }

            {
                float u0 = FaceU0(4), u1 = FaceU1(4);
                WriteBlockVertex(v++, -0.5f, -0.5f, 0.5f, u0, 0f, 0.65f);
                WriteBlockVertex(v++, -0.5f, 0.5f, 0.5f, u0, 1f, 0.65f);
                WriteBlockVertex(v++, -0.5f, 0.5f, -0.5f, u1, 1f, 0.65f);
                WriteBlockVertex(v++, -0.5f, -0.5f, -0.5f, u1, 0f, 0.65f);
            }

            {
                float u0 = FaceU0(5), u1 = FaceU1(5);
                WriteBlockVertex(v++, 0.5f, -0.5f, -0.5f, u0, 0f, 0.65f);
                WriteBlockVertex(v++, 0.5f, 0.5f, -0.5f, u0, 1f, 0.65f);
                WriteBlockVertex(v++, 0.5f, 0.5f, 0.5f, u1, 1f, 0.65f);
                WriteBlockVertex(v++, 0.5f, -0.5f, 0.5f, u1, 0f, 0.65f);
            }

            GL.BufferSubData(BufferTarget.ArrayBuffer, IntPtr.Zero,
                             _blockVerts.Length * sizeof(float), _blockVerts);

            var blockModel =
                Matrix4.CreateScale(0.35f) *
                Matrix4.CreateRotationX(MathF.PI / 8f) *
                Matrix4.CreateRotationY(entity.SpinAngle) *
                Matrix4.CreateTranslation(entity.Position.X,
                                          entity.Position.Y + EntityItem.HoverHeight,
                                          entity.Position.Z);
            shader.SetMatrix4("model", ref blockModel);

            GL.DrawElements(PrimitiveType.Triangles, BlockCubeIndices.Length,
                            DrawElementsType.UnsignedInt, 0);
        }

        if (currentTex != atlasHandle)
        {
            GL.ActiveTexture(TextureUnit.Texture0);
            GL.BindTexture(TextureTarget.Texture2D, atlasHandle);
        }

        GL.BindVertexArray(0);
    }

    private void WriteBlockVertex(int index, float x, float y, float z,
                                  float u, float v, float light)
    {
        int i = index * FloatsPerVertex;
        _blockVerts[i + 0] = x;
        _blockVerts[i + 1] = y;
        _blockVerts[i + 2] = z;
        _blockVerts[i + 3] = u;
        _blockVerts[i + 4] = v;
        _blockVerts[i + 5] = light;
        _blockVerts[i + 6] = 1.0f; // ao
    }

    // =====================================================================
    //  Remote players (VSModel + name tags)
    // =====================================================================

    /// <summary>
    /// Draws every remote player using the player VSModel.
    /// </summary>
    public void RenderRemotePlayers(IEnumerable<RemotePlayer> players, Shader shader)
    {
        foreach (var player in players)
        {
            float yawRad = MathHelper.DegreesToRadians(player.Yaw);
            var model = Matrix4.CreateRotationY(yawRad) *
                        Matrix4.CreateTranslation(player.Position);

            RenderModel(shader, PlayerModelPath, ref model);
        }
    }

    /// <summary>
    /// Draws name tag overlays via ImGui. Must be called inside an ImGui frame.
    /// </summary>
    public void RenderNameTags(IEnumerable<RemotePlayer> players, Camera camera,
                               int viewportWidth, int viewportHeight)
    {
        foreach (var player in players)
        {
            var worldPos = player.Position + new Vector3(0f, 2.2f, 0f);
            var screen = WorldToScreen(worldPos, camera, viewportWidth, viewportHeight);
            if (screen.Z <= 0f) continue;

            ImGui.SetNextWindowPos(new System.Numerics.Vector2(screen.X - 40f, screen.Y),
                                   ImGuiCond.Always);
            ImGui.SetNextWindowSize(new System.Numerics.Vector2(80f, 22f), ImGuiCond.Always);
            ImGui.SetNextWindowBgAlpha(0.45f);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new System.Numerics.Vector2(4f, 2f));

            var flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoNav |
                        ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoInputs |
                        ImGuiWindowFlags.NoSavedSettings;

            if (ImGui.Begin($"##nametag_{player.PlayerId}", flags))
                ImGui.TextUnformatted(player.Name);
            ImGui.End();
            ImGui.PopStyleVar();
        }
    }

    // =====================================================================
    //  Internal: model cache, texture, animation
    // =====================================================================

    private CachedModel? GetOrUploadModel(string modelRelPath)
    {
        if (_modelCache.TryGetValue(modelRelPath, out var cached))
            return cached;

        string modelsDir = Path.Combine(AppContext.BaseDirectory, "Assets", "Models");
        string filePath = Path.Combine(modelsDir, modelRelPath.ToLowerInvariant() + ".json");

        VSModel? source;
        ModelMesh? mesh;
        try
        {
            source = VSModelLoader.LoadModel(filePath);
            mesh = VSModelLoader.BuildAnimatedMesh(source, filePath, null);
        }
        catch
        {
            _modelCache[modelRelPath] = null;
            return null;
        }

        int texHandle = UploadModelTexture(mesh.TexturePng);

        VSAnimationController? anim = source.Animations.Count > 0
            ? new VSAnimationController(source.Animations[0])
            : null;

        ComputeBounds(mesh.Vertices, out var bMin, out var bMax);

        var entry = new CachedModel
        {
            Gpu = UploadModelMesh(mesh),
            TexHandle = texHandle,
            Source = source,
            FilePath = filePath,
            Animation = anim,
            BoundsMin = bMin,
            BoundsMax = bMax,
        };
        _modelCache[modelRelPath] = entry;
        return entry;
    }

    private static void ComputeBounds(float[] verts, out Vector3 min, out Vector3 max)
    {
        min = new Vector3(float.MaxValue);
        max = new Vector3(float.MinValue);
        for (int i = 0; i < verts.Length; i += FloatsPerVertex)
        {
            float x = verts[i], y = verts[i + 1], z = verts[i + 2];
            if (x < min.X) min.X = x;
            if (y < min.Y) min.Y = y;
            if (z < min.Z) min.Z = z;
            if (x > max.X) max.X = x;
            if (y > max.Y) max.Y = y;
            if (z > max.Z) max.Z = z;
        }
    }

    /// <summary>
    /// Returns the axis-aligned bounding box of a cached model, or false if the model
    /// is not loaded. Used by inventory rendering to auto-fit items.
    /// </summary>
    public bool TryGetModelBounds(string modelRelPath, out Vector3 boundsMin, out Vector3 boundsMax)
    {
        var cached = GetOrUploadModel(modelRelPath);
        if (cached == null)
        {
            boundsMin = boundsMax = Vector3.Zero;
            return false;
        }
        boundsMin = cached.BoundsMin;
        boundsMax = cached.BoundsMax;
        return true;
    }

    private static void AdvanceAnimation(CachedModel? cached, float deltaTime)
    {
        if (cached?.Animation is null || cached.Source is null || deltaTime == 0f) return;

        cached.Animation.Advance(deltaTime * 30f);
        var offsets = cached.Animation.Evaluate();

        ModelMesh mesh = VSModelLoader.BuildAnimatedMesh(cached.Source, cached.FilePath!, offsets);

        GL.BindBuffer(BufferTarget.ArrayBuffer, cached.Gpu.Vbo);
        GL.BufferData(BufferTarget.ArrayBuffer, mesh.Vertices.Length * sizeof(float),
                      mesh.Vertices, BufferUsageHint.DynamicDraw);
        GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
    }

    private static MeshGpu UploadModelMesh(ModelMesh mesh)
    {
        var gpu = new MeshGpu { IndexCount = mesh.Indices.Length };

        gpu.Vao = GL.GenVertexArray();
        GL.BindVertexArray(gpu.Vao);

        gpu.Vbo = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.ArrayBuffer, gpu.Vbo);
        GL.BufferData(BufferTarget.ArrayBuffer, mesh.Vertices.Length * sizeof(float),
                      mesh.Vertices, BufferUsageHint.StaticDraw);

        gpu.Ebo = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.ElementArrayBuffer, gpu.Ebo);
        GL.BufferData(BufferTarget.ElementArrayBuffer, mesh.Indices.Length * sizeof(uint),
                      mesh.Indices, BufferUsageHint.StaticDraw);

        const int stride = 7 * sizeof(float);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, 0);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, stride, 3 * sizeof(float));
        GL.EnableVertexAttribArray(1);
        GL.VertexAttribPointer(2, 1, VertexAttribPointerType.Float, false, stride, 5 * sizeof(float));
        GL.EnableVertexAttribArray(2);
        GL.DisableVertexAttribArray(3);
        GL.VertexAttrib1(3, 0.0f);
        GL.VertexAttribPointer(4, 1, VertexAttribPointerType.Float, false, stride, 6 * sizeof(float));
        GL.EnableVertexAttribArray(4);

        GL.BindVertexArray(0);
        return gpu;
    }

    private static int UploadModelTexture(byte[]? pngBytes)
    {
        if (pngBytes is not { Length: > 0 }) return 0;

        ImageResult img = ImageResult.FromMemory(pngBytes, ColorComponents.RedGreenBlueAlpha);
        int tex = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, tex);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8,
                      img.Width, img.Height, 0,
                      PixelFormat.Rgba, PixelType.UnsignedByte, img.Data);
        GL.TexParameter(TextureTarget.Texture2D,
            TextureParameterName.TextureMinFilter, (int)TextureMinFilter.NearestMipmapNearest);
        GL.TexParameter(TextureTarget.Texture2D,
            TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D,
            TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D,
            TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);
        GL.BindTexture(TextureTarget.Texture2D, 0);
        return tex;
    }

    private static void BindModelTexture(CachedModel cached, Shader shader)
    {
        GL.ActiveTexture(TextureUnit.Texture0);
        if (cached.TexHandle != 0)
        {
            GL.BindTexture(TextureTarget.Texture2D, cached.TexHandle);
            shader.SetInt("uNoTexture", 0);
        }
        else
        {
            GL.BindTexture(TextureTarget.Texture2D, 0);
            shader.SetInt("uNoTexture", 1);
        }
    }

    private static void DrawCachedModel(CachedModel cached)
    {
        GL.BindVertexArray(cached.Gpu.Vao);
        GL.DrawElements(PrimitiveType.Triangles, cached.Gpu.IndexCount, DrawElementsType.UnsignedInt, 0);
        GL.BindVertexArray(0);
    }

    private static Vector3 WorldToScreen(Vector3 world, Camera camera, int vpW, int vpH)
    {
        var clip = new Vector4(world, 1f) *
                   (camera.GetViewMatrix() * camera.GetProjectionMatrix());
        if (MathF.Abs(clip.W) < 0.0001f) return new Vector3(0, 0, -1);
        float ndcX = clip.X / clip.W;
        float ndcY = clip.Y / clip.W;
        float z = clip.W;
        return new Vector3(
            (ndcX * 0.5f + 0.5f) * vpW,
            (1f - (ndcY * 0.5f + 0.5f)) * vpH,
            z);
    }

    // =====================================================================
    //  Disposal
    // =====================================================================

    public void Dispose()
    {
        _gpuResources.Free(_blockMesh);

        foreach (var entry in _modelCache.Values)
        {
            if (entry == null) continue;
            if (entry.TexHandle != 0) GL.DeleteTexture(entry.TexHandle);
            entry.Gpu.Dispose();
        }
        _modelCache.Clear();
    }
}
