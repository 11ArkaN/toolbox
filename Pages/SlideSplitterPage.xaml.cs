using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Toolbox.Models;
using Toolbox.Services;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Toolbox.Pages;

using WinRectangle = Microsoft.UI.Xaml.Shapes.Rectangle;

public sealed partial class SlideSplitterPage : Page
{
    private readonly PdfSlideSplitService _slideSplitService = new();
    private readonly List<WinRectangle> _fragmentLines = [];
    private StorageFile? _selectedPdfFile;
    private string? _outputDirectory;
    private bool _isUpdatingSplit;
    private bool _isDraggingSplit;

    public SlideSplitterPage()
    {
        InitializeComponent();
        ConfigureControls();
        Canvas.SetZIndex(SplitLine, 3);
        Canvas.SetZIndex(SplitLineHitTarget, 4);
    }

    private void ConfigureControls()
    {
        _isUpdatingSplit = true;
        SplitSlider.Maximum = 90;
        SplitSlider.Value = 50;
        SplitSlider.Minimum = 10;
        SplitNumberBox.Value = 50;
        _isUpdatingSplit = false;
    }

    private async void BrowsePdfButton_Click(object sender, RoutedEventArgs e)
    {
        StorageFile? file = await PickerService.PickPdfFileAsync();
        if (file is null)
        {
            return;
        }

        _selectedPdfFile = file;
        PdfPathBox.Text = file.Path;
        _outputDirectory ??= Path.Combine(Path.GetDirectoryName(file.Path) ?? Environment.CurrentDirectory, "slides_out");
        OutputPathBox.Text = _outputDirectory;
        ExtractButton.IsEnabled = true;
        SlideInfoBar.IsOpen = false;
        AppendLog("PDF loaded: " + Path.GetFileName(file.Path));

        await LoadPreviewAsync(file);
    }

    private async void ChooseOutputButton_Click(object sender, RoutedEventArgs e)
    {
        StorageFolder? folder = await PickerService.PickFolderAsync();
        if (folder is null)
        {
            return;
        }

        _outputDirectory = folder.Path;
        OutputPathBox.Text = folder.Path;
        AppendLog("Output folder: " + folder.Path);
    }

