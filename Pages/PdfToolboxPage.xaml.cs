using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Toolbox.Models;
using Toolbox.Services;
using Windows.Storage;

namespace Toolbox.Pages;

public sealed partial class PdfToolboxPage : Page
{
    private readonly PdfToolboxService _pdfService = new();
    private readonly ObservableCollection<string> _pdfNames = [];
    private readonly List<StorageFile> _pdfFiles = [];
    private string? _outputDirectory;

    public PdfToolboxPage()
    {
        InitializeComponent();
        PdfListView.ItemsSource = _pdfNames;
        UpdateOperationVisibility();
        UpdateRunState();
    }

    private async void AddPdfsButton_Click(object sender, RoutedEventArgs e)
    {
        IReadOnlyList<StorageFile> files = await PickerService.PickPdfFilesAsync();
        int added = 0;
        foreach (StorageFile file in files)
        {
            if (_pdfFiles.Any(existing => string.Equals(existing.Path, file.Path, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            _pdfFiles.Add(file);
            _pdfNames.Add(Path.GetFileName(file.Path));
            added++;
        }

        if (_outputDirectory is null && _pdfFiles.Count > 0)
        {
            _outputDirectory = Path.Combine(Path.GetDirectoryName(_pdfFiles[0].Path) ?? Environment.CurrentDirectory, "pdf_out");
            PdfOutputFolderBox.Text = _outputDirectory;
        }

        ShowInfo(added == 0 ? "No new PDFs were added." : $"Added {added} PDF file(s).", added == 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Informational);
        UpdateRunState();
    }

    private void ClearPdfsButton_Click(object sender, RoutedEventArgs e)
    {
        _pdfFiles.Clear();
        _pdfNames.Clear();
        UpdateRunState();
    }

    private async void ChoosePdfOutputButton_Click(object sender, RoutedEventArgs e)
    {
        StorageFolder? folder = await PickerService.PickFolderAsync();
        if (folder is null)
        {
            return;
        }

        _outputDirectory = folder.Path;
        PdfOutputFolderBox.Text = folder.Path;
    }

    private void PdfOperationComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateOperationVisibility();
        UpdateRunState();
    }

    private async void RunPdfToolButton_Click(object sender, RoutedEventArgs e)
    {
        if (_pdfFiles.Count == 0)
        {
            ShowInfo("Select at least one PDF.", InfoBarSeverity.Warning);
            return;
        }

        string outputDirectory = _outputDirectory
            ?? Path.Combine(Path.GetDirectoryName(_pdfFiles[0].Path) ?? Environment.CurrentDirectory, "pdf_out");
        _outputDirectory = outputDirectory;
        PdfOutputFolderBox.Text = outputDirectory;

        SetBusy(true);
        PdfProgressBar.Value = 0;
        PdfInfoBar.IsOpen = false;
        PdfLogBox.Text = string.Empty;

        var progress = new Progress<ToolProgress>(value =>
        {
            PdfProgressBar.Value = Math.Clamp(value.Percent, 0, 100);
            AppendLog(value.Message);
        });

        try
        {
            int operation = PdfOperationComboBox.SelectedIndex;
            switch (operation)
            {
                case 0:
                    string merged = await _pdfService.MergeAsync(_pdfFiles, outputDirectory, PdfOutputNameBox.Text, progress);
                    ShowInfo($"Created {Path.GetFileName(merged)}.", InfoBarSeverity.Success);
                    break;
                case 1:
                    IReadOnlyList<string> split = await _pdfService.SplitEveryPageAsync(_pdfFiles, outputDirectory, progress);
                    ShowInfo($"Created {split.Count} PDF file(s).", InfoBarSeverity.Success);
                    break;
                case 2:
                    string edited = await _pdfService.EditPagesAsync(
                        _pdfFiles[0],
                        outputDirectory,
                        PdfOutputNameBox.Text,
                        DeletePagesBox.Text,
                        RotatePagesBox.Text,
                        SelectedRotateDegrees(),
                        progress);
                    ShowInfo($"Created {Path.GetFileName(edited)}.", InfoBarSeverity.Success);
                    break;
                case 3:
                    IReadOnlyList<string> compressed = await _pdfService.CompressAsync(
                        _pdfFiles,
                        outputDirectory,
                        IntegerOrDefault(PdfDpiBox, 150),
                        IntegerOrDefault(PdfQualityBox, 82),
                        progress);
                    ShowInfo($"Created {compressed.Count} compressed PDF file(s).", InfoBarSeverity.Success);
                    break;
                default:
                    IReadOnlyList<string> images = await _pdfService.ExportPagesToImagesAsync(
                        _pdfFiles,
                        outputDirectory,
                        PdfImageFormatComboBox.SelectedIndex == 1 ? ImageOutputFormat.Png : ImageOutputFormat.Jpg,
                        IntegerOrDefault(PdfDpiBox, 150),
                        IntegerOrDefault(PdfQualityBox, 82),
                        progress);
                    ShowInfo($"Exported {images.Count} image file(s).", InfoBarSeverity.Success);
                    break;
            }
        }
        catch (Exception ex)
        {
            ShowInfo(ex.Message, InfoBarSeverity.Error);
            AppendLog("Error: " + ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void UpdateOperationVisibility()
    {
        int operation = PdfOperationComboBox.SelectedIndex;
        PageEditOptions.Visibility = operation == 2 ? Visibility.Visible : Visibility.Collapsed;
        RasterOptions.Visibility = operation is 3 or 4 ? Visibility.Visible : Visibility.Collapsed;
        PdfImageFormatComboBox.Visibility = operation == 4 ? Visibility.Visible : Visibility.Collapsed;
        PdfOutputNameBox.Visibility = operation is 0 or 2 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateRunState()
    {
        PdfSummaryText.Text = _pdfFiles.Count == 0
            ? "No PDFs selected"
            : $"{_pdfFiles.Count} PDF file(s) selected";
        RunPdfToolButton.IsEnabled = _pdfFiles.Count > 0;
    }

    private void SetBusy(bool isBusy)
    {
        AddPdfsButton.IsEnabled = !isBusy;
        ClearPdfsButton.IsEnabled = !isBusy;
        ChoosePdfOutputButton.IsEnabled = !isBusy;
        PdfOperationComboBox.IsEnabled = !isBusy;
        RunPdfToolButton.IsEnabled = !isBusy && _pdfFiles.Count > 0;
        PdfProgressBar.IsIndeterminate = isBusy && PdfProgressBar.Value <= 0;
    }

    private int SelectedRotateDegrees()
    {
        return RotateDegreesComboBox.SelectedIndex switch
        {
            0 => 90,
            2 => 270,
            _ => 180
        };
    }

    private static int IntegerOrDefault(NumberBox box, int fallback)
    {
        return double.IsFinite(box.Value) ? (int)Math.Round(box.Value) : fallback;
    }

    private void AppendLog(string message)
    {
        string line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        PdfLogBox.Text = string.IsNullOrEmpty(PdfLogBox.Text)
            ? line
            : PdfLogBox.Text + Environment.NewLine + line;
        PdfLogBox.SelectionStart = PdfLogBox.Text.Length;
    }

    private void ShowInfo(string message, InfoBarSeverity severity)
    {
        PdfInfoBar.Message = message;
        PdfInfoBar.Severity = severity;
        PdfInfoBar.IsOpen = true;
    }
}
