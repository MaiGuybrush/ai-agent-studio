using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AiAgentStudio.Api.Tickets.Migrations
{
    /// <inheritdoc />
    public partial class InitialTickets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "requirement_ticket",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    CimEngineerEmpNo = table.Column<string>(type: "text", nullable: true),
                    CimEngineerName = table.Column<string>(type: "text", nullable: true),
                    BusinessUserEmpNo = table.Column<string>(type: "text", nullable: true),
                    BusinessUserName = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<string>(type: "text", nullable: true),
                    CancelReason = table.Column<string>(type: "text", nullable: true),
                    StatusBeforeCancel = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_requirement_ticket", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ticket_event",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TicketId = table.Column<string>(type: "text", nullable: true),
                    Type = table.Column<string>(type: "text", nullable: true),
                    ActorEmpNo = table.Column<string>(type: "text", nullable: true),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    FromValue = table.Column<string>(type: "text", nullable: true),
                    ToValue = table.Column<string>(type: "text", nullable: true),
                    Reason = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ticket_event", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ticket_event_TicketId",
                table: "ticket_event",
                column: "TicketId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "requirement_ticket");

            migrationBuilder.DropTable(
                name: "ticket_event");
        }
    }
}
