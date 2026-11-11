using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Web;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
internal sealed class EmojiPicker : Form
{
 private readonly WebView2 view=new WebView2 {Dock=DockStyle.Fill,DefaultBackgroundColor=Visual.Canvas};
 private readonly List<Dictionary<string,object>> items;
 private readonly List<MemoryStream> streams=new List<MemoryStream>();
 private string documentUri="";
 private readonly Timer timeout=new Timer {Interval=12000};
 internal Dictionary<string,object> SelectedEmoji;
 internal readonly List<string> Diagnostics=new List<string>();
 internal EmojiPicker(List<Dictionary<string,object>> values,CoreWebView2Environment environment,string title,bool autoTest=false,Func<Dictionary<string,object>,bool> onInsert=null)
 {
  if(autoTest){Opacity=0;ShowInTaskbar=false;timeout.Tick+=delegate{Diagnostics.Add("timeout");timeout.Stop();Close();};timeout.Start();}
  items=values;Text=title;ClientSize=new Size(680,520);MinimumSize=new Size(480,380);StartPosition=FormStartPosition.CenterParent;BackColor=Visual.Canvas;
  AutoScaleDimensions=new SizeF(96,96);AutoScaleMode=AutoScaleMode.Dpi;Controls.Add(view);
  Shown+=async delegate {Diagnostics.Add("shown");
   try{
    await view.EnsureCoreWebView2Async(environment);Diagnostics.Add("initialized");view.CoreWebView2.Settings.AreHostObjectsAllowed=false;view.CoreWebView2.Settings.AreDevToolsEnabled=false;
    if(autoTest){view.CoreWebView2.AddWebResourceRequestedFilter("https://*",CoreWebView2WebResourceContext.All);view.CoreWebView2.WebResourceRequested+=delegate(object sender,CoreWebView2WebResourceRequestedEventArgs e){var stream=new MemoryStream(Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg' width='64' height='64'><circle cx='32' cy='32' r='28' fill='#ef4869'/></svg>"));streams.Add(stream);e.Response=environment.CreateWebResourceResponse(stream,200,"OK","Content-Type: image/svg+xml");};}
    view.CoreWebView2.NavigationStarting+=delegate(object s,CoreWebView2NavigationStartingEventArgs e){Diagnostics.Add("nav:"+(e.Uri.Length>80?e.Uri.Substring(0,80):e.Uri));if(e.Uri!="about:blank"&&e.Uri!=documentUri)e.Cancel=true;};
    view.CoreWebView2.NewWindowRequested+=delegate(object s,CoreWebView2NewWindowRequestedEventArgs e){e.Handled=true;};
    view.CoreWebView2.PermissionRequested+=delegate(object s,CoreWebView2PermissionRequestedEventArgs e){e.State=CoreWebView2PermissionState.Deny;};
    view.CoreWebView2.WebMessageReceived+=delegate(object sender,CoreWebView2WebMessageReceivedEventArgs e){
        if(e.Source!="about:blank"&&e.Source!=documentUri)return;
        string value=e.TryGetWebMessageAsString();int index;
        if(value=="done"){BeginInvoke(new Action(delegate{DialogResult=DialogResult.OK;Close();}));return;}
        if(!Int32.TryParse(value,out index)||index<0||index>=items.Count)return;
        Dictionary<string,object> chosen=items[index];
        BeginInvoke(new Action(async delegate{
            if(IsDisposed)return;
            if(onInsert!=null){
                bool inserted=onInsert(chosen);
                try{if(!view.IsDisposed&&view.CoreWebView2!=null)await view.CoreWebView2.ExecuteScriptAsync("document.getElementById('feedback').textContent="+Json.Encode(inserted?"已插入 "+Json.Str(chosen,"token")+"，可以继续点选":"输入框达到长度上限，未插入")+";");}catch(ObjectDisposedException){}catch(InvalidOperationException){}
            }else{SelectedEmoji=chosen;DialogResult=DialogResult.OK;Close();}
        }));
    };
    StringBuilder html=new StringBuilder("<!doctype html><meta charset='utf-8'><meta name='referrer' content='no-referrer'><style>body{margin:0;padding:20px 20px 76px;background:#0e121b;color:#eaeff8;font:14px 'Microsoft YaHei UI'}.toolbar{position:fixed;bottom:0;left:0;right:0;background:#171d29;border-top:1px solid #2c3648;padding:12px 20px;display:flex;justify-content:space-between;align-items:center}.toolbar button{min-height:36px;padding:6px 22px;background:#ec4869}.toolbar span{font-size:12px;color:#9babc2}h3{margin:0 0 8px}p{color:#9babc2;margin:0 0 18px}.grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(96px,1fr));gap:10px}button{border:1px solid #2c3648;border-radius:12px;background:#171d29;color:#eaeff8;padding:10px;cursor:pointer;min-height:106px}button:hover,button:focus{border-color:#ec4869;background:#293044}img{width:64px;height:64px;object-fit:contain}span{display:block;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;margin-top:6px}</style><h3>");
    html.Append(HttpUtility.HtmlEncode(title)).Append("</h3><p>").Append(onInsert!=null?"每次点选都会在下方消息框插入 [名称]；可重复选择，选好点完成。":"点击缩略图设置待发送表情包，不会给当前聊天发送。").Append("</p><div class='grid'>");
    for(int i=0;i<items.Count;i++){Uri uri;string src=Json.Str(items[i],"src");if(!Uri.TryCreate(src,UriKind.Absolute,out uri)||uri.Scheme!="https")continue;html.Append("<button data-index='").Append(i).Append("'><img draggable='false' src='").Append(HttpUtility.HtmlAttributeEncode(src)).Append("'><span>").Append(HttpUtility.HtmlEncode(Json.Str(items[i],"name"))).Append("</span></button>");}
    html.Append("</div><div class='toolbar'><span id='feedback'>选择不会发送消息</span><button id='done'>完成</button></div><script>document.addEventListener('click',e=>{if(e.target.closest('#done')){chrome.webview.postMessage('done');return;}const b=e.target.closest('button[data-index]');if(b)chrome.webview.postMessage(b.dataset.index);});</script>");
    if(autoTest)view.CoreWebView2.DOMContentLoaded+=async delegate {
        Diagnostics.Add("dom");
        string clicks=onInsert==null?"document.querySelector('button[data-index]').click();":"[0,0,1].forEach(i=>document.querySelector('button[data-index=\"'+i+'\"]').click());setTimeout(()=>document.querySelector('#done').click(),300);";
        await view.CoreWebView2.ExecuteScriptAsync(clicks);
    };
    documentUri="data:text/html;charset=utf-8;base64,"+Convert.ToBase64String(Encoding.UTF8.GetBytes(html.ToString()));view.NavigateToString(html.ToString());

   }catch(Exception ex){Diagnostics.Add(ex.ToString());if(!autoTest)MessageBox.Show(this,ex.Message,"表情预览无法打开");Close();}
  };
 }
 protected override void Dispose(bool disposing){if(disposing){timeout.Dispose();view.Dispose();foreach(var stream in streams)stream.Dispose();}base.Dispose(disposing);}
}
