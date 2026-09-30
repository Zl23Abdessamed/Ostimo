namespace Ostimo.Core.Services;

using Ostimo.Core.Models;

/// <summary>
/// Talks to the <c>rpm-ostree</c> CLI (and, for privileged operations,
/// <c>pkexec</c>). This is the only interface in the app that should ever
/// need to know a real rpm-ostree/ostree system is involved -- everything
/// above it (ViewModels, the diff parser) depends on this interface, not on
/// <see cref="System.Diagnostics.Process"/> directly, so a fake
/// implementation can stand in for tests or UI development without a real
/// Aurora/bootc machine.
///
/// See RpmOstree.md §6.4-6.5 for the rationale and the safe process-running
/// pattern this is built on (always <c>ArgumentList</c>, never a
/// shell-interpolated command string).
/// </summary>
public interface IRpmOstreeClient
{
    /// <summary>
    /// Runs <c>rpm-ostree status --json</c> and deserializes it into a
    /// <see cref="StatusResult"/>.
    /// </summary>
    /// <exception cref="RpmOstreeNotFoundException">
    /// Thrown when the <c>rpm-ostree</c> binary itself can't be found --
    /// i.e. this isn't an rpm-ostree-managed system at all (RpmOstree.md
    /// §8.3, first empty/error state to design for).
    /// </exception>
    /// <exception cref="RpmOstreeCommandException">
    /// Thrown when the command runs but exits non-zero, or produces output
    /// that can't be parsed as the expected JSON shape.
    /// </exception>
    Task<StatusResult> GetStatusAsync(CancellationToken ct = default);

    /// <summary>
    /// Runs <c>rpm-ostree db diff &lt;fromCommit&gt; &lt;toCommit&gt;</c> and
    /// returns its raw stdout, unparsed. Deliberately returns text rather
    /// than a <see cref="Models.DeploymentDiff"/> -- there is no
    /// <c>--json</c> flag for this command (RpmOstree.md §5.2), so turning
    /// the text into a structured diff is <see cref="IDeploymentDiffParser"/>'s
    /// job, kept separate so each piece can be tested (and replaced) on its
    /// own.
    /// </summary>
    /// <exception cref="RpmOstreeNotFoundException">
    /// Thrown when the <c>rpm-ostree</c> binary can't be found.
    /// </exception>
    /// <exception cref="RpmOstreeCommandException">
    /// Thrown when the command exits non-zero (e.g. an unknown/invalid
    /// commit checksum).
    /// </exception>
    Task<string> GetRawDiffAsync(string fromCommit, string toCommit, CancellationToken ct = default);

    /// <summary>
    /// Rolls back to the previous deployment via
    /// <c>pkexec rpm-ostree rollback</c>. Per RpmOstree.md §5.3, this swaps
    /// deployment order rather than targeting a specific commit -- there is
    /// no "roll back to this exact one" if it isn't already adjacent, and
    /// that limitation is the caller's (ViewModel/UI's) responsibility to be
    /// honest about, not something this method can work around.
    /// </summary>
    /// <exception cref="PrivilegedActionDeclinedException">
    /// Thrown when the user cancels or fails the polkit authentication
    /// prompt.
    /// </exception>
    /// <exception cref="RpmOstreeCommandException">
    /// Thrown when the command runs (post-authentication) but exits non-zero.
    /// </exception>
    Task RollbackAsync(CancellationToken ct = default);

    /// <summary>
    /// Pins or unpins the deployment currently at <paramref name="deploymentIndex"/>
    /// via <c>pkexec ostree admin pin [--unpin] &lt;index&gt;</c>.
    /// </summary>
    /// <param name="deploymentIndex">
    /// Position in the <c>rpm-ostree status</c> deployment array, computed by
    /// the caller from a freshly-fetched <see cref="StatusResult"/>
    /// immediately before calling this -- per RpmOstree.md §5.4, pin/unpin
    /// operate on index, not commit hash, so a stale index could silently
    /// pin the wrong deployment if the list changed underneath the caller.
    /// </param>
    /// <param name="pin"><c>true</c> to pin, <c>false</c> to unpin.</param>
    /// <exception cref="PrivilegedActionDeclinedException">
    /// Thrown when the user cancels or fails the polkit authentication
    /// prompt.
    /// </exception>
    /// <exception cref="RpmOstreeCommandException">
    /// Thrown when the command runs (post-authentication) but exits non-zero.
    /// </exception>
    Task PinAsync(int deploymentIndex, bool pin, CancellationToken ct = default);
}

/// <summary>
/// The <c>rpm-ostree</c> binary itself could not be found on <c>PATH</c> --
/// this almost certainly means the app is running on a non-rpm-ostree
/// system. Callers should treat this as an expected, user-facing empty
/// state (RpmOstree.md §8.3), not an unexpected crash.
/// </summary>
public sealed class RpmOstreeNotFoundException(string fileName, Exception? innerException = null)
    : Exception($"Could not find the '{fileName}' executable. This system may not be managed by rpm-ostree.", innerException)
{
    public string FileName { get; } = fileName;
}

/// <summary>
/// An <c>rpm-ostree</c> (or <c>ostree</c>) command ran but exited with a
/// non-zero status, or otherwise failed to produce the expected output.
/// Carries the exit code and stderr so the UI can show something more
/// useful than a generic failure message (RpmOstree.md §6.5).
/// </summary>
public sealed class RpmOstreeCommandException(string command, int exitCode, string standardError)
    : Exception($"'{command}' exited with code {exitCode}: {standardError}")
{
    public string Command { get; } = command;
    public int ExitCode { get; } = exitCode;
    public string StandardError { get; } = standardError;
}

/// <summary>
/// A privileged action (rollback, pin/unpin) did not complete because the
/// polkit authentication prompt was cancelled or failed. Distinguished from
/// <see cref="RpmOstreeCommandException"/> so the UI can show "you cancelled
/// the authentication prompt" rather than a generic command-failure message.
/// </summary>
public sealed class PrivilegedActionDeclinedException(string action, string? details = null)
    : Exception(details is null
        ? $"Authentication for '{action}' was cancelled or failed."
        : $"Authentication for '{action}' was cancelled or failed: {details}")
{
    public string Action { get; } = action;
}
