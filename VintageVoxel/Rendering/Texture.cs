using OpenTK.Graphics.OpenGL4;

namespace VintageVoxel;

/// <summary>
/// Wraps an OpenGL 2D texture object.
/// Accepts raw RGBA pixel bytes — no external image library required.
///
/// WHY nearest-neighbour filtering?
///   Voxel (pixel-art) textures need crisp edges between pixels.
///   GL_LINEAR would blur the tile boundaries and make the atlas look smeared.
///   GL_NEAREST_MIPMAP_NEAREST keeps sharpness at all distances.
///
/// WHY ClampToEdge wrapping?
///   Each tile in the atlas occupies a UV sub-range.  If UVs overshoot the tile
///   boundary even slightly (floating-point error), GL_REPEAT would wrap back to
///   the opposite tile.  ClampToEdge clamps to the last texel, preventing atlas
///   "bleeding" between tiles along face seams.
/// </summary>
public sealed class Texture : IDisposable
{
    public readonly int Handle;
    private bool _disposed;

    public Texture(int width, int height, byte[] rgba)
    {
        Handle = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, Handle);

        // Upload the pixel data.
        // InternalFormat Rgba8  = four 8-bit channels stored on the GPU.
        // PixelFormat Rgba      = describes the layout of the bytes we're uploading.
        GL.TexImage2D(
            TextureTarget.Texture2D,
            0,                            // mipmap level 0 (base image)
            PixelInternalFormat.Rgba8,
            width, height,
            0,                            // border — must be 0 in core profile
            PixelFormat.Rgba,
            PixelType.UnsignedByte,
            rgba);

        // Nearest-neighbour for the crisp pixel-art look voxel games need.
        GL.TexParameter(TextureTarget.Texture2D,
            TextureParameterName.TextureMinFilter, (int)TextureMinFilter.NearestMipmapNearest);
        GL.TexParameter(TextureTarget.Texture2D,
            TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);

        // Prevent atlas tile bleeding at UV seams.
        GL.TexParameter(TextureTarget.Texture2D,
            TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D,
            TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);

        // Generate mipmaps so NearestMipmapNearest works at all distances.
        GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);

        GL.BindTexture(TextureTarget.Texture2D, 0);
    }

    /// <summary>
    /// Activate the given texture unit and bind this texture to it.
    /// Texture units decouple the GPU sampler slot (e.g. unit 0 = uTexture sampler)
    /// from the actual texture object, letting multiple textures exist at once.
    /// </summary>
    public void Use(TextureUnit unit = TextureUnit.Texture0)
    {
        GL.ActiveTexture(unit);
        GL.BindTexture(TextureTarget.Texture2D, Handle);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            GL.DeleteTexture(Handle);
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }

    ~Texture() => Dispose();
}
