using Trader.Core.Alerts;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Trader.Core.Journal;
using Trader.Core.Models;
using Trader.Core.Projection;
using Trader.Core.Simulator;

namespace Trader.Infrastructure.Persistence;

public sealed class TraderDbContext(DbContextOptions<TraderDbContext> options) : IdentityDbContext<AppUser>(options)
{
    public DbSet<Instrument> Instruments => Set<Instrument>();
    public DbSet<ChartDrawing> ChartDrawings => Set<ChartDrawing>();
    public DbSet<Article> Articles => Set<Article>();
    public DbSet<Suggestion> Suggestions => Set<Suggestion>();
    public DbSet<AppSetting> AppSettings => Set<AppSetting>();
    public DbSet<MoveExplanationRecord> MoveExplanations => Set<MoveExplanationRecord>();
    public DbSet<PaperTrade> PaperTrades => Set<PaperTrade>();
    public DbSet<AlertRule> AlertRules => Set<AlertRule>();
    public DbSet<JournalTrade> JournalTrades => Set<JournalTrade>();
    public DbSet<Alert> Alerts => Set<Alert>();
    public DbSet<PriceAlert> PriceAlerts => Set<PriceAlert>();
    public DbSet<ProjectionRecord> ProjectionRecords => Set<ProjectionRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        ValueConverter<DateTimeOffset, long> utcTicks = new(
            value => value.UtcTicks,
            ticks => new DateTimeOffset(ticks, TimeSpan.Zero));

