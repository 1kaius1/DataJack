// SPDX-License-Identifier: GPL-3.0-or-later
// Non-virtualized scrollback list for Phase 2. See ARCHITECTURE.md §6.2 and §6.4 MessageView.
// Virtualization is deferred to Phase 4 when the message model is stable.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using DataJack.Core.Irc;
using DataJack.Ui.Buffers;
using DataJack.Ui.Themes;

namespace DataJack.Ui.Rendering;

/// <summary>
/// A scrolling list of IRC message rows. Renders each <see cref="MessageEntry"/> as a
/// timestamp + nick + styled text row. Phase 2 holds all messages in memory (no spill).
/// </summary>
public sealed class MessageView : Border
{
    private readonly ScrollViewer _scroll;
    private readonly StackPanel   _panel;
    private ThemeManager          _theme;
    private IBuffer?              _buffer;

    // Called when the user clicks a URL span in a message.
    public event Action<string>? UrlClicked;

    public MessageView(ThemeManager theme)
    {
        _theme = theme;
        _panel = new StackPanel { Orientation = Orientation.Vertical };
        _scroll = new ScrollViewer
        {
            Content                  = _panel,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility   = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };
        Child = _scroll;
    }

    // ---------------------------------------------------------------------------
    // Buffer binding
    // ---------------------------------------------------------------------------

    /// <summary>Switch the view to display a different buffer.</summary>
    public void SetBuffer(IBuffer? buffer)
    {
        if (_buffer is not null)
            _buffer.MessageAdded -= OnMessageAdded;

        _buffer = buffer;
        _panel.Children.Clear();

        if (_buffer is null) return;

        foreach (var msg in _buffer.Messages)
            _panel.Children.Add(BuildRow(msg));

        _buffer.MessageAdded += OnMessageAdded;
        ScrollToBottom();
    }

    /// <summary>Apply a new theme and redraw the visible messages.</summary>
    public void ApplyTheme(ThemeManager theme)
    {
        _theme = theme;
        var buf = _buffer;
        SetBuffer(null);
        SetBuffer(buf);
    }

    // ---------------------------------------------------------------------------
    // Row construction
    // ---------------------------------------------------------------------------

    private void OnMessageAdded(MessageEntry msg)
    {
        // MessageAdded fires on the IRC dispatch thread; marshal to UI thread before
        // touching Avalonia controls (controls must be created and mutated on their owner thread).
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _panel.Children.Add(BuildRow(msg));
            ScrollToBottom();
        });
    }

    private Control BuildRow(MessageEntry msg)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin      = new Thickness(0, 1),
        };

        // Timestamp
        if (_theme.Theme.TimestampFormat.Length > 0)
        {
            var tsLabel = new TextBlock
            {
                Text       = msg.Timestamp.ToLocalTime().ToString(_theme.Theme.TimestampFormat),
                Foreground = new SolidColorBrush(ThemeManager.ParseHex(_theme.Theme.Chrome.StatusBarForeground)),
                Margin     = new Thickness(0, 0, 6, 0),
                FontSize   = _theme.Theme.FontSize ?? 13,
            };
            row.Children.Add(tsLabel);
        }

        // Nick (and prefix punctuation)
        string nickDisplay = FormatNickLabel(msg);
        if (nickDisplay.Length > 0)
        {
            var nickBlock = new TextBlock
            {
                Margin   = new Thickness(0, 0, 4, 0),
                FontSize = _theme.Theme.FontSize ?? 13,
            };

            if (msg.Kind == MessageKind.Normal && msg.Nick is not null)
            {
                nickBlock.Inlines!.Add(new Run("<"));
                nickBlock.Inlines.Add(IrcTextRenderer.RenderNick(msg.Nick));
                nickBlock.Inlines.Add(new Run(">"));
            }
            else
            {
                nickBlock.Text       = nickDisplay;
                nickBlock.Foreground = KindBrush(msg.Kind);
            }

            row.Children.Add(nickBlock);
        }

        // Message body
        var body = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize     = _theme.Theme.FontSize ?? 13,
        };

        string text = msg.Kind == MessageKind.Action && msg.Nick is not null
            ? $"* {msg.Nick} {msg.Text}"
            : msg.Text;

        // Parse spans; collect URLs, then wire a single PointerPressed handler per row.
        // Wiring one handler per URL span caused N handlers to accumulate on the shared
        // body TextBlock, opening N browser tabs on any click for messages with N URLs.
        var spans = IrcTextParser.Parse(text);
        var inlines = IrcTextRenderer.Render(text, _theme).ToList();
        var rowUrls = new List<string>();
        for (int si = 0; si < inlines.Count && si < spans.Length; si++)
        {
            if (spans[si].Url is string url)
                rowUrls.Add(url);
            body.Inlines!.Add(inlines[si]);
        }

        // Coarse whole-body hit testing: clicking anywhere on the row opens the first URL.
        // Phase 4 can refine to per-run hit testing once layout geometry is stable.
        if (rowUrls.Count > 0)
            body.PointerPressed += (_, _) => UrlClicked?.Invoke(rowUrls[0]);

        body.Foreground = KindBrush(msg.Kind);
        row.Children.Add(body);

        return row;
    }

    private static string FormatNickLabel(MessageEntry msg) => msg.Kind switch
    {
        MessageKind.Normal  => string.Empty, // nick rendered separately with coloring
        MessageKind.Action  => string.Empty,
        MessageKind.Join    => "-->",
        MessageKind.Part    => "<--",
        MessageKind.Quit    => "<--",
        MessageKind.Kick    => "<--",
        MessageKind.NickChange => "---",
        MessageKind.Topic   => "---",
        MessageKind.Mode    => "---",
        MessageKind.Error   => "***",
        MessageKind.Info    => "---",
        MessageKind.Motd    => "---",
        MessageKind.Notice  => msg.Nick is not null ? $"-{msg.Nick}-" : "-!-",
        MessageKind.ServerNotice => "-!-",
        MessageKind.RawLine => string.Empty,
        _                   => string.Empty,
    };

    private IBrush KindBrush(MessageKind kind) => kind switch
    {
        MessageKind.Join        => new SolidColorBrush(ThemeManager.ParseHex("#A6E3A1")),
        MessageKind.Part        => new SolidColorBrush(ThemeManager.ParseHex("#6C7086")),
        MessageKind.Quit        => new SolidColorBrush(ThemeManager.ParseHex("#6C7086")),
        MessageKind.Kick        => new SolidColorBrush(ThemeManager.ParseHex("#F38BA8")),
        MessageKind.Error       => new SolidColorBrush(ThemeManager.ParseHex("#F38BA8")),
        MessageKind.Notice      => new SolidColorBrush(ThemeManager.ParseHex("#FAB387")),
        MessageKind.ServerNotice => new SolidColorBrush(ThemeManager.ParseHex("#FAB387")),
        MessageKind.Motd        => new SolidColorBrush(ThemeManager.ParseHex("#6C7086")),
        MessageKind.Info        => new SolidColorBrush(ThemeManager.ParseHex("#89B4FA")),
        MessageKind.RawLine     => new SolidColorBrush(ThemeManager.ParseHex("#585B70")),
        _ => new SolidColorBrush(ThemeManager.ParseHex(_theme.Theme.Chrome.Foreground)),
    };

    private void ScrollToBottom() =>
        _scroll.ScrollToEnd();
}
