using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HuebRun.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class PaymentReviewNote : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ReviewNote",
                table: "Payments",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ReviewNote",
                table: "Payments");
        }
    }
}
