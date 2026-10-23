using System.Text.Json;

namespace PixelCam;

sealed record BackgroundChoice(string Id,string Label)
{
    public override string ToString()=>Label;
}

static class BackgroundPresets
{
    public const string Default="preset:neutral";
    public static readonly BackgroundChoice[] Choices={
        new("preset:black","Pure black"),
        new(Default,"Soft neutral gradient"),
        new("preset:slate","Slate gradient"),
        new("preset:warm","Warm gray gradient"),
        new("preset:white","Pure white"),
        new("custom","Custom image…")
    };
    public static bool IsPreset(string path)=>Choices.Any(c=>c.Id==path && c.Id!="custom");
    public static byte[] CreatePixels(string id,int width,int height)
    {
        (Color top,Color bottom)=id switch {
            "preset:black"=>(Color.Black,Color.Black),
            "preset:neutral"=>(Color.FromArgb(219,215,208),Color.FromArgb(169,175,178)),
            "preset:slate"=>(Color.FromArgb(67,82,103),Color.FromArgb(28,40,58)),
            "preset:warm"=>(Color.FromArgb(191,178,162),Color.FromArgb(115,110,105)),
            "preset:white"=>(Color.White,Color.White),
            _=>throw new IOException("Unknown background preset.")
        };
        var pixels=new byte[checked(width*height*3)];
        for(int y=0;y<height;y++) {
            double blend=height<=1?0:y/(double)(height-1);blend=blend*blend*(3-2*blend);
            byte b=(byte)Math.Round(top.B+(bottom.B-top.B)*blend),g=(byte)Math.Round(top.G+(bottom.G-top.G)*blend),r=(byte)Math.Round(top.R+(bottom.R-top.R)*blend);
            for(int x=0,p=y*width*3;x<width;x++,p+=3){pixels[p]=b;pixels[p+1]=g;pixels[p+2]=r;}
        }
        return pixels;
    }
    public static void Test(string path)
    {
        var checks=new Dictionary<string,object>();
        foreach(var choice in Choices.Where(c=>c.Id!="custom")) {
            byte[] pixels=MaxineEffects.LoadBackground(choice.Id,64,48);
            bool correct=pixels.Length==64*48*3;
            if(choice.Id=="preset:black")correct&=pixels.All(p=>p==0);
            else if(choice.Id=="preset:white")correct&=pixels.All(p=>p==255);
            else correct&=!pixels.Take(64*3).SequenceEqual(pixels.Skip(47*64*3));
            checks[choice.Id]=correct;
        }
        checks["passed"]=checks.Values.OfType<bool>().All(v=>v);
        File.WriteAllText(path,JsonSerializer.Serialize(checks,new JsonSerializerOptions{WriteIndented=true}));
        if(!(bool)checks["passed"])throw new IOException("Background preset check failed.");
    }
}
