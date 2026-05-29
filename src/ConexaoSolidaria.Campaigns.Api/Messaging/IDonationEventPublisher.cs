using ConexaoSolidaria.Shared.Events;

namespace ConexaoSolidaria.Campaigns.Api.Messaging;

public interface IDonationEventPublisher
{
    Task PublishAsync(DoacaoRecebidaEvent donationEvent, CancellationToken cancellationToken);
}
