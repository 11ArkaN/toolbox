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
        int fragmentsPerSlide = settings.VisualCropMode ? Math.Max(2, settings.FragmentsPerSlide) : 1;
        int totalSlices = checked((int)sourceDocument.PageCount * 2 * fragmentsPerSlide);

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
                    string fragmentSuffix = segment.FragmentNumber is null ? string.Empty : $"_f{segment.FragmentNumber:00}";
                    string fileName = $"{baseName}_p{pageIndex + 1:000}_s{segment.SlideNumber}{fragmentSuffix}.png";
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

        foreach (CropSegment segment in SplitSlideIntoFragments(1, topSlide, settings))
        {
            yield return segment;
        }

        foreach (CropSegment segment in SplitSlideIntoFragments(2, bottomSlide, settings))
        {
            yield return segment;
        }
    }

    private static IEnumerable<CropSegment> SplitSlideIntoFragments(
        int slideNumber,
        ImageSharpRectangle slide,
        SlideSplitSettings settings)
    {
        if (!settings.VisualCropMode)
        {
            yield return new CropSegment(slideNumber, null, slide);
            yield break;
        }

        int count = Math.Max(2, settings.FragmentsPerSlide);
        for (int index = 0; index < count; index++)
        {
            int y0 = slide.Y + (int)Math.Round(slide.Height * index / (double)count);
            int y1 = slide.Y + (int)Math.Round(slide.Height * (index + 1) / (double)count);
            yield return new CropSegment(
                slideNumber,
                index + 1,
                new ImageSharpRectangle(slide.X, y0, slide.Width, Math.Max(0, y1 - y0)));
        }
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

    private sealed record CropSegment(int SlideNumber, int? FragmentNumber, ImageSharpRectangle Crop);
}
