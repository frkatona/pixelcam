using System.Diagnostics;
using System.Text.Json;

namespace PixelCam;

static class ProcessingTests
{
    static byte[] Pattern(int width,int height)
    {
        byte[] frame=new byte[width*height*3/2];
        for(int y=0;y<height;y++)for(int x=0;x<width;x++)frame[y*width+x]=(byte)(35+145*x/(double)width+35*y/(double)height);
        for(int i=width*height;i<frame.Length;i+=2){frame[i]=110;frame[i+1]=145;}return frame;
    }
    static double Mean(byte[] data,int count){long sum=0;for(int i=0;i<count;i++)sum+=data[i];return sum/(double)count;}
    static double Spread(byte[] data,int count){double mean=Mean(data,count),sum=0;for(int i=0;i<count;i++)sum+=(data[i]-mean)*(data[i]-mean);return Math.Sqrt(sum/count);}
    static void CheckDecodedColor(Dictionary<string,object> checks)
    {
        // Three constant raw frames exercise the same tagged-input normalization as the phone.
        // No camera or personal image is involved: black, white, and BT.709 full-range red.
        const int pixels=16*16,frameBytes=pixels*3/2;
        string scratch=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"PixelCam","test-scratch",Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);string input=Path.Combine(scratch,"full709.yuv"),output=Path.Combine(scratch,"limited601.nv12");
        try {
            byte[] frames=new byte[frameBytes*3];var samples=new[]{(Y:0,U:128,V:128),(Y:255,U:128,V:128),(Y:54,U:99,V:255)};
            for(int i=0;i<samples.Length;i++){
                int offset=i*frameBytes;Array.Fill(frames,(byte)samples[i].Y,offset,pixels);Array.Fill(frames,(byte)samples[i].U,offset+pixels,pixels/4);Array.Fill(frames,(byte)samples[i].V,offset+pixels+pixels/4,pixels/4);
            }
            File.WriteAllBytes(input,frames);
            Program.Run(Program.Tool("ffmpeg.exe"),new[]{"-hide_banner","-loglevel","error","-f","rawvideo","-pix_fmt","yuvj420p","-color_range","pc","-colorspace","bt709","-s","16x16","-framerate","1","-i",input,"-vf","scale="+Program.DecodeColorOptions,"-frames:v","3","-pix_fmt","nv12","-f","rawvideo",output}).GetAwaiter().GetResult();
            byte[] normalized=File.ReadAllBytes(output);
            bool Match(int frame,int y,int u,int v){
                if(normalized.Length!=frameBytes*3)return false;int offset=frame*frameBytes;
                for(int i=0;i<pixels;i++)if(Math.Abs(normalized[offset+i]-y)>1)return false;
                for(int i=0;i<pixels/2;i+=2)if(Math.Abs(normalized[offset+pixels+i]-u)>1 || Math.Abs(normalized[offset+pixels+i+1]-v)>1)return false;
                return true;
            }
            checks["decodedFullRangeBlackBecomesLimited16"]=Match(0,16,128,128);
            checks["decodedFullRangeWhiteBecomesLimited235"]=Match(1,235,128,128);
            checks["decodedBt709RedBecomesBt601Limited"]=Match(2,81,90,240);
            if(normalized.Length==frameBytes*3)checks["decodedNormalizationSamples"]=new{blackY=normalized[0],whiteY=normalized[frameBytes],redY=normalized[frameBytes*2],redU=normalized[frameBytes*2+pixels],redV=normalized[frameBytes*2+pixels+1]};
        }
        finally {
            // Delete only the two known synthetic fixtures, then the empty unique directory.
            try{File.Delete(input);File.Delete(output);Directory.Delete(scratch);}catch(IOException){}
        }
    }
    public static void Run(string path)
    {
        var checks=new Dictionary<string,object>();
        try {
            const int w=128,h=72;byte[] source=Pattern(w,h);var neutral=ProcessingSettings.Neutral;
            byte[] Render(ProcessingSettings settings)=>new FrameProcessor(w,h).Process(source,settings).Data.ToArray();
            var baseline=Render(neutral);checks["neutralPreservesLegalNv12"]=source.SequenceEqual(baseline);
            var bright=Render(neutral with{Brightness=.3});checks["manualBrightness"]=Mean(bright,w*h)>Mean(baseline,w*h)+15;
            var contrast=Render(neutral with{Contrast=1.4});checks["manualContrast"]=Spread(contrast,w*h)>Spread(baseline,w*h)+8;
            var sat=Render(neutral with{Saturation=1.5});checks["manualSaturation"]=Math.Abs(sat[w*h]-128)>Math.Abs(baseline[w*h]-128);
            var gamma=Render(neutral with{Gamma=1.5});checks["manualGamma"]=Mean(gamma,w*h)>Mean(baseline,w*h)+10;
            var warm=Render(neutral with{Warmth=.5});checks["manualWhiteBalance"]=warm[w*h]<baseline[w*h] && warm[w*h+1]>baseline[w*h+1];
            var left=Render(neutral with{Zoom=2,CenterX=.25});var right=Render(neutral with{Zoom=2,CenterX=.75});checks["zoomAndPanChooseDifferentImageRegions"]=Mean(right,w*h)>Mean(left,w*h)+50;
            var centered=new ProcessingSettings{Zoom=5,CenterX=-100,CenterY=100}.Bounded();checks["cropStaysInsideFullFrame"]=centered.CenterX==.1 && centered.CenterY==.9;
            var rotated=new FrameProcessor(w,h).Process(source,neutral with{Rotation=90,Mirror=true});checks["rotationDimensions"]=rotated.Width==h && rotated.Height==w && rotated.Data.Length==source.Length;
            var defaults=new ProcessingSettings();checks["allFiveManualDefaults"]=!defaults.AutoBrightness && !defaults.AutoContrast && !defaults.AutoSaturation && !defaults.AutoGamma && !defaults.AutoWhiteBalance;
            checks["freshManualSettingsPreserveLegalNv12"]=Render(defaults).SequenceEqual(source);
            byte[] dark=new byte[source.Length];Array.Fill(dark,(byte)60,0,w*h);for(int i=w*h;i<dark.Length;i+=2){dark[i]=140;dark[i+1]=120;}
            var automaticSettings=defaults with{AutoBrightness=true,AutoContrast=true,AutoSaturation=true,AutoGamma=true,AutoWhiteBalance=true};
            var automatic=new FrameProcessor(w,h);VideoFrame adjusted=default;for(int i=0;i<60;i++)adjusted=automatic.Process(dark,automaticSettings);
            checks["automaticSettingsActuallyAdapt"]=Mean(adjusted.Data,w*h)>72 && automatic.Effective.Contrast>=.88 && automatic.Effective.Saturation>1 && Math.Abs(automatic.Effective.Warmth)>.05;
            CheckDecodedColor(checks);
            foreach(var dimension in new[]{(1920,1080),(3840,2160)}) {
                byte[] input=Pattern(dimension.Item1,dimension.Item2);
                foreach(double zoom in new[]{1d,2d}) {
                    var processor=new FrameProcessor(dimension.Item1,dimension.Item2);var config=new ProcessingSettings{Zoom=zoom,CenterX=.6,CenterY=.5};var timings=new List<double>();
                    for(int i=0;i<16;i++){processor.Process(input,config);if(i>3)timings.Add(processor.LastMilliseconds);}
                    timings.Sort();checks[$"processing{dimension.Item1}Zoom{zoom}"]=new{medianMs=Math.Round(timings[timings.Count/2],2),p95Ms=Math.Round(timings[(int)(timings.Count*.95)],2)};
                }
            }
            checks["passed"]=checks.Where(pair=>pair.Value is bool).All(pair=>(bool)pair.Value);
            if(!(bool)checks["passed"])Environment.ExitCode=2;
        }catch(Exception ex){checks["passed"]=false;checks["error"]=ex.ToString();Environment.ExitCode=1;}
        File.WriteAllText(path,JsonSerializer.Serialize(checks,new JsonSerializerOptions(Program.Json){WriteIndented=true}));
    }
}
