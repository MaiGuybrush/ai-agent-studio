using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AiAgentStudio.Api.Tickets.Migrations
{
    /// <inheritdoc />
    public partial class Interviews : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "agent_run",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RunId = table.Column<string>(type: "text", nullable: true),
                    TicketId = table.Column<string>(type: "text", nullable: true),
                    EmpNo = table.Column<string>(type: "text", nullable: true),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EndedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Result = table.Column<string>(type: "text", nullable: true),
                    Model = table.Column<string>(type: "text", nullable: true),
                    InputTokens = table.Column<long>(type: "bigint", nullable: true),
                    OutputTokens = table.Column<long>(type: "bigint", nullable: true),
                    Requests = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_run", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "interview",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TicketId = table.Column<string>(type: "text", nullable: true),
                    ReopenReason = table.Column<string>(type: "text", nullable: true),
                    OpeningContext = table.Column<string>(type: "text", nullable: true),
                    ConversationJson = table.Column<string>(type: "jsonb", nullable: true),
                    IsRunning = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_interview", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_agent_run_TicketId",
                table: "agent_run",
                column: "TicketId");

            migrationBuilder.CreateIndex(
                name: "IX_interview_TicketId",
                table: "interview",
                column: "TicketId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agent_run");

            migrationBuilder.DropTable(
                name: "interview");
        }
    }
}
