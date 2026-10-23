using System.Diagnostics;
using System.Text.Json;

namespace PixelCam;

static class OrientationTests
{
    public static void RunRecorded(string clip,string path)=>RunRecordedChecks(clip,path).GetAwaiter().GetResult();

    static async Task RunRecordedChecks(string clip,string path)
    {
        var checks=new Dictionary<string,object>{{"phoneAccessed",false},{"source",clip}};
        using var decoder=new Decoder(clip,1280,720,recorded:true){Settings=ProcessingSettings.Neutral};
        string decoderError="";decoder.Error+=message=>decoderError=message;Task running=decoder.Start();
        try {
            async Task WaitForOrientation(int angle,int manual=0)
            {
                long before=Interlocked.Read(ref decoder.Frames);decoder.SourceRotation=angle;
                int expectedWidth=(angle+manual)%180==0?1280:720,expectedHeight=(angle+manual)%180==0?720:1280;
                var watch=Stopwatch.StartNew();
                while(watch.Elapsed.TotalSeconds<20) {
                    if(decoder.BackgroundError!="")throw new IOException(decoder.BackgroundError);
                    if(Interlocked.Read(ref decoder.Frames)>=before+8 && decoder.AppliedSourceRotation==angle && decoder.OutputWidth==expectedWidth && decoder.OutputHeight==expectedHeight)return;
                    await Task.Delay(40);
                }
                throw new TimeoutException($"Orientation{angle} did not produce expected{expectedWidth}x{expectedHeight} output. {decoderError}");
            }
            foreach(int angle in new[]{0,90,180,270,0}) {
                await WaitForOrientation(angle);checks[$"hotRotation{angle}WithoutAi"]=true;
            }
            checks["allOffTurnsRecreatedContexts"]=decoder.OrientationChanges==4;
            decoder.Settings=ProcessingSettings.Neutral with{Background=BackgroundEffect.Blur};
            long previous=decoder.BackgroundFrames;
            foreach(int angle in new[]{90,180,270,0}) {
                await WaitForOrientation(angle);
                long now=decoder.BackgroundFrames;checks[$"hotRotation{angle}WithBlur"]=now>previous && decoder.BackgroundError=="";previous=now;
            }
            checks["allBlurTurnsRecreatedContexts"]=decoder.OrientationChanges==8;
            decoder.Settings=decoder.Settings with{EyeContact=true};
            long previousEyes=decoder.EyeFrames;
            foreach(int angle in new[]{0,180,90,0}) {
                await WaitForOrientation(angle);
                long now=decoder.EyeFrames;
                checks[$"hotRotation{angle}WithEyesAndBlur"]=now>previousEyes && decoder.EyeError=="" && decoder.BackgroundError=="";previousEyes=now;
            }
            decoder.Settings=decoder.Settings with{Rotation=90};
            await WaitForOrientation(0,90);checks["manualRotationAfterAuto"]=true;
            await WaitForOrientation(90,90);checks["manualAndAutoCompose"]=true;
            checks["noDecoderRestart"]=decoder.ProcessLaunches==1;
            checks["backgroundFramesMonotonic"]=decoder.BackgroundFrames>=previous;
            checks["eyeFramesMonotonic"]=decoder.EyeFrames>=previousEyes;
            checks["backgroundFrames"]=decoder.BackgroundFrames;checks["eyeFrames"]=decoder.EyeFrames;checks["frames"]=decoder.Frames;
            checks["passed"]=checks.Where(pair=>pair.Value is bool && pair.Key!="phoneAccessed").All(pair=>(bool)pair.Value);
            if(!(bool)checks["passed"])Environment.ExitCode=2;
        }catch(Exception ex){checks["passed"]=false;checks["error"]=ex.ToString();Environment.ExitCode=1;}
        finally{decoder.Stop();await running;}
        File.WriteAllText(path,JsonSerializer.Serialize(checks,new JsonSerializerOptions(Program.Json){WriteIndented=true}));
    }

