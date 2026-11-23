// VolumeBridgeApo: endpoint-effect (EFX) Audio Processing Object that applies the Windows volume
// slider / mute of a speaker whose hardware ignores them. In-place float32 gain with a short
// linear ramp; zero added latency.
//
// Volume source, in order of preference:
//   1. Windows 11 APO notifications (APO_NOTIFICATION_TYPE_ENDPOINT_VOLUME) for this endpoint.
//   2. The gain the VolumeBridge tray app writes into the shared file mapping.
//   3. Unity gain (behaves like no APO) until one of the above is known.
//
// MIT License, (c) 2026 ZanderH-code. Written against the Windows SDK APO headers.

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <atomic>
#include <cmath>
#include <new>
#include <mmdeviceapi.h>
#include <audioenginebaseapo.h>
#include <audioengineextensionapo.h>
#include <BaseAudioProcessingObject.h>
#include <shlobj.h>
#include <strsafe.h>
#include "SharedState.h"

// {B2800674-8C35-428D-A3EA-750171E7DD5C}
static const CLSID CLSID_VolumeBridgeApo =
    { 0xb2800674, 0x8c35, 0x428d, { 0xa3, 0xea, 0x75, 0x01, 0x71, 0xe7, 0xdd, 0x5c } };
static const wchar_t* const kClsidString = L"{B2800674-8C35-428D-A3EA-750171E7DD5C}";

static HMODULE g_module = nullptr;
static std::atomic<long> g_objects{ 0 };
static std::atomic<long> g_locks{ 0 };

// ---------------------------------------------------------------------------------------------
// Shared state (file mapping in %ProgramData%\VolumeBridge\apo.dat). Opened once per process.
// ---------------------------------------------------------------------------------------------
static SRWLOCK g_sharedLock = SRWLOCK_INIT;
static VbShared* g_shared = nullptr;

