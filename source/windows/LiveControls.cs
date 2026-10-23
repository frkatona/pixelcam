using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace PixelCam;

// A native TrackBar does not reliably expose WinForms DoubleClick. Inspect
// button messages before its native drag handling, and reset only if both
// clicks hit the thumb. Clicking the channel keeps normal page-step behavior.
sealed class ResettableTrackBar : TrackBar
{
    const int LeftDown=0x0201,LeftUp=0x0202,LeftDoubleClick=0x0203,MouseMoveMessage=0x0200,CancelMode=0x001F,GetThumbRectangle=0x0419;
    bool pendingThumbClick,suppressMouseUp;
    long previousClickTime;
    Point previousClick;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int ResetValue { get; set; }
    public void ResetToDefault()=>Value=Math.Clamp(ResetValue,Minimum,Maximum);
    public Rectangle GetThumbBounds()
    {
        if(!IsHandleCreated)return Rectangle.Empty;
        SendMessage(Handle,GetThumbRectangle,IntPtr.Zero,out NativeRect bounds);
        return Rectangle.FromLTRB(bounds.Left,bounds.Top,bounds.Right,bounds.Bottom);
    }
    static bool Nearby(Point first,Point second)
    {
        Size area=SystemInformation.DoubleClickSize;
        return Math.Abs(first.X-second.X)<=Math.Max(1,area.Width/2) && Math.Abs(first.Y-second.Y)<=Math.Max(1,area.Height/2);
    }
    protected override void WndProc(ref Message message)
    {
        if(message.Msg is LeftDown or LeftDoubleClick) {
            long packed=message.LParam.ToInt64();var point=new Point(unchecked((short)(packed&0xffff)),unchecked((short)((packed>>16)&0xffff)));
            bool thumb=Enabled && GetThumbBounds().Contains(point);long now=Environment.TickCount64;
            if(thumb && pendingThumbClick && now-previousClickTime<=SystemInformation.DoubleClickTime && Nearby(previousClick,point)) {
                pendingThumbClick=false;suppressMouseUp=true;Capture=false;Focus();ResetToDefault();message.Result=IntPtr.Zero;return;
            }
            pendingThumbClick=thumb;previousClick=point;previousClickTime=now;
        } else if(message.Msg==MouseMoveMessage && pendingThumbClick && (message.WParam.ToInt64()&1)!=0) {
            long packed=message.LParam.ToInt64();var point=new Point(unchecked((short)(packed&0xffff)),unchecked((short)((packed>>16)&0xffff)));
            if(!Nearby(previousClick,point))pendingThumbClick=false;
        } else if(message.Msg==LeftUp && suppressMouseUp) {
            suppressMouseUp=false;message.Result=IntPtr.Zero;return;
        } else if(message.Msg==CancelMode)pendingThumbClick=suppressMouseUp=false;
        base.WndProc(ref message);
    }
    protected override void OnEnabledChanged(EventArgs e){if(!Enabled)pendingThumbClick=suppressMouseUp=false;base.OnEnabledChanged(e);}
    protected override void OnLostFocus(EventArgs e){pendingThumbClick=false;base.OnLostFocus(e);}
    [StructLayout(LayoutKind.Sequential)]struct NativeRect { public int Left,Top,Right,Bottom; }
    [DllImport("user32.dll",EntryPoint="SendMessageW")]static extern IntPtr SendMessage(IntPtr window,int message,IntPtr parameter,out NativeRect rectangle);
}

