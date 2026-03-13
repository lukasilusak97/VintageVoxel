using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace VintageVoxel.Rendering;

/// <summary>
/// Renders vehicle models using either loaded Minecraft-format JSON meshes
/// (body + per-wheel) or a fallback procedural box-based vehicle.
///
/// The shader, unit-cube VAO, and fallback parts are shared across all vehicles.
/// Per-vehicle-type meshes are uploaded on first use and cached by model path.
/// </summary>
public sealed class VehicleRenderer : IDisposable
{
    // ─── Fallback procedural parts ────────────────────────────────────────
    private readonly record struct Part(
        Vector3 Offset,    // local centre offset from vehicle origin
        Vector3 HalfSize,  // half-extents
        Vector3 Color      // RGB [0,1]
    );

    private static readonly Part[] FallbackParts =
    {
        // Main body
        new(new Vector3(0f, 0f, 0f), new Vector3(1.0f, 0.25f, 2.0f), new Vector3(0.35f, 0.35f, 0.38f)),
        // Cabin
        new(new Vector3(0f, 0.55f, -0.3f), new Vector3(0.7f, 0.30f, 0.9f), new Vector3(0.3f, 0.45f, 0.65f)),
        // Front-left wheel
        new(new Vector3(-1.05f, -0.25f, -1.6f), new Vector3(0.15f, 0.20f, 0.20f), new Vector3(0.12f, 0.12f, 0.12f)),
        // Front-right wheel
        new(new Vector3( 1.05f, -0.25f, -1.6f), new Vector3(0.15f, 0.20f, 0.20f), new Vector3(0.12f, 0.12f, 0.12f)),
        // Rear-left wheel
        new(new Vector3(-1.05f, -0.25f,  1.6f), new Vector3(0.15f, 0.20f, 0.20f), new Vector3(0.12f, 0.12f, 0.12f)),
        // Rear-right wheel
        new(new Vector3( 1.05f, -0.25f,  1.6f), new Vector3(0.15f, 0.20f, 0.20f), new Vector3(0.12f, 0.12f, 0.12f)),
        // Hood accent
        new(new Vector3(0f, 0.26f, -1.4f), new Vector3(0.8f, 0.05f, 0.5f), new Vector3(0.55f, 0.15f, 0.15f)),
    };

    private static readonly float[] CubeVerts =
    {
        -1f,-1f,-1f,   1f,-1f,-1f,   1f, 1f,-1f,  -1f, 1f,-1f,
        -1f,-1f, 1f,   1f,-1f, 1f,   1f, 1f, 1f,  -1f, 1f, 1f,
    };

    private static readonly uint[] CubeIndices =
    {
        0,1,2, 2,3,0,   4,6,5, 6,4,7,
        0,3,7, 7,4,0,   1,5,6, 6,2,1,
        3,2,6, 6,7,3,   0,4,5, 5,1,0,
    };

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

    private readonly Dictionary<string, MeshGpu> _meshCache = new(StringComparer.OrdinalIgnoreCase);

    private readonly Shader _shader;
    private readonly int _vao, _vbo, _ebo;
    private readonly int _uModel, _uView, _uProjection, _uColor;

