using System.Windows;
using LuaToolsGui.Services;

namespace LuaToolsGui.Views;

public partial class AchievementUnlockedWindow : Window
{
    public AchievementUnlockedWindow(AchievementPopupContent content)
    {
        InitializeComponent();
        DataContext = content;
    }

    private void Popup_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) => Close();
}
