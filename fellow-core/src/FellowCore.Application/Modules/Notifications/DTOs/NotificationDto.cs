namespace FellowCore.Application.Modules.Notifications.DTOs;

/// <summary>
/// Resposta da API pra UI do dropdown de notificações. Type serializado como
/// int (convenção do projeto — frontend normaliza). MetadataJson volta como
/// objeto JSON inline (não string) pra ergonomia do frontend; backend
/// deserializa antes de devolver.
/// </summary>
public record NotificationDto(
    Guid Id,
    int Type,
    string Title,
    string Body,
    string? ResourceUrl,
    object? Metadata,
    DateTime? ReadAt,
    DateTime CreatedAt
);

/// <summary>
/// Lista paginada — front usa TotalCount pra "mais 23 não lidas" / paginação.
/// </summary>
public record NotificationListDto(
    IReadOnlyList<NotificationDto> Items,
    int TotalCount,
    int UnreadCount
);
