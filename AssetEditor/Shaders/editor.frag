#version 450 core

// Per-draw-call flat colour, set from C# via the uColor uniform.
uniform vec3 uColor;

out vec4 FragColor;

void main()
{
    FragColor = vec4(uColor, 1.0);
}
