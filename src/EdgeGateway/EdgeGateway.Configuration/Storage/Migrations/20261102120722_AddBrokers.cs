using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EdgeGateway.Configuration.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddBrokers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Brokers",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Host = table.Column<string>(type: "TEXT", nullable: false),
                    Port = table.Column<int>(type: "INTEGER", nullable: false),
                    UseTls = table.Column<bool>(type: "INTEGER", nullable: false),
                    CertificateLocation = table.Column<string>(type: "TEXT", nullable: true),
                    CertificateSha256 = table.Column<string>(type: "TEXT", nullable: true),
                    KeepAliveSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    ConnectTimeoutSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    RetryDelaySeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    MaxRetryDelaySeconds = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Brokers", x => x.Id);
                    table.CheckConstraint("CK_Brokers_Port", "Port BETWEEN 1 AND 65535");
                });

            migrationBuilder.InsertData(
                table: "Brokers",
                columns: new[] { "Id", "CertificateLocation", "CertificateSha256", "ConnectTimeoutSeconds", "Host", "KeepAliveSeconds", "MaxRetryDelaySeconds", "Port", "RetryDelaySeconds", "UseTls" },
                values: new object[] { "local", "certs/broker.crt", null, 10, "127.0.0.1", 60, 30, 8883, 2, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Brokers");
        }
    }
}
