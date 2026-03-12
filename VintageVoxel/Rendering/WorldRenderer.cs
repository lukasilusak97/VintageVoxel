using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using StbImageSharp;

namespace VintageVoxel.Rendering;

/// <summary>
/// Owns all chunk and model GPU resources and drives the 3-D render pass each frame.
/// Responsibilities: chunk mesh uploads, placed-model tracking, frustum culling,
/// entity rendering, hotbar 3-D preview, and the chunk-border debug overlay.
/// Also manages a shadow-map depth pass so world geometry and entities cast
/// directional shadows on the ground.
/// </summary>
public sealed class WorldRenderer : IDisposable
{
    // GPU handle bundle for one loaded item model.
    private readonly record struct ModelGpu(GpuMesh Mesh, int TexHandle);

    private readonly Dictionary<Vector3i, GpuMesh> _chunkGpuData = new();
    private readonly Dictionary<int, ModelGpu> _modelGpu = new();
    private readonly Dictionary<Vector3i, EntityItem> _placedModels = new();

    private readonly GpuResourceManager _gpu;
    private readonly World _world;
    private readonly Shader _shader;
    private readonly Shader _shadowShader;
    private readonly Texture _atlas;
    private readonly EntityItemRenderer _entityRenderer;
    private readonly Inventory _inventory;
    private readonly ChunkBorderRenderer _borders;

    // ── Shadow map ────────────────────────────────────────────────────────────
    private const int ShadowMapSize = 2048;

    // Sun direction pointing FROM the ground TOWARD the sun (used to position the
    // light camera above the scene).  The slight horizontal offset gives angled
    // shadows that make depth easier to read.
    private static readonly Vector3 SunDirection =
        Vector3.Normalize(new Vector3(0.55f, 1.8f, 0.35f));

