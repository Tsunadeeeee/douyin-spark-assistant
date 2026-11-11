using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

[assembly: AssemblyTitle("火花助手桌面版")]
[assembly: AssemblyDescription("Windows desktop companion with embedded official login and explicit message actions")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

internal static class Program
{
    [DllImport("user32.dll")] private static extern bool SetProcessDPIAware();
    [STAThread] private static void Main(string[] args)
    {
        SetProcessDPIAware();
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        bool test = Array.IndexOf(args, "--self-test") >= 0;
        bool demo = test || Array.IndexOf(args, "--demo") >= 0;
        string report = null;
        for (int i = 0; i < args.Length - 1; i++) if (args[i] == "--report") report = Path.GetFullPath(args[i + 1]);
        bool created;
        using (Mutex mutex = new Mutex(true, demo ? "Local\\SparkDeskDemo-" + Process.GetCurrentProcess().Id : "Local\\SparkDeskApp-v1", out created))
        {
            if (!created) { MessageBox.Show("软件已在运行，请使用已打开的火花助手窗口。", "火花助手"); return; }
            try { Application.Run(new MainForm(demo, test, report)); }
            catch (Exception ex)
            {
                if (test && report != null) File.WriteAllText(report, "{\"ok\":false,\"fatal\":" + Json.Encode(ex.ToString()) + "}", Encoding.UTF8);
                else MessageBox.Show(ex.Message, "启动失败");
                Environment.ExitCode = 1;
            }
            finally { mutex.ReleaseMutex(); }
        }
    }
}

internal static class Json
{
    private static JavaScriptSerializer Serializer() { return new JavaScriptSerializer { MaxJsonLength = 2 * 1024 * 1024, RecursionLimit = 64 }; }
    internal static string Encode(object value) { return Serializer().Serialize(value); }
    internal static Dictionary<string, object> Object(string text) { return Serializer().Deserialize<Dictionary<string, object>>(text); }
    internal static string Str(Dictionary<string, object> value, string key)
    {
        object result; return value != null && value.TryGetValue(key, out result) && result != null ? Convert.ToString(result) : "";
    }
    internal static Dictionary<string, object> Map(Dictionary<string, object> value, string key)
    {
        object result; return value != null && value.TryGetValue(key, out result) ? result as Dictionary<string, object> : null;
    }
}

internal sealed class LocalState
{
    internal readonly string Folder;
    internal readonly string FilePath;
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SparkDesk.LocalState.v1");
    internal LocalState(string folder) { Folder = folder; Directory.CreateDirectory(folder); FilePath = Path.Combine(folder, "state.bin"); }
    internal static Dictionary<string, object> Defaults()
    {
        return new Dictionary<string, object> {
            {"config", new Dictionary<string, object> {{"message","续个火花 🔥"},{"includeActive",true},{"includeRekindling",true},{"includeDormant",true},{"includeExpiring",true},{"manualSelection",false},{"dailyDedup",true},{"intervalSeconds",15},{"maxRecipients",100}}},
            {"records",new Dictionary<string, object>()}
        };
    }
    internal Dictionary<string, object> Read()
    {
        if (!File.Exists(FilePath)) return Defaults();
        byte[] plain = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), Entropy, DataProtectionScope.CurrentUser);
        Dictionary<string, object> result = Json.Object(Encoding.UTF8.GetString(plain));
        if (Json.Map(result, "config") == null || Json.Map(result, "records") == null) throw new InvalidDataException("本地配置格式不正确，已停止自动发送，未重置发送记录。");
        return result;
    }
    internal void Save(Dictionary<string, object> state)
    {
        if (Json.Map(state, "config") == null || Json.Map(state, "records") == null) throw new InvalidDataException("保存数据格式不正确");
        string json = Json.Encode(state);
        if (json.Length > 1024 * 1024) throw new InvalidDataException("本地记录过大，未继续保存");
        byte[] encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes(json), Entropy, DataProtectionScope.CurrentUser);
        string temp = FilePath + ".tmp";
        using (FileStream file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None)) { file.Write(encrypted, 0, encrypted.Length); file.Flush(true); }
        if (File.Exists(FilePath)) File.Replace(temp, FilePath, FilePath + ".bak"); else File.Move(temp, FilePath);
        Read();
    }
}

internal sealed class MainForm : Form
{
    private readonly bool demo, selfTest;
    private readonly string reportPath;
    private readonly LocalState storage;
    private CoreWebView2Environment currentEnvironment;
    private bool automationAvailable;
    private Panel browserHost;
    private Button loadEmojiButton,chooseEmojiButton;
    private ComboBox contentType;
    private Label emojiLabel,platformTitle,footer;
    private Dictionary<string,object> selectedEmoji;
    private readonly Dictionary<string,List<Dictionary<string,object>>> emojiCatalogs=new Dictionary<string,List<Dictionary<string,object>>>();
    private Dictionary<string, object> state;
    private WebView2 browser;
    private TextBox message, logBox;
    private CheckBox active, recovery, dormant, expiring, manual, dailyDedup;
    private readonly ToolTip tips = new ToolTip();
    private NumericUpDown interval, limit;
    private Button scanButton, sendButton, stopButton, homeButton, refreshButton, saveButton, selectAllButton, clearSelectionButton;
    private RecipientList recipients;
    private Label status, address, engineLabel, listLabel;
    private bool ready, busy, selfTestStarted, renderingRecipients;
    private string session = "", lastStatus = "", lastRunMode = "";
    private readonly HashSet<string> excluded = new HashSet<string>();
    private readonly HashSet<string> selected = new HashSet<string>();
    private readonly Dictionary<string,bool> selectionOverrides = new Dictionary<string,bool>();
    private readonly List<string> logLines = new List<string>();
    private readonly List<MemoryStream> testResponses = new List<MemoryStream>();
    private readonly Color background = Color.FromArgb(16, 19, 28), panel = Color.FromArgb(26, 31, 44), ink = Color.FromArgb(237, 241, 249);
    private readonly Color muted = Color.FromArgb(164, 176, 197), accent = Color.FromArgb(232, 53, 85);
    private TableLayoutPanel rootLayout;

    internal MainForm(bool isDemo, bool isTest, string output)
    {
        SuspendLayout();
        demo = isDemo; selfTest = isTest; reportPath = output;
        string folder = demo ? Path.Combine(Path.GetTempPath(), "SparkDeskDemo-" + Guid.NewGuid().ToString("N")) : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SparkDesk");
        storage = new LocalState(folder); state = storage.Read();
        Text = "火花助手 · V1" + (demo ? " — 本地模拟演示" : "");
        ClientSize = new Size(1440, 980); MinimumSize = new Size(1180, 940);
        StartPosition = FormStartPosition.CenterScreen; BackColor = background; ForeColor = ink;
        Font = new Font("Microsoft YaHei UI", 9F);
        using (Stream icon = Assembly.GetExecutingAssembly().GetManifestResourceStream("spark.ico")) if (icon != null) Icon = new Icon(icon);
        KeyPreview = true;
        if (selfTest) { ShowInTaskbar = false; Opacity = 0; }
        BuildUI(); ApplyConfig();
        AutoScaleDimensions = new SizeF(96F,96F); AutoScaleMode = AutoScaleMode.Dpi;
        ResumeLayout(true);
        Shown += async delegate { await InitializeBrowser(); };
        FormClosing += delegate { if (browser != null && browser.CoreWebView2 != null) browser.CoreWebView2.Stop(); };
        KeyDown += async delegate(object sender, KeyEventArgs e) { if (e.KeyCode == Keys.Escape) { await StopTask(); e.Handled = true; } };
        Resize += async delegate { if (WindowState == FormWindowState.Minimized && busy) await StopTask(); };
    }

