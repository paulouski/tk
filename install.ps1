# tk install script — downloads the latest release binary
# Run: irm https://raw.githubusercontent.com/paulouski/tk/main/install.ps1 | iex

$ErrorActionPreference = "Stop"

$installDir = Join-Path $env:LOCALAPPDATA "tk"
$exe = Join-Path $installDir "tk.exe"
$downloadUrl = "https://github.com/paulouski/tk/releases/latest/download/tk.exe"

# --- Check for .NET 8 Runtime ---
Write-Host "Checking for .NET 8 Runtime..." -ForegroundColor Cyan

$hasRuntime = $false
try {
    $runtimes = & dotnet --list-runtimes 2>&1
    $hasRuntime = $runtimes | Select-String "Microsoft\.NETCore\.App 8\." | Measure-Object | Select-Object -ExpandProperty Count
    $hasRuntime = $hasRuntime -gt 0
} catch {
    $hasRuntime = $false
}

if (-not $hasRuntime) {
    Write-Host ".NET 8 Runtime not found." -ForegroundColor Yellow
    Write-Host "Attempting to install via winget..." -ForegroundColor Cyan
    try {
        & winget install --id Microsoft.DotNet.Runtime.8 -e --source winget
        if ($LASTEXITCODE -ne 0) { throw "winget exited with code $LASTEXITCODE" }
        Write-Host ".NET 8 Runtime installed." -ForegroundColor Green
    } catch {
        Write-Host "Could not install automatically. Please install .NET 8 Runtime manually:" -ForegroundColor Red
        Write-Host "  https://dotnet.microsoft.com/download/dotnet/8" -ForegroundColor Yellow
        exit 1
    }
}

# --- Download binary ---
Write-Host "Downloading tk..." -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path $installDir | Out-Null
Invoke-WebRequest -Uri $downloadUrl -OutFile $exe -UseBasicParsing
Write-Host "Downloaded to $exe" -ForegroundColor Green

# --- Add to PATH if not already there ---
$userPath = [Environment]::GetEnvironmentVariable("Path", "User")
if ($userPath -notlike "*$installDir*") {
    [Environment]::SetEnvironmentVariable("Path", "$userPath;$installDir", "User")
    Write-Host "Added $installDir to user PATH." -ForegroundColor Green
    Write-Host "Restart your terminal for PATH changes to take effect." -ForegroundColor Yellow
} else {
    Write-Host "PATH already contains $installDir" -ForegroundColor Green
}

# --- Install global Claude and AGENTS instructions ---
& $exe init

Write-Host ""
Write-Host "Done. Run 'tk --help' to verify." -ForegroundColor Green
