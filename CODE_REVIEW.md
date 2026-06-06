# CODE_REVIEW.md

Code review of the HEAD~5...HEAD diff (Phases 1-3: HexChat feature parity).
Findings are ordered critical to low severity. Each entry includes the defect,
a concrete failure scenario, and a remediation plan.

---

## 9. MEDIUM -- ReconnectController event subscription is never unsubscribed

**File:** [src/core/irc/Reconnect.cs](src/core/irc/Reconnect.cs#L63)
**Line:** 63

```csharp
dispatcher.Subscribe<ConnectionClosed>(OnConnectionClosed);  // line 63
```

`DisposeAsync` (line 157) cancels the CTS but never calls
`dispatcher.Unsubscribe<ConnectionClosed>(OnConnectionClosed)`. The
`EventDispatcher` retains a delegate reference to the disposed
`ReconnectController` for the lifetime of the application. Each server session
that is created and destroyed leaks one `ReconnectController` instance
permanently.

**Failure scenario:** A user connects to and disconnects from five different
servers in one session. Five `ReconnectController` instances, each rooted by the
`EventDispatcher`, are never collected. On long-running sessions with many
server changes (e.g., conference IRC use), this is a slow but unbounded
accumulation.

**Remediation:**
```csharp
public async ValueTask DisposeAsync()
{
    _dispatcher.Unsubscribe<ConnectionClosed>(OnConnectionClosed);  // add this
    await _cts.CancelAsync().ConfigureAwait(false);
    ...
}
```

---

## 10. LOW -- MessageAdded lambda is never unsubscribed when a buffer is removed

**File:** [src/ui/buffers/Manager.cs](src/ui/buffers/Manager.cs#L107)
**Lines:** 107, 112-116

```csharp
// AddBuffer (line 107):
buffer.MessageAdded += msg => MessageAdded?.Invoke(buffer, msg);

// RemoveBuffer (lines 112-116):
private void RemoveBuffer(IBuffer buffer)
{
    _buffers.Remove(buffer);
    BufferDestroyed?.Invoke(buffer);
    // lambda not unsubscribed
}
```

The anonymous lambda wired in `AddBuffer` captures both `buffer` and a
reference into `BufferManager`. Because the lambda is anonymous, it cannot be
unsubscribed by `RemoveBuffer`. The removed buffer's `MessageAdded` delegate
holds a live reference back into `BufferManager`, preventing the buffer from
being collected and forwarding post-removal messages to dead UI state.

**Failure scenario:** User closes a channel tab. `RemoveBuffer` removes the
`ChannelBuffer` from `_buffers`. The server still delivers messages to that
channel before the PART is acknowledged; the leaked lambda invokes
`BufferManager.MessageAdded` for a buffer that no longer exists, updating UI
components that have been torn down.

**Remediation:** Store a named handler per buffer so it can be removed:
```csharp
private T AddBuffer<T>(T buffer) where T : IBuffer
{
    Action<MessageEntry> handler = msg => MessageAdded?.Invoke(buffer, msg);
    buffer.MessageAdded += handler;
    _handlers[buffer] = handler;   // Dictionary<IBuffer, Action<MessageEntry>>
    _buffers.Add(buffer);
    BufferCreated?.Invoke(buffer);
    return buffer;
}

private void RemoveBuffer(IBuffer buffer)
{
    if (_handlers.Remove(buffer, out var handler))
        buffer.MessageAdded -= handler;
    _buffers.Remove(buffer);
    BufferDestroyed?.Invoke(buffer);
}
```

---

## Findings index

| # | Severity | File | Line | Summary |
|---|----------|------|------|---------|
| 9 | Medium | Reconnect.cs | 63 | `ConnectionClosed` never unsubscribed in `DisposeAsync`; per-session object leak |
| 10 | Low | Manager.cs | 107/112 | `MessageAdded` lambda not removed on `RemoveBuffer`; buffer retained after close |
