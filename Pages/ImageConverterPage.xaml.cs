using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Toolbox.Models;
using Toolbox.Services;
using Windows.Storage;

namespace Toolbox.Pages;

public sealed partial class ImageConverterPage : Page
{
    private readonly ImageBatchConversionService _conversionService = new();
    private readonly ObservableCollection<string> _previewItems = [];
    private readonly List<string> _imagePaths = [];
    private string? _outputDirectory;
    private bool _isInitialized;

    public ImageConverterPage()
    {
        InitializeComponent();
        ConfigureNumberBoxes();
        ImagePreviewListView.ItemsSource = _previewItems;
        _isInitialized = true;
        UpdateRuleAvailability();
        RefreshPreview();
    }

    private void ConfigureNumberBoxes()
    {
        ImageQualityBox.Maximum = 100;
        ImageQualityBox.Value = 85;
        ImageQualityBox.Minimum = 1;

        ImageWidthBox.Value = 1920;
        ImageWidthBox.Minimum = 0;

        ImageHeightBox.Value = 1080;
        ImageHeightBox.Minimum = 0;
    }

    private async void AddImagesButton_Click(object sender, RoutedEventArgs e)
    {
        IReadOnlyList<StorageFile> files = await PickerService.PickImageFilesAsync();
        AddImages(files.Select(file => file.Path));
    }

    private async void AddImageFolderButton_Click(object sender, RoutedEventArgs e)
    {
        StorageFolder? folder = await PickerService.PickFolderAsync();
        if (folder is null)
        {
            return;
        }

        SearchOption option = ImageIncludeSubfoldersCheckBox.IsChecked == true
            ? SearchOption.AllDirectories
            : SearchOption.TopDirectoryOnly;
        HashSet<string> supported = _conversionService.SupportedExtensions.ToHashSet(StringComparer.OrdinalIgnoreCase);
        AddImages(Directory.EnumerateFiles(folder.Path, "*", option).Where(path => supported.Contains(Path.GetExtension(path))));
    }

    private async void ChooseImageOutputButton_Click(object sender, RoutedEventArgs e)
    {
        StorageFolder? folder = await PickerService.PickFolderAsync();
        if (folder is null)
        {
            return;
        }

        _outputDirectory = folder.Path;
        ImageOutputFolderBox.Text = folder.Path;
        RefreshPreview();
    }

    private void ClearImagesButton_Click(object sender, RoutedEventArgs e)
    {
        _imagePaths.Clear();
        RefreshPreview();
    }

