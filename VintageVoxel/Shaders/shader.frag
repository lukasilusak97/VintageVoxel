#version 450 core

// Interpolated UV from the vertex shader.
in vec2 vTexCoord;

// Output colour written into the back buffer.
out vec4 FragColor;

// The texture atlas bound to texture unit 0.
uniform sampler2D uTexture;

// Debug toggle: when non-zero, skip atlas sampling and output solid white.
// Useful for verifying mesh geometry and lighting without texture noise.
uniform int uNoTexture;

void main()
{
    if (uNoTexture != 0)
        FragColor = vec4(1.0);
    else
        FragColor = texture(uTexture, vTexCoord);
}
