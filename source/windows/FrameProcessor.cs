using System.Diagnostics;
using System.Drawing.Imaging;

namespace PixelCam;

public sealed record ProcessingSettings
{
    public bool AutoBrightness { get; init; }
    public bool AutoContrast { get; init; }
    public bool AutoSaturation { get; init; }
    public bool AutoGamma { get; init; }
    public bool AutoWhiteBalance { get; init; }
    public double Brightness { get; init; }
    public double Contrast { get; init; } = 1;
    public double Saturation { get; init; } = 1;
    public double Gamma { get; init; } = 1;
    public double Warmth { get; init; }
    public double Tint { get; init; }
    public double Zoom { get; init; } = 1;
    public double CenterX { get; init; } = .5;
    public double CenterY { get; init; } = .5;
    public int Rotation { get; init; }
    public bool Mirror { get; init; }
    public BackgroundEffect Background { get; init; }
    public bool EyeContact { get; init; }
    public double BlurStrength { get; init; } = .5;
    public int SegmentationMode { get; init; } = 0;
    public string BackgroundImage { get; init; } = "";
    public ProcessingSettings Bounded()
    {
        double z=Math.Clamp(double.IsFinite(Zoom)?Zoom:1,1,5), half=.5/z;
        return this with {
            Zoom=z, CenterX=Math.Clamp(double.IsFinite(CenterX)?CenterX:.5,half,1-half), CenterY=Math.Clamp(double.IsFinite(CenterY)?CenterY:.5,half,1-half),
            Brightness=Math.Clamp(double.IsFinite(Brightness)?Brightness:0,-1,1), Contrast=Math.Clamp(double.IsFinite(Contrast)?Contrast:1,.25,2),
            Saturation=Math.Clamp(double.IsFinite(Saturation)?Saturation:1,0,2), Gamma=Math.Clamp(double.IsFinite(Gamma)?Gamma:1,.5,2),
            Warmth=Math.Clamp(double.IsFinite(Warmth)?Warmth:0,-1,1),Tint=Math.Clamp(double.IsFinite(Tint)?Tint:0,-1,1),
            Rotation=((Rotation%360+360)%360)/90*90,
            Background=Enum.IsDefined(Background)?Background:BackgroundEffect.Off,
            BlurStrength=Math.Clamp(double.IsFinite(BlurStrength)?BlurStrength:.5,0,1),SegmentationMode=Math.Clamp(SegmentationMode,0,3),BackgroundImage=BackgroundImage??""
        };
    }
    public static ProcessingSettings Neutral => new() { AutoBrightness=false,AutoContrast=false,AutoSaturation=false,AutoGamma=false,AutoWhiteBalance=false };
}
public sealed record EffectiveColor(double Brightness=0,double Contrast=1,double Saturation=1,double Gamma=1,double Warmth=0,double Tint=0);
readonly record struct VideoFrame(byte[] Data,int Width,int Height);

