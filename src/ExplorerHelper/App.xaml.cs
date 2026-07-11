using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace ExplorerHelper;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

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
