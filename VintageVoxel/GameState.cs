namespace VintageVoxel;

/// <summary>
/// Top-level game states that drive the state machine in <see cref="Game"/>.
///
/// Flow:
///   MainMenu  ──Play──▶  Playing  ──ESC──▶  Paused
///                             ▲                │
///                             └──ESC / Resume──┘
///   Any state ──Quit──▶  (window closes)
/// </summary>
public enum GameState
{
    /// <summary>Pre-game main menu. Physics and streaming are paused; cursor is free.</summary>
    MainMenu,

    /// <summary>World is being loaded. Streaming runs but physics and player input are disabled.
    /// A loading overlay is drawn until all initial chunks are ready.</summary>
    Loading,

    /// <summary>Active gameplay. Physics and streaming run; cursor is captured.</summary>
    Playing,

    /// <summary>Pause menu. World is frozen; cursor is free for menu interaction.</summary>
    Paused,

    /// <summary>Sentinel — the window is about to close.</summary>
    Exiting
}
