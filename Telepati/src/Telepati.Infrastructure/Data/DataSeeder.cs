using Microsoft.EntityFrameworkCore;
using Telepati.Domain;
using Telepati.Infrastructure.Storage;
using Telepati.Shared.Configuration;

namespace Telepati.Infrastructure.Data;

/// <summary>
/// Populates a fresh database with themes, demo accounts and a realistic conversation history so
/// the apps are usable the moment they start. Every step is idempotent — seeding an already
/// populated database is a no-op, which makes it safe to run on every boot.
/// </summary>
public class DataSeeder(TelepatiDbContext db, TelepatiOptions options, IStorageService storage)
{
    // Fixed seed keeps demo data identical across machines, which makes screenshots reproducible.
    private readonly Random _random = new(20260805);

    public const string DemoPassword = "Telepati123!";

    public async Task SeedAsync(CancellationToken ct = default)
    {
        await SeedThemesAsync(ct);
        await SeedStickersAsync(ct);

        if (!options.Database.SeedSampleData) return;
        if (await db.Users.AnyAsync(ct)) return;

        var users = await SeedUsersAsync(ct);
        await SeedContactsAsync(users, ct);
        await SeedConversationsAsync(users, ct);
        await SeedStatusesAsync(users, ct);
    }

    private async Task SeedThemesAsync(CancellationToken ct)
    {
        if (await db.Themes.AnyAsync(ct)) return;

        db.Themes.AddRange(
            new ThemeDefinition
            {
                Name = "Telepati Classic", Description = "Ungu segar khas Telepati.",
                PrimaryColor = "#6C5CE7", SecondaryColor = "#00CEC9", AccentColor = "#FD79A8",
                BackgroundColor = "#FFFFFF", SurfaceColor = "#F5F6FA", TextColor = "#2D3436",
                IconSet = "💬", IsActive = true
            },
            new ThemeDefinition
            {
                Name = "Telepati Midnight", Description = "Mode gelap yang nyaman di mata.",
                PrimaryColor = "#A29BFE", SecondaryColor = "#55EFC4", AccentColor = "#FF7675",
                BackgroundColor = "#12131A", SurfaceColor = "#1C1E28", TextColor = "#EDEEF3",
                IconSet = "🌙", IsDark = true
            },
            new ThemeDefinition
            {
                Name = "Lebaran", Description = "Nuansa hijau emas untuk Idulfitri.",
                PrimaryColor = "#1E8449", SecondaryColor = "#F1C40F", AccentColor = "#E67E22",
                BackgroundColor = "#FFFDF5", SurfaceColor = "#F3EFE0", TextColor = "#1B2B22",
                IconSet = "🌙✨", IsSeasonal = true,
                ActiveFrom = new DateTimeOffset(2026, 3, 18, 0, 0, 0, TimeSpan.Zero),
                ActiveTo = new DateTimeOffset(2026, 3, 25, 23, 59, 59, TimeSpan.Zero)
            },
            new ThemeDefinition
            {
                Name = "Tahun Baru", Description = "Kembang api dan biru malam.",
                PrimaryColor = "#2C3E90", SecondaryColor = "#F5C518", AccentColor = "#E84393",
                BackgroundColor = "#0E1230", SurfaceColor = "#191D45", TextColor = "#F2F3FF",
                IconSet = "🎆", IsDark = true, IsSeasonal = true,
                ActiveFrom = new DateTimeOffset(2026, 12, 28, 0, 0, 0, TimeSpan.Zero),
                ActiveTo = new DateTimeOffset(2027, 1, 3, 23, 59, 59, TimeSpan.Zero)
            },
            new ThemeDefinition
            {
                Name = "Kemerdekaan", Description = "Merah putih untuk 17 Agustus.",
                PrimaryColor = "#C0392B", SecondaryColor = "#FFFFFF", AccentColor = "#E74C3C",
                BackgroundColor = "#FFFFFF", SurfaceColor = "#FDF2F2", TextColor = "#2D3436",
                IconSet = "🇮🇩", IsSeasonal = true,
                ActiveFrom = new DateTimeOffset(2026, 8, 15, 0, 0, 0, TimeSpan.Zero),
                ActiveTo = new DateTimeOffset(2026, 8, 18, 23, 59, 59, TimeSpan.Zero)
            },
            new ThemeDefinition
            {
                Name = "Natal", Description = "Merah hijau hangat untuk Desember.",
                PrimaryColor = "#B3242B", SecondaryColor = "#1E8449", AccentColor = "#F1C40F",
                BackgroundColor = "#FFFBF7", SurfaceColor = "#F6EDE7", TextColor = "#2B211D",
                IconSet = "🎄", IsSeasonal = true,
                ActiveFrom = new DateTimeOffset(2026, 12, 20, 0, 0, 0, TimeSpan.Zero),
                ActiveTo = new DateTimeOffset(2026, 12, 27, 23, 59, 59, TimeSpan.Zero)
            });

        await db.SaveChangesAsync(ct);
    }