    private async void ConvertImagesButton_Click(object sender, RoutedEventArgs e)
    {
        if (_imagePaths.Count == 0)
        {
            ShowInfo("Select at least one image.", InfoBarSeverity.Warning);
            return;
        }

        string outputDirectory = _outputDirectory
            ?? Path.Combine(Path.GetDirectoryName(_imagePaths[0]) ?? Environment.CurrentDirectory, "image_out");
        _outputDirectory = outputDirectory;
        ImageOutputFolderBox.Text = outputDirectory;

        SetBusy(true);
        ImageProgressBar.Value = 0;
        ImageInfoBar.IsOpen = false;

        var progress = new Progress<ToolProgress>(value =>
        {
            ImageProgressBar.Value = Math.Clamp(value.Percent, 0, 100);
            ImageSummaryText.Text = value.Message;
        });

        try
        {
            IReadOnlyList<string> outputs = await _conversionService.ConvertAsync(
                _imagePaths,
                outputDirectory,
                CollectOptions(),
                progress);

            ShowInfo($"Converted {outputs.Count} image file(s).", InfoBarSeverity.Success);
            RefreshPreview();
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

    private void ImageRules_Changed(object sender, RoutedEventArgs e) => RefreshPreviewAfterInput();

    private void ImageRules_Changed(object sender, SelectionChangedEventArgs e) => RefreshPreviewAfterInput();

    private void ImageRules_Changed(object sender, TextChangedEventArgs e) => RefreshPreviewAfterInput();

    private void ImageRules_Changed(NumberBox sender, NumberBoxValueChangedEventArgs args) => RefreshPreviewAfterInput();

    private void RefreshPreviewAfterInput()
    {
        if (!_isInitialized)
        {
            return;
        }

        UpdateRuleAvailability();
        RefreshPreview();
    }

    private void AddImages(IEnumerable<string> paths)
    {
        int added = 0;
        foreach (string path in paths)
        {
            if (!File.Exists(path) || _imagePaths.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            _imagePaths.Add(path);
            added++;
        }

        if (_outputDirectory is null && _imagePaths.Count > 0)
        {
            _outputDirectory = Path.Combine(Path.GetDirectoryName(_imagePaths[0]) ?? Environment.CurrentDirectory, "image_out");
            ImageOutputFolderBox.Text = _outputDirectory;
        }

        ShowInfo(added == 0 ? "No new images were added." : $"Added {added} image file(s).", added == 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Informational);
        RefreshPreview();
    }

    private void RefreshPreview()
    {
        _previewItems.Clear();

        if (_imagePaths.Count == 0)
        {
            ImageSummaryText.Text = "No images selected";
            ConvertImagesButton.IsEnabled = false;
            return;
        }

        string outputDirectory = _outputDirectory
            ?? Path.Combine(Path.GetDirectoryName(_imagePaths[0]) ?? Environment.CurrentDirectory, "image_out");
        ImageConversionOptions options = CollectOptions();
        foreach (string input in _imagePaths.Take(250))
        {
            string output = _conversionService.PreviewOutputPath(input, outputDirectory, options);
            _previewItems.Add($"{Path.GetFileName(input)}  ->  {Path.GetFileName(output)}");
        }

        ImageSummaryText.Text = _imagePaths.Count > 250
            ? $"{_imagePaths.Count} image(s), showing first 250"
            : $"{_imagePaths.Count} image(s) ready";
        ConvertImagesButton.IsEnabled = _imagePaths.Count > 0;
    }

    private ImageConversionOptions CollectOptions()
    {
        return new ImageConversionOptions(
            ImageFormatComboBox.SelectedIndex switch
            {
                1 => ImageOutputFormat.Png,
                2 => ImageOutputFormat.Webp,
                _ => ImageOutputFormat.Jpg
            },
            ImageSuffixBox.Text,
            Math.Clamp(IntegerOrDefault(ImageQualityBox, 85), 1, 100),
            ResizeModeComboBox.SelectedIndex switch
            {
                1 => ImageResizeMode.Fit,
                2 => ImageResizeMode.Fill,
                3 => ImageResizeMode.Stretch,
                _ => ImageResizeMode.None
            },
            Math.Max(0, IntegerOrDefault(ImageWidthBox, 0)),
            Math.Max(0, IntegerOrDefault(ImageHeightBox, 0)),
            CropAspectCheckBox.IsChecked == true,
            SelectedAspectRatio());
    }

    private double SelectedAspectRatio()
    {
        return AspectRatioComboBox.SelectedIndex switch
        {
            0 => 1,
            2 => 4.0 / 3.0,
            3 => 3.0 / 2.0,
            4 => 9.0 / 16.0,
            _ => 16.0 / 9.0
        };
    }

    private void UpdateRuleAvailability()
    {
        bool resizeEnabled = ResizeModeComboBox.SelectedIndex > 0;
        ImageWidthBox.IsEnabled = resizeEnabled;
        ImageHeightBox.IsEnabled = resizeEnabled;
        AspectRatioComboBox.IsEnabled = CropAspectCheckBox.IsChecked == true;
        ImageQualityBox.IsEnabled = ImageFormatComboBox.SelectedIndex != 1;
    }

    private void SetBusy(bool isBusy)
    {
        AddImagesButton.IsEnabled = !isBusy;
        AddImageFolderButton.IsEnabled = !isBusy;
        ClearImagesButton.IsEnabled = !isBusy;
        ChooseImageOutputButton.IsEnabled = !isBusy;
        ConvertImagesButton.IsEnabled = !isBusy && _imagePaths.Count > 0;
        ImageProgressBar.IsIndeterminate = isBusy && ImageProgressBar.Value <= 0;
    }

    private static int IntegerOrDefault(NumberBox box, int fallback)
    {
        return double.IsFinite(box.Value) ? (int)Math.Round(box.Value) : fallback;
    }

    private void ShowInfo(string message, InfoBarSeverity severity)
    {
        ImageInfoBar.Message = message;
        ImageInfoBar.Severity = severity;
        ImageInfoBar.IsOpen = true;
    }
}
