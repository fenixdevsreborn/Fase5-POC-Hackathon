namespace ConexaoSolidaria.Identity.Api.Requests;

public sealed record CadastroDoadorRequest(
    string NomeCompleto,
    string Email,
    string Cpf,
    string Senha);
