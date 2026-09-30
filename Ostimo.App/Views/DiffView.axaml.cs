using Avalonia.Controls;

namespace Ostimo.App.Views;

/// <summary>
/// Code-behind for <c>DiffView.axaml</c>. Per RpmOstree.md §6.2, all
/// behavior lives in ViewModels -- this exists only to wire up the control
/// itself; DataContext is set by whichever parent view hosts this
/// (DeploymentDetailView), not here.
/// </summary>
public partial class DiffView : UserControl
{
    public DiffView()
    {
        InitializeComponent();
    }
}
