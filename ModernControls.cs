using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

internal static class Visual
{
    internal static readonly Color Canvas = Color.FromArgb(14,18,27);
    internal static readonly Color Card = Color.FromArgb(23,29,41);
    internal static readonly Color Input = Color.FromArgb(17,23,34);
    internal static readonly Color Border = Color.FromArgb(44,54,72);
    internal static readonly Color Text = Color.FromArgb(234,239,248);
    internal static readonly Color Muted = Color.FromArgb(155,171,194);
    internal static readonly Color Accent = Color.FromArgb(236,72,105);
    internal static GraphicsPath Round(RectangleF r, float radius)
    {
        GraphicsPath p = new GraphicsPath(); float d=Math.Min(radius*2,Math.Min(r.Width,r.Height));
        if(d<=0) { p.AddRectangle(r); return p; }
        p.AddArc(r.Left,r.Top,d,d,180,90);p.AddArc(r.Right-d,r.Top,d,d,270,90);
        p.AddArc(r.Right-d,r.Bottom-d,d,d,0,90);p.AddArc(r.Left,r.Bottom-d,d,d,90,90);p.CloseFigure();return p;
    }
}
internal sealed class SoftButton : Button
{
    private bool hover;
    internal SoftButton() { SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer,true); FlatStyle=FlatStyle.Flat;FlatAppearance.BorderSize=0;Cursor=Cursors.Hand; }
    protected override void OnMouseEnter(EventArgs e) { hover=true;Invalidate();base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover=false;Invalidate();base.OnMouseLeave(e); }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent==null?Visual.Card:Parent.BackColor);e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
        Color fill=Enabled?(hover?ControlPaint.Light(BackColor,.09F):BackColor):Color.FromArgb(37,44,57);
        using(GraphicsPath p=Visual.Round(new RectangleF(0,0,Width-1,Height-1),9))
        using(SolidBrush brush=new SolidBrush(fill)) e.Graphics.FillPath(brush,p);
        TextRenderer.DrawText(e.Graphics,Text,Font,new Rectangle(5,2,Width-10,Height-4),Enabled?ForeColor:Color.FromArgb(104,117,136),TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis|TextFormatFlags.SingleLine);
        if(Focused && ShowFocusCues) using(Pen pen=new Pen(Color.FromArgb(130,Visual.Text))) using(GraphicsPath p=Visual.Round(new RectangleF(3,3,Width-7,Height-7),7)) e.Graphics.DrawPath(pen,p);
    }
}
internal sealed class SoftPanel : Panel
{
    internal SoftPanel() { DoubleBuffered=true;BackColor=Visual.Card;Padding=new Padding(14); }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
        using(GraphicsPath p=Visual.Round(new RectangleF(.5F,.5F,Width-1,Height-1),14))
        using(Pen pen=new Pen(Visual.Border)) e.Graphics.DrawPath(pen,p);
        base.OnPaint(e);
    }
}
internal interface IRecipientVisual
{
    string Title { get; }
    string Detail { get; }
    string Tag { get; }
    string Hint { get; }
}
internal sealed class RecipientList : Control
{
    internal sealed class RecipientCollection : System.Collections.ObjectModel.Collection<object>
    {
        private readonly RecipientList owner;
        internal RecipientCollection(RecipientList value){owner=value;}
        internal new int Add(object item){base.Add(item);return Count-1;}
        protected override void InsertItem(int index,object item){base.InsertItem(index,item);owner.ItemsChanged();}
        protected override void RemoveItem(int index){owner.checks.Remove(this[index]);base.RemoveItem(index);owner.ItemsChanged();}
        protected override void ClearItems(){base.ClearItems();owner.checks.Clear();owner.ItemsChanged();}
        protected override void SetItem(int index,object item){owner.checks.Remove(this[index]);base.SetItem(index,item);owner.ItemsChanged();}
    }
    private readonly HashSet<object> checks=new HashSet<object>();
    private readonly ToolTip tooltip=new ToolTip {InitialDelay=450,ReshowDelay=200,AutoPopDelay=9000};
    private readonly VScrollBar scrollbar=new VScrollBar {Dock=DockStyle.Right,TabStop=false,Visible=false};
    private readonly System.Windows.Forms.Timer animation=new System.Windows.Forms.Timer {Interval=15};
    private readonly System.Diagnostics.Stopwatch frameClock=new System.Diagnostics.Stopwatch();
    private Font titleFont,detailFont;
    private int hoverIndex=-1,selectedIndex=-1,updateDepth;
    private double offset,target;
    private float scale=1F;
    internal readonly RecipientCollection Items;
    internal int ItemHeight {get;private set;}
    internal double ScrollOffset {get{return offset;}}
    internal double ScrollTarget {get{return target;}}
    internal bool IsAnimating {get{return animation.Enabled;}}
    internal int TopIndex {get{return (int)(offset/Math.Max(1,ItemHeight));}set{ScrollTo(value*(double)ItemHeight,false);}}
    internal int SelectedIndex {get{return selectedIndex;}set{int old=selectedIndex;selectedIndex=Math.Max(-1,Math.Min(Items.Count-1,value));InvalidateRow(old);InvalidateRow(selectedIndex);}}
    public event ItemCheckEventHandler ItemCheck;
    internal RecipientList()
    {
        Items=new RecipientCollection(this);ItemHeight=62;BackColor=Visual.Input;ForeColor=Visual.Text;TabStop=true;
        AccessibleRole=AccessibleRole.List;AccessibleName="可勾选好友列表";
        SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw|ControlStyles.Selectable,true);
        Controls.Add(scrollbar);scrollbar.Scroll+=delegate(object sender,ScrollEventArgs e){ScrollTo(e.NewValue,false);};
        animation.Tick+=delegate{AnimateFrame();};UpdateFonts();
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SystemParametersInfo(uint action,uint parameter,ref int value,uint flags);
    private static bool AnimationEnabled {get{int value=1;return !SystemParametersInfo(0x1042,0,ref value,0)||value!=0;}}
    private double MaximumOffset {get{return Math.Max(0,Items.Count*(double)ItemHeight-ClientSize.Height);}}
    internal List<object> CheckedItems {get{var result=new List<object>();foreach(object item in Items)if(checks.Contains(item))result.Add(item);return result;}}
    internal void BeginUpdate(){updateDepth++;}
    internal void EndUpdate(){if(updateDepth>0)updateDepth--;if(updateDepth==0){UpdateScrollMetrics();Invalidate();}}
    private void ItemsChanged(){if(updateDepth==0){UpdateScrollMetrics();Invalidate();}}
    private void UpdateFonts()
    {
        if(titleFont!=null)titleFont.Dispose();if(detailFont!=null)detailFont.Dispose();
        titleFont=new Font(Font.FontFamily,9.5F,FontStyle.Bold);detailFont=new Font(Font.FontFamily,8F);
    }
    protected override void OnFontChanged(EventArgs e){base.OnFontChanged(e);UpdateFonts();Invalidate();}
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);using(Graphics g=CreateGraphics())scale=g.DpiY/96F;
        ItemHeight=(int)Math.Round(62*scale);UpdateScrollMetrics();
    }
    protected override void OnResize(EventArgs e){base.OnResize(e);if(Items!=null)UpdateScrollMetrics();}
    private void UpdateScrollMetrics()
    {
        offset=Math.Max(0,Math.Min(MaximumOffset,offset));target=Math.Max(0,Math.Min(MaximumOffset,target));
        scrollbar.Visible=MaximumOffset>0;scrollbar.SmallChange=ItemHeight;scrollbar.LargeChange=Math.Max(1,ClientSize.Height);
        scrollbar.Maximum=Math.Max(0,Items.Count*ItemHeight-1);SyncThumb();
        if(selectedIndex>=Items.Count)selectedIndex=Items.Count-1;
    }
    private void SyncThumb(){scrollbar.Value=Math.Max(0,Math.Min(Math.Max(0,scrollbar.Maximum-scrollbar.LargeChange+1),(int)Math.Round(offset)));}
    private void ScrollTo(double value,bool smooth)
    {
        target=Math.Max(0,Math.Min(MaximumOffset,value));hoverIndex=-1;tooltip.SetToolTip(this,"");
        if(!smooth||!AnimationEnabled){animation.Stop();frameClock.Reset();offset=target;SyncThumb();Invalidate();return;}
        if(Math.Abs(target-offset)<.5){offset=target;animation.Stop();SyncThumb();Invalidate();return;}
        if(!animation.Enabled){frameClock.Restart();animation.Start();}
    }
    internal void ScrollBy(double pixels){ScrollTo(target+pixels,true);}
    private void AnimateFrame()
    {
        double elapsed=Math.Min(50,Math.Max(1,frameClock.Elapsed.TotalMilliseconds));frameClock.Restart();
        offset+=(target-offset)*(1-Math.Exp(-elapsed/38.0));
        if(Math.Abs(target-offset)<.5){offset=target;animation.Stop();frameClock.Reset();}
        SyncThumb();Invalidate();
    }
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if(!Enabled)return;int lines=SystemInformation.MouseWheelScrollLines;
        if(lines==0)return;double step=lines<0?Math.Max(ItemHeight,ClientSize.Height-ItemHeight):ItemHeight*(double)lines;
        ScrollBy(-e.Delta/120.0*step);
    }
    internal bool GetItemChecked(int index){return index>=0&&index<Items.Count&&checks.Contains(Items[index]);}
    internal void SetItemChecked(int index,bool value)
    {
        if(index<0||index>=Items.Count)return;object item=Items[index];bool old=checks.Contains(item);if(old==value)return;
        var args=new ItemCheckEventArgs(index,value?CheckState.Checked:CheckState.Unchecked,old?CheckState.Checked:CheckState.Unchecked);
        if(ItemCheck!=null)ItemCheck(this,args);if(args.NewValue==CheckState.Checked)checks.Add(item);else checks.Remove(item);
        if(updateDepth==0)InvalidateRow(index);
    }
    internal void ClearItems(){animation.Stop();frameClock.Reset();offset=target=0;selectedIndex=hoverIndex=-1;tooltip.SetToolTip(this,"");checks.Clear();Items.Clear();}
    internal int IndexFromPoint(Point point)
    {
        if(point.Y<0||point.Y>=ClientSize.Height||point.X<0||point.X>=ClientSize.Width-(scrollbar.Visible?scrollbar.Width:0))return -1;
        int index=(int)Math.Floor((point.Y+offset)/Math.Max(1,ItemHeight));return index<Items.Count?index:-1;
    }
    private Rectangle RowRectangle(int index){return new Rectangle(0,(int)Math.Floor(index*ItemHeight-offset),Math.Max(0,ClientSize.Width-(scrollbar.Visible?scrollbar.Width:0)),ItemHeight);}
    private void InvalidateRow(int index){if(index<0||index>=Items.Count)return;Rectangle r=Rectangle.Intersect(ClientRectangle,RowRectangle(index));if(!r.IsEmpty)Invalidate(r);}
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);if(!Enabled||e.Button!=MouseButtons.Left)return;Focus();int index=IndexFromPoint(e.Location);
        if(index>=0){SelectedIndex=index;SetItemChecked(index,!GetItemChecked(index));}
    }
    protected override bool IsInputKey(Keys keyData){Keys k=keyData&Keys.KeyCode;return k==Keys.Up||k==Keys.Down||k==Keys.Home||k==Keys.End||k==Keys.PageUp||k==Keys.PageDown||base.IsInputKey(keyData);}
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if(!Enabled){base.OnKeyDown(e);return;}
        if(e.KeyCode==Keys.Space&&SelectedIndex>=0){SetItemChecked(SelectedIndex,!GetItemChecked(SelectedIndex));e.Handled=e.SuppressKeyPress=true;return;}
        int next=SelectedIndex,page=Math.Max(1,ClientSize.Height/ItemHeight);
        switch(e.KeyCode){case Keys.Down:next++;break;case Keys.Up:next=Math.Max(0,next-1);break;case Keys.Home:next=0;break;case Keys.End:next=Items.Count-1;break;case Keys.PageDown:next+=page;break;case Keys.PageUp:next-=page;break;default:base.OnKeyDown(e);return;}
        SelectedIndex=next;if(SelectedIndex>=0){double top=SelectedIndex*(double)ItemHeight;if(top<target)ScrollTo(top,false);else if(top+ItemHeight>target+Height)ScrollTo(top+ItemHeight-Height,false);}
        e.Handled=e.SuppressKeyPress=true;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);int index=IndexFromPoint(e.Location);if(index==hoverIndex)return;int old=hoverIndex;hoverIndex=index;
        IRecipientVisual item=index>=0?Items[index] as IRecipientVisual:null;tooltip.SetToolTip(this,item==null?"":item.Hint);InvalidateRow(old);InvalidateRow(index);
    }
    protected override void OnMouseLeave(EventArgs e){int old=hoverIndex;hoverIndex=-1;InvalidateRow(old);base.OnMouseLeave(e);}
    protected override void OnPaint(PaintEventArgs e)
    {
        if(updateDepth>0)return;e.Graphics.Clear(BackColor);int first=Math.Max(0,(int)Math.Floor(offset/ItemHeight));
        int last=Math.Min(Items.Count-1,(int)Math.Floor((offset+ClientSize.Height)/ItemHeight));
        for(int index=first;index<=last;index++) {var item=Items[index] as IRecipientVisual;if(item!=null)DrawRow(e.Graphics,index,item,RowRectangle(index));}
        base.OnPaint(e);
    }
    private void DrawRow(Graphics g,int index,IRecipientVisual item,Rectangle r)
    {
        bool check=GetItemChecked(index),hover=index==hoverIndex;
        using(SolidBrush b=new SolidBrush(check?Color.FromArgb(33,39,53):hover?Color.FromArgb(28,35,48):BackColor))g.FillRectangle(b,r);
        g.SmoothingMode=SmoothingMode.AntiAlias;
        float box=17*scale,x=r.X+12*scale,y=r.Y+(r.Height-box)/2;
        using(GraphicsPath p=Visual.Round(new RectangleF(x,y,box,box),4*scale)){
            using(SolidBrush b=new SolidBrush(check?Visual.Accent:Color.FromArgb(27,34,47)))g.FillPath(b,p);
            if(!check)using(Pen pen=new Pen(Color.FromArgb(81,96,120),1.1F))g.DrawPath(pen,p);
        }
        if(check)using(Pen pen=new Pen(Color.White,1.8F*scale)){pen.StartCap=pen.EndCap=LineCap.Round;g.DrawLines(pen,new PointF[]{new PointF(x+4*scale,y+9*scale),new PointF(x+7*scale,y+12*scale),new PointF(x+13*scale,y+5*scale)});}
        int left=r.X+(int)(41*scale),pad=(int)(12*scale);
        {
            int tagWidth=Math.Min((int)(118*scale),TextRenderer.MeasureText(item.Tag,detailFont).Width+14);
            Rectangle tag=new Rectangle(r.Right-tagWidth-pad,r.Top+(int)(10*scale),tagWidth,(int)(20*scale));
            using(SolidBrush b=new SolidBrush(Color.FromArgb(41,47,61)))using(GraphicsPath p=Visual.Round(tag,6*scale))g.FillPath(b,p);
            TextRenderer.DrawText(g,item.Tag,detailFont,tag,Color.FromArgb(246,189,137),TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter|TextFormatFlags.SingleLine|TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g,item.Title,titleFont,new Rectangle(left,r.Top+(int)(8*scale),Math.Max(0,tag.Left-left-5),(int)(24*scale)),Visual.Text,TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis|TextFormatFlags.SingleLine);
            TextRenderer.DrawText(g,item.Detail,detailFont,new Rectangle(left,r.Top+(int)(34*scale),r.Right-left-pad,(int)(20*scale)),Visual.Muted,TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis|TextFormatFlags.SingleLine);
        }
        using(Pen pen=new Pen(Color.FromArgb(35,43,58)))g.DrawLine(pen,r.X+10,r.Bottom-1,r.Right-10,r.Bottom-1);
    }
    protected override void Dispose(bool disposing){if(disposing){animation.Stop();animation.Dispose();tooltip.Dispose();if(titleFont!=null)titleFont.Dispose();if(detailFont!=null)detailFont.Dispose();}base.Dispose(disposing);}
}

internal sealed class SoftComboBox : ComboBox
{
 internal SoftComboBox(){DrawMode=DrawMode.OwnerDrawFixed;FlatStyle=FlatStyle.Flat;DropDownStyle=ComboBoxStyle.DropDownList;BackColor=Visual.Input;ForeColor=Visual.Text;}
 protected override void OnDrawItem(DrawItemEventArgs e){
  if(e.Index<0)return;bool hot=(e.State&DrawItemState.Selected)!=0;
  using(SolidBrush brush=new SolidBrush(hot?Color.FromArgb(44,55,74):Visual.Input))e.Graphics.FillRectangle(brush,e.Bounds);
  TextRenderer.DrawText(e.Graphics,GetItemText(Items[e.Index]),Font,e.Bounds,Visual.Text,TextFormatFlags.VerticalCenter|TextFormatFlags.Left|TextFormatFlags.EndEllipsis);
 }
}
