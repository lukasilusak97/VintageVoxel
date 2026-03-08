using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using VintageVoxel.Editor;

var settings = new NativeWindowSettings
{
    ClientSize = new Vector2i(1280, 720),
    Title = "VintageVoxel — Asset Editor",
    Flags = ContextFlags.ForwardCompatible,
    Profile = ContextProfile.Core,
    APIVersion = new Version(4, 5)
};

using var editor = new EditorWindow(GameWindowSettings.Default, settings);
editor.Run();
