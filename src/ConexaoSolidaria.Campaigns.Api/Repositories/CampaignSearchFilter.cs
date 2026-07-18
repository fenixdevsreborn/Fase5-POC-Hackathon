using System.Linq.Expressions;
using ConexaoSolidaria.Shared.Domain;

namespace ConexaoSolidaria.Campaigns.Api.Repositories;

/// <summary>
/// Recorte aplicado a uma busca de campanhas, ALEM do termo textual.
///
/// Existe como tipo unico de proposito: a busca tem dois motores (Elasticsearch e, quando ele cai,
/// PostgreSQL com ILIKE). Se cada um carregasse a sua propria copia do filtro, bastaria alguem
/// mexer em um deles para que uma queda do ES mudasse silenciosamente o conjunto de resultados —
/// numa pagina de transparencia, isso apareceria como campanhas canceladas/encerradas surgindo do
/// nada. Aqui o filtro e declarado uma vez e cada motor apenas o traduz para a sua linguagem
/// (<see cref="ToPredicate"/> para EF Core; o equivalente em query DSL fica no repositorio do ES).
/// </summary>
/// <param name="ApenasAtivas">
/// Restringe a campanhas em andamento: status Ativa E data de termino ainda no futuro. E o recorte
/// que a vitrine publica de transparencia usa.
/// </param>
public sealed record CampaignSearchFilter(bool ApenasAtivas = false)
{
    /// <summary>Sem nenhum recorte: busca em todas as campanhas (comportamento padrao).</summary>
    public static CampaignSearchFilter Nenhum { get; } = new();

    /// <summary>Somente campanhas ativas e dentro do prazo.</summary>
    public static CampaignSearchFilter SomenteAtivas { get; } = new(ApenasAtivas: true);

    /// <summary>Indica se ha algum recorte a aplicar (evita trabalho quando o filtro e vazio).</summary>
    public bool TemFiltro => ApenasAtivas;

    /// <summary>
    /// Traducao do filtro para EF Core, usada pelo fallback de PostgreSQL. Recebe o instante de
    /// referencia por parametro (em vez de ler UtcNow aqui dentro) para que a expressao seja
    /// deterministica e o mesmo "agora" valha para a consulta inteira.
    /// </summary>
    public Expression<Func<Campaign, bool>> ToPredicate(DateTimeOffset agora)
    {
        if (!ApenasAtivas)
        {
            return _ => true;
        }

        return campaign => campaign.Status == CampaignStatus.Ativa && campaign.DataFim >= agora;
    }
}
