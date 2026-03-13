using ImGuiNET;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using VintageVoxel.Networking;
using VintageVoxel.Physics;
using VintageVoxel.Rendering;
using VintageVoxel.UI;
using SNVector3 = System.Numerics.Vector3;

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
    private World? _world;
    private Texture _atlas = null!;

    private ImGuiController _imgui = null!;
    private DebugWindow _debugWindow = null!;
    private DebugState _debugState = null!;
    private bool _debugVisible = false;

    private string _savePath = WorldPersistence.DefaultSavePath;
    private string? _lastSaveStatus;

    private GameState _gameState = GameState.MainMenu;

    // ─── Main-menu sub-pages ──────────────────────────────────────────────────
    private enum MenuPage { Main, NewWorld, LoadWorld, Multiplayer }
    private MenuPage _menuPage = MenuPage.Main;

    // New-world form
    private string _newWorldName = "New World";
    private string _newWorldSeedStr = "0";
    private int _newWorldType = 0;  // 0 = Normal, 1 = Flat

    // Load-world list
    private List<WorldPersistence.WorldInfo> _worldList = new();
    private int _selectedWorldIndex = -1;
    private bool _confirmDeleteWorld = false;

    private Player _player = new();
    private readonly InventoryWindow _inventoryWindow = new();
    private readonly CreativeInventoryWindow _creativeWindow = new();
    private readonly List<(ItemStack Stack, float DispX, float DispY, float Size)> _hotbarRenderTargets = new();
    private readonly List<EntityItem> _entityItems = new();
    private EntityItemRenderer _entityRenderer = null!;

    private WorldRenderer? _worldRenderer;
    private WorldStreamer? _worldStreamer;
    private InteractionHandler? _interaction;

    // ─── Vehicle ───────────────────────────────────────────────────────────
    private VehicleRenderer _vehicleRenderer = null!;
    private VehicleDebugRenderer _vehicleDebugRenderer = null!;
    private readonly List<Vehicle> _vehicles = new();

    private bool _firstMove = true;
    private Vector2 _lastMousePos;

    // ─── Multiplayer ──────────────────────────────────────────────────────────
    private GameServer? _gameServer;
    private GameClient? _gameClient;
    private readonly LanDiscovery _lanDiscovery = new();
    private readonly Dictionary<int, RemotePlayer> _remotePlayers = new();
    private RemotePlayerRenderer _remotePlayerRenderer = null!;
    private readonly ChatWindow _chatWindow = new();

    // Multiplayer menu form state
    private string _playerName = "Player";  // loaded from disk in OnLoad
    private string _joinIp = "127.0.0.1";
    // Host-existing-world list (populated when Multiplayer page opens)
    private List<WorldPersistence.WorldInfo> _hostWorldList = new();
    private int _selectedHostWorldIndex = -1;
    private string _multiStatus = "";
    private float _stateTimer;            // seconds since last SendPlayerState
    private const float StateInterval = 1f / 20f; // 20 Hz

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

        _imgui = new ImGuiController(Size.X, Size.Y);
        _debugWindow = new DebugWindow();
        _debugState = new DebugState();

        _remotePlayerRenderer = new RemotePlayerRenderer();
        _playerName = WorldPersistence.LoadPlayerName("Player");

        ItemRegistry.Load(Path.Combine(assetsDir, "items.json"));
        EntityRegistry.Load(Path.Combine(assetsDir, "entities.json"));

        _entityRenderer = new EntityItemRenderer(_gpuResources);

        _vehicleRenderer = new VehicleRenderer();
        _vehicleDebugRenderer = new VehicleDebugRenderer();
    }

    protected override void OnUpdateFrame(FrameEventArgs args)
    {
        base.OnUpdateFrame(args);

        float dt = (float)args.Time;

        Profiler.Begin("ImGui Update");
        _imgui.Update(this, dt);
        Profiler.End("ImGui Update");

        // Tick networking and LAN discovery in every state so connection handshake
        // completes and transitions to Playing even from the main menu.
        _gameClient?.Tick();
        _lanDiscovery.Poll();

        if (_gameState != GameState.Playing)
            return;

        Profiler.Begin("Physics");
        var occupiedVehicle = _vehicles.Find(v => v.IsOccupied);
        if (occupiedVehicle != null)
        {
            occupiedVehicle.Update(KeyboardState, dt);
            occupiedVehicle.UpdateCamera(_camera);
            // Still tick other vehicles so they don't freeze mid-air.
            foreach (var v in _vehicles)
                if (v != occupiedVehicle) v.Update(KeyboardState, dt);
        }
        else
        {
            PhysicsSystem.Update(_camera, _world, KeyboardState, dt);
            // Still tick vehicle physics when not driving so they don't freeze mid-air.
            foreach (var v in _vehicles)
                v.Update(KeyboardState, dt);
        }
        Profiler.End("Physics");

        Profiler.Begin("Entities");
        for (int i = _entityItems.Count - 1; i >= 0; i--)
        {
            _entityItems[i].Update(_world, dt);
            if (_entityItems[i].PickupCooldown <= 0f &&
                (_camera.FeetPosition - _entityItems[i].Position).Length < EntityItem.PickupRadius)
            {
                var entity = _entityItems[i];
                _player.Inventory.AddItem(entity.Item, entity.Count);
                _entityItems.RemoveAt(i);
                if (entity.NetworkId >= 0)
                    _gameClient?.SendPickupEntity(entity.NetworkId);
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

        // ── Multiplayer tick ─────────────────────────────────────────────────
        if (_gameClient != null)
        {
            // Send position at 20 Hz.
            _stateTimer += dt;
            if (_stateTimer >= StateInterval)
            {
                _stateTimer = 0f;
                _gameClient.SendPlayerState(_camera.FeetPosition, _camera.Yaw, _camera.Pitch);
            }

            // Advance interpolation for every remote player.
            foreach (var rp in _remotePlayers.Values)
                rp.Update(dt);

            // Return cursor to game when chat input closes.
            if (!_chatWindow.IsInputOpen && CursorState == CursorState.Normal && !_debugVisible && !_inventoryWindow.IsOpen)
                CursorState = CursorState.Grabbed;
        }

        _worldStreamer?.Update(_camera.Position);
    }

    protected override void OnRenderFrame(FrameEventArgs args)
    {
        base.OnRenderFrame(args);

        Profiler.Begin("GL Clear");
        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        Profiler.End("GL Clear");

        _worldRenderer?.Render(_camera, _entityItems, _debugState,
                               _gameState == GameState.Playing,
                               FramebufferSize.X, FramebufferSize.Y);

        // Render vehicles.
        if (_gameState == GameState.Playing && _vehicles.Count > 0)
        {
            foreach (var v in _vehicles)
            {
                v.Render(_camera);
                if (_debugState.ShowVehicleDebug)
                    _vehicleDebugRenderer.Render(v, _camera);
            }
        }

        // Render remote player box-man models (before ImGui so tags draw over them).
        if (_gameState == GameState.Playing && _remotePlayers.Count > 0)
            _remotePlayerRenderer.Render(_remotePlayers.Values, _camera,
                                         FramebufferSize.X, FramebufferSize.Y);

        Profiler.Begin("HUD");
        if (_gameState == GameState.Playing)
            DrawHUDImGui(_player);
        Profiler.End("HUD");

        if (_gameState == GameState.Playing)
        {
            _inventoryWindow.Draw(_player.Inventory);
            if (_inventoryWindow.IsOpen)
                _creativeWindow.Draw(_inventoryWindow);
        }

        if (_debugVisible && _gameState == GameState.Playing && _worldRenderer != null)
        {
            _debugWindow.Render(
                fps: (float)(1.0 / args.Time),
                frameTimeMs: (float)(args.Time * 1000.0),
                playerPos: _camera.Position,
                chunksLoaded: _worldRenderer.ChunkCount,
                creativeMode: _camera.CreativeMode,
                heldItem: _player.Inventory.HeldStack,
                hotbarSlot: _player.Inventory.SelectedSlot,
                debugState: _debugState,
                saveStatus: _lastSaveStatus);
        }

        if (_gameState == GameState.MainMenu)
            DrawMainMenu();
        else if (_gameState == GameState.Paused)
            DrawPauseMenu();

        // Chat overlay + remote-player name tags (multiplayer only).
        if (_gameState == GameState.Playing && _gameClient != null)
        {
            _chatWindow.Draw((float)args.Time, ImGui.GetIO().DisplaySize.X, ImGui.GetIO().DisplaySize.Y);
            _remotePlayerRenderer.RenderNameTags(_remotePlayers.Values, _camera,
                                                  FramebufferSize.X, FramebufferSize.Y);
        }

        Profiler.Begin("ImGui");
        _imgui.Render();
        Profiler.End("ImGui");

        // 3-D item previews rendered AFTER ImGui so the window/slot backgrounds
        // don't paint over them.  Slot borders remain visible because scissor
        // insets by 4 px inside each slot rect.
        if (_gameState == GameState.Playing && _worldRenderer != null)
        {
            var ds = ImGui.GetIO().DisplaySize;
            if (_hotbarRenderTargets.Count > 0)
                _worldRenderer.RenderInventoryItems3D(
                    _hotbarRenderTargets, ds.X, ds.Y,
                    FramebufferSize.X, FramebufferSize.Y);
            if (_inventoryWindow.IsOpen && _inventoryWindow.SlotRenderTargets.Count > 0)
                _worldRenderer.RenderInventoryItems3D(
                    _inventoryWindow.SlotRenderTargets, ds.X, ds.Y,
                    FramebufferSize.X, FramebufferSize.Y);
            if (_inventoryWindow.IsOpen && _creativeWindow.SlotRenderTargets.Count > 0)
                _worldRenderer.RenderInventoryItems3D(
                    _creativeWindow.SlotRenderTargets, ds.X, ds.Y,
                    FramebufferSize.X, FramebufferSize.Y);
        }

        Profiler.Begin("SwapBuffers");
        SwapBuffers();
        Profiler.End("SwapBuffers");
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);

        if (_camera is null || _world is null || _interaction is null) return;
        if (_gameState != GameState.Playing) return;
        if (_inventoryWindow.IsOpen) return;

        _interaction?.HandleMouseDown(e);
    }

    protected override void OnKeyDown(KeyboardKeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.Key == Keys.Escape)
        {
            // Close inventory first; only fall through to pause if already closed.
            if (_inventoryWindow.IsOpen && _gameState == GameState.Playing)
            {
                CloseInventory();
                return;
            }

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

        if (e.Key == Keys.E && _gameState == GameState.Playing)
        {
            // Try vehicle enter/leave first.
            foreach (var v in _vehicles)
            {
                if (v.TryToggle(_camera.Position))
                {
                    if (!v.IsOccupied)
                    {
                        // Exiting: restore player position beside the vehicle.
                        _camera.Position = v.GetExitPosition();
                        _camera.Velocity = OpenTK.Mathematics.Vector3.Zero;
                    }
                    return;
                }
            }

            if (_inventoryWindow.IsOpen)
                CloseInventory();
            else
                OpenInventory();
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
            _lastSaveStatus = _interaction?.SaveWorld();
        }

        if (e.Key == Keys.Q && _gameState == GameState.Playing)
            _interaction?.HandleItemDrop();

        // Open chat input in multiplayer.
        if (e.Key == Keys.T && _gameState == GameState.Playing && _gameClient != null)
        {
            _chatWindow.OpenInput();
            // Release cursor while typing.
            CursorState = CursorState.Normal;
            _firstMove = true;
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
            _player.Inventory.ScrollHotbar(delta);
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

        // Disconnect / stop server before saving so no save races occur.
        _gameClient?.Disconnect();
        _gameServer?.Stop();
        _lanDiscovery.Dispose();

        if (_world != null)
        {
            WorldPersistence.SavePlayer(_savePath, _player, _camera.Position);
            WorldPersistence.SaveAll(_savePath, _world);
        }

        GL.BindVertexArray(0);
        foreach (var v in _vehicles)
            v.Dispose();
        _vehicles.Clear();
        _vehicleRenderer?.Dispose();
        _vehicleDebugRenderer?.Dispose();
        _remotePlayerRenderer?.Dispose();
        _worldRenderer?.Dispose();
        _atlas.Dispose();
        _shader.Dispose();
        _entityRenderer.Dispose();
        _gpuResources.Dispose();
        _imgui.Dispose();
    }

    // -------------------------------------------------------------------------
    // Entity spawning
    // -------------------------------------------------------------------------

    /// <summary>
    /// Spawns an entity at the given world-space position. Called by
    /// <see cref="InteractionHandler"/> when an entity item is placed.
    /// </summary>
    private void SpawnEntity(int entityId, OpenTK.Mathematics.Vector3 position)
    {
        var def = EntityRegistry.Get(entityId);
        if (def == null || _world == null) return;

        if (string.Equals(def.Type, "vehicle", StringComparison.OrdinalIgnoreCase))
        {
            var setup = EntityRegistry.GetVehicleSetup(entityId);
            var spawnPos = new SNVector3(position.X, position.Y, position.Z);
            _vehicles.Add(new Vehicle(_world, spawnPos, _vehicleRenderer, setup));
        }
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
        _worldRenderer?.Dispose();
        _world = new World();
        _camera.Position = new Vector3(16f, 35f, 16f);

        _world.Update(_camera.Position, out var initial, out _);
        foreach (var key in initial)
        {
            if (WorldPersistence.TryLoadChunk(_savePath, key, out Chunk? saved))
                _world.ReplaceChunk(key, saved);
        }
        LightEngine.PropagateSunlight(_world);

        // Restore saved player (position + inventory + stats) or start fresh.
        if (WorldPersistence.TryLoadPlayer(_savePath, out var loadedPlayer, out var loadedPos))
        {
            _player = loadedPlayer;
            _camera.Position = loadedPos;
        }
        else
        {
            _player = new Player();
        }

        _worldRenderer = new WorldRenderer(_gpuResources, _world, _shader, _atlas,
                                           _entityRenderer, _player.Inventory);
        foreach (var key in initial)
        {
            _worldRenderer.RebuildChunk(key);
            if (_world.Chunks.TryGetValue(key, out var c))
                _worldRenderer.ScanChunkForPlacedModels(c);
        }

        _entityItems.Clear();
        foreach (var v in _vehicles)
            v.Dispose();
        _vehicles.Clear();
        _worldStreamer = new WorldStreamer(_world, _worldRenderer, _savePath);
        _interaction = new InteractionHandler(_world, _camera, _player.Inventory,
                                              _worldRenderer, _entityItems, _savePath);
        _interaction.OnEntitySpawn = SpawnEntity;
        _interaction.NetworkClient = null; // single-player: no networking
        _lastSaveStatus = null;
        TransitionToPlaying();
    }

    private void OpenInventory()
    {
        _inventoryWindow.IsOpen = true;
        CursorState = CursorState.Normal;
        _firstMove = true;
    }

    private void CloseInventory()
    {
        _inventoryWindow.IsOpen = false;
        _inventoryWindow.OnClose(_player.Inventory);
        CursorState = _debugVisible ? CursorState.Normal : CursorState.Grabbed;
    }

    private void TransitionToPaused()
    {
        // Ensure inventory is dismissed before the pause menu takes over.
        if (_inventoryWindow.IsOpen)
        {
            _inventoryWindow.IsOpen = false;
            _inventoryWindow.OnClose(_player.Inventory);
        }
        _gameState = GameState.Paused;
        CursorState = CursorState.Normal;
        _firstMove = true;
    }

    /// <summary>
    /// Draws the crosshair, hotbar slots, and item count labels using the ImGui
    /// foreground draw list (no separate OpenGL shader needed).
    /// Also fills <see cref="_hotbarRenderTargets"/> so 3-D item previews can be
    /// rendered after <see cref="ImGuiController.Render"/>.
    /// </summary>
    private void DrawHUDImGui(Player player)
    {
        var inventory = player.Inventory;
        var dl = ImGui.GetForegroundDrawList();
        var ds = ImGui.GetIO().DisplaySize;

        // ── Crosshair ─────────────────────────────────────────────────────────
        float cx = ds.X * 0.5f;
        float cy = ds.Y * 0.5f;
        // Shadow
        dl.AddRectFilled(new System.Numerics.Vector2(cx - 11f, cy - 2f),
                         new System.Numerics.Vector2(cx + 11f, cy + 2f), 0x66000000);
        dl.AddRectFilled(new System.Numerics.Vector2(cx - 2f, cy - 11f),
                         new System.Numerics.Vector2(cx + 2f, cy + 11f), 0x66000000);
        // White bars
        dl.AddRectFilled(new System.Numerics.Vector2(cx - 10f, cy - 1f),
                         new System.Numerics.Vector2(cx + 10f, cy + 1f), 0xE6FFFFFF);
        dl.AddRectFilled(new System.Numerics.Vector2(cx - 1f, cy - 10f),
                         new System.Numerics.Vector2(cx + 1f, cy + 10f), 0xE6FFFFFF);

        // ── Hotbar ────────────────────────────────────────────────────────────
        int count = Inventory.HotbarSize;
        float slotSz = GameConstants.Render.HotbarSlotSize;
        float slotGp = GameConstants.Render.HotbarSlotGap;
        float totalW = count * slotSz + (count - 1) * slotGp;
        float x0 = (ds.X - totalW) * 0.5f;
        float y0 = ds.Y - GameConstants.Render.HotbarBottomPad - slotSz;
        const float BorderR = 3f;

        _hotbarRenderTargets.Clear();

        // ── HP & Stamina bars ─────────────────────────────────────────────────
        const float BarH = 8f;
        const float BarGap = 6f;  // gap between the two bars
        float barY = y0 - BarH - 8f;
        float halfW = (totalW - BarGap) * 0.5f;

        // HP — left half, red
        float hpFrac = MathF.Max(0f, MathF.Min(1f, player.Hp / player.MaxHp));
        // background
        dl.AddRectFilled(
            new System.Numerics.Vector2(x0, barY),
            new System.Numerics.Vector2(x0 + halfW, barY + BarH),
            0x99000000, 2f);
        // fill
        if (hpFrac > 0f)
            dl.AddRectFilled(
                new System.Numerics.Vector2(x0, barY),
                new System.Numerics.Vector2(x0 + halfW * hpFrac, barY + BarH),
                0xCC2255EE, 2f);  // AABBGGRR → red
        // border
        dl.AddRect(
            new System.Numerics.Vector2(x0, barY),
            new System.Numerics.Vector2(x0 + halfW, barY + BarH),
            0xCCFFFFFF, 2f);
        // label
        string hpLabel = $"♥ {(int)player.Hp}/{(int)player.MaxHp}";
        var hpTs = ImGui.CalcTextSize(hpLabel);
        float hpTx = x0 + (halfW - hpTs.X) * 0.5f;
        float hpTy = barY + (BarH - hpTs.Y) * 0.5f;
        dl.AddText(new System.Numerics.Vector2(hpTx + 1, hpTy + 1), 0xAA000000, hpLabel);
        dl.AddText(new System.Numerics.Vector2(hpTx, hpTy), 0xFFFFFFFF, hpLabel);

        // Stamina — right half, green-yellow
        float stFrac = MathF.Max(0f, MathF.Min(1f, player.Stamina / player.MaxStamina));
        float stX0 = x0 + halfW + BarGap;
        // background
        dl.AddRectFilled(
            new System.Numerics.Vector2(stX0, barY),
            new System.Numerics.Vector2(stX0 + halfW, barY + BarH),
            0x99000000, 2f);
        // fill
        if (stFrac > 0f)
            dl.AddRectFilled(
                new System.Numerics.Vector2(stX0, barY),
                new System.Numerics.Vector2(stX0 + halfW * stFrac, barY + BarH),
                0xCC00CC44, 2f);  // AABBGGRR → green
        // border
        dl.AddRect(
            new System.Numerics.Vector2(stX0, barY),
            new System.Numerics.Vector2(stX0 + halfW, barY + BarH),
            0xCCFFFFFF, 2f);
        // label
        string stLabel = $"⚡ {(int)player.Stamina}/{(int)player.MaxStamina}";
        var stTs = ImGui.CalcTextSize(stLabel);
        float stTx = stX0 + (halfW - stTs.X) * 0.5f;
        float stTy = barY + (BarH - stTs.Y) * 0.5f;
        dl.AddText(new System.Numerics.Vector2(stTx + 1, stTy + 1), 0xAA000000, stLabel);
        dl.AddText(new System.Numerics.Vector2(stTx, stTy), 0xFFFFFFFF, stLabel);

        for (int i = 0; i < count; i++)
        {
            float sx = x0 + i * (slotSz + slotGp);
            bool sel = i == inventory.SelectedSlot;

            // Border
            uint borderCol = sel ? 0xFFFFFFFF : 0xE68C8C8C;
            dl.AddRectFilled(
                new System.Numerics.Vector2(sx - 2f, y0 - 2f),
                new System.Numerics.Vector2(sx + slotSz + 2f, y0 + slotSz + 2f),
                borderCol, BorderR + 1f);

            // Background — semi-transparent so 3-D items show through
            uint bgCol = sel ? 0x73595959u : 0x521F1F1Fu;
            dl.AddRectFilled(
                new System.Numerics.Vector2(sx, y0),
                new System.Numerics.Vector2(sx + slotSz, y0 + slotSz),
                bgCol, BorderR);

            var stack = inventory.Slots[i];
            if (stack.IsEmpty) continue;

            // Queue for 3-D render
            _hotbarRenderTargets.Add((stack, sx, y0, slotSz));

            // Count label (bottom-right)
            if (stack.Count > 1)
            {
                string lbl = stack.Count.ToString();
                var ts = ImGui.CalcTextSize(lbl);
                float tx = sx + slotSz - ts.X - 3f;
                float ty = y0 + slotSz - ts.Y - 2f;
                dl.AddText(new System.Numerics.Vector2(tx + 1, ty + 1), 0xAA000000, lbl);
                dl.AddText(new System.Numerics.Vector2(tx, ty), 0xFFEBEB00, lbl);
            }
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
            case MenuPage.Multiplayer: DrawMultiplayerPage(); break;
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

        if (ImGui.Button("Multiplayer", new System.Numerics.Vector2(220f, 40f)))
        {
            _multiStatus = "";
            _hostWorldList = WorldPersistence.ListWorlds();
            _selectedHostWorldIndex = _hostWorldList.Count > 0 ? 0 : -1;
            _lanDiscovery.StartSearch();
            _menuPage = MenuPage.Multiplayer;
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

        if (!canLoad) ImGui.BeginDisabled();
        if (ImGui.Button("Delete", new System.Numerics.Vector2(80f, 36f)) && canLoad)
            _confirmDeleteWorld = true;
        if (!canLoad) ImGui.EndDisabled();

        ImGui.SameLine();

        if (ImGui.Button("Back", new System.Numerics.Vector2(80f, 36f)))
            _menuPage = MenuPage.Main;

        // ── Delete confirmation popup ─────────────────────────────────────
        if (_confirmDeleteWorld)
            ImGui.OpenPopup("Delete World?");

        if (ImGui.BeginPopupModal("Delete World?", ref _confirmDeleteWorld,
                ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoMove))
        {
            ImGui.Text($"Are you sure you want to delete");
            ImGui.TextColored(new System.Numerics.Vector4(1f, 0.4f, 0.4f, 1f),
                _worldList[_selectedWorldIndex].DisplayName);
            ImGui.Text("This cannot be undone.");
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            if (ImGui.Button("Yes, Delete", new System.Numerics.Vector2(120f, 30f)))
            {
                WorldPersistence.DeleteWorld(_worldList[_selectedWorldIndex].SavePath);
                _worldList.RemoveAt(_selectedWorldIndex);
                _selectedWorldIndex = -1;
                _confirmDeleteWorld = false;
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel", new System.Numerics.Vector2(80f, 30f)))
            {
                _confirmDeleteWorld = false;
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
        }

        ImGui.End();
    }

    private void DrawMultiplayerPage()
    {
        BeginCenteredWindow("##multiplayer");

        ImGui.TextColored(new System.Numerics.Vector4(0.4f, 0.9f, 1.0f, 1.0f), "Multiplayer");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.Text("Your Name");
        ImGui.SetNextItemWidth(200f);
        if (ImGui.InputText("##pname", ref _playerName, 24))
            WorldPersistence.SavePlayerName(_playerName);
        ImGui.Spacing();

        // ── Host ──────────────────────────────────────────────────────────────
        ImGui.SeparatorText("Host a World");

        if (_hostWorldList.Count == 0)
        {
            ImGui.TextDisabled("No saved worlds — create one below.");
        }
        else
        {
            ImGui.SetNextItemWidth(320f);
            if (ImGui.BeginListBox("##hostworldlist", new System.Numerics.Vector2(320f, 80f)))
            {
                for (int i = 0; i < _hostWorldList.Count; i++)
                {
                    bool sel = _selectedHostWorldIndex == i;
                    if (ImGui.Selectable(_hostWorldList[i].DisplayName, sel))
                        _selectedHostWorldIndex = i;
                    if (sel) ImGui.SetItemDefaultFocus();
                }
                ImGui.EndListBox();
            }

            bool canHost = _selectedHostWorldIndex >= 0 && _selectedHostWorldIndex < _hostWorldList.Count;
            if (!canHost) ImGui.BeginDisabled();
            if (ImGui.Button("Host Selected", new System.Numerics.Vector2(160f, 32f)) && canHost)
                StartHostExistingWorld(_hostWorldList[_selectedHostWorldIndex]);
            if (!canHost) ImGui.EndDisabled();
        }

        ImGui.Spacing();
        ImGui.TextDisabled("— or create & host a new world —");
        ImGui.SetNextItemWidth(200f);
        ImGui.InputText("##host_wname", ref _newWorldName, 64);
        ImGui.SameLine(); ImGui.TextDisabled("world name");
        if (ImGui.Button("Host New", new System.Numerics.Vector2(160f, 32f)))
        {
            if (!int.TryParse(_newWorldSeedStr.Trim(), out int seed)) seed = 0;
            StartHostGame(_newWorldName, seed, flat: false);
        }

        ImGui.Spacing();
        ImGui.SeparatorText("Join a Server");

        // ── LAN list ──────────────────────────────────────────────────────────
        var found = _lanDiscovery.Servers;
        if (found.Count == 0)
        {
            ImGui.TextDisabled("Searching for LAN servers…");
        }
        else
        {
            if (ImGui.BeginListBox("##lanservers", new System.Numerics.Vector2(300f, 80f)))
            {
                foreach (var s in found)
                {
                    if (ImGui.Selectable($"{s.Name}  ({s.PlayerCount} online)  [{s.IpAddress}]"))
                        _joinIp = s.IpAddress;
                }
                ImGui.EndListBox();
            }
        }

        ImGui.Spacing();
        ImGui.Text("IP Address");
        ImGui.SetNextItemWidth(200f);
        ImGui.InputText("##joinip", ref _joinIp, 64);

        ImGui.Spacing();
        if (ImGui.Button("Connect", new System.Numerics.Vector2(160f, 32f)))
            JoinGame(_joinIp, GameServer.DefaultPort);

        if (!string.IsNullOrEmpty(_multiStatus))
        {
            ImGui.Spacing();
            ImGui.TextColored(new System.Numerics.Vector4(1f, 0.5f, 0.3f, 1f), _multiStatus);
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (ImGui.Button("Back", new System.Numerics.Vector2(80f, 32f)))
            _menuPage = MenuPage.Main;

        ImGui.End();
    }

    // -------------------------------------------------------------------------
    // Multiplayer session management
    // -------------------------------------------------------------------------

    /// <summary>Hosts a listen-server for an existing saved world and connects locally.</summary>
    private void StartHostExistingWorld(WorldPersistence.WorldInfo info)
    {
        var (_, seed, flat) = WorldPersistence.LoadMeta(info.SavePath);
        WorldGenConfig.FlatWorld = flat;
        NoiseGenerator.SetSeed(seed);

        _gameServer = new GameServer(info.SavePath, seed, flat, $"{_playerName}'s World");
        _gameServer.Start();

        _savePath = info.SavePath;
        ConnectClient("127.0.0.1", GameServer.DefaultPort);
    }

    /// <summary>Hosts a listen-server for a new world and connects locally.</summary>
    private void StartHostGame(string worldName, int seed, bool flat)
    {
        if (string.IsNullOrWhiteSpace(worldName)) worldName = "Hosted World";

        WorldGenConfig.FlatWorld = flat;
        NoiseGenerator.SetSeed(seed);
        var savePath = WorldPersistence.GetSavePath(worldName);
        WorldPersistence.SaveMeta(savePath, worldName, seed, flat);

        _gameServer = new GameServer(savePath, seed, flat, $"{_playerName}'s World");
        _gameServer.Start();

        _savePath = savePath;
        ConnectClient("127.0.0.1", GameServer.DefaultPort);
    }

    /// <summary>Connects as a pure client (no local server).</summary>
    private void JoinGame(string ip, int port)
    {
        if (string.IsNullOrWhiteSpace(ip)) return;
        ConnectClient(ip, port);
    }

    private void ConnectClient(string ip, int port)
    {
        _multiStatus = "Connecting…";
        _remotePlayers.Clear();

        _gameClient = new GameClient();
        _chatWindow.OnSend += msg => _gameClient?.SendChat(msg);

        // Wire server → client events.
        _gameClient.OnWorldInfo += info =>
        {
            // Reuse the existing RebuildWorld flow but skip StartNewWorld name collision.
            // For join: we only set generation settings and rebuild from received chunks.
            WorldGenConfig.FlatWorld = info.IsFlat;
            NoiseGenerator.SetSeed(info.Seed);
            _camera.Position = info.SpawnPos;
            // Rebuild world without loading from disk (server sends chunks).
            RebuildWorldForMultiplayer();
            _multiStatus = "";
        };

        _gameClient.OnChunkData += pkt =>
        {
            var chunk = Chunk.CreateForDeserialization(new Vector3i(pkt.ChunkCoord.X, pkt.ChunkCoord.Y, pkt.ChunkCoord.Z));
            PacketSerializer.DecodeChunkBlocks(pkt.BlockData, chunk);
            LightEngine.ComputeChunk(chunk, _world);
            _world.ReplaceChunk(pkt.ChunkCoord, chunk);
            _worldRenderer.RebuildChunk(pkt.ChunkCoord);
        };

        _gameClient.OnBlockUpdate += pkt =>
        {
            var block = new Block { Id = pkt.BlockId, IsTransparent = BlockRegistry.IsTransparent(pkt.BlockId), Layer = (byte)(pkt.BlockId == 0 ? 0 : 16) };
            _world.SetBlock(pkt.BlockPos.X, pkt.BlockPos.Y, pkt.BlockPos.Z, block);
            LightEngine.UpdateAtBlock(pkt.BlockPos, _world);
            _worldRenderer.RebuildAffectedChunks(pkt.BlockPos);
        };

        _gameClient.OnPlayerJoin += pkt =>
        {
            _remotePlayers[pkt.PlayerId] = new RemotePlayer(pkt.PlayerId, pkt.Name);
            _chatWindow.AddMessage("", $"★ {pkt.Name} joined");
        };

        _gameClient.OnPlayerLeave += pkt =>
        {
            if (_remotePlayers.TryGetValue(pkt.PlayerId, out var rp))
            {
                _chatWindow.AddMessage("", $"★ {rp.Name} left");
                _remotePlayers.Remove(pkt.PlayerId);
            }
        };

        _gameClient.OnPlayerState += pkt =>
        {
            if (_remotePlayers.TryGetValue(pkt.PlayerId, out var rp))
                rp.ApplyState(pkt.Position, pkt.Yaw, pkt.Pitch);
        };

        _gameClient.OnChatMessage += pkt =>
            _chatWindow.AddMessage(pkt.Name, pkt.Message);

        _gameClient.OnEntityItemSpawn += pkt =>
        {
            var item = ItemRegistry.Get(pkt.ItemId);
            if (item == null) return;
            var entity = new EntityItem(item, pkt.Count, pkt.Position, pkt.Velocity)
            {
                NetworkId = pkt.EntityId,
            };
            _entityItems.Add(entity);
        };

        _gameClient.OnEntityItemRemove += pkt =>
        {
            for (int i = _entityItems.Count - 1; i >= 0; i--)
            {
                if (_entityItems[i].NetworkId == pkt.EntityId)
                {
                    _entityItems.RemoveAt(i);
                    break;
                }
            }
        };

        _gameClient.OnDisconnected += reason =>
        {
            _multiStatus = $"Disconnected: {reason}";
            _remotePlayers.Clear();
        };

        _gameClient.Connect(ip, port, _playerName);
    }

    /// <summary>
    /// Minimal world rebuild used when joining a multiplayer session.
    /// The server will stream chunk data via <see cref="GameClient.OnChunkData"/>;
    /// we just need an empty World and renderer ready to receive it.
    /// </summary>
    private void RebuildWorldForMultiplayer()
    {
        _worldRenderer?.Dispose();
        _world = new World();

        _player = new Player();

        _worldRenderer = new WorldRenderer(_gpuResources, _world, _shader, _atlas,
                                           _entityRenderer, _player.Inventory);

        _entityItems.Clear();
        foreach (var v in _vehicles)
            v.Dispose();
        _vehicles.Clear();
        _worldStreamer = new WorldStreamer(_world, _worldRenderer, _savePath);
        _interaction = new InteractionHandler(_world, _camera, _player.Inventory,
                                                _worldRenderer, _entityItems, _savePath);
        _interaction.OnEntitySpawn = SpawnEntity;
        _interaction.NetworkClient = _gameClient;
        _lastSaveStatus = null;
        TransitionToPlaying();
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
            _lastSaveStatus = _interaction?.SaveWorld();
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

