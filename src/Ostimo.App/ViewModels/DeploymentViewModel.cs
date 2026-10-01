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
/// One selectable row in the timeline list (OstimoUi.md §3.1), and -- when
/// selected -- the source of everything shown in the right-hand detail pane
/// (§3.2). This is the app's "master/detail" leaf: <see cref="TimelineViewModel"/>
/// holds a collection of these, and <see cref="MainWindowViewModel"/> shows
/// whichever one is currently selected.
///
/// Deliberately holds the underlying <see cref="Deployment"/> immutable model
/// plus display-ready derived properties, rather than duplicating its fields
/// as separate observable properties -- nothing about a single deployment's
/// own data changes after it's fetched; only the diff-comparison state and
/// in-flight action state below are genuinely observable.
/// </summary>
public sealed partial class DeploymentViewModel : ObservableObject
{
    private readonly IRpmOstreeClient _rpmOstreeClient;
    private readonly IDeploymentDiffParser _diffParser;

    /// <summary>
    /// Raised after a rollback or pin/unpin succeeds, so
    /// <see cref="MainWindowViewModel"/> knows to refresh the whole timeline
    /// (OstimoUi.md §5.2 -- "refresh the deployment list ... rather than
    /// only updating the detail pane in isolation").
    /// </summary>
    public event EventHandler? DeploymentsChanged;

    public DeploymentViewModel(
        Deployment deployment,
        int index,
        IReadOnlyList<DeploymentViewModel> allDeployments,
        IRpmOstreeClient rpmOstreeClient,
        IDeploymentDiffParser diffParser)
    {
        Model = deployment;
        Index = index;
        _rpmOstreeClient = rpmOstreeClient;
        _diffParser = diffParser;

        // "Compare against" defaults to the booted deployment (OstimoUi.md
        // §3.2), unless *this* deployment IS the booted one, in which case
        // comparing against itself would be a degenerate always-empty diff --
        // fall back to the next-most-recent deployment instead.
        var bootedCandidate = allDeployments.FirstOrDefault(d => d.Model.Booted && d.Model.Id != deployment.Id);
        var fallbackCandidate = allDeployments
            .Where(d => d.Model.Id != deployment.Id)
            .OrderByDescending(d => d.Model.Timestamp)
            .FirstOrDefault();

        CompareTargets = allDeployments.Where(d => d.Model.Id != deployment.Id).ToList();
        _compareAgainst = bootedCandidate ?? fallbackCandidate;

        Diff = DiffViewModel.CreateLoading();
    }

    /// <summary>
    /// The underlying data this row/detail view represents.
    /// </summary>
    public Deployment Model { get; }

    /// <summary>
    /// Position in the <c>rpm-ostree status</c> deployment array *at the time
    /// this ViewModel was built*. Passed straight through to
    /// <see cref="IRpmOstreeClient.PinAsync"/>, which needs an index rather
    /// than a commit hash (RpmOstree.md §5.4) -- because a stale index could
    /// silently pin the wrong deployment, this ViewModel (and the whole
    /// <see cref="TimelineViewModel"/> collection it belongs to) is
    /// rebuilt from scratch on every refresh rather than mutated in place.
    /// </summary>
    public int Index { get; }

    // ----- Display-ready formatting (OstimoUi.md §3.1, §3.2) -----

    public string Version => Model.Version;

    public bool IsBooted => Model.Booted;

    public bool IsPinned => Model.Pinned;

    public bool IsStaged => Model.Staged;

    /// <summary>
    /// True when this deployment is booted AND pinned — the view shows
    /// the booted glyph as primary with a small pin badge overlay.
    /// </summary>
    public bool ShowPinBadgeOverlay => IsBooted && IsPinned;

    /// <summary>
    /// True for the default/unfilled circle glyph in the timeline row.
    /// Shown when this deployment is neither booted, pinned, nor staged —
    /// the plain "not special" state (OstimoUi.md §3.1).
    /// </summary>
    public bool ShowPlainOutlineGlyph => !IsBooted && !IsPinned && !IsStaged;

    /// <summary>
    /// True when this deployment is pinned but NOT booted, so the pin
    /// glyph replaces the filled circle entirely (never shown alongside
    /// the booted dot — see <see cref="ShowPinBadgeOverlay"/>).
    /// </summary>
    public bool IsPinnedGlyphPrimary => IsPinned && !IsBooted;

    /// <summary>
    /// Full commit hash, for clipboard copy (OstimoUi.md §3.2 -- the copy
    /// button copies the *full* checksum even though only a truncated form
    /// is displayed, since that's what <c>db diff</c> actually needs).
    /// </summary>
    public string FullChecksum => Model.Checksum;

    /// <summary>
    /// "first6…last6" truncated form for display (OstimoUi.md §3.2's
    /// metadata table). Falls back to the full string if it's already short
    /// enough that truncating it wouldn't save anything.
    /// </summary>
    public string TruncatedChecksum
    {
        get
        {
            const int prefixLength = 6;
            const int suffixLength = 6;
            var checksum = Model.Checksum;

            if (checksum.Length <= prefixLength + suffixLength)
            {
                return checksum;
            }

            return $"{checksum[..prefixLength]}…{checksum[^suffixLength..]}";
        }
    }

