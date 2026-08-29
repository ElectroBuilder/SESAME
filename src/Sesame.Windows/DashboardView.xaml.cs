using System.Windows;
using System.Windows.Controls;

namespace Sesame;

public sealed class DashboardStats
{
    public bool Connected { get; init; }
    public int FileCount { get; init; }
    public string Folder { get; init; } = "";
    public int AppCount { get; init; }
    public int GameCount { get; init; }
    public int OptimizeCount { get; init; }
    public int InSteamCount { get; init; }
    public int SelectedCount { get; init; }
    public int StoreCount { get; init; }
}

public partial class DashboardView : UserControl
{
    public event Action<string>? OpenTab;
    public event Action? ScanRequested;
    public event Action? OptimizeRequested;

    public DashboardView() => InitializeComponent();

    public void UpdateStats(DashboardStats stats)
    {
        if (!stats.Connected)
        {
            HintText.Text = "Connect to the Steam Deck first. Scan apps and games when you are ready; Files stays usable.";
            SetTile(FilesCount, FilesDetail, "—", "Connect to browse folders");
            SetTile(AppsCount, AppsDetail, "—", "Native Deck apps");
            SetTile(GamesCount, GamesDetail, "—", "ROMs and games you added");
            SetTile(OptimizeCount, OptimizeDetail, "—", "Shortcuts, covers and collections");
            SetTile(StoreCount, StoreDetail, "—", "Mods, packs and ROM hacks");
            return;
        }

        HintText.Text = "Scan apps and games, then Optimize to write Steam shortcuts and artwork.";
        var folder = string.IsNullOrWhiteSpace(stats.Folder) ? "the Deck" : stats.Folder;
        SetTile(FilesCount, FilesDetail, stats.FileCount.ToString(), stats.FileCount + " items in " + folder);
        SetTile(AppsCount, AppsDetail, stats.AppCount.ToString(),
            stats.AppCount == 0 ? "Scan to find native apps" : stats.AppCount + " native apps");
        SetTile(GamesCount, GamesDetail, stats.GameCount.ToString(),
            stats.GameCount == 0 ? "Scan to list ROMs" : stats.GameCount + " games in the library");
        var opt = stats.OptimizeCount == 0
            ? "Scan to load titles for Steam"
            : stats.InSteamCount + " in Steam · " + stats.SelectedCount + " selected";
        SetTile(OptimizeCount, OptimizeDetail, stats.OptimizeCount.ToString(), opt);
        SetTile(StoreCount, StoreDetail, stats.StoreCount.ToString(),
            stats.StoreCount == 0 ? "Browse mods and packs" : stats.StoreCount + " catalog titles");
    }

    private static void SetTile(TextBlock count, TextBlock detail, string value, string text)
    {
        count.Text = value;
        detail.Text = text;
    }

    private void Scan_Click(object sender, RoutedEventArgs e) => ScanRequested?.Invoke();
    private void Optimize_Click(object sender, RoutedEventArgs e) => OptimizeRequested?.Invoke();
    private void Files_Click(object sender, RoutedEventArgs e) => OpenTab?.Invoke("files");
    private void Apps_Click(object sender, RoutedEventArgs e) => OpenTab?.Invoke("apps");
    private void Games_Click(object sender, RoutedEventArgs e) => OpenTab?.Invoke("games");
    private void OptimizeTab_Click(object sender, RoutedEventArgs e) => OpenTab?.Invoke("optimize");
    private void Store_Click(object sender, RoutedEventArgs e) => OpenTab?.Invoke("store");
}
