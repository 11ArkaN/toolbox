using System.Globalization;
using Toolbox.Models;

namespace Toolbox.Services;

public sealed class BatchRenameService
{
    public IReadOnlyList<BatchRenamePreviewItem> BuildPreview(
        IReadOnlyList<string> filePaths,
        BatchRenameOptions options)
    {
        var items = filePaths
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => Path.GetFileName(path), StringComparer.CurrentCultureIgnoreCase)
            .Select((path, index) =>
            {
                var item = new BatchRenamePreviewItem(path);
                item.NewName = BuildName(path, index, options);
                return item;
            })
            .ToList();

        Validate(items);
        return items;
    }

    public void Apply(IReadOnlyList<BatchRenamePreviewItem> items)
    {
        var renames = items
            .Where(item => item.CanRename)
            .Where(item => !PathsExactlyEqual(item.OriginalPath, item.TargetPath))
            .ToList();

        if (renames.Count == 0)
        {
            return;
        }

        var temporaryMoves = new List<(string OriginalPath, string TempPath, string TargetPath)>();
        var completedFinalMoves = new List<(string OriginalPath, string TargetPath)>();

        try
        {
            foreach (BatchRenamePreviewItem item in renames)
            {
                string tempPath = Path.Combine(item.DirectoryPath, $".toolbox-rename-{Guid.NewGuid():N}.tmp");
                File.Move(item.OriginalPath, tempPath);
                temporaryMoves.Add((item.OriginalPath, tempPath, item.TargetPath));
            }

            foreach ((string originalPath, string tempPath, string targetPath) in temporaryMoves)
            {
                File.Move(tempPath, targetPath);
                completedFinalMoves.Add((originalPath, targetPath));
            }
        }
        catch
        {
            foreach ((string originalPath, string targetPath) in completedFinalMoves.AsEnumerable().Reverse())
            {
                if (File.Exists(targetPath) && !File.Exists(originalPath))
                {
                    File.Move(targetPath, originalPath);
                }
            }

            foreach ((string originalPath, string tempPath, _) in temporaryMoves.AsEnumerable().Reverse())
            {
                if (File.Exists(tempPath) && !File.Exists(originalPath))
                {
                    File.Move(tempPath, originalPath);
                }
            }

            throw;
        }
    }

    private static string BuildName(string path, int index, BatchRenameOptions options)
    {
        string originalName = Path.GetFileNameWithoutExtension(path);
        string extension = Path.GetExtension(path);
        string name = options.NameMode == RenameNameMode.ReplaceName && !string.IsNullOrWhiteSpace(options.BaseName)
            ? options.BaseName.Trim()
            : originalName;

        if (!string.IsNullOrEmpty(options.FindText))
        {
            name = name.Replace(options.FindText, options.ReplaceText ?? string.Empty, StringComparison.CurrentCultureIgnoreCase);
        }

        name = ApplyCase(name, options.CaseMode);
        name = $"{options.Prefix}{name}{options.Suffix}";

        if (options.UseNumbering)
        {
            int number = options.NumberStart + (index * options.NumberStep);
            string formattedNumber = Math.Max(0, number).ToString(new string('0', Math.Clamp(options.NumberPadding, 1, 12)), CultureInfo.InvariantCulture);
            name = options.NumberPlacement == RenameNumberPlacement.Prefix
                ? $"{formattedNumber}{options.NumberSeparator}{name}"
                : $"{name}{options.NumberSeparator}{formattedNumber}";
        }

        string dateText = BuildDateText(path, options);
        if (!string.IsNullOrWhiteSpace(dateText))
        {
            name = options.DatePlacement == RenameDatePlacement.Prefix
                ? $"{dateText}{options.DateSeparator}{name}"
                : $"{name}{options.DateSeparator}{dateText}";
        }

        extension = ApplyExtension(extension, options);
        return SanitizeFileName(name) + extension;
    }

    private static string ApplyCase(string value, RenameCaseMode mode)
    {
        return mode switch
        {
            RenameCaseMode.Lower => value.ToLower(CultureInfo.CurrentCulture),
            RenameCaseMode.Upper => value.ToUpper(CultureInfo.CurrentCulture),
            RenameCaseMode.Title => CultureInfo.CurrentCulture.TextInfo.ToTitleCase(value.ToLower(CultureInfo.CurrentCulture)),
            _ => value
        };
    }

    private static string BuildDateText(string path, BatchRenameOptions options)
    {
        DateTime? date = options.DateMode switch
        {
            RenameDateMode.Today => DateTime.Today,
            RenameDateMode.Created => File.GetCreationTime(path),
            RenameDateMode.Modified => File.GetLastWriteTime(path),
            _ => null
        };

        if (date is null)
        {
            return string.Empty;
        }

        string format = string.IsNullOrWhiteSpace(options.DateFormat) ? "yyyy-MM-dd" : options.DateFormat.Trim();
        return date.Value.ToString(format, CultureInfo.InvariantCulture);
    }

    private static string ApplyExtension(string extension, BatchRenameOptions options)
    {
        return options.ExtensionMode switch
        {
            RenameExtensionMode.Lower => extension.ToLowerInvariant(),
            RenameExtensionMode.Upper => extension.ToUpperInvariant(),
            RenameExtensionMode.Replace => NormalizeExtension(options.CustomExtension),
            _ => extension
        };
    }

    private static string NormalizeExtension(string extension)
    {
        string trimmed = (extension ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return string.Empty;
        }

        return trimmed.StartsWith('.') ? trimmed : "." + trimmed;
    }

    private static string SanitizeFileName(string fileName)
    {
        string sanitized = fileName.Trim();
        foreach (char invalid in Path.GetInvalidFileNameChars())
        {
            sanitized = sanitized.Replace(invalid, '-');
        }

        return string.IsNullOrWhiteSpace(sanitized) ? "renamed" : sanitized;
    }

    private static void Validate(IReadOnlyList<BatchRenamePreviewItem> items)
    {
        var targetGroups = items
            .GroupBy(item => item.TargetPath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

        foreach (BatchRenamePreviewItem item in items)
        {
            if (string.IsNullOrWhiteSpace(item.NewName))
            {
                item.Status = "Empty name";
                item.CanRename = false;
                continue;
            }

            if (targetGroups[item.TargetPath] > 1)
            {
                item.Status = "Duplicate target";
                item.CanRename = false;
                continue;
            }

            if (PathsExactlyEqual(item.OriginalPath, item.TargetPath))
            {
                item.Status = "Unchanged";
                item.CanRename = false;
                continue;
            }

            bool targetExistsOutsideBatch = File.Exists(item.TargetPath)
                && !items.Any(other => PathsEqualIgnoreCase(other.OriginalPath, item.TargetPath));
            if (targetExistsOutsideBatch)
            {
                item.Status = "Target exists";
                item.CanRename = false;
                continue;
            }

            item.Status = "Ready";
            item.CanRename = true;
        }
    }

    private static bool PathsExactlyEqual(string first, string second)
    {
        return string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), StringComparison.Ordinal);
    }

    private static bool PathsEqualIgnoreCase(string first, string second)
    {
        return string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase);
    }
}
