using System.ComponentModel.DataAnnotations;
using System.IO.Compression;
using Microsoft.Extensions.Options;
using UnlimitedPDF;
using UnlimitedPdf.Service.Contracts;
using UnlimitedPdf.Service.Options;
using UnlimitedPdf.Service.Validation;

namespace UnlimitedPdf.Service.Services;

public sealed partial class PdfService(
    IOptions<PdfServiceOptions> options,
    PdfFileValidator validator,
    ILogger<PdfService> logger) : IPdfService
{
    private readonly PdfServiceOptions _limits = options.Value;

    public Task<PdfOutcome> CreateAsync(DocumentRequest request, CancellationToken cancellationToken) =>
        ExecuteAsync(async () =>
        {
            Validate(request);
            if (request.Pages.Length > _limits.MaxPagesPerDocument)
                throw new ArgumentException("The document has too many pages.");

            var document = new PdfDocument
            {
                MarginTop = request.MarginTop, MarginBottom = request.MarginBottom,
                MarginLeft = request.MarginLeft, MarginRight = request.MarginRight
            };
            document.Body.MaxPages = _limits.MaxPagesPerDocument;
            if (request.Header is not null)
            {
                document.Header.Height = 20;
                document.Header.AddText(request.Header, 0, 0);
            }
            if (request.Footer is not null)
            {
                document.Footer.Height = 20;
                document.Footer.AddText(request.Footer, 0, 0);
            }
            foreach (var pageRequest in request.Pages)
            {
                if (pageRequest is null) throw new ArgumentException("Pages cannot contain null.");
                Validate(pageRequest);
                var page = document.AddPage();
                foreach (var block in pageRequest.Blocks)
                {
                    if (block is null) throw new ArgumentException("Blocks cannot contain null.");
                    Validate(block);
                    await (block switch
                    {
                        TextBlock text => AddTextAsync(page, text),
                        TableBlock table => AddTableAsync(page, table),
                        ImageBlock image => AddImageAsync(page, image, cancellationToken)
                    });
                }
            }
            await using var output = new LimitedMemoryStream(_limits.MaxOutputSize);
            try { await document.SaveAsync(output, cancellationToken); }
            catch (InvalidOperationException) { throw new ArgumentException("The document layout is invalid or exceeds the page limit."); }
            await CheckOutputPagesAsync(output, cancellationToken);
            return new PdfResult(output.ToArray(), "application/pdf", "document.pdf");
        });

    public Task<PdfOutcome> HtmlAsync(HtmlRequest request, CancellationToken cancellationToken) =>
        ExecuteAsync(async () =>
        {
            Validate(request);
            var html = PdfHtml.FromString(request.Html, new PdfHtmlLoadOptions { ResourceResolver = _ => null });
            await using var output = new LimitedMemoryStream(_limits.MaxOutputSize);
            try { await PdfOperations.ConvertHtmlAsync(html, output, cancellationToken: cancellationToken); }
            catch (InvalidOperationException) { throw new ArgumentException("The HTML layout is invalid."); }
            await CheckOutputPagesAsync(output, cancellationToken);
            var warnings = string.Join(", ", html.Warnings.Select(w => w.Kind.ToString()).Distinct());
            return new PdfResult(output.ToArray(), "application/pdf", SafeFileName(request.FileName), warnings);
        });

    public Task<PdfOutcome> MergeAsync(IReadOnlyList<IFormFile> files, CancellationToken cancellationToken) =>
        ExecuteAsync(async () =>
        {
            if (files.Count < 2 || files.Count > Math.Min(20, _limits.MaxFilesPerMerge))
                throw new ArgumentException("Provide between 2 and the configured maximum number of files.");
            var inputs = new List<PdfFile>();
            var pages = 0;
            foreach (var file in files)
            {
                var input = await LoadAsync(file, cancellationToken);
                pages += input.PageCount;
                if (pages > _limits.MaxPagesPerDocument) throw new ArgumentException("The merged document has too many pages.");
                inputs.Add(input);
            }
            await using var output = new LimitedMemoryStream(_limits.MaxOutputSize);
            await PdfOperations.MergeAsync(output, inputs, cancellationToken);
            return new PdfResult(output.ToArray(), "application/pdf", "merged.pdf");
        });

    public Task<PdfOutcome> SplitAsync(IFormFile file, int? everyNPages, string? ranges, CancellationToken cancellationToken) =>
        ExecuteAsync(async () =>
        {
            if ((everyNPages is null) == string.IsNullOrWhiteSpace(ranges) || everyNPages is <= 0)
                throw new ArgumentException("Provide either a positive everyNPages or ranges.");
            var input = await LoadAsync(file, cancellationToken);
            var parsed = string.IsNullOrWhiteSpace(ranges) ? null : ParseRanges(ranges, input.PageCount);
            await using var output = new LimitedMemoryStream(_limits.MaxOutputSize);
            var budget = new OutputBudget(_limits.MaxOutputSize);
            using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            {
                Stream? entry = null;
                try
                {
                    Stream Open(PdfSplitPart part)
                    {
                        entry?.Dispose();
                        return entry = new BudgetStream(zip.CreateEntry(part.FileName).Open(), budget);
                    }
                    if (parsed is not null)
                        await PdfOperations.SplitAsync(input, parsed, Open, cancellationToken);
                    else
                        await PdfOperations.SplitAsync(input, everyNPages!.Value, Open, cancellationToken);
                }
                finally { if (entry is not null) await entry.DisposeAsync(); }
            }
            return new PdfResult(output.ToArray(), "application/zip", "split.zip");
        });

    public Task<PdfOutcome> ExtractAsync(IFormFile file, string ranges, bool remove, CancellationToken cancellationToken) =>
        ExecuteAsync(async () =>
        {
            var input = await LoadAsync(file, cancellationToken);
            var parsed = ParseRanges(ranges, input.PageCount);
            await using var output = new LimitedMemoryStream(_limits.MaxOutputSize);
            if (remove)
                await PdfOperations.RemoveAsync(input, parsed, output, cancellationToken);
            else
                await PdfOperations.ExtractAsync(input, parsed, output, cancellationToken);
            return new PdfResult(output.ToArray(), "application/pdf", remove ? "remaining.pdf" : "extracted.pdf");
        });

    private async Task<PdfFile> LoadAsync(IFormFile file, CancellationToken cancellationToken)
    {
        if (file.Length > _limits.MaxUploadSize) throw new ArgumentException("File exceeds the upload limit.");
        if (!await validator.IsPdfAsync(file, cancellationToken)) throw new ArgumentException("The file is not a PDF.");
        await using var input = file.OpenReadStream();
        var pdf = await PdfFile.FromStreamAsync(input, cancellationToken: cancellationToken);
        if (pdf.PageCount > _limits.MaxPagesPerDocument) throw new ArgumentException("The PDF has too many pages.");
        return pdf;
    }

    private IReadOnlyList<PdfPageRange> ParseRanges(string ranges, int pageCount)
    {
        var parsed = PdfPageRange.Parse(ranges);
        if (parsed.Any(r => r.LastPage > pageCount) ||
            parsed.Sum(r => (long)r.LastPage - r.FirstPage + 1) > _limits.MaxPagesPerDocument)
            throw new ArgumentException("Page ranges exceed the document or configured page limit.");
        return parsed;
    }

    private async Task CheckOutputPagesAsync(MemoryStream output, CancellationToken cancellationToken)
    {
        output.Position = 0;
        var result = await PdfFile.FromStreamAsync(output, cancellationToken: cancellationToken);
        if (result.PageCount > _limits.MaxPagesPerDocument) throw new ArgumentException("The generated PDF has too many pages.");
    }

    private static void Validate(object value)
    {
        var results = new List<ValidationResult>();
        if (!Validator.TryValidateObject(value, new ValidationContext(value), results, validateAllProperties: true))
            throw new ArgumentException("The request contains invalid values.");
    }

    private static Task AddTextAsync(PdfPage page, TextBlock block)
    {
        page.AddText(block.Text, block.X, block.Y, block.FontSize);
        return Task.CompletedTask;
    }

    private static Task AddTableAsync(PdfPage page, TableBlock block)
    {
        if (block.Rows.Any(r => r is null || r.Length == 0 || r.Any(c => c is null)) ||
            block.HeaderRowCount > block.Rows.Length || block.Rows.Any(r => r.Length != block.Rows[0].Length))
            throw new ArgumentException("Table rows and headers are invalid.");
        var table = page.AddTable(block.X, block.Y, block.Width);
        table.SetColumnWidths(Enumerable.Repeat(block.Width / block.Rows[0].Length, block.Rows[0].Length).ToArray());
        table.HeaderRowCount = block.HeaderRowCount;
        table.RepeatHeaderRows = block.RepeatHeaderRows;
        for (var row = 0; row < block.Rows.Length; row++)
            for (var column = 0; column < block.Rows[row].Length; column++)
                table.GetOrCreateCell(row, column).Text = block.Rows[row][column];
        return Task.CompletedTask;
    }

    private static async Task AddImageAsync(PdfPage page, ImageBlock block, CancellationToken cancellationToken)
    {
        await using var stream = new MemoryStream(Convert.FromBase64String(block.Base64));
        var image = await PdfImage.FromStreamAsync(stream, cancellationToken: cancellationToken);
        page.AddImage(image, block.X, block.Y, block.Width, block.Height);
    }

    private static string SafeFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "document.pdf";
        var clean = Path.GetFileName(name.Replace('\\', '/'));
        return clean.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ? clean : clean + ".pdf";
    }

    private async Task<PdfOutcome> ExecuteAsync(Func<Task<PdfResult>> operation)
    {
        try { return await operation(); }
        catch (Exception ex) when (ex is ArgumentException or FormatException or PdfFileException or PdfImageException or PdfHtmlException || HasOutputLimitError(ex))
        {
            LogInvalid(logger, ex.GetType().Name);
            return new ValidationError("The PDF request or input is invalid.");
        }

        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            LogFailed(logger, ex.GetType().Name);
            return new ProcessingError();
        }
    }

    private static bool HasOutputLimitError(Exception exception)
    {
        for (Exception? error = exception; error is not null; error = error.InnerException)
            if (error is OutputLimitException) return true;
        return false;
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Invalid PDF request: {ErrorType}")]
    private static partial void LogInvalid(ILogger logger, string errorType);

    [LoggerMessage(EventId = 2, Level = LogLevel.Error, Message = "PDF processing failed: {ErrorType}")]
    private static partial void LogFailed(ILogger logger, string errorType);
}
