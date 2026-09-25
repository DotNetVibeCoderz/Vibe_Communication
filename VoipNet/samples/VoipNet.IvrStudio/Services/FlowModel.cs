using System.Text.Json;
using System.Text.Json.Serialization;
using VoipNet.Enterprise.Ivr;

namespace VoipNet.IvrStudio.Services;

[JsonConverter(typeof(JsonStringEnumConverter<OptionKind>))]
public enum OptionKind
{
    GoToMenu,
    CollectDigits,
    Transfer,
    Queue,
    AiAgent,
    Say,
    HangUp,
}

/// <summary>An editable menu option. Which fields matter depends on <see cref="Kind"/>.</summary>
public sealed class OptionModel
{
    public char Digit { get; set; } = '1';

    /// <summary>String view of <see cref="Digit"/> for form binding.</summary>
    [JsonIgnore]
    public string DigitText
    {
        get => Digit.ToString();
        set => Digit = string.IsNullOrEmpty(value) ? '1' : value[0];
    }

    public string Label { get; set; } = string.Empty;

    public OptionKind Kind { get; set; } = OptionKind.GoToMenu;

    /// <summary>Menu id, SIP URI or queue name.</summary>
    public string Target { get; set; } = string.Empty;

    /// <summary>Spoken text: collect prompt, announcement or line to say.</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>Name the collected value is stored under.</summary>
    public string Key { get; set; } = "account";

    public string Describe() => Kind switch
    {
        OptionKind.GoToMenu => $"go to {Target}",
        OptionKind.CollectDigits => $"collect {Key} → {Target}",
        OptionKind.Transfer => $"transfer to {Target}",
        OptionKind.Queue => $"queue {Target}",
        OptionKind.AiAgent => "hand to AI agent",
        OptionKind.Say => "say a line",
        _ => "hang up",
    };
}

public sealed class MenuModel
{
    public string Id { get; set; } = "main";

    public string Prompt { get; set; } = string.Empty;

    public int TimeoutSeconds { get; set; } = 6;

    public List<OptionModel> Options { get; set; } = [];

    /// <summary>Where the menu sits on the designer's canvas. Zero means it has never been placed,
    /// and the studio lays it out from the flow's shape instead.</summary>
    public double X { get; set; }

    public double Y { get; set; }
}

/// <summary>The flow as the studio edits and saves it.</summary>
public sealed class FlowModel
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public string Name { get; set; } = "Gravicode Net — customer line";

    public string Welcome { get; set; } = string.Empty;

    public string AiInstructions { get; set; } = string.Empty;

    public List<MenuModel> Menus { get; set; } = [];

    /// <summary>The menu a caller starts in: the first one, which is what <see cref="Build"/> runs.</summary>
    [JsonIgnore]
    public MenuModel? Entry => Menus.FirstOrDefault();

    /// <summary>Gives every unplaced menu a position: the entry menu first, then whatever it leads to,
    /// row by row, so a flow that has never been arranged still reads as a graph.</summary>
    public void Layout()
    {
        if (Menus.All(m => m.X != 0 || m.Y != 0))
        {
            return;
        }

        var placed = new HashSet<string>();
        var row = Entry is null ? [] : new List<MenuModel> { Entry };
        var depth = 0;
        while (row.Count > 0)
        {
            for (var i = 0; i < row.Count; i++)
            {
                var menu = row[i];
                placed.Add(menu.Id);
                if (menu.X == 0 && menu.Y == 0)
                {
                    menu.X = 40 + i * 260;
                    menu.Y = 30 + depth * 210;
                }
            }

            var next = row
                .SelectMany(m => m.Options)
                .Where(o => o.Kind is OptionKind.GoToMenu or OptionKind.CollectDigits)
                .Select(o => Menus.FirstOrDefault(m => m.Id == o.Target))
                .OfType<MenuModel>()
                .Where(m => placed.Add(m.Id))
                .ToList();
            row = next;
            depth++;
        }

        // Anything the entry menu cannot reach still has to go somewhere.
        foreach (var (menu, i) in Menus.Where(m => m.X == 0 && m.Y == 0).Select((m, i) => (m, i)))
        {
            menu.X = 40 + i * 260;
            menu.Y = 30 + (depth + 1) * 210;
        }
    }

    public static FlowModel Sample() => new()
    {
        Welcome = "Selamat datang di Gravicode Net.",
        AiInstructions = "Kamu Sari, agen AI layanan pelanggan ISP Gravicode Net. Jawab singkat dalam bahasa Indonesia, maksimal dua kalimat. Gunakan nomor pelanggan jika tersedia.",
        Menus =
        [
            new MenuModel
            {
                Id = "main",
                Prompt = "Tekan satu untuk berlangganan, dua untuk bantuan teknis, nol untuk operator.",
                Options =
                [
                    new OptionModel { Digit = '1', Label = "Berlangganan", Kind = OptionKind.Queue, Target = "sales", Text = "Menghubungkan ke tim penjualan." },
                    new OptionModel { Digit = '2', Label = "Bantuan teknis", Kind = OptionKind.CollectDigits, Key = "account", Text = "Masukkan nomor pelanggan Anda, akhiri dengan tanda pagar.", Target = "support" },
                    new OptionModel { Digit = '0', Label = "Operator", Kind = OptionKind.Transfer, Target = "sip:operator@pbx.local", Text = "Mohon tunggu, Anda akan disambungkan." },
                ],
            },
            new MenuModel
            {
                Id = "support",
                Prompt = "Tekan satu untuk bicara dengan asisten AI, dua untuk status gangguan.",
                Options =
                [
                    new OptionModel { Digit = '1', Label = "Asisten AI", Kind = OptionKind.AiAgent },
                    new OptionModel { Digit = '2', Label = "Status gangguan", Kind = OptionKind.Say, Text = "Saat ini tidak ada gangguan di area Anda." },
                    new OptionModel { Digit = '9', Label = "Kembali", Kind = OptionKind.GoToMenu, Target = "main" },
                ],
            },
        ],
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static FlowModel? FromJson(string json) => JsonSerializer.Deserialize<FlowModel>(json, Json);

    /// <summary>Turns the editable model into a runnable flow. Throws with a readable message when it is inconsistent.</summary>
    public IvrFlow Build(Func<VoipCall, IvrContext, CancellationToken, Task> aiHandler)
    {
        var builder = IvrFlow.Create().Welcome(Welcome);
        foreach (var menu in Menus)
        {
            builder.Menu(menu.Id, menu.Prompt, m =>
            {
                m.WaitFor(TimeSpan.FromSeconds(Math.Clamp(menu.TimeoutSeconds, 2, 30)));
                foreach (var option in menu.Options)
                {
                    IvrAction action = option.Kind switch
                    {
                        OptionKind.GoToMenu => new IvrAction.Goto(option.Target),
                        OptionKind.CollectDigits => new IvrAction.Collect(option.Key, option.Text, 12, option.Target),
                        OptionKind.Transfer => new IvrAction.Transfer(option.Target, option.Text),
                        OptionKind.Queue => new IvrAction.Enqueue(option.Target, option.Text),
                        OptionKind.AiAgent => new IvrAction.Handoff("ai-agent", aiHandler),
                        OptionKind.Say => new IvrAction.Say(option.Text),
                        _ => new IvrAction.Hangup(string.IsNullOrWhiteSpace(option.Text) ? null : option.Text),
                    };
                    m.Option(option.Digit, option.Label, action);
                }
            });
        }

        return builder.Build();
    }
}
