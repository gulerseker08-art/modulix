# ADR-0002: Tenant stratejisi

- **Durum:** Kabul edildi, 16 Ekim 2026
- **Karar:** Tek veritabanı, her tenant tablosunda `TenantId`, iki katmanlı koruma

## Problem

Tek geliştiricinin işletebileceği, izolasyonu tek bir kod hatasına bağlı bırakmayan ve ileride tenant başına ayrı veritabanına izin veren bir model gerekiyor.

## Seçenekler

1. **Shared database + `TenantId`** (seçildi).
2. Tenant başına şema: migration ve bağlantı yönetimi tenant sayısıyla büyür.
3. Tenant başına veritabanı: en güçlü izolasyon, en yüksek işletim yükü; Faz 3 kapsamında.

## Tasarım

- **Katman 1:** EF Core global query filter, `ITenantEntity` uygulayan her entity'de otomatik.
- **Katman 2:** SQL Server Row-Level Security. Bağlantı açılışında `sp_set_session_context 'TenantId'` çalışır; güvenlik politikası `TenantId = SESSION_CONTEXT(N'TenantId')` koşulunu hem okumada hem yazmada uygular.
- Uygulamanın veritabanı kullanıcısı RLS'i atlayamaz; migration ayrı bir kullanıcıyla çalışır.
- Bağlantı seçimi `ITenantConnectionResolver` arkasındadır; Faz 1'de tek bağlantı döner.
- Aktif tenant, oturumdaki kullanıcının üyeliğinden sunucuda doğrulanır; istemcinin gönderdiği `TenantId` tek başına kabul edilmez.

## Sonuçlar

- Her tenant tablosunda `TenantId` zorunludur ve indeksler onunla başlar.
- Platform geneli tablolar ayrı şemadadır ve RLS dışındadır.
- Arka plan işleri ve outbox işleyicisi tenant bağlamını açıkça kurar; bağlamsız sorgu satır döndürmez.
- Query filter unutulsa bile RLS veriyi korur; Sprint 1'de filtre kapatılarak testle kanıtlanır.
