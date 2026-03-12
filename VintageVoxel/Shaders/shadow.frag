#version 450 core

// Shadow-map depth pass.
// Samples the atlas (or model texture) and discards transparent fragments so
// leaves and other alpha-tested geometry cast accurate shadows.
in vec2 vTexCoord;

uniform sampler2D uTexture;   // atlas for chunks/entities, own tex for models
uniform int       uAlphaTest; // 1 = discard alpha < 0.5, 0 = fully opaque pass

void main()
{
    if (uAlphaTest != 0)
    {
        if (texture(uTexture, vTexCoord).a < 0.5) discard;
    }
}
