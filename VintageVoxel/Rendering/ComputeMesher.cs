using OpenTK.Graphics.OpenGL4;

namespace VintageVoxel.Rendering;

/// <summary>
/// GPU-accelerated chunk mesher using a compute shader.
/// Replaces <see cref="ChunkMeshBuilder"/> for the opaque + transparent mesh generation.
///
/// Pipeline per chunk:
///   1. Build a padded 34x34x34 volume (chunk + 1-voxel neighbor border) on CPU.
///   2. Upload block data + light data to SSBOs.
///   3. Dispatch compute shader (8x8x8 workgroups of 4x4x4 threads).
///   4. Memory barrier + read back vertex/index counts.
///   5. Copy from staging SSBOs to correctly-sized VBO/EBO.
///   6. Build a VAO and return GpuMesh handles for rendering.
/// </summary>
public sealed class ComputeMesher : IDisposable
{
    private const int PadSize = 34;
    private const int PadVolume = PadSize * PadSize * PadSize; // 39304

    // Worst-case budget: ~200K opaque faces, ~50K water faces.
    private const int MaxOpaqueFaces = 200_000;
    private const int MaxTransFaces = 50_000;
    private const int MaxOpaqueVerts = MaxOpaqueFaces * 4;
    private const int MaxOpaqueIdx = MaxOpaqueFaces * 6;
    private const int MaxTransVerts = MaxTransFaces * 4;
    private const int MaxTransIdx = MaxTransFaces * 6;
    private const int VertexFloats = 8;

    private const int RegStride = 7;  // 6 tile indices + 1 flags word
    private const int MaxBlockIds = 256;

    private readonly ComputeShader _shader;

    // Input SSBOs
    private readonly int _blockSsbo;
    private readonly int _lightSsbo;
    private readonly int _registrySsbo;

    // Staging output SSBOs (shared across dispatches)
    private readonly int _opaqueVertSsbo;
    private readonly int _opaqueIdxSsbo;
    private readonly int _transVertSsbo;
    private readonly int _transIdxSsbo;
    private readonly int _counterSsbo;

    // CPU-side staging arrays (reused per dispatch)
    private readonly uint[] _blockBuf = new uint[PadVolume];
    private readonly uint[] _lightBuf = new uint[PadVolume];

    // Readback buffer (4 counters)
    private readonly uint[] _counters = new uint[4];

    private bool _disposed;

    public ComputeMesher()
    {
        _shader = new ComputeShader("Shaders/chunk_mesh.comp");

        _blockSsbo = CreateSsbo(PadVolume * sizeof(uint));
        _lightSsbo = CreateSsbo(PadVolume * sizeof(uint));

        var regData = BuildRegistryData();
        _registrySsbo = CreateSsbo(regData.Length * sizeof(uint));
        UploadSsbo(_registrySsbo, regData);

        _opaqueVertSsbo = CreateSsbo(MaxOpaqueVerts * VertexFloats * sizeof(float));
        _opaqueIdxSsbo = CreateSsbo(MaxOpaqueIdx * sizeof(uint));
        _transVertSsbo = CreateSsbo(MaxTransVerts * VertexFloats * sizeof(float));
        _transIdxSsbo = CreateSsbo(MaxTransIdx * sizeof(uint));
        _counterSsbo = CreateSsbo(4 * sizeof(uint));
    }

