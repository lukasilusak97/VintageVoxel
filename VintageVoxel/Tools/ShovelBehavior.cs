using OpenTK.Mathematics;

namespace VintageVoxel;

/// <summary>
/// Shovel tool behavior: scoops layers from target blocks (grass, sand, dirt)
/// and deposits them elsewhere, creating partial-height blocks.
/// <para>
/// <b>Left-click:</b> Removes up to <see cref="ToolDef.Capacity"/> layers from
/// the hit block (if it's a valid target) and stores them in <see cref="ToolData"/>.
/// <br/>
/// <b>Right-click:</b> Places carried layers onto the target position, either
/// adding to an existing partial block of the same type or creating a new one.
/// </para>
/// </summary>
public sealed class ShovelBehavior : IToolBehavior
{
    public static readonly ShovelBehavior Instance = new();

    public bool OnLeftClick(ToolContext ctx)
    {
        // Scan down past non-target transparent blocks (e.g. short grass)
        // so the player can scoop from above.
        var targetPos = ctx.HitPos;
        var block = ctx.World.GetBlock(targetPos.X, targetPos.Y, targetPos.Z);
        while (!block.IsEmpty
               && !ctx.ToolDef.CanTargetBlock(block.Id)
               && BlockRegistry.IsTransparent(block.Id))
        {
            targetPos = new Vector3i(targetPos.X, targetPos.Y - 1, targetPos.Z);
            block = ctx.World.GetBlock(targetPos.X, targetPos.Y, targetPos.Z);
        }

        if (block.IsEmpty || !ctx.ToolDef.CanTargetBlock(block.Id))
            return false;

        // Can only carry one block type at a time.
        if (!ctx.ToolData.IsEmpty && ctx.ToolData.CarriedBlockId != block.Id)
            return false;

        int remaining = ctx.ToolDef.Capacity - ctx.ToolData.CarriedLayers;
        if (remaining <= 0) return false;

        int scoop = Math.Min(remaining, block.Layer);
        if (scoop <= 0) return false;

        // Update the tool state.
        ctx.ToolData.CarriedBlockId = block.Id;
        ctx.ToolData.CarriedLayers += (byte)scoop;

        // Update the block in the world.
        byte newLayer = (byte)(block.Layer - scoop);
        if (newLayer == 0)
        {
            ctx.World.SetBlock(targetPos.X, targetPos.Y, targetPos.Z, Block.Air);
        }
        else
        {
            ctx.World.SetBlock(targetPos.X, targetPos.Y, targetPos.Z, new Block
            {
                Id = block.Id,
                IsTransparent = block.IsTransparent,
                Layer = newLayer,
                WaterLevel = block.WaterLevel,
            });
        }

        LightEngine.UpdateAtBlock(targetPos, ctx.World);
        ctx.Renderer.RebuildAffectedChunks(targetPos);
        return true;
    }

