using AzureFilesSync.Core.Contracts;
using AzureFilesSync.Core.Models;
using Markdig;
using System.Text.RegularExpressions;

namespace AzureFilesSync.Infrastructure.Docs;

public sealed class FileSystemUserHelpContentService : IUserHelpContentService
{
    private static readonly IReadOnlyList<HelpTopic> Topics =
    [
        new("overview", "Overview", "README.md"),
        new("getting-started", "Getting Started", "getting-started.md"),
        new("ui-tour", "UI Tour", "ui-tour.md"),
        new("transfers", "Transfers", "transfers.md"),
        new("queue-management", "Queue Management", "queue-management.md"),
        new("buttons-and-actions", "Buttons and Actions", "buttons-and-actions.md"),
        new("troubleshooting", "Troubleshooting", "troubleshooting.md")
    ];

    private readonly MarkdownPipeline _pipeline;
    private readonly string _docsRoot;

    public FileSystemUserHelpContentService(string? docsRoot = null)
    {
        _pipeline = new MarkdownPipelineBuilder()
            .UseAdvancedExtensions()
            .Build();
        _docsRoot = ResolveDocsRoot(docsRoot);
    }

    public IReadOnlyList<HelpTopic> GetTopics() => Topics;

    public async Task<HelpDocument> LoadTopicAsync(string topicId, CancellationToken cancellationToken)
    {
        var topic = Topics.FirstOrDefault(x => string.Equals(x.Id, topicId, StringComparison.OrdinalIgnoreCase));
        if (topic is null)
        {
            throw new InvalidOperationException($"Unknown help topic '{topicId}'.");
        }

        var path = Path.Combine(_docsRoot, topic.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"Help document not found: {topic.RelativePath}");
        }

        var markdown = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        markdown = RewriteInternalMarkdownLinks(markdown);
        var htmlBody = Markdown.ToHtml(markdown, _pipeline);
        var html = WrapHtml(topic.Title, htmlBody, Path.GetDirectoryName(path) ?? _docsRoot);
        return new HelpDocument(topic.Id, topic.Title, markdown, html, path);
    }

    private static string ResolveDocsRoot(string? preferredRoot)
    {
        if (!string.IsNullOrWhiteSpace(preferredRoot))
        {
            return preferredRoot;
        }

        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "docs", "user"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "docs", "user"))
        };

        foreach (var candidate in candidates)
        {
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return candidates[0];
    }

    private static string WrapHtml(string title, string bodyHtml, string sourceDirectory)
    {
        var baseUri = new Uri(AppendTrailingSeparator(sourceDirectory)).AbsoluteUri;
        return $$"""
<!DOCTYPE html>
<html>
  <head>
    <meta charset="utf-8" />
    <base href="{{baseUri}}" />
    <title>{{title}}</title>
    <style>
      body { font-family: "Segoe UI", Arial, sans-serif; margin: 16px; color: #1f2937; line-height: 1.4; }
      h1, h2, h3 { color: #0f2f5f; }
      code { background: #f3f4f6; padding: 2px 4px; border-radius: 3px; }
      pre code { display: block; padding: 10px; overflow: auto; }
      table { border-collapse: collapse; width: 100%; }
      th, td { border: 1px solid #d1d5db; padding: 6px 8px; text-align: left; }
      blockquote { margin: 0; padding: 8px 12px; border-left: 4px solid #93c5fd; background: #eff6ff; }
      a { color: #2563eb; text-decoration: none; }
      a:hover { text-decoration: underline; }
    </style>
  </head>
  <body>
    {{bodyHtml}}
  </body>
</html>
""";
    }

    private static string AppendTrailingSeparator(string path) =>
        path.EndsWith(Path.DirectorySeparatorChar) || path.EndsWith(Path.AltDirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;

    private static string RewriteInternalMarkdownLinks(string markdown)
    {
        var linksByFile = Topics.ToDictionary(
            x => Path.GetFileName(x.RelativePath),
            x => x.Id,
            StringComparer.OrdinalIgnoreCase);

        return Regex.Replace(
            markdown,
            @"(?<!\!)\[([^\]]+)\]\(([^)]+)\)",
            match =>
            {
                var label = match.Groups[1].Value;
                var target = match.Groups[2].Value.Trim();
                if (target.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    target.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                    target.StartsWith("#", StringComparison.OrdinalIgnoreCase))
                {
                    return match.Value;
                }

                var anchor = string.Empty;
                var hashIndex = target.IndexOf('#');
                if (hashIndex >= 0)
                {
                    anchor = target[hashIndex..];
                    target = target[..hashIndex];
                }

                if (!target.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                {
                    return match.Value;
                }

                var fileName = Path.GetFileName(target);
                if (!linksByFile.TryGetValue(fileName, out var topicId))
                {
                    return match.Value;
                }

                return $"[{label}](help://topic/{topicId}{anchor})";
            },
            RegexOptions.Compiled);
    }
}
