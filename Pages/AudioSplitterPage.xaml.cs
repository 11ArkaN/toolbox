using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Toolbox.Models;
using Toolbox.Services;
using Windows.Storage;

namespace Toolbox.Pages;

public sealed partial class AudioSplitterPage : Page
{
    private readonly AudioSplitService _audioSplitService = new();
    private StorageFile? _selectedAudioFile;

    public AudioSplitterPage()
    {
        InitializeComponent();
    }

    private async void BrowseAudioButton_Click(object sender, RoutedEventArgs e)
    {
        StorageFile? file = await PickerService.PickAudioFileAsync();
        if (file is null)
        {
            return;
        }

        _selectedAudioFile = file;
        AudioPathBox.Text = file.Path;
        AudioOutputText.Text = "Output: " + Path.GetDirectoryName(file.Path);
        AudioStatusText.Text = "Ready";
        AudioProgressBar.Value = 0;
        AudioInfoBar.IsOpen = false;
        SplitAudioButton.IsEnabled = true;
    }

    private async void SplitAudioButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedAudioFile is null)
        {
            ShowInfo("Select an audio file first.", InfoBarSeverity.Warning);
            return;
        }

        SetBusy(true);
        AudioInfoBar.IsOpen = false;

        var progress = new Progress<AudioSplitProgress>(value =>
        {
            AudioProgressBar.Value = Math.Clamp(value.Percent, 0, 100);
            AudioStatusText.Text = value.Message;
        });

        try
        {
            AudioSplitResult result = await _audioSplitService.SplitInHalfAsync(_selectedAudioFile.Path, progress);
            AudioOutputText.Text = $"Created:{Environment.NewLine}{Path.GetFileName(result.FirstPartPath)}{Environment.NewLine}{Path.GetFileName(result.SecondPartPath)}";
            ShowInfo("Audio split completed.", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            AudioProgressBar.Value = 0;
            AudioStatusText.Text = "Failed";
            ShowInfo(ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool isBusy)
    {
        BrowseAudioButton.IsEnabled = !isBusy;
        SplitAudioButton.IsEnabled = !isBusy && _selectedAudioFile is not null;
        AudioProgressBar.IsIndeterminate = isBusy && AudioProgressBar.Value <= 0;
    }

    private void ShowInfo(string message, InfoBarSeverity severity)
    {
        AudioInfoBar.Message = message;
        AudioInfoBar.Severity = severity;
        AudioInfoBar.IsOpen = true;
    }
}
