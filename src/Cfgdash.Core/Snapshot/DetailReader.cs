using System.Text;
using Cfgdash.Core.Files;
using Cfgdash.Core.Infrastructure;

namespace Cfgdash.Core.Snapshot;

/// <summary>Resolves a <c>detailId</c> through its collector (docs/contracts/collector-v1.md runner rule 6). Throws <see cref="TimeoutException"/> when the provider exceeds the timeout.</summary>
internal sealed class DetailReader(
    MachineEnvironment environment,
    IReadOnlyList<ICollector> collectors,
    IRedactor redactor,
    IProcessRunner processes,
    TimeSpan? providerTimeout = null) : IDetailReader
{
    private readonly TimeSpan timeout = providerTimeout ?? TimeSpan.FromSeconds(20);

    public async Task<DetailResponse?> ReadAsync(string targetDir, string detailId, CancellationToken cancellationToken)
    {
        var colon = detailId.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0 || !SnapshotBuilder.IsValidDetailKey(detailId[(colon + 1)..]))
        {
            return null;
        }

        var collectorId = detailId[..colon];
        if (collectors.FirstOrDefault(c => c.Manifest.Id == collectorId) is not IDetailProvider provider)
        {
            return null;
        }

        var context = new CollectContext
        {
            Environment = environment,
            TargetDir = PathUtil.Full(targetDir),
            Files = new FileRegistry(environment.CompanyPrefixes),
            Redactor = redactor,
            Processes = processes,
        };

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        var key = detailId[(colon + 1)..];
        var work = Task.Run(() => provider.GetDetailAsync(key, context, cts.Token), cts.Token);
        DetailContent? detail;
        try
        {
            detail = await work.WaitAsync(timeout, cancellationToken);
        }
        catch (TimeoutException) when (!work.IsCompleted)
        {
            _ = work.ContinueWith(static t => t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            throw;
        }
        catch (OperationCanceledException e) when (!cancellationToken.IsCancellationRequested && cts.IsCancellationRequested)
        {
            throw new TimeoutException("detail provider timed out", e);
        }

        if (detail is null)
        {
            return null;
        }

        var contentType = detail.ContentType is "markdown" or "json" ? detail.ContentType : "text";
        string content;
        if (contentType == "json" && FileContentReader.TryParse(detail.Content, out var node))
        {
            content = redactor.RedactJson(node)?.ToJsonString(FileContentReader.Indented) ?? "null";
        }
        else
        {
            contentType = contentType == "json" ? "text" : contentType;
            content = redactor.RedactText(detail.Content);
        }

        var bytes = Encoding.UTF8.GetBytes(content);
        var truncated = bytes.Length > FileContentReader.MaxBytes;
        if (truncated)
        {
            content = new UTF8Encoding(false).GetString(bytes, 0, FileContentReader.MaxBytes);
            var newline = content.LastIndexOf('\n');
            content = newline >= 0 ? content[..(newline + 1)] : content.TrimEnd('�');
            contentType = contentType == "json" ? "text" : contentType;
        }

        return new DetailResponse(detailId, redactor.RedactText(detail.Title), contentType, content, truncated);
    }
}
