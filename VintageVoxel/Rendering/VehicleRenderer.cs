using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using StbImageSharp;

namespace VintageVoxel.Rendering;

/// <summary>
/// Renders vehicle models using Vintage Story JSON meshes (body + per-wheel).
/// Per-vehicle-type meshes are uploaded on first use and cached by model path.
/// </summary>
public sealed class VehicleRenderer : IDisposable
{
    // ─── Cached model GPU data ────────────────────────────────────────────
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

    /// <summary>Cached per-model data: GPU buffers, raw model for re-meshing, and animation state.</summary>
    private sealed class CachedModel
    {
        public required MeshGpu Gpu;
        public int TexHandle;
        public VSModel? Source;
        public string? FilePath;
        public VSAnimationController? Animation;
    }

    private readonly Dictionary<string, CachedModel?> _modelCache = new(StringComparer.OrdinalIgnoreCase);

    private readonly Shader _shader;

    public VehicleRenderer()
    {
        _shader = new Shader("Shaders/shader.vert", "Shaders/shader.frag");
    }

    /// <summary>
    /// Draws the vehicle at the given world-space position and orientation.
    /// Renders the JSON body mesh from <paramref name="setup"/> and wheel meshes
    /// from <paramref name="wheelModelPath"/> at active slot positions.
    /// <paramref name="deltaTime"/> advances any keyframe animations defined in the models.
    /// </summary>
    public void Render(System.Numerics.Vector3 position, System.Numerics.Quaternion orientation,
                       Camera camera, VehicleSetup? setup = null,
                       System.Numerics.Vector3[]? wheelOffsetsWorld = null,
                       string? wheelModelPath = null,
                       bool[]? activeWheelMask = null,
                       float deltaTime = 0f)
    {
        CachedModel? bodyCached = setup?.BodyModel != null ? GetOrUpload(setup.BodyModel) : null;
        if (bodyCached == null) return; // no model, nothing to render

        _shader.Use();

        var view = camera.GetViewMatrix();
        var proj = camera.GetProjectionMatrix();
        _shader.SetMatrix4("view", ref view);
        _shader.SetMatrix4("projection", ref proj);

        // Shader uniforms required by shader.frag
        _shader.SetInt("uTexture", 0);
        _shader.SetInt("uNoTexture", 0);
        _shader.SetFloat("uFogStart", 9999f);
        _shader.SetFloat("uFogEnd", 10000f);
        _shader.SetFloat("uAlphaOverride", -1.0f);

        // Shadow map not available here -- bind a dummy identity matrix.
        var identity = Matrix4.Identity;
        _shader.SetMatrix4("lightSpaceMatrix", ref identity);

        // Convert Bepu pose to OpenTK for matrix math.
        var pos = Physics.MathConversions.ToOpenTK(position);
        var rot = Physics.MathConversions.ToOpenTK(orientation);
        var worldRot = Matrix4.CreateFromQuaternion(rot);
        var worldTrans = Matrix4.CreateTranslation(pos);

        CachedModel? wheelCached = wheelModelPath != null ? GetOrUpload(wheelModelPath) : null;

        // Advance animations and re-upload meshes when needed.
        AdvanceAnimation(bodyCached, deltaTime);
        AdvanceAnimation(wheelCached, deltaTime);

        // Render body model.
        BindModelTexture(bodyCached);
        var model = worldRot * worldTrans;
        _shader.SetMatrix4("model", ref model);
        DrawMeshGpu(bodyCached.Gpu);

        // Render wheel models at active slots only.
        if (wheelCached != null && wheelOffsetsWorld != null)
        {
            BindModelTexture(wheelCached);
            for (int i = 0; i < wheelOffsetsWorld.Length; i++)
            {
                if (activeWheelMask != null && !activeWheelMask[i]) continue;
                var wheelPos = Physics.MathConversions.ToOpenTK(wheelOffsetsWorld[i]);
                var wheelModel = worldRot * Matrix4.CreateTranslation(wheelPos);
                _shader.SetMatrix4("model", ref wheelModel);
                DrawMeshGpu(wheelCached.Gpu);
            }
        }
    }

    private void BindModelTexture(CachedModel cached)
    {
        GL.ActiveTexture(TextureUnit.Texture0);
        if (cached.TexHandle != 0)
        {
            GL.BindTexture(TextureTarget.Texture2D, cached.TexHandle);
            _shader.SetInt("uNoTexture", 0);
        }
        else
        {
            GL.BindTexture(TextureTarget.Texture2D, 0);
            _shader.SetInt("uNoTexture", 1);
        }
    }

    // ─── Mesh cache helpers ───────────────────────────────────────────────

    private CachedModel? GetOrUpload(string modelRelPath)
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

        int texHandle = 0;
        if (mesh.TexturePng is { Length: > 0 } pngBytes)
        {
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
            texHandle = tex;
        }

        VSAnimationController? anim = source.Animations.Count > 0
            ? new VSAnimationController(source.Animations[0])
            : null;

        var entry = new CachedModel
        {
            Gpu = UploadMesh(mesh),
            TexHandle = texHandle,
            Source = source,
            FilePath = filePath,
            Animation = anim,
        };
        _modelCache[modelRelPath] = entry;
        return entry;
    }

    /// <summary>
    /// If the cached model has an active animation, advances it and re-uploads the mesh.
    /// </summary>
    private static void AdvanceAnimation(CachedModel? cached, float deltaTime)
    {
        if (cached?.Animation is null || cached.Source is null || deltaTime == 0f) return;

        cached.Animation.Advance(deltaTime * 30f); // VS animations run at 30 fps by convention
        var offsets = cached.Animation.Evaluate();

        ModelMesh mesh = VSModelLoader.BuildAnimatedMesh(cached.Source, cached.FilePath!, offsets);

        // Re-upload vertex data to existing VBO (index count doesn't change).
        GL.BindBuffer(BufferTarget.ArrayBuffer, cached.Gpu.Vbo);
        GL.BufferData(BufferTarget.ArrayBuffer, mesh.Vertices.Length * sizeof(float),
                      mesh.Vertices, BufferUsageHint.DynamicDraw);
        GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
    }

    private static MeshGpu UploadMesh(ModelMesh mesh)
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

        // Entity / model vertex layout: pos(3) + uv(2) + light(1) + ao(1)
        const int stride = 7 * sizeof(float);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, 0);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, stride, 3 * sizeof(float));
        GL.EnableVertexAttribArray(1);
        GL.VertexAttribPointer(2, 1, VertexAttribPointerType.Float, false, stride, 5 * sizeof(float));
        GL.EnableVertexAttribArray(2);
        // blockLight not in buffer -- standalone default of 0.
        GL.DisableVertexAttribArray(3);
        GL.VertexAttrib1(3, 0.0f);
        GL.VertexAttribPointer(4, 1, VertexAttribPointerType.Float, false, stride, 6 * sizeof(float));
        GL.EnableVertexAttribArray(4);

        GL.BindVertexArray(0);
        return gpu;
    }

    private static void DrawMeshGpu(MeshGpu gpu)
    {
        GL.BindVertexArray(gpu.Vao);
        GL.DrawElements(PrimitiveType.Triangles, gpu.IndexCount, DrawElementsType.UnsignedInt, 0);
        GL.BindVertexArray(0);
    }

    public void Dispose()
    {
        _shader.Dispose();
        foreach (var entry in _modelCache.Values)
        {
            if (entry == null) continue;
            if (entry.TexHandle != 0)
                GL.DeleteTexture(entry.TexHandle);
            entry.Gpu.Dispose();
        }
        _modelCache.Clear();
    }
}
