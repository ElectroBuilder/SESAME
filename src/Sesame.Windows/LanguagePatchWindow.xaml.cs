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
            StatusText.Text = "Searching for text in the ROM…";
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
                    "Donkey Kong 64 text table recognized. Only real in-game sentences are in the table. " +
                    "A Dutch sentence must fit the original slot. The original file stays.";
            else if (_lines.Any(l => l.Codec == "sm64"))
                HintText.Text =
                    "Super Mario 64: each dialogue is translated as a whole (story and joke, not loose words). " +
                    "A Dutch sentence must fit the original slot. The original file stays.";
            else if (_lines.Any(l => l.Generic))
                HintText.Text =
                    "No Banjo dialogue table. SESAME looks for English sentences (ASCII or unpacked Rare blocks) " +
                    "and skips binary junk. Games with their own font will not show everything. " +
                    "Lines that are too long are truncated. The original file stays.";
            UpdateProgress(new TranslateProgress { Total = _lines.Count, Done = 0, Message = "Ready to translate…" });
            if (_lines.Count == 0)
            {
                StatusText.Text = "No English dialogue found in this dump.";
                return;
            }

            await TranslateNow(tryOnline: true);
            var pending = _lines.Count(DutchTranslator.IsPending);
            StatusText.Text = pending == 0
                ? $"{_lines.Count} lines done (natural Dutch, not word-for-word)."
                : $"{_lines.Count - pending} done · {pending} still open. Use Translate online if a line stays English.";

            _view?.Refresh();
            ApplyBtn.IsEnabled = true;
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
            MessageBox.Show(this, ex.Message, "Language patch");
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
                ? "A key is already stored. Type a new one to replace it."
                : "Paste a DeepL key first.";
            UpdateDeepLStatus();
            return;
        }

        TranslateSettings.SaveDeepLKey(typed);
        DeepLKeyBox.Clear();
        UpdateDeepLStatus();
        StatusText.Text = "DeepL key saved. Click Translate online for contextual sentences.";
    }

    private void ClearDeepL_Click(object sender, RoutedEventArgs e)
    {
        if (!TranslateSettings.HasDeepL && string.IsNullOrEmpty(DeepLKeyBox.Password))
            return;
        if (MessageBox.Show(this, "Remove the saved DeepL key?", "Language patch",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        TranslateSettings.ClearKey();
        DeepLKeyBox.Clear();
        UpdateDeepLStatus();
        StatusText.Text = "DeepL key cleared. Online translate falls back to Google.";
    }

    private void UpdateDeepLStatus()
    {
        DeepLStatus.Text = TranslateSettings.HasDeepL
            ? "Key saved. DeepL is on: whole dialogues, informal Dutch (je/jij), names stay English."
            : "No DeepL key: Google as fallback. Free key via deepl.com/pro-api (Free).";
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
                ? $"{done} of {_lines.Count} done. Check idioms, then build the ROM."
                : tryOnline
                    ? $"{done} of {_lines.Count} translated. {left} short names/shouts stay English."
                    : $"{filled} rows updated locally. {left} stay English (often names or shouts). Your manual edits were not overwritten.";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Translate stopped. What was already done is saved.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Translate partly failed: " + ex.Message;
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
        CountText.Text = $"{p.Done} translated · {p.Remaining} remaining · {p.Total} total";
        CacheText.Text = p.FromCache > 0 ? $"{p.FromCache} from cache" : "";
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
        if (kind.Contains("Dialogue", StringComparison.OrdinalIgnoreCase) &&
            line.Kind is not (BkTextKind.Dialog or BkTextKind.Raw))
            return false;
        if (kind.Contains("quiz", StringComparison.OrdinalIgnoreCase) &&
            line.Kind is not (BkTextKind.Quiz or BkTextKind.Grunty))
            return false;
        if (kind.Contains("ROM text", StringComparison.OrdinalIgnoreCase) && line.Kind != BkTextKind.Raw)
            return false;
        if (kind.Contains("Changed", StringComparison.OrdinalIgnoreCase) && !line.Changed)
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
                $"{tooLong} lines are longer than the available ROM slot. " +
                "Those lines are shortened so the game does not crash; the rest stays complete. " +
                "Empty lines and accented letters are made safe automatically. Continue?",
                "Language patch", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (go != MessageBoxResult.Yes) return;
        }

        ApplyBtn.IsEnabled = false;
        RetranslateBtn.IsEnabled = false;
        OnlineBtn.IsEnabled = false;
        DeckBtn.Visibility = Visibility.Collapsed;
        ShowError("");
        ShowSuccess("");
        SetBuildProgress(1, "Build ROM started…");
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

            SetBuildProgress(97, "Writing file…", result.Errors);
            var ext = CartRom.Extension(result.Rom);
            var name = Sanitize(_gameName) + " (NL)" + ext;
            var saved = await RomOutput.SaveAsync(result.Rom, name, (pct, msg) =>
                Dispatcher.BeginInvoke(() => SetBuildProgress(pct, msg, result.Errors)));
            OutputPath = saved.Path;
            CountText.Text = result.Errors > 0 ? $"100% · {result.Errors} error(s)" : "100%";
            ShowSuccess(result.Summary + Environment.NewLine + saved.Message);
            if (result.Errors > 0)
            {
                ShowError(result.LastError ?? "Some of the text did not fit in the ROM.");
                SetBuildProgress(100, "ROM saved locally, but not everything fitted.", result.Errors);
                DeckBtn.Visibility = Visibility.Visible;
                MessageBox.Show(this,
                    (result.LastError ?? "Not all texts fitted in the ROM.") +
                    Environment.NewLine + Environment.NewLine +
                    "The window stays open. Shorten the red lines and click Build ROM again.",
                    "Build ROM");
            }
            else
            {
                ShowError("");
                SetBuildProgress(100, "ROM ready. Putting it on the Deck…");
                sendToDeck = true;
            }
        }
        catch (Exception ex)
        {
            ShowSuccess("");
            ShowError(ex.Message);
            StatusText.Text = "Build ROM failed.";
            SetBuildProgress(0, StatusText.Text);
            MessageBox.Show(this, ex.Message, "Build ROM");
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
            MessageBox.Show(this, "There is no ROM file yet. Click Build ROM first.", "Language patch");
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
            Title = "Export texts",
            Filter = "Excel workbook (*.xlsx)|*.xlsx|CSV for Excel (*.csv)|*.csv",
            FileName = Sanitize(_gameName) + " texts.xlsx",
            InitialDirectory = RomOutput.LocalDir()
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dlg.FileName)!);
            TranslationSheet.Export(dlg.FileName, _lines.ToList());
            ShowSuccess("Excel file saved: " + dlg.FileName);
            ShowError("");
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
            MessageBox.Show(this, ex.Message, "Export Excel");
        }
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        CommitGridEdits();
        var dlg = new OpenFileDialog
        {
            Title = "Import texts",
            Filter = "Excel or CSV (*.xlsx;*.csv)|*.xlsx;*.csv|Excel workbook (*.xlsx)|*.xlsx|CSV (*.csv)|*.csv"
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
            ShowSuccess($"{n} rows taken from Excel. Those are now the new translation. Then click Build ROM.");
            ShowError("");
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
            MessageBox.Show(this, ex.Message, "Import Excel");
        }
    }

    private void ClearCache_Click(object sender, RoutedEventArgs e)
    {
        CommitGridEdits();
        var n = DutchTranslator.CacheCount();
        var go = MessageBox.Show(this,
            $"Clear the stored translation cache ({n} lines)? " +
            "Texts you see on this screen stay. Only automatic reuse next time is removed.",
            "Clear cache", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (go != MessageBoxResult.Yes) return;
        DutchTranslator.ClearCache();
        CacheText.Text = "";
        ShowSuccess("Cache cleared. Manual texts in the table stay.");
        ShowError("");
    }

    private void SetBuildProgress(int percent, string message, int errors = 0)
    {
        WorkBar.Maximum = 100;
        WorkBar.Value = Math.Clamp(percent, 0, 100);
        StatusText.Text = message;
        CountText.Text = errors > 0
            ? $"{percent}% · {errors} error(s)"
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
            "The Dutch ROM is ready locally." + Environment.NewLine + "Also send it to the Deck?",
            "ROM ready", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
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
