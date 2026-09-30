using System.Text.Json.Serialization;

namespace Ostimo.Core.Models;

// Note: FirstOrDefault below relies on System.Linq. Assumes <ImplicitUsings>enable</ImplicitUsings>
// in the .csproj (the .NET default for new projects) so System.Linq is available without an
// explicit using. If ImplicitUsings is off, add `using System.Linq;` at the top of this file.
/// <summary>
/// Represents the result of <c>rpm-ostree status --json</c>: the full list of
/// deployments plus whether an rpm-ostree transaction is currently running.
///
/// Confirmed real top-level keys (Aurora DX): "deployments", "transaction",
/// "cached-update", "update-driver". Only the first two matter for the
/// timeline; "cached-update" and "update-driver" are left off this model
/// deliberately -- v1 doesn't do update-checking (see RpmOstree.md §4,
/// non-goals), so there's nothing here yet that needs them.
/// </summary>
public sealed record StatusResult
{
    [JsonPropertyName("deployments")]
    public required IReadOnlyList<Deployment> Deployments { get; init; } = [];

    /// <summary>
    /// True when rpm-ostree reports a transaction (e.g. an in-progress
    /// upgrade or install) already running. The JSON field is either
    /// <c>null</c> or an object describing the transaction -- this flattens
    /// that into a simple bool since v1 only needs to know whether to disable
    /// actions and show a "busy" state, not the transaction's details.
    /// </summary>
    [JsonIgnore]
    public bool TransactionInProgress => RawTransaction is not null;

    [JsonPropertyName("transaction")]
    public object? RawTransaction { get; init; }

    /// <summary>
    /// The currently booted deployment, if any. Should always be exactly one
    /// per the rpm-ostree contract, but this stays defensive (returns null
    /// rather than throwing) since a status command run mid-transaction or on
    /// an unusual system is a real case worth handling gracefully rather than
    /// crashing on (see RpmOstree.md §8.3, empty/error states).
    /// </summary>
    [JsonIgnore]
    public Deployment? Booted => Deployments.FirstOrDefault(d => d.Booted);
}