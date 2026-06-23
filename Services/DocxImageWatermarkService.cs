using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Toolbox.Models;

namespace Toolbox.Services;

using DrawingImage = System.Drawing.Image;
using DrawingRectangle = System.Drawing.Rectangle;

public sealed class DocxImageWatermarkService
{
    private const int PreviewMaxWidth = 520;
    private const int PreviewMaxHeight = 340;

    private static readonly XNamespace DrawingNamespace = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace RelationshipNamespace = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace PackageRelationshipNamespace = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly XNamespace PictureNamespace = "http://schemas.openxmlformats.org/drawingml/2006/picture";
    private static readonly XNamespace VmlNamespace = "urn:schemas-microsoft-com:vml";

    private static readonly string[] FontCandidates =
    [
        @"C:\Windows\Fonts\arialbd.ttf",
        @"C:\Windows\Fonts\arial.ttf",
        @"C:\Windows\Fonts\calibrib.ttf",
        @"C:\Windows\Fonts\calibri.ttf"
    ];

    public IReadOnlyList<string> SupportedExtensions { get; } =
    [
        ".png",
        ".jpg",
        ".jpeg",
        ".bmp",
        ".tif",
        ".tiff"
    ];

    public Task<IReadOnlyList<DocxEmbeddedImagePreview>> BuildPreviewAsync(
        string inputPath,
        DocxWatermarkOptions options,
        IProgress<ToolProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            if (!File.Exists(inputPath))
            {
                throw new FileNotFoundException("DOCX file was not found.", inputPath);
            }

            using var archive = ZipFile.OpenRead(inputPath);
            Dictionary<string, List<NormalizedRect>> visibleRects = CollectVisibleRects(archive);
            ZipArchiveEntry[] imageEntries = archive.Entries
                .Where(entry => IsSupportedMediaEntry(entry.FullName))
                .OrderBy(entry => entry.FullName, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var previews = new List<DocxEmbeddedImagePreview>();
            using FontResources fontResources = LoadFont(options.FontPath);

            for (int index = 0; index < imageEntries.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ZipArchiveEntry entry = imageEntries[index];
                byte[] raw = ReadEntryBytes(entry);
                string extension = Path.GetExtension(entry.FullName).ToLowerInvariant();
                IReadOnlyList<NormalizedRect> rects = visibleRects.TryGetValue(entry.FullName, out List<NormalizedRect>? found)
                    ? found
                    : [NormalizedRect.Full];

                try
                {
                    using var originalStream = new MemoryStream(raw);
                    using DrawingImage original = DrawingImage.FromStream(originalStream, useEmbeddedColorManagement: true, validateImageData: false);
                    byte[] previewBytes = string.IsNullOrWhiteSpace(options.Text)
                        ? BuildPreviewPng(raw)
                        : BuildPreviewPng(WatermarkImage(raw, extension, rects, entry.FullName + "|" + options.Seed, options, fontResources));

                    previews.Add(new DocxEmbeddedImagePreview(
                        inputPath,
                        entry.FullName,
                        extension,
                        original.Width,
                        original.Height,
                        DedupeRects(rects).Count,
                        previewBytes));
                }
                catch
                {
                    // A broken or unsupported embedded bitmap should not prevent
                    // the remaining previews from being shown.
                }

                progress?.Report(new ToolProgress(Percent(index + 1, imageEntries.Length), $"Loaded preview {index + 1} of {imageEntries.Length}."));
            }

            progress?.Report(new ToolProgress(100, $"Loaded {previews.Count} image preview(s)."));
            return (IReadOnlyList<DocxEmbeddedImagePreview>)previews;
        }, cancellationToken);
    }

    public Task<IReadOnlyList<DocxWatermarkDocumentResult>> ProcessDocumentsAsync(
        IReadOnlyList<string> inputPaths,
        string outputDirectory,
        string suffix,
        DocxWatermarkOptions options,
        IProgress<ToolProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            if (inputPaths.Count == 0)
            {
                throw new InvalidOperationException("Select at least one DOCX file.");
            }

            if (string.IsNullOrWhiteSpace(options.Text))
            {
                throw new InvalidOperationException("Enter watermark text.");
            }

            Directory.CreateDirectory(outputDirectory);
            string normalizedSuffix = string.IsNullOrWhiteSpace(suffix) ? "_watermark" : suffix.Trim();
            var results = new List<DocxWatermarkDocumentResult>();

            for (int index = 0; index < inputPaths.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string inputPath = inputPaths[index];
                string outputPath = BuildOutputPath(inputPath, outputDirectory, normalizedSuffix);
                DocxWatermarkDocumentResult result = ProcessDocument(inputPath, outputPath, options, cancellationToken);
                results.Add(result);

                progress?.Report(new ToolProgress(
                    Percent(index + 1, inputPaths.Count),
                    $"Created {Path.GetFileName(result.OutputPath)} with {result.ProcessedImages} image(s)."));
            }

            progress?.Report(new ToolProgress(100, $"Finished {results.Count} DOCX file(s)."));
            return (IReadOnlyList<DocxWatermarkDocumentResult>)results;
        }, cancellationToken);
    }

    private DocxWatermarkDocumentResult ProcessDocument(
        string inputPath,
        string outputPath,
        DocxWatermarkOptions options,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(inputPath))
        {
            throw new FileNotFoundException("DOCX file was not found.", inputPath);
        }

        if (Path.GetFullPath(inputPath).Equals(Path.GetFullPath(outputPath), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Output DOCX path cannot be the same as the source path.");
        }

        int processed = 0;
        int skipped = 0;

        using var sourceArchive = ZipFile.OpenRead(inputPath);
        Dictionary<string, List<NormalizedRect>> visibleRects = CollectVisibleRects(sourceArchive);
        using FontResources fontResources = LoadFont(options.FontPath);

        using var outputStream = File.Create(outputPath);
        using var outputArchive = new ZipArchive(outputStream, ZipArchiveMode.Create);

        foreach (ZipArchiveEntry sourceEntry in sourceArchive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ZipArchiveEntry targetEntry = outputArchive.CreateEntry(sourceEntry.FullName, CompressionLevel.Fastest);
            targetEntry.LastWriteTime = sourceEntry.LastWriteTime;
            byte[] raw = ReadEntryBytes(sourceEntry);

            if (IsSupportedMediaEntry(sourceEntry.FullName))
            {
                string extension = Path.GetExtension(sourceEntry.FullName).ToLowerInvariant();
                IReadOnlyList<NormalizedRect> rects = visibleRects.TryGetValue(sourceEntry.FullName, out List<NormalizedRect>? found)
                    ? found
                    : [NormalizedRect.Full];

                try
                {
                    raw = WatermarkImage(raw, extension, rects, sourceEntry.FullName + "|" + options.Seed, options, fontResources);
                    processed++;
                }
                catch
                {
                    skipped++;
                }
            }

            using Stream targetStream = targetEntry.Open();
            targetStream.Write(raw);
        }

        return new DocxWatermarkDocumentResult(inputPath, outputPath, processed, skipped);
    }

    private static Dictionary<string, List<NormalizedRect>> CollectVisibleRects(ZipArchive archive)
    {
        var result = new Dictionary<string, List<NormalizedRect>>(StringComparer.OrdinalIgnoreCase);
        ZipArchiveEntry[] xmlEntries = archive.Entries
            .Where(entry => entry.FullName.StartsWith("word/", StringComparison.OrdinalIgnoreCase)
                && entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
                && !entry.FullName.Contains("/_rels/", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        foreach (ZipArchiveEntry xmlEntry in xmlEntries)
        {
            Dictionary<string, string> relationships = ReadRelationships(archive, xmlEntry.FullName);
            if (relationships.Count == 0)
            {
                continue;
            }

            XDocument document;
            try
            {
                using Stream xmlStream = xmlEntry.Open();
                document = XDocument.Load(xmlStream);
            }
            catch
            {
                continue;
            }

            foreach (XElement picture in document.Descendants(PictureNamespace + "pic"))
            {
                XElement? blip = picture.Descendants(DrawingNamespace + "blip").FirstOrDefault();
                string? relationshipId = blip?.Attribute(RelationshipNamespace + "embed")?.Value;
                if (relationshipId is null || !relationships.TryGetValue(relationshipId, out string? mediaPath))
                {
                    continue;
                }

                XElement? sourceRect = picture.Descendants(DrawingNamespace + "srcRect").FirstOrDefault();
                AddRect(result, mediaPath, CropFromSourceRect(sourceRect));
            }

            foreach (XElement imageData in document.Descendants(VmlNamespace + "imagedata"))
            {
                string? relationshipId = imageData.Attribute(RelationshipNamespace + "id")?.Value;
                if (relationshipId is not null && relationships.TryGetValue(relationshipId, out string? mediaPath))
                {
                    AddRect(result, mediaPath, NormalizedRect.Full);
                }
            }
        }

        return result;
    }

    private static Dictionary<string, string> ReadRelationships(ZipArchive archive, string partName)
    {
        string relationshipsName = RelationshipsNameForPart(partName);
        ZipArchiveEntry? relationshipsEntry = archive.GetEntry(relationshipsName);
        if (relationshipsEntry is null)
        {
            return [];
        }

        XDocument document;
        try
        {
            using Stream relationshipStream = relationshipsEntry.Open();
            document = XDocument.Load(relationshipStream);
        }
        catch
        {
            return [];
        }

        var relationships = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (XElement relationship in document.Descendants(PackageRelationshipNamespace + "Relationship"))
        {
            string? id = relationship.Attribute("Id")?.Value;
            string? target = relationship.Attribute("Target")?.Value;
            string mode = relationship.Attribute("TargetMode")?.Value ?? string.Empty;

            if (string.IsNullOrWhiteSpace(id)
                || string.IsNullOrWhiteSpace(target)
                || mode.Equals("External", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            relationships[id] = ResolveTarget(partName, target);
        }

        return relationships;
    }

    private static byte[] WatermarkImage(
        byte[] data,
        string extension,
        IReadOnlyList<NormalizedRect> rects,
        string seedName,
        DocxWatermarkOptions options,
        FontResources fontResources)
    {
        using var inputStream = new MemoryStream(data);
        using DrawingImage source = DrawingImage.FromStream(inputStream, useEmbeddedColorManagement: true, validateImageData: false);
        using var bitmap = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);

        using (Graphics initialGraphics = Graphics.FromImage(bitmap))
        {
            initialGraphics.CompositingQuality = CompositingQuality.HighQuality;
            initialGraphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            initialGraphics.SmoothingMode = SmoothingMode.HighQuality;
            initialGraphics.DrawImage(source, 0, 0, source.Width, source.Height);
        }

        foreach ((NormalizedRect rect, int index) in DedupeRects(rects).Select((rect, index) => (rect, index)))
        {
            DrawingRectangle visible = RectToPixels(rect, bitmap.Width, bitmap.Height);
            if (visible.Width <= 0 || visible.Height <= 0)
            {
                continue;
            }

            FitResult fit = FitFont(options.Text, fontResources, visible.Width, visible.Height, options.MinFontSize, options.MaxFontSize);
            using var font = new Font(fontResources.Family, fit.Size, fontResources.Style, GraphicsUnit.Pixel);
            TextBounds bounds = MeasureText(options.Text, font, fit.Spacing);
            Point position = ChoosePosition(options.Position, seedName + "|" + index + "|" + rect, visible, bounds, fit.Margin);

            var textArea = DrawingRectangle.Intersect(
                new DrawingRectangle(
                    position.X,
                    position.Y,
                    Math.Max(1, (int)Math.Ceiling(bounds.Width)),
                    Math.Max(1, (int)Math.Ceiling(bounds.Height))),
                new DrawingRectangle(0, 0, bitmap.Width, bitmap.Height));

            Color color = ChooseColor(bitmap, textArea, options.Opacity);
            DrawMultilineText(bitmap, options.Text, fontResources, position.X, position.Y, fit.Size, fit.Spacing, color);
        }

        using var outputStream = new MemoryStream();
        SaveImage(bitmap, outputStream, extension);
        return outputStream.ToArray();
    }

    private static byte[] BuildPreviewPng(byte[] data)
    {
        using var sourceStream = new MemoryStream(data);
        using DrawingImage source = DrawingImage.FromStream(sourceStream, useEmbeddedColorManagement: true, validateImageData: false);
        Size previewSize = FitInside(source.Width, source.Height, PreviewMaxWidth, PreviewMaxHeight);

        using var preview = new Bitmap(previewSize.Width, previewSize.Height, PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(preview))
        {
            graphics.Clear(Color.Transparent);
            graphics.CompositingQuality = CompositingQuality.HighQuality;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.SmoothingMode = SmoothingMode.HighQuality;
            graphics.DrawImage(source, 0, 0, previewSize.Width, previewSize.Height);
        }

        using var output = new MemoryStream();
        preview.Save(output, ImageFormat.Png);
        return output.ToArray();
    }

    private static FitResult FitFont(
        string text,
        FontResources fontResources,
        int visibleWidth,
        int visibleHeight,
        int minSize,
        int maxSize)
    {
        minSize = Math.Max(1, minSize);
        maxSize = Math.Max(minSize, maxSize);
        double ratio = visibleWidth / (double)Math.Max(visibleHeight, 1);
        bool shortWide = ratio >= 4 || visibleHeight <= 100;
        int margin;
        int start;

        if (shortWide)
        {
            margin = visibleHeight <= 50 ? 1 : visibleHeight <= 90 ? 2 : 3;
            start = Math.Min(maxSize, Math.Max(minSize, (int)Math.Min(visibleHeight * 0.30, visibleWidth * 0.06)));
        }
        else
        {
            margin = Math.Max(3, Math.Min(8, (int)Math.Round(Math.Min(visibleWidth, visibleHeight) * 0.015)));
            start = Math.Min(maxSize, Math.Max(minSize, (int)(Math.Min(visibleWidth, visibleHeight) * 0.038)));
        }

        int availableWidth = Math.Max(1, visibleWidth - 2 * margin);
        int availableHeight = Math.Max(1, visibleHeight - 2 * margin);

        for (int size = start; size >= minSize; size--)
        {
            int spacing = size <= 10 ? 1 : Math.Max(1, (int)Math.Round(size * 0.08));
            using var font = new Font(fontResources.Family, size, fontResources.Style, GraphicsUnit.Pixel);
            TextBounds bounds = MeasureText(text, font, spacing);
            if (bounds.Width <= availableWidth && bounds.Height <= availableHeight)
            {
                return new FitResult(size, spacing, margin);
            }
        }

        return new FitResult(minSize, 1, margin);
    }

    private static TextBounds MeasureText(string text, Font font, int spacing)
    {
        string[] lines = SplitLines(text);
        using var scratch = new Bitmap(1, 1);
        using Graphics graphics = Graphics.FromImage(scratch);
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        using var format = new StringFormat(StringFormat.GenericTypographic)
        {
            FormatFlags = StringFormatFlags.MeasureTrailingSpaces
        };

        float width = 0;
        float lineHeight = font.GetHeight(graphics);
        foreach (string line in lines)
        {
            SizeF size = graphics.MeasureString(string.IsNullOrEmpty(line) ? " " : line, font, int.MaxValue, format);
            width = Math.Max(width, size.Width);
        }

        float height = lines.Length * lineHeight + Math.Max(0, lines.Length - 1) * spacing;
        return new TextBounds(width, height);
    }

    private static Point ChoosePosition(
        DocxWatermarkPosition position,
        string seedText,
        DrawingRectangle visibleRect,
        TextBounds bounds,
        int margin)
    {
        int minX = visibleRect.Left + margin;
        int maxX = visibleRect.Right - margin - (int)Math.Ceiling(bounds.Width);
        int minY = visibleRect.Top + margin;
        int maxY = visibleRect.Bottom - margin - (int)Math.Ceiling(bounds.Height);

        int x = position switch
        {
            DocxWatermarkPosition.BottomLeft => minX,
            DocxWatermarkPosition.BottomRight => maxX,
            DocxWatermarkPosition.Center => (minX + maxX) / 2,
            _ => DeterministicRandom(seedText + "|x", minX, maxX)
        };

        int y = position switch
        {
            DocxWatermarkPosition.BottomLeft or DocxWatermarkPosition.BottomRight => maxY,
            DocxWatermarkPosition.Center => (minY + maxY) / 2,
            _ => DeterministicRandom(seedText + "|y", minY, maxY)
        };

        return new Point(
            Math.Clamp(x, visibleRect.Left, Math.Max(visibleRect.Left, visibleRect.Right - 1)),
            Math.Clamp(y, visibleRect.Top, Math.Max(visibleRect.Top, visibleRect.Bottom - 1)));
    }

    private static int DeterministicRandom(string seedText, int minValue, int maxValue)
    {
        if (maxValue < minValue)
        {
            return minValue;
        }

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(seedText));
        int seed = BitConverter.ToInt32(hash, 0);
        return new Random(seed).Next(minValue, maxValue + 1);
    }

    private static Color ChooseColor(Bitmap bitmap, DrawingRectangle area, double opacity)
    {
        if (area.Width <= 0 || area.Height <= 0)
        {
            return Color.FromArgb(Alpha(opacity), 0, 0, 0);
        }

        int samplesTarget = 2400;
        int step = Math.Max(1, (int)Math.Sqrt(area.Width * area.Height / (double)samplesTarget));
        double luminanceSum = 0;
        int samples = 0;

        for (int y = area.Top; y < area.Bottom; y += step)
        {
            for (int x = area.Left; x < area.Right; x += step)
            {
                Color pixel = bitmap.GetPixel(x, y);
                luminanceSum += 0.2126 * pixel.R + 0.7152 * pixel.G + 0.0722 * pixel.B;
                samples++;
            }
        }

        double average = samples == 0 ? 127 : luminanceSum / samples;
        int alpha = Alpha(opacity);
        return average < 140
            ? Color.FromArgb(alpha, 255, 255, 255)
            : Color.FromArgb(alpha, 0, 0, 0);
    }

    private static void DrawMultilineText(
        Bitmap bitmap,
        string text,
        FontResources fontResources,
        int x,
        int y,
        int size,
        int spacing,
        Color color)
    {
        string[] lines = SplitLines(text);
        using Graphics graphics = Graphics.FromImage(bitmap);
        graphics.CompositingMode = CompositingMode.SourceOver;
        graphics.CompositingQuality = CompositingQuality.HighQuality;
        graphics.SmoothingMode = SmoothingMode.HighQuality;
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        using var font = new Font(fontResources.Family, size, fontResources.Style, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(color);
        using var format = new StringFormat(StringFormat.GenericTypographic);

        float currentY = y;
        float lineHeight = font.GetHeight(graphics);
        foreach (string line in lines)
        {
            if (!string.IsNullOrEmpty(line))
            {
                graphics.DrawString(line, font, brush, x, currentY, format);
            }

            currentY += lineHeight + spacing;
        }
    }

    private static void SaveImage(Bitmap bitmap, Stream stream, string extension)
    {
        switch (extension)
        {
            case ".jpg":
            case ".jpeg":
                ImageCodecInfo? jpegCodec = ImageCodecInfo.GetImageEncoders().FirstOrDefault(codec => codec.FormatID == ImageFormat.Jpeg.Guid);
                if (jpegCodec is null)
                {
                    bitmap.Save(stream, ImageFormat.Jpeg);
                    return;
                }

                using (var parameters = new EncoderParameters(1))
                {
                    parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 95L);
                    bitmap.Save(stream, jpegCodec, parameters);
                }

                break;
            case ".png":
                bitmap.Save(stream, ImageFormat.Png);
                break;
            case ".bmp":
                bitmap.Save(stream, ImageFormat.Bmp);
                break;
            case ".tif":
            case ".tiff":
                bitmap.Save(stream, ImageFormat.Tiff);
                break;
            default:
                bitmap.Save(stream, ImageFormat.Png);
                break;
        }
    }

    private static FontResources LoadFont(string? userFontPath)
    {
        string? fontPath = null;
        if (!string.IsNullOrWhiteSpace(userFontPath) && File.Exists(userFontPath))
        {
            fontPath = userFontPath;
        }
        else
        {
            fontPath = FontCandidates.FirstOrDefault(File.Exists);
        }

        if (fontPath is null)
        {
            FontFamily fallbackFamily = FontFamily.GenericSansSerif;
            FontStyle fallbackStyle = fallbackFamily.IsStyleAvailable(FontStyle.Bold) ? FontStyle.Bold : FontStyle.Regular;
            return new FontResources(null, fallbackFamily, fallbackStyle);
        }

        var privateFonts = new PrivateFontCollection();
        privateFonts.AddFontFile(fontPath);
        FontFamily family = privateFonts.Families[0];
        FontStyle style = family.IsStyleAvailable(FontStyle.Bold) ? FontStyle.Bold : FontStyle.Regular;
        return new FontResources(privateFonts, family, style);
    }

    private static bool IsSupportedMediaEntry(string entryName)
    {
        return entryName.StartsWith("word/media/", StringComparison.OrdinalIgnoreCase)
            && entryName.IndexOf('/', "word/media/".Length) < 0
            && Path.GetExtension(entryName).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".tif" or ".tiff";
    }

    private static string BuildOutputPath(string inputPath, string outputDirectory, string suffix)
    {
        string candidate = Path.Combine(outputDirectory, Path.GetFileNameWithoutExtension(inputPath) + suffix + ".docx");
        return UniquePath(candidate);
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path))
        {
            return path;
        }

        string directory = Path.GetDirectoryName(path) ?? Environment.CurrentDirectory;
        string baseName = Path.GetFileNameWithoutExtension(path);
        string extension = Path.GetExtension(path);
        int counter = 2;
        string candidate;
        do
        {
            candidate = Path.Combine(directory, $"{baseName}_{counter}{extension}");
            counter++;
        }
        while (File.Exists(candidate));

        return candidate;
    }

    private static void AddRect(IDictionary<string, List<NormalizedRect>> result, string mediaPath, NormalizedRect rect)
    {
        if (!result.TryGetValue(mediaPath, out List<NormalizedRect>? rects))
        {
            rects = [];
            result[mediaPath] = rects;
        }

        rects.Add(rect);
    }

    private static NormalizedRect CropFromSourceRect(XElement? sourceRect)
    {
        if (sourceRect is null)
        {
            return NormalizedRect.Full;
        }

        double left = ParseCropValue(sourceRect.Attribute("l")?.Value);
        double top = ParseCropValue(sourceRect.Attribute("t")?.Value);
        double right = 1.0 - ParseCropValue(sourceRect.Attribute("r")?.Value);
        double bottom = 1.0 - ParseCropValue(sourceRect.Attribute("b")?.Value);

        return right <= left || bottom <= top
            ? NormalizedRect.Full
            : new NormalizedRect(left, top, right, bottom);
    }

    private static double ParseCropValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0;
        }

        value = value.Trim();
        if (value.EndsWith('%'))
        {
            return ClampPercent(ParseDouble(value[..^1]) / 100.0);
        }

        return ClampPercent(ParseDouble(value) / 100000.0);
    }

    private static double ParseDouble(string value)
    {
        return double.TryParse(value.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double parsed)
            ? parsed
            : 0;
    }

    private static double ClampPercent(double value)
    {
        return Math.Clamp(value, 0, 1);
    }

    private static DrawingRectangle RectToPixels(NormalizedRect rect, int width, int height)
    {
        int left = Math.Clamp((int)Math.Round(rect.Left * width), 0, width);
        int top = Math.Clamp((int)Math.Round(rect.Top * height), 0, height);
        int right = Math.Clamp((int)Math.Round(rect.Right * width), 0, width);
        int bottom = Math.Clamp((int)Math.Round(rect.Bottom * height), 0, height);

        if (right <= left || bottom <= top)
        {
            return new DrawingRectangle(0, 0, width, height);
        }

        return DrawingRectangle.FromLTRB(left, top, right, bottom);
    }

    private static IReadOnlyList<NormalizedRect> DedupeRects(IReadOnlyList<NormalizedRect> rects)
    {
        var result = new List<NormalizedRect>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (NormalizedRect rect in rects)
        {
            string key = $"{Math.Round(rect.Left, 5)}|{Math.Round(rect.Top, 5)}|{Math.Round(rect.Right, 5)}|{Math.Round(rect.Bottom, 5)}";
            if (seen.Add(key))
            {
                result.Add(rect);
            }
        }

        return result.Count == 0 ? [NormalizedRect.Full] : result;
    }

    private static string RelationshipsNameForPart(string partName)
    {
        string directory = ZipDirectoryName(partName);
        string fileName = ZipFileName(partName);
        return NormalizeZipPath(string.IsNullOrEmpty(directory)
            ? "_rels/" + fileName + ".rels"
            : directory + "/_rels/" + fileName + ".rels");
    }

    private static string ResolveTarget(string partName, string target)
    {
        string directory = ZipDirectoryName(partName);
        return NormalizeZipPath(string.IsNullOrEmpty(directory) ? target : directory + "/" + target);
    }

    private static string NormalizeZipPath(string path)
    {
        var parts = new List<string>();
        foreach (string rawPart in path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (rawPart == ".")
            {
                continue;
            }

            if (rawPart == "..")
            {
                if (parts.Count > 0)
                {
                    parts.RemoveAt(parts.Count - 1);
                }

                continue;
            }

            parts.Add(rawPart);
        }

        return string.Join('/', parts);
    }

    private static string ZipDirectoryName(string path)
    {
        int slashIndex = path.LastIndexOf('/');
        return slashIndex <= 0 ? string.Empty : path[..slashIndex];
    }

    private static string ZipFileName(string path)
    {
        int slashIndex = path.LastIndexOf('/');
        return slashIndex < 0 ? path : path[(slashIndex + 1)..];
    }

    private static byte[] ReadEntryBytes(ZipArchiveEntry entry)
    {
        using Stream stream = entry.Open();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private static Size FitInside(int width, int height, int maxWidth, int maxHeight)
    {
        if (width <= 0 || height <= 0)
        {
            return new Size(1, 1);
        }

        double scale = Math.Min(maxWidth / (double)width, maxHeight / (double)height);
        scale = Math.Min(1, scale);
        return new Size(Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
    }

    private static string[] SplitLines(string text)
    {
        string normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        return normalized.Split('\n');
    }

    private static int Alpha(double opacity)
    {
        return Math.Clamp((int)Math.Round(Math.Clamp(opacity, 0, 1) * 255), 0, 255);
    }

    private static int Percent(int done, int total)
    {
        if (total <= 0)
        {
            return 0;
        }

        return Math.Clamp((int)Math.Round(done * 100.0 / total), 0, 100);
    }

    private readonly record struct NormalizedRect(double Left, double Top, double Right, double Bottom)
    {
        public static NormalizedRect Full { get; } = new(0, 0, 1, 1);
    }

    private sealed record FitResult(int Size, int Spacing, int Margin);

    private sealed record TextBounds(float Width, float Height);

    private sealed class FontResources(PrivateFontCollection? privateFonts, FontFamily family, FontStyle style) : IDisposable
    {
        public FontFamily Family { get; } = family;

        public FontStyle Style { get; } = style;

        public void Dispose()
        {
            privateFonts?.Dispose();
        }
    }
}
