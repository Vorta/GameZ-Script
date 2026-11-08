const assert = require('node:assert/strict');
const cp = require('node:child_process');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const {pathToFileURL} = require('node:url');
const rpc = require('vscode-jsonrpc/node');

const executable = process.env.GAMEZ_SERVER || path.resolve(__dirname, '../server/GameZ.Script.LanguageServer.exe');
const uri = file => pathToFileURL(file).href.replace(/^file:\/\/\/([A-Za-z]):/, 'file:///$1%3A');
const canonical = value => decodeURIComponent(value).toLowerCase();
const range = {start:{line:0,character:0},end:{line:10000,character:10000}};
const objective = '(m1 (primary (test (node_state (node (building healthy) state (active))))))';
const vehicle = '(bot (common_mode (weapons ((SRM2 1 1 5000 2.0 25.0 1000.0 0 firepoint (firepoint01 firepoint02))))))';
const animation = '(ANIMATION_DEFINITIONS (ANIMATION_LIST (ANIMATION_DEFINITION (NAME (root) ANIMATION_NAME (child)))))';
const nativeNet = '(version (106) node_00 (12 (1.0 2.0 3.0) (1)) node_01 (12 (4.0 5.0 6.0) (-1)))';
const delay = ms => new Promise(resolve => setTimeout(resolve, ms));
function write(file, text) { fs.mkdirSync(path.dirname(file), {recursive:true}); fs.writeFileSync(file, text); return file; }
function project(root) { fs.mkdirSync(path.join(root,'data'),{recursive:true}); fs.mkdirSync(path.join(root,'gamegen')); return root; }
function model(game) { return JSON.stringify({asset:{version:'2.0'},scene:0,scenes:[{extras:{zstudio:{sourceFormat:4,...(game==='mw3'?{game}: {})}}}],nodes:[]}); }

