using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using VintageVoxel.Rendering;

namespace VintageVoxel;

/// <summary>
/// Renders the 2-D HUD overlay: crosshair and hotbar with item icons.
///
/// WHY a separate renderer?
///   The world pass uses a perspective projection, depth testing, and back-face
///   culling.  2-D UI elements need an orthographic pixel-space projection, no
///   depth writes, and alpha blending.  Keeping these concerns in a dedicated class
///   means we never accidentally stomp the 3-D GL state mid-frame.
/// </summary>
public sealed class HUDRenderer : IDisposable
{
    // --- GPU resources ---
    private readonly Shader _shader;
    private readonly GpuMesh _mesh;  // VAO + dynamic VBO + static EBO shared by every 2-D draw call.
    private readonly GpuResourceManager _gpuResources;

    // Current orthographic projection (pixel-space, top-left origin).
    private Matrix4 _ortho;
    private int _screenWidth;
    private int _screenHeight;

    // --- Layout constants (all in screen pixels) — defined in GameConstants.Render ---
    private static int SlotSize => GameConstants.Render.HotbarSlotSize;
    private static int SlotGap => GameConstants.Render.HotbarSlotGap;
    private static int HotbarBottomPad => GameConstants.Render.HotbarBottomPad;

    // Shared index data: two triangles forming a CCW quad.
    // Vertex order: top-left(0), top-right(1), bottom-right(2), bottom-left(3).
    private static readonly uint[] QuadIndices = { 0, 1, 2, 2, 3, 0 };

    public HUDRenderer(GpuResourceManager gpuResources, int screenWidth, int screenHeight)
    {
        _gpuResources = gpuResources;
        _shader = new Shader("Shaders/hud.vert", "Shaders/hud.frag");
        SetScreenSize(screenWidth, screenHeight);

        // One VAO/VBO pair shared by every 2-D draw call.
        // Vertices: 4 × (x, y, u, v) = 16 floats. Uploaded as DynamicDraw because
        // the quad rectangle changes with every call.
        _mesh = _gpuResources.AllocateDynamicMesh(16, QuadIndices, 4);
    }

    // -------------------------------------------------------------------------
    // Public API
    // -------------------------------------------------------------------------

    /// <summary>
    /// Rebuilds the orthographic projection to match the new window dimensions.
    /// Call this from <see cref="Game.OnResize"/>.
    /// </summary>
    public void SetScreenSize(int w, int h)
    {
        _screenWidth = w;
        _screenHeight = h;
        // Map pixel coordinates directly: (0,0) = top-left, (w,h) = bottom-right.
        // The -1 / +1 near/far planes give plenty of room since depth is unused here.
        _ortho = Matrix4.CreateOrthographicOffCenter(0f, w, h, 0f, -1f, 1f);
    }

    /// <summary>
    /// Renders the complete HUD for one frame.
    /// Must be called AFTER the 3-D world pass and BEFORE ImGui.Render().
    /// <paramref name="screenWidth"/> and <paramref name="screenHeight"/> must be
    /// the current framebuffer dimensions — they are forwarded to
    /// <see cref="SetScreenSize"/> so the ortho projection and position
    /// calculations always use identical values.
    /// </summary>
    public void Render(Inventory inventory, Texture atlas, int screenWidth, int screenHeight)
    {
        // Always sync the ortho projection to the current framebuffer size so it
        // can never diverge from the pixel-space coordinates used below.
        if (screenWidth != _screenWidth || screenHeight != _screenHeight)
            SetScreenSize(screenWidth, screenHeight);

        // --- Switch to 2-D state ---
        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        GL.Disable(EnableCap.DepthTest);
        GL.Disable(EnableCap.CullFace);

        _shader.Use();
        _shader.SetMatrix4("uProjection", ref _ortho);

        // Bind the atlas to unit 0 so item icons can sample it.
        atlas.Use(TextureUnit.Texture0);
        _shader.SetInt("uTexture", 0);

        // Use _screenWidth / _screenHeight throughout — they are now guaranteed
        // to match the ortho projection set above.
        DrawCrosshair(_screenWidth, _screenHeight);
        DrawHotbar(inventory, _screenWidth, _screenHeight);

        // --- Restore 3-D state ---
        GL.Disable(EnableCap.Blend);
        GL.Enable(EnableCap.DepthTest);
        GL.Enable(EnableCap.CullFace);
    }

    // -------------------------------------------------------------------------
    // Crosshair
    // -------------------------------------------------------------------------

