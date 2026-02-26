namespace AzureFilesSync.Core.Models;

public sealed record HelpTopic(
    string Id,
    string Title,
    string RelativePath);

public sealed record HelpDocument(
    string TopicId,
    string Title,
    string Markdown,
    string Html,
    string SourcePath);
