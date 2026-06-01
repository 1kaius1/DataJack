// SPDX-License-Identifier: GPL-3.0-or-later
// InputBox: command/message entry with per-buffer history and nick tab completion.
// See ARCHITECTURE.md §6.4 InputBox.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using DataJack.Ui.Buffers;
using DataJack.Ui.Themes;

namespace DataJack.Ui.Layout;

/// <summary>
/// Single-line text entry for IRC commands and messages.
/// Up/Down arrows navigate per-buffer history. Tab completes nicks.
/// A leading '/' character marks the input as a command.
/// </summary>
public sealed class InputBox : Border
{
    private const int HistoryDepth = 100;

    private readonly TextBox         _textBox;
    private readonly List<string>    _history   = new();
    private int                      _histIdx   = -1;
    private string                   _draft     = string.Empty; // saved text while browsing history

    // Tab completion state
    private List<string>             _candidates   = new();
    private int                      _candidateIdx = 0;
    private string                   _completionPrefix = string.Empty;

    private ChannelBuffer?           _activeChannel;

    // ---------------------------------------------------------------------------
    // Events
    // ---------------------------------------------------------------------------

    /// <summary>Raised when the user submits a command (line starting with '/').</summary>
    public event Action<string>? CommandSubmitted;

    /// <summary>Raised when the user submits a normal message (no '/' prefix).</summary>
    public event Action<string>? MessageSubmitted;

    // ---------------------------------------------------------------------------
    // Construction
    // ---------------------------------------------------------------------------

    public InputBox(ThemeManager theme)
    {
        _textBox = new TextBox
        {
            Background      = new SolidColorBrush(ThemeManager.ParseHex(theme.Theme.Chrome.InputBackground)),
            Foreground      = new SolidColorBrush(ThemeManager.ParseHex(theme.Theme.Chrome.InputForeground)),
            BorderThickness = new Thickness(0),
            FontSize        = theme.Theme.FontSize ?? 13,
            Padding         = new Thickness(6, 4),
            CaretBrush      = new SolidColorBrush(ThemeManager.ParseHex(theme.Theme.Chrome.Foreground)),
        };

        if (theme.Theme.FontFamily is not null)
            _textBox.FontFamily = new FontFamily(theme.Theme.FontFamily);

        _textBox.KeyDown += OnKeyDown;
        Child = _textBox;
    }

    // ---------------------------------------------------------------------------
    // Active channel (used for tab completion candidates)
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Set the channel whose member list should be used as tab completion candidates.
    /// Pass null when the active buffer is not a channel.
    /// </summary>
    public void SetActiveChannel(ChannelBuffer? channel)
    {
        _activeChannel = channel;
        ResetCompletion();
    }

    /// <summary>Focus the text entry.</summary>
    public void Focus() => _textBox.Focus();

    // ---------------------------------------------------------------------------
    // Key handling
    // ---------------------------------------------------------------------------

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Return:
                Submit();
                e.Handled = true;
                break;

            case Key.Up:
                NavigateHistory(-1);
                e.Handled = true;
                break;

            case Key.Down:
                NavigateHistory(+1);
                e.Handled = true;
                break;

            case Key.Tab:
                PerformCompletion();
                e.Handled = true;
                break;

            default:
                // Any non-completion key resets the completion cycle.
                if (e.Key != Key.Tab)
                    ResetCompletion();
                break;
        }
    }

    private void Submit()
    {
        string text = _textBox.Text?.Trim() ?? string.Empty;
        if (text.Length == 0) return;

        PushHistory(text);
        _textBox.Text = string.Empty;
        _histIdx = -1;
        _draft   = string.Empty;
        ResetCompletion();

        if (text.StartsWith('/'))
            CommandSubmitted?.Invoke(text);
        else
            MessageSubmitted?.Invoke(text);
    }

    // ---------------------------------------------------------------------------
    // Per-buffer history
    // ---------------------------------------------------------------------------

    private void PushHistory(string entry)
    {
        // Avoid consecutive duplicates.
        if (_history.Count > 0 && _history[^1] == entry) return;

        _history.Add(entry);
        if (_history.Count > HistoryDepth)
            _history.RemoveAt(0);
    }

    private void NavigateHistory(int direction)
    {
        if (_history.Count == 0) return;

        if (_histIdx == -1)
        {
            // Save whatever the user was typing before browsing history.
            _draft   = _textBox.Text ?? string.Empty;
            _histIdx = _history.Count;
        }

        int next = _histIdx + direction;

        if (next < 0)            return; // already at oldest entry
        if (next > _history.Count) return; // already at newest

        _histIdx = next;

        _textBox.Text = _histIdx == _history.Count
            ? _draft
            : _history[_history.Count - 1 - (_history.Count - 1 - _histIdx)];

        // Place caret at end.
        _textBox.SelectionStart = _textBox.Text?.Length ?? 0;
    }

    // ---------------------------------------------------------------------------
    // Tab completion
    // ---------------------------------------------------------------------------

    private void PerformCompletion()
    {
        string text = _textBox.Text ?? string.Empty;

        // Build candidate list on first Tab press.
        if (_candidates.Count == 0)
        {
            int wordStart = text.LastIndexOf(' ') + 1;
            _completionPrefix = text[wordStart..];
            if (_completionPrefix.Length == 0) return;

            _candidates = GetCompletionCandidates(_completionPrefix);
            _candidateIdx = 0;
        }

        if (_candidates.Count == 0) return;

        string match = _candidates[_candidateIdx % _candidates.Count];
        _candidateIdx++;

        int wordStart2 = text.LastIndexOf(' ') + 1;
        string suffix  = wordStart2 == 0 ? ": " : " "; // leading nick gets ": " appended
        _textBox.Text  = text[..wordStart2] + match + suffix;
        _textBox.SelectionStart = _textBox.Text.Length;
    }

    private List<string> GetCompletionCandidates(string prefix)
    {
        var candidates = new List<string>();

        if (_activeChannel is not null)
        {
            foreach (var m in _activeChannel.Members)
                if (m.Nick.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    candidates.Add(m.Nick);
        }

        // Command completion when the prefix starts with '/'.
        if (prefix.StartsWith('/'))
        {
            foreach (var cmd in KnownCommands)
                if (cmd.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    candidates.Add(cmd);
        }

        candidates.Sort(StringComparer.OrdinalIgnoreCase);
        return candidates;
    }

    private void ResetCompletion()
    {
        _candidates   = new List<string>();
        _candidateIdx = 0;
        _completionPrefix = string.Empty;
    }

    // Minimal built-in command name list for completion purposes.
    private static readonly string[] KnownCommands =
    {
        "/join", "/part", "/quit", "/msg", "/notice", "/query", "/nick",
        "/me", "/ctcp", "/mode", "/op", "/deop", "/voice", "/devoice",
        "/kick", "/ban", "/unban", "/kickban", "/invite", "/topic",
        "/away", "/back", "/whois", "/who", "/ignore", "/unignore",
        "/server", "/connect", "/disconnect", "/reconnect", "/list",
        "/names", "/raw", "/quote", "/charset", "/dcc", "/dns", "/ping",
        "/set", "/alias", "/load", "/unload", "/timer", "/help",
    };
}
