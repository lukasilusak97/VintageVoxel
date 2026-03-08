#version 450 core

// Minimal vertex shader for rendering world-space line geometry.
// Only needs position — no UVs or textures.
layout(location = 0) in vec3 aPosition;

uniform mat4 view;
uniform mat4 projection;

void main()
{
    gl_Position = projection * view * vec4(aPosition, 1.0);
}
