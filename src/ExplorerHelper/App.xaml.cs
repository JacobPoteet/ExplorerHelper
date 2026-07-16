using System.IO;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using Wpf.Ui.Appearance;

namespace ExplorerHelper;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Brand accent (violet) instead of the system/Windows default blue. Regenerates the
        // Primary/Secondary/Tertiary accent shades every themed control derives from.
        ApplicationAccentColorManager.Apply(
            Color.FromRgb(0x7C, 0x5C, 0xFC), ApplicationTheme.Dark);

        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.Message, "Explorer Helper — unexpected error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        // Launched from the Explorer context menu the folder arrives as the first argument.
        var folder = e.Args.FirstOrDefault(Directory.Exists);

        if (folder is null)
        {
            var dialog = new OpenFolderDialog { Title = "Choose a folder to clean" };
            if (dialog.ShowDialog() == true)
                folder = dialog.FolderName;
        }

        var window = new MainWindow();
        window.Show();
        if (folder is not null)
            window.LoadFolder(folder);
    }
}
