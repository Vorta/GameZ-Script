const path = require('node:path');
const fs = require('node:fs');
const os = require('node:os');
const cp = require('node:child_process');
const {runTests} = require('@vscode/test-electron');
(async()=>{
  // Launch VS Code with the same expanded Windows path spelling used by .NET.
  const profile = fs.realpathSync.native(fs.mkdtempSync(path.join(os.tmpdir(),'gamez-vscode-')));
  const workspace = path.join(profile,'project');
  fs.mkdirSync(path.join(workspace,'gamegen'),{recursive:true}); fs.mkdirSync(path.join(workspace,'data/m1/zrdr'),{recursive:true});
  fs.writeFileSync(path.join(workspace,'data/m1/zrdr/anim.zad'),'(ANIMATION_DEFINITIONS (ANIMATION_LIST (ANIMATION_DEFINITION (NAME (root) ANIMATION_NAME (child)))))');
  fs.writeFileSync(path.join(workspace,'data/m1/zrdr/bom.zrd'),'\ufeff(TEST (ascii))','utf8');
  fs.writeFileSync(path.join(workspace,'data/m1/zrdr/utf16.zrd'),'\ufeff(TEST (ascii))','utf16le');
  try {
    await runTests({ vscodeExecutablePath: process.env.GAMEZ_VSCODE, version:'stable', extensionDevelopmentPath:path.resolve(__dirname,'..'), extensionTestsPath:path.join(__dirname,'host-suite.cjs'),
      launchArgs:[workspace,'--user-data-dir',path.join(profile,'profile'),'--extensions-dir',path.join(profile,'extensions'),'--disable-workspace-trust','--skip-welcome','--skip-release-notes','--disable-updates','--disable-telemetry'] });
    // A real failing protocol peer exercises the client's Starting-state cleanup.
    const failingSource = path.join(profile,'failing-peer'); fs.mkdirSync(failingSource);
    fs.writeFileSync(path.join(failingSource,'peer.csproj'),'<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><AssemblyName>GameZ.Script.LanguageServer</AssemblyName></PropertyGroup></Project>');
    fs.writeFileSync(path.join(failingSource,'Program.cs'),`
using System; using System.Text; using System.Text.Json;
using var input = Console.OpenStandardInput(); using var output = Console.OpenStandardOutput();
while (true) {
  var header = new StringBuilder(); int b;
  while ((b = input.ReadByte()) >= 0) { header.Append((char)b); if (header.ToString().EndsWith("\\r\\n\\r\\n")) break; }
  if (b < 0) return;
  int length = int.Parse(header.ToString().Split(": ")[1].Split('\\r')[0]);
  var bytes = new byte[length]; input.ReadExactly(bytes); using var message = JsonDocument.Parse(bytes);
  if (message.RootElement.GetProperty("method").GetString() != "initialize") continue;
  var reply = Encoding.UTF8.GetBytes("{\\"jsonrpc\\":\\"2.0\\",\\"id\\":" + message.RootElement.GetProperty("id").GetRawText() + ",\\"error\\":{\\"code\\":-32603,\\"message\\":\\"Synthetic initialize failure\\"}}");
  output.Write(Encoding.ASCII.GetBytes("Content-Length: " + reply.Length + "\\r\\n\\r\\n")); output.Write(reply); output.Flush(); return;
}`);
    const failingOutput = path.join(profile,'failing-output');
    cp.execFileSync('dotnet',['publish',path.join(failingSource,'peer.csproj'),'-c','Release','-r','win-x64','--self-contained','false','-o',failingOutput],{windowsHide:true,stdio:'pipe'});
    for (const mode of ['missing','initialize']) {
      const root = path.join(profile,mode); fs.mkdirSync(root);
      const copy = path.join(root,'extension');
      const original = path.resolve(__dirname,'..');
      fs.cpSync(original,copy,{recursive:true,filter:p=>!['node_modules','server','test','.vscode-test'].includes(path.relative(original,p).split(path.sep)[0])});
      if (mode==='initialize') fs.cpSync(failingOutput,path.join(copy,'server'),{recursive:true});
      await runTests({vscodeExecutablePath:process.env.GAMEZ_VSCODE,version:'stable',extensionDevelopmentPath:copy,extensionTestsPath:path.join(__dirname,'recovery-suite.cjs'),
        extensionTestsEnv:{GAMEZ_RECOVERY_ROOT:root,GAMEZ_RECOVERY_MODE:mode,GAMEZ_RECOVERY_SERVER_SOURCE:path.join(original,'server/GameZ.Script.LanguageServer.exe')},
        launchArgs:[workspace,'--user-data-dir',path.join(root,'profile'),'--extensions-dir',path.join(root,'extensions'),'--disable-workspace-trust','--skip-welcome','--skip-release-notes','--disable-updates','--disable-telemetry']});
    }
  } finally { fs.rmSync(profile,{recursive:true,force:true,maxRetries:5,retryDelay:300}); }
})().catch(e=>{console.error(e);process.exitCode=1;});
