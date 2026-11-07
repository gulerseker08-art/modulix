namespace Modulix.Shared.Platform;

// Destek erişimi (ADR-0028 Dilim 7, Y1.3): gerekçeli, süreli, salt okunur ve tek firmayla sınırlı.

/// <summary><see cref="Minutes"/> boşsa 60; 15-480 dakika arası olmalı. Gerekçe zorunludur.</summary>
public sealed record GrantSupportAccessRequest(string Reason, int? Minutes = null);


public enum SupportAccessStateDto
{
    Active = 1,
    Expired = 2,
    Revoked = 3,
}

public sealed record SupportAccessDto(
    Guid Id,
    Guid TenantId,
    string TenantName,
    Guid UserId,
    string UserName,
    string Reason,
    DateTimeOffset GrantedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? EndedAt,
    SupportAccessStateDto State);

/// <summary>Destek oturumunda yapılan bir istek (zaman, yöntem, yol).</summary>
public sealed record SupportAccessReadDto(DateTimeOffset At, string Method, string Path);
