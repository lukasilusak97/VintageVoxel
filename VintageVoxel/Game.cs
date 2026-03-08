using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace VintageVoxel;

/// <summary>
/// The main game window. Inherits from GameWindow which manages the OS window,
/// the OpenGL context, and the game loop (Update + Render at fixed/variable rates).
/// </summary>
public class Game : GameWindow
{
    // OpenGL resource bundle for one chunk — managed GPU-side per loaded chunk.
    private readonly record struct ChunkGpu(int Vao, int Vbo, int Ebo, int IndexCount);

    private readonly Dictionary<Vector2i, ChunkGpu> _chunkGpuData = new();

    private Shader _shader = null!;
    private Camera _camera = null!;
    private World _world = null!;
    private Texture _atlas = null!;

    // Track whether the mouse is captured for FPS look.
    private bool _firstMove = true;
    private Vector2 _lastMousePos;

    public Game(GameWindowSettings gameWindowSettings, NativeWindowSettings nativeWindowSettings)
        : base(gameWindowSettings, nativeWindowSettings)
    {
    }

    protected override void OnLoad()
    {
        base.OnLoad();

        GL.ClearColor(0.53f, 0.81f, 0.92f, 1.0f);
        GL.Enable(EnableCap.DepthTest);

        // Enable back-face culling: skip rendering faces whose normal points away from
        // the camera. For a closed solid like a cube, this halves the fragment work.
        // CullFace.Back + FrontFace.CounterClockwise is the OpenGL default convention.
        GL.Enable(EnableCap.CullFace);
        GL.CullFace(CullFaceMode.Back);
        GL.FrontFace(FrontFaceDirection.Ccw);

        // Capture the mouse cursor so it doesn't leave the window while looking around.
        CursorState = CursorState.Grabbed;

        _shader = new Shader("Shaders/shader.vert", "Shaders/shader.frag");

        // Generate the procedural texture atlas (Dirt, Stone, Grass tiles) and
        // upload it to the GPU.  Must be done after the GL context is created (i.e.
        // inside OnLoad) and before the first draw call.
        _atlas = TextureAtlas.Generate();

        // Camera starts above the noise terrain (max surface ~22 blocks) and slightly
        // outside the origin chunk on the +Z side for a good opening view.
        float aspect = Size.X / (float)Size.Y;
        _camera = new Camera(new Vector3(16f, 35f, 50f), 70f, aspect);

        // -----------------------------------------------------------------------
        // Initialise the world and pre-generate the first ring of chunks around
        // the spawn position.  ALL chunks are inserted into the dictionary before
        // any mesh is built, so cross-chunk face culling works correctly for the
        // entire initial grid.
        // -----------------------------------------------------------------------
        _world = new World();
        _world.Update(_camera.Position, out var initial, out _);
        foreach (var key in initial)
            _chunkGpuData[key] = UploadChunk(_world.Chunks[key]);
    }

    // -------------------------------------------------------------------------
    // Chunk GPU helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Meshes <paramref name="chunk"/> (with cross-chunk culling against the world),
    /// uploads the geometry to the GPU, and returns a <see cref="ChunkGpu"/> bundle.
    /// </summary>
    private ChunkGpu UploadChunk(Chunk chunk)
    {
        ChunkMesh mesh = ChunkMeshBuilder.Build(chunk, _world);

        int vao = GL.GenVertexArray();
        GL.BindVertexArray(vao);

        int vbo = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
        GL.BufferData(BufferTarget.ArrayBuffer,
                      mesh.Vertices.Length * sizeof(float),
                      mesh.Vertices,
                      BufferUsageHint.StaticDraw);

        int ebo = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.ElementArrayBuffer, ebo);
        GL.BufferData(BufferTarget.ElementArrayBuffer,
                      mesh.Indices.Length * sizeof(uint),
                      mesh.Indices,
                      BufferUsageHint.StaticDraw);

