using System.Text.Json.Nodes;
using StreamJsonRpc;
using GameZ.Script.Core;
using GameZ.Script.Core.Language;
using GameZ.Script.Core.Sources;

namespace GameZ.Script.LanguageServer;

/// <summary>Thin LSP transport. Every language decision belongs to the shared Core analysis.</summary>
public sealed partial class GameZLanguageServer : IDisposable
{
    private sealed record Buffer(string Uri, string Path, string Text, int Version);
    private readonly Lock gate = new();
    private readonly Dictionary<string, Buffer> buffers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (long Generation, LanguageProject Project, bool Missions)> projects = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim analysisGate = new(1);
    private readonly SemaphoreSlim missionGate = new(1);
    private readonly List<string> folders = [];
    private readonly Dictionary<string, string> dialects = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (long Revision, GameVariant? Game)> games = new(StringComparer.OrdinalIgnoreCase);
    private string defaultDialect = "auto";
    private long identityRevision;
    private readonly HashSet<string> published = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource pending = new();
    private bool trusted, stopped;
    private long generation;
    public JsonRpc? Rpc { get; set; }

    [JsonRpcMethod("initialize", UseSingleObjectParameterDeserialization = true)]
    public object Initialize(JsonObject args)
    {
        lock (gate)
        {
            trusted = args["initializationOptions"]?["trusted"]?.GetValue<bool>() == true;
            ReadDialects(args["initializationOptions"]);
            if (args["workspaceFolders"] is JsonArray ws) foreach (var folder in ws) AddFolder(folder?["uri"]?.GetValue<string>());
            else AddFolder(args["rootUri"]?.GetValue<string>());
        }
        return new
        {
            capabilities = new
            {
                positionEncoding = "utf-16", textDocumentSync = new { openClose = true, change = 1, save = new { includeText = false } },
                completionProvider = new { resolveProvider = false, triggerCharacters = new[] { "(", " ", ",", "%", "\\", "/" } },
                hoverProvider = true, definitionProvider = true, documentSymbolProvider = true,
                inlayHintProvider = new { resolveProvider = false },
                signatureHelpProvider = new { triggerCharacters = new[] { "(", " ", "," }, retriggerCharacters = new[] { " ", "," } },
                executeCommandProvider = new { commands = new[] { "gamez.server.validateProject" } },
                workspace = new { workspaceFolders = new { supported = true, changeNotifications = true } }
            },
            serverInfo = new { name = "GameZ Script", version = "1.4.0" }
        };
    }
    [JsonRpcMethod("initialized", UseSingleObjectParameterDeserialization = true)] public void Initialized(JsonObject _) { }
    [JsonRpcMethod("shutdown")] public object? Shutdown() { lock (gate) { stopped = true; pending.Cancel(); } return null; }
    [JsonRpcMethod("exit")] public void Exit() => Rpc?.Dispose();

