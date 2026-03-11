using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using StbImageSharp;

namespace VintageVoxel.Rendering;

/// <summary>
/// Owns all chunk and model GPU resources and drives the 3-D render pass each frame.
/// Responsibilities: chunk mesh uploads, placed-model tracking, frustum culling,
/// entity rendering, hotbar 3-D preview, and the chunk-border debug overlay.
/// </summary>
public sealed class WorldRenderer : IDisposable
{
    // GPU handle bundle for one loaded item model.
    private readonly record struct ModelGpu(GpuMesh Mesh, int TexHandle);

    private readonly Dictionary<Vector2i, GpuMesh> _chunkGpuData = new();
    private readonly Dictionary<int, ModelGpu> _modelGpu = new();
    private readonly Dictionary<Vector3i, EntityItem> _placedModels = new();

    private readonly GpuResourceManager _gpu;
    private readonly World _world;
    private readonly Shader _shader;
    private readonly Texture _atlas;
    private readonly EntityItemRenderer _entityRenderer;
    private readonly Inventory _inventory;
    private readonly ChunkBorderRenderer _borders;

    // Reusable 1-element array for hotbar slot 3-D preview rendering.
    private readonly EntityItem[] _hudSlot = new EntityItem[1];

    /// <summary>Set to true whenever the chunk set changes so border geometry is rebuilt.</summary>
    public bool BordersDirty { get; set; } = true;

    /// <summary>Number of chunks currently holding GPU data.</summary>
    public int ChunkCount => _chunkGpuData.Count;

    public WorldRenderer(GpuResourceManager gpu, World world, Shader shader, Texture atlas,
                         EntityItemRenderer entityRenderer, Inventory inventory)
    {
        _gpu = gpu;
        _world = world;
        _shader = shader;
        _atlas = atlas;
        _entityRenderer = entityRenderer;
        _inventory = inventory;
        _borders = new ChunkBorderRenderer();
    }

    // -------------------------------------------------------------------------
    // Chunk GPU lifecycle
    // -------------------------------------------------------------------------

    /// <summary>Meshes <paramref name="chunk"/> and uploads it to the GPU.</summary>
    private GpuMesh UploadChunkGpu(Chunk chunk)
    {
        ChunkMesh mesh = ChunkMeshBuilder.Build(chunk, _world);
        return _gpu.UploadMesh(mesh.Vertices, mesh.Indices, 7);
    }

    /// <summary>
    /// Re-meshes and re-uploads the chunk at <paramref name="key"/>, replacing any
    /// existing GPU data. Does nothing if the chunk is not loaded.
    /// </summary>
    public void RebuildChunk(Vector2i key)
    {
        if (!_world.Chunks.TryGetValue(key, out var chunk)) return;
        if (_chunkGpuData.TryGetValue(key, out var old)) _gpu.Free(old);
        _chunkGpuData[key] = UploadChunkGpu(chunk);
    }

    /// <summary>Frees the GPU mesh for <paramref name="key"/> and removes it from the cache.</summary>
    public void TryFreeChunkGpu(Vector2i key)
    {
        if (!_chunkGpuData.TryGetValue(key, out var gpu)) return;
        _gpu.Free(gpu);
        _chunkGpuData.Remove(key);
    }

    /// <summary>
    /// Rebuilds the chunk owning the world block at (wx, _, wz) plus any
    /// immediately neighbouring chunks whose boundary faces may have changed.
    /// </summary>
    public void RebuildAffectedChunks(Vector3i blockPos)
    {
        int cx = (int)MathF.Floor((float)blockPos.X / Chunk.Size);
        int cz = (int)MathF.Floor((float)blockPos.Z / Chunk.Size);
        RebuildChunk(new Vector2i(cx, cz));

        int lx = blockPos.X - cx * Chunk.Size;
        int lz = blockPos.Z - cz * Chunk.Size;
        if (lx == 0) RebuildChunk(new Vector2i(cx - 1, cz));
        if (lx == Chunk.Size - 1) RebuildChunk(new Vector2i(cx + 1, cz));
        if (lz == 0) RebuildChunk(new Vector2i(cx, cz - 1));
        if (lz == Chunk.Size - 1) RebuildChunk(new Vector2i(cx, cz + 1));
    }

