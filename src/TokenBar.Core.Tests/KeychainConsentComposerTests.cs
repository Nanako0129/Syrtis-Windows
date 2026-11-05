using TokenBar.Core;

namespace TokenBar.Core.Tests;

// S3b (C3): the one composer owns both credential-read answers and installs a
// single full-replace payload, so neither consent can silently drop the other.
// Every test asserts what reaches the (fake) FFI setter, since on Windows the
// composed payload is the only gate before DPAPI.
public class KeychainConsentComposerTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "tokenbar-tests", Guid.NewGuid().ToString("N"));

    private readonly List<string> _calls = [];

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
        }
    }

    private SettingsStore Store() => new(Path.Combine(_dir, "settings.json"));

    private KeychainConsentComposer Composer(SettingsStore store) =>
        new(store, json =>
        {
            lock (_calls)
            {
                _calls.Add(json);
            }
        });

    // C3: a Grok withdrawal keeps a stored Cursor yes (the composed payload
    // still carries cursor), and a Cursor withdrawal keeps a stored Grok yes.
    [Fact]
    public void WithdrawingOneConsentKeepsTheOther()
    {
        var store = Store();
        var composer = Composer(store);
        composer.Install(KeychainConsentComposer.GrokBotId, true);
        composer.Install(KeychainConsentComposer.CursorId, true);
        Assert.Equal(["""{"grok-bot":true}""", """{"grok-bot":true,"cursor":true}"""], _calls);

        // Grok Withdraw: cursor stays.
        composer.Install(KeychainConsentComposer.GrokBotId, false);
        Assert.Equal("""{"cursor":true}""", _calls[^1]);
        Assert.True(store.GetNullableBool(KeychainConsentComposer.CursorStorageKey));
        Assert.False(store.GetNullableBool(KeychainConsentComposer.GrokBotStorageKey));

        // Grok on again (cursor still on), then Cursor Withdraw: grok stays.
        composer.Install(KeychainConsentComposer.GrokBotId, true);
        composer.Install(KeychainConsentComposer.CursorId, true);
        composer.Install(KeychainConsentComposer.CursorId, false);
        Assert.Equal("""{"grok-bot":true}""", _calls[^1]);
        Assert.True(store.GetNullableBool(KeychainConsentComposer.GrokBotStorageKey));
        Assert.False(store.GetNullableBool(KeychainConsentComposer.CursorStorageKey));
    }

    // C3: a Grok Allow while Cursor's stored answer is already yes installs
    // BOTH in one payload, not just grok.
    // Mutation: compose from the store for the changing id too (read grok from
    // the store before writing it) -> the first Allow would compose
    // {"cursor":true} (grok still absent in the store) and drop the new grant.
    [Fact]
    public void GrantingOneWhileTheOtherIsStoredYesInstallsBoth()
    {
        var store = Store();
        store.SetBool(KeychainConsentComposer.CursorStorageKey, true);
        var composer = Composer(store);
        var changed = composer.Install(KeychainConsentComposer.GrokBotId, true);
        Assert.True(changed);
        Assert.Equal(["""{"grok-bot":true,"cursor":true}"""], _calls);
    }

    // Core first, then the store: a throwing setter persists neither answer.
    [Fact]
    public void AThrowingSetterPersistsNothing()
    {
        var store = Store();
        var composer = new KeychainConsentComposer(store, _ => throw new InvalidOperationException("boom"));
        Assert.Throws<InvalidOperationException>(
            () => composer.Install(KeychainConsentComposer.CursorId, true));
        Assert.Null(store.GetNullableBool(KeychainConsentComposer.CursorStorageKey));
    }

    // A denial with nothing installed is a no-op for the core; the stored
    // answer still sticks.
    [Fact]
    public void DenialWithNothingInstalledMakesNoCoreCall()
    {
        var store = Store();
        var composer = Composer(store);
        var changed = composer.Install(KeychainConsentComposer.CursorId, false);
        Assert.False(changed);
        Assert.Empty(_calls);
        Assert.False(store.GetNullableBool(KeychainConsentComposer.CursorStorageKey));
    }

    // Launch: a stored Cursor yes is installed as the composed payload; no
    // store write happens (the answers are already stored).
    [Fact]
    public void LaunchInstallsAStoredCursorYes()
    {
        var store = Store();
        store.SetBool(KeychainConsentComposer.CursorStorageKey, true);
        Composer(store).InstallComposedIfAnyGranted();
        Assert.Equal(["""{"cursor":true}"""], _calls);
    }

    // Launch with both stored yes installs both; with neither, no setter call.
    [Fact]
    public void LaunchInstallsBothOrNothing()
    {
        var store = Store();
        store.SetBool(KeychainConsentComposer.GrokBotStorageKey, true);
        store.SetBool(KeychainConsentComposer.CursorStorageKey, true);
        Composer(store).InstallComposedIfAnyGranted();
        Assert.Equal(["""{"grok-bot":true,"cursor":true}"""], _calls);

        // A store in its own empty directory (Store() would reload the file
        // the first half just wrote).
        _calls.Clear();
        var empty = new SettingsStore(Path.Combine(_dir, "empty", "settings.json"));
        Assert.Null(empty.GetNullableBool(KeychainConsentComposer.GrokBotStorageKey));
        Assert.Null(empty.GetNullableBool(KeychainConsentComposer.CursorStorageKey));
        Composer(empty).InstallComposedIfAnyGranted();
        Assert.Empty(_calls);
    }

    // C3: an answer that changes nothing installed makes no core call. Here
    // only cursor is installed and Grok Bot's "Not now" composes the same
    // {"cursor":true}.
    // Mutation: KeychainConsentComposer.cs `if (changed)` -> `if (true)`
    // (re-send every answer) -> one setter call.
    [Fact]
    public void ANoForOneIdWhileOnlyTheOtherIsInstalledMakesNoCoreCall()
    {
        var store = Store();
        var composer = Composer(store);
        composer.Install(KeychainConsentComposer.CursorId, true);
        Assert.Equal(["""{"cursor":true}"""], _calls);
        _calls.Clear();
        Assert.False(composer.Install(KeychainConsentComposer.GrokBotId, false));
        Assert.Empty(_calls);
        Assert.False(store.GetNullableBool(KeychainConsentComposer.GrokBotStorageKey));
    }

    // The launch install runs twice (before Cursor sync, then as the fetch
    // hook); the second is a no-op once the first succeeded.
    [Fact]
    public void ASecondLaunchInstallMakesNoCoreCall()
    {
        var store = Store();
        store.SetBool(KeychainConsentComposer.CursorStorageKey, true);
        var composer = Composer(store);
        composer.InstallComposedIfAnyGranted();
        composer.InstallComposedIfAnyGranted();
        Assert.Equal(["""{"cursor":true}"""], _calls);
    }

    // A launch reapply failure must not throw (it would take down launch).
    [Fact]
    public void LaunchReapplyFailureIsSwallowed()
    {
        var store = Store();
        store.SetBool(KeychainConsentComposer.CursorStorageKey, true);
        var composer = new KeychainConsentComposer(store, _ => throw new InvalidOperationException("boom"));
        composer.InstallComposedIfAnyGranted(); // must not throw
    }

    // The core FFI accepts a composed payload carrying both ids and the empty
    // withdrawal (a smoke test of the real setter, unlike the fakes above).
    [Fact]
    public void NativeSetterAcceptsAComposedPayload()
    {
        KeychainConsentComposer.NativeSetter("""{"grok-bot":true,"cursor":true}""");
        KeychainConsentComposer.NativeSetter("{}");
    }
}
