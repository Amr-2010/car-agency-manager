# Debian-based images make native runtime dependencies predictable.
FROM mcr.microsoft.com/dotnet/sdk:10.0-bookworm-slim AS build
WORKDIR /src
COPY src/CarAgency.Api/CarAgency.Api.csproj src/CarAgency.Api/
RUN dotnet restore src/CarAgency.Api/CarAgency.Api.csproj
COPY src/CarAgency.Api/ src/CarAgency.Api/
RUN dotnet publish src/CarAgency.Api/CarAgency.Api.csproj -c Release -o /out --no-restore -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-bookworm-slim AS runtime
WORKDIR /app
COPY --from=build /out .
ENV ASPNETCORE_URLS=http://0.0.0.0:8080 \
    DOTNET_EnableDiagnostics=1 \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "CarAgency.Api.dll"]