    private int _shadowFbo;
    private int _shadowDepthTex;
    private Matrix4 _lightSpaceMatrix = Matrix4.Identity;

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
        _shadowShader = new Shader("Shaders/shadow.vert", "Shaders/shadow.frag");
        InitShadowMap();
    }

    // ── Shadow map setup ──────────────────────────────────────────────────────

    private void InitShadowMap()
    {
        // Depth-only texture that will receive the scene depth from the light's POV.
        _shadowDepthTex = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, _shadowDepthTex);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.DepthComponent24,
                      ShadowMapSize, ShadowMapSize, 0,
                      PixelFormat.DepthComponent, PixelType.Float, IntPtr.Zero);
        // Nearest filtering: we do manual PCF in the fragment shader.
        GL.TexParameter(TextureTarget.Texture2D,
            TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D,
            TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        // ClampToBorder with depth = 1.0: fragments outside the shadow frustum are lit.
        GL.TexParameter(TextureTarget.Texture2D,
            TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToBorder);
        GL.TexParameter(TextureTarget.Texture2D,
            TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToBorder);
        GL.TexParameter(TextureTarget.Texture2D,
            TextureParameterName.TextureBorderColor, new float[] { 1f, 1f, 1f, 1f });

        // Framebuffer with only a depth attachment (no colour writes at all).
        _shadowFbo = GL.GenFramebuffer();
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _shadowFbo);
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer,
                                FramebufferAttachment.DepthAttachment,
                                TextureTarget.Texture2D, _shadowDepthTex, 0);
        GL.DrawBuffer(DrawBufferMode.None);
        GL.ReadBuffer(ReadBufferMode.None);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        GL.BindTexture(TextureTarget.Texture2D, 0);
    }

    private Matrix4 ComputeLightSpaceMatrix(Vector3 cameraPos)
    {
        // Position the light camera high above the scene in the sun's direction.
        Vector3 lightPos = cameraPos + SunDirection * 160f;

        // Choose an "up" vector that is never parallel to SunDirection.
        Vector3 up = MathF.Abs(Vector3.Dot(SunDirection, Vector3.UnitX)) < 0.9f
                     ? Vector3.UnitX : Vector3.UnitZ;

        Matrix4 lightView = Matrix4.LookAt(lightPos, cameraPos, up);

        // Orthographic projection large enough to cover the loaded render area.
        float halfExt = World.RenderDistance * Chunk.Size + 16f;
        Matrix4 lightProj = Matrix4.CreateOrthographic(
            halfExt * 2f, halfExt * 2f, 1f, 450f);

        // OpenTK row-major: view * proj = proj_glsl * view_glsl in column-major GLSL.
        Matrix4 combined = lightView * lightProj;

        // Texel snapping: align the projection origin to whole shadow-map texels.
        // Without this, the shadow map shifts by a sub-texel amount every frame as
        // the camera moves, causing visible flickering/swimming at shadow edges.
        //
        // In OpenTK row-major storage: combined.M41 / M42 are m[3][0] / m[3][1] in
        // GLSL column-major terms, i.e. the X and Y NDC translation of the world
        // origin (0,0,0,1) through this matrix.
        float halfRes = ShadowMapSize * 0.5f;
        combined.M41 += (MathF.Round(combined.M41 * halfRes) - combined.M41 * halfRes) / halfRes;
        combined.M42 += (MathF.Round(combined.M42 * halfRes) - combined.M42 * halfRes) / halfRes;

        return combined;
    }

    // -------------------------------------------------------------------------
    // Chunk GPU lifecycle
    // -------------------------------------------------------------------------

    /// <summary>Meshes <paramref name="chunk"/> and uploads it to the GPU.</summary>
    private GpuMesh UploadChunkGpu(Chunk chunk)
    {
        ChunkMesh mesh = ChunkMeshBuilder.Build(chunk, _world);
        return _gpu.UploadMesh(mesh.Vertices, mesh.Indices, 8);
    }

    /// <summary>
    /// Re-meshes and re-uploads the chunk at <paramref name="key"/>, replacing any
    /// existing GPU data. Does nothing if the chunk is not loaded.
    /// </summary>
    public void RebuildChunk(Vector3i key)
    {
        if (!_world.Chunks.TryGetValue(key, out var chunk)) return;
        if (_chunkGpuData.TryGetValue(key, out var old)) _gpu.Free(old);
        _chunkGpuData[key] = UploadChunkGpu(chunk);
    }

    /// <summary>Frees the GPU mesh for <paramref name="key"/> and removes it from the cache.</summary>
    public void TryFreeChunkGpu(Vector3i key)
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
        int cy = (int)MathF.Floor((float)blockPos.Y / Chunk.Size);
        int cz = (int)MathF.Floor((float)blockPos.Z / Chunk.Size);
        RebuildChunk(new Vector3i(cx, cy, cz));

        int lx = blockPos.X - cx * Chunk.Size;
        int ly = blockPos.Y - cy * Chunk.Size;
        int lz = blockPos.Z - cz * Chunk.Size;
        if (lx == 0) RebuildChunk(new Vector3i(cx - 1, cy, cz));
        if (lx == Chunk.Size - 1) RebuildChunk(new Vector3i(cx + 1, cy, cz));
        if (ly == 0) RebuildChunk(new Vector3i(cx, cy - 1, cz));
        if (ly == Chunk.Size - 1) RebuildChunk(new Vector3i(cx, cy + 1, cz));
        if (lz == 0) RebuildChunk(new Vector3i(cx, cy, cz - 1));
        if (lz == Chunk.Size - 1) RebuildChunk(new Vector3i(cx, cy, cz + 1));
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
    public void EvictChunkPlacedModels(Vector3i key)
    {
        int minX = key.X * Chunk.Size, maxX = (key.X + 1) * Chunk.Size;
        int minY = key.Y * Chunk.Size, maxY = (key.Y + 1) * Chunk.Size;
        int minZ = key.Z * Chunk.Size, maxZ = (key.Z + 1) * Chunk.Size;
        foreach (var pos in _placedModels.Keys
            .Where(p => p.X >= minX && p.X < maxX && p.Y >= minY && p.Y < maxY && p.Z >= minZ && p.Z < maxZ)
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

    // ── Shadow pass ───────────────────────────────────────────────────────────

    /// <summary>
    /// Renders the scene into the depth-only shadow-map FBO from the directional
    /// light's perspective.  Both chunk meshes and entity items are included so
    /// all objects cast shadows on the ground.
    /// </summary>
    private void RenderShadowPass(IReadOnlyList<EntityItem> entityItems)
    {
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _shadowFbo);
        GL.Viewport(0, 0, ShadowMapSize, ShadowMapSize);
        GL.Clear(ClearBufferMask.DepthBufferBit);

        // Polygon offset pushes stored depth slightly farther to avoid self-shadowing acne.
        GL.Enable(EnableCap.PolygonOffsetFill);
        GL.PolygonOffset(2f, 4f);
        GL.Enable(EnableCap.DepthTest);

        _shadowShader.Use();
        _shadowShader.SetMatrix4("lightSpaceMatrix", ref _lightSpaceMatrix);

        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2D, _atlas.Handle);
        _shadowShader.SetInt("uTexture", 0);

        // --- Chunks: rendered WITHOUT alpha-test so the shadow map has no holes.
        // Alpha-tested (leaf) geometry with per-texel holes causes the Poisson PCF
        // to sample through those holes (stored depth = 1.0 = sky) and report
        // "fully lit" for leaf faces that should be in canopy shadow.
        // Solid leaf-block silhouettes on the shadow map fix this completely.
        _shadowShader.SetInt("uAlphaTest", 0);
        foreach (var (key, gpu) in _chunkGpuData)
        {
            if (gpu.IndexCount == 0) continue;
            var model = Matrix4.CreateTranslation(
                key.X * Chunk.Size, key.Y * Chunk.Size, key.Z * Chunk.Size);
            _shadowShader.SetMatrix4("model", ref model);
            GL.BindVertexArray(gpu.Vao);
            GL.DrawElements(PrimitiveType.Triangles, gpu.IndexCount,
                            DrawElementsType.UnsignedInt, 0);
        }

        // --- Floating entity items (dropped picks, etc.) ---
        if (entityItems.Count > 0)
            _entityRenderer.Render(entityItems, _shadowShader, _atlas.Handle);

        // --- Stationary placed models ---
        // Each model may use its own texture; switch alpha-test on/off accordingly.
        foreach (var (blockPos, entity) in _placedModels)
        {
            var item = entity.Item;
            if (item.Mesh is null) continue;
            ModelGpu mg = GetOrCreateModelGpu(item);

            if (mg.TexHandle != 0)
            {
                GL.BindTexture(TextureTarget.Texture2D, mg.TexHandle);
                _shadowShader.SetInt("uAlphaTest", 1);
            }
            else
            {
                GL.BindTexture(TextureTarget.Texture2D, _atlas.Handle);
                _shadowShader.SetInt("uAlphaTest", 0);
            }

            var model = Matrix4.CreateScale(1f / 16f) *
                        Matrix4.CreateTranslation(blockPos.X, blockPos.Y, blockPos.Z);
            _shadowShader.SetMatrix4("model", ref model);
            GL.BindVertexArray(mg.Mesh.Vao);
            GL.DrawElements(PrimitiveType.Triangles, mg.Mesh.IndexCount,
                            DrawElementsType.UnsignedInt, 0);
        }

        // Restore atlas on unit 0 for subsequent passes.
        GL.BindTexture(TextureTarget.Texture2D, _atlas.Handle);

        GL.BindVertexArray(0);
        GL.Disable(EnableCap.PolygonOffsetFill);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    // ── Main render pass ──────────────────────────────────────────────────────

    /// <summary>
    /// Executes the full 3-D render pass for one frame: chunks, entities, placed models,
    /// the debug border overlay, and the hotbar 3-D item previews.
    /// </summary>
    public void Render(Camera camera, IReadOnlyList<EntityItem> entityItems,
                       DebugState debug, bool gameIsPlaying, int fbWidth, int fbHeight)
    {
        // ── 1. Shadow pass ────────────────────────────────────────────────────
        _lightSpaceMatrix = ComputeLightSpaceMatrix(camera.Position);
        RenderShadowPass(entityItems);

        // Restore the main framebuffer and viewport after the shadow pass.
        GL.Viewport(0, 0, fbWidth, fbHeight);

        // ── 2. Main scene pass ────────────────────────────────────────────────
        if (debug.WireframeMode)
            GL.PolygonMode(MaterialFace.FrontAndBack, PolygonMode.Line);

        _shader.Use();
        _atlas.Use(TextureUnit.Texture0);
        _shader.SetInt("uTexture", 0);
        int noTexMode = debug.LightingDebug ? 2 : (debug.NoTextures ? 1 : 0);
        _shader.SetInt("uNoTexture", noTexMode);

        // Bind shadow depth texture to unit 1.
        GL.ActiveTexture(TextureUnit.Texture1);
        GL.BindTexture(TextureTarget.Texture2D, _shadowDepthTex);
        _shader.SetInt("uShadowMap", 1);
        _shader.SetMatrix4("lightSpaceMatrix", ref _lightSpaceMatrix);
        GL.ActiveTexture(TextureUnit.Texture0); // restore default active unit

        // Atmospheric fog: start at 60% of render distance, fully opaque at 95%.
        float renderDist = World.RenderDistance * Chunk.Size;
        _shader.SetFloat("uFogStart", renderDist * 0.60f);
        _shader.SetFloat("uFogEnd", renderDist * 0.95f);

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
            int wy = key.Y * Chunk.Size;
            int wz = key.Z * Chunk.Size;
            if (!frustum.ContainsAabb(
                    new Vector3(wx, wy, wz),
                    new Vector3(wx + Chunk.Size, wy + Chunk.Size, wz + Chunk.Size)))
                continue;

            var model = Matrix4.CreateTranslation(wx, wy, wz);
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

    /// <summary>
    /// Renders inventory slot items as 3-D mini previews using the same
    /// viewport-per-slot technique as <see cref="RenderHotbarItems3D"/>.
    /// <paramref name="targets"/> is the <see cref="InventoryWindow.SlotRenderTargets"/>
    /// list populated during that window's Draw call.  Coordinates are in ImGui
    /// display space (Y=0 at top); this method converts them to GL framebuffer space.
    /// Must be called AFTER <see cref="InventoryWindow.Draw"/> and BEFORE
    /// ImGuiController.Render so the items appear below the (transparent) ImGui overlay.
    /// </summary>
    public void RenderInventoryItems3D(
        IReadOnlyList<(ItemStack Stack, float DispX, float DispY, float DispSize)> targets,
        float displayW, float displayH,
        int fbWidth, int fbHeight)
    {
        if (targets.Count == 0) return;

        float sx = fbWidth / displayW;
        float sy = fbHeight / displayH;
        const int pad = 4;

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

        foreach (var (stack, dispX, dispY, dispSize) in targets)
        {
            if (stack.IsEmpty || stack.Item == null) continue;

            // Convert display coords (Y=0 top) → GL framebuffer coords (Y=0 bottom).
            int innerSize = (int)(dispSize * Math.Min(sx, sy)) - pad * 2;
            if (innerSize <= 0) continue;

            int vx = (int)(dispX * sx) + pad;
            int vy = (int)(fbHeight - (dispY + dispSize) * sy) + pad;

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
        _shadowShader.Dispose();
        GL.DeleteFramebuffer(_shadowFbo);
        GL.DeleteTexture(_shadowDepthTex);
    }
}
