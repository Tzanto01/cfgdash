namespace Cfgdash.Core.Tests.Support;

/// <summary>A fact that is reported as skipped on non-Windows hosts.</summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Windows only";
        }
    }
}
