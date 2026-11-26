namespace EnCopy;

/// <summary>
/// 编译期区分日常版（EnCopy）与开发版（EnCopy Dev），保证单实例互斥、注册表项与数据目录完全隔离。
/// </summary>
public static class AppIdentity
{
#if ENCOPY_DAILY
    public const bool IsDev = false;
    public const string AppName = "EnCopy";
    public const string MutexName = @"Local\app.encopy.EnCopy";
    public const string RegistryRunValueName = "EnCopy";
#else
    public const bool IsDev = true;
    public const string AppName = "EnCopy Dev";
    public const string MutexName = @"Local\app.encopy.EnCopy.dev";
    public const string RegistryRunValueName = "EnCopyDev";
#endif
}
