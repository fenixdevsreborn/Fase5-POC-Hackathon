using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ConexaoSolidaria.Shared.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCampaignImagemAndUniqueTitulo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Imagem",
                table: "campaigns",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TituloNormalizado",
                table: "campaigns",
                type: "character varying(160)",
                maxLength: 160,
                nullable: false,
                defaultValue: "");

            // Backfill: a coluna nasce vazia para todas as linhas existentes, o que faria a criacao
            // do indice UNICO abaixo falhar na hora (N linhas com ''). Reproduz em SQL exatamente o
            // que Campaign.NormalizarTitulo faz em C#: trim, espacos internos colapsados, minusculas.
            migrationBuilder.Sql(
                """
                UPDATE campaigns
                SET "TituloNormalizado" = lower(btrim(regexp_replace("Titulo", '\s+', ' ', 'g')));
                """);

            // Bancos anteriores a esta migration podiam ter titulos repetidos (nao havia restricao).
            // Em vez de apagar ou renomear campanhas do usuario, desambigua apenas a CHAVE de
            // unicidade, preservando o "Titulo" original intacto: a mais antiga fica com a forma
            // canonica e as demais recebem sufixo. O gestor renomeia depois se quiser.
            migrationBuilder.Sql(
                """
                WITH duplicadas AS (
                    SELECT "Id",
                           row_number() OVER (
                               PARTITION BY "TituloNormalizado"
                               ORDER BY "CriadaEm", "Id") AS posicao
                    FROM campaigns
                )
                UPDATE campaigns c
                SET "TituloNormalizado" = left(c."TituloNormalizado", 150) || ' (' || d.posicao || ')'
                FROM duplicadas d
                WHERE c."Id" = d."Id" AND d.posicao > 1;
                """);

            migrationBuilder.CreateIndex(
                name: "ix_campaigns_titulo_normalizado",
                table: "campaigns",
                column: "TituloNormalizado",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_campaigns_titulo_normalizado",
                table: "campaigns");

            migrationBuilder.DropColumn(
                name: "Imagem",
                table: "campaigns");

            migrationBuilder.DropColumn(
                name: "TituloNormalizado",
                table: "campaigns");
        }
    }
}