    private void DrawCrosshair(int sw, int sh)
    {
        float cx = sw * 0.5f;
        float cy = sh * 0.5f;

        // Slight dark shadow first so the white bars are visible against bright sky.
        var shadow = new Vector4(0f, 0f, 0f, 0.4f);
        DrawQuad(cx - 11f, cy - 2f, 22f, 4f, shadow); // horizontal shadow
        DrawQuad(cx - 2f, cy - 11f, 4f, 22f, shadow); // vertical shadow

        var white = new Vector4(1f, 1f, 1f, 0.9f);
        DrawQuad(cx - 10f, cy - 1f, 20f, 2f, white); // horizontal bar
        DrawQuad(cx - 1f, cy - 10f, 2f, 20f, white); // vertical bar
    }

    // -------------------------------------------------------------------------
    // Hotbar
    // -------------------------------------------------------------------------

    private void DrawHotbar(Inventory inventory, int sw, int sh)
    {
        int count = Inventory.HotbarSize;
        float totalWidth = count * SlotSize + (count - 1) * SlotGap;
        float x0 = (sw - totalWidth) * 0.5f;
        float y0 = sh - HotbarBottomPad - SlotSize;

        for (int i = 0; i < count; i++)
        {
            float sx = x0 + i * (SlotSize + SlotGap);
            bool selected = i == inventory.SelectedSlot;

            // Outer border — bright white for selected, dim grey for others.
            var borderColor = selected
                ? new Vector4(1f, 1f, 1f, 1.0f)
                : new Vector4(0.55f, 0.55f, 0.55f, 0.9f);
            DrawQuad(sx - 2f, y0 - 2f, SlotSize + 4f, SlotSize + 4f, borderColor);

            // Slot background — slightly lighter for the selected slot.
            var bgColor = selected
                ? new Vector4(0.35f, 0.35f, 0.35f, 0.92f)
                : new Vector4(0.12f, 0.12f, 0.12f, 0.85f);
            DrawQuad(sx, y0, SlotSize, SlotSize, bgColor);

            // Item icons are rendered as 3-D mini-entities by Game.RenderHotbarItems3D
            // (called immediately after this 2-D pass), so no 2-D icon is drawn here.
        }
    }

    // -------------------------------------------------------------------------
    // Primitive helpers
    // -------------------------------------------------------------------------

    /// <summary>Draws a solid-colour axis-aligned quad in pixel space.</summary>
    private void DrawQuad(float x, float y, float w, float h, Vector4 color)
    {
        // UV is irrelevant for flat-colour quads but we still provide [0,1] range
        // so the vertex layout remains consistent.
        float[] verts =
        {
            x,     y,     0f, 0f,
            x + w, y,     1f, 0f,
            x + w, y + h, 1f, 1f,
            x,     y + h, 0f, 1f,
        };
        UploadAndDraw(verts, color, useTexture: false);
    }

    /// <summary>
    /// Draws a textured quad sampling the atlas tile at index <paramref name="tileIndex"/>.
    /// </summary>
    private void DrawAtlasTile(float x, float y, float w, float h, int tileIndex)
    {
        float uMin = tileIndex * TextureAtlas.TileUvWidth;
        float uMax = uMin + TextureAtlas.TileUvWidth;

        float[] verts =
        {
            x,     y,     uMin, 0f,
            x + w, y,     uMax, 0f,
            x + w, y + h, uMax, 1f,
            x,     y + h, uMin, 1f,
        };
        UploadAndDraw(verts, Vector4.One, useTexture: true);
    }

    private void UploadAndDraw(float[] verts, Vector4 color, bool useTexture)
    {
        GL.BindVertexArray(_mesh.Vao);

        // Orphan-and-replace pattern: passing a new sub-data range invalidates the
        // old buffer contents, letting the driver pipeline the upload without stalling.
        GL.BindBuffer(BufferTarget.ArrayBuffer, _mesh.Vbo);
        GL.BufferSubData(BufferTarget.ArrayBuffer, IntPtr.Zero,
                         verts.Length * sizeof(float), verts);

        _shader.SetVector4("uColor", color);
        _shader.SetInt("uUseTexture", useTexture ? 1 : 0);

        GL.DrawElements(PrimitiveType.Triangles, 6, DrawElementsType.UnsignedInt, 0);
        GL.BindVertexArray(0);
    }

    // -------------------------------------------------------------------------
    // IDisposable
    // -------------------------------------------------------------------------

    public void Dispose()
    {
        _gpuResources.Free(_mesh);
        _shader.Dispose();
    }
}
