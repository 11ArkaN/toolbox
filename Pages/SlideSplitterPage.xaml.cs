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
    private const double PreviewRenderDpi = 120.0;
    private const double CropHandleHitSize = 14.0;

    private readonly PdfSlideSplitService _slideSplitService = new();
    private readonly List<WinRectangle> _normalCropShapes = [];
    private readonly List<NormalizedCropRectangle> _visualCropRectangles = [];
    private readonly List<WinRectangle> _visualCropShapes = [];
    private StorageFile? _selectedPdfFile;
    private string? _outputDirectory;
    private double _previewPixelWidth;
    private double _previewPixelHeight;
    private bool _isInitialized;
    private bool _isBusy;
    private bool _isUpdatingSplit;
    private bool _isDraggingSplit;
    private bool _isDrawingCropRectangle;
    private CropEditMode _cropEditMode = CropEditMode.None;
    private int _selectedCropIndex = -1;
    private int _activeCropIndex = -1;
    private WinPoint _cropStartPoint;
    private WinPoint _resizeAnchorPoint;
    private WinRect _editStartCrop;
    private WinRectangle? _activeCropShape;

    public SlideSplitterPage()
    {
        InitializeComponent();
        ConfigureControls();
        Canvas.SetZIndex(SplitLine, 3);
        Canvas.SetZIndex(SplitLineHitTarget, 4);
        _isInitialized = true;
        UpdateModeControls();
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
            byte[] previewBytes = await _slideSplitService.RenderFirstPagePreviewAsync(pdfFile, (int)PreviewRenderDpi);
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
        if (!_isInitialized || _isUpdatingSplit)
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
        if (!_isInitialized || _isUpdatingSplit || !double.IsFinite(args.NewValue))
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
        UpdateModeControls();
        UpdateCropRectangleCount();
        UpdateSplitLinePosition();
    }

    private void ClearCropRectanglesButton_Click(object sender, RoutedEventArgs e)
    {
        ClearCropRectangles();
    }

    private void DeleteSelectedCropRectangleButton_Click(object sender, RoutedEventArgs e)
    {
        DeleteSelectedCropRectangle();
    }

    private void CropSetting_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_isInitialized && !_isUpdatingSplit)
        {
            UpdateSplitLinePosition();
        }
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
            BeginVisualCropInteraction(e);
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
        if (_cropEditMode is CropEditMode.Moving or CropEditMode.Resizing)
        {
            UpdateCropEdit(e);
            return;
        }

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
        if (_cropEditMode is CropEditMode.Moving or CropEditMode.Resizing)
        {
            EndCropEdit(e);
            return;
        }

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
        bool visualCrop = VisualCropCheckBox.IsChecked == true;
        SplitLine.Visibility = visualCrop ? Visibility.Collapsed : Visibility.Visible;
        SplitLineHitTarget.Visibility = visualCrop ? Visibility.Collapsed : Visibility.Visible;

        if (visualCrop)
        {
            ClearNormalCropShapes();
            RefreshVisualCropShapes();
            return;
        }

        double splitY = bounds.Y + (bounds.Height * SplitSlider.Value / 100.0);
        SplitLine.Width = bounds.Width;
        SplitLineHitTarget.Width = bounds.Width;
        Canvas.SetLeft(SplitLine, bounds.X);
        Canvas.SetLeft(SplitLineHitTarget, bounds.X);
        Canvas.SetTop(SplitLine, Math.Clamp(splitY - 1.5, bounds.Y, bounds.Y + bounds.Height));
        Canvas.SetTop(SplitLineHitTarget, Math.Clamp(splitY - 9, bounds.Y, bounds.Y + bounds.Height));

        RefreshNormalCropShapes(bounds);
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

    private void BeginVisualCropInteraction(PointerRoutedEventArgs e)
    {
        WinRect bounds = GetPreviewImageBounds();
        WinPoint pointer = e.GetCurrentPoint(PreviewOverlay).Position;
        int hitIndex = HitTestCropRectangle(pointer, bounds);
        if (hitIndex >= 0)
        {
            BeginCropEdit(hitIndex, pointer, bounds, e);
            return;
        }

        StartCropRectangle(e);
    }

    private void BeginCropEdit(int cropIndex, WinPoint pointer, WinRect bounds, PointerRoutedEventArgs e)
    {
        _selectedCropIndex = cropIndex;
        _activeCropIndex = cropIndex;
        _cropStartPoint = ClampToBounds(pointer, bounds);
        _editStartCrop = FromNormalizedCrop(_visualCropRectangles[cropIndex], bounds);
        _cropEditMode = IsOnResizeHandle(_cropStartPoint, _editStartCrop) ? CropEditMode.Resizing : CropEditMode.Moving;
        _resizeAnchorPoint = new WinPoint(_editStartCrop.X, _editStartCrop.Y);

        RefreshVisualCropShapes();
        PreviewOverlay.CapturePointer(e.Pointer);
        UpdateCropRectangleCount();
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
        _selectedCropIndex = -1;
        _cropStartPoint = ClampToBounds(pointer, bounds);
        _activeCropShape = CreateCropShape(isSelected: true);
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
        _selectedCropIndex = _visualCropRectangles.Count - 1;
        _visualCropShapes.Add(_activeCropShape);
        _activeCropShape = null;
        RefreshVisualCropShapes();
        UpdateCropRectangleCount();
    }

    private void UpdateCropEdit(PointerRoutedEventArgs e)
    {
        if (_activeCropIndex < 0 || _activeCropIndex >= _visualCropRectangles.Count)
        {
            return;
        }

        WinRect bounds = GetPreviewImageBounds();
        WinPoint pointer = ClampToBounds(e.GetCurrentPoint(PreviewOverlay).Position, bounds);
        WinRect updatedCrop;

        if (_cropEditMode == CropEditMode.Moving)
        {
            double dx = pointer.X - _cropStartPoint.X;
            double dy = pointer.Y - _cropStartPoint.Y;
            updatedCrop = ClampMovedCrop(
                new WinRect(_editStartCrop.X + dx, _editStartCrop.Y + dy, _editStartCrop.Width, _editStartCrop.Height),
                bounds);
        }
        else
        {
            updatedCrop = BuildRect(_resizeAnchorPoint, pointer);
            updatedCrop = ClampCropToBounds(updatedCrop, bounds);
        }

        if (updatedCrop.Width < 8 || updatedCrop.Height < 8)
        {
            return;
        }

        _visualCropRectangles[_activeCropIndex] = ToNormalizedCrop(updatedCrop, bounds);
        RefreshVisualCropShapes();
    }

    private void EndCropEdit(PointerRoutedEventArgs e)
    {
        PreviewOverlay.ReleasePointerCapture(e.Pointer);
        _cropEditMode = CropEditMode.None;
        _activeCropIndex = -1;
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
        for (int index = 0; index < _visualCropRectangles.Count; index++)
        {
            NormalizedCropRectangle rectangle = _visualCropRectangles[index];
            WinRectangle shape = CreateCropShape(index == _selectedCropIndex);
            WinRect absolute = FromNormalizedCrop(rectangle, bounds);
            Canvas.SetLeft(shape, absolute.X);
            Canvas.SetTop(shape, absolute.Y);
            shape.Width = absolute.Width;
            shape.Height = absolute.Height;
            Canvas.SetZIndex(shape, 5);
            PreviewOverlay.Children.Add(shape);
            _visualCropShapes.Add(shape);

            if (index == _selectedCropIndex)
            {
                var handle = new WinRectangle
                {
                    Width = CropHandleHitSize,
                    Height = CropHandleHitSize,
                    Fill = new SolidColorBrush(Colors.Gold),
                    Stroke = new SolidColorBrush(Colors.Black),
                    StrokeThickness = 1,
                    Opacity = 0.95
                };
                Canvas.SetLeft(handle, absolute.X + absolute.Width - CropHandleHitSize / 2.0);
                Canvas.SetTop(handle, absolute.Y + absolute.Height - CropHandleHitSize / 2.0);
                Canvas.SetZIndex(handle, 6);
                PreviewOverlay.Children.Add(handle);
                _visualCropShapes.Add(handle);
            }
        }
    }

    private void RefreshNormalCropShapes(WinRect bounds)
    {
        ClearNormalCropShapes();
        if (PreviewImage.Source is null || VisualCropCheckBox.IsChecked == true)
        {
            return;
        }

        double left = bounds.X + bounds.Width * NumberOrDefault(MarginLeftBox, 2) / 100.0;
        double right = bounds.X + bounds.Width - bounds.Width * NumberOrDefault(MarginRightBox, 2) / 100.0;
        double top = bounds.Y + bounds.Height * NumberOrDefault(MarginTopBox, 2) / 100.0;
        double bottom = bounds.Y + bounds.Height - bounds.Height * NumberOrDefault(MarginBottomBox, 2) / 100.0;
        double split = bounds.Y + bounds.Height * SplitSlider.Value / 100.0;
        double exportHeight = _previewPixelHeight * NumberOrDefault(DpiBox, 300) / PreviewRenderDpi;
        double gutter = exportHeight <= 0
            ? 0
            : NumberOrDefault(GutterBox, 20) / exportHeight * bounds.Height;
        double halfGutter = gutter / 2.0;

        AddNormalCropShape(new WinRect(left, top, Math.Max(0, right - left), Math.Max(0, split - halfGutter - top)));
        AddNormalCropShape(new WinRect(left, split + halfGutter, Math.Max(0, right - left), Math.Max(0, bottom - split - halfGutter)));
    }

    private void AddNormalCropShape(WinRect crop)
    {
        if (crop.Width < 2 || crop.Height < 2)
        {
            return;
        }

        var shape = new WinRectangle
        {
            Width = crop.Width,
            Height = crop.Height,
            Stroke = new SolidColorBrush(Colors.DeepSkyBlue),
            StrokeThickness = 2,
            Fill = new SolidColorBrush(Colors.Transparent),
            Opacity = 0.9
        };

        Canvas.SetLeft(shape, crop.X);
        Canvas.SetTop(shape, crop.Y);
        Canvas.SetZIndex(shape, 1);
        PreviewOverlay.Children.Add(shape);
        _normalCropShapes.Add(shape);
    }

    private void ClearNormalCropShapes()
    {
        foreach (WinRectangle shape in _normalCropShapes)
        {
            PreviewOverlay.Children.Remove(shape);
        }

        _normalCropShapes.Clear();
    }

    private static WinRectangle CreateCropShape(bool isSelected = false)
    {
        return new WinRectangle
        {
            Stroke = new SolidColorBrush(isSelected ? Colors.Gold : Colors.MediumSeaGreen),
            StrokeThickness = isSelected ? 3 : 2,
            Fill = new SolidColorBrush(isSelected ? Colors.Gold : Colors.Transparent),
            Opacity = isSelected ? 0.45 : 0.95
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

    private int HitTestCropRectangle(WinPoint point, WinRect bounds)
    {
        for (int index = _visualCropRectangles.Count - 1; index >= 0; index--)
        {
            if (Contains(FromNormalizedCrop(_visualCropRectangles[index], bounds), point))
            {
                return index;
            }
        }

        return -1;
    }

    private static bool IsOnResizeHandle(WinPoint point, WinRect crop)
    {
        var bottomRight = new WinPoint(crop.X + crop.Width, crop.Y + crop.Height);
        return Math.Abs(point.X - bottomRight.X) <= CropHandleHitSize
            && Math.Abs(point.Y - bottomRight.Y) <= CropHandleHitSize;
    }

    private static WinPoint ClampToBounds(WinPoint point, WinRect bounds)
    {
        return new WinPoint(
            Math.Clamp(point.X, bounds.X, bounds.X + bounds.Width),
            Math.Clamp(point.Y, bounds.Y, bounds.Y + bounds.Height));
    }

    private static WinRect ClampMovedCrop(WinRect crop, WinRect bounds)
    {
        double x = Math.Clamp(crop.X, bounds.X, bounds.X + bounds.Width - crop.Width);
        double y = Math.Clamp(crop.Y, bounds.Y, bounds.Y + bounds.Height - crop.Height);
        return new WinRect(x, y, crop.Width, crop.Height);
    }

    private static WinRect ClampCropToBounds(WinRect crop, WinRect bounds)
    {
        double x = Math.Clamp(crop.X, bounds.X, bounds.X + bounds.Width);
        double y = Math.Clamp(crop.Y, bounds.Y, bounds.Y + bounds.Height);
        double right = Math.Clamp(crop.X + crop.Width, bounds.X, bounds.X + bounds.Width);
        double bottom = Math.Clamp(crop.Y + crop.Height, bounds.Y, bounds.Y + bounds.Height);
        return new WinRect(x, y, Math.Max(0, right - x), Math.Max(0, bottom - y));
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
        _selectedCropIndex = -1;
        _activeCropIndex = -1;
        _cropEditMode = CropEditMode.None;
        UpdateCropRectangleCount();
    }

    private void DeleteSelectedCropRectangle()
    {
        if (_selectedCropIndex < 0 || _selectedCropIndex >= _visualCropRectangles.Count)
        {
            return;
        }

        _visualCropRectangles.RemoveAt(_selectedCropIndex);
        _selectedCropIndex = Math.Min(_selectedCropIndex, _visualCropRectangles.Count - 1);
        RefreshVisualCropShapes();
        UpdateCropRectangleCount();
    }

    private void UpdateCropRectangleCount()
    {
        int count = _visualCropRectangles.Count;
        string selection = _selectedCropIndex >= 0 && _selectedCropIndex < count ? $" - selected {_selectedCropIndex + 1}" : string.Empty;
        CropRectangleCountText.Text = (count == 1 ? "1 rectangle" : $"{count} rectangles") + selection;
        UpdateModeControls();
    }

    private void SetBusy(bool isBusy)
    {
        _isBusy = isBusy;
        UpdateModeControls();
    }

    private void UpdateModeControls()
    {
        bool visualCrop = VisualCropCheckBox.IsChecked == true;
        bool isBusy = _isBusy;

        BrowsePdfButton.IsEnabled = !isBusy;
        ChooseOutputButton.IsEnabled = !isBusy;
        ExtractButton.IsEnabled = !isBusy && _selectedPdfFile is not null;
        SplitSlider.IsEnabled = !isBusy && !visualCrop;
        SplitNumberBox.IsEnabled = !isBusy && !visualCrop;
        MarginLeftBox.IsEnabled = !isBusy && !visualCrop;
        MarginRightBox.IsEnabled = !isBusy && !visualCrop;
        MarginTopBox.IsEnabled = !isBusy && !visualCrop;
        MarginBottomBox.IsEnabled = !isBusy && !visualCrop;
        GutterBox.IsEnabled = !isBusy && !visualCrop;
        DpiBox.IsEnabled = !isBusy;
        ExportPngCheckBox.IsEnabled = !isBusy;
        ExportPdfCheckBox.IsEnabled = !isBusy;
        VisualCropCheckBox.IsEnabled = !isBusy;
        DeleteSelectedCropRectangleButton.IsEnabled = !isBusy && visualCrop && _selectedCropIndex >= 0 && _selectedCropIndex < _visualCropRectangles.Count;
        ClearCropRectanglesButton.IsEnabled = !isBusy && visualCrop && _visualCropRectangles.Count > 0;
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

    private enum CropEditMode
    {
        None,
        Moving,
        Resizing
    }
}
