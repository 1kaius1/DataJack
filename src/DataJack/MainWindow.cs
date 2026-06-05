// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;
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
    private readonly ConcurrentDictionary<string, ServerSession> _sessions = new();
    private AliasManager                 _aliasManager = new();
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

            // Initialise alias manager from persisted config and wire persistence back.
            _aliasManager = new AliasManager(_configLoader.Config.Aliases);
            _aliasManager.AliasesChanged += () =>
            {
                var updated = _configLoader.Config with { Aliases = new Dictionary<string, string>(_aliasManager.GetAll()) };
                _ = _configLoader.UpdateAsync(updated);
            };

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

    private async void OnCommandIssued(string command, IBuffer? sourceBuffer)
    {
        try
        {
            string trimmed = command.TrimStart('/');

            // Apply alias expansion before parsing the verb. TryExpand returns the
            // expanded command with a leading '/'; single-pass substitution only.
            string? expanded = _aliasManager.TryExpand(trimmed);
            if (expanded is not null)
                trimmed = expanded.TrimStart('/');

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

                case "ALIAS":
                    var ar = _aliasManager.HandleAlias(args);
                    PrintToBuffer(sourceBuffer,
                        ar.Success ? MessageKind.Info : MessageKind.Error, ar.Message);
                    break;

                case "UNALIAS":
                    var ur = _aliasManager.HandleUnalias(args);
                    PrintToBuffer(sourceBuffer,
                        ur.Success ? MessageKind.Info : MessageKind.Error, ur.Message);
                    break;

                default:
                    if (!_sessions.TryGetValue(server, out var session))
                    {
                        PrintToBuffer(sourceBuffer, MessageKind.Error, "Not connected.");
                        break;
                    }
                    await DispatchIrcCommandAsync(session, verb, args, sourceBuffer);
                    break;
            }
        }
        catch (ArgumentException ex)
        {
            PrintToBuffer(sourceBuffer, MessageKind.Error, ex.Message);
        }
    }

    private async Task DispatchIrcCommandAsync(
        ServerSession session,
        string verb,
        string args,
        IBuffer? sourceBuffer)
    {
        string? defaultChannel = (sourceBuffer as ChannelBuffer)?.Channel;
        string? defaultTarget  = sourceBuffer switch
        {
            ChannelBuffer ch => ch.Channel,
            QueryBuffer   q  => q.TargetNick,
            _                => null,
        };

        // Splits "word tail" into (word, tail); tail is empty string when absent.
        static (string First, string Tail) Split2(string s)
        {
            int i = s.IndexOf(' ');
            return i < 0 ? (s, string.Empty) : (s[..i], s[(i + 1)..].TrimStart());
        }

        // Splits "word1 word2 tail" into (word1, word2, tail).
        static (string First, string Second, string Tail) Split3(string s)
        {
            var (first, a) = Split2(s);
            var (second, tail) = Split2(a);
            return (first, second, tail);
        }

        switch (verb.ToUpperInvariant())
        {
            case "JOIN":
            {
                var (channel, key) = Split2(args);
                await session.Router.JoinAsync(channel, key.Length > 0 ? key : null);
                break;
            }

            case "PART":
            {
                string partChannel;
                string? partReason;
                if (args.Length > 0 && IsChannelPrefix(args[0]))
                {
                    var (ch, reason) = Split2(args);
                    partChannel = ch;
                    partReason  = reason.Length > 0 ? reason : null;
                }
                else
                {
                    partChannel = defaultChannel ?? string.Empty;
                    partReason  = args.Length > 0 ? args : null;
                }
                if (partChannel.Length == 0)
                {
                    PrintToBuffer(sourceBuffer, MessageKind.Error,
                        "Usage: /part [#channel] [reason]");
                    break;
                }
                await session.Router.PartAsync(partChannel, partReason);
                break;
            }

            case "MSG":
            {
                var (target, text) = Split2(args);
                await session.Router.MsgAsync(target, text);
                break;
            }

            case "NOTICE":
            {
                var (target, text) = Split2(args);
                await session.Router.NoticeAsync(target, text);
                break;
            }

            case "NICK":
                await session.Router.NickAsync(args);
                break;

            case "QUIT":
                // Send QUIT first so the server sees the reason, then remove and dispose
                // the session immediately — this cancels the ReconnectController CTS before
                // ConnectionClosed fires, so voluntary /quit never triggers a reconnect.
                await session.Router.QuitAsync(args.Length > 0 ? args : null);
                if (_sessions.TryRemove(sourceBuffer?.Server ?? string.Empty, out var quitSession))
                    await quitSession.DisposeAsync();
                break;

            case "RAW":
                await session.Router.RawAsync(args);
                break;

            case "LIST":
                await session.Router.ListAsync(args.Length > 0 ? args : null);
                break;

            case "WHOIS":
                await session.Router.WhoisAsync(args);
                break;

            case "WHO":
                await session.Router.WhoAsync(args.Length > 0 ? args : null);
                break;

            case "AWAY":
                await session.Router.AwayAsync(args.Length > 0 ? args : null);
                break;

            case "BACK":
                await session.Router.BackAsync();
                break;

            case "QUERY":
            {
                var (nick, message) = Split2(args);
                await session.Router.QueryAsync(nick, message.Length > 0 ? message : null);
                break;
            }

            case "ME":
                if (defaultTarget is null)
                {
                    PrintToBuffer(sourceBuffer, MessageKind.Error,
                        "This command can only be used in a channel or query.");
                    break;
                }
                await session.Router.MeAsync(defaultTarget, args);
                break;

            case "PING":
                await session.Router.PingAsync(args);
                break;

            case "CTCP":
            {
                var (nick, rest) = Split2(args);
                var (ctcpCmd, ctcpParams) = Split2(rest);
                await session.Router.CtcpAsync(nick, ctcpCmd,
                    ctcpParams.Length > 0 ? ctcpParams : null);
                break;
            }

            case "NAMES":
            {
                string? namesChannel = args.Length > 0 ? args : defaultChannel;
                await session.Router.NamesAsync(namesChannel);
                break;
            }

            case "TOPIC":
                if (defaultChannel is null)
                {
                    PrintToBuffer(sourceBuffer, MessageKind.Error,
                        "This command can only be used in a channel.");
                    break;
                }
                await session.Router.TopicAsync(defaultChannel,
                    args.Length > 0 ? args : null);
                break;

            case "INVITE":
            {
                var (nick, chanArg) = Split2(args);
                string inviteChannel = chanArg.Length > 0
                    ? chanArg
                    : (defaultChannel ?? string.Empty);
                if (inviteChannel.Length == 0)
                {
                    PrintToBuffer(sourceBuffer, MessageKind.Error,
                        "Usage: /invite <nick> [#channel]");
                    break;
                }
                await session.Router.InviteAsync(nick, inviteChannel);
                break;
            }

            case "KICK":
            {
                if (defaultChannel is null)
                {
                    PrintToBuffer(sourceBuffer, MessageKind.Error,
                        "This command can only be used in a channel.");
                    break;
                }
                var (nick, reason) = Split2(args);
                await session.Router.KickAsync(defaultChannel, nick,
                    reason.Length > 0 ? reason : null);
                break;
            }

            case "BAN":
                if (defaultChannel is null)
                {
                    PrintToBuffer(sourceBuffer, MessageKind.Error,
                        "This command can only be used in a channel.");
                    break;
                }
                await session.Router.BanAsync(defaultChannel, args);
                break;

            case "UNBAN":
                if (defaultChannel is null)
                {
                    PrintToBuffer(sourceBuffer, MessageKind.Error,
                        "This command can only be used in a channel.");
                    break;
                }
                await session.Router.UnbanAsync(defaultChannel, args);
                break;

            case "KICKBAN":
            {
                if (defaultChannel is null)
                {
                    PrintToBuffer(sourceBuffer, MessageKind.Error,
                        "This command can only be used in a channel.");
                    break;
                }
                var (nick, mask, reason) = Split3(args);
                await session.Router.KickBanAsync(defaultChannel, nick, mask,
                    reason.Length > 0 ? reason : null);
                break;
            }

            case "OP":
                if (defaultChannel is null)
                {
                    PrintToBuffer(sourceBuffer, MessageKind.Error,
                        "This command can only be used in a channel.");
                    break;
                }
                await session.Router.OpAsync(defaultChannel, args);
                break;

            case "DEOP":
                if (defaultChannel is null)
                {
                    PrintToBuffer(sourceBuffer, MessageKind.Error,
                        "This command can only be used in a channel.");
                    break;
                }
                await session.Router.DeopAsync(defaultChannel, args);
                break;

            case "VOICE":
                if (defaultChannel is null)
                {
                    PrintToBuffer(sourceBuffer, MessageKind.Error,
                        "This command can only be used in a channel.");
                    break;
                }
                await session.Router.VoiceAsync(defaultChannel, args);
                break;

            case "DEVOICE":
                if (defaultChannel is null)
                {
                    PrintToBuffer(sourceBuffer, MessageKind.Error,
                        "This command can only be used in a channel.");
                    break;
                }
                await session.Router.DevoiceAsync(defaultChannel, args);
                break;

            case "MODE":
            {
                var (target, rest) = Split2(args);
                var (modeStr, paramStr) = Split2(rest);
                var modeParams = paramStr.Length > 0
                    ? (IReadOnlyList<string>)paramStr.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    : null;
                await session.Router.ModeAsync(target, modeStr, modeParams);
                break;
            }

            default:
                PrintToBuffer(sourceBuffer, MessageKind.Error, $"Unknown command: /{verb}");
                break;
        }
    }

    private async void OnMessageIssued(string message, IBuffer? sourceBuffer)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(message)) return;

            string server = sourceBuffer?.Server ?? string.Empty;
            if (!_sessions.TryGetValue(server, out var session)) return;

            string? target = sourceBuffer switch
            {
                ChannelBuffer ch => ch.Channel,
                QueryBuffer   q  => q.TargetNick,
                _                => null,
            };
            if (target is null) return;

            await session.Router.MsgAsync(target, message);
        }
        catch (ArgumentException ex)
        {
            PrintToBuffer(sourceBuffer, MessageKind.Error, ex.Message);
        }
    }

    // ---------------------------------------------------------------------------
    // Auto-away / idle
    // ---------------------------------------------------------------------------

    private void OnIdleTripped()
    {
        string msg = _configLoader.Config.Away.AwayMessage;
        foreach (var (_, session) in _sessions)
            _ = session.Router.AwayAsync(msg);
    }

    private void OnActivityResumed()
    {
        foreach (var (_, session) in _sessions)
            _ = session.Router.BackAsync();
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

    private async Task ConnectToServerAsync(ServerEntry entry)
    {
        // Dispose any existing session for this server name before reconnecting.
        if (_sessions.TryRemove(entry.NetworkName, out var old))
            await old.DisposeAsync().ConfigureAwait(false);

        try
        {
            var session = await ServerSession.ConnectAsync(
                entry, _configLoader.Config, _dispatcher).ConfigureAwait(false);
            _sessions[entry.NetworkName] = session;
        }
        catch (Exception ex)
        {
            // ConnectionFailed is already published by IRCConnection; this catch covers
            // any exception that escapes before the connection attempt starts.
            var netBuf = _bufferManager.Buffers.OfType<NetworkStatusBuffer>().FirstOrDefault();
            netBuf?.AddMessage(new MessageEntry(
                DateTimeOffset.Now, null, MessageKind.Error,
                $"Connect error ({entry.NetworkName}): {ex.Message}", null));
        }
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
    // Helpers
    // ---------------------------------------------------------------------------

    private void PrintToBuffer(IBuffer? buf, MessageKind kind, string text)
    {
        var target = buf
            ?? _bufferManager.Buffers.OfType<NetworkStatusBuffer>().FirstOrDefault();
        target?.AddMessage(new MessageEntry(DateTimeOffset.Now, null, kind, text, null));
    }

    private static bool IsChannelPrefix(char c) => c is '#' or '&' or '+' or '!';

    // ---------------------------------------------------------------------------
    // Cleanup
    // ---------------------------------------------------------------------------

    protected override void OnClosed(EventArgs e)
    {
        foreach (var (_, session) in _sessions)
            _ = session.DisposeAsync().AsTask();
        _sessions.Clear();

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
