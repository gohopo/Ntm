using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ntm.Gateway.Data.Migrations.Config
{
    /// <inheritdoc />
    public partial class PriceRows : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "prices",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Provider = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    UpstreamModel = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    Source = table.Column<int>(type: "INTEGER", nullable: false),
                    MatchedModelId = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    InputPrice = table.Column<decimal>(type: "TEXT", nullable: true),
                    CacheReadPrice = table.Column<decimal>(type: "TEXT", nullable: true),
                    CacheWrite5mPrice = table.Column<decimal>(type: "TEXT", nullable: true),
                    CacheWrite1hPrice = table.Column<decimal>(type: "TEXT", nullable: true),
                    OutputPrice = table.Column<decimal>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    LongContext = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_prices", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_prices_Provider_UpstreamModel",
                table: "prices",
                columns: new[] { "Provider", "UpstreamModel" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "prices");
        }
    }
}
