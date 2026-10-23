using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

namespace PixelCam;

public enum BackgroundEffect { Off, Blur, Replace }

/// <summary>Owns a Maxine stream and its temporal mask state on the decoder thread.</summary>
sealed class MaxineEffects : IDisposable
{
    readonly int width,height;
    readonly byte[] output;
    IntPtr context;
    int loadedMode=-1;
    string loadedImage="";
    bool eyeFailed;
    ulong previousNativeEyeFrames;
    public string EyeError { get; private set; }="";
    public long EyeFrames { get; private set; }
    public long EyeFaceFrames { get; private set; }
    public int EyeFaceCount { get; private set; }
    public float EyeConfidence { get; private set; }
    public Rectangle EyeBounds { get; private set; }
    public static string RuntimePath=>MaxineSetup.RuntimeDirectory();
    public static bool Available => MaxineHardware.Current.Supported && File.Exists(Program.Tool("PixelCamMaxine.dll")) && File.Exists(Path.Combine(RuntimePath,"NVVideoEffects.dll")) && Directory.Exists(Path.Combine(RuntimePath,"models"));
    public static string ArRuntimePath=>MaxineSetup.RuntimeDirectory(true);
    public double LastMilliseconds { get; private set; }
    public long ProcessedFrames { get; private set; }
    static MaxineEffects()
    {
        NativeLibrary.SetDllImportResolver(typeof(MaxineEffects).Assembly,(name,assembly,path)=>name=="PixelCamMaxine"?NativeLibrary.Load(Program.Tool("PixelCamMaxine.dll")):IntPtr.Zero);
    }
    public MaxineEffects(int width,int height){this.width=width;this.height=height;output=new byte[checked(width*height*3/2)];}
    void EnsureContext(int mode)
    {
        if(context!=IntPtr.Zero && loadedMode==mode)return;
        DisposeContext();
        if(!MaxineHardware.Current.Supported)throw new IOException(MaxineHardware.Current.Message);
        if(!Available)throw new IOException("Maxine setup is needed. Open Background and choose Set up Maxine; other controls still work.");
        var error=new StringBuilder(2048);
        context=PcMaxineCreate(RuntimePath,width,height,mode,error,error.Capacity);
        if(context==IntPtr.Zero)throw new IOException("Maxine: "+error);
        loadedMode=mode;loadedImage="";eyeFailed=false;previousNativeEyeFrames=0;
    }
    public byte[] Process(byte[] source,ProcessingSettings settings,byte[]? diagnosticMask=null)
    {
        if(!settings.EyeContact){eyeFailed=false;EyeError="";EyeFaceCount=0;EyeConfidence=0;EyeBounds=Rectangle.Empty;}
        if(settings.Background==BackgroundEffect.Off && !settings.EyeContact){LastMilliseconds=0;return source;}
        long start=Stopwatch.GetTimestamp();EnsureContext(settings.SegmentationMode);
        var error=new StringBuilder(2048);
        if(settings.Background==BackgroundEffect.Replace && loadedImage!=settings.BackgroundImage)
        {
            byte[] background=LoadBackground(settings.BackgroundImage,width,height);
            if(PcMaxineBackground(context,background,error,error.Capacity)!=0)throw new IOException("Maxine: "+error);
            loadedImage=settings.BackgroundImage;
        }
        int result=PcMaxineProcessEyes(context,source,output,(int)settings.Background,(float)settings.BlurStrength,diagnosticMask,settings.EyeContact && !eyeFailed?1:0,ArRuntimePath,error,error.Capacity);
        if(result<0)throw new IOException("Maxine: "+error);
        if(result>0){EyeError=error.ToString();eyeFailed=true;}
        PcMaxineEyeStatus(context,out float confidence,out int faceCount,out ulong nativeFrames);
        ulong frameDelta=nativeFrames>=previousNativeEyeFrames?nativeFrames-previousNativeEyeFrames:nativeFrames;previousNativeEyeFrames=nativeFrames;
        EyeFrames+=(long)frameDelta;EyeConfidence=confidence;EyeFaceCount=faceCount;
        if(settings.EyeContact && !eyeFailed && frameDelta>0 && faceCount>0)EyeFaceFrames++;
        PcMaxineEyeBounds(context,out int left,out int top,out int right,out int bottom);EyeBounds=Rectangle.FromLTRB(left,top,right,bottom);
        LastMilliseconds=Stopwatch.GetElapsedTime(start).TotalMilliseconds;ProcessedFrames++;return output;
    }
    // Test-only baseline with the identical NV12/BGR conversion but no gaze change.
    public byte[] GpuRoundTrip(byte[] source)
    {
        EnsureContext(0);var error=new StringBuilder(2048);
        if(PcMaxineProcessEyes(context,source,output,0,.5f,null,0,ArRuntimePath,error,error.Capacity)<0)throw new IOException(error.ToString());
        return output;
    }
    public static byte[] LoadBackground(string path,int width,int height)
    {
        if(BackgroundPresets.IsPreset(path))return BackgroundPresets.CreatePixels(path,width,height);
        if(string.IsNullOrWhiteSpace(path)||!File.Exists(path))throw new IOException("Choose a background image for replacement.");
        using var image=Image.FromFile(path);
        using var canvas=new Bitmap(width,height,PixelFormat.Format24bppRgb);
        using(var g=Graphics.FromImage(canvas))
        {
            g.InterpolationMode=InterpolationMode.HighQualityBicubic;
            double scale=Math.Max(width/(double)image.Width,height/(double)image.Height);
            float w=(float)(image.Width*scale),h=(float)(image.Height*scale);
            g.DrawImage(image,(width-w)/2,(height-h)/2,w,h);
        }
        byte[] pixels=new byte[width*height*3];
        var bits=canvas.LockBits(new Rectangle(0,0,width,height),ImageLockMode.ReadOnly,PixelFormat.Format24bppRgb);
        try{for(int y=0;y<height;y++)Marshal.Copy(bits.Scan0+y*bits.Stride,pixels,y*width*3,width*3);}finally{canvas.UnlockBits(bits);}
        return pixels;
    }
    void DisposeContext(){if(context!=IntPtr.Zero){PcMaxineDestroy(context);context=IntPtr.Zero;}loadedMode=-1;loadedImage="";}
    public void Dispose()=>DisposeContext();
    public void Reset(){if(context!=IntPtr.Zero && PcMaxineReset(context)!=0)DisposeContext();}
    [DllImport("PixelCamMaxine",CallingConvention=CallingConvention.Cdecl)]static extern IntPtr PcMaxineCreate([MarshalAs(UnmanagedType.LPUTF8Str)]string path,int width,int height,int mode,StringBuilder error,int length);
    [DllImport("PixelCamMaxine",CallingConvention=CallingConvention.Cdecl)]static extern void PcMaxineDestroy(IntPtr context);
    [DllImport("PixelCamMaxine",CallingConvention=CallingConvention.Cdecl)]static extern int PcMaxineReset(IntPtr context);
    [DllImport("PixelCamMaxine",CallingConvention=CallingConvention.Cdecl)]static extern int PcMaxineBackground(IntPtr context,byte[] pixels,StringBuilder error,int length);
    [DllImport("PixelCamMaxine",CallingConvention=CallingConvention.Cdecl)]static extern int PcMaxineProcess(IntPtr context,byte[] input,[Out]byte[] output,int effect,float strength,[Out]byte[]? alpha,StringBuilder error,int length);
    [DllImport("PixelCamMaxine",CallingConvention=CallingConvention.Cdecl)]static extern int PcMaxineProcessEyes(IntPtr context,byte[] input,[Out]byte[] output,int effect,float strength,[Out]byte[]? alpha,int eyeEnabled,[MarshalAs(UnmanagedType.LPUTF8Str)]string arPath,StringBuilder error,int length);
    [DllImport("PixelCamMaxine",CallingConvention=CallingConvention.Cdecl)]static extern int PcMaxineEyeStatus(IntPtr context,out float confidence,out int faceCount,out ulong processedFrames);
    [DllImport("PixelCamMaxine",CallingConvention=CallingConvention.Cdecl)]static extern int PcMaxineEyeBounds(IntPtr context,out int left,out int top,out int right,out int bottom);
}
