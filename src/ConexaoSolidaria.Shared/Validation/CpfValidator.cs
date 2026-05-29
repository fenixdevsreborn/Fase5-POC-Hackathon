using System.Text.RegularExpressions;

namespace ConexaoSolidaria.Shared.Validation;

public static partial class CpfValidator
{
    public static bool IsValid(string? cpf)
    {
        if (string.IsNullOrWhiteSpace(cpf))
        {
            return false;
        }

        var digits = Normalize(cpf);
        if (digits.Length != 11 || digits.Distinct().Count() == 1)
        {
            return false;
        }

        var firstVerifier = CalculateVerifierDigit(digits[..9], 10);
        var secondVerifier = CalculateVerifierDigit(digits[..9] + firstVerifier, 11);

        return digits[9] == ToChar(firstVerifier) && digits[10] == ToChar(secondVerifier);
    }

    public static string Normalize(string cpf) => NonDigitsRegex().Replace(cpf, string.Empty);

    private static int CalculateVerifierDigit(string digits, int initialWeight)
    {
        var sum = 0;

        for (var index = 0; index < digits.Length; index++)
        {
            sum += (digits[index] - '0') * (initialWeight - index);
        }

        var remainder = sum % 11;
        return remainder < 2 ? 0 : 11 - remainder;
    }

    private static char ToChar(int digit) => (char)('0' + digit);

    [GeneratedRegex(@"\D", RegexOptions.Compiled)]
    private static partial Regex NonDigitsRegex();
}