    private async Task SeedStickersAsync(CancellationToken ct)
    {
        if (await db.StickerPacks.AnyAsync(ct)) return;

        var pack = new StickerPack
        {
            Name = "Telepati Emoji",
            Description = "Paket ekspresi bawaan.",
            IsBuiltIn = true
        };
        db.StickerPacks.Add(pack);

        string[] emojis = ["😀", "😂", "🥰", "😎", "🤔", "👍", "🙏", "🔥", "🎉", "❤️", "😭", "🤝", "☕", "🍚", "🚀", "🌙"];
        foreach (var emoji in emojis)
        {
            db.Stickers.Add(new Sticker
            {
                StickerPackId = pack.Id,
                Emoji = emoji,
                StorageKey = $"stickers/builtin/{System.Net.WebUtility.UrlEncode(emoji)}.png",
                Keywords = emoji
            });
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task<List<User>> SeedUsersAsync(CancellationToken ct)
    {
        var hash = BCrypt.Net.BCrypt.HashPassword(DemoPassword);

        var people = new (string Username, string Name, string About, UserRole Role)[]
        {
            ("kangfadhil", "Kang Fadhil", "Founder Gravicode Studios ☕", UserRole.SuperAdmin),
            ("admin", "Administrator", "Admin Telepati", UserRole.Admin),
            ("moderator", "Moderator Telepati", "Jaga ketertiban grup", UserRole.Moderator),
            ("sitinurhaliza", "Siti Nurhaliza", "Suka masak dan berkebun 🌱", UserRole.User),
            ("budisantoso", "Budi Santoso", "Backend engineer, penggemar kopi tubruk", UserRole.User),
            ("dewilestari", "Dewi Lestari", "Menulis cerita di waktu senggang ✍️", UserRole.User),
            ("agusprasetyo", "Agus Prasetyo", "Anak rantau, kangen rumah", UserRole.User),
            ("rinamarlina", "Rina Marlina", "Designer. Suka warna pastel 🎨", UserRole.User),
            ("hendrawijaya", "Hendra Wijaya", "Main futsal tiap Jumat ⚽", UserRole.User),
            ("lailatulfitri", "Lailatul Fitri", "Guru SD, sabar itu kunci", UserRole.User),
            ("ekopurnomo", "Eko Purnomo", "Fotografer lepas 📷", UserRole.User),
            ("mayasari", "Maya Sari", "Data analyst. Excel is life", UserRole.User),
            ("rizkyramadhan", "Rizky Ramadhan", "Gamer paruh waktu 🎮", UserRole.User),
            ("putriandini", "Putri Andini", "Mahasiswa tingkat akhir, semangat!", UserRole.User),
            ("dimasaditya", "Dimas Aditya", "Mobile dev, Flutter & MAUI", UserRole.User),
            ("nurulhidayah", "Nurul Hidayah", "Suka baca novel sebelum tidur 📚", UserRole.User),
            ("bagusprakoso", "Bagus Prakoso", "Sepeda tiap akhir pekan 🚴", UserRole.User),
            ("winarnisih", "Winarni Sih", "Ibu rumah tangga, jago rendang", UserRole.User),
            ("fajarnugroho", "Fajar Nugroho", "DevOps. Uptime adalah segalanya", UserRole.User),
            ("intanpermata", "Intan Permata", "Content creator kuliner 🍜", UserRole.User),
            ("yudhapratama", "Yudha Pratama", "Musisi indie 🎸", UserRole.User),
            ("saraswatidewi", "Saraswati Dewi", "Yoga tiap pagi 🧘", UserRole.User),
            ("ahmadfauzi", "Ahmad Fauzi", "Santri digital, ngoding sambil ngaji", UserRole.User),
            ("cintaamelia", "Cinta Amelia", "Traveler. 12 provinsi dan terus jalan ✈️", UserRole.User),
            ("gilangramadhan", "Gilang Ramadhan", "Barista di kedai sendiri ☕", UserRole.User)
        };

        // Coordinates cluster around Bandung so nearby search returns believable distances.
        const double baseLat = -6.9175;
        const double baseLon = 107.6191;

        var users = new List<User>();
        for (var i = 0; i < people.Length; i++)
        {
            var (username, name, about, role) = people[i];
            users.Add(new User
            {
                Username = username,
                Email = $"{username}@telepati.app",
                PhoneNumber = $"0812{(34000000 + i * 137):D8}"[..12],
                DisplayName = name,
                About = about,
                PasswordHash = hash,
                Role = role,
                Presence = i % 4 == 0 ? UserPresence.Online : UserPresence.Offline,
                LastSeenAt = DateTimeOffset.UtcNow.AddMinutes(-_random.Next(1, 2880)),
                LastLatitude = baseLat + (_random.NextDouble() - 0.5) * 0.6,
                LastLongitude = baseLon + (_random.NextDouble() - 0.5) * 0.6,
                ShareLocationForDiscovery = i % 3 != 0,
                CreatedAt = DateTimeOffset.UtcNow.AddDays(-_random.Next(10, 400))
            });
        }

        // The bot is a real account so it can hold memberships and send messages like anyone else.
        users.Add(new User
        {
            Username = options.Bot.Handle,
            Email = $"{options.Bot.Handle}@telepati.app",
            DisplayName = options.Bot.DisplayName,
            About = "Temen ngobrol yang selalu siap. Mention @" + options.Bot.Handle + " di grup ya!",
            PasswordHash = hash,
            AvatarUrl = options.Bot.AvatarUrl,
            IsBot = true,
            Presence = UserPresence.Online,
            Role = UserRole.User
        });

        db.Users.AddRange(users);

        foreach (var user in users)
        {
            var saved = new Chat { Type = ChatType.SavedMessages, Title = "Pesan Tersimpan", CreatedById = user.Id, MemberCount = 1 };
            db.Chats.Add(saved);
            db.ChatMembers.Add(new ChatMember { ChatId = saved.Id, UserId = user.Id, Role = ChatMemberRole.Owner });
        }

        await db.SaveChangesAsync(ct);
        return users;
    }

    private async Task SeedContactsAsync(List<User> users, CancellationToken ct)
    {
        var humans = users.Where(u => !u.IsBot).ToList();

        foreach (var owner in humans)
        {
            var others = humans.Where(u => u.Id != owner.Id).OrderBy(_ => _random.Next()).Take(_random.Next(6, 14));
            foreach (var other in others)
            {
                db.Contacts.Add(new Contact
                {
                    OwnerId = owner.Id,
                    ContactUserId = other.Id,
                    IsFavorite = _random.Next(5) == 0
                });
            }
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task SeedConversationsAsync(List<User> users, CancellationToken ct)
    {
        var humans = users.Where(u => !u.IsBot).ToList();
        var bot = users.First(u => u.IsBot);

        var directOpeners = new[]
        {
            "Halo! Apa kabar?", "Eh, jadi ketemu besok?", "Udah makan siang belum?",
            "Btw file yang kemarin udah aku kirim ya 📎", "Wkwkwk parah sih ini 😂",
            "Nanti malam nonton bareng?", "Aku on the way, macet dikit",
            "Makasih banyak ya, sangat membantu 🙏", "Besok meeting jam berapa?",
            "Selamat pagi! Semangat ya hari ini ✨"
        };

        var replies = new[]
        {
            "Baik dong, kamu gimana?", "Siap, aku catat ya 👍", "Belum nih, laper banget",
            "Oke sudah aku terima, makasih!", "Iya ngakak banget 🤣", "Boleh, jam 8 ya",
            "Santai, hati-hati di jalan", "Sama-sama, senang bisa bantu 😊",
            "Jam 10 pagi di ruang rapat", "Pagi! Semangat juga 💪"
        };

        // --- direct chats -----------------------------------------------------
        for (var i = 0; i < humans.Count; i++)
        {
            for (var j = i + 1; j < Math.Min(i + 4, humans.Count); j++)
            {
                var a = humans[i];
                var b = humans[j];
                var startedAt = DateTimeOffset.UtcNow.AddDays(-_random.Next(1, 30));

                var chat = new Chat { Type = ChatType.Direct, CreatedById = a.Id, MemberCount = 2, CreatedAt = startedAt };
                db.Chats.Add(chat);
                db.ChatMembers.Add(new ChatMember { ChatId = chat.Id, UserId = a.Id, JoinedAt = startedAt });
                db.ChatMembers.Add(new ChatMember { ChatId = chat.Id, UserId = b.Id, JoinedAt = startedAt });

                var turns = _random.Next(4, 12);
                var at = startedAt;
                Message? last = null;

                for (var turn = 0; turn < turns; turn++)
                {
                    var fromA = turn % 2 == 0;
                    at = at.AddMinutes(_random.Next(2, 180));
                    last = new Message
                    {
                        ChatId = chat.Id,
                        SenderId = fromA ? a.Id : b.Id,
                        Type = MessageType.Text,
                        Content = fromA ? directOpeners[_random.Next(directOpeners.Length)] : replies[_random.Next(replies.Length)],
                        CreatedAt = at,
                        DeliveryState = MessageDeliveryState.Read
                    };
                    db.Messages.Add(last);
                }

                chat.LastMessageId = last?.Id;
                chat.LastMessageAt = last?.CreatedAt ?? startedAt;
            }
        }

        // --- groups -----------------------------------------------------------
        var groupPlans = new (string Title, string Description, int Size)[]
        {
            ("Tim Telepati 🚀", "Koordinasi harian tim produk.", 8),
            ("Keluarga Besar 👨‍👩‍👧‍👦", "Grup keluarga, jangan spam ya.", 7),
            ("Futsal Jumat ⚽", "Kumpul jam 8 malam di GOR.", 9),
            ("Ngopi Bandung ☕", "Rekomendasi kedai kopi enak.", 10),
            ("Belajar .NET 🧑‍💻", "Diskusi C#, Blazor, MAUI, Avalonia.", 12)
        };

        var groupTalk = new[]
        {
            "Rekan-rekan, jangan lupa standup jam 9 ya 🙌",
            "Ada yang tahu kenapa build-nya gagal?",
            "Sudah aku push ke branch feature/chat-ui",
            "Setuju, mending kita pakai SignalR dulu buat default",
            "Besok libur ya? 😄",
            "Foto kemarin udah aku upload ke drive",
            "Mantap! Progressnya cepat banget 🔥",
            "Aku telat 15 menit, mulai duluan aja",
            "Ini dokumentasinya sudah aku rapikan",
            "Ada yang mau titip kopi?"
        };

        foreach (var (title, description, size) in groupPlans)
        {
            var members = humans.OrderBy(_ => _random.Next()).Take(size).ToList();
            var owner = members[0];
            var createdAt = DateTimeOffset.UtcNow.AddDays(-_random.Next(20, 120));

            var chat = new Chat
            {
                Type = ChatType.Group,
                Title = title,
                Description = description,
                CreatedById = owner.Id,
                MemberCount = members.Count + 1,
                CreatedAt = createdAt
            };
            db.Chats.Add(chat);

            for (var i = 0; i < members.Count; i++)
            {
                db.ChatMembers.Add(new ChatMember
                {
                    ChatId = chat.Id,
                    UserId = members[i].Id,
                    Role = i == 0 ? ChatMemberRole.Owner : i == 1 ? ChatMemberRole.Admin : i == 2 ? ChatMemberRole.Moderator : ChatMemberRole.Member,
                    JoinedAt = createdAt
                });
            }

            // Kang Bacot sits in every group so the mention flow is demoable out of the box.
            db.ChatMembers.Add(new ChatMember { ChatId = chat.Id, UserId = bot.Id, JoinedAt = createdAt });

            var at = createdAt;
            Message? last = null;
            for (var turn = 0; turn < _random.Next(10, 25); turn++)
            {
                at = at.AddHours(_random.Next(1, 20));
                last = new Message
                {
                    ChatId = chat.Id,
                    SenderId = members[_random.Next(members.Count)].Id,
                    Type = MessageType.Text,
                    Content = groupTalk[_random.Next(groupTalk.Length)],
                    CreatedAt = at,
                    IsPinned = turn == 0,
                    DeliveryState = MessageDeliveryState.Read
                };
                db.Messages.Add(last);
            }

            chat.LastMessageId = last?.Id;
            chat.LastMessageAt = last?.CreatedAt ?? createdAt;
        }

        // --- channels ---------------------------------------------------------
        var channelPlans = new (string Title, string Handle, string Description, string[] Posts)[]
        {
            ("Info Telepati", "telepati", "Pengumuman resmi Telepati.",
                ["Selamat datang di Telepati! 🎉", "Update v1.0: panggilan video kini tersedia.", "Tips: ketik #resetbot untuk mereset Kang Bacot."]),
            ("Lowongan IT Indonesia", "lokerit", "Info lowongan kerja bidang IT.",
                ["Dibutuhkan .NET Developer, remote, 2 posisi.", "Lowongan UI/UX Designer di Bandung.", "Internship backend, batch Agustus dibuka."]),
            ("Resep Nusantara", "resepnusantara", "Resep masakan Indonesia tiap hari.",
                ["Rendang Padang autentik 🍛", "Soto Betawi kuah susu, gurihnya nagih.", "Es cendol Bandung untuk siang panas 🥤"])
        };

        foreach (var (title, handle, description, posts) in channelPlans)
        {
            var owner = humans[_random.Next(humans.Count)];
            var subscribers = humans.OrderBy(_ => _random.Next()).Take(_random.Next(10, humans.Count)).ToList();
            var createdAt = DateTimeOffset.UtcNow.AddDays(-_random.Next(30, 200));

            var chat = new Chat
            {
                Type = ChatType.Channel,
                Title = title,
                Handle = handle,
                Description = description,
                CreatedById = owner.Id,
                IsPublic = true,
                OnlyAdminsCanPost = true,
                MemberCount = subscribers.Count + 1,
                CreatedAt = createdAt
            };
            db.Chats.Add(chat);
            db.ChatMembers.Add(new ChatMember { ChatId = chat.Id, UserId = owner.Id, Role = ChatMemberRole.Owner, JoinedAt = createdAt });

            foreach (var subscriber in subscribers.Where(s => s.Id != owner.Id))
            {
                db.ChatMembers.Add(new ChatMember { ChatId = chat.Id, UserId = subscriber.Id, JoinedAt = createdAt });
            }

            var at = createdAt;
            Message? last = null;
            foreach (var post in posts)
            {
                at = at.AddDays(_random.Next(1, 10));
                last = new Message
                {
                    ChatId = chat.Id,
                    SenderId = owner.Id,
                    Type = MessageType.Text,
                    Content = post,
                    CreatedAt = at,
                    DeliveryState = MessageDeliveryState.Read
                };
                db.Messages.Add(last);
            }

            chat.LastMessageId = last?.Id;
            chat.LastMessageAt = last?.CreatedAt ?? createdAt;
        }

        // --- direct chat with the bot ----------------------------------------
        foreach (var human in humans.Take(5))
        {
            var createdAt = DateTimeOffset.UtcNow.AddDays(-_random.Next(1, 10));
            var chat = new Chat { Type = ChatType.Bot, Title = options.Bot.DisplayName, CreatedById = human.Id, MemberCount = 2, CreatedAt = createdAt };
            db.Chats.Add(chat);
            db.ChatMembers.Add(new ChatMember { ChatId = chat.Id, UserId = human.Id, JoinedAt = createdAt });
            db.ChatMembers.Add(new ChatMember { ChatId = chat.Id, UserId = bot.Id, JoinedAt = createdAt });

            var ask = new Message
            {
                ChatId = chat.Id, SenderId = human.Id, Type = MessageType.Text,
                Content = "Kang, bantu ringkas berita teknologi hari ini dong",
                CreatedAt = createdAt.AddMinutes(1), DeliveryState = MessageDeliveryState.Read
            };
            var answer = new Message
            {
                ChatId = chat.Id, SenderId = bot.Id, Type = MessageType.Text, IsBotMessage = true,
                Content = "Siap! Ini ringkasannya:\n\n1. **Rilis .NET 10** — performa runtime naik signifikan.\n2. **Blazor** — render mode makin fleksibel.\n3. **WebRTC** — dukungan codec baru di browser.\n\nMau aku carikan sumbernya juga?",
                CreatedAt = createdAt.AddMinutes(2), DeliveryState = MessageDeliveryState.Read
            };
            db.Messages.AddRange(ask, answer);

            chat.LastMessageId = answer.Id;
            chat.LastMessageAt = answer.CreatedAt;
        }

        await db.SaveChangesAsync(ct);
        await BackfillReceiptsAsync(ct);
    }

    /// <summary>
    /// Seed messages are inserted in bulk without going through <c>MessageService</c>, so the
    /// per-recipient receipts that power delivery reports are generated afterwards.
    /// </summary>
    private async Task BackfillReceiptsAsync(CancellationToken ct)
    {
        var messages = await db.Messages.AsNoTracking()
            .Select(m => new { m.Id, m.ChatId, m.SenderId })
            .ToListAsync(ct);

        var membersByChat = (await db.ChatMembers.AsNoTracking()
                .Select(m => new { m.ChatId, m.UserId })
                .ToListAsync(ct))
            .GroupBy(m => m.ChatId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.UserId).ToList());

        foreach (var message in messages)
        {
            if (!membersByChat.TryGetValue(message.ChatId, out var members)) continue;

            foreach (var memberId in members.Where(id => id != message.SenderId))
            {
                db.MessageReceipts.Add(new MessageReceipt
                {
                    MessageId = message.Id,
                    UserId = memberId,
                    State = MessageDeliveryState.Read,
                    DeliveredAt = DateTimeOffset.UtcNow,
                    ReadAt = DateTimeOffset.UtcNow
                });
            }
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task SeedStatusesAsync(List<User> users, CancellationToken ct)
    {
        var captions = new[]
        {
            "Pagi yang cerah di Bandung", "Kopi dulu biar semangat",
            "Alhamdulillah project selesai", "Weekend vibes",
            "Lagi belajar hal baru nih", "Kangen rumah"
        };

        var colors = new[] { "#6C5CE7", "#00B894", "#0984E3", "#E17055", "#D63031", "#2D3436" };

        var posters = users.Where(u => !u.IsBot).OrderBy(_ => _random.Next()).Take(10).ToList();

        for (var i = 0; i < posters.Count; i++)
        {
            var user = posters[i];
            var postedAt = DateTimeOffset.UtcNow.AddHours(-_random.Next(1, 20));

            db.StatusPosts.Add(new StatusPost
            {
                UserId = user.Id,
                MediaType = StatusMediaType.Text,
                Caption = captions[_random.Next(captions.Length)],
                BackgroundColor = colors[_random.Next(colors.Length)],
                CreatedAt = postedAt,
                ExpiresAt = postedAt.AddHours(24),
                ViewCount = _random.Next(0, 30)
            });

            // A few people post a picture as well, so the feed exercises the image path and
            // the viewer can be seen doing something other than rendering text.
            if (i % 3 == 0)
            {
                var key = await CreateSampleImageAsync(user.DisplayName, colors[(i + 2) % colors.Length], ct);

                db.StatusPosts.Add(new StatusPost
                {
                    UserId = user.Id,
                    MediaType = StatusMediaType.Image,
                    Caption = "Suasana sore ini",
                    StorageKey = key,
                    CreatedAt = postedAt.AddMinutes(30),
                    ExpiresAt = postedAt.AddHours(24),
                    ViewCount = _random.Next(0, 20)
                });
            }
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Writes a small SVG through the storage provider so image statuses point at a file that
    /// really exists. Generating one beats bundling binary assets in the repository.
    /// </summary>
    private async Task<string> CreateSampleImageAsync(string name, string color, CancellationToken ct)
    {
        var initials = string.Concat(name.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Take(2).Select(part => part[0])).ToUpperInvariant();

        var svg = $"""
                   <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 720 1280" width="720" height="1280">
                     <defs>
                       <linearGradient id="g" x1="0" y1="0" x2="1" y2="1">
                         <stop offset="0%" stop-color="{color}"/>
                         <stop offset="100%" stop-color="#12131A"/>
                       </linearGradient>
                     </defs>
                     <rect width="720" height="1280" fill="url(#g)"/>
                     <circle cx="360" cy="520" r="120" fill="rgba(255,255,255,0.16)"/>
                     <text x="360" y="560" text-anchor="middle" fill="#fff"
                           font-family="sans-serif" font-size="96" font-weight="700">{initials}</text>
                     <text x="360" y="760" text-anchor="middle" fill="rgba(255,255,255,0.9)"
                           font-family="sans-serif" font-size="42">{System.Net.WebUtility.HtmlEncode(name)}</text>
                   </svg>
                   """;

        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(svg));
        return await storage.UploadAsync(stream, "status.svg", "image/svg+xml", StorageFolders.Status, ct);
    }
}