async function scenario(trusted) {
  const temporary = fs.realpathSync.native(fs.mkdtempSync(path.join(os.tmpdir(),'gamez-profiles-')));
  const roots = ['native','recoil','unknown'].map(name => project(path.join(temporary,name)));
  const nativeModel = write(path.join(roots[0],'data/c1/models/world.gltf'),model('mw3'));
  write(path.join(roots[1],'data/m1/models/world.gltf'),model('recoil'));
  const child = cp.spawn(executable, [], {stdio:['pipe','pipe','pipe'],windowsHide:true});
  let errors=''; child.stderr.on('data', b => errors += b);
  const connection = rpc.createMessageConnection(new rpc.StreamMessageReader(child.stdout),new rpc.StreamMessageWriter(child.stdin));
  const notices=[]; let profileChanges=0;
  connection.onNotification('textDocument/publishDiagnostics', n => notices.push(n));
  connection.onNotification('gamez/gameProfileChanged', () => profileChanges++);
  connection.listen();
  const deadline = setTimeout(() => child.kill(),60000);
  const versions=new Map();
  const open = async(file,text,languageId='gamez-script') => { versions.set(file,1); await connection.sendNotification('textDocument/didOpen',{textDocument:{uri:uri(file),text,languageId,version:1}}); };
  const change = async(file,text) => { const version=versions.get(file)+1;versions.set(file,version);await connection.sendNotification('textDocument/didChange',{textDocument:{uri:uri(file),version},contentChanges:[{text}]}); };
  const request = (method,file,rest={}) => connection.sendRequest(method,{textDocument:{uri:uri(file)},...rest});
  const profile = file => request('gamez/getGameProfile',file);
  const hints = file => request('textDocument/inlayHint',file,{range});
  const position = (text, needle) => ({line:0,character:text.indexOf(needle)});
  const latest = file => notices.filter(n => canonical(n.uri)===canonical(uri(file)) && n.version===versions.get(file)).at(-1);
  const waitDiagnostics = async(file,predicate=()=>true) => { const until=Date.now()+10000; while(Date.now()<until){const n=latest(file);if(n && predicate(n.diagnostics))return n.diagnostics;await delay(40);}throw new Error('Missing expected diagnostics: '+file+' '+JSON.stringify(latest(file))); };
  const configure = async(dialects=[]) => connection.sendNotification('workspace/didChangeConfiguration',{settings:{dialect:'auto',dialects:dialects.map(([root,dialect])=>({uri:uri(root),dialect}))}});
  try {
    await connection.sendRequest('initialize',{workspaceFolders:roots.map(root=>({uri:uri(root),name:path.basename(root)})),initializationOptions:{trusted,dialect:'auto'}});
    await connection.sendNotification('initialized',{});
    const objectives = roots.map(root => write(path.join(root,'data/c1/zrdr/objectives.zrd'),objective));
    for(const file of objectives) await open(file,objective);
    for(let i=0;i<objectives.length;i++) {
      const p=await profile(objectives[i]);
      assert.equal(p.game,trusted?(i===0?'mw3':i===1?'recoil':null):null);
      assert.equal(p.source,trusted && i<2?'workspace':'unknown');
      assert.equal(p.restricted,!trusted);
      assert.equal(typeof p.generation,'number');
      const diagnostics=await waitDiagnostics(objectives[i]);
      assert(!diagnostics.some(d=>/NODE_(ID|SHAPE|LINK)/.test(d.code)),'node_state is never a spatial node');
    }
    const ambiguous=write(path.join(roots[2],'data/common/zrdr/anim.zad'),animation);
    const script=write(path.join(roots[2],'gamegen/unknown.gs'),'NewWorld root\nSetCameraType garbage\n');
    await open(ambiguous,animation);await open(script,fs.readFileSync(script,'utf8'),'gamez-gamegen');
    for(const [file,text,needle] of [[ambiguous,animation,'root'],[script,fs.readFileSync(script,'utf8'),'root']]) {
      assert.equal((await profile(file)).game,null);
      assert.deepEqual(await hints(file),[],'Unknown has no dialect-specific hints');
      assert.equal(await request('textDocument/hover',file,{position:position(text,needle)}),null,'Unknown has no dialect-specific hover');
      assert.equal(await request('textDocument/signatureHelp',file,{position:position(text,needle)}),null,'Unknown has no dialect-specific signature');
      assert.deepEqual((await request('textDocument/completion',file,{position:position(text,needle)})).items,[],'Unknown has no dialect-specific completion');
      assert.deepEqual(await request('textDocument/definition',file,{position:position(text,needle)}),[],'Unknown has no dialect-specific navigation');
      assert((await request('textDocument/documentSymbol',file)).length>0,'Unknown retains source outline');
      const diagnostics=await waitDiagnostics(file);
      assert(!diagnostics.some(d=>d.severity===1 || /^RECOIL_|^MW3_|^GAMEZ_(COMMAND|OPERANDS|BOOLEAN|NUMBER)$/.test(d.code)),'Unknown avoids engine-specific findings');
    }
    await change(ambiguous,'(NAME (root)');
    const malformed=await waitDiagnostics(ambiguous,d=>d.some(x=>x.code==='GAMEZ_SYNTAX'));
    assert(malformed.some(d=>d.code==='GAMEZ_SYNTAX'),'Unknown retains shared syntax checks');
    await change(ambiguous,animation);
    await assert.rejects(connection.sendRequest('workspace/executeCommand',{command:'gamez.server.validateProject',arguments:[uri(ambiguous)]}),trusted?/unknown|select game/i:/trusted/i);
    await configure([[roots[2],'recoil']]);
    assert.equal((await profile(ambiguous)).game,'recoil');
    assert.equal((await profile(ambiguous)).source,'configuration');
    assert((await hints(ambiguous)).some(h=>h.label==='root node:'),'Explicit game activates semantic assistance');
    // Exercise the project cache before returning to Unknown.
    await request('textDocument/completion',ambiguous,{position:position(animation,'root')});
    await configure();
    assert.equal((await profile(ambiguous)).game,null);
    assert.deepEqual(await hints(ambiguous),[],'Returning to Unknown removes known cached hints');
    assert.equal(await request('textDocument/hover',ambiguous,{position:position(animation,'root')}),null);
    assert.deepEqual((await request('textDocument/completion',ambiguous,{position:position(animation,'root')})).items,[]);
    const nativeVehicle=write(path.join(roots[0],'data/common/zrdr/vehicle.zrd'),vehicle);
    await open(nativeVehicle,vehicle);
    await configure([[roots[0],'mw3']]);
    assert.equal((await profile(objectives[0])).game,'mw3');
    assert((await hints(nativeVehicle)).some(h=>h.label==='availability:'),'Profile MW3 implies native weapon layout');
    await configure([[roots[0],'recoil']]);
    assert.equal((await profile(nativeVehicle)).game,'recoil');
    assert(!(await hints(nativeVehicle)).some(h=>h.label==='availability:'),'Override changes actual semantics');
    await configure();
    if(trusted) {
      assert.equal((await profile(nativeVehicle)).game,'mw3');
      // Remove authoritative evidence after a project request, so an old project cannot keep MW3 sticky.
      await request('textDocument/completion',nativeVehicle,{position:position(vehicle,'SRM2')});
      fs.unlinkSync(nativeModel);
      await connection.sendNotification('workspace/didChangeWatchedFiles',{changes:[{uri:uri(nativeModel),type:3}]});
      assert.equal((await profile(nativeVehicle)).game,null);
      assert.deepEqual(await hints(nativeVehicle),[],'Deleted identity invalidates cached semantic assistance');
      const closedMarker=write(path.join(roots[2],'data/c1/zrdr/net_01.zrd'),nativeNet);
      await connection.sendNotification('workspace/didChangeWatchedFiles',{changes:[{uri:uri(closedMarker),type:1}]});
      assert.equal((await profile(ambiguous)).game,'mw3','Trusted partial project discovers a closed positive marker');
      assert.equal((await profile(ambiguous)).source,'project-source');
      // The source overlay hides the on-disk marker during editing.
      await open(closedMarker,'()');
      assert.equal((await profile(ambiguous)).game,null,'Unsaved source overlay removes identity without saving');
      assert.equal(fs.readFileSync(closedMarker,'utf8'),nativeNet);
      const childProject=project(path.join(roots[2],'data/native-child'));
      write(path.join(childProject,'data/c1/models/world.gltf'),model('mw3'));
      const childObjective=write(path.join(childProject,'data/c1/zrdr/objectives.zrd'),objective);
      await open(childObjective,objective);
      await connection.sendNotification('workspace/didChangeWatchedFiles',{changes:[{uri:uri(path.join(childProject,'data/c1/models/world.gltf')),type:1}]});
      assert.equal((await profile(ambiguous)).game,null,'A nested project model cannot determine its parent project game');
      const childProfile=await profile(childObjective);
      assert.equal(childProfile.game,'mw3','A nested project retains its own authoritative model identity');
      assert.equal(childProfile.source,'workspace');
      assert.equal(canonical(childProfile.rootUri),canonical(uri(childProject)));
      // Deep validation of a RECOIL parent must not reinterpret a nested native project.
      const recoilChild=project(path.join(roots[1],'data/native-child'));
      write(path.join(recoilChild,'data/c1/models/world.gltf'),model('mw3'));
      const recoilChildNet=write(path.join(recoilChild,'data/c1/zrdr/net_01.zrd'),nativeNet);
      await open(recoilChildNet,nativeNet);
      await connection.sendNotification('workspace/didChangeWatchedFiles',{changes:[{uri:uri(path.join(recoilChild,'data/c1/models/world.gltf')),type:1}]});
      assert.equal((await profile(objectives[1])).game,'recoil');
      const nestedNativeProfile=await profile(recoilChildNet);
      assert.equal(nestedNativeProfile.game,'mw3');
      assert.equal(canonical(nestedNativeProfile.rootUri),canonical(uri(recoilChild)));
      assert((await hints(recoilChildNet)).some(h=>h.label==='x:'),'Nested native assistance uses the child game');
      const beforeParentValidation=notices.length;
      const parentValidation=await connection.sendRequest('workspace/executeCommand',{command:'gamez.server.validateProject',arguments:[uri(objectives[1])]});
      assert.equal(parentValidation.files,1,'Parent validation counts only sources owned by the parent project');
      const nestedPublications=notices.slice(beforeParentValidation).filter(n=>canonical(n.uri)===canonical(uri(recoilChildNet)));
      assert(!nestedPublications.some(n=>n.diagnostics.some(d=>/^RECOIL_|^GAMEZ_ARITY$/.test(d.code))),'Parent validation never publishes parent-dialect diagnostics onto a nested native source');
      assert.equal((await profile(recoilChildNet)).game,'mw3','Parent validation preserves the child profile');
      // A failed identity scan must not prevent local assistance on a decisive active document.
      const incompleteProject=project(path.join(roots[2],'incomplete-identity'));
      write(path.join(incompleteProject,'data/aa.zrd'),Buffer.alloc(16*1024*1024+1,32));
      const incompleteNet=write(path.join(incompleteProject,'data/net_01.zrd'),'()');
      const partialNet='(version (106) node_';
      await open(incompleteNet,partialNet);
      const localProfile=await profile(incompleteNet);
      assert.equal(localProfile.game,'mw3','Active native markers survive an unavailable project identity');
      assert.equal(localProfile.source,'document');
      assert.match(localProfile.reason,/unavailable/i);
      const localCompletion=await request('textDocument/completion',incompleteNet,{position:{line:0,character:partialNet.length}});
      assert(localCompletion.items.some(item=>item.label==='node_00'),'Local native completion works while project context is unavailable');
      assert.deepEqual(await request('textDocument/definition',incompleteNet,{position:{line:0,character:partialNet.indexOf('106')}}),[],'Unavailable project context leaves navigation local without throwing');
      assert.equal(fs.readFileSync(incompleteNet,'utf8'),'()','Native identity and assistance use an unsaved buffer');
    } else {
      // A restricted workspace can contain several nested projects; source evidence stays local.
      const nested=project(path.join(roots[2],'nested'));
      const marker=write(path.join(nested,'data/c1/zrdr/net_01.zrd'),nativeNet);
      const sibling=write(path.join(nested,'data/c1/zrdr/objectives.zrd'),objective);
      await open(marker,nativeNet);await open(sibling,objective);
      assert.equal((await profile(marker)).game,'mw3');
      assert.equal((await profile(marker)).source,'document');
      assert((await hints(marker)).some(h=>h.label==='x:'),'Intrinsic MW3 document evidence enables native assistance');
      assert.equal((await profile(sibling)).game,null,'Restricted sibling source does not inherit an unverified owning project');
      assert.equal((await profile(ambiguous)).game,null,'Nested document evidence does not leak across projects');
    }
    assert(profileChanges>0,'Resolution-affecting changes notify the client');
    for(const file of objectives)assert.equal(fs.readFileSync(file,'utf8'),objective,'Analysis leaves objectives unchanged');
    await connection.sendRequest('shutdown');await connection.sendNotification('exit');
    console.log('PASS game profiles '+(trusted?'trusted':'restricted')+': initial node_state, neutral providers, overrides, cache transitions, provenance and project boundaries');
  } finally {
    clearTimeout(deadline);connection.dispose();child.kill();
    const resolved=fs.realpathSync.native(temporary), tempRoot=fs.realpathSync.native(os.tmpdir());
    assert(resolved.startsWith(tempRoot+path.sep),'Disposable test folder remains within temp');
    fs.rmSync(resolved,{recursive:true,force:true,maxRetries:5,retryDelay:200});
    if(errors.includes('Unhandled exception'))throw new Error(errors);
  }
}
async function validationCancellation() {
  const temporary = fs.realpathSync.native(fs.mkdtempSync(path.join(os.tmpdir(),'gamez-profile-cancel-')));
  const root = project(path.join(temporary,'project'));
  const text='(TEST (ok))';
  for(let i=0;i<2500;i++)write(path.join(root,'data/zrdr',`closed_${String(i).padStart(4,'0')}.zrd`),text);
  const file=write(path.join(root,'data/zrdr/open.zrd'),text);
  const child=cp.spawn(executable,[],{stdio:['pipe','pipe','pipe'],windowsHide:true});
  let errors='',removed=false,removal;
  child.stderr.on('data',b=>errors+=b);
  const connection=rpc.createMessageConnection(new rpc.StreamMessageReader(child.stdout),new rpc.StreamMessageWriter(child.stdin));
  connection.onNotification('textDocument/publishDiagnostics',n=>{
    if(n.uri.includes('closed_')&&!removed){
      removed=true;
      removal=connection.sendNotification('workspace/didChangeWorkspaceFolders',{event:{removed:[{uri:uri(root),name:'project'}],added:[]}});
    }
  });
  connection.listen();
  const deadline=setTimeout(()=>child.kill(),30000);
  try {
    await connection.sendRequest('initialize',{workspaceFolders:[{uri:uri(root),name:'project'}],initializationOptions:{trusted:true,dialect:'recoil'}});
    await connection.sendNotification('initialized',{});
    await connection.sendNotification('textDocument/didOpen',{textDocument:{uri:uri(file),languageId:'gamez-script',version:1,text}});
    await assert.rejects(connection.sendRequest('workspace/executeCommand',{command:'gamez.server.validateProject',arguments:[uri(file)]}),/cancel|changed/i,'Removing the owning workspace during publication cancels validation instead of returning stale success');
    assert(removed,'The test removed the workspace after the first closed-source diagnostic publication');
    await removal;
    await connection.sendRequest('shutdown');await connection.sendNotification('exit');
    console.log('PASS game profiles: removing a workspace during closed-source publication cancels deep validation');
  } finally {
    clearTimeout(deadline);connection.dispose();child.kill();
    const resolved=fs.realpathSync.native(temporary),tempRoot=fs.realpathSync.native(os.tmpdir());
    assert(resolved.startsWith(tempRoot+path.sep),'Disposable cancellation test remains within temp');
    fs.rmSync(resolved,{recursive:true,force:true,maxRetries:5,retryDelay:200});
    if(errors.includes('Unhandled exception'))throw new Error(errors);
  }
}
(async()=>{await scenario(true);await scenario(false);await validationCancellation();})().catch(error=>{console.error(error);process.exitCode=1;});
