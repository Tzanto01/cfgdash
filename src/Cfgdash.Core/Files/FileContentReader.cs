using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cfgdash.Core.Files;

/// <summary>Reads registered files for display: redacted, capped at 512 KiB, never-read files refused.</summary>
internal sealed class FileContentReader(IRedactor redactor) : IFileContentReader
{
    public const int MaxBytes = 512 * 1024;

    internal static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public async Task<FileContent> ReadAsync(RegisteredFile file, CancellationToken cancellationToken)
    {
        if (FileRegistry.IsNeverRead(file.FullPath))
        {
            throw new UnauthorizedAccessException("This file is never read by cfgdash.");
        }

        byte[] buffer;
        bool truncated;
        await using (var stream = new FileStream(file.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, useAsync: true))
        {
            buffer = new byte[MaxBytes + 1];
            var read = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, cancellationToken);
            truncated = read > MaxBytes;
            Array.Resize(ref buffer, Math.Min(read, MaxBytes));
        }

        var text = new UTF8Encoding(false).GetString(buffer).TrimStart('﻿');
        if (truncated)
        {
            text = text[..(text.LastIndexOf('\n') + 1)];
        }

        var extension = Path.GetExtension(file.FullPath).ToLowerInvariant();
        var contentType = extension switch
        {
            ".md" or ".markdown" => "markdown",
            ".json" => "json",
            _ => "text",
        };

        string content;
        if (contentType == "json" && !truncated && TryParse(text, out var node))
        {
            content = redactor.RedactJson(node)?.ToJsonString(Indented) ?? "null";
        }
        else
        {
            if (contentType == "json")
            {
                contentType = "text";
            }

            content = redactor.RedactText(text);
        }

        return new FileContent(file.Ref.Id, file.Ref.Path, file.Ref.Kind, contentType, content, truncated);
    }

    internal static bool TryParse(string text, out JsonNode? node)
    {
        try
        {
            node = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            return true;
        }
        catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException)
        {
            node = null;
            return false;
        }
    }
}
