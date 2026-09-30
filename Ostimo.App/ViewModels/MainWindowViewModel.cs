using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Threading.Tasks;

namespace Ostimo.App.ViewModels;

using Ostimo.Core.Models;
using Ostimo.Core.Services;

/// <summary>
/// The top-level window ViewModel. Owns a <see cref="TimelineViewModel"/> and
/// exposes whichever <see cref="DeploymentViewModel"/> is currently selected
/// as the detail pane's content (RpmOstree.md §6.2's classic master/detail
/// split) -- plus the window-wide states that sit above both panes: the
/// first-load "not an rpm-ostree system" screen, the "zero deployments"
/// placeholder, and the persistent transaction-in-progress banner
/// (OstimoUi.md §6).
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly IRpmOstreeClient _rpmOstreeClient;

    public MainWindowViewModel(
        IRpmOstreeClient rpmOstreeClient,
        IDeploymentDiffParser diffParser)
    {
        _rpmOstreeClient = rpmOstreeClient;
        Timeline = new TimelineViewModel(rpmOstreeClient, diffParser);
        Timeline.StatusRefreshed += OnStatusRefreshed;
        Timeline.PropertyChanged += OnTimelinePropertyChanged;
    }

    public TimelineViewModel Timeline { get; }

    /// <summary>
    /// Convenience passthrough so the view can bind directly to
    /// <c>MainWindowViewModel.SelectedDeployment</c> for the detail pane,
    /// without reaching two levels deep into <c>Timeline.SelectedDeployment</c>
    /// every time.
    /// </summary>
    public DeploymentViewModel? SelectedDeployment => Timeline.SelectedDeployment;

    // ----- Window-level load state (OstimoUi.md §6) -----

    /// <summary>
    /// True until the very first <see cref="TimelineViewModel.RefreshAsync"/>
    /// call resolves (success or failure). While true, the view shows
    /// neither the normal layout nor an error screen -- just whatever
    /// initial-load placeholder feels least jarring (a blank pane or a
    /// small spinner is fine here; OstimoUi.md doesn't specify this exact
    /// instant, only the states that follow it).
    /// </summary>
    [ObservableProperty]
    private bool _isInitialLoad = true;

    /// <summary>
    /// True when the very first load failed because
    /// <see cref="RpmOstreeNotFoundException"/> was thrown -- i.e. this
    /// genuinely isn't an rpm-ostree-managed system. Per OstimoUi.md §6,
    /// this replaces the entire timeline+detail layout with a full-window
    /// centered empty state and has no retry button, since the situation
    /// won't resolve itself without a different OS.
    /// </summary>
    [ObservableProperty]
    private bool _isNotAnRpmOstreeSystem;

    /// <summary>
    /// The exact copy for the "not an rpm-ostree system" state (OstimoUi.md
    /// §6's table) -- kept here rather than hardcoded in the view so the
    /// wording lives in one place.
    /// </summary>
    public string NotAnRpmOstreeSystemHeading => "Ostimo needs rpm-ostree";

    public string NotAnRpmOstreeSystemBody =>
        "This system doesn't appear to be running rpm-ostree. Ostimo only works on " +
        "OSTree-based distros like Aurora, Bazzite, Bluefin, or Fedora Silverblue/Kinoite.";

    /// <summary>
    /// True once loaded successfully but with zero deployments in the
    /// result -- an unusual-but-handled case (OstimoUi.md §6, RpmOstree.md
    /// §10). The timeline pane shows a quiet inline message instead of an
    /// empty list, and the detail pane shows a matching neutral placeholder.
    /// </summary>
    public bool HasZeroDeployments => !IsInitialLoad && !IsNotAnRpmOstreeSystem && Timeline.Deployments.Count == 0;

    /// <summary>
    /// Inverse of <see cref="HasZeroDeployments"/> — true when there
    /// are deployments to display. Used so child views can bind to a
    /// positive condition without needing negation in their binding
    /// expressions (OstimoUi.md §6).
    /// </summary>
    public bool HasContent => !HasZeroDeployments;

    // ----- Transaction-in-progress banner (OstimoUi.md §6) -----

    /// <summary>
    /// True whenever the most recently fetched <see cref="StatusResult"/>
    /// reports an in-progress transaction. Drives a persistent banner across
    /// the *whole window* (not just one pane), and disables rollback/pin
    /// actions app-wide while true -- the banner itself carries its own
    /// manual refresh affordance rather than auto-polling (OstimoUi.md §6,
    /// consistent with RpmOstree.md §9's no-auto-refresh non-goal).
    /// </summary>
    [ObservableProperty]
    private bool _transactionInProgress;

    public string TransactionBannerMessage =>
        "rpm-ostree is currently running an update. Actions are disabled until it finishes.";

    /// <summary>
    /// Bound to the banner's own refresh button (OstimoUi.md §6) -- a
    /// separate command from <see cref="TimelineViewModel.RefreshCommand"/>
    /// only in the sense that it's reachable from the banner; it drives the
    /// exact same underlying refresh.
    /// </summary>
    [RelayCommand]
    private Task RefreshFromBannerAsync() => Timeline.RefreshCommand.ExecuteAsync(null);

    // ----- Startup -----

    /// <summary>
    /// Called once by the view (e.g. from the window's constructor or a
    /// <c>Loaded</c> handler) to kick off the very first load. Not done
    /// inside this ViewModel's own constructor since it's async and
    /// constructors can't await.
    /// </summary>
    public async Task InitializeAsync()
    {
        try
        {
            await Timeline.RefreshAsync();
        }
        catch (RpmOstreeNotFoundException)
        {
            IsNotAnRpmOstreeSystem = true;
        }
        finally
        {
            IsInitialLoad = false;
            OnPropertyChanged(nameof(HasZeroDeployments));
        }
    }

    private void OnStatusRefreshed(object? sender, StatusResult status)
    {
        TransactionInProgress = status.TransactionInProgress;
        OnPropertyChanged(nameof(HasZeroDeployments));
    }

    private void OnTimelinePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TimelineViewModel.SelectedDeployment))
        {
            OnPropertyChanged(nameof(SelectedDeployment));
        }
    }
}