sealed class ColorControl : UserControl
{
    public readonly CheckBox Automatic=new(){Text="Auto",Checked=false,AutoSize=true};
    public readonly ResettableTrackBar Slider=new(){TickStyle=TickStyle.None,AutoSize=false,Height=27,SmallChange=1,LargeChange=5};
    readonly Label valueLabel=new(){AutoSize=true,Anchor=AnchorStyles.Right,TextAlign=ContentAlignment.MiddleRight,Margin=new Padding(6,0,10,0)};
    readonly Func<int,string> format; bool quiet;int manualValue;
    public event EventHandler? Changed;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool IsAutomatic { get=>Automatic.Checked;set=>Automatic.Checked=value; }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int Value {
        get=>manualValue;
        set {
            int bounded=Math.Clamp(value,Slider.Minimum,Slider.Maximum);bool changed=manualValue!=bounded;manualValue=bounded;
            bool previous=quiet;quiet=true;Slider.Value=bounded;quiet=previous;UpdateText();
            if(changed && !quiet)Changed?.Invoke(this,EventArgs.Empty);
        }
    }
    public ColorControl(string name,int minimum,int maximum,int value,Func<int,string> format)
    {
        this.format=format;manualValue=value;AutoScaleMode=AutoScaleMode.None;Width=288;Height=58;Dock=DockStyle.Top;Margin=Padding.Empty;
        ForeColor=Color.FromArgb(231,237,245);BackColor=Color.FromArgb(23,32,44);
        // Reserve independent, content-sized columns so text and Auto cannot
        // overlap when Windows changes the font or the available panel width.
        var layout=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=3,RowCount=2,Margin=Padding.Empty,Padding=Padding.Empty};
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));Controls.Add(layout);
        layout.Controls.Add(new Label{Text=name,AutoSize=true,Anchor=AnchorStyles.Left,Margin=Padding.Empty},0,0);
        valueLabel.ForeColor=Color.FromArgb(169,189,211);layout.Controls.Add(valueLabel,1,0);
        Automatic.Anchor=AnchorStyles.Right;Automatic.Margin=Padding.Empty;layout.Controls.Add(Automatic,2,0);
        Slider.Minimum=minimum;Slider.Maximum=maximum;Slider.Value=value;Slider.ResetValue=value;Slider.Enabled=true;Slider.Dock=DockStyle.Fill;Slider.Margin=Padding.Empty;Slider.AccessibleName="Manual "+name;layout.Controls.Add(Slider,0,1);layout.SetColumnSpan(Slider,3);
        Automatic.AccessibleName="Automatic "+name;
        Automatic.CheckedChanged+=(_,_)=>{
            Slider.Enabled=!Automatic.Checked;
            if(!Automatic.Checked){bool previous=quiet;quiet=true;Slider.Value=manualValue;quiet=previous;}
            UpdateText();if(!quiet)Changed?.Invoke(this,EventArgs.Empty);
        };
        Slider.ValueChanged+=(_,_)=>{if(!quiet && !IsAutomatic)manualValue=Slider.Value;UpdateText();if(!quiet)Changed?.Invoke(this,EventArgs.Empty);};
        UpdateText();
    }
    void UpdateText()=>valueLabel.Text=format(Slider.Value);
    public void ShowAuto(int value){if(!IsAutomatic)return;bool previous=quiet;quiet=true;Slider.Value=Math.Clamp(value,Slider.Minimum,Slider.Maximum);quiet=previous;UpdateText();}
}

