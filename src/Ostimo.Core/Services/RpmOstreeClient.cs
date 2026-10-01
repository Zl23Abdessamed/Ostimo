using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace Ostimo.Core.Services;

using Ostimo.Core.Models;

/// <summary>
/// The only class in Ostimo.Core that touches <see cref="Process"/> directly
/// for read operations (<c>status</c>, <c>db diff</c>). Privileged
/// operations (rollback, pin/unpin) are delegated to
/// <see cref="IPrivilegedActionRunner"/>, which owns the <c>pkexec</c>-specific
/// exit-code handling -- see RpmOstree.md §6.4-6.5 and §7 for the rationale.
///
/// Every process invocation uses <see cref="ProcessStartInfo.ArgumentList"/>,
/// never a manually interpolated command string, which avoids shell
/// injection entirely and sidesteps quoting bugs (RpmOstree.md §6.5).
/// </summary>
public sealed class RpmOstreeClient : IRpmOstreeClient
{
    private const string RpmOstreeExecutable = "rpm-ostree";

    private static readonly JsonSerializerOptions StatusJsonOptions = new()
    {
        // rpm-ostree's JSON is already exactly hyphen-cased per-field via
        // [JsonPropertyName] on the models, so no naming policy is needed
        // here -- this just keeps deserialization forgiving of fields the
        // models don't map (e.g. "cached-update", "update-driver", the many
        // "requested-*" arrays not surfaced in v1 -- see StatusResult.cs and
        // RpmOstree.md §5.1's field table).
        PropertyNameCaseInsensitive = false,
    };

    private readonly IPrivilegedActionRunner _privilegedActionRunner;

    public RpmOstreeClient(IPrivilegedActionRunner privilegedActionRunner)
    {
        _privilegedActionRunner = privilegedActionRunner;
    }

    public async Task<StatusResult> GetStatusAsync(CancellationToken ct = default)
    {
        var (exitCode, stdOut, stdErr) = await RunAsync(RpmOstreeExecutable, ["status", "--json"], ct);

        if (exitCode != 0)
        {
            throw new RpmOstreeCommandException($"{RpmOstreeExecutable} status --json", exitCode, stdErr);
        }

        try
        {
            var result = JsonSerializer.Deserialize<StatusResult>(stdOut, StatusJsonOptions);

            // A status command that succeeds (exit 0) but somehow produces
            // no parseable object at all is a real-but-unusual case (RpmOstree.md
            // §8.3) -- treat it as a command failure rather than letting a
            // null flow upward and NRE somewhere in the UI layer.
            return result ?? throw new RpmOstreeCommandException(
                $"{RpmOstreeExecutable} status --json", exitCode, "Command succeeded but produced no parseable JSON output.");
        }
        catch (JsonException ex)
        {
            throw new RpmOstreeCommandException(
                $"{RpmOstreeExecutable} status --json", exitCode, $"Failed to parse JSON output: {ex.Message}");
        }
    }

    public async Task<string> GetRawDiffAsync(string fromCommit, string toCommit, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fromCommit);
        ArgumentException.ThrowIfNullOrWhiteSpace(toCommit);

        var (exitCode, stdOut, stdErr) = await RunAsync(
            RpmOstreeExecutable, ["db", "diff", fromCommit, toCommit], ct);

        if (exitCode != 0)
        {
            throw new RpmOstreeCommandException(
                $"{RpmOstreeExecutable} db diff {fromCommit} {toCommit}", exitCode, stdErr);
        }

        // Deliberately returned raw/unparsed -- see IRpmOstreeClient's doc
        // comment. Turning this into a DeploymentDiff is IDeploymentDiffParser's
        // job.
        return stdOut;
    }

    public Task RollbackAsync(CancellationToken ct = default) =>
        _privilegedActionRunner.RunAsync("rollback", RpmOstreeExecutable, ["rollback"], ct);

    public Task PinAsync(int deploymentIndex, bool pin, CancellationToken ct = default)
    {
        if (deploymentIndex < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(deploymentIndex), deploymentIndex, "Deployment index cannot be negative.");
        }

        // Per RpmOstree.md §5.4, `ostree admin pin` (not `rpm-ostree`) is the
        // binary for this, and it operates on index, not commit hash. The
        // caller is responsible for computing deploymentIndex from a
        // freshly-fetched StatusResult immediately before calling this, to
        // avoid acting on a stale index -- see IRpmOstreeClient's doc comment.
        List<string> args = pin
            ? ["admin", "pin", deploymentIndex.ToString()]
            : ["admin", "pin", "--unpin", deploymentIndex.ToString()];

        return _privilegedActionRunner.RunAsync(pin ? "pin" : "unpin", "ostree", args, ct);
    }

    /// <summary>
    /// Runs an unprivileged command and captures its exit code, stdout, and
    /// stderr. See RpmOstree.md §6.5 for why this shape (redirected output,
    /// <c>ArgumentList</c>, no shell) is the safe way to shell out from C#.
    /// </summary>
    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(
        string fileName, IReadOnlyList<string> args, CancellationToken ct)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };

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
            // The executable itself couldn't be found -- almost certainly
            // means this isn't an rpm-ostree-managed system at all
            // (RpmOstree.md §8.3's first empty/error state). Surfaced as its
            // own exception type so the UI can show a dedicated "not an
            // rpm-ostree system" screen rather than a generic error.
            throw new RpmOstreeNotFoundException(fileName, ex);
        }

        var stdOutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stdErrTask = process.StandardError.ReadToEndAsync(ct);

        await process.WaitForExitAsync(ct);

        return (process.ExitCode, await stdOutTask, await stdErrTask);
    }
}
