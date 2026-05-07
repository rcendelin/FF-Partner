FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
EXPOSE 8080

# curl je nutný pro HEALTHCHECK — base image ho ve výchozím stavu neobsahuje
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

# Nepouštět jako root
RUN addgroup --system --gid 1001 bridge && \
    adduser --system --uid 1001 --ingroup bridge bridge
USER bridge

# SDK 9.x je nutný kvůli .slnx formátu i global.json. Runtime image (aspnet:8.0)
# zůstává — TFM projektu je net8.0.
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

COPY ["src/Bridge.Api/Bridge.Api.csproj", "src/Bridge.Api/"]
COPY ["src/Bridge.Application/Bridge.Application.csproj", "src/Bridge.Application/"]
COPY ["src/Bridge.Domain/Bridge.Domain.csproj", "src/Bridge.Domain/"]
COPY ["src/Bridge.Infrastructure/Bridge.Infrastructure.csproj", "src/Bridge.Infrastructure/"]
RUN dotnet restore "src/Bridge.Api/Bridge.Api.csproj"

COPY . .
WORKDIR "/src/src/Bridge.Api"
RUN dotnet build "Bridge.Api.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "Bridge.Api.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .

HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
    CMD curl --silent --fail http://localhost:8080/health || exit 1

ENTRYPOINT ["dotnet", "Bridge.Api.dll"]
