using PdfSharpCore.Drawing;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Toolbox.Models;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Toolbox.Services;

using ImageSharpRectangle = SixLabors.ImageSharp.Rectangle;
using PdfSharpDocument = PdfSharpCore.Pdf.PdfDocument;
using WinPdfDocument = Windows.Data.Pdf.PdfDocument;
using WinPdfPage = Windows.Data.Pdf.PdfPage;

public sealed class PdfSlideSplitService
{
    public async Task<byte[]> RenderFirstPagePreviewAsync(
        StorageFile pdfFile,
        int dpi = 120,
        CancellationToken cancellationToken = default)
    {
        WinPdfDocument document = await OpenDocumentAsync(pdfFile, cancellationToken);
        if (document.PageCount == 0)
        {
            throw new InvalidOperationException("PDF contains no pages.");
        }

        using WinPdfPage page = document.GetPage(0);
        return await RenderPageToPngBytesAsync(page, dpi, cancellationToken);
    }

    public async Task<SlideSplitResult> ExtractAsync(
        StorageFile pdfFile,
        string outputDirectory,
        SlideSplitSettings settings,
        IProgress<SlideSplitProgress> progress,
        CancellationToken cancellationToken = default)
    {
        if (!settings.ExportPng && !settings.ExportPdf)
        {
            throw new InvalidOperationException("Select PNG, PDF, or both outputs.");
        }

        if (settings.VisualCropMode && settings.VisualCropRectangles.Count < 2)
        {
            throw new InvalidOperationException("Visual crop mode requires at least two selected rectangles.");
        }

        string pdfPath = pdfFile.Path;
        if (!File.Exists(pdfPath))
        {
            throw new FileNotFoundException("PDF file does not exist.", pdfPath);
        }

        Directory.CreateDirectory(outputDirectory);

        WinPdfDocument sourceDocument = await OpenDocumentAsync(pdfFile, cancellationToken);
        if (sourceDocument.PageCount == 0)
        {
            throw new InvalidOperationException("PDF contains no pages.");
        }

        string baseName = Path.GetFileNameWithoutExtension(pdfPath);
        string? tempDirectory = settings.ExportPdf && !settings.ExportPng
            ? Directory.CreateTempSubdirectory("toolbox-slides-").FullName
            : null;

        var slideImages = new List<SlideImage>();
        int attemptedSlices = 0;
        int slicesPerPage = settings.VisualCropMode ? settings.VisualCropRectangles.Count : 2;
        int totalSlices = checked((int)sourceDocument.PageCount * slicesPerPage);

        try
        {
            for (uint pageIndex = 0; pageIndex < sourceDocument.PageCount; pageIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress.Report(new SlideSplitProgress(Percent(attemptedSlices, totalSlices), $"Rendering page {pageIndex + 1}..."));

                using WinPdfPage page = sourceDocument.GetPage(pageIndex);
                byte[] pagePng = await RenderPageToPngBytesAsync(page, settings.Dpi, cancellationToken);

                using Image<Rgba32> image = Image.Load<Rgba32>(pagePng);
                foreach (CropSegment segment in BuildCropRectangles(image.Width, image.Height, settings))
                {
                    attemptedSlices++;
                    if (segment.Crop.Width <= 0 || segment.Crop.Height <= 0)
                    {
                        progress.Report(new SlideSplitProgress(Percent(attemptedSlices, totalSlices), $"Skipped empty slice on page {pageIndex + 1}."));
                        continue;
                    }

                    using Image<Rgba32> slide = image.Clone(context => context.Crop(segment.Crop));
                    string fileName = $"{baseName}_p{pageIndex + 1:000}_{segment.Name}.png";
                    string imagePath = settings.ExportPng
                        ? Path.Combine(outputDirectory, fileName)
                        : Path.Combine(tempDirectory!, fileName);

                    await slide.SaveAsPngAsync(imagePath, cancellationToken);
                    slideImages.Add(new SlideImage(imagePath, slide.Width, slide.Height));

                    progress.Report(new SlideSplitProgress(Percent(attemptedSlices, totalSlices), $"Created {fileName}."));
                }
            }

            if (slideImages.Count == 0)
            {
                throw new InvalidOperationException("No valid slide crops were produced.");
            }

            string? pdfPathOut = null;
            if (settings.ExportPdf)
            {
                progress.Report(new SlideSplitProgress(96, "Creating combined PDF..."));
                pdfPathOut = Path.Combine(outputDirectory, $"{baseName}_slides.pdf");
                CreatePdfFromImages(slideImages, pdfPathOut, settings.Dpi);
            }

            progress.Report(new SlideSplitProgress(100, "Slide extraction completed."));
            return new SlideSplitResult(slideImages.Count, outputDirectory, pdfPathOut);
        }
        finally
        {
            if (tempDirectory is not null && Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    private static async Task<WinPdfDocument> OpenDocumentAsync(StorageFile file, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await WinPdfDocument.LoadFromFileAsync(file);
    }

    private static async Task<byte[]> RenderPageToPngBytesAsync(WinPdfPage page, int dpi, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        uint destinationWidth = Math.Max(1, (uint)Math.Round(page.Size.Width * dpi / 96.0));
        uint destinationHeight = Math.Max(1, (uint)Math.Round(page.Size.Height * dpi / 96.0));

        using var stream = new InMemoryRandomAccessStream();
        var options = new PdfPageRenderOptions
        {
            DestinationWidth = destinationWidth,
            DestinationHeight = destinationHeight
        };

        await page.RenderToStreamAsync(stream, options);
        return await ReadRandomAccessStreamAsync(stream);
    }

    private static async Task<byte[]> ReadRandomAccessStreamAsync(IRandomAccessStream stream)
    {
        if (stream.Size > int.MaxValue)
        {
            throw new InvalidOperationException("Rendered PDF page is too large to process in memory.");
        }

        var bytes = new byte[(int)stream.Size];
        using var reader = new DataReader(stream.GetInputStreamAt(0));
        await reader.LoadAsync((uint)stream.Size);
        reader.ReadBytes(bytes);
        return bytes;
    }

    private static IEnumerable<CropSegment> BuildCropRectangles(
        int width,
        int height,
        SlideSplitSettings settings)
    {
        if (settings.VisualCropMode)
        {
            for (int index = 0; index < settings.VisualCropRectangles.Count; index++)
            {
                NormalizedCropRectangle rectangle = settings.VisualCropRectangles[index];
                yield return new CropSegment(
                    $"r{index + 1:00}",
                    ClampCrop(
                        PixelRatio(width, rectangle.X),
                        PixelRatio(height, rectangle.Y),
                        PixelRatio(width, rectangle.Width),
                        PixelRatio(height, rectangle.Height),
                        width,
                        height));
            }

            yield break;
        }

        int left = PixelPercent(width, settings.MarginLeftPercent);
        int right = width - PixelPercent(width, settings.MarginRightPercent);
        int top = PixelPercent(height, settings.MarginTopPercent);
        int bottom = height - PixelPercent(height, settings.MarginBottomPercent);
        int split = PixelPercent(height, settings.SplitPercent);
        int halfGutter = Math.Max(0, settings.GutterPixels / 2);

        int cropWidth = Math.Max(0, right - left);
        int topBottom = split - halfGutter;
        int bottomTop = split + halfGutter;

        var topSlide = ClampCrop(left, top, cropWidth, topBottom - top, width, height);
        var bottomSlide = ClampCrop(left, bottomTop, cropWidth, bottom - bottomTop, width, height);

        yield return new CropSegment("s1", topSlide);
        yield return new CropSegment("s2", bottomSlide);
    }

    private static ImageSharpRectangle ClampCrop(int x, int y, int width, int height, int imageWidth, int imageHeight)
    {
        int clampedX = Math.Clamp(x, 0, Math.Max(0, imageWidth - 1));
        int clampedY = Math.Clamp(y, 0, Math.Max(0, imageHeight - 1));
        int clampedRight = Math.Clamp(x + width, clampedX, imageWidth);
        int clampedBottom = Math.Clamp(y + height, clampedY, imageHeight);

        return new ImageSharpRectangle(
            clampedX,
            clampedY,
            Math.Max(0, clampedRight - clampedX),
            Math.Max(0, clampedBottom - clampedY));
    }

    private static int PixelPercent(int value, double percent)
    {
        return (int)Math.Round(value * Math.Clamp(percent, 0, 100) / 100.0);
    }

    private static int PixelRatio(int value, double ratio)
    {
        return (int)Math.Round(value * Math.Clamp(ratio, 0, 1));
    }

    private static int Percent(int done, int total)
    {
        if (total <= 0)
        {
            return 0;
        }

        return Math.Clamp((int)Math.Round(done * 94.0 / total), 0, 94);
    }

    private static void CreatePdfFromImages(IReadOnlyList<SlideImage> slideImages, string outputPath, int dpi)
    {
        using var document = new PdfSharpDocument();

        foreach (SlideImage slideImage in slideImages)
        {
            using XImage image = XImage.FromFile(slideImage.Path);
            PdfSharpCore.Pdf.PdfPage page = document.AddPage();
            page.Width = XUnit.FromPoint(slideImage.Width * 72.0 / dpi);
            page.Height = XUnit.FromPoint(slideImage.Height * 72.0 / dpi);

            using XGraphics graphics = XGraphics.FromPdfPage(page);
            graphics.DrawImage(image, 0, 0, page.Width, page.Height);
        }

        document.Save(outputPath);
    }

    private sealed record SlideImage(string Path, int Width, int Height);

    private sealed record CropSegment(string Name, ImageSharpRectangle Crop);
}
