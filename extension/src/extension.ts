import * as vscode from 'vscode';
import * as path from 'node:path';
import { LanguageClient, LanguageClientOptions, ServerOptions } from 'vscode-languageclient/node';

let client: LanguageClient | undefined;
let watcher: vscode.FileSystemWatcher | undefined;
let lifecycle: Promise<void> = Promise.resolve();
let deactivating = false;
let retireClient: () => Promise<void> = async () => {};
export async function activate(context: vscode.ExtensionContext): Promise<void> {
    deactivating = false;
    const output = vscode.window.createOutputChannel('GameZ Script', { log: true });
    context.subscriptions.push(output);
    const encodingDiagnostics = vscode.languages.createDiagnosticCollection('gamez-script-encoding');
    const checkEncoding = (document: vscode.TextDocument): void => {
        if (!['gamez-script', 'gamez-gamegen'].includes(document.languageId)) return;
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
        const options: LanguageClientOptions = {
            documentSelector: [{ scheme: 'file', language: 'gamez-script' }, { scheme: 'file', language: 'gamez-gamegen' }],
            initializationOptions: { trusted: vscode.workspace.isTrusted },
            synchronize: { fileEvents: watcher }, outputChannel: output
        };
        client = new LanguageClient('gamez-script', 'GameZ Script', server, options);
        await client.start();
    };
    const restart = (): Promise<void> => {
        lifecycle = lifecycle.catch(() => undefined).then(async () => {
            if (deactivating) return;
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
        });
        return lifecycle;
    };
    context.subscriptions.push(vscode.commands.registerCommand('gamez.restartLanguageServer', restart));
    context.subscriptions.push(vscode.commands.registerCommand('gamez.validateProject', async () => {
        const editor = vscode.window.activeTextEditor;
        if (!editor || !['gamez-script', 'gamez-gamegen'].includes(editor.document.languageId)) { void vscode.window.showInformationMessage('Open a GameZ Script source file first.'); return; }
        if (!vscode.workspace.isTrusted) { void vscode.window.showInformationMessage('Trust this workspace to validate the source project.'); return; }
        if (!client?.isRunning()) {
            void vscode.window.showInformationMessage('GameZ Script language server is unavailable. Run GameZ Script: Restart Language Server, then validate again.');
            return;
        }
        try {
            await vscode.window.withProgress({ location: vscode.ProgressLocation.Notification, title: 'GameZ Script: validating project', cancellable: true }, async (_, token) => {
                const result = await client?.sendRequest<{files: number; errors: number; warnings: number}>('workspace/executeCommand', { command: 'gamez.server.validateProject', arguments: [editor.document.uri.toString()] }, token);
                if (result) void vscode.window.showInformationMessage(`GameZ Script: ${result.files} files checked, ${result.errors} errors, ${result.warnings} warnings. See Problems for details.`);
            });
        } catch (error) { if (!(error instanceof Error && error.message.includes('cancel'))) void vscode.window.showErrorMessage(`GameZ Script: ${String(error)}`); }
    }));
    context.subscriptions.push(vscode.workspace.onDidGrantWorkspaceTrust(restart));
    await restart();
}
export async function deactivate(): Promise<void> { deactivating = true; await lifecycle.catch(() => undefined); await retireClient(); }
