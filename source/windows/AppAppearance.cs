using System.Drawing.Drawing2D;

namespace PixelCam;

sealed record AppTheme(string Id,string Name,Color Window,Color Surface,Color Raised,Color Border,Color Text,Color Muted,Color Accent,Color AccentText,Color Selection)
{
    public override string ToString()=>Name;
}

static class AppAppearance
{
    public const string Primary="appearance:primary",Muted="appearance:muted",Preview="appearance:preview";
    public static readonly AppTheme[] Themes={
        new("midnight","Midnight",Color.FromArgb(19,26,36),Color.FromArgb(23,32,44),Color.FromArgb(34,45,60),Color.FromArgb(64,81,101),Color.FromArgb(231,237,245),Color.FromArgb(169,189,211),Color.FromArgb(56,124,220),Color.White,Color.FromArgb(37,67,106)),
        new("graphite","Graphite",Color.FromArgb(30,29,28),Color.FromArgb(39,38,36),Color.FromArgb(53,51,48),Color.FromArgb(87,83,77),Color.FromArgb(240,237,231),Color.FromArgb(191,183,171),Color.FromArgb(187,139,74),Color.FromArgb(21,20,18),Color.FromArgb(83,65,42)),
        new("light","Light",Color.FromArgb(242,244,247),Color.White,Color.FromArgb(232,236,242),Color.FromArgb(186,196,209),Color.FromArgb(29,38,52),Color.FromArgb(87,104,125),Color.FromArgb(38,100,185),Color.White,Color.FromArgb(217,231,250))
    };
    public static AppTheme Resolve(string? id)=>Themes.FirstOrDefault(t=>string.Equals(t.Id,id,StringComparison.OrdinalIgnoreCase))??Themes[0];
    public static void Apply(Control root,string? id)=>Apply(root,Resolve(id));
    public static void Apply(Control root,AppTheme theme)
    {
        root.SuspendLayout();
        try { ApplyTree(root,theme); }
        finally { root.ResumeLayout(true);root.Invalidate(true); }
    }
    static void ApplyTree(Control control,AppTheme theme)
    {
        // Retain the original muted role when switching between palettes later.
        if(control is Label && control.Tag==null && (control.ForeColor==Color.FromArgb(169,189,211) || control.ForeColor==Color.FromArgb(155,175,198)))control.Tag=Muted;
        Color surface=control.Parent?.BackColor??theme.Window;
        control.BackColor=control is Form?theme.Window:control is TabPage or ColorControl or WhiteBalanceControl?theme.Surface:surface;
        control.ForeColor=control.Tag as string==Muted?theme.Muted:theme.Text;
        switch(control)
        {
            case StyledTabControl tabs:
                tabs.Theme=theme;control.BackColor=theme.Window;break;
            case PictureBox:
                control.BackColor=Color.FromArgb(8,12,18);break;
            case FramingControl:
                control.BackColor=Color.FromArgb(9,15,23);control.ForeColor=theme.Accent;break;
            case Button button:
                bool primary=button.Tag as string==Primary;
                button.FlatStyle=FlatStyle.Flat;button.UseVisualStyleBackColor=false;
                button.BackColor=primary?theme.Accent:theme.Raised;button.ForeColor=primary?theme.AccentText:theme.Text;
                button.FlatAppearance.BorderColor=primary?theme.Accent:theme.Border;
                button.FlatAppearance.BorderSize=1;button.FlatAppearance.MouseOverBackColor=primary?Blend(theme.Accent,theme.AccentText,.12f):theme.Selection;
                button.FlatAppearance.MouseDownBackColor=primary?Blend(theme.Accent,Color.Black,.16f):theme.Border;break;
            case ComboBox combo:
                combo.BackColor=theme.Raised;combo.ForeColor=theme.Text;break;
            case ToolStrip strip:
                strip.BackColor=theme.Surface;strip.ForeColor=theme.Text;strip.Renderer=new AppearanceMenuRenderer(theme);
                ApplyItems(strip.Items,theme);break;
        }
        if(control.Tag as string==Preview){control.BackColor=Color.FromArgb(8,12,18);control.ForeColor=Color.FromArgb(169,189,211);}
        foreach(Control child in control.Controls)ApplyTree(child,theme);
    }
    static void ApplyItems(ToolStripItemCollection items,AppTheme theme)
    {
        foreach(ToolStripItem item in items)
        {
            item.BackColor=theme.Surface;item.ForeColor=theme.Text;
            if(item is ToolStripDropDownItem dropdown)
            {
                dropdown.DropDown.BackColor=theme.Surface;dropdown.DropDown.ForeColor=theme.Text;
                dropdown.DropDown.Renderer=new AppearanceMenuRenderer(theme);ApplyItems(dropdown.DropDownItems,theme);
            }
        }
    }
    static Color Blend(Color first,Color second,float weight)=>Color.FromArgb((int)(first.R*(1-weight)+second.R*weight),(int)(first.G*(1-weight)+second.G*weight),(int)(first.B*(1-weight)+second.B*weight));

