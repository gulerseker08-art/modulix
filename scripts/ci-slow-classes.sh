#!/usr/bin/env bash
# CI: bir .trx dosyasindan test siniflarini toplam test suresine gore siralar (en yavas 15) ve Markdown tablosu yazar.
# Sureler paralel kosan testlerin toplamidir; duvar saatinden buyuk olabilir, siralama ise anlamlidir.
# Amac: parcalamayi (scripts/ci-test-shard.sh) ve yavas sinif adaylarini veriyle ayarlamak.
#
# Kullanim: scripts/ci-slow-classes.sh <dosya.trx> [>> "$GITHUB_STEP_SUMMARY"]
set -euo pipefail

trx="${1:?trx dosyasi gerekli}"
[ -f "$trx" ] || { echo "trx bulunamadi: $trx" >&2; exit 0; }

echo "| Sinif | Test | Toplam sure (sn) |"
echo "|---|---:|---:|"
grep -o '<UnitTestResult [^>]*>' "$trx" \
  | sed -E 's/.*testName="([^"]*)".*duration="([0-9:.]+)".*/\1\t\2/' \
  | awk -F'\t' '
      {
        split($1, p, ".")
        cls = p[1] "." p[2] "." p[3]
        sub(/^Modulix\.IntegrationTests\./, "", cls)
        split($2, t, ":")
        sec = t[1] * 3600 + t[2] * 60 + t[3]
        total[cls] += sec
        tests[cls]++
      }
      END { for (c in total) printf "%s\t%d\t%.1f\n", c, tests[c], total[c] }' \
  | sort -t$'\t' -k3,3nr \
  | head -n 15 \
  | awk -F'\t' '{ printf "| %s | %s | %s |\n", $1, $2, $3 }'
