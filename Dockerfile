# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0.401 AS build
WORKDIR /source
COPY Directory.Build.props ./
COPY src/Clarvio.Mail/Clarvio.Mail.csproj src/Clarvio.Mail/packages.lock.json src/Clarvio.Mail/
RUN dotnet restore src/Clarvio.Mail/Clarvio.Mail.csproj --locked-mode
COPY src/Clarvio.Mail/ src/Clarvio.Mail/
RUN dotnet publish src/Clarvio.Mail/Clarvio.Mail.csproj --configuration Release --no-restore --output /publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0.12 AS runtime
WORKDIR /app
RUN apt-get update && apt-get install -y --no-install-recommends libgssapi-krb5-2 && rm -rf /var/lib/apt/lists/*
ENV ASPNETCORE_ENVIRONMENT=Production ASPNETCORE_HTTP_PORTS=8740 DOTNET_EnableDiagnostics=0
COPY --from=build --chown=app:app /publish/ ./
USER $APP_UID
EXPOSE 8740
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 CMD ["dotnet", "Clarvio.Mail.dll", "--healthcheck"]
ENTRYPOINT ["dotnet", "Clarvio.Mail.dll"]
