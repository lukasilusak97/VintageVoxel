using ImGuiNET;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using VintageVoxel.Physics;
using VintageVoxel.Rendering;

namespace VintageVoxel;

/// <summary>
/// The main game window. Manages the OS window, the OpenGL context, and the game
/// loop. Subsystems are delegated to <see cref="WorldRenderer"/>,
/// <see cref="WorldStreamer"/>, and <see cref="InteractionHandler"/>.
/// </summary>
public class Game : GameWindow
{
    private GpuResourceManager _gpuResources = null!;
    private Shader _shader = null!;
    private Camera _camera = null!;
    private World _world = null!;
    private Texture _atlas = null!;

    private ImGuiController _imgui = null!;
    private DebugWindow _debugWindow = null!;
    private DebugState _debugState = null!;
    private bool _debugVisible = false;

    private string _savePath = WorldPersistence.DefaultSavePath;
    private string? _lastSaveStatus;

    private GameState _gameState = GameState.MainMenu;

    // ─── Main-menu sub-pages ──────────────────────────────────────────────────
    private enum MenuPage { Main, NewWorld, LoadWorld }
    private MenuPage _menuPage = MenuPage.Main;

    // New-world form
    private string _newWorldName = "New World";
    private string _newWorldSeedStr = "0";
    private int _newWorldType = 0;  // 0 = Normal, 1 = Flat

    // Load-world list
    private List<WorldPersistence.WorldInfo> _worldList = new();
    private int _selectedWorldIndex = -1;

    private readonly Inventory _inventory = new(Inventory.HotbarSize);
    private HUDRenderer _hud = null!;
    private readonly List<EntityItem> _entityItems = new();
    private EntityItemRenderer _entityRenderer = null!;

    private WorldRenderer _worldRenderer = null!;
    private WorldStreamer _worldStreamer = null!;
    private InteractionHandler _interaction = null!;

    private bool _firstMove = true;
    private Vector2 _lastMousePos;

    public Game(GameWindowSettings gameWindowSettings, NativeWindowSettings nativeWindowSettings)
        : base(gameWindowSettings, nativeWindowSettings)
    {
    }

    protected override void OnLoad()
    {
        base.OnLoad();

        _gpuResources = new GpuResourceManager();

        GL.ClearColor(0.53f, 0.81f, 0.92f, 1.0f);
        GL.Enable(EnableCap.DepthTest);
        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        GL.Enable(EnableCap.CullFace);
        GL.CullFace(CullFaceMode.Back);
        GL.FrontFace(FrontFaceDirection.Ccw);

        _shader = new Shader("Shaders/shader.vert", "Shaders/shader.frag");

        string assetsDir = Path.Combine(AppContext.BaseDirectory, "Assets");
        BlockRegistry.Load(Path.Combine(assetsDir, "blocks.json"));
        var textureNames = BlockRegistry.GetAllTextureNames();
        var textureTints = BlockRegistry.GetTextureTints();
        _atlas = TextureAtlas.Build(
            textureNames,
            Path.Combine(assetsDir, "Textures", "Block"),
            out var nameToIndex,
            textureTints);
        BlockRegistry.Initialize(nameToIndex);

        float aspect = Size.X / (float)Size.Y;
        _camera = new Camera(new Vector3(16f, 35f, 50f), 70f, aspect);

        _world = new World();
        _world.Update(_camera.Position, out var initial, out _);

        // Replace freshly-generated chunks with any saved counterparts before lighting.
        foreach (var key in initial)
        {
            if (WorldPersistence.TryLoadChunk(_savePath, key, out Chunk? saved))
                _world.ReplaceChunk(key, saved);
        }

        LightEngine.PropagateSunlight(_world);

        _imgui = new ImGuiController(Size.X, Size.Y);
        _debugWindow = new DebugWindow();
        _debugState = new DebugState();

        ItemRegistry.Load(Path.Combine(assetsDir, "items.json"));
        var torch = ItemRegistry.All.Values.FirstOrDefault(i =>
            i.Name.Equals("torch", StringComparison.OrdinalIgnoreCase));
        if (torch != null)
            _inventory.AddItem(torch, 1);

        _hud = new HUDRenderer(_gpuResources, FramebufferSize.X, FramebufferSize.Y);
        _entityRenderer = new EntityItemRenderer(_gpuResources);

        _worldRenderer = new WorldRenderer(_gpuResources, _world, _shader, _atlas,
                                           _entityRenderer, _inventory);

        // Upload initial chunks (lighting already computed above).
        foreach (var key in initial)
            _worldRenderer.RebuildChunk(key);

        // Register any MODEL blocks saved to disk as placed models.
        foreach (var key in initial)
            if (_world.Chunks.TryGetValue(key, out var ic)) _worldRenderer.ScanChunkForPlacedModels(ic);

        _worldStreamer = new WorldStreamer(_world, _worldRenderer, _savePath);
        _interaction = new InteractionHandler(_world, _camera, _inventory,
                                              _worldRenderer, _entityItems, _savePath);
    }

