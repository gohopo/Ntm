using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ntm.Gateway.Data.Migrations.Config
{
    /// <inheritdoc />
    public partial class ThinkingOutputPrice : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "ThinkingOutputPrice",
                table: "prices",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ThinkingOutputPrice",
                table: "prices");
        }
    }
}
