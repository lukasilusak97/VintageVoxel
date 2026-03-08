using ImGuiNET;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using VintageVoxel;

namespace VintageVoxel.Editor;

/// <summary>
/// The main Asset Editor window. Phase E1 skeleton — ImGui layout placeholder.
/// </summary>
public sealed class EditorWindow : GameWindow
{
    private ImGuiController _imgui = null!;

    // ── Orbit camera (E1.3) ────────────────────────────────────────────────
    private OrbitCamera _camera = null!;

    // ── 3D line renderer (axes + reference grid) ───────────────────────────
    private Shader _lineShader = null!;
    private int _axisVao, _axisVbo;
    private int _gridVao, _gridVbo, _gridVertexCount;

    // ── Export state ───────────────────────────────────────────────────────
    private string _modelName = "my_model";
    private string? _lastExportPath;

    public EditorWindow(GameWindowSettings gameWindowSettings, NativeWindowSettings nativeWindowSettings)
        : base(gameWindowSettings, nativeWindowSettings) { }

    protected override void OnLoad()
    {
        base.OnLoad();

        GL.ClearColor(0.12f, 0.12f, 0.14f, 1.0f);
        GL.Enable(EnableCap.DepthTest);

        _imgui = new ImGuiController(ClientSize.X, ClientSize.Y);

        // E1.3 — Orbit camera, starts 5 units away at a 45° angle
        float aspect = (float)ClientSize.X / ClientSize.Y;
        _camera = new OrbitCamera(radius: 5f, fovDegrees: 60f, aspect: aspect);

        _lineShader = new Shader("Shaders/editor.vert", "Shaders/editor.frag");
        BuildAxisMesh();
        BuildGridMesh();
    }

    protected override void OnResize(ResizeEventArgs e)
    {
        base.OnResize(e);
        GL.Viewport(0, 0, e.Width, e.Height);
        _imgui.WindowResized(e.Width, e.Height);
        _camera?.UpdateAspect((float)e.Width / e.Height);
    }

    protected override void OnMouseMove(MouseMoveEventArgs e)
    {
        base.OnMouseMove(e);
        // RMB drag → orbit
        if (MouseState.IsButtonDown(MouseButton.Right))
            _camera.Orbit(-e.DeltaX * 0.005f, -e.DeltaY * 0.005f);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        // Only zoom when ImGui is not consuming the scroll
        if (!ImGui.GetIO().WantCaptureMouse)
            _camera.Zoom(-e.OffsetY * 0.4f);
    }

    protected override void OnUpdateFrame(FrameEventArgs args)
    {
        base.OnUpdateFrame(args);
        _imgui.Update(this, (float)args.Time);
    }

    protected override void OnRenderFrame(FrameEventArgs args)
    {
        base.OnRenderFrame(args);

        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

        Render3D();
        DrawUI();

        _imgui.Render();
        SwapBuffers();
    }

    private void DrawUI()
    {
        // ── Left toolbar panel ──────────────────────────────────────────────
        ImGui.SetNextWindowPos(new System.Numerics.Vector2(0, 0), ImGuiCond.Always);
        ImGui.SetNextWindowSize(new System.Numerics.Vector2(200, ClientSize.Y), ImGuiCond.Always);
        ImGui.Begin("Tools", ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoCollapse);
        ImGui.Text("Tools / Colors");
        ImGui.Separator();
        ImGui.TextDisabled("Orbit Camera:");
        ImGui.BulletText("RMB drag — orbit");
        ImGui.BulletText("Scroll — zoom");
        ImGui.Separator();
        ImGui.Text("(Phase E2 — coming soon)");
        ImGui.End();

        // ── Right properties panel ─────────────────────────────────────────
        ImGui.SetNextWindowPos(new System.Numerics.Vector2(ClientSize.X - 220, 0), ImGuiCond.Always);
        ImGui.SetNextWindowSize(new System.Numerics.Vector2(220, ClientSize.Y), ImGuiCond.Always);
        ImGui.Begin("File / Export", ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoCollapse);

        ImGui.Text("Model Name");
        ImGui.InputText("##modelName", ref _modelName, 64);
        ImGui.Separator();

        if (ImGui.Button("Export JSON", new System.Numerics.Vector2(-1, 0)))
            ExportModel();

        if (_lastExportPath is not null)
        {
            ImGui.Spacing();
            ImGui.TextWrapped($"Saved:\n{_lastExportPath}");
        }

        ImGui.End();

        // ── Centre viewport label ──────────────────────────────────────────
        float vpX = 200;
        float vpW = ClientSize.X - 200 - 220;
        ImGui.SetNextWindowPos(new System.Numerics.Vector2(vpX, 0), ImGuiCond.Always);
        ImGui.SetNextWindowSize(new System.Numerics.Vector2(vpW, 30), ImGuiCond.Always);
        ImGui.Begin("Viewport", ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove
                               | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoScrollbar);
        ImGui.Text("3D Viewport — Orbit Camera (Phase E1)");
        ImGui.End();
    }

