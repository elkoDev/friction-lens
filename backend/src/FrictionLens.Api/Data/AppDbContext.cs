using FrictionLens.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace FrictionLens.Api.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Workspace> Workspaces => Set<Workspace>();
    public DbSet<TrackedEntity> TrackedEntities => Set<TrackedEntity>();
    public DbSet<SourceConfig> SourceConfigs => Set<SourceConfig>();
    public DbSet<SourceSyncState> SourceSyncStates => Set<SourceSyncState>();
    public DbSet<RawDocument> RawDocuments => Set<RawDocument>();
    public DbSet<DecisionInsight> DecisionInsights => Set<DecisionInsight>();
    public DbSet<FrictionDriver> FrictionDrivers => Set<FrictionDriver>();
    public DbSet<CanonicalThemeRecord> CanonicalThemes => Set<CanonicalThemeRecord>();
    public DbSet<ThemeAlias> ThemeAliases => Set<ThemeAlias>();
    public DbSet<IngestionJob> IngestionJobs => Set<IngestionJob>();
    public DbSet<IngestionDeadLetter> IngestionDeadLetters => Set<IngestionDeadLetter>();
    public DbSet<TakedownTombstone> TakedownTombstones => Set<TakedownTombstone>();
    public DbSet<DataImportAttestation> DataImportAttestations => Set<DataImportAttestation>();
    public DbSet<UsageLedgerEntry> UsageLedger => Set<UsageLedgerEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Workspace>(e =>
        {
            e.HasKey(w => w.Id);
            e.HasIndex(w => w.Slug).IsUnique();
            e.Property(w => w.Name).HasMaxLength(160);
            e.Property(w => w.Slug).HasMaxLength(160);
        });

        modelBuilder.Entity<TrackedEntity>(e =>
        {
            e.HasKey(t => t.Id);
            e.HasOne(t => t.Workspace)
                .WithMany(w => w.Entities)
                .HasForeignKey(t => t.WorkspaceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SourceConfig>(e =>
        {
            e.HasKey(s => s.Id);
            e.HasOne(s => s.Workspace)
                .WithMany(w => w.Sources)
                .HasForeignKey(s => s.WorkspaceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SourceSyncState>(e =>
        {
            e.HasKey(s => s.SourceConfigId);
            e.HasOne(s => s.SourceConfig)
                .WithOne(c => c.SyncState)
                .HasForeignKey<SourceSyncState>(s => s.SourceConfigId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<RawDocument>(e =>
        {
            e.HasKey(d => d.Id);
            e.HasIndex(d => new { d.WorkspaceId, d.ContentHash }).IsUnique();
            e.HasIndex(d => new { d.WorkspaceId, d.SourceType, d.ExternalId });
            e.HasIndex(d => new { d.WorkspaceId, d.SimHash64 });
            e.HasOne(d => d.Workspace)
                .WithMany(w => w.Documents)
                .HasForeignKey(d => d.WorkspaceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<DecisionInsight>(e =>
        {
            e.HasKey(i => i.Id);
            e.HasIndex(i => new { i.WorkspaceId, i.ObservedAt });
            e.HasIndex(i => new { i.WorkspaceId, i.GroundingVerified, i.IsQuarantined });
            e.HasOne(i => i.Workspace)
                .WithMany(w => w.Insights)
                .HasForeignKey(i => i.WorkspaceId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(i => i.RawDocument)
                .WithMany()
                .HasForeignKey(i => i.RawDocumentId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<FrictionDriver>(e =>
        {
            e.HasKey(f => f.Id);
            e.HasIndex(f => new { f.CanonicalTheme, f.TargetEntity });
            e.HasOne(f => f.DecisionInsight)
                .WithMany(i => i.Drivers)
                .HasForeignKey(f => f.DecisionInsightId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(f => f.CanonicalThemeRecord)
                .WithMany()
                .HasForeignKey(f => f.CanonicalThemeId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<CanonicalThemeRecord>(e =>
        {
            e.HasKey(t => t.Id);
            e.HasIndex(t => new { t.WorkspaceId, t.Slug }).IsUnique();
            e.HasOne(t => t.Workspace)
                .WithMany(w => w.CanonicalThemes)
                .HasForeignKey(t => t.WorkspaceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ThemeAlias>(e =>
        {
            e.HasKey(a => a.Id);
            e.HasIndex(a => new { a.WorkspaceId, a.NormalizedPhrase }).IsUnique();
            e.HasOne(a => a.CanonicalTheme)
                .WithMany(t => t.Aliases)
                .HasForeignKey(a => a.CanonicalThemeId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<IngestionJob>(e =>
        {
            e.HasKey(j => j.Id);
            e.HasIndex(j => new { j.Status, j.CreatedAt });
            e.HasOne(j => j.Workspace)
                .WithMany(w => w.Jobs)
                .HasForeignKey(j => j.WorkspaceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<IngestionDeadLetter>(e =>
        {
            e.HasKey(d => d.Id);
            e.HasIndex(d => new { d.WorkspaceId, d.OccurredAt });
        });

        modelBuilder.Entity<TakedownTombstone>(e =>
        {
            e.HasKey(t => t.Id);
            e.HasIndex(t => t.NormalizedUrlHash);
            e.HasIndex(t => t.ContentHash);
            e.HasIndex(t => t.ExternalId);
        });

        modelBuilder.Entity<DataImportAttestation>(e =>
        {
            e.HasKey(a => a.Id);
            e.HasIndex(a => new { a.WorkspaceId, a.AttestedAt });
        });

        modelBuilder.Entity<UsageLedgerEntry>(e =>
        {
            e.HasKey(u => u.Id);
            e.HasIndex(u => new { u.WorkspaceId, u.CreatedAt });
        });

        // Pure UTC ticks converter on SQLite for lossless, offset-safe chronological storage while Postgres uses native timestamptz.
        if (Database.IsSqlite())
        {
            var utcTicksConverter = new ValueConverter<DateTimeOffset, long>(
                v => v.UtcTicks,
                v => new DateTimeOffset(v, TimeSpan.Zero));

            var nullableUtcTicksConverter = new ValueConverter<DateTimeOffset?, long?>(
                v => v.HasValue ? v.Value.UtcTicks : null,
                v => v.HasValue ? new DateTimeOffset(v.Value, TimeSpan.Zero) : null);

            foreach (var entityType in modelBuilder.Model.GetEntityTypes())
            {
                foreach (var prop in entityType.GetProperties())
                {
                    if (prop.ClrType == typeof(DateTimeOffset))
                        prop.SetValueConverter(utcTicksConverter);
                    else if (prop.ClrType == typeof(DateTimeOffset?))
                        prop.SetValueConverter(nullableUtcTicksConverter);
                }
            }
        }
    }
}
