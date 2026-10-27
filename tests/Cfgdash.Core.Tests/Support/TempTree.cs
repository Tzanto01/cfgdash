namespace Cfgdash.Core.Tests.Support;

/// <summary>A unique temp directory for fixture files, deleted recursively on dispose.</summary>
public sealed class TempTree : IDisposable
{
    public TempTree()
    {
        Root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cfgdash-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string Path(string relative) => System.IO.Path.GetFullPath(System.IO.Path.Combine(Root, relative));

    public string Write(string relative, string content)
    {
        var path = Path(relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    public string Dir(string relative)
    {
        var path = Path(relative);
        Directory.CreateDirectory(path);
        return path;
    }

    public void Dispose()
    {
        try
        {
            Delete(new DirectoryInfo(Root));
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void Delete(DirectoryInfo dir)
    {
        if (!dir.Exists)
        {
            return;
        }

        foreach (var child in dir.EnumerateFileSystemInfos())
        {
            if (child is DirectoryInfo sub && sub.LinkTarget is null)
            {
                Delete(sub);
            }
            else
            {
                child.Attributes = FileAttributes.Normal;
                child.Delete();
            }
        }

        dir.Delete();
    }
}
