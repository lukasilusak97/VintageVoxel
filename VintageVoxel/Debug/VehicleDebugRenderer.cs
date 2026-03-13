using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using VintageVoxel.Physics;

namespace VintageVoxel;

/// <summary>
/// Draws wireframe debug overlays for vehicle physics:
///   - Chassis collision box (green)
///   - Centre of mass (yellow cross)
///   - Wheel attachment points (white)
///   - Suspension rays (green = grounded, red = no contact)
///   - Ground hit points (cyan)
///   - Velocity vector (magenta)
/// Uses GL_LINES with the line.vert/line.frag shaders, same as
/// <see cref="ChunkBorderRenderer"/>.
/// </summary>
public sealed class VehicleDebugRenderer : IDisposable
{
    private readonly Shader _shader;
    private readonly int _vao;
    private readonly int _vbo;

    // Re-uploaded each frame — small vertex count, no need for persistent mapping.
    private readonly List<float> _verts = new(1024);
    private readonly List<(int start, int count, Vector3 color)> _batches = new(16);

    public VehicleDebugRenderer()
    {
        _shader = new Shader("Shaders/line.vert", "Shaders/line.frag");

        _vao = GL.GenVertexArray();
        GL.BindVertexArray(_vao);

        _vbo = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);

        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), 0);
        GL.EnableVertexAttribArray(0);

        GL.BindVertexArray(0);
    }

    /// <summary>
    /// Draws all vehicle debug overlays for the given vehicle.
    /// </summary>
    public void Render(Vehicle vehicle, Camera camera)
    {
        _verts.Clear();
        _batches.Clear();

        var pos = vehicle.Position.ToOpenTK();
        var ori = vehicle.Orientation.ToOpenTK();
        var halfExtents = vehicle.ChassisHalfExtents.ToOpenTK();
        var velocity = vehicle.LinearVelocity.ToOpenTK();

        // ---- Chassis collision box (green wireframe) ----
        BuildWireBox(pos, ori, halfExtents, new Vector3(0f, 1f, 0f));

        // ---- Centre of mass cross (yellow) ----
        BuildCross(pos, 0.3f, new Vector3(1f, 1f, 0f));

        // ---- Wheels, suspension rays, hit points ----
        var wheelOffsets = vehicle.GetWheelOffsetsWorld();
        var wheelStates = vehicle.GetWheelStates();

        for (int i = 0; i < wheelOffsets.Length; i++)
        {
            var wheelWorld = wheelOffsets[i].ToOpenTK();

            // Wheel attachment point (white cross)
            BuildCross(wheelWorld, 0.15f, new Vector3(1f, 1f, 1f));

            if (wheelStates[i].OnGround)
            {
                var hitPt = wheelStates[i].HitPoint.ToOpenTK();

                // Suspension ray (green = grounded)
                AddLine(wheelWorld, hitPt, new Vector3(0f, 1f, 0f));

                // Hit point (cyan cross)
                BuildCross(hitPt, 0.1f, new Vector3(0f, 1f, 1f));
            }
            else
            {
                // Suspension ray (red = no contact, draw full suspension length down)
                var rayEnd = wheelWorld - Vector3.UnitY * vehicle.SuspensionLength;
                AddLine(wheelWorld, rayEnd, new Vector3(1f, 0f, 0f));
            }
        }

        // ---- Velocity vector (magenta) ----
        if (velocity.LengthSquared > 0.01f)
        {
            var velEnd = pos + velocity.Normalized() * MathF.Min(velocity.Length * 0.3f, 5f);
            AddLine(pos, velEnd, new Vector3(1f, 0f, 1f));
        }

        // ---- Upload and draw ----
        if (_verts.Count == 0) return;

        float[] data = _verts.ToArray();
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
        GL.BufferData(BufferTarget.ArrayBuffer, data.Length * sizeof(float), data, BufferUsageHint.StreamDraw);

        GL.Disable(EnableCap.DepthTest);
        GL.LineWidth(2f);

        _shader.Use();
        var view = camera.GetViewMatrix();
        var proj = camera.GetProjectionMatrix();
        _shader.SetMatrix4("view", ref view);
        _shader.SetMatrix4("projection", ref proj);

        GL.BindVertexArray(_vao);

        foreach (var (start, count, color) in _batches)
        {
            _shader.SetVector3("uColor", color);
            GL.DrawArrays(PrimitiveType.Lines, start, count);
        }

        GL.BindVertexArray(0);
        GL.LineWidth(1f);
        GL.Enable(EnableCap.DepthTest);
    }

    private void AddLine(Vector3 a, Vector3 b, Vector3 color)
    {
        int startVert = _verts.Count / 3;
        _verts.Add(a.X); _verts.Add(a.Y); _verts.Add(a.Z);
        _verts.Add(b.X); _verts.Add(b.Y); _verts.Add(b.Z);
        _batches.Add((startVert, 2, color));
    }

    private void BuildCross(Vector3 center, float size, Vector3 color)
    {
        int startVert = _verts.Count / 3;
        // X axis
        _verts.Add(center.X - size); _verts.Add(center.Y); _verts.Add(center.Z);
        _verts.Add(center.X + size); _verts.Add(center.Y); _verts.Add(center.Z);
        // Y axis
        _verts.Add(center.X); _verts.Add(center.Y - size); _verts.Add(center.Z);
        _verts.Add(center.X); _verts.Add(center.Y + size); _verts.Add(center.Z);
        // Z axis
        _verts.Add(center.X); _verts.Add(center.Y); _verts.Add(center.Z - size);
        _verts.Add(center.X); _verts.Add(center.Y); _verts.Add(center.Z + size);
        _batches.Add((startVert, 6, color));
    }

    private void BuildWireBox(Vector3 center, Quaternion rotation, Vector3 halfExtents, Vector3 color)
    {
        // 8 corners of the OBB
        Span<Vector3> corners = stackalloc Vector3[8];
        for (int i = 0; i < 8; i++)
        {
            float sx = (i & 1) != 0 ? 1f : -1f;
            float sy = (i & 2) != 0 ? 1f : -1f;
            float sz = (i & 4) != 0 ? 1f : -1f;
            var local = new Vector3(sx * halfExtents.X, sy * halfExtents.Y, sz * halfExtents.Z);
            corners[i] = center + Vector3.Transform(local, rotation);
        }

        int startVert = _verts.Count / 3;

        // 12 edges: bottom face (y-), top face (y+), 4 vertical pillars
        // Corner index layout: 0(-,-,-) 1(+,-,-) 2(-,+,-) 3(+,+,-) 4(-,-,+) 5(+,-,+) 6(-,+,+) 7(+,+,+)
        ReadOnlySpan<int> edges = stackalloc int[]
        {
            0,1, 4,5, 0,4, 1,5,  // bottom face
            2,3, 6,7, 2,6, 3,7,  // top face
            0,2, 1,3, 4,6, 5,7,  // vertical pillars
        };

        for (int i = 0; i < edges.Length; i += 2)
        {
            var a = corners[edges[i]];
            var b = corners[edges[i + 1]];
            _verts.Add(a.X); _verts.Add(a.Y); _verts.Add(a.Z);
            _verts.Add(b.X); _verts.Add(b.Y); _verts.Add(b.Z);
        }

        _batches.Add((startVert, 24, color));
    }

    public void Dispose()
    {
        GL.DeleteVertexArray(_vao);
        GL.DeleteBuffer(_vbo);
        _shader.Dispose();
    }
}
