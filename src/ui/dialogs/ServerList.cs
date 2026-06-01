// SPDX-License-Identifier: GPL-3.0-or-later
// ServerListDialog: basic address book for adding, editing, and deleting server entries.
// Auto-connect entries are highlighted. See ARCHITECTURE.md §6.4 ServerListDialog.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using DataJack.Core.Storage.Config;
using DataJack.Ui.Themes;

namespace DataJack.Ui.Dialogs;

/// <summary>
/// Modal dialog that lets the user manage the server address book.
/// Returns the updated list via <see cref="Result"/> after the user clicks Save.
/// </summary>
public sealed class ServerListDialog : Window
{
    private readonly ConfigLoader _loader;
    private readonly ThemeManager _theme;
    private readonly ListBox      _listBox;
    private readonly List<ServerEntry> _entries;

    // Edit-panel controls
    private readonly TextBox _txtNetwork;
    private readonly TextBox _txtAddress;
    private readonly TextBox _txtPort;
    private readonly CheckBox _chkTls;
    private readonly TextBox _txtPassword;
    private readonly TextBox _txtNick;
    private readonly TextBox _txtAutoJoin;
    private readonly CheckBox _chkAutoConnect;

    private int _selectedIndex = -1;

    /// <summary>The saved server list after the dialog closes. Null if cancelled.</summary>
    public List<ServerEntry>? Result { get; private set; }

    public ServerListDialog(ConfigLoader loader, ThemeManager theme)
    {
        _loader  = loader;
        _theme   = theme;
        _entries = new List<ServerEntry>(loader.Config.Servers);

        Title  = "Server List";
        Width  = 700;
        Height = 480;
        CanResize = true;

        // -----------------------------------------------------------------------
        // Build layout: list on the left, edit form on the right
        // -----------------------------------------------------------------------

        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition(220, GridUnitType.Pixel));
        root.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        root.RowDefinitions.Add(new RowDefinition(GridLength.Star));
        root.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        // ---- Left: server list + add/remove buttons ----
        var leftPanel = new DockPanel();
        Grid.SetColumn(leftPanel, 0);
        Grid.SetRow(leftPanel, 0);

        _listBox = new ListBox { Margin = new Thickness(4) };
        _listBox.SelectionChanged += OnSelectionChanged;
        DockPanel.SetDock(_listBox, Dock.Top);
        leftPanel.Children.Add(_listBox);
        root.Children.Add(leftPanel);

