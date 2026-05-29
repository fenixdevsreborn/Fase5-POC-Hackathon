using System.Security.Claims;
using ConexaoSolidaria.Campaigns.Api.Data;
using ConexaoSolidaria.Campaigns.Api.Domain;
using ConexaoSolidaria.Campaigns.Api.Messaging;
using ConexaoSolidaria.Campaigns.Api.Requests;
using ConexaoSolidaria.Campaigns.Api.Responses;
using ConexaoSolidaria.Shared.Auth;
using ConexaoSolidaria.Shared.Events;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ConexaoSolidaria.Campaigns.Api.Controllers;

[ApiController]
[Route("api/doacoes")]
public sealed class DoacoesController(CampaignsDbContext db, IDonationEventPublisher publisher) : ControllerBase
{
    [HttpPost]
    [Authorize(Roles = ApplicationRoles.Doador)]
    [ProducesResponseType<DoacaoAceitaResponse>(StatusCodes.Status202Accepted)]
    public async Task<ActionResult<DoacaoAceitaResponse>> Criar(
        CriarDoacaoRequest request,
        CancellationToken cancellationToken)
    {
        var doadorIdValue = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var doadorEmail = User.FindFirstValue(ClaimTypes.Email) ?? string.Empty;

        if (!Guid.TryParse(doadorIdValue, out var doadorId))
        {
            return Unauthorized(new { mensagem = "Token JWT nao contem o identificador do doador." });
        }

        if (request.ValorDoacao <= 0)
        {
            return BadRequest(new { mensagem = "ValorDoacao deve ser maior que zero." });
        }

        var campaign = await db.Campaigns.SingleOrDefaultAsync(
            item => item.Id == request.IdCampanha,
            cancellationToken);

        if (campaign is null)
        {
            return NotFound(new { mensagem = "Campanha nao encontrada." });
        }

        if (!campaign.CanReceiveDonation(DateTimeOffset.UtcNow))
        {
            return BadRequest(new { mensagem = "Doacao nao permitida para campanhas encerradas ou canceladas." });
        }

        var donation = Donation.Create(campaign.Id, doadorId, doadorEmail, request.ValorDoacao);
        db.Donations.Add(donation);
        await db.SaveChangesAsync(cancellationToken);

        var donationEvent = new DoacaoRecebidaEvent(
            Guid.NewGuid(),
            donation.Id,
            campaign.Id,
            doadorId,
            doadorEmail,
            donation.Valor,
            DateTimeOffset.UtcNow);

        await publisher.PublishAsync(donationEvent, cancellationToken);

        return Accepted(new DoacaoAceitaResponse(
            donation.Id,
            campaign.Id,
            donation.Valor,
            DonationStatus.Pendente.ToString(),
            "Doacao recebida e enviada para processamento assincrono."));
    }
}
