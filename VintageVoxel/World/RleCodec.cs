namespace VintageVoxel;

/// <summary>
/// Generic Run-Length Encoding helpers used by <see cref="WorldPersistence"/>.
///
/// WHY a separate class?
///   Both block-ID and sub-voxel serialization share the same RLE algorithm.
///   Centralising the logic here means bug fixes and format tweaks apply to
///   both call sites automatically, and the codec can be unit-tested in
///   isolation from disk I/O.
///
/// Wire format (written by Write*, consumed by Read*):
///   [int32]  Number of (value, runLength) pairs that follow.
///   Per pair — ushort variant:  [ushort value] [ushort runLength]
///   Per pair — bool variant:    [byte  value (0/1)] [ushort runLength]
/// </summary>
public static class RleCodec
{
    // -------------------------------------------------------------------------
    // Ushort variant — used for block IDs
    // -------------------------------------------------------------------------

    /// <summary>
    /// RLE-encodes a sequence of <see cref="ushort"/> values and writes the
    /// result to <paramref name="bw"/>.
    /// </summary>
    /// <param name="bw">Destination writer.</param>
    /// <param name="getValue">Index-based accessor for the source sequence.</param>
    /// <param name="count">Total number of elements in the sequence.</param>
    public static void WriteUshort(BinaryWriter bw, Func<int, ushort> getValue, int count)
    {
        var runs = new List<(ushort value, ushort run)>(64);
        int i = 0;
        while (i < count)
        {
            ushort val = getValue(i);
            int run = 1;
            while (i + run < count && getValue(i + run) == val && run < ushort.MaxValue)
                run++;
            runs.Add((val, (ushort)run));
            i += run;
        }

        bw.Write(runs.Count);
        foreach (var (val, run) in runs)
        {
            bw.Write(val);
            bw.Write(run);
        }
    }

    /// <summary>
    /// Decodes a ushort RLE stream previously written by <see cref="WriteUshort"/>
    /// and returns the expanded array of <paramref name="totalCount"/> values.
    /// </summary>
    public static ushort[] ReadUshort(BinaryReader br, int totalCount)
    {
        int entryCount = br.ReadInt32();
        var result = new ushort[totalCount];
        int pos = 0;
        for (int e = 0; e < entryCount; e++)
        {
            ushort val = br.ReadUInt16();
            ushort run = br.ReadUInt16();
            for (int j = 0; j < run && pos < totalCount; j++)
                result[pos++] = val;
        }
        return result;
    }

    // -------------------------------------------------------------------------
    // Bool variant — used for sub-voxel filled flags
    // -------------------------------------------------------------------------

    /// <summary>
    /// RLE-encodes a sequence of <see cref="bool"/> values (stored as byte 0/1)
    /// and writes the result to <paramref name="bw"/>.
    /// </summary>
    /// <param name="bw">Destination writer.</param>
    /// <param name="getValue">Index-based accessor for the source sequence.</param>
    /// <param name="count">Total number of elements in the sequence.</param>
    public static void WriteBool(BinaryWriter bw, Func<int, bool> getValue, int count)
    {
        var runs = new List<(bool value, ushort run)>(32);
        int i = 0;
        while (i < count)
        {
            bool val = getValue(i);
            int run = 1;
            while (i + run < count && getValue(i + run) == val && run < ushort.MaxValue)
                run++;
            runs.Add((val, (ushort)run));
            i += run;
        }

        bw.Write(runs.Count);
        foreach (var (val, run) in runs)
        {
            bw.Write((byte)(val ? 1 : 0));
            bw.Write(run);
        }
    }

    /// <summary>
    /// Decodes a bool RLE stream previously written by <see cref="WriteBool"/>
    /// and delivers each (index, value) pair to <paramref name="setValue"/>.
    /// </summary>
    public static void ReadBool(BinaryReader br, Action<int, bool> setValue, int totalCount)
    {
        int entryCount = br.ReadInt32();
        int pos = 0;
        for (int e = 0; e < entryCount; e++)
        {
            bool val = br.ReadByte() != 0;
            ushort run = br.ReadUInt16();
            for (int j = 0; j < run && pos < totalCount; j++)
                setValue(pos++, val);
        }
    }
}
