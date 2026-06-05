// SPDX-License-Identifier: GPL-3.0-or-later
// User-defined command aliases with %1/%2.../%* argument substitution.
// Aliases are persisted in AppConfig.Aliases. See ARCHITECTURE.md §13.

using System.Text;

namespace DataJack.Core.Irc;

/// <summary>
/// Stores user-defined command aliases and expands them with positional argument
/// substitution. Alias names are compared case-insensitively. Alias expansions are
/// stored verbatim and expanded lazily when <see cref="TryExpand"/> is called.
///
/// Substitution tokens in the expansion string:
/// <list type="table">
///   <item><term>%1 .. %9</term><description>Individual whitespace-delimited argument (empty string when absent).</description></item>
///   <item><term>%*</term><description>All arguments joined by a single space (empty string when there are none).</description></item>
/// </list>
///
/// See ARCHITECTURE.md §13 for the command priority hierarchy.
/// </summary>
public sealed class AliasManager
{
    private readonly Dictionary<string, string> _aliases;

    /// <summary>
    /// Fired whenever the alias map changes (add, replace, or remove).
    /// Listeners should persist <see cref="GetAll"/> back to <c>AppConfig.Aliases</c>.
    /// </summary>
    public event Action? AliasesChanged;

    /// <param name="initial">
    /// Existing aliases loaded from config. Keys are stored case-insensitively.
    /// May be null or empty.
    /// </param>
    public AliasManager(IReadOnlyDictionary<string, string>? initial = null)
    {
        _aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (initial is not null)
            foreach (var (k, v) in initial)
                _aliases[k] = v;
    }

    // ---------------------------------------------------------------------------
    // CRUD
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Add or replace an alias. The <paramref name="name"/> is stored in lowercase.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// If <paramref name="name"/> is empty or contains spaces, or if
    /// <paramref name="expansion"/> is empty.
    /// </exception>
    public void Set(string name, string expansion)
    {
        ValidateName(name);
        ArgumentException.ThrowIfNullOrEmpty(expansion, nameof(expansion));
        _aliases[name] = expansion;
        AliasesChanged?.Invoke();
    }