    /// <summary>
    /// The parsed, human-readable image name (e.g. "aurora-dx"), per
    /// OstimoUi.md §3.2 -- the metadata table shows this, not the raw
    /// <c>ostree-image-signed:docker://...</c> reference.
    /// </summary>
    public string ImageName => Model.ImageShortName;

    /// <summary>
    /// The raw, untruncated image reference -- kept accessible (e.g. via a
    /// tooltip) per OstimoUi.md §3.2, even though it's not the primary
    /// display value.
    /// </summary>
    public string FullImageReference => Model.ContainerImageReference;

    /// <summary>
    /// "MMM d, yyyy, HH:mm" in the user's local time zone (OstimoUi.md
    /// §3.2's "Built" row) -- <see cref="DateTimeOffset.LocalDateTime"/>
    /// already carries out the conversion; this never displays a raw Unix
    /// integer or silently-UTC time.
    /// </summary>
    public string BuiltDisplay => Model.Timestamp.LocalDateTime.ToString("MMM d, yyyy, HH:mm");

    /// <summary>
    /// The timeline row's secondary line (OstimoUi.md §3.1): "Booted · Sep 22",
    /// "Pinned · Aug 30", "Staged · pending reboot" (§3.4), or just the date
    /// with no status word for a plain deployment.
    ///
    /// Note this is the one place OstimoUi.md's own mockup uses a
    /// middle-dot-joined string ("Booted · Sep 22") despite §2.4's general
    /// "no middle-dot metadata" rule -- §3.1 spells out this exact format
    /// for the secondary line specifically, so it's followed literally here
    /// rather than reinterpreted as a violation of the general rule.
    /// </summary>
    public string SecondaryLine
    {
        get
        {
            if (IsStaged)
            {
                return "Staged · pending reboot";
            }

            var date = RelativeFriendlyDate;

            if (IsBooted)
            {
                return $"Booted · {date}";
            }

            if (IsPinned)
            {
                return $"Pinned · {date}";
            }

            return date;
        }
    }

    /// <summary>
    /// "MMM d" for the current calendar year, "MMM d, yyyy" otherwise
    /// (OstimoUi.md §3.1) -- avoids a two-year-old deployment misreading as
    /// recent just because the year is omitted.
    /// </summary>
    private string RelativeFriendlyDate
    {
        get
        {
            var local = Model.Timestamp.LocalDateTime;
            return local.Year == DateTime.Now.Year
                ? local.ToString("MMM d")
                : local.ToString("MMM d, yyyy");
        }
    }

    // ----- Compare-against selection (OstimoUi.md §3.2) -----

