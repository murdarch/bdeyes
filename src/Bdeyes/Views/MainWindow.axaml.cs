using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Bdeyes.ViewModels;

namespace Bdeyes.Views;

public partial class MainWindow : Window
{
    private DispatcherTimer? _refreshTimer;
    private MainViewModel? _viewModel;

    public MainWindow()
    {
        InitializeComponent();
        Opened += OnOpened;
        Closed += OnClosed;
        KeyDown += OnKeyDown;
    }

    private async void OnOpened(object? sender, EventArgs eventArgs)
    {
        Opened -= OnOpened;
        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }
        _viewModel = viewModel;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;

        await viewModel.InitializeAsync();
        _refreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMinutes(1),
        };
        _refreshTimer.Tick += (_, _) =>
        {
            if (viewModel.RefreshCommand.CanExecute(null))
            {
                viewModel.RefreshCommand.Execute(null);
            }
        };
        _refreshTimer.Start();
    }

    private void OnClosed(object? sender, EventArgs eventArgs)
    {
        _refreshTimer?.Stop();
        _refreshTimer = null;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel = null;
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key == Key.Escape && DataContext is MainViewModel { HasSelection: true } viewModel)
        {
            viewModel.CloseDetailCommand.Execute(null);
            eventArgs.Handled = true;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName == nameof(MainViewModel.Detail) && _viewModel?.Detail is not null)
        {
            Dispatcher.UIThread.Post(
                () => DetailScroller.Offset = default,
                DispatcherPriority.Background);
        }
    }

    private async void OpenWorkspace_Click(object? sender, RoutedEventArgs eventArgs)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Open a Beads workspace",
            AllowMultiple = false,
        });
        var path = folders.FirstOrDefault()?.TryGetLocalPath();
        if (path is not null && DataContext is MainViewModel viewModel)
        {
            await viewModel.OpenWorkspaceAsync(path);
        }
    }
}