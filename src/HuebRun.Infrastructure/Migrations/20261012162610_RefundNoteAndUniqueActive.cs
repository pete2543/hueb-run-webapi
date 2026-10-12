using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HuebRun.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RefundNoteAndUniqueActive : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Refunds_PaymentId",
                table: "Refunds");

            migrationBuilder.AlterColumn<string>(
                name: "ExternalRef",
                table: "Refunds",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Note",
                table: "Refunds",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Refunds_PaymentId_Active",
                table: "Refunds",
                column: "PaymentId",
                unique: true,
                filter: "\"PaymentId\" IS NOT NULL AND \"Status\" <> 'Rejected'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Refunds_PaymentId_Active",
                table: "Refunds");

            migrationBuilder.DropColumn(
                name: "Note",
                table: "Refunds");

            migrationBuilder.AlterColumn<string>(
                name: "ExternalRef",
                table: "Refunds",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(100)",
                oldMaxLength: 100,
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Refunds_PaymentId",
                table: "Refunds",
                column: "PaymentId");
        }
    }
}
