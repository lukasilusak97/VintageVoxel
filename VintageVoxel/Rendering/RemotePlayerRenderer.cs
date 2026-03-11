using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using ImGuiNET;

namespace VintageVoxel.Rendering;

/// <summary>
/// Renders all connected remote players as procedural humanoid "box-man" meshes.
///
/// Geometry (unit-space, origin at feet):
///   Head   — 0.6 × 0.6 × 0.6 cube, centred at y=1.8
///   Body   — 0.5 × 0.75 × 0.25 box, centred at y=1.2
///   L/R arm — 0.25 × 0.6 × 0.25, offset ±0.375 side, centred at y=1.2
///   L/R leg — 0.25 × 0.75 × 0.25, offset ±0.15 side, centred at y=0.375
///
/// The same shared VAO is drawn for every body part, with per-part model
/// matrices and colours set via uniforms on the line/flat shader.
///
/// Uses the existing "Shaders/line.vert" + "Shaders/line.frag" pair (pos-only,
/// solid-colour) to avoid any texture-atlas dependency.
/// </summary>
public sealed class RemotePlayerRenderer : IDisposable
{
    // -------------------------------------------------------------------------
    // Part descriptors
    // -------------------------------------------------------------------------

    private readonly record struct Part(
        Vector3 Offset,        // centre offset from feet
        Vector3 HalfSize,      // half-extents in each axis
        Vector3 Color          // RGB [0,1]
    );

    private static readonly Part[] Parts =
    {
        // Head
        new(new Vector3(0f, 1.8f, 0f),  new Vector3(0.30f, 0.30f, 0.30f), new Vector3(0.95f, 0.82f, 0.70f)),
        // Body
        new(new Vector3(0f, 1.15f, 0f), new Vector3(0.25f, 0.37f, 0.13f), new Vector3(0.25f, 0.45f, 0.80f)),
        // Left arm
        new(new Vector3(-0.375f, 1.15f, 0f), new Vector3(0.13f, 0.30f, 0.13f), new Vector3(0.95f, 0.82f, 0.70f)),
        // Right arm
        new(new Vector3( 0.375f, 1.15f, 0f), new Vector3(0.13f, 0.30f, 0.13f), new Vector3(0.95f, 0.82f, 0.70f)),
        // Left leg
        new(new Vector3(-0.15f, 0.37f, 0f),  new Vector3(0.13f, 0.37f, 0.13f), new Vector3(0.15f, 0.25f, 0.60f)),
        // Right leg
        new(new Vector3( 0.15f, 0.37f, 0f),  new Vector3(0.13f, 0.37f, 0.13f), new Vector3(0.15f, 0.25f, 0.60f)),
    };

    // -------------------------------------------------------------------------
    // GPU data — one unit cube, instanced via model matrix per part
    // -------------------------------------------------------------------------

    // 8 vertices of a [-1,+1] unit cube, 3 floats each.
    private static readonly float[] CubeVerts =
    {
       -1f,-1f,-1f,  // 0
        1f,-1f,-1f,  // 1
        1f, 1f,-1f,  // 2
       -1f, 1f,-1f,  // 3
       -1f,-1f, 1f,  // 4
        1f,-1f, 1f,  // 5
        1f, 1f, 1f,  // 6
       -1f, 1f, 1f,  // 7
    };

    // 12 triangles (36 indices) winding CCW.
    private static readonly uint[] CubeIndices =
    {
        0,1,2, 2,3,0,  // back
        4,6,5, 6,4,7,  // front
        0,3,7, 7,4,0,  // left
        1,5,6, 6,2,1,  // right
        3,2,6, 6,7,3,  // top
        0,4,5, 5,1,0,  // bottom
    };

    private readonly Shader _shader;
    private int _vao;
    private int _vbo;
    private int _ebo;

    // Cached uniform locations.
    private readonly int _uModel;
    private readonly int _uView;
    private readonly int _uProjection;
    private readonly int _uColor;

    // -------------------------------------------------------------------------
    // Constructor
    // -------------------------------------------------------------------------

