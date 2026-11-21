using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace TokenBar.Core;

/// <summary>
/// Makes a Velopack install root writable only by the user who owns it.
/// <para>
/// Velopack sets no ACL: the root and everything below it inherit from
/// <c>%LOCALAPPDATA%</c>. Any account granted write on the profile or on
/// <c>AppData</c> (for example a sandbox that granted itself
/// <c>(OI)(CI)</c> Modify there) can then replace <c>Syrtis.App.exe</c>,
/// <c>tb_core_ffi.dll</c> or <c>Update.exe</c>, which run with the user's full
/// rights. This gives the root a protected DACL holding exactly three allow
/// ACEs, <c>FullControl</c> with <c>(OI)(CI)</c>, for the current user,
/// SYSTEM and Administrators:
/// <c>D:PAI(A;OICI;FA;;;&lt;user&gt;)(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)</c>.
/// Windows then recomputes the inherited ACEs of everything below it, and an
/// update (staged under the root, then renamed to <c>current</c>) inherits the
/// same three.
/// </para>
/// <para>
/// Left alone on purpose: the owner (never taken or changed), explicit ACEs
/// someone put on a child, and anything that is not a Velopack root.
/// </para>
/// <para>
/// Known limit, measured with a write-restricted token against a test tree:
/// an account whose grant on the root's parent includes FILE_DELETE_CHILD can
/// still rename the whole protected root away while nothing inside it is
/// open, and plant a new folder in its place. A file held open inside the
/// root without delete sharing (the running app) makes that rename fail, for
/// the owner too.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class InstallDirectoryProtection
{
    public enum Result
    {
        Applied,
        AlreadyProtected,
        SkippedNotOwner,
        SkippedMissing,
        Failed,
    }

    private const InheritanceFlags BothInherit =
        InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

    /// <summary>Applies the owner-only DACL to <paramref name="root"/>.
    /// Never throws.</summary>
    public static Result Apply(string root) => Apply(root, out _);

    /// <summary>As <see cref="Apply(string)"/>; <paramref name="error"/> is
    /// the exception behind a <see cref="Result.Failed"/>, null otherwise
    /// (also null for a root that is a reparse point).</summary>
    public static Result Apply(string root, out Exception? error) =>
        ApplyForTesting(root, ownerAccepted: null, out error);

    /// <summary>Test seam: <paramref name="ownerAccepted"/> replaces the owner
    /// rule (current user or Administrators), because a root owned by anyone
    /// else cannot be built without admin rights.</summary>
    public static Result ApplyForTesting(
        string root, Func<SecurityIdentifier, bool>? ownerAccepted, out Exception? error)
    {
        error = null;
        try
        {
            return ApplyCore(root, ownerAccepted);
        }
        catch (Exception ex)
        {
            error = ex;
            return Result.Failed;
        }
    }

    private static Result ApplyCore(string root, Func<SecurityIdentifier, bool>? ownerAccepted)
    {
        var dir = new DirectoryInfo(root);
        if (!dir.Exists)
        {
            return Result.SkippedMissing;
        }

        // A junction or symlink would point the write at whatever it targets.
        if (dir.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            return Result.Failed;
        }

        var user = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("No user SID.");
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        SecurityIdentifier[] targets = [user, system, admins];

        var current = dir.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        var owner = current.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        var accepted = ownerAccepted
            ?? (sid => sid == user || sid == admins);
        if (owner is null || !accepted(owner))
        {
            return Result.SkippedNotOwner;
        }

        if (IsTarget(current, targets) && ChildrenInheritOnly(dir.FullName, targets))
        {
            return Result.AlreadyProtected;
        }

        // Built fresh, never edited from `current`: an edited copy would keep
        // an explicit foreign ACE on the root. No owner is set, so only the
        // DACL is written (SetNamedSecurityInfo, which also propagates).
        var fresh = new DirectorySecurity();
        fresh.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in targets)
        {
            fresh.AddAccessRule(new FileSystemAccessRule(
                sid, FileSystemRights.FullControl, BothInherit,
                PropagationFlags.None, AccessControlType.Allow));
        }

        dir.SetAccessControl(fresh);
        return Result.Applied;
    }

    private static bool IsTarget(DirectorySecurity security, SecurityIdentifier[] targets)
    {
        if (!security.AreAccessRulesProtected)
        {
            return false;
        }

        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>().ToList();
        return rules.Count == targets.Length
            && rules.All(r =>
                r.AccessControlType == AccessControlType.Allow
                && !r.IsInherited
                && r.FileSystemRights == FileSystemRights.FullControl
                && r.InheritanceFlags == BothInherit
                && r.PropagationFlags == PropagationFlags.None)
            && targets.All(t => rules.Any(r => r.IdentityReference == t));
    }

    /// <summary>True when the children that matter carry no inherited ACE for
    /// a SID outside <paramref name="targets"/>, so a propagation that stopped
    /// partway is redone. Explicit child ACEs are not counted: rewriting the
    /// root cannot remove them, so counting them would only rewrite it at
    /// every start.</summary>
    private static bool ChildrenInheritOnly(string root, SecurityIdentifier[] targets)
    {
        var updateExe = new FileInfo(Path.Combine(root, "Update.exe"));
        var currentDir = new DirectoryInfo(Path.Combine(root, "current"));
        var appExe = new FileInfo(Path.Combine(root, "current", "Syrtis.App.exe"));

        return (!updateExe.Exists || InheritsOnly(updateExe.GetAccessControl(), targets))
            && (!currentDir.Exists || InheritsOnly(currentDir.GetAccessControl(), targets))
            && (!appExe.Exists || InheritsOnly(appExe.GetAccessControl(), targets));
    }

    private static bool InheritsOnly(FileSystemSecurity security, SecurityIdentifier[] targets) =>
        security.GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<AuthorizationRule>()
            .Where(r => r.IsInherited)
            .All(r => targets.Any(t => r.IdentityReference == t));
}
