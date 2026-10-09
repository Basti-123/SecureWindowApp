param(
    [switch]$SkipPublish,
    [string]$OutputDirectory = "publish"
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$output = [IO.Path]::GetFullPath((Join-Path $root $OutputDirectory))
$project = [xml](Get-Content -LiteralPath (Join-Path $root "SecureWindowApp.csproj") -Raw)
$version = [string]$project.Project.PropertyGroup.Version
$runtime = Get-Content -LiteralPath (Join-Path $PSScriptRoot "webview2-runtime.json") -Raw | ConvertFrom-Json
$archive = Join-Path $root "Veyra-$version-win-x64-portable.zip"

if (-not $SkipPublish) {
    & dotnet publish (Join-Path $root "SecureWindowApp.csproj") -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true -o $output
    if ($LASTEXITCODE -ne 0) { throw "Der Windows-Build ist fehlgeschlagen." }
}
if (-not (Test-Path -LiteralPath (Join-Path $output "Veyra.exe"))) { throw "Veyra.exe fehlt im Publish-Ordner." }

$temporary = Join-Path ([IO.Path]::GetTempPath()) ("VeyraPortable-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $temporary | Out-Null
try {
    $cab = Join-Path $temporary "WebView2.cab"
    Invoke-WebRequest -Uri $runtime.url -OutFile $cab
    if ((Get-FileHash -LiteralPath $cab -Algorithm SHA256).Hash.ToLowerInvariant() -ne $runtime.sha256) {
        throw "Die SHA-256-Pruefung der Microsoft-WebView2-Laufzeit ist fehlgeschlagen."
    }
    $unpacked = Join-Path $temporary "unpacked"
    New-Item -ItemType Directory -Path $unpacked | Out-Null
    & "$env:SystemRoot\System32\expand.exe" $cab '-F:*' $unpacked | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Die WebView2-Laufzeit konnte nicht entpackt werden." }
    $runtimeExe = @(Get-ChildItem -LiteralPath $unpacked -Filter msedgewebview2.exe -Recurse)
    if ($runtimeExe.Count -ne 1) { throw "Die entpackte WebView2-Laufzeit ist unvollstaendig." }
    $runtimeFolder = Join-Path $output "WebView2Runtime"
    if (Test-Path -LiteralPath $runtimeFolder) { Remove-Item -LiteralPath $runtimeFolder -Recurse -Force }
    New-Item -ItemType Directory -Path $runtimeFolder | Out-Null
    Get-ChildItem -LiteralPath $runtimeExe[0].Directory.FullName -Force | Copy-Item -Destination $runtimeFolder -Recurse -Force
    Copy-Item -LiteralPath (Join-Path $root "PORTABLE.txt") -Destination (Join-Path $output "README.txt") -Force

    # Only the app, its runtime, and instructions belong in the download.
    Compress-Archive -LiteralPath @((Join-Path $output "Veyra.exe"), (Join-Path $output "README.txt"), $runtimeFolder) -DestinationPath $archive -CompressionLevel Optimal -Force
    (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant() | Set-Content -Path "$archive.sha256" -Encoding ascii
    Write-Host "Portables Paket erstellt: $archive"
}
finally { Remove-Item -LiteralPath $temporary -Recurse -Force }
