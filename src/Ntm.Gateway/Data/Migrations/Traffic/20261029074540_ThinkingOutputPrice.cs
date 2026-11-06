using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ntm.Gateway.Data.Migrations.Traffic
{
    /// <inheritdoc />
    public partial class ThinkingOutputPrice : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "SawThinking",
                table: "attempts",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SawThinking",
                table: "attempts");
        }
    }
}
