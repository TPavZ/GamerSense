"""Publish existing GamerSense ZIPs. Credentials stay in memory; no force pushes."""
from pathlib import Path,PurePosixPath
import argparse,hashlib,json,os,re,shutil,subprocess,time,urllib.request,urllib.error,urllib.parse,zipfile

REPOSITORY='TPavZ/GamerSense'
GIT=shutil.which('git') or os.environ.get('GAMERSENSE_GIT')
if not GIT:
    bundled=Path.home()/'.cache/codex-runtimes/codex-primary-runtime/dependencies/native/git/cmd/git.exe'
    if bundled.is_file():GIT=str(bundled)
if GIT:os.environ['PATH']=str(Path(GIT).parent)+os.pathsep+os.environ.get('PATH','')
def sha256(path):return hashlib.sha256(path.read_bytes()).hexdigest()
def git_command(repo,*args,check=True):
    result=subprocess.run([GIT,'-c','safe.directory='+str(repo),'-C',str(repo),*args],text=True,capture_output=True)
    if check and result.returncode:raise RuntimeError(result.stderr.strip())
    return result
def credential():
    token=os.environ.get('GH_TOKEN') or os.environ.get('GITHUB_TOKEN')
    if token:return token
    if shutil.which('gh'):
        result=subprocess.run(['gh','auth','token','--hostname','github.com'],text=True,capture_output=True)
        if result.returncode==0 and result.stdout.strip():return result.stdout.strip()
    env=os.environ.copy();env.update(GIT_TERMINAL_PROMPT='0',GCM_INTERACTIVE='never')
    result=subprocess.run([GIT,'credential','fill'],input='protocol=https\nhost=github.com\n\n',text=True,capture_output=True,env=env)
    fields=dict(line.split('=',1) for line in result.stdout.splitlines() if '=' in line) if result.returncode==0 else {}
    if not fields.get('password'):raise RuntimeError('A local GitHub sign-in is required. Use gh auth login or git credential-manager github login --device. Never paste a token into chat.')
    return fields['password']
class GitHub:
    def __init__(self,token):self.token=token;self.last_write=0
    def request(self,method,path,payload=None,content_type=None):
        url=path if path.startswith('https://') else 'https://api.github.com'+path
        if urllib.parse.urlsplit(url).hostname not in ('api.github.com','uploads.github.com'):raise ValueError('Unexpected GitHub API host')
        if method!='GET':
            time.sleep(max(0,1.1-(time.monotonic()-self.last_write)));self.last_write=time.monotonic()
        data=payload if isinstance(payload,bytes) else json.dumps(payload).encode() if payload is not None else None
        headers={'Authorization':'Bearer '+self.token,'User-Agent':'GamerSense-release-publisher','Accept':'application/vnd.github+json','X-GitHub-Api-Version':'2026-03-10'}
        if data is not None:headers['Content-Type']=content_type or 'application/json'
        try:
            with urllib.request.urlopen(urllib.request.Request(url,data=data,headers=headers,method=method),timeout=60) as response:return json.load(response)
        except urllib.error.HTTPError as exc:
            if method=='GET' and exc.code==404:return None
            raise RuntimeError(f'GitHub {method} request failed with HTTP {exc.code}; existing tags/assets were not overwritten.') from None
