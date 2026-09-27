# ================= Stage 1: Build =================
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY *.csproj ./
RUN dotnet restore

COPY . ./
RUN dotnet publish -c Release -o /app/publish

# ================= Stage 2: Runtime =================
FROM mcr.microsoft.com/dotnet/runtime:8.0 AS final
WORKDIR /app

# Install PowerShell (required to run Playwright's install script) and Xvfb (virtual display)
RUN apt-get update \
    && apt-get install -y --no-install-recommends wget apt-transport-https ca-certificates gnupg \
    && wget -q https://packages.microsoft.com/config/debian/12/packages-microsoft-prod.deb -O packages-microsoft-prod.deb \
    && dpkg -i packages-microsoft-prod.deb \
    && rm packages-microsoft-prod.deb \
    && apt-get update \
    && apt-get install -y --no-install-recommends powershell xvfb xauth \
    && rm -rf /var/lib/apt/lists/*

# Copy the published app (includes playwright.ps1 matching the project's Playwright version)
COPY --from=build /app/publish .

# Install Chromium + all its OS-level dependencies
# (force the full Chromium build, not the headless-shell-only build,
# since we run in non-headless mode under Xvfb)
ENV PLAYWRIGHT_CHROMIUM_USE_HEADLESS_SHELL=0
RUN pwsh playwright.ps1 install --with-deps chromium

# Run the app under a virtual display so Chrome runs in normal
# (non-headless) mode, which the target site doesn't block
ENTRYPOINT ["xvfb-run", "--auto-servernum", "--server-args=-screen 0 1920x1080x24", "dotnet", "test.dll"]