sealed class WhiteBalanceControl : UserControl
{
    public readonly CheckBox Automatic=new(){Text="Auto",Checked=false,AutoSize=true};
    public readonly ResettableTrackBar Warmth=Track("White balance warmth"),Tint=Track("White balance tint");
    readonly Label warmLabel=new(),tintLabel=new();bool quiet;int manualWarmth,manualTint;
    public event EventHandler? Changed;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int ManualWarmth { get=>manualWarmth;set=>SetManual(true,value); }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int ManualTint { get=>manualTint;set=>SetManual(false,value); }
    static ResettableTrackBar Track(string name)=>new(){Minimum=-100,Maximum=100,Value=0,ResetValue=0,TickStyle=TickStyle.None,AutoSize=false,Height=25,SmallChange=1,LargeChange=5,Enabled=true,AccessibleName=name};
    public WhiteBalanceControl()
    {
        AutoScaleMode=AutoScaleMode.None;Width=288;Height=104;Dock=DockStyle.Top;Margin=Padding.Empty;ForeColor=Color.FromArgb(231,237,245);BackColor=Color.FromArgb(23,32,44);
        Controls.Add(new Label{Text="White balance",AutoSize=true,Location=new Point(0,0)});
        Automatic.Location=new Point(226,0);Automatic.Anchor=AnchorStyles.Top|AnchorStyles.Right;Automatic.AccessibleName="Automatic white balance";Controls.Add(Automatic);
        warmLabel.Location=new Point(0,24);warmLabel.Size=new Size(90,22);tintLabel.Location=new Point(0,61);tintLabel.Size=new Size(90,22);
        Warmth.Location=new Point(90,23);Warmth.Width=193;Warmth.Anchor=AnchorStyles.Left|AnchorStyles.Right|AnchorStyles.Top;
        Tint.Location=new Point(90,60);Tint.Width=193;Tint.Anchor=AnchorStyles.Left|AnchorStyles.Right|AnchorStyles.Top;
        Controls.Add(warmLabel);Controls.Add(tintLabel);Controls.Add(Warmth);Controls.Add(Tint);
        Automatic.CheckedChanged+=(_,_)=>{
            Warmth.Enabled=Tint.Enabled=!Automatic.Checked;
            if(!Automatic.Checked){bool previous=quiet;quiet=true;Warmth.Value=manualWarmth;Tint.Value=manualTint;quiet=previous;UpdateLabels();}
            if(!quiet)Changed?.Invoke(this,EventArgs.Empty);
        };
        Warmth.ValueChanged+=(_,_)=>{if(!quiet && !Automatic.Checked)manualWarmth=Warmth.Value;UpdateLabels();if(!quiet)Changed?.Invoke(this,EventArgs.Empty);};
        Tint.ValueChanged+=(_,_)=>{if(!quiet && !Automatic.Checked)manualTint=Tint.Value;UpdateLabels();if(!quiet)Changed?.Invoke(this,EventArgs.Empty);};UpdateLabels();
    }
    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);if(Automatic==null || Warmth==null)return;
        Automatic.Location=new Point(Math.Max(0,ClientSize.Width-Automatic.PreferredSize.Width-2),0);
        Warmth.Bounds=new Rectangle(90,23,Math.Max(40,ClientSize.Width-83),25);Tint.Bounds=new Rectangle(90,60,Math.Max(40,ClientSize.Width-83),25);
    }
    void UpdateLabels(){warmLabel.Text=$"Warmth {Warmth.Value:+0;-0;0}";tintLabel.Text=$"Tint {Tint.Value:+0;-0;0}";}
    void SetManual(bool warmth,int value)
    {
        int bounded=Math.Clamp(value,-100,100);bool changed=(warmth?manualWarmth:manualTint)!=bounded;
        if(warmth)manualWarmth=bounded;else manualTint=bounded;
        bool previous=quiet;quiet=true;(warmth?Warmth:Tint).Value=bounded;quiet=previous;UpdateLabels();
        if(changed && !quiet)Changed?.Invoke(this,EventArgs.Empty);
    }
    public void ShowAuto(double warmth,double tint){if(!Automatic.Checked)return;bool previous=quiet;quiet=true;Warmth.Value=Math.Clamp((int)Math.Round(warmth*100),-100,100);Tint.Value=Math.Clamp((int)Math.Round(tint*100),-100,100);quiet=previous;UpdateLabels();}
}

