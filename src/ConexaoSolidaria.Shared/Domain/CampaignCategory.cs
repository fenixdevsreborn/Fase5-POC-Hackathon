namespace ConexaoSolidaria.Shared.Domain;

/// <summary>
/// Categoria/causa da campanha, exibida como chip no frontend e usada para filtro visual.
/// Persistida como string (igual a <see cref="CampaignStatus"/>). O valor default e
/// <see cref="Outros"/> (0), garantindo que registros/documentos sem a categoria caiam num
/// rotulo valido em vez de um enum indefinido.
/// </summary>
public enum CampaignCategory
{
    Outros = 0,
    Saude = 1,
    Educacao = 2,
    Alimentacao = 3,
    Moradia = 4,
    MeioAmbiente = 5,
    Assistencia = 6,
    Animais = 7,
    Cultura = 8
}
