using ConexaoSolidaria.Campaigns.Api.Data;
using ConexaoSolidaria.Campaigns.Api.Domain;
using ConexaoSolidaria.Campaigns.Api.Requests;
using ConexaoSolidaria.Campaigns.Api.Responses;
using ConexaoSolidaria.Campaigns.Api.Services;
using ConexaoSolidaria.Shared.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ConexaoSolidaria.Campaigns.Api.Controllers;

[ApiController]
[Route("api/campanhas")]
public sealed class CampanhasController(CampaignsDbContext db, ICampaignService campaignService) : ControllerBase
{
    [HttpGet("{id:guid}")]
    [Authorize(Roles = ApplicationRoles.GestorOng)]
    public async Task<ActionResult<CampanhaResponse>> ObterPorId(Guid id, CancellationToken cancellationToken)
    {
        var campaign = await campaignService.GetByIdAsync(id, cancellationToken);
        return campaign is null ? NotFound() : Ok(ToResponse(campaign));
    }

    [HttpGet("search")]
    [AllowAnonymous]
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
    [Authorize(Roles = ApplicationRoles.GestorOng)]
    [ProducesResponseType<CampanhaResponse>(StatusCodes.Status201Created)]
    public async Task<ActionResult<CampanhaResponse>> Criar(
        SalvarCampanhaRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var campaign = await campaignService.CreateAsync(
                request.Titulo,
                request.Descricao,
                request.DataInicio,
                request.DataFim,
                request.MetaFinanceira,
                request.Status,
                cancellationToken);

            return CreatedAtAction(nameof(ObterPorId), new { id = campaign.Id }, ToResponse(campaign));
        }
        catch (DomainRuleException ex)
        {
            return BadRequest(new { mensagem = ex.Message });
        }
    }

    [HttpPut("{id:guid}")]
    [Authorize(Roles = ApplicationRoles.GestorOng)]
    public async Task<ActionResult<CampanhaResponse>> Atualizar(
        Guid id,
        SalvarCampanhaRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var campaign = await campaignService.UpdateAsync(
                id,
                request.Titulo,
                request.Descricao,
                request.DataInicio,
                request.DataFim,
                request.MetaFinanceira,
                request.Status,
                cancellationToken);

            return campaign is null ? NotFound() : Ok(ToResponse(campaign));
        }
        catch (DomainRuleException ex)
        {
            return BadRequest(new { mensagem = ex.Message });
        }
    }

    [HttpGet("transparencia")]
    [AllowAnonymous]
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
            campaign.Status);
    }

}