sealed class FramingControl : Control
{
    Bitmap? image; RectangleF full; double zoom=1,cx=.5,cy=.5;
    bool dragging;PointF dragOffset;
    public event EventHandler? Changed;
    public double Zoom=>zoom;public double CenterX=>cx;public double CenterY=>cy;
    public FramingControl()
    {
        SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw|ControlStyles.Selectable,true);
        BackColor=Color.FromArgb(9,15,23);ForeColor=Color.FromArgb(111,182,255);Height=177;Dock=DockStyle.Top;TabStop=true;
        AccessibleName="Framing navigator: drag the cropped frame inside the full camera image";
    }
    public void SetImage(Bitmap bitmap){var old=image;image=bitmap;old?.Dispose();UpdateImageBounds();Invalidate();}
    public void SetFrame(double newZoom,double x,double y,bool notify=true)
    {
        var bounded=new ProcessingSettings{Zoom=newZoom,CenterX=x,CenterY=y}.Bounded();
        if(zoom==bounded.Zoom && cx==bounded.CenterX && cy==bounded.CenterY)return;
        zoom=bounded.Zoom;cx=bounded.CenterX;cy=bounded.CenterY;Invalidate();if(notify)Changed?.Invoke(this,EventArgs.Empty);
    }
    RectangleF Crop=>new((float)(full.Left+full.Width*(cx-.5/zoom)),(float)(full.Top+full.Height*(cy-.5/zoom)),(float)(full.Width/zoom),(float)(full.Height/zoom));
    void UpdateImageBounds()
    {
        float aspect=image==null?16f/9:image.Width/(float)image.Height;
        float width=Math.Max(1,ClientSize.Width-12),height=width/aspect;
        if(height>ClientSize.Height-12){height=Math.Max(1,ClientSize.Height-12);width=height*aspect;}
        full=new RectangleF((ClientSize.Width-width)/2,(ClientSize.Height-height)/2,width,height);
    }
    protected override void OnSizeChanged(EventArgs e){base.OnSizeChanged(e);UpdateImageBounds();}
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);var g=e.Graphics;g.SmoothingMode=SmoothingMode.AntiAlias;
        UpdateImageBounds();
        if(image!=null)g.DrawImage(image,full);else{using var brush=new SolidBrush(Color.FromArgb(31,46,62));g.FillRectangle(brush,full);}
        using(var border=new Pen(Color.FromArgb(116,139,162),1))g.DrawRectangle(border,full.X,full.Y,full.Width,full.Height);
        RectangleF crop=Crop;
        using(var shade=new Region(full)){shade.Exclude(crop);using var brush=new SolidBrush(Color.FromArgb(135,0,0,0));g.FillRegion(brush,shade);}
        using(var pen=new Pen(ForeColor,Focused?3:2)){g.DrawRectangle(pen,crop.X,crop.Y,crop.Width,crop.Height);}
        using(var crosshair=new Pen(Color.FromArgb(215,255,255,255),1)){g.DrawLine(crosshair,crop.Left+crop.Width/2-6,crop.Top+crop.Height/2,crop.Left+crop.Width/2+6,crop.Top+crop.Height/2);g.DrawLine(crosshair,crop.Left+crop.Width/2,crop.Top+crop.Height/2-6,crop.Left+crop.Width/2,crop.Top+crop.Height/2+6);}
    }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);if(e.Button!=MouseButtons.Left || full.Width<=0 || !full.Contains(e.Location))return;
        Focus();RectangleF crop=Crop;
        if(!crop.Contains(e.Location))SetFrame(zoom,(e.X-full.Left)/full.Width,(e.Y-full.Top)/full.Height);
        dragOffset=new PointF((float)((e.X-full.Left)/full.Width-cx),(float)((e.Y-full.Top)/full.Height-cy));dragging=true;Capture=true;Cursor=Cursors.SizeAll;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);if(dragging && full.Width>0)PanFromPoint(e.Location);
        else Cursor=zoom>1 && full.Contains(e.Location)?Cursors.SizeAll:Cursors.Default;
    }
    protected override void OnMouseUp(MouseEventArgs e){base.OnMouseUp(e);dragging=false;Capture=false;}
    protected override void OnMouseCaptureChanged(EventArgs e){base.OnMouseCaptureChanged(e);if(!Capture)dragging=false;}
    protected override void OnMouseWheel(MouseEventArgs e){base.OnMouseWheel(e);SetFrame(zoom+(e.Delta>0?.1:-.1),cx,cy);}
    protected override bool IsInputKey(Keys keyData)=>keyData is Keys.Left or Keys.Right or Keys.Up or Keys.Down || base.IsInputKey(keyData);
    protected override void OnKeyDown(KeyEventArgs e)
    {
        double step=e.Shift ? .05 : .01;
        switch(e.KeyCode){case Keys.Left:SetFrame(zoom,cx-step,cy);break;case Keys.Right:SetFrame(zoom,cx+step,cy);break;case Keys.Up:SetFrame(zoom,cx,cy-step);break;case Keys.Down:SetFrame(zoom,cx,cy+step);break;case Keys.Home:SetFrame(1,.5,.5);break;default:base.OnKeyDown(e);return;}e.Handled=true;
    }
    public void TestDrag(float deltaX,float deltaY)
    {
        UpdateImageBounds();dragOffset=PointF.Empty;
        PanFromPoint(new PointF((float)(full.Left+(cx+deltaX)*full.Width),(float)(full.Top+(cy+deltaY)*full.Height)));
    }
    void PanFromPoint(PointF point)=>SetFrame(zoom,(point.X-full.Left)/full.Width-dragOffset.X,(point.Y-full.Top)/full.Height-dragOffset.Y);
    protected override void Dispose(bool disposing){if(disposing){image?.Dispose();image=null;}base.Dispose(disposing);}
}
