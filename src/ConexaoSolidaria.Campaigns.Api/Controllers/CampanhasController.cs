using ConexaoSolidaria.Campaigns.Api.Data;
using ConexaoSolidaria.Campaigns.Api.Domain;
using ConexaoSolidaria.Campaigns.Api.Requests;
using ConexaoSolidaria.Campaigns.Api.Responses;
using ConexaoSolidaria.Shared.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ConexaoSolidaria.Campaigns.Api.Controllers;

[ApiController]
[Route("api/campanhas")]
public sealed class CampanhasController(CampaignsDbContext db) : ControllerBase
{
    [HttpGet("{id:guid}")]
    [Authorize(Roles = ApplicationRoles.GestorOng)]
    public async Task<ActionResult<CampanhaResponse>> ObterPorId(Guid id, CancellationToken cancellationToken)
    {
        var campaign = await db.Campaigns.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        return campaign is null ? NotFound() : Ok(ToResponse(campaign));
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
            var campaign = Campaign.Create(
                request.Titulo,
                request.Descricao,
                request.DataInicio,
                request.DataFim,
                request.MetaFinanceira,
                request.Status,
                DateTimeOffset.UtcNow);

            db.Campaigns.Add(campaign);
            await db.SaveChangesAsync(cancellationToken);

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
        var campaign = await db.Campaigns.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (campaign is null)
        {
            return NotFound();
        }

        try
        {
            campaign.Update(
                request.Titulo,
                request.Descricao,
                request.DataInicio,
                request.DataFim,
                request.MetaFinanceira,
                request.Status,
                DateTimeOffset.UtcNow);

            await db.SaveChangesAsync(cancellationToken);
            return Ok(ToResponse(campaign));
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
