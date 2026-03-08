using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace VintageVoxel;

/// <summary>
/// Compiles a GLSL vertex + fragment shader pair and links them into an OpenGL Program.
/// A "Program" is what actually runs on the GPU; all draw calls use whichever program
/// is currently bound with GL.UseProgram().
/// </summary>
public class Shader : IDisposable
{
    // The handle to the linked GPU program object.
    public readonly int Handle;

    private bool _disposed = false;

    public Shader(string vertPath, string fragPath)
    {
        // --- 1. Compile vertex shader ---
        // A vertex shader runs once per vertex. Its job: transform 3D positions to
        // clip-space coordinates that OpenGL can rasterize into pixels.
        int vertShader = CompileShader(ShaderType.VertexShader, File.ReadAllText(vertPath));

        // --- 2. Compile fragment shader ---
        // A fragment shader runs once per pixel covered by a primitive.
        // Its job: output a final color for that pixel.
        int fragShader = CompileShader(ShaderType.FragmentShader, File.ReadAllText(fragPath));

        // --- 3. Link into a Program ---
        // Linking connects the output variables of one stage to the input variables of
        // the next stage, and validates that the combination is legal.
        Handle = GL.CreateProgram();
        GL.AttachShader(Handle, vertShader);
        GL.AttachShader(Handle, fragShader);
        GL.LinkProgram(Handle);

        GL.GetProgram(Handle, GetProgramParameterName.LinkStatus, out int linkStatus);
        if (linkStatus == 0)
        {
            string info = GL.GetProgramInfoLog(Handle);
            throw new Exception($"Shader link error:\n{info}");
        }

        // Shader objects are no longer needed once linked into the program.
        // Detaching and deleting them frees GPU memory.
        GL.DetachShader(Handle, vertShader);
        GL.DetachShader(Handle, fragShader);
        GL.DeleteShader(vertShader);
        GL.DeleteShader(fragShader);
    }

    private static int CompileShader(ShaderType type, string source)
    {
        int shader = GL.CreateShader(type);
        GL.ShaderSource(shader, source);
        GL.CompileShader(shader);

        GL.GetShader(shader, ShaderParameter.CompileStatus, out int status);
        if (status == 0)
        {
            string info = GL.GetShaderInfoLog(shader);
            throw new Exception($"{type} compile error:\n{info}");
        }

        return shader;
    }

    /// <summary>Bind this program so subsequent draw calls use it.</summary>
    public void Use() => GL.UseProgram(Handle);

    // --- Uniform helpers ---
    // Uniforms are per-draw-call constants set from C# and read inside shader code.

    public void SetInt(string name, int value)
        => GL.Uniform1(GL.GetUniformLocation(Handle, name), value);

    public void SetFloat(string name, float value)
        => GL.Uniform1(GL.GetUniformLocation(Handle, name), value);

    public void SetMatrix4(string name, ref Matrix4 matrix)
        => GL.UniformMatrix4(GL.GetUniformLocation(Handle, name), false, ref matrix);

    public void SetVector3(string name, Vector3 value)
        => GL.Uniform3(GL.GetUniformLocation(Handle, name), value);

    // --- IDisposable ---
    // OpenGL objects live on the GPU; we must delete them explicitly when done.
    public void Dispose()
    {
        if (!_disposed)
        {
            GL.DeleteProgram(Handle);
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }

    ~Shader() => Dispose();
}
