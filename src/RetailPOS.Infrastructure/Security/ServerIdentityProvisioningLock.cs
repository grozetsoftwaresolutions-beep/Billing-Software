using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace RetailPOS.Infrastructure.Security;

public sealed class LockTimeoutException : Exception
{
    public LockTimeoutException(string message) : base(message) { }
}

public sealed class ServerIdentityProvisioningLock
{
    public const string DefaultMutexName = @"Global\GrozetPOS_ServerIdentityProvisioningLock";
    public const string LockfileName = "server_identity.provisioning.lock";

    private static readonly SemaphoreSlim SharedInProcessSemaphore = new(1, 1);
    private readonly SemaphoreSlim _inProcessSemaphore;

    public ServerIdentityProvisioningLock(SemaphoreSlim? inProcessSemaphore = null)
    {
        _inProcessSemaphore = inProcessSemaphore ?? SharedInProcessSemaphore;
    }

    public async Task<IAsyncDisposable> AcquireAsync(
        string securityDirectory,
        string? customMutexName = null,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(15);
        string mutexName = customMutexName ?? DefaultMutexName;

        // Layer 1: In-process SemaphoreSlim
        bool semAcquired = await _inProcessSemaphore.WaitAsync(effectiveTimeout, ct);
        if (!semAcquired)
        {
            throw new LockTimeoutException("Layer 1 in-process semaphore acquisition timed out.");
        }

        Mutex? mutex = null;
        bool mutexAcquired = false;
        FileStream? lockStream = null;

        try
        {
            // Layer 2: Named OS Mutex
            mutex = CreateCrossProcessMutex(mutexName);
            try
            {
                mutexAcquired = mutex.WaitOne(effectiveTimeout);
            }
            catch (AbandonedMutexException)
            {
                // Abandoned mutex: previous process terminated abnormally. We now own it safely.
                mutexAcquired = true;
            }

            if (!mutexAcquired)
            {
                throw new LockTimeoutException("Layer 2 named OS mutex acquisition timed out.");
            }

            // Layer 3: Kernel FileStream Lockfile
            Directory.CreateDirectory(securityDirectory);
            string lockfilePath = Path.Combine(securityDirectory, LockfileName);

            var start = DateTime.UtcNow;
            while (DateTime.UtcNow - start < effectiveTimeout)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    lockStream = new FileStream(
                        lockfilePath,
                        FileMode.OpenOrCreate,
                        FileAccess.ReadWrite,
                        FileShare.None,
                        bufferSize: 4096,
                        FileOptions.DeleteOnClose);
                    break;
                }
                catch (IOException)
                {
                    // Lockfile held by another process; retry briefly
                    await Task.Delay(50, ct);
                }
            }

            if (lockStream is null)
            {
                throw new LockTimeoutException("Layer 3 kernel lockfile acquisition timed out.");
            }

            return new LockReleaser(_inProcessSemaphore, mutex, lockStream);
        }
        catch
        {
            // Clean LIFO unroll on failure
            if (lockStream is not null)
            {
                try { lockStream.Dispose(); } catch { }
            }
            if (mutex is not null)
            {
                if (mutexAcquired)
                {
                    try { mutex.ReleaseMutex(); } catch { }
                }
                mutex.Dispose();
            }
            _inProcessSemaphore.Release();
            throw;
        }
    }

    private static Mutex CreateCrossProcessMutex(string mutexName)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                var security = new MutexSecurity();
                var systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
                var adminSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
                var worldSid = new SecurityIdentifier(WellKnownSidType.WorldSid, null);

                security.AddAccessRule(new MutexAccessRule(systemSid, MutexRights.FullControl, AccessControlType.Allow));
                security.AddAccessRule(new MutexAccessRule(adminSid, MutexRights.FullControl, AccessControlType.Allow));
                security.AddAccessRule(new MutexAccessRule(worldSid, MutexRights.FullControl, AccessControlType.Allow));

                return MutexAcl.Create(initiallyOwned: false, mutexName, out _, security);
            }
            catch
            {
                try
                {
                    return new Mutex(initiallyOwned: false, mutexName);
                }
                catch (UnauthorizedAccessException) when (mutexName.StartsWith("Global\\", StringComparison.OrdinalIgnoreCase))
                {
                    var localName = "Local\\" + mutexName["Global\\".Length..];
                    return new Mutex(initiallyOwned: false, localName);
                }
            }
        }

        return new Mutex(initiallyOwned: false, mutexName);
    }

    private sealed class LockReleaser : IAsyncDisposable
    {
        private readonly SemaphoreSlim _semaphore;
        private readonly Mutex _mutex;
        private readonly FileStream _lockStream;
        private int _disposed;

        public LockReleaser(SemaphoreSlim semaphore, Mutex mutex, FileStream lockStream)
        {
            _semaphore = semaphore;
            _mutex = mutex;
            _lockStream = lockStream;
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                // Strict LIFO release order: Lockfile -> Mutex -> Semaphore
                try { _lockStream.Dispose(); } catch { }
                try { _mutex.ReleaseMutex(); } catch { }
                try { _mutex.Dispose(); } catch { }
                _semaphore.Release();
            }
            return ValueTask.CompletedTask;
        }
    }
}
