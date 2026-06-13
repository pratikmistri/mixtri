using System;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Musio_App.Pages;
using Musio_App.ViewModels;

namespace Musio_App.Controls;

/// <summary>
/// Visual content of the full app shell: TitleBar + NavigationView + content
/// Frame. Hosted today by <see cref="MainWindow"/>; in Phase B the same
/// control will also be hosted inside the unified <c>AppShellWindow</c>.
/// </summary>
public sealed partial class FullShellControl : UserControl
{
    private RecordingViewModel? _dockedPillViewModel;

    /// <summary>Exposes the navigation frame so the host window / App can reach the current page.</summary>
    public Frame ContentFrame => NavFrame;

    /// <summary>Title bar element so the host window can call <c>SetTitleBar</c>.</summary>
    public TitleBar TitleBarElement => AppTitleBar;

    /// <summary>
    /// Show or hide the static Collapse-to-Mini button in the title bar. The
    /// shell window flips this to <c>true</c> when in the <c>Full</c> state
    /// and <c>false</c> when the docked pill takes over in <c>FullRecording</c>.
    /// </summary>
    public bool IsCollapseButtonVisible
    {
        get => CollapseButton.Visibility == Visibility.Visible;
        set => CollapseButton.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Raised when the user clicks the static Collapse-to-Mini button in
    /// <c>Full</c> state (host morphs Full → MiniSetup).
    /// </summary>
    public event EventHandler? CollapseRequested;

    /// <summary>
    /// Raised when the user clicks the Stop button in the docked title-bar
    /// pill while in <c>FullRecording</c>.
    /// </summary>
    public event EventHandler? DockedPillStopRequested;

    /// <summary>
    /// Raised when the user clicks the Collapse button in the docked
    /// title-bar pill while in <c>FullRecording</c> (host morphs
    /// FullRecording → MiniRecording).
    /// </summary>
    public event EventHandler? DockedPillCollapseRequested;

    public FullShellControl()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Reveal the docked title-bar recording pill and bind it to the supplied
    /// <see cref="RecordingViewModel"/> so the elapsed-time text updates in
    /// real time. Hides the static Collapse-to-Mini button while shown.
    /// </summary>
    /// <remarks>
    /// Phase B uses a simplified title-bar treatment (elapsed text + Stop +
    /// Collapse) rather than the full <see cref="RecordingPillControl"/>
    /// visuals; the stopping ticker animation does not run in this slot.
    /// Promoting to the full pill (and adding the connected-element morph)
    /// is Phase C work — see spec §4.3.1 / §4.6.
    /// </remarks>
    public void ShowDockedPill(RecordingViewModel viewModel)
    {
        if (viewModel is null) throw new ArgumentNullException(nameof(viewModel));

        if (_dockedPillViewModel is not null)
            _dockedPillViewModel.PropertyChanged -= OnDockedPillViewModelPropertyChanged;

        _dockedPillViewModel = viewModel;
        DockedElapsedText.Text = _dockedPillViewModel.ElapsedTime;
        _dockedPillViewModel.PropertyChanged += OnDockedPillViewModelPropertyChanged;

        DockedStopButton.IsEnabled = true;
        DockedPillPanel.Visibility = Visibility.Visible;
        CollapseButton.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Tear down the docked title-bar pill (used when leaving
    /// <c>FullRecording</c>). Safe to call when the pill is already hidden.
    /// </summary>
    public void HideDockedPill()
    {
        if (_dockedPillViewModel is not null)
        {
            _dockedPillViewModel.PropertyChanged -= OnDockedPillViewModelPropertyChanged;
            _dockedPillViewModel = null;
        }
        DockedPillPanel.Visibility = Visibility.Collapsed;
    }

    private void OnDockedPillViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(RecordingViewModel.ElapsedTime)) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_dockedPillViewModel is not null)
                DockedElapsedText.Text = _dockedPillViewModel.ElapsedTime;
        });
    }

    private void TitleBar_PaneToggleRequested(TitleBar sender, object args)
    {
        NavView.IsPaneOpen = !NavView.IsPaneOpen;
    }

    private void TitleBar_BackRequested(TitleBar sender, object args)
    {
        NavFrame.GoBack();
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.IsSettingsSelected)
        {
            NavFrame.Navigate(typeof(SettingsPage));
        }
        else if (args.SelectedItem is NavigationViewItem item)
        {
            switch (item.Tag)
            {
                case "record":
                    NavFrame.Navigate(typeof(RecordingPage));
                    break;
                case "editor":
                    NavFrame.Navigate(typeof(EditorPage));
                    break;
                default:
                    System.Diagnostics.Debug.WriteLine($"[FullShellControl] Unknown navigation item tag: {item.Tag}");
                    break;
            }
        }
    }

    private void CollapseButton_Click(object sender, RoutedEventArgs e)
    {
        CollapseRequested?.Invoke(this, EventArgs.Empty);
    }

    private void DockedStopButton_Click(object sender, RoutedEventArgs e)
    {
        DockedStopButton.IsEnabled = false;
        _dockedPillViewModel?.NotifyStopRequested();
        DockedPillStopRequested?.Invoke(this, EventArgs.Empty);
    }

    private void DockedCollapseButton_Click(object sender, RoutedEventArgs e)
    {
        DockedPillCollapseRequested?.Invoke(this, EventArgs.Empty);
    }
}
