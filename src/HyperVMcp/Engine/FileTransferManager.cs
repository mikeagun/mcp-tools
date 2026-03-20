// Copyright (c) HyperV MCP contributors
// SPDX-License-Identifier: MIT

using System.Text;

namespace HyperVMcp.Engine;

/// <summary>
/// Manages file transfers to and from VMs via PowerShell sessions.
/// Transfers run asynchronously through the CommandRunner pipeline, returning a CommandJob
/// that agents can poll, cancel, and inspect using existing command tools.
/// </summary>
public sealed class FileTransferManager
{
    private readonly SessionManager _sessionManager;
    private readonly CommandRunner _commandRunner;

    /// <summary>
    /// Threshold in bytes above which files are compressed before transfer.
    /// </summary>
    public long CompressionThresholdBytes { get; set; } = 10 * 1024 * 1024; // 10 MB

    public FileTransferManager(SessionManager sessionManager, CommandRunner commandRunner)
    {
        _sessionManager = sessionManager;
        _commandRunner = commandRunner;
    }

    /// <summary>
    /// Copy files from the local host to a VM. Returns a CommandJob for async polling.
    /// The transfer runs in the elevated backend with progress output.
    /// </summary>
    public CommandJob CopyToVm(string sessionId, IReadOnlyList<string> sources, string destination,
        bool? compress = null, int timeoutSeconds = 30)
    {
        _sessionManager.GetSession(sessionId); // validate session exists

        var resolvedPaths = ResolvePaths(sources);
        if (resolvedPaths.Count == 0)
            throw new InvalidOperationException("No files matched the source pattern(s).");

        var shouldCompress = compress ?? ShouldCompress(resolvedPaths);
        var totalSize = CalculateTotalSize(resolvedPaths);
        var sizeMb = Math.Round(totalSize / (1024.0 * 1024.0), 1);

        string script;
        if (shouldCompress)
            script = BuildCompressedUploadScript(sessionId, resolvedPaths, destination, totalSize);
        else
            script = BuildDirectUploadScript(sessionId, resolvedPaths, destination, totalSize);

        var description = $"copy_to_vm: {resolvedPaths.Count} file(s) ({sizeMb} MB) -> {destination}";
        return _commandRunner.ExecuteTransferWithTimeout(script, description, sessionId, timeoutSeconds);
    }

    /// <summary>
    /// Copy files from a VM to the local host. Returns a CommandJob for async polling.
    /// </summary>
    public CommandJob CopyFromVm(string sessionId, IReadOnlyList<string> sources, string destination,
        int timeoutSeconds = 30)
    {
        _sessionManager.GetSession(sessionId); // validate session exists

        var script = BuildDownloadScript(sessionId, sources, destination);
        var description = $"copy_from_vm: {sources.Count} path(s) -> {destination}";
        return _commandRunner.ExecuteTransferWithTimeout(script, description, sessionId, timeoutSeconds);
    }

