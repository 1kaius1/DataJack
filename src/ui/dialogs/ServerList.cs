// SPDX-License-Identifier: GPL-3.0-or-later
// ServerListDialog: complete server address book with SASL credentials, username/realname
// overrides, connect commands, and JSON import/export. See ARCHITECTURE.md §6.4.

using System.IO;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using DataJack.Core.Storage.Config;
using DataJack.Ui.Themes;

namespace DataJack.Ui.Dialogs;

/// <summary>
/// Modal dialog that lets the user manage the server address book.
/// Returns the updated list via <see cref="Result"/> after the user clicks Save.
/// Provides JSON import and export for transferring entries between installations.
/// </summary>
public sealed class ServerListDialog : Window
{
    private static readonly string[] SaslMechanisms =
        { "None", "SCRAM-SHA-512", "SCRAM-SHA-256", "EXTERNAL", "PLAIN" };

    private readonly ConfigLoader   _loader;
    private readonly List<ServerEntry> _entries;
    private readonly ListBox        _listBox;

    // Edit-panel controls — connection
    private readonly TextBox  _txtNetwork;
    private readonly TextBox  _txtAddress;
    private readonly TextBox  _txtPort;
    private readonly CheckBox _chkTls;
    private readonly TextBox  _txtPassword;

    // Edit-panel controls — identity overrides
    private readonly TextBox _txtNick;
    private readonly TextBox _txtUsername;
    private readonly TextBox _txtRealname;

    // Edit-panel controls — SASL
    private readonly ComboBox _cmbSaslMechanism;
    private readonly TextBox  _txtSaslAccount;
    private readonly TextBox  _txtSaslPassword;

    // Edit-panel controls — behavior
    private readonly TextBox  _txtAutoJoin;
    private readonly TextBox  _txtConnectCommands;
    private readonly CheckBox _chkAutoConnect;

    private int _selectedIndex = -1;

    /// <summary>The saved server list after the dialog closes. Null if cancelled.</summary>
    public List<ServerEntry>? Result { get; private set; }

    /// <summary>
    /// Raised when the user clicks "Connect". Provides the selected server entry,
    /// or null if no entry is selected.
    /// </summary>
    public event Action<ServerEntry?>? ConnectRequested;

    public ServerListDialog(ConfigLoader loader, ThemeManager theme)
    {
        _loader  = loader;
        _entries = new List<ServerEntry>(loader.Config.Servers);

        Title     = "Server List";
        Width     = 730;
        Height    = 580;
        CanResize = true;

        // -----------------------------------------------------------------------
        // Root grid: list panel left | edit panel right / button bar bottom
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

        _listBox = new ListBox { Margin = new Thickness(4) };
        _listBox.SelectionChanged += OnSelectionChanged;
        leftPanel.Children.Add(_listBox);

        root.Children.Add(leftPanel);

        // ---- Right: scrollable edit form ----
        var form = new StackPanel { Margin = new Thickness(8, 4, 8, 4), Spacing = 5 };
        Grid.SetColumn(form, 1);
        Grid.SetRow(form, 0);

        // Connection
        AddSectionLabel(form, "Connection");
        _txtNetwork  = AddFormRow(form, "Network name:",    new TextBox());
        _txtAddress  = AddFormRow(form, "Server address:",  new TextBox());
        _txtPort     = AddFormRow(form, "Port:",            new TextBox { Text = "6697" });
        _chkTls      = AddFormCheck(form, "Use TLS", defaultValue: true);
        _txtPassword = AddFormRow(form, "Server password:", new TextBox { PasswordChar = '*' });

        // Identity overrides
        AddSectionLabel(form, "Identity (override global settings)");
        _txtNick      = AddFormRow(form, "Nick:",      new TextBox());
        _txtUsername  = AddFormRow(form, "Username:",  new TextBox());
        _txtRealname  = AddFormRow(form, "Realname:",  new TextBox());

        // SASL
        AddSectionLabel(form, "SASL Authentication");
        _cmbSaslMechanism = AddFormRow(form, "Mechanism:", new ComboBox
        {
            ItemsSource    = SaslMechanisms,
            SelectedIndex  = 0,
            MinWidth       = 160,
        });
        _txtSaslAccount  = AddFormRow(form, "Account:",       new TextBox());
        _txtSaslPassword = AddFormRow(form, "SASL password:", new TextBox { PasswordChar = '*' });

        // Behavior
        AddSectionLabel(form, "Behavior");
        _txtAutoJoin = AddFormRow(form, "Auto-join (space-separated):", new TextBox());
        _txtConnectCommands = AddFormRow(form, "Connect commands (one per line):", new TextBox
        {
            AcceptsReturn   = true,
            Height          = 56,
            TextWrapping    = TextWrapping.Wrap,
            VerticalContentAlignment = VerticalAlignment.Top,
        });
        _chkAutoConnect = AddFormCheck(form, "Auto-connect on launch", defaultValue: false);

        var scrollForm = new ScrollViewer
        {
            Content             = form,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        };
        Grid.SetColumn(scrollForm, 1);
        Grid.SetRow(scrollForm, 0);
        root.Children.Add(scrollForm);

        // ---- Bottom button bar ----
        var bottomBar = new DockPanel { Margin = new Thickness(8, 4) };
        Grid.SetColumnSpan(bottomBar, 2);
        Grid.SetRow(bottomBar, 1);

        // Import / Export on the left
        var leftButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        var btnImport = new Button { Content = "Import..." };
        btnImport.Click += OnImportClick;
        var btnExport = new Button { Content = "Export..." };
        btnExport.Click += OnExportClick;
        leftButtons.Children.Add(btnImport);
        leftButtons.Children.Add(btnExport);
        DockPanel.SetDock(leftButtons, Dock.Left);
        bottomBar.Children.Add(leftButtons);

        // Connect / Save / Cancel on the right
        var rightButtons = new StackPanel
        {
            Orientation         = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing             = 4,
        };
        var btnConnect = new Button { Content = "Connect" };
        btnConnect.Click += (_, _) => ConnectAndClose();
        var btnSave = new Button { Content = "Save" };
        btnSave.Click += (_, _) => SaveAndClose();
        var btnCancel = new Button { Content = "Cancel" };
        btnCancel.Click += (_, _) => Close();
        rightButtons.Children.Add(btnConnect);
        rightButtons.Children.Add(btnSave);
        rightButtons.Children.Add(btnCancel);
        bottomBar.Children.Add(rightButtons);

        root.Children.Add(bottomBar);
        Content = root;

        RefreshList();
    }

