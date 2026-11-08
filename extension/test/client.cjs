const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const assert = require('node:assert/strict');
const esbuild = require('esbuild');

// Exercise the actual client with controlled server lifecycle events. In particular,
// vscode-languageclient reuses its LanguageClient object after an automatic restart.
const event = () => ({
  listeners: [],
  register(fn) { this.listeners.push(fn); return {dispose: () => { this.listeners = this.listeners.filter(item => item !== fn); }}; },
  fire(value) { for (const fn of [...this.listeners]) fn(value); }
});
const disposable = () => ({dispose() {}});
const commands = new Map(), clients = [];
const State = {Stopped: 1, Running: 2, Starting: 3};
let generation = 50, requests = 0, delayedResponse;
const profile = (value, game = 'mw3') => ({game, source: 'workspace', rootUri: 'file:///D:/fixture', reason: null, restricted: false, generation: value});
class Client {
  constructor() { this.changed = event(); this.notifications = event(); this.running = false; clients.push(this); }
  onNotification(_, fn) { return this.notifications.register(fn); }
  onDidChangeState(fn) { return this.changed.register(fn); }
  isRunning() { return this.running; }
  async start() {
    this.running = false; this.changed.fire({newState: State.Starting});
    this.running = true; this.changed.fire({newState: State.Running});
  }
  async dispose() { this.running = false; }
  async sendRequest() {
    requests++;
    if (delayedResponse) { const response = delayedResponse; delayedResponse = undefined; return response; }
    return profile(generation);
  }
}
class CancellationTokenSource {
  constructor() { this.token = {isCancellationRequested: false}; }
  cancel() { this.token.isCancellationRequested = true; }
  dispose() {}
}
const folder = {name: 'fixture', uri: {toString: () => 'file:///D:/fixture'}};
const document = {uri: {scheme: 'file', toString: () => 'file:///D:/fixture/data/c1/zrdr/objectives.zrd'}, languageId: 'gamez-script', version: 1};
const vscode = {
  CancellationTokenSource, StatusBarAlignment: {Right: 2}, ConfigurationTarget: {WorkspaceFolder: 3},
  window: {
    activeTextEditor: {document}, createOutputChannel: () => ({warn() {}, error() {}, dispose() {}}),
    createStatusBarItem: () => ({text: '', tooltip: '', show() {}, hide() {}, dispose() {}}),
    onDidChangeActiveTextEditor: disposable, showErrorMessage: async () => undefined, showInformationMessage: async () => undefined
  },
  workspace: {
    isTrusted: true, workspaceFolders: [folder], textDocuments: [], getWorkspaceFolder: () => folder, getConfiguration: () => ({get: () => 'auto'}),
    onDidOpenTextDocument: disposable, onDidChangeTextDocument: disposable, onDidCloseTextDocument: disposable, onDidSaveTextDocument: disposable,
    onDidGrantWorkspaceTrust: disposable, onDidChangeConfiguration: disposable, onDidChangeWorkspaceFolders: disposable,
    createFileSystemWatcher: () => ({onDidCreate: disposable, onDidChange: disposable, onDidDelete: disposable, dispose() {}})
  },
  commands: {registerCommand: (name, fn) => { commands.set(name, fn); return disposable(); }, executeCommand: async () => undefined},
  languages: {createDiagnosticCollection: disposable}, Uri: {parse: () => ({fsPath: 'D:\\fixture'})}
};
const source = fs.readFileSync(path.join(__dirname, '../src/extension.ts'), 'utf8');
const output = esbuild.transformSync(source, {loader: 'ts', format: 'cjs', target: 'node22'}).code;
const loaded = {exports: {}};
vm.runInNewContext(output, {
  module: loaded, exports: loaded.exports,
  require: name => name === 'vscode' ? vscode : name === 'vscode-languageclient/node' ? {LanguageClient: Client, State} : require(name),
  setTimeout, clearTimeout, console
});
const waitUntil = async predicate => {
  const deadline = Date.now() + 3000;
  while (!predicate()) { if (Date.now() > deadline) assert.fail('Client lifecycle test timed out'); await new Promise(resolve => setTimeout(resolve, 10)); }
};
(async () => {
  const context = {subscriptions: [], extensionPath: 'D:/extension', asAbsolutePath: item => item};
  const api = await loaded.exports.activate(context);
  try {
    await waitUntil(() => api.getGameStatus().profile?.generation === 50);
    assert.equal(api.getGameStatus().text, 'GameZ: MW3');
    const original = clients[0];
    // Leave a request from the old process in flight; a late result must stay ignored.
    let resolveOld;
    delayedResponse = new Promise(resolve => { resolveOld = resolve; });
    const beforeRequests = requests;
    original.notifications.fire({});
    await waitUntil(() => requests > beforeRequests);
    original.running = false; original.changed.fire({newState: State.Stopped});
    generation = 1;
    await original.start();
    await waitUntil(() => api.getGameStatus().profile?.generation === 1);
    assert.equal(clients.length, 1, 'Automatic recovery keeps the same LanguageClient object');
    assert.equal(api.getGameStatus().text, 'GameZ: MW3', 'Fresh low generation replaces the previous process status');
    resolveOld(profile(99, 'recoil'));
    await new Promise(resolve => setTimeout(resolve, 30));
    assert.equal(api.getGameStatus().text, 'GameZ: MW3', 'Late previous-process result cannot overwrite recovered status');
    assert.equal(api.getGameStatus().profile.generation, 1);
    generation = 0;
    await commands.get('gamez.restartLanguageServer')();
    await waitUntil(() => api.getGameStatus().profile?.generation === 0);
    assert.equal(clients.length, 2, 'Explicit restart replaces the retired client');
    console.log('PASS client automatic and explicit restart generations, late response cancellation and status recovery');
  } finally {
    await loaded.exports.deactivate();
    context.subscriptions.forEach(subscription => subscription.dispose());
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
