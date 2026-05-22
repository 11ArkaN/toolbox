namespace Toolbox.Models;

public enum ImageOutputFormat
{
    Jpg,
    Png,
    Webp
}

public enum ImageResizeMode
{
    None,
    Fit,
    Fill,
    Stretch
}

public sealed record ImageConversionOptions(
    ImageOutputFormat Format,
    string Suffix,
    int Quality,
    ImageResizeMode ResizeMode,
    int Width,
    int Height,
    bool CropToAspectRatio,
    double AspectRatio);