    // ---------------------------------------------------------------------------
    // Form layout helpers
    // ---------------------------------------------------------------------------

    private static void AddSectionLabel(StackPanel form, string text)
    {
        form.Children.Add(new TextBlock
        {
            Text       = text,
            FontWeight = FontWeight.SemiBold,
            Margin     = new Thickness(0, 6, 0, 0),
        });
    }

    private static T AddFormRow<T>(StackPanel form, string label, T control) where T : Control
    {
        var row = new DockPanel { Margin = new Thickness(0, 1) };
        var lbl = new TextBlock
        {
            Text              = label,
            Width             = 200,
            VerticalAlignment = VerticalAlignment.Top,
            Margin            = new Thickness(0, 3, 6, 0),
        };
        DockPanel.SetDock(lbl, Dock.Left);
        row.Children.Add(lbl);
        row.Children.Add(control);
        form.Children.Add(row);
        return control;
    }

    private static CheckBox AddFormCheck(StackPanel form, string label, bool defaultValue)
    {
        var cb = new CheckBox { Content = label, IsChecked = defaultValue, Margin = new Thickness(0, 1) };
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
            _listBox.Items.Add(new ListBoxItem
            {
                Content = e.AutoConnect ? $"* {e.NetworkName}" : e.NetworkName,
                Tag     = e,
            });
        }
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        SaveCurrentEdits();
        _selectedIndex = _listBox.SelectedIndex;
        if (_selectedIndex < 0 || _selectedIndex >= _entries.Count) return;

