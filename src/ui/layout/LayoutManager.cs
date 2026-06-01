// SPDX-License-Identifier: GPL-3.0-or-later
// LayoutManager: the main window layout. Tab bar (HexChat-style) only for Phase 2.
// Tree view and split view are Phase 4. See ARCHITECTURE.md §6.4 LayoutManager.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using DataJack.Ui.Buffers;
using DataJack.Ui.Rendering;
using DataJack.Ui.Themes;

namespace DataJack.Ui.Layout;

/// <summary>
/// Assembles the application layout: tab strip (top), message area (center),
/// nicklist (right), input box (bottom), status bar (bottom-right).
/// Reacts to <see cref="BufferManager"/> events to add/remove tabs automatically.
/// </summary>
public sealed class LayoutManager : Grid, IDisposable
{
    private readonly BufferManager  _buffers;
    private readonly ThemeManager   _theme;
    private readonly TabControl     _tabs;
    private readonly MessageView    _messageView;
    private readonly NicklistPanel  _nicklist;
    private readonly InputBox       _inputBox;
    private readonly TextBlock      _statusBar;
    private IBuffer?                _active;
    private bool                    _disposed;

    public LayoutManager(BufferManager buffers, ThemeManager theme)
    {
        _buffers = buffers;
        _theme   = theme;

        // Row definitions: tab bar | content area | input + status bar
        RowDefinitions.Add(new RowDefinition(GridLength.Auto));   // 0: tab strip
        RowDefinitions.Add(new RowDefinition(GridLength.Star));   // 1: content
        RowDefinitions.Add(new RowDefinition(GridLength.Auto));   // 2: input + status

        ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));  // 0: message area
        ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));  // 1: nicklist

        // Tab strip
        _tabs = new TabControl
        {
            Background = new SolidColorBrush(ThemeManager.ParseHex(theme.Theme.Chrome.TabBackground)),
        };
        Grid.SetRow(_tabs, 0);
        Grid.SetColumnSpan(_tabs, 2);
        Children.Add(_tabs);

        // Message view (fills center)
        _messageView = new MessageView(theme)
        {
            Background = new SolidColorBrush(ThemeManager.ParseHex(theme.Theme.Chrome.Background)),
        };
        Grid.SetRow(_messageView, 1);
        Grid.SetColumn(_messageView, 0);
        Children.Add(_messageView);

        // Nicklist (right column, hidden when not on a channel)
        _nicklist = new NicklistPanel(theme);
        _nicklist.IsVisible = false;
        _nicklist.NickAction += OnNickAction;
        Grid.SetRow(_nicklist, 1);
        Grid.SetColumn(_nicklist, 1);
        Children.Add(_nicklist);

        // Input + status bar row
        var inputRow = new Grid();
        inputRow.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        inputRow.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

        _inputBox = new InputBox(theme);
        _inputBox.CommandSubmitted += OnCommandSubmitted;
        _inputBox.MessageSubmitted += OnMessageSubmitted;
        Grid.SetColumn(_inputBox, 0);
        inputRow.Children.Add(_inputBox);

        _statusBar = new TextBlock
        {
            Text       = "Not connected",
            Foreground = new SolidColorBrush(ThemeManager.ParseHex(theme.Theme.Chrome.StatusBarForeground)),
            Background = new SolidColorBrush(ThemeManager.ParseHex(theme.Theme.Chrome.StatusBarBackground)),
            Padding    = new Thickness(8, 4),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(_statusBar, 1);
        inputRow.Children.Add(_statusBar);

        Grid.SetRow(inputRow, 2);
        Grid.SetColumnSpan(inputRow, 2);
        Children.Add(inputRow);

        // Wire tab selection to buffer activation.
        _tabs.SelectionChanged += OnTabSelectionChanged;

        // Subscribe to buffer events.
        _buffers.BufferCreated   += OnBufferCreated;
        _buffers.BufferDestroyed += OnBufferDestroyed;

        // Add existing buffers (in case LayoutManager is constructed after buffers exist).
        foreach (var buf in _buffers.Buffers)
            AddTab(buf);

        // Activate the first buffer.
        if (_buffers.Buffers.Count > 0)
            ActivateBuffer(_buffers.Buffers[0]);
    }

    // ---------------------------------------------------------------------------
    // Tab management
    // ---------------------------------------------------------------------------

    private void OnTabSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_tabs.SelectedItem is TabItem item && item.Tag is IBuffer buf)
            ActivateBuffer(buf);
    }

    private void OnBufferCreated(IBuffer buffer)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() => AddTab(buffer));
    }

    private void OnBufferDestroyed(IBuffer buffer)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() => RemoveTab(buffer));
    }

    private void AddTab(IBuffer buffer)
    {
        var header = new TextBlock
        {
            Text       = buffer.Title,
            Foreground = new SolidColorBrush(ThemeManager.ParseHex(_theme.Theme.Chrome.TabForeground)),
            Padding    = new Thickness(8, 4),
        };

        var item = new TabItem
        {
            Header  = header,
            Tag     = buffer,
            Content = new Border(), // content rendered in the shared MessageView, not inside the TabItem
        };

        // Highlight unread tabs.
        buffer.MessageAdded += _ =>
        {
            if (_active != buffer)
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    header.Foreground = new SolidColorBrush(
                        ThemeManager.ParseHex(_theme.Theme.Chrome.TabUnreadForeground)));
        };

        _tabs.Items.Add(item);
    }

    private void RemoveTab(IBuffer buffer)
    {
        var item = FindTab(buffer);
        if (item is null) return;
        _tabs.Items.Remove(item);

        if (_active == buffer && _tabs.Items.Count > 0)
            ((TabItem)_tabs.Items[0]!).IsSelected = true;
    }

    private TabItem? FindTab(IBuffer buffer) =>
        _tabs.Items.OfType<TabItem>().FirstOrDefault(t => t.Tag == buffer);

    // ---------------------------------------------------------------------------
    // Buffer activation
    // ---------------------------------------------------------------------------

    private void ActivateBuffer(IBuffer buffer)
    {
        _active = buffer;
        buffer.MarkRead();

        // Reset the tab header color to the normal tab foreground.
        if (FindTab(buffer) is { Header: TextBlock header })
            header.Foreground = new SolidColorBrush(ThemeManager.ParseHex(_theme.Theme.Chrome.TabActiveForeground));

        _messageView.SetBuffer(buffer);

        if (buffer is ChannelBuffer ch)
        {
            _nicklist.SetChannel(ch);
            _nicklist.IsVisible = true;
            _inputBox.SetActiveChannel(ch);
        }
        else
        {
            _nicklist.IsVisible = false;
            _inputBox.SetActiveChannel(null);
        }
    }

    // ---------------------------------------------------------------------------
    // Command and message routing
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Raised when the user submits a slash command. The calling code (MainWindow)
    /// routes it to the IRCCommandRouter.
    /// </summary>
    public event Action<string, IBuffer?>? CommandIssued;

    /// <summary>Raised when the user submits a plain message to the active buffer.</summary>
    public event Action<string, IBuffer?>? MessageIssued;

    private void OnCommandSubmitted(string cmd) =>
        CommandIssued?.Invoke(cmd, _active);

    private void OnMessageSubmitted(string msg) =>
        MessageIssued?.Invoke(msg, _active);

    // ---------------------------------------------------------------------------
    // Nicklist actions
    // ---------------------------------------------------------------------------

    private void OnNickAction(string nick, string action)
    {
        if (_active is null) return;
        string server = _active.Server;
        string channel = (_active as ChannelBuffer)?.Channel ?? string.Empty;

        string cmd = action switch
        {
            "whois"   => $"/whois {nick}",
            "query"   => $"/query {nick}",
            "op"      => $"/op {nick}",
            "deop"    => $"/deop {nick}",
            "voice"   => $"/voice {nick}",
            "devoice" => $"/devoice {nick}",
            "kick"    => $"/kick {channel} {nick}",
            "ban"     => $"/ban {channel} {nick}",
            "ignore"  => $"/ignore {nick}",
            _         => string.Empty,
        };

        if (cmd.Length > 0) CommandIssued?.Invoke(cmd, _active);
    }

    // ---------------------------------------------------------------------------
    // Public helpers
    // ---------------------------------------------------------------------------

    /// <summary>Update the status bar text (e.g. connection state, lag).</summary>
    public void SetStatus(string text)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() => _statusBar.Text = text);
    }

    /// <summary>Focus the input box.</summary>
    public void FocusInput() => _inputBox.Focus();

    // ---------------------------------------------------------------------------
    // Disposal
    // ---------------------------------------------------------------------------

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _buffers.BufferCreated   -= OnBufferCreated;
        _buffers.BufferDestroyed -= OnBufferDestroyed;
    }
}
