using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Celestite.Dialogs.Games;

public partial class DmmGameMigrationDialog : UserControl
{
    public DmmGameMigrationDialog()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
