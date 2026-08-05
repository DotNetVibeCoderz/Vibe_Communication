using Microsoft.EntityFrameworkCore;
using Telepati.Domain;
using Telepati.Infrastructure.Data;
using Telepati.Infrastructure.Services;
using Telepati.Shared.Contracts;

namespace Telepati.Tests;

public class SkillManifestTests
{
    [Fact]
    public void Frontmatter_supplies_the_metadata_and_the_body_becomes_instructions()
    {
        var manifest = SkillManifest.Parse("""
            ---
            name: PDF Toolkit
            description: Membaca, menggabung, dan mengisi formulir PDF.
            version: 1.2.0
            author: Contoh
            license: MIT
            ---

            # Cara pakai

            1. Baca berkasnya
            2. Ekstrak halaman
            """);

        Assert.Equal("PDF Toolkit", manifest.Name);
        Assert.Equal("Membaca, menggabung, dan mengisi formulir PDF.", manifest.Description);
        Assert.Equal("1.2.0", manifest.Version);
        Assert.Equal("MIT", manifest.License);
        Assert.Contains("Ekstrak halaman", manifest.Instructions);
        Assert.DoesNotContain("name:", manifest.Instructions);
    }

    [Fact]
    public void A_skill_without_frontmatter_still_parses()
    {
        // Community skills are inconsistent; one malformed file must not break a gallery.
        var manifest = SkillManifest.Parse("""
            # Diagram Helper

            Membuat diagram arsitektur dari deskripsi teks.

            ## Langkah
            1. Tentukan komponen
            """);

        Assert.Equal("Diagram Helper", manifest.Name);
        Assert.Contains("diagram arsitektur", manifest.Description);
        Assert.Contains("Tentukan komponen", manifest.Instructions);
    }

    [Fact]
    public void Broken_yaml_degrades_instead_of_throwing()
    {
        var manifest = SkillManifest.Parse("""
            ---
            name: [ this is not valid
              description: "unterminated
            ---

            # Fallback

            Isi instruksinya tetap berguna.
            """);

        Assert.Contains("Isi instruksinya", manifest.Instructions);
    }

    [Fact]
    public void Empty_input_produces_an_empty_manifest_rather_than_an_exception()
    {
        var manifest = SkillManifest.Parse("");

        Assert.Equal(string.Empty, manifest.Name);
        Assert.Equal(string.Empty, manifest.Instructions);
    }

    [Theory]
    [InlineData("PDF Toolkit", "pdf-toolkit")]
    [InlineData("  Excel   Helper  ", "excel-helper")]
    [InlineData("3D/Blender Render", "3d-blender-render")]
    public void Slugs_are_folder_safe(string input, string expected) =>
        Assert.Equal(expected, SkillCatalogService.Slugify(input));
}

public class McpCatalogTests
{
    /// <summary>
    /// Every mutation writes an activity log row keyed on a real user, so the harness creates
    /// one — a made-up admin id would trip the foreign key rather than test anything.
    /// </summary>
    private static async Task<(TelepatiDbContext Db, McpCatalogService Service, Guid AdminId)> CreateAsync()
    {
        var connection = new Microsoft.Data.Sqlite.SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<TelepatiDbContext>().UseSqlite(connection).Options;
        var db = new TelepatiDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var admin = new User
        {
            Username = "admin", Email = "admin@test.local", DisplayName = "Admin",
            PasswordHash = "x", Role = UserRole.Admin
        };
        db.Users.Add(admin);
        await db.SaveChangesAsync();

        return (db, new McpCatalogService(db, new ActivityLogger(db)), admin.Id);
    }

    [Fact]
    public async Task The_seeded_catalogue_covers_every_requested_category_and_ships_disabled()
    {
        var (db, _, _) = await CreateAsync();
        await new GallerySeeder(db).SeedAsync();

        var servers = await db.McpServers.AsNoTracking().ToListAsync();

        Assert.NotEmpty(servers);

        // Enabling a server launches a process; nothing may be on by default.
        Assert.All(servers, s => Assert.False(s.IsEnabled));

        foreach (var required in new[]
                 {
                     McpCategory.Design, McpCategory.ThreeD, McpCategory.Presentation,
                     McpCategory.Document, McpCategory.Coding
                 })
        {
            Assert.Contains(servers, s => s.Category == required);
        }
    }

