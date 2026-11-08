import * as vscode from 'vscode';
import * as path from 'node:path';
import { LanguageClient, LanguageClientOptions, ServerOptions, State } from 'vscode-languageclient/node';

interface GameProfile {
    game: 'mw3' | 'recoil' | null;
    source: 'configuration' | 'workspace' | 'document' | 'project-source' | 'unknown';
    rootUri: string | null;
    reason: string | null;
    restricted: boolean;
    generation: number;
}
export interface GameZExtensionApi {
    getGameStatus(): { visible: boolean; text: string; tooltip: string; profile: Readonly<GameProfile> | undefined };
}

let client: LanguageClient | undefined;
let watcher: vscode.FileSystemWatcher | undefined;
let lifecycle: Promise<void> = Promise.resolve();
let deactivating = false;
let retireClient: () => Promise<void> = async () => {};
export async function activate(context: vscode.ExtensionContext): Promise<GameZExtensionApi> {
    deactivating = false;
    const output = vscode.window.createOutputChannel('GameZ Script', { log: true });
    context.subscriptions.push(output);
    const supported = (document: vscode.TextDocument | undefined): document is vscode.TextDocument =>
        document?.uri.scheme === 'file' && ['gamez-script', 'gamez-gamegen', 'gamez-animation'].includes(document.languageId);
    const status = vscode.window.createStatusBarItem('gamez.gameProfile', vscode.StatusBarAlignment.Right, 110);
    status.name = 'GameZ Script game';
    status.command = 'gamez.selectGame';
    let visible = false;
    let profile: GameProfile | undefined;
    let profileSequence = 0;
    let profileCancellation: vscode.CancellationTokenSource | undefined;
    let refreshTimer: ReturnType<typeof setTimeout> | undefined;
    let starting = false;
    let latestGeneration = -1;
    let clientSubscriptions: vscode.Disposable[] = [];
    const invalidateStatusRequest = (): number => {
        if (refreshTimer) clearTimeout(refreshTimer);
        refreshTimer = undefined;
        profileCancellation?.cancel(); profileCancellation?.dispose(); profileCancellation = undefined;
        profile = undefined;
        return ++profileSequence;
    };
    const showPendingStatus = (): void => {
        if (deactivating) return;
        visible = supported(vscode.window.activeTextEditor?.document);
        if (!visible) { status.hide(); return; }
        status.text = starting || client?.isRunning() ? 'GameZ: Detecting…' : 'GameZ: Unavailable';
        status.tooltip = starting || client?.isRunning()
            ? 'Determining the game rules for this file.'
            : 'GameZ Script language server is unavailable. Run GameZ Script: Restart Language Server.';
        status.show();
    };
    const refreshStatus = async (): Promise<void> => {
        const sequence = invalidateStatusRequest();
        showPendingStatus();
        const document = vscode.window.activeTextEditor?.document;
        const selectedClient = client;
        if (deactivating || !supported(document) || !selectedClient?.isRunning()) return;
        const uri = document.uri.toString(), version = document.version;
        const cancellation = new vscode.CancellationTokenSource();
        profileCancellation = cancellation;
        try {
            const result = await selectedClient.sendRequest<GameProfile>('gamez/getGameProfile', { textDocument: { uri } }, cancellation.token);
            const active = vscode.window.activeTextEditor?.document;
            if (deactivating || cancellation.token.isCancellationRequested || sequence !== profileSequence || client !== selectedClient ||
                active !== document || active.version !== version || result.generation < latestGeneration) return;
            latestGeneration = result.generation;
            profile = result;
            const name = result.game === 'mw3' ? 'MW3' : result.game === 'recoil' ? 'RECOIL' : 'Unknown';
            status.text = `GameZ: ${name}`;
            const source = { configuration: 'Explicit gamez.dialect override', workspace: 'Auto: source-model metadata',
                document: 'Auto: parsed document markers', 'project-source': 'Auto: source markers in the owning project', unknown: 'Auto: game not determined' }[result.source];
            let root = '';
            if (result.rootUri) { try { root = vscode.Uri.parse(result.rootUri).fsPath; } catch { root = result.rootUri; } }
            status.tooltip = [`GameZ Script — ${name}`, source, root ? `Project: ${root}` : '', result.reason ?? '',
                result.restricted ? 'Restricted Mode: document-local assistance only.' : '', 'Click to select Auto, MW3 or RECOIL.'].filter(Boolean).join('\n');
        } catch (error) {
            if (!deactivating && sequence === profileSequence && client === selectedClient && !cancellation.token.isCancellationRequested) {
                status.text = 'GameZ: Unavailable';
                status.tooltip = 'Could not determine the game rules. Run GameZ Script: Restart Language Server to retry.';
                output.warn(`Game profile request failed: ${String(error)}`);
            }
        } finally {
            if (profileCancellation === cancellation) profileCancellation = undefined;
            cancellation.dispose();
        }
    };
    const scheduleStatus = (delay = 120): void => {
        invalidateStatusRequest();
        showPendingStatus();
        if (!deactivating) refreshTimer = setTimeout(() => { refreshTimer = undefined; void refreshStatus(); }, delay);
    };
    context.subscriptions.push(status, { dispose: () => { invalidateStatusRequest(); clientSubscriptions.forEach(subscription => subscription.dispose()); } },
        vscode.window.onDidChangeActiveTextEditor(() => scheduleStatus(0)),
        vscode.workspace.onDidChangeTextDocument(event => { if (supported(event.document)) scheduleStatus(); }),
        vscode.workspace.onDidOpenTextDocument(document => { if (supported(document)) scheduleStatus(); }),
        vscode.workspace.onDidCloseTextDocument(document => { if (supported(document)) scheduleStatus(); }));
    const selectGame = async (resource?: vscode.Uri): Promise<void> => {
        const uri = resource ?? vscode.window.activeTextEditor?.document.uri;
        const folder = uri && vscode.workspace.getWorkspaceFolder(uri);
        if (!folder) { await vscode.commands.executeCommand('workbench.action.openSettings', 'gamez.dialect'); return; }
        const configuration = vscode.workspace.getConfiguration('gamez', folder.uri);
        const current = configuration.get<string>('dialect', 'auto');
        const choices = [
            { label: 'Auto', value: 'auto', description: 'Detect from project metadata and verified source markers' },
            { label: 'MW3', value: 'mw3', description: 'Use MechWarrior 3 rules for this workspace folder' },
            { label: 'RECOIL', value: 'recoil', description: 'Use RECOIL rules for this workspace folder' }
        ].map(choice => ({ ...choice, picked: choice.value === current, detail: choice.value === current ? 'Current setting' : undefined }));
        const chosen = await vscode.window.showQuickPick(choices, { title: `GameZ Script game — ${folder.name}`, placeHolder: 'Select the game rules for this workspace folder' });
        if (chosen) await configuration.update('dialect', chosen.value, vscode.ConfigurationTarget.WorkspaceFolder);
    };
    context.subscriptions.push(vscode.commands.registerCommand('gamez.selectGame', selectGame));
    const encodingDiagnostics = vscode.languages.createDiagnosticCollection('gamez-script-encoding');
    const checkEncoding = (document: vscode.TextDocument): void => {
        if (!['gamez-script', 'gamez-gamegen', 'gamez-animation'].includes(document.languageId)) return;
        const text = document.getText();
        const at = /[^\x00-\x7f]/.exec(text)?.index;
        const encoding = document.encoding.toLowerCase();
        if (encoding !== 'iso88591' && (at !== undefined || encoding !== 'utf8')) {
            const start = at ?? 0;
            const diagnostic = new vscode.Diagnostic(new vscode.Range(document.positionAt(start), document.positionAt(Math.min(start + 1, text.length))),
                'GameZ source files use Latin-1. Use Save with Encoding → Western (ISO 8859-1) to preserve non-ASCII source characters.', vscode.DiagnosticSeverity.Error);
            diagnostic.source = 'GameZ Script'; diagnostic.code = 'GAMEZ_FILE_ENCODING'; encodingDiagnostics.set(document.uri, [diagnostic]);
        } else encodingDiagnostics.delete(document.uri);
    };
    context.subscriptions.push(encodingDiagnostics, vscode.workspace.onDidOpenTextDocument(checkEncoding),
        vscode.workspace.onDidChangeTextDocument(event => checkEncoding(event.document)), vscode.workspace.onDidSaveTextDocument(checkEncoding),
        vscode.workspace.onDidCloseTextDocument(document => encodingDiagnostics.delete(document.uri)));
    vscode.workspace.textDocuments.forEach(checkEncoding);
    retireClient = async (): Promise<void> => {
        invalidateStatusRequest();
        clientSubscriptions.forEach(subscription => subscription.dispose()); clientSubscriptions = [];
        latestGeneration = -1;
        const previous = client;
        client = undefined;
        // dispose marks the old client as disposed even if shutdown rejects in Starting state.
        // This also prevents its connection-closed handler from restarting it behind the new client.
        try { await previous?.dispose(); }
        catch (error) { output.warn(`Retiring language client: ${String(error)}`); }
        finally { watcher?.dispose(); watcher = undefined; }
    };
    const start = async (): Promise<void> => {
        const server: ServerOptions = { command: context.asAbsolutePath(path.join('server', 'GameZ.Script.LanguageServer.exe')), options: { cwd: context.extensionPath } };
        watcher?.dispose();
        watcher = vscode.workspace.createFileSystemWatcher('**/*.{zad,zrd,zan,gs,gw,gltf,glb,bin,json,png,wav}');
        clientSubscriptions.push(watcher.onDidCreate(() => scheduleStatus()), watcher.onDidChange(() => scheduleStatus()), watcher.onDidDelete(() => scheduleStatus()));
        const options: LanguageClientOptions = {
            documentSelector: ['gamez-script', 'gamez-gamegen', 'gamez-animation'].map(language => ({ scheme: 'file', language })),
            initializationOptions: { trusted: vscode.workspace.isTrusted,
                dialect: vscode.workspace.getConfiguration('gamez').get<string>('dialect', 'auto'),
                dialects: (vscode.workspace.workspaceFolders ?? []).map(folder => ({ uri: folder.uri.toString(), dialect: vscode.workspace.getConfiguration('gamez', folder.uri).get<string>('dialect', 'auto') })) },
            synchronize: { fileEvents: watcher }, outputChannel: output
        };
        client = new LanguageClient('gamez-script', 'GameZ Script', server, options);
        const selectedClient = client;
        clientSubscriptions.push(client.onNotification('gamez/gameProfileChanged', () => { if (client === selectedClient) scheduleStatus(); }),
            client.onDidChangeState(event => {
                if (client !== selectedClient) return;
                if (event.newState === State.Starting) {
                    // Automatic crash recovery starts a new server on this same client object.
                    // Its generation counter is independent of the previous process.
                    starting = true;
                    latestGeneration = -1;
                }
                else starting = false;
                scheduleStatus(0);
            }));
        await client.start();
    };
    const restart = (): Promise<void> => {
        lifecycle = lifecycle.catch(() => undefined).then(async () => {
            if (deactivating) return;
            starting = true;
            invalidateStatusRequest(); showPendingStatus();
            await retireClient();
            if (deactivating) return;
            try { await start(); }
            catch (error) {
                await retireClient();
                output.error(`Language server start failed: ${String(error)}`);
                // Activation must finish so the restart command stays available after a failed launch.
                void vscode.window.showErrorMessage('GameZ Script could not start its language server. Use GameZ Script: Restart Language Server to try again.', 'Restart server')
                    .then(action => { if (action && !deactivating) void restart(); });
            }
            finally { starting = false; if (!deactivating) await refreshStatus(); }
        });
        return lifecycle;
    };
    context.subscriptions.push(vscode.commands.registerCommand('gamez.restartLanguageServer', restart));
    context.subscriptions.push(vscode.commands.registerCommand('gamez.validateProject', async () => {
        const editor = vscode.window.activeTextEditor;
        if (!editor || !['gamez-script', 'gamez-gamegen', 'gamez-animation'].includes(editor.document.languageId)) { void vscode.window.showInformationMessage('Open a GameZ Script source file first.'); return; }
        if (!vscode.workspace.isTrusted) { void vscode.window.showInformationMessage('Trust this workspace to validate the source project.'); return; }
        if (!client?.isRunning()) {
            void vscode.window.showInformationMessage('GameZ Script language server is unavailable. Run GameZ Script: Restart Language Server, then validate again.');
            return;
        }
        try {
            const selectedClient = client;
            const selectedProfile = await selectedClient.sendRequest<GameProfile>('gamez/getGameProfile', { textDocument: { uri: editor.document.uri.toString() } });
            if (!selectedProfile.game) {
                void vscode.window.showInformationMessage('GameZ Script cannot determine this project’s game. Select MW3 or RECOIL before validating the project.', 'Choose game')
                    .then(action => { if (action) void selectGame(editor.document.uri); });
                return;
            }
            await vscode.window.withProgress({ location: vscode.ProgressLocation.Notification, title: 'GameZ Script: validating project', cancellable: true }, async (_, token) => {
                const result = await selectedClient.sendRequest<{files: number; errors: number; warnings: number}>('workspace/executeCommand', { command: 'gamez.server.validateProject', arguments: [editor.document.uri.toString()] }, token);
                if (result) void vscode.window.showInformationMessage(`GameZ Script: ${result.files} files checked, ${result.errors} errors, ${result.warnings} warnings. See Problems for details.`);
            });
        } catch (error) { if (!(error instanceof Error && error.message.includes('cancel'))) void vscode.window.showErrorMessage(`GameZ Script: ${String(error)}`); }
    }));
    context.subscriptions.push(vscode.workspace.onDidGrantWorkspaceTrust(restart));
    context.subscriptions.push(vscode.workspace.onDidChangeConfiguration(event => { if (event.affectsConfiguration('gamez.dialect')) void restart(); }),
        vscode.workspace.onDidChangeWorkspaceFolders(() => { void restart(); }));
    await restart();
    return { getGameStatus: () => ({ visible, text: status.text, tooltip: typeof status.tooltip === 'string' ? status.tooltip : '', profile: profile ? { ...profile } : undefined }) };
}
export async function deactivate(): Promise<void> { deactivating = true; await lifecycle.catch(() => undefined); await retireClient(); }