    // -------------------------------------------------------------------------
    // Placed model management
    // -------------------------------------------------------------------------

    /// <summary>
    /// Registers a MODEL-type block at <paramref name="blockPos"/> as a placed model
    /// to be rendered each frame without physics or pickup.
    /// </summary>
    public void AddPlacedModel(Vector3i blockPos, Item item)
    {
        var pos = new Vector3(blockPos.X + 0.5f,
                              blockPos.Y + 0.5f - EntityItem.HoverHeight,
                              blockPos.Z + 0.5f);
        var entity = new EntityItem(item, 1, pos);
        entity.SpinAngle = 0f;
        entity.PickupCooldown = float.MaxValue;
        _placedModels[blockPos] = entity;
    }

    /// <summary>Removes the placed model at <paramref name="pos"/>, if any.</summary>
    public void RemovePlacedModel(Vector3i pos) => _placedModels.Remove(pos);

    /// <summary>Removes all placed models that belong to chunk <paramref name="key"/>.</summary>
    public void EvictChunkPlacedModels(Vector2i key)
    {
        int minX = key.X * Chunk.Size, maxX = (key.X + 1) * Chunk.Size;
        int minZ = key.Y * Chunk.Size, maxZ = (key.Y + 1) * Chunk.Size;
        foreach (var pos in _placedModels.Keys
            .Where(p => p.X >= minX && p.X < maxX && p.Z >= minZ && p.Z < maxZ)
            .ToList())
            _placedModels.Remove(pos);
    }

    /// <summary>
    /// Scans <paramref name="chunk"/> for transparent MODEL blocks and registers
    /// each as a placed model. Call after every initial or streamed chunk load.
    /// </summary>
    public void ScanChunkForPlacedModels(Chunk chunk)
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

    // -------------------------------------------------------------------------
    // Model GPU cache
    // -------------------------------------------------------------------------

    private ModelGpu GetOrCreateModelGpu(Item item)
    {
        if (_modelGpu.TryGetValue(item.Id, out var cached)) return cached;

        var mesh = item.Mesh!;
        var gpuMesh = _gpu.UploadMesh(mesh.Vertices, mesh.Indices, 7);

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

        var entry = new ModelGpu(gpuMesh, texHandle);
        _modelGpu[item.Id] = entry;
        return entry;
    }

    // -------------------------------------------------------------------------
    // Rendering
    // -------------------------------------------------------------------------

    /// <summary>
    /// Executes the full 3-D render pass for one frame: chunks, entities, placed models,
    /// the debug border overlay, and the hotbar 3-D item previews.
    /// </summary>
    public void Render(Camera camera, IReadOnlyList<EntityItem> entityItems,
                       DebugState debug, bool gameIsPlaying, int fbWidth, int fbHeight)
    {
        if (debug.WireframeMode)
            GL.PolygonMode(MaterialFace.FrontAndBack, PolygonMode.Line);

        _shader.Use();
        _atlas.Use(TextureUnit.Texture0);
        _shader.SetInt("uTexture", 0);
        int noTexMode = debug.LightingDebug ? 2 : (debug.NoTextures ? 1 : 0);
        _shader.SetInt("uNoTexture", noTexMode);

        var view = camera.GetViewMatrix();
        var projection = camera.GetProjectionMatrix();
        _shader.SetMatrix4("view", ref view);
        _shader.SetMatrix4("projection", ref projection);

        var frustum = Frustum.FromViewProjection(view, projection);

        Profiler.Begin("Chunk Draw");
        RenderChunks(frustum);
        Profiler.End("Chunk Draw");

        Profiler.Begin("Entity Render");
        _entityRenderer.Render(entityItems, _shader, _atlas.Handle, item =>
        {
            if (item.Mesh == null) return null;
            var mg = GetOrCreateModelGpu(item);
            return (mg.Mesh.Vao, mg.Mesh.IndexCount, mg.TexHandle);
        });
        if (_placedModels.Count > 0)
            RenderPlacedModels();
        Profiler.End("Entity Render");

        if (debug.WireframeMode)
            GL.PolygonMode(MaterialFace.FrontAndBack, PolygonMode.Fill);

        if (debug.ShowChunkBorders)
        {
            if (BordersDirty)
            {
                _borders.UpdateGeometry(_chunkGpuData.Keys);
                BordersDirty = false;
            }
            _borders.Render(ref view, ref projection);
        }
    }

