using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ntm.Gateway.Data.Migrations.Traffic
{
    /// <inheritdoc />
    public partial class AttemptPriceSnapshot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "CacheReadPrice",
                table: "attempts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "CacheWrite1hPrice",
                table: "attempts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "CacheWrite5mPrice",
                table: "attempts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "InputPrice",
                table: "attempts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "LongContextOver",
                table: "attempts",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "OutputPrice",
                table: "attempts",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CacheReadPrice",
                table: "attempts");

            migrationBuilder.DropColumn(
                name: "CacheWrite1hPrice",
                table: "attempts");

            migrationBuilder.DropColumn(
                name: "CacheWrite5mPrice",
                table: "attempts");

            migrationBuilder.DropColumn(
                name: "InputPrice",
                table: "attempts");

            migrationBuilder.DropColumn(
                name: "LongContextOver",
                table: "attempts");

            migrationBuilder.DropColumn(
                name: "OutputPrice",
                table: "attempts");
        }
    }
}
