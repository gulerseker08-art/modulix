#!/usr/bin/env bash
# Modulix - Görev 1: solution iskeleti (macOS / Linux)
#
# Repo kökünde, bir kez çalıştırın:
#   bash scripts/init-solution.sh
#
# Proje dosyalarını 'dotnet new' şablonları üretir; paket sürümleri sizin
# kurulu .NET 10 SDK'nızdan gelir. Betik sonunda derler ve testleri çalıştırır.

set -euo pipefail
cd "$(dirname "$0")/.."

step() { printf '\n== %s ==\n' "$1"; }
fail() { printf 'HATA: %s\n' "$1" >&2; exit 1; }

src_path()  { printf 'src/%s/%s.csproj' "$1" "$1"; }
test_path() { printf 'tests/%s/%s.csproj' "$1" "$1"; }

new_source() { # <ad> <şablon>
  dotnet new "$2" -n "$1" -o "src/$1" -f net10.0
  dotnet sln add "$(src_path "$1")"
}

new_test() { # <ad>
  dotnet new xunit -n "$1" -o "tests/$1" -f net10.0
  dotnet sln add "$(test_path "$1")"
}

add_refs() { # <proje yolu> <referans adı>...
  local from="$1"; shift
  local to
  for to in "$@"; do
    dotnet add "$from" reference "$(src_path "$to")"
  done
}

# --- Ön kontroller -------------------------------------------------------------

if [[ -e Modulix.sln || -e Modulix.slnx ]]; then
  fail "Solution zaten var. Bu betik yalnızca boş bir repoda bir kez çalıştırılır."
fi

export DOTNET_NOLOGO=1
sdk="$(dotnet --version | tail -n 1)" || fail ".NET 10 SDK bulunamadı. 'dotnet --list-sdks' çıktısını kontrol edin."
[[ "$sdk" == 10.* ]] || fail ".NET 10 SDK bulunamadı (bulunan: '$sdk')."
echo ".NET SDK: $sdk"

# --- Oluşturma ----------------------------------------------------------------

step "1/6 Solution ve repo dosyaları"
dotnet new sln -n Modulix
dotnet new gitignore
dotnet new editorconfig

step "2/6 Kaynak projeler (src)"
new_source Modulix.Core.Contracts    classlib
new_source Modulix.Core              classlib
new_source Modulix.Infrastructure    classlib
new_source Modulix.Modules.Crm       classlib
new_source Modulix.Modules.Logistics classlib
new_source Modulix.AI                classlib
new_source Modulix.Shared            classlib
new_source Modulix.Api               webapi
new_source Modulix.Web               blazorwasm

step "3/6 Test projeleri (tests)"
new_test Modulix.ArchitectureTests
new_test Modulix.Core.Tests
new_test Modulix.Modules.Crm.Tests
new_test Modulix.Modules.Logistics.Tests
new_test Modulix.IntegrationTests
new_test Modulix.E2ETests

step "4/6 Proje referansları"
# Kurallar tests/Modulix.ArchitectureTests içinde de denetlenir.
add_refs "$(src_path Modulix.Core)"              Modulix.Core.Contracts
add_refs "$(src_path Modulix.Infrastructure)"    Modulix.Core Modulix.Core.Contracts
add_refs "$(src_path Modulix.Modules.Crm)"       Modulix.Core.Contracts
add_refs "$(src_path Modulix.Modules.Logistics)" Modulix.Core.Contracts
add_refs "$(src_path Modulix.AI)"                Modulix.Core.Contracts
add_refs "$(src_path Modulix.Web)"               Modulix.Shared
add_refs "$(src_path Modulix.Api)" \
  Modulix.Core.Contracts Modulix.Core Modulix.Infrastructure \
  Modulix.Modules.Crm Modulix.Modules.Logistics Modulix.AI Modulix.Shared

add_refs "$(test_path Modulix.Core.Tests)"              Modulix.Core
add_refs "$(test_path Modulix.Modules.Crm.Tests)"       Modulix.Modules.Crm
add_refs "$(test_path Modulix.Modules.Logistics.Tests)" Modulix.Modules.Logistics
add_refs "$(test_path Modulix.IntegrationTests)"        Modulix.Api

step "5/6 Şablon örnek dosyaları ve mimari testler"
find src tests \( -name Class1.cs -o -name UnitTest1.cs \) -type f -delete
cp scripts/templates/ArchitectureTests/*.cs tests/Modulix.ArchitectureTests/

if ! grep -q 'Include="Xunit"' "$(test_path Modulix.ArchitectureTests)"; then
  echo 'global using Xunit;' > tests/Modulix.ArchitectureTests/GlobalUsings.cs
fi

step "6/6 Derleme ve testler"
dotnet build
# Diğer test projeleri henüz boş; testleri geldikçe bu satır 'dotnet test' olur.
dotnet test "$(test_path Modulix.ArchitectureTests)" --no-build

printf '\nTamam. Yukarıdaki test özetini (son 20 satır) Claude ile paylaşın.\n'
