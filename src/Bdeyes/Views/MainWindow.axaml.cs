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
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
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
        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        if (eventArgs.Key == Key.Escape && viewModel.HasSelection)
        {
            viewModel.CloseDetailCommand.Execute(null);
            eventArgs.Handled = true;
            return;
        }

        if (!BeadList.IsKeyboardFocusWithin)
        {
            return;
        }

        eventArgs.Handled = eventArgs.Key switch
        {
            Key.Right => viewModel.ExpandOrSelectFirstChild(),
            Key.Left => viewModel.CollapseOrSelectParent(),
            Key.Space => viewModel.ToggleSelectedExpansion(),
            _ => false,
        };

        if (eventArgs.Handled && viewModel.SelectedRow is not null)
        {
            Dispatcher.UIThread.Post(
                () => BeadList.ScrollIntoView(viewModel.SelectedRow),
                DispatcherPriority.Background);
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