using UnlimitedPdf.Service.Contracts;

namespace UnlimitedPdf.Service.Services;

public closed record PdfOutcome;
public sealed record PdfResult(byte[] Content, string ContentType, string FileName, string? Warnings = null) : PdfOutcome;
public sealed record ValidationError(string Message) : PdfOutcome;
public sealed record ProcessingError : PdfOutcome;

public interface IPdfService
{
    Task<PdfOutcome> CreateAsync(DocumentRequest request, CancellationToken cancellationToken);
    Task<PdfOutcome> HtmlAsync(HtmlRequest request, CancellationToken cancellationToken);
    Task<PdfOutcome> MergeAsync(IReadOnlyList<IFormFile> files, CancellationToken cancellationToken);
    Task<PdfOutcome> SplitAsync(IFormFile file, int? everyNPages, string? ranges, CancellationToken cancellationToken);
    Task<PdfOutcome> ExtractAsync(IFormFile file, string ranges, bool remove, CancellationToken cancellationToken);
}