static VbShared* OpenShared()
{
    AcquireSRWLockExclusive(&g_sharedLock);
    if (g_shared == nullptr)
    {
        wchar_t path[MAX_PATH];
        if (SUCCEEDED(SHGetFolderPathW(nullptr, CSIDL_COMMON_APPDATA, nullptr, SHGFP_TYPE_CURRENT, path)) &&
            SUCCEEDED(StringCchCatW(path, MAX_PATH, L"\\VolumeBridge\\apo.dat")))
        {
            HANDLE file = CreateFileW(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
                                      nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
            if (file != INVALID_HANDLE_VALUE)
            {
                HANDLE map = CreateFileMappingW(file, nullptr, PAGE_READWRITE, 0, VB_SHARED_SIZE, nullptr);
                CloseHandle(file);
                if (map != nullptr)
                {
                    void* view = MapViewOfFile(map, FILE_MAP_ALL_ACCESS, 0, 0, VB_SHARED_SIZE);
                    CloseHandle(map); // the view keeps the mapping alive
                    if (view != nullptr)
                    {
                        auto* s = static_cast<VbShared*>(view);
                        if (s->magic != VB_SHARED_MAGIC)
                        {
                            ZeroMemory(s, sizeof(VbShared));
                            s->version = 1;
                            s->apoMasterVolume = -1.0f;
                            InterlockedExchange(reinterpret_cast<volatile LONG*>(&s->magic), static_cast<LONG>(VB_SHARED_MAGIC));
                        }
                        // A fresh audiodg process: counts left by a previous (crashed/restarted) one are stale.
                        InterlockedExchange(reinterpret_cast<volatile LONG*>(&s->apoInstances), 0);
                        g_shared = s; // kept mapped for the life of the process
                    }
                }
            }
        }
    }
    VbShared* result = g_shared;
    ReleaseSRWLockExclusive(&g_sharedLock);
    return result;
}

static float ReadFloat(volatile float* p)
{
    LONG v = InterlockedCompareExchange(reinterpret_cast<volatile LONG*>(p), 0, 0);
    float f;
    memcpy(&f, &v, sizeof f);
    return f;
}

static void WriteFloat(volatile float* p, float f)
{
    LONG v;
    memcpy(&v, &f, sizeof v);
    InterlockedExchange(reinterpret_cast<volatile LONG*>(p), v);
}

static float GainFromSlider(float scalar, bool muted)
{
    if (muted) return 0.0f;
    if (!(scalar >= 0.0f)) return 1.0f;   // NaN guard
    return scalar > 1.0f ? 1.0f : scalar; // dB = 20*log10(scalar): the slider value as linear amplitude
}

// ---------------------------------------------------------------------------------------------
// The APO
// ---------------------------------------------------------------------------------------------
class VolumeBridgeApo final :
    public CBaseAudioProcessingObject,
    public IAudioSystemEffects3,
    public IAudioProcessingObjectNotifications
{
public:
    static const CRegAPOProperties<1> sm_RegProperties;

    VolumeBridgeApo() : CBaseAudioProcessingObject(sm_RegProperties) { g_objects++; }

    // IUnknown
    STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override
    {
        if (ppv == nullptr) return E_POINTER;
        *ppv = nullptr;
        if (riid == __uuidof(IUnknown) || riid == __uuidof(IAudioProcessingObject))
            *ppv = static_cast<IAudioProcessingObject*>(this);
        else if (riid == __uuidof(IAudioProcessingObjectRT))
            *ppv = static_cast<IAudioProcessingObjectRT*>(this);
        else if (riid == __uuidof(IAudioProcessingObjectConfiguration))
            *ppv = static_cast<IAudioProcessingObjectConfiguration*>(this);
        else if (riid == __uuidof(IAudioSystemEffects))
            *ppv = static_cast<IAudioSystemEffects*>(static_cast<IAudioSystemEffects2*>(static_cast<IAudioSystemEffects3*>(this)));
        else if (riid == __uuidof(IAudioSystemEffects2))
            *ppv = static_cast<IAudioSystemEffects2*>(static_cast<IAudioSystemEffects3*>(this));
        else if (riid == __uuidof(IAudioSystemEffects3))
            *ppv = static_cast<IAudioSystemEffects3*>(this);
        else if (riid == __uuidof(IAudioProcessingObjectNotifications))
            *ppv = static_cast<IAudioProcessingObjectNotifications*>(this);
        else
            return E_NOINTERFACE;
        AddRef();
        return S_OK;
    }
    STDMETHODIMP_(ULONG) AddRef() override { return InterlockedIncrement(&m_refs); }
    STDMETHODIMP_(ULONG) Release() override
    {
        ULONG r = InterlockedDecrement(&m_refs);
        if (r == 0) delete this;
        return r;
    }

    // IAudioProcessingObject
    STDMETHODIMP GetLatency(HNSTIME* pTime) override
    {
        if (pTime == nullptr) return E_POINTER;
        *pTime = 0;
        return S_OK;
    }

    STDMETHODIMP Initialize(UINT32 cbDataSize, BYTE* pbyData) override
    {
        if ((pbyData == nullptr) != (cbDataSize == 0)) return E_INVALIDARG;
        m_shared = OpenShared();

        IMMDeviceCollection* devices = nullptr;
        UINT32 kind = 0;
        if (cbDataSize >= sizeof(APOInitSystemEffects3))
        {
            devices = reinterpret_cast<APOInitSystemEffects3*>(pbyData)->pDeviceCollection;
            kind = 3;
        }
        else if (cbDataSize >= sizeof(APOInitSystemEffects2))
        {
            devices = reinterpret_cast<APOInitSystemEffects2*>(pbyData)->pDeviceCollection;
            kind = 2;
        }
        else if (cbDataSize >= sizeof(APOInitSystemEffects))
        {
            kind = 1;
        }

        if (devices != nullptr)
        {
            UINT count = 0;
            if (SUCCEEDED(devices->GetCount(&count)) && count > 0)
                devices->Item(count - 1, &m_endpoint); // the last device is the audio endpoint
        }
        if (m_shared != nullptr) InterlockedExchange(reinterpret_cast<volatile LONG*>(&m_shared->apoInitKind), static_cast<LONG>(kind));

        m_bIsInitialized = true;
        return S_OK;
    }

    STDMETHODIMP LockForProcess(UINT32 nIn, APO_CONNECTION_DESCRIPTOR** ppIn,
                                UINT32 nOut, APO_CONNECTION_DESCRIPTOR** ppOut) override
    {
        HRESULT hr = CBaseAudioProcessingObject::LockForProcess(nIn, ppIn, nOut, ppOut);
        if (FAILED(hr)) return hr;

        UNCOMPRESSEDAUDIOFORMAT fmt{};
        float rate = 48000.0f;
        if (nIn > 0 && ppIn[0]->pFormat != nullptr && SUCCEEDED(ppIn[0]->pFormat->GetUncompressedAudioFormat(&fmt)))
            rate = fmt.fFramesPerSecond > 0 ? fmt.fFramesPerSecond : rate;
        m_rampFrames = static_cast<UINT32>(rate * 0.020f); // 20 ms
        if (m_rampFrames == 0) m_rampFrames = 1;

        m_gain = TargetGain(); // start at the right level, no fade-in
        if (m_shared != nullptr)
        {
            InterlockedIncrement(reinterpret_cast<volatile LONG*>(&m_shared->apoInstances));
            InterlockedExchange(reinterpret_cast<volatile LONG*>(&m_shared->apoSampleRate), static_cast<LONG>(rate));
            InterlockedExchange(reinterpret_cast<volatile LONG*>(&m_shared->apoChannels), static_cast<LONG>(m_u32SamplesPerFrame));
        }
        m_counted = true;
        return S_OK;
    }

    STDMETHODIMP UnlockForProcess() override
    {
        if (m_counted && m_shared != nullptr)
            InterlockedDecrement(reinterpret_cast<volatile LONG*>(&m_shared->apoInstances));
        m_counted = false;
        return CBaseAudioProcessingObject::UnlockForProcess();
    }

    // IAudioProcessingObjectRT — real-time: no allocation, locks, or I/O.
    STDMETHODIMP_(void) APOProcess(UINT32 nIn, APO_CONNECTION_PROPERTY** ppIn,
                                   UINT32 nOut, APO_CONNECTION_PROPERTY** ppOut) override
    {
        if (nIn == 0 || nOut == 0) return;
        APO_CONNECTION_PROPERTY* in = ppIn[0];
        APO_CONNECTION_PROPERTY* out = ppOut[0];
        const UINT32 frames = in->u32ValidFrameCount;
        const UINT32 ch = m_u32SamplesPerFrame;
        auto* src = reinterpret_cast<float*>(in->pBuffer);
        auto* dst = reinterpret_cast<float*>(out->pBuffer);

        const float target = TargetGain();
        if (in->u32BufferFlags == BUFFER_SILENT)
        {
            ZeroMemory(dst, static_cast<SIZE_T>(frames) * ch * sizeof(float)); // silent buffers may hold stale data
            m_gain = target; // nothing audible to ramp
        }
        else
        {
            float g = m_gain;
            const float step = (target - g) / static_cast<float>(m_rampFrames);
            for (UINT32 f = 0; f < frames; ++f)
            {
                if (g != target)
                {
                    g += step;
                    if ((step > 0 && g > target) || (step < 0 && g < target)) g = target;
                }
                const UINT32 base = f * ch;
                for (UINT32 c = 0; c < ch; ++c) dst[base + c] = src[base + c] * g;
            }
            m_gain = g;
        }

        out->u32ValidFrameCount = frames;
        out->u32BufferFlags = in->u32BufferFlags;

        if (m_shared != nullptr)
        {
            WriteFloat(&m_shared->apoTargetGain, target);
            InterlockedExchange64(reinterpret_cast<volatile LONG64*>(&m_shared->apoHeartbeat), static_cast<LONG64>(GetTickCount64()));
        }
    }

    // IAudioSystemEffects2 / 3: no user-controllable effects are exposed.
    STDMETHODIMP GetEffectsList(LPGUID* ppEffectsIds, UINT* pcEffects, HANDLE) override
    {
        if (ppEffectsIds == nullptr || pcEffects == nullptr) return E_POINTER;
        *ppEffectsIds = nullptr;
        *pcEffects = 0;
        return S_OK;
    }
    STDMETHODIMP GetControllableSystemEffectsList(AUDIO_SYSTEMEFFECT** effects, UINT* numEffects, HANDLE) override
    {
        if (effects == nullptr || numEffects == nullptr) return E_POINTER;
        *effects = nullptr;
        *numEffects = 0;
        return S_OK;
    }
    STDMETHODIMP SetAudioSystemEffectState(GUID, AUDIO_SYSTEMEFFECT_STATE) override { return E_NOTIMPL; }

    // IAudioProcessingObjectNotifications (Windows 11): subscribe to this endpoint's volume.
    STDMETHODIMP GetApoNotificationRegistrationInfo(APO_NOTIFICATION_DESCRIPTOR** apoNotifications, DWORD* count) override
    {
        if (apoNotifications == nullptr || count == nullptr) return E_POINTER;
        *apoNotifications = nullptr;
        *count = 0;
        if (m_endpoint == nullptr) return S_OK;
        auto* d = static_cast<APO_NOTIFICATION_DESCRIPTOR*>(CoTaskMemAlloc(sizeof(APO_NOTIFICATION_DESCRIPTOR)));
        if (d == nullptr) return E_OUTOFMEMORY;
        ZeroMemory(d, sizeof(*d));
        d->type = APO_NOTIFICATION_TYPE_ENDPOINT_VOLUME;
        m_endpoint->AddRef();
        d->audioEndpointVolume.device = m_endpoint;
        *apoNotifications = d;
        *count = 1;
        return S_OK;
    }

    STDMETHODIMP_(void) HandleNotification(APO_NOTIFICATION* n) override
    {
        if (n == nullptr || n->type != APO_NOTIFICATION_TYPE_ENDPOINT_VOLUME || n->audioEndpointVolumeChange.volume == nullptr)
            return;
        const float master = n->audioEndpointVolumeChange.volume->fMasterVolume;
        const bool muted = n->audioEndpointVolumeChange.volume->bMuted != FALSE;
        WriteFloat(&m_notifGain, GainFromSlider(master, muted));
        m_haveNotif.store(true, std::memory_order_release);
        if (m_shared != nullptr)
        {
            WriteFloat(&m_shared->apoMasterVolume, master);
            InterlockedExchange(reinterpret_cast<volatile LONG*>(&m_shared->apoMuted), muted ? 1 : 0);
            InterlockedIncrement(reinterpret_cast<volatile LONG*>(&m_shared->apoNotifications));
        }
    }

private:
    ~VolumeBridgeApo()
    {
        if (m_counted && m_shared != nullptr)
            InterlockedDecrement(reinterpret_cast<volatile LONG*>(&m_shared->apoInstances));
        if (m_endpoint != nullptr) m_endpoint->Release();
        g_objects--;
    }

    float TargetGain()
    {
        UINT32 source = VbSourceNone;
        float gain = 1.0f;
        if (m_haveNotif.load(std::memory_order_acquire))
        {
            gain = ReadFloat(&m_notifGain);
            source = VbSourceNotification;
        }
        else if (m_shared != nullptr && m_shared->magic == VB_SHARED_MAGIC && m_shared->appSeq != 0)
        {
            gain = ReadFloat(&m_shared->appGain);
            source = VbSourceApp;
        }
        if (!(gain >= 0.0f)) gain = 1.0f;
        if (gain > 1.0f) gain = 1.0f;
        if (m_shared != nullptr && m_shared->apoSource != source)
            InterlockedExchange(reinterpret_cast<volatile LONG*>(&m_shared->apoSource), static_cast<LONG>(source));
        return gain;
    }

    volatile LONG m_refs = 1;
    IMMDevice* m_endpoint = nullptr;
    VbShared* m_shared = nullptr;
    volatile float m_notifGain = 1.0f;
    std::atomic<bool> m_haveNotif{ false };
    float m_gain = 1.0f;
    UINT32 m_rampFrames = 960;
    bool m_counted = false;
};

const CRegAPOProperties<1> VolumeBridgeApo::sm_RegProperties(
    CLSID_VolumeBridgeApo,
    L"VolumeBridge volume",
    L"Copyright (c) 2026 ZanderH-code (MIT)",
    1, 0,
    __uuidof(IAudioProcessingObject));

// ---------------------------------------------------------------------------------------------
// COM plumbing
// ---------------------------------------------------------------------------------------------
class ClassFactory final : public IClassFactory
{
public:
    STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override
    {
        if (ppv == nullptr) return E_POINTER;
        if (riid == __uuidof(IUnknown) || riid == __uuidof(IClassFactory))
        {
            *ppv = static_cast<IClassFactory*>(this);
            return S_OK;
        }
        *ppv = nullptr;
        return E_NOINTERFACE;
    }
    STDMETHODIMP_(ULONG) AddRef() override { return 2; }
    STDMETHODIMP_(ULONG) Release() override { return 1; }
    STDMETHODIMP CreateInstance(IUnknown* outer, REFIID riid, void** ppv) override
    {
        if (ppv == nullptr) return E_POINTER;
        *ppv = nullptr;
        if (outer != nullptr) return CLASS_E_NOAGGREGATION;
        auto* apo = new (std::nothrow) VolumeBridgeApo();
        if (apo == nullptr) return E_OUTOFMEMORY;
        HRESULT hr = apo->QueryInterface(riid, ppv);
        apo->Release();
        return hr;
    }
    STDMETHODIMP LockServer(BOOL lock) override
    {
        if (lock) g_locks++; else g_locks--;
        return S_OK;
    }
};
static ClassFactory g_factory;

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        g_module = instance;
        DisableThreadLibraryCalls(instance);
    }
    return TRUE;
}

