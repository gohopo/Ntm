namespace Ntm.Gateway;

/// <summary>启动前就能确定起不来的情况（数据目录被占、数据库比程序新等）。只打一行原因，不打堆栈。</summary>
public sealed class NtmStartupException : Exception
{
    public NtmStartupException()
    {
    }

    public NtmStartupException(string message)
        : base(message)
    {
    }

    public NtmStartupException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