        // Vertex layout: 5 floats per vertex (xyz position + uv texcoord), stride = 20 bytes.
        // Location 0 — position (3 floats, byte offset 0).
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 5 * sizeof(float), 0);
        GL.EnableVertexAttribArray(0);

        // Location 1 — UV texcoord (2 floats, byte offset 12).
        GL.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, 5 * sizeof(float), 3 * sizeof(float));
        GL.EnableVertexAttribArray(1);

        GL.BindVertexArray(0);
        return new ChunkGpu(vao, vbo, ebo, mesh.Indices.Length);
    }

    /// <summary>Releases the three OpenGL objects owned by a <see cref="ChunkGpu"/>.</summary>
    private static void DeleteChunkGpu(ChunkGpu gpu)
    {
        GL.DeleteVertexArray(gpu.Vao);
        GL.DeleteBuffer(gpu.Vbo);
        GL.DeleteBuffer(gpu.Ebo);
    }

    protected override void OnUpdateFrame(FrameEventArgs args)
    {
        base.OnUpdateFrame(args);

        if (KeyboardState.IsKeyDown(Keys.Escape))
        {
            CursorState = CursorState.Normal;
            Close();
        }

        float dt = (float)args.Time;
        _camera.ProcessKeyboard(KeyboardState, dt);

        // Mouse look: compute the delta from last frame's position.
        // On first frame, snap without a jump.
        var mouse = MouseState;
        if (_firstMove)
        {
            _lastMousePos = new Vector2(mouse.X, mouse.Y);
            _firstMove = false;
        }
        else
        {
            var delta = new Vector2(mouse.X - _lastMousePos.X, mouse.Y - _lastMousePos.Y);
            _lastMousePos = new Vector2(mouse.X, mouse.Y);
            _camera.ProcessMouseMovement(delta);
        }

        // -----------------------------------------------------------------------
        // Chunk streaming: load chunks entering the render radius, unload those
        // that moved out of range, and re-mesh new arrivals plus their neighbours
        // so cross-chunk seam faces are properly culled.
        // -----------------------------------------------------------------------
        _world.Update(_camera.Position, out var added, out var removed);

        foreach (var key in removed)
        {
            if (_chunkGpuData.TryGetValue(key, out var gpu))
            {
                DeleteChunkGpu(gpu);
                _chunkGpuData.Remove(key);
            }
        }

        if (added.Count > 0)
        {
            // Include the four cardinal neighbours of each new chunk so their
            // boundary faces (previously exposed toward the empty slot) get re-culled.
            var toRebuild = new HashSet<Vector2i>(added);
            foreach (var key in added)
            {
                toRebuild.Add(new Vector2i(key.X - 1, key.Y));
                toRebuild.Add(new Vector2i(key.X + 1, key.Y));
                toRebuild.Add(new Vector2i(key.X, key.Y - 1));
                toRebuild.Add(new Vector2i(key.X, key.Y + 1));
            }

            foreach (var key in toRebuild)
            {
                if (!_world.Chunks.TryGetValue(key, out var chunk)) continue;
                if (_chunkGpuData.TryGetValue(key, out var old))
                    DeleteChunkGpu(old);
                _chunkGpuData[key] = UploadChunk(chunk);
            }
        }
    }

    protected override void OnRenderFrame(FrameEventArgs args)
    {
        base.OnRenderFrame(args);

        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

        _shader.Use();

        // Bind the atlas to texture unit 0 and tell the shader which unit to sample.
        // TextureUnit.Texture0 activates slot 0; the uniform value 0 matches that slot.
        _atlas.Use(TextureUnit.Texture0);
        _shader.SetInt("uTexture", 0);

        // Build the Model matrix: the chunk's local block coordinates are directly in
        // world-space since chunk Position is (0,0,0).  In Phase 7 this becomes a
        // per-chunk translation: Matrix4.CreateTranslation(chunk.Position * Chunk.Size).
        var view = _camera.GetViewMatrix();
        var projection = _camera.GetProjectionMatrix();

        // Upload view + projection once — shared by all chunk draw calls this frame.
        _shader.SetMatrix4("view", ref view);
        _shader.SetMatrix4("projection", ref projection);

        // Draw each loaded chunk with its own world-space translation matrix.
        foreach (var (key, gpu) in _chunkGpuData)
        {
            if (gpu.IndexCount == 0) continue; // All-air chunk — nothing to submit.

            if (!_world.Chunks.TryGetValue(key, out var chunk)) continue;

            // Translate the mesh (local coords 0..31) to the chunk's world position.
            var model = Matrix4.CreateTranslation(
                chunk.Position.X * Chunk.Size,
                0f,
                chunk.Position.Z * Chunk.Size);
            _shader.SetMatrix4("model", ref model);

            GL.BindVertexArray(gpu.Vao);
            GL.DrawElements(PrimitiveType.Triangles, gpu.IndexCount, DrawElementsType.UnsignedInt, 0);
        }

        GL.BindVertexArray(0);
        SwapBuffers();
    }

    protected override void OnResize(ResizeEventArgs e)
    {
        base.OnResize(e);
        GL.Viewport(0, 0, e.Width, e.Height);
        // Keep the projection aspect ratio in sync with the window.
        _camera?.SetAspectRatio(e.Width / (float)e.Height);
    }

    protected override void OnUnload()
    {
        base.OnUnload();

        GL.BindVertexArray(0);
        foreach (var gpu in _chunkGpuData.Values)
            DeleteChunkGpu(gpu);
        _chunkGpuData.Clear();

        _atlas.Dispose();
        _shader.Dispose();
    }
}
