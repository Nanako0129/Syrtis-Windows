using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using TokenBar.Core;
using Result = TokenBar.Core.InstallDirectoryProtection.Result;

namespace TokenBar.Core.Tests;

/// <summary>
/// The install-root protection against a real NTFS tree in this test's own
/// temp directory. The parent gets an explicit inheritable Modify ACE for
/// BUILTIN\Users, standing in for a sandbox account granted write on the
/// profile; every ACL this suite changes is on a directory it created.
/// Windows only; elsewhere each test returns without asserting. The suite
/// also runs on macOS, so nothing here is a static field: a type initializer
/// would call Windows-only APIs before any test's platform guard.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class InstallDirectoryProtectionTests
{
    private static SecurityIdentifier Users => new(WellKnownSidType.BuiltinUsersSid, null);

    private static SecurityIdentifier[] Targets =>
    [
        WindowsIdentity.GetCurrent().User!,
        new(WellKnownSidType.LocalSystemSid, null),
        new(WellKnownSidType.BuiltinAdministratorsSid, null),
    ];

    [Fact]
    public void InheritedForeignWriteIsRemovedFromTheTree()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var tree = new Tree();

        // Control: the fixture really carries the inherited grant it adds.
        Assert.Contains(Rules(FileAcl(tree.File)), r => IsAddedUsersGrant(r, InheritanceFlags.None));
        Assert.Contains(Rules(DirAcl(tree.Current)), r => IsAddedUsersGrant(r, Both));

        Assert.Equal(Result.Applied, InstallDirectoryProtection.Apply(tree.Root));

        AssertExactlyTargets(DirAcl(tree.Root));
        Assert.DoesNotContain(Rules(FileAcl(tree.File)), r => r.IdentityReference == Users);
        Assert.DoesNotContain(Rules(DirAcl(tree.Current)), r => r.IdentityReference == Users);
        AssertInheritsOnlyTargets(FileAcl(tree.File));
        AssertInheritsOnlyTargets(DirAcl(tree.Current));

        var before = DirAcl(tree.Root).GetSecurityDescriptorBinaryForm();
        Assert.Equal(Result.AlreadyProtected, InstallDirectoryProtection.Apply(tree.Root));
        Assert.Equal(before, DirAcl(tree.Root).GetSecurityDescriptorBinaryForm());

        // What an update stages under the root afterwards inherits the three.
        var later = Path.Combine(tree.Current, "later.dll");
        File.WriteAllText(later, "x");
        AssertInheritsOnlyTargets(FileAcl(later));
        Assert.Equal(Targets.Length, Rules(FileAcl(later)).Count);
    }

    /// <summary>A file renamed in from outside the root keeps the inherited
    /// ACEs of the folder it came from, which stands in for a propagation that
    /// stopped partway: the root is already right while a deep file still
    /// carries the foreign grant. Startup must find it anywhere in the tree;
    /// the hooks write without looking.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AStaleInheritedAceDeepInTheTreeIsRewritten(bool alwaysWrite)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var tree = new Tree();
        Assert.Equal(Result.Applied, InstallDirectoryProtection.Apply(tree.Root));

        var stage = Directory.CreateDirectory(Path.Combine(tree.Parent, "stage")).FullName;
        var staged = Path.Combine(stage, "b.dll");
        File.WriteAllText(staged, "x");
        var deep = Path.Combine(tree.Current, "sub", "b.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(deep)!);
        File.Move(staged, deep);

        // Control: the moved file really carries the stale inherited grant,
        // and the root itself is still exactly the target.
        Assert.Contains(Rules(FileAcl(deep)), r => IsAddedUsersGrant(r, InheritanceFlags.None));
        AssertExactlyTargets(DirAcl(tree.Root));

        Assert.Equal(
            Result.Applied,
            InstallDirectoryProtection.Apply(tree.Root, alwaysWrite, out var error));
        Assert.Null(error);

        Assert.DoesNotContain(Rules(FileAcl(deep)), r => r.IdentityReference == Users);
        AssertInheritsOnlyTargets(FileAcl(deep));
        Assert.Equal(Result.AlreadyProtected, InstallDirectoryProtection.Apply(tree.Root));
    }

    [Fact]
    public void ExplicitForeignAceOnTheRootIsNotKept()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var tree = new Tree();
        var root = new DirectoryInfo(tree.Root);
        var acl = root.GetAccessControl();
        acl.AddAccessRule(new FileSystemAccessRule(
            Users, FileSystemRights.Write, Both, PropagationFlags.None, AccessControlType.Allow));
        root.SetAccessControl(acl);
        Assert.Contains(Rules(DirAcl(tree.Root)), r => r.IdentityReference == Users && !r.IsInherited);

        Assert.Equal(Result.Applied, InstallDirectoryProtection.Apply(tree.Root));

        AssertExactlyTargets(DirAcl(tree.Root));
    }

    [Fact]
    public void MissingRootIsSkipped()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var tree = new Tree();
        Assert.Equal(
            Result.SkippedMissing,
            InstallDirectoryProtection.Apply(Path.Combine(tree.Parent, "absent")));
    }

    /// <summary>A root owned by another account needs admin rights to build,
    /// so the owner rule is replaced through the seam.</summary>
    [Fact]
    public void ForeignOwnedRootIsSkippedWithoutWriting()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var tree = new Tree();
        var before = DirAcl(tree.Root).GetSecurityDescriptorBinaryForm();

        var result = InstallDirectoryProtection.ApplyForTesting(tree.Root, _ => false, alwaysWrite: false, out var error);

        Assert.Equal(Result.SkippedNotOwner, result);
        Assert.Null(error);
        Assert.Equal(before, DirAcl(tree.Root).GetSecurityDescriptorBinaryForm());
    }

    [Fact]
    public void JunctionRootFailsWithoutWriting()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var tree = new Tree();
        var link = Path.Combine(tree.Parent, "link");
        // A junction needs no admin rights (a symlink would).
        var psi = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        };
        foreach (var arg in new[] { "/c", "mklink", "/J", link, tree.Root })
        {
            psi.ArgumentList.Add(arg);
        }

        using (var mklink = Process.Start(psi)!)
        {
            mklink.StandardOutput.ReadToEnd();
            mklink.WaitForExit();
            Assert.Equal(0, mklink.ExitCode);
        }

        Assert.True(new DirectoryInfo(link).Attributes.HasFlag(FileAttributes.ReparsePoint));
        var target = DirAcl(tree.Root).GetSecurityDescriptorBinaryForm();

        var result = InstallDirectoryProtection.Apply(link, alwaysWrite: false, out var error);

        Assert.Equal(Result.Failed, result);
        Assert.Null(error);
        Assert.Equal(target, DirAcl(tree.Root).GetSecurityDescriptorBinaryForm());
        Assert.False(DirAcl(tree.Root).AreAccessRulesProtected);
    }

    private const InheritanceFlags Both = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

    private static DirectorySecurity DirAcl(string path) =>
        new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access);

    private static FileSecurity FileAcl(string path) =>
        new FileInfo(path).GetAccessControl(AccessControlSections.Access);

    private static List<FileSystemAccessRule> Rules(FileSystemSecurity security) =>
        security.GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>().ToList();

    /// <summary>Exactly the ACE <see cref="Tree"/> put on the parent, as
    /// inherited by a child (the temp directory may carry other grants).</summary>
    private static bool IsAddedUsersGrant(FileSystemAccessRule rule, InheritanceFlags inheritance) =>
        rule.IdentityReference == Users
        && rule.IsInherited
        && rule.AccessControlType == AccessControlType.Allow
        && rule.FileSystemRights.HasFlag(FileSystemRights.Modify)
        && rule.InheritanceFlags == inheritance;

    private static void AssertExactlyTargets(DirectorySecurity security)
    {
        Assert.True(security.AreAccessRulesProtected);
        var rules = Rules(security);
        Assert.Equal(Targets.Length, rules.Count);
        foreach (var rule in rules)
        {
            Assert.Contains(Targets, t => rule.IdentityReference == t);
            Assert.False(rule.IsInherited);
            Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
            Assert.Equal(FileSystemRights.FullControl, rule.FileSystemRights);
            Assert.Equal(Both, rule.InheritanceFlags);
            Assert.Equal(PropagationFlags.None, rule.PropagationFlags);
        }
    }

    private static void AssertInheritsOnlyTargets(FileSystemSecurity security)
    {
        var rules = Rules(security);
        Assert.NotEmpty(rules);
        Assert.All(rules, r =>
        {
            Assert.True(r.IsInherited);
            Assert.Contains(Targets, t => r.IdentityReference == t);
        });
    }

    /// <summary><c>&lt;temp&gt;\saacl-&lt;guid&gt;</c> with the Users grant,
    /// <c>root\current\x.exe</c> below it. Deletes only that directory.</summary>
    private sealed class Tree : IDisposable
    {
        public string Parent { get; } = Path.Combine(Path.GetTempPath(), "saacl-" + Guid.NewGuid().ToString("N"));
        public string Root => Path.Combine(Parent, "root");
        public string Current => Path.Combine(Root, "current");
        public string File => Path.Combine(Current, "x.exe");

        public Tree()
        {
            var parent = Directory.CreateDirectory(Parent);
            var acl = parent.GetAccessControl();
            acl.AddAccessRule(new FileSystemAccessRule(
                Users, FileSystemRights.Modify, Both, PropagationFlags.None, AccessControlType.Allow));
            parent.SetAccessControl(acl);

            Directory.CreateDirectory(Current);
            System.IO.File.WriteAllText(File, "x");
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Parent, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