    /// <summary>
    /// Meshes a chunk on the GPU and returns opaque + transparent GpuMesh handles
    /// ready for rendering. The returned meshes own their own correctly-sized VBO/EBO.
    /// </summary>
    public (GpuMesh opaque, GpuMesh transparent) MeshChunk(Chunk chunk, World world)
    {
        BuildPaddedVolume(chunk, world);
        UploadSsbo(_blockSsbo, _blockBuf);
        UploadSsbo(_lightSsbo, _lightBuf);

        // Reset counters to zero.
        uint[] zeros = { 0, 0, 0, 0 };
        UploadSsbo(_counterSsbo, zeros);

        // Bind all SSBOs.
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 0, _blockSsbo);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 1, _lightSsbo);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 2, _registrySsbo);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 3, _opaqueVertSsbo);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 4, _opaqueIdxSsbo);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 5, _transVertSsbo);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 6, _transIdxSsbo);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 7, _counterSsbo);

        // Set uniforms.
        _shader.Use();
        _shader.SetFloat("uTileUvWidth", TextureAtlas.TileUvWidth);
        _shader.SetUint("uMaxVerts", (uint)MaxOpaqueVerts);
        _shader.SetUint("uMaxIdx", (uint)MaxOpaqueIdx);
        _shader.SetUint("uMaxTVerts", (uint)MaxTransVerts);
        _shader.SetUint("uMaxTIdx", (uint)MaxTransIdx);

        // Dispatch: 32/4 = 8 workgroups per axis.
        _shader.Dispatch(8, 8, 8);

        // Wait for compute to finish writing to SSBOs.
        GL.MemoryBarrier(MemoryBarrierFlags.ShaderStorageBarrierBit |
                         MemoryBarrierFlags.BufferUpdateBarrierBit);

        // Read back the 4 atomic counters (16 bytes -- negligible sync cost).
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _counterSsbo);
        GL.GetBufferSubData(BufferTarget.ShaderStorageBuffer, IntPtr.Zero,
                            4 * sizeof(uint), _counters);

        uint oVerts = _counters[0];
        uint oIdx = _counters[1];
        uint tVerts = _counters[2];
        uint tIdx = _counters[3];

        var opaque = CopyStagingToGpuMesh(
            _opaqueVertSsbo, _opaqueIdxSsbo,
            (int)oVerts, (int)oIdx);

        var transparent = CopyStagingToGpuMesh(
            _transVertSsbo, _transIdxSsbo,
            (int)tVerts, (int)tIdx);

        return (opaque, transparent);
    }

    /// <summary>
    /// Rebuilds the block registry SSBO. Call if block definitions change at runtime.
    /// </summary>
    public void RefreshRegistry()
    {
        var regData = BuildRegistryData();
        UploadSsbo(_registrySsbo, regData);
    }

    // -----------------------------------------------------------------------
    // Padded volume construction
    // -----------------------------------------------------------------------

    private void BuildPaddedVolume(Chunk chunk, World world)
    {
        int ox = chunk.Position.X * Chunk.Size;
        int oy = chunk.Position.Y * Chunk.Size;
        int oz = chunk.Position.Z * Chunk.Size;

        for (int pz = 0; pz < PadSize; pz++)
            for (int py = 0; py < PadSize; py++)
                for (int px = 0; px < PadSize; px++)
                {
                    int lx = px - 1, ly = py - 1, lz = pz - 1;
                    int padIdx = px + PadSize * (py + PadSize * pz);

                    Block block;
                    byte sun, blk;

                    if (Chunk.InBounds(lx, ly, lz))
                    {
                        block = chunk.GetBlock(lx, ly, lz);
                        int flatIdx = Chunk.Index(lx, ly, lz);
                        sun = chunk.SunLight[flatIdx];
                        blk = chunk.BlockLight[flatIdx];
                    }
                    else
                    {
                        int wx = ox + lx, wy = oy + ly, wz = oz + lz;
                        block = world.GetBlock(wx, wy, wz);
                        var (sf, bf) = world.GetSunAndBlockLight(wx, wy, wz);
                        sun = (byte)(sf * 15f + 0.5f);
                        blk = (byte)(bf * 15f + 0.5f);
                    }

                    // Pack block data.
                    uint packed = (uint)block.Id
                        | ((uint)Math.Min((int)block.Layer, 16) << 16)
                        | ((uint)Math.Min((int)block.WaterLevel, 16) << 21)
                        | (block.IsTransparent ? (1u << 26) : 0u)
                        | (BlockRegistry.HasModel(block.Id) ? (1u << 27) : 0u);
                    _blockBuf[padIdx] = packed;

                    // Pack light data: sun in bits [3:0], block in bits [7:4].
                    _lightBuf[padIdx] = (uint)Math.Min((int)sun, 15)
                                      | ((uint)Math.Min((int)blk, 15) << 4);
                }
    }

    // -----------------------------------------------------------------------
    // Block registry SSBO construction
    // -----------------------------------------------------------------------

    private static uint[] BuildRegistryData()
    {
        var buf = new uint[MaxBlockIds * RegStride];
        for (int id = 0; id < MaxBlockIds; id++)
        {
            ushort uid = (ushort)id;
            for (int face = 0; face < 6; face++)
                buf[id * RegStride + face] = (uint)BlockRegistry.TileForFace(uid, face);

            uint flags = 0;
            if (BlockRegistry.IsTransparent(uid)) flags |= 1;
            if (BlockRegistry.HasModel(uid)) flags |= 2;
            buf[id * RegStride + 6] = flags;
        }
        return buf;
    }

    // -----------------------------------------------------------------------
    // GPU buffer helpers
    // -----------------------------------------------------------------------

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

    /// <summary>
    /// Copies vertex/index data from staging SSBOs to a correctly-sized VBO/EBO,
    /// builds a VAO with the standard stride-8 layout, and returns a GpuMesh.
    /// </summary>
    private static GpuMesh CopyStagingToGpuMesh(
        int srcVertSsbo, int srcIdxSsbo,
        int vertexCount, int indexCount)
    {
        int vertBytes = vertexCount * VertexFloats * sizeof(float);
        int idxBytes = indexCount * sizeof(uint);

        // Create target VBO.
        int vbo = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
        if (vertBytes > 0)
        {
            GL.BufferData(BufferTarget.ArrayBuffer, vertBytes,
                          IntPtr.Zero, BufferUsageHint.StaticDraw);
            GL.BindBuffer(BufferTarget.CopyReadBuffer, srcVertSsbo);
            GL.CopyBufferSubData(BufferTarget.CopyReadBuffer,
                                 BufferTarget.ArrayBuffer,
                                 IntPtr.Zero, IntPtr.Zero, vertBytes);
        }
        else
        {
            GL.BufferData(BufferTarget.ArrayBuffer, 0,
                          IntPtr.Zero, BufferUsageHint.StaticDraw);
        }

        // Create target EBO.
        int ebo = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.ElementArrayBuffer, ebo);
        if (idxBytes > 0)
        {
            GL.BufferData(BufferTarget.ElementArrayBuffer, idxBytes,
                          IntPtr.Zero, BufferUsageHint.StaticDraw);
            GL.BindBuffer(BufferTarget.CopyReadBuffer, srcIdxSsbo);
            GL.CopyBufferSubData(BufferTarget.CopyReadBuffer,
                                 BufferTarget.ElementArrayBuffer,
                                 IntPtr.Zero, IntPtr.Zero, idxBytes);
        }
        else
        {
            GL.BufferData(BufferTarget.ElementArrayBuffer, 0,
                          IntPtr.Zero, BufferUsageHint.StaticDraw);
        }

        // Build VAO with stride-8 vertex layout matching GpuResourceManager.SetupAttribs(8).
        int vao = GL.GenVertexArray();
        GL.BindVertexArray(vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
        GL.BindBuffer(BufferTarget.ElementArrayBuffer, ebo);

        int stride = VertexFloats * sizeof(float); // 32 bytes
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, 0);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, stride, 3 * sizeof(float));
        GL.EnableVertexAttribArray(1);
        GL.VertexAttribPointer(2, 1, VertexAttribPointerType.Float, false, stride, 5 * sizeof(float));
        GL.EnableVertexAttribArray(2);
        GL.VertexAttribPointer(3, 1, VertexAttribPointerType.Float, false, stride, 6 * sizeof(float));
        GL.EnableVertexAttribArray(3);
        GL.VertexAttribPointer(4, 1, VertexAttribPointerType.Float, false, stride, 7 * sizeof(float));
        GL.EnableVertexAttribArray(4);

        GL.BindVertexArray(0);

        return new GpuMesh
        {
            Vao = vao,
            Vbo = vbo,
            Ebo = ebo,
            IndexCount = indexCount,
        };
    }

    // -----------------------------------------------------------------------
    // Disposal
    // -----------------------------------------------------------------------

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        GL.DeleteBuffer(_blockSsbo);
        GL.DeleteBuffer(_lightSsbo);
        GL.DeleteBuffer(_registrySsbo);
        GL.DeleteBuffer(_opaqueVertSsbo);
        GL.DeleteBuffer(_opaqueIdxSsbo);
        GL.DeleteBuffer(_transVertSsbo);
        GL.DeleteBuffer(_transIdxSsbo);
        GL.DeleteBuffer(_counterSsbo);
        _shader.Dispose();

        GC.SuppressFinalize(this);
    }

    ~ComputeMesher() => Dispose();
}
