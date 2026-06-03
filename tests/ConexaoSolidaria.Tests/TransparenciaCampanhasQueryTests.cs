using ConexaoSolidaria.Campaigns.Api.Requests;

namespace ConexaoSolidaria.Tests;

public sealed class TransparenciaCampanhasQueryTests
{
    [Fact]
    public void Validate_ShouldAcceptDefaultQuery()
    {
        var query = new TransparenciaCampanhasQuery();

        var errors = query.Validate();

        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_ShouldRejectInvalidPagination()
    {
        var query = new TransparenciaCampanhasQuery
        {
            Page = 0,
            PageSize = TransparenciaCampanhasQuery.MaxPageSize + 1
        };

        var errors = query.Validate();

        Assert.Contains(nameof(TransparenciaCampanhasQuery.Page), errors.Keys);
        Assert.Contains(nameof(TransparenciaCampanhasQuery.PageSize), errors.Keys);
    }

    [Fact]
    public void Validate_ShouldRejectInvalidRanges()
    {
        var query = new TransparenciaCampanhasQuery
        {
            MetaMinima = 2000,
            MetaMaxima = 1000,
            ValorArrecadadoMinimo = 500,
            ValorArrecadadoMaximo = 100,
            DataFimInicial = DateTimeOffset.UtcNow.AddDays(10),
            DataFimFinal = DateTimeOffset.UtcNow
        };

        var errors = query.Validate();

        Assert.Contains("MetaFinanceira", errors.Keys);
        Assert.Contains("ValorArrecadado", errors.Keys);
        Assert.Contains("DataFim", errors.Keys);
    }

    [Fact]
    public void SearchValidate_ShouldAcceptTitleAndDefaultPagination()
    {
        var query = new TransparenciaCampanhasSearchQuery
        {
            Titulo = "Natal"
        };

        var errors = query.Validate();

        Assert.Empty(errors);
    }

    [Fact]
    public void SearchValidate_ShouldRejectMissingTitleAndInvalidPagination()
    {
        var query = new TransparenciaCampanhasSearchQuery
        {
            Titulo = " ",
            Page = 0,
            PageSize = TransparenciaCampanhasSearchQuery.MaxPageSize + 1
        };

        var errors = query.Validate();

        Assert.Contains(nameof(TransparenciaCampanhasSearchQuery.Titulo), errors.Keys);
        Assert.Contains(nameof(TransparenciaCampanhasSearchQuery.Page), errors.Keys);
        Assert.Contains(nameof(TransparenciaCampanhasSearchQuery.PageSize), errors.Keys);
    }
}
