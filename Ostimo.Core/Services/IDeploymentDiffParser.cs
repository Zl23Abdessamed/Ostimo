namespace Ostimo.Core.Services;

using Ostimo.Core.Models;

/// <summary>
/// Parses the plain-text output of <c>rpm-ostree db diff</c> into a
/// <see cref="DeploymentDiff"/>.
///
/// There is no <c>--json</c> flag for <c>db diff</c> (confirmed in
/// RpmOstree.md §5.2), so this is a line-based text parser rather than a
/// JSON deserializer. Kept behind an interface so it's trivial to unit test
/// with captured sample output, and easy to swap out if rpm-ostree ever
/// grows a structured output mode.
/// </summary>
public interface IDeploymentDiffParser
{
    /// <summary>
    /// Parses raw <c>rpm-ostree db diff</c> stdout into a <see cref="DeploymentDiff"/>.
    /// </summary>
    /// <param name="fromChecksum">
    /// The commit checksum the diff was computed *from* (the older/base side).
    /// Not present in <paramref name="rawDbDiffOutput"/> itself -- the caller
    /// supplies it, since it's the caller who invoked <c>db diff</c> with
    /// these two commits in the first place.
    /// </param>
    /// <param name="toChecksum">
    /// The commit checksum the diff was computed *to* (the newer/target side).
    /// </param>
    /// <param name="rawDbDiffOutput">
    /// The raw stdout of <c>rpm-ostree db diff &lt;fromChecksum&gt; &lt;toChecksum&gt;</c>.
    /// May be empty (no differences), or contain any subset of the
    /// "Upgraded:", "Added:", and "Removed:" sections in any order.
    /// </param>
    DeploymentDiff Parse(string fromChecksum, string toChecksum, string rawDbDiffOutput);
}