    private Label LabelText(string text, float size, Color color, bool bold)
    {
        return new Label { Text = text, AutoSize = false, ForeColor = color, Font = new Font(Font.FontFamily, size, bold ? FontStyle.Bold : FontStyle.Regular), Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0) };
    }
    private Button ButtonText(string text, Color color)
    {
        Button result = new SoftButton { Text = text, BackColor = color, ForeColor = ink, FlatStyle = FlatStyle.Flat, Height = 36, Margin = new Padding(3), Cursor = Cursors.Hand };
        result.FlatAppearance.BorderSize = 0; return result;
    }
    private void BuildUI()
    {
        BackColor=Visual.Canvas;
        TableLayoutPanel root=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=1,RowCount=3,Padding=new Padding(18),BackColor=Visual.Canvas,Margin=Padding.Empty};
        rootLayout=root;root.RowStyles.Add(new RowStyle(SizeType.Absolute,64));root.RowStyles.Add(new RowStyle(SizeType.Percent,100));root.RowStyles.Add(new RowStyle(SizeType.Absolute,28));Controls.Add(root);
        TableLayoutPanel heading=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=2,Margin=Padding.Empty};heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));heading.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,440));
        TableLayoutPanel brand=new TableLayoutPanel {Dock=DockStyle.Fill,RowCount=1,Margin=Padding.Empty};brand.RowStyles.Add(new RowStyle(SizeType.Percent,100));
        brand.Controls.Add(LabelText("火花助手",22,Visual.Text,true),0,0);heading.Controls.Add(brand,0,0);
        Label edition=LabelText(demo?"V1  ·  本地模拟演示":"V1  ·  独立登录  /  本地保存",9,Visual.Muted,false);edition.TextAlign=ContentAlignment.MiddleRight;heading.Controls.Add(edition,1,0);root.Controls.Add(heading,0,0);
        TableLayoutPanel body=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=2,RowCount=1,Margin=new Padding(0,10,0,0)};body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,440));root.Controls.Add(body,0,1);
        SoftPanel webCard=new SoftPanel {Dock=DockStyle.Fill,Padding=new Padding(10),Margin=new Padding(0,0,14,0)};body.Controls.Add(webCard,0,0);
        TableLayoutPanel webLayout=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=1,RowCount=3,BackColor=Visual.Card,Margin=Padding.Empty};webLayout.RowStyles.Add(new RowStyle(SizeType.Absolute,42));webLayout.RowStyles.Add(new RowStyle(SizeType.Absolute,27));webLayout.RowStyles.Add(new RowStyle(SizeType.Percent,100));webCard.Controls.Add(webLayout);
        TableLayoutPanel navigation=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=3,Margin=Padding.Empty};navigation.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));navigation.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,124));navigation.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,66));
        platformTitle=LabelText("抖音会话",11,Visual.Text,true);navigation.Controls.Add(platformTitle,0,0);
        homeButton=ButtonText("登录 / 私信",Color.FromArgb(40,49,66));homeButton.Dock=DockStyle.Fill;
        refreshButton=ButtonText("刷新",Color.FromArgb(40,49,66));refreshButton.Dock=DockStyle.Fill;
        homeButton.Click+=delegate{NavigateChat();};refreshButton.Click+=delegate{if(!busy&&browser.CoreWebView2!=null)browser.Reload();};navigation.Controls.Add(homeButton,1,0);navigation.Controls.Add(refreshButton,2,0);webLayout.Controls.Add(navigation,0,0);
        address=LabelText("正在打开官方登录页面…",8,Visual.Muted,false);webLayout.Controls.Add(address,0,1);
        browserHost=new Panel {Dock=DockStyle.Fill,Margin=new Padding(0,5,0,0),BackColor=Visual.Input};browser=new WebView2 {Dock=DockStyle.Fill,DefaultBackgroundColor=Visual.Input};browserHost.Controls.Add(browser);webLayout.Controls.Add(browserHost,0,2);
        TableLayoutPanel sidebar=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=1,RowCount=3,Margin=Padding.Empty};sidebar.RowStyles.Add(new RowStyle(SizeType.Absolute,518));sidebar.RowStyles.Add(new RowStyle(SizeType.Percent,100));sidebar.RowStyles.Add(new RowStyle(SizeType.Absolute,66));body.Controls.Add(sidebar,1,0);
        SoftPanel setupCard=new SoftPanel {Dock=DockStyle.Fill,Margin=new Padding(0,0,0,12),Padding=new Padding(15,10,15,10)};sidebar.Controls.Add(setupCard,0,0);
        TableLayoutPanel setup=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=1,RowCount=10,BackColor=Visual.Card,Margin=Padding.Empty};float[] heights={28,32,80,68,54,35,48,44,40,36};
        for(int i=0;i<heights.Length;i++)setup.RowStyles.Add(new RowStyle(SizeType.Absolute,heights[i]));setupCard.Controls.Add(setup);
        TableLayoutPanel setupTitle=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=2,Margin=Padding.Empty};setupTitle.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,100));setupTitle.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));setupTitle.Controls.Add(LabelText("发送设置",11,Visual.Text,true),0,0);engineLabel=LabelText("先完成左侧登录",8,Visual.Muted,false);engineLabel.TextAlign=ContentAlignment.MiddleRight;setupTitle.Controls.Add(engineLabel,1,0);setup.Controls.Add(setupTitle,0,0);
        manual=new CheckBox {Text="自选好友  ·  扫描全部单聊",AutoSize=true,ForeColor=Color.FromArgb(242,184,150),Margin=new Padding(0,6,0,0)};setup.Controls.Add(manual,0,1);
        TableLayoutPanel composerOptions=new TableLayoutPanel {Dock=DockStyle.Fill,RowCount=2,ColumnCount=3,Margin=Padding.Empty};
        composerOptions.RowStyles.Add(new RowStyle(SizeType.Absolute,38));composerOptions.RowStyles.Add(new RowStyle(SizeType.Percent,100));composerOptions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));composerOptions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,98));composerOptions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,98));
        contentType=new SoftComboBox {Dock=DockStyle.Fill,DropDownStyle=ComboBoxStyle.DropDownList,BackColor=Visual.Input,ForeColor=Visual.Text,Margin=new Padding(0,6,6,0)};contentType.Items.AddRange(new object[]{"文字消息","文字 / 自带表情","收藏表情包"});contentType.SelectedIndex=0;
        loadEmojiButton=ButtonText("读取表情",Color.FromArgb(44,55,74));loadEmojiButton.Dock=DockStyle.Fill;chooseEmojiButton=ButtonText("选择表情",Color.FromArgb(44,55,74));chooseEmojiButton.Dock=DockStyle.Fill;
        emojiLabel=LabelText("可选官方自带表情，或账号收藏的表情包。",8,Visual.Muted,false);emojiLabel.AutoEllipsis=true;composerOptions.Controls.Add(contentType,0,0);composerOptions.Controls.Add(loadEmojiButton,1,0);composerOptions.Controls.Add(chooseEmojiButton,2,0);composerOptions.Controls.Add(emojiLabel,0,1);composerOptions.SetColumnSpan(emojiLabel,3);setup.Controls.Add(composerOptions,0,2);
        contentType.SelectedIndexChanged+=delegate{UpdateContentUI();};loadEmojiButton.Click+=async delegate{await ReadEmojis();};chooseEmojiButton.Click+=delegate{ChooseEmoji();};
        message=new TextBox {Multiline=true,Dock=DockStyle.Fill,MaxLength=300,AcceptsReturn=true,ScrollBars=ScrollBars.Vertical,BackColor=Visual.Input,ForeColor=Visual.Text,BorderStyle=BorderStyle.None,Margin=new Padding(5,6,5,4)};
        SoftPanel messageBox=new SoftPanel {Dock=DockStyle.Fill,BackColor=Visual.Input,Padding=new Padding(7,4,7,4),Margin=new Padding(0,1,0,5)};messageBox.Controls.Add(message);setup.Controls.Add(messageBox,0,3);
        TableLayoutPanel types=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=2,RowCount=2,Margin=Padding.Empty};types.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50));types.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50));types.RowStyles.Add(new RowStyle(SizeType.Percent,50));types.RowStyles.Add(new RowStyle(SizeType.Percent,50));
        active=new CheckBox {Text="已点燃",AutoSize=true,ForeColor=Visual.Text,Margin=new Padding(0,4,0,0)};recovery=new CheckBox {Text="重燃中",AutoSize=true,ForeColor=Visual.Text,Margin=new Padding(0,4,0,0)};dormant=new CheckBox {Text="灰色 / 未点燃",AutoSize=true,ForeColor=Visual.Text,Margin=new Padding(0,4,0,0)};expiring=new CheckBox {Text="即将消失",AutoSize=true,ForeColor=Visual.Text,Margin=new Padding(0,4,0,0)};
        types.Controls.Add(active,0,0);types.Controls.Add(recovery,1,0);types.Controls.Add(dormant,0,1);types.Controls.Add(expiring,1,1);setup.Controls.Add(types,0,4);
        TableLayoutPanel preferences=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=2,RowCount=1,Margin=Padding.Empty};preferences.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));preferences.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,115));preferences.RowStyles.Add(new RowStyle(SizeType.Percent,100));
        Label smart=LabelText("自选默认：近 30 天联系 + 互相关注\n其余不勾选；可手动调整。",8,Visual.Muted,false);preferences.Controls.Add(smart,0,0);
        dailyDedup=new CheckBox {Text="每日防重复",AutoSize=true,Checked=true,ForeColor=Visual.Text,Anchor=AnchorStyles.Right,Margin=Padding.Empty};preferences.Controls.Add(dailyDedup,1,0);setup.Controls.Add(preferences,0,5);
        tips.SetToolTip(dailyDedup,"勾选：同一好友当天只尝试一次。取消：再次点击发送可在当天重复发送；仍保留发送记录。");
        TableLayoutPanel numbers=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=4,Margin=Padding.Empty};numbers.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,80));numbers.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50));numbers.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,80));numbers.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50));
        numbers.Controls.Add(LabelText("间隔 / 秒",8,Visual.Muted,false),0,0);numbers.Controls.Add(LabelText("人数上限",8,Visual.Muted,false),2,0);
        interval=new NumericUpDown {Minimum=10,Maximum=300,Value=15,Dock=DockStyle.Fill,BackColor=Visual.Input,ForeColor=Visual.Text,BorderStyle=BorderStyle.FixedSingle,Margin=new Padding(0,10,10,0)};
        limit=new NumericUpDown {Minimum=1,Maximum=200,Value=100,Dock=DockStyle.Fill,BackColor=Visual.Input,ForeColor=Visual.Text,BorderStyle=BorderStyle.FixedSingle,Margin=new Padding(0,10,0,0)};numbers.Controls.Add(interval,1,0);numbers.Controls.Add(limit,3,0);setup.Controls.Add(numbers,0,6);
        TableLayoutPanel actions=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=2,Margin=Padding.Empty};actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,38));actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,62));
        scanButton=ButtonText("扫描火花",Color.FromArgb(44,55,74));scanButton.Dock=DockStyle.Fill;sendButton=ButtonText("开始续火",Visual.Accent);sendButton.Dock=DockStyle.Fill;sendButton.Font=new Font(Font,FontStyle.Bold);
        scanButton.Click+=async delegate{await StartTask("scan");};sendButton.Click+=async delegate{await StartTask("send");};actions.Controls.Add(scanButton,0,0);actions.Controls.Add(sendButton,1,0);setup.Controls.Add(actions,0,7);
        TableLayoutPanel secondary=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=3,Margin=Padding.Empty};for(int i=0;i<3;i++)secondary.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,33.33F));
        stopButton=ButtonText("停止 · Esc",Color.FromArgb(65,47,51));stopButton.Dock=DockStyle.Fill;stopButton.Click+=async delegate{await StopTask();};saveButton=ButtonText("保存设置",Color.FromArgb(37,46,63));saveButton.Dock=DockStyle.Fill;saveButton.Click+=delegate{try{SaveSettings();SetStatus("设置已保存在本机。");}catch(Exception ex){SetStatus(ex.Message);}};
        Button export=ButtonText("导出日志",Color.FromArgb(37,46,63));export.Dock=DockStyle.Fill;export.Click+=delegate{ExportLogs();};secondary.Controls.Add(stopButton,0,0);secondary.Controls.Add(saveButton,1,0);secondary.Controls.Add(export,2,0);setup.Controls.Add(secondary,0,8);
        status=LabelText("先登录，再扫描或选择好友。",8.5F,Color.FromArgb(208,190,166),false);status.AutoEllipsis=true;setup.Controls.Add(status,0,9);
        SoftPanel peopleCard=new SoftPanel {Dock=DockStyle.Fill,Margin=new Padding(0,0,0,10),Padding=new Padding(12,9,12,10)};sidebar.Controls.Add(peopleCard,0,1);
        TableLayoutPanel peopleLayout=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=1,RowCount=2,Margin=Padding.Empty,BackColor=Visual.Card};peopleLayout.RowStyles.Add(new RowStyle(SizeType.Absolute,36));peopleLayout.RowStyles.Add(new RowStyle(SizeType.Percent,100));peopleCard.Controls.Add(peopleLayout);
        TableLayoutPanel listTools=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=3,Margin=Padding.Empty};listTools.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));listTools.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,68));listTools.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,68));
        listLabel=LabelText("好友名单",9,Visual.Text,true);selectAllButton=ButtonText("全选",Color.FromArgb(37,46,63));selectAllButton.Dock=DockStyle.Fill;clearSelectionButton=ButtonText("清空",Color.FromArgb(37,46,63));clearSelectionButton.Dock=DockStyle.Fill;selectAllButton.Click+=delegate{SetAllRecipients(true);};clearSelectionButton.Click+=delegate{SetAllRecipients(false);};listTools.Controls.Add(listLabel,0,0);listTools.Controls.Add(selectAllButton,1,0);listTools.Controls.Add(clearSelectionButton,2,0);peopleLayout.Controls.Add(listTools,0,0);
        recipients=new RecipientList {Dock=DockStyle.Fill,Margin=new Padding(0,6,0,0)};
        recipients.ItemCheck+=delegate(object sender,ItemCheckEventArgs e){
            if(renderingRecipients)return;Recipient item=recipients.Items[e.Index] as Recipient;if(item==null)return;
            bool check=e.NewValue==CheckState.Checked;
            if(manual.Checked){selectionOverrides[item.Id]=check;if(check)selected.Add(item.Id);else selected.Remove(item.Id);}
            else{if(check)excluded.Remove(item.Id);else excluded.Add(item.Id);}
            UpdateListLabel();
        };
        peopleLayout.Controls.Add(recipients,0,1);
        SoftPanel activity=new SoftPanel {Dock=DockStyle.Fill,Margin=Padding.Empty,Padding=new Padding(12,7,12,7)};sidebar.Controls.Add(activity,0,2);
        logBox=new TextBox {Multiline=true,ReadOnly=true,Dock=DockStyle.Fill,BackColor=Visual.Card,ForeColor=Visual.Muted,BorderStyle=BorderStyle.None,ScrollBars=ScrollBars.Vertical,Font=new Font(Font.FontFamily,8F)};activity.Controls.Add(logBox);
        manual.CheckedChanged+=delegate{selected.Clear();selectionOverrides.Clear();excluded.Clear();recipients.ClearItems();UpdateModeUI();SetStatus(manual.Checked?"默认勾选近30天联系且互关的好友；可手动调整。":"按四类火花筛选；扫描后可调整名单。");};
        footer=LabelText("本地保存 · 防重复可选 · 手动选择优先       发送与续火结果以抖音为准",8,Visual.Muted,false);root.Controls.Add(footer,0,2);
        foreach(TableLayoutPanel oneRow in new[]{heading,navigation,setupTitle,numbers,actions,secondary,listTools}) {oneRow.RowCount=1;oneRow.RowStyles.Clear();oneRow.RowStyles.Add(new RowStyle(SizeType.Percent,100));}
        SetBusy(false);
    }


    private string ContentKind {get{return contentType.SelectedIndex==1?"builtin":contentType.SelectedIndex==2?"custom":"text";}}
    private void UpdateContentUI()
    {
        if(contentType==null||message==null)return;
        bool custom=ContentKind=="custom";string library=custom?"custom":"builtin";
        contentType.Enabled=!busy;message.Enabled=!busy&&!custom;
        loadEmojiButton.Enabled=ready&&automationAvailable&&!busy;
        chooseEmojiButton.Enabled=!busy&&emojiCatalogs.ContainsKey(library)&&emojiCatalogs[library].Count>0;
        chooseEmojiButton.Text=custom?"选择表情包":"插入表情";
        if(custom)emojiLabel.Text=selectedEmoji!=null&&Json.Str(selectedEmoji,"kind")=="custom"?"已选："+Json.Str(selectedEmoji,"name")+" · 仅发送表情包":"打开单聊 → 读取表情 → 选择收藏表情包。";
        else emojiLabel.Text="点选以 [名称] 插入下方光标处；可多选、重复选择。";
    }
    private async Task ReadEmojis()
    {
        if(busy||!ready||!automationAvailable)return;
        try{string kind=ContentKind=="custom"?"custom":"builtin";string result=await browser.CoreWebView2.ExecuteScriptAsync("Boolean(window.__SparkDesktop&&window.__SparkDesktop.loadEmoji("+Json.Encode(kind)+"))");if(result!="true")SetStatus("表情读取入口未就绪。");}catch(Exception ex){SetStatus(ex.Message);}
    }
    private bool InsertBuiltinEmoji(Dictionary<string,object> item)
    {
        if(busy||ContentKind=="custom")return false;
        string token=Json.Str(item,"token");
        if(!Regex.IsMatch(token,@"^\[[^\[\]\r\n]{1,40}\]$")||String.IsNullOrWhiteSpace(token.Substring(1,token.Length-2)))return false;
        int start=message.SelectionStart,length=message.SelectionLength;
        if(message.TextLength-length+token.Length>message.MaxLength){SetStatus("消息最多 300 字，未插入该表情。");return false;}
        message.SelectedText=token;message.SelectionStart=start+token.Length;message.SelectionLength=0;
        UpdateContentUI();return true;
    }
    private void ChooseEmoji()
    {
        string kind=ContentKind=="custom"?"custom":"builtin";List<Dictionary<string,object>> items;
        if(!emojiCatalogs.TryGetValue(kind,out items)||items.Count==0)return;
        Func<Dictionary<string,object>,bool> insert=kind=="builtin"?(Func<Dictionary<string,object>,bool>)InsertBuiltinEmoji:null;
        using(EmojiPicker picker=new EmojiPicker(items,currentEnvironment,"抖音"+(kind=="builtin"?"自带表情 · 连续插入":"收藏表情包"),false,insert)){
            picker.ShowDialog(this);
            if(kind=="custom"&&picker.SelectedEmoji!=null)selectedEmoji=picker.SelectedEmoji;
            UpdateContentUI();SaveSettings();
        }
        if(kind=="builtin")message.Focus();
    }

    private void ApplyConfig()
    {
        Dictionary<string, object> cfg = Json.Map(state, "config");
        message.Text = Json.Str(cfg, "message");
        selectedEmoji=Json.Map(cfg,"emoji");string kind=Json.Str(cfg,"contentKind");contentType.SelectedIndex=kind=="builtin"?1:kind=="custom"?2:0;
        if(kind=="builtin"&&!cfg.ContainsKey("builtinTokenVersion")&&selectedEmoji!=null){
            string token=Json.Str(selectedEmoji,"name");
            if(Regex.IsMatch(token,@"^\[[^\[\]\r\n]{1,40}\]$")&&message.TextLength+token.Length<=message.MaxLength)message.AppendText(token);
            selectedEmoji=null;
        }
        UpdateContentUI();
        active.Checked = cfg.ContainsKey("includeActive") && Convert.ToBoolean(cfg["includeActive"]);
        recovery.Checked = cfg.ContainsKey("includeRekindling") && Convert.ToBoolean(cfg["includeRekindling"]);
        dormant.Checked = !cfg.ContainsKey("includeDormant") || Convert.ToBoolean(cfg["includeDormant"]);
        expiring.Checked = !cfg.ContainsKey("includeExpiring") || Convert.ToBoolean(cfg["includeExpiring"]);
        manual.Checked = cfg.ContainsKey("manualSelection") && Convert.ToBoolean(cfg["manualSelection"]);
        dailyDedup.Checked = !cfg.ContainsKey("dailyDedup") || Convert.ToBoolean(cfg["dailyDedup"]);
        decimal n; if (decimal.TryParse(Json.Str(cfg, "intervalSeconds"), out n)) interval.Value = Math.Max(interval.Minimum, Math.Min(interval.Maximum, n));
        if (decimal.TryParse(Json.Str(cfg, "maxRecipients"), out n)) limit.Value = Math.Max(limit.Minimum, Math.Min(limit.Maximum, n));
        UpdateModeUI();
    }
    private Dictionary<string, object> CurrentConfig()
    {


        return new Dictionary<string, object> {{"builtinTokenVersion",1},{"contentKind",ContentKind},{"emoji",selectedEmoji},{"message",message.Text.Trim()},{"includeActive",active.Checked},{"includeRekindling",recovery.Checked},{"includeDormant",dormant.Checked},{"includeExpiring",expiring.Checked},{"manualSelection",manual.Checked},{"dailyDedup",dailyDedup.Checked},{"intervalSeconds",(int)interval.Value},{"maxRecipients",(int)limit.Value}};
    }
    private void SaveSettings() { state["config"] = CurrentConfig(); storage.Save(state); }
    private void SetAllRecipients(bool check)
    {
        if (busy) return;
        for (int i = 0; i < recipients.Items.Count; i++) {
            Recipient person=recipients.Items[i] as Recipient;
            if(manual.Checked && person!=null){selectionOverrides[person.Id]=check;if(check)selected.Add(person.Id);else selected.Remove(person.Id);}
            recipients.SetItemChecked(i, check);
        }
        UpdateListLabel();
    }
    private void UpdateListLabel()
    {
        if (listLabel == null || recipients == null) return;
        int count = 0;
        foreach (Recipient person in recipients.Items) if (manual.Checked ? selected.Contains(person.Id) : !excluded.Contains(person.Id)) count++;
        listLabel.Text = "已选 " + count + " / " + recipients.Items.Count + " 人";
    }
    private void UpdateModeUI()
    {
        foreach (CheckBox filter in new CheckBox[] { active, recovery, dormant, expiring }) {
            filter.Enabled = !busy; filter.AutoCheck = !manual.Checked;
            filter.ForeColor = manual.Checked ? muted : ink;
        }
        scanButton.Text = manual.Checked ? "扫描全部" : "扫描火花";
        sendButton.Text = manual.Checked ? "发送勾选好友" : "开始续火";
        UpdateListLabel();
    }
    private void SetStatus(string text) { lastStatus = text; status.Text = text; }
    private void Log(string text)
    {
        string line = DateTime.Now.ToString("HH:mm:ss") + " " + text; logLines.Add(line); if (logLines.Count > 1000) logLines.RemoveAt(0);
        int start = Math.Max(0, logLines.Count - 30); logBox.Text = String.Join(Environment.NewLine, logLines.GetRange(start, logLines.Count - start)); logBox.SelectionStart = logBox.TextLength; logBox.ScrollToCaret();
    }
    private void SetBusy(bool value)
    {
        busy = value; scanButton.Enabled = sendButton.Enabled = ready && automationAvailable && !value;
        stopButton.Enabled = value; homeButton.Enabled = refreshButton.Enabled = !value;
        message.Enabled = manual.Enabled = dailyDedup.Enabled = interval.Enabled = limit.Enabled = saveButton.Enabled = recipients.Enabled = selectAllButton.Enabled = clearSelectionButton.Enabled = !value;
        UpdateModeUI(); UpdateContentUI();
    }
    private bool ChatSource(string url)
    {
        Uri uri;return Uri.TryCreate(url,UriKind.Absolute,out uri)&&uri.Scheme=="https"&&uri.Host=="www.douyin.com"&&(uri.AbsolutePath=="/chat"||uri.AbsolutePath.StartsWith("/chat/",StringComparison.Ordinal));
    }
    private static string ResourceText(string name)
    {
        using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)) { if (stream == null) throw new FileNotFoundException("缺少程序内置资源 " + name); using (StreamReader reader = new StreamReader(stream, Encoding.UTF8)) return reader.ReadToEnd(); }
    }
    private async Task InitializeBrowser()
    {
        try
        {
            string version = CoreWebView2Environment.GetAvailableBrowserVersionString(null);
            Log("内置 WebView2 " + version);
            WebView2 sourceBrowser=browser;
            CoreWebView2Environment environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(storage.Folder, "WebView2"), null);currentEnvironment=environment;
            await browser.EnsureCoreWebView2Async(environment);
            browser.ZoomFactor = 0.8;
            browser.CoreWebView2.Settings.AreHostObjectsAllowed = false;
            browser.CoreWebView2.Settings.AreDevToolsEnabled = demo;
            browser.CoreWebView2.Settings.IsStatusBarEnabled = false;
            browser.CoreWebView2.WebMessageReceived += OnWebMessage;
            browser.CoreWebView2.NavigationStarting += delegate(object sender, CoreWebView2NavigationStartingEventArgs e)
            {
                if(sourceBrowser!=browser)return;
                Uri uri; if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out uri) || uri.Scheme != "https") { e.Cancel = true; return; }
                ready = false; SetBusy(false); session = ""; engineLabel.Text = "正在加载官方登录 / 私信页面…";
                selected.Clear(); selectionOverrides.Clear(); excluded.Clear(); recipients.ClearItems(); UpdateListLabel();
                address.Text = demo ? "本地模拟演示 · 不连接真实账号、不发送真实消息" : uri.GetLeftPart(UriPartial.Path);
            };
            browser.CoreWebView2.NavigationCompleted += async delegate(object sender, CoreWebView2NavigationCompletedEventArgs e)
            {
                if(sourceBrowser!=browser)return;
                if (!e.IsSuccess) { SetStatus("页面加载失败：" + e.WebErrorStatus + "。可点击刷新。"); return; }
                if (!ChatSource(browser.Source.AbsoluteUri)) { SetStatus("当前页面用于登录；登录后点击打开登录 / 私信。"); return; }
                try { await InjectEngine(); } catch (Exception ex) { SetStatus("页面适配启动失败：" + ex.Message); Log(ex.Message); }
            };
            browser.CoreWebView2.NewWindowRequested += delegate(object sender, CoreWebView2NewWindowRequestedEventArgs e)
            {
                e.Handled = true;if(sourceBrowser!=browser)return; Uri target; if (!busy && Uri.TryCreate(e.Uri, UriKind.Absolute, out target) && target.Scheme == "https") browser.CoreWebView2.Navigate(target.AbsoluteUri);
            };
            browser.CoreWebView2.ProcessFailed += delegate(object sender, CoreWebView2ProcessFailedEventArgs e) { if(sourceBrowser!=browser)return; ready = false; SetBusy(false); SetStatus("登录页面进程退出，任务已停止；点击刷新重新加载。"); };
            browser.CoreWebView2.PermissionRequested += delegate(object sender, CoreWebView2PermissionRequestedEventArgs e) { e.State = CoreWebView2PermissionState.Deny; };
            if (demo) ConfigureDemo(environment);
            NavigateChat();
        }
        catch (Exception ex)
        {
            SetStatus("内置浏览器启动失败：" + ex.Message); Log("需要 Microsoft Edge WebView2 Runtime；安装后重开软件。");
            if (selfTest) FinishTest(false, ex.ToString(), null);
        }
    }
    private void NavigateChat() { if (!busy && browser.CoreWebView2 != null) browser.CoreWebView2.Navigate("https://www.douyin.com/chat/"); }
    private async Task InjectEngine()
    {
        session = Guid.NewGuid().ToString("N"); state = storage.Read();
        string source = ResourceText("engine.js").Replace("__SPARK_BOOT__", Json.Encode(new Dictionary<string, object> {{"state",state},{"session",session}}));
        await browser.CoreWebView2.ExecuteScriptAsync(source);
    }
    private async void OnWebMessage(object sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        bool stopAfterError = false;
        try
        {
            if (browser.CoreWebView2!=sender || !ChatSource(args.Source)) return;
            Dictionary<string, object> envelope = Json.Object(args.WebMessageAsJson);
            if (Json.Str(envelope, "channel") != "spark-desktop-v1" || Json.Str(envelope, "session") != session) return;
            string type = Json.Str(envelope, "type"); Dictionary<string, object> data = Json.Map(envelope, "data");
            if (type == "persist")
            {
                string id = Json.Str(envelope, "id");
                string saveError = "";
                try { storage.Save(data); state = data; }
                catch (Exception ex) { saveError = "本地保存失败：" + ex.Message; }
                await Ack(id, saveError == "", saveError);
            }
            else if (type == "ready")
            {
                ready = true;Dictionary<string,object> caps=Json.Map(data,"capabilities");automationAvailable=caps==null?true:caps.ContainsKey("send")&&Convert.ToBoolean(caps["send"]);SetBusy(false);engineLabel.Text=automationAvailable?"就绪 · 本地保存":"登录可用 · 续火待适配";
                SetStatus(demo ? "本地演示就绪；操作不会发送真实消息。" : "登录页面已就绪。先完成登录，再扫描或发送。");
                if(!automationAvailable)SetStatus(Json.Str(data,"reason"));
                Log("抖音"+"页面就绪；"+(automationAvailable?"消息功能可用":"当前页面尚未验证私信控件"));
                if (selfTest && !selfTestStarted) { selfTestStarted = true; BeginInvoke(new Action(async delegate {await RunSelfTest();})); }
            }
            else if (type == "emojiCatalog") {
                string kind=Json.Str(data,"kind");var list=new List<Dictionary<string,object>>();IEnumerable items=data!=null&&data.ContainsKey("items")?data["items"] as IEnumerable:null;
                if(items!=null)foreach(object raw in items){var item=raw as Dictionary<string,object>;if(item!=null&&Json.Str(item,"kind")==kind)list.Add(item);}
                emojiCatalogs[kind]=list;UpdateContentUI();
            }
            else if (type == "busy") SetBusy(data != null && data.ContainsKey("value") && Convert.ToBoolean(data["value"]));
            else if (type == "status") SetStatus(Json.Str(data, "text"));
            else if (type == "log") Log(Json.Str(data, "text"));
            else if (type == "preview")
            {
                renderingRecipients = true;
                try {
                    recipients.ClearItems(); IEnumerable items = envelope["data"] as IEnumerable;
                    if (items != null) foreach (object raw in items)
                    {
                        Dictionary<string, object> item = raw as Dictionary<string, object>; if (item == null) continue;
                        Recipient person = new Recipient { Name = Json.Str(item, "name"), Id = Json.Str(item, "identity"), State = Json.Str(item, "state"), Evidence = Json.Str(item, "evidence"), Contact = Json.Str(item,"lastContact"), Reason=Json.Str(item,"defaultReason"), Relation=Json.Str(item,"mutualFollow"), Recent=Json.Str(item,"recentContact") };
                        if (manual.Checked && lastRunMode == "scan") {
                            bool includeDefault;
                            if (!selectionOverrides.TryGetValue(person.Id,out includeDefault)) includeDefault=item.ContainsKey("defaultSelected")&&Convert.ToBoolean(item["defaultSelected"]);
                            if (includeDefault) selected.Add(person.Id); else selected.Remove(person.Id);
                        }
                        bool include = manual.Checked ? selected.Contains(person.Id) : !excluded.Contains(person.Id);
                        int index=recipients.Items.Add(person); recipients.SetItemChecked(index,include);
                    }
                } finally { renderingRecipients = false; }
                if (recipients.Items.Count > 0) recipients.TopIndex=0;
                UpdateListLabel();
            }
        }
        catch (Exception ex) { Log("页面消息处理失败：" + ex.Message); SetStatus("页面消息异常，任务已停止。"); stopAfterError = true; }
        if (stopAfterError) await StopTask();
    }
    private Task<string> Ack(string id, bool success, string error)
    {
        return browser.CoreWebView2.ExecuteScriptAsync("window.__SparkDesktop && window.__SparkDesktop.ack(" + Json.Encode(id) + "," + (success ? "true" : "false") + "," + Json.Encode(error) + ");");
    }
    private async Task StartTask(string mode)
    {
        if (!ready || busy || !automationAvailable) return;
        if (mode == "send" && manual.Checked && selected.Count == 0) { SetStatus("请先扫描全部会话并勾选至少一位好友；本次未发送。"); return; }
        try
        {
            SaveSettings(); Dictionary<string, object> cfg = CurrentConfig(); lastRunMode=mode;
            SetBusy(true); SetStatus(mode == "scan" ? "正在扫描名单…" : "正在核对名单并准备发送…");
            string script = "Boolean(window.__SparkDesktop && window.__SparkDesktop.start(" + Json.Encode(mode) + "," + Json.Encode(cfg) + "," + Json.Encode(new List<string>(excluded)) + "," + Json.Encode(new List<string>(selected)) + "))";
            string result = await browser.CoreWebView2.ExecuteScriptAsync(script);
            if (result != "true") { SetBusy(false); SetStatus("页面引擎尚未就绪，请刷新后重试。"); }
        }
        catch (Exception ex) { SetBusy(false); SetStatus(ex.Message); Log(ex.Message); }
    }
    private async Task StopTask()
    {
        if (browser.CoreWebView2 == null) return;
        try { await browser.CoreWebView2.ExecuteScriptAsync("window.__SparkDesktop && window.__SparkDesktop.stop();"); } catch { }
        if (busy) SetStatus("正在停止，已经点击发送的消息不会撤回…");
    }
    private void ExportLogs()
    {
        using (SaveFileDialog dialog = new SaveFileDialog { Filter = "文本日志|*.txt", FileName = "火花助手日志-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt" })
            if (dialog.ShowDialog(this) == DialogResult.OK) File.WriteAllLines(dialog.FileName, logLines.ToArray(), Encoding.UTF8);
    }
    private void ConfigureDemo(CoreWebView2Environment environment)
    {
        browser.CoreWebView2.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        browser.CoreWebView2.WebResourceRequested += delegate(object sender, CoreWebView2WebResourceRequestedEventArgs e)
        {
            string body, contentType;
            if (ChatSource(e.Request.Uri)) { body = ResourceText("demo.html"); contentType = "text/html; charset=utf-8"; }
            else { body = "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"32\" height=\"32\"><rect width=\"32\" height=\"32\" rx=\"9\" fill=\"#e83555\"/></svg>"; contentType = "image/svg+xml"; }
            MemoryStream stream = new MemoryStream(Encoding.UTF8.GetBytes(body)); testResponses.Add(stream);
            e.Response = environment.CreateWebResourceResponse(stream, 200, "OK", "Content-Type: " + contentType + "\r\nCache-Control: no-store");
        };
    }
    private async Task WaitIdle()
    {
        Stopwatch watch = Stopwatch.StartNew(); while (busy && watch.ElapsedMilliseconds < 30000) await Task.Delay(80);
        if (busy) throw new TimeoutException("桌面任务未在测试时间内完成");
    }
    private async Task<int> DemoSentCount() { return Int32.Parse(await browser.CoreWebView2.ExecuteScriptAsync("window.sent.length")); }
    private async Task RunSelfTest()
    {
        List<string> checks = new List<string>();
        try
        {
            await Task.Delay(300);
            await browser.CoreWebView2.ExecuteScriptAsync("(function(){const R=Date,b=new R().setHours(12,0,0,0),start=R.now(),f=20;window.Date=class extends R{constructor(...a){super(...(a.length?a:[b+(R.now()-start)*f]));}static now(){return b+(R.now()-start)*f;}};const t=setTimeout.bind(window);window.setTimeout=(fn,ms,...a)=>t(fn,Math.max(4,ms/f),...a);})();");
            checks.Add("Native WebView2 window and desktop bridge initialized");
            await StartTask("scan"); await WaitIdle();
            if (recipients.Items.Count != 5 || await DemoSentCount() != 0) throw new Exception("扩展火花名单测试失败");
            checks.Add("Automatic scan includes active, recovery, gray, unlit and expiring flames");
            dormant.Checked = false; expiring.Checked = false;
            await StartTask("scan"); await WaitIdle();
            if (recipients.Items.Count != 2 || await DemoSentCount() != 0) throw new Exception("名单扫描测试失败"); checks.Add("Scan matches active and recovering friends and sends nothing");
            recipients.SetItemChecked(0, false);
            message.Text = "你好\r\n今天也开心 🔥"; interval.Value = 10;
            await StartTask("send"); await WaitIdle();
            if (await DemoSentCount() != 1) throw new Exception("排除名单测试失败：" + lastStatus);
            checks.Add("Native unchecked recipient stays excluded across rescanning");
            recipients.SetItemChecked(0, true);
            await StartTask("send"); await WaitIdle();
            if (await DemoSentCount() != 2) throw new Exception("桌面发送测试失败：" + lastStatus);
            string exact = await browser.CoreWebView2.ExecuteScriptAsync("window.sent.every(x=>x.text==='你好\\n今天也开心 🔥')");
            if (exact != "true") throw new Exception("自定义消息不一致"); checks.Add("Native settings deliver exact multiline messages to two selected friends");
            await StartTask("send"); await WaitIdle(); if (await DemoSentCount() != 2) throw new Exception("重复发送测试失败"); checks.Add("Second run does not send duplicate messages");
            if (Json.Map(storage.Read(), "records").Count != 2) throw new Exception("持久化记录测试失败"); checks.Add("Two attempt records saved through encrypted durable desktop storage");
            if (Encoding.UTF8.GetString(File.ReadAllBytes(storage.FilePath)).Contains("今天也开心")) throw new Exception("配置未加密"); checks.Add("Saved config is encrypted with Windows DPAPI");
            Dictionary<string, object> persisted = storage.Read();
            string ackRejected = await browser.CoreWebView2.ExecuteScriptAsync("JSON.stringify(Object.keys(window.__SparkDesktop))");
            if (ackRejected.IndexOf("start", StringComparison.Ordinal) < 0 || ackRejected.IndexOf("ack", StringComparison.Ordinal) < 0) throw new Exception("桌面桥接口异常");
            checks.Add("Only bounded start, emoji-library, stop and persistence acknowledgement commands exposed");
            await browser.CoreWebView2.ExecuteScriptAsync("window.__SparkDesktop.stop()"); checks.Add("Native stop command accepted");
            manual.Checked = true;
            await StartTask("scan"); await WaitIdle();
            if (recipients.Items.Count != 6 || recipients.CheckedItems.Count != 3) throw new Exception("自选模式智能默认选中状态测试失败："+recipients.CheckedItems.Count);
            foreach (Recipient person in recipients.CheckedItems) if (person.Name=="阿白"||person.Name=="普通好友"||person.Name=="尚未点燃的好友")throw new Exception("默认选中了应排除的好友");
            checks.Add("Manual defaults select only recent mutual friends; stale, non-mutual and unknown remain unchecked");
            SetAllRecipients(false);
            await StartTask("send"); await WaitIdle();
            if (await DemoSentCount() != 2) throw new Exception("空名单意外发送");
            checks.Add("Manual send with no selection sends nobody");
            for (int i = 0; i < recipients.Items.Count; i++) if (((Recipient)recipients.Items[i]).Name == "普通好友") recipients.SetItemChecked(i, true);
            await StartTask("send"); await WaitIdle();
            if (await DemoSentCount() != 3 || await browser.CoreWebView2.ExecuteScriptAsync("window.sent[2].name==='普通好友'") != "true") throw new Exception("普通好友自选发送测试失败：" + lastStatus);
            checks.Add("Manual mode sends only the explicitly selected non-fire friend");
            if (recipients.CheckedItems.Count != 1) throw new Exception("重新扫描改变了自选名单");
            checks.Add("Manual checked state is preserved across resend scanning");
            SetAllRecipients(true); if (recipients.CheckedItems.Count != 6) throw new Exception("全选失败");
            SetAllRecipients(false); if (recipients.CheckedItems.Count != 0) throw new Exception("清空失败");
            checks.Add("Native select-all and clear controls update the manual allowlist");
            for (int i = 0; i < recipients.Items.Count; i++) if (((Recipient)recipients.Items[i]).Name == "普通好友") recipients.SetItemChecked(i, true);
            await StartTask("send"); await WaitIdle(); if (await DemoSentCount() != 3) throw new Exception("自选模式重复发送");
            checks.Add("Daily deduplication remains effective in manual mode");
            await StartTask("scan"); await WaitIdle();
            if(recipients.CheckedItems.Count!=1||((Recipient)recipients.CheckedItems[0]).Name!="普通好友")throw new Exception("扫描覆盖了用户手动选择");
            checks.Add("Manual overrides for both checked and unchecked friends survive explicit rescans");
            if(recipients.TopIndex!=0)throw new Exception("本地名单未滚到顶部");
            await browser.CoreWebView2.ExecuteScriptAsync("document.querySelector('#scroller').style.height='170px';document.querySelector('#scroller').scrollTop=350");
            if(Int32.Parse(await browser.CoreWebView2.ExecuteScriptAsync("document.querySelector('#scroller').scrollTop"))<=0)throw new Exception("回顶测试前列表没有滚动");
            recipients.TopIndex=recipients.Items.Count-1;
            if(recipients.TopIndex==0)throw new Exception("回顶测试前本地名单没有滚动");
            await StartTask("scan");await WaitIdle();
            if(await browser.CoreWebView2.ExecuteScriptAsync("document.querySelector('#scroller').scrollTop")!="0"||recipients.TopIndex!=0)throw new Exception("扫描完成后列表未复位");
            checks.Add("Native recipient list and embedded conversation list both return to top after scan");
            await browser.CoreWebView2.ExecuteScriptAsync("document.querySelector('#scroller').style.height='600px'");
            selectionOverrides.Clear();
            await StartTask("scan"); await WaitIdle();
            if(recipients.CheckedItems.Count!=3)throw new Exception("默认筛选无法恢复");
            SetAllRecipients(false);
            await browser.CoreWebView2.ExecuteScriptAsync("document.querySelectorAll('.ConversationItemTagNextToTitletimeStr')[1].textContent='刚刚'");
            await StartTask("scan");await WaitIdle();
            if(recipients.CheckedItems.Count!=0)throw new Exception("清空后重新扫描恢复了勾选");
            checks.Add("Clear-all remembers unchecked state even when contact eligibility changes");
            await browser.CoreWebView2.ExecuteScriptAsync("document.querySelectorAll('.ConversationItemTagNextToTitletimeStr')[1].textContent='2025/01/01'");
            selectionOverrides.Clear();await StartTask("scan");await WaitIdle();

            contentType.SelectedIndex=1;await ReadEmojis();await WaitIdle();
            if(!emojiCatalogs.ContainsKey("builtin")||emojiCatalogs["builtin"].Count!=3)throw new Exception("自带表情库读取失败");
            checks.Add("Native built-in emoji library loads without sending");
            message.Text="前后";message.SelectionStart=1;
            using(EmojiPicker picker=new EmojiPicker(emojiCatalogs["builtin"],currentEnvironment,"自带表情连续插入测试",true,InsertBuiltinEmoji)){
                if(picker.ShowDialog(this)!=DialogResult.OK)throw new Exception("表情缩略图选择通信失败:"+String.Join(";",picker.Diagnostics));
            }
            string first=Json.Str(emojiCatalogs["builtin"][0],"token"),second=Json.Str(emojiCatalogs["builtin"][1],"token");
            if(message.Text!="前"+first+first+second+"后")throw new Exception("连续选择未按光标顺序插入或丢失重复表情");
            if(await DemoSentCount()!=3)throw new Exception("读取或选择表情意外发送");
            checks.Add("Native picker repeatedly inserts exact bracket tokens at the textbox caret without sending");
            message.Select(0,1);if(!InsertBuiltinEmoji(emojiCatalogs["builtin"][1])||!message.Text.StartsWith(second+first))throw new Exception("选中文字替换失败");
            string composed=message.Text;message.Text=new string('字',299);message.Select(299,0);
            if(InsertBuiltinEmoji(emojiCatalogs["builtin"][0])||message.TextLength!=299)throw new Exception("表情插入超过字符限制");
            message.Text=composed;message.SelectionStart=message.TextLength;UpdateContentUI();SaveSettings();
            checks.Add("Token insertion replaces selected text and enforces the existing 300-character limit");
            string originalMessage=message.Text;int dyRecordCount=Json.Map(storage.Read(),"records").Count;
            ready=false;browser.Reload();await WaitReady();ApplyConfig();
            if(!automationAvailable||message.Text!=originalMessage||Json.Map(state,"records").Count!=dyRecordCount)throw new Exception("重新加载后抖音配置或记录未保留");
            checks.Add("Reload preserves composed emoji tokens, config and attempt history");
            Dictionary<string,object> migration=Json.Map(state,"config");migration.Remove("builtinTokenVersion");migration["contentKind"]="builtin";migration["message"]="旧消息";migration["emoji"]=new Dictionary<string,object>{{"kind","builtin"},{"name","[捂脸]"}};
            ApplyConfig();if(message.Text!="旧消息[捂脸]")throw new Exception("旧版单表情设置未迁移到输入框");
            SaveSettings();ApplyConfig();if(message.Text!="旧消息[捂脸]")throw new Exception("配置重复加载追加了额外表情");
            message.Text=originalMessage;SaveSettings();
            checks.Add("Legacy single-emoji config migrates to visible tokens once without duplicate append");
            var resources=new HashSet<string>(Assembly.GetExecutingAssembly().GetManifestResourceNames());
            if(!resources.SetEquals(new[]{"engine.js","spark.ico","demo.html"}))throw new Exception("发布程序包含非预期平台资源");
            checks.Add("Executable embeds only the Douyin engine and required application resources");
            await browser.CoreWebView2.ExecuteScriptAsync("(function(){const R=Date,b=new R().setHours(12,0,0,0),start=R.now(),f=20;window.Date=class extends R{constructor(...a){super(...(a.length?a:[b+(R.now()-start)*f]));}static now(){return b+(R.now()-start)*f;}};const t=setTimeout.bind(window);window.setTimeout=(fn,ms,...a)=>t(fn,Math.max(4,ms/f),...a);})();");
            await browser.CoreWebView2.ExecuteScriptAsync("document.querySelector('[data-e2e=conversation-item]').dispatchEvent(new MouseEvent('mousedown',{bubbles:true}))");
            contentType.SelectedIndex=2;await ReadEmojis();await WaitIdle();
            if(!emojiCatalogs.ContainsKey("custom")||emojiCatalogs["custom"].Count!=3)throw new Exception("收藏表情库读取失败");
            selectedEmoji=emojiCatalogs["custom"][1];UpdateContentUI();
            state["records"]=new Dictionary<string,object>();storage.Save(state);
            await browser.CoreWebView2.ExecuteScriptAsync("window.__SparkDesktop=undefined;");await InjectEngine();await WaitReady();
            manual.Checked=false;active.Checked=false;recovery.Checked=false;dormant.Checked=true;expiring.Checked=false;limit.Value=10;
            await StartTask("send");await WaitIdle();
            if(await DemoSentCount()!=2||await browser.CoreWebView2.ExecuteScriptAsync("window.sent.every(x=>x.kind==='custom'&&x.text==='')")!="true")throw new Exception("收藏表情原生发送失败："+lastStatus);
            checks.Add("Native favorite-sticker mode sends only selected image content with no extra text");
            await StartTask("send");await WaitIdle();if(await DemoSentCount()!=2)throw new Exception("表情发送没有按日去重");
            checks.Add("Daily deduplication also covers native favorite-sticker mode");
            message.Text="你好[捂脸][微笑][捂脸]";contentType.SelectedIndex=1;SaveSettings();
            state["records"]=new Dictionary<string,object>();storage.Save(state);
            await browser.CoreWebView2.ExecuteScriptAsync("window.sent=[];window.__SparkDesktop=undefined;");await InjectEngine();await WaitReady();
            await StartTask("send");await WaitIdle();
            if(await DemoSentCount()!=2||await browser.CoreWebView2.ExecuteScriptAsync("window.sent.every(x=>x.text==='你好[捂脸][微笑][捂脸]'&&JSON.stringify(x.emojis)===JSON.stringify(['[捂脸]','[微笑]','[捂脸]']))")!="true")throw new Exception("输入框多表情未完整发送："+lastStatus);
            checks.Add("Native send preserves mixed text and repeated built-in token order in one message");
            if(!dailyDedup.Checked)throw new Exception("每日防重复应默认开启");
            await StartTask("send");await WaitIdle();if(await DemoSentCount()!=2)throw new Exception("默认开关未阻止重复发送");
            checks.Add("Daily duplicate checkbox defaults on and blocks repeated built-in messages");
            dailyDedup.Checked=false;await StartTask("send");await WaitIdle();
            if(await DemoSentCount()!=4||Json.Map(storage.Read(),"records").Count!=2)throw new Exception("关闭防重复后未允许重发或丢失发送记录");
            checks.Add("Disabling daily duplicate filtering permits another batch while retaining durable records");
            SaveSettings();dailyDedup.Checked=true;ApplyConfig();
            if(dailyDedup.Checked||Convert.ToBoolean(Json.Map(storage.Read(),"config")["dailyDedup"]))throw new Exception("开关设置未保存");
            checks.Add("Daily duplicate checkbox persists disabled value through config reload");
            dailyDedup.Checked=true;await StartTask("send");await WaitIdle();if(await DemoSentCount()!=4)throw new Exception("重新启用后未使用原有防重复记录");
            checks.Add("Re-enabling daily duplicate filtering immediately honors existing attempt history");
            manual.Checked=true;await StartTask("scan");await WaitIdle();
            if(Controls.Count!=1||Controls[0]!=rootLayout||rootLayout.Width!=ClientSize.Width)throw new Exception("单平台布局未铺满窗口");
            checks.Add("Single-platform layout fills the window without a platform navigation rail");
            if(reportPath!=null) { var debug=new List<object>(); CollectLayout(rootLayout,debug);File.WriteAllText(Path.Combine(Path.GetDirectoryName(reportPath),"layout-debug.json"),Json.Encode(debug),new UTF8Encoding(false)); }
            string screenshot = reportPath != null ? Path.Combine(Path.GetDirectoryName(reportPath), "软件界面演示.png") : null;
            if (screenshot != null) {
                await CaptureScreenshot(screenshot);
                Size original=Size;Size=MinimumSize;PerformLayout();await Task.Delay(100);
                if(recipients.ClientSize.Height<recipients.ItemHeight||sendButton.Height<30||message.Height<30)throw new Exception("最小窗口关键控件被截断");
                await CaptureScreenshot(Path.Combine(Path.GetDirectoryName(screenshot),"软件界面-最小窗口.png"));Size=original;
            }
            checks.Add("High-DPI rendering and minimum window layout keep input and friend rows visible");
            FinishTest(true, "Local isolated application smoke test; no real Douyin login or send performed.", checks);
        }
        catch (Exception ex) { FinishTest(false, ex.ToString() + "\nStatus: " + lastStatus, checks); }
    }
    private async Task WaitReady(){for(int i=0;i<200&&!ready;i++)await Task.Delay(50);if(!ready)throw new Exception("抖音页面未就绪");}
    private void CollectLayout(Control node,List<object> debug)
    {
        if(node is Label || node is TableLayoutPanel)debug.Add(new{Type=node.GetType().Name,Text=node.Text,Bounds=node.Bounds.ToString(),Font=node.Font.Size,FontHeight=node.Font.Height,Parent=node.Parent.GetType().Name});
        foreach(Control child in node.Controls)CollectLayout(child,debug);
    }
    private async Task CaptureScreenshot(string path)
    {
        using (MemoryStream stream = new MemoryStream())
        {
            await browser.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream); stream.Position = 0;
            using (Bitmap webImage = new Bitmap(stream))
            using (Bitmap full = new Bitmap(ClientSize.Width, ClientSize.Height))
            {
                rootLayout.DrawToBitmap(full, new Rectangle(Point.Empty, ClientSize));
                Point topLeft = PointToClient(browser.PointToScreen(Point.Empty));
                using (Graphics g = Graphics.FromImage(full)) g.DrawImage(webImage, new Rectangle(topLeft, browser.ClientSize));
                full.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }
        }
    }
    private void FinishTest(bool ok, string description, List<string> checks)
    {
        if (reportPath != null) { Directory.CreateDirectory(Path.GetDirectoryName(reportPath)); File.WriteAllText(reportPath, Json.Encode(new Dictionary<string, object> {{"ok",ok},{"description",description},{"checks",checks},{"timestamp",DateTimeOffset.Now.ToString("o")}}), new UTF8Encoding(false)); }
        Environment.ExitCode = ok ? 0 : 1; BeginInvoke(new Action(Close));
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { if (browser != null) browser.Dispose(); foreach (MemoryStream stream in testResponses) stream.Dispose(); }
        base.Dispose(disposing);
    }
    private sealed class Recipient : IRecipientVisual
    {
        internal string Name, Id, State, Evidence, Contact, Reason, Relation, Recent;
        public string Title { get { return Name; } }
        public string Detail { get { return String.IsNullOrEmpty(Reason)?"信息待确认":Reason; } }
        public string Hint { get { return Name+"\n"+Tag+"\n"+Detail+"\n最近会话："+(String.IsNullOrEmpty(Contact)?"未识别":Contact)+"\n可手动勾选或取消"; } }
        public string Tag { get {
            string label;
            switch (State) {
                case "active": label = "已点燃"; break;
                case "rekindling": label = "重燃中"; break;
                case "dormant": label = "灰色 / 未点燃"; break;
                case "expiring":
                    string countdown = (Evidence ?? "").Split('|')[0].Trim();
                    label = countdown.Length > 0 && countdown.Length <= 22 ? countdown : "即将消失"; break;
                case "inactive": label = "已消失 / 已结束"; break;
                case "none": label = "无火花标记"; break;
                default: label = "状态待确认"; break;
            }
            return label;
        } }
        public override string ToString() { return Name + " · " + Tag; }
    }
}
