using System.Text.Json.Nodes;
using CiDebugMcp.Engine;
using McpSharp;

namespace CiDebugMcp.Tools;

/// <summary>
/// Artifact download, extraction, and management tools.
/// Supports parallel downloads with async start/poll pattern.
/// </summary>
public static class ArtifactTools
{
    public static void Register(McpServer server, IGitHubApi github, DownloadManager downloadManager,
        CiProviderResolver? resolver = null)
    {
        server.RegisterTool(new ToolInfo
        {
            Name = "download_artifact",
            Description = "CI build artifact operations (GitHub Actions / Azure Pipelines). " +
                          "GitHub: full download/extract pipeline with background downloads. " +
                          "ADO: artifact listing only (provide url param with ADO build URL). " +
                          "Modes: list_only=true → list artifacts; run_id or artifact_id → start download; " +
                          "download_id → poll/extract existing; list_downloads=true → all active downloads. " +
                          "Returns: { download_id, status ('queued'|'downloading'|'completed'|'failed'), " +
                          "artifact: {id, name}, progress?: {bytes_mb, total_mb, percent, elapsed, eta}, " +
                          "size_mb?, contents_summary?: {total_files, uncompressed_size_mb}, " +
                          "sample_contents?: string[], hint?, error? } " +
                          "or for extract: { download_id, status: 'extracted', extracted: [{zip_path, local_path, size_mb}], count } " +
                          "or for list_only: { run_id, artifacts: [{id, name, size_mb, expired}], count, hint }",
            InputSchema = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["repo"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Repository as 'owner/repo' (not needed for ADO if url is provided)",
                    },
                    ["url"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "CI URL — auto-detects GitHub or ADO. For ADO, repo is not needed.",
                    },
                    ["run_id"] = new JsonObject
                    {
                        ["type"] = "number",
                        ["description"] = "Workflow run ID — list or download artifacts from this run",
                    },
                    ["pr"] = new JsonObject
                    {
                        ["type"] = "number",
                        ["description"] = "PR number — resolves to the latest workflow run on the PR's head commit",
                    },
                    ["artifact_id"] = new JsonObject
                    {
                        ["type"] = "number",
                        ["description"] = "Specific artifact ID to download (from list_only or get_ci_failures hint)",
                    },
                    ["name_filter"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Substring match on artifact name (e.g. 'Build-x64_Debug'). " +
                                          "Auto-starts download of first match when used with run_id.",
                    },
                    ["download_id"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Poll or extract from an existing download (e.g. 'dl-1')",
                    },
                    ["list_only"] = new JsonObject
                    {
                        ["type"] = "boolean",
                        ["description"] = "Just list artifacts without downloading (default: false)",
                        ["default"] = false,
                    },
                    ["list_downloads"] = new JsonObject
                    {
                        ["type"] = "boolean",
                        ["description"] = "Return status of all active/recent downloads (default: false)",
                        ["default"] = false,
                    },
                    ["timeout"] = new JsonObject
                    {
                        ["type"] = "number",
                        ["description"] = "Seconds to wait for download progress before returning " +
                                          "(default: 30, 0 = instant status). Downloads run in background " +
                                          "independently — they are NOT cancelled when this timeout expires. " +
                                          "Use download_id to poll or wait longer in subsequent calls.",
                        ["default"] = 30,
                    },
                    ["extract"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["items"] = new JsonObject { ["type"] = "string" },
                        ["description"] = "File paths or globs to extract after download completes " +
                                          "(e.g. ['ebpfapi.dll', '*.exe'])",
                    },
                },
                ["required"] = new JsonArray(),
            },
            Handler = args =>
            {
                var listDownloads = args["list_downloads"]?.GetValue<bool>() ?? false;
                if (listDownloads)
                {
                    return ListAllDownloads(downloadManager);
                }

                var downloadId = args["download_id"]?.GetValue<string>();
                if (downloadId != null)
                {
                    return HandleExistingDownload(downloadManager, downloadId, args);
                }

                // Check for ADO URL
                var url = args["url"]?.GetValue<string>();
                if (url != null && resolver != null)
                {
                    var resolved = resolver.ResolveFromUrl(url);
                    if (resolved?.Provider.ProviderName == "ado")
                    {
                        var buildId = resolved.ExtractedQuery?.BuildId
                            ?? args["run_id"]?.GetValue<long>().ToString();
                        var nameFilter2 = args["name_filter"]?.GetValue<string>();
                        var listOnly2 = args["list_only"]?.GetValue<bool>() ?? false;

                        if (buildId != null && listOnly2)
                        {
                            return ListAdoArtifactsAsync(resolved.Provider, buildId, nameFilter2)
                                .GetAwaiter().GetResult();
                        }

                        if (buildId == null)
                            throw new ArgumentException("ADO requires a buildId — provide url with ?buildId=N or run_id param");

                        return ListAdoArtifactsAsync(resolved.Provider, buildId, nameFilter2)
                            .GetAwaiter().GetResult();
                    }
                }

                // Parse repo (GitHub path)
                var repoStr = args["repo"]?.GetValue<string>();
                string? owner = null, repo = null;
                if (repoStr != null && repoStr.Contains('/'))
                {
                    var parts = repoStr.Split('/', 2);
                    owner = parts[0];
                    repo = parts[1];
                }

                if (owner == null || repo == null)
                    throw new ArgumentException("repo is required as 'owner/repo'");

                var runId = args["run_id"]?.GetValue<long>();
                var prNumber = args["pr"]?.GetValue<int>();
                var artifactId = args["artifact_id"]?.GetValue<long>();
                var nameFilter = args["name_filter"]?.GetValue<string>();
                var listOnly = args["list_only"]?.GetValue<bool>() ?? false;
                var timeout = args["timeout"]?.GetValue<int>() ?? 30;

                // Resolve PR → run_id
                if (!runId.HasValue && prNumber.HasValue)
                {
                    runId = ResolvePrToRunIdAsync(github, owner, repo, prNumber.Value)
                        .GetAwaiter().GetResult();
                }

                if (listOnly && runId.HasValue)
                {
                    return ListArtifactsAsync(github, owner, repo, runId.Value, nameFilter)
                        .GetAwaiter().GetResult();
                }

                if (artifactId.HasValue)
                {
                    return StartDownloadAsync(github, downloadManager, owner, repo,
                        artifactId.Value, null, timeout, args).GetAwaiter().GetResult();
                }

                if (runId.HasValue)
                {
                    return StartDownloadFromRunAsync(github, downloadManager, owner, repo,
                        runId.Value, nameFilter, timeout, args).GetAwaiter().GetResult();
                }

                throw new ArgumentException("Provide run_id, pr, artifact_id, or download_id");
            },
        });
    }

    /// <summary>
    /// Resolve a PR number to the latest workflow run ID on its head commit.
    /// </summary>
    private static async Task<long> ResolvePrToRunIdAsync(
        IGitHubApi github, string owner, string repo, int prNumber)
    {
        var prData = await github.GetJsonAsync($"/repos/{owner}/{repo}/pulls/{prNumber}");
        var headSha = prData?["head"]?["sha"]?.GetValue<string>()
            ?? throw new ArgumentException($"Could not get head SHA for PR #{prNumber}");

        // Find runs for this SHA
        var runsData = await github.GetJsonAsync(
            $"/repos/{owner}/{repo}/actions/runs?head_sha={headSha}&per_page=5");
        var runs = runsData?["workflow_runs"]?.AsArray();
        if (runs == null || runs.Count == 0)
            throw new ArgumentException($"No workflow runs found for PR #{prNumber} (SHA: {headSha[..8]})");

        return runs[0]!["id"]!.GetValue<long>();
    }

    private static JsonNode ListAllDownloads(DownloadManager dm)
    {
        var downloads = dm.GetAllDownloads();
        var arr = new JsonArray();
        foreach (var d in downloads)
        {
            var entry = new JsonObject
            {
                ["download_id"] = d.DownloadId,
                ["artifact"] = d.ArtifactName,
                ["status"] = d.Status,
            };
            if (d.Status == "downloading")
            {
                entry["percent"] = d.Percent;
                entry["eta"] = d.EtaSeconds > 0 ? $"~{d.EtaSeconds}s" : "unknown";
            }
            else if (d.Status == "completed")
            {
                entry["size_mb"] = d.TotalBytes / (1024 * 1024);
                entry["files"] = d.TotalContents;
            }
            else if (d.Error != null)
            {
                entry["error"] = d.Error;
            }
            arr.Add(entry);
        }

        return new JsonObject
        {
            ["downloads"] = arr,
            ["count"] = downloads.Count,
        };
    }

    private static JsonNode HandleExistingDownload(DownloadManager dm, string downloadId, JsonObject args)
    {
        var job = dm.GetDownload(downloadId)
            ?? throw new ArgumentException($"No download with id '{downloadId}'");

        var timeout = args["timeout"]?.GetValue<int>() ?? 30;
        var extractPatterns = args["extract"]?.AsArray()
            ?.Select(n => n!.GetValue<string>()).ToArray();

        // Wait for progress if requested
        if (timeout > 0 && !job.IsCompleted)
        {
            job.WaitForNews(timeout * 1000);
        }

        var status = job.GetStatus(includeContents: true, maxContents: 30);

        // Handle extraction
        if (extractPatterns != null && extractPatterns.Length > 0)
        {
            if (!status.IsCompleted)
            {
                return new JsonObject
                {
                    ["download_id"] = downloadId,
                    ["status"] = status.Status,
                    ["progress"] = BuildProgressObject(status),
                    ["note"] = "Download still in progress — extraction will be available after completion",
                };
            }

            if (status.Error != null)
            {
                return new JsonObject
                {
                    ["download_id"] = downloadId,
                    ["status"] = "failed",
                    ["error"] = status.Error,
                };
            }

            var extractDir = dm.GetExtractDir(downloadId);
            var extracted = job.Extract(extractPatterns, extractDir);
            return BuildExtractResponse(downloadId, extracted);
        }

        return BuildStatusResponse(status);
    }

    private static async Task<JsonNode> ListArtifactsAsync(
        IGitHubApi github, string owner, string repo, long runId, string? nameFilter)
    {
        var json = await github.GetJsonAsync(
            $"/repos/{owner}/{repo}/actions/runs/{runId}/artifacts?per_page=50");
        var artifacts = json?["artifacts"]?.AsArray() ?? [];

        var arr = new JsonArray();
        foreach (var a in artifacts)
        {
            var name = a?["name"]?.GetValue<string>() ?? "";
            if (nameFilter != null && !name.Contains(nameFilter, StringComparison.OrdinalIgnoreCase))
                continue;

            var id = a?["id"]?.GetValue<long>() ?? 0;
            var sizeMb = (a?["size_in_bytes"]?.GetValue<long>() ?? 0) / (1024 * 1024);
            var expired = a?["expired"]?.GetValue<bool>() ?? false;

            arr.Add(new JsonObject
            {
                ["id"] = id,
                ["name"] = name,
                ["size_mb"] = sizeMb,
                ["expired"] = expired,
            });
        }

        var result = new JsonObject
        {
            ["run_id"] = runId,
            ["artifacts"] = arr,
            ["count"] = arr.Count,
        };

        if (arr.Count > 0)
        {
            var firstId = arr[0]!["id"]!.GetValue<long>();
            result["hint"] = $"download_artifact(repo='{owner}/{repo}', artifact_id={firstId}) to start download";
        }

        return result;
    }

    private static async Task<JsonNode> StartDownloadFromRunAsync(
        IGitHubApi github, DownloadManager dm, string owner, string repo,
        long runId, string? nameFilter, int timeout, JsonObject args)
    {
        // List artifacts and find the match
        var json = await github.GetJsonAsync(
            $"/repos/{owner}/{repo}/actions/runs/{runId}/artifacts?per_page=50");
        var artifacts = json?["artifacts"]?.AsArray() ?? [];

        JsonNode? target = null;
        foreach (var a in artifacts)
        {
            var name = a?["name"]?.GetValue<string>() ?? "";
            if (nameFilter == null || name.Contains(nameFilter, StringComparison.OrdinalIgnoreCase))
            {
                target = a;
                break;
            }
        }

        if (target == null)
        {
            return new JsonObject
            {
                ["error"] = $"No artifact found" + (nameFilter != null ? $" matching '{nameFilter}'" : ""),
                ["available"] = new JsonArray(artifacts.Select(a =>
                    (JsonNode)JsonValue.Create(a?["name"]?.GetValue<string>() ?? "")!).ToArray()),
            };
        }

        var artifactId = target["id"]!.GetValue<long>();
        var artifactName = target["name"]!.GetValue<string>();

        return await StartDownloadAsync(github, dm, owner, repo, artifactId, artifactName, timeout, args);
    }

    private static async Task<JsonNode> StartDownloadAsync(
        IGitHubApi github, DownloadManager dm, string owner, string repo,
        long artifactId, string? artifactName, int timeout, JsonObject args)
    {
        // Resolve artifact name if not provided
        if (artifactName == null)
        {
            try
            {
                var info = await github.GetJsonAsync($"/repos/{owner}/{repo}/actions/artifacts/{artifactId}");
                artifactName = info?["name"]?.GetValue<string>() ?? $"artifact-{artifactId}";
            }
            catch
            {
                artifactName = $"artifact-{artifactId}";
            }
        }

        var job = dm.StartDownload(owner, repo, artifactId, artifactName);

        // Wait for initial progress
        if (timeout > 0)
        {
            job.WaitForNews(timeout * 1000);
        }

        var status = job.GetStatus(includeContents: true, maxContents: 30);

        // If completed and extract was requested, do it
        var extractPatterns = args["extract"]?.AsArray()
            ?.Select(n => n!.GetValue<string>()).ToArray();

        if (extractPatterns != null && extractPatterns.Length > 0 && status.IsCompleted && status.Error == null)
        {
            var extractDir = dm.GetExtractDir(job.DownloadId);
            var extracted = job.Extract(extractPatterns, extractDir);
            return BuildExtractResponse(job.DownloadId, extracted);
        }

        return BuildStatusResponse(status);
    }

    private static JsonObject BuildStatusResponse(DownloadStatus status)
    {
        var result = new JsonObject
        {
            ["download_id"] = status.DownloadId,
            ["status"] = status.Status,
            ["artifact"] = new JsonObject
            {
                ["id"] = status.ArtifactId,
                ["name"] = status.ArtifactName,
            },
        };

        if (status.Status == "downloading")
        {
            result["progress"] = BuildProgressObject(status);
            result["hint"] = $"download_artifact(download_id='{status.DownloadId}', timeout=60) to wait for completion";
        }
        else if (status.Status == "completed")
        {
            result["size_mb"] = status.TotalBytes / (1024 * 1024);
            result["contents_summary"] = new JsonObject
            {
                ["total_files"] = status.TotalContents,
                ["uncompressed_size_mb"] = status.UncompressedSizeMb,
            };
            if (status.Contents != null)
            {
                result["sample_contents"] = new JsonArray(
                    status.Contents.Select(c => (JsonNode)JsonValue.Create(c)!).ToArray());
                if (status.HasMoreContents)
                    result["contents_truncated"] = status.TotalContents;
            }
            result["hint"] = $"download_artifact(download_id='{status.DownloadId}', extract=['*.dll']) to extract files";
        }
        else if (status.Error != null)
        {
            result["error"] = status.Error;
        }

        return result;
    }

    private static JsonObject BuildProgressObject(DownloadStatus status)
    {
        return new JsonObject
        {
            ["bytes_mb"] = status.BytesDownloaded / (1024 * 1024),
            ["total_mb"] = status.TotalBytes / (1024 * 1024),
            ["percent"] = status.Percent,
            ["elapsed"] = $"{status.ElapsedSeconds}s",
            ["eta"] = status.EtaSeconds > 0 ? $"~{status.EtaSeconds}s" : "unknown",
        };
    }

    private static JsonObject BuildExtractResponse(string downloadId, List<ExtractedFile> extracted)
    {
        var arr = new JsonArray();
        foreach (var f in extracted)
        {
            arr.Add(new JsonObject
            {
                ["zip_path"] = f.ZipPath,
                ["local_path"] = f.LocalPath,
                ["size_mb"] = Math.Round((double)f.SizeBytes / (1024 * 1024), 1),
            });
        }

        return new JsonObject
        {
            ["download_id"] = downloadId,
            ["status"] = "extracted",
            ["extracted"] = arr,
            ["count"] = extracted.Count,
        };
    }

    private static async Task<JsonNode> ListAdoArtifactsAsync(
        ICiProvider provider, string buildId, string? nameFilter)
    {
        var artifacts = await provider.ListArtifactsAsync(buildId);

        var arr = new JsonArray();
        foreach (var a in artifacts)
        {
            if (nameFilter != null && !a.Name.Contains(nameFilter, StringComparison.OrdinalIgnoreCase))
                continue;
            var entry = new JsonObject
            {
                ["id"] = a.Id,
                ["name"] = a.Name,
            };
            if (a.SizeBytes > 0)
                entry["size_mb"] = a.SizeBytes / (1024 * 1024);
            if (a.Expired)
                entry["expired"] = true;
            arr.Add(entry);
        }

        var result = new JsonObject
        {
            ["provider"] = "ado",
            ["build_id"] = buildId,
            ["artifacts"] = arr,
            ["count"] = arr.Count,
        };

        if (arr.Count > 0 && artifacts.Any(a => a.SizeBytes == 0))
            result["note"] = "ADO does not report artifact sizes in list responses";

        return result;
    }
}
