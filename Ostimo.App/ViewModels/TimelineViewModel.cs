using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Ostimo.App.ViewModels;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Ostimo.Core.Models;
using Ostimo.Core.Services;

/// <summary>
/// Drives the left-hand "Deployments" pane (OstimoUi.md §3.1): the
/// newest-first list, the manual refresh control, and selection.
///
/// Owns the single source of truth for the current <see cref="StatusResult"/>
/// and rebuilds its <see cref="DeploymentViewModel"/> collection from scratch
/// on every refresh rather than diffing/mutating in place -- deliberately,
/// since <see cref="DeploymentViewModel.Index"/> and rollback-adjacency both
/// need to be recomputed from a freshly-fetched list every time (RpmOstree.md
/// §5.4's "avoid acting on a stale index" warning applies just as much to
/// adjacency as it does to pin index).
/// </summary>
public sealed partial class TimelineViewModel : ObservableObject
{
    private readonly IRpmOstreeClient _rpmOstreeClient;
    private readonly IDeploymentDiffParser _diffParser;

    /// <summary>
    /// Raised whenever a refresh completes successfully, so
    /// <see cref="MainWindowViewModel"/> can re-derive window-level state
    /// (the empty-deployments state, the transaction-in-progress banner) from
    /// the freshly-fetched <see cref="StatusResult"/> without this ViewModel
    /// needing to know about those concerns itself.
    /// </summary>
    public event EventHandler<StatusResult>? StatusRefreshed;

    public TimelineViewModel(IRpmOstreeClient rpmOstreeClient, IDeploymentDiffParser diffParser)
    {
        _rpmOstreeClient = rpmOstreeClient;
        _diffParser = diffParser;
    }

    /// <summary>
    /// The timeline rows, newest-first -- <c>status --json</c> already
    /// returns deployments in this order, so no client-side re-sorting is
    /// applied (OstimoUi.md §3.1).
    /// </summary>
    public ObservableCollection<DeploymentViewModel> Deployments { get; } = [];

    [ObservableProperty]
    private DeploymentViewModel? _selectedDeployment;

    /// <summary>
    /// True while a status refresh is in flight -- the view swaps the
    /// refresh icon for a spinner while this is true (OstimoUi.md §3.1),
    /// rather than showing a modal loading overlay that would block the
    /// person from looking at what's already on screen.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    private bool _isRefreshing;

    /// <summary>
    /// Set when a *manual* refresh fails after the timeline has already
    /// loaded successfully once. Per OstimoUi.md §6's "status refresh fails
    /// mid-session" row, the last-known-good timeline stays on screen; this
    /// only drives a small transient error toast/banner, not a full-pane
    /// replacement.
    /// </summary>
    [ObservableProperty]
    private string? _refreshErrorMessage;

    /// <summary>
    /// True once a first load has completed (successfully or not) --
    /// <see cref="MainWindowViewModel"/> uses this to distinguish "still
    /// loading for the first time" from "a later refresh failed", since
    /// those two situations warrant different full-pane vs. toast
    /// treatments (OstimoUi.md §6).
    /// </summary>
    public bool HasLoadedOnce { get; private set; }

    /// <summary>
    /// Fetches (or re-fetches) the deployment list and rebuilds
    /// <see cref="Deployments"/>. Called once on startup and again whenever
    /// the person clicks the refresh button, and after a successful
    /// rollback/pin action (OstimoUi.md §5.2's "refresh the deployment list"
    /// requirement).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRefresh))]
    public async Task RefreshAsync()
    {
        IsRefreshing = true;

        try
        {
            var status = await _rpmOstreeClient.GetStatusAsync();
            await RebuildFromStatusAsync(status);
            RefreshErrorMessage = null;
            StatusRefreshed?.Invoke(this, status);
        }
        catch (Exception ex) when (ex is RpmOstreeCommandException or RpmOstreeNotFoundException)
        {
            if (HasLoadedOnce)
            {
                // Keep showing the last-known-good timeline (OstimoUi.md
                // §6) -- don't touch Deployments at all, just surface the
                // failure as a small toast/banner via this message.
                RefreshErrorMessage = $"Couldn't refresh: {DescribeFailure(ex)}";
            }
            else
            {
                // First load failed outright -- there's nothing "last known
                // good" to preserve. MainWindowViewModel is responsible for
                // showing the appropriate full-window empty state
                // (RpmOstreeNotFoundException) by re-throwing/observing this,
                // so it's rethrown here rather than swallowed.
                throw;
            }
        }
        finally
        {
            IsRefreshing = false;
            HasLoadedOnce = true;
        }
    }

