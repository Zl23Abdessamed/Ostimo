using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Ostimo.App;

using Ostimo.App.ViewModels;

/// <summary>
/// Code-behind for the app's single top-level window. Its only
/// responsibility beyond wiring the control is kicking off
/// <see cref="MainWindowViewModel.InitializeAsync"/> once the window has
/// loaded -- that method is async and <see cref="MainWindowViewModel"/>'s
/// constructor can't be, per RpmOstree.md §6.2's "no code-behind logic
/// beyond wiring DataContext" -- the exception being this one unavoidable
/// async-kickoff, which is a lifecycle concern the view legitimately owns.
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object? sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;

        if (DataContext is MainWindowViewModel viewModel)
        {
            await viewModel.InitializeAsync();
        }
    }
}
