using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ntm.Gateway.Data.Migrations.Traffic
{
    /// <inheritdoc />
    public partial class Restrictions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BlockDetail",
                table: "requests",
                type: "TEXT",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "BlockedBy",
                table: "requests",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BlockDetail",
                table: "requests");

            migrationBuilder.DropColumn(
                name: "BlockedBy",
                table: "requests");
        }
    }
}
