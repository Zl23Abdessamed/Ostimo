namespace Ostimo.Core.Models;

// Note: Added/Removed/Upgraded below use System.Linq (Where). Assumes
// <ImplicitUsings>enable</ImplicitUsings> in the .csproj; add an explicit
// `using System.Linq;` if that's not set.

/// <summary>
/// The kind of change a single package underwent between two deployments, as
/// reported by <c>rpm-ostree db diff</c>.
/// </summary>
public enum PackageChangeKind
{
    Added,
    Removed,
    Upgraded,
}

/// <summary>
/// One package's change between two deployments.
///
/// <c>rpm-ostree db diff</c> has no <c>--json</c> flag (confirmed in
/// RpmOstree.md §5.2), so this is produced by <c>IDeploymentDiffParser</c>
/// parsing plain text like:
///
/// <code>
/// Upgraded:
///   kernel 6.12.1-200.fc40.x86_64 -&gt; 6.12.3-200.fc40.x86_64
/// Added:
///   ripgrep 14.1.0-2.fc40.x86_64
/// Removed:
///   some-old-package 1.0-1.fc40.x86_64
/// </code>
///
/// For Added/Removed, only <see cref="NewVersion"/> or <see cref="OldVersion"/>
/// (respectively) is populated -- the other is null, not an empty string, so
/// callers can distinguish "no version" from "version wasn't parsed".
/// </summary>
public sealed record PackageChange(
    PackageChangeKind Kind,
    string Name,
    string? OldVersion,
    string? NewVersion
);

/// <summary>
/// The full set of package changes between two deployments, identified by
/// their OSTree commit checksums.
/// </summary>
public sealed record DeploymentDiff(
    string FromChecksum,
    string ToChecksum,
    IReadOnlyList<PackageChange> Changes
)
{
    public IEnumerable<PackageChange> Added => Changes.Where(c => c.Kind == PackageChangeKind.Added);
    public IEnumerable<PackageChange> Removed => Changes.Where(c => c.Kind == PackageChangeKind.Removed);
    public IEnumerable<PackageChange> Upgraded => Changes.Where(c => c.Kind == PackageChangeKind.Upgraded);

    public bool IsEmpty => Changes.Count == 0;
}