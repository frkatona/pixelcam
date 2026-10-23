// PixelCam's small C ABI bridge. NVIDIA's headers/proxies are kept unmodified in nvidia/.
// All calls for a context belong to one decoder thread; the UI only changes C# snapshots.
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <string>
#include <vector>
#include <stdexcept>
#include <cstring>
#include <algorithm>
#include <cmath>
#include "nvVideoEffects.h"
#include "nvAR.h"

char* g_nvVFXSDKPath = nullptr; // Used by NVIDIA's supplied proxy loader.
char* g_nvARSDKPath = nullptr;
static std::string runtimePath;
static std::string arRuntimePath;
static HMODULE arLibrary = nullptr;
static bool arSearchConfigured = false;
static std::wstring widePath(const std::string& value) {
    int count=MultiByteToWideChar(CP_UTF8,MB_ERR_INVALID_CHARS,value.c_str(),-1,nullptr,0);
    if(count<1)throw std::runtime_error("The NVIDIA runtime path is not valid UTF-8.");
    std::wstring result(count,L'\0');
    MultiByteToWideChar(CP_UTF8,MB_ERR_INVALID_CHARS,value.c_str(),-1,result.data(),count);
    result.resize(count-1);return result;
}
static void loadArLibrary(const char* path) {
    if(arLibrary)return;
    std::wstring library=widePath(std::string(path)+"\\nvARPose.dll");
    if(GetFileAttributesW(library.c_str())==INVALID_FILE_ATTRIBUTES)throw std::runtime_error("Eye contact: NVIDIA AR runtime not found at "+std::string(path));
    if(!arSearchConfigured) {
        // NVIDIA's proxies alternate SetDllDirectory between SDK roots. Keep both
        // registered for delayed dependency loading without relying on PATH/CWD.
        if(!SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_DEFAULT_DIRS))throw std::runtime_error("Could not configure NVIDIA DLL search directories.");
        for(const auto& root:{runtimePath,std::string(path)}) {
            auto directory=widePath(root);
            if(!AddDllDirectory(directory.c_str()))throw std::runtime_error("Could not add NVIDIA DLL directory: "+root);
        }
        arSearchConfigured=true;
    }
    arLibrary=LoadLibraryExW(library.c_str(),nullptr,LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR|LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
    if(!arLibrary)throw std::runtime_error("Eye contact: NVIDIA AR runtime dependencies could not load (Windows error "+std::to_string(GetLastError())+").");
}
static void check(NvCV_Status code, const char* operation) {
    if (code != NVCV_SUCCESS) {
        const char* description = NvCV_GetErrorStringFromCode(code);
        throw std::runtime_error(std::string(operation) + ": " + (description ? description : "SDK error") + " (" + std::to_string(code) + ")");
    }
}
static void errorText(char* output, int length, const std::exception& error) {
    if (output && length > 0) strncpy_s(output, length, error.what(), _TRUNCATE);
}
using Synchronize = int (__cdecl*)(CUstream);

