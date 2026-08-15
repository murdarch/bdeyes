using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Bdeyes.ViewModels;

namespace Bdeyes.Views;

public sealed class OutlineListBox : ListBox
{

    protected override Type StyleKeyOverride => typeof(ListBox);
    protected override AutomationPeer OnCreateAutomationPeer() =>
        new OutlineListBoxAutomationPeer(this);




    protected override Control CreateContainerForItemOverride(
        object? item,
        int index,
        object? recycleKey) =>
        new OutlineListBoxItem();

    protected override bool NeedsContainerOverride(
        object? item,
        int index,
        out object? recycleKey) =>
        NeedsContainer<OutlineListBoxItem>(item, out recycleKey);
}

public sealed class OutlineListBoxAutomationPeer : ListBoxAutomationPeer
{
    public OutlineListBoxAutomationPeer(OutlineListBox owner)
        : base(owner)
    {
    }

    protected override AutomationControlType GetAutomationControlTypeCore() =>
        AutomationControlType.Tree;


}


public sealed class OutlineListBoxItem : ListBoxItem
{
    protected override Type StyleKeyOverride => typeof(ListBoxItem);

    protected override AutomationPeer OnCreateAutomationPeer() =>
        new OutlineListBoxItemAutomationPeer(this);
}

public sealed class OutlineListBoxItemAutomationPeer : ListItemAutomationPeer, IExpandCollapseProvider
{
    public OutlineListBoxItemAutomationPeer(OutlineListBoxItem owner)
        : base(owner)
    {
    }

    private BeadRowViewModel? Row => Owner.DataContext as BeadRowViewModel;

    public ExpandCollapseState ExpandCollapseState => Row switch
    {
        not { HasVisibleChildren: true } => ExpandCollapseState.LeafNode,
        { IsExpanded: true } => ExpandCollapseState.Expanded,
        _ => ExpandCollapseState.Collapsed,
    };

    public bool ShowsMenu => false;

    public void Expand() => SetExpanded(true);

    public void Collapse() => SetExpanded(false);

    protected override AutomationControlType GetAutomationControlTypeCore() =>
        AutomationControlType.TreeItem;

    protected override string? GetHelpTextCore() =>
        Row?.HierarchyAutomationHelp ?? base.GetHelpTextCore();
    protected override string? GetNameCore()
    {
        var name = base.GetNameCore();
        if (Row is not { } row)
        {
            return name;
        }

        var hierarchy = row.Parent is null
            ? "Level 1"
            : $"Level {row.Depth + 1}, child of {row.Parent.Id}";
        return string.IsNullOrWhiteSpace(name)
            ? hierarchy
            : $"{name}. {hierarchy}";
    }





    private void SetExpanded(bool expanded)
    {
        EnsureEnabled();
        if (Row is not { HasVisibleChildren: true } row || row.IsExpanded == expanded)
        {
            return;
        }

        var previous = ExpandCollapseState;
        row.ToggleExpansionCommand.Execute(null);
        RaisePropertyChangedEvent(
            ExpandCollapsePatternIdentifiers.ExpandCollapseStateProperty,
            previous,
            ExpandCollapseState);
    }
}
