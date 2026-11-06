using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ntm.Gateway.Data.Migrations.Config
{
    /// <inheritdoc />
    public partial class ChannelProxy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 明文的 ProxyUrl 从来没有接口能写入，删掉不丢数据；换成加密的整段地址（Q131）。
            migrationBuilder.DropColumn(
                name: "ProxyUrl",
                table: "channels");

            migrationBuilder.AddColumn<string>(
                name: "ProtectedProxyUrl",
                table: "channels",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ProtectedProxyUrl",
                table: "channels");

            migrationBuilder.AddColumn<string>(
                name: "ProxyUrl",
                table: "channels",
                type: "TEXT",
                maxLength: 500,
                nullable: true);
        }
    }
}
