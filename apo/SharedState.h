// Shared state between VolumeBridgeApo.dll (inside audiodg.exe) and VolumeBridge.exe (tray app).
// Backed by %ProgramData%\VolumeBridge\apo.dat (file mapping), so it works across the service session.
// Layout must match src/VolumeBridge/Audio/ApoShared.cs.
#pragma once
#include <stdint.h>

#define VB_SHARED_MAGIC   0x31474256u   // "VBG1"
#define VB_SHARED_SIZE    4096u

enum VbVolumeSource : uint32_t
{
    VbSourceNone = 0,          // nothing known yet: unity gain (speaker's own fixed level)
    VbSourceNotification = 1,  // Windows 11 APO endpoint-volume notifications
    VbSourceApp = 2,           // gain written by the tray app
};

#pragma pack(push, 4)
struct VbShared
{
    uint32_t magic;
    uint32_t version;          // 1

    // Written by the app.
    float    appGain;          // linear gain 0..1 derived from the slider/mute
    uint32_t appSeq;           // incremented on every write; 0 = never written

    // Written by the APO.
    uint32_t apoInstances;     // APO objects currently locked for processing
    uint32_t apoSource;        // VbVolumeSource in use
    float    apoTargetGain;    // gain the APO is ramping to
    uint32_t apoInitKind;      // 1/2/3 = APOInitSystemEffects / 2 / 3 seen in Initialize
    uint64_t apoHeartbeat;     // GetTickCount64() of the last processed buffer
    uint32_t apoSampleRate;
    uint32_t apoChannels;
    float    apoMasterVolume;  // last scalar from notifications (-1 = none)
    uint32_t apoMuted;
    uint32_t apoNotifications; // count of endpoint-volume notifications received
    int32_t  apoLastError;     // last HRESULT worth reporting (0 = none)
    uint32_t apoPid;           // audiodg process that owns apoInstances
};
#pragma pack(pop)
