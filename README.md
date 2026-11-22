# UnlimitedPdf.Service

A .NET 11 Minimal API for creating and processing PDFs with UnlimitedPDF 1.x.

## Run

Install the .NET 11 SDK, then:

```sh
dotnet build UnlimitedPdf.Service.sln
dotnet test UnlimitedPdf.Service.sln
dotnet run --project src/UnlimitedPdf.Service --urls http://localhost:5114
```

OpenAPI 3.2 is available at `/openapi/v1.json`. For Docker:

```sh
docker build -t unlimitedpdf-service .
docker run --rm -p 8080:8080 unlimitedpdf-service
```

## Endpoints

| Method | Path | Input | Output |
|---|---|---|---|
| GET | `/health` | none | 200 |
| POST | `/api/pdf/documents` | JSON pages and blocks | PDF |
| POST | `/api/pdf/html` | JSON `html`, optional `fileName` | PDF |
| POST | `/api/pdf/merge` | multipart `files` (2–20, in order) | PDF |
| POST | `/api/pdf/split` | multipart `file`, `everyNPages` **or** `ranges` | ZIP |
| POST | `/api/pdf/extract` | multipart `file`, `ranges` | PDF |
| POST | `/api/pdf/remove` | multipart `file`, `ranges` | PDF |

Coordinates and dimensions are PDF points, with the origin at the bottom left. Text blocks have
`type: "text"`, `text`, `x`, `y`, and optional `fontSize`; image blocks have `type: "image"`,
base64-encoded PNG/JPEG in `base64`, `x`, `y`, `width`, and `height`. Table blocks have
`type: "table"`, `x`, `y`, `width`, rectangular `rows`, `headerRowCount`, and
`repeatHeaderRows`. Tables may continue onto additional pages. Document margins and `header` and
`footer` text are optional. The header and footer support `{page}` and `{pages}` placeholders.

Page ranges are 1-based and inclusive; e.g. `10, 1-3` extracts page 10 followed by pages 1–3.
HTML conversion blocks remote resources and returns warning kinds in the `X-Pdf-Warnings` header.
Data URI resources remain supported; no local base folder is configured.

```sh
curl -sS -X POST http://localhost:5114/api/pdf/documents \
  -H 'Content-Type: application/json' \
  -d '{"pages":[{"blocks":[{"type":"text","text":"Hello","x":50,"y":700}]}]}' -o first.pdf
curl -sS -X POST http://localhost:5114/api/pdf/html \
  -H 'Content-Type: application/json' -d '{"html":"<h1>Hello</h1>","fileName":"hello.pdf"}' -o second.pdf
curl -sS -X POST http://localhost:5114/api/pdf/merge \
  -F files=@first.pdf -F files=@second.pdf -o merged.pdf
curl -sS -X POST http://localhost:5114/api/pdf/split \
  -F file=@merged.pdf -F everyNPages=1 -o parts.zip
curl -sS -X POST http://localhost:5114/api/pdf/extract \
  -F file=@merged.pdf -F 'ranges=2,1' -o extracted.pdf
curl -sS -X POST http://localhost:5114/api/pdf/remove \
  -F file=@merged.pdf -F ranges=1 -o remaining.pdf
curl -i http://localhost:5114/health
```

Multipart endpoints opt into .NET 11 cross-origin CSRF protection. Non-browser clients such as
curl can omit `Origin` and Fetch Metadata headers; browser requests must be same-origin. This
service does not configure cross-origin browser access or authentication.

The `PdfService` configuration section controls `MaxUploadSize` (50 MiB total request body),
`MaxFilesPerMerge` (20, capped at 20), `MaxPagesPerDocument` (500), and
`RequestTimeoutSeconds` (120). Environment variables can override these, e.g.
`PdfService__MaxUploadSize=10485760`. A fixed per-IP limit allows 60 PDF requests per minute.
Deploy behind a trusted proxy configured to forward client addresses if per-client limiting is
required behind a load balancer. PDF and ZIP responses are not response-compressed.

See `src/UnlimitedPdf.Service/UnlimitedPdf.Service.http` for HTTP client samples.
