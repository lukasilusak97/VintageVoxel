namespace VintageVoxel;

/// <summary>
/// Procedurally generates a texture atlas entirely in memory — no PNG files required.
///
/// Layout: a horizontal strip of <see cref="TileCount"/> tiles, each
/// <see cref="TileSize"/> x <see cref="TileSize"/> pixels, stored as RGBA bytes.
///
///   Tile 0 — Dirt   (warm brown)
///   Tile 1 — Stone  (cool gray)
///   Tile 2 — Grass top (green)
///   Tile 3 — Torch  (warm orange/yellow)
///
/// WHY procedural instead of loading a .png?
///   Avoids a runtime file dependency and keeps the project self-contained for now.
///   The Texture class accepts raw RGBA bytes, so swapping in a file loader later
///   (e.g. StbImageSharp) only changes this class, not the rest of the pipeline.
/// </summary>
public static class TextureAtlas
{
    public const int TileSize = 16;
    public const int TileCount = 4;
    public const int Width = TileSize * TileCount; // 64 px
    public const int Height = TileSize;             // 16 px

    /// <summary>UV width of a single tile in normalised [0, 1] space.</summary>
    public static readonly float TileUvWidth = 1f / TileCount;

    /// <summary>
    /// Generates all tiles, uploads the result to the GPU and returns a
    /// ready-to-use <see cref="Texture"/>.
    /// </summary>
    public static Texture Generate()
    {
        // 4 bytes (RGBA) per pixel, Width x Height pixels.
        byte[] pixels = new byte[Width * Height * 4];

        for (int tileIndex = 0; tileIndex < TileCount; tileIndex++)
            PaintTile(pixels, tileIndex);

        return new Texture(Width, Height, pixels);
    }

    // ------------------------------------------------------------------
    // Per-tile painters
    // ------------------------------------------------------------------

    private static void PaintTile(byte[] pixels, int tileIndex)
    {
        int xBase = tileIndex * TileSize;

        for (int py = 0; py < TileSize; py++)
            for (int px = 0; px < TileSize; px++)
            {
                int dstIdx = ((py * Width) + (xBase + px)) * 4;

                (byte r, byte g, byte b) = tileIndex switch
                {
                    0 => DirtColor(px, py),
                    1 => StoneColor(px, py),
                    2 => GrassTopColor(px, py),
                    3 => TorchColor(px, py),
                    _ => ((byte)255, (byte)0, (byte)255), // Magenta = missing tile
                };

                pixels[dstIdx + 0] = r;
                pixels[dstIdx + 1] = g;
                pixels[dstIdx + 2] = b;
                pixels[dstIdx + 3] = 255; // Fully opaque
            }
    }

    // ------------------------------------------------------------------
    // Color recipes
    // ------------------------------------------------------------------

    private static (byte r, byte g, byte b) DirtColor(int px, int py)
    {
        int h = Hash(px, py);
        // Warm brown base with ±16 variation per channel.
        byte r = Clamp(135 + Vary(h, 16));
        byte g = Clamp(88 + Vary(h >> 5, 16));
        byte b = Clamp(40 + Vary(h >> 10, 8));
        return (r, g, b);
    }

    private static (byte r, byte g, byte b) StoneColor(int px, int py)
    {
        int h = Hash(px, py);
        // Neutral gray — same value for R/G/B, ±24 variation.
        byte v = Clamp(118 + Vary(h, 24));
        return (v, v, v);
    }

    private static (byte r, byte g, byte b) GrassTopColor(int px, int py)
    {
        int h = Hash(px, py);
        // Muted green with ±20 variation on G, smaller on R/B.
        byte r = Clamp(72 + Vary(h, 12));
        byte g = Clamp(124 + Vary(h >> 4, 20));
        byte b = Clamp(36 + Vary(h >> 9, 8));
        return (r, g, b);
    }

    private static (byte r, byte g, byte b) TorchColor(int px, int py)
    {
        int h = Hash(px, py);
        // Warm orange-yellow flame with a dark brown stick at the bottom.
        bool isStick = py > TileSize / 2;
        if (isStick)
        {
            // Dark brown stick
            byte r = Clamp(100 + Vary(h, 10));
            byte g = Clamp(65 + Vary(h >> 4, 10));
            byte b = Clamp(20 + Vary(h >> 8, 6));
            return (r, g, b);
        }
        // Flame: bright orange fading to yellow toward the tip
        float t = 1f - py / (TileSize * 0.5f); // 0 at midpoint, 1 at top
        byte fr = Clamp((int)(220 + Vary(h, 20)));
        byte fg = Clamp((int)(120 + t * 80 + Vary(h >> 3, 20)));
        byte fb = Clamp((int)(0 + Vary(h >> 7, 15)));
        return (fr, fg, fb);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    /// <summary>
    /// Integer hash — mixes two pixel coordinates into a pseudo-random scalar.
    /// No allocations; deterministic so the same pixel always gets the same colour.
    /// </summary>
    private static int Hash(int x, int y)
    {
        unchecked
        {
            int h = x * 374761393 + y * 668265263;
            h = (h ^ (h >> 13)) * 1274126177;
            return h ^ (h >> 16);
        }
    }

    /// <summary>Maps the low bits of <paramref name="h"/> to [-amplitude, +amplitude].</summary>
    private static int Vary(int h, int amplitude) =>
        (h & (amplitude * 2 - 1)) - amplitude;

    private static byte Clamp(int v) => (byte)Math.Clamp(v, 0, 255);
}