    public bool OnRightClick(ToolContext ctx)
    {
        if (ctx.ToolData.IsEmpty) return false;

        // Resolve the actual target: skip downward past non-target transparent blocks
        // (e.g. short grass) to find the real solid surface underneath.
        var targetPos = ctx.HitPos;
        var targetBlock = ctx.World.GetBlock(targetPos.X, targetPos.Y, targetPos.Z);
        while (!targetBlock.IsEmpty
               && targetBlock.Id != ctx.ToolData.CarriedBlockId
               && BlockRegistry.IsTransparent(targetBlock.Id))
        {
            targetPos = new Vector3i(targetPos.X, targetPos.Y - 1, targetPos.Z);
            targetBlock = ctx.World.GetBlock(targetPos.X, targetPos.Y, targetPos.Z);
        }

        // 1. If the resolved block is the same type and partial, add layers to it.
        if (!targetBlock.IsEmpty && targetBlock.Id == ctx.ToolData.CarriedBlockId && targetBlock.IsPartial)
        {
            DepositOnBlock(ctx, targetPos, targetBlock);
            return true;
        }

        // 2. If the resolved block is a different type but partial, merge into the
        //    same cell to avoid a visual gap. The deposited material takes over the
        //    block type; the combined layer count is clamped to 16.
        if (!targetBlock.IsEmpty && targetBlock.IsPartial && targetBlock.Id != ctx.ToolData.CarriedBlockId)
        {
            int combined = targetBlock.Layer + ctx.ToolData.CarriedLayers;
            int deposit = Math.Min(ctx.ToolData.CarriedLayers, 16 - targetBlock.Layer);
            if (deposit > 0)
            {
                byte newLayer = (byte)(targetBlock.Layer + deposit);
                bool transparent = BlockRegistry.IsTransparent(ctx.ToolData.CarriedBlockId);
                ctx.World.SetBlock(targetPos.X, targetPos.Y, targetPos.Z, new Block
                {
                    Id = ctx.ToolData.CarriedBlockId,
                    IsTransparent = transparent,
                    Layer = newLayer,
                    WaterLevel = targetBlock.WaterLevel,
                });

                ctx.ToolData.CarriedLayers -= (byte)deposit;
                if (ctx.ToolData.CarriedLayers == 0) ctx.ToolData.Clear();

                LightEngine.UpdateAtBlock(targetPos, ctx.World);
                ctx.Renderer.RebuildAffectedChunks(targetPos);
                return true;
            }
        }

        // 3. Place above the resolved block (layers always stack vertically).
        var abovePos = new Vector3i(targetPos.X, targetPos.Y + 1, targetPos.Z);
        var aboveBlock = ctx.World.GetBlock(abovePos.X, abovePos.Y, abovePos.Z);

        // If block above is same type and partial, deposit onto it.
        if (!aboveBlock.IsEmpty && aboveBlock.Id == ctx.ToolData.CarriedBlockId && aboveBlock.IsPartial)
        {
            DepositOnBlock(ctx, abovePos, aboveBlock);
            return true;
        }

        // If block above is empty or transparent non-target, replace it with a new partial block.
        if (aboveBlock.IsEmpty
            || (BlockRegistry.IsTransparent(aboveBlock.Id) && !ctx.ToolDef.CanTargetBlock(aboveBlock.Id)))
        {
            int deposit = Math.Min((int)ctx.ToolData.CarriedLayers, 16);
            bool transparent = BlockRegistry.IsTransparent(ctx.ToolData.CarriedBlockId);
            ctx.World.SetBlock(abovePos.X, abovePos.Y, abovePos.Z, new Block
            {
                Id = ctx.ToolData.CarriedBlockId,
                IsTransparent = transparent,
                Layer = (byte)deposit,
                WaterLevel = 0,
            });

            ctx.ToolData.CarriedLayers -= (byte)deposit;
            if (ctx.ToolData.CarriedLayers == 0) ctx.ToolData.Clear();

            LightEngine.UpdateAtBlock(abovePos, ctx.World);
            ctx.Renderer.RebuildAffectedChunks(abovePos);
            return true;
        }

        // Can't place here (full block above, etc.)
        return false;
    }

    private static void DepositOnBlock(ToolContext ctx, Vector3i pos, Block existing)
    {
        int space = 16 - existing.Layer;
        int deposit = Math.Min(ctx.ToolData.CarriedLayers, space);
        if (deposit <= 0) return;

        byte newLayer = (byte)(existing.Layer + deposit);

        ctx.World.SetBlock(pos.X, pos.Y, pos.Z, new Block
        {
            Id = existing.Id,
            IsTransparent = existing.IsTransparent,
            Layer = newLayer,
            WaterLevel = existing.WaterLevel,
        });

        ctx.ToolData.CarriedLayers -= (byte)deposit;
        if (ctx.ToolData.CarriedLayers == 0) ctx.ToolData.Clear();

        LightEngine.UpdateAtBlock(pos, ctx.World);
        ctx.Renderer.RebuildAffectedChunks(pos);
    }
}
