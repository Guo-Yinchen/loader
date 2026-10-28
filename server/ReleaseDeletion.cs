sealed partial class Store
{
    public void DeleteRelease(string programId, string version)
    {
        if (!ProgramId.IsMatch(programId) || !VersionName.IsMatch(version))
            throw new ArgumentException("程序 ID 或版本号无效。");
        lock (gate)
        {
            var program = GetProgram(programId) ?? throw new ArgumentException("程序不存在。");
            var target = Path.GetFullPath(Path.Combine(root, "programs", programId, "releases", version));
            var allowed = Path.GetFullPath(Path.Combine(root, "programs")) + Path.DirectorySeparatorChar;
            if (!target.StartsWith(allowed, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("版本目录无效。");
            if (!Directory.Exists(target)) throw new ArgumentException("这个版本不存在。");
            // Refuse junctions/symlinks in ancestors and descendants before recursive deletion.
            for (var ancestor = new DirectoryInfo(target); ancestor is not null; ancestor = ancestor.Parent)
                if ((ancestor.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new ArgumentException("版本目录包含链接，不能删除。");
            if (program.RecommendedVersion == version || Latest(programId)?.Version == version)
                throw new ArgumentException("不能删除当前推荐版本，请先选择另一个可用版本并保存推荐设置。");
            var pending = new Stack<string>();
            pending.Push(target);
            while (pending.TryPop(out var directory))
                foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    var attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                        throw new ArgumentException("版本目录包含链接，不能删除。");
                    if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
                }
            // On Windows a download holds the ZIP open: fail before touching its siblings.
            var package = Path.Combine(target, "package.zip");
            if (File.Exists(package))
                using (new FileStream(package, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            Directory.Delete(target, recursive: true);
            launchManifests.Remove(programId + "/" + version);
        }
    }
}
