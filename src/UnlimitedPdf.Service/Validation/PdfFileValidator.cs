namespace UnlimitedPdf.Service.Validation;

public sealed class PdfFileValidator
{
    public async Task<bool> IsPdfAsync(IFormFile file, CancellationToken cancellationToken)
    {
        await using var stream = file.OpenReadStream();
        byte[] header = new byte[5];
        return await stream.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, cancellationToken) == header.Length
            && header.AsSpan().SequenceEqual("%PDF-"u8);
    }
}