    /// <summary>
    /// List files on the VM at a given path. Returns compact entries with summary stats.
    /// </summary>
    public VmFileListResult ListVmFiles(string sessionId, string path, string? pattern = null,
        bool recurse = false, int? depth = null, int maxResults = 200)
    {
        _sessionManager.GetSession(sessionId);

        var filter = string.IsNullOrEmpty(pattern) ? "" : $" -Filter '{PsUtils.PsEscape(pattern)}'";
        var isRecursive = depth.HasValue || recurse;

        // depth implies recurse; -Depth requires -Recurse in PS
        string depthFlag;
        if (depth.HasValue)
            depthFlag = $" -Recurse -Depth {depth.Value}";
        else if (recurse)
            depthFlag = " -Recurse";
        else
            depthFlag = "";

        var escapedPath = PsUtils.PsEscape(path);

        // When recursing, emit relative path from listing root instead of just Name.
        // This disambiguates files in different subdirectories.
        var nameExpr = isRecursive
            ? $"$rel = $_.FullName.Substring('{escapedPath}'.TrimEnd('\\').Length + 1); $rel"
            : "$($_.Name)";

        // Single PS script: enumerate all items, compute summary, return truncated list.
        var script = $@"
            $basePath = '{escapedPath}'
            if (-not (Test-Path $basePath)) {{ throw ""Path not found: $basePath"" }}
            $items = @(Get-ChildItem -Path $basePath{filter}{depthFlag} -ErrorAction Stop)
            $files = @($items | Where-Object {{ -not $_.PSIsContainer }})
            $dirs = @($items | Where-Object {{ $_.PSIsContainer }})
            $totalBytes = ($files | Measure-Object -Property Length -Sum).Sum
            if (-not $totalBytes) {{ $totalBytes = 0 }}
            ""SUMMARY`t$($files.Count)`t$($dirs.Count)`t$totalBytes""
            $items | Select-Object -First {maxResults} | ForEach-Object {{
                $n = {nameExpr}
                ""$n`t$($_.Length)`t$($_.PSIsContainer)""
            }}
        ";

        var (output, errors) = _sessionManager.ExecuteOnVm(sessionId, script);

        // Check for errors (e.g., path not found, broken session).
        var errorText = string.Join("; ", errors.Where(e => !string.IsNullOrWhiteSpace(e)));
        if (!string.IsNullOrEmpty(errorText))
            throw new InvalidOperationException(errorText);

        var result = new VmFileListResult { Path = path };
        var hasSummary = false;

        foreach (var line in output.Where(l => !string.IsNullOrWhiteSpace(l)))
        {
            var parts = line.Split('\t');
            if (parts[0] == "SUMMARY" && parts.Length >= 4)
            {
                hasSummary = true;
                result.TotalFiles = int.TryParse(parts[1], out var fc) ? fc : 0;
                result.TotalDirectories = int.TryParse(parts[2], out var dc) ? dc : 0;
                result.TotalSizeBytes = long.TryParse(parts[3], out var tb) ? tb : 0;
            }
            else if (parts.Length >= 3)
            {
                result.Entries.Add(new VmFileEntry
                {
                    Name = parts[0],
                    Size = long.TryParse(parts[1], out var sz) ? sz : 0,
                    Dir = parts[2] == "True",
                });
            }
        }

        // If no SUMMARY line was parsed, the script didn't execute properly.
        if (!hasSummary && output.Count == 0)
            throw new InvalidOperationException("Failed to list files — session may be broken. Try reconnect_vm.");

        result.TotalCount = result.TotalFiles + result.TotalDirectories;
        result.Truncated = result.TotalCount > maxResults;
        return result;
    }

    #region Script builders

    private string BuildCompressedUploadScript(string sessionId, List<string> resolvedPaths,
        string destination, long totalSize)
    {
        var sessionRef = PsUtils.PsEscape(sessionId);
        var destPath = NormalizeDirPath(destination);
        var escapedDest = PsUtils.PsEscape(destPath);
        var tempArchiveName = $"hyperv-mcp-{Guid.NewGuid():N}.tar";
        var remoteArchiveName = $"_hyperv_mcp_{Guid.NewGuid():N}.tar";
        var escapedArchiveOnVm = PsUtils.PsEscape(Path.Combine(destPath, remoteArchiveName));
        var fileCount = resolvedPaths.Count;
        var sizeMb = Math.Round(totalSize / (1024.0 * 1024.0), 1);

        var sb = new StringBuilder();

        // Phase 1: Create tar archive locally using Windows tar.exe (no size limits, fast).
        sb.AppendLine($"Write-Output '[1/4] Archiving {fileCount} path(s) ({sizeMb} MB)...'");
        sb.AppendLine($"$archivePath = Join-Path ([IO.Path]::GetTempPath()) '{tempArchiveName}'");

        if (resolvedPaths.Count == 1 && Directory.Exists(resolvedPaths[0]))
        {
            // Single directory: tar the directory itself (preserving its name) from the parent.
            // This gives cp -r semantics: copy_to_vm("...\Release", "C:\") → C:\Release\*
            var dirInfo = new DirectoryInfo(resolvedPaths[0]);
            var parentDir = PsUtils.PsEscape(dirInfo.Parent?.FullName ?? dirInfo.FullName);
            var dirName = PsUtils.PsEscape(dirInfo.Name);
            sb.AppendLine($"tar -cf $archivePath -C '{parentDir}' '{dirName}'");
        }
        else
        {
            // Multiple files/dirs: tar from parent directory.
            var firstPath = resolvedPaths[0];
            var parentDir = PsUtils.PsEscape(Path.GetDirectoryName(firstPath) ?? ".");
            var fileNames = string.Join("' '",
                resolvedPaths.Select(p => PsUtils.PsEscape(Path.GetFileName(p))));
            sb.AppendLine($"tar -cf $archivePath -C '{parentDir}' '{fileNames}'");
        }

        sb.AppendLine("if ($LASTEXITCODE -ne 0) { throw 'tar archiving failed (exit code ' + $LASTEXITCODE + ')' }");
        sb.AppendLine("$archiveMb = [math]::Round((Get-Item $archivePath).Length / 1MB, 1)");
        sb.AppendLine("Write-Output \"[1/4] Archived to $archiveMb MB\"");

        // Phase 2: Prepare destination directory on VM.
        sb.AppendLine("Write-Output '[2/4] Preparing destination on VM...'");
        sb.AppendLine($"Invoke-Command -Session $global:__sessions['{sessionRef}'] -ScriptBlock {{");
        sb.AppendLine($"    New-Item -ItemType Directory -Path '{escapedDest}' -Force -ErrorAction SilentlyContinue | Out-Null");
        sb.AppendLine("}");

        // Phase 3: Transfer archive to VM.
        sb.AppendLine("Write-Output \"[3/4] Transferring $archiveMb MB to VM...\"");
        sb.AppendLine($"Copy-Item -ToSession $global:__sessions['{sessionRef}'] -Path $archivePath -Destination '{escapedArchiveOnVm}' -Force -ErrorAction Stop");
        sb.AppendLine("Write-Output '[3/4] Transfer complete'");

        // Phase 4: Extract on VM using tar.
        sb.AppendLine("Write-Output '[4/4] Extracting on VM...'");
        sb.AppendLine($"Invoke-Command -Session $global:__sessions['{sessionRef}'] -ErrorAction Stop -ScriptBlock {{");
        sb.AppendLine($"    tar -xf '{escapedArchiveOnVm}' -C '{escapedDest}'");
        sb.AppendLine($"    if ($LASTEXITCODE -ne 0) {{ throw 'tar extraction failed' }}");
        sb.AppendLine($"    Remove-Item '{escapedArchiveOnVm}' -Force -ErrorAction SilentlyContinue");
        sb.AppendLine("}");
        sb.AppendLine("Write-Output '[4/4] Extraction complete'");

        // Cleanup local archive.
        sb.AppendLine("Remove-Item $archivePath -Force -ErrorAction SilentlyContinue");
        sb.AppendLine($"Write-Output 'TRANSFER_COMPLETE: files={fileCount} compressed=True'");

        return sb.ToString();
    }

    private string BuildDirectUploadScript(string sessionId, List<string> resolvedPaths,
        string destination, long totalSize)
    {
        var sessionRef = PsUtils.PsEscape(sessionId);
        var fileCount = resolvedPaths.Count;
        var sizeMb = Math.Round(totalSize / (1024.0 * 1024.0), 1);

        // Determine if destination is a directory or a file path.
        var destIsDir = resolvedPaths.Count > 1
            || destination.EndsWith('\\') || destination.EndsWith('/')
            || string.IsNullOrEmpty(Path.GetExtension(destination))
            || resolvedPaths.Any(Directory.Exists);

        string createDirPath, escapedDest;
        if (destIsDir)
        {
            var dirPath = NormalizeDirPath(destination);
            createDirPath = PsUtils.PsEscape(dirPath);
            // Trailing backslash tells Copy-Item this is a directory target.
            escapedDest = PsUtils.PsEscape(dirPath.TrimEnd('\\') + "\\");
        }
        else
        {
            createDirPath = PsUtils.PsEscape(Path.GetDirectoryName(destination) ?? destination);
            escapedDest = PsUtils.PsEscape(destination);
        }

        var sb = new StringBuilder();

        // Create destination directory on VM.
        sb.AppendLine($"Write-Output 'Preparing destination ({sizeMb} MB to transfer)...'");
        sb.AppendLine($"Invoke-Command -Session $global:__sessions['{sessionRef}'] -ScriptBlock {{");
        sb.AppendLine($"    New-Item -ItemType Directory -Path '{createDirPath}' -Force -ErrorAction SilentlyContinue | Out-Null");
        sb.AppendLine("}");

        // Copy files with per-file progress for multiple files.
        if (resolvedPaths.Count == 1)
        {
            var srcPath = PsUtils.PsEscape(resolvedPaths[0]);
            var fileName = Path.GetFileName(resolvedPaths[0]);
            sb.AppendLine($"Write-Output 'Copying {fileName} ({sizeMb} MB)...'");
            sb.AppendLine($"Copy-Item -ToSession $global:__sessions['{sessionRef}'] -Path '{srcPath}' -Destination '{escapedDest}' -Force -Recurse -ErrorAction Stop");
        }
        else
        {
            for (var i = 0; i < resolvedPaths.Count; i++)
            {
                var srcPath = PsUtils.PsEscape(resolvedPaths[i]);
                var fileName = Path.GetFileName(resolvedPaths[i]);
                sb.AppendLine($"Write-Output '[{i + 1}/{fileCount}] Copying {fileName}...'");
                sb.AppendLine($"Copy-Item -ToSession $global:__sessions['{sessionRef}'] -Path '{srcPath}' -Destination '{escapedDest}' -Force -Recurse -ErrorAction Stop");
            }
        }

        // Verify the session is still healthy and files arrived.
        sb.AppendLine($"Invoke-Command -Session $global:__sessions['{sessionRef}'] -ErrorAction Stop -ScriptBlock {{");
        sb.AppendLine($"    if (-not (Test-Path '{escapedDest}')) {{ throw 'Transfer verification failed: destination not found on VM' }}");
        sb.AppendLine("}");
        sb.AppendLine($"Write-Output 'TRANSFER_COMPLETE: files={fileCount} compressed=False'");
        return sb.ToString();
    }

    private string BuildDownloadScript(string sessionId, IReadOnlyList<string> sources, string destination)
    {
        var sessionRef = PsUtils.PsEscape(sessionId);
        var escapedDest = PsUtils.PsEscape(destination);
        var pathList = string.Join("', '", sources.Select(PsUtils.PsEscape));

        var sb = new StringBuilder();
        sb.AppendLine($"New-Item -ItemType Directory -Path '{escapedDest}' -Force -ErrorAction SilentlyContinue | Out-Null");
        sb.AppendLine($"Write-Output 'Copying {sources.Count} path(s) from VM...'");
        sb.AppendLine($"Copy-Item -FromSession $global:__sessions['{sessionRef}'] -Path @('{pathList}') -Destination '{escapedDest}' -Force -Recurse -ErrorAction Stop");
        sb.AppendLine($"$count = (Get-ChildItem '{escapedDest}' -Recurse -File).Count");
        sb.AppendLine("Write-Output \"TRANSFER_COMPLETE: files=$count\"");
        return sb.ToString();
    }

    #endregion

    #region Path resolution and analysis

    /// <summary>
    /// Normalize a directory path for safe use in PowerShell and tar commands.
    /// Ensures root paths like "C:\" don't get mangled by TrimEnd operations.
    /// Uses Path.GetFullPath to normalize separators and resolve relative segments.
    /// </summary>
    private static string NormalizeDirPath(string path)
    {
        // Path.GetFullPath handles: trailing backslashes, forward slashes,
        // double backslashes, relative segments (..\), and normalizes to canonical form.
        // For "C:\" it returns "C:\", for "C:\foo\" it returns "C:\foo\",
        // for "C:\foo" it returns "C:\foo".
        return Path.GetFullPath(path);
    }

    private static List<string> ResolvePaths(IReadOnlyList<string> sources)
    {
        var resolved = new List<string>();
        foreach (var source in sources)
        {
            if (source.Contains('*') || source.Contains('?'))
            {
                var dir = Path.GetDirectoryName(source) ?? ".";
                var pattern = Path.GetFileName(source);
                resolved.AddRange(Directory.GetFiles(dir, pattern));
            }
            else
            {
                resolved.Add(source);
            }
        }
        return resolved;
    }

    private bool ShouldCompress(List<string> paths)
    {
        var totalSize = CalculateTotalSize(paths);
        return totalSize > CompressionThresholdBytes || paths.Any(Directory.Exists);
    }

    private static long CalculateTotalSize(List<string> paths)
    {
        var total = 0L;
        foreach (var path in paths)
        {
            if (File.Exists(path))
                total += new FileInfo(path).Length;
            else if (Directory.Exists(path))
                total += GetDirectorySize(path);
        }
        return total;
    }

    private static long GetDirectorySize(string path)
    {
        if (!Directory.Exists(path)) return 0;
        return Directory.GetFiles(path, "*", SearchOption.AllDirectories)
            .Sum(f => new FileInfo(f).Length);
    }

    #endregion
}
