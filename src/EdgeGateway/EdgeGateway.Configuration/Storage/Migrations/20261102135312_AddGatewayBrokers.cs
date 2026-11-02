using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EdgeGateway.Configuration.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddGatewayBrokers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GatewayBrokers",
                columns: table => new
                {
                    GatewayId = table.Column<string>(type: "TEXT", nullable: false),
                    BrokerId = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GatewayBrokers", x => x.GatewayId);
                    table.ForeignKey(
                        name: "FK_GatewayBrokers_Brokers_BrokerId",
                        column: x => x.BrokerId,
                        principalTable: "Brokers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GatewayBrokers_BrokerId",
                table: "GatewayBrokers",
                column: "BrokerId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GatewayBrokers");
        }
    }
}
