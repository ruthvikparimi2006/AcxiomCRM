using AcxiomCRM.Models;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace AcxiomCRM.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : IdentityDbContext<ApplicationUser>(options)
{
    const string CaseInsensitive = "Latin1_General_CI_AS";
    public const string AuditLogTrigger = "TR_AuditLogs_AppendOnly";

    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Lead> Leads => Set<Lead>();
    public DbSet<Opportunity> Opportunities => Set<Opportunity>();
    public DbSet<FollowUp> FollowUps => Set<FollowUp>();
    public DbSet<Activity> Activities => Set<Activity>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        builder.Properties<Enum>().HaveConversion<string>().HaveMaxLength(20);
        builder.Properties<decimal>().HavePrecision(18, 2);
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<Customer>(e =>
        {
            // Limitation: 6-digit padding; codes repeat past id 999,999 and the unique index rejects them.
            e.Property(c => c.CustomerCode)
                .HasComputedColumnSql("'CUS-' + RIGHT('000000' + CAST([CustomerId] AS varchar(10)), 6)", stored: true);
            e.HasIndex(c => c.CustomerCode).IsUnique();
            // §16 #9: duplicate = same Email (case-insensitive) or same Phone, including inactive customers.
            e.Property(c => c.Email).UseCollation(CaseInsensitive);
            e.HasIndex(c => c.Email).IsUnique();
            e.HasIndex(c => c.Phone).IsUnique();
        });

        builder.Entity<Lead>(e =>
        {
            e.Property(l => l.LeadCode)
                .HasComputedColumnSql("'LEAD-' + RIGHT('000000' + CAST([LeadId] AS varchar(10)), 6)", stored: true);
            e.HasIndex(l => l.LeadCode).IsUnique();
            e.ToTable(t => t.HasCheckConstraint("CK_Lead_ExpectedValue", "[ExpectedValue] BETWEEN 0 AND 100000000"));
            e.HasQueryFilter(l => !l.IsDeleted);
        });

        builder.Entity<Opportunity>(e =>
        {
            e.ToTable(t =>
            {
                t.HasCheckConstraint("CK_Opportunity_Amount", "[Amount] >= 0");
                t.HasCheckConstraint("CK_Opportunity_Probability", "[Probability] BETWEEN 0 AND 100");
            });
            e.HasQueryFilter(o => !o.IsDeleted);
        });

        builder.Entity<FollowUp>(e =>
        {
            e.ToTable(t => t.HasCheckConstraint("CK_FollowUp_OneRelatedRecord",
                "(CASE WHEN [CustomerId] IS NULL THEN 0 ELSE 1 END" +
                " + CASE WHEN [LeadId] IS NULL THEN 0 ELSE 1 END" +
                " + CASE WHEN [OpportunityId] IS NULL THEN 0 ELSE 1 END) = 1"));
            e.HasQueryFilter(f => !f.IsDeleted);
        });

        builder.Entity<Activity>(e =>
        {
            e.ToTable(t => t.HasCheckConstraint("CK_Activity_RelatedRecord",
                "[CustomerId] IS NOT NULL OR [LeadId] IS NOT NULL"));
            e.HasQueryFilter(a => !a.IsDeleted);
        });

        // AUD-04: the database itself refuses UPDATE/DELETE on audit rows (trigger created in the AuditLogAppendOnly
        // migration). Declaring it stops EF from using an OUTPUT clause on insert, which SQL Server forbids with triggers.
        builder.Entity<AuditLog>(e =>
        {
            e.HasIndex(a => a.CreatedDate);
            e.ToTable(t => t.HasTrigger(AuditLogTrigger));
        });

        // §16 #3: no cascading deletes between CRM records (Identity's own tables keep their defaults).
        foreach (var fk in builder.Model.GetEntityTypes()
                     .Where(t => t.ClrType.Namespace == typeof(Customer).Namespace)
                     .SelectMany(t => t.GetForeignKeys()))
            fk.DeleteBehavior = DeleteBehavior.Restrict;
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        RejectAuditLogChanges();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        RejectAuditLogChanges();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    // Audit records are append-only (AUD-04): rejected here first, and by the database trigger as a backstop.
    void RejectAuditLogChanges()
    {
        if (ChangeTracker.Entries<AuditLog>().Any(e => e.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("Audit log entries cannot be modified or deleted.");
    }
}
