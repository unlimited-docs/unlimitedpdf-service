namespace UnlimitedPdf.Service.Options;

public sealed class PdfServiceOptions
{
    public long MaxUploadSize { get; set; } = 50 * 1024 * 1024;
    public long MaxOutputSize { get; set; } = 50 * 1024 * 1024;
    public int MaxFilesPerMerge { get; set; } = 20;
    public int MaxPagesPerDocument { get; set; } = 500;
    public int RequestTimeoutSeconds { get; set; } = 120;
}
