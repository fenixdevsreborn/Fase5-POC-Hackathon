namespace ConexaoSolidaria.Shared.Domain;

/// <summary>
/// Ja existe uma campanha com o mesmo titulo (comparacao por
/// <see cref="Campaign.NormalizarTitulo"/>: sem diferenciar maiusculas/minusculas nem espacos).
///
/// Separada de <see cref="DomainRuleException"/> de proposito: nao e um dado invalido (422),
/// e um conflito com o estado atual do sistema — o handler global traduz para 409 Conflict.
/// </summary>
public sealed class DuplicateCampaignTitleException(string titulo)
    : Exception($"Ja existe uma campanha com o titulo \"{titulo}\".")
{
    /// <summary>Titulo, como informado pelo usuario, que colidiu com uma campanha existente.</summary>
    public string Titulo { get; } = titulo;
}
