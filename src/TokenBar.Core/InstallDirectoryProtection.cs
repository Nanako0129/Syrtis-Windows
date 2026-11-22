using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace TokenBar.Core;

/// <summary>
/// Makes a Velopack install root writable only by the current user, SYSTEM
/// and Administrators.
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
/// Known limit, measured on 2026-11-21 with a write-restricted token of the
/// current user against a test tree (the shape of a sandbox that runs as the
/// user): this stops creating or overwriting files inside the root, but not
/// deleting them, and not renaming the whole root away and planting a new
/// folder in its place whenever the restricting SID may write to the root's
/// parent — with or without FILE_DELETE_CHILD on that parent, because a
/// write-restricted token checks its restricting SIDs only for write rights,
/// and DELETE is granted through the user. A file held open inside the root
/// without delete sharing (the running app) makes that rename fail, for the
/// owner too. The remedy is outside the app: remove the sandbox's write grant
/// from the profile, or do not start the sandbox in the home directory.
/// </para>
/// <para>
/// The reparse-point check and the write are both path-based, so an account
/// that can write the root's parent could swap the root for a junction
/// between them. Such an account can already rename the root away (the limit
/// above), so this is not handled.
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

    /// <summary>Applies the owner-only DACL to <paramref name="root"/>,
    /// skipping the write when the whole tree already matches. Never
    /// throws.</summary>
    public static Result Apply(string root) => Apply(root, alwaysWrite: false, out _);

    /// <summary>As <see cref="Apply(string)"/>. <paramref name="alwaysWrite"/>
    /// skips the <see cref="Result.AlreadyProtected"/> check (the Velopack
    /// hooks, which run right after Velopack created files under the root).
    /// <paramref name="error"/> is the exception behind a
    /// <see cref="Result.Failed"/>, null otherwise (also null for a root that
    /// is a reparse point).</summary>
    public static Result Apply(string root, bool alwaysWrite, out Exception? error) =>
        ApplyForTesting(root, ownerAccepted: null, alwaysWrite, out error);

    /// <summary>Test seam: <paramref name="ownerAccepted"/> replaces the owner
    /// rule (current user or Administrators), because a root owned by anyone
    /// else cannot be built without admin rights.</summary>
    public static Result ApplyForTesting(
        string root, Func<SecurityIdentifier, bool>? ownerAccepted, bool alwaysWrite, out Exception? error)
    {
        error = null;
        try
        {
            return ApplyCore(root, ownerAccepted, alwaysWrite);
        }
        catch (Exception ex)
        {
            error = ex;
            return Result.Failed;
        }
    }

    private static Result ApplyCore(string root, Func<SecurityIdentifier, bool>? ownerAccepted, bool alwaysWrite)
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

        // Running as SYSTEM is never the per-user install this protects, and
        // the target set would name SYSTEM twice. Reported as not-owner.
        if (user == system)
        {
            return Result.SkippedNotOwner;
        }

        SecurityIdentifier[] targets = [user, system, admins];

        var current = dir.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        var owner = current.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        var accepted = ownerAccepted
            ?? (sid => sid == user || sid == admins);
        if (owner is null || !accepted(owner))
        {
            return Result.SkippedNotOwner;
        }

        if (!alwaysWrite && IsTarget(current, targets) && TreeInheritsOnly(dir, targets))
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

    /// <summary>True when nothing under the root carries an inherited ACE for
    /// a SID outside <paramref name="targets"/>, so a propagation that stopped
    /// partway (or a file renamed in with its old inherited ACEs) is redone.
    /// Explicit ACEs are not counted: rewriting the root cannot remove them.
    /// Reparse points are neither read (a path-based read would follow them)
    /// nor descended into.</summary>
    private static bool TreeInheritsOnly(DirectoryInfo root, SecurityIdentifier[] targets)
    {
        var options = new EnumerationOptions
        {
            AttributesToSkip = 0,
            IgnoreInaccessible = false,
            RecurseSubdirectories = false,
        };
        var pending = new Stack<DirectoryInfo>();
        pending.Push(root);
        while (pending.TryPop(out var dir))
        {
            foreach (var entry in dir.EnumerateFileSystemInfos("*", options))
            {
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    continue;
                }

                if (entry is DirectoryInfo sub)
                {
                    if (!InheritsOnly(sub.GetAccessControl(AccessControlSections.Access), targets))
                    {
                        return false;
                    }

                    pending.Push(sub);
                }
                else if (!InheritsOnly(((FileInfo)entry).GetAccessControl(AccessControlSections.Access), targets))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool InheritsOnly(FileSystemSecurity security, SecurityIdentifier[] targets) =>
        security.GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<AuthorizationRule>()
            .Where(r => r.IsInherited)
            .All(r => targets.Any(t => r.IdentityReference == t));
}
