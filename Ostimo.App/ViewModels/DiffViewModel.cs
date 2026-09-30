using CommunityToolkit.Mvvm.ComponentModel;

namespace Ostimo.App.ViewModels;

using System.Collections.Generic;
using System.Linq;
using Ostimo.Core.Models;

/// <summary>
/// Presentation wrapper around a single <see cref="PackageChange"/> row, as
/// shown inside a diff section (OstimoUi.md §3.2 "Diff section").
///
/// This exists mainly so the view can bind display-ready strings (e.g. the
/// "old → new" pair) without putting formatting logic in XAML, and so the
/// "Removed" reduced-opacity treatment has a single boolean to bind to
/// instead of every view re-deriving it from <see cref="Kind"/>.
/// </summary>
public sealed class PackageChangeViewModel
{
    public PackageChangeViewModel(PackageChange change)
    {
        Kind = change.Kind;
        Name = change.Name;
        OldVersion = change.OldVersion;
        NewVersion = change.NewVersion;
    }

    public PackageChangeKind Kind { get; }

    public string Name { get; }

    public string? OldVersion { get; }

    public string? NewVersion { get; }

    /// <summary>
    /// True for <see cref="PackageChangeKind.Removed"/> rows -- per
    /// OstimoUi.md §3.2, these render at ~80% opacity in addition to their
    /// color, so colorblind users aren't relying on red alone (§7).
    /// </summary>
    public bool IsRemoved => Kind == PackageChangeKind.Removed;

    /// <summary>
    /// Whether to show an arrow between old and new versions. Only
    /// "Upgraded" rows have both an old and a new version to connect
    /// (OstimoUi.md §3.2) -- Added/Removed rows show a single version with
    /// no arrow.
    /// </summary>
    public bool ShowArrow => Kind == PackageChangeKind.Upgraded;
}

/// <summary>
/// One collapsible category within the diff section -- "Upgraded (3)",
/// "Added (1)", "Removed (1)" (OstimoUi.md §3.2). Sections are only created
/// for non-empty categories, and are always ordered Upgraded → Added →
/// Removed to match both the CLI's own output order and the mockup, not
/// alphabetically (OstimoUi.md §3.2's explicit "don't re-sort" note).
/// </summary>
public sealed partial class DiffSectionViewModel : ObservableObject
{
    public DiffSectionViewModel(string title, PackageChangeKind kind, IReadOnlyList<PackageChange> changes)
    {
        Title = title;
        Kind = kind;
        Changes = changes.Select(c => new PackageChangeViewModel(c)).ToList();
    }

    public string Title { get; }

    public PackageChangeKind Kind { get; }

    public IReadOnlyList<PackageChangeViewModel> Changes { get; }

    public int Count => Changes.Count;

    /// <summary>
    /// "Upgraded (3)" -- built here rather than in XAML so the count and the
    /// label can never drift apart.
    /// </summary>
    public string Header => $"{Title} ({Count})";

    /// <summary>
    /// True for <see cref="PackageChangeKind.Upgraded"/> rows -- used by
    /// the DiffView.xaml colored ellipse marker (OstimoUi.md §3.2).
    /// </summary>
    public bool IsUpgraded => Kind == PackageChangeKind.Upgraded;

    /// <summary>
    /// True for <see cref="PackageChangeKind.Added"/> rows -- used by
    /// the DiffView.xaml colored ellipse marker.
    /// </summary>
    public bool IsAdded => Kind == PackageChangeKind.Added;

    /// <summary>
    /// True for <see cref="PackageChangeKind.Removed"/> rows -- used by
    /// the DiffView.xaml colored ellipse marker (OstimoUi.md §3.2).
    /// </summary>
    public bool IsRemoved => Kind == PackageChangeKind.Removed;

    /// <summary>
    /// Sections start expanded by default (OstimoUi.md §3.2) and the
    /// collapsed/expanded state persists only for the current session, not
    /// across app restarts (explicitly deferred per OstimoUi.md §9) -- so
    /// this is plain in-memory state with no backing store.
    /// </summary>
    [ObservableProperty]
    private bool _isExpanded = true;
}

