using System.Windows;
using LuaToolsGui.Services;
using LuaToolsGui.ViewModels;

namespace LuaToolsGui.Views;

public partial class CanIRunItDialog : Wpf.Ui.Controls.FluentWindow
{
    private readonly CanIRunItViewModel _viewModel;
    private readonly long _appId;
    private readonly string _gameName;

    public CanIRunItDialog(CanIRunItViewModel viewModel, long appId, string gameName)
    {
        InitializeComponent();
        DataContext = _viewModel = viewModel;
        _appId = appId;
        _gameName = gameName;
        Loaded += async (_, _) => await _viewModel.LoadAsync(_appId, _gameName);
        Closed += (_, _) => _viewModel.Cancel();
    }

    private async void Analyze_Click(object sender, RoutedEventArgs e)
    {
        string key = ApiKeyBox.Password;
        ApiKeyBox.Clear();
        await _viewModel.SubmitKeyAsync(key, RememberKeyBox.IsChecked == true);
    }

    private async void Retry_Click(object sender, RoutedEventArgs e) => await _viewModel.RetryAsync();

    private void OpenRouterKeys_Click(object sender, RoutedEventArgs e) =>
        SteamService.OpenUrl("https://openrouter.ai/settings/keys");

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
