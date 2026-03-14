namespace VintageVoxel;

/// <summary>
/// Wheel configuration loaded from a wheel setup JSON file (e.g. buggie_wheel.json).
/// Defines the wheel model and suspension parameters.
/// </summary>
public sealed class WheelSetup
{
    /// <summary>Model path for the wheel mesh (relative to Assets/Models/, no extension).</summary>
    public string? Model { get; set; }

    public SuspensionConfig Suspension { get; set; } = new();

    public sealed class SuspensionConfig
    {
        public float SuspensionLength { get; set; } = 1f;
        public float RestLength { get; set; } = 0.6f;
        public float SpringStiffness { get; set; } = 5000f;
        public float Damping { get; set; } = 1000f;
        public float RightingTorque { get; set; } = 800f;
        public float MinGroundClearance { get; set; } = 0.3f;
    }
}
