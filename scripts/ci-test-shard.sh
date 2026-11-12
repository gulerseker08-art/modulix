#!/usr/bin/env bash
# CI: entegrasyon testlerini N parcaya boler ve verilen parcanin `dotnet test --filter` ifadesini yazar.
#
# Kullanim:
#   dotnet test <proje> --no-build --list-tests | scripts/ci-test-shard.sh <parca> <parca-sayisi>
#
# - Parca numarasi 1'den baslar. Bolme test SINIFI duzeyindedir: ayni sinifin testleri hep ayni parcada kalir
#   (siniflar IClassFixture<ModulixApiFactory> ve [Collection] paylasir).
# - Dengeleme: siniflar test sayisina gore buyukten kucuge siralanir, her sinif o an en az yuklu parcaya verilir.
#   Girdi ayni oldukca sonuc deterministiktir; her parca ayni listeden kendi payini hesaplar.
# - Filtre `FullyQualifiedName~<Sinif>.` bicimindedir (sondaki nokta onemli: `Foo.` filtresi `FooBar` sinifini yakalamaz).
# - Parca bos kalirsa (sinif sayisi parcadan azsa) hicbir testle eslesmeyen bir ifade yazilir.
set -euo pipefail

shard="${1:?parca numarasi gerekli (1..N)}"
count="${2:?parca sayisi gerekli}"

if ! [[ "$shard" =~ ^[0-9]+$ && "$count" =~ ^[0-9]+$ ]] || ((shard < 1 || shard > count)); then
  echo "gecersiz parca: $shard / $count" >&2
  exit 2
fi

# Test adlarinin ilk iki parcasi sinif adidir: Modulix.IntegrationTests.<Sinif>.<Yontem>(...)
classes=$(
  grep -E '^[[:space:]]+Modulix\.IntegrationTests\.' \
    | sed -E 's/^[[:space:]]+//' \
    | awk -F. '{ print $1 "." $2 "." $3 }' \
    | sort | uniq -c \
    | sort -k1,1nr -k2,2
)

if [ -z "$classes" ]; then
  echo "test listesi bos; --list-tests ciktisi okunamadi" >&2
  exit 3
fi

selected=$(
  echo "$classes" | awk -v shard="$shard" -v count="$count" '
    {
      # En az yuklu parcayi bul (esitlikte en kucuk numara).
      best = 1
      for (i = 2; i <= count; i++) if (load[i] < load[best]) best = i
      load[best] += $1
      if (best == shard) picked[++n] = $2
    }
    END {
      for (i = 1; i <= n; i++) print picked[i]
      print "#yuk " load[shard] > "/dev/stderr"
    }'
)

if [ -z "$selected" ]; then
  echo "FullyQualifiedName=Modulix.IntegrationTests.__bos_parca__"
  exit 0
fi

echo "$selected" | awk '{ printf "%sFullyQualifiedName~%s.", (NR > 1 ? "|" : ""), $1 }'
echo
