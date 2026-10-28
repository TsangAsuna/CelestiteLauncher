using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Celestite.Dialogs.Games;

public partial class AutoDetectDmmGamesDialog : UserControl
{
    public AutoDetectDmmGamesDialog()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
