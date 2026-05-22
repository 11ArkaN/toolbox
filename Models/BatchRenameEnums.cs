namespace Toolbox.Models;

public enum RenameNameMode
{
    KeepOriginal,
    ReplaceName
}

public enum RenameCaseMode
{
    Keep,
    Lower,
    Upper,
    Title
}

public enum RenameNumberPlacement
{
    Prefix,
    Suffix
}

public enum RenameDateMode
{
    None,
    Today,
    Created,
    Modified
}

public enum RenameDatePlacement
{
    Prefix,
    Suffix
}

public enum RenameExtensionMode
{
    Keep,
    Lower,
    Upper,
    Replace
}
