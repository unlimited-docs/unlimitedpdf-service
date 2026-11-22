using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace UnlimitedPdf.Service.Contracts;

public sealed record DocumentRequest
{
    [Required, MinLength(1)]
    public required PageRequest[] Pages { get; init; }
    [Range(0, 300)] public double MarginTop { get; init; } = 36;
    [Range(0, 300)] public double MarginBottom { get; init; } = 36;
    [Range(0, 300)] public double MarginLeft { get; init; } = 36;
    [Range(0, 300)] public double MarginRight { get; init; } = 36;
    public string? Header { get; init; }
    public string? Footer { get; init; }
}

public sealed record PageRequest
{
    [Required] public required ContentBlock[] Blocks { get; init; }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TextBlock), "text")]
[JsonDerivedType(typeof(TableBlock), "table")]
[JsonDerivedType(typeof(ImageBlock), "image")]
public closed record ContentBlock;

public sealed record TextBlock : ContentBlock
{
    [Required] public required string Text { get; init; }
    public double X { get; init; }
    public double Y { get; init; }
    [Range(1, 200)] public double FontSize { get; init; } = 12;
}

public sealed record TableBlock : ContentBlock
{
    public double X { get; init; }
    public double Y { get; init; }
    [Range(1, 1000)] public double Width { get; init; }
    [Required, MinLength(1)] public required string[][] Rows { get; init; }
    [Range(0, 1000)] public int HeaderRowCount { get; init; } = 1;
    public bool RepeatHeaderRows { get; init; } = true;
}

public sealed record ImageBlock : ContentBlock
{
    [Required] public required string Base64 { get; init; }
    public double X { get; init; }
    public double Y { get; init; }
    [Range(1, 2000)] public double Width { get; init; }
    [Range(1, 2000)] public double Height { get; init; }
}

public sealed record HtmlRequest
{
    [Required] public required string Html { get; init; }
    public string? FileName { get; init; }
}
