#version 450 core

// Interpolated UV from the vertex shader.
in vec2 vTexCoord;

// Output colour written into the back buffer.
out vec4 FragColor;

// The texture atlas bound to texture unit 0.
// sampler2D lets the GPU sample (read) texels from the bound 2D texture.
uniform sampler2D uTexture;

void main()
{
    // texture() samples the atlas at the interpolated UV coordinate.
    // The hardware filters between mipmap levels automatically.
    FragColor = texture(uTexture, vTexCoord);
}
