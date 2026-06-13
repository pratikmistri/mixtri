using System;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Musio_App.ViewModels;

namespace Musio_App.Controls;

/// <summary>
/// Visual content of the recording pill (elapsed time + Stop + the "Stopping…"
/// ticker). Hosted today inside <c>RecordingOverlayWindow</c>; in Phase B it
/// will also be hosted inside the unified <c>AppShellWindow</c>.
/// </summary>
public sealed partial class RecordingPillControl : UserControl
{
    private RecordingViewModel? _viewModel;
    private bool _stopRequested;
    private Microsoft.UI.Xaml.Media.Animation.Storyboard? _currentPhraseStoryboard;

    private static readonly string[] StoppingPhrases =
    [
        "Engaging…",
        "Standby…",
        "Energizing…",
        "Processing…",
        "Computing…",
        "Make it…",
        "Hold steady",
        "All stop",
    ];
    private DispatcherTimer? _phraseTimer;
    private int _phraseIndex;
    private bool _showingA = true;

    /// <summary>Raised when the user requests to stop recording (Stop button).</summary>
    public event EventHandler? StopRequested;

    /// <summary>
    /// Raised when the user clicks the Expand-to-Full button (only visible in
    /// the MiniRecording state). The host shell window morphs to
    /// <c>FullRecording</c>; the active recording is NOT stopped.
    /// </summary>
    public event EventHandler? ExpandRequested;

    public RecordingPillControl()
    {
        InitializeComponent();
        Unloaded += OnUnloaded;
    }

