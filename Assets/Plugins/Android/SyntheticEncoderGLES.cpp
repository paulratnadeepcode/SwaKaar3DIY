// Development POC: bounded three-texture synthetic recording; no camera ownership.
#if defined(__ANDROID__)
#include <EGL/egl.h>
#include <EGL/eglext.h>
#include <media/NdkMediaCodec.h>
#include <media/NdkMediaFormat.h>
#include <media/NdkMediaMuxer.h>
#include <GLES3/gl3.h>
#include <fcntl.h>
#include <cerrno>
#include <cstdlib>
#include <mutex>
#include <algorithm>
#include <android/native_window.h>
#include <dlfcn.h>
#include <pthread.h>
#include <unistd.h>
#include <sys/syscall.h>
#include <atomic>
#include <chrono>
#include <cstdio>
#include <cstring>
#include <cstdarg>
#include <new>
#include <type_traits>

namespace EncoderWindow {
using Clock=std::chrono::steady_clock;
struct Slot {
    // Free -> Reserved (main) -> Pending (render) -> Consuming (worker) -> Free.
    std::atomic<int> state{0}; GLuint texture=0; GLsync producer=nullptr;
    int frame=0; int64_t pts=0;
};
struct Job {
    std::atomic<int> done{0}, callbackDone{0}, cancel{0};
    long callerTid=0,callbackTid=0,workerTid=0;
    double callbackMs=0;
    bool pass=false,clean=true;
    std::atomic<bool> failed{false};
    std::mutex failureMutex,traceMutex;
    bool recording=false, realCamera=false;
    int frameLimit=150, reservedFrames=0;
    int64_t lastReservedPts=-1;
    std::atomic<int> ready{0}, ended{0}, outstanding{0}, producerError{0};
    std::atomic<int> submitted{0}, swapped{0}, samples{0}, queueHigh{0};
    std::atomic<int> drainStop{0}, drainDone{0}, eos{0};
    std::atomic<int64_t> callbackMaxNs{0};
    Slot slots[3];
    char path[1024]={};
    AMediaCodec* drainCodec=nullptr;
    AMediaMuxer* muxer=nullptr;
    bool muxerStarted=false,finalized=false;
    int outputFd=-1,formatChanges=0,configBuffers=0,keySamples=0;
    int64_t firstPts=-1,lastPts=-1,samplePts[320]={},inputPts[320]={};
    int sampleBytes[320]={},sampleFlags[320]={};
    double elapsedMs=0,maxSwapMs=0,producerWaitMs=0,consumerWaitMs=0,maxDrainMs=0;
    int producerTimeouts=0,consumerTimeouts=0;
    PFNEGLPRESENTATIONTIMEANDROIDPROC presentation=nullptr;
    void* glLib=nullptr;
    int mediaError=0; EGLint eglError=EGL_SUCCESS;
    char requested[256]={},canonical[256]={},actual[256]={};
    char failure[128]="none",trace[8192]={},cleanup[1024]={},report[24000]={};
    void* eglLib=nullptr; void* mediaLib=nullptr; void* androidLib=nullptr;
    EGLDisplay display=EGL_NO_DISPLAY; EGLContext unity=EGL_NO_CONTEXT;
#define E(n) decltype(&n) n##Fn=nullptr;
    E(eglGetCurrentDisplay) E(eglGetCurrentContext) E(eglGetError) E(eglGetProcAddress)
    E(eglQueryString) E(eglQueryContext) E(eglChooseConfig) E(eglGetConfigAttrib)
    E(eglBindAPI) E(eglCreateContext) E(eglCreateWindowSurface) E(eglMakeCurrent)
    E(eglSwapBuffers) E(eglQuerySurface) E(eglDestroySurface) E(eglDestroyContext) E(eglReleaseThread)
    E(AMediaCodec_createCodecByName)
    // Optional API28 functions are dynamically resolved; avoid compile-time API26 references.
    media_status_t (*AMediaCodec_getNameFn)(AMediaCodec*,char**)=nullptr;
    void (*AMediaCodec_releaseNameFn)(AMediaCodec*,char*)=nullptr;
    E(AMediaFormat_new) E(AMediaFormat_setString) E(AMediaFormat_setInt32) E(AMediaFormat_delete)
    E(AMediaCodec_configure) E(AMediaCodec_createInputSurface) E(AMediaCodec_start)
    E(AMediaCodec_stop) E(AMediaCodec_delete) E(ANativeWindow_release)
    E(AMediaCodec_dequeueOutputBuffer) E(AMediaCodec_getOutputBuffer)
    E(AMediaCodec_getOutputFormat) E(AMediaCodec_releaseOutputBuffer)
    media_status_t (*signalEOS)(AMediaCodec*)=nullptr; // API26, resolved dynamically.
    E(AMediaMuxer_new) E(AMediaMuxer_addTrack) E(AMediaMuxer_start)
    E(AMediaMuxer_writeSampleData) E(AMediaMuxer_stop) E(AMediaMuxer_delete)
    E(glFenceSync) E(glClientWaitSync) E(glDeleteSync) E(glFlush) E(glGetError)
    E(glCreateShader) E(glShaderSource) E(glCompileShader) E(glGetShaderiv) E(glDeleteShader)
    E(glCreateProgram) E(glAttachShader) E(glLinkProgram) E(glGetProgramiv) E(glDeleteProgram)
    E(glGenVertexArrays) E(glBindVertexArray) E(glDeleteVertexArrays)
    E(glUseProgram) E(glGetUniformLocation) E(glUniform1i) E(glActiveTexture)
    E(glBindTexture) E(glViewport) E(glDrawArrays) E(glDisable)
#undef E
};
#if __ANDROID_API__ >= 28
static_assert(std::is_same<decltype(&AMediaCodec_getName),decltype(Job::AMediaCodec_getNameFn)>::value,"Codec name ABI");
static_assert(std::is_same<decltype(&AMediaCodec_releaseName),decltype(Job::AMediaCodec_releaseNameFn)>::value,"Codec name release ABI");
#endif
static void Append(char* buf,size_t cap,const char* format,...) {
    size_t used=std::strlen(buf); if(used>=cap-1) return;
    va_list a; va_start(a,format); std::vsnprintf(buf+used,cap-used,format,a); va_end(a);
}
static void Fail(Job* j,const char* op,int media=0,EGLint egl=EGL_SUCCESS) {
    std::lock_guard<std::mutex> guard(j->failureMutex);
    if(!j->failed.load()) {
        j->failed=true; j->mediaError=media; j->eglError=egl;
        std::snprintf(j->failure,sizeof(j->failure),"%s",op);
    }
    // Completion derives pass from failed after all native threads have joined.
}
static bool Egl(Job* j,const char* op,bool ok) {
    EGLint error=j->eglGetErrorFn();
    std::unique_lock<std::mutex> traceGuard(j->traceMutex);
    if(!ok || error!=EGL_SUCCESS || (std::strcmp(op,"eglSwapBuffers")!=0 &&
        std::strcmp(op,"eglPresentationTimeANDROID")!=0))
        Append(j->trace,sizeof(j->trace),"%s=%d/0x%x; ",op,ok,error);
    traceGuard.unlock();
    if(!ok || error!=EGL_SUCCESS) { Fail(j,op,0,error); return false; }
    return true;
}
static bool Media(Job* j,const char* op,media_status_t status) {
    std::unique_lock<std::mutex> traceGuard(j->traceMutex);
    if(status!=AMEDIA_OK || (std::strcmp(op,"AMediaMuxer_writeSampleData")!=0 &&
        std::strcmp(op,"AMediaCodec_releaseOutputBuffer")!=0))
        Append(j->trace,sizeof(j->trace),"%s=%d; ",op,int(status));
    traceGuard.unlock();
    if(status!=AMEDIA_OK) { Fail(j,op,int(status)); return false; }
    return true;
}
static bool Cancelled(Job* j) {
    if(!j->cancel.load()) return false;
    Fail(j,"Cancelled between setup operations"); return true;
}
static void CleanEgl(Job* j,const char* op,EGLBoolean ok) {
    EGLint error=ok?EGL_SUCCESS:j->eglGetErrorFn();
    Append(j->cleanup,sizeof(j->cleanup),"%s=%d/0x%x; ",op,ok,error);
    if(!ok) j->clean=false;
}
static void CleanMedia(Job* j,const char* op,media_status_t status) {
    Append(j->cleanup,sizeof(j->cleanup),"%s=%d; ",op,int(status));
    if(status!=AMEDIA_OK) j->clean=false;
}
static bool Ext(const char* list,const char* name) {
    if(!list) return false;
    size_t n=std::strlen(name);
    for(const char* p=list;(p=std::strstr(p,name))!=nullptr;p+=n)
        if((p==list || p[-1]==' ') && (p[n]==' ' || p[n]=='\0')) return true;
    return false;
}
static bool Attr(Job* j,EGLConfig cfg,EGLint attr,const char* name,EGLint& value) {
    EGLBoolean ok=j->eglGetConfigAttribFn(j->display,cfg,attr,&value);
    char op[64]; std::snprintf(op,sizeof(op),"config.%s",name);
    if(!Egl(j,op,ok==EGL_TRUE)) return false;
    Append(j->trace,sizeof(j->trace),"value=0x%x; ",value); return true;
}
static bool Config(Job* j,EGLConfig& cfg) {
    const char* version=j->eglQueryStringFn(j->display,EGL_VERSION);
    if(!Egl(j,"eglQueryString(version)",version!=nullptr)) return false;
    const char* vendor=j->eglQueryStringFn(j->display,EGL_VENDOR);
    if(!Egl(j,"eglQueryString(vendor)",vendor!=nullptr)) return false;
    const char* extensions=j->eglQueryStringFn(j->display,EGL_EXTENSIONS);
    if(!Egl(j,"eglQueryString(extensions)",extensions!=nullptr)) return false;
    int major=0,minor=0; std::sscanf(version,"%d.%d",&major,&minor);
    bool create=Ext(extensions,"EGL_KHR_create_context");
    bool noConfig=Ext(extensions,"EGL_KHR_no_config_context");
    bool recordable=Ext(extensions,"EGL_ANDROID_recordable");
    Append(j->trace,sizeof(j->trace),"EGL=%.96s vendor=%.96s KHR_create_context=%d KHR_no_config_context=%d ANDROID_recordable=%d; ",
        version,vendor,create,noConfig,recordable);
    if(!(major>1 || (major==1 && minor>=5) || create) || !recordable) {
        Fail(j,"Required GLES3/recordable EGL support not advertised"); return false;
    }
    EGLint id=-1,type=0,clientMajor=0;
    EGLBoolean ok=j->eglQueryContextFn(j->display,j->unity,EGL_CONFIG_ID,&id);
    if(!Egl(j,"eglQueryContext(Unity config)",ok==EGL_TRUE)) return false;
    ok=j->eglQueryContextFn(j->display,j->unity,EGL_CONTEXT_CLIENT_TYPE,&type);
    if(!Egl(j,"eglQueryContext(Unity type)",ok==EGL_TRUE)) return false;
    ok=j->eglQueryContextFn(j->display,j->unity,EGL_CONTEXT_CLIENT_VERSION,&clientMajor);
    if(!Egl(j,"eglQueryContext(Unity version)",ok==EGL_TRUE)) return false;
    Append(j->trace,sizeof(j->trace),"UnityConfigId=%d type=0x%x major=%d; ",id,type,clientMajor);
    if(type!=EGL_OPENGL_ES_API || clientMajor<3) { Fail(j,"Unity context is not GLES3+"); return false; }
    EGLint count=0;
    if(id>0) {
        const EGLint attrs[]={EGL_CONFIG_ID,id,EGL_NONE};
        ok=j->eglChooseConfigFn(j->display,attrs,&cfg,1,&count);
        if(!Egl(j,"eglChooseConfig(Unity ID)",ok==EGL_TRUE)) return false;
    } else if(id==0 && noConfig) {
        const EGLint attrs[]={EGL_SURFACE_TYPE,EGL_WINDOW_BIT,
            EGL_RENDERABLE_TYPE,EGL_OPENGL_ES3_BIT_KHR,
            EGL_RED_SIZE,8,EGL_GREEN_SIZE,8,EGL_BLUE_SIZE,8,EGL_ALPHA_SIZE,8,
            EGL_RECORDABLE_ANDROID,EGL_TRUE,EGL_NONE};
        ok=j->eglChooseConfigFn(j->display,attrs,&cfg,1,&count);
        if(!Egl(j,"eglChooseConfig(confirmed configless window)",ok==EGL_TRUE)) return false;
    } else { Fail(j,"Unity configless state not confirmed"); return false; }
    Append(j->trace,sizeof(j->trace),"configCount=%d; ",count);
    if(count!=1) { Fail(j,"No compatible recordable window config"); return false; }
    EGLint selected=0,surface=0,render=0,r=0,g=0,b=0,a=0,record=0,visual=0;
    if(!Attr(j,cfg,EGL_CONFIG_ID,"id",selected) ||
       !Attr(j,cfg,EGL_SURFACE_TYPE,"surfaceMask",surface) ||
       !Attr(j,cfg,EGL_RENDERABLE_TYPE,"renderMask",render) ||
       !Attr(j,cfg,EGL_RED_SIZE,"red",r) || !Attr(j,cfg,EGL_GREEN_SIZE,"green",g) ||
       !Attr(j,cfg,EGL_BLUE_SIZE,"blue",b) || !Attr(j,cfg,EGL_ALPHA_SIZE,"alpha",a) ||
       !Attr(j,cfg,EGL_RECORDABLE_ANDROID,"recordable",record) ||
       !Attr(j,cfg,EGL_NATIVE_VISUAL_ID,"nativeVisual",visual)) return false;
    if((id>0 && selected!=id) || !(surface&EGL_WINDOW_BIT) ||
       !(render&EGL_OPENGL_ES3_BIT_KHR) || r<8 || g<8 || b<8 || a<8 || record!=EGL_TRUE) {
        Fail(j,"Resolved config lacks required recordable/GLES3/window/RGBA8 support"); return false;
    }
    Append(j->trace,sizeof(j->trace),"selectedConfig=%p; ",cfg); return true;
}
static void Complete(Job* j) {
    if(j->workerTid && j->eglReleaseThreadFn)
        CleanEgl(j,"eglReleaseThread",j->eglReleaseThreadFn());
    if(j->glLib) dlclose(j->glLib);
    if(j->androidLib) dlclose(j->androidLib);
    if(j->mediaLib) dlclose(j->mediaLib);
    if(j->eglLib) dlclose(j->eglLib);
    if(!j->clean || j->failed || j->cancel.load()) j->pass=false;
    j->done.store(1,std::memory_order_release); // Last worker access.
}

static double Ms(Clock::time_point a) {
    return std::chrono::duration<double,std::milli>(Clock::now()-a).count();
}
// Only worker/drain threads wait. After a timeout retain ownership until the fence
// actually signals. A permanently hung driver deliberately retains the job/textures.
static void WaitFence(Job* j,GLsync fence,bool producer) {
    auto start=Clock::now(); bool timed=false;
    for(;;) {
        GLenum result=j->glClientWaitSyncFn(fence,0,2000000);
        if(result==GL_ALREADY_SIGNALED || result==GL_CONDITION_SATISFIED) break;
        if(!timed && (Ms(start)>2000 || result==GL_WAIT_FAILED)) {
            timed=true; Fail(j,producer?"Producer fence wait timeout/error":"Consumer fence wait timeout/error");
            if(producer) ++j->producerTimeouts; else ++j->consumerTimeouts;
        }
        if(result==GL_WAIT_FAILED) usleep(2000);
    }
    j->glDeleteSyncFn(fence);
    if(producer) j->producerWaitMs+=Ms(start); else j->consumerWaitMs+=Ms(start);
}
static void* Drain(void* ptr) {
    Job* j=static_cast<Job*>(ptr);
    auto start=Clock::now(); ssize_t track=-1;
    while(!j->drainStop.load() && !j->eos.load()) {
        if(Ms(start)>20000) { Fail(j,"Encoder drain deadline (20s)"); break; }
        auto call=Clock::now();
        AMediaCodecBufferInfo info{};
        ssize_t index=j->AMediaCodec_dequeueOutputBufferFn(j->drainCodec,&info,10000);
        j->maxDrainMs=std::max(j->maxDrainMs,Ms(call));
        if(index==AMEDIACODEC_INFO_TRY_AGAIN_LATER) continue;
        if(index==AMEDIACODEC_INFO_OUTPUT_BUFFERS_CHANGED) continue;
        if(index==AMEDIACODEC_INFO_OUTPUT_FORMAT_CHANGED) {
            if(++j->formatChanges!=1) { Fail(j,"Repeated codec output format change"); break; }
            AMediaFormat* f=j->AMediaCodec_getOutputFormatFn(j->drainCodec);
            if(!f) { Fail(j,"Missing codec output format"); break; }
            track=j->AMediaMuxer_addTrackFn(j->muxer,f);
            j->AMediaFormat_deleteFn(f);
            if(track<0) { Fail(j,"AMediaMuxer_addTrack",int(track)); break; }
            if(!Media(j,"AMediaMuxer_start",j->AMediaMuxer_startFn(j->muxer))) break;
            j->muxerStarted=true;
            continue;
        }
        if(index<0) { Fail(j,"AMediaCodec_dequeueOutputBuffer",int(index)); break; }
        // Always release a dequeued buffer, including failed writes and EOS.
        bool config=(info.flags&AMEDIACODEC_BUFFER_FLAG_CODEC_CONFIG)!=0;
        bool end=(info.flags&AMEDIACODEC_BUFFER_FLAG_END_OF_STREAM)!=0;
        if(config) ++j->configBuffers;
        if(info.size>0 && !config) {
            size_t capacity=0;
            uint8_t* bytes=j->AMediaCodec_getOutputBufferFn(j->drainCodec,size_t(index),&capacity);
            int sample=j->samples.load();
            if(!bytes || info.offset<0 || size_t(info.offset)>capacity ||
                size_t(info.size)>capacity-size_t(info.offset) || !j->muxerStarted ||
                sample>=j->frameLimit || info.presentationTimeUs<0 ||
                (sample>0 && info.presentationTimeUs<=j->lastPts) ||
                (info.flags&AMEDIACODEC_BUFFER_FLAG_PARTIAL_FRAME)) {
                Fail(j,"Invalid/nonmonotonic/partial encoded output or missing format");
            } else {
                AMediaCodecBufferInfo normalized=info; normalized.offset=0;
                if(Media(j,"AMediaMuxer_writeSampleData",
                    j->AMediaMuxer_writeSampleDataFn(j->muxer,size_t(track),bytes+info.offset,&normalized))) {
                    j->samplePts[sample]=info.presentationTimeUs;
                    j->sampleBytes[sample]=info.size; j->sampleFlags[sample]=int(info.flags);
                    if(sample==0) j->firstPts=info.presentationTimeUs;
                    j->lastPts=info.presentationTimeUs;
                    if(info.flags&AMEDIACODEC_BUFFER_FLAG_KEY_FRAME) ++j->keySamples;
                    j->samples.fetch_add(1);
                }
            }
        }
        if(!Media(j,"AMediaCodec_releaseOutputBuffer",
            j->AMediaCodec_releaseOutputBufferFn(j->drainCodec,size_t(index),false))) break;
        if(end) j->eos.store(1);
        if(j->failed.load()) break;
    }
    j->drainDone.store(1,std::memory_order_release);
    return nullptr;
}
static GLuint Shader(Job* j,GLenum type,const char* text) {
    GLuint s=j->glCreateShaderFn(type);
    if(!s) { Fail(j,"GL shader allocation"); return 0; }
    j->glShaderSourceFn(s,1,&text,nullptr); j->glCompileShaderFn(s);
    GLint ok=0; j->glGetShaderivFn(s,GL_COMPILE_STATUS,&ok);
    if(!ok) { j->glDeleteShaderFn(s); Fail(j,"Encoder copy shader compilation"); return 0; }
    return s;
}
static bool Record(Job* j,AMediaCodec* codec,EGLSurface surface) {
    auto start=Clock::now();
    GLuint vs=0,fs=0,program=0,vao=0; pthread_t drain{};
    bool drainStarted=false;
    do {
        j->glLib=dlopen("libGLESv3.so",RTLD_NOW|RTLD_LOCAL);
        if(!j->glLib) { Fail(j,"Load GLES3"); break; }
        bool loaded=true;
#define G(n) j->n##Fn=reinterpret_cast<decltype(j->n##Fn)>(dlsym(j->glLib,#n)); loaded=loaded && j->n##Fn;
        G(glFenceSync) G(glClientWaitSync) G(glDeleteSync) G(glFlush) G(glGetError)
        G(glCreateShader) G(glShaderSource) G(glCompileShader) G(glGetShaderiv) G(glDeleteShader)
        G(glCreateProgram) G(glAttachShader) G(glLinkProgram) G(glGetProgramiv) G(glDeleteProgram)
        G(glGenVertexArrays) G(glBindVertexArray) G(glDeleteVertexArrays)
        G(glUseProgram) G(glGetUniformLocation) G(glUniform1i) G(glActiveTexture)
        G(glBindTexture) G(glViewport) G(glDrawArrays) G(glDisable)
#undef G
        j->presentation=reinterpret_cast<PFNEGLPRESENTATIONTIMEANDROIDPROC>(
            j->eglGetProcAddressFn("eglPresentationTimeANDROID"));
        const char* extensions=j->eglQueryStringFn(j->display,EGL_EXTENSIONS);
        if(!loaded || !j->presentation || !Ext(extensions,"EGL_ANDROID_presentation_time")) {
            Fail(j,"GLES entry points / presentation time extension unavailable"); break;
        }
        vs=Shader(j,GL_VERTEX_SHADER,
            "#version 300 es\nout vec2 uv; void main(){vec2 p=vec2(float((gl_VertexID<<1)&2),float(gl_VertexID&2));uv=p;gl_Position=vec4(p*2.0-1.0,0,1);}");
        fs=Shader(j,GL_FRAGMENT_SHADER,
            "#version 300 es\nprecision highp float; in vec2 uv; uniform sampler2D image; out vec4 color; void main(){color=texture(image,uv);}");
        if(!vs || !fs) break;
        program=j->glCreateProgramFn(); j->glAttachShaderFn(program,vs);
        j->glAttachShaderFn(program,fs); j->glLinkProgramFn(program);
        GLint linked=0; j->glGetProgramivFn(program,GL_LINK_STATUS,&linked);
        if(!linked) { Fail(j,"Encoder copy program link"); break; }
        j->glGenVertexArraysFn(1,&vao); j->glBindVertexArrayFn(vao);
        j->glUseProgramFn(program);
        j->glUniform1iFn(j->glGetUniformLocationFn(program,"image"),0);
        j->glActiveTextureFn(GL_TEXTURE0); j->glViewportFn(0,0,1920,1080);
        j->glDisableFn(GL_BLEND); j->glDisableFn(GL_DEPTH_TEST); j->glDisableFn(GL_SCISSOR_TEST);
        if(j->glGetErrorFn()!=GL_NO_ERROR) { Fail(j,"Encoder GL initialization"); break; }
        j->outputFd=open(j->path,O_CREAT|O_EXCL|O_RDWR|O_CLOEXEC,0600);
        if(j->outputFd<0) { Fail(j,"Create unique partial MP4",errno); break; }
        j->muxer=j->AMediaMuxer_newFn(j->outputFd,AMEDIAMUXER_OUTPUT_FORMAT_MPEG_4);
        if(!j->muxer) { Fail(j,"AMediaMuxer_new"); break; }
        j->drainCodec=codec;
        int threadStatus=pthread_create(&drain,nullptr,Drain,j);
        if(threadStatus!=0) { Fail(j,"Drain thread start",threadStatus); break; }
        drainStarted=true;
        j->ready.store(1,std::memory_order_release);
        int64_t lastInput=-1;
        while(!j->failed.load() && !j->cancel.load()) {
            if(Ms(start)>15000) { Fail(j,"Producer/worker deadline (15s)"); break; }
            if(j->producerError.load()) { Fail(j,"Producer callback GL error",j->producerError.load()); break; }
            Slot* slot=nullptr;
            for(auto& s:j->slots) if(s.state.load(std::memory_order_acquire)==2 &&
                (!slot || s.frame<slot->frame)) slot=&s;
            if(!slot) {
                bool empty=true;
                for(auto& s:j->slots) if(s.state.load()!=0) empty=false;
                if(j->ended.load() && empty) break;
                usleep(1000); continue;
            }
            slot->state.store(3);
            WaitFence(j,slot->producer,true); slot->producer=nullptr;
            if(!j->failed.load() && !j->cancel.load()) {
                if(slot->pts<=lastInput) Fail(j,"Nonmonotonic input PTS");
                else {
                    j->glBindTextureFn(GL_TEXTURE_2D,slot->texture);
                    j->glDrawArraysFn(GL_TRIANGLES,0,3);
                    GLsync consumed=j->glFenceSyncFn(GL_SYNC_GPU_COMMANDS_COMPLETE,0);
                    j->glFlushFn();
                    if(!consumed) {
                        Fail(j,"Consumer fence creation failed; retaining texture");
                        // Cannot prove GPU completion. Deliberately never free this in-use job.
                        for(;;) usleep(100000);
                    }
                    WaitFence(j,consumed,false);
                    if(j->glGetErrorFn()!=GL_NO_ERROR) Fail(j,"Encoder draw GL error");
                    if(!j->failed.load() && Egl(j,"eglPresentationTimeANDROID",
                        j->presentation(j->display,surface,slot->pts)==EGL_TRUE)) {
                        auto swapStart=Clock::now();
                        bool ok=Egl(j,"eglSwapBuffers",j->eglSwapBuffersFn(j->display,surface)==EGL_TRUE);
                        j->maxSwapMs=std::max(j->maxSwapMs,Ms(swapStart));
                        if(ok) { int index=j->swapped.fetch_add(1); j->inputPts[index]=slot->pts/1000; lastInput=slot->pts; }
                    }
                }
            }
            slot->state.store(0,std::memory_order_release);
        }
        // Stop new reservations before retiring the bounded pending queue.
        j->ready.store(-1);
        for(;;) {
            bool empty=true;
            for(auto& s:j->slots) {
                int state=s.state.load(std::memory_order_acquire);
                if(state==2) {
                    WaitFence(j,s.producer,true); s.producer=nullptr;
                    s.state.store(0,std::memory_order_release);
                } else if(state!=0) empty=false;
            }
            if(empty && j->outstanding.load()==0) break;
            usleep(1000); // A submitted render callback must execute before resources can be freed.
        }
        if(!Media(j,"AMediaCodec_signalEndOfInputStream",j->signalEOS(codec))) break;
        auto eosStart=Clock::now();
        while(!j->drainDone.load(std::memory_order_acquire) && Ms(eosStart)<6000) usleep(1000);
        if(!j->drainDone.load()) { Fail(j,"Encoder EOS timeout (6s)"); j->drainStop.store(1); }
    } while(false);
    j->ready.store(-1);
    if(drainStarted) {
        j->drainStop.store(1);
        pthread_join(drain,nullptr); // Never on Unity's main/render threads.
    }
    if(j->muxerStarted) CleanMedia(j,"AMediaMuxer_stop",j->AMediaMuxer_stopFn(j->muxer));
    if(j->muxer) CleanMedia(j,"AMediaMuxer_delete",j->AMediaMuxer_deleteFn(j->muxer));
    if(j->outputFd>=0) {
        if(fsync(j->outputFd)!=0) { j->clean=false; Fail(j,"fsync partial MP4",errno); }
        if(close(j->outputFd)!=0) { j->clean=false; Fail(j,"close partial MP4",errno); }
    }
    if(vao) j->glDeleteVertexArraysFn(1,&vao);
    if(program) j->glDeleteProgramFn(program);
    if(vs) j->glDeleteShaderFn(vs);
    if(fs) j->glDeleteShaderFn(fs);
    j->elapsedMs=Ms(start);
    bool matched=j->swapped.load()>0 && j->samples.load()==j->swapped.load();
    if(matched) for(int i=0;i<j->samples.load();++i)
        if(std::llabs(j->samplePts[i]-j->inputPts[i])>1) { matched=false; Fail(j,"Encoded PTS differs from submitted slot by more than 1us"); break; }
    if(!j->failed.load() && !j->cancel.load() && (!j->eos.load() || !matched || !j->muxerStarted || j->keySamples<1))
        Fail(j,"EOS/sample count/muxer completion mismatch");
    return !j->failed.load() && !j->cancel.load() && j->clean && matched && j->eos.load();
}

static void* Worker(void* ptr) {
    Job* j=static_cast<Job*>(ptr); j->workerTid=syscall(SYS_gettid);
    AMediaCodec* codec=nullptr; AMediaFormat* format=nullptr; ANativeWindow* window=nullptr;
    EGLContext context=EGL_NO_CONTEXT; EGLSurface surface=EGL_NO_SURFACE;
    bool started=false,current=false;
    do {
        if(Cancelled(j)) break;
        j->mediaLib=dlopen("libmediandk.so",RTLD_NOW|RTLD_LOCAL);
        j->androidLib=dlopen("libandroid.so",RTLD_NOW|RTLD_LOCAL);
        if(!j->mediaLib || !j->androidLib) { Fail(j,"Cannot load Android media/window library"); break; }
        bool loaded=true;
#define M(n) j->n##Fn=reinterpret_cast<decltype(j->n##Fn)>(dlsym(j->mediaLib,#n)); loaded=loaded && j->n##Fn;
        M(AMediaCodec_createCodecByName) M(AMediaCodec_getName) M(AMediaCodec_releaseName)
        M(AMediaFormat_new) M(AMediaFormat_setString) M(AMediaFormat_setInt32) M(AMediaFormat_delete)
        M(AMediaCodec_configure) M(AMediaCodec_createInputSurface) M(AMediaCodec_start)
        M(AMediaCodec_stop) M(AMediaCodec_delete)
        M(AMediaCodec_dequeueOutputBuffer) M(AMediaCodec_getOutputBuffer)
        M(AMediaCodec_getOutputFormat) M(AMediaCodec_releaseOutputBuffer)
        M(AMediaMuxer_new) M(AMediaMuxer_addTrack) M(AMediaMuxer_start)
        M(AMediaMuxer_writeSampleData) M(AMediaMuxer_stop) M(AMediaMuxer_delete)
#undef M
        j->signalEOS=reinterpret_cast<decltype(j->signalEOS)>(dlsym(j->mediaLib,"AMediaCodec_signalEndOfInputStream"));
        if(!j->signalEOS) loaded=false;
        j->ANativeWindow_releaseFn=reinterpret_cast<decltype(j->ANativeWindow_releaseFn)>(dlsym(j->androidLib,"ANativeWindow_release"));
        if(!loaded || !j->ANativeWindow_releaseFn) { Fail(j,"Required media/window entry point missing (getName needs API28+)"); break; }
        codec=j->AMediaCodec_createCodecByNameFn(j->requested);
        Append(j->trace,sizeof(j->trace),"AMediaCodec_createCodecByName(%s)=%p; ",j->requested,codec);
        if(!codec) { Fail(j,"Named hardware codec creation returned null"); break; }
        char* actual=nullptr;
        media_status_t nameStatus=j->AMediaCodec_getNameFn(codec,&actual);
        bool nameOk=Media(j,"AMediaCodec_getName",nameStatus);
        if(actual) {
            std::snprintf(j->actual,sizeof(j->actual),"%s",actual);
            j->AMediaCodec_releaseNameFn(codec,actual);
            Append(j->trace,sizeof(j->trace),"actualName=%s; AMediaCodec_releaseName=done; ",j->actual);
        }
        if(!nameOk) break;
        if(std::strcmp(j->actual,j->requested)!=0 && std::strcmp(j->actual,j->canonical)!=0) {
            Fail(j,"Actual codec does not match verified name/canonical name"); break;
        }
        if(Cancelled(j)) break;
        format=j->AMediaFormat_newFn();
        Append(j->trace,sizeof(j->trace),"AMediaFormat_new=%p; ",format);
        if(!format) { Fail(j,"Media format allocation failed"); break; }
        j->AMediaFormat_setStringFn(format,"mime","video/avc");
        j->AMediaFormat_setInt32Fn(format,"width",1920);
        j->AMediaFormat_setInt32Fn(format,"height",1080);
        j->AMediaFormat_setInt32Fn(format,"frame-rate",30);
        j->AMediaFormat_setInt32Fn(format,"bitrate",16000000);
        j->AMediaFormat_setInt32Fn(format,"i-frame-interval",1);
        j->AMediaFormat_setInt32Fn(format,"color-format",0x7f000789);
        // Baseline avoids B-frame reordering; actual stream still needs decoder inspection.
        j->AMediaFormat_setInt32Fn(format,"profile",1);
        j->AMediaFormat_setInt32Fn(format,"max-bframes",0);
        Append(j->trace,sizeof(j->trace),"format setters=video/avc 1920x1080 fps30 bitrate16000000 GOP1 surface0x7f000789; ");
        if(!Media(j,"AMediaCodec_configure",j->AMediaCodec_configureFn(codec,format,nullptr,nullptr,AMEDIACODEC_CONFIGURE_FLAG_ENCODE))) break;
        if(!Media(j,"AMediaCodec_createInputSurface",j->AMediaCodec_createInputSurfaceFn(codec,&window))) break;
        if(!window) { Fail(j,"Input surface returned null native window"); break; }
        Append(j->trace,sizeof(j->trace),"inputWindow=%p; ",window);
        if(Cancelled(j)) break;
        if(!Media(j,"AMediaCodec_start",j->AMediaCodec_startFn(codec))) break;
        started=true;
        EGLint entry=j->eglGetErrorFn();
        Append(j->trace,sizeof(j->trace),"display=%p unityContext=%p workerEntryEGL=0x%x; ",j->display,j->unity,entry);
        EGLConfig cfg=nullptr;
        if(!Config(j,cfg) || Cancelled(j)) break;
        EGLBoolean ok=j->eglBindAPIFn(EGL_OPENGL_ES_API);
        if(!Egl(j,"eglBindAPI(GLES)",ok==EGL_TRUE)) break;
        const EGLint ca[]={EGL_CONTEXT_CLIENT_VERSION,3,EGL_NONE};
        context=j->eglCreateContextFn(j->display,cfg,j->unity,ca);
        if(!Egl(j,"eglCreateContext(shared GLES3)",context!=EGL_NO_CONTEXT)) break;
        const EGLint wa[]={EGL_NONE};
        surface=j->eglCreateWindowSurfaceFn(j->display,cfg,window,wa);
        if(!Egl(j,"eglCreateWindowSurface(encoder)",surface!=EGL_NO_SURFACE)) break;
        ok=j->eglMakeCurrentFn(j->display,surface,surface,context);
        if(!Egl(j,"eglMakeCurrent(encoder worker)",ok==EGL_TRUE)) break;
        current=true;
        EGLint width=0,height=0,version=0;
        ok=j->eglQuerySurfaceFn(j->display,surface,EGL_WIDTH,&width);
        if(!Egl(j,"eglQuerySurface(width)",ok==EGL_TRUE)) break;
        ok=j->eglQuerySurfaceFn(j->display,surface,EGL_HEIGHT,&height);
        if(!Egl(j,"eglQuerySurface(height)",ok==EGL_TRUE)) break;
        ok=j->eglQueryContextFn(j->display,context,EGL_CONTEXT_CLIENT_VERSION,&version);
        if(!Egl(j,"eglQueryContext(worker GLES)",ok==EGL_TRUE)) break;
        Append(j->trace,sizeof(j->trace),"workerContext=%p windowSurface=%p actualSurface=%dx%d GLESmajor=%d; ",context,surface,width,height,version);
        if(width!=1920 || height!=1080 || version<3) { Fail(j,"Actual encoder surface dimensions/GLES version mismatch"); break; }
        if(Cancelled(j)) break;
        j->pass=j->recording ? Record(j,codec,surface) : true;
    } while(false);
    j->ready.store(-1);
    // Record joins its drain thread and retires all queued GPU accesses before this cleanup.
    if(current) CleanEgl(j,"eglMakeCurrent(unbind)",j->eglMakeCurrentFn(j->display,EGL_NO_SURFACE,EGL_NO_SURFACE,EGL_NO_CONTEXT));
    if(surface!=EGL_NO_SURFACE) CleanEgl(j,"eglDestroySurface",j->eglDestroySurfaceFn(j->display,surface));
    if(context!=EGL_NO_CONTEXT) CleanEgl(j,"eglDestroyContext",j->eglDestroyContextFn(j->display,context));
    if(started) CleanMedia(j,"AMediaCodec_stop",j->AMediaCodec_stopFn(codec));
    if(codec) CleanMedia(j,"AMediaCodec_delete",j->AMediaCodec_deleteFn(codec));
    if(window) { j->ANativeWindow_releaseFn(window); Append(j->cleanup,sizeof(j->cleanup),"ANativeWindow_release=done; "); }
    if(format) { j->AMediaFormat_deleteFn(format); Append(j->cleanup,sizeof(j->cleanup),"AMediaFormat_delete=done; "); }
    if(j->recording && j->pass && j->clean && !j->cancel.load()) {
        char finalPath[1024]; std::snprintf(finalPath,sizeof(finalPath),"%s",j->path);
        size_t len=std::strlen(finalPath);
        if(len>8 && std::strcmp(finalPath+len-8,".partial")==0) {
            finalPath[len-8]='\0';
            // Atomic no-replace rename; unlike hard links this supports Android emulated storage.
            if(syscall(SYS_renameat2,AT_FDCWD,j->path,AT_FDCWD,finalPath,1 /* RENAME_NOREPLACE */)==0) {
                j->finalized=true;

            } else Fail(j,"Publishing MP4 without overwrite",errno);
        } else Fail(j,"Invalid partial suffix");
    }
    Complete(j); return nullptr;
}
static void Callback(int eventId,void* ptr) {
    Job* j=static_cast<Job*>(ptr); if(!j) return;
    if(eventId>=2 && eventId<=4) {
        auto start=Clock::now();
        Slot& s=j->slots[eventId-2];
        // Commands generating this slot precede the callback in the same command buffer.
        s.producer=j->glFenceSyncFn(GL_SYNC_GPU_COMMANDS_COMPLETE,0);
        j->glFlushFn();
        GLenum error=j->glGetErrorFn();
        if(!s.producer || error!=GL_NO_ERROR) {
            j->producerError.store(error?int(error):-1);
            // No safe producer completion evidence: retain the reserved slot indefinitely.
            s.state.store(4,std::memory_order_release);
        } else {
            j->submitted.fetch_add(1);
            s.state.store(2,std::memory_order_release);
        }
        int64_t ns=std::chrono::duration_cast<std::chrono::nanoseconds>(Clock::now()-start).count();
        int64_t old=j->callbackMaxNs.load();
        while(old<ns && !j->callbackMaxNs.compare_exchange_weak(old,ns)) {}
        j->outstanding.fetch_sub(1,std::memory_order_release); // Last access.
        return;
    }
    if(eventId!=1) return;
    auto start=Clock::now(); j->callbackTid=syscall(SYS_gettid);
    struct Guard { Job* j; Clock::time_point start; ~Guard() {
        j->callbackMs=std::chrono::duration<double,std::milli>(Clock::now()-start).count();
        j->callbackDone.store(1,std::memory_order_release);
    }} guard{j,start};
    j->eglLib=dlopen("libEGL.so",RTLD_NOW|RTLD_LOCAL);
    if(!j->eglLib) { Fail(j,"Cannot load EGL library"); Complete(j); return; }
    bool loaded=true;
#define E(n) j->n##Fn=reinterpret_cast<decltype(j->n##Fn)>(dlsym(j->eglLib,#n)); loaded=loaded && j->n##Fn;
    E(eglGetCurrentDisplay) E(eglGetCurrentContext) E(eglGetError) E(eglGetProcAddress)
    E(eglQueryString) E(eglQueryContext) E(eglChooseConfig) E(eglGetConfigAttrib)
    E(eglBindAPI) E(eglCreateContext) E(eglCreateWindowSurface) E(eglMakeCurrent)
    E(eglSwapBuffers) E(eglQuerySurface) E(eglDestroySurface) E(eglDestroyContext) E(eglReleaseThread)
#undef E
    if(!j->eglSwapBuffersFn) { Fail(j,"Required EGL entry point missing: eglSwapBuffers"); Complete(j); return; }
    if(!loaded) { Fail(j,"Required EGL entry point missing"); Complete(j); return; }
    EGLint entryError=j->eglGetErrorFn();
    Append(j->trace,sizeof(j->trace),"callbackEntryEGL=0x%x; ",entryError);
    j->display=j->eglGetCurrentDisplayFn();
    if(!Egl(j,"eglGetCurrentDisplay(callback)",j->display!=EGL_NO_DISPLAY)) { Complete(j); return; }
    j->unity=j->eglGetCurrentContextFn();
    if(!Egl(j,"eglGetCurrentContext(callback)",j->unity!=EGL_NO_CONTEXT)) { Complete(j); return; }
    Append(j->trace,sizeof(j->trace),"capturedDisplay=%p capturedContext=%p; ",j->display,j->unity);
    pthread_attr_t attr;
    if(pthread_attr_init(&attr)!=0) { Fail(j,"Thread attribute initialization failed"); Complete(j); return; }
    int status=pthread_attr_setdetachstate(&attr,PTHREAD_CREATE_DETACHED);
    pthread_t worker;
    if(status==0) status=pthread_create(&worker,&attr,Worker,j);
    pthread_attr_destroy(&attr);
    if(status!=0) { Fail(j,"Worker startup failed"); Complete(j); }
}
static_assert(sizeof(void*)==8,"ARM64 checkpoint");
static_assert(std::is_same<decltype(&Callback),void(*)(int,void*)>::value,"Render callback ABI");
}
#define EW_EXPORT extern "C" __attribute__((visibility("default")))
EW_EXPORT int EW1_Abi() { return 1; }
EW_EXPORT void* EW1_Callback() { return reinterpret_cast<void*>(&EncoderWindow::Callback); }
EW_EXPORT void* EW1_Create(const char* requested,const char* canonical) {
    if(!requested || !canonical || !requested[0] || !canonical[0] ||
       std::strlen(requested)>=256 || std::strlen(canonical)>=256) return nullptr;
    auto* j=new(std::nothrow) EncoderWindow::Job; if(!j) return nullptr;
    std::snprintf(j->requested,sizeof(j->requested),"%s",requested);
    std::snprintf(j->canonical,sizeof(j->canonical),"%s",canonical);
    j->callerTid=syscall(SYS_gettid); return j;
}
EW_EXPORT int EW1_Done(void* p) {
    auto* j=static_cast<EncoderWindow::Job*>(p);
    return j->done.load(std::memory_order_acquire) && j->callbackDone.load(std::memory_order_acquire) && j->outstanding.load()==0;
}
EW_EXPORT const char* EW1_Report(void* p) {
    auto* j=static_cast<EncoderWindow::Job*>(p);
    std::snprintf(j->report,sizeof(j->report),
        "pass=%d cleanup=%d cancelled=%d callerTid=%ld callbackTid=%ld workerTid=%ld callbackMs=%.3f requested=%s canonical=%s actual=%s firstFailure=%s Media=%d EGL=0x%x framesSubmitted=0 swaps=0 MP4=not-created; trace={%s} cleanupTrace={%s}",
        j->pass,j->clean,j->cancel.load(),j->callerTid,j->callbackTid,j->workerTid,j->callbackMs,
        j->requested,j->canonical,j->actual,j->failure,j->mediaError,j->eglError,j->trace,j->cleanup);
    if(j->recording) {
        std::snprintf(j->report,sizeof(j->report),
            "schema=2 pass=%d cleanup=%d cancelled=%d finalized=%d requested=%s actual=%s firstFailure=%s Media=%d EGL=0x%x "
            "submitted=%d swapped=%d encodedSamples=%d target=%d cameraMode=%d capacity=%d expectedDecodedFrames=%d decodedFrames=UNVERIFIED "
            "EOS=%d formatChanges=%d codecConfigBuffers=%d keySamples=%d queueHigh=%d "
            "elapsedMs=%.3f maxSwapMs=%.3f producerWaitTotalMs=%.3f consumerWaitTotalMs=%.3f "
            "producerTimeouts=%d consumerTimeouts=%d maxDrainCallMs=%.3f producerCallbackMaxMs=%.3f initCallbackMs=%.3f "
            "callerTid=%ld callbackTid=%ld workerTid=%ld firstPtsUs=%lld lastPtsUs=%lld "
            "path=%s; trace={%s} cleanupTrace={%s}; samples=[",
            int(j->pass),int(j->clean),j->cancel.load(),int(j->finalized),j->requested,j->actual,j->failure,
            j->mediaError,j->eglError,j->submitted.load(),j->swapped.load(),j->samples.load(),
            j->realCamera?0:150,int(j->realCamera),j->frameLimit,j->samples.load(),
            j->eos.load(),j->formatChanges,j->configBuffers,j->keySamples,j->queueHigh.load(),
            j->elapsedMs,j->maxSwapMs,j->producerWaitMs,j->consumerWaitMs,j->producerTimeouts,j->consumerTimeouts,
            j->maxDrainMs,double(j->callbackMaxNs.load())/1e6,j->callbackMs,j->callerTid,j->callbackTid,j->workerTid,
            static_cast<long long>(j->firstPts),static_cast<long long>(j->lastPts),j->path,j->trace,j->cleanup);
        for(int i=0;i<j->samples.load() && i<j->frameLimit;++i)
            EncoderWindow::Append(j->report,sizeof(j->report),"%s%lld:%d:0x%x",i?",":"",
                static_cast<long long>(j->samplePts[i]),j->sampleBytes[i],j->sampleFlags[i]);
        EncoderWindow::Append(j->report,sizeof(j->report),"]; sampleTuple=ptsUs:bytes:flags; decoded count needs external decoder");
    }
    return j->report;
}
EW_EXPORT int EW1_Passed(void* p) { return static_cast<EncoderWindow::Job*>(p)->pass; }
EW_EXPORT void EW1_Cancel(void* p) { static_cast<EncoderWindow::Job*>(p)->cancel.store(1); }
EW_EXPORT void EW1_Destroy(void* p) { delete static_cast<EncoderWindow::Job*>(p); }

