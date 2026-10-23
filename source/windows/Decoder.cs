using System.Diagnostics;

namespace PixelCam;

sealed class Decoder : IDisposable
{
    readonly string url; readonly int width,height; readonly bool recorded; readonly int fps;
    readonly CancellationTokenSource cancellation=new();
    FrameProcessor processor;
    MaxineEffects effects;
    byte[]? orientedBuffer;
    int requestedSourceRotation,appliedSourceRotation,orientationChanges,outputWidth,outputHeight;
    int requestedEffectsReload,appliedEffectsReload;
    long backgroundFrames,eyeFrames,eyeFaceFrames,previousBackgroundFrames,previousEyeFrames,previousEyeFaceFrames;
    ProcessingSettings? failedBackgroundSettings;
    bool backgroundWasActive;
    public string BackgroundError="";
    public double BackgroundMilliseconds=>effects.LastMilliseconds;
    public long BackgroundFrames=>Interlocked.Read(ref backgroundFrames);
    public string EyeError=>effects.EyeError;
    public long EyeFrames=>Interlocked.Read(ref eyeFrames);
    public long EyeFaceFrames=>Interlocked.Read(ref eyeFaceFrames);
    public int EyeFaceCount=>effects.EyeFaceCount;
    public float EyeConfidence=>effects.EyeConfidence;
    ProcessingSettings settings;
    Process? process; WebcamWriter? webcam; int webcamWidth,webcamHeight;
    public volatile bool WebcamEnabled;
    public string WebcamError="";public long WebcamFrames,Frames;public int ProcessLaunches;
    public EffectiveColor Effective => processor.Effective;
    public double ProcessingMilliseconds => processor.LastMilliseconds;
    public ProcessingSettings Settings { get=>Volatile.Read(ref settings);set=>Volatile.Write(ref settings,value.Bounded()); }
    // The phone's physical correction is separate from the user's manual
    // rotation. Apply it before AI so gaze/segmentation see an upright subject.
    public int SourceRotation { get=>Volatile.Read(ref requestedSourceRotation);set=>Volatile.Write(ref requestedSourceRotation,Nv12Orientation.Normalize(value)); }
    public int AppliedSourceRotation=>Volatile.Read(ref appliedSourceRotation);
    public int OrientationChanges=>Volatile.Read(ref orientationChanges);
    public int OutputWidth=>Volatile.Read(ref outputWidth);
    public int OutputHeight=>Volatile.Read(ref outputHeight);
    public void ReloadEffects()=>Interlocked.Increment(ref requestedEffectsReload);
    public event Action<Bitmap>? Preview,FullFrame;
    public event Action? Frame;
    public event Action<string>? Error;
    public Decoder(string url,int width,int height,int rotation=0,bool mirror=false,bool recorded=false,int fps=30)
    {
        this.url=url;this.width=width;this.height=height;this.recorded=recorded;this.fps=Math.Max(1,fps);processor=new(width,height);effects=new(width,height);settings=new(){Rotation=rotation,Mirror=mirror};outputWidth=rotation%180==0?width:height;outputHeight=rotation%180==0?height:width;
    }
    public Task Start()=>Task.Run(Run);
    void Run()
    {
        var previewClock=Stopwatch.StartNew();var navigatorClock=Stopwatch.StartNew();var webcamRetry=Stopwatch.StartNew();
        while(!cancellation.IsCancellationRequested) {
            try {
                var start=recorded?RecordedVideo.DecodeStart(url,width,height,true,true):new ProcessStartInfo(Program.Tool("ffmpeg.exe")){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
                if(!recorded)foreach(string argument in new[]{"-hide_banner","-loglevel","error","-fflags","nobuffer","-flags","low_delay","-probesize","32","-analyzeduration","0","-rw_timeout","8000000","-f","h264","-i",url+"/video.h264","-an","-vsync","0","-vf","scale="+Program.DecodeColorOptions,"-pix_fmt","nv12","-f","rawvideo","pipe:1"}) start.ArgumentList.Add(argument);
                using var current=Process.Start(start)??throw new IOException("The video decoder could not start.");process=current;Interlocked.Increment(ref ProcessLaunches);
                if(cancellation.IsCancellationRequested){try{current.Kill(true);}catch{}break;}
                string lastError="";current.ErrorDataReceived+=(_,e)=>{if(!string.IsNullOrWhiteSpace(e.Data))lastError=e.Data;};current.BeginErrorReadLine();
                byte[] source=new byte[checked(width*height*3/2)];
                while(!cancellation.IsCancellationRequested) {
                    current.StandardOutput.BaseStream.ReadExactly(source);
                    ProcessingSettings snapshot=Settings;
                    int sourceRotation=SourceRotation;
                    VideoFrame oriented=Nv12Orientation.Rotate(source,width,height,sourceRotation,ref orientedBuffer);
                    int reload=Volatile.Read(ref requestedEffectsReload);
                    if(appliedSourceRotation!=sourceRotation || appliedEffectsReload!=reload) {
                        // Even a180-degree turn must clear temporal gaze/mask
                        // state, although its dimensions have not changed.
                        effects.Dispose();effects=new MaxineEffects(oriented.Width,oriented.Height);processor=new FrameProcessor(oriented.Width,oriented.Height);
                        if(appliedSourceRotation!=sourceRotation)Interlocked.Increment(ref orientationChanges);Volatile.Write(ref appliedSourceRotation,sourceRotation);appliedEffectsReload=reload;backgroundWasActive=false;failedBackgroundSettings=null;BackgroundError="";
                        previousBackgroundFrames=previousEyeFrames=previousEyeFaceFrames=0;
                    }
                    byte[] effected=oriented.Data;
                    if(snapshot.Background==BackgroundEffect.Off && !snapshot.EyeContact){if(backgroundWasActive)effects.Reset();backgroundWasActive=false;effects.Process(oriented.Data,snapshot);BackgroundError="";failedBackgroundSettings=null;}
                    else if(failedBackgroundSettings==null || failedBackgroundSettings.Background!=snapshot.Background || failedBackgroundSettings.EyeContact!=snapshot.EyeContact || failedBackgroundSettings.SegmentationMode!=snapshot.SegmentationMode || failedBackgroundSettings.BlurStrength!=snapshot.BlurStrength || failedBackgroundSettings.BackgroundImage!=snapshot.BackgroundImage)
                    {
                        try{effected=effects.Process(oriented.Data,snapshot);backgroundWasActive=true;BackgroundError="";failedBackgroundSettings=null;}
                        catch(Exception ex){BackgroundError=ex.Message+" Original background shown.";failedBackgroundSettings=snapshot;}
                    }
                    RecordEffectCounters();
                    VideoFrame output=processor.Process(effected,snapshot);
                    Volatile.Write(ref outputWidth,output.Width);Volatile.Write(ref outputHeight,output.Height);
                    if(webcam!=null && (!WebcamEnabled || webcamWidth!=output.Width || webcamHeight!=output.Height)) {webcam.Dispose();webcam=null;webcamRetry.Restart();}
                    if(WebcamEnabled && webcam==null && webcamRetry.ElapsedMilliseconds>120) {
                        try {webcam=new WebcamWriter(output.Width,output.Height,fps);webcamWidth=output.Width;webcamHeight=output.Height;WebcamError="";}
                        catch(IOException){WebcamError="The webcam is in use by another publisher. Close OBS virtual camera or reselect PixelCam in your video app.";webcamRetry.Restart();}
                    }
                    if(webcam!=null){webcam.Write(output.Data);Interlocked.Increment(ref WebcamFrames);}
                    Interlocked.Increment(ref Frames);Frame?.Invoke();
                    if(previewClock.ElapsedMilliseconds>=100 && Preview!=null){Preview.Invoke(FrameProcessor.ToBitmap(output.Data,output.Width,output.Height));previewClock.Restart();}
                    if(navigatorClock.ElapsedMilliseconds>=200 && FullFrame!=null){FullFrame.Invoke(processor.Navigator(oriented.Data,snapshot));navigatorClock.Restart();}
                }
                if(!cancellation.IsCancellationRequested)Error?.Invoke(string.IsNullOrEmpty(lastError)?"Waiting for camera video…":lastError);
            }catch(Exception ex){if(!cancellation.IsCancellationRequested)Error?.Invoke(ex is EndOfStreamException?"Waiting for camera video…":ex.Message);}
            finally{process=null;effects.Reset();}
            if(!cancellation.IsCancellationRequested)Thread.Sleep(500);
        }
        webcam?.Dispose();webcam=null;effects.Dispose();
    }
    void RecordEffectCounters()
    {
        long current=effects.ProcessedFrames;Interlocked.Add(ref backgroundFrames,Math.Max(0,current-previousBackgroundFrames));previousBackgroundFrames=current;
        current=effects.EyeFrames;Interlocked.Add(ref eyeFrames,Math.Max(0,current-previousEyeFrames));previousEyeFrames=current;
        current=effects.EyeFaceFrames;Interlocked.Add(ref eyeFaceFrames,Math.Max(0,current-previousEyeFaceFrames));previousEyeFaceFrames=current;
    }
    public void Stop(){cancellation.Cancel();try{process?.Kill(true);}catch{}}
    public void Dispose(){Stop();cancellation.Dispose();}
}
