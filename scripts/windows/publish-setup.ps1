param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$OutputPath = "publish\windows-setup",
    [switch]$SelfContained
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$output = Join-Path $repoRoot $OutputPath

New-Item -ItemType Directory -Path $output -Force | Out-Null

$selfContainedValue = if ($SelfContained) { "true" } else { "false" }

Write-Host "Publishing AtmLogAgent.Service..." -ForegroundColor Cyan
dotnet publish (Join-Path $repoRoot "src\AtmLogAgent.Service\AtmLogAgent.Service.csproj") `
    -c $Configuration `
    -r $Runtime `
    --self-contained $selfContainedValue `
    -p:EnableWindowsTargeting=true `
    -o $output

Write-Host "Publishing AtmLogAgent.SetupWizard..." -ForegroundColor Cyan
dotnet publish (Join-Path $repoRoot "src\AtmLogAgent.SetupWizard\AtmLogAgent.SetupWizard.csproj") `
    -c $Configuration `
    -r $Runtime `
    --self-contained $selfContainedValue `
    -p:EnableWindowsTargeting=true `
    -o $output

Write-Host ""
Write-Host "Windows setup bundle ready:" -ForegroundColor Green
Write-Host "  $output"
Write-Host ""
Write-Host "Run as Administrator on the ATM:"
Write-Host "  AtmLogAgent.SetupWizard.exe"
