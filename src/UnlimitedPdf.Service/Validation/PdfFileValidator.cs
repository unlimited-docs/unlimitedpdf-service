using System.ComponentModel.DataAnnotations;

namespace UnlimitedPdf.Service.Validation;

public sealed class PdfFileValidator
{
    private static readonly PdfHeaderAttribute Header = new();

    public async Task<bool> IsPdfAsync(IFormFile file, CancellationToken cancellationToken) =>
        await Header.GetValidationResultAsync(file, new ValidationContext(file), cancellationToken) == ValidationResult.Success;
}

public sealed class PdfHeaderAttribute : AsyncValidationAttribute
{
    protected override ValidationResult? IsValid(object? value, ValidationContext validationContext) =>
        throw new NotSupportedException("PDF header validation must run asynchronously.");

    protected override async Task<ValidationResult?> IsValidAsync(
        object? value, ValidationContext validationContext, CancellationToken cancellationToken)
    {
        if (value is not IFormFile file) return new ValidationResult("A PDF file is required.");
        await using var stream = file.OpenReadStream();
        byte[] header = new byte[5];
        var valid = await stream.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, cancellationToken) == header.Length
            && header.AsSpan().SequenceEqual("%PDF-"u8);
        return valid ? ValidationResult.Success : new ValidationResult("The file is not a PDF.");
    }
}
