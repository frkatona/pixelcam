using System.Diagnostics;
using System.Drawing.Imaging;
using System.Text.Json;

namespace PixelCam;

static class EyeContactTests
{
    // All saved images are derived solely from the explicitly supplied recording.
    public static async Task Run(string[] args)
    {
        string report=Path.GetFullPath(args[2]);Directory.CreateDirectory(Path.GetDirectoryName(report)!);
        var results=new Dictionary<string,object>{{"phoneAccessed",false}};
        try
        {
            var video=await RecordedVideo.Probe(args[1]);int width=args.Length>3?int.Parse(args[3]):1280,height=(width*video.Height/video.Width)&~1;
            results["source"]=video;results["width"]=width;results["height"]=height;
            results["defaultOff"]=!new ProcessingSettings().EyeContact;
            using var effects=new MaxineEffects(width,height);using var baselineEffects=new MaxineEffects(width,height);
            using var decoder=Process.Start(RecordedVideo.DecodeStart(video.Path,width,height))!;
            Task<string> stderr=decoder.StandardError.ReadToEndAsync();byte[] source=new byte[width*height*3/2];
            var settings=ProcessingSettings.Neutral with{EyeContact=true};var timings=new List<double>();
            int frames=0,faceFrames=0,localizedFrames=0;long changedInside=0,changedOutside=0;bool saved=false;
            try
            {
                while(frames<100)
                {
                    try{await decoder.StandardOutput.BaseStream.ReadExactlyAsync(source);}catch(EndOfStreamException){break;}
                    byte[] corrected=effects.Process(source,settings).ToArray();frames++;
                    if(effects.EyeError!="")throw new IOException(effects.EyeError);
                    if(frames>5)timings.Add(effects.LastMilliseconds);
                    if(effects.EyeFaceCount>0)
                    {
                        faceFrames++;Rectangle eyes=Rectangle.Intersect(new Rectangle(0,0,width,height),effects.EyeBounds);
                        if(frames%5==0 && eyes.Width>0 && eyes.Height>0)
                        {
                            byte[] baseline=baselineEffects.GpuRoundTrip(source);long inside=0,outside=0;
                            // Same conversion in both paths: significant changes should be localized to eyes.
                            for(int y=0;y<height;y++)for(int x=0;x<width;x++)if(Math.Abs(corrected[y*width+x]-baseline[y*width+x])>3){if(eyes.Contains(x,y))inside++;else outside++;}
                            changedInside+=inside;changedOutside+=outside;if(inside>5)localizedFrames++;
                            if(!saved && inside>20)
                            {
                                using var original=FrameProcessor.ToBitmap(baseline,width,height);original.Save(report+".before.png",ImageFormat.Png);
                                using var after=FrameProcessor.ToBitmap(corrected,width,height);after.Save(report+".after.png",ImageFormat.Png);
                                results["sampleEyeBounds"]=new{eyes.X,eyes.Y,eyes.Width,eyes.Height};saved=true;
                            }
                        }
                    }
                }
            }
            finally{if(!decoder.HasExited)decoder.Kill(true);await decoder.WaitForExitAsync();}
            results["frames"]=frames;results["sdkEyeFrames"]=effects.EyeFrames;results["faceFrames"]=faceFrames;results["localizedFrames"]=localizedFrames;results["changedEyePixels"]=changedInside;results["changedOutsideEyePixels"]=changedOutside;
            results["eyeRegionChanged"]=localizedFrames>0 && changedInside>20;
            results["changesLocalized"]=changedInside>changedOutside*4;
            results["eyeError"]=effects.EyeError;
            byte[] off=effects.Process(source,ProcessingSettings.Neutral);results["offExactPassthrough"]=ReferenceEquals(off,source) && off.SequenceEqual(source);effects.Reset();
            effects.Process(source,settings with{Background=BackgroundEffect.Blur});results["combinedBlur"]=effects.EyeError=="" && effects.EyeFaceCount>0;
            string background=report+".background.png";using(var image=new Bitmap(640,360)){using var graphics=Graphics.FromImage(image);graphics.Clear(Color.FromArgb(38,78,120));image.Save(background,ImageFormat.Png);}
            effects.Process(source,settings with{Background=BackgroundEffect.Replace,BackgroundImage=background});results["combinedReplacement"]=effects.EyeError=="" && effects.EyeFaceCount>0;
            timings.Sort();results["medianMs"]=timings.Count>0?timings[timings.Count/2]:0;results["p95Ms"]=timings.Count>0?timings[(int)((timings.Count-1)*.95)]:0;
            results["passed"]=frames>=60 && faceFrames>30 && results.Where(p=>p.Value is bool && p.Key!="phoneAccessed").All(p=>(bool)p.Value);
            if(!(bool)results["passed"])Environment.ExitCode=2;
        }
        catch(Exception ex){results["passed"]=false;results["error"]=ex.ToString();Environment.ExitCode=1;}
        File.WriteAllText(report,JsonSerializer.Serialize(results,new JsonSerializerOptions(Program.Json){WriteIndented=true}));
    }
}