    public RemotePlayerRenderer()
    {
        _shader = new Shader("Shaders/line.vert", "Shaders/line.frag");

        // We need a model matrix uniform that line.vert doesn't have by default.
        // So we use shaders/shader.vert BUT we only fill pos (no UV/light/ao needed
        // since the fragment shader only reads uColor).
        // Actually we need dedicated simple shaders — let's use the line shader pair
        // with an extended vertex shader that supports a model matrix.
        // Since line.vert doesn't have a model uniform, we reuse shader.vert which
        // does have model/view/projection, and accept that it passes dummy UV/light/ao
        // to the fragment shader. We pair it with line.frag to ignore those varyings.
        // BUT shader.vert has layout location 1,2,3 for UV/light/ao which we DON'T send.
        // OpenGL ignores missing attribute data (treats as zero) — this is safe.
        //
        // We'll set up ONLY location 0 (position) and leave others unbound.
        _shader.Dispose();
        _shader = new Shader("Shaders/shader.vert", "Shaders/line.frag");

        _uModel = GL.GetUniformLocation(_shader.Handle, "model");
        _uView = GL.GetUniformLocation(_shader.Handle, "view");
        _uProjection = GL.GetUniformLocation(_shader.Handle, "projection");
        _uColor = GL.GetUniformLocation(_shader.Handle, "uColor");

        // Build cube VAO — position only (attribute 0, 3 floats).
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

        // Attribute 0: position (3 floats, stride = 12 bytes).
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), 0);
        GL.EnableVertexAttribArray(0);

        GL.BindVertexArray(0);
    }

    // -------------------------------------------------------------------------
    // Render
    // -------------------------------------------------------------------------

    /// <summary>
    /// Draws every remote player in <paramref name="players"/> using the given
    /// camera matrices. Call this inside the main render pass with depth test on.
    /// </summary>
    public void Render(IEnumerable<RemotePlayer> players, Camera camera,
                       int viewportWidth, int viewportHeight)
    {
        if (!players.Any()) return;

        GL.UseProgram(_shader.Handle);

        var view = camera.GetViewMatrix();
        var proj = camera.GetProjectionMatrix();
        GL.UniformMatrix4(_uView, false, ref view);
        GL.UniformMatrix4(_uProjection, false, ref proj);

        GL.BindVertexArray(_vao);

        foreach (var player in players)
        {
            float yawRad = MathHelper.DegreesToRadians(player.Yaw);
            var baseRot = Matrix4.CreateRotationY(yawRad);

            foreach (var part in Parts)
            {
                // Scale the unit [-1,+1] cube to the half-extents, translate to offset.
                var scale = Matrix4.CreateScale(part.HalfSize);
                var trans = Matrix4.CreateTranslation(part.Offset);
                var worldT = Matrix4.CreateTranslation(player.Position);
                var model = scale * trans * baseRot * worldT;

                GL.UniformMatrix4(_uModel, false, ref model);
                GL.Uniform3(_uColor, part.Color);
                GL.DrawElements(PrimitiveType.Triangles, CubeIndices.Length, DrawElementsType.UnsignedInt, 0);
            }
        }

        GL.BindVertexArray(0);
    }

    /// <summary>
    /// Draws name tag overlays via ImGui. Must be called inside an ImGui frame,
    /// after the GL render pass, so the tag appears on top.
    /// </summary>
    public void RenderNameTags(IEnumerable<RemotePlayer> players, Camera camera,
                               int viewportWidth, int viewportHeight)
    {
        foreach (var player in players)
        {
            // Project the name-tag position (above the head) to screen space.
            var worldPos = player.Position + new Vector3(0f, 2.2f, 0f);
            var screen = WorldToScreen(worldPos, camera, viewportWidth, viewportHeight);
            if (screen.Z <= 0f) continue; // behind camera

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

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static Vector3 WorldToScreen(Vector3 world, Camera camera,
                                         int vpW, int vpH)
    {
        var clip = new Vector4(world, 1f) *
                   (camera.GetViewMatrix() * camera.GetProjectionMatrix());
        if (MathF.Abs(clip.W) < 0.0001f) return new Vector3(0, 0, -1);
        float ndcX = clip.X / clip.W;
        float ndcY = clip.Y / clip.W;
        float z = clip.W; // positive = in front of camera
        return new Vector3(
            (ndcX * 0.5f + 0.5f) * vpW,
            (1f - (ndcY * 0.5f + 0.5f)) * vpH,
            z);
    }

    // -------------------------------------------------------------------------
    // Disposal
    // -------------------------------------------------------------------------

    public void Dispose()
    {
        GL.DeleteVertexArray(_vao);
        GL.DeleteBuffer(_vbo);
        GL.DeleteBuffer(_ebo);
        _shader.Dispose();
    }
}
