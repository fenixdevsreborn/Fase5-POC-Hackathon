using ConexaoSolidaria.Shared.Validation;

namespace ConexaoSolidaria.Tests;

public sealed class CpfValidatorTests
{
    [Theory]
    [InlineData("529.982.247-25")]
    [InlineData("52998224725")]
    public void IsValid_ShouldAcceptValidCpf(string cpf)
    {
        Assert.True(CpfValidator.IsValid(cpf));
    }

    [Theory]
    [InlineData("111.111.111-11")]
    [InlineData("123")]
    [InlineData("")]
    public void IsValid_ShouldRejectInvalidCpf(string cpf)
    {
        Assert.False(CpfValidator.IsValid(cpf));
    }
}
