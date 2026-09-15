using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using AvaloniaEdit.TextMate;
using RumbleGallery.ViewModels;
using TextMateSharp.Grammars;

namespace RumbleGallery;

public partial class MainWindow : Window
{
    private MainViewModel? _viewModel;

    public MainWindow()
    {
        InitializeComponent();

        var registry = new RegistryOptions(ThemeName.DarkPlus);
        var textMate = CodeEditor.InstallTextMate(registry);
        textMate.SetGrammar(registry.GetScopeByLanguageId(registry.GetLanguageByExtension(".cs").Id));

        DataContextChanged += (_, _) => Attach(DataContext as MainViewModel);
    }

    private void Attach(MainViewModel? viewModel)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelChanged;
            _viewModel.Output.CollectionChanged -= OnOutputChanged;
        }

        _viewModel = viewModel;
        if (viewModel is null)
        {
            return;
        }

        viewModel.PropertyChanged += OnViewModelChanged;
        viewModel.Output.CollectionChanged += OnOutputChanged;
        viewModel.CopyToClipboard = async text =>
        {
            if (Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(text);
            }
        };
        CodeEditor.Text = viewModel.Code;
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.Code) && _viewModel is not null)
        {
            CodeEditor.Text = _viewModel.Code;
            CodeEditor.ScrollToHome();
        }
    }

    private void OnOutputChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && _viewModel is { Output.Count: > 0 } vm)
        {
            Dispatcher.UIThread.Post(() => OutputList.ScrollIntoView(vm.Output[^1]), DispatcherPriority.Background);
        }
    }
}
