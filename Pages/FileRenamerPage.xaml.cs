using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Toolbox.Models;
using Toolbox.Services;
using Windows.Storage;

namespace Toolbox.Pages;

public sealed partial class FileRenamerPage : Page
{
    private readonly BatchRenameService _renameService = new();
    private readonly List<string> _filePaths = [];
    private readonly ObservableCollection<BatchRenamePreviewItem> _previewItems = [];
    private bool _isInitialized;

    public FileRenamerPage()
    {
        InitializeComponent();
        PreviewListView.ItemsSource = _previewItems;
        _isInitialized = true;
        UpdateRuleAvailability();
        RefreshPreview();
    }

    private async void AddFilesButton_Click(object sender, RoutedEventArgs e)
    {
        IReadOnlyList<StorageFile> files = await PickerService.PickAnyFilesAsync();
        AddPaths(files.Select(file => file.Path));
    }

    private async void AddFolderButton_Click(object sender, RoutedEventArgs e)
    {
        StorageFolder? folder = await PickerService.PickFolderAsync();
        if (folder is null)
        {
            return;
        }

        var option = IncludeSubfoldersCheckBox.IsChecked == true
            ? SearchOption.AllDirectories
            : SearchOption.TopDirectoryOnly;
        var filters = ParseExtensionFilter(FolderExtensionFilterBox.Text);

        IEnumerable<string> paths = Directory.EnumerateFiles(folder.Path, "*", option)
            .Where(path => filters.Count == 0 || filters.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase));
        AddPaths(paths);
    }

    private void ClearFilesButton_Click(object sender, RoutedEventArgs e)
    {
        _filePaths.Clear();
        RefreshPreview();
    }

    private void RenameButton_Click(object sender, RoutedEventArgs e)
    {
        var readyItems = _previewItems.Where(item => item.CanRename).ToList();
        if (readyItems.Count == 0)
        {
            ShowInfo("No valid renames to apply.", InfoBarSeverity.Warning);
            return;
        }

        var targetsByOriginal = readyItems.ToDictionary(item => item.OriginalPath, item => item.TargetPath, StringComparer.OrdinalIgnoreCase);

        try
        {
            _renameService.Apply(readyItems);

            for (int i = 0; i < _filePaths.Count; i++)
            {
                if (targetsByOriginal.TryGetValue(_filePaths[i], out string? targetPath))
                {
                    _filePaths[i] = targetPath;
                }
            }

            ShowInfo($"Renamed {readyItems.Count} file(s).", InfoBarSeverity.Success);
            RefreshPreview();
        }
        catch (Exception ex)
        {
            ShowInfo(ex.Message, InfoBarSeverity.Error);
            RefreshPreview();
        }
    }

    private void Rules_Changed(object sender, RoutedEventArgs e) => RefreshPreviewAfterInput();

    private void Rules_Changed(object sender, SelectionChangedEventArgs e) => RefreshPreviewAfterInput();

    private void Rules_Changed(object sender, TextChangedEventArgs e) => RefreshPreviewAfterInput();

    private void Rules_Changed(NumberBox sender, NumberBoxValueChangedEventArgs args) => RefreshPreviewAfterInput();

    private void RefreshPreviewAfterInput()
    {
        if (!_isInitialized)
        {
            return;
        }

        UpdateRuleAvailability();
        RefreshPreview();
    }

    private void AddPaths(IEnumerable<string> paths)
    {
        int added = 0;
        foreach (string path in paths)
        {
            if (!File.Exists(path) || _filePaths.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            _filePaths.Add(path);
            added++;
        }

        ShowInfo(added == 0 ? "No new files were added." : $"Added {added} file(s).", added == 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Informational);
        RefreshPreview();
    }

    private void RefreshPreview()
    {
        _previewItems.Clear();

        if (_filePaths.Count == 0)
        {
            SummaryText.Text = "No files selected";
            RenameButton.IsEnabled = false;
            return;
        }

        IReadOnlyList<BatchRenamePreviewItem> preview = _renameService.BuildPreview(_filePaths, CollectOptions());
        foreach (BatchRenamePreviewItem item in preview)
        {
            _previewItems.Add(item);
        }

        int ready = _previewItems.Count(item => item.CanRename);
        int blocked = _previewItems.Count - ready;
        SummaryText.Text = $"{_previewItems.Count} file(s), {ready} ready, {blocked} blocked";
        RenameButton.IsEnabled = ready > 0;
    }

    private BatchRenameOptions CollectOptions()
    {
        return new BatchRenameOptions(
            NameModeComboBox.SelectedIndex == 1 ? RenameNameMode.ReplaceName : RenameNameMode.KeepOriginal,
            BaseNameBox.Text,
            PrefixBox.Text,
            SuffixBox.Text,
            FindTextBox.Text,
            ReplaceTextBox.Text,
            CaseModeComboBox.SelectedIndex switch
            {
                1 => RenameCaseMode.Lower,
                2 => RenameCaseMode.Upper,
                3 => RenameCaseMode.Title,
                _ => RenameCaseMode.Keep
            },
            UseNumberingCheckBox.IsChecked == true,
            IntegerOrDefault(NumberStartBox, 1),
            Math.Max(1, IntegerOrDefault(NumberStepBox, 1)),
            Math.Clamp(IntegerOrDefault(NumberPaddingBox, 3), 1, 12),
            NumberPlacementComboBox.SelectedIndex == 0 ? RenameNumberPlacement.Prefix : RenameNumberPlacement.Suffix,
            NumberSeparatorBox.Text,
            DateModeComboBox.SelectedIndex switch
            {
                1 => RenameDateMode.Today,
                2 => RenameDateMode.Created,
                3 => RenameDateMode.Modified,
                _ => RenameDateMode.None
            },
            DatePlacementComboBox.SelectedIndex == 0 ? RenameDatePlacement.Prefix : RenameDatePlacement.Suffix,
            DateFormatBox.Text,
            DateSeparatorBox.Text,
            ExtensionModeComboBox.SelectedIndex switch
            {
                1 => RenameExtensionMode.Lower,
                2 => RenameExtensionMode.Upper,
                3 => RenameExtensionMode.Replace,
                _ => RenameExtensionMode.Keep
            },
            CustomExtensionBox.Text);
    }

    private void UpdateRuleAvailability()
    {
        BaseNameBox.IsEnabled = NameModeComboBox.SelectedIndex == 1;
        CustomExtensionBox.IsEnabled = ExtensionModeComboBox.SelectedIndex == 3;

        bool useNumbering = UseNumberingCheckBox.IsChecked == true;
        NumberStartBox.IsEnabled = useNumbering;
        NumberStepBox.IsEnabled = useNumbering;
        NumberPaddingBox.IsEnabled = useNumbering;
        NumberPlacementComboBox.IsEnabled = useNumbering;
        NumberSeparatorBox.IsEnabled = useNumbering;
    }

    private static int IntegerOrDefault(NumberBox box, int fallback)
    {
        return double.IsFinite(box.Value) ? (int)Math.Round(box.Value) : fallback;
    }

    private static HashSet<string> ParseExtensionFilter(string text)
    {
        return text
            .Split([',', ';', ' '], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(value => value.StartsWith('.') ? value : "." + value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private void ShowInfo(string message, InfoBarSeverity severity)
    {
        RenameInfoBar.Message = message;
        RenameInfoBar.Severity = severity;
        RenameInfoBar.IsOpen = true;
    }
}
