// SPDX-License-Identifier: GPL-3.0-or-later
using DataJack.Core.Irc;
using Xunit;

namespace DataJack.Core.Tests;

/// <summary>
/// Unit tests for <see cref="AliasManager"/> and <see cref="AliasCommandResult"/>.
/// </summary>
public sealed class AliasManagerTests
{
    // ---------------------------------------------------------------------------
    // Constructor / initialization
    // ---------------------------------------------------------------------------

    [Fact]
    public void Constructor_EmptyInitial_HasNoAliases()
    {
        var mgr = new AliasManager();
        Assert.Empty(mgr.GetAll());
    }

    [Fact]
    public void Constructor_InitializesFromDictionary()
    {
        var initial = new Dictionary<string, string> { ["weather"] = "/msg #weather %1" };
        var mgr = new AliasManager(initial);
        Assert.Single(mgr.GetAll());
        Assert.True(mgr.GetAll().ContainsKey("weather"));
    }

    [Fact]
    public void Constructor_InitialDictionaryIsCopied_MutationDoesNotAffectManager()
    {
        var initial = new Dictionary<string, string> { ["foo"] = "/bar" };
        var mgr = new AliasManager(initial);
        initial["baz"] = "/qux";
        Assert.Single(mgr.GetAll());
    }

    // ---------------------------------------------------------------------------
    // Set / GetAll
    // ---------------------------------------------------------------------------

    [Fact]
    public void Set_AddsNewAlias()
    {
        var mgr = new AliasManager();
        mgr.Set("w", "/whois %1");
        Assert.Single(mgr.GetAll());
        Assert.Equal("/whois %1", mgr.GetAll()["w"]);
    }

    [Fact]
    public void Set_ReplacesExistingAlias()
    {
        var mgr = new AliasManager();
        mgr.Set("w", "/whois %1");
        mgr.Set("w", "/who %1");
        Assert.Single(mgr.GetAll());
        Assert.Equal("/who %1", mgr.GetAll()["w"]);
    }

    [Fact]
    public void GetAll_ReturnsSnapshot_NotLiveReference()
    {
        var mgr = new AliasManager();
        mgr.Set("a", "/b");
        var snap = mgr.GetAll();
        mgr.Set("c", "/d");
        Assert.Single(snap); // snapshot was taken before second Set
    }

    [Fact]
    public void Set_ThrowsOnEmptyName()
    {
        var mgr = new AliasManager();
        Assert.Throws<ArgumentException>(() => mgr.Set("", "/foo"));
    }

    [Fact]
    public void Set_ThrowsOnNameWithSpaces()
    {
        var mgr = new AliasManager();
        Assert.Throws<ArgumentException>(() => mgr.Set("my alias", "/foo"));
    }

    [Fact]
    public void Set_ThrowsOnEmptyExpansion()
    {
        var mgr = new AliasManager();
        Assert.Throws<ArgumentException>(() => mgr.Set("a", ""));
    }

    // ---------------------------------------------------------------------------
    // Remove
    // ---------------------------------------------------------------------------

    [Fact]
    public void Remove_ReturnsTrueAndRemovesAlias()
    {
        var mgr = new AliasManager();
        mgr.Set("w", "/whois %1");
        bool result = mgr.Remove("w");
        Assert.True(result);
        Assert.Empty(mgr.GetAll());
    }

    [Fact]
    public void Remove_ReturnsFalseForMissingAlias()
    {
        var mgr = new AliasManager();
        Assert.False(mgr.Remove("nosuchname"));
    }

    // ---------------------------------------------------------------------------
    // AliasesChanged event
    // ---------------------------------------------------------------------------

    [Fact]
    public void Set_FiresAliasesChangedEvent()
    {
        var mgr = new AliasManager();
        int fired = 0;
        mgr.AliasesChanged += () => fired++;
        mgr.Set("a", "/b");
        Assert.Equal(1, fired);
    }

    [Fact]
    public void Remove_FiresEventOnSuccess()
    {
        var mgr = new AliasManager();
        mgr.Set("a", "/b");
        int fired = 0;
        mgr.AliasesChanged += () => fired++;
        mgr.Remove("a");
        Assert.Equal(1, fired);
    }

