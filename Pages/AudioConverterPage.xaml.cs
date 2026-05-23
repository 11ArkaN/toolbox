using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Toolbox.Models;
using Toolbox.Services;
using Windows.Storage;

namespace Toolbox.Pages;

public sealed partial class AudioConverterPage : Page
{
    private readonly AudioConversionService _conversionService = new();
    private readonly ObservableCollection<string> _previewItems = [];
    private readonly List<string> _audioPaths = [];
    private string? _outputDirectory;
    private bool _isInitialized;

    public AudioConverterPage()
    {
        InitializeComponent();
        AudioPreviewListView.ItemsSource = _previewItems;
        _isInitialized = true;
        RefreshPreview();
    }

    private async void AddAudioFilesButton_Click(object sender, RoutedEventArgs e)
    {
        IReadOnlyList<StorageFile> files = await PickerService.PickAudioFilesAsync();
        AddAudioFiles(files.Select(file => file.Path));
    }

    private async void AddAudioFolderButton_Click(object sender, RoutedEventArgs e)
    {
        StorageFolder? folder = await PickerService.PickFolderAsync();
        if (folder is null)
        {
            return;
        }

        SearchOption option = AudioIncludeSubfoldersCheckBox.IsChecked == true
            ? SearchOption.AllDirectories
            : SearchOption.TopDirectoryOnly;
        HashSet<string> supported = _conversionService.SupportedExtensions.ToHashSet(StringComparer.OrdinalIgnoreCase);
        AddAudioFiles(Directory.EnumerateFiles(folder.Path, "*", option).Where(path => supported.Contains(Path.GetExtension(path))));
    }

    private async void ChooseAudioOutputButton_Click(object sender, RoutedEventArgs e)
    {
        StorageFolder? folder = await PickerService.PickFolderAsync();
        if (folder is null)
        {
            return;
        }

        _outputDirectory = folder.Path;
        AudioOutputFolderBox.Text = folder.Path;
        RefreshPreview();
    }

    private void ClearAudioFilesButton_Click(object sender, RoutedEventArgs e)
    {
        _audioPaths.Clear();
        RefreshPreview();
    }

    private async void ConvertAudioButton_Click(object sender, RoutedEventArgs e)
    {
        if (_audioPaths.Count == 0)
        {
            ShowInfo("Select at least one audio file.", InfoBarSeverity.Warning);
            return;
        }

        string outputDirectory = _outputDirectory
            ?? Path.Combine(Path.GetDirectoryName(_audioPaths[0]) ?? Environment.CurrentDirectory, "mp3_out");
        _outputDirectory = outputDirectory;
        AudioOutputFolderBox.Text = outputDirectory;

        SetBusy(true);
        AudioConversionProgressBar.Value = 0;
        AudioConversionInfoBar.IsOpen = false;

        var progress = new Progress<ToolProgress>(value =>
        {
            AudioConversionProgressBar.Value = Math.Clamp(value.Percent, 0, 100);
            AudioSummaryText.Text = value.Message;
        });

        try
        {
            IReadOnlyList<string> outputs = await _conversionService.ConvertToMp3Async(
                _audioPaths,
                outputDirectory,
                CollectOptions(),
                progress);

            ShowInfo($"Converted {outputs.Count} audio file(s) to MP3.", InfoBarSeverity.Success);
            RefreshPreview();
        }
        catch (Exception ex)
        {
            AudioConversionProgressBar.Value = 0;
            ShowInfo(ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void AudioRules_Changed(object sender, RoutedEventArgs e) => RefreshPreviewAfterInput();

    private void AudioRules_Changed(object sender, SelectionChangedEventArgs e) => RefreshPreviewAfterInput();

    private void AudioRules_Changed(object sender, TextChangedEventArgs e) => RefreshPreviewAfterInput();

    private void RefreshPreviewAfterInput()
    {
        if (!_isInitialized)
        {
            return;
        }

        RefreshPreview();
    }

    private void AddAudioFiles(IEnumerable<string> paths)
    {
        int added = 0;
        HashSet<string> supported = _conversionService.SupportedExtensions.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string path in paths)
        {
            if (!File.Exists(path)
                || !supported.Contains(Path.GetExtension(path))
                || _audioPaths.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            _audioPaths.Add(path);
            added++;
        }

        if (_outputDirectory is null && _audioPaths.Count > 0)
        {
            _outputDirectory = Path.Combine(Path.GetDirectoryName(_audioPaths[0]) ?? Environment.CurrentDirectory, "mp3_out");
            AudioOutputFolderBox.Text = _outputDirectory;
        }

        ShowInfo(
            added == 0 ? "No new audio files were added." : $"Added {added} audio file(s).",
            added == 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Informational);
        RefreshPreview();
    }

    private void RefreshPreview()
    {
        _previewItems.Clear();

        if (_audioPaths.Count == 0)
        {
            AudioSummaryText.Text = "No audio files selected";
            ConvertAudioButton.IsEnabled = false;
            return;
        }

        string outputDirectory = _outputDirectory
            ?? Path.Combine(Path.GetDirectoryName(_audioPaths[0]) ?? Environment.CurrentDirectory, "mp3_out");
        AudioConversionOptions options = CollectOptions();
        foreach (string input in _audioPaths.Take(250))
        {
            string output = _conversionService.PreviewOutputPath(input, outputDirectory, options);
            _previewItems.Add($"{Path.GetFileName(input)}  ->  {Path.GetFileName(output)}");
        }

        AudioSummaryText.Text = _audioPaths.Count > 250
            ? $"{_audioPaths.Count} audio file(s), showing first 250"
            : $"{_audioPaths.Count} audio file(s) ready";
        ConvertAudioButton.IsEnabled = _audioPaths.Count > 0;
    }

    private AudioConversionOptions CollectOptions()
    {
        return new AudioConversionOptions(
            AudioSuffixBox.Text,
            AudioBitrateComboBox.SelectedIndex switch
            {
                0 => 128,
                2 => 256,
                3 => 320,
                _ => 192
            },
            AudioSampleRateComboBox.SelectedIndex switch
            {
                1 => 44100,
                2 => 48000,
                _ => 0
            },
            AudioChannelsComboBox.SelectedIndex switch
            {
                1 => AudioChannelMode.Stereo,
                2 => AudioChannelMode.Mono,
                _ => AudioChannelMode.Original
            });
    }

    private void SetBusy(bool isBusy)
    {
        AddAudioFilesButton.IsEnabled = !isBusy;
        AddAudioFolderButton.IsEnabled = !isBusy;
        ClearAudioFilesButton.IsEnabled = !isBusy;
        ChooseAudioOutputButton.IsEnabled = !isBusy;
        ConvertAudioButton.IsEnabled = !isBusy && _audioPaths.Count > 0;
        AudioConversionProgressBar.IsIndeterminate = isBusy && AudioConversionProgressBar.Value <= 0;
    }

    private void ShowInfo(string message, InfoBarSeverity severity)
    {
        AudioConversionInfoBar.Message = message;
        AudioConversionInfoBar.Severity = severity;
        AudioConversionInfoBar.IsOpen = true;
    }
}
