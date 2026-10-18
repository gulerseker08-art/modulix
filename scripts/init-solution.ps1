# Modulix - Gorev 1: solution iskeleti
#
# Repo kokunde, bir kez calistirin:
#   powershell -ExecutionPolicy Bypass -File scripts\init-solution.ps1
#
# Proje dosyalarini 'dotnet new' sablonlari uretir; paket surumleri sizin
# kurulu .NET 10 SDK'nizdan gelir. Betik sonunda derler ve testleri calistirir.

$ErrorActionPreference = 'Stop'
Set-Location (Split-Path -Parent $PSScriptRoot)

function Invoke-Dotnet {
    & dotnet @args
    if ($LASTEXITCODE -ne 0) {
        throw "Komut basarisiz oldu: dotnet $($args -join ' ')"
    }
}

function Write-Step([string] $text) {
    Write-Host ''
    Write-Host "== $text ==" -ForegroundColor Cyan
}

# --- On kontroller ------------------------------------------------------------

if ((Test-Path 'Modulix.sln') -or (Test-Path 'Modulix.slnx')) {
    throw 'Solution zaten var. Bu betik yalnizca bos bir repoda bir kez calistirilir.'
}

$env:DOTNET_NOLOGO = '1'
$sdk = & dotnet --version | Select-Object -Last 1
if ($LASTEXITCODE -ne 0 -or -not ("$sdk" -like '10.*')) {
    throw ".NET 10 SDK bulunamadi (bulunan: '$sdk'). 'dotnet --list-sdks' ciktisini kontrol edin."
}
Write-Host ".NET SDK: $sdk"

# --- Proje tanimlari ----------------------------------------------------------

$sourceProjects = [ordered]@{
    'Modulix.Core.Contracts'    = 'classlib'
    'Modulix.Core'              = 'classlib'
    'Modulix.Infrastructure'    = 'classlib'
    'Modulix.Modules.Crm'       = 'classlib'
    'Modulix.Modules.Logistics' = 'classlib'
    'Modulix.AI'                = 'classlib'
    'Modulix.Shared'            = 'classlib'
    'Modulix.Api'               = 'webapi'
    'Modulix.Web'               = 'blazorwasm'
}

# Kurallar tests/Modulix.ArchitectureTests icinde de denetlenir.
$sourceReferences = [ordered]@{
    'Modulix.Core'              = @('Modulix.Core.Contracts')
    'Modulix.Infrastructure'    = @('Modulix.Core', 'Modulix.Core.Contracts')
    'Modulix.Modules.Crm'       = @('Modulix.Core.Contracts')
    'Modulix.Modules.Logistics' = @('Modulix.Core.Contracts')
    'Modulix.AI'                = @('Modulix.Core.Contracts')
    'Modulix.Web'               = @('Modulix.Shared')
    'Modulix.Api'               = @(
        'Modulix.Core.Contracts', 'Modulix.Core', 'Modulix.Infrastructure',
        'Modulix.Modules.Crm', 'Modulix.Modules.Logistics', 'Modulix.AI', 'Modulix.Shared')
}

$testProjects = [ordered]@{
    'Modulix.ArchitectureTests'       = @()
    'Modulix.Core.Tests'              = @('Modulix.Core')
    'Modulix.Modules.Crm.Tests'       = @('Modulix.Modules.Crm')
    'Modulix.Modules.Logistics.Tests' = @('Modulix.Modules.Logistics')
    'Modulix.IntegrationTests'        = @('Modulix.Api')
    'Modulix.E2ETests'                = @()
}

# --- Olusturma ----------------------------------------------------------------

Write-Step '1/6 Solution ve repo dosyalari'
Invoke-Dotnet new sln -n Modulix
Invoke-Dotnet new gitignore
Invoke-Dotnet new editorconfig

Write-Step '2/6 Kaynak projeler (src)'
foreach ($name in $sourceProjects.Keys) {
    $template = $sourceProjects[$name]
    Invoke-Dotnet new $template -n $name -o "src/$name" -f net10.0
    Invoke-Dotnet sln add "src/$name/$name.csproj"
}

Write-Step '3/6 Test projeleri (tests)'
foreach ($name in $testProjects.Keys) {
    Invoke-Dotnet new xunit -n $name -o "tests/$name" -f net10.0
    Invoke-Dotnet sln add "tests/$name/$name.csproj"
}

Write-Step '4/6 Proje referanslari'
foreach ($name in $sourceReferences.Keys) {
    foreach ($reference in $sourceReferences[$name]) {
        Invoke-Dotnet add "src/$name/$name.csproj" reference "src/$reference/$reference.csproj"
    }
}
foreach ($name in $testProjects.Keys) {
    foreach ($reference in $testProjects[$name]) {
        Invoke-Dotnet add "tests/$name/$name.csproj" reference "src/$reference/$reference.csproj"
    }
}

Write-Step '5/6 Sablon ornek dosyalari ve mimari testler'
Get-ChildItem -Path 'src', 'tests' -Recurse -File -Include 'Class1.cs', 'UnitTest1.cs' | Remove-Item
Copy-Item -Path 'scripts/templates/ArchitectureTests/*.cs' -Destination 'tests/Modulix.ArchitectureTests/'

$architectureProject = 'tests/Modulix.ArchitectureTests/Modulix.ArchitectureTests.csproj'
if (-not (Select-String -Path $architectureProject -Pattern 'Include="Xunit"' -SimpleMatch -Quiet)) {
    Set-Content -Path 'tests/Modulix.ArchitectureTests/GlobalUsings.cs' -Value 'global using Xunit;' -Encoding UTF8
}

Write-Step '6/6 Derleme ve testler'
Invoke-Dotnet build
# Diger test projeleri henuz bos; testleri geldikce bu satir 'dotnet test' olur.
Invoke-Dotnet test 'tests/Modulix.ArchitectureTests/Modulix.ArchitectureTests.csproj' --no-build

Write-Host ''
Write-Host 'Tamam. Yukaridaki test ozetini (son 20 satir) Claude ile paylasin.' -ForegroundColor Green
