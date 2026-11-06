using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ntm.Gateway.Data.Migrations.Config
{
    /// <summary>
    /// api_tokens → api_keys。脚手架生成的是删表重建，会丢掉所有 API 密钥，这里手写成原地改名：
    /// SQLite 3.25 起 ALTER TABLE … RENAME TO 保留数据和索引，索引名单独改。
    /// </summary>
    public partial class RenameApiKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameTable(
                name: "api_tokens",
                newName: "api_keys");

            migrationBuilder.RenameIndex(
                name: "IX_api_tokens_Fingerprint",
                table: "api_keys",
                newName: "IX_api_keys_Fingerprint");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameIndex(
                name: "IX_api_keys_Fingerprint",
                table: "api_keys",
                newName: "IX_api_tokens_Fingerprint");

            migrationBuilder.RenameTable(
                name: "api_keys",
                newName: "api_tokens");
        }
    }
}
