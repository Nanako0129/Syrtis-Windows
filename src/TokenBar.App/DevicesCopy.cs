namespace TokenBar.App;

/// <summary>Settings → Devices copy, ported from macOS <c>DevicesCopy</c>
/// (DevicesModel.swift :727-890, Syrtis f8a1a0ae). English is the localization
/// key, as everywhere in this port. Differences from macOS, all approved
/// (sa-4f brief, plan §5-Q4 and the 2026-11-22 ledger): the 13 "this Mac"
/// strings say "this PC"; the Cocoa <c>%@</c> placeholders are <c>{0}</c>;
/// <see cref="ApproveEffect"/> carries the Discord clause; and four
/// Windows-only sentences, <see cref="FirewallNote"/> and the three W4
/// disclosures. Text from another device or from the agent (a label, an
/// address, a fingerprint, a path) is only ever a format argument or shown
/// verbatim, never a key. A pure file so the test project can pin it.</summary>
internal static class DevicesCopy
{
    public const string Page = "Devices";

    // (A) Share this PC
    public const string ShareSection = "Share this PC";
    public const string ShareSummary = "Let your other devices read this PC's usage totals.";
    public const string Service = "Sharing service";
    public const string ServiceRunning = "Running";
    public const string ServiceStopped = "Not running";
    public const string NotRunning = "The sharing service isn't running.";
    public const string StartBusy = "Another copy of Syrtis is using the sharing service.";
    public const string StartUnsafe = "The sharing service's folder can't be used safely.";
    public const string StartFailed = "The sharing service couldn't start.";
    public const string Retry = "Retry";
    public const string ShareToggle = "Share this PC's usage";
    public const string ShareOffNote = "Turning sharing off ends access for every device listed under Devices with access.";
    public const string NeedsScope = "Approve the scan scope below before sharing.";
    public const string SharingBusy = "An earlier pairing invitation hasn't expired yet. Try again in a few minutes.";
    public const string SharingAlreadyOn = "Sharing is already on.";
    public const string DeviceName = "Device name";
    public const string DeviceNameHint = "Linked devices will see this name.";
    public const string ListenOn = "Listen on";
    public const string LocalOnlyLabel = "This PC only (127.0.0.1)";
    public const string ChooseAddress = "Choose an address";
    public const string Port = "Port";
    public const string Apply = "Apply";
    public const string UnreachableLine = "Other devices can't reach this PC.";
    public const string PublicAddress = "This address may be reachable from the internet.";
    public const string PortRangeLine = "Use a port from 1024 to 65535.";
    public const string NameTooLong = "The name is too long (at most 128 bytes).";
    public const string AddressRefused = "Syrtis can't listen on this address.";
    public const string CreateInvitation = "Create invitation";
    public const string InvitationReady = "Invitation ready. Copy it and paste it on the other device.";
    public const string ExpiresIn = "Expires in {0}";
    public const string Copy = "Copy";
    public const string CopiedLine = "Copied";
    public const string CancelInvitation = "Cancel invitation";
    public const string OfferNeedsSharing = "Turn on sharing first.";
    public const string OfferNeedsAddress = "Choose a listen address first.";
    public const string Requests = "Waiting for approval";
    public const string CompareCode = "Check that the other device shows the same code.";
    public const string Approve = "Approve";
    public const string Reject = "Reject";
    // The user-approved Windows sentence (plan §6): macOS's plus the Discord clause.
    public const string ApproveEffect = "Approving lets that device read this PC's usage totals in the approved scan scope until you revoke it. That device may show them combined with its own, including on its Discord status.";
    public const string RequestExpired = "This request has expired.";
    public const string GrantsHeader = "Devices with access";
    public const string Revoke = "Revoke";
    public const string RevokeTitle = "Revoke access for {0}?";
    public const string RevokeMessage = "That device can no longer read this PC's usage.";
    public const string Cancel = "Cancel";

    // Windows only (plan §5-Q4, user-approved 2026-11-21): under the listen
    // address. The prompt itself is not measured yet (SA-4h).
    public const string FirewallNote = "The first time you apply, Windows may ask whether to allow Syrtis through the firewall. Choose Allow, or other devices can't reach this PC.";

    // Windows only: the W4 known limits (agent spec state-store-v1.md), in the
    // wording the user approved 2026-11-22, shown in the section's info flyout.
    // macOS deliberately does not show them (2026-11-23).
    public const string FolderWritable = "Programs and accounts that can write to your AppData folder, such as the OpenAI Codex sandbox, can change the sharing service's folder while Syrtis isn't running.";
    public const string FolderReplaced = "A folder replaced that way can make this PC share with devices you didn't approve.";
    public const string FolderRestored = "An older copy put back can undo what you changed since, so a device you revoked can read your usage again.";

    // (B) Scan scope
    public const string ScopeSection = "Scan scope";
    public const string ScopeSummary = "What sharing reads, and what it sends.";
    public const string ScopeBody = "Sharing sends usage totals: token counts, cost, message and turn counts, and duration, grouped by day and hour (with the time-zone offset) and by client, model, provider and agent name. Those names are sent exactly as recorded on this PC. Conversation content is not sent, and there are no fields for file paths or accounts. This PC's device name, if you set one, is also sent to the devices linked to it. Clients that a Syrtis update adds support for are included automatically.";
    public const string HomeFolder = "Home folder";
    public const string ConfigFolder = "Config folder";
    public const string DataFolder = "Data folder";
    public const string AddedFolders = "Added folders";
    public const string ExcludedFolders = "Excluded folders";
    public const string OpenCodeDatabases = "OpenCode databases";
    public const string Clients = "Clients";
    public const string EnvRoots = "Some clients' folders are set by environment variables ({0}). This PC's pages follow them; sharing doesn't.";
    public const string ScopeApprovedLine = "Approved.";
    public const string NeedsApproval = "Needs approval. Nothing is shared until then.";
    public const string ApproveScope = "Approve scan scope";
    public const string ScopeChanged = "The scope changed after it was shown. Review it again, then approve.";

