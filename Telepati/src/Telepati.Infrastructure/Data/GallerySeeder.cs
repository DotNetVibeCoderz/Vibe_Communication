using Microsoft.EntityFrameworkCore;
using Telepati.Domain;

namespace Telepati.Infrastructure.Data;

/// <summary>
/// Seeds the two galleries: the skill repositories an admin can browse, and a catalogue of
/// well-known MCP servers.
///
/// Everything here ships **disabled**. Enabling a server launches a process or opens a network
/// connection, and that has to be a decision an admin makes on purpose rather than something
/// that happens on first boot.
/// </summary>
public class GallerySeeder(TelepatiDbContext db)
{
    public async Task SeedAsync(CancellationToken ct = default)
    {
        await SeedSkillRepositoriesAsync(ct);
        await SeedMcpServersAsync(ct);
    }

    private async Task SeedSkillRepositoriesAsync(CancellationToken ct)
    {
        if (await db.SkillRepositories.AnyAsync(ct)) return;

        db.SkillRepositories.AddRange(
            new SkillRepository
            {
                Name = "Anthropic Skills",
                Slug = "anthropics/skills",
                Description = "Kumpulan skill resmi dari Anthropic — dokumen, spreadsheet, presentasi, PDF, dan lainnya.",
                HomepageUrl = "https://github.com/anthropics/skills",
                Reference = "main",
                Path = string.Empty,
                IsOfficial = true
            },
            new SkillRepository
            {
                Name = "Claude Code Plugins",
                Slug = "anthropics/claude-code",
                Description = "Skill dan plugin yang menyertai Claude Code.",
                HomepageUrl = "https://github.com/anthropics/claude-code",
                Reference = "main",
                Path = "plugins",
                IsOfficial = true
            },
            new SkillRepository
            {
                Name = "Awesome Claude Skills",
                Slug = "travis-burmaster/awesome-claude-skills",
                Description = "Kurasi komunitas berisi skill buatan pihak ketiga. Tinjau isinya sebelum dipasang.",
                HomepageUrl = "https://github.com/travis-burmaster/awesome-claude-skills",
                Reference = "main",
                Path = string.Empty,
                IsOfficial = false
            });

        await db.SaveChangesAsync(ct);
    }