/// <summary>
/// Drives the "Diff" section of the detail pane (OstimoUi.md §3.2): the
/// collapsible Upgraded/Added/Removed groups, plus the empty and error
/// states called out in §6 ("db diff returns nothing" / "db diff command
/// fails").
///
/// This ViewModel is intentionally dumb -- it only knows how to *present* a
/// <see cref="DeploymentDiff"/> (or the absence/failure of one). Fetching the
/// diff and deciding which two checksums to compare is
/// <see cref="DeploymentViewModel"/>'s job (OstimoUi.md §3.2's "Compare
/// against" selector), which constructs and replaces this ViewModel whenever
/// the comparison target changes.
/// </summary>
public sealed partial class DiffViewModel : ObservableObject
{
    /// <summary>
    /// Fixed display order for diff categories -- matches both the CLI's own
    /// section order and OstimoUi.md §3.2's explicit "don't alphabetize"
    /// instruction.
    /// </summary>
    private static readonly (PackageChangeKind Kind, string Title)[] SectionOrder =
    [
        (PackageChangeKind.Upgraded, "Upgraded"),
        (PackageChangeKind.Added, "Added"),
        (PackageChangeKind.Removed, "Removed"),
    ];

    private DiffViewModel()
    {
    }

    /// <summary>
    /// True while <see cref="IRpmOstreeClient.GetRawDiffAsync"/> (and the
    /// subsequent parse) is in flight. The view can show a lightweight
    /// inline loading indicator in the Diff section while this is true,
    /// without blocking the rest of the detail pane (OstimoUi.md §3.1's
    /// "never block the person from looking at what's already on screen"
    /// principle applies here too).
    /// </summary>
    [ObservableProperty]
    private bool _isLoading;

    /// <summary>
    /// Non-empty diff sections, in <see cref="SectionOrder"/>. Empty when
    /// there's nothing to show -- either because the diff was genuinely
    /// empty (<see cref="IsEmptyDiff"/>) or because it failed
    /// (<see cref="ErrorMessage"/>) or is still loading.
    /// </summary>
    public IReadOnlyList<DiffSectionViewModel> Sections { get; private set; } = [];

    /// <summary>
    /// True when the diff succeeded but contained no changes at all
    /// (<see cref="DeploymentDiff.IsEmpty"/>). Per OstimoUi.md §6, the view
    /// should replace the Diff section with a single centered line ("No
    /// package differences between these two deployments.") rather than
    /// leaving a blank area that could be misread as a loading glitch.
    /// </summary>
    public bool IsEmptyDiff { get; private set; }

    /// <summary>
    /// Set when <c>GetRawDiffAsync</c> threw a
    /// <see cref="RpmOstreeCommandException"/>. Holds the raw stderr, which
    /// the view renders verbatim in a monospace block prefixed with
    /// "Couldn't compute the diff:" (OstimoUi.md §6) -- never a generic
    /// "something went wrong" message.
    /// </summary>
    public string? ErrorMessage { get; private set; }

    public bool HasError => ErrorMessage is not null;

    /// <summary>
    /// Builds a loading placeholder. <see cref="DeploymentViewModel"/> shows
    /// this immediately when the comparison target changes, then replaces it
    /// once the real diff (or an error) comes back, so the UI never sits on
    /// stale content while a new diff is being fetched.
    /// </summary>
    public static DiffViewModel CreateLoading() => new() { IsLoading = true };

    /// <summary>
    /// Builds the success case from a parsed <see cref="DeploymentDiff"/>.
    /// </summary>
    public static DiffViewModel FromDiff(DeploymentDiff diff)
    {
        var vm = new DiffViewModel { IsLoading = false };

        if (diff.IsEmpty)
        {
            vm.IsEmptyDiff = true;
            return vm;
        }

        var changesByKind = diff.Changes
            .GroupBy(c => c.Kind)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<PackageChange>)g.ToList());

        vm.Sections = SectionOrder
            .Where(s => changesByKind.ContainsKey(s.Kind))
            .Select(s => new DiffSectionViewModel(s.Title, s.Kind, changesByKind[s.Kind]))
            .ToList();

        return vm;
    }

    /// <summary>
    /// Builds the failure case from a <see cref="RpmOstreeCommandException"/>
    /// raised by <c>GetRawDiffAsync</c> (OstimoUi.md §6's "db diff command
    /// fails" row) -- <paramref name="stdErr"/> is shown as-is, not wrapped
    /// or summarized, per RpmOstree.md §6.5's "surface real errors" principle.
    /// </summary>
    public static DiffViewModel FromError(string stdErr) =>
        new() { IsLoading = false, ErrorMessage = stdErr };
}