def build_new_entry(repo,assets,version):
    if not re.fullmatch(r'\d+\.\d+\.\d+',version):raise ValueError('Use a numeric version, for example 0.4.21')
    paths=[assets/f'GamerSense-v{version}-app.zip',assets/f'GamerSense-v{version}-source.zip']
    if not all(p.is_file() for p in paths):raise FileNotFoundError('Both existing App and Source ZIPs are required. Build and test them before publishing.')
    if git_command(repo,'status','--porcelain').stdout.strip():raise RuntimeError('Commit or resolve local checkout changes before publishing.')
    tag='v'+version
    existing=git_command(repo,'tag','--list',tag).stdout.strip()
    with zipfile.ZipFile(paths[0]) as app:
        if app.testzip() is not None:raise ValueError('App ZIP failed its CRC check')
        dependencies=json.loads(app.read('GamerSense/GamerSense.deps.json'))
        if 'GamerSense/'+version not in dependencies.get('libraries',{}):raise ValueError('App ZIP version does not match requested release')
    with zipfile.ZipFile(paths[1]) as source:
        if source.testzip() is not None:raise ValueError('Source ZIP failed its CRC check')
        if 'GamerSense/BUILD-DEBUG.bat' not in source.namelist():raise ValueError('Source ZIP must retain BUILD-DEBUG.bat')
        project=source.read('GamerSense/src/GamerSense/GamerSense.csproj').decode('utf-8-sig')
        if f'<Version>{version}</Version>' not in project:raise ValueError('Source project version does not match requested release')
        if existing:
            for entry in source.infolist():
                parts=PurePosixPath(entry.filename).parts
                if not parts or parts[0]!='GamerSense' or '..' in parts:raise ValueError('Unsafe source ZIP path')
                name='/'.join(parts[1:])
                if entry.is_dir() or any(p in ('bin','obj','.git') for p in parts) or name in ('AGENTS.md','.gitignore','README.md') or name.startswith(('scripts/','docs/')):continue
                expected=subprocess.run([GIT,'hash-object','--stdin'],input=source.read(entry),capture_output=True,check=True).stdout.decode().strip()
                actual=git_command(repo,'rev-parse',tag+':'+name,check=False)
                if actual.returncode or actual.stdout.strip()!=expected:raise ValueError('Existing tag differs from Source ZIP: '+name)
    if not existing:
        files={}
        with zipfile.ZipFile(paths[1]) as z:
            assert z.testzip() is None
            for entry in z.infolist():
                if entry.is_dir():continue
                parts=PurePosixPath(entry.filename).parts
                if not parts or parts[0]!='GamerSense' or '..' in parts:raise ValueError('Unsafe source ZIP path')
                parts=parts[1:]
                if any(p in ('bin','obj','.git') for p in parts):continue
                files['/'.join(parts)]=z.read(entry)
        if f'<Version>{version}</Version>' not in files['src/GamerSense/GamerSense.csproj'].decode('utf-8-sig'):raise ValueError('Source project version does not match requested release')
        # Keep repository publishing instructions/tools while importing app source.
        keep=lambda p:p in ('AGENTS.md','.gitignore','README.md') or p.startswith(('scripts/','docs/'))
        for name in git_command(repo,'ls-files','-z').stdout.split('\0'):
            if not name or keep(name):continue
            p=(repo/name).resolve()
            if not p.is_relative_to(repo) or '.git' in p.relative_to(repo).parts:raise ValueError('Checkout path escaped repository')
            if p.is_file():p.unlink()
        for name,data in files.items():
            if keep(name):continue
            p=(repo/name).resolve()
            if not p.is_relative_to(repo):raise ValueError('Source path escaped repository')
            p.parent.mkdir(parents=True,exist_ok=True);p.write_bytes(data)
        git_command(repo,'config','core.autocrlf','false');git_command(repo,'add','--all')
        git_command(repo,'commit','-m',f'Release GamerSense {tag}')
        git_command(repo,'tag','-a',tag,'-m',f'GamerSense {tag}; source ZIP SHA-256 {sha256(paths[1])}')
    commit=git_command(repo,'rev-parse',tag+'^{commit}').stdout.strip()
    guide=assets/f'GamerSense-v{version}-guide.txt'
    if guide.is_file():paths.append(guide)
    checkdir=assets/'.github-release-checksums'/tag;checkdir.mkdir(parents=True,exist_ok=True)
    checksum=checkdir/'SHA256SUMS.txt';checksum.write_text(''.join(sha256(p)+'  '+p.name+'\n' for p in paths),encoding='ascii');paths.append(checksum)
    return {'version':version,'tag':tag,'commit':commit,'summary':'Development build. See the included guide for changes, limitations and testing instructions.','assets':[{'path':str(p.resolve()),'name':p.name,'bytes':p.stat().st_size,'sha256':sha256(p)} for p in paths]}
