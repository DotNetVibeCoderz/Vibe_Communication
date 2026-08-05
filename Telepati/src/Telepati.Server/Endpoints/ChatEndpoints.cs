using System.Security.Claims;
using Telepati.Infrastructure.Services;
using Telepati.Server.Realtime;
using Telepati.Shared.Contracts;
using static Telepati.Server.Endpoints.QueryDefaults;

namespace Telepati.Server.Endpoints;

public static class ChatEndpoints
{
    public static IEndpointRouteBuilder MapChatEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/chats").WithTags("Chats").RequireAuthorization();

        group.MapGet("/", async (ClaimsPrincipal user, IChatService chats, int? page, int? pageSize, bool? includeArchived, CancellationToken ct) =>
            Results.Ok(await chats.GetChatsAsync(user.GetUserId(), Page(page), Size(pageSize, 30), includeArchived ?? false, ct)))
        .WithSummary("Daftar percakapan");

        group.MapGet("/{chatId:guid}", async (Guid chatId, ClaimsPrincipal user, IChatService chats, CancellationToken ct) =>
        {
            var chat = await chats.GetChatAsync(user.GetUserId(), chatId, ct);
            return chat is null ? Results.NotFound() : Results.Ok(chat);
        })
        .WithSummary("Detail satu percakapan");

        group.MapPost("/", async (CreateChatRequest request, ClaimsPrincipal user, IChatService chats, CancellationToken ct) =>
        {
            var result = await chats.CreateChatAsync(user.GetUserId(), request, ct);
            return result.Success ? Results.Ok(result.Data) : Results.BadRequest(result);
        })
        .WithSummary("Buat grup, channel, atau chat langsung");

        group.MapPost("/direct/{otherUserId:guid}", async (Guid otherUserId, ClaimsPrincipal user, IChatService chats, CancellationToken ct) =>
            Results.Ok(await chats.GetOrCreateDirectChatAsync(user.GetUserId(), otherUserId, ct)))
        .WithSummary("Buka (atau buat) chat langsung dengan seseorang");

        group.MapGet("/{chatId:guid}/members", async (Guid chatId, IChatService chats, CancellationToken ct) =>
            Results.Ok(await chats.GetMembersAsync(chatId, ct)))
        .WithSummary("Daftar anggota");

        group.MapPost("/{chatId:guid}/members", async (Guid chatId, AddMembersRequest request, ClaimsPrincipal user, IChatService chats, CancellationToken ct) =>
            Results.Ok(await chats.AddMembersAsync(user.GetUserId(), chatId, request.UserIds, ct)))
        .WithSummary("Tambah anggota");

        group.MapDelete("/{chatId:guid}/members/{memberId:guid}", async (Guid chatId, Guid memberId, ClaimsPrincipal user, IChatService chats, CancellationToken ct) =>
            Results.Ok(await chats.RemoveMemberAsync(user.GetUserId(), chatId, memberId, ct)))
        .WithSummary("Keluarkan anggota");

        group.MapPut("/members/role", async (UpdateMemberRoleRequest request, ClaimsPrincipal user, IChatService chats, CancellationToken ct) =>
            Results.Ok(await chats.UpdateMemberRoleAsync(user.GetUserId(), request, ct)))
        .WithSummary("Ubah role anggota (admin/moderator/member)");

        group.MapPost("/{chatId:guid}/leave", async (Guid chatId, ClaimsPrincipal user, IChatService chats, CancellationToken ct) =>
            Results.Ok(await chats.LeaveAsync(user.GetUserId(), chatId, ct)))
        .WithSummary("Keluar dari percakapan");

        group.MapPut("/{chatId:guid}", async (Guid chatId, UpdateChatRequest request, ClaimsPrincipal user, IChatService chats, CancellationToken ct) =>
            Results.Ok(await chats.UpdateChatAsync(user.GetUserId(), chatId, request.Title, request.Description, request.AvatarUrl, request.OnlyAdminsCanPost, ct)))
        .WithSummary("Ubah info percakapan");

        group.MapPost("/{chatId:guid}/mute", async (Guid chatId, ToggleRequest request, ClaimsPrincipal user, IChatService chats, CancellationToken ct) =>
            Results.Ok(await chats.SetMuteAsync(user.GetUserId(), chatId, request.Value, ct)))
        .WithSummary("Bisukan / aktifkan notifikasi");

        group.MapPost("/{chatId:guid}/pin", async (Guid chatId, ToggleRequest request, ClaimsPrincipal user, IChatService chats, CancellationToken ct) =>
            Results.Ok(await chats.SetPinAsync(user.GetUserId(), chatId, request.Value, ct)))
        .WithSummary("Sematkan percakapan di atas daftar");

        group.MapPost("/{chatId:guid}/archive", async (Guid chatId, ToggleRequest request, ClaimsPrincipal user, IChatService chats, CancellationToken ct) =>
            Results.Ok(await chats.SetArchiveAsync(user.GetUserId(), chatId, request.Value, ct)))
        .WithSummary("Arsipkan percakapan");

        group.MapGet("/channels/search", async (string? q, int? page, int? pageSize, IChatService chats, CancellationToken ct) =>
            Results.Ok(await chats.SearchPublicChannelsAsync(q ?? string.Empty, Page(page), Size(pageSize, 20), ct)))
        .WithSummary("Cari channel publik");

