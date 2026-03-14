using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using VintageVoxel.Rendering;

namespace VintageVoxel;

/// <summary>
/// GPU-accelerated single-chunk light engine using iterative parallel relaxation.
///
/// Pipeline per chunk:
///   1. Build block data + sky-open column flags on CPU.
///   2. Upload to SSBOs and dispatch the seed compute shader.
///   3. Run 15 iterations of the propagation shader (ping-pong 3D textures).
///   4. Read back the converged light values into the chunk arrays.
///
/// Cross-chunk light bleeding is handled separately by seeding the CPU BFS
/// with border voxels after readback (see <see cref="LightEngine.SeedBorderBleeding"/>).
/// </summary>
public sealed class GpuLightEngine : IDisposable
{
    private const int CS = Chunk.Size;
    private const int Vol = Chunk.Volume;
    private const int PropagationIterations = 15;

    private readonly ComputeShader _seedShader;
    private readonly ComputeShader _propagateShader;

    // Ping-pong 3D textures (32^3, RGBA8).
    private readonly int _texA;
    private readonly int _texB;

    // Input SSBOs.
    private readonly int _blockSsbo;
    private readonly int _skyOpenSsbo;

    // Reusable CPU-side staging buffers.
    private readonly uint[] _blockBuf = new uint[Vol];
    private readonly uint[] _skyOpenBuf = new uint[CS];
    private readonly byte[] _readbackBuf = new byte[Vol * 4];

    private bool _disposed;

    public GpuLightEngine()
    {
        _seedShader = new ComputeShader("Shaders/light_seed.comp");
        _propagateShader = new ComputeShader("Shaders/light_propagate.comp");

        _texA = Create3DTexture();
        _texB = Create3DTexture();

        _blockSsbo = CreateSsbo(Vol * sizeof(uint));
        _skyOpenSsbo = CreateSsbo(CS * sizeof(uint));
    }

