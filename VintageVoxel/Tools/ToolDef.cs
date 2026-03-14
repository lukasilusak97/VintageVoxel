namespace VintageVoxel;

/// <summary>
/// Immutable definition of a tool's properties, loaded from the "tool" section
/// of an item entry in items.json.
/// <para>
/// <see cref="Type"/> selects the <see cref="IToolBehavior"/> implementation
/// via <see cref="ToolBehaviorFactory"/>. Additional fields are interpreted
/// by the concrete behavior — e.g. a shovel reads <see cref="Capacity"/> as
/// the maximum number of layers it can carry.
/// </para>
/// </summary>
public sealed record ToolDef(
    /// <summary>Behavior type key ("shovel", "pickaxe", "axe", …).</summary>
    string Type,

    /// <summary>
    /// Generic capacity value whose meaning depends on <see cref="Type"/>.
    /// Shovels: max layers the tool can carry (1-16).
    /// Other tools may use it for durability, power, etc.
    /// </summary>
    int Capacity,

    /// <summary>
    /// Block IDs this tool is designed to work on.
    /// Shovels: blocks whose layers can be scooped.
    /// Pickaxes/axes: blocks that can be broken efficiently.
    /// </summary>
    int[] TargetBlocks)
{
    /// <summary>Returns true if <paramref name="blockId"/> is in <see cref="TargetBlocks"/>.</summary>
    public bool CanTargetBlock(ushort blockId)
    {
        foreach (int id in TargetBlocks)
            if (id == blockId) return true;
        return false;
    }
}
