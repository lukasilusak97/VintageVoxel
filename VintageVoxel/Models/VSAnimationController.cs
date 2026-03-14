using OpenTK.Mathematics;

namespace VintageVoxel;

/// <summary>
/// Evaluates a <see cref="VSAnimation"/> at a given point in time, producing
/// per-element <see cref="ElementTransform"/> offsets that can be fed into
/// <see cref="VSModelLoader.CalculateTransform"/>.
/// </summary>
public sealed class VSAnimationController
{
    private readonly VSAnimation _animation;
    private readonly float _duration;

    /// <summary>Current playback time (in frame units). Wraps at the end of the animation.</summary>
    public float Time { get; set; }

    public VSAnimationController(VSAnimation animation)
    {
        _animation = animation;

        // Duration is the frame value of the last keyframe (or 0 if empty).
        _duration = animation.KeyFrames.Count > 0
            ? animation.KeyFrames[^1].Frame
            : 0f;
    }

    /// <summary>
    /// Advances playback time by <paramref name="deltaFrames"/> and wraps around.
    /// </summary>
    public void Advance(float deltaFrames)
    {
        if (_duration <= 0f) return;
        Time = (Time + deltaFrames) % _duration;
    }

    /// <summary>
    /// Evaluates the animation at the current <see cref="Time"/> and returns
    /// per-element transform offsets keyed by element name.
    /// </summary>
    public Dictionary<string, ElementTransform> Evaluate()
    {
        var result = new Dictionary<string, ElementTransform>();
        if (_animation.KeyFrames.Count == 0) return result;

        // Find the two adjacent keyframes that bracket the current time.
        (VSKeyFrame a, VSKeyFrame b, float t) = FindBracketingFrames(Time);

        // Collect all element names that appear in either keyframe.
        var elementNames = new HashSet<string>(a.Elements.Keys);
        foreach (string key in b.Elements.Keys) elementNames.Add(key);

        foreach (string name in elementNames)
        {
            a.Elements.TryGetValue(name, out ElementTransform? ta);
            b.Elements.TryGetValue(name, out ElementTransform? tb);

            result[name] = Interpolate(ta, tb, t);
        }

        return result;
    }

    /// <summary>
    /// Finds the two keyframes that surround <paramref name="frame"/> and returns
    /// the interpolation factor <c>t</c> in [0..1].
    /// </summary>
    private (VSKeyFrame a, VSKeyFrame b, float t) FindBracketingFrames(float frame)
    {
        List<VSKeyFrame> kfs = _animation.KeyFrames;

        // Before or at first keyframe.
        if (frame <= kfs[0].Frame)
            return (kfs[0], kfs[0], 0f);

        for (int i = 0; i < kfs.Count - 1; i++)
        {
            if (frame >= kfs[i].Frame && frame <= kfs[i + 1].Frame)
            {
                float span = kfs[i + 1].Frame - kfs[i].Frame;
                float t = span > 0f ? (frame - kfs[i].Frame) / span : 0f;
                return (kfs[i], kfs[i + 1], t);
            }
        }

        // Past the last keyframe — clamp.
        return (kfs[^1], kfs[^1], 0f);
    }

    /// <summary>
    /// Interpolates between two <see cref="ElementTransform"/>s.
    /// Position offsets use Lerp; rotation offsets use Slerp via quaternion conversion.
    /// </summary>
    private static ElementTransform Interpolate(ElementTransform? a, ElementTransform? b, float t)
    {
        float ax = a?.OffsetX ?? 0f, ay = a?.OffsetY ?? 0f, az = a?.OffsetZ ?? 0f;
        float bx = b?.OffsetX ?? 0f, by = b?.OffsetY ?? 0f, bz = b?.OffsetZ ?? 0f;

        // Lerp position offsets.
        float ox = ax + (bx - ax) * t;
        float oy = ay + (by - ay) * t;
        float oz = az + (bz - az) * t;

        // Build quaternions from Euler angles and Slerp.
        var qa = Quaternion.FromEulerAngles(
            MathHelper.DegreesToRadians(a?.RotationX ?? 0f),
            MathHelper.DegreesToRadians(a?.RotationY ?? 0f),
            MathHelper.DegreesToRadians(a?.RotationZ ?? 0f));
        var qb = Quaternion.FromEulerAngles(
            MathHelper.DegreesToRadians(b?.RotationX ?? 0f),
            MathHelper.DegreesToRadians(b?.RotationY ?? 0f),
            MathHelper.DegreesToRadians(b?.RotationZ ?? 0f));

        var qr = Quaternion.Slerp(qa, qb, t);
        var euler = qr.ToEulerAngles(); // returns radians

        return new ElementTransform
        {
            OffsetX = ox,
            OffsetY = oy,
            OffsetZ = oz,
            RotationX = MathHelper.RadiansToDegrees(euler.X),
            RotationY = MathHelper.RadiansToDegrees(euler.Y),
            RotationZ = MathHelper.RadiansToDegrees(euler.Z),
        };
    }
}
