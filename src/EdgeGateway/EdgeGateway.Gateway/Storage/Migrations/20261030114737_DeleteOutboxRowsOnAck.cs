using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EdgeGateway.Gateway.Storage.Migrations
{
    /// <inheritdoc />
    public partial class DeleteOutboxRowsOnAck : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Without SentAt every remaining row counts as unsent, so acknowledged rows must go first or they would
            // be published again. Their BrokerHistory totals were already counted when they were acknowledged.
            migrationBuilder.Sql("DELETE FROM Outbox WHERE SentAt IS NOT NULL;");

            migrationBuilder.DropIndex(
                name: "IX_Outbox_Unsent",
                table: "Outbox");

            migrationBuilder.DropColumn(
                name: "SentAt",
                table: "Outbox");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "SentAt",
                table: "Outbox",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Outbox_Unsent",
                table: "Outbox",
                column: "Id",
                filter: "SentAt IS NULL");
        }
    }
}