    private async void ExtractButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedPdfFile is null)
        {
            ShowInfo("Select a PDF first.", InfoBarSeverity.Warning);
            return;
        }

        string outputDirectory = _outputDirectory
            ?? Path.Combine(Path.GetDirectoryName(_selectedPdfFile.Path) ?? Environment.CurrentDirectory, "slides_out");
        _outputDirectory = outputDirectory;
        OutputPathBox.Text = outputDirectory;

        SlideSplitSettings settings = CollectSettings();
        if (!settings.ExportPng && !settings.ExportPdf)
        {
            ShowInfo("Select PNG, PDF, or both outputs.", InfoBarSeverity.Warning);
            return;
        }

        SetBusy(true);
        SlideInfoBar.IsOpen = false;
        SlideProgressBar.Value = 0;
        SlideStatusText.Text = "Starting...";
        AppendLog("Extraction started.");

        var progress = new Progress<SlideSplitProgress>(value =>
        {
            SlideProgressBar.Value = Math.Clamp(value.Percent, 0, 100);
            SlideStatusText.Text = value.Message;
            AppendLog(value.Message);
        });

        try
        {
            SlideSplitResult result = await Task.Run(async () =>
                await _slideSplitService.ExtractAsync(_selectedPdfFile, outputDirectory, settings, progress));

            string pdfText = result.PdfPath is null ? string.Empty : $"{Environment.NewLine}PDF: {Path.GetFileName(result.PdfPath)}";
            ShowInfo($"Created {result.SlideCount} output file(s).{pdfText}", InfoBarSeverity.Success);
            AppendLog("Extraction completed.");
        }
        catch (Exception ex)
        {
            SlideProgressBar.Value = 0;
            SlideStatusText.Text = "Failed";
            ShowInfo(ex.Message, InfoBarSeverity.Error);
            AppendLog("Error: " + ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task LoadPreviewAsync(StorageFile pdfFile)
    {
        SetBusy(true);
        SlideProgressBar.IsIndeterminate = true;
        SlideStatusText.Text = "Rendering preview...";

        try
        {
            byte[] previewBytes = await _slideSplitService.RenderFirstPagePreviewAsync(pdfFile);
            PreviewImage.Source = await LoadBitmapAsync(previewBytes);
            PreviewPlaceholder.Visibility = Visibility.Collapsed;
            SlideStatusText.Text = "Ready";
            SlideProgressBar.Value = 0;
            UpdateSplitLinePosition();
        }
        catch (Exception ex)
        {
            PreviewImage.Source = null;
            PreviewPlaceholder.Visibility = Visibility.Visible;
            ShowInfo(ex.Message, InfoBarSeverity.Error);
            AppendLog("Preview error: " + ex.Message);
        }
        finally
        {
            SlideProgressBar.IsIndeterminate = false;
            SetBusy(false);
        }
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

    private SlideSplitSettings CollectSettings()
    {
        return new SlideSplitSettings(
            SplitSlider.Value,
            NumberOrDefault(MarginLeftBox, 2),
            NumberOrDefault(MarginRightBox, 2),
            NumberOrDefault(MarginTopBox, 2),
            NumberOrDefault(MarginBottomBox, 2),
            (int)NumberOrDefault(GutterBox, 20),
            (int)NumberOrDefault(DpiBox, 300),
            ExportPngCheckBox.IsChecked == true,
            ExportPdfCheckBox.IsChecked == true,
            VisualCropCheckBox.IsChecked == true,
            (int)NumberOrDefault(FragmentCountBox, 2));
    }

    private static double NumberOrDefault(NumberBox numberBox, double fallback)
    {
        return double.IsFinite(numberBox.Value) ? numberBox.Value : fallback;
    }

    private void SplitSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_isUpdatingSplit)
        {
            return;
        }

        _isUpdatingSplit = true;
        SplitNumberBox.Value = e.NewValue;
        _isUpdatingSplit = false;
        UpdateSplitLinePosition();
    }

    private void SplitNumberBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_isUpdatingSplit || !double.IsFinite(args.NewValue))
        {
            return;
        }

        _isUpdatingSplit = true;
        SplitSlider.Value = Math.Clamp(args.NewValue, SplitSlider.Minimum, SplitSlider.Maximum);
        _isUpdatingSplit = false;
        UpdateSplitLinePosition();
    }

    private void VisualCropSettings_Changed(object sender, RoutedEventArgs e)
    {
        FragmentCountBox.IsEnabled = VisualCropCheckBox.IsChecked == true;
        UpdateSplitLinePosition();
    }

    private void FragmentCountBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        UpdateSplitLinePosition();
    }

    private void PreviewOverlay_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateSplitLinePosition();
    }

    private void PreviewOverlay_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (PreviewImage.Source is null)
        {
            return;
        }

        _isDraggingSplit = true;
        PreviewOverlay.CapturePointer(e.Pointer);
        SetSplitFromPointer(e);
    }

    private void PreviewOverlay_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_isDraggingSplit)
        {
            SetSplitFromPointer(e);
        }
    }

    private void PreviewOverlay_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDraggingSplit)
        {
            return;
        }

        _isDraggingSplit = false;
        PreviewOverlay.ReleasePointerCapture(e.Pointer);
    }

    private void SetSplitFromPointer(PointerRoutedEventArgs e)
    {
        if (PreviewOverlay.ActualHeight <= 0)
        {
            return;
        }

        double y = e.GetCurrentPoint(PreviewOverlay).Position.Y;
        double percent = Math.Clamp(y / PreviewOverlay.ActualHeight * 100.0, SplitSlider.Minimum, SplitSlider.Maximum);
        SplitSlider.Value = percent;
    }

    private void UpdateSplitLinePosition()
    {
        double width = PreviewOverlay.ActualWidth;
        double height = PreviewOverlay.ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        double splitY = height * SplitSlider.Value / 100.0;
        SplitLine.Width = width;
        SplitLineHitTarget.Width = width;
        Canvas.SetTop(SplitLine, Math.Clamp(splitY - 1.5, 0, height));
        Canvas.SetTop(SplitLineHitTarget, Math.Clamp(splitY - 9, 0, height));

        RefreshFragmentLines(width, height);
    }

    private void RefreshFragmentLines(double width, double height)
    {
        foreach (WinRectangle line in _fragmentLines)
        {
            PreviewOverlay.Children.Remove(line);
        }

        _fragmentLines.Clear();

        if (PreviewImage.Source is null || VisualCropCheckBox.IsChecked != true)
        {
            return;
        }

        int fragments = Math.Max(2, (int)NumberOrDefault(FragmentCountBox, 2));
        double split = SplitSlider.Value;
        AddFragmentLines(width, height, startPercent: 0, endPercent: split, fragments);
        AddFragmentLines(width, height, startPercent: split, endPercent: 100, fragments);
    }

    private void AddFragmentLines(double width, double height, double startPercent, double endPercent, int fragments)
    {
        for (int index = 1; index < fragments; index++)
        {
            double percent = startPercent + ((endPercent - startPercent) * index / fragments);
            double y = height * percent / 100.0;
            var line = new WinRectangle
            {
                Width = width,
                Height = 2,
                Fill = new SolidColorBrush(Colors.MediumSeaGreen),
                Opacity = 0.85
            };

            Canvas.SetTop(line, Math.Clamp(y - 1, 0, height));
            Canvas.SetZIndex(line, 2);
            PreviewOverlay.Children.Add(line);
            _fragmentLines.Add(line);
        }
    }

    private void SetBusy(bool isBusy)
    {
        BrowsePdfButton.IsEnabled = !isBusy;
        ChooseOutputButton.IsEnabled = !isBusy;
        ExtractButton.IsEnabled = !isBusy && _selectedPdfFile is not null;
        SplitSlider.IsEnabled = !isBusy;
        SplitNumberBox.IsEnabled = !isBusy;
        MarginLeftBox.IsEnabled = !isBusy;
        MarginRightBox.IsEnabled = !isBusy;
        MarginTopBox.IsEnabled = !isBusy;
        MarginBottomBox.IsEnabled = !isBusy;
        GutterBox.IsEnabled = !isBusy;
        DpiBox.IsEnabled = !isBusy;
        ExportPngCheckBox.IsEnabled = !isBusy;
        ExportPdfCheckBox.IsEnabled = !isBusy;
        VisualCropCheckBox.IsEnabled = !isBusy;
        FragmentCountBox.IsEnabled = !isBusy && VisualCropCheckBox.IsChecked == true;
    }

    private void AppendLog(string message)
    {
        string line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        SlideLogBox.Text = string.IsNullOrEmpty(SlideLogBox.Text)
            ? line
            : SlideLogBox.Text + Environment.NewLine + line;
        SlideLogBox.SelectionStart = SlideLogBox.Text.Length;
    }

    private void ShowInfo(string message, InfoBarSeverity severity)
    {
        SlideInfoBar.Message = message;
        SlideInfoBar.Severity = severity;
        SlideInfoBar.IsOpen = true;
    }
}