        group.MapPost("/channels/{channelId:guid}/subscribe", async (Guid channelId, ClaimsPrincipal user, IChatService chats, CancellationToken ct) =>
            Results.Ok(await chats.SubscribeChannelAsync(user.GetUserId(), channelId, ct)))
        .WithSummary("Berlangganan channel");

        return app;
    }
}

public static class MessageEndpoints
{
    public static IEndpointRouteBuilder MapMessageEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/messages").WithTags("Messages").RequireAuthorization();

        group.MapGet("/{chatId:guid}", async (Guid chatId, ClaimsPrincipal user, IMessageService messages,
            int? page, int? pageSize, Guid? before, CancellationToken ct) =>
            Results.Ok(await messages.GetMessagesAsync(user.GetUserId(), chatId, Page(page), Size(pageSize, 50), before, ct)))
        .WithSummary("Ambil pesan dalam satu percakapan");

        // Routed through the orchestrator so a REST send triggers the bot exactly like SignalR does.
        group.MapPost("/", async (SendMessageRequest request, ClaimsPrincipal user, ChatOrchestrator orchestrator, CancellationToken ct) =>
        {
            var result = await orchestrator.SendAsync(user.GetUserId(), request, ct);
            return result.Success ? Results.Ok(result.Data) : Results.BadRequest(result);
        })
        .WithSummary("Kirim pesan");

        group.MapPut("/", async (EditMessageRequest request, ClaimsPrincipal user, IMessageService messages, CancellationToken ct) =>
        {
            var result = await messages.EditAsync(user.GetUserId(), request, ct);
            return result.Success ? Results.Ok(result.Data) : Results.BadRequest(result);
        })
        .WithSummary("Ubah pesan");

        group.MapDelete("/{messageId:guid}", async (Guid messageId, ClaimsPrincipal user, IMessageService messages, CancellationToken ct) =>
            Results.Ok(await messages.DeleteAsync(user.GetUserId(), messageId, ct)))
        .WithSummary("Hapus pesan");

        group.MapPost("/{messageId:guid}/pin", async (Guid messageId, ToggleRequest request, ClaimsPrincipal user, IMessageService messages, CancellationToken ct) =>
            Results.Ok(await messages.PinAsync(user.GetUserId(), messageId, request.Value, ct)))
        .WithSummary("Sematkan / lepas pesan");

        group.MapGet("/{chatId:guid}/pinned", async (Guid chatId, ClaimsPrincipal user, IMessageService messages, CancellationToken ct) =>
            Results.Ok(await messages.GetPinnedAsync(user.GetUserId(), chatId, ct)))
        .WithSummary("Daftar pesan tersemat");

        group.MapPost("/react", async (ReactionRequest request, ClaimsPrincipal user, IMessageService messages, CancellationToken ct) =>
            Results.Ok(await messages.ReactAsync(user.GetUserId(), request, ct)))
        .WithSummary("Beri / batalkan reaksi emoji");

        group.MapPost("/{messageId:guid}/forward/{targetChatId:guid}", async (Guid messageId, Guid targetChatId,
            ClaimsPrincipal user, IMessageService messages, CancellationToken ct) =>
        {
            var result = await messages.ForwardAsync(user.GetUserId(), messageId, targetChatId, ct);
            return result.Success ? Results.Ok(result.Data) : Results.BadRequest(result);
        })
        .WithSummary("Teruskan pesan ke chat lain");

        group.MapPost("/read", async (ReadReceiptRequest request, ClaimsPrincipal user, IMessageService messages, CancellationToken ct) =>
            Results.Ok(await messages.MarkReadAsync(user.GetUserId(), request, ct)))
        .WithSummary("Tandai sudah dibaca");

        group.MapPost("/typing", async (TypingRequestDto request, ClaimsPrincipal user, IMessageService messages, CancellationToken ct) =>
            Results.Ok(await messages.SetTypingAsync(user.GetUserId(), request.ChatId, request.IsTyping, ct)))
        .WithSummary("Kirim indikator sedang mengetik");

        group.MapGet("/search", async (string q, Guid? chatId, int? page, int? pageSize,
            ClaimsPrincipal user, IMessageService messages, CancellationToken ct) =>
            Results.Ok(await messages.SearchAsync(user.GetUserId(), q, chatId, Page(page), Size(pageSize, 30), ct)))
        .WithSummary("Cari pesan");

        group.MapGet("/{messageId:guid}/delivery", async (Guid messageId, ClaimsPrincipal user, IMessageService messages, CancellationToken ct) =>
        {
            var report = await messages.GetDeliveryReportAsync(user.GetUserId(), messageId, ct);
            return report is null ? Results.NotFound() : Results.Ok(report);
        })
        .WithSummary("Laporan status pengiriman pesan");

        group.MapGet("/mentions", async (ClaimsPrincipal user, IMessageService messages, int? take, CancellationToken ct) =>
            Results.Ok(await messages.GetMentionsAsync(user.GetUserId(), Size(take, 20), ct)))
        .WithSummary("Pesan yang menyebut saya");

        return app;
    }
}

public record AddMembersRequest(IReadOnlyList<Guid> UserIds);
public record UpdateChatRequest(string? Title, string? Description, string? AvatarUrl, bool? OnlyAdminsCanPost);
public record ToggleRequest(bool Value);
public record TypingRequestDto(Guid ChatId, bool IsTyping);