STDAPI DllGetClassObject(REFCLSID clsid, REFIID riid, LPVOID* ppv)
{
    if (clsid != CLSID_VolumeBridgeApo) return CLASS_E_CLASSNOTAVAILABLE;
    return g_factory.QueryInterface(riid, ppv);
}

STDAPI DllCanUnloadNow()
{
    return (g_objects == 0 && g_locks == 0) ? S_OK : S_FALSE;
}

static HRESULT SetString(HKEY root, const wchar_t* subKey, const wchar_t* name, const wchar_t* value)
{
    HKEY key;
    LSTATUS s = RegCreateKeyExW(root, subKey, 0, nullptr, 0, KEY_SET_VALUE, nullptr, &key, nullptr);
    if (s != ERROR_SUCCESS) return HRESULT_FROM_WIN32(s);
    s = RegSetValueExW(key, name, 0, REG_SZ, reinterpret_cast<const BYTE*>(value),
                       static_cast<DWORD>((wcslen(value) + 1) * sizeof(wchar_t)));
    RegCloseKey(key);
    return HRESULT_FROM_WIN32(s);
}

STDAPI DllRegisterServer()
{
    wchar_t path[MAX_PATH];
    if (GetModuleFileNameW(g_module, path, MAX_PATH) == 0) return HRESULT_FROM_WIN32(GetLastError());
    wchar_t key[128];
    StringCchPrintfW(key, 128, L"SOFTWARE\\Classes\\CLSID\\%s", kClsidString);
    HRESULT hr = SetString(HKEY_LOCAL_MACHINE, key, nullptr, L"VolumeBridge volume APO");
    if (FAILED(hr)) return hr;
    StringCchCatW(key, 128, L"\\InprocServer32");
    hr = SetString(HKEY_LOCAL_MACHINE, key, nullptr, path);
    if (FAILED(hr)) return hr;
    hr = SetString(HKEY_LOCAL_MACHINE, key, L"ThreadingModel", L"Both");
    if (FAILED(hr)) return hr;
    return RegisterAPO(&VolumeBridgeApo::sm_RegProperties.m_Properties);
}

STDAPI DllUnregisterServer()
{
    UnregisterAPO(CLSID_VolumeBridgeApo);
    wchar_t key[128];
    StringCchPrintfW(key, 128, L"SOFTWARE\\Classes\\CLSID\\%s", kClsidString);
    LSTATUS s = RegDeleteTreeW(HKEY_LOCAL_MACHINE, key);
    return (s == ERROR_SUCCESS || s == ERROR_FILE_NOT_FOUND) ? S_OK : HRESULT_FROM_WIN32(s);
}
