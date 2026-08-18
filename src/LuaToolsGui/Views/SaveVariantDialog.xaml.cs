using System.Windows;
using LuaToolsGui.Models;

namespace LuaToolsGui.Views;

/// <summary>Asks which data-defined release/mod save layout should be backed up or restored.</summary>
public partial class SaveVariantDialog : Wpf.Ui.Controls.FluentWindow
{
    public string GameName { get; }
    public IReadOnlyList<GameSaveVariant> Variants { get; }
    public GameSaveVariant? SelectedVariant { get; private set; }

    public SaveVariantDialog(GameSaveDefinition game)
    {
        GameName = game.Name;
        Variants = game.SaveVariants;
        InitializeComponent();
        DataContext = this;
    }

    private void SelectVariant_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: GameSaveVariant variant }) return;
        SelectedVariant = variant;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
