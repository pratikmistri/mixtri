namespace Musio_App.Helpers;

/// <summary>
/// The app's display name, read from the package manifest at runtime rather than hard-coded.
/// </summary>
/// <remarks>
/// A locally deployed dev build is labelled <c>"Musio (dev)"</c> so it is instantly
/// distinguishable from the Store build, which stays plain <c>"Musio"</c>. The suffix is
/// applied to the generated <c>AppxManifest.xml</c> by the <c>ApplyMusioDevBranding</c> target
/// in <c>Musio.App.csproj</c> — see that target for when it does and does not run.
/// <para>
/// Every in-app surface that shows the app's name reads it from here, so the manifest stays
/// the single source of truth and no UI has to know whether it is a dev build. Do NOT re-derive
/// the "(dev)" suffix in app code: whether it applies is a packaging-time decision, and a second
/// copy of that rule would drift from the manifest the app is actually registered under.
/// </para>
/// </remarks>
internal static class AppBranding
{
    private static readonly Lazy<string> LazyDisplayName = new(Resolve);

    /// <summary>"Musio" for the Store build, "Musio (dev)" for a local dev deployment.</summary>
    public static string DisplayName => LazyDisplayName.Value;

    private const string Fallback = "Musio";

    private static string Resolve()
    {
        // Package.Current throws when the app somehow runs unpackaged. It never should
        // (WindowsPackageType is MSIX), but the app's name is not worth a startup crash.
        try
        {
            var name = Windows.ApplicationModel.Package.Current.DisplayName;
            return string.IsNullOrWhiteSpace(name) ? Fallback : name;
        }
        catch
        {
            return Fallback;
        }
    }
}
