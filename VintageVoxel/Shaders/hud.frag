#version 450 core

in vec2 vTexCoord;
out vec4 FragColor;

// The shared texture atlas (same unit as the 3D world pass).
uniform sampler2D uTexture;

// Per-draw tint / flat colour.  Alpha carries the opacity.
uniform vec4 uColor;

// 0 = flat colour only, 1 = sample atlas and multiply by uColor.
uniform int uUseTexture;

void main()
{
    if (uUseTexture != 0)
        FragColor = texture(uTexture, vTexCoord) * uColor;
    else
        FragColor = uColor;
}
