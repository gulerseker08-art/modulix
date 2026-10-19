# Modulix - Gorev 2: veritabani ve migration altyapisi (bir kez calistirilir)
#
# Once zip icerigini repo kokune kopyalayin (Program.cs'in uzerine yazilmasini onaylayin), sonra:
#   powershell -ExecutionPolicy Bypass -File scripts\gorev2-apply.ps1
#
# Yaptiklari:
#   1. NuGet paketlerini projelere ekler (surumler .NET 10 ile uyumlu en guncel yamaya sabitlenir).
#   2. Sablondan kalan ornek dosyayi siler.
#   3. dotnet-ef aracini repoya yerel arac olarak kurar.
#   4. Derler ve ilk migration'i (Baseline) olusturur.
#   5. scripts\db-up.ps1 ile veritabanini kurar ve migration'i uygular.
#   6. Mimari ve entegrasyon testlerini calistirir.

$ErrorActionPreference = 'Stop'
Set-Location (Split-Path -Parent $PSScriptRoot)
$env:DOTNET_NOLOGO = '1'

$EfVersion = '10.0.*'

function Invoke-Checked {
    param([string] $Exe, [string[]] $Arguments)
    & $Exe @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Komut basarisiz oldu: $Exe $($Arguments -join ' ')"
    }
}

function Write-Step([string] $text) {
    Write-Host ''
    Write-Host "== $text ==" -ForegroundColor Cyan
}

function Add-PackageReference {
    param([string] $Project, [string] $Id, [string] $Version, [switch] $DevelopmentOnly)

    $path = (Resolve-Path $Project).Path
    [xml] $xml = Get-Content -Raw -Path $path

    foreach ($node in $xml.SelectNodes('//PackageReference')) {
        if ($node.GetAttribute('Include') -eq $Id) {
            Write-Host "  $Id zaten var: $Project"
            return
        }
    }

    $group = $xml.CreateElement('ItemGroup')
    $reference = $xml.CreateElement('PackageReference')
    $reference.SetAttribute('Include', $Id)
    $reference.SetAttribute('Version', $Version)
    if ($DevelopmentOnly) {
        # Yalnizca gelistirme araci; yayinlanan uygulamaya ve diger projelere gecmez.
        $reference.SetAttribute('PrivateAssets', 'all')
    }
    [void] $group.AppendChild($reference)
    [void] $xml.DocumentElement.AppendChild($group)
    $xml.Save($path)
    Write-Host "  + $Id $Version -> $Project"
}

# --- On kontroller ------------------------------------------------------------

if (-not ((Test-Path 'Modulix.slnx') -or (Test-Path 'Modulix.sln'))) {
    throw 'Solution bulunamadi. Betigi repo kokunden calistirin (Gorev 1 tamamlanmis olmali).'
}
if (-not (Test-Path 'src/Modulix.Infrastructure/Persistence/ModulixDbContext.cs')) {
    throw 'Gorev 2 dosyalari bulunamadi. Zip icerigini repo kokune kopyaladiginizdan emin olun.'
}

$sdk = & dotnet --version | Select-Object -Last 1
if ($LASTEXITCODE -ne 0 -or -not ("$sdk" -like '10.*')) {
    throw ".NET 10 SDK bulunamadi (bulunan: '$sdk')."
}

# --- 1/6 Paketler -------------------------------------------------------------

Write-Step '1/6 NuGet paketleri'
Add-PackageReference 'src/Modulix.Infrastructure/Modulix.Infrastructure.csproj' 'Microsoft.EntityFrameworkCore.SqlServer' $EfVersion
# SqlClient'in getirdigi 9.0.x surumunde bilinen guvenlik aciklari var (GHSA-846f-f764-76mx, GHSA-2fmv-rv2c-59x7);
# yamali 10.0.x surumu dogrudan eklenir.
Add-PackageReference 'src/Modulix.Infrastructure/Modulix.Infrastructure.csproj' 'System.Security.Cryptography.Pkcs' $EfVersion
Add-PackageReference 'src/Modulix.Api/Modulix.Api.csproj' 'Microsoft.EntityFrameworkCore.Design' $EfVersion -DevelopmentOnly
Add-PackageReference 'src/Modulix.Api/Modulix.Api.csproj' 'Microsoft.Extensions.Diagnostics.HealthChecks.EntityFrameworkCore' $EfVersion
Add-PackageReference 'tests/Modulix.IntegrationTests/Modulix.IntegrationTests.csproj' 'Microsoft.Extensions.Configuration.UserSecrets' $EfVersion
Add-PackageReference 'tests/Modulix.IntegrationTests/Modulix.IntegrationTests.csproj' 'Microsoft.Extensions.Configuration.EnvironmentVariables' $EfVersion

# --- 2/6 Sablon ornegi ---------------------------------------------------------

Write-Step '2/6 Sablondan kalan ornek dosya'
if (Test-Path 'src/Modulix.Api/Modulix.Api.http') {
    Remove-Item 'src/Modulix.Api/Modulix.Api.http'
    Write-Host '  - src/Modulix.Api/Modulix.Api.http silindi (silinen weatherforecast ornegine aitti)'
}

# --- 3/6 dotnet-ef -------------------------------------------------------------

Write-Step '3/6 dotnet-ef yerel araci'
# .NET surumune gore arac listesi .config/ altinda veya repo kokunde olabilir.
function Find-ToolManifest {
    foreach ($candidate in @('.config/dotnet-tools.json', 'dotnet-tools.json')) {
        if (Test-Path $candidate) { return $candidate }
    }
    return $null
}

$manifest = Find-ToolManifest
if (-not $manifest) {
    Invoke-Checked dotnet @('new', 'tool-manifest')
    $manifest = Find-ToolManifest
    if (-not $manifest) { throw 'Arac listesi (dotnet-tools.json) olusturulamadi.' }
}
Write-Host "  Arac listesi: $manifest"
if (-not (Select-String -Path $manifest -Pattern '"dotnet-ef"' -Quiet)) {
    Invoke-Checked dotnet @('tool', 'install', 'dotnet-ef', '--version', $EfVersion)
}
Invoke-Checked dotnet @('tool', 'restore')

# --- 4/6 Derleme ve ilk migration ---------------------------------------------

Write-Step '4/6 Derleme ve ilk migration (Baseline)'
Invoke-Checked dotnet @('build')

if (-not (Test-Path 'src/Modulix.Infrastructure/Persistence/Migrations')) {
    Invoke-Checked dotnet @(
        'ef', 'migrations', 'add', 'Baseline',
        '--project', 'src/Modulix.Infrastructure',
        '--startup-project', 'src/Modulix.Api',
        '--output-dir', 'Persistence/Migrations')
} else {
    Write-Host '  Migrations klasoru zaten var; atlandi.'
}

# --- 5/6 Veritabani ------------------------------------------------------------

Write-Step '5/6 Veritabani (scripts\db-up.ps1)'
& (Join-Path $PSScriptRoot 'db-up.ps1')
Set-Location (Split-Path -Parent $PSScriptRoot)

# --- 6/6 Testler ---------------------------------------------------------------

Write-Step '6/6 Testler'
Invoke-Checked dotnet @('build')
Invoke-Checked dotnet @('test', 'tests/Modulix.ArchitectureTests/Modulix.ArchitectureTests.csproj', '--no-build')
Invoke-Checked dotnet @('test', 'tests/Modulix.IntegrationTests/Modulix.IntegrationTests.csproj', '--no-build')

Write-Host ''
Write-Host 'Tamam. Son 20 satiri Claude ile paylasin.' -ForegroundColor Green