    [Fact]
    public async Task A_server_needing_a_key_cannot_be_enabled_until_one_is_supplied()
    {
        var (db, service, adminId) = await CreateAsync();
        await new GallerySeeder(db).SeedAsync();

        var figma = await db.McpServers.AsNoTracking().FirstAsync(s => s.Slug == "figma");
        Assert.True(figma.RequiresApiKey);

        var rejected = await service.SetEnabledAsync(adminId, figma.Id, true);
        Assert.False(rejected.Success);

        await service.UpdateAsync(adminId, new McpServerDto
        {
            Id = figma.Id,
            Name = figma.Name,
            Category = (int)figma.Category,
            Transport = (int)figma.Transport,
            Command = figma.Command,
            EnvironmentVariables = ["FIGMA_API_KEY=figd_contoh"]
        });

        var accepted = await service.SetEnabledAsync(adminId, figma.Id, true);
        Assert.True(accepted.Success);
    }

    [Fact]
    public async Task Secret_values_are_masked_on_the_way_out()
    {
        var (db, service, adminId) = await CreateAsync();

        var added = await service.AddAsync(adminId, new McpServerDto
        {
            Name = "Contoh",
            Transport = 0,
            Command = "npx",
            EnvironmentVariables = ["SOME_API_KEY=rahasia123", "LOG_LEVEL=debug"]
        });

        Assert.True(added.Success);

        var fetched = await service.GetAsync(added.Data!.Id);
        var variables = fetched.Data!.EnvironmentVariables;

        Assert.Contains("SOME_API_KEY=••••••", variables);
        // Non-secret values stay readable — masking everything would hide useful configuration.
        Assert.Contains("LOG_LEVEL=debug", variables);
    }

    [Fact]
    public async Task A_masked_value_coming_back_does_not_overwrite_the_real_secret()
    {
        var (db, service, adminId) = await CreateAsync();

        var added = await service.AddAsync(adminId, new McpServerDto
        {
            Name = "Contoh",
            Transport = 0,
            Command = "npx",
            EnvironmentVariables = ["SOME_API_KEY=rahasia123"]
        });

        var fetched = await service.GetAsync(added.Data!.Id);

        // Saving the form unchanged sends the masked placeholder back; the stored value must
        // survive, otherwise every unrelated edit would silently destroy the key.
        await service.UpdateAsync(adminId, fetched.Data! with { Category = 2 });

        var stored = await db.McpServers.AsNoTracking().FirstAsync(s => s.Id == added.Data.Id);
        Assert.Contains("rahasia123", stored.EnvironmentVariables);
    }

    [Fact]
    public async Task Built_in_entries_cannot_be_deleted_but_can_be_disabled()
    {
        var (db, service, adminId) = await CreateAsync();
        await new GallerySeeder(db).SeedAsync();

        var builtIn = await db.McpServers.AsNoTracking().FirstAsync(s => s.IsBuiltIn);

        var removal = await service.RemoveAsync(adminId, builtIn.Id);
        Assert.False(removal.Success);

        var disable = await service.SetEnabledAsync(adminId, builtIn.Id, false);
        Assert.True(disable.Success);
    }

    [Fact]
    public async Task A_stdio_server_without_a_command_is_rejected()
    {
        var (_, service, adminId) = await CreateAsync();

        var result = await service.AddAsync(adminId, new McpServerDto
        {
            Name = "Tanpa perintah",
            Transport = 0
        });

        Assert.False(result.Success);
        Assert.Contains("perintah", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_http_server_needs_a_valid_absolute_url()
    {
        var (_, service, adminId) = await CreateAsync();

        var bad = await service.AddAsync(adminId, new McpServerDto
        {
            Name = "URL rusak", Transport = 1, Url = "bukan-url"
        });
        Assert.False(bad.Success);

        var good = await service.AddAsync(adminId, new McpServerDto
        {
            Name = "Server jauh", Transport = 1, Url = "https://contoh.com/mcp"
        });
        Assert.True(good.Success);
    }

    [Fact]
    public async Task Newly_added_servers_start_disabled()
    {
        var (_, service, adminId) = await CreateAsync();

        var added = await service.AddAsync(adminId, new McpServerDto
        {
            Name = "Baru", Transport = 0, Command = "npx"
        });

        Assert.True(added.Success);
        Assert.False(added.Data!.IsEnabled);
    }
}

public class SkillRepositorySeedTests
{
    [Fact]
    public async Task Trusted_skill_sources_are_seeded_and_the_official_ones_are_marked()
    {
        var connection = new Microsoft.Data.Sqlite.SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<TelepatiDbContext>().UseSqlite(connection).Options;
        await using var db = new TelepatiDbContext(options);
        await db.Database.EnsureCreatedAsync();

        await new GallerySeeder(db).SeedAsync();

        var repositories = await db.SkillRepositories.AsNoTracking().ToListAsync();

        Assert.NotEmpty(repositories);
        Assert.Contains(repositories, r => r.Slug == "anthropics/skills" && r.IsOfficial);
        Assert.All(repositories, r => Assert.True(r.IsEnabled));
    }
}
