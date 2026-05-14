# Dockerfile

# ── Stage 1: build ───────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0-bookworm AS build
WORKDIR /src
COPY . .
RUN dotnet publish SaddleRAG.Mcp/SaddleRAG.Mcp.csproj \
        -c Release \
        -r linux-x64 \
        --self-contained true \
        -p:UseGpu=false \
        -p:TreatWarningsAsErrors=true \
        -o /app/publish

# ── Stage 2: runtime ─────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0-bookworm-slim AS runtime

# Playwright / Chromium system dependencies + PowerShell (for playwright.ps1)
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        curl \
        wget \
        gnupg \
        apt-transport-https \
        libatk1.0-0 \
        libatk-bridge2.0-0 \
        libcups2 \
        libdrm2 \
        libgbm1 \
        libgtk-3-0 \
        libnspr4 \
        libnss3 \
        libxcomposite1 \
        libxdamage1 \
        libxfixes3 \
        libxkbcommon0 \
        libxrandr2 \
        libasound2 \
        xvfb \
        fonts-liberation \
    && wget -q https://packages.microsoft.com/config/debian/12/packages-microsoft-prod.deb -O /tmp/packages-microsoft-prod.deb \
    && dpkg -i /tmp/packages-microsoft-prod.deb \
    && rm /tmp/packages-microsoft-prod.deb \
    && apt-get update \
    && apt-get install -y --no-install-recommends powershell \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app/publish .

# Install Playwright browsers using the PowerShell script shipped with publish output
RUN pwsh playwright.ps1 install chromium \
    && rm -rf /var/lib/apt/lists/*

EXPOSE 6100

HEALTHCHECK --interval=30s --timeout=10s --start-period=300s --retries=5 \
    CMD curl -sf http://localhost:6100/health || exit 1

ENTRYPOINT ["./SaddleRAG.Mcp"]
