#!/usr/bin/env bash
# Télécharge les packages NuGet un par un avec retry (réseau instable)
set -euo pipefail

PKG_DIR="/home/sevan/Documents/Projects/ME/AtmLogAgent/local-packages"
mkdir -p "$PKG_DIR"

download() {
    local id="$1" ver="$2"
    local lower_id=$(echo "$id" | tr '[:upper:]' '[:lower:]')
    local url="https://api.nuget.org/v3-flatcontainer/${lower_id}/${ver}/${lower_id}.${ver}.nupkg"
    local dest="${PKG_DIR}/${lower_id}.${ver}.nupkg"
    
    if [ -f "$dest" ]; then
        echo "  ✓ ${id} ${ver} (already cached)"
        return 0
    fi
    
    echo "  ↓ ${id} ${ver}..."
    curl -4 -fSL --retry 5 --retry-delay 3 --retry-max-time 120 \
         --connect-timeout 15 --max-time 120 \
         -o "$dest" "$url" 2>&1
    echo "  ✓ ${id} ${ver} ($(du -h "$dest" | cut -f1))"
}

echo "=== Downloading NuGet packages ==="

# Core direct dependencies
download "Microsoft.Extensions.Logging.Abstractions" "8.0.0"
download "Microsoft.Extensions.Options" "8.0.0"
download "SSH.NET" "2024.1.0"
download "Polly" "8.3.1"
download "Polly.Extensions.Http" "3.0.0"
download "Polly.Core" "8.3.1"
download "Microsoft.Data.Sqlite" "8.0.0"
download "Microsoft.Data.Sqlite.Core" "8.0.0"
download "Newtonsoft.Json" "13.0.3"
download "Serilog" "4.0.0"
download "Serilog.Extensions.Logging" "8.0.0"
download "Serilog.Sinks.File" "5.0.0"

# Service direct dependencies
download "Microsoft.Extensions.Hosting" "8.0.0"
download "Microsoft.Extensions.Hosting.WindowsServices" "8.0.0"
download "Microsoft.Extensions.Hosting.Systemd" "8.0.0"
download "Serilog.Extensions.Hosting" "8.0.0"
download "Serilog.Sinks.Console" "5.0.0"
download "Serilog.Enrichers.Thread" "3.1.0"
download "Serilog.Enrichers.Environment" "2.3.0"

# Transitive dependencies (common)
download "Microsoft.Extensions.Configuration" "8.0.0"
download "Microsoft.Extensions.Configuration.Abstractions" "8.0.0"
download "Microsoft.Extensions.Configuration.Binder" "8.0.1"
download "Microsoft.Extensions.Configuration.CommandLine" "8.0.0"
download "Microsoft.Extensions.Configuration.EnvironmentVariables" "8.0.0"
download "Microsoft.Extensions.Configuration.FileExtensions" "8.0.0"
download "Microsoft.Extensions.Configuration.Json" "8.0.0"
download "Microsoft.Extensions.Configuration.UserSecrets" "8.0.0"
download "Microsoft.Extensions.DependencyInjection" "8.0.0"
download "Microsoft.Extensions.DependencyInjection.Abstractions" "8.0.0"
download "Microsoft.Extensions.Diagnostics" "8.0.0"
download "Microsoft.Extensions.Diagnostics.Abstractions" "8.0.0"
download "Microsoft.Extensions.FileProviders.Abstractions" "8.0.0"
download "Microsoft.Extensions.FileProviders.Physical" "8.0.0"
download "Microsoft.Extensions.FileSystemGlobbing" "8.0.0"
download "Microsoft.Extensions.Hosting.Abstractions" "8.0.0"
download "Microsoft.Extensions.Http" "8.0.0"
download "Microsoft.Extensions.Logging" "8.0.0"
download "Microsoft.Extensions.Logging.Configuration" "8.0.0"
download "Microsoft.Extensions.Logging.Console" "8.0.0"
download "Microsoft.Extensions.Logging.Debug" "8.0.0"
download "Microsoft.Extensions.Logging.EventLog" "8.0.0"
download "Microsoft.Extensions.Logging.EventSource" "8.0.0"
download "Microsoft.Extensions.Options.ConfigurationExtensions" "8.0.0"
download "Microsoft.Extensions.Primitives" "8.0.0"
download "SQLitePCLRaw.bundle_e_sqlite3" "2.1.6"
download "SQLitePCLRaw.core" "2.1.6"
download "SQLitePCLRaw.lib.e_sqlite3" "2.1.6"
download "SQLitePCLRaw.provider.e_sqlite3" "2.1.6"
download "System.Diagnostics.EventLog" "8.0.0"
download "System.ServiceProcess.ServiceController" "8.0.0"
download "SshNet.Security.Cryptography" "1.3.1"

echo ""
echo "=== Done: $(ls "$PKG_DIR"/*.nupkg 2>/dev/null | wc -l) packages downloaded ==="