// One AR feature owns its CPU output arrays for the feature's complete lifetime.
// The input/output image pixels remain on the VFX stream's GPU buffers.
struct EyeContact {
    NvAR_FeatureHandle feature = nullptr;
    NvCVImage output;
    NvAR_BBoxes boxes{};
    NvAR_Rect faceBox{};
    std::vector<NvAR_Point2f> landmarks, redirectedLandmarks;
    std::vector<float> confidence;
    NvAR_Point3f gazeDirection[2]{};
    NvAR_Quaternion headPose{};
    float gazeAngles[2]{}, headTranslation[3]{};
    float meanConfidence = 0;
    int faceCount = 0;
    int left=0,top=0,right=0,bottom=0;
    ~EyeContact() { if(feature)NvAR_Destroy(feature); NvCVImage_Dealloc(&output); }
    void init(const char* path,NvCVImage& input,CUstream stream) {
        if(!path || !path[0])throw std::runtime_error("Eye contact: open Background and choose Set up Eye contact.");
        if(arRuntimePath.empty()) {
            arRuntimePath=path; g_nvARSDKPath=arRuntimePath.data();
        } else if(arRuntimePath!=path)throw std::runtime_error("Restart PixelCam to change the NVIDIA AR runtime folder.");
        loadArLibrary(path);
        unsigned version=0;
        check(NvAR_GetVersion(&version),"Load NVIDIA AR runtime");
        check(NvAR_Create(NvAR_Feature_GazeRedirection,&feature),"Create Eye Contact");
        check(NvAR_SetString(feature,NvAR_Parameter_Config(ModelDir),(arRuntimePath+"\\models").c_str()),"Set Eye Contact model directory");
        check(NvAR_SetU32(feature,NvAR_Parameter_Config(Landmarks_Size),68),"Set Eye Contact landmarks");
        check(NvAR_SetU32(feature,NvAR_Parameter_Config(Temporal),0xffffffffu),"Enable Eye Contact temporal tracking");
        check(NvAR_SetU32(feature,NvAR_Parameter_Config(GazeRedirect),1),"Enable gaze redirection");
        check(NvAR_SetU32(feature,NvAR_Parameter_Config(DetectClosure),1),"Enable blink/occlusion detection");
        check(NvAR_SetU32(feature,NvAR_Parameter_Config(UseCudaGraph),0),"Set Eye Contact CUDA graph mode");
        check(NvAR_SetU32(feature,NvAR_Parameter_Config(EyeSizeSensitivity),3),"Set Eye Contact eye sensitivity");
        check(NvAR_SetCudaStream(feature,NvAR_Parameter_Config(CUDAStream),stream),"Set Eye Contact CUDA stream");
        check(NvAR_Load(feature),"Load Eye Contact models");
        check(NvCVImage_Alloc(&output,input.width,input.height,NVCV_BGR,NVCV_U8,NVCV_CHUNKY,NVCV_GPU,1),"Allocate Eye Contact output");
        check(NvAR_SetObject(feature,NvAR_Parameter_Input(Image),&input,sizeof(NvCVImage)),"Set Eye Contact image");
        check(NvAR_SetObject(feature,NvAR_Parameter_Output(Image),&output,sizeof(NvCVImage)),"Set Eye Contact output");
        check(NvAR_SetS32(feature,NvAR_Parameter_Input(Width),input.width),"Set Eye Contact input width");
        check(NvAR_SetS32(feature,NvAR_Parameter_Input(Height),input.height),"Set Eye Contact input height");
        unsigned count=0;
        check(NvAR_GetU32(feature,NvAR_Parameter_Config(Landmarks_Size),&count),"Get Eye Contact landmark count");
        if(count==0 || count>126)throw std::runtime_error("Eye Contact returned an unexpected landmark count.");
        landmarks.resize(count); confidence.resize(count); redirectedLandmarks.resize(12);
        check(NvAR_SetObject(feature,NvAR_Parameter_Output(Landmarks),landmarks.data(),sizeof(NvAR_Point2f)),"Set Eye Contact landmark output");
        check(NvAR_SetObject(feature,NvAR_Parameter_Output(GazeOutputLandmarks),redirectedLandmarks.data(),sizeof(NvAR_Point2f)),"Set redirected landmark output");
        check(NvAR_SetF32Array(feature,NvAR_Parameter_Output(LandmarksConfidence),confidence.data(),static_cast<int>(count)),"Set landmark confidence output");
        check(NvAR_SetF32Array(feature,NvAR_Parameter_Output(OutputGazeVector),gazeAngles,2),"Set Eye Contact gaze output");
        check(NvAR_SetF32Array(feature,NvAR_Parameter_Output(OutputHeadTranslation),headTranslation,3),"Set Eye Contact translation output");
        check(NvAR_SetObject(feature,NvAR_Parameter_Output(HeadPose),&headPose,sizeof(NvAR_Quaternion)),"Set Eye Contact head pose");
        check(NvAR_SetObject(feature,NvAR_Parameter_Output(GazeDirection),gazeDirection,sizeof(NvAR_Point3f)),"Set Eye Contact gaze direction");
        boxes.boxes=&faceBox; boxes.max_boxes=1; boxes.num_boxes=1;
        check(NvAR_SetObject(feature,NvAR_Parameter_Output(BoundingBoxes),&boxes,sizeof(NvAR_BBoxes)),"Set Eye Contact face output");
    }
    void run(NvCVImage& input,CUstream stream) {
        // The source is a safe default when no face is found or redirection disengages.
        check(NvCVImage_Transfer(&input,&output,1,stream,nullptr),"Prime Eye Contact output");
        std::fill(confidence.begin(),confidence.end(),0.f); faceBox={};boxes.num_boxes=1;
        check(NvAR_Run(feature),"Run Eye Contact");
        float sum=0;
        for(float value:confidence)if(std::isfinite(value))sum+=value;
        meanConfidence=confidence.empty()?0:sum/static_cast<float>(confidence.size());
        faceCount=(boxes.num_boxes>0 && faceBox.width>0 && faceBox.height>0 && meanConfidence>=.15f)?1:0;
        left=top=right=bottom=0;
        if(faceCount && landmarks.size()>=48) {
            float x0=static_cast<float>(input.width),y0=static_cast<float>(input.height),x1=0,y1=0;
            for(int i=36;i<48;i++){const auto& point=landmarks[i];x0=std::min(x0,point.x);y0=std::min(y0,point.y);x1=std::max(x1,point.x);y1=std::max(y1,point.y);}
            if(std::isfinite(x0)&&std::isfinite(y0)&&std::isfinite(x1)&&std::isfinite(y1)&&x1>x0&&y1>y0) {
                int margin=std::max(4,static_cast<int>((x1-x0)*.12f));
                left=std::clamp(static_cast<int>(std::floor(x0))-margin,0,static_cast<int>(input.width));
                top=std::clamp(static_cast<int>(std::floor(y0))-margin,0,static_cast<int>(input.height));
                right=std::clamp(static_cast<int>(std::ceil(x1))+margin,0,static_cast<int>(input.width));
                bottom=std::clamp(static_cast<int>(std::ceil(y1))+margin,0,static_cast<int>(input.height));
            }
        }
        if(faceCount)check(NvCVImage_Transfer(&output,&input,1,stream,nullptr),"Use corrected eye image");
    }
};

