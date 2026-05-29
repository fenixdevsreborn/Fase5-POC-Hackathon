namespace ConexaoSolidaria.Identity.Api.Responses;

public sealed record AuthResponse(
    Guid UsuarioId,
    string NomeCompleto,
    string Email,
    string Role,
    string AccessToken,
    DateTimeOffset ExpiraEm);
