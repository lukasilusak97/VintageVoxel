using System.Runtime.CompilerServices;
using ImGuiNET;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace VintageVoxel;

/// <summary>
/// OpenTK 4.x backend for ImGui.NET.
///
/// Responsibilities:
///   - Upload the ImGui font atlas to a GPU texture.
///   - Compile an inline GLSL shader that renders ImGui draw lists.
///   - Manage a dynamic VAO/VBO/EBO for per-frame ImGui geometry.
///   - Each frame: relay OpenTK input into ImGui IO and call ImGui.NewFrame().
///   - After all UI commands: call Render() to submit the draw list to OpenGL.
/// </summary>
public sealed class ImGuiController : IDisposable
{
    // --- GPU objects ---
    private int _vao, _vbo, _ebo;
    private int _fontTexture;
    private int _shaderProgram;
    private int _projLocation;

    // Dynamic buffer sizes (bytes) — grown lazily as ImGui requests more geometry.
    private int _vertexBufferSize = 5000;
    private int _indexBufferSize = 10000;

    private int _windowWidth;
    private int _windowHeight;
    private bool _frameBegun;

    // Pre-built mapping: OpenTK key → ImGui named key.
    private static readonly Dictionary<Keys, ImGuiKey> s_keyMap = BuildKeyMap();

    // -------------------------------------------------------------------------

    public ImGuiController(int width, int height)
    {
        _windowWidth = width;
        _windowHeight = height;

        ImGui.CreateContext();
        var io = ImGui.GetIO();
        io.Fonts.AddFontDefault();

        ApplyGameStyle();

        // Tell ImGui the renderer supports VtxOffset in draw commands so it can
        // emit geometry that exceeds 65k vertices per draw list (required for
        // GL.DrawElementsBaseVertex).
        io.BackendFlags |= ImGuiBackendFlags.RendererHasVtxOffset;

        CreateDeviceResources();
    }

    public void WindowResized(int width, int height)
    {
        _windowWidth = width;
        _windowHeight = height;
    }

    // -------------------------------------------------------------------------
    // Device resource creation
    // -------------------------------------------------------------------------

