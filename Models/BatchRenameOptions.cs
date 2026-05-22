namespace Toolbox.Models;

public sealed record BatchRenameOptions(
    RenameNameMode NameMode,
    string BaseName,
    string Prefix,
    string Suffix,
    string FindText,
    string ReplaceText,
    RenameCaseMode CaseMode,
    bool UseNumbering,
    int NumberStart,
    int NumberStep,
    int NumberPadding,
    RenameNumberPlacement NumberPlacement,
    string NumberSeparator,
    RenameDateMode DateMode,
    RenameDatePlacement DatePlacement,
    string DateFormat,
    string DateSeparator,
    RenameExtensionMode ExtensionMode,
    string CustomExtension);
