using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Npgsql;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Guest> Guests => Set<Guest>();
    public DbSet<PlayerData> PlayerData => Set<PlayerData>();
    public DbSet<FuelPriceCache> FuelPriceCaches => Set<FuelPriceCache>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
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
