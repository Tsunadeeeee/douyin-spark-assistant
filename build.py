from pathlib import Path
import shutil,subprocess,json
from PIL import Image,ImageDraw

root=Path(__file__).resolve().parent
out=root/'release'/'火花助手桌面版'
out.mkdir(parents=True,exist_ok=True)
# engine.js is the maintained desktop source, independent of archived userscripts.
subprocess.run(['node','--check',str(root/'engine.js')],check=True)
icon=Image.new('RGBA',(256,256),(0,0,0,0));d=ImageDraw.Draw(icon)
d.rounded_rectangle((8,8,248,248),radius=54,fill='#171d2b')
d.polygon([(144,36),(165,95),(199,129),(201,168),(177,208),(128,226),(78,209),(55,169),(65,126),(97,89),(98,140),(126,113)],fill='#ed3658')
d.polygon([(139,122),(156,160),(152,187),(130,199),(110,188),(105,166)],fill='#ffdb9a')
icon.save(root/'spark.ico',sizes=[(16,16),(24,24),(32,32),(48,48),(64,64),(128,128),(256,256)])
fixture=(root/'desktop-fixture.html').read_text(encoding='utf-8')
people=[{'name':'阿青','state':'active','when':'昨天 20:00'},
 {'name':'阿白','state':'rekindling','when':'2025/01/01'},
 {'name':'普通好友','state':'none','when':'刚刚','mutual':False},
 {'name':'灰色火花','state':'gray','when':'11/05'},
 {'name':'即将消失的好友','state':'expiring','when':'前天'},
 {'name':'尚未点燃的好友','state':'unlit','when':'未知时间'},
 {'name':'群聊','state':'active','group':True}]
fixture=fixture.replace('__PEOPLE__',json.dumps(people,ensure_ascii=False)).replace('__OPTIONS__','{}')
fixture=fixture.replace('width:550px;height:600px','width:calc(100% - 305px);height:calc(100vh - 85px)').replace('width:420px;min-height:60px','width:calc(100% - 45px);min-height:60px')
fixture=fixture.replace('</style>', '*{box-sizing:border-box}body{overflow:hidden}#scroller{width:280px;height:calc(100vh - 90px);scrollbar-width:thin;scrollbar-color:#424b61 #141925}.conversationConversationItemwrapper{height:86px;padding:14px 12px;position:relative;border:0;border-bottom:1px solid #293144;gap:10px}.conversationConversationItemwrapper>div{min-width:0;flex:1}.conversationConversationItemtitleWrapper{gap:6px}.conversationConversationItemtitle{max-width:125px;overflow:hidden;white-space:nowrap;text-overflow:ellipsis}.ConversationItemTagNextToTitletimeStr{position:absolute;right:12px;bottom:10px;font-size:11px;color:#91a0ba}.conversationConversationItemwrapper small{font-size:11px;color:#a7b2c8;display:block;margin-top:8px;max-width:175px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}.commonStreakstreakContainer{white-space:nowrap;font-size:10px}.componentsRightPanelwrapper{left:310px;width:calc(100% - 325px);height:calc(100vh - 90px);border-radius:10px}.messageEditorinputArea{width:calc(100% - 36px);border-radius:8px}.messageMsgInputpublishBtn{border:0;border-radius:6px;color:white}'+'</style>')
(root/'demo.html').write_text(fixture,encoding='utf-8')
lib=root/'deps/webview2/lib/net462'
for name in ['Microsoft.Web.WebView2.Core.dll','Microsoft.Web.WebView2.WinForms.dll']:
 shutil.copy2(lib/name,out/name)
shutil.copy2(root/'deps/webview2/runtimes/win-x64/native/WebView2Loader.dll',out/'WebView2Loader.dll')
compiler=Path('C:/Windows/Microsoft.NET/Framework64/v4.0.30319/csc.exe')
args=[str(compiler),'/nologo','/target:winexe','/platform:x64','/optimize+','/utf8output','/out:'+str(out/'火花助手.exe'),'/win32icon:'+str(root/'spark.ico')]
for name in ['System.dll','System.Core.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Web.Extensions.dll','System.Web.dll','System.Security.dll']:
 args.append('/reference:'+name)
for name in ['Microsoft.Web.WebView2.Core.dll','Microsoft.Web.WebView2.WinForms.dll']:
 args.append('/reference:'+str(lib/name))
for name in ['engine.js','spark.ico','demo.html']:
 args.append('/resource:'+str(root/name)+','+name)
args.append(str(root/'SparkDesk.cs'))
args.append(str(root/'ModernControls.cs'))
args.append(str(root/'EmojiPicker.cs'))
args.append(str(root/'BuiltinEmojiCache.cs'))
subprocess.run(args,check=True)
(out/'火花助手.exe.config').write_text('<?xml version="1.0" encoding="utf-8"?><configuration><startup useLegacyV2RuntimeActivationPolicy="true"><supportedRuntime version="v4.0" sku=".NETFramework,Version=v4.8"/></startup><runtime><loadFromRemoteSources enabled="false"/></runtime></configuration>',encoding='utf-8')
for license_path in (root/'deps/webview2').glob('*LICENSE*'):
 shutil.copy2(license_path,out/license_path.name)
print('Built:',out/'火花助手.exe')
