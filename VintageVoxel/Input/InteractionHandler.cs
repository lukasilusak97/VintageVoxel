using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.GraphicsLibraryFramework;
using VintageVoxel.Networking;
using VintageVoxel.Physics;
using VintageVoxel.Rendering;

namespace VintageVoxel;

/// <summary>
/// Handles all player-world interactions: block breaking/placing, item dropping,
/// vehicle body pickup, and wheel attachment.
/// Delegates the actual mesh rebuilds and placed-model tracking to <see cref="WorldRenderer"/>.
/// </summary>
public sealed class InteractionHandler
{
    private readonly World _world;
    private readonly Camera _camera;
    private readonly Inventory _inventory;
    private readonly WorldRenderer _renderer;
    private readonly List<EntityItem> _entityItems;
    private readonly List<Vehicle> _vehicles;
    private readonly string _savePath;

    /// <summary>
    /// When set, block actions are sent to the server for authoritative
    /// application. The server response will update the local world via
    /// <see cref="GameClient.OnBlockUpdate"/>. We still apply optimistically
    /// locally for responsiveness.
    /// </summary>
    public GameClient? NetworkClient { get; set; }

    /// <summary>
    /// Callback invoked when an entity item is placed. Receives the entity ID and
    /// the world-space spawn position. The caller (Game) is responsible for
    /// actually creating the entity (e.g. a Vehicle).
    /// </summary>
    public Action<int, Vector3>? OnEntitySpawn { get; set; }

    public InteractionHandler(World world, Camera camera, Inventory inventory,
                              WorldRenderer renderer, List<EntityItem> entityItems,
                              List<Vehicle> vehicles, string savePath)
    {
        _world = world;
        _camera = camera;
        _inventory = inventory;
        _renderer = renderer;
        _entityItems = entityItems;
        _vehicles = vehicles;
        _savePath = savePath;
    }

    /// <summary>Processes a mouse button press in the Playing state.</summary>
    public void HandleMouseDown(MouseButtonEventArgs e)
    {
        if (e.Button == MouseButton.Left)
        {
            // Try picking up a vehicle body first.
            if (TryPickupVehicle()) return;

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
            // Try attaching a wheel to a placed vehicle body first.
            if (TryAttachWheel()) return;

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

        if (held.Item!.Type == ItemType.Entity)
        {
            // Wheel items can only be attached to placed vehicle bodies, not placed standalone.
            var def = EntityRegistry.Get(held.Item.EntityId);
            if (def != null && string.Equals(def.Type, "vehicleWheel", StringComparison.OrdinalIgnoreCase))
                return;

            var item = held.Item;
            _inventory.RemoveItem(item, 1);
            var spawnPos = new Vector3(wx + 0.5f, wy + 1.0f, wz + 0.5f);
            OnEntitySpawn?.Invoke(item.EntityId, spawnPos);
            return;
        }

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

    // -------------------------------------------------------------------------
    // Vehicle assembly helpers
    // -------------------------------------------------------------------------

    /// <summary>Radius within which the camera ray is considered "pointing at" a vehicle body.</summary>
    private const float VehiclePickupRayRadius = 2.0f;

    /// <summary>Radius within which the camera ray is considered "pointing at" a wheel slot.</summary>
    private const float WheelSlotRayRadius = 1.0f;

    /// <summary>
    /// Left-click: if the camera ray is close to any placed vehicle body,
    /// remove it and return all parts (body + attached wheels) to inventory.
    /// </summary>
    private bool TryPickupVehicle()
    {
        var origin = _camera.Position;
        var dir = _camera.Front.Normalized();

        for (int vi = _vehicles.Count - 1; vi >= 0; vi--)
        {
            var vehicle = _vehicles[vi];
            if (vehicle.IsOccupied) continue;

            var vehiclePos = vehicle.Position.ToOpenTK();
            float playerDist = (origin - vehiclePos).Length;
            if (playerDist > vehicle.InteractRadius) continue;

            float rayDist = DistanceFromRay(origin, dir, vehiclePos);
            if (rayDist > VehiclePickupRayRadius) continue;

            // Return body item to inventory.
            var bodyItem = ItemRegistry.GetByEntityId(vehicle.BodyEntityId);
            if (bodyItem != null)
                _inventory.AddItem(bodyItem, 1);

            // Return each attached wheel to inventory.
            for (int i = 0; i < vehicle.WheelSlotCount; i++)
            {
                if (!vehicle.IsWheelAttached(i)) continue;
                int wheelEntityId = vehicle.GetWheelEntityId(i);
                var wheelItem = ItemRegistry.GetByEntityId(wheelEntityId);
                if (wheelItem != null)
                    _inventory.AddItem(wheelItem, 1);
            }

            vehicle.Dispose();
            _vehicles.RemoveAt(vi);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Right-click while holding a wheel entity item: find the closest
    /// unoccupied wheel slot on a nearby vehicle body and attach the wheel.
    /// </summary>
    private bool TryAttachWheel()
    {
        ref var held = ref _inventory.HeldStack;
        if (held.IsEmpty || held.Item!.Type != ItemType.Entity) return false;

        var def = EntityRegistry.Get(held.Item.EntityId);
        if (def == null || !string.Equals(def.Type, "vehicleWheel", StringComparison.OrdinalIgnoreCase))
            return false;

        var wheelSetup = EntityRegistry.GetWheelSetup(held.Item.EntityId);
        if (wheelSetup == null) return false;

        var origin = _camera.Position;
        var dir = _camera.Front.Normalized();

        Vehicle? bestVehicle = null;
        int bestSlot = -1;
        float bestDist = float.MaxValue;

        foreach (var vehicle in _vehicles)
        {
            float playerDist = (origin - vehicle.Position.ToOpenTK()).Length;
            if (playerDist > vehicle.InteractRadius) continue;

            var wheelOffsets = vehicle.GetWheelOffsetsWorld();
            for (int i = 0; i < vehicle.WheelSlotCount; i++)
            {
                if (vehicle.IsWheelAttached(i)) continue;

                var slotWorld = wheelOffsets[i].ToOpenTK();
                float d = DistanceFromRay(origin, dir, slotWorld);
                if (d < bestDist && d < WheelSlotRayRadius)
                {
                    bestDist = d;
                    bestVehicle = vehicle;
                    bestSlot = i;
                }
            }
        }

        if (bestVehicle == null || bestSlot < 0) return false;

        bestVehicle.AttachWheel(bestSlot, held.Item.EntityId, wheelSetup);
        _inventory.RemoveItem(held.Item, 1);
        return true;
    }

    /// <summary>
    /// Computes the perpendicular distance from a point to a ray,
    /// returning float.MaxValue if the point is behind the ray origin.
    /// </summary>
    private static float DistanceFromRay(Vector3 rayOrigin, Vector3 rayDir, Vector3 point)
    {
        var toPoint = point - rayOrigin;
        float t = Vector3.Dot(toPoint, rayDir);
        if (t < 0f) return float.MaxValue; // behind camera
        var closest = rayOrigin + rayDir * t;
        return (point - closest).Length;
    }
}
