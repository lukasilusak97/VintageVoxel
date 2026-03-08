#version 450 core

// Solid-colour fragment shader used for debug line overlays.
// uColor is set from C# (e.g. red for chunk borders).
uniform vec3 uColor;

out vec4 FragColor;

void main()
{
    FragColor = vec4(uColor, 1.0);
}