        var listButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin      = new Thickness(4, 2),
            Spacing     = 4,
        };
        var btnAdd = new Button { Content = "Add" };
        btnAdd.Click += (_, _) => AddServer();
        var btnRemove = new Button { Content = "Remove" };
        btnRemove.Click += (_, _) => RemoveSelected();
        listButtons.Children.Add(btnAdd);
        listButtons.Children.Add(btnRemove);
        DockPanel.SetDock(listButtons, Dock.Bottom);
        leftPanel.Children.Add(listButtons);

        // ---- Right: edit form ----
        var form = new StackPanel { Margin = new Thickness(8), Spacing = 6 };
        Grid.SetColumn(form, 1);
        Grid.SetRow(form, 0);

        _txtNetwork   = AddFormRow(form, "Network name:", new TextBox());
        _txtAddress   = AddFormRow(form, "Server address:", new TextBox());
        _txtPort      = AddFormRow(form, "Port:", new TextBox { Text = "6697" });
        _chkTls       = AddFormCheck(form, "Use TLS", true);
        _txtPassword  = AddFormRow(form, "Password:", new TextBox { PasswordChar = '*' });
        _txtNick      = AddFormRow(form, "Nick (override):", new TextBox());
        _txtAutoJoin  = AddFormRow(form, "Auto-join channels (space-separated):", new TextBox());
        _chkAutoConnect = AddFormCheck(form, "Auto-connect on launch", false);

        root.Children.Add(form);

        // ---- Bottom: Save / Cancel buttons ----
        var bottomBar = new StackPanel
        {
            Orientation       = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin            = new Thickness(8),
            Spacing           = 6,
        };
        Grid.SetColumnSpan(bottomBar, 2);
        Grid.SetRow(bottomBar, 1);

        var btnConnect = new Button { Content = "Connect" };
        btnConnect.Click += (_, _) => ConnectAndClose();
        var btnSave = new Button { Content = "Save" };
        btnSave.Click += (_, _) => SaveAndClose();
        var btnCancel = new Button { Content = "Cancel" };
        btnCancel.Click += (_, _) => Close();

        bottomBar.Children.Add(btnConnect);
        bottomBar.Children.Add(btnSave);
        bottomBar.Children.Add(btnCancel);
        root.Children.Add(bottomBar);

        Content = root;

        RefreshList();
    }

    // ---------------------------------------------------------------------------
    // Form helpers
    // ---------------------------------------------------------------------------

    private static T AddFormRow<T>(StackPanel form, string label, T control) where T : Control
    {
        var row = new DockPanel { Margin = new Thickness(0, 2) };
        var lbl = new TextBlock
        {
            Text  = label,
            Width = 180,
            VerticalAlignment = VerticalAlignment.Center,
        };
        DockPanel.SetDock(lbl, Dock.Left);
        row.Children.Add(lbl);
        row.Children.Add(control);
        form.Children.Add(row);
        return control;
    }

    private static CheckBox AddFormCheck(StackPanel form, string label, bool defaultValue)
    {
        var cb = new CheckBox { Content = label, IsChecked = defaultValue };
        form.Children.Add(cb);
        return cb;
    }

    // ---------------------------------------------------------------------------
    // List management
    // ---------------------------------------------------------------------------

    private void RefreshList()
    {
        _listBox.Items.Clear();
        foreach (var e in _entries)
        {
            var item = new ListBoxItem
            {
                Content = e.AutoConnect ? $"* {e.NetworkName}" : e.NetworkName,
                Tag     = e,
            };
            _listBox.Items.Add(item);
        }
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        SaveCurrentEdits();
        _selectedIndex = _listBox.SelectedIndex;
        if (_selectedIndex < 0 || _selectedIndex >= _entries.Count) return;

        var entry = _entries[_selectedIndex];
        _txtNetwork.Text      = entry.NetworkName;
        _txtAddress.Text      = entry.Address;
        _txtPort.Text         = entry.Port.ToString();
        _chkTls.IsChecked     = entry.Tls;
        _txtPassword.Text     = entry.Password ?? string.Empty;
        _txtNick.Text         = entry.Nick ?? string.Empty;
        _txtAutoJoin.Text     = string.Join(" ", entry.AutoJoinChannels);
        _chkAutoConnect.IsChecked = entry.AutoConnect;
    }

    private void SaveCurrentEdits()
    {
        if (_selectedIndex < 0 || _selectedIndex >= _entries.Count) return;

        var existing = _entries[_selectedIndex];
        if (!int.TryParse(_txtPort.Text, out int port)) port = 6697;

        _entries[_selectedIndex] = existing with
        {
            NetworkName      = _txtNetwork.Text?.Trim() ?? existing.NetworkName,
            Address          = _txtAddress.Text?.Trim() ?? existing.Address,
            Port             = port,
            Tls              = _chkTls.IsChecked == true,
            Password         = string.IsNullOrWhiteSpace(_txtPassword.Text) ? null : _txtPassword.Text,
            Nick             = string.IsNullOrWhiteSpace(_txtNick.Text) ? null : _txtNick.Text,
            AutoJoinChannels = (_txtAutoJoin.Text ?? string.Empty)
                                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                                .ToList(),
            AutoConnect      = _chkAutoConnect.IsChecked == true,
        };
    }

    private void AddServer()
    {
        SaveCurrentEdits();
        var entry = ServerEntry.New("New Network", "irc.example.com");
        _entries.Add(entry);
        RefreshList();
        _listBox.SelectedIndex = _entries.Count - 1;
    }

    private void RemoveSelected()
    {
        if (_selectedIndex < 0 || _selectedIndex >= _entries.Count) return;
        _entries.RemoveAt(_selectedIndex);
        _selectedIndex = -1;
        RefreshList();
    }

    // ---------------------------------------------------------------------------
    // Close actions
    // ---------------------------------------------------------------------------

    private void SaveAndClose()
    {
        SaveCurrentEdits();
        Result = new List<ServerEntry>(_entries);
        Close();
    }

    private void ConnectAndClose()
    {
        SaveCurrentEdits();
        Result = new List<ServerEntry>(_entries);
        // Signal the caller that a connection attempt was requested.
        ConnectRequested?.Invoke(_selectedIndex >= 0 ? _entries[_selectedIndex] : null);
        Close();
    }

    /// <summary>
    /// Raised when the user clicks "Connect". Provides the selected server entry.
    /// </summary>
    public event Action<ServerEntry?>? ConnectRequested;
}
