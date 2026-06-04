// SPDX-License-Identifier: GPL-3.0-or-later
// LayoutManager: main window layout supporting two navigation modes.
//   "tabs"  — HexChat-style horizontal tab bar at the top (Phase 2 behaviour).
//   "tree"  — mIRC-style vertical server/channel tree sidebar on the left (Phase 3).
// Split view is Phase 4. See ARCHITECTURE.md §6.4 LayoutManager.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using DataJack.Ui.Buffers;
using DataJack.Ui.Rendering;
using DataJack.Ui.Themes;

namespace DataJack.Ui.Layout;

/// <summary>
/// Assembles the application layout: navigation panel (tab bar or tree sidebar),
/// message area, nicklist, input box, and status bar.
///
/// <para>
/// In <em>tabs</em> mode a horizontal <see cref="TabControl"/> spans the full width
/// at the top, exactly as in Phase 2.
/// </para>
/// <para>
/// In <em>tree</em> mode a <see cref="TreeView"/> sidebar is shown on the left.
/// Buffers are grouped by server; global buffers (NetworkStatus, Highlights) appear
/// at the root level. Server nodes are expanded by default.
/// </para>
/// <para>
/// Call <see cref="SetLayoutMode"/> to switch modes at runtime; the change is
/// instantaneous and the active buffer is preserved.
/// </para>
/// </summary>
public sealed class LayoutManager : Grid, IDisposable
{
    // ---------------------------------------------------------------------------
    // Grid layout constants
    // ---------------------------------------------------------------------------

    private const int ColSidebar  = 0; // tree view (width = 0 in tabs mode)
    private const int ColContent  = 1; // message area
    private const int ColNicklist = 2; // nicklist panel

    private const int RowTabs    = 0; // tab strip (auto height; collapsed in tree mode)
    private const int RowContent = 1; // content area
    private const int RowInput   = 2; // input + status bar

    // ---------------------------------------------------------------------------
    // Fields
    // ---------------------------------------------------------------------------

    private readonly BufferManager _buffers;
    private readonly ThemeManager  _theme;

    // Shared components
    private readonly MessageView   _messageView;
    private readonly NicklistPanel _nicklist;
    private readonly InputBox      _inputBox;
    private readonly TextBlock     _statusBar;

    // Tab mode
    private readonly TabControl    _tabs;

    // Tree mode
    private readonly TreeView      _tree;
    // serverId → parent TreeViewItem (contains child buffer nodes)
    private readonly Dictionary<string, TreeViewItem> _serverNodes   = new();
    // buffer.Id → leaf TreeViewItem
    private readonly Dictionary<string, TreeViewItem> _nodesByBuffer = new();

    // Layout state
    private readonly ColumnDefinition _sidebarColumn;
    private string   _layoutMode = "tabs";
    private IBuffer? _active;
    private bool     _disposed;

    // ---------------------------------------------------------------------------
    // Public state
    // ---------------------------------------------------------------------------

    /// <summary>The current layout mode: <c>"tabs"</c> or <c>"tree"</c>.</summary>
    public string CurrentLayoutMode => _layoutMode;

    // ---------------------------------------------------------------------------
    // Events
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Raised when the user submits a slash command in the input box.
    /// The caller (MainWindow) routes it to the <c>IRCCommandRouter</c>.
    /// </summary>
    public event Action<string, IBuffer?>? CommandIssued;

    /// <summary>Raised when the user submits a plain message to the active buffer.</summary>
    public event Action<string, IBuffer?>? MessageIssued;

    // ---------------------------------------------------------------------------
    // Construction
    // ---------------------------------------------------------------------------