    protected override void OnUnload()
    {
        GL.DeleteVertexArray(_axisVao);
        GL.DeleteBuffer(_axisVbo);
        GL.DeleteVertexArray(_gridVao);
        GL.DeleteBuffer(_gridVbo);
        _lineShader.Dispose();
        _imgui.Dispose();
        base.OnUnload();
    }

    // ── 3D Scene ────────────────────────────────────────────────────────────

    private void Render3D()
    {
        var view = _camera.GetViewMatrix();
        var proj = _camera.GetProjectionMatrix();

        _lineShader.Use();
        _lineShader.SetMatrix4("view", ref view);
        _lineShader.SetMatrix4("projection", ref proj);

        // Reference grid (dim grey)
        _lineShader.SetVector3("uColor", new Vector3(0.35f, 0.35f, 0.38f));
        GL.BindVertexArray(_gridVao);
        GL.DrawArrays(PrimitiveType.Lines, 0, _gridVertexCount);

        // X axis — red
        _lineShader.SetVector3("uColor", new Vector3(0.9f, 0.25f, 0.25f));
        GL.BindVertexArray(_axisVao);
        GL.DrawArrays(PrimitiveType.Lines, 0, 2);

        // Y axis — green
        _lineShader.SetVector3("uColor", new Vector3(0.25f, 0.9f, 0.25f));
        GL.DrawArrays(PrimitiveType.Lines, 2, 2);

        // Z axis — blue
        _lineShader.SetVector3("uColor", new Vector3(0.25f, 0.45f, 0.95f));
        GL.DrawArrays(PrimitiveType.Lines, 4, 2);

        GL.BindVertexArray(0);
    }

    /// <summary>Uploads the three axis lines (X=red, Y=green, Z=blue) to the GPU.</summary>
    private void BuildAxisMesh()
    {
        float len = 2.5f;
        float[] verts =
        {
            0, 0, 0,   len, 0,   0,   // X
            0, 0, 0,   0,   len, 0,   // Y
            0, 0, 0,   0,   0,   len, // Z
        };

        _axisVao = GL.GenVertexArray();
        _axisVbo = GL.GenBuffer();
        GL.BindVertexArray(_axisVao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _axisVbo);
        GL.BufferData(BufferTarget.ArrayBuffer, verts.Length * sizeof(float),
                      verts, BufferUsageHint.StaticDraw);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), 0);
        GL.EnableVertexAttribArray(0);
        GL.BindVertexArray(0);
    }

    /// <summary>
    /// Uploads a flat XZ reference grid from -8 to +8 with 1-unit spacing to the GPU.
    /// Phase E2 will replace this with the 16×16×16 canvas wireframe.
    /// </summary>
    private void BuildGridMesh()
    {
        var verts = new System.Collections.Generic.List<float>();
        const int Half = 8;

        for (int i = -Half; i <= Half; i++)
        {
            // Lines parallel to Z
            verts.AddRange(new[] { (float)i, 0f, (float)-Half,
                                   (float)i, 0f, (float) Half });
            // Lines parallel to X
            verts.AddRange(new[] { (float)-Half, 0f, (float)i,
                                   (float) Half, 0f, (float)i });
        }

        float[] arr = verts.ToArray();
        _gridVertexCount = arr.Length / 3;

        _gridVao = GL.GenVertexArray();
        _gridVbo = GL.GenBuffer();
        GL.BindVertexArray(_gridVao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _gridVbo);
        GL.BufferData(BufferTarget.ArrayBuffer, arr.Length * sizeof(float),
                      arr, BufferUsageHint.StaticDraw);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), 0);
        GL.EnableVertexAttribArray(0);
        GL.BindVertexArray(0);
    }

    // ── Export ─────────────────────────────────────────────────────────────
    private void ExportModel()
    {
        // Phase E2 will populate _voxels from the canvas; for now the list is
        // empty so the exported file is a valid (blank) model skeleton.
        var model = new VintageVoxel.VoxelModel
        {
            Name = string.IsNullOrWhiteSpace(_modelName) ? "unnamed" : _modelName.Trim(),
            Type = "MicroBlock",
            GridSize = 16,
            Voxels = new List<VintageVoxel.VoxelEntry>() // populated by canvas in Phase E2
        };

        try
        {
            _lastExportPath = AssetExporter.ExportToSharedData(model);
        }
        catch (Exception ex)
        {
            _lastExportPath = $"Error: {ex.Message}";
        }
    }
}
