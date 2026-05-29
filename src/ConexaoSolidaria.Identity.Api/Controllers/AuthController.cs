using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using ConexaoSolidaria.Identity.Api.Data;
using ConexaoSolidaria.Identity.Api.Domain;
using ConexaoSolidaria.Identity.Api.Requests;
using ConexaoSolidaria.Identity.Api.Responses;
using ConexaoSolidaria.Identity.Api.Security;
using ConexaoSolidaria.Shared.Validation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ConexaoSolidaria.Identity.Api.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class AuthController(IdentityDbContext db, JwtTokenService jwtTokenService) : ControllerBase
{
    [HttpPost("cadastro-doador")]
    [AllowAnonymous]
    [ProducesResponseType<AuthResponse>(StatusCodes.Status201Created)]
    public async Task<ActionResult<AuthResponse>> CadastrarDoador(
        CadastroDoadorRequest request,
        CancellationToken cancellationToken)
    {
        var validationErrors = ValidateCadastro(request);
        if (validationErrors.Count > 0)
        {
            return BadRequest(new ValidationProblemDetails(validationErrors));
        }

        var email = AppUser.NormalizeEmail(request.Email);
        var cpf = CpfValidator.Normalize(request.Cpf);

        if (await db.Users.AnyAsync(user => user.Email == email, cancellationToken))
        {
            return Conflict(new { mensagem = "Ja existe um usuario com este email." });
        }

        if (await db.Users.AnyAsync(user => user.Cpf == cpf, cancellationToken))
        {
            return Conflict(new { mensagem = "Ja existe um usuario com este CPF." });
        }

        var user = AppUser.CreateDoador(
            request.NomeCompleto,
            request.Email,
            request.Cpf,
            BCrypt.Net.BCrypt.HashPassword(request.Senha));

        db.Users.Add(user);
        await db.SaveChangesAsync(cancellationToken);

        return Created($"/api/auth/me", jwtTokenService.CreateToken(user));
    }

    [HttpPost("login")]
    [AllowAnonymous]
    [ProducesResponseType<AuthResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<AuthResponse>> Login(LoginRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Senha))
        {
            return BadRequest(new ValidationProblemDetails(new Dictionary<string, string[]>
            {
                ["credenciais"] = ["Email e senha sao obrigatorios."]
            }));
        }

        var email = AppUser.NormalizeEmail(request.Email);
        var user = await db.Users.SingleOrDefaultAsync(item => item.Email == email, cancellationToken);

        if (user is null || !BCrypt.Net.BCrypt.Verify(request.Senha, user.PasswordHash))
        {
            return Unauthorized(new { mensagem = "Credenciais invalidas." });
        }

        return Ok(jwtTokenService.CreateToken(user));
    }

    [HttpGet("me")]
    [Authorize]
    public ActionResult<object> Me()
    {
        return Ok(new
        {
            usuarioId = User.FindFirstValue(ClaimTypes.NameIdentifier),
            nome = User.FindFirstValue(ClaimTypes.Name),
            email = User.FindFirstValue(ClaimTypes.Email),
            role = User.FindFirstValue(ClaimTypes.Role)
        });
    }

    private static Dictionary<string, string[]> ValidateCadastro(CadastroDoadorRequest request)
    {
        var errors = new Dictionary<string, string[]>();

        if (string.IsNullOrWhiteSpace(request.NomeCompleto))
        {
            errors["nomeCompleto"] = ["Nome completo e obrigatorio."];
        }

        if (string.IsNullOrWhiteSpace(request.Email) || !new EmailAddressAttribute().IsValid(request.Email))
        {
            errors["email"] = ["Email invalido."];
        }

        if (!CpfValidator.IsValid(request.Cpf))
        {
            errors["cpf"] = ["CPF invalido."];
        }

        if (string.IsNullOrWhiteSpace(request.Senha) || request.Senha.Length < 8)
        {
            errors["senha"] = ["Senha deve conter pelo menos 8 caracteres."];
        }

        return errors;
    }
}