    /// <param name="buffers">Source of buffer lifecycle events.</param>
    /// <param name="theme">Theme source; used for all chrome colors.</param>
    /// <param name="initialLayoutMode">
    /// Starting mode: <c>"tabs"</c> (default) or <c>"tree"</c>.
    /// Loaded from <see cref="DataJack.Core.Storage.Config.AppearanceSettings.LayoutMode"/>.
    /// </param>
    public LayoutManager(
        BufferManager buffers,
        ThemeManager  theme,
        string        initialLayoutMode = "tabs")
    {
        _buffers = buffers;
        _theme   = theme;

        // -----------------------------------------------------------------------
        // Grid columns: sidebar | content | nicklist
        // -----------------------------------------------------------------------
        _sidebarColumn = new ColumnDefinition(new GridLength(0)); // collapsed until tree mode
        ColumnDefinitions.Add(_sidebarColumn);
        ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

        // Grid rows: tab bar | content | input
        RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        RowDefinitions.Add(new RowDefinition(GridLength.Star));
        RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        // -----------------------------------------------------------------------
        // Tab bar (tabs mode only — spans all 3 columns)
        // -----------------------------------------------------------------------
        _tabs = new TabControl
        {
            Background = new SolidColorBrush(ThemeManager.ParseHex(theme.Theme.Chrome.TabBackground)),
        };
        Grid.SetRow(_tabs, RowTabs);
        Grid.SetColumn(_tabs, ColSidebar);
        Grid.SetColumnSpan(_tabs, 3);
        Children.Add(_tabs);

        // -----------------------------------------------------------------------
        // Tree sidebar (tree mode only — spans rows 0 and 1, col 0)
        // -----------------------------------------------------------------------
        _tree = new TreeView
        {
            Background = new SolidColorBrush(ThemeManager.ParseHex(theme.Theme.Chrome.TabBackground)),
            IsVisible  = false,
        };
        Grid.SetRow(_tree, RowTabs);
        Grid.SetColumn(_tree, ColSidebar);
        Grid.SetRowSpan(_tree, 2); // spans the tab-row and content-row heights
        Children.Add(_tree);

        // -----------------------------------------------------------------------
        // Message view
        // -----------------------------------------------------------------------
        _messageView = new MessageView(theme)
        {
            Background = new SolidColorBrush(ThemeManager.ParseHex(theme.Theme.Chrome.Background)),
        };
        Grid.SetRow(_messageView, RowContent);
        Grid.SetColumn(_messageView, ColContent);
        Children.Add(_messageView);

        // -----------------------------------------------------------------------
        // Nicklist
        // -----------------------------------------------------------------------
        _nicklist = new NicklistPanel(theme) { IsVisible = false };
        _nicklist.NickAction += OnNickAction;
        Grid.SetRow(_nicklist, RowContent);
        Grid.SetColumn(_nicklist, ColNicklist);
        Children.Add(_nicklist);

        // -----------------------------------------------------------------------
        // Input + status bar row
        // -----------------------------------------------------------------------
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
            Text              = "Not connected",
            Foreground        = new SolidColorBrush(ThemeManager.ParseHex(theme.Theme.Chrome.StatusBarForeground)),
            Background        = new SolidColorBrush(ThemeManager.ParseHex(theme.Theme.Chrome.StatusBarBackground)),
            Padding           = new Thickness(8, 4),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(_statusBar, 1);
        inputRow.Children.Add(_statusBar);

        Grid.SetRow(inputRow, RowInput);
        Grid.SetColumn(inputRow, ColSidebar);
        Grid.SetColumnSpan(inputRow, 3);
        Children.Add(inputRow);

        // -----------------------------------------------------------------------
        // Event wiring
        // -----------------------------------------------------------------------
        _tabs.SelectionChanged += OnTabSelectionChanged;
        _tree.SelectionChanged += OnTreeSelectionChanged;
        _buffers.BufferCreated   += OnBufferCreated;
        _buffers.BufferDestroyed += OnBufferDestroyed;

        // Populate both views with any buffers that already exist.
        foreach (var buf in _buffers.Buffers)
            AddBufferToViews(buf);

        // Apply the initial layout mode (sets visibility and column widths).
        SetLayoutMode(initialLayoutMode);

        if (_buffers.Buffers.Count > 0)
            ActivateBuffer(_buffers.Buffers[0]);
    }

    // ---------------------------------------------------------------------------
    // Layout mode switching
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Switches to the given layout mode (<c>"tabs"</c> or <c>"tree"</c>).
    /// Illegal values are silently ignored. The active buffer is preserved.
    /// </summary>
    public void SetLayoutMode(string mode)
    {
        if (mode != "tabs" && mode != "tree")
            return;

        _layoutMode = mode;

        if (mode == "tree")
        {
            _tabs.IsVisible      = false;
            _sidebarColumn.Width = new GridLength(200);
            _tree.IsVisible      = true;
            if (_active is not null)
                SelectTreeNode(_active);
        }
        else
        {
            _tree.IsVisible      = false;
            _sidebarColumn.Width = new GridLength(0);
            _tabs.IsVisible      = true;
            if (_active is not null && FindTab(_active) is { } tab)
                tab.IsSelected = true;
        }
    }

    /// <summary>Toggles between <c>"tabs"</c> and <c>"tree"</c> mode.</summary>
    public void ToggleLayoutMode() =>
        SetLayoutMode(_layoutMode == "tabs" ? "tree" : "tabs");

    // ---------------------------------------------------------------------------
    // Tab management (tabs mode)
    // ---------------------------------------------------------------------------

