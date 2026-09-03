using System.Collections.Specialized;
using System.Windows.Controls;
using LuaToolsGui.ViewModels;

namespace LuaToolsGui.Views;

public partial class AchievementBridgeLogView : UserControl
{
    public AchievementBridgeLogView(AchievementBridgeLogViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.Entries.CollectionChanged += ScrollToLatest;
    }

    private void ScrollToLatest(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems is { Count: > 0 })
            Dispatcher.BeginInvoke(() => LogList.ScrollIntoView(e.NewItems[^1]));
    }
}
