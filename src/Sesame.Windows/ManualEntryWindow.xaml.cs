using System.IO;
using System.Windows;
using Microsoft.Win32;
using Sesame.Services.GameOptimizer;

namespace Sesame;

public partial class ManualEntryWindow : Window
{
    public ManualShortcut Result { get; private set; } = new();

    public ManualEntryWindow(string kind, bool browse)
    {
        InitializeComponent();
        var app = kind.Equals("App", StringComparison.OrdinalIgnoreCase);
        Title = app ? "Add app" : "Add game";
        HintText.Text = app
            ? "Name and a launch path are enough. The app stays in Apps and Artwork until you remove it."
            : "Name and a launch path are enough. The game stays in Games and Artwork until you remove it.";
        Result.Kind = app ? "App" : "Game";
        Result.AddedByUser = true;
        BrowseBtn.Visibility = browse ? Visibility.Visible : Visibility.Collapsed;
        NameBox.Focus();
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Choose executable",
            Filter = "Executables|*.exe;*.sh;*|" + "All files|*.*"
        };
        if (dlg.ShowDialog(this) == true)
            ExeBox.Text = dlg.FileName;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text?.Trim() ?? "";
        var exe = ExeBox.Text?.Trim().Trim('"') ?? "";
        if (name.Length == 0 || exe.Length == 0)
        {
            MessageBox.Show(this, "Fill in a name and a path to the executable.", Title);
            return;
        }

        Result.Name = name;
        Result.Exe = exe;
        Result.StartDir = Path.GetDirectoryName(exe.Replace('\\', '/').TrimEnd('/')) ?? "";
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
