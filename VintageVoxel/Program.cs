using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using VintageVoxel;

// GameWindowSettings controls the game loop (update rate).
// RenderFrequency = 0.0 means render as fast as possible (unlocked framerate).
var gameSettings = new GameWindowSettings
{
    UpdateFrequency = 60.0,  // Target 60 logic updates per second
};

// NativeWindowSettings controls the OS window (size, title, GL version).
var nativeSettings = new NativeWindowSettings
{
    ClientSize = new Vector2i(800, 600),
    Title = "VintageVoxel",
    // Request OpenGL 4.5 Core Profile — no legacy fixed-function pipeline.
    APIVersion = new Version(4, 5),
    Profile = ContextProfile.Core,
    Flags = ContextFlags.ForwardCompatible,
};

using var game = new Game(gameSettings, nativeSettings);
// Run() starts the game loop: it calls OnUpdateFrame and OnRenderFrame repeatedly
// until the window is closed, then disposes the GL context and OS window.
game.Run();

