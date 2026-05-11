# Multi-stage build for the SetGame .NET 10 server. Serves both the
# /api/* endpoints and the SPA assets that live under server/wwwroot/.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first for layer caching
COPY server/SetGameServer.csproj server/
RUN dotnet restore server/SetGameServer.csproj

# Build & publish (wwwroot is included automatically by Microsoft.NET.Sdk.Web)
COPY server/ server/
RUN dotnet publish server/SetGameServer.csproj -c Release -o /app/publish /p:UseAppHost=false

# Runtime image
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

ARG CONTAINER_APP_ENV_DOMAIN
ARG MSRHUB_ENV_DOMAIN
ARG GIT_COMMIT_HASH
ENV CONTAINER_APP_ENV_DOMAIN=$CONTAINER_APP_ENV_DOMAIN
ENV MSRHUB_ENV_DOMAIN=$MSRHUB_ENV_DOMAIN
ENV GIT_COMMIT_HASH=$GIT_COMMIT_HASH
ENV ASPNETCORE_URLS=http://+:8080

WORKDIR /app
COPY --from=build /app/publish ./

EXPOSE 8080

ENTRYPOINT ["dotnet", "SetGameServer.dll"]
