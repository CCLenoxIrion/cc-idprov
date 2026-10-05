using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Onboarding.Core.Configuration;
using Onboarding.Core.Domain;
using Onboarding.Data.Conversion;
using Onboarding.Data.Entities;
using Onboarding.Data.Seed;

namespace Onboarding.Data;

public sealed class OnboardingDbContext(DbContextOptions<OnboardingDbContext> options) : DbContext(options)
{
    public DbSet<Request> Requests => Set<Request>();
    public DbSet<RequestStep> RequestSteps => Set<RequestStep>();
    public DbSet<ChecklistItem> ChecklistItems => Set<ChecklistItem>();
    public DbSet<ChecklistTemplate> ChecklistTemplates => Set<ChecklistTemplate>();
    public DbSet<AuditEntry> AuditLog => Set<AuditEntry>();
    public DbSet<GlobalConfigRecord> GlobalConfig => Set<GlobalConfigRecord>();
    public DbSet<AreaConfig> Areas => Set<AreaConfig>();
    public DbSet<DepartmentConfig> Departments => Set<DepartmentConfig>();
    public DbSet<ConfigHistoryEntry> ConfigHistory => Set<ConfigHistoryEntry>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<UtcTicksConverter>();
        configurationBuilder.Properties<RequestType>().HaveConversion<string>().HaveMaxLength(32);
        configurationBuilder.Properties<RequestStatus>().HaveConversion<string>().HaveMaxLength(32);
        configurationBuilder.Properties<StepStatus>().HaveConversion<string>().HaveMaxLength(32);
        configurationBuilder.Properties<ChecklistItemStatus>().HaveConversion<string>().HaveMaxLength(32);
        configurationBuilder.Properties<ChecklistScope>().HaveConversion<string>().HaveMaxLength(32);
        configurationBuilder.Properties<ConfigChangeType>().HaveConversion<string>().HaveMaxLength(32);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<Request>(b =>
        {
            b.ToTable("Requests");
            b.HasKey(r => r.Id);
            b.Property(r => r.Version).IsConcurrencyToken();
            b.HasIndex(r => r.Status);
            b.ComplexProperty(r => r.Input, input =>
            {
                input.Property(i => i.FirstName).HasMaxLength(200);
                input.Property(i => i.LastName).HasMaxLength(200);
                input.Property(i => i.Mail).HasMaxLength(256);
                input.Property(i => i.Extension).HasMaxLength(4);
            });
            Json(b.Property(r => r.IdentityOverride));
            Json(b.Property(r => r.Derived));
            Json(b.Property(r => r.ConfigSnapshot));
            b.Ignore(r => r.IsClosed);
            b.HasMany(r => r.Steps).WithOne().HasForeignKey(s => s.RequestId).OnDelete(DeleteBehavior.Restrict);
            b.HasMany(r => r.Checklist).WithOne().HasForeignKey(c => c.RequestId).OnDelete(DeleteBehavior.Restrict);
            b.Navigation(r => r.Steps).AutoInclude();
            b.Navigation(r => r.Checklist).AutoInclude();
        });

        modelBuilder.Entity<RequestStep>(b =>
        {
            b.ToTable("RequestSteps");
            b.HasKey(s => s.Id);
            b.Property(s => s.StepKey).HasMaxLength(64);
            b.Property(s => s.Version).IsConcurrencyToken();
            b.HasIndex(s => new { s.RequestId, s.StepKey }).IsUnique();
            b.HasIndex(s => new { s.Status, s.NextAttemptAt });
        });

        modelBuilder.Entity<ChecklistItem>(b =>
        {
            b.ToTable("ChecklistItems");
            b.HasKey(c => c.Id);
            b.Property(c => c.Title).HasMaxLength(200);
            b.Ignore(c => c.IsSatisfied);
        });

        modelBuilder.Entity<ChecklistTemplate>(b =>
        {
            b.ToTable("ChecklistTemplates");
            b.HasKey(t => t.Id);
            b.Property(t => t.Title).HasMaxLength(200);
            b.Property(t => t.Version).IsConcurrencyToken();
        });

        modelBuilder.Entity<AuditEntry>(b =>
        {
            b.ToTable("AuditLog");
            b.HasKey(a => a.Id);
            b.Property(a => a.Action).HasMaxLength(64);
            b.Property(a => a.StepKey).HasMaxLength(64);
            b.HasIndex(a => a.RequestId);
            b.HasIndex(a => a.Timestamp);
        });

        modelBuilder.Entity<GlobalConfigRecord>(b =>
        {
            b.ToTable("GlobalConfig");
            b.HasKey(g => g.Id);
            b.Property(g => g.Id).ValueGeneratedNever();
            b.Property(g => g.Version).IsConcurrencyToken();
            Json(b.Property(g => g.Settings));
        });

        modelBuilder.Entity<AreaConfig>(b =>
        {
            b.ToTable("Areas");
            b.HasKey(a => a.Id);
            b.Property(a => a.Name).HasMaxLength(64);
            b.HasIndex(a => a.Name).IsUnique();
            b.Property(a => a.Version).IsConcurrencyToken();
        });

        modelBuilder.Entity<DepartmentConfig>(b =>
        {
            b.ToTable("Departments");
            b.HasKey(d => d.Id);
            b.Property(d => d.Name).HasMaxLength(128);
            b.HasIndex(d => d.Name).IsUnique();
            b.Property(d => d.Version).IsConcurrencyToken();
            Json(b.Property(d => d.AdGroups));
            Json(b.Property(d => d.Licenses));
            Json(b.Property(d => d.SharedMailboxes));
            Json(b.Property(d => d.LogonScript));
            Json(b.Property(d => d.Teams));
        });

        modelBuilder.Entity<ConfigHistoryEntry>(b =>
        {
            b.ToTable("ConfigHistory");
            b.HasKey(h => h.Id);
            b.Property(h => h.EntityType).HasMaxLength(64);
            b.Property(h => h.EntityId).HasMaxLength(64);
            b.HasIndex(h => new { h.EntityType, h.EntityId });
        });

        SeedData.Apply(modelBuilder);
    }

    private static void Json<T>(PropertyBuilder<T> property)
    {
        property.HasConversion(new JsonValueConverter<T>(), new JsonValueComparer<T>());
    }
}
