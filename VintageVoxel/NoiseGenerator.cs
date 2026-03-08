namespace VintageVoxel;

/// <summary>
/// Classic gradient (Perlin) noise using the standard 256-entry permutation table.
///
/// WHY Perlin over random value noise?
///   Perlin noise produces smooth, continuous gradients at all scales — no blocky
///   artefacts at grid boundaries.  The gradient function ensures the derivative is
///   zero at lattice points, giving the characteristic smooth undulation that reads
///   as natural terrain.
///
/// Sample2D returns a value in approximately [-1, 1].
/// Octave stacks multiple layers (fractional Brownian motion) for natural terrain.
/// </summary>
public static class NoiseGenerator
{
    // Standard Perlin permutation table — 256 pseudo-random bytes, repeated twice
    // so that out-of-range indices (xi+1) never need a modulo operation.
    private static readonly int[] P = Build();

    private static int[] Build()
    {
        ReadOnlySpan<byte> src = new byte[]
        {
            151,160,137, 91, 90, 15,131, 13,201, 95, 96, 53,194,233,  7,225,
            140, 36,103, 30, 69,142,  8, 99, 37,240, 21, 10, 23,190,  6,148,
            247,120,234, 75,  0, 26,197, 62, 94,252,219,203,117, 35, 11, 32,
             57,177, 33, 88,237,149, 56, 87,174, 20,125,136,171,168, 68,175,
             74,165, 71,134,139, 48, 27,166, 77,146,158,231, 83,111,229,122,
             60,211,133,230,220,105, 92, 41, 55, 46,245, 40,244,102,143, 54,
             65, 25, 63,161,  1,216, 80, 73,209, 76,132,187,208, 89, 18,169,
            200,196,135,130,116,188,159, 86,164,100,109,198,173,186,  3, 64,
             52,217,226,250,124,123,  5,202, 38,147,118,126,255, 82, 85,212,
            207,206, 59,227, 47, 16, 58, 17,182,189, 28, 42,223,183,170,213,
            119,248,152,  2, 44,154,163, 70,221,153,101,155,167, 43,172,  9,
            129, 22, 39,253, 19, 98,108,110, 79,113,224,232,178,185,112,104,
            218,246, 97,228,251, 34,242,193,238,210,144, 12,191,179,162,241,
             81, 51,145,235,249, 14,239,107, 49,192,214, 31,181,199,106,157,
            184, 84,204,176,115,121, 50, 45,127,  4,150,254,138,236,205, 93,
            222,114, 67, 29, 24, 72,243,141,128,195, 78, 66,215, 61,156,180
        };

        var p = new int[512];
        for (int i = 0; i < 512; i++)
            p[i] = src[i & 255];
        return p;
    }

    // -------------------------------------------------------------------------
    // Public API
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns a single Perlin noise value for (x, y) in approximately [-1, 1].
    /// </summary>
    public static float Sample2D(float x, float y)
    {
        // Integer cell the point falls in, masked to [0,255] for array indexing.
        int xi = (int)MathF.Floor(x) & 255;
        int yi = (int)MathF.Floor(y) & 255;

        // Fractional position inside the cell.
        float xf = x - MathF.Floor(x);
        float yf = y - MathF.Floor(y);

        // Fade curves smooth the interpolation so derivatives are zero at cell edges.
        float u = Fade(xf);
        float v = Fade(yf);

        // Hash the four corners of the 2-D cell.
        int aa = P[P[xi] + yi];
        int ab = P[P[xi] + yi + 1];
        int ba = P[P[xi + 1] + yi];
        int bb = P[P[xi + 1] + yi + 1];

        // Interpolate the four gradient contributions.
        float x1 = Lerp(Grad(aa, xf, yf), Grad(ba, xf - 1f, yf), u);
        float x2 = Lerp(Grad(ab, xf, yf - 1f), Grad(bb, xf - 1f, yf - 1f), u);
        return Lerp(x1, x2, v);
    }

    /// <summary>
    /// Fractional Brownian Motion: stacks <paramref name="octaves"/> noise layers
    /// at increasing frequencies (lacunarity) and decreasing amplitudes (persistence)
    /// for natural-looking terrain.  Returns a normalised value in [0, 1].
    /// </summary>
    public static float Octave(float x, float y,
        int octaves = 4, float lacunarity = 2f, float persistence = 0.5f)
    {
        float value = 0f, amplitude = 1f, frequency = 1f, maxValue = 0f;
        for (int i = 0; i < octaves; i++)
        {
            value += Sample2D(x * frequency, y * frequency) * amplitude;
            maxValue += amplitude;
            amplitude *= persistence;
            frequency *= lacunarity;
        }
        // Remap from approximately [-1,1] to [0,1].
        return (value / maxValue + 1f) * 0.5f;
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    // Ken Perlin's improved fade: 6t^5 - 15t^4 + 10t^3
    // Its first derivative is zero at t=0 and t=1, producing smooth C2 continuity.
    private static float Fade(float t) => t * t * t * (t * (t * 6f - 15f) + 10f);

    private static float Lerp(float a, float b, float t) => a + t * (b - a);

    private static float Grad(int hash, float x, float y)
    {
        // 2-D gradient: select one of four unit diagonal vectors from the low 2 bits.
        return ((hash & 1) == 0 ? x : -x)
             + ((hash & 2) == 0 ? y : -y);
    }
}