    private void RenderChunks(Frustum frustum)
    {
        foreach (var (key, gpu) in _chunkGpuData)
        {
            if (gpu.IndexCount == 0) continue;

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
    }

    private void RenderPlacedModels()
    {
        GL.Disable(EnableCap.CullFace);

        foreach (var (blockPos, entity) in _placedModels)
        {
            var item = entity.Item;
            if (item.Mesh is null) continue;

            ModelGpu mg = GetOrCreateModelGpu(item);

            if (mg.TexHandle != 0)
            {
                GL.ActiveTexture(TextureUnit.Texture0);
                GL.BindTexture(TextureTarget.Texture2D, mg.TexHandle);
            }

            var model = Matrix4.CreateScale(1f / 16f) *
                        Matrix4.CreateTranslation(blockPos.X, blockPos.Y, blockPos.Z);
            _shader.SetMatrix4("model", ref model);

            GL.BindVertexArray(mg.Mesh.Vao);
            GL.DrawElements(PrimitiveType.Triangles, mg.Mesh.IndexCount,
                            DrawElementsType.UnsignedInt, 0);
        }

        GL.BindVertexArray(0);
        GL.Enable(EnableCap.CullFace);
        _atlas.Use(TextureUnit.Texture0);
    }

    /// <summary>
    /// Renders each occupied hotbar slot as a spinning 3-D mini item by reusing
    /// <see cref="EntityItemRenderer"/>. A tiny GL viewport is set per slot.
    /// Must be called AFTER the 2-D HUD backgrounds so items appear on top.
    /// </summary>
    public void RenderHotbarItems3D(int fbWidth, int fbHeight)
    {
        int count = Inventory.HotbarSize;
        float totalWidth = count * GameConstants.Render.HotbarSlotSize + (count - 1) * GameConstants.Render.HotbarSlotGap;
        float x0 = (fbWidth - totalWidth) * 0.5f;

        int glBaseY = GameConstants.Render.HotbarBottomPad;
        int slotPx = GameConstants.Render.HotbarSlotSize;
        const int pad = 4;
        int innerSize = slotPx - pad * 2;

        var miniProj = Matrix4.CreatePerspectiveFieldOfView(
            MathHelper.DegreesToRadians(40f), 1f, 0.01f, 20f);
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
            return (mg.Mesh.Vao, mg.Mesh.IndexCount, mg.TexHandle);
        };

        for (int i = 0; i < count; i++)
        {
            var stack = _inventory.Slots[i];
            if (stack.IsEmpty || stack.Item == null) continue;

            int vx = (int)(x0 + i * (GameConstants.Render.HotbarSlotSize + GameConstants.Render.HotbarSlotGap)) + pad;
            int vy = glBaseY + pad;

            GL.Scissor(vx, vy, innerSize, innerSize);
            GL.Viewport(vx, vy, innerSize, innerSize);

            _hudSlot[0] = new EntityItem(stack.Item, stack.Count, Vector3.Zero)
            {
                SpinAngle = MathF.PI / 4f
            };
            _entityRenderer.Render(_hudSlot, _shader, _atlas.Handle, gpuGetter);
        }

        GL.Disable(EnableCap.ScissorTest);
        GL.Enable(EnableCap.DepthTest);
        GL.Enable(EnableCap.CullFace);
        GL.Viewport(0, 0, fbWidth, fbHeight);
        _atlas.Use(TextureUnit.Texture0);
    }

    // -------------------------------------------------------------------------
    // Cleanup
    // -------------------------------------------------------------------------

    public void Dispose()
    {
        foreach (var gpu in _chunkGpuData.Values)
            _gpu.Free(gpu);
        _chunkGpuData.Clear();

        foreach (var mg in _modelGpu.Values)
        {
            _gpu.Free(mg.Mesh);
            if (mg.TexHandle != 0) GL.DeleteTexture(mg.TexHandle);
        }
        _modelGpu.Clear();

        _borders.Dispose();
    }
}
