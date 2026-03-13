using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.GraphicsLibraryFramework;
using VintageVoxel.Networking;
using VintageVoxel.Rendering;

namespace VintageVoxel;

/// <summary>
/// Handles all player-world interactions: block breaking/placing and
/// item dropping. Delegates the actual mesh rebuilds and placed-model tracking
/// to <see cref="WorldRenderer"/>.
/// </summary>
public sealed class InteractionHandler
{
    private readonly World _world;
    private readonly Camera _camera;
    private readonly Inventory _inventory;
    private readonly WorldRenderer _renderer;
    private readonly List<EntityItem> _entityItems;
    private readonly string _savePath;

    /// <summary>
    /// When set, block actions are sent to the server for authoritative
    /// application. The server response will update the local world via
    /// <see cref="GameClient.OnBlockUpdate"/>. We still apply optimistically
    /// locally for responsiveness.
    /// </summary>
    public GameClient? NetworkClient { get; set; }

    public InteractionHandler(World world, Camera camera, Inventory inventory,
                              WorldRenderer renderer, List<EntityItem> entityItems, string savePath)
    {
        _world = world;
        _camera = camera;
        _inventory = inventory;
        _renderer = renderer;
        _entityItems = entityItems;
        _savePath = savePath;
    }

    /// <summary>Processes a mouse button press in the Playing state.</summary>
    public void HandleMouseDown(MouseButtonEventArgs e)
    {
        if (e.Button == MouseButton.Left)
        {
            var hit = Raycaster.Cast(_camera.Position, _camera.Front, _world);
            if (hit.Hit)
            {
                ushort brokenId = _world.GetBlock(hit.BlockPos.X, hit.BlockPos.Y, hit.BlockPos.Z).Id;
                _world.SetBlock(hit.BlockPos.X, hit.BlockPos.Y, hit.BlockPos.Z, Block.Air);
                LightEngine.UpdateAtBlock(hit.BlockPos, _world);
                _renderer.RemovePlacedModel(hit.BlockPos);
                SpawnBlockDrop(brokenId, hit.BlockPos);
                NetworkClient?.SendPlayerAction(PlayerActionKind.Break, hit.BlockPos, 0, Vector3i.Zero);
                _renderer.RebuildAffectedChunks(hit.BlockPos);
            }
        }
        else if (e.Button == MouseButton.Right)
        {
            var hit = Raycaster.Cast(_camera.Position, _camera.Front, _world);
            if (hit.Hit)
            {
                var place = hit.BlockPos + hit.Normal;
                PlaceHeldBlock(place.X, place.Y, place.Z);
            }
        }
    }

    /// <summary>Drops one item from the held hotbar stack into the world.</summary>
    public void HandleItemDrop()
    {
        ref var held = ref _inventory.HeldStack;
        if (!held.IsEmpty)
        {
            var dropItem = held.Item!;
            int removed = _inventory.RemoveItem(dropItem, 1);
            if (removed > 0)
            {
                var spawnPos = _camera.Position + _camera.Front * 0.8f;
                var impulse = _camera.Front * 5f + new Vector3(0f, 2f, 0f);
                if (NetworkClient != null)
                {
                    // Server will broadcast EntityItemSpawnPacket back to all clients.
                    NetworkClient.SendDropItem(dropItem.Id, removed, spawnPos, impulse);
                }
                else
                {
                    _entityItems.Add(new EntityItem(dropItem, removed, spawnPos, impulse));
                }
            }
        }
    }

    /// <summary>Saves all loaded chunks to disk and returns a status string.</summary>
    public string SaveWorld()
    {
        int count = WorldPersistence.SaveAll(_savePath, _world);
        return $"Saved {count} chunk(s) at {DateTime.Now:HH:mm:ss}";
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    private void PlaceHeldBlock(int wx, int wy, int wz)
    {
        ref var held = ref _inventory.HeldStack;
        if (held.IsEmpty) return;

        var blockPos = new Vector3i(wx, wy, wz);

        if (held.Item!.Type == ItemType.Model)
        {
            var item = held.Item;
            _inventory.RemoveItem(item, 1);
            _world.SetBlock(wx, wy, wz, new Block { Id = (ushort)item.Id, IsTransparent = true, Layer = 16 });
            LightEngine.UpdateAtBlock(blockPos, _world);
            _renderer.AddPlacedModel(blockPos, item);
            _renderer.RebuildAffectedChunks(blockPos);
            NetworkClient?.SendPlayerAction(PlayerActionKind.Place, blockPos, (ushort)item.Id, Vector3i.Zero);
            return;
        }

        ushort id = (ushort)held.Item!.Id;
        _inventory.RemoveItem(held.Item!, 1);
        _world.SetBlock(wx, wy, wz, new Block { Id = id, IsTransparent = false, Layer = 16 });
        LightEngine.UpdateAtBlock(blockPos, _world);
        _renderer.RebuildAffectedChunks(blockPos);
        NetworkClient?.SendPlayerAction(PlayerActionKind.Place, blockPos, id, Vector3i.Zero);
    }

    private void SpawnBlockDrop(ushort blockId, Vector3i blockPos)
    {
        // In multiplayer the server handles drops and broadcasts EntityItemSpawnPacket.
        if (NetworkClient != null) return;

        var item = ItemRegistry.Get(blockId);
        if (item == null) return;
        var spawnPos = new Vector3(blockPos.X + 0.5f, blockPos.Y + 0.5f, blockPos.Z + 0.5f);
        var impulse = new Vector3(0f, 3f, 0f);
        _entityItems.Add(new EntityItem(item, 1, spawnPos, impulse));
    }
}
