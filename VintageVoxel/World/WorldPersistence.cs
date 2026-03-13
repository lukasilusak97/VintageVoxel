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
/// File layout  (c_{X}_{Y}_{Z}.bin):
///   [4 bytes]  Magic "VVCK" — identifies the file type.
///   [1 byte ]  Version = 2 — allows future format changes.
///   [4 bytes]  Chunk X (int32).
///   [4 bytes]  Chunk Y (int32, vertical layer index).
///   [4 bytes]  Chunk Z (int32).
///   Block RLE:
///     [4 bytes]  Entry count (int32).
///     Per entry: [2 bytes] block ID (ushort) + [2 bytes] run length (ushort).
///
/// RLE compression:
///   Run-Length Encoding exploits the long uniform runs that dominate voxel
///   data (large Stone regions, large Air regions).  A typical 32x32x32 = 32,768
///   block chunk compresses from ~64 KB of raw IDs down to a few hundred bytes.
/// </summary>
public static class WorldPersistence
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("VVCK");
    private const byte Version = 4;  // v4 removes chiseled block data

    /// <summary>Root directory that contains all per-world save folders.</summary>
    public static string SavesRootPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "VintageVoxel", "Saves");

    /// <summary>
    /// Default save folder: <c>%AppData%\VintageVoxel\Saves\default</c>.
    /// </summary>
    public static string DefaultSavePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "VintageVoxel", "Saves", "default");

    /// <summary>Returns the save directory for a world with the given display name.</summary>
    public static string GetSavePath(string worldName) =>
        Path.Combine(SavesRootPath, SanitizeWorldName(worldName));

    // -------------------------------------------------------------------------
    // World listing and metadata
    // -------------------------------------------------------------------------

    /// <summary>Identifies a saved world by its folder name and user-visible title.</summary>
    public sealed class WorldInfo
    {
        public string FolderName { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string SavePath { get; set; } = "";
    }

    /// <summary>Returns metadata for every world folder found under <see cref="SavesRootPath"/>.</summary>
    public static List<WorldInfo> ListWorlds()
    {
        var result = new List<WorldInfo>();
        if (!Directory.Exists(SavesRootPath)) return result;
        foreach (var dir in Directory.EnumerateDirectories(SavesRootPath))
        {
            var folderName = Path.GetFileName(dir) ?? "world";
            var (displayName, _, _) = LoadMeta(dir);
            result.Add(new WorldInfo { FolderName = folderName, DisplayName = displayName, SavePath = dir });
        }
        return result;
    }

    /// <summary>Writes a small <c>meta.txt</c> alongside the chunk files.</summary>
    public static void SaveMeta(string folder, string displayName, int seed, bool flat)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllLines(Path.Combine(folder, "meta.txt"), new string[]
        {
            $"name={displayName}",
            $"seed={seed}",
            $"flat={flat}"
        });
    }

    /// <summary>
    /// Reads <c>meta.txt</c> from <paramref name="folder"/>.
    /// Returns sensible defaults when the file is absent.
    /// </summary>
    public static (string displayName, int seed, bool flat) LoadMeta(string folder)
    {
        string displayName = Path.GetFileName(folder) ?? "world";
        int seed = 0;
        bool flat = false;
        string metaPath = Path.Combine(folder, "meta.txt");
        if (!File.Exists(metaPath)) return (displayName, seed, flat);
        foreach (var line in File.ReadAllLines(metaPath))
        {
            if (line.StartsWith("name=", StringComparison.Ordinal)) displayName = line["name=".Length..];
            else if (line.StartsWith("seed=", StringComparison.Ordinal)) int.TryParse(line["seed=".Length..], out seed);
            else if (line.StartsWith("flat=", StringComparison.Ordinal)) bool.TryParse(line["flat=".Length..], out flat);
        }
        return (displayName, seed, flat);
    }

    // -------------------------------------------------------------------------
    // Player config
    // -------------------------------------------------------------------------

    private static string PlayerConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "VintageVoxel", "player.cfg");

    /// <summary>Persists the player's display name across sessions.</summary>
    public static void SavePlayerName(string name)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PlayerConfigPath)!);
        File.WriteAllText(PlayerConfigPath, $"name={name}");
    }

    /// <summary>Returns the saved player name, or <paramref name="defaultName"/> if none saved.</summary>
    public static string LoadPlayerName(string defaultName = "Player")
    {
        if (!File.Exists(PlayerConfigPath)) return defaultName;
        foreach (var line in File.ReadAllLines(PlayerConfigPath))
            if (line.StartsWith("name=", StringComparison.Ordinal))
                return line["name=".Length..];
        return defaultName;
    }

    private static string SanitizeWorldName(string name)
    {
        var invalid = new HashSet<char>(Path.GetInvalidFileNameChars());
        var sb = new System.Text.StringBuilder();
        foreach (char c in name)
            if (!invalid.Contains(c)) sb.Append(c);
        var result = sb.ToString().Trim();
        return string.IsNullOrEmpty(result) ? "world" : result;
    }

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
    public static void SaveChunk(string folder, Vector3i key, Chunk chunk)
    {
        string path = GetChunkPath(folder, key);
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var bw = new BinaryWriter(fs, Encoding.UTF8, leaveOpen: false);

        // --- Header ---
        bw.Write(Magic);        // "VVCK"
        bw.Write(Version);      // 2
        bw.Write(key.X);        // chunk X
        bw.Write(key.Y);        // chunk Y (vertical layer)
        bw.Write(key.Z);        // chunk Z

        // --- Block RLE (IDs) ---
        WriteBlockRle(bw, chunk);

        // --- Shape RLE (v3+) ---
        WriteShapeRle(bw, chunk);
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
        Vector3i key,
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

            // Validate version — accept v2 (no Shape data), v3 (with Shape + chiseled), and v4.
            byte version = br.ReadByte();
            if (version < 2 || version > Version) return false;

            int cx = br.ReadInt32();
            int cy = br.ReadInt32();
            int cz = br.ReadInt32();
            if (cx != key.X || cy != key.Y || cz != key.Z) return false; // Sanity check.

            // Allocate a chunk that skips terrain generation — its _blocks will
            // be entirely overwritten by the saved data below.
            chunk = Chunk.CreateForDeserialization(new Vector3i(cx, cy, cz));

            // Decode block RLE into the chunk's internal array.
            ushort[] blockIds = ReadBlockRle(br);

            // v3+: decode Shape RLE; v2 saves have no shape data (all cubes).
            byte[]? shapes = (version >= 3) ? ReadShapeRle(br) : null;

            chunk.LoadBlocksFromSave(blockIds, shapes);

            // Skip chiseled block data from v3 saves (no longer used).
            if (version == 3)
            {
                int chiseledCount = br.ReadInt32();
                for (int i = 0; i < chiseledCount; i++)
                {
                    br.ReadInt32();   // flat index
                    br.ReadUInt16();  // source block ID
                    // skip sub-voxel RLE
                    int entryCount = br.ReadInt32();
                    for (int e = 0; e < entryCount; e++)
                    {
                        br.ReadByte();    // filled
                        br.ReadUInt16();  // run length
                    }
                }
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

    private static void WriteShapeRle(BinaryWriter bw, Chunk chunk)
    {
        var runs = new List<(byte shape, ushort count)>(32);
        int i = 0;
        while (i < Chunk.Volume)
        {
            byte shape = chunk.GetRawBlockShape(i);
            int run = 1;
            while (i + run < Chunk.Volume &&
                   chunk.GetRawBlockShape(i + run) == shape &&
                   run < ushort.MaxValue)
                run++;
            runs.Add((shape, (ushort)run));
            i += run;
        }
        bw.Write(runs.Count);
        foreach (var (shape, count) in runs)
        {
            bw.Write(shape);
            bw.Write(count);
        }
    }

    private static byte[] ReadShapeRle(BinaryReader br)
    {
        int entryCount = br.ReadInt32();
        var shapes = new byte[Chunk.Volume];
        int pos = 0;
        for (int e = 0; e < entryCount; e++)
        {
            byte shape = br.ReadByte();
            ushort count = br.ReadUInt16();
            for (int j = 0; j < count && pos < Chunk.Volume; j++)
                shapes[pos++] = shape;
        }
        return shapes;
    }

    private static void WriteBlockRle(BinaryWriter bw, Chunk chunk)
    {
        // Scan the flat block array and accumulate (id, runLength) pairs.
        // WHY collect first? We need the entry count before the entries themselves.
        var runs = new List<(ushort id, ushort count)>(64);
        int i = 0;
        while (i < Chunk.Volume)
        {
            ushort id = chunk.GetRawBlockId(i);
            int run = 1;
            // Merge as many identical neighbours as fit in a ushort run length.
            while (i + run < Chunk.Volume &&
                   chunk.GetRawBlockId(i + run) == id &&
                   run < ushort.MaxValue)
                run++;
            runs.Add((id, (ushort)run));
            i += run;
        }

        bw.Write(runs.Count);
        foreach (var (id, count) in runs)
        {
            bw.Write(id);    // ushort
            bw.Write(count); // ushort
        }
    }

    private static ushort[] ReadBlockRle(BinaryReader br)
    {
        int entryCount = br.ReadInt32();
        var ids = new ushort[Chunk.Volume];
        int pos = 0;
        for (int e = 0; e < entryCount; e++)
        {
            ushort id = br.ReadUInt16();
            ushort count = br.ReadUInt16();
            for (int j = 0; j < count && pos < Chunk.Volume; j++)
                ids[pos++] = id;
        }
        return ids;
    }

    // -------------------------------------------------------------------------
    // Utility
    // -------------------------------------------------------------------------

    private static string GetChunkPath(string folder, Vector3i key) =>
        Path.Combine(folder, $"c_{key.X}_{key.Y}_{key.Z}.bin");

    // -------------------------------------------------------------------------
    // Player data
    // -------------------------------------------------------------------------

    private static readonly byte[] PlayerMagic = Encoding.ASCII.GetBytes("VVPL");
    private const byte PlayerVersion = 1;

    /// <summary>
    /// Serialises all player state (HP, stamina, spawn point, position, inventory)
    /// to <c>player.bin</c> inside <paramref name="folder"/>.
    /// </summary>
    public static void SavePlayer(string folder, Player player, Vector3 cameraPosition)
    {
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "player.bin");
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var bw = new BinaryWriter(fs, Encoding.UTF8, leaveOpen: false);

        bw.Write(PlayerMagic);   // "VVPL"
        bw.Write(PlayerVersion); // 1

        bw.Write(player.Hp);
        bw.Write(player.Stamina);

        bw.Write(player.SpawnPoint.X);
        bw.Write(player.SpawnPoint.Y);
        bw.Write(player.SpawnPoint.Z);

        bw.Write(cameraPosition.X);
        bw.Write(cameraPosition.Y);
        bw.Write(cameraPosition.Z);

        var slots = player.Inventory.Slots;
        bw.Write(player.Inventory.SelectedSlot);
        bw.Write(slots.Count);
        foreach (var slot in slots)
        {
            if (slot.IsEmpty || slot.Item == null)
            {
                bw.Write((byte)0);
            }
            else
            {
                bw.Write((byte)1);
                bw.Write(slot.Item.Id);
                bw.Write(slot.Count);
            }
        }
    }

    /// <summary>
    /// Attempts to read player state from <c>player.bin</c> in
    /// <paramref name="folder"/>.  Returns <c>false</c> when the file is absent
    /// or corrupt — the caller should create a default <see cref="Player"/> in
    /// that case.
    /// </summary>
    public static bool TryLoadPlayer(
        string folder,
        out Player player,
        out Vector3 position)
    {
        player = new Player();
        position = new Vector3(16f, 35f, 16f);

        string path = Path.Combine(folder, "player.bin");
        if (!File.Exists(path)) return false;

        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var br = new BinaryReader(fs, Encoding.UTF8, leaveOpen: false);

            byte[] magic = br.ReadBytes(4);
            if (!magic.AsSpan().SequenceEqual(PlayerMagic.AsSpan())) return false;

            byte version = br.ReadByte();
            if (version != PlayerVersion) return false;

            player.Hp = br.ReadSingle();
            player.Stamina = br.ReadSingle();

            float spawnX = br.ReadSingle();
            float spawnY = br.ReadSingle();
            float spawnZ = br.ReadSingle();
            player.SpawnPoint = new Vector3(spawnX, spawnY, spawnZ);

            float posX = br.ReadSingle();
            float posY = br.ReadSingle();
            float posZ = br.ReadSingle();
            position = new Vector3(posX, posY, posZ);

            int selectedSlot = br.ReadInt32();
            player.Inventory.SelectSlot(selectedSlot);

            int slotCount = br.ReadInt32();
            int maxSlots = player.Inventory.Slots.Count;
            for (int i = 0; i < slotCount; i++)
            {
                byte hasItem = br.ReadByte();
                if (hasItem == 0) continue;
                int itemId = br.ReadInt32();
                int count = br.ReadInt32();
                if (i < maxSlots && ItemRegistry.All.TryGetValue(itemId, out var item))
                    player.Inventory.GetSlotRef(i) = new ItemStack(item, count);
            }

            return true;
        }
        catch
        {
            // Corrupt or truncated file — start fresh rather than crashing.
            player = new Player();
            position = new Vector3(16f, 35f, 16f);
            return false;
        }
    }
}
