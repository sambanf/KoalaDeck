using System.ComponentModel;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using KoalaDeck.Controls;
using KoalaDeck.Interop;
using KoalaDeck.ViewModels;

namespace KoalaDeck;

public partial class MainWindow : Window
{
    private const int CaptionRgb = 0x0B0D12; // BgColor
    private const int BorderRgb = 0x252B39;  // BorderColor
    private const string AppDragFormat = "KoalaDeck.AppName";

    private readonly MainViewModel _viewModel;
    private bool _allowClose;
    private FrameworkElement? _dragCandidate;
    private Point _dragStart;
    private DragGhostAdorner? _dragGhost;

    public MainWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();

        SourceInitialized += (_, _) =>
        {
            NativeMethods.ApplyDarkTitleBar(new WindowInteropHelper(this).Handle, CaptionRgb, BorderRgb);
            FitToWorkArea();
        };
        IsVisibleChanged += (_, _) => UpdateLiveUpdates();
        StateChanged += (_, _) => UpdateLiveUpdates();
    }

    /// <summary>Raised when the X button hid the window instead of closing the app.</summary>
    public event EventHandler? HiddenToTray;

    public void ShowDashboard()
    {
        if (!IsVisible)
        {
            Show();
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        Topmost = true; // reliably bring the window to the front when opened from the tray
        Topmost = false;
        Focus();
    }

    public void CloseForExit()
    {
        _allowClose = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose)
        {
            // X button: keep running in the system tray.
            e.Cancel = true;
            Hide();
            HiddenToTray?.Invoke(this, EventArgs.Empty);
        }

        base.OnClosing(e);
    }

    /// <summary>Smaller screens (or 125–150 % scaling): never open taller or wider than the work area.</summary>
    private void FitToWorkArea()
    {
        Rect area = SystemParameters.WorkArea;
        MinHeight = Math.Min(MinHeight, area.Height);
        MinWidth = Math.Min(MinWidth, area.Width);
        Height = Math.Min(Height, area.Height);
        Width = Math.Min(Width, area.Width);
        Top = area.Top + (area.Height - Height) / 2;
        Left = area.Left + (area.Width - Width) / 2;
    }

    private void UpdateLiveUpdates() =>
        _viewModel.SetLiveUpdates(IsVisible && WindowState != WindowState.Minimized);

    private void VolumeSlider_DragStarted(object sender, DragStartedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ChannelViewModel channel })
        {
            channel.IsUserAdjusting = true;
        }
    }

    private void VolumeSlider_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ChannelViewModel channel })
        {
            channel.IsUserAdjusting = false;
        }
    }

    // ------------------------------------------------------------------ drag & drop of apps between channels

    private void AppRow_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragCandidate = sender as FrameworkElement;
        _dragStart = e.GetPosition(RootGrid);
    }

    private void AppRow_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || !ReferenceEquals(sender, _dragCandidate)
            || _dragCandidate is not { DataContext: AppRowViewModel row } source)
        {
            return;
        }

        Point position = e.GetPosition(RootGrid);
        if (Math.Abs(position.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(position.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        _dragCandidate = null;
        StartDrag(source, row, e.GetPosition(source));
    }

    private void StartDrag(FrameworkElement source, AppRowViewModel row, Point grabOffset)
    {
        AdornerLayer? layer = AdornerLayer.GetAdornerLayer(RootGrid);
        if (layer is not null)
        {
            _dragGhost = new DragGhostAdorner(RootGrid, source, grabOffset);
            layer.Add(_dragGhost);
            _dragGhost.MoveTo(Mouse.GetPosition(RootGrid));
        }

        row.IsDragging = true;
        try
        {
            DragDrop.DoDragDrop(source, new DataObject(AppDragFormat, row.Name), DragDropEffects.Move);
        }
        finally
        {
            row.IsDragging = false;
            if (_dragGhost is not null)
            {
                layer?.Remove(_dragGhost);
                _dragGhost = null;
            }

            SetDropTarget(null);
        }
    }

    private void Root_PreviewDragOver(object sender, DragEventArgs e) =>
        _dragGhost?.MoveTo(e.GetPosition(RootGrid));

    /// <summary>Reached only outside the channel cards (they mark the event handled).</summary>
    private void Root_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = DragDropEffects.None;
        SetDropTarget(null);
        e.Handled = true;
    }

    private void Card_DragOver(object sender, DragEventArgs e)
    {
        ChannelViewModel? channel = (sender as FrameworkElement)?.DataContext as ChannelViewModel;
        bool accept = channel is not null && e.Data.GetDataPresent(AppDragFormat);
        e.Effects = accept ? DragDropEffects.Move : DragDropEffects.None;
        SetDropTarget(accept ? channel : null);
        e.Handled = true;
    }

    private void Card_Drop(object sender, DragEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ChannelViewModel channel } && e.Data.GetData(AppDragFormat) is string name)
        {
            _viewModel.MoveApp(name, channel.Index);
        }

        SetDropTarget(null);
        e.Handled = true;
    }

    private void SetDropTarget(ChannelViewModel? target)
    {
        foreach (ChannelViewModel channel in _viewModel.Channels)
        {
            channel.IsDropTarget = ReferenceEquals(channel, target);
        }
    }
}
