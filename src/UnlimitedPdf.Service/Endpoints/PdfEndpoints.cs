using Microsoft.AspNetCore.Antiforgery;
using UnlimitedPdf.Service.Contracts;
using UnlimitedPdf.Service.Services;

namespace UnlimitedPdf.Service.Endpoints;

public static class PdfEndpoints
{
    extension(IEndpointRouteBuilder endpoints)
    {
        public RouteGroupBuilder MapPdfEndpoints()
        {
            var group = endpoints.MapGroup("/api/pdf").RequireRateLimiting("pdf");
            group.MapPost("/documents", async (DocumentRequest request, IPdfService service, HttpContext context) =>
                    Result(await service.CreateAsync(request, context.RequestAborted), context))
                .WithSummary("Create a PDF").WithDescription("Generate a PDF from pages and text, table or image blocks.")
                .Produces(StatusCodes.Status200OK, contentType: "application/pdf").ProducesProblem(400).ProducesProblem(500);
            group.MapPost("/html", async (HtmlRequest request, IPdfService service, HttpContext context) =>
                    Result(await service.HtmlAsync(request, context.RequestAborted), context))
                .WithSummary("Convert HTML").WithDescription("Convert HTML to PDF; remote resources are blocked and warnings are returned in X-Pdf-Warnings.")
                .Produces(StatusCodes.Status200OK, contentType: "application/pdf").ProducesProblem(400).ProducesProblem(500);
            group.MapPost("/merge", async (HttpContext context, IPdfService service) =>
            {
                if (!IsCsrfValid(context)) return Results.Problem(statusCode: 403);
                var form = await context.Request.ReadFormAsync(context.RequestAborted);
                return Result(await service.MergeAsync(form.Files.GetFiles("files"), context.RequestAborted), context);
            }).WithMetadata(new RequireAntiforgeryTokenAttribute(true))
                .Accepts<IFormFileCollection>("multipart/form-data")
                .WithSummary("Merge PDFs").WithDescription("Merge 2 to 20 files in upload order.")
                .Produces(StatusCodes.Status200OK, contentType: "application/pdf").ProducesProblem(400).ProducesProblem(500);
            group.MapPost("/split", async (HttpContext context, IPdfService service) =>
            {
                if (!IsCsrfValid(context)) return Results.Problem(statusCode: 403);
                var form = await context.Request.ReadFormAsync(context.RequestAborted);
                if (form.Files.Count != 1 || form.Files.GetFile("file") is not { } file)
                    return Results.Problem(statusCode: 400, detail: "Provide one file.");
                var raw = form["everyNPages"].ToString();
                if (raw.Length > 0 && !int.TryParse(raw, out _))
                    return Results.Problem(statusCode: 400, detail: "everyNPages must be an integer.");
                int? every = raw.Length == 0 ? null : int.Parse(raw);
                return Result(await service.SplitAsync(file, every, form["ranges"].ToString(), context.RequestAborted), context);
            }).WithMetadata(new RequireAntiforgeryTokenAttribute(true))
                .Accepts<IFormFile>("multipart/form-data")
                .WithSummary("Split a PDF").WithDescription("Split by page count or ranges into a ZIP archive.")
                .Produces(StatusCodes.Status200OK, contentType: "application/zip").ProducesProblem(400).ProducesProblem(500);
            MapRanges(group, "/extract", false);
            MapRanges(group, "/remove", true);
            return group;
        }
    }

    private static void MapRanges(RouteGroupBuilder group, string route, bool remove)
    {
        group.MapPost(route, async (HttpContext context, IPdfService service) =>
        {
            if (!IsCsrfValid(context)) return Results.Problem(statusCode: 403);
            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            if (form.Files.Count != 1 || form.Files.GetFile("file") is not { } file)
                return Results.Problem(statusCode: 400, detail: "Provide one file.");
            return Result(await service.ExtractAsync(file, form["ranges"].ToString(), remove, context.RequestAborted), context);
        }).WithMetadata(new RequireAntiforgeryTokenAttribute(true))
            .Accepts<IFormFile>("multipart/form-data")
            .WithSummary(remove ? "Remove pages" : "Extract pages")
            .WithDescription(remove ? "Remove the specified page ranges." : "Extract the specified page ranges in order.")
            .Produces(StatusCodes.Status200OK, contentType: "application/pdf").ProducesProblem(400).ProducesProblem(500);
    }

    private static bool IsCsrfValid(HttpContext context) =>
        context.Features.Get<IAntiforgeryValidationFeature>() is { IsValid: true };

    private static IResult Result(PdfOutcome outcome, HttpContext context) => outcome switch
    {
        PdfResult result => File(result, context),
        ValidationError error => Results.Problem(statusCode: 400, detail: error.Message),
        ProcessingError => Results.Problem(statusCode: 500)
    };

    private static IResult File(PdfResult result, HttpContext context)
    {
        if (result.Warnings is not null) context.Response.Headers["X-Pdf-Warnings"] = result.Warnings;
        return Results.File(result.Content, result.ContentType, result.FileName);
    }
}
