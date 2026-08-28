using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Microsoft.Win32;
using Sesame.Services;
using Sesame.Services.N64;

namespace Sesame;

public partial class LanguagePatchWindow : Window
{
    private readonly byte[] _rom;
    private readonly string _gameName;
    private readonly ObservableCollection<BkTextLine> _lines = new();
    private readonly ListCollectionView? _view;
    private CancellationTokenSource? _cts;
    private bool _rememberEdits;
    private bool _deckChosen;

    public string? OutputPath { get; set; }

    public LanguagePatchWindow(byte[] rom, string gameName)
    {
        InitializeComponent();
        _rom = rom;
        _gameName = gameName;
        Title = "Nederlandse taalpatch " + AppVersion.Label;
        TitleText.Text = gameName + " → Nederlands  ·  " + AppVersion.Label;
        _view = new ListCollectionView(_lines);
        _view.Filter = FilterLine;
        LineGrid.ItemsSource = _view;
        TranslateSettings.Load();
        DeepLKeyBox.Clear();
        UpdateDeepLStatus();
        Loaded += async (_, _) => await StartAsync();
        Closed += (_, _) => _cts?.Cancel();
        Closing += OnClosing;
    }

    private async Task StartAsync()
    {
        ApplyBtn.IsEnabled = false;
        RetranslateBtn.IsEnabled = false;
        OnlineBtn.IsEnabled = false;
        try
        {
            StatusText.Text = "Tekst zoeken in de ROM…";
            var extracted = await Task.Run(() => LanguagePatcher.Extract(_rom, msg =>
                Dispatcher.Invoke(() => StatusText.Text = msg)));
            _lines.Clear();
            foreach (var line in extracted)
            {
                line.PropertyChanged += LineOnPropertyChanged;
                _lines.Add(line);
            }
            PhraseGrid.ItemsSource = DutchIdioms.Scan(_lines);
            if (_lines.Any(l => l.Codec == "dk64"))
                HintText.Text =
                    "Donkey Kong 64-teksttabel herkend. Alleen echte in-game zinnen staan in de tabel. " +
                    "Een Nederlandse zin moet in het originele vak passen. Het origineel blijft staan.";
            else if (_lines.Any(l => l.Codec == "sm64"))
                HintText.Text =
                    "Super Mario 64: elke dialoog wordt als geheel vertaald (verhaal en grap, geen losse woorden). " +
                    "Een Nederlandse zin moet in het originele vak passen. Het origineel blijft staan.";
            else if (_lines.Any(l => l.Generic))
                HintText.Text =
                    "Geen Banjo-dialoogtabel. SESAME zoekt Engelse zinnen (ASCII of uitgepakte Rare-blokken) " +
                    "en slaat binaire rommel over. Games met een eigen lettertype tonen niet alles. " +
                    "Te lange zinnen worden afgekapt. Het origineel blijft staan.";
            UpdateProgress(new TranslateProgress { Total = _lines.Count, Done = 0, Message = "Klaar om te vertalen…" });
            if (_lines.Count == 0)
            {
                StatusText.Text = "Geen Engelse dialoog gevonden in deze dump.";
                return;
            }

            await TranslateNow(tryOnline: true);
            var pending = _lines.Count(DutchTranslator.IsPending);
            StatusText.Text = pending == 0
                ? $"{_lines.Count} zinnen klaar (natuurlijk Nederlands, geen woord-voor-woord)."
                : $"{_lines.Count - pending} klaar · {pending} nog open. Gebruik Online vertalen als een zin Engels blijft.";

            _view?.Refresh();
            ApplyBtn.IsEnabled = true;
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
            MessageBox.Show(this, ex.Message, "Taalpatch");
        }
        finally
        {
            RetranslateBtn.IsEnabled = true;
            OnlineBtn.IsEnabled = true;
        }
    }

    private void SaveDeepL_Click(object sender, RoutedEventArgs e)
    {
        var typed = DeepLKeyBox.Password ?? "";
        if (string.IsNullOrWhiteSpace(typed))
        {
            StatusText.Text = TranslateSettings.HasDeepL
                ? "Er is al een sleutel opgeslagen. Typ een nieuwe om die te vervangen."
                : "Plak eerst een DeepL-sleutel.";
            UpdateDeepLStatus();
            return;
        }

        TranslateSettings.SaveDeepLKey(typed);
        DeepLKeyBox.Clear();
        UpdateDeepLStatus();
        StatusText.Text = "DeepL-sleutel opgeslagen. Klik op Online vertalen voor contextuele zinnen.";
    }