    /// <summary>
    /// Show or hide the Expand-to-Full button. The button is hidden by default
    /// so the recording overlay window (Phase A) doesn't accidentally surface
    /// it; <c>AppShellWindow</c> sets it true while in MiniRecording.
    /// </summary>
    public bool IsExpandButtonVisible
    {
        get => ExpandButton.Visibility == Visibility.Visible;
        set => ExpandButton.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Override the pill's root background brush (used by the host window
    /// to fall back to a solid colour when Desktop Acrylic is unavailable).
    /// </summary>
    public void SetRootBackground(Microsoft.UI.Xaml.Media.Brush brush)
    {
        RootGrid.Background = brush;
    }

    /// <summary>
    /// Bind the pill to the shared <see cref="RecordingViewModel"/> so the
    /// elapsed-time text updates in real time. Safe to call multiple times.
    /// </summary>
    public void Initialize(RecordingViewModel viewModel)
    {
        if (viewModel is null) throw new ArgumentNullException(nameof(viewModel));

        if (_viewModel is not null)
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;

        _viewModel = viewModel;
        ElapsedText.Text = _viewModel.ElapsedTime;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    /// <summary>
    /// Detach from the view model and stop any in-progress animations so the
    /// containing window can be closed safely.
    /// </summary>
    public void Teardown()
    {
        _phraseTimer?.Stop();
        _phraseTimer = null;
        _currentPhraseStoryboard?.Stop();
        _currentPhraseStoryboard = null;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel = null;
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // Stop the phrase timer + storyboard and drop the VM subscription so a
        // future host (Phase B's AppShellWindow swapping the pill out) can't
        // leak a ticking DispatcherTimer or a re-entering storyboard on a
        // detached control.
        Teardown();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RecordingViewModel.ElapsedTime))
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_viewModel is not null)
                    ElapsedText.Text = _viewModel.ElapsedTime;
            });
        }
    }

    /// <summary>
    /// Reset the pill back to its initial recording UI: timer + Stop + Expand
    /// row visible, stopping ticker hidden, Stop button re-enabled, and the
    /// internal "stop requested" latch cleared. Required because the pill is
    /// a long-lived instance inside <c>AppShellWindow</c> (it goes
    /// <see cref="Visibility.Collapsed"/> rather than unloaded between
    /// recordings), so without an explicit reset a second Record cycle would
    /// surface in the disabled stopping state.
    /// </summary>
    public void ResetToRecordingState()
    {
        // Tear down any in-flight stopping animation.
        _phraseTimer?.Stop();
        _phraseTimer = null;
        _currentPhraseStoryboard?.Stop();
        _currentPhraseStoryboard = null;

        // Restore the recording-row UI.
        RecordingPanel.Visibility = Visibility.Visible;
        ButtonRow.Visibility = Visibility.Visible;
        StoppingTextHost.Visibility = Visibility.Collapsed;
        StoppingSpinner.Visibility = Visibility.Collapsed;

        StopButton.IsEnabled = true;
        ExpandButton.IsEnabled = true;

        _stopRequested = false;

        // Keep the elapsed-time text fresh on re-entry (the VM may already
        // be at 00:00 for the new recording).
        if (_viewModel is not null)
            ElapsedText.Text = _viewModel.ElapsedTime;
    }

    /// <summary>
    /// Stop the stopping-ticker timer + storyboard without dropping the
    /// view-model subscription. Called by the host shell when the pill is
    /// about to be hidden (cross-fade out), so a hidden pill doesn't keep
    /// churning CPU on a stopping animation no one can see.
    /// </summary>
    public void PauseTickerWhileHidden()
    {
        _phraseTimer?.Stop();
        _phraseTimer = null;
        _currentPhraseStoryboard?.Stop();
        _currentPhraseStoryboard = null;
    }

    private async void Stop_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_stopRequested) return;
            _stopRequested = true;
            _viewModel?.NotifyStopRequested();
            StopButton.IsEnabled = false;
            ExpandButton.IsEnabled = false;
            ShowStoppingState();

            // Yield to let the UI render the stopping state before the heavy stop work begins
            await System.Threading.Tasks.Task.Delay(50);

            StopRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[RecordingPillControl] Stop_Click failed: {ex.Message}");
        }
    }

    private void Expand_Click(object sender, RoutedEventArgs e)
    {
        // Don't fire after a stop request — the pill is on its way out and the
        // shell should not start morphing to FullRecording underneath it.
        if (_stopRequested) return;
        ExpandRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Swap the pill from "elapsed time + Stop" into the animated
    /// "Stopping…" ticker. Public so the host window can trigger this when
    /// the user closes the overlay window directly (mirrors today's
    /// <c>RecordingOverlayWindow.OnOverlayClosing</c> path).
    /// </summary>
    public void ShowStoppingState()
    {
        // Swap to the stopping UI (collapse the whole button row so the
        // Expand button — when surfaced in MiniRecording — also goes away).
        RecordingPanel.Visibility = Visibility.Collapsed;
        ButtonRow.Visibility = Visibility.Collapsed;
        StoppingTextHost.Visibility = Visibility.Visible;
        StoppingSpinner.Visibility = Visibility.Visible;

        _phraseTimer?.Stop();

        // Cycle through phrases with a vertical flip-ticker animation
        _phraseIndex = 0;
        _showingA = true;
        StoppingTextA.Text = StoppingPhrases[0];
        StoppingTextA.Opacity = 1;
        StoppingTextATranslate.Y = 0;
        StoppingTextB.Opacity = 0;
        StoppingTextBTranslate.Y = StoppingTextHost.Height;

        _phraseTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _phraseTimer.Tick += (_, _) =>
        {
            _phraseIndex = (_phraseIndex + 1) % StoppingPhrases.Length;
            AnimateToPhrase(StoppingPhrases[_phraseIndex]);
        };
        _phraseTimer.Start();
    }

    /// <summary>True once Stop has been triggered (idempotent guard for the host window).</summary>
    public bool IsStopRequested => _stopRequested;

    /// <summary>
    /// Mark Stop as already-requested without firing the event. Used by hosts
    /// that initiate the stop themselves (e.g. user closes the overlay
    /// window) before delegating to <see cref="ShowStoppingState"/>.
    /// </summary>
    public void MarkStopRequested() => _stopRequested = true;

    private void AnimateToPhrase(string newPhrase)
    {
        double height = StoppingTextHost.Height;
        var outgoing = _showingA ? StoppingTextA : StoppingTextB;
        var incoming = _showingA ? StoppingTextB : StoppingTextA;
        var outgoingTranslate = _showingA ? StoppingTextATranslate : StoppingTextBTranslate;
        var incomingTranslate = _showingA ? StoppingTextBTranslate : StoppingTextATranslate;

        // Pre-position the incoming text below the host and set its content
        incoming.Text = newPhrase;
        incoming.Opacity = 0;
        incomingTranslate.Y = height;

        var duration = TimeSpan.FromMilliseconds(350);
        var easing = new Microsoft.UI.Xaml.Media.Animation.CubicEase
        {
            EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseInOut,
        };

        var sb = new Microsoft.UI.Xaml.Media.Animation.Storyboard();

        // Outgoing: slide up + fade out
        var outY = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
        {
            To = -height,
            Duration = duration,
            EasingFunction = easing,
        };
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(outY, outgoingTranslate);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(outY, "Y");
        sb.Children.Add(outY);

        var outOp = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
        {
            To = 0,
            Duration = duration,
        };
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(outOp, outgoing);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(outOp, "Opacity");
        sb.Children.Add(outOp);

        // Incoming: slide up from below + fade in
        var inY = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
        {
            To = 0,
            Duration = duration,
            EasingFunction = easing,
        };
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(inY, incomingTranslate);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(inY, "Y");
        sb.Children.Add(inY);

        var inOp = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
        {
            To = 1,
            Duration = duration,
        };
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(inOp, incoming);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(inOp, "Opacity");
        sb.Children.Add(inOp);

        _currentPhraseStoryboard?.Stop();
        _currentPhraseStoryboard = sb;
        sb.Completed += (_, _) =>
        {
            if (ReferenceEquals(_currentPhraseStoryboard, sb)) _currentPhraseStoryboard = null;
        };
        sb.Begin();
        _showingA = !_showingA;
    }
}
