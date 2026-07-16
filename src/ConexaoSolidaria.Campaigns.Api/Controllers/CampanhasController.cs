using Asp.Versioning;
using ConexaoSolidaria.Shared.Domain;
using ConexaoSolidaria.Shared.Persistence;
using ConexaoSolidaria.Campaigns.Api.Infrastructure;
using ConexaoSolidaria.Campaigns.Api.Requests;
using ConexaoSolidaria.Campaigns.Api.Responses;
using ConexaoSolidaria.Campaigns.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.EntityFrameworkCore;

namespace ConexaoSolidaria.Campaigns.Api.Controllers;

[ApiController]
[ApiVersion("1.0")]
[Route("api/campanhas")]
public sealed class CampanhasController(CampaignsDbContext db, ICampaignService campaignService) : ControllerBase
{
    // Publico: o frontend anonimo consulta uma campanha por Id sem precisar listar/filtrar todas.
    // AllowAnonymous tambem permite o gestor autenticado (anonimo nao bloqueia quem tem token).
    [HttpGet("{id:guid}")]
    [AllowAnonymous]
    public async Task<ActionResult<CampanhaResponse>> ObterPorId(Guid id, CancellationToken cancellationToken)
    {
        var campaign = await campaignService.GetResponseByIdAsync(id, cancellationToken);
        return campaign is null
            ? ProblemResults.NotFound("Campanha nao encontrada.")
            : Ok(campaign);
    }

    // Cache curto (~5s) num read publico. Eventualmente consistente: o total arrecadado e atualizado
    // pelo Worker de forma assincrona; 5s de defasagem e aceitavel e reduz carga no ES/Postgres.
    [HttpGet("search")]
    [AllowAnonymous]
    [OutputCache(Duration = 5)]
    public async Task<ActionResult<PaginatedResponse<CampanhaResponse>>> Search(
        [FromQuery] string? q,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 10,
        CancellationToken cancellationToken = default)
    {
        var result = await campaignService.SearchAsync(q, page, pageSize, cancellationToken);
        page = Math.Max(page, 1);
        pageSize = pageSize < 1 ? 10 : Math.Clamp(pageSize, 1, 100);

        return Ok(new PaginatedResponse<CampanhaResponse>(
            result.Items,
            page,
            pageSize,
            result.Total,
            result.Total == 0 ? 0 : (int)Math.Ceiling(result.Total / (double)pageSize)));
    }

