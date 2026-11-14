# Modulix - yerel veritabanini baslatir ve gunceller.
# Her gun, bilgisayar yeniden basladiktan sonra veya git pull sonrasinda guvenle calistirilabilir:
#   powershell -ExecutionPolicy Bypass -File scripts\db-up.ps1
#
# Yaptiklari:
#   1. .env yoksa olusturur ve sifreleri rastgele uretir (.env git'e gitmez).
#   2. Docker'da SQL Server'i ve yerel test posta kutusunu (Mailpit, http://localhost:8025) baslatir.
#   3. Veritabanini, semalari ve iki kullaniciyi kurar (docker/sqlserver/init.sql).
#   4. Baglanti cumlelerini user-secrets'a yazar (kod deposuna sifre girmez).
#   5. Bekleyen migration'lari migration kullanicisiyla uygular.
#   6. Ornek tenant ve kullanicilari olusturur (varsa dokunmaz).

$ErrorActionPreference = 'Stop'
Set-Location (Split-Path -Parent $PSScriptRoot)
$env:DOTNET_NOLOGO = '1'

$ApiProject    = 'src/Modulix.Api/Modulix.Api.csproj'
$UserSecretsId = 'modulix-api'
$PasswordKeys  = @('MSSQL_SA_PASSWORD', 'MODULIX_APP_PASSWORD', 'MODULIX_MIGRATOR_PASSWORD')

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

function New-Password {
    # Buyuk harf, kucuk harf, rakam ve sembol icerir; $ ve # icermez.
    'Mx!' + [guid]::NewGuid().ToString('N').Substring(0, 24)
}

function Read-EnvFile([string] $path) {
    $values = @{}
    if (-not (Test-Path $path)) { return $values }
    foreach ($line in Get-Content -Path $path) {
        $text = "$line".Trim()
        if ($text.Length -eq 0 -or $text.StartsWith('#')) { continue }
        $separator = $text.IndexOf('=')
        if ($separator -lt 1) { continue }
        $values[$text.Substring(0, $separator).Trim()] = $text.Substring($separator + 1).Trim()
    }
    return $values
}

# --- 1/6 Docker ve .env --------------------------------------------------------

Write-Step '1/6 Docker ve .env kontrolu'

if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
    throw 'docker komutu bulunamadi. Docker Desktop kurulu mu?'
}

$previousPreference = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
docker info *> $null
$dockerRunning = ($LASTEXITCODE -eq 0)
$ErrorActionPreference = $previousPreference
if (-not $dockerRunning) {
    throw 'Docker calismiyor. Docker Desktop''i acin, "Engine running" yazisini bekleyin ve tekrar deneyin.'
}

# .env yoksa olusturur; varsa yalnizca eksik veya bos degerleri tamamlar, mevcut sifrelere dokunmaz.
$envValues = Read-EnvFile '.env'
$envChanged = -not (Test-Path '.env')

if ([string]::IsNullOrWhiteSpace($envValues['MSSQL_PORT'])) {
    $envValues['MSSQL_PORT'] = '14333'
    $envChanged = $true
}
foreach ($key in $PasswordKeys) {
    if ([string]::IsNullOrWhiteSpace($envValues[$key])) {
        $envValues[$key] = New-Password
        $envChanged = $true
        Write-Host "  $key uretildi."
    }
}

if ($envChanged) {
    $envLines = @(
        '# Yerel gelistirme ayarlari. scripts\db-up.ps1 uretir; git''e gonderilmez.',
        '# Sifreyi elle degistirirseniz: en az 8 karakter, buyuk/kucuk harf, rakam ve sembol; $ ve # kullanmayin.',
        "MSSQL_PORT=$($envValues['MSSQL_PORT'])"
    )
    foreach ($key in $PasswordKeys) {
        $envLines += "$key=$($envValues[$key])"
    }
    Set-Content -Path '.env' -Value $envLines -Encoding ASCII
    Write-Host '  .env yazildi.'
}

if (-not (Select-String -Path '.gitignore' -Pattern '^\.env$' -Quiet)) {
    Add-Content -Path '.gitignore' -Value "`r`n# Yerel sifreler (scripts/db-up.ps1)`r`n.env"
}

$port = $envValues['MSSQL_PORT']

# --- 2/6 SQL Server ------------------------------------------------------------

Write-Step '2/6 SQL Server baslatiliyor (ilk seferde imaj inecek, birkac dakika surebilir)'
Invoke-Checked docker @('compose', 'up', '-d', '--wait', 'sqlserver', 'mailpit')

# --- 3/6 Veritabani, semalar, kullanicilar -------------------------------------

Write-Step '3/6 Veritabani, semalar ve kullanicilar'
Invoke-Checked docker @('compose', 'run', '--rm', 'db-init')

# --- 4/6 Baglanti cumleleri ----------------------------------------------------

Write-Step '4/6 Baglanti cumleleri (user-secrets)'

$apiXml = Get-Content -Raw -Path $ApiProject
if ($apiXml -notmatch '<UserSecretsId>') {
    Invoke-Checked dotnet @('user-secrets', 'init', '--project', $ApiProject, '--id', $UserSecretsId)
} elseif ($apiXml -notmatch "<UserSecretsId>$UserSecretsId</UserSecretsId>") {
    # Sablonun urettigi rastgele kimligi sabit kimlikle degistir (testler bu kimligi okur).
    $apiXml = $apiXml -replace '<UserSecretsId>[^<]*</UserSecretsId>', "<UserSecretsId>$UserSecretsId</UserSecretsId>"
    Set-Content -Path $ApiProject -Value $apiXml -Encoding UTF8 -NoNewline
    Write-Host "UserSecretsId '$UserSecretsId' olarak ayarlandi."
}

# 127.0.0.1: "localhost" bazi makinelerde once IPv6 (::1) adresine cozulur; Docker portu yalnizca 127.0.0.1e bagli oldugundan baglanti zaman asimina duser.
$server = "127.0.0.1,$port"
$appConnection = "Server=$server;Database=Modulix;User Id=modulix_app;Password=$($envValues['MODULIX_APP_PASSWORD']);TrustServerCertificate=True"
$migratorConnection = "Server=$server;Database=Modulix;User Id=modulix_migrator;Password=$($envValues['MODULIX_MIGRATOR_PASSWORD']);TrustServerCertificate=True"

Invoke-Checked dotnet @('user-secrets', 'set', 'ConnectionStrings:Modulix', $appConnection, '--project', $ApiProject)
Invoke-Checked dotnet @('user-secrets', 'set', 'ConnectionStrings:ModulixMigrations', $migratorConnection, '--project', $ApiProject)

# --- 5/6 Migration -------------------------------------------------------------

Write-Step '5/6 Migration uygulaniyor (tek zincir)'
Invoke-Checked dotnet @('run', '--project', 'src/Modulix.Api', '--', 'migrate')

# --- 6/6 Ornek veri ------------------------------------------------------------

Write-Step '6/6 Ornek veri (yalnizca gelistirme)'
Invoke-Checked dotnet @('run', '--project', 'src/Modulix.Api', '--no-build', '--', 'seed')

Write-Host ''
Write-Host "Veritabani hazir: localhost,$port / Modulix" -ForegroundColor Green
