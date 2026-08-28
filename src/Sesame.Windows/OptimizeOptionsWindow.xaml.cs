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
            ? "1 geselecteerde game optimaliseren."
            : count + " geselecteerde games optimaliseren.";
        OverwriteBox.IsChecked = overwriteShortcuts;
        ArtworkBox.IsChecked = overwriteArtwork;
        MaskBox.IsChecked = useMasks;
        SteamNote.Text = steamNote;
        GyroNote.Text = gyroNote ?? "";
        GyroNote.Visibility = string.IsNullOrWhiteSpace(gyroNote)
            ? Visibility.Collapsed
            : Visibility.Visible;
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