    sealed class AppearanceMenuRenderer(AppTheme theme):ToolStripProfessionalRenderer(new AppearanceMenuColors(theme))
    {
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor=e.Item.Enabled?theme.Text:theme.Muted;base.OnRenderItemText(e);
        }
        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            Rectangle bounds=e.ImageRectangle;
            float unit=Math.Min(bounds.Width,bounds.Height)/16f;
            using var pen=new Pen(theme.Text,Math.Max(1.5f,unit*2)){StartCap=LineCap.Round,EndCap=LineCap.Round,LineJoin=LineJoin.Round};
            e.Graphics.DrawLines(pen,new[]{new PointF(bounds.Left+unit*3,bounds.Top+unit*8),new PointF(bounds.Left+unit*7,bounds.Top+unit*12),new PointF(bounds.Left+unit*13,bounds.Top+unit*4)});
        }
    }
    sealed class AppearanceMenuColors(AppTheme theme):ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground=>theme.Surface;
        public override Color MenuBorder=>theme.Border;
        public override Color MenuItemBorder=>theme.Border;
        public override Color MenuItemSelected=>theme.Selection;
        public override Color MenuItemSelectedGradientBegin=>theme.Selection;
        public override Color MenuItemSelectedGradientEnd=>theme.Selection;
        public override Color MenuItemPressedGradientBegin=>theme.Selection;
        public override Color MenuItemPressedGradientMiddle=>theme.Selection;
        public override Color MenuItemPressedGradientEnd=>theme.Selection;
        public override Color ImageMarginGradientBegin=>theme.Surface;
        public override Color ImageMarginGradientMiddle=>theme.Surface;
        public override Color ImageMarginGradientEnd=>theme.Surface;
        public override Color ToolStripGradientBegin=>theme.Surface;
        public override Color ToolStripGradientMiddle=>theme.Surface;
        public override Color ToolStripGradientEnd=>theme.Surface;
        public override Color StatusStripGradientBegin=>theme.Surface;
        public override Color StatusStripGradientEnd=>theme.Surface;
        public override Color MenuStripGradientBegin=>theme.Surface;
        public override Color MenuStripGradientEnd=>theme.Surface;
        public override Color ToolStripBorder=>theme.Border;
        public override Color SeparatorDark=>theme.Border;
        public override Color SeparatorLight=>theme.Surface;
        public override Color CheckBackground=>theme.Selection;
        public override Color CheckSelectedBackground=>theme.Selection;
        public override Color CheckPressedBackground=>theme.Selection;
    }
}

// Keep native tab navigation and accessibility; paint only the tab headers.
sealed class StyledTabControl:TabControl
{
    AppTheme theme=AppAppearance.Themes[0];
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public AppTheme Theme {get=>theme;set{theme=value;Invalidate();}}
    public StyledTabControl()
    {
        DrawMode=TabDrawMode.OwnerDrawFixed;SizeMode=TabSizeMode.Normal;Padding=new Point(20,6);
        SetStyle(ControlStyles.OptimizedDoubleBuffer,true);UpdateMetrics();
    }
    void UpdateMetrics()
    {
        int scale(int value)=>(int)Math.Round(value*DeviceDpi/96d);
        // Native per-label widths leave more room for the longer Color tab.
        var size=new Size(0,Math.Max(Font.Height+scale(14),scale(32)));
        if(ItemSize!=size)ItemSize=size;
    }
    protected override void OnControlAdded(ControlEventArgs e){base.OnControlAdded(e);UpdateMetrics();}
    protected override void OnFontChanged(EventArgs e){base.OnFontChanged(e);UpdateMetrics();}
    protected override void OnDpiChangedAfterParent(EventArgs e){base.OnDpiChangedAfterParent(e);UpdateMetrics();}
    protected override void OnSizeChanged(EventArgs e){base.OnSizeChanged(e);UpdateMetrics();}
    protected override void OnSelectedIndexChanged(EventArgs e){base.OnSelectedIndexChanged(e);Invalidate();}
    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if(e.Index<0 || e.Index>=TabPages.Count)return;
        Rectangle bounds=GetTabRect(e.Index);bool selected=e.Index==SelectedIndex;
        using(var fill=new SolidBrush(selected?theme.Selection:theme.Raised))e.Graphics.FillRectangle(fill,bounds);
        using(var border=new Pen(theme.Border))e.Graphics.DrawRectangle(border,bounds.Left,bounds.Top,bounds.Width-1,bounds.Height-1);
        if(selected)
        {
            int thickness=Math.Max(2,(int)Math.Round(3*DeviceDpi/96d));using var accent=new SolidBrush(theme.Accent);
            e.Graphics.FillRectangle(accent,bounds.Left,bounds.Bottom-thickness,bounds.Width,thickness);
        }
        Rectangle text=Rectangle.Inflate(bounds,-6,-3);text.Height-=2;
        using var font=new Font(Font,selected?FontStyle.Bold:FontStyle.Regular);
        TextRenderer.DrawText(e.Graphics,TabPages[e.Index].Text,font,text,selected?theme.Text:theme.Muted,TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis|TextFormatFlags.SingleLine|TextFormatFlags.NoPrefix);
        if(selected && Focused && ShowFocusCues)ControlPaint.DrawFocusRectangle(e.Graphics,Rectangle.Inflate(bounds,-4,-5),theme.Text,theme.Selection);
    }
}
