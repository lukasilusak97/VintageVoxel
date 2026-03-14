using OpenTK.Mathematics;
using VintageVoxel.Rendering;

namespace VintageVoxel;

/// <summary>
/// Bundles all the state a <see cref="IToolBehavior"/> needs to execute an action.
/// Passed by value to avoid allocations on every click.
/// </summary>
public readonly struct ToolContext
{
    /// <summary>The live world — used to read/write blocks.</summary>
    public World World { get; init; }

    /// <summary>Renderer — used to rebuild chunk meshes after block changes.</summary>
    public WorldRenderer Renderer { get; init; }

    /// <summary>World-space position of the block that was hit by the raycast.</summary>
    public Vector3i HitPos { get; init; }

    /// <summary>Surface normal of the hit face (used to compute the adjacent placement position).</summary>
    public Vector3i Normal { get; init; }

    /// <summary>The tool's static definition (type, capacity, target blocks).</summary>
    public ToolDef ToolDef { get; init; }

    /// <summary>The tool's mutable runtime state (carried material). Never null for tool items.</summary>
    public ToolData ToolData { get; init; }
}
