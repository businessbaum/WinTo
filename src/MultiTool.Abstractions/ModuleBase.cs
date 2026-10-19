using Avalonia.Controls;

namespace MultiTool.Abstractions;

/// <summary>Convenient base for a module. Only <see cref="Build"/> is required.</summary>
public abstract class ModuleBase : IModule
{
    private ModuleActivity _activity = ModuleActivity.Idle;
    private IModuleContext? _context;

    /// <summary>Available from <see cref="Initialize"/> on.</summary>
    protected IModuleContext Context =>
        _context ?? throw new InvalidOperationException("Initialize has not been called yet.");

    protected IShell Shell => Context.Shell;

    protected IModuleLog Log => Context.Log;

    public ModuleActivity Activity => _activity;

    public event EventHandler<ModuleActivity>? ActivityChanged;

    public virtual void Initialize(IModuleContext context)
    {
        _context = context;
        OnInitialize();
    }

    /// <summary>After <see cref="Context"/> is set. Keep it cheap.</summary>
    protected virtual void OnInitialize() { }

    public Control CreateView() => Build();

    /// <summary>Builds the view. Called again on every open.</summary>
    protected abstract Control Build();

    public virtual void ViewActivated(Control view) { }

    public virtual void ViewDeactivated(Control view) { }

    public virtual void ViewClosed(Control view) { }

    /// <inheritdoc cref="IModule.OpenWith"/>
    public virtual void OpenWith(string argument) { }

    /// <inheritdoc cref="IModule.HasUnsavedWork"/>
    public virtual bool HasUnsavedWork => false;

    /// <summary>Tells the shell what is running.</summary>
    protected void SetActivity(ModuleActivity activity)
    {
        if (_activity == activity) return;
        _activity = activity;
        ActivityChanged?.Invoke(this, activity);
    }

    protected void SetIdle() => SetActivity(ModuleActivity.Idle);

    /// <summary>Writes the status for the home page tile.</summary>
    protected void PublishStatus(StatusLevel level, string text, string? detail = null) =>
        Context.PublishStatus(new ModuleStatus(level, text, DateTimeOffset.Now, detail));

    public virtual void Dispose() => GC.SuppressFinalize(this);
}
