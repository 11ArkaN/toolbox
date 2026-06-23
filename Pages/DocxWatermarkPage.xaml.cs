using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Toolbox.Models;
using Toolbox.Services;
using Windows.System;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Toolbox.Pages;

public sealed partial class DocxWatermarkPage : Page
{
    private readonly DocxImageWatermarkService _watermarkService = new();
    private readonly ObservableCollection<string> _docxNames = [];
    private readonly ObservableCollection<DocxWatermarkPreviewItem> _previewItems = [];
    private readonly List<string> _docxPaths = [];
    private readonly Stack<IReadOnlyList<string>> _addHistory = new();
    private string? _outputDirectory;
    private string? _fontPath;
    private CancellationTokenSource? _previewRefreshCts;
    private bool _isInitialized;
    private bool _isBusy;
    private bool _isPreviewBusy;

    public DocxWatermarkPage()
    {
        InitializeComponent();
        ConfigureControls();
        DocxListView.ItemsSource = _docxNames;
        WatermarkPreviewListView.ItemsSource = _previewItems;
        _isInitialized = true;
        UpdateRunState();
    }

    private void ConfigureControls()
    {
        OpacityBox.Minimum = 0;
        OpacityBox.Maximum = 1;
        OpacityBox.SmallChange = 0.01;
        OpacityBox.LargeChange = 0.05;
        OpacityBox.Value = 0.085;

        MinFontSizeBox.Minimum = 1;
        MinFontSizeBox.Maximum = 120;
        MinFontSizeBox.Value = 7;

        MaxFontSizeBox.Minimum = 1;
        MaxFontSizeBox.Maximum = 180;
        MaxFontSizeBox.Value = 16;
    }

    private async void AddDocxButton_Click(object sender, RoutedEventArgs e)
    {
        IReadOnlyList<StorageFile> files = await PickerService.PickDocxFilesAsync();
        AddDocuments(files.Select(file => file.Path));
    }

