"""Fetch pinned Microsoft WebView2 SDK; never reads user profiles."""
from pathlib import Path
import hashlib,json,shutil,urllib.request,zipfile
root=Path(__file__).resolve().parent
meta=json.loads((root/'sdk-source.json').read_text(encoding='utf-8'))
deps=root/'deps';deps.mkdir(exist_ok=True)
package=deps/'webview2.nupkg'
if not package.exists():
 temporary=deps/'webview2.nupkg.download'
 with urllib.request.urlopen(meta['source'],timeout=120) as response,temporary.open('wb') as target:
  shutil.copyfileobj(response,target)
 if hashlib.sha256(temporary.read_bytes()).hexdigest()!=meta['sha256']:
  temporary.unlink();raise RuntimeError('WebView2 SDK checksum mismatch')
 temporary.replace(package)
assert hashlib.sha256(package.read_bytes()).hexdigest()==meta['sha256'],'WebView2 SDK checksum mismatch'
destination=(deps/'webview2').resolve()
with zipfile.ZipFile(package) as archive:
 for name in archive.namelist():
  if not (destination/name).resolve().is_relative_to(destination):raise RuntimeError('Unexpected archive path')
 archive.extractall(destination)
print('Verified official WebView2 SDK:',meta['version'])
