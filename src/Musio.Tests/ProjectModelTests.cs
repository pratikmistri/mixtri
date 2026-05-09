using Musio.Core.Models;
using Musio.Core.Capture;

namespace Musio.Tests;

[TestClass]
public sealed class ProjectModelTests
{
    #region Default Values

    [TestMethod]
    public void Project_DefaultId_IsUniqueGuid()
    {
        var p1 = new Project();
        var p2 = new Project();

        Assert.AreNotEqual(Guid.Empty, p1.Id);
        Assert.AreNotEqual(p1.Id, p2.Id, "Each project should get a unique Id");
    }

    [TestMethod]
    public void Project_DefaultName_IsEmpty()
    {
        var project = new Project();
        Assert.AreEqual(string.Empty, project.Name);
    }

    [TestMethod]
    public void Project_DefaultFps_Is30()
    {
        var project = new Project();
        Assert.AreEqual(30, project.Fps);
    }

    [TestMethod]
    public void Project_DefaultVideoPaths_AreEmpty()
    {
        var project = new Project();
        Assert.AreEqual(string.Empty, project.VideoFilePath);
        Assert.AreEqual(string.Empty, project.CursorDataFilePath);
    }

    [TestMethod]
    public void Project_DefaultOptionalPaths_AreNull()
    {
        var project = new Project();
        Assert.IsNull(project.WebcamFilePath);
        Assert.IsNull(project.KeyboardDataFilePath);
    }

    [TestMethod]
    public void Project_DefaultAudioFilePaths_IsEmptyList()
    {
        var project = new Project();
        Assert.IsNotNull(project.AudioFilePaths);
        Assert.AreEqual(0, project.AudioFilePaths.Count);
    }

    [TestMethod]
    public void Project_DefaultCaptureType_IsMonitor()
    {
        var project = new Project();
        Assert.AreEqual(CaptureTargetType.Monitor, project.CaptureType);
    }

    [TestMethod]
    public void Project_DefaultDimensions_AreZero()
    {
        var project = new Project();
        Assert.AreEqual(0, project.Width);
        Assert.AreEqual(0, project.Height);
        Assert.AreEqual(TimeSpan.Zero, project.Duration);
    }

    [TestMethod]
    public void Project_DefaultOffsets_AreZero()
    {
        var project = new Project();
        Assert.AreEqual(0.0, project.MouseToVideoOffsetSeconds);
        Assert.AreEqual(0.0, project.AudioToVideoOffsetSeconds);
        Assert.AreEqual(0, project.CropOffsetX);
        Assert.AreEqual(0, project.CropOffsetY);
        Assert.AreEqual(0f, project.DpiScale);
    }

    #endregion

    #region Property Assignment

    [TestMethod]
    public void Project_PropertyAssignment_RetainsValues()
    {
        var project = new Project
        {
            Name = "Test Recording",
            VideoFilePath = @"C:\recordings\video.mp4",
            Width = 1920,
            Height = 1080,
            Fps = 60,
            Duration = TimeSpan.FromSeconds(120),
            MouseToVideoOffsetSeconds = 0.15,
            AudioToVideoOffsetSeconds = -0.05,
            CropOffsetX = 100,
            CropOffsetY = 200,
            DpiScale = 1.5f,
        };

        Assert.AreEqual("Test Recording", project.Name);
        Assert.AreEqual(1920, project.Width);
        Assert.AreEqual(1080, project.Height);
        Assert.AreEqual(60, project.Fps);
        Assert.AreEqual(TimeSpan.FromSeconds(120), project.Duration);
        Assert.AreEqual(0.15, project.MouseToVideoOffsetSeconds, 0.001);
        Assert.AreEqual(-0.05, project.AudioToVideoOffsetSeconds, 0.001);
        Assert.AreEqual(100, project.CropOffsetX);
        Assert.AreEqual(200, project.CropOffsetY);
        Assert.AreEqual(1.5f, project.DpiScale);
    }

    [TestMethod]
    public void Project_AudioFilePaths_CanAddMultiple()
    {
        var project = new Project();
        project.AudioFilePaths.Add("system_audio.wav");
        project.AudioFilePaths.Add("mic_audio.wav");

        Assert.AreEqual(2, project.AudioFilePaths.Count);
    }

    #endregion

    #region CreatedAt

    [TestMethod]
    public void Project_CreatedAt_IsRecentTimestamp()
    {
        var before = DateTime.Now.AddSeconds(-1);
        var project = new Project();
        var after = DateTime.Now.AddSeconds(1);

        Assert.IsTrue(project.CreatedAt >= before && project.CreatedAt <= after,
            "CreatedAt should be near current time");
    }

    #endregion
}
