using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using Toolbox.Models;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Toolbox.Services;

using PdfSharpDocument = PdfSharpCore.Pdf.PdfDocument;
using PdfSharpPage = PdfSharpCore.Pdf.PdfPage;
using WinPdfDocument = Windows.Data.Pdf.PdfDocument;
using WinPdfPage = Windows.Data.Pdf.PdfPage;

public sealed class PdfToolboxService
{
    public Task<string> MergeAsync(
        IReadOnlyList<StorageFile> pdfFiles,
        string outputDirectory,
        string outputName,
        IProgress<ToolProgress> progress,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            RequireFiles(pdfFiles);
            Directory.CreateDirectory(outputDirectory);
            string outputPath = UniquePath(Path.Combine(outputDirectory, EnsurePdfExtension(outputName, "merged.pdf")));

            using var output = new PdfSharpDocument();
            int totalPages = CountPages(pdfFiles);
            int done = 0;

            foreach (StorageFile file in pdfFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var input = PdfReader.Open(file.Path, PdfDocumentOpenMode.Import);
                for (int i = 0; i < input.PageCount; i++)
                {
                    output.AddPage(input.Pages[i]);
                    done++;
                    progress.Report(new ToolProgress(Percent(done, totalPages), $"Merged {Path.GetFileName(file.Path)} page {i + 1}."));
                }
            }

            output.Save(outputPath);
            progress.Report(new ToolProgress(100, "PDF merge completed."));
            return outputPath;
        }, cancellationToken);
    }

    public Task<IReadOnlyList<string>> SplitEveryPageAsync(
        IReadOnlyList<StorageFile> pdfFiles,
        string outputDirectory,
        IProgress<ToolProgress> progress,
        CancellationToken cancellationToken = default)
    {
        return Task.Run<IReadOnlyList<string>>(() =>
        {
            RequireFiles(pdfFiles);
            Directory.CreateDirectory(outputDirectory);
            int totalPages = CountPages(pdfFiles);
            int done = 0;
            var outputs = new List<string>();

            foreach (StorageFile file in pdfFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var input = PdfReader.Open(file.Path, PdfDocumentOpenMode.Import);
                string baseName = Path.GetFileNameWithoutExtension(file.Path);

                for (int i = 0; i < input.PageCount; i++)
                {
                    using var output = new PdfSharpDocument();
                    output.AddPage(input.Pages[i]);
                    string outputPath = UniquePath(Path.Combine(outputDirectory, $"{baseName}_p{i + 1:000}.pdf"));
                    output.Save(outputPath);
                    outputs.Add(outputPath);

                    done++;
                    progress.Report(new ToolProgress(Percent(done, totalPages), $"Saved {Path.GetFileName(outputPath)}."));
                }
            }

            progress.Report(new ToolProgress(100, "PDF split completed."));
            return outputs;
        }, cancellationToken);
    }

    public Task<string> EditPagesAsync(
        StorageFile pdfFile,
        string outputDirectory,
        string outputName,
        string deletePagesText,
        string rotatePagesText,
        int rotateDegrees,
        IProgress<ToolProgress> progress,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            Directory.CreateDirectory(outputDirectory);
            string outputPath = UniquePath(Path.Combine(outputDirectory, EnsurePdfExtension(outputName, $"{Path.GetFileNameWithoutExtension(pdfFile.Path)}_edited.pdf")));

            using var input = PdfReader.Open(pdfFile.Path, PdfDocumentOpenMode.Import);
            var deletePages = ParsePageSet(deletePagesText, input.PageCount);
            var rotatePages = ParsePageSet(rotatePagesText, input.PageCount);
            using var output = new PdfSharpDocument();

            for (int i = 0; i < input.PageCount; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (deletePages.Contains(i))
                {
                    progress.Report(new ToolProgress(Percent(i + 1, input.PageCount), $"Deleted page {i + 1}."));
                    continue;
                }

                PdfSharpPage page = output.AddPage(input.Pages[i]);
                if (rotatePages.Contains(i))
                {
                    page.Rotate = NormalizeRotation(page.Rotate + rotateDegrees);
                }

                progress.Report(new ToolProgress(Percent(i + 1, input.PageCount), $"Processed page {i + 1}."));
            }

            output.Save(outputPath);
            progress.Report(new ToolProgress(100, "PDF page edit completed."));
            return outputPath;
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<string>> ExportPagesToImagesAsync(
        IReadOnlyList<StorageFile> pdfFiles,
        string outputDirectory,
        ImageOutputFormat format,
        int dpi,
        int quality,
        IProgress<ToolProgress> progress,
        CancellationToken cancellationToken = default)
    {
        RequireFiles(pdfFiles);
        Directory.CreateDirectory(outputDirectory);

        int totalPages = await CountPagesAsync(pdfFiles, cancellationToken);
        int done = 0;
        var outputs = new List<string>();

        foreach (StorageFile file in pdfFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WinPdfDocument document = await WinPdfDocument.LoadFromFileAsync(file);
            string baseName = Path.GetFileNameWithoutExtension(file.Path);

            for (uint i = 0; i < document.PageCount; i++)
            {
                using WinPdfPage page = document.GetPage(i);
                byte[] pngBytes = await RenderPageToPngBytesAsync(page, dpi, cancellationToken);
                string outputPath = UniquePath(Path.Combine(outputDirectory, $"{baseName}_p{i + 1:000}.{ImageExtension(format)}"));
                await SaveRenderedImageAsync(pngBytes, outputPath, format, quality, cancellationToken);
                outputs.Add(outputPath);

                done++;
                progress.Report(new ToolProgress(Percent(done, totalPages), $"Exported {Path.GetFileName(outputPath)}."));
            }
        }

        progress.Report(new ToolProgress(100, "PDF image export completed."));
        return outputs;
    }

    public async Task<IReadOnlyList<string>> CompressAsync(
        IReadOnlyList<StorageFile> pdfFiles,
        string outputDirectory,
        int dpi,
        int quality,
        IProgress<ToolProgress> progress,
        CancellationToken cancellationToken = default)
    {
        RequireFiles(pdfFiles);
        Directory.CreateDirectory(outputDirectory);

        int totalPages = await CountPagesAsync(pdfFiles, cancellationToken);
        int done = 0;
        var outputs = new List<string>();
        using TempDirectory temp = TempDirectory.Create();

        foreach (StorageFile file in pdfFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WinPdfDocument document = await WinPdfDocument.LoadFromFileAsync(file);
            using var output = new PdfSharpDocument();
            string baseName = Path.GetFileNameWithoutExtension(file.Path);
            string outputPath = UniquePath(Path.Combine(outputDirectory, $"{baseName}_compressed.pdf"));

            for (uint i = 0; i < document.PageCount; i++)
            {
                using WinPdfPage page = document.GetPage(i);
                byte[] pngBytes = await RenderPageToPngBytesAsync(page, dpi, cancellationToken);
                string tempImage = Path.Combine(temp.Path, $"{Guid.NewGuid():N}.jpg");
                await SaveRenderedImageAsync(pngBytes, tempImage, ImageOutputFormat.Jpg, quality, cancellationToken);

                using Image imageInfo = await Image.LoadAsync(tempImage, cancellationToken);
                using XImage pdfImage = XImage.FromFile(tempImage);
                PdfSharpPage pdfPage = output.AddPage();
                pdfPage.Width = XUnit.FromPoint(imageInfo.Width * 72.0 / dpi);
                pdfPage.Height = XUnit.FromPoint(imageInfo.Height * 72.0 / dpi);
                using XGraphics graphics = XGraphics.FromPdfPage(pdfPage);
                graphics.DrawImage(pdfImage, 0, 0, pdfPage.Width, pdfPage.Height);

                done++;
                progress.Report(new ToolProgress(Percent(done, totalPages), $"Compressed {baseName} page {i + 1}."));
            }

            output.Save(outputPath);
            outputs.Add(outputPath);
        }

        progress.Report(new ToolProgress(100, "PDF compression completed."));
        return outputs;
    }

    public static HashSet<int> ParsePageSet(string text, int pageCount)
    {
        var pages = new HashSet<int>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return pages;
        }

        foreach (string token in text.Split([',', ';', ' '], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = token.Split('-', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 1 && int.TryParse(parts[0], out int page))
            {
                AddPage(pages, page, pageCount);
            }
            else if (parts.Length == 2 && int.TryParse(parts[0], out int start) && int.TryParse(parts[1], out int end))
            {
                int first = Math.Min(start, end);
                int last = Math.Max(start, end);
                for (int pageNumber = first; pageNumber <= last; pageNumber++)
                {
                    AddPage(pages, pageNumber, pageCount);
                }
            }
        }

        return pages;
    }

    private static void AddPage(HashSet<int> pages, int pageNumber, int pageCount)
    {
        if (pageNumber >= 1 && pageNumber <= pageCount)
        {
            pages.Add(pageNumber - 1);
        }
    }

    private static int CountPages(IReadOnlyList<StorageFile> files)
    {
        int count = 0;
        foreach (StorageFile file in files)
        {
            using var input = PdfReader.Open(file.Path, PdfDocumentOpenMode.Import);
            count += input.PageCount;
        }

        return Math.Max(1, count);
    }

    private static async Task<int> CountPagesAsync(IReadOnlyList<StorageFile> files, CancellationToken cancellationToken)
    {
        int count = 0;
        foreach (StorageFile file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WinPdfDocument document = await WinPdfDocument.LoadFromFileAsync(file);
            count += (int)document.PageCount;
        }

        return Math.Max(1, count);
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

    private static async Task SaveRenderedImageAsync(
        byte[] pngBytes,
        string outputPath,
        ImageOutputFormat format,
        int quality,
        CancellationToken cancellationToken)
    {
        using Image image = Image.Load(pngBytes);
        switch (format)
        {
            case ImageOutputFormat.Jpg:
                await image.SaveAsJpegAsync(outputPath, new JpegEncoder { Quality = Math.Clamp(quality, 1, 100) }, cancellationToken);
                break;
            case ImageOutputFormat.Png:
                await image.SaveAsPngAsync(outputPath, new PngEncoder(), cancellationToken);
                break;
            default:
                await image.SaveAsJpegAsync(outputPath, new JpegEncoder { Quality = Math.Clamp(quality, 1, 100) }, cancellationToken);
                break;
        }
    }

    private static void RequireFiles(IReadOnlyList<StorageFile> files)
    {
        if (files.Count == 0)
        {
            throw new InvalidOperationException("Select at least one PDF file.");
        }
    }

    private static string EnsurePdfExtension(string value, string fallback)
    {
        string name = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        return name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ? name : name + ".pdf";
    }

    private static int NormalizeRotation(int degrees)
    {
        int normalized = degrees % 360;
        if (normalized < 0)
        {
            normalized += 360;
        }

        return normalized;
    }

    private static string ImageExtension(ImageOutputFormat format)
    {
        return format == ImageOutputFormat.Png ? "png" : "jpg";
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path))
        {
            return path;
        }

        string directory = Path.GetDirectoryName(path) ?? Environment.CurrentDirectory;
        string name = Path.GetFileNameWithoutExtension(path);
        string extension = Path.GetExtension(path);
        int counter = 2;
        string candidate;
        do
        {
            candidate = Path.Combine(directory, $"{name}_{counter}{extension}");
            counter++;
        }
        while (File.Exists(candidate));

        return candidate;
    }

    private static int Percent(int done, int total)
    {
        if (total <= 0)
        {
            return 0;
        }

        return Math.Clamp((int)Math.Round(done * 100.0 / total), 0, 100);
    }

    private sealed class TempDirectory : IDisposable
    {
        private TempDirectory(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static TempDirectory Create()
        {
            return new TempDirectory(Directory.CreateTempSubdirectory("toolbox-pdf-").FullName);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
