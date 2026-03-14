namespace VintageVoxel;

/// <summary>
/// Pickaxe tool behavior: limits block-breaking to target blocks (stone, ores, etc.).
/// Left-click breaks valid target blocks; right-click has no special behavior.
/// </summary>
public sealed class PickaxeBehavior : IToolBehavior
{
    public static readonly PickaxeBehavior Instance = new();

    public bool OnLeftClick(ToolContext ctx)
    {
        var block = ctx.World.GetBlock(ctx.HitPos.X, ctx.HitPos.Y, ctx.HitPos.Z);

        if (block.IsEmpty) return false;

        // Only break blocks that the pickaxe targets.
        if (!ctx.ToolDef.CanTargetBlock(block.Id)) return false;

        // Break the block — the caller (InteractionHandler) handles drops/lighting.
        return false; // Return false to let the default break logic run.
    }

    public bool OnRightClick(ToolContext ctx) => false;
}