        modelBuilder.Entity<Article>(entity =>
        {
            entity.HasIndex(a => a.UrlHash).IsUnique();
            entity.HasIndex(a => a.TitleHash);
            entity.HasIndex(a => a.PublishedAt);
            entity.Property(a => a.PublishedAt).HasConversion(utcTicks);
            entity.Property(a => a.IngestedAt).HasConversion(utcTicks);
            entity.Property(a => a.Url).HasMaxLength(2048).IsRequired();
            entity.Property(a => a.UrlHash).HasMaxLength(64).IsRequired();
            entity.Property(a => a.TitleHash).HasMaxLength(64).IsRequired();
            entity.Property(a => a.Title).HasMaxLength(512).IsRequired();
            entity.Property(a => a.Source).HasMaxLength(128).IsRequired();
            entity.Property(a => a.SourceType).HasConversion<string>().HasMaxLength(16);
            entity.HasMany(a => a.Symbols).WithOne().HasForeignKey(s => s.ArticleId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ArticleSymbol>(entity =>
        {
            entity.HasKey(s => new { s.ArticleId, s.Symbol });
            entity.HasIndex(s => s.Symbol);
            entity.Property(s => s.Symbol).HasMaxLength(32).IsRequired();
        });

        modelBuilder.Entity<AlertRule>(entity =>
        {
            entity.HasIndex(r => r.UserId);
            entity.Property(r => r.UserId).HasMaxLength(450).IsRequired();
            entity.Property(r => r.Name).HasMaxLength(64).IsRequired();
            entity.Property(r => r.Symbol).HasMaxLength(32);
            entity.Property(r => r.AssetClass).HasConversion<string>().HasMaxLength(16);
            entity.Property(r => r.Direction).HasConversion<string>().HasMaxLength(8);
            entity.Property(r => r.EventKind).HasConversion<string>().HasMaxLength(32);
            entity.Property(r => r.Sentiment).HasConversion<string>().HasMaxLength(8);
            entity.Property(r => r.CreatedAt).HasConversion(utcTicks);
        });

        modelBuilder.Entity<PaperTrade>(entity =>
        {
            entity.HasIndex(t => new { t.UserId, t.Status });
            entity.Property(t => t.UserId).HasMaxLength(450).IsRequired();
            entity.Property(t => t.Symbol).HasMaxLength(32).IsRequired();
            entity.Property(t => t.Name).HasMaxLength(128).IsRequired();
            entity.Property(t => t.Direction).HasConversion<string>().HasMaxLength(8);
            entity.Property(t => t.Status).HasConversion<string>().HasMaxLength(16);
            entity.Property(t => t.OpenedAt).HasConversion(utcTicks);
            entity.Property(t => t.ClosedAt).HasConversion(utcTicks);
        });

        modelBuilder.Entity<MoveExplanationRecord>(entity =>
        {
            entity.HasIndex(r => new { r.Symbol, r.From, r.To });
            entity.Property(r => r.Symbol).HasMaxLength(32).IsRequired();
            entity.Property(r => r.NarrativeStatus).HasMaxLength(16).IsRequired();
            entity.Property(r => r.From).HasConversion(utcTicks);
            entity.Property(r => r.To).HasConversion(utcTicks);
            entity.Property(r => r.CreatedAt).HasConversion(utcTicks);
        });

        modelBuilder.Entity<Suggestion>(entity =>
        {
            entity.HasIndex(s => s.Status);
            entity.HasIndex(s => s.Symbol);
            entity.Property(s => s.Symbol).HasMaxLength(32).IsRequired();
            entity.Property(s => s.Name).HasMaxLength(128).IsRequired();
            entity.Property(s => s.Direction).HasConversion<string>().HasMaxLength(8);
            entity.Property(s => s.Status).HasConversion<string>().HasMaxLength(16);
            entity.Property(s => s.Horizon).HasConversion<string>().HasMaxLength(16).HasDefaultValue(SuggestionHorizon.Swing);
            entity.Property(s => s.ShadowReason).HasMaxLength(200);
            entity.Property(s => s.Variant).HasMaxLength(8).HasDefaultValue("A");
            entity.Property(s => s.CreatedAt).HasConversion(utcTicks);
            entity.Property(s => s.ExpiresAt).HasConversion(utcTicks);
            entity.Property(s => s.ClosedAt).HasConversion(utcTicks);
        });

        modelBuilder.Entity<JournalTrade>(entity =>
        {
            entity.HasIndex(t => new { t.UserId, t.Status });
            entity.Property(t => t.UserId).HasMaxLength(450).IsRequired();
            entity.Property(t => t.Symbol).HasMaxLength(32).IsRequired();
            entity.Property(t => t.Name).HasMaxLength(128).IsRequired();
            entity.Property(t => t.Platform).HasMaxLength(64);
            entity.Property(t => t.Notes).HasMaxLength(2000);
            entity.Property(t => t.Setup).HasMaxLength(40);
            entity.Property(t => t.Tags).HasMaxLength(120);
            entity.Property(t => t.Emotion).HasMaxLength(20);
            entity.Property(t => t.Direction).HasConversion<string>().HasMaxLength(8);
            entity.Property(t => t.Status).HasConversion<string>().HasMaxLength(16);
            entity.Property(t => t.EntryTime).HasConversion(utcTicks);
            entity.Property(t => t.ExitTime).HasConversion(utcTicks);
        });

        modelBuilder.Entity<PriceAlert>(entity =>
        {
            entity.HasIndex(a => new { a.UserId, a.TriggeredAt });
            entity.Property(a => a.UserId).HasMaxLength(450).IsRequired();
            entity.Property(a => a.Symbol).HasMaxLength(32).IsRequired();
            entity.Property(a => a.Note).HasMaxLength(200);
            entity.Property(a => a.Condition).HasConversion<string>().HasMaxLength(8);
            entity.Property(a => a.CreatedAt).HasConversion(utcTicks);
            entity.Property(a => a.TriggeredAt).HasConversion(utcTicks);
        });

        modelBuilder.Entity<ProjectionRecord>(entity =>
        {
            entity.HasIndex(r => r.CreatedAt);
            entity.HasIndex(r => new { r.Symbol, r.TargetTime, r.HorizonMinutes }).IsUnique();
            entity.Property(r => r.Symbol).HasMaxLength(32).IsRequired();
            entity.Property(r => r.Name).HasMaxLength(128).IsRequired();
            entity.Property(r => r.PlanDirection).HasConversion<string>().HasMaxLength(8);
            entity.Property(r => r.CreatedAt).HasConversion(utcTicks);
            entity.Property(r => r.OriginTime).HasConversion(utcTicks);
            entity.Property(r => r.TargetTime).HasConversion(utcTicks);
            entity.Property(r => r.ResolvedAt).HasConversion(utcTicks);
        });

        modelBuilder.Entity<Alert>(entity =>
        {
            entity.Property(a => a.UserId).HasMaxLength(450);
            entity.HasIndex(a => a.CreatedAt);
            entity.Property(a => a.Symbol).HasMaxLength(32).IsRequired();
            entity.Property(a => a.Title).HasMaxLength(128).IsRequired();
            entity.Property(a => a.Message).HasMaxLength(256).IsRequired();
            entity.Property(a => a.Kind).HasConversion<string>().HasMaxLength(16);
            entity.Property(a => a.CreatedAt).HasConversion(utcTicks);
        });

        modelBuilder.Entity<AppSetting>(entity =>
        {
            entity.HasKey(s => s.Key);
            entity.Property(s => s.Key).HasMaxLength(64);
            entity.Property(s => s.Value).IsRequired();
        });

        modelBuilder.Entity<ChartDrawing>(entity =>
        {
            entity.HasIndex(d => d.Symbol).IsUnique();
            entity.Property(d => d.Symbol).HasMaxLength(32).IsRequired();
            entity.Property(d => d.Json).IsRequired();
        });

        modelBuilder.Entity<Instrument>(entity =>
        {
            entity.HasIndex(i => i.Symbol).IsUnique();
            entity.Property(i => i.Symbol).HasMaxLength(32).IsRequired();
            entity.Property(i => i.Name).HasMaxLength(128).IsRequired();
            entity.Property(i => i.Currency).HasMaxLength(8).IsRequired();
            entity.Property(i => i.AssetClass).HasConversion<string>().HasMaxLength(16);
            entity.HasData(InstrumentSeed.All);
        });
    }
}