    private void ClearDeepL_Click(object sender, RoutedEventArgs e)
    {
        if (!TranslateSettings.HasDeepL && string.IsNullOrEmpty(DeepLKeyBox.Password))
            return;
        if (MessageBox.Show(this, "Opgeslagen DeepL-sleutel verwijderen?", "Taalpatch",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        TranslateSettings.ClearKey();
        DeepLKeyBox.Clear();
        UpdateDeepLStatus();
        StatusText.Text = "DeepL-sleutel gewist. Online vertalen valt terug op Google.";
    }

    private void UpdateDeepLStatus()
    {
        DeepLStatus.Text = TranslateSettings.HasDeepL
            ? "Sleutel opgeslagen. DeepL staat aan: hele dialogen, informeel Nederlands (je/jij), namen blijven Engels."
            : "Geen DeepL-sleutel: Google als fallback. Gratis sleutel via deepl.com/pro-api (Free).";
    }

    private async void Retranslate_Click(object sender, RoutedEventArgs e)
    {
        CommitGridEdits();
        await TranslateNow(tryOnline: false);
    }

    private async void OnlineTranslate_Click(object sender, RoutedEventArgs e)
    {
        CommitGridEdits();
        await TranslateNow(tryOnline: true);
    }

    private async Task TranslateNow(bool tryOnline = true)
    {
        if (_lines.Count == 0) return;
        CommitGridEdits();
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        RetranslateBtn.IsEnabled = false;
        OnlineBtn.IsEnabled = false;
        ApplyBtn.IsEnabled = false;
        _rememberEdits = false;
        var before = _lines.Count(l => l.Changed);
        try
        {
            await DutchTranslator.TranslateAsync(_lines, p =>
                Dispatcher.Invoke(() => UpdateProgress(p)), ct, useCache: true, tryOnline: tryOnline);
            _view?.Refresh();
            PhraseGrid.ItemsSource = DutchIdioms.Scan(_lines);
            var left = _lines.Count(DutchTranslator.IsPending);
            var done = _lines.Count - left;
            var filled = Math.Max(0, _lines.Count(l => l.Changed) - before);
            StatusText.Text = left == 0
                ? $"{done} van {_lines.Count} klaar. Controleer gezegdes, daarna ROM maken."
                : tryOnline
                    ? $"{done} van {_lines.Count} vertaald. {left} korte namen/kreten blijven Engels."
                    : $"{filled} regels lokaal bijgewerkt. {left} blijven Engels (vaak namen of kreten). Jouw handmatige edits zijn niet overschreven.";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Vertalen gestopt. Wat al klaar was, is opgeslagen.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Vertalen deels mislukt: " + ex.Message;
        }
        finally
        {
            _rememberEdits = true;
            RetranslateBtn.IsEnabled = true;
            OnlineBtn.IsEnabled = true;
            ApplyBtn.IsEnabled = _lines.Count > 0;
            UpdateProgress(new TranslateProgress
            {
                Total = _lines.Count,
                Done = _lines.Count - _lines.Count(DutchTranslator.IsPending),
                Message = StatusText.Text
            });
        }
    }

    private void UpdateProgress(TranslateProgress p)
    {
        WorkBar.Maximum = Math.Max(1, p.Total);
        WorkBar.Value = p.Done;
        CountText.Text = $"{p.Done} vertaald · {p.Remaining} te gaan · {p.Total} totaal";
        CacheText.Text = p.FromCache > 0 ? $"{p.FromCache} uit cache" : "";
        if (!string.IsNullOrWhiteSpace(p.Message))
            StatusText.Text = p.Message;
    }

    private void LineOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_rememberEdits || sender is not BkTextLine line) return;
        if (e.PropertyName is not (nameof(BkTextLine.Translation) or null)) return;
        line.UserEdited = true;
        DutchTranslator.Remember(line.Original, line.Translation, userEdit: true, max: line.MaxChars);
        UpdateProgress(new TranslateProgress
        {
            Total = _lines.Count,
            Done = _lines.Count - _lines.Count(DutchTranslator.IsPending),
            Message = StatusText.Text
        });
    }

    private void Filter_Changed(object sender, RoutedEventArgs e) => _view?.Refresh();

