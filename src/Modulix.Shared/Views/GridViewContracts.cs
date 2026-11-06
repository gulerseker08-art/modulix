namespace Modulix.Shared.Views;

/// <summary>Kullanıcının kişisel liste görünümü (ADR-0036): görünen sütunlar (sıralı), sıralama anahtarı ve sorgu dizgisi biçiminde süzgeç.</summary>
public sealed record GridViewDto(Guid Id, long Version, string ScreenKey, string Name, IReadOnlyList<string> Columns, string? Sort, string? Filters);

/// <summary>Oluştururken <c>Version</c> verilmez; güncellemede okunan sürüm gönderilir (eski sürüm çakışmadır).</summary>
public sealed record SaveGridViewRequest(string ScreenKey, string Name, IReadOnlyList<string> Columns, string? Sort = null, string? Filters = null, long? Version = null);
