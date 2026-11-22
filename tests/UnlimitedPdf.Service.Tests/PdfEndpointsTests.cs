using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using UnlimitedPDF;

namespace UnlimitedPdf.Service.Tests;

public sealed class PdfEndpointsTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public PdfEndpointsTests(WebApplicationFactory<Program> factory) => _client = factory.CreateClient();

    [Fact]
    public async Task HealthAndPdfOperationsWork()
    {
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/health")).StatusCode);
        var document = await _client.PostAsJsonAsync("/api/pdf/documents", new
        {
            pages = new[]
            {
                new { blocks = new[] { new { type = "text", text = "first page", x = 50, y = 700 } } },
                new { blocks = new[] { new { type = "text", text = "second page", x = 50, y = 700 } } }
            }
        });
        var pdf = await AssertPdf(document);
        var html = await _client.PostAsJsonAsync("/api/pdf/html", new { html = "<h1>Hello</h1>", fileName = "hello.pdf" });
        await AssertPdf(html);
        Assert.True(html.Headers.Contains("X-Pdf-Warnings"));

        using var merge = new MultipartFormDataContent();
        merge.Add(new ByteArrayContent(pdf), "files", "one.pdf");
        merge.Add(new ByteArrayContent(pdf), "files", "two.pdf");
        var merged = await AssertPdf(await _client.PostAsync("/api/pdf/merge", merge));
        Assert.Equal(4, PdfFile.FromBytes(merged).PageCount);

        using var split = Form(pdf);
        split.Add(new StringContent("1"), "everyNPages");
        var splitResponse = await _client.PostAsync("/api/pdf/split", split);
        Assert.Equal(HttpStatusCode.OK, splitResponse.StatusCode);
        Assert.Equal("application/zip", splitResponse.Content.Headers.ContentType?.MediaType);
        await using var zipStream = await splitResponse.Content.ReadAsStreamAsync();
        using var zip = new ZipArchive(zipStream, ZipArchiveMode.Read);
        Assert.Equal(2, zip.Entries.Count);
        foreach (var entry in zip.Entries)
        {
            await using var stream = entry.Open();
            var header = new byte[5];
            await stream.ReadExactlyAsync(header);
            Assert.Equal("%PDF-"u8.ToArray(), header);
        }

        using var extract = Form(pdf);
        extract.Add(new StringContent("2,1"), "ranges");
        var extracted = await AssertPdf(await _client.PostAsync("/api/pdf/extract", extract));
        Assert.Equal(2, PdfFile.FromBytes(extracted).PageCount);
        using var remove = Form(pdf);
        remove.Add(new StringContent("1"), "ranges");
        var remaining = await AssertPdf(await _client.PostAsync("/api/pdf/remove", remove));
        Assert.Equal(1, PdfFile.FromBytes(remaining).PageCount);
    }

    [Fact]
    public async Task InvalidUploadsAndRangesAreRejected()
    {
        using var invalid = Form("not a pdf"u8.ToArray());
        invalid.Add(new StringContent("1"), "ranges");
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsync("/api/pdf/extract", invalid)).StatusCode);

        var response = await _client.PostAsJsonAsync("/api/pdf/documents", new
        {
            pages = new[] { new { blocks = Array.Empty<object>() } }
        });
        var pdf = await AssertPdf(response);
        using var badRange = Form(pdf);
        badRange.Add(new StringContent("7-"), "ranges");
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsync("/api/pdf/extract", badRange)).StatusCode);
    }

    [Fact]
    public async Task OversizeRequestIsRejected()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?> { ["PdfService:MaxUploadSize"] = "1024" })));
        using var client = factory.CreateClient();
        using var form = Form(new byte[2048]);
        form.Add(new StringContent("1"), "ranges");
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await client.PostAsync("/api/pdf/extract", form)).StatusCode);
    }

    [Fact]
    public async Task CrossSiteMultipartIsRejected()
    {
        using var form = Form("not a pdf"u8.ToArray());
        form.Add(new StringContent("1"), "ranges");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/pdf/extract") { Content = form };
        request.Headers.Add("Origin", "https://evil.example");
        request.Headers.Add("Sec-Fetch-Site", "cross-site");
        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.DoesNotContain("The PDF request or input is invalid", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task HtmlReportsBlockedRemoteResources()
    {
        var response = await _client.PostAsJsonAsync("/api/pdf/html",
            new { html = "<img src='https://example.invalid/image.png' alt='image'>" });
        await AssertPdf(response);
        Assert.NotEmpty(response.Headers.GetValues("X-Pdf-Warnings").Single());
    }

    [Fact]
    public async Task TablesCanContinueAcrossPagesAndRespectPageLimit()
    {
        var rows = Enumerable.Range(0, 100).Select(i => new[] { "Name " + i, "Value" }).ToArray();
        var payload = new
        {
            pages = new[] { new { blocks = new[] { new
            {
                type = "table", x = 50, y = 700, width = 300, rows,
                headerRowCount = 1, repeatHeaderRows = true
            } } } }
        };
        var pdf = await AssertPdf(await _client.PostAsJsonAsync("/api/pdf/documents", payload));
        Assert.True(PdfFile.FromBytes(pdf).PageCount > 1);
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?> { ["PdfService:MaxPagesPerDocument"] = "1" })));
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/pdf/documents", payload)).StatusCode);
    }

    [Fact]
    public async Task SplitRangesAndOpenApiAreAvailable()
    {
        var pdf = await AssertPdf(await _client.PostAsJsonAsync("/api/pdf/documents", new
        {
            pages = new[] { new { blocks = Array.Empty<object>() }, new { blocks = Array.Empty<object>() } }
        }));
        using var form = Form(pdf);
        form.Add(new StringContent("2,1"), "ranges");
        var split = await _client.PostAsync("/api/pdf/split", form);
        Assert.Equal(HttpStatusCode.OK, split.StatusCode);
        await using var zipStream = await split.Content.ReadAsStreamAsync();
        using var zip = new ZipArchive(zipStream, ZipArchiveMode.Read);
        Assert.Equal(2, zip.Entries.Count);
        var openApi = await _client.GetStringAsync("/openapi/v1.json");
        Assert.Contains("\"openapi\": \"3.2.0\"", openApi);
        Assert.Contains("\"everyNPages\"", openApi, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SplitOutputBudgetLimitsRepeatedRanges()
    {
        var pdf = await AssertPdf(await _client.PostAsJsonAsync("/api/pdf/documents", new
        {
            pages = new[] { new { blocks = Array.Empty<object>() } }
        }));
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?> { ["PdfService:MaxOutputSize"] = (pdf.Length * 3).ToString() })));
        using var client = factory.CreateClient();
        using var form = Form(pdf);
        form.Add(new StringContent(string.Join(",", Enumerable.Repeat("1", 20))), "ranges");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/pdf/split", form)).StatusCode);
    }

    [Fact]
    public async Task InvalidTableLayoutReturnsBadRequest()
    {
        var rows = Enumerable.Range(0, 101).Select(i => new[] { $"Row {i}" }).ToArray();
        var response = await _client.PostAsJsonAsync("/api/pdf/documents", new
        {
            pages = new[] { new { blocks = new[] { new
            {
                type = "table", x = 50, y = 700, width = 300, rows,
                headerRowCount = 100, repeatHeaderRows = true
            } } } }
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static MultipartFormDataContent Form(byte[] pdf)
    {
        var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(pdf), "file", "input.pdf");
        return form;
    }

    private static async Task<byte[]> AssertPdf(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);
        var data = await response.Content.ReadAsByteArrayAsync();
        Assert.True(data.AsSpan().StartsWith("%PDF-"u8));
        return data;
    }
}
