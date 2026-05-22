using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Toolbox.Models;

public sealed class BatchRenamePreviewItem : INotifyPropertyChanged
{
    private string _newName = string.Empty;
    private string _status = string.Empty;
    private bool _canRename;

    public BatchRenamePreviewItem(string originalPath)
    {
        OriginalPath = originalPath;
        OriginalName = Path.GetFileName(originalPath);
        DirectoryPath = Path.GetDirectoryName(originalPath) ?? string.Empty;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string OriginalPath { get; }

    public string OriginalName { get; }

    public string DirectoryPath { get; }

    public string NewName
    {
        get => _newName;
        set => SetField(ref _newName, value);
    }

    public string TargetPath => Path.Combine(DirectoryPath, NewName);

    public string Status
    {
        get => _status;
        set => SetField(ref _status, value);
    }

    public bool CanRename
    {
        get => _canRename;
        set => SetField(ref _canRename, value);
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        if (propertyName == nameof(NewName))
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TargetPath)));
        }
    }
}
