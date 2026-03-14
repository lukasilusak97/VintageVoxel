using OpenTK.Graphics.OpenGL4;

namespace VintageVoxel.Rendering;

/// <summary>
/// Compiles and links a single GLSL compute shader program.
/// Provides dispatch and uniform helpers for compute-only workloads.
/// </summary>
public sealed class ComputeShader : IDisposable
{
    public readonly int Handle;
    private bool _disposed;

    public ComputeShader(string compPath)
    {
        string source = File.ReadAllText(compPath);
        int shader = GL.CreateShader(ShaderType.ComputeShader);
        GL.ShaderSource(shader, source);
        GL.CompileShader(shader);

        GL.GetShader(shader, ShaderParameter.CompileStatus, out int status);
        if (status == 0)
        {
            string info = GL.GetShaderInfoLog(shader);
            throw new Exception($"Compute shader compile error ({compPath}):\n{info}");
        }

        Handle = GL.CreateProgram();
        GL.AttachShader(Handle, shader);
        GL.LinkProgram(Handle);

        GL.GetProgram(Handle, GetProgramParameterName.LinkStatus, out int linkStatus);
        if (linkStatus == 0)
        {
            string info = GL.GetProgramInfoLog(Handle);
            throw new Exception($"Compute shader link error ({compPath}):\n{info}");
        }

        GL.DetachShader(Handle, shader);
        GL.DeleteShader(shader);
    }

    public void Use() => GL.UseProgram(Handle);

    public void SetInt(string name, int value)
        => GL.Uniform1(GL.GetUniformLocation(Handle, name), value);

    public void SetFloat(string name, float value)
        => GL.Uniform1(GL.GetUniformLocation(Handle, name), value);

    public void SetUint(string name, uint value)
        => GL.Uniform1(GL.GetUniformLocation(Handle, name), value);

    public void Dispatch(int groupsX, int groupsY, int groupsZ)
    {
        GL.DispatchCompute(groupsX, groupsY, groupsZ);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            GL.DeleteProgram(Handle);
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }

    ~ComputeShader() => Dispose();
}