    /// <summary>
    /// Computes full intra-chunk lighting on the GPU, then writes results back
    /// into <paramref name="chunk"/>.SunLight and BlockLight arrays.
    /// </summary>
    public void ComputeChunk(Chunk chunk, World world)
    {
        BuildBlockData(chunk);
        BuildSkyOpenFlags(chunk, world);

        UploadSsbo(_blockSsbo, _blockBuf);
        UploadSsbo(_skyOpenSsbo, _skyOpenBuf);

        // -- Phase 1: Seed pass -------------------------------------------
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 0, _blockSsbo);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 1, _skyOpenSsbo);
        GL.BindImageTexture(0, _texA, 0, true, 0,
            TextureAccess.WriteOnly, SizedInternalFormat.Rgba8);

        _seedShader.Use();
        _seedShader.Dispatch(CS / 8, CS / 8, CS / 4);
        GL.MemoryBarrier(MemoryBarrierFlags.ShaderImageAccessBarrierBit);

        // -- Phase 2: Iterative relaxation (ping-pong) --------------------
        _propagateShader.Use();
        int readTex = _texA, writeTex = _texB;

        for (int i = 0; i < PropagationIterations; i++)
        {
            GL.BindImageTexture(0, readTex, 0, true, 0,
                TextureAccess.ReadOnly, SizedInternalFormat.Rgba8);
            GL.BindImageTexture(1, writeTex, 0, true, 0,
                TextureAccess.WriteOnly, SizedInternalFormat.Rgba8);

            _propagateShader.Dispatch(CS / 8, CS / 8, CS / 4);
            GL.MemoryBarrier(MemoryBarrierFlags.ShaderImageAccessBarrierBit);

            (readTex, writeTex) = (writeTex, readTex);
        }

        // After the loop readTex points to the last-written texture.
        ReadBack(readTex, chunk);
    }

    // -----------------------------------------------------------------
    // CPU data building
    // -----------------------------------------------------------------

    private void BuildBlockData(Chunk chunk)
    {
        for (int z = 0; z < CS; z++)
            for (int y = 0; y < CS; y++)
                for (int x = 0; x < CS; x++)
                {
                    int idx = Chunk.Index(x, y, z);
                    ref Block b = ref chunk.GetBlock(x, y, z);
                    uint packed = b.Id;
                    packed |= (uint)b.Layer << 16;
                    packed |= (uint)b.WaterLevel << 21;
                    if (b.IsTransparent) packed |= 1u << 26;
                    _blockBuf[idx] = packed;
                }
    }

    private void BuildSkyOpenFlags(Chunk chunk, World world)
    {
        Array.Clear(_skyOpenBuf, 0, CS);
        bool isTop = chunk.Position.Y == World.MaxChunkY - 1;

        for (int z = 0; z < CS; z++)
            for (int x = 0; x < CS; x++)
                if (isTop || !IsColumnBlockedAbove(chunk, world, x, z))
                    _skyOpenBuf[z] |= 1u << x;
    }

    private static bool IsColumnBlockedAbove(Chunk chunk, World world, int x, int z)
    {
        for (int cy = chunk.Position.Y + 1; cy < World.MaxChunkY; cy++)
        {
            var key = new Vector3i(chunk.Position.X, cy, chunk.Position.Z);
            if (!world.Chunks.TryGetValue(key, out var above)) continue;
            for (int ay = 0; ay < Chunk.Size; ay++)
                if (above.GetBlock(x, ay, z).IsFullBlock) return true;
        }
        return false;
    }

    // -----------------------------------------------------------------
    // GPU readback
    // -----------------------------------------------------------------

    private void ReadBack(int tex, Chunk chunk)
    {
        GL.BindTexture(TextureTarget.Texture3D, tex);
        GL.GetTexImage(TextureTarget.Texture3D, 0,
            PixelFormat.Rgba, PixelType.UnsignedByte, _readbackBuf);
        GL.BindTexture(TextureTarget.Texture3D, 0);

        // 255 = 17 * 15, so integer division by 17 recovers exact light levels.
        for (int i = 0; i < Vol; i++)
        {
            chunk.SunLight[i] = (byte)(_readbackBuf[i * 4] / 17);
            chunk.BlockLight[i] = (byte)(_readbackBuf[i * 4 + 1] / 17);
        }
    }

    // -----------------------------------------------------------------
    // GPU resource helpers
    // -----------------------------------------------------------------

    private static int Create3DTexture()
    {
        int tex = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture3D, tex);
        GL.TexImage3D(TextureTarget.Texture3D, 0, PixelInternalFormat.Rgba8,
            CS, CS, CS, 0, PixelFormat.Rgba, PixelType.UnsignedByte, IntPtr.Zero);
        GL.TexParameter(TextureTarget.Texture3D,
            TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture3D,
            TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture3D,
            TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture3D,
            TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture3D,
            TextureParameterName.TextureWrapR, (int)TextureWrapMode.ClampToEdge);
        GL.BindTexture(TextureTarget.Texture3D, 0);
        return tex;
    }

    private static int CreateSsbo(int sizeBytes)
    {
        int ssbo = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, ssbo);
        GL.BufferData(BufferTarget.ShaderStorageBuffer, sizeBytes,
            IntPtr.Zero, BufferUsageHint.DynamicDraw);
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, 0);
        return ssbo;
    }

    private static void UploadSsbo(int ssbo, uint[] data)
    {
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, ssbo);
        GL.BufferSubData(BufferTarget.ShaderStorageBuffer, IntPtr.Zero,
            data.Length * sizeof(uint), data);
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, 0);
    }

    // -----------------------------------------------------------------
    // Disposal
    // -----------------------------------------------------------------

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _seedShader.Dispose();
        _propagateShader.Dispose();
        GL.DeleteTexture(_texA);
        GL.DeleteTexture(_texB);
        GL.DeleteBuffer(_blockSsbo);
        GL.DeleteBuffer(_skyOpenSsbo);

        GC.SuppressFinalize(this);
    }

    ~GpuLightEngine() => Dispose();
}
