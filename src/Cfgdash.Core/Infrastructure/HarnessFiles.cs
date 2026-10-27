using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cfgdash.Core.Infrastructure;

internal static class HarnessFiles
{
    private const int MaxJsonBytes = 4 * 1024 * 1024;

    /// <summary>Strict JSON (no comments, no trailing commas), max 4 MiB. False with an error when missing, too large, unparseable or not an object.</summary>
    public static bool TryReadStrictJson(string path, out JsonObject? root, out string? error)
    {
        root = null;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                error = "file not found";
                return false;
            }

            if (info.Length > MaxJsonBytes)
            {
                error = "file larger than 4 MiB";
                return false;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var node = JsonNode.Parse(stream, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
            if (node is not JsonObject obj)
            {
                error = "root is not a JSON object";
                return false;
            }

            root = obj;
            error = null;
            return true;
        }
        catch (JsonException e)
        {
            error = "invalid JSON: " + FirstLine(e.Message);
            return false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            error = e.GetType().Name + ": " + FirstLine(e.Message);
            return false;
        }
    }

    public static string? ReadText(string path, int maxBytes = 2 * 1024 * 1024)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[(int)Math.Min(maxBytes, stream.Length)];
            var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            return new UTF8Encoding(false).GetString(buffer, 0, read).TrimStart('﻿');
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static long Tokens(long chars) => (chars + 3) / 4;

    private static string FirstLine(string text) => text.Split('\n')[0].Trim();
}
