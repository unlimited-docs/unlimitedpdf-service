using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.Extensions.Options;
using UnlimitedPdf.Service.Endpoints;
using UnlimitedPdf.Service.Options;
using UnlimitedPdf.Service.Services;
using UnlimitedPdf.Service.Validation;

var builder = WebApplication.CreateBuilder(args);
builder.Services.Configure<PdfServiceOptions>(builder.Configuration.GetSection("PdfService"));
builder.WebHost.ConfigureKestrel((context, o) =>
    o.Limits.MaxRequestBodySize = (context.Configuration.GetSection("PdfService").Get<PdfServiceOptions>() ?? new()).MaxUploadSize);
builder.Services.AddOptions<FormOptions>().Configure<IOptions<PdfServiceOptions>>((o, limits) =>
    o.MultipartBodyLengthLimit = limits.Value.MaxUploadSize);
builder.Services.AddRequestTimeouts();
builder.Services.AddOptions<RequestTimeoutOptions>().Configure<IOptions<PdfServiceOptions>>((o, limits) =>
{
    o.DefaultPolicy = new RequestTimeoutPolicy
    {
        Timeout = TimeSpan.FromSeconds(limits.Value.RequestTimeoutSeconds),
        TimeoutStatusCode = StatusCodes.Status504GatewayTimeout
    };
});
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("pdf", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 60, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
builder.Services.AddResponseCompression(o =>
{
    o.EnableForHttps = true;
    o.MimeTypes = ["application/json", "application/problem+json"];
});
builder.Services.AddProblemDetails();
builder.Services.AddValidation();
builder.Services.AddOpenApi();
builder.Services.AddSingleton<PdfFileValidator>();
builder.Services.AddScoped<IPdfService, PdfService>();

var app = builder.Build();
app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
{
    var error = context.Features.Get<IExceptionHandlerFeature>()?.Error;
    var status = error switch
    {
        BadHttpRequestException bad => bad.StatusCode,
        FormatException => StatusCodes.Status400BadRequest,
        InvalidDataException => StatusCodes.Status400BadRequest,
        _ => StatusCodes.Status500InternalServerError
    };
    await Results.Problem(statusCode: status).ExecuteAsync(context);
}));
app.UseResponseCompression();
app.UseRequestTimeouts();
app.UseRateLimiter();
app.Use(async (context, next) =>
{
    var limits = context.RequestServices.GetRequiredService<IOptions<PdfServiceOptions>>();
    if (context.Request.ContentLength > limits.Value.MaxUploadSize)
    {
        await Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge).ExecuteAsync(context);
        return;
    }
    await next(context);
});
app.MapOpenApi();
app.MapGet("/health", () => Results.Ok()).WithSummary("Liveness check").WithDescription("Checks that the service is running.")
    .Produces(StatusCodes.Status200OK).ProducesProblem(500).ShortCircuit();
app.MapPdfEndpoints();
app.Run();

public partial class Program;
