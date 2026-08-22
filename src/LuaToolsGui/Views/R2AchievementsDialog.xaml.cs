using LuaToolsGui.ViewModels;

namespace LuaToolsGui.Views;

/// <summary>Modal, read-only list of the local Ubisoft Connect R2 achievement state.</summary>
public partial class R2AchievementsDialog : Wpf.Ui.Controls.FluentWindow
{
    private readonly R2AchievementsViewModel _viewModel;

    public R2AchievementsDialog(R2AchievementsViewModel viewModel, long appId, string gameName)
    {
        InitializeComponent();
        DataContext = _viewModel = viewModel;
        _viewModel.CloseRequested = Close;
        Loaded += async (_, _) => await _viewModel.LoadAsync(appId, gameName);
    }
}