    [JsonRpcMethod("textDocument/didOpen", UseSingleObjectParameterDeserialization = true)]
    public void Open(JsonObject args)
    {
        var doc = args["textDocument"]!;
        Put(doc["uri"]!.GetValue<string>(), doc["text"]!.GetValue<string>(), doc["version"]!.GetValue<int>());
    }
    [JsonRpcMethod("textDocument/didChange", UseSingleObjectParameterDeserialization = true)]
    public void Change(JsonObject args)
    {
        var doc = args["textDocument"]!;
        if (args["contentChanges"] is JsonArray { Count: > 0 } changes)
            Put(doc["uri"]!.GetValue<string>(), changes[^1]!["text"]!.GetValue<string>(), doc["version"]!.GetValue<int>());
    }
    [JsonRpcMethod("textDocument/didClose", UseSingleObjectParameterDeserialization = true)]
    public async Task Close(JsonObject args)
    {
        string uri = Canonical(args["textDocument"]!["uri"]!.GetValue<string>());
        lock (gate) { buffers.Remove(uri); Invalidate(); }
        if (Rpc != null) await Rpc.NotifyWithParameterObjectAsync("textDocument/publishDiagnostics", new { uri, diagnostics = Array.Empty<object>() });
    }
    [JsonRpcMethod("textDocument/didSave", UseSingleObjectParameterDeserialization = true)] public void Save(JsonObject _) { lock (gate) Invalidate(); }
    [JsonRpcMethod("workspace/didChangeWatchedFiles", UseSingleObjectParameterDeserialization = true)] public void FilesChanged(JsonObject _) { lock (gate) { identityRevision++; Invalidate(); } }
    [JsonRpcMethod("workspace/didChangeConfiguration", UseSingleObjectParameterDeserialization = true)]
    public void ConfigurationChanged(JsonObject args) { lock (gate) { ReadDialects(args["settings"]); identityRevision++; Invalidate(); } }
    private void ReadDialects(JsonNode? settings)
    {
        defaultDialect = settings?["gamez"]?["dialect"]?.GetValue<string>() ?? settings?["dialect"]?.GetValue<string>() ?? "auto";
        dialects.Clear();
        if (settings?["dialects"] is JsonArray rows)
            foreach (var row in rows.Take(128))
                if (Local(row?["uri"]?.GetValue<string>()) is { } path)
                    dialects[path] = row?["dialect"]?.GetValue<string>() ?? "auto";
    }
    [JsonRpcMethod("workspace/didChangeWorkspaceFolders", UseSingleObjectParameterDeserialization = true)]
    public void FoldersChanged(JsonObject args)
    {
        lock (gate)
        {
            if (args["event"]?["removed"] is JsonArray removed) foreach (var f in removed) if (Local(f?["uri"]?.GetValue<string>()) is { } path) folders.RemoveAll(p => p.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (args["event"]?["added"] is JsonArray added) foreach (var f in added) AddFolder(f?["uri"]?.GetValue<string>());
            identityRevision++;
            Invalidate();
        }
    }
    private void AddFolder(string? uri) { if (Local(uri) is { } path && !folders.Contains(path, StringComparer.OrdinalIgnoreCase)) folders.Add(path); }
    private void Put(string uri, string text, int version)
    {
        string? path = Local(uri);
        if (path == null || !LanguageProject.IsLanguageFile(path)) return;
        uri = new Uri(path).AbsoluteUri;
        lock (gate)
        {
            if (buffers.TryGetValue(uri, out var old) && old.Version >= version) return;
            buffers[uri] = new(uri, path, text, version); Invalidate();
        }
    }
    private void Invalidate()
    {
        // Closed-file diagnostics from a project check belong to its old snapshot too.
        foreach (string uri in published.Where(uri => !buffers.ContainsKey(uri)).ToArray())
        {
            _ = NotifyEmpty(uri); published.Remove(uri);
        }
        generation++; projects.Clear(); sourceGames.Clear(); pending.Cancel(); pending.Dispose(); pending = new();
        var cancellation = pending.Token; long revision = generation;
        _ = NotifyProfileChanged();
        _ = Background(revision, cancellation);
    }
    private async Task NotifyEmpty(string uri)
    {
        try { if (Rpc != null) await Rpc.NotifyWithParameterObjectAsync("textDocument/publishDiagnostics", new { uri, diagnostics = Array.Empty<object>() }); }
        catch (Exception ex) when (ex is ObjectDisposedException or IOException or OperationCanceledException) { }
    }
    private async Task Background(long revision, CancellationToken token)
    {
        try
        {
            await Task.Delay(300, token);
            Buffer[] opened; lock (gate) opened = buffers.Values.ToArray();
            foreach (var buffer in opened)
            {
                var local = await Task.Run(() => RecoilLanguage.AnalyzeResolved(buffer.Path, buffer.Text, Game(buffer.Path, token), token), token);
                await Publish(buffer, local, revision, token);
            }
            foreach (string root in opened.Select(b => Root(b.Path)).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (Game(Path.Combine(root, "gamegen", "profile.gs"), token) == null) continue;
                var project = await Project(root, false, revision, token, true);
                foreach (var buffer in opened.Where(b => Root(b.Path)?.Equals(root, StringComparison.OrdinalIgnoreCase) == true))
                    if (project.Documents.TryGetValue(SourceProject.Relative(root, buffer.Path), out var doc)) await Publish(buffer, doc, revision, token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            if (!token.IsCancellationRequested && Rpc != null)
            {
                await Rpc.NotifyWithParameterObjectAsync("window/logMessage", new { type = 2, message = "GameZ Script: project analysis incomplete: " + ex.Message });
                Buffer[] opened; lock (gate) opened = buffers.Values.ToArray();
                foreach (var buffer in opened)
                {
                    if (token.IsCancellationRequested) break;
                    var local = RecoilLanguage.AnalyzeResolved(buffer.Path, buffer.Text, Game(buffer.Path, token), token);
                    await PublishIncomplete(buffer, local, revision, ex.Message, token);
                }
            }
        }
        catch (ObjectDisposedException) { }
    }
    private async Task PublishIncomplete(Buffer buffer, LanguageDocument doc, long revision, string message, CancellationToken token)
    {
        Task? notification;
        lock (gate)
        {
            if (token.IsCancellationRequested || stopped || generation != revision || !buffers.TryGetValue(buffer.Uri, out var current) || current.Version != buffer.Version) return;
            published.Add(buffer.Uri);
            object issue = new { range = Range(buffer.Text, new(0, 0)), severity = 3, code = "GAMEZ_PROJECT_INCOMPLETE", source = "GameZ Script", message = "Only document-local checks are available: " + (message.Length > 1000 ? message[..1000] : message) };
            notification = Rpc?.NotifyWithParameterObjectAsync("textDocument/publishDiagnostics", new { uri = buffer.Uri, version = buffer.Version, diagnostics = Diagnostics(doc).Append(issue).ToArray() });
        }
        if (notification != null) await notification;
    }
    private async Task Publish(Buffer buffer, LanguageDocument doc, long revision, CancellationToken token)
    {
        Task? notification;
        lock (gate)
        {
            token.ThrowIfCancellationRequested();
            if (stopped || generation != revision || !buffers.TryGetValue(buffer.Uri, out var current) || current.Version != buffer.Version) return;
            published.Add(buffer.Uri);
            notification = Rpc?.NotifyWithParameterObjectAsync("textDocument/publishDiagnostics", new { uri = buffer.Uri, version = buffer.Version, diagnostics = Diagnostics(doc) });
        }
        if (notification != null) await notification;
    }
    private async Task<LanguageProject> Project(string root, bool deep, long revision, CancellationToken token, bool missions = false)
    {
        var selectedGate = missions || deep ? missionGate : analysisGate;
        await selectedGate.WaitAsync(token);
        try
        {
            Dictionary<string, string> overlay;
            lock (gate)
            {
                if (!deep && projects.TryGetValue(root, out var cached) && cached.Generation == revision && (cached.Missions || !missions)) return cached.Project;
                overlay = buffers.Values.Where(b => Root(b.Path)?.Equals(root, StringComparison.OrdinalIgnoreCase) == true).ToDictionary(b => SourceProject.Relative(root, b.Path), b => b.Text, StringComparer.OrdinalIgnoreCase);
            }
            var game = Game(Path.Combine(root, "gamegen", "profile.gs"), token)
                ?? throw new InvalidOperationException("Game is unknown. Use GameZ Script: Select Game to choose MW3 or RECOIL before validating this project.");
            var project = await Task.Run(() => LanguageProject.Analyze(root, overlay, deep, token, missionAnalysis: missions || deep,
                game: game), token);
            lock (gate) if (generation == revision && (!projects.TryGetValue(root, out var ready) || ready.Generation != revision || !ready.Missions || missions || deep)) projects[root] = (revision, project, missions || deep);
            return project;
        }
        finally { selectedGate.Release(); }
    }
    private string? Root(string path)
    {
        lock (gate)
        {
            if (!trusted) return null;
            string? boundary = folders.Where(f => Under(path, f)).MaxBy(f => f.Length);
            if (boundary == null) return null;
            for (string? folder = Path.GetDirectoryName(path); folder != null && Under(folder, boundary); folder = Path.GetDirectoryName(folder))
                if (SourceProject.IsProject(folder)) return folder;
            return null;
        }
    }
    private async Task<(LanguageDocument Document, LanguageProject? Project, int Offset)> Document(JsonObject args, CancellationToken token, bool contextualAnalysis = true, bool cacheOnly = false, bool fullContext = false)
    {
        string uri = Canonical(args["textDocument"]!["uri"]!.GetValue<string>());
        Buffer buffer; long revision; CancellationToken contextToken;
        lock (gate) { buffer = buffers.GetValueOrDefault(uri) ?? throw new InvalidOperationException("Document is not open."); revision = generation; contextToken = pending.Token; }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, contextToken);
        token = linked.Token;
        LanguageProject? project = null;
        string? root = contextualAnalysis ? Root(buffer.Path) : null;
        // A decisive active document can still work locally when the owning project's
        // identity scan is incomplete. Context requires a project-wide identity.
        if (root != null && Game(Path.Combine(root, "gamegen", "profile.gs"), token) == null) root = null;
        if (root != null)
        {
            try
            {
                // Script assistance stays responsive while contextual analysis runs in the background.
                if (cacheOnly && pathIsScript(buffer.Path)) { lock (gate) if (projects.TryGetValue(root, out var cached) && cached.Generation == revision) project = cached.Project; }
                else project = await Project(root, false, revision, token, fullContext);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { await Console.Error.WriteLineAsync(ex.Message); }
        }
        var doc = project != null && project.Documents.TryGetValue(SourceProject.Relative(root!, buffer.Path), out var contextual) ? contextual : RecoilLanguage.AnalyzeResolved(buffer.Path, buffer.Text, Game(buffer.Path, token), token);
        token.ThrowIfCancellationRequested();
        lock (gate) if (generation != revision) throw new OperationCanceledException("Source buffer changed during language request.");
        return (doc, project, Offset(buffer.Text, args["position"]));
    }
    private static bool pathIsScript(string path) => path.EndsWith(".gs", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".gw", StringComparison.OrdinalIgnoreCase);
    [JsonRpcMethod("textDocument/completion", UseSingleObjectParameterDeserialization = true)]
    public async Task<object> Completion(JsonObject args, CancellationToken token)
    {
        var (doc, project, offset) = await Document(args, token, cacheOnly: true);
        var items = project?.Complete(doc.Path, offset) ?? RecoilLanguage.Complete(doc, offset);
        bool Quoted(LanguageCompletion item) => item.ReplacementSpan is { Length: > 0 } span && doc.Text[span.Start] == '"';
        // Core already filters decoded prefixes. Keep the client's filter compatible with source quotes/escapes,
        // and re-query such lists as the user types instead of filtering a cached decoded spelling.
        string Filter(LanguageCompletion item) => Quoted(item) ? doc.Text[item.ReplacementSpan!.Value.Start..offset] + item.Label : item.Label;
        return new { isIncomplete = items.Count >= RecoilLanguage.MaximumResults || items.Any(i => i.Kind == "reference" || Quoted(i)), items = items.Select(i => new { label = i.Label, filterText = Filter(i), detail = i.Detail, documentation = i.Documentation == null ? null : new { kind = "markdown", value = i.Documentation }, textEdit = new { range = Range(doc.Text, i.ReplacementSpan ?? new(offset, 0)), newText = i.InsertText }, insertTextFormat = i.IsSnippet ? 2 : 1, kind = i.IsSnippet ? 15 : i.Kind == "reference" ? 18 : i.Kind == "enumMember" ? 20 : 14 }) };
    }
    [JsonRpcMethod("textDocument/inlayHint", UseSingleObjectParameterDeserialization = true)]
    public async Task<object> InlayHints(JsonObject args, CancellationToken token)
    {
        var (doc, _, _) = await Document(args, token, false);
        int start = Offset(doc.Text, args["range"]?["start"]), end = Offset(doc.Text, args["range"]?["end"]);
        var hints = RecoilLanguage.InlayHints(doc, new(start, Math.Max(0, end - start)), token);
        if (hints.Truncated) await Console.Error.WriteLineAsync("GameZ Script: inlay hint limit reached for requested range; request a smaller range.");
        // Build the line index once: thousands of hints must not rescan the entire buffer individually.
        var lines = new List<int> { 0 };
        for (int i = 0; i < doc.Text.Length; i++) if (doc.Text[i] == '\n') lines.Add(i + 1);
        var result = hints.Items.Select(h =>
        {
            token.ThrowIfCancellationRequested();
            int line = lines.BinarySearch(h.Span.Start); if (line < 0) line = ~line - 1;
            return new { position = new { line, character = h.Span.Start - lines[line] }, label = h.Label, kind = 2,
                paddingRight = true, tooltip = new { kind = "markdown", value = h.Documentation } };
        }).ToArray();
        CheckCurrent(doc, token);
        return result;
    }
    private void CheckCurrent(LanguageDocument doc, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (gate)
            if (stopped || !buffers.TryGetValue(new Uri(doc.Path).AbsoluteUri, out var current) || current.Text != doc.Text)
                throw new OperationCanceledException("Source buffer changed during language request.");
    }
    [JsonRpcMethod("textDocument/signatureHelp", UseSingleObjectParameterDeserialization = true)]
    public async Task<object?> SignatureHelp(JsonObject args, CancellationToken token)
    {
        var (doc, _, offset) = await Document(args, token, false);
        var signature = RecoilLanguage.SignatureHelp(doc, offset);
        CheckCurrent(doc, token);
        return signature == null ? null : new { activeSignature = 0, activeParameter = signature.ActiveParameter,
            signatures = new[] { new { label = signature.Label, documentation = new { kind = "markdown", value = signature.Documentation },
                parameters = signature.Parameters.Select(p => new { label = p.Label, documentation = new { kind = "markdown", value = p.Documentation } }).ToArray() } } };
    }
    [JsonRpcMethod("textDocument/hover", UseSingleObjectParameterDeserialization = true)]
    public async Task<object?> Hover(JsonObject args, CancellationToken token)
    {
        var (doc, _, offset) = await Document(args, token, false); var hover = RecoilLanguage.Hover(doc, offset);
        CheckCurrent(doc, token);
        return hover == null ? null : new { contents = new { kind = "markdown", value = hover.Markdown }, range = Range(doc.Text, hover.Span) };
    }
    [JsonRpcMethod("textDocument/documentSymbol", UseSingleObjectParameterDeserialization = true)]
    public async Task<object> Symbols(JsonObject args, CancellationToken token)
    {
        var (doc, _, _) = await Document(args, token, false);
        CheckCurrent(doc, token);
        object Convert(LanguageSymbol symbol) => new { name = symbol.Name, kind = symbol.Kind == "class" ? 5 : 7, range = Range(doc.Text, symbol.Span), selectionRange = Range(doc.Text, symbol.Selection), children = symbol.Children.Select(Convert).ToArray() };
        return doc.Symbols.Select(Convert).ToArray();
    }
    [JsonRpcMethod("textDocument/definition", UseSingleObjectParameterDeserialization = true)]
    public async Task<object> Definition(JsonObject args, CancellationToken token)
    {
        var (doc, project, offset) = await Document(args, token, fullContext: true);
        if (project == null) return Array.Empty<object>();
        return project.Definition(doc.Path, offset).DistinctBy(location => (location.Path.ToLowerInvariant(), location.Span)).Select(location => new
        {
            uri = new Uri(SourceProject.Resolve(project.Root, location.Path)).AbsoluteUri,
            range = project.TargetLineStarts(location.Path) is { } lines ? IndexedRange(lines, location.Span) : Range(project.TargetText(location.Path) ?? "", location.Span)
        }).ToArray();
    }
    [JsonRpcMethod("workspace/executeCommand", UseSingleObjectParameterDeserialization = true)]
    public async Task<object> Execute(JsonObject args, CancellationToken token)
    {
        if (args["command"]?.GetValue<string>() != "gamez.server.validateProject") throw new InvalidOperationException("Unknown command.");
        string? path = Local(args["arguments"]?[0]?.GetValue<string>());
        string root = path == null ? throw new InvalidOperationException("Choose an open source document.") : Root(path) ?? throw new InvalidOperationException("Open a trusted source-project workspace containing data/ and gamegen/.");
        long revision; CancellationToken contextToken; lock (gate) { revision = generation; contextToken = pending.Token; }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, contextToken); token = linked.Token;
        var project = await Project(root, true, revision, token);
        foreach (var doc in project.ValidationDocuments)
        {
            Task? notice;
            lock (gate)
            {
                token.ThrowIfCancellationRequested();
                if (generation != revision) throw new OperationCanceledException("Sources changed during validation.");
                if (Root(SourceProject.Resolve(root, doc.Path))?.Equals(root, StringComparison.OrdinalIgnoreCase) != true) continue;
                string uri = new Uri(SourceProject.Resolve(root, doc.Path)).AbsoluteUri;
                published.Add(uri);
                int? version = buffers.GetValueOrDefault(uri)?.Version;
                notice = Rpc?.NotifyWithParameterObjectAsync("textDocument/publishDiagnostics", new { uri, version, diagnostics = Diagnostics(doc) });
            }
            if (notice != null) await notice;
        }
        lock (gate)
        {
            token.ThrowIfCancellationRequested();
            if (generation != revision) throw new OperationCanceledException("Sources changed during validation.");
        }
        return new { files = project.CheckedFileCount, errors = project.ValidationDocuments.Sum(d => d.Diagnostics.Count(x => x.Severity == "error")), warnings = project.ValidationDocuments.Sum(d => d.Diagnostics.Count(x => x.Severity == "warning")) };
    }
    private static object IndexedRange(IReadOnlyList<int> lines, TextSpan span)
    {
        object At(int offset)
        {
            int lo = 0, hi = lines.Count - 1;
            while (lo < hi) { int mid = lo + (hi - lo + 1) / 2; if (lines[mid] <= offset) lo = mid; else hi = mid - 1; }
            return new { line = lo, character = Math.Max(0, offset - lines[lo]) };
        }
        return new { start = At(span.Start), end = At(span.End) };
    }
    private static object[] Diagnostics(LanguageDocument doc) => doc.Diagnostics.Select(d => (object)new
    {
        range = Range(doc.Text, d.Span), severity = d.Severity == "error" ? 1 : d.Severity == "warning" ? 2 : 3, code = d.Code, source = "GameZ Script",
        message = (d.Mission == null ? "" : d.Mission + ": ") + d.Message,
        relatedInformation = d.Related?.Select(r => new { location = new { uri = new Uri(r.Path).AbsoluteUri, range = Range(r.Text, r.Span) }, message = r.Message }).ToArray()
    }).ToArray();
    public static object Range(string text, TextSpan span) => new { start = Position(text, span.Start), end = Position(text, span.End) };
    private static object Position(string text, int offset)
    {
        offset = Math.Clamp(offset, 0, text.Length); int line = 0, start = 0;
        for (int i = 0; i < offset; i++) if (text[i] == '\n') { line++; start = i + 1; }
        return new { line, character = offset - start };
    }
    private static int Offset(string text, JsonNode? position)
    {
        int line = position?["line"]?.GetValue<int>() ?? 0, column = position?["character"]?.GetValue<int>() ?? 0, start = 0;
        for (int i = 0; i < Math.Max(0, line); i++) { int next = text.IndexOf('\n', start); if (next < 0) return text.Length; start = next + 1; }
        int end = text.IndexOf('\n', start); if (end < 0) end = text.Length;
        return Math.Clamp((long)start + column, start, end) is long offset ? (int)offset : start;
    }
    private static bool Under(string path, string root) => path.Equals(root, StringComparison.OrdinalIgnoreCase) || path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static string Canonical(string uri) => Local(uri) is { } path ? new Uri(path).AbsoluteUri : uri;
    private static string? Local(string? uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || !parsed.IsFile || parsed.IsUnc) return null;
        string path = parsed.LocalPath;
        // VS Code escapes the drive colon. Uri then retains the URI's leading slash.
        if (path.Length >= 4 && path[0] == '/' && char.IsAsciiLetter(path[1]) && path[2] == ':' && path[3] == '/') path = path[1..];
        return Path.GetFullPath(path);
    }
    public void Dispose() { lock (gate) { stopped = true; pending.Cancel(); } }
}
