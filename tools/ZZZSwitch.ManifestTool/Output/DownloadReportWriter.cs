using System.Text.Json;
using ZZZSwitch.ManifestTool.Download;

namespace ZZZSwitch.ManifestTool.Output;

public sealed class DownloadReportWriter
{
    private readonly JsonSerializerOptions _json;

    public DownloadReportWriter(JsonSerializerOptions json) => _json = json;

    public async Task<string> WriteAsync(
        SophonDownloadReport report,
        string outputRoot,
        CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(
            Path.GetFullPath(outputRoot),
            $"download-report-{report.Region}-{report.Version}.json");
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                await JsonSerializer.SerializeAsync(stream, report, _json, cancellationToken)
                    .ConfigureAwait(false);
            }

            File.Move(temporary, path, overwrite: true);
            return path;
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}
