namespace Toolbox.Models;

public sealed record SlideSplitSettings(
    double SplitPercent,
    double MarginLeftPercent,
    double MarginRightPercent,
    double MarginTopPercent,
    double MarginBottomPercent,
    int GutterPixels,
    int Dpi,
    bool ExportPng,
    bool ExportPdf,
    bool VisualCropMode,
    IReadOnlyList<NormalizedCropRectangle> VisualCropRectangles);
