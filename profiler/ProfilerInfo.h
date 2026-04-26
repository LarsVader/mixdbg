// ProfilerInfo.h — ICorProfilerInfo vtable wrapper.
//
// Instead of defining the full 33-method COM interface (which requires the
// corprof.h header we don't ship), we call methods by vtable slot index.
// Slot numbers are counted from the start of the vtable (0 = QI, 1 = AddRef,
// 2 = Release, then ICorProfilerInfo methods starting at slot 3).

#pragma once

#include "CoreClrTypes.h"

class ProfilerInfo {
    IUnknown* m_pInfo;
    void** m_vtbl;

public:
    explicit ProfilerInfo(IUnknown* pInfo) : m_pInfo(pInfo) {
        m_vtbl = *reinterpret_cast<void***>(pInfo);
    }

    // Slot 5: GetCodeInfo(FunctionID, LPCBYTE*, ULONG*)
    // Returns the start address and size of JIT'd native code.
    HRESULT GetCodeInfo(FunctionID funcId, LPCBYTE* pStart, ULONG* pcSize) {
        typedef HRESULT(STDMETHODCALLTYPE* Fn)(void*, FunctionID, LPCBYTE*, ULONG*);
        return reinterpret_cast<Fn>(m_vtbl[5])(m_pInfo, funcId, pStart, pcSize);
    }

    // Slot 15: GetFunctionInfo(FunctionID, ClassID*, ModuleID*, mdToken*)
    // Returns the metadata token and containing module for a function.
    HRESULT GetFunctionInfo(FunctionID funcId, ClassID* pClassId,
                            ModuleID* pModuleId, mdToken* pToken) {
        typedef HRESULT(STDMETHODCALLTYPE* Fn)(void*, FunctionID, ClassID*, ModuleID*, mdToken*);
        return reinterpret_cast<Fn>(m_vtbl[15])(m_pInfo, funcId, pClassId, pModuleId, pToken);
    }

    // Slot 16: SetEventMask(DWORD)
    // Configures which profiler events the CLR delivers.
    HRESULT SetEventMask(DWORD dwEvents) {
        typedef HRESULT(STDMETHODCALLTYPE* Fn)(void*, DWORD);
        return reinterpret_cast<Fn>(m_vtbl[16])(m_pInfo, dwEvents);
    }

    // Slot 17: SetEnterLeaveFunctionHooks(FunctionEnter*, FunctionLeave*, FunctionTailcall*)
    HRESULT SetEnterLeaveFunctionHooks(void* pEnter, void* pLeave, void* pTailcall) {
        typedef HRESULT(STDMETHODCALLTYPE* Fn)(void*, void*, void*, void*);
        return reinterpret_cast<Fn>(m_vtbl[17])(m_pInfo, pEnter, pLeave, pTailcall);
    }

    // Slot 18: SetFunctionIDMapper(FunctionIDMapper*)
    HRESULT SetFunctionIDMapper(void* pFunc) {
        typedef HRESULT(STDMETHODCALLTYPE* Fn)(void*, void*);
        return reinterpret_cast<Fn>(m_vtbl[18])(m_pInfo, pFunc);
    }

    // Slot 20: GetModuleInfo(ModuleID, LPCBYTE*, ULONG, ULONG*, WCHAR*, AssemblyID*)
    HRESULT GetModuleInfo(ModuleID moduleId, LPCBYTE* ppBaseAddr, ULONG cchName,
                          ULONG* pcchName, WCHAR* szName, AssemblyID* pAssemblyId) {
        typedef HRESULT(STDMETHODCALLTYPE* Fn)(void*, ModuleID, LPCBYTE*, ULONG, ULONG*, WCHAR*, AssemblyID*);
        return reinterpret_cast<Fn>(m_vtbl[20])(m_pInfo, moduleId, ppBaseAddr, cchName, pcchName, szName, pAssemblyId);
    }

    // Slot 35: GetILToNativeMapping(FunctionID, ULONG32, ULONG32*, ILNativeMap[])
    HRESULT GetILToNativeMapping(FunctionID funcId, ULONG32 cMap, ULONG32* pcMap, void* map) {
        typedef HRESULT(STDMETHODCALLTYPE* Fn)(void*, FunctionID, ULONG32, ULONG32*, void*);
        return reinterpret_cast<Fn>(m_vtbl[35])(m_pInfo, funcId, cMap, pcMap, map);
    }

    // Returns the native offset for the first IL instruction (method body start).
    ULONG32 GetMethodBodyOffset(FunctionID funcId) {
        // Two-pass: query count, then allocate appropriately.
        ULONG32 count = 0;
        GetILToNativeMapping(funcId, 0, &count, nullptr);
        if (count == 0) return 0;
        ILNativeMap mapsStack[128];
        ILNativeMap* maps = (count <= 128)
            ? mapsStack
            : (ILNativeMap*)malloc(count * sizeof(ILNativeMap));
        if (!maps) return 0;
        if (FAILED(GetILToNativeMapping(funcId, count, &count, maps)) || count == 0) {
            if (maps != mapsStack) free(maps);
            return 0;
        }
        ULONG32 result = 0;
        for (ULONG32 i = 0; i < count; i++) {
            if (maps[i].ilOffset == 0) { result = maps[i].nativeStartOffset; goto done; }
        }
        for (ULONG32 i = 0; i < count; i++) {
            if ((int)maps[i].ilOffset >= 0) { result = maps[i].nativeStartOffset; goto done; }
        }
    done:
        if (maps != mapsStack) free(maps);
        return result;
    }
};
