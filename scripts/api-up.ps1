# Modulix - Docker'daki API imajini guncel kodla yeniden derler ve baslatir.
# `docker compose up` imaj zaten varsa yeniden derlemez; yerel imaj (modulix:local) kodun gerisinde kalir.
# main guncellendikten sonra guvenle calistirilabilir:
#   powershell -ExecutionPolicy Bypass -File scripts\api-up.ps1
#
# Yaptiklari:
#   1. main dalindaysaniz ve calisma agaciniz temizse `git pull --ff-only` ile main'i ceker (aksi halde atlar).
#   2. scripts\db-up.ps1 ile veritabanini ve migration'lari gunceller (-SkipDb ile atlanir).
#   3. API imajini yeniden derler ve konteyneri yeniler (`docker compose up -d --build api`).
#   4. /health yanit verene kadar bekler.

param(
    [switch] $SkipPull,
    [switch] $SkipDb
)

$ErrorActionPreference = 'Stop'
Set-Location (Split-Path -Parent $PSScriptRoot)

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

# --- 1/4 Kod -------------------------------------------------------------------

Write-Step '1/4 Kod guncelleniyor'

if ($SkipPull) {
    Write-Host '  -SkipPull verildi, atlandi.'
} else {
    $branch = (& git rev-parse --abbrev-ref HEAD).Trim()
    $dirty = (& git status --porcelain)
    if ($branch -ne 'main') {
        Write-Host "  Dal '$branch' (main degil); git pull atlandi, bu dalin kodu derlenecek." -ForegroundColor Yellow
    } elseif ($dirty) {
        Write-Host '  Calisma agacinda kaydedilmemis degisiklik var; git pull atlandi.' -ForegroundColor Yellow
    } else {
        Invoke-Checked git @('pull', '--ff-only', 'origin', 'main')
    }
}

Write-Host "  Derlenecek commit: $((& git rev-parse --short HEAD).Trim())"

# --- 2/4 Veritabani ------------------------------------------------------------

Write-Step '2/4 Veritabani ve migration'

if ($SkipDb) {
    Write-Host '  -SkipDb verildi, atlandi.'
} else {
    & (Join-Path $PSScriptRoot 'db-up.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'db-up.ps1 basarisiz oldu.' }
}

# --- 3/4 API imaji -------------------------------------------------------------

Write-Step '3/4 API imaji derleniyor (birkac dakika surebilir)'
Invoke-Checked docker @('compose', 'up', '-d', '--build', 'api')

# --- 4/4 Saglik kontrolu -------------------------------------------------------

Write-Step '4/4 /health bekleniyor'

$healthy = $false
for ($i = 0; $i -lt 30; $i++) {
    try {
        $response = Invoke-WebRequest -Uri 'http://127.0.0.1:8080/health' -UseBasicParsing -TimeoutSec 3
        if ($response.StatusCode -eq 200) { $healthy = $true; break }
    } catch {
        Start-Sleep -Seconds 2
    }
}

Write-Host ''
if ($healthy) {
    Write-Host 'API hazir: http://127.0.0.1:8080' -ForegroundColor Green
} else {
    Write-Host '/health 60 sn icinde yanit vermedi. Kayitlar: docker compose logs api --tail 100' -ForegroundColor Yellow
}
