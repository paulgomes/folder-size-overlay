#!/usr/bin/env python3
"""Compile/run a platform-independent C# logic harness with the isolated SDK."""
import argparse,json,os,pathlib,re,subprocess,sys
root=pathlib.Path(__file__).resolve().parent
p=argparse.ArgumentParser();p.add_argument('--out',required=True);p.add_argument('--run',action='store_true');p.add_argument('--define',action='append',default=[]);p.add_argument('sources',nargs='+');a=p.parse_args()
def latest_version(directory, description):
    candidates=[path for path in directory.glob('8.0.*') if path.is_dir()]
    if not candidates:
        sys.exit('Missing .NET 8 '+description+' in '+str(directory))
    def key(path):
        version, _, prerelease=path.name.partition('-')
        return tuple(int(part) for part in re.findall(r'\d+',version)), not bool(prerelease), prerelease
    return max(candidates,key=key)
ref=latest_version(root/'dotnet/packs/Microsoft.NETCore.App.Ref','reference pack')/'ref/net8.0'
csc=latest_version(root/'dotnet/sdk','SDK')/'Roslyn/bincore/csc.dll'
runtime=latest_version(root/'dotnet/shared/Microsoft.NETCore.App','runtime')
if not ref.is_dir() or not csc.is_file():
    sys.exit('The selected .NET 8 SDK/reference pack is incomplete.')
args=[str(root/'dotnet/dotnet'),str(csc),'-nologo','-noconfig','-nostdlib+','-langversion:5','-utf8output','-optimize+','-target:exe','-out:'+a.out]
args+=['-r:'+str(path) for path in sorted(ref.glob('*.dll'))]
args+=['-define:'+value for value in a.define]
args+=a.sources
out=pathlib.Path(a.out).resolve();out.parent.mkdir(parents=True,exist_ok=True)
env=dict(os.environ,DOTNET_CLI_TELEMETRY_OPTOUT='1',DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1',DOTNET_NOLOGO='1')
code=subprocess.run(args,env=env).returncode
if code:sys.exit(code)
version=runtime.name
out.with_suffix('.runtimeconfig.json').write_text(json.dumps({'runtimeOptions':{'tfm':'net8.0','framework':{'name':'Microsoft.NETCore.App','version':version}}},indent=2))
if a.run: sys.exit(subprocess.run([str(root/'dotnet/dotnet'),str(out)],env=env).returncode)
