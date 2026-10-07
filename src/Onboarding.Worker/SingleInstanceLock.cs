namespace Onboarding.Worker;

/// <summary>
/// Ensures only one worker runs per database (DECISIONS B3): an exclusively opened lock file next
/// to the database, on Windows additionally a machine-wide named mutex.
/// </summary>
public sealed class SingleInstanceLock : IDisposable
{
    private readonly FileStream _file;
    private readonly Mutex? _mutex;

    private SingleInstanceLock(FileStream file, Mutex? mutex)
    {
        _file = file;
        _mutex = mutex;
    }

    public string Path => _file.Name;

    /// <summary>Acquires the lock or throws <see cref="InvalidOperationException"/> if another worker holds it.</summary>
    public static SingleInstanceLock Acquire(string databaseFilePath, string mutexName = @"Global\Onboarding.Worker")
    {
        var lockPath = databaseFilePath + ".worker.lock";
        Mutex? mutex = null;
        if (OperatingSystem.IsWindows())
        {
            mutex = new Mutex(initiallyOwned: false, mutexName);
            bool owned;
            try
            {
                owned = mutex.WaitOne(TimeSpan.Zero);
            }
            catch (AbandonedMutexException)
            {
                owned = true; // previous worker crashed
            }

            if (!owned)
            {
                mutex.Dispose();
                throw new InvalidOperationException($"Another worker is already running (mutex {mutexName}).");
            }
        }

        try
        {
            var file = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            return new SingleInstanceLock(file, mutex);
        }
        catch (IOException ex)
        {
            if (mutex is not null)
            {
                mutex.ReleaseMutex();
                mutex.Dispose();
            }

            throw new InvalidOperationException($"Another worker is already running (lock file {lockPath}).", ex);
        }
    }

    public void Dispose()
    {
        _file.Dispose();
        if (_mutex is not null)
        {
            _mutex.ReleaseMutex();
            _mutex.Dispose();
        }
    }
}