    private bool CanRefresh() => !IsRefreshing;

    private static string DescribeFailure(Exception ex) => ex switch
    {
        RpmOstreeCommandException cmd => cmd.StandardError,
        RpmOstreeNotFoundException notFound => notFound.Message,
        _ => ex.Message,
    };

    private async Task RebuildFromStatusAsync(StatusResult status)
    {
        var previouslySelectedId = SelectedDeployment?.Model.Id;

        // Detach the old rows' event subscriptions before discarding them.
        foreach (var old in Deployments)
        {
            old.DeploymentsChanged -= OnChildDeploymentsChanged;
        }

        Deployments.Clear();

        // Build every row first (so each one's CompareTargets/adjacency can
        // see the full sibling list), then wire cross-cutting state
        // (adjacency, event subscription) in a second pass.
        var newRows = new List<DeploymentViewModel>(status.Deployments.Count);
        for (var i = 0; i < status.Deployments.Count; i++)
        {
            newRows.Add(new DeploymentViewModel(status.Deployments[i], i, newRows, _rpmOstreeClient, _diffParser));
        }

        // CompareTargets above is populated as each row is constructed, but
        // since it needs *every* sibling (including ones constructed after
        // it), rebuild it properly now that the full list exists. This is
        // simplest as a second construction pass rather than a mutable
        // CompareTargets list, keeping DeploymentViewModel's public surface
        // immutable-looking.
        newRows.Clear();
        for (var i = 0; i < status.Deployments.Count; i++)
        {
            newRows.Add(new DeploymentViewModel(status.Deployments[i], i, newRows, _rpmOstreeClient, _diffParser));
        }

        ApplyRollbackAdjacency(newRows);

        foreach (var row in newRows)
        {
            row.DeploymentsChanged += OnChildDeploymentsChanged;
            Deployments.Add(row);
        }

        foreach (var row in newRows)
        {
            await row.InitializeAsync();
        }

        // Preserve selection across a refresh where possible (by deployment
        // id, since index/serial can shift); otherwise default to the
        // booted deployment (OstimoUi.md §3.1 -- "booted deployment is
        // selected by default on first load").
        SelectedDeployment =
            (previouslySelectedId is not null
                ? Deployments.FirstOrDefault(d => d.Model.Id == previouslySelectedId)
                : null)
            ?? Deployments.FirstOrDefault(d => d.Model.Booted)
            ?? Deployments.FirstOrDefault();
    }

    /// <summary>
    /// Computes, for each row, whether it sits immediately above or below
    /// the booted deployment in the newest-first list -- the only case
    /// <c>rpm-ostree rollback</c> can actually honor (RpmOstree.md §5.3,
    /// OstimoUi.md §5.1). Done here, once per refresh, rather than inside
    /// <see cref="DeploymentViewModel"/> itself, since adjacency is a
    /// property of the row's position in the whole list, not something a
    /// single row can determine about itself.
    /// </summary>
    private static void ApplyRollbackAdjacency(IReadOnlyList<DeploymentViewModel> rows)
    {
        var bootedIndex = -1;
        for (var i = 0; i < rows.Count; i++)
        {
            if (rows[i].Model.Booted)
            {
                bootedIndex = i;
                break;
            }
        }

        if (bootedIndex == -1)
        {
            // No booted deployment at all (shouldn't happen per the
            // rpm-ostree contract, but StatusResult.Booted is deliberately
            // defensive about this -- RpmOstree.md §8.3) -- nothing is
            // adjacent to a booted deployment that doesn't exist.
            foreach (var row in rows)
            {
                row.CanRollback = false;
            }

            return;
        }

        for (var i = 0; i < rows.Count; i++)
        {
            var isAdjacent = i == bootedIndex - 1 || i == bootedIndex + 1;
            rows[i].CanRollback = isAdjacent && i != bootedIndex;
        }

        // The booted deployment itself is never a rollback target -- there's
        // nothing to roll back *to* from itself.
        rows[bootedIndex].CanRollback = false;
    }

    private void OnChildDeploymentsChanged(object? sender, EventArgs e) => _ = RefreshAsync();
}
