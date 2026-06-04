// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Controls;
using DataJack.Core.Events;
using DataJack.Core.Irc;
using DataJack.Core.Storage.Config;
using DataJack.Platform.Spell;
using DataJack.Ui.Buffers;
using DataJack.Ui.Dialogs;
using DataJack.Ui.Layout;
using DataJack.Ui.Themes;

namespace DataJack;

/// <summary>
/// Application main window. Owns the top-level component graph:
/// EventDispatcher -> BufferManager -> LayoutManager.
/// Config and theme are loaded at construction time.
/// </summary>
internal sealed class MainWindow : Window
{
    private readonly EventDispatcher     _dispatcher;
    private readonly ConfigLoader        _configLoader;
    private readonly ThemeManager        _themeManager;
    private readonly BufferManager       _bufferManager;
    private readonly LayoutManager       _layout;
    private readonly ISpellCheckService  _spellService;
    private IdleMonitor?                 _idleMonitor;
    private DebugLogger?                 _debugLogger;

    public MainWindow()
    {
        Title  = "DataJack";
        Width  = 1024;
        Height = 768;

        // -----------------------------------------------------------------------
        // Bootstrap order: config -> theme -> dispatcher -> buffers -> layout
        // -----------------------------------------------------------------------

        _configLoader  = new ConfigLoader();
        _themeManager  = new ThemeManager();
        _dispatcher    = new EventDispatcher();
        _bufferManager = new BufferManager(_dispatcher);
        // LayoutMode is loaded from config during BootstrapAsync; pass the default here
        // so the layout is usable before config loads. BootstrapAsync calls SetLayoutMode
        // again once the real preference is known.
        _layout = new LayoutManager(_bufferManager, _themeManager);

        // Spell check service: created once; wired into the input box after config loads.
        _spellService = SpellCheckServiceFactory.Create();

        Content = _layout;

        // Wire layout actions to IRC command routing.
        _layout.CommandIssued += OnCommandIssued;
        _layout.MessageIssued += OnMessageIssued;

        // Hot-reload the UI when the theme changes.
        _themeManager.ThemeChanged += _ =>
            Avalonia.Threading.Dispatcher.UIThread.Post(ApplyTheme);

        // Load config and theme asynchronously after the window is shown.
        Opened += async (_, _) => await BootstrapAsync();
    }

    // ---------------------------------------------------------------------------
    // Bootstrap
    // ---------------------------------------------------------------------------

    private async Task BootstrapAsync()
    {
        try
        {
            // No ConfigureAwait(false) here: all post-await work touches UI objects
            // (Window.Background, LayoutManager controls) and must stay on the UI thread.
            await _configLoader.LoadAsync();

            _themeManager.Load(_configLoader.Config.Appearance.ThemeName);
            _layout.SetLayoutMode(_configLoader.Config.Appearance.LayoutMode);
            _layout.SetSpellCheckService(_spellService);

            // Start debug logger if a path is configured.
            string? debugPath = _configLoader.Config.Advanced.DebugLogPath;
            if (!string.IsNullOrWhiteSpace(debugPath))
                _debugLogger = new DebugLogger(debugPath, _dispatcher);

            // Start the idle monitor if auto-away is configured.
            var awayCfg = _configLoader.Config.Away;
            if (awayCfg.AutoAwayEnabled && awayCfg.AutoAwayDelaySec > 0)
            {
                _idleMonitor = new IdleMonitor(awayCfg.AutoAwayDelaySec);
                _layout.InputActivity        += _idleMonitor.NotifyActivity;
                _idleMonitor.IdleTripped     += OnIdleTripped;
                _idleMonitor.ActivityResumed += OnActivityResumed;
            }

            _dispatcher.Start();

            ApplyTheme();
            PostStartupMessage();

            foreach (var server in _configLoader.Config.Servers.Where(s => s.AutoConnect))
                _ = ConnectToServerAsync(server);
        }
        catch (Exception ex)
        {
            // Surface bootstrap failures in the NetworkStatus buffer rather than
            // silently swallowing them via the async void event handler.
            var netBuf = _bufferManager.Buffers
                .OfType<NetworkStatusBuffer>().FirstOrDefault();
            netBuf?.AddMessage(new MessageEntry(
                DateTimeOffset.Now, null, MessageKind.Error,
                $"Startup error: {ex.Message}", null));
        }
    }

    private void PostStartupMessage()
    {
        var netBuf = _bufferManager.Buffers.OfType<NetworkStatusBuffer>().FirstOrDefault();
        netBuf?.AddMessage(new MessageEntry(
            DateTimeOffset.Now, null, MessageKind.Info,
            "DataJack started. Use /serverlist to add a server or /connect <host> to connect.",
            null));
    }

