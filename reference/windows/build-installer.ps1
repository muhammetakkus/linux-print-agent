# Publish + installer'i tek adimda uretir; surum numarasini Constants.cs'ten okur.
# Boylece installer asla bayat bir publish klasoruyle veya yanlis surum etiketiyle paketlenmez.
#
# Authenticode imzalama: -SignSubject (sertifika sahibi adi) ya da -SignThumbprint
# verilirse hem uygulama exe'si hem installer imzalanir. Ikisi de yoksa imzalama
# atlanir ve build eskisi gibi calisir -> sertifika gelene kadar bir sey bozulmaz.
#   .\build-installer.ps1 -SignSubject "TE BILISIM LTD STI"
# Sertifika Windows sertifika deposunda gorunmelidir (USB token surucusu veya
# bulut HSM saglayicisi kurulunca gorunur).
param(
    [string]$SignSubject = $env:NUXPRINT_SIGN_SUBJECT,
    [string]$SignThumbprint = $env:NUXPRINT_SIGN_THUMBPRINT,
    [string]$TimestampUrl = $(if ($env:NUXPRINT_TIMESTAMP_URL) { $env:NUXPRINT_TIMESTAMP_URL } else { "http://timestamp.digicert.com" })
)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

$signingEnabled = $SignSubject -or $SignThumbprint

# signtool.exe Windows SDK ile gelir; PATH'te yoksa Windows Kits altinda en yeni
# surumu bulunur.
function Get-SignTool {
    $cmd = (Get-Command signtool.exe -ErrorAction SilentlyContinue).Source
    if ($cmd) { return $cmd }
    $candidates = Get-ChildItem -Path @(
        "${env:ProgramFiles(x86)}\Windows Kits\10\bin",
        "${env:ProgramFiles}\Windows Kits\10\bin"
    ) -Filter signtool.exe -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.DirectoryName -match 'x64' } |
        Sort-Object FullName -Descending
    if (-not $candidates) { throw "signtool.exe bulunamadi - Windows SDK kurulu mu?" }
    return $candidates[0].FullName
}

# Zaman damgasi sart: damgasiz imza, sertifika suresi dolunca gecersiz olur ve
# sahadaki eski installer'lar bir anda 'Unknown Publisher'a doner.
function Invoke-CodeSign {
    param([string]$Path)
    if (-not $signingEnabled) { return }
    $signtool = Get-SignTool
    $signArgs = @("sign", "/fd", "SHA256", "/tr", $TimestampUrl, "/td", "SHA256")
    if ($SignThumbprint) { $signArgs += @("/sha1", $SignThumbprint) } else { $signArgs += @("/n", $SignSubject) }
    $signArgs += $Path

    Write-Host "Signing $Path" -ForegroundColor Cyan
    & $signtool @signArgs
    if ($LASTEXITCODE -ne 0) { throw "imzalama basarisiz: $Path" }

    & $signtool verify /pa /q $Path
    if ($LASTEXITCODE -ne 0) { throw "imza dogrulanamadi: $Path" }
}

$constants = Get-Content "src/PrintAgent.Common/Constants.cs" -Raw
if ($constants -notmatch 'AppVersion = "([^"]+)"') { throw "AppVersion Constants.cs icinde bulunamadi" }
$version = $Matches[1]
Write-Host "Building NuxPrint Agent v$version" -ForegroundColor Cyan

if (Test-Path "publish/ui") { Remove-Item "publish/ui" -Recurse -Force }
dotnet publish src/PrintAgent.UI -c Release -o publish/ui
if ($LASTEXITCODE -ne 0) { throw "dotnet publish basarisiz" }

# Bundle the pinned official portable helper; never download/execute helpers at runtime.
$helperZip = Join-Path ([System.IO.Path]::GetTempPath()) ('nuxprint-sumatra-' + [guid]::NewGuid() + '.zip')
try {
    Invoke-WebRequest 'https://www.sumatrapdfreader.org/dl/rel/3.6.1/SumatraPDF-3.6.1-64.zip' -OutFile $helperZip
    if ((Get-FileHash $helperZip -Algorithm SHA256).Hash -ne '98B33A518D42986856D225064B0CD2D3643ECF78CBF84AB873D26CC51877A544') { throw 'SumatraPDF archive hash mismatch' }
    $tools = New-Item -ItemType Directory -Path 'publish/ui/tools' -Force
    $archive = [System.IO.Compression.ZipFile]::OpenRead($helperZip)
    try {
        $entry = $archive.GetEntry('SumatraPDF-3.6.1-64.exe')
        if (-not $entry) { throw 'SumatraPDF executable missing' }
        [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $tools.FullName 'SumatraPDF.exe'), $true)
    } finally { $archive.Dispose() }
    (Get-FileHash (Join-Path $tools.FullName 'SumatraPDF.exe') -Algorithm SHA256).Hash | Set-Content (Join-Path $tools.FullName 'SumatraPDF.sha256') -Encoding ascii
} finally { if (Test-Path -LiteralPath $helperZip) { Remove-Item -LiteralPath $helperZip } }

# Unsigned development packages cannot use automatic updates. Signed releases pin their expected signer.
if ($signingEnabled) {
    $certificates = @(Get-ChildItem Cert:\CurrentUser\My, Cert:\LocalMachine\My | Where-Object { if ($SignThumbprint) { $_.Thumbprint -eq $SignThumbprint } else { $_.Subject -like "*$SignSubject*" } })
    if ($certificates.Count -ne 1) { throw 'Select exactly one update signing certificate with -SignThumbprint' }
    $certificates[0].GetCertHashString([System.Security.Cryptography.HashAlgorithmName]::SHA256) | Set-Content 'publish/ui/trusted-update-signer.sha256' -Encoding ascii
}

# iscc PATH'te yoksa bilinen kurulum yollarinda ara
$iscc = (Get-Command iscc -ErrorAction SilentlyContinue).Source
if (-not $iscc) {
    $iscc = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles}\Inno Setup 6\ISCC.exe",
        "${env:LocalAppData}\Programs\Inno Setup 6\ISCC.exe"
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $iscc) { throw "ISCC.exe bulunamadi - Inno Setup 6 kurulu mu?" }

# Once uygulama exe'si: installer'in icine imzali girmeli. Inno paketledikten
# sonra imzalamak imkansiz - dosya arsivin icinde kalir.
if ($signingEnabled) {
    Get-ChildItem "publish/ui" -Filter *.exe | ForEach-Object { Invoke-CodeSign $_.FullName }
} else {
    Write-Host "Imzalama atlandi (-SignSubject/-SignThumbprint verilmedi) - cikti 'Unknown Publisher' gorunecek" -ForegroundColor Yellow
}

& $iscc /DMyAppVersion=$version setup.iss
if ($LASTEXITCODE -ne 0) { throw "iscc basarisiz" }

# Sonra installer'in kendisi: kullanicinin cift tikladigi ve UAC'nin yayinci
# adini okudugu dosya budur.
Invoke-CodeSign "installer/NuxPrintAgent-Setup-$version.exe"

Write-Host "OK -> installer/NuxPrintAgent-Setup-$version.exe" -ForegroundColor Green

