using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ConexaoSolidaria.Shared.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCampaignCategoria : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Categoria",
                table: "campaigns",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "Outros");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Categoria",
                table: "campaigns");
        }
    }
}
