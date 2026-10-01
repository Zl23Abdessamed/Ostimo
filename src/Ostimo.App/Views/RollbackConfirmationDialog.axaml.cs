using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using System;
using System.Threading.Tasks;

namespace Ostimo.App.Views;

/// <summary>
/// Modal "Roll back to this deployment?" dialog (OstimoUi.md §5.1). Shown via
/// <see cref="ShowAsync"/> from <c>DeploymentDetailView</c>'s code-behind
/// immediately before invoking <c>DeploymentViewModel.RollbackCommand</c> --
/// this dialog itself has no knowledge of rpm-ostree/pkexec at all, it only
/// asks the yes/no question and reports the answer back.
/// </summary>
public partial class RollbackConfirmationDialog : Window
{
    private bool _confirmed;

    public RollbackConfirmationDialog()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Shows the dialog modally over <paramref name="owner"/> and returns
    /// <c>true</c> only if the person clicked "Roll back" (never for
    /// "Cancel", Escape, or closing the window via chrome).
    /// </summary>
    /// <param name="owner">The window to center over and modally block.</param>
    /// <param name="targetVersion">
    /// The version string the system will boot into next -- interpolated
    /// into the body copy so the person reads exactly what they're
    /// confirming (OstimoUi.md §5.1).
    /// </param>
    public static async Task<bool> ShowAsync(Window owner, string targetVersion)
    {
        var dialog = new RollbackConfirmationDialog();
        dialog.BodyText.Text =
            $"Your system will boot into {targetVersion} the next time you restart. " +
            "This does not happen immediately.";

        await dialog.ShowDialog(owner);
        return dialog._confirmed;
    }

    private void OnConfirmClick(object? sender, RoutedEventArgs e)
    {
        _confirmed = true;
        Close();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        _confirmed = false;
        Close();
    }

    // Tab-cycling within the dialog and modal input blocking to the rest of
    // the app are provided by Avalonia's own ShowDialog/focus-scope behavior
    // (§7 "confirmation dialogs are modal and trap focus"). IsCancel="True"
    // on the Cancel button already wires Escape to it; this override is a
    // defensive backstop in case that binding is ever removed from the XAML.
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            _confirmed = false;
            Close();
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }
}
