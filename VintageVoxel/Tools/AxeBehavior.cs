namespace VintageVoxel;

/// <summary>
/// Axe tool behavior: limits block-breaking to target blocks (wood, planks, etc.).
/// Left-click breaks valid target blocks; right-click has no special behavior.
/// </summary>
public sealed class AxeBehavior : IToolBehavior
{
    public static readonly AxeBehavior Instance = new();

    public bool OnLeftClick(ToolContext ctx)
    {
        var block = ctx.World.GetBlock(ctx.HitPos.X, ctx.HitPos.Y, ctx.HitPos.Z);

        if (block.IsEmpty) return false;

        // Only break blocks that the axe targets.
        if (!ctx.ToolDef.CanTargetBlock(block.Id)) return false;

        // Return false to let the default break logic run.
        return false;
    }

    public bool OnRightClick(ToolContext ctx) => false;
}