    private async Task SeedMcpServersAsync(CancellationToken ct)
    {
        if (await db.McpServers.AnyAsync(ct)) return;

        // Every entry is a published, widely used server. Commands are the documented ones —
        // an admin can still edit them, but the defaults are not guesses.
        var catalogue = new (string Name, string Slug, string Description, McpCategory Category,
            string Command, string[] Args, string? Package, string? Homepage, string? KeyEnv)[]
        {
            // --- coding -------------------------------------------------------
            ("Filesystem", "filesystem",
                "Baca dan tulis berkas di folder yang diizinkan. Argumen terakhir adalah folder yang boleh diakses.",
                McpCategory.Coding, "npx", ["-y", "@modelcontextprotocol/server-filesystem", "./workspace"],
                "@modelcontextprotocol/server-filesystem", "https://github.com/modelcontextprotocol/servers", null),

            ("Git", "git",
                "Baca riwayat, diff, dan isi repositori Git lokal.",
                McpCategory.Coding, "uvx", ["mcp-server-git", "--repository", "."],
                "mcp-server-git", "https://github.com/modelcontextprotocol/servers", null),

            ("GitHub", "github",
                "Cari repo, baca kode, kelola issue dan pull request.",
                McpCategory.Coding, "npx", ["-y", "@modelcontextprotocol/server-github"],
                "@modelcontextprotocol/server-github", "https://github.com/modelcontextprotocol/servers",
                "GITHUB_PERSONAL_ACCESS_TOKEN"),

            ("Context7", "context7",
                "Dokumentasi pustaka dan framework yang selalu terbaru, langsung ke dalam konteks.",
                McpCategory.Coding, "npx", ["-y", "@upstash/context7-mcp"],
                "@upstash/context7-mcp", "https://github.com/upstash/context7", null),

            ("Sequential Thinking", "sequential-thinking",
                "Membantu model memecah masalah rumit menjadi langkah-langkah yang terstruktur.",
                McpCategory.Coding, "npx", ["-y", "@modelcontextprotocol/server-sequential-thinking"],
                "@modelcontextprotocol/server-sequential-thinking", "https://github.com/modelcontextprotocol/servers", null),

            // --- design -------------------------------------------------------
            ("Figma", "figma",
                "Baca berkas Figma: frame, komponen, dan token desain untuk diterjemahkan jadi kode.",
                McpCategory.Design, "npx", ["-y", "figma-developer-mcp", "--stdio"],
                "figma-developer-mcp", "https://github.com/GLips/Figma-Context-MCP", "FIGMA_API_KEY"),

            ("Canva", "canva",
                "Buat dan ubah desain Canva — poster, materi sosial media, dan presentasi.",
                McpCategory.Design, "npx", ["-y", "@canva/cli@latest", "mcp"],
                "@canva/cli", "https://www.canva.dev/docs/apps/mcp-server/", null),

            ("Excalidraw", "excalidraw",
                "Membuat diagram dan sketsa gaya papan tulis secara terprogram.",
                McpCategory.Design, "npx", ["-y", "mcp_excalidraw"],
                "mcp_excalidraw", "https://github.com/yctimlin/mcp_excalidraw", null),

            // --- 3D -----------------------------------------------------------
            ("Blender", "blender",
                "Kendalikan Blender: buat objek 3D, atur material, dan render adegan.",
                McpCategory.ThreeD, "uvx", ["blender-mcp"],
                "blender-mcp", "https://github.com/ahujasid/blender-mcp", null),

            ("Unity", "unity",
                "Berinteraksi dengan editor Unity: scene, GameObject, dan aset.",
                McpCategory.ThreeD, "npx", ["-y", "unity-mcp-server"],
                "unity-mcp-server", "https://github.com/CoderGamester/mcp-unity", null),

            ("Three.js Docs", "threejs-docs",
                "Dokumentasi dan contoh Three.js untuk membangun adegan 3D di web.",
                McpCategory.ThreeD, "npx", ["-y", "@upstash/context7-mcp"],
                "@upstash/context7-mcp", "https://github.com/upstash/context7", null),

            // --- presentation --------------------------------------------------
            ("PowerPoint", "powerpoint",
                "Buat dan sunting berkas .pptx — slide, teks, gambar, dan bagan.",
                McpCategory.Presentation, "uvx", ["office-powerpoint-mcp-server"],
                "office-powerpoint-mcp-server", "https://github.com/GongRzhe/Office-PowerPoint-MCP-Server", null),

            ("Marp Slides", "marp",
                "Susun presentasi dari Markdown lalu ekspor ke HTML atau PDF.",
                McpCategory.Presentation, "npx", ["-y", "@marp-team/marp-cli", "--server"],
                "@marp-team/marp-cli", "https://github.com/marp-team/marp-cli", null),

            // --- documents ------------------------------------------------------
            ("Word", "word",
                "Buat dan sunting dokumen .docx lengkap dengan gaya dan tabel.",
                McpCategory.Document, "uvx", ["office-word-mcp-server"],
                "office-word-mcp-server", "https://github.com/GongRzhe/Office-Word-MCP-Server", null),

            ("Excel", "excel",
                "Baca dan tulis berkas .xlsx: rumus, format, dan bagan.",
                McpCategory.Document, "npx", ["-y", "@negokaz/excel-mcp-server"],
                "@negokaz/excel-mcp-server", "https://github.com/negokaz/excel-mcp-server", null),

            ("PDF Reader", "pdf-reader",
                "Ekstrak teks, tabel, dan metadata dari berkas PDF.",
                McpCategory.Document, "uvx", ["mcp-server-pdf"],
                "mcp-server-pdf", "https://github.com/modelcontextprotocol/servers", null),

            ("Markitdown", "markitdown",
                "Konversi PDF, Office, gambar, dan HTML menjadi Markdown yang rapi.",
                McpCategory.Document, "uvx", ["markitdown-mcp"],
                "markitdown-mcp", "https://github.com/microsoft/markitdown", null),

            // --- data ------------------------------------------------------------
            ("SQLite", "sqlite",
                "Kueri dan analisa basis data SQLite lokal.",
                McpCategory.Data, "uvx", ["mcp-server-sqlite", "--db-path", "./workspace/data.db"],
                "mcp-server-sqlite", "https://github.com/modelcontextprotocol/servers", null),

            ("PostgreSQL", "postgres",
                "Kueri baca-saja ke basis data PostgreSQL beserta skemanya.",
                McpCategory.Data, "npx", ["-y", "@modelcontextprotocol/server-postgres"],
                "@modelcontextprotocol/server-postgres", "https://github.com/modelcontextprotocol/servers",
                "POSTGRES_CONNECTION_STRING"),

            ("Memory", "memory",
                "Ingatan jangka panjang berbasis graf pengetahuan, lintas percakapan.",
                McpCategory.Data, "npx", ["-y", "@modelcontextprotocol/server-memory"],
                "@modelcontextprotocol/server-memory", "https://github.com/modelcontextprotocol/servers", null),

            // --- web ---------------------------------------------------------------
            ("Playwright", "playwright",
                "Kendalikan browser sungguhan: navigasi, isi formulir, dan ambil tangkapan layar.",
                McpCategory.Web, "npx", ["-y", "@playwright/mcp@latest"],
                "@playwright/mcp", "https://github.com/microsoft/playwright-mcp", null),

            ("Fetch", "fetch",
                "Ambil sebuah halaman web dan ubah menjadi Markdown.",
                McpCategory.Web, "uvx", ["mcp-server-fetch"],
                "mcp-server-fetch", "https://github.com/modelcontextprotocol/servers", null),

            ("Tavily", "tavily",
                "Pencarian dan ekstraksi web yang dirancang untuk agen.",
                McpCategory.Web, "npx", ["-y", "tavily-mcp"],
                "tavily-mcp", "https://github.com/tavily-ai/tavily-mcp", "TAVILY_API_KEY"),

            ("Brave Search", "brave-search",
                "Pencarian web dan lokal lewat Brave Search API.",
                McpCategory.Web, "npx", ["-y", "@modelcontextprotocol/server-brave-search"],
                "@modelcontextprotocol/server-brave-search", "https://github.com/modelcontextprotocol/servers",
                "BRAVE_API_KEY"),

            // --- productivity ---------------------------------------------------------
            ("Notion", "notion",
                "Baca dan tulis halaman serta basis data Notion.",
                McpCategory.Productivity, "npx", ["-y", "@notionhq/notion-mcp-server"],
                "@notionhq/notion-mcp-server", "https://github.com/makenotion/notion-mcp-server", "NOTION_API_KEY"),

            ("Slack", "slack",
                "Baca kanal dan kirim pesan ke workspace Slack.",
                McpCategory.Productivity, "npx", ["-y", "@modelcontextprotocol/server-slack"],
                "@modelcontextprotocol/server-slack", "https://github.com/modelcontextprotocol/servers",
                "SLACK_BOT_TOKEN"),

            ("Google Drive", "gdrive",
                "Cari dan baca berkas di Google Drive.",
                McpCategory.Productivity, "npx", ["-y", "@modelcontextprotocol/server-gdrive"],
                "@modelcontextprotocol/server-gdrive", "https://github.com/modelcontextprotocol/servers",
                "GDRIVE_CREDENTIALS_PATH"),

            ("Time", "time",
                "Waktu saat ini dan konversi zona waktu.",
                McpCategory.General, "uvx", ["mcp-server-time"],
                "mcp-server-time", "https://github.com/modelcontextprotocol/servers", null)
        };

        foreach (var (name, slug, description, category, command, args, package, homepage, keyEnv) in catalogue)
        {
            db.McpServers.Add(new McpServerDefinition
            {
                Name = name,
                Slug = slug,
                Description = description,
                Category = category,
                Transport = McpTransport.Stdio,
                Command = command,
                Arguments = string.Join('\n', args),
                PackageName = package,
                HomepageUrl = homepage,
                RequiresApiKey = keyEnv is not null,
                ApiKeyEnvironmentName = keyEnv,
                EnvironmentVariables = keyEnv is null ? null : $"{keyEnv}=",
                IsBuiltIn = true,
                IsEnabled = false
            });
        }

        await db.SaveChangesAsync(ct);
    }
}
