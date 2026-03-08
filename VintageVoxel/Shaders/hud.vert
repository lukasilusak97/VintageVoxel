#version 450 core

// 2-D vertex attributes (pixel-space coordinates are converted to clip space
// by the orthographic projection uniform — no view or model matrices needed).
layout(location = 0) in vec2 aPosition;  // screen-pixel XY (0,0 = top-left)
layout(location = 1) in vec2 aTexCoord;  // atlas UV [0,1]

uniform mat4 uProjection;

out vec2 vTexCoord;

void main()
{
    gl_Position = uProjection * vec4(aPosition, 0.0, 1.0);
    vTexCoord = aTexCoord;
}
