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
using WinPoint = Windows.Foundation.Point;
using WinRect = Windows.Foundation.Rect;

public sealed partial class SlideSplitterPage : Page
{
    private readonly PdfSlideSplitService _slideSplitService = new();
    private readonly List<NormalizedCropRectangle> _visualCropRectangles = [];
    private readonly List<WinRectangle> _visualCropShapes = [];
    private StorageFile? _selectedPdfFile;
    private string? _outputDirectory;
    private double _previewPixelWidth;
    private double _previewPixelHeight;
    private bool _isUpdatingSplit;
    private bool _isDraggingSplit;
    private bool _isDrawingCropRectangle;
    private WinPoint _cropStartPoint;
    private WinRectangle? _activeCropShape;

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
        ClearCropRectangles();
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

        if (settings.VisualCropMode && settings.VisualCropRectangles.Count < 2)
        {
            ShowInfo("Draw at least two crop rectangles before exporting in visual crop mode.", InfoBarSeverity.Warning);
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
            BitmapImage preview = await LoadBitmapAsync(previewBytes);
            _previewPixelWidth = preview.PixelWidth;
            _previewPixelHeight = preview.PixelHeight;
            PreviewImage.Source = preview;
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
            _visualCropRectangles.ToArray());
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
        ClearCropRectanglesButton.IsEnabled = VisualCropCheckBox.IsChecked == true && _visualCropRectangles.Count > 0;
        UpdateCropRectangleCount();
        UpdateSplitLinePosition();
    }

    private void ClearCropRectanglesButton_Click(object sender, RoutedEventArgs e)
    {
        ClearCropRectangles();
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

        if (VisualCropCheckBox.IsChecked == true)
        {
            StartCropRectangle(e);
        }
        else
        {
            _isDraggingSplit = true;
            PreviewOverlay.CapturePointer(e.Pointer);
            SetSplitFromPointer(e);
        }
    }

    private void PreviewOverlay_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_isDrawingCropRectangle)
        {
            UpdateActiveCropRectangle(e);
            return;
        }

        if (_isDraggingSplit)
        {
            SetSplitFromPointer(e);
        }
    }

    private void PreviewOverlay_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_isDrawingCropRectangle)
        {
            FinishCropRectangle(e);
            return;
        }

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
        WinRect bounds = GetPreviewImageBounds();
        if (bounds.Height <= 0)
        {
            return;
        }

        double percent = Math.Clamp((y - bounds.Y) / bounds.Height * 100.0, SplitSlider.Minimum, SplitSlider.Maximum);
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

        WinRect bounds = GetPreviewImageBounds();
        double splitY = bounds.Y + (bounds.Height * SplitSlider.Value / 100.0);
        SplitLine.Width = bounds.Width;
        SplitLineHitTarget.Width = bounds.Width;
        Canvas.SetLeft(SplitLine, bounds.X);
        Canvas.SetLeft(SplitLineHitTarget, bounds.X);
        Canvas.SetTop(SplitLine, Math.Clamp(splitY - 1.5, bounds.Y, bounds.Y + bounds.Height));
        Canvas.SetTop(SplitLineHitTarget, Math.Clamp(splitY - 9, bounds.Y, bounds.Y + bounds.Height));

        RefreshVisualCropShapes();
    }

    private WinRect GetPreviewImageBounds()
    {
        double width = PreviewOverlay.ActualWidth;
        double height = PreviewOverlay.ActualHeight;
        if (width <= 0 || height <= 0 || _previewPixelWidth <= 0 || _previewPixelHeight <= 0)
        {
            return new WinRect(0, 0, width, height);
        }

        double scale = Math.Min(width / _previewPixelWidth, height / _previewPixelHeight);
        double displayWidth = _previewPixelWidth * scale;
        double displayHeight = _previewPixelHeight * scale;
        return new WinRect((width - displayWidth) / 2.0, (height - displayHeight) / 2.0, displayWidth, displayHeight);
    }

    private void StartCropRectangle(PointerRoutedEventArgs e)
    {
        WinRect bounds = GetPreviewImageBounds();
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        WinPoint pointer = e.GetCurrentPoint(PreviewOverlay).Position;
        if (!Contains(bounds, pointer))
        {
            return;
        }

        _isDrawingCropRectangle = true;
        _cropStartPoint = ClampToBounds(pointer, bounds);
        _activeCropShape = CreateCropShape();
        PreviewOverlay.Children.Add(_activeCropShape);
        Canvas.SetZIndex(_activeCropShape, 5);
        PreviewOverlay.CapturePointer(e.Pointer);
        UpdateCropShape(_activeCropShape, _cropStartPoint, _cropStartPoint);
    }

    private void UpdateActiveCropRectangle(PointerRoutedEventArgs e)
    {
        if (_activeCropShape is null)
        {
            return;
        }

        WinPoint pointer = ClampToBounds(e.GetCurrentPoint(PreviewOverlay).Position, GetPreviewImageBounds());
        UpdateCropShape(_activeCropShape, _cropStartPoint, pointer);
    }

    private void FinishCropRectangle(PointerRoutedEventArgs e)
    {
        if (_activeCropShape is null)
        {
            _isDrawingCropRectangle = false;
            return;
        }

        WinRect bounds = GetPreviewImageBounds();
        WinPoint endPoint = ClampToBounds(e.GetCurrentPoint(PreviewOverlay).Position, bounds);
        WinRect absoluteCrop = BuildRect(_cropStartPoint, endPoint);
        PreviewOverlay.ReleasePointerCapture(e.Pointer);
        _isDrawingCropRectangle = false;

        if (absoluteCrop.Width < 8 || absoluteCrop.Height < 8)
        {
            PreviewOverlay.Children.Remove(_activeCropShape);
            _activeCropShape = null;
            return;
        }

        NormalizedCropRectangle normalized = ToNormalizedCrop(absoluteCrop, bounds);
        _visualCropRectangles.Add(normalized);
        _visualCropShapes.Add(_activeCropShape);
        _activeCropShape = null;
        UpdateCropRectangleCount();
    }

    private void RefreshVisualCropShapes()
    {
        foreach (WinRectangle shape in _visualCropShapes)
        {
            PreviewOverlay.Children.Remove(shape);
        }

        _visualCropShapes.Clear();

        if (PreviewImage.Source is null || VisualCropCheckBox.IsChecked != true)
        {
            return;
        }

        WinRect bounds = GetPreviewImageBounds();
        foreach (NormalizedCropRectangle rectangle in _visualCropRectangles)
        {
            WinRectangle shape = CreateCropShape();
            WinRect absolute = FromNormalizedCrop(rectangle, bounds);
            Canvas.SetLeft(shape, absolute.X);
            Canvas.SetTop(shape, absolute.Y);
            shape.Width = absolute.Width;
            shape.Height = absolute.Height;
            Canvas.SetZIndex(shape, 5);
            PreviewOverlay.Children.Add(shape);
            _visualCropShapes.Add(shape);
        }
    }

    private static WinRectangle CreateCropShape()
    {
        return new WinRectangle
        {
            Stroke = new SolidColorBrush(Colors.MediumSeaGreen),
            StrokeThickness = 2,
            Fill = new SolidColorBrush(Colors.Transparent),
            Opacity = 0.95
        };
    }

    private static void UpdateCropShape(WinRectangle shape, WinPoint start, WinPoint end)
    {
        WinRect rect = BuildRect(start, end);
        Canvas.SetLeft(shape, rect.X);
        Canvas.SetTop(shape, rect.Y);
        shape.Width = rect.Width;
        shape.Height = rect.Height;
    }

    private static WinRect BuildRect(WinPoint first, WinPoint second)
    {
        double x = Math.Min(first.X, second.X);
        double y = Math.Min(first.Y, second.Y);
        return new WinRect(x, y, Math.Abs(second.X - first.X), Math.Abs(second.Y - first.Y));
    }

    private static bool Contains(WinRect rect, WinPoint point)
    {
        return point.X >= rect.X
            && point.X <= rect.X + rect.Width
            && point.Y >= rect.Y
            && point.Y <= rect.Y + rect.Height;
    }

    private static WinPoint ClampToBounds(WinPoint point, WinRect bounds)
    {
        return new WinPoint(
            Math.Clamp(point.X, bounds.X, bounds.X + bounds.Width),
            Math.Clamp(point.Y, bounds.Y, bounds.Y + bounds.Height));
    }

    private static NormalizedCropRectangle ToNormalizedCrop(WinRect crop, WinRect bounds)
    {
        return new NormalizedCropRectangle(
            (crop.X - bounds.X) / bounds.Width,
            (crop.Y - bounds.Y) / bounds.Height,
            crop.Width / bounds.Width,
            crop.Height / bounds.Height);
    }

    private static WinRect FromNormalizedCrop(NormalizedCropRectangle crop, WinRect bounds)
    {
        return new WinRect(
            bounds.X + (crop.X * bounds.Width),
            bounds.Y + (crop.Y * bounds.Height),
            crop.Width * bounds.Width,
            crop.Height * bounds.Height);
    }

    private void ClearCropRectangles()
    {
        foreach (WinRectangle shape in _visualCropShapes)
        {
            PreviewOverlay.Children.Remove(shape);
        }

        if (_activeCropShape is not null)
        {
            PreviewOverlay.Children.Remove(_activeCropShape);
            _activeCropShape = null;
        }

        _visualCropShapes.Clear();
        _visualCropRectangles.Clear();
        UpdateCropRectangleCount();
    }

    private void UpdateCropRectangleCount()
    {
        int count = _visualCropRectangles.Count;
        CropRectangleCountText.Text = count == 1 ? "1 rectangle" : $"{count} rectangles";
        ClearCropRectanglesButton.IsEnabled = VisualCropCheckBox.IsChecked == true && count > 0;
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
        ClearCropRectanglesButton.IsEnabled = !isBusy && VisualCropCheckBox.IsChecked == true && _visualCropRectangles.Count > 0;
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