struct Context {
    int width, height;
    NvVFX_Handle greenscreen = nullptr, blur = nullptr;
    CUstream stream = nullptr;
    NvVFX_StateObjectHandle state = nullptr;
    std::vector<NvVFX_StateObjectHandle> states;
    NvCVImage nv12, bgr, mask, result, background, staging;
    Synchronize synchronize = nullptr;
    HMODULE cuda = nullptr;
    bool backgroundSet = false, blurLoaded = false;
    float lastStrength = -1;
    EyeContact* eyes = nullptr;
    unsigned long long eyeFrames = 0;
    float eyeConfidence = 0;
    int eyeFaces = 0;
    Context(int w, int h) : width(w), height(h) {}
    ~Context() {
        if (synchronize && stream) synchronize(stream);
        delete eyes;
        if (state && greenscreen) NvVFX_DeallocateState(greenscreen, state);
        if (greenscreen) NvVFX_DestroyEffect(greenscreen);
        if (blur) NvVFX_DestroyEffect(blur);
        NvCVImage_Dealloc(&nv12); NvCVImage_Dealloc(&bgr); NvCVImage_Dealloc(&mask);
        NvCVImage_Dealloc(&result); NvCVImage_Dealloc(&background); NvCVImage_Dealloc(&staging);
        if (stream) NvVFX_CudaStreamDestroy(stream);
        if (cuda) FreeLibrary(cuda);
    }
    void init(int mode) {
        unsigned version = 0;
        check(NvVFX_GetVersion(&version), "Load NVIDIA Video Effects runtime");
        cuda = LoadLibraryA((runtimePath + "\\cudart64_12.dll").c_str());
        if (!cuda) throw std::runtime_error("The Maxine CUDA runtime is missing.");
        synchronize = reinterpret_cast<Synchronize>(GetProcAddress(cuda, "cudaStreamSynchronize"));
        if (!synchronize) throw std::runtime_error("cudaStreamSynchronize is unavailable.");
        check(NvVFX_CudaStreamCreate(&stream), "Create CUDA stream");
        check(NvCVImage_Alloc(&nv12,width,height,NVCV_YUV420,NVCV_U8,NVCV_NV12,NVCV_GPU,1), "Allocate GPU NV12");
        nv12.colorspace = NVCV_601 | NVCV_VIDEO_RANGE;
        check(NvCVImage_Alloc(&bgr,width,height,NVCV_BGR,NVCV_U8,NVCV_CHUNKY,NVCV_GPU,1), "Allocate GPU color");
        check(NvCVImage_Alloc(&mask,width,height,NVCV_A,NVCV_U8,NVCV_CHUNKY,NVCV_GPU,1), "Allocate alpha mask");
        check(NvCVImage_Alloc(&result,width,height,NVCV_BGR,NVCV_U8,NVCV_CHUNKY,NVCV_GPU,1), "Allocate result");
        check(NvCVImage_Alloc(&background,width,height,NVCV_BGR,NVCV_U8,NVCV_CHUNKY,NVCV_GPU,1), "Allocate background");
        check(NvVFX_CreateEffect(NVVFX_FX_GREEN_SCREEN,&greenscreen), "Create AI Green Screen");
        check(NvVFX_SetString(greenscreen,NVVFX_MODEL_DIRECTORY,(runtimePath+"\\models").c_str()), "Set model directory");
        check(NvVFX_SetU32(greenscreen,NVVFX_MODE,mode), "Set segmentation mode");
        check(NvVFX_SetCudaStream(greenscreen,NVVFX_CUDA_STREAM,stream), "Set CUDA stream");
        check(NvVFX_SetU32(greenscreen,NVVFX_MAX_INPUT_WIDTH,width), "Set maximum width");
        check(NvVFX_SetU32(greenscreen,NVVFX_MAX_INPUT_HEIGHT,height), "Set maximum height");
        check(NvVFX_SetU32(greenscreen,NVVFX_MAX_NUMBER_STREAMS,1), "Set stream count");
        check(NvVFX_SetImage(greenscreen,NVVFX_INPUT_IMAGE,&bgr), "Set segmentation input");
        check(NvVFX_SetImage(greenscreen,NVVFX_OUTPUT_IMAGE,&mask), "Set segmentation mask");
        check(NvVFX_Load(greenscreen), "Load segmentation model");
        check(NvVFX_AllocateState(greenscreen,&state), "Allocate temporal state");
        unsigned batch = 1;
        check(NvVFX_GetU32(greenscreen,NVVFX_MODEL_BATCH,&batch), "Get model batch");
        states.resize(batch); states[0] = state;
        check(NvVFX_SetStateObjectHandleArray(greenscreen,NVVFX_STATE,states.data()), "Bind temporal state");
        check(NvVFX_CreateEffect(NVVFX_FX_BGBLUR,&blur), "Create Background Blur");
        check(NvVFX_SetCudaStream(blur,NVVFX_CUDA_STREAM,stream), "Set blur stream");
        check(NvVFX_SetImage(blur,NVVFX_INPUT_IMAGE_0,&bgr), "Set blur image");
        check(NvVFX_SetImage(blur,NVVFX_INPUT_IMAGE_1,&mask), "Set blur mask");
        check(NvVFX_SetImage(blur,NVVFX_OUTPUT_IMAGE,&result), "Set blur result");
    }
    void sync() { if (synchronize(stream) != 0) throw std::runtime_error("CUDA stream synchronization failed."); }
    void finishPendingWork() noexcept { if(synchronize && stream)synchronize(stream); }
    void resetEyes() noexcept { finishPendingWork();delete eyes;eyes=nullptr;eyeConfidence=0;eyeFaces=0; }
};
#define EXPORT extern "C" __declspec(dllexport)
EXPORT void* __cdecl PcMaxineCreate(const char* path,int width,int height,int mode,char* error,int length) {
    Context* context = nullptr;
    try {
        if (!path || width<512 || height<288 || width%2 || height%2 || mode<0 || mode>3) throw std::runtime_error("Maxine needs even dimensions of at least 512 x 288 and a mode from 0 to 3.");
        if (runtimePath.empty()) { runtimePath=path; g_nvVFXSDKPath=runtimePath.data(); SetDllDirectoryA(path); }
        else if (runtimePath!=path) throw std::runtime_error("Restart PixelCam to change the Maxine runtime folder.");
        context = new Context(width,height); context->init(mode); return context;
    } catch (const std::exception& ex) { errorText(error,length,ex); delete context; return nullptr; }
}
EXPORT void __cdecl PcMaxineDestroy(Context* context) { delete context; }
EXPORT int __cdecl PcMaxineReset(Context* context) { if(!context)return 0;context->resetEyes();return NvVFX_ResetState(context->greenscreen,context->state); }
EXPORT int __cdecl PcMaxineBackground(Context* context,unsigned char* pixels,char* error,int length) {
    try {
        if (!context || !pixels) throw std::runtime_error("No replacement image supplied.");
        NvCVImage cpu;
        check(NvCVImage_Init(&cpu,context->width,context->height,context->width*3,pixels,NVCV_BGR,NVCV_U8,NVCV_CHUNKY,NVCV_CPU), "Wrap replacement image");
        check(NvCVImage_Transfer(&cpu,&context->background,1,context->stream,&context->staging), "Upload background");
        context->sync(); context->backgroundSet=true; return 0;
    } catch(const std::exception& ex) {if(context)context->finishPendingWork();errorText(error,length,ex); return -1;}
}
static int processFrame(Context* context,unsigned char* input,unsigned char* output,int effect,float strength,unsigned char* alpha,int eyeEnabled,const char* arPath,char* error,int length) {
    try {
        if (!context || !input || !output) throw std::runtime_error("No video frame supplied.");
        NvCVImage cpuIn,cpuOut;
        check(NvCVImage_Init(&cpuIn,context->width,context->height,context->width,input,NVCV_YUV420,NVCV_U8,NVCV_NV12,NVCV_CPU), "Wrap input");
        check(NvCVImage_Init(&cpuOut,context->width,context->height,context->width,output,NVCV_YUV420,NVCV_U8,NVCV_NV12,NVCV_CPU), "Wrap output");
        cpuIn.colorspace=cpuOut.colorspace=NVCV_601|NVCV_VIDEO_RANGE;
        check(NvCVImage_Transfer(&cpuIn,&context->nv12,1,context->stream,&context->staging), "Upload NV12");
        check(NvCVImage_Transfer(&context->nv12,&context->bgr,1,context->stream,nullptr), "Convert NV12 to BGR");
        bool eyeFailed=false;
        if(eyeEnabled) {
            try {
                if(!context->eyes) { context->eyes=new EyeContact();context->eyes->init(arPath,context->bgr,context->stream); }
                context->eyes->run(context->bgr,context->stream);
                context->eyeConfidence=context->eyes->meanConfidence;context->eyeFaces=context->eyes->faceCount;context->eyeFrames++;
            } catch(const std::exception& ex) {
                context->resetEyes();errorText(error,length,ex);eyeFailed=true;
                // A failed AR operation may have queued a partial output copy.
                // Rebuild pristine BGR from the still-untouched source NV12.
                check(NvCVImage_Transfer(&context->nv12,&context->bgr,1,context->stream,nullptr),"Restore source after Eye Contact failure");
            }
        } else if(context->eyes)context->resetEyes();
        if(effect!=0)check(NvVFX_Run(context->greenscreen,0), "Run AI Green Screen");
        NvCVImage* finalImage=&context->result;
        if (effect==1) {
            if (strength!=context->lastStrength) {
                check(NvVFX_SetF32(context->blur,NVVFX_STRENGTH,strength), "Set blur strength");
                // The SDK's blur settings take effect on Load. Only reload when strength changes.
                check(NvVFX_Load(context->blur), "Load blur settings");
                context->lastStrength=strength; context->blurLoaded=true;
            }
            check(NvVFX_Run(context->blur,0), "Run Background Blur");
        } else if (effect==2) {
            if (!context->backgroundSet) throw std::runtime_error("Choose a replacement background first.");
            check(NvCVImage_Composite(&context->bgr,&context->background,&context->mask,&context->result,context->stream), "Composite background");
        } else if(effect==0)finalImage=&context->bgr;
        else throw std::runtime_error("Unknown background effect.");
        check(NvCVImage_Transfer(finalImage,&context->nv12,1,context->stream,nullptr), "Convert result to NV12");
        check(NvCVImage_Transfer(&context->nv12,&cpuOut,1,context->stream,&context->staging), "Download result");
        if (alpha && effect!=0) {
            NvCVImage cpuAlpha;
            check(NvCVImage_Init(&cpuAlpha,context->width,context->height,context->width,alpha,NVCV_A,NVCV_U8,NVCV_CHUNKY,NVCV_CPU), "Wrap diagnostic mask");
            check(NvCVImage_Transfer(&context->mask,&cpuAlpha,1,context->stream,nullptr), "Download diagnostic mask");
        }
        context->sync(); return eyeFailed?1:0;
    } catch(const std::exception& ex) {if(context)context->finishPendingWork();errorText(error,length,ex); return -1;}
}

