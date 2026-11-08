namespace Modulix.Shared.Notifications;

public sealed record NotificationDto(Guid Id, string Kind, string Title, string? Link, DateTimeOffset CreatedAt, bool IsRead);

public sealed record NotificationPageDto(IReadOnlyList<NotificationDto> Items, int UnreadCount);

public sealed record UnreadNotificationCountDto(int Count);
