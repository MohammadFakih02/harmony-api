namespace Harmony.API.DTOs.Requests;

public record CreateGuildRequest(
    string Name,
    string? Description
);

public record UpdateGuildRequest(
    string? Name,
    string? Description,
    bool? IsPublic
);