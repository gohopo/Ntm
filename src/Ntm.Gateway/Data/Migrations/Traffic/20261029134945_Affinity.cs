using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ntm.Gateway.Data.Migrations.Traffic
{
    /// <inheritdoc />
    public partial class Affinity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Affinity",
                table: "requests",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Affinity",
                table: "requests");
        }
    }
}
