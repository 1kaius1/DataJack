// SPDX-License-Identifier: GPL-3.0-or-later
using DataJack.Core.Events;
using DataJack.Core.Irc;

namespace DataJack.Core.Caps.Handlers;

/// <summary>
/// Accumulates IRCv3 batch-tagged messages and emits a single <see cref="BatchReceived"/>
/// event when the server closes the batch with <c>BATCH -id</c>.
///
/// <para>
/// Design note: this handler subscribes to <see cref="RawLineReceived"/> and parses lines
/// directly (using <see cref="IRCParser.ParseMessage"/>) rather than subscribing to typed
/// events from the main parser. This avoids an ordering hazard: the main parser routes
/// lines through its own internal channel, so typed events for batch-tagged messages would
/// arrive on the event bus AFTER the <c>BATCH -</c> raw line has already been seen by the
/// batch handler, causing the accumulated list to be empty. By operating on raw lines, the
/// batch handler accumulates events in strict TCP arrival order.
/// </para>
///
/// <para>
/// Phase 3 limitation: batch-tagged messages are also published to the event bus individually
/// by the main <see cref="IRCParser"/>. Consumers that do not want per-message display inside
/// a batch should filter on <c>Tags["batch"]</c> and suppress rendering until
/// <see cref="BatchReceived"/> arrives. Full suppression (preventing individual events from
/// reaching the UI) requires coordination with the parser and is deferred to Phase 4.
/// </para>
///
/// <para>
/// Phase 3 coverage: PRIVMSG (plain and ACTION), NOTICE. Other message types within a batch
/// are included as-is or omitted; Phase 4 can extend <c>CreateTypedEvent</c>.
/// </para>
/// </summary>
public sealed class BatchHandler
{
    private sealed class BatchAccumulator
    {
        public required string BatchType;
        public readonly List<object> Events = [];
    }

    private readonly string _serverId;
    private readonly EventDispatcher _dispatcher;

    // Active batches keyed by batch reference label (case-sensitive per spec).
    private readonly Dictionary<string, BatchAccumulator> _batches =
        new(StringComparer.Ordinal);

    public BatchHandler(string serverId, EventDispatcher dispatcher)
    {
        _serverId   = serverId;
        _dispatcher = dispatcher;
        dispatcher.Subscribe<RawLineReceived>(OnRawLineReceived);
    }

    private void OnRawLineReceived(RawLineReceived evt)
    {
        if (!evt.Server.Equals(_serverId, StringComparison.Ordinal)) return;

        var msg = IRCParser.ParseMessage(evt.Line);
        if (msg is null) return;

        if (msg.Value.Command == "BATCH")
        {
            HandleBatchCommand(msg.Value);
            return;
        }

        // If the message carries a batch tag that references an active batch, accumulate it.
        if (msg.Value.Tags?.TryGetValue("batch", out var batchId) == true
            && batchId is not null
            && _batches.TryGetValue(batchId, out var acc))
        {
            var typed = CreateTypedEvent(msg.Value);
            if (typed is not null) acc.Events.Add(typed);
        }
    }

    private void HandleBatchCommand(IrcMessage msg)
    {
        if (msg.Params.Length < 1) return;

        string first = msg.Param(0);

        if (first.StartsWith('+') && first.Length > 1)
        {
            // BATCH + id type [params...]
            string id   = first[1..];
            string type = msg.Param(1);
            _batches[id] = new BatchAccumulator { BatchType = type };
        }
        else if (first.StartsWith('-') && first.Length > 1)
        {
            // BATCH - id: close the batch and emit BatchReceived
            string id = first[1..];
            if (!_batches.TryGetValue(id, out var acc)) return;
            _batches.Remove(id);

            _ = _dispatcher.PublishAsync(
                new BatchReceived(_serverId, acc.BatchType, id, acc.Events),
                EventPriority.Normal).AsTask();
        }
    }

    // Converts a parsed IrcMessage into the appropriate typed event object.
    // Returns null for message types not handled in Phase 3.
    private object? CreateTypedEvent(IrcMessage msg) =>
        msg.Command switch
        {
            "PRIVMSG" => CreatePrivmsgEvent(msg),
            "NOTICE"  => CreateNoticeEvent(msg),
            _         => null,
        };

    private object? CreatePrivmsgEvent(IrcMessage msg)
    {
        if (msg.Nick is null || msg.Params.Length < 2) return null;

        string target = msg.Param(0);
        string text   = msg.Param(1);

        // CTCP ACTION
        if (text.StartsWith('') && text.EndsWith('') && text.Length >= 2)
        {
            var body   = text[1..^1];
            int sp     = body.IndexOf(' ');
            string cmd = sp < 0 ? body : body[..sp];
            if (string.Equals(cmd, "ACTION", StringComparison.OrdinalIgnoreCase))
            {
                string action = sp < 0 ? string.Empty : body[(sp + 1)..];
                return new ActionReceived(_serverId, target, msg.Nick, action, msg.Tags);
            }
            return null; // other CTCP within a batch: omit
        }

        return new MessageReceived(_serverId, target, msg.Nick, text, msg.Tags, IsSelf: false);
    }

    private object? CreateNoticeEvent(IrcMessage msg)
    {
        if (msg.Params.Length < 2) return null;
        string target = msg.Param(0);
        string text   = msg.Param(1);

        if (msg.Nick is null)
            return new ServerNoticeReceived(_serverId, text);

        return new NoticeReceived(_serverId, target, msg.Nick, text, msg.Tags);
    }
}