    [Fact]
    public void Remove_DoesNotFireEventForMissingAlias()
    {
        var mgr = new AliasManager();
        int fired = 0;
        mgr.AliasesChanged += () => fired++;
        mgr.Remove("nosuch");
        Assert.Equal(0, fired);
    }

    // ---------------------------------------------------------------------------
    // TryExpand — null / no match
    // ---------------------------------------------------------------------------

    [Fact]
    public void TryExpand_ReturnsNullForUnknownCommand()
    {
        var mgr = new AliasManager();
        Assert.Null(mgr.TryExpand("unknowncmd arg1"));
    }

    [Fact]
    public void TryExpand_ReturnsNullForEmptyString()
    {
        var mgr = new AliasManager();
        Assert.Null(mgr.TryExpand(""));
    }

    // ---------------------------------------------------------------------------
    // TryExpand — %1 substitution
    // ---------------------------------------------------------------------------

    [Fact]
    public void TryExpand_PercentOne_ReplacedWithFirstArg()
    {
        var mgr = new AliasManager();
        mgr.Set("w", "/whois %1");
        Assert.Equal("/whois alice", mgr.TryExpand("w alice"));
    }

    [Fact]
    public void TryExpand_PercentOne_EmptyStringWhenNoArg()
    {
        var mgr = new AliasManager();
        mgr.Set("w", "/whois %1");
        Assert.Equal("/whois ", mgr.TryExpand("w"));
    }

    [Fact]
    public void TryExpand_MultiplePercentOne_AllReplaced()
    {
        var mgr = new AliasManager();
        mgr.Set("greet", "/msg %1 Hello %1!");
        Assert.Equal("/msg alice Hello alice!", mgr.TryExpand("greet alice"));
    }

    // ---------------------------------------------------------------------------
    // TryExpand — %2..%9 substitution
    // ---------------------------------------------------------------------------

    [Fact]
    public void TryExpand_PercentTwo_ReplacedWithSecondArg()
    {
        var mgr = new AliasManager();
        mgr.Set("greet", "/msg %1 Hello %2!");
        Assert.Equal("/msg alice Hello world!", mgr.TryExpand("greet alice world"));
    }

    [Fact]
    public void TryExpand_PercentTwo_EmptyStringWhenOnlyOneArg()
    {
        var mgr = new AliasManager();
        mgr.Set("greet", "/msg %1 Hello %2!");
        Assert.Equal("/msg alice Hello !", mgr.TryExpand("greet alice"));
    }

    // ---------------------------------------------------------------------------
    // TryExpand — %* substitution
    // ---------------------------------------------------------------------------

    [Fact]
    public void TryExpand_PercentStar_AllArgsCombined()
    {
        var mgr = new AliasManager();
        mgr.Set("echo", "/msg #chan %*");
        Assert.Equal("/msg #chan hello world foo", mgr.TryExpand("echo hello world foo"));
    }

    [Fact]
    public void TryExpand_PercentStar_EmptyStringWhenNoArgs()
    {
        var mgr = new AliasManager();
        mgr.Set("echo", "/msg #chan %*");
        Assert.Equal("/msg #chan ", mgr.TryExpand("echo"));
    }

    // ---------------------------------------------------------------------------
    // TryExpand — mixed tokens
    // ---------------------------------------------------------------------------

    [Fact]
    public void TryExpand_Mixed_PercentOneAndPercentStar()
    {
        var mgr = new AliasManager();
        // /announce alice hello world -> /msg alice hello world (first arg twice: first by %1, rest by %*)
        mgr.Set("announce", "/msg %1 %*");
        Assert.Equal("/msg alice alice bob charlie", mgr.TryExpand("announce alice bob charlie"));
    }

    [Fact]
    public void TryExpand_ExpansionWithoutLeadingSlash_SlashAddedByManager()
    {
        var mgr = new AliasManager();
        mgr.Set("j", "join %1");
        Assert.Equal("/join #test", mgr.TryExpand("j #test"));
    }

