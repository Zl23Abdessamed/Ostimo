using System.ComponentModel;
using System.Diagnostics;

namespace Ostimo.Core.Services;

/// <summary>
/// Wraps <c>pkexec</c> invocations for the app's two privileged operations
/// (rollback, pin/unpin). Kept as its own class/interface, separate from
/// <see cref="RpmOstreeClient"/>, so the polkit-specific exit-code
/// conventions live in exactly one place rather than being duplicated (or,
/// worse, half-duplicated) across every privileged call site.
///
/// See RpmOstree.md §7 for the polkit rationale: this is the "ask nicely via
/// the system's normal authentication dialog" approach, not
/// <c>sudo</c>-in-a-hidden-terminal and never "run the whole GUI as root".
/// </summary>
public interface IPrivilegedActionRunner
{
    /// <summary>
    /// Runs <c>pkexec &lt;fileName&gt; &lt;args...&gt;</c> and throws if it
    /// didn't succeed.
    /// </summary>
    /// <param name="actionName">
    /// A short, human-readable name for the action (e.g. "rollback", "pin"),
    /// used only to make thrown exceptions more useful -- not passed to
    /// <c>pkexec</c> itself.
    /// </param>
    /// <param name="fileName">The program pkexec should run as root, e.g. "rpm-ostree".</param>
    /// <param name="args">
    /// Arguments for <paramref name="fileName"/>. Always passed through
    /// <see cref="ProcessStartInfo.ArgumentList"/>, never string-concatenated
    /// -- see RpmOstree.md §7.2's explicit warning against that.
    /// </param>
    /// <exception cref="PrivilegedActionDeclinedException">
    /// Thrown when the polkit authentication dialog was dismissed/cancelled,
    /// or authorization otherwise couldn't be obtained.
    /// </exception>
    /// <exception cref="RpmOstreeCommandException">
    /// Thrown when authentication succeeded but the underlying command
    /// itself exited non-zero.
    /// </exception>
    /// <exception cref="RpmOstreeNotFoundException">
    /// Thrown when <c>pkexec</c> itself can't be found -- e.g. no polkit
    /// installed on this system.
    /// </exception>
    Task RunAsync(string actionName, string fileName, IReadOnlyList<string> args, CancellationToken ct = default);
}

/// <summary>
/// Default <see cref="IPrivilegedActionRunner"/>, backed by
/// <c>pkexec</c>.
///
/// Exit-code contract, per <c>man pkexec</c>: on success, pkexec returns
/// whatever the wrapped program returned. If authorization could not be
/// obtained through authentication, or the calling process simply isn't
/// authorized, or some other error occurred, pkexec itself exits 127. If
/// authorization specifically failed because the user dismissed the
/// authentication dialog, pkexec exits 126. Those two codes are therefore
/// reserved by pkexec's own contract and are treated here as "the user
/// declined/failed to authenticate", never as a legitimate exit code from
/// the wrapped program (<c>rpm-ostree</c>/<c>ostree</c> exiting 126 or 127
/// on their own account would be exceedingly unusual, and conflating that
/// with an auth failure is an acceptable, clearly-documented trade-off for
/// how much simpler it keeps the exit-code handling here).
/// </summary>
public sealed class PrivilegedActionRunner : IPrivilegedActionRunner
{
    private const string PkexecExecutable = "pkexec";
    private const int PkexecNotAuthorizedExitCode = 127;
    private const int PkexecDialogDismissedExitCode = 126;

    public async Task RunAsync(string actionName, string fileName, IReadOnlyList<string> args, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actionName);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = PkexecExecutable,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };

        // pkexec's own first argument is the program to run as root;
        // everything after that is that program's arguments. All added via
        // ArgumentList -- see RpmOstree.md §7.2.
        process.StartInfo.ArgumentList.Add(fileName);
        foreach (var arg in args)
        {
            process.StartInfo.ArgumentList.Add(arg);
        }

        try
        {
            process.Start();
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 2 /* ENOENT */)
        {
            // pkexec itself is missing -- e.g. no polkit on this system.
            // Distinct from "the target program is missing", which would
            // show up as pkexec exiting 127, not as a failure to start
            // pkexec at all.
            throw new RpmOstreeNotFoundException(PkexecExecutable, ex);
        }

        var stdOutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stdErrTask = process.StandardError.ReadToEndAsync(ct);

        await process.WaitForExitAsync(ct);

        var exitCode = process.ExitCode;
        var stdErr = await stdErrTask;
        // stdout isn't needed for these fire-and-forget privileged commands,
        // but it's still read to completion so the process's output buffer
        // can't fill up and deadlock the pipe.
        _ = await stdOutTask;

        switch (exitCode)
        {
            case 0:
                return;

            case PkexecDialogDismissedExitCode:
                throw new PrivilegedActionDeclinedException(actionName, "the authentication dialog was dismissed.");

            case PkexecNotAuthorizedExitCode:
                throw new PrivilegedActionDeclinedException(actionName, "authorization could not be obtained.");

            default:
                // pkexec succeeded in running the target program, but the
                // program itself failed -- surface this as a normal command
                // failure, not an auth problem.
                throw new RpmOstreeCommandException($"{PkexecExecutable} {fileName}", exitCode, stdErr);
        }
    }
}
