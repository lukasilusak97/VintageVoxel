namespace VintageVoxel;

/// <summary>
/// Defines the interaction behavior for a tool type.
/// Each concrete implementation (shovel, pickaxe, axe, …) handles
/// left-click and right-click differently.
/// <para>
/// Implementations are stateless singletons — all mutable state lives in
/// <see cref="ToolData"/> on the <see cref="ItemStack"/>.
/// </para>
/// </summary>
public interface IToolBehavior
{
    /// <summary>
    /// Called when the player left-clicks while holding this tool.
    /// Returns <c>true</c> if the tool handled the interaction (suppressing
    /// the default block-break logic).
    /// </summary>
    bool OnLeftClick(ToolContext ctx);

    /// <summary>
    /// Called when the player right-clicks while holding this tool.
    /// Returns <c>true</c> if the tool handled the interaction (suppressing
    /// the default block-place logic).
    /// </summary>
    bool OnRightClick(ToolContext ctx);
}
