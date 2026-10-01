using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;

namespace Ostimo.App.Views;

using Ostimo.App.ViewModels;

/// <summary>
/// Code-behind for <c>DeploymentDetailView.axaml</c>.
///
/// Holds exactly one thing that has to live here rather than in
/// <see cref="DeploymentViewModel"/>: the actual clipboard write. Avalonia's
/// clipboard API is reached via <c>TopLevel.GetTopLevel(this)</c>, which
/// needs a live <see cref="Avalonia.Visual"/> -- a platform/UI-layer concern
/// that <see cref="DeploymentViewModel"/> (shared with, and testable
/// independent of, the UI per RpmOstree.md §6.2) has no business depending
/// on directly. Everything else -- including the transient "Copied" toast
/// state -- stays in the ViewModel via <see cref="DeploymentViewModel.NotifyChecksumCopiedAsync"/>.
/// </summary>
public partial class DeploymentDetailView : UserControl
{
    public DeploymentDetailView()
    {
        InitializeComponent();
    }

    private async void OnCopyChecksumClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not DeploymentViewModel viewModel)
        {
            return;
        }

        var topLevel = TopLevel.GetTopLevel(this);
        var clipboard = topLevel?.Clipboard;

        if (clipboard is not null)
        {
            await clipboard.SetTextAsync(viewModel.FullChecksum);
        }

        // Drives the transient "Copied" toast (OstimoUi.md §5.3) regardless
        // of whether the clipboard call above succeeded -- if the platform
        // has no clipboard available there's nothing more useful to do than
        // let the person try again, not silently swallow the click.
        await viewModel.NotifyChecksumCopiedAsync();
    }

    /// <summary>
    /// Opens the confirmation dialog (OstimoUi.md §5.1, RpmOstree.md §11
    /// step 6) and only invokes <c>RollbackCommand</c> if the person
    /// explicitly confirms. This is why the button in the .axaml is wired to
    /// <c>Click</c> rather than <c>Command="{Binding RollbackCommand}"</c>
    /// directly -- the dialog has to sit in between the click and the actual
    /// privileged action.
    /// </summary>
    private async void OnRollbackClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not DeploymentViewModel viewModel)
        {
            return;
        }

        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is null)
        {
            return;
        }

        var confirmed = await RollbackConfirmationDialog.ShowAsync(owner, viewModel.Version);
        if (!confirmed)
        {
            return;
        }

        if (viewModel.RollbackCommand.CanExecute(null))
        {
            await viewModel.RollbackCommand.ExecuteAsync(null);
        }
    }
}
