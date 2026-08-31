using System.IO;

namespace Sesame.Services;

public readonly record struct PackPayloadSource(string LocalPath, string RemoteDir, bool IsDirectory);
public readonly record struct OwnedFileUploadPlan(string LocalPath, string RemoteDir, string RemoteName,
    string RemotePath);

public static class PackOwnershipPlanner
{
    public static List<OwnedFileUploadPlan> Build(IReadOnlyList<PackPayloadSource> jobs)
    {
        var result = new List<OwnedFileUploadPlan>();
        foreach (var job in jobs)
        {
            if (!job.IsDirectory)
            {
                var name = Path.GetFileName(job.LocalPath);
                result.Add(new OwnedFileUploadPlan(job.LocalPath, job.RemoteDir, name,
                    Normalize(DeckClient.Combine(job.RemoteDir, name))));
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(job.LocalPath, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(job.LocalPath, file).Replace('\\', '/');
                if (relative.StartsWith("../", StringComparison.Ordinal) || relative == ".." ||
                    Path.IsPathRooted(relative))
                    throw new InvalidDataException("Prepared payload escapes its pack root: " + relative);
                var remotePath = Normalize(DeckClient.Combine(job.RemoteDir, relative));
                result.Add(new OwnedFileUploadPlan(file, DeckClient.Parent(remotePath), Path.GetFileName(remotePath),
                    remotePath));
            }
        }

        var duplicate = result.GroupBy(upload => upload.RemotePath, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw new InvalidDataException("Pack maps multiple payload files to the same target: " + duplicate.Key);
        return result;
    }

    private static string Normalize(string path) =>
        (path ?? "").Trim().Replace('\\', '/').TrimEnd('/');
}
