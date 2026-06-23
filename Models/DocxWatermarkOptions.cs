namespace Toolbox.Models;

public enum DocxWatermarkPosition
{
    Random,
    BottomLeft,
    BottomRight,
    Center
}

public sealed record DocxWatermarkOptions(
    string Text,
    double Opacity,
    DocxWatermarkPosition Position,
    string Seed,
    string? FontPath,
    int MinFontSize,
    int MaxFontSize);

public sealed record DocxEmbeddedImagePreview(
    string DocumentPath,
    string MediaPath,
    string Extension,
    int Width,
    int Height,
    int VisibleRegionCount,
    byte[] PngBytes);

public sealed record DocxWatermarkDocumentResult(
    string InputPath,
    string OutputPath,
    int ProcessedImages,
    int SkippedImages);
