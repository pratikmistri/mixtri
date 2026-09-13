namespace Mixtri.Core.Shell;

/// <summary>Tracks work that must finish before its editor process can close.</summary>
public sealed class ProjectActivity
{
    private int _count;
    public bool IsBusy => Volatile.Read(ref _count) != 0;

    public IDisposable Begin()
    {
        Interlocked.Increment(ref _count);
        return new Lease(this);
    }

    private sealed class Lease(ProjectActivity owner) : IDisposable
    {
        private ProjectActivity? _owner = owner;
        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            if (owner is not null) Interlocked.Decrement(ref owner._count);
        }
    }
}