    private void ApplyTheme()
    {
        var chrome = _themeManager.Theme.Chrome;
        Background = new Avalonia.Media.SolidColorBrush(
            ThemeManager.ParseHex(chrome.Background));
    }

    // ---------------------------------------------------------------------------
    // IRC command routing
    // ---------------------------------------------------------------------------

    private void OnCommandIssued(string command, IBuffer? sourceBuffer)
    {
        // Parse the command verb and route to the appropriate action.
        string trimmed = command.TrimStart('/');
        int spaceIdx = trimmed.IndexOf(' ');
        string verb = spaceIdx < 0 ? trimmed : trimmed[..spaceIdx];
        string args = spaceIdx < 0 ? string.Empty : trimmed[(spaceIdx + 1)..].TrimStart();

        string server = sourceBuffer?.Server ?? string.Empty;

        switch (verb.ToUpperInvariant())
        {
            case "SERVER":
            case "CONNECT":
                HandleConnectCommand(args);
                break;

            case "SERVERLIST":
                OpenServerList();
                break;

            case "LAYOUT":
                HandleLayoutCommand(args);
                break;

            default:
                // TODO Phase 3: route through IRCCommandRouter once connections are managed.
                _ = server;
                _ = args;
                break;
        }
    }

    private void OnMessageIssued(string message, IBuffer? sourceBuffer)
    {
        // TODO Phase 3: send message via FloodController -> IRCConnection.
        _ = message;
        _ = sourceBuffer;
    }

    // ---------------------------------------------------------------------------
    // Auto-away / idle
    // ---------------------------------------------------------------------------

    private void OnIdleTripped()
    {
        // TODO Phase 3: send AWAY <message> on all connected servers once connection
        // management is wired up. The away message is _configLoader.Config.Away.AwayMessage.
    }

    private void OnActivityResumed()
    {
        // TODO Phase 3: send bare AWAY (back) on all connected servers once connection
        // management is wired up.
    }

    // ---------------------------------------------------------------------------
    // Connection management
    // ---------------------------------------------------------------------------

    private void HandleLayoutCommand(string args)
    {
        string target = args.Trim().ToLowerInvariant();
        string newMode = target switch
        {
            "toggle"            => _layout.CurrentLayoutMode == "tabs" ? "tree" : "tabs",
            "tabs" or "tab"     => "tabs",
            "tree"              => "tree",
            _                   => string.Empty,
        };

        if (string.IsNullOrEmpty(newMode))
            return;

        _layout.SetLayoutMode(newMode);

        // Persist the preference to config (fire-and-forget; non-critical).
        var updated = _configLoader.Config with
        {
            Appearance = _configLoader.Config.Appearance with { LayoutMode = newMode },
        };
        _ = _configLoader.UpdateAsync(updated);
    }

    private void HandleConnectCommand(string args)
    {
        // /connect host[:port] or /server host[:port]
        if (string.IsNullOrWhiteSpace(args)) { OpenServerList(); return; }

        string host = args;
        int port = 6697;

        int colon = args.LastIndexOf(':');
        if (colon > 0 && int.TryParse(args[(colon + 1)..], out int p))
        {
            host = args[..colon];
            port = p;
        }

        var entry = ServerEntry.New(host, host) with { Port = port };
        _ = ConnectToServerAsync(entry);
    }

    private Task ConnectToServerAsync(ServerEntry entry)
    {
        // TODO Phase 3: instantiate IRCConnection, NetworkProvider, etc. and connect.
        _bufferManager.GetType(); // reference to suppress unused warning
        return Task.CompletedTask;
    }

    private void OpenServerList()
    {
        var dialog = new ServerListDialog(_configLoader, _themeManager);
        dialog.ConnectRequested += entry =>
        {
            if (entry is not null) _ = ConnectToServerAsync(entry);
        };
        dialog.Closed += async (_, _) =>
        {
            if (dialog.Result is null) return;
            var updated = _configLoader.Config with { Servers = dialog.Result };
            await _configLoader.UpdateAsync(updated).ConfigureAwait(false);
        };
        dialog.Show();
    }

    // ---------------------------------------------------------------------------
    // Cleanup
    // ---------------------------------------------------------------------------

    protected override void OnClosed(EventArgs e)
    {
        _debugLogger?.Dispose();
        _idleMonitor?.Dispose();
        _layout.Dispose();
        _bufferManager.Dispose();
        _themeManager.Dispose();
        _spellService.Dispose();
        _ = _dispatcher.DisposeAsync().AsTask();
        base.OnClosed(e);
    }
}
