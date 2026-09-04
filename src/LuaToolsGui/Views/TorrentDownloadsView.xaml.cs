using System.Windows.Controls;
using LuaToolsGui.ViewModels;

namespace LuaToolsGui.Views;

public partial class TorrentDownloadsView : UserControl
{
    public TorrentDownloadsView(TorrentDownloadsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
