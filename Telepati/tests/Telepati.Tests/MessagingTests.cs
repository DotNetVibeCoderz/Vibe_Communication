using Telepati.Domain;
using Telepati.Shared.Contracts;

namespace Telepati.Tests;

public class MessagingTests
{
    [Fact]
    public async Task Direct_chat_is_reused_rather_than_duplicated()
    {
        await using var harness = new TestHarness();
        var alice = await harness.CreateUserAsync("alice");
        var bob = await harness.CreateUserAsync("bob");

        var first = await harness.Chats.GetOrCreateDirectChatAsync(alice, bob);
        var second = await harness.Chats.GetOrCreateDirectChatAsync(bob, alice);

        Assert.Equal(first.Id, second.Id);
    }

    [Fact]
    public async Task Direct_chat_title_is_resolved_per_viewer()
    {
        await using var harness = new TestHarness();
        var alice = await harness.CreateUserAsync("alice");
        var bob = await harness.CreateUserAsync("bob");

        var chat = await harness.Chats.GetOrCreateDirectChatAsync(alice, bob);

        // A direct chat stores no title; each side must see the other's name.
        var asAlice = await harness.Chats.GetChatAsync(alice, chat.Id);
        var asBob = await harness.Chats.GetChatAsync(bob, chat.Id);

        Assert.Equal("bob", asAlice!.Title);
        Assert.Equal("alice", asBob!.Title);
    }

    [Fact]
    public async Task Sending_a_message_increments_only_the_other_members_unread_count()
    {
        await using var harness = new TestHarness();
        var alice = await harness.CreateUserAsync("alice");
        var bob = await harness.CreateUserAsync("bob");

        var chat = await harness.Chats.GetOrCreateDirectChatAsync(alice, bob);
        await harness.Messages.SendAsync(alice, new SendMessageRequest { ChatId = chat.Id, Content = "Halo!" });

        var bobChats = await harness.Chats.GetChatsAsync(bob, 1, 10, false);
        var aliceChats = await harness.Chats.GetChatsAsync(alice, 1, 10, false);

        Assert.Equal(1, bobChats.Items.Single(c => c.Id == chat.Id).UnreadCount);
        Assert.Equal(0, aliceChats.Items.Single(c => c.Id == chat.Id).UnreadCount);
    }

    [Fact]
    public async Task Marking_read_clears_unread_and_settles_receipts()
    {
        await using var harness = new TestHarness();
        var alice = await harness.CreateUserAsync("alice");
        var bob = await harness.CreateUserAsync("bob");

        var chat = await harness.Chats.GetOrCreateDirectChatAsync(alice, bob);
        var sent = await harness.Messages.SendAsync(alice, new SendMessageRequest { ChatId = chat.Id, Content = "Halo!" });

        await harness.Messages.MarkReadAsync(bob, new ReadReceiptRequest(chat.Id, sent.Data!.Id));

        var bobChats = await harness.Chats.GetChatsAsync(bob, 1, 10, false);
        Assert.Equal(0, bobChats.Items.Single(c => c.Id == chat.Id).UnreadCount);

        var report = await harness.Messages.GetDeliveryReportAsync(alice, sent.Data.Id);
        Assert.Equal(1, report!.Read);
    }

    [Fact]
    public async Task Blocked_users_cannot_reach_each_other()
    {
        await using var harness = new TestHarness();
        var alice = await harness.CreateUserAsync("alice");
        var bob = await harness.CreateUserAsync("bob");

        var chat = await harness.Chats.GetOrCreateDirectChatAsync(alice, bob);
        await harness.Contacts.BlockAsync(bob, alice, "spam");

        // The block is symmetric: the blocker is protected in both directions.
        var result = await harness.Messages.SendAsync(alice, new SendMessageRequest { ChatId = chat.Id, Content = "halo?" });

        Assert.False(result.Success);
    }

