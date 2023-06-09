using System.Text.RegularExpressions;
using LanguageServer.HierarchicalAnalysis;
using Microsoft.Extensions.Logging;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace LanguageServer;

public class SemanticTokensHandler : SemanticTokensHandlerBase
{
    private readonly ILogger logger;
    private readonly ITokenPriorityAnalyzer tokenPriorityAnalyzer;

    public SemanticTokensHandler(
        ILogger<SemanticTokensHandler> logger,
        ITokenPriorityAnalyzer tokenPriorityAnalyzer)
    {
        this.logger = logger;
        this.tokenPriorityAnalyzer = tokenPriorityAnalyzer;
    }

    protected override SemanticTokensRegistrationOptions CreateRegistrationOptions(
        SemanticTokensCapability capability, 
        ClientCapabilities clientCapabilities)
    {
        return new SemanticTokensRegistrationOptions
        {
            DocumentSelector = DocumentSelector.ForPattern("**/*.dsh", "**/*.dsh.conf"),
            Legend = new SemanticTokensLegend
            {
                TokenTypes = new Container<SemanticTokenType>(
                    SemanticTokenType.Comment,
                    SemanticTokenType.Keyword,
                    SemanticTokenType.Function,
                    SemanticTokenType.Method,
                    SemanticTokenType.String,
                    SemanticTokenType.Variable
                ),
                TokenModifiers = new Container<SemanticTokenModifier>(
                    "sql-captured",
                    "concurrency-captured",
                    "concurrency-modified",
                    "ui-captured",
                    SemanticTokenModifier.Async,
                    SemanticTokenModifier.Declaration
                )
            },
            Full = true
        };
    }

    protected override Task<SemanticTokensDocument> GetSemanticTokensDocument(
        ITextDocumentIdentifierParams @params, 
        CancellationToken cancellationToken)
    {
        return Task.FromResult(new SemanticTokensDocument(RegistrationOptions.Legend));
    }

    protected override async Task Tokenize(
        SemanticTokensBuilder builder, 
        ITextDocumentIdentifierParams identifier, 
        CancellationToken cancellationToken)
    {
        var path = DocumentUri.GetFileSystemPath(identifier);
        if (path is null)
        {
            logger.LogWarning("Invalid document identifier: {identifier}", identifier);
            return;
        }

        if (path.EndsWith(".dsh.conf"))
        {
            logger.LogInformation("Configuration update");
            await ConfigurationUpdateHandler.HandleConfigurationUpdateAsync(this.tokenPriorityAnalyzer, path, logger);
            return;
        }

        var content = await File.ReadAllLinesAsync(path, cancellationToken)
            .ConfigureAwait(false);

        var variableRegistry = new List<string>();

        foreach (var (lineContent, lineNumber) in content.Select((l, i) => (l, i)))
        {
            var queue = new PriorityQueue<(int, int, string, string[]), int>();

            if (lineContent.StartsWith('#'))
            {
                queue.Enqueue((
                    0,
                    lineContent.Length,
                    SemanticTokenType.Comment,
                    Array.Empty<string>()
                ),
                0);
            }

            var foundKeywords = FindKeywords(lineContent, new [] {"var", "do", "fun"});
            foreach (var (keywordStart, keywordLen, _) in foundKeywords)
            {
                queue.Enqueue((
                    keywordStart,
                    keywordLen,
                    SemanticTokenType.Keyword,
                    Array.Empty<string>()),
                    keywordStart);
            }

            var stringMatches = Regex.Matches(lineContent, "\".*?\"", RegexOptions.Singleline);
            foreach (var match in stringMatches.Select(x => x))
            {
                queue.Enqueue((
                    match.Index,
                    match.Length,
                    SemanticTokenType.String,
                    Array.Empty<string>()),
                    match.Index);
            }
            
            var funcCallMatches = Regex.Matches(lineContent, "\\w+?\\(", RegexOptions.Singleline);
            foreach (var match in funcCallMatches.Select(x => x))
            {
                queue.Enqueue((
                    match.Index,
                    match.Length-1,
                    SemanticTokenType.Method,
                    Array.Empty<string>()), 
                    match.Index);
            }

            var variableDeclarationMatch = Regex.Match(lineContent, "var\\s([\\w\\d]+)");
            if (variableDeclarationMatch.Success)
            {
                var varaibleName = variableDeclarationMatch.Groups[1].Value;
                variableRegistry.Add(varaibleName);
            }

            var foundVariables = FindKeywords(lineContent, variableRegistry.ToArray());
            foreach (var (keywordStart, keywordLen, variable) in foundVariables)
            {
                string[] modifiers = variable switch 
                {
                    "shared_counter" => new[] { "concurrency-modified", "concurrency-captured", "ui-captured", "sql-captured" },
                    "increment" => new[] { "concurrency-captured" },
                    "counter_label" => new[] { "ui-captured" },
                    "counterId" => new[] { "sql-captured" },
                    "ui" => Array.Empty<string>(),
                    _ => Array.Empty<string>()
                };

                if (variableDeclarationMatch.Success && 
                    variableDeclarationMatch.Groups[1].Value == variable)
                {
                    modifiers = modifiers.Append((string)SemanticTokenModifier.Declaration).ToArray();
                }

                var finalModifier = tokenPriorityAnalyzer.SelectPreferredModifier(modifiers);

                queue.Enqueue((
                    keywordStart,
                    keywordLen,
                    SemanticTokenType.Variable,
                    new[] { finalModifier }),
                    keywordStart);
            }

            while (queue.TryDequeue(out var item, out var priority))
            {
                var (start, len, token, modifiers) = item;
                builder.Push(
                    lineNumber,
                    start,
                    len,
                    token,
                    modifiers);
            }
        }
    }

    protected List<(int, int, string)> FindKeywords(
        string line, 
        string[] keywords, 
        int startIndex = 0,
        List<(int, int, string)>? foundKeywords = null)
    {
        foundKeywords ??= new();

        int? keywordIndex = null;
        var keywordLength = 0;
        string? foundKeyword = null;

        foreach (var keyword in keywords)
        {
            var index = line.IndexOf(keyword, startIndex);
            if (index != -1 && (!keywordIndex.HasValue || index < keywordIndex))
            {
                keywordIndex = index;
                keywordLength = keyword.Length;
                foundKeyword = keyword;
            }
        }

        if (keywordIndex is not int foundIndex)
        {
            return foundKeywords;
        }
        
        foundKeywords.Add((foundIndex, keywordLength, foundKeyword!));
        return FindKeywords(line, keywords, foundIndex + keywordLength, foundKeywords);
    }
}