using CommunityToolkit.Mvvm.ComponentModel;
using VoipNet.Gallery.Infrastructure;
using VoipNet.Gallery.Pages;

namespace VoipNet.Gallery;

/// <summary>A heading in the navigation rail, or a page under it.</summary>
public sealed record NavItem(string Label, DemoPage? Page)
{
    public bool IsHeading => Page is null;

    public bool IsPage => Page is not null;
}

public sealed partial class GalleryViewModel : ObservableObject
{
    private readonly Avalonia.Threading.DispatcherTimer _timer;

    public GalleryViewModel()
    {
        DemoPage[] pages =
        [
            new OverviewPage(),
            new FirstCallPage(),
            new CodecsPage(),
            new DtmfPage(),
            new HoldTransferPage(),
            new ConferencePage(),
            new RecordingPage(),
            new SecurityPage(),
            new ChatModelsPage(),
            new VoiceAgentPage(),
            new IvrPage(),
            new CallCenterPage(),
            new DiagnosticsPage(),
        ];

        foreach (var group in pages.GroupBy(p => p.Section))
        {
            Navigation.Add(new NavItem(group.Key, null));
            foreach (var page in group)
            {
                Navigation.Add(new NavItem(page.Title, page));
            }
        }

        Pages = pages;
        SelectedItem = Navigation[1];
        _timer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    public List<NavItem> Navigation { get; } = [];

    public IReadOnlyList<DemoPage> Pages { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Current))]
    public partial NavItem? SelectedItem { get; set; }

    public DemoPage? Current => SelectedItem?.Page;

    public void Tick() => Current?.Tick();

    partial void OnSelectedItemChanging(NavItem? oldValue, NavItem? newValue)
    {
        if (newValue is { IsHeading: true })
        {
            return;
        }

        if (oldValue?.Page is { } previous && !ReferenceEquals(previous, newValue?.Page))
        {
            _ = previous.ResetAsync();
        }
    }

    partial void OnSelectedItemChanged(NavItem? value)
    {
        // Headings are not selectable: move to the first page below.
        if (value is { IsHeading: true })
        {
            var index = Navigation.IndexOf(value);
            SelectedItem = index + 1 < Navigation.Count ? Navigation[index + 1] : null;
        }
    }
}
