namespace VintageVoxel;

/// <summary>
/// Vehicle body configuration loaded from a vehicle setup JSON file (e.g. buggie.json).
/// Defines the body model, chassis, wheel attachment slots, and controller.
/// Wheel model and suspension are defined separately in <see cref="WheelSetup"/>.
/// </summary>
public sealed class VehicleSetup
{
    /// <summary>Model path for the body mesh (relative to Assets/Models/, no extension).</summary>
    public string? BodyModel { get; set; }

    public ChassisSetup Chassis { get; set; } = new();

    /// <summary>Local-space wheel attachment positions. Null = derive from chassis half-extents.</summary>
    public Vec3[]? WheelSlots { get; set; }

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
