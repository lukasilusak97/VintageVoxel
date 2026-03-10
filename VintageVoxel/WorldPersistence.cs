using System.Diagnostics.CodeAnalysis;
using System.Text;
using OpenTK.Mathematics;

namespace VintageVoxel;

/// <summary>
/// Handles binary serialization and deserialization of chunk data.
///
/// WHY per-chunk files?  Streaming: only the chunks currently entering the
/// render radius need to be read from disk, with no seek cost across a
/// monolithic save file.
///
/// File layout  (c_{X}_{Z}.bin):
///   [4 bytes]  Magic "VVCK" — identifies the file type.
///   [1 byte ]  Version = 1 — allows future format changes.
///   [4 bytes]  Chunk X (int32).
///   [4 bytes]  Chunk Z (int32).
///   Block RLE:
///     [4 bytes]  Entry count (int32).
///     Per entry: [2 bytes] block ID (ushort) + [2 bytes] run length (ushort).
///   Chiseled blocks:
///     [4 bytes]  Chiseled block count (int32).
///     Per block:
///       [4 bytes]  Flat array index (int32).
///       [2 bytes]  Source block ID (ushort).
///       Sub-voxel RLE:
///         [4 bytes]  Entry count (int32).
///         Per entry: [1 byte] filled (0/1) + [2 bytes] run length (ushort).
///
/// RLE compression:
///   Run-Length Encoding exploits the long uniform runs that dominate voxel
///   data (large Stone regions, large Air regions).  A typical 32x32x32 = 32,768
///   block chunk compresses from ~64 KB of raw IDs down to a few hundred bytes.
/// </summary>
public static class WorldPersistence
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("VVCK");
    private const byte Version = 1;

    /// <summary>
    /// Default save folder: <c>%AppData%\VintageVoxel\Saves\default</c>.
    /// </summary>
    public static string DefaultSavePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "VintageVoxel", "Saves", "default");

    // -------------------------------------------------------------------------
    // Save
    // -------------------------------------------------------------------------

    /// <summary>
    /// Saves every currently loaded chunk to <paramref name="folder"/>.
    /// Creates the directory if it does not exist.
    /// Returns the number of chunk files written.
    /// </summary>
    public static int SaveAll(string folder, World world)
    {
        Directory.CreateDirectory(folder);
        int count = 0;
        foreach (var (key, chunk) in world.Chunks)
        {
            SaveChunk(folder, key, chunk);
            count++;
        }
        return count;
    }

    /// <summary>
    /// Serialises <paramref name="chunk"/> to a binary file inside <paramref name="folder"/>.
    /// </summary>
    public static void SaveChunk(string folder, Vector2i key, Chunk chunk)
    {
        string path = GetChunkPath(folder, key);
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var bw = new BinaryWriter(fs, Encoding.UTF8, leaveOpen: false);

        // --- Header ---
        bw.Write(Magic);        // "VVCK"
        bw.Write(Version);      // 1
        bw.Write(key.X);        // chunk X
        bw.Write(key.Y);        // chunk Z

        // --- Block RLE ---
        // Collect (id, runLength) pairs over the entire flat block array.
        WriteBlockRle(bw, chunk);

        // --- Chiseled block data ---
        bw.Write(chunk.ChiseledBlocks.Count);
        foreach (var (flatIdx, chisel) in chunk.ChiseledBlocks)
        {
            bw.Write(flatIdx);
            bw.Write(chisel.SourceBlockId);
            WriteSubVoxelRle(bw, chisel);
        }
    }

    // -------------------------------------------------------------------------
    // Load
    // -------------------------------------------------------------------------

    /// <summary>
    /// Attempts to load the chunk at <paramref name="key"/> from <paramref name="folder"/>.
    ///
    /// Returns <c>false</c> (leaving <paramref name="chunk"/> as <c>null</c>) when:
    ///   • the chunk file does not exist (chunk will be procedurally generated), or
    ///   • the file is corrupt / version mismatch (silently skipped, regenerated).
    /// </summary>
    public static bool TryLoadChunk(
        string folder,
        Vector2i key,
        [NotNullWhen(true)] out Chunk? chunk)
    {
        chunk = null;
        string path = GetChunkPath(folder, key);
        if (!File.Exists(path)) return false;

        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var br = new BinaryReader(fs, Encoding.UTF8, leaveOpen: false);

            // Validate magic.
            byte[] magic = br.ReadBytes(4);
            if (!magic.AsSpan().SequenceEqual(Magic.AsSpan())) return false;

            // Validate version.
            byte version = br.ReadByte();
            if (version != Version) return false;

            int cx = br.ReadInt32();
            int cz = br.ReadInt32();
            if (cx != key.X || cz != key.Y) return false; // Sanity check.

            // Allocate a chunk that skips terrain generation — its _blocks will
            // be entirely overwritten by the saved data below.
            chunk = Chunk.CreateForDeserialization(new Vector3i(cx, 0, cz));

            // Decode block RLE into the chunk's internal array.
            ushort[] blockIds = ReadBlockRle(br);
            chunk.LoadBlocksFromSave(blockIds);

            // Decode any chiseled block data.
            int chiseledCount = br.ReadInt32();
            for (int i = 0; i < chiseledCount; i++)
            {
                int flatIdx = br.ReadInt32();
                ushort srcId = br.ReadUInt16();
                var chisel = new ChiseledBlockData(srcId);
                ReadSubVoxelRle(br, chisel);
                chunk.ChiseledBlocks[flatIdx] = chisel;
            }

            return true;
        }
        catch
        {
            // Treat a corrupt or truncated file as missing so the chunk is
            // regenerated fresh rather than crashing the game.
            chunk = null;
            return false;
        }
    }

    // -------------------------------------------------------------------------
    // RLE helpers — blocks
    // -------------------------------------------------------------------------

    private static void WriteBlockRle(BinaryWriter bw, Chunk chunk) =>
        RleCodec.WriteUshort(bw, i => chunk.GetRawBlockId(i), Chunk.Volume);

    private static ushort[] ReadBlockRle(BinaryReader br) =>
        RleCodec.ReadUshort(br, Chunk.Volume);

    // -------------------------------------------------------------------------
    // RLE helpers — sub-voxels
    // -------------------------------------------------------------------------

    private static void WriteSubVoxelRle(BinaryWriter bw, ChiseledBlockData chisel) =>
        RleCodec.WriteBool(bw, i => chisel.GetRaw(i), ChiseledBlockData.SubVolume);

    private static void ReadSubVoxelRle(BinaryReader br, ChiseledBlockData chisel) =>
        RleCodec.ReadBool(br, (i, v) => chisel.SetRaw(i, v), ChiseledBlockData.SubVolume);

    // -------------------------------------------------------------------------
    // Utility
    // -------------------------------------------------------------------------

    private static string GetChunkPath(string folder, Vector2i key) =>
        Path.Combine(folder, $"c_{key.X}_{key.Y}.bin");
}
