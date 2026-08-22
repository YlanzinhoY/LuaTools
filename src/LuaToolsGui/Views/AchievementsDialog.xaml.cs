using LuaToolsGui.ViewModels;

namespace LuaToolsGui.Views;

/// <summary>Modal, read-only list of achievements resolved from Steam and compatible local providers.</summary>
public partial class AchievementsDialog : Wpf.Ui.Controls.FluentWindow
{
    private readonly AchievementsViewModel _viewModel;

    public AchievementsDialog(AchievementsViewModel viewModel, long appId, string gameName)
    {
        InitializeComponent();
        DataContext = _viewModel = viewModel;
        _viewModel.CloseRequested = Close;
        Loaded += async (_, _) => await _viewModel.LoadAsync(appId, gameName);
    }
}
