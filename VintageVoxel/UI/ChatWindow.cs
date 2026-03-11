using ImGuiNET;
using System.Numerics;
using VintageVoxel.Networking;

namespace VintageVoxel.UI;

/// <summary>
/// A small ImGui chat overlay rendered during gameplay.
///
/// Messages are accumulated from <see cref="AddMessage"/> and displayed in a scrollable
/// list. The player can type a message in the input field and press Enter (or click Send)
/// to call <see cref="OnSend"/>.
/// </summary>
public sealed class ChatWindow
{
    private const int MaxMessages = 100;
    private const float WindowWidth = 420f;
    private const float WindowHeight = 180f;
    private const float InputHeight = 28f;
    // How long (seconds) a message stays fully visible before fading.
    private const float VisibleDuration = 8f;

    public record ChatLine(string DisplayText, float ReceivedAt);

    private readonly List<ChatLine> _lines = new();
    private string _input = "";
    private bool _scrollToBottom;
    private float _time;

    /// <summary>Called when the player submits a message. Hook this to <see cref="GameClient.SendChat"/>.</summary>
    public event Action<string>? OnSend;

    // Whether the chat input field is focused (ESC / Enter closes it).
    private bool _inputFocused;
    public bool IsInputOpen => _inputFocused;

    // -------------------------------------------------------------------------
    // Public API
    // -------------------------------------------------------------------------

    /// <summary>Appends a new line to the chat history.</summary>
    public void AddMessage(string name, string message)
    {
        string text = string.IsNullOrEmpty(name) ? message : $"<{name}> {message}";
        _lines.Add(new ChatLine(text, _time));
        if (_lines.Count > MaxMessages)
            _lines.RemoveAt(0);
        _scrollToBottom = true;
    }

    /// <summary>Opens the chat input so the player can type.</summary>
    public void OpenInput() => _inputFocused = true;

    /// <summary>
    /// Draws the chat overlay. Call every frame inside an ImGui frame.
    /// <paramref name="dt"/> is used to age messages for fade-out.
    /// </summary>
    public void Draw(float dt, float vpWidth, float vpHeight)
    {
        _time += dt;

        // ── Message list ─────────────────────────────────────────────────────
        float listY = vpHeight - WindowHeight - InputHeight - 10f;
        ImGui.SetNextWindowPos(new Vector2(8f, listY), ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(WindowWidth, WindowHeight), ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(_inputFocused ? 0.55f : 0.25f);

        var listFlags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoNav |
                        ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoInputs |
                        ImGuiWindowFlags.NoSavedSettings;

        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(6f, 4f));
        if (ImGui.Begin("##chat_messages", listFlags))
        {
            foreach (var line in _lines)
            {
                float age = _time - line.ReceivedAt;
                float alpha = _inputFocused ? 1f : Math.Max(0f, 1f - (age - VisibleDuration) * 0.5f);
                if (alpha <= 0f) continue;
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 1f, 1f, alpha));
                ImGui.TextWrapped(line.DisplayText);
                ImGui.PopStyleColor();
            }

            if (_scrollToBottom)
            {
                ImGui.SetScrollHereY(1f);
                _scrollToBottom = false;
            }
        }
        ImGui.End();
        ImGui.PopStyleVar();

        // ── Input field ───────────────────────────────────────────────────────
        if (!_inputFocused) return;

        float inputY = vpHeight - InputHeight - 10f;
        ImGui.SetNextWindowPos(new Vector2(8f, inputY), ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(WindowWidth, InputHeight + 8f), ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0.75f);

        var inputFlags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoNav |
                         ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings;

        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(6f, 4f));
        if (ImGui.Begin("##chat_input", inputFlags))
        {
            ImGui.SetNextItemWidth(WindowWidth - 70f);
            ImGui.SetKeyboardFocusHere();

            bool submitted = ImGui.InputText("##msg", ref _input, 256,
                                             ImGuiInputTextFlags.EnterReturnsTrue);
            ImGui.SameLine();
            if ((submitted || ImGui.Button("Send")) && !string.IsNullOrWhiteSpace(_input))
            {
                OnSend?.Invoke(_input.Trim());
                _input = "";
                _inputFocused = false;
            }

            // ESC closes without sending.
            if (ImGui.IsKeyPressed(ImGuiKey.Escape))
            {
                _input = "";
                _inputFocused = false;
            }
        }
        ImGui.End();
        ImGui.PopStyleVar();
    }
}
