namespace Cfgdash.Core.Tests.Support;

/// <summary>A fact that is reported as skipped on non-Linux hosts.</summary>
public sealed class LinuxFactAttribute : FactAttribute
{
    public LinuxFactAttribute()
    {
        if (!OperatingSystem.IsLinux())
        {
            Skip = "Linux only";
        }
    }
}
