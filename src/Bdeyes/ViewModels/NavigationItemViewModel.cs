using CommunityToolkit.Mvvm.ComponentModel;

namespace Bdeyes.ViewModels;

public enum DashboardMode
{
    Now,
    Blocked,
    Unclaimed,
    Aging,
    All,
    Epics,
}

public sealed partial class NavigationItemViewModel : ViewModelBase
{
    public NavigationItemViewModel(DashboardMode mode, string label, string glyph)
    {
        Mode = mode;
        Label = label;
        Glyph = glyph;
    }

    public DashboardMode Mode { get; }

    public string Label { get; }

    public string Glyph { get; }

    [ObservableProperty]
    public partial int Count { get; set; }

    public override string ToString() => $"{Label}: {Count}";
}
