# =============================================================================
#  Gera o instalador do Klyra Optimizer
#  Uso:  powershell -ExecutionPolicy Bypass -File .\Installer\build.ps1
#        -SelfContained  -> publica com o .NET embutido
# =============================================================================
param(
    [switch]$SelfContained,
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "KlyraOptimizer.csproj"
$publish = Join-Path $root "publish"
$iss = Join-Path $PSScriptRoot "setup.iss"
$output = Join-Path $PSScriptRoot "Output"

Write-Host "==> Limpando publicacao anterior..." -ForegroundColor Cyan
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
if (Test-Path $output) { Remove-Item $output -Recurse -Force }

$bin = Join-Path $root "bin\$Configuration"
$obj = Join-Path $root "obj\$Configuration"
if (Test-Path $bin) { Remove-Item $bin -Recurse -Force }
if (Test-Path $obj) { Remove-Item $obj -Recurse -Force }

Write-Host "==> Publicando Klyra Optimizer ($Configuration)..." -ForegroundColor Cyan
$publishArgs = @(
    $project,
    "-c", $Configuration,
    "-r", "win-x64",
    "--self-contained", $(if ($SelfContained) { "true" } else { "false" }),
    "-p:PublishSingleFile=false",
    "-p:DebugType=none",
    "-o", $publish
)

& dotnet publish @publishArgs
if ($LASTEXITCODE -ne 0) { throw "Falha ao publicar o aplicativo." }

$exe = Join-Path $publish "KlyraOptimizer.exe"
if (-not (Test-Path $exe)) { throw "KlyraOptimizer.exe nao foi gerado em $publish." }

Write-Host "==> Testando o executavel publicado (smoke)..." -ForegroundColor Cyan
$smoke = Join-Path $env:TEMP "klyraoptimizer-smoke-build.txt"
if (Test-Path $smoke) { Remove-Item $smoke -Force }

$proc = Start-Process -FilePath $exe `
    -ArgumentList "--smoke", "--no-elevate", "--out", $smoke `
    -PassThru -Wait
if ($proc.ExitCode -ne 0) { throw "O executavel publicado falhou ao abrir a interface (exit $($proc.ExitCode))." }

$report = @()
if (Test-Path $smoke) { $report = Get-Content $smoke }
if (-not ($report -match "^\s*OK:").Count -or ($report -match "FALHA")) {
    throw "O executavel publicado nao passou no teste de telas. Relatorio: $($report -join ' | ')"
}
Write-Host ("    telas OK: " + (($report | Where-Object { $_ -match '^OK:' }) -join ', ')) -ForegroundColor DarkGray

Write-Host "==> Localizando o Inno Setup (ISCC)..." -ForegroundColor Cyan
$candidates = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 7\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "C:\Program Files (x86)\Inno Setup 7\ISCC.exe",
    "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
    "C:\Program Files\Inno Setup 7\ISCC.exe",
    "C:\Program Files\Inno Setup 6\ISCC.exe"
)

$iscc = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) {
    throw "Inno Setup nao encontrado. Instale com: winget install -e --id JRSoftware.InnoSetup"
}

Write-Host "==> Compilando instalador com $iscc" -ForegroundColor Cyan
$isccArgs = @($iss)
if ($SelfContained) { $isccArgs += "/DSelfContainedBuild=1" }

& $iscc @isccArgs
if ($LASTEXITCODE -ne 0) { throw "Falha ao compilar o instalador (ISCC)." }

$setup = Get-ChildItem $output -Filter "KlyraOptimizer-Setup-*.exe" | Select-Object -First 1
if (-not $setup) { throw "Instalador nao encontrado em $output." }

Write-Host ""
Write-Host "Instalador gerado:" -ForegroundColor Green
Write-Host ("  {0}  ({1:N1} MB)" -f $setup.FullName, ($setup.Length / 1MB)) -ForegroundColor Green
