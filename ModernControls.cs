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
internal sealed class RecipientList : ListBox
{
    private readonly HashSet<object> checks=new HashSet<object>();
    private readonly ToolTip tooltip=new ToolTip { InitialDelay=450,ReshowDelay=200,AutoPopDelay=9000 };
    private int hoverIndex=-1;
    public event ItemCheckEventHandler ItemCheck;
    internal RecipientList()
    {
        DrawMode=DrawMode.OwnerDrawFixed;ItemHeight=62;BorderStyle=BorderStyle.None;
        BackColor=Visual.Input;ForeColor=Visual.Text;IntegralHeight=false;
        SetStyle(ControlStyles.OptimizedDoubleBuffer,true);
    }
    internal List<object> CheckedItems { get { List<object> result=new List<object>();foreach(object item in Items) if(checks.Contains(item))result.Add(item);return result; } }
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        using(Graphics g=CreateGraphics())ItemHeight=(int)Math.Round(62*g.DpiY/96F);
    }
    internal bool GetItemChecked(int index) { return index>=0&&index<Items.Count&&checks.Contains(Items[index]); }
    internal void SetItemChecked(int index,bool value)
    {
        if(index<0||index>=Items.Count)return;
        object item=Items[index];bool old=checks.Contains(item);if(old==value)return;
        ItemCheckEventArgs args=new ItemCheckEventArgs(index,value?CheckState.Checked:CheckState.Unchecked,old?CheckState.Checked:CheckState.Unchecked);
        if(ItemCheck!=null)ItemCheck(this,args);
        if(args.NewValue==CheckState.Checked)checks.Add(item);else checks.Remove(item);
        Invalidate(GetItemRectangle(index));
    }
    internal void ClearItems() { checks.Clear();Items.Clear();hoverIndex=-1; }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);int index=IndexFromPoint(e.Location);
        if(Enabled&&e.Button==MouseButtons.Left&&index>=0)SetItemChecked(index,!GetItemChecked(index));
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if(e.KeyCode==Keys.Space&&SelectedIndex>=0&&Enabled){SetItemChecked(SelectedIndex,!GetItemChecked(SelectedIndex));e.Handled=true;e.SuppressKeyPress=true;return;}base.OnKeyDown(e);
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);int index=IndexFromPoint(e.Location);
        if(index!=hoverIndex){hoverIndex=index;IRecipientVisual item=index>=0?Items[index] as IRecipientVisual:null;tooltip.SetToolTip(this,item==null?"":item.Hint);Invalidate();}
    }
    protected override void OnMouseLeave(EventArgs e){hoverIndex=-1;Invalidate();base.OnMouseLeave(e);}
    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if(e.Index<0||e.Index>=Items.Count)return;
        IRecipientVisual item=Items[e.Index] as IRecipientVisual;if(item==null)return;
        bool check=GetItemChecked(e.Index),hover=e.Index==hoverIndex;
        Rectangle r=e.Bounds;float scale=DeviceDpiValue(e.Graphics);
        using(SolidBrush b=new SolidBrush(check?Color.FromArgb(33,39,53):hover?Color.FromArgb(28,35,48):BackColor))e.Graphics.FillRectangle(b,r);
        e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
        float box=17*scale,x=r.X+12*scale,y=r.Y+(r.Height-box)/2;
        using(GraphicsPath p=Visual.Round(new RectangleF(x,y,box,box),4*scale)){
            using(SolidBrush b=new SolidBrush(check?Visual.Accent:Color.FromArgb(27,34,47)))e.Graphics.FillPath(b,p);
            if(!check)using(Pen pen=new Pen(Color.FromArgb(81,96,120),1.1F))e.Graphics.DrawPath(pen,p);
        }
        if(check)using(Pen pen=new Pen(Color.White,1.8F*scale)){pen.StartCap=pen.EndCap=LineCap.Round;e.Graphics.DrawLines(pen,new PointF[]{new PointF(x+4*scale,y+9*scale),new PointF(x+7*scale,y+12*scale),new PointF(x+13*scale,y+5*scale)});}
        int left=r.X+(int)(41*scale),pad=(int)(12*scale);
        using(Font titleFont=new Font(Font.FontFamily,9.5F,FontStyle.Bold))
        using(Font detailFont=new Font(Font.FontFamily,8F)){
            int tagWidth=Math.Min((int)(118*scale),TextRenderer.MeasureText(item.Tag,detailFont).Width+14);
            Rectangle tag=new Rectangle(r.Right-tagWidth-pad,r.Top+(int)(10*scale),tagWidth,(int)(20*scale));
            using(SolidBrush b=new SolidBrush(Color.FromArgb(41,47,61)))using(GraphicsPath p=Visual.Round(tag,6*scale))e.Graphics.FillPath(b,p);
            TextRenderer.DrawText(e.Graphics,item.Tag,detailFont,tag,Color.FromArgb(246,189,137),TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter|TextFormatFlags.SingleLine|TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(e.Graphics,item.Title,titleFont,new Rectangle(left,r.Top+(int)(8*scale),Math.Max(0,tag.Left-left-5),(int)(24*scale)),Visual.Text,TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis|TextFormatFlags.SingleLine);
            TextRenderer.DrawText(e.Graphics,item.Detail,detailFont,new Rectangle(left,r.Top+(int)(34*scale),r.Right-left-pad,(int)(20*scale)),Visual.Muted,TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis|TextFormatFlags.SingleLine);
        }
        using(Pen pen=new Pen(Color.FromArgb(35,43,58)))e.Graphics.DrawLine(pen,r.X+10,r.Bottom-1,r.Right-10,r.Bottom-1);
    }
    private static float DeviceDpiValue(Graphics g){return g.DpiX/96F;}
    protected override void Dispose(bool disposing){if(disposing)tooltip.Dispose();base.Dispose(disposing);}
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