    private void OnTabSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_tabs.SelectedItem is TabItem item && item.Tag is IBuffer buf)
            ActivateBuffer(buf);
    }

    private void OnBufferCreated(IBuffer buffer)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() => AddBufferToViews(buffer));
    }

    private void OnBufferDestroyed(IBuffer buffer)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() => RemoveBufferFromViews(buffer));
    }

    private void AddBufferToViews(IBuffer buffer)
    {
        AddTab(buffer);
        AddTreeNode(buffer);
    }

    private void RemoveBufferFromViews(IBuffer buffer)
    {
        RemoveTab(buffer);
        RemoveTreeNode(buffer);
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
            Content = new Border(),
        };

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
    // Tree management (tree mode)
    // ---------------------------------------------------------------------------

    private void OnTreeSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_tree.SelectedItem is TreeViewItem item && item.Tag is IBuffer buf)
            ActivateBuffer(buf);
    }

    private void AddTreeNode(IBuffer buffer)
    {
        var header = MakeTreeHeader(buffer);

        var item = new TreeViewItem
        {
            Header = header,
            Tag    = buffer,
        };

        // Mirror the unread indicator in the tree.
        buffer.MessageAdded += _ =>
        {
            if (_active != buffer)
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    header.Foreground = new SolidColorBrush(
                        ThemeManager.ParseHex(_theme.Theme.Chrome.TabUnreadForeground)));
        };

        _nodesByBuffer[buffer.Id] = item;

        if (string.IsNullOrEmpty(buffer.Server))
        {
            // Global buffers (NetworkStatus, Highlights) are root-level tree nodes.
            _tree.Items.Add(item);
            return;
        }

        // Server-specific buffers nest under a server group node.
        if (!_serverNodes.TryGetValue(buffer.Server, out var serverNode))
        {
            serverNode = new TreeViewItem
            {
                Header     = buffer.Server,
                Tag        = null, // group node, not a selectable buffer
                IsExpanded = true,
            };
            _serverNodes[buffer.Server] = serverNode;
            _tree.Items.Add(serverNode);
        }

        serverNode.Items.Add(item);
    }

    private void RemoveTreeNode(IBuffer buffer)
    {
        if (!_nodesByBuffer.TryGetValue(buffer.Id, out var item))
            return;

        _nodesByBuffer.Remove(buffer.Id);

        if (string.IsNullOrEmpty(buffer.Server))
        {
            _tree.Items.Remove(item);
            return;
        }

        if (_serverNodes.TryGetValue(buffer.Server, out var serverNode))
        {
            serverNode.Items.Remove(item);

            // Remove the server group node when the last child is gone.
            if (serverNode.Items.Count == 0)
            {
                _serverNodes.Remove(buffer.Server);
                _tree.Items.Remove(serverNode);
            }
        }
    }

    // Selects the tree node for the given buffer, clearing all others.
    private void SelectTreeNode(IBuffer buffer)
    {
        foreach (var node in _nodesByBuffer.Values)
            node.IsSelected = false;

        if (_nodesByBuffer.TryGetValue(buffer.Id, out var item))
            item.IsSelected = true;
    }

    // Builds the TextBlock header for a tree leaf node.
    private TextBlock MakeTreeHeader(IBuffer buffer)
    {
        // Prefix indicates the buffer kind for quick visual scanning.
        string prefix = buffer.Kind switch
        {
            BufferKind.Channel  => "#",
            BufferKind.Query    => "~",
            BufferKind.DccChat  => "D",
            BufferKind.Notices  => "!",
            BufferKind.RawLog   => "%",
            _                   => " ",
        };

        // ServerStatus uses just the server name (already the title).
        // NetworkStatus and Highlights use their own title without a prefix.
        string text = (buffer.Kind == BufferKind.ServerStatus ||
                       buffer.Kind == BufferKind.NetworkStatus ||
                       buffer.Kind == BufferKind.Highlights)
            ? buffer.Title
            : $"{prefix} {buffer.Title}";

        return new TextBlock
        {
            Text       = text,
            Foreground = new SolidColorBrush(ThemeManager.ParseHex(_theme.Theme.Chrome.TabForeground)),
            Padding    = new Thickness(4, 2),
        };
    }

    // ---------------------------------------------------------------------------
    // Buffer activation (shared by both modes)
    // ---------------------------------------------------------------------------

    private void ActivateBuffer(IBuffer buffer)
    {
        _active = buffer;
        buffer.MarkRead();

        // Reset tab foreground.
        if (FindTab(buffer) is { Header: TextBlock tabHeader })
            tabHeader.Foreground = new SolidColorBrush(
                ThemeManager.ParseHex(_theme.Theme.Chrome.TabActiveForeground));

        // Reset tree node foreground and mark it selected.
        if (_nodesByBuffer.TryGetValue(buffer.Id, out var treeItem))
        {
            if (treeItem.Header is TextBlock treeHeader)
                treeHeader.Foreground = new SolidColorBrush(
                    ThemeManager.ParseHex(_theme.Theme.Chrome.TabActiveForeground));
            SelectTreeNode(buffer);
        }

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

    private void OnCommandSubmitted(string cmd) => CommandIssued?.Invoke(cmd, _active);
    private void OnMessageSubmitted(string msg) => MessageIssued?.Invoke(msg, _active);

    // ---------------------------------------------------------------------------
    // Nicklist context menu actions
    // ---------------------------------------------------------------------------

    private void OnNickAction(string nick, string action)
    {
        if (_active is null) return;
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

        if (cmd.Length > 0)
            CommandIssued?.Invoke(cmd, _active);
    }

    // ---------------------------------------------------------------------------
    // Public helpers
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Wires a spell check service into the input box.
    /// Has no effect when <see cref="DataJack.Platform.Spell.ISpellCheckService.IsAvailable"/>
    /// is <see langword="false"/>.
    /// </summary>
    public void SetSpellCheckService(DataJack.Platform.Spell.ISpellCheckService service) =>
        _inputBox.SetSpellCheckService(service);

    /// <summary>Update the status bar text (e.g. connection state, lag indicator).</summary>
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
