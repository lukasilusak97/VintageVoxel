using ImGuiNET;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using StbImageSharp;

namespace VintageVoxel;

/// <summary>
/// The main game window. Inherits from GameWindow which manages the OS window,
/// the OpenGL context, and the game loop (Update + Render at fixed/variable rates).
/// </summary>
public class Game : GameWindow
{
    // OpenGL resource bundle for one chunk — managed GPU-side per loaded chunk.
    private readonly record struct ChunkGpu(int Vao, int Vbo, int Ebo, int IndexCount);

    // OpenGL resource bundle for one model mesh — cached per item ID.
    private readonly record struct ModelGpu(int Vao, int Vbo, int Ebo, int IndexCount, int TexHandle);

    private readonly Dictionary<Vector2i, ChunkGpu> _chunkGpuData = new();
    private readonly Dictionary<int, ModelGpu> _modelGpu = new();

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

    // --- Phase 14: Persistence ---
    // Folder that holds per-chunk .bin files (one file per loaded chunk).
    private readonly string _savePath = WorldPersistence.DefaultSavePath;
    // Last save/load status shown in the debug overlay.
    private string? _lastSaveStatus;

    // --- Phase 15: Game State Machine ---
    private GameState _gameState = GameState.MainMenu;

    // --- Phase 16: Inventory ---
    // 10-slot hotbar; pre-seeded with one Dirt and one Stone stack for easy testing.
    private readonly Inventory _inventory = new(Inventory.HotbarSize);

    // --- Phase 17: HUD ---
    private HUDRenderer _hud = null!;

    // --- Phase 18: Dropped item entities ---
    private readonly List<EntityItem> _entityItems = new();
    private EntityItemRenderer _entityRenderer = null!;
    // Reusable 1-element array passed to EntityItemRenderer for hotbar previews.
    private readonly EntityItem[] _hudSlot = new EntityItem[1];

    // --- Placed model blocks (torches, etc.) tracked outside the chunk mesh ---
    // Keyed by world block position; value is a static entity used only for rendering.
    private readonly Dictionary<Vector3i, EntityItem> _placedModels = new();

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

        // Start in the main menu — cursor is free until the player clicks Play.

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

        // Phase 14: replace freshly-generated chunks with any saved counterparts.
        // We do this BEFORE lighting so BFS runs on the restored block data.
        foreach (var key in initial)
        {
            if (WorldPersistence.TryLoadChunk(_savePath, key, out Chunk? saved))
                _world.ReplaceChunk(key, saved);
        }

        // Compute lighting for all initially loaded chunks before uploading geometry.
        // All chunks are in the dictionary so cross-chunk BFS works correctly.
        LightEngine.PropagateSunlight(_world);
        foreach (var key in initial)
            _chunkGpuData[key] = UploadChunk(_world.Chunks[key]);

        // Scan initial chunks for any MODEL blocks saved to disk (e.g. placed torches).
        foreach (var key in initial)
            if (_world.Chunks.TryGetValue(key, out var ic)) ScanChunkForPlacedModels(ic);

        // -----------------------------------------------------------------------
        // Phase 9: Initialise ImGui backend, debug window and border renderer.
        // ImGuiController must be created after the GL context exists (OnLoad).
        // -----------------------------------------------------------------------
        _imgui = new ImGuiController(Size.X, Size.Y);
        _debugWindow = new DebugWindow();
        _borders = new ChunkBorderRenderer();

        // Pre-seed the hotbar from items.json so all registered items are available.
        // Resolve relative to the executable directory so the path works regardless
        // of the working directory (project root vs. bin/Debug/net8.0 during debugging).
        ItemRegistry.Load(Path.Combine(AppContext.BaseDirectory, "Assets", "items.json"));
        var torch = ItemRegistry.All.Values.FirstOrDefault(i =>
            i.Name.Equals("torch", StringComparison.OrdinalIgnoreCase));
        if (torch != null)
            _inventory.AddItem(torch, 1);

        // Phase 17: HUD renderer (crosshair + hotbar).
        // Use FramebufferSize (physical pixels) rather than ClientSize/Size so the
        // ortho projection matches the actual GL viewport on HiDPI displays.
        _hud = new HUDRenderer(FramebufferSize.X, FramebufferSize.Y);

        // Phase 18: renderer for dropped item entities.
        _entityRenderer = new EntityItemRenderer();
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

