using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Musio_App.ViewModels;

public enum CaptureMode
{
    FullScreen,
    Window,
    CustomRegion
}

public partial class RecordingViewModel : ObservableObject
{
    [ObservableProperty]
    private CaptureMode _captureMode = CaptureMode.FullScreen;

    [ObservableProperty]
    private bool _isSystemAudioEnabled = true;

    [ObservableProperty]
    private bool _isMicEnabled;

    [ObservableProperty]
    private int _fps = 30;

    [ObservableProperty]
    private bool _isWebcamEnabled;

    [ObservableProperty]
    private string _recordingStatus = "Ready to record";

    [RelayCommand]
    private void StartRecording()
    {
        RecordingStatus = "Recording started…";
    }
}
