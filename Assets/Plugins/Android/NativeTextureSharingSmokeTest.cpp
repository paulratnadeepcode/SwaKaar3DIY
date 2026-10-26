// Original smoke-test code. No camera ownership, codecs, or external dependencies.
#if defined(__ANDROID__)
#include <EGL/egl.h>
#include <EGL/eglext.h>
#include <GLES3/gl3.h>
#include <dlfcn.h>
#include <pthread.h>
#include <atomic>
#include <chrono>
#include <cstdio>
#include <cstdlib>
#include <cstdint>
#include <unistd.h>
#include <sys/syscall.h>
#include <new>
#include <type_traits>
#include <cstring>
#include <cstdarg>

#define EXPORT extern "C" __attribute__((visibility("default")))
using Clock = std::chrono::steady_clock;
static double Ms(Clock::time_point a) {
    return std::chrono::duration<double, std::milli>(Clock::now()-a).count();
}
struct Job {
    GLuint texture; int pattern;
    std::atomic<int> done{0}, cancel{0}, callbackFinished{0};
    long submitTid=0, callbackTid=0, workerTid=0;
    EGLDisplay display=EGL_NO_DISPLAY; EGLContext unity=EGL_NO_CONTEXT;
    EGLSyncKHR fence=EGL_NO_SYNC_KHR;
    void* egl=nullptr; void* gles=nullptr;
    double callbackMs=0, waitMs=0;
    unsigned char pixels[16]={};
    unsigned glError=0, eglError=EGL_SUCCESS;
    bool passed=false, cleanup=false, shared=false, synced=false;
    bool failureRecorded=false;
    char reason[128]="Not executed", report[4096]={};
    char diagnostic[2304]={}, cleanupDiagnostic[384]={};
    unsigned cleanupGlError=0;
#define E(name) decltype(&name) name##Fn=nullptr;
    E(eglGetCurrentDisplay) E(eglGetCurrentContext) E(eglQueryContext)
    E(eglChooseConfig) E(eglBindAPI) E(eglCreateContext) E(eglCreatePbufferSurface)
    E(eglMakeCurrent) E(eglDestroySurface) E(eglDestroyContext) E(eglGetError)
    E(eglGetProcAddress) E(eglReleaseThread)
    E(eglQueryString) E(eglGetConfigAttrib)
#undef E
    PFNEGLCREATESYNCKHRPROC createSync=nullptr;
    PFNEGLCLIENTWAITSYNCKHRPROC waitSync=nullptr;
    PFNEGLDESTROYSYNCKHRPROC destroySync=nullptr;
#define G(name) decltype(&name) name##Fn=nullptr;
    G(glFlush) G(glGenFramebuffers) G(glBindFramebuffer)
    G(glFramebufferTexture2D) G(glCheckFramebufferStatus)
    G(glReadPixels) G(glDeleteFramebuffers) G(glGetError)
#undef G
};
static void Reason(Job* j,const char* text) { std::snprintf(j->reason,sizeof(j->reason),"%s",text); }
static void Append(char* buffer,size_t capacity,const char* format,...) {
    size_t used=std::strlen(buffer);
    if(used>=capacity-1) return;
    va_list args; va_start(args,format);
    std::vsnprintf(buffer+used,capacity-used,format,args);
    va_end(args);
}
static void Failure(Job* j,const char* operation,EGLint error) {
    if(!j->failureRecorded) {
        j->failureRecorded=true;
        j->eglError=error;
        Reason(j,operation);
    }
    j->passed=false;
}
// Called immediately after each worker EGL operation, before any other EGL call.
static bool Check(Job* j,const char* operation,bool success) {
    EGLint error=j->eglGetErrorFn();
    Append(j->diagnostic,sizeof(j->diagnostic),"%s=%d/0x%x; ",operation,success,error);
    if(!success || error!=EGL_SUCCESS) {
        Failure(j,operation,error);
        return false;
    }
    return true;
}
static void CleanupCheck(Job* j,const char* operation,bool success) {
    // Do not overwrite the original operation/error, even if cleanup also fails.
    if(!success) {
        EGLint error=j->eglGetErrorFn();
        j->cleanup=false; j->passed=false;
        Append(j->cleanupDiagnostic,sizeof(j->cleanupDiagnostic),"%s=0x%x; ",operation,error);
    }
}
static bool HasExtension(const char* list,const char* name) {
    if(!list) return false;
    size_t length=std::strlen(name);
    for(const char* p=list;(p=std::strstr(p,name))!=nullptr;p+=length)
        if((p==list || p[-1]==' ') && (p[length]==' ' || p[length]=='\0')) return true;
    return false;
}
static bool ConfigAttribute(Job* j,EGLConfig config,EGLint attribute,const char* name,EGLint& value) {
    EGLBoolean ok=j->eglGetConfigAttribFn(j->display,config,attribute,&value);
    // Includes the attribute's name and value so unavailable queries are identifiable.
    char operation[64]; std::snprintf(operation,sizeof(operation),"config.%s",name);
    bool valid=Check(j,operation,ok==EGL_TRUE);
    if(valid) Append(j->diagnostic,sizeof(j->diagnostic),"value=0x%x; ",value);
    return valid;
}
static bool SelectConfig(Job* j,EGLConfig& config) {
    const char* version=j->eglQueryStringFn(j->display,EGL_VERSION);
    if(!Check(j,"eglQueryString(version)",version!=nullptr)) return false;
    Append(j->diagnostic,sizeof(j->diagnostic),"version=%.96s; ",version);
    int major=0,minor=0;
    if(std::sscanf(version,"%d.%d",&major,&minor)!=2) {
        Failure(j,"Unparseable EGL version",EGL_SUCCESS); return false;
    }
    const char* vendor=j->eglQueryStringFn(j->display,EGL_VENDOR);
    if(!Check(j,"eglQueryString(vendor)",vendor!=nullptr)) return false;
    Append(j->diagnostic,sizeof(j->diagnostic),"vendor=%.96s; ",vendor);
    const char* extensions=j->eglQueryStringFn(j->display,EGL_EXTENSIONS);
    if(!Check(j,"eglQueryString(extensions)",extensions!=nullptr)) return false;
    bool core15=major>1 || (major==1 && minor>=5);
    bool create=HasExtension(extensions,"EGL_KHR_create_context");
    bool noConfig=HasExtension(extensions,"EGL_KHR_no_config_context");
    bool fence=HasExtension(extensions,"EGL_KHR_fence_sync");
    Append(j->diagnostic,sizeof(j->diagnostic),
        "KHR_create_context=%d KHR_no_config_context=%d KHR_fence_sync=%d KHR_surfaceless_context=%d ANDROID_recordable=%d; ",
        create,noConfig,fence,HasExtension(extensions,"EGL_KHR_surfaceless_context"),
        HasExtension(extensions,"EGL_ANDROID_recordable"));
    if(!core15 && !create) {
        Failure(j,"GLES3 config selection needs EGL 1.5 or EGL_KHR_create_context",EGL_SUCCESS); return false;
    }
    // This experiment deliberately retains the existing KHR fence implementation.
    if(!fence) {
        Failure(j,"EGL_KHR_fence_sync not advertised",EGL_SUCCESS); return false;
    }
    EGLint configId=-1,clientType=0,clientVersion=0;
    EGLBoolean ok=j->eglQueryContextFn(j->display,j->unity,EGL_CONFIG_ID,&configId);
    if(!Check(j,"eglQueryContext(configId)",ok==EGL_TRUE)) return false;
    Append(j->diagnostic,sizeof(j->diagnostic),"unityConfigId=%d; ",configId);
    ok=j->eglQueryContextFn(j->display,j->unity,EGL_CONTEXT_CLIENT_TYPE,&clientType);
    if(!Check(j,"eglQueryContext(clientType)",ok==EGL_TRUE)) return false;
    ok=j->eglQueryContextFn(j->display,j->unity,EGL_CONTEXT_CLIENT_VERSION,&clientVersion);
    if(!Check(j,"eglQueryContext(clientVersion)",ok==EGL_TRUE)) return false;
    Append(j->diagnostic,sizeof(j->diagnostic),"unityClient=0x%x major=%d; ",clientType,clientVersion);
    if(clientType!=EGL_OPENGL_ES_API || clientVersion<3) {
        Failure(j,"Captured Unity context is not GLES3+",EGL_SUCCESS); return false;
    }
    EGLint count=0;
    if(configId>0) {
        // ID lookup only: EGL ignores other selection filters when CONFIG_ID is specified.
        const EGLint attributes[]={EGL_CONFIG_ID,configId,EGL_NONE};
        ok=j->eglChooseConfigFn(j->display,attributes,&config,1,&count);
        if(!Check(j,"eglChooseConfig(Unity ID)",ok==EGL_TRUE)) return false;
        Append(j->diagnostic,sizeof(j->diagnostic),"selection=UnityConfig count=%d; ",count);
    } else if(configId==0 && noConfig) {
        // Confirmed by a successful zero-ID query, explicit extension support, and GLES3 type/version.
        // KHR_no_config_context allows sharing with a context created using a concrete config.
        const EGLint attributes[]={
            EGL_SURFACE_TYPE,EGL_PBUFFER_BIT,
            EGL_RENDERABLE_TYPE,EGL_OPENGL_ES3_BIT_KHR,
            EGL_RED_SIZE,8,EGL_GREEN_SIZE,8,EGL_BLUE_SIZE,8,EGL_ALPHA_SIZE,8,EGL_NONE};
        ok=j->eglChooseConfigFn(j->display,attributes,&config,1,&count);
        if(!Check(j,"eglChooseConfig(configless RGBA8)",ok==EGL_TRUE)) return false;
        Append(j->diagnostic,sizeof(j->diagnostic),"selection=confirmedConfigless count=%d; ",count);
    } else {
        Failure(j,configId==0?"Zero Unity config ID without advertised EGL_KHR_no_config_context":"Invalid negative Unity config ID",EGL_SUCCESS);
        return false;
    }
    if(count!=1) {
        Failure(j,"eglChooseConfig returned no usable config",EGL_SUCCESS); return false;
    }
    EGLint id=0,surfaces=0,renderable=0,red=0,green=0,blue=0,alpha=0,depth=0,stencil=0,samples=0;
    if(!ConfigAttribute(j,config,EGL_CONFIG_ID,"id",id) ||
       !ConfigAttribute(j,config,EGL_SURFACE_TYPE,"surfaces",surfaces) ||
       !ConfigAttribute(j,config,EGL_RENDERABLE_TYPE,"renderable",renderable) ||
       !ConfigAttribute(j,config,EGL_RED_SIZE,"red",red) ||
       !ConfigAttribute(j,config,EGL_GREEN_SIZE,"green",green) ||
       !ConfigAttribute(j,config,EGL_BLUE_SIZE,"blue",blue) ||
       !ConfigAttribute(j,config,EGL_ALPHA_SIZE,"alpha",alpha) ||
       !ConfigAttribute(j,config,EGL_DEPTH_SIZE,"depth",depth) ||
       !ConfigAttribute(j,config,EGL_STENCIL_SIZE,"stencil",stencil) ||
       !ConfigAttribute(j,config,EGL_SAMPLES,"samples",samples)) return false;
    Append(j->diagnostic,sizeof(j->diagnostic),"selectedConfig=%p; ",config);
    if(configId>0 && id!=configId) {
        Failure(j,"Resolved config ID differs from Unity config ID",EGL_SUCCESS); return false;
    }
    if(!(surfaces&EGL_PBUFFER_BIT)) {
        Failure(j,"Selected Unity config lacks EGL_PBUFFER_BIT; no blind fallback",EGL_SUCCESS); return false;
    }
    if(!(renderable&EGL_OPENGL_ES3_BIT_KHR)) {
        Failure(j,"Selected config lacks GLES3 renderable bit",EGL_SUCCESS); return false;
    }
    // Conservative worker default-framebuffer policy, separate from the owned RGBA8 texture.
    if(red<8 || green<8 || blue<8 || alpha<8) {
        Failure(j,"Selected config lacks required RGBA8 channel sizes",EGL_SUCCESS); return false;
    }
    return true;
}
static void Finish(Job* j) {
    if(j->fence!=EGL_NO_SYNC_KHR && j->destroySync) {
        EGLBoolean ok=j->destroySync(j->display,j->fence);
        CleanupCheck(j,"eglDestroySyncKHR",ok==EGL_TRUE);
        j->fence=EGL_NO_SYNC_KHR;
    }
    if(j->workerTid!=0 && j->eglReleaseThreadFn) {
        EGLBoolean ok=j->eglReleaseThreadFn();
        CleanupCheck(j,"eglReleaseThread",ok==EGL_TRUE);
    }
    if(j->gles) dlclose(j->gles);
    if(j->egl) dlclose(j->egl);
    // Final access to Job: managed owner also waits for callbackFinished.
    j->done.store(1,std::memory_order_release);
}
static void* Worker(void* ptr) {
    auto* j=static_cast<Job*>(ptr);
    j->workerTid=syscall(SYS_gettid);
    EGLContext context=EGL_NO_CONTEXT; EGLSurface surface=EGL_NO_SURFACE;
    GLuint fbo=0; bool current=false;
    do {
        if(j->cancel.load()) { Reason(j,"Cancelled before worker context"); break; }
        // EGL error state is per-thread. Preserve any entry error separately, then check every call.
        EGLint entryError=j->eglGetErrorFn();
        Append(j->diagnostic,sizeof(j->diagnostic),"display=%p unityContext=%p workerEntryError=0x%x; ",
            j->display,j->unity,entryError);
        EGLConfig config=nullptr;
        if(!SelectConfig(j,config)) break;
        EGLBoolean ok=j->eglBindAPIFn(EGL_OPENGL_ES_API);
        if(!Check(j,"eglBindAPI(GLES)",ok==EGL_TRUE)) break;
        const EGLint ca[]={EGL_CONTEXT_CLIENT_VERSION,3,EGL_NONE};
        context=j->eglCreateContextFn(j->display,config,j->unity,ca);
        if(!Check(j,"eglCreateContext(shared GLES3)",context!=EGL_NO_CONTEXT)) break;
        const EGLint pa[]={EGL_WIDTH,64,EGL_HEIGHT,64,EGL_NONE};
        surface=j->eglCreatePbufferSurfaceFn(j->display,config,pa);
        if(!Check(j,"eglCreatePbufferSurface(64x64)",surface!=EGL_NO_SURFACE)) break;
        ok=j->eglMakeCurrentFn(j->display,surface,surface,context);
        if(!Check(j,"eglMakeCurrent(worker)",ok==EGL_TRUE)) break;
        Append(j->diagnostic,sizeof(j->diagnostic),"workerContext=%p pbuffer=%p; ",context,surface);
        current=true; j->shared=true;
        auto start=Clock::now();
        // One bounded fence wait, on the worker only. No glFinish on Unity's thread.
        EGLint wait=j->waitSync(j->display,j->fence,0,1000000000ULL);
        j->waitMs=Ms(start);
        bool waited=Check(j,"eglClientWaitSyncKHR",wait==EGL_CONDITION_SATISFIED_KHR);
        Append(j->diagnostic,sizeof(j->diagnostic),"waitStatus=0x%x; ",wait);
        if(!waited) break;
        j->synced=true;
        if(j->cancel.load()) { Reason(j,"Cancelled after fence"); break; }
        j->glGenFramebuffersFn(1,&fbo);
        j->glBindFramebufferFn(GL_FRAMEBUFFER,fbo);
        j->glFramebufferTexture2DFn(GL_FRAMEBUFFER,GL_COLOR_ATTACHMENT0,GL_TEXTURE_2D,j->texture,0);
        if(j->glCheckFramebufferStatusFn(GL_FRAMEBUFFER)!=GL_FRAMEBUFFER_COMPLETE) {
            Reason(j,"Shared texture framebuffer incomplete"); break;
        }
        const int xy[8]={8,8,55,8,8,55,55,55};
        for(int k=0;k<4;k++)
            j->glReadPixelsFn(xy[2*k],xy[2*k+1],1,1,GL_RGBA,GL_UNSIGNED_BYTE,j->pixels+4*k);
        j->glError=j->glGetErrorFn();
        // GLES lower-left coordinates. Pattern 1 reverses all four quadrants.
        const unsigned char colors[16]={255,0,0,255,0,255,0,255,0,0,255,255,255,255,0,255};
        j->passed=(j->glError==GL_NO_ERROR);
        for(int k=0;k<4;k++) for(int c=0;c<4;c++) {
            int expected=colors[4*(j->pattern==0?k:3-k)+c];
            if(std::abs(int(j->pixels[4*k+c])-expected)>8) j->passed=false;
        }
        Reason(j,j->passed?"Corner colors match within 8/255":"Corner mismatch or GL error");
    } while(false);
    j->cleanup=true;
    if(current) {
        if(fbo) j->glDeleteFramebuffersFn(1,&fbo);
        j->cleanupGlError=j->glGetErrorFn();
        if(j->cleanupGlError!=GL_NO_ERROR) { j->cleanup=false; j->passed=false; }
        EGLBoolean ok=j->eglMakeCurrentFn(j->display,EGL_NO_SURFACE,EGL_NO_SURFACE,EGL_NO_CONTEXT);
        CleanupCheck(j,"eglMakeCurrent(unbind)",ok==EGL_TRUE);
    }
    if(surface!=EGL_NO_SURFACE) {
        EGLBoolean ok=j->eglDestroySurfaceFn(j->display,surface);
        CleanupCheck(j,"eglDestroySurface",ok==EGL_TRUE);
    }
    if(context!=EGL_NO_CONTEXT) {
        EGLBoolean ok=j->eglDestroyContextFn(j->display,context);
        CleanupCheck(j,"eglDestroyContext",ok==EGL_TRUE);
    }
    // Never eglTerminate: the display belongs to Unity.
    if(!j->cleanup || j->failureRecorded || j->cancel.load()) j->passed=false;
    Finish(j);
    return nullptr;
}
static void RenderEvent(int eventId,void* data) {
    auto* j=static_cast<Job*>(data); if(!j || eventId!=1) return;
    auto start=Clock::now();
    j->callbackTid=syscall(SYS_gettid);
    struct CallbackGuard {
        Job* job; Clock::time_point start;
        ~CallbackGuard() {
            job->callbackMs=Ms(start);
            job->callbackFinished.store(1,std::memory_order_release);
        }
    } guard{j,start};
    j->egl=dlopen("libEGL.so",RTLD_NOW|RTLD_LOCAL);
    j->gles=dlopen("libGLESv3.so",RTLD_NOW|RTLD_LOCAL);
    bool ok=j->egl && j->gles;
#define LOAD_E(n) j->n##Fn=reinterpret_cast<decltype(j->n##Fn)>(j->egl?dlsym(j->egl,#n):nullptr); ok=ok && j->n##Fn;
    LOAD_E(eglGetCurrentDisplay) LOAD_E(eglGetCurrentContext) LOAD_E(eglQueryContext)
    LOAD_E(eglChooseConfig) LOAD_E(eglBindAPI) LOAD_E(eglCreateContext)
    LOAD_E(eglCreatePbufferSurface) LOAD_E(eglMakeCurrent)
    LOAD_E(eglDestroySurface) LOAD_E(eglDestroyContext) LOAD_E(eglGetError) LOAD_E(eglGetProcAddress) LOAD_E(eglReleaseThread)
    LOAD_E(eglQueryString) LOAD_E(eglGetConfigAttrib)
#undef LOAD_E
#define LOAD_G(n) j->n##Fn=reinterpret_cast<decltype(j->n##Fn)>(j->gles?dlsym(j->gles,#n):nullptr); ok=ok && j->n##Fn;
    LOAD_G(glFlush) LOAD_G(glGenFramebuffers) LOAD_G(glBindFramebuffer)
    LOAD_G(glFramebufferTexture2D) LOAD_G(glCheckFramebufferStatus)
    LOAD_G(glReadPixels) LOAD_G(glDeleteFramebuffers) LOAD_G(glGetError)
#undef LOAD_G
    if(ok) {
        j->createSync=reinterpret_cast<PFNEGLCREATESYNCKHRPROC>(j->eglGetProcAddressFn("eglCreateSyncKHR"));
        j->waitSync=reinterpret_cast<PFNEGLCLIENTWAITSYNCKHRPROC>(j->eglGetProcAddressFn("eglClientWaitSyncKHR"));
        j->destroySync=reinterpret_cast<PFNEGLDESTROYSYNCKHRPROC>(j->eglGetProcAddressFn("eglDestroySyncKHR"));
        ok=j->createSync && j->waitSync && j->destroySync;
    }
    if(!ok) { j->cleanup=true; Reason(j,"Missing runtime EGL/GLES/fence entry point"); j->callbackMs=Ms(start); Finish(j); return; }
    j->display=j->eglGetCurrentDisplayFn(); j->unity=j->eglGetCurrentContextFn();
    if(j->display==EGL_NO_DISPLAY || j->unity==EGL_NO_CONTEXT || j->cancel.load()) {
        j->cleanup=true; Reason(j,"No Unity EGL context or cancelled"); j->callbackMs=Ms(start); Finish(j); return;
    }
    j->fence=j->createSync(j->display,EGL_SYNC_FENCE_KHR,nullptr);
    if(j->fence==EGL_NO_SYNC_KHR) {
        j->cleanup=true; EGLint error=j->eglGetErrorFn(); Failure(j,"eglCreateSyncKHR(producer)",error);
        j->callbackMs=Ms(start); Finish(j); return;
    }
    j->glFlushFn();
    // The managed owner waits for both worker completion and CallbackGuard publication.
    pthread_t thread;
    pthread_attr_t attr;
    if(pthread_attr_init(&attr)!=0) {
        j->cleanup=true; Reason(j,"Worker attributes initialization failed"); Finish(j); return;
    }
    int setup=pthread_attr_setdetachstate(&attr,PTHREAD_CREATE_DETACHED);
    int result=setup==0?pthread_create(&thread,&attr,Worker,j):setup;
    pthread_attr_destroy(&attr);
    if(result!=0) { j->cleanup=true; Reason(j,"Worker thread creation failed"); Finish(j); }
}
static_assert(std::is_same<decltype(&RenderEvent),void(*)(int,void*)>::value, "Unity callback ABI");
static_assert(sizeof(void*)==8 && sizeof(GLuint)==4, "Android ARM64 ABI required");
EXPORT int S2B_Abi() { return 1; }
EXPORT void* S2B_Callback() { return reinterpret_cast<void*>(&RenderEvent); }
EXPORT void* S2B_Create(uint32_t texture,int pattern) {
    if(!texture || pattern<0 || pattern>1) return nullptr;
    auto* j=new(std::nothrow) Job; if(!j) return nullptr;
    j->submitTid=syscall(SYS_gettid); j->texture=texture; j->pattern=pattern; return j;
}
EXPORT int S2B_Done(void* ptr) {
    auto* j=static_cast<Job*>(ptr);
    return j->done.load(std::memory_order_acquire) && j->callbackFinished.load(std::memory_order_acquire);
}
EXPORT const char* S2B_Report(void* ptr) {
    auto* j=static_cast<Job*>(ptr);
    std::snprintf(j->report,sizeof(j->report),
        "pattern=%d pass=%d submitTid=%ld callbackTid=%ld workerTid=%ld shared=%d synchronized=%d cleanup=%d cancelled=%d callbackMs=%.3f waitMs=%.3f EGL=0x%x GL=0x%x pixelsBL_BR_TL_TR=[%u,%u,%u,%u;%u,%u,%u,%u;%u,%u,%u,%u;%u,%u,%u,%u] reason=%s",
        j->pattern,j->passed,j->submitTid,j->callbackTid,j->workerTid,j->shared,j->synced,j->cleanup,j->cancel.load(),
        j->callbackMs,j->waitMs,j->eglError,j->glError,
        j->pixels[0],j->pixels[1],j->pixels[2],j->pixels[3],
        j->pixels[4],j->pixels[5],j->pixels[6],j->pixels[7],
        j->pixels[8],j->pixels[9],j->pixels[10],j->pixels[11],
        j->pixels[12],j->pixels[13],j->pixels[14],j->pixels[15],j->reason);
    size_t used=std::strlen(j->report);
    std::snprintf(j->report+used,sizeof(j->report)-used,
        " diagnostics={%s} cleanupEGL={%s} cleanupGL=0x%x",
        j->diagnostic,j->cleanupDiagnostic[0]?j->cleanupDiagnostic:"none",j->cleanupGlError);
    return j->report;
}
EXPORT int S2B_Passed(void* ptr) {
    auto* j=static_cast<Job*>(ptr); return j->passed && j->cleanup;
}
EXPORT void S2B_Cancel(void* ptr) { static_cast<Job*>(ptr)->cancel.store(1); }
EXPORT void S2B_Destroy(void* ptr) { delete static_cast<Job*>(ptr); }
#endif
