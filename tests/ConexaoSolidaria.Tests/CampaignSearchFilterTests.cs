using ConexaoSolidaria.Campaigns.Api.Repositories;
using ConexaoSolidaria.Shared.Domain;

namespace ConexaoSolidaria.Tests;

/// <summary>
/// O filtro de busca e traduzido para dois motores (Elasticsearch e o fallback de PostgreSQL).
/// Estes testes travam a semantica do lado do EF — se ela mudar sem que a query do ES acompanhe,
/// uma queda do Elasticsearch passaria a exibir campanhas encerradas na vitrine de transparencia.
/// </summary>
public sealed class CampaignSearchFilterTests
{
    private static readonly DateTimeOffset Agora = new(2026, 07, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Nenhum_NaoDeveFiltrarNada()
    {
        Assert.False(CampaignSearchFilter.Nenhum.TemFiltro);

        var predicado = CampaignSearchFilter.Nenhum.ToPredicate(Agora).Compile();

        Assert.True(predicado(Criar(CampaignStatus.Ativa, Agora.AddDays(10))));
        Assert.True(predicado(Criar(CampaignStatus.Cancelada, Agora.AddDays(10))));
        Assert.True(predicado(Criar(CampaignStatus.Concluida, Agora.AddDays(-10))));
    }

    [Fact]
    public void SomenteAtivas_DeveAceitarAtivaDentroDoPrazo()
    {
        Assert.True(CampaignSearchFilter.SomenteAtivas.TemFiltro);

        var predicado = CampaignSearchFilter.SomenteAtivas.ToPredicate(Agora).Compile();

        Assert.True(predicado(Criar(CampaignStatus.Ativa, Agora.AddDays(10))));
    }

    // Limite inclusivo: uma campanha que termina exatamente agora ainda esta valendo.
    [Fact]
    public void SomenteAtivas_DeveAceitarCampanhaQueTerminaExatamenteAgora()
    {
        var predicado = CampaignSearchFilter.SomenteAtivas.ToPredicate(Agora).Compile();

        Assert.True(predicado(Criar(CampaignStatus.Ativa, Agora)));
    }

    [Fact]
    public void SomenteAtivas_DeveRejeitarAtivaComPrazoVencido()
    {
        var predicado = CampaignSearchFilter.SomenteAtivas.ToPredicate(Agora).Compile();

        Assert.False(predicado(Criar(CampaignStatus.Ativa, Agora.AddSeconds(-1))));
    }

    [Theory]
    [InlineData(CampaignStatus.Concluida)]
    [InlineData(CampaignStatus.Cancelada)]
    public void SomenteAtivas_DeveRejeitarStatusNaoAtivo(CampaignStatus status)
    {
        var predicado = CampaignSearchFilter.SomenteAtivas.ToPredicate(Agora).Compile();

        // Mesmo dentro do prazo, o status manda.
        Assert.False(predicado(Criar(status, Agora.AddDays(30))));
    }

    // Cria a campanha ja no estado desejado. O dominio nao deixa nascer com data no passado nem
    // transicionar de Ativa direto para outro estado sem regra, entao a construcao respeita isso:
    // nasce valida no futuro e, quando preciso, transiciona e so depois "vence".
    private static Campaign Criar(CampaignStatus status, DateTimeOffset dataFim)
    {
        var referencia = dataFim < Agora ? dataFim : Agora;

        var campanha = Campaign.Create(
            "Campanha de teste",
            "Descricao de teste",
            referencia.AddDays(-1),
            dataFim,
            1000m,
            CampaignStatus.Ativa,
            referencia.AddDays(-1));

        switch (status)
        {
            case CampaignStatus.Concluida:
                campanha.Concluir();
                break;
            case CampaignStatus.Cancelada:
                campanha.Cancelar();
                break;
        }

        return campanha;
    }
}