    private bool FilterLine(object obj)
    {
        if (obj is not BkTextLine line) return false;
        var kind = (KindBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";
        if (kind.Contains("dialoog", StringComparison.OrdinalIgnoreCase) &&
            line.Kind is not (BkTextKind.Dialog or BkTextKind.Raw))
            return false;
        if (kind.Contains("quiz", StringComparison.OrdinalIgnoreCase) &&
            line.Kind is not (BkTextKind.Quiz or BkTextKind.Grunty))
            return false;
        if (kind.Contains("ROM-tekst", StringComparison.OrdinalIgnoreCase) && line.Kind != BkTextKind.Raw)
            return false;
        if (kind.Contains("Gewijzigd", StringComparison.OrdinalIgnoreCase) && !line.Changed)
            return false;
        var q = FilterBox.Text.Trim();
        if (q.Length == 0) return true;
        return line.Original.Contains(q, StringComparison.OrdinalIgnoreCase) ||
               line.Translation.Contains(q, StringComparison.OrdinalIgnoreCase) ||
               line.Speaker.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        CommitGridEdits();
        var tooLong = _lines.Count(l => !l.Fits);
        if (tooLong > 0)
        {
            var go = MessageBox.Show(this,
                $"{tooLong} regels zijn langer dan het beschikbare ROM-vak. " +
                "Die zinnen worden ingekort zodat het spel niet crasht; de rest blijft volledig. " +
                "Lege regels en letters met accenten worden automatisch veilig gemaakt. Doorgaan?",
                "Taalpatch", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (go != MessageBoxResult.Yes) return;
        }

        ApplyBtn.IsEnabled = false;
        RetranslateBtn.IsEnabled = false;
        OnlineBtn.IsEnabled = false;
        DeckBtn.Visibility = Visibility.Collapsed;
        ShowError("");
        ShowSuccess("");
        SetBuildProgress(1, "ROM maken gestart…");
        await Task.Delay(1);
        var sendToDeck = false;
        try
        {
            var snapshot = _lines.ToList();
            var source = _rom;
            var result = await Task.Run(() =>
            {
                DutchTranslator.RememberMany(snapshot);
                return LanguagePatcher.Build(source, snapshot, p =>
                    Dispatcher.BeginInvoke(() =>
                    {
                        SetBuildProgress(p.Percent, p.Message, p.Errors);
                        if (!string.IsNullOrWhiteSpace(p.LastError))
                            ShowError(p.LastError);
                    }));
            });

            SetBuildProgress(97, "Bestand schrijven…", result.Errors);
            var ext = CartRom.Extension(result.Rom);
            var name = Sanitize(_gameName) + " (NL)" + ext;
            var saved = await RomOutput.SaveAsync(result.Rom, name, (pct, msg) =>
                Dispatcher.BeginInvoke(() => SetBuildProgress(pct, msg, result.Errors)));
            OutputPath = saved.Path;
            CountText.Text = result.Errors > 0 ? $"100% · {result.Errors} fout(en)" : "100%";
            ShowSuccess(result.Summary + Environment.NewLine + saved.Message);
            if (result.Errors > 0)
            {
                ShowError(result.LastError ?? "Een deel van de tekst paste niet in de ROM.");
                SetBuildProgress(100, "ROM lokaal opgeslagen, maar niet alles paste.", result.Errors);
                DeckBtn.Visibility = Visibility.Visible;
                MessageBox.Show(this,
                    (result.LastError ?? "Niet alle teksten pasten in de ROM.") +
                    Environment.NewLine + Environment.NewLine +
                    "Het venster blijft open. Kort de rode regels in en klik opnieuw op ROM maken.",
                    "ROM maken");
            }
            else
            {
                ShowError("");
                SetBuildProgress(100, "ROM klaar. Wordt op de Deck gezet…");
                sendToDeck = true;
            }
        }
        catch (Exception ex)
        {
            ShowSuccess("");
            ShowError(ex.Message);
            StatusText.Text = "ROM maken mislukt.";
            SetBuildProgress(0, StatusText.Text);
            MessageBox.Show(this, ex.Message, "ROM maken");
        }
        finally
        {
            ApplyBtn.IsEnabled = true;
            RetranslateBtn.IsEnabled = true;
            OnlineBtn.IsEnabled = true;
        }

        if (sendToDeck)
        {
            _deckChosen = true;
            DialogResult = true;
        }
    }

    private void Deck_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(OutputPath) || !File.Exists(OutputPath))
        {
            MessageBox.Show(this, "Er is nog geen ROM-bestand. Klik eerst op ROM maken.", "Taalpatch");
            return;
        }
        _deckChosen = true;
        DialogResult = true;
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        CommitGridEdits();
        var dlg = new SaveFileDialog
        {
            Title = "Teksten exporteren",
            Filter = "Excel-werkblad (*.xlsx)|*.xlsx|CSV voor Excel (*.csv)|*.csv",
            FileName = Sanitize(_gameName) + " teksten.xlsx",
            InitialDirectory = RomOutput.LocalDir()
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dlg.FileName)!);
            TranslationSheet.Export(dlg.FileName, _lines.ToList());
            ShowSuccess("Excel-bestand opgeslagen: " + dlg.FileName);
            ShowError("");
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
            MessageBox.Show(this, ex.Message, "Excel exporteren");
        }
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        CommitGridEdits();
        var dlg = new OpenFileDialog
        {
            Title = "Teksten importeren",
            Filter = "Excel of CSV (*.xlsx;*.csv)|*.xlsx;*.csv|Excel-werkblad (*.xlsx)|*.xlsx|CSV (*.csv)|*.csv"
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var n = TranslationSheet.Import(dlg.FileName, _lines);
            _view?.Refresh();
            PhraseGrid.ItemsSource = DutchIdioms.Scan(_lines);
            UpdateProgress(new TranslateProgress
            {
                Total = _lines.Count,
                Done = _lines.Count - _lines.Count(DutchTranslator.IsPending),
                Message = StatusText.Text
            });
            ShowSuccess($"{n} regels uit Excel gezet. Die zijn nu de nieuwe vertaling. Klik daarna op ROM maken.");
            ShowError("");
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
            MessageBox.Show(this, ex.Message, "Excel importeren");
        }
    }

    private void ClearCache_Click(object sender, RoutedEventArgs e)
    {
        CommitGridEdits();
        var n = DutchTranslator.CacheCount();
        var go = MessageBox.Show(this,
            $"De opgeslagen vertaalcache wissen ({n} zinnen)? " +
            "Teksten die je nu in dit scherm ziet blijven staan. Alleen automatische hergebruik bij een volgende keer verdwijnt.",
            "Cache wissen", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (go != MessageBoxResult.Yes) return;
        DutchTranslator.ClearCache();
        CacheText.Text = "";
        ShowSuccess("Cache gewist. Handmatige teksten in de tabel blijven staan.");
        ShowError("");
    }

    private void SetBuildProgress(int percent, string message, int errors = 0)
    {
        WorkBar.Maximum = 100;
        WorkBar.Value = Math.Clamp(percent, 0, 100);
        StatusText.Text = message;
        CountText.Text = errors > 0
            ? $"{percent}% · {errors} fout(en)"
            : $"{percent}%";
        CacheText.Text = "";
    }

    private void ShowError(string text)
    {
        ErrorText.Text = text;
        ErrorText.Visibility = string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ShowSuccess(string text)
    {
        SuccessText.Text = text;
        SuccessText.Visibility = string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        CommitGridEdits();
        _cts?.Cancel();
        DialogResult = false;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_deckChosen || DialogResult == true || DialogResult == false) return;
        if (string.IsNullOrWhiteSpace(OutputPath) || !File.Exists(OutputPath)) return;
        var go = MessageBox.Show(this,
            "De Nederlandse ROM staat lokaal klaar." + Environment.NewLine + "Ook naar de Deck sturen?",
            "ROM klaar", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (go == MessageBoxResult.Cancel)
        {
            e.Cancel = true;
            return;
        }
        if (go == MessageBoxResult.Yes)
        {
            _deckChosen = true;
            DialogResult = true;
        }
    }

    private void LineGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit) return;
        if (e.EditingElement is TextBox box)
            box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
    }

    private void LineGrid_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) =>
        CommitGridEdits();

    private void CommitGridEdits()
    {
        try
        {
            LineGrid.CommitEdit(DataGridEditingUnit.Cell, true);
            LineGrid.CommitEdit(DataGridEditingUnit.Row, true);
        }
        catch { /* geen actieve cel */ }
    }

    private static string Sanitize(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, ' ');
        return string.Join(" ", name.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim();
    }
}
