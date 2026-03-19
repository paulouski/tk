# tk install script
# Run: powershell -ExecutionPolicy Bypass -File install.ps1

$ErrorActionPreference = "Stop"

$installDir = Join-Path $env:LOCALAPPDATA "tk"
$exe = Join-Path $installDir "tk.exe"

Write-Host "Building tk..." -ForegroundColor Cyan
dotnet publish -c Release -o $installDir --nologo -v quiet
if ($LASTEXITCODE -ne 0) { Write-Host "Build failed." -ForegroundColor Red; exit 1 }

# Add to PATH if not already there
$userPath = [Environment]::GetEnvironmentVariable("Path", "User")
if ($userPath -notlike "*$installDir*") {
    [Environment]::SetEnvironmentVariable("Path", "$userPath;$installDir", "User")
    Write-Host "Added $installDir to user PATH." -ForegroundColor Green
    Write-Host "Restart your terminal for PATH changes to take effect." -ForegroundColor Yellow
} else {
    Write-Host "PATH already contains $installDir" -ForegroundColor Green
}

# Install Claude Code instructions
& $exe init

Write-Host ""
Write-Host "Done. Run 'tk --help' to verify." -ForegroundColor Green
