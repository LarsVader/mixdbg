// CoreClrTypes.h — CoreCLR profiling type aliases, constants, and GUIDs.
//
// These definitions come from corprof.h / corprof.idl which we don't ship.
// They are stable across .NET versions.

#pragma once

#include <windows.h>
#include <unknwn.h>
#include <objbase.h>
#include <stdio.h>
#include <stdlib.h>
#include <malloc.h>
#include <string.h>

// LPCBYTE is from the profiling API headers which we don't include.
typedef const BYTE* LPCBYTE;

// ============================================================================
// CoreCLR profiling type aliases (from corprof.h)
// ============================================================================
typedef UINT_PTR FunctionID;
typedef UINT_PTR ClassID;
typedef UINT_PTR ModuleID;
typedef UINT_PTR AssemblyID;
typedef UINT_PTR ThreadID;
typedef UINT_PTR ObjectID;
typedef UINT_PTR GCHandleID;
typedef UINT_PTR AppDomainID;
typedef UINT32   mdToken;

// COR_PRF_MONITOR flags
#define COR_PRF_MONITOR_JIT_COMPILATION 0x00000020
#define COR_PRF_MONITOR_ENTERLEAVE      0x00001000

// ============================================================================
// GUIDs
// ============================================================================

// MixDbgProfiler CLSID: {D13D53A1-6E42-4D6B-B4C5-8F3A7E2C1B90}
static const GUID CLSID_MixDbgProfiler =
    { 0xD13D53A1, 0x6E42, 0x4D6B, { 0xB4, 0xC5, 0x8F, 0x3A, 0x7E, 0x2C, 0x1B, 0x90 } };

// ICorProfilerCallback: {176FBED1-A55C-4796-98CA-A9DA0EF883E7}
static const GUID IID_ICorProfilerCallback =
    { 0x176FBED1, 0xA55C, 0x4796, { 0x98, 0xCA, 0xA9, 0xDA, 0x0E, 0xF8, 0x83, 0xE7 } };

// ICorProfilerCallback2: {8A8CC829-CCF2-49fe-BBAE-0F022228071A}
static const GUID IID_ICorProfilerCallback2 =
    { 0x8A8CC829, 0xCCF2, 0x49FE, { 0xBB, 0xAE, 0x0F, 0x02, 0x22, 0x28, 0x07, 0x1A } };

// ICorProfilerInfo: {28B5557D-3F3F-48b4-90B2-5F9EEA2F6C48}
static const GUID IID_ICorProfilerInfo =
    { 0x28B5557D, 0x3F3F, 0x48B4, { 0x90, 0xB2, 0x5F, 0x9E, 0xEA, 0x2F, 0x6C, 0x48 } };

// COR_DEBUG_IL_TO_NATIVE_MAP structure (3 x ULONG32)
struct ILNativeMap { ULONG32 ilOffset; ULONG32 nativeStartOffset; ULONG32 nativeEndOffset; };
