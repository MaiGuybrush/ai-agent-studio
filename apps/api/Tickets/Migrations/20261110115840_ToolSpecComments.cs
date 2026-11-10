using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AiAgentStudio.Api.Tickets.Migrations
{
    /// <inheritdoc />
    public partial class ToolSpecComments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "review_assignment",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TicketId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    SpecId = table.Column<string>(type: "text", nullable: true),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    FromEmpNo = table.Column<string>(type: "text", nullable: true),
                    ToEmpNo = table.Column<string>(type: "text", nullable: true),
                    ByEmpNo = table.Column<string>(type: "text", nullable: true),
                    Reason = table.Column<string>(type: "text", nullable: true),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_review_assignment", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "tool_spec_comment",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TicketId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    SpecId = table.Column<string>(type: "text", nullable: true),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    AuthorEmpNo = table.Column<string>(type: "text", nullable: true),
                    Body = table.Column<string>(type: "text", nullable: true),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tool_spec_comment", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_review_assignment_TicketId_SpecId",
                table: "review_assignment",
                columns: new[] { "TicketId", "SpecId" });

            migrationBuilder.CreateIndex(
                name: "IX_tool_spec_comment_TicketId_SpecId",
                table: "tool_spec_comment",
                columns: new[] { "TicketId", "SpecId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "review_assignment");

            migrationBuilder.DropTable(
                name: "tool_spec_comment");
        }
    }
}
