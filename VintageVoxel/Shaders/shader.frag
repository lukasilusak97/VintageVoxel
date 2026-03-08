#version 450 core

// Interpolated from the vertex shader.
in vec2 vTexCoord;
in float vLight;   // combined light level [0,1]
in float vAo;      // ambient occlusion factor [0.4,1.0]

// Output colour written into the back buffer.
out vec4 FragColor;

// The texture atlas bound to texture unit 0.
uniform sampler2D uTexture;

// Debug toggle: when non-zero, skip atlas sampling and output solid white.
// Useful for verifying mesh geometry and lighting without texture noise.
// When set to 2, output the AO factor only (greyscale) for debugging.
uniform int uNoTexture;

void main()
{
    // Minimum ambient light so deep caves are never completely black.
    float ambient  = 0.05;
    float lighting = max(ambient, vLight * vAo);

    if (uNoTexture == 2)
    {
        // AO debug mode: show AO + light as greyscale, no texture.
        FragColor = vec4(vec3(lighting), 1.0);
    }
    else if (uNoTexture != 0)
    {
        // Original white mode — now also modulated by lighting.
        FragColor = vec4(vec3(lighting), 1.0);
    }
    else
    {
        vec4 texColor = texture(uTexture, vTexCoord);
        FragColor = vec4(texColor.rgb * lighting, texColor.a);
    }
}
