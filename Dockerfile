# Default Render deployment: ASP.NET Core API
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/CarAgency.Api/CarAgency.Api.csproj src/CarAgency.Api/
RUN dotnet restore src/CarAgency.Api/CarAgency.Api.csproj
COPY src/CarAgency.Api/ src/CarAgency.Api/
RUN dotnet publish src/CarAgency.Api/CarAgency.Api.csproj -c Release -o /out --no-restore
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /out .
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "CarAgency.Api.dll"]