    public static void Run(string path)
    {
        var checks=new Dictionary<string,object>{{"phoneAccessed",false}};
        try {
            byte[] source={1,2,3,4,5,6,7,8,11,21,12,22};
            var original=source.ToArray();byte[]? buffer=null;
            var zero=Nv12Orientation.Rotate(source,4,2,0,ref buffer);
            checks["zeroCopiesNoPixels"]=ReferenceEquals(zero.Data,source) && zero.Width==4 && zero.Height==2;
            var expected=new Dictionary<int,byte[]> {
                [90]=new byte[]{5,1,6,2,7,3,8,4,11,21,12,22},
                [180]=new byte[]{8,7,6,5,4,3,2,1,12,22,11,21},
                [270]=new byte[]{4,8,3,7,2,6,1,5,12,22,11,21}
            };
            foreach(int angle in new[]{90,180,270}) {
                var rotated=Nv12Orientation.Rotate(source,4,2,angle,ref buffer);
                checks[$"clockwise{angle}ExactBytes"]=rotated.Data.SequenceEqual(expected[angle]);
                checks[$"clockwise{angle}Dimensions"]=rotated.Width==(angle==180?4:2) && rotated.Height==(angle==180?2:4);
                byte[]? restoredBuffer=null;
                var restored=Nv12Orientation.Rotate(rotated.Data,rotated.Width,rotated.Height,360-angle,ref restoredBuffer);
                checks[$"clockwise{angle}InverseRestoresSource"]=restored.Width==4 && restored.Height==2 && restored.Data.SequenceEqual(source);
            }
            // Four distinct UV pairs exercise both chroma axes and U/V order.
            byte[] square=Enumerable.Range(1,16).Select(i=>(byte)i).Concat(new byte[]{31,91,32,92,33,93,34,94}).ToArray();
            var expectedUv=new Dictionary<int,byte[]> {
                [90]=new byte[]{33,93,31,91,34,94,32,92},
                [180]=new byte[]{34,94,33,93,32,92,31,91},
                [270]=new byte[]{32,92,34,94,31,91,33,93}
            };
            foreach(int angle in new[]{90,180,270}) {
                var rotated=Nv12Orientation.Rotate(square,4,4,angle,ref buffer);
                checks[$"chromaPairs{angle}"]=rotated.Data.AsSpan(16).SequenceEqual(expectedUv[angle]);
            }
            byte[] rectangular=Enumerable.Range(0,36).Select(i=>(byte)(i*7)).ToArray();
            var frame=new VideoFrame(rectangular,6,4);
            for(int i=0;i<4;i++) {
                byte[]? next=null;
                frame=Nv12Orientation.Rotate(frame.Data,frame.Width,frame.Height,90,ref next);
            }
            checks["fourQuarterTurnsRestoreAllBytes"]=frame.Width==6 && frame.Height==4 && frame.Data.SequenceEqual(rectangular);
            checks["sourceNotMutated"]=source.SequenceEqual(original);
            checks["negativeAngleNormalized"]=Nv12Orientation.Normalize(-90)==270 && Nv12Orientation.Normalize(450)==90;
            bool invalid=false;try{Nv12Orientation.Normalize(45);}catch(ArgumentOutOfRangeException){invalid=true;}
            checks["nonQuarterTurnRejected"]=invalid;
            foreach(var size in new[]{(1920,1080),(3840,2160)}) {
                var input=new byte[size.Item1*size.Item2*3/2];new Random(42).NextBytes(input);
                byte[]? output=null;var timings=new List<double>();
                for(int i=0;i<12;i++) {
                    long start=Stopwatch.GetTimestamp();Nv12Orientation.Rotate(input,size.Item1,size.Item2,90,ref output);
                    if(i>2)timings.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                }
                timings.Sort();checks[$"rotation90At{size.Item1}"]=new{medianMs=Math.Round(timings[timings.Count/2],2),p95Ms=Math.Round(timings[(int)(timings.Count*.95)],2)};
            }
            checks["passed"]=checks.Where(pair=>pair.Value is bool && pair.Key!="phoneAccessed").All(pair=>(bool)pair.Value);
            if(!(bool)checks["passed"])Environment.ExitCode=2;
        }catch(Exception ex){checks["passed"]=false;checks["error"]=ex.ToString();Environment.ExitCode=1;}
        File.WriteAllText(path,JsonSerializer.Serialize(checks,new JsonSerializerOptions(Program.Json){WriteIndented=true}));
    }
}