    [Fact]
    public async Task Reacting_twice_with_the_same_emoji_removes_the_reaction()
    {
        await using var harness = new TestHarness();
        var alice = await harness.CreateUserAsync("alice");
        var bob = await harness.CreateUserAsync("bob");

        var chat = await harness.Chats.GetOrCreateDirectChatAsync(alice, bob);
        var sent = await harness.Messages.SendAsync(alice, new SendMessageRequest { ChatId = chat.Id, Content = "lucu" });

        await harness.Messages.ReactAsync(bob, new ReactionRequest(sent.Data!.Id, "😂"));
        await harness.Messages.ReactAsync(bob, new ReactionRequest(sent.Data.Id, "😂"));

        var page = await harness.Messages.GetMessagesAsync(alice, chat.Id, 1, 10, null);
        Assert.Empty(page.Items.Single(m => m.Id == sent.Data.Id).Reactions);
    }

    [Fact]
    public async Task Channel_rejects_posts_from_ordinary_members()
    {
        await using var harness = new TestHarness();
        var owner = await harness.CreateUserAsync("owner");
        var reader = await harness.CreateUserAsync("reader");

        var channel = await harness.Chats.CreateChatAsync(owner, new CreateChatRequest(
            (int)ChatType.Channel, "Info", "Pengumuman", [reader], false, true, "info"));

        var result = await harness.Messages.SendAsync(reader, new SendMessageRequest
        {
            ChatId = channel.Data!.Id,
            Content = "boleh posting?"
        });

        Assert.False(result.Success);

        var byOwner = await harness.Messages.SendAsync(owner, new SendMessageRequest
        {
            ChatId = channel.Data.Id,
            Content = "pengumuman resmi"
        });

        Assert.True(byOwner.Success);
    }

    [Fact]
    public async Task Owner_leaving_a_group_promotes_the_senior_admin()
    {
        await using var harness = new TestHarness();
        var owner = await harness.CreateUserAsync("owner");
        var second = await harness.CreateUserAsync("second");

        var group = await harness.Chats.CreateChatAsync(owner, new CreateChatRequest(
            (int)ChatType.Group, "Tim", null, [second], false, false, null));

        await harness.Chats.UpdateMemberRoleAsync(owner,
            new UpdateMemberRoleRequest(group.Data!.Id, second, (int)ChatMemberRole.Admin));

        await harness.Chats.LeaveAsync(owner, group.Data.Id);

        var members = await harness.Chats.GetMembersAsync(group.Data.Id);
        Assert.Equal((int)ChatMemberRole.Owner, members.Single(m => m.UserId == second).Role);
    }

    [Fact]
    public async Task Encrypted_chat_stores_ciphertext_and_stays_out_of_search()
    {
        await using var harness = new TestHarness(o => o.Security.EnableEndToEndEncryption = true);
        var alice = await harness.CreateUserAsync("alice");
        var bob = await harness.CreateUserAsync("bob");

        var chat = await harness.Chats.GetOrCreateDirectChatAsync(alice, bob);
        await harness.Messages.SendAsync(alice, new SendMessageRequest { ChatId = chat.Id, Content = "RAHASIA-CIPHERTEXT" });

        // The server holds no plaintext, so it must not pretend to search these messages.
        var results = await harness.Messages.SearchAsync(alice, "RAHASIA", null, 1, 10);
        Assert.Empty(results.Items);

        var page = await harness.Messages.GetMessagesAsync(alice, chat.Id, 1, 10, null);
        Assert.True(page.Items.Single().IsEncrypted);
    }

    [Fact]
    public async Task Realtime_push_reaches_every_member_of_the_chat()
    {
        await using var harness = new TestHarness();
        var alice = await harness.CreateUserAsync("alice");
        var bob = await harness.CreateUserAsync("bob");
        var carol = await harness.CreateUserAsync("carol");

        var group = await harness.Chats.CreateChatAsync(alice, new CreateChatRequest(
            (int)ChatType.Group, "Tim", null, [bob, carol], false, false, null));

        harness.Notifier.Sent.Clear();
        await harness.Messages.SendAsync(alice, new SendMessageRequest { ChatId = group.Data!.Id, Content = "halo tim" });

        var push = harness.Notifier.Sent.Single();
        Assert.Equal(3, push.Recipients.Count);
    }
}