/// <summary>Live NV12 processing. Immutable settings are sampled once per frame. Cropping,
/// rotation and mirroring share one bilinear resample; color uses compact lookup tables.</summary>
sealed class FrameProcessor
{
    readonly int sourceWidth,sourceHeight;
    readonly byte[] yLut=new byte[256],uLut=new byte[256],vLut=new byte[256];
    readonly ParallelOptions parallel=new() { MaxDegreeOfParallelism=Math.Min(8,Environment.ProcessorCount) };
    Mapping? map; byte[]? output;
    double autoBrightness,autoContrast=1,autoSaturation=1,autoGamma=1,autoU,autoV;
    int frameNumber;
    EffectiveColor effective=new();
    public EffectiveColor Effective => Volatile.Read(ref effective);
    public double LastMilliseconds { get; private set; }
    public FrameProcessor(int width,int height) { sourceWidth=width;sourceHeight=height; }
    sealed class Axis
    {
        public int[] Start,Next,Weight;
        public Axis(int count,int source,double center,double zoom,bool invert)
        {
            Start=new int[count];Next=new int[count];Weight=new int[count];
            for(int i=0;i<count;i++) {
                double normalized=center-.5/zoom+(i+.5)/(count*zoom);
                double position=Math.Clamp((invert?1-normalized:normalized)*source-.5,0,source-1);
                int floor=(int)position; Start[i]=floor;Next[i]=Math.Min(source-1,floor+1);Weight[i]=(int)Math.Round((position-floor)*256);
            }
        }
    }
    sealed class Mapping
    {
        public ProcessingSettings Settings;
        public int Width,Height; public bool Transpose;
        public Axis X,Y,UVX,UVY;
        public Mapping(int sourceWidth,int sourceHeight,ProcessingSettings settings)
        {
            Settings=settings; Transpose=settings.Rotation%180!=0;
            Width=Transpose?sourceHeight:sourceWidth;Height=Transpose?sourceWidth:sourceHeight;
            bool inverseX=settings.Rotation==90 || settings.Rotation==180;
            bool inverseY=settings.Rotation==180 || settings.Rotation==270;
            inverseX^=settings.Mirror;
            // For a transposed image, display X indexes source Y and display Y indexes source X.
            int xSource=Transpose?sourceHeight:sourceWidth,ySource=Transpose?sourceWidth:sourceHeight;
            X=new Axis(Width,xSource,settings.CenterX,settings.Zoom,inverseX);
            Y=new Axis(Height,ySource,settings.CenterY,settings.Zoom,inverseY);
            UVX=new Axis(Width/2,xSource/2,settings.CenterX,settings.Zoom,inverseX);
            UVY=new Axis(Height/2,ySource/2,settings.CenterY,settings.Zoom,inverseY);
        }
        public bool SameGeometry(ProcessingSettings s) => Settings.Zoom==s.Zoom && Settings.CenterX==s.CenterX && Settings.CenterY==s.CenterY && Settings.Rotation==s.Rotation && Settings.Mirror==s.Mirror;
    }
    public VideoFrame Process(byte[] source,ProcessingSettings settings)
    {
        long start=Stopwatch.GetTimestamp(); settings=settings.Bounded();
        if(map==null || !map.SameGeometry(settings)) map=new Mapping(sourceWidth,sourceHeight,settings);
        if(output==null || output.Length!=source.Length) output=new byte[source.Length];
        if(frameNumber++%3==0) Analyze(source,map,settings);
        UpdateTables(settings);
        if(settings.Zoom==1 && settings.Rotation==0 && !settings.Mirror) ApplyColorOnly(source,output);
        else Resample(source,output,map);
        LastMilliseconds=Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        return new VideoFrame(output,map.Width,map.Height);
    }
    void Analyze(byte[] source,Mapping geometry,ProcessingSettings settings)
    {
        if(!settings.AutoBrightness && !settings.AutoContrast && !settings.AutoGamma && !settings.AutoSaturation && !settings.AutoWhiteBalance) return;
        var histogram=new int[256];
        int count=0,neutrals=0; double sum=0,chroma=0,neutralU=0,neutralV=0;
        int yStep=Math.Max(1,geometry.Height/72),xStep=Math.Max(1,geometry.Width/128),uvBase=sourceWidth*sourceHeight;
        for(int y=0;y<geometry.Height;y+=yStep) for(int x=0;x<geometry.Width;x+=xStep) {
            int sx=geometry.Transpose?geometry.Y.Start[y]:geometry.X.Start[x],sy=geometry.Transpose?geometry.X.Start[x]:geometry.Y.Start[y];
            int luma=source[sy*sourceWidth+sx];histogram[luma]++;sum+=luma;count++;
            int uv=uvBase+(sy/2)*sourceWidth+(sx&~1),u=source[uv]-128,v=source[uv+1]-128;
            chroma+=Math.Sqrt(u*u+v*v);
            if(luma>45 && luma<215 && Math.Abs(u)+Math.Abs(v)<36) { neutralU+=u;neutralV+=v;neutrals++; }
        }
        if(count==0) return;
        int Percentile(double fraction) { int target=(int)(count*fraction),running=0; for(int i=0;i<256;i++) { running+=histogram[i];if(running>=target)return i; } return 235; }
        int low=Percentile(.05),middle=Percentile(.5),high=Percentile(.95);
        double spread=high-low;
        double brightness=Math.Clamp((112-sum/count)/219d,-.07,.07);
        double contrast=spread<25?1:Math.Clamp(175/Math.Max(80,spread),.88,1.35);
        double centered=Math.Clamp(((middle-16)/219d-.5)*contrast+.5+brightness,.12,.88);
        double gamma=Math.Clamp(Math.Log(centered)/Math.Log(.46),.85,1.2);
        double saturation=chroma/count<5?1:Math.Clamp(25/(chroma/count),.88,1.15);
        double targetU=neutrals>40?Math.Clamp(-neutralU/neutrals,-7,7):0,targetV=neutrals>40?Math.Clamp(-neutralV/neutrals,-7,7):0;
        const double blend=.14;
        autoBrightness+=(brightness-autoBrightness)*blend;autoContrast+=(contrast-autoContrast)*blend;
        autoGamma+=(gamma-autoGamma)*blend;autoSaturation+=(saturation-autoSaturation)*blend;autoU+=(targetU-autoU)*blend;autoV+=(targetV-autoV)*blend;
    }
    void UpdateTables(ProcessingSettings s)
    {
        double brightness=s.AutoBrightness?autoBrightness:s.Brightness*.35;
        double contrast=s.AutoContrast?autoContrast:s.Contrast,saturation=s.AutoSaturation?autoSaturation:s.Saturation,gamma=s.AutoGamma?autoGamma:s.Gamma;
        double u=s.AutoWhiteBalance?autoU:-s.Warmth*20-s.Tint*8,v=s.AutoWhiteBalance?autoV:s.Warmth*17-s.Tint*8;
        for(int i=0;i<256;i++) {
            double luma=Math.Clamp(((i-16)/219d-.5)*contrast+.5+brightness,0,1);
            yLut[i]=(byte)Math.Clamp((int)Math.Round(16+219*Math.Pow(luma,1/gamma)),16,235);
            uLut[i]=(byte)Math.Clamp((int)Math.Round((i-128)*saturation+128+u),16,240);
            vLut[i]=(byte)Math.Clamp((int)Math.Round((i-128)*saturation+128+v),16,240);
        }
        // Solve the warmth/tint offsets for the UI's current auto reading.
        double warmth=(v-u)/37,tint=(-u-20*warmth)/8;
        Volatile.Write(ref effective,new EffectiveColor(brightness/.35,contrast,saturation,gamma,Math.Clamp(warmth,-1,1),Math.Clamp(tint,-1,1)));
    }
    unsafe void ApplyColorOnly(byte[] source,byte[] destination)
    {
        int lumaBytes=sourceWidth*sourceHeight;
        fixed(byte* src=source) fixed(byte* dst=destination) {
            nint input=(nint)src,output=(nint)dst;
            Parallel.For(0,(sourceHeight+31)/32,parallel,block=> {
                byte* from=(byte*)input,to=(byte*)output;
                int start=block*32*sourceWidth,end=Math.Min(sourceHeight,(block+1)*32)*sourceWidth;
                for(int i=start;i<end;i++) to[i]=yLut[from[i]];
            });
            Parallel.For(0,(sourceHeight/2+31)/32,parallel,block=> {
                byte* from=(byte*)input,to=(byte*)output;
                int start=lumaBytes+block*32*sourceWidth,end=lumaBytes+Math.Min(sourceHeight/2,(block+1)*32)*sourceWidth;
                for(int i=start;i<end;i+=2) { to[i]=uLut[from[i]];to[i+1]=vLut[from[i+1]]; }
            });
        }
    }
    unsafe void Resample(byte[] source,byte[] destination,Mapping geometry)
    {
        fixed(byte* src=source) fixed(byte* dst=destination) {
            nint input=(nint)src,output=(nint)dst;
            Parallel.For(0,(geometry.Height+15)/16,parallel,block=> {
                byte* from=(byte*)input,to=(byte*)output;
                for(int y=block*16;y<Math.Min(geometry.Height,(block+1)*16);y++) {
                    int dy=geometry.Y.Start[y],dyNext=geometry.Y.Next[y],wy=geometry.Y.Weight[y],outputRow=y*geometry.Width;
                    for(int x=0;x<geometry.Width;x++) {
                        int dx=geometry.X.Start[x],dxNext=geometry.X.Next[x],wx=geometry.X.Weight[x];
                        int a,b,c,d;
                        if(!geometry.Transpose) { int r0=dy*sourceWidth,r1=dyNext*sourceWidth; a=from[r0+dx];b=from[r0+dxNext];c=from[r1+dx];d=from[r1+dxNext]; }
                        else { int r0=dx*sourceWidth,r1=dxNext*sourceWidth; a=from[r0+dy];b=from[r1+dy];c=from[r0+dyNext];d=from[r1+dyNext]; }
                        int top=a*(256-wx)+b*wx,bottom=c*(256-wx)+d*wx;
                        to[outputRow+x]=yLut[(top*(256-wy)+bottom*wy+32768)>>16];
                    }
                }
            });
            int sourceUV=sourceWidth*sourceHeight,destinationUV=geometry.Width*geometry.Height;
            Parallel.For(0,(geometry.Height/2+15)/16,parallel,block=> {
                byte* from=(byte*)input+sourceUV,to=(byte*)output+destinationUV;
                for(int y=block*16;y<Math.Min(geometry.Height/2,(block+1)*16);y++) {
                    int dy=geometry.UVY.Start[y],dyNext=geometry.UVY.Next[y],wy=geometry.UVY.Weight[y],outputRow=y*geometry.Width;
                    for(int x=0;x<geometry.Width/2;x++) {
                        int dx=geometry.UVX.Start[x],dxNext=geometry.UVX.Next[x],wx=geometry.UVX.Weight[x];
                        int p00,p10,p01,p11;
                        if(!geometry.Transpose) { p00=dy*sourceWidth+dx*2;p10=dy*sourceWidth+dxNext*2;p01=dyNext*sourceWidth+dx*2;p11=dyNext*sourceWidth+dxNext*2; }
                        else { p00=dx*sourceWidth+dy*2;p10=dxNext*sourceWidth+dy*2;p01=dx*sourceWidth+dyNext*2;p11=dxNext*sourceWidth+dyNext*2; }
                        for(int channel=0;channel<2;channel++) {
                            int top=from[p00+channel]*(256-wx)+from[p10+channel]*wx,bottom=from[p01+channel]*(256-wx)+from[p11+channel]*wx;
                            int value=(top*(256-wy)+bottom*wy+32768)>>16;
                            to[outputRow+x*2+channel]=(channel==0?uLut:vLut)[value];
                        }
                    }
                }
            });
        }
    }
    public unsafe Bitmap Navigator(byte[] source,ProcessingSettings settings)
    {
        settings=settings.Bounded(); bool transposed=settings.Rotation%180!=0;
        int orientedW=transposed?sourceHeight:sourceWidth,orientedH=transposed?sourceWidth:sourceHeight;
        int w=256,h=Math.Max(2,(int)Math.Round(256d*orientedH/orientedW));
        var bitmap=new Bitmap(w,h,PixelFormat.Format24bppRgb);
        var bits=bitmap.LockBits(new Rectangle(0,0,w,h),ImageLockMode.WriteOnly,PixelFormat.Format24bppRgb);
        for(int y=0;y<h;y++) for(int x=0;x<w;x++) {
            double nx=(x+.5)/w,ny=(y+.5)/h;if(settings.Mirror) nx=1-nx;
            (double sx,double sy)=settings.Rotation switch {90=>(ny,1-nx),180=>(1-nx,1-ny),270=>(1-ny,nx),_=>(nx,ny)};
            int ix=Math.Clamp((int)(sx*sourceWidth),0,sourceWidth-1),iy=Math.Clamp((int)(sy*sourceHeight),0,sourceHeight-1);
            int uv=sourceWidth*sourceHeight+iy/2*sourceWidth+(ix&~1);
            byte* pixel=(byte*)bits.Scan0+y*bits.Stride+x*3;
            WriteRgb(pixel,yLut[source[iy*sourceWidth+ix]],uLut[source[uv]],vLut[source[uv+1]]);
        }
        bitmap.UnlockBits(bits);return bitmap;
    }
    static int Clamp(int v)=>v<0?0:v>255?255:v;
    static unsafe void WriteRgb(byte* pixel,int y,int u,int v)
    {
        int c=298*(y-16),d=u-128,e=v-128;
        pixel[0]=(byte)Clamp((c+541*d+128)>>8);pixel[1]=(byte)Clamp((c-55*d-136*e+128)>>8);pixel[2]=(byte)Clamp((c+459*e+128)>>8);
    }
    public static unsafe Bitmap ToBitmap(byte[] source,int width,int height)
    {
        double scale=Math.Min(1,1440d/Math.Max(width,height));int previewWidth=Math.Max(1,(int)Math.Round(width*scale)),previewHeight=Math.Max(1,(int)Math.Round(height*scale));
        var bitmap=new Bitmap(previewWidth,previewHeight,PixelFormat.Format24bppRgb);
        var bits=bitmap.LockBits(new Rectangle(0,0,previewWidth,previewHeight),ImageLockMode.WriteOnly,PixelFormat.Format24bppRgb);
        fixed(byte* from=source) {
            byte* uv=from+width*height;
            for(int y=0;y<previewHeight;y++) { int sy=Math.Min(height-1,(int)((y+.5)*height/previewHeight));byte* to=(byte*)bits.Scan0+y*bits.Stride;for(int x=0;x<previewWidth;x++){int sx=Math.Min(width-1,(int)((x+.5)*width/previewWidth));WriteRgb(to+x*3,from[sy*width+sx],uv[sy/2*width+(sx&~1)],uv[sy/2*width+(sx&~1)+1]);} }
        }
        bitmap.UnlockBits(bits);return bitmap;
    }
}