    [HttpPost]
    [Authorize(Policy = "CampaignManagement")]
    [ProducesResponseType<CampanhaResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<CampanhaResponse>> Criar(
        SalvarCampanhaRequest request,
        CancellationToken cancellationToken)
    {
        // Regras de dominio (DomainRuleException) sao convertidas em 422 pelo DomainExceptionHandler global.
        var campaign = await campaignService.CreateAsync(
            request.Titulo,
            request.Descricao,
            request.DataInicio,
            request.DataFim,
            request.MetaFinanceira,
            request.Status,
            request.Categoria,
            cancellationToken);

        return CreatedAtAction(nameof(ObterPorId), new { id = campaign.Id }, ToResponse(campaign));
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = "CampaignManagement")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<CampanhaResponse>> Atualizar(
        Guid id,
        SalvarCampanhaRequest request,
        CancellationToken cancellationToken)
    {
        // Regras de dominio (DomainRuleException) sao convertidas em 422 pelo DomainExceptionHandler global.
        var campaign = await campaignService.UpdateAsync(
            id,
            request.Titulo,
            request.Descricao,
            request.DataInicio,
            request.DataFim,
            request.MetaFinanceira,
            request.Status,
            request.Categoria,
            cancellationToken);

        return campaign is null
            ? ProblemResults.NotFound("Campanha nao encontrada.")
            : Ok(ToResponse(campaign));
    }

    // Cache curto (~5s) num read publico agregado. Eventualmente consistente (total vem do Worker);
    // 5s de defasagem e aceitavel para transparencia e reduz pressao no banco sob carga.
    [HttpGet("transparencia")]
    [AllowAnonymous]
    [OutputCache(Duration = 5)]
    public async Task<ActionResult<IReadOnlyCollection<TransparenciaCampanhaResponse>>> Transparencia(
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var campaigns = await db.Campaigns
            .AsNoTracking()
            .Where(campaign => campaign.Status == CampaignStatus.Ativa && campaign.DataFim >= now)
            .OrderBy(campaign => campaign.DataFim)
            .Select(campaign => new TransparenciaCampanhaResponse(
                campaign.Titulo,
                campaign.MetaFinanceira,
                campaign.ValorTotalArrecadado))
            .ToListAsync(cancellationToken);

        return Ok(campaigns);
    }

    // Read model para dashboards (CQRS leve). A tabela campaign_stats e POPULADA pelo Donations.Worker;
    // aqui apenas lemos (AsNoTracking + projecao). Endpoint anonimo para transparencia avancada/gestao.
    [HttpGet("stats")]
    [AllowAnonymous]
    [OutputCache(Duration = 5)]
    public async Task<ActionResult<IReadOnlyCollection<CampanhaStatsResponse>>> Stats(
        CancellationToken cancellationToken)
    {
        var stats = await db.CampaignStats
            .AsNoTracking()
            .OrderByDescending(item => item.AtualizadoEm)
            .Select(item => new CampanhaStatsResponse(
                item.CampaignId,
                item.Titulo,
                item.MetaFinanceira,
                item.TotalArrecadado,
                item.DoacoesProcessadas,
                item.AtualizadoEm))
            .ToListAsync(cancellationToken);

        return Ok(stats);
    }

    // Acoes de ciclo de vida (gestor). O dominio valida a transicao; DomainRuleException vira 422 global.
    [HttpPost("{id:guid}/ativar")]
    [Authorize(Policy = "CampaignManagement")]
    [ProducesResponseType<CampanhaResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public Task<ActionResult<CampanhaResponse>> Ativar(Guid id, CancellationToken cancellationToken)
        => TransicionarAsync(id, CampaignTransition.Ativar, cancellationToken);

    [HttpPost("{id:guid}/concluir")]
    [Authorize(Policy = "CampaignManagement")]
    [ProducesResponseType<CampanhaResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public Task<ActionResult<CampanhaResponse>> Concluir(Guid id, CancellationToken cancellationToken)
        => TransicionarAsync(id, CampaignTransition.Concluir, cancellationToken);

    [HttpPost("{id:guid}/cancelar")]
    [Authorize(Policy = "CampaignManagement")]
    [ProducesResponseType<CampanhaResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public Task<ActionResult<CampanhaResponse>> Cancelar(Guid id, CancellationToken cancellationToken)
        => TransicionarAsync(id, CampaignTransition.Cancelar, cancellationToken);

    private async Task<ActionResult<CampanhaResponse>> TransicionarAsync(
        Guid id,
        CampaignTransition action,
        CancellationToken cancellationToken)
    {
        var campaign = await campaignService.TransitionAsync(id, action, cancellationToken);
        return campaign is null
            ? ProblemResults.NotFound("Campanha nao encontrada.")
            : Ok(ToResponse(campaign));
    }

    // Resposta imediata de criar/atualizar: TotalDoadores = 0 aqui (o read model campaign_stats e
    // eventualmente consistente, atualizado pelo Worker). A vitrine/listagem le o valor real via search.
    private static CampanhaResponse ToResponse(Campaign campaign)
    {
        return new CampanhaResponse(
            campaign.Id,
            campaign.Titulo,
            campaign.Descricao,
            campaign.DataInicio,
            campaign.DataFim,
            campaign.MetaFinanceira,
            campaign.ValorTotalArrecadado,
            campaign.Status,
            campaign.Categoria,
            0);
    }

}
