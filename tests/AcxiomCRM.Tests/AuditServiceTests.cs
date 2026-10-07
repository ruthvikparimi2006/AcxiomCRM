using AcxiomCRM.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace AcxiomCRM.Tests;

[Collection("db")]
public class AuditServiceTests(DbFixture fx)
{
    async Task<long> WriteEntryAsync(object? newValue = null)
    {
        var user = await fx.AddUserAsync();
        using var db = fx.NewContext();
        await new AuditService(db, new HttpContextAccessor())
            .LogAsync("Customers", "Create", "Customer", "42", newValue: newValue, userId: user.Id);
        return await db.AuditLogs.MaxAsync(a => a.AuditLogId);
    }

    [Fact]
    public async Task LogAsync_writes_an_entry()
    {
        var id = await WriteEntryAsync(new { CustomerName = "Acme" });

        using var db = fx.NewContext();
        var entry = await db.AuditLogs.SingleAsync(a => a.AuditLogId == id);
        Assert.Equal("Create", entry.Action);
        Assert.Equal("Customers", entry.Module);
        Assert.Equal("42", entry.RecordId);
        Assert.Equal("Success", entry.Result);
        Assert.Contains("Acme", entry.NewValue);
    }

    [Fact]
    public async Task Secrets_are_never_stored()
    {
        var id = await WriteEntryAsync(new
        {
            UserName = "alice",
            Password = "P@ssw0rd!",
            PasswordHash = "AQAAAA...",
            ResetToken = "tok",
            Nested = new { SecurityStamp = "stamp", Keep = "yes" },
        });

        using var db = fx.NewContext();
        var json = (await db.AuditLogs.SingleAsync(a => a.AuditLogId == id)).NewValue!;
        Assert.Contains("alice", json);
        Assert.Contains("yes", json);
        foreach (var secret in new[] { "P@ssw0rd!", "AQAAAA", "tok", "stamp" })
            Assert.DoesNotContain(secret, json);
    }

    [Fact]
    public async Task Entries_cannot_be_updated_or_deleted()
    {
        var id = await WriteEntryAsync();

        using var db = fx.NewContext();
        var entry = await db.AuditLogs.SingleAsync(a => a.AuditLogId == id);
        entry.Action = "Tampered";
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());

        using var db2 = fx.NewContext();
        db2.AuditLogs.Remove(await db2.AuditLogs.SingleAsync(a => a.AuditLogId == id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => db2.SaveChangesAsync());
    }
}
