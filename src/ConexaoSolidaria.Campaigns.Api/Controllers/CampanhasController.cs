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
    [ProducesResponseType<PaginatedResponse<TransparenciaCampanhaResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<PaginatedResponse<TransparenciaCampanhaResponse>>> Transparencia(
        CancellationToken cancellationToken,
        [FromQuery] TransparenciaCampanhasQuery request)
    {
        var validationErrors = request.Validate();
        if (validationErrors.Count > 0)
        {
            return BadRequest(new ValidationProblemDetails(validationErrors));
        }

        var now = DateTimeOffset.UtcNow;
        var query = db.Campaigns
            .AsNoTracking()
            .Where(campaign => campaign.Status == CampaignStatus.Ativa && campaign.DataFim >= now);

        if (!string.IsNullOrWhiteSpace(request.Titulo))
        {
            var titleFilter = $"%{request.Titulo.Trim()}%";
            query = query.Where(campaign => EF.Functions.ILike(campaign.Titulo, titleFilter));
        }

        if (request.MetaMinima is not null)
        {
            query = query.Where(campaign => campaign.MetaFinanceira >= request.MetaMinima);
        }

        if (request.MetaMaxima is not null)
        {
            query = query.Where(campaign => campaign.MetaFinanceira <= request.MetaMaxima);
        }

        if (request.ValorArrecadadoMinimo is not null)
        {
            query = query.Where(campaign => campaign.ValorTotalArrecadado >= request.ValorArrecadadoMinimo);
        }

        if (request.ValorArrecadadoMaximo is not null)
        {
            query = query.Where(campaign => campaign.ValorTotalArrecadado <= request.ValorArrecadadoMaximo);
        }

        if (request.DataFimInicial is not null)
        {
            query = query.Where(campaign => campaign.DataFim >= request.DataFimInicial.Value.ToUniversalTime());
        }

        if (request.DataFimFinal is not null)
        {
            query = query.Where(campaign => campaign.DataFim <= request.DataFimFinal.Value.ToUniversalTime());
        }

        var totalItems = await query.CountAsync(cancellationToken);

        var campaigns = await query
            .OrderBy(campaign => campaign.DataFim)
            .ThenBy(campaign => campaign.Titulo)
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .Select(campaign => new TransparenciaCampanhaResponse(
                campaign.Titulo,
                campaign.MetaFinanceira,
                campaign.ValorTotalArrecadado))
            .ToListAsync(cancellationToken);

        return Ok(PaginatedResponse<TransparenciaCampanhaResponse>.Create(
            campaigns,
            request.Page,
            request.PageSize,
            totalItems));
    }

    [HttpGet("transparencia-search")]
    [AllowAnonymous]
    [ProducesResponseType<PaginatedResponse<TransparenciaCampanhaResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<PaginatedResponse<TransparenciaCampanhaResponse>>> TransparenciaSearch(
        CancellationToken cancellationToken,
        [FromQuery] TransparenciaCampanhasSearchQuery request)
    {
        var validationErrors = request.Validate();
        if (validationErrors.Count > 0)
        {
            return BadRequest(new ValidationProblemDetails(validationErrors));
        }

        var matchingCampaignIds = await campaignSearchService.SearchIdsByTitleAsync(
            request.Titulo!,
            cancellationToken);

        if (matchingCampaignIds is null)
        {
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new { mensagem = "Elasticsearch indisponivel para busca fuzzy por titulo." });
        }

        if (matchingCampaignIds.Count == 0)
        {
            return Ok(PaginatedResponse<TransparenciaCampanhaResponse>.Create(
                Array.Empty<TransparenciaCampanhaResponse>(),
                request.Page,
                request.PageSize,
                0));
        }

        var now = DateTimeOffset.UtcNow;
        var ids = matchingCampaignIds.ToArray();
        var orderBySearchResult = ids
            .Select((id, index) => new { id, index })
            .ToDictionary(item => item.id, item => item.index);

        var matchingCampaigns = await db.Campaigns
            .AsNoTracking()
            .Where(campaign => campaign.Status == CampaignStatus.Ativa && campaign.DataFim >= now)
            .Where(campaign => ids.Contains(campaign.Id))
            .Select(campaign => new
            {
                campaign.Id,
                campaign.Titulo,
                campaign.MetaFinanceira,
                campaign.ValorTotalArrecadado
            })
            .ToListAsync(cancellationToken);

        var totalItems = matchingCampaigns.Count;
        var campaigns = matchingCampaigns
            .OrderBy(campaign => orderBySearchResult[campaign.Id])
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .Select(campaign => new TransparenciaCampanhaResponse(
                campaign.Titulo,
                campaign.MetaFinanceira,
                campaign.ValorTotalArrecadado))
            .ToList();

        return Ok(PaginatedResponse<TransparenciaCampanhaResponse>.Create(
            campaigns,
            request.Page,
            request.PageSize,
            totalItems));
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
