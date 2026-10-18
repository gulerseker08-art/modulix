# ADR-0001: Veritabanı

- **Durum:** Kabul edildi, 16 Ekim 2026
- **Karar:** SQL Server 2025

## Problem

Tenant izolasyonunu veritabanı düzeyinde de koruyan, custom field'ları JSON olarak saklayan, minimum bütçeye uyan ve tek geliştiricinin rahat işletebileceği bir veritabanı gerekiyor.

## Seçenekler

1. **SQL Server** (seçildi): geliştiricinin bildiği veritabanı; EF Core desteği ve Row-Level Security var.
2. **PostgreSQL**: lisans ücreti yok, güçlü JSON desteği; geliştirici için yeni bir veritabanı.
3. **Azure SQL Database ücretsiz katmanı**: veritabanı başına aylık 100.000 vCore saniye (yaklaşık 28 saat). Geliştirme için yeterli, mesai boyu açık kalan pilot ve sürekli çalışan outbox işi için yetersiz.

## Ortamlar

| Ortam | Sürüm | Lisans maliyeti |
| --- | --- | --- |
| Geliştirme (Docker) | SQL Server Developer | Yok |
| Pilot (VPS) | SQL Server Express | Yok |
| Büyüme | Azure SQL Database veya Standard | Var; fiyat doğrulanmadı |

Express sınırları (Microsoft dokümanı): en fazla 1 soket veya 4 çekirdek, 1.410 MB buffer pool, veritabanı başına 50 GB, SQL Server Agent yok. Linux sürümü sayfası 10 GB gösteriyor; pilot öncesi doğrulanacak.

## Sonuçlar

- Custom field'lar tek JSON kolonunda tutulur; sık filtrelenen alanlar için hesaplanan kolon ve indeks eklenir.
- SQL Agent olmadığı için yedekleme uygulamanın zamanlanmış işiyle alınır ve sunucu dışına kopyalanır.
- Express sınırına yaklaşılınca uygulama kodu değişmeden Azure SQL Database'e veya Standard'a geçilir.
- Veritabanına özgü kod yalnızca `Modulix.Infrastructure` içinde durur.
