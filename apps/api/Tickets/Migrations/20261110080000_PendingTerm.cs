using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AiAgentStudio.Api.Tickets.Migrations
{
    /// <inheritdoc />
    public partial class PendingTerm : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "pending_term",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AliasesJson = table.Column<string>(type: "jsonb", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ConfirmedByEmpNo = table.Column<string>(type: "text", nullable: true),
                    Explanation = table.Column<string>(type: "text", nullable: true),
                    GiteaIssueNumber = table.Column<int>(type: "integer", nullable: false),
                    GiteaIssueUrl = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<string>(type: "text", nullable: true),
                    Term = table.Column<string>(type: "text", nullable: true),
                    TicketId = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pending_term", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_pending_term_Term",
                table: "pending_term",
                column: "Term");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "pending_term");
        }
    }
}