        LoadEntry(_entries[_selectedIndex]);
    }

    private void LoadEntry(ServerEntry entry)
    {
        _txtNetwork.Text          = entry.NetworkName;
        _txtAddress.Text          = entry.Address;
        _txtPort.Text             = entry.Port.ToString();
        _chkTls.IsChecked         = entry.Tls;
        _txtPassword.Text         = entry.Password ?? string.Empty;
        _txtNick.Text             = entry.Nick      ?? string.Empty;
        _txtUsername.Text         = entry.Username  ?? string.Empty;
        _txtRealname.Text         = entry.Realname  ?? string.Empty;

        if (entry.Sasl is null)
        {
            _cmbSaslMechanism.SelectedIndex = 0; // "None"
            _txtSaslAccount.Text            = string.Empty;
            _txtSaslPassword.Text           = string.Empty;
        }
        else
        {
            int idx = Array.IndexOf(SaslMechanisms, entry.Sasl.Mechanism);
            _cmbSaslMechanism.SelectedIndex = idx < 0 ? 0 : idx;
            _txtSaslAccount.Text            = entry.Sasl.Account;
            _txtSaslPassword.Text           = entry.Sasl.Password;
        }

        _txtAutoJoin.Text          = string.Join(" ", entry.AutoJoinChannels);
        _txtConnectCommands.Text   = string.Join("\n", entry.ConnectCommands);
        _chkAutoConnect.IsChecked  = entry.AutoConnect;
    }

    private void SaveCurrentEdits()
    {
        if (_selectedIndex < 0 || _selectedIndex >= _entries.Count) return;

        if (!int.TryParse(_txtPort.Text, out int port)) port = 6697;

        string? saslMechanism = _cmbSaslMechanism.SelectedItem is string s && s != "None" ? s : null;
        SaslCredentials? sasl = saslMechanism is null ? null : new SaslCredentials(
            Mechanism: saslMechanism,
            Account:   _txtSaslAccount.Text?.Trim() ?? string.Empty,
            Password:  _txtSaslPassword.Text ?? string.Empty);

        var connectCommands = (_txtConnectCommands.Text ?? string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();

        var existing = _entries[_selectedIndex];
        _entries[_selectedIndex] = existing with
        {
            NetworkName      = _txtNetwork.Text?.Trim() ?? existing.NetworkName,
            Address          = _txtAddress.Text?.Trim() ?? existing.Address,
            Port             = port,
            Tls              = _chkTls.IsChecked == true,
            Password         = Blank(_txtPassword.Text),
            Nick             = Blank(_txtNick.Text),
            Username         = Blank(_txtUsername.Text),
            Realname         = Blank(_txtRealname.Text),
            Sasl             = sasl,
            AutoJoinChannels = (_txtAutoJoin.Text ?? string.Empty)
                                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                                .ToList(),
            ConnectCommands  = connectCommands,
            AutoConnect      = _chkAutoConnect.IsChecked == true,
        };
    }

    // Return null for whitespace-only text; strip surrounding whitespace otherwise.
    private static string? Blank(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private void AddServer()
    {
        SaveCurrentEdits();
        _entries.Add(ServerEntry.New("New Network", "irc.example.com"));
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
    // Import / Export
    // ---------------------------------------------------------------------------

    private async void OnExportClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        SaveCurrentEdits();
        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title             = "Export Server List",
                SuggestedFileName = "datajack-servers.json",
                FileTypeChoices   = new[] { new FilePickerFileType("JSON Files") { Patterns = new[] { "*.json" } } },
            });
            if (file is null) return;

            string json = ServerListExport.ExportToJson(_entries);
            await using var stream = await file.OpenWriteAsync();
            await using var writer = new StreamWriter(stream);
            await writer.WriteAsync(json);
        }
        catch (Exception ex)
        {
            await ShowErrorAsync($"Export failed: {ex.Message}");
        }
    }

    private async void OnImportClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title          = "Import Server List",
                AllowMultiple  = false,
                FileTypeFilter = new[] { new FilePickerFileType("JSON Files") { Patterns = new[] { "*.json" } } },
            });
            if (files.Count == 0) return;

            await using var stream = await files[0].OpenReadAsync();
            using var reader = new StreamReader(stream);
            string json = await reader.ReadToEndAsync();

            var imported = ServerListExport.ImportFromJson(json);
            SaveCurrentEdits();
            _entries.AddRange(imported);
            RefreshList();
        }
        catch (JsonException ex)
        {
            await ShowErrorAsync($"Import failed — invalid JSON: {ex.Message}");
        }
        catch (InvalidDataException ex)
        {
            await ShowErrorAsync($"Import failed — {ex.Message}");
        }
        catch (Exception ex)
        {
            await ShowErrorAsync($"Import failed: {ex.Message}");
        }
    }

    private async Task ShowErrorAsync(string message)
    {
        var dlg = new Window
        {
            Title                   = "Error",
            Width                   = 420,
            Height                  = 140,
            WindowStartupLocation   = WindowStartupLocation.CenterOwner,
            CanResize               = false,
        };

        var okBtn = new Button
        {
            Content             = "OK",
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin              = new Thickness(0, 10, 0, 0),
        };
        okBtn.Click += (_, _) => dlg.Close();

        dlg.Content = new StackPanel
        {
            Margin   = new Thickness(16),
            Children =
            {
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                okBtn,
            },
        };

        await dlg.ShowDialog(this);
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
        ConnectRequested?.Invoke(_selectedIndex >= 0 ? _entries[_selectedIndex] : null);
        Close();
    }
}
