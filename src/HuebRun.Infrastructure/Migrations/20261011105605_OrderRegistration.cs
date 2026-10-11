using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HuebRun.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class OrderRegistration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateSequence(
                name: "order_no_seq");

            migrationBuilder.AddColumn<int>(
                name: "LastBibNumber",
                table: "EventDistances",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastBibNumber",
                table: "EventDistances");

            migrationBuilder.DropSequence(
                name: "order_no_seq");
        }
    }
}