    // (C) Linked devices
    public const string LinkedSection = "Linked devices";
    public const string LinkedSummary = "Devices whose usage this PC can read.";
    public const string NoLinked = "No linked devices.";
    public const string Remove = "Remove";
    public const string RemoveTitle = "Remove {0}?";
    public const string RemoveMessage = "This PC stops reading that device's usage. Link it again to read it.";
    public const string LinkAnother = "Link another device";
    public const string PastePlaceholder = "Paste the invitation";
    public const string Link = "Link";
    public const string YourCode = "Code: {0}";
    public const string WaitingApproval = "Check that the other device shows the same code and approve it there. Waiting…";
    public const string LinkedLine = "Linked.";
    public const string LinkDestination = "Syrtis will connect to:";
    public const string LinkDestinationHint = "Check that this is the address of your other device. The code you compare next confirms which device it is.";
    public const string InvitationExpired = "The invitation expired. Create a new one on the other device.";
    public const string Declined = "The other device declined.";
    public const string UnreachableDevice = "Syrtis couldn't reach the other device.";
    public const string IdentityChanged = "This PC's sharing identity changed while waiting. Link again.";
    public const string InvalidInvitation = "That isn't a valid invitation.";
    public const string OwnInvitation = "That invitation is from this PC.";
    public const string UnusableInvitation = "That invitation can't be used anymore.";
    public const string LinkFailed = "Linking didn't finish. Try again.";
    public const string CancelledStillLinked = "Syrtis couldn't remove a device whose linking you cancelled. Remove it from the list.";
    public const string StateActive = "Active";
    public const string StateRevoked = "Revoked";
    public const string StateExpired = "Expired";

    public const string GenericFailure = "Something went wrong. Try again.";

    /// <summary>The info flyout of "Share this PC": macOS's two lines, then the
    /// three W4 limits.</summary>
    public static IEnumerable<(string Text, bool Heading)> ShareInfoRows() =>
    [
        (ShareOffNote, false), (ApproveEffect, false),
        (FolderWritable, false), (FolderReplaced, false), (FolderRestored, false),
    ];

    public static IEnumerable<(string Text, bool Heading)> LinkedInfoRows() => [(RemoveMessage, false)];

    /// <summary>Start failures (DM :827-833). <c>unsafeState</c> also covers
    /// another process holding <c>Nyanako</c> open with DELETE access (agent
    /// W4): that is the agent's answer, no new copy.</summary>
    public static string StartFailure(string reason) => reason switch
    {
        "busy" => StartBusy,
        "unsafeState" or "unsafeSocket" or "pathTooLong" => StartUnsafe,
        _ => StartFailed,
    };

    public static string SharingFailure(string reason) => reason switch
    {
        "busy" => SharingBusy,
        "invalid" => SharingAlreadyOn,
        _ => GenericFailure,
    };

    public static string OfferFailure(string reason) => reason switch
    {
        "unauthorized" => OfferNeedsSharing,
        "invalid" => OfferNeedsAddress,
        _ => GenericFailure,
    };

    public static string LinkFailure(string reason) => reason switch
    {
        "expired" => InvitationExpired,
        "rejected" => Declined,
        "transport" => UnreachableDevice,
        "identityChanged" => IdentityChanged,
        "invalidOffer" => InvalidInvitation,
        "selfPairing" => OwnInvitation,
        "offerUnusable" or "unauthorized" or "revoked" => UnusableInvitation,
        _ => LinkFailed,
    };

    /// <summary>A grant or peer state; an unknown one is shown as sent.</summary>
    public static string State(string raw) => raw switch
    {
        "active" => StateActive,
        "revoked" => StateRevoked,
        "expired" => StateExpired,
        _ => raw,
    };

    public static readonly IReadOnlyList<string> All =
    [
        Page, ShareSection, ShareSummary, Service, ServiceRunning, ServiceStopped, NotRunning,
        StartBusy, StartUnsafe, StartFailed, Retry, ShareToggle, ShareOffNote, NeedsScope,
        SharingBusy, SharingAlreadyOn, DeviceName, DeviceNameHint, ListenOn, LocalOnlyLabel, ChooseAddress, Port,
        Apply, UnreachableLine, PublicAddress, PortRangeLine, NameTooLong, AddressRefused,
        CreateInvitation, InvitationReady, ExpiresIn, Copy, CopiedLine, CancelInvitation,
        OfferNeedsSharing, OfferNeedsAddress, Requests, CompareCode, Approve, Reject, ApproveEffect,
        RequestExpired, GrantsHeader, Revoke, RevokeTitle, RevokeMessage, Cancel,
        ScopeSection, ScopeSummary, ScopeBody, HomeFolder, ConfigFolder, DataFolder, AddedFolders,
        ExcludedFolders, OpenCodeDatabases, Clients, EnvRoots, ScopeApprovedLine, NeedsApproval,
        ApproveScope, ScopeChanged, LinkedSection, LinkedSummary, NoLinked, Remove, RemoveTitle,
        RemoveMessage, LinkAnother, PastePlaceholder, Link, YourCode, WaitingApproval, LinkedLine,
        LinkDestination, LinkDestinationHint,
        InvitationExpired, Declined, UnreachableDevice, IdentityChanged, InvalidInvitation,
        OwnInvitation, UnusableInvitation, LinkFailed, CancelledStillLinked, StateActive,
        StateRevoked, StateExpired, GenericFailure,
        FirewallNote, FolderWritable, FolderReplaced, FolderRestored,
    ];
}