    public VehicleRenderer()
    {
        _shader = new Shader("Shaders/shader.vert", "Shaders/line.frag");

        _uModel = GL.GetUniformLocation(_shader.Handle, "model");
        _uView = GL.GetUniformLocation(_shader.Handle, "view");
        _uProjection = GL.GetUniformLocation(_shader.Handle, "projection");
        _uColor = GL.GetUniformLocation(_shader.Handle, "uColor");

        _vao = GL.GenVertexArray();
        GL.BindVertexArray(_vao);

        _vbo = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
        GL.BufferData(BufferTarget.ArrayBuffer, CubeVerts.Length * sizeof(float),
                      CubeVerts, BufferUsageHint.StaticDraw);

        _ebo = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.ElementArrayBuffer, _ebo);
        GL.BufferData(BufferTarget.ElementArrayBuffer, CubeIndices.Length * sizeof(uint),
                      CubeIndices, BufferUsageHint.StaticDraw);

        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), 0);
        GL.EnableVertexAttribArray(0);

        GL.BindVertexArray(0);
    }

    /// <summary>
    /// Draws the vehicle at the given world-space position and orientation.
    /// When <paramref name="setup"/> is provided and its model paths resolve,
    /// renders the JSON body + wheel meshes; otherwise falls back to procedural boxes.
    /// </summary>
    public void Render(System.Numerics.Vector3 position, System.Numerics.Quaternion orientation,
                       Camera camera, VehicleSetup? setup = null,
                       System.Numerics.Vector3[]? wheelOffsetsWorld = null)
    {
        GL.UseProgram(_shader.Handle);

        var view = camera.GetViewMatrix();
        var proj = camera.GetProjectionMatrix();
        GL.UniformMatrix4(_uView, false, ref view);
        GL.UniformMatrix4(_uProjection, false, ref proj);

        // Convert Bepu pose to OpenTK for matrix math.
        var pos = Physics.MathConversions.ToOpenTK(position);
        var rot = Physics.MathConversions.ToOpenTK(orientation);
        var worldRot = Matrix4.CreateFromQuaternion(rot);
        var worldTrans = Matrix4.CreateTranslation(pos);

        MeshGpu? bodyGpu = setup?.BodyModel != null ? GetOrUpload(setup.BodyModel) : null;
        MeshGpu? wheelGpu = setup?.WheelModel != null ? GetOrUpload(setup.WheelModel) : null;

        if (bodyGpu != null)
        {
            // Render body model.
            var model = worldRot * worldTrans;
            GL.UniformMatrix4(_uModel, false, ref model);
            GL.Uniform3(_uColor, new Vector3(1f, 1f, 1f));
            DrawMeshGpu(bodyGpu);

            // Render wheel models at each wheel position.
            if (wheelGpu != null && wheelOffsetsWorld != null)
            {
                foreach (var wpos in wheelOffsetsWorld)
                {
                    var wheelPos = Physics.MathConversions.ToOpenTK(wpos);
                    var wheelModel = worldRot * Matrix4.CreateTranslation(wheelPos);
                    GL.UniformMatrix4(_uModel, false, ref wheelModel);
                    DrawMeshGpu(wheelGpu);
                }
            }
        }
        else
        {
            // Fallback: procedural box parts.
            GL.BindVertexArray(_vao);
            foreach (var part in FallbackParts)
            {
                var scale = Matrix4.CreateScale(part.HalfSize);
                var trans = Matrix4.CreateTranslation(part.Offset);
                var model = scale * trans * worldRot * worldTrans;

                GL.UniformMatrix4(_uModel, false, ref model);
                GL.Uniform3(_uColor, part.Color);
                GL.DrawElements(PrimitiveType.Triangles, CubeIndices.Length, DrawElementsType.UnsignedInt, 0);
            }
            GL.BindVertexArray(0);
        }
    }

    // ─── Mesh cache helpers ───────────────────────────────────────────────

    private MeshGpu? GetOrUpload(string modelRelPath)
    {
        if (_meshCache.TryGetValue(modelRelPath, out var cached))
            return cached;

        string modelsDir = Path.Combine(AppContext.BaseDirectory, "Assets", "Models");
        string filePath = Path.Combine(modelsDir, modelRelPath.ToLowerInvariant() + ".json");

        if (!MinecraftModelLoader.TryLoad(filePath, out ModelMesh? mesh) || mesh == null)
        {
            _meshCache[modelRelPath] = null!;
            return null;
        }

        var gpu = UploadMesh(mesh);
        _meshCache[modelRelPath] = gpu;
        return gpu;
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

        // Vertex layout: 7 floats [x,y,z, u,v, light, ao]
        const int stride = 7 * sizeof(float);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, 0);
        GL.EnableVertexAttribArray(0);

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
        GL.DeleteVertexArray(_vao);
        GL.DeleteBuffer(_vbo);
        GL.DeleteBuffer(_ebo);
        _shader.Dispose();
        foreach (var gpu in _meshCache.Values)
            gpu?.Dispose();
        _meshCache.Clear();
    }
}
