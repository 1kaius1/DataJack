// SPDX-License-Identifier: GPL-3.0-or-later
// NicklistPanel: displays channel members grouped by their highest mode prefix.
// See ARCHITECTURE.md §6.4 NicklistPanel.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using DataJack.Ui.Buffers;
using DataJack.Ui.Themes;

namespace DataJack.Ui.Rendering;

/// <summary>
/// Displays the list of users in the active channel, grouped by mode prefix in descending
/// privilege order. Updates automatically when the channel's member list changes.
/// </summary>
public sealed class NicklistPanel : Border
{
    // Prefix order from highest to lowest privilege (HexChat convention).
    private static readonly (char Prefix, string Label)[] s_prefixGroups =
    {
        ('~', "Owners"),
        ('&', "Admins"),
        ('@', "Ops"),
        ('%', "Halfops"),
        ('+', "Voiced"),
        ('\0', "Users"),
    };

    private readonly ScrollViewer _scroll;
    private readonly StackPanel   _panel;
    private ThemeManager          _theme;
    private ChannelBuffer?        _channel;

    public NicklistPanel(ThemeManager theme)
    {
        _theme = theme;
        _panel = new StackPanel { Orientation = Orientation.Vertical };
        _scroll = new ScrollViewer
        {
            Content                  = _panel,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility   = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Width = 140,
        };
        Background = new SolidColorBrush(ThemeManager.ParseHex(theme.Theme.Chrome.NicklistBackground));
        Child = _scroll;
    }

    /// <summary>Switch the nicklist to display a different channel.</summary>
    public void SetChannel(ChannelBuffer? channel)
    {
        _channel = channel;
        Refresh();
    }

    /// <summary>Rebuild the nicklist from the current channel's member list.</summary>
    public void Refresh()
    {
        _panel.Children.Clear();
        if (_channel is null) return;

        var members = _channel.Members;

        foreach (var (prefix, label) in s_prefixGroups)
        {
            var group = prefix == '\0'
                ? members.Where(m => m.HighestPrefix == '\0').ToList()
                : members.Where(m => m.HighestPrefix == prefix).ToList();

            if (group.Count == 0) continue;

            // Group header
            _panel.Children.Add(BuildGroupHeader($"{label} ({group.Count})"));

            // Sort within group: case-insensitive alphabetical.
            group.Sort((a, b) => string.Compare(a.Nick, b.Nick, StringComparison.OrdinalIgnoreCase));

            foreach (var member in group)
                _panel.Children.Add(BuildNickRow(member));
        }
    }

    private Control BuildGroupHeader(string text)
    {
        return new TextBlock
        {
            Text       = text,
            Foreground = new SolidColorBrush(ThemeManager.ParseHex(_theme.Theme.Chrome.NickGroupHeaderForeground)),
            FontSize   = (_theme.Theme.FontSize ?? 13) - 1,
            Margin     = new Thickness(4, 6, 4, 2),
        };
    }

    private Control BuildNickRow(ChannelMember member)
    {
        string display = member.Prefixes.Length > 0
            ? $"{member.Prefixes[0]}{member.Nick}"
            : member.Nick;

        var nickLabel = new TextBlock
        {
            Text     = display,
            FontSize = _theme.Theme.FontSize ?? 13,
            Margin   = new Thickness(8, 1, 4, 1),
            Foreground = new SolidColorBrush(ThemeManager.ParseHex(_theme.Theme.Chrome.Foreground)),
        };

        var row = new Border
        {
            Child = nickLabel,
        };

        row.ContextMenu = BuildContextMenu(member.Nick);

        // Hover highlight.
        row.PointerEntered += (_, _) =>
            row.Background = new SolidColorBrush(ThemeManager.ParseHex(_theme.Theme.Chrome.SelectionBackground));
        row.PointerExited  += (_, _) =>
            row.Background = null;

        return row;
    }

    private ContextMenu BuildContextMenu(string nick)
    {
        var menu = new ContextMenu();
        menu.ItemsSource = new[]
        {
            new MenuItem { Header = "WHOIS",  Command = new DelegateCommand(() => NickAction?.Invoke(nick, "whois")) },
            new MenuItem { Header = "Query",  Command = new DelegateCommand(() => NickAction?.Invoke(nick, "query")) },
            new MenuItem { Header = "-" },
            new MenuItem { Header = "Op",     Command = new DelegateCommand(() => NickAction?.Invoke(nick, "op")) },
            new MenuItem { Header = "Deop",   Command = new DelegateCommand(() => NickAction?.Invoke(nick, "deop")) },
            new MenuItem { Header = "Voice",  Command = new DelegateCommand(() => NickAction?.Invoke(nick, "voice")) },
            new MenuItem { Header = "Devoice",Command = new DelegateCommand(() => NickAction?.Invoke(nick, "devoice")) },
            new MenuItem { Header = "-" },
            new MenuItem { Header = "Kick",   Command = new DelegateCommand(() => NickAction?.Invoke(nick, "kick")) },
            new MenuItem { Header = "Ban",    Command = new DelegateCommand(() => NickAction?.Invoke(nick, "ban")) },
            new MenuItem { Header = "Ignore", Command = new DelegateCommand(() => NickAction?.Invoke(nick, "ignore")) },
        };
        return menu;
    }

    /// <summary>
    /// Raised when the user selects a context menu action.
    /// Parameters: (nick, action) where action is one of: whois, query, op, deop,
    /// voice, devoice, kick, ban, ignore.
    /// </summary>
    public event Action<string, string>? NickAction;

    // ---------------------------------------------------------------------------
    // Minimal ICommand wrapper for context menu items
    // ---------------------------------------------------------------------------

    private sealed class DelegateCommand : System.Windows.Input.ICommand
    {
        private readonly Action _execute;
        public DelegateCommand(Action execute) => _execute = execute;
        public bool CanExecute(object? _) => true;
        public void Execute(object? _) => _execute();
        public event EventHandler? CanExecuteChanged { add { } remove { } }
    }
}
