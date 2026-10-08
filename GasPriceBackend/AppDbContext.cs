using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Npgsql;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Guest> Guests => Set<Guest>();
    public DbSet<PlayerData> PlayerData => Set<PlayerData>();
    public DbSet<FuelPriceCache> FuelPriceCaches => Set<FuelPriceCache>();
    public DbSet<FuelAdjustment> FuelAdjustments => Set<FuelAdjustment>();
    public DbSet<DoeFuelPrice> DoeFuelPrices => Set<DoeFuelPrice>();
    public DbSet<DoePumpPriceReport> DoePumpPriceReports => Set<DoePumpPriceReport>();
    public DbSet<DoeImportJob> DoeImportJobs => Set<DoeImportJob>();
    public DbSet<DoePageCache> DoePageCaches => Set<DoePageCache>();
    public DbSet<FuelNews> FuelNews => Set<FuelNews>();
    public DbSet<FuelNewsRevision> FuelNewsRevisions => Set<FuelNewsRevision>();
    public DbSet<FuelNewsSubscription> FuelNewsSubscriptions => Set<FuelNewsSubscription>();
    public DbSet<FuelNewsDelivery> FuelNewsDeliveries => Set<FuelNewsDelivery>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<FuelNews>(e =>
        {
            e.ToTable("fuel_news");
            e.HasKey(n => n.Id);
            e.Property(n => n.Id).HasColumnName("id");
            e.Property(n => n.ImportKey).HasColumnName("import_key").HasMaxLength(100);
            e.Property(n => n.TopicKey).HasColumnName("topic_key").HasMaxLength(100);
            e.Property(n => n.Category).HasColumnName("category").HasMaxLength(20);
            e.Property(n => n.Status).HasColumnName("status").HasMaxLength(20);
            e.Property(n => n.ContentJson).HasColumnName("content_json").HasColumnType("jsonb");
            e.Property(n => n.ContentHash).HasColumnName("content_hash").HasMaxLength(64);
            e.Property(n => n.Revision).HasColumnName("revision");
            e.Property(n => n.PublishedAtUtc).HasColumnName("published_at_utc");
            e.Property(n => n.UpdatedAtUtc).HasColumnName("updated_at_utc");
            e.Property(n => n.ExpiresAtUtc).HasColumnName("expires_at_utc");
            e.Property(n => n.SupersededById).HasColumnName("superseded_by_id");
            e.HasIndex(n => n.ImportKey).IsUnique();
            e.HasIndex(n => new { n.PublishedAtUtc, n.Id });
            e.HasIndex(n => new { n.TopicKey, n.Status });
            e.HasOne<FuelNews>().WithMany().HasForeignKey(n => n.SupersededById).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<FuelNewsRevision>(e =>
        {
            e.ToTable("fuel_news_revision");
            e.HasKey(n => new { n.NewsId, n.Revision });
            e.Property(n => n.NewsId).HasColumnName("news_id");
            e.Property(n => n.Revision).HasColumnName("revision");
            e.Property(n => n.ContentJson).HasColumnName("content_json").HasColumnType("jsonb");
            e.Property(n => n.SavedAtUtc).HasColumnName("saved_at_utc");
            e.HasOne<FuelNews>().WithMany().HasForeignKey(n => n.NewsId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<FuelNewsSubscription>(e =>
        {
            e.ToTable("fuel_news_subscription");
            e.HasKey(n => n.AppInstanceId);
            e.Property(n => n.AppInstanceId).HasColumnName("app_instance_id");
            e.Property(n => n.PushToken).HasColumnName("push_token").HasMaxLength(230);
            e.Property(n => n.Enabled).HasColumnName("enabled");
            e.Property(n => n.IncludeForecasts).HasColumnName("include_forecasts");
            e.Property(n => n.UpdatedAtUtc).HasColumnName("updated_at_utc");
            e.HasIndex(n => n.PushToken).IsUnique();
            e.HasOne<Guest>().WithOne().HasForeignKey<FuelNewsSubscription>(n => n.AppInstanceId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<FuelNewsDelivery>(e =>
        {
            e.ToTable("fuel_news_delivery");
            e.HasKey(n => n.Id);
            e.Property(n => n.Id).HasColumnName("id");
            e.Property(n => n.NewsId).HasColumnName("news_id");
            e.Property(n => n.Revision).HasColumnName("revision");
            e.Property(n => n.AppInstanceId).HasColumnName("app_instance_id");
            e.Property(n => n.Status).HasColumnName("status").HasMaxLength(30);
            e.Property(n => n.Attempts).HasColumnName("attempts");
            e.Property(n => n.CreatedAtUtc).HasColumnName("created_at_utc");
            e.Property(n => n.NextAttemptAtUtc).HasColumnName("next_attempt_at_utc");
            e.Property(n => n.TicketId).HasColumnName("ticket_id").HasMaxLength(100);
            e.Property(n => n.Error).HasColumnName("error").HasMaxLength(100);
            e.HasIndex(n => new { n.NewsId, n.Revision, n.AppInstanceId }).IsUnique();
            e.HasIndex(n => new { n.Status, n.NextAttemptAtUtc });
            e.HasOne<FuelNewsRevision>().WithMany().HasForeignKey(n => new { n.NewsId, n.Revision }).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<FuelNewsSubscription>().WithMany().HasForeignKey(n => n.AppInstanceId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<DoePageCache>(e =>
        {
            e.ToTable("doe_page_cache");
            e.HasKey(p => new { p.ContentHash, p.ExtractorVersion, p.PageNumber });
            e.Property(p => p.ContentHash).HasColumnName("content_hash").HasMaxLength(64);
            e.Property(p => p.ExtractorVersion).HasColumnName("extractor_version").HasMaxLength(64);
            e.Property(p => p.PageNumber).HasColumnName("page_number");
            e.Property(p => p.PageCount).HasColumnName("page_count");
            e.Property(p => p.ExtractionJson).HasColumnName("extraction_json").HasColumnType("jsonb");
            e.Property(p => p.ExtractedAtUtc).HasColumnName("extracted_at_utc");
        });
        // Map to snake_case columns (the Postgres convention).
        modelBuilder.Entity<Guest>(e =>
        {
            e.ToTable("guests");
            e.HasKey(g => g.AppInstanceId);
            e.Property(g => g.AppInstanceId).HasColumnName("app_instance_id");
            e.Property(g => g.GuestCredentialHash).HasColumnName("guest_credential_hash");
            e.Property(g => g.AccessTokenHash).HasColumnName("access_token_hash");
            e.Property(g => g.AccessTokenExpiresAt).HasColumnName("access_token_expires_at");
            e.Property(g => g.LegacyToken).HasColumnName("legacy_token");
            e.Property(g => g.PlayerId)
                .HasColumnName("player_id");
            e.HasIndex(g => g.PlayerId).IsUnique();
            e.Property(g => g.PlayerName)
                .HasColumnName("player_name")
                .HasMaxLength(24);
            e.HasIndex(g => g.PlayerName).IsUnique();
            e.Property(g => g.IpAddress).HasColumnName("ip_address");
            e.Property(g => g.UserAgent).HasColumnName("user_agent");
            e.Property(g => g.DeviceType).HasColumnName("device_type");
            e.Property(g => g.CreatedAt).HasColumnName("created_at");
            e.Property(g => g.LastLoginAt).HasColumnName("last_login_at");
            e.Property(g => g.LoginCount).HasColumnName("login_count");
            e.Property(g => g.IsLoggedIn)
                .HasColumnName("is_logged_in")
                .HasDefaultValue(false);
            e.Property(g => g.LastLogoutAt).HasColumnName("last_logout_at");
            e.Property(g => g.LogoutCount).HasColumnName("logout_count");
        });

        modelBuilder.Entity<PlayerData>(e =>
        {
            e.ToTable("player_data");
            e.HasKey(p => p.AppInstanceId);
            e.Property(p => p.AppInstanceId).HasColumnName("app_instance_id");
            e.Property(p => p.PlayerId).HasColumnName("player_id");
            e.HasIndex(p => p.PlayerId).IsUnique();
            e.Property(p => p.Health)
                .HasColumnName("health")
                .HasDefaultValue(PlayerDataDefaults.Health);
            e.Property(p => p.Money)
                .HasColumnName("money")
                .HasDefaultValue(PlayerDataDefaults.Money);
            e.Property(p => p.PositionJson)
                .HasColumnName("position_json")
                .HasColumnType("jsonb")
                .HasDefaultValueSql("'{}'::jsonb");
            e.Property(p => p.InventoryJson)
                .HasColumnName("inventory_json")
                .HasColumnType("jsonb")
                .HasDefaultValueSql("'[]'::jsonb");
            e.Property(p => p.ExtraDataJson)
                .HasColumnName("extra_data_json")
                .HasColumnType("jsonb")
                .HasDefaultValueSql("'{}'::jsonb");
            e.Property(p => p.CreatedAt).HasColumnName("created_at");
            e.Property(p => p.UpdatedAt).HasColumnName("updated_at");

            e.HasOne<Guest>()
                .WithOne()
                .HasForeignKey<PlayerData>(p => p.AppInstanceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<FuelPriceCache>(e =>
        {
            e.ToTable("fuel_price_cache");
            e.HasKey(c => c.Id);
            e.Property(c => c.Id).HasColumnName("id");
            e.Property(c => c.Scope)
                .HasColumnName("scope")
                .HasMaxLength(16);
            e.Property(c => c.City)
                .HasColumnName("city")
                .HasMaxLength(100);
            e.Property(c => c.Province)
                .HasColumnName("province")
                .HasMaxLength(100);
            e.Property(c => c.Region)
                .HasColumnName("region")
                .HasMaxLength(100);
            e.Property(c => c.CityKey)
                .HasColumnName("city_key")
                .HasMaxLength(100);
            e.Property(c => c.ProvinceKey)
                .HasColumnName("province_key")
                .HasMaxLength(100);
            e.Property(c => c.RegionKey)
                .HasColumnName("region_key")
                .HasMaxLength(100);
            e.Property(c => c.ResultJson)
                .HasColumnName("result_json")
                .HasColumnType("jsonb");
            e.Property(c => c.Model)
                .HasColumnName("model")
                .HasMaxLength(100);
            e.Property(c => c.CachedAt).HasColumnName("cached_at");
            e.Property(c => c.RefreshAfter).HasColumnName("refresh_after");
            e.Property(c => c.HitCount)
                .HasColumnName("hit_count")
                .HasDefaultValue(0L);

            e.HasIndex(c => new { c.Scope, c.ProvinceKey, c.CityKey, c.CachedAt });
            e.HasIndex(c => new { c.Scope, c.RegionKey, c.CachedAt });
        });

        modelBuilder.Entity<FuelAdjustment>(e =>
        {
            e.ToTable("fuel_adjustment");
            e.HasKey(a => a.Id);
            e.Property(a => a.Id).HasColumnName("id");
            e.Property(a => a.WeekStart).HasColumnName("week_start");
            e.Property(a => a.WeekEnd).HasColumnName("week_end");
            e.Property(a => a.OilCompany).HasColumnName("oil_company").HasMaxLength(100);
            e.Property(a => a.EffectiveDatePhilippines).HasColumnName("effective_date_philippines");
            e.Property(a => a.EffectiveAtUtc).HasColumnName("effective_at_utc");
            e.Property(a => a.GasolineChangePerLiter).HasColumnName("gasoline_change_per_liter").HasPrecision(8, 2);
            e.Property(a => a.DieselChangePerLiter).HasColumnName("diesel_change_per_liter").HasPrecision(8, 2);
            e.Property(a => a.KeroseneChangePerLiter).HasColumnName("kerosene_change_per_liter").HasPrecision(8, 2);
            e.Property(a => a.SourceUrl).HasColumnName("source_url").HasMaxLength(2048);
            e.Property(a => a.FetchedAtUtc).HasColumnName("fetched_at_utc");
            e.HasIndex(a => new { a.WeekStart, a.OilCompany, a.EffectiveAtUtc })
                .IsUnique().HasFilter("effective_at_utc IS NOT NULL");
            e.HasIndex(a => new { a.WeekStart, a.OilCompany, a.EffectiveDatePhilippines })
                .IsUnique().HasFilter("effective_at_utc IS NULL");
        });

        modelBuilder.Entity<DoeFuelPrice>(e =>
        {
            e.ToTable("doe_fuel_price");
            e.HasKey(p => p.Id);
            e.Property(p => p.Id).HasColumnName("id");
            e.Property(p => p.ReportId).HasColumnName("report_id");
            e.Property(p => p.WeekStart).HasColumnName("week_start");
            e.Property(p => p.WeekEnd).HasColumnName("week_end");
            e.Property(p => p.City).HasColumnName("city").HasMaxLength(100);
            e.Property(p => p.CityKey).HasColumnName("city_key").HasMaxLength(100);
            e.Property(p => p.Province).HasColumnName("province").HasMaxLength(100);
            e.Property(p => p.ProvinceKey).HasColumnName("province_key").HasMaxLength(100);
            e.Property(p => p.Region).HasColumnName("region").HasMaxLength(100);
            e.Property(p => p.OilCompany).HasColumnName("oil_company").HasMaxLength(100);
            e.Property(p => p.FuelGrade).HasColumnName("fuel_grade").HasMaxLength(20);
            e.Property(p => p.MinPricePerLiter).HasColumnName("min_price_per_liter").HasPrecision(8, 2);
            e.Property(p => p.MaxPricePerLiter).HasColumnName("max_price_per_liter").HasPrecision(8, 2);
            e.Property(p => p.SourceUrl).HasColumnName("source_url").HasMaxLength(2048);
            e.Property(p => p.FetchedAtUtc).HasColumnName("fetched_at_utc");
            e.HasIndex(p => new { p.WeekStart, p.CityKey, p.ProvinceKey, p.OilCompany, p.FuelGrade }).IsUnique();
            e.HasIndex(p => new { p.CityKey, p.ProvinceKey, p.WeekEnd });
            e.HasIndex(p => p.ReportId);
            e.HasOne<DoePumpPriceReport>().WithMany().HasForeignKey(p => p.ReportId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<DoePumpPriceReport>(e =>
        {
            e.ToTable("doe_pump_price_report");
            e.HasKey(p => p.Id);
            e.Property(p => p.Id).HasColumnName("id");
            e.Property(p => p.Section).HasColumnName("section").HasMaxLength(40);
            e.Property(p => p.Subdivision).HasColumnName("subdivision").HasMaxLength(60);
            e.Property(p => p.WeekStart).HasColumnName("week_start");
            e.Property(p => p.WeekEnd).HasColumnName("week_end");
            e.Property(p => p.SourceUrl).HasColumnName("source_url").HasMaxLength(2048);
            e.Property(p => p.ContentHash).HasColumnName("content_hash").HasMaxLength(64);
            e.Property(p => p.ImportedAtUtc).HasColumnName("imported_at_utc");
            e.Property(p => p.PriceRows).HasColumnName("price_rows");
            e.Property(p => p.Status).HasColumnName("status").HasMaxLength(30);
            e.HasIndex(p => new { p.SourceUrl, p.WeekStart }).IsUnique();
            e.HasIndex(p => new { p.Section, p.Subdivision, p.WeekStart });
        });

        modelBuilder.Entity<DoeImportJob>(e =>
        {
            e.ToTable("doe_import_job");
            e.HasKey(p => p.Id);
            e.Property(p => p.Id).HasColumnName("id");
            e.Property(p => p.Mode).HasColumnName("mode").HasMaxLength(20);
            e.Property(p => p.From).HasColumnName("from_date");
            e.Property(p => p.To).HasColumnName("to_date");
            e.Property(p => p.Status).HasColumnName("status").HasMaxLength(30);
            e.Property(p => p.ActiveSlot).HasColumnName("active_slot");
            e.Property(p => p.CreatedAtUtc).HasColumnName("created_at_utc");
            e.Property(p => p.StartedAtUtc).HasColumnName("started_at_utc");
            e.Property(p => p.HeartbeatAtUtc).HasColumnName("heartbeat_at_utc");
            e.Property(p => p.FinishedAtUtc).HasColumnName("finished_at_utc");
            e.Property(p => p.ReportsFound).HasColumnName("reports_found");
            e.Property(p => p.ReportsImported).HasColumnName("reports_imported");
            e.Property(p => p.ReportsSkipped).HasColumnName("reports_skipped");
            e.Property(p => p.ReportsFailed).HasColumnName("reports_failed");
            e.Property(p => p.PriceRowsAdded).HasColumnName("price_rows_added");
            e.Property(p => p.PriceRowsUpdated).HasColumnName("price_rows_updated");
            e.Property(p => p.DetailsJson).HasColumnName("details_json").HasColumnType("jsonb");
            e.Property(p => p.RequestJson).HasColumnName("request_json").HasColumnType("jsonb");
            e.Property(p => p.ResultJson).HasColumnName("result_json").HasColumnType("jsonb");
            e.Property(p => p.Error).HasColumnName("error").HasMaxLength(500);
            e.HasIndex(p => new { p.Status, p.CreatedAtUtc });
            e.HasIndex(p => p.ActiveSlot).IsUnique().HasFilter("active_slot IS NOT NULL");
        });
    }
}

public static class DbConfig
{
    // Resolves the connection string, in priority order:
    //   1. DATABASE_URL  (Render provides this as a postgres:// URI)
    //   2. ConnectionStrings:Postgres  (explicit override)
    //   3. local PostgreSQL fallback (localhost, current OS user, trust auth)
    public static string ResolveConnectionString(IConfiguration config)
    {
        var url = Environment.GetEnvironmentVariable("DATABASE_URL");
        if (!string.IsNullOrWhiteSpace(url))
            return FromUrl(url);

        var explicitCs = config.GetConnectionString("Postgres");
        if (!string.IsNullOrWhiteSpace(explicitCs))
            return explicitCs;

        return new NpgsqlConnectionStringBuilder
        {
            Host = "localhost",
            Port = 5432,
            Database = "backendserver",
            Username = Environment.UserName
        }.ConnectionString;
    }

    private static string FromUrl(string url)
    {
        var uri = new Uri(url);
        var parts = uri.UserInfo.Split(':', 2);

        return new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.Port > 0 ? uri.Port : 5432,
            Username = Uri.UnescapeDataString(parts[0]),
            Password = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : "",
            Database = uri.AbsolutePath.TrimStart('/'),
            SslMode = SslMode.Require         // Render requires TLS
        }.ConnectionString;
    }
}

// Lets `dotnet ef` build the context without running the whole app.
public class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var environment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
            ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
            ?? "Development";

        var config = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile($"appsettings.{environment}.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(DbConfig.ResolveConnectionString(config))
            .Options;

        return new AppDbContext(options);
    }
}