EXPORT int __cdecl PcMaxineProcess(Context* context,unsigned char* input,unsigned char* output,int effect,float strength,unsigned char* alpha,char* error,int length) {
    return processFrame(context,input,output,effect,strength,alpha,0,nullptr,error,length);
}
// Return 1 only when eye correction failed but a valid background/original frame
// was still produced. Return -1 for a frame-pipeline failure.
EXPORT int __cdecl PcMaxineProcessEyes(Context* context,unsigned char* input,unsigned char* output,int effect,float strength,unsigned char* alpha,int eyeEnabled,const char* arPath,char* error,int length) {
    return processFrame(context,input,output,effect,strength,alpha,eyeEnabled,arPath,error,length);
}
EXPORT int __cdecl PcMaxineEyeStatus(Context* context,float* confidence,int* faceCount,unsigned long long* processedFrames) {
    if(!context)return -1;
    if(confidence)*confidence=context->eyeConfidence;
    if(faceCount)*faceCount=context->eyeFaces;
    if(processedFrames)*processedFrames=context->eyeFrames;
    return 0;
}
EXPORT int __cdecl PcMaxineEyeBounds(Context* context,int* left,int* top,int* right,int* bottom) {
    if(!context)return -1;
    if(left)*left=context->eyes?context->eyes->left:0;
    if(top)*top=context->eyes?context->eyes->top:0;
    if(right)*right=context->eyes?context->eyes->right:0;
    if(bottom)*bottom=context->eyes?context->eyes->bottom:0;
    return 0;
}
