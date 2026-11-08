const assert = require('node:assert/strict');
const cp = require('node:child_process');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const {pathToFileURL} = require('node:url');
const rpc = require('vscode-jsonrpc/node');
(async()=>{
  const temporary = fs.realpathSync.native(fs.mkdtempSync(path.join(os.tmpdir(),'gamez-mw3-')));
  const roots = ['recoil','mw3','partial'].map(name=>path.join(temporary,name));
  const uri = file=>pathToFileURL(file).href;
  const child = cp.spawn(process.env.GAMEZ_SERVER || path.resolve(__dirname,'../server/GameZ.Script.LanguageServer.exe'),[],{stdio:['pipe','pipe','pipe'],windowsHide:true});
  let errors=''; child.stderr.on('data',b=>errors+=b);
  const connection=rpc.createMessageConnection(new rpc.StreamMessageReader(child.stdout),new rpc.StreamMessageWriter(child.stdin));
  const notices=[]; connection.onNotification('textDocument/publishDiagnostics',notice=>notices.push(notice)); connection.listen();
  const deadline=setTimeout(()=>{child.kill();},60000);
  const restricted=process.argv.includes('--restricted');
  function write(root,relative,text){const file=path.join(root,relative);fs.mkdirSync(path.dirname(file),{recursive:true});fs.writeFileSync(file,text);return file;}
  const open=async(file,text,languageId='gamez-script')=>connection.sendNotification('textDocument/didOpen',{textDocument:{uri:uri(file),text,languageId,version:1}});
  const range=text=>({start:{line:0,character:0},end:{line:10000,character:10000}});
  const hints=async(file,text)=>connection.sendRequest('textDocument/inlayHint',{textDocument:{uri:uri(file)},range:range(text)});
  try {
    for(const root of roots){fs.mkdirSync(path.join(root,'data'),{recursive:true});fs.mkdirSync(path.join(root,'gamegen'));}
    write(roots[1],'data/c1/models/world.gltf',JSON.stringify({asset:{version:'2.0'},scene:0,scenes:[{extras:{zstudio:{sourceFormat:4,game:'mw3'}}}],nodes:[]}));
    const init=await connection.sendRequest('initialize',{workspaceFolders:roots.map(root=>({uri:uri(root),name:path.basename(root)})),initializationOptions:{trusted:!restricted,dialects:[{uri:uri(roots[1]),dialect:restricted?'mw3':'auto'}]}});
    assert.equal(init.serverInfo.version,require('../package.json').version);
    await connection.sendNotification('initialized',{});
    const net='(version (106) node_00 (12 (1.0 2.0 3.0) (1)) node_01 (12 (4.0 5.0 6.0) (-1)))';
    const nativeFile=write(roots[1],'data/c1/zrdr/aipath/m1/net_01.zrd',net);
    const recoilFile=write(roots[0],'data/m1/zrdr/net_01.zrd','');
    await open(nativeFile,net); await open(recoilFile,'');
    const nativeHints=await hints(nativeFile,net);
    const nativeProfile=await connection.sendRequest('gamez/getGameProfile',{textDocument:{uri:uri(nativeFile)}});
    assert.equal(nativeProfile.game,'mw3','The visible profile agrees with native assistance');
    assert.equal(nativeProfile.source,restricted?'configuration':'workspace');
    assert(nativeHints.length>=8,'MW3 variable-link nodes receive native metadata');
    assert(nativeHints.some(h=>/x:/i.test(h.label)),'Native XYZ hints');
    if(!restricted){
      const partialNet=write(roots[2],'data/c1/zrdr/net_01.zrd',net);
      const weapon='(bot (common_mode (weapons ((SRM2 1 1 5000 2.0 25.0 1000.0 0 firepoint (firepoint01 firepoint02))))))';
      const sibling=write(roots[2],'data/common/zrdr/vehicle.zrd',weapon);
      await open(partialNet,net);await open(sibling,weapon);
      const siblingHints=await hints(sibling,weapon);
      assert(siblingHints.some(h=>h.label==='availability:'),'Native marker in a partial owning project reaches ambiguous sibling fast paths');
      assert(!siblingHints.some(h=>h.label==='initial hardpoint state:'),'Partial native project never applies RECOIL weapon layout');
    }
    const rotor='SI Animation Script\nFRAMES: 2\nOBJECTS: 1\n\nFrame: 1\nObject: actor\nScaling: 1.0 1.0 1.0\nRotation: 0.0 0.0 0.0\nTranslation: 0.0 1.0 2.0\n\nFrame: 4\nObject: actor\nScaling: 1.0 1.0 1.0\nRotation: 0.0 0.0 0.0\nTranslation: 3.0 4.0 5.0\n';
    const siFile=write(roots[1],'data/c1/zrdr/clip.zan',rotor);
    await open(siFile,rotor,'gamez-animation');
    assert((await hints(siFile,rotor)).length>=18,'SI channel metadata');
    const motion='MOTION (LOOP_SECONDS (1.0) FRAME_COUNT (1) PART (NAME (hip) SAMPLES ((0.0 1.0 2.0 1.0 0.0 0.0 0.0) (0.0 1.0 2.0 1.0 0.0 0.0 0.0))))';
    const motionFile=write(roots[1],'data/common/mechs/motions/sample.zan',motion);
    await open(motionFile,motion,'gamez-animation');
    const motionHints=await hints(motionFile,motion);
    assert(motionHints.length>=14,'Both seven-component samples receive hints');
    assert(motionHints.some(h=>/w:/i.test(h.label)),'Quaternion W remains first');
    const waitUntil=Date.now()+10000;
    while(!notices.some(n=>n.uri===uri(nativeFile))&&Date.now()<waitUntil)await new Promise(r=>setTimeout(r,50));
    const nativeNotice=notices.filter(n=>n.uri===uri(nativeFile)).at(-1);
    assert(nativeNotice,'Native diagnostics published');
    assert(!nativeNotice.diagnostics.some(d=>/^RECOIL_/.test(d.code)),'Initial native analysis never flashes RECOIL rules');
    assert(!nativeNotice.diagnostics.some(d=>d.severity===1),'The valid native variable-link network is accepted');
    await connection.sendNotification('workspace/didChangeConfiguration',{settings:{dialects:[{uri:uri(roots[1]),dialect:'recoil'}]}});
    const recoilHints=await hints(nativeFile,net);
    assert.equal((await connection.sendRequest('gamez/getGameProfile',{textDocument:{uri:uri(nativeFile)}})).game,'recoil');
    assert.notDeepEqual(recoilHints.map(h=>h.label),nativeHints.map(h=>h.label),'Dialect override invalidates local assistance');
    await connection.sendNotification('workspace/didChangeConfiguration',{settings:{dialects:[{uri:uri(roots[1]),dialect:'mw3'}]}});
    assert.deepEqual((await hints(nativeFile,net)).map(h=>h.label),nativeHints.map(h=>h.label));
    assert.equal(fs.readFileSync(nativeFile,'utf8'),net); assert.equal(fs.readFileSync(motionFile,'utf8'),motion);
    await connection.sendRequest('shutdown');await connection.sendNotification('exit');
    console.log('PASS MW3 '+(restricted?'restricted':'trusted')+' profiles, multi-root native hints, SI/motion ZAN and configuration changes');
  } finally {clearTimeout(deadline);connection.dispose();child.kill();fs.rmSync(temporary,{recursive:true,force:true,maxRetries:5,retryDelay:200});if(errors.includes('Unhandled exception'))throw new Error(errors);}
})().catch(error=>{console.error(error);process.exitCode=1;});