EW_EXPORT void* EW2_Create(const char* requested,const char* canonical,const char* path,
                           uintptr_t a,uintptr_t b,uintptr_t c) {
    if(!path || std::strlen(path)>=1024 || std::strlen(path)<9 ||
       std::strcmp(path+std::strlen(path)-8,".partial")!=0 ||
       !a || !b || !c || a>UINT32_MAX || b>UINT32_MAX || c>UINT32_MAX || a==b || b==c || a==c) return nullptr;
    auto* j=static_cast<EncoderWindow::Job*>(EW1_Create(requested,canonical));
    if(!j) return nullptr;
    j->recording=true;
    std::snprintf(j->path,sizeof(j->path),"%s",path);
    j->slots[0].texture=GLuint(a); j->slots[1].texture=GLuint(b); j->slots[2].texture=GLuint(c);
    return j;
}
// Camera mode keeps the verified synthetic exports and fixed synthetic timestamp contract intact.
EW_EXPORT void* EW3_Create(const char* requested,const char* canonical,const char* path,
                           uintptr_t a,uintptr_t b,uintptr_t c) {
    auto* j=static_cast<EncoderWindow::Job*>(EW2_Create(requested,canonical,path,a,b,c));
    if(j) { j->realCamera=true; j->frameLimit=320; }
    return j;
}
EW_EXPORT int EW2_Ready(void* ptr) {
    return static_cast<EncoderWindow::Job*>(ptr)->ready.load(std::memory_order_acquire);
}
EW_EXPORT int EW2_Reserve(void* ptr,int frame,int64_t pts) {
    auto* j=static_cast<EncoderWindow::Job*>(ptr);
    bool invalid=j->realCamera
        ? (frame!=j->reservedFrames || frame<0 || frame>=j->frameLimit || pts<0 ||
           pts>=10000000000LL || (frame==0 && pts!=0) ||
           (frame>0 && pts/1000<=j->lastReservedPts/1000))
        : (frame<0 || frame>=150 || pts!=int64_t(frame)*1000000000LL/30);
    if(invalid || j->ready.load()!=1 || j->cancel.load() || j->ended.load()) return -1;
    for(int i=0;i<3;++i) {
        int free=0;
        if(!j->slots[i].state.compare_exchange_strong(free,1)) continue;
        j->outstanding.fetch_add(1);
        if(j->ready.load()!=1 || j->cancel.load() || j->ended.load()) {
            j->slots[i].state.store(0);
            j->outstanding.fetch_sub(1); return -1;
        }
        j->slots[i].frame=frame; j->slots[i].pts=pts;
        if(j->realCamera) { ++j->reservedFrames; j->lastReservedPts=pts; }
        int occupancy=0;
        for(auto& s:j->slots) if(s.state.load()!=0) ++occupancy;
        int high=j->queueHigh.load();
        while(high<occupancy && !j->queueHigh.compare_exchange_weak(high,occupancy)) {}
        return i;
    }
    return -1;
}
EW_EXPORT void EW2_End(void* ptr) {
    static_cast<EncoderWindow::Job*>(ptr)->ended.store(1);
}

#endif