    protected override void OnUpdateFrame(FrameEventArgs args)
    {
        base.OnUpdateFrame(args);

        float dt = (float)args.Time;

        Profiler.Begin("ImGui Update");
        _imgui.Update(this, dt);
        Profiler.End("ImGui Update");

        if (_gameState != GameState.Playing)
            return;

        Profiler.Begin("Physics");
        PhysicsSystem.Update(_camera, _world, KeyboardState, dt);
        Profiler.End("Physics");

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
            _firstMove = true;
        }

        _worldStreamer.Update(_camera.Position);
    }

    protected override void OnRenderFrame(FrameEventArgs args)
    {
        base.OnRenderFrame(args);

        Profiler.Begin("GL Clear");
        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        Profiler.End("GL Clear");

        _worldRenderer.Render(_camera, _entityItems, _debugState,
                              _gameState == GameState.Playing,
                              FramebufferSize.X, FramebufferSize.Y);

        Profiler.Begin("HUD");
        if (_gameState == GameState.Playing)
        {
            _hud.Render(_inventory, _atlas, FramebufferSize.X, FramebufferSize.Y);
            _worldRenderer.RenderHotbarItems3D(FramebufferSize.X, FramebufferSize.Y);
            DrawHotbarCounts();
        }
        Profiler.End("HUD");

        if (_debugVisible && _gameState == GameState.Playing)
        {
            _debugWindow.Render(
                fps: (float)(1.0 / args.Time),
                frameTimeMs: (float)(args.Time * 1000.0),
                playerPos: _camera.Position,
                chunksLoaded: _worldRenderer.ChunkCount,
                creativeMode: _camera.CreativeMode,
                heldItem: _inventory.HeldStack,
                hotbarSlot: _inventory.SelectedSlot,
                debugState: _debugState,
                saveStatus: _lastSaveStatus);
        }

        if (_gameState == GameState.MainMenu)
            DrawMainMenu();
        else if (_gameState == GameState.Paused)
            DrawPauseMenu();

        Profiler.Begin("ImGui");
        _imgui.Render();
        Profiler.End("ImGui");

        Profiler.Begin("SwapBuffers");
        SwapBuffers();
        Profiler.End("SwapBuffers");
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);

        if (_camera is null || _world is null) return;
        if (_gameState != GameState.Playing) return;

        _interaction.HandleMouseDown(e);
    }

    protected override void OnKeyDown(KeyboardKeyEventArgs e)
    {
        base.OnKeyDown(e);

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
            }
            return;
        }

        if (e.Key == Keys.F3 && _gameState == GameState.Playing)
        {
            _debugVisible = !_debugVisible;
            CursorState = _debugVisible ? CursorState.Normal : CursorState.Grabbed;
        }

        if (e.Key == Keys.F && _gameState == GameState.Playing)
        {
            _camera.CreativeMode = !_camera.CreativeMode;
            _camera.Velocity = Vector3.Zero;
        }

        if (e.Key == Keys.S &&
            (e.Modifiers & KeyModifiers.Control) != 0 &&
            _gameState == GameState.Playing)
        {
            _lastSaveStatus = _interaction.SaveWorld();
        }

        if (e.Key == Keys.Q && _gameState == GameState.Playing)
            _interaction.HandleItemDrop();
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

        WorldPersistence.SaveAll(_savePath, _world);

        GL.BindVertexArray(0);
        _worldRenderer.Dispose();
        _atlas.Dispose();
        _shader.Dispose();
        _hud.Dispose();
        _entityRenderer.Dispose();
        _gpuResources.Dispose();
        _imgui.Dispose();
    }

    // -------------------------------------------------------------------------
    // State-machine transitions & ImGui menus
    // -------------------------------------------------------------------------

    private void TransitionToPlaying()
    {
        _gameState = GameState.Playing;
        _firstMove = true;
        CursorState = _debugVisible ? CursorState.Normal : CursorState.Grabbed;
    }

    /// <summary>
    /// Creates a brand-new world: applies generation settings, wipes any existing
    /// save for that name, writes metadata, then enters gameplay.
    /// </summary>
    private void StartNewWorld(string name, int seed, bool flat)
    {
        WorldGenConfig.FlatWorld = flat;
        NoiseGenerator.SetSeed(seed);
        _savePath = WorldPersistence.GetSavePath(name);
        if (Directory.Exists(_savePath))
            Directory.Delete(_savePath, recursive: true);
        WorldPersistence.SaveMeta(_savePath, name, seed, flat);
        RebuildWorld();
    }

    /// <summary>
    /// Loads an existing saved world: restores its generation settings for newly
    /// streamed chunks, then enters gameplay.
    /// </summary>
    private void LoadExistingWorld(WorldPersistence.WorldInfo info)
    {
        _savePath = info.SavePath;
        var (_, seed, flat) = WorldPersistence.LoadMeta(_savePath);
        WorldGenConfig.FlatWorld = flat;
        NoiseGenerator.SetSeed(seed);
        RebuildWorld();
    }

    /// <summary>
    /// Tears down the current world and renderer, builds a fresh world using the
    /// current <see cref="_savePath"/> and world-gen settings, loads any saved
    /// chunks from disk, then transitions to <see cref="GameState.Playing"/>.
    /// </summary>
    private void RebuildWorld()
    {
        _worldRenderer.Dispose();
        _world = new World();
        _camera.Position = new Vector3(16f, 35f, 16f);

        _world.Update(_camera.Position, out var initial, out _);
        foreach (var key in initial)
        {
            if (WorldPersistence.TryLoadChunk(_savePath, key, out Chunk? saved))
                _world.ReplaceChunk(key, saved);
        }
        LightEngine.PropagateSunlight(_world);

        _worldRenderer = new WorldRenderer(_gpuResources, _world, _shader, _atlas,
                                           _entityRenderer, _inventory);
        foreach (var key in initial)
        {
            _worldRenderer.RebuildChunk(key);
            if (_world.Chunks.TryGetValue(key, out var c))
                _worldRenderer.ScanChunkForPlacedModels(c);
        }

        _entityItems.Clear();
        _worldStreamer = new WorldStreamer(_world, _worldRenderer, _savePath);
        _interaction = new InteractionHandler(_world, _camera, _inventory,
                                              _worldRenderer, _entityItems, _savePath);
        _lastSaveStatus = null;
        TransitionToPlaying();
    }

    private void TransitionToPaused()
    {
        _gameState = GameState.Paused;
        CursorState = CursorState.Normal;
        _firstMove = true;
    }

    private void DrawHotbarCounts()
    {
        var drawList = ImGui.GetForegroundDrawList();
        var displaySize = ImGui.GetIO().DisplaySize;

        float slotSize = GameConstants.Render.HotbarSlotSize;
        float slotGap = GameConstants.Render.HotbarSlotGap;
        int slots = Inventory.HotbarSize;
        float totalW = slots * slotSize + (slots - 1) * slotGap;
        float x0 = (displaySize.X - totalW) * 0.5f;
        float y0 = displaySize.Y - GameConstants.Render.HotbarBottomPad - slotSize;
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
    /// Routes to whichever main-menu sub-page is currently active.
    /// Called from <see cref="OnRenderFrame"/> when state is <see cref="GameState.MainMenu"/>.
    /// </summary>
    private void DrawMainMenu()
    {
        switch (_menuPage)
        {
            case MenuPage.Main: DrawMainMenuPage(); break;
            case MenuPage.NewWorld: DrawNewWorldPage(); break;
            case MenuPage.LoadWorld: DrawLoadWorldPage(); break;
        }
    }

    /// <summary>Helper: positions and opens a centered, auto-sized ImGui window.</summary>
    private static void BeginCenteredWindow(string id)
    {
        var ds = ImGui.GetIO().DisplaySize;
        ImGui.SetNextWindowPos(
            new System.Numerics.Vector2(ds.X * 0.5f, ds.Y * 0.5f),
            ImGuiCond.Always,
            new System.Numerics.Vector2(0.5f, 0.5f));
        ImGui.SetNextWindowBgAlpha(0.92f);
        ImGui.Begin(id,
            ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoDecoration |
            ImGuiWindowFlags.AlwaysAutoResize |
            ImGuiWindowFlags.NoSavedSettings);
    }

    private void DrawMainMenuPage()
    {
        BeginCenteredWindow("##mainmenu");

        ImGui.TextColored(new System.Numerics.Vector4(0.4f, 0.9f, 1.0f, 1.0f), "VintageVoxel");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (ImGui.Button("New World", new System.Numerics.Vector2(220f, 40f)))
        {
            _newWorldName = "New World";
            _newWorldSeedStr = "0";
            _newWorldType = 0;
            _menuPage = MenuPage.NewWorld;
        }

        ImGui.Spacing();

        if (ImGui.Button("Load World", new System.Numerics.Vector2(220f, 40f)))
        {
            _worldList = WorldPersistence.ListWorlds();
            _selectedWorldIndex = _worldList.Count > 0 ? 0 : -1;
            _menuPage = MenuPage.LoadWorld;
        }

        ImGui.Spacing();

        if (ImGui.Button("Quit", new System.Numerics.Vector2(220f, 40f)))
            Close();

        ImGui.End();
    }

    private void DrawNewWorldPage()
    {
        BeginCenteredWindow("##newworld");

        ImGui.TextColored(new System.Numerics.Vector4(0.4f, 0.9f, 1.0f, 1.0f), "Create New World");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.Text("World Name");
        ImGui.SetNextItemWidth(260f);
        ImGui.InputText("##wname", ref _newWorldName, 64);
        ImGui.Spacing();

        ImGui.Text("World Type");
        ImGui.RadioButton("Normal Generation", ref _newWorldType, 0);
        ImGui.RadioButton("Flat World", ref _newWorldType, 1);
        ImGui.Spacing();

        if (_newWorldType == 0)
        {
            ImGui.Text("Seed  (0 = default)");
            ImGui.SetNextItemWidth(260f);
            ImGui.InputText("##wseed", ref _newWorldSeedStr, 20);
            ImGui.Spacing();
        }

        ImGui.Separator();
        ImGui.Spacing();

        if (ImGui.Button("Create World", new System.Numerics.Vector2(160f, 36f)))
        {
            if (!int.TryParse(_newWorldSeedStr.Trim(), out int seed))
                seed = Math.Abs(_newWorldSeedStr.GetHashCode());
            if (string.IsNullOrWhiteSpace(_newWorldName))
                _newWorldName = "New World";
            StartNewWorld(_newWorldName, seed, _newWorldType == 1);
        }

        ImGui.SameLine();

        if (ImGui.Button("Back", new System.Numerics.Vector2(80f, 36f)))
            _menuPage = MenuPage.Main;

        ImGui.End();
    }

    private void DrawLoadWorldPage()
    {
        BeginCenteredWindow("##loadworld");

        ImGui.TextColored(new System.Numerics.Vector4(0.4f, 0.9f, 1.0f, 1.0f), "Load World");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (_worldList.Count == 0)
        {
            ImGui.TextDisabled("No saved worlds found.");
        }
        else
        {
            if (ImGui.BeginListBox("##worlds", new System.Numerics.Vector2(300f, 180f)))
            {
                for (int i = 0; i < _worldList.Count; i++)
                {
                    bool selected = _selectedWorldIndex == i;
                    if (ImGui.Selectable(_worldList[i].DisplayName, selected))
                        _selectedWorldIndex = i;
                    if (selected)
                        ImGui.SetItemDefaultFocus();
                }
                ImGui.EndListBox();
            }
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        bool canLoad = _selectedWorldIndex >= 0 && _selectedWorldIndex < _worldList.Count;
        if (!canLoad) ImGui.BeginDisabled();
        if (ImGui.Button("Play", new System.Numerics.Vector2(120f, 36f)) && canLoad)
            LoadExistingWorld(_worldList[_selectedWorldIndex]);
        if (!canLoad) ImGui.EndDisabled();

        ImGui.SameLine();

        if (ImGui.Button("Back", new System.Numerics.Vector2(80f, 36f)))
            _menuPage = MenuPage.Main;

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
            _lastSaveStatus = _interaction.SaveWorld();
            TransitionToPlaying();
        }

        ImGui.Spacing();

        if (ImGui.Button("New World", new System.Numerics.Vector2(220f, 40f)))
        {
            _newWorldName = "New World";
            _newWorldSeedStr = "0";
            _newWorldType = 0;
            _menuPage = MenuPage.NewWorld;
            _gameState = GameState.MainMenu;
            CursorState = CursorState.Normal;
        }

        ImGui.Spacing();

        if (ImGui.Button("Quit", new System.Numerics.Vector2(220f, 40f)))
            Close();

        ImGui.End();
    }
}

