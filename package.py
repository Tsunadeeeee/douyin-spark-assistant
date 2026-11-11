"""Package V1 from explicit allowlists; never reads account or browser directories."""
from pathlib import Path
import argparse,datetime,hashlib,json,shutil,zipfile
root=Path(__file__).resolve().parent
parser=argparse.ArgumentParser();parser.add_argument('--output',type=Path,default=root/'dist');args=parser.parse_args()
destination=args.output.resolve();destination.mkdir(parents=True,exist_ok=True)
out=destination/'火花助手桌面版';out.mkdir(exist_ok=True)
engine=json.loads((root/'engine-test-results.json').read_text(encoding='utf-8'))
native=json.loads((root/'native-selftest.json').read_text(encoding='utf-8-sig'))
assert engine['version']=='1.0.0' and engine['passed']==engine['total'] and engine['total']>=81
assert native['ok'] and len(native['checks'])>=44
assert (root/'engine-test-results.json').stat().st_mtime>=(root/'engine.js').stat().st_mtime,'Rerun engine tests'
assert (root/'native-selftest.json').stat().st_mtime>=(root/'release/火花助手桌面版/火花助手.exe').stat().st_mtime,'Rerun native tests'
binaries=['火花助手.exe','火花助手.exe.config','Microsoft.Web.WebView2.Core.dll','Microsoft.Web.WebView2.WinForms.dll','WebView2Loader.dll','LICENSE.txt']
allowed=set(binaries+['开始使用.txt','软件界面演示.png','测试记录.json','SHA256.txt'])
unexpected=[p.name for p in out.iterdir() if not p.is_file() or p.name not in allowed]
assert not unexpected,'Unexpected files in release target: '+repr(unexpected)
for name in binaries:shutil.copy2(root/'release/火花助手桌面版'/name,out/name)
for name in ['开始使用.txt','软件界面演示.png']:shutil.copy2(root/name,out/name)
results={'version':'V1','assembly_version':'1.0.0.0','build_date':datetime.date.today().isoformat(),
 'platform':'Windows x64 / .NET Framework 4.8 / WebView2',
 'sdk':json.loads((root/'sdk-source.json').read_text(encoding='utf-8')),
 'desktop_bridge':engine,'native_executable':native,
 'capabilities':{'douyin':['text','builtin_emoji_tokens','repeat_emoji_insertion','persistent_builtin_emoji_cache','append_only_emoji_catalog','favorite_sticker','scan','manual_selection','optional_daily_deduplication','batch_completion_log','smooth_pixel_scrolling','expanded_log_panel']},
 'privacy':{'packaging':'Explicit file allowlist only','login_profiles_included':False,'personal_settings_included':False,'runtime_emoji_cache_included':False,'screenshot':'Synthetic isolated demo only'},
 'limitations':['No authenticated Douyin account validation','No delivery or streak verification']}
(out/'测试记录.json').write_text(json.dumps(results,ensure_ascii=False,indent=2),encoding='utf-8')
hashes=[hashlib.sha256((out/name).read_bytes()).hexdigest()+'  '+name for name in sorted(allowed-{'SHA256.txt'})]
(out/'SHA256.txt').write_text('\n'.join(hashes)+'\n',encoding='utf-8')
binary_zip=destination/'火花助手桌面版-Windows-x64.zip'
with zipfile.ZipFile(binary_zip,'w',zipfile.ZIP_DEFLATED,compresslevel=9) as archive:
 for name in sorted(allowed):archive.write(out/name,arcname='火花助手桌面版/'+name)
with zipfile.ZipFile(binary_zip) as archive:assert archive.testzip() is None and len(archive.namelist())==len(allowed)
source_names=['SparkDesk.cs','ModernControls.cs','EmojiPicker.cs','BuiltinEmojiCache.cs','engine.js','build.py','bootstrap.py','package.py','test_engine.cjs','desktop-fixture.html','demo.html','spark.ico','sdk-source.json','BUILD_NOTES.md','README.md','.gitignore','package.json','requirements.txt','开始使用.txt','RELEASE_NOTES.md']
source_zip=destination/'火花助手桌面版-V1-源码.zip'
with zipfile.ZipFile(source_zip,'w',zipfile.ZIP_DEFLATED,compresslevel=9) as archive:
 for name in source_names:archive.write(root/name,arcname='douyin-spark-assistant/'+name)
 archive.write(root/'软件界面演示.png',arcname='douyin-spark-assistant/docs/screenshot.png')
with zipfile.ZipFile(source_zip) as archive:assert archive.testzip() is None and len(archive.namelist())==len(source_names)+1
for path in [binary_zip,source_zip]:print(path,'SHA256',hashlib.sha256(path.read_bytes()).hexdigest())
