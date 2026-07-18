using ConexaoSolidaria.Shared.Domain;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace ConexaoSolidaria.Campaigns.Api.Infrastructure;

/// <summary>
/// Traduz excecoes de dominio em <c>application/problem+json</c>:
/// <see cref="DomainRuleException"/> (dado invalido) em 422 Unprocessable Entity e
/// <see cref="DuplicateCampaignTitleException"/> (conflito com o estado atual) em 409 Conflict.
/// Demais excecoes nao sao tratadas aqui (retorna false) e caem no ProblemDetails padrao (500).
/// </summary>
public sealed class DomainExceptionHandler(
    IProblemDetailsService problemDetailsService,
    ILogger<DomainExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is DuplicateCampaignTitleException duplicateTitleException)
        {
            return await WriteConflictAsync(httpContext, duplicateTitleException, cancellationToken);
        }

        if (exception is not DomainRuleException domainRuleException)
        {
            return false;
        }

        logger.LogWarning(domainRuleException, "Regra de negocio violada: {Message}", domainRuleException.Message);

        httpContext.Response.StatusCode = StatusCodes.Status422UnprocessableEntity;

        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = domainRuleException,
            ProblemDetails = new ProblemDetails
            {
                Status = StatusCodes.Status422UnprocessableEntity,
                Title = "Regra de negocio violada",
                Detail = domainRuleException.Message,
                Type = "https://datatracker.ietf.org/doc/html/rfc4918#section-11.2"
            }
        });
    }

    // 409: o payload esta bem formado, mas colide com uma campanha ja cadastrada. A Web usa o
    // Detail direto no formulario/lista de importacao para apontar qual titulo repetiu.
    private async ValueTask<bool> WriteConflictAsync(
        HttpContext httpContext,
        DuplicateCampaignTitleException exception,
        CancellationToken cancellationToken)
    {
        logger.LogWarning("Titulo de campanha duplicado: {Titulo}", exception.Titulo);

        httpContext.Response.StatusCode = StatusCodes.Status409Conflict;

        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = new ProblemDetails
            {
                Status = StatusCodes.Status409Conflict,
                Title = "Campanha duplicada",
                Detail = exception.Message,
                Type = "https://datatracker.ietf.org/doc/html/rfc9110#section-15.5.10"
            }
        });
    }
}
