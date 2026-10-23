using System.Diagnostics;
using System.Text.Json;
using System.Drawing.Imaging;

namespace PixelCam;

sealed record RecordedVideo(string Path,int Width,int Height,double Fps)
{
    public static async Task<RecordedVideo> Probe(string path)
    {
        path=System.IO.Path.GetFullPath(path);
        if(!File.Exists(path))throw new IOException("Video file not found: "+path);
        string json=await Program.Run(Program.Tool("ffprobe.exe"),new[]{"-v","error","-select_streams","v:0","-show_entries","stream=width,height,avg_frame_rate","-of","json",path});
        using var document=JsonDocument.Parse(json);
        var stream=document.RootElement.GetProperty("streams").EnumerateArray().First();
        int width=stream.GetProperty("width").GetInt32(),height=stream.GetProperty("height").GetInt32();
        var fraction=stream.GetProperty("avg_frame_rate").GetString()!.Split('/');
        double fps=double.Parse(fraction[0],System.Globalization.CultureInfo.InvariantCulture)/double.Parse(fraction[1],System.Globalization.CultureInfo.InvariantCulture);
        return new(path,width&~1,height&~1,double.IsFinite(fps)&&fps>0?fps:30);
    }
    public static ProcessStartInfo DecodeStart(string path,int width,int height,bool loop=false,bool realtime=false)
    {
        var start=new ProcessStartInfo(Program.Tool("ffmpeg.exe")){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        var arguments=new List<string>{"-hide_banner","-loglevel","error"};
        if(loop)arguments.AddRange(new[]{"-stream_loop","-1"});if(realtime)arguments.Add("-re");
        arguments.AddRange(new[]{"-noautorotate","-i",path,"-an","-vf",$"scale={width}:{height}:{Program.DecodeColorOptions}","-pix_fmt","nv12","-f","rawvideo","pipe:1"});
        foreach(string argument in arguments)start.ArgumentList.Add(argument);return start;
    }
    // No phone APIs occur here. Diagnostic images come exclusively from the supplied file.
    public static async Task Test(string[] args)
    {
        string report=args[2];Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(report))!);
        var results=new Dictionary<string,object>();
        try
        {
            var video=await Probe(args[1]);int width=args.Length>3?int.Parse(args[3]):video.Width,height=args.Length>4?int.Parse(args[4]):width*video.Height/video.Width&~1;
            results["source"]=video;results["testedWidth"]=width;results["testedHeight"]=height;results["phoneAccessed"]=false;
            using var effects=new MaxineEffects(width,height);
            using var process=Process.Start(DecodeStart(video.Path,width,height))!;
            Task<string> errors=process.StandardError.ReadToEndAsync();
            byte[] source=new byte[width*height*3/2],mask=new byte[width*height];
            var settings=ProcessingSettings.Neutral with{Background=BackgroundEffect.Blur};
            var durations=new List<double>();long foreground=0,background=0,soft=0;int frames=0;
            while(frames<100)
            {
                try{await process.StandardOutput.BaseStream.ReadExactlyAsync(source);}catch(EndOfStreamException){break;}
                byte[] output=effects.Process(source,settings,mask);frames++;
                if(frames>5)durations.Add(effects.LastMilliseconds);
                if(frames==30)
                {
                    using var original=FrameProcessor.ToBitmap(source,width,height);original.Save(report+".source.png",ImageFormat.Png);
                    using var blurred=FrameProcessor.ToBitmap(output,width,height);blurred.Save(report+".blur.png",ImageFormat.Png);
                    double bgDelta=0,fgDelta=0;long bgCount=0,fgCount=0;
                    for(int i=0;i<mask.Length;i++){if(mask[i]<16){bgDelta+=Math.Abs(output[i]-source[i]);bgCount++;}if(mask[i]>239){fgDelta+=Math.Abs(output[i]-source[i]);fgCount++;}}
                    results["blurBackgroundLumaChange"]=bgDelta/Math.Max(1,bgCount);results["blurForegroundLumaChange"]=fgDelta/Math.Max(1,fgCount);
                    for(int i=0;i<mask.Length;i++){if(mask[i]<16)background++;else if(mask[i]>239)foreground++;else soft++;}
                    byte[] maskNv12=new byte[source.Length];Array.Copy(mask,maskNv12,mask.Length);Array.Fill(maskNv12,(byte)128,mask.Length,maskNv12.Length-mask.Length);
                    using var maskImage=FrameProcessor.ToBitmap(maskNv12,width,height);maskImage.Save(report+".mask.png",ImageFormat.Png);
                    string replacement=report+".background.png";
                    using(var image=new Bitmap(640,360)) {using var g=Graphics.FromImage(image);g.Clear(Color.FromArgb(24,48,100));using var brush=new System.Drawing.Drawing2D.LinearGradientBrush(new Rectangle(0,0,640,360),Color.FromArgb(25,50,95),Color.FromArgb(60,120,150),35);g.FillRectangle(brush,0,0,640,360);image.Save(replacement,ImageFormat.Png);}
                    byte[] replaced=effects.Process(source,settings with{Background=BackgroundEffect.Replace,BackgroundImage=replacement});
                    bgDelta=fgDelta=0;for(int i=0;i<mask.Length;i++){if(mask[i]<16)bgDelta+=Math.Abs(replaced[i]-source[i]);if(mask[i]>239)fgDelta+=Math.Abs(replaced[i]-source[i]);}
                    results["replacementBackgroundLumaChange"]=bgDelta/Math.Max(1,bgCount);results["replacementForegroundLumaChange"]=fgDelta/Math.Max(1,fgCount);
                    using var replacedImage=FrameProcessor.ToBitmap(replaced,width,height);replacedImage.Save(report+".replace.png",ImageFormat.Png);
                }
            }
            if(!process.HasExited)process.Kill(true);await process.WaitForExitAsync();
            durations.Sort();results["frames"]=frames;results["foregroundPixels"]=foreground;results["backgroundPixels"]=background;results["softEdgePixels"]=soft;
            results["medianMs"]=durations.Count>0?durations[durations.Count/2]:0;results["p95Ms"]=durations.Count>0?durations[(int)((durations.Count-1)*.95)]:0;
            results["passed"]=frames>=60 && foreground>mask.Length*.1 && background>mask.Length*.1 && soft>0 && (double)results["blurBackgroundLumaChange"]>1 && (double)results["blurForegroundLumaChange"]<3 && (double)results["replacementBackgroundLumaChange"]>15 && (double)results["replacementForegroundLumaChange"]<3;
            if(!(bool)results["passed"])Environment.ExitCode=2;
        }
        catch(Exception ex){results["passed"]=false;results["error"]=ex.ToString();Environment.ExitCode=1;}
        File.WriteAllText(report,JsonSerializer.Serialize(results,new JsonSerializerOptions(Program.Json){WriteIndented=true}));
    }
}