    /// <summary>
    /// Remove an alias by name. Returns <c>false</c> if no alias with that name exists.
    /// </summary>
    public bool Remove(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name, nameof(name));
        if (!_aliases.Remove(name))
            return false;
        AliasesChanged?.Invoke();
        return true;
    }

    /// <summary>
    /// Return a snapshot of all current aliases, keyed by name (compared case-insensitively).
    /// The returned dictionary is independent of the internal map; mutations to it are ignored.
    /// </summary>
    public IReadOnlyDictionary<string, string> GetAll() =>
        new Dictionary<string, string>(_aliases, StringComparer.OrdinalIgnoreCase);

    // ---------------------------------------------------------------------------
    // Expansion
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Try to expand a command line (without the leading '/') as an alias.
    /// </summary>
    /// <param name="commandLine">
    /// The raw text after the '/', e.g. <c>"weather Seattle"</c>.
    /// Leading whitespace is trimmed before matching.
    /// </param>
    /// <returns>
    /// The expanded command string including a leading '/' (e.g. <c>"/msg #weather Seattle"</c>),
    /// or <c>null</c> when the first word does not match any known alias.
    /// </returns>
    public string? TryExpand(string commandLine)
    {
        ArgumentNullException.ThrowIfNull(commandLine);

        ReadOnlySpan<char> span = commandLine.AsSpan().TrimStart();
        int space = span.IndexOf(' ');

        string name = (space < 0 ? span : span[..space]).ToString();
        string rest = space < 0 ? string.Empty : span[(space + 1)..].ToString();

        if (!_aliases.TryGetValue(name, out string? expansion))
            return null;

        return Substitute(expansion, rest);
    }

    // ---------------------------------------------------------------------------
    // Command handlers
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Parse and execute the text arguments to the <c>/alias</c> command.
    /// <list type="table">
    ///   <item><term>(empty)</term><description>List all defined aliases.</description></item>
    ///   <item><term>name</term><description>Show the expansion for a single alias.</description></item>
    ///   <item><term>name expansion</term><description>Add or replace an alias.</description></item>
    /// </list>
    /// Returns an <see cref="AliasCommandResult"/> whose <c>Message</c> is suitable for
    /// printing to the active buffer.
    /// </summary>
    public AliasCommandResult HandleAlias(string args)
    {
        args = args.Trim();

        if (args.Length == 0)
        {
            if (_aliases.Count == 0)
                return new AliasCommandResult(true, "No aliases defined.");

            var lines = _aliases
                .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .Select(kv => $"/alias {kv.Key} {kv.Value}");
            return new AliasCommandResult(true, string.Join('\n', lines));
        }

        int space = args.IndexOf(' ');
        if (space < 0)
        {
            // Show a single alias definition.
            string queryName = args;
            return _aliases.TryGetValue(queryName, out string? exp)
                ? new AliasCommandResult(true, $"/alias {queryName} {exp}")
                : new AliasCommandResult(false, $"No alias named '{queryName}'.");
        }

        // Add or replace.
        string aliasName = args[..space];
        string aliasExpansion = args[(space + 1)..].Trim();

        try
        {
            Set(aliasName, aliasExpansion);
            return new AliasCommandResult(true, $"Alias '{aliasName}' set to: {aliasExpansion}");
        }
        catch (ArgumentException ex)
        {
            return new AliasCommandResult(false, ex.Message);
        }
    }

    /// <summary>
    /// Remove an alias by name (handles the <c>/unalias name</c> command).
    /// </summary>
    public AliasCommandResult HandleUnalias(string name)
    {
        name = name.Trim();
        if (string.IsNullOrEmpty(name))
            return new AliasCommandResult(false, "Usage: /unalias <name>");

        return Remove(name)
            ? new AliasCommandResult(true, $"Alias '{name}' removed.")
            : new AliasCommandResult(false, $"No alias named '{name}'.");
    }

    // ---------------------------------------------------------------------------
    // Implementation helpers
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Single-pass substitution of %1..%9 and %* in the expansion template.
    /// The leading '/' is stripped before substitution and re-added to the result,
    /// so callers may define expansions with or without a leading slash.
    /// </summary>
    private static string Substitute(string expansion, string args)
    {
        // Strip optional leading '/' from the stored expansion.
        string body = expansion.StartsWith('/') ? expansion[1..] : expansion;

        string[] parts = args.Length == 0
            ? Array.Empty<string>()
            : args.Split(' ', StringSplitOptions.None);

        // Single-pass scan to avoid double-substitution when args contain '%' tokens.
        var sb = new StringBuilder(body.Length + args.Length);
        int i = 0;
        while (i < body.Length)
        {
            if (body[i] == '%' && i + 1 < body.Length)
            {
                char next = body[i + 1];
                if (next == '*')
                {
                    sb.Append(args);
                    i += 2;
                    continue;
                }
                if (next is >= '1' and <= '9')
                {
                    int idx = next - '0'; // 1-based
                    if (idx <= parts.Length)
                        sb.Append(parts[idx - 1]);
                    // absent argument -> emit nothing (empty string)
                    i += 2;
                    continue;
                }
            }
            sb.Append(body[i]);
            i++;
        }

        return "/" + sb.ToString();
    }

    private static void ValidateName(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name, nameof(name));
        if (name.Contains(' '))
            throw new ArgumentException("Alias name must not contain spaces.", nameof(name));
    }
}

/// <summary>Result of an alias management command (add, remove, list, show).</summary>
public readonly record struct AliasCommandResult(
    /// <summary><c>true</c> when the operation succeeded; <c>false</c> on error.</summary>
    bool Success,
    /// <summary>Human-readable message suitable for printing to the active buffer.</summary>
    string Message);
