namespace VintageVoxel;

/// <summary>
/// Maps tool type strings (from items.json) to <see cref="IToolBehavior"/> singletons.
/// Register new tool behaviors here when adding new tool types.
/// </summary>
public static class ToolBehaviorFactory
{
    private static readonly Dictionary<string, IToolBehavior> _behaviors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["shovel"] = ShovelBehavior.Instance,
        ["pickaxe"] = PickaxeBehavior.Instance,
        ["axe"] = AxeBehavior.Instance,
    };

    /// <summary>
    /// Returns the behavior for the given tool type, or <c>null</c> if unregistered.
    /// </summary>
    public static IToolBehavior? Get(string toolType)
        => _behaviors.TryGetValue(toolType, out var behavior) ? behavior : null;

    /// <summary>
    /// Registers a custom tool behavior at runtime. Overwrites any existing
    /// registration for the same <paramref name="toolType"/>.
    /// </summary>
    public static void Register(string toolType, IToolBehavior behavior)
        => _behaviors[toolType] = behavior;
}