    /// <summary>
    /// Every other deployment this one can be compared against. Per
    /// OstimoUi.md §9's open question, this build keeps the simpler model:
    /// always "this deployment vs. something else", not full any-vs-any.
    /// </summary>
    public IReadOnlyList<DeploymentViewModel> CompareTargets { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CompareButtonEnabled))]
    private DeploymentViewModel? _compareAgainst;

    partial void OnCompareAgainstChanged(DeploymentViewModel? value) => _ = RefreshDiffAsync();

    public bool CompareButtonEnabled => CompareAgainst is not null;

    /// <summary>
    /// The current diff against <see cref="CompareAgainst"/>. Starts in the
    /// loading state and is replaced wholesale (not mutated) whenever the
    /// comparison target changes or the diff is (re)computed.
    /// </summary>
    [ObservableProperty]
    private DiffViewModel _diff;

    /// <summary>
    /// Kicks off the initial diff fetch. <see cref="TimelineViewModel"/> (or
    /// whoever constructs this ViewModel) calls this once after construction
    /// rather than doing it inline in the constructor, since it's an async
    /// operation and constructors can't be async.
    /// </summary>
    public Task InitializeAsync() => RefreshDiffAsync();

    private async Task RefreshDiffAsync()
    {
        var target = CompareAgainst;

        if (target is null)
        {
            // No other deployment to compare against at all (e.g. a
            // single-deployment history) -- treat this the same as an
            // empty diff rather than showing a loading spinner forever.
            Diff = DiffViewModel.FromDiff(new DeploymentDiff(Model.Checksum, Model.Checksum, []));
            return;
        }

        Diff = DiffViewModel.CreateLoading();

        try
        {
            var rawDiff = await _rpmOstreeClient.GetRawDiffAsync(Model.Checksum, target.Model.Checksum);
            var parsed = _diffParser.Parse(Model.Checksum, target.Model.Checksum, rawDiff);
            Diff = DiffViewModel.FromDiff(parsed);
        }
        catch (RpmOstreeCommandException ex)
        {
            // OstimoUi.md §6: replace the Diff section with the real stderr,
            // prefixed by the view -- this ViewModel just carries the raw
            // message through, it doesn't wrap or summarize it.
            Diff = DiffViewModel.FromError(ex.StandardError);
        }
    }

    // ----- Rollback (OstimoUi.md §5.1) -----

    /// <summary>
    /// True only when this deployment is immediately above or below the
    /// booted deployment in the (newest-first) timeline -- the only case
    /// <c>rpm-ostree rollback</c> can actually honor, since it swaps
    /// deployment order rather than targeting an arbitrary commit
    /// (RpmOstree.md §5.3). Computed by <see cref="TimelineViewModel"/> from
    /// list adjacency and pushed in, since this ViewModel has no visibility
    /// into its neighbors' positions on its own.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RollbackTooltip))]
    private bool _canRollback;

    /// <summary>
    /// Explains *why* rollback is disabled when it is (OstimoUi.md §5.1) --
    /// the view shows this as a tooltip or inline note rather than just
    /// graying the button out silently.
    /// </summary>
    public string? RollbackTooltip => CanRollback
        ? null
        : "Rollback only affects the two most recent deployments — this one isn't adjacent to the booted deployment.";

    [ObservableProperty]
    private bool _isRollbackInFlight;

    [ObservableProperty]
    private string? _rollbackStatusMessage;

    /// <summary>
    /// Runs the rollback itself, called by the view *after* the confirmation
    /// dialog (OstimoUi.md §5.1, RpmOstree.md §11 step 6) has already been
    /// shown and accepted -- this ViewModel doesn't own dialog presentation,
    /// since that's a platform/view concern, not application logic.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanExecuteRollback))]
    private async Task RollbackAsync()
    {
        IsRollbackInFlight = true;
        RollbackStatusMessage = "Waiting for authentication…";

        try
        {
            await _rpmOstreeClient.RollbackAsync();
            RollbackStatusMessage = null;
            DeploymentsChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (PrivilegedActionDeclinedException)
        {
            // Expected, unremarkable outcome (OstimoUi.md §5.1) -- not an
            // error state, just quietly return to normal.
            RollbackStatusMessage = "Rollback cancelled.";
        }
        catch (RpmOstreeCommandException ex)
        {
            RollbackStatusMessage = $"Couldn't roll back: {ex.StandardError}";
        }
        finally
        {
            IsRollbackInFlight = false;
        }
    }

    private bool CanExecuteRollback() => CanRollback && !IsRollbackInFlight;

    partial void OnCanRollbackChanged(bool value) => RollbackCommand.NotifyCanExecuteChanged();

    partial void OnIsRollbackInFlightChanged(bool value) => RollbackCommand.NotifyCanExecuteChanged();

    // ----- Pin / Unpin (OstimoUi.md §5.2) -----

    [ObservableProperty]
    private bool _isPinActionInFlight;

    [ObservableProperty]
    private string? _pinStatusMessage;

    /// <summary>
    /// "Pin" when currently unpinned, "Unpin" when currently pinned
    /// (OstimoUi.md §5.2) -- the label always names the resulting action,
    /// never a static toggle label.
    /// </summary>
    public string PinButtonLabel => IsPinned ? "Unpin" : "Pin";

    /// <summary>
    /// Pin/unpin has no adjacency restriction the way rollback does
    /// (OstimoUi.md §5.2), so it's always enabled bar an action already in
    /// flight.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanExecuteTogglePin))]
    private async Task TogglePinAsync()
    {
        IsPinActionInFlight = true;
        PinStatusMessage = "Waiting for authentication…";
        var pinning = !IsPinned;

        try
        {
            await _rpmOstreeClient.PinAsync(Index, pinning);
            PinStatusMessage = null;
            DeploymentsChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (PrivilegedActionDeclinedException)
        {
            PinStatusMessage = pinning ? "Pin cancelled." : "Unpin cancelled.";
        }
        catch (RpmOstreeCommandException ex)
        {
            PinStatusMessage = $"Couldn't {(pinning ? "pin" : "unpin")}: {ex.StandardError}";
        }
        finally
        {
            IsPinActionInFlight = false;
        }
    }

    private bool CanExecuteTogglePin() => !IsPinActionInFlight;

    partial void OnIsPinActionInFlightChanged(bool value) => TogglePinCommand.NotifyCanExecuteChanged();

    // ----- Copy commit hash (OstimoUi.md §5.3) -----

    [ObservableProperty]
    private bool _showCopiedConfirmation;

    /// <summary>
    /// The view is expected to actually place <see cref="FullChecksum"/> on
    /// the clipboard (a platform/Avalonia concern) and then call this to
    /// drive the transient "Copied" toast (OstimoUi.md §5.3, ~1.5s). Kept as
    /// a plain method rather than a command with clipboard access baked in,
    /// since clipboard APIs are UI-layer, not something Ostimo.Core-adjacent
    /// ViewModels should depend on directly.
    /// </summary>
    public async Task NotifyChecksumCopiedAsync()
    {
        ShowCopiedConfirmation = true;
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        ShowCopiedConfirmation = false;
    }
}
