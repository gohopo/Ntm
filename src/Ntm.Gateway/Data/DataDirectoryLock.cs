namespace Ntm.Gateway.Data;

/// <summary>
/// 一个数据目录同一时刻只能有一个网关在用（Q341，ADR-0003）：启动时独占地打开 <c>ntm.lock</c>，
/// 进程退出时操作系统会释放它，不会留下需要手工删的锁。restore 也靠它判断网关是不是还在运行。
/// </summary>
public sealed class DataDirectoryLock : IDisposable
{
    public const string FileName = "ntm.lock";

    private readonly FileStream _stream;

    private DataDirectoryLock(FileStream stream) => _stream = stream;

    /// <summary>拿到锁；已经被别的进程（或本进程里别的网关）拿着时抛 <see cref="NtmStartupException"/>。</summary>
    public static DataDirectoryLock Acquire(string directory)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, FileName);
        try
        {
            return new DataDirectoryLock(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
        }
        catch (IOException ex)
        {
            throw new NtmStartupException($"数据目录 {directory} 正被另一个网关使用（{FileName} 被占用）。同一个数据目录只能跑一个网关。", ex);
        }
    }

    public void Dispose() => _stream.Dispose();
}

/// <summary>网关进程持有的那把数据目录锁，跟着服务容器一起释放。</summary>
public sealed class DataDirectoryGuard : IDisposable
{
    private DataDirectoryLock? _lock;

    internal void Hold(string directory) => _lock ??= DataDirectoryLock.Acquire(directory);

    public void Dispose() => _lock?.Dispose();
}