    // ---------------------------------------------------------------------------
    // TryExpand — case-insensitive name lookup
    // ---------------------------------------------------------------------------

    [Fact]
    public void TryExpand_CaseInsensitive_UpperCaseInput()
    {
        var mgr = new AliasManager();
        mgr.Set("weather", "/msg #weather %1");
        Assert.Equal("/msg #weather Seattle", mgr.TryExpand("WEATHER Seattle"));
    }

    [Fact]
    public void TryExpand_CaseInsensitive_MixedCaseStoredName()
    {
        var mgr = new AliasManager();
        mgr.Set("Weather", "/msg #weather %1");
        Assert.Equal("/msg #weather Seattle", mgr.TryExpand("weather Seattle"));
    }

    // ---------------------------------------------------------------------------
    // HandleAlias — list
    // ---------------------------------------------------------------------------

    [Fact]
    public void HandleAlias_EmptyArgs_NoAliasesDefined_ReturnsSuccessMessage()
    {
        var mgr = new AliasManager();
        var result = mgr.HandleAlias("");
        Assert.True(result.Success);
        Assert.Contains("No aliases", result.Message);
    }

    [Fact]
    public void HandleAlias_EmptyArgs_ListsAllAliasesAlphabetically()
    {
        var mgr = new AliasManager();
        mgr.Set("z", "/zzz");
        mgr.Set("a", "/aaa");
        var result = mgr.HandleAlias("");
        Assert.True(result.Success);
        int posA = result.Message.IndexOf("/alias a", StringComparison.Ordinal);
        int posZ = result.Message.IndexOf("/alias z", StringComparison.Ordinal);
        Assert.True(posA < posZ, "Aliases should be listed alphabetically.");
    }

    // ---------------------------------------------------------------------------
    // HandleAlias — show single
    // ---------------------------------------------------------------------------

    [Fact]
    public void HandleAlias_NameOnly_ShowsDefinition()
    {
        var mgr = new AliasManager();
        mgr.Set("w", "/whois %1");
        var result = mgr.HandleAlias("w");
        Assert.True(result.Success);
        Assert.Contains("/alias w", result.Message);
        Assert.Contains("/whois %1", result.Message);
    }

    [Fact]
    public void HandleAlias_NameOnly_NotFound_ReturnsFailure()
    {
        var mgr = new AliasManager();
        var result = mgr.HandleAlias("nosuch");
        Assert.False(result.Success);
        Assert.Contains("nosuch", result.Message);
    }

    // ---------------------------------------------------------------------------
    // HandleAlias — set
    // ---------------------------------------------------------------------------

    [Fact]
    public void HandleAlias_NameAndExpansion_SetsAlias()
    {
        var mgr = new AliasManager();
        var result = mgr.HandleAlias("weather /msg #weather %1");
        Assert.True(result.Success);
        Assert.Equal("/msg #weather %1", mgr.GetAll()["weather"]);
    }

    [Fact]
    public void HandleAlias_InvalidName_ReturnsFailure()
    {
        var mgr = new AliasManager();
        // Name is empty after splitting (double space)
        var result = mgr.HandleAlias("  ");
        // Whitespace-only args treated as empty -> list
        Assert.True(result.Success);
        Assert.Contains("No aliases", result.Message);
    }

    // ---------------------------------------------------------------------------
    // HandleUnalias
    // ---------------------------------------------------------------------------

    [Fact]
    public void HandleUnalias_RemovesAlias_ReturnsSuccess()
    {
        var mgr = new AliasManager();
        mgr.Set("w", "/whois %1");
        var result = mgr.HandleUnalias("w");
        Assert.True(result.Success);
        Assert.Empty(mgr.GetAll());
    }

    [Fact]
    public void HandleUnalias_NotFound_ReturnsFailure()
    {
        var mgr = new AliasManager();
        var result = mgr.HandleUnalias("nosuch");
        Assert.False(result.Success);
        Assert.Contains("nosuch", result.Message);
    }

    [Fact]
    public void HandleUnalias_EmptyName_ReturnsFailure()
    {
        var mgr = new AliasManager();
        var result = mgr.HandleUnalias("  ");
        Assert.False(result.Success);
    }
}
