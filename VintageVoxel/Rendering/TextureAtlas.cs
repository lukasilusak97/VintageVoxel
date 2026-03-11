using StbImageSharp;

namespace VintageVoxel;

/// <summary>
/// Builds a texture atlas by stitching 16×16 PNG tiles from disk into a horizontal strip.
///
/// Layout: <see cref="TileCount"/> tiles each <see cref="TileSize"/>×<see cref="TileSize"/>
/// pixels wide, stored as RGBA bytes.  Call <see cref="Build"/> once at startup after
/// <see cref="BlockRegistry.Load"/> has enumerated all required texture names.
/// </summary>
public static class TextureAtlas
{
    public const int TileSize = 16;

    /// <summary>Number of tiles in the current atlas.  Set by <see cref="Build"/>.</summary>
    public static int TileCount { get; private set; } = 1;

    /// <summary>UV width of one tile in normalised [0, 1] space.  Set by <see cref="Build"/>.</summary>
    public static float TileUvWidth { get; private set; } = 1f;

    /// <summary>
    /// Loads each named PNG from <paramref name="textureDir"/>, stitches them into a
    /// horizontal strip atlas, uploads it to the GPU and returns the <see cref="Texture"/>.
    /// </summary>
    /// <param name="textureNames">Ordered list of texture names (without ".png" extension).</param>
    /// <param name="textureDir">Directory that contains the PNG files.</param>
    /// <param name="nameToIndex">Output map from texture name to tile index in the atlas.</param>
    /// <param name="textureTints">Optional per-texture RGB tint multipliers (e.g. foliage color for leaves).</param>
    public static Texture Build(
        IReadOnlyList<string> textureNames,
        string textureDir,
        out Dictionary<string, int> nameToIndex,
        Dictionary<string, (byte R, byte G, byte B)>? textureTints = null)
    {
        TileCount = Math.Max(1, textureNames.Count);
        TileUvWidth = 1f / TileCount;
        nameToIndex = new Dictionary<string, int>(textureNames.Count, StringComparer.OrdinalIgnoreCase);

        int atlasWidth = TileSize * TileCount;
        int atlasHeight = TileSize;
        byte[] pixels = new byte[atlasWidth * atlasHeight * 4];

        for (int i = 0; i < textureNames.Count; i++)
        {
            string name = textureNames[i];
            nameToIndex[name] = i;

            string path = Path.Combine(textureDir, name + ".png");
            byte[] tilePixels = File.Exists(path)
                ? LoadTile(path)
                : MagentaTile();

            if (textureTints != null && textureTints.TryGetValue(name, out var tint))
                ApplyTint(tilePixels, tint.R, tint.G, tint.B);

            BlitTile(pixels, tilePixels, i, atlasWidth);
        }

        return new Texture(atlasWidth, atlasHeight, pixels);
    }

    // ------------------------------------------------------------------
    // Private helpers
    // ------------------------------------------------------------------

    private static byte[] LoadTile(string path)
    {
        byte[] pngBytes = File.ReadAllBytes(path);
        ImageResult img = ImageResult.FromMemory(pngBytes, ColorComponents.RedGreenBlueAlpha);

        if (img.Width == TileSize && img.Height == TileSize)
            return img.Data;

        // Resize: sample nearest-neighbour into a 16×16 buffer.
        byte[] resampled = new byte[TileSize * TileSize * 4];
        for (int dy = 0; dy < TileSize; dy++)
        {
            int sy = dy * img.Height / TileSize;
            for (int dx = 0; dx < TileSize; dx++)
            {
                int sx = dx * img.Width / TileSize;
                int src = (sy * img.Width + sx) * 4;
                int dst = (dy * TileSize + dx) * 4;
                resampled[dst] = img.Data[src];
                resampled[dst + 1] = img.Data[src + 1];
                resampled[dst + 2] = img.Data[src + 2];
                resampled[dst + 3] = img.Data[src + 3];
            }
        }
        return resampled;
    }

    private static void ApplyTint(byte[] tile, byte r, byte g, byte b)
    {
        for (int i = 0; i < TileSize * TileSize; i++)
        {
            tile[i * 4 + 0] = (byte)(tile[i * 4 + 0] * r / 255);
            tile[i * 4 + 1] = (byte)(tile[i * 4 + 1] * g / 255);
            tile[i * 4 + 2] = (byte)(tile[i * 4 + 2] * b / 255);
            // alpha channel unchanged
        }
    }

    /// <summary>Magenta 16×16 tile — shown when a referenced texture file is missing.</summary>
    private static byte[] MagentaTile()
    {
        byte[] t = new byte[TileSize * TileSize * 4];
        for (int i = 0; i < TileSize * TileSize; i++)
        {
            t[i * 4] = 255; // R
            t[i * 4 + 1] = 0;   // G
            t[i * 4 + 2] = 255; // B
            t[i * 4 + 3] = 255; // A
        }
        return t;
    }

    private static void BlitTile(byte[] atlas, byte[] tile, int tileIndex, int atlasWidth)
    {
        int xBase = tileIndex * TileSize;
        for (int py = 0; py < TileSize; py++)
        {
            int srcRow = py * TileSize * 4;
            int dstRow = (py * atlasWidth + xBase) * 4;
            Array.Copy(tile, srcRow, atlas, dstRow, TileSize * 4);
        }
    }
}