    private void UndoAddDocxButton_Click(object sender, RoutedEventArgs e)
    {
        if (_addHistory.Count == 0)
        {
            return;
        }

        IReadOnlyList<string> lastAdded = _addHistory.Pop();
        foreach (string path in lastAdded)
        {
            int index = _docxPaths.FindIndex(existing => string.Equals(existing, path, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                continue;
            }

            _docxPaths.RemoveAt(index);
            _docxNames.RemoveAt(index);
        }

        ShowInfo($"Removed {lastAdded.Count} last-added DOCX file(s).", InfoBarSeverity.Informational);
        UpdateRunState();
        QueuePreviewRefresh();
    }

    private void ClearDocxButton_Click(object sender, RoutedEventArgs e)
    {
        _docxPaths.Clear();
        _docxNames.Clear();
        _addHistory.Clear();
        _previewItems.Clear();
        PreviewSummaryText.Text = "No DOCX selected";
        UpdateRunState();
    }

    private async void ChooseDocxOutputButton_Click(object sender, RoutedEventArgs e)
    {
        StorageFolder? folder = await PickerService.PickFolderAsync();
        if (folder is null)
        {
            return;
        }

        _outputDirectory = folder.Path;
        DocxOutputFolderBox.Text = folder.Path;
    }

    private async void ChooseFontButton_Click(object sender, RoutedEventArgs e)
    {
        StorageFile? file = await PickerService.PickFontFileAsync();
        if (file is null)
        {
            return;
        }

        _fontPath = file.Path;
        FontPathBox.Text = file.Path;
        QueuePreviewRefresh();
    }

    private async void RefreshPreviewButton_Click(object sender, RoutedEventArgs e)
    {
        _previewRefreshCts?.Cancel();
        await RefreshPreviewAsync(CancellationToken.None, showErrors: true);
    }

    private async void ApplyWatermarkButton_Click(object sender, RoutedEventArgs e)
    {
        if (_docxPaths.Count == 0)
        {
            ShowInfo("Select at least one DOCX file.", InfoBarSeverity.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(WatermarkTextBox.Text))
        {
            ShowInfo("Enter watermark text.", InfoBarSeverity.Warning);
            return;
        }

        string outputDirectory = _outputDirectory
            ?? Path.Combine(Path.GetDirectoryName(_docxPaths[0]) ?? Environment.CurrentDirectory, "docx_watermark_out");
        _outputDirectory = outputDirectory;
        DocxOutputFolderBox.Text = outputDirectory;

        _previewRefreshCts?.Cancel();
        SetBusy(true);
        DocxWatermarkProgressBar.Value = 0;
        DocxWatermarkInfoBar.IsOpen = false;

        var progress = new Progress<ToolProgress>(value =>
        {
            DocxWatermarkProgressBar.Value = Math.Clamp(value.Percent, 0, 100);
            PreviewSummaryText.Text = value.Message;
        });

        try
        {
            IReadOnlyList<DocxWatermarkDocumentResult> results = await _watermarkService.ProcessDocumentsAsync(
                _docxPaths.ToArray(),
                outputDirectory,
                DocxSuffixBox.Text,
                CollectOptions(),
                progress);

            int images = results.Sum(result => result.ProcessedImages);
            int skipped = results.Sum(result => result.SkippedImages);
            string skippedText = skipped > 0 ? $" Skipped {skipped} image(s)." : string.Empty;
            ShowInfo($"Created {results.Count} DOCX file(s) with {images} watermarked image(s).{skippedText}", InfoBarSeverity.Success);
            UpdateRunState();
        }
        catch (Exception ex)
        {
            ShowInfo(ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void WatermarkSettings_Changed(object sender, RoutedEventArgs e) => QueuePreviewRefresh();

    private void WatermarkSettings_Changed(object sender, SelectionChangedEventArgs e) => QueuePreviewRefresh();

    private void WatermarkSettings_Changed(object sender, TextChangedEventArgs e) => QueuePreviewRefresh();

    private void WatermarkSettings_Changed(NumberBox sender, NumberBoxValueChangedEventArgs args) => QueuePreviewRefresh();

    private void WatermarkPreviewListView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not DocxWatermarkPreviewItem item)
        {
            return;
        }

        FullscreenPreviewImage.Source = item.Image;
        FullscreenPreviewTitle.Text = item.Title;
        FullscreenPreviewDetails.Text = item.Details;
        FullscreenPreviewOverlay.Visibility = Visibility.Visible;
        FullscreenPreviewOverlay.Focus(FocusState.Programmatic);
    }

    private void CloseFullscreenPreviewButton_Click(object sender, RoutedEventArgs e)
    {
        CloseFullscreenPreview();
    }

    private void FullscreenPreviewOverlay_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape)
        {
            return;
        }

        CloseFullscreenPreview();
        e.Handled = true;
    }

    private void CloseFullscreenPreview()
    {
        FullscreenPreviewOverlay.Visibility = Visibility.Collapsed;
        FullscreenPreviewImage.Source = null;
    }

    private void AddDocuments(IEnumerable<string> paths)
    {
        var addedPaths = new List<string>();
        foreach (string path in paths)
        {
            if (!File.Exists(path)
                || !Path.GetExtension(path).Equals(".docx", StringComparison.OrdinalIgnoreCase)
                || _docxPaths.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            _docxPaths.Add(path);
            _docxNames.Add(Path.GetFileName(path));
            addedPaths.Add(path);
        }

        if (_outputDirectory is null && _docxPaths.Count > 0)
        {
            _outputDirectory = Path.Combine(Path.GetDirectoryName(_docxPaths[0]) ?? Environment.CurrentDirectory, "docx_watermark_out");
            DocxOutputFolderBox.Text = _outputDirectory;
        }

        if (addedPaths.Count > 0)
        {
            _addHistory.Push(addedPaths);
        }

        ShowInfo(
            addedPaths.Count == 0 ? "No new DOCX files were added." : $"Added {addedPaths.Count} DOCX file(s).",
            addedPaths.Count == 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Informational);

        UpdateRunState();
        QueuePreviewRefresh();
    }

    private void QueuePreviewRefresh()
    {
        if (!_isInitialized)
        {
            return;
        }

        _previewRefreshCts?.Cancel();

        if (_docxPaths.Count == 0)
        {
            _previewItems.Clear();
            PreviewSummaryText.Text = "No DOCX selected";
            UpdateRunState();
            return;
        }

        var cts = new CancellationTokenSource();
        _previewRefreshCts = cts;
        _ = RefreshPreviewAfterDelayAsync(cts.Token);
    }

    private async Task RefreshPreviewAfterDelayAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(450, cancellationToken);
            await RefreshPreviewAsync(cancellationToken, showErrors: false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task RefreshPreviewAsync(CancellationToken cancellationToken, bool showErrors)
    {
        if (_docxPaths.Count == 0)
        {
            _previewItems.Clear();
            PreviewSummaryText.Text = "No DOCX selected";
            UpdateRunState();
            return;
        }

        SetPreviewBusy(true);
        _previewItems.Clear();
        DocxWatermarkProgressBar.Value = 0;
        PreviewSummaryText.Text = "Loading preview...";

        try
        {
            DocxWatermarkOptions options = CollectOptions();
            int loaded = 0;
            for (int documentIndex = 0; documentIndex < _docxPaths.Count; documentIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string documentPath = _docxPaths[documentIndex];
                var progress = new Progress<ToolProgress>(value =>
                {
                    double combined = (documentIndex + value.Percent / 100.0) / _docxPaths.Count * 100.0;
                    DocxWatermarkProgressBar.Value = Math.Clamp(combined, 0, 100);
                    PreviewSummaryText.Text = value.Message;
                });

                IReadOnlyList<DocxEmbeddedImagePreview> previews = await _watermarkService.BuildPreviewAsync(
                    documentPath,
                    options,
                    progress,
                    cancellationToken);

                foreach (DocxEmbeddedImagePreview preview in previews)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    BitmapImage bitmap = await LoadBitmapAsync(preview.PngBytes);
                    _previewItems.Add(new DocxWatermarkPreviewItem(
                        bitmap,
                        $"{Path.GetFileName(preview.DocumentPath)} - {Path.GetFileName(preview.MediaPath)}",
                        $"{preview.Width} x {preview.Height}px, visible regions: {preview.VisibleRegionCount}"));
                    loaded++;
                }
            }

            PreviewSummaryText.Text = loaded == 0
                ? "No supported embedded images found"
                : $"{loaded} embedded image(s) in preview";
            DocxWatermarkProgressBar.Value = loaded == 0 ? 0 : 100;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (showErrors)
            {
                ShowInfo(ex.Message, InfoBarSeverity.Error);
            }

            PreviewSummaryText.Text = "Preview failed";
        }
        finally
        {
            SetPreviewBusy(false);
        }
    }

    private DocxWatermarkOptions CollectOptions()
    {
        int minFont = Math.Max(1, IntegerOrDefault(MinFontSizeBox, 7));
        int maxFont = Math.Max(minFont, IntegerOrDefault(MaxFontSizeBox, 16));

        return new DocxWatermarkOptions(
            WatermarkTextBox.Text.Replace("\\n", Environment.NewLine),
            Math.Clamp(NumberOrDefault(OpacityBox, 0.085), 0, 1),
            PositionComboBox.SelectedIndex switch
            {
                1 => DocxWatermarkPosition.BottomLeft,
                2 => DocxWatermarkPosition.BottomRight,
                3 => DocxWatermarkPosition.Center,
                _ => DocxWatermarkPosition.Random
            },
            string.IsNullOrWhiteSpace(SeedBox.Text) ? "12345" : SeedBox.Text,
            _fontPath,
            minFont,
            maxFont);
    }

    private void UpdateRunState()
    {
        bool hasDocuments = _docxPaths.Count > 0;
        ClearDocxButton.IsEnabled = hasDocuments && !_isBusy;
        UndoAddDocxButton.IsEnabled = _addHistory.Count > 0 && !_isBusy;
        RefreshPreviewButton.IsEnabled = hasDocuments && !_isBusy && !_isPreviewBusy;
        ApplyWatermarkButton.IsEnabled = hasDocuments && !_isBusy && !_isPreviewBusy;

        if (!hasDocuments)
        {
            PreviewSummaryText.Text = "No DOCX selected";
        }
        else if (!_isPreviewBusy && _previewItems.Count == 0)
        {
            PreviewSummaryText.Text = $"{_docxPaths.Count} DOCX file(s) selected";
        }
    }

    private void SetBusy(bool isBusy)
    {
        _isBusy = isBusy;
        AddDocxButton.IsEnabled = !isBusy;
        ChooseDocxOutputButton.IsEnabled = !isBusy;
        ChooseFontButton.IsEnabled = !isBusy;
        DocxSuffixBox.IsEnabled = !isBusy;
        WatermarkTextBox.IsEnabled = !isBusy;
        OpacityBox.IsEnabled = !isBusy;
        PositionComboBox.IsEnabled = !isBusy;
        SeedBox.IsEnabled = !isBusy;
        MinFontSizeBox.IsEnabled = !isBusy;
        MaxFontSizeBox.IsEnabled = !isBusy;
        DocxWatermarkProgressBar.IsIndeterminate = isBusy && DocxWatermarkProgressBar.Value <= 0;
        UpdateRunState();
    }

    private void SetPreviewBusy(bool isPreviewBusy)
    {
        _isPreviewBusy = isPreviewBusy;
        DocxWatermarkProgressBar.IsIndeterminate = isPreviewBusy && DocxWatermarkProgressBar.Value <= 0;
        UpdateRunState();
    }

    private static int IntegerOrDefault(NumberBox box, int fallback)
    {
        return double.IsFinite(box.Value) ? (int)Math.Round(box.Value) : fallback;
    }

    private static double NumberOrDefault(NumberBox box, double fallback)
    {
        return double.IsFinite(box.Value) ? box.Value : fallback;
    }

    private static async Task<BitmapImage> LoadBitmapAsync(byte[] bytes)
    {
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(bytes);
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
        }

        stream.Seek(0);
        var bitmap = new BitmapImage();
        await bitmap.SetSourceAsync(stream);
        return bitmap;
    }

    private void ShowInfo(string message, InfoBarSeverity severity)
    {
        DocxWatermarkInfoBar.Message = message;
        DocxWatermarkInfoBar.Severity = severity;
        DocxWatermarkInfoBar.IsOpen = true;
    }

    public sealed record DocxWatermarkPreviewItem(BitmapImage Image, string Title, string Details);
}