def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--repo',type=Path,default=Path(__file__).resolve().parents[1])
    parser.add_argument('--manifest',type=Path);parser.add_argument('--base',type=Path,default=Path.cwd())
    parser.add_argument('--version');parser.add_argument('--assets-dir',type=Path)
    parser.add_argument('--check-only',action='store_true');parser.add_argument('--result',type=Path)
    args=parser.parse_args();repo=args.repo.resolve()
    if not GIT:raise RuntimeError('Install Git or set GAMERSENSE_GIT to its executable path')
    remote=git_command(repo,'remote','get-url','origin').stdout.strip().lower().removesuffix('.git')
    if remote!='https://github.com/tpavz/gamersense':raise RuntimeError('This publisher is restricted to TPavZ/GamerSense')
    if bool(args.manifest)==bool(args.version):raise ValueError('Use either --manifest or --version, not both')
    if args.manifest:entries=json.loads(args.manifest.read_text(encoding='utf-8'))['versions']
    else:
        if args.assets_dir is None:raise ValueError('--assets-dir is required for a new version')
        entries=[build_new_entry(repo,args.assets_dir.resolve(),args.version)]
    for entry in entries:
        actual=git_command(repo,'rev-parse',entry['tag']+'^{commit}').stdout.strip()
        if actual!=entry['commit']:raise ValueError('Local tag has changed: '+entry['tag'])
        for asset in entry['assets']:
            p=(args.base/asset['path']).resolve();asset['resolvedPath']=p
            if p.stat().st_size!=asset['bytes'] or sha256(p)!=asset['sha256']:raise ValueError('Asset changed: '+asset['name'])
    if args.check_only:
        print(json.dumps({'localVersionsChecked':len(entries),'assetsChecked':sum(len(e['assets']) for e in entries),'remoteWrites':False},indent=2));return
    api=GitHub(credential());identity=api.request('GET','/user');metadata=api.request('GET','/repos/'+REPOSITORY)
    if identity['login'].lower()!='tpavz' or not metadata.get('permissions',{}).get('push'):raise RuntimeError('Sign into the TPavZ account with repository write access')
    git_command(repo,'fetch','origin','main')
    if git_command(repo,'merge-base','--is-ancestor','origin/main','HEAD',check=False).returncode:raise RuntimeError('Remote main has changes outside this history; reconcile them first. No force push will be performed.')
    git_command(repo,'push','--atomic','origin','HEAD:refs/heads/main',*['refs/tags/'+e['tag']+':refs/tags/'+e['tag'] for e in entries])
    published=[]
    for entry in entries:
        prefix='/repos/'+REPOSITORY+'/releases'
        release=api.request('GET',prefix+'/tags/'+entry['tag'])
        if release is None:
            release=api.request('POST',prefix,{'tag_name':entry['tag'],'target_commitish':entry['commit'],'name':'GamerSense '+entry['tag'],'body':entry['summary']+'\n\nDownload the App ZIP to run GamerSense, or Source ZIP to build it. Extract the entire ZIP into a new folder. SHA256SUMS.txt verifies the exact attached files.\n\nRecovered historical versions retain their source snapshots; archive imports do not invent original commit dates.','draft':True,'prerelease':False})
        existing={a['name']:a for a in api.request('GET',prefix+'/'+str(release['id'])+'/assets?per_page=100')}
        for asset in entry['assets']:
            uploaded=existing.get(asset['name'])
            if uploaded is None:
                url=release['upload_url'].split('{')[0]+'?'+urllib.parse.urlencode({'name':asset['name']})
                uploaded=api.request('POST',url,asset['resolvedPath'].read_bytes(),'application/zip' if asset['name'].endswith('.zip') else 'text/plain')
            if uploaded['size']!=asset['bytes'] or uploaded.get('digest')!='sha256:'+asset['sha256']:
                raise ValueError('GitHub upload hash/size mismatch: '+asset['name']+'; no existing asset was overwritten.')
        release=api.request('PATCH',prefix+'/'+str(release['id']),{'draft':False,'make_latest':'true' if entry is entries[-1] else 'false'})
        published.append({'version':entry['version'],'commit':entry['commit'],'url':release['html_url'],'assetsVerified':len(entry['assets'])})
        if args.result:args.result.parent.mkdir(parents=True,exist_ok=True);args.result.write_text(json.dumps(published,indent=2),encoding='utf-8')
        print('Published '+entry['tag']+'; all asset SHA-256 values verified',flush=True)
    print(json.dumps({'versionsPublished':len(published),'latest':published[-1]['url']},indent=2))
if __name__=='__main__':main()
