using Telepati.Client.Core;
using Telepati.Shared.Contracts;

namespace Telepati.UI.Services;

/// <summary>
/// Holds the palette the UI is currently painted with. The active theme comes from the server —
/// including any seasonal skin whose date window is open — and is translated into the CSS custom
/// properties the stylesheet reads, so a theme change never requires new CSS.
/// </summary>
public class ThemeState(ITelepatiClient client)
{
    private static readonly ThemeDto BundledLight = new(
        Guid.Empty, "Telepati Classic", "Tema bawaan",
        "#6C5CE7", "#00CEC9", "#FD79A8", "#FFFFFF", "#F5F6FA", "#2D3436",
        "💬", false, true, false, null, null);

    private static readonly ThemeDto BundledDark = new(
        Guid.Empty, "Telepati Midnight", "Mode gelap",
        "#A29BFE", "#55EFC4", "#FF7675", "#12131A", "#1C1E28", "#EDEEF3",
        "🌙", true, false, false, null, null);

    /// <summary>What the server says is active. Kept separate from what is painted.</summary>
    private ThemeDto _serverTheme = BundledLight;

    public event Action? Changed;

    /// <summary>
    /// The theme actually painted. When the user asks for dark but the active theme is a light
    /// one, the bundled dark palette stands in — otherwise the mode toggle would flip the icon
    /// and change nothing, because the six colour variables would still be the light ones.
    /// </summary>
    public ThemeDto Current => IsDark && !_serverTheme.IsDark ? BundledDark : _serverTheme;

    /// <summary>light | dark | system — the user's preference, independent of which theme is active.</summary>
    public string Mode { get; private set; } = "system";

    /// <summary>Resolved from <see cref="Mode"/> and the OS preference reported by the browser.</summary>
    public bool IsDark { get; private set; }

    public async Task LoadAsync(CancellationToken ct = default)
    {
        try
        {
            var theme = await client.GetActiveThemeAsync(ct);
            if (theme is not null)
            {
                _serverTheme = theme;

                // A theme that declares itself dark decides the mode; that is what makes a
                // seasonal night skin like Tahun Baru look right without any user action.
                if (theme.IsDark) IsDark = true;

                Changed?.Invoke();
            }
        }
        catch
        {
            // Offline or unreachable: the bundled palette keeps the app usable.
        }
    }

    public void SetMode(string mode, bool systemPrefersDark)
    {
        Mode = mode;
        IsDark = mode switch
        {
            "dark" => true,
            "light" => false,
            _ => systemPrefersDark || _serverTheme.IsDark
        };

        Changed?.Invoke();
    }

    /// <summary>Applies a theme directly — used by the admin console's live preview.</summary>
    public void Apply(ThemeDto theme)
    {
        _serverTheme = theme;
        IsDark = theme.IsDark;
        Changed?.Invoke();
    }

    /// <summary>The inline style block bound to the app root.</summary>
    public string ToCssVariables()
    {
        var theme = Current;

        return $"--tp-primary:{theme.PrimaryColor};" +
               $"--tp-secondary:{theme.SecondaryColor};" +
               $"--tp-accent:{theme.AccentColor};" +
               $"--tp-bg:{theme.BackgroundColor};" +
               $"--tp-surface:{theme.SurfaceColor};" +
               $"--tp-text:{theme.TextColor};";
    }

    public string SeasonalMark => Current.IsSeasonal ? Current.IconSet ?? string.Empty : string.Empty;
}
