FROM mcr.microsoft.com/dotnet/sdk:11.0 AS build
WORKDIR /source
COPY Directory.Build.props Directory.Packages.props UnlimitedPdf.Service.slnx ./
COPY src/UnlimitedPdf.Service/UnlimitedPdf.Service.csproj src/UnlimitedPdf.Service/
COPY tests/UnlimitedPdf.Service.Tests/UnlimitedPdf.Service.Tests.csproj tests/UnlimitedPdf.Service.Tests/
RUN dotnet restore src/UnlimitedPdf.Service/UnlimitedPdf.Service.csproj
COPY src/UnlimitedPdf.Service/ src/UnlimitedPdf.Service/
RUN dotnet publish src/UnlimitedPdf.Service/UnlimitedPdf.Service.csproj -c Release --no-restore -o /app

FROM mcr.microsoft.com/dotnet/aspnet:11.0 AS runtime
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "UnlimitedPdf.Service.dll"]
