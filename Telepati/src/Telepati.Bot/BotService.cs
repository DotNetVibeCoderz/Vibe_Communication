using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Telepati.Domain;
using Telepati.Infrastructure.Data;
using Telepati.Infrastructure.Services;
using Telepati.Shared.Configuration;

namespace Telepati.Bot;

public interface IBotService
{
    /// <summary>True when this message is addressed to the bot (direct chat, or a mention in a group).</summary>
    Task<bool> ShouldRespondAsync(Guid chatId, string? content, CancellationToken ct = default);
    Task<BotReply> HandleAsync(BotRequest request, CancellationToken ct = default);
    Task<Guid?> GetBotUserIdAsync(CancellationToken ct = default);
}

public record BotRequest(Guid ChatId, Guid UserId, string UserName, string Content, bool IsGroup, IReadOnlyList<string>? AttachmentUrls = null);

public record BotReply(bool Handled, string? Content, bool IsSystemNotice = false);

/// <summary>
/// Kang Bacot. Owns conversation state — one session per direct chat and one per group — plus
/// the inline commands and the auto-compaction that keeps a long conversation inside the model's
/// context window.
/// </summary>
public class BotService(
    TelepatiDbContext db,
    IKernelFactory kernelFactory,
    ISettingsService settings,
    IActivityLogger activity,
    ILogger<BotService> logger) : IBotService
{
    public const string ResetCommand = "#resetbot";
    public const string PersonaCommand = "#newpersona";

    public async Task<Guid?> GetBotUserIdAsync(CancellationToken ct = default)
    {
        var options = await settings.GetOptionsAsync(ct);
        return await db.Users.AsNoTracking()
            .Where(u => u.IsBot && u.Username == options.Bot.Handle)
            .Select(u => (Guid?)u.Id)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<bool> ShouldRespondAsync(Guid chatId, string? content, CancellationToken ct = default)
    {
        var options = await settings.GetOptionsAsync(ct);
        if (!options.Bot.Enabled || !options.Features.EnableBots) return false;

        var chat = await db.Chats.AsNoTracking().FirstOrDefaultAsync(c => c.Id == chatId, ct);
        if (chat is null) return false;

        // Direct conversations are always for the bot; in a group it must be called by name,
        // otherwise it would answer every message in the room.
        if (chat.Type is ChatType.Bot or ChatType.Direct)
        {
            return await db.ChatMembers.AsNoTracking()
                .AnyAsync(m => m.ChatId == chatId && db.Users.Any(u => u.Id == m.UserId && u.IsBot), ct);
        }

        if (string.IsNullOrWhiteSpace(content)) return false;
        return content.Contains($"@{options.Bot.Handle}", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<BotReply> HandleAsync(BotRequest request, CancellationToken ct = default)
    {
        var options = (await settings.GetOptionsAsync(ct)).Bot;
        if (!options.Enabled) return new BotReply(false, null);

        var prompt = StripMention(request.Content, options.Handle);
        var session = await GetOrCreateSessionAsync(request, options, ct);

        // Inline commands are handled before the model is ever consulted.
        if (prompt.StartsWith(ResetCommand, StringComparison.OrdinalIgnoreCase))
        {
            return await ResetSessionAsync(session, ct);
        }

        if (prompt.StartsWith(PersonaCommand, StringComparison.OrdinalIgnoreCase))
        {
            return await SetPersonaAsync(session, prompt[PersonaCommand.Length..].Trim(), ct);
        }

        if (string.IsNullOrWhiteSpace(prompt))
        {
            return new BotReply(true, "Iya, ada yang bisa dibantu? 😄");
        }

        try
        {
            var reply = await CompleteAsync(session, request, prompt, options, ct);
            await activity.LogAsync(ActivityKind.BotInvoked, request.UserId,
                $"Kang Bacot menjawab di chat {request.ChatId}.", entityType: nameof(BotSession), entityId: session.Id, ct: ct);
            return new BotReply(true, reply);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Kang Bacot failed to answer in chat {ChatId}", request.ChatId);
            return new BotReply(true, $"Waduh, ada kendala di sisi saya: {e.Message}\n\nCoba lagi sebentar lagi ya 🙏");
        }
    }

    // -- conversation ---------------------------------------------------------

    private async Task<string> CompleteAsync(BotSession session, BotRequest request, string prompt, BotOptions options, CancellationToken ct)
    {
        var kernel = await kernelFactory.CreateWithToolsAsync(options, ct);
        var chatService = kernel.GetRequiredService<IChatCompletionService>();

        var history = new ChatHistory();
        history.AddSystemMessage(BuildSystemPrompt(session, request, options));

        // Skills are announced by name and one-line description only. Loading every skill's
        // full instructions up front would exhaust the context window before the conversation
        // even starts; the model calls Skills.LoadSkill when one actually looks relevant.
        var skillIndex = await BuildSkillIndexAsync(ct);
        if (skillIndex is not null) history.AddSystemMessage(skillIndex);

        // Everything compaction already folded away is replayed as a single summary turn.
        if (!string.IsNullOrWhiteSpace(session.CompactedSummary))
        {
            history.AddSystemMessage($"Ringkasan percakapan sebelumnya:\n{session.CompactedSummary}");
        }

        var previous = await db.BotMessages.AsNoTracking()
            .Where(m => m.BotSessionId == session.Id && m.IsActive)
            .OrderBy(m => m.CreatedAt)
            .ToListAsync(ct);

        foreach (var message in previous)
        {
            if (message.Role == "assistant") history.AddAssistantMessage(message.Content);
            else if (message.Role == "user") history.AddUserMessage(message.Content);
        }

        var userTurn = request.IsGroup ? $"[{request.UserName}]: {prompt}" : prompt;

        if (request.AttachmentUrls is { Count: > 0 })
        {
            // The model cannot fetch a URL by itself; naming the files points it at the tools.
            userTurn += "\n\nLampiran yang dikirim user (gunakan tool Files.DownloadFile atau Web.ReadTextFromUrl bila perlu):\n"
                        + string.Join('\n', request.AttachmentUrls.Select(u => $"- {u}"));
        }

        history.AddUserMessage(userTurn);

        var executionSettings = kernelFactory.CreateExecutionSettings(options);

        var result = await chatService.GetChatMessageContentAsync(history, executionSettings, kernel, ct);
        var answer = result.Content;

        // Reasoning models sometimes come back with an empty content part — typically after a
        // tool round trip, where the whole budget went on reasoning tokens. Nudging once for a
        // written answer recovers it; without this the user simply gets nothing back.
        if (string.IsNullOrWhiteSpace(answer))
        {
            logger.LogWarning("Empty reply from {Provider}/{Model}; retrying once.", options.Provider, options.Model);

            history.AddUserMessage("Tolong jawab dalam teks biasa sekarang, singkat saja.");
            result = await chatService.GetChatMessageContentAsync(history, executionSettings, kernel, ct);
            answer = result.Content;
        }

        // The nudge only ever lived in the local history object, which is rebuilt from the
        // database on the next turn — so nothing has to be cleaned up here.
        if (string.IsNullOrWhiteSpace(answer))
        {
            // Deliberately not persisted: a failed turn must not pollute the remembered
            // conversation, or the model keeps seeing its own silence as context.
            return "Hmm, aku belum berhasil menyusun jawaban untuk itu. Coba tanya dengan kalimat lain ya 🙏";
        }

        db.BotMessages.Add(new BotMessage
        {
            BotSessionId = session.Id, Role = "user", Content = userTurn, TokenCount = EstimateTokens(userTurn)
        });
        db.BotMessages.Add(new BotMessage
        {
            BotSessionId = session.Id, Role = "assistant", Content = answer, TokenCount = EstimateTokens(answer)
        });

        session.TurnCount++;
        session.EstimatedTokens += EstimateTokens(userTurn) + EstimateTokens(answer);
        session.LastInteractionAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);
        await CompactIfNeededAsync(session, kernel, options, ct);

        return answer;
    }

    /// <summary>
    /// A compact index of installed skills, or null when none are enabled. This is the
    /// progressive-disclosure entry point: names and descriptions now, full instructions later.
    /// </summary>
    private async Task<string?> BuildSkillIndexAsync(CancellationToken ct)
    {
        var skills = await db.Skills.AsNoTracking()
            .Where(s => s.IsEnabled)
            .OrderBy(s => s.Name)
            .Select(s => new { s.Slug, s.Name, s.Description })
            .ToListAsync(ct);

        if (skills.Count == 0) return null;

        var lines = skills.Select(s => $"- {s.Slug}: {s.Name} — {Shorten(s.Description)}");

        return $"""
                Skill khusus yang terpasang dan bisa kamu pakai:
                {string.Join('\n', lines)}

                Kalau tugas user cocok dengan salah satunya, panggil Skills.LoadSkill dengan slug-nya
                untuk membaca instruksi lengkap, lalu ikuti langkah-langkahnya. Sebuah skill bisa
                membawa referensi (Skills.ReadReference) dan skrip yang bisa dijalankan
                (Skills.RunSkillScript).
                """;
    }

    private static string Shorten(string value) =>
        value.Length <= 160 ? value : value[..160].TrimEnd() + "…";

    private string BuildSystemPrompt(BotSession session, BotRequest request, BotOptions options)
    {
        var persona = string.IsNullOrWhiteSpace(session.SystemPromptOverride)
            ? options.SystemPrompt
            : session.SystemPromptOverride;

        var context = request.IsGroup
            ? $"Kamu sedang berada di dalam sebuah grup. Pesan diawali dengan [nama pengirim]. " +
              $"Kamu hanya menjawab ketika di-mention dengan @{options.Handle}. Sapa orang dengan namanya."
            : $"Kamu sedang mengobrol berdua dengan {request.UserName}.";

        return $"""
                {persona}

                {context}

                Panduan tambahan:
                - Tulis jawaban dalam Markdown: gunakan tabel, blok kode dengan penanda bahasa, dan tautan bila relevan.
                - Untuk gambar/video/audio, sertakan sebagai tautan Markdown agar bisa dirender klien.
                - Gunakan tool yang tersedia bila butuh data terkini, perhitungan, membaca file, atau menjalankan skrip.
                - Jangan mengarang fakta. Kalau tidak tahu, katakan tidak tahu.
                - Setelah membuat file untuk user, panggil Files.ShareResult agar user dapat tautan unduhnya.
                """;
    }

    /// <summary>
    /// When the running estimate crosses the configured share of the window, the oldest turns
    /// are summarised into one paragraph and deactivated. History stays useful without the
    /// prompt growing without bound.
    /// </summary>
    private async Task CompactIfNeededAsync(BotSession session, Kernel kernel, BotOptions options, CancellationToken ct)
    {
        var threshold = options.ContextWindowTokens * options.AutoCompactThreshold;
        if (session.EstimatedTokens < threshold) return;

        var active = await db.BotMessages
            .Where(m => m.BotSessionId == session.Id && m.IsActive)
            .OrderBy(m => m.CreatedAt)
            .ToListAsync(ct);

        if (active.Count <= options.MaxTurnsKeptVerbatim) return;

        var toCompact = active.Take(active.Count - options.MaxTurnsKeptVerbatim).ToList();
        var transcript = string.Join('\n', toCompact.Select(m => $"{m.Role}: {m.Content}"));

        string summary;
        try
        {
            var chatService = kernel.GetRequiredService<IChatCompletionService>();
            var history = new ChatHistory();
            history.AddSystemMessage(
                "Ringkas percakapan berikut menjadi satu paragraf padat dalam Bahasa Indonesia. " +
                "Pertahankan fakta penting, keputusan, nama, angka, dan preferensi user. Buang basa-basi.");
            history.AddUserMessage(transcript);

            var result = await chatService.GetChatMessageContentAsync(history, null, kernel, ct);
            summary = result.Content ?? string.Empty;
        }
        catch (Exception e)
        {
            // If summarising fails, dropping the old turns still beats blowing the context window.
            logger.LogWarning(e, "Auto-compact summarisation failed for session {SessionId}", session.Id);
            summary = $"(ringkasan otomatis gagal dibuat pada {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm} UTC)";
        }

        session.CompactedSummary = string.IsNullOrWhiteSpace(session.CompactedSummary)
            ? summary
            : $"{session.CompactedSummary}\n\n{summary}";

        foreach (var message in toCompact) message.IsActive = false;

        session.EstimatedTokens = active
            .Skip(active.Count - options.MaxTurnsKeptVerbatim)
            .Sum(m => m.TokenCount) + EstimateTokens(session.CompactedSummary);

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Compacted {Count} turns for bot session {SessionId}", toCompact.Count, session.Id);
    }

    // -- commands -------------------------------------------------------------

    private async Task<BotReply> ResetSessionAsync(BotSession session, CancellationToken ct)
    {
        await db.BotMessages.Where(m => m.BotSessionId == session.Id).ExecuteDeleteAsync(ct);

        // A reset restores the configured persona too, not just the transcript.
        session.SystemPromptOverride = null;
        session.CompactedSummary = null;
        session.TurnCount = 0;
        session.EstimatedTokens = 0;
        await db.SaveChangesAsync(ct);

        return new BotReply(true,
            "🧹 Oke, memori sesi ini sudah aku kosongkan dan persona balik ke bawaan. Mulai dari awal ya!",
            IsSystemNotice: true);
    }

    private async Task<BotReply> SetPersonaAsync(BotSession session, string persona, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(persona))
        {
            return new BotReply(true,
                $"Format: `{PersonaCommand} [deskripsi persona baru]`\n\nContoh: `{PersonaCommand} Kamu adalah asisten riset yang formal dan selalu menyertakan sumber.`",
                IsSystemNotice: true);
        }

        session.SystemPromptOverride = persona;
        await db.SaveChangesAsync(ct);

        return new BotReply(true,
            $"✅ Persona untuk sesi ini diganti.\n\n> {persona}\n\nKetik `{ResetCommand}` untuk kembali ke persona bawaan.",
            IsSystemNotice: true);
    }

    // -- session lookup -------------------------------------------------------

    private async Task<BotSession> GetOrCreateSessionAsync(BotRequest request, BotOptions options, CancellationToken ct)
    {
        // Group sessions key on the chat (shared by everyone in the room); direct sessions key
        // on the user. That single distinction is what isolates the histories.
        var chatId = request.IsGroup ? request.ChatId : (Guid?)null;
        var userId = request.IsGroup ? (Guid?)null : request.UserId;

        var session = await db.BotSessions
            .FirstOrDefaultAsync(s => s.ChatId == chatId && s.UserId == userId && s.BotHandle == options.Handle, ct);

        if (session is not null) return session;

        session = new BotSession { ChatId = chatId, UserId = userId, BotHandle = options.Handle };
        db.BotSessions.Add(session);
        await db.SaveChangesAsync(ct);
        return session;
    }

    private static string StripMention(string content, string handle) =>
        content.Replace($"@{handle}", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();

    /// <summary>
    /// Rough token estimate. Indonesian and English both land near four characters per token,
    /// which is accurate enough to decide when to compact and avoids a tokenizer dependency
    /// that would differ per provider anyway.
    /// </summary>
    private static int EstimateTokens(string? text) =>
        string.IsNullOrEmpty(text) ? 0 : Math.Max(1, text.Length / 4);
}
