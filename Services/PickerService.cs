using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Toolbox.Services;

public static class PickerService
{
    public static async Task<StorageFile?> PickAudioFileAsync()
    {
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.MusicLibrary
        };

        foreach (string extension in new[] { ".mp3", ".wav", ".flac", ".aac", ".ogg", ".m4a", ".wma" })
        {
            picker.FileTypeFilter.Add(extension);
        }

        InitializeWithWindow.Initialize(picker, App.GetMainWindowHandle());
        return await picker.PickSingleFileAsync();
    }

    public static async Task<StorageFile?> PickPdfFileAsync()
    {
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary
        };

        picker.FileTypeFilter.Add(".pdf");
        InitializeWithWindow.Initialize(picker, App.GetMainWindowHandle());
        return await picker.PickSingleFileAsync();
    }

    public static async Task<IReadOnlyList<StorageFile>> PickPdfFilesAsync()
    {
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary
        };

        picker.FileTypeFilter.Add(".pdf");
        InitializeWithWindow.Initialize(picker, App.GetMainWindowHandle());
        return await picker.PickMultipleFilesAsync();
    }

    public static async Task<IReadOnlyList<StorageFile>> PickImageFilesAsync()
    {
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.PicturesLibrary
        };

        foreach (string extension in new[] { ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif", ".tif", ".tiff" })
        {
            picker.FileTypeFilter.Add(extension);
        }

        InitializeWithWindow.Initialize(picker, App.GetMainWindowHandle());
        return await picker.PickMultipleFilesAsync();
    }

    public static async Task<IReadOnlyList<StorageFile>> PickAnyFilesAsync()
    {
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary
        };

        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, App.GetMainWindowHandle());
        IReadOnlyList<StorageFile> files = await picker.PickMultipleFilesAsync();
        return files;
    }

    public static async Task<StorageFolder?> PickFolderAsync()
    {
        var picker = new FolderPicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary
        };

        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, App.GetMainWindowHandle());
        return await picker.PickSingleFolderAsync();
    }
}
