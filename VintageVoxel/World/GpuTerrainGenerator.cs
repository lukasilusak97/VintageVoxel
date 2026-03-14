using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using VintageVoxel.Rendering;

namespace VintageVoxel;

/// <summary>
/// GPU-accelerated terrain noise generator using a compute shader.
///
/// Pipeline per chunk column (32x32):
///   1. Upload the permutation table (one-time, shared across all dispatches).
///   2. Set chunk XZ uniform and dispatch 32x32 threads.
///   3. Read back heightmap (float[1024]) and biome map (int[1024]).
///   4. Pass results to <see cref="Chunk.GenerateFromHeightmap"/>.
/// </summary>
public sealed class GpuTerrainGenerator : IDisposable
{
    private const int CS = Chunk.Size; // 32
    private const int Columns = CS * CS; // 1024

    private readonly ComputeShader _shader;

    // Input SSBO: permutation table (512 ints).
    private readonly int _permSsbo;

    // Output SSBOs.
    private readonly int _heightSsbo;
    private readonly int _biomeSsbo;

    // CPU readback buffers (reused per dispatch).
    private readonly float[] _heightBuf = new float[Columns];
    private readonly int[] _biomeBuf = new int[Columns];

    private bool _disposed;

    public GpuTerrainGenerator()
    {
        _shader = new ComputeShader("Shaders/terrain_noise.comp");

        _permSsbo = CreateSsbo(512 * sizeof(int));
        _heightSsbo = CreateSsbo(Columns * sizeof(float));
        _biomeSsbo = CreateSsbo(Columns * sizeof(int));

        UploadPermutationTable();
    }

    /// <summary>
    /// Re-uploads the permutation table after <see cref="NoiseGenerator.SetSeed"/>
    /// has been called so the GPU uses the same seed as the CPU.
    /// </summary>
    public void RefreshPermutationTable() => UploadPermutationTable();

    /// <summary>
    /// Dispatches the terrain noise compute shader for the given chunk XZ
    /// position and reads back the results.
    /// </summary>
    public void Generate(Vector2i chunkXZ, out float[] heights, out int[] biomes)
    {
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 0, _permSsbo);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 1, _heightSsbo);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 2, _biomeSsbo);

        _shader.Use();
        int loc = GL.GetUniformLocation(_shader.Handle, "uChunkXZ");
        GL.Uniform2(loc, chunkXZ.X, chunkXZ.Y);

        // 32/8 = 4 workgroups per axis.
        _shader.Dispatch(CS / 8, CS / 8, 1);

        GL.MemoryBarrier(MemoryBarrierFlags.ShaderStorageBarrierBit |
                         MemoryBarrierFlags.BufferUpdateBarrierBit);

        // Read back heights.
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _heightSsbo);
        GL.GetBufferSubData(BufferTarget.ShaderStorageBuffer, IntPtr.Zero,
            Columns * sizeof(float), _heightBuf);

        // Read back biomes.
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _biomeSsbo);
        GL.GetBufferSubData(BufferTarget.ShaderStorageBuffer, IntPtr.Zero,
            Columns * sizeof(int), _biomeBuf);

        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, 0);

        // Return copies so the caller owns the arrays.
        heights = (float[])_heightBuf.Clone();
        biomes = (int[])_biomeBuf.Clone();
    }

    // -----------------------------------------------------------------
    // Permutation table upload
    // -----------------------------------------------------------------

    private void UploadPermutationTable()
    {
        int[] table = NoiseGenerator.GetPermutationTable();
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _permSsbo);
        GL.BufferSubData(BufferTarget.ShaderStorageBuffer, IntPtr.Zero,
            table.Length * sizeof(int), table);
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, 0);
    }

    // -----------------------------------------------------------------
    // GPU resource helpers (mirrors GpuLightEngine patterns)
    // -----------------------------------------------------------------

    private static int CreateSsbo(int sizeBytes)
    {
        int ssbo = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, ssbo);
        GL.BufferData(BufferTarget.ShaderStorageBuffer, sizeBytes,
            IntPtr.Zero, BufferUsageHint.DynamicDraw);
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, 0);
        return ssbo;
    }

    // -----------------------------------------------------------------
    // Disposal
    // -----------------------------------------------------------------

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _shader.Dispose();
        GL.DeleteBuffer(_permSsbo);
        GL.DeleteBuffer(_heightSsbo);
        GL.DeleteBuffer(_biomeSsbo);

        GC.SuppressFinalize(this);
    }

    ~GpuTerrainGenerator() => Dispose();
}
