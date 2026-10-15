namespace NovaTune.Core.Tests;

/// <summary>Временная папка теста; удаляется после теста.</summary>
public sealed class TestDir : IDisposable
{
    public TestDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "novatune-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public string WriteFile(string relative, string content)
    {
        var full = Combine(relative.Split('/'));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // Временная папка будет удалена системой позже.
        }
    }
}
