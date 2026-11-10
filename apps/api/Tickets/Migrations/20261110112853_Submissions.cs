using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AiAgentStudio.Api.Tickets.Migrations
{
    /// <inheritdoc />
    public partial class Submissions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "submission",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TicketId = table.Column<string>(type: "text", nullable: true),
                    Number = table.Column<int>(type: "integer", nullable: false),
                    WorkflowJson = table.Column<string>(type: "jsonb", nullable: true),
                    SubmittedByEmpNo = table.Column<string>(type: "text", nullable: true),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_submission", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "tool_spec",
                columns: table => new
                {
                    TicketId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SpecId = table.Column<string>(type: "text", nullable: false),
                    Kind = table.Column<string>(type: "text", nullable: true),
                    State = table.Column<string>(type: "text", nullable: true),
                    CurrentRevision = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tool_spec", x => new { x.TicketId, x.SpecId });
                });

            migrationBuilder.CreateTable(
                name: "tool_spec_revision",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TicketId = table.Column<string>(type: "text", nullable: true),
                    SpecId = table.Column<string>(type: "text", nullable: true),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    SubmissionNumber = table.Column<int>(type: "integer", nullable: false),
                    ContentJson = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tool_spec_revision", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_submission_TicketId_Number",
                table: "submission",
                columns: new[] { "TicketId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_tool_spec_revision_TicketId_SpecId_Revision",
                table: "tool_spec_revision",
                columns: new[] { "TicketId", "SpecId", "Revision" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "submission");

            migrationBuilder.DropTable(
                name: "tool_spec");

            migrationBuilder.DropTable(
                name: "tool_spec_revision");
        }
    }
}
