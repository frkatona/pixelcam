namespace PixelCam;

/// <summary>Lossless clockwise quarter turns of limited-range NV12. UV samples
/// remain interleaved pairs; rotating the chroma bytes independently swaps color.</summary>
static class Nv12Orientation
{
    static readonly ParallelOptions parallel=new(){MaxDegreeOfParallelism=Math.Min(8,Environment.ProcessorCount)};

    public static int Normalize(int clockwise)
    {
        int angle=(clockwise%360+360)%360;
        if(angle%90!=0)throw new ArgumentOutOfRangeException(nameof(clockwise),"Orientation must be a whole quarter turn.");
        return angle;
    }

    public static VideoFrame Rotate(byte[] source,int width,int height,int clockwise,ref byte[]? buffer)
    {
        int angle=Normalize(clockwise);
        if(width<=0 || height<=0 || (width&1)!=0 || (height&1)!=0)throw new ArgumentOutOfRangeException(nameof(width),"NV12 dimensions must be positive and even.");
        int pixels=checked(width*height),length=checked(pixels+pixels/2);
        if(source.Length!=length)throw new ArgumentException("NV12 buffer does not match its dimensions.",nameof(source));
        if(angle==0)return new VideoFrame(source,width,height);
        if(buffer==null || buffer.Length!=length || ReferenceEquals(buffer,source))buffer=new byte[length];
        int outputWidth=angle%180==0?width:height,outputHeight=angle%180==0?height:width;
        RotatePlane(source,buffer,0,width,height,1,angle);
        RotatePlane(source,buffer,pixels,width/2,height/2,2,angle);
        return new VideoFrame(buffer,outputWidth,outputHeight);
    }

    static void RotatePlane(byte[] source,byte[] destination,int offset,int width,int height,int elementBytes,int angle)
    {
        int outputWidth=angle%180==0?width:height,outputHeight=angle%180==0?height:width;
        int stride=width*elementBytes,outputStride=outputWidth*elementBytes;
        Parallel.For(0,(outputHeight+15)/16,parallel,block=> {
            for(int y=block*16;y<Math.Min(outputHeight,(block+1)*16);y++) {
                int sourceIndex,step;
                if(angle==90){sourceIndex=offset+(height-1)*stride+y*elementBytes;step=-stride;}
                else if(angle==270){sourceIndex=offset+(width-1-y)*elementBytes;step=stride;}
                else {sourceIndex=offset+(height-1-y)*stride+(width-1)*elementBytes;step=-elementBytes;}
                int destinationIndex=offset+y*outputStride;
                for(int x=0;x<outputWidth;x++,sourceIndex+=step,destinationIndex+=elementBytes) {
                    destination[destinationIndex]=source[sourceIndex];
                    if(elementBytes==2)destination[destinationIndex+1]=source[sourceIndex+1];
                }
            }
        });
    }
}
