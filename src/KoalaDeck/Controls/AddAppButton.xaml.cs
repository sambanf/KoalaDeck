using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using KoalaDeck.ViewModels;

namespace KoalaDeck.Controls;

/// <summary>
/// "+" button of an app channel. The popup lists running apps (playing ones marked) and installed Start menu apps,
/// with a search box; Enter picks the best match, or adds the typed text as a process name.
/// </summary>
public partial class AddAppButton : UserControl
{
    public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(
        nameof(ItemsSource), typeof(IEnumerable), typeof(AddAppButton),
        new PropertyMetadata(null, (d, e) => ((AddAppButton)d).RunningList.ItemsSource = ((AddAppButton)d).CreateView(e.NewValue)));

    public static readonly DependencyProperty InstalledItemsSourceProperty = DependencyProperty.Register(
        nameof(InstalledItemsSource), typeof(IEnumerable), typeof(AddAppButton),
        new PropertyMetadata(null, (d, e) => ((AddAppButton)d).InstalledList.ItemsSource = ((AddAppButton)d).CreateView(e.NewValue)));

    public static readonly DependencyProperty CommandProperty = DependencyProperty.Register(
        nameof(Command), typeof(ICommand), typeof(AddAppButton));

    public static readonly DependencyProperty OpeningCommandProperty = DependencyProperty.Register(
        nameof(OpeningCommand), typeof(ICommand), typeof(AddAppButton));

    private long _closedAt;

    public AddAppButton()
    {
        InitializeComponent();
    }

    /// <summary>Running apps (<see cref="AudioAppItem"/>).</summary>
    public IEnumerable? ItemsSource
    {
        get => (IEnumerable?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    /// <summary>Installed apps that are not running (<see cref="AudioAppItem"/>).</summary>
    public IEnumerable? InstalledItemsSource
    {
        get => (IEnumerable?)GetValue(InstalledItemsSourceProperty);
        set => SetValue(InstalledItemsSourceProperty, value);
    }

    /// <summary>Executed with the chosen process name.</summary>
    public ICommand? Command
    {
        get => (ICommand?)GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }

    /// <summary>Executed right before the popup opens.</summary>
    public ICommand? OpeningCommand
    {
        get => (ICommand?)GetValue(OpeningCommandProperty);
        set => SetValue(OpeningCommandProperty, value);
    }

    private string Search => SearchBox.Text.Trim();

    /// <summary>Own view per popup, so filtering one channel's list never filters the other's.</summary>
    private ICollectionView? CreateView(object? source)
    {
        if (source is not IList list)
        {
            return null;
        }

        var view = new ListCollectionView(list) { Filter = item => item is AudioAppItem app && app.Matches(Search) };
        ((INotifyCollectionChanged)view).CollectionChanged += (_, _) => UpdateNoMatch();
        return view;
    }

    private void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        // A click on the button while the popup is open first closes it (StaysOpen=False); don't reopen straight away.
        if (DropDown.IsOpen || Environment.TickCount64 - _closedAt <= 250)
        {
            return;
        }

        if (OpeningCommand?.CanExecute(null) == true)
        {
            OpeningCommand.Execute(null);
        }

        DropDown.IsOpen = true;
    }

    private void Item_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: AudioAppItem app })
        {
            Choose(app.Name);
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        (RunningList.ItemsSource as ICollectionView)?.Refresh();
        (InstalledList.ItemsSource as ICollectionView)?.Refresh();
        UpdateNoMatch();
    }

    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Search.Length > 0)
        {
            Choose(BestMatch()?.Name ?? Search);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            DropDown.IsOpen = false;
            e.Handled = true;
        }
    }

    /// <summary>Exact process-name match first, then the first visible running app, then the first installed one.</summary>
    private AudioAppItem? BestMatch()
    {
        IEnumerable<AudioAppItem> visible = Visible(RunningList).Concat(Visible(InstalledList)).ToList();
        return visible.FirstOrDefault(app => string.Equals(app.Name, Search, StringComparison.OrdinalIgnoreCase)
                || string.Equals(app.Name + ".exe", Search, StringComparison.OrdinalIgnoreCase))
            ?? visible.FirstOrDefault();

        static IEnumerable<AudioAppItem> Visible(ItemsControl list) =>
            (list.ItemsSource as ICollectionView)?.Cast<AudioAppItem>() ?? Enumerable.Empty<AudioAppItem>();
    }

    private void UpdateNoMatch()
    {
        bool empty = !RunningList.HasItems && !InstalledList.HasItems;
        NoMatch.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        NoMatch.Text = Search.Length == 0
            ? "No apps found."
            : $"No app matches “{Search}”. Press Enter to add “{Search}” as a process name.";
    }

    private void Choose(string name)
    {
        if (Command?.CanExecute(name) == true)
        {
            Command.Execute(name);
        }

        DropDown.IsOpen = false;
    }

    private void DropDown_Opened(object? sender, EventArgs e)
    {
        SearchBox.Text = string.Empty;
        SearchBox.Focus();
        UpdateNoMatch();
    }

    private void DropDown_Closed(object? sender, EventArgs e) => _closedAt = Environment.TickCount64;
}
