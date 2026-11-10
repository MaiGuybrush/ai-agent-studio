using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiAgentStudio.Api.Tickets.Migrations
{
    /// <inheritdoc />
    public partial class ToolSpecDraft : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_workflow_draft",
                table: "workflow_draft");

            migrationBuilder.AddColumn<string>(
                name: "Doc",
                table: "workflow_draft",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "steps");

            migrationBuilder.AddPrimaryKey(
                name: "PK_workflow_draft",
                table: "workflow_draft",
                columns: new[] { "TicketId", "Doc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_workflow_draft",
                table: "workflow_draft");

            migrationBuilder.DropColumn(
                name: "Doc",
                table: "workflow_draft");

            migrationBuilder.AddPrimaryKey(
                name: "PK_workflow_draft",
                table: "workflow_draft",
                column: "TicketId");
        }
    }
}
