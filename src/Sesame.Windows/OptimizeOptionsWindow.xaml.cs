using System.Windows;

namespace Sesame;

public partial class OptimizeOptionsWindow : Window
{
    public OptimizeOptionsWindow()
    {
        InitializeComponent();
    }

    public bool OverwriteShortcuts => OverwriteBox.IsChecked == true;
    public bool OverwriteArtwork => ArtworkBox.IsChecked == true;
    public bool UseMasks => MaskBox.IsChecked == true;

    public void Bind(int count, bool overwriteShortcuts, bool overwriteArtwork, bool useMasks,
        string steamNote, string? gyroNote = null)
    {
        CountText.Text = count == 1
            ? "Optimize 1 selected game."
            : "Optimize " + count + " selected games.";
        OverwriteBox.IsChecked = overwriteShortcuts;
        ArtworkBox.IsChecked = overwriteArtwork;
        MaskBox.IsChecked = useMasks;
        SteamNote.Text = steamNote;
        GyroNote.Text = gyroNote ?? "";
        var showGyro = !string.IsNullOrWhiteSpace(gyroNote);
        GyroNote.Visibility = showGyro ? Visibility.Visible : Visibility.Collapsed;
        GyroBox.Visibility = showGyro ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