    private void CreateDeviceResources()
    {
        // ---- Font atlas texture ----
        // ImGui compiles the font glyphs into a single RGBA atlas image.
        // We upload it to the GPU and store the texture ID in ImGui's IO so it
        // can reference the texture during draw-list rendering.
        var io = ImGui.GetIO();
        io.Fonts.GetTexDataAsRGBA32(out System.IntPtr pixels, out int fw, out int fh, out _);

        _fontTexture = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, _fontTexture);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba,
                      fw, fh, 0, PixelFormat.Rgba, PixelType.UnsignedByte, pixels);

        io.Fonts.SetTexID((System.IntPtr)_fontTexture);
        io.Fonts.ClearTexData(); // Free CPU-side copy — GPU owns it now.

        // ---- ImGui GLSL shader (inlined — no separate file needed) ----
        // Vertex: position (vec2) + uv (vec2) + colour (vec4, packed as 4 ubytes).
        // Fragment: multiply vertex colour by atlas texel colour.
        const string vertSrc = """
            #version 330 core
            layout(location = 0) in vec2 Position;
            layout(location = 1) in vec2 UV;
            layout(location = 2) in vec4 Color;
            uniform mat4 ProjMtx;
            out vec2 Frag_UV;
            out vec4 Frag_Color;
            void main()
            {
                Frag_UV    = UV;
                Frag_Color = Color;
                gl_Position = ProjMtx * vec4(Position.xy, 0.0, 1.0);
            }
            """;

        const string fragSrc = """
            #version 330 core
            in vec2 Frag_UV;
            in vec4 Frag_Color;
            uniform sampler2D Texture;
            layout(location = 0) out vec4 Out_Color;
            void main()
            {
                Out_Color = Frag_Color * texture(Texture, Frag_UV.st);
            }
            """;

        int vs = GL.CreateShader(ShaderType.VertexShader);
        GL.ShaderSource(vs, vertSrc);
        GL.CompileShader(vs);

        int fs = GL.CreateShader(ShaderType.FragmentShader);
        GL.ShaderSource(fs, fragSrc);
        GL.CompileShader(fs);

        _shaderProgram = GL.CreateProgram();
        GL.AttachShader(_shaderProgram, vs);
        GL.AttachShader(_shaderProgram, fs);
        GL.LinkProgram(_shaderProgram);
        GL.DetachShader(_shaderProgram, vs);
        GL.DetachShader(_shaderProgram, fs);
        GL.DeleteShader(vs);
        GL.DeleteShader(fs);

        _projLocation = GL.GetUniformLocation(_shaderProgram, "ProjMtx");
        GL.UseProgram(_shaderProgram);
        GL.Uniform1(GL.GetUniformLocation(_shaderProgram, "Texture"), 0);

        // ---- VAO / VBO / EBO ----
        // ImDrawVert layout: pos(8) + uv(8) + col(4) = 20 bytes.
        _vao = GL.GenVertexArray();
        GL.BindVertexArray(_vao);

        _vbo = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
        GL.BufferData(BufferTarget.ArrayBuffer, _vertexBufferSize, System.IntPtr.Zero, BufferUsageHint.DynamicDraw);

        _ebo = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.ElementArrayBuffer, _ebo);
        GL.BufferData(BufferTarget.ElementArrayBuffer, _indexBufferSize, System.IntPtr.Zero, BufferUsageHint.DynamicDraw);

        int stride = Unsafe.SizeOf<ImDrawVert>(); // 20 bytes
        GL.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, stride, 0);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, stride, 8);
        GL.EnableVertexAttribArray(1);
        GL.VertexAttribPointer(2, 4, VertexAttribPointerType.UnsignedByte, true, stride, 16);
        GL.EnableVertexAttribArray(2);

        GL.BindVertexArray(0);
        GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
    }

    // -------------------------------------------------------------------------
    // Per-frame update (call BEFORE all ImGui UI commands)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Updates ImGui IO (display size, time, input) and begins a new frame.
    /// Must be called once per frame before any ImGui.* UI calls.
    /// </summary>
    public void Update(GameWindow wnd, float dt)
    {
        // If a previous frame was started but never rendered, finalise it now so
        // BeginFrame state is consistent.
        if (_frameBegun)
        {
            ImGui.Render();
            _frameBegun = false;
        }

        var io = ImGui.GetIO();
        io.DisplaySize = new System.Numerics.Vector2(_windowWidth, _windowHeight);
        io.DisplayFramebufferScale = new System.Numerics.Vector2(1f, 1f);
        io.DeltaTime = dt > 0f ? dt : 1f / 60f;

        ProcessMouseInput(wnd, io);
        ProcessKeyboardInput(wnd, io);

        ImGui.NewFrame();
        _frameBegun = true;
    }

    private static void ProcessMouseInput(GameWindow wnd, ImGuiIOPtr io)
    {
        var m = wnd.MouseState;
        io.AddMousePosEvent(m.X, m.Y);
        io.AddMouseButtonEvent(0, m.IsButtonDown(MouseButton.Left));
        io.AddMouseButtonEvent(1, m.IsButtonDown(MouseButton.Right));
        io.AddMouseButtonEvent(2, m.IsButtonDown(MouseButton.Middle));
        if (m.ScrollDelta.Y != 0f)
            io.AddMouseWheelEvent(0f, m.ScrollDelta.Y);
    }

    private static void ProcessKeyboardInput(GameWindow wnd, ImGuiIOPtr io)
    {
        var kb = wnd.KeyboardState;
        foreach (var (k, ik) in s_keyMap)
            io.AddKeyEvent(ik, kb.IsKeyDown(k));
    }

    /// <summary>
    /// Feeds a typed Unicode code point into ImGui (hook from GameWindow.OnTextInput).
    /// </summary>
    public void PressChar(uint codepoint) => ImGui.GetIO().AddInputCharacter(codepoint);

    // -------------------------------------------------------------------------
    // Render (call AFTER all ImGui UI commands)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Finalises the ImGui frame and emits all draw calls.
    /// Call after all ImGui.* UI commands and before SwapBuffers.
    /// </summary>
    public void Render()
    {
        if (!_frameBegun) return;
        _frameBegun = false;

        ImGui.Render();
        RenderDrawData(ImGui.GetDrawData());
    }

    private void RenderDrawData(ImDrawDataPtr data)
    {
        if (data.CmdListsCount == 0) return;

        // --- Save GL state that ImGui will modify ---
        GL.GetInteger(GetPName.TextureBinding2D, out int prevTex);
        GL.GetInteger(GetPName.ArrayBufferBinding, out int prevAB);
        GL.GetInteger(GetPName.VertexArrayBinding, out int prevVA);
        bool prevBlend = GL.IsEnabled(EnableCap.Blend);
        bool prevCull = GL.IsEnabled(EnableCap.CullFace);
        bool prevDepth = GL.IsEnabled(EnableCap.DepthTest);
        bool prevScissor = GL.IsEnabled(EnableCap.ScissorTest);

        // --- Set ImGui render state ---
        GL.Enable(EnableCap.Blend);
        GL.BlendEquation(BlendEquationMode.FuncAdd);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        GL.Disable(EnableCap.CullFace);
        GL.Disable(EnableCap.DepthTest);
        GL.Enable(EnableCap.ScissorTest);
        // Ensure fill mode — wireframe debug toggle must not affect ImGui.
        GL.PolygonMode(MaterialFace.FrontAndBack, PolygonMode.Fill);

        // Orthographic projection maps ImGui screen coordinates to clip space.
        float L = data.DisplayPos.X;
        float R = data.DisplayPos.X + data.DisplaySize.X;
        float T = data.DisplayPos.Y;
        float B = data.DisplayPos.Y + data.DisplaySize.Y;
        var ortho = Matrix4.CreateOrthographicOffCenter(L, R, B, T, -1f, 1f);

        GL.UseProgram(_shaderProgram);
        GL.UniformMatrix4(_projLocation, false, ref ortho);
        GL.BindVertexArray(_vao);

        data.ScaleClipRects(ImGui.GetIO().DisplayFramebufferScale);

        for (int i = 0; i < data.CmdListsCount; i++)
        {
            // data.CmdLists is an nint (ImDrawList**) in this ImGui.NET version.
            // Use Marshal to read each pointer value safely without an unsafe block.
            nint listPtrVal = System.Runtime.InteropServices.Marshal.ReadIntPtr(
                data.CmdLists + i * System.IntPtr.Size);
            ImDrawListPtr cmdList;
            unsafe { cmdList = new ImDrawListPtr((ImGuiNET.ImDrawList*)(void*)listPtrVal); }

            // Upload vertices.
            int vtxBytes = cmdList.VtxBuffer.Size * Unsafe.SizeOf<ImDrawVert>();
            GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
            if (vtxBytes > _vertexBufferSize)
            {
                _vertexBufferSize = vtxBytes;
                GL.BufferData(BufferTarget.ArrayBuffer, _vertexBufferSize,
                              System.IntPtr.Zero, BufferUsageHint.DynamicDraw);
            }
            GL.BufferSubData(BufferTarget.ArrayBuffer, System.IntPtr.Zero, vtxBytes,
                             cmdList.VtxBuffer.Data);

            // Upload indices.
            int idxBytes = cmdList.IdxBuffer.Size * sizeof(ushort);
            GL.BindBuffer(BufferTarget.ElementArrayBuffer, _ebo);
            if (idxBytes > _indexBufferSize)
            {
                _indexBufferSize = idxBytes;
                GL.BufferData(BufferTarget.ElementArrayBuffer, _indexBufferSize,
                              System.IntPtr.Zero, BufferUsageHint.DynamicDraw);
            }
            GL.BufferSubData(BufferTarget.ElementArrayBuffer, System.IntPtr.Zero, idxBytes,
                             cmdList.IdxBuffer.Data);

            // Process draw commands.
            for (int j = 0; j < cmdList.CmdBuffer.Size; j++)
            {
                ImDrawCmdPtr cmd = cmdList.CmdBuffer[j];
                if (cmd.UserCallback != System.IntPtr.Zero) continue;

                GL.ActiveTexture(TextureUnit.Texture0);
                GL.BindTexture(TextureTarget.Texture2D, (int)cmd.TextureId);

                // ImGui clip rect is in top-left origin; GL.Scissor needs bottom-left.
                var cr = cmd.ClipRect;
                GL.Scissor(
                    (int)cr.X,
                    _windowHeight - (int)cr.W,
                    (int)(cr.Z - cr.X),
                    (int)(cr.W - cr.Y));

                // DrawElementsBaseVertex allows a single draw list to span > 65k verts.
                GL.DrawElementsBaseVertex(
                    PrimitiveType.Triangles,
                    (int)cmd.ElemCount,
                    DrawElementsType.UnsignedShort,
                    (System.IntPtr)(cmd.IdxOffset * sizeof(ushort)),
                    (int)cmd.VtxOffset);
            }
        }

        // --- Restore GL state ---
        if (!prevBlend) GL.Disable(EnableCap.Blend);
        if (prevCull) GL.Enable(EnableCap.CullFace);
        if (prevDepth) GL.Enable(EnableCap.DepthTest);
        if (!prevScissor) GL.Disable(EnableCap.ScissorTest);
        GL.BindTexture(TextureTarget.Texture2D, prevTex);
        GL.BindBuffer(BufferTarget.ArrayBuffer, prevAB);
        GL.BindVertexArray(prevVA);
    }

    // -------------------------------------------------------------------------
    // Key map
    // -------------------------------------------------------------------------

    private static Dictionary<Keys, ImGuiKey> BuildKeyMap() => new()
    {
        [Keys.Tab] = ImGuiKey.Tab,
        [Keys.Left] = ImGuiKey.LeftArrow,
        [Keys.Right] = ImGuiKey.RightArrow,
        [Keys.Up] = ImGuiKey.UpArrow,
        [Keys.Down] = ImGuiKey.DownArrow,
        [Keys.PageUp] = ImGuiKey.PageUp,
        [Keys.PageDown] = ImGuiKey.PageDown,
        [Keys.Home] = ImGuiKey.Home,
        [Keys.End] = ImGuiKey.End,
        [Keys.Insert] = ImGuiKey.Insert,
        [Keys.Delete] = ImGuiKey.Delete,
        [Keys.Backspace] = ImGuiKey.Backspace,
        [Keys.Space] = ImGuiKey.Space,
        [Keys.Enter] = ImGuiKey.Enter,
        [Keys.Escape] = ImGuiKey.Escape,
        [Keys.LeftControl] = ImGuiKey.LeftCtrl,
        [Keys.LeftShift] = ImGuiKey.LeftShift,
        [Keys.LeftAlt] = ImGuiKey.LeftAlt,
        [Keys.LeftSuper] = ImGuiKey.LeftSuper,
        [Keys.RightControl] = ImGuiKey.RightCtrl,
        [Keys.RightShift] = ImGuiKey.RightShift,
        [Keys.RightAlt] = ImGuiKey.RightAlt,
        [Keys.RightSuper] = ImGuiKey.RightSuper,
        [Keys.A] = ImGuiKey.A,
        [Keys.B] = ImGuiKey.B,
        [Keys.C] = ImGuiKey.C,
        [Keys.D] = ImGuiKey.D,
        [Keys.E] = ImGuiKey.E,
        [Keys.F] = ImGuiKey.F,
        [Keys.G] = ImGuiKey.G,
        [Keys.H] = ImGuiKey.H,
        [Keys.I] = ImGuiKey.I,
        [Keys.J] = ImGuiKey.J,
        [Keys.K] = ImGuiKey.K,
        [Keys.L] = ImGuiKey.L,
        [Keys.M] = ImGuiKey.M,
        [Keys.N] = ImGuiKey.N,
        [Keys.O] = ImGuiKey.O,
        [Keys.P] = ImGuiKey.P,
        [Keys.Q] = ImGuiKey.Q,
        [Keys.R] = ImGuiKey.R,
        [Keys.S] = ImGuiKey.S,
        [Keys.T] = ImGuiKey.T,
        [Keys.U] = ImGuiKey.U,
        [Keys.V] = ImGuiKey.V,
        [Keys.W] = ImGuiKey.W,
        [Keys.X] = ImGuiKey.X,
        [Keys.Y] = ImGuiKey.Y,
        [Keys.Z] = ImGuiKey.Z,
        [Keys.D0] = ImGuiKey._0,
        [Keys.D1] = ImGuiKey._1,
        [Keys.D2] = ImGuiKey._2,
        [Keys.D3] = ImGuiKey._3,
        [Keys.D4] = ImGuiKey._4,
        [Keys.D5] = ImGuiKey._5,
        [Keys.D6] = ImGuiKey._6,
        [Keys.D7] = ImGuiKey._7,
        [Keys.D8] = ImGuiKey._8,
        [Keys.D9] = ImGuiKey._9,
        [Keys.F1] = ImGuiKey.F1,
        [Keys.F2] = ImGuiKey.F2,
        [Keys.F3] = ImGuiKey.F3,
        [Keys.F4] = ImGuiKey.F4,
        [Keys.F5] = ImGuiKey.F5,
        [Keys.F6] = ImGuiKey.F6,
        [Keys.F7] = ImGuiKey.F7,
        [Keys.F8] = ImGuiKey.F8,
        [Keys.F9] = ImGuiKey.F9,
        [Keys.F10] = ImGuiKey.F10,
        [Keys.F11] = ImGuiKey.F11,
        [Keys.F12] = ImGuiKey.F12,
    };

    // -------------------------------------------------------------------------

    private static void ApplyGameStyle()
    {
        var style = ImGui.GetStyle();

        // #84C5C8 → (0.518, 0.773, 0.784)
        var accent = new System.Numerics.Vector4(0.518f, 0.773f, 0.784f, 1.00f);
        var accentDim = new System.Numerics.Vector4(0.518f, 0.773f, 0.784f, 0.70f);
        var accentBright = new System.Numerics.Vector4(0.62f, 0.86f, 0.87f, 1.00f);

        // No roundings
        style.WindowRounding = 0f;
        style.ChildRounding = 0f;
        style.FrameRounding = 0f;
        style.PopupRounding = 0f;
        style.ScrollbarRounding = 0f;
        style.GrabRounding = 0f;
        style.TabRounding = 0f;

        style.WindowBorderSize = 1f;
        style.FrameBorderSize = 0f;
        style.WindowPadding = new System.Numerics.Vector2(10f, 10f);
        style.FramePadding = new System.Numerics.Vector2(6f, 4f);
        style.ItemSpacing = new System.Numerics.Vector2(8f, 6f);

        var colors = style.Colors;
        colors[(int)ImGuiCol.Text] = new(0.92f, 0.93f, 0.94f, 1.00f);
        colors[(int)ImGuiCol.TextDisabled] = new(0.50f, 0.52f, 0.53f, 1.00f);
        colors[(int)ImGuiCol.WindowBg] = new(0.08f, 0.09f, 0.10f, 0.94f);
        colors[(int)ImGuiCol.ChildBg] = new(0.08f, 0.09f, 0.10f, 0.00f);
        colors[(int)ImGuiCol.PopupBg] = new(0.10f, 0.11f, 0.12f, 0.96f);
        colors[(int)ImGuiCol.Border] = new(0.28f, 0.30f, 0.31f, 0.60f);
        colors[(int)ImGuiCol.BorderShadow] = new(0.00f, 0.00f, 0.00f, 0.00f);
        colors[(int)ImGuiCol.FrameBg] = new(0.14f, 0.15f, 0.16f, 1.00f);
        colors[(int)ImGuiCol.FrameBgHovered] = new(0.20f, 0.22f, 0.23f, 1.00f);
        colors[(int)ImGuiCol.FrameBgActive] = accentDim;
        colors[(int)ImGuiCol.TitleBg] = new(0.06f, 0.07f, 0.08f, 1.00f);
        colors[(int)ImGuiCol.TitleBgActive] = new(0.10f, 0.11f, 0.12f, 1.00f);
        colors[(int)ImGuiCol.TitleBgCollapsed] = new(0.06f, 0.07f, 0.08f, 0.60f);
        colors[(int)ImGuiCol.MenuBarBg] = new(0.10f, 0.11f, 0.12f, 1.00f);
        colors[(int)ImGuiCol.ScrollbarBg] = new(0.08f, 0.09f, 0.10f, 0.80f);
        colors[(int)ImGuiCol.ScrollbarGrab] = new(0.28f, 0.30f, 0.31f, 1.00f);
        colors[(int)ImGuiCol.ScrollbarGrabHovered] = accentDim;
        colors[(int)ImGuiCol.ScrollbarGrabActive] = accent;
        colors[(int)ImGuiCol.CheckMark] = accent;
        colors[(int)ImGuiCol.SliderGrab] = accentDim;
        colors[(int)ImGuiCol.SliderGrabActive] = accent;
        colors[(int)ImGuiCol.Button] = new(0.16f, 0.18f, 0.19f, 1.00f);
        colors[(int)ImGuiCol.ButtonHovered] = accentDim;
        colors[(int)ImGuiCol.ButtonActive] = accent;
        colors[(int)ImGuiCol.Header] = new(0.16f, 0.18f, 0.19f, 1.00f);
        colors[(int)ImGuiCol.HeaderHovered] = accentDim;
        colors[(int)ImGuiCol.HeaderActive] = accent;
        colors[(int)ImGuiCol.Separator] = new(0.28f, 0.30f, 0.31f, 0.60f);
        colors[(int)ImGuiCol.SeparatorHovered] = accentDim;
        colors[(int)ImGuiCol.SeparatorActive] = accent;
        colors[(int)ImGuiCol.ResizeGrip] = new(0.28f, 0.30f, 0.31f, 0.40f);
        colors[(int)ImGuiCol.ResizeGripHovered] = accentDim;
        colors[(int)ImGuiCol.ResizeGripActive] = accent;
        colors[(int)ImGuiCol.Tab] = new(0.14f, 0.15f, 0.16f, 1.00f);
        colors[(int)ImGuiCol.TabHovered] = accentDim;
        colors[(int)ImGuiCol.TabActive] = accent;
        colors[(int)ImGuiCol.TextSelectedBg] = new(0.518f, 0.773f, 0.784f, 0.35f);
        colors[(int)ImGuiCol.NavHighlight] = accent;
        colors[(int)ImGuiCol.PlotLines] = accentBright;
        colors[(int)ImGuiCol.PlotLinesHovered] = accent;
        colors[(int)ImGuiCol.PlotHistogram] = accentBright;
        colors[(int)ImGuiCol.PlotHistogramHovered] = accent;
    }

    public void Dispose()
    {
        GL.DeleteVertexArray(_vao);
        GL.DeleteBuffer(_vbo);
        GL.DeleteBuffer(_ebo);
        GL.DeleteTexture(_fontTexture);
        GL.DeleteProgram(_shaderProgram);
        ImGui.DestroyContext();
    }
}
