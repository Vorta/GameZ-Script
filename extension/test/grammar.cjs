const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { Registry, parseRawGrammar } = require('vscode-textmate');
const onig = require('vscode-oniguruma');
(async () => {
  const wasm = fs.readFileSync(require.resolve('vscode-oniguruma/release/onig.wasm'));
  await onig.loadWASM(wasm.buffer.slice(wasm.byteOffset, wasm.byteOffset + wasm.byteLength));
  const grammarFile = path.resolve(__dirname, '../syntaxes/gamez.tmLanguage.json');
  const registry = new Registry({ onigLib: Promise.resolve({ createOnigScanner: s => new onig.OnigScanner(s), createOnigString: s => new onig.OnigString(s) }), loadGrammar: async scope => {
    const file = scope === 'source.gamez.gamegen' ? path.resolve(__dirname, '../syntaxes/gamegen.tmLanguage.json') : grammarFile;
    return parseRawGrammar(fs.readFileSync(file, 'utf8'), file);
  } });
  const grammar = await registry.loadGrammar('source.gamez');
  const scopes = grammar.tokenizeLine('CALL_ANIMATION ( NAME ( child ) START_TIME ( EVENT_OFFSET 10.0 ) ) # hello').tokens.flatMap(t => t.scopes);
  for (const scope of ['keyword.control.gamez', 'constant.numeric.float.gamez', 'comment.line.number-sign.gamez', 'support.type.property-name.gamez']) assert(scopes.includes(scope), scope);
  const stringScopes = grammar.tokenizeLine('"path\\\\file\\x20.zan" f32:7FC00001').tokens.flatMap(t => t.scopes);
  assert(stringScopes.includes('constant.character.escape.gamez')); assert(stringScopes.includes('constant.numeric.float.raw.gamez'));
  const script = await registry.loadGrammar('source.gamez.gamegen');
  const scriptScopes = script.tokenizeLine('LoadGameGen %model%.gltf tank # comment').tokens.flatMap(t => t.scopes);
  for (const scope of ['support.function.gamegen', 'variable.other.macro.gamegen', 'comment.line.number-sign.gamegen']) assert(scriptScopes.includes(scope), scope);
  const quotes = script.tokenizeLine('source "a#comment"').tokens.flatMap(t => t.scopes);
  assert(quotes.includes('comment.line.number-sign.gamegen')); assert(!quotes.some(s => s.startsWith('string.')));
  for (const line of ['source#comment','set,x,y#comment']) {
    const tokens=script.tokenizeLine(line).tokens;
    assert(tokens.some(t=>t.scopes.includes('comment.line.number-sign.gamegen')),'Adjacent builtin comment');
    if(line.includes(',')) assert(tokens.some(t=>t.scopes.includes('punctuation.separator.gamegen')),'Builtin comma operands');
  }
  assert.deepEqual(require('../gamegen-language-configuration.json').autoClosingPairs, []);
  const languages = require('../package.json').contributes.languages;
  assert(languages.find(l => l.id === 'gamez-gamegen').extensions.includes('.gw'));
  assert(languages.find(l => l.id === 'gamez-script').extensions.includes('.zad'));
  registry.dispose(); console.log('PASS GameZ Script grammar');
})().catch(e => { console.error(e); process.exitCode = 1; });
