using Avalonia.Controls;

namespace Ostimo.App.Views;

/// <summary>
/// Code-behind for <c>TimelineView.axaml</c>. All list/selection/refresh
/// behavior lives in <c>TimelineViewModel</c> per RpmOstree.md §6.2 -- this
/// exists only to wire up the control itself.
/// </summary>
public partial class TimelineView : UserControl
{
    public TimelineView()
    {
        InitializeComponent();
    }
}
