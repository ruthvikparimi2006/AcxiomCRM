using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AcxiomCRM.Data.Migrations
{
    /// <inheritdoc />
    // AUD-04: audit rows can be inserted but never changed or removed, whatever path the SQL comes from.
    public partial class AuditLogAppendOnly : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // EXEC runs CREATE TRIGGER as its own batch, which SQL Server requires; without it the
            // idempotent deployment script (which wraps each migration in IF ... BEGIN ... END) would fail.
            migrationBuilder.Sql("""
                EXEC(N'CREATE TRIGGER [dbo].[TR_AuditLogs_AppendOnly] ON [dbo].[AuditLogs]
                INSTEAD OF UPDATE, DELETE
                AS
                BEGIN
                    THROW 50001, ''Audit log entries are append-only and cannot be modified or deleted.'', 1;
                END');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("EXEC(N'DROP TRIGGER [dbo].[TR_AuditLogs_AppendOnly]');");
        }
    }
}
