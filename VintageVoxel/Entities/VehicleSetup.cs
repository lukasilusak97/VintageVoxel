namespace VintageVoxel;

/// <summary>
/// Vehicle configuration loaded from an entity setup JSON file (e.g. buggie.json).
/// Properties map 1:1 to the JSON schema in Assets/Entities/.
/// </summary>
public sealed class VehicleSetup
{
    /// <summary>Model path for the body mesh (relative to Assets/Models/, no extension).</summary>
    public string? BodyModel { get; set; }

    /// <summary>Model path for the wheel mesh (relative to Assets/Models/, no extension).</summary>
    public string? WheelModel { get; set; }

    public ChassisSetup Chassis { get; set; } = new();

    /// <summary>Local-space wheel attachment positions. Null = derive from chassis half-extents.</summary>
    public Vec3[]? Wheels { get; set; }

    public SuspensionSetup Suspension { get; set; } = new();
    public ControllerSetup Controller { get; set; } = new();
    public float InteractRadius { get; set; } = 5f;
    public Vec3 CameraOffset { get; set; } = new();

    public sealed class ChassisSetup
    {
        public float Mass { get; set; } = 500f;
        public float Width { get; set; } = 2f;
        public float Height { get; set; } = 1f;
        public float Length { get; set; } = 4f;
    }

    public sealed class SuspensionSetup
    {
        public float SuspensionLength { get; set; } = 1f;
        public float RestLength { get; set; } = 0.6f;
        public float SpringStiffness { get; set; } = 5000f;
        public float Damping { get; set; } = 1000f;
        public float RightingTorque { get; set; } = 800f;
        public float MinGroundClearance { get; set; } = 0.3f;
    }

    public sealed class ControllerSetup
    {
        public float DriveForce { get; set; } = 1000f;
        public float BrakeForce { get; set; } = 4000f;
        public float SteerTorque { get; set; } = 1500f;
        public float LateralGrip { get; set; } = 0.9f;
    }

    public sealed class Vec3
    {
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
    }
}
