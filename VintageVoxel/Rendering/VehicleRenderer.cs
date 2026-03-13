using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace VintageVoxel.Rendering;

/// <summary>
/// Renders a simple procedural vehicle model made of coloured box parts,
/// using the same technique as <see cref="RemotePlayerRenderer"/>:
/// a unit cube scaled/translated/rotated per part via model matrix uniforms.
///
/// Parts (origin = vehicle centre, Y-up):
///   Chassis body  — 2×0.5×4   dark grey
///   Roof/cabin    — 1.4×0.6×1.8 blue-grey, centred above rear half
///   4 wheels      — 0.3×0.3×0.3 black cubes at the corners
/// </summary>
public sealed class VehicleRenderer : IDisposable
{
    private readonly record struct Part(
        Vector3 Offset,    // local centre offset from vehicle origin
        Vector3 HalfSize,  // half-extents
        Vector3 Color      // RGB [0,1]
    );

    private static readonly Part[] Parts =
    {
        // Main body
        new(new Vector3(0f, 0f, 0f), new Vector3(1.0f, 0.25f, 2.0f), new Vector3(0.35f, 0.35f, 0.38f)),
        // Cabin
        new(new Vector3(0f, 0.55f, -0.3f), new Vector3(0.7f, 0.30f, 0.9f), new Vector3(0.3f, 0.45f, 0.65f)),
        // Front-left wheel
        new(new Vector3(-1.05f, -0.25f, -1.6f), new Vector3(0.15f, 0.20f, 0.20f), new Vector3(0.12f, 0.12f, 0.12f)),
        // Front-right wheel
        new(new Vector3( 1.05f, -0.25f, -1.6f), new Vector3(0.15f, 0.20f, 0.20f), new Vector3(0.12f, 0.12f, 0.12f)),
        // Rear-left wheel
        new(new Vector3(-1.05f, -0.25f,  1.6f), new Vector3(0.15f, 0.20f, 0.20f), new Vector3(0.12f, 0.12f, 0.12f)),
        // Rear-right wheel
        new(new Vector3( 1.05f, -0.25f,  1.6f), new Vector3(0.15f, 0.20f, 0.20f), new Vector3(0.12f, 0.12f, 0.12f)),
        // Hood accent
        new(new Vector3(0f, 0.26f, -1.4f), new Vector3(0.8f, 0.05f, 0.5f), new Vector3(0.55f, 0.15f, 0.15f)),
    };

    private static readonly float[] CubeVerts =
    {
        -1f,-1f,-1f,   1f,-1f,-1f,   1f, 1f,-1f,  -1f, 1f,-1f,
        -1f,-1f, 1f,   1f,-1f, 1f,   1f, 1f, 1f,  -1f, 1f, 1f,
    };

    private static readonly uint[] CubeIndices =
    {
        0,1,2, 2,3,0,   4,6,5, 6,4,7,
        0,3,7, 7,4,0,   1,5,6, 6,2,1,
        3,2,6, 6,7,3,   0,4,5, 5,1,0,
    };

    private readonly Shader _shader;
    private readonly int _vao, _vbo, _ebo;
    private readonly int _uModel, _uView, _uProjection, _uColor;

    public VehicleRenderer()
    {
        _shader = new Shader("Shaders/shader.vert", "Shaders/line.frag");

        _uModel = GL.GetUniformLocation(_shader.Handle, "model");
        _uView = GL.GetUniformLocation(_shader.Handle, "view");
        _uProjection = GL.GetUniformLocation(_shader.Handle, "projection");
        _uColor = GL.GetUniformLocation(_shader.Handle, "uColor");

        _vao = GL.GenVertexArray();
        GL.BindVertexArray(_vao);

        _vbo = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
        GL.BufferData(BufferTarget.ArrayBuffer, CubeVerts.Length * sizeof(float),
                      CubeVerts, BufferUsageHint.StaticDraw);

        _ebo = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.ElementArrayBuffer, _ebo);
        GL.BufferData(BufferTarget.ElementArrayBuffer, CubeIndices.Length * sizeof(uint),
                      CubeIndices, BufferUsageHint.StaticDraw);

        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), 0);
        GL.EnableVertexAttribArray(0);

        GL.BindVertexArray(0);
    }

    /// <summary>
    /// Draws the vehicle at the given world-space position and orientation.
    /// </summary>
    /// <param name="position">World-space centre of the vehicle (System.Numerics).</param>
    /// <param name="orientation">Vehicle orientation quaternion (System.Numerics).</param>
    /// <param name="camera">Current camera for view/projection matrices.</param>
    public void Render(System.Numerics.Vector3 position, System.Numerics.Quaternion orientation, Camera camera)
    {
        GL.UseProgram(_shader.Handle);

        var view = camera.GetViewMatrix();
        var proj = camera.GetProjectionMatrix();
        GL.UniformMatrix4(_uView, false, ref view);
        GL.UniformMatrix4(_uProjection, false, ref proj);

        GL.BindVertexArray(_vao);

        // Convert Bepu pose to OpenTK for matrix math.
        var pos = Physics.MathConversions.ToOpenTK(position);
        var rot = Physics.MathConversions.ToOpenTK(orientation);
        var worldRot = Matrix4.CreateFromQuaternion(rot);
        var worldTrans = Matrix4.CreateTranslation(pos);

        foreach (var part in Parts)
        {
            var scale = Matrix4.CreateScale(part.HalfSize);
            var trans = Matrix4.CreateTranslation(part.Offset);
            var model = scale * trans * worldRot * worldTrans;

            GL.UniformMatrix4(_uModel, false, ref model);
            GL.Uniform3(_uColor, part.Color);
            GL.DrawElements(PrimitiveType.Triangles, CubeIndices.Length, DrawElementsType.UnsignedInt, 0);
        }

        GL.BindVertexArray(0);
    }

    public void Dispose()
    {
        GL.DeleteVertexArray(_vao);
        GL.DeleteBuffer(_vbo);
        GL.DeleteBuffer(_ebo);
        _shader.Dispose();
    }
}
