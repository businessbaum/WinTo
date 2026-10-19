using Avalonia.Controls;

namespace MultiTool.Abstractions;

/// <summary>
/// The only type a module assembly has to expose.
/// </summary>
/// <remarks>
/// <para>Lifecycle, in this order:</para>
/// <code>
///   ctor            parameterless and trivial; runs inside the user's click
///   Initialize      once; load settings, nothing expensive
///   CreateView      every time a tab or window opens
///   ViewActivated   the view became visible
///   ViewDeactivated the view exists but is not visible
///   ViewClosed      tab or window closed; the module stays alive
///   Dispose         the host releases the module
/// </code>
/// <para>
/// <b>CreateView can run more than once</b>, so state belongs in the module, not the view.
/// </para>
/// <para>
/// <b>Resources.</b> A module's own <c>avares://</c> resources work. Avalonia resolves them by
/// <i>simple</i> assembly name and takes the first match, so the name must be unique (hence
/// the <c>MultiTool.Modules.</c> prefix), and the host must <b>never</b> reference a module's
/// <c>avares://</c>: that would load a second copy of the module into the default context.
/// </para>
/// <para>
/// <b>Don't tie state to visibility.</b> Switching tabs detaches the view, so
/// <c>OnDetachedFromVisualTree</c> is the wrong place to stop recordings, timers or audio
/// devices. Use <see cref="ViewClosed"/> and <see cref="IDisposable.Dispose"/>.
/// </para>
/// </remarks>
public interface IModule : IDisposable
{
    /// <summary>Once after loading. Keep it cheap; the user is waiting.</summary>
    void Initialize(IModuleContext context);

    /// <summary>Builds the view. Called again on every open.</summary>
    Control CreateView();

    /// <summary>The view became visible. Start timers, level meters and previews here.</summary>
    void ViewActivated(Control view);

    /// <summary>
    /// The view is alive but hidden (another tab, minimized). Stop whatever only runs for
    /// looking at it.
    /// </summary>
    /// <remarks>
    /// <b>Release nothing here.</b> Devices, threads, hooks and state belong to
    /// <see cref="ViewClosed"/> and <see cref="IDisposable.Dispose"/>; otherwise a tab switch
    /// would cut off the microphone.
    /// </remarks>
    void ViewDeactivated(Control view);

    /// <summary>The view was closed. The module itself stays loaded.</summary>
    void ViewClosed(Control view);

    /// <summary>
    /// Another module opened this one through <see cref="IShell.OpenModule(string, string)"/>
    /// and passed something along. Since 0.9.0.
    /// </summary>
    /// <remarks>
    /// Arrives <b>after</b> <see cref="CreateView"/> and <see cref="ViewActivated"/>, or alone
    /// if the module was already open. Empty by default so older modules keep loading.
    /// </remarks>
    void OpenWith(string argument) { }

    /// <summary>
    /// Whether closing would lose something the user can't get back: a search result, a filled shelf.
    /// Since 0.11.0.
    /// </summary>
    /// <remarks>
    /// Running work is covered by <see cref="ModuleActivity.IsBusy"/>; this is for finished results
    /// that live only in memory. The shell asks before closing only if one of the two is true.
    /// Empty by default so older modules keep loading.
    /// </remarks>
    bool HasUnsavedWork => false;

    /// <summary>What the module is doing right now. Shown in the footer and on the tile.</summary>
    ModuleActivity Activity { get; }

    event EventHandler<ModuleActivity>? ActivityChanged;
}

/// <param name="IsBusy">While <c>true</c>, the module is not released and quitting asks first.</param>
/// <param name="Text">What is happening, in a few words: "Downloading…".</param>
/// <param name="Progress">0…1, or <c>null</c> for indeterminate.</param>
public sealed record ModuleActivity(bool IsBusy, string? Text = null, double? Progress = null)
{
    public static readonly ModuleActivity Idle = new(false);
}
