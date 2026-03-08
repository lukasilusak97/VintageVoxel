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

    // --- Phase 9: ImGui debug dashboard ---
    private ImGuiController _imgui = null!;
    private DebugWindow _debugWindow = null!;
    private ChunkBorderRenderer _borders = null!;
    // Whether the debug overlay is currently open (F3 toggles).
    private bool _debugVisible = false;
    // Set to true when the chunk set changes so border geometry is rebuilt.
    private bool _bordersDirty = true;

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

        // -----------------------------------------------------------------------
        // Phase 9: Initialise ImGui backend, debug window and border renderer.
        // ImGuiController must be created after the GL context exists (OnLoad).
        // -----------------------------------------------------------------------
        _imgui = new ImGuiController(Size.X, Size.Y);
        _debugWindow = new DebugWindow();
        _borders = new ChunkBorderRenderer();
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

    /// <summary>
    /// Re-meshes and re-uploads the chunk at <paramref name="key"/>, replacing any
    /// existing GPU data.  Does nothing if the chunk is not loaded.
    /// </summary>
    private void RebuildChunk(Vector2i key)
    {
        if (!_world.Chunks.TryGetValue(key, out var chunk)) return;
        if (_chunkGpuData.TryGetValue(key, out var old)) DeleteChunkGpu(old);
        _chunkGpuData[key] = UploadChunk(chunk);
    }

    /// <summary>
    /// Rebuilds the chunk that owns the world block at (wx, _, wz) plus any
    /// immediately neighbouring chunks whose boundary faces may have changed.
    /// </summary>
    private void RebuildAffectedChunks(int wx, int wy, int wz)
    {
        int cx = (int)MathF.Floor((float)wx / Chunk.Size);
        int cz = (int)MathF.Floor((float)wz / Chunk.Size);
        RebuildChunk(new Vector2i(cx, cz));

        // If the modified block sits on a chunk boundary, the adjacent chunk's
        // exposed faces change too — rebuild it so seams stay watertight.
        int lx = wx - cx * Chunk.Size;
        int lz = wz - cz * Chunk.Size;
        if (lx == 0) RebuildChunk(new Vector2i(cx - 1, cz));
        if (lx == Chunk.Size - 1) RebuildChunk(new Vector2i(cx + 1, cz));
        if (lz == 0) RebuildChunk(new Vector2i(cx, cz - 1));
        if (lz == Chunk.Size - 1) RebuildChunk(new Vector2i(cx, cz + 1));
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
        _camera.PhysicsUpdate(_world, KeyboardState, dt);

        // Mouse look: only active when cursor is grabbed (debug overlay closed).
        if (CursorState == CursorState.Grabbed)
        {
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
        }
        else
        {
            // Reset so there is no jump when cursor is re-grabbed.
            _firstMove = true;
        }

        // Feed input into ImGui every frame (keeps its state consistent whether
        // the overlay is visible or not).
        _imgui.Update(this, dt);

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
        if (removed.Count > 0) _bordersDirty = true;

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
            _bordersDirty = true;
        }
    }

    protected override void OnRenderFrame(FrameEventArgs args)
    {
        base.OnRenderFrame(args);

        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

        // --- Apply debug render-mode toggles ---
        if (_debugWindow.WireframeMode)
            GL.PolygonMode(MaterialFace.FrontAndBack, PolygonMode.Line);

        _shader.Use();

        // Bind the atlas to texture unit 0 and tell the shader which unit to sample.
        _atlas.Use(TextureUnit.Texture0);
        _shader.SetInt("uTexture", 0);
        _shader.SetInt("uNoTexture", _debugWindow.NoTextures ? 1 : 0);

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

            var model = Matrix4.CreateTranslation(
                chunk.Position.X * Chunk.Size,
                0f,
                chunk.Position.Z * Chunk.Size);
            _shader.SetMatrix4("model", ref model);

            GL.BindVertexArray(gpu.Vao);
            GL.DrawElements(PrimitiveType.Triangles, gpu.IndexCount, DrawElementsType.UnsignedInt, 0);
        }

        GL.BindVertexArray(0);

        // Restore fill mode before drawing debug overlays and ImGui.
        if (_debugWindow.WireframeMode)
            GL.PolygonMode(MaterialFace.FrontAndBack, PolygonMode.Fill);

        // --- Chunk border overlay ---
        if (_debugWindow.ShowChunkBorders)
        {
            // Lazily rebuild border geometry when the chunk set changes.
            if (_bordersDirty)
            {
                _borders.UpdateGeometry(_chunkGpuData.Keys);
                _bordersDirty = false;
            }
            _borders.Render(ref view, ref projection);
        }

        // --- ImGui debug dashboard ---
        if (_debugVisible)
        {
            _debugWindow.Draw(
                fps: (float)(1.0 / args.Time),
                frameTimeMs: (float)(args.Time * 1000.0),
                playerPos: _camera.Position,
                chunksLoaded: _chunkGpuData.Count,
                creativeMode: _camera.CreativeMode);
        }
        _imgui.Render(); // Render the ImGui frame (empty if overlay is hidden).

        SwapBuffers();
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);

        // Guard: ignore clicks that arrive before OnLoad has finished.
        if (_camera is null || _world is null) return;

        if (e.Button == MouseButton.Left)
        {
            // Left click — break the targeted block (replace with Air).
            var hit = Raycaster.Cast(_camera.Position, _camera.Front, _world);
            if (hit.Hit)
            {
                _world.SetBlock(hit.BlockPos.X, hit.BlockPos.Y, hit.BlockPos.Z, Block.Air);
                RebuildAffectedChunks(hit.BlockPos.X, hit.BlockPos.Y, hit.BlockPos.Z);
            }
        }
        else if (e.Button == MouseButton.Right)
        {
            // Right click — place a Stone block on the face the player is looking at.
            var hit = Raycaster.Cast(_camera.Position, _camera.Front, _world);
            if (hit.Hit)
            {
                var place = hit.BlockPos + hit.Normal;
                _world.SetBlock(place.X, place.Y, place.Z,
                    new Block { Id = 2, IsTransparent = false }); // Stone
                RebuildAffectedChunks(place.X, place.Y, place.Z);
            }
        }
    }

    protected override void OnKeyDown(KeyboardKeyEventArgs e)
    {
        base.OnKeyDown(e);

        // F3 — toggle the debug overlay.
        // When visible: release cursor so ImGui checkboxes are interactive.
        // When hidden:  grab cursor so the FPS camera works again.
        if (e.Key == Keys.F3)
        {
            _debugVisible = !_debugVisible;
            CursorState = _debugVisible ? CursorState.Normal : CursorState.Grabbed;
        }

        // F — toggle Creative / Survival mode (Phase 10).
        if (e.Key == Keys.F)
        {
            _camera.CreativeMode = !_camera.CreativeMode;
            // Reset vertical velocity so there is no launch impulse on switch.
            _camera.Velocity = Vector3.Zero;
        }
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        // Forward typed characters to ImGui so text fields work correctly.
        _imgui?.PressChar((uint)e.Unicode);
    }

    protected override void OnResize(ResizeEventArgs e)
    {
        base.OnResize(e);
        GL.Viewport(0, 0, e.Width, e.Height);
        _camera?.SetAspectRatio(e.Width / (float)e.Height);
        _imgui?.WindowResized(e.Width, e.Height);
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
        _borders.Dispose();
        _imgui.Dispose();
    }
}
