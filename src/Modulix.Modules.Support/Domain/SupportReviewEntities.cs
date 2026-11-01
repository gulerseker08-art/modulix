namespace Modulix.Modules.Support.Domain;

// Şikâyet inceleme (ADR-0024, dilim 6).

/// <summary>
/// Şikâyet talebinin düzeltici aksiyonu: kök nedene karşı yapılacak iş, sorumlusu ve son tarihi. Talebin çözümündeki serbest "düzeltici aksiyon" metninden ayrıdır; izlenebilir ve gecikebilir.
/// Tamamlama ve iptal gerekçe (kanıt/sonuç notu) ister; silinmez, yalnızca durumu değişir. Yalnızca şikâyet türündeki talepte açılır.
/// </summary>
public sealed class CorrectiveAction : SupportEntity
{
    public const int TitleMaxLength = 200;
    public const int NoteMaxLength = 1000;
    public const int MaxPerTicket = 20;

    public Guid TicketId { get; internal set; }

    public string Title { get; internal set; } = string.Empty;

    /// <summary>Sorumlu (etkin üye olmalı).</summary>
    public Guid OwnerId { get; internal set; }

    public DateOnly DueDate { get; internal set; }

    /// <summary><see cref="Lifecycle.CorrectiveActionStatus"/>.</summary>
    public string Status { get; internal set; } = Lifecycle.CorrectiveActionStatus.Open;

    /// <summary>Tamamlama sonucu ya da iptal gerekçesi.</summary>
    public string? Note { get; internal set; }

    public DateTimeOffset? ClosedAt { get; internal set; }

    public Guid? ClosedBy { get; internal set; }

    public Guid? CreatedBy { get; internal set; }
}