        // Vertex layout: 7 floats per vertex (xyz position + uv texcoord + light + ao), stride = 28 bytes.
        // Location 0 — position (3 floats, byte offset 0).
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 7 * sizeof(float), 0);
        GL.EnableVertexAttribArray(0);

        // Location 1 — UV texcoord (2 floats, byte offset 12).
        GL.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, 7 * sizeof(float), 3 * sizeof(float));
        GL.EnableVertexAttribArray(1);

        // Location 2 — light level (1 float, byte offset 20).
        GL.VertexAttribPointer(2, 1, VertexAttribPointerType.Float, false, 7 * sizeof(float), 5 * sizeof(float));
        GL.EnableVertexAttribArray(2);

        // Location 3 — ambient occlusion (1 float, byte offset 24).
        GL.VertexAttribPointer(3, 1, VertexAttribPointerType.Float, false, 7 * sizeof(float), 6 * sizeof(float));
        GL.EnableVertexAttribArray(3);

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
    /// Uploads a <see cref="ModelMesh"/> to the GPU (once) and caches the result.
    /// Returns the cached entry on subsequent calls for the same item ID.
    /// </summary>
    private ModelGpu GetOrCreateModelGpu(Item item)
    {
        if (_modelGpu.TryGetValue(item.Id, out var cached)) return cached;

        var mesh = item.Mesh!;

        int vao = GL.GenVertexArray();
        GL.BindVertexArray(vao);

        int vbo = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
        GL.BufferData(BufferTarget.ArrayBuffer,
                      mesh.Vertices.Length * sizeof(float),
                      mesh.Vertices, BufferUsageHint.StaticDraw);

        int ebo = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.ElementArrayBuffer, ebo);
        GL.BufferData(BufferTarget.ElementArrayBuffer,
                      mesh.Indices.Length * sizeof(uint),
                      mesh.Indices, BufferUsageHint.StaticDraw);

        // Same 7-float layout as the chunk shader.
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 7 * sizeof(float), 0);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, 7 * sizeof(float), 3 * sizeof(float));
        GL.EnableVertexAttribArray(1);
        GL.VertexAttribPointer(2, 1, VertexAttribPointerType.Float, false, 7 * sizeof(float), 5 * sizeof(float));
        GL.EnableVertexAttribArray(2);
        GL.VertexAttribPointer(3, 1, VertexAttribPointerType.Float, false, 7 * sizeof(float), 6 * sizeof(float));
        GL.EnableVertexAttribArray(3);

        GL.BindVertexArray(0);

        // Upload the model's own texture (PNG bytes decoded to RGBA).
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

        var entry = new ModelGpu(vao, vbo, ebo, mesh.Indices.Length, texHandle);
        _modelGpu[item.Id] = entry;
        return entry;
    }

    /// <summary>
    /// Renders all entries in <see cref="_placedModels"/> using their <see cref="ModelMesh"/>
    /// geometry and per-model texture, then rebinds the atlas for subsequent draw calls.
    /// </summary>
    private void RenderPlacedModels()
    {
        GL.Disable(EnableCap.CullFace);

        foreach (var (blockPos, entity) in _placedModels)
        {
            var item = entity.Item;
            if (item.Mesh is null) continue; // fallback: skip items with no mesh

            ModelGpu mg = GetOrCreateModelGpu(item);

            // Temporarily bind the model's own texture (or fall back to atlas if none).
            if (mg.TexHandle != 0)
            {
                GL.ActiveTexture(TextureUnit.Texture0);
                GL.BindTexture(TextureTarget.Texture2D, mg.TexHandle);
            }

            // Minecraft model coords are 0-16; scale to one-block (0-1) world space.
            var model = Matrix4.CreateScale(1f / 16f) *
                        Matrix4.CreateTranslation(blockPos.X, blockPos.Y, blockPos.Z);
            _shader.SetMatrix4("model", ref model);

            GL.BindVertexArray(mg.Vao);
            GL.DrawElements(PrimitiveType.Triangles, mg.IndexCount,
                            DrawElementsType.UnsignedInt, 0);
        }

        GL.BindVertexArray(0);
        GL.Enable(EnableCap.CullFace);

        // Rebind the atlas so the rest of the frame uses the correct texture.
        _atlas.Use(TextureUnit.Texture0);
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

    /// <summary>
    /// Registers a MODEL-type block at <paramref name="blockPos"/> as a placed model
    /// to be rendered each frame without physics or pickup.
    /// </summary>
    private void AddPlacedModel(Vector3i blockPos, Item item)
    {
        // Position so the icon centre lands at the block centre (accounting for HoverHeight).
        var pos = new Vector3(blockPos.X + 0.5f,
                              blockPos.Y + 0.5f - EntityItem.HoverHeight,
                              blockPos.Z + 0.5f);
        var entity = new EntityItem(item, 1, pos);
        entity.SpinAngle = 0f;           // static — no spin
        entity.PickupCooldown = float.MaxValue; // never picked up via proximity
        _placedModels[blockPos] = entity;
    }

    /// <summary>
    /// Scans <paramref name="chunk"/> for transparent MODEL blocks and registers
    /// each as a placed model.  Called after every initial or streamed chunk load.
    /// </summary>
    private void ScanChunkForPlacedModels(Chunk chunk)
    {
        for (int z = 0; z < Chunk.Size; z++)
            for (int y = 0; y < Chunk.Size; y++)
                for (int x = 0; x < Chunk.Size; x++)
                {
                    ref var block = ref chunk.GetBlock(x, y, z);
                    if (block.IsEmpty || !block.IsTransparent) continue;
                    var item = ItemRegistry.Get(block.Id);
                    if (item?.Type != ItemType.Model) continue;
                    int wx = chunk.Position.X * Chunk.Size + x;
                    int wz = chunk.Position.Z * Chunk.Size + z;
                    AddPlacedModel(new Vector3i(wx, y, wz), item);
                }
    }

    /// <summary>
    /// Spawns a 1-count dropped item entity at the centre of the broken block.
    /// Does nothing if the block ID has no corresponding item in the registry.
    /// </summary>
    private void SpawnBlockDrop(ushort blockId, Vector3i blockPos)
    {
        var item = ItemRegistry.Get(blockId);
        if (item == null) return;
        var spawnPos = new Vector3(blockPos.X + 0.5f, blockPos.Y + 0.5f, blockPos.Z + 0.5f);
        var impulse = new Vector3(0f, 3f, 0f);
        _entityItems.Add(new EntityItem(item, 1, spawnPos, impulse));
    }

    /// <summary>
    /// Renders each occupied hotbar slot as a spinning 3-D mini item by reusing
    /// <see cref="EntityItemRenderer"/>.  A tiny GL viewport is set per slot so the
    /// item fills only that region; depth test is disabled to avoid z-fighting with
    /// the world geometry already in the depth buffer.
    /// </summary>
    private void RenderHotbarItems3D(int fbWidth, int fbHeight)
    {
        int count = Inventory.HotbarSize;
        float totalWidth = count * HUDRenderer.SlotSize + (count - 1) * HUDRenderer.SlotGap;
        float x0 = (fbWidth - totalWidth) * 0.5f;

        // HUDRenderer uses a top-left-origin pixel space, OpenGL viewport uses bottom-left.
        // HUD y0 (top-left) = fbHeight - HotbarBottomPad - SlotSize
        // GL y0  (bot-left) = fbHeight - HUD_y0 - SlotSize = HotbarBottomPad
        int glBaseY = HUDRenderer.HotbarBottomPad;
        int slotPx = HUDRenderer.SlotSize;
        const int pad = 4;
        int innerSize = slotPx - pad * 2;

        // Mini-view: tight perspective into a square slot.
        var miniProj = Matrix4.CreatePerspectiveFieldOfView(
            MathHelper.DegreesToRadians(40f), 1f, 0.01f, 20f);
        // Isometric-ish camera: above and slightly in front, looking at the item centre.
        var eye = new Vector3(0.6f, 0.5f, 1.0f);
        var target = new Vector3(0f, 0.15f, 0f);
        var miniView = Matrix4.LookAt(eye, target, Vector3.UnitY);

        _shader.Use();
        _atlas.Use(TextureUnit.Texture0);
        _shader.SetInt("uTexture", 0);
        _shader.SetInt("uNoTexture", 0);
        _shader.SetMatrix4("projection", ref miniProj);
        _shader.SetMatrix4("view", ref miniView);

        GL.Disable(EnableCap.DepthTest);
        GL.Disable(EnableCap.CullFace);
        GL.Enable(EnableCap.ScissorTest);

        Func<Item, (int Vao, int IndexCount, int TexHandle)?> gpuGetter = item =>
        {
            if (item.Mesh == null) return null;
            var mg = GetOrCreateModelGpu(item);
            return (mg.Vao, mg.IndexCount, mg.TexHandle);
        };

        for (int i = 0; i < count; i++)
        {
            var stack = _inventory.Slots[i];
            if (stack.IsEmpty || stack.Item == null) continue;

            int vx = (int)(x0 + i * (HUDRenderer.SlotSize + HUDRenderer.SlotGap)) + pad;
            int vy = glBaseY + pad;

            GL.Scissor(vx, vy, innerSize, innerSize);
            GL.Viewport(vx, vy, innerSize, innerSize);

            _hudSlot[0] = new EntityItem(stack.Item, stack.Count, Vector3.Zero)
            {
                SpinAngle = MathF.PI / 4f  // fixed 45° angle — no spin
            };
            _entityRenderer.Render(_hudSlot, _shader, _atlas.Handle, gpuGetter);
        }

        // Restore full-screen viewport and 3-D GL state.
        GL.Disable(EnableCap.ScissorTest);
        GL.Enable(EnableCap.DepthTest);
        GL.Enable(EnableCap.CullFace);
        GL.Viewport(0, 0, fbWidth, fbHeight);
        _atlas.Use(TextureUnit.Texture0);
    }

    /// <summary>
    /// Places the block represented by the currently held hotbar item at world
    /// position (wx, wy, wz).  If the hotbar is empty, falls back to Stone (ID 2).
    /// MODEL-type items are not placeable as voxel blocks and are silently ignored.
    /// </summary>
    private void PlaceHeldBlock(int wx, int wy, int wz)
    {
        ref var held = ref _inventory.HeldStack;

        if (!held.IsEmpty && held.Item!.Type == ItemType.Model)
        {
            // MODEL items are stored as transparent placeholder blocks so the chunk
            // mesher skips them; the visual is provided by _placedModels rendering.
            _world.SetBlock(wx, wy, wz, new Block { Id = (ushort)held.Item.Id, IsTransparent = true });
            LightEngine.UpdateAtBlock(wx, wy, wz, _world);
            AddPlacedModel(new Vector3i(wx, wy, wz), held.Item);
            RebuildAffectedChunks(wx, wy, wz);
            return;
        }

        ushort id = held.IsEmpty ? (ushort)2 : (ushort)held.Item!.Id;
        _world.SetBlock(wx, wy, wz, new Block { Id = id, IsTransparent = false });
        LightEngine.UpdateAtBlock(wx, wy, wz, _world);
        RebuildAffectedChunks(wx, wy, wz);
    }

    protected override void OnUpdateFrame(FrameEventArgs args)
    {
        base.OnUpdateFrame(args);

        float dt = (float)args.Time;

        // Feed input into ImGui every frame (keeps its state consistent whether
        // the overlay is visible or not).
        Profiler.Begin("ImGui Update");
        _imgui.Update(this, dt);
        Profiler.End("ImGui Update");

        // Physics, mouse look, and chunk streaming are gated on the Playing state.
        // When Paused or in the MainMenu the world is frozen.
        if (_gameState != GameState.Playing)
            return;

        Profiler.Begin("Physics");
        _camera.PhysicsUpdate(_world, KeyboardState, dt);
        Profiler.End("Physics");

        // --- Phase 18: Update dropped item entities + proximity pickup ---
        Profiler.Begin("Entities");
        for (int i = _entityItems.Count - 1; i >= 0; i--)
        {
            _entityItems[i].Update(_world, dt);
            if (_entityItems[i].PickupCooldown <= 0f &&
                (_camera.FeetPosition - _entityItems[i].Position).Length < EntityItem.PickupRadius)
            {
                _inventory.AddItem(_entityItems[i].Item, _entityItems[i].Count);
                _entityItems.RemoveAt(i);
            }
        }
        Profiler.End("Entities");

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

        // -----------------------------------------------------------------------
        // Chunk streaming: load chunks entering the render radius, unload those
        // that moved out of range, and re-mesh new arrivals plus their neighbours
        // so cross-chunk seam faces are properly culled.
        // -----------------------------------------------------------------------
        Profiler.Begin("Chunk Stream: World Update");
        _world.Update(_camera.Position, out var added, out var removed);
        Profiler.End("Chunk Stream: World Update");

        Profiler.Begin("Chunk Stream: Unload");
        foreach (var key in removed)
        {
            // Evict placed models that belong to the unloaded chunk.
            int minX = key.X * Chunk.Size, maxX = (key.X + 1) * Chunk.Size;
            int minZ = key.Y * Chunk.Size, maxZ = (key.Y + 1) * Chunk.Size;
            foreach (var pos in _placedModels.Keys
                .Where(p => p.X >= minX && p.X < maxX && p.Z >= minZ && p.Z < maxZ)
                .ToList())
                _placedModels.Remove(pos);

            if (_chunkGpuData.TryGetValue(key, out var gpu))
            {
                DeleteChunkGpu(gpu);
                _chunkGpuData.Remove(key);
            }
        }
        if (removed.Count > 0) _bordersDirty = true;
        Profiler.End("Chunk Stream: Unload");

        if (added.Count > 0)
        {
            // Phase 14: replace freshly-generated streaming chunks with saved data.
            Profiler.Begin("Chunk Stream: Disk Load");
            foreach (var key in added)
            {
                if (WorldPersistence.TryLoadChunk(_savePath, key, out Chunk? saved))
                    _world.ReplaceChunk(key, saved);
            }
            Profiler.End("Chunk Stream: Disk Load");

            // Compute lighting for the new chunks plus their immediate neighbours
            // (seam-accurate BFS needs the neighbour data available first).
            Profiler.Begin("Chunk Stream: Lighting");
            foreach (var key in added)
            {
                if (_world.Chunks.TryGetValue(key, out var newChunk))
                    LightEngine.ComputeChunk(newChunk, _world);
            }
            Profiler.End("Chunk Stream: Lighting");

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

            Profiler.Begin("Chunk Stream: Mesh Upload");
            foreach (var key in toRebuild)
            {
                if (!_world.Chunks.TryGetValue(key, out var chunk)) continue;
                if (_chunkGpuData.TryGetValue(key, out var old))
                    DeleteChunkGpu(old);
                _chunkGpuData[key] = UploadChunk(chunk);
            }
            Profiler.End("Chunk Stream: Mesh Upload");

            // Scan newly added chunks for MODEL blocks loaded from disk.
            foreach (var key in added)
                if (_world.Chunks.TryGetValue(key, out var sc)) ScanChunkForPlacedModels(sc);

            _bordersDirty = true;
        }
    }

    protected override void OnRenderFrame(FrameEventArgs args)
    {
        base.OnRenderFrame(args);

        Profiler.Begin("GL Clear");
        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        Profiler.End("GL Clear");

        // --- Apply debug render-mode toggles ---
        if (_debugWindow.WireframeMode)
            GL.PolygonMode(MaterialFace.FrontAndBack, PolygonMode.Line);

        _shader.Use();

        // Bind the atlas to texture unit 0 and tell the shader which unit to sample.
        _atlas.Use(TextureUnit.Texture0);
        _shader.SetInt("uTexture", 0);
        // uNoTexture: 0 = textured, 1 = white (no texture), 2 = AO+light greyscale debug
        int noTexMode = _debugWindow.LightingDebug ? 2 : (_debugWindow.NoTextures ? 1 : 0);
        _shader.SetInt("uNoTexture", noTexMode);

        var view = _camera.GetViewMatrix();
        var projection = _camera.GetProjectionMatrix();

        // Upload view + projection once — shared by all chunk draw calls this frame.
        _shader.SetMatrix4("view", ref view);
        _shader.SetMatrix4("projection", ref projection);

        // Build the view frustum once per frame.  Any chunk whose axis-aligned
        // bounding box is entirely outside the frustum is skipped — no draw call,
        // no vertex shader invocations, no wasted GPU time.
        var frustum = Frustum.FromViewProjection(view, projection);

        // Draw each loaded chunk with its own world-space translation matrix.
        // key.X = chunk X, key.Y = chunk Z (no need to look up the Chunk object).
        Profiler.Begin("Chunk Draw");
        foreach (var (key, gpu) in _chunkGpuData)
        {
            if (gpu.IndexCount == 0) continue; // All-air chunk — nothing to submit.

            // --- Phase 12: Frustum Culling ---
            // Each chunk occupies a Chunk.Size³ AABB.  Test it against the six
            // frustum planes before issuing the draw call.
            int wx = key.X * Chunk.Size;
            int wz = key.Y * Chunk.Size;
            if (!frustum.ContainsAabb(
                    new Vector3(wx, 0f, wz),
                    new Vector3(wx + Chunk.Size, Chunk.Size, wz + Chunk.Size)))
                continue;

            var model = Matrix4.CreateTranslation(wx, 0f, wz);
            _shader.SetMatrix4("model", ref model);

            GL.BindVertexArray(gpu.Vao);
            GL.DrawElements(PrimitiveType.Triangles, gpu.IndexCount, DrawElementsType.UnsignedInt, 0);
        }

        GL.BindVertexArray(0);
        Profiler.End("Chunk Draw");

        // --- Phase 18: Render dropped item entities (world-space, same shader) ---
        Profiler.Begin("Entity Render");
        _entityRenderer.Render(_entityItems, _shader, _atlas.Handle,
            item =>
            {
                if (item.Mesh == null) return null;
                var mg = GetOrCreateModelGpu(item);
                return (mg.Vao, mg.IndexCount, mg.TexHandle);
            });

        // Render statically placed MODEL blocks (torches, etc.) without physics/spin.
        if (_placedModels.Count > 0)
            RenderPlacedModels();
        Profiler.End("Entity Render");

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

        // --- Phase 17: 2-D HUD (crosshair + hotbar) — Playing state only ---
        Profiler.Begin("HUD");
        if (_gameState == GameState.Playing)
        {
            _hud.Render(_inventory, _atlas, FramebufferSize.X, FramebufferSize.Y);
            // Render hotbar item icons as 3-D spinning entities (same approach as world drops).
            RenderHotbarItems3D(FramebufferSize.X, FramebufferSize.Y);
            DrawHotbarCounts();
        }
        Profiler.End("HUD");

        // --- ImGui debug dashboard (Playing state only) ---
        if (_debugVisible && _gameState == GameState.Playing)
        {
            _debugWindow.Draw(
                fps: (float)(1.0 / args.Time),
                frameTimeMs: (float)(args.Time * 1000.0),
                playerPos: _camera.Position,
                chunksLoaded: _chunkGpuData.Count,
                creativeMode: _camera.CreativeMode,
                heldItem: _inventory.HeldStack,
                hotbarSlot: _inventory.SelectedSlot,
                saveStatus: _lastSaveStatus);
        }

        // --- Phase 15: State menus ---
        if (_gameState == GameState.MainMenu)
            DrawMainMenu();
        else if (_gameState == GameState.Paused)
            DrawPauseMenu();

        Profiler.Begin("ImGui");
        _imgui.Render(); // Render the ImGui frame (empty if overlay is hidden).
        Profiler.End("ImGui");

        Profiler.Begin("SwapBuffers");
        SwapBuffers();
        Profiler.End("SwapBuffers");
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);

        // Guard: ignore clicks that arrive before OnLoad has finished or when not playing.
        if (_camera is null || _world is null) return;
        if (_gameState != GameState.Playing) return;

        if (e.Button == MouseButton.Left)
        {
            // Left click — break the targeted block or remove a single sub-voxel.
            var hit = Raycaster.Cast(_camera.Position, _camera.Front, _world);
            if (hit.Hit)
            {
                if (hit.IsChiseled)
                {
                    // Remove the specific sub-voxel that was hit.
                    var chisel = _world.GetChiselData(
                        hit.BlockPos.X, hit.BlockPos.Y, hit.BlockPos.Z);
                    if (chisel != null)
                    {
                        chisel.Set(hit.SubVoxelPos.X, hit.SubVoxelPos.Y, hit.SubVoxelPos.Z, false);

                        // If the last sub-voxel was removed, convert the container back to Air.
                        if (!chisel.HasAnyFilled())
                        {
                            ushort chiseledId = _world.GetBlock(
                                hit.BlockPos.X, hit.BlockPos.Y, hit.BlockPos.Z).Id;
                            _world.SetBlock(hit.BlockPos.X, hit.BlockPos.Y, hit.BlockPos.Z,
                                Block.Air);
                            int cx = (int)MathF.Floor((float)hit.BlockPos.X / Chunk.Size);
                            int cz = (int)MathF.Floor((float)hit.BlockPos.Z / Chunk.Size);
                            if (_world.Chunks.TryGetValue(new Vector2i(cx, cz), out var ch))
                                ch.ChiseledBlocks.Remove(Chunk.Index(
                                    hit.BlockPos.X - cx * Chunk.Size,
                                    hit.BlockPos.Y,
                                    hit.BlockPos.Z - cz * Chunk.Size));
                            LightEngine.UpdateAtBlock(
                                hit.BlockPos.X, hit.BlockPos.Y, hit.BlockPos.Z, _world);
                            SpawnBlockDrop(chiseledId, hit.BlockPos);
                        }
                    }
                }
                else
                {
                    ushort brokenId = _world.GetBlock(
                        hit.BlockPos.X, hit.BlockPos.Y, hit.BlockPos.Z).Id;
                    _world.SetBlock(hit.BlockPos.X, hit.BlockPos.Y, hit.BlockPos.Z, Block.Air);
                    LightEngine.UpdateAtBlock(hit.BlockPos.X, hit.BlockPos.Y, hit.BlockPos.Z, _world);
                    _placedModels.Remove(hit.BlockPos);
                    SpawnBlockDrop(brokenId, hit.BlockPos);
                }
                RebuildAffectedChunks(hit.BlockPos.X, hit.BlockPos.Y, hit.BlockPos.Z);
            }
        }
        else if (e.Button == MouseButton.Right)
        {
            // Right click — place or restore a sub-voxel, or place a Stone block.
            var hit = Raycaster.Cast(_camera.Position, _camera.Front, _world);
            if (hit.Hit)
            {
                if (hit.IsChiseled)
                {
                    var newSub = hit.SubVoxelPos + hit.SubNormal;
                    var chisel = _world.GetChiselData(
                        hit.BlockPos.X, hit.BlockPos.Y, hit.BlockPos.Z);
                    if (chisel != null && ChiseledBlockData.InBounds(newSub.X, newSub.Y, newSub.Z))
                    {
                        // Fill the adjacent sub-voxel within the same chiseled block.
                        chisel.Set(newSub.X, newSub.Y, newSub.Z, true);
                        RebuildAffectedChunks(hit.BlockPos.X, hit.BlockPos.Y, hit.BlockPos.Z);
                    }
                    else
                    {
                        // Adjacent position is outside the chiseled block — fall back to
                        // placing a block from the held hotbar item in the adjacent world position.
                        var place = hit.BlockPos + hit.Normal;
                        PlaceHeldBlock(place.X, place.Y, place.Z);
                    }
                }
                else
                {
                    var place = hit.BlockPos + hit.Normal;
                    PlaceHeldBlock(place.X, place.Y, place.Z);
                }
            }
        }
        else if (e.Button == MouseButton.Middle)
        {
            // Middle click — convert the targeted block into a chiseled container.
            // The block is replaced with Block.ChiseledId; a ChiseledBlockData
            // (all 4096 sub-voxels filled) is registered in the owning chunk.
            var hit = Raycaster.Cast(_camera.Position, _camera.Front, _world);
            if (hit.Hit && !hit.IsChiseled)
            {
                int wx = hit.BlockPos.X, wy = hit.BlockPos.Y, wz = hit.BlockPos.Z;
                ushort origId = _world.GetBlock(wx, wy, wz).Id;

                _world.SetBlock(wx, wy, wz,
                    new Block { Id = Block.ChiseledId, IsTransparent = false });

                int cx = (int)MathF.Floor((float)wx / Chunk.Size);
                int cz = (int)MathF.Floor((float)wz / Chunk.Size);
                if (_world.Chunks.TryGetValue(new Vector2i(cx, cz), out var chunk))
                    chunk.GetOrCreateChiseled(wx - cx * Chunk.Size, wy, wz - cz * Chunk.Size,
                                              origId);

                LightEngine.UpdateAtBlock(wx, wy, wz, _world);
                RebuildAffectedChunks(wx, wy, wz);
            }
        }
    }

    protected override void OnKeyDown(KeyboardKeyEventArgs e)
    {
        base.OnKeyDown(e);

        // ESC — state machine transitions.
        if (e.Key == Keys.Escape)
        {
            switch (_gameState)
            {
                case GameState.Playing:
                    TransitionToPaused();
                    break;
                case GameState.Paused:
                    TransitionToPlaying();
                    break;
                    // MainMenu: ESC does nothing (use the Quit button to exit).
            }
            return;
        }

        // F3 — toggle the debug overlay (Playing state only).
        // When visible: release cursor so ImGui checkboxes are interactive.
        // When hidden:  grab cursor so the FPS camera works again.
        if (e.Key == Keys.F3 && _gameState == GameState.Playing)
        {
            _debugVisible = !_debugVisible;
            CursorState = _debugVisible ? CursorState.Normal : CursorState.Grabbed;
        }

        // F — toggle Creative / Survival mode (Phase 10). Playing state only.
        if (e.Key == Keys.F && _gameState == GameState.Playing)
        {
            _camera.CreativeMode = !_camera.CreativeMode;
            // Reset vertical velocity so there is no launch impulse on switch.
            _camera.Velocity = Vector3.Zero;
        }

        // Ctrl+S — save all currently loaded chunks to disk. Playing state only.
        if (e.Key == Keys.S &&
            (e.Modifiers & OpenTK.Windowing.GraphicsLibraryFramework.KeyModifiers.Control) != 0 &&
            _gameState == GameState.Playing)
        {
            int count = WorldPersistence.SaveAll(_savePath, _world);
            _lastSaveStatus = $"Saved {count} chunk(s) at {DateTime.Now:HH:mm:ss}";
        }

        // Q — drop one item from the held stack into the world. Playing state only.
        if (e.Key == Keys.Q && _gameState == GameState.Playing)
        {
            ref var held = ref _inventory.HeldStack;
            if (!held.IsEmpty)
            {
                var dropItem = held.Item!;
                int removed = _inventory.RemoveItem(dropItem, 1);
                if (removed > 0)
                {
                    // Spawn the entity slightly in front of and above the camera
                    // with a small forward+upward impulse so it arcs away.
                    var spawnPos = _camera.Position + _camera.Front * 0.8f;
                    var impulse = _camera.Front * 5f + new Vector3(0f, 2f, 0f);
                    _entityItems.Add(new EntityItem(dropItem, removed, spawnPos, impulse));
                }
            }
        }
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);

        // Hotbar cycling — only when Playing and cursor is grabbed (FPS mode).
        if (_gameState == GameState.Playing && CursorState == CursorState.Grabbed)
        {
            // e.OffsetY > 0 → scroll up → go to the previous slot (cycle backwards).
            int delta = e.OffsetY > 0 ? -1 : 1;
            _inventory.ScrollHotbar(delta);
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
        // HUD uses physical framebuffer pixels for its ortho projection.
        _hud?.SetScreenSize(FramebufferSize.X, FramebufferSize.Y);
    }

    protected override void OnUnload()
    {
        base.OnUnload();

        // Phase 14: auto-save all loaded chunks when the game window closes.
        WorldPersistence.SaveAll(_savePath, _world);

        GL.BindVertexArray(0);
        foreach (var gpu in _chunkGpuData.Values)
            DeleteChunkGpu(gpu);
        _chunkGpuData.Clear();

        foreach (var mg in _modelGpu.Values)
        {
            GL.DeleteVertexArray(mg.Vao);
            GL.DeleteBuffer(mg.Vbo);
            GL.DeleteBuffer(mg.Ebo);
            if (mg.TexHandle != 0) GL.DeleteTexture(mg.TexHandle);
        }
        _modelGpu.Clear();

        _atlas.Dispose();
        _shader.Dispose();
        _borders.Dispose();
        _hud.Dispose();
        _entityRenderer.Dispose();
        _imgui.Dispose();
    }

    // -------------------------------------------------------------------------
    // Phase 15: State-machine transitions & ImGui menus
    // -------------------------------------------------------------------------

    /// <summary>
    /// Switches to <see cref="GameState.Playing"/>.
    /// Grabs the cursor (unless the debug overlay is open) and resets the
    /// mouse-delta accumulator so there's no camera jump on resume.
    /// </summary>
    private void TransitionToPlaying()
    {
        _gameState = GameState.Playing;
        _firstMove = true;
        // Respect the debug overlay: keep cursor free when F3 window is open.
        CursorState = _debugVisible ? CursorState.Normal : CursorState.Grabbed;
    }

    /// <summary>
    /// Switches to <see cref="GameState.Paused"/>.
    /// Releases the cursor so the player can interact with the pause menu.
    /// </summary>
    private void TransitionToPaused()
    {
        _gameState = GameState.Paused;
        CursorState = CursorState.Normal;
        _firstMove = true;
    }

    /// <summary>
    /// Overlays the item-stack count in the bottom-right corner of each occupied hotbar slot.
    /// Uses ImGui's foreground draw list so it composites on top of the OpenGL HUD pass.
    /// </summary>
    private void DrawHotbarCounts()
    {
        var drawList = ImGui.GetForegroundDrawList();
        var displaySize = ImGui.GetIO().DisplaySize;

        float slotSize = HUDRenderer.SlotSize;
        float slotGap = HUDRenderer.SlotGap;
        int slots = Inventory.HotbarSize;
        float totalW = slots * slotSize + (slots - 1) * slotGap;
        float x0 = (displaySize.X - totalW) * 0.5f;
        float y0 = displaySize.Y - HUDRenderer.HotbarBottomPad - slotSize;
        const float pad = 4f;

        for (int i = 0; i < slots; i++)
        {
            var stack = _inventory.Slots[i];
            if (stack.IsEmpty || stack.Count <= 1) continue;

            string label = stack.Count.ToString();
            var textSize = ImGui.CalcTextSize(label);
            float tx = x0 + i * (slotSize + slotGap) + slotSize - pad - textSize.X;
            float ty = y0 + slotSize - pad - textSize.Y;

            // Drop shadow for readability against any background.
            drawList.AddText(new System.Numerics.Vector2(tx + 1, ty + 1), 0xFF000000, label);
            // Bright white count.
            drawList.AddText(new System.Numerics.Vector2(tx, ty), 0xFFFFFFFF, label);
        }
    }

    /// <summary>
    /// Renders the centered main-menu ImGui overlay.
    /// Called from <see cref="OnRenderFrame"/> when state is <see cref="GameState.MainMenu"/>.
    /// </summary>
    private void DrawMainMenu()
    {
        var displaySize = ImGui.GetIO().DisplaySize;
        ImGui.SetNextWindowPos(
            new System.Numerics.Vector2(displaySize.X * 0.5f, displaySize.Y * 0.5f),
            ImGuiCond.Always,
            new System.Numerics.Vector2(0.5f, 0.5f));
        ImGui.SetNextWindowBgAlpha(0.92f);
        ImGui.Begin("##mainmenu",
            ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoDecoration |
            ImGuiWindowFlags.AlwaysAutoResize |
            ImGuiWindowFlags.NoSavedSettings);

        ImGui.TextColored(new System.Numerics.Vector4(0.4f, 0.9f, 1.0f, 1.0f), "VintageVoxel");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (ImGui.Button("Play", new System.Numerics.Vector2(220f, 40f)))
            TransitionToPlaying();

        ImGui.Spacing();

        if (ImGui.Button("Quit", new System.Numerics.Vector2(220f, 40f)))
            Close();

        ImGui.End();
    }

    /// <summary>
    /// Renders the centered pause-menu ImGui overlay.
    /// Called from <see cref="OnRenderFrame"/> when state is <see cref="GameState.Paused"/>.
    /// </summary>
    private void DrawPauseMenu()
    {
        var displaySize = ImGui.GetIO().DisplaySize;
        ImGui.SetNextWindowPos(
            new System.Numerics.Vector2(displaySize.X * 0.5f, displaySize.Y * 0.5f),
            ImGuiCond.Always,
            new System.Numerics.Vector2(0.5f, 0.5f));
        ImGui.SetNextWindowBgAlpha(0.92f);
        ImGui.Begin("##pausemenu",
            ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoDecoration |
            ImGuiWindowFlags.AlwaysAutoResize |
            ImGuiWindowFlags.NoSavedSettings);

        ImGui.TextColored(new System.Numerics.Vector4(1.0f, 0.85f, 0.3f, 1.0f), "— Paused —");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (ImGui.Button("Resume", new System.Numerics.Vector2(220f, 40f)))
            TransitionToPlaying();

        ImGui.Spacing();

        if (ImGui.Button("Save & Resume", new System.Numerics.Vector2(220f, 40f)))
        {
            int count = WorldPersistence.SaveAll(_savePath, _world);
            _lastSaveStatus = $"Saved {count} chunk(s) at {DateTime.Now:HH:mm:ss}";
            TransitionToPlaying();
        }

        ImGui.Spacing();

        if (ImGui.Button("Quit", new System.Numerics.Vector2(220f, 40f)))
            Close();

        ImGui.End();
    }
}

