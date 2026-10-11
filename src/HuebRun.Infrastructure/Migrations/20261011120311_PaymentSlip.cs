using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HuebRun.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class PaymentSlip : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "SlipImageUrl",
                table: "Payments",
                newName: "SlipObjectKey");

            migrationBuilder.AddColumn<int>(
                name: "CheckAttempts",
                table: "Payments",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "NextCheckAt",
                table: "Payments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "TransferredAt",
                table: "Payments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Payments_NextCheckAt",
                table: "Payments",
                column: "NextCheckAt",
                filter: "\"Status\" = 'Verifying'");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_OrderId_Verifying",
                table: "Payments",
                column: "OrderId",
                unique: true,
                filter: "\"Status\" = 'Verifying'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Payments_NextCheckAt",
                table: "Payments");

            migrationBuilder.DropIndex(
                name: "IX_Payments_OrderId_Verifying",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "CheckAttempts",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "NextCheckAt",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "TransferredAt",
                table: "Payments");

            migrationBuilder.RenameColumn(
                name: "SlipObjectKey",
                table: "Payments",
                newName: "SlipImageUrl");
        }
    }
}
