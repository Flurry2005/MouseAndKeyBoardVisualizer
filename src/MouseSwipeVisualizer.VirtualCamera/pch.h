// Precompiled header for the virtual camera media source.
#pragma once

#define NOMINMAX
#include <windows.h>
#include <unknwn.h>
#include <ole2.h>

// GUIDs from the headers below (KSCAMERAPROFILE_*, MF attributes, ...) are defined via
// DEFINE_GUID + DECLSPEC_SELECTANY, so including initguid.h in every translation unit is safe.
#include <initguid.h>

#include <propvarutil.h>
#include <ks.h>
#include <ksproxy.h>
#include <ksmedia.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mfobjects.h>
#include <mferror.h>
#include <mfreadwrite.h>
#include <mfvirtualcamera.h>
#include <cfgmgr32.h>
#include <devpkey.h>
#include <sddl.h>
#include <strsafe.h>

#include <wrl/client.h>
#include <wrl/implements.h>

#include <atomic>
#include <cstdint>
#include <cstring>
#include <deque>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

#include "Common.h"